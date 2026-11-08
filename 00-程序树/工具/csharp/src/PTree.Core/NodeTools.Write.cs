using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PTree.Core;

public static partial class NodeTools
{
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
}
