using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>C# 快照基线：对象键顺序无关、数组顺序敏感；忽略 generated_at。
/// 自 2026-11-06 起，本节期望值由 C# 内核快照固化；Python 参考版冻结在 09-归档，不再作为判据。
/// 需要刷新快照时设 PTREE_UPDATE_SNAPSHOT=1 运行测试，再人工复核 diff。</summary>
public sealed class ParityTests
{
    private static readonly Lazy<PTreeRepository> Repository = new(() => PTreeRepository.Load(RepoLocator.Root));
    private static readonly object SnapshotLock = new();

    private static string SnapshotFile => Path.Combine(RepoLocator.Root, "00-程序树", "工具", "csharp", "tests", "PTree.Tests", "Snapshots", "parity_snapshot.json");

    public static IEnumerable<object[]> SnapshotNodeIds()
        => Repository.Value.NodeMap.Keys.OrderBy(id => id, StringComparer.Ordinal).Select(id => new object[] { id });

    [Fact]
    public void IndexMatchesSnapshot()
    {
        var actual = JsonOutput.Serialize(Repository.Value.BuildIndex());
        var expected = LoadOrUpdateSnapshot("index", actual);
        // tree_version 每次写盘都递增，钉在金标里只会每写一次就红；版本格式由 CoreTests 管
        AssertJsonEqual(expected, actual, "generated_at", "tree_version");
    }

    [Theory]
    [MemberData(nameof(SnapshotNodeIds))]
    public void NodeMatchesGolden(string id)
    {
        var actual = JsonOutput.Serialize(Repository.Value.BuildNode(id));
        var expected = LoadOrUpdateSnapshot("node:" + id, actual);
        AssertJsonEqual(expected, actual);
    }

    private static string LoadOrUpdateSnapshot(string key, string actual)
    {
        lock (SnapshotLock)
        {
            var all = LoadSnapshots();
            if (Environment.GetEnvironmentVariable("PTREE_UPDATE_SNAPSHOT") == "1")
            {
                all[key] = actual;
                Directory.CreateDirectory(Path.GetDirectoryName(SnapshotFile)!);
                File.WriteAllText(SnapshotFile, JsonSerializer.Serialize(all,
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                    new System.Text.UTF8Encoding(false));
                return actual;
            }
            Assert.True(all.TryGetValue(key, out var expected),
                "缺少 C# 快照：" + key + "（人工确认内核口径变化后可设 PTREE_UPDATE_SNAPSHOT=1 重新生成）");
            return expected!;
        }
    }

    private static Dictionary<string, string> LoadSnapshots()
    {
        if (!File.Exists(SnapshotFile)) return new Dictionary<string, string>(StringComparer.Ordinal);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SnapshotFile));
        return loaded is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(loaded, StringComparer.Ordinal);
    }

    private static void AssertJsonEqual(string expectedJson, string actualJson, params string[] ignoredRootKeys)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        var ignored = new HashSet<string>(ignoredRootKeys, StringComparer.Ordinal);
        var problems = new List<string>();
        Compare(expected.RootElement, actual.RootElement, "$", ignored, problems);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
    }

    private static void Compare(JsonElement expected, JsonElement actual, string path, ISet<string> ignoredRootKeys, List<string> problems)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            problems.Add($"{path}: 类型 {expected.ValueKind} != {actual.ValueKind}");
            return;
        }
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var expectedProperties = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var actualProperties = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var expectedKeys = expectedProperties.Keys.Where(key => !ignoredRootKeys.Contains(key)).ToHashSet(StringComparer.Ordinal);
                foreach (var key in expectedKeys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!actualProperties.TryGetValue(key, out var actualValue))
                    {
                        problems.Add($"{path}.{key}: 候选缺失");
                        continue;
                    }
                    Compare(expectedProperties[key], actualValue, path + "." + key, ignoredRootKeys, problems);
                }
                foreach (var key in actualProperties.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!ignoredRootKeys.Contains(key) && !expectedKeys.Contains(key))
                    {
                        problems.Add($"{path}.{key}: 候选多出");
                    }
                }
                break;
            }
            case JsonValueKind.Array:
            {
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();
                if (expectedItems.Count != actualItems.Count)
                {
                    problems.Add($"{path}: 长度 {expectedItems.Count} != {actualItems.Count}");
                }
                for (var index = 0; index < Math.Min(expectedItems.Count, actualItems.Count); index++)
                {
                    Compare(expectedItems[index], actualItems[index], $"{path}[{index}]", ignoredRootKeys, problems);
                }
                break;
            }
            case JsonValueKind.String:
                if (!string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
                {
                    problems.Add($"{path}: {Truncate(expected.GetString())} != {Truncate(actual.GetString())}");
                }
                break;
            case JsonValueKind.Number:
                if (expected.GetRawText() != actual.GetRawText())
                {
                    problems.Add($"{path}: {expected.GetRawText()} != {actual.GetRawText()}");
                }
                break;
            default:
                break;
        }
    }

    private static string Truncate(string? value)
        => value is null ? "null" : (value.Length <= 80 ? value : value[..80] + "…");
}
