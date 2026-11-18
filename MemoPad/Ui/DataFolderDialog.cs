using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MemoPad.Core;

namespace MemoPad.Ui;

/// <summary>
/// 데이터 폴더 고르기 창: 처음 실행할 때 (또는 고른 폴더를 쓸 수 없게 됐을 때) · 설정의 [바꾸기…].
/// 경로 칸 (붙여 넣기 가능) + [찾기…] + 고른 폴더에 무슨 일이 생기는지 한 줄
/// </summary>
internal static class DataFolderDialog
{
    /// <summary>처음 실행: 메모를 둘 폴더. [끝내기] · 닫기 = null</summary>
    public static string? Choose(string initial, string? warning = null) =>
        Run(null, "MemoPad 데이터 폴더", "메모와 설정을 저장할 폴더를 고르세요.", initial, warning, current: null);

    /// <summary>설정: 새 데이터 폴더 (지금 폴더와 다르고 쓸 수 있는 폴더). 취소 = null</summary>
    public static string? Change(Window owner, string current) =>
        Run(owner, "데이터 폴더 바꾸기", "새 데이터 폴더를 고르세요. 바꾸면 MemoPad 를 다시 시작합니다.", current, null, current);

    private static string? Run(Window? owner, string title, string intro, string initial, string? warning, string? current)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 8, 0) };
        var find = Chrome.Pill("찾기…", () => { });
        find.Margin = new Thickness(0, 10, 0, 0);
        find.Height = 32;
        var row = new DockPanel();
        DockPanel.SetDock(find, Dock.Right);
        row.Children.Add(find);
        row.Children.Add(box);
        var note = DialogWindow.Text("", muted: true);
        note.Margin = new Thickness(0, 10, 0, 0);

        var panel = new StackPanel();
        if (warning is not null)
        {
            var alert = DialogWindow.Text(warning);
            alert.Foreground = (Brush)Application.Current.Resources["WarningBrush"];
            alert.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(alert);
        }
        panel.Children.Add(DialogWindow.Text(intro));
        panel.Children.Add(row);
        panel.Children.Add(note);

        var dialog = new DialogWindow(owner, title, Icons.Folder, 520) { Body = panel };
        string? chosen = null;
        dialog.AddButton(current is null ? "끝내기" : "취소", result: false);
        var accept = dialog.AddButton(current is null ? "시작" : "옮기기", () =>
        {
            string folder = box.Text.Trim();
            string? problem = current is not null && DataLocation.IsInside(folder, current)
                ? "지금 데이터 폴더 안의 폴더는 고를 수 없습니다."
                : DataLocation.CheckWritable(folder);
            if (problem is not null)
            {
                note.Foreground = (Brush)Application.Current.Resources["WarningBrush"];
                note.Text = "이 폴더를 쓸 수 없습니다: " + problem;
                return;
            }
            chosen = Path.GetFullPath(folder);
            dialog.DialogResult = true;
        }, primary: true);

        // 고른 폴더에 무슨 일이 생기는지
        void Explain()
        {
            string folder = box.Text.Trim();
            bool has = folder.Length > 0 && Directory.Exists(folder) && DataLocation.HasData(folder);
            note.Foreground = (Brush)Application.Current.Resources["MutedTextBrush"];
            accept.IsEnabled = folder.Length > 0;
            if (current is null)
            {
                note.Text = has ? "이 폴더에 있는 메모를 씁니다." : "나중에 설정 → 데이터 폴더에서 바꿀 수 있습니다.";
                return;
            }
            if (folder.Length > 0 && DataLocation.Same(folder, current))
            {
                note.Text = "지금 쓰는 폴더입니다.";
                accept.IsEnabled = false;
            }
            else if (has)
            {
                note.Text = "이 폴더에 있는 메모를 씁니다. 지금 메모는 지금 폴더에 그대로 남습니다.";
                accept.Content = "바꾸기";
            }
            else
            {
                note.Text = "메모와 설정을 이 폴더로 옮깁니다.";
                accept.Content = "옮기기";
            }
        }
        find.Click += (_, _) =>
        {
            if (Pick(dialog, box.Text.Trim()) is { } folder) box.Text = folder;
        };
        box.TextChanged += (_, _) => Explain();
        Explain();
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? chosen : null;
    }

    /// <summary>Windows 폴더 고르기 창 (취소 = null)</summary>
    public static string? Pick(Window? owner, string initial)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "메모를 저장할 폴더", Multiselect = false };
        string start = initial;
        while (start.Length > 0 && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? "";
        if (start.Length > 0) dialog.InitialDirectory = start;
        bool? ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FolderName : null;
    }
}
