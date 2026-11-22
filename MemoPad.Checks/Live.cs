using System.IO;
using MemoPad.Core;
using MemoPad.Net;
using MemoPad.Ui;

/// <summary>
/// 같은 망의 정음 메모패드(또는 다른 MemoPad)와 실제로 주고받기: --jungum &lt;IP&gt; &lt;ID&gt;
/// 이 PC 의 주소는 MEMOPAD_LIVE_IP (기본 127.0.0.9: 같은 PC 의 정음 메모패드는 다른 주소에서 9710 을 씀)
/// </summary>
static class Live
{
    private static string MyIp => Environment.GetEnvironmentVariable("MEMOPAD_LIVE_IP") is { Length: > 0 } custom ? custom : "127.0.0.9";

    /// <summary>정음 메모패드가 이 주소의 정음 포트(9710 · 9712)로 보내니 정음 포트로도 받음</summary>
    private static Desk NewDesk(string myIp)
    {
        Environment.SetEnvironmentVariable("MEMOPAD_JUNGUM_PORTS", "1");
        var desk = Program.NewDesk("MemoPad시험", myIp);
        Program.Assert(desk.Net.JungumListening, "정음 포트(9710)로 받기");
        return desk;
    }

    public static int Run(string ip, string id)
    {
        string myIp = MyIp;
        var desk = NewDesk(myIp);
        var target = new Person { Ip = ip, Id = id, Name = id };
        int failures = 0;
        void Report(bool ok, string text) { Console.WriteLine($"{(ok ? "OK  " : "FAIL")}  {text}"); if (!ok) failures++; }
        Program.Run(async () =>
        {
            var status = await MemoService.QueryStatusAsync(ip, CancellationToken.None);
            Report(status.State is PeerState.Present or PeerState.Absent, $"상태 확인: {status.Text} ({status.Milliseconds} ms)");

            var memo = desk.NewMemo();
            memo.ReplaceRtf(RtfText.FromPlain($"MemoPad 시험 메모 {DateTime.Now:HH:mm:ss}\n한글 · English · 1234"));
            string file = Path.Combine(Path.GetTempPath(), "MemoPad 첨부 시험.txt");
            File.WriteAllText(file, "MemoPad 에서 보낸 첨부입니다.\r\n", new System.Text.UTF8Encoding(true));
            await memo.AttachFiles([file]);
            await desk.SendAsync(memo, SendKind.Send, [target]);
            Report(memo.Memo.SentTo.Any(p => p.Ip == ip), "메모 + 첨부 보내기 (그쪽 화면에 메모가 떴는지 보세요)");

            var circulation = desk.NewMemo();
            circulation.ReplaceRtf(RtfText.FromPlain($"MemoPad 회람 시험 {DateTime.Now:HH:mm:ss}"));
            await desk.SendAsync(circulation, SendKind.Circulate, [target]);
            Report(circulation.Memo.Circulation?.Targets[0].State == Desk.CirculationReceivedState, "회람 시작");
            await Program.Until(() => circulation.Memo.Circulation!.Targets[0].State != Desk.CirculationWaiting, "받음 알림", 15);
            Console.WriteLine("그쪽에서 받은 회람에 의견을 쓰고 [회람 전달] 하세요 (3분까지 기다림)");
            await Program.Until(() => desk.Windows.Any(w => w.Memo.Origin == MemoOrigin.CirculationResult), "회람 결과", 180);
            var result = desk.Windows.First(w => w.Memo.Origin == MemoOrigin.CirculationResult);
            Report(result.Memo.Text.StartsWith("[회람 결과입니다.]"), "회람 결과: " + result.Memo.Text.Replace("\n", " / "));
            Report(circulation.Memo.Circulation!.Targets[0].State == Desk.CirculationPassedState, "전달 알림 → 상태 '전달'");
        }, 240);
        foreach (var window in desk.Windows.ToList()) window.Close();
        desk.Shutdown();
        return failures == 0 ? 0 : 1;
    }

