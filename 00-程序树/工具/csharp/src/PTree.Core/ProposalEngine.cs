using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PTree.Core;

public sealed record ProposalIssue(
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("node")] string? Node,
    [property: JsonPropertyName("message")] string Message);

public sealed class ProposalEval
{
    public string Label { get; set; } = "proposal";
    public bool Ok => Issues.All(issue => issue.Level != "E");
    public List<ProposalIssue> Issues { get; } = new();
    public List<ProposalIssue> Errors => Issues.Where(issue => issue.Level == "E").ToList();
    public List<ProposalIssue> Warnings => Issues.Where(issue => issue.Level == "W").ToList();
    public List<ProposalIssue> Notes => Issues.Where(issue => issue.Level == "I").ToList();
    public List<string> Summary { get; } = new();
    public List<string> Added { get; } = new();
    public List<string> Removed { get; } = new();
    public List<string> Deprecated { get; } = new();
    public List<RawMap> PruneCandidates { get; } = new();
    public bool BenchChanged { get; set; }
    public bool MetaChanged { get; set; }
    public RawMap Manifest { get; set; } = new();
    public object? Contracts { get; set; }
    public object? Benchmarks { get; set; }
    public object? Bindings { get; set; }
    internal Dictionary<string, string> IntroOverrides { get; } = new(StringComparer.Ordinal);
    internal string Actor { get; set; } = "ptree-cs";
    internal string Reason { get; set; } = "";
    internal RawMap? Proposal { get; set; }
}

public sealed class ApplyResult
{
    public bool Ok { get; set; }
    public bool DryRun { get; set; }
    public bool Written { get; set; }
    public List<string> Proposals { get; } = new();
    public List<ProposalIssue> Errors { get; } = new();
    public List<ProposalIssue> Warnings { get; } = new();
    public List<ProposalIssue> Notes { get; } = new();
    public List<string> Summary { get; } = new();
    public List<string> WrittenFiles { get; } = new();
    public string? LedgerId { get; set; }
    public string? Error { get; set; }
}

/// <summary>C# 提案引擎：解析提案、逐 op 落内存状态、候选校验、可选写盘与台账。</summary>
public static class ProposalEngine
{
    private static readonly string[] AllowedOps =
    {
        "add", "modify", "deprecate", "prune", "split", "merge", "intro", "meta",
        "bench_add", "bench_bind", "bench_cover", "bench_prune",
    };

    public static ProposalEval Evaluate(PTreeRepository repository, string yamlText, string label)
    {
        var evaluation = new ProposalEval { Label = label };
        object? parsed;
        try
        {
            parsed = RawYaml.Parse(yamlText);
        }
        catch (PTreeException exception)
        {
            Add(evaluation, "E", "R02", null, "提案 YAML 解析失败：" + exception.Message);
            return evaluation;
        }
        if (parsed is not RawMap proposal)
        {
            Add(evaluation, "E", "R02", null, "提案顶层必须是映射");
            return evaluation;
        }
        evaluation.Proposal = proposal;
        evaluation.Actor = StringOf(proposal, "author")?.Trim() is { Length: > 0 } author ? author : "ptree-cs";
        evaluation.Reason = StringOf(proposal, "reason")?.Trim() ?? "";
        evaluation.Manifest = CloneMap(repository.ManifestRaw);
        evaluation.Contracts = CloneValue(repository.ContractsRaw);
        evaluation.Benchmarks = CloneValue(repository.BenchmarksRaw);
        evaluation.Bindings = CloneValue(repository.BindingsRaw);
        if (!(proposal.TryGetValue("ops", out var opsRaw) && opsRaw is List<object?> ops && ops.Count > 0))
        {
            Add(evaluation, "E", "R02", null, "提案必须包含非空 ops 数组");
            return evaluation;
        }

        for (var index = 0; index < ops.Count; index++)
        {
            if (ops[index] is not RawMap op)
            {
                Add(evaluation, "E", "R02", null, "ops[" + index + "] 必须是映射");
                continue;
            }
            var opType = StringOf(op, "op");
            var opLabel = "ops[" + index + "]";
            var beforeErrors = evaluation.Errors.Count;
            switch (opType)
            {
                case "add": ApplyAdd(repository, evaluation, op, opLabel); break;
                case "modify": ApplyModify(repository, evaluation, op, opLabel); break;
                case "deprecate": ApplyDeprecate(evaluation, op, opLabel); break;
                case "prune": ApplyPrune(repository, evaluation, op, opLabel); break;
                case "split": ApplySplit(repository, evaluation, op, opLabel); break;
                case "merge": ApplyMerge(evaluation, op, opLabel); break;
                case "intro": ApplyIntro(evaluation, op, opLabel); break;
                case "meta": ApplyMeta(evaluation, op, opLabel); break;
                case "bench_add":
                case "bench_bind":
                case "bench_cover":
                case "bench_prune":
                    ApplyBench(evaluation, op, opType, opLabel); break;
                default:
                    Add(evaluation, "E", "R02", null, opLabel + ".op 非法：" + (opType ?? "<null>") +
                        "（允许 " + string.Join("/", AllowedOps) + "）");
                    break;
            }
            if (evaluation.Errors.Count > beforeErrors) continue;
            if (opType is "add" or "modify") RecordIntro(evaluation, op, opLabel);
        }
        ApplyProposalContracts(evaluation, proposal, label);
        ValidateCandidate(repository, evaluation);
        return evaluation;
    }

