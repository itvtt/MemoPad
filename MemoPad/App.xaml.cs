using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MemoPad.Core;
using MemoPad.Ui;
using Forms = System.Windows.Forms;

namespace MemoPad;

/// <summary>
/// MemoPad.exe          트레이에 상주, 바탕화면 메모를 띄움. 이미 실행 중이면 메모 관리기를 앞으로
/// MemoPad.exe --tray   (Windows 시작할 때) 똑같이 시작
/// MemoPad.exe --new    이미 실행 중이면 새 메모
/// MemoPad.exe --settings  설정 창 (이미 실행 중이면 그 MemoPad 의 설정)
/// 트레이 아이콘: 클릭 = 새 메모, Ctrl+클릭 = 모두 보이기, Alt+클릭 = 메모 관리기, 오른쪽 클릭 = 메뉴
/// </summary>
public partial class App : Application
{
    private const string ShowSignalName = @"Local\MemoPad-show", NewSignalName = @"Local\MemoPad-new", SettingsSignalName = @"Local\MemoPad-settings";
    private Mutex? _single;
    private EventWaitHandle? _showSignal, _newSignal, _settingsSignal;
    private RegisteredWaitHandle? _showWait, _newWait, _settingsWait;
    private Forms.NotifyIcon? _tray;
    private Window? _trayHost;
    private DateTime _lastTrayClick;

    public static Desk? Desk { get; private set; }

    public static ImageSource IconImage { get; } = LoadIconImage();

