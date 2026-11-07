using System.Globalization;

namespace PTree.Core;

/// <summary>
/// 程序树内核：加载清单 / 契约 / 标杆 / 绑定 / 节点说明，并构建 index / node 输出。
/// 该库不依赖工作目录；所有路径相对仓库根解析。
/// </summary>
public sealed class PTreeRepository
{
    public const string ManifestDir = "00-程序树";
    public const string ManifestRel = ManifestDir + "/program-tree.yaml";
    public const string ContractsRel = ManifestDir + "/契约/contracts.yaml";
    public const string BenchmarksRel = ManifestDir + "/标杆/benchmarks.yaml";
    public const string BindingsRel = ManifestDir + "/标杆/绑定.yaml";
    public const string IntroDirRel = ManifestDir + "/说明";
    public const string EvaluationDirRel = ManifestDir + "/评价";
    public const string IntroExtension = ".txt";

    public string Root { get; }
    public Manifest Manifest { get; }
    public RawMap ManifestRaw { get; }
    public string ContractsPath { get; }
    public object? ContractsRaw { get; }
    public ContractsDocument Contracts { get; }
    public BenchmarksDocument Benchmarks { get; }
    public BindingsDocument Bindings { get; }
    public object? BenchmarksRaw { get; }
    public object? BindingsRaw { get; }
    public string BenchmarksPath { get; }
    public string BindingsPath { get; }

    private readonly Dictionary<string, NodeModel> _nodeMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupModel> _groupMap = new(StringComparer.Ordinal);
    private readonly List<string> _nodeOrder = new();

    public IReadOnlyList<NodeModel> Nodes => Manifest.Nodes;
    public IReadOnlyDictionary<string, NodeModel> NodeMap => _nodeMap;
    public IReadOnlyList<GroupModel> Groups => Manifest.Groups;

    private PTreeRepository(string root, Manifest manifest, RawMap manifestRaw, string contractsPath,
        object? contractsRaw, ContractsDocument contracts,
        BenchmarksDocument benchmarks, BindingsDocument bindings,
        object? benchmarksRaw, object? bindingsRaw, string benchmarksPath, string bindingsPath)
    {
        BenchmarksRaw = benchmarksRaw;
        BindingsRaw = bindingsRaw;
        BenchmarksPath = benchmarksPath;
        BindingsPath = bindingsPath;
        Root = root;
        Manifest = manifest;
        ManifestRaw = manifestRaw;
        ContractsPath = contractsPath;
        ContractsRaw = contractsRaw;
        Contracts = contracts;
        Benchmarks = benchmarks;
        Bindings = bindings;
        foreach (var node in manifest.Nodes)
        {
            if (node.Id is not { Length: > 0 } id)
            {
                continue;
            }
            if (_nodeMap.TryAdd(id, node))
            {
                _nodeOrder.Add(id);
            }
        }
        LoadGroups();
    }

    private void LoadGroups()
    {
        if (!ManifestRaw.TryGetValue("groups", out var rawGroups) || rawGroups is not List<object?> list)
        {
            return;
        }
        foreach (var item in list)
        {
            if (item is not RawMap map)
            {
                continue;
            }
            var group = new GroupModel
            {
                Id = map.TryGetValue("id", out var id) ? id as string : null,
                Name = map.TryGetValue("name", out var name) ? name as string : null,
                Description = map.TryGetValue("description", out var description) ? description as string : null,
                Parent = map.TryGetValue("parent", out var parent) ? parent as string : null,
            };
            if (map.TryGetValue("members", out var members) && members is List<object?> memberList)
            {
                foreach (var member in memberList)
                {
                    if (member is string text)
                    {
                        group.Members.Add(text);
                    }
                }
            }
            Manifest.Groups.Add(group);
            if (group.Id is { Length: > 0 } groupId)
            {
                _groupMap.TryAdd(groupId, group);
            }
        }
    }

    public IReadOnlyList<string> GroupsForNode(string nodeId)
    {
        var result = new List<string>();
        foreach (var group in Manifest.Groups)
        {
            if (group.Id is { Length: > 0 } id && GroupContains(group, nodeId, new HashSet<string>(StringComparer.Ordinal)))
            {
                result.Add(id);
            }
        }
        return result;
    }

