using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PTree.Core;

/// <summary>备份配置分层与路径常量。</summary>
public static class BackupLayout
{
    /// <summary>项目层（进 git）：只放非敏感项。</summary>
    public const string ConfigRel = "00-程序树/运维/备份.yaml";

    /// <summary>台账：每行一个备份点（或一条 trimmed 记录）。</summary>
    public const string JournalRel = "00-程序树/运维/备份.jsonl";

    /// <summary>备份产物目录（必须进 .gitignore）。</summary>
    public const string BackupsDirRel = "00-程序树/运维/备份";

    /// <summary>全局目录覆盖变量：指向替代 %USERPROFILE%\.ptree 的目录（测试与多用户隔离用）。</summary>
    public const string HomeEnvVar = "PTreeBackupHome";

    public const string GlobalFileName = "global.yaml";
    public const string CredentialsFileName = "credentials.enc";

    public static readonly string[] Modes = { "git-bundle", "tag", "copy" };

    public static int DefaultRetention = 10;

    public static string ConfigPath(string root) => Path.Combine(root, ConfigRel.Replace('/', Path.DirectorySeparatorChar));

    public static string JournalPath(string root) => Path.Combine(root, JournalRel.Replace('/', Path.DirectorySeparatorChar));

    public static string BackupsRoot(string root) => Path.Combine(root, BackupsDirRel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>规范化 mode：bundle 视为 git-bundle；未知值原样返回（交给校验/工具报错）。</summary>
    public static string NormalizeMode(string? mode)
    {
        var value = (mode ?? "").Trim().ToLowerInvariant();
        if (value is "" or "bundle" or "git-bundle" or "git_bundle") return "git-bundle";
        if (value == "tag") return "tag";
        if (value == "copy") return "copy";
        return value;
    }

    public static bool IsKnownMode(string? mode) => Modes.Contains(NormalizeMode(mode), StringComparer.Ordinal);
}

/// <summary>全局层目录（不进 git）：密钥、全局默认值、加密凭据。</summary>
public sealed class BackupHome
{
    public string Directory { get; }
    public string GlobalPath => Path.Combine(Directory, BackupLayout.GlobalFileName);
    public string CredentialsPath => Path.Combine(Directory, BackupLayout.CredentialsFileName);
    public string Source { get; }

    private BackupHome(string directory, string source)
    {
        Directory = directory;
        Source = source;
    }

    /// <summary>显式 home 优先；其次环境变量 PTreeBackupHome；最后 %USERPROFILE%\.ptree。</summary>
    public static BackupHome Resolve(string? explicitHome = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitHome))
        {
            return new BackupHome(Path.GetFullPath(explicitHome.Trim()), "argument");
        }
        var fromEnv = Environment.GetEnvironmentVariable(BackupLayout.HomeEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return new BackupHome(Path.GetFullPath(fromEnv.Trim()), "env:" + BackupLayout.HomeEnvVar);
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) profile = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        return new BackupHome(Path.Combine(profile, ".ptree"), "user-profile");
    }
}

/// <summary>项目层配置：enabled / mode / retention / targets / last_run。全部非敏感。</summary>
public sealed class BackupSettings
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "git-bundle";
    public int Retention { get; set; } = BackupLayout.DefaultRetention;
    public List<string> Targets { get; } = new();
    public string? LastRun { get; set; }
    public string? LastTreeVersion { get; set; }
    public string? Note { get; set; }
    /// <summary>mode / retention 是否在项目配置里显式写过（否则可回退全局默认值）。</summary>
    public bool ModeExplicit { get; set; }
    public bool RetentionExplicit { get; set; }
    public bool Exists { get; set; }
    public string Path { get; set; } = "";

    public static BackupSettings Load(string root)
    {
        var path = BackupLayout.ConfigPath(root);
        var settings = new BackupSettings { Path = path, Exists = File.Exists(path) };
        if (!settings.Exists) return settings;
        object? raw;
        try
        {
            raw = YamlLoader.LoadRawDocument(path);
        }
        catch (PTreeException)
        {
            return settings;
        }
        if (raw is not RawMap map) return settings;
        if (map.TryGetValue("enabled", out var enabled)) settings.Enabled = ToBool(enabled) ?? false;
        if (map.TryGetValue("mode", out var mode))
        {
            settings.Mode = BackupLayout.NormalizeMode(mode as string);
            settings.ModeExplicit = true;
        }
        if (map.TryGetValue("retention", out var retention))
        {
            var value = ToInt(retention);
            if (value is > 0)
            {
                settings.Retention = value!.Value;
                settings.RetentionExplicit = true;
            }
        }
        if (map.TryGetValue("targets", out var targets) && targets is List<object?> list)
        {
            foreach (var item in list.OfType<string>().Select(text => text.Trim()).Where(text => text.Length > 0))
            {
                if (!settings.Targets.Contains(item, StringComparer.Ordinal)) settings.Targets.Add(item);
            }
        }
        if (map.TryGetValue("last_run", out var lastRun)) settings.LastRun = lastRun as string;
        if (map.TryGetValue("last_tree_version", out var lastVersion)) settings.LastTreeVersion = lastVersion as string;
        if (map.TryGetValue("note", out var note)) settings.Note = note as string;
        return settings;
    }

    public void Save()
    {
        var map = new RawMap();
        map.Set("enabled", Enabled);
        map.Set("mode", BackupLayout.NormalizeMode(Mode));
        map.Set("retention", (long)Retention);
        var targets = new List<object?>();
        foreach (var target in Targets) targets.Add(target);
        map.Set("targets", targets);
        map.Set("last_run", string.IsNullOrWhiteSpace(LastRun) ? null : LastRun);
        map.Set("last_tree_version", string.IsNullOrWhiteSpace(LastTreeVersion) ? null : LastTreeVersion);
        if (!string.IsNullOrWhiteSpace(Note)) map.Set("note", Note);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(Path, YamlEmitter.Dump(map), new UTF8Encoding(false));
        Exists = true;
    }

    internal static bool? ToBool(object? value) => value switch
    {
        bool flag => flag,
        string text => bool.TryParse(text.Trim(), out var parsed) ? parsed : null,
        _ => null,
    };

    internal static int? ToInt(object? value) => value switch
    {
        long number => (int)number,
        int number => number,
        string text => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        _ => null,
    };
}

