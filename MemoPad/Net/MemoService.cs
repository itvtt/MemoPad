using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MemoPad.Core;

namespace MemoPad.Net;

public sealed record OutgoingFile(string Name, string Path);
public sealed record OutgoingMemo(MemoHeader Header, string Rtf, IReadOnlyList<OutgoingFile> Files);
public sealed record ReceivedFile(string Name, byte[] Data);

public sealed class IncomingMemo
{
    public required MemoHeader Header { get; init; }
    public required string Rtf { get; init; }
    public required string RemoteIp { get; init; }
    public List<ReceivedFile> Files { get; } = [];
}

/// <summary>회람을 받았다 · 넘겼다는 알림 (회람을 시작한 사람이 받음)</summary>
public sealed record CirculationNote(Guid MemoId, string Ip, bool Passed);

public enum DeliveryOutcome { Delivered, Absent, Offline, Timeout, Failed }

public sealed record DeliveryResult(Person Target, DeliveryOutcome Outcome, string Message)
{
    public bool Ok => Outcome == DeliveryOutcome.Delivered;
}

public enum PeerState { Present, Absent, Off, NoAnswer }

public sealed record PeerStatus(PeerState State, int Milliseconds)
{
    public string Text => State switch
    {
        PeerState.Present => "있음",
        PeerState.Absent => "부재중",
        PeerState.Off => "꺼져 있음",
        _ => "응답 없음"
    };
}

