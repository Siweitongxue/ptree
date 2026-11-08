using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PTree.Core;

/// <summary>节点级 MCP 工具实现：AI 只给字段，不碰文件；写操作原子落盘、台账、重生成文档。</summary>
public static class NodeTools
{
    // 联系类型白名单与 R40 共用 LinkKinds 一份，避免写入口和校验两处各写一遍
    private static readonly string[] LinkTypes = LinkKinds.All;
    private static readonly Regex IdPattern = new(@"^[a-z][a-z0-9-]*(\.[a-z0-9-]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex SlugPattern = new(@"^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);
    private static readonly string[] ReadonlyKinds = { "root", "department", "program", "step", "external" };

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
        PTreeRepository.Load(root);
        return NodeCards.RenderGuide();
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

    /// <summary>短介绍统一口径：一行、去空白 ≤80 字、不许占位词。</summary>
    private static string? NormalizeShortIntro(string? raw, out string? error)
    {
        error = null;
        if (raw is null) return null;
        var oneLine = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        var compact = oneLine.Count(ch => !char.IsWhiteSpace(ch));
        if (compact == 0) { error = "short_intro 不能为空"; return null; }
        if (compact > 80) { error = "short_intro 去空白后不能超过 80 字（实际 " + compact + " 字）"; return null; }
        if (oneLine.Contains("待补", StringComparison.Ordinal) ||
            oneLine.Contains("TODO", StringComparison.OrdinalIgnoreCase) ||
            oneLine.Contains("TBD", StringComparison.OrdinalIgnoreCase))
        { error = "short_intro 不得含占位词（待补 / TODO / TBD）"; return null; }
        return oneLine;
    }

