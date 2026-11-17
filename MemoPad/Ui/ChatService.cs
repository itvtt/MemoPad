using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using MemoPad.Core;
using MemoPad.Net;

namespace MemoPad.Ui;

public enum ChatLineKind { System, Mine, Other }

public sealed record ChatLine(ChatLineKind Kind, string Who, string Text, DateTime At);

/// <summary>대화 하나 (방장 또는 손님). 일은 백그라운드에서, 알림은 UI 스레드로</summary>
public abstract class ChatSession
{
    protected readonly Desk Desk;
    protected readonly CancellationTokenSource Stop = new();

    protected ChatSession(Desk desk) { Desk = desk; }

    public List<ChatProtocol.Participant> Members { get; } = [];
    public List<ChatLine> Lines { get; } = [];
    public abstract bool IsHost { get; }
    public abstract string Title { get; }
    public bool Ended { get; protected set; }

    public event Action<ChatLine>? LineAdded;
    public event Action? MembersChanged;

    public abstract Task SayAsync(string text);
    public abstract void Leave();

    protected string MyChatName => Desk.Settings.MyChatName;
    protected ChatProtocol.Participant Myself => new(MyChatName, Desk.Me, ChatProtocol.DefaultFace);

    protected void Post(ChatLineKind kind, string who, string text) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        var line = new ChatLine(kind, who, text, DateTime.Now);
        Lines.Add(line);
        LineAdded?.Invoke(line);
    });

    protected void PostMembers(Action change) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        change();
        MembersChanged?.Invoke();
    });

    /// <summary>"대화명: 말" → (대화명, 말)</summary>
    public static (string Who, string Text) Split(string line)
    {
        int at = line.IndexOf(": ", StringComparison.Ordinal);
        return at > 0 && at < 40 ? (line[..at], line[(at + 2)..]) : ("", line);
    }
}

/// <summary>방장: 이 PC 의 9712 로 들어온 손님들. 말을 받으면 모두에게 돌려줌</summary>
public sealed class HostSession : ChatSession
{
    private sealed class Guest
    {
        public required TcpClient Client { get; init; }
        public required NetworkStream Stream { get; init; }
        public ChatProtocol.Participant? Info { get; set; }
        public SemaphoreSlim Write { get; } = new(1, 1);
    }

    private readonly List<Guest> _guests = [];

    public HostSession(Desk desk) : base(desk)
    {
        PostMembers(() => Members.Add(Myself));
    }

    public override bool IsHost => true;
    public override string Title => "대화방 (내 PC)";
    public bool Locked { get; set; }

    public void Accept(TcpClient client) => _ = Task.Run(() => Serve(client));

