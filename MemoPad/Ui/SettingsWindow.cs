using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MemoPad.Core;
using MemoPad.Net;

namespace MemoPad.Ui;

/// <summary>설정: 바꾸면 바로 저장 (저장 단추 없음)</summary>
internal sealed class SettingsWindow : Window
{
    private readonly Desk _desk;
    private AppSettings S => _desk.Settings;
    private readonly TextBlock _netState = new() { Style = (Style)Application.Current.Resources["MutedTextStyle"], Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _passwordState = new() { Style = (Style)Application.Current.Resources["FieldLabelStyle"] };
    private readonly Dictionary<HotkeyAction, Button> _keyBoxes = [];
    private readonly TextBlock _keyMessage = new() { Style = (Style)Application.Current.Resources["MutedTextStyle"], Margin = new Thickness(0, 4, 0, 2), Visibility = Visibility.Collapsed };
    /// <summary>단축키 칸을 누른 뒤 키를 기다리는 중</summary>
    private HotkeyAction? _waiting;

    public SettingsWindow(Desk desk)
    {
        _desk = desk;
        Chrome.Apply(this, resizable: true);
        Title = "설정 - MemoPad";
        Icon = App.IconImage;
        Width = 600;
        Height = 720;
        MinWidth = 500;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var bar = Chrome.TitleBar(this, "설정", Icons.Settings, minMax: false);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 8) };
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)), BorderThickness = new Thickness(1), Child = root };

        if (desk.Networked) AddNetworkSections(body);
        AddLocalSections(body);

        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => StopWaiting();
        Closed += (_, _) => { StopWaiting(); S.Save(); };
    }

    /// <summary>내 정보 · 받기 · 보내기 (네트워크 기능을 쓸 때만)</summary>
    private void AddNetworkSections(StackPanel body)
    {
        var desk = _desk;
        // ── 내 정보 ──
        var me = Section(body, "내 정보 (메모를 받는 사람에게 보임)");
        var grid = Fields(me);
        Field(grid, 0, "이름", Text(S.Name, v => S.Name = v, Environment.UserName));
        Field(grid, 1, "ID", Text(S.Id, v => S.Id = v, Environment.UserName));
        Field(grid, 2, "E-Mail", Text(S.Email, v => S.Email = v, "(선택)"));
        Field(grid, 3, "대화명", Text(S.ChatName, v => S.ChatName = v, "(비우면 ID)"));
        Field(grid, 4, "IP 주소", IpChooser());
        me.Children.Add(Check("네트워크 기능 사용 (메모 주고받기 · 상태 확인 · 대화)", S.UseNetwork, v =>
        {
            S.UseNetwork = v;
            if (v) desk.StartNetwork(); else desk.StopNetwork();
            UpdateNetState();
        }));
        me.Children.Add(_netState);
        UpdateNetState();

        // ── 받기 · 보내기 ──
        var net = Section(body, "받기 · 보내기");
        var soundRow = new DockPanel();
        var soundButton = Chrome.Pill(S.SoundFile.Length > 0 ? Path.GetFileName(S.SoundFile) : "기본 소리", () => { });
        soundButton.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "소리 파일 (*.wav)|*.wav", Title = "받을 때 소리" };
            if (dialog.ShowDialog(this) == true) S.SoundFile = dialog.FileName;
            else if (S.SoundFile.Length > 0 && Dialogs.Ask(this, "소리", "기본 소리로 되돌릴까요?")) S.SoundFile = "";
            soundButton.Content = S.SoundFile.Length > 0 ? Path.GetFileName(S.SoundFile) : "기본 소리";
            S.Save();
        };
        soundButton.Height = 28;
        DockPanel.SetDock(soundButton, Dock.Right);
        soundRow.Children.Add(soundButton);
        soundRow.Children.Add(Check("메모를 받으면 소리", S.Sound, v => S.Sound = v));
        net.Children.Add(soundRow);
        net.Children.Add(Check("받은 메모는 항상 위", S.ReceivedOnTop, v => S.ReceivedOnTop = v));
        net.Children.Add(Check("받은 메모 줄 앞에 '>' 붙이기", S.QuoteReceived, v => S.QuoteReceived = v));
        net.Children.Add(Check("보낸 메모는 버리기 (휴지통으로)", S.CloseAfterSend, v => S.CloseAfterSend = v));
        net.Children.Add(Check("보내지 못한 사람이 있으면 알리기", S.NotifyUndelivered, v => S.NotifyUndelivered = v));
        var netGrid = Fields(net);
        Field(netGrid, 0, "부재중 문구", Text(S.AbsentText, v => S.AbsentText = v, "저는 현재 부재중입니다."));
        Field(netGrid, 1, "받은 메모 전달", Text(S.ForwardTo, v => S.ForwardTo = v, "다른 PC 의 IP (여럿이면 ; 로)"));
        Field(netGrid, 2, "주소록 폴더", FolderChooser());
    }

    /// <summary>데이터 폴더 · 새 메모 · 일반 · 보안</summary>
    private void AddLocalSections(StackPanel body)
    {
        // ── 데이터 폴더 ──
        var data = Section(body, "데이터 폴더");
        var dataRow = new DockPanel();
        var dataButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        var open = Chrome.Pill("열기", () => System.Diagnostics.Process.Start("explorer.exe", $"\"{AppSettings.DataDirectory}\""));
        var change = Chrome.Pill("바꾸기…", ChangeDataFolder);
        open.Height = change.Height = 30;
        change.IsEnabled = DataLocation.TestFolder is null;
        if (!change.IsEnabled) change.ToolTip = "시험 폴더(MEMOPAD_DATA_DIR)를 쓰는 중";
        dataButtons.Children.Add(open);
        dataButtons.Children.Add(change);
        DockPanel.SetDock(dataButtons, Dock.Right);
        dataRow.Children.Add(dataButtons);
        dataRow.Children.Add(new TextBox { Text = AppSettings.DataDirectory, IsReadOnly = true });
        data.Children.Add(dataRow);
        var dataNote = new TextBlock { Text = "메모와 설정이 저장되는 곳입니다. 바꾸면 새 폴더로 옮긴 뒤 다시 시작합니다.", Style = (Style)Application.Current.Resources["MutedTextStyle"], Margin = new Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap };
        data.Children.Add(dataNote);

        // ── 새 메모 ──
        var memo = Section(body, "새 메모");
        var swatches = new WrapPanel();
        foreach (var (name, hex) in MemoColors.Palette)
        {
            var swatch = new RadioButton { Style = (Style)Application.Current.Resources["SwatchStyle"], Background = new SolidColorBrush(MemoColors.Parse(hex)), ToolTip = name, GroupName = "color", IsChecked = string.Equals(S.DefaultColor, hex, StringComparison.OrdinalIgnoreCase), Width = 28, Height = 28 };
            string chosen = hex;
            swatch.Checked += (_, _) => { S.DefaultColor = chosen; S.Save(); };
            swatches.Children.Add(swatch);
        }
        var memoGrid = Fields(memo);
        Field(memoGrid, 0, "바탕색", swatches);
        var font = Chrome.Pill($"{S.FontFamily} {S.FontSize * 72 / 96:0.#}pt", () => { });
        font.Height = 28;
        font.HorizontalAlignment = HorizontalAlignment.Left;
        font.Margin = new Thickness(0);
        font.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.FontDialog { ShowEffects = false, AllowVerticalFonts = false };
            try { dialog.Font = new System.Drawing.Font(S.FontFamily, (float)(S.FontSize * 72 / 96)); }
            catch (ArgumentException) { }
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            S.FontFamily = dialog.Font.Name;
            S.FontSize = Math.Round(dialog.Font.SizeInPoints * 96 / 72, 1);
            font.Content = $"{S.FontFamily} {dialog.Font.SizeInPoints:0.#}pt";
            S.Save();
        };
        Field(memoGrid, 1, "글꼴", font);
        var size = new StackPanel { Orientation = Orientation.Horizontal };
        size.Children.Add(Number(S.DefaultWidth, 120, 1200, v => S.DefaultWidth = v));
        size.Children.Add(new TextBlock { Text = "×", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        size.Children.Add(Number(S.DefaultHeight, 60, 1000, v => S.DefaultHeight = v));
        Field(memoGrid, 2, "크기", size);
        Field(memoGrid, 3, "투명도", OpacitySlider(S.DefaultOpacity, v => S.DefaultOpacity = v));
        Field(memoGrid, 4, "접은 메모", OpacitySlider(S.CollapsedOpacity, v => S.CollapsedOpacity = v));
        memo.Children.Add(Check("항상 위", S.DefaultTop, v => S.DefaultTop = v));
        memo.Children.Add(Check("상태줄 보이기", S.DefaultStatusBar, v => S.DefaultStatusBar = v));
        memo.Children.Add(Check("스크롤 막대 (새로 여는 메모부터)", S.ScrollBar, v => S.ScrollBar = v));

        // ── 일반 ──
        var general = Section(body, "일반");
        bool installed = ILuncher.AppKit.AppUpdater.IsInstalledCopy("MemoPad");
        var startup = Check("Windows 시작할 때 실행", StartWithWindows.IsOn, v => StartWithWindows.Set(v));
        startup.IsEnabled = installed;
        if (!installed) startup.ToolTip = "설치한 사본에서만 (i-luncher 로 설치)";
        general.Children.Add(startup);
        general.Children.Add(Check("시작할 때 메모 관리기 열기", S.ManagerAtStart, v => S.ManagerAtStart = v));
        general.Children.Add(Check("메모 제목 늘 보이기", S.AlwaysShowTitle, v => { S.AlwaysShowTitle = v; Refresh(); }));
        general.Children.Add(Check("다른 창을 쓸 때 메모 테두리 숨기기", S.HideInactiveBorder, v => { S.HideInactiveBorder = v; Refresh(); }));
        general.Children.Add(Check("버린 메모는 휴지통으로 (메모 관리기에서 되살리기)", S.UseTrash, v => S.UseTrash = v));
        var generalGrid = Fields(general);
        Field(generalGrid, 0, "날짜 넣기 (Ctrl+D)", FormatChooser(Formats.Dates, S.DateFormat, i => S.DateFormat = i, Formats.Date));
        Field(generalGrid, 1, "시간 넣기 (Ctrl+T)", FormatChooser(Formats.Times, S.TimeFormat, i => S.TimeFormat = i, Formats.Time));
        Field(generalGrid, 2, "정렬 간격", Number(S.ArrangeGap, 0, 100, v => S.ArrangeGap = v));
        general.Children.Add(Check("정렬할 때 펼친 메모는 그대로", S.ArrangeSkipExpanded, v => S.ArrangeSkipExpanded = v));

        // ── 단축키 ──
        var keys = Section(body, "단축키");
        var keyGrid = Fields(keys);
        for (int i = 0; i < Hotkeys.Actions.Length; i++) Field(keyGrid, i, Hotkeys.Name(Hotkeys.Actions[i]), KeyRow(Hotkeys.Actions[i]));
        keys.Children.Add(_keyMessage);
        _desk.Hotkeys.Register();   // 앞서 쓰지 못한 키를 다른 프로그램이 놓았을 수 있음
        ShowKeys();

        // ── 보안 ──
        var security = Section(body, "보안");
        var passwordRow = new DockPanel();
        var passwordButtons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(passwordButtons, Dock.Right);
        passwordButtons.Children.Add(Chrome.Pill("암호 정하기…", ChangePassword));
        passwordButtons.Children.Add(Chrome.Pill("암호 지우기", ClearPassword));
        passwordRow.Children.Add(passwordButtons);
        passwordRow.Children.Add(_passwordState);
        security.Children.Add(passwordRow);
        if (_desk.Networked) security.Children.Add(Check("받은 메모는 늘 보안 메모로", S.SecureReceived, v => S.SecureReceived = v));
        UpdatePassword();
    }

    // ── 단축키 ──

    private UIElement KeyRow(HotkeyAction action)
    {
        var row = new DockPanel();
        var clear = Chrome.IconButton(Icons.Close, "단축키 없애기", () =>
        {
            StopWaiting();
            _desk.Hotkeys.Set(action, default);
            _keyMessage.Visibility = Visibility.Collapsed;
            ShowKeys();
        }, size: 9);
        clear.Width = clear.Height = 26;
        clear.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(clear, Dock.Right);
        row.Children.Add(clear);
        var box = new Button { Style = (Style)Application.Current.Resources["KeyBoxStyle"], ToolTip = "눌러서 바꾸기" };
        box.Click += (_, _) => StartWaiting(action);
        row.Children.Add(box);
        _keyBoxes[action] = box;
        return row;
    }

    private void ShowKeys()
    {
        var failed = _desk.Hotkeys.Failed;
        foreach (var (action, box) in _keyBoxes)
        {
            var binding = _desk.Hotkeys.Get(action);
            bool waiting = _waiting == action;
            box.Content = waiting ? "원하는 키를 누르세요…" : binding + (failed.Contains(action) ? "  · 다른 프로그램이 사용 중" : "");
            box.Foreground = (Brush)Application.Current.Resources[waiting || binding.IsNone ? "MutedTextBrush" : failed.Contains(action) ? "WarningBrush" : "TextBrush"];
            box.BorderBrush = waiting ? (Brush)Application.Current.Resources["AccentBrush"] : new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));
        }
    }

    private void StartWaiting(HotkeyAction action)
    {
        if (_waiting is null) _desk.Hotkeys.Suspend();
        _waiting = action;
        _keyMessage.Visibility = Visibility.Collapsed;
        ShowKeys();
        Keyboard.Focus(_keyBoxes[action]);
    }

    private void StopWaiting()
    {
        if (_waiting is null) return;
        _waiting = null;
        _desk.Hotkeys.Resume();
        ShowKeys();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_waiting is not { } action)
        {
            if (e.Key == Key.Escape) Close();
            return;
        }
        e.Handled = true;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape && modifiers == ModifierKeys.None) { StopWaiting(); return; }
        if (HotkeyBinding.FromKey(key, modifiers, out string? warning) is not { } binding) return;   // 수정 키만: 나머지 키를 기다림
        _keyMessage.Text = TakeKey(action, binding, warning, out bool problem) ?? "";
        _keyMessage.Foreground = (Brush)Application.Current.Resources[problem ? "WarningBrush" : "MutedTextBrush"];
        _keyMessage.Visibility = _keyMessage.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>누른 키를 단축키로. 다른 쪽이 쓰던 키면 옮기고, 다른 프로그램이 쓰는 키면 앞의 것으로 되돌림. 알릴 말 (없으면 null)</summary>
    internal string? TakeKey(HotkeyAction action, HotkeyBinding binding, string? warning, out bool problem)
    {
        var hotkeys = _desk.Hotkeys;
        string? moved = null;
        foreach (var other in Hotkeys.Actions.Where(a => a != action && hotkeys.Get(a) == binding))
        {
            hotkeys.Set(other, default);
            moved = $"{Hotkeys.Name(other)}에서 옮겼습니다.";
        }
        var previous = hotkeys.Get(action);
        hotkeys.Set(action, binding);
        StopWaiting();
        problem = hotkeys.Failed.Contains(action);
        if (problem)
        {
            hotkeys.Set(action, previous);
            ShowKeys();
            return $"{binding} 는 다른 프로그램이 쓰고 있습니다.";
        }
        ShowKeys();
        var parts = new[] { moved, warning }.Where(t => t is not null).ToList();
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    private void Refresh()
    {
        foreach (var window in _desk.Windows) window.ApplyLook();
    }

    private StackPanel Section(StackPanel body, string title)
    {
        body.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        var panel = new StackPanel();
        body.Children.Add(new Border { Style = (Style)Application.Current.Resources["SectionStyle"], Child = panel });
        return panel;
    }

    private static Grid Fields(StackPanel section)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        section.Children.Add(grid);
        return grid;
    }

    private static void Field(Grid grid, int row, string label, UIElement field) => Dialogs.AddRow(grid, row, label, field);

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 5, 0, 5) };
        box.Checked += (_, _) => { set(true); S.Save(); };
        box.Unchecked += (_, _) => { set(false); S.Save(); };
        return box;
    }

    private TextBox Text(string value, Action<string> set, string hint = "")
    {
        var box = new TextBox { Text = value, Tag = hint };
        box.LostFocus += (_, _) => { set(box.Text.Trim()); S.Save(); };
        return box;
    }

    private TextBox Number(int value, int min, int max, Action<int> set)
    {
        var box = new TextBox { Text = value.ToString(), Width = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
        box.LostFocus += (_, _) =>
        {
            if (int.TryParse(box.Text, out int number)) { number = Math.Clamp(number, min, max); set(number); box.Text = number.ToString(); S.Save(); }
            else box.Text = value.ToString();
        };
        return box;
    }

    private Grid OpacitySlider(double value, Action<double> set)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var slider = new Slider { Minimum = 0.2, Maximum = 1, Value = value, LargeChange = 0.1, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = $"{value:P0}", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        slider.ValueChanged += (_, e) => { label.Text = $"{e.NewValue:P0}"; set(Math.Round(e.NewValue, 2)); };
        slider.LostMouseCapture += (_, _) => S.Save();
        grid.Children.Add(slider);
        grid.Children.Add(label);
        return grid;
    }

    private Button FormatChooser(string[] formats, int current, Action<int> set, Func<DateTime, int, string> sample)
    {
        var button = Chrome.Pill(sample(DateTime.Now, current), () => { });
        button.Height = 28;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0);
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            for (int i = 0; i < formats.Length; i++)
            {
                int index = i;
                menu.Items.Add(MemoWindow.Item(sample(DateTime.Now, i), () =>
                {
                    set(index);
                    button.Content = sample(DateTime.Now, index);
                    S.Save();
                }, check: i == current));
            }
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return button;
    }

    private UIElement IpChooser()
    {
        var addresses = NetAddress.LocalAddresses();
        var button = Chrome.Pill(S.Ip.Length > 0 ? S.Ip : $"자동 ({NetAddress.MyAddress("")})", () => { });
        button.Height = 28;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0);
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            menu.Items.Add(MemoWindow.Item($"자동 ({NetAddress.MyAddress("")})", () => { S.Ip = ""; button.Content = $"자동 ({NetAddress.MyAddress("")})"; S.Save(); }, check: S.Ip.Length == 0));
            foreach (string ip in addresses)
                menu.Items.Add(MemoWindow.Item(ip, () => { S.Ip = ip; button.Content = ip; S.Save(); }, check: S.Ip == ip));
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return button;
    }

    private UIElement FolderChooser()
    {
        var panel = new DockPanel();
        var box = new TextBox { Text = S.AddressBookFolder, Tag = "(비우면 이 PC 의 설정 폴더) 공유 폴더면 여러 사람이 함께 씀" };
        var browse = Chrome.Pill("찾기…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "주소록 폴더" };
            if (dialog.ShowDialog(this) == true) { box.Text = dialog.FolderName; Apply(); }
        });
        browse.Height = 30;
        DockPanel.SetDock(browse, Dock.Right);
        panel.Children.Add(browse);
        panel.Children.Add(box);
        void Apply()
        {
            string folder = box.Text.Trim();
            if (folder.Length > 0 && !Directory.Exists(folder))
            {
                Dialogs.Info(this, "주소록 폴더", "폴더가 없거나 열 수 없습니다.");
                box.Text = S.AddressBookFolder;
                return;
            }
            if (folder == S.AddressBookFolder) return;
            // 지금 주소록을 새 폴더로 합쳐서 옮김 (새 폴더에 이미 있으면 합침)
            var current = _desk.LoadAddressBook();
            S.AddressBookFolder = folder;
            S.Save();
            var target = _desk.LoadAddressBook();
            foreach (var person in current.People.Where(p => target.FindByIp(p.Ip) is null)) target.People.Add(person);
            foreach (var group in current.Groups.Where(g => target.Groups.All(t => t.Name != g.Name))) target.Groups.Add(group);
            _desk.SaveAddressBook(target, this);
        }
        box.LostFocus += (_, _) => Apply();
        return panel;
    }

    private void UpdateNetState()
    {
        if (!S.UseNetwork) { _netState.Text = "꺼져 있음: 메모를 받지 않습니다."; return; }
        if (!_desk.Net.Listening) { _netState.Text = $"받을 수 없음: {_desk.Net.StartError}"; return; }
        _netState.Text = $"받는 중: {NetAddress.MyAddress(S.Ip)} · 메모 포트 {Ports.MemoPort} · 대화 포트 {Ports.ChatPort}\n" + (_desk.Net.JungumListening
            ? $"정음 메모패드 사용자가 보낸 것도 받음 (포트 {JuProtocol.MemoPort} · {JuProtocol.ChatPort})"
            : "정음 메모패드 사용자가 보낸 것은 이 PC 의 정음 메모패드가 받음");
    }

    /// <summary>데이터 폴더 바꾸기: 빈 폴더면 옮기고, MemoPad 데이터가 있는 폴더면 그 메모를 씀. 그다음 다시 시작</summary>
    private void ChangeDataFolder()
    {
        if (DataFolderDialog.Change(this, AppSettings.DataDirectory) is not { } folder) return;
        ((App)Application.Current).ChangeDataFolder(folder, move: !DataLocation.HasData(folder));
    }

    private void UpdatePassword() => _passwordState.Text = S.HasPassword ? "보안 암호가 있습니다." : "보안 암호가 없습니다.";

    private void ChangePassword()
    {
        if (S.HasPassword)
        {
            string? old = Dialogs.Password(this, "암호 바꾸기", "이전 암호");
            if (old is null) return;
            if (!S.CheckPassword(old)) { Dialogs.Info(this, "암호", "이전의 암호가 확인되지 않습니다."); return; }
        }
        string? first = Dialogs.Password(this, "암호 정하기", "새 암호");
        if (string.IsNullOrEmpty(first)) return;
        string? second = Dialogs.Password(this, "암호 정하기", "새 암호 확인");
        if (second is null) return;
        if (first != second) { Dialogs.Info(this, "암호", "입력한 두 개의 새 암호가 서로 다릅니다."); return; }
        S.SetPassword(first);
        S.Save();
        UpdatePassword();
    }

    private void ClearPassword()
    {
        if (!S.HasPassword) return;
        string? old = Dialogs.Password(this, "암호 지우기", "지금 암호");
        if (old is null) return;
        if (!S.CheckPassword(old)) { Dialogs.Info(this, "암호", "암호가 맞지 않습니다."); return; }
        S.SetPassword("");
        S.Save();
        UpdatePassword();
        foreach (var window in _desk.Windows.Where(w => w.Memo.Secured))
        {
            window.Memo.Secured = false;
            _desk.Save(window.Memo);
            window.ApplyLook();
        }
        foreach (var memo in _desk.Store.All.Where(m => m.Secured)) { memo.Secured = false; _desk.Save(memo); }
    }
}

/// <summary>Windows 시작할 때 실행 (HKCU Run, 트레이로 시작)</summary>
internal static class StartWithWindows
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "MemoPad";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsOn
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
        if (on) key.SetValue(Name, Command);
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>켜져 있으면 지금 실행 파일 경로로 고침 (설치 폴더가 바뀐 경우)</summary>
    public static void Refresh()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (key?.GetValue(Name) is string value && value != Command) key.SetValue(Name, Command);
    }
}
