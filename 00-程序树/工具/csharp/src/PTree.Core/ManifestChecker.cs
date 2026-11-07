using System.Globalization;
using System.Text.RegularExpressions;

namespace PTree.Core;

/// <summary>清单扩展检查 T07-T12：组、时间戳、策划全覆盖、附件、文档形态、体验预设节点。</summary>
public static class ManifestChecker
{
    private static readonly Regex DateRe = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);
    private static readonly string[] PresetIds =
    {
        "governance.program-tree-plugin.board",
        "governance.program-tree-plugin.terminal",
        "governance.program-tree-plugin.debug-suppress",
    };

    public static List<TreeIssue> Check(PTreeRepository repository, TreeStore trees)
    {
        var issues = new List<TreeIssue>();
        CheckGroups(repository, issues);
        CheckTimestamps(repository, issues);
        CheckAttachments(repository, issues);
        issues.AddRange(CheckPlanningCoverage(repository, trees));
        CheckDocumentShape(trees, issues);
        CheckPresets(repository, issues);
        return issues;
    }

    private static void CheckGroups(PTreeRepository repository, List<TreeIssue> issues)
    {
        var groups = repository.Manifest.Groups;
        if (groups.Count == 0)
        {
            issues.Add(new TreeIssue("E", "T07", "清单缺少 groups 段", null, "清单"));
            return;
        }
        var nodes = repository.NodeMap.Keys.ToHashSet(StringComparer.Ordinal);
        var groupMap = groups.Where(group => group.Id is { Length: > 0 })
            .GroupBy(group => group.Id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var id = group.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                issues.Add(new TreeIssue("E", "T07", "组缺少 id", null, "组"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                issues.Add(new TreeIssue("E", "T07", "组缺少 name：" + id, null, id));
            }
            if (string.IsNullOrWhiteSpace(group.Description))
            {
                issues.Add(new TreeIssue("E", "T07", "组缺少 description：" + id, null, id));
            }
            if (group.Parent is not null && (!groupMap.ContainsKey(group.Parent) || group.Parent == id))
            {
                issues.Add(new TreeIssue("E", "T07", "组的 parent 不存在或自指：" + id + " -> " + group.Parent, null, id));
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in group.Members)
            {
                if (!seen.Add(member))
                {
                    issues.Add(new TreeIssue("E", "T07", "组成员重复：" + id + " -> " + member, null, id));
                }
                if (!nodes.Contains(member) && !groupMap.ContainsKey(member))
                {
                    issues.Add(new TreeIssue("E", "T07", "组成员不存在（既不是节点也不是组）：" + id + " -> " + member, null, id));
                }
            }
            var depth = 1;
            var current = group;
            var chain = new HashSet<string>(StringComparer.Ordinal) { id };
            while (current.Parent is not null)
            {
                if (!chain.Add(current.Parent))
                {
                    issues.Add(new TreeIssue("E", "T07", "组 parent 成环：" + id, null, id));
                    break;
                }
                depth++;
                if (!groupMap.TryGetValue(current.Parent, out var parentGroup))
                {
                    break;
                }
                current = parentGroup;
            }
            if (depth > 2)
            {
                issues.Add(new TreeIssue("E", "T07", "组层级超过两层：" + id + "（深度 " + depth + "）", null, id));
            }
        }
        foreach (var node in repository.Nodes)
        {
            if (node.Id is { Length: > 0 } id && repository.GroupsForNode(id).Count == 0)
            {
                issues.Add(new TreeIssue("E", "T07", "节点没有组归属：" + id, null, id));
            }
        }
    }

    private static void CheckTimestamps(PTreeRepository repository, List<TreeIssue> issues)
    {
        foreach (var node in repository.Nodes)
        {
            var id = node.Id ?? "?";
            foreach (var (name, value) in new[] { ("created_at", node.CreatedAt), ("updated_at", node.UpdatedAt) })
            {
                if (!(value is not null && DateRe.IsMatch(value) &&
                      DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
                {
                    issues.Add(new TreeIssue("E", "T08", "节点 " + id + " 的 " + name + " 必须是合法日期 YYYY-MM-DD", null, id));
                }
            }
            if (node.CreatedAt is { } created && node.UpdatedAt is { } updated &&
                DateRe.IsMatch(created) && DateRe.IsMatch(updated) &&
                DateTime.TryParseExact(created, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var c) &&
                DateTime.TryParseExact(updated, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u) && u < c)
            {
                issues.Add(new TreeIssue("E", "T08", "节点 " + id + " 的 updated_at 早于 created_at", null, id));
            }
        }
    }

    private static void CheckAttachments(PTreeRepository repository, List<TreeIssue> issues)
    {
        foreach (var node in repository.Nodes)
        {
            var id = node.Id ?? "?";
            foreach (var attachment in node.Attachments)
            {
                if (string.IsNullOrWhiteSpace(attachment.Path) || string.IsNullOrWhiteSpace(attachment.Kind))
                {
                    issues.Add(new TreeIssue("E", "T10", "节点 " + id + " 的附件必须同时有 path 与 kind", null, id));
                    continue;
                }
                var path = attachment.Path!;
                if (Path.IsPathRooted(path) || path.Contains(':') || path.Split('/', '\\').Any(part => part == ".."))
                {
                    issues.Add(new TreeIssue("E", "T10", "附件必须是仓库内相对路径：" + id + " -> " + path, null, id));
                    continue;
                }
                var full = Path.Combine(repository.Root, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full) && !Directory.Exists(full))
                {
                    issues.Add(new TreeIssue("E", "T10", "附件文件不存在：" + id + " -> " + path, null, id));
                }
            }
        }
    }

    public static List<TreeIssue> CheckPlanningCoverage(PTreeRepository repository, TreeStore trees)
    {
        var issues = new List<TreeIssue>();
        var planning = trees.FindDocument("策划树");
        if (planning is null)
        {
            issues.Add(new TreeIssue("E", "T09", "策划树实例不存在，无法检查策划全覆盖", null, "策划树"));
            return issues;
        }
        var referenceIndex = planning.FieldNames.ToList().FindIndex(name => name == "对应程序");
        if (referenceIndex < 0)
        {
            issues.Add(new TreeIssue("E", "T09", "策划树缺少对应程序字段，无法检查策划全覆盖", null, "策划树"));
            return issues;
        }
        var nodeIds = repository.NodeMap.Keys.ToHashSet(StringComparer.Ordinal);
        var idPattern = new Regex(@"^[a-z][a-z0-9-]*(\.[a-z0-9-]+)*$", RegexOptions.CultureInvariant);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in planning.Entries)
        {
            if (entry.Fields.Count <= referenceIndex) continue;
            foreach (var token in Regex.Split(entry.Fields[referenceIndex], "[、,，；;]"))
            {
                var value = token.Trim();
                if (value.Length == 0) continue;
                if (value == "全部程序节点")
                {
                    issues.Add(new TreeIssue("E", "T09",
                        "策划条目不能使用通配词 全部程序节点，必须逐节点映射或对程序节点写 planning_exemption：" + entry.Id,
                        entry.LineNo, "策划树"));
                    continue;
                }
                if (!idPattern.IsMatch(value))
                {
                    issues.Add(new TreeIssue("E", "T09",
                        "策划条目引用不是合法程序 id：" + entry.Id + " -> " + value,
                        entry.LineNo, "策划树"));
                    continue;
                }
                if (!nodeIds.Contains(value))
                {
                    issues.Add(new TreeIssue("E", "T09",
                        "策划条目引用不存在的程序 id：" + entry.Id + " -> " + value,
                        entry.LineNo, "策划树"));
                    continue;
                }
                referenced.Add(value);
            }
        }
        foreach (var node in repository.Nodes.Where(node => node.Kind == "program"))
        {
            var id = node.Id ?? "?";
            if (referenced.Contains(id)) continue;
            if (!string.IsNullOrWhiteSpace(node.PlanningExemption))
            {
                issues.Add(new TreeIssue("I", "T09",
                    "策划豁免：" + id + " —— " + node.PlanningExemption, null, id));
                continue;
            }
            issues.Add(new TreeIssue("E", "T09",
                "program 节点没有被任何策划条目引用，也没有 planning_exemption 理由：" + id, null, id));
        }
        return issues;
    }

    private static void CheckDocumentShape(TreeStore trees, List<TreeIssue> issues)
    {
        var planning = trees.FindDocument("策划树");
        if (planning is null)
        {
            return;
        }
        var formIndex = planning.FieldNames.ToList().FindIndex(name => name == "文档形态");
        foreach (var entry in planning.Entries)
        {
            var value = formIndex >= 0 && entry.Fields.Count > formIndex ? entry.Fields[formIndex].Trim() : string.Empty;
            if (value is not ("整合稿" or "转述稿"))
            {
                issues.Add(new TreeIssue("E", "T11",
                    "策划条目缺少或非法 文档形态（整合稿/转述稿）：" + entry.Id, entry.LineNo, "策划树"));
            }
            var confirmed = entry.Fields.Any(field => field == "已确认" || field == "未确认");
            var hasAnyConfirm = entry.Fields.Any(field => field.Contains("确认", StringComparison.Ordinal));
            if (hasAnyConfirm && !confirmed)
            {
                issues.Add(new TreeIssue("E", "T12",
                    "策划条目确认状态非法（只允许未确认/已确认）：" + entry.Id, entry.LineNo, "策划树"));
            }
        }
    }

    private static void CheckPresets(PTreeRepository repository, List<TreeIssue> issues)
    {
        // 预设节点是可选能力：清单里一个都没有时跳过，避免把内部项目名写进公开内核。
        if (!PresetIds.Any(id => repository.NodeMap.ContainsKey(id)))
        {
            return;
        }
        foreach (var id in PresetIds)
        {
            if (!repository.NodeMap.TryGetValue(id, out var node))
            {
                issues.Add(new TreeIssue("E", "T12", "缺少体验插件预设节点：" + id, null, "清单"));
                continue;
            }
            if (node.Status != StatusValues.Todo)
            {
                issues.Add(new TreeIssue("E", "T12", "体验插件预设节点状态必须为 未完成：" + id, null, id));
            }
            if (string.IsNullOrWhiteSpace(node.Responsibility) || node.NotResponsible.Count == 0)
            {
                issues.Add(new TreeIssue("E", "T12", "体验插件预设节点必须写清职责与不做什么：" + id, null, id));
            }
        }
    }
}