    public static string AddNode(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var parentId = StringArg(args, "parent");
        var name = StringArg(args, "name");
        var kind = StringArg(args, "kind");
        var responsibility = StringArg(args, "responsibility");
        var intro = StringArg(args, "intro");
        var preset = BoolArg(args, "preset") == true;
        var outputsProvided = ListArg(args, "outputs") is not null;
        var outputsNone = BoolArg(args, "outputs_none") == true;
        var presetFiles = ListArg(args, "files");
        var shortIntro = NormalizeShortIntro(StringArg(args, "short_intro"), out var shortIntroError);
        var groupId = StringArg(args, "group");
        var errors = new List<string>();
        if (shortIntroError is not null) errors.Add(shortIntroError);
        if (!string.IsNullOrWhiteSpace(groupId) &&
            !repository.Groups.Any(group => string.Equals(group.Id, groupId, StringComparison.Ordinal)))
            errors.Add("功能块不存在：" + groupId);
        if (string.IsNullOrWhiteSpace(parentId)) errors.Add("parent 不能为空");
        if (string.IsNullOrWhiteSpace(name)) errors.Add("name 不能为空");
        if (string.IsNullOrWhiteSpace(responsibility)) errors.Add("responsibility 不能为空");
        if (string.IsNullOrWhiteSpace(intro)) errors.Add("intro 不能为空：它是 AI 读节点时看到的说明正文");
        if (string.IsNullOrWhiteSpace(kind) || !ReadonlyKinds.Contains(kind, StringComparer.Ordinal))
            errors.Add("kind 必须是 department / program / step / external 之一");
        if (preset && presetFiles is { Count: > 0 }) errors.Add("preset 占位节点不能带 files");
        if (preset && !outputsProvided && !outputsNone) errors.Add("preset 必须带 outputs，或显式传 outputs_none=true");
        if (preset && outputsNone && outputsProvided) errors.Add("preset 的 outputs 与 outputs_none 只能二选一");
        if (errors.Count > 0) return Fail(errors, repository);

        if (!repository.NodeMap.TryGetValue(parentId!, out var parent))
            return Fail(new[] { "parent 不存在：" + parentId }, repository);
        if (kind == "root") return Fail(new[] { "root 只能有一个，不能通过 tree_add_node 新增" }, repository);

        var slug = StringArg(args, "slug");
        if (string.IsNullOrWhiteSpace(slug)) slug = Slugify(name!);
        if (string.IsNullOrWhiteSpace(slug) || !SlugPattern.IsMatch(slug!))
            return Fail(new[] { "无法从 name 推出英文短名；请传 slug（小写字母开头，可含数字与连字符）" }, repository);

        var id = parentId + "." + slug;
        if (!IdPattern.IsMatch(id)) return Fail(new[] { "生成 id 非法：" + id }, repository);
        if (repository.NodeMap.ContainsKey(id)) return Fail(new[] { "节点已存在：" + id }, repository);

        var parentKind = parent.Kind ?? "";
        var expected = parentKind switch
        {
            "root" => new[] { "department", "external" },
            "department" => new[] { "program", "external" },
            "program" => new[] { "step", "external" },
            _ => Array.Empty<string>(),
        };
        if (expected.Length == 0 || !expected.Contains(kind, StringComparer.Ordinal))
            return Fail(new[] { "parent（" + parentKind + "）下不能挂 kind=" + kind }, repository);

        var inputRefs = ListArg(args, "inputs") ?? (kind is "program" or "step" ? parent.Inputs.Cast<object?>().ToList() : new List<object?>());
        var outputRefs = ListArg(args, "outputs") ?? (preset ? new List<object?>() : kind == "step" ? parent.Outputs.Cast<object?>().ToList() : new List<object?>());
        if (kind is "program" or "step" && outputRefs.Count == 0 && !(preset && outputsNone))
            return Fail(new[] { kind + " 至少需要 1 个 output 契约；请传 outputs（可先用父节点已有契约），preset 可传 outputs_none=true" }, repository);
        var contractIds = repository.Contracts.Contracts.Select(contract => contract.Id).Where(value => value is not null).ToHashSet(StringComparer.Ordinal);
        var badContract = inputRefs.Concat(outputRefs).OfType<string>().FirstOrDefault(reference => !contractIds.Contains(reference));
        if (badContract is not null) return Fail(new[] { "契约不存在：" + badContract }, repository);

        var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var node = new RawMap();
        node.Set("id", id);
        node.Set("name", name);
        node.Set("kind", kind);
        node.Set("layer", (parent.Layer ?? 0) + 1);
        node.Set("parent", parentId);
        node.Set("phase", StringArg(args, "phase") ?? parent.Phase ?? "P0");
        node.Set("status", "未完成");
        node.Set("responsibility", responsibility);
        node.Set("not_responsible", ListArg(args, "not_responsible") ??
                                   (kind is "program" or "step" ? new List<object?> { "不承担父节点之外的职责" } : new List<object?>()));
        node.Set("inputs", inputRefs);
        node.Set("outputs", outputRefs);
        node.Set("depends_on", ListArg(args, "depends_on") ?? new List<object?> { parentId });
        node.Set("files", preset ? new List<object?>() : ListArg(args, "files") ?? new List<object?>());
        if (preset)
        {
            node.Set("preset", true);
            node.Set("status", "未完成");
            if (outputsNone) node.Set("preset_outputs", "none");
        }
        var acceptance = ListArg(args, "acceptance");
        node.Set("acceptance", acceptance is { Count: > 0 } ? acceptance : new List<object?> { "人工清单：新增节点后人工复核职责与验收" });
        node.Set("spec", parent.Spec ?? "00-程序树/README.txt");
        node.Set("created_at", date);
        node.Set("updated_at", date);
        if (kind == "program") node.Set("planning_exemption", "MCP 新增程序，策划条目待补");

        var summaries = new List<string> { "add " + id + "（parent=" + parentId + "，kind=" + kind + "）" };
        var proposeOnly = BoolArg(args, "propose_only") == true;
        if (proposeOnly)
        {
            return JsonOutput.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["propose_only"] = true,
                ["id"] = id,
                ["node"] = JsonValue(node),
                ["errors"] = Array.Empty<string>(),
                ["tree_version"] = repository.Manifest.TreeVersion,
                ["note"] = "干跑：未落盘，未跑完整校验；去掉 propose_only 才真正新增",
            });
        }

        var outcome = Commit(repository, "mcp:tree_add_node", "MCP 新增节点 " + id,
            manifest =>
            {
                var parentRaw = FindNode(manifest, parentId!);
                if (parentRaw is null) throw new PTreeException("parent 不存在：" + parentId);
                var groups = EnsureList(manifest, "groups");
                var joined = 0;
                foreach (var group in groups.OfType<RawMap>())
                {
                    var members = group.TryGetValue("members", out var rawMembers) && rawMembers is List<object?> list ? list : null;
                    if (members is null || !members.OfType<string>().Contains(parentId, StringComparer.Ordinal)) continue;
                    if (!members.OfType<string>().Contains(id, StringComparer.Ordinal)) members.Add(id);
                    joined++;
                }
                if (joined == 0) throw new PTreeException("parent 不在任何组里，无法自动继承组归属");
                if (!string.IsNullOrWhiteSpace(groupId))
                {
                    var target = groups.OfType<RawMap>().FirstOrDefault(group => StringOf(group, "id") == groupId);
                    if (target is null) throw new PTreeException("功能块不存在：" + groupId);
                    var members = target.TryGetValue("members", out var rawMembers) && rawMembers is List<object?> list
                        ? list : new List<object?>();
                    if (!members.OfType<string>().Contains(id, StringComparer.Ordinal)) members.Add(id);
                    target.Set("members", members);
                }
                EnsureList(manifest, "nodes").Add(node);
            },
            contracts => SyncContractMembership(contracts, id,
                new List<object?>(), new List<object?>(), inputRefs, outputRefs),
            new Dictionary<string, string?>(StringComparer.Ordinal) { [id] = intro },
            summaries);

        if (outcome.Ok && shortIntro is not null)
        {
            var shortPath = Path.Combine(repository.Root, "00-程序树", "短介绍", id + ".txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(shortPath)!);
                File.WriteAllText(shortPath, shortIntro + "\n", new System.Text.UTF8Encoding(false));
                outcome.Files.Add("00-程序树/短介绍/" + id + ".txt");
            }
            catch (Exception exception)
            {
                return Fail(new[] { "写短介绍失败：" + exception.Message }, repository);
            }
        }
        return Result(repository, outcome, id, repository.Manifest.TreeVersion, new Dictionary<string, object?> { ["node"] = JsonValue(node) });
    }

    public static string AddLink(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var from = StringArg(args, "from");
        var to = StringArg(args, "to");
        var type = StringArg(args, "type");
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(from)) errors.Add("from 不能为空");
        if (string.IsNullOrWhiteSpace(to)) errors.Add("to 不能为空");
        if (type is null || !LinkTypes.Contains(type, StringComparer.Ordinal))
            errors.Add("type 必须是 " + LinkKinds.Written + " 之一");
        if (errors.Count > 0) return Fail(errors, repository);
        if (!repository.NodeMap.ContainsKey(from!)) errors.Add("from 节点不存在：" + from);
        if (!repository.NodeMap.ContainsKey(to!)) errors.Add("to 节点不存在：" + to);
        if (from == to) errors.Add("不允许自连接：" + from);
        if (errors.Count > 0) return Fail(errors, repository);
        if (Links(repository.ManifestRaw).Any(link =>
                StringOf(link, "from") == from && StringOf(link, "to") == to && StringOf(link, "type") == type))
            return Fail(new[] { "重复连接：" + from + " -[" + type + "]-> " + to }, repository);

        var edge = new RawMap();
        edge.Set("from", from);
        edge.Set("to", to);
        edge.Set("type", type);
        if (!string.IsNullOrWhiteSpace(StringArg(args, "note"))) edge.Set("note", StringArg(args, "note"));
        edge.Set("created_at", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var summaries = new List<string> { "link " + from + " -[" + type + "]-> " + to };
        var outcome = Commit(repository, "mcp:tree_add_link", "MCP 新增连接 " + from + " -> " + to,
            manifest => EnsureList(manifest, "links").Add(edge), null, new Dictionary<string, string?>(), summaries);
        return Result(repository, outcome, null, repository.Manifest.TreeVersion,
            new Dictionary<string, object?> { ["edge"] = JsonValue(edge) });
    }


    /// <summary>
    /// 新建功能块（打组）：把多个节点并成一个有名字、有简介、有详细介绍的块。
    /// 组 id 为 group.&lt;slug&gt;，成员可以是节点或别的组；一个节点可以进多个块。
    /// </summary>
    public static string AddGroup(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var name = StringArg(args, "name");
        var slug = StringArg(args, "slug");
        var description = StringArg(args, "description");
        var parent = StringArg(args, "parent");
        var intro = StringArg(args, "intro");
        var members = ListArg(args, "nodes") ?? ListArg(args, "members");
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add("name 不能为空：功能块要有名字");
        if (members is null || members.Count == 0) errors.Add("nodes 不能为空：功能块至少并一个节点");
        if (errors.Count > 0) return Fail(errors, repository);

        if (string.IsNullOrWhiteSpace(slug)) slug = Slugify(name!);
        if (string.IsNullOrWhiteSpace(slug)) slug = "block-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var id = "group." + slug;
        if (repository.Groups.Any(group => string.Equals(group.Id, id, StringComparison.Ordinal)))
            return Fail(new[] { "功能块已存在：" + id }, repository);
        if (repository.NodeMap.ContainsKey(id)) return Fail(new[] { "节点已占用该 id：" + id }, repository);

        foreach (var member in members!.OfType<string>().Select(value => value.Trim()))
        {
            if (member.Length == 0) continue;
            if (!repository.NodeMap.ContainsKey(member) && !repository.Groups.Any(group => group.Id == member))
                errors.Add("成员不存在：" + member);
        }
        if (!string.IsNullOrWhiteSpace(parent) && !repository.Groups.Any(group => group.Id == parent))
            errors.Add("parent 组不存在：" + parent);
        if (errors.Count > 0) return Fail(errors, repository);

        var group = new RawMap();
        group.Set("id", id);
        group.Set("name", name);
        group.Set("description", string.IsNullOrWhiteSpace(description) ? name : description);
        group.Set("parent", string.IsNullOrWhiteSpace(parent) ? null : parent);
        group.Set("members", members!.OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct().Select(value => (object?)value).ToList());
        group.Set("created_at", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var summaries = new List<string> { "group " + id + "：" + string.Join(",", members!) };
        var outcome = Commit(repository, "mcp:tree_add_group", "MCP 新建功能块 " + id,
            manifest =>
            {
                var groups = EnsureList(manifest, "groups");
                groups.Add(group);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    foreach (var item in groups.OfType<RawMap>())
                    {
                        if (StringOf(item, "id") != parent) continue;
                        var children = item.TryGetValue("members", out var raw) && raw is List<object?> list
                            ? list : new List<object?>();
                        if (!children.Contains(id)) children.Add(id);
                        item.Set("members", children);
                    }
                }
            },
            null,
            intro is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(StringComparer.Ordinal) { [id] = intro },
            summaries);
        return Result(repository, outcome, id, repository.Manifest.TreeVersion,
            new Dictionary<string, object?> { ["group"] = JsonValue(group) });
    }

    /// <summary>改功能块：名字、一行简介、父组、成员（整体替换或增删）、详细介绍。</summary>
    public static string UpdateGroup(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var id = StringArg(args, "id");
        if (string.IsNullOrWhiteSpace(id)) return Fail(new[] { "id 不能为空" }, repository);
        var current = repository.Groups.FirstOrDefault(group => string.Equals(group.Id, id, StringComparison.Ordinal));
        if (current is null) return Fail(new[] { "功能块不存在：" + id }, repository);

        var name = StringArg(args, "name");
        var description = StringArg(args, "description");
        var parent = StringArg(args, "parent");
        var intro = StringArg(args, "intro");
        var members = ListArg(args, "members");
        var addMembers = (ListArg(args, "add_members") ?? new List<object?>()).OfType<string>().ToList();
        var removeMembers = (ListArg(args, "remove_members") ?? new List<object?>()).OfType<string>().ToList();
        if (name is null && description is null && parent is null && intro is null &&
            members is null && addMembers.Count == 0 && removeMembers.Count == 0)
            return Fail(new[] { "没有传入任何可更新字段" }, repository);

        var nextMembers = members is null ? current.Members.ToList() : members.OfType<string>().ToList();
        foreach (var member in addMembers.Where(value => !string.IsNullOrWhiteSpace(value)))
            if (!nextMembers.Contains(member)) nextMembers.Add(member);
        foreach (var member in removeMembers) nextMembers.Remove(member);
        nextMembers = nextMembers.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();
        var errors = new List<string>();
        foreach (var member in nextMembers)
        {
            if (!repository.NodeMap.ContainsKey(member) && !repository.Groups.Any(group => group.Id == member))
                errors.Add("成员不存在：" + member);
        }
        if (nextMembers.Count == 0) errors.Add("功能块至少留一个成员");
        if (!string.IsNullOrWhiteSpace(parent))
        {
            if (parent == id) errors.Add("父组不能是自己");
            else if (!repository.Groups.Any(group => group.Id == parent)) errors.Add("parent 组不存在：" + parent);
            else
            {
                var cursor = parent;
                for (var hop = 0; hop < 8 && !string.IsNullOrWhiteSpace(cursor); hop++)
                {
                    if (cursor == id) { errors.Add("父组成环：" + parent); break; }
                    cursor = repository.Groups.FirstOrDefault(group => group.Id == cursor)?.Parent;
                }
            }
        }
        if (errors.Count > 0) return Fail(errors, repository);

        var diffs = new List<string>();
        var summaries = new List<string> { "update group " + id };
        var outcome = Commit(repository, "mcp:tree_update_group", "MCP 更新功能块 " + id,
            manifest =>
            {
                var target = EnsureList(manifest, "groups").OfType<RawMap>()
                    .FirstOrDefault(item => StringOf(item, "id") == id);
                if (target is null) throw new PTreeException("功能块不存在：" + id);
                void Apply(string key, object? value)
                {
                    var before = target.TryGetValue(key, out var existing) ? existing : null;
                    if (ValuesEqual(before, value)) return;
                    target.Set(key, value);
                    diffs.Add(key + "：" + Brief(before) + " -> " + Brief(value));
                }
                if (name is not null) Apply("name", name);
                if (description is not null) Apply("description", description);
                if (parent is not null) Apply("parent", string.IsNullOrWhiteSpace(parent) ? null : parent);
                if (members is not null || addMembers.Count > 0 || removeMembers.Count > 0)
                    Apply("members", nextMembers.Select(value => (object?)value).ToList());
            },
            null,
            intro is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(StringComparer.Ordinal) { [id!] = intro },
            summaries);
        return Result(repository, outcome, id, repository.Manifest.TreeVersion,
            new Dictionary<string, object?> { ["diff"] = diffs });
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

    public static string UpdateNode(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var id = StringArg(args, "id");
        if (string.IsNullOrWhiteSpace(id)) return Fail(new[] { "id 不能为空" }, repository);
        if (!repository.NodeMap.TryGetValue(id!, out _))
        {
            // 网格条目（D01、M01、MA0、U0、F01…）不是节点，但同样要能被写：只开 short_intro 一个口子
            var gridResult = TryUpdateGridShortIntro(repository, id!, args);
            if (gridResult is not null) return gridResult;
            return Fail(new[] { "节点不存在：" + id }, repository);
        }
        var raw = FindNode(repository.ManifestRaw, id!);
        if (raw is null) return Fail(new[] { "节点不存在：" + id }, repository);

        var allowed = new[] { "name", "responsibility", "not_responsible", "inputs", "outputs", "depends_on", "files", "acceptance", "status", "phase", "spec", "evaluation", "preset", "preset_outputs", "evidence", "notes" };
        var updates = new List<(string Key, object? Value)>();
        foreach (var key in allowed)
        {
            if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(key, out var property)) continue;
            if (key == "evidence")
            {
                // 证据是对象数组：test / at / result / note
                var entries = new List<object?>();
                if (property.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var map = new RawMap();
                        foreach (var field in item.EnumerateObject())
                        {
                            map.Set(field.Name, field.Value.ValueKind switch
                            {
                                JsonValueKind.String => field.Value.GetString(),
                                JsonValueKind.Null => null,
                                _ => field.Value.GetRawText(),
                            });
                        }
                        entries.Add(map);
                    }
                }
                updates.Add((key, entries));
                continue;
            }
            object? newValue = property.ValueKind switch
            {
                JsonValueKind.Array => property.EnumerateArray().Select(item => (object?)item.GetString()).Where(item => item is not null).ToList(),
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => property.GetRawText(),
            };
            updates.Add((key, newValue));
        }
        var intro = StringArg(args, "intro");
        var shortIntro = StringArg(args, "short_intro");
        if (updates.Count == 0 && intro is null && shortIntro is null)
            return Fail(new[] { "没有传入任何可更新字段" }, repository);
        if (shortIntro is not null)
        {
            var oneLine = shortIntro.Replace("\r", " ").Replace("\n", " ").Trim();
            var compactLength = oneLine.Count(ch => !char.IsWhiteSpace(ch));
            if (compactLength == 0) return Fail(new[] { "short_intro 不能为空" }, repository);
            if (compactLength > 80)
                return Fail(new[] { "short_intro 去空白后不能超过 80 字（实际 " + compactLength + " 字）" }, repository);
            if (oneLine.Contains("待补", StringComparison.Ordinal) ||
                oneLine.Contains("TODO", StringComparison.OrdinalIgnoreCase) ||
                oneLine.Contains("TBD", StringComparison.OrdinalIgnoreCase))
                return Fail(new[] { "short_intro 不得含占位词（待补 / TODO / TBD）" }, repository);
            shortIntro = oneLine;
        }

        var presetBefore = raw.TryGetValue("preset", out var presetRaw) && presetRaw is true;
        var presetUpdate = updates.FirstOrDefault(update => update.Key == "preset");
        var presetAfter = updates.Any(update => update.Key == "preset") ? presetUpdate.Value is true : presetBefore;
        var filesUpdate = updates.FirstOrDefault(update => update.Key == "files");
        var filesAfter = updates.Any(update => update.Key == "files") && filesUpdate.Value is List<object?> incomingFiles
            ? incomingFiles : ListOf(raw, "files");
        if (presetAfter && filesAfter.Count > 0)
            return Fail(new[] { "preset 占位节点不能带 files；转正时请同时传 preset=false" }, repository);
        if (presetBefore && !presetAfter && filesAfter.Count > 0)
        {
            // 明确去掉 preset 后允许转正
        }
        var statusUpdate = updates.FirstOrDefault(update => update.Key == "status");
        if (presetAfter && updates.Any(update => update.Key == "status") &&
            !string.Equals(statusUpdate.Value as string, "未完成", StringComparison.Ordinal))
            return Fail(new[] { "preset 占位节点状态必须为未完成" }, repository);

        var diffs = new List<string>();
        var summaries = new List<string> { "update " + id };
        var oldInputs = ListOf(raw, "inputs");
        var oldOutputs = ListOf(raw, "outputs");
        var newInputs = updates.Any(update => update.Key == "inputs")
            ? updates.First(update => update.Key == "inputs").Value as List<object?> ?? new List<object?>()
            : oldInputs;
        var newOutputs = updates.Any(update => update.Key == "outputs")
            ? updates.First(update => update.Key == "outputs").Value as List<object?> ?? new List<object?>()
            : oldOutputs;
        var contractsChanged = updates.Any(update => update.Key is "inputs" or "outputs");
        Action<RawMap>? contractsMutation = contractsChanged
            ? contracts => SyncContractMembership(contracts, id!, oldInputs, oldOutputs, newInputs, newOutputs)
            : null;
        var guardPaths = new List<string> { "00-程序树/program-tree.yaml" };
        if (intro is not null) guardPaths.Add("00-程序树/说明/" + id + ".txt");
        if (shortIntro is not null) guardPaths.Add("00-程序树/短介绍/" + id + ".txt");
        var guardMessage = Coord.Guard(repository.Root, guardPaths, Coord.AgentOf(args));
        if (guardMessage is not null) return Fail(new[] { "写盘被协作守门拦下：" + guardMessage }, repository);

        var outcome = Commit(repository, "mcp:tree_update_node", "MCP 更新节点 " + id,
            manifest =>
            {
                var target = FindNode(manifest, id!);
                if (target is null) throw new PTreeException("节点不存在：" + id);
                foreach (var (key, newValue) in updates)
                {
                    var oldValue = target.TryGetValue(key, out var existing) ? existing : null;
                    if (ValuesEqual(oldValue, newValue)) continue;
                    if (key == "preset" && newValue is false)
                    {
                        target.Remove("preset");
                        target.Remove("preset_outputs");
                    }
                    else
                    {
                        target.Set(key, newValue);
                    }
                    diffs.Add(key + "：" + Brief(oldValue) + " -> " + Brief(newValue));
                }
                if (target.TryGetValue("preset", out var finalPreset) && finalPreset is true)
                {
                    target.Set("status", "未完成");
                    target.Set("files", new List<object?>());
                }
                target.Set("updated_at", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            },
            contractsMutation,
            intro is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(StringComparer.Ordinal) { [id!] = intro },
            summaries);
        if (outcome.Ok && shortIntro is not null)
        {
            // 短介绍是独立小文件，校验已在上面预做过；落盘失败照样报错，不让它半成功
            var shortPath = Path.Combine(repository.Root, "00-程序树", "短介绍", id + ".txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(shortPath)!);
                File.WriteAllText(shortPath, shortIntro + "\n", new System.Text.UTF8Encoding(false));
                outcome.Files.Add("00-程序树/短介绍/" + id + ".txt");
            }
            catch (Exception exception)
            {
                return Fail(new[] { "写短介绍失败：" + exception.Message }, repository);
            }
        }
        return Result(repository, outcome, id, repository.Manifest.TreeVersion,
            new Dictionary<string, object?> { ["diff"] = diffs });
    }

    /// <summary>
    /// 网格条目写入口：只接受 short_intro 一个字段，写 00-程序树/短介绍/&lt;编号&gt;.txt。
    /// 与节点同一口径校验；不是网格条目时返回 null，交给调用方按「节点不存在」处理。
    /// </summary>
    private static string? TryUpdateGridShortIntro(PTreeRepository repository, string id, JsonElement? args)
    {
        var items = GridReadings.Items(TreeStore.Load(repository.Root), repository.Root)
            .Where(item => string.Equals(item.Id, id, StringComparison.Ordinal)).ToList();
        if (items.Count == 0) return null;
        if (items.Count > 1)
        {
            return Fail(new[]
            {
                "网格条目编号不唯一：" + id + " 同时出现在 " +
                string.Join("、", items.Select(item => item.Kind + "(" + item.RelPath + ")").Distinct(StringComparer.Ordinal)),
            }, repository);
        }

        var gridKeys = new HashSet<string>(StringComparer.Ordinal) { "id", "short_intro" };
        var extraKeys = new List<string>();
        if (args is { ValueKind: JsonValueKind.Object } value)
        {
            foreach (var property in value.EnumerateObject())
                if (!gridKeys.Contains(property.Name)) extraKeys.Add(property.Name);
        }
        if (extraKeys.Count > 0)
        {
            return Fail(new[]
            {
                "网格条目只有 short_intro 可写，字段用文件维护（收到：" + string.Join("、", extraKeys) + "）",
            }, repository);
        }

        var shortIntro = NormalizeShortIntro(StringArg(args, "short_intro"), out var shortIntroError);
        if (shortIntroError is not null) return Fail(new[] { shortIntroError }, repository);
        if (shortIntro is null) return Fail(new[] { "没有传入任何可更新字段：网格条目只支持 short_intro" }, repository);

        var gridGuard = Coord.Guard(repository.Root, new[] { GridReadings.ShortRel(id) }, Coord.AgentOf(args));
        if (gridGuard is not null) return Fail(new[] { "写盘被协作守门拦下：" + gridGuard }, repository);

        var oldText = NodeCards.ReadShortIntro(repository, id);
        var path = GridReadings.ShortPath(repository.Root, id);
        var rel = GridReadings.ShortRel(id);
        string? temp = null;
        try
        {
            // 先写临时文件再原子替换：失败不留半截短介绍
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, shortIntro + "\n", new System.Text.UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        catch (Exception exception)
        {
            try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            return Fail(new[] { "写短介绍失败：" + exception.Message }, repository);
        }

        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["id"] = id,
            ["tree_version"] = repository.Manifest.TreeVersion,
            ["errors"] = new List<object?>(),
            ["written_files"] = new List<object?> { rel },
            ["ledger"] = null,
            ["diff"] = new List<object?> { "short_intro：" + Brief(oldText) + " -> " + Brief(shortIntro) },
        });
    }

    public static string RetireNode(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var id = StringArg(args, "id");
        var reason = StringArg(args, "reason");
        var replacement = StringArg(args, "replacement");
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(id)) errors.Add("id 不能为空");
        if (string.IsNullOrWhiteSpace(reason)) errors.Add("reason 不能为空：退役必须留理由");
        if (errors.Count > 0) return Fail(errors, repository);
        if (!repository.NodeMap.ContainsKey(id!)) return Fail(new[] { "节点不存在：" + id }, repository);
        if (!string.IsNullOrWhiteSpace(replacement) && !repository.NodeMap.ContainsKey(replacement!))
            return Fail(new[] { "replacement 节点不存在：" + replacement }, repository);

        var summaries = new List<string> { "retire " + id + "：" + reason + (string.IsNullOrWhiteSpace(replacement) ? "" : " -> " + replacement) };
        var outcome = Commit(repository, "mcp:tree_retire_node", "MCP 退役节点 " + id,
            manifest =>
            {
                var target = FindNode(manifest, id!);
                if (target is null) throw new PTreeException("节点不存在：" + id);
                target.Set("status", "退役");
                target.Set("legacy_reason", reason);
                if (!string.IsNullOrWhiteSpace(replacement)) target.Set("replaced_by", replacement);
                target.Set("authority", null);
                target.Set("updated_at", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            },
            null,
            new Dictionary<string, string?>(), summaries);
        return Result(repository, outcome, id, repository.Manifest.TreeVersion,
            new Dictionary<string, object?> { ["status"] = "退役", ["reason"] = reason, ["replacement"] = replacement });
    }

    /// <summary>tree_guard：一次常检 = HealthCheck + validate 计数；默认一张 ≤10 行卡。</summary>
    public static string Guard(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var format = (StringArg(args, "format") ?? "card").Trim().ToLowerInvariant();
        if (format is not ("card" or "full"))
            return Fail(new[] { "format 只能是 card（默认纯文本卡）或 full（原 JSON）" }, repository);
        var limit = IntArg(args, "limit") ?? HealthCheck.DefaultGuardLines;
        if (limit <= 0) limit = HealthCheck.DefaultGuardLines;
        var report = HealthCheck.Run(repository);
        var validation = Validator.Validate(repository);
        if (format == "card") return HealthCheck.RenderGuardCard(repository, report, validation, limit);
        var payload = HealthCheck.ToPayload(report);
        payload["tree_version"] = repository.Manifest.TreeVersion;
        payload["validation"] = new Dictionary<string, object?>
        {
            ["ok"] = validation.Ok,
            ["errors"] = validation.Errors.Count,
            ["warnings"] = validation.Warnings.Count,
        };
        return JsonOutput.Serialize(payload);
    }

    /// <summary>tree_relocate：把节点登记的代码路径迁到新位置（可选同时换 parent）；校验新路径存在、parent 存在且不成环。</summary>
    public static string Relocate(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var id = StringArg(args, "id");
        var reason = StringArg(args, "reason");
        var parentArg = StringArg(args, "parent");
        var rawFiles = ListArg(args, "files");
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(id)) errors.Add("id 不能为空");
        var files = rawFiles?.OfType<string>().Select(trim => trim.Trim())
            .Where(trim => trim.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (files is null || files.Count == 0) errors.Add("files 至少 1 条：迁移必须给新的登记路径");
        if (errors.Count > 0) return Fail(errors, repository);
        if (!repository.NodeMap.TryGetValue(id!, out var node)) return Fail(new[] { "节点不存在：" + id }, repository);
        foreach (var file in files!) if (!HealthCheck.Exists(repository.Root, file)) errors.Add("新路径不存在：" + file);

        var oldParent = node.Parent;
        var newParent = string.IsNullOrWhiteSpace(parentArg) ? null : parentArg!.Trim();
        var parentChanged = newParent is not null && !string.Equals(newParent, oldParent, StringComparison.Ordinal);
        var newLayer = node.Layer ?? 0;
        if (newParent is not null)
        {
            if (node.Kind == "root") errors.Add("根节点不能改 parent：" + id);
            else if (!repository.NodeMap.TryGetValue(newParent, out var parentNode)) errors.Add("新 parent 不存在：" + newParent);
            else if (string.Equals(newParent, id, StringComparison.Ordinal)) errors.Add("新 parent 不能是节点自己：" + newParent);
            else if (IsDescendant(repository, newParent, id!)) errors.Add("新 parent 是它的子节点，会成环：" + newParent);
            else
            {
                var expected = parentNode.Kind switch
                {
                    "root" => new[] { "department", "external" },
                    "department" => new[] { "program", "external" },
                    "program" => new[] { "step", "external" },
                    _ => Array.Empty<string>(),
                };
                if (expected.Length == 0 || !expected.Contains(node.Kind ?? "", StringComparer.Ordinal))
                    errors.Add("parent（" + parentNode.Kind + "）下不能挂 kind=" + node.Kind);
                else newLayer = (parentNode.Layer ?? 0) + 1;
            }
        }
        if (errors.Count > 0) return Fail(errors, repository);

        var oldFiles = node.Files.ToList();
        var diffs = DiffPaths(oldFiles, files!);
        if (parentChanged) diffs.Add("parent：" + (oldParent ?? "∅") + " -> " + newParent);
        var summaries = new List<string>
        {
            "relocate " + id + "：" + string.Join(",", oldFiles) + " -> " + string.Join(",", files!),
        };
        var outcome = Commit(repository, "mcp:tree_relocate",
            "MCP 迁移节点代码路径 " + id + (string.IsNullOrWhiteSpace(reason) ? "" : "：" + reason),
            manifest =>
            {
                var target = FindNode(manifest, id!);
                if (target is null) throw new PTreeException("节点不存在：" + id);
                target.Set("files", files!.Cast<object?>().ToList());
                if (parentChanged)
                {
                    target.Set("parent", newParent);
                    target.Set("layer", newLayer);
                    var depends = ListOf(target, "depends_on");
                    var replaced = false;
                    for (var index = 0; index < depends.Count; index++)
                    {
                        if (depends[index] is string dependency && string.Equals(dependency, oldParent, StringComparison.Ordinal))
                        {
                            depends[index] = newParent;
                            replaced = true;
                        }
                    }
                    if (replaced) target.Set("depends_on", depends);
                }
                target.Set("updated_at", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            },
            null,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            summaries);
        return Result(repository, outcome, id, repository.Manifest.TreeVersion, new Dictionary<string, object?>
        {
            ["diff"] = diffs,
            ["old_files"] = oldFiles,
            ["files"] = files,
            ["parent"] = newParent,
            ["reason"] = reason,
        });
    }

    /// <summary>tree_delete：彻底删除一个节点（节点段、组、links、契约、台账）；有子节点或被依赖时拒绝。</summary>
    public static string Delete(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var id = StringArg(args, "id");
        var reason = StringArg(args, "reason")?.Trim();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(id)) errors.Add("id 不能为空");
        if (string.IsNullOrWhiteSpace(reason) || reason!.Length < 8)
            errors.Add("reason 至少 8 个字：删除不可恢复，必须写清原因");
        if (errors.Count > 0) return Fail(errors, repository);
        if (!repository.NodeMap.TryGetValue(id!, out var node)) return Fail(new[] { "节点不存在：" + id }, repository);

        var children = repository.Nodes
            .Where(other => other.Id is { Length: > 0 } && string.Equals(other.Parent, id, StringComparison.Ordinal))
            .Select(other => other.Id!).OrderBy(value => value, StringComparer.Ordinal).ToList();
        if (children.Count > 0)
        {
            return Fail(new[] { "有子节点，拒绝删除（先迁移或删除子节点）：" + string.Join("、", children) }
                .Concat(children.Select(child => "子节点：" + child)), repository);
        }
        var dependents = repository.Nodes
            .Where(other => other.Id is { Length: > 0 } otherId && otherId != id &&
                            other.DependsOn.Contains(id!, StringComparer.Ordinal))
            .Select(other => other.Id!).OrderBy(value => value, StringComparer.Ordinal).ToList();
        if (dependents.Count > 0)
        {
            return Fail(new[] { "有节点 depends_on 它，拒绝删除（先改依赖）：" + string.Join("、", dependents) }
                .Concat(dependents.Select(dependent => "依赖它：" + dependent)), repository);
        }

        var removedFieldCount = 0;
        var removedName = node.Name;
        var removedKind = node.Kind;
        var removedStatus = node.Status;
        var removedMembers = 0;
        var removedLinks = 0;
        var contractsTouched = new List<string>();
        var contractsRemoved = new List<string>();
        var summaries = new List<string> { "delete " + id + "：" + reason };
        var outcome = Commit(repository, "mcp:tree_delete", "MCP 删除节点 " + id + "：" + reason,
            manifest =>
            {
                var nodes = EnsureList(manifest, "nodes");
                var index = nodes.FindIndex(item => item is RawMap map && StringOf(map, "id") == id);
                if (index < 0) throw new PTreeException("节点不存在：" + id);
                if (nodes[index] is RawMap removed)
                {
                    removedFieldCount = removed.Count;
                    removedName = StringOf(removed, "name") ?? removedName;
                    removedKind = StringOf(removed, "kind") ?? removedKind;
                    removedStatus = StringOf(removed, "status") ?? removedStatus;
                }
                nodes.RemoveAt(index);
                foreach (var group in EnsureList(manifest, "groups").OfType<RawMap>())
                {
                    removedMembers += EnsureList2(group, "members")
                        .RemoveAll(member => member is string text && string.Equals(text, id, StringComparison.Ordinal));
                }
                removedLinks = EnsureList(manifest, "links").RemoveAll(item => item is RawMap link &&
                    (StringOf(link, "from") == id || StringOf(link, "to") == id));
            },
            contracts =>
            {
                var items = ListOf(contracts, "contracts");
                foreach (var contract in items.OfType<RawMap>())
                {
                    var contractId = StringOf(contract, "id");
                    if (contractId is null) continue;
                    var touched = 0;
                    foreach (var field in new[] { "producers", "consumers" })
                    {
                        if (!contract.TryGetValue(field, out var rawValues) || rawValues is not List<object?> values) continue;
                        touched += values.RemoveAll(value => value is string text && string.Equals(text, id, StringComparison.Ordinal));
                    }
                    if (touched > 0) contractsTouched.Add(contractId);
                }
                for (var index = items.Count - 1; index >= 0; index--)
                {
                    if (items[index] is not RawMap contract) continue;
                    var contractId = StringOf(contract, "id");
                    if (contractId is null || !contractsTouched.Contains(contractId, StringComparer.Ordinal)) continue;
                    if (!IsEmptyContract(contract)) continue;
                    contractsRemoved.Add(contractId);
                    items.RemoveAt(index);
                }
            },
            new Dictionary<string, string?>(StringComparer.Ordinal) { [id!] = null },
            summaries);
        var removedFiles = node.Files.ToList();
        return Result(repository, outcome, id, repository.Manifest.TreeVersion, new Dictionary<string, object?>
        {
            ["deleted"] = new Dictionary<string, object?>
            {
                ["node"] = id,
                ["name"] = removedName,
                ["kind"] = removedKind,
                ["status"] = removedStatus,
                ["field_count"] = removedFieldCount,
                ["files"] = removedFiles,
                ["links_removed"] = removedLinks,
                ["group_members_removed"] = removedMembers,
                ["contracts_touched"] = contractsTouched,
                ["contracts_removed"] = contractsRemoved,
            },
            ["summary"] = "删除节点 " + id + "（" + removedKind + "，" + removedStatus + "）：字段 " + removedFieldCount +
                         " 条，links " + removedLinks + " 条，契约 " + contractsTouched.Count + " 个（移除空契约 " +
                         contractsRemoved.Count + " 个），组归属 " + removedMembers + " 处",
        });
    }

    private static bool IsDescendant(PTreeRepository repository, string candidate, string ancestor)
    {
        var current = candidate;
        var guard = 0;
        while (!string.IsNullOrEmpty(current) && guard++ < 200)
        {
            if (string.Equals(current, ancestor, StringComparison.Ordinal)) return true;
            if (!repository.NodeMap.TryGetValue(current, out var node)) return false;
            current = node.Parent ?? "";
        }
        return false;
    }

    private static List<string> DiffPaths(List<string> before, List<string> after)
    {
        if (before.Count == after.Count)
        {
            var lines = new List<string>();
            for (var index = 0; index < before.Count; index++)
            {
                if (!string.Equals(before[index], after[index], StringComparison.Ordinal))
                    lines.Add(before[index] + " -> " + after[index]);
            }
            if (lines.Count == 0) lines.Add("files 未变化：" + string.Join(",", after));
            return lines;
        }
        var changed = before.Where(old => !after.Contains(old, StringComparer.Ordinal))
            .Select(old => old + " -> （移除）").ToList();
        changed.AddRange(after.Where(fresh => !before.Contains(fresh, StringComparer.Ordinal))
            .Select(fresh => "（新增） -> " + fresh));
        return changed;
    }

    private static bool IsEmptyContract(RawMap contract)
    {
        var producers = ListOf(contract, "producers").Count;
        var consumers = ListOf(contract, "consumers").Count;
        var external = contract.TryGetValue("external", out var ext) && ext is true;
        var externalConsumers = contract.TryGetValue("external_consumers", out var extConsumers) && extConsumers is true;
        return (producers == 0 && !external) || (consumers == 0 && !externalConsumers);
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

    public static string GitSync(string? root, IEnumerable<string>? files, string? message)
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["written_files"] = files?.ToList() ?? new List<string>(),
            ["message"] = message,
        });
        return GitSync(root, payload);
    }

    public static string GitSync(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var rawFiles = ListArg(args, "written_files");
        if (rawFiles is null || rawFiles.Count == 0)
            return GitFail(repository, "written_files 不能为空：请传本次写操作返回的文件路径");
        var files = new List<string>();
        foreach (var raw in rawFiles.OfType<string>())
        {
            var normalized = raw.Replace('\\', '/').Trim();
            if (normalized.Length == 0 || Path.IsPathRooted(normalized)) return GitFail(repository, "written_files 必须是仓库内相对路径：" + raw);
            var absolute = Path.GetFullPath(Path.Combine(repository.Root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(repository.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return GitFail(repository, "written_files 越出仓库：" + raw);
            var relative = Path.GetRelativePath(repository.Root, absolute).Replace('\\', '/');
            if (!files.Contains(relative, StringComparer.Ordinal)) files.Add(relative);
        }
        var message = StringArg(args, "message");
        var ledger = LatestLedger(repository.Root);
        if (string.IsNullOrWhiteSpace(message))
        {
            if (ledger is null) return GitFail(repository, "缺少 commit message，且台账没有可用的 chg 条目");
            message = ledger.Value.Id + " " + ledger.Value.Summary;
        }
        var inside = RunGit(repository.Root, "rev-parse", "--is-inside-work-tree");
        if (inside.ExitCode != 0 || !inside.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            return GitFail(repository, "git 不可用或不在仓库内");
        var add = RunGit(repository.Root, new[] { "add", "--all", "--" }.Concat(files));
        if (add.ExitCode != 0) return GitFail(repository, "git add 失败：" + FirstLine(add.Stderr));
        var diffArgs = new[] { "diff", "--cached", "--quiet", "--" }.Concat(files).ToArray();
        var diff = RunGit(repository.Root, diffArgs);
        if (diff.ExitCode == 0)
        {
            return JsonOutput.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["committed"] = false,
                ["message"] = message,
                ["files"] = files,
                ["groups"] = GroupFiles(files),
                ["ledger"] = ledger?.Id,
                ["note"] = "没有可提交的改动，未产生空提交",
            });
        }
        if (diff.ExitCode != 1) return GitFail(repository, "git diff 失败：" + FirstLine(diff.Stderr));
        var commit = RunGit(repository.Root, new[] { "commit", "-m", message, "--" }.Concat(files));
        if (commit.ExitCode != 0) return GitFail(repository, "git commit 失败：" + FirstLine(commit.Stderr));
        var head = RunGit(repository.Root, "rev-parse", "--short", "HEAD");
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["committed"] = true,
            ["message"] = message,
            ["commit"] = head.ExitCode == 0 ? head.Stdout.Trim() : null,
            ["files"] = files,
            ["groups"] = GroupFiles(files),
            ["ledger"] = ledger?.Id,
        });
    }

    private static string GitFail(PTreeRepository repository, string error) => JsonOutput.Serialize(new Dictionary<string, object?>
    {
        ["ok"] = false,
        ["committed"] = false,
        ["error"] = error,
        ["tree_version"] = repository.Manifest.TreeVersion,
        ["files"] = new List<string>(),
    });

    private static Dictionary<string, List<string>> GroupFiles(List<string> files) =>
        files.GroupBy(path => path.Contains('/') ? path[..path.LastIndexOf('/')] : ".", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    private static (string Id, string Summary)? LatestLedger(string root)
    {
        var path = Path.Combine(root, "00-程序树", "变更", "台账.jsonl");
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            try
            {
                using var document = JsonDocument.Parse(lines[index]);
                var element = document.RootElement;
                if (element.ValueKind != JsonValueKind.Object) continue;
                var id = element.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var summary = element.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
                if (string.IsNullOrWhiteSpace(summary) && element.TryGetProperty("ops", out var ops) && ops.ValueKind == JsonValueKind.Array)
                    summary = string.Join("；", ops.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)));
                if (summary is { Length: > 80 }) summary = summary[..80];
                return (id, summary ?? "树改动");
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunGit(string root, params string[] arguments) =>
        RunGit(root, (IEnumerable<string>)arguments);

    private static (int ExitCode, string Stdout, string Stderr) RunGit(string root, IEnumerable<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(root);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return (-1, "", "无法启动 git");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception exception)
        {
            return (-1, "", exception.Message);
        }
    }

    private static string FirstLine(string text) => text.Split('\n').FirstOrDefault()?.Trim() ?? text;

    // ------------------------------------------------------------------
    // 写盘、校验、台账、重生成
    // ------------------------------------------------------------------

    /// <summary>AddNode 与 UpdateNode 共用：让节点 inputs/outputs 与契约 consumers/producers 双向一致。</summary>
    private static void SyncContractMembership(RawMap contracts, string nodeId,
        IReadOnlyList<object?> oldInputs, IReadOnlyList<object?> oldOutputs,
        IReadOnlyList<object?> newInputs, IReadOnlyList<object?> newOutputs)
    {
        static bool Contains(IReadOnlyList<object?> values, string id) =>
            values.OfType<string>().Contains(id, StringComparer.Ordinal);
        static void AddUnique(List<object?> values, string id)
        {
            if (!values.OfType<string>().Contains(id, StringComparer.Ordinal)) values.Add(id);
        }
        static void RemoveAll(List<object?> values, string id) =>
            values.RemoveAll(value => value is string text && string.Equals(text, id, StringComparison.Ordinal));

        foreach (var contract in ListOf(contracts, "contracts").OfType<RawMap>())
        {
            var contractId = StringOf(contract, "id");
            if (contractId is null) continue;
            var producers = EnsureList2(contract, "producers");
            if (Contains(newOutputs, contractId)) AddUnique(producers, nodeId);
            else if (Contains(oldOutputs, contractId)) RemoveAll(producers, nodeId);

            var consumers = EnsureList2(contract, "consumers");
            if (Contains(newInputs, contractId)) AddUnique(consumers, nodeId);
            else if (Contains(oldInputs, contractId)) RemoveAll(consumers, nodeId);
        }
    }

    private sealed record CommitOutcome(bool Ok, List<Dictionary<string, object?>> Errors, string? Error,
        string? TreeVersion, string? LedgerId, List<string> Files);

    private static CommitOutcome Commit(PTreeRepository repository, string actor, string reason,
        Action<RawMap> mutate, Action<RawMap>? contractsMutation, Dictionary<string, string?> introWrites, List<string> summaries)
    {
        var root = repository.Root;
        var manifestPath = Path.Combine(root, PTreeRepository.ManifestRel.Replace('/', Path.DirectorySeparatorChar));
        var contractsPath = repository.ContractsPath;
        var generatedDir = Path.Combine(root, "00-程序树", "生成");
        var ledgerPath = Path.Combine(root, "00-程序树", "变更", "台账.jsonl");
        var generated = new[] { "程序树总览.txt", "落地顺序.txt", "程序书.txt", "标杆看板.txt", BriefRenderer.FileName };
        var backups = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        void Backup(string path) { if (!backups.ContainsKey(path)) backups[path] = File.Exists(path) ? File.ReadAllBytes(path) : null; }
        Backup(manifestPath);
        Backup(contractsPath);
        Backup(ledgerPath);
        foreach (var name in generated) Backup(Path.Combine(generatedDir, name));
        foreach (var id in introWrites.Keys) Backup(Path.Combine(root, PTreeRepository.IntroDirRel.Replace('/', Path.DirectorySeparatorChar), id + PTreeRepository.IntroExtension));

        var manifest = CloneMap(repository.ManifestRaw);
        try
        {
            mutate(manifest);
            manifest.Set("updated", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var versionAfter = BumpVersion(repository.Manifest.TreeVersion);
            manifest.Set("tree_version", versionAfter);
            WriteText(manifestPath, YamlEmitter.Dump(manifest));
            if (contractsMutation is not null && repository.ContractsRaw is RawMap contractsRaw)
            {
                var contracts = CloneMap(contractsRaw);
                contractsMutation(contracts);
                contracts.Set("updated", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                WriteText(contractsPath, YamlEmitter.Dump(contracts));
            }
            foreach (var pair in introWrites)
            {
                var path = Path.Combine(root, PTreeRepository.IntroDirRel.Replace('/', Path.DirectorySeparatorChar), pair.Key + PTreeRepository.IntroExtension);
                if (pair.Value is null) { if (File.Exists(path)) File.Delete(path); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var text = pair.Value.Replace("\r\n", "\n").Replace('\r', '\n');
                    WriteText(path, text.EndsWith('\n') ? text : text + "\n");
                }
            }

            var reloaded = PTreeRepository.Load(root);
            var validation = Validator.Validate(reloaded);
            var store = TreeStore.Load(root);
            var checkIssues = TreeChecker.Check(store);
            checkIssues.AddRange(ManifestChecker.Check(reloaded, store));
            var errors = new List<Dictionary<string, object?>>();
            errors.AddRange(validation.Errors.Select(issue => Issue("E", issue.Code, issue.Node, issue.Message)));
            errors.AddRange(checkIssues.Where(issue => issue.Level == "E").Select(issue => Issue("E", issue.Code, issue.Kind, issue.Message)));
            if (errors.Count > 0)
            {
                Restore(backups);
                return new CommitOutcome(false, errors.Take(50).ToList(), errors[0]["message"] as string, repository.Manifest.TreeVersion, null, new List<string>());
            }

            GeneratedProducts.WriteAll(reloaded);
            WriteText(Path.Combine(generatedDir, BriefRenderer.FileName), BriefRenderer.Render(reloaded));
            var ledgerId = AppendLedger(reloaded, ledgerPath, actor, reason, summaries, validation, repository.Manifest.TreeVersion, versionAfter);
            var written = new List<string> { Path.GetRelativePath(root, manifestPath).Replace('\\', '/') };
            if (contractsMutation is not null) written.Add(Path.GetRelativePath(root, contractsPath).Replace('\\', '/'));
            written.Add("00-程序树/变更/台账.jsonl");
            return new CommitOutcome(true, errors, null, versionAfter, ledgerId,
                written.Concat(
                    new[] { "程序树总览.txt", "落地顺序.txt", "程序书.txt", "标杆看板.txt", BriefRenderer.FileName }
                        .Select(name => "00-程序树/生成/" + name)).ToList());
        }
        catch (Exception exception)
        {
            Restore(backups);
            return new CommitOutcome(false, new List<Dictionary<string, object?>>(), exception.Message, repository.Manifest.TreeVersion, null, new List<string>());
        }
    }

    private static void Restore(Dictionary<string, byte[]?> backups)
    {
        foreach (var pair in backups)
        {
            try
            {
                if (pair.Value is null) { if (File.Exists(pair.Key)) File.Delete(pair.Key); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                    var temp = pair.Key + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllBytes(temp, pair.Value);
                    File.Move(temp, pair.Key, overwrite: true);
                }
            }
            catch { }
        }
    }

    private static string AppendLedger(PTreeRepository repository, string path, string actor, string reason,
        List<string> summaries, ValidationPayload validation, string? versionBefore, string? versionAfter)
    {
        var entries = new List<JsonElement>();
        if (File.Exists(path))
        {
            foreach (var line in PythonText.SplitLines(File.ReadAllText(path)))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { using var document = JsonDocument.Parse(line); entries.Add(document.RootElement.Clone()); } catch { }
            }
        }
        var prefix = "chg-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";
        var maximum = 0;
        foreach (var entry in entries)
        {
            if (entry.TryGetProperty("id", out var id) && id.GetString() is { } text && text.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(text[prefix.Length..], out var number)) maximum = Math.Max(maximum, number);
        }
        var ledgerId = prefix + (maximum + 1).ToString("0000", CultureInfo.InvariantCulture);
        var ledger = new Dictionary<string, object?>
        {
            ["id"] = ledgerId,
            ["at"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["actor"] = actor,
            ["proposal"] = "00-程序树/变更",
            ["reason"] = reason,
            ["ops"] = summaries,
            ["validation"] = new Dictionary<string, int> { ["errors"] = validation.Errors.Count, ["warnings"] = validation.Warnings.Count },
            ["tree_version_before"] = versionBefore,
            ["tree_version_after"] = versionAfter,
            ["result"] = "applied",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(ledger, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n", new UTF8Encoding(false));
        return ledgerId;
    }

    private static string BumpVersion(string? current)
    {
        var parts = (current ?? "1.0.0").Split('.');
        if (parts.Length != 3 || !parts.All(part => int.TryParse(part, out _))) return "1.0.1";
        var patch = int.Parse(parts[2], CultureInfo.InvariantCulture) + 1;
        return parts[0] + "." + parts[1] + "." + patch;
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    // ------------------------------------------------------------------
    // 读取辅助
    // ------------------------------------------------------------------

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

    private static List<object?> EnsureList(RawMap manifest, string key)
    {
        if (manifest.TryGetValue(key, out var raw) && raw is List<object?> list) return list;
        var created = new List<object?>();
        manifest.Set(key, created);
        return created;
    }

    private static List<object?> EnsureList2(RawMap map, string key) => EnsureList(map, key);

    private static List<object?> ListOf(RawMap map, string key) =>
        map.TryGetValue(key, out var value) && value is List<object?> list ? list : new List<object?>();

    private static RawMap CloneMap(RawMap source)
    {
        var clone = new RawMap();
        foreach (var pair in source) clone.Set(pair.Key, CloneValue(pair.Value));
        return clone;
    }

    private static object? CloneValue(object? value) => value switch
    {
        RawMap map => CloneMap(map),
        List<object?> list => list.Select(CloneValue).ToList(),
        _ => value,
    };

    private static object? JsonValue(object? value) => value switch
    {
        RawMap map => map.ToDictionary(pair => pair.Key, pair => JsonValue(pair.Value), StringComparer.Ordinal),
        List<object?> list => list.Select(JsonValue).ToList(),
        _ => value,
    };

    private static Dictionary<string, object?> Issue(string level, string code, string? node, string message) => new()
    {
        ["level"] = level,
        ["code"] = code,
        ["node"] = node,
        ["message"] = message,
    };

    private static string Result(CommitOutcome outcome, string? id, string? currentVersion, Dictionary<string, object?> extra)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = outcome.Ok,
            ["id"] = id,
            ["tree_version"] = outcome.Ok ? outcome.TreeVersion : currentVersion,
            ["errors"] = outcome.Errors,
            ["written_files"] = outcome.Files,
            ["ledger"] = outcome.LedgerId,
        };
        if (outcome.Error is not null) payload["error"] = outcome.Error;
        foreach (var pair in extra) payload[pair.Key] = pair.Value;
        return JsonOutput.Serialize(payload);
    }

    /// <summary>
    /// 写操作收尾：成功且清单声明 auto_commit 时，把本次写盘文件提交成一条 git 记录；
    /// 再把这次 tree_version 快照映射到备份点（备份是附属功能，未启用时什么都不做）。
    /// </summary>
    private static string Result(PTreeRepository repository, CommitOutcome outcome, string? id, string? currentVersion, Dictionary<string, object?> extra)
    {
        var text = Result(outcome, id, currentVersion, extra);
        if (!outcome.Ok || outcome.Files.Count == 0) return text;
        JsonElement? commit = null;
        if (AutoCommitEnabled(repository))
        {
            var commitText = GitSync(repository.Root, outcome.Files, "auto: " + (outcome.LedgerId ?? "tree change"));
            try
            {
                commit = JsonDocument.Parse(commitText).RootElement.Clone();
            }
            catch (JsonException)
            {
                commit = null;
            }
        }
        var backup = BackupService.AfterTreeChange(repository, outcome.TreeVersion ?? repository.Manifest.TreeVersion);
        if (commit is null && backup is null) return text;
        try
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(text);
            if (payload is null) return text;
            if (commit is not null) payload["commit"] = commit.Value;
            if (backup is not null) payload["backup"] = backup;
            return JsonOutput.Serialize(payload);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static bool AutoCommitEnabled(PTreeRepository repository)
    {
        if (!repository.ManifestRaw.TryGetValue("conventions", out var raw) || raw is not RawMap conventions) return false;
        return conventions.TryGetValue("auto_commit", out var flag) && flag is true;
    }

    private static string Fail(IEnumerable<string> errors, PTreeRepository repository)
    {
        var list = errors.ToList();
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = list.FirstOrDefault() ?? "参数错误",
            ["errors"] = list.Select(message => Issue("E", "TOOL", null, message)).ToList(),
            ["tree_version"] = repository.Manifest.TreeVersion,
        });
    }

    private static string Slugify(string name)
    {
        var builder = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(ch);
            else if (ch is ' ' or '_' or '-' or '.') builder.Append('-');
        }
        var slug = Regex.Replace(builder.ToString(), "-+", "-").Trim('-');
        return SlugPattern.IsMatch(slug) ? slug : "";
    }

    private static string? StringArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int? IntArg(JsonElement? args, string name) =>
        int.TryParse(StringArg(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? BoolArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static List<object?>? ListArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Array) return null;
        return property.EnumerateArray().Select(item => item.ValueKind switch
        {
            JsonValueKind.String => (object?)item.GetString(),
            JsonValueKind.Number => item.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        }).Where(item => item is not null).ToList();
    }

    private static string? StringOf(RawMap? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) ? value as string : null;

    private static bool ValuesEqual(object? left, object? right) => JsonSerializer.Serialize(JsonValue(left)) == JsonSerializer.Serialize(JsonValue(right));

    private static string Brief(object? value)
    {
        if (value is null) return "∅";
        if (value is string text) return text.Length <= 40 ? text : text[..40] + "…";
        return JsonSerializer.Serialize(JsonValue(value), new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) is { Length: <= 60 } json ? json : JsonSerializer.Serialize(JsonValue(value), new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        })[..60] + "…";
    }
}
