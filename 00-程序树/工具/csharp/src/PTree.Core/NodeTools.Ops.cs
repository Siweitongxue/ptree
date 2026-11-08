using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PTree.Core;

public static partial class NodeTools
{
    /// <summary>tree_guard：一次常检 = HealthCheck + validate 计数；默认一张 ≤10 行卡。</summary>
    public static string Guard(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var format = (StringArg(args, "format") ?? "card").Trim().ToLowerInvariant();
        if (format is not ("card" or "full"))
            return Fail(new[] { "format 只能是 card（默认纯文本卡）或 full（原 JSON）" }, repository);
        var limit = IntArg(args, "limit") ?? HealthCheck.DefaultGuardLines;
        if (limit <= 0) limit = HealthCheck.DefaultGuardLines;
        var report = HealthCheck.Run(repository);
        var validation = Validator.Validate(repository);
        if (format == "card") return HealthCheck.RenderGuardCard(repository, report, validation, limit);
        var payload = HealthCheck.ToPayload(report);
        payload["tree_version"] = repository.Manifest.TreeVersion;
        payload["validation"] = new Dictionary<string, object?>
        {
            ["ok"] = validation.Ok,
            ["errors"] = validation.Errors.Count,
            ["warnings"] = validation.Warnings.Count,
        };
        return JsonOutput.Serialize(payload);
    }

