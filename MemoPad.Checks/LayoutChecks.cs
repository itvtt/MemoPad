using System.Net;
using MemoPad.Core;
using MemoPad.Ui;

/// <summary>메모 창 자리: 접기(사각형 + 제목) · 옮겨서 펼치기 · 정렬 · 제목. 데이터 폴더 옮기기</summary>
static partial class Program
{
    private static void DataFolder()
    {
        Assert(!DataLocation.NeedsChoice, "시험 폴더(MEMOPAD_DATA_DIR)면 묻지 않음");
        string from = Folder("data-from"), to = System.IO.Path.Combine(Folder("data-parent"), "새 폴더");
        System.IO.File.WriteAllText(System.IO.Path.Combine(from, "settings.json"), "{}");
        System.IO.File.WriteAllText(System.IO.Path.Combine(from, "folders.json"), "[]");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(from, "memos", "abc", "files"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(from, "memos", "abc.json"), "{}");
        System.IO.File.WriteAllText(System.IO.Path.Combine(from, "memos", "abc", "files", "첨부.txt"), "첨부");
        System.IO.File.WriteAllText(System.IO.Path.Combine(from, "남의 파일.txt"), "그대로");

        Assert(DataLocation.HasData(from) && !DataLocation.HasData(to), "MemoPad 데이터가 있는 폴더 알아보기");
        Equal(null, DataLocation.CheckWritable(to), "새 폴더 만들고 써 보기");
        Assert(DataLocation.CheckWritable(@"상대\경로") is not null, "전체 경로만");
        string? missing = DataLocation.CheckWritable(@"Q:\없는 드라이브\MemoPad");
        Assert(missing is not null && missing.Contains("찾을 수 없습니다"), "없는 드라이브는 한국어로: " + missing);
        Assert(DataLocation.IsInside(System.IO.Path.Combine(from, "memos"), from) && !DataLocation.IsInside(to, from), "안쪽 폴더 알아보기");
        Assert(DataLocation.Same(from + "\\", from.ToUpperInvariant()), "같은 폴더 (대소문자 · 끝의 \\)");

        DataLocation.Move(from, to);
        Equal("첨부", System.IO.File.ReadAllText(System.IO.Path.Combine(to, "memos", "abc", "files", "첨부.txt")), "첨부까지 옮김");
        Assert(System.IO.File.Exists(System.IO.Path.Combine(to, "settings.json")) && System.IO.File.Exists(System.IO.Path.Combine(to, "memos", "abc.json")), "설정 · 메모 옮김");
        Assert(!DataLocation.HasData(from), "옛 폴더의 MemoPad 데이터는 지움");
        Assert(System.IO.File.Exists(System.IO.Path.Combine(from, "남의 파일.txt")) && !System.IO.File.Exists(System.IO.Path.Combine(to, "남의 파일.txt")), "다른 파일은 그대로 (옮기지도 지우지도 않음)");
    }

