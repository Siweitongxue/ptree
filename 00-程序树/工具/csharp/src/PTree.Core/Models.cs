using System.Collections.Generic;

namespace PTree.Core;

/// <summary>联系类型白名单：写入口 tree_add_link、校验 R40 与读卡共用这一份，避免两处各写一遍。</summary>
public static class LinkKinds
{
    // 基址：A 是 B 的基址/被继承的脚本（B 继承或复用 A 的实现）；其余为通用联系类型
    public static readonly string[] All = { "引用", "测试", "探针", "覆盖", "数据", "控制", "基址" };

    public static string Written => string.Join(" / ", All);
}

/// <summary>程序树清单（program-tree.yaml）的内存模型。</summary>
public sealed class Manifest
{
    public int SchemaVersion { get; set; }
    public string? TreeVersion { get; set; }
    public string? Updated { get; set; }
    public string? Root { get; set; }
    public Conventions? Conventions { get; set; }
    public List<PhaseModel> Phases { get; } = new();
    public List<NodeModel> Nodes { get; } = new();
    public List<GroupModel> Groups { get; } = new();
}

/// <summary>组：成员只能是节点或组，最深两层。</summary>
public sealed class GroupModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Parent { get; set; }
    public List<string> Members { get; } = new();
}

public sealed class Conventions
{
    public string? ProgramRoot { get; set; }
    public string? SpecFilename { get; set; }
    public string? ManifestFilename { get; set; }
    public string? ContractsFile { get; set; }
}

public sealed class PhaseModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Goal { get; set; }
    public List<string> ExitCriteria { get; } = new();
}

/// <summary>树节点。字段与 program-tree.yaml 的节点同构。</summary>
public sealed class NodeModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public int? Layer { get; set; }
    public string? Status { get; set; }
    public string? Phase { get; set; }
    public string? Parent { get; set; }
    public string? Responsibility { get; set; }
    public List<string> NotResponsible { get; } = new();
    public List<string> Inputs { get; } = new();
    public List<string> Outputs { get; } = new();
    public List<string> DependsOn { get; } = new();
    public List<string> Files { get; } = new();
    public string? Home { get; set; }
    public List<string> Acceptance { get; } = new();
    public List<EvidenceModel> Evidence { get; } = new();
    public string? Spec { get; set; }
    public string? Owner { get; set; }
    public string? Platform { get; set; }
    public string? Notes { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public string? PlanningExemption { get; set; }
    public string? Evaluation { get; set; }
    public bool Preset { get; set; }
    public bool PresetOutputsNone { get; set; }
    public List<AttachmentModel> Attachments { get; } = new();
}

public sealed class AttachmentModel
{
    public string? Path { get; set; }
    public string? Kind { get; set; }
}

public sealed class EvidenceModel
{
    public string? Test { get; set; }
    public string? At { get; set; }
    public string? Result { get; set; }
    public string? Note { get; set; }
}

/// <summary>契约定义（contracts.yaml）。只保留 node/index 输出需要的字段。</summary>
public sealed class ContractModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Purpose { get; set; }
    public bool External { get; set; }
    public bool ExternalConsumers { get; set; }
    public List<string> Producers { get; } = new();
    public List<string> Consumers { get; } = new();
    public List<string> FieldNames { get; } = new();
}

public sealed class ContractsDocument
{
    public int SchemaVersion { get; set; }
    public string? ContractsVersion { get; set; }
    public List<ContractModel> Contracts { get; } = new();
    public Dictionary<string, ContractModel> ById { get; } = new(System.StringComparer.Ordinal);
}

/// <summary>标杆定义（标杆/benchmarks.yaml）。</summary>
public sealed class BenchmarksDocument
{
    public int SchemaVersion { get; set; }
    public List<BenchmarkModel> Benchmarks { get; } = new();
    public Dictionary<string, BenchmarkModel> ById { get; } = new(System.StringComparer.Ordinal);
}

public sealed class BenchmarkModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? Reference { get; set; }
    public List<BenchmarkLevelModel> Levels { get; } = new();
}

public sealed class BenchmarkLevelModel
{
    public int? Level { get; set; }
    public string? Name { get; set; }
    public string? Criteria { get; set; }
    public string? Evidence { get; set; }
}

/// <summary>节点 × 标杆绑定（标杆/绑定.yaml）。</summary>
public sealed class BindingsDocument
{
    public int SchemaVersion { get; set; }
    public List<BindingModel> Bindings { get; } = new();
}

public sealed class BindingModel
{
    public string? Node { get; set; }
    public string? Benchmark { get; set; }
    public int? Level { get; set; }
    public int? Target { get; set; }
    public List<GapModel> Gaps { get; } = new();
    public List<BindingHistoryModel> History { get; } = new();
}

public sealed class GapModel
{
    public string? Dimension { get; set; }
    public string? Gap { get; set; }
    public string? Next { get; set; }
    public string? Opened { get; set; }
}

public sealed class BindingHistoryModel
{
    public string? Date { get; set; }
    public int? From { get; set; }
    public int? To { get; set; }
    public string? Proposal { get; set; }
}
