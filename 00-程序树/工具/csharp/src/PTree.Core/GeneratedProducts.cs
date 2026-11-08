using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PTree.Core;

/// <summary>
/// 四份生成物的全文渲染（纯文本、无 Markdown 标记），按 ptree.py 的 view/order/book/benchmark 正文移植。
/// 统一输出到 00-程序树/生成/*.txt，文件头写明由 ptree-cs 生成、禁止手改、时间戳。
/// </summary>
public static class GeneratedProducts
{
    public const string GeneratedDir = "00-程序树/生成";
    public const string ViewFile = "程序树总览.txt";
    public const string OrderFile = "落地顺序.txt";
    public const string BookFile = "程序书.txt";
    public const string BenchmarkFile = "标杆看板.txt";

    private static readonly string[] KindOrder = { "root", "department", "program", "step", "external" };
    private static readonly string[] StatusOrder = { "未完成", "已完成", "已验证", "退役" };
    private static readonly Dictionary<string, string> StatusLegend = new(StringComparer.Ordinal)
    {
        ["未完成"] = "还没开工或还在写（files 为空或部分存在，spec 必须存在）",
        ["已完成"] = "有产物、能编译能跑（files 必须都存在）",
        ["已验证"] = "已完成 + 证据全 pass，只有用户确认后才能标",
        ["退役"] = "历史资产，保留不删（写 authority 与 replaced_by）",
    };
    private static readonly string[] SpecHeadings = { "职责", "不负责", "输入", "输出", "依赖", "边界与失败模式", "验收", "落地阶段" };

    public static void WriteAll(PTreeRepository repository)
    {
        var issues = Validator.Validate(repository);
        Directory.CreateDirectory(Path.Combine(repository.Root, GeneratedDir.Replace('/', Path.DirectorySeparatorChar)));
        Write(repository, ViewFile, RenderView(repository, issues));
        Write(repository, OrderFile, RenderOrder(repository, issues));
        Write(repository, BookFile, RenderBook(repository, issues));
        Write(repository, BenchmarkFile, RenderBenchmark(repository, issues));
    }

