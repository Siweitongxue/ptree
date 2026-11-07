using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>读节点/树状态：9 行以内卡片、短介绍、depth 图、full 原 JSON。</summary>
public sealed class NodeCardsTests
{
    private static readonly Lazy<PTreeRepository> Repository = new(() => PTreeRepository.Load(RepoLocator.Root));

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-cards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "00-程序树"));
        return root;
    }

    private static void WriteManifest(string root, string nodes)
    {
        var builder = new StringBuilder();
        builder.Append("schema_version: 1\ntree_version: 9.9.9\nupdated: '2026-11-06'\nroot: n000\nphases: []\nnodes:\n");
        builder.Append(nodes);
        File.WriteAllText(Path.Combine(root, "00-程序树", "program-tree.yaml"), builder.ToString(), new UTF8Encoding(false));
    }

    private static string NodeBlock(string id, string name, int layer, string parent, string responsibility, string files,
        string depends)
    {
        var builder = new StringBuilder();
        builder.Append("  - id: ").Append(id).Append('\n');
        builder.Append("    name: ").Append(name).Append('\n');
        builder.Append("    kind: ").Append(layer == 0 ? "root" : "program").Append('\n');
        builder.Append("    layer: ").Append(layer).Append('\n');
        builder.Append("    parent: ").Append(parent).Append('\n');
        builder.Append("    phase: P0\n    status: 未完成\n");
        builder.Append("    responsibility: ").Append(responsibility).Append('\n');
        builder.Append("    not_responsible: []\n    inputs: []\n    outputs: []\n");
        builder.Append("    depends_on: ").Append(depends).Append('\n');
        builder.Append("    files: ").Append(files).Append('\n');
        builder.Append("    acceptance: [测试：无]\n    spec: x\n");
        return builder.ToString();
    }

    [Fact]
    public void CardStaysWithinNineLinesAndBudgetForEveryNode()
    {
        var ids = Repository.Value.NodeMap.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(ids);
        var total = 0L;
        foreach (var id in ids)
        {
            var text = NodeTools.ReadNode(RepoLocator.Root, Args(new { id, depth = 1 }));
            var lines = text.Split('\n');
            Assert.True(lines.Length <= 9, id + " 行数 " + lines.Length + "\n" + text);
            var bytes = Encoding.UTF8.GetByteCount(text);
            Assert.True(bytes <= 800, id + " 字节 " + bytes + "\n" + text);
            Assert.True(lines[1].StartsWith("短 ", StringComparison.Ordinal), id + " 第2行不是短介绍\n" + text);
            Assert.All(lines, line => Assert.True(line.Length <= 110, id + " 行过长 " + line));
            total += bytes;
            Assert.DoesNotContain("\n\n", text);
            Assert.False(text.TrimStart().StartsWith('{'), id + " 默认应是纯文本卡\n" + text);
        }
        Assert.True(total / ids.Count <= 500, "平均字节 " + total / ids.Count);
    }

    [Fact]
    public void CardSecondLineUsesShortIntroFile()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "负责根", "[]", "[]") +
                                NodeBlock("n000.app", "程序", 1, "n000", "负责程序", "[]", "[]"));
            var directory = Path.Combine(root, "00-程序树", "短介绍");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "n000.app.txt"), "把输入变成结构化结果。\n", new UTF8Encoding(false));
            var card = NodeTools.ReadNode(root, Args(new { id = "n000.app", depth = 1 }));
            Assert.Contains("\n短 把输入变成结构化结果。\n", card);
            var missing = NodeTools.ReadNode(root, Args(new { id = "n000", depth = 1 }));
            Assert.Contains("\n短 未写\n", missing);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateNodeWritesShortIntroAndRejectsBadOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-short-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var init = InitRunner.Write(InitRunner.Plan(root, "短介绍测试", force: true));
            Assert.True(init.Ok, string.Join("\n", init.Issues));

            var ok = NodeTools.UpdateNode(root, Args(new { id = "core.app", short_intro = "把输入变成结构化结果。" }));
            Assert.Contains("\"ok\": true", ok);
            var path = Path.Combine(root, "00-程序树", "短介绍", "core.app.txt");
            Assert.True(File.Exists(path));
            Assert.Equal("把输入变成结构化结果。\n", File.ReadAllText(path).Replace("\r\n", "\n"));
            Assert.Equal("把输入变成结构化结果。", NodeCards.ReadShortIntro(PTreeRepository.Load(root), "core.app"));

            var tooLong = NodeTools.UpdateNode(root, Args(new { id = "core.app", short_intro = new string('长', 81) }));
            Assert.Contains("80 字", tooLong);
            var placeholder = NodeTools.UpdateNode(root, Args(new { id = "core.app", short_intro = "待补功能" }));
            Assert.Contains("占位词", placeholder);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingShortIntroValidatesAsWarning()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "负责根", "[]", "[]") +
                                NodeBlock("n000.app", "程序", 1, "n000", "负责程序", "[]", "[]"));
            var validation = Validator.Validate(PTreeRepository.Load(root));
            Assert.Contains(validation.Warnings, issue => issue.Code == "R37" && issue.Node == "n000.app");
            Assert.DoesNotContain(validation.Errors, issue => issue.Code == "R37");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TooLongShortIntroValidatesAsError()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "负责根", "[]", "[]") +
                                NodeBlock("n000.app", "程序", 1, "n000", "负责程序", "[]", "[]"));
            var directory = Path.Combine(root, "00-程序树", "短介绍");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "n000.app.txt"), new string('短', 81), new UTF8Encoding(false));
            var validation = Validator.Validate(PTreeRepository.Load(root));
            Assert.Contains(validation.Errors, issue => issue.Code == "R37" && issue.Node == "n000.app");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DepthZeroExpandsWholeGraphAndWarnsWhenTruncated()
    {
        var root = TempRoot();
        try
        {
            var builder = new StringBuilder();
            for (var index = 0; index < 260; index++)
            {
                var id = "n" + index.ToString("000");
                builder.Append("  - id: ").Append(id).Append('\n');
                builder.Append("    name: N").Append(index).Append('\n');
                builder.Append("    kind: ").Append(index == 0 ? "root" : "program").Append('\n');
                builder.Append("    layer: ").Append(index).Append('\n');
                builder.Append("    parent: ").Append(index == 0 ? "null" : "'n" + (index - 1).ToString("000") + "'").Append('\n');
                builder.Append("    phase: P0\n    status: 未完成\n    responsibility: 负责演示\n");
                builder.Append("    not_responsible: []\n    inputs: []\n    outputs: []\n    depends_on: []\n");
                builder.Append("    files: []\n    acceptance: []\n    spec: x\n");
            }
            WriteManifest(root, builder.ToString());

            var shallow = NodeTools.ReadNode(root, Args(new { id = "n000", depth = 2 }));
            Assert.StartsWith("# n000 深2：3 节点 2 边", shallow);
            Assert.Contains("\n节点：\n", shallow);
            Assert.Contains("\n边：\n", shallow);
            Assert.Contains("-[层级]->", shallow);

            var full = NodeTools.ReadNode(root, Args(new { id = "n000", depth = 0 }));
            Assert.StartsWith("# n000 深0：260 节点 259 边", full);
            Assert.Contains("⚠ 截断 60 节点未列，用 depth=1 逐点展开", full);
            Assert.Contains("\n节点：\n", full);
            Assert.Contains("\n边：\n", full);
            Assert.Contains("-[层级]->", full);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FullFormatKeepsOriginalKeySet()
    {
        var text = NodeTools.ReadNode(RepoLocator.Root, Args(new { id = "governance.validator", format = "full" }));
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(new[] { "ok", "tree_version", "node", "intro", "groups", "neighbors" },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        var neighbors = root.GetProperty("neighbors");
        Assert.Equal(
            new[]
            {
                "upstream", "downstream", "children", "parent", "siblings", "depends_on", "depended_by",
                "related_plan", "related_art", "related_ui", "links_out", "links_in", "impact",
            },
            neighbors.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.True(Encoding.UTF8.GetByteCount(text) > 2000);
    }

    [Fact]
    public void StatusCardStaysWithinEightLinesAndBudget()
    {
        var text = NodeTools.Status(RepoLocator.Root, Args(new { format = "card" }));
        Assert.True(text.Split('\n').Length <= 8, text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 700, text);
        Assert.Contains("程序树", text);
        Assert.Contains("网格", text);
        Assert.Contains("入口 ", text);
        using var document = JsonDocument.Parse(NodeTools.Status(RepoLocator.Root, Args(new { format = "full" })));
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("by_status").ValueKind);
    }

    [Fact]
    public void FindIncludesShortIntro()
    {
        var text = NodeTools.Find(RepoLocator.Root, Args(new { query = "governance.validator" }));
        using var document = JsonDocument.Parse(text);
        var node = document.RootElement.GetProperty("nodes")[0];
        Assert.True(node.TryGetProperty("short_intro", out _));
    }

    [Fact]
    public void MapNamesListsEveryNodeWithRelations()
    {
        var repository = Repository.Value;
        var names = NodeTools.Map(RepoLocator.Root, Args(new { mode = "names" }));
        Assert.StartsWith("程序树 " + repository.Manifest.TreeVersion, names);
        var lines = names.Split('\n');
        // 首行汇总 + 每个节点一行（有功能块时末尾再加一行「块 N 个」）
        Assert.True(lines.Length >= repository.Nodes.Count + 1, "行数 " + lines.Length + " < " + (repository.Nodes.Count + 1));
        foreach (var node in repository.Nodes)
        {
            Assert.Contains(lines, line => line.TrimStart().StartsWith(node.Id + " ", StringComparison.Ordinal));
        }
        Assert.Contains(" 子", names);
        Assert.Contains(" 依", names);
        // names 模式不给短介绍
        Assert.DoesNotContain("· 能", names);
        Assert.True(Encoding.UTF8.GetByteCount(names) <= 12000, "names 字节 " + Encoding.UTF8.GetByteCount(names));
    }

    [Fact]
    public void MapSummaryAddsShortIntro()
    {
        var text = NodeTools.Map(RepoLocator.Root, Args(new { mode = "summary" }));
        var intro = NodeCards.ReadShortIntro(Repository.Value, "governance.validator");
        Assert.Contains(intro[..Math.Min(12, intro.Length)], text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 17000, "summary 字节 " + Encoding.UTF8.GetByteCount(text));
        Assert.True(Encoding.UTF8.GetByteCount(text) > Encoding.UTF8.GetByteCount(NodeTools.Map(RepoLocator.Root, Args(new { mode = "names" }))),
            "summary 必须比 names 长");
    }

    [Fact]
    public void MapRejectsUnknownMode()
    {
        var text = NodeTools.Map(RepoLocator.Root, Args(new { mode = "all" }));
        using var document = JsonDocument.Parse(text);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("mode 只能是", text);
    }
}
