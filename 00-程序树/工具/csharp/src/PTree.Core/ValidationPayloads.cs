using System.Text.Json.Serialization;

namespace PTree.Core;

public sealed class Issue
{
    public string Level { get; }
    public string Code { get; }
    public string? Node { get; }
    public string Message { get; }

    public Issue(string level, string code, string message, string? node = null)
    {
        Level = level;
        Code = code;
        Node = node;
        Message = message;
    }

    public static Issue Error(string code, string message, string? node = null) => new("E", code, message, node);
}

public sealed class ValidationIssuePayload
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("node")]
    public string? Node { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class ValidationPayload
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("errors")]
    public List<ValidationIssuePayload> Errors { get; } = new();

    [JsonPropertyName("warnings")]
    public List<ValidationIssuePayload> Warnings { get; } = new();

    [JsonPropertyName("notes")]
    public List<ValidationIssuePayload> Notes { get; } = new();

    public static ValidationPayload From(IEnumerable<Issue> issues)
    {
        var payload = new ValidationPayload();
        foreach (var issue in issues)
        {
            var item = new ValidationIssuePayload { Code = issue.Code, Node = issue.Node, Message = issue.Message };
            switch (issue.Level)
            {
                case "E":
                    payload.Errors.Add(item);
                    break;
                case "W":
                    payload.Warnings.Add(item);
                    break;
                default:
                    payload.Notes.Add(item);
                    break;
            }
        }
        payload.Ok = payload.Errors.Count == 0;
        return payload;
    }
}
