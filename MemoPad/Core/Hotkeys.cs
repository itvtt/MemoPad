using System.Windows.Input;
using System.Windows.Interop;
using InputKey = System.Windows.Input.Key;

namespace MemoPad.Core;

/// <summary>전역 단축키로 하는 일</summary>
public enum HotkeyAction { ShowAll, HideAll }

/// <summary>단축키 하나: Windows 수정 키(MOD_*) + 가상 키. Key 가 0 이면 없음</summary>
public readonly record struct HotkeyBinding(uint Modifiers, uint Key)
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

    [System.Text.Json.Serialization.JsonIgnore] public bool IsNone => Key == 0;

    /// <summary>Ctrl+Alt+H · Win+F9 · 없음</summary>
    public override string ToString()
    {
        if (IsNone) return "없음";
        var parts = new List<string>();
        if ((Modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk) => vk switch
    {
        0x2C => "PrtSc",
        0x13 => "Pause",
        0x91 => "ScrollLock",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x08 => "Backspace",
        0x1B => "Esc",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        0x6A => "Num*",
        0x6B => "Num+",
        0x6D => "Num-",
        0x6E => "Num.",
        0x6F => "Num/",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => KeyInterop.KeyFromVirtualKey((int)vk).ToString()
    };

    /// <summary>
    /// 누른 키(조합)를 단축키로 (수정 키만 누른 것은 아직 아님 → null).
    /// Ctrl · Alt · Win 없이 글을 쓸 때 누르는 키면 warning 에 알려 줄 말 (그래도 단축키로 씀)
    /// </summary>
    public static HotkeyBinding? FromKey(InputKey key, ModifierKeys modifiers, out string? warning)
    {
        warning = null;
        if (key is InputKey.LeftCtrl or InputKey.RightCtrl or InputKey.LeftAlt or InputKey.RightAlt or InputKey.LeftShift or InputKey.RightShift or InputKey.LWin or InputKey.RWin or InputKey.None)
            return null;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return null;
        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
        var binding = new HotkeyBinding(mods, vk);
        bool typing = vk is 0x08 or 0x09 or 0x0D or 0x20 or (>= 0x25 and <= 0x28) or (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A)
            or (>= 0x60 and <= 0x6F) or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDE);
        if (typing && (mods & ~MOD_SHIFT) == 0)
            warning = $"{binding} 는 글을 쓸 때도 누르는 키라, MemoPad 가 켜져 있는 동안 다른 곳에서 입력되지 않습니다.";
        return binding;
    }
}

/// <summary>
/// 모두 보이기 · 모두 숨기기 전역 단축키. 처음에는 없음 (다른 프로그램과 겹치지 않게) → 설정에서 정함 (settings.json).
/// 다른 프로그램이 이미 쓰는 조합은 등록되지 않음 (Failed)
/// </summary>
public sealed class Hotkeys : IDisposable
{
    public static readonly HotkeyAction[] Actions = [HotkeyAction.ShowAll, HotkeyAction.HideAll];

    private const int WM_HOTKEY = 0x312;
    private const uint MOD_NOREPEAT = 0x4000;
    private readonly AppSettings _settings;
    private readonly HwndSource _window;
    private readonly Dictionary<HotkeyAction, HotkeyBinding> _bindings = [];
    private readonly HashSet<HotkeyAction> _registered = [];
    private bool _suspended, _disposed;

    public Hotkeys(AppSettings settings)
    {
        _settings = settings;
        foreach (var action in Actions)
            _bindings[action] = settings.Hotkeys?.TryGetValue(action.ToString(), out var saved) == true ? saved : default;
        // 메시지만 받는 창 (HWND_MESSAGE)
        _window = new HwndSource(new HwndSourceParameters("MemoPad hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(Hook);
    }

    public event Action<HotkeyAction>? Pressed;

    public static string Name(HotkeyAction action) => action == HotkeyAction.HideAll ? "모두 숨기기" : "모두 보이기";

    public HotkeyBinding Get(HotkeyAction action) => _bindings[action];

    /// <summary>메뉴에 붙일 글자 (없으면 "")</summary>
    public string Gesture(HotkeyAction action) => _bindings[action].IsNone ? "" : _bindings[action].ToString();

    /// <summary>정했지만 등록하지 못한 것 (다른 프로그램이 쓰는 중)</summary>
    public IReadOnlyList<HotkeyAction> Failed => [.. Actions.Where(a => !_bindings[a].IsNone && !_registered.Contains(a))];

    /// <summary>아직 등록하지 못한 것만 다시 시도</summary>
    public void Register()
    {
        if (_suspended || _disposed) return;
        foreach (var action in Actions)
        {
            var binding = _bindings[action];
            if (_registered.Contains(action) || binding.IsNone) continue;
            if (RegisterHotKey(_window.Handle, Id(action), binding.Modifiers | MOD_NOREPEAT, binding.Key)) _registered.Add(action);
        }
    }

    /// <summary>새 단축키를 누르는 동안: 모두 놓아 그 키를 MemoPad 가 가로채지 않게</summary>
    public void Suspend()
    {
        _suspended = true;
        foreach (var action in Actions) Unregister(action);
    }

    public void Resume()
    {
        _suspended = false;
        Register();
    }

    /// <summary>바꾸고 저장. 멈춰 둔 동안이면 Resume 때 등록</summary>
    public void Set(HotkeyAction action, HotkeyBinding binding)
    {
        Unregister(action);
        _bindings[action] = binding;
        _settings.Hotkeys = Actions.ToDictionary(a => a.ToString(), a => _bindings[a]);
        _settings.Save();
        Register();
    }

    private void Unregister(HotkeyAction action)
    {
        if (_registered.Remove(action)) UnregisterHotKey(_window.Handle, Id(action));
    }

    internal static int Id(HotkeyAction action) => Array.IndexOf(Actions, action) + 1;

    /// <summary>WM_HOTKEY 를 받는 창 (확인용)</summary>
    internal IntPtr Handle => _window.Handle;

    private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY && wParam.ToInt32() is var id && id >= 1 && id <= Actions.Length)
        {
            handled = true;
            Pressed?.Invoke(Actions[id - 1]);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var action in Actions) Unregister(action);
        _disposed = true;
        _window.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
