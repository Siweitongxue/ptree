using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>协作占用与留言：锁冲突、续期、过期失效、强拆、信箱已读。</summary>
[Collection("coord-serial")]
public sealed class CoordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ptree-coord-" + Guid.NewGuid().ToString("N"));

    public CoordTests() => Directory.CreateDirectory(Path.Combine(_root, "00-程序树"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static bool Ok(string json) => json.Contains("\"ok\": true", StringComparison.Ordinal);

    [Fact]
    public void ClaimThenClaimByAnotherIsRejectedWithHolder()
    {
        var first = Coord.Claim(_root, Args(new { path = "a/b.txt", agent = "alpha" }));
        Assert.True(Ok(first));

        var second = Coord.Claim(_root, Args(new { path = "a/b.txt", agent = "beta" }));
        Assert.False(Ok(second));
        Assert.Contains("\"by\": \"alpha\"", second);
        Assert.Contains("文件被占用", second);
    }

    [Fact]
    public void ClaimTwiceSameAgentRenews()
    {
        Coord.Claim(_root, Args(new { path = "a.txt", agent = "alpha", ttl_minutes = 1 }));
        var again = Coord.Claim(_root, Args(new { path = "a.txt", agent = "alpha", ttl_minutes = 60 }));
        Assert.True(Ok(again));
        Assert.Contains("\"renewed\": true", again);
        Assert.Contains("\"ttl_minutes\": 60", again);
    }

    [Fact]
    public void ExpiredLockDoesNotBlock()
    {
        Coord.Claim(_root, Args(new { path = "c.txt", agent = "alpha" }));
        var file = Path.Combine(Coord.Dir(_root), "locks.json");
        var text = File.ReadAllText(file).Replace("\"expires_at\": \"", "\"expires_at\": \"2000-01-01 00:00:00");
        File.WriteAllText(file, text);

        var taken = Coord.Claim(_root, Args(new { path = "c.txt", agent = "beta" }));
        Assert.True(Ok(taken));
        Assert.Contains("\"by\": \"beta\"", taken);
    }

    [Fact]
    public void ReleaseRequiresOwnerOrForce()
    {
        Coord.Claim(_root, Args(new { path = "d.txt", agent = "alpha" }));
        var wrong = Coord.Release(_root, Args(new { path = "d.txt", agent = "beta" }));
        Assert.False(Ok(wrong));

        var forced = Coord.Release(_root, Args(new { path = "d.txt", agent = "beta", force = true }));
        Assert.True(Ok(forced));
        Assert.Contains("\"released\": true", forced);
    }

    [Fact]
    public void BackslashPathIsNormalized()
    {
        Coord.Claim(_root, Args(new { path = "x" + Path.DirectorySeparatorChar + "y.txt", agent = "alpha" }));
        var blocked = Coord.Claim(_root, Args(new { path = "x/y.txt", agent = "beta" }));
        Assert.False(Ok(blocked));
        var list = Coord.List(_root);
        Assert.Contains("x/y.txt", list);
    }

    [Fact]
    public void SayThenInboxMarksRead()
    {
        var sent = Coord.Say(_root, Args(new { to = "beta", from = "alpha", text = "我改了 a.txt，你看一下" }));
        Assert.True(Ok(sent));

        var first = Coord.Inbox(_root, Args(new { agent = "beta" }));
        Assert.Contains("我改了 a.txt", first);
        using (var doc = JsonDocument.Parse(first))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
            Assert.True(doc.RootElement.GetProperty("marked_read").GetBoolean());
        }

        var second = Coord.Inbox(_root, Args(new { agent = "beta" }));
        using (var doc = JsonDocument.Parse(second))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        }
    }

    [Fact]
    public void InboxKeepDoesNotMarkRead()
    {
        Coord.Say(_root, Args(new { to = "beta", from = "alpha", text = "只看一眼" }));
        var peek = Coord.Inbox(_root, Args(new { agent = "beta", keep = true }));
        Assert.Contains("只看一眼", peek);
        Assert.Contains("\"marked_read\": false", peek);

        var real = Coord.Inbox(_root, Args(new { agent = "beta" }));
        Assert.Contains("只看一眼", real);
    }
}
