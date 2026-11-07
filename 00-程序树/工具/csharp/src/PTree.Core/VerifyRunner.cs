using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PTree.Core;

/// <summary>一键验证：validate / check / 四树解析 / 生成物新鲜度 / 说明覆盖 / 策划映射 / MCP / 插件冒烟。</summary>
public sealed class VerifyCheck
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "internal";

    [JsonPropertyName("blocking")]
    public bool Blocking { get; set; } = true;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "PASS";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; }

    [JsonPropertyName("metrics")]
    public Dictionary<string, object?> Metrics { get; } = new(StringComparer.Ordinal);

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("detail")]
    public List<string> Detail { get; } = new();

    /// <summary>这一项检查自己的单次耗时（毫秒）；报告里直接可见，用来分辨「慢」和「红」。</summary>
    [JsonPropertyName("elapsed_ms")]
    public double ElapsedMs { get; set; }
}

public sealed class VerifyHonest
{
    [JsonPropertyName("verified_nodes")]
    public int VerifiedNodes { get; set; }

    [JsonPropertyName("evidence_count")]
    public int EvidenceCount { get; set; }

    [JsonPropertyName("nodes_with_home")]
    public int NodesWithHome { get; set; }

    [JsonPropertyName("missing_intro")]
    public int MissingIntro { get; set; }

    [JsonPropertyName("planning_unmapped")]
    public int PlanningUnmapped { get; set; }
}

public sealed class VerifyResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("generated_at")]
    public string GeneratedAt { get; set; } = "";

    [JsonPropertyName("root")]
    public string Root { get; set; } = "";

    [JsonPropertyName("checks")]
    public List<VerifyCheck> Checks { get; } = new();

    [JsonPropertyName("honest")]
    public VerifyHonest Honest { get; set; } = new();

    [JsonPropertyName("pass_count")]
    public int PassCount { get; set; }

    [JsonPropertyName("fail_count")]
    public int FailCount { get; set; }

    [JsonPropertyName("warn_count")]
    public int WarnCount { get; set; }

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; }
}

