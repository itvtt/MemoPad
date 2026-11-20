using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MemoPad.Core;

namespace MemoPad.Ui;

/// <summary>메모 관리기: 폴더(모든 메모 · 내 메모 · 만든 폴더 · 휴지통) + 메모 목록 + 찾기</summary>
internal sealed class ManagerWindow : Window
{
    private const string AllKey = "\0all", TrashKey = "\0trash", RootKey = "";
    private readonly Desk _desk;
    private readonly ListBox _folders = new();
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended };
    private readonly TextBox _search = new() { Tag = "메모 찾기", Width = 220 };
    private readonly TextBlock _count = new() { Style = (Style)Application.Current.Resources["MutedTextStyle"], VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _emptyTrash;
    private readonly DispatcherTimer _reloadSoon = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _folder = AllKey;
    private Point _dragStart;

    public ManagerWindow(Desk desk)
    {
        _desk = desk;
        Chrome.Apply(this, resizable: true);
        Title = "메모 관리기 - MemoPad";
        Icon = App.IconImage;
        var bounds = desk.Settings.ManagerBounds;
        Width = bounds is { Length: 4 } ? bounds[2] : 820;
        Height = bounds is { Length: 4 } ? bounds[3] : 560;
        if (bounds is { Length: 4 }) { Left = bounds[0]; Top = bounds[1]; WindowStartupLocation = WindowStartupLocation.Manual; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MinWidth = 560;
        MinHeight = 360;

        var bar = Chrome.TitleBar(this, "메모 관리기", Icons.Folder, minMax: true);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var toolbar = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Chrome.IconButton(Icons.Add, "새 메모 (Ctrl+N)", () => desk.NewMemo()));
        actions.Children.Add(Chrome.IconButton(Icons.Pen, "새 그림", () => desk.NewMemo(MemoKind.Drawing)));
        actions.Children.Add(Separator());
        actions.Children.Add(Chrome.IconButton(Icons.Show, "바탕화면에 보이기 (Enter)", () => ForSelected(m => desk.Open(m))));
        actions.Children.Add(Chrome.IconButton(Icons.Hide, "숨기기", () => ForSelected(Hide)));
        actions.Children.Add(Chrome.IconButton(Icons.Grid, "바탕화면 메모 정렬", desk.Arrange));
        actions.Children.Add(Chrome.IconButton(Icons.Delete, "버리기 (Delete)", DeleteSelected));
        actions.Children.Add(Separator());
        actions.Children.Add(Chrome.IconButton("", "새 폴더", NewFolder));
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

        var footer = new DockPanel { Margin = new Thickness(18, 2, 16, 10) };
        _emptyTrash = Chrome.Pill("휴지통 비우기", EmptyTrash);
        _emptyTrash.Height = 28;
        DockPanel.SetDock(_emptyTrash, Dock.Right);
        footer.Children.Add(_emptyTrash);
        footer.Children.Add(_count);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var grid = new Grid { Margin = new Thickness(16, 0, 16, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(Card(_folders));
        var listCard = Card(_list);
        Grid.SetColumn(listCard, 2);
        grid.Children.Add(listCard);
        root.Children.Add(grid);
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)), BorderThickness = new Thickness(1), Child = root };

        _folders.SelectionChanged += (_, _) =>
        {
            if (_folders.SelectedItem is ListBoxItem { Tag: string key }) { _folder = key; FillList(); }
        };
        _folders.AllowDrop = true;
        _folders.Drop += Folders_Drop;
        _folders.MouseRightButtonUp += (_, e) => FolderMenu(e);
        _search.TextChanged += (_, _) => FillList();
        _list.MouseDoubleClick += (_, _) => ForSelected(m => desk.Open(m));
        _list.PreviewMouseMove += List_PreviewMouseMove;
        _list.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        _list.ContextMenuOpening += List_ContextMenuOpening;
        _list.ContextMenu = new ContextMenu();
        PreviewKeyDown += OnKey;
        _reloadSoon.Tick += (_, _) => { _reloadSoon.Stop(); Reload(); };
        Closing += (_, _) =>
        {
            if (WindowState == WindowState.Normal) desk.Settings.ManagerBounds = [Left, Top, Width, Height];
            desk.Settings.Save();
        };
        desk.Store.Removed += _ => _reloadSoon.Start();
        Reload();
    }

    private static Border Separator() => new() { Width = 1, Height = 18, Margin = new Thickness(6, 0, 6, 0), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)) };

    private static Border Card(UIElement child) => new()
    {
        CornerRadius = new CornerRadius(12),
        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x24, 0x28)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x31, 0x37)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(6),
        Child = child
    };

    public void ShowFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _search.Focus();
    }

    /// <summary>메모 하나가 바뀜: 목록에 있으면 그 줄만 (없으면 잠시 뒤 전부)</summary>
    public void Refresh(MemoData memo) => _reloadSoon.Start();

    public void Reload()
    {
        var live = _desk.Store.Live.ToList();
        _folders.Items.Clear();
        _folders.Items.Add(FolderRow("모든 메모", AllKey, live.Count, ""));
        _folders.Items.Add(FolderRow("내 메모", RootKey, live.Count(m => m.Folder == ""), Icons.Note));
        foreach (var folder in _desk.Store.Folders) _folders.Items.Add(FolderRow(folder.Name, folder.Id, live.Count(m => m.Folder == folder.Id), Icons.Folder));
        _folders.Items.Add(FolderRow("휴지통", TrashKey, _desk.Store.Trash.Count(), Icons.Delete));
        _folders.SelectedItem = _folders.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == _folder) ?? _folders.Items[0];
        FillList();
    }

    private static ListBoxItem FolderRow(string label, string key, int count, string glyph)
    {
        var row = new DockPanel();
        var number = new TextBlock { Text = count.ToString(), Foreground = (Brush)Application.Current.Resources["MutedTextBrush"], FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(number, Dock.Right);
        row.Children.Add(number);
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons.Font, FontSize = 13, Width = 24, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBA, 0xC2)) });
        row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = row, Tag = key };
    }

    private IEnumerable<MemoData> Current()
    {
        IEnumerable<MemoData> memos = _folder switch
        {
            TrashKey => _desk.Store.Trash,
            AllKey => _desk.Store.Live,
            _ => _desk.Store.Live.Where(m => m.Folder == _folder)
        };
        string text = _search.Text.Trim();
        if (text.Length > 0)
            memos = memos.Where(m => !m.Secured && (m.Text.Contains(text, StringComparison.CurrentCultureIgnoreCase) || (m.From?.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)));
        return _folder == TrashKey ? memos.OrderByDescending(m => m.DeletedAt) : memos.OrderByDescending(m => m.Modified);
    }

    private void FillList()
    {
        var selected = SelectedMemos().Select(m => m.Id).ToHashSet();
        _list.Items.Clear();
        foreach (var memo in Current())
        {
            var item = new ListBoxItem { Content = Row(memo), Tag = memo };
            _list.Items.Add(item);
            if (selected.Contains(memo.Id)) item.IsSelected = true;
        }
        _count.Text = $"메모 {_list.Items.Count}개" + (_folder != TrashKey ? $" · 바탕화면 {_desk.Windows.Count()}개" : "");
        _emptyTrash.Visibility = _folder == TrashKey && _list.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement Row(MemoData memo)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(MemoColors.Parse(memo.Color)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(dot);
        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        string first = memo.Secured ? "(보안 메모)" : memo.FirstLine;
        text.Children.Add(new TextBlock { Text = first, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        string second = memo.Secured ? "" : string.Join(" ", memo.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Skip(1)).Trim();
        if (second.Length > 0) text.Children.Add(new TextBlock { Text = second, FontSize = 12, Foreground = (Brush)Application.Current.Resources["MutedTextBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
        grid.Children.Add(text);
        var meta = new StackPanel { Margin = new Thickness(12, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(meta, 2);
        var kind = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        void Glyph(string g, string tip) => kind.Children.Add(new TextBlock { Text = g, FontFamily = Icons.Font, FontSize = 11, Margin = new Thickness(6, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBA, 0xC2)), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center });
        if (memo.Visible && !memo.Deleted) Glyph(Icons.Show, "바탕화면에 있음");
        if (memo.Alarm is { Done: false }) Glyph(Icons.Clock, $"알람 {memo.Alarm.At:M/d HH:mm}");
        if (memo.Attachments.Count > 0) Glyph(Icons.Attach, $"첨부 {memo.Attachments.Count}개");
        kind.Children.Add(new TextBlock { Text = memo.From is { } from && memo.Origin != MemoOrigin.Mine ? $"{memo.KindText} · {from.Label}" : memo.KindText, FontSize = 11.5, Margin = new Thickness(6, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBA, 0xC2)) });
        meta.Children.Add(kind);
        meta.Children.Add(new TextBlock { Text = Formats.Short(memo.Deleted ? memo.DeletedAt ?? memo.Modified : memo.Modified), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Right, Foreground = (Brush)Application.Current.Resources["MutedTextBrush"] });
        grid.Children.Add(meta);
        return grid;
    }

    private List<MemoData> SelectedMemos() => _list.SelectedItems.OfType<ListBoxItem>().Select(i => (MemoData)i.Tag).ToList();

    public void Select(MemoData memo)
    {
        if (_list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((MemoData)i.Tag).Id == memo.Id) is not { } item)
        {
            _folder = AllKey;
            _search.Clear();
            Reload();
            item = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((MemoData)i.Tag).Id == memo.Id);
        }
        if (item is null) return;
        _list.SelectedItem = item;
        item.BringIntoView();
    }

    private void ForSelected(Action<MemoData> action)
    {
        foreach (var memo in SelectedMemos())
        {
            if (memo.Deleted) continue;
            action(memo);
        }
        FillList();
    }

    private void Hide(MemoData memo)
    {
        if (_desk.WindowOf(memo) is { } window) _desk.HideMemo(window);
        else { memo.Visible = false; _desk.Save(memo); }
    }

    private void DeleteSelected()
    {
        var memos = SelectedMemos();
        if (memos.Count == 0) return;
        if (_folder == TrashKey)
        {
            if (!Dialogs.Ask(this, "완전히 지우기", $"고른 메모 {memos.Count}개를 완전히 지울까요? 되살릴 수 없습니다.")) return;
            foreach (var memo in memos) _desk.Store.Delete(memo);
        }
        else
        {
            foreach (var memo in memos)
            {
                if (_desk.WindowOf(memo) is { } window) { window.SaveNow(); window.Close(); }
                _desk.Store.MoveToTrash(memo, _desk.Settings.UseTrash);
            }
        }
        Reload();
    }

    private void Restore()
    {
        foreach (var memo in SelectedMemos().Where(m => m.Deleted)) _desk.Store.Restore(memo);
        Reload();
    }

    private void EmptyTrash()
    {
        var trash = _desk.Store.Trash.ToList();
        if (trash.Count == 0 || !Dialogs.Ask(this, "휴지통 비우기", $"휴지통에 있는 메모 {trash.Count}개를 모두 지울까요?")) return;
        foreach (var memo in trash) _desk.Store.Delete(memo);
        Reload();
    }

    private void MoveTo(IEnumerable<MemoData> memos, string folder)
    {
        foreach (var memo in memos)
        {
            if (memo.Deleted) _desk.Store.Restore(memo);
            memo.Folder = folder;
            _desk.Save(memo);
        }
        Reload();
    }

    private void NewFolder()
    {
        string? name = Dialogs.Input(this, "새 폴더", "폴더 이름");
        if (string.IsNullOrWhiteSpace(name)) return;
        _folder = _desk.Store.AddFolder(name).Id;
        Reload();
    }

    private void FolderMenu(MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(_folders, (DependencyObject)e.OriginalSource) as ListBoxItem;
        string key = item?.Tag as string ?? "";
        var folder = _desk.Store.Folders.FirstOrDefault(f => f.Id == key);
        var menu = new ContextMenu();
        menu.Items.Add(MemoWindow.Item("새 폴더…", NewFolder, ""));
        if (folder is not null)
        {
            menu.Items.Add(MemoWindow.Item("이름 바꾸기…", () =>
            {
                string? name = Dialogs.Input(this, "폴더 이름 바꾸기", "폴더 이름", folder.Name);
                if (!string.IsNullOrWhiteSpace(name)) { _desk.Store.RenameFolder(folder, name); Reload(); }
            }, Icons.Edit));
            menu.Items.Add(MemoWindow.Item("폴더 지우기", () =>
            {
                if (!Dialogs.Ask(this, "폴더 지우기", $"'{folder.Name}' 폴더를 지울까요? 안의 메모는 '내 메모'로 옮깁니다.")) return;
                _desk.Store.RemoveFolder(folder);
                _folder = RootKey;
                Reload();
            }, Icons.Delete));
        }
        if (key == TrashKey) menu.Items.Add(MemoWindow.Item("휴지통 비우기", EmptyTrash, Icons.Delete));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var memos = SelectedMemos();
        if (memos.Count == 0) { e.Handled = true; return; }
        var menu = new ContextMenu();
        if (_folder == TrashKey)
        {
            menu.Items.Add(MemoWindow.Item("되살리기", Restore, Icons.Restore));
            menu.Items.Add(MemoWindow.Item("완전히 지우기", DeleteSelected, Icons.Delete));
        }
        else
        {
            menu.Items.Add(MemoWindow.Item("바탕화면에 보이기", () => ForSelected(m => _desk.Open(m)), Icons.Show, "Enter"));
            menu.Items.Add(MemoWindow.Item("숨기기", () => ForSelected(Hide), Icons.Hide));
            menu.Items.Add(MemoWindow.Item("화면 가운데로 불러오기", () => ForSelected(CenterOnScreen), ""));
            var layer = MemoWindow.Sub("위치", "",
                MemoWindow.Item("항상 위", () => ForSelected(m => SetLayer(m, MemoLayer.Top)), check: memos.All(m => m.Layer == MemoLayer.Top)),
                MemoWindow.Item("보통", () => ForSelected(m => SetLayer(m, MemoLayer.Normal)), check: memos.All(m => m.Layer == MemoLayer.Normal)),
                MemoWindow.Item("항상 아래", () => ForSelected(m => SetLayer(m, MemoLayer.Bottom)), check: memos.All(m => m.Layer == MemoLayer.Bottom)));
            menu.Items.Add(layer);
            menu.Items.Add(MemoWindow.Item("접기 · 펼치기", () => ForSelected(m => { if (_desk.WindowOf(m) is { } w) w.ToggleCollapse(); else { m.Collapsed = !m.Collapsed; _desk.Save(m); } }), Icons.Minimize));
            menu.Items.Add(MemoWindow.Item("인쇄…", () => { if (memos.FirstOrDefault() is { } m) _desk.Open(m).Print(); }, gesture: "Ctrl+P"));
            menu.Items.Add(new Separator());
        }
        var move = MemoWindow.Sub("폴더로 옮기기", Icons.Folder, MemoWindow.Item("내 메모", () => MoveTo(memos, "")));
        foreach (var folder in _desk.Store.Folders) { var f = folder; move.Items.Add(MemoWindow.Item(f.Name, () => MoveTo(memos, f.Id))); }
        menu.Items.Add(move);
        if (_folder != TrashKey)
        {
            var discard = MemoWindow.Item("버리기", DeleteSelected, Icons.Delete, "Delete");
            discard.Style = (Style)Application.Current.Resources["DangerMenuItemStyle"];
            menu.Items.Add(discard);
        }
        _list.ContextMenu = menu;
    }

    private void SetLayer(MemoData memo, MemoLayer layer)
    {
        if (_desk.WindowOf(memo) is { } window) window.SetLayer(layer);
        else { memo.Layer = layer; _desk.Save(memo); }
    }

    private void CenterOnScreen(MemoData memo)
    {
        var work = Native.PrimaryWorkArea();
        var window = _desk.Open(memo);
        window.MoveTo((work.Left + work.Right - memo.Width) / 2, (work.Top + work.Bottom - memo.Height) / 2);
        window.BringToFront();
    }

    private void MoreMenu(Button anchor)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MemoWindow.Item("모두 보이기", _desk.ShowAll, Icons.Show, _desk.Hotkeys.Gesture(HotkeyAction.ShowAll)));
        menu.Items.Add(MemoWindow.Item("모두 숨기기", _desk.HideAll, Icons.Hide, _desk.Hotkeys.Gesture(HotkeyAction.HideAll)));
        menu.Items.Add(MemoWindow.Item("바탕화면 메모 정렬", _desk.Arrange, Icons.Grid));
        menu.Items.Add(new Separator());
        menu.Items.Add(MemoWindow.Item("정음 메모 가져오기…", ImportJungum, ""));
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ImportJungum()
    {
        var folders = JungumImport.DefaultFolders().ToList();
        int count = JungumImport.ImportAll(_desk, folders, out var scanned);
        Dialogs.Info(this, "정음 메모 가져오기", count > 0
            ? $"정음 메모패드의 메모 {count}개를 가져왔습니다 ('내 메모', 바탕화면에는 숨김)."
            : scanned.Count == 0 ? "정음 메모패드의 메모 폴더를 찾지 못했습니다." : "가져올 새 메모가 없습니다.\n찾아본 곳: " + string.Join("\n", scanned));
        Reload();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && e.Key != Key.Escape) return;
        switch (e.Key)
        {
            case Key.Delete: DeleteSelected(); e.Handled = true; break;
            case Key.Enter: if (_folder == TrashKey) Restore(); else ForSelected(m => _desk.Open(m)); e.Handled = true; break;
            case Key.Escape when _search.Text.Length > 0: _search.Clear(); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.N when Keyboard.Modifiers == ModifierKeys.Control: _desk.NewMemo(); e.Handled = true; break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control: _search.Focus(); e.Handled = true; break;
        }
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < 8 && Math.Abs(position.Y - _dragStart.Y) < 8) return;
        var memos = SelectedMemos();
        if (memos.Count == 0) return;
        DragDrop.DoDragDrop(_list, new DataObject("MemoPadMemos", memos.Select(m => m.Id.ToString()).ToArray()), DragDropEffects.Move);
    }

    private void Folders_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("MemoPadMemos") is not string[] ids) return;
        var target = ItemsControl.ContainerFromElement(_folders, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (target?.Tag is not string key || key == AllKey) return;
        var memos = ids.Select(id => _desk.Store.Find(Guid.Parse(id))).Where(m => m is not null).Select(m => m!).ToList();
        if (key == TrashKey)
        {
            foreach (var memo in memos)
            {
                if (_desk.WindowOf(memo) is { } window) { window.SaveNow(); window.Close(); }
                _desk.Store.MoveToTrash(memo, true);
            }
            Reload();
        }
        else MoveTo(memos, key);
    }
}
