namespace PTree.Core;

/// <summary>复刻 ptree.py 的 _iter_program_dirs：os.walk 顺序、目录过滤、符号链接不递归。</summary>
public sealed record ProgramDirectory(string Rel, IReadOnlyList<string> FileNames);

public static class ProgramWalker
{
    private const string ProgramRoot = "10-程序";
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.Ordinal)
    {
        "__pycache__", "node_modules", "venv", ".venv",
    };

    private sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return -1;
            }
            if (right is null)
            {
                return 1;
            }
            var leftRunes = left.EnumerateRunes().ToArray();
            var rightRunes = right.EnumerateRunes().ToArray();
            var count = Math.Min(leftRunes.Length, rightRunes.Length);
            for (var index = 0; index < count; index++)
            {
                var comparison = leftRunes[index].Value.CompareTo(rightRunes[index].Value);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
            return leftRunes.Length.CompareTo(rightRunes.Length);
        }
    }

    public static List<ProgramDirectory> Enumerate(string root)
    {
        var result = new List<ProgramDirectory>();
        var basePath = Path.Combine(root, ProgramRoot);
        if (!Directory.Exists(basePath))
        {
            return result;
        }
        Walk(basePath, root, result, isRoot: true);
        return result;
    }

    private static void Walk(string directory, string root, List<ProgramDirectory> result, bool isRoot)
    {
        List<string> directories;
        List<string> files;
        try
        {
            directories = Directory.EnumerateDirectories(directory)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .Where(name => !name.StartsWith('.') && !SkippedDirectoryNames.Contains(name))
                .OrderBy(name => name, CodePointComparer.Instance)
                .ToList();
            files = Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .ToList();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return;
        }
        var rel = Path.GetRelativePath(root, directory).Replace('\\', '/');
        if (isRoot)
        {
            directories = directories
                .Where(name => rel + "/" + name != ProgramRoot + "/共享")
                .ToList();
        }
        result.Add(new ProgramDirectory(rel, files));
        foreach (var name in directories)
        {
            var child = Path.Combine(directory, name);
            try
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                continue;
            }
            Walk(child, root, result, isRoot: false);
        }
    }
}
