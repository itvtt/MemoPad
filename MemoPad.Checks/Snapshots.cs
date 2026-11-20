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
            // 접은 메모 (작은 사각형): 글 · 그림 · 보안, 8배로
            memo.ToggleCollapse();
            Save(memo, Path.Combine(folder, "collapsed.png"), zoom: 8);
            drawing.ToggleCollapse();
            Save(drawing, Path.Combine(folder, "collapsed-drawing.png"), zoom: 8);
            memo.Memo.Secured = true;
            memo.ApplyLook();
            Save(memo, Path.Combine(folder, "collapsed-secured.png"), zoom: 8);
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
