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
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "00-程序树", "program-tree.yaml")))
            {
                return directory;
            }
            directory = Path.GetDirectoryName(directory);
        }
        throw new InvalidOperationException("找不到仓库根（00-程序树/program-tree.yaml）");
    }
}
