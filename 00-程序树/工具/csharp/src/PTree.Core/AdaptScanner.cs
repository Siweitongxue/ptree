using System.Text.RegularExpressions;

namespace PTree.Core;

/// <summary>固定目录自动适配：按清单 adapt 段扫描文件，只读报告已归属/未归属，不写清单。</summary>
public static class AdaptScanner
{
    public static Dictionary<string, object?> Scan(PTreeRepository repository)
    {
        var root = repository.Root;
        var configs = ReadConfigs(repository.ManifestRaw);
        var assigned = new List<Dictionary<string, object?>>();
        var unassigned = new List<Dictionary<string, object?>>();
        var ignored = new List<Dictionary<string, object?>>();
        var errors = new List<string>();
        var directories = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var config in configs)
        {
            var dir = config.Dir.Replace('\\', '/').TrimEnd('/');
            var absolute = Path.GetFullPath(Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar)));
            if (!Directory.Exists(absolute))
            {
                errors.Add("adapt 目录不存在：" + dir);
                directories.Add(new Dictionary<string, object?> { ["dir"] = dir, ["exists"] = false, ["matched"] = 0, ["assigned"] = 0, ["unassigned"] = 0, ["ignored"] = 0 });
                continue;
            }
            var matched = 0;
            var assignedHere = 0;
            var unassignedHere = 0;
            var ignoredHere = 0;
            foreach (var path in EnumerateFilesNoLinks(absolute))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!seen.Add(relative)) continue;
                if (!config.Patterns.Any(pattern => Matches(pattern, relative)))
                {
                    ignoredHere++;
                    ignored.Add(new Dictionary<string, object?> { ["path"] = relative, ["dir"] = dir, ["reason"] = "不匹配 adapt patterns" });
                    continue;
                }
                matched++;
                var owner = FindOwner(repository, relative);
                if (owner is not null)
                {
                    assignedHere++;
                    assigned.Add(new Dictionary<string, object?> { ["path"] = relative, ["node"] = owner, ["dir"] = dir });
                }
                else
                {
                    unassignedHere++;
                    var suggestion = Suggest(repository, relative);
                    unassigned.Add(new Dictionary<string, object?>
                    {
                        ["path"] = relative,
                        ["dir"] = dir,
                        ["suggested_node"] = suggestion?.Node,
                        ["suggested_prefix"] = suggestion?.Prefix,
                    });
                }
            }
            directories.Add(new Dictionary<string, object?>
            {
                ["dir"] = dir,
                ["exists"] = true,
                ["patterns"] = config.Patterns,
                ["matched"] = matched,
                ["assigned"] = assignedHere,
                ["unassigned"] = unassignedHere,
                ["ignored"] = ignoredHere,
            });
        }

        var covered = assigned.Count + unassigned.Count + ignored.Count;
        return new Dictionary<string, object?>
        {
            ["ok"] = errors.Count == 0,
            ["root"] = root,
            ["configured_directories"] = configs.Count,
            ["directories"] = directories,
            ["total_files"] = covered,
            ["assigned"] = assigned,
            ["unassigned"] = unassigned,
            ["ignored"] = ignored,
            ["unaccounted"] = new List<object?>(),
            ["counts"] = new Dictionary<string, int>
            {
                ["assigned"] = assigned.Count,
                ["unassigned"] = unassigned.Count,
                ["ignored"] = ignored.Count,
                ["unaccounted"] = 0,
                ["total"] = covered,
            },
            ["errors"] = errors,
            ["note"] = "只读报告：不写清单；unassigned 按最长路径前缀给出归属建议。",
        };
    }

    private sealed record Config(string Dir, List<string> Patterns);

    private static List<Config> ReadConfigs(RawMap manifest)
    {
        var result = new List<Config>();
        if (!manifest.TryGetValue("adapt", out var raw) || raw is not List<object?> items) return result;
        foreach (var item in items)
        {
            if (item is not RawMap map) continue;
            var dir = map.TryGetValue("dir", out var dirValue) ? dirValue as string : null;
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var patterns = new List<string>();
            if (map.TryGetValue("patterns", out var patternValue) && patternValue is List<object?> patternList)
            {
                patterns.AddRange(patternList.OfType<string>().Where(pattern => !string.IsNullOrWhiteSpace(pattern)));
            }
            result.Add(new Config(dir, patterns));
        }
        return result;
    }

    private static IEnumerable<string> EnumerateFilesNoLinks(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(current).ToList();
                dirs = Directory.EnumerateDirectories(current).ToList();
            }
            catch (Exception) { continue; }
            foreach (var file in files) yield return file;
            foreach (var dir in dirs)
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                queue.Enqueue(dir);
            }
        }
    }

    private static string? FindOwner(PTreeRepository repository, string relative)
    {
        foreach (var node in repository.Nodes)
        {
            if (node.Id is null) continue;
            foreach (var reference in node.Files)
            {
                var normalized = reference.Replace('\\', '/').TrimEnd('/');
                if (string.Equals(normalized, relative, StringComparison.Ordinal)) return node.Id;
                if (normalized.Length > 0 && relative.StartsWith(normalized + "/", StringComparison.Ordinal)) return node.Id;
            }
        }
        return null;
    }

    private static (string Node, string Prefix)? Suggest(PTreeRepository repository, string relative)
    {
        (string Node, string Prefix)? best = null;
        foreach (var node in repository.Nodes)
        {
            if (node.Id is null) continue;
            var prefixes = new List<string>();
            if (!string.IsNullOrWhiteSpace(node.Home)) prefixes.Add(node.Home.Replace('\\', '/').TrimEnd('/'));
            foreach (var reference in node.Files) prefixes.Add(reference.Replace('\\', '/').TrimEnd('/'));
            foreach (var prefix in prefixes)
            {
                if (prefix.Length == 0) continue;
                if (!relative.StartsWith(prefix + "/", StringComparison.Ordinal)) continue;
                if (best is null || prefix.Length > best.Value.Prefix.Length) best = (node.Id, prefix);
            }
        }
        return best;
    }

    private static bool Matches(string pattern, string relative)
    {
        var normalized = pattern.Replace('\\', '/');
        var expression = "^" + Regex.Escape(normalized)
            .Replace("\\*\\*", "\u0001")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", "[^/]")
            .Replace("\u0001", ".*") + "$";
        var name = relative.Contains('/') ? relative[(relative.LastIndexOf('/') + 1)..] : relative;
        return Regex.IsMatch(relative, expression, RegexOptions.CultureInvariant) ||
               Regex.IsMatch(name, expression, RegexOptions.CultureInvariant);
    }
}