/// <summary>verify 的实现：只读检查，报告由 RenderReport 生成；插件冒烟失败只记 WARN 不阻断。</summary>
public static class VerifyRunner
{
    public static VerifyResult Run(PTreeRepository repository)
    {
        var result = new VerifyResult
        {
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Root = repository.Root,
        };

        var validateWatch = Stopwatch.StartNew();
        var validation = Validator.Validate(repository);
        result.Checks.Add(new VerifyCheck
        {
            Name = "validate",
            Command = "ptree-cs validate（进程内调用 Validator.Validate）",
            ExitCode = validation.Ok ? 0 : 1,
            Status = validation.Ok ? "PASS" : "FAIL",
            Message = "清单校验 R01-R46",
        }.WithMetrics(("errors", validation.Errors.Count), ("warnings", validation.Warnings.Count), ("notes", validation.Notes.Count))
         .WithDetail(validation.Errors.Select(issue => "[E] " + issue.Code + " " + (issue.Node ?? "-") + ": " + issue.Message).Take(10))
         .WithElapsed(validateWatch));

        var checkWatch = Stopwatch.StartNew();
        var treeIssues = new List<TreeIssue>();
        TreeStore? store = null;
        var treeError = "";
        try
        {
            store = TreeStore.Load(repository.Root);
            treeIssues.AddRange(TreeChecker.Check(store));
            treeIssues.AddRange(ManifestChecker.Check(repository, store));
        }
        catch (Exception exception)
        {
            treeError = exception.Message;
        }
        var treeErrors = treeIssues.Count(issue => issue.Level == "E");
        var treeWarnings = treeIssues.Count(issue => issue.Level == "W");
        var treeNotes = treeIssues.Count(issue => issue.Level == "I");
        result.Checks.Add(new VerifyCheck
        {
            Name = "check",
            Command = "ptree-cs check（进程内调用 TreeChecker + ManifestChecker）",
            ExitCode = treeError.Length == 0 && treeErrors == 0 ? 0 : 1,
            Status = treeError.Length == 0 && treeErrors == 0 ? "PASS" : "FAIL",
            Message = treeError.Length > 0 ? "check 抛错：" + treeError : "跨树检查 T01-T12（树实例或校验器可能正在被改，红是如实记录）",
        }.WithMetrics(("errors", treeErrors), ("warnings", treeWarnings), ("notes", treeNotes))
         .WithDetail(treeIssues.Where(issue => issue.Level == "E").Select(issue => "[E] " + issue.Code + " " + (issue.Kind ?? "-") + ":" + (issue.Line ?? 0) + " " + issue.Message).Take(10))
         .WithElapsed(checkWatch));

        var instanceWatch = Stopwatch.StartNew();
        var parsedKinds = new List<string>();
        var parseErrors = 0;
        var entryCount = 0;
        if (store is not null)
        {
            parsedKinds.AddRange(store.Documents.Select(document => document.Kind));
            parseErrors = store.ParseIssues.Count;
            entryCount = store.Documents.Sum(document => document.Entries.Count);
        }
        var docsCount = parsedKinds.Count;
        result.Checks.Add(new VerifyCheck
        {
            Name = "tree-instances",
            Command = "读取 00-程序树/树实例/ 清单 trees 声明的树（进程内 TreeStore.Load）",
            ExitCode = docsCount > 0 && parseErrors == 0 ? 0 : 1,
            Status = docsCount > 0 && parseErrors == 0 ? "PASS" : "FAIL",
            Message = docsCount > 0 ? "已加载 " + docsCount + " 棵树：" + string.Join("、", parsedKinds) : "没有可解析的树实例",
        }.WithMetrics(("documents", docsCount), ("entries", entryCount), ("parse_errors", parseErrors))
         .WithDetail(store?.ParseIssues.Select(issue => "[E] " + issue.Code + " " + issue.Kind + ":" + issue.Line + " " + issue.Message).Take(10) ?? Enumerable.Empty<string>())
         .WithElapsed(instanceWatch));

        var freshnessWatch = Stopwatch.StartNew();
        var manifestPath = Path.Combine(repository.Root, PTreeRepository.ManifestRel.Replace('/', Path.DirectorySeparatorChar));
        var generatedDir = Path.Combine(repository.Root, GeneratedProducts.GeneratedDir.Replace('/', Path.DirectorySeparatorChar));
        var manifestTime = File.Exists(manifestPath) ? File.GetLastWriteTimeUtc(manifestPath) : DateTime.MinValue;
        var staleFiles = new List<string>();
        var missingFiles = new List<string>();
        var expectedFiles = new[] { GeneratedProducts.ViewFile, GeneratedProducts.OrderFile, GeneratedProducts.BookFile, GeneratedProducts.BenchmarkFile };
        foreach (var name in expectedFiles)
        {
            var path = Path.Combine(generatedDir, name);
            if (!File.Exists(path))
            {
                missingFiles.Add(name);
            }
            else if (File.GetLastWriteTimeUtc(path) < manifestTime)
            {
                staleFiles.Add(name);
            }
        }
        result.Checks.Add(new VerifyCheck
        {
            Name = "generated-freshness",
            Command = "比较 program-tree.yaml 与 生成/*.txt 的修改时间",
            ExitCode = missingFiles.Count == 0 && staleFiles.Count == 0 ? 0 : 1,
            Status = missingFiles.Count == 0 && staleFiles.Count == 0 ? "PASS" : "FAIL",
            Message = missingFiles.Count > 0 ? "生成物缺失：" + string.Join("、", missingFiles) :
                staleFiles.Count > 0 ? "生成物比清单旧：" + string.Join("、", staleFiles) : "四份生成物均不早于清单",
        }.WithMetrics(("missing", missingFiles.Count), ("stale", staleFiles.Count), ("manifest_mtime", manifestTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
         .WithDetail(expectedFiles.Select(name => name + " = " + (File.Exists(Path.Combine(generatedDir, name)) ? File.GetLastWriteTimeUtc(Path.Combine(generatedDir, name)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "缺失")))
         .WithElapsed(freshnessWatch));

        var briefWatch = Stopwatch.StartNew();
        var briefPath = Path.Combine(generatedDir, BriefRenderer.FileName);
        var briefExists = File.Exists(briefPath);
        var briefStale = briefExists && File.GetLastWriteTimeUtc(briefPath) < manifestTime;
        var briefBytes = briefExists ? new FileInfo(briefPath).Length : 0;
        var briefLines = briefExists ? PythonText.SplitLines(File.ReadAllText(briefPath)).Count : 0;
        result.Checks.Add(new VerifyCheck
        {
            Name = "brief-freshness",
            Command = "比较 program-tree.yaml 与 生成/项目速览.txt（不懂项目先读它）",
            ExitCode = briefExists && !briefStale ? 0 : 1,
            Status = briefExists && !briefStale ? "PASS" : "FAIL",
            Message = !briefExists ? "速览缺失：生成/项目速览.txt（运行 ptree-cs brief）" :
                briefStale ? "速览比清单旧：生成/项目速览.txt（运行 ptree-cs brief 重生成）" : "速览存在且不早于清单",
        }.WithMetrics(("exists", briefExists ? 1 : 0), ("stale", briefStale ? 1 : 0), ("lines", briefLines), ("bytes", briefBytes))
         .WithDetail(new[]
         {
             "速览路径：" + (briefExists ? "00-程序树/生成/" + BriefRenderer.FileName : "缺失"),
             "上限：" + BriefRenderer.MaxLines + " 行 / " + BriefRenderer.MaxBytes + " 字节；实测 " + briefLines + " 行 / " + briefBytes + " 字节",
         })
         .WithElapsed(briefWatch));

        var introWatch = Stopwatch.StartNew();
        var expectedIntro = repository.Nodes
            .Where(node => node.Kind is "program" or "department")
            .Select(node => node.Id ?? "?")
            .ToList();
        var missingIntro = expectedIntro.Where(id => !File.Exists(repository.IntroPath(id))).ToList();
        result.Checks.Add(new VerifyCheck
        {
            Name = "intro-coverage",
            Command = "统计 00-程序树/说明/<id>.txt 对 program/department 节点的覆盖",
            ExitCode = missingIntro.Count == 0 ? 0 : 1,
            Status = missingIntro.Count == 0 ? "PASS" : "FAIL",
            Message = missingIntro.Count == 0 ? "说明覆盖完整" : "缺说明节点：" + string.Join("、", missingIntro.Take(10)) + (missingIntro.Count > 10 ? " 等" : ""),
        }.WithMetrics(("expected", expectedIntro.Count), ("present", expectedIntro.Count - missingIntro.Count), ("missing", missingIntro.Count))
         .WithDetail(missingIntro.Take(20))
         .WithElapsed(introWatch));

        var planningWatch = Stopwatch.StartNew();
        var (unmapped, planningDetail) = PlanningCoverage(repository, store);
        result.Checks.Add(new VerifyCheck
        {
            Name = "planning-coverage",
            Command = "统计策划树 对应程序 字段对 program 节点的覆盖（含豁免；通配词不算覆盖）",
            ExitCode = unmapped.Count == 0 ? 0 : 1,
            Status = unmapped.Count == 0 ? "PASS" : "FAIL",
            Message = unmapped.Count == 0 ? "program 节点均有策划条目或豁免" : "策划未映射：" + string.Join("、", unmapped.Take(10)) + (unmapped.Count > 10 ? " 等" : ""),
        }.WithMetrics(("unmapped", unmapped.Count)).WithDetail(planningDetail.Take(20))
         .WithElapsed(planningWatch));

        result.Honest = new VerifyHonest
        {
            VerifiedNodes = repository.Nodes.Count(node => node.Status == "已验证" || node.Status == "verified"),
            EvidenceCount = repository.Nodes.Sum(node => node.Evidence.Count),
            NodesWithHome = repository.Nodes.Count(node => !string.IsNullOrWhiteSpace(node.Home)),
            MissingIntro = missingIntro.Count,
            PlanningUnmapped = unmapped.Count,
        };

        var mcpWatch = Stopwatch.StartNew();
        result.Checks.Add(RunMcp(repository).WithElapsed(mcpWatch));
        var smokeWatch = Stopwatch.StartNew();
        result.Checks.Add(RunPluginSmoke(repository).WithElapsed(smokeWatch));

        result.PassCount = result.Checks.Count(check => check.Status == "PASS");
        result.WarnCount = result.Checks.Count(check => check.Status == "WARN");
        result.FailCount = result.Checks.Count(check => check.Status == "FAIL");
        result.Ok = result.FailCount == 0;
        result.ExitCode = result.Ok ? 0 : 1;
        return result;
    }

    private static VerifyCheck WithElapsed(this VerifyCheck check, Stopwatch watch)
    {
        watch.Stop();
        check.ElapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1, MidpointRounding.AwayFromZero);
        return check;
    }

    private static VerifyCheck WithMetrics(this VerifyCheck check, params (string Key, object? Value)[] metrics)
    {
        foreach (var (key, value) in metrics) check.Metrics[key] = value;
        return check;
    }

    private static VerifyCheck WithDetail(this VerifyCheck check, IEnumerable<string> lines)
    {
        check.Detail.AddRange(lines);
        return check;
    }

    private static (List<string> Unmapped, List<string> Detail) PlanningCoverage(PTreeRepository repository, TreeStore? store)
    {
        var unmapped = new List<string>();
        var detail = new List<string>();
        var planning = store?.FindDocument("策划树");
        if (planning is null)
        {
            unmapped.AddRange(repository.Nodes.Where(node => node.Kind == "program").Select(node => node.Id ?? "?"));
            detail.Add("策划树实例不可用，全部 program 视为未映射");
            return (unmapped, detail);
        }
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var wildcardInvalid = false;
        foreach (var entry in planning.Entries)
        {
            foreach (var field in entry.Fields)
            {
                foreach (var token in Regex.Split(field, "[、,，；;]"))
                {
                    var value = token.Trim();
                    // 策划树规范禁止“全部程序节点”通配词（check T09 视为 E），因此它不构成映射覆盖
                    if (value == "全部程序节点") wildcardInvalid = true;
                    else if (value.Length > 0 && !value.Contains(' ') && !value.Contains("见") && !value.Contains("待")) referenced.Add(value);
                }
            }
        }
        if (wildcardInvalid) detail.Add("策划树出现通配词“全部程序节点”（check T09 判 E），不计入映射覆盖");
        foreach (var node in repository.Nodes.Where(node => node.Kind == "program"))
        {
            var id = node.Id ?? "?";
            if (referenced.Contains(id)) continue;
            if (!string.IsNullOrWhiteSpace(node.PlanningExemption))
            {
                detail.Add("豁免：" + id + " —— " + node.PlanningExemption);
                continue;
            }
            unmapped.Add(id);
            detail.Add("未映射：" + id);
        }
        return (unmapped, detail);
    }

    private static VerifyCheck RunMcp(PTreeRepository repository)
    {
        var check = new VerifyCheck
        {
            Name = "mcp",
            Kind = "external",
            Command = "ptree-cs mcp（管道发 initialize + tools/list）",
        };
        try
        {
            var (fileName, arguments) = CurrentCliInvocation();
            var startInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = repository.Root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add("mcp");
            startInfo.ArgumentList.Add("--root");
            startInfo.ArgumentList.Add(repository.Root);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                check.Status = "FAIL";
                check.ExitCode = 1;
                check.Message = "MCP 进程启动失败";
                return check;
            }
            // 先异步收 stdout/stderr：新工具集的 tools/list 帧超过管道缓冲区，边写边读才不会死锁。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}");
            process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
            process.StandardInput.Close();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(true); } catch { }
                check.Status = "FAIL";
                check.ExitCode = 1;
                check.Message = "MCP 15 秒内未退出";
                return check;
            }
            var output = stdoutTask.GetAwaiter().GetResult();
            var errors = stderrTask.GetAwaiter().GetResult();
            var frames = PythonText.SplitLines(output).Where(line => line.Trim().Length > 0).ToList();
            var toolsCount = 0;
            var handshakeOk = false;
            foreach (var frame in frames)
            {
                try
                {
                    using var document = JsonDocument.Parse(frame);
                    if (!document.RootElement.TryGetProperty("id", out var id)) continue;
                    if (id.ValueKind == JsonValueKind.Number && id.GetInt32() == 1) handshakeOk = true;
                    if (id.ValueKind == JsonValueKind.Number && id.GetInt32() == 2 &&
                        document.RootElement.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
                    {
                        toolsCount = tools.GetArrayLength();
                    }
                }
                catch (JsonException)
                {
                }
            }
            check.Metrics["frames"] = frames.Count;
            check.Metrics["tools"] = toolsCount;
            check.ExitCode = handshakeOk && toolsCount >= 5 ? 0 : 1;
            check.Status = check.ExitCode == 0 ? "PASS" : "FAIL";
            check.Message = handshakeOk ? "MCP initialize 成功；tools/list 返回 " + toolsCount + " 个工具" : "MCP 未返回 initialize 响应";
            if (errors.Length > 0) check.Detail.Add("stderr: " + errors.Trim().Split('\n').Last());
            return check;
        }
        catch (Exception exception)
        {
            check.Status = "FAIL";
            check.ExitCode = 2;
            check.Message = "MCP 检查失败：" + exception.Message;
            return check;
        }
    }

