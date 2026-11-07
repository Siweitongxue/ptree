using System.Text;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>常规代码改动只进台账，不进 health 问题、不进 tree_guard 卡。</summary>
public sealed class HealthChangesTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-health38-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "00-程序树"));
        return root;
    }

    private static void WriteManifest(string root)
    {
        File.WriteAllText(Path.Combine(root, "00-程序树", "program-tree.yaml"), """
schema_version: 1
tree_version: 1.0.0
updated: '2026-01-01'
root: n000
phases: []
nodes:
  - id: n000
    name: 根
    kind: root
    layer: 0
    parent: null
    phase: P0
    status: 未完成
    responsibility: 负责根节点职责
    not_responsible: []
    inputs: []
    outputs: []
    depends_on: []
    files: [code.py]
    acceptance: []
    spec: x
    updated_at: '2026-01-01'
""", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "code.py"), "print(1)\n", new UTF8Encoding(false));
        File.SetLastWriteTime(Path.Combine(root, "code.py"), DateTime.Now.AddDays(3));
    }

    [Fact]
    public void StaleCodeIsLoggedSilentlyAndNotReported()
    {
        var root = TempRoot();
        try
        {
            WriteManifest(root);
            var repository = PTreeRepository.Load(root);
            var report = HealthCheck.Run(repository);

            // 不进问题清单：stale 只是台账
            Assert.True(report.StaleCount > 0);
            Assert.DoesNotContain(report.Issues, issue => issue.Kind == HealthCheck.StaleCode);
            Assert.Empty(report.Issues);
            Assert.True(HealthCheck.ToPayload(report)["ok"] is true);

            // 常检卡保持两行，不因台账变长
            Assert.Equal("一切正常\n可以开始干活", HealthCheck.RenderGuardCard(repository, report, Validator.Validate(repository), 5));

            // 但 health 明确告知有台账，并给查询入口
            var text = HealthCheck.RenderText(report);
            Assert.Contains("已静默记录", text);
            Assert.Contains(report.StaleCount + " 处", text);

            // 同一 node+file 一天只追加一条
            var first = HealthCheck.ReadChanges(root, null, 30, 20);
            Assert.NotEmpty(first);
            Assert.Contains(first, change => change.Node == "n000" && change.File == "code.py" && change.LateDays > 0);
            var second = HealthCheck.Run(repository);
            Assert.Equal(first.Count, HealthCheck.ReadChanges(root, null, 30, 20).Count);
            Assert.Equal(report.StaleCount, second.StaleCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
