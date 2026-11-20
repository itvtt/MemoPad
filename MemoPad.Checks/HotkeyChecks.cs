using System.Net;
using System.Runtime.InteropServices;
using MemoPad.Core;
using MemoPad.Ui;

/// <summary>모두 보이기 · 모두 숨기기 전역 단축키, 메모 머리의 날짜</summary>
static partial class Program
{
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private static async Task HotkeysFlow()
    {
        string folder = Folder("desk-hotkeys");
        var desk = new Desk(new AppSettings { Name = "단축키", UseNetwork = false, StartupConfigured = true, Sound = false, AddressBookFolder = folder },
            new MemoStore(folder), IPAddress.Parse("127.0.0.13"));
        desk.Start();
        SettingsWindow? settingsWindow = null;
        try
        {
            const uint mods = HotkeyBinding.MOD_CONTROL | HotkeyBinding.MOD_ALT | HotkeyBinding.MOD_SHIFT;
            var hide = new HotkeyBinding(mods, 0x7A);   // F11
            var show = new HotkeyBinding(mods, 0x79);   // F10
            Equal("Ctrl+Alt+Shift+F11", hide.ToString(), "단축키 이름");
            Equal("없음", default(HotkeyBinding).ToString(), "없는 단축키");
            Assert(desk.Hotkeys.Get(HotkeyAction.ShowAll).IsNone && desk.Hotkeys.Get(HotkeyAction.HideAll).IsNone, "처음에는 단축키 없음");

            desk.Hotkeys.Set(HotkeyAction.HideAll, hide);
            desk.Hotkeys.Set(HotkeyAction.ShowAll, show);
            Equal(0, desk.Hotkeys.Failed.Count, "Windows 에 등록");
            Equal("Ctrl+Alt+Shift+F10", desk.Hotkeys.Gesture(HotkeyAction.ShowAll), "메뉴에 붙는 글자");
            var saved = AppSettings.Load().Hotkeys;
            Assert(saved is not null && saved["HideAll"] == hide && saved["ShowAll"] == show, "settings.json 에 저장 · 다시 읽기");
            // 정말 Windows 전역 단축키: 같은 키를 다른 곳에서는 등록하지 못함
            using (var other = new Hotkeys(new AppSettings { Hotkeys = new() { ["HideAll"] = hide } }))
            {
                other.Register();
                Equal(1, other.Failed.Count, "같은 키는 한 곳만 (다른 프로그램이 쓰는 중)");
            }

            var a = desk.NewMemo();
            a.ReplaceRtf(RtfText.FromPlain("하나"));
            var b = desk.NewMemo();
            b.ReplaceRtf(RtfText.FromPlain("둘"));
            Equal(Formats.Title(a.Memo.Created), a.TitleText.Text, "메모 머리는 날짜 · 요일 · 시각");

            // 키를 누르면 Windows 가 보내는 WM_HOTKEY
            PostMessage(desk.Hotkeys.Handle, 0x312, (IntPtr)Hotkeys.Id(HotkeyAction.HideAll), IntPtr.Zero);
            await Until(() => !desk.Windows.Any(), "모두 숨기기 단축키");
            Assert(!a.Memo.Visible && !b.Memo.Visible, "숨긴 메모는 숨김으로 저장");
            PostMessage(desk.Hotkeys.Handle, 0x312, (IntPtr)Hotkeys.Id(HotkeyAction.ShowAll), IntPtr.Zero);
            await Until(() => desk.Windows.Count() == 2 && desk.Windows.All(w => w.IsVisible), "모두 보이기 단축키");

            // 설정 창에서 바꾸기: 다른 쪽이 쓰던 키는 옮김, 다른 프로그램이 쓰는 키는 앞의 것으로 되돌림
            settingsWindow = new SettingsWindow(desk);
            string? text = settingsWindow.TakeKey(HotkeyAction.ShowAll, hide, null, out bool problem);
            Assert(!problem && text is not null && text.Contains("옮겼"), "겹치면 옮김: " + text);
            Assert(desk.Hotkeys.Get(HotkeyAction.ShowAll) == hide && desk.Hotkeys.Get(HotkeyAction.HideAll).IsNone, "옮긴 뒤: 보이기 = F11, 숨기기 = 없음");
            using (var taken = new Hotkeys(new AppSettings { Hotkeys = new() { ["HideAll"] = show } }))
            {
                taken.Register();
                text = settingsWindow.TakeKey(HotkeyAction.HideAll, show, null, out problem);
                Assert(problem && desk.Hotkeys.Get(HotkeyAction.HideAll).IsNone, "다른 프로그램이 쓰는 키는 되돌림: " + text);
            }
            HotkeyBinding.FromKey(System.Windows.Input.Key.H, System.Windows.Input.ModifierKeys.None, out string? warning);
            Assert(warning is not null, "글을 쓸 때 누르는 키는 알림");
            Equal(null, HotkeyBinding.FromKey(System.Windows.Input.Key.LeftCtrl, System.Windows.Input.ModifierKeys.Control, out _), "수정 키만 누르면 아직 아님");

            var day = new DateTime(DateTime.Now.Year, 3, 1, 9, 5, 0);
            Equal($"3월 1일 ({"일월화수목금토"[(int)day.DayOfWeek]}) 09:05", Formats.Title(day), "올해 날짜");
            Equal("2025년 11월 20일 (목) 15:12", Formats.Title(new DateTime(2025, 11, 20, 15, 12, 0)), "다른 해 날짜");
        }
        finally
        {
            settingsWindow?.Close();
            foreach (var window in desk.Windows.ToList()) window.Close();
            desk.Shutdown();
        }
        using var after = new Hotkeys(new AppSettings { Hotkeys = new() { ["HideAll"] = new HotkeyBinding(HotkeyBinding.MOD_CONTROL | HotkeyBinding.MOD_ALT | HotkeyBinding.MOD_SHIFT, 0x7A) } });
        after.Register();
        Equal(0, after.Failed.Count, "끝내면 단축키를 놓음");
    }
}
