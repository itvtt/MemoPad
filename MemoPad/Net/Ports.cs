using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace MemoPad.Net;

/// <summary>
/// 포트. MemoPad 는 자기 포트(메모 9810 · 대화 9812 · 첨부 9826~)로 받아 같은 PC 의 정음 메모패드(9710 · 9712 · 9726~)와 부딪히지 않음.
/// 정음 메모패드를 쓰는 사람도 보낼 수 있게, 이 PC 에 정음 메모패드가 없을 때만 정음 포트로도 받음 (있으면 정음 포트는 정음 메모패드 것).
/// 보낼 때는 두 포트에 함께 연결해 MemoPad 가 받으면 MemoPad, 아니면 정음 메모패드로.
/// </summary>
public static class Ports
{
    public const int Memo = 9810;
    public const int Chat = 9812;
    public const int Attach = 9826;

    /// <summary>MemoPad 메모 포트 (시험할 때 MEMOPAD_MEMO_PORT)</summary>
    public static int MemoPort => Env("MEMOPAD_MEMO_PORT", Memo);
    /// <summary>MemoPad 대화 포트 (시험할 때 MEMOPAD_CHAT_PORT)</summary>
    public static int ChatPort => Env("MEMOPAD_CHAT_PORT", Chat);

    /// <summary>보낼 때 연결할 상대 포트: MemoPad, 그다음 정음 메모패드</summary>
    public static int[] MemoTargets => MemoPort == JuProtocol.MemoPort ? [MemoPort] : [MemoPort, JuProtocol.MemoPort];
    public static int[] ChatTargets => ChatPort == JuProtocol.ChatPort ? [ChatPort] : [ChatPort, JuProtocol.ChatPort];

    /// <summary>이 PC 에 정음 메모패드가 있음 (실행 중이거나 설치됨)</summary>
    public static bool JungumOnThisPc
    {
        get
        {
            var running = Process.GetProcessesByName("JUMemoPad");
            foreach (var process in running) process.Dispose();
            if (running.Length > 0) return true;
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                string root = Environment.GetFolderPath(folder);
                if (root.Length > 0 && File.Exists(Path.Combine(root, "SamSung", "JungUmMemoPad", "JUMemoPad.exe"))) return true;
            }
            return false;
        }
    }

    /// <summary>정음 포트(9710 · 9712)로도 받을지: 이 PC 에 정음 메모패드가 없을 때만. 시험할 때 MEMOPAD_JUNGUM_PORTS=1 · 0</summary>
    public static bool ListenJungum => Environment.GetEnvironmentVariable("MEMOPAD_JUNGUM_PORTS") switch
    {
        "1" => true,
        "0" => false,
        _ => !JungumOnThisPc
    };

    /// <summary>
    /// 상대에게 연결: [MemoPad 포트, 정음 포트]에 함께 연결해 MemoPad 쪽이 받으면 MemoPad, 아니면 정음 메모패드.
    /// (Windows 는 닫힌 포트도 1초쯤 다시 시도하므로 차례로 하면 정음 메모패드 사용자에게 늘 늦어짐)
    /// 둘 다 안 되면 마지막 포트의 오류를 던짐
    /// </summary>
    public static async Task<TcpClient> ConnectAsync(string ip, int[] ports, TimeSpan timeout, CancellationToken token)
    {
        if (ports.Length == 1) return await JuProtocol.ConnectAsync(ip, ports[0], timeout, token);
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(token);
        var mine = TryConnect(ip, ports[0], timeout, abandon.Token);
        var other = TryConnect(ip, ports[1], timeout, abandon.Token);
        var first = await Task.WhenAny(mine, other);
        if (first == other && other.Result.Client is not null)
            await Task.WhenAny(mine, Task.Delay(Grace, token));   // 같은 PC 에 MemoPad 도 있으면 거의 함께 받음
        else
            await (first == mine ? (mine.Result.Client is null ? other : Task.CompletedTask) : mine);
        var chosen = mine.IsCompleted && mine.Result.Client is { } memoPad ? memoPad
                   : other.IsCompleted ? other.Result.Client : null;
        abandon.Cancel();
        foreach (var attempt in new[] { mine, other })
            _ = attempt.ContinueWith(t => { if (t.Result.Client is { } client && client != chosen) client.Dispose(); }, TaskScheduler.Default);
        if (chosen is not null) return chosen;
        token.ThrowIfCancellationRequested();
        var error = (other.IsCompleted ? other.Result.Error : null) ?? mine.Result.Error;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error ?? new SocketException((int)SocketError.ConnectionRefused));
        return null!;
    }

    /// <summary>정음 포트가 먼저 받았을 때 MemoPad 포트를 더 기다리는 시간 (같은 PC 면 둘이 거의 함께 받음)</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(150);

    private static async Task<(TcpClient? Client, Exception? Error)> TryConnect(string ip, int port, TimeSpan timeout, CancellationToken token)
    {
        try { return (await JuProtocol.ConnectAsync(ip, port, timeout, token), null); }
        catch (Exception error) { return (null, error); }
    }

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int port) && port is > 0 and < 65536 ? port : fallback;
}
