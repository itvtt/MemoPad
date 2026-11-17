using System.Windows;
using MemoPad.Core;
using MemoPad.Net;

namespace MemoPad.Ui;

/// <summary>
/// 보내기 · 답장 · 전달 · 회람. 회람은 정음 메모패드와 같은 차례 방식:
///   시작한 사람 → 첫 사람 (종류 2, 대상 목록 전체)
///   받은 사람 → 시작한 사람에게 "받음" 알림, [회람 전달] 하면 "전달" 알림 + 다음 사람에게 (종류 3, 내 칸 = 3)
///   마지막 사람 → 시작한 사람에게 결과 (종류 3, 모든 칸 = 3). 메모 머리의 보낸 사람은 늘 시작한 사람
/// </summary>
public sealed partial class Desk
{
    // 회람 대상 상태: 3 = 전달 (정음 메모패드 칸 값), 1 · 16 은 이 PC 에서만 (받음 · 못 받음)
    public const int CirculationWaiting = 0, CirculationReceivedState = 1, CirculationPassedState = 3, CirculationMissed = 16;

    public static string CirculationStateText(int state) => state switch
    {
        CirculationReceivedState => "받음",
        CirculationPassedState => "전달",
        CirculationMissed => "못 받음",
        _ => "대기"
    };

    private MemoHeader HeaderFor(MemoWindow window)
    {
        var memo = window.Memo;
        var bounds = window.IsVisible ? window.PixelBounds : new Native.RECT { Left = memo.X, Top = memo.Y, Right = memo.X + memo.Width, Bottom = memo.Y + memo.Height };
        return new MemoHeader
        {
            Sender = Me,
            MemoId = memo.Id,
            X = bounds.Left,
            Y = bounds.Top,
            Width = memo.Collapsed ? memo.Width : bounds.Right - bounds.Left,
            Height = memo.Collapsed ? memo.Height : bounds.Bottom - bounds.Top,
            Color = MemoColors.ToJungum(memo.Color),
            Created = memo.Created,
            Modified = memo.Modified
        };
    }

    private List<OutgoingFile> FilesOf(MemoData memo) =>
        memo.Attachments.Select(name => new OutgoingFile(name, Store.AttachmentPath(memo, name))).Where(f => System.IO.File.Exists(f.Path)).ToList();

    /// <summary>이 PC 의 주소인가: 설정에서 IP 를 정했으면 그 주소만, 아니면 이 PC 의 모든 주소</summary>
    private bool IsMeStrict(Person person) =>
        person.Ip.Length > 0 && (person.Ip == MyIp || Settings.Ip.Length == 0 && NetAddress.LocalAddresses().Contains(person.Ip));

