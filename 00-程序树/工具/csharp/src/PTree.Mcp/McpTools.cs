using System.Globalization;
using System.Text.Json;
using PTree.Core;

namespace PTree.Mcp;

/// <summary>MCP 工具实现：只复用 Core，不复制校验逻辑；输出统一 JSON 文本。</summary>
public static class McpTools
{
    private static readonly string[] ChangeDir = { "00-程序树", "变更" };

    public static string Status(string? root)
    {
        var repository = PTreeRepository.Load(root);
        var validation = Validator.Validate(repository);
        var stats = Reporting.BuildStats(repository);
        var result = new Dictionary<string, object?>
        {
            ["ok"] = validation.Ok,
            ["counts"] = new Dictionary<string, int>
            {
                ["errors"] = validation.Errors.Count,
                ["warnings"] = validation.Warnings.Count,
                ["notes"] = validation.Notes.Count,
            },
            ["errors"] = validation.Errors.Take(5).ToList(),
            ["warnings"] = validation.Warnings.Take(5).ToList(),
            ["notes_summary"] = validation.Notes.GroupBy(note => note.Code).ToDictionary(group => group.Key, group => group.Count()),
            ["stats"] = stats,
            ["benchmarks"] = new Dictionary<string, object?>
            {
                ["benchmarks"] = repository.Benchmarks.Benchmarks.Count,
                ["bindings"] = repository.Bindings.Bindings.Count,
            },
            ["kernel"] = KernelPayload(),
        };
        return JsonOutput.Serialize(result);
    }

