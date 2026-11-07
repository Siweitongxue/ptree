using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PTree.Core;

/// <summary>
/// 程序树健康检查：只报三类重要警告——登记的代码文件消失、已完成/已验证却没有实现文件、登记目录整体不存在。
/// 代码 mtime 晚于 updated_at 只静默写 运维/文件变更.jsonl，不进入 Issues；另提供 changes 查询。
/// </summary>
public sealed class HealthIssue
{
    public string Level { get; init; } = "W";
    public string Kind { get; init; } = "";
    public string Node { get; init; } = "";
    public string Message { get; init; } = "";
    public string? File { get; init; }
    public int? Days { get; init; }
}

public sealed class HealthReport
{
    public int CheckedNodes { get; set; }
    public int NodesWithFiles { get; set; }
    public int StaleCount { get; set; }
    public List<HealthIssue> Issues { get; } = new();
    public List<FileChange> Changes { get; } = new();

    public int CountOf(string kind) => Issues.Count(issue => issue.Kind == kind);
}

public sealed record FileChange(
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("node")] string Node,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("mtime")] string Mtime,
    [property: JsonPropertyName("late_days")] int LateDays);

public static class HealthCheck
{
    public const string MissingFile = "missing_file";
    public const string DoneWithoutImpl = "done_without_impl";
    public const string MissingDirectory = "missing_directory";
    public const string StaleCode = "stale_code";
    public const string ChangesRel = "00-程序树/运维/文件变更.jsonl";

    public const int MaxTextLines = 12;
    public const int DefaultGuardLines = 5;
    public const int MaxGuardLines = 20;

    private static readonly string[] StatusOrder = { "未完成", "已完成", "已验证", "退役" };

