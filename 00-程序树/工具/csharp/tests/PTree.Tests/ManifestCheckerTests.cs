using PTree.Core;
using Xunit;

namespace PTree.Tests;

public sealed class ManifestCheckerTests
{
    private static TreeStore PlanningStore(string programRefs)
    {
        var fields = new[] { "编号", "名称", "目标", "边界", "未定", "对应程序", "文档形态", "确认状态", "时间" };
        var values = new[] { "D03", "程序树", "目标", "边界", "未定", programRefs, "转述稿", "未确认", "2026-10-31" };
        var document = new TreeDocument("策划树", "策划树", "3", "00-程序树/树实例/策划树.txt",
            fields, new[] { new TreeEntry("D03", values, 1) });
        var store = new TreeStore();
        store.Documents.Add(document);
        return store;
    }

    private static PTreeRepository Repository => PTreeRepository.Load(RepoLocator.Root);

    [Fact]
    public void WildcardProgramReferenceReportsError()
    {
        var issues = ManifestChecker.CheckPlanningCoverage(Repository, PlanningStore("全部程序节点"));
        Assert.Contains(issues, issue => issue.Code == "T09" && issue.Level == "E" && issue.Message.Contains("通配词"));
    }

    [Fact]
    public void UnknownProgramReferenceReportsError()
    {
        var issues = ManifestChecker.CheckPlanningCoverage(Repository, PlanningStore("governance.missing-node"));
        Assert.Contains(issues, issue => issue.Code == "T09" && issue.Level == "E" && issue.Message.Contains("不存在"));
    }

    [Fact]
    public void PerNodeMappingPasses()
    {
        var ids = Repository.Nodes.Where(node => node.Kind == "program").Select(node => node.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id)).OrderBy(id => id, StringComparer.Ordinal);
        var issues = ManifestChecker.CheckPlanningCoverage(Repository, PlanningStore(string.Join("、", ids)));
        Assert.DoesNotContain(issues, issue => issue.Code == "T09" && issue.Level == "E");
    }
}
