using System.IO;
using System.Windows;
using System.Windows.Threading;
using MemoPad.Core;
using MemoPad.Net;

namespace MemoPad.Ui;

public enum SendKind { Send, Reply, Forward, Circulate }

/// <summary>
/// 앱 전체를 묶음: 메모 창들 · 저장소 · 설정 · 주고받기 · 알람 · 다른 창(관리기 · 설정 · 주소록 · 상태 · 대화)
/// </summary>
public sealed partial class Desk
{
    private readonly Dictionary<Guid, MemoWindow> _windows = [];
    private readonly DispatcherTimer _alarmTimer;
    private readonly DispatcherTimer _portTimer;
    private ManagerWindow? _manager;
    private SettingsWindow? _settingsWindow;
    private AddressBookWindow? _addressWindow;
    private StatusWindow? _statusWindow;

    public Desk(AppSettings settings, MemoStore store, System.Net.IPAddress? bind = null)
    {
        Settings = settings;
        Store = store;
        Net = new MemoService(() => Settings.Absent ? (Settings.AbsentText.Length > 0 ? Settings.AbsentText : "부재중입니다.") : null, bind);
        Net.MemoReceived += memo => Application.Current.Dispatcher.BeginInvoke(() => OnMemoReceived(memo));
        Net.NoteReceived += note => Application.Current.Dispatcher.BeginInvoke(() => OnCirculationNote(note));
        Net.Problem += text => Log.Write("[받기] " + text);
        Chat = new ChatService(this);
        _alarmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _alarmTimer.Tick += (_, _) => CheckAlarms();
        // 이 PC 에 정음 메모패드가 생기면 정음 포트를 내주고, 없어지면 다시 받음
        _portTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _portTimer.Tick += (_, _) => { if (Settings.UseNetwork && Net.Listening) UpdateJungumPorts(); };
    }

    public AppSettings Settings { get; }
    public MemoStore Store { get; }
    public MemoService Net { get; }
    public ChatService Chat { get; }
    public string MyIp => NetAddress.MyAddress(Settings.Ip);
    public Person Me => Settings.Me(MyIp);
    public IEnumerable<MemoWindow> Windows => _windows.Values;

    /// <summary>바뀐 상태를 알림 (트레이 글자 · 관리기 목록)</summary>
    public event Action? StateChanged;

    public void Start()
    {
        foreach (var memo in Store.Live.Where(m => m.Visible).OrderBy(m => m.Modified)) Open(memo, activate: false);
        if (Settings.UseNetwork) StartNetwork();
        _alarmTimer.Start();
        CheckAlarms();
        if (Settings.ManagerAtStart) ShowManager();
    }

    public void StartNetwork()
    {
        Net.Start();
        Chat.Start();
        UpdateJungumPorts();
        _portTimer.Start();
        StateChanged?.Invoke();
    }

    public void StopNetwork()
    {
        _portTimer.Stop();
        Net.Stop();
        Chat.Stop();
        StateChanged?.Invoke();
    }

    /// <summary>정음 메모패드 포트(9710 · 9712)로도 받기: 이 PC 에 정음 메모패드가 없을 때만 (Ports.ListenJungum)</summary>
    private void UpdateJungumPorts()
    {
        bool on = Ports.ListenJungum;
        bool before = Net.JungumListening;
        Net.SetJungumPort(on);
        Chat.SetJungumPort(on);
        if (Net.JungumListening != before) StateChanged?.Invoke();
    }

    public void Shutdown()
    {
        _portTimer.Stop();
        foreach (var window in _windows.Values.ToList()) window.SaveNow();
        Chat.Stop();
        Net.Dispose();
    }

    public void Save(MemoData memo)
    {
        Store.Save(memo);
        _manager?.Refresh(memo);
    }

    // ── 메모 창 ────────────────────────────────────────────────

    public MemoWindow? WindowOf(MemoData memo) => _windows.GetValueOrDefault(memo.Id);

    public MemoWindow NewMemo(MemoKind kind = MemoKind.Text)
    {
        var memo = new MemoData
        {
            Kind = kind,
            Color = Settings.DefaultColor,
            Layer = Settings.DefaultTop ? MemoLayer.Top : MemoLayer.Normal,
            Opacity = Math.Clamp(Settings.DefaultOpacity, 0.2, 1),
            StatusBar = Settings.DefaultStatusBar
        };
        Save(memo);
        var window = Open(memo, activate: true);
        window.Editor.Focus();
        return window;
    }

    /// <summary>메모를 바탕화면에 보이게 (창이 없으면 만듦)</summary>
    public MemoWindow Open(MemoData memo, bool activate = true, bool show = true)
    {
        if (!_windows.TryGetValue(memo.Id, out var window))
        {
            window = new MemoWindow(this, memo);
            window.Closed += (_, _) => _windows.Remove(memo.Id);
            _windows[memo.Id] = window;
        }
        if (!memo.Visible)
        {
            memo.Visible = true;
            Save(memo);
        }
        if (activate) { window.Show(); window.BringToFront(); }
        else if (show) { window.ShowActivated = false; window.Show(); window.ShowActivated = true; }
        return window;
    }

