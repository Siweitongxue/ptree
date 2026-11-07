using System.Globalization;
using System.Text;

namespace PTree.Core;

/// <summary>
/// 读节点与树状态的纯文本渲染。depth=1 给 9 行以内的纯文本卡（短介绍、契约、依赖、相关网格、边、文件、说明）；
/// depth>=2 与 depth=0 统一成「首行汇总 + 节点： + 边：」；format=full 走原 JSON。
/// </summary>
public static class NodeCards
{
    public const int MaxDepth = 8;
    public const int MaxExpandNodes = 200;
    public const int MaxExpandEdges = 400;
    public const int MaxCardBytes = 800;
    public const int MaxCardLines = 9;
    public const int MaxStatusBytes = 700;
    public const int MaxResponsibilityChars = 50;
    public const string EntryPath = "00-程序树/README.txt";

    private static readonly string[] StatusOrder = { "未完成", "已完成", "已验证", "退役" };

    /// <summary>读本生成日期，卡片与树状态共用。</summary>
    public static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string RenderNode(PTreeRepository repository, string id, int depth)
    {
        if (!repository.NodeMap.TryGetValue(id, out var node)) throw new PTreeException("节点不存在：" + id);
        return depth == 1 ? RenderCard(repository, node) : RenderGraph(repository, id, depth);
    }

    // ------------------------------------------------------------------
    // depth=1：10 行以内的判断卡
    // ------------------------------------------------------------------

    private static string RenderCard(PTreeRepository repository, NodeModel node)
    {
        var id = node.Id ?? "";
        var lines = new List<string>();

        var head = new StringBuilder();
        head.Append(id).Append(' ').Append(Cap(node.Name ?? "", 30));
        // 预设占位节点要一眼看得出来：状态后面直接标「·预设」，不另起一行
        if (!string.IsNullOrWhiteSpace(node.Status))
            head.Append(" [").Append(node.Preset ? node.Status + "·预设" : node.Status).Append(']');
        head.Append(" L").Append(node.Layer ?? 0);
        if (!string.IsNullOrWhiteSpace(node.Phase)) head.Append(' ').Append(Cap(node.Phase!, 12));
        head.Append(" 父").Append(string.IsNullOrWhiteSpace(node.Parent) ? "-" : node.Parent);
        head.Append(" 子").Append(repository.Nodes.Count(other => other.Parent == id));
        lines.Add(head.ToString());

        lines.Add("短 " + ReadShortIntro(repository, id));

        var upstream = repository.Contracts.Contracts
            .Where(contract => contract.Consumers.Contains(id, StringComparer.Ordinal))
            .OrderBy(contract => contract.Id, StringComparer.Ordinal).ToList();
        if (upstream.Count > 0)
            lines.Add("吃 " + JoinLimited(upstream.Select(contract => ContractItem(contract, true, 40)).ToList(), "; ", 260));

        var downstream = repository.Contracts.Contracts
            .Where(contract => contract.Producers.Contains(id, StringComparer.Ordinal))
            .OrderBy(contract => contract.Id, StringComparer.Ordinal).ToList();
        if (downstream.Count > 0)
            lines.Add("给 " + JoinLimited(downstream.Select(contract => ContractItem(contract, false, 40)).ToList(), "; ", 260));

        var depends = node.DependsOn.Where(value => repository.NodeMap.ContainsKey(value)).Take(4).ToList();
        var dependedBy = repository.Nodes.Count(other => other.Id != id && other.DependsOn.Contains(id, StringComparer.Ordinal));
        var blocks = repository.GroupsForNode(id).Where(groupId => !IsAutoGroup(groupId)).Take(3).ToList();
        var dependsLine = depends.Count > 0 || dependedBy > 0
            ? "依 " + (depends.Count == 0 ? "0" : string.Join(",", depends)) + " 被依 " + dependedBy
            : "";
        if (blocks.Count > 0)
        {
            var blockText = " 块 " + string.Join(",", blocks);
            lines.Add(dependsLine.Length == 0 ? blockText.TrimStart() : dependsLine + blockText);
        }
        else if (dependsLine.Length > 0)
        {
            lines.Add(dependsLine);
        }

        var store = TreeNodeStore(repository);
        var planAll = NodeTools.RelatedPlanning(repository, id, store).Select(IdOf).Where(value => value is not null).ToList();
        var plan = planAll.Take(3).ToList();
        var art = NodeTools.RelatedTree(repository, "美术树", id, store).Select(IdOf).Where(value => value is not null).Take(3).ToList();
        var ui = NodeTools.RelatedTree(repository, "UI树", id, store).Select(IdOf).Where(value => value is not null).Take(3).ToList();
        var affected = ImpactIds(repository, id, downstream);
        if (plan.Count > 0 || art.Count > 0 || ui.Count > 0 || affected.Count > 0)
            lines.Add("策 " + string.Join(",", plan!) + (planAll.Count > plan.Count ? "+" + (planAll.Count - plan.Count) : "") +
                      " 美 " + string.Join(",", art!) + " UI " + string.Join(",", ui!) + " 波及 " + affected.Count);

        // 与「策」行同口径：只列前 3 条，剩下的补 +N，不悄悄丢联系
        var allEdges = Links(repository.ManifestRaw)
            .Where(link => StringOf(link, "from") == id || StringOf(link, "to") == id)
            .Select(link => (StringOf(link, "from") ?? "?") + " -[" + (StringOf(link, "type") ?? "引用") + "]-> " + (StringOf(link, "to") ?? "?"))
            .ToList();
        if (allEdges.Count > 0)
        {
            // 先按「最多 3 条 + 字节预算」挑能放下的，剩下的补 +N：不能被后面的 110 字截断把 +N 吃掉
            var shown = new List<string>();
            foreach (var edge in allEdges.Take(3))
            {
                var candidate = shown.Count == 0 ? edge : string.Join(",", shown) + "," + edge;
                if (shown.Count > 0 && Encoding.UTF8.GetByteCount(candidate) + 12 > 96) break;
                shown.Add(edge);
            }
            var hidden = allEdges.Count - shown.Count;
            lines.Add("边 " + string.Join(",", shown) + (hidden > 0 ? "+" + hidden : ""));
        }

        if (node.Files.Count > 0) lines.Add("文件 " + string.Join(",", node.Files.Take(2)));
        lines.Add("详 " + repository.IntroRel(id));

        var text = string.Join("\n", lines.Select(line => Cap(line, 110)));
        return FitUtf8(text, MaxCardBytes);
    }