    private async Task Serve(TcpClient client)
    {
        client.NoDelay = true;
        var guest = new Guest { Client = client, Stream = client.GetStream() };
        var token = Stop.Token;
        try
        {
            using (var hello = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                hello.CancelAfter(TimeSpan.FromSeconds(15));
                var first = await ChatProtocol.ReadAsync(guest.Stream, hello.Token);
                if (first.Command == ChatProtocol.Join) first = await ChatProtocol.ReadAsync(guest.Stream, hello.Token);
                if (first.Command != ChatProtocol.MyInfo) { client.Dispose(); return; }
                var info = ChatProtocol.ParseInfo(first.Payload);
                if (!NetAddress.IsIPv4(info.Person.Ip)) info = info with { Person = new Person { Ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "", Name = info.Person.Name, Email = info.Person.Email } };
                guest.Info = info;
            }
            if (Locked)
            {
                await Send(guest, ChatProtocol.System, ChatProtocol.Text("대상에 접속이 많거나 거부당하여 들어 갈수 없습니다..."));
                client.Dispose();
                Post(ChatLineKind.System, "", $">>> '{guest.Info.ChatName}'님은 잠긴 대화방에서 받아 들여지지 않았습니다.");
                return;
            }
            List<Guest> others;
            lock (_guests) { others = _guests.ToList(); _guests.Add(guest); }
            await Send(guest, ChatProtocol.System, ChatProtocol.Text($">>> {Desk.Me.Label}과 연결이 되었습니다."));
            string entered = $">>> '{guest.Info.Person.Label}'님이 입실 하였습니다.";
            await SendAll(ChatProtocol.System, ChatProtocol.Text(entered));
            await Send(guest, ChatProtocol.Member, ChatProtocol.MemberPayload(Myself));
            foreach (var other in others.Where(o => o.Info is not null)) await Send(guest, ChatProtocol.Member, ChatProtocol.MemberPayload(other.Info!));
            await Send(guest, ChatProtocol.Member, ChatProtocol.MemberPayload(guest.Info));
            foreach (var other in others) await Send(other, ChatProtocol.Member, ChatProtocol.MemberPayload(guest.Info));
            PostMembers(() => Members.Add(guest.Info));
            Post(ChatLineKind.System, "", entered);

            while (!token.IsCancellationRequested)
            {
                var message = await ChatProtocol.ReadAsync(guest.Stream, token);
                if (message.Command == ChatProtocol.Say)
                {
                    int at = 0;
                    string text = ChatProtocol.ReadText(message.Payload, ref at);
                    await SendAll(ChatProtocol.Broadcast, ChatProtocol.BroadcastPayload(guest.Info.Person.Ip, text));
                    var (who, said) = Split(text);
                    Post(ChatLineKind.Other, who.Length > 0 ? who : guest.Info.ChatName, said);
                }
                else if (message.Command == ChatProtocol.Leave) break;
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or EndOfStreamException or InvalidDataException or ObjectDisposedException) { }
        bool removed;
        lock (_guests) removed = _guests.Remove(guest);
        client.Dispose();
        if (removed && guest.Info is { } left && !token.IsCancellationRequested)
        {
            string text = $">>> '{left.Person.Label}'님이 퇴실 하였습니다.";
            await SendAll(ChatProtocol.System, ChatProtocol.Text(text));
            PostMembers(() => Members.RemoveAll(m => m.Person.Ip == left.Person.Ip && m.ChatName == left.ChatName));
            Post(ChatLineKind.System, "", text);
        }
    }

    private static async Task Send(Guest guest, int command, byte[] payload)
    {
        await guest.Write.WaitAsync();
        try { await guest.Stream.WriteAsync(ChatProtocol.Frame(command, payload)); }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
        finally { guest.Write.Release(); }
    }

    private async Task SendAll(int command, byte[] payload)
    {
        List<Guest> guests;
        lock (_guests) guests = _guests.ToList();
        await Task.WhenAll(guests.Select(g => Send(g, command, payload)));
    }

    public int GuestCount { get { lock (_guests) return _guests.Count; } }

    public override async Task SayAsync(string text)
    {
        string line = $"{MyChatName}: {text}";
        Post(ChatLineKind.Mine, MyChatName, text);
        await SendAll(ChatProtocol.Broadcast, ChatProtocol.BroadcastPayload(Desk.MyIp, line));
    }

    public override void Leave()
    {
        Stop.Cancel();
        lock (_guests)
        {
            foreach (var guest in _guests) guest.Client.Dispose();
            _guests.Clear();
        }
        Ended = true;
    }
}

/// <summary>손님: 다른 PC 의 9712 에 들어감</summary>
public sealed class GuestSession : ChatSession
{
    private readonly Person _host;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _write = new(1, 1);

    public GuestSession(Desk desk, Person host) : base(desk) { _host = host; }

    public override bool IsHost => false;
    public override string Title => $"{_host.Label} ({_host.Ip})";

    public async Task<bool> ConnectAsync()
    {
        Post(ChatLineKind.System, "", $">>> {_host.Label}({_host.Ip})에게 연결중...");
        try
        {
            _client = await JuProtocol.ConnectAsync(_host.Ip, ChatService.RemotePort, TimeSpan.FromSeconds(4), Stop.Token);
            _stream = _client.GetStream();
            await _stream.WriteAsync(JuProtocol.Concat(ChatProtocol.Frame(ChatProtocol.Join, []), ChatProtocol.Frame(ChatProtocol.MyInfo, ChatProtocol.InfoPayload(MyChatName, Desk.Me))));
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException)
        {
            Post(ChatLineKind.System, "", "==대상에 접속 할 수 없습니다. 상대편 PC가 꺼져 있거나 네트워크 오류입니다. ==");
            Ended = true;
            return false;
        }
        _ = Task.Run(ReadLoop);
        return true;
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!Stop.IsCancellationRequested)
            {
                var message = await ChatProtocol.ReadAsync(_stream!, Stop.Token);
                int at = 0;
                switch (message.Command)
                {
                    case ChatProtocol.System:
                        Post(ChatLineKind.System, "", ChatProtocol.ReadText(message.Payload, ref at));
                        break;
                    case ChatProtocol.Member:
                        var member = ChatProtocol.ParseMember(message.Payload);
                        PostMembers(() =>
                        {
                            Members.RemoveAll(m => m.Person.Ip == member.Person.Ip && m.ChatName == member.ChatName);
                            Members.Add(member);
                        });
                        break;
                    case ChatProtocol.Broadcast:
                        string ip = ChatProtocol.ReadText(message.Payload, ref at);
                        string text = ChatProtocol.ReadText(message.Payload, ref at);
                        var (who, said) = Split(text);
                        bool mine = ip == Desk.MyIp && who == MyChatName;
                        if (!mine) Post(ChatLineKind.Other, who.Length > 0 ? who : ip, said);
                        break;
                }
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or EndOfStreamException or InvalidDataException or ObjectDisposedException) { }
        if (!Stop.IsCancellationRequested)
            Post(ChatLineKind.System, "", $"연결 대상의 프로그램이 종료되어 더이상 대화가 불가능합니다.");
        Ended = true;
    }