    public void HideMemo(MemoWindow window)
    {
        window.SaveNow();
        window.Memo.Visible = false;
        Save(window.Memo);
        window.Close();
    }

    /// <summary>버리기: 휴지통으로 (휴지통을 안 쓰면 물어보고 지움)</summary>
    public void Discard(MemoWindow window)
    {
        window.SaveNow();
        var memo = window.Memo;
        if (!Settings.UseTrash && window.HasBody && !Dialogs.Ask(window, "버리기", "메모를 버리시겠습니까?")) return;
        bool empty = !window.HasBody && memo.Attachments.Count == 0;
        window.Close();
        if (empty) Store.Delete(memo);
        else Store.MoveToTrash(memo, Settings.UseTrash);
        _manager?.Reload();
        StateChanged?.Invoke();
    }

    public void ShowAll()
    {
        foreach (var memo in Store.Live.ToList()) Open(memo, activate: false);
        foreach (var window in _windows.Values) window.BringToFront();
    }

    public void HideAll()
    {
        foreach (var window in _windows.Values.ToList()) HideMemo(window);
    }

    public void DiscardAll()
    {
        if (_windows.Count == 0 || !Dialogs.Ask(null, "모두 버리기", "바탕화면의 메모를 모두 버리시겠습니까?" + (Settings.UseTrash ? "\n(메모 관리기의 휴지통에서 되살릴 수 있습니다)" : ""))) return;
        foreach (var window in _windows.Values.ToList())
        {
            window.SaveNow();
            var memo = window.Memo;
            window.Close();
            Store.MoveToTrash(memo, Settings.UseTrash);
        }
        _manager?.Reload();
    }

    /// <summary>새 메모 자리: 작업 영역 오른쪽 아래부터, 같은 자리에 메모가 있으면 비켜서</summary>
    public void PlaceNew(MemoData memo)
    {
        var work = Native.PrimaryWorkArea();
        if (Native.GetCursorPos(out var cursor)) work = Native.WorkAreaAt(cursor.X, cursor.Y);
        int step = Math.Max(20, memo.Height / 6);
        int x = work.Right - memo.Width - 24, y = work.Bottom - memo.Height - 24;
        var taken = _windows.Values.Where(w => w.Memo.Id != memo.Id && w.IsVisible).Select(w => (w.Memo.X, w.Memo.Y)).ToHashSet();
        for (int i = 0; i < 30 && taken.Any(t => Math.Abs(t.X - x) < 12 && Math.Abs(t.Y - y) < 12); i++)
        {
            x -= step;
            y -= step;
            if (y < work.Top || x < work.Left) { x = work.Right - memo.Width - 24 - i * 6; y = work.Bottom - memo.Height - 24; }
        }
        memo.X = x;
        memo.Y = y;
    }

    /// <summary>바탕화면 메모 정렬: 오른쪽 위부터 세로로 쌓고 넘치면 왼쪽 열로</summary>
    public void Arrange()
    {
        var work = Native.PrimaryWorkArea();
        int gap = Math.Max(0, Settings.ArrangeGap);
        int x = work.Right - gap, y = work.Top + gap, column = 0;
        var windows = _windows.Values.Where(w => w.IsVisible && !(Settings.ArrangeSkipExpanded && !w.Memo.Collapsed))
            .OrderBy(w => w.Memo.Collapsed).ThenBy(w => w.Memo.Created).ToList();
        foreach (var window in windows)
        {
            var bounds = window.PixelBounds;
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            if (y + height > work.Bottom - gap && y > work.Top + gap)
            {
                x -= column + gap;
                y = work.Top + gap;
                column = 0;
            }
            column = Math.Max(column, width);
            window.MoveTo(x - width, y);
            y += height + gap;
        }
    }

    // ── 다른 창 ────────────────────────────────────────────────

    public void ShowManager(MemoData? select = null)
    {
        _manager ??= CreateManager();
        _manager.ShowFront();
        if (select is not null) _manager.Select(select);
    }

    private ManagerWindow CreateManager()
    {
        var window = new ManagerWindow(this);
        window.Closed += (_, _) => _manager = null;
        return window;
    }

    public void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ShowAddressBook()
    {
        if (_addressWindow is null)
        {
            _addressWindow = new AddressBookWindow(this);
            _addressWindow.Closed += (_, _) => _addressWindow = null;
        }
        _addressWindow.Show();
        _addressWindow.Activate();
    }

    public void ShowStatus()
    {
        if (_statusWindow is null)
        {
            _statusWindow = new StatusWindow(this);
            _statusWindow.Closed += (_, _) => _statusWindow = null;
        }
        _statusWindow.Show();
        _statusWindow.Activate();
    }

