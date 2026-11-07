using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>网格条目写入口：tree_update_node 的 short_intro 回退（只写短介绍，不动清单）。</summary>
public sealed class GridWriteTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static string TempRepo(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-grid-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var init = InitRunner.Write(InitRunner.Plan(root, name, force: true));
        Assert.True(init.Ok, string.Join("\n", init.Issues));
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void UnknownIdIsStillNodeNotFound()
    {
        var root = TempRepo("网格写入口反例");
        try
        {
            var result = NodeTools.UpdateNode(root, Args(new { id = "no-such-thing" }));
            Assert.Contains("\"ok\": false", result);
            Assert.Contains("节点不存在：no-such-thing", result);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void GridEntryRejectsNodeFields()
    {
        var root = TempRepo("网格写入口字段");
        try
        {
            // D01 是 init 起步策划树里的真实网格条目，不是节点
            var result = NodeTools.UpdateNode(root, Args(new { id = "D01", name = "改名", status = "已完成" }));
            Assert.Contains("\"ok\": false", result);
            Assert.Contains("网格条目只有 short_intro 可写", result);
            Assert.Contains("name", result);
            Assert.Contains("status", result);
            Assert.False(File.Exists(GridReadings.ShortPath(root, "D01")));

            var noFields = NodeTools.UpdateNode(root, Args(new { id = "D01" }));
            Assert.Contains("没有传入任何可更新字段", noFields);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UpdateNodeWritesGridShortIntroFileAndClearsR39Warning()
    {
        var root = TempRepo("网格写入口正例");
        try
        {
            var before = Validator.Validate(PTreeRepository.Load(root));
            Assert.Contains(before.Warnings, issue =>
                issue.Code == "R39" && issue.Node == "D01" && issue.Message.Contains("缺网格短介绍", StringComparison.Ordinal));

            const string text = "起步主流程：从输入到结果的最小闭环，用一条策划确认边界。";
            var result = NodeTools.UpdateNode(root, Args(new { id = "D01", short_intro = text }));
            using (var document = JsonDocument.Parse(result))
            {
                var payload = document.RootElement;
                Assert.True(payload.GetProperty("ok").GetBoolean(), result);
                Assert.Equal("D01", payload.GetProperty("id").GetString());
                Assert.Equal("00-程序树/短介绍/D01.txt", payload.GetProperty("written_files")[0].GetString());
                Assert.StartsWith("short_intro：未写 -> ", payload.GetProperty("diff")[0].GetString(), StringComparison.Ordinal);
                Assert.Equal("1.0.0", payload.GetProperty("tree_version").GetString());
            }

            var path = GridReadings.ShortPath(root, "D01");
            Assert.True(File.Exists(path), "短介绍文件应已落盘：" + GridReadings.ShortRel("D01"));
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "短介绍必须是 UTF-8 无 BOM");
            Assert.Equal(text + "\n", File.ReadAllText(path).Replace("\r\n", "\n"));
            Assert.Equal(text, NodeCards.ReadShortIntro(PTreeRepository.Load(root), "D01"));

            var after = Validator.Validate(PTreeRepository.Load(root));
            Assert.DoesNotContain(after.Warnings, issue => issue.Code == "R39" && issue.Node == "D01");
            Assert.DoesNotContain(after.Errors, issue => issue.Code == "R39" && issue.Node == "D01");
            // 网格条目不是节点：写短介绍不 bump tree_version
            Assert.Equal("1.0.0", PTreeRepository.Load(root).Manifest.TreeVersion);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void GridEntryRejectsBadShortIntro()
    {
        var root = TempRepo("网格写入口校验");
        try
        {
            var tooLong = NodeTools.UpdateNode(root, Args(new { id = "M01", short_intro = new string('长', 81) }));
            Assert.Contains("80 字", tooLong);
            var placeholder = NodeTools.UpdateNode(root, Args(new { id = "M01", short_intro = "待补占位" }));
            Assert.Contains("占位词", placeholder);
            var todo = NodeTools.UpdateNode(root, Args(new { id = "M01", short_intro = "TODO 之后再写" }));
            Assert.Contains("占位词", todo);
            var empty = NodeTools.UpdateNode(root, Args(new { id = "M01", short_intro = "   " }));
            Assert.Contains("不能为空", empty);
            Assert.False(File.Exists(GridReadings.ShortPath(root, "M01")), "被拒的短介绍不应落盘");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DuplicateGridIdAcrossGridsIsRejected()
    {
        var root = TempRepo("网格写入口重复");
        try
        {
            var manifestPath = Path.Combine(root, "00-程序树", "program-tree.yaml");
            File.AppendAllText(manifestPath,
                "trees:\n- kind: 音效树\n  file: 音效树\n  id_pattern: '^D[0-9]{2,}$'\n  strict: true\n",
                new UTF8Encoding(false));
            var treeDir = Path.Combine(root, "00-程序树", "树实例");
            Directory.CreateDirectory(treeDir);
            var lines = new[]
            {
                "音效树（测试）",
                "版本 1 2026-11-06",
                "1 每行字段顺序：编号 ｜ 名称 ｜ 说明",
                "D01 ｜ 撞号条目 ｜ 与策划树 D01 同编号",
            };
            File.WriteAllText(Path.Combine(treeDir, "音效树.txt"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            var items = GridReadings.Items(TreeStore.Load(root), root);
            Assert.Equal(2, items.Count(item => item.Id == "D01"));

            var result = NodeTools.UpdateNode(root, Args(new { id = "D01", short_intro = "编号重复时拒绝。" }));
            Assert.Contains("\"ok\": false", result);
            Assert.Contains("不唯一", result);
            Assert.Contains("策划树", result);
            Assert.Contains("音效树", result);
            Assert.False(File.Exists(GridReadings.ShortPath(root, "D01")), "编号重复时不得写盘");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RealRepoD01ShortIntroIsReadableReadOnly()
    {
        var path = GridReadings.ShortPath(RepoLocator.Root, "D01");
        Assert.True(File.Exists(path), "真实仓库缺 D01 短介绍：" + GridReadings.ShortRel("D01"));
        var before = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);

        var repository = PTreeRepository.Load(RepoLocator.Root);
        var text = NodeCards.ReadShortIntro(repository, "D01");
        Assert.NotEqual("未写", text);
        Assert.False(string.IsNullOrWhiteSpace(text));
        var card = NodeTools.ReadNode(RepoLocator.Root, Args(new { id = "D01" }));
        Assert.StartsWith("D01 ", card, StringComparison.Ordinal);
        Assert.Contains("短 " + text, card);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }
}
