using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MemoPad.Core;

namespace MemoPad.Ui;

/// <summary>알람 설정: 날짜 · 시간 · 미리 알림 · 소리 · 설명. 끄기 = 알람 없애기</summary>
internal sealed class AlarmWindow : DialogWindow
{
    private static readonly (string Label, int Minutes)[] Advances =
        [("정시", 0), ("5분 전", 5), ("10분 전", 10), ("15분 전", 15), ("20분 전", 20), ("30분 전", 30), ("40분 전", 40), ("45분 전", 45), ("50분 전", 50), ("1시간 전", 60), ("1시간 30분 전", 90), ("2시간 전", 120)];

    private readonly System.Windows.Controls.Calendar _calendar = new() { SelectionMode = CalendarSelectionMode.SingleDate };
    private readonly TextBox _hour = new() { Width = 54, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly TextBox _minute = new() { Width = 54, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly WrapPanel _advance = new();
    private readonly TextBox _note = new() { Tag = "설명 (선택)" };
    private readonly TextBlock _sound = new() { Style = (Style)Application.Current.Resources["MutedTextStyle"], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _error = new() { Foreground = (Brush)Application.Current.Resources["WarningBrush"], Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private string _soundFile;
    private int _advanceMinutes;

    public AlarmWindow(Window owner, Alarm? current) : base(owner, "알람", Icons.Clock, 470)
    {
        var at = current is { Done: false } ? current.At : DateTime.Now.AddHours(1).AddMinutes(-DateTime.Now.Minute);
        _calendar.SelectedDate = at.Date;
        _calendar.DisplayDate = at.Date;
        _hour.Text = at.Hour.ToString("00");
        _minute.Text = at.Minute.ToString("00");
        _note.Text = current?.Note ?? "";
        _soundFile = current?.Sound ?? "";
        _advanceMinutes = current?.AdvanceMinutes ?? 0;

        var panel = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        _calendar.Background = Brushes.White;
        top.Children.Add(new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = _calendar, Background = Brushes.White, Padding = new Thickness(2) });

        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        right.Children.Add(Label("시간"));
        var time = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        time.Children.Add(_hour);
        time.Children.Add(new TextBlock { Text = ":", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 16 });
        time.Children.Add(_minute);
        right.Children.Add(time);
        var quick = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, minutes) in new[] { ("10분 뒤", 10), ("30분 뒤", 30), ("1시간 뒤", 60), ("내일 9시", -1) })
        {
            var chip = Chrome.Pill(label, () => SetQuick(minutes));
            chip.Height = 26;
            chip.FontSize = 12;
            chip.MinWidth = 0;
            chip.Margin = new Thickness(0, 0, 6, 6);
            quick.Children.Add(chip);
        }
        right.Children.Add(quick);
        top.Children.Add(right);
        panel.Children.Add(top);

        panel.Children.Add(Label("미리 알림", 14));
        foreach (var (label, minutes) in Advances)
        {
            var chip = new RadioButton { Content = label, Style = (Style)Application.Current.Resources["SegmentStyle"], GroupName = "advance", IsChecked = minutes == _advanceMinutes, Height = 26, Margin = new Thickness(0, 0, 4, 4) };
            int value = minutes;
            chip.Checked += (_, _) => _advanceMinutes = value;
            _advance.Children.Add(chip);
        }
        _advance.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_advance);

        panel.Children.Add(Label("설명", 10));
        _note.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_note);

        var soundRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var soundButtons = new StackPanel { Orientation = Orientation.Horizontal };
        soundButtons.Children.Add(Chrome.IconButton("", "들어 보기", () => AlarmPopup.PlaySound(_soundFile, loop: false), 10.5));
        soundButtons.Children.Add(Chrome.Pill("소리 바꾸기…", ChooseSound));
        DockPanel.SetDock(soundButtons, Dock.Right);
        soundRow.Children.Add(soundButtons);
        soundRow.Children.Add(_sound);
        panel.Children.Add(soundRow);
        panel.Children.Add(_error);
        UpdateSound();
        Body = panel;

