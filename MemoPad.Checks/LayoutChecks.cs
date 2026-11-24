using System.Net;
using MemoPad.Core;
using MemoPad.Ui;

/// <summary>메모 창 자리: 접기(작은 사각형) · 옮겨서 펼치기 · 정렬. 데이터 폴더 옮기기</summary>
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
            var square = window.PixelBounds;
            int side = square.Right - square.Left;
            Equal(side, square.Bottom - square.Top, "접으면 정사각형");
            Assert(side is >= 22 and <= 64, "작은 사각형: " + side);
            Equal((open.Right, open.Top), (square.Right, square.Top), "사각형은 메모의 오른쪽 위");

            // 사각형을 끌어 옮긴 것처럼 → 펼치면 그 자리를 오른쪽 위로 해서 원래 크기로
            Native.SetWindowPos(Native.Handle(window), IntPtr.Zero, 900, 350, side, side, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            window.ToggleCollapse();
            await Task.Delay(150);
            var again = window.PixelBounds;
            Equal((900 + side, 350), (again.Right, again.Top), "옮긴 사각형 자리에서 펼침");
            Equal((width, height), (again.Right - again.Left, again.Bottom - again.Top), "펼친 크기 그대로");

            // 정렬: 접은 메모는 사각형이 정렬 자리에
            window.ToggleCollapse();
            window.MoveTo(500, 200);
            await Task.Delay(150);
            var moved = window.PixelBounds;
            Equal((500, 200, side), (moved.Left, moved.Top, moved.Right - moved.Left), "정렬 자리에 사각형");

            // 다시 켜도 접힌 채로 같은 자리 (저장된 자리는 펼친 메모 기준)
            var memo = window.Memo;
            window.Close();
            var reopened = desk.Open(memo, activate: false);
            await Task.Delay(150);
            var restored = reopened.PixelBounds;
            Equal((moved.Left, moved.Top, side), (restored.Left, restored.Top, restored.Right - restored.Left), "다시 열어도 같은 사각형");
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
