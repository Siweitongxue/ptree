using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>功能块（打组）：建组、并节点、详细介绍、卡上可见、R38 校验。</summary>
public sealed class GroupsTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static (string Root, bool Ok) Init()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var init = InitRunner.Write(InitRunner.Plan(root, "功能块测试", force: true));
        return (root, init.Ok);
    }

    private static string Intro(string tail) =>
        "这个功能块把解决同一件事的几个程序并在一起，方便 AI 一次读完它们的边界与配合方式。它解决的是理解成本：单个节点只说自己干什么，" + tail +
        "。成员之间的契约与依赖仍写在各自节点上，块只负责给出一张整体的职责地图与判断顺序，不重复校验规则，也不替成员决定实现细节。改动块时先看成员是否还匹配，成员换了要同步改这段介绍，否则块会变成过时的目录。";

    [Fact]
    public void AddGroupJoinsNodesAndShowsOnCard()
    {
        var (root, ok) = Init();
        try
        {
            Assert.True(ok);
            var result = NodeTools.AddGroup(root, Args(new
            {
                name = "主流程",
                description = "从请求进来到结果落库的主流程",
                nodes = new[] { "core", "core.app" },
                intro = Intro("这里只剩两三个程序时不要建块，直接在部门里看更省"),
            }));
            using var document = JsonDocument.Parse(result);
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), result);
            var id = document.RootElement.GetProperty("id").GetString()!;
            Assert.StartsWith("group.", id);

            var repository = PTreeRepository.Load(root);
            var group = repository.Groups.Single(candidate => candidate.Id == id);
            Assert.Equal(2, group.Members.Count);
            Assert.Contains("core.app", group.Members);
            Assert.True(File.Exists(Path.Combine(root, "00-程序树", "说明", id + ".txt")));

            var card = NodeTools.ReadNode(root, Args(new { id = "core.app" }));
            Assert.Contains("块 " + id, card);
            Assert.True(card.Split('\n').Length <= 9, card);

            // 功能块自己也要能被读到、被搜到、在整树粗读里出现
            var blockCard = NodeTools.ReadNode(root, Args(new { id }));
            Assert.Contains("[块] 成员 2", blockCard);
            Assert.Contains("详 00-程序树/说明/" + id + ".txt", blockCard);

            var found = NodeTools.Find(root, Args(new { query = "主流程" }));
            using var foundDocument = JsonDocument.Parse(found);
            Assert.True(foundDocument.RootElement.GetProperty("groups").GetArrayLength() >= 1, found);

            var map = NodeTools.Map(root, Args(new { mode = "summary" }));
            Assert.Contains("块 ", map);
            Assert.Contains(id, map);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddGroupRejectsUnknownMember()
    {
        var (root, ok) = Init();
        try
        {
            Assert.True(ok);
            var result = NodeTools.AddGroup(root, Args(new { name = "空块", nodes = new[] { "core.app", "ghost.node" } }));
            using var document = JsonDocument.Parse(result);
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("ghost.node", result);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateGroupChangesMembersAndIntro()
    {
        var (root, ok) = Init();
        try
        {
            Assert.True(ok);
            var added = NodeTools.AddGroup(root, Args(new
            {
                name = "主流程",
                description = "主流程",
                nodes = new[] { "core.app" },
                intro = Intro("先只并一个，后面再加"),
            }));
            using var document = JsonDocument.Parse(added);
            var id = document.RootElement.GetProperty("id").GetString()!;

            var updated = NodeTools.UpdateGroup(root, Args(new { id, add_members = new[] { "core" }, description = "主流程与根" }));
            using var updatedDocument = JsonDocument.Parse(updated);
            Assert.True(updatedDocument.RootElement.GetProperty("ok").GetBoolean(), updated);
            var repository = PTreeRepository.Load(root);
            Assert.Equal(2, repository.Groups.Single(group => group.Id == id).Members.Count);

            var removed = NodeTools.UpdateGroup(root, Args(new { id, remove_members = new[] { "core" } }));
            Assert.Contains("\"ok\": true", removed);
            Assert.Single(PTreeRepository.Load(root).Groups.Single(group => group.Id == id).Members);

            var empty = NodeTools.UpdateGroup(root, Args(new { id, remove_members = new[] { "core.app" } }));
            Assert.Contains("至少留一个成员", empty);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GroupValidationCatchesMissingIntroAndBadMembers()
    {
        var (root, ok) = Init();
        try
        {
            Assert.True(ok);
            var manifestPath = Path.Combine(root, "00-程序树", "program-tree.yaml");
            var text = File.ReadAllText(manifestPath);
            text = text.Replace("groups:\n", "groups:\n  - id: group.scratch\n    name: 临时块\n    description: 用来试校验的块\n    parent: null\n    members: [ghost.node]\n", StringComparison.Ordinal);
            File.WriteAllText(manifestPath, text, new UTF8Encoding(false));
            var validation = Validator.Validate(PTreeRepository.Load(root));
            Assert.Contains(validation.Errors, issue => issue.Code == "R38" && issue.Message.Contains("组成员不存在", StringComparison.Ordinal));
            Assert.Contains(validation.Warnings, issue => issue.Code == "R38" && issue.Message.Contains("缺功能块详细介绍", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
