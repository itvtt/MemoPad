using System.IO;
using Microsoft.Win32;

namespace MemoPad.Core;

/// <summary>
/// 데이터 폴더 (메모 · 설정 · 기록). 정하는 순서: MEMOPAD_DATA_DIR (확인 · 시험) → 처음 실행 때 고른 폴더
/// (HKCU\Software\MemoPad 의 DataFolder, 앱을 지웠다 다시 설치해도 남음) → 기본 %LOCALAPPDATA%\MemoPad.
/// 설정에서 바꾸면 MemoPad 의 파일만 새 폴더로 옮기고 다시 시작
/// </summary>
public static class DataLocation
{
    private const string KeyPath = @"Software\MemoPad";
    private const string ValueName = "DataFolder";

    /// <summary>데이터 폴더에 MemoPad 가 두는 것 (옮길 때 이것만 옮기고, 다른 파일은 건드리지 않음)</summary>
    public static readonly string[] Items = ["settings.json", "folders.json", "addressbook.json", "memopad.log", "memos"];

    public static string Default => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoPad");

    /// <summary>시험 폴더 (MEMOPAD_DATA_DIR). 있으면 기록을 읽지도 쓰지도 않음</summary>
    public static string? TestFolder => Environment.GetEnvironmentVariable("MEMOPAD_DATA_DIR") is { Length: > 0 } folder ? folder : null;

    /// <summary>처음 실행 때 고른 폴더 (없으면 null)</summary>
    public static string? Saved
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) is string { Length: > 0 } folder ? folder : null;
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
    }

    public static void Save(string folder)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(ValueName, folder);
    }

    public static string Current => TestFolder ?? Saved ?? Default;

    /// <summary>
    /// 처음 실행이라 물어야 함: 고른 적이 없고, 기본 폴더에 예전 메모도 없음 (예전 버전에서 쓰던 기본 폴더는 그대로 씀)
    /// </summary>
    public static bool NeedsChoice => TestFolder is null && Saved is null && !HasData(Default);

    /// <summary>이 폴더에 MemoPad 데이터가 있음</summary>
    public static bool HasData(string folder) =>
        File.Exists(Path.Combine(folder, "settings.json")) || Directory.Exists(Path.Combine(folder, "memos"));

    /// <summary>폴더를 만들고 써 보기. 문제가 있으면 그 까닭, 없으면 null</summary>
    public static string? CheckWritable(string folder)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder)) return "전체 경로가 아닙니다.";
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, $".memopad-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return error switch
            {
                UnauthorizedAccessException => "이 폴더에 쓸 권한이 없습니다.",
                DirectoryNotFoundException or DriveNotFoundException => "드라이브나 폴더를 찾을 수 없습니다.",
                PathTooLongException => "경로가 너무 깁니다.",
                ArgumentException or NotSupportedException => "폴더 경로로 쓸 수 없는 글자가 있습니다.",
                _ => "폴더를 만들거나 쓸 수 없습니다. (" + error.Message + ")"
            };
        }
    }

    /// <summary>같은 폴더인가 (대소문자 · 끝의 \ 무시). 경로가 아니면 false</summary>
    public static bool Same(string a, string b) =>
        Full(a) is { } x && Full(b) is { } y && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    /// <summary>inner 가 outer 안의 폴더인가. 경로가 아니면 false</summary>
    public static bool IsInside(string inner, string outer) =>
        Full(inner) is { } x && Full(outer) is { } y && x.StartsWith(y + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? Full(string path)
    {
        try { return path.Length == 0 ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>MemoPad 데이터(Items)를 from → to 로 옮김: 모두 복사한 뒤에 옛 폴더에서 지움 (복사가 실패하면 옛 데이터는 그대로)</summary>
    public static void Move(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string item in Items)
        {
            string source = Path.Combine(from, item), target = Path.Combine(to, item);
            if (File.Exists(source)) File.Copy(source, target, overwrite: true);
            else if (Directory.Exists(source)) CopyFolder(source, target);
        }
        foreach (string item in Items)
        {
            string source = Path.Combine(from, item);
            try
            {
                if (File.Exists(source)) File.Delete(source);
                else if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        // 옛 폴더가 기본 폴더이고 비었으면 지움
        try { if (Same(from, Default) && !Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (string folder in Directory.GetDirectories(source)) CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
    }
}