    public static string RenderView(PTreeRepository repository, ValidationPayload issues)
    {
        var stats = ComputeStats(repository);
        var lines = Header(repository, issues, repository.ProjectName + " 程序树总览");
        lines.Add("节点统计：共 " + stats.Total + " 个（" + KindSummary(stats) + "）");
        lines.Add("");
        lines.Add("一、树结构");
        lines.Add("");
        var tree = BuildAsciiTree(repository);
        if (tree.Count == 0) tree.Add("（无节点）");
        lines.AddRange(tree);
        lines.Add("");
        lines.Add("二、状态图例");
        lines.Add("");
        foreach (var status in StatusOrder)
        {
            lines.Add("  " + status + "：" + StatusLegend[status]);
        }
        lines.Add("");
        lines.Add("三、按部门统计");
        lines.Add("");
        var mapping = repository.NodeMap;
        foreach (var department in stats.ByDepartment.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            mapping.TryGetValue(department, out var node);
            var prefix = department + ".";
            var descendants = repository.Nodes.Where(item => item.Id is not null && item.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            var done = descendants.Count(item => item.Status is "已完成" or "已验证" or "built" or "verified");
            var doing = descendants.Count(item => item.Status is "未完成" or "planned" or "in_progress");
            var other = descendants.Count - done - doing;
            lines.Add("  " + department + "（" + (node?.Name ?? "") + "）：节点 " + descendants.Count + "；已落地 " + done + "；推进中 " + doing + "；其它 " + other);
        }
        if (stats.ByDepartment.Count == 0) lines.Add("  （无部门节点）");
        lines.Add("");
        lines.Add("四、依赖与父子关系");
        lines.Add("");
        foreach (var node in repository.Nodes)
        {
            if (node.Id is null) continue;
            var parts = new List<string>();
            if (node.DependsOn.Count > 0) parts.Add("依赖 " + string.Join("、", node.DependsOn));
            if (!string.IsNullOrEmpty(node.Parent)) parts.Add("父节点 " + node.Parent);
            if (parts.Count == 0) continue;
            lines.Add("  " + node.Id + "（" + node.Name + " · " + node.Status + "）：" + string.Join("；", parts));
        }
        lines.Add("");
        return string.Join("\n", lines) + "\n";
    }

    public static string RenderOrder(PTreeRepository repository, ValidationPayload issues)
    {
        var lines = Header(repository, issues, "程序树落地顺序");
        var mapping = repository.NodeMap;
        var phaseMeta = repository.Manifest.Phases.ToDictionary(phase => phase.Id ?? "", phase => phase, StringComparer.Ordinal);
        var nodesByPhase = repository.Nodes.Where(node => node.Id is not null && node.Phase is not null)
            .GroupBy(node => node.Phase!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        lines.Add("阶段顺序");
        lines.Add("");
        var phaseIndex = 0;
        foreach (var phaseId in PhaseIds(repository))
        {
            phaseIndex++;
            phaseMeta.TryGetValue(phaseId, out var phase);
            lines.Add("  " + phaseIndex + ". " + phaseId + " " + (phase?.Name ?? "") + " — " + (phase?.Goal ?? ""));
        }
        lines.Add("");
        var blocked = new List<string>();
        foreach (var phaseId in PhaseIds(repository))
        {
            phaseMeta.TryGetValue(phaseId, out var phase);
            lines.Add("阶段 " + phaseId + " " + (phase?.Name ?? ""));
            lines.Add("");
            var phaseNodes = nodesByPhase.TryGetValue(phaseId, out var list) ? list : new List<NodeModel>();
            var (ordered, leftover) = TopoOrder(phaseNodes, mapping);
            var leftoverSet = leftover.ToHashSet(StringComparer.Ordinal);
            foreach (var node in ordered.Concat(leftover.Select(id => mapping[id])))
            {
                var deps = node.DependsOn.Where(dep => !string.IsNullOrWhiteSpace(dep)).ToList();
                var suffix = node.Id is not null && leftoverSet.Contains(node.Id) ? "（依赖成环，按清单顺序）" : "";
                lines.Add("  [" + node.Status + "] " + node.Id + " " + node.Name + " — 依赖：" + (deps.Count > 0 ? string.Join("、", deps) : "无") + suffix);
                foreach (var dep in deps)
                {
                    mapping.TryGetValue(dep, out var depNode);
                    var depStatus = depNode?.Status ?? "缺失";
                    if (depStatus is not ("已完成" or "已验证" or "built" or "verified"))
                    {
                        blocked.Add("  " + node.Id + "（" + phaseId + "）：依赖 " + dep + " 当前 status=" + depStatus + "，尚未已完成/已验证");
                    }
                }
            }
            if (phaseNodes.Count == 0) lines.Add("  （无节点）");
            lines.Add("");
        }
        lines.Add("被阻塞节点");
        lines.Add("");
        if (blocked.Count == 0) lines.Add("  （无）");
        else lines.AddRange(blocked);
        lines.Add("");
        return string.Join("\n", lines) + "\n";
    }

    public static string RenderBook(PTreeRepository repository, ValidationPayload issues)
    {
        var stats = ComputeStats(repository);
        var lines = Header(repository, issues, repository.ProjectName + " 程序书");
        lines.Add("节点统计：共 " + stats.Total + " 个（" + KindSummary(stats) + "）");
        lines.Add("");
        lines.Add("程序书怎么用");
        lines.Add("");
        lines.Add("这是一份活文档：AI 可写变更提案（00-程序树/变更/*.yaml），用 apply 校验（默认 dry-run）后落地。");
        lines.Add("机器唯一真相是 program-tree.yaml；本文件只是给人和 AI 读的总书，禁止手改。");
        lines.Add("");
        lines.Add("树结构");
        lines.Add("");
        var tree = BuildAsciiTree(repository);
        lines.AddRange(tree.Count > 0 ? tree : new List<string> { "（无节点）" });
        lines.Add("");
        lines.Add("节点页");
        lines.Add("");
        foreach (var node in repository.Nodes)
        {
            if (node.Id is null) continue;
            lines.Add("节点：" + node.Id + " · " + node.Name);
            lines.Add("");
            lines.Add("  类型/层级：" + node.Kind + " / " + node.Layer + "；阶段：" + node.Phase + "；状态：" + node.Status);
            lines.Add("  父节点：" + (string.IsNullOrEmpty(node.Parent) ? "（无）" : node.Parent));
            lines.Add("  依赖：" + JoinOr(node.DependsOn));
            lines.Add("  输入契约：" + JoinOr(node.Inputs));
            lines.Add("  输出契约：" + JoinOr(node.Outputs));
            if (!string.IsNullOrEmpty(node.Home)) lines.Add("  代码目录：" + node.Home);
            if (node.Files.Count > 0) lines.Add("  落地文件：" + string.Join("、", node.Files));
            if (node.Acceptance.Count > 0) lines.Add("  验收：" + string.Join("；", node.Acceptance));
            if (node.Evidence.Count > 0)
            {
                lines.Add("  证据：" + node.Evidence.Count + " 条（最近：" + string.Join("；", node.Evidence.Take(3)
                    .Select(item => item.Test + "@" + item.At + "=" + item.Result)) + "）");
            }
            if (!string.IsNullOrEmpty(node.Spec)) lines.Add("  SPEC：" + node.Spec);
            if (!string.IsNullOrWhiteSpace(node.Notes)) lines.Add("  备注：" + node.Notes.Trim());
            lines.Add("");
            lines.Add("  职责：" + (string.IsNullOrWhiteSpace(node.Responsibility) ? "（未填写）" : node.Responsibility));
            if (node.NotResponsible.Count > 0) lines.Add("  不负责：" + string.Join("；", node.NotResponsible));
            lines.Add("");
            var (upstream, downstream) = Connections(repository, node);
            lines.Add("  连接：");
            lines.Add("    上游：" + (upstream.Count == 0 ? "无" : string.Join("；", upstream)));
            lines.Add("    下游：" + (downstream.Count == 0 ? "无" : string.Join("；", downstream)));
            var bindings = BindingsOf(repository, node.Id);
            if (bindings.Count > 0)
            {
                var parts = bindings.Select(binding => binding.Benchmark + "（L" + (binding.Level?.ToString() ?? "?") + " → L" + (binding.Target?.ToString() ?? "?") + "；差距 " + binding.Gaps.Count + " 条）");
                lines.Add("    标杆：" + string.Join("；", parts));
            }
            lines.Add("");
            var introPath = repository.IntroPath(node.Id);
            if (File.Exists(introPath))
            {
                lines.Add("  说明（" + repository.IntroRel(node.Id) + "）：");
                foreach (var introLine in File.ReadAllText(introPath).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    lines.Add("    " + introLine);
                }
                lines.Add("");
            }
            var specPath = SpecForNode(repository, node);
            if (specPath is not null)
            {
                var sections = ReadSpecSections(specPath);
                if (sections.Count > 0)
                {
                    lines.Add("  SPEC 摘要：");
                    foreach (var heading in SpecHeadings)
                    {
                        if (sections.TryGetValue(heading, out var content) && !string.IsNullOrWhiteSpace(content))
                        {
                            lines.Add("    " + heading + "：" + Truncate(content));
                        }
                    }
                    lines.Add("");
                }
            }
            lines.Add("");
        }
        lines.Add("变更历史入口");
        lines.Add("");
        lines.Add("  提案目录：00-程序树/变更/（AI 编写的变更提案，apply 处理）");
        lines.Add("  历史归档：00-程序树/变更/历史/（apply 落地后自动归档）");
        lines.Add("  剪枝候选：00-程序树/变更/剪枝候选.yaml");
        lines.Add("  写入口：ptree-cs apply --file <提案>（默认 dry-run，--write 落地）");
        lines.Add("");
        var pending = PendingProposals(repository);
        lines.Add("待处理提案：" + (pending.Count > 0 ? string.Join("；", pending) : "无"));
        lines.Add("");
        lines.Add("已修剪节点（来自变更历史）");
        lines.Add("");
        var archives = Archives(repository);
        var prunedAny = false;
        foreach (var archive in archives)
        {
            if (archive.Removed.Count == 0) continue;
            prunedAny = true;
            lines.Add("  " + string.Join("、", archive.Removed) + (string.IsNullOrEmpty(archive.Title) ? "" : "（" + archive.Title + "）") + "：已从清单移除（归档 " + archive.Name + "）");
        }
        if (!prunedAny) lines.Add("  （暂无）");
        lines.Add("");
        var recent = archives.Take(10).Select(item => item.Name + (string.IsNullOrEmpty(item.Title) ? "" : "（" + item.Title + "）")).ToList();
        lines.Add("最近归档：" + (recent.Count > 0 ? string.Join("；", recent) : "无"));
        lines.Add("");
        return string.Join("\n", lines) + "\n";
    }

    public static string RenderBenchmark(PTreeRepository repository, ValidationPayload issues)
    {
        var lines = Header(repository, issues, repository.ProjectName + " 标杆看板");
        var bindings = repository.Bindings.Bindings;
        var benchmarkMap = repository.Benchmarks.ById;
        var required = RequiredNodes(repository);
        var boundNodes = bindings.Where(item => !string.IsNullOrEmpty(item.Node)).Select(item => item.Node!).ToHashSet(StringComparer.Ordinal);
        var covered = required.Count(item => boundNodes.Contains(item));
        lines.Add("问题统计：E=" + issues.Errors.Count + "，W=" + issues.Warnings.Count);
        lines.Add("标杆：" + repository.Benchmarks.Benchmarks.Count + " 条；绑定：" + bindings.Count + " 条；覆盖达标：" + covered + "/" + required.Count);
        var baseline = BaselineDate(repository);
        if (baseline is not null) lines.Add("差距时效基准日（program-tree.yaml updated）：" + baseline.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        lines.Add("");
        lines.Add("一、标杆登记");
        lines.Add("");
        if (repository.BenchmarksRaw is null)
        {
            lines.Add("（00-程序树/标杆/benchmarks.yaml 不存在，无法登记）");
            lines.Add("");
        }
        else if (repository.Benchmarks.Benchmarks.Count == 0)
        {
            lines.Add("（benchmarks 为空）");
            lines.Add("");
        }
        else
        {
            var rawBenchmarks = RawBenchmarks(repository);
            foreach (var benchmark in repository.Benchmarks.Benchmarks)
            {
                if (benchmark.Id is null) continue;
                rawBenchmarks.TryGetValue(benchmark.Id, out var raw);
                lines.Add("  标杆：" + benchmark.Id);
                lines.Add("    名称：" + (benchmark.Name ?? "—"));
                lines.Add("    类别：" + (benchmark.Kind ?? "—"));
                lines.Add("    参考对象：" + Shorten(benchmark.Reference ?? "—", 44));
                lines.Add("    维度：" + (Dimensions(raw) is { Count: > 0 } dimensions ? string.Join("；", dimensions) : "—"));
                var nodes = bindings.Where(item => item.Benchmark == benchmark.Id).Select(item => item.Node).Where(item => item is not null).ToList();
                lines.Add("    被绑节点：" + (nodes.Count > 0 ? string.Join("、", nodes) : "—"));
                lines.Add("");
            }
        }
        lines.Add("二、覆盖情况");
        lines.Add("");
        if (repository.BenchmarksRaw is null)
        {
            lines.Add("（00-程序树/标杆/benchmarks.yaml 不存在，无法判断覆盖）");
            lines.Add("");
        }
        else if (required.Count == 0)
        {
            lines.Add("（coverage.required 未登记任何节点；覆盖范围只扩不缩，扩范围走 bench_cover）");
            lines.Add("");
        }
        else
        {
            foreach (var nodeId in required)
            {
                repository.NodeMap.TryGetValue(nodeId, out var node);
                var bound = bindings.Where(item => item.Node == nodeId).ToList();
                if (bound.Count == 0)
                {
                    lines.Add("  ❌ " + nodeId + "（" + (node?.Name ?? "?") + "）：未绑定任何标杆，必须 bench_bind 或从 coverage 中移除");
                }
                else
                {
                    var parts = bound.Select(item => item.Benchmark + "（" + (benchmarkMap.TryGetValue(item.Benchmark ?? "", out var bench) ? bench.Name : "?") + "，L" + item.Level + " → L" + item.Target + "）");
                    lines.Add("  ✅ " + nodeId + "（" + (node?.Name ?? "?") + "）：已绑定 " + string.Join("；", parts));
                }
            }
            lines.Add("");
            lines.Add("覆盖达标：" + covered + "/" + required.Count + "（" + (covered == required.Count ? "全部达标" : "仍有缺口") + "）");
            lines.Add("");
        }
        lines.Add("三、收敛看板");
        lines.Add("");
        if (bindings.Count == 0)
        {
            lines.Add("（还没有任何绑定；节点立标杆用 bench_bind）");
            lines.Add("");
        }
        else
        {
            foreach (var binding in bindings)
            {
                var level = binding.Level is >= 0 and <= 4 ? binding.Level.Value : (int?)null;
                var target = binding.Target is >= 0 and <= 4 ? binding.Target.Value : (int?)null;
                var next = binding.Gaps.FirstOrDefault(gap => !string.IsNullOrWhiteSpace(gap.Next))?.Next;
                if (next is null && level is not null && target is not null && level < target) next = "补一条差距（先把“不好”拆到维度）";
                var oldest = OldestGapDays(repository, binding);
                benchmarkMap.TryGetValue(binding.Benchmark ?? "", out var benchmark);
                var evidence = benchmark?.Levels.FirstOrDefault(step => step.Level == level)?.Evidence;
                lines.Add("  节点 " + (binding.Node ?? "—") + " × " + (binding.Benchmark ?? "—"));
                lines.Add("    进度：L" + (level?.ToString() ?? "?") + " → L" + (target?.ToString() ?? "?") + "（" + (level ?? 0) + "/4）");
                lines.Add("    当前等级证据：" + (string.IsNullOrWhiteSpace(evidence) ? "—" : evidence));
                lines.Add("    差距：" + binding.Gaps.Count + " 条；最老差距：" + (oldest is null ? "—" : oldest + " 天"));
                lines.Add("    下一动作：" + Shorten(next ?? "—", 40));
                lines.Add("");
            }
        }
        lines.Add("四、过期警报（R28：opened 超过 120 天未关）");
        lines.Add("");
        var stale = issues.Warnings.Concat(issues.Notes).Where(issue => issue.Code == "R28").ToList();
        if (stale.Count == 0) lines.Add("  （无过期差距）");
        else
        {
            foreach (var issue in stale) lines.Add("  [W] " + issue.Code + " " + (issue.Node is null ? "" : issue.Node + ": ") + issue.Message);
        }
        lines.Add("");
        lines.Add("收敛环（规范第五节）：动手前只选一条差距；动手后等级没升、差距没减 → 本次改动记为无效改动，回滚或重写。");
        lines.Add("");
        return string.Join("\n", lines) + "\n";
    }

    public static string RenderBenchmarkNode(PTreeRepository repository, string nodeId)
    {
        if (!repository.NodeMap.TryGetValue(nodeId, out var node))
        {
            throw new PTreeException("节点不存在：" + nodeId);
        }
        var lines = new List<string> { "标杆 · " + nodeId + " · " + (node.Name ?? "?"), "", "由 ptree-cs 生成，禁止手改", "生成时间：" + NowText() };
        lines.Add("节点状态：" + node.Status + "；阶段：" + node.Phase + "；类型：" + node.Kind);
        var baseline = BaselineDate(repository);
        if (baseline is not null) lines.Add("差距时效基准日：" + baseline.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        lines.Add("");
        var bindings = BindingsOf(repository, nodeId);
        if (bindings.Count == 0)
        {
            lines.Add("该节点尚未绑定任何标杆。");
            lines.Add("");
            lines.Add("按标杆规范第九节：先提 bench_add + bench_bind（或说明为什么不需要，写进提案 reason）。");
            lines.Add("没有标杆的节点只能保证“不长歪”，保证不了“变好”。");
            lines.Add("");
            return string.Join("\n", lines) + "\n";
        }
        var benchmarkMap = repository.Benchmarks.ById;
        var rawBenchmarks = RawBenchmarks(repository);
        foreach (var binding in bindings)
        {
            benchmarkMap.TryGetValue(binding.Benchmark ?? "", out var benchmark);
            rawBenchmarks.TryGetValue(binding.Benchmark ?? "", out var raw);
            var level = binding.Level is >= 0 and <= 4 ? binding.Level.Value : (int?)null;
            var target = binding.Target is >= 0 and <= 4 ? binding.Target.Value : (int?)null;
            lines.Add("标杆 " + binding.Benchmark + " · " + (benchmark?.Name ?? "?"));
            lines.Add("");
            lines.Add("  类别：" + (benchmark?.Kind ?? "—"));
            lines.Add("  参考对象：" + (benchmark?.Reference ?? "—"));
            lines.Add("  为什么需要：" + RawString(raw, "why"));
            lines.Add("  维度：" + (Dimensions(raw) is { Count: > 0 } dims ? string.Join("；", dims) : "—"));
            lines.Add("  当前等级：L" + (level?.ToString() ?? "?") + " → 目标：L" + (target?.ToString() ?? "?") + "（" + (level ?? 0) + "/4）");
            lines.Add("");
            lines.Add("  五级阶梯（全文）：");
            foreach (var step in benchmark?.Levels.OrderBy(item => item.Level ?? 99) ?? Enumerable.Empty<BenchmarkLevelModel>())
            {
                lines.Add("    L" + step.Level + " " + (step.Name ?? "") + "：" + (step.Criteria ?? "") + (string.IsNullOrWhiteSpace(step.Evidence) ? "" : "（证据：" + step.Evidence + "）"));
            }
            lines.Add("");
            lines.Add("  差距：");
            if (binding.Gaps.Count == 0) lines.Add("    （无）");
            else
            {
                foreach (var gap in binding.Gaps)
                {
                    lines.Add("    " + (gap.Dimension ?? "?") + "：" + (gap.Gap ?? "") + " → 下一步：" + (gap.Next ?? "—") + "（opened " + (gap.Opened ?? "—") + "）");
                }
            }
            lines.Add("");
        }
        return string.Join("\n", lines) + "\n";
    }

    private static List<string> Header(PTreeRepository repository, ValidationPayload issues, string title)
    {
        return new List<string>
        {
            title,
            "",
            "由 ptree-cs 生成，禁止手改",
            "生成时间：" + NowText(),
            "tree_version：" + (repository.Manifest.TreeVersion ?? "?"),
            "校验状态：" + Banner(issues),
            "",
        };
    }

    private static string Banner(ValidationPayload issues) => issues.Errors.Count > 0
        ? "未通过校验（E=" + issues.Errors.Count + "，W=" + issues.Warnings.Count + "，详见 validate 输出）"
        : "通过校验（E=0，W=" + issues.Warnings.Count + "）";

    private static string NowText() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private sealed class Stats
    {
        public int Total;
        public Dictionary<string, int> ByKind = new(StringComparer.Ordinal);
        public Dictionary<string, int> ByStatus = new(StringComparer.Ordinal);
        public Dictionary<string, int> ByPhase = new(StringComparer.Ordinal);
        public Dictionary<string, int> ByDepartment = new(StringComparer.Ordinal);
    }

    private static Stats ComputeStats(PTreeRepository repository)
    {
        var stats = new Stats { Total = repository.Nodes.Count };
        foreach (var node in repository.Nodes)
        {
            if (node.Kind is not null) stats.ByKind[node.Kind] = stats.ByKind.GetValueOrDefault(node.Kind) + 1;
            if (node.Status is not null) stats.ByStatus[node.Status] = stats.ByStatus.GetValueOrDefault(node.Status) + 1;
            if (node.Phase is not null) stats.ByPhase[node.Phase] = stats.ByPhase.GetValueOrDefault(node.Phase) + 1;
            if (node.Kind == "department" && node.Id is not null)
            {
                var prefix = node.Id + ".";
                stats.ByDepartment[node.Id] = repository.Nodes.Count(item => item.Id is not null && item.Id.StartsWith(prefix, StringComparison.Ordinal));
            }
        }
        return stats;
    }

    private static string KindSummary(Stats stats) => string.Join(" · ", KindOrder.Where(kind => stats.ByKind.ContainsKey(kind)).Select(kind => kind + "=" + stats.ByKind[kind]));

    private static IEnumerable<string> PhaseIds(PTreeRepository repository)
    {
        var ids = repository.Manifest.Phases.Select(phase => phase.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToList();
        foreach (var phase in repository.Nodes.Select(node => node.Phase).Where(phase => !string.IsNullOrWhiteSpace(phase)).Select(phase => phase!))
        {
            if (!ids.Contains(phase, StringComparer.Ordinal)) ids.Add(phase);
        }
        return ids;
    }

    private static (List<NodeModel> Ordered, List<string> Leftover) TopoOrder(List<NodeModel> nodes, IReadOnlyDictionary<string, NodeModel> mapping)
    {
        var ids = nodes.Where(node => node.Id is not null).Select(node => node.Id!).ToList();
        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        var orderIndex = ids.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);
        var dependencies = ids.ToDictionary(id => id, id => (mapping.TryGetValue(id, out var node) ? node.DependsOn : new List<string>())
            .Where(dep => idSet.Contains(dep) && dep != id).ToList(), StringComparer.Ordinal);
        var remaining = new HashSet<string>(ids, StringComparer.Ordinal);
        var result = new List<NodeModel>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => !dependencies[id].Any(remaining.Contains)).OrderBy(id => orderIndex[id]).ToList();
            if (ready.Count == 0) break;
            foreach (var id in ready)
            {
                result.Add(mapping[id]);
                remaining.Remove(id);
            }
        }
        var leftover = remaining.OrderBy(id => orderIndex[id]).ToList();
        return (result, leftover);
    }

    private static List<string> BuildAsciiTree(PTreeRepository repository)
    {
        var lines = new List<string>();
        var mapping = repository.NodeMap;
        var order = mapping.Keys.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in repository.Nodes)
        {
            if (node.Id is null || node.Parent is null || !mapping.ContainsKey(node.Parent)) continue;
            if (!children.TryGetValue(node.Parent, out var list)) children[node.Parent] = list = new List<string>();
            list.Add(node.Id);
        }
        foreach (var list in children.Values) list.Sort((left, right) => order.GetValueOrDefault(left).CompareTo(order.GetValueOrDefault(right)));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string id, string prefix, bool isLast, bool isRoot)
        {
            visited.Add(id);
            var node = mapping[id];
            var label = id + " [" + node.Status + " " + node.Phase + "] " + node.Name;
            if (isRoot)
            {
                lines.Add(label);
            }
            else
            {
                lines.Add(prefix + (isLast ? "└── " : "├── ") + label);
            }
            var childPrefix = isRoot ? "" : prefix + (isLast ? "    " : "│   ");
            var kids = children.TryGetValue(id, out var list) ? list.Where(kid => !visited.Contains(kid) && mapping.ContainsKey(kid)).ToList() : new List<string>();
            for (var index = 0; index < kids.Count; index++) Walk(kids[index], childPrefix, index == kids.Count - 1, false);
        }
        if (repository.Manifest.Root is { } rootId && mapping.ContainsKey(rootId)) Walk(rootId, "", true, true);
        foreach (var id in mapping.Keys)
        {
            if (!visited.Contains(id)) Walk(id, "", true, true);
        }
        return lines;
    }

