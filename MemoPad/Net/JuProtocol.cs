using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using MemoPad.Core;

namespace MemoPad.Net;

/// <summary>
/// 정음 메모패드(9.x)와 같은 네트워크 형식. 자세한 배치는 docs\protocol.md.
///   메모 포트 9710: 메모 · 회람 · 상태 확인 · 부재중
///   대화 포트 9712: 대화창 (SimpleChat)
///   첨부: 받는 쪽이 알려 준 포트로 따로 연결
/// 숫자는 모두 little-endian 4바이트, 글자는 ANSI(CP949) 뒤에 같은 글자의 UTF-16 을 붙인 칸 또는 UTF-16 만
/// </summary>
public static class JuProtocol
{
    // 정음 메모패드의 포트 (MemoPad 자기 포트는 Ports)
    public const int MemoPort = 9710;
    public const int ChatPort = 9712;
    public const int AttachPort = 9726;
    /// <summary>정음 메모패드가 알리는 버전과 같게 (받는 쪽이 이 값으로 기능을 고름)</summary>
    public const string Version = "9.5.1.133";

    // 메모 포트 명령
    public const int CmdMemo = 0x01, AcceptMemo = 0x02, AbsentReply = 0x03, CmdHeader = 0x04, CmdBody = 0x05, CmdEnd = 0x06;
    public const int ReceiverInfo = 0x09, BodyReceived = 0x20, Done = 0x63, AttachHere = 0x43;
    public const int CmdStatus = 0x32, StatusAbsent = 0x33, StatusPresent = 0x34;
    // 회람 알림 (받은 사람 → 회람을 시작한 사람): [16][메모 id][바이트 수][ANSI IP + NUL], 응답 없음
    public const int NoteReceived = 0x16, NotePassed = 0x17;
    // 첨부 연결
    public const int AttachBegin = 0x50, AttachReady = 0x51, AttachNext = 0x52, ChunkReceived = 0x53, ChunkMore = 0x54, AttachEnd = 0x44;

    public const int HeaderSize = 520;
    public const int EntrySize = 642;

    public static Encoding Ansi { get; } = CreateAnsi();

