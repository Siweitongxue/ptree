using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>阅读阶梯工具：tree_guide、tree_map mode=blocks（默认）、tree_read_group。</summary>
public sealed class ReadLadderTests
{
    private static readonly Lazy<PTreeRepository> Repository = new(() => PTreeRepository.Load(RepoLocator.Root));

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void GuideFitsTenLinesAndPointsAtBlocksFirst()
    {
        var text = NodeTools.Guide(RepoLocator.Root);
        var lines = text.Split('\n');
        Assert.True(lines.Length <= 10, "阶梯行数 " + lines.Length + "：\n" + text);
        Assert.Contains("mode=blocks", text);
        Assert.Contains("tree_read_group", text);
        Assert.Contains("能粗别往细", text.Replace("能用上层解决就别往下走", "能粗别往细"));
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 1200, "字节 " + Encoding.UTF8.GetByteCount(text));
    }

    [Fact]
    public void MapDefaultsToBlocks()
    {
        var text = NodeTools.Map(RepoLocator.Root, null);
        Assert.StartsWith("程序树 ", text);
        Assert.Contains("功能块 1 + 部门 9", text);
        Assert.Contains("块 group.tree-toolchain 程序树工具链 · 5 成员 · ", text);
        Assert.Contains("部 governance 治理部", text);
        // 最粗一层不列成员清单
        Assert.DoesNotContain("governance.validator ", text);
        Assert.True(text.Split('\n').Length <= 20, "行数 " + text.Split('\n').Length);
    }

    [Fact]
    public void ReadGroupListsMembers()
    {
        var names = NodeTools.ReadGroup(RepoLocator.Root, Args(new { id = "group.tree-toolchain" }));
        Assert.Contains("组 group.tree-toolchain 程序树工具链 · 5 成员", names);
        foreach (var id in new[] { "governance.tree-spec", "governance.manifest", "governance.validator", "governance.program-tree-mcp", "governance.program-tree-plugin" })
        {
            Assert.Contains(id + " ", names);
        }
        var summary = NodeTools.ReadGroup(RepoLocator.Root, Args(new { id = "group.tree-toolchain", mode = "summary" }));
        Assert.Contains(" · 用 ", summary);
        Assert.Contains("Validator.cs", summary);
    }

    [Fact]
    public void ReadGroupRejectsUnknownAndBadMode()
    {
        var missing = NodeTools.ReadGroup(RepoLocator.Root, Args(new { id = "group.ghost" }));
        using (var document = JsonDocument.Parse(missing))
        {
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Contains("组不存在", missing);

        var bad = NodeTools.ReadGroup(RepoLocator.Root, Args(new { id = "group.tree-toolchain", mode = "all" }));
        Assert.Contains("mode 只能是", bad);
    }
}