    /// <param name="preset">받을 사람을 미리 정함 (받을 사람 창을 띄우지 않음)</param>
    public async Task SendAsync(MemoWindow window, SendKind kind, IReadOnlyList<Person>? preset = null)
    {
        var memo = window.Memo;
        if (!Networked) return;
        if (memo.Secured && !window.Unlock()) return;
        window.SaveNow();

        List<Person> targets;
        if (kind == SendKind.Reply)
        {
            if (memo.From is null) return;
            if (memo.Kind == MemoKind.Text && memo.Rtf == memo.ReceivedRtf &&
                !Dialogs.Ask(window, "답장", "받은 메모에 수정한 내용이 없습니다.\n그래도 보내시겠습니까?")) return;
            targets = [memo.From];
        }
        else if (preset is { Count: > 0 }) targets = preset.Select(p => p.Copy()).ToList();
        else
        {
            var picker = new RecipientsWindow(this, window, kind);
            if (picker.ShowDialog() != true || picker.Result.Count == 0) return;
            targets = picker.Result;
            RememberRecipients(targets);
        }

        string rtf = window.RtfForSending();
        var files = FilesOf(memo);
        var toast = Toast.Show(window, "보내는 중…");
        try
        {
            if (kind == SendKind.Circulate)
            {
                memo.Circulation = new Circulation
                {
                    Id = memo.Id,
                    Originator = Me,
                    Targets = targets.Select(p => new CirculationTarget { Person = p.Copy() }).ToList()
                };
                memo.Origin = MemoOrigin.CirculationOriginal;
                var header = HeaderFor(window);
                header.Type = MemoHeader.TypeCirculation;
                var (first, failures) = await DeliverCirculationAsync(memo.Circulation, header, rtf, files, 0, toast);
                Save(memo);
                window.ApplyLook();
                toast.Finish(first is null ? "회람을 받을 사람이 없습니다" : $"{first.Label}에게 회람을 보냈습니다");
                if (failures.Count > 0) ShowUndelivered(window, failures, null);
                return;
            }

            int done = 0;
            var results = new List<DeliveryResult>();
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(targets.Select(async target =>
            {
                await gate.WaitAsync();
                try
                {
                    var result = await Net.SendAsync(target, new OutgoingMemo(HeaderFor(window), rtf, files), CancellationToken.None,
                        new Progress<string>(text => toast.Update(text)));
                    lock (results) results.Add(result);
                }
                finally
                {
                    gate.Release();
                    toast.Update($"보내는 중… ({Interlocked.Increment(ref done)}/{targets.Count})");
                }
            }));

            var delivered = results.Where(r => r.Ok).Select(r => r.Target).ToList();
            foreach (var person in delivered.Where(p => memo.SentTo.All(s => s.Ip != p.Ip))) memo.SentTo.Add(person.Copy());
            if (delivered.Count > 0 && memo.Origin == MemoOrigin.Mine) memo.Origin = MemoOrigin.Sent;
            Save(memo);
            window.ApplyLook();
            var failed = results.Where(r => !r.Ok).ToList();
            toast.Finish(failed.Count == 0 ? (targets.Count == 1 ? $"{targets[0].Label}에게 보냈습니다" : $"{targets.Count}명에게 보냈습니다")
                                           : $"{delivered.Count}/{targets.Count}명에게 보냈습니다");
            if (failed.Count > 0 && (Settings.NotifyUndelivered || delivered.Count == 0))
                ShowUndelivered(window, failed, retry: () => _ = ResendAsync(window, failed.Select(f => f.Target).ToList()));
            else if (failed.Count == 0 && Settings.CloseAfterSend) Discard(window);
        }
        finally
        {
            toast.CloseSoon();
        }
    }

    private async Task ResendAsync(MemoWindow window, List<Person> targets)
    {
        string rtf = window.RtfForSending();
        var files = FilesOf(window.Memo);
        var toast = Toast.Show(window, "다시 보내는 중…");
        var results = new List<DeliveryResult>();
        foreach (var target in targets) results.Add(await Net.SendAsync(target, new OutgoingMemo(HeaderFor(window), rtf, files), CancellationToken.None));
        var failed = results.Where(r => !r.Ok).ToList();
        toast.Finish(failed.Count == 0 ? "보냈습니다" : $"{results.Count - failed.Count}/{results.Count}명에게 보냈습니다");
        toast.CloseSoon();
        if (failed.Count > 0) ShowUndelivered(window, failed, () => _ = ResendAsync(window, failed.Select(f => f.Target).ToList()));
    }

    /// <summary>대상 목록 (정음 메모패드 칸): 전달한 사람만 3</summary>
    private static List<CirculationEntry> Entries(Circulation circulation) =>
        circulation.Targets.Select(t => new CirculationEntry
        {
            Person = t.Person.Copy(),
            State = t.State == CirculationPassedState ? CirculationPassedState : 0
        }).ToList();

    /// <summary>
    /// 회람을 targets[start..] 중 아직 전달하지 않은 첫 사람에게. 못 받으면 "못 받음"으로 두고 다음 사람에게.
    /// 아무도 못 받으면 null
    /// </summary>
    private async Task<(Person? Delivered, List<DeliveryResult> Failures)> DeliverCirculationAsync(Circulation circulation, MemoHeader header, string rtf,
        List<OutgoingFile> files, int start, Toast toast)
    {
        var failures = new List<DeliveryResult>();
        header.MemoId = circulation.Id;
        header.Sender = circulation.Originator.Copy();
        for (int i = Math.Max(0, start); i < circulation.Targets.Count; i++)
        {
            var target = circulation.Targets[i];
            if (target.State == CirculationPassedState || IsMeStrict(target.Person)) continue;
            header.Entries = Entries(circulation);
            toast.Update($"{target.Person.Label}에게 회람 보내는 중…");
            var result = await Net.SendAsync(target.Person, new OutgoingMemo(header, rtf, files), CancellationToken.None);
            if (result.Ok)
            {
                target.State = CirculationReceivedState;
                return (target.Person, failures);
            }
            target.State = CirculationMissed;
            failures.Add(result);
        }
        return (null, failures);
    }

