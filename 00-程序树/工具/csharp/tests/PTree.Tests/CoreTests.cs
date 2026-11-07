using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>自 2026-11-06 起，清单版本与节点数以 C# 内核当前快照为准；Python 参考版冻结在 09-归档，不再作为判据。</summary>
public sealed class CoreTests
{
    private static PTreeRepository Load() => PTreeRepository.Load(RepoLocator.Root);

    [Fact]
    public void ManifestLoadsTreeVersionAndNodes()
    {
        var repository = Load();
        Assert.Matches(@"^\d+\.\d+\.\d+$", repository.Manifest.TreeVersion ?? "");
        Assert.Equal("ai-company", repository.Manifest.Root);
        Assert.Contains(repository.Manifest.Nodes, node => node.Id == "governance.validator");
        Assert.Contains(repository.Manifest.Nodes, node => node.Id == "info.understanding");
    }

    [Fact]
    public void IndexGroupsProgramsByDepartment()
    {
        var index = Load().BuildIndex();
        Assert.Matches(@"^\d+\.\d+\.\d+$", index.TreeVersion ?? "");
        Assert.Equal(9, index.Departments.Count);
        Assert.Equal(4, index.Departments["info"].Programs.Count);
        Assert.Equal("info.understanding", index.Departments["info"].Programs[0].Id);
        Assert.DoesNotContain(index.Departments.Keys, key => key.Contains('.'));
    }

    [Fact]
    public void RootNodePayloadHasChildrenAndIntro()
    {
        var payload = Load().BuildNode("ai-company");
        Assert.Equal("root", payload.Kind);
        Assert.Null(payload.Parent);
        Assert.Equal(9, payload.Children.Count);
        Assert.Equal("info", payload.Children[0]);
        Assert.NotNull(payload.Intro.Text);
        Assert.StartsWith("AI Company 是公司根节点", payload.Intro.Text);
        Assert.Equal("00-程序树/说明/ai-company.txt", payload.Intro.Path);
        Assert.Null(payload.Benchmark);
    }

    [Fact]
    public void NodePayloadHasIntroAndConnectionFields()
    {
        var payload = Load().BuildNode("delivery.showcase");
        Assert.NotNull(payload.Intro.Text);
        Assert.StartsWith("展示界面是面向人的页面", payload.Intro.Text);
        Assert.Equal("09-归档/旧智能体公司/展示界面/server.js", payload.Code.Files[0]);
        Assert.Equal("00-程序树/说明/delivery.showcase.txt", payload.Code.ReadBeforeEdit[0]);
        Assert.Contains("00-程序树/契约/contracts.yaml", payload.Code.ReadBeforeEdit);
        var contract = Assert.Single(payload.Connections.Upstream);
        Assert.Equal("delivery.artifact", contract.Contract);
        Assert.Equal(new[] { "items", "manifest_path", "status" }.Length, contract.Fields.Count);
        Assert.Single(payload.Connections.Downstream);
    }

    [Fact]
    public void BenchmarkDetailIsBuiltFromBinding()
    {
        var payload = Load().BuildNode("delivery.showcase");
        var benchmark = Assert.IsType<BenchmarkDetailPayload>(payload.Benchmark);
        Assert.Equal("bench.delivery-showcase", benchmark.Id);
        Assert.Equal(1, benchmark.Level);
        Assert.Equal(3, benchmark.Target);
        Assert.Equal(3, benchmark.Gaps.Count);
        Assert.Equal(5, benchmark.Levels.Count);
        Assert.StartsWith("首屏顶部加", benchmark.Next);
        Assert.Null(payload.Benchmarks);
    }

    [Fact]
    public void UnknownNodeThrowsPTreeException()
    {
        Assert.Throws<PTreeException>(() => Load().BuildNode("no.such.node"));
    }

    [Fact]
    public void JsonOutputKeepsChineseUnescaped()
    {
        var json = JsonOutput.Serialize(Load().BuildNode("info"));
        Assert.Contains("信息部", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u4fe1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitRootIsUsed()
    {
        var repository = PTreeRepository.Load(RepoLocator.Root);
        Assert.Equal(Path.GetFullPath(RepoLocator.Root), repository.Root);
        Assert.Throws<PTreeException>(() => PTreeRepository.Load(Path.Combine(RepoLocator.Root, "no-such-dir")));
    }
}
