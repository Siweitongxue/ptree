using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>R40 联系登记、R41 代码引用未登记、R42 预设转正一致性，以及预设节点在读工具里的标记。</summary>
public sealed class LinkAndPresetTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "00-程序树"));
        return root;
    }

    /// <summary>最小清单：只有节点；links 段由调用方拼在 nodes 之后。</summary>
    private static void WriteManifest(string root, string nodes, string links = "")
    {
        var builder = new StringBuilder();
        builder.Append("schema_version: 1\ntree_version: 9.9.9\nupdated: '2026-11-07'\nroot: n000\nphases: []\nnodes:\n");
        builder.Append(nodes);
        if (links.Length > 0) builder.Append(links);
        File.WriteAllText(Path.Combine(root, "00-程序树", "program-tree.yaml"), builder.ToString(), new UTF8Encoding(false));
    }

    private static string NodeBlock(string id, string name, int layer, string parent, string files,
        string depends = "[]", bool preset = false, string status = "未完成", string responsibility = "负责演示职责")
    {
        var builder = new StringBuilder();
        builder.Append("  - id: ").Append(id).Append('\n');
        builder.Append("    name: ").Append(name).Append('\n');
        if (preset) builder.Append("    preset: true\n");
        builder.Append("    kind: ").Append(layer == 0 ? "root" : "program").Append('\n');
        builder.Append("    layer: ").Append(layer).Append('\n');
        builder.Append("    parent: ").Append(parent).Append('\n');
        builder.Append("    phase: P0\n    status: ").Append(status).Append('\n');
        builder.Append("    responsibility: ").Append(responsibility).Append('\n');
        builder.Append("    not_responsible: []\n    inputs: []\n    outputs: []\n");
        builder.Append("    depends_on: ").Append(depends).Append('\n');
        builder.Append("    files: ").Append(files).Append('\n');
        builder.Append("    acceptance: [测试：无]\n    spec: x\n");
        return builder.ToString();
    }

    private static string WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return relative;
    }

    // ------------------------------------------------------------------
    // R40 联系登记
    // ------------------------------------------------------------------

    [Fact]
    public void R40RejectsMissingFrom()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") + NodeBlock("n000.app", "程序", 1, "n000", "[]"),
                "links:\n  - {to: n000.app, type: 引用}\n");
            var validation = Validator.Validate(PTreeRepository.Load(root));
            var issue = validation.Errors.Single(entry => entry.Code == "R40");
            Assert.Contains("links[0].from 不能为空", issue.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R40RejectsUnknownEndpoints()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") + NodeBlock("n000.app", "程序", 1, "n000", "[]"),
                "links:\n  - {from: ghost.a, to: n000.app, type: 引用, note: 依据}\n  - {from: n000, to: ghost.b, type: 引用, note: 依据}\n");
            var validation = Validator.Validate(PTreeRepository.Load(root));
            var errors = validation.Errors.Where(entry => entry.Code == "R40").Select(entry => entry.Message).ToList();
            Assert.Contains(errors, message => message.Contains("links[0].from 不存在：ghost.a", StringComparison.Ordinal));
            Assert.Contains(errors, message => message.Contains("links[1].to 不存在：ghost.b", StringComparison.Ordinal));
            Assert.All(errors, message => Assert.Contains("既不是节点 id，也不是网格条目编号", message));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R40RejectsUnknownType()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") + NodeBlock("n000.app", "程序", 1, "n000", "[]"),
                "links:\n  - {from: n000, to: n000.app, type: 派生, note: 依据}\n");
            var validation = Validator.Validate(PTreeRepository.Load(root));
            var issue = validation.Errors.Single(entry => entry.Code == "R40");
            Assert.Contains("links[0].type 非法：派生", issue.Message);
            Assert.Contains("引用 / 测试 / 探针 / 覆盖 / 数据 / 控制", issue.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R40RejectsSelfLinkAndDuplicate()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") + NodeBlock("n000.app", "程序", 1, "n000", "[]"),
                "links:\n  - {from: n000.app, to: n000.app, type: 引用, note: 依据}\n" +
                "  - {from: n000, to: n000.app, type: 引用, note: 依据}\n" +
                "  - {from: n000, to: n000.app, type: 引用, note: 依据}\n");
            var validation = Validator.Validate(PTreeRepository.Load(root));
            var errors = validation.Errors.Where(entry => entry.Code == "R40").Select(entry => entry.Message).ToList();
            Assert.Contains(errors, message => message.Contains("links[0] 不允许自连接：n000.app", StringComparison.Ordinal));
            Assert.Contains(errors, message => message.Contains("links[2] 重复：n000 -[引用]-> n000.app", StringComparison.Ordinal));
            Assert.DoesNotContain(errors, message => message.StartsWith("links[1] 重复", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R40AcceptsValidLinkAndHintsMissingNote()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") + NodeBlock("n000.app", "程序", 1, "n000", "[]"),
                "links:\n  - {from: n000, to: n000.app, type: 测试, note: 依据}\n" +
                "  - {from: n000.app, to: n000, type: 引用}\n");
            var validation = Validator.Validate(PTreeRepository.Load(root));
            Assert.DoesNotContain(validation.Errors, entry => entry.Code == "R40");
            var note = validation.Notes.Single(entry => entry.Code == "R40");
            Assert.Contains("links[1] n000.app -[引用]-> n000 没有 note", note.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R40HintsEmptyLinksSection()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") + NodeBlock("n000.app", "程序", 1, "n000", "[]"));
            var validation = Validator.Validate(PTreeRepository.Load(root));
            Assert.Contains(validation.Notes, entry => entry.Code == "R40" && entry.Message.Contains("links 段还没有登记任何联系", StringComparison.Ordinal));
            Assert.DoesNotContain(validation.Errors, entry => entry.Code == "R40");
            Assert.DoesNotContain(validation.Warnings, entry => entry.Code == "R40");
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // R41 代码引用未登记
    // ------------------------------------------------------------------

    private static string CrossReferenceNodes()
        => NodeBlock("n000", "根", 0, "null", "[]") +
           NodeBlock("n000.a", "甲程序", 1, "n000",
               "[00-程序树/工具/csharp/src/A/A.cs]") +
           NodeBlock("n000.b", "乙程序", 1, "n000",
               "[00-程序树/工具/csharp/src/B/B.cs]");

    private static void WriteCrossReferenceCode(string root)
    {
        WriteFile(root, "00-程序树/工具/csharp/src/A/A.cs",
            "using B.Core;\n\nnamespace A.Core;\n\npublic static class Demo\n{\n}\n");
        WriteFile(root, "00-程序树/工具/csharp/src/B/B.cs",
            "namespace B.Core;\n\npublic static class Demo\n{\n}\n");
    }

    [Fact]
    public void R41HintsUnregisteredCrossNodeReference()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, CrossReferenceNodes());
            WriteCrossReferenceCode(root);
            var validation = Validator.Validate(PTreeRepository.Load(root));
            var entries = validation.Notes.Where(entry => entry.Code == "R41").ToList();
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.Node == "n000.a" &&
                entry.Message.Contains("n000.a 的代码引用了 n000.b 的 00-程序树/工具/csharp/src/B/B.cs，但没有登记联系", StringComparison.Ordinal));
            Assert.Contains(entries, entry => entry.Message.StartsWith("共 1 条代码引用未登记", StringComparison.Ordinal));
            Assert.DoesNotContain(validation.Errors, entry => entry.Code == "R41");
            Assert.DoesNotContain(validation.Warnings, entry => entry.Code == "R41");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R41StaysQuietWhenDependsOnOrReferenceLinkExists()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") +
                                NodeBlock("n000.a", "甲程序", 1, "n000", "[00-程序树/工具/csharp/src/A/A.cs]", "[n000.b]") +
                                NodeBlock("n000.b", "乙程序", 1, "n000", "[00-程序树/工具/csharp/src/B/B.cs]"));
            WriteCrossReferenceCode(root);
            Assert.DoesNotContain(Validator.Validate(PTreeRepository.Load(root)).Notes, entry => entry.Code == "R41");

            // 同一棵树改成用 type=引用 的 link 登记，也不该再报
            WriteManifest(root, CrossReferenceNodes(),
                "links:\n  - {from: n000.a, to: n000.b, type: 引用, note: A 的 using B.Core}\n");
            Assert.DoesNotContain(Validator.Validate(PTreeRepository.Load(root)).Notes, entry => entry.Code == "R41");

            // 只有 type=测试 的 link 不算代码联系登记，仍要提示
            WriteManifest(root, CrossReferenceNodes(),
                "links:\n  - {from: n000.a, to: n000.b, type: 测试, note: 冒烟}\n");
            Assert.Contains(Validator.Validate(PTreeRepository.Load(root)).Notes, entry => entry.Code == "R41");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R41CapsThreePerNodeAndCountsInSummary()
    {
        var root = TempRoot();
        try
        {
            var nodes = new StringBuilder(NodeBlock("n000", "根", 0, "null", "[]"));
            var files = new List<string> { "00-程序树/工具/csharp/src/A/A.cs" };
            nodes.Append(NodeBlock("n000.a", "甲程序", 1, "n000", "[00-程序树/工具/csharp/src/A/A.cs]"));
            var usings = new StringBuilder();
            for (var index = 0; index < 5; index++)
            {
                var id = "n000.b" + index;
                var rel = "00-程序树/工具/csharp/src/B" + index + "/B" + index + ".cs";
                nodes.Append(NodeBlock(id, "乙程序" + index, 1, "n000", "[" + rel + "]"));
                WriteFile(root, rel, "namespace B" + index + ".Core;\n\npublic static class Demo\n{\n}\n");
                usings.Append("using B").Append(index).Append(".Core;\n");
            }
            WriteFile(root, files[0], usings + "\nnamespace A.Core;\n\npublic static class Demo\n{\n}\n");
            WriteManifest(root, nodes.ToString());

            var notes = Validator.Validate(PTreeRepository.Load(root)).Notes.Where(entry => entry.Code == "R41").ToList();
            Assert.Equal(4, notes.Count);
            Assert.Equal(3, notes.Count(entry => entry.Node == "n000.a"));
            Assert.Contains(notes, entry => entry.Message.StartsWith("共 5 条代码引用未登记，已列出 3 条", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // R42 预设转正一致性
    // ------------------------------------------------------------------

    [Fact]
    public void R42WarnsWhenBuiltNodeStillSaysNotBuilt()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") +
                                NodeBlock("n000.app", "程序", 1, "n000", "[10-程序/模块/a.txt]", "[]", false, "已完成"));
            WriteFile(root, "00-程序树/短介绍/n000.app.txt", "把输入变成结构化结果，尚未建成。");
            var validation = Validator.Validate(PTreeRepository.Load(root));
            var issue = validation.Warnings.Single(entry => entry.Code == "R42");
            Assert.Equal("n000.app", issue.Node);
            Assert.Contains("仍写「尚未建成」", issue.Message);
            Assert.DoesNotContain(validation.Errors, entry => entry.Code == "R42");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R42AlsoChecksIntroFile()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") +
                                NodeBlock("n000.app", "程序", 1, "n000", "[10-程序/模块/a.txt]", "[]", false, "已验证"));
            WriteFile(root, "00-程序树/说明/n000.app.txt", "这个节点还是预设占位，先占坑。");
            var validation = Validator.Validate(PTreeRepository.Load(root));
            var issue = validation.Warnings.Single(entry => entry.Code == "R42");
            Assert.Contains("00-程序树/说明/n000.app.txt", issue.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R42IgnoresUnfinishedAndPresetNodes()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") +
                                NodeBlock("n000.app", "程序", 1, "n000", "[10-程序/模块/a.txt]", "[]", false, "未完成") +
                                NodeBlock("n000.stub", "占位", 1, "n000", "[]", "[]", true, "未完成"));
            WriteFile(root, "00-程序树/短介绍/n000.app.txt", "把输入变成结构化结果，尚未建成。");
            WriteFile(root, "00-程序树/短介绍/n000.stub.txt", "预设占位，等代码。");
            var validation = Validator.Validate(PTreeRepository.Load(root));
            Assert.DoesNotContain(validation.Warnings, entry => entry.Code == "R42");
            Assert.DoesNotContain(validation.Errors, entry => entry.Code == "R42");

            // 转正后仍留着占位字样才报 W
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") +
                                NodeBlock("n000.app", "程序", 1, "n000", "[10-程序/模块/a.txt]", "[]", false, "已完成"));
            Assert.Contains(Validator.Validate(PTreeRepository.Load(root)).Warnings, entry => entry.Code == "R42");
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // 预设节点在读工具里看得出来
    // ------------------------------------------------------------------

    [Fact]
    public void PresetNodeCardShowsMarkerAndNoFilesLine()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") +
                                NodeBlock("n000.stub", "占位", 1, "n000", "[]", "[]", true, "未完成"));
            var card = NodeTools.ReadNode(root, Args(new { id = "n000.stub", depth = 1 }));
            var lines = card.Split('\n');
            Assert.StartsWith("n000.stub ", lines[0]);
            Assert.Contains("[未完成·预设]", lines[0]);
            Assert.DoesNotContain(lines, line => line.StartsWith("文件 ", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Length == 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void PresetNodeCardOnTempTreeShowsMarker()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") +
                                NodeBlock("n000.stub", "占位", 1, "n000", "[]", "[]", true, "未完成"));
            var card = NodeTools.ReadNode(root, Args(new { id = "n000.stub", depth = 1 }));
            var lines = card.Split('\n');
            Assert.Contains("[未完成·预设]", lines[0]);
            Assert.DoesNotContain(lines, line => line.StartsWith("文件 ", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MapMarksPresetNodesInline()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") +
                                NodeBlock("n000.stub1", "占位一", 1, "n000", "[]", "[]", true) +
                                NodeBlock("n000.stub2", "占位二", 1, "n000", "[]", "[]", true) +
                                NodeBlock("n000.plain", "普通", 1, "n000", "[]"));
            foreach (var mode in new[] { "names", "summary" })
            {
                var text = NodeTools.Map(root, Args(new { mode }));
                var line = text.Split('\n').Single(item => item.TrimStart().StartsWith("n000.stub1 ", StringComparison.Ordinal));
                Assert.Contains("(预设)", line);
                // 只加这一处记号，不额外加行：带记号的正好是预设节点数
                var marked = text.Split('\n').Count(item => item.Contains("(预设)", StringComparison.Ordinal));
                Assert.Equal(RepositoryPresetCount(root), marked);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void FindReportsPresetFlag()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") +
                                NodeBlock("n000.stub", "占位", 1, "n000", "[]", "[]", true) +
                                NodeBlock("n000.plain", "普通", 1, "n000", "[]"));
            var text = NodeTools.Find(root, Args(new { query = "n000.stub" }));
            using var document = JsonDocument.Parse(text);
            var node = document.RootElement.GetProperty("nodes")[0];
            Assert.True(node.GetProperty("preset").GetBoolean());
            var plain = NodeTools.Find(root, Args(new { query = "n000.plain" }));
            using var plainDocument = JsonDocument.Parse(plain);
            Assert.False(plainDocument.RootElement.GetProperty("nodes")[0].GetProperty("preset").GetBoolean());
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static int RepositoryPresetCount(string root)
        => PTreeRepository.Load(root).Nodes.Count(node => node.Preset);

    // ------------------------------------------------------------------
    // 基址联系 / R44 预设长期未转正 / R45 策划文档缺口 / R41 补法参数
    // ------------------------------------------------------------------

    [Fact]
    public void R40AcceptsBaseLinkAndRejectionMessageListsIt()
    {
        var root = TempRoot();
        try
        {
            Assert.Contains("基址", LinkKinds.All);
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") + NodeBlock("n000.app", "程序", 1, "n000", "[]"),
                "links:\n  - {from: n000.app, to: n000, type: 基址, note: n000.app 继承 n000 的实现}\n");
            var validation = Validator.Validate(PTreeRepository.Load(root));
            Assert.DoesNotContain(validation.Errors, entry => entry.Code == "R40");

            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") + NodeBlock("n000.app", "程序", 1, "n000", "[]"),
                "links:\n  - {from: n000, to: n000.app, type: 派生, note: 依据}\n");
            var bad = Validator.Validate(PTreeRepository.Load(root)).Errors.Single(entry => entry.Code == "R40");
            Assert.Contains("links[0].type 非法：派生", bad.Message);
            Assert.Contains("引用 / 测试 / 探针 / 覆盖 / 数据 / 控制 / 基址", bad.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void WriteEntryAcceptsBaseLinkAndRejectsBogusType()
    {
        // 写入口会整树复跑校验，必须用一棵合法的最小树（InitRunner），手拼夹具会被 phases 之类的 E 挡下
        var root = Path.Combine(Path.GetTempPath(), "ptree-baselink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var init = InitRunner.Write(InitRunner.Plan(root, "基址联系写入口", force: true));
        Assert.True(init.Ok, string.Join("\n", init.Issues));
        try
        {
            var ok = NodeTools.AddLink(root, Args(new { from = "core.app", to = "core", type = "基址", note = "core.app 继承 core 的实现" }));
            using (var document = JsonDocument.Parse(ok))
                Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), ok);
            Assert.Contains("type: 基址", File.ReadAllText(Path.Combine(root, "00-程序树", "program-tree.yaml")));

            var bad = NodeTools.AddLink(root, Args(new { from = "core", to = "core.app", type = "派生", note = "x" }));
            using (var document = JsonDocument.Parse(bad))
                Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), bad);
            Assert.Contains("基址", bad);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R41HintCarriesCopyPasteAddLinkArguments()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, CrossReferenceNodes());
            WriteCrossReferenceCode(root);
            var note = Validator.Validate(PTreeRepository.Load(root)).Notes
                .First(entry => entry.Code == "R41" && entry.Node == "n000.a");
            Assert.Contains("tree_add_link", note.Message);
            Assert.Contains("\"from\":\"n000.a\"", note.Message);
            Assert.Contains("\"to\":\"n000.b\"", note.Message);
            Assert.Contains("\"type\":\"引用\"", note.Message);
            Assert.Contains("note\":\"来源 00-程序树/工具/csharp/src/A/A.cs", note.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R41ReadsCsprojProjectReference()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, CrossReferenceNodes());
            // A 只靠 csproj 的 ProjectReference 引用 B（没有 using，没有命名空间交叉）
            WriteFile(root, "00-程序树/工具/csharp/src/A/A.cs", "namespace A.Core;\n\npublic static class Demo\n{\n}\n");
            WriteFile(root, "00-程序树/工具/csharp/src/B/B.cs", "namespace B.Core;\n\npublic static class Demo\n{\n}\n");
            WriteFile(root, "00-程序树/工具/csharp/src/A/A.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"..\\B\\B.csproj\" />\n  </ItemGroup>\n</Project>\n");
            WriteFile(root, "00-程序树/工具/csharp/src/B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");
            var validation = Validator.Validate(PTreeRepository.Load(root));
            var note = validation.Notes.First(entry => entry.Code == "R41" && entry.Node == "n000.a");
            Assert.Contains("n000.a 的代码引用了 n000.b 的 00-程序树/工具/csharp/src/B/B.csproj", note.Message);
            Assert.Contains("\"type\":\"引用\"", note.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R44FlagsPresetOlderThanThirtyDays()
    {
        var root = TempRoot();
        try
        {
            var old = DateTime.Now.AddDays(-45).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var fresh = DateTime.Now.AddDays(-3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            WriteManifest(root,
                NodeBlock("n000", "根", 0, "null", "[]") +
                NodeBlock("n000.stale", "旧占位", 1, "n000", "[]", "[]", true) + "    created_at: '" + old + "'\n" +
                NodeBlock("n000.fresh", "新占位", 1, "n000", "[]", "[]", true) + "    created_at: '" + fresh + "'\n");
            var notes = Validator.Validate(PTreeRepository.Load(root)).Notes.Where(entry => entry.Code == "R44").ToList();
            var stale = notes.Single(entry => entry.Node == "n000.stale");
            Assert.Contains("45 天没有转正", stale.Message);
            Assert.Contains("转正（tree_update_node 传 preset=false 并补 files）", stale.Message);
            Assert.Contains("退役", stale.Message);
            Assert.DoesNotContain(notes, entry => entry.Node == "n000.fresh");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void R45FlagsPlanningEntriesWithoutDocument()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null", "[]") + NodeBlock("n000.app", "程序", 1, "n000", "[]"));
            var treePath = Path.Combine(root, "00-程序树", "树实例", "策划树.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(treePath)!);
            File.WriteAllText(treePath,
                "策划树（测试）\n版本 1 2026-11-07\n\n一 读法\n" +
                "5 每行字段顺序：编号 ｜ 名称 ｜ 目标 ｜ 边界 ｜ 未定 ｜ 对应程序 ｜ 文档形态 ｜ 确认状态 ｜ 时间 ｜ 文档。\n\n" +
                "四 条目\n" +
                "D01 ｜ 甲 ｜ 目标 ｜ 边界 ｜ 未定 ｜ n000.app ｜ 转述稿 ｜ 未确认 ｜ 2026-11-07 ｜ 未写\n" +
                "D02 ｜ 乙 ｜ 目标 ｜ 边界 ｜ 未定 ｜ n000.app ｜ 转述稿 ｜ 未确认 ｜ 2026-11-07 ｜ 00-程序树/策划/乙.txt\n" +
                "D03 ｜ 丙 ｜ 目标 ｜ 边界 ｜ 未定 ｜ 无 ｜ 转述稿 ｜ 未确认 ｜ 2026-11-07 ｜ 未写\n",
                new UTF8Encoding(false));
            var notes = Validator.Validate(PTreeRepository.Load(root)).Notes.Where(entry => entry.Code == "R45").ToList();
            Assert.Single(notes);
            Assert.Equal("D01", notes[0].Node);
            Assert.Contains("文档列现在是「未写」", notes[0].Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
