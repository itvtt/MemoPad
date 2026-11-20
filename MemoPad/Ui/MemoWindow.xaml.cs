using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MemoPad.Core;

namespace MemoPad.Ui;

/// <summary>
/// 바탕화면 메모 한 장. 머리를 끌어 옮기고 가장자리로 크기를 바꿈. 고칠 때마다 잠시 뒤 저장.
/// 메뉴는 MemoWindow.Menus.cs, 그림 메모는 MemoWindow.Ink.cs
/// </summary>
public partial class MemoWindow : Window
{
    /// <summary>접은 메모 사각형 한 변 (DIP). 정음 메모패드는 22 픽셀</summary>
    private const double CollapsedSize = 24;
    private readonly Desk _desk;
    private readonly DispatcherTimer _saveTimer;
    private bool _loading, _dirty, _unlocked, _placed;

    public MemoWindow(Desk desk, MemoData memo)
    {
        InitializeComponent();
        _desk = desk;
        Memo = memo;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) => SaveNow();

        var settings = desk.Settings;
        Editor.Document = NewDocument(settings);
        Editor.VerticalScrollBarVisibility = settings.ScrollBar ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        Editor.TextChanged += (_, _) => { if (!_loading) MarkDirty(); };
        Editor.ContextMenuOpening += Editor_ContextMenuOpening;
        DataObject.AddPastingHandler(Editor, OnPasting);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewDragOver += OnPreviewDragOver;
        PreviewDrop += OnPreviewDrop;
        SourceInitialized += OnSourceInitialized;
        Activated += (_, _) =>
        {
            ApplyActive(true);
            // 창을 앞으로 가져오면 바로 본문에 쓸 수 있게
            if (!IsKeyboardFocusWithin && !Memo.Collapsed) Dispatcher.BeginInvoke(() => (Memo.Kind == MemoKind.Drawing ? (IInputElement)Ink : Editor).Focus());
        };
        Deactivated += (_, _) => OnDeactivated();
        Closing += (_, _) => SaveNow();
        Header.MouseEnter += (_, _) => ApplyTitle();
        Header.MouseLeave += (_, _) => ApplyTitle();

