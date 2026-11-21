using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoPad.Core;

/// <summary>설정: 데이터 폴더(기본 %LOCALAPPDATA%\MemoPad)의 settings.json (설치 폴더 밖이라 업데이트 · 재설치해도 남음)</summary>
public sealed class AppSettings
{
    /// <summary>데이터 폴더 (DataLocation). 시작할 때 정하고, 설정에서 바꾸면 다시 시작</summary>
    public static string DataDirectory { get; private set; } = DataLocation.Current;

    /// <summary>처음 실행 때 고른 폴더를 씀 (설정 · 메모를 읽기 전에)</summary>
    public static void UseDataDirectory(string folder) => DataDirectory = folder;

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── 내 정보 (주고받을 때 상대에게 보이는 이름) ──
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string ChatName { get; set; } = "";
    /// <summary>비우면 자동 (네트워크 카드가 여럿이면 고름)</summary>
    public string Ip { get; set; } = "";
    public bool UseNetwork { get; set; } = true;

    // ── 받기 · 보내기 ──
    public bool Sound { get; set; } = true;
    public string SoundFile { get; set; } = "";
    public bool ReceivedOnTop { get; set; }
    public bool QuoteReceived { get; set; } = true;
    public bool CloseAfterSend { get; set; }
    public bool NotifyUndelivered { get; set; } = true;
    /// <summary>받은 메모를 다시 보낼 주소 (; 로 구분)</summary>
    public string ForwardTo { get; set; } = "";
    public bool Absent { get; set; }
    public string AbsentText { get; set; } = "저는 현재 부재중입니다.";
    /// <summary>주소록 파일(addressbook.json)이 있는 폴더. 비우면 설정 폴더. 공유 폴더로 하면 여러 사람이 함께 씀</summary>
    public string AddressBookFolder { get; set; } = "";
    public List<string> RecentRecipients { get; set; } = [];

    // ── 새 메모 기본값 ──
    public string DefaultColor { get; set; } = MemoColors.Default;
    public string FontFamily { get; set; } = "Malgun Gothic";
    public double FontSize { get; set; } = 13;
    public int DefaultWidth { get; set; } = 260;
    public int DefaultHeight { get; set; } = 180;
    public bool DefaultTop { get; set; }
    public double DefaultOpacity { get; set; } = 1;
    public double CollapsedOpacity { get; set; } = 1;
    public bool ScrollBar { get; set; }
    public bool DefaultStatusBar { get; set; }

    // ── 일반 ──
    public bool StartupConfigured { get; set; }
    public bool ManagerAtStart { get; set; }
    public bool AlwaysShowTitle { get; set; } = true;
    public bool HideInactiveBorder { get; set; }
    public bool UseTrash { get; set; } = true;
    public int DateFormat { get; set; }
    public int TimeFormat { get; set; }
    public int ArrangeGap { get; set; } = 10;
    public bool ArrangeSkipExpanded { get; set; }

    // ── 단축키 (전역, HotkeyAction 이름 → 키. 없으면 없음) ──
    public Dictionary<string, HotkeyBinding>? Hotkeys { get; set; }

    // ── 보안 ──
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public bool SecureReceived { get; set; }

    // ── 대화 ──
    public string[] ChatMacros { get; set; } = ["", "안녕하세요?", "안녕히계세요.", "", "", "", "", "", "", "", "", ""];

    // ── 창 위치 ──
    public double[]? ManagerBounds { get; set; }

    [JsonIgnore] public bool HasPassword => PasswordHash.Length > 0;

    [JsonIgnore] public string AddressBookPath =>
        Path.Combine(string.IsNullOrWhiteSpace(AddressBookFolder) ? DataDirectory : AddressBookFolder, "addressbook.json");

    public Person Me(string ip) => new()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? Environment.UserName : Name,
        Id = string.IsNullOrWhiteSpace(Id) ? Environment.UserName : Id,
        Email = Email,
        Ip = ip
    };

    public string MyChatName => string.IsNullOrWhiteSpace(ChatName) ? (string.IsNullOrWhiteSpace(Id) ? Environment.UserName : Id) : ChatName;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) is { } settings)
            {
                var macros = settings.ChatMacros ?? [];
                Array.Resize(ref macros, 12);
                settings.ChatMacros = macros.Select(m => m ?? "").ToArray();
                return settings;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Files.WriteAtomic(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // ── 암호: PBKDF2 (설정 파일에는 해시만) ──
    public void SetPassword(string password)
    {
        if (password.Length == 0) { PasswordHash = PasswordSalt = ""; return; }
        byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        PasswordSalt = Convert.ToBase64String(salt);
        PasswordHash = Convert.ToBase64String(Hash(password, salt));
    }

    public bool CheckPassword(string password)
    {
        if (!HasPassword) return true;
        try
        {
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Hash(password, Convert.FromBase64String(PasswordSalt)), Convert.FromBase64String(PasswordHash));
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Hash(string password, byte[] salt) =>
        System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
}

internal static class Files
{
    /// <summary>
    /// 임시 파일에 쓰고 디스크까지 내린 뒤 바꿔 끼움 (쓰다가 꺼지거나 전기가 나가도 예전 파일이 남음).
    /// keepBackup 이면 바로 전 내용을 path.bak 으로 남김 (파일이 깨졌을 때 되살리는 데 씀)
    /// </summary>
    public static void WriteAtomic(string path, string text, bool keepBackup = false)
    {
        string temp = path + ".tmp";
        byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (keepBackup && File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>다른 곳이 쓰는 중이어도 읽기 (백업 · 되살리기용)</summary>
    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>같은 이름이 있으면 "이름 (2).확장자"</summary>
    public static string Unique(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        if (!File.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(path)) return path;
        }
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        clean = clean.Trim().TrimEnd('.');
        return clean.Length == 0 ? "file" : clean;
    }
}
