using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MemoPad.Core;

namespace MemoPad.Ui;

/// <summary>그림 메모: 펜 색 · 굵기 · 지우개 · 되돌리기 · 다시 · 전부 지우기 · 확대 · 축소</summary>
public partial class MemoWindow
{
    private static readonly (string Name, string Hex)[] PenColors =
        [("검정", "#1F2124"), ("빨강", "#D92D20"), ("파랑", "#1D5FD1"), ("초록", "#1E8A47"), ("주황", "#E8790C"), ("흰색", "#FFFFFF")];
    private static readonly double[] PenWidths = [2, 4.5, 9];

    private readonly Stack<(StrokeCollection Added, StrokeCollection Removed)> _undo = new(), _redo = new();
    private bool _replaying;
    private Color _penColor = MemoColors.Parse("#1F2124");
    private double _penWidth = 2;

    private void SetupInk()
    {
        if (Memo.Ink.Length > 0)
        {
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(Memo.Ink));
                Ink.Strokes = new StrokeCollection(stream);
            }
            catch (Exception error) when (error is FormatException or ArgumentException or IOException) { }
        }
        Ink.EditingMode = Memo.ReadOnly ? InkCanvasEditingMode.None : InkCanvasEditingMode.Ink;
        ApplyPen();
        InkZoom.ScaleX = InkZoom.ScaleY = Math.Clamp(Memo.Zoom, 0.5, 3);
        Ink.Strokes.StrokesChanged += OnStrokesChanged;
        BuildInkTools();
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (!_replaying)
        {
            _undo.Push((e.Added, e.Removed));
            _redo.Clear();
        }
        MarkDirty();
    }

    private string InkData()
    {
        using var stream = new MemoryStream();
        Ink.Strokes.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private void ApplyPen()
    {
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _penColor,
            Width = _penWidth,
            Height = _penWidth,
            FitToCurve = true,
            IgnorePressure = false
        };
    }

    private void BuildInkTools()
    {
        InkTools.Children.Clear();
        var style = (Style)Resources["InkToolStyle"];
        foreach (var (name, hex) in PenColors)
        {
            var color = MemoColors.Parse(hex);
            var tool = new RadioButton
            {
                Style = style, GroupName = "pen", ToolTip = name, IsChecked = color == _penColor,
                Content = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), StrokeThickness = 0.8 }
            };
            tool.Checked += (_, _) =>
            {
                _penColor = color;
                Ink.EditingMode = InkCanvasEditingMode.Ink;
                ApplyPen();
            };
            InkTools.Children.Add(tool);
        }
        InkTools.Children.Add(new Border { Width = 1, Height = 16, Margin = new Thickness(4, 0, 4, 0), Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)) });
        foreach (double width in PenWidths)
        {
            var tool = new RadioButton
            {
                Style = style, GroupName = "width", ToolTip = $"굵기 {width}", IsChecked = Math.Abs(width - _penWidth) < 0.1,
                Content = new System.Windows.Shapes.Ellipse { Width = Math.Min(12, width + 2), Height = Math.Min(12, width + 2), Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)) }
            };
            double chosen = width;
            tool.Checked += (_, _) => { _penWidth = chosen; ApplyPen(); };
            InkTools.Children.Add(tool);
        }
        InkTools.Children.Add(new Border { Width = 1, Height = 16, Margin = new Thickness(4, 0, 4, 0), Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)) });
        var eraser = new ToggleButton { Style = (Style)Resources["MemoToggle"], Content = Icons.Eraser, ToolTip = "지우개 (선 하나씩)" };
        eraser.Checked += (_, _) => Ink.EditingMode = InkCanvasEditingMode.EraseByStroke;
        eraser.Unchecked += (_, _) => Ink.EditingMode = InkCanvasEditingMode.Ink;
        InkTools.Children.Add(eraser);
        InkTools.Children.Add(InkButton(Icons.Undo, "그리기 취소 (Ctrl+Z)", InkUndo));
        InkTools.Children.Add(InkButton(Icons.Redo, "다시 그리기 (Ctrl+Y)", InkRedo));
        InkTools.Children.Add(InkButton(Icons.Delete, "전부 지우기", () =>
        {
            if (Ink.Strokes.Count > 0 && Dialogs.Ask(this, "그림 메모", "그림의 내용을 지우시겠습니까?")) Ink.Strokes.Clear();
        }));
        InkTools.Children.Add(InkButton(Icons.ZoomOut, "축소", () => SetZoom(InkZoom.ScaleX / 1.25)));
        InkTools.Children.Add(InkButton(Icons.ZoomIn, "확대", () => SetZoom(InkZoom.ScaleX * 1.25)));
    }

    private Button InkButton(string glyph, string tip, Action action)
    {
        var button = new Button { Style = (Style)Resources["MemoButton"], Content = glyph, ToolTip = tip, Width = 24 };
        button.Click += (_, _) => action();
        return button;
    }

    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 0.5, 3);
        InkZoom.ScaleX = InkZoom.ScaleY = zoom;
        Memo.Zoom = zoom;
        _desk.Save(Memo);
    }

    private void InkUndo()
    {
        if (_undo.Count == 0) return;
        var (added, removed) = _undo.Pop();
        _replaying = true;
        try
        {
            Ink.Strokes.Remove(new StrokeCollection(added.Where(Ink.Strokes.Contains)));
            Ink.Strokes.Add(removed);
        }
        finally { _replaying = false; }
        _redo.Push((added, removed));
    }

    private void InkRedo()
    {
        if (_redo.Count == 0) return;
        var (added, removed) = _redo.Pop();
        _replaying = true;
        try
        {
            Ink.Strokes.Remove(new StrokeCollection(removed.Where(Ink.Strokes.Contains)));
            Ink.Strokes.Add(added);
        }
        finally { _replaying = false; }
        _undo.Push((added, removed));
    }

    /// <summary>선들을 메모 바탕색 위에 그린 그림 (보내기 · 인쇄 · 저장)</summary>
    public BitmapSource RenderInk()
    {
        var bounds = Ink.Strokes.Count > 0 ? Ink.Strokes.GetBounds() : new Rect(0, 0, 200, 120);
        bounds = new Rect(0, 0, Math.Max(bounds.Right + 12, 60), Math.Max(bounds.Bottom + 12, 40));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(MemoColors.Parse(Memo.Color)), null, bounds);
            Ink.Strokes.Draw(context);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