    private bool GroupContains(GroupModel group, string nodeId, HashSet<string> visiting)
    {
        if (group.Id is not { Length: > 0 } groupId || !visiting.Add(groupId))
        {
            return false;
        }
        foreach (var member in group.Members)
        {
            if (string.Equals(member, nodeId, StringComparison.Ordinal))
            {
                return true;
            }
            if (_groupMap.TryGetValue(member, out var child) && GroupContains(child, nodeId, visiting))
            {
                return true;
            }
        }
        visiting.Remove(groupId);
        return false;
    }

    /// <summary>从内存原始清单构造候选仓库；文件路径仍指向真实根，供校验规则读取代码布局。</summary>
    public static PTreeRepository FromRaw(string root, RawMap manifestRaw, object? contractsRaw,
        object? benchmarksRaw, object? bindingsRaw, string? contractsPath = null,
        string? benchmarksPath = null, string? bindingsPath = null)
    {
        var manifest = new Manifest
        {
            SchemaVersion = manifestRaw.TryGetValue("schema_version", out var schema) && schema is long schemaValue ? (int)schemaValue : 0,
            TreeVersion = manifestRaw.TryGetValue("tree_version", out var version) ? version as string : null,
            Updated = manifestRaw.TryGetValue("updated", out var updated) ? updated as string : null,
            Root = manifestRaw.TryGetValue("root", out var rootId) ? rootId as string : null,
        };
        if (manifestRaw.TryGetValue("nodes", out var nodeValue) && nodeValue is List<object?> nodeList)
        {
            foreach (var item in nodeList.OfType<RawMap>())
            {
                manifest.Nodes.Add(new NodeModel
                {
                    Id = item.TryGetValue("id", out var id) ? id as string : null,
                    Name = item.TryGetValue("name", out var name) ? name as string : null,
                    Kind = item.TryGetValue("kind", out var kind) ? kind as string : null,
                    Status = item.TryGetValue("status", out var status) ? StatusValues.Normalize(status as string) : null,
                    Phase = item.TryGetValue("phase", out var phase) ? phase as string : null,
                    Parent = item.TryGetValue("parent", out var parent) ? parent as string : null,
                    Home = item.TryGetValue("home", out var home) ? home as string : null,
                    Evaluation = item.TryGetValue("evaluation", out var evaluation) ? evaluation as string : null,
                    Preset = item.TryGetValue("preset", out var preset) && preset is true,
                    PresetOutputsNone = string.Equals(item.TryGetValue("preset_outputs", out var presetOutputs) ? presetOutputs as string : null, "none", StringComparison.Ordinal),
                    CreatedAt = item.TryGetValue("created_at", out var created) ? created as string : null,
                    UpdatedAt = item.TryGetValue("updated_at", out var updatedAt) ? updatedAt as string : null,
                });
                var model = manifest.Nodes[^1];
                if (item.TryGetValue("files", out var files) && files is List<object?> fileList)
                    model.Files.AddRange(fileList.OfType<string>());
                if (item.TryGetValue("inputs", out var inputs) && inputs is List<object?> inputList)
                    model.Inputs.AddRange(inputList.OfType<string>());
                if (item.TryGetValue("outputs", out var outputs) && outputs is List<object?> outputList)
                    model.Outputs.AddRange(outputList.OfType<string>());
                if (item.TryGetValue("depends_on", out var depends) && depends is List<object?> dependList)
                    model.DependsOn.AddRange(dependList.OfType<string>());
                if (item.TryGetValue("acceptance", out var acceptance) && acceptance is List<object?> acceptanceList)
                    model.Acceptance.AddRange(acceptanceList.OfType<string>());
            }
        }
        var cPath = contractsPath ?? Path.Combine(root, ContractsRel.Replace('/', Path.DirectorySeparatorChar));
        var bPath = benchmarksPath ?? Path.Combine(root, BenchmarksRel.Replace('/', Path.DirectorySeparatorChar));
        var bindPath = bindingsPath ?? Path.Combine(root, BindingsRel.Replace('/', Path.DirectorySeparatorChar));
        return new PTreeRepository(root, manifest, manifestRaw, cPath, contractsRaw,
            new ContractsDocument(), new BenchmarksDocument(), new BindingsDocument(),
            benchmarksRaw, bindingsRaw, bPath, bindPath);
    }