    private static (List<string> Upstream, List<string> Downstream) Connections(PTreeRepository repository, NodeModel node)
    {
        var upstream = new List<string>();
        var downstream = new List<string>();
        foreach (var contractId in node.Inputs)
        {
            if (!repository.Contracts.ById.TryGetValue(contractId, out var contract)) continue;
            var producers = contract.Producers.Count > 0 ? string.Join("、", contract.Producers) : "（无生产者）";
            upstream.Add(contractId + " ← " + producers);
        }
        foreach (var contractId in node.Outputs)
        {
            if (!repository.Contracts.ById.TryGetValue(contractId, out var contract)) continue;
            var consumers = contract.Consumers.Count > 0 ? string.Join("、", contract.Consumers) : (contract.ExternalConsumers ? "外部消费者" : "（无消费者）");
            downstream.Add(contractId + " → " + consumers);
        }
        return (upstream, downstream);
    }

    private static List<BindingModel> BindingsOf(PTreeRepository repository, string nodeId) =>
        repository.Bindings.Bindings.Where(binding => binding.Node == nodeId).ToList();

    private static List<string> RequiredNodes(PTreeRepository repository)
    {
        var result = new List<string>();
        if (repository.BenchmarksRaw is not RawMap root) return result;
        if (!root.TryGetValue("coverage", out var coverage) || coverage is not RawMap coverageMap) return result;
        if (!coverageMap.TryGetValue("required", out var required) || required is not List<object?> list) return result;
        foreach (var item in list)
        {
            if (item is RawMap map && map.TryGetValue("node", out var node) && node is string nodeId && !result.Contains(nodeId, StringComparer.Ordinal))
            {
                result.Add(nodeId);
            }
        }
        return result;
    }