    /// <summary>받은 회람을 다음 사람에게 (마지막이면 회람을 시작한 사람에게 결과로)</summary>
    public async Task PassCirculationAsync(MemoWindow window)
    {
        var memo = window.Memo;
        if (memo.Circulation is not { } circulation || circulation.Passed) return;
        if (memo.Secured && !window.Unlock()) return;
        window.SaveNow();

        int me = circulation.MyIndex;
        if (me >= 0 && me < circulation.Targets.Count) circulation.Targets[me].State = CirculationPassedState;
        string rtf = window.RtfForSending();
        var files = FilesOf(memo);
        var toast = Toast.Show(window, "회람 전달 중…");
        try
        {
            _ = MemoService.SendNoteAsync(circulation.Originator.Ip, passed: true, circulation.Id, MyIp);
            var header = HeaderFor(window);
            header.Type = MemoHeader.TypeCirculationPass;
            var (next, failures) = await DeliverCirculationAsync(circulation, header, rtf, files, me + 1, toast);
            if (next is null)
            {
                // 마지막: 시작한 사람에게 결과
                header.Entries = Entries(circulation);
                var back = await Net.SendAsync(circulation.Originator, new OutgoingMemo(header, rtf, files), CancellationToken.None);
                if (!back.Ok) failures.Add(back);
                toast.Finish(back.Ok ? $"회람 결과를 {circulation.Originator.Label}에게 보냈습니다" : "회람 결과를 보내지 못했습니다");
            }
            else toast.Finish($"{next.Label}에게 회람을 전달했습니다");
            circulation.Passed = failures.Count == 0 || next is not null;
            Save(memo);
            window.ApplyLook();
            if (failures.Count > 0) ShowUndelivered(window, failures, null);
        }
        finally { toast.CloseSoon(); }
    }

    /// <summary>"없는 사람 다시 보내기": 아직 전달하지 않은 사람에게 다시 (첫 사람부터 차례로)</summary>
    public async Task ResumeCirculationAsync(MemoWindow window)
    {
        var memo = window.Memo;
        var original = memo.Origin == MemoOrigin.CirculationOriginal ? memo
            : memo.Circulation is { } c ? Store.Find(c.Id) ?? memo : memo;
        if (original.Circulation is not { } circulation) return;
        string rtf = window.RtfForSending();
        var header = HeaderFor(window);
        header.Type = MemoHeader.TypeCirculation;
        var toast = Toast.Show(window, "회람 다시 보내는 중…");
        try
        {
            var (first, failures) = await DeliverCirculationAsync(circulation, header, rtf, FilesOf(memo), 0, toast);
            Save(original);
            toast.Finish(first is null ? "회람을 받을 사람이 없습니다" : $"{first.Label}에게 회람을 보냈습니다");
            if (failures.Count > 0) ShowUndelivered(window, failures, null);
        }
        finally { toast.CloseSoon(); }
    }

    /// <summary>받은 메모가 회람일 때: 결과면 원본에 상태를 합치고, 아니면 내 자리를 찾아 "받음" 알림</summary>
    private void PrepareCirculation(MemoData memo, MemoHeader header, ref string rtf)
    {
        var targets = header.Entries.Select(e => new CirculationTarget { Person = e.Person.Copy(), State = (e.State & 0xFF) == CirculationPassedState ? CirculationPassedState : 0 }).ToList();
        foreach (var target in targets) target.StateText = CirculationStateText(target.State);
        var original = Store.All.FirstOrDefault(m => m.Origin == MemoOrigin.CirculationOriginal && m.Circulation?.Id == header.MemoId);
        bool result = original is not null || IsMeStrict(header.Sender) && header.Type == MemoHeader.TypeCirculationPass;
        if (result)
        {
            memo.Origin = MemoOrigin.CirculationResult;
            memo.From = targets.LastOrDefault(t => t.State == CirculationPassedState)?.Person ?? memo.From;
            memo.Circulation = new Circulation { Id = header.MemoId, Originator = Me, Targets = targets, Passed = true };
            if (original?.Circulation is { } c)
            {
                foreach (var target in c.Targets)
                    if (targets.FirstOrDefault(t => t.Person.Ip == target.Person.Ip) is { State: CirculationPassedState }) target.State = CirculationPassedState;
                Save(original);
                memo.Circulation.Targets = c.Targets.Select(t => new CirculationTarget { Person = t.Person.Copy(), State = t.State, StateText = CirculationStateText(t.State) }).ToList();
            }
            if (Settings.QuoteReceived) rtf = RtfText.Quote(rtf);
            rtf = RtfText.Prepend(rtf, "[회람 결과입니다.]", "");
            return;
        }

        int index = header.Entries.FindIndex(e => e.Person.Ip.Length > 0 && IsMeStrict(e.Person));
        if (index < 0) index = header.Entries.FindIndex(e => e.Person.Id.Length > 0 && string.Equals(e.Person.Id, Me.Id, StringComparison.OrdinalIgnoreCase));
        memo.Origin = MemoOrigin.CirculationReceived;
        memo.From = header.Sender.Copy();
        memo.Circulation = new Circulation { Id = header.MemoId, Originator = header.Sender.Copy(), Targets = targets, MyIndex = index };
        if (index >= 0) targets[index].State = CirculationReceivedState;
        // 정음 메모패드처럼: 받은 내용을 따옴표로 두고 맨 위에 내 의견 칸
        if (Settings.QuoteReceived) rtf = RtfText.Quote(rtf);
        rtf = RtfText.Prepend(rtf, $"[{Me.Label}의 의견]", "");
        _ = MemoService.SendNoteAsync(header.Sender.Ip, passed: false, header.MemoId, MyIp);
    }

