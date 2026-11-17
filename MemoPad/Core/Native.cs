using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MemoPad.Core;

public static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_APPWINDOW = 0x40000;
    public const int LWA_ALPHA = 0x2;
    public const int WM_WINDOWPOSCHANGING = 0x46, WM_GETMINMAXINFO = 0x24, WM_EXITSIZEMOVE = 0x232, WM_NCLBUTTONDBLCLK = 0xA3;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2), HWND_BOTTOM = new(1), HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, int flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT point, int flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool FlashWindow(IntPtr hWnd, bool invert);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern bool PlaySound(string? sound, IntPtr module, int flags);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(int action, int param, ref bool value, int winIni);

    public const int SND_ASYNC = 0x1, SND_FILENAME = 0x20000, SND_ALIAS = 0x10000, SND_NODEFAULT = 0x2, SND_LOOP = 0x8;

    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();

    public static RECT WindowRect(Window window)
    {
        GetWindowRect(Handle(window), out var rect);
        return rect;
    }

    /// <summary>창이 있는 모니터 (없으면 마우스 · 주 모니터)의 작업 영역 (실제 화소)</summary>
    public static RECT WorkArea(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        return info.rcWork;
    }

    public static RECT WorkAreaAt(int x, int y) => WorkArea(MonitorFromPoint(new POINT { X = x, Y = y }, 2));

    public static RECT PrimaryWorkArea() => WorkArea(MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1));

    public static void AddExStyle(Window window, int add, int remove = 0)
    {
        var handle = Handle(window);
        long style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr((style | (uint)add) & ~(long)(uint)remove));
    }

    /// <summary>창 전체 투명도 (0.1 ~ 1). 1 이면 겹치기를 끔</summary>
    public static void SetOpacity(Window window, double opacity)
    {
        var handle = Handle(window);
        long style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        if (opacity >= 0.999)
        {
            if ((style & WS_EX_LAYERED) != 0) SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style & ~(long)WS_EX_LAYERED));
            return;
        }
        if ((style & WS_EX_LAYERED) == 0) SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_LAYERED));
        SetLayeredWindowAttributes(handle, 0, (byte)Math.Clamp((int)Math.Round(opacity * 255), 25, 255), LWA_ALPHA);
    }

    /// <summary>Windows 11 둥근 모서리 + 테두리 색 (안 되는 Windows 는 그대로)</summary>
    public static void RoundCorners(Window window, System.Windows.Media.Color? border = null, bool small = false)
    {
        var handle = Handle(window);
        int round = small ? 3 : 2;
        DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        SetBorderColor(window, border);
    }

    /// <summary>테두리 색 (null = 테두리 없음)</summary>
    public static void SetBorderColor(Window window, System.Windows.Media.Color? border)
    {
        int value = border is { } c ? c.R | (c.G << 8) | (c.B << 16) : -2;
        DwmSetWindowAttribute(Handle(window), 34, ref value, sizeof(int));
    }

    public static bool ScreenSaverRunning()
    {
        bool running = false;
        SystemParametersInfo(0x72, 0, ref running, 0);
        return running;
    }
}