    /// <summary>读短介绍；文件不存在或为空时返回「未写」。</summary>
    public static string ReadShortIntro(PTreeRepository repository, string id)
    {
        var path = Path.Combine(repository.Root, "00-程序树", "短介绍", id + ".txt");
        if (!File.Exists(path)) return "未写";
        try
        {
            var text = File.ReadAllText(path).Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length == 0 ? "未写" : Cap(text, 80);
        }
        catch (Exception) { return "未写"; }
    }

    /// <summary>阅读阶梯：一句话版，给 AI 一次调用就知道先粗后细怎么走。</summary>
    public static string RenderGuide()
    {
        return string.Join("\n", new[]
        {
            "程序树阅读阶梯（先粗后细，能用上层解决就别往下走）",
            "0 常检 tree_guard：只报 文件消失 / 登记目录不存在 / 已完成却没有实现 三类",
            "1 块   tree_map mode=blocks：项目分成哪几块、每块几个成员、一行简介（默认）",
            "2 名称 tree_map mode=names：全树名称 + 子数 + 依赖，96 节点约 6.5KB",
            "3 简介 tree_map mode=summary：名称 + 一行短介绍，约 12.6KB",
            "4 组内 tree_read_group id=<组> mode=names|summary：某块里有什么，summary 带用法",
            "5 单点 tree_read_node id=<节点>：9 行以内卡；不够再 format=full 或 depth=0",
            "6 条目 策划 D01 / 美术 M01 / UI U01 / 全文件 F01 用同一个 tree_read_node",
            "记：卡上 …+N 表示还有内容没显示；实现细节在 00-程序树/说明/<id>.txt，不在简介里",
            "全文 00-程序树/规范/阅读阶梯_v1.txt；技能 program-tree-reading",
        });
    }

