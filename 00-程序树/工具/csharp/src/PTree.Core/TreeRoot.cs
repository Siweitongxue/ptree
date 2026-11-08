namespace PTree.Core;

/// <summary>
/// 树根解析：只认一个标志 —— <c>&lt;dir&gt;\00-程序树\program-tree.yaml</c>。
/// 显式根优先；没有显式根时从当前目录逐级向上找，再退到程序所在目录向上找；
/// 都找不到就抛 <see cref="InvalidOperationException"/>，绝不静默把当前目录当根。
/// 这一层不保存任何状态：同一时刻的「当前根」由调用方（MCP 服务进程）持有。
/// </summary>
public static class TreeRoot
{
    /// <summary>识别树根的唯一标志文件（相对路径）。</summary>
    public const string ManifestRelativePath = "00-程序树/program-tree.yaml";

    /// <summary>这个目录是不是树根：只看 00-程序树/program-tree.yaml 是否存在，不猜别的标志。</summary>
    public static bool LooksLikeTree(string dir)
        => File.Exists(Path.Combine(dir, "00-程序树", "program-tree.yaml"));

    /// <summary>解析树根：不传就用当前目录做起点，程序目录做兜底。</summary>
    public static string Resolve(string? explicitRoot)
        => Resolve(explicitRoot, Environment.CurrentDirectory, null);

    /// <summary>
    /// 解析树根（可指定起点，测试用，避免改 cwd 串测试）。
    /// </summary>
    /// <param name="explicitRoot">显式树根；给了就必须是树根，否则抛异常。</param>
    /// <param name="startDirectory">向上搜索的起点；为空用当前目录。</param>
    /// <param name="fallbackDirectory">起点找不到时的兜底起点；为空用 AppContext.BaseDirectory。</param>
    public static string Resolve(string? explicitRoot, string? startDirectory, string? fallbackDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            string full;
            try
            {
                full = Path.GetFullPath(explicitRoot.Trim());
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidOperationException("不是树根（路径不合法）：" + explicitRoot, exception);
            }
            if (!LooksLikeTree(full))
            {
                throw new InvalidOperationException(
                    "不是树根：该目录下没有 " + ManifestRelativePath + "，路径 " + full);
            }
            return full;
        }

        var start = string.IsNullOrWhiteSpace(startDirectory) ? Environment.CurrentDirectory : startDirectory!;
        var fallback = string.IsNullOrWhiteSpace(fallbackDirectory) ? AppContext.BaseDirectory : fallbackDirectory!;
        var searched = new List<string>();

        var found = SearchUpward(start, searched);
        if (found is null && !SameDirectory(start, fallback))
        {
            found = SearchUpward(fallback, searched);
        }
        if (found is not null) return found;

        throw new InvalidOperationException(
            "找不到树根：" + string.Join(" 与 ", searched) + " 逐级向上都没有 " + ManifestRelativePath +
            "；请用 --root 指定树根，或在 MCP 里用 tree_use {\"path\": \"<树根>\"} 切过去。");
    }

    private static string? SearchUpward(string start, List<string> searched)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        string current;
        try
        {
            current = Path.GetFullPath(start);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        searched.Add(current);
        while (!string.IsNullOrEmpty(current))
        {
            if (LooksLikeTree(current)) return current;
            var parent = Directory.GetParent(current);
            if (parent is null) break;
            current = parent.FullName;
        }
        return null;
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
