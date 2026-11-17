using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MemoPad.Core;

namespace MemoPad.Ui;

/// <summary>메모 아래에 잠깐 뜨는 작은 알림 (보내는 중 · 보냈습니다). 누르지 않아도 사라지고 포커스를 뺏지 않음</summary>
internal sealed class Toast : Window
{
    private readonly TextBlock _text;

    private Toast(string text)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        _text = new TextBlock { Text = text, FontSize = 12.5, Foreground = Brushes.White, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis };
        Content = new Border
        {
            Margin = new Thickness(6),
            Padding = new Thickness(12, 7, 12, 8),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x26, 0x27, 0x2B)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)),
            BorderThickness = new Thickness(1),
            Child = _text
        };
    }

    public static Toast Show(Window anchor, string text)
    {
        var toast = new Toast(text);
        toast.SourceInitialized += (_, _) => Native.AddExStyle(toast, Native.WS_EX_TOOLWINDOW | 0x08000000 /* WS_EX_NOACTIVATE */);
        toast.Loaded += (_, _) => toast.Place(anchor);
        toast.Show();
        return toast;
    }

    private void Place(Window anchor)
    {
        if (!anchor.IsVisible) { Left = SystemParameters.WorkArea.Right - ActualWidth - 20; Top = SystemParameters.WorkArea.Bottom - ActualHeight - 20; return; }
        var work = SystemParameters.WorkArea;
        double left = anchor.Left, top = anchor.Top + anchor.ActualHeight;
        if (top + ActualHeight > work.Bottom) top = anchor.Top - ActualHeight;
        Left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - ActualWidth));
        Top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }

    public void Update(string text) => Dispatcher.BeginInvoke(() => _text.Text = text);

    public void Finish(string text) => Dispatcher.BeginInvoke(() => _text.Text = text);

    public void CloseSoon() => Dispatcher.BeginInvoke(async () =>
    {
        await Task.Delay(1800);
        Close();
    });
}
