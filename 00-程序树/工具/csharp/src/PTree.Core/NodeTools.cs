using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PTree.Core;

/// <summary>节点级 MCP 工具实现：AI 只给字段，不碰文件；写操作原子落盘、台账、重生成文档。</summary>
public static partial class NodeTools
{
    private const int GitTimeoutMs = 120_000;

    // 联系类型白名单与 R40 共用 LinkKinds 一份，避免写入口和校验两处各写一遍
    private static readonly string[] LinkTypes = LinkKinds.All;

    private static readonly Regex IdPattern = new(@"^[a-z][a-z0-9-]*(\.[a-z0-9-]+)*$", RegexOptions.CultureInvariant);

    private static readonly Regex SlugPattern = new(@"^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);

    private static readonly string[] ReadonlyKinds = { "root", "department", "program", "step", "external" };

    /// <summary>AddNode 与 UpdateNode 共用：让节点 inputs/outputs 与契约 consumers/producers 双向一致。</summary>
    private static void SyncContractMembership(RawMap contracts, string nodeId,
        IReadOnlyList<object?> oldInputs, IReadOnlyList<object?> oldOutputs,
        IReadOnlyList<object?> newInputs, IReadOnlyList<object?> newOutputs)
    {
        static bool Contains(IReadOnlyList<object?> values, string id) =>
            values.OfType<string>().Contains(id, StringComparer.Ordinal);
        static void AddUnique(List<object?> values, string id)
        {
            if (!values.OfType<string>().Contains(id, StringComparer.Ordinal)) values.Add(id);
        }
        static void RemoveAll(List<object?> values, string id) =>
            values.RemoveAll(value => value is string text && string.Equals(text, id, StringComparison.Ordinal));

        foreach (var contract in ListOf(contracts, "contracts").OfType<RawMap>())
        {
            var contractId = StringOf(contract, "id");
            if (contractId is null) continue;
            var producers = EnsureList2(contract, "producers");
            if (Contains(newOutputs, contractId)) AddUnique(producers, nodeId);
            else if (Contains(oldOutputs, contractId)) RemoveAll(producers, nodeId);

            var consumers = EnsureList2(contract, "consumers");
            if (Contains(newInputs, contractId)) AddUnique(consumers, nodeId);
            else if (Contains(oldInputs, contractId)) RemoveAll(consumers, nodeId);
        }
    }

    private sealed record CommitOutcome(bool Ok, List<Dictionary<string, object?>> Errors, string? Error,
        string? TreeVersion, string? LedgerId, List<string> Files);

    private static CommitOutcome Commit(PTreeRepository repository, string actor, string reason,
        Action<RawMap> mutate, Action<RawMap>? contractsMutation, Dictionary<string, string?> introWrites, List<string> summaries)
    {
        var root = repository.Root;
        var manifestPath = Path.Combine(root, PTreeRepository.ManifestRel.Replace('/', Path.DirectorySeparatorChar));
        var contractsPath = repository.ContractsPath;
        var generatedDir = Path.Combine(root, "00-程序树", "生成");
        var ledgerPath = Path.Combine(root, "00-程序树", "变更", "台账.jsonl");
        var generated = new[] { "程序树总览.txt", "落地顺序.txt", "程序书.txt", "标杆看板.txt", BriefRenderer.FileName };
        var backups = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        void Backup(string path) { if (!backups.ContainsKey(path)) backups[path] = File.Exists(path) ? File.ReadAllBytes(path) : null; }
        Backup(manifestPath);
        Backup(contractsPath);
        Backup(ledgerPath);
        foreach (var name in generated) Backup(Path.Combine(generatedDir, name));
        foreach (var id in introWrites.Keys) Backup(Path.Combine(root, PTreeRepository.IntroDirRel.Replace('/', Path.DirectorySeparatorChar), id + PTreeRepository.IntroExtension));

        var manifest = CloneMap(repository.ManifestRaw);
        try
        {
            mutate(manifest);
            manifest.Set("updated", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var versionAfter = BumpVersion(repository.Manifest.TreeVersion);
            manifest.Set("tree_version", versionAfter);
            WriteText(manifestPath, YamlEmitter.Dump(manifest));
            if (contractsMutation is not null && repository.ContractsRaw is RawMap contractsRaw)
            {
                var contracts = CloneMap(contractsRaw);
                contractsMutation(contracts);
                contracts.Set("updated", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                WriteText(contractsPath, YamlEmitter.Dump(contracts));
            }
            foreach (var pair in introWrites)
            {
                var path = Path.Combine(root, PTreeRepository.IntroDirRel.Replace('/', Path.DirectorySeparatorChar), pair.Key + PTreeRepository.IntroExtension);
                if (pair.Value is null) { if (File.Exists(path)) File.Delete(path); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var text = pair.Value.Replace("\r\n", "\n").Replace('\r', '\n');
                    WriteText(path, text.EndsWith('\n') ? text : text + "\n");
                }
            }

            var reloaded = PTreeRepository.Load(root);
            var validation = Validator.Validate(reloaded);
            var store = TreeStore.Load(root);
            var checkIssues = TreeChecker.Check(store);
            checkIssues.AddRange(ManifestChecker.Check(reloaded, store));
            var errors = new List<Dictionary<string, object?>>();
            errors.AddRange(validation.Errors.Select(issue => Issue("E", issue.Code, issue.Node, issue.Message)));
            errors.AddRange(checkIssues.Where(issue => issue.Level == "E").Select(issue => Issue("E", issue.Code, issue.Kind, issue.Message)));
            if (errors.Count > 0)
            {
                Restore(backups);
                return new CommitOutcome(false, errors.Take(50).ToList(), errors[0]["message"] as string, repository.Manifest.TreeVersion, null, new List<string>());
            }

            GeneratedProducts.WriteAll(reloaded);
            WriteText(Path.Combine(generatedDir, BriefRenderer.FileName), BriefRenderer.Render(reloaded));
            var ledgerId = AppendLedger(reloaded, ledgerPath, actor, reason, summaries, validation, repository.Manifest.TreeVersion, versionAfter);
            var written = new List<string> { Path.GetRelativePath(root, manifestPath).Replace('\\', '/') };
            if (contractsMutation is not null) written.Add(Path.GetRelativePath(root, contractsPath).Replace('\\', '/'));
            written.Add("00-程序树/变更/台账.jsonl");
            return new CommitOutcome(true, errors, null, versionAfter, ledgerId,
                written.Concat(
                    new[] { "程序树总览.txt", "落地顺序.txt", "程序书.txt", "标杆看板.txt", BriefRenderer.FileName }
                        .Select(name => "00-程序树/生成/" + name)).ToList());
        }
        catch (Exception exception)
        {
            Restore(backups);
            return new CommitOutcome(false, new List<Dictionary<string, object?>>(), exception.Message, repository.Manifest.TreeVersion, null, new List<string>());
        }
    }

    private static string AppendLedger(PTreeRepository repository, string path, string actor, string reason,
        List<string> summaries, ValidationPayload validation, string? versionBefore, string? versionAfter)
    {
        var entries = new List<JsonElement>();
        if (File.Exists(path))
        {
            foreach (var line in PythonText.SplitLines(File.ReadAllText(path)))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { using var document = JsonDocument.Parse(line); entries.Add(document.RootElement.Clone()); } catch { }
            }
        }
        var prefix = "chg-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";
        var maximum = 0;
        foreach (var entry in entries)
        {
            if (entry.TryGetProperty("id", out var id) && id.GetString() is { } text && text.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(text[prefix.Length..], out var number)) maximum = Math.Max(maximum, number);
        }
        var ledgerId = prefix + (maximum + 1).ToString("0000", CultureInfo.InvariantCulture);
        var ledger = new Dictionary<string, object?>
        {
            ["id"] = ledgerId,
            ["at"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["actor"] = actor,
            ["proposal"] = "00-程序树/变更",
            ["reason"] = reason,
            ["ops"] = summaries,
            ["validation"] = new Dictionary<string, int> { ["errors"] = validation.Errors.Count, ["warnings"] = validation.Warnings.Count },
            ["tree_version_before"] = versionBefore,
            ["tree_version_after"] = versionAfter,
            ["result"] = "applied",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(ledger, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n", new UTF8Encoding(false));
        return ledgerId;
    }

    private static string BumpVersion(string? current)
    {
        var parts = (current ?? "1.0.0").Split('.');
        if (parts.Length != 3 || !parts.All(part => int.TryParse(part, out _))) return "1.0.1";
        var patch = int.Parse(parts[2], CultureInfo.InvariantCulture) + 1;
        return parts[0] + "." + parts[1] + "." + patch;
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    // ------------------------------------------------------------------
    // 读取辅助
    // ------------------------------------------------------------------

    private static RawMap CloneMap(RawMap source)
    {
        var clone = new RawMap();
        foreach (var pair in source) clone.Set(pair.Key, CloneValue(pair.Value));
        return clone;
    }

    private static object? CloneValue(object? value) => value switch
    {
        RawMap map => CloneMap(map),
        List<object?> list => list.Select(CloneValue).ToList(),
        _ => value,
    };

    private static object? JsonValue(object? value) => value switch
    {
        RawMap map => map.ToDictionary(pair => pair.Key, pair => JsonValue(pair.Value), StringComparer.Ordinal),
        List<object?> list => list.Select(JsonValue).ToList(),
        _ => value,
    };

    private static string Result(CommitOutcome outcome, string? id, string? currentVersion, Dictionary<string, object?> extra)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = outcome.Ok,
            ["id"] = id,
            ["tree_version"] = outcome.Ok ? outcome.TreeVersion : currentVersion,
            ["errors"] = outcome.Errors,
            ["written_files"] = outcome.Files,
            ["ledger"] = outcome.LedgerId,
        };
        if (outcome.Error is not null) payload["error"] = outcome.Error;
        foreach (var pair in extra) payload[pair.Key] = pair.Value;
        return JsonOutput.Serialize(payload);
    }

    private static string Fail(IEnumerable<string> errors, PTreeRepository repository)
    {
        var list = errors.ToList();
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = list.FirstOrDefault() ?? "参数错误",
            ["errors"] = list.Select(message => Issue("E", "TOOL", null, message)).ToList(),
            ["tree_version"] = repository.Manifest.TreeVersion,
        });
    }

    private static string? StringArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int? IntArg(JsonElement? args, string name) =>
        int.TryParse(StringArg(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? BoolArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static List<object?>? ListArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Array) return null;
        return property.EnumerateArray().Select(item => item.ValueKind switch
        {
            JsonValueKind.String => (object?)item.GetString(),
            JsonValueKind.Number => item.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        }).Where(item => item is not null).ToList();
    }

    private static bool ValuesEqual(object? left, object? right) => JsonSerializer.Serialize(JsonValue(left)) == JsonSerializer.Serialize(JsonValue(right));

    private static string Brief(object? value)
    {
        if (value is null) return "∅";
        if (value is string text) return text.Length <= 40 ? text : text[..40] + "…";
        return JsonSerializer.Serialize(JsonValue(value), new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) is { Length: <= 60 } json ? json : JsonSerializer.Serialize(JsonValue(value), new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        })[..60] + "…";
    }
}