    public static ProposalEval EvaluateFile(PTreeRepository repository, string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        return Evaluate(repository, text, Path.GetFileName(path));
    }

    public static ApplyResult Apply(PTreeRepository repository, IReadOnlyList<string> paths, bool write,
        bool dryRun, Func<PTreeRepository, string>? regenerate = null)
    {
        var result = new ApplyResult { DryRun = dryRun || !write };
        var evalList = new List<ProposalEval>();
        var current = repository;
        foreach (var path in paths)
        {
            result.Proposals.Add(Path.GetFileName(path));
            ProposalEval evaluation;
            try
            {
                evaluation = EvaluateFile(current, path);
            }
            catch (PTreeException exception)
            {
                result.Errors.Add(new ProposalIssue("E", "R02", null, exception.Message));
                break;
            }
            evalList.Add(evaluation);
            result.Errors.AddRange(evaluation.Errors);
            result.Warnings.AddRange(evaluation.Warnings);
            result.Notes.AddRange(evaluation.Notes);
            result.Summary.AddRange(evaluation.Summary);
            if (!evaluation.Ok) break;
            current = PTreeRepository.FromRaw(repository.Root, evaluation.Manifest, evaluation.Contracts,
                evaluation.Benchmarks, evaluation.Bindings);
        }
        result.Ok = result.Errors.Count == 0;
        if (!write || !result.Ok || evalList.Count == 0)
        {
            return result;
        }
        var applyAgent = evalList[0].Actor;
        var guardPaths = new List<string> { "00-程序树/program-tree.yaml" };
        var guardMessage = Coord.Guard(repository.Root, guardPaths, applyAgent);
        if (guardMessage is not null)
        {
            result.Ok = false;
            result.Error = guardMessage;
            result.Errors.Add(new ProposalIssue("E", "R02", null, "写盘被协作守门拦下：" + guardMessage));
            return result;
        }

        try
        {
            WriteEvaluations(repository, evalList, result, regenerate);
            result.Written = true;
        }
        catch (Exception exception)
        {
            result.Ok = false;
            result.Written = false;
            result.Error = exception.Message;
            result.Errors.Add(new ProposalIssue("E", "R02", null, exception.Message));
        }
        return result;
    }

    private static void ApplyAdd(PTreeRepository repository, ProposalEval evaluation, RawMap op, string label)
    {
        if (op.TryGetValue("node", out var nodeRaw) && nodeRaw is RawMap node)
        {
            AddNode(repository, evaluation, node, label + ".node", appendToList: true);
        }
        else
        {
            Add(evaluation, "E", "R02", null, label + ".node 必须是映射");
        }
        foreach (var contract in ListOf(op, "contracts").OfType<RawMap>())
        {
            UpsertContract(evaluation, contract);
        }
    }

    private static void ApplyModify(PTreeRepository repository, ProposalEval evaluation, RawMap op, string label)
    {
        var id = StringOf(op, "node");
        var target = FindNode(evaluation, id);
        if (target is null)
        {
            Add(evaluation, "E", "R02", id, label + ".modify 目标节点不存在");
            return;
        }
        if (!op.TryGetValue("changes", out var changesRaw) || changesRaw is not RawMap changes || changes.Count == 0)
        {
            Add(evaluation, "E", "R02", id, label + ".modify 必须提供非空 changes");
            return;
        }
        foreach (var pair in changes)
        {
            if (pair.Key == "id")
            {
                Add(evaluation, "E", "R02", id, label + ".modify 不允许改 id");
                continue;
            }
            target.Set(pair.Key, CloneValue(pair.Value));
        }
        evaluation.Summary.Add("modify " + id);
    }