    public static PTreeRepository Load(string? root = null)
    {
        var resolvedRoot = ResolveRoot(root);
        var manifestPath = Path.Combine(resolvedRoot, ManifestRel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(manifestPath))
        {
            throw new PTreeException("找不到程序树清单：" + manifestPath);
        }
        var (manifest, manifestRaw) = YamlLoader.LoadManifestDocument(manifestPath);

        var contractsPath = ResolveContractsPath(resolvedRoot, manifestRaw);
        object? contractsRaw = File.Exists(contractsPath) ? YamlLoader.LoadRawDocument(contractsPath) : null;
        var contracts = contractsRaw is RawMap ? YamlLoader.LoadContracts(contractsPath) : new ContractsDocument();

        var benchmarksPath = Path.Combine(resolvedRoot, RelToSystem(BenchmarksRel));
        var benchmarksRaw = File.Exists(benchmarksPath) ? YamlLoader.LoadRawDocument(benchmarksPath) : null;
        var benchmarks = benchmarksRaw is not null
            ? YamlLoader.LoadBenchmarks(benchmarksPath)
            : new BenchmarksDocument();

        var bindingsPath = Path.Combine(resolvedRoot, RelToSystem(BindingsRel));
        var bindingsRaw = File.Exists(bindingsPath) ? YamlLoader.LoadRawDocument(bindingsPath) : null;
        var bindings = bindingsRaw is not null
            ? YamlLoader.LoadBindings(bindingsPath)
            : new BindingsDocument();

        return new PTreeRepository(resolvedRoot, manifest, manifestRaw, contractsPath, contractsRaw, contracts,
            benchmarks, bindings, benchmarksRaw, bindingsRaw, benchmarksPath, bindingsPath);
    }

