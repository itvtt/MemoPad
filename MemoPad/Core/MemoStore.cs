using System.IO;
using System.Text.Json;

namespace MemoPad.Core;

public sealed class MemoFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
}

/// <summary>
/// 메모 저장소: &lt;설정 폴더&gt;\memos\&lt;id&gt;.json (메모마다 한 파일), 첨부는 memos\&lt;id&gt;\files\,
/// 폴더 목록은 folders.json. 메모를 고칠 때마다 그 파일만 다시 씀
/// </summary>
public sealed class MemoStore
{
    private readonly string _root;
    private readonly Dictionary<Guid, MemoData> _memos = [];

    public MemoStore(string? dataDirectory = null)
    {
        _root = dataDirectory ?? AppSettings.DataDirectory;
        Directory.CreateDirectory(MemoFolderPath);
        Folders = LoadFolders();
        foreach (string file in Directory.EnumerateFiles(MemoFolderPath, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<MemoData>(File.ReadAllText(file), AppSettings.Json) is { } memo) _memos[memo.Id] = memo;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    public string MemoFolderPath => Path.Combine(_root, "memos");
    public List<MemoFolder> Folders { get; }
    public IEnumerable<MemoData> All => _memos.Values;
    public IEnumerable<MemoData> Live => _memos.Values.Where(m => !m.Deleted);
    public IEnumerable<MemoData> Trash => _memos.Values.Where(m => m.Deleted);

    public event Action<MemoData>? Changed;
    public event Action<MemoData>? Removed;

    public MemoData? Find(Guid id) => _memos.GetValueOrDefault(id);

    public string FilesFolder(MemoData memo) => Path.Combine(MemoFolderPath, memo.Id.ToString("N"), "files");

    public string AttachmentPath(MemoData memo, string name) => Path.Combine(FilesFolder(memo), name);

    public void Save(MemoData memo)
    {
        _memos[memo.Id] = memo;
        try { Files.WriteAtomic(PathOf(memo.Id), JsonSerializer.Serialize(memo, AppSettings.Json)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke(memo);
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
        try
        {
            File.Delete(PathOf(memo.Id));
            string folder = Path.Combine(MemoFolderPath, memo.Id.ToString("N"));
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Removed?.Invoke(memo);
    }

    /// <summary>첨부 파일을 메모 폴더로 복사하고 이름을 돌려줌 (같은 이름이면 " (2)")</summary>
    public string AddAttachment(MemoData memo, string sourcePath, string? name = null)
    {
        string folder = FilesFolder(memo);
        Directory.CreateDirectory(folder);
        string target = Files.Unique(folder, Files.SafeName(name ?? Path.GetFileName(sourcePath)));
        File.Copy(sourcePath, target);
        memo.Attachments.Add(Path.GetFileName(target));
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

    public void RemoveAttachment(MemoData memo, string name)
    {
        memo.Attachments.Remove(name);
        try { File.Delete(AttachmentPath(memo, name)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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
