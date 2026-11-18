using System.Net;
using MemoPad.Core;
using MemoPad.Ui;

/// <summary>메모 창 자리: 접기(작은 사각형) · 옮겨서 펼치기 · 정렬</summary>
static partial class Program
{
    private static async Task CollapseSquare()
    {
        string folder = Folder("desk-collapse");
        var desk = new Desk(new AppSettings { Name = "접기", UseNetwork = false, StartupConfigured = true, Sound = false, AddressBookFolder = folder },
            new MemoStore(folder), IPAddress.Parse("127.0.0.12"));
        desk.Start();
        try
        {
            var window = desk.NewMemo();
            window.ReplaceRtf(RtfText.FromPlain("접기 시험 첫 줄\n둘째 줄"));
            window.MoveTo(400, 300);
            await Task.Delay(150);
            var open = window.PixelBounds;
            int width = open.Right - open.Left, height = open.Bottom - open.Top;

            window.ToggleCollapse();
            await Task.Delay(150);
            var square = window.PixelBounds;
            int side = square.Right - square.Left;
            Equal(side, square.Bottom - square.Top, "접으면 정사각형");
            Assert(side is >= 22 and <= 64, "작은 사각형: " + side);
            Equal((open.Right, open.Top), (square.Right, square.Top), "사각형은 메모의 오른쪽 위");

            // 사각형을 끌어 옮긴 것처럼 → 펼치면 그 자리를 오른쪽 위로 해서 원래 크기로
            Native.SetWindowPos(Native.Handle(window), IntPtr.Zero, 900, 350, side, side, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            window.ToggleCollapse();
            await Task.Delay(150);
            var again = window.PixelBounds;
            Equal((900 + side, 350), (again.Right, again.Top), "옮긴 사각형 자리에서 펼침");
            Equal((width, height), (again.Right - again.Left, again.Bottom - again.Top), "펼친 크기 그대로");

            // 정렬: 접은 메모는 사각형이 정렬 자리에
            window.ToggleCollapse();
            window.MoveTo(500, 200);
            await Task.Delay(150);
            var moved = window.PixelBounds;
            Equal((500, 200, side), (moved.Left, moved.Top, moved.Right - moved.Left), "정렬 자리에 사각형");

            // 다시 켜도 접힌 채로 같은 자리 (저장된 자리는 펼친 메모 기준)
            var memo = window.Memo;
            window.Close();
            var reopened = desk.Open(memo, activate: false);
            await Task.Delay(150);
            var restored = reopened.PixelBounds;
            Equal((moved.Left, moved.Top, side), (restored.Left, restored.Top, restored.Right - restored.Left), "다시 열어도 같은 사각형");
        }
        finally
        {
            foreach (var window in desk.Windows.ToList()) window.Close();
            desk.Shutdown();
        }
    }
}
