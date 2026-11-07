using System.Text;

namespace PTree.Core;

/// <summary>受限 Python import 扫描器：跳过注释/字符串/f-string，处理括号与反斜杠续行。</summary>
public static class PythonImports
{
    public static IEnumerable<string> Scan(string text)
    {
        var result = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var ch = text[index];
            if (ch == '#')
            {
                index = SkipToLineEnd(text, index);
                continue;
            }
            if (ch is '\'' or '"')
            {
                index = SkipString(text, index);
                continue;
            }
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_')
            {
                var start = index;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
                var word = text[start..index];
                if (word == "import")
                {
                    foreach (var module in ReadImportList(text, ref index)) result.Add(module);
                }
                else if (word == "from")
                {
                    var module = ReadFromModule(text, ref index);
                    if (module is not null && !module.StartsWith('.')) result.Add(module);
                }
                continue;
            }
            index++;
        }
        return result.Distinct(StringComparer.Ordinal);
    }

    private static int SkipToLineEnd(string text, int index)
    {
        while (index < text.Length && text[index] is not '\n' and not '\r') index++;
        return index;
    }

    private static int SkipString(string text, int index)
    {
        var quote = text[index];
        var triple = index + 2 < text.Length && text[index + 1] == quote && text[index + 2] == quote;
        index += triple ? 3 : 1;
        while (index < text.Length)
        {
            if (text[index] == '\\') { index += 2; continue; }
            if (text[index] == quote && (!triple || (index + 2 < text.Length && text[index + 1] == quote && text[index + 2] == quote)))
            {
                index += triple ? 3 : 1;
                return index;
            }
            if (!triple && (text[index] == '\n' || text[index] == '\r')) return index;
            index++;
        }
        return index;
    }

    private static List<string> ReadImportList(string text, ref int index)
    {
        var modules = new List<string>();
        var builder = new StringBuilder();
        var depth = 0;
        while (index < text.Length)
        {
            var ch = text[index];
            if (ch == '\\' && index + 1 < text.Length && text[index + 1] is '\n' or '\r')
            {
                index += text[index + 1] == '\r' && index + 2 < text.Length && text[index + 2] == '\n' ? 3 : 2;
                continue;
            }
            if (ch == '#') { index = SkipToLineEnd(text, index); continue; }
            if (ch == '(') { depth++; index++; continue; }
            if (ch == ')') { depth--; index++; continue; }
            if (depth == 0 && (ch == '\n' || ch == '\r' || ch == ';')) break;
            if (depth == 0 && ch == ',')
            {
                AddModule(builder, modules);
                index++;
                continue;
            }
            if (depth == 0 && char.IsWhiteSpace(ch)) { index++; continue; }
            builder.Append(ch);
            index++;
        }
        AddModule(builder, modules);
        return modules;
    }

    private static void AddModule(StringBuilder builder, List<string> modules)
    {
        var text = builder.ToString().Trim();
        builder.Clear();
        if (text.Length == 0) return;
        var asIndex = text.IndexOf(" as ", StringComparison.Ordinal);
        if (asIndex >= 0) text = text[..asIndex].Trim();
        if (text.Length > 0) modules.Add(text);
    }

    private static string? ReadFromModule(string text, ref int index)
    {
        var start = index;
        var depth = 0;
        while (index < text.Length)
        {
            var ch = text[index];
            if (ch == '\\' && index + 1 < text.Length && text[index + 1] is '\n' or '\r')
            {
                index += text[index + 1] == '\r' && index + 2 < text.Length && text[index + 2] == '\n' ? 3 : 2;
                continue;
            }
            if (ch is '\'' or '"') { index = SkipString(text, index); continue; }
            if (ch == '(') { depth++; index++; continue; }
            if (ch == ')') { depth--; index++; continue; }
            if (ch == '#' || (depth == 0 && (ch == '\n' || ch == '\r' || ch == ';'))) break;
            if (depth == 0 && char.IsWhiteSpace(ch) && text[(start)..index].Trim().Length > 0) break;
            index++;
        }
        var module = text[start..index].Trim();
        return module.Length == 0 ? null : module;
    }
}
