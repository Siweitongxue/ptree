using System.Globalization;
using System.Text.RegularExpressions;

namespace PTree.Core;

public sealed record TreeEntry(string Id, IReadOnlyList<string> Fields, int LineNo);

public sealed record TreeDocument(string Kind, string Title, string Version, string RelPath,
    IReadOnlyList<string> FieldNames, IReadOnlyList<TreeEntry> Entries)
{
    public Dictionary<string, TreeEntry> ById { get; } = Entries
        .GroupBy(entry => entry.Id, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    public bool Strict { get; init; } = true;
    public string IdPattern { get; init; } = "";
}

public sealed record TreeReference(string FromKind, string FromId, string TargetKind, string TargetId,
    string FieldName, int LineNo);

public sealed record TreeIssue(string Level, string Code, string Message, int? Line, string Kind);

public sealed class TreeStore
{
    private static readonly Regex DRef = new(@"^D[0-9]{2,}$", RegexOptions.CultureInvariant);
    private static readonly Regex MRef = new(@"^M[0-9]{2,}$", RegexOptions.CultureInvariant);
    private static readonly Regex URef = new(@"^U[0-9]{2,}$", RegexOptions.CultureInvariant);
    private static readonly Regex ProgramRef = new(@"^[a-z][a-z0-9-]*(\.[a-z0-9-]+)*$", RegexOptions.CultureInvariant);

    private sealed record TreeConfig(string Kind, string File, string IdPattern, bool Strict);

    public List<TreeDocument> Documents { get; } = new();
    public List<TreeReference> References { get; } = new();
    public List<TreeIssue> ParseIssues { get; } = new();

    private static List<TreeConfig> DefaultConfigs() => new()
    {
        new("程序树", "程序树", ProgramRef.ToString(), true),
        new("策划树", "策划树", @"^D[0-9]{2,}(\.[0-9]+)?$", true),
        new("美术树", "美术树", MRef.ToString(), true),
        new("UI树", "UI树", URef.ToString(), true),
    };

    private static List<TreeConfig> ConfiguredTrees(string root, TreeStore store)
    {
        var manifestPath = Path.Combine(root, "00-程序树", "program-tree.yaml");
        if (!File.Exists(manifestPath)) return DefaultConfigs();
        try
        {
            var raw = RawYaml.Parse(File.ReadAllText(manifestPath));
            if (raw is not RawMap manifest || !manifest.TryGetValue("trees", out var treesRaw) ||
                treesRaw is not List<object?> trees || trees.Count == 0)
            {
                return DefaultConfigs();
            }
            var configs = new List<TreeConfig>();
            foreach (var item in trees)
            {
                if (item is not RawMap map)
                {
                    store.ParseIssues.Add(new TreeIssue("E", "T00", "trees 条目必须是映射", null, "trees"));
                    continue;
                }
                var kind = map.TryGetValue("kind", out var kindValue) ? kindValue as string : null;
                var file = map.TryGetValue("file", out var fileValue) ? fileValue as string : null;
                var pattern = map.TryGetValue("id_pattern", out var patternValue) ? patternValue as string : null;
                var strict = map.TryGetValue("strict", out var strictValue) && strictValue is true;
                if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(file) ||
                    string.IsNullOrWhiteSpace(pattern))
                {
                    store.ParseIssues.Add(new TreeIssue("E", "T00", "trees 条目必须包含非空 kind、file、id_pattern", null, kind ?? "trees"));
                    continue;
                }
                try { _ = new Regex(pattern); }
                catch (ArgumentException)
                {
                    store.ParseIssues.Add(new TreeIssue("E", "T00", "树 " + kind + " 的 id_pattern 不是合法正则：" + pattern, null, kind));
                    continue;
                }
                configs.Add(new TreeConfig(kind, file, pattern, strict));
            }
            if (configs.Count == 0) return DefaultConfigs();
            var merged = DefaultConfigs();
            foreach (var config in configs)
            {
                var index = merged.FindIndex(item => string.Equals(item.Kind, config.Kind, StringComparison.Ordinal));
                if (index >= 0) merged[index] = config;
                else merged.Add(config);
            }
            return merged;
        }
        catch (Exception)
        {
            return DefaultConfigs();
        }
    }

    public static TreeStore Load(string root)
    {
        var store = new TreeStore();
        foreach (var config in ConfiguredTrees(root, store))
        {
            var fileName = config.File.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? config.File[..^4] : config.File;
            var rel = "00-程序树/树实例/" + fileName + ".txt";
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                store.ParseIssues.Add(new TreeIssue("E", "T00", "树实例文件不存在：" + rel, null, config.Kind));
                continue;
            }
            var document = store.ParseDocument(config, rel, path);
            store.Documents.Add(document);
        }
        store.CollectReferences();
        return store;
    }

    private TreeDocument ParseDocument(TreeConfig config, string rel, string path)
    {
        var kind = config.Kind;
        var lines = PythonText.SplitLines(File.ReadAllText(path, new System.Text.UTF8Encoding(false, true)));
        var title = lines.Count > 0 ? lines[0].Split('（')[0].Trim() : kind;
        var version = "?";
        foreach (var line in lines.Take(8))
        {
            var match = Regex.Match(line, @"版本\s*([0-9]+)");
            if (match.Success) { version = match.Groups[1].Value; break; }
        }
        var fieldNames = new List<string>();
        foreach (var line in lines)
        {
            var formatMarker = line.Contains("每行字段顺序：") ? "每行字段顺序：" : line.Contains("每行格式：") ? "每行格式：" : null;
            if (formatMarker is null) continue;
            var format = line[(line.IndexOf(formatMarker, StringComparison.Ordinal) + formatMarker.Length)..];
            format = format.TrimEnd('。');
            fieldNames = format.Split('｜').Select((field, index) =>
            {
                var text = field.Trim();
                if (index == 0) text = Regex.Replace(text, @"^[0-9]+\s*", "");
                return text.Trim();
            }).Where(text => text.Length > 0).ToList();
            break;
        }
        if (fieldNames.Count == 0)
        {
            fieldNames = kind switch
            {
                "程序树" => new List<string> { "id", "名称", "一句话职责", "状态", "退役", "组", "时间", "策划条目" },
                "策划树" => new List<string> { "编号", "名称", "目标", "边界", "未定", "对应程序", "文档形态", "确认状态", "时间" },
                "美术树" => new List<string> { "编号", "名称", "分类", "用处", "风格", "状态", "组", "时间" },
                "UI树" => new List<string> { "编号", "名称", "场景", "时刻", "父子集", "字体", "状态", "组", "时间" },
                _ => new List<string> { "编号", "名称", "说明" },
            };
        }
        var idPattern = new Regex(config.IdPattern, RegexOptions.CultureInvariant);
        var entries = new List<TreeEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!line.Contains('｜')) continue;
            if (line.Contains("每行格式：") || line.Contains("每行字段顺序：") || line.Contains("列字段：") || line.StartsWith("版本", StringComparison.Ordinal)) continue;
            var fields = line.Split('｜').Select(field => field.Trim()).ToList();
            if (fields.Count < 2) continue;
            var id = fields[0];
            if (Regex.IsMatch(id, "^(G[0-9]{1,2}|MA[0-9]|U[0-9])$")) continue;
            if (!idPattern.IsMatch(id))
            {
                ParseIssues.Add(new TreeIssue("E", "T00", "无法解析的条目行：" + line, index + 1, kind));
                continue;
            }
            if (!seen.Add(id))
            {
                ParseIssues.Add(new TreeIssue("E", "T00", "重复 id：" + id + "（行 " + (index + 1) + "）", index + 1, kind));
                continue;
            }
            if (fields.Count < fieldNames.Count)
            {
                var missing = string.Join("、", fieldNames.Skip(fields.Count));
                ParseIssues.Add(new TreeIssue("E", "T00",
                    "条目 " + id + " 缺字段：" + missing + "（字段数 " + fields.Count + "/" + fieldNames.Count + "）",
                    index + 1, kind));
            }
            entries.Add(new TreeEntry(id, fields, index + 1));
        }
        return new TreeDocument(kind, title, version, rel, fieldNames, entries)
        {
            Strict = config.Strict,
            IdPattern = config.IdPattern,
        };
    }

    private void CollectReferences()
    {
        var docs = Documents.ToDictionary(document => document.Kind, StringComparer.Ordinal);
        foreach (var document in Documents)
        {
            foreach (var entry in document.Entries)
            {
                foreach (var reference in ExtractReferences(document, entry, docs))
                {
                    References.Add(reference);
                }
            }
        }
    }

    private IEnumerable<TreeReference> ExtractReferences(TreeDocument document, TreeEntry entry,
        Dictionary<string, TreeDocument> docs)
    {
        var known = document.Kind is "程序树" or "策划树" or "美术树" or "UI树";
        var wantedField = document.Kind switch
        {
            "程序树" => "策划",
            "策划树" => "对应程序",
            _ => "父子集",
        };
        var fieldIndex = document.FieldNames.ToList().FindIndex(name => name.Contains(wantedField, StringComparison.Ordinal));
        if (fieldIndex < 0 || fieldIndex >= entry.Fields.Count) yield break;
        var targetKind = known ? document.Kind switch
        {
            "程序树" => "策划树",
            "策划树" => "程序树",
            "美术树" => "美术树",
            _ => "UI树",
        } : document.Kind;
        var pattern = known ? targetKind switch
        {
            "程序树" => ProgramRef,
            "策划树" => new Regex(@"^D[0-9]{2,}(\.[0-9]+)?$", RegexOptions.CultureInvariant),
            "美术树" => MRef,
            _ => URef,
        } : new Regex(document.IdPattern, RegexOptions.CultureInvariant);
        var fieldName = document.FieldNames.Count > fieldIndex ? document.FieldNames[fieldIndex] : "引用";
        foreach (var token in Regex.Split(entry.Fields[fieldIndex], "[、,，；;]"))
        {
            var value = token.Trim();
            if (value.Length == 0 || !pattern.IsMatch(value)) continue;
            if (value.Contains("全部") || value.Contains("程序树")) continue;
            yield return new TreeReference(document.Kind, entry.Id, targetKind, value, fieldName, entry.LineNo);
        }
    }

    public TreeDocument? FindDocument(string kind) => Documents.FirstOrDefault(doc => doc.Kind == kind);

    public (TreeDocument Document, TreeEntry Entry)? FindEntry(string id)
    {
        foreach (var document in Documents)
        {
            if (document.ById.TryGetValue(id, out var entry))
            {
                return (document, entry);
            }
        }
        return null;
    }
}

