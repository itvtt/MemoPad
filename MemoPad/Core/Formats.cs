using System.Globalization;

namespace MemoPad.Core;

/// <summary>날짜 · 시간 넣기 (Ctrl+D · Ctrl+T) 형식: 정음 메모패드와 같은 목록</summary>
public static class Formats
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    public static readonly string[] Dates =
    [
        "yyyy-MM-dd", "yy-MM-dd", "yy-M-d", "yyyy-M-d", "M/d/yy", "M/d/yyyy", "MM/dd/yy", "MM/dd/yyyy",
        "yyyy년 M월 d일 dddd", "yyyy년 M월 d일", "yy년 M월 d일 dddd", "yy년 M월 d일", "yyyy년 MM월 dd일 dddd", "yyyy년 MM월 dd일"
    ];

    public static readonly string[] Times =
    [
        "tt h:mm:ss", "tt hh:mm:ss", "H:mm:ss", "HH:mm:ss", "tt h시 mm분", "tt h시 mm분 ss초", "H시 mm분", "H시 mm분 ss초", "HH:mm"
    ];

    public static string Date(DateTime value, int format) => value.ToString(Dates[Math.Clamp(format, 0, Dates.Length - 1)], Korean);
    public static string Time(DateTime value, int format) => value.ToString(Times[Math.Clamp(format, 0, Times.Length - 1)], Korean);

    /// <summary>목록 · 상태줄의 짧은 시각: 오늘이면 시:분, 올해면 월/일 시:분</summary>
    public static string Short(DateTime value)
    {
        var now = DateTime.Now;
        if (value.Date == now.Date) return value.ToString("HH:mm", Korean);
        if (value.Year == now.Year) return value.ToString("M/d HH:mm", Korean);
        return value.ToString("yyyy-MM-dd", Korean);
    }

    public static string Full(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", Korean);

    /// <summary>메모 머리: 11월 20일 (금) 15:12, 다른 해면 2025년 11월 20일 (목) 15:12</summary>
    public static string Title(DateTime value) =>
        value.ToString(value.Year == DateTime.Now.Year ? "M월 d일 (ddd) HH:mm" : "yyyy년 M월 d일 (ddd) HH:mm", Korean);
}
