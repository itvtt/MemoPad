using System.IO;
using System.Net;
using MemoPad.Core;
using MemoPad.Net;
using MemoPad.Ui;

/// <summary>이 PC 안에서 127.0.0.x 주소 여럿으로 주고받기 (정음 메모패드와 같은 형식)</summary>
static partial class Program
{
    private static async Task Network()
    {
        string? absent = null;
        using var a = new MemoService(() => null, IPAddress.Parse("127.0.0.5"));
        using var b = new MemoService(() => absent, IPAddress.Parse("127.0.0.6"));
        Assert(a.Start() && b.Start(), "듣기: " + a.StartError + b.StartError);
        var received = new List<IncomingMemo>();
        var notes = new List<CirculationNote>();
        b.MemoReceived += memo => { lock (received) received.Add(memo); };
        b.NoteReceived += note => { lock (notes) notes.Add(note); };

        string big = Path.Combine(Folder("send"), "큰 파일.bin");
        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(big, bytes);
        string small = Path.Combine(Folder("send"), "한글 이름.txt");
        File.WriteAllText(small, "첨부 내용");
        var header = new MemoHeader { Sender = new Person { Ip = "127.0.0.5", Id = "a", Name = "가나다" }, X = 10, Y = 20 };
        string rtf = RtfText.FromPlain("주고받기 시험\n둘째 줄");
        var target = new Person { Ip = "127.0.0.6", Name = "B" };
        var result = await a.SendAsync(target, new OutgoingMemo(header, rtf, [new OutgoingFile("큰 파일.bin", big), new OutgoingFile("한글 이름.txt", small)]), CancellationToken.None);
        Assert(result.Ok, $"보내기: {result.Outcome} {result.Message}");
        await Until(() => received.Count == 1, "받은 메모");
        var memo = received[0];
        Equal("가나다", memo.Header.Sender.Name, "보낸 사람");
        Equal("주고받기 시험\n둘째 줄", RtfText.Plain(memo.Rtf), "본문");
        Equal(2, memo.Files.Count, "첨부 수");
        Assert(memo.Files[0].Data.AsSpan().SequenceEqual(bytes), "큰 첨부 바이트");
        Equal("한글 이름.txt", memo.Files[1].Name, "첨부 이름");
        Equal("첨부 내용", System.Text.Encoding.UTF8.GetString(memo.Files[1].Data), "작은 첨부");

        Equal(PeerState.Present, (await MemoService.QueryStatusAsync("127.0.0.6", CancellationToken.None)).State, "상태: 있음");
        absent = "회의 중입니다";
        Equal(PeerState.Absent, (await MemoService.QueryStatusAsync("127.0.0.6", CancellationToken.None)).State, "상태: 부재중");
        var refused = await a.SendAsync(target, new OutgoingMemo(header, rtf, []), CancellationToken.None);
        Equal(DeliveryOutcome.Absent, refused.Outcome, "부재중 보내기");
        Equal("회의 중입니다", refused.Message, "부재중 문구");
        Equal(PeerState.Off, (await MemoService.QueryStatusAsync("127.0.0.250", CancellationToken.None)).State, "상태: 꺼져 있음");

        var id = Guid.NewGuid();
        await MemoService.SendNoteAsync("127.0.0.6", passed: true, id, "127.0.0.5");
        await Until(() => notes.Count == 1, "회람 알림");
        Equal((id, "127.0.0.5", true), (notes[0].MemoId, notes[0].Ip, notes[0].Passed), "회람 알림 내용");
    }

