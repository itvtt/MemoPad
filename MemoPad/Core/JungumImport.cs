using System.Buffers.Binary;
using System.IO;
using System.Text;
using MemoPad.Net;
using Microsoft.Win32;

namespace MemoPad.Core;

/// <summary>
/// 정음 메모패드의 메모 파일(*.HMD, "SAMSUNGMEMO_1.2")을 MemoPad 메모로 가져옴.
/// 메모 id 가 같으면 다시 가져오지 않음. 하위 폴더는 같은 이름의 메모 관리기 폴더로
/// </summary>
public static class JungumImport
{
    private const string Magic = "SAMSUNGMEMO_";

    /// <summary>정음 메모패드가 메모를 두는 곳 (설치 폴더의 Desktop, 권한 없이 쓴 경우 VirtualStore, 설정한 데이터 폴더)</summary>
    public static IEnumerable<string> DefaultFolders()
    {
        var found = new List<string>();
        string? configured = null;
        try { configured = Registry.CurrentUser.OpenSubKey(@"Software\SAMSUNG\Accessory\JUMemoPad")?.GetValue("DataFolderCur") as string; }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        if (!string.IsNullOrWhiteSpace(configured)) found.Add(configured);
        foreach (var programs in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string root = Environment.GetFolderPath(programs);
            if (root.Length == 0) continue;
            string install = Path.Combine(root, "SamSung", "JungUmMemoPad", "Desktop");
            found.Add(install);
            string relative = install.Substring(Path.GetPathRoot(install)!.Length);
            found.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualStore", relative));
        }
        return found.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static int ImportAll(Ui.Desk desk, IEnumerable<string> folders, out List<string> scanned)
    {
        scanned = [];
        int count = 0;
        string owner = "";
        try { owner = Registry.CurrentUser.OpenSubKey(@"Software\SAMSUNG\Accessory\JUMemoPad")?.GetValue("ID") as string ?? ""; }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        foreach (string folder in folders)
        {
            scanned.Add(folder);
            foreach (string file in Directory.EnumerateFiles(folder, "*.HMD", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(folder, Path.GetDirectoryName(file)!);
                if (relative.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("RECYCLED", StringComparison.OrdinalIgnoreCase))) continue;
                MemoData? memo;
                try { memo = Read(File.ReadAllBytes(file), owner); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                if (memo is null || desk.Store.Find(memo.Id) is not null) continue;
                if (relative != "." && relative.Length > 0)
                {
                    string name = relative.Split(Path.DirectorySeparatorChar)[0];
                    memo.Folder = (desk.Store.Folders.FirstOrDefault(f => f.Name == name) ?? desk.Store.AddFolder(name)).Id;
                }
                memo.Text = RtfText.Plain(memo.Rtf);
                desk.Save(memo);
                count++;
            }
        }
        return count;
    }

    /// <summary>HMD 한 개 → 메모 (모르는 형식이면 null)</summary>
    public static MemoData? Read(byte[] data, string ownerId = "")
    {
        if (data.Length < 0x200 || Encoding.ASCII.GetString(data, 0, Magic.Length) != Magic) return null;
        int rtfAt = IndexOf(data, Encoding.ASCII.GetBytes("{\\rtf"), 0x40);
        if (rtfAt < 0) return null;
        int rtfEnd = Array.IndexOf(data, (byte)0, rtfAt);
        string rtf = JuProtocol.Ansi.GetString(data, rtfAt, (rtfEnd < 0 ? data.Length : rtfEnd) - rtfAt);

        int kind = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x11));
        var memo = new MemoData
        {
            Id = new Guid(data.AsSpan(0x15, 16)),
            X = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x29)),
            Y = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x2D)),
            Width = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x31)), 120, 1600),
            Height = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x35)), 60, 1200),
            Color = MemoColors.FromJungum(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x39))),
            Modified = FileTime(data, 0x41),
            Created = FileTime(data, 0x49),
            Rtf = rtf,
            Visible = false
        };
        var sender = new Person
        {
            Ip = JuProtocol.ReadWide(data, 0x61, 256),
            Id = JuProtocol.ReadWide(data, 0x161, 128),
            Name = JuProtocol.ReadWide(data, 0x1E1, 128)
        };
        if (sender.Id.Length > 0 && !sender.Id.Equals(ownerId, StringComparison.OrdinalIgnoreCase) && NetAddress.IsIPv4(sender.Ip))
        {
            memo.From = sender;
            memo.ReceivedAt = memo.Created;
            memo.Origin = kind == 3 ? MemoOrigin.CirculationResult : MemoOrigin.Received;
        }
        return memo;
    }

    private static DateTime FileTime(byte[] data, int at) => MemoHeader.FromOle(BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(at)));

    private static int IndexOf(byte[] data, byte[] pattern, int start)
    {
        for (int i = start; i <= data.Length - pattern.Length; i++)
        {
            int j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }
}
