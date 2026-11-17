using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MemoPad.Core;
using MemoPad.Net;

namespace MemoPad.Ui;

/// <summary>사용자 상태 확인: 주소록 사람마다 있음 · 부재중 · 꺼져 있음과 반응 시간</summary>
internal sealed class StatusWindow : Window
{
    private readonly Desk _desk;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended };
    private readonly Dictionary<string, (PeerStatus? Status, DateTime? At)> _results = [];
    private CancellationTokenSource? _running;
    private readonly TextBlock _summary = new() { Style = (Style)Application.Current.Resources["MutedTextStyle"], VerticalAlignment = VerticalAlignment.Center };

    public StatusWindow(Desk desk)
    {
        _desk = desk;
        Chrome.Apply(this, resizable: true);
        Title = "사용자 상태 확인 - MemoPad";
        Width = 640;
        Height = 500;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = App.IconImage;

        var bar = Chrome.TitleBar(this, "사용자 상태 확인", Icons.Status, minMax: true);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var tools = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Chrome.Pill("전체 확인", () => _ = CheckAsync(null), primary: true));
        buttons.Children.Add(Chrome.Pill("선택 확인", () => _ = CheckAsync(Selected())));
        buttons.Children.Add(Chrome.Pill("파일로 저장…", SaveToFile));
        ((Button)buttons.Children[0]).Margin = new Thickness(0);
        DockPanel.SetDock(buttons, Dock.Left);
        tools.Children.Add(buttons);
        _summary.Margin = new Thickness(14, 0, 0, 0);
        tools.Children.Add(_summary);
        DockPanel.SetDock(tools, Dock.Top);
        root.Children.Add(tools);

        var card = new Border
        {
            Margin = new Thickness(16, 0, 16, 16),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x24, 0x28)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x31, 0x37)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Child = _list
        };
        root.Children.Add(card);
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)), BorderThickness = new Thickness(1), Child = root };

        _list.MouseDoubleClick += (_, _) =>
        {
            var people = Selected();
            if (people.Count == 0) return;
            var window = _desk.NewMemo();
            window.PresetRecipients.AddRange(people);
        };
        Closed += (_, _) => _running?.Cancel();
        Loaded += (_, _) => _ = CheckAsync(null);
        Fill();
    }

    private List<Person> Selected() => _list.SelectedItems.OfType<ListBoxItem>().Select(i => (Person)i.Tag).ToList();

    private void Fill()
    {
        var people = _desk.LoadAddressBook().People.OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList();
        var selected = Selected().Select(p => p.Ip).ToHashSet();
        _list.Items.Clear();
        foreach (var person in people)
        {
            _results.TryGetValue(person.Ip, out var result);
            var grid = new Grid();
            foreach (double star in new[] { 1.4, 1, 0.8, 1.1, 1 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(star, GridUnitType.Star) });
            var (text, color) = result.Status switch
            {
                null => (result.At is null ? "" : "확인 중…", Color.FromRgb(0x9A, 0x9A, 0xA2)),
                { State: PeerState.Present } => ("● 있음", Color.FromRgb(0x4C, 0xD7, 0x87)),
                { State: PeerState.Absent } => ("● 부재중", Color.FromRgb(0xFF, 0xC5, 0x6B)),
                { State: PeerState.Off } => ("○ 꺼져 있음", Color.FromRgb(0x9A, 0x9A, 0xA2)),
                _ => ("○ 응답 없음", Color.FromRgb(0xFF, 0x8A, 0x80))
            };
            void Cell(string value, int column, Color? foreground = null, bool strong = false)
            {
                var cell = new TextBlock { Text = value, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0), FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal };
                if (foreground is { } c) cell.Foreground = new SolidColorBrush(c);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
            Cell(person.Label, 0, strong: true);
            Cell(text, 1, color);
            Cell(result.Status is { State: PeerState.Present or PeerState.Absent } s ? $"{s.Milliseconds} ms" : "", 2, Color.FromRgb(0xB8, 0xBA, 0xC2));
            Cell(person.Ip, 3, Color.FromRgb(0xB8, 0xBA, 0xC2));
            Cell(person.Id, 4, Color.FromRgb(0xB8, 0xBA, 0xC2));
            var item = new ListBoxItem { Content = grid, Tag = person, ToolTip = "두 번 클릭: 메모 보내기" };
            _list.Items.Add(item);
            if (selected.Contains(person.Ip)) item.IsSelected = true;
        }
        int present = _results.Values.Count(r => r.Status?.State == PeerState.Present);
        int absent = _results.Values.Count(r => r.Status?.State == PeerState.Absent);
        _summary.Text = people.Count == 0 ? "주소록에 사람이 없습니다." : $"{people.Count}명 · 있음 {present} · 부재중 {absent}";
    }

    private async Task CheckAsync(List<Person>? only)
    {
        _running?.Cancel();
        var cancel = _running = new CancellationTokenSource();
        var people = only ?? _desk.LoadAddressBook().People;
        foreach (var person in people) _results[person.Ip] = (null, DateTime.Now);
        Fill();
        using var gate = new SemaphoreSlim(16);
        await Task.WhenAll(people.Select(async person =>
        {
            await gate.WaitAsync(cancel.Token).ConfigureAwait(true);
            try
            {
                var status = await MemoService.QueryStatusAsync(person.Ip, cancel.Token);
                _results[person.Ip] = (status, DateTime.Now);
                if (!cancel.IsCancellationRequested) Fill();
            }
            catch (OperationCanceledException) { }
            finally { gate.Release(); }
        }));
    }

    private void SaveToFile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "텍스트 (*.txt)|*.txt|CSV (*.csv)|*.csv", FileName = $"사용자 상태 {DateTime.Now:yyyyMMdd HHmm}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        var lines = new List<string> { $"수신인 상태 확인 기록 · 일시 : {DateTime.Now:yyyy-MM-dd HH:mm:ss}", "이름,상태,ID,IP 주소,E-Mail,반응시간" };
        foreach (var person in _desk.LoadAddressBook().People)
        {
            _results.TryGetValue(person.Ip, out var result);
            lines.Add(string.Join(",", person.Name, result.Status?.Text ?? "", person.Id, person.Ip, person.Email, result.Status is { } s ? $"{s.Milliseconds}ms" : ""));
        }
        try { File.WriteAllLines(dialog.FileName, lines, new System.Text.UTF8Encoding(true)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Dialogs.Info(this, "파일로 저장", error.Message); }
    }
}