    public override async Task SayAsync(string text)
    {
        if (_stream is null || Ended) { Post(ChatLineKind.System, "", "현재 연결이 안되어 있습니다."); return; }
        Post(ChatLineKind.Mine, MyChatName, text);
        await _write.WaitAsync();
        try { await _stream.WriteAsync(ChatProtocol.Frame(ChatProtocol.Say, ChatProtocol.Text($"{MyChatName}: {text}"))); }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { Post(ChatLineKind.System, "", "보내지 못했습니다 (연결이 끊김)."); }
        finally { _write.Release(); }
    }

    public override void Leave()
    {
        try { _stream?.Write(ChatProtocol.Frame(ChatProtocol.Leave, [])); }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
        Stop.Cancel();
        _client?.Dispose();
        Ended = true;
    }
}

/// <summary>대화 포트 듣기와 대화창 관리</summary>
public sealed class ChatService
{
    private readonly Desk _desk;
    private TcpListener? _listener;
    private HostSession? _host;
    private ChatWindow? _hostWindow;

    public ChatService(Desk desk) { _desk = desk; }

    public static int ListenPort => int.TryParse(Environment.GetEnvironmentVariable("MEMOPAD_CHAT_PORT"), out int port) ? port : JuProtocol.ChatPort;
    public static int RemotePort => int.TryParse(Environment.GetEnvironmentVariable("MEMOPAD_REMOTE_CHAT_PORT"), out int port) ? port : JuProtocol.ChatPort;

    public string StartError { get; private set; } = "";
    public bool Listening => _listener is not null;
    /// <summary>이 PC 의 대화방 (아무도 안 들어왔으면 null)</summary>
    public HostSession? Host => _host;

    public void Start()
    {
        if (_listener is not null) return;
        try
        {
            var listener = new TcpListener(_desk.Net.Bind, ListenPort);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            _listener = listener;
            StartError = "";
            _ = AcceptLoop(listener);
        }
        catch (SocketException error) { StartError = error.Message; }
    }

    public void Stop()
    {
        try { _listener?.Stop(); }
        catch (SocketException) { }
        _listener = null;
        _host?.Leave();
        _host = null;
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception error) when (error is ObjectDisposedException or SocketException or InvalidOperationException) { return; }
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var host = EnsureHost();
                ShowHostWindow(activate: false);
                host.Accept(client);
            });
        }
    }

    private HostSession EnsureHost()
    {
        if (_host is null || _host.Ended) _host = new HostSession(_desk);
        return _host;
    }

    private void ShowHostWindow(bool activate)
    {
        if (_hostWindow is null)
        {
            _hostWindow = new ChatWindow(_desk, EnsureHost());
            _hostWindow.Closed += (_, _) => _hostWindow = null;
        }
        if (!_hostWindow.IsVisible) _hostWindow.Show();
        if (activate) _hostWindow.Activate();
        else _hostWindow.Flash();
    }

    /// <summary>대화창 열기: 사람이 있으면 그 PC 에 들어가고, 없으면 내 대화방</summary>
    public void OpenWindow(Person? with)
    {
        if (with is null) { ShowHostWindow(activate: true); return; }
        var session = new GuestSession(_desk, with);
        var window = new ChatWindow(_desk, session);
        window.Show();
        _ = session.ConnectAsync();
    }
}
