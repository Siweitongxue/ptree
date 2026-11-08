using PTree.Core;

namespace PTree.Tests;

/// <summary>
/// TreeRoot 只做解析、不保存状态，所以这里一律用带 startDirectory 的重载，不改 cwd，
/// 也就不会跟并行跑的其他测试互相踩当前目录。
/// </summary>
public sealed class TreeRootTests
{
    [Fact]
    public void ExplicitRootWins()
    {
        var tree = CreateTempTree("explicit");
        var elsewhere = CreateTempDirectory("elsewhere");
        try
        {
            Assert.Equal(Path.GetFullPath(tree), TreeRoot.Resolve(tree, elsewhere));
            Assert.Equal(Path.GetFullPath(tree), TreeRoot.Resolve(tree, tree));
        }
        finally
        {
            TryDelete(tree);
            TryDelete(elsewhere);
        }
    }

    [Fact]
    public void ExplicitPathThatIsNotATreeThrowsAndMentionsPath()
    {
        var notATree = CreateTempDirectory("not-a-tree");
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => TreeRoot.Resolve(notATree, notATree));
            Assert.Contains("不是树根", exception.Message, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(notATree), exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(notATree);
        }
    }

    [Fact]
    public void WalksUpwardFromNestedDirectory()
    {
        var tree = CreateTempTree("nested");
        var nested = Path.Combine(tree, "a", "b", "c");
        Directory.CreateDirectory(nested);
        try
        {
            // 起点 a/b/c 没有清单，逐级向上到 a 找到树根
            Assert.Equal(Path.GetFullPath(tree), TreeRoot.Resolve(null, nested, nested));
        }
        finally
        {
            TryDelete(tree);
        }
    }

    [Fact]
    public void FallsBackToFallbackDirectoryWhenStartHasNoTree()
    {
        var tree = CreateTempTree("fallback");
        var bare = CreateTempDirectory("bare");
        try
        {
            // 起点找不到树时，退回兜底起点（生产上就是 AppContext.BaseDirectory）继续向上找
            Assert.Equal(Path.GetFullPath(tree), TreeRoot.Resolve(null, bare, tree));
        }
        finally
        {
            TryDelete(tree);
            TryDelete(bare);
        }
    }

    [Fact]
    public void ThrowsWhenNothingFound()
    {
        var bare = CreateTempDirectory("nothing");
        var alsoBare = CreateTempDirectory("nothing-either");
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => TreeRoot.Resolve(null, bare, alsoBare));
            Assert.Contains("找不到树根", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(bare);
            TryDelete(alsoBare);
        }
    }

    [Fact]
    public void LooksLikeTreeNeedsTheManifestFile()
    {
        var tree = CreateTempTree("looks");
        var bare = CreateTempDirectory("looks-bare");
        try
        {
            Assert.True(TreeRoot.LooksLikeTree(tree));
            Assert.False(TreeRoot.LooksLikeTree(bare));
        }
        finally
        {
            TryDelete(tree);
            TryDelete(bare);
        }
    }

    private static string CreateTempTree(string tag)
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-treeroot-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "00-程序树"));
        File.WriteAllText(Path.Combine(root, "00-程序树", "program-tree.yaml"), "version: \"1\"\n", new System.Text.UTF8Encoding(false));
        return root;
    }

    private static string CreateTempDirectory(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), "ptree-treeroot-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