    private static void ApplyDeprecate(ProposalEval evaluation, RawMap op, string label)
    {
        var id = StringOf(op, "node");
        var target = FindNode(evaluation, id);
        if (target is null)
        {
            Add(evaluation, "E", "R02", id, label + ".deprecate 目标节点不存在");
            return;
        }
        if (Downstream(evaluation, id!).Count > 0)
        {
            Add(evaluation, "E", "R02", id, label + ".deprecate 有下游依赖，禁止直接退役：" +
                string.Join("、", Downstream(evaluation, id!)));
        }
        target.Set("status", StatusValues.Retired);
        if (!target.ContainsKey("legacy_reason"))
            target.Set("legacy_reason", string.IsNullOrWhiteSpace(StringOf(op, "reason")) ? "由提案退役，保留只读记录" : StringOf(op, "reason"));
        if (!target.ContainsKey("authority")) target.Set("authority", null);
        var replaced = StringOf(op, "replaced_by");
        target.Set("replaced_by", replaced);
        var reason = StringOf(op, "reason");
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var notes = StringOf(target, "notes");
            target.Set("notes", (notes is { Length: > 0 } ? notes + "；" : "") + "废弃原因：" + reason.Trim());
        }
        evaluation.Deprecated.Add(id!);
        evaluation.Summary.Add("deprecate " + id + (replaced is null ? "" : " -> " + replaced));
    }

    private static void ApplyPrune(PTreeRepository repository, ProposalEval evaluation, RawMap op, string label)
    {
        var id = StringOf(op, "node");
        var target = FindNode(evaluation, id);
        if (target is null)
        {
            Add(evaluation, "E", "R02", id, label + ".prune 目标节点不存在");
            return;
        }
        var mode = StringOf(op, "mode");
        if (mode == "deprecate")
        {
            ApplyDeprecate(evaluation, op, label);
            return;
        }
        if (mode is not ("prune_candidate" or "remove"))
        {
            Add(evaluation, "E", "R02", id, label + ".prune.mode 非法：" + (mode ?? "<null>"));
            return;
        }
        var downstream = Downstream(evaluation, id!);
        if (downstream.Count > 0)
        {
            Add(evaluation, "E", "R24", id, label + ".prune 仍有下游依赖：" + string.Join("、", downstream));
        }
        if (!string.Equals(StringOf(target, "status"), StatusValues.Todo, StringComparison.Ordinal))
        {
            Add(evaluation, "E", "R24", id, label + ".prune 只允许未完成节点");
        }
        if (mode == "prune_candidate")
        {
            if (evaluation.Errors.Count == 0)
            {
                var candidate = new RawMap();
                candidate.Set("node", id);
                candidate.Set("reason", StringOf(op, "reason") ?? "");
                evaluation.PruneCandidates.Add(candidate);
                evaluation.Summary.Add("prune_candidate " + id);
            }
            return;
        }
        if (ListOf(target, "files").Count > 0)
        {
            Add(evaluation, "E", "R24", id, label + ".prune 节点仍登记 files，先清理代码");
        }
        if (evaluation.Errors.Count == 0)
        {
            RemoveNode(evaluation, id!);
            evaluation.Summary.Add("prune(remove) " + id);
        }
    }

    private static void ApplySplit(PTreeRepository repository, ProposalEval evaluation, RawMap op, string label)
    {
        var id = StringOf(op, "node");
        var target = FindNode(evaluation, id);
        if (target is null)
        {
            Add(evaluation, "E", "R02", id, label + ".split 目标节点不存在");
            return;
        }
        if (Children(evaluation, id!).Count > 0)
        {
            Add(evaluation, "E", "R02", id, label + ".split 目标节点仍有子节点");
        }
        var layer = LongOf(target, "layer");
        if (layer >= 3)
        {
            Add(evaluation, "E", "R02", id, label + ".split 目标节点已在最底层（layer=3）");
        }
        if (!(op.TryGetValue("into", out var intoRaw) && intoRaw is List<object?> fragments && fragments.Count >= 2))
        {
            Add(evaluation, "E", "R02", id, label + ".split 必须提供 into 数组（至少 2 个节点）");
            return;
        }
        foreach (var fragmentRaw in fragments)
        {
            if (fragmentRaw is not RawMap fragment)
            {
                Add(evaluation, "E", "R02", id, label + ".split.into 元素必须是映射");
                continue;
            }
            if (!fragment.ContainsKey("parent")) fragment.Set("parent", id);
            if (!fragment.ContainsKey("phase")) fragment.Set("phase", StringOf(target, "phase"));
            if (!fragment.ContainsKey("layer")) fragment.Set("layer", (layer ?? 0) + 1);
            if (!fragment.ContainsKey("kind")) fragment.Set("kind", "step");
            AddNode(repository, evaluation, fragment, label + ".split.into", appendToList: true);
        }
        target.Set("status", StatusValues.Retired);
        if (!target.ContainsKey("legacy_reason"))
            target.Set("legacy_reason", string.IsNullOrWhiteSpace(StringOf(op, "reason")) ? "拆分后退役，保留只读记录" : StringOf(op, "reason"));
        if (!target.ContainsKey("authority")) target.Set("authority", null);
        target.Set("replaced_by", StringOf(op, "replaced_by"));
        var successor = string.Join("、", fragments.OfType<RawMap>().Select(item => StringOf(item, "id")).Where(value => value is not null));
        var notes = StringOf(target, "notes");
        target.Set("notes", ((notes is { Length: > 0 } ? notes + "；" : "") + "拆分去向：" + successor).Trim('；'));
        evaluation.Deprecated.Add(id!);
        evaluation.Summary.Add("split " + id + " -> " + successor);
    }

    private static void ApplyMerge(ProposalEval evaluation, RawMap op, string label)
    {
        if (!(op.TryGetValue("nodes", out var nodesRaw) && nodesRaw is List<object?> sourceIds && sourceIds.Count >= 2))
        {
            Add(evaluation, "E", "R02", null, label + ".merge 必须提供至少 2 个节点 id");
            return;
        }
        var sources = new List<RawMap>();
        foreach (var sourceId in sourceIds.OfType<string>())
        {
            var source = FindNode(evaluation, sourceId);
            if (source is null)
            {
                Add(evaluation, "E", "R02", sourceId, label + ".merge 源节点不存在");
                continue;
            }
            sources.Add(source);
            if (StringOf(source, "status") != StatusValues.Todo)
                Add(evaluation, "E", "R02", sourceId, label + ".merge 只允许合并未完成节点");
            if (Children(evaluation, sourceId).Count > 0)
                Add(evaluation, "E", "R02", sourceId, label + ".merge 源节点仍有子节点");
            var downstream = Downstream(evaluation, sourceId).Where(item => !sourceIds.Contains(item)).ToList();
            if (downstream.Count > 0)
                Add(evaluation, "E", "R02", sourceId, label + ".merge 源节点仍有下游依赖：" + string.Join("、", downstream));
        }
        if (op.TryGetValue("into", out var intoRaw) && intoRaw is RawMap merged)
        {
            if (!merged.ContainsKey("parent") && sources.Count > 0)
            {
                var parents = sources.Select(source => StringOf(source, "parent")).Distinct().ToList();
                if (parents.Count == 1) merged.Set("parent", parents[0]);
            }
            AddNode(null, evaluation, merged, label + ".merge.into", appendToList: true);
            foreach (var source in sources)
            {
                var sourceId = StringOf(source, "id");
                if (sourceId is not null && evaluation.Errors.Count == 0)
                {
                    RemoveNode(evaluation, sourceId);
                }
            }
            evaluation.Summary.Add("merge " + string.Join("、", sourceIds) + " -> " + StringOf(merged, "id"));
        }
        else
        {
            Add(evaluation, "E", "R02", null, label + ".merge 必须提供 into 节点片段");
        }
    }

    private static void ApplyIntro(ProposalEval evaluation, RawMap op, string label)
    {
        var id = StringOf(op, "node");
        if (FindNode(evaluation, id) is null)
        {
            Add(evaluation, "E", "R02", id, label + ".intro 目标节点不存在");
            return;
        }
        var value = op.TryGetValue("text", out var text) ? text : op.TryGetValue("intro", out var intro) ? intro : null;
        var content = value as string ?? (value is RawMap map ? StringOf(map, "text") : null);
        if (string.IsNullOrWhiteSpace(content))
        {
            Add(evaluation, "E", "R02", id, label + ".intro 必须提供非空 text");
            return;
        }
        evaluation.IntroOverrides[id!] = content;
        evaluation.Summary.Add("intro " + id + "（" + PythonText.LengthWithoutWhitespace(content) + " 字）");
    }

    private static void ApplyMeta(ProposalEval evaluation, RawMap op, string label)
    {
        var changed = new List<string>();
        if (op.TryGetValue("tree_version", out var versionRaw))
        {
            var version = versionRaw as string;
            if (version is null || !System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$"))
                Add(evaluation, "E", "R02", null, label + ".meta.tree_version 必须形如 1.1.0");
            else
            {
                var old = StringOf(evaluation.Manifest, "tree_version");
                evaluation.Manifest.Set("tree_version", version);
                changed.Add("tree_version " + old + " -> " + version);
            }
        }
        if (op.TryGetValue("updated", out var updatedRaw))
        {
            var updated = updatedRaw as string;
            if (updated is null || !DateTime.TryParseExact(updated, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _))
                Add(evaluation, "E", "R02", null, label + ".meta.updated 必须是合法日期 YYYY-MM-DD");
            else
            {
                var old = StringOf(evaluation.Manifest, "updated");
                evaluation.Manifest.Set("updated", updated);
                changed.Add("updated " + old + " -> " + updated);
            }
        }
        if (changed.Count == 0 && evaluation.Errors.Count == 0)
            Add(evaluation, "E", "R02", null, label + ".meta 必须至少提供 tree_version 或 updated");
        evaluation.MetaChanged = true;
        evaluation.Summary.Add("meta " + (changed.Count > 0 ? string.Join("；", changed) : "无变化"));
    }

    private static void ApplyBench(ProposalEval evaluation, RawMap op, string opType, string label)
    {
        evaluation.BenchChanged = true;
        var doc = evaluation.Benchmarks as RawMap;
        var bindingDoc = evaluation.Bindings as RawMap;
        switch (opType)
        {
            case "bench_add":
                if (op.TryGetValue("benchmark", out var benchmarkRaw) && benchmarkRaw is RawMap benchmark)
                {
                    var benchmarkList = EnsureList(doc!, "benchmarks");
                    benchmarkList.Add(CloneMap(benchmark));
                    evaluation.Summary.Add("bench_add " + StringOf(benchmark, "id"));
                }
                else Add(evaluation, "E", "R02", null, label + ".bench_add 缺少 benchmark");
                break;
            case "bench_bind":
                if (op.TryGetValue("binding", out var bindingRaw) && bindingRaw is RawMap binding)
                {
                    var bindingList2 = EnsureList(bindingDoc!, "bindings");
                    bindingList2.Add(CloneMap(binding));
                    evaluation.Summary.Add("bench_bind " + StringOf(binding, "node") + " × " + StringOf(binding, "benchmark"));
                }
                else Add(evaluation, "E", "R02", null, label + ".bench_bind 缺少 binding");
                break;
            case "bench_cover":
                if (op.TryGetValue("coverage", out var coverageRaw) && coverageRaw is RawMap coverage)
                {
                    doc!.Set("coverage", CloneMap(coverage));
                    evaluation.Summary.Add("bench_cover " + StringOf(coverage, "benchmark"));
                }
                else Add(evaluation, "E", "R02", null, label + ".bench_cover 缺少 coverage");
                break;
            case "bench_prune":
                var id = StringOf(op, "benchmark");
                if (id is null)
                {
                    Add(evaluation, "E", "R02", null, label + ".bench_prune 缺少 benchmark");
                    break;
                }
                if (doc?.TryGetValue("benchmarks", out var rawList) == true && rawList is List<object?> list)
                {
                    list.RemoveAll(item => item is RawMap map && StringOf(map, "id") == id);
                }
                if (bindingDoc?.TryGetValue("bindings", out var bindingListRaw) == true && bindingListRaw is List<object?> bindingList)
                {
                    if (bindingList.Any(item => item is RawMap map && StringOf(map, "benchmark") == id))
                        Add(evaluation, "E", "R30", id, label + ".bench_prune 标杆仍被绑定引用");
                }
                evaluation.Summary.Add("bench_prune " + id);
                break;
        }
    }

    private static void ApplyProposalContracts(ProposalEval evaluation, RawMap proposal, string label)
    {
        if (!proposal.TryGetValue("contracts", out var contractsRaw)) return;
        if (contractsRaw is not List<object?> contracts)
        {
            Add(evaluation, "E", "R02", null, label + ".contracts 必须是数组");
            return;
        }
        foreach (var item in contracts.OfType<RawMap>()) UpsertContract(evaluation, item);
    }

    private static void UpsertContract(ProposalEval evaluation, RawMap contract)
    {
        var id = StringOf(contract, "id");
        if (string.IsNullOrWhiteSpace(id)) return;
        if (evaluation.Contracts is not RawMap doc) doc = new RawMap();
        var list = EnsureList(doc, "contracts");
        var existing = list.OfType<RawMap>().FirstOrDefault(item => StringOf(item, "id") == id);
        if (existing is null) list.Add(CloneMap(contract));
        else
        {
            var replacement = CloneMap(contract);
            var position = list.IndexOf(existing);
            list[position] = replacement;
        }
        evaluation.Contracts = doc;
    }

    public static ProposalEval CheckPlan(PTreeRepository repository, string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        var parsed = RawYaml.Parse(text);
        RawMap? node = parsed as RawMap;
        if (node is not null && node.TryGetValue("node", out var nodeRaw) && nodeRaw is RawMap inner) node = inner;
        var proposal = new RawMap();
        proposal.Set("author", "ptree-cs");
        proposal.Set("reason", "check-plan");
        var ops = new List<object?>();
        if (node is not null)
        {
            var op = new RawMap();
            op.Set("op", "add");
            op.Set("node", CloneMap(node));
            ops.Add(op);
        }
        proposal.Set("ops", ops);
        return Evaluate(repository, YamlEmitter.Dump(proposal), Path.GetFileName(path));
    }

    private static void ValidateCandidate(PTreeRepository repository, ProposalEval evaluation)
    {
        evaluation.Actor = evaluation.Actor.Trim();
        var candidate = PTreeRepository.FromRaw(repository.Root, evaluation.Manifest, evaluation.Contracts,
            evaluation.Benchmarks, evaluation.Bindings);
        var baseline = Validator.Validate(repository);
        var candidateResult = Validator.Validate(candidate);
        var baselineKeys = baseline.Errors.Concat(baseline.Warnings)
            .Select(issue => issue.Code + "\u0000" + issue.Node + "\u0000" + issue.Message)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var issue in candidateResult.Errors)
        {
            var key = issue.Code + "" + issue.Node + "" + issue.Message;
            if (baselineKeys.Contains(key)) continue;
            Add(evaluation, "E", issue.Code, issue.Node, issue.Message);
        }
        foreach (var issue in candidateResult.Warnings)
        {
            var key = issue.Code + "" + issue.Node + "" + issue.Message;
            if (baselineKeys.Contains(key)) continue;
            Add(evaluation, "W", issue.Code, issue.Node, issue.Message);
        }
    }

    private static void AddNode(PTreeRepository? repository, ProposalEval evaluation, RawMap node, string label, bool appendToList)
    {
        var id = StringOf(node, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            Add(evaluation, "E", "R02", null, label + " 缺少 id");
            return;
        }
        if (FindNode(evaluation, id) is not null)
        {
            Add(evaluation, "E", "R02", id, label + " id 重复：" + id);
            return;
        }
        if (!node.ContainsKey("created_at")) node.Set("created_at", "2026-10-25");
        if (!node.ContainsKey("updated_at")) node.Set("updated_at", "2026-10-25");
        if (!node.ContainsKey("attachments")) node.Set("attachments", new List<object?>());
        if (appendToList)
        {
            var nodes = EnsureList(evaluation.Manifest, "nodes");
            nodes.Add(CloneMap(node));
            evaluation.Added.Add(id);
            evaluation.Summary.Add("add " + id);
        }
    }

    private static void RemoveNode(ProposalEval evaluation, string id)
    {
        if (evaluation.Manifest.TryGetValue("nodes", out var nodesRaw) && nodesRaw is List<object?> nodes)
            nodes.RemoveAll(item => item is RawMap map && StringOf(map, "id") == id);
        if (evaluation.Manifest.TryGetValue("groups", out var groupsRaw) && groupsRaw is List<object?> groups)
        {
            foreach (var group in groups.OfType<RawMap>())
            {
                if (group.TryGetValue("members", out var membersRaw) && membersRaw is List<object?> members)
                    members.RemoveAll(item => item as string == id);
            }
        }
        evaluation.Removed.Add(id);
    }

    private static List<string> Downstream(ProposalEval evaluation, string id)
    {
        var result = new List<string>();
        foreach (var node in Nodes(evaluation))
        {
            var nodeId = StringOf(node, "id");
            if (nodeId == id) continue;
            if (ListOf(node, "depends_on").OfType<string>().Contains(id, StringComparer.Ordinal))
                result.Add(nodeId ?? "?");
        }
        return result;
    }

    private static List<string> Children(ProposalEval evaluation, string id) => Nodes(evaluation)
        .Where(node => StringOf(node, "parent") == id).Select(node => StringOf(node, "id")).Where(value => value is not null)
        .Select(value => value!).ToList();

    private static List<RawMap> Nodes(ProposalEval evaluation) =>
        evaluation.Manifest.TryGetValue("nodes", out var raw) && raw is List<object?> list
            ? list.OfType<RawMap>().ToList() : new List<RawMap>();

    private static RawMap? FindNode(ProposalEval evaluation, string? id) =>
        id is null ? null : Nodes(evaluation).FirstOrDefault(node => StringOf(node, "id") == id);

    private static List<object?> EnsureList(RawMap map, string key)
    {
        if (map.TryGetValue(key, out var raw) && raw is List<object?> list) return list;
        var created = new List<object?>();
        map.Set(key, created);
        return created;
    }

    private static void RecordIntro(ProposalEval evaluation, RawMap op, string label)
    {
        if (!op.TryGetValue("intro", out var raw)) return;
        var text = raw as string ?? (raw is RawMap map ? StringOf(map, "text") : null);
        if (string.IsNullOrWhiteSpace(text))
        {
            Add(evaluation, "E", "R02", null, label + ".intro 必须是非空说明正文");
            return;
        }
        var id = StringOf(op, "node") ?? (op.TryGetValue("node", out var node) && node is RawMap nodeMap ? StringOf(nodeMap, "id") : null);
        if (id is not null) evaluation.IntroOverrides[id] = text;
    }

    private static void Add(ProposalEval evaluation, string level, string code, string? node, string message)
    {
        if (evaluation.Issues.Any(issue => issue.Level == level && issue.Code == code && issue.Node == node && issue.Message == message))
            return;
        evaluation.Issues.Add(new ProposalIssue(level, code, node, message));
    }

    private static string? StringOf(RawMap? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) ? value as string : null;

    private static long? LongOf(RawMap map, string key) =>
        map.TryGetValue(key, out var value) && value is long number ? number : null;

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

    private static void WriteEvaluations(PTreeRepository repository, List<ProposalEval> evaluations,
        ApplyResult result, Func<PTreeRepository, string>? regenerate)
    {
        var final = evaluations[^1];
        var manifestPath = Path.Combine(repository.Root, PTreeRepository.ManifestRel.Replace('/', Path.DirectorySeparatorChar));
        var contractsPath = repository.ContractsPath;
        var benchChanged = evaluations.Any(item => item.BenchChanged);
        var backup = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var created = new List<string>();
        var introBackup = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var path in new[] { manifestPath, contractsPath, repository.BenchmarksPath, repository.BindingsPath,
                     Path.Combine(repository.Root, "00-程序树", "生成", "程序树总览.txt"),
                     Path.Combine(repository.Root, "00-程序树", "生成", "落地顺序.txt"),
                     Path.Combine(repository.Root, "00-程序树", "生成", "程序书.txt"),
                     Path.Combine(repository.Root, "00-程序树", "生成", "标杆看板.txt") })
        {
            if (File.Exists(path)) backup[path] = File.ReadAllBytes(path);
        }
        foreach (var id in final.IntroOverrides.Keys)
        {
            var path = Path.Combine(repository.Root, PTreeRepository.IntroDirRel.Replace('/', Path.DirectorySeparatorChar), id + PTreeRepository.IntroExtension);
            introBackup[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        foreach (var id in evaluations.SelectMany(item => item.Removed))
        {
            var path = Path.Combine(repository.Root, PTreeRepository.IntroDirRel.Replace('/', Path.DirectorySeparatorChar), id + PTreeRepository.IntroExtension);
            if (!introBackup.ContainsKey(path) && File.Exists(path)) introBackup[path] = File.ReadAllBytes(path);
        }
        try
        {
            WriteText(manifestPath, YamlEmitter.Dump(final.Manifest));
            result.WrittenFiles.Add(Path.GetRelativePath(repository.Root, manifestPath).Replace('\\', '/'));
            if (final.Contracts is RawMap contracts)
            {
                WriteText(contractsPath, YamlEmitter.Dump(contracts));
                result.WrittenFiles.Add(Path.GetRelativePath(repository.Root, contractsPath).Replace('\\', '/'));
            }
            if (benchChanged)
            {
                if (final.Benchmarks is RawMap benchmarks) WriteText(repository.BenchmarksPath, YamlEmitter.Dump(benchmarks));
                if (final.Bindings is RawMap bindings) WriteText(repository.BindingsPath, YamlEmitter.Dump(bindings));
            }
            foreach (var pair in final.IntroOverrides)
            {
                var path = Path.Combine(repository.Root, PTreeRepository.IntroDirRel.Replace('/', Path.DirectorySeparatorChar), pair.Key + PTreeRepository.IntroExtension);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteText(path, pair.Value.EndsWith('\n') ? pair.Value : pair.Value + "\n");
                if (!introBackup.ContainsKey(path) || introBackup[path] is null) created.Add(path);
            }
            foreach (var id in evaluations.SelectMany(item => item.Removed).Distinct())
            {
                var path = Path.Combine(repository.Root, PTreeRepository.IntroDirRel.Replace('/', Path.DirectorySeparatorChar), id + PTreeRepository.IntroExtension);
                if (File.Exists(path)) File.Delete(path);
            }
            var reloaded = PTreeRepository.Load(repository.Root);
            var residual = Validator.Validate(reloaded);
            if (residual.Errors.Count > 0)
            {
                throw new PTreeException("落地后 validate 失败，已回滚：" + string.Join("；", residual.Errors.Take(5).Select(issue => issue.Code + " " + issue.Message)));
            }
            if (regenerate is not null) regenerate(reloaded);
            var ledger = AppendLedger(repository, evaluations, final, residual);
            result.LedgerId = ledger.TryGetValue("id", out var ledgerId) ? ledgerId as string : null;
            result.WrittenFiles.Add("00-程序树/变更/台账.jsonl");
        }
        catch
        {
            foreach (var path in created)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
            foreach (var pair in backup)
            {
                try { WriteBytes(pair.Key, pair.Value); } catch { }
            }
            foreach (var pair in introBackup)
            {
                try
                {
                    if (pair.Value is null) { if (File.Exists(pair.Key)) File.Delete(pair.Key); }
                    else WriteBytes(pair.Key, pair.Value);
                }
                catch { }
            }
            throw;
        }
    }

    private static Dictionary<string, object?> AppendLedger(PTreeRepository repository, List<ProposalEval> evaluations,
        ProposalEval final, ValidationPayload residual)
    {
        var path = Path.Combine(repository.Root, "00-程序树", "变更", "台账.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        var ledger = new Dictionary<string, object?>
        {
            ["id"] = prefix + (maximum + 1).ToString("0000", CultureInfo.InvariantCulture),
            ["at"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["actor"] = final.Actor,
            ["proposal"] = "00-程序树/变更/" + (evaluations.Count > 0 ? evaluations[0].Label : ""),
            ["reason"] = final.Reason,
            ["ops"] = evaluations.SelectMany(item => item.Summary).ToList(),
            ["validation"] = new Dictionary<string, int> { ["errors"] = residual.Errors.Count, ["warnings"] = residual.Warnings.Count },
            ["tree_version_before"] = repository.Manifest.TreeVersion,
            ["tree_version_after"] = StringOf(final.Manifest, "tree_version"),
            ["result"] = "applied",
        };
        var ledgerOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.AppendAllText(path, JsonSerializer.Serialize(ledger, ledgerOptions) + "\n", new UTF8Encoding(false));
        return ledger;
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>轻量 YAML 发射器：保持 RawMap 键顺序，输出 UTF-8 友好、可被 PyYAML/YamlDotNet 读取。</summary>
public static class YamlEmitter
{
    public static string Dump(object? value)
    {
        var builder = new StringBuilder();
        WriteValue(value, builder, 0);
        return builder.ToString().TrimEnd() + "\n";
    }

    private static void WriteValue(object? value, StringBuilder builder, int indent)
    {
        switch (value)
        {
            case RawMap map:
                WriteMap(map, builder, indent, false);
                break;
            case List<object?> list:
                WriteList(list, builder, indent);
                break;
            default:
                builder.Append(new string(' ', indent)).Append(Scalar(value)).Append('\n');
                break;
        }
    }

    private static void WriteMap(RawMap map, StringBuilder builder, int indent, bool inlineFirst)
    {
        if (map.Count == 0)
        {
            builder.Append(new string(' ', Math.Max(0, indent - 2))).Append("{}\n");
            return;
        }
        var first = true;
        foreach (var pair in map)
        {
            if (inlineFirst && first) builder.Append(new string(' ', indent)).Append("- ");
            else builder.Append(new string(' ', inlineFirst ? indent + 2 : indent));
            builder.Append(pair.Key).Append(':');
            WritePairValue(pair.Value, builder, inlineFirst ? indent + 4 : indent + 2);
            first = false;
        }
    }

    private static void WriteList(List<object?> list, StringBuilder builder, int indent)
    {
        if (list.Count == 0)
        {
            builder.Append(new string(' ', Math.Max(0, indent - 2))).Append("[]\n");
            return;
        }
        foreach (var item in list)
        {
            if (item is RawMap map)
            {
                WriteMap(map, builder, indent, true);
            }
            else if (item is List<object?> nested)
            {
                builder.Append(new string(' ', indent)).Append("-\n");
                WriteList(nested, builder, indent + 2);
            }
            else
            {
                builder.Append(new string(' ', indent)).Append("- ").Append(Scalar(item)).Append('\n');
            }
        }
    }

    private static void WritePairValue(object? value, StringBuilder builder, int childIndent)
    {
        if (value is RawMap map)
        {
            if (map.Count == 0) builder.Append(" {}\n");
            else
            {
                builder.Append('\n');
                WriteMap(map, builder, childIndent, false);
            }
            return;
        }
        if (value is List<object?> list)
        {
            if (list.Count == 0) builder.Append(" []\n");
            else
            {
                builder.Append('\n');
                WriteList(list, builder, childIndent);
            }
            return;
        }
        builder.Append(' ').Append(Scalar(value)).Append('\n');
    }

    private static string Scalar(object? value)
    {
        if (value is null) return "null";
        if (value is bool boolean) return boolean ? "true" : "false";
        if (value is long or int) return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        if (value is double number) return number.ToString("R", CultureInfo.InvariantCulture);
        var text = value as string ?? PyValue.Str(value);
        if (text.Length == 0) return "''";
        var needsQuote = text.Any(ch => ch is ':' or '#' or '[' or ']' or '{' or '}' or ',' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '\n' or '\r');
        needsQuote |= text is "null" or "true" or "false" or "~";
        needsQuote |= System.Text.RegularExpressions.Regex.IsMatch(text, @"^[+-]?[0-9]+(?:\.[0-9]+)?$");
        needsQuote |= System.Text.RegularExpressions.Regex.IsMatch(text, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}$");
        if (!needsQuote) return text;
        return "'" + text.Replace("'", "''") + "'";
    }
}