    private static Encoding CreateAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949);
    }

    // ── 읽기 · 쓰기 ─────────────────────────────────────────────

    public static async Task<int> ReadIntAsync(Stream stream, CancellationToken token)
    {
        var buffer = await ReadExactAsync(stream, 4, token);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    public static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        if (count < 0 || count > 64 * 1024 * 1024) throw new InvalidDataException($"길이가 이상합니다: {count}");
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), token);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        return buffer;
    }

    public static Task WriteIntAsync(Stream stream, int value, CancellationToken token) => stream.WriteAsync(Int(value), token).AsTask();

    public static byte[] Int(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var part in parts) { part.CopyTo(all, at); at += part.Length; }
        return all;
    }

    /// <summary>UTF-16 글자 + NUL, 앞에 바이트 수 (부재중 문구 · 첨부 이름 · 대화)</summary>
    public static byte[] LengthPrefixedUnicode(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        return Concat(Int(bytes.Length), bytes);
    }

    public static string Unicode(byte[] bytes) => Encoding.Unicode.GetString(bytes).TrimEnd('\0');

    /// <summary>받는 쪽이 보내는 정보 (응답 0x09 뒤): [길이][버전 블록][0][0x1f]</summary>
    public static byte[] ReceiverInfoBlock()
    {
        var block = new byte[35];
        BinaryPrimitives.WriteInt32LittleEndian(block, 35);
        Encoding.Unicode.GetBytes(Version).CopyTo(block, 4);
        return Concat(Int(35), block, Int(0), Int(0x1f));
    }

    // ── 칸 글자: ANSI + NUL + UTF-16 + NUL ────────────────────

    public static void WriteDualString(byte[] buffer, int offset, int size, string text)
    {
        while (true)
        {
            var ansi = Ansi.GetBytes(text);
            var wide = Encoding.Unicode.GetBytes(text);
            if (ansi.Length + 1 + wide.Length + 2 <= size || text.Length == 0)
            {
                Array.Clear(buffer, offset, size);
                ansi.AsSpan(0, Math.Min(ansi.Length, size - 1)).CopyTo(buffer.AsSpan(offset));
                if (ansi.Length + 1 + wide.Length + 2 <= size) wide.CopyTo(buffer, offset + ansi.Length + 1);
                return;
            }
            text = text[..^1];
        }
    }

    public static string ReadDualString(byte[] buffer, int offset, int size)
    {
        int end = Array.IndexOf(buffer, (byte)0, offset, size);
        if (end < 0) end = offset + size;
        string ansi = Ansi.GetString(buffer, offset, end - offset);
        // 뒤에 붙은 UTF-16 이 ANSI 와 같은 글자면 그쪽 (ANSI 로 못 쓰는 글자도 살아 있음)
        int wideStart = end + 1, limit = offset + size;
        if (wideStart + 1 < limit)
        {
            int wideEnd = wideStart;
            while (wideEnd + 1 < limit && (buffer[wideEnd] != 0 || buffer[wideEnd + 1] != 0)) wideEnd += 2;
            string wide = Encoding.Unicode.GetString(buffer, wideStart, wideEnd - wideStart);
            if (wide.Length > 0 && wide.Length >= ansi.Length / 2 && Ansi.GetString(Ansi.GetBytes(wide)) == ansi) return wide;
        }
        return ansi;
    }

    public static void WriteWide(byte[] buffer, int offset, int size, string text)
    {
        Array.Clear(buffer, offset, size);
        var wide = Encoding.Unicode.GetBytes(text);
        wide.AsSpan(0, Math.Min(wide.Length, size - 2)).CopyTo(buffer.AsSpan(offset));
    }

    public static string ReadWide(byte[] buffer, int offset, int size)
    {
        int end = offset;
        while (end + 1 < offset + size && (buffer[end] != 0 || buffer[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(buffer, offset, end - offset);
    }

    public static string ReadAnsi(byte[] buffer, int offset, int size)
    {
        int end = Array.IndexOf(buffer, (byte)0, offset, size);
        return Ansi.GetString(buffer, offset, (end < 0 ? offset + size : end) - offset);
    }

    /// <summary>회람 알림 한 통: [명령][16][메모 id][바이트 수][ANSI IP + NUL]</summary>
    public static byte[] Note(int command, Guid memo, string ip)
    {
        var address = Concat(Ansi.GetBytes(ip), [0]);
        return Concat(Int(command), Int(16), memo.ToByteArray(), Int(address.Length), address);
    }

    public static async Task<TcpClient> ConnectAsync(string ip, int port, TimeSpan timeout, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(ip, port, limit.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

/// <summary>메모 머리 (명령 0x04 뒤 520바이트 + 회람 대상 642바이트씩)</summary>
public sealed class MemoHeader
{
    // 메모 머리 안의 자리 (명령 4바이트를 뺀 위치)
    // 첫 칸 (128바이트): IP 글자 + NUL 바로 뒤에 [00][10][00][00][F0][첨부 수 2바이트] (IP 길이에 따라 자리가 움직임)
    private const int IpAt = 0, IpSize = 16, IpRegion = 128;
    private const int IdAt = 128, NameAt = 192, DualSize = 64, VersionAt = 322, VersionSize = 64;
    private const int TypeAt = 448, GuidAt = 452, Field468 = 468, XAt = 472, YAt = 476, WAt = 480, HAt = 484, ColorAt = 488;
    private const int CreatedAt = 492, ModifiedAt = 500, Field508 = 508, Field512 = 512, CountAt = 516;

    /// <summary>0 = 메모, 2 = 회람 (시작한 사람 → 첫 사람), 3 = 회람 (다음 사람에게 넘김 · 마지막이면 시작한 사람에게 결과)</summary>
    public const int TypeMemo = 0, TypeCirculation = 2, TypeCirculationPass = 3;

    public Person Sender { get; set; } = new();
    public int AttachmentCount { get; set; }
    public string Version { get; set; } = JuProtocol.Version;
    public int Type { get; set; }
    public Guid MemoId { get; set; } = Guid.NewGuid();
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 235;
    public int Height { get; set; } = 141;
    public int Color { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
    public int Unknown468 { get; set; }
    public int Unknown508 { get; set; }
    public int Unknown512 { get; set; } = 1;
    public List<CirculationEntry> Entries { get; set; } = [];

    public bool IsCirculation => Type is TypeCirculation or TypeCirculationPass || Entries.Count > 0;

    public byte[] ToBytes()
    {
        var buffer = new byte[JuProtocol.HeaderSize + Entries.Count * JuProtocol.EntrySize];
        var ip = JuProtocol.Ansi.GetBytes(Sender.Ip);
        int end = Math.Min(ip.Length, IpSize - 1);
        ip.AsSpan(0, end).CopyTo(buffer.AsSpan(IpAt));
        // end = NUL 자리. 정음 메모패드는 NUL+2 의 0x10 을 보고 NUL+6 의 첨부 수를 읽음
        buffer[IpAt + end + 2] = 0x10;
        buffer[IpAt + end + 5] = 0xF0;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(IpAt + end + 6), (ushort)AttachmentCount);
        JuProtocol.WriteDualString(buffer, IdAt, DualSize, Sender.Id);
        JuProtocol.WriteDualString(buffer, NameAt, DualSize, Sender.Name);
        JuProtocol.WriteWide(buffer, VersionAt, VersionSize, Version);
        Write(buffer, TypeAt, Type);
        MemoId.ToByteArray().CopyTo(buffer, GuidAt);
        Write(buffer, Field468, Unknown468);
        Write(buffer, XAt, X);
        Write(buffer, YAt, Y);
        Write(buffer, WAt, Width);
        Write(buffer, HAt, Height);
        Write(buffer, ColorAt, Color);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(CreatedAt), ToOle(Created));
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(ModifiedAt), ToOle(Modified));
        Write(buffer, Field508, Unknown508);
        Write(buffer, Field512, Unknown512);
        Write(buffer, CountAt, Entries.Count);
        for (int i = 0; i < Entries.Count; i++) Entries[i].WriteTo(buffer, JuProtocol.HeaderSize + i * JuProtocol.EntrySize);
        return buffer;
    }

    /// <summary>520바이트를 읽고, 회람이면 대상 칸 수를 돌려줌 (이어서 읽을 바이트 = 그 수 × 642)</summary>
    public static MemoHeader Parse(byte[] buffer)
    {
        var header = new MemoHeader
        {
            Sender = new Person
            {
                Ip = JuProtocol.ReadAnsi(buffer, IpAt, IpSize),
                Id = JuProtocol.ReadDualString(buffer, IdAt, DualSize),
                Name = JuProtocol.ReadDualString(buffer, NameAt, DualSize)
            },
            AttachmentCount = ReadAttachmentCount(buffer),
            Version = JuProtocol.ReadWide(buffer, VersionAt, VersionSize),
            Type = Read(buffer, TypeAt),
            MemoId = new Guid(buffer.AsSpan(GuidAt, 16)),
            Unknown468 = Read(buffer, Field468),
            X = Read(buffer, XAt),
            Y = Read(buffer, YAt),
            Width = Read(buffer, WAt),
            Height = Read(buffer, HAt),
            Color = Read(buffer, ColorAt),
            Created = FromOle(BinaryPrimitives.ReadDoubleLittleEndian(buffer.AsSpan(CreatedAt))),
            Modified = FromOle(BinaryPrimitives.ReadDoubleLittleEndian(buffer.AsSpan(ModifiedAt))),
            Unknown508 = Read(buffer, Field508),
            Unknown512 = Read(buffer, Field512)
        };
        int count = Read(buffer, CountAt);
        for (int i = 0; i < count && buffer.Length >= JuProtocol.HeaderSize + (i + 1) * JuProtocol.EntrySize; i++)
            header.Entries.Add(CirculationEntry.Read(buffer, JuProtocol.HeaderSize + i * JuProtocol.EntrySize));
        return header;
    }

    public static int EntryCount(byte[] header) => Math.Clamp(Read(header, CountAt), 0, 500);

    private static int ReadAttachmentCount(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0, IpAt, IpSize);
        if (end < 0 || end + 8 > IpRegion || buffer[end + 2] != 0x10) return 0;
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(end + 6));
    }

    private static void Write(byte[] buffer, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at), value);
    private static int Read(byte[] buffer, int at) => BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(at));

    // 시각은 OLE 날짜 (1899-12-30 부터의 날 수, double)
    private static double ToOle(DateTime time)
    {
        try { return time.ToOADate(); }
        catch (OverflowException) { return DateTime.Now.ToOADate(); }
    }

    public static DateTime FromOle(double value)
    {
        if (double.IsNaN(value) || value < 2 || value > 2958465) return DateTime.Now;
        try { return DateTime.FromOADate(value); }
        catch (ArgumentException) { return DateTime.Now; }
    }
}

/// <summary>회람 대상 한 칸 (642바이트): [상태 2][IP 256][ID 128][이름 128][E-Mail 128], 글자는 UTF-16</summary>
public sealed class CirculationEntry
{
    public int State { get; set; }
    public Person Person { get; set; } = new();

    public void WriteTo(byte[] buffer, int at)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at), (ushort)State);
        JuProtocol.WriteWide(buffer, at + 2, 256, Person.Ip);
        JuProtocol.WriteWide(buffer, at + 258, 128, Person.Id);
        JuProtocol.WriteWide(buffer, at + 386, 128, Person.Name);
        JuProtocol.WriteWide(buffer, at + 514, 128, Person.Email);
    }

    public static CirculationEntry Read(byte[] buffer, int at) => new()
    {
        State = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at)),
        Person = new Person
        {
            Ip = JuProtocol.ReadWide(buffer, at + 2, 256),
            Id = JuProtocol.ReadWide(buffer, at + 258, 128),
            Name = JuProtocol.ReadWide(buffer, at + 386, 128),
            Email = JuProtocol.ReadWide(buffer, at + 514, 128)
        }
    };
}