    public void OpenChat(Person? with = null) => Chat.OpenWindow(with);

    public AddressBook LoadAddressBook() => AddressBook.Load(Settings.AddressBookPath);

    public bool SaveAddressBook(AddressBook book, Window? owner)
    {
        try
        {
            book.Save(Settings.AddressBookPath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Dialogs.Info(owner, "주소록", $"주소록을 저장할 수 없습니다 (다른 사람이 고치는 중이거나 쓸 수 없는 폴더).\n{error.Message}");
            return false;
        }
    }

    public void SetAbsent(bool absent)
    {
        Settings.Absent = absent;
        Settings.Save();
        StateChanged?.Invoke();
    }

    // ── 알람 ───────────────────────────────────────────────────

    public void EditAlarm(MemoWindow window)
    {
        var dialog = new AlarmWindow(window, window.Memo.Alarm);
        if (dialog.ShowDialog() != true) return;
        window.Memo.Alarm = dialog.Result;
        Save(window.Memo);
        window.ApplyLook();
        CheckAlarms();
    }

    private void CheckAlarms()
    {
        var now = DateTime.Now;
        foreach (var memo in Store.Live.Where(m => m.Alarm is { Done: false } a && a.RingAt <= now).ToList())
        {
            memo.Alarm!.Done = true;
            Save(memo);
            var window = Open(memo, activate: false, show: false);
            window.ApplyLook();
            window.BringToFront(flash: true, activate: false);
            AlarmPopup.Ring(window, memo);
        }
    }

    // ── 받기 ───────────────────────────────────────────────────

    private void OnMemoReceived(IncomingMemo incoming)
    {
        var header = incoming.Header;
        // 답장은 보낸 사람이 알려 준 주소로 (정음 메모패드와 같게), 비어 있으면 연결해 온 주소로
        var sender = header.Sender.Copy();
        if (!NetAddress.IsIPv4(sender.Ip)) sender.Ip = incoming.RemoteIp;
        var known = LoadAddressBook().FindByIp(sender.Ip);
        if (known is not null && known.Email.Length > 0 && sender.Email.Length == 0) sender.Email = known.Email;

        var memo = new MemoData
        {
            Origin = MemoOrigin.Received,
            Color = MemoColors.FromJungum(header.Color),
            From = sender,
            ReceivedAt = DateTime.Now,
            Created = header.Created,
            Layer = Settings.ReceivedOnTop ? MemoLayer.Top : MemoLayer.Normal,
            Secured = Settings.SecureReceived && Settings.HasPassword,
            StatusBar = Settings.DefaultStatusBar
        };
        memo.Width = Math.Clamp(header.Width, 160, 1600);
        memo.Height = Math.Clamp(header.Height, 90, 1200);

        string rtf = incoming.Rtf;
        try
        {
            if (header.IsCirculation) PrepareCirculation(memo, header, ref rtf);
            else if (Settings.QuoteReceived) rtf = RtfText.Quote(rtf);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // 꾸미다 실패해도 받은 내용은 그대로 보여 줌
            Log.Error("받은 메모 꾸미기", error);
            rtf = incoming.Rtf;
        }
        memo.Rtf = rtf;
        memo.ReceivedRtf = rtf;
        memo.Text = RtfText.Plain(rtf);
        foreach (var file in incoming.Files) Store.AddAttachment(memo, file.Name, file.Data);
        Save(memo);

        var window = Open(memo, activate: false, show: false);
        window.BringToFront(flash: true, activate: false);
        PlayReceiveSound();
        StateChanged?.Invoke();
        _ = ForwardReceivedAsync(memo, header, incoming);
    }

    private void PlayReceiveSound()
    {
        if (!Settings.Sound) return;
        if (Settings.SoundFile.Length > 0 && File.Exists(Settings.SoundFile))
            Native.PlaySound(Settings.SoundFile, IntPtr.Zero, Native.SND_ASYNC | Native.SND_FILENAME | Native.SND_NODEFAULT);
        else Native.PlaySound("Notification.IM", IntPtr.Zero, Native.SND_ASYNC | Native.SND_ALIAS);
    }

    /// <summary>받은 메모를 다른 주소로도 보냄 (설정 → 받은 메모 전달 주소)</summary>
    private async Task ForwardReceivedAsync(MemoData memo, MemoHeader header, IncomingMemo incoming)
    {
        var targets = Settings.ForwardTo.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries).Where(NetAddress.IsIPv4).ToList();
        if (targets.Count == 0 || header.IsCirculation) return;
        var files = memo.Attachments.Select(name => new OutgoingFile(name, Store.AttachmentPath(memo, name))).ToList();
        foreach (string ip in targets)
            await Net.SendAsync(new Person { Ip = ip, Name = ip }, new OutgoingMemo(header, incoming.Rtf, files), CancellationToken.None);
    }
}
