using System.Text.Json.Serialization;

namespace MemoPad.Core;

public enum MemoKind { Text, Drawing }

/// <summary>메모가 생긴 곳 (메모 관리기의 "종류")</summary>
public enum MemoOrigin
{
    Mine,
    Received,
    Sent,
    CirculationOriginal,
    CirculationReceived,
    CirculationResult
}

public enum MemoLayer { Normal, Top, Bottom }

/// <summary>사람 하나 (주소록 · 보낸 사람 · 회람 대상)</summary>
public sealed class Person
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Email { get; set; } = "";

    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(Name) ? (string.IsNullOrWhiteSpace(Id) ? Ip : Id) : Name;
    public Person Copy() => new() { Name = Name, Id = Id, Ip = Ip, Email = Email };
    public override string ToString() => $"{Label} ({Ip})";
}

/// <summary>회람 대상 하나와 그 사람의 상태</summary>
public sealed class CirculationTarget
{
    public Person Person { get; set; } = new();
    /// <summary>정음 메모패드 형식의 상태 칸 그대로 (받음 · 전달 · 없음 등)</summary>
    public int State { get; set; }
    public string StateText { get; set; } = "";
}

public sealed class Circulation
{
    /// <summary>회람 id (정음 메모패드와 주고받는 메모 id, 시작한 사람의 원본 메모 id)</summary>
    public Guid Id { get; set; }
    public Person Originator { get; set; } = new();
    public List<CirculationTarget> Targets { get; set; } = [];
    /// <summary>이 PC 가 회람을 받았을 때 목록에서 내 자리 (-1 = 원본)</summary>
    public int MyIndex { get; set; } = -1;
    public bool Passed { get; set; }
}

public sealed class Alarm
{
    public DateTime At { get; set; }
    /// <summary>몇 분 먼저 알릴지 (0 = 정시)</summary>
    public int AdvanceMinutes { get; set; }
    public string Sound { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Done { get; set; }

    [JsonIgnore] public DateTime RingAt => At.AddMinutes(-AdvanceMinutes);
}

public sealed class MemoData
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MemoKind Kind { get; set; }
    public MemoOrigin Origin { get; set; }
    /// <summary>메모 관리기 폴더 id ("" = 내 메모). 휴지통은 <see cref="Deleted"/></summary>
    public string Folder { get; set; } = "";

    /// <summary>본문 RTF (그림 메모는 비어 있음)</summary>
    public string Rtf { get; set; } = "";
    /// <summary>그림 메모의 선 (ISF, base64)</summary>
    public string Ink { get; set; } = "";
    /// <summary>찾기 · 목록용 일반 글자 (저장할 때 함께 씀)</summary>
    public string Text { get; set; } = "";

    public string Color { get; set; } = MemoColors.Default;
    /// <summary>창 위치 · 크기 (실제 화소, 화면 좌표)</summary>
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public int Width { get; set; }
    public int Height { get; set; }
    public MemoLayer Layer { get; set; }
    /// <summary>바탕화면에 보이는지 (숨기면 메모 관리기에만)</summary>
    public bool Visible { get; set; } = true;
    public bool Collapsed { get; set; }
    public bool Locked { get; set; }
    public bool ReadOnly { get; set; }
    public bool Secured { get; set; }
    /// <summary>0.2 ~ 1</summary>
    public double Opacity { get; set; } = 1;
    public bool StatusBar { get; set; }
    public double Zoom { get; set; } = 1;

    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
    public DateTime? ReceivedAt { get; set; }

    public Person? From { get; set; }
    public List<Person> SentTo { get; set; } = [];
    public Circulation? Circulation { get; set; }
    public Alarm? Alarm { get; set; }
    /// <summary>첨부 파일 이름 (메모 폴더의 files\ 안)</summary>
    public List<string> Attachments { get; set; } = [];

    public bool Deleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>받은 메모를 처음 열었을 때의 RTF (답장할 때 고쳤는지 보려고)</summary>
    public string ReceivedRtf { get; set; } = "";

    [JsonIgnore] public string FirstLine
    {
        get
        {
            // 회람의 "[홍길동의 의견]" · "[회람 결과입니다.]" 머리와 '>' 는 건너뛰고 본문 첫 줄
            foreach (var line in Text.Split('\n'))
            {
                string trimmed = line.Trim().TrimStart('>', ' ').Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('[') && (trimmed.EndsWith("의 의견]") || trimmed == "[회람 결과입니다.]")) continue;
                return trimmed.Length > 80 ? trimmed[..80] : trimmed;
            }
            return Kind == MemoKind.Drawing ? "(그림)" : "(내용 없음)";
        }
    }

    [JsonIgnore] public string KindText => Origin switch
    {
        MemoOrigin.Received => "받은 메모",
        MemoOrigin.Sent => "보낸 메모",
        MemoOrigin.CirculationOriginal => "회람 원본",
        MemoOrigin.CirculationReceived => "받은 회람",
        MemoOrigin.CirculationResult => "회람 결과",
        _ => Kind == MemoKind.Drawing ? "그림" : "메모"
    };
}

/// <summary>메모 바탕색 (밝은 판: 글자는 늘 어두운 색)</summary>
public static class MemoColors
{
    public const string Default = "#FFF4A8";

    public static readonly (string Name, string Hex)[] Palette =
    [
        ("노랑", "#FFF4A8"),
        ("살구", "#FFD9B3"),
        ("분홍", "#FFD3DC"),
        ("연두", "#D9F2B4"),
        ("민트", "#C4F0E2"),
        ("하늘", "#CDE7FF"),
        ("파랑", "#B9D3FF"),
        ("모래", "#EFE3CC"),
        ("회색", "#E4E6EA"),
        ("흰색", "#FFFFFF")
    ];

    /// <summary>정음 메모패드의 바탕색 18가지 (색 번호 순서)</summary>
    public static readonly string[] Jungum =
    [
        "#FFFF99", "#FFEB63", "#FF99FF", "#99FFFF", "#FF9999", "#FFB2B5", "#99FF99", "#C6FFB5", "#9999FF",
        "#E7B2FF", "#A5D7FF", "#DCDCDC", "#A89890", "#88C0B8", "#CFAFB7", "#9DB9C8", "#A2C8A8", "#ECD59D"
    ];

    /// <summary>받은 메모: 보낸 사람이 고른 색 그대로</summary>
    public static string FromJungum(int index) => index >= 0 && index < Jungum.Length ? Jungum[index] : Default;

    /// <summary>보낼 때: 가장 가까운 정음 색 번호</summary>
    public static int ToJungum(string hex)
    {
        var color = Parse(hex);
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < Jungum.Length; i++)
        {
            var other = Parse(Jungum[i]);
            double distance = Math.Pow(color.R - other.R, 2) * 0.3 + Math.Pow(color.G - other.G, 2) * 0.59 + Math.Pow(color.B - other.B, 2) * 0.11;
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    public static System.Windows.Media.Color Parse(string hex)
    {
        try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Default); }
    }

    /// <summary>바탕보다 조금 진한 색 (머리 · 테두리)</summary>
    public static System.Windows.Media.Color Shade(System.Windows.Media.Color color, double amount) =>
        System.Windows.Media.Color.FromRgb((byte)(color.R * (1 - amount)), (byte)(color.G * (1 - amount)), (byte)(color.B * (1 - amount)));
}
