using System.IO;
using System.Text.Json;

namespace MemoPad.Core;

public sealed class MemoFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
}

/// <summary>
/// 메모 저장소: &lt;설정 폴더&gt;\memos\&lt;id&gt;.json (메모마다 한 파일, 바로 전 저장본은 &lt;id&gt;.json.bak), 첨부는 memos\&lt;id&gt;\files\,
/// 폴더 목록은 folders.json. 메모를 고칠 때마다 그 파일만 다시 씀.
/// 지키는 것: 저장하지 못하면 잊지 않고 다시 시도 (<see cref="RetryUnsaved"/>) · 깨진 파일은 .bak 으로 되살리고, 안 되면
/// memos\읽지 못한 메모 로 옮겨 알림 · 하루 한 번 backup\MemoPad-날짜.zip (최근 7개)
/// </summary>
public sealed class MemoStore
{
    /// <summary>읽지 못한 메모 파일을 옮겨 두는 곳 (memos 안)</summary>
    public const string DamagedFolderName = "읽지 못한 메모";
    public const int BackupsKept = 7;

    private readonly string _root;
    private readonly Dictionary<Guid, MemoData> _memos = [];
    private readonly HashSet<Guid> _unsaved = [];

    public MemoStore(string? dataDirectory = null)
    {
        _root = dataDirectory ?? AppSettings.DataDirectory;
        Directory.CreateDirectory(MemoFolderPath);
        Folders = LoadFolders();
        foreach (string file in Directory.EnumerateFiles(MemoFolderPath, "*.json")) Load(file);
    }

    public string MemoFolderPath => Path.Combine(_root, "memos");
    public string DamagedFolderPath => Path.Combine(MemoFolderPath, DamagedFolderName);
    public string BackupFolderPath => Path.Combine(_root, "backup");
    public List<MemoFolder> Folders { get; }
    public IEnumerable<MemoData> All => _memos.Values;
    public IEnumerable<MemoData> Live => _memos.Values.Where(m => !m.Deleted);
    public IEnumerable<MemoData> Trash => _memos.Values.Where(m => m.Deleted);

    /// <summary>시작할 때 깨져 있어 바로 전 저장본(.bak)으로 되살린 메모 파일</summary>
    public List<string> Recovered { get; } = [];
    /// <summary>시작할 때 읽지 못해 <see cref="DamagedFolderPath"/> 로 옮긴 파일</summary>
    public List<string> Damaged { get; } = [];
    /// <summary>저장하지 못해 다시 시도할 메모 수 (0 이면 모두 저장됨)</summary>
    public int UnsavedCount => _unsaved.Count;

    public event Action<MemoData>? Changed;
    public event Action<MemoData>? Removed;
    /// <summary>모두 저장된 상태에서 처음 저장하지 못했을 때 (까닭)</summary>
    public event Action<string>? SaveFailed;
    /// <summary>저장하지 못했던 메모를 모두 저장했을 때</summary>
    public event Action? SaveRecovered;

    public MemoData? Find(Guid id) => _memos.GetValueOrDefault(id);

    public string FilesFolder(MemoData memo) => Path.Combine(MemoFolderPath, memo.Id.ToString("N"), "files");

    public string AttachmentPath(MemoData memo, string name) => Path.Combine(FilesFolder(memo), name);

    /// <summary>메모 하나를 파일에 씀. 못 쓰면 고친 내용을 그대로 두고 나중에 다시 씀 (<see cref="RetryUnsaved"/>)</summary>
    public bool Save(MemoData memo)
    {
        _memos[memo.Id] = memo;
        bool saved = Write(memo);
        Changed?.Invoke(memo);
        return saved;
    }

    /// <summary>저장하지 못했던 메모를 다시 씀 (Desk 가 10초마다 · 끝낼 때)</summary>
    public void RetryUnsaved()
    {
        foreach (Guid id in _unsaved.ToList())
        {
            if (_memos.TryGetValue(id, out var memo)) Write(memo);
            else _unsaved.Remove(id);
        }
    }

    private bool Write(MemoData memo)
    {
        try
        {
            Files.WriteAtomic(PathOf(memo.Id), JsonSerializer.Serialize(memo, AppSettings.Json), keepBackup: true);
            if (_unsaved.Remove(memo.Id) && _unsaved.Count == 0) SaveRecovered?.Invoke();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            bool first = _unsaved.Count == 0;
            if (_unsaved.Add(memo.Id)) Log.Error("저장 " + memo.Id.ToString("N"), error);
            if (first) SaveFailed?.Invoke(error.Message);
            return false;
        }
    }