    /// <summary>tree_relocate：把节点登记的代码路径迁到新位置（可选同时换 parent）；校验新路径存在、parent 存在且不成环。</summary>
    public static string Relocate(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var id = StringArg(args, "id");
        var reason = StringArg(args, "reason");
        var parentArg = StringArg(args, "parent");
        var rawFiles = ListArg(args, "files");
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(id)) errors.Add("id 不能为空");
        var files = rawFiles?.OfType<string>().Select(trim => trim.Trim())
            .Where(trim => trim.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (files is null || files.Count == 0) errors.Add("files 至少 1 条：迁移必须给新的登记路径");
        if (errors.Count > 0) return Fail(errors, repository);
        if (!repository.NodeMap.TryGetValue(id!, out var node)) return Fail(new[] { "节点不存在：" + id }, repository);
        foreach (var file in files!) if (!HealthCheck.Exists(repository.Root, file)) errors.Add("新路径不存在：" + file);

        var oldParent = node.Parent;
        var newParent = string.IsNullOrWhiteSpace(parentArg) ? null : parentArg!.Trim();
        var parentChanged = newParent is not null && !string.Equals(newParent, oldParent, StringComparison.Ordinal);
        var newLayer = node.Layer ?? 0;
        if (newParent is not null)
        {
            if (node.Kind == "root") errors.Add("根节点不能改 parent：" + id);
            else if (!repository.NodeMap.TryGetValue(newParent, out var parentNode)) errors.Add("新 parent 不存在：" + newParent);
            else if (string.Equals(newParent, id, StringComparison.Ordinal)) errors.Add("新 parent 不能是节点自己：" + newParent);
            else if (IsDescendant(repository, newParent, id!)) errors.Add("新 parent 是它的子节点，会成环：" + newParent);
            else
            {
                var expected = parentNode.Kind switch
                {
                    "root" => new[] { "department", "external" },
                    "department" => new[] { "program", "external" },
                    "program" => new[] { "step", "external" },
                    _ => Array.Empty<string>(),
                };
                if (expected.Length == 0 || !expected.Contains(node.Kind ?? "", StringComparer.Ordinal))
                    errors.Add("parent（" + parentNode.Kind + "）下不能挂 kind=" + node.Kind);
                else newLayer = (parentNode.Layer ?? 0) + 1;
            }
        }
        if (errors.Count > 0) return Fail(errors, repository);

        var oldFiles = node.Files.ToList();
        var diffs = DiffPaths(oldFiles, files!);
        if (parentChanged) diffs.Add("parent：" + (oldParent ?? "∅") + " -> " + newParent);
        var summaries = new List<string>
        {
            "relocate " + id + "：" + string.Join(",", oldFiles) + " -> " + string.Join(",", files!),
        };
        var outcome = Commit(repository, "mcp:tree_relocate",
            "MCP 迁移节点代码路径 " + id + (string.IsNullOrWhiteSpace(reason) ? "" : "：" + reason),
            manifest =>
            {
                var target = FindNode(manifest, id!);
                if (target is null) throw new PTreeException("节点不存在：" + id);
                target.Set("files", files!.Cast<object?>().ToList());
                if (parentChanged)
                {
                    target.Set("parent", newParent);
                    target.Set("layer", newLayer);
                    var depends = ListOf(target, "depends_on");
                    var replaced = false;
                    for (var index = 0; index < depends.Count; index++)
                    {
                        if (depends[index] is string dependency && string.Equals(dependency, oldParent, StringComparison.Ordinal))
                        {
                            depends[index] = newParent;
                            replaced = true;
                        }
                    }
                    if (replaced) target.Set("depends_on", depends);
                }
                target.Set("updated_at", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            },
            null,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            summaries);
        return Result(repository, outcome, id, repository.Manifest.TreeVersion, new Dictionary<string, object?>
        {
            ["diff"] = diffs,
            ["old_files"] = oldFiles,
            ["files"] = files,
            ["parent"] = newParent,
            ["reason"] = reason,
        });
    }

    /// <summary>tree_delete：彻底删除一个节点（节点段、组、links、契约、台账）；有子节点或被依赖时拒绝。</summary>
    public static string Delete(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var id = StringArg(args, "id");
        var reason = StringArg(args, "reason")?.Trim();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(id)) errors.Add("id 不能为空");
        if (string.IsNullOrWhiteSpace(reason) || reason!.Length < 8)
            errors.Add("reason 至少 8 个字：删除不可恢复，必须写清原因");
        if (errors.Count > 0) return Fail(errors, repository);
        if (!repository.NodeMap.TryGetValue(id!, out var node)) return Fail(new[] { "节点不存在：" + id }, repository);

        var children = repository.Nodes
            .Where(other => other.Id is { Length: > 0 } && string.Equals(other.Parent, id, StringComparison.Ordinal))
            .Select(other => other.Id!).OrderBy(value => value, StringComparer.Ordinal).ToList();
        if (children.Count > 0)
        {
            return Fail(new[] { "有子节点，拒绝删除（先迁移或删除子节点）：" + string.Join("、", children) }
                .Concat(children.Select(child => "子节点：" + child)), repository);
        }
        var dependents = repository.Nodes
            .Where(other => other.Id is { Length: > 0 } otherId && otherId != id &&
                            other.DependsOn.Contains(id!, StringComparer.Ordinal))
            .Select(other => other.Id!).OrderBy(value => value, StringComparer.Ordinal).ToList();
        if (dependents.Count > 0)
        {
            return Fail(new[] { "有节点 depends_on 它，拒绝删除（先改依赖）：" + string.Join("、", dependents) }
                .Concat(dependents.Select(dependent => "依赖它：" + dependent)), repository);
        }

        var removedFieldCount = 0;
        var removedName = node.Name;
        var removedKind = node.Kind;
        var removedStatus = node.Status;
        var removedMembers = 0;
        var removedLinks = 0;
        var contractsTouched = new List<string>();
        var contractsRemoved = new List<string>();
        var summaries = new List<string> { "delete " + id + "：" + reason };
        var outcome = Commit(repository, "mcp:tree_delete", "MCP 删除节点 " + id + "：" + reason,
            manifest =>
            {
                var nodes = EnsureList(manifest, "nodes");
                var index = nodes.FindIndex(item => item is RawMap map && StringOf(map, "id") == id);
                if (index < 0) throw new PTreeException("节点不存在：" + id);
                if (nodes[index] is RawMap removed)
                {
                    removedFieldCount = removed.Count;
                    removedName = StringOf(removed, "name") ?? removedName;
                    removedKind = StringOf(removed, "kind") ?? removedKind;
                    removedStatus = StringOf(removed, "status") ?? removedStatus;
                }
                nodes.RemoveAt(index);
                foreach (var group in EnsureList(manifest, "groups").OfType<RawMap>())
                {
                    removedMembers += EnsureList2(group, "members")
                        .RemoveAll(member => member is string text && string.Equals(text, id, StringComparison.Ordinal));
                }
                removedLinks = EnsureList(manifest, "links").RemoveAll(item => item is RawMap link &&
                    (StringOf(link, "from") == id || StringOf(link, "to") == id));
            },
            contracts =>
            {
                var items = ListOf(contracts, "contracts");
                foreach (var contract in items.OfType<RawMap>())
                {
                    var contractId = StringOf(contract, "id");
                    if (contractId is null) continue;
                    var touched = 0;
                    foreach (var field in new[] { "producers", "consumers" })
                    {
                        if (!contract.TryGetValue(field, out var rawValues) || rawValues is not List<object?> values) continue;
                        touched += values.RemoveAll(value => value is string text && string.Equals(text, id, StringComparison.Ordinal));
                    }
                    if (touched > 0) contractsTouched.Add(contractId);
                }
                for (var index = items.Count - 1; index >= 0; index--)
                {
                    if (items[index] is not RawMap contract) continue;
                    var contractId = StringOf(contract, "id");
                    if (contractId is null || !contractsTouched.Contains(contractId, StringComparer.Ordinal)) continue;
                    if (!IsEmptyContract(contract)) continue;
                    contractsRemoved.Add(contractId);
                    items.RemoveAt(index);
                }
            },
            new Dictionary<string, string?>(StringComparer.Ordinal) { [id!] = null },
            summaries);
        var removedFiles = node.Files.ToList();
        return Result(repository, outcome, id, repository.Manifest.TreeVersion, new Dictionary<string, object?>
        {
            ["deleted"] = new Dictionary<string, object?>
            {
                ["node"] = id,
                ["name"] = removedName,
                ["kind"] = removedKind,
                ["status"] = removedStatus,
                ["field_count"] = removedFieldCount,
                ["files"] = removedFiles,
                ["links_removed"] = removedLinks,
                ["group_members_removed"] = removedMembers,
                ["contracts_touched"] = contractsTouched,
                ["contracts_removed"] = contractsRemoved,
            },
            ["summary"] = "删除节点 " + id + "（" + removedKind + "，" + removedStatus + "）：字段 " + removedFieldCount +
                         " 条，links " + removedLinks + " 条，契约 " + contractsTouched.Count + " 个（移除空契约 " +
                         contractsRemoved.Count + " 个），组归属 " + removedMembers + " 处",
        });
    }

