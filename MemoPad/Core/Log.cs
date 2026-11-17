using System.IO;

namespace MemoPad.Core;

/// <summary>오류 기록: &lt;설정 폴더&gt;\memopad.log (1MB 가 넘으면 앞쪽을 버림)</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(AppSettings.DataDirectory, "memopad.log");

    public static void Error(string where, Exception? error) => Write($"[{where}] {error}");

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DataDirectory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_048_576)
                {
                    var keep = File.ReadAllText(FilePath);
                    File.WriteAllText(FilePath, keep[(keep.Length / 2)..]);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException) { }
    }
}
