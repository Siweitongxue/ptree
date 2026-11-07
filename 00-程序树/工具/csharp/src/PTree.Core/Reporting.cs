using System.Text.Json;

namespace PTree.Core;

/// <summary>只读统计/台账/报告辅助；不含 CLI 参数解析。</summary>
public static class Reporting
{
    public static object BuildStats(PTreeRepository repository)
    {
        var nodes = repository.ManifestRaw.TryGetValue("nodes", out var value) && value is List<object?> list
            ? list.OfType<RawMap>().ToList()
            : new List<RawMap>();
        var byStatus = new Dictionary<string, int>(StringComparer.Ordinal);
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var byPhase = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            string? Kind(string key) => node.TryGetValue(key, out var raw) ? raw as string : null;
            if (Kind("status") is { } status) byStatus[status] = byStatus.GetValueOrDefault(status) + 1;
            if (Kind("kind") is { } kind) byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
            if (Kind("phase") is { } phase) byPhase[phase] = byPhase.GetValueOrDefault(phase) + 1;
        }
        var departments = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.TryGetValue("kind", out var kind) && kind as string != "department") continue;
            if (node.TryGetValue("id", out var id) && id is string departmentId && departmentId.Length > 0)
                departments[departmentId] = nodes.Count(other => other.TryGetValue("id", out var otherId) &&
                    otherId is string text && text.StartsWith(departmentId + ".", StringComparison.Ordinal));
        }
        return new
        {
            tree_version = repository.Manifest.TreeVersion,
            total = nodes.Count,
            by_status = byStatus,
            by_kind = byKind,
            by_phase = byPhase,
            by_department = departments,
        };
    }

    public static List<JsonElement> ReadLedger(PTreeRepository repository)
    {
        var path = Path.Combine(repository.Root, "00-程序树", "变更", "台账.jsonl");
        var result = new List<JsonElement>();
        if (!File.Exists(path)) return result;
        foreach (var line in PythonText.SplitLines(File.ReadAllText(path)))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                result.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
            }
        }
        return result;
    }

}
