using System.Text.Encodings.Web;
using System.Text.Json;

namespace PTree.Core;

/// <summary>统一的 JSON 输出：UTF-8、中文不转义、缩进 2 空格。</summary>
public static class JsonOutput
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