/// <summary>全局层：密钥 + 全局默认值。不进 git；缺失即「未配置」，不静默生成（仅 setup 首次配置时生成并回报）。</summary>
public sealed class BackupGlobal
{
    public const string Section = "backup";

    public string? Key { get; set; }
    public int? Retention { get; set; }
    public string? Mode { get; set; }
    public bool Exists { get; set; }
    public BackupHome Home { get; }

    public BackupGlobal(BackupHome home) => Home = home;

    public static BackupGlobal Load(BackupHome home)
    {
        var global = new BackupGlobal(home);
        if (!File.Exists(home.GlobalPath)) return global;
        global.Exists = true;
        object? raw;
        try
        {
            raw = YamlLoader.LoadRawDocument(home.GlobalPath);
        }
        catch (PTreeException)
        {
            return global;
        }
        if (raw is not RawMap root || !root.TryGetValue(Section, out var section) || section is not RawMap backup) return global;
        if (backup.TryGetValue("key", out var key)) global.Key = key as string;
        if (backup.TryGetValue("retention", out var retention)) global.Retention = BackupSettings.ToInt(retention);
        if (backup.TryGetValue("mode", out var mode)) global.Mode = BackupLayout.NormalizeMode(mode as string);
        return global;
    }

    public void Save()
    {
        var backup = new RawMap();
        backup.Set("key", string.IsNullOrWhiteSpace(Key) ? null : Key);
        backup.Set("retention", Retention is null ? null : (long)Retention.Value);
        backup.Set("mode", string.IsNullOrWhiteSpace(Mode) ? null : Mode);
        var root = new RawMap();
        root.Set(Section, backup);
        Directory.CreateDirectory(Home.Directory);
        File.WriteAllText(Home.GlobalPath, YamlEmitter.Dump(root), new UTF8Encoding(false));
        Exists = true;
    }

    public bool HasKey => BackupCrypto.TryParseKey(Key, out _);

    public bool TryGetKey(out byte[] key) => BackupCrypto.TryParseKey(Key, out key);
}

/// <summary>加密凭据：整体写入 credentials.enc（JSON 明文再加密），只能整体读回给调用方，任何输出都脱敏。</summary>
public sealed class BackupCredentials
{
    public string Username { get; set; } = "";
    public string Token { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public bool HasToken => !string.IsNullOrEmpty(Token);

    public static BackupCredentials? Load(BackupGlobal global)
    {
        if (!global.TryGetKey(out var key)) return null;
        if (!File.Exists(global.Home.CredentialsPath)) return null;
        string plaintext;
        try
        {
            plaintext = BackupCrypto.Decrypt(key, File.ReadAllText(global.Home.CredentialsPath, Encoding.UTF8));
        }
        catch (Exception)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new BackupCredentials
            {
                Username = root.TryGetProperty("username", out var username) ? username.GetString() ?? "" : "",
                Token = root.TryGetProperty("token", out var token) ? token.GetString() ?? "" : "",
                UpdatedAt = root.TryGetProperty("updated_at", out var updatedAt) ? updatedAt.GetString() ?? "" : "",
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(BackupGlobal global)
    {
        if (!global.TryGetKey(out var key)) throw new PTreeException("全局配置缺少备份密钥，无法加密凭据");
        var payload = new Dictionary<string, object?>
        {
            ["username"] = Username,
            ["token"] = Token,
            ["updated_at"] = UpdatedAt,
        };
        var plaintext = JsonSerializer.Serialize(payload, JsonOutput.Options);
        var encrypted = BackupCrypto.Encrypt(key, plaintext);
        Directory.CreateDirectory(global.Home.Directory);
        File.WriteAllText(global.Home.CredentialsPath, encrypted, new UTF8Encoding(false));
    }

    /// <summary>脱敏摘要：只回用户名与打码 token。</summary>
    public Dictionary<string, object?> MaskedSummary() => new()
    {
        ["username"] = Username,
        ["token_masked"] = BackupCrypto.MaskToken(Token),
        ["has_token"] = HasToken,
        ["updated_at"] = UpdatedAt,
    };
}
