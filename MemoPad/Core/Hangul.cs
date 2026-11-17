using System.Text;

namespace MemoPad.Core;

/// <summary>
/// 한/영 오타 변환 (두벌식 자판): 영문 상태로 친 "dkssud" ↔ "안녕", 한글 상태로 친 "ㅗ디ㅣㅐ" ↔ "hello".
/// 초성 찾기("ㅎㄱㄷ" → 홍길동)도 여기서
/// </summary>
public static class Hangul
{
    private const string Cho = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
    private const string Jung = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
    private const string Jong = " ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

    // 자판 글쇠 → 자모 (소문자, Shift 는 된소리 · ㅒ · ㅖ)
    private const string Keys = "qwertyuiopasdfghjklzxcvbnm";
    private const string KeyJamo = "ㅂㅈㄷㄱㅅㅛㅕㅑㅐㅔㅁㄴㅇㄹㅎㅗㅓㅏㅣㅋㅌㅊㅍㅠㅜㅡ";
    private static readonly Dictionary<char, char> ShiftJamo = new() { ['Q'] = 'ㅃ', ['W'] = 'ㅉ', ['E'] = 'ㄸ', ['R'] = 'ㄲ', ['T'] = 'ㅆ', ['O'] = 'ㅒ', ['P'] = 'ㅖ' };

    private static readonly Dictionary<(char, char), char> VowelPairs = new()
    {
        [('ㅗ', 'ㅏ')] = 'ㅘ', [('ㅗ', 'ㅐ')] = 'ㅙ', [('ㅗ', 'ㅣ')] = 'ㅚ',
        [('ㅜ', 'ㅓ')] = 'ㅝ', [('ㅜ', 'ㅔ')] = 'ㅞ', [('ㅜ', 'ㅣ')] = 'ㅟ', [('ㅡ', 'ㅣ')] = 'ㅢ'
    };

    private static readonly Dictionary<(char, char), char> FinalPairs = new()
    {
        [('ㄱ', 'ㅅ')] = 'ㄳ', [('ㄴ', 'ㅈ')] = 'ㄵ', [('ㄴ', 'ㅎ')] = 'ㄶ', [('ㄹ', 'ㄱ')] = 'ㄺ', [('ㄹ', 'ㅁ')] = 'ㄻ',
        [('ㄹ', 'ㅂ')] = 'ㄼ', [('ㄹ', 'ㅅ')] = 'ㄽ', [('ㄹ', 'ㅌ')] = 'ㄾ', [('ㄹ', 'ㅍ')] = 'ㄿ', [('ㄹ', 'ㅎ')] = 'ㅀ', [('ㅂ', 'ㅅ')] = 'ㅄ'
    };

    public static bool IsSyllable(char c) => c >= '가' && c <= '힣';
    public static bool IsJamo(char c) => c >= 'ㄱ' && c <= 'ㅣ';
    private static bool IsVowel(char c) => Jung.IndexOf(c) >= 0;

    /// <summary>한글이 있으면 영문 자판 글자로, 없으면 영문을 한글로</summary>
    public static string ConvertTypo(string text) =>
        text.Any(c => IsSyllable(c) || IsJamo(c)) ? ToKeys(text) : ToHangul(text);

    /// <summary>"dkssud" → "안녕" (영문이 아닌 글자는 그대로)</summary>
    public static string ToHangul(string keys)
    {
        var jamo = new StringBuilder();
        foreach (char c in keys)
        {
            if (ShiftJamo.TryGetValue(c, out char shifted)) { jamo.Append(shifted); continue; }
            int index = Keys.IndexOf(char.ToLowerInvariant(c));
            jamo.Append(index >= 0 ? KeyJamo[index] : c);
        }
        return Compose(jamo.ToString());
    }

    /// <summary>"안녕" → "dkssud"</summary>
    public static string ToKeys(string text)
    {
        var result = new StringBuilder();
        foreach (char c in text)
        {
            if (IsSyllable(c))
            {
                int code = c - '가';
                AppendJamoKeys(result, Cho[code / 588]);
                AppendJamoKeys(result, Jung[code % 588 / 28]);
                if (code % 28 > 0) AppendJamoKeys(result, Jong[code % 28]);
            }
            else if (IsJamo(c)) AppendJamoKeys(result, c);
            else result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>"홍길동" → "ㅎㄱㄷ"</summary>
    public static string Initials(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char c in text) result.Append(IsSyllable(c) ? Cho[(c - '가') / 588] : c);
        return result.ToString();
    }

    private static void AppendJamoKeys(StringBuilder result, char jamo)
    {
        foreach (var ((first, second), pair) in VowelPairs)
            if (pair == jamo) { AppendJamoKeys(result, first); AppendJamoKeys(result, second); return; }
        foreach (var ((first, second), pair) in FinalPairs)
            if (pair == jamo) { AppendJamoKeys(result, first); AppendJamoKeys(result, second); return; }
        foreach (var (key, value) in ShiftJamo)
            if (value == jamo) { result.Append(key); return; }
        int index = KeyJamo.IndexOf(jamo);
        result.Append(index >= 0 ? Keys[index] : jamo);
    }

    /// <summary>자모 나열을 두벌식 규칙으로 글자로 모음</summary>
    public static string Compose(string jamo)
    {
        var output = new StringBuilder();
        char cho = '\0', jung = '\0', jong = '\0';

        void Flush()
        {
            if (cho != '\0' && jung != '\0')
                output.Append((char)('가' + Cho.IndexOf(cho) * 588 + Jung.IndexOf(jung) * 28 + (jong == '\0' ? 0 : Jong.IndexOf(jong))));
            else
            {
                if (cho != '\0') output.Append(cho);
                if (jung != '\0') output.Append(jung);
                if (jong != '\0') output.Append(jong);
            }
            cho = jung = jong = '\0';
        }

        foreach (char c in jamo)
        {
            if (!IsJamo(c)) { Flush(); output.Append(c); continue; }
            if (!IsVowel(c))
            {
                if (cho != '\0' && jung != '\0')
                {
                    if (jong == '\0' && Jong.IndexOf(c) > 0) { jong = c; continue; }
                    if (jong != '\0' && FinalPairs.TryGetValue((jong, c), out char pair)) { jong = pair; continue; }
                }
                Flush();
                cho = c;
                continue;
            }
            // 모음
            if (cho != '\0' && jung == '\0') { jung = c; continue; }
            if (jung != '\0' && jong == '\0' && VowelPairs.TryGetValue((jung, c), out char vowel)) { jung = vowel; continue; }
            if (cho != '\0' && jung != '\0' && jong != '\0')
            {
                // 받침이 다음 글자의 초성으로: 겹받침이면 뒤쪽만 옮김
                char moved = jong;
                foreach (var ((first, second), pair) in FinalPairs)
                    if (pair == jong) { jong = first; moved = second; goto split; }
                jong = '\0';
            split:
                if (Cho.IndexOf(moved) < 0) { Flush(); output.Append(moved); jung = c; continue; }
                Flush();
                cho = moved;
                jung = c;
                continue;
            }
            Flush();
            jung = c;
        }
        Flush();
        return output.ToString();
    }
}
