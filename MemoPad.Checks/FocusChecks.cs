using System.Net;
using System.Windows;
using MemoPad.Core;
using MemoPad.Ui;

/// <summary>모두 보이기 · 설정: 메모끼리 서로 앞으로 나오려고 활성화를 주고받지 않음</summary>
static partial class Program
{
    private static async Task FocusCalm()
    {
        string folder = Folder("desk-focus");
        var desk = new Desk(new AppSettings { Name = "포커스", UseNetwork = false, StartupConfigured = true, Sound = false, AddressBookFolder = folder },
            new MemoStore(folder), IPAddress.Parse("127.0.0.15"));
        desk.Start();
        int activations = 0;
        void Count(object? sender, EventArgs e) => activations++;
        try
        {
            for (int i = 0; i < 4; i++) desk.NewMemo().ReplaceRtf(RtfText.FromPlain($"메모 {i + 1}"));
            foreach (var window in desk.Windows) window.Activated += Count;
            await Task.Delay(300);

            activations = 0;
            desk.ShowAll();
            await Task.Delay(1500);
            int afterShowAll = activations;
            Assert(afterShowAll <= desk.Windows.Count() + 1, $"모두 보이기 뒤 활성화가 멈춤 (1.5초에 {afterShowAll}번)");

            var top = desk.Windows.OrderBy(w => w.Memo.Modified).Last();
            Assert(top.IsActive && desk.Windows.Count(w => w.IsActive) == 1, "모두 보이기 뒤 맨 위(최근) 메모 하나만 활성");

            // 트레이 메뉴가 닫히며 메모가 잠깐 활성화된 바로 뒤에 설정을 엶
            activations = 0;
            desk.Windows.First().Activate();
            desk.ShowSettings();
            await Task.Delay(1500);
            var settings = Application.Current.Windows.OfType<SettingsWindow>().Single();
            Assert(activations <= 1, $"설정을 연 뒤 메모가 활성화를 주고받지 않음 (1.5초에 {activations}번)");
            Assert(settings.IsActive && !desk.Windows.Any(w => w.IsActive), "설정 창이 활성 상태로 남음 (메모가 뺏지 않음)");
            settings.Close();
        }
        finally
        {
            foreach (var window in desk.Windows.ToList()) window.Close();
            desk.Shutdown();
        }
    }
}
