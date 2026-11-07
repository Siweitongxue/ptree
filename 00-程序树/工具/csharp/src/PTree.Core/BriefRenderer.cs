using System.Globalization;
using System.Text;

namespace PTree.Core;

/// <summary>项目速览：≤120 行、≤6KB 的纯文本入口，所有数字实时取自清单与树实例。</summary>
public static class BriefRenderer
{
    public const string FileName = "项目速览.txt";
    public const int MaxLines = 120;
    public const int MaxBytes = 6144;

    public static string Render(PTreeRepository repository)
    {
        var lines = new List<string>();
        var treeStore = TreeStore.Load(repository.Root);
        var trees = treeStore.Documents.ToDictionary(document => document.Kind, StringComparer.Ordinal);
        var nodes = repository.Nodes;
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.Status is { } status) statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;
        }

        lines.Add("AI Company 项目速览");
        lines.Add("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                  "；tree_version：" + (repository.Manifest.TreeVersion ?? "?") + "；内核：ptree-cs");
        lines.Add("一句话：用程序树管全公司的程序、契约、说明、标杆，另有若干张实例网格；AI 先看网格定位再动手。");
        lines.Add("");
        lines.Add("一、网格（主网格在前，预留网格在后）");
        var treeDuties = new Dictionary<string, (string Duty, string Note)>(StringComparer.Ordinal)
        {
            ["策划树"] = ("要做什么、做到什么算够、还缺什么", "对应程序与确认状态"),
            ["美术树"] = ("资产分类、启用退役、风格与用处", "界面与美术资源的登记"),
            ["UI树"] = ("场景、时刻、父子集、字体与状态", "界面元素的登记"),
        };
        lines.Add(TreeLine(trees, "程序树", "程序的位置、职责、连接、状态", "机器真相 program-tree.yaml"));
        foreach (var document in treeStore.Documents.Where(document => document.Kind != "程序树"))
        {
            if (treeDuties.TryGetValue(document.Kind, out var duty)) lines.Add(TreeLine(trees, document.Kind, duty.Duty, duty.Note));
            else lines.Add(document.Kind + "：预留网格，字段与编号规则已定、条目待填；实例 " +
                           (trees.TryGetValue(document.Kind, out var reserved) ? reserved.Entries.Count : 0) + " 条（strict=false）");
        }
        lines.Add("");
        lines.Add("二、节点与状态");
        lines.Add("节点总数 " + nodes.Count + "：" + string.Join("、", new[] { "未完成", "已完成", "已验证", "退役" }
            .Select(status => status + " " + statusCounts.GetValueOrDefault(status))));
        lines.Add("");
        lines.Add("三、部门（名称：一句话职责；程序数）");
        foreach (var department in nodes.Where(node => node.Kind == "department").OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            var id = department.Id ?? "?";
            var prefix = id + ".";
            var programs = nodes.Count(node => node.Kind == "program" && node.Id is not null && node.Id.StartsWith(prefix, StringComparison.Ordinal));
            lines.Add((department.Name ?? id) + "（" + id + "）：" + Short(department.Responsibility, 40) + "；程序 " + programs);
        }
        if (!nodes.Any(node => node.Kind == "department")) lines.Add("（无部门节点）");
        lines.Add("");
        lines.Add("四、关键连接（按契约，主干 10 条内）");
        var connections = repository.Contracts.Contracts
            .Where(contract => contract.Id is not null)
            .OrderByDescending(contract => contract.Producers.Count + contract.Consumers.Count)
            .ThenBy(contract => contract.Id, StringComparer.Ordinal)
            .Take(10)
            .ToList();
        foreach (var contract in connections)
        {
            var producers = contract.Producers.Count == 0 ? "外部" : Endpoint(contract.Producers);
            var consumers = contract.Consumers.Count == 0 ? (contract.ExternalConsumers ? "外部" : "（无消费者）") : Endpoint(contract.Consumers);
            lines.Add(contract.Id + "（" + (contract.Name ?? "未命名") + "）：" + producers + " → " + consumers);
        }
        if (connections.Count == 0) lines.Add("（暂无契约）");
        lines.Add("");
        lines.Add("五、当前缺口");
        var verified = statusCounts.GetValueOrDefault("已验证");
        lines.Add("未验证节点：" + (nodes.Count - verified) + "（已验证 " + verified + "）");
        lines.Add("未确认策划：" + UnconfirmedPlanning(treeStore) + " 条");
        lines.Add("未实现预设：" + UnimplementedPresets(repository) + " 个");
        lines.Add("有 home 节点：" + nodes.Count(node => !string.IsNullOrWhiteSpace(node.Home)) + " 个");
        lines.Add("");
        lines.Add("六、从哪深入");
        lines.Add("完整程序书：00-程序树/生成/程序书.txt（全文与节点说明）");
        lines.Add("树视图：00-程序树/生成/树视图.html");
        lines.Add("单节点：ptree-cs node <id> --root <仓库根>");
        lines.Add("节点级工具：tree_add_node / tree_read_node / tree_update_node / tree_retire_node / tree_add_link（MCP 与 HTTP 同源）");
        lines.Add("");
        lines.Add("七、五条常用命令");
        lines.Add("ptree-cs validate：清单校验 R01-R46");
        lines.Add("ptree-cs check：跨树检查 T01-T12");
        lines.Add("ptree-cs brief：重生成本速览");
        lines.Add("ptree-cs book：重生成完整程序书");
        lines.Add("ptree-cs verify：一键验证并写运维报告");
        lines.Add("");

        var content = string.Join("\n", lines) + "\n";
        if (lines.Count > MaxLines || Encoding.UTF8.GetByteCount(content) > MaxBytes)
        {
            throw new PTreeException("速览超出预算：行数 " + lines.Count + "/" + MaxLines +
                                     "、字节 " + Encoding.UTF8.GetByteCount(content) + "/" + MaxBytes +
                                     "（删减部门职责或主干连接条数）");
        }
        return content;
    }

    private static string TreeLine(Dictionary<string, TreeDocument> trees, string kind, string duty, string note)
    {
        var count = trees.TryGetValue(kind, out var document) ? document.Entries.Count : 0;
        return kind + "：管" + duty + "；实例 " + count + " 条（" + note + "）";
    }

    private static string Endpoint(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "—" : values[0] + (values.Count > 1 ? " 等 " + values.Count + " 家" : "");
    }

    private static string Short(string? text, int limit)
    {
        var value = (text ?? "（未填写职责）").Trim().Replace("\n", " ");
        return value.Length <= limit ? value : value[..limit] + "…";
    }

    private static int UnconfirmedPlanning(TreeStore store)
    {
        var planning = store.FindDocument("策划树");
        if (planning is null) return 0;
        return planning.Entries.Count(entry => !entry.Fields.Any(field =>
            field.StartsWith("已确认", StringComparison.Ordinal)));
    }

    private static int UnimplementedPresets(PTreeRepository repository)
    {
        if (repository.ManifestRaw.TryGetValue("nodes", out var raw) && raw is List<object?> list)
        {
            return list.OfType<RawMap>().Count(node =>
                node.TryGetValue("preset", out var preset) && preset is true &&
                (!(node.TryGetValue("status", out var status) && status is string text) || text is not ("已完成" or "已验证")));
        }
        return 0;
    }
}