/// <summary>회람 상태 조회: 대상마다 받음 · 전달 · 없음, 없는 사람 다시 보내기</summary>
internal sealed class CirculationStatusWindow : DialogWindow
{
    public CirculationStatusWindow(Desk desk, MemoWindow owner, Circulation circulation) : base(owner, "회람 상태", Icons.Circulate, 440)
    {
        var list = new StackPanel();
        int index = 1;
        foreach (var target in circulation.Targets)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var state = new TextBlock { Text = Desk.CirculationStateText(target.State), FontWeight = FontWeights.SemiBold, Foreground = target.State == Desk.CirculationMissed ? (Brush)Application.Current.Resources["WarningBrush"] : (Brush)Application.Current.Resources["TextBrush"] };
            DockPanel.SetDock(state, Dock.Right);
            row.Children.Add(state);
            row.Children.Add(new TextBlock { Text = $"{index++}. {target.Person.Label}  ({target.Person.Ip})" });
            list.Children.Add(row);
        }
        Body = list;
        bool pending = circulation.Targets.Any(t => t.State is Desk.CirculationMissed);
        if (pending && owner.Memo.Origin is MemoOrigin.CirculationResult or MemoOrigin.CirculationOriginal)
            AddButton("없는 사람 다시 보내기", () => _ = desk.ResumeCirculationAsync(owner), result: true);
        AddButton("확인", primary: true, result: true);
    }
}
