using PTree.Core;
using Xunit;

namespace PTree.Tests;

public sealed class ProposalEngineTests
{
    private static string Root()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "00-程序树", "program-tree.yaml"))) return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("找不到仓库根");
    }

    [Fact]
    public void AddNodeSucceeds()
    {
        var repository = PTreeRepository.Load(Root());
        var yaml = """
proposal: test
author: test
reason: 单测新增
ops:
- op: add
  node:
    id: governance.program-tree-plugin.test-engine-node
    name: 引擎测试节点
    kind: step
    layer: 3
    parent: governance.program-tree-plugin
    phase: P0
    status: 未完成
    responsibility: 只用于提案引擎单测的临时节点
    not_responsible:
    - 不进入真实清单
    inputs: []
    outputs:
    - tree.validation-report
    depends_on: []
    files: []
    acceptance:
    - 单测要求
    spec: 00-程序树/program-tree.yaml
    preset: true
    created_at: '2026-10-25'
    updated_at: '2026-10-25'
    attachments: []
""";
        var result = ProposalEngine.Evaluate(repository, yaml, "add.yaml");
        Assert.True(result.Ok, string.Join("；", result.Errors.Select(issue => issue.Message)));
        Assert.Contains("governance.program-tree-plugin.test-engine-node", result.Added);
    }

    [Fact]
    public void PruneWithDependencyReportsError()
    {
        var repository = PTreeRepository.Load(Root());
        var yaml = """
proposal: test
author: test
reason: 单测剪枝
ops:
- op: prune
  node: runtime.secrets
  mode: remove
""";
        var result = ProposalEngine.Evaluate(repository, yaml, "prune.yaml");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, issue => issue.Code is "R24" or "R02");
    }

    [Fact]
    public void DryRunDoesNotWriteManifest()
    {
        var root = Root();
        var repository = PTreeRepository.Load(root);
        var manifestPath = Path.Combine(root, "00-程序树", "program-tree.yaml");
        var before = File.ReadAllBytes(manifestPath);
        var yaml = """
proposal: test
author: test
reason: 单测 dry-run
ops:
- op: meta
  tree_version: 9.9.9
""";
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
