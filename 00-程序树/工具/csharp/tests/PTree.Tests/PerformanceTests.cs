using System.Diagnostics;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>
/// 性能预算：不是基准，是「别退一个数量级」的护栏。上限取实测中位数的约 3 倍，
/// 机器抖一下不该红；超了报实测值，方便直接看出慢在哪。
/// </summary>
[CollectionDefinition("perf", DisableParallelization = true)]
public sealed class PerformanceCollection
{
}

[Collection("perf")]
public sealed class PerformanceTests
{
    // 2026-11-07 Debug 构建实测中位数（bench --repeat 5，进程内，含加载清单）：
    // validate 约 300 ms、read-node 约 120 ms、map --mode names 约 145 ms；上限按 3 倍左右取整。
    private const double ValidateBudgetMs = 900;
    private const double ReadNodeBudgetMs = 420;
    private const double MapBudgetMs = 480;

    private static readonly Lazy<PTreeRepository> Repository = new(() => PTreeRepository.Load(RepoLocator.Root));

    /// <summary>预热一次再取 3 次里的最小值：只量稳定成本，不量 JIT 首跑。</summary>
    private static double BestOfThree(Action action)
    {
        action();
        var best = double.MaxValue;
        for (var index = 0; index < 3; index++)
        {
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void ValidateStaysWithinBudget()
    {
        var repository = Repository.Value;
        var elapsed = BestOfThree(() => Validator.Validate(repository));
        Assert.True(elapsed <= ValidateBudgetMs,
            "validate 实测 " + elapsed.ToString("0.0") + " ms，超过预算 " + ValidateBudgetMs + " ms（约实测 3 倍）");
    }

    [Fact]
    public void ReadNodeStaysWithinBudget()
    {
        var repository = Repository.Value;
        var nodeId = repository.Nodes.First(node => node.Kind == "program" && node.Id is { Length: > 0 }).Id!;
        var elapsed = BestOfThree(() => NodeCards.RenderNode(repository, nodeId, 1));
        Assert.True(elapsed <= ReadNodeBudgetMs,
            "read-node 实测 " + elapsed.ToString("0.0") + " ms，超过预算 " + ReadNodeBudgetMs + " ms（约实测 3 倍）");
    }

    [Fact]
    public void MapNamesStaysWithinBudget()
    {
        var repository = Repository.Value;
        var elapsed = BestOfThree(() => NodeCards.RenderMap(repository, "names"));
        Assert.True(elapsed <= MapBudgetMs,
            "map --mode names 实测 " + elapsed.ToString("0.0") + " ms，超过预算 " + MapBudgetMs + " ms（约实测 3 倍）");
    }

    [Fact]
    public void BenchReportsSamplesAndColdStart()
    {
        var report = BenchRunner.Run(RepoLocator.Root, 2, "governance.validator");
        Assert.Equal(2, report.Iterations);
        Assert.True(report.ColdStartMs > 0, "冷启动耗时必须可测");
        Assert.False(string.IsNullOrWhiteSpace(report.Root));
        Assert.Equal(6, report.Items.Count);
        Assert.Equal("validate", report.Items[0].Name);
        foreach (var item in report.Items)
        {
            Assert.True(item.MinMs <= item.MedianMs + 0.1, item.Name + " min 大于 median");
            Assert.True(item.MedianMs <= item.P95Ms + 0.1, item.Name + " median 大于 p95");
            Assert.True(item.Iterations == 2, item.Name + " 迭代数不对");
        }
        Assert.Contains("validate", BenchRunner.RenderText(report));
    }
}