    /// <summary>
    /// 포트: 127.0.0.8 = 정음 메모패드와 MemoPad 가 함께 켜진 PC (서로 다른 포트라 둘 다 듣고, MemoPad 끼리는 MemoPad 로),
    /// 127.0.0.7 = 정음 메모패드만 있는 PC (정음 포트로 보냄)
    /// </summary>
    private static async Task PortsFlow()
    {
        using var jungumBoth = new MemoService(() => null, IPAddress.Parse("127.0.0.8"));
        using var memoPadBoth = new MemoService(() => null, IPAddress.Parse("127.0.0.8"));
        using var jungumOnly = new MemoService(() => null, IPAddress.Parse("127.0.0.7"));
        using var sender = new MemoService(() => null, IPAddress.Parse("127.0.0.5"));
        jungumBoth.SetJungumPort(true);
        jungumOnly.SetJungumPort(true);
        Assert(jungumBoth.JungumListening && jungumOnly.JungumListening && !jungumBoth.Listening, "정음 메모패드 대신: 정음 포트만 듣기");
        Assert(memoPadBoth.Start(), "정음 메모패드가 켜진 PC 에서 MemoPad 듣기: " + memoPadBoth.StartError);
        Assert(sender.Start(), "보내는 쪽 듣기: " + sender.StartError);
        var atJungumBoth = new List<IncomingMemo>();
        var atMemoPadBoth = new List<IncomingMemo>();
        var atJungumOnly = new List<IncomingMemo>();
        jungumBoth.MemoReceived += memo => { lock (atJungumBoth) atJungumBoth.Add(memo); };
        memoPadBoth.MemoReceived += memo => { lock (atMemoPadBoth) atMemoPadBoth.Add(memo); };
        jungumOnly.MemoReceived += memo => { lock (atJungumOnly) atJungumOnly.Add(memo); };

        var header = new MemoHeader { Sender = new Person { Ip = "127.0.0.5", Id = "a", Name = "가" } };
        string file = Path.Combine(Folder("ports"), "첨부.txt");
        File.WriteAllText(file, "포트 시험");
        var both = await sender.SendAsync(new Person { Ip = "127.0.0.8", Name = "함께" }, new OutgoingMemo(header, RtfText.FromPlain("MemoPad 에게"), [new OutgoingFile("첨부.txt", file)]), CancellationToken.None);
        Assert(both.Ok, $"함께 켜진 PC 로 보내기: {both.Outcome} {both.Message}");
        await Until(() => atMemoPadBoth.Count == 1, "MemoPad 가 받음");
        Equal(0, atJungumBoth.Count, "정음 메모패드는 받지 않음");
        Equal("포트 시험", System.Text.Encoding.UTF8.GetString(atMemoPadBoth[0].Files[0].Data), "MemoPad 첨부 (MemoPad 첨부 포트)");

        var only = await sender.SendAsync(new Person { Ip = "127.0.0.7", Name = "정음" }, new OutgoingMemo(header, RtfText.FromPlain("정음에게"), [new OutgoingFile("첨부.txt", file)]), CancellationToken.None);
        Assert(only.Ok, $"정음 메모패드만 있는 PC 로 보내기: {only.Outcome} {only.Message}");
        await Until(() => atJungumOnly.Count == 1, "정음 포트로 받음");
        Equal("정음에게", RtfText.Plain(atJungumOnly[0].Rtf), "정음 포트 본문");
        Equal(1, atJungumOnly[0].Files.Count, "정음 포트 첨부");
        Equal(PeerState.Present, (await MemoService.QueryStatusAsync("127.0.0.7", CancellationToken.None)).State, "정음 포트 상태 확인");

        var host = NewDesk("정음방", "127.0.0.10");
        var guest = NewDesk("손님", "127.0.0.6");
        try
        {
            host.Chat.Stop();
            host.Chat.SetJungumPort(true);
            Assert(!host.Chat.Listening && host.Chat.JungumListening, "대화: 정음 포트만 듣기");
            var session = new GuestSession(guest, host.Me);
            Assert(await session.ConnectAsync(), "대화: 정음 포트로 들어감");
            await Until(() => session.Lines.Any(l => l.Text.Contains("연결이 되었습니다")), "대화: 연결 알림");
            session.Leave();
        }
        finally
        {
            foreach (var window in System.Windows.Application.Current.Windows.OfType<ChatWindow>().ToList()) window.Close();
            host.Shutdown();
            guest.Shutdown();
        }
    }

    /// <summary>책상(앱 한 개) 하나: 따로 쓰는 폴더 · 주소</summary>
    internal static Desk NewDesk(string name, string ip)
    {
        string folder = Folder("desk-" + name);
        var settings = new AppSettings
        {
            Name = name, Id = name.ToLowerInvariant(), Ip = ip, StartupConfigured = true, Sound = false,
            AddressBookFolder = folder, NotifyUndelivered = false, AlwaysShowTitle = true
        };
        var desk = new Desk(settings, new MemoStore(folder), IPAddress.Parse(ip));
        desk.Start();
        Assert(desk.Net.Listening, $"{name} 듣기: {desk.Net.StartError}");
        return desk;
    }