    public static string Read(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var scope = StringArg(args, "scope") ?? "tree";
        switch (scope)
        {
            case "node":
            {
                var nodeId = StringArg(args, "node_id");
                if (string.IsNullOrWhiteSpace(nodeId))
                {
                    return ErrorJson("scope=node 必须给 node_id");
                }
                try
                {
                    return JsonOutput.Serialize(WithKernel(repository.BuildNode(nodeId)));
                }
                catch (PTreeException exception)
                {
                    return ErrorJson(exception.Message);
                }
            }
            case "tree":
                return JsonOutput.Serialize(WithKernel(repository.BuildIndex()));
            case "benchmark":
                return JsonOutput.Serialize(BuildBenchmarkPayload(repository));
            case "changes":
            {
                var limit = IntArg(args, "limit") ?? 10;
                if (limit <= 0) limit = 10;
                var entries = Reporting.ReadLedger(repository);
                var nodeFilter = StringArg(args, "node_id");
                if (!string.IsNullOrWhiteSpace(nodeFilter))
                {
                    entries = entries.Where(entry => HasNode(entry, nodeFilter)).ToList();
                }
                entries = Enumerable.Reverse(entries).Take(limit).ToList();
                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["entries"] = entries,
                    ["count"] = entries.Count,
                    ["filter_node"] = nodeFilter,
                    ["limit"] = limit,
                    ["kernel"] = KernelPayload(),
                };
                return JsonOutput.Serialize(result);
            }
            default:
                return ErrorJson("scope 只能是 tree / node / benchmark / changes");
        }
    }

    public static string Check(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var file = StringArg(args, "file");
        if (!string.IsNullOrWhiteSpace(file))
        {
            var path = Path.IsPathRooted(file) ? file : Path.Combine(repository.Root, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                return ErrorJson("文件不存在：" + file, new Dictionary<string, object?> { ["file"] = file });
            }
            try
            {
                YamlLoader.LoadRawDocument(path);
            }
            catch (PTreeException exception)
            {
                return JsonOutput.Serialize(new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["file"] = file,
                    ["dry_run"] = true,
                    ["errors"] = new[] { new { code = "R02", node = (string?)null, message = exception.Message } },
                    ["warnings"] = Array.Empty<object>(),
                    ["notes"] = Array.Empty<object>(),
                    ["kernel"] = KernelPayload(),
                });
            }
            return JsonOutput.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["file"] = file,
                ["dry_run"] = true,
                ["note"] = "C# 内核的提案语义校验尚未迁移，这里只确认文件存在且 YAML 可解析；完整 dry-run 请用 Python 对照实现。",
                ["errors"] = Array.Empty<object>(),
                ["warnings"] = Array.Empty<object>(),
                ["notes"] = Array.Empty<object>(),
                ["kernel"] = KernelPayload(),
            });
        }
        var validation = Validator.Validate(repository);
        var result = new Dictionary<string, object?>
        {
            ["ok"] = validation.Ok,
            ["dry_run"] = false,
            ["errors"] = validation.Errors,
            ["warnings"] = validation.Warnings,
            ["notes"] = validation.Notes,
            ["kernel"] = KernelPayload(),
        };
        return JsonOutput.Serialize(result);
    }

    public static string Propose(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        var proposal = StringArg(args, "proposal");
        if (string.IsNullOrWhiteSpace(proposal))
        {
            return ErrorJson("proposal 不能为空，请提供完整变更提案 YAML");
        }
        var filename = SanitizeFilename(StringArg(args, "filename"));
        var directory = Path.Combine(new[] { repository.Root }.Concat(ChangeDir).ToArray());
        Directory.CreateDirectory(directory);
        var dest = Path.Combine(directory, filename);
        var relative = Path.GetRelativePath(repository.Root, dest).Replace('\\', '/');
        if (!IsInside(dest, directory))
        {
            return ErrorJson("提案必须落在 00-程序树/变更/ 下");
        }
        File.WriteAllText(dest, proposal, new System.Text.UTF8Encoding(false));
        var evaluation = ProposalEngine.EvaluateFile(repository, dest);
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = evaluation.Ok,
            ["file"] = relative,
            ["dry_run"] = true,
            ["errors"] = evaluation.Errors,
            ["warnings"] = evaluation.Warnings,
            ["notes"] = evaluation.Notes,
            ["summary"] = evaluation.Summary,
            ["added"] = evaluation.Added,
            ["removed"] = evaluation.Removed,
            ["deprecated"] = evaluation.Deprecated,
            ["kernel"] = KernelPayload(),
        });
    }

    public static string Apply(string? root, JsonElement? args)
    {
        var repository = PTreeRepository.Load(root);
        if (!(BoolArg(args, "confirm") ?? false))
        {
            return ErrorJson("拒绝：落地会改清单和台账。必须由用户明确批准后，用 confirm=true 再调用。");
        }
        var file = StringArg(args, "file");
        if (string.IsNullOrWhiteSpace(file))
        {
            return ErrorJson("必须提供 file（提案路径，位于 00-程序树/变更/ 下）");
        }
        var directory = Path.Combine(new[] { repository.Root }.Concat(ChangeDir).ToArray());
        var full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(repository.Root, file));
        if (!IsInside(full, directory))
        {
            return ErrorJson("只能落地 00-程序树/变更/ 下的提案");
        }
        if (!(full.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
        {
            return ErrorJson("提案必须是 .yaml / .yml 文件");
        }
        if (!File.Exists(full))
        {
            return ErrorJson("提案文件不存在：" + file);
        }
        var result = ProposalEngine.Apply(repository, new[] { full }, true, false,
            reloaded => { GeneratedProducts.WriteAll(reloaded); return "ok"; });
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = result.Ok,
            ["file"] = Path.GetRelativePath(repository.Root, full).Replace('\\', '/'),
            ["written"] = result.Written,
            ["ledger_id"] = result.LedgerId,
            ["written_files"] = result.WrittenFiles,
            ["errors"] = result.Errors,
            ["warnings"] = result.Warnings,
            ["notes"] = result.Notes,
            ["summary"] = result.Summary,
            ["error"] = result.Error,
            ["kernel"] = KernelPayload(),
        });
    }

    private static Dictionary<string, object?> WithKernel(object payload)
    {
        var map = JsonSerializer.SerializeToElement(payload, JsonOutput.Options).EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value);
        map["kernel"] = KernelPayload();
        return map;
    }

    private static object BuildBenchmarkPayload(PTreeRepository repository)
    {
        var benchmarkMap = repository.Benchmarks.ById;
        var benchmarks = repository.Benchmarks.Benchmarks.Select(benchmark => new Dictionary<string, object?>
        {
            ["id"] = benchmark.Id,
            ["name"] = benchmark.Name,
            ["kind"] = benchmark.Kind,
            ["reference"] = benchmark.Reference,
            ["levels"] = benchmark.Levels.Select(level => new Dictionary<string, object?>
            {
                ["level"] = level.Level,
                ["name"] = level.Name,
                ["criteria"] = level.Criteria,
                ["evidence"] = level.Evidence,
            }).ToList(),
        }).ToList();
        var bindings = repository.Bindings.Bindings.Select(binding =>
        {
            benchmarkMap.TryGetValue(binding.Benchmark ?? string.Empty, out var benchmark);
            return new Dictionary<string, object?>
            {
                ["node"] = binding.Node,
                ["benchmark"] = binding.Benchmark,
                ["level"] = binding.Level,
                ["target"] = binding.Target,
                ["gaps"] = binding.Gaps.Select(gap => new Dictionary<string, object?>
                {
                    ["dimension"] = gap.Dimension,
                    ["gap"] = gap.Gap,
                    ["next"] = gap.Next,
                    ["opened"] = gap.Opened,
                }).ToList(),
                ["next"] = binding.Gaps.FirstOrDefault()?.Next,
                ["benchmark_name"] = benchmark?.Name,
            };
        }).ToList();
        return new Dictionary<string, object?>
        {
            ["tree_version"] = repository.Manifest.TreeVersion,
            ["generated_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["benchmarks"] = benchmarks,
            ["bindings"] = bindings,
            ["benchmark_count"] = benchmarks.Count,
            ["binding_count"] = bindings.Count,
            ["kernel"] = KernelPayload(),
        };
    }

    private static bool HasNode(JsonElement entry, string nodeId)
    {
        return entry.ValueKind == JsonValueKind.Object &&
               entry.TryGetProperty("nodes", out var nodes) &&
               nodes.ValueKind == JsonValueKind.Object &&
               nodes.TryGetProperty(nodeId, out _);
    }

    private static Dictionary<string, object?> KernelPayload() => new()
    {
        ["name"] = KernelInfo.Name,
        ["version"] = KernelInfo.Version,
    };

    public static string ErrorJson(string message, Dictionary<string, object?>? extra = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = message,
            ["kernel"] = KernelPayload(),
        };
        if (extra is not null)
        {
            foreach (var pair in extra) payload[pair.Key] = pair.Value;
        }
        return JsonOutput.Serialize(payload);
    }

    private static string? StringArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property))
        {
            return null;
        }
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int? IntArg(JsonElement? args, string name)
    {
        var text = StringArg(args, name);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static bool? BoolArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property))
        {
            return null;
        }
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static string SanitizeFilename(string? raw)
    {
        var value = string.IsNullOrWhiteSpace(raw) ? "proposal-" + DateTimeOffset.Now.ToUnixTimeMilliseconds() + ".yaml" : raw.Trim();
        foreach (var invalid in "\\/:*?\"<>|") value = value.Replace(invalid, '-');
        value = string.Join('-', value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        return value.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            ? value
            : value + ".yaml";
    }

    private static bool IsInside(string child, string parent)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
