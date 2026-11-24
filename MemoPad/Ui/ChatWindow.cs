using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemoPad.Core;
using MemoPad.Net;

namespace MemoPad.Ui;

/// <summary>대화창: 왼쪽 대화, 오른쪽 참여자, 아래 입력. F1~F12 = 단축 문구</summary>
public sealed class ChatWindow : Window
{
    private readonly Desk _desk;
    private ChatSession _session;
    private readonly StackPanel _log = new() { Margin = new Thickness(4) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox _members = new();
    private readonly TextBox _input = new() { Tag = "할 말 (Enter 보내기 · F1~F12 단축 문구)" };
    private readonly Grid _titleBar;
    private readonly Button _lock;

    public ChatWindow(Desk desk, ChatSession session)
    {
        _desk = desk;
        _session = session;
        Chrome.Apply(this, resizable: true);
        Width = 620;
        Height = 460;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = App.IconImage;

        _lock = Chrome.IconButton(Icons.Lock, "대화방 잠그기 (새로 못 들어옴)", ToggleLock, 13);
        _titleBar = Chrome.TitleBar(this, "대화", Icons.Chat, true,
            Chrome.IconButton("", "접속 변경 (다른 사람에게 들어가기)", Connect, 13),
            _lock,
            Chrome.IconButton(Icons.Save, "갈무리 (대화 내용 저장)", SaveLog, 13),
            Chrome.IconButton("", "단축 문구 (F1~F12)", EditMacros, 13));
        var root = new DockPanel();
        DockPanel.SetDock(_titleBar, Dock.Top);
        root.Children.Add(_titleBar);

        var input = new DockPanel { Margin = new Thickness(14, 8, 14, 14) };
        var send = Chrome.Pill("보내기", () => _ = SendAsync(), primary: true);
        DockPanel.SetDock(send, Dock.Right);
        input.Children.Add(send);
        input.Children.Add(_input);
        DockPanel.SetDock(input, Dock.Bottom);
        root.Children.Add(input);

        var grid = new Grid { Margin = new Thickness(14, 12, 14, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        _scroll.Content = _log;
        grid.Children.Add(Card(_scroll));
        var members = Card(_members);
        Grid.SetColumn(members, 2);
        grid.Children.Add(members);
        root.Children.Add(grid);
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)), BorderThickness = new Thickness(1), Child = root };

        _input.PreviewKeyDown += Input_PreviewKeyDown;
        _members.MouseDoubleClick += (_, _) =>
        {
            if (_members.SelectedItem is ListBoxItem { Tag: ChatProtocol.Participant p } && p.Person.Ip != desk.MyIp)
                desk.NewMemo().PresetRecipients.Add(p.Person.Copy());
        };
        PreviewKeyDown += Window_PreviewKeyDown;
        Loaded += (_, _) => _input.Focus();
        Closed += (_, _) => { if (!_session.IsHost) _session.Leave(); };
        Attach(session);
    }