    public static string ResolveRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var full = Path.GetFullPath(explicitRoot.Trim());
            if (!Directory.Exists(full))
            {
                throw new PTreeException("--root 不是目录：" + explicitRoot);
            }
            return full;
        }
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = Path.GetFullPath(start);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, RelToSystem(ManifestRel))))
                {
                    return current;
                }
                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }
                current = parent.FullName;
            }
        }
        throw new PTreeException("找不到" + ManifestRel + "；请用 --root 指定仓库根");
    }

    public IndexPayload BuildIndex()
    {
        var payload = new IndexPayload
        {
            TreeVersion = Manifest.TreeVersion,
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        foreach (var node in Manifest.Nodes)
        {
            if (!string.Equals(node.Kind, "department", StringComparison.Ordinal) || node.Id is not { Length: > 0 } departmentId)
            {
                continue;
            }
            var department = new DepartmentIndexPayload
            {
                Name = node.Name,
                Status = node.Status,
                Phase = node.Phase,
            };
            foreach (var other in Manifest.Nodes)
            {
                if (!string.Equals(other.Parent, departmentId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!string.Equals(other.Kind, "program", StringComparison.Ordinal))
                {
                    continue;
                }
                department.Programs.Add(new ProgramIndexPayload
                {
                    Id = other.Id,
                    Name = other.Name,
                    Status = other.Status,
                    Phase = other.Phase,
                    Purpose = other.Responsibility,
                });
                if (other.Id is { Length: > 0 } programId)
                {
                    department.Programs[^1].Groups.AddRange(GroupsForNode(programId));
                }
            }
            payload.Departments[departmentId] = department;
        }
        foreach (var group in Manifest.Groups)
        {
            var groupPayload = new GroupPayload
            {
                Id = group.Id,
                Name = group.Name,
                Description = group.Description,
                Parent = group.Parent,
            };
            groupPayload.Members.AddRange(group.Members);
            payload.Groups.Add(groupPayload);
        }
        return payload;
    }

    public NodePayload BuildNode(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_nodeMap.TryGetValue(id, out var node))
        {
            throw new PTreeException("节点不存在：" + id);
        }
        var introText = ReadIntroText(id);
        var payload = new NodePayload
        {
            Id = node.Id,
            Name = node.Name,
            Kind = node.Kind,
            Layer = node.Layer,
            Status = node.Status,
            Phase = node.Phase,
            Parent = node.Parent,
            Purpose = node.Responsibility,
            Intro = new IntroPayload { Path = IntroRel(id), Text = introText },
            Code = BuildCode(node, introText),
            Connections = BuildConnections(node, id),
            Spec = node.Spec,
            CreatedAt = node.CreatedAt,
            UpdatedAt = node.UpdatedAt,
        };
        payload.Groups.AddRange(GroupsForNode(id));
        try
        {
            // 策划对齐也进 node 载荷：卡片只放 3 条，读全量要靠这里
            payload.RelatedPlan.AddRange(NodeTools.RelatedPlanning(this, id, TreeStore.Load(Root)));
        }
        catch (PTreeException)
        {
            // 策划树缺失时不影响节点载荷
        }
        foreach (var other in Manifest.Nodes)
        {
            if (string.Equals(other.Parent, id, StringComparison.Ordinal) && other.Id is { Length: > 0 } childId)
            {
                payload.Children.Add(childId);
            }
        }
        foreach (var evidence in node.Evidence)
        {
            payload.Evidence.Add(new EvidencePayload
            {
                Test = evidence.Test,
                At = evidence.At,
                Result = evidence.Result,
                Note = evidence.Note,
            });
        }
        payload.NotResponsible.AddRange(node.NotResponsible);
        payload.Acceptance.AddRange(node.Acceptance);
        foreach (var attachment in node.Attachments)
        {
            payload.Attachments.Add(new AttachmentPayload { Path = attachment.Path, Kind = attachment.Kind });
        }
        var details = BuildBenchmarkDetails(id);
        payload.Benchmark = details.Count > 0 ? details[0] : null;
        if (details.Count > 1)
        {
            payload.Benchmarks = details;
        }
        return payload;
    }

    public string IntroRel(string id) => IntroDirRel + "/" + id + IntroExtension;

    public string EvaluationRel(string id) => EvaluationDirRel + "/" + id + ".txt";

    public string EvaluationPath(string id) => Path.Combine(Root, RelToSystem(EvaluationDirRel), id + ".txt");

    public string IntroPath(string id) => Path.Combine(Root, RelToSystem(IntroDirRel), id + IntroExtension);

    private string? ReadIntroText(string id)
    {
        var path = IntroPath(id);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var text = File.ReadAllText(path);
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }
        catch (IOException)
        {
            return null;
        }
    }

    private CodePayload BuildCode(NodeModel node, string? introText)
    {
        var payload = new CodePayload { Home = node.Home };
        payload.Files.AddRange(node.Files);
        var readBeforeEdit = new List<string>();
        void Add(string? item)
        {
            if (!string.IsNullOrEmpty(item) && !readBeforeEdit.Contains(item, StringComparer.Ordinal))
            {
                readBeforeEdit.Add(item);
            }
        }

        if (introText is not null)
        {
            Add(IntroRel(node.Id!));
        }
        if (!string.IsNullOrWhiteSpace(node.Spec))
        {
            Add(node.Spec.Split('#', 2)[0].Trim());
        }
        if (!string.IsNullOrWhiteSpace(node.Home))
        {
            var home = node.Home.Replace('\\', '/').Trim().TrimEnd('/');
            Add(home + "/SPEC.md");
            Add(home + "/program.yaml");
        }
        foreach (var file in node.Files)
        {
            Add(file);
        }
        if (node.Inputs.Count > 0 || node.Outputs.Count > 0)
        {
            Add(ContractsRel);
        }
        payload.ReadBeforeEdit.AddRange(readBeforeEdit);
        return payload;
    }

    private ConnectionsPayload BuildConnections(NodeModel node, string id)
    {
        var payload = new ConnectionsPayload();
        foreach (var contractId in node.Inputs)
        {
            Contracts.ById.TryGetValue(contractId, out var contract);
            var upstream = new ContractConnectionPayload
            {
                Contract = contractId,
                Purpose = contract?.Purpose,
                Version = contract?.Version,
                From = new List<NodeStatusPayload>(),
            };
            if (contract is not null)
            {
                foreach (var producer in contract.Producers)
                {
                    if (_nodeMap.TryGetValue(producer, out var producerNode))
                    {
                        upstream.From.Add(new NodeStatusPayload { Id = producer, Status = producerNode.Status });
                    }
                }
                upstream.Fields.AddRange(contract.FieldNames);
            }
            payload.Upstream.Add(upstream);
        }
        foreach (var contractId in node.Outputs)
        {
            Contracts.ById.TryGetValue(contractId, out var contract);
            var downstream = new ContractConnectionPayload
            {
                Contract = contractId,
                Purpose = contract?.Purpose,
                Version = contract?.Version,
                To = new List<NodeStatusPayload>(),
            };
            if (contract is not null)
            {
                foreach (var consumer in contract.Consumers)
                {
                    if (_nodeMap.TryGetValue(consumer, out var consumerNode))
                    {
                        downstream.To.Add(new NodeStatusPayload { Id = consumer, Status = consumerNode.Status });
                    }
                }
                downstream.Fields.AddRange(contract.FieldNames);
            }
            payload.Downstream.Add(downstream);
        }
        foreach (var dependency in node.DependsOn)
        {
            payload.DependsOn.Add(new DependencyPayload
            {
                Id = dependency,
                Status = _nodeMap.TryGetValue(dependency, out var dependencyNode) ? dependencyNode.Status : null,
            });
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddImpact(string? target, string via, string direction)
        {
            if (string.IsNullOrEmpty(target) || string.Equals(target, id, StringComparison.Ordinal))
            {
                return;
            }
            var key = target + "\u0000" + via + "\u0000" + direction;
            if (!seen.Add(key))
            {
                return;
            }
            payload.Impact.Add(new ImpactPayload { Node = target, Via = via, Direction = direction });
        }

        foreach (var entry in payload.Downstream)
        {
            if (entry.Contract is null || !Contracts.ById.TryGetValue(entry.Contract, out var contract))
            {
                continue;
            }
            foreach (var consumer in contract.Consumers)
            {
                if (_nodeMap.ContainsKey(consumer))
                {
                    AddImpact(consumer, "契约 " + entry.Contract, "downstream");
                }
            }
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in Manifest.Nodes)
        {
            if (other.Id is not { Length: > 0 } otherId || !visited.Add(otherId))
            {
                continue;
            }
            if (other.DependsOn.Contains(id, StringComparer.Ordinal))
            {
                AddImpact(otherId, "depends_on", "downstream");
            }
            if (string.Equals(other.Parent, id, StringComparison.Ordinal))
            {
                AddImpact(otherId, "parent", "downstream");
            }
        }
        foreach (var entry in payload.Upstream)
        {
            if (entry.Contract is null || !Contracts.ById.TryGetValue(entry.Contract, out var contract))
            {
                continue;
            }
            foreach (var producer in contract.Producers)
            {
                if (_nodeMap.ContainsKey(producer))
                {
                    AddImpact(producer, "契约 " + entry.Contract, "upstream");
                }
            }
        }
        return payload;
    }

    private List<BenchmarkDetailPayload> BuildBenchmarkDetails(string id)
    {
        var details = new List<BenchmarkDetailPayload>();
        foreach (var binding in Bindings.Bindings)
        {
            if (!string.Equals(binding.Node, id, StringComparison.Ordinal))
            {
                continue;
            }
            BenchmarkModel? benchmark = null;
            if (binding.Benchmark is { } benchmarkId)
            {
                Benchmarks.ById.TryGetValue(benchmarkId, out benchmark);
            }
            var detail = new BenchmarkDetailPayload
            {
                Id = binding.Benchmark,
                Name = benchmark?.Name,
                Kind = benchmark?.Kind,
                Reference = benchmark?.Reference,
                Level = binding.Level,
                Target = binding.Target,
                Next = binding.Gaps.FirstOrDefault(gap => !string.IsNullOrWhiteSpace(gap.Next))?.Next,
            };
            foreach (var gap in binding.Gaps)
            {
                detail.Gaps.Add(new GapPayload
                {
                    Dimension = gap.Dimension,
                    Gap = gap.Gap,
                    Next = gap.Next,
                    Opened = gap.Opened,
                });
            }
            if (benchmark is not null)
            {
                foreach (var level in benchmark.Levels)
                {
                    detail.Levels.Add(new BenchmarkLevelPayload
                    {
                        Level = level.Level,
                        Name = level.Name,
                        Criteria = level.Criteria,
                        Evidence = level.Evidence,
                    });
                }
            }
            details.Add(detail);
        }
        return details;
    }

    private static string ResolveContractsPath(string root, RawMap manifestRaw)
    {
        var relative = ContractsRel;
        if (manifestRaw.TryGetValue("conventions", out var conventions) && conventions is RawMap conventionsMap &&
            conventionsMap.TryGetValue("contracts_file", out var value) && value is string configured)
        {
            relative = configured.Trim();
        }
        if (IsPythonAbsolutePath(relative))
        {
            return relative;
        }
        var direct = Path.Combine(root, RelToSystem(relative));
        if (File.Exists(direct))
        {
            return direct;
        }
        var underManifest = Path.Combine(root, RelToSystem(ManifestDir), RelToSystem(relative));
        return File.Exists(underManifest) ? underManifest : direct;
    }

    /// <summary>Windows pathlib.Path.is_absolute：必须有盘符（或 UNC），仅以 / 开头不是绝对路径。</summary>
    private static bool IsPythonAbsolutePath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }
        return path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
    }

    private static string RelToSystem(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);
}
