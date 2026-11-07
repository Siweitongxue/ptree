using System.Text.Json.Serialization;

namespace PTree.Core;

public sealed class IndexPayload
{
    [JsonPropertyName("tree_version")]
    public string? TreeVersion { get; set; }

    [JsonPropertyName("generated_at")]
    public string? GeneratedAt { get; set; }

    [JsonPropertyName("departments")]
    public Dictionary<string, DepartmentIndexPayload> Departments { get; } = new(StringComparer.Ordinal);

    [JsonPropertyName("groups")]
    public List<GroupPayload> Groups { get; } = new();
}

public sealed class GroupPayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    [JsonPropertyName("members")]
    public List<string> Members { get; } = new();
}

public sealed class DepartmentIndexPayload
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("programs")]
    public List<ProgramIndexPayload> Programs { get; } = new();
}

public sealed class ProgramIndexPayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    [JsonPropertyName("groups")]
    public List<string> Groups { get; } = new();
}

public sealed class NodePayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("layer")]
    public int? Layer { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    [JsonPropertyName("children")]
    public List<string> Children { get; } = new();

    [JsonPropertyName("related_plan")]
    public List<object?> RelatedPlan { get; } = new();

    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    [JsonPropertyName("not_responsible")]
    public List<string> NotResponsible { get; } = new();

    [JsonPropertyName("intro")]
    public IntroPayload Intro { get; set; } = new();

    [JsonPropertyName("code")]
    public CodePayload Code { get; set; } = new();

    [JsonPropertyName("connections")]
    public ConnectionsPayload Connections { get; set; } = new();

    [JsonPropertyName("benchmark")]
    public BenchmarkDetailPayload? Benchmark { get; set; }

    [JsonPropertyName("benchmarks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BenchmarkDetailPayload>? Benchmarks { get; set; }

    [JsonPropertyName("acceptance")]
    public List<string> Acceptance { get; } = new();

    [JsonPropertyName("evidence")]
    public List<EvidencePayload> Evidence { get; } = new();

    [JsonPropertyName("spec")]
    public string? Spec { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("groups")]
    public List<string> Groups { get; } = new();

    [JsonPropertyName("attachments")]
    public List<AttachmentPayload> Attachments { get; } = new();
}

public sealed class AttachmentPayload
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }
}

public sealed class IntroPayload
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public sealed class CodePayload
{
    [JsonPropertyName("home")]
    public string? Home { get; set; }

    [JsonPropertyName("files")]
    public List<string> Files { get; } = new();

    [JsonPropertyName("read_before_edit")]
    public List<string> ReadBeforeEdit { get; } = new();
}

public sealed class ConnectionsPayload
{
    [JsonPropertyName("upstream")]
    public List<ContractConnectionPayload> Upstream { get; } = new();

    [JsonPropertyName("downstream")]
    public List<ContractConnectionPayload> Downstream { get; } = new();

    [JsonPropertyName("depends_on")]
    public List<DependencyPayload> DependsOn { get; } = new();

    [JsonPropertyName("impact")]
    public List<ImpactPayload> Impact { get; } = new();
}

public sealed class ContractConnectionPayload
{
    [JsonPropertyName("contract")]
    public string? Contract { get; set; }

    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<NodeStatusPayload>? From { get; set; }

    [JsonPropertyName("to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<NodeStatusPayload>? To { get; set; }

    [JsonPropertyName("fields")]
    public List<string> Fields { get; } = new();
}

public sealed class NodeStatusPayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class DependencyPayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class ImpactPayload
{
    [JsonPropertyName("node")]
    public string? Node { get; set; }

    [JsonPropertyName("via")]
    public string? Via { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }
}

public sealed class BenchmarkDetailPayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    [JsonPropertyName("level")]
    public int? Level { get; set; }

    [JsonPropertyName("target")]
    public int? Target { get; set; }

    [JsonPropertyName("gaps")]
    public List<GapPayload> Gaps { get; } = new();

    [JsonPropertyName("next")]
    public string? Next { get; set; }

    [JsonPropertyName("levels")]
    public List<BenchmarkLevelPayload> Levels { get; } = new();
}

public sealed class GapPayload
{
    [JsonPropertyName("dimension")]
    public string? Dimension { get; set; }

    [JsonPropertyName("gap")]
    public string? Gap { get; set; }

    [JsonPropertyName("next")]
    public string? Next { get; set; }

    [JsonPropertyName("opened")]
    public string? Opened { get; set; }
}

public sealed class BenchmarkLevelPayload
{
    [JsonPropertyName("level")]
    public int? Level { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("criteria")]
    public string? Criteria { get; set; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; set; }
}

public sealed class EvidencePayload
{
    [JsonPropertyName("test")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Test { get; set; }

    [JsonPropertyName("at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? At { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Result { get; set; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}