    private static Border Card(UIElement child) => new()
    {
        CornerRadius = new CornerRadius(12),
        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x24, 0x28)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x31, 0x37)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(6),
        Child = child
    };

    private void Attach(ChatSession session)
    {
        _session = session;
        Title = $"대화 - {session.Title}";
        Chrome.SetTitle(_titleBar, $"대화 · {session.Title}");
        _lock.Visibility = session.IsHost ? Visibility.Visible : Visibility.Collapsed;
        _log.Children.Clear();
        foreach (var line in session.Lines) AddLine(line);
        session.LineAdded += line => { if (_session == session) AddLine(line); };
        session.MembersChanged += () => { if (_session == session) FillMembers(); };
        FillMembers();
    }

    public void Flash()
    {
        if (!IsActive) Native.FlashWindow(Native.Handle(this), true);
    }

    private void AddLine(ChatLine line)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 2), FontSize = 13 };
        if (line.Kind == ChatLineKind.System)
        {
            text.Text = line.Text;
            text.Foreground = (Brush)Application.Current.Resources["MutedTextBrush"];
            text.FontSize = 12;
        }
        else
        {
            text.Inlines.Add(new System.Windows.Documents.Run(line.Who + "  ")
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(line.Kind == ChatLineKind.Mine ? Color.FromRgb(0x7F, 0xB0, 0xFF) : Color.FromRgb(0xFF, 0xD4, 0x7A))
            });
            text.Inlines.Add(new System.Windows.Documents.Run(line.Text));
            text.ToolTip = line.At.ToString("HH:mm:ss");
            if (line.Kind == ChatLineKind.Other) Flash();
        }
        _log.Children.Add(text);
        _scroll.ScrollToEnd();
    }

    private void FillMembers()
    {
        _members.Items.Clear();
        foreach (var member in _session.Members)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = member.ChatName.Length > 0 ? member.ChatName : member.Person.Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new TextBlock { Text = member.Person.Ip, FontSize = 11.5, Foreground = (Brush)Application.Current.Resources["MutedTextBrush"] });
            _members.Items.Add(new ListBoxItem { Content = panel, Tag = member, ToolTip = $"{member.Person.Name} · 두 번 클릭: 메모 보내기" });
        }
    }

    private async Task SendAsync(string? text = null)
    {
        text ??= _input.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text == _input.Text) _input.Clear();
        await _session.SayAsync(text.Trim());
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = SendAsync(); }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is >= Key.F1 and <= Key.F12 && Keyboard.Modifiers == ModifierKeys.None)
        {
            string macro = _desk.Settings.ChatMacros[key - Key.F1];
            if (macro.Length > 0) _ = SendAsync(macro);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) Close();
    }

    private void Connect()
    {
        var picker = new ConnectDialog(this, _desk);
        if (picker.ShowDialog() != true || picker.Result is not { } person) return;
        if (!_session.IsHost) _session.Leave();
        var session = new GuestSession(_desk, person);
        Attach(session);
        _ = session.ConnectAsync();
    }

    private void ToggleLock()
    {
        if (_session is not HostSession host) return;
        host.Locked = !host.Locked;
        ((TextBlock)_lock.Content).Text = host.Locked ? "" : "";
        _lock.ToolTip = host.Locked ? "잠김: 새로 못 들어옴 (누르면 풀기)" : "대화방 잠그기";
        AddLine(new ChatLine(ChatLineKind.System, "", host.Locked ? ">>> 대화방을 잠갔습니다." : ">>> 대화방 잠금을 풀었습니다.", DateTime.Now));
    }

    private void SaveLog()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "텍스트 파일(*.txt)|*.txt", FileName = $"대화 {DateTime.Now:yyyyMMdd HHmm}.txt", Title = "갈무리 파일 저장" };
        if (dialog.ShowDialog(this) != true) return;
        var lines = new List<string> { $"<<<{DateTime.Now:yyyy-MM-dd HH:mm}에 기록>>>", "<<<참여자>>>" };
        lines.AddRange(_session.Members.Select(m => $"  {m.ChatName} ({m.Person.Name}, {m.Person.Ip})"));
        lines.Add("");
        lines.AddRange(_session.Lines.Select(l => l.Kind == ChatLineKind.System ? $"[{l.At:HH:mm:ss}] {l.Text}" : $"[{l.At:HH:mm:ss}] {l.Who}: {l.Text}"));
        try { File.WriteAllLines(dialog.FileName, lines, new System.Text.UTF8Encoding(true)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Dialogs.Info(this, "갈무리", error.Message); }
    }

    private void EditMacros()
    {
        var boxes = new TextBox[12];
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 12; i++)
        {
            boxes[i] = new TextBox { Text = _desk.Settings.ChatMacros[i], MaxLength = 250 };
            Dialogs.AddRow(grid, i, $"F{i + 1}", boxes[i]);
        }
        var dialog = new DialogWindow(this, "단축 문구", "", 420) { Body = grid };
        dialog.AddButton("취소", result: false);
        dialog.AddButton("저장", primary: true, result: true);
        if (dialog.ShowDialog() != true) return;
        _desk.Settings.ChatMacros = boxes.Select(b => b.Text).ToArray();
        _desk.Settings.Save();
    }
}

/// <summary>접속 변경: 주소록 사람 또는 IP 주소</summary>
internal sealed class ConnectDialog : DialogWindow
{
    public ConnectDialog(Window owner, Desk desk) : base(owner, "접속 변경", "", 420)
    {
        var book = desk.LoadAddressBook();
        var search = new TextBox { Tag = "이름 · IP" };
        var list = new ListBox { Height = 220, Margin = new Thickness(0, 8, 0, 0) };
        void Fill()
        {
            list.Items.Clear();
            foreach (var person in book.Search(search.Text).OrderBy(p => p.Name, StringComparer.CurrentCulture))
                list.Items.Add(new ListBoxItem { Content = $"{person.Label}   {person.Ip}", Tag = person });
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        var panel = new StackPanel();
        panel.Children.Add(search);
        panel.Children.Add(list);
        Body = panel;
        AddButton("취소", result: false);
        AddButton("들어가기", () =>
        {
            string text = search.Text.Trim();
            if (list.SelectedItem is ListBoxItem { Tag: Person person }) Result = person;
            else if (NetAddress.IsIPv4(text)) Result = new Person { Ip = text, Name = text };
            if (Result is not null) DialogResult = true;
        }, primary: true);
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: Person p }) { Result = p; DialogResult = true; } };
        Loaded += (_, _) => search.Focus();
    }

    public Person? Result { get; private set; }
}
