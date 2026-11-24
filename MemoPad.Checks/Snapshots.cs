using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MemoPad.Core;
using MemoPad.Ui;

/// <summary>창들을 PNG 로 (모양 확인용): --snapshot &lt;폴더&gt;</summary>
static class Snapshots
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var desk = Program.NewDesk("스냅샷", "127.0.0.8");
        try
        {
            var memo = desk.NewMemo();
            memo.ReplaceRtf(RtfText.FromPlain("회의 2시 3층\n자료 준비하기\n안녕하세요"));
            memo.Memo.Alarm = new Alarm { At = DateTime.Today.AddDays(1).AddHours(9) };
            memo.Memo.StatusBar = true;
            memo.ApplyLook();
            Save(memo, Path.Combine(folder, "memo.png"));
            var drawing = desk.NewMemo(MemoKind.Drawing);
            Save(drawing, Path.Combine(folder, "drawing.png"));
            // 제목 붙인 메모 · 제목 고치는 중
            var titled = desk.NewMemo();
            titled.ReplaceRtf(RtfText.FromPlain("월요일 10시\n지난주 실적 정리"));
            titled.SetTitle("주간 회의");
            Save(titled, Path.Combine(folder, "memo-title.png"));
            titled.ToggleDeleteLock();
            Save(titled, Path.Combine(folder, "memo-delete-locked.png"));
            titled.ToggleDeleteLock();
            titled.BeginTitleEdit();
            Save(titled, Path.Combine(folder, "memo-title-edit.png"));
            titled.EndTitleEdit(commit: false);
            // 접은 메모 (사각형 + 제목 · 본문 첫 줄): 글 · 제목 · 그림 · 보안, 4배로
            memo.ToggleCollapse();
            Save(memo, Path.Combine(folder, "collapsed.png"), zoom: 4);
            titled.ToggleCollapse();
            Save(titled, Path.Combine(folder, "collapsed-title.png"), zoom: 4);
            titled.SetTitle("다음 주 월요일 주간 회의 자료 준비");
            Save(titled, Path.Combine(folder, "collapsed-long-title.png"), zoom: 4);
            drawing.ToggleCollapse();
            Save(drawing, Path.Combine(folder, "collapsed-drawing.png"), zoom: 4);
            memo.Memo.Secured = true;
            memo.ApplyLook();
            Save(memo, Path.Combine(folder, "collapsed-secured.png"), zoom: 4);
            desk.ShowManager();
            Save(Application.Current.Windows.OfType<Window>().First(w => w.Title.StartsWith("메모 관리기")), Path.Combine(folder, "manager.png"));
            desk.ShowSettings();
            var settings = Application.Current.Windows.OfType<Window>().First(w => w.Title.StartsWith("설정"));
            Save(settings, Path.Combine(folder, "settings.png"));
            // 아래쪽 (단축키 · 보안): 하나는 정한 키, 하나는 없음
            desk.Hotkeys.Set(HotkeyAction.HideAll, new HotkeyBinding(HotkeyBinding.MOD_CONTROL | HotkeyBinding.MOD_ALT, 0x48));
            settings.Close();
            desk.ShowSettings();
            settings = Application.Current.Windows.OfType<Window>().First(w => w.Title.StartsWith("설정"));
            settings.Show();
            settings.UpdateLayout();
            FindScroll(settings)?.ScrollToEnd();
            Save(settings, Path.Combine(folder, "settings-end.png"));
            desk.Hotkeys.Set(HotkeyAction.HideAll, default);
            desk.ShowAddressBook();
            Save(Application.Current.Windows.OfType<Window>().First(w => w.Title.StartsWith("주소록")), Path.Combine(folder, "addressbook.png"));
            // 글꼴 · 글자 크기 (메모마다): 궁서 16pt 로 바꾼 메모, 메뉴 → 글꼴 · 글자 크기
            var fontMemo = desk.NewMemo();
            fontMemo.ReplaceRtf(RtfText.FromPlain("궁서 16pt 로 바꾼 메모\n다른 메모는 기본 글꼴 그대로"));
            fontMemo.SetFont("Gungsuh", 16 * 96.0 / 72);
            Save(fontMemo, Path.Combine(folder, "memo-font.png"));
            foreach (var (header, file) in new[] { ("글꼴", "menu-font"), ("글자 크기", "menu-size") })
            {
                var menu = fontMemo.OpenMemoMenu(fontMemo.Editor);
                Program.Run(() => Task.Delay(300), 5);
                menu.Items.OfType<System.Windows.Controls.MenuItem>().First(item => item.Header as string == header).IsSubmenuOpen = true;
                Program.Run(() => Task.Delay(500), 5);
                SavePopups(Path.Combine(folder, file));
                menu.IsOpen = false;
                Program.Run(() => Task.Delay(200), 5);
            }
            Console.WriteLine($"PNG -> {folder}");
            return 0;
        }
        finally
        {
            foreach (var window in Application.Current.Windows.OfType<Window>().ToList()) window.Close();
            desk.Shutdown();
        }
    }

    private static System.Windows.Controls.ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>열린 메뉴(팝업)마다 PNG: 경로-1.png, 경로-2.png …</summary>
    private static void SavePopups(string path)
    {
        int n = 0;
        foreach (var source in PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>().ToList())
        {
            if (source.RootVisual is not FrameworkElement root || !root.IsVisible || root.GetType().Name != "PopupRoot") continue;
            double scale = VisualTreeHelper.GetDpi(root).DpiScaleX;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create($"{path}-{++n}.png");
            encoder.Save(stream);
        }
    }

    private static void Save(Window window, string path, double zoom = 1)
    {
        window.Show();
        window.UpdateLayout();
        Program.Run(() => Task.Delay(400), 5);
        var element = (FrameworkElement)window.Content;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX * zoom;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
