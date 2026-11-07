using System.Text;

namespace PTree.Core;

/// <summary>
/// 评价空间：优先读 00-程序树/评价/&lt;节点id&gt;.txt 的固定四行（判断/位置/做法/缺）；
/// 缺文件或字段时回退读 yaml 节点的 evaluation 字符串；两处都没有就当作没有评价。
/// </summary>
public sealed class EvaluationSpace
{
    public static readonly string[] Judgments = { "必须有", "应该有", "可有可无", "不该有" };

    private static readonly char[] Separators = { ' ', '\t', '\u3000', ':', '：' };

    public string? Judgment { get; private set; }
    public string? Position { get; private set; }
    public string? Method { get; private set; }
    public string? Missing { get; private set; }
    public string? FilePath { get; private set; }
    public string Source { get; private set; } = "";

    public bool Has => !string.IsNullOrWhiteSpace(Judgment);

    /// <summary>读一个节点的评价：评价文件优先，yaml 兜底。</summary>
    public static EvaluationSpace Load(PTreeRepository repository, NodeModel node)
    {
        var space = new EvaluationSpace();
        var id = node.Id ?? "";
        if (id.Length > 0) space.ReadFile(repository.EvaluationPath(id));
        if (!string.IsNullOrWhiteSpace(node.Evaluation)) space.FillFromYaml(node.Evaluation!);
        return space;
    }

    /// <summary>只读评价文件；文件不存在时返回空评价。</summary>
    public static EvaluationSpace ReadOnly(string path)
    {
        var space = new EvaluationSpace();
        space.ReadFile(path);
        return space;
    }

    private void ReadFile(string path)
    {
        if (!File.Exists(path)) return;
        FilePath = path;
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            var index = line.IndexOfAny(Separators);
            string key;
            string value;
            if (index < 0)
            {
                key = line;
                value = "";
            }
            else
            {
                key = line[..index];
                value = line[(index + 1)..].Trim();
            }
            if (value.Length == 0) continue;
            switch (key)
            {
                case "判断":
                    Judgment = value;
                    Source = "file";
                    break;
                case "位置":
                    Position = value;
                    break;
                case "做法":
                    Method = value;
                    break;
                case "缺":
                    Missing = value;
                    break;
            }
        }
    }

    private void FillFromYaml(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        if (!Has)
        {
            var judgment = Judgments.FirstOrDefault(trimmed.Contains);
            if (judgment is not null)
            {
                Judgment = judgment;
                Source = "yaml";
            }
        }
        Position ??= PositionFromYaml(trimmed);
        Missing ??= MissingFromYaml(trimmed);
    }

    private static string? PositionFromYaml(string text)
    {
        if (text.Contains("位置对", StringComparison.Ordinal)) return "对";
        const string move = "应移到";
        var moveIndex = text.IndexOf(move, StringComparison.Ordinal);
        if (moveIndex < 0) return null;
        var target = text[(moveIndex + move.Length)..].TrimStart('：', ':', '，', ',', ' ');
        var end = target.IndexOfAny(new[] { '，', ',', '。', '；', ';', ' ' });
        return end < 0 ? target : target[..end];
    }

    private static string? MissingFromYaml(string text)
    {
        const string marker = "当前缺的是";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var tail = text[(index + marker.Length)..].TrimStart('：', ':', '，', ',', ' ');
        var end = tail.IndexOf('。');
        return end < 0 ? tail : tail[..end];
    }
}
