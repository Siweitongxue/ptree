using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace PTree.Core;

/// <summary>YAML 原始映射：保留键顺序，重复键按后值覆盖（对齐 Python dict 语义）。</summary>
public sealed class RawMap : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Keys => _order;
    public int Count => _order.Count;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);

    public object? this[string key] => _values[key];

    public void Set(string key, object? value)
    {
        if (!_values.ContainsKey(key))
        {
            _order.Add(key);
        }
        _values[key] = value;
    }

    public bool Remove(string key)
    {
        if (!_values.Remove(key)) return false;
        _order.Remove(key);
        return true;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        foreach (var key in _order)
        {
            yield return new KeyValuePair<string, object?>(key, _values[key]);
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>把 YamlDotNet 表示模型转成 Python/PyYAML 风格的可校验值树。</summary>
public static class RawYaml
{
    private static readonly Regex DateRe = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex TimestampRe = new(
        @"^([0-9]{4})-([0-9]{1,2})-([0-9]{1,2})(?:[Tt]|[ \t]+)([0-9]{1,2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]*))?(?:[ \t]*(Z|([-+][0-9]{1,2})(?::([0-9]{2}))?))?$",
        RegexOptions.CultureInvariant);

    public static object? Parse(string text)
    {
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }
        var parser = new Parser(new StringReader(text));
        var anchors = new Dictionary<string, object?>(StringComparer.Ordinal);
        parser.MoveNext();
        if (parser.Current is not StreamStart)
        {
            return null;
        }
        parser.MoveNext();
        if (parser.Current is StreamEnd)
        {
            return null;
        }
        if (parser.Current is DocumentStart)
        {
            parser.MoveNext();
        }
        if (parser.Current is DocumentEnd or StreamEnd)
        {
            return null;
        }
        return ParseNode(parser, anchors);
    }

    private static object? ParseNode(IParser parser, Dictionary<string, object?> anchors)
    {
        var current = parser.Current;
        switch (current)
        {
            case Scalar scalar:
            {
                var value = ScalarValue(scalar.Value, scalar.Style);
                var anchor = scalar.Anchor.ToString();
                if (anchor.Length > 0)
                {
                    anchors[anchor] = value;
                }
                parser.MoveNext();
                return value;
            }
            case MappingStart mappingStart:
            {
                parser.MoveNext();
                var pairs = new List<(object? Key, object? Value)>();
                while (parser.Current is not MappingEnd)
                {
                    var key = ParseNode(parser, anchors);
                    var value = ParseNode(parser, anchors);
                    pairs.Add((key, value));
                }
                parser.MoveNext();
                var map = BuildMap(pairs);
                var anchor = mappingStart.Anchor.ToString();
                if (anchor.Length > 0)
                {
                    anchors[anchor] = map;
                }
                return map;
            }
            case SequenceStart sequenceStart:
            {
                parser.MoveNext();
                var list = new List<object?>();
                while (parser.Current is not SequenceEnd)
                {
                    list.Add(ParseNode(parser, anchors));
                }
                parser.MoveNext();
                var anchor = sequenceStart.Anchor.ToString();
                if (anchor.Length > 0)
                {
                    anchors[anchor] = list;
                }
                return list;
            }
            case AnchorAlias alias:
            {
                var value = anchors.TryGetValue(alias.Value.ToString(), out var anchored) ? anchored : null;
                parser.MoveNext();
                return value;
            }
            default:
                return null;
        }
    }

    private static RawMap BuildMap(List<(object? Key, object? Value)> pairs)
    {
        var explicitPairs = new List<(string Key, object? Value)>();
        var merged = new List<RawMap>();
        foreach (var pair in pairs)
        {
            var key = pair.Key as string ?? PyValue.Str(pair.Key);
            if (key == "<<")
            {
                if (pair.Value is RawMap mergeMap)
                {
                    merged.Add(mergeMap);
                }
                else if (pair.Value is List<object?> mergeList)
                {
                    var maps = mergeList.OfType<RawMap>().ToList();
                    maps.Reverse();
                    merged.AddRange(maps);
                }
                continue;
            }
            explicitPairs.Add((key, pair.Value));
        }
        var map = new RawMap();
        foreach (var mergeMap in merged)
        {
            foreach (var entry in mergeMap)
            {
                map.Set(entry.Key, entry.Value);
            }
        }
        foreach (var pair in explicitPairs)
        {
            map.Set(pair.Key, pair.Value);
        }
        return map;
    }

    public static RawMap ToMap(YamlMappingNode node)
    {
        var explicitPairs = new List<(string Key, object? Value)>();
        var merged = new List<RawMap>();
        foreach (var entry in node.Children)
        {
            var key = (entry.Key as YamlScalarNode)?.Value ?? string.Empty;
            if (key == "<<")
            {
                var mergeValue = ToValue(entry.Value);
                if (mergeValue is RawMap mergeMap)
                {
                    merged.Add(mergeMap);
                }
                else if (mergeValue is List<object?> mergeList)
                {
                    var maps = mergeList.OfType<RawMap>().ToList();
                    maps.Reverse();
                    merged.AddRange(maps);
                }
                continue;
            }
            explicitPairs.Add((key, ToValue(entry.Value)));
        }
        var map = new RawMap();
        foreach (var mergeMap in merged)
        {
            foreach (var pair in mergeMap)
            {
                map.Set(pair.Key, pair.Value);
            }
        }
        foreach (var pair in explicitPairs)
        {
            map.Set(pair.Key, pair.Value);
        }
        return map;
    }

    public static object? ToValue(YamlNode node)
    {
        return node switch
        {
            YamlMappingNode mapping => ToMap(mapping),
            YamlSequenceNode sequence => sequence.Children.Select(ToValue).ToList(),
            YamlScalarNode scalar => ScalarValue(scalar),
            _ => null,
        };
    }

    private static object? ScalarValue(YamlScalarNode node)
        => ScalarValue(node.Value, node.Style);

    private static object? ScalarValue(string? value, ScalarStyle style)
    {
        if (value is null)
        {
            return null;
        }
        if (style is not (ScalarStyle.Plain or ScalarStyle.Any))
        {
            return value;
        }
        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON":
                return true;
            case "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF":
                return false;
        }
        if (TryInteger(value, out var integer))
        {
            return integer;
        }
        if (TryFloat(value, out var number))
        {
            return number;
        }
        if (DateRe.IsMatch(value))
        {
            var parts = value.Split('-');
            return new PyDate(int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture));
        }
        var timestamp = TimestampRe.Match(value);
        if (timestamp.Success)
        {
            return new PyDateTime(NormalizeTimestamp(timestamp));
        }
        return value;
    }

    private static string NormalizeTimestamp(Match match)
    {
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var hour = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
        var second = int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[7].Value;
        var text = string.Format(CultureInfo.InvariantCulture, "{0:0000}-{1:00}-{2:00}T{3:00}:{4:00}:{5:00}",
            year, month, day, hour, minute, second);
        if (fraction.Length > 0)
        {
            var trimmed = fraction.TrimEnd('0');
            if (trimmed.Length > 0)
            {
                text += "." + trimmed.PadRight(6, '0')[..6];
            }
        }
        if (match.Groups[8].Success)
        {
            if (match.Groups[8].Value == "Z")
            {
                text += "+00:00";
            }
            else
            {
                var offsetHour = int.Parse(match.Groups[9].Value, CultureInfo.InvariantCulture);
                var sign = match.Groups[9].Value[0] == '-' ? '-' : '+';
                var offsetMinute = match.Groups[10].Success
                    ? int.Parse(match.Groups[10].Value, CultureInfo.InvariantCulture)
                    : 0;
                text += string.Format(CultureInfo.InvariantCulture, "{0}{1:00}:{2:00}", sign, offsetHour,
                    offsetMinute);
            }
        }
        return text;
    }

    private static bool TryInteger(string value, out long result)
    {
        result = 0;
        var text = value.Replace("_", string.Empty);
        var sign = 1;
        if (text.StartsWith('+'))
        {
            text = text[1..];
        }
        else if (text.StartsWith('-'))
        {
            sign = -1;
            text = text[1..];
        }
        if (text.Length == 0)
        {
            return false;
        }
        try
        {
            long parsed;
            if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
            {
                parsed = Convert.ToInt64(text[2..], 2);
            }
            else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
            {
                parsed = Convert.ToInt64(text[2..], 16);
            }
            else if (text.Length > 1 && text[0] == '0' && text.All(ch => ch is >= '0' and <= '7'))
            {
                parsed = Convert.ToInt64(text[1..], 8);
            }
            else if (text.Contains(':'))
            {
                var parts = text.Split(':');
                parsed = 0;
                foreach (var part in parts)
                {
                    if (part.Length == 0 || !part.All(char.IsDigit))
                    {
                        return false;
                    }
                    parsed = parsed * 60 + int.Parse(part, CultureInfo.InvariantCulture);
                }
            }
            else if (text.All(char.IsDigit) && long.TryParse(text, NumberStyles.Integer,
                         CultureInfo.InvariantCulture, out parsed))
            {
            }
            else
            {
                return false;
            }
            result = sign * parsed;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryFloat(string value, out double result)
    {
        result = 0;
        var text = value.Replace("_", string.Empty);
        var lower = text.ToLowerInvariant();
        if (lower is ".inf" or "+.inf")
        {
            result = double.PositiveInfinity;
            return true;
        }
        if (lower == "-.inf")
        {
            result = double.NegativeInfinity;
            return true;
        }
        if (lower is ".nan" or "+.nan" or "-.nan")
        {
            result = double.NaN;
            return true;
        }
        if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }
        return false;
    }
}