        if (current is not null)
        {
            var off = AddButton("알람 끄기", () => { Result = null; DialogResult = true; });
            off.Margin = new Thickness(0);
        }
        AddButton("취소", result: false);
        AddButton("설정", Accept, primary: true);
        Result = current;
    }

    public Alarm? Result { get; private set; }

    private static TextBlock Label(string text, double top = 0) => new()
    {
        Text = text,
        Margin = new Thickness(0, top, 0, 0),
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
        FontSize = 12.5
    };

    private void SetQuick(int minutes)
    {
        var at = minutes < 0 ? DateTime.Today.AddDays(1).AddHours(9) : DateTime.Now.AddMinutes(minutes);
        _calendar.SelectedDate = at.Date;
        _calendar.DisplayDate = at.Date;
        _hour.Text = at.Hour.ToString("00");
        _minute.Text = at.Minute.ToString("00");
    }

    private void ChooseSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "소리 파일 (*.wav)|*.wav", Title = "알람 소리" };
        if (dialog.ShowDialog(this) == true) { _soundFile = dialog.FileName; UpdateSound(); }
    }

    private void UpdateSound() => _sound.Text = _soundFile.Length > 0 && File.Exists(_soundFile) ? $"소리: {Path.GetFileName(_soundFile)}" : "소리: 기본 알람";

    private void Accept()
    {
        if (_calendar.SelectedDate is not { } date || !int.TryParse(_hour.Text, out int hour) || !int.TryParse(_minute.Text, out int minute) || hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            _error.Text = "날짜와 시간(0~23 : 0~59)을 확인하세요.";
            _error.Visibility = Visibility.Visible;
            return;
        }
        var at = date.Date.AddHours(hour).AddMinutes(minute);
        if (at.AddMinutes(-_advanceMinutes) <= DateTime.Now)
        {
            _error.Text = "이미 지난 시간입니다.";
            _error.Visibility = Visibility.Visible;
            return;
        }
        Result = new Alarm { At = at, AdvanceMinutes = _advanceMinutes, Note = _note.Text.Trim(), Sound = _soundFile };
        DialogResult = true;
    }
}

/// <summary>알람이 울릴 때 뜨는 창: 약속 시간 · 메모 첫 줄 · 설명, 5분 뒤 다시 · 확인</summary>
internal sealed class AlarmPopup : DialogWindow
{
    private AlarmPopup(MemoWindow memoWindow, MemoData memo) : base(null, "알람", Icons.Clock, 400)
    {
        var alarm = memo.Alarm!;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "약속 시간이 되었습니다", FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = alarm.At.ToString("M월 d일 (ddd) tt h:mm", CultureInfo.GetCultureInfo("ko-KR")) + (alarm.AdvanceMinutes > 0 ? $" · {alarm.AdvanceMinutes}분 전 알림" : ""), Margin = new Thickness(0, 4, 0, 10), Foreground = (Brush)Application.Current.Resources["MutedTextBrush"] });
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(MemoColors.Parse(memo.Color)),
            Padding = new Thickness(12, 8, 12, 8),
            Child = new TextBlock { Text = memo.Title.Length > 0 ? memo.Title : memo.Secured ? "(보안 메모)" : memo.FirstLine, Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x21, 0x24)), TextWrapping = TextWrapping.Wrap, MaxHeight = 80 }
        });
        if (alarm.Note.Length > 0) panel.Children.Add(new TextBlock { Text = alarm.Note, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
        Body = panel;
        var snooze = AddButton("5분 뒤 다시", () =>
        {
            memo.Alarm = new Alarm { At = DateTime.Now.AddMinutes(5), Sound = alarm.Sound, Note = alarm.Note };
            memoWindow.Memo.Alarm = memo.Alarm;
            memoWindow.ApplyLook();
            App.Desk?.Save(memo);
            Close();
        });
        snooze.Margin = new Thickness(0);
        AddButton("메모 보기", () => { memoWindow.BringToFront(); Close(); });
        AddButton("확인", Close, primary: true);
        Topmost = true;
        Closed += (_, _) => Native.PlaySound(null, IntPtr.Zero, 0);
    }

    public static void Ring(MemoWindow window, MemoData memo)
    {
        ILuncher.AppKit.AppUsage.Count("alarm");
        var popup = new AlarmPopup(window, memo);
        popup.Show();
        popup.Activate();
        PlaySound(memo.Alarm?.Sound ?? "", loop: true);
        // 소리는 15초까지 (정음 메모패드와 같게)
        _ = popup.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(15_000);
            Native.PlaySound(null, IntPtr.Zero, 0);
        });
    }

    public static void PlaySound(string file, bool loop)
    {
        int flags = Native.SND_ASYNC | Native.SND_NODEFAULT | (loop ? Native.SND_LOOP : 0);
        if (file.Length > 0 && File.Exists(file)) Native.PlaySound(file, IntPtr.Zero, flags | Native.SND_FILENAME);
        else Native.PlaySound("Notification.Looping.Alarm", IntPtr.Zero, flags | Native.SND_ALIAS);
    }
}