    private static async Task CollapseSquare()
    {
        string folder = Folder("desk-collapse");
        var desk = new Desk(new AppSettings { Name = "접기", UseNetwork = false, StartupConfigured = true, Sound = false, AddressBookFolder = folder },
            new MemoStore(folder), IPAddress.Parse("127.0.0.12"));
        desk.Start();
        try
        {
            var window = desk.NewMemo();
            window.ReplaceRtf(RtfText.FromPlain("접기 시험 첫 줄\n둘째 줄"));
            window.MoveTo(400, 300);
            await Task.Delay(150);
            var open = window.PixelBounds;
            int width = open.Right - open.Left, height = open.Bottom - open.Top;

            window.ToggleCollapse();
            await Task.Delay(150);
            var tab = window.PixelBounds;
            int side = tab.Bottom - tab.Top, tabWidth = tab.Right - tab.Left;
            Assert(side is >= 22 and <= 64, "접으면 낮은 한 줄: " + side);
            Equal("접기 시험 첫 줄", window.FaceText.Text, "접은 메모에 본문 첫 줄");
            Assert(tabWidth > side * 2 && tabWidth <= width, $"글자만큼 넓고 펼친 메모보다 넓지 않음: {tabWidth} (높이 {side}, 메모 {width})");
            Equal((open.Right, open.Top), (tab.Right, tab.Top), "접은 메모는 메모의 오른쪽 위");

            // 접은 메모를 끌어 옮긴 것처럼 → 펼치면 그 자리를 오른쪽 위로 해서 원래 크기로
            Native.SetWindowPos(Native.Handle(window), IntPtr.Zero, 900, 350, tabWidth, side, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            window.ToggleCollapse();
            await Task.Delay(150);
            var again = window.PixelBounds;
            Equal((900 + tabWidth, 350), (again.Right, again.Top), "옮긴 자리에서 펼침");
            Equal((width, height), (again.Right - again.Left, again.Bottom - again.Top), "펼친 크기 그대로");

            // 정렬: 접은 메모가 정렬 자리에
            window.ToggleCollapse();
            window.MoveTo(500, 200);
            await Task.Delay(150);
            var moved = window.PixelBounds;
            Equal((500, 200, tabWidth), (moved.Left, moved.Top, moved.Right - moved.Left), "정렬 자리에 접은 메모");

            // 다시 켜도 접힌 채로 같은 자리 (저장된 자리는 펼친 메모 기준)
            var memo = window.Memo;
            window.Close();
            var reopened = desk.Open(memo, activate: false);
            await Task.Delay(150);
            var restored = reopened.PixelBounds;
            Equal((moved.Left, moved.Top, tabWidth), (restored.Left, restored.Top, restored.Right - restored.Left), "다시 열어도 같은 자리 · 너비");

            // 글이 없는 메모 · 잠긴 보안 메모는 사각형만
            var empty = desk.NewMemo();
            empty.ToggleCollapse();
            await Task.Delay(150);
            var square = empty.PixelBounds;
            Equal(square.Right - square.Left, square.Bottom - square.Top, "빈 메모는 사각형만");
            reopened.Memo.Secured = true;
            reopened.ApplyLook();
            await Task.Delay(150);
            var secured = reopened.PixelBounds;
            Equal((secured.Bottom - secured.Top, restored.Right), (secured.Right - secured.Left, secured.Right), "잠긴 보안 메모는 사각형만 (오른쪽 위 그대로)");
        }
        finally
        {
            foreach (var window in desk.Windows.ToList()) window.Close();
            desk.Shutdown();
        }
    }

    /// <summary>메모 제목: 머리에 보임 · 접은 메모에 보임 · 저장 · 입력 칸(Enter · Esc · 날짜 그대로) · 비우면 제목 없음</summary>
    private static async Task MemoTitle()
    {
        string folder = Folder("desk-title");
        var desk = new Desk(new AppSettings { Name = "제목", UseNetwork = false, StartupConfigured = true, Sound = false, AddressBookFolder = folder },
            new MemoStore(folder), IPAddress.Parse("127.0.0.15"));
        desk.Start();
        try
        {
            var window = desk.NewMemo();
            window.ReplaceRtf(RtfText.FromPlain("본문 첫 줄\n둘째 줄"));
            await Task.Delay(100);
            string dated = window.TitleText.Text;
            Assert(dated.Contains('월') && window.Memo.Title.Length == 0, "처음엔 제목 없음 (머리에 날짜): " + dated);

            window.SetTitle("  회의록  ");
            Equal(("회의록", "회의록"), (window.Memo.Title, window.TitleText.Text), "제목 붙이기 (앞뒤 빈칸 뺌) · 머리에 보임");
            Equal("회의록", new MemoStore(folder).Find(window.Memo.Id)?.Title, "제목 저장");

            window.ToggleCollapse();
            await Task.Delay(150);
            Equal("회의록", window.FaceText.Text, "접으면 제목이 보임 (본문 첫 줄 대신)");
            int titledWidth = window.PixelBounds.Right - window.PixelBounds.Left;

            // 입력 칸: 접혀 있으면 펼쳐서 · Enter 저장
            window.BeginTitleEdit();
            Assert(!window.Memo.Collapsed && window.EditingTitle, "제목 바꾸기는 펼쳐서 입력 칸");
            Equal("회의록", window.TitleEditor.Text, "입력 칸에 지금 제목");
            window.TitleEditor.Text = "3층 회의 준비 자료 목록";
            window.EndTitleEdit(commit: true);
            Equal(("3층 회의 준비 자료 목록", false), (window.Memo.Title, window.EditingTitle), "Enter = 저장, 입력 칸 닫힘");

            window.ToggleCollapse();
            await Task.Delay(150);
            int longerWidth = window.PixelBounds.Right - window.PixelBounds.Left;
            Assert(longerWidth > titledWidth, $"긴 제목은 접은 메모도 넓게: {titledWidth} → {longerWidth}");
            double scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            Assert(longerWidth <= Math.Round(MemoWindow.CollapsedMaxWidth * scale), $"그래도 최대 너비까지만 (한글 8자쯤, 나머지는 …): {longerWidth}");
            window.ToggleCollapse();

            // Esc = 그대로
            window.BeginTitleEdit();
            window.TitleEditor.Text = "버릴 제목";
            window.EndTitleEdit(commit: false);
            Equal("3층 회의 준비 자료 목록", window.Memo.Title, "Esc = 그대로");

            // 비우면 제목 없음 → 머리에 날짜, 접으면 본문 첫 줄
            window.BeginTitleEdit();
            window.TitleEditor.Text = " ";
            window.EndTitleEdit(commit: true);
            Equal(("", dated), (window.Memo.Title, window.TitleText.Text), "비우면 제목 없음 (머리에 날짜)");
            window.ToggleCollapse();
            await Task.Delay(100);
            Equal("본문 첫 줄", window.FaceText.Text, "제목이 없으면 접은 메모에 본문 첫 줄");
            window.ToggleCollapse();

            // 제목이 없던 메모: 미리 넣은 날짜를 그대로 저장하면 제목 없음 그대로
            window.BeginTitleEdit();
            Equal(dated, window.TitleEditor.Text, "제목 없는 메모의 입력 칸엔 날짜");
            window.EndTitleEdit(commit: true);
            Equal("", window.Memo.Title, "날짜 그대로 두면 제목 없음 그대로");

            Equal("회의록", new MemoData { Title = "회의록", Text = "본문" }.Label, "목록 · 파일 이름 = 제목");
            Equal("본문", new MemoData { Text = "본문" }.Label, "제목이 없으면 본문 첫 줄");
        }
        finally
        {
            foreach (var window in desk.Windows.ToList()) window.Close();
            desk.Shutdown();
        }
    }

    /// <summary>삭제 잠금: 머리 자물쇠 단추 · ✕ 꺼짐 · 버리기 안 됨 · 저장 · 풀면 버려짐</summary>
    private static async Task DeleteLock()
    {
        string folder = Folder("desk-deletelock");
        var desk = new Desk(new AppSettings { Name = "삭제 잠금", UseNetwork = false, StartupConfigured = true, Sound = false, AddressBookFolder = folder },
            new MemoStore(folder), IPAddress.Parse("127.0.0.17"));
        desk.Start();
        try
        {
            var window = desk.NewMemo();
            window.ReplaceRtf(RtfText.FromPlain("지우면 안 되는 메모"));
            await Task.Delay(100);
            Equal((Icons.Unlock, true), ((string)window.DeleteLockButton.Content, window.CloseButton.IsEnabled), "처음엔 열린 자물쇠 · ✕ 켜짐");

            window.ToggleDeleteLock();
            Equal((true, Icons.Lock, false), (window.Memo.DeleteLocked, (string)window.DeleteLockButton.Content, window.CloseButton.IsEnabled), "잠그면 닫힌 자물쇠 · ✕ 꺼짐");
            Assert(new MemoStore(folder).Find(window.Memo.Id)?.DeleteLocked == true, "삭제 잠금 저장");

            desk.Discard(window);
            await Task.Delay(100);
            Assert(window.IsVisible && !window.Memo.Deleted && desk.WindowOf(window.Memo) is not null, "잠긴 메모는 버리기 (✕ · Alt+F4 · 메뉴) 안 됨");

            window.ToggleDeleteLock();
            Equal((false, true), (window.Memo.DeleteLocked, window.CloseButton.IsEnabled), "풀면 ✕ 다시 켜짐");
            var memo = window.Memo;
            desk.Discard(window);
            await Task.Delay(100);
            Assert(memo.Deleted && desk.WindowOf(memo) is null, "풀면 버려짐 (휴지통)");
        }
        finally
        {
            foreach (var window in desk.Windows.ToList()) window.Close();
            desk.Shutdown();
        }
    }

    /// <summary>새 메모 기본은 항상 위 (처음 설치 = 기본 설정), 설정에서 끄면 보통</summary>
    private static async Task DefaultOnTop()
    {
        string folder = Folder("desk-top");
        var settings = new AppSettings { Name = "항상 위", UseNetwork = false, StartupConfigured = true, Sound = false, AddressBookFolder = folder };
        Assert(settings.DefaultTop && settings.ReceivedOnTop, "기본 설정: 새 메모 · 받은 메모 항상 위");
        var desk = new Desk(settings, new MemoStore(folder), IPAddress.Parse("127.0.0.14"));
        desk.Start();
        try
        {
            var top = desk.NewMemo();
            await Task.Delay(100);
            Equal((MemoLayer.Top, true), (top.Memo.Layer, top.Topmost), "새 메모는 항상 위");
            settings.DefaultTop = false;
            var normal = desk.NewMemo();
            await Task.Delay(100);
            Equal((MemoLayer.Normal, false), (normal.Memo.Layer, normal.Topmost), "설정에서 끄면 보통");
        }
        finally
        {
            foreach (var window in desk.Windows.ToList()) window.Close();
            desk.Shutdown();
        }
    }

    /// <summary>메모마다 글꼴 · 글자 크기: 본문 전체 · 저장 · 다시 열어도 그대로 · 다른 메모는 그대로 · 단계 · 기본으로 · 읽기 전용은 못 바꿈</summary>
    private static async Task FontPerMemo()
    {
        string folder = Folder("desk-font");
        var desk = new Desk(new AppSettings { Name = "글꼴", UseNetwork = false, StartupConfigured = true, Sound = false, AddressBookFolder = folder },
            new MemoStore(folder), IPAddress.Parse("127.0.0.13"));
        desk.Start();
        try
        {
            var window = desk.NewMemo();
            window.ReplaceRtf(RtfText.FromPlain("글꼴 시험 첫 줄\n둘째 줄"));
            var other = desk.NewMemo();
            other.ReplaceRtf(RtfText.FromPlain("다른 메모"));

            window.SetFont("Gulim", 18 * 96.0 / 72);
            window.SaveNow();
            Equal(("Gulim", 24.0), (window.Memo.FontFamily, window.Memo.FontSize), "메모에 글꼴 · 크기");
            Assert(window.Memo.Rtf.Contains("Gulim") && window.Memo.Rtf.Contains(@"\fs36"), "본문 전체가 굴림 18pt (RTF)");
            Assert(other.Memo.FontFamily.Length == 0 && !other.Memo.Rtf.Contains("Gulim"), "다른 메모는 그대로");

            // 다시 열어도 그대로이고, 새로 쓰는 글자도 그 글꼴
            var memo = window.Memo;
            window.Close();
            var reopened = desk.Open(memo, activate: false);
            await Task.Delay(100);
            Equal(("Gulim", 24.0), (reopened.EffectiveFontFamily, reopened.EffectiveFontSize), "다시 열어도 그대로");
            Assert(reopened.Editor.Document.FontFamily.Source.StartsWith("Gulim", StringComparison.Ordinal), "새로 쓰는 글자도 굴림");

            // 한 단계씩 (Ctrl+휠): 18 → 20 → 18 → 16
            reopened.StepFontSize(1);
            Equal(20.0, Math.Round(reopened.Memo.FontSize * 72 / 96, 2), "크게 한 단계");
            reopened.StepFontSize(-1);
            reopened.StepFontSize(-1);
            Equal(16.0, Math.Round(reopened.Memo.FontSize * 72 / 96, 2), "작게 두 단계");

            // 기본으로 되돌림
            reopened.SetFont("", 0);
            reopened.SaveNow();
            Equal(("", 0.0), (reopened.Memo.FontFamily, reopened.Memo.FontSize), "기본으로");
            Assert(!reopened.Memo.Rtf.Contains("Gulim"), "본문도 기본 글꼴로");

            // 읽기 전용이면 바꾸지 않음
            reopened.Memo.ReadOnly = true;
            reopened.SetFont("Batang", 40);
            Equal("", reopened.Memo.FontFamily, "읽기 전용은 그대로");
        }
        finally
        {
            foreach (var window in desk.Windows.ToList()) window.Close();
            desk.Shutdown();
        }
    }
}
