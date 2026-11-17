using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemoPad.Core;
using MemoPad.Net;

namespace MemoPad.Ui;

/// <summary>사람 한 명 고치기 (이름 · ID · IP · E-Mail · 그룹)</summary>
internal static class PersonDialog
{
    public sealed record Edited(Person Person, HashSet<string> Groups);

    public static Edited? Edit(Window? owner, Person person, AddressBook book, string title)
    {
        var name = new TextBox { Text = person.Name };
        var id = new TextBox { Text = person.Id };
        var ip = new TextBox { Text = person.Ip, Tag = "예) 10.0.0.25" };
        var email = new TextBox { Text = person.Email, Tag = "(선택)" };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Dialogs.AddRow(grid, 0, "이름", name);
        Dialogs.AddRow(grid, 1, "ID", id);
        Dialogs.AddRow(grid, 2, "IP 주소", ip);
        Dialogs.AddRow(grid, 3, "E-Mail", email);
        var groups = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var group in book.Groups)
            groups.Children.Add(new CheckBox { Content = group.Name, IsChecked = group.Members.Contains(person.Ip), Margin = new Thickness(0, 4, 14, 4) });
        if (book.Groups.Count > 0) Dialogs.AddRow(grid, 4, "그룹", groups);
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["WarningBrush"], Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(error);
        var dialog = new DialogWindow(owner, title, Icons.People, 420) { Body = panel };
        dialog.AddButton("취소", result: false);
        dialog.AddButton("저장", () =>
        {
            string problem = !NetAddress.IsIPv4(ip.Text.Trim()) ? "IP 주소를 숫자로 입력하세요 (예: 10.0.0.25)."
                : name.Text.Trim().Length == 0 ? "이름을 입력하세요." : "";
            error.Text = problem;
            error.Visibility = problem.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (problem.Length == 0) dialog.DialogResult = true;
        }, primary: true);
        dialog.Loaded += (_, _) => name.Focus();
        if (dialog.ShowDialog() != true) return null;
        var edited = new Person { Name = name.Text.Trim(), Id = id.Text.Trim(), Ip = ip.Text.Trim(), Email = email.Text.Trim() };
        var chosen = groups.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToHashSet();
        return new Edited(edited, chosen);
    }
}