        LoadContent();
        ApplyLook();
    }

    public MemoData Memo { get; }

    /// <summary>주소록에서 "메모 보내기"로 연 메모: 보내기 창에 미리 넣을 사람</summary>
    public List<Person> PresetRecipients { get; } = [];

    public static FlowDocument NewDocument(AppSettings settings) => new()
    {
        PagePadding = new Thickness(0),
        FontFamily = new FontFamily(settings.FontFamily + ", Malgun Gothic"),
        FontSize = settings.FontSize,
        LineHeight = double.NaN
    };

    // ── 내용 ─────────────────────────────────────────────────

    private void LoadContent()
    {
        _loading = true;
        try
        {
            if (Memo.Kind == MemoKind.Drawing) SetupInk();
            else if (Memo.Rtf.Length > 0) RtfText.Load(Editor.Document, Memo.Rtf);
            Editor.IsReadOnly = Memo.ReadOnly;
            ApplyAttachments();
        }
        finally { _loading = false; }
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>지금 내용을 메모 데이터에 넣고 저장</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _dirty = false;
        if (Memo.Kind == MemoKind.Drawing) Memo.Ink = InkData();
        else
        {
            Memo.Rtf = RtfText.Save(Editor.Document);
            Memo.Text = RtfText.Plain(Editor.Document);
        }
        Memo.Modified = DateTime.Now;
        _desk.Save(Memo);
        ApplyTitle();
        ApplyStatus();
    }

    /// <summary>보낼 본문 RTF (그림 메모는 그림 한 장짜리 RTF)</summary>
    public string RtfForSending()
    {
        SaveNow();
        return Memo.Kind == MemoKind.Drawing ? RtfText.FromImage(RenderInk()) : RtfText.Save(Editor.Document);
    }

    /// <summary>본문을 바꿈 (회람 의견 · 따옴표 등)</summary>
    public void ReplaceRtf(string rtf)
    {
        if (Memo.Kind == MemoKind.Drawing) return;
        RtfText.Load(Editor.Document, rtf);
        _dirty = true;
        SaveNow();
    }

    public bool HasBody => Memo.Kind == MemoKind.Drawing ? Ink.Strokes.Count > 0 : RtfText.Plain(Editor.Document).Trim().Length > 0;

    // ── 창 자리 ───────────────────────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Native.AddExStyle(this, Native.WS_EX_TOOLWINDOW, Native.WS_EX_APPWINDOW);
        HwndSource.FromHwnd(Native.Handle(this))?.AddHook(WndProc);
        PlaceFromMemo();
        ApplyBorder(IsActive);
        ApplyOpacity();
        ApplyLayer();
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void PlaceFromMemo()
    {
        if (Memo.Width <= 0 || Memo.Height <= 0)
        {
            Memo.Width = (int)Math.Round((Memo.Kind == MemoKind.Drawing ? Math.Max(_desk.Settings.DefaultWidth, 380) : _desk.Settings.DefaultWidth) * Scale);
            Memo.Height = (int)Math.Round(_desk.Settings.DefaultHeight * Scale);
        }
        if (Memo.X == int.MinValue || Memo.Y == int.MinValue) _desk.PlaceNew(Memo);
        // 화면 밖이면 (모니터를 뺀 경우) 주 모니터 안으로
        if (Memo.Collapsed)
        {
            int size = SquarePixels;
            var area = Native.WorkAreaAt(Memo.X + Memo.Width - size / 2, Memo.Y + size / 2);
            Memo.X = Math.Clamp(Memo.X + Memo.Width - size, area.Left, area.Right - size) + size - Memo.Width;
            Memo.Y = Math.Clamp(Memo.Y, area.Top, area.Bottom - size);
        }
        else
        {
            var work = Native.WorkAreaAt(Memo.X + 20, Memo.Y + 10);
            Memo.X = Math.Clamp(Memo.X, work.Left - Memo.Width + 60, work.Right - 60);
            Memo.Y = Math.Clamp(Memo.Y, work.Top, work.Bottom - 30);
        }
        _placed = true;
        ApplyBounds();
    }

    /// <summary>접은 메모 사각형 한 변 (픽셀)</summary>
    private int SquarePixels => (int)Math.Round(CollapsedSize * Scale);

    /// <summary>
    /// 메모 자리(Memo.X · Y · Width · Height)는 늘 펼친 메모 기준. 접으면 그 오른쪽 위 모서리에 작은 사각형
    /// (정음 메모패드처럼 접기 단추 자리), 사각형을 옮기면 펼칠 메모도 따라감
    /// </summary>
    private void ApplyBounds()
    {
        if (!_placed) return;
        int x = Memo.X, y = Memo.Y, width = Memo.Width, height = Memo.Height;
        if (Memo.Collapsed)
        {
            width = height = SquarePixels;
            x = Memo.X + Memo.Width - width;
        }
        Native.SetWindowPos(Native.Handle(this), IntPtr.Zero, x, y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>창의 지금 자리를 메모에 (펼친 상태면 크기도, 접은 상태면 사각형 자리로 메모 자리를)</summary>
    private void CaptureBounds()
    {
        var rect = Native.WindowRect(this);
        if (Memo.Collapsed)
        {
            Memo.X = rect.Right - Memo.Width;
            Memo.Y = rect.Top;
        }
        else
        {
            Memo.X = rect.Left;
            Memo.Y = rect.Top;
            Memo.Width = rect.Right - rect.Left;
            Memo.Height = rect.Bottom - rect.Top;
        }
        _desk.Save(Memo);
    }

    /// <summary>창의 왼쪽 위를 (x, y) 로 (바탕화면 메모 정렬). 접은 메모는 사각형이 그 자리에</summary>
    public void MoveTo(int x, int y)
    {
        if (Memo.Collapsed) x += SquarePixels - Memo.Width;
        Memo.X = x;
        Memo.Y = y;
        ApplyBounds();
        _desk.Save(Memo);
    }

    /// <summary>펼칠 때 메모가 화면 밖으로 나가지 않게</summary>
    private void KeepOnScreen()
    {
        var work = Native.WorkAreaAt(Memo.X + Memo.Width - 10, Memo.Y + 10);
        Memo.X = Math.Clamp(Memo.X, work.Left, Math.Max(work.Left, work.Right - Memo.Width));
        Memo.Y = Math.Clamp(Memo.Y, work.Top, Math.Max(work.Top, work.Bottom - Memo.Height));
    }

    public Native.RECT PixelBounds => Native.WindowRect(this);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_EXITSIZEMOVE) CaptureBounds();
        else if (msg == Native.WM_WINDOWPOSCHANGING && Memo.Layer == MemoLayer.Bottom)
        {
            var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            if ((pos.flags & Native.SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = Native.HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, true);
            }
        }
        return IntPtr.Zero;
    }

    // ── 모양 ─────────────────────────────────────────────────

    /// <summary>색 · 층 · 잠금 · 상태줄 · 접기 · 보안 · 제목을 메모 데이터대로</summary>
    public void ApplyLook()
    {
        var color = MemoColors.Parse(Memo.Color);
        Root.Background = new SolidColorBrush(color);
        Background = Root.Background;
        PinButton.Content = Memo.Layer == MemoLayer.Top ? Icons.Pinned : Icons.Pin;
        PinButton.ToolTip = Memo.Layer == MemoLayer.Top ? "항상 위 끄기" : "항상 위";
        Editor.IsReadOnly = Memo.ReadOnly;
        Ink.EditingMode = Memo.ReadOnly ? InkCanvasEditingMode.None : Ink.EditingMode;
        WindowChromeInfo.ResizeBorderThickness = new Thickness(Memo.Locked || Memo.Collapsed ? 0 : 5);
        ApplyCollapse();
        ApplySecure();
        ApplyTitle();
        ApplyStatus();
        if (_placed)
        {
            ApplyBorder(IsActive);
            ApplyOpacity();
            ApplyLayer();
        }
    }

    private void ApplyCollapse()
    {
        bool open = !Memo.Collapsed;
        bool drawing = Memo.Kind == MemoKind.Drawing;
        Editor.Visibility = open && !drawing ? Visibility.Visible : Visibility.Collapsed;
        InkHost.Visibility = open && drawing ? Visibility.Visible : Visibility.Collapsed;
        InkBar.Visibility = open && drawing && !Memo.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
        AttachmentPanel.Visibility = open && Memo.Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Visibility = open && Memo.StatusBar ? Visibility.Visible : Visibility.Collapsed;
        Header.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CollapsedFace.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        // 접은 사각형은 아주 작으므로 최소 크기를 풂
        MinWidth = open ? 120 : 0;
        MinHeight = open ? 60 : 0;
        if (!open) ApplyFace();
        ApplyBounds();
    }

    /// <summary>접은 사각형 안: 글 메모는 글줄, 그림 메모는 선 하나, 잠긴 보안 메모는 자물쇠 (오른쪽 아래는 접힌 귀)</summary>
    private void ApplyFace()
    {
        bool locked = Memo.Secured && !_unlocked;
        FaceGlyph.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        FaceGlyph.Text = locked ? Icons.Lock : "";
        FaceLines.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        FaceLines.Data = Geometry.Parse(Memo.Kind == MemoKind.Drawing
            ? "M 5,16 C 7.5,6 10.5,18 13,10 S 17,9 18,7"
            : "M 5.5,7.5 H 15.5 M 5.5,11.5 H 17 M 5.5,15.5 H 11.5");
        CollapsedFace.ToolTip = (locked ? "보안 메모" : Memo.FirstLine) + "\n두 번 클릭하면 펼칩니다";
    }

    private void ApplySecure()
    {
        bool cover = Memo.Secured && !_unlocked && !Memo.Collapsed;
        SecureCover.Visibility = cover ? Visibility.Visible : Visibility.Collapsed;
        if (cover)
        {
            Editor.Visibility = Visibility.Hidden;
            InkHost.Visibility = Visibility.Hidden;
            AttachmentPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyTitle()
    {
        string title = Memo.Collapsed && !(Memo.Secured && !_unlocked) ? Memo.FirstLine : Memo.Origin switch
        {
            MemoOrigin.Received => $"{Memo.From?.Label} · {Formats.Title(Memo.ReceivedAt ?? Memo.Created)}",
            MemoOrigin.CirculationReceived => $"{Memo.Circulation?.Originator.Label ?? Memo.From?.Label}의 회람",
            MemoOrigin.CirculationResult => "회람 결과",
            MemoOrigin.CirculationOriginal => "회람 원본",
            MemoOrigin.Sent => "보냄 → " + string.Join(", ", Memo.SentTo.Select(p => p.Label)),
            _ => Formats.Title(Memo.Created)
        };
        TitleText.Text = title;
        bool show = _desk.Settings.AlwaysShowTitle || IsActive || Header.IsMouseOver || Memo.Collapsed;
        TitleText.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        HeaderButtons.Opacity = IsActive || Header.IsMouseOver ? 1 : 0.55;
        MenuButton.Opacity = HeaderButtons.Opacity;

        StateIcons.Children.Clear();
        void State(string glyph, string tip) => StateIcons.Children.Add(new TextBlock { Text = glyph, Margin = new Thickness(3, 0, 0, 0), ToolTip = tip });
        if (Memo.Alarm is { Done: false } alarm) State(Icons.Clock, $"알람 {alarm.At:M/d HH:mm}");
        if (Memo.Locked) State(Icons.Lock, "잠김 (옮기기 · 크기 바꾸기 안 됨)");
        if (Memo.ReadOnly) State(Icons.ReadOnly, "읽기 전용");
        if (Memo.Secured) State("", "보안 메모");
        Title = $"MemoPad - {Memo.FirstLine}";
        if (Memo.Collapsed) ApplyFace();
    }

    private void ApplyStatus()
    {
        if (!Memo.StatusBar) return;
        var parts = new List<string>();
        if (Memo.From is { } from && Memo.Origin is MemoOrigin.Received or MemoOrigin.CirculationReceived or MemoOrigin.CirculationResult)
            parts.Add($"{from.Label}({from.Ip}) · 받음 {Formats.Full(Memo.ReceivedAt ?? Memo.Created)}");
        else parts.Add($"만듦 {Formats.Full(Memo.Created)}");
        parts.Add($"고침 {Formats.Short(Memo.Modified)}");
        if (Memo.Alarm is { Done: false } alarm) parts.Add($"알람 {alarm.At:M/d HH:mm}");
        StatusText.Text = string.Join(" · ", parts);
    }

    private void ApplyActive(bool active)
    {
        ApplyBorder(active);
        ApplyTitle();
    }

    private void ApplyBorder(bool active)
    {
        if (!_placed) return;
        var color = MemoColors.Parse(Memo.Color);
        bool hide = _desk.Settings.HideInactiveBorder && !active;
        Native.RoundCorners(this, hide ? null : MemoColors.Shade(color, active ? 0.32 : 0.18), small: true);
    }

    private void ApplyOpacity()
    {
        if (!_placed) return;
        double opacity = Memo.Opacity;
        if (Memo.Collapsed) opacity = Math.Min(opacity, _desk.Settings.CollapsedOpacity);
        Native.SetOpacity(this, opacity);
    }

    public void PreviewOpacity(double opacity) => Native.SetOpacity(this, opacity);

    private void ApplyLayer()
    {
        if (!_placed) return;
        Topmost = Memo.Layer == MemoLayer.Top;
        if (Memo.Layer == MemoLayer.Bottom)
            Native.SetWindowPos(Native.Handle(this), Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void OnDeactivated()
    {
        SaveNow();
        ApplyActive(false);
        if (Memo.Secured && _unlocked)
        {
            _unlocked = false;
            ApplySecure();
            ApplyTitle();
        }
    }

    /// <summary>앞으로 가져옴. activate = false 면 쓰던 창의 포커스를 뺏지 않음 (받은 메모 · 알람은 깜빡임)</summary>
    public void BringToFront(bool flash = false, bool activate = true)
    {
        if (!IsVisible)
        {
            ShowActivated = activate;
            Show();
            ShowActivated = true;
        }
        if (Memo.Collapsed && flash) ToggleCollapse();
        var handle = Native.Handle(this);
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
        if (Memo.Layer != MemoLayer.Top)
            Native.SetWindowPos(handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (activate) Activate();
        if (flash) _ = Blink();
    }

    private async Task Blink()
    {
        var normal = MemoColors.Parse(Memo.Color);
        var bright = MemoColors.Shade(normal, 0.25);
        for (int i = 0; i < 3; i++)
        {
            Root.Background = new SolidColorBrush(bright);
            await Task.Delay(180);
            Root.Background = new SolidColorBrush(normal);
            await Task.Delay(180);
        }
    }

    // ── 머리 ─────────────────────────────────────────────────

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleCollapse(); return; }
        if (Memo.Locked) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        CaptureBounds();
    }

    private void Header_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenMemoMenu(null);
        e.Handled = true;
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e) => OpenMemoMenu(MenuButton);

    private void PinButton_Click(object sender, RoutedEventArgs e) => SetLayer(Memo.Layer == MemoLayer.Top ? MemoLayer.Normal : MemoLayer.Top);

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => ToggleCollapse();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => _desk.Discard(this);

    /// <summary>접기 ↔ 펼치기. 접으면 오른쪽 위 모서리의 작은 사각형, 펼치면 그 사각형 자리를 오른쪽 위로 해서 열림</summary>
    public void ToggleCollapse()
    {
        SaveNow();
        CaptureBounds();
        Memo.Collapsed = !Memo.Collapsed;
        if (!Memo.Collapsed) KeepOnScreen();
        ApplyLook();
        _desk.Save(Memo);
        if (!Memo.Collapsed && IsActive) (Memo.Kind == MemoKind.Drawing ? (IInputElement)Ink : Editor).Focus();
    }

    public void SetLayer(MemoLayer layer)
    {
        Memo.Layer = layer;
        _desk.Save(Memo);
        ApplyLook();
    }

    // ── 보안 ─────────────────────────────────────────────────

    private void SecureCover_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Unlock();

    /// <summary>암호를 물어 보안 메모를 엶 (창을 떠나면 다시 가려짐)</summary>
    public bool Unlock()
    {
        if (!Memo.Secured || _unlocked) return true;
        if (_desk.Settings.HasPassword)
        {
            string? password = Dialogs.Password(this, "보안 메모", "암호가 필요한 메모입니다.");
            if (password is null) return false;
            if (!_desk.Settings.CheckPassword(password))
            {
                Dialogs.Info(this, "보안 메모", "암호가 맞지 않습니다.");
                return false;
            }
        }
        _unlocked = true;
        ApplyLook();
        Activate();
        return true;
    }

    // ── 첨부 ─────────────────────────────────────────────────

    public void ApplyAttachments()
    {
        AttachmentPanel.Children.Clear();
        foreach (string name in Memo.Attachments)
        {
            var chip = new Border
            {
                Margin = new Thickness(0, 2, 4, 0),
                Padding = new Thickness(6, 2, 8, 3),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)),
                Cursor = Cursors.Hand,
                ToolTip = $"{name}\n누르면 열기 · 오른쪽 클릭 = 저장 · 빼기",
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = Icons.Attach, FontFamily = Icons.Font, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = name, Margin = new Thickness(4, 0, 0, 0), FontSize = 11.5, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0, 0, 0)) }
                    }
                }
            };
            string file = name;
            chip.MouseLeftButtonUp += (_, _) => OpenAttachment(file);
            chip.MouseRightButtonUp += (_, e) => { AttachmentMenu(file).IsOpen = true; e.Handled = true; };
            AttachmentPanel.Children.Add(chip);
        }
        AttachmentPanel.Visibility = Memo.Attachments.Count > 0 && !Memo.Collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenAttachment(string name)
    {
        string path = _desk.Store.AttachmentPath(Memo, name);
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Dialogs.Info(this, "첨부", $"열 수 없습니다: {error.Message}");
        }
    }

    public void AttachFiles(IEnumerable<string> paths)
    {
        foreach (string path in paths.Where(File.Exists))
        {
            try { _desk.Store.AddAttachment(Memo, path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Dialogs.Info(this, "파일 첨부", $"{Path.GetFileName(path)}: {error.Message}");
            }
        }
        _desk.Save(Memo);
        ApplyAttachments();
        ApplyTitle();
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = Memo.ReadOnly ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || Memo.ReadOnly) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            // 그림 파일 하나를 글 메모 본문에 놓으면 그림 넣기, 아니면 첨부
            if (Memo.Kind == MemoKind.Text && files.Length == 1 && IsImageFile(files[0]) && Editor.IsMouseOver && Keyboard.Modifiers != ModifierKeys.Shift)
                InsertImageFile(files[0]);
            else AttachFiles(files);
        }
    }

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";

    // ── 열쇠 ─────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.System && e.SystemKey == Key.F4) { e.Handled = true; _desk.Discard(this); return; }
        if (Memo.Collapsed && mods == ModifierKeys.None && e.Key is Key.Enter or Key.Space) { e.Handled = true; ToggleCollapse(); return; }
        if (e.Key == Key.F4 && mods == ModifierKeys.None) { e.Handled = true; ConvertTypo(); return; }
        if (mods == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.N: e.Handled = true; _desk.NewMemo(); return;
                case Key.S when _desk.Networked: e.Handled = true; _ = _desk.SendAsync(this, SendKind.Send); return;
                case Key.F when _desk.Networked: e.Handled = true; _ = _desk.SendAsync(this, SendKind.Forward); return;
                case Key.P: e.Handled = true; Print(); return;
                case Key.Y when _desk.Networked && Memo.From is not null: e.Handled = true; _ = _desk.SendAsync(this, SendKind.Reply); return;
                case Key.Y when Memo.Kind == MemoKind.Drawing: e.Handled = true; InkRedo(); return;
                case Key.Z when Memo.Kind == MemoKind.Drawing: e.Handled = true; InkUndo(); return;
                case Key.D when Memo.Kind == MemoKind.Text: e.Handled = true; InsertText(Formats.Date(DateTime.Now, _desk.Settings.DateFormat)); return;
                case Key.T when Memo.Kind == MemoKind.Text: e.Handled = true; InsertText(Formats.Time(DateTime.Now, _desk.Settings.TimeFormat)); return;
                case Key.M when Memo.Kind == MemoKind.Text: e.Handled = true; InsertImage(); return;
                case Key.Add: e.Handled = true; EditingCommands.IncreaseIndentation.Execute(null, Editor); return;
                case Key.Subtract: e.Handled = true; EditingCommands.DecreaseIndentation.Execute(null, Editor); return;
            }
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.V && Memo.Kind == MemoKind.Text) { e.Handled = true; PastePlain(); }
    }

    // ── 편집 도우미 ───────────────────────────────────────────

    public void InsertText(string text)
    {
        if (Editor.IsReadOnly) return;
        Editor.Selection.Text = text;
        Editor.CaretPosition = Editor.Selection.End;
        Editor.Selection.Select(Editor.CaretPosition, Editor.CaretPosition);
        Editor.Focus();
    }

    public void PastePlain()
    {
        if (Editor.IsReadOnly || !Clipboard.ContainsText()) return;
        InsertText(Clipboard.GetText().Replace("\r\n", "\r"));
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        // 그림만 있는 클립보드 (화면 캡처 등): 그림으로 넣기
        if (e.DataObject.GetDataPresent(DataFormats.Rtf) || e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        if (!e.DataObject.GetDataPresent(DataFormats.Bitmap)) return;
        e.CancelCommand();
        if (Clipboard.GetImage() is { } image) InsertImageSource(image);
    }

    /// <summary>한/영 오타 변환: 고른 글자, 없으면 커서 앞 낱말</summary>
    public void ConvertTypo()
    {
        if (Memo.Kind != MemoKind.Text || Editor.IsReadOnly) return;
        if (Editor.Selection.IsEmpty)
        {
            var start = Editor.CaretPosition;
            var position = start;
            while (true)
            {
                var back = position.GetNextInsertionPosition(LogicalDirection.Backward);
                if (back is null) break;
                string ch = new TextRange(back, position).Text;
                if (ch.Length == 0 || char.IsWhiteSpace(ch[0])) break;
                position = back;
            }
            Editor.Selection.Select(position, start);
        }
        string text = Editor.Selection.Text;
        if (text.Length == 0) return;
        if (text.Length > 256) { Dialogs.Info(this, "한/영 오타 변환", "고른 글자가 너무 깁니다 (256자까지)."); return; }
        Editor.Selection.Text = Hangul.ConvertTypo(text);
        Editor.CaretPosition = Editor.Selection.End;
        Editor.Selection.Select(Editor.CaretPosition, Editor.CaretPosition);
    }

    public void InsertImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "그림 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif|모든 파일|*.*", Title = "그림 넣기" };
        if (dialog.ShowDialog(this) == true) InsertImageFile(dialog.FileName);
    }

    private void InsertImageFile(string path)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            InsertImageSource(image);
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException)
        {
            Dialogs.Info(this, "그림 넣기", $"그림을 읽을 수 없습니다: {error.Message}");
        }
    }

    /// <summary>그림을 본문에 (RTF 로 저장 · 전송되도록 RTF 조각으로 넣음)</summary>
    private void InsertImageSource(System.Windows.Media.Imaging.BitmapSource image)
    {
        if (Editor.IsReadOnly) return;
        // 메모 폭에 맞춰 줄임
        double maxWidth = Math.Max(80, Editor.ActualWidth - 30) * Scale;
        if (image.PixelWidth > maxWidth)
        {
            double ratio = maxWidth / image.PixelWidth;
            image = new System.Windows.Media.Imaging.TransformedBitmap(image, new ScaleTransform(ratio, ratio));
        }
        string rtf = RtfText.FromImage(image);
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(rtf));
        Editor.Selection.Load(stream, DataFormats.Rtf);
        Editor.CaretPosition = Editor.Selection.End;
        MarkDirty();
    }

    public void InsertTable()
    {
        if (Editor.IsReadOnly || Dialogs.TableSize(this) is not { } size) return;
        var table = new Table { CellSpacing = 0, BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(0.5) };
        for (int c = 0; c < size.Columns; c++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (int r = 0; r < size.Rows; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < size.Columns; c++)
                row.Cells.Add(new TableCell(new Paragraph()) { BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(0.5), Padding = new Thickness(3, 1, 3, 1) });
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        var paragraph = Editor.CaretPosition.Paragraph;
        if (paragraph is not null && paragraph.Parent is FlowDocument or Section or ListItem or TableCell)
        {
            var blocks = paragraph.Parent switch
            {
                FlowDocument d => d.Blocks,
                Section s => s.Blocks,
                ListItem l => l.Blocks,
                TableCell t => t.Blocks,
                _ => Editor.Document.Blocks
            };
            blocks.InsertAfter(paragraph, table);
        }
        else Editor.Document.Blocks.Add(table);
        MarkDirty();
    }
}