    private static bool IsDescendant(PTreeRepository repository, string candidate, string ancestor)
    {
        var current = candidate;
        var guard = 0;
        while (!string.IsNullOrEmpty(current) && guard++ < 200)
        {
            if (string.Equals(current, ancestor, StringComparison.Ordinal)) return true;
            if (!repository.NodeMap.TryGetValue(current, out var node)) return false;
            current = node.Parent ?? "";
        }
        return false;
    }

    private static List<string> DiffPaths(List<string> before, List<string> after)
    {
        if (before.Count == after.Count)
        {
            var lines = new List<string>();
            for (var index = 0; index < before.Count; index++)
            {
                if (!string.Equals(before[index], after[index], StringComparison.Ordinal))
                    lines.Add(before[index] + " -> " + after[index]);
            }
            if (lines.Count == 0) lines.Add("files 未变化：" + string.Join(",", after));
            return lines;
        }
        var changed = before.Where(old => !after.Contains(old, StringComparer.Ordinal))
            .Select(old => old + " -> （移除）").ToList();
        changed.AddRange(after.Where(fresh => !before.Contains(fresh, StringComparer.Ordinal))
            .Select(fresh => "（新增） -> " + fresh));
        return changed;
    }

    private static bool IsEmptyContract(RawMap contract)
    {
        var producers = ListOf(contract, "producers").Count;
        var consumers = ListOf(contract, "consumers").Count;
        var external = contract.TryGetValue("external", out var ext) && ext is true;
        var externalConsumers = contract.TryGetValue("external_consumers", out var extConsumers) && extConsumers is true;
        return (producers == 0 && !external) || (consumers == 0 && !externalConsumers);
    }

