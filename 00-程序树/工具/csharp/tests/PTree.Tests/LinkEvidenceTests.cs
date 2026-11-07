using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>「已验证必须带证据」与「联系可清点」两条闭环。</summary>
public sealed class LinkEvidenceTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

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
        var all = NodeTools.Links(RepoLocator.Root, null);
        Assert.Contains("联系 ", all);
        Assert.Contains(" -[引用]-> ", all);
        Assert.DoesNotContain("没写依据", all);
        var filtered = NodeTools.Links(RepoLocator.Root, Args(new { node = "governance.validator" }));
        Assert.Contains("governance.validator", filtered);
        Assert.Contains("（过滤：governance.validator）", filtered);
        Assert.True(filtered.Split('\n').Length <= all.Split('\n').Length);
    }
}