    /// <summary>最粗一层：功能块与部门分区，只给成员数与一行简介，不给成员清单。</summary>
    public static string RenderBlocks(PTreeRepository repository)
    {
        var blocks = repository.Groups.Where(group => !IsAutoGroup(group.Id))
            .OrderBy(group => group.Id, StringComparer.Ordinal).ToList();
        var departments = repository.Nodes.Where(node => node.Kind == "department" && node.Id is { Length: > 0 })
            .OrderBy(node => node.Id, StringComparer.Ordinal).ToList();
        var lines = new List<string>
        {
            "程序树 " + (repository.Manifest.TreeVersion ?? "?") + " · " + repository.Nodes.Count + " 节点 · 功能块 " +
            blocks.Count + " + 部门 " + departments.Count + " · " + Today,
        };
        foreach (var block in blocks)
        {
            lines.Add("块 " + block.Id + " " + Cap(block.Name ?? "", 20) + " · " + block.Members.Count + " 成员 · " +
                      Cap(block.Description ?? "未写简介", 40));
        }
        foreach (var department in departments)
        {
            var direct = repository.Nodes.Count(node => node.Parent == department.Id);
            lines.Add("部 " + department.Id + " " + Cap(department.Name ?? "", 20) + " · " + direct + " 直属成员");
        }
        lines.Add("下一层 tree_map mode=names（名称）| mode=summary（名称+简介）| tree_read_group id=<组>");
        return string.Join("\n", lines);
    }

    /// <summary>组内视图：names 只列成员名，summary 再带一行简介与用法（首个代码文件或 无代码）。</summary>
    public static string RenderGroupMembers(PTreeRepository repository, GroupModel group, string? mode)
    {
        var summary = string.Equals(mode, "summary", StringComparison.OrdinalIgnoreCase);
        var id = group.Id ?? "?";
        var lines = new List<string>
        {
            "组 " + id + " " + Cap(group.Name ?? "", 24) + " · " + group.Members.Count + " 成员 · " +
            (string.IsNullOrWhiteSpace(group.Description) ? "未写简介" : Cap(group.Description!, 40)),
        };
        var shown = 0;
        foreach (var member in group.Members)
        {
            if (shown >= MaxExpandNodes) break;
            shown++;
            var node = repository.NodeMap.TryGetValue(member, out var found) ? found : null;
            if (node is null)
            {
                var child = repository.Groups.FirstOrDefault(candidate => candidate.Id == member);
                lines.Add("组 " + member + " " + Cap(child?.Name ?? "", 20) + " · " + (child?.Members.Count ?? 0) + " 成员");
                continue;
            }
            var line = node.Id + " " + Cap(node.Name ?? "", 18);
            if (!string.IsNullOrWhiteSpace(node.Status)) line += " [" + node.Status + "]";
            if (node.Preset) line += "(预设)";
            if (summary) line += " · " + Cut(ReadShortIntro(repository, node.Id!), 40) + " · 用 " + UsageOf(node);
            lines.Add(line);
        }
        if (group.Members.Count > shown) lines.Add("⚠ 只列前 " + shown + " 个成员");
        if (!summary) lines.Add("加简介与用法：tree_read_group id=" + id + " mode=summary");
        return string.Join("\n", lines);
    }

    private static string UsageOf(NodeModel node)
    {
        if (node.Files.Count == 0) return "无代码";
        return Cap(node.Files[0], 44);
    }

