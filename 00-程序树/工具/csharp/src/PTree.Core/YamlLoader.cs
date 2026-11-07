using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PTree.Core;

/// <summary>PTree 的 YAML 读取层：只负责把 YAML 映射成模型，不承载业务规则。</summary>
public static class YamlLoader
{
    public static Manifest LoadManifest(string path) => LoadManifestDocument(path).Manifest;

    /// <summary>按原始 YAML 值树读取（允许根为映射或序列，空文档返回 null）。</summary>
    public static object? LoadRawDocument(string path)
    {
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return RawYaml.Parse(text);
        }
        catch (YamlException exc)
        {
            throw new PTreeException("YAML 解析失败 " + path + "：" + exc.Message, exc);
        }
        catch (IOException exc)
        {
            throw new PTreeException("无法读取文件 " + path + "：" + exc.Message, exc);
        }
    }

    public static (Manifest Manifest, RawMap Raw) LoadManifestDocument(string path)
    {
        var root = LoadRoot(path);
        return (ParseManifest(root), RawYaml.ToMap(root));
    }

    private static Manifest ParseManifest(YamlMappingNode root)
    {
        var manifest = new Manifest
        {
            SchemaVersion = Y.Int(root, "schema_version") ?? 0,
            TreeVersion = Y.Str(Y.Get(root, "tree_version")),
            Updated = Y.Str(Y.Get(root, "updated")),
            Root = Y.Str(Y.Get(root, "root")),
        };
        if (Y.Map(Y.Get(root, "conventions")) is { } conventions)
        {
            manifest.Conventions = new Conventions
            {
                ProgramRoot = Y.Str(Y.Get(conventions, "program_root")),
                SpecFilename = Y.Str(Y.Get(conventions, "spec_filename")),
                ManifestFilename = Y.Str(Y.Get(conventions, "manifest_filename")),
                ContractsFile = Y.Str(Y.Get(conventions, "contracts_file")),
            };
        }
        foreach (var phaseNode in Y.Seq(Y.Get(root, "phases")))
        {
            if (Y.Map(phaseNode) is not { } phaseMap)
            {
                continue;
            }
            var phase = new PhaseModel
            {
                Id = Y.Str(Y.Get(phaseMap, "id")),
                Name = Y.Str(Y.Get(phaseMap, "name")),
                Goal = Y.Str(Y.Get(phaseMap, "goal")),
            };
            phase.ExitCriteria.AddRange(Y.StrList(Y.Get(phaseMap, "exit_criteria")));
            manifest.Phases.Add(phase);
        }
        foreach (var nodeNode in Y.Seq(Y.Get(root, "nodes")))
        {
            if (Y.Map(nodeNode) is { } nodeMap)
            {
                manifest.Nodes.Add(ParseNode(nodeMap));
            }
        }
        return manifest;
    }

    public static ContractsDocument LoadContracts(string path)
    {
        var root = LoadRoot(path);
        var document = new ContractsDocument
        {
            SchemaVersion = Y.Int(root, "schema_version") ?? 0,
            ContractsVersion = Y.Str(Y.Get(root, "contracts_version")),
        };
        foreach (var contractNode in Y.Seq(Y.Get(root, "contracts")))
        {
            if (Y.Map(contractNode) is not { } map)
            {
                continue;
            }
            var contract = new ContractModel
            {
                Id = Y.Str(Y.Get(map, "id")),
                Name = Y.Str(Y.Get(map, "name")),
                Version = Y.Str(Y.Get(map, "version")),
                Purpose = Y.Str(Y.Get(map, "purpose")),
                External = Y.Bool(Y.Get(map, "external")),
                ExternalConsumers = Y.Bool(Y.Get(map, "external_consumers")),
            };
            contract.Producers.AddRange(Y.StrList(Y.Get(map, "producers")));
            contract.Consumers.AddRange(Y.StrList(Y.Get(map, "consumers")));
            contract.FieldNames.AddRange(ReadFieldNames(map));
            if (contract.Id is { Length: > 0 } id)
            {
                document.ById.TryAdd(id, contract);
            }
            document.Contracts.Add(contract);
        }
        return document;
    }

    public static BenchmarksDocument LoadBenchmarks(string path)
    {
        var root = LoadRoot(path);
        var document = new BenchmarksDocument
        {
            SchemaVersion = Y.Int(root, "schema_version") ?? 0,
        };
        foreach (var benchmarkNode in Y.Seq(Y.Get(root, "benchmarks")))
        {
            if (Y.Map(benchmarkNode) is not { } map)
            {
                continue;
            }
            var benchmark = new BenchmarkModel
            {
                Id = Y.Str(Y.Get(map, "id")),
                Name = Y.Str(Y.Get(map, "name")),
                Kind = Y.Str(Y.Get(map, "kind")),
                Reference = Y.Str(Y.Get(map, "reference")),
            };
            foreach (var levelNode in Y.Seq(Y.Get(map, "levels")))
            {
                if (Y.Map(levelNode) is not { } levelMap)
                {
                    continue;
                }
                benchmark.Levels.Add(new BenchmarkLevelModel
                {
                    Level = Y.Int(levelMap, "level"),
                    Name = Y.Str(Y.Get(levelMap, "name")),
                    Criteria = Y.Str(Y.Get(levelMap, "criteria")),
                    Evidence = Y.Str(Y.Get(levelMap, "evidence")),
                });
            }
            if (benchmark.Id is { Length: > 0 } id)
            {
                document.ById.TryAdd(id, benchmark);
            }
            document.Benchmarks.Add(benchmark);
        }
        return document;
    }

    public static BindingsDocument LoadBindings(string path)
    {
        var root = LoadRoot(path);
        var document = new BindingsDocument
        {
            SchemaVersion = Y.Int(root, "schema_version") ?? 0,
        };
        foreach (var bindingNode in Y.Seq(Y.Get(root, "bindings")))
        {
            if (Y.Map(bindingNode) is not { } map)
            {
                continue;
            }
            var binding = new BindingModel
            {
                Node = Y.Str(Y.Get(map, "node")),
                Benchmark = Y.Str(Y.Get(map, "benchmark")),
                Level = Y.Int(map, "level"),
                Target = Y.Int(map, "target"),
            };
            foreach (var gapNode in Y.Seq(Y.Get(map, "gaps")))
            {
                if (Y.Map(gapNode) is not { } gapMap)
                {
                    continue;
                }
                binding.Gaps.Add(new GapModel
                {
                    Dimension = Y.Str(Y.Get(gapMap, "dimension")),
                    Gap = Y.Str(Y.Get(gapMap, "gap")),
                    Next = Y.Str(Y.Get(gapMap, "next")),
                    Opened = Y.Str(Y.Get(gapMap, "opened")),
                });
            }
            foreach (var historyNode in Y.Seq(Y.Get(map, "history")))
            {
                if (Y.Map(historyNode) is not { } historyMap)
                {
                    continue;
                }
                binding.History.Add(new BindingHistoryModel
                {
                    Date = Y.Str(Y.Get(historyMap, "date")),
                    From = Y.Int(historyMap, "from"),
                    To = Y.Int(historyMap, "to"),
                    Proposal = Y.Str(Y.Get(historyMap, "proposal")),
                });
            }
            document.Bindings.Add(binding);
        }
        return document;
    }

    private static NodeModel ParseNode(YamlMappingNode map)
    {
        var node = new NodeModel
        {
            Id = Y.Str(Y.Get(map, "id")),
            Name = Y.Str(Y.Get(map, "name")),
            Kind = Y.Str(Y.Get(map, "kind")),
            Layer = Y.Int(map, "layer"),
            Status = StatusValues.Normalize(Y.Str(Y.Get(map, "status"))),
            Phase = Y.Str(Y.Get(map, "phase")),
            Parent = Y.Str(Y.Get(map, "parent")),
            Responsibility = Y.Str(Y.Get(map, "responsibility")),
            Home = Y.Str(Y.Get(map, "home")),
            Spec = Y.Str(Y.Get(map, "spec")),
            Owner = Y.Str(Y.Get(map, "owner")),
            Platform = Y.Str(Y.Get(map, "platform")),
            Notes = Y.Str(Y.Get(map, "notes")),
            CreatedAt = Y.Str(Y.Get(map, "created_at")),
            UpdatedAt = Y.Str(Y.Get(map, "updated_at")),
            PlanningExemption = Y.Str(Y.Get(map, "planning_exemption")),
            Evaluation = Y.Str(Y.Get(map, "evaluation")),
            Preset = Y.Bool(Y.Get(map, "preset")) == true,
            PresetOutputsNone = string.Equals(Y.Str(Y.Get(map, "preset_outputs")), "none", StringComparison.Ordinal),
        };
        node.NotResponsible.AddRange(Y.StrList(Y.Get(map, "not_responsible")));
        node.Inputs.AddRange(Y.StrList(Y.Get(map, "inputs")));
        node.Outputs.AddRange(Y.StrList(Y.Get(map, "outputs")));
        node.DependsOn.AddRange(Y.StrList(Y.Get(map, "depends_on")));
        node.Files.AddRange(Y.StrList(Y.Get(map, "files")));
        node.Acceptance.AddRange(Y.StrList(Y.Get(map, "acceptance")));
        foreach (var attachmentNode in Y.Seq(Y.Get(map, "attachments")))
        {
            if (Y.Map(attachmentNode) is not { } attachmentMap)
            {
                continue;
            }
            node.Attachments.Add(new AttachmentModel
            {
                Path = Y.Str(Y.Get(attachmentMap, "path")),
                Kind = Y.Str(Y.Get(attachmentMap, "kind")),
            });
        }
        foreach (var evidenceNode in Y.Seq(Y.Get(map, "evidence")))
        {
            if (Y.Map(evidenceNode) is not { } evidenceMap)
            {
                continue;
            }
            node.Evidence.Add(new EvidenceModel
            {
                Test = Y.Str(Y.Get(evidenceMap, "test")),
                At = Y.Str(Y.Get(evidenceMap, "at")),
                Result = Y.Str(Y.Get(evidenceMap, "result")),
                Note = Y.Str(Y.Get(evidenceMap, "note")),
            });
        }
        return node;
    }

    private static IEnumerable<string> ReadFieldNames(YamlMappingNode map)
    {
        if (Y.Map(Y.Get(map, "fields")) is { } fields)
        {
            return fields.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value ?? string.Empty);
        }
        if (Y.Map(Y.Get(map, "schema")) is { } schema && Y.Map(Y.Get(schema, "fields")) is { } schemaFields)
        {
            return schemaFields.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value ?? string.Empty);
        }
        return Array.Empty<string>();
    }

    private static YamlMappingNode LoadRoot(string path)
    {
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
            {
                throw new PTreeException("YAML 文件为空：" + path);
            }
            return stream.Documents[0].RootNode as YamlMappingNode
                ?? throw new PTreeException("YAML 顶层必须是映射：" + path);
        }
        catch (YamlException exc)
        {
            throw new PTreeException("YAML 解析失败 " + path + "：" + exc.Message, exc);
        }
        catch (IOException exc)
        {
            throw new PTreeException("无法读取文件 " + path + "：" + exc.Message, exc);
        }
    }
}

