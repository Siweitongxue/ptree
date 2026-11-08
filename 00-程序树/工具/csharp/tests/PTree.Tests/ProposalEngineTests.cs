using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>提案引擎：新增、剪枝、dry-run 三条路径。断言全部基于 RepoLocator.Root 指向的示例树。</summary>
public sealed class ProposalEngineTests
{
    private const string AddTemplate = @"
proposal: test
author: test
reason: 单测新增
contracts:
- id: test.step.output
  version: 1.0.0
  purpose: 只用于提案引擎单测的临时产出
  fields:
    value:
      type: string
  required: [value]
  external_consumers: true
  producers: [__ID__]
  consumers: []
ops:
- op: add
  node:
    id: __ID__
    name: 引擎测试步骤
    kind: step
    layer: 3
    parent: __PARENT__
    phase: P0
    status: 未完成
    responsibility: 只用于提案引擎单测的临时节点
    not_responsible:
    - 不进入真实清单
    inputs: []
    outputs:
    - test.step.output
    depends_on: []
    files: []
    acceptance:
    - 测试：单测要求
    spec: 00-程序树/README.txt
    created_at: '2026-11-07'
    updated_at: '2026-11-07'
";

    private const string PruneTemplate = @"
proposal: test
author: test
reason: 单测剪枝
ops:
- op: prune
  node: __NODE__
  mode: remove
";

    private static PTreeRepository Repository() => PTreeRepository.Load(RepoLocator.Root);

    [Fact]
    public void AddNodeSucceeds()
    {
        var repository = Repository();
        var parent = repository.Nodes.First(node => node.Kind == "program" && node.Id is { Length: > 0 }).Id!;
        var id = parent + ".engine-test";
        var yaml = AddTemplate.Replace("__PARENT__", parent, StringComparison.Ordinal)
                               .Replace("__ID__", id, StringComparison.Ordinal);
        var result = ProposalEngine.Evaluate(repository, yaml, "add.yaml");
        Assert.True(result.Ok, string.Join("；", result.Errors.Select(issue => issue.Message)));
        Assert.Contains(id, result.Added);
    }

    [Fact]
    public void PruneWithDependencyReportsError()
    {
        var repository = Repository();
        var target = repository.Nodes.First(node =>
            node.Kind == "department" && repository.Nodes.Any(child => child.Parent == node.Id));
        var yaml = PruneTemplate.Replace("__NODE__", target.Id!, StringComparison.Ordinal);
        var result = ProposalEngine.Evaluate(repository, yaml, "prune.yaml");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, issue => issue.Code is "R24" or "R02");
    }

    [Fact]
    public void BaselineErrorDoesNotBlockUnrelatedProposal()
    {
        // 基线里已有 E 时，与它无关的提案也必须判通过。
        // 回归：去重键一边用 "\u0000" 拼、一边用空串拼，导致基线 E 被当成新问题重复计入。
        var temp = Path.Combine(Path.GetTempPath(), "ptree-baseline-" + Guid.NewGuid().ToString("N"));
        CopyTree(RepoLocator.Root, temp);
        try
        {
            var spec = Path.Combine(temp, "00-程序树", "README.txt");
            if (File.Exists(spec)) File.Delete(spec);
            File.WriteAllText(Path.Combine(temp, "00-程序树", "test-spec.txt"), "临时说明", new System.Text.UTF8Encoding(false));
            var repository = PTreeRepository.Load(temp);
            Assert.NotEmpty(Validator.Validate(repository).Errors);

            var yaml = AddTemplate.Replace("__PARENT__", "core.app", StringComparison.Ordinal)
                                   .Replace("__ID__", "core.app.baseline-test", StringComparison.Ordinal)
                                   .Replace("spec: 00-程序树/README.txt", "spec: 00-程序树/test-spec.txt", StringComparison.Ordinal);
            var result = ProposalEngine.Evaluate(repository, yaml, "add.yaml");
            Assert.True(result.Ok, string.Join("；", result.Errors.Select(issue => issue.Message)));
            Assert.DoesNotContain(result.Errors, issue => issue.Code == "R15");
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar) || relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            if (relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            if (relative.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    [Fact]
    public void DryRunDoesNotWriteManifest()
    {
        var repository = Repository();
        var manifestPath = Path.Combine(RepoLocator.Root, "00-程序树", "program-tree.yaml");
        var before = File.ReadAllBytes(manifestPath);
        const string yaml = @"
proposal: test
author: test
reason: 单测 dry-run
ops:
- op: meta
  tree_version: 9.9.9
";
        var temp = Path.Combine(Path.GetTempPath(), "ptree-proposal-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(temp, yaml, new System.Text.UTF8Encoding(false));
        try
        {
            var result = ProposalEngine.Apply(repository, new[] { temp }, false, true);
            Assert.True(result.Ok);
            Assert.False(result.Written);
            Assert.Equal(before, File.ReadAllBytes(manifestPath));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