/// <summary>
/// 메모 포트(9710): 받기 (메모 · 회람 · 상태 확인 · 부재중 응답 · 첨부)와 보내기.
/// 정음 메모패드와 같은 순서로 주고받음 (JuProtocol)
/// </summary>
public sealed class MemoService : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<string?> _absentText;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    /// <param name="absentText">부재중이면 상대에게 보낼 문구, 아니면 null</param>
    public MemoService(Func<string?> absentText, IPAddress? bind = null)
    {
        _absentText = absentText;
        Bind = bind ?? BindAddress;
    }

    /// <summary>이 서비스가 받는 주소</summary>
    public IPAddress Bind { get; }

    /// <summary>받을 주소 (시험할 때 MEMOPAD_BIND=127.0.0.1 처럼 한 주소만)</summary>
    public static IPAddress BindAddress =>
        IPAddress.TryParse(Environment.GetEnvironmentVariable("MEMOPAD_BIND"), out var address) ? address : IPAddress.Any;

    public static int ListenPort => int.TryParse(Environment.GetEnvironmentVariable("MEMOPAD_MEMO_PORT"), out int port) ? port : JuProtocol.MemoPort;
    /// <summary>상대 PC 의 메모 포트 (보통 9710)</summary>
    public static int RemotePort => int.TryParse(Environment.GetEnvironmentVariable("MEMOPAD_REMOTE_PORT"), out int port) ? port : JuProtocol.MemoPort;

    public bool Listening => _listener is not null;
    public string StartError { get; private set; } = "";

    /// <summary>받은 메모 (스레드 풀에서 불림)</summary>
    public event Action<IncomingMemo>? MemoReceived;
    /// <summary>회람 받음 · 전달 알림 (스레드 풀에서 불림)</summary>
    public event Action<CirculationNote>? NoteReceived;
    /// <summary>받다가 생긴 문제 (기록용)</summary>
    public event Action<string>? Problem;

    public bool Start()
    {
        try
        {
            var listener = new TcpListener(Bind, ListenPort);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            _listener = listener;
            StartError = "";
            _ = AcceptLoop(listener);
            return true;
        }
        catch (SocketException error)
        {
            StartError = error.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"포트 {ListenPort} 을(를) 다른 프로그램이 쓰고 있습니다 (정음 메모패드가 켜져 있으면 끄세요)."
                : error.Message;
            return false;
        }
    }

    public void Stop()
    {
        try { _listener?.Stop(); }
        catch (SocketException) { }
        _listener = null;
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException or SocketException or InvalidOperationException) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        var token = limit.Token;
        try
        {
            var stream = client.GetStream();
            int command = await JuProtocol.ReadIntAsync(stream, token);
            if (command == JuProtocol.CmdStatus)
            {
                await JuProtocol.WriteIntAsync(stream, _absentText() is null ? JuProtocol.StatusPresent : JuProtocol.StatusAbsent, token);
                return;
            }
            if (command is JuProtocol.NoteReceived or JuProtocol.NotePassed)
            {
                int idLength = await JuProtocol.ReadIntAsync(stream, token);
                var id = new Guid(await JuProtocol.ReadExactAsync(stream, idLength == 16 ? 16 : throw new InvalidDataException("회람 알림 형식이 다릅니다"), token));
                string ip = remote;
                try
                {
                    int ipLength = await JuProtocol.ReadIntAsync(stream, token);
                    if (ipLength is > 0 and < 256) ip = JuProtocol.ReadAnsi(await JuProtocol.ReadExactAsync(stream, ipLength, token), 0, ipLength);
                }
                catch (EndOfStreamException) { }
                NoteReceived?.Invoke(new CirculationNote(id, ip, command == JuProtocol.NotePassed));
                return;
            }
            if (command != JuProtocol.CmdMemo) { Problem?.Invoke($"{remote}: 모르는 명령 0x{command:X}"); return; }

            if (_absentText() is { } absent)
            {
                await stream.WriteAsync(JuProtocol.Concat(JuProtocol.Int(JuProtocol.AbsentReply), JuProtocol.LengthPrefixedUnicode(absent)), token);
                return;
            }
            await JuProtocol.WriteIntAsync(stream, JuProtocol.AcceptMemo, token);

            if (await JuProtocol.ReadIntAsync(stream, token) != JuProtocol.CmdHeader) { Problem?.Invoke($"{remote}: 머리가 없습니다"); return; }
            var headerBytes = await JuProtocol.ReadExactAsync(stream, JuProtocol.HeaderSize, token);
            int entries = MemoHeader.EntryCount(headerBytes);
            if (entries > 0) headerBytes = JuProtocol.Concat(headerBytes, await JuProtocol.ReadExactAsync(stream, entries * JuProtocol.EntrySize, token));
            var header = MemoHeader.Parse(headerBytes);
            await stream.WriteAsync(JuProtocol.Concat(JuProtocol.Int(JuProtocol.ReceiverInfo), JuProtocol.ReceiverInfoBlock()), token);

            if (await JuProtocol.ReadIntAsync(stream, token) != JuProtocol.CmdBody) { Problem?.Invoke($"{remote}: 본문이 없습니다"); return; }
            int length = await JuProtocol.ReadIntAsync(stream, token);
            int repeated = await JuProtocol.ReadIntAsync(stream, token);
            var body = await JuProtocol.ReadExactAsync(stream, Math.Max(length, repeated), token);
            await JuProtocol.WriteIntAsync(stream, JuProtocol.BodyReceived, token);

            if (await JuProtocol.ReadIntAsync(stream, token) != JuProtocol.CmdEnd) { Problem?.Invoke($"{remote}: 끝 표시가 없습니다"); return; }
            await JuProtocol.ReadIntAsync(stream, token);

            var memo = new IncomingMemo { Header = header, Rtf = DecodeBody(body), RemoteIp = remote };
            if (header.AttachmentCount > 0)
            {
                var (attachListener, port) = OpenAttachListener(Bind);
                using (attachListener)
                {
                    await stream.WriteAsync(JuProtocol.Concat(JuProtocol.Int(JuProtocol.AttachHere), JuProtocol.Int(port)), token);
                    client.Client.Shutdown(SocketShutdown.Send);
                    try { await ReceiveFiles(attachListener, memo, token); }
                    catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or EndOfStreamException or InvalidDataException)
                    {
                        Problem?.Invoke($"{remote}: 첨부를 받지 못했습니다 ({error.Message})");
                    }
                }
            }
            else
            {
                await JuProtocol.WriteIntAsync(stream, JuProtocol.Done, token);
            }
            MemoReceived?.Invoke(memo);
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or EndOfStreamException or InvalidDataException)
        {
            Problem?.Invoke($"{remote}: {error.Message}");
        }
    }

    private static (Socket Listener, int Port) OpenAttachListener(IPAddress bind)
    {
        for (int port = JuProtocol.AttachPort; port < JuProtocol.AttachPort + 40; port++)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            try
            {
                socket.Bind(new IPEndPoint(bind, port));
                socket.Listen(1);
                return (socket, port);
            }
            catch (SocketException) { socket.Dispose(); }
        }
        var any = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        any.Bind(new IPEndPoint(bind, 0));
        any.Listen(1);
        return (any, ((IPEndPoint)any.LocalEndPoint!).Port);
    }

    private static async Task ReceiveFiles(Socket listener, IncomingMemo memo, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        using var socket = await listener.AcceptAsync(limit.Token);
        socket.NoDelay = true;
        using var stream = new NetworkStream(socket, ownsSocket: false);
        // 큰 파일은 오래 걸릴 수 있음: 조각마다 시간을 다시 잼
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(token);
        transfer.CancelAfter(TimeSpan.FromMinutes(30));
        var t = transfer.Token;

        var begin = await JuProtocol.ReadExactAsync(stream, 24, t);
        if (BitConverter.ToInt32(begin, 0) != JuProtocol.AttachBegin) throw new InvalidDataException("첨부 시작 표시가 없습니다");
        await JuProtocol.WriteIntAsync(stream, JuProtocol.AttachReady, t);
        while (true)
        {
            int next = await JuProtocol.ReadIntAsync(stream, t);
            if (next != JuProtocol.AttachNext) throw new InvalidDataException($"첨부 순서가 다릅니다 (0x{next:X})");
            int nameLength = await JuProtocol.ReadIntAsync(stream, t);
            if (nameLength == 0)
            {
                await JuProtocol.ReadIntAsync(stream, t);  // 0x44 끝
                return;
            }
            string name = JuProtocol.Unicode(await JuProtocol.ReadExactAsync(stream, nameLength, t));
            using var data = new MemoryStream();
            while (true)
            {
                int chunk = await JuProtocol.ReadIntAsync(stream, t);
                if (chunk == 0) break;
                data.Write(await JuProtocol.ReadExactAsync(stream, chunk, t));
                await JuProtocol.WriteIntAsync(stream, JuProtocol.ChunkReceived, t);
                int more = await JuProtocol.ReadIntAsync(stream, t);
                if (more != JuProtocol.ChunkMore) throw new InvalidDataException($"첨부 조각 표시가 다릅니다 (0x{more:X})");
            }
            memo.Files.Add(new ReceivedFile(Path.GetFileName(name), data.ToArray()));
            await JuProtocol.WriteIntAsync(stream, JuProtocol.AttachReady, t);
        }
    }

    /// <summary>본문 바이트 → RTF 글자 (끝의 NUL 은 뺌). RTF 가 아니면 일반 글자로 보고 RTF 로 감쌈</summary>
    public static string DecodeBody(byte[] body)
    {
        int length = Array.IndexOf(body, (byte)0);
        if (length < 0) length = body.Length;
        string text = JuProtocol.Ansi.GetString(body, 0, length);
        return text.TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal) ? text : RtfText.FromPlain(text);
    }

    public static byte[] EncodeBody(string rtf) => JuProtocol.Concat(JuProtocol.Ansi.GetBytes(rtf), [0]);

    // ── 보내기 ─────────────────────────────────────────────

    /// <summary>한 사람에게 메모 보내기 (첨부 포함)</summary>
    public async Task<DeliveryResult> SendAsync(Person target, OutgoingMemo memo, CancellationToken token, IProgress<string>? progress = null)
    {
        TcpClient client;
        try { client = await JuProtocol.ConnectAsync(target.Ip, RemotePort, ConnectTimeout, token); }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            return new DeliveryResult(target, DeliveryOutcome.Offline, error is OperationCanceledException && !token.IsCancellationRequested
                ? "연결 시간이 지났습니다 (PC 가 꺼져 있거나 메모패드가 실행되어 있지 않습니다)"
                : "PC 가 꺼져 있거나 메모패드가 실행되어 있지 않습니다");
        }

        using var _ = client;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(StepTimeout + TimeSpan.FromSeconds(memo.Rtf.Length / 50_000));
        try
        {
            var stream = client.GetStream();
            progress?.Report($"{target.Label}에게 연결중...");
            await JuProtocol.WriteIntAsync(stream, JuProtocol.CmdMemo, limit.Token);
            int answer = await JuProtocol.ReadIntAsync(stream, limit.Token);
            if (answer == JuProtocol.AbsentReply)
            {
                int length = await JuProtocol.ReadIntAsync(stream, limit.Token);
                string text = JuProtocol.Unicode(await JuProtocol.ReadExactAsync(stream, length, limit.Token));
                return new DeliveryResult(target, DeliveryOutcome.Absent, text);
            }
            if (answer != JuProtocol.AcceptMemo) return new DeliveryResult(target, DeliveryOutcome.Failed, $"받는 쪽 응답이 다릅니다 (0x{answer:X})");

            progress?.Report($"{target.Label}에게 정보 보내는 중...");
            memo.Header.AttachmentCount = memo.Files.Count;
            await stream.WriteAsync(JuProtocol.Concat(JuProtocol.Int(JuProtocol.CmdHeader), memo.Header.ToBytes()), limit.Token);
            answer = await JuProtocol.ReadIntAsync(stream, limit.Token);
            if (answer != JuProtocol.ReceiverInfo) return new DeliveryResult(target, DeliveryOutcome.Failed, $"받는 쪽 응답이 다릅니다 (0x{answer:X})");
            int infoLength = await JuProtocol.ReadIntAsync(stream, limit.Token);
            await JuProtocol.ReadExactAsync(stream, infoLength + 8, limit.Token);

            progress?.Report($"{target.Label}에게 내용 보내는 중...");
            var body = EncodeBody(memo.Rtf);
            await stream.WriteAsync(JuProtocol.Concat(JuProtocol.Int(JuProtocol.CmdBody), JuProtocol.Int(body.Length), JuProtocol.Int(body.Length), body), limit.Token);
            answer = await JuProtocol.ReadIntAsync(stream, limit.Token);
            if (answer != JuProtocol.BodyReceived) return new DeliveryResult(target, DeliveryOutcome.Failed, $"받는 쪽 응답이 다릅니다 (0x{answer:X})");
            await stream.WriteAsync(JuProtocol.Concat(JuProtocol.Int(JuProtocol.CmdEnd), JuProtocol.Int(0)), limit.Token);
            answer = await JuProtocol.ReadIntAsync(stream, limit.Token);
            if (answer == JuProtocol.AttachHere)
            {
                int port = await JuProtocol.ReadIntAsync(stream, limit.Token);
                if (memo.Files.Count > 0)
                {
                    progress?.Report($"{target.Label}에게 파일을 보내는 중...");
                    await SendFiles(target.Ip, port, memo.Files, token, progress);
                }
            }
            else if (answer != JuProtocol.Done) return new DeliveryResult(target, DeliveryOutcome.Failed, $"받는 쪽 응답이 다릅니다 (0x{answer:X})");
            return new DeliveryResult(target, DeliveryOutcome.Delivered, "");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new DeliveryResult(target, DeliveryOutcome.Timeout, "보내는 중에 시간이 초과되었습니다");
        }
        catch (Exception error) when (error is IOException or SocketException or EndOfStreamException or InvalidDataException)
        {
            return new DeliveryResult(target, DeliveryOutcome.Failed, "보내는 중에 연결이 끊겼습니다");
        }
    }

    private static async Task SendFiles(string ip, int port, IReadOnlyList<OutgoingFile> files, CancellationToken token, IProgress<string>? progress)
    {
        using var client = await JuProtocol.ConnectAsync(ip, port, TimeSpan.FromSeconds(10), token);
        var stream = client.GetStream();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(30));
        var t = limit.Token;
        long total = files.Sum(f => new FileInfo(f.Path).Length);
        await stream.WriteAsync(JuProtocol.Concat(JuProtocol.Int(JuProtocol.AttachBegin), JuProtocol.Int(0x10), JuProtocol.Int(0x10), JuProtocol.Int(0x37),
            JuProtocol.Int((int)(total >> 32)), JuProtocol.Int((int)total)), t);
        await Expect(stream, JuProtocol.AttachReady, t);
        var buffer = new byte[2048];
        for (int index = 0; index < files.Count; index++)
        {
            var file = files[index];
            progress?.Report($"파일을 보내는 중입니다. ({index + 1}/{files.Count})");
            await stream.WriteAsync(JuProtocol.Concat(JuProtocol.Int(JuProtocol.AttachNext), JuProtocol.LengthPrefixedUnicode(file.Name)), t);
            await using (var input = File.OpenRead(file.Path))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, t)) > 0)
                {
                    await stream.WriteAsync(JuProtocol.Concat(JuProtocol.Int(read), buffer[..read]), t);
                    await Expect(stream, JuProtocol.ChunkReceived, t);
                    await JuProtocol.WriteIntAsync(stream, JuProtocol.ChunkMore, t);
                }
            }
            await JuProtocol.WriteIntAsync(stream, 0, t);
            await Expect(stream, JuProtocol.AttachReady, t);
        }
        await stream.WriteAsync(JuProtocol.Concat(JuProtocol.Int(JuProtocol.AttachNext), JuProtocol.Int(0), JuProtocol.Int(JuProtocol.AttachEnd)), t);
    }

    private static async Task Expect(Stream stream, int value, CancellationToken token)
    {
        int answer = await JuProtocol.ReadIntAsync(stream, token);
        if (answer != value) throw new InvalidDataException($"응답이 다릅니다 (0x{answer:X}, 기다린 값 0x{value:X})");
    }

    /// <summary>회람을 시작한 사람에게 받음 · 전달 알림 (못 보내도 그냥 넘어감)</summary>
    public static async Task SendNoteAsync(string originatorIp, bool passed, Guid memo, string myIp)
    {
        try
        {
            using var client = await JuProtocol.ConnectAsync(originatorIp, RemotePort, ConnectTimeout, CancellationToken.None);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.GetStream().WriteAsync(JuProtocol.Note(passed ? JuProtocol.NotePassed : JuProtocol.NoteReceived, memo, myIp), limit.Token);
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException) { }
    }

    /// <summary>사용자 상태 확인 (있음 · 부재중 · 꺼져 있음)</summary>
    public static async Task<PeerStatus> QueryStatusAsync(string ip, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = await JuProtocol.ConnectAsync(ip, RemotePort, ConnectTimeout, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            var stream = client.GetStream();
            await JuProtocol.WriteIntAsync(stream, JuProtocol.CmdStatus, limit.Token);
            int answer = await JuProtocol.ReadIntAsync(stream, limit.Token);
            int ms = (int)watch.ElapsedMilliseconds;
            return new PeerStatus(answer == JuProtocol.StatusAbsent ? PeerState.Absent : PeerState.Present, ms);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return new PeerStatus(PeerState.Off, (int)watch.ElapsedMilliseconds);
        }
        catch (Exception error) when (error is IOException or EndOfStreamException)
        {
            return new PeerStatus(PeerState.NoAnswer, (int)watch.ElapsedMilliseconds);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Stop();
        _stop.Dispose();
    }
}
