using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PTree.Core;

/// <summary>备份台账里的一行（备份点或 trimmed 记录）。</summary>
public sealed class BackupRecord
{
    public string At { get; init; } = "";
    public string TreeVersion { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Target { get; init; } = "";
    public string Bundle { get; init; } = "";
    public string? Commit { get; init; }
    public long Bytes { get; init; }
    public JsonElement Raw { get; init; }
}

/// <summary>
/// 自动备份子系统（附属功能、默认关闭）：把树每次完整改动（tree_version 快照）映射到对应内层代码仓的备份点。
/// 配置分两层：项目层 00-程序树/运维/备份.yaml（非敏感，进 git）+ 全局层 %USERPROFILE%\.ptree（密钥与加密凭据，不进 git）。
/// 所有对外输出都脱敏；凭据只能整体读回，token 明文不进日志/台账/异常/响应。
/// </summary>
public static class BackupService
{
    public const string SetupHint = "请先跑 tree_backup_setup 完成配置（写入用户名与 token）";
    public const string EnableHint = "启用前必须先完成配置：" + SetupHint;
    public const int MaxRetention = 1000;

    // ------------------------------------------------------------------
    // 状态
    // ------------------------------------------------------------------

    public static string Status(string? root, JsonElement? args, BackupHome? home = null)
    {
        var repository = PTreeRepository.Load(root);
        var settings = ApplyGlobalDefaults(repository.Root, home);
        var resolvedHome = home ?? BackupHome.Resolve();
        var global = BackupGlobal.Load(resolvedHome);
        var credentials = BackupCredentials.Load(global);
        var records = ReadJournal(repository.Root);
        var points = EffectivePoints(records);
        var configured = global.HasKey && credentials is not null;
        var warnings = new List<string>();
        if (settings.Enabled && !configured) warnings.Add(UnconfiguredMessage(global, credentials));
        if (settings.Enabled && settings.Targets.Count == 0) warnings.Add("备份已启用但没有配置任何 target，备份不会产出任何备份点");
        var diskBytes = DiskUsage(BackupLayout.BackupsRoot(repository.Root));
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["enabled"] = settings.Enabled,
            ["mode"] = settings.Mode,
            ["retention"] = settings.Retention,
            ["secret_scan"] = settings.SecretScan,
            ["targets"] = new Dictionary<string, object?>
            {
                ["count"] = settings.Targets.Count,
                ["list"] = settings.Targets,
            },
            ["credentials"] = new Dictionary<string, object?>
            {
                ["status"] = configured ? "已配置" : "未配置",
                ["configured"] = configured,
                ["username"] = credentials?.Username ?? "",
                ["token_masked"] = BackupCrypto.MaskToken(credentials?.Token),
                ["has_key"] = global.HasKey,
                ["updated_at"] = credentials?.UpdatedAt ?? "",
            },
            ["last_run"] = settings.LastRun,
            ["last_tree_version"] = settings.LastTreeVersion,
            ["backup_points"] = points.Count,
            ["disk_bytes"] = diskBytes,
            ["disk_text"] = HumanBytes(diskBytes),
            ["config_file"] = settings.Path,
            ["config_exists"] = settings.Exists,
            ["global_home"] = resolvedHome.Directory,
            ["global_home_source"] = resolvedHome.Source,
            ["journal"] = BackupLayout.JournalPath(repository.Root),
            ["warnings"] = warnings,
        });
    }

    // ------------------------------------------------------------------
    // 改配置（enabled / mode / retention / targets）
    // ------------------------------------------------------------------

    public static string Config(string? root, JsonElement? args, BackupHome? home = null)
    {
        var repository = PTreeRepository.Load(root);
        var settings = ApplyGlobalDefaults(repository.Root, home);
        var errors = new List<string>();
        var warnings = new List<string>();

        var enabledArg = BoolArg(args, "enabled");
        var modeArg = StringArg(args, "mode");
        var retentionArg = IntArg(args, "retention");
        var targetsArg = ListArg(args, "targets");
        var addArg = ListArg(args, "add_targets");
        var removeArg = ListArg(args, "remove_targets");
        var secretScanArg = BoolArg(args, "secret_scan");

        if (enabledArg is null && modeArg is null && retentionArg is null && targetsArg is null &&
            addArg is null && removeArg is null && secretScanArg is null)
        {
            return Failure("没有要改的项：至少给 enabled / mode / retention / secret_scan / targets / add_targets / remove_targets 之一");
        }
        if (secretScanArg is not null) settings.SecretScan = secretScanArg.Value;

        if (modeArg is not null)
        {
            var normalized = BackupLayout.NormalizeMode(modeArg);
            if (!BackupLayout.IsKnownMode(normalized))
            {
                errors.Add("mode 只能是 git-bundle / tag / copy（收到：" + modeArg + "）");
            }
            else
            {
                settings.Mode = normalized;
                settings.ModeExplicit = true;
            }
        }
        if (retentionArg is not null)
        {
            if (retentionArg < 1 || retentionArg > MaxRetention)
            {
                errors.Add("retention 必须在 1-" + MaxRetention + " 之间（默认 10）");
            }
            else
            {
                settings.Retention = retentionArg.Value;
                settings.RetentionExplicit = true;
            }
        }
        if (targetsArg is not null)
        {
            settings.Targets.Clear();
            foreach (var target in targetsArg) AddTarget(settings, target, errors);
        }
        if (addArg is not null)
        {
            foreach (var target in addArg) AddTarget(settings, target, errors);
        }
        if (removeArg is not null)
        {
            foreach (var target in removeArg)
            {
                var trimmed = (target ?? "").Trim();
                if (trimmed.Length == 0) continue;
                if (!settings.Targets.Remove(trimmed))
                {
                    var match = settings.Targets.FirstOrDefault(item => string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase));
                    if (match is null) warnings.Add("target 不在配置里，未删除：" + trimmed);
                    else settings.Targets.Remove(match);
                }
            }
        }
        if (errors.Count > 0) return Failure(errors[0], errors);

        if (enabledArg is true)
        {
            var resolvedHome = home ?? BackupHome.Resolve();
            var global = BackupGlobal.Load(resolvedHome);
            var credentials = BackupCredentials.Load(global);
            if (!BackupLayout.IsKnownMode(settings.Mode))
            {
                return Failure("当前 mode 非法（" + settings.Mode + "），不能启用：先改成 git-bundle / tag / copy");
            }
            if (!global.HasKey || credentials is null || string.IsNullOrWhiteSpace(credentials.Username))
            {
                return JsonOutput.Serialize(new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["enabled"] = settings.Enabled,
                    ["error"] = "拒绝启用：全局密钥或加密凭据缺失（" + (global.HasKey ? "凭据未配置" : "密钥未配置") + "）。" + SetupHint,
                    ["hint"] = SetupHint,
                    ["next"] = "tree_backup_setup username=<用户名> token=<token>，之后再 tree_backup_config enabled=true",
                    ["global_home"] = resolvedHome.Directory,
                    ["need_setup"] = true,
                });
            }
            settings.Enabled = true;
        }
        else if (enabledArg is false)
        {
            settings.Enabled = false;
        }

        if (settings.Enabled && settings.Targets.Count == 0)
        {
            warnings.Add("已启用但没有任何 target：请用 add_targets 加内层 git 仓路径或节点 id，否则备份不产出备份点");
        }
        settings.Save();
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["enabled"] = settings.Enabled,
            ["mode"] = settings.Mode,
            ["retention"] = settings.Retention,
            ["secret_scan"] = settings.SecretScan,
            ["targets"] = settings.Targets,
            ["config_file"] = settings.Path,
            ["warnings"] = warnings,
            ["note"] = settings.Enabled ? "备份已启用：每次树完整改动都会产出一个备份点" : "备份已关闭（默认关闭，随时可再开）",
        });
    }

    // ------------------------------------------------------------------
    // 写/改凭据（加密落盘到全局目录）
    // ------------------------------------------------------------------

    public static string Setup(string? root, JsonElement? args, BackupHome? home = null)
    {
        // 只需要项目根来做路径提示；没有项目也能配置（AI 可能先配后开）。
        var projectRoot = string.IsNullOrWhiteSpace(root) ? null : PTreeRepository.ResolveRoot(root);
        var resolvedHome = home ?? BackupHome.Resolve();
        var global = BackupGlobal.Load(resolvedHome);
        var existing = BackupCredentials.Load(global);
        var usernameArg = StringArg(args, "username");
        var tokenArg = StringArg(args, "token");

        var username = string.IsNullOrWhiteSpace(usernameArg) ? existing?.Username ?? "" : usernameArg!.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            return Failure("username 不能为空：请给备份账号的用户名", hint: "tree_backup_setup username=<用户名> token=<token>");
        }
        var token = string.IsNullOrEmpty(tokenArg) ? existing?.Token ?? "" : tokenArg;
        var keyCreated = false;
        if (!global.HasKey)
        {
            global.Key = BackupCrypto.NewKeyBase64();
            global.Save();
            keyCreated = true;
        }
        var stored = new BackupCredentials
        {
            Username = username,
            Token = token,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        stored.Save(global);
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["configured"] = true,
            ["username"] = stored.Username,
            ["token_masked"] = BackupCrypto.MaskToken(stored.Token),
            ["has_token"] = stored.HasToken,
            ["updated_at"] = stored.UpdatedAt,
            ["key_created"] = keyCreated,
            ["credentials_file"] = resolvedHome.CredentialsPath,
            ["global_file"] = resolvedHome.GlobalPath,
            ["global_home"] = resolvedHome.Directory,
            ["project_root"] = projectRoot,
            ["note"] = keyCreated
                ? "已生成 32 字节全局密钥并写入全局配置；token 已 AES-256-GCM 加密保存，只回显脱敏值"
                : "凭据已加密更新，只回显脱敏值",
            ["warning"] = stored.HasToken ? null : "token 为空：只是更新了用户名，没有可用 token",
        });
    }

    // ------------------------------------------------------------------
    // 立即备份
    // ------------------------------------------------------------------

    public static string Now(string? root, JsonElement? args, BackupHome? home = null)
    {
        var repository = PTreeRepository.Load(root);
        var settings = ApplyGlobalDefaults(repository.Root, home);
        var elapsed = Stopwatch.StartNew();
        if (!settings.Exists)
        {
            return Failure("备份未配置：缺少 " + BackupLayout.ConfigRel + "（默认关闭）。先 tree_backup_setup 完成配置，再 tree_backup_config enabled=true",
                hint: SetupHint);
        }
        if (!settings.Enabled)
        {
            return Failure("备份未启用：enabled=false（默认关闭）。先 tree_backup_setup 完成配置，再 tree_backup_config enabled=true",
                hint: SetupHint);
        }
        if (settings.Targets.Count == 0) return Failure("备份已启用但没有 target：先用 tree_backup_config add_targets=[...] 加内层 git 仓");
        var resolvedHome = home ?? BackupHome.Resolve();
        var outcome = Run(repository, settings, resolvedHome, repository.Manifest.TreeVersion, elapsed);
        return JsonOutput.Serialize(outcome);
    }

    /// <summary>只读扫描 targets，列出疑似密钥；命中即返回 ok=false，供 CLI / MCP 在备份前检查。</summary>
    public static string Scan(string? root, JsonElement? args, BackupHome? home = null)
    {
        var repository = PTreeRepository.Load(root);
        var settings = ApplyGlobalDefaults(repository.Root, home);
        var targetFilter = StringArg(args, "target");
        var targets = settings.Targets
            .Where(target => string.IsNullOrWhiteSpace(targetFilter) || string.Equals(target, targetFilter, StringComparison.Ordinal))
            .ToList();
        var findings = new List<SecretFinding>();
        var errors = new List<string>();
        foreach (var target in targets)
        {
            var resolved = ResolveTarget(repository, target);
            if (!resolved.Ok)
            {
                errors.Add(target + "：" + resolved.Error);
                continue;
            }
            findings.AddRange(SecretScanner.ScanDirectory(repository.Root, resolved.Directory!));
        }
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = findings.Count == 0 && errors.Count == 0,
            ["tree_version"] = repository.Manifest.TreeVersion,
            ["secret_scan"] = settings.SecretScan,
            ["targets"] = targets,
            ["findings"] = findings,
            ["errors"] = errors,
            ["hint"] = findings.Count == 0 ? null : "确认不是密钥后，可在 00-程序树/运维/备份.yaml 设 secret_scan: false，或 tree_backup_config secret_scan=false 后重试",
        };
        if (targets.Count == 0) payload["error"] = "没有配置任何 target：先 tree_backup_config add_targets=[...]";
        else if (errors.Count > 0) payload["error"] = "有 target 无法解析：" + errors.Count + " 个";
        else if (findings.Count > 0) payload["error"] = "发现 " + findings.Count + " 处疑似密钥，拒绝生成备份";
        return JsonOutput.Serialize(payload);
    }

    /// <summary>树写操作成功后的自动备份钩子：未启用返回 null，绝不把异常抛给写操作。</summary>
    public static Dictionary<string, object?>? AfterTreeChange(PTreeRepository repository, string? treeVersion)
    {
        try
        {
            BackupHome home;
            BackupSettings settings;
            try
            {
                home = BackupHome.Resolve();
                settings = BackupSettings.Load(repository.Root);
            }
            catch (Exception)
            {
                return null;
            }
            if (!settings.Exists || !settings.Enabled || settings.Targets.Count == 0) return null;
            var elapsed = Stopwatch.StartNew();
            var outcome = Run(repository, settings, home, treeVersion ?? repository.Manifest.TreeVersion, elapsed);
            return new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["ok"] = outcome["ok"],
                ["tree_version"] = outcome["tree_version"],
                ["points"] = outcome["points"],
                ["results"] = outcome["results"],
                ["elapsed_ms"] = outcome["elapsed_ms"],
                ["findings"] = outcome.TryGetValue("findings", out var findings) ? findings : null,
                ["hint"] = outcome.TryGetValue("hint", out var hint) ? hint : null,
                ["error"] = outcome.TryGetValue("error", out var error) ? error : null,
            };
        }
        catch (Exception exception)
        {
            return new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["ok"] = false,
                ["error"] = "自动备份失败（不影响本次树改动）：" + exception.Message,
            };
        }
    }

    // ------------------------------------------------------------------
    // 备份点列表 / 校验
    // ------------------------------------------------------------------

    public static string List(string? root, JsonElement? args, BackupHome? home = null)
    {
        var repository = PTreeRepository.Load(root);
        var targetFilter = StringArg(args, "target");
        var limit = IntArg(args, "limit") is int value && value > 0 ? Math.Min(value, 500) : 20;
        var includeTrimmed = BoolArg(args, "include_trimmed") ?? false;
        var all = ReadJournal(repository.Root);
        var records = (includeTrimmed ? all : EffectivePoints(all))
            .Where(record => targetFilter is null || string.Equals(record.Target, targetFilter, StringComparison.Ordinal));
        var ordered = records
            .OrderByDescending(record => record.TreeVersion, StringComparer.Ordinal)
            .ThenByDescending(record => record.At, StringComparer.Ordinal)
            .ToList();
        var selected = ordered.Take(limit).ToList();
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["count"] = selected.Count,
            ["total"] = ordered.Count,
            ["limit"] = limit,
            ["target"] = targetFilter,
            ["include_trimmed"] = includeTrimmed,
            ["entries"] = selected.Select(record => record.Raw).ToList(),
        });
    }

    public static string Verify(string? root, JsonElement? args, BackupHome? home = null)
    {
        var repository = PTreeRepository.Load(root);
        var targetFilter = StringArg(args, "target");
        var limit = IntArg(args, "limit") is int value && value > 0 ? Math.Min(value, 200) : 50;
        var records = EffectivePoints(ReadJournal(repository.Root))
            .Where(record => string.Equals(record.Kind, "bundle", StringComparison.Ordinal))
            .Where(record => targetFilter is null || string.Equals(record.Target, targetFilter, StringComparison.Ordinal))
            .OrderByDescending(record => record.TreeVersion, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
        var results = new List<Dictionary<string, object?>>();
        var failures = 0;
        foreach (var record in records)
        {
            var path = HealthCheck.ResolvePath(repository.Root, record.Bundle);
            if (string.IsNullOrWhiteSpace(record.Bundle) || !File.Exists(path))
            {
                failures++;
                results.Add(new Dictionary<string, object?>
                {
                    ["tree_version"] = record.TreeVersion,
                    ["target"] = record.Target,
                    ["bundle"] = record.Bundle,
                    ["ok"] = false,
                    ["error"] = "bundle 文件不存在",
                });
                continue;
            }
            // git bundle verify 必须在仓库上下文里跑，否则报「need a repository to verify a bundle」
            var resolved = ResolveTarget(repository, record.Target);
            var workDirectory = resolved.Ok ? resolved.Directory! : repository.Root;
            var result = RunGit(workDirectory, "bundle", "verify", path);
            var ok = result.ExitCode == 0;
            if (!ok) failures++;
            results.Add(new Dictionary<string, object?>
            {
                ["tree_version"] = record.TreeVersion,
                ["target"] = record.Target,
                ["bundle"] = record.Bundle,
                ["bytes"] = record.Bytes,
                ["ok"] = ok,
                ["error"] = ok ? null : FirstLine(result.Stderr),
            });
        }
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = failures == 0,
            ["count"] = results.Count,
            ["failures"] = failures,
            ["entries"] = results,
        });
    }

    // ------------------------------------------------------------------
    // health / guard 的 W 提示
    // ------------------------------------------------------------------

    public static void AppendHealthIssue(HealthReport report, PTreeRepository repository)
    {
        try
        {
            var settings = BackupSettings.Load(repository.Root);
            if (!settings.Exists || !settings.Enabled) return;
            var home = BackupHome.Resolve();
            var global = BackupGlobal.Load(home);
            var credentials = BackupCredentials.Load(global);
            if (global.HasKey && credentials is not null) return;
            report.Issues.Add(new HealthIssue
            {
                Level = "W",
                Kind = "backup_unconfigured",
                Node = "backup",
                Message = "备份已启用但未完成配置（" + (global.HasKey ? "缺少加密凭据" : "缺少全局密钥") + "）：" + SetupHint,
            });
        }
        catch (Exception)
        {
            // health 不能被备份配置拖垮；异常一律静默
        }
    }

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    private static BackupSettings ApplyGlobalDefaults(string root, BackupHome? home)
    {
        var settings = BackupSettings.Load(root);
        BackupGlobal global;
        try
        {
            global = BackupGlobal.Load(home ?? BackupHome.Resolve());
        }
        catch (Exception)
        {
            return settings;
        }
        if (!settings.ModeExplicit && !string.IsNullOrWhiteSpace(global.Mode) && BackupLayout.IsKnownMode(global.Mode))
        {
            settings.Mode = BackupLayout.NormalizeMode(global.Mode);
        }
        if (!settings.RetentionExplicit && global.Retention is > 0)
        {
            settings.Retention = global.Retention.Value;
        }
        return settings;
    }

    private static Dictionary<string, object?> Run(PTreeRepository repository, BackupSettings settings,
        BackupHome home, string? treeVersion, Stopwatch elapsed)
    {
        var version = string.IsNullOrWhiteSpace(treeVersion) ? "unknown" : treeVersion!.Trim();
        var started = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var versionDir = Path.Combine(BackupLayout.BackupsRoot(repository.Root), SafeName(version));
        var results = new List<Dictionary<string, object?>>();
        var written = new List<BackupRecord>();
        var warnings = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var okCount = 0;
        if (settings.SecretScan)
        {
            var findings = new List<SecretFinding>();
            var scannedTargets = new List<Dictionary<string, object?>>();
            foreach (var target in settings.Targets)
            {
                var resolved = ResolveTarget(repository, target);
                if (!resolved.Ok) continue;
                var targetFindings = SecretScanner.ScanDirectory(repository.Root, resolved.Directory!);
                findings.AddRange(targetFindings);
                scannedTargets.Add(new Dictionary<string, object?>
                {
                    ["target"] = target,
                    ["path"] = resolved.Directory,
                    ["findings"] = targetFindings.Count,
                });
            }
            if (findings.Count > 0)
            {
                elapsed.Stop();
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["enabled"] = true,
                    ["tree_version"] = version,
                    ["mode"] = settings.Mode,
                    ["retention"] = settings.Retention,
                    ["secret_scan"] = true,
                    ["started_at"] = started,
                    ["elapsed_ms"] = elapsed.ElapsedMilliseconds,
                    ["points"] = 0,
                    ["results"] = scannedTargets,
                    ["findings"] = findings,
                    ["warnings"] = new List<string>(),
                    ["error"] = "发现 " + findings.Count + " 处疑似密钥，已拒绝生成备份（未写 bundle，未写备份台账）",
                    ["hint"] = "确认不是密钥后，可在 00-程序树/运维/备份.yaml 设 secret_scan: false，或 tree_backup_config secret_scan=false 后重试",
                };
            }
        }
        foreach (var target in settings.Targets)
        {
            var item = new Dictionary<string, object?>
            {
                ["target"] = target,
            };
            var resolved = ResolveTarget(repository, target);
            if (!resolved.Ok)
            {
                item["ok"] = false;
                item["error"] = resolved.Error;
                results.Add(item);
                continue;
            }
            var directory = resolved.Directory!;
            var inside = RunGit(directory, "rev-parse", "--is-inside-work-tree");
            if (inside.ExitCode != 0 || !inside.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                item["ok"] = false;
                item["error"] = "target 不是 git 工作区：" + directory;
                results.Add(item);
                continue;
            }
            var head = RunGit(directory, "rev-parse", "HEAD");
            if (head.ExitCode != 0)
            {
                item["ok"] = false;
                item["error"] = "git rev-parse HEAD 失败（仓库可能还没有提交）：" + FirstLine(head.Stderr);
                results.Add(item);
                continue;
            }
            var commit = head.Stdout.Trim();
            item["path"] = directory;
            item["commit"] = commit;
            Directory.CreateDirectory(versionDir);
            var name = UniqueName(usedNames, SafeName(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
            long bytes;
            string bundleRel;
            string kind;
            try
            {
                switch (settings.Mode)
                {
                    case "tag":
                    {
                        var tag = "ptree-backup-" + SafeName(version) + "-" + name;
                        var tagged = RunGit(directory, "tag", "-f", tag, commit);
                        if (tagged.ExitCode != 0) throw new PTreeException("git tag 失败：" + FirstLine(tagged.Stderr));
                        kind = "tag";
                        bundleRel = "";
                        bytes = 0;
                        item["tag"] = tag;
                        break;
                    }
                    case "copy":
                    {
                        var destination = Path.Combine(versionDir, name);
                        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                        CopyDirectory(directory, destination);
                        kind = "copy";
                        bundleRel = Relative(repository.Root, destination);
                        bytes = DiskUsage(destination);
                        break;
                    }
                    default:
                    {
                        var file = Path.Combine(versionDir, name + ".bundle");
                        if (File.Exists(file)) File.Delete(file);
                        var bundle = RunGit(directory, "bundle", "create", file, "--all");
                        if (bundle.ExitCode != 0) throw new PTreeException("git bundle create 失败：" + FirstLine(bundle.Stderr));
                        if (!File.Exists(file)) throw new PTreeException("git bundle create 未产出文件：" + file);
                        kind = "bundle";
                        bundleRel = Relative(repository.Root, file);
                        bytes = new FileInfo(file).Length;
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                item["ok"] = false;
                item["error"] = exception.Message;
                results.Add(item);
                continue;
            }
            var record = new BackupRecord
            {
                At = started,
                TreeVersion = version,
                Kind = kind,
                Target = target,
                Commit = commit,
                Bundle = bundleRel,
                Bytes = bytes,
            };
            AppendJournal(repository.Root, record);
            written.Add(record);
            item["ok"] = true;
            item["kind"] = kind;
            item["bundle"] = bundleRel;
            item["bytes"] = bytes;
            results.Add(item);
            okCount++;
        }

        foreach (var record in written)
        {
            var trimmed = TrimTarget(repository.Root, record.Target, settings.Retention, started);
            if (trimmed > 0)
            {
                warnings.Add(record.Target + " 超出 retention=" + settings.Retention + "，已裁掉 " + trimmed + " 个旧备份点");
            }
        }

        if (okCount > 0)
        {
            settings.LastRun = started;
            settings.LastTreeVersion = version;
            settings.Save();
        }
        elapsed.Stop();
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = okCount > 0,
            ["enabled"] = true,
            ["tree_version"] = version,
            ["mode"] = settings.Mode,
            ["retention"] = settings.Retention,
            ["started_at"] = started,
            ["elapsed_ms"] = elapsed.ElapsedMilliseconds,
            ["points"] = written.Count,
            ["results"] = results,
            ["warnings"] = warnings,
            ["global_home"] = home.Directory,
        };
        if (okCount == 0) payload["error"] = "没有产出任何备份点：检查 target 是否是有效 git 仓";
        return payload;
    }

    internal sealed record TargetResolution(bool Ok, string? Directory, string? Error);

    private static TargetResolution ResolveTarget(PTreeRepository repository, string target)
    {
        var trimmed = target.Trim().Replace('\\', '/');
        if (trimmed.Length == 0) return new TargetResolution(false, null, "target 为空");
        // 1) 绝对路径 / 相对项目根的路径
        var candidate = Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(repository.Root, trimmed.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(candidate)) return new TargetResolution(true, Path.GetFullPath(candidate), null);
        // 2) 节点 id：home 优先，其次第一个文件的目录
        if (repository.NodeMap.TryGetValue(trimmed, out var node))
        {
            if (!string.IsNullOrWhiteSpace(node.Home))
            {
                var home = HealthCheck.ResolvePath(repository.Root, node.Home!);
                if (Directory.Exists(home)) return new TargetResolution(true, home, null);
            }
            foreach (var file in node.Files)
            {
                var directory = Path.GetDirectoryName(HealthCheck.ResolvePath(repository.Root, file));
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return new TargetResolution(true, directory, null);
            }
            return new TargetResolution(false, null, "节点 " + trimmed + " 登记的 home / files 目录都不存在");
        }
        return new TargetResolution(false, null, "target 不存在（既不是目录，也不是节点 id）：" + target);
    }

    /// <summary>同一 target 只留最近 retention 个备份点：删 bundle 并追加 trimmed 记录。</summary>
    private static int TrimTarget(string root, string target, int retention, string at)
    {
        var keep = retention < 1 ? 1 : retention;
        var records = ReadJournal(root);
        var points = EffectivePoints(records).Where(record => string.Equals(record.Target, target, StringComparison.Ordinal)).ToList();
        if (points.Count <= keep) return 0;
        var trimmed = 0;
        foreach (var record in points.Take(points.Count - keep))
        {
            var path = HealthCheck.ResolvePath(root, record.Bundle);
            try
            {
                if (!string.IsNullOrWhiteSpace(record.Bundle) && File.Exists(path)) File.Delete(path);
                else if (!string.IsNullOrWhiteSpace(record.Bundle) && Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception)
            {
                // 删除失败也要记 trimmed，避免台账与磁盘不一致时静默
            }
            AppendJournal(root, new BackupRecord
            {
                At = at,
                TreeVersion = record.TreeVersion,
                Kind = "trimmed",
                Target = record.Target,
                Commit = record.Commit,
                Bundle = record.Bundle,
                Bytes = record.Bytes,
            });
            trimmed++;
        }
        return trimmed;
    }

    public static List<BackupRecord> ReadJournal(string root)
    {
        var path = BackupLayout.JournalPath(root);
        var records = new List<BackupRecord>();
        if (!File.Exists(path)) return records;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var element = document.RootElement;
                if (element.ValueKind != JsonValueKind.Object) continue;
                records.Add(new BackupRecord
                {
                    At = Text(element, "at"),
                    TreeVersion = Text(element, "tree_version"),
                    Kind = Text(element, "kind"),
                    Target = Text(element, "target"),
                    Bundle = Text(element, "bundle"),
                    Commit = element.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.String ? commit.GetString() : null,
                    Bytes = element.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out var size) ? size : 0,
                    Raw = element.Clone(),
                });
            }
            catch (JsonException)
            {
            }
        }
        return records;
    }

    /// <summary>台账必须一行一条（jsonl），所以用紧凑 JSON，不用 JsonOutput 的缩进格式。</summary>
    private static readonly JsonSerializerOptions JournalOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static void AppendJournal(string root, BackupRecord record)
    {
        var path = BackupLayout.JournalPath(root);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var payload = new Dictionary<string, object?>
        {
            ["at"] = record.At,
            ["tree_version"] = record.TreeVersion,
            ["kind"] = record.Kind,
            ["target"] = record.Target,
            ["commit"] = record.Commit,
            ["bundle"] = record.Bundle,
            ["bytes"] = record.Bytes,
        };
        File.AppendAllText(path, JsonSerializer.Serialize(payload, JournalOptions) + "\n", new UTF8Encoding(false));
    }

    private static bool IsTrimmed(BackupRecord record) => string.Equals(record.Kind, "trimmed", StringComparison.Ordinal);

    /// <summary>
    /// 台账是 append-only：裁剪时只追加 kind=trimmed 的墓碑记录，不重写历史行。
    /// 有效备份点 = 非 trimmed 且没有对应墓碑（同 target + tree_version + bundle）的行。
    /// </summary>
    public static List<BackupRecord> EffectivePoints(IEnumerable<BackupRecord> records)
    {
        var all = records.ToList();
        var tombstoned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in all)
        {
            if (IsTrimmed(record)) tombstoned.Add(PointKey(record));
        }
        return all.Where(record => !IsTrimmed(record) && !tombstoned.Contains(PointKey(record))).ToList();
    }

    private static string PointKey(BackupRecord record) =>
        record.Target + "\u0000" + record.TreeVersion + "\u0000" + record.Bundle;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static void AddTarget(BackupSettings settings, string? raw, List<string> errors)
    {
        var target = (raw ?? "").Trim();
        if (target.Length == 0)
        {
            errors.Add("target 不能为空");
            return;
        }
        if (!settings.Targets.Contains(target, StringComparer.Ordinal)) settings.Targets.Add(target);
    }

    private static string UnconfiguredMessage(BackupGlobal global, BackupCredentials? credentials)
    {
        var missing = global.HasKey ? "加密凭据未配置" : "全局密钥未配置";
        return "备份已启用但未完成配置（" + missing + "）：" + SetupHint;
    }

    private static string Failure(string error, IReadOnlyList<string>? errors = null, string? hint = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = error,
        };
        if (errors is { Count: > 1 }) payload["errors"] = errors;
        if (!string.IsNullOrWhiteSpace(hint)) payload["hint"] = hint;
        return JsonOutput.Serialize(payload);
    }

    private static string SafeName(string raw)
    {
        var builder = new StringBuilder();
        foreach (var ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.') builder.Append(ch);
            else builder.Append('-');
        }
        var name = builder.ToString().Trim('-', '.');
        if (name.Length == 0) name = "repo";
        if (name.Length > 80) name = name[..80];
        return name;
    }

    private static string UniqueName(HashSet<string> used, string name)
    {
        var candidate = name;
        var index = 2;
        while (!used.Add(candidate))
        {
            candidate = name + "-" + index;
            index++;
        }
        return candidate;
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static long DiskUsage(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { }
            }
        }
        catch (IOException)
        {
        }
        return total;
    }

    private static string HumanBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }

    private static (int ExitCode, string Stdout, string Stderr) RunGit(string directory, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(directory);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return (-1, "", "无法启动 git");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception exception)
        {
            return (-1, "", exception.Message);
        }
    }

    private static string FirstLine(string text) => text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? text;

    // ------------------------------------------------------------------
    // 参数解析（与 MCP 一致：字符串/数字/布尔/数组都归一）
    // ------------------------------------------------------------------

    private static string? StringArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int? IntArg(JsonElement? args, string name)
    {
        var text = StringArg(args, name);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static bool? BoolArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static List<string>? ListArg(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Array)
        {
            var result = new List<string>();
            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
                }
            }
            return result;
        }
        if (property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        return null;
    }
}
