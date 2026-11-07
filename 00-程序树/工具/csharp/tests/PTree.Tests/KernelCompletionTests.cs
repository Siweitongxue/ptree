using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

public sealed class KernelCompletionTests
{
    private const string BaseManifest = """
schema_version: 1
tree_version: 1.0.0
updated: '2026-11-06'
root: a
phases:
  - {id: P0, name: 地基, goal: g, exit_criteria: [x]}
nodes:
  - id: a
    name: A
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
    files: []
    acceptance: []
    spec: x
  - id: b
    name: B
    kind: department
    layer: 1
    parent: a
    phase: P0
    status: 未完成
    responsibility: 负责部门职责
    not_responsible: []
    inputs: []
    outputs: []
    depends_on: []
    files: []
    acceptance: []
    spec: x
  - id: c
    name: C
    kind: program
    layer: 2
    parent: b
    phase: P0
    status: 未完成
    home: assets
    responsibility: 负责程序职责
    not_responsible: [不负责其他]
    inputs: []
    outputs: []
    depends_on: [b]
    files: []
    acceptance: [测试：无]
    spec: x
""";

    private static ValidationPayload Validate(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-kernel34-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var raw = (RawMap)RawYaml.Parse(manifest)!;
            return Validator.Validate(PTreeRepository.FromRaw(root, raw, null, null, null));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EvaluationMustBeFortyCharsAndNoPlaceholder()
    {
        var shortPayload = Validate(BaseManifest.Replace("    files: []\n    acceptance: [测试：无]",
            "    files: []\n    evaluation: 太短\n    acceptance: [测试：无]", StringComparison.Ordinal));
        Assert.Contains(shortPayload.Errors, issue => issue.Code == "R34");
        var placeholder = Validate(BaseManifest.Replace("    files: []\n    acceptance: [测试：无]",
            "    files: []\n    evaluation: " + new string('评', 40) + "待补\n    acceptance: [测试：无]", StringComparison.Ordinal));
        Assert.Contains(placeholder.Errors, issue => issue.Code == "R34");
        var good = Validate(BaseManifest.Replace("    files: []\n    acceptance: [测试：无]",
            "    files: []\n    evaluation: " + new string('评', 40) + "\n    acceptance: [测试：无]", StringComparison.Ordinal));
        Assert.DoesNotContain(good.Errors, issue => issue.Code == "R34");
    }

    [Fact]
    public void PresetWithFilesFailsButEmptyPresetPasses()
    {
        var bad = Validate(BaseManifest.Replace("    files: []\n    acceptance: [测试：无]",
            "    files: [assets/x.cs]\n    preset: true\n    preset_outputs: none\n    acceptance: [测试：无]", StringComparison.Ordinal));
        Assert.Contains(bad.Errors, issue => issue.Code == "R35" && issue.Message.Contains("转正"));
        var good = Validate(BaseManifest.Replace("    files: []\n    acceptance: [测试：无]",
            "    files: []\n    preset: true\n    preset_outputs: none\n    acceptance: [测试：无]", StringComparison.Ordinal));
        Assert.DoesNotContain(good.Errors, issue => issue.Code == "R35");
    }

    [Fact]
    public void GitSyncOutsideRepositoryReturnsOkFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-git34-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "00-程序树"));
        try
        {
            File.WriteAllText(Path.Combine(root, "00-程序树", "program-tree.yaml"),
                "schema_version: 1\ntree_version: 1.0.0\nupdated: '2026-11-06'\nroot: a\nphases: []\nnodes: []\n");
            File.WriteAllText(Path.Combine(root, "x.txt"), "x");
            var text = NodeTools.GitSync(root, new[] { "x.txt" }, "test");
            using var document = JsonDocument.Parse(text);
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AdaptScanAccountsForEveryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-adapt34-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        try
        {
            File.WriteAllText(Path.Combine(root, "assets", "owned.cs"), "x");
            File.WriteAllText(Path.Combine(root, "assets", "new.unity"), "x");
            File.WriteAllText(Path.Combine(root, "assets", "other.shader"), "x");
            var manifest = BaseManifest.Replace("    files: []\n    acceptance: [测试：无]",
                "    files: [assets/owned.cs]\n    acceptance: [测试：无]", StringComparison.Ordinal) +
                "\nadapt:\n  - dir: assets\n    patterns: ['*.cs', '*.unity']\n";
            var repository = PTreeRepository.FromRaw(root, (RawMap)RawYaml.Parse(manifest)!, null, null, null);
            var report = AdaptScanner.Scan(repository);
            var counts = (Dictionary<string, int>)report["counts"]!;
            Assert.Equal(1, counts["assigned"]);
            Assert.Equal(1, counts["unassigned"]);
            Assert.Equal(1, counts["ignored"]);
            Assert.Equal(3, counts["total"]);
            var unassigned = (List<Dictionary<string, object?>>)report["unassigned"]!;
            Assert.Equal("c", unassigned[0]["suggested_node"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