    public static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static HealthReport Run(PTreeRepository repository)
    {
        var report = new HealthReport();
        foreach (var node in repository.Nodes)
        {
            if (node.Id is not { Length: > 0 } id) continue;
            report.CheckedNodes++;
            var status = StatusValues.Normalize(node.Status);
            var files = node.Files.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            if (files.Count == 0)
            {
                if (status is StatusValues.Done or StatusValues.Verified)
                {
                    report.Issues.Add(new HealthIssue
                    {
                        Level = "E",
                        Kind = DoneWithoutImpl,
                        Node = id,
                        Message = "已完成却无实现：status=" + status + " 但 files 为空",
                    });
                }
                continue;
            }
            report.NodesWithFiles++;
            var existing = new List<(string Relative, DateTime Modified)>();
            var missingDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relative in files)
            {
                var path = ResolvePath(repository.Root, relative);
                if (File.Exists(path))
                {
                    existing.Add((relative, File.GetLastWriteTime(path)));
                    continue;
                }
                if (Directory.Exists(path))
                {
                    existing.Add((relative, Directory.GetLastWriteTime(path)));
                    continue;
                }
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    if (missingDirectories.Add(parent))
                    {
                        var normalized = relative.Replace('\\', '/');
                        var slash = normalized.LastIndexOf('/');
                        var directory = slash > 0 ? normalized[..slash] : normalized;
                        report.Issues.Add(new HealthIssue
                        {
                            Level = "E",
                            Kind = MissingDirectory,
                            Node = id,
                            File = directory,
                            Message = "登记目录整体不存在：" + directory,
                        });
                    }
                    continue;
                }
                report.Issues.Add(new HealthIssue
                {
                    Level = "E",
                    Kind = MissingFile,
                    Node = id,
                    File = relative,
                    Message = "文件消失：" + relative,
                });
            }
            var updated = ParseDate(node.UpdatedAt) ?? ParseDate(repository.Manifest.Updated);
            if (existing.Count == 0 || updated is null) continue;
            foreach (var (relative, modified) in existing)
            {
                var days = (modified.Date - updated.Value.Date).Days;
                if (days <= 0) continue;
                report.StaleCount++;
                var change = RecordFileChange(repository.Root, id, relative, modified, days);
                if (change is not null) report.Changes.Add(change);
            }
        }
        // 备份是附属功能：只有 enabled=true 且全局密钥/凭据缺失时才追加一条 W
        BackupService.AppendHealthIssue(report, repository);
        return report;
    }

    /// <summary>命令行 health：汇总一行 + 每条问题一行，总行数不超过 12。</summary>
    public static string RenderText(HealthReport report)
    {
        var issues = Ordered(report.Issues);
        var lines = new List<string> { "检查 " + report.CheckedNodes + " 个节点，问题 " + issues.Count + " 条" };
        var tail = report.StaleCount == 0
            ? null
            : "另有 " + report.StaleCount + " 处代码改动晚于说明，已静默记录（changes 查）";
        var budget = MaxTextLines - 1 - (tail is null ? 0 : 1);
        var listing = issues.Count <= budget ? issues.Count : budget - 1;
        for (var index = 0; index < listing; index++) lines.Add(Line(issues[index]));
        if (issues.Count > listing) lines.Add("另有 " + (issues.Count - listing) + " 条，用 --json 取全量");
        if (tail is not null) lines.Add(tail);
        return string.Join("\n", lines);
    }

    /// <summary>tree_guard 卡：两行汇总 + 最多 limit 行最重要的问题；没问题只有两行。</summary>
    public static string RenderGuardCard(PTreeRepository repository, HealthReport report, ValidationPayload validation, int limit)
    {
        if (report.Issues.Count == 0) return "一切正常\n可以开始干活";
        var capped = Math.Clamp(limit <= 0 ? DefaultGuardLines : limit, 1, MaxGuardLines);
        var issues = Ordered(report.Issues);
        var lines = new List<string>
        {
            "tree_guard 程序树常检 · " + Today,
            "节点 " + repository.Nodes.Count + " 状态 " + StatusText(repository) + " · 校验 E" + validation.Errors.Count + " W" + validation.Warnings.Count,
        };
        for (var index = 0; index < Math.Min(capped, issues.Count); index++) lines.Add(Line(issues[index]));
        if (issues.Count > capped) lines.Add("另有 " + (issues.Count - capped) + " 条，用 format=full 取全量");
        return string.Join("\n", lines);
    }

    public static Dictionary<string, object?> ToPayload(HealthReport report)
    {
        var issues = Ordered(report.Issues);
        return new Dictionary<string, object?>
        {
            ["ok"] = report.CountOf(MissingFile) == 0 && report.CountOf(MissingDirectory) == 0 && report.CountOf(DoneWithoutImpl) == 0,
            ["generated_at"] = Today,
            ["checked_nodes"] = report.CheckedNodes,
            ["nodes_with_files"] = report.NodesWithFiles,
            ["issue_count"] = issues.Count,
            ["counts"] = new Dictionary<string, int>
            {
                [MissingFile] = report.CountOf(MissingFile),
                [DoneWithoutImpl] = report.CountOf(DoneWithoutImpl),
                [StaleCode] = report.StaleCount,
                [MissingDirectory] = report.CountOf(MissingDirectory),
            },
            ["issues"] = issues.Select(issue => new Dictionary<string, object?>
            {
                ["level"] = issue.Level,
                ["kind"] = issue.Kind,
                ["node"] = issue.Node,
                ["message"] = issue.Message,
                ["file"] = issue.File,
                ["days"] = issue.Days,
            }).ToList(),
        };
    }

    /// <summary>静默记录常规代码更新；同一 node+file 一天只追加一次。</summary>
    public static FileChange? RecordFileChange(string root, string node, string file, DateTime modified, int days)
    {
        try
        {
            var path = Path.Combine(root, ChangesRel.Replace('/', Path.DirectorySeparatorChar));
            var at = Today;
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var item = document.RootElement;
                        if (item.TryGetProperty("at", out var atValue) && atValue.GetString() == at &&
                            item.TryGetProperty("node", out var nodeValue) && nodeValue.GetString() == node &&
                            item.TryGetProperty("file", out var fileValue) && fileValue.GetString() == file.Replace('\\', '/'))
                            return null;
                    }
                    catch (JsonException) { }
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var change = new FileChange(at, node, file.Replace('\\', '/'),
                modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), days);
            File.AppendAllText(path, JsonSerializer.Serialize(change, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n", new UTF8Encoding(false));
            return change;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static List<FileChange> ReadChanges(string root, string? node, int days, int limit)
    {
        var path = Path.Combine(root, ChangesRel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return new List<FileChange>();
        var cutoff = DateTime.Now.Date.AddDays(-Math.Max(0, days));
        var result = new List<FileChange>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var change = JsonSerializer.Deserialize<FileChange>(line);
                if (change is null) continue;
                if (!string.IsNullOrWhiteSpace(node) && !string.Equals(change.Node, node, StringComparison.Ordinal)) continue;
                if (DateTime.TryParse(change.At, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at.Date < cutoff) continue;
                result.Add(change);
            }
            catch (JsonException) { }
        }
        return result.OrderByDescending(change => change.At, StringComparer.Ordinal)
            .Take(Math.Clamp(limit <= 0 ? 20 : limit, 1, 500)).ToList();
    }

    public static string RenderChanges(IReadOnlyList<FileChange> changes) =>
        changes.Count == 0 ? "没有文件变更记录" : string.Join("\n", changes.Select(change =>
            change.At + " " + change.Node + " " + change.File + " 晚 " + change.LateDays + " 天"));

    public static string ResolvePath(string root, string relative)
    {
        var trimmed = relative.Trim().Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(root, trimmed);
    }

    public static bool Exists(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return false;
        var path = ResolvePath(root, relative);
        return File.Exists(path) || Directory.Exists(path);
    }

    private static List<HealthIssue> Ordered(IEnumerable<HealthIssue> issues) => issues
        .OrderBy(issue => Rank(issue.Kind))
        .ThenBy(issue => issue.Node, StringComparer.Ordinal)
        .ThenBy(issue => issue.File ?? "", StringComparer.Ordinal)
        .ToList();

    private static int Rank(string kind) => kind switch
    {
        MissingFile => 0,
        MissingDirectory => 1,
        DoneWithoutImpl => 2,
        _ => 3,
    };

    private static string Line(HealthIssue issue) =>
        issue.Level + " " + issue.Node + " " + issue.Message;

    private static string StatusText(PTreeRepository repository)
    {
        var tally = repository.Nodes.GroupBy(node => StatusValues.Normalize(node.Status) ?? "?")
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return string.Join(";", StatusOrder.Select(status =>
            status + " " + (tally.TryGetValue(status, out var count) ? count : 0)));
    }

    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        var candidate = trimmed.Length >= 10 ? trimmed[..10] : trimmed;
        if (DateTime.TryParseExact(candidate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact.Date;
        if (DateTime.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed.Date;
        return null;
    }
}
