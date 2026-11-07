using System.Globalization;
using System.Text.RegularExpressions;

namespace PTree.Core;

public sealed record TreeInstanceItem(string Id, string Name, IReadOnlyList<string> Fields, string SourceLine);

public sealed record TreeInstance(string Key, string Name, string Version, string RelPath,
    IReadOnlyList<TreeInstanceItem> Items);

/// <summary>读取 00-程序树/树实例 下的四份实例文档（纯文本，非机器树）。</summary>
public static class TreeInstances
{
    public static readonly string[] Keys = { "程序树", "策划树", "美术树", "UI树" };

    public static List<TreeInstance> Load(string root)
    {
        var result = new List<TreeInstance>();
        foreach (var key in Keys)
        {
            var rel = "00-程序树/树实例/" + key + ".txt";
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }
            var lines = PythonText.SplitLines(File.ReadAllText(path, new System.Text.UTF8Encoding(false, true)));
            var title = lines.Count > 0 ? lines[0] : key;
            var name = title.Split('（')[0].Trim();
            var version = "?";
            foreach (var line in lines.Take(5))
            {
                var match = Regex.Match(line, @"版本\s*([0-9]+)");
                if (match.Success)
                {
                    version = match.Groups[1].Value;
                    break;
                }
            }
            var items = new List<TreeInstanceItem>();
            foreach (var line in lines)
            {
                if (!line.Contains('｜'))
                {
                    continue;
                }
                var fields = line.Split('｜').Select(field => field.Trim()).ToArray();
                if (fields.Length < 2 || !Regex.IsMatch(fields[0], @"^[A-Za-z0-9][A-Za-z0-9.\-]*$"))
                {
                    continue;
                }
                items.Add(new TreeInstanceItem(fields[0], fields.Length > 1 ? fields[1] : string.Empty,
                    fields, line));
            }
            result.Add(new TreeInstance(key, name, version, rel, items));
        }
        return result;
    }

    public static TreeInstanceItem? FindNode(string root, string id)
    {
        foreach (var tree in Load(root))
        {
            var item = tree.Items.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (item is not null)
            {
                return item;
            }
        }
        return null;
    }

    public static Dictionary<string, object?> Summary(string root)
        => Load(root).ToDictionary(tree => tree.Key, tree => (object?)new
        {
            name = tree.Name,
            version = tree.Version,
            path = tree.RelPath,
            item_count = tree.Items.Count,
            ids = tree.Items.Take(20).Select(item => item.Id).ToList(),
        }, StringComparer.Ordinal);
}
