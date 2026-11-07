using System.Globalization;
using System.Numerics;
using System.Text;

namespace PTree.Core;

/// <summary>
/// Python 文本语义复刻：str.lower 的特殊大小写、unicodedata 类别过滤、按码点二元组、
/// 以及 Python "%.2f" 的精确舍入。仅实现 R08 所需部分，不做 NFKC。
/// </summary>
public static class PythonText
{
    private static readonly Rune TurkishCapitalI = new(0x0130);
    private static readonly Rune GreekCapitalSigma = new(0x03A3);
    private static readonly Rune GreekFinalSigma = new(0x03C2);
    private static readonly Rune CombiningDotAbove = new(0x0307);

    /// <summary>Python str.lower()：处理 İ → i+ U+0307、词尾 Σ → ς，其余逐码点小写。</summary>
    public static string Lower(string text)
    {
        var runes = text.EnumerateRunes().ToArray();
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < runes.Length; index++)
        {
            var rune = runes[index];
            if (rune == TurkishCapitalI)
            {
                builder.Append('i').Append(CombiningDotAbove.ToString());
                continue;
            }
            if (rune == GreekCapitalSigma && IsFinalSigma(runes, index))
            {
                builder.Append(GreekFinalSigma.ToString());
                continue;
            }
            builder.Append(Rune.ToLowerInvariant(rune).ToString());
        }
        return builder.ToString();
    }

    private static bool IsFinalSigma(Rune[] runes, int index)
    {
        var before = index - 1;
        while (before >= 0 && IsCaseIgnorable(runes[before]))
        {
            before--;
        }
        var hasCasedBefore = before >= 0 && IsCased(runes[before]);
        var after = index + 1;
        while (after < runes.Length && IsCaseIgnorable(runes[after]))
        {
            after++;
        }
        var hasCasedAfter = after < runes.Length && IsCased(runes[after]);
        return hasCasedBefore && !hasCasedAfter;
    }

    private static bool IsCased(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter;
    }

    private static bool IsCaseIgnorable(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or
            UnicodeCategory.Format or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol)
        {
            return true;
        }
        // Unicode Case_Ignorable 还包含 Word_Break 的 MidLetter / MidNumLet / Single_Quote 常见标点
        return rune.Value is 0x0027 or 0x002E or 0x003A or 0x00AD or 0x2018 or 0x2019 or 0x2024
            or 0xFE52 or 0xFF07 or 0xFF0E or 0xFF1A;
    }

    /// <summary>Python _normalize_text：lower 后去掉空白与 P/S/Z 类别字符。</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in Lower(text).EnumerateRunes())
        {
            if (IsPythonSpace(rune) || IsPunctuationSymbolOrSeparator(rune))
            {
                continue;
            }
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static bool IsPythonSpace(Rune rune)
    {
        // Python str.isspace() 额外把 U+001C..U+001F 视为空白
        return Rune.IsWhiteSpace(rune) || rune.Value is >= 0x001C and <= 0x001F;
    }

    private static bool IsPunctuationSymbolOrSeparator(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or
            UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or
            UnicodeCategory.FinalQuotePunctuation or
            UnicodeCategory.OtherPunctuation or
            UnicodeCategory.MathSymbol or
            UnicodeCategory.CurrencySymbol or
            UnicodeCategory.ModifierSymbol or
            UnicodeCategory.OtherSymbol or
            UnicodeCategory.SpaceSeparator or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator;
    }

    /// <summary>Python len(re.sub(r"\s+", "", text))。</summary>
    public static int LengthWithoutWhitespace(string text)
        => text.EnumerateRunes().Count(rune => !Rune.IsWhiteSpace(rune));

    /// <summary>Python str.splitlines：支持 \n、\r、\r\n、\v、\f、\x1c-\x1e、\x85、\u2028、\u2029；末尾换行不产生额外空行。</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var builder = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            var isBreak = false;
            if (ch == '\r')
            {
                isBreak = true;
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
            }
            else if (ch is '\n' or '\v' or '\f' or '\u001c' or '\u001d' or '\u001e' or '\u0085' or '\u2028' or '\u2029')
            {
                isBreak = true;
            }
            if (isBreak)
            {
                lines.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(ch);
            }
        }
        if (builder.Length > 0)
        {
            lines.Add(builder.ToString());
        }
        return lines;
    }

    /// <summary>Python _bigrams：按码点切，长度 &lt;2 时返回单元素或空集合。</summary>
    public static HashSet<string> Bigrams(string text)
    {
        var runes = text.EnumerateRunes().ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (runes.Length == 0)
        {
            return result;
        }
        if (runes.Length == 1)
        {
            result.Add(text);
            return result;
        }
        for (var index = 0; index < runes.Length - 1; index++)
        {
            result.Add(string.Concat(runes[index].ToString(), runes[index + 1].ToString()));
        }
        return result;
    }

    public static double Jaccard(string left, string right)
    {
        var a = Bigrams(left);
        var b = Bigrams(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0.0;
        }
        var intersection = a.Intersect(b, StringComparer.Ordinal).Count();
        var union = a.Count + b.Count - intersection;
        return (double)intersection / union;
    }

    /// <summary>按 Python "%.2f" 语义格式化：对 double 的精确二进制值做四舍六入五成双（half-even）。</summary>
    public static string Format2(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }
        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }
        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }
        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var magnitude = bits & long.MaxValue;
        var exponentBits = (int)((magnitude >> 52) & 0x7FF);
        var mantissa = magnitude & 0xFFFFFFFFFFFFFL;
        int exponent;
        if (exponentBits == 0)
        {
            exponent = -1074;
        }
        else
        {
            mantissa |= 1L << 52;
            exponent = exponentBits - 1075;
        }
        var numerator = new BigInteger(mantissa) * 100;
        var denominator = BigInteger.One;
        if (exponent >= 0)
        {
            numerator <<= exponent;
        }
        else
        {
            denominator <<= -exponent;
        }
        var cents = BigInteger.DivRem(numerator, denominator, out var remainder);
        var doubled = remainder * 2;
        if (doubled > denominator || (doubled == denominator && !cents.IsEven))
        {
            cents += 1;
        }
        var sign = negative && !cents.IsZero ? "-" : string.Empty;
        var whole = BigInteger.DivRem(cents, 100, out var fraction);
        return sign + whole.ToString(CultureInfo.InvariantCulture) + "." +
               fraction.ToString("00", CultureInfo.InvariantCulture);
    }
}
