namespace PTree.Core;

/// <summary>状态迁移：旧六值只读兼容，读入后统一映射为四值。</summary>
public static class StatusValues
{
    public const string Todo = "未完成";
    public const string Done = "已完成";
    public const string Verified = "已验证";
    public const string Retired = "退役";
    public static readonly string[] All = { Todo, Done, Verified, Retired };

    public static string? Normalize(string? value) => value switch
    {
        "planned" or "in_progress" or "未完成" => Todo,
        "built" or "已完成" => Done,
        "verified" or "已验证" => Verified,
        "legacy" or "deprecated" or "退役" or "已退役" => Retired,
        _ => value,
    };

    public static bool IsValid(string? value) => value is Todo or Done or Verified or Retired;
}