public static class TreeChecker
{
    public static List<TreeIssue> Check(TreeStore store)
    {
        var issues = new List<TreeIssue>(store.ParseIssues);
        var docs = store.Documents.ToDictionary(document => document.Kind, StringComparer.Ordinal);

        // T02 策划条目必须有确认状态
        if (docs.TryGetValue("策划树", out var planningDoc) && planningDoc.Strict)
        {
            foreach (var entry in planningDoc.Entries)
            {
                var hasConfirmation = entry.Fields.Any(field =>
                    field == "已确认" || field == "未确认" ||
                    field.StartsWith("已确认", StringComparison.Ordinal) ||
                    field.StartsWith("未确认", StringComparison.Ordinal));
                if (!hasConfirmation)
                {
                    issues.Add(new TreeIssue("E", "T02",
                        "策划条目缺少确认状态（未确认/已确认）：" + entry.Id, entry.LineNo, "策划树"));
                }
            }
        }

        // T03 引用必须存在
        foreach (var reference in store.References)
        {
            if (!docs.TryGetValue(reference.TargetKind, out var target) ||
                !target.ById.ContainsKey(reference.TargetId))
            {
                issues.Add(new TreeIssue("E", "T03",
                    "断链：" + reference.FromKind + " 条目 " + reference.FromId + " 的 " +
                    reference.FieldName + " 引用 " + reference.TargetId + " 不存在于 " + reference.TargetKind,
                    reference.LineNo, reference.FromKind));
            }
        }

        // T04 美术条目字段
        if (docs.TryGetValue("美术树", out var artDoc) && artDoc.Strict)
        {
            foreach (var entry in artDoc.Entries)
            {
                var missing = new List<string>();
                foreach (var fieldName in new[] { "名称", "分类", "用处", "风格" })
                {
                    var position = artDoc.FieldNames.ToList().FindIndex(name => name == fieldName);
                    if (position < 0 || entry.Fields.Count <= position || entry.Fields[position].Trim().Length == 0) missing.Add(fieldName);
                }
                if (missing.Count > 0)
                    issues.Add(new TreeIssue("E", "T04",
                        "美术条目缺必填字段：" + entry.Id + " 缺 " + string.Join("、", missing), entry.LineNo, "美术树"));
            }
        }

        // T05 UI 条目字段
        if (docs.TryGetValue("UI树", out var uiDoc) && uiDoc.Strict)
        {
            foreach (var entry in uiDoc.Entries)
            {
                var missing = new List<string>();
                foreach (var fieldName in new[] { "场景", "时刻", "父子集" })
                {
                    var position = uiDoc.FieldNames.ToList().FindIndex(name => name == fieldName);
                    if (position < 0 || entry.Fields.Count <= position || entry.Fields[position].Trim().Length == 0) missing.Add(fieldName);
                }
                if (missing.Count > 0)
                    issues.Add(new TreeIssue("E", "T05",
                        "UI 条目缺必填字段：" + entry.Id + " 缺 " + string.Join("、", missing), entry.LineNo, "UI树"));
            }
        }

        // T06 状态三档 + 退役
        foreach (var document in store.Documents.Where(document => document.Strict))
        {
            var statusIndex = document.Kind == "策划树" ? -1 : document.FieldNames.ToList().FindIndex(name => name == "状态");
            if (statusIndex < 0) continue;
            foreach (var entry in document.Entries)
            {
                if (entry.Fields.Count <= statusIndex) continue;
                var status = entry.Fields[statusIndex].Trim();
                if (status.Length == 0) continue;
                var mapped = status switch
                {
                    "planned" or "in_progress" => "未完成",
                    "built" => "已完成",
                    "verified" => "已验证",
                    "legacy" or "deprecated" or "已退役" => "退役",
                    _ => null,
                };
                if (mapped is not null)
                {
                    issues.Add(new TreeIssue("I", "T06",
                        "旧六值状态 " + status + "（条目 " + entry.Id + "），迁移后应为：" + mapped,
                        entry.LineNo, document.Kind));
                }
                else if (status is not ("未完成" or "已完成" or "已验证" or "退役" or "已退役"))
                {
                    issues.Add(new TreeIssue("E", "T06",
                        "状态非法：" + status + "（条目 " + entry.Id + "，只允许未完成/已完成/已验证/已退役）",
                        entry.LineNo, document.Kind));
                }
            }
        }
        return issues;
    }