    private static VerifyCheck RunPluginSmoke(PTreeRepository repository)
    {
        var check = new VerifyCheck
        {
            Name = "plugin-smoke",
            Kind = "external",
            Blocking = false,
            Command = "node 10-程序/governance/program-tree-plugin/tests/smoke.mjs（失败只记 WARN，不阻断）",
        };
        try
        {
            var script = Path.Combine(repository.Root, "10-程序", "governance", "program-tree-plugin", "tests", "smoke.mjs");
            if (!File.Exists(script))
            {
                check.Status = "WARN";
                check.ExitCode = 2;
                check.Message = "插件冒烟脚本不存在：" + script;
                return check;
            }
            var startInfo = new ProcessStartInfo("node")
            {
                WorkingDirectory = repository.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            startInfo.ArgumentList.Add(script);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                check.Status = "WARN";
                check.ExitCode = 2;
                check.Message = "node 无法启动";
                return check;
            }
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60000))
            {
                try { process.Kill(true); } catch { }
                check.Status = "WARN";
                check.ExitCode = 2;
                check.Message = "插件冒烟 60 秒超时";
                return check;
            }
            var lastLine = PythonText.SplitLines(stdout).LastOrDefault(line => line.Trim().Length > 0) ?? "";
            check.ExitCode = process.ExitCode;
            check.Status = process.ExitCode == 0 ? "PASS" : "WARN";
            check.Message = process.ExitCode == 0 ? "插件冒烟通过：" + lastLine.Trim() : "插件冒烟失败（不阻断）：" + (lastLine.Trim().Length > 0 ? lastLine.Trim() : stderr.Trim().Split('\n').Last());
            return check;
        }
        catch (Exception exception)
        {
            check.Status = "WARN";
            check.ExitCode = 2;
            check.Message = "插件冒烟无法执行（不阻断）：" + exception.Message;
            return check;
        }
    }

    private static (string FileName, List<string> Arguments) CurrentCliInvocation()
    {
        var process = Environment.ProcessPath ?? "dotnet";
        var assembly = typeof(VerifyRunner).Assembly.Location;
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? assembly;
        var name = Path.GetFileNameWithoutExtension(process);
        if (name.Equals("ptree-cs", StringComparison.OrdinalIgnoreCase))
        {
            return (process, new List<string>());
        }
        return ("dotnet", new List<string> { entry });
    }

    public static string RenderReport(VerifyResult result, string reportPath)
    {
        var lines = new List<string>
        {
            "AI Company 一键验证报告",
            "",
            "由 ptree-cs verify 生成，禁止手改",
            "生成时间：" + result.GeneratedAt,
            "仓库根：" + result.Root,
            "报告路径：" + reportPath,
            "",
            "一、检查项",
            "",
        };
        foreach (var check in result.Checks)
        {
            lines.Add("[" + check.Status + "] " + check.Name + "（" + (check.Blocking ? "阻断" : "不阻断") + "）");
            lines.Add("  命令：" + check.Command);
            lines.Add("  退出码：" + check.ExitCode);
            lines.Add("  问题：" + check.Message);
            if (check.Metrics.Count > 0)
            {
                lines.Add("  关键数字：" + string.Join("；", check.Metrics.Select(pair => pair.Key + "=" + (Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? ""))));
            }
            lines.Add("  耗时：" + check.ElapsedMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            foreach (var detail in check.Detail.Take(10)) lines.Add("  明细：" + detail);
            lines.Add("");
        }
        lines.Add("二、诚实指标");
        lines.Add("");
        lines.Add("  已验证节点数：" + result.Honest.VerifiedNodes);
        lines.Add("  证据条数：" + result.Honest.EvidenceCount);
        lines.Add("  有 home 节点数：" + result.Honest.NodesWithHome);
        lines.Add("  缺说明节点数：" + result.Honest.MissingIntro);
        lines.Add("  策划未映射节点数：" + result.Honest.PlanningUnmapped);
        lines.Add("");
        lines.Add("三、汇总");
        lines.Add("");
        lines.Add("  通过：" + result.PassCount + "；失败：" + result.FailCount + "；警告（不阻断）：" + result.WarnCount);
        lines.Add("  结论：" + (result.Ok ? "PASS" : "FAIL"));
        lines.Add("  建议退出码：" + result.ExitCode);
        lines.Add("");
        lines.Add("说明：插件 smoke 失败只记 WARN，不改变 PASS/FAIL；check 依赖树实例与校验器，其他智能体改树期间可能如实报红。");
        lines.Add("");
        return string.Join("\n", lines) + "\n";
    }
}
