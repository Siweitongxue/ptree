using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PTree.Core;

public static partial class NodeTools
{
    public static string Status(string? root) => Status(root, null);

    public static string Status(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var validation = Validator.Validate(repository);
        var format = (StringArg(args, "format") ?? "card").Trim().ToLowerInvariant();
        if (format is not ("card" or "full"))
            return Fail(new[] { "format 只能是 card（默认纯文本卡）或 full（原 JSON）" }, repository);
        if (format == "card") return NodeCards.RenderStatus(repository, validation);
        var stats = Reporting.BuildStats(repository);
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = validation.Ok,
            ["tree_version"] = repository.Manifest.TreeVersion,
            ["counts"] = new Dictionary<string, int>
            {
                ["nodes"] = repository.Nodes.Count,
                ["errors"] = validation.Errors.Count,
                ["warnings"] = validation.Warnings.Count,
            },
            ["by_status"] = repository.Nodes.GroupBy(node => node.Status ?? "?").ToDictionary(group => group.Key, group => group.Count()),
            ["by_kind"] = repository.Nodes.GroupBy(node => node.Kind ?? "?").ToDictionary(group => group.Key, group => group.Count()),
            ["stats"] = stats,
        });
    }

    /// <summary>整棵树粗读：mode=names（只给名称+联系）或 summary（再加一行短介绍）。</summary>
    public static string Map(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var mode = (StringArg(args, "mode") ?? "blocks").Trim().ToLowerInvariant();
        if (mode is not ("blocks" or "names" or "summary"))
            return Fail(new[] { "mode 只能是 blocks（功能块/部门分区，默认）、names（名称+联系）或 summary（再加短介绍）" }, repository);
        if (mode == "blocks") return NodeCards.RenderBlocks(repository);
        return NodeCards.RenderMap(repository, mode);
    }

    /// <summary>阅读阶梯一句话版：给 AI 一次调用就知道先粗后细怎么走。</summary>
    public static string Guide(string? root)
    {
        var repository = PTreeRepository.Load(root);
        return NodeCards.RenderGuide(repository);
    }

    /// <summary>组内视图：某功能块/部门组里有哪些节点；summary 档带一行简介与用法。</summary>
    public static string ReadGroup(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var id = StringArg(args, "id");
        if (string.IsNullOrWhiteSpace(id)) return Fail(new[] { "id 不能为空：组 id 形如 group.tree-toolchain" }, repository);
        var group = repository.Groups.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (group is null) return Fail(new[] { "组不存在：" + id + "（用 tree_map mode=blocks 看有哪些块）" }, repository);
        var mode = (StringArg(args, "mode") ?? "names").Trim().ToLowerInvariant();
        if (mode is not ("names" or "summary"))
            return Fail(new[] { "mode 只能是 names（成员名）或 summary（再加简介与用法）" }, repository);
        return NodeCards.RenderGroupMembers(repository, group, mode);
    }

    /// <summary>显式联系的清单：可按节点过滤，列出类型与依据；没有 note 的单独提示。</summary>
    public static string Links(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var node = StringArg(args, "node");
        var all = Links(repository.ManifestRaw);
        var rows = all
            .Where(link => string.IsNullOrWhiteSpace(node) ||
                           StringOf(link, "from") == node || StringOf(link, "to") == node)
            .ToList();
        var lines = new List<string>();
        foreach (var link in rows)
        {
            var note = StringOf(link, "note") ?? "";
            var noteText = note.Length == 0 ? " ⚠ 没写依据" : " · " + (note.Length <= 90 ? note : note[..90] + "…");
            lines.Add((StringOf(link, "from") ?? "?") + " -[" + (StringOf(link, "type") ?? "引用") + "]-> " +
                      (StringOf(link, "to") ?? "?") + noteText);
        }
        lines.Add("联系 " + rows.Count + " 条" + (string.IsNullOrWhiteSpace(node) ? "" : "（过滤：" + node + "）") +
                  (all.Count > rows.Count ? "，全树共 " + all.Count + " 条" : ""));
        return string.Join("\n", lines);
    }

    public static string Check(string? root)
    {
        var repository = PTreeRepository.Load(root);
        var validation = Validator.Validate(repository);
        var store = TreeStore.Load(repository.Root);
        var treeIssues = TreeChecker.Check(store);
        treeIssues.AddRange(ManifestChecker.Check(repository, store));
        var errors = validation.Errors.Select(issue => Issue("E", issue.Code, issue.Node, issue.Message)).ToList();
        errors.AddRange(treeIssues.Where(issue => issue.Level == "E").Select(issue => Issue("E", issue.Code, issue.Kind, issue.Message)));
        var warnings = validation.Warnings.Select(issue => Issue("W", issue.Code, issue.Node, issue.Message)).ToList();
        warnings.AddRange(treeIssues.Where(issue => issue.Level == "W").Select(issue => Issue("W", issue.Code, issue.Kind, issue.Message)));
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = errors.Count == 0,
            ["tree_version"] = repository.Manifest.TreeVersion,
            ["counts"] = new Dictionary<string, int>
            {
                ["errors"] = errors.Count,
                ["warnings"] = warnings.Count,
                ["notes"] = validation.Notes.Count + treeIssues.Count(issue => issue.Level == "I"),
            },
            ["errors"] = errors,
            ["warnings"] = warnings,
            ["notes"] = validation.Notes.Take(10).ToList(),
        });
    }

    public static string ReadNode(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var id = StringArg(args, "id");
        if (string.IsNullOrWhiteSpace(id)) return Fail(new[] { "id 不能为空" }, repository);
        if (!repository.NodeMap.TryGetValue(id!, out var node))
        {
            // 功能块不是节点，但也要能被读到：id 命中组就直接给块卡
            var block = repository.Groups.FirstOrDefault(group => string.Equals(group.Id, id, StringComparison.Ordinal));
            if (block is null)
            {
                // 网格条目也不是节点：编号命中树实例的一行就给行卡（与 CLI node 命令同一份 TreeStore 解析）
                var gridStore = TreeStore.Load(repository.Root);
                var grid = GridReadings.Find(gridStore, repository.Root, id!);
                if (grid is null) return Fail(new[] { "节点不存在：" + id }, repository);
                if ((StringArg(args, "format") ?? "card").Trim().ToLowerInvariant() == "full")
                {
                    var gridFields = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var index = 0; index < grid.Fields.Count; index++)
                    {
                        var name = index < grid.FieldNames.Count ? grid.FieldNames[index] : "字段" + (index + 1);
                        gridFields[name] = grid.Fields[index];
                    }
                    return JsonOutput.Serialize(new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["kind"] = "grid",
                        ["grid"] = new Dictionary<string, object?>
                        {
                            ["id"] = grid.Id,
                            ["name"] = grid.Name,
                            ["tree"] = grid.Kind,
                            ["source"] = grid.RelPath,
                            ["line"] = grid.LineNo,
                            ["group_row"] = grid.IsGroupRow,
                            ["fields"] = gridFields,
                        },
                        ["short_intro"] = new Dictionary<string, object?>
                        {
                            ["path"] = GridReadings.ShortRel(grid.Id),
                            ["text"] = NodeCards.ReadShortIntro(repository, grid.Id),
                        },
                        ["tree_version"] = repository.Manifest.TreeVersion,
                    });
                }
                return NodeCards.RenderGridCard(repository, grid);
            }
            if ((StringArg(args, "format") ?? "card").Trim().ToLowerInvariant() == "full")
                return JsonOutput.Serialize(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["kind"] = "group",
                    ["group"] = new Dictionary<string, object?>
                    {
                        ["id"] = block.Id,
                        ["name"] = block.Name,
                        ["description"] = block.Description,
                        ["parent"] = block.Parent,
                        ["members"] = block.Members,
                        ["intro_path"] = "00-程序树/说明/" + block.Id + ".txt",
                    },
                    ["tree_version"] = repository.Manifest.TreeVersion,
                });
            return NodeCards.RenderGroupCard(repository, block);
        }

        var depth = IntArg(args, "depth") ?? 1;
        if (depth < 0) depth = 0;
        if (depth > NodeCards.MaxDepth) depth = NodeCards.MaxDepth;
        var format = (StringArg(args, "format") ?? "card").Trim().ToLowerInvariant();
        if (format is not ("card" or "full"))
            return Fail(new[] { "format 只能是 card（默认纯文本卡）或 full（原 JSON）" }, repository);
        if (format == "card") return NodeCards.RenderNode(repository, id!, depth);

        var store = TreeStore.Load(repository.Root);
        var raw = FindNode(repository.ManifestRaw, id!);
        var intro = File.Exists(repository.IntroPath(id!)) ? File.ReadAllText(repository.IntroPath(id!)) : null;

        var children = Descendants(repository, id!, depth == 0 ? NodeCards.MaxDepth : depth);
        var siblings = repository.Nodes
            .Where(other => other.Id != id && other.Parent == node.Parent)
            .Select(Summary)
            .ToList();
        var contractsUpstream = repository.Contracts.Contracts
            .Where(contract => contract.Consumers.Contains(id!, StringComparer.Ordinal))
            .Select(contract => new Dictionary<string, object?>
            {
                ["contract"] = contract.Id,
                ["name"] = contract.Name,
                ["producers"] = contract.Producers.Count == 0 ? new List<object?> { "外部" } : contract.Producers.Cast<object?>().ToList(),
                ["purpose"] = contract.Purpose,
            }).ToList();
        var contractsDownstream = repository.Contracts.Contracts
            .Where(contract => contract.Producers.Contains(id!, StringComparer.Ordinal))
            .Select(contract => new Dictionary<string, object?>
            {
                ["contract"] = contract.Id,
                ["name"] = contract.Name,
                ["consumers"] = contract.Consumers.Count == 0 ? new List<object?> { "外部" } : contract.Consumers.Cast<object?>().ToList(),
                ["purpose"] = contract.Purpose,
            }).ToList();
        var depends = node.DependsOn.Where(dep => repository.NodeMap.ContainsKey(dep)).Select(dep => Summary(repository.NodeMap[dep])).ToList();
        var dependedBy = repository.Nodes.Where(other => other.Id != id && other.DependsOn.Contains(id!, StringComparer.Ordinal))
            .Select(Summary).ToList();
        var outgoingLinks = Links(repository.ManifestRaw).Where(link => StringOf(link, "from") == id).Select(JsonValue).ToList();
        var incomingLinks = Links(repository.ManifestRaw).Where(link => StringOf(link, "to") == id).Select(JsonValue).ToList();
        var impactDeps = repository.Nodes.Where(other => other.Id != id && other.DependsOn.Contains(id!, StringComparer.Ordinal))
            .Select(other => other.Id).Where(value => value is not null).ToList();
        var impactConsumers = contractsDownstream
            .SelectMany(item => (item["consumers"] as List<object?> ?? new List<object?>()).OfType<string>())
            .Where(value => value != "外部").Distinct(StringComparer.Ordinal).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["tree_version"] = repository.Manifest.TreeVersion,
            ["node"] = raw is null ? Summary(node) : JsonValue(raw),
            ["intro"] = intro is null ? null : new Dictionary<string, object?> { ["path"] = repository.IntroRel(id!), ["text"] = intro },
            ["groups"] = repository.GroupsForNode(id!),
            ["neighbors"] = new Dictionary<string, object?>
            {
                ["upstream"] = contractsUpstream,
                ["downstream"] = contractsDownstream,
                ["children"] = children,
                ["parent"] = node.Parent is not null && repository.NodeMap.TryGetValue(node.Parent, out var parentNode) ? Summary(parentNode) : null,
                ["siblings"] = siblings,
                ["depends_on"] = depends,
                ["depended_by"] = dependedBy,
                ["related_plan"] = RelatedPlanning(repository, id!, store),
                ["related_art"] = RelatedTree(repository, "美术树", id!, store),
                ["related_ui"] = RelatedTree(repository, "UI树", id!, store),
                ["links_out"] = outgoingLinks,
                ["links_in"] = incomingLinks,
                ["impact"] = new Dictionary<string, object?>
                {
                    ["direct_dependents"] = impactDeps,
                    ["contract_consumers"] = impactConsumers,
                    ["descendants"] = CountDescendants(repository, id!),
                },
            },
        };
        return JsonOutput.Serialize(payload);
    }

    public static string Find(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var query = StringArg(args, "query");
        var status = StringArg(args, "status");
        var kind = StringArg(args, "kind");
        var limit = Math.Clamp(IntArg(args, "limit") ?? 20, 1, 20);
        IEnumerable<NodeModel> matched = repository.Nodes;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var keyword = query.Trim();
            matched = matched.Where(node =>
                (node.Id ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (node.Name ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (node.Responsibility ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(status)) matched = matched.Where(node => node.Status == status);
        if (!string.IsNullOrWhiteSpace(kind)) matched = matched.Where(node => node.Kind == kind);
        var rows = matched.OrderBy(node => node.Layer).ThenBy(node => node.Id, StringComparer.Ordinal).Take(limit).ToList();
        var blocks = string.IsNullOrWhiteSpace(query) || !string.IsNullOrWhiteSpace(kind)
            ? new List<GroupModel>()
            : repository.Groups.Where(group =>
                    !string.IsNullOrEmpty(group.Id) && group.Id.StartsWith("group.", StringComparison.Ordinal) &&
                    group.Id != "group.all" && !group.Id.StartsWith("group.dept-", StringComparison.Ordinal) &&
                    ((group.Id ?? "").Contains(query!.Trim(), StringComparison.OrdinalIgnoreCase) ||
                     (group.Name ?? "").Contains(query!.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Take(limit).ToList();
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["tree_version"] = repository.Manifest.TreeVersion,
            ["count"] = rows.Count + blocks.Count,
            ["groups"] = blocks.Select(group => new Dictionary<string, object?>
            {
                ["id"] = group.Id,
                ["name"] = group.Name,
                ["kind"] = "group",
                ["short_intro"] = group.Description,
                ["members"] = group.Members.Count,
            }).ToList(),
            ["nodes"] = rows.Select(node => new Dictionary<string, object?>
            {
                ["id"] = node.Id,
                ["name"] = node.Name,
                ["status"] = node.Status,
                ["kind"] = node.Kind,
                ["preset"] = node.Preset,
                ["responsibility"] = node.Responsibility,
                ["short_intro"] = node.Id is null ? "" : NodeCards.ReadShortIntro(repository, node.Id),
            }).ToList(),
        });
    }

    public static string Changes(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var node = StringArg(args, "node");
        var days = IntArg(args, "days") ?? 30;
        var limit = IntArg(args, "limit") ?? 20;
        var changes = HealthCheck.ReadChanges(repository.Root, node, days, limit);
        return HealthCheck.RenderChanges(changes);
    }

    public static string Scan(string? root)
    {
        var repository = PTreeRepository.Load(root);
        return JsonOutput.Serialize(AdaptScanner.Scan(repository));
    }

    private static List<object?> Descendants(PTreeRepository repository, string id, int depth)
    {
        var result = new List<object?>();
        foreach (var child in repository.Nodes.Where(node => node.Parent == id).OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            var entry = new Dictionary<string, object?>
            {
                ["id"] = child.Id,
                ["name"] = child.Name,
                ["kind"] = child.Kind,
                ["status"] = child.Status,
                ["responsibility"] = child.Responsibility,
            };
            if (depth > 1 && child.Id is not null) entry["children"] = Descendants(repository, child.Id, depth - 1);
            result.Add(entry);
        }
        return result;
    }

    private static int CountDescendants(PTreeRepository repository, string id)
    {
        var count = 0;
        var queue = new Queue<string>();
        queue.Enqueue(id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in repository.Nodes.Where(node => node.Parent == current && node.Id is not null))
            {
                count++;
                queue.Enqueue(child.Id!);
            }
        }
        return count;
    }

    internal static List<object?> RelatedPlanning(PTreeRepository repository, string id, TreeStore store)
    {
        var planning = store.FindDocument("策划树");
        if (planning is null) return new List<object?>();
        var index = planning.FieldNames.ToList().FindIndex(name => name == "对应程序");
        if (index < 0) return new List<object?>();
        var result = new List<object?>();
        foreach (var entry in planning.Entries)
        {
            if (entry.Fields.Count <= index) continue;
            var tokens = Regex.Split(entry.Fields[index], "[、,，；;]").Select(token => token.Trim());
            if (!tokens.Any(token => token == id || token.StartsWith(id + ".", StringComparison.Ordinal))) continue;
            var confirm = entry.Fields.FirstOrDefault(field => field.StartsWith("已确认", StringComparison.Ordinal) || field.StartsWith("未确认", StringComparison.Ordinal)) ?? "未确认";
            result.Add(new Dictionary<string, object?>
            {
                ["id"] = entry.Id,
                ["name"] = entry.Fields.Count > 1 ? entry.Fields[1] : null,
                ["document"] = entry.Fields.Count > 6 ? entry.Fields[6] : null,
                ["confirmation"] = confirm,
            });
            if (result.Count >= 20) break;
        }
        return result;
    }

    internal static List<object?> RelatedTree(PTreeRepository repository, string kind, string id, TreeStore store)
    {
        var document = store.FindDocument(kind);
        if (document is null) return new List<object?>();
        var index = document.FieldNames.ToList().FindIndex(name => name == "父子集");
        if (index < 0) return new List<object?>();
        return document.Entries.Where(entry => entry.Fields.Count > index &&
                Regex.Split(entry.Fields[index], "[、,，；;]").Select(token => token.Trim())
                    .Any(token => token == id || token.StartsWith(id + ".", StringComparison.Ordinal)))
            .Take(20)
            .Select(entry => (object?)new Dictionary<string, object?>
            {
                ["id"] = entry.Id,
                ["name"] = entry.Fields.Count > 1 ? entry.Fields[1] : null,
            }).ToList();
    }

    private static Dictionary<string, object?> Summary(NodeModel node) => new()
    {
        ["id"] = node.Id,
        ["name"] = node.Name,
        ["kind"] = node.Kind,
        ["status"] = node.Status,
        ["responsibility"] = node.Responsibility,
    };

    private static List<RawMap> Links(RawMap manifest) =>
        manifest.TryGetValue("links", out var raw) && raw is List<object?> list ? list.OfType<RawMap>().ToList() : new List<RawMap>();

    private static RawMap? FindNode(RawMap manifest, string id) =>
        Nodes(manifest).FirstOrDefault(node => StringOf(node, "id") == id);

    private static List<RawMap> Nodes(RawMap manifest) =>
        manifest.TryGetValue("nodes", out var raw) && raw is List<object?> list ? list.OfType<RawMap>().ToList() : new List<RawMap>();

    private static Dictionary<string, object?> Issue(string level, string code, string? node, string message) => new()
    {
        ["level"] = level,
        ["code"] = code,
        ["node"] = node,
        ["message"] = message,
    };

    private static string? StringOf(RawMap? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) ? value as string : null;
}
