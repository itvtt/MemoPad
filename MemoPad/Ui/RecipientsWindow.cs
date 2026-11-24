using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MemoPad.Core;
using MemoPad.Net;

namespace MemoPad.Ui;

/// <summary>받을 사람 지정 (보내기 · 전달 · 회람): 이름 입력 · 주소록 · 그룹 · 최근, 오른쪽에 받을 사람 (회람은 순서대로)</summary>
internal sealed class RecipientsWindow : DialogWindow
{
    private readonly Desk _desk;
    private readonly SendKind _kind;
    private AddressBook _book;
    private readonly TextBox _search = new() { Tag = "이름 · ID · 초성 · IP 입력 후 Enter" };
    private readonly ListBox _people = new() { Height = 300 };
    private readonly ListBox _chosen = new() { Height = 262 };
    private readonly WrapPanel _groups = new() { Margin = new Thickness(0, 8, 0, 6) };
    private readonly Button _send;
    private string _group = "";

    public RecipientsWindow(Desk desk, Window owner, SendKind kind)
        : base(owner, kind switch { SendKind.Circulate => "회람 대상", SendKind.Forward => "전달할 사람", _ => "받을 사람" }, Icons.People, 680)
    {
        _desk = desk;
        _kind = kind;
        _book = desk.LoadAddressBook();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        left.Children.Add(_search);
        left.Children.Add(_groups);
        left.Children.Add(Panel(_people));
        grid.Children.Add(left);

        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Height = 34 };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tools, Dock.Right);
        tools.Children.Add(Chrome.IconButton(Icons.Up, "위로", () => MoveChosen(-1), 9.5));
        tools.Children.Add(Chrome.IconButton(Icons.Down, "아래로", () => MoveChosen(1), 9.5));
        tools.Children.Add(Chrome.IconButton(Icons.Close, "빼기 (Delete)", RemoveChosen, 9));
        head.Children.Add(tools);
        head.Children.Add(new TextBlock { Text = kind == SendKind.Circulate ? "회람 순서" : "받을 사람", Style = (Style)Application.Current.Resources["FieldLabelStyle"], FontWeight = FontWeights.SemiBold });
        right.Children.Add(head);
        right.Children.Add(Panel(_chosen));
        right.Children.Add(new TextBlock
        {
            Text = kind == SendKind.Circulate ? "위에서부터 차례로 돌고, 마지막 사람이 전달하면 결과가 돌아옵니다." : "주소록에서 두 번 클릭하거나 Enter.",
            Style = (Style)Application.Current.Resources["MutedTextStyle"], Margin = new Thickness(2, 8, 0, 0)
        });
        grid.Children.Add(right);
        Body = grid;

        var bookButton = AddButton("주소록…", () =>
        {
            desk.ShowAddressBook();
        });
        bookButton.Margin = new Thickness(0, 0, 0, 0);
        AddButton("취소", result: false);
        _send = AddButton(kind == SendKind.Circulate ? "회람 돌리기" : "보내기", Accept, primary: true);
        _send.IsEnabled = false;

        _search.TextChanged += (_, _) => Fill();
        _search.PreviewKeyDown += Search_PreviewKeyDown;
        _people.MouseDoubleClick += (_, _) => AddSelected();
        _people.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { AddSelected(); e.Handled = true; } };
        _chosen.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete) { RemoveChosen(); e.Handled = true; } };
        _chosen.MouseDoubleClick += (_, _) => RemoveChosen();
        Activated += (_, _) => { _book = desk.LoadAddressBook(); BuildGroups(); Fill(); };
        Loaded += (_, _) => _search.Focus();
        BuildGroups();
        Fill();
        if (owner is MemoWindow memo) foreach (var person in memo.PresetRecipients) Add(person);
    }

    public List<Person> Result { get; } = [];

    private static Border Panel(UIElement child) => new()
    {
        CornerRadius = new CornerRadius(10),
        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x24, 0x28)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x31, 0x37)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(4),
        Child = child
    };

    private void BuildGroups()
    {
        _groups.Children.Clear();
        void Chip(string label, string key)
        {
            var chip = new RadioButton { Content = label, Style = (Style)Application.Current.Resources["SegmentStyle"], GroupName = "groups", IsChecked = key == _group, Margin = new Thickness(0, 0, 4, 4), Height = 26 };
            chip.Checked += (_, _) => { _group = key; Fill(); };
            _groups.Children.Add(chip);
        }
        Chip("전체", "");
        if (_desk.Settings.RecentRecipients.Count > 0) Chip("최근", "\0recent");
        foreach (var group in _book.Groups) Chip(group.Name, group.Name);
    }

    private IEnumerable<Person> Source()
    {
        if (_group == "\0recent")
            return _desk.Settings.RecentRecipients.Select(ip => _book.FindByIp(ip) ?? new Person { Ip = ip, Name = ip });
        if (_group.Length > 0 && _book.Groups.FirstOrDefault(g => g.Name == _group) is { } group) return _book.MembersOf(group);
        return _book.People.OrderBy(p => p.Name, StringComparer.CurrentCulture);
    }

    private void Fill()
    {
        string text = _search.Text.Trim();
        var source = Source();
        if (text.Length > 0) source = source.Where(p => AddressBook.Matches(p, text));
        _people.Items.Clear();
        foreach (var person in source) _people.Items.Add(Row(person));
        if (_people.Items.Count > 0) _people.SelectedIndex = 0;
    }

    private static ListBoxItem Row(Person person)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = person.Label, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { person.Id, person.Ip }.Where(s => s.Length > 0)), FontSize = 11.5, Foreground = (Brush)Application.Current.Resources["MutedTextBrush"] });
        return new ListBoxItem { Content = panel, Tag = person };
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _people.Items.Count > 0)
        {
            _people.Focus();
            (_people.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, _people.SelectedIndex)) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string text = _search.Text.Trim();
            if (text.Length == 0) { if (_chosen.Items.Count > 0) Accept(); return; }
            if (_people.SelectedItem is ListBoxItem { Tag: Person person }) Add(person);
            else if (NetAddress.IsIPv4(text)) Add(new Person { Ip = text, Name = text });
            _search.Clear();
        }
    }

    private void AddSelected()
    {
        if (_people.SelectedItem is ListBoxItem { Tag: Person person }) Add(person);
    }

    private void Add(Person person)
    {
        if (_chosen.Items.OfType<ListBoxItem>().Any(i => ((Person)i.Tag).Ip == person.Ip)) return;
        if (person.Ip == _desk.MyIp && _kind == SendKind.Circulate) return;
        _chosen.Items.Add(Row(person));
        _send.IsEnabled = true;
        _send.Content = (_kind == SendKind.Circulate ? "회람 돌리기" : "보내기") + $" ({_chosen.Items.Count})";
    }

    private void RemoveChosen()
    {
        int index = _chosen.SelectedIndex;
        if (index < 0) return;
        _chosen.Items.RemoveAt(index);
        if (_chosen.Items.Count > 0) _chosen.SelectedIndex = Math.Min(index, _chosen.Items.Count - 1);
        _send.IsEnabled = _chosen.Items.Count > 0;
        _send.Content = (_kind == SendKind.Circulate ? "회람 돌리기" : "보내기") + (_chosen.Items.Count > 0 ? $" ({_chosen.Items.Count})" : "");
    }

    private void MoveChosen(int delta)
    {
        int index = _chosen.SelectedIndex, target = index + delta;
        if (index < 0 || target < 0 || target >= _chosen.Items.Count) return;
        var item = _chosen.Items[index];
        _chosen.Items.RemoveAt(index);
        _chosen.Items.Insert(target, item);
        _chosen.SelectedIndex = target;
    }

    private void Accept()
    {
        Result.Clear();
        Result.AddRange(_chosen.Items.OfType<ListBoxItem>().Select(i => ((Person)i.Tag).Copy()));
        if (Result.Count == 0) return;
        DialogResult = true;
    }
}