    private static Dictionary<string, RawMap> RawBenchmarks(PTreeRepository repository)
    {
        var result = new Dictionary<string, RawMap>(StringComparer.Ordinal);
        if (repository.BenchmarksRaw is not RawMap root) return result;
        if (!root.TryGetValue("benchmarks", out var benchmarks) || benchmarks is not List<object?> list) return result;
        foreach (var item in list)
        {
            if (item is RawMap map && map.TryGetValue("id", out var id) && id is string benchmarkId)
            {
                result[benchmarkId] = map;
            }
        }
        return result;
    }

    private static List<string> Dimensions(RawMap? benchmark)
    {
        var result = new List<string>();
        if (benchmark is null || !benchmark.TryGetValue("dimensions", out var dimensions) || dimensions is not List<object?> list) return result;
        foreach (var item in list)
        {
            if (item is not RawMap map) continue;
            var id = map.TryGetValue("id", out var idValue) ? idValue as string : null;
            var name = map.TryGetValue("name", out var nameValue) ? nameValue as string : null;
            if (id is not null) result.Add(id + " " + (name ?? ""));
        }
        return result;
    }

    private static string RawString(RawMap? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) && value is string text && text.Length > 0 ? text : "—";

    private static DateTime? BaselineDate(PTreeRepository repository)
    {
        if (repository.Manifest.Updated is null) return null;
        return DateTime.TryParseExact(repository.Manifest.Updated, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static int? OldestGapDays(PTreeRepository repository, BindingModel binding)
    {
        var baseline = BaselineDate(repository);
        if (baseline is null) return null;
        var days = binding.Gaps.Select(gap => gap.Opened)
            .Where(opened => opened is not null && DateTime.TryParseExact(opened, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .Select(opened => (int)Math.Max(0, (baseline.Value - DateTime.ParseExact(opened!, "yyyy-MM-dd", CultureInfo.InvariantCulture)).Days))
            .ToList();
        return days.Count > 0 ? days.Max() : null;
    }

    private sealed record ArchiveInfo(string Name, string Title, List<string> Removed);

    private static List<ArchiveInfo> Archives(PTreeRepository repository)
    {
        var result = new List<ArchiveInfo>();
        var directory = Path.Combine(repository.Root, "00-程序树", "变更", "历史");
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.GetFiles(directory).Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)).OrderByDescending(path => path, StringComparer.Ordinal))
        {
            var removed = new List<string>();
            var title = "";
            try
            {
                if (RawYaml.Parse(File.ReadAllText(path)) is RawMap map)
                {
                    if (map.TryGetValue("removed_nodes", out var removedValue) && removedValue is List<object?> list)
                    {
                        removed.AddRange(list.OfType<string>());
                    }
                    if (map.TryGetValue("proposal", out var proposal) && proposal is RawMap proposalMap && proposalMap.TryGetValue("proposal", out var proposalTitle) && proposalTitle is string text)
                    {
                        title = text;
                    }
                }
            }
            catch (Exception)
            {
                // 归档解析失败不阻断生成物
            }
            result.Add(new ArchiveInfo(Path.GetFileName(path), title, removed));
        }
        return result;
    }

    private static List<string> PendingProposals(PTreeRepository repository)
    {
        var result = new List<string>();
        var directory = Path.Combine(repository.Root, "00-程序树", "变更");
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.GetFiles(directory).Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)).OrderBy(path => path, StringComparer.Ordinal))
        {
            var title = "";
            try
            {
                if (RawYaml.Parse(File.ReadAllText(path)) is RawMap map && map.TryGetValue("proposal", out var value) && value is string text) title = text;
            }
            catch (Exception)
            {
                title = "（无法解析）";
            }
            result.Add(Path.GetFileName(path) + (string.IsNullOrEmpty(title) ? "" : "（" + title + "）"));
        }
        return result;
    }

    private static string? SpecForNode(PTreeRepository repository, NodeModel node)
    {
        if (string.IsNullOrWhiteSpace(node.Spec)) return null;
        var pathPart = node.Spec.Split('#')[0].Trim();
        if (pathPart.Length == 0) return null;
        var path = Path.Combine(repository.Root, pathPart.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) return path;
        if (!string.IsNullOrEmpty(node.Home))
        {
            var candidate = Path.Combine(repository.Root, node.Home.Replace('/', Path.DirectorySeparatorChar), "SPEC.md");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static Dictionary<string, string> ReadSpecSections(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return result;
        }
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var match = Regex.Match(lines[index], "^#{2}\\s+(.+?)\\s*$");
            if (!match.Success) continue;
            var title = match.Groups[1].Value.Trim();
            var content = new List<string>();
            for (var sub = index + 1; sub < lines.Length; sub++)
            {
                if (Regex.IsMatch(lines[sub], "^#{1,2}\\s+")) break;
                content.Add(lines[sub]);
            }
            result[title] = string.Join("\n", content).Trim();
        }
        return result;
    }

    private static string Truncate(string text, int limit = 240)
    {
        var normalized = Regex.Replace(text, "\\s+", " ").Trim();
        return normalized.Length <= limit ? normalized : normalized[..limit] + "……";
    }

    private static string Shorten(string text, int limit)
    {
        var value = text.Trim().Replace("\n", " ");
        return value.Length <= limit ? value : value[..limit] + "…";
    }

    private static string JoinOr(List<string> values) => values.Count > 0 ? string.Join("、", values) : "无";

    private static void Write(PTreeRepository repository, string name, string content)
    {
        var directory = Path.Combine(repository.Root, GeneratedDir.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(Path.Combine(directory, name), content, new UTF8Encoding(false));
    }
}
