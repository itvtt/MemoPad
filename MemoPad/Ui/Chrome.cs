using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using MemoPad.Core;

namespace MemoPad.Ui;

/// <summary>
/// 어두운 창의 직접 만든 제목 표시줄: 끌기 · 두 번 눌러 최대화 · 가장자리 크기 조절 · 최소화 · 최대화 · 닫기,
/// 최대화해도 작업 표시줄이 보이게 (WM_GETMINMAXINFO)
/// </summary>
internal static class Chrome
{
    public const double TitleHeight = 44;

    public static void Apply(Window window, bool resizable)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        window.Background = (Brush)Application.Current.Resources["WindowBrush"];
        window.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        window.FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        window.FontSize = 13;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        window.UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = TitleHeight,
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(resizable ? 6 : 0),
            UseAeroCaptionButtons = false
        });
        window.SourceInitialized += (_, _) =>
        {
            Native.RoundCorners(window, Color.FromRgb(0x3A, 0x3B, 0x42));
            if (resizable) HwndSource.FromHwnd(Native.Handle(window))?.AddHook(MaximizeHook);
        };
    }

    /// <summary>제목 표시줄 (아이콘 글자 · 제목 · 오른쪽 단추). extra 는 닫기 왼쪽에 들어갈 단추들</summary>
    public static Grid TitleBar(Window window, string title, string glyph, bool minMax, params UIElement[] extra)
    {
        var bar = new Grid { Height = TitleHeight, Background = (Brush)Application.Current.Resources["TitleBrush"] };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (glyph.Length > 0)
            left.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons.Font, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE3)), Margin = new Thickness(0, 0, 9, 0) });
        var titleText = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(titleText);
        bar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 5, 0) };
        foreach (var element in extra)
        {
            WindowChrome.SetIsHitTestVisibleInChrome(element, true);
            right.Children.Add(element);
        }
        if (minMax)
        {
            right.Children.Add(ChromeButton(window, "", "최소화", () => window.WindowState = WindowState.Minimized, false));
            var max = ChromeButton(window, "", "최대화", () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized, false);
            window.StateChanged += (_, _) =>
            {
                ((TextBlock)max.Content).Text = window.WindowState == WindowState.Maximized ? "" : "";
                max.ToolTip = window.WindowState == WindowState.Maximized ? "이전 크기로" : "최대화";
            };
            right.Children.Add(max);
        }
        right.Children.Add(ChromeButton(window, "", "닫기", window.Close, true));
        bar.Children.Add(right);
        bar.Tag = titleText;
        return bar;
    }

    public static void SetTitle(Grid bar, string title)
    {
        if (bar.Tag is TextBlock text) text.Text = title;
    }

    private static Button ChromeButton(Window window, string glyph, string tip, Action action, bool close)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources[close ? "CloseChromeButtonStyle" : "ChromeButtonStyle"],
            Content = new TextBlock { Text = glyph, FontFamily = Icons.Font, FontSize = 8 },
            ToolTip = tip
        };
        button.Click += (_, _) => action();
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    /// <summary>둥근 아이콘 단추 (제목 표시줄 · 도구 막대)</summary>
    public static Button IconButton(string glyph, string tip, Action action, double size = 12)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["IconButtonStyle"],
            Content = new TextBlock { Text = glyph, FontFamily = Icons.Font, FontSize = size },
            ToolTip = tip
        };
        button.Click += (_, _) => action();
        return button;
    }

    public static Button Pill(string text, Action action, bool primary = false)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources[primary ? "PrimaryPillButtonStyle" : "PillButtonStyle"],
            Content = text,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 72
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static IntPtr MaximizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = Native.MonitorFromWindow(hwnd, 2);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        var mmi = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }
}

/// <summary>Segoe Fluent Icons (Windows 11) · Segoe MDL2 Assets (Windows 10) 글자</summary>
internal static class Icons
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    /// <summary>열린 자물쇠 (삭제 잠금 꺼짐) · 네 방향 화살표 (자리 고정 상태)</summary>
    public const string Unlock = "", Move = "";
    public const string Menu = "", Pin = "", Pinned = "", Minimize = "", Close = "", Lock = "",
        Clock = "", Attach = "", ReadOnly = "", Send = "", Reply = "", Mail = "", Add = "",
        Delete = "", Edit = "", Search = "", Folder = "", People = "", Chat = "", Settings = "",
        Pen = "", Eraser = "", Undo = "", Redo = "", ZoomIn = "", ZoomOut = "", Restore = "",
        Refresh = "", Up = "", Down = "", Save = "", Status = "", Grid = "", Hide = "",
        Show = "", Note = "", Circulate = "", Forward = "", Picture = "", Expand = "";
}
