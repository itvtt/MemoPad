using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MemoPad.Ui;

/// <summary>어두운 작은 창: 제목 표시줄 + 내용 + 아래 단추 줄. Esc = 닫기, Enter = 기본 단추</summary>
internal class DialogWindow : Window
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ContentControl _body = new();
    private Button? _primary;

    public DialogWindow(Window? owner, string title, string glyph = "", double width = 420)
    {
        Chrome.Apply(this, resizable: false);
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        Owner = owner is { IsLoaded: true, IsVisible: true } ? owner : null;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = Owner is null;
        Topmost = Owner?.Topmost ?? true;
        TitleBar = Chrome.TitleBar(this, title, glyph, minMax: false);
        var root = new DockPanel();
        DockPanel.SetDock(TitleBar, Dock.Top);
        root.Children.Add(TitleBar);
        var footer = new Border { Padding = new Thickness(22, 4, 22, 18), Child = _buttons };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        _body.Margin = new Thickness(22, 16, 22, 14);
        root.Children.Add(_body);
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)), BorderThickness = new Thickness(1), Child = root };
        PreviewKeyDown += OnKey;
    }

    public Grid TitleBar { get; }

    public object Body { get => _body.Content; set => _body.Content = value; }

    public Button AddButton(string text, Action? action = null, bool primary = false, bool? result = null)
    {
        var button = Chrome.Pill(text, () =>
        {
            action?.Invoke();
            if (result is { } r) { DialogResult = r; }
        }, primary);
        _buttons.Children.Add(button);
        if (primary) _primary = button;
        return button;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && _primary is { IsEnabled: true } && !(Keyboard.FocusedElement is TextBox { AcceptsReturn: true }))
        {
            _primary.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            e.Handled = true;
        }
    }

    public static TextBlock Text(string text, bool muted = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = muted ? 12 : 13.5,
        Foreground = muted ? (Brush)Application.Current.Resources["MutedTextBrush"] : (Brush)Application.Current.Resources["TextBrush"],
        LineHeight = 20
    };
}

internal static class Dialogs
{
    /// <summary>시험용: 정해 두면 창을 띄우지 않고 이 답 (제목 · 내용을 받음)</summary>
    internal static Func<string, string, bool>? TestAnswer;

    public static bool Ask(Window? owner, string title, string message, string yes = "예", string no = "아니요")
    {
        if (TestAnswer is { } answer) return answer(title, message);
        var dialog = new DialogWindow(owner, title) { Body = DialogWindow.Text(message) };
        dialog.AddButton(no, result: false);
        dialog.AddButton(yes, primary: true, result: true);
        return dialog.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string message)
    {
        var dialog = new DialogWindow(owner, title) { Body = DialogWindow.Text(message) };
        dialog.AddButton("확인", primary: true, result: true);
        dialog.ShowDialog();
    }

    /// <summary>글 한 줄 받기 (취소 = null)</summary>
    public static string? Input(Window? owner, string title, string label, string initial = "", string hint = "")
    {
        var box = new TextBox { Text = initial, Tag = hint, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel();
        if (label.Length > 0) panel.Children.Add(DialogWindow.Text(label));
        panel.Children.Add(box);
        var dialog = new DialogWindow(owner, title) { Body = panel };
        dialog.AddButton("취소", result: false);
        dialog.AddButton("확인", primary: true, result: true);
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }

    /// <summary>여러 줄 글 받기 (회람 의견 등)</summary>
    public static string? MultiLine(Window? owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, VerticalContentAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel();
        panel.Children.Add(DialogWindow.Text(label));
        panel.Children.Add(box);
        var dialog = new DialogWindow(owner, title, width: 460) { Body = panel };
        dialog.AddButton("취소", result: false);
        dialog.AddButton("확인", primary: true, result: true);
        dialog.Loaded += (_, _) => box.Focus();
        return dialog.ShowDialog() == true ? box.Text : null;
    }

    public static string? Password(Window? owner, string title, string label)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(DialogWindow.Text(label));
        panel.Children.Add(box);
        var dialog = new DialogWindow(owner, title, "", 360) { Body = panel };
        dialog.AddButton("취소", result: false);
        dialog.AddButton("확인", primary: true, result: true);
        dialog.Loaded += (_, _) => box.Focus();
        return dialog.ShowDialog() == true ? box.Password : null;
    }

    /// <summary>표 넣기: 행 · 열 수</summary>
    public static (int Rows, int Columns)? TableSize(Window? owner)
    {
        var rows = new TextBox { Text = "3", Width = 80 };
        var columns = new TextBox { Text = "3", Width = 80 };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        AddRow(grid, 0, "행", rows);
        AddRow(grid, 1, "열", columns);
        var dialog = new DialogWindow(owner, "표 넣기", width: 300) { Body = grid };
        dialog.AddButton("취소", result: false);
        dialog.AddButton("넣기", primary: true, result: true);
        dialog.Loaded += (_, _) => { rows.Focus(); rows.SelectAll(); };
        if (dialog.ShowDialog() != true) return null;
        return int.TryParse(rows.Text, out int r) && int.TryParse(columns.Text, out int c) && r is > 0 and <= 50 && c is > 0 and <= 20 ? (r, c) : null;
    }

    public static void AddRow(Grid grid, int row, string label, UIElement field)
    {
        while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition());
        var text = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["FieldLabelStyle"], Margin = new Thickness(0, 4, 14, 4) };
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        if (field is FrameworkElement element) element.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
    }

    /// <summary>투명도 (0.2~1) 고르기. 움직이는 동안 preview 로 바로 보여 줌</summary>
    public static double? Opacity(Window? owner, double current, Action<double> preview)
    {
        var slider = new Slider { Minimum = 0.2, Maximum = 1, Value = current, SmallChange = 0.05, LargeChange = 0.1 };
        var label = DialogWindow.Text($"{current:P0}");
        slider.ValueChanged += (_, e) => { label.Text = $"{e.NewValue:P0}"; preview(e.NewValue); };
        var panel = new StackPanel();
        panel.Children.Add(label);
        panel.Children.Add(slider);
        var dialog = new DialogWindow(owner, "투명도", width: 320) { Body = panel };
        dialog.AddButton("취소", result: false);
        dialog.AddButton("확인", primary: true, result: true);
        bool ok = dialog.ShowDialog() == true;
        if (!ok) preview(current);
        return ok ? slider.Value : null;
    }
}
