using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using MemoPad.Core;
using MemoPad.Net;
using MemoPad.Ui;

/// <summary>
/// MemoPad 확인. 실패하면 종료 코드 1.
///   (인자 없음)                   자판 변환 · 메모 머리 · RTF · 주소록 · 저장소 · 정음 메모 파일 · 주고받기(이 PC 안 127.0.0.x) · 회람 · 대화
///   --snapshot &lt;폴더&gt;           메모 · 관리기 · 설정 · 주소록 · 받을 사람 · 알람 창을 PNG 로
///   --jungum &lt;IP&gt; &lt;ID&gt;         같은 망의 정음 메모패드와 실제로: 상태 확인 · 메모 · 첨부 · 회람 시작 (그쪽 화면에서 확인)
/// </summary>
static partial class Program
{
    private static int _passed, _failed;
    private static string _root = "";

    [STAThread]
    private static int Main(string[] args)
    {
        _root = Path.Combine(Path.GetTempPath(), "memopad-checks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("MEMOPAD_DATA_DIR", Path.Combine(_root, "settings"));
        // 확인은 MemoPad 포트로만 (정음 포트는 시험마다 직접 켬). 실제 정음 메모패드와 시험(--jungum…)은 Live 에서 켬
        Environment.SetEnvironmentVariable("MEMOPAD_JUNGUM_PORTS", "0");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // await 뒤에도 UI 스레드로 돌아오게 (앱과 같게)
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MemoPad;component/Theme.xaml") });
        try
        {
            if (args is ["--snapshot", var folder]) return Snapshots.Run(folder);   // 앱과 같게 (네트워크 기능 꺼짐)
            Features.Network = true;   // 꺼 둔 네트워크 코드도 시험해 깨지지 않게
            if (args is ["--jungum", var ip, var id]) return Live.Run(ip, id);
            if (args is ["--jungum-wait", var wait]) return Live.Wait(int.Parse(wait));
            if (args is ["--jungum-chat", var chatIp]) return Live.Chat(chatIp);
            if (args is ["--status", var statusIp]) return Live.Status(statusIp);

            Check("한/영 오타 변환: 두벌식 · 겹받침 · 겹모음 · 되돌리기", Typing);
            Check("메모 머리: 이름 · ID(한글) · 메모 id · 위치 · 시각(OLE) · 회람 칸이 그대로", HeaderRoundTrip);
            Check("메모 머리: 정음 메모패드가 보낸 배치에서 읽기", HeaderFromJungumLayout);
            Check("RTF: 따옴표 (빈 줄 포함) · 앞에 줄 넣기 · 일반 글자 · 그림", Rtf);
            Check("주소록: 찾기 · 초성 · CSV 내보내기/불러오기", AddressBookCsv);
            Check("저장소: 저장 · 다시 읽기 · 휴지통 · 되살리기 · 폴더 · 첨부", Store);
            Check("저장소 지키기: 깨진 파일 되살림 · 읽지 못한 메모 폴더 · 잠깐 잠긴 파일 · 저장 실패 다시 시도 · 하루 백업 7개", StoreSafety);
            Check("접기: 오른쪽 위 작은 사각형 · 옮긴 자리에서 펼침 · 정렬 · 다시 열기", () => Run(CollapseSquare));
            Check("글꼴 · 글자 크기: 메모마다 본문 전체 · 저장 · 다시 열어도 그대로 · 단계 · 기본으로 · 읽기 전용", () => Run(FontPerMemo));
            Check("데이터 폴더: MemoPad 파일만 옮김 · 첨부까지 · 있는 데이터 알아보기 · 안쪽 폴더", DataFolder);
            Check("단축키: 모두 숨기기 · 모두 보이기 (전역) · 저장 · 겹치면 옮김 · 남이 쓰면 되돌림, 메모 머리 날짜", () => Run(HotkeysFlow));
            Check("정음 메모 파일(HMD) 읽기", Hmd);
            Check("대화 메시지: 내 정보 · 참여자 · 돌려주기", ChatPayloads);
            Check("주고받기: 메모 · 큰 첨부 · 상태 · 부재중", () => Run(Network));
            Check("회람: 시작 → 둘째 → 셋째 → 결과, 받음 · 전달 알림", () => Run(CirculationFlow));
            Check("대화: 방장 · 손님 둘 · 말 돌려주기 · 나가기", () => Run(ChatFlow));
            Check("포트: 정음 메모패드와 한 PC 에 함께 · 정음 포트로 보내기 · 정음 대화방", () => Run(PortsFlow));
            Check("네트워크 기능 꺼짐(앱 기본): 포트를 하나도 열지 않음", NetworkOff);
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, Action check)
    {
        try
        {
            check();
            _passed++;
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception error)
        {
            _failed++;
            Console.WriteLine($"FAIL  {name}\n      {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
        }
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected '{expected}', got '{actual}'");
    }

    /// <summary>비동기 작업을 UI 스레드에서 메시지를 돌리며 기다림</summary>
    internal static void Run(Func<Task> work, int seconds = 60)
    {
        var task = work();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!task.IsCompleted) throw new TimeoutException("시간 안에 끝나지 않았습니다");
        task.GetAwaiter().GetResult();
    }

    /// <summary>조건이 맞을 때까지 메시지를 돌리며 기다림</summary>
    internal static async Task Until(Func<bool> condition, string what, int seconds = 15)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.Now > end) throw new TimeoutException($"기다렸지만 오지 않음: {what}");
            await Task.Delay(50);
        }
    }

    internal static string Folder(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ── 자판 ──────────────────────────────────────────────────

    private static void Typing()
    {
        Equal("안녕하세요", Hangul.ToHangul("dkssudgktpdy"), "안녕하세요");
        Equal("값을 읽고", Hangul.ToHangul("rkqtdmf dlfrrh"), "겹받침 ㅄ · ㄺ");
        Equal("왜 괜찮아", Hangul.ToHangul("dho rhoscksgdk"), "겹모음 ㅙ");
        Equal("까치 뿌리 쓸", Hangul.ToHangul("Rkcl Qnfl Tmf"), "된소리");
        Equal("hello", Hangul.ToKeys("ㅗ디ㅣㅐ"), "한글 → 영문");
        Equal("dkssudgktpdy", Hangul.ConvertTypo("안녕하세요"), "되돌리기");
        Equal("ㅎㄱㄷ", Hangul.Initials("홍길동"), "초성");
        Equal("메모 2시", Hangul.ToHangul("apah 2tl"), "숫자 · 빈칸은 그대로");
    }

    // ── 메모 머리 ──────────────────────────────────────────────

    private static void HeaderRoundTrip()
    {
        var created = new DateTime(2026, 11, 17, 22, 53, 2);
        var header = new MemoHeader
        {
            Sender = new Person { Ip = "10.1.2.3", Id = "hong123", Name = "홍길동AB" },
            AttachmentCount = 2,
            Type = MemoHeader.TypeCirculation,
            MemoId = Guid.NewGuid(),
            X = 1635, Y = 838, Width = 235, Height = 141, Color = 6,
            Created = created, Modified = created.AddMinutes(3),
            Entries =
            [
                new CirculationEntry { State = 3, Person = new Person { Ip = "10.1.2.4", Id = "kim", Name = "김철수", Email = "kim@example.com" } },
                new CirculationEntry { State = 0, Person = new Person { Ip = "10.1.2.5", Id = "lee", Name = "이영희" } }
            ]
        };
        var bytes = header.ToBytes();
        Equal(JuProtocol.HeaderSize + 2 * JuProtocol.EntrySize, bytes.Length, "길이");
        Equal(2, MemoHeader.EntryCount(bytes), "회람 칸 수");
        var back = MemoHeader.Parse(bytes);
        Equal("10.1.2.3", back.Sender.Ip, "IP");
        Equal("hong123", back.Sender.Id, "ID");
        Equal("홍길동AB", back.Sender.Name, "이름");
        Equal(header.MemoId, back.MemoId, "메모 id");
        Equal(2, back.AttachmentCount, "첨부 수");
        Equal(MemoHeader.TypeCirculation, back.Type, "종류");
        Equal((1635, 838, 235, 141, 6), (back.X, back.Y, back.Width, back.Height, back.Color), "위치 · 크기 · 색");
        Assert(Math.Abs((back.Created - created).TotalSeconds) < 1, "만든 시각");
        Equal("김철수", back.Entries[0].Person.Name, "회람 칸 이름");
        Equal("kim@example.com", back.Entries[0].Person.Email, "회람 칸 메일");
        Equal(3, back.Entries[0].State, "회람 칸 상태");
        // ANSI(CP949) 칸 뒤에 같은 글자의 UTF-16 이 붙어 있음 (정음 메모패드와 같은 칸)
        var ansi = JuProtocol.Ansi.GetBytes("홍길동AB");
        Assert(bytes.AsSpan(192, ansi.Length).SequenceEqual(ansi) && bytes[192 + ansi.Length] == 0, "이름 칸 ANSI");
        Equal("홍길동AB", System.Text.Encoding.Unicode.GetString(bytes, 192 + ansi.Length + 1, 10), "이름 칸 UTF-16");
        // 시각은 OLE 날짜 (double)
        Equal(created.ToOADate(), BitConverter.ToDouble(bytes, 492), "OLE 날짜");
    }

    /// <summary>정음 메모패드가 실제로 보낸 머리와 같은 배치(값은 바꿈)를 읽음</summary>
    private static void HeaderFromJungumLayout()
    {
        var buffer = new byte[JuProtocol.HeaderSize];
        JuProtocol.Ansi.GetBytes("10.0.0.7").CopyTo(buffer, 0);
        buffer[14] = 0x10; buffer[17] = 0xF0;          // 정음 메모패드가 남기는 쓰레기 값
        JuProtocol.WriteDualString(buffer, 128, 64, "dm");
        JuProtocol.WriteDualString(buffer, 192, 64, "디엠");
        System.Text.Encoding.Unicode.GetBytes("9.5.1.133").CopyTo(buffer, 322);
        BitConverter.GetBytes(new DateTime(2026, 11, 17).ToOADate()).CopyTo(buffer, 492);
        BitConverter.GetBytes(1).CopyTo(buffer, 512);
        var header = MemoHeader.Parse(buffer);
        Equal(0, header.AttachmentCount, "첨부 수 (표시 0x10 이 NUL+2 에 없으면 0)");
        Equal("10.0.0.7", header.Sender.Ip, "IP");
        Equal("dm", header.Sender.Id, "ID");
        Equal("디엠", header.Sender.Name, "이름");
        Equal("9.5.1.133", header.Version, "버전");
        Equal(new DateTime(2026, 11, 17), header.Created, "만든 날");
        Equal(0, header.Entries.Count, "회람 칸");

        // 정음 메모패드가 보낸 그대로: "172.16.10.21" NUL 00 10 00 00 F0 01 00 → 첨부 1 (IP 길이에 따라 자리가 움직임)
        var captured = new byte[JuProtocol.HeaderSize];
        byte[] start = [.. System.Text.Encoding.ASCII.GetBytes("172.16.10.21"), 0x00, 0x00, 0x10, 0x00, 0x00, 0xF0, 0x01, 0x00];
        start.CopyTo(captured, 0);
        Equal(1, MemoHeader.Parse(captured).AttachmentCount, "정음 배치 첨부 수");
        // 보낼 때도 같은 배치 (IP 9자)
        var mine = new MemoHeader { Sender = new Person { Ip = "127.0.0.9" }, AttachmentCount = 3 }.ToBytes();
        Equal((byte)0x10, mine[11], "NUL+2 표시");
        Equal((byte)3, mine[15], "NUL+6 첨부 수");
    }

    // ── RTF ───────────────────────────────────────────────────

    private static void Rtf()
    {
        string rtf = RtfText.FromPlain("첫 줄\n\n셋째 줄 {괄호}");
        Equal("첫 줄\n\n셋째 줄 {괄호}", RtfText.Plain(rtf), "일반 글자 → RTF → 글자");
        // 빈 줄이 있어도 (예전에 여기서 꺼짐)
        Equal("> 첫 줄\n> \n> 셋째 줄 {괄호}", RtfText.Plain(RtfText.Quote(rtf)), "따옴표");
        Equal("[회람 결과입니다.]\n\n첫 줄", RtfText.Plain(RtfText.Prepend(RtfText.FromPlain("첫 줄"), "[회람 결과입니다.]", "")), "앞에 넣기");
        // 정음 메모패드가 보내는 RTF (CP949 \'xx)
        string jungum = "{\\rtf1\\ansi\\ansicpg949\\deff0{\\fonttbl{\\f0\\fnil\\fcharset129 \\'b1\\'bc\\'b8\\'b2;}}\\pard\\f0\\fs20\\lang1042\\'c5\\'d7\\'bd\\'ba\\'c6\\'ae hello\\par}";
        Equal("테스트 hello", RtfText.Plain(jungum), "CP949 RTF");
        // 그림 한 장짜리 RTF 가 다시 읽힘
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(20, 10, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var document = RtfText.Document(RtfText.FromImage(bitmap));
        Assert(document.Blocks.Count > 0, "그림 RTF");
        var body = MemoService.EncodeBody(jungum);
        Equal(jungum, MemoService.DecodeBody(body), "보내는 본문 (끝 NUL)");
    }

    private static void AddressBookCsv()
    {
        var book = new AddressBook();
        book.Upsert(new Person { Name = "홍길동", Id = "hong", Ip = "10.0.0.1", Email = "hong@example.com" });
        book.Upsert(new Person { Name = "김, 철수", Id = "kim", Ip = "10.0.0.2" });
        book.AddGroup("기술팀").Members.AddRange(["10.0.0.1", "10.0.0.2"]);
        Equal(1, book.Search("ㅎㄱ").Count(), "초성 찾기");
        Equal(1, book.Search("10.0.0.2").Count(), "IP 찾기");
        var copy = new AddressBook();
        Equal(2, copy.MergeCsv(book.ToCsv()), "CSV 불러오기");
        Equal("김, 철수", copy.FindByIp("10.0.0.2")?.Name, "쉼표 든 이름");
        Equal(2, copy.MembersOf(copy.Groups.Single()).Count(), "그룹");
        string path = Path.Combine(Folder("book"), "addressbook.json");
        copy.Save(path);
        Equal(2, AddressBook.Load(path).People.Count, "저장 · 읽기");
    }

    private static void Store()
    {
        string folder = Folder("store");
        var store = new MemoStore(folder);
        var memo = new MemoData { Rtf = RtfText.FromPlain("저장 시험"), Text = "저장 시험", Color = "#CDE7FF", Alarm = new Alarm { At = DateTime.Today.AddDays(1) } };
        store.Save(memo);
        string file = Path.Combine(_root, "att.txt");
        File.WriteAllText(file, "첨부");
        store.AddAttachment(memo, file);
        store.AddAttachment(memo, file);
        Equal(2, memo.Attachments.Count, "첨부 둘 (같은 이름 → (2))");
        Assert(memo.Attachments[1].Contains("(2)"), "두 번째 이름");
        // 폴더 → 폴더이름.zip (안의 파일까지)
        string dropped = Path.Combine(_root, "자료 폴더");
        Directory.CreateDirectory(Path.Combine(dropped, "안"));
        File.WriteAllText(Path.Combine(dropped, "안", "a.txt"), "가");
        string zipName = store.CopyAttachment(memo, dropped + Path.DirectorySeparatorChar);
        Equal("자료 폴더.zip", zipName, "폴더 첨부 이름");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(store.AttachmentPath(memo, zipName)))
            Assert(zip.Entries.Any(entry => entry.FullName.Replace('\\', '/') == "자료 폴더/안/a.txt"), "폴더 첨부 내용");
        Equal(2, memo.Attachments.Count, "CopyAttachment 는 목록을 건드리지 않음");
        // 열려 있는 첨부는 빼지 않음
        using (File.Open(store.AttachmentPath(memo, memo.Attachments[1]), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert(!store.RemoveAttachment(memo, memo.Attachments[1]) && memo.Attachments.Count == 2, "열린 첨부는 그대로 둠");
        Assert(store.RemoveAttachment(memo, memo.Attachments[1]) && memo.Attachments.Count == 1, "닫으면 뺌");
        store.AddAttachment(memo, file);
        var work = store.AddFolder("업무");
        memo.Folder = work.Id;
        store.Save(memo);
        var again = new MemoStore(folder);
        var loaded = again.Find(memo.Id)!;
        Equal("저장 시험", loaded.Text, "다시 읽은 글자");
        Equal(work.Id, loaded.Folder, "폴더");
        Equal("업무", again.Folders.Single().Name, "폴더 이름");
        Assert(loaded.Alarm is not null, "알람");
        again.MoveToTrash(loaded, useTrash: true);
        Assert(again.Trash.Contains(loaded) && !again.Live.Contains(loaded), "휴지통");
        again.Restore(loaded);
        Assert(again.Live.Contains(loaded), "되살리기");
        again.Delete(loaded);
        Assert(new MemoStore(folder).Find(memo.Id) is null && !Directory.Exists(Path.Combine(folder, "memos", memo.Id.ToString("N"))), "지우기 (첨부 폴더까지)");
    }

    private static void StoreSafety()
    {
        string folder = Folder("store-safety");
        var store = new MemoStore(folder);
        var memo = new MemoData { Rtf = RtfText.FromPlain("첫 내용"), Text = "첫 내용" };
        store.Save(memo);
        memo.Text = "둘째 내용";
        store.Save(memo);
        string path = Path.Combine(folder, "memos", memo.Id.ToString("N") + ".json");
        Assert(File.ReadAllText(path + ".bak").Contains("첫 내용") && File.ReadAllText(path).Contains("둘째 내용"), "저장할 때 바로 전 저장본을 .bak 으로");

        // 깨진 파일 → .bak 으로 되살림
        File.WriteAllText(path, "{\"id\": \"" + memo.Id + "\", \"text\": \"잘린");
        var recovered = new MemoStore(folder);
        Equal("첫 내용", recovered.Find(memo.Id)?.Text, "깨진 파일은 바로 전 저장본으로");
        Assert(recovered.Recovered.Count == 1 && recovered.Damaged.Count == 0, "되살린 것으로 알림");
        Assert(new MemoStore(folder).Find(memo.Id) is not null, "되살린 내용을 다시 써 둠");

        // 둘 다 깨짐 → 읽지 못한 메모 폴더로 (조용히 사라지지 않음)
        File.WriteAllText(path, "깨짐");
        File.WriteAllText(path + ".bak", "깨짐");
        var damaged = new MemoStore(folder);
        Assert(damaged.Find(memo.Id) is null && damaged.Damaged.Count == 1, "읽지 못한 것으로 알림");
        Assert(!File.Exists(path) && Directory.GetFiles(damaged.DamagedFolderPath, "*.json").Length == 1, "파일은 읽지 못한 메모 폴더로 옮김 (지우지 않음)");
        Assert(new MemoStore(folder).Damaged.Count == 0, "옮긴 뒤에는 다시 알리지 않음");

        // 잠깐 잠긴 파일 → 건드리지 않고 이번만 건너뜀
        var other = new MemoData { Text = "잠긴 메모" };
        store = new MemoStore(folder);
        store.Save(other);
        string otherPath = Path.Combine(folder, "memos", other.Id.ToString("N") + ".json");
        using (new FileStream(otherPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = new MemoStore(folder);
            Assert(locked.Find(other.Id) is null && locked.Damaged.Count == 0 && File.Exists(otherPath), "잠긴 파일은 옮기지 않음");

            // 저장 실패 → 고친 내용을 잊지 않고 다시 시도
            int failed = 0, back = 0;
            store.SaveFailed += _ => failed++;
            store.SaveRecovered += () => back++;
            other.Text = "잠긴 동안 고침";
            Assert(!store.Save(other) && store.UnsavedCount == 1 && failed == 1, "저장 못 함을 알림 (한 번)");
            other.Text = "잠긴 동안 또 고침";
            store.Save(other);
            Equal(1, failed, "실패 알림은 한 번만");
            store.RetryUnsaved();
            Equal(1, store.UnsavedCount, "잠겨 있는 동안은 그대로 기다림");
            Assert(back == 0, "아직 저장 안 됨");

            // 잠금이 풀린 뒤 (using 끝) 다시 시도
            store.SaveRecovered += () => { };
        }
        store.RetryUnsaved();
        Equal(0, store.UnsavedCount, "풀리면 저장");
        Equal("잠긴 동안 또 고침", new MemoStore(folder).Find(other.Id)?.Text, "마지막으로 고친 내용이 저장됨");

        // 지우면 .bak 도
        store.Delete(other);
        Assert(!File.Exists(otherPath) && !File.Exists(otherPath + ".bak"), "지우면 .bak 도 지움");

        // 하루 백업: 메모 파일이 들어가고, 최근 7개만 남음
        store.Save(new MemoData { Text = "백업할 메모" });
        string? zipPath = store.BackupDaily(new DateTime(2026, 1, 1));
        using (var zip = System.IO.Compression.ZipFile.OpenRead(zipPath!))
            Assert(zip.Entries.Any(e => e.FullName.StartsWith("memos/") && e.FullName.EndsWith(".json")), "백업에 메모 파일");
        for (int day = 2; day <= 10; day++) store.BackupDaily(new DateTime(2026, 1, day));
        var kept = Directory.GetFiles(store.BackupFolderPath, "MemoPad-*.zip").Select(Path.GetFileName).OrderBy(n => n).ToList();
        Equal(MemoStore.BackupsKept, kept.Count, "백업은 최근 7개");
        Equal("MemoPad-2026-01-04.zip", kept[0], "오래된 것부터 지움");
        Assert(DataLocation.Items.Contains("backup"), "데이터 폴더를 옮기면 백업도 함께");
    }

    private static void Hmd()
    {
        var data = new byte[0x300];
        System.Text.Encoding.ASCII.GetBytes("SAMSUNGMEMO_1.2\x1a").CopyTo(data, 0);
        BitConverter.GetBytes(1).CopyTo(data, 0x11);
        var id = Guid.NewGuid();
        id.ToByteArray().CopyTo(data, 0x15);
        BitConverter.GetBytes(100).CopyTo(data, 0x29);
        BitConverter.GetBytes(200).CopyTo(data, 0x2D);
        BitConverter.GetBytes(300).CopyTo(data, 0x31);
        BitConverter.GetBytes(150).CopyTo(data, 0x35);
        BitConverter.GetBytes(3).CopyTo(data, 0x39);
        BitConverter.GetBytes(new DateTime(2026, 1, 2).ToOADate()).CopyTo(data, 0x49);
        System.Text.Encoding.Unicode.GetBytes("10.0.0.9").CopyTo(data, 0x61);
        System.Text.Encoding.Unicode.GetBytes("kim").CopyTo(data, 0x161);
        System.Text.Encoding.Unicode.GetBytes("김철수").CopyTo(data, 0x1E1);
        var rtf = System.Text.Encoding.ASCII.GetBytes("{\\rtf1\\ansi hello\\par}\0");
        data = [.. data, .. rtf];
        var memo = JungumImport.Read(data, "me")!;
        Equal(id, memo.Id, "id");
        Equal((100, 200, 300, 150), (memo.X, memo.Y, memo.Width, memo.Height), "자리");
        Equal(MemoColors.Jungum[3], memo.Color, "색");
        Equal(new DateTime(2026, 1, 2), memo.Created, "만든 날");
        Equal("김철수", memo.From?.Name, "보낸 사람");
        Equal(MemoOrigin.Received, memo.Origin, "받은 메모");
        Assert(memo.Rtf.StartsWith("{\\rtf1"), "본문");
        Assert(JungumImport.Read(new byte[0x300]) is null, "다른 파일은 무시");
    }

    private static void ChatPayloads()
    {
        var me = new Person { Ip = "10.0.0.3", Name = "홍길동", Email = "a@b.cd" };
        var info = ChatProtocol.ParseInfo(ChatProtocol.InfoPayload("chatty", me));
        Equal(("chatty", "10.0.0.3", "홍길동", "a@b.cd"), (info.ChatName, info.Person.Ip, info.Person.Name, info.Person.Email), "내 정보");
        // 정음 메모패드가 보낸 내 정보 크기: 글자 4개 + 0 두 개 (기록과 같은 90바이트)
        Equal(90, ChatProtocol.InfoPayload("chatty", new Person { Ip = "172.16.10.21", Name = "홍길동AB", Email = "a@b.cd" }).Length, "내 정보 길이");
        var member = ChatProtocol.ParseMember(ChatProtocol.MemberPayload(info));
        Equal("chatty", member.ChatName, "참여자");
        int at = 0;
        var payload = ChatProtocol.BroadcastPayload("10.0.0.3", "chatty: 안녕 hi");
        Equal("10.0.0.3", ChatProtocol.ReadText(payload, ref at), "돌려주기 IP");
        Equal("chatty: 안녕 hi", ChatProtocol.ReadText(payload, ref at), "돌려주기 글");
    }
}