    /// <summary>
    /// 整棵树的粗读：mode=names 只给「名称 + 联系」，mode=summary 再加一行短介绍。
    /// 两条模式都不列契约明细与文件，逐点细节用 read_node。
    /// </summary>
    public static string RenderMap(PTreeRepository repository, string? mode)
    {
        var summary = !string.Equals(mode, "names", StringComparison.OrdinalIgnoreCase);
        var nodes = repository.Nodes.Where(node => node.Id is { Length: > 0 }).ToList();
        var children = new Dictionary<string, List<NodeModel>>(StringComparer.Ordinal);
        var roots = new List<NodeModel>();
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.Parent) && repository.NodeMap.ContainsKey(node.Parent))
            {
                if (!children.TryGetValue(node.Parent!, out var list)) children[node.Parent!] = list = new List<NodeModel>();
                list.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        var builder = new StringBuilder();
        builder.Append("程序树 ").Append(repository.Manifest.TreeVersion ?? "?")
            .Append(" · ").Append(nodes.Count).Append(" 节点 · ").Append(Today)
            .Append(summary ? " · 名称+短介绍+联系\n" : " · 名称+联系\n");
        var shown = 0;
        void Walk(NodeModel node, int depth)
        {
            if (shown >= MaxExpandNodes) return;
            shown++;
            var id = node.Id!;
            builder.Append(new string(' ', depth * 2)).Append(id).Append(' ').Append(Cap(node.Name ?? "", 24));
            // 预设占位只在这里加一个记号，不额外占行
            if (node.Preset) builder.Append("(预设)");
            if (!string.IsNullOrWhiteSpace(node.Status)) builder.Append(" [").Append(node.Status).Append(']');
            var kids = children.TryGetValue(id, out var list) ? list.Count : 0;
            if (kids > 0) builder.Append(" 子").Append(kids);
            // 依赖父节点不另写：缩进已经表达了这层联系
            var depends = node.DependsOn
                .Where(value => repository.NodeMap.ContainsKey(value) && value != id && value != node.Parent).ToList();
            if (depends.Count > 0)
            {
                var head = depends.Count <= 2 ? string.Join(",", depends) : string.Join(",", depends.Take(2)) + "+" + (depends.Count - 2);
                builder.Append(" 依").Append(Cap(head, 44));
            }
            if (summary)
            {
                var intro = ReadShortIntro(repository, id);
                if (intro != "未写") builder.Append(" · ").Append(Cut(intro, 24));
            }
            builder.Append('\n');
            if (!children.TryGetValue(id, out var kidsList)) return;
            foreach (var child in kidsList) Walk(child, depth + 1);
        }
        foreach (var root in roots) Walk(root, 0);
        var blocks = repository.Groups
            .Where(group => !IsAutoGroup(group.Id))
            .OrderBy(group => group.Id, StringComparer.Ordinal)
            .ToList();
        if (blocks.Count > 0)
        {
            builder.Append("块 ").Append(blocks.Count).Append(" 个：");
            builder.Append(JoinLimited(blocks.Select(group =>
                (group.Id ?? "?") + " " + Cap(group.Name ?? "", 16) + "(" + group.Members.Count + ")").ToList(), "；", 600));
            builder.Append('\n');
        }
        // 网格条目不在程序树里，但也是阅读项：只报覆盖数字，条目明细走 read_node
        var gridItems = GridReadings.Items(TreeStore.Load(repository.Root), repository.Root);
        if (gridItems.Count > 0)
        {
            var covered = gridItems.Count(item => File.Exists(GridReadings.ShortPath(repository.Root, item.Id)));
            builder.Append("网格条目 ").Append(gridItems.Count).Append(" 条，短介绍 ")
                .Append(covered).Append('/').Append(gridItems.Count).Append('\n');
        }
        if (nodes.Count > shown)
            builder.Append("⚠ 只列前 ").Append(shown).Append(" 个，用 tree_find 精确找\n");
        return builder.ToString().TrimEnd('\n');
    }

    private static string FitUtf8(string text, int maxBytes)
    {
        while (Encoding.UTF8.GetByteCount(text) > maxBytes && text.Length > 0)
            text = text[..^1];
        return text.TrimEnd('\n');
    }

    private static string EvaluationLine(EvaluationSpace evaluation)
    {
        if (!evaluation.Has) return "评 无（H07 要求编码前先写）";
        var judgment = Cap(evaluation.Judgment ?? "未写判断", 8);
        var position = Cap(PositionText(evaluation.Position), 16);
        var missing = Cut(string.IsNullOrWhiteSpace(evaluation.Missing) ? "未写" : evaluation.Missing!, 26);
        return "评 " + judgment + "·位置" + position + "·缺" + missing;
    }

    /// <summary>功能块卡：不是节点，但也要能一眼看出它管哪些节点、介绍在哪。</summary>
    public static string RenderGroupCard(PTreeRepository repository, GroupModel group)
    {
        var id = group.Id ?? "?";
        var lines = new List<string>
        {
            id + " " + Cap(group.Name ?? "", 30) + " [块] 成员 " + group.Members.Count +
            (string.IsNullOrWhiteSpace(group.Parent) ? "" : " 父" + group.Parent),
            "短 " + (string.IsNullOrWhiteSpace(group.Description) ? "未写" : Cap(group.Description!, 60)),
        };
        var members = group.Members.Take(8).ToList();
        if (members.Count > 0)
            lines.Add("成 " + JoinLimited(members, ",", 260) + (group.Members.Count > members.Count ? " +" + (group.Members.Count - members.Count) : ""));
        var introPath = "00-程序树/说明/" + id + ".txt";
        lines.Add(File.Exists(Path.Combine(repository.Root, "00-程序树", "说明", id + ".txt")) ? "详 " + introPath : "详 未写（R38 要求功能块有详细介绍）");
        return FitUtf8(string.Join("\n", lines), MaxCardBytes);
    }

