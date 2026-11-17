using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MemoPad.Core;

namespace MemoPad.Ui;

public partial class MemoWindow
{
    internal static MenuItem Item(string header, Action action, string icon = "", string gesture = "", bool enabled = true, bool check = false)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
        if (icon.Length > 0) item.Icon = new TextBlock { Text = icon };
        else if (check) item.Icon = new TextBlock { Text = "" };
        item.Click += (_, _) => action();
        return item;
    }

    internal static MenuItem Sub(string header, string icon, params object[] items)
    {
        var item = new MenuItem { Header = header };
        if (icon.Length > 0) item.Icon = new TextBlock { Text = icon };
        foreach (var child in items) item.Items.Add(child);
        return item;
    }

    // ── 메모 메뉴 (≡ · 머리 오른쪽 클릭) ─────────────────────────

    private void OpenMemoMenu(UIElement? anchor)
    {
        SaveNow();
        var menu = new ContextMenu();
        bool received = Memo.From is not null;
        bool text = Memo.Kind == MemoKind.Text;
        var items = menu.Items;

        items.Add(Item("새 메모", () => _desk.NewMemo(), Icons.Add, "Ctrl+N"));
        items.Add(Item("새 그림", () => _desk.NewMemo(MemoKind.Drawing), Icons.Pen));
        items.Add(Item("메모 관리기", () => _desk.ShowManager(Memo), Icons.Folder));
        items.Add(new Separator());

        if (_desk.Settings.UseNetwork)
        {
            items.Add(Item("보내기…", () => _ = _desk.SendAsync(this, SendKind.Send), Icons.Send, "Ctrl+S"));
            if (received) items.Add(Item("답장", () => _ = _desk.SendAsync(this, SendKind.Reply), Icons.Reply, "Ctrl+Y"));
            items.Add(Item("전달…", () => _ = _desk.SendAsync(this, SendKind.Forward), Icons.Forward, "Ctrl+F"));
            var circulation = Sub("회람", Icons.Circulate,
                Item("회람 돌리기…", () => _ = _desk.SendAsync(this, SendKind.Circulate)));
            if (Memo.Origin == MemoOrigin.CirculationReceived && Memo.Circulation is { Passed: false })
                circulation.Items.Add(Item("회람 전달", () => _ = _desk.PassCirculationAsync(this)));
            if (Memo.Circulation is not null && Memo.Origin is MemoOrigin.CirculationOriginal or MemoOrigin.CirculationResult)
                circulation.Items.Add(Item("회람 상태 조회…", () => _desk.ShowCirculationStatus(this)));
            items.Add(circulation);
            if (received)
                items.Add(Sub("보낸 사람", Icons.People,
                    Item("보낸 사람 정보", () => _desk.ShowSenderInfo(this)),
                    Item("주소록에 추가…", () => _desk.AddSenderToAddressBook(this)),
                    Item("보낸 사람과 대화", () => _desk.OpenChat(Memo.From))));
            items.Add(new Separator());
        }

        items.Add(Item("알람…", () => _desk.EditAlarm(this), Icons.Clock));
        items.Add(ColorMenu());
        items.Add(Sub("옵션", Icons.Settings,
            Item("항상 위", () => SetLayer(Memo.Layer == MemoLayer.Top ? MemoLayer.Normal : MemoLayer.Top), check: Memo.Layer == MemoLayer.Top),
            Item("항상 아래", () => SetLayer(Memo.Layer == MemoLayer.Bottom ? MemoLayer.Normal : MemoLayer.Bottom), check: Memo.Layer == MemoLayer.Bottom),
            new Separator(),
            Item("잠그기 (자리 고정)", () => Toggle(m => m.Locked = !m.Locked), check: Memo.Locked),
            Item("읽기 전용", () => Toggle(m => m.ReadOnly = !m.ReadOnly), check: Memo.ReadOnly),
            Item("보안 메모 (암호)", ToggleSecure, check: Memo.Secured),
            new Separator(),
            Item("상태줄", () => Toggle(m => m.StatusBar = !m.StatusBar), check: Memo.StatusBar),
            Item("투명도…", ChangeOpacity)));
        items.Add(Sub("파일", Icons.Save,
            Item("파일 첨부…", AttachWithDialog, Icons.Attach, enabled: !Memo.ReadOnly),
            new Separator(),
            Item("다른 파일 불러오기…", ImportFile, enabled: text && !Memo.ReadOnly),
            Item("파일로 저장…", ExportFile, enabled: text),
            Item("그림으로 저장…", SaveImage),
            new Separator(),
            Item("인쇄…", Print, "", "Ctrl+P")));
        items.Add(new Separator());
        items.Add(Item("숨기기", () => _desk.HideMemo(this), Icons.Hide));
        var discard = Item("버리기", () => _desk.Discard(this), Icons.Delete, "Alt+F4");
        discard.Style = (Style)Application.Current.Resources["DangerMenuItemStyle"];
        items.Add(discard);

        menu.PlacementTarget = anchor ?? this;
        menu.Placement = anchor is null ? PlacementMode.MousePoint : PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem ColorMenu()
    {
        var panel = new WrapPanel { Width = 5 * 30, Margin = new Thickness(4) };
        var menu = Sub("바탕 색", "");
        foreach (var (name, hex) in MemoColors.Palette)
        {
            var swatch = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(2), ToolTip = name, Cursor = Cursors.Hand,
                Template = SwatchTemplate(hex, string.Equals(hex, Memo.Color, StringComparison.OrdinalIgnoreCase))
            };
            string chosen = hex;
            swatch.Click += (_, _) => { SetColor(chosen); CloseMenus(menu); };
            panel.Children.Add(swatch);
        }
        menu.Items.Add(new MenuItem { Header = panel, StaysOpenOnClick = true, Template = PlainItemTemplate() });
        menu.Items.Add(Item("직접 고르기…", PickColor));
        return menu;
    }

    private static void CloseMenus(MenuItem item)
    {
        DependencyObject? node = item;
        while (node is not null and not System.Windows.Controls.ContextMenu) node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node);
        if (node is System.Windows.Controls.ContextMenu menu) menu.IsOpen = false;
    }

    private static ControlTemplate SwatchTemplate(string hex, bool selected)
    {
        var template = new ControlTemplate(typeof(Button));
        var ellipse = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        ellipse.SetValue(System.Windows.Shapes.Shape.FillProperty, new SolidColorBrush(MemoColors.Parse(hex)));
        ellipse.SetValue(System.Windows.Shapes.Shape.StrokeProperty, selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
        ellipse.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, selected ? 2.5 : 1.0);
        template.VisualTree = ellipse;
        return template;
    }

    private static ControlTemplate PlainItemTemplate()
    {
        var template = new ControlTemplate(typeof(MenuItem));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        template.VisualTree = presenter;
        return template;
    }

    public void SetColor(string hex)
    {
        Memo.Color = hex;
        _desk.Save(Memo);
        ApplyLook();
    }

    private void PickColor()
    {
        var color = MemoColors.Parse(Memo.Color);
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B) };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SetColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }

    private void Toggle(Action<MemoData> change)
    {
        change(Memo);
        _desk.Save(Memo);
        ApplyLook();
    }

    private void ToggleSecure()
    {
        if (!Memo.Secured && !_desk.Settings.HasPassword)
        {
            Dialogs.Info(this, "보안 메모", "보안 암호가 없습니다. 설정 → 보안에서 암호를 정한 뒤 쓰세요.");
            _desk.ShowSettings();
            return;
        }
        if (Memo.Secured && !Unlock()) return;
        Memo.Secured = !Memo.Secured;
        _unlocked = !Memo.Secured || _unlocked;
        _desk.Save(Memo);
        ApplyLook();
    }

    private void ChangeOpacity()
    {
        if (Dialogs.Opacity(this, Memo.Opacity, PreviewOpacity) is { } opacity)
        {
            Memo.Opacity = opacity;
            _desk.Save(Memo);
        }
        ApplyOpacity();
    }

    private void AttachWithDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "파일 첨부" };
        if (dialog.ShowDialog(this) == true) AttachFiles(dialog.FileNames);
    }

    private ContextMenu AttachmentMenu(string name)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("열기", () => OpenAttachment(name)));
        menu.Items.Add(Item("다른 이름으로 저장…", () => SaveAttachmentAs(name), Icons.Save));
        menu.Items.Add(Item("폴더에서 보기", () =>
        {
            string path = _desk.Store.AttachmentPath(Memo, name);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }, Icons.Folder));
        if (!Memo.ReadOnly)
            menu.Items.Add(Item("빼기", () =>
            {
                _desk.Store.RemoveAttachment(Memo, name);
                _desk.Save(Memo);
                ApplyAttachments();
            }, Icons.Delete));
        return menu;
    }

    private void SaveAttachmentAs(string name)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = name, Title = "다른 이름으로 저장" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.Copy(_desk.Store.AttachmentPath(Memo, name), dialog.FileName, overwrite: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Dialogs.Info(this, "저장", error.Message); }
    }

    // ── 편집 메뉴 (본문 오른쪽 클릭) ───────────────────────────

    private void Editor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu();
        bool editable = !Editor.IsReadOnly;
        bool selected = !Editor.Selection.IsEmpty;
        var items = menu.Items;
        items.Add(Item("실행 취소", () => Editor.Undo(), Icons.Undo, "Ctrl+Z", editable && Editor.CanUndo));
        items.Add(Item("다시 실행", () => Editor.Redo(), Icons.Redo, "Ctrl+Shift+Z", editable && Editor.CanRedo));
        items.Add(new Separator());
        items.Add(Item("잘라내기", () => Editor.Cut(), "", "Ctrl+X", editable && selected));
        items.Add(Item("복사", () => Editor.Copy(), "", "Ctrl+C", selected));
        items.Add(Item("붙여넣기", () => Editor.Paste(), "", "Ctrl+V", editable));
        items.Add(Item("텍스트만 붙여넣기", PastePlain, "", "Ctrl+Shift+V", editable && Clipboard.ContainsText()));
        items.Add(Item("모두 선택", () => Editor.SelectAll(), "", "Ctrl+A"));
        items.Add(new Separator());
        if (editable)
        {
            items.Add(Item("글꼴…", ChooseFont, ""));
            items.Add(Sub("글자", "",
                Item("굵게", () => EditingCommands.ToggleBold.Execute(null, Editor), "", "Ctrl+B"),
                Item("기울임", () => EditingCommands.ToggleItalic.Execute(null, Editor), "", "Ctrl+I"),
                Item("밑줄", () => EditingCommands.ToggleUnderline.Execute(null, Editor), "", "Ctrl+U"),
                Item("취소선", ToggleStrike, ""),
                new Separator(),
                TextColorItem("검정", "#1F2124"), TextColorItem("빨강", "#D92D20"), TextColorItem("파랑", "#1D5FD1"),
                TextColorItem("초록", "#1E8A47"), TextColorItem("주황", "#D9730D"), TextColorItem("회색", "#6B6F76"),
                new Separator(),
                Item("형광펜", () => Highlight("#FFE45C")), Item("형광펜 지우기", () => Highlight(null))));
            items.Add(Sub("문단", "",
                Item("글머리표", () => EditingCommands.ToggleBullets.Execute(null, Editor), "", "Ctrl+Shift+L"),
                Item("번호", () => EditingCommands.ToggleNumbering.Execute(null, Editor), "", "Ctrl+Shift+N"),
                new Separator(),
                Item("들여쓰기 늘리기", () => EditingCommands.IncreaseIndentation.Execute(null, Editor), "", "Ctrl+Num+"),
                Item("들여쓰기 줄이기", () => EditingCommands.DecreaseIndentation.Execute(null, Editor), "", "Ctrl+Num-"),
                new Separator(),
                Item("왼쪽 정렬", () => EditingCommands.AlignLeft.Execute(null, Editor), "", "Ctrl+L"),
                Item("가운데 정렬", () => EditingCommands.AlignCenter.Execute(null, Editor), "", "Ctrl+E"),
                Item("오른쪽 정렬", () => EditingCommands.AlignRight.Execute(null, Editor), "", "Ctrl+R")));
            items.Add(new Separator());
            items.Add(Item("날짜 넣기", () => InsertText(Formats.Date(DateTime.Now, _desk.Settings.DateFormat)), "", "Ctrl+D"));
            items.Add(Item("시간 넣기", () => InsertText(Formats.Time(DateTime.Now, _desk.Settings.TimeFormat)), Icons.Clock, "Ctrl+T"));
            items.Add(Item("표 넣기…", InsertTable, ""));
            items.Add(Item("그림 넣기…", InsertImage, Icons.Picture, "Ctrl+M"));
            items.Add(new Separator());
            items.Add(Item("한/영 오타 변환", ConvertTypo, "", "F4"));
        }
        Editor.ContextMenu = menu;
    }

    private MenuItem TextColorItem(string name, string hex)
    {
        var item = Item(name, () => Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(MemoColors.Parse(hex))));
        item.Icon = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(MemoColors.Parse(hex)), Stroke = Brushes.White, StrokeThickness = 0.6 };
        return item;
    }

    private void Highlight(string? hex) =>
        Editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, hex is null ? null! : new SolidColorBrush(MemoColors.Parse(hex)));

    private void ToggleStrike()
    {
        var current = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        bool on = current is TextDecorationCollection decorations && decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough);
        Editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, on ? new TextDecorationCollection() : TextDecorations.Strikethrough);
    }

    private void ChooseFont()
    {
        var family = Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily ?? Editor.Document.FontFamily;
        double size = Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double s ? s : Editor.Document.FontSize;
        var weight = Editor.Selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w ? w : FontWeights.Normal;
        var style = Editor.Selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle st ? st : FontStyles.Normal;
        string name = family.Source.Split(',')[0].Trim();
        System.Drawing.FontStyle drawingStyle = (weight >= FontWeights.SemiBold ? System.Drawing.FontStyle.Bold : 0) | (style == FontStyles.Italic ? System.Drawing.FontStyle.Italic : 0);
        using var dialog = new System.Windows.Forms.FontDialog { ShowEffects = false, ShowColor = false, AllowVerticalFonts = false };
        try { dialog.Font = new System.Drawing.Font(name, (float)(size * 72 / 96), drawingStyle); }
        catch (ArgumentException) { }
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var font = dialog.Font;
        var target = Editor.Selection.IsEmpty ? new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd) : Editor.Selection;
        if (Editor.Selection.IsEmpty)
        {
            Editor.Document.FontFamily = new FontFamily(font.Name);
            Editor.Document.FontSize = font.SizeInPoints * 96 / 72;
        }
        target.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(font.Name));
        target.ApplyPropertyValue(TextElement.FontSizeProperty, (double)font.SizeInPoints * 96 / 72);
        target.ApplyPropertyValue(TextElement.FontWeightProperty, font.Bold ? FontWeights.Bold : FontWeights.Normal);
        target.ApplyPropertyValue(TextElement.FontStyleProperty, font.Italic ? FontStyles.Italic : FontStyles.Normal);
        MarkDirty();
    }

    // ── 파일 ─────────────────────────────────────────────────

    private void ImportFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "서식 있는 문자열 (*.rtf)|*.rtf|텍스트 문서 (*.txt)|*.txt|모든 파일|*.*", Title = "다른 파일 불러오기" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var stream = File.OpenRead(dialog.FileName);
            bool rtf = Path.GetExtension(dialog.FileName).Equals(".rtf", StringComparison.OrdinalIgnoreCase);
            if (rtf) Editor.Selection.Load(stream, DataFormats.Rtf);
            else
            {
                using var reader = new StreamReader(stream, JuEncoding.Detect(File.ReadAllBytes(dialog.FileName)));
                InsertText(reader.ReadToEnd().Replace("\r\n", "\r"));
            }
            MarkDirty();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Dialogs.Info(this, "불러오기", $"파일을 불러올 수 없습니다: {error.Message}");
        }
    }

    private void ExportFile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "서식 있는 문자열 (*.rtf)|*.rtf|텍스트 문서 (*.txt)|*.txt",
            FileName = Files.SafeName(Memo.FirstLine.Length > 30 ? Memo.FirstLine[..30] : Memo.FirstLine),
            Title = "파일로 저장"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SaveNow();
            if (dialog.FilterIndex == 2 || Path.GetExtension(dialog.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(dialog.FileName, RtfText.Plain(Editor.Document).Replace("\n", "\r\n"), new System.Text.UTF8Encoding(true));
            else File.WriteAllText(dialog.FileName, RtfText.Save(Editor.Document), System.Text.Encoding.ASCII);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Dialogs.Info(this, "파일로 저장", $"저장할 수 없습니다: {error.Message}");
        }
    }

    /// <summary>본문을 그림(PNG)으로</summary>
    private void SaveImage()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 그림 (*.png)|*.png", FileName = Files.SafeName(Memo.FirstLine.Length > 30 ? Memo.FirstLine[..30] : Memo.FirstLine) + ".png", Title = "그림으로 저장" };
        if (dialog.ShowDialog(this) != true) return;
        var image = Memo.Kind == MemoKind.Drawing ? RenderInk() : RenderVisual(Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        try
        {
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Dialogs.Info(this, "그림으로 저장", error.Message); }
    }

    private BitmapSource RenderVisual(FrameworkElement element)
    {
        double scale = Scale;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    // ── 인쇄 ─────────────────────────────────────────────────

    public void Print()
    {
        if (Memo.Secured && !Unlock()) return;
        SaveNow();
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        var document = new FlowDocument
        {
            PageWidth = dialog.PrintableAreaWidth,
            PageHeight = dialog.PrintableAreaHeight,
            PagePadding = new Thickness(56),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = new FontFamily("Malgun Gothic"),
            FontSize = 13
        };
        var head = new Paragraph { FontSize = 10.5, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 14) };
        head.Inlines.Add(new Run($"생성시간 : {Formats.Full(Memo.Created)}"));
        if (Memo.ReceivedAt is { } received) head.Inlines.Add(new Run($"    받은시간 : {Formats.Full(received)}  ({Memo.From?.Label})"));
        head.Inlines.Add(new Run($"    최종편집 : {Formats.Full(Memo.Modified)}    인쇄시간 : {Formats.Full(DateTime.Now)}"));
        document.Blocks.Add(head);
        if (Memo.Kind == MemoKind.Drawing)
            document.Blocks.Add(new BlockUIContainer(new Image { Source = RenderInk(), Stretch = Stretch.Uniform, MaxWidth = dialog.PrintableAreaWidth - 112 }));
        else
        {
            var body = RtfText.Document(RtfText.Save(Editor.Document));
            foreach (var block in body.Blocks.ToList())
            {
                body.Blocks.Remove(block);
                document.Blocks.Add(block);
            }
        }
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, $"MemoPad - {Memo.FirstLine}");
    }
}

/// <summary>텍스트 파일 인코딩 짐작 (BOM · UTF-8 · CP949)</summary>
internal static class JuEncoding
{
    public static System.Text.Encoding Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return System.Text.Encoding.UTF8;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return System.Text.Encoding.Unicode;
        try
        {
            new System.Text.UTF8Encoding(false, true).GetString(bytes);
            return System.Text.Encoding.UTF8;
        }
        catch (System.Text.DecoderFallbackException) { return Net.JuProtocol.Ansi; }
    }
}