    /// <summary>상태 확인만: --status &lt;IP&gt; (MemoPad 포트, 없으면 정음 메모패드 포트. 그쪽 화면에는 아무것도 뜨지 않음)</summary>
    public static int Status(string ip)
    {
        PeerStatus? status = null;
        Program.Run(async () => status = await MemoService.QueryStatusAsync(ip, CancellationToken.None), 20);
        Console.WriteLine($"{ip}: {status?.Text} ({status?.Milliseconds} ms)");
        return status?.State is PeerState.Present or PeerState.Absent ? 0 : 1;
    }

    /// <summary>대화: --jungum-chat &lt;IP&gt; (그쪽 대화방에 들어가 말하고 나옴)</summary>
    public static int Chat(string ip)
    {
        string myIp = MyIp;
        var desk = NewDesk(myIp);
        var session = new GuestSession(desk, new Person { Ip = ip, Name = ip });
        bool ok = false;
        Program.Run(async () =>
        {
            ok = await session.ConnectAsync();
            await Program.Until(() => session.Members.Count >= 2, "참여자 목록", 10);
            await session.SayAsync($"MemoPad 대화 시험 {DateTime.Now:HH:mm:ss}");
            await Task.Delay(5000);
            foreach (var line in session.Lines) Console.WriteLine($"[{line.Kind}] {line.Who} {line.Text}");
            Console.WriteLine("참여자: " + string.Join(", ", session.Members.Select(m => $"{m.ChatName}({m.Person.Ip})")));
            session.Leave();
            await Task.Delay(500);
        }, 40);
        desk.Shutdown();
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 받는 쪽으로 기다림: --jungum-wait &lt;초&gt;. 받은 회람은 의견을 달아 [회람 전달], 받은 메모는 답장.
    /// 이 PC 의 주소 · ID 는 MEMOPAD_LIVE_IP · MEMOPAD_LIVE_ID (정음 메모패드 주소록에 그 IP · ID 로 넣어 두고 보내세요)
    /// </summary>
    public static int Wait(int seconds)
    {
        string myIp = MyIp;
        var desk = NewDesk(myIp);
        if (Environment.GetEnvironmentVariable("MEMOPAD_LIVE_ID") is { Length: > 0 } id) desk.Settings.Id = id;
        var handled = new HashSet<Guid>();
        int done = 0;
        Console.WriteLine($"{myIp} 에서 기다리는 중 ({seconds}초)");
        Program.Run(async () =>
        {
            var end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end)
            {
                foreach (var window in desk.Windows.ToList())
                {
                    if (!handled.Add(window.Memo.Id)) continue;
                    var memo = window.Memo;
                    Console.WriteLine($"받음: {memo.KindText} · {memo.From?.Label} · {memo.Text.Replace('\n', '/')}");
                    if (memo.Origin == MemoOrigin.CirculationReceived)
                    {
                        window.ReplaceRtf(RtfText.Append(memo.Rtf, "MemoPad 에서 의견 남깁니다"));
                        await desk.PassCirculationAsync(window);
                        Console.WriteLine($"회람 전달 → 상태: {string.Join(", ", memo.Circulation!.Targets.Select(t => $"{t.Person.Label}={Desk.CirculationStateText(t.State)}"))}");
                        done++;
                    }
                    else if (memo.Origin == MemoOrigin.Received)
                    {
                        window.ReplaceRtf(RtfText.Append(memo.Rtf, "MemoPad 답장입니다"));
                        await desk.SendAsync(window, SendKind.Reply);
                        Console.WriteLine("답장 보냄");
                        done++;
                    }
                }
                await Task.Delay(300);
            }
        }, seconds + 30);
        if (desk.Chat.Host is { } host)
            foreach (var line in host.Lines) Console.WriteLine($"대화 [{line.Kind}] {line.Who} {line.Text}");
        foreach (var window in desk.Windows.ToList()) window.Close();
        desk.Shutdown();
        return done > 0 || desk.Chat.Host is not null ? 0 : 1;
    }
}