    private static ImageSource LoadIconImage()
    {
        try { return BitmapFrame.Create(new Uri("pack://application:,,,/MemoPad;component/Assets/MemoPad.ico")); }
        catch (Exception error) when (error is IOException or NotSupportedException) { return new DrawingImage(); }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        // 어떤 오류가 나도 꺼지지 않게: 기록하고 계속 (메모를 받으려면 늘 켜져 있어야 함)
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("작업", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("앱", args.ExceptionObject as Exception);
        bool wantsNew = e.Args.Any(a => string.Equals(a, "--new", StringComparison.OrdinalIgnoreCase));
        bool wantsSettings = e.Args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));
        // 다시 시작 (데이터 폴더를 바꾼 뒤): 앞의 MemoPad 가 끝날 때까지 기다림
        int after = Array.FindIndex(e.Args, a => string.Equals(a, "--after", StringComparison.OrdinalIgnoreCase));
        if (after >= 0 && after + 1 < e.Args.Length && int.TryParse(e.Args[after + 1], out int previous))
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(previous); old.WaitForExit(15_000); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
        }
        // 시험용 폴더(MEMOPAD_DATA_DIR)로 띄우면 이름을 따로 (설치된 MemoPad 와 함께 켜 둘 수 있게)
        string suffix = Environment.GetEnvironmentVariable("MEMOPAD_DATA_DIR") is { Length: > 0 } dir
            ? "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..12] : "";
        _single = new Mutex(true, @"Local\MemoPad-single" + suffix, out bool first);
        if (!first)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting((wantsSettings ? SettingsSignalName : wantsNew ? NewSignalName : ShowSignalName) + suffix);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        if (!ChooseDataFolder()) { Shutdown(); return; }
        bool firstRun = !File.Exists(Path.Combine(AppSettings.DataDirectory, "settings.json"));
        var settings = AppSettings.Load();
        bool installed = ILuncher.AppKit.AppUpdater.IsInstalledCopy("MemoPad");
        // 처음 한 번: Windows 시작할 때 실행 (로그인하면 메모가 바탕화면에 다시 뜨고 알람이 울리게). 설치된 사본일 때만
        if (!settings.StartupConfigured && installed)
        {
            settings.StartupConfigured = true;
            try { StartWithWindows.Set(true); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        else if (installed) StartWithWindows.Refresh();
        settings.Save();

        Desk = new Desk(settings, new MemoStore());
        Desk.StateChanged += UpdateTray;
        CreateTray();
        Desk.Start();
        UpdateTray();
        WarnIfNotListening();
        WatchStorage(Desk.Store);
        if (firstRun)
        {
            if (Desk.Networked) Desk.ShowSettings();   // 내 정보(이름 · ID)를 정하게
            if (!Desk.Store.Live.Any()) Desk.NewMemo();
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName + suffix);
        _newSignal = new EventWaitHandle(false, EventResetMode.AutoReset, NewSignalName + suffix);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(() => Desk.ShowManager()), null, -1, false);
        _newWait = ThreadPool.RegisterWaitForSingleObject(_newSignal, (_, _) => Dispatcher.BeginInvoke(() => Desk.NewMemo()), null, -1, false);
        _settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsSignalName + suffix);
        _settingsWait = ThreadPool.RegisterWaitForSingleObject(_settingsSignal, (_, _) => Dispatcher.BeginInvoke(() => Desk?.ShowSettings()), null, -1, false);
        if (wantsSettings) Desk.ShowSettings();
        SessionEnding += (_, _) => Desk?.Shutdown();

        // 사용 시간 · 가동 시간 · 메모 · 알람 집계 (AppKit\AppUsage.cs → API 서버, ImPerformance 성과 계산용). 바탕화면 메모라 상주 앱
        ILuncher.AppKit.AppUsage.Start(ILuncher.AppKit.UsageKind.Resident);
        // i-luncher AppKit: 설치된 사본이면 시작할 때 GitHub 릴리스를 확인해 스스로 업데이트 (AppKit\AppUpdater.cs)
        ILuncher.AppKit.AppUpdater.StartBackground(ready => Dispatcher.BeginInvoke(() =>
        {
            if (Dialogs.Ask(null, "MemoPad 업데이트", $"새 버전 {ready.Update.Version}을(를) 받았습니다. (지금 {ready.CurrentVersion})\n\n지금 다시 시작할까요? [아니요]를 누르면 종료할 때 적용됩니다."))
            {
                ready.RestartNow();
                Quit();
            }
        }));
    }

    // ── 트레이 ─────────────────────────────────────────────────

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon { Text = "MemoPad", Icon = LoadTrayIcon(), Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right) { ShowTrayMenu(); return; }
            if (e.Button != Forms.MouseButtons.Left) return;
            // 두 번 클릭해도 메모가 두 장 생기지 않게
            if ((DateTime.Now - _lastTrayClick).TotalMilliseconds < Forms.SystemInformation.DoubleClickTime) return;
            _lastTrayClick = DateTime.Now;
            var keys = Forms.Control.ModifierKeys;
            if (keys.HasFlag(Forms.Keys.Control)) Desk!.ShowAll();
            else if (keys.HasFlag(Forms.Keys.Alt)) Desk!.ShowManager();
            else Desk!.NewMemo();
        };
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/MemoPad;component/Assets/MemoPad.ico"));
        if (resource is null) return System.Drawing.SystemIcons.Application;
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    /// <summary>
    /// 메모 파일 지키기: 시작할 때 되살리거나 옮긴 파일 · 저장하지 못함 · 다시 저장됨을 트레이로 알리고, 하루 한 번 백업(뒤에서)
    /// </summary>
    private void WatchStorage(MemoStore store)
    {
        if (store.Damaged.Count > 0)
            _tray?.ShowBalloonTip(10000, "MemoPad: 읽지 못한 메모가 있습니다",
                $"메모 {store.Damaged.Count}개가 깨져 있어 '{MemoStore.DamagedFolderName}' 폴더에 옮겨 두었습니다. 자세한 것은 memopad.log", Forms.ToolTipIcon.Warning);
        else if (store.Recovered.Count > 0)
            _tray?.ShowBalloonTip(8000, "MemoPad: 메모를 되살렸습니다",
                $"깨져 있던 메모 {store.Recovered.Count}개를 바로 전 저장본으로 되살렸습니다.", Forms.ToolTipIcon.Info);
        store.SaveFailed += reason => _tray?.ShowBalloonTip(10000, "MemoPad: 메모를 저장하지 못했습니다",
            $"고친 내용은 그대로 두고 10초마다 다시 저장합니다. 데이터 폴더를 확인하세요. ({reason})", Forms.ToolTipIcon.Warning);
        store.SaveRecovered += () => _tray?.ShowBalloonTip(5000, "MemoPad", "저장하지 못했던 메모를 모두 저장했습니다.", Forms.ToolTipIcon.Info);
        _ = Task.Run(() => store.BackupDaily());
    }

    /// <summary>메모를 받을 수 없으면 (다른 프로그램이 MemoPad 포트를 씀) 트레이 알림</summary>
    private void WarnIfNotListening()
    {
        if (_tray is null || Desk is null || !Desk.Networked || Desk.Net.Listening) return;
        _tray.ShowBalloonTip(8000, "MemoPad: 메모를 받을 수 없습니다",
            Desk.Net.StartError + " 그 프로그램을 끈 뒤 트레이 메뉴의 [메모 받기 다시 시도]를 누르세요.", Forms.ToolTipIcon.Warning);
    }

    private void UpdateTray()
    {
        if (_tray is null || Desk is null) return;
        string text = "MemoPad";
        if (Desk.Networked && Desk.Settings.Absent) text += " · 부재중";
        else if (Desk.Networked && !Desk.Net.Listening) text += " · 메모를 받을 수 없음";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>트레이 메뉴 (메모와 같은 어두운 메뉴). 보이지 않는 창을 잠깐 앞으로 해 바깥을 누르면 닫히게</summary>
    private void ShowTrayMenu()
    {
        var desk = Desk!;
        if (_trayHost is null)
        {
            _trayHost = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, Width = 0, Height = 0, Left = -10000, Top = -10000, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true };
            _trayHost.SourceInitialized += (_, _) => Native.AddExStyle(_trayHost, Native.WS_EX_TOOLWINDOW);
        }
        _trayHost.Show();
        _trayHost.Activate();
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var items = menu.Items;
        items.Add(MemoWindow.Item("새 메모", () => desk.NewMemo(), Icons.Add, "클릭"));
        items.Add(MemoWindow.Item("새 그림", () => desk.NewMemo(MemoKind.Drawing), Icons.Pen));
        items.Add(new Separator());
        if (desk.Networked) items.Add(MemoWindow.Item("대화창 열기", () => desk.OpenChat(), Icons.Chat));
        items.Add(MemoWindow.Item("메모 관리기", () => desk.ShowManager(), Icons.Folder, "Alt+클릭"));
        items.Add(new Separator());
        string showKey = desk.Hotkeys.Gesture(HotkeyAction.ShowAll);
        items.Add(MemoWindow.Item("모두 보이기", desk.ShowAll, Icons.Show, showKey.Length > 0 ? showKey : "Ctrl+클릭"));
        items.Add(MemoWindow.Item("모두 숨기기", desk.HideAll, Icons.Hide, desk.Hotkeys.Gesture(HotkeyAction.HideAll)));
        items.Add(MemoWindow.Item("바탕화면 메모 정렬", desk.Arrange, Icons.Grid));
        var discard = MemoWindow.Item("모두 버리기", desk.DiscardAll, Icons.Delete);
        discard.Style = (Style)Resources["DangerMenuItemStyle"];
        items.Add(discard);
        if (desk.Networked)
        {
            items.Add(new Separator());
            items.Add(MemoWindow.Item("주소록", desk.ShowAddressBook, Icons.People));
            items.Add(MemoWindow.Item("사용자 상태 확인", desk.ShowStatus, Icons.Status));
            items.Add(MemoWindow.Item("부재중", () => desk.SetAbsent(!desk.Settings.Absent), check: desk.Settings.Absent));
            if (!desk.Net.Listening)
                items.Add(MemoWindow.Item("메모 받기 다시 시도", () => { desk.StartNetwork(); WarnIfNotListening(); }, Icons.Refresh));
        }
        items.Add(new Separator());
        items.Add(MemoWindow.Item("설정", desk.ShowSettings, Icons.Settings));
        items.Add(MemoWindow.Item("종료", Quit, ""));
        menu.Closed += (_, _) => _trayHost?.Hide();
        menu.PlacementTarget = _trayHost;
        menu.IsOpen = true;
    }

    private void Quit()
    {
        Desk?.Shutdown();
        Shutdown();
    }

    /// <summary>
    /// 처음 실행이면 데이터 폴더를 물음 (고른 폴더를 쓸 수 없게 됐을 때도). [끝내기] = false.
    /// 고른 폴더는 HKCU\Software\MemoPad 에 남아 다시 설치해도 그대로 씀
    /// </summary>
    private static bool ChooseDataFolder()
    {
        string? warning = null;
        if (DataLocation.TestFolder is null && DataLocation.Saved is { } saved && DataLocation.CheckWritable(saved) is { } problem)
            warning = $"데이터 폴더를 쓸 수 없습니다.\n{saved}\n({problem})";
        else if (!DataLocation.NeedsChoice) return true;
        string? folder = DataFolderDialog.Choose(DataLocation.Saved ?? DataLocation.Default, warning);
        if (folder is null) return false;
        DataLocation.Save(folder);
        AppSettings.UseDataDirectory(folder);
        return true;
    }

    /// <summary>
    /// 데이터 폴더 바꾸기 (설정): 메모를 모두 저장하고 창을 닫은 뒤, move 면 MemoPad 파일을 새 폴더로 옮기고 (아니면 그 폴더의 메모를 씀)
    /// 새 폴더를 기록하고 다시 시작. 옮기다 실패하면 옛 폴더 그대로 다시 시작
    /// </summary>
    public void ChangeDataFolder(string folder, bool move)
    {
        Desk?.Shutdown();
        Desk = null;
        foreach (var window in Windows.OfType<Window>().ToList()) window.Close();   // 마지막 저장은 옛 폴더에
        try
        {
            if (move) DataLocation.Move(AppSettings.DataDirectory, folder);
            DataLocation.Save(folder);
            AppSettings.UseDataDirectory(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("데이터 폴더 옮기기", error);
            Dialogs.Info(null, "데이터 폴더", $"옮기지 못했습니다. 지금 폴더를 그대로 씁니다.\n{error.Message}");
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"--after {Environment.ProcessId}") { UseShellExecute = false });
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _newWait?.Unregister(null);
        _settingsWait?.Unregister(null);
        _showSignal?.Dispose();
        _newSignal?.Dispose();
        _settingsSignal?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _single?.Dispose();
        base.OnExit(e);
    }
}