    private static async Task CirculationFlow()
    {
        var a = NewDesk("가", "127.0.0.5");
        var b = NewDesk("나", "127.0.0.6");
        var c = NewDesk("다", "127.0.0.7");
        try
        {
            var start = a.NewMemo();
            start.ReplaceRtf(RtfText.FromPlain("회람 시험입니다"));
            await a.SendAsync(start, SendKind.Circulate, [b.Me, c.Me]);
            Equal(MemoOrigin.CirculationOriginal, start.Memo.Origin, "원본");

            await Until(() => b.Windows.Any(w => w.Memo.Origin == MemoOrigin.CirculationReceived), "나: 회람 받음");
            var atB = b.Windows.First(w => w.Memo.Origin == MemoOrigin.CirculationReceived);
            Equal("가", atB.Memo.Circulation!.Originator.Name, "나: 시작한 사람");
            Equal(0, atB.Memo.Circulation.MyIndex, "나: 내 자리");
            Assert(atB.Memo.Text.StartsWith("[나의 의견]"), "나: 의견 칸 " + atB.Memo.Text);
            await Until(() => start.Memo.Circulation!.Targets[0].State == Desk.CirculationReceivedState, "가: 나 받음 알림");

            atB.ReplaceRtf(RtfText.Append(atB.Memo.Rtf, "나도 좋습니다"));
            await b.PassCirculationAsync(atB);
            await Until(() => c.Windows.Any(w => w.Memo.Origin == MemoOrigin.CirculationReceived), "다: 회람 받음");
            await Until(() => start.Memo.Circulation!.Targets[0].State == Desk.CirculationPassedState, "가: 나 전달 알림");
            var atC = c.Windows.First(w => w.Memo.Origin == MemoOrigin.CirculationReceived);
            Equal(1, atC.Memo.Circulation!.MyIndex, "다: 내 자리");
            Equal("가", atC.Memo.Circulation.Originator.Name, "다: 시작한 사람 (머리는 늘 시작한 사람)");
            Assert(atC.Memo.Text.Contains("나도 좋습니다"), "다: 나의 의견이 보임");

            await c.PassCirculationAsync(atC);
            await Until(() => a.Windows.Any(w => w.Memo.Origin == MemoOrigin.CirculationResult), "가: 회람 결과");
            var result = a.Windows.First(w => w.Memo.Origin == MemoOrigin.CirculationResult);
            Assert(result.Memo.Text.StartsWith("[회람 결과입니다.]"), "결과 머리: " + result.Memo.Text);
            Assert(result.Memo.Text.Contains("나도 좋습니다"), "결과에 의견");
            await Until(() => start.Memo.Circulation!.Targets.All(t => t.State == Desk.CirculationPassedState), "가: 모두 전달");
        }
        finally
        {
            foreach (var desk in new[] { a, b, c })
            {
                foreach (var window in desk.Windows.ToList()) window.Close();
                desk.Shutdown();
            }
        }
    }

    private static async Task ChatFlow()
    {
        var host = NewDesk("방장", "127.0.0.5");
        var one = NewDesk("하나", "127.0.0.6");
        var two = NewDesk("둘", "127.0.0.7");
        try
        {
            var first = new GuestSession(one, host.Me);
            var second = new GuestSession(two, host.Me);
            Assert(await first.ConnectAsync(), "하나 연결");
            await Until(() => first.Lines.Any(l => l.Text.Contains("연결이 되었습니다")), "하나: 연결 알림");
            Assert(await second.ConnectAsync(), "둘 연결");
            await Until(() => host.Chat.Host?.GuestCount == 2, "방장: 손님 둘");
            await Until(() => first.Members.Count == 3, "하나: 참여자 셋 (방장 · 하나 · 둘)");
            await first.SayAsync("안녕하세요 hi");
            await Until(() => second.Lines.Any(l => l.Text == "안녕하세요 hi" && l.Who == "하나"), "둘: 하나의 말");
            await Until(() => host.Chat.Host!.Lines.Any(l => l.Text == "안녕하세요 hi"), "방장: 하나의 말");
            await host.Chat.Host!.SayAsync("방장입니다");
            await Until(() => first.Lines.Any(l => l.Text == "방장입니다") && second.Lines.Any(l => l.Text == "방장입니다"), "손님들: 방장의 말");
            second.Leave();
            await Until(() => host.Chat.Host!.GuestCount == 1, "방장: 둘 나감");
            await Until(() => first.Lines.Any(l => l.Text.Contains("퇴실")), "하나: 퇴실 알림");
            first.Leave();
        }
        finally
        {
            foreach (var window in System.Windows.Application.Current.Windows.OfType<ChatWindow>().ToList()) window.Close();
            foreach (var desk in new[] { host, one, two }) desk.Shutdown();
        }
    }
}