    public static string MapProgramStatus(string status) => status switch
    {
        "未完成" or "planned" or "in_progress" => "未完成",
        "已完成" or "built" => "已完成",
        "已验证" or "verified" => "已验证",
        "退役" or "已退役" or "legacy" or "deprecated" => "退役",
        _ => "未知",
    };
}

/// <summary>
/// 网格条目（阅读项）：树实例里一行有编号的内容，编号是它的唯一名字。
/// 程序树网格的条目就是节点，短介绍由 R37 管，这里不收录。
/// </summary>
public sealed record GridReading(string Kind, string Id, string RelPath, int LineNo,
    IReadOnlyList<string> FieldNames, IReadOnlyList<string> Fields)
{
    public string Name => Fields.Count > 1 ? Fields[1] : Id;

    /// <summary>组行（MA0–MA4、U0–U2）字段比网格字段表少，按「编号 名称 父 子集 说明」读。</summary>
    public bool IsGroupRow { get; init; }
}

/// <summary>
/// 网格阅读项的唯一定义处：校验器 R39、读工具的行卡、tree_map 的覆盖统计共用这一份，
/// 免得三处各解析一遍树实例。程序树网格一律跳过。
/// </summary>
public static class GridReadings
{
    public const string ProgramKind = "程序树";
    private static readonly string[] GroupFieldNames = { "编号", "名称", "父", "子集", "说明" };