internal static class Y
{
    public static YamlMappingNode? Map(YamlNode? node) => node as YamlMappingNode;

    public static IEnumerable<YamlNode> Seq(YamlNode? node)
        => node is YamlSequenceNode sequence ? sequence.Children : Enumerable.Empty<YamlNode>();

    public static string? Str(YamlNode? node)
    {
        if (node is not YamlScalarNode scalar || scalar.Value is null)
        {
            return null;
        }
        // YAML 的 null / ~ 在 RepresentationModel 里是普通标量，这里归一化回 null
        if (scalar.Style == ScalarStyle.Plain &&
            (scalar.Value is "null" or "~" or "Null" or "NULL"))
        {
            return null;
        }
        return scalar.Value;
    }

    public static int? Int(YamlMappingNode map, string key) => int.TryParse(Str(Get(map, key)), out var value) ? value : null;

    public static bool Bool(YamlNode? node) => string.Equals(Str(node), "true", StringComparison.OrdinalIgnoreCase);

    public static YamlNode? Get(YamlMappingNode? map, string key)
    {
        if (map is null)
        {
            return null;
        }
        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }
        return null;
    }

    public static List<string> StrList(YamlNode? node)
    {
        var result = new List<string>();
        foreach (var item in Seq(node))
        {
            if (Str(item) is { } value)
            {
                result.Add(value);
            }
        }
        return result;
    }
}
