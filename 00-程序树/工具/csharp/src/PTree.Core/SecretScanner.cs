using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PTree.Core;

/// <summary>一条疑似密钥命中；redacted 只保留前后少量字符，绝不回显完整密钥。</summary>
public sealed record SecretFinding(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("redacted")] string Redacted);

/// <summary>备份前的密钥扫描：只读扫描目标目录，跳过 bin/obj/.git、备份目录、.enc 与二进制文件。</summary>
public static class SecretScanner
{
    public const long MaxFileBytes = 2 * 1024 * 1024;
    public const string GenericKind = "generic-assignment";
    public const string FilenameKind = "sensitive-filename";

    private static readonly RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly RegexOptions IgnoreCaseOptions = Options | RegexOptions.IgnoreCase;

    private static readonly (string Kind, Regex Pattern)[] Patterns =
    {
        ("aws-access-key", new Regex(@"\bAKIA[0-9A-Z]{16}\b", Options)),
        ("github-token", new Regex(@"\b(?:gh[pous]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b", Options)),
        ("openai-key", new Regex(@"\bsk-(?!ant-)(?:proj-)?[A-Za-z0-9_-]{20,}\b", Options)),
        ("anthropic-key", new Regex(@"\bsk-ant-[A-Za-z0-9_-]{20,}\b", Options)),
        ("slack-token", new Regex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", Options)),
        ("google-api-key", new Regex(@"\bAIza[0-9A-Za-z_-]{35}\b", Options)),
        ("stripe-key", new Regex(@"\b(?:sk|rk)_live_[0-9A-Za-z]{16,}\b", Options)),
        ("jwt", new Regex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", Options)),
        ("private-key", new Regex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----", Options)),
        (GenericKind, new Regex(@"(?i)\b(?:api[_-]?key|secret|token|password|passwd|pwd)\s*[:=]\s*[""']?(?<secret>[A-Za-z0-9_\-]{16,})", IgnoreCaseOptions)),
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".a", ".lib", ".obj", ".o", ".class", ".jar", ".war",
        ".zip", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".bundle", ".pack", ".idx", ".png", ".jpg",
        ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".mp3", ".mp4", ".mov", ".avi", ".wav", ".ogg", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".bin",
        ".dat", ".db", ".sqlite", ".sqlite3", ".pak", ".iso", ".dmg", ".msi",
    };

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules",
    };

    public static List<SecretFinding> ScanDirectory(string root, string directory)
    {
        var findings = new List<SecretFinding>();
        if (!Directory.Exists(directory)) return findings;
        var fullRoot = Path.GetFullPath(root);
        var backupRoot = Path.Combine(fullRoot, "00-程序树", "运维", "备份");
        foreach (var file in EnumerateFilesNoLinks(directory))
        {
            var full = Path.GetFullPath(file);
            if (IsUnder(full, backupRoot)) continue;
            var display = DisplayPath(fullRoot, full);
            if (ShouldSkip(display)) continue;
            findings.AddRange(ScanFile(fullRoot, full));
        }
        return findings.OrderBy(finding => finding.File, StringComparer.Ordinal)
            .ThenBy(finding => finding.Line).ThenBy(finding => finding.Kind, StringComparer.Ordinal).ToList();
    }

    public static List<SecretFinding> ScanFile(string root, string file)
    {
        var findings = new List<SecretFinding>();
        var full = Path.GetFullPath(file);
        var display = DisplayPath(Path.GetFullPath(root), full);
        var name = Path.GetFileName(full);
        if (IsSensitiveName(name))
            findings.Add(new SecretFinding(display, 0, FilenameKind, name));
        if (ShouldSkip(display)) return findings;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > MaxFileBytes) return findings;
            var text = File.ReadAllText(full, new UTF8Encoding(false, true));
            if (text.Contains('\0')) return findings;
            findings.AddRange(ScanText(display, text));
        }
        catch (Exception)
        {
            // 无权限、编码异常或文件被占用：不把异常抛给备份流程，也不会回显文件内容。
        }
        return findings;
    }

    public static List<SecretFinding> ScanText(string file, string text)
    {
        var findings = new List<SecretFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var name = Path.GetFileName(file);
        if (IsSensitiveName(name))
        {
            var key = file + "\u0000" + FilenameKind + "\u0000" + name;
            if (seen.Add(key)) findings.Add(new SecretFinding(file, 0, FilenameKind, name));
        }
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0) continue;
            foreach (var (kind, pattern) in Patterns)
            {
                foreach (Match match in pattern.Matches(line))
                {
                    var secret = match.Groups["secret"].Success ? match.Groups["secret"].Value : match.Value;
                    if (secret.Length == 0) continue;
                    var key = index + "\u0000" + kind + "\u0000" + match.Index + "\u0000" + secret.Length;
                    if (!seen.Add(key)) continue;
                    findings.Add(new SecretFinding(file, index + 1, kind, Redact(secret)));
                }
            }
        }
        return findings;
    }

    public static string Redact(string? secret)
    {
        var value = (secret ?? "").Trim();
        if (value.Length == 0) return "";
        if (value.Length <= 10) return "****";
        return value[..6] + "****" + value[^4..];
    }

    private static bool IsSensitiveName(string name) =>
        name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".env", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkip(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => SkipDirectories.Contains(segment))) return true;
        if (normalized.Contains("00-程序树/运维/备份/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("00-程序树/运维/备份", StringComparison.OrdinalIgnoreCase)) return true;
        var extension = Path.GetExtension(relative);
        return extension.Equals(".enc", StringComparison.OrdinalIgnoreCase) || BinaryExtensions.Contains(extension);
    }

    private static string DisplayPath(string root, string full)
    {
        if (IsUnder(full, root)) return Path.GetRelativePath(root, full).Replace('\\', '/');
        return full.Replace('\\', '/');
    }

    private static bool IsUnder(string full, string directory)
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateFilesNoLinks(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(current).ToList();
                dirs = Directory.EnumerateDirectories(current).ToList();
            }
            catch (Exception) { continue; }
            foreach (var file in files) yield return file;
            foreach (var dir in dirs)
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (SkipDirectories.Contains(info.Name)) continue;
                if (IsUnder(Path.GetFullPath(dir), Path.Combine(Path.GetFullPath(root), "00-程序树", "运维", "备份"))) continue;
                queue.Enqueue(dir);
            }
        }
    }
}
