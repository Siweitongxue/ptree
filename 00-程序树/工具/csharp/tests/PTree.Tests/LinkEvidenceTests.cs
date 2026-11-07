using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>「已验证必须带证据」与「联系可清点」两条闭环。</summary>
public sealed class LinkEvidenceTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "00-程序树"));
        return root;
    }

    private static void WriteManifest(string root, string nodes, string links)
    {
        var builder = new StringBuilder();
        builder.Append("schema_version: 1\ntree_version: 9.9.9\nupdated: '2026-11-07'\nroot: n000\nphases: []\nnodes:\n");
        builder.Append(nodes);
        builder.Append(links);
        File.WriteAllText(Path.Combine(root, "00-程序树", "program-tree.yaml"), builder.ToString(), new UTF8Encoding(false));
    }

    private static string NodeBlock(string id, string name, int layer, string parent) =>
        "  - id: " + id + "\n    name: " + name + "\n    kind: " + (layer == 0 ? "root" : "program") +
        "\n    layer: " + layer + "\n    parent: " + parent +
        "\n    phase: P0\n    status: 未完成\n    responsibility: 负责演示职责\n" +
        "    not_responsible: []\n    inputs: []\n    outputs: []\n    depends_on: []\n" +
        "    files: []\n    acceptance: [测试：无]\n    spec: x\n";

    [Fact]
    public void VerifiedStatusRequiresPassingEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-r46-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(InitRunner.Write(InitRunner.Plan(root, "证据测试", force: true)).Ok);
            // 没有 evidence 就标已验证：写入必须被 R46 拦住并回滚
            var refused = NodeTools.UpdateNode(root, Args(new { id = "core.app", status = "已验证" }));
            using (var document = JsonDocument.Parse(refused))
            {
                Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), refused);
            }
            Assert.Contains("R46", refused);
            Assert.Equal("未完成", PTreeRepository.Load(root).NodeMap["core.app"].Status);

            // 补一条 result=pass 的证据后，已验证才允许
            var withEvidence = NodeTools.UpdateNode(root, Args(new
            {
                id = "core.app",
                evidence = new[]
                {
                    new { test = "人工走查：跑通主流程", at = "2026-11-07", result = "pass", note = "用户口头确认可用" },
                },
                status = "已验证",
            }));
            Assert.True(withEvidence.Contains("\"ok\": true"), withEvidence);
            Assert.Equal("已验证", PTreeRepository.Load(root).NodeMap["core.app"].Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LinksCommandListsExplicitLinksWithNotes()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root, NodeBlock("n000", "根", 0, "null") + NodeBlock("n000.app", "程序", 1, "n000"),
                "links:\n  - {from: n000, to: n000.app, type: 引用, note: 入口关系}\n  - {from: n000.app, to: n000, type: 数据, note: 回传结果}\n");
            var all = NodeTools.Links(root, null);
            Assert.Contains("联系 2 条", all);
            Assert.Contains(" -[引用]-> ", all);
            Assert.Contains(" -[数据]-> ", all);
            Assert.DoesNotContain("没写依据", all);
            var filtered = NodeTools.Links(root, Args(new { node = "n000.app" }));
            Assert.Contains("n000.app", filtered);
            Assert.Contains("（过滤：n000.app）", filtered);
            Assert.True(filtered.Split('\n').Length <= all.Split('\n').Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
