using System.IO;
using System.Text;
using System.Text.Json;

namespace MemoPad.Core;

public sealed class AddressGroup
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>구성원 (사람의 IP 로 찾음)</summary>
    public List<string> Members { get; set; } = [];
}

/// <summary>
/// 주소록: 사람(이름 · ID · IP · E-Mail)과 그룹. 파일 하나(addressbook.json)라서 주소록 폴더를 공유 폴더로 두면 여러 사람이 함께 씀.
/// 바꿀 때마다 파일을 다시 읽고 고쳐 씀 (다른 사람이 고친 것을 덮지 않게)
/// </summary>
public sealed class AddressBook
{
    public List<Person> People { get; set; } = [];
    public List<AddressGroup> Groups { get; set; } = [];

    public static AddressBook Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<AddressBook>(File.ReadAllText(path), AppSettings.Json) is { } book) return book;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new AddressBook();
    }

    /// <exception cref="IOException">공유 폴더에 쓸 수 없을 때</exception>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Files.WriteAtomic(path, JsonSerializer.Serialize(this, AppSettings.Json));
                return;
            }
            catch (IOException) when (attempt < 4) { Thread.Sleep(150); }
        }
    }

    public Person? FindByIp(string ip) => People.FirstOrDefault(p => p.Ip == ip);

    /// <summary>이름 · ID · IP 로 찾기 (보내기 창의 "이름 입력")</summary>
    public IEnumerable<Person> Search(string text)
    {
        text = text.Trim();
        return text.Length == 0 ? People : People.Where(p => Matches(p, text));
    }

    public static bool Matches(Person p, string text) =>
        p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || p.Id.Contains(text, StringComparison.OrdinalIgnoreCase)
        || p.Ip.StartsWith(text, StringComparison.Ordinal)
        || Hangul.Initials(p.Name).StartsWith(text, StringComparison.Ordinal);

    public IEnumerable<Person> MembersOf(AddressGroup group) =>
        group.Members.Select(FindByIp).Where(p => p is not null).Select(p => p!);

    /// <summary>사람 추가 또는 같은 IP 면 고침</summary>
    public Person Upsert(Person person)
    {
        var existing = FindByIp(person.Ip);
        if (existing is null) { People.Add(person); return person; }
        existing.Name = person.Name;
        existing.Id = person.Id;
        existing.Email = person.Email;
        return existing;
    }

    public void Remove(Person person)
    {
        People.Remove(person);
        foreach (var group in Groups) group.Members.Remove(person.Ip);
    }

    public void Rename(Person person, string newIp)
    {
        foreach (var group in Groups)
        {
            int index = group.Members.IndexOf(person.Ip);
            if (index >= 0) group.Members[index] = newIp;
        }
        person.Ip = newIp;
    }

    // ── CSV (불러오기 · 내보내기): 이름,ID,IP,E-Mail,그룹 ──
    public string ToCsv()
    {
        var text = new StringBuilder("이름,ID,IP,E-Mail,그룹\r\n");
        foreach (var person in People)
        {
            string groups = string.Join(";", Groups.Where(g => g.Members.Contains(person.Ip)).Select(g => g.Name));
            text.Append(string.Join(",", new[] { person.Name, person.Id, person.Ip, person.Email, groups }.Select(Csv))).Append("\r\n");
        }
        return text.ToString();
    }

    /// <summary>CSV 를 합침. 더한 · 고친 사람 수</summary>
    public int MergeCsv(string csv)
    {
        int count = 0;
        foreach (var row in ParseCsv(csv).Skip(1))
        {
            if (row.Count < 3 || !Net.NetAddress.IsIPv4(row[2].Trim())) continue;
            var person = Upsert(new Person { Name = row[0].Trim(), Id = row[1].Trim(), Ip = row[2].Trim(), Email = row.Count > 3 ? row[3].Trim() : "" });
            if (row.Count > 4)
                foreach (string name in row[4].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var group = Groups.FirstOrDefault(g => g.Name == name) ?? AddGroup(name);
                    if (!group.Members.Contains(person.Ip)) group.Members.Add(person.Ip);
                }
            count++;
        }
        return count;
    }

    public AddressGroup AddGroup(string name)
    {
        var group = new AddressGroup { Name = name };
        Groups.Add(group);
        return group;
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r'));
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