    // TreeStore 只收字段齐全的条目行；组行（MA0–MA4、U0–U2）字段少于字段表，被解析器跳过，
    // 但它们同样是「有编号的条目」，R39 与读工具都要算进来。
    private static readonly Regex GroupRowId = new(@"^(G[0-9]{1,2}|MA[0-9]|U[0-9])$", RegexOptions.CultureInvariant);

    public static List<GridReading> Items(TreeStore store, string root)
    {
        var items = new List<GridReading>();
        foreach (var document in store.Documents)
        {
            if (string.Equals(document.Kind, ProgramKind, StringComparison.Ordinal)) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                if (!seen.Add(entry.Id)) continue;
                items.Add(new GridReading(document.Kind, entry.Id, document.RelPath, entry.LineNo,
                    document.FieldNames, entry.Fields));
            }
            var path = Path.Combine(root, document.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            var lines = PythonText.SplitLines(File.ReadAllText(path, new System.Text.UTF8Encoding(false, true)));
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                if (!line.Contains('｜')) continue;
                var cells = line.Split('｜').Select(cell => cell.Trim()).ToList();
                if (cells.Count < 2 || !GroupRowId.IsMatch(cells[0])) continue;
                if (!seen.Add(cells[0])) continue;
                items.Add(new GridReading(document.Kind, cells[0], document.RelPath, index + 1,
                    GroupFieldNames, cells) { IsGroupRow = true });
            }
        }
        return items;
    }

    public static GridReading? Find(TreeStore store, string root, string id)
        => Items(store, root).FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    public static string ShortRel(string id) => "00-程序树/短介绍/" + id + ".txt";

    public static string ShortPath(string root, string id)
        => Path.Combine(root, "00-程序树", "短介绍", id + ".txt");
}