/// <summary>网格条目的卡：不是节点也不是功能块，读它的行字段、短介绍与来源行号。</summary>
    public static string RenderGridCard(PTreeRepository repository, GridReading reading)
    {
        var fields = reading.Fields;
        var names = reading.FieldNames;
        var body = new List<string>
        {
            reading.Id + " " + Cap(reading.Name, 30) + " [" + reading.Kind + "]",
            "短 " + ReadShortIntro(repository, reading.Id),
        };
        // 文档列是「缺口可见性」的入口：单独一行，且先占位，避免被下面的字节预算从尾部裁掉
        var docIndex = names.ToList().FindIndex(name => name == "文档");
        if (docIndex > 0 && docIndex < fields.Count && fields[docIndex].Length > 0)
            body.Add(Cap("文档 " + fields[docIndex], 90));
        var fieldLines = 0;
        for (var index = 1; index < fields.Count && fieldLines < 4; index++)
        {
            var value = fields[index];
            if (string.IsNullOrWhiteSpace(value)) continue;
            var name = index < names.Count ? names[index] : "字段";
            if (name == "名称") continue;
            body.Add(Cap(name + " " + value, 90));
            fieldLines++;
        }
        var tail = "源 " + reading.RelPath + (reading.LineNo > 0 ? ":" + reading.LineNo : "");
        // 字段多的行（如策划的对应程序）会把字节预算吃掉：从尾部丢字段，先保住来源行与短介绍
        while (body.Count > 3 && Encoding.UTF8.GetByteCount(string.Join("\n", body) + "\n" + tail) > MaxCardBytes - 20)
            body.RemoveAt(body.Count - 1);
        body.Add(tail);
        return FitUtf8(string.Join("\n", body), MaxCardBytes);
    }

    /// <summary>group.all 与 group.dept-* 是自动派生的部门组，不是人建的功能块，不进卡。</summary>
    private static bool IsAutoGroup(string? id) =>
        string.IsNullOrEmpty(id) || id == "group.all" || id.StartsWith("group.dept-", StringComparison.Ordinal);

    /// <summary>按标点断句截断，避免把「双内核并存」切成「双内核并」。</summary>
    private static string Cut(string text, int max)
    {
        var value = text.Trim();
        if (value.Length <= max) return value;
        var window = value[..max];
        var stop = window.LastIndexOfAny(new[] { '，', '、', '；', '。', '：', ',', ';' });
        return stop >= 8 ? window[..stop] + "…" : window + "…";
    }

    private static string PositionText(string? position)
    {
        var value = position?.Trim();
        if (string.IsNullOrEmpty(value)) return "未写";
        if (!value.StartsWith("应移到", StringComparison.Ordinal)) return value;
        var target = value[3..].Trim();
        return target.Length == 0 ? "未写" : target;
    }

    /// <summary>L4：首个文件所在目录的实况（存在与否、实际文件数），再列最多两个登记路径，缺失的加 ✗。</summary>
    private static string PositionLine(PTreeRepository repository, NodeModel node, List<string> issues)
    {
        var files = node.Files;
        if (files.Count == 0)
        {
            issues.Add("地址未登记");
            return "位 未登记 ✗ 0 文件";
        }
        var directory = DirectoryOf(files[0]);
        var directoryText = directory.Length == 0 ? "仓库根" : directory;
        var directoryAbs = directory.Length == 0
            ? repository.Root
            : Path.Combine(repository.Root, ToSystem(directory));
        var exists = Directory.Exists(directoryAbs);
        var actual = 0;
        if (exists)
        {
            var registered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var other in repository.Nodes)
                foreach (var file in other.Files)
                    if (string.Equals(DirectoryOf(file), directory, StringComparison.Ordinal)) registered.Add(NameOf(file));
            foreach (var path in Directory.EnumerateFiles(directoryAbs))
            {
                actual++;
            }
        }
        var missing = files.Count(file => !File.Exists(Path.Combine(repository.Root, ToSystem(file))));
        if (missing > 0) issues.Add("文件不存在 " + missing);
        var shown = JoinLimited(files.Take(2)
            .Select(file => DisplayPath(file, directory) + (File.Exists(Path.Combine(repository.Root, ToSystem(file))) ? "" : "✗"))
            .ToList(), " ", 90);
        return "位 " + Cap(directoryText, 34) + " " + (exists ? "✓" : "✗") + " " + actual + " 文件 " + shown;
    }

    private static string ContractItem(ContractModel contract, bool upstream, int capBytes)
    {
        var name = string.IsNullOrWhiteSpace(contract.Name) ? (contract.Id ?? "?") : contract.Name!;
        var peers = upstream
            ? (contract.Producers.Count == 0 ? new List<string> { "外部" } : contract.Producers)
            : (contract.Consumers.Count == 0 ? new List<string> { "外部" } : contract.Consumers);
        var head = Cap(name, 10) + (upstream ? "←" : "→");
        var budget = Math.Max(8, capBytes - Encoding.UTF8.GetByteCount(head));
        return head + JoinLimited(peers.Take(2).ToList(), ",", budget);
    }

    private static List<string> ImpactIds(PTreeRepository repository, string id, List<ContractModel> downstream)
    {
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in repository.Nodes)
        {
            if (other.Id is { Length: > 0 } otherId && otherId != id &&
                other.DependsOn.Contains(id, StringComparer.Ordinal) && seen.Add(otherId))
                targets.Add(otherId);
        }
        foreach (var contract in downstream)
        {
            foreach (var consumer in contract.Consumers)
            {
                if (consumer == "外部" || consumer == id || !repository.NodeMap.ContainsKey(consumer)) continue;
                if (seen.Add(consumer)) targets.Add(consumer);
            }
        }
        return targets;
    }

    private static string? IdOf(object? item) =>
        item is Dictionary<string, object?> map && map.TryGetValue("id", out var id) ? id as string : null;

    private static string DirectoryOf(string path)
    {
        var trimmed = path.Trim();
        var index = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return index <= 0 ? "" : trimmed[..index];
    }

    private static string DisplayPath(string path, string directory)
    {
        var prefix = directory.Length == 0 ? "" : directory + "/";
        return prefix.Length > 0 && path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    private static string NameOf(string path)
    {
        var index = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string ToSystem(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private static string Cap(string text, int maxChars)
    {
        var value = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return value.Length <= maxChars ? value : value[..maxChars];
    }

    /// <summary>按字节预算拼接条目，只在条目边界截断，避免把节点 id 切断。</summary>
    private static string JoinLimited(List<string> items, string separator, int capBytes)
    {
        var text = "";
        foreach (var item in items)
        {
            var candidate = text.Length == 0 ? item : text + separator + item;
            if (text.Length > 0 && Encoding.UTF8.GetByteCount(candidate) > capBytes) break;
            text = candidate;
            if (Encoding.UTF8.GetByteCount(text) > capBytes) break;
        }
        return text.Length == 0 ? Cap(items.FirstOrDefault() ?? "", 20) : text;
    }

    // ------------------------------------------------------------------
    // depth>=2 / depth=0：首行汇总 + 节点表 + 边表（同一套格式）
    // ------------------------------------------------------------------

    private static string RenderGraph(PTreeRepository repository, string id, int depth)
    {
        var edges = BuildEdges(repository);
        return depth == 0 ? RenderWhole(repository, id, edges) : RenderNeighborhood(repository, id, depth, edges);
    }

    private static string RenderNeighborhood(PTreeRepository repository, string id, int depth, List<(string From, string To, string Type)> edges)
    {
        var limit = Math.Min(depth, MaxDepth);
        var order = new List<string> { id };
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var frontier = new List<string> { id };
        for (var hop = 0; hop < limit && frontier.Count > 0; hop++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                foreach (var edge in edges)
                {
                    var other = edge.From == current ? edge.To : edge.To == current ? edge.From : null;
                    if (other is null || !seen.Add(other)) continue;
                    order.Add(other);
                    next.Add(other);
                }
            }
            frontier = next;
        }

        var totalNodes = order.Count;
        var listed = order.Take(MaxExpandNodes).ToList();
        var expanded = new HashSet<string>(order, StringComparer.Ordinal);
        var kept = new HashSet<string>(listed, StringComparer.Ordinal);
        var totalEdges = edges.Count(edge => expanded.Contains(edge.From) && expanded.Contains(edge.To));
        var keptEdges = edges.Where(edge => kept.Contains(edge.From) && kept.Contains(edge.To)).Take(MaxExpandEdges).ToList();
        var truncatedNodes = totalNodes - listed.Count;

        var builder = new StringBuilder();
        builder.Append("# ").Append(id).Append(" 深").Append(depth).Append('：')
            .Append(totalNodes).Append(" 节点 ").Append(totalEdges).Append(" 边\n");
        AppendTables(builder, repository, listed, keptEdges);
        if (truncatedNodes > 0)
            builder.Append("⚠ 截断 ").Append(truncatedNodes).Append(" 节点未列，用 depth=1 逐点展开\n");
        return builder.ToString().TrimEnd('\n');
    }

    private static string RenderWhole(PTreeRepository repository, string id, List<(string From, string To, string Type)> edges)
    {
        var nodes = repository.Nodes.Where(node => node.Id is { Length: > 0 }).ToList();
        var listed = nodes.Take(MaxExpandNodes).ToList();
        var keptIds = new HashSet<string>(listed.Select(node => node.Id!), StringComparer.Ordinal);
        var keptEdges = edges.Where(edge => keptIds.Contains(edge.From) && keptIds.Contains(edge.To)).Take(MaxExpandEdges).ToList();
        var truncatedNodes = nodes.Count - listed.Count;

        var builder = new StringBuilder();
        builder.Append("# ").Append(id).Append(" 深0：").Append(nodes.Count).Append(" 节点 ")
            .Append(edges.Count).Append(" 边\n");
        AppendTables(builder, repository, listed.Select(node => node.Id!).ToList(), keptEdges);
        if (truncatedNodes > 0)
            builder.Append("⚠ 截断 ").Append(truncatedNodes).Append(" 节点未列，用 depth=1 逐点展开\n");
        return builder.ToString().TrimEnd('\n');
    }

    private static void AppendTables(StringBuilder builder, PTreeRepository repository, List<string> order,
        List<(string From, string To, string Type)> keptEdges)
    {
        builder.Append("节点：\n");
        foreach (var nodeId in order)
        {
            var node = repository.NodeMap[nodeId];
            builder.Append(nodeId).Append(' ').Append(node.Name ?? "");
            // 与 map 同口径：预设占位跟在名字后面，不另占行
            if (node.Preset) builder.Append("(预设)");
            if (!string.IsNullOrWhiteSpace(node.Status)) builder.Append(" [").Append(node.Status).Append(']');
            builder.Append('\n');
        }
        builder.Append("边：\n");
        foreach (var edge in keptEdges)
            builder.Append(edge.From).Append(" -[").Append(edge.Type).Append("]-> ").Append(edge.To).Append('\n');
    }

    /// <summary>关键节点：按度数降序取前 N；度数相同按 id 排。</summary>
    private static List<string> KeyNodes(List<NodeModel> nodes, List<(string From, string To, string Type)> edges, int count)
    {
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
            if (node.Id is { Length: > 0 } nodeId) degree[nodeId] = 0;
        foreach (var edge in edges)
        {
            if (degree.ContainsKey(edge.From)) degree[edge.From]++;
            if (degree.ContainsKey(edge.To)) degree[edge.To]++;
        }
        return degree.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(count).Select(pair => pair.Key).ToList();
    }

    private static List<(string From, string To, string Type)> BuildEdges(PTreeRepository repository)
    {
        var edges = new List<(string From, string To, string Type)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string from, string to, string type)
        {
            if (from.Length == 0 || to.Length == 0 || from == to) return;
            if (!seen.Add(from + "\u0000" + to + "\u0000" + type)) return;
            edges.Add((from, to, type));
        }

        foreach (var node in repository.Nodes)
        {
            if (node.Id is not { Length: > 0 } nodeId) continue;
            if (!string.IsNullOrWhiteSpace(node.Parent) && repository.NodeMap.ContainsKey(node.Parent)) Add(node.Parent!, nodeId, "层级");
        }
        foreach (var contract in repository.Contracts.Contracts)
        {
            var consumers = contract.Consumers.Where(value => repository.NodeMap.ContainsKey(value)).ToList();
            if (consumers.Count == 0) continue;
            foreach (var producer in contract.Producers.Where(value => repository.NodeMap.ContainsKey(value)))
                foreach (var consumer in consumers) Add(producer, consumer, "契约");
        }
        foreach (var node in repository.Nodes)
        {
            if (node.Id is not { Length: > 0 } nodeId) continue;
            foreach (var dependency in node.DependsOn)
                if (repository.NodeMap.ContainsKey(dependency)) Add(nodeId, dependency, "依赖");
        }
        foreach (var link in Links(repository.ManifestRaw))
        {
            var from = StringOf(link, "from");
            var to = StringOf(link, "to");
            if (from is null || to is null) continue;
            Add(from, to, StringOf(link, "type") ?? "引用");
        }
        return edges;
    }

    // ------------------------------------------------------------------
    // tree_status 卡
    // ------------------------------------------------------------------

    public static string RenderStatus(PTreeRepository repository, ValidationPayload validation)
    {
        var nodes = repository.Nodes;
        var lines = new List<string>();
        var statusTally = nodes.GroupBy(node => node.Status ?? "?")
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var statusCounts = StatusOrder
            .Select(status => status + " " + (statusTally.TryGetValue(status, out var tally) ? tally : 0))
            .Concat(statusTally.Where(pair => !StatusOrder.Contains(pair.Key))
                .Select(pair => pair.Key + " " + pair.Value))
            .ToList();
        var head = new StringBuilder();
        var presetCount = nodes.Count(node => node.Preset);
        var linkCount = Links(repository.ManifestRaw).Count;
        head.Append("程序树 ").Append(repository.Manifest.TreeVersion ?? "?").Append(" 节点 ").Append(nodes.Count)
            .Append(" 预设 ").Append(presetCount).Append(" 联系 ").Append(linkCount);
        if (statusCounts.Count > 0) head.Append(" 状态 ").Append(string.Join(";", statusCounts));
        head.Append(" · ").Append(Today);
        lines.Add(head.ToString());

        var layers = nodes.GroupBy(node => node.Layer ?? 0).OrderBy(group => group.Key)
            .Select(group => "L" + group.Key + " " + group.Count()).ToList();
        if (layers.Count > 0) lines.Add("层 " + string.Join(";", layers));

        var departments = nodes.Where(node => node.Kind == "department" && node.Id is { Length: > 0 })
            .Select(node => node.Id!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(departmentId => departmentId + " " + nodes.Count(other => other.Id is { Length: > 0 } otherId &&
                otherId.StartsWith(departmentId + ".", StringComparison.Ordinal)))
            .ToList();
        if (departments.Count > 0) lines.Add("部门 " + JoinCapped(departments, 12));

        var grids = GridCounts(repository);
        if (grids.Count > 0) lines.Add("网格 " + JoinCapped(grids, 8));

        lines.Add(validation.Errors.Count == 0
            ? "校验 通过 E 0 W " + validation.Warnings.Count + " I " + validation.Notes.Count
            : "校验 未通过 E " + validation.Errors.Count + " W " + validation.Warnings.Count + " I " + validation.Notes.Count);
        lines.Add("入口 " + EntryPath);

        var text = string.Join("\n", lines);
        while (Encoding.UTF8.GetByteCount(text) > MaxStatusBytes && lines.Count > 4)
        {
            lines.RemoveAt(lines.Count - 2);
            text = string.Join("\n", lines);
        }
        return text;
    }

    private static string JoinCapped(List<string> items, int cap)
    {
        if (items.Count <= cap) return string.Join(";", items);
        return string.Join(";", items.Take(cap)) + " …+" + (items.Count - cap);
    }

    private static List<string> GridCounts(PTreeRepository repository)
    {
        var result = new List<string>();
        try
        {
            foreach (var document in TreeStore.Load(repository.Root).Documents)
                result.Add(document.Kind + " " + document.Entries.Count);
        }
        catch (Exception)
        {
            return result;
        }
        return result;
    }

    private static TreeStore TreeNodeStore(PTreeRepository repository)
    {
        try { return TreeStore.Load(repository.Root); }
        catch (Exception) { return new TreeStore(); }
    }

    private static List<RawMap> Links(RawMap manifest) =>
        manifest.TryGetValue("links", out var raw) && raw is List<object?> list ? list.OfType<RawMap>().ToList() : new List<RawMap>();

    private static string? StringOf(RawMap? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) ? value as string : null;
}
