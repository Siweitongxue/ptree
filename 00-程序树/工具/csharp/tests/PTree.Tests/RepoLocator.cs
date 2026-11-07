using System.Text.Json;
using PTree.Core;

namespace PTree.Tests;

internal static class RepoLocator
{
    public static string Root { get; } = FindRoot();

    public static string GoldenDir => Path.Combine(Root, "00-程序树", "工具", "parity", "golden");

    public static IEnumerable<string> GoldenNodeIds()
        => Directory.GetFiles(Path.Combine(GoldenDir, "nodes"), "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(id => id, StringComparer.Ordinal);

    private static string FindRoot()
    {
        // 1) 显式指定：PTREE_TEST_ROOT 指向任意含 00-程序树/program-tree.yaml 的目录
        var explicitRoot = Environment.GetEnvironmentVariable("PTREE_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot) && LooksLikeRoot(explicitRoot!))
        {
            return Path.GetFullPath(explicitRoot!);
        }

        // 2) 开源仓库：测试统一跑在 examples/demo 示例树上，仓库根的自建树不参与快照
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var demo = Path.Combine(directory, "examples", "demo");
            if (LooksLikeRoot(demo))
            {
                return demo;
            }
            directory = Path.GetDirectoryName(directory);
        }

        // 3) 兜底：向上找仓库根的树清单
        directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (LooksLikeRoot(directory))
            {
                return directory;
            }
            directory = Path.GetDirectoryName(directory);
        }
        throw new InvalidOperationException("找不到树根（PTREE_TEST_ROOT / examples/demo / 00-程序树/program-tree.yaml）");
    }

    private static bool LooksLikeRoot(string dir)
        => File.Exists(Path.Combine(dir, "00-程序树", "program-tree.yaml"));
}