/// <summary>주소록: 왼쪽 그룹, 오른쪽 사람. 사람을 그룹으로 끌어 넣기, 불러오기 · 내보내기(CSV)</summary>
internal sealed class AddressBookWindow : Window
{
    private readonly Desk _desk;
    private AddressBook _book;
    private readonly ListBox _groups = new();
    private readonly ListBox _people = new();
    private readonly TextBox _search = new() { Tag = "찾기", Width = 200 };
    private readonly TextBlock _where = new() { Style = (Style)Application.Current.Resources["MutedTextStyle"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };

    public AddressBookWindow(Desk desk)
    {
        _desk = desk;
        _book = desk.LoadAddressBook();
        Chrome.Apply(this, resizable: true);
        Title = "주소록 - MemoPad";
        Width = 760;
        Height = 540;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = App.IconImage;

        var bar = Chrome.TitleBar(this, "주소록", Icons.People, minMax: true);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var toolbar = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Chrome.IconButton(Icons.Add, "사람 추가", AddPerson));
        actions.Children.Add(Chrome.IconButton("", "그룹 추가", AddGroup));
        actions.Children.Add(Chrome.IconButton(Icons.Edit, "고치기", EditSelected));
        actions.Children.Add(Chrome.IconButton(Icons.Delete, "지우기", DeleteSelected));
        actions.Children.Add(new Border { Width = 1, Height = 18, Margin = new Thickness(6, 0, 6, 0), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)) });
        actions.Children.Add(Chrome.IconButton(Icons.Status, "상태 확인", desk.ShowStatus));
        actions.Children.Add(Chrome.IconButton(Icons.Send, "메모 보내기", SendToSelected));
        actions.Children.Add(Chrome.IconButton(Icons.Chat, "대화", ChatWithSelected));
        var more = Chrome.IconButton("", "더 보기", () => { });
        more.Click += (_, _) => MoreMenu(more);
        actions.Children.Add(more);
        DockPanel.SetDock(actions, Dock.Left);
        toolbar.Children.Add(actions);
        DockPanel.SetDock(_search, Dock.Right);
        toolbar.Children.Add(_search);
        toolbar.Children.Add(new Border());
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var footer = new Border { Padding = new Thickness(18, 4, 18, 10), Child = _where };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var grid = new Grid { Margin = new Thickness(16, 0, 16, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(Card(_groups));
        var peopleCard = Card(_people);
        Grid.SetColumn(peopleCard, 2);
        grid.Children.Add(peopleCard);
        root.Children.Add(grid);
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)), BorderThickness = new Thickness(1), Child = root };

        _groups.SelectionChanged += (_, _) => FillPeople();
        _search.TextChanged += (_, _) => FillPeople();
        _people.MouseDoubleClick += (_, _) => EditSelected();
        _people.SelectionMode = SelectionMode.Extended;
        _people.PreviewMouseMove += People_PreviewMouseMove;
        _groups.AllowDrop = true;
        _groups.Drop += Groups_Drop;
        _groups.MouseDoubleClick += (_, _) => RenameGroup();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && !(Keyboard.FocusedElement is TextBox)) { DeleteSelected(); e.Handled = true; }
            else if (e.Key == Key.Escape) Close();
        };
        Activated += (_, _) => Reload();
        Reload();
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

    private void Reload()
    {
        string selected = (_groups.SelectedItem as ListBoxItem)?.Tag as string ?? "";
        _book = _desk.LoadAddressBook();
        _groups.Items.Clear();
        _groups.Items.Add(GroupRow("전체", "", _book.People.Count));
        foreach (var group in _book.Groups) _groups.Items.Add(GroupRow(group.Name, group.Name, _book.MembersOf(group).Count()));
        _groups.SelectedItem = _groups.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? _groups.Items[0];
        _where.Text = $"주소록 파일: {_desk.Settings.AddressBookPath}";
        FillPeople();
    }

    private static ListBoxItem GroupRow(string label, string key, int count)
    {
        var row = new DockPanel();
        var number = new TextBlock { Text = count.ToString(), Foreground = (Brush)Application.Current.Resources["MutedTextBrush"], FontSize = 12 };
        DockPanel.SetDock(number, Dock.Right);
        row.Children.Add(number);
        row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        return new ListBoxItem { Content = row, Tag = key, ToolTip = key.Length > 0 ? "두 번 클릭: 이름 바꾸기 · 사람을 끌어다 놓으면 이 그룹에 넣기" : null };
    }

    private AddressGroup? SelectedGroup =>
        (_groups.SelectedItem as ListBoxItem)?.Tag is string { Length: > 0 } name ? _book.Groups.FirstOrDefault(g => g.Name == name) : null;

    private void FillPeople()
    {
        var group = SelectedGroup;
        IEnumerable<Person> people = group is null ? _book.People : _book.MembersOf(group);
        string text = _search.Text.Trim();
        if (text.Length > 0) people = people.Where(p => AddressBook.Matches(p, text));
        _people.Items.Clear();
        foreach (var person in people.OrderBy(p => p.Name, StringComparer.CurrentCulture))
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            void Cell(string value, int column, bool strong = false)
            {
                var cell = new TextBlock { Text = value, FontSize = strong ? 13.5 : 12.5, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) };
                if (!strong) cell.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBA, 0xC2));
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
            Cell(person.Name, 0, true);
            Cell(person.Id, 1);
            Cell(person.Ip, 2);
            Cell(person.Email, 3);
            _people.Items.Add(new ListBoxItem { Content = grid, Tag = person });
        }
    }

    private List<Person> SelectedPeople => _people.SelectedItems.OfType<ListBoxItem>().Select(i => (Person)i.Tag).ToList();

    private void Commit()
    {
        _desk.SaveAddressBook(_book, this);
        Reload();
    }

    private void AddPerson()
    {
        _book = _desk.LoadAddressBook();
        var edited = PersonDialog.Edit(this, new Person(), _book, "사람 추가");
        if (edited is null) return;
        if (_book.FindByIp(edited.Person.Ip) is not null && !Dialogs.Ask(this, "사람 추가", "이미 같은 IP 주소로 등록된 사람이 있습니다.\n새 정보로 바꿀까요?")) return;
        var person = _book.Upsert(edited.Person);
        ApplyGroups(person, edited.Groups);
        if (SelectedGroup is { } group && !group.Members.Contains(person.Ip)) group.Members.Add(person.Ip);
        Commit();
    }

    private void EditSelected()
    {
        if (SelectedPeople.FirstOrDefault() is not { } person) return;
        var edited = PersonDialog.Edit(this, person.Copy(), _book, "고치기");
        if (edited is null) return;
        _book = _desk.LoadAddressBook();
        var current = _book.FindByIp(person.Ip);
        if (current is null) { current = _book.Upsert(edited.Person); }
        else
        {
            if (edited.Person.Ip != current.Ip) _book.Rename(current, edited.Person.Ip);
            current.Name = edited.Person.Name;
            current.Id = edited.Person.Id;
            current.Email = edited.Person.Email;
        }
        ApplyGroups(current, edited.Groups);
        Commit();
    }

    private void ApplyGroups(Person person, HashSet<string> chosen)
    {
        foreach (var group in _book.Groups)
        {
            bool member = chosen.Contains(group.Name);
            if (member && !group.Members.Contains(person.Ip)) group.Members.Add(person.Ip);
            if (!member) group.Members.Remove(person.Ip);
        }
    }

    private void DeleteSelected()
    {
        var people = SelectedPeople;
        if (people.Count > 0)
        {
            var group = SelectedGroup;
            string question = group is null ? $"고른 {people.Count}명을 주소록에서 지울까요?" : $"고른 {people.Count}명을 '{group.Name}' 그룹에서 뺄까요?";
            if (!Dialogs.Ask(this, "지우기", question)) return;
            _book = _desk.LoadAddressBook();
            foreach (var person in people)
            {
                if (group is null) { if (_book.FindByIp(person.Ip) is { } p) _book.Remove(p); }
                else _book.Groups.FirstOrDefault(g => g.Name == group.Name)?.Members.Remove(person.Ip);
            }
            Commit();
        }
        else if (SelectedGroup is { } group && Dialogs.Ask(this, "그룹 지우기", $"'{group.Name}' 그룹을 지울까요? (사람은 남습니다)"))
        {
            _book = _desk.LoadAddressBook();
            _book.Groups.RemoveAll(g => g.Name == group.Name);
            Commit();
        }
    }

    private void AddGroup()
    {
        string? name = Dialogs.Input(this, "그룹 추가", "그룹 이름");
        if (string.IsNullOrWhiteSpace(name)) return;
        _book = _desk.LoadAddressBook();
        if (_book.Groups.Any(g => g.Name == name)) { Dialogs.Info(this, "그룹 추가", "이미 같은 이름의 그룹이 있습니다."); return; }
        _book.AddGroup(name);
        Commit();
    }

    private void RenameGroup()
    {
        if (SelectedGroup is not { } group) return;
        string? name = Dialogs.Input(this, "그룹 이름 바꾸기", "그룹 이름", group.Name);
        if (string.IsNullOrWhiteSpace(name) || name == group.Name) return;
        _book = _desk.LoadAddressBook();
        if (_book.Groups.FirstOrDefault(g => g.Name == group.Name) is { } current) current.Name = name;
        Commit();
    }

    private Point _dragStart;

    private void People_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragStart = e.GetPosition(this); return; }
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < 8 && Math.Abs(position.Y - _dragStart.Y) < 8) return;
        var people = SelectedPeople;
        if (people.Count == 0) return;
        DragDrop.DoDragDrop(_people, new DataObject("MemoPadPeople", people.Select(p => p.Ip).ToArray()), DragDropEffects.Copy);
    }

    private void Groups_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("MemoPadPeople") is not string[] ips) return;
        var target = (e.OriginalSource as DependencyObject) is { } source ? ItemsControl.ContainerFromElement(_groups, source) as ListBoxItem : null;
        if (target?.Tag is not string { Length: > 0 } name) return;
        _book = _desk.LoadAddressBook();
        var group = _book.Groups.FirstOrDefault(g => g.Name == name);
        if (group is null) return;
        foreach (string ip in ips.Where(ip => !group.Members.Contains(ip))) group.Members.Add(ip);
        Commit();
    }

    /// <summary>새 메모를 열고 받을 사람을 미리 넣어 둠 (쓰고 Ctrl+S)</summary>
    private void SendToSelected()
    {
        var people = SelectedPeople;
        if (people.Count == 0) return;
        var window = _desk.NewMemo();
        window.PresetRecipients.AddRange(people.Select(p => p.Copy()));
    }

    private void ChatWithSelected()
    {
        if (SelectedPeople.FirstOrDefault() is { } person) _desk.OpenChat(person);
    }

    private void MoreMenu(Button anchor)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MemoWindow.Item("CSV 로 내보내기…", Export, Icons.Save));
        menu.Items.Add(MemoWindow.Item("CSV 불러오기…", Import, ""));
        menu.Items.Add(MemoWindow.Item("다른 주소록 합치기…", MergeOther, Icons.Folder));
        menu.Items.Add(new Separator());
        menu.Items.Add(MemoWindow.Item("주소록 폴더 바꾸기…", () => _desk.ShowSettings(), Icons.Settings));
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "주소록.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, _book.ToCsv(), new System.Text.UTF8Encoding(true)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Dialogs.Info(this, "내보내기", error.Message); }
    }

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "CSV (*.csv)|*.csv|모든 파일|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            _book = _desk.LoadAddressBook();
            int count = _book.MergeCsv(JuEncoding.Detect(bytes).GetString(bytes).TrimStart('﻿'));
            Commit();
            Dialogs.Info(this, "불러오기", $"{count}명을 더하거나 고쳤습니다.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Dialogs.Info(this, "불러오기", error.Message); }
    }

    /// <summary>다른 폴더의 주소록(addressbook.json)을 합침</summary>
    private void MergeOther()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "MemoPad 주소록 (addressbook.json)|addressbook.json|모든 파일|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var other = AddressBook.Load(dialog.FileName);
        _book = _desk.LoadAddressBook();
        foreach (var person in other.People) _book.Upsert(person.Copy());
        foreach (var group in other.Groups)
        {
            var mine = _book.Groups.FirstOrDefault(g => g.Name == group.Name) ?? _book.AddGroup(group.Name);
            foreach (string ip in group.Members.Where(ip => !mine.Members.Contains(ip))) mine.Members.Add(ip);
        }
        Commit();
        Dialogs.Info(this, "합치기", $"{other.People.Count}명, 그룹 {other.Groups.Count}개를 합쳤습니다.");
    }
}
