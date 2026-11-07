using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>内核载荷与索引：断言全部从 RepoLocator.Root 指向的示例树派生，不写死任何内部项目的数据。</summary>
public sealed class CoreTests
{
    private static PTreeRepository Load() => PTreeRepository.Load(RepoLocator.Root);

    [Fact]
    public void ManifestLoadsTreeVersionAndNodes()
    {
        var repository = Load();
        Assert.Matches(@"^\d+\.\d+\.\d+$", repository.Manifest.TreeVersion ?? "");
        Assert.False(string.IsNullOrWhiteSpace(repository.Manifest.Root));
        Assert.Contains(repository.Manifest.Nodes, node => node.Id == repository.Manifest.Root);
        Assert.All(repository.Manifest.Nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.Id)));
        Assert.Contains(repository.Manifest.Nodes, node => node.Kind == "department");
    }

    [Fact]
    public void IndexGroupsProgramsByDepartment()
    {
        var repository = Load();
        var index = repository.BuildIndex();
        Assert.Matches(@"^\d+\.\d+\.\d+$", index.TreeVersion ?? "");
        var departments = repository.Nodes.Where(node => node.Kind == "department").Select(node => node.Id!).ToList();
        Assert.NotEmpty(departments);
        Assert.Equal(departments.Count, index.Departments.Count);
        Assert.DoesNotContain(index.Departments.Keys, key => key.Contains('.'));
        foreach (var department in departments)
        {
            var expected = repository.Nodes
                .Where(node => node.Kind == "program" && node.Parent == department)
                .Select(node => node.Id!)
                .ToList();
            Assert.Equal(expected, index.Departments[department].Programs.Select(program => program.Id).ToList());
        }
    }

    [Fact]
    public void RootNodePayloadHasChildrenAndIntro()
    {
        var repository = Load();
        var rootId = repository.Manifest.Root!;
        var payload = repository.BuildNode(rootId);
        Assert.Equal("root", payload.Kind);
        Assert.Null(payload.Parent);
        var children = repository.Nodes.Where(node => node.Parent == rootId).Select(node => node.Id!).ToList();
        Assert.Equal(children, payload.Children);
        Assert.NotNull(payload.Intro.Text);
        Assert.Equal("00-程序树/说明/" + rootId + ".txt", payload.Intro.Path);
        Assert.True(File.Exists(Path.Combine(RepoLocator.Root, "00-程序树", "说明", rootId + ".txt")));
        if (repository.Bindings.Bindings.All(binding => binding.Node != rootId))
        {
            Assert.Null(payload.Benchmark);
        }
    }

    [Fact]
    public void NodePayloadHasIntroAndConnectionFields()
    {
        var repository = Load();
        var node = repository.Nodes.First(candidate => candidate.Inputs.Count > 0 && candidate.Outputs.Count > 0);
        var payload = repository.BuildNode(node.Id!);
        Assert.NotNull(payload.Intro.Text);
        Assert.Equal(repository.IntroRel(node.Id!), payload.Intro.Path);
        Assert.Contains(payload.Code.ReadBeforeEdit, path => path.EndsWith("说明/" + node.Id + ".txt", StringComparison.Ordinal));
        Assert.Contains(payload.Code.ReadBeforeEdit, path => path.Contains("程序树/README", StringComparison.Ordinal));
        var upstream = Assert.Single(payload.Connections.Upstream);
        Assert.Equal(node.Inputs[0], upstream.Contract);
        Assert.NotEmpty(upstream.Fields);
        var downstream = Assert.Single(payload.Connections.Downstream);
        Assert.Equal(node.Outputs[0], downstream.Contract);
    }

    [Fact]
    public void BenchmarkDetailIsBuiltFromBinding()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-bench-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(RepoLocator.Root, root);
        try
        {
            var benchmarkDir = Path.Combine(root, "00-程序树", "标杆");
            Directory.CreateDirectory(benchmarkDir);
            File.WriteAllText(Path.Combine(benchmarkDir, "benchmarks.yaml"), """
schema_version: 1
updated: '2026-11-07'
benchmarks:
  - id: bench.demo
    name: 起步程序体验
    kind: vision
    reference: 一屏看懂起步程序
    why: 单测用标杆
    dimensions:
      - id: d1
        name: 上手
        question: 新用户能否一步跑通
    levels:
      - {level: 0, name: 不可用, criteria: 打不开, evidence: 无}
      - {level: 1, name: 能看, criteria: 能跑, evidence: 帮助文档}
      - {level: 2, name: 有结构, criteria: 主次分明, evidence: 走查记录}
      - {level: 3, name: 愿意用, criteria: 不用解释, evidence: 用户试用}
      - {level: 4, name: 标杆级, criteria: 同品级, evidence: 对照清单}
coverage:
  required: []
""", new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(benchmarkDir, "绑定.yaml"), """
schema_version: 1
updated: '2026-11-07'
bindings:
  - node: core.app
    benchmark: bench.demo
    level: 1
    target: 3
    evidence: 走查记录
    gaps:
      - {dimension: d1, gap: 缺一步跑通, next: 首屏顶部加一键入口, opened: '2026-11-07'}
      - {dimension: d1, gap: 缺说明, next: 补三段说明, opened: '2026-11-07'}
      - {dimension: d1, gap: 缺反馈, next: 补结果提示, opened: '2026-11-07'}
    history:
      - {date: '2026-11-07', from: 0, to: 1, proposal: 单测}
""", new System.Text.UTF8Encoding(false));
            var payload = PTreeRepository.Load(root).BuildNode("core.app");
            var benchmark = Assert.IsType<BenchmarkDetailPayload>(payload.Benchmark);
            Assert.Equal("bench.demo", benchmark.Id);
            Assert.Equal(1, benchmark.Level);
            Assert.Equal(3, benchmark.Target);
            Assert.Equal(3, benchmark.Gaps.Count);
            Assert.Equal(5, benchmark.Levels.Count);
            Assert.StartsWith("首屏顶部加", benchmark.Next);
            Assert.Null(payload.Benchmarks);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnknownNodeThrowsPTreeException()
    {
        Assert.Throws<PTreeException>(() => Load().BuildNode("no.such.node"));
    }

    [Fact]
    public void JsonOutputKeepsChineseUnescaped()
    {
        var repository = Load();
        var node = repository.Nodes.First(candidate => candidate.Name is { Length: > 0 });
        var json = JsonOutput.Serialize(repository.BuildNode(node.Id!));
        Assert.Contains(node.Name!, json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitRootIsUsed()
    {
        var repository = PTreeRepository.Load(RepoLocator.Root);
        Assert.Equal(Path.GetFullPath(RepoLocator.Root), repository.Root);
        Assert.Throws<PTreeException>(() => PTreeRepository.Load(Path.Combine(RepoLocator.Root, "no-such-dir")));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
