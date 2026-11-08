using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PTree.Core;

public static partial class NodeTools
{
    private static void Restore(Dictionary<string, byte[]?> backups)
    {
        foreach (var pair in backups)
        {
            try
            {
                if (pair.Value is null) { if (File.Exists(pair.Key)) File.Delete(pair.Key); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                    var temp = pair.Key + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllBytes(temp, pair.Value);
                    File.Move(temp, pair.Key, overwrite: true);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 写操作收尾：成功且清单声明 auto_commit 时，把本次写盘文件提交成一条 git 记录；
    /// 再把这次 tree_version 快照映射到备份点（备份是附属功能，未启用时什么都不做）。
    /// </summary>
    private static string Result(PTreeRepository repository, CommitOutcome outcome, string? id, string? currentVersion, Dictionary<string, object?> extra)
    {
        var text = Result(outcome, id, currentVersion, extra);
        if (!outcome.Ok || outcome.Files.Count == 0) return text;
        JsonElement? commit = null;
        if (AutoCommitEnabled(repository))
        {
            var commitText = GitSync(repository.Root, outcome.Files, "auto: " + (outcome.LedgerId ?? "tree change"));
            try
            {
                commit = JsonDocument.Parse(commitText).RootElement.Clone();
            }
            catch (JsonException)
            {
                commit = null;
            }
        }
        var backup = BackupService.AfterTreeChange(repository, outcome.TreeVersion ?? repository.Manifest.TreeVersion);
        if (commit is null && backup is null) return text;
        try
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(text);
            if (payload is null) return text;
            if (commit is not null) payload["commit"] = commit.Value;
            if (backup is not null) payload["backup"] = backup;
            return JsonOutput.Serialize(payload);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static bool AutoCommitEnabled(PTreeRepository repository)
    {
        if (!repository.ManifestRaw.TryGetValue("conventions", out var raw) || raw is not RawMap conventions) return false;
        return conventions.TryGetValue("auto_commit", out var flag) && flag is true;
    }
}
