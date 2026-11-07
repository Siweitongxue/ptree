using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

public sealed class HealthAndLifecycleTests
{
    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool Ok(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Init(string root, string name = "健康检查")
    {
        var init = InitRunner.Write(InitRunner.Plan(root, name, force: true));
        Assert.True(init.Ok, string.Join("\n", init.Issues));
    }

    private static string WriteCode(string root, params string[] parts)
    {
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// 健康检查用的假代码" + Environment.NewLine, new UTF8Encoding(false));
        return string.Join('/', parts);
    }

    private static void RewriteManifest(string root, Action<RawMap> mutate)
    {
        var path = Path.Combine(root, "00-程序树", "program-tree.yaml");
        var raw = RawYaml.Parse(File.ReadAllText(path)) as RawMap ?? throw new InvalidOperationException("清单根必须是映射");
        mutate(raw);
        File.WriteAllText(path, YamlEmitter.Dump(raw), new UTF8Encoding(false));
    }

    private static RawMap NodeOf(RawMap manifest, string id) =>
        (manifest.TryGetValue("nodes", out var raw) && raw is List<object?> list ? list.OfType<RawMap>() : Enumerable.Empty<RawMap>())
        .First(node => node.TryGetValue("id", out var value) && (value as string) == id);

    [Fact]
    public void MissingFileIsReported()
    {
        var root = TempRoot();
        try
        {
            Init(root);
            var relative = WriteCode(root, "10-程序", "app", "main.cs");
            var update = NodeTools.UpdateNode(root, Args("""{"id":"core.app","files":["__FILE__"]}""".Replace("__FILE__", relative)));
            Assert.True(Ok(update), update);

            var clean = HealthCheck.Run(PTreeRepository.Load(root));
            Assert.DoesNotContain(clean.Issues, issue => issue.Kind == HealthCheck.MissingFile);

            File.Delete(Path.Combine(root, "10-程序", "app", "main.cs"));
            var report = HealthCheck.Run(PTreeRepository.Load(root));
            var issue = report.Issues.Single(candidate => candidate.Kind == HealthCheck.MissingFile);
            Assert.Equal("core.app", issue.Node);
            Assert.Equal("E", issue.Level);
            Assert.Contains("文件消失", issue.Message);
            Assert.Equal(1, report.CountOf(HealthCheck.MissingFile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StaleCodeIsRecordedButNotAnIssue()
    {
        var root = TempRoot();
        try
        {
            Init(root);
            var relative = WriteCode(root, "10-程序", "app", "main.cs");
            var update = NodeTools.UpdateNode(root, Args("""{"id":"core.app","files":["__FILE__"]}""".Replace("__FILE__", relative)));
            Assert.True(Ok(update), update);

            File.SetLastWriteTime(Path.Combine(root, "10-程序", "app", "main.cs"), DateTime.Now.AddDays(5));

            var report = HealthCheck.Run(PTreeRepository.Load(root));
            Assert.DoesNotContain(report.Issues, candidate => candidate.Kind == HealthCheck.StaleCode);
            Assert.True(report.StaleCount > 0);
            var changes = HealthCheck.ReadChanges(root, "core.app", 30, 20);
            Assert.Contains(changes, change => change.File == relative && change.LateDays >= 4);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DoneWithoutImplementationIsReported()
    {
        var root = TempRoot();
        try
        {
            Init(root);
            var update = NodeTools.UpdateNode(root, Args("""{"id":"core.app","status":"已完成"}"""));
            Assert.True(Ok(update), update);

            var report = HealthCheck.Run(PTreeRepository.Load(root));
            var issue = report.Issues.Single(candidate => candidate.Kind == HealthCheck.DoneWithoutImpl);
            Assert.Equal("core.app", issue.Node);
            Assert.Contains("已完成却无实现", issue.Message);
            Assert.DoesNotContain(report.Issues, candidate => candidate.Kind is HealthCheck.MissingFile or HealthCheck.StaleCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GuardCardIsTwoLinesWhenClean()
    {
        var root = TempRoot();
        try
        {
            Init(root);
            var repository = PTreeRepository.Load(root);
            var report = HealthCheck.Run(repository);
            Assert.Empty(report.Issues);
            var card = HealthCheck.RenderGuardCard(repository, report, Validator.Validate(repository), 5);
            Assert.Equal("一切正常\n可以开始干活", card);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HealthTextStaysWithinTwelveLines()
    {
        var report = new HealthReport { CheckedNodes = 20, StaleCount = 7 };
        for (var index = 0; index < 15; index++)
        {
            report.Issues.Add(new HealthIssue
            {
                Level = "W",
                Kind = HealthCheck.DoneWithoutImpl,
                Node = "node." + index.ToString("00"),
                Message = "已完成却无实现：status=已完成 但 files 为空",
            });
        }
        report.Issues.Add(new HealthIssue
        {
            Level = "E",
            Kind = HealthCheck.MissingFile,
            Node = "node.missing",
            Message = "文件消失：10-程序/gone.cs",
        });
        var text = HealthCheck.RenderText(report);
        var lines = text.Split('\n');
        Assert.True(lines.Length <= HealthCheck.MaxTextLines, "行数 " + lines.Length + "：\n" + text);
        Assert.Contains("检查 20 个节点，问题 16 条", lines[0]);
        Assert.Contains("文件消失", lines[1]);
        Assert.Contains("另有 7 条", string.Join("\n", lines));
        Assert.Contains("7 处代码改动", lines[^1]);
    }

    [Fact]
    public void RelocateMovesFilesAndKeepsValidationGreen()
    {
        var root = TempRoot();
        try
        {
            Init(root);
            var before = WriteCode(root, "10-程序", "app", "main.cs");
            var update = NodeTools.UpdateNode(root, Args("""{"id":"core.app","files":["__FILE__"]}""".Replace("__FILE__", before)));
            Assert.True(Ok(update), update);

            var moved = WriteCode(root, "10-程序", "moved", "main.cs");
            File.Delete(Path.Combine(root, "10-程序", "app", "main.cs"));

            var relocate = NodeTools.Relocate(root, Args(
                """{"id":"core.app","files":["__FILE__"],"reason":"程序目录搬迁"}""".Replace("__FILE__", moved)));
            Assert.True(Ok(relocate), relocate);

            var repository = PTreeRepository.Load(root);
            Assert.Equal(moved, repository.NodeMap["core.app"].Files.Single());
            Assert.Empty(Validator.Validate(repository).Errors);

            using var document = JsonDocument.Parse(relocate);
            var diff = document.RootElement.GetProperty("diff").EnumerateArray().Select(item => item.GetString() ?? "").ToList();
            Assert.Contains(before + " -> " + moved, diff);

            var missing = NodeTools.Relocate(root, Args(
                """{"id":"core.app","files":["10-程序/not-there/main.cs"]}"""));
            Assert.False(Ok(missing), missing);
            Assert.Contains("新路径不存在", missing);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteRefusesNodeWithChildren()
    {
        var root = TempRoot();
        try
        {
            Init(root);
            var delete = NodeTools.Delete(root, Args("""{"id":"core","reason":"想直接删掉有子节点的部门"}"""));
            Assert.False(Ok(delete), delete);
            Assert.Contains("子节点", delete);
            Assert.Contains("core.app", delete);
            Assert.True(PTreeRepository.Load(root).NodeMap.ContainsKey("core"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteRemovesLeafFromNodesGroupsLinksContractsAndWritesLedger()
    {
        var root = TempRoot();
        try
        {
            Init(root);
            var add = NodeTools.AddNode(root, Args("""
            {"parent":"core.app","name":"扫尾步骤","slug":"tail-step","kind":"step",
             "responsibility":"收尾主流程留下的临时产物",
             "intro":"这是删除演练用的步骤节点，挂在 core.app 下面，只负责收尾主流程留下的临时产物，不承担主流程本身的职责。代码放在 10-程序/core/app/tail-step/ 目录，入口写在 main.cs，测试写在同目录 tests 下，改完后依次跑 ptree-cs validate、ptree-cs check 与 ptree-cs verify，三条都零 E 才算改好；说明文件在 00-程序树/说明/core.app.tail-step.txt，清单登记在 00-程序树/program-tree.yaml。它不写契约定义，也不改别的节点；删除时 links、组归属与契约 consumers 与 producers 一起清掉，台账留一条删除记录。"}
            """));
            Assert.True(Ok(add), add);
            var link = NodeTools.AddLink(root, Args(
                """{"from":"core.app","to":"core.app.tail-step","type":"控制"}"""));
            Assert.True(Ok(link), link);

            var delete = NodeTools.Delete(root, Args(
                """{"id":"core.app.tail-step","reason":"扫尾步骤并入主流程，节点作废"}"""));
            Assert.True(Ok(delete), delete);

            var repository = PTreeRepository.Load(root);
            Assert.False(repository.NodeMap.ContainsKey("core.app.tail-step"));
            Assert.DoesNotContain("core.app.tail-step", repository.Groups.Single(group => group.Id == "group.all").Members);
            Assert.Contains(repository.Contracts.Contracts, contract => contract.Id == "app.result");
            Assert.Contains(repository.Contracts.Contracts, contract => contract.Id == "user.request");
            Assert.False(File.Exists(Path.Combine(root, "00-程序树", "说明", "core.app.tail-step.txt")));
            Assert.Empty(Validator.Validate(repository).Errors);

            using var document = JsonDocument.Parse(delete);
            var deleted = document.RootElement.GetProperty("deleted");
            Assert.Equal(1, deleted.GetProperty("links_removed").GetInt32());
            Assert.Equal(2, deleted.GetProperty("contracts_touched").GetArrayLength());
            Assert.Equal(0, deleted.GetProperty("contracts_removed").GetArrayLength());
            Assert.True(deleted.GetProperty("field_count").GetInt32() > 5);

            var ledger = Reporting.ReadLedger(repository);
            Assert.Contains(ledger, entry => entry.TryGetProperty("ops", out var ops) &&
                                             (ops.ToString() ?? "").Contains("delete core.app.tail-step", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateNodeIntroReplacesWholeText()
    {
        var root = TempRoot();
        try
        {
            Init(root);
            var intro = "这是演练用的改后说明正文，用来验证 tree_update_node 的 intro 参数会整篇替换而不是追加。代码放在 10-程序/app/ 目录，入口与测试都在该目录下，改完跑 ptree-cs validate 与 ptree-cs check；说明文件本身在 00-程序树/说明/core.app.txt，清单在 00-程序树/program-tree.yaml。它不改契约定义，也不改别的节点；只承诺本节点职责范围内的产物与验收，越界的事一律不做，遇到与别的节点冲突的地方先停下来找负责人对齐。";
            var update = NodeTools.UpdateNode(root, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["id"] = "core.app",
                ["intro"] = intro,
            }));
            Assert.True(Ok(update), update);

            var path = Path.Combine(root, "00-程序树", "说明", "core.app.txt");
            var written = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.Equal(intro + "\n", written);
            Assert.Empty(Validator.Validate(PTreeRepository.Load(root)).Errors);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

