using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>阅读阶梯工具：tree_guide、tree_map mode=blocks（默认）、tree_read_group。断言从 RepoLocator.Root 的示例树派生。</summary>
public sealed class ReadLadderTests
{
    private static readonly Lazy<PTreeRepository> Repository = new(() => PTreeRepository.Load(RepoLocator.Root));

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static bool IsAutoGroup(string? id) =>
        string.IsNullOrEmpty(id) || id == "group.all" || id.StartsWith("group.dept-", StringComparison.Ordinal);

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
        var repository = Repository.Value;
        var blocks = repository.Groups.Where(group => !IsAutoGroup(group.Id)).ToList();
        var departments = repository.Nodes.Where(node => node.Kind == "department").ToList();
        var text = NodeTools.Map(RepoLocator.Root, null);
        Assert.StartsWith("程序树 " + repository.Manifest.TreeVersion, text);
        Assert.Contains("功能块 " + blocks.Count + " + 部门 " + departments.Count, text);
        foreach (var block in blocks)
        {
            Assert.Contains("块 " + block.Id + " ", text);
        }
        foreach (var department in departments)
        {
            Assert.Contains("部 " + department.Id + " ", text);
        }
        // 最粗一层不列成员清单：任何 program 节点的行首 id 都不该出现
        foreach (var program in repository.Nodes.Where(node => node.Kind == "program"))
        {
            Assert.DoesNotContain("\n" + program.Id + " ", text);
        }
        Assert.True(text.Split('\n').Length <= 20, "行数 " + text.Split('\n').Length);
    }

    [Fact]
    public void ReadGroupListsMembers()
    {
        var repository = Repository.Value;
        var group = repository.Groups.First(candidate => candidate.Members.Count > 0);
        var names = NodeTools.ReadGroup(RepoLocator.Root, Args(new { id = group.Id }));
        Assert.Contains("组 " + group.Id + " " + (group.Name ?? "") + " · " + group.Members.Count + " 成员", names);
        foreach (var member in group.Members)
        {
            Assert.Contains(member + " ", names);
        }
        var summary = NodeTools.ReadGroup(RepoLocator.Root, Args(new { id = group.Id, mode = "summary" }));
        Assert.Contains(" · 用 ", summary);
        Assert.True(summary.Length > names.Length, "summary 必须比 names 长");
        var firstNode = repository.NodeMap[group.Members.First(member => repository.NodeMap.ContainsKey(member))];
        var usage = firstNode.Files.Count == 0 ? "无代码" : firstNode.Files[0];
        Assert.Contains(usage, summary);
    }

    [Fact]
    public void ReadGroupRejectsUnknownAndBadMode()
    {
        var repository = Repository.Value;
        var group = repository.Groups.First();
        var missing = NodeTools.ReadGroup(RepoLocator.Root, Args(new { id = "group.ghost" }));
        using (var document = JsonDocument.Parse(missing))
        {
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Contains("组不存在", missing);

        var bad = NodeTools.ReadGroup(RepoLocator.Root, Args(new { id = group.Id, mode = "all" }));
        Assert.Contains("mode 只能是", bad);
    }
}