    /// <summary>회람 받음 · 전달 알림 → 원본의 대상 상태</summary>
    private void OnCirculationNote(CirculationNote note)
    {
        var original = Store.All.FirstOrDefault(m => m.Origin == MemoOrigin.CirculationOriginal && m.Circulation?.Id == note.MemoId);
        if (original?.Circulation is not { } circulation) return;
        var target = circulation.Targets.FirstOrDefault(t => t.Person.Ip == note.Ip);
        if (target is null) return;
        if (note.Passed) target.State = CirculationPassedState;
        else if (target.State != CirculationPassedState) target.State = CirculationReceivedState;
        target.StateText = CirculationStateText(target.State);
        Save(original);
        WindowOf(original)?.ApplyLook();
    }

    public void ShowCirculationStatus(MemoWindow window)
    {
        var memo = window.Memo;
        var circulation = memo.Origin == MemoOrigin.CirculationResult && memo.Circulation is { } c && Store.Find(c.Id)?.Circulation is { } live ? live : memo.Circulation;
        if (circulation is null) return;
        new CirculationStatusWindow(this, window, circulation).ShowDialog();
    }

    public void ShowSenderInfo(MemoWindow window)
    {
        if (window.Memo.From is not { } from) return;
        Dialogs.Info(window, "보낸 사람 정보", $"이름 : {from.Name}\nID : {from.Id}\nIP : {from.Ip}\nE-Mail : {from.Email}");
    }

    public void AddSenderToAddressBook(MemoWindow window)
    {
        if (window.Memo.From is not { } from) return;
        var edited = PersonDialog.Edit(window, from.Copy(), LoadAddressBook(), "주소록에 추가");
        if (edited is null) return;
        var book = LoadAddressBook();
        var person = book.Upsert(edited.Person);
        foreach (var group in book.Groups)
        {
            bool member = edited.Groups.Contains(group.Name);
            if (member && !group.Members.Contains(person.Ip)) group.Members.Add(person.Ip);
            if (!member) group.Members.Remove(person.Ip);
        }
        SaveAddressBook(book, window);
    }

    private void RememberRecipients(IEnumerable<Person> people)
    {
        foreach (var person in people)
        {
            Settings.RecentRecipients.Remove(person.Ip);
            Settings.RecentRecipients.Insert(0, person.Ip);
        }
        if (Settings.RecentRecipients.Count > 12) Settings.RecentRecipients.RemoveRange(12, Settings.RecentRecipients.Count - 12);
        Settings.Save();
    }

    private void ShowUndelivered(Window owner, List<DeliveryResult> failures, Action? retry)
    {
        var text = string.Join("\n", failures.Select(f => f.Outcome == DeliveryOutcome.Absent
            ? $"• {f.Target.Label}({f.Target.Ip}): 부재중 — {f.Message}"
            : $"• {f.Target.Label}({f.Target.Ip}): {f.Message}"));
        var dialog = new DialogWindow(owner, "보내지 못했습니다", "", 460) { Body = DialogWindow.Text(text) };
        if (retry is not null) dialog.AddButton("다시 보내기", retry, result: true);
        dialog.AddButton("확인", primary: true, result: false);
        dialog.ShowDialog();
    }
}
