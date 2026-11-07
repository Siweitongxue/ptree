using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>网格阅读项：示例树网格条目的短介绍、R39 校验、读工具的行卡与 tree_map 覆盖统计。</summary>
public sealed class GridReadingsTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-grid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static IEnumerable<object[]> GridEntryIds()
    {
        var store = TreeStore.Load(RepoLocator.Root);
        foreach (var item in GridReadings.Items(store, RepoLocator.Root))
        {
            yield return new object[] { item.Kind, item.Id };
        }
    }

    [Fact]
    public void GridEntriesCoverEveryInstanceDocument()
    {
        var store = TreeStore.Load(RepoLocator.Root);
        var items = GridReadings.Items(store, RepoLocator.Root);
        Assert.NotEmpty(items);
        Assert.Equal(items.Count, items.Select(item => (item.Kind, item.Id)).Distinct().Count());
        // 每份非程序树实例里的每个条目都要被阅读项收进来
        foreach (var document in store.Documents.Where(document => document.Kind != GridReadings.ProgramKind))
        {
            foreach (var entry in document.Entries)
            {
                Assert.Contains(items, item => item.Kind == document.Kind && item.Id == entry.Id);
            }
        }
        // 程序树网格不重复收录：它的条目就是节点，短介绍由 R37 管
        Assert.DoesNotContain(items, item => item.Kind == GridReadings.ProgramKind);
        Assert.All(items, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
    }

    [Theory]
    [MemberData(nameof(GridEntryIds))]
    public void EveryGridEntryHasReadableShortIntro(string kind, string id)
    {
        var path = GridReadings.ShortPath(RepoLocator.Root, id);
        Assert.True(File.Exists(path), kind + " " + id + " 缺短介绍：" + GridReadings.ShortRel(id));
        var text = File.ReadAllText(path);
        var compact = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        Assert.False(string.IsNullOrEmpty(compact), kind + " " + id + " 短介绍为空");
        Assert.True(compact.Length <= 80, kind + " " + id + " 短介绍 " + compact.Length + " 字");
        Assert.DoesNotContain("待补", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("TBD", compact, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TODO", compact, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void R39FlagsMissingAndOverlongGridShortIntro()
    {
        var root = TempRoot();
        try
        {
            InitRunner.Write(InitRunner.Plan(root, "网格阅读项测试", force: true));
            var grid = Path.Combine(root, "00-程序树", "树实例");
            Directory.CreateDirectory(grid);
            var lines = new[]
            {
                "策划树（测试）",
                "版本 1 2026-11-06",
                "1 每行字段顺序：编号 ｜ 名称 ｜ 目标 ｜ 边界 ｜ 未定 ｜ 对应程序 ｜ 文档形态 ｜ 确认状态 ｜ 时间。",
                "D01｜缺短介绍｜目标一｜边界一｜未定一｜b｜转述稿｜未确认｜2026-11-06",
                "D02｜超长短介绍｜目标二｜边界二｜未定二｜b｜转述稿｜未确认｜2026-11-06",
            };
            File.WriteAllText(Path.Combine(grid, "策划树.txt"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            var shorts = Path.Combine(root, "00-程序树", "短介绍");
            Directory.CreateDirectory(shorts);
            File.WriteAllText(Path.Combine(shorts, "D02.txt"), new string('长', 81) + "\n", new UTF8Encoding(false));

            var validation = Validator.Validate(PTreeRepository.Load(root));
            Assert.Contains(validation.Warnings, issue => issue.Code == "R39" &&
                issue.Message.Contains("缺网格短介绍", StringComparison.Ordinal) &&
                issue.Message.Contains("D01", StringComparison.Ordinal));
            Assert.Contains(validation.Errors, issue => issue.Code == "R39" &&
                issue.Message.Contains("超过 80 字", StringComparison.Ordinal) &&
                issue.Message.Contains("D02", StringComparison.Ordinal));
            // 有文件的那条不该再报缺失
            Assert.DoesNotContain(validation.Warnings, issue => issue.Code == "R39" &&
                issue.Message.Contains("D02", StringComparison.Ordinal) &&
                issue.Message.Contains("缺网格短介绍", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [MemberData(nameof(GridEntryIds))]
    public void ReadNodeGivesGridEntryCard(string kind, string id)
    {
        var text = NodeTools.ReadNode(RepoLocator.Root, Args(new { id }));
        var lines = text.Split('\n');
        Assert.True(lines.Length <= 9, id + " 行数 " + lines.Length + "\n" + text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 800, id + " 字节 " + Encoding.UTF8.GetByteCount(text) + "\n" + text);
        Assert.StartsWith(id + " ", lines[0], StringComparison.Ordinal);
        Assert.Contains("[" + kind + "]", lines[0]);
        Assert.StartsWith("短 ", lines[1], StringComparison.Ordinal);
        Assert.Contains("源 00-程序树/树实例/", text);
        Assert.DoesNotContain("\n\n", text);
        Assert.False(text.TrimStart().StartsWith('{'), id + " 默认应是纯文本卡");
    }

    [Fact]
    public void TreeMapReportsGridCoverageAfterBlocks()
    {
        var store = TreeStore.Load(RepoLocator.Root);
        var items = GridReadings.Items(store, RepoLocator.Root);
        var covered = items.Count(item => File.Exists(GridReadings.ShortPath(RepoLocator.Root, item.Id)));
        var map = NodeTools.Map(RepoLocator.Root, Args(new { mode = "summary" }));
        var lines = map.Split('\n');
        Assert.Contains("网格条目 " + items.Count + " 条，短介绍 " + covered + "/" + items.Count, map);
        var gridIndex = Array.FindIndex(lines, line => line.StartsWith("网格条目 ", StringComparison.Ordinal));
        Assert.True(gridIndex >= 0, "summary 里必须有网格覆盖行");
        foreach (var section in lines.Select((value, index) => (value, index))
                     .Where(item => item.value.StartsWith("块 ", StringComparison.Ordinal) ||
                                    item.value.StartsWith("部 ", StringComparison.Ordinal)))
        {
            Assert.True(section.index < gridIndex, "网格覆盖行应在功能块与部门分区之后：" + section.value);
        }
    }
}
