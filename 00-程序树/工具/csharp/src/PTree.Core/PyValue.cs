using System.Globalization;
using System.Text;

namespace PTree.Core;

/// <summary>Python 语义辅助：对齐 repr/str/type 名与 int/bool 判定，用于逐字复刻 Python 版报错。</summary>
public static class PyValue
{
    public static string TypeName(object? value) => value switch
    {
        null => "NoneType",
        RawMap => "dict",
        List<object?> => "list",
        string => "str",
        bool => "bool",
        long => "int",
        double => "float",
        PyDate => "date",
        PyDateTime => "datetime",
        _ => value.GetType().Name,
    };

    public static bool IsString(object? value) => value is string;

    public static bool NonEmptyString(object? value) => value is string text && !string.IsNullOrWhiteSpace(text);

    /// <summary>Python 真值：None/False/0/空字符串/空容器为假。</summary>
    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => text.Length > 0,
        long number => number != 0,
        double number => number != 0,
        List<object?> list => list.Count > 0,
        RawMap map => map.Count > 0,
        _ => true,
    };

    /// <summary>Python len(str)：按 Unicode 码点计数，而不是 UTF-16 单元。</summary>
    public static int Length(string text) => text.EnumerateRunes().Count();

    /// <summary>Python 切片 text[:count]。</summary>
    public static string Head(string text, int count)
        => string.Concat(text.EnumerateRunes().Take(count).Select(rune => rune.ToString()));

    public static bool IsList(object? value) => value is List<object?>;

    public static bool IsMap(object? value) => value is RawMap;

    public static bool IsBool(object? value) => value is bool;

    /// <summary>Python isinstance(value, int)：bool 也算 int。</summary>
    public static bool IsIntLike(object? value) => value is long or bool;

    public static bool IsPlainInt(object? value) => value is long;

    public static long? AsLong(object? value) => value switch
    {
        long number => number,
        bool flag => flag ? 1L : 0L,
        _ => null,
    };

    public static bool EqualsPy(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null)
        {
            return false;
        }
        if (left is string leftText && right is string rightText)
        {
            return string.Equals(leftText, rightText, StringComparison.Ordinal);
        }
        if (left is List<object?> leftList && right is List<object?> rightList)
        {
            return leftList.Count == rightList.Count &&
                   leftList.Zip(rightList, EqualsPy).All(equal => equal);
        }
        if (left is RawMap leftMap && right is RawMap rightMap)
        {
            if (leftMap.Count != rightMap.Count)
            {
                return false;
            }
            foreach (var entry in leftMap)
            {
                if (!rightMap.TryGetValue(entry.Key, out var rightValue) || !EqualsPy(entry.Value, rightValue))
                {
                    return false;
                }
            }
            return true;
        }
        if (left is PyDate leftDate && right is PyDate rightDate)
        {
            return leftDate == rightDate;
        }
        if (left is PyDateTime leftDateTime && right is PyDateTime rightDateTime)
        {
            return string.Equals(leftDateTime.Iso, rightDateTime.Iso, StringComparison.Ordinal);
        }
        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is bool leftBool && right is bool rightBool)
            {
                return leftBool == rightBool;
            }
            return Convert.ToDouble(left, CultureInfo.InvariantCulture) ==
                   Convert.ToDouble(right, CultureInfo.InvariantCulture);
        }
        return left.Equals(right);
    }

    private static bool IsNumeric(object? value) => value is bool or long or double;

    public static string Str(object? value) => value switch
    {
        null => "None",
        string text => text,
        bool flag => flag ? "True" : "False",
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => FormatDouble(number),
        List<object?> list => "[" + string.Join(", ", list.Select(Repr)) + "]",
        RawMap map => ReprMap(map),
        PyDate date => date.Iso,
        PyDateTime dateTime => dateTime.Iso.Replace('T', ' '),
        _ => value.ToString() ?? "None",
    };

    public static string Repr(object? value) => value switch
    {
        null => "None",
        string text => ReprString(text),
        bool flag => flag ? "True" : "False",
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => FormatDouble(number),
        List<object?> list => "[" + string.Join(", ", list.Select(Repr)) + "]",
        RawMap map => ReprMap(map),
        PyDate date => "datetime.date(" + date.Year + ", " + date.Month + ", " + date.Day + ")",
        PyDateTime dateTime => ReprDateTime(dateTime),
        _ => ReprString(value.ToString() ?? "None"),
    };

    private static string ReprDateTime(PyDateTime dateTime)
    {
        var iso = dateTime.Iso;
        var datePart = iso[..10];
        var timePart = iso.Length > 11 ? iso[11..] : string.Empty;
        var year = int.Parse(datePart[..4], System.Globalization.CultureInfo.InvariantCulture);
        var month = int.Parse(datePart[5..7], System.Globalization.CultureInfo.InvariantCulture);
        var day = int.Parse(datePart[8..10], System.Globalization.CultureInfo.InvariantCulture);
        var fields = new List<string> { year.ToString(), month.ToString(), day.ToString() };
        if (timePart.Length >= 8)
        {
            fields.Add(int.Parse(timePart[..2], System.Globalization.CultureInfo.InvariantCulture).ToString());
            fields.Add(int.Parse(timePart[3..5], System.Globalization.CultureInfo.InvariantCulture).ToString());
            fields.Add(int.Parse(timePart[6..8], System.Globalization.CultureInfo.InvariantCulture).ToString());
        }
        return "datetime.datetime(" + string.Join(", ", fields) + ")";
    }

    private static string ReprMap(RawMap map)
        => "{" + string.Join(", ", map.Select(entry => ReprString(entry.Key) + ": " + Repr(entry.Value))) + "}";

    private static string ReprString(string text)
    {
        var builder = new StringBuilder("'");
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\'':
                    builder.Append("\\'");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (ch < 0x20 || ch == 0x7f)
                    {
                        builder.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }
                    break;
            }
        }
        builder.Append('\'');
        return builder.ToString();
    }

    private static string FormatDouble(double number)
    {
        if (double.IsNaN(number))
        {
            return "nan";
        }
        if (double.IsPositiveInfinity(number))
        {
            return "inf";
        }
        if (double.IsNegativeInfinity(number))
        {
            return "-inf";
        }
        var text = number.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('e') && !text.Contains('E'))
        {
            text += ".0";
        }
        return text;
    }
}
