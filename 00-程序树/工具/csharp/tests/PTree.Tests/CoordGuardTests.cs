using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>协作守门：占用者不匹配拒绝、require_claim、同占用者放行、环境变量旁路。</summary>
[Collection("coord-serial")]
public sealed class CoordGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ptree-guard-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous;

    public CoordGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "00-程序树"));
        _previous = Environment.GetEnvironmentVariable("PTREE_COORD");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PTREE_COORD", _previous);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ClaimByOtherBlocksWrite()
    {
        Coord.Claim(_root, Args(new { path = "00-程序树/program-tree.yaml", agent = "alpha" }));
        var blocked = Coord.Guard(_root, new[] { "00-程序树/program-tree.yaml" }, "beta");
        Assert.NotNull(blocked);
        Assert.Contains("alpha", blocked!);
    }

    [Fact]
    public void SameOwnerPasses()
    {
        Coord.Claim(_root, Args(new { path = "00-程序树/program-tree.yaml", agent = "alpha" }));
        Assert.Null(Coord.Guard(_root, new[] { "00-程序树/program-tree.yaml" }, "alpha"));
    }

    [Fact]
    public void AnonymousCallerPassesEvenWhenHeldByOther()
    {
        // 只管 AI：没带身份的调用（人 / 脚本 / 单智能体）不挡
        Coord.Claim(_root, Args(new { path = "00-程序树/program-tree.yaml", agent = "alpha" }));
        Assert.Null(Coord.Guard(_root, new[] { "00-程序树/program-tree.yaml" }, ""));
    }

    [Fact]
    public void UnclaimedWriteIsAllowedByDefault()
    {
        Assert.Null(Coord.Guard(_root, new[] { "00-程序树/program-tree.yaml" }, "beta"));
    }

    [Fact]
    public void RequireClaimRejectsUnclaimedWrite()
    {
        File.WriteAllText(Path.Combine(_root, "00-程序树", "协作策略.yaml"), "enforce: true\nrequire_claim: true\n");
        var blocked = Coord.Guard(_root, new[] { "00-程序树/program-tree.yaml" }, "beta");
        Assert.NotNull(blocked);
        Assert.Contains("未登记占用", blocked!);

        Coord.Claim(_root, Args(new { path = "00-程序树/program-tree.yaml", agent = "beta" }));
        Assert.Null(Coord.Guard(_root, new[] { "00-程序树/program-tree.yaml" }, "beta"));
    }

    [Fact]
    public void EnvironmentSwitchDisablesGuard()
    {
        Coord.Claim(_root, Args(new { path = "00-程序树/program-tree.yaml", agent = "alpha" }));
        Environment.SetEnvironmentVariable("PTREE_COORD", "off");
        Assert.Null(Coord.Guard(_root, new[] { "00-程序树/program-tree.yaml" }, "beta"));
    }

    [Fact]
    public void PolicyCanDisableEnforcement()
    {
        File.WriteAllText(Path.Combine(_root, "00-程序树", "协作策略.yaml"), "enforce: false\n");
        Coord.Claim(_root, Args(new { path = "00-程序树/program-tree.yaml", agent = "alpha" }));
        Assert.Null(Coord.Guard(_root, new[] { "00-程序树/program-tree.yaml" }, "beta"));
    }
}
