using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.Imaging;

namespace MemoPad.Core;

/// <summary>RTF 글자 다루기 (메모 본문 = RTF: 정음 메모패드와 주고받는 형식 그대로)</summary>
public static class RtfText
{
    /// <summary>일반 글자 → RTF (한글은 \uN 으로)</summary>
    public static string FromPlain(string text, string font = "Malgun Gothic", int halfPoints = 20)
    {
        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\ansicpg949\deff0{\fonttbl{\f0\fnil\fcharset129 ").Append(Escape(font)).Append(@";}}");
        rtf.Append(@"\viewkind4\uc1\pard\f0\fs").Append(halfPoints).Append(' ');
        rtf.Append(Escape(text.Replace("\r\n", "\n"), paragraphs: true));
        rtf.Append(@"\par}");
        return rtf.ToString();
    }

    private static string Escape(string text, bool paragraphs = false)
    {
        var result = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            if (c == '\\' || c == '{' || c == '}') result.Append('\\').Append(c);
            else if (c == '\n' && paragraphs) result.Append(@"\par ");
            else if (c == '\t') result.Append(@"\tab ");
            else if (c < 0x80) result.Append(c);
            else result.Append(@"\u").Append((short)c).Append('?');
        }
        return result.ToString();
    }

    // ── FlowDocument 로 (UI 스레드에서) ──

    public static FlowDocument Document(string rtf)
    {
        var document = new FlowDocument();
        Load(document, rtf);
        return document;
    }

    public static void Load(FlowDocument document, string rtf)
    {
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        if (string.IsNullOrEmpty(rtf)) { range.Text = ""; return; }
        try
        {
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(EnsureAscii(rtf)));
            range.Load(stream, DataFormats.Rtf);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
        {
            range.Text = rtf.StartsWith("{\\rtf", StringComparison.Ordinal) ? "" : rtf;
        }
    }

    public static string Save(FlowDocument document)
    {
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.Rtf);
        return JuEncoding(stream.ToArray());
    }

    public static string Plain(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd).Text.Replace("\r\n", "\n").TrimEnd('\n');

    public static string Plain(string rtf) => string.IsNullOrEmpty(rtf) ? "" : Plain(Document(rtf));

    /// <summary>문단마다 앞에 "> " (받은 메모에 '>' 추가)</summary>
    public static string Quote(string rtf)
    {
        var document = Document(rtf);
        foreach (var paragraph in Paragraphs(document.Blocks))
        {
            if (paragraph.Inlines.FirstInline is { } first) paragraph.Inlines.InsertBefore(first, new Run("> "));
            else paragraph.Inlines.Add(new Run("> "));
        }
        return Save(document);
    }

    /// <summary>맨 앞에 문단 하나 더하기 ("[회람 결과입니다.]" 등)</summary>
    public static string Prepend(string rtf, params string[] lines)
    {
        var document = Document(rtf);
        var first = document.Blocks.FirstBlock;
        foreach (string line in lines)
        {
            var paragraph = new Paragraph(new Run(line)) { Margin = new Thickness(0) };
            if (first is null) document.Blocks.Add(paragraph);
            else document.Blocks.InsertBefore(first, paragraph);
        }
        return Save(document);
    }

    /// <summary>맨 뒤에 문단들 더하기 (회람 의견)</summary>
    public static string Append(string rtf, params string[] lines)
    {
        var document = Document(rtf);
        foreach (string line in lines) document.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0) });
        return Save(document);
    }

    /// <summary>그림 한 장짜리 RTF (그림 메모를 정음 메모패드로 보낼 때)</summary>
    public static string FromImage(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var bytes = stream.ToArray();
        int twipsW = (int)(image.PixelWidth * 15), twipsH = (int)(image.PixelHeight * 15);
        var rtf = new StringBuilder(bytes.Length * 2 + 200);
        rtf.Append(@"{\rtf1\ansi\ansicpg949\deff0{\fonttbl{\f0\fnil\fcharset129 Malgun Gothic;}}\viewkind4\uc1\pard ");
        rtf.Append(@"{\pict\pngblip\picw").Append(image.PixelWidth).Append(@"\pich").Append(image.PixelHeight)
           .Append(@"\picwgoal").Append(twipsW).Append(@"\pichgoal").Append(twipsH).Append('\n');
        for (int i = 0; i < bytes.Length; i++)
        {
            rtf.Append(bytes[i].ToString("x2"));
            if (i % 64 == 63) rtf.Append('\n');
        }
        rtf.Append(@"}\par}");
        return rtf.ToString();
    }

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks.ToList())
        {
            switch (block)
            {
                case Paragraph paragraph: yield return paragraph; break;
                case List list:
                    foreach (var item in list.ListItems)
                        foreach (var inner in Paragraphs(item.Blocks)) yield return inner;
                    break;
                case Section section:
                    foreach (var inner in Paragraphs(section.Blocks)) yield return inner;
                    break;
            }
        }
    }

    /// <summary>RTF 는 7비트 글자: 혹시 섞인 한글 바이트는 \uN 으로</summary>
    private static string EnsureAscii(string rtf)
    {
        if (rtf.All(c => c < 0x80)) return rtf;
        var result = new StringBuilder(rtf.Length + 64);
        foreach (char c in rtf)
            if (c < 0x80) result.Append(c);
            else result.Append(@"\u").Append((short)c).Append('?');
        return result.ToString();
    }

    private static string JuEncoding(byte[] bytes) => Encoding.ASCII.GetString(bytes).TrimEnd('\0');
}
