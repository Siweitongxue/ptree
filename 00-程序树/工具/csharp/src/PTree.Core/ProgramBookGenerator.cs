using System.Text;
using System.Text.RegularExpressions;

namespace PTree.Core;

/// <summary>程序书生成结果：Llm=false 时 Content 是离线模板，Reason 说明回退原因。</summary>
public sealed record BookGenerationResult(
    bool Ok,
    bool Llm,
    string Content,
    string Reason,
    string Prompt,
    int Attempts)
{
    public bool Fallback => !Llm;
}

/// <summary>从树模型构造受限 prompt，调用 DeepSeek，并校验输出只能引用白名单路径。</summary>
public static class ProgramBookGenerator
{
    private static readonly Regex PathPattern = new(@"(?:00-程序树/|src/)[\p{L}\p{N}_./-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BuildPrompt(PTreeRepository repository, string? nodeId = null)
    {
        var nodes = nodeId is null
            ? repository.Nodes.Where(node => node.Id is { Length: > 0 }).ToList()
            : repository.Nodes.Where(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal)).ToList();
        if (nodes.Count == 0) nodes = repository.Nodes.Where(node => node.Id is { Length: > 0 }).ToList();
        var whitelist = BuildWhitelist(repository, nodes);
        var builder = new StringBuilder();
        builder.AppendLine("请根据下面这棵程序树的真实节点事实，写一份 800-1200 字的中文纯文本程序书。");
        builder.AppendLine("要求：不要使用 Markdown 的井号标题、星号加粗或列表符号；只写连贯正文；只能引用下列白名单路径，不得编造任何路径、文件名、接口或实现细节。");
        builder.AppendLine("路径白名单：");
        foreach (var path in whitelist.OrderBy(value => value, StringComparer.Ordinal))
            builder.AppendLine("- " + path);
        builder.AppendLine("节点事实：");
        foreach (var node in nodes)
        {
            builder.AppendLine("节点 id：" + node.Id);
            builder.AppendLine("名称：" + (node.Name ?? ""));
            builder.AppendLine("类型：" + (node.Kind ?? "") + "；层级：" + (node.Layer?.ToString() ?? ""));
            builder.AppendLine("职责：" + (node.Responsibility ?? ""));
            builder.AppendLine("输入契约：" + (node.Inputs.Count == 0 ? "无" : string.Join(",", node.Inputs)));
            builder.AppendLine("输出契约：" + (node.Outputs.Count == 0 ? "无" : string.Join(",", node.Outputs)));
            builder.AppendLine("依赖：" + (node.DependsOn.Count == 0 ? "无" : string.Join(",", node.DependsOn)));
            builder.AppendLine("登记文件：" + (node.Files.Count == 0 ? "无" : string.Join(",", node.Files)));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public static HashSet<string> BuildWhitelist(PTreeRepository repository, IEnumerable<NodeModel>? nodes = null)
    {
        var whitelist = new HashSet<string>(StringComparer.Ordinal) { "00-程序树/README.txt" };
        foreach (var node in nodes ?? repository.Nodes)
        {
            if (node.Id is { Length: > 0 } id) whitelist.Add(repository.IntroRel(id));
            if (!string.IsNullOrWhiteSpace(node.Spec)) whitelist.Add(node.Spec!);
            foreach (var file in node.Files.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var normalized = file.Replace('\\', '/');
                whitelist.Add(normalized);
                var srcIndex = normalized.IndexOf("/src/", StringComparison.Ordinal);
                if (srcIndex >= 0) whitelist.Add(normalized[(srcIndex + 1)..]);
            }
        }
        return whitelist;
    }

    /// <summary>提取输出中的 00-程序树/ 与 src/ 路径；返回白名单外的路径。</summary>
    public static List<string> OutOfWhitelistPaths(string content, ISet<string> whitelist)
    {
        var invalid = new List<string>();
        foreach (Match match in PathPattern.Matches(content ?? ""))
        {
            var path = match.Value.TrimEnd('.', ',', ';', ':', '。', '，', '；', '：', ')', '）', ']', '】');
            if (path.Length == 0 || whitelist.Contains(path)) continue;
            if (!invalid.Contains(path, StringComparer.Ordinal)) invalid.Add(path);
        }
        return invalid;
    }

    public static bool ValidateOutput(string content, ISet<string> whitelist) => OutOfWhitelistPaths(content, whitelist).Count == 0;

    public static async Task<BookGenerationResult> GenerateAsync(PTreeRepository repository, DeepSeekClient client, string? nodeId = null)
    {
        var prompt = BuildPrompt(repository, nodeId);
        var template = GeneratedProducts.RenderBook(repository, Validator.Validate(repository));
        var whitelist = BuildWhitelist(repository);
        if (!client.Config.Configured)
            return new BookGenerationResult(false, false, template, "未配置 DeepSeek API Key（DEEPSEEK_API_KEY / global.yaml deepseek.api_key / credentials.enc）", prompt, 0);

        var attempts = 0;
        var lastReason = "";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            attempts++;
            var result = await client.ChatAsync(prompt);
            if (!result.Ok)
            {
                lastReason = result.Error;
                continue;
            }
            var invalid = OutOfWhitelistPaths(result.Content, whitelist);
            if (invalid.Count == 0)
                return new BookGenerationResult(true, true, result.Content.Trim(), "", prompt, attempts);
            lastReason = "输出引用了白名单外路径：" + string.Join(",", invalid.Take(3));
        }
        return new BookGenerationResult(false, false, template, "DeepSeek 生成失败，已回退模板：" + lastReason, prompt, attempts);
    }
}