    /// <summary>
    /// 메모 파일 하나 읽기. 내용이 깨졌으면 바로 전 저장본(.bak)으로 되살리고, 그것도 안 되면 읽지 못한 메모 폴더로 옮김.
    /// 잠깐 열 수 없는 것(다른 프로그램이 잡고 있음 등)은 건드리지 않고 이번만 건너뜀
    /// </summary>
    private void Load(string file)
    {
        string text;
        try { text = Files.ReadShared(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Error("읽기 " + Path.GetFileName(file), error);
            return;
        }
        if (Parse(text) is { } memo)
        {
            _memos[memo.Id] = memo;
            return;
        }

        string backup = file + ".bak";
        string? previousText = null;
        try { if (File.Exists(backup)) previousText = Files.ReadShared(backup); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (previousText is not null && Parse(previousText) is { } previous)
        {
            _memos[previous.Id] = previous;
            try { Files.WriteAtomic(file, previousText); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _unsaved.Add(previous.Id); }
            Recovered.Add(Path.GetFileName(file));
            Log.Write($"[메모 되살림] {Path.GetFileName(file)}: 깨져 있어 바로 전 저장본으로 되살림");
            return;
        }

        try
        {
            Directory.CreateDirectory(DamagedFolderPath);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(file, Path.Combine(DamagedFolderPath, $"{Path.GetFileNameWithoutExtension(file)}-{stamp}.json"));
            if (File.Exists(backup)) File.Move(backup, Path.Combine(DamagedFolderPath, $"{Path.GetFileNameWithoutExtension(file)}-{stamp}.json.bak"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Damaged.Add(Path.GetFileName(file));
        Log.Write($"[메모 읽지 못함] {Path.GetFileName(file)}: {DamagedFolderName} 폴더로 옮김");
    }

    private static MemoData? Parse(string text)
    {
        try { return JsonSerializer.Deserialize<MemoData>(text, AppSettings.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// 하루 한 번 백업: backup\MemoPad-yyyy-MM-dd.zip 에 메모 파일 · 폴더 목록 · 주소록 · 설정 (첨부 파일은 빼고). 최근 7개만 남김.
    /// 시작할 때 뒤에서 돌림 (읽는 중 바뀌는 파일은 건너뜀)
    /// </summary>
    public string? BackupDaily(DateTime? today = null)
    {
        try
        {
            Directory.CreateDirectory(BackupFolderPath);
            string target = Path.Combine(BackupFolderPath, $"MemoPad-{(today ?? DateTime.Today):yyyy-MM-dd}.zip");
            if (!File.Exists(target))
            {
                string temp = target + ".tmp";
                using (var zip = System.IO.Compression.ZipFile.Open(temp, System.IO.Compression.ZipArchiveMode.Create))
                {
                    void Add(string path, string entry)
                    {
                        try
                        {
                            string text = Files.ReadShared(path);
                            using var writer = new StreamWriter(zip.CreateEntry(entry, System.IO.Compression.CompressionLevel.Optimal).Open(), new System.Text.UTF8Encoding(false));
                            writer.Write(text);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                    foreach (string file in Directory.EnumerateFiles(MemoFolderPath, "*.json")) Add(file, "memos/" + Path.GetFileName(file));
                    foreach (string name in new[] { "folders.json", "addressbook.json", "settings.json" })
                        if (File.Exists(Path.Combine(_root, name))) Add(Path.Combine(_root, name), name);
                }
                File.Move(temp, target, overwrite: true);
            }
            foreach (var old in new DirectoryInfo(BackupFolderPath).GetFiles("MemoPad-*.zip").OrderByDescending(f => f.Name).Skip(BackupsKept))
                old.Delete();
            return target;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("백업", error);
            return null;
        }
    }

    /// <summary>휴지통으로 (UseTrash 가 꺼져 있으면 바로 지움)</summary>
    public void MoveToTrash(MemoData memo, bool useTrash)
    {
        if (!useTrash) { Delete(memo); return; }
        memo.Deleted = true;
        memo.DeletedAt = DateTime.Now;
        memo.Visible = false;
        Save(memo);
    }

    public void Restore(MemoData memo)
    {
        memo.Deleted = false;
        memo.DeletedAt = null;
        if (memo.Folder.Length > 0 && Folders.All(f => f.Id != memo.Folder)) memo.Folder = "";
        Save(memo);
    }

    public void Delete(MemoData memo)
    {
        _memos.Remove(memo.Id);
        _unsaved.Remove(memo.Id);
        try
        {
            File.Delete(PathOf(memo.Id));
            File.Delete(PathOf(memo.Id) + ".bak");
            string folder = Path.Combine(MemoFolderPath, memo.Id.ToString("N"));
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Removed?.Invoke(memo);
    }

    /// <summary>첨부 파일을 메모 폴더로 복사하고 이름을 돌려줌 (같은 이름이면 " (2)")</summary>
    public string AddAttachment(MemoData memo, string sourcePath, string? name = null)
    {
        string copied = CopyAttachment(memo, sourcePath, name);
        memo.Attachments.Add(copied);
        return copied;
    }

    /// <summary>
    /// 파일이나 폴더(→ 폴더이름.zip)를 메모의 첨부 폴더로 복사하고 이름을 돌려줌. 메모의 첨부 목록은 건드리지 않음
    /// (큰 파일을 화면 밖에서 복사할 때 씀). 복사하다 실패하면 반쯤 쓴 파일은 지움
    /// </summary>
    public string CopyAttachment(MemoData memo, string sourcePath, string? name = null)
    {
        string folder = FilesFolder(memo);
        Directory.CreateDirectory(folder);
        bool isFolder = Directory.Exists(sourcePath);
        string source = Path.TrimEndingDirectorySeparator(sourcePath);
        string target = Files.Unique(folder, Files.SafeName(name ?? Path.GetFileName(source) + (isFolder ? ".zip" : "")));
        try
        {
            if (isFolder) System.IO.Compression.ZipFile.CreateFromDirectory(source, target, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: true);
            else File.Copy(sourcePath, target);
        }
        catch
        {
            try { File.Delete(target); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return Path.GetFileName(target);
    }

    public string AddAttachment(MemoData memo, string name, byte[] content)
    {
        string folder = FilesFolder(memo);
        Directory.CreateDirectory(folder);
        string target = Files.Unique(folder, Files.SafeName(name));
        File.WriteAllBytes(target, content);
        memo.Attachments.Add(Path.GetFileName(target));
        return Path.GetFileName(target);
    }

    /// <summary>첨부를 뺌. 파일이 다른 프로그램에 열려 있어 지울 수 없으면 그대로 두고 false</summary>
    public bool RemoveAttachment(MemoData memo, string name)
    {
        try { File.Delete(AttachmentPath(memo, name)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        memo.Attachments.Remove(name);
        return true;
    }

    /// <summary>복사본 (보낸 메모 · 전달 · 회람 결과): 새 id, 첨부도 복사</summary>
    public MemoData Duplicate(MemoData memo)
    {
        var json = JsonSerializer.Serialize(memo, AppSettings.Json);
        var copy = JsonSerializer.Deserialize<MemoData>(json, AppSettings.Json)!;
        copy.Id = Guid.NewGuid();
        copy.Attachments = [];
        foreach (string name in memo.Attachments)
        {
            string source = AttachmentPath(memo, name);
            if (File.Exists(source)) AddAttachment(copy, source, name);
        }
        return copy;
    }

    // ── 폴더 ──
    public MemoFolder AddFolder(string name)
    {
        var folder = new MemoFolder { Name = name };
        Folders.Add(folder);
        SaveFolders();
        return folder;
    }

    public void RenameFolder(MemoFolder folder, string name)
    {
        folder.Name = name;
        SaveFolders();
    }

    public void RemoveFolder(MemoFolder folder)
    {
        Folders.Remove(folder);
        SaveFolders();
        foreach (var memo in _memos.Values.Where(m => m.Folder == folder.Id).ToList())
        {
            memo.Folder = "";
            Save(memo);
        }
    }

    private string PathOf(Guid id) => Path.Combine(MemoFolderPath, id.ToString("N") + ".json");
    private string FoldersPath => Path.Combine(_root, "folders.json");

    private List<MemoFolder> LoadFolders()
    {
        try
        {
            if (File.Exists(FoldersPath) && JsonSerializer.Deserialize<List<MemoFolder>>(File.ReadAllText(FoldersPath), AppSettings.Json) is { } list) return list;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    private void SaveFolders()
    {
        try { Files.WriteAtomic(FoldersPath, JsonSerializer.Serialize(Folders, AppSettings.Json)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