    public static string GitSync(string? root, IEnumerable<string>? files, string? message)
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["written_files"] = files?.ToList() ?? new List<string>(),
            ["message"] = message,
        });
        return GitSync(root, payload);
    }

    public static string GitSync(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var rawFiles = ListArg(args, "written_files");
        if (rawFiles is null || rawFiles.Count == 0)
            return GitFail(repository, "written_files 不能为空：请传本次写操作返回的文件路径");
        var files = new List<string>();
        foreach (var raw in rawFiles.OfType<string>())
        {
            var normalized = raw.Replace('\\', '/').Trim();
            if (normalized.Length == 0 || Path.IsPathRooted(normalized)) return GitFail(repository, "written_files 必须是仓库内相对路径：" + raw);
            var absolute = Path.GetFullPath(Path.Combine(repository.Root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(repository.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return GitFail(repository, "written_files 越出仓库：" + raw);
            var relative = Path.GetRelativePath(repository.Root, absolute).Replace('\\', '/');
            if (!files.Contains(relative, StringComparer.Ordinal)) files.Add(relative);
        }
        var message = StringArg(args, "message");
        var ledger = LatestLedger(repository.Root);
        if (string.IsNullOrWhiteSpace(message))
        {
            if (ledger is null) return GitFail(repository, "缺少 commit message，且台账没有可用的 chg 条目");
            message = ledger.Value.Id + " " + ledger.Value.Summary;
        }
        var inside = RunGit(repository.Root, "rev-parse", "--is-inside-work-tree");
        if (inside.ExitCode != 0 || !inside.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            return GitFail(repository, "git 不可用或不在仓库内");
        var add = RunGit(repository.Root, new[] { "add", "--all", "--" }.Concat(files));
        if (add.ExitCode != 0) return GitFail(repository, "git add 失败：" + FirstLine(add.Stderr));
        var diffArgs = new[] { "diff", "--cached", "--quiet", "--" }.Concat(files).ToArray();
        var diff = RunGit(repository.Root, diffArgs);
        if (diff.ExitCode == 0)
        {
            return JsonOutput.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["committed"] = false,
                ["message"] = message,
                ["files"] = files,
                ["groups"] = GroupFiles(files),
                ["ledger"] = ledger?.Id,
                ["note"] = "没有可提交的改动，未产生空提交",
            });
        }
        if (diff.ExitCode != 1) return GitFail(repository, "git diff 失败：" + FirstLine(diff.Stderr));
        var commit = RunGit(repository.Root, new[] { "commit", "-m", message, "--" }.Concat(files));
        if (commit.ExitCode != 0) return GitFail(repository, "git commit 失败：" + FirstLine(commit.Stderr));
        var head = RunGit(repository.Root, "rev-parse", "--short", "HEAD");
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["committed"] = true,
            ["message"] = message,
            ["commit"] = head.ExitCode == 0 ? head.Stdout.Trim() : null,
            ["files"] = files,
            ["groups"] = GroupFiles(files),
            ["ledger"] = ledger?.Id,
        });
    }

    private static string GitFail(PTreeRepository repository, string error) => JsonOutput.Serialize(new Dictionary<string, object?>
    {
        ["ok"] = false,
        ["committed"] = false,
        ["error"] = error,
        ["tree_version"] = repository.Manifest.TreeVersion,
        ["files"] = new List<string>(),
    });

    private static Dictionary<string, List<string>> GroupFiles(List<string> files) =>
        files.GroupBy(path => path.Contains('/') ? path[..path.LastIndexOf('/')] : ".", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    private static (string Id, string Summary)? LatestLedger(string root)
    {
        var path = Path.Combine(root, "00-程序树", "变更", "台账.jsonl");
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            try
            {
                using var document = JsonDocument.Parse(lines[index]);
                var element = document.RootElement;
                if (element.ValueKind != JsonValueKind.Object) continue;
                var id = element.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var summary = element.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
                if (string.IsNullOrWhiteSpace(summary) && element.TryGetProperty("ops", out var ops) && ops.ValueKind == JsonValueKind.Array)
                    summary = string.Join("；", ops.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)));
                if (summary is { Length: > 80 }) summary = summary[..80];
                return (id, summary ?? "树改动");
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunGit(string root, params string[] arguments) =>
        RunGit(root, (IEnumerable<string>)arguments);

    private static (int ExitCode, string Stdout, string Stderr) RunGit(string root, IEnumerable<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(root);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return (-1, "", "无法启动 git");
            // 两路管道必须并发读：先读完 stdout 再读 stderr，任一路写满就会双方互等（死锁）。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeoutMs))
            {
                try { process.Kill(true); } catch { }
                return (-1, "", "git 超时（" + GitTimeoutMs / 1000 + " 秒）");
            }
            return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }
        catch (Exception exception)
        {
            return (-1, "", exception.Message);
        }
    }

    private static string FirstLine(string text) => text.Split('\n').FirstOrDefault()?.Trim() ?? text;

    // ------------------------------------------------------------------
    // 写盘、校验、台账、重生成
    // ------------------------------------------------------------------
}
