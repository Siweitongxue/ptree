using System.Text;
using System.Text.Json;
using PTree.Core;
using PTree.Mcp;

namespace PTree.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var (root, rest) = ExtractRoot(args);
            if (rest.Count == 0) { PrintUsage(); return 2; }
            var command = rest[0];
            var options = ParseOptions(rest.Skip(1).ToList());
            if (command == "mcp") return McpServer.Run(root);
            if (command == "init") return Init(root, options);
            if (command == "version")
            {
                WriteJson(JsonOutput.Serialize(new
                {
                    kernel = KernelInfo.Name,
                    version = KernelInfo.Version,
                    server = KernelInfo.ServerName,
                    assembly = typeof(Program).Assembly.GetName().Name,
                    path = Environment.ProcessPath,
                }));
                return 0;
            }
            // bench 必须先于仓库加载：它自己要把「进程启动 → 首次命令」算进冷启动，不能被提前的加载预热
            if (command == "bench") return Bench(root, options);
            var repository = PTreeRepository.Load(root);
            switch (command)
            {
                case "index":
                    WriteJson(JsonOutput.Serialize(repository.BuildIndex()));
                    return 0;
                case "read-node":
                {
                    if (options.Positionals.Count == 0) { Console.Error.WriteLine("ptree-cs: read-node 需要一个节点 id"); return 2; }
                    WriteText(NodeTools.ReadNode(root, JsonSerializer.SerializeToElement(new
                    {
                        id = options.Positionals[0],
                        depth = options.Depth ?? 1,
                        format = options.Format ?? "card",
                    })));
                    return 0;
                }
                case "status":
                    WriteText(NodeTools.Status(root, JsonSerializer.SerializeToElement(new { format = options.Format ?? "card" })));
                    return 0;
                case "map":
                    WriteText(NodeTools.Map(root, JsonSerializer.SerializeToElement(new { mode = options.Mode ?? "blocks" })));
                    return 0;
                case "guide":
                    WriteText(NodeTools.Guide(root));
                    return 0;
                case "links":
                    WriteText(NodeTools.Links(root, JsonSerializer.SerializeToElement(new { node = options.Node })));
                    return 0;
                case "group":
                {
                    if (options.Positionals.Count == 0) { Console.Error.WriteLine("ptree-cs: group 需要一个组 id，例如 group.tree-toolchain"); return 2; }
                    WriteText(NodeTools.ReadGroup(root, JsonSerializer.SerializeToElement(new
                    {
                        id = options.Positionals[0],
                        mode = options.Mode ?? "names",
                    })));
                    return 0;
                }
                case "node":
                {
                    if (options.Positionals.Count == 0) { Console.Error.WriteLine("ptree-cs: node 需要一个节点 id"); return 2; }
                    var id = options.Positionals[0];
                    if (repository.NodeMap.ContainsKey(id)) { WriteJson(JsonOutput.Serialize(repository.BuildNode(id))); return 0; }
                    var found = TreeStore.Load(repository.Root).FindEntry(id);
                    if (found is null) throw new PTreeException("节点不存在：" + id);
                    WriteJson(JsonOutput.Serialize(new { id = found.Value.Entry.Id, name = found.Value.Entry.Fields.Count > 1 ? found.Value.Entry.Fields[1] : "", source = found.Value.Document.RelPath, kind = found.Value.Document.Kind, fields = found.Value.Entry.Fields, line = found.Value.Entry.LineNo }));
                    return 0;
                }
                case "validate":
                {
                    var payload = Validator.Validate(repository);
                    if (options.Json) WriteJson(JsonOutput.Serialize(payload)); else PrintValidation(payload);
                    return payload.Ok ? 0 : 1;
                }
                case "stats":
                {
                    var stats = Reporting.BuildStats(repository);
                    if (options.Json) WriteJson(JsonOutput.Serialize(stats)); else WriteJson(JsonOutput.Serialize(stats));
                    return 0;
                }
                case "health":
                    return Health(repository, options);
                case "changes":
                    return Changes(repository, options);
                case "relocate":
                    return Relocate(repository, options);
                case "delete":
                    return DeleteNode(repository, options);
                case "check":
                {
                    var store = TreeStore.Load(repository.Root);
                    var issues = TreeChecker.Check(store);
                    issues.AddRange(ManifestChecker.Check(repository, store));
                    var errors = issues.Where(issue => issue.Level == "E").Select(issue => new { code = issue.Code, node = issue.Kind, message = issue.Message }).ToList();
                    var warnings = issues.Where(issue => issue.Level == "W").Select(issue => new { code = issue.Code, node = issue.Kind, message = issue.Message }).ToList();
                    var notes = issues.Where(issue => issue.Level == "I").Select(issue => new { code = issue.Code, node = issue.Kind, message = issue.Message }).ToList();
                    if (options.Json) WriteJson(JsonOutput.Serialize(new { ok = errors.Count == 0, errors, warnings, notes }));
                    else
                    {
                        foreach (var issue in issues) Console.WriteLine("[" + issue.Level + "] " + issue.Code + " " + issue.Kind + ": " + issue.Message);
                        Console.WriteLine("cross-tree check：E=" + errors.Count + " W=" + warnings.Count + " I=" + notes.Count);
                    }
                    return errors.Count == 0 ? 0 : 1;
                }
                case "orphans":
                {
                    var payload = Validator.Orphans(repository);
                    if (options.Json) WriteJson(JsonOutput.Serialize(payload)); else PrintIssuesOnly(payload);
                    return payload.Ok ? 0 : 1;
                }
                case "view": return Generate(repository, GeneratedProducts.ViewFile, GeneratedProducts.RenderView, options.Json);
                case "order": return Generate(repository, GeneratedProducts.OrderFile, GeneratedProducts.RenderOrder, options.Json);
                case "book": return Generate(repository, GeneratedProducts.BookFile, GeneratedProducts.RenderBook, options.Json);
                case "brief": return Brief(repository, options.Json);
                case "benchmark": return Benchmark(repository, options);
                case "log": return Log(repository, options);
                case "tree": return Tree(repository, options);
                case "apply": return ApplyDryRun(repository, options);
                case "check-plan": return CheckPlan(repository, options);
                case "verify": return Verify(repository, options.Json);
                case "adapt":
                {
                    var payload = AdaptScanner.Scan(repository);
                    WriteJson(JsonOutput.Serialize(payload));
                    return payload.TryGetValue("ok", out var ok) && ok is true ? 0 : 1;
                }
                case "backup": return Backup(repository, options);
                case "git-sync":
                {
                    var files = options.Files.Count > 0 ? options.Files : options.Positionals;
                    WriteJson(NodeTools.GitSync(repository.Root, files, options.Message));
                    return 0;
                }
                default:
                    Console.Error.WriteLine("ptree-cs: 未知命令：" + command);
                    PrintUsage();
                    return 2;
            }
        }
        catch (PTreeException exception) { Console.Error.WriteLine("ptree-cs: " + exception.Message); return 2; }
        catch (Exception exception) { Console.Error.WriteLine("ptree-cs: 未预期错误：" + exception.Message); return 2; }
    }

    private sealed class Options
    {
        public List<string> Positionals { get; } = new();
        public bool Json { get; set; }
        public string? Node { get; set; }
        public int Limit { get; set; } = 20;
        public string? File { get; set; }
        public int? Depth { get; set; }
        public int? Days { get; set; }
        public string? Format { get; set; }
        public string? Mode { get; set; }
        public string? Name { get; set; }
        public string? Message { get; set; }
        public string? Reason { get; set; }
        public string? Parent { get; set; }
        public List<string> Files { get; } = new();
        public bool Write { get; set; }
        public bool Force { get; set; }
        public bool? EnabledFlag { get; set; }
        public int? Retention { get; set; }
        public List<string> AddTargets { get; } = new();
        public List<string> RemoveTargets { get; } = new();
        public string? Username { get; set; }
        public string? Token { get; set; }
        public string? Target { get; set; }
        public bool? SecretScanFlag { get; set; }
        public int? Repeat { get; set; }
    }

    private static Options ParseOptions(List<string> tokens)
    {
        var options = new Options();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            switch (token)
            {
                case "--json": options.Json = true; break;
                case "--node": if (index + 1 < tokens.Count) options.Node = tokens[++index]; break;
                case "--limit": if (index + 1 < tokens.Count && int.TryParse(tokens[++index], out var limit)) options.Limit = limit; break;
                case "--file": if (index + 1 < tokens.Count) options.File = tokens[++index]; break;
                case "--files": if (index + 1 < tokens.Count) options.Files.AddRange(tokens[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--message": if (index + 1 < tokens.Count) options.Message = tokens[++index]; break;
                case "--reason": if (index + 1 < tokens.Count) options.Reason = tokens[++index]; break;
                case "--parent": if (index + 1 < tokens.Count) options.Parent = tokens[++index]; break;
                case "--name": if (index + 1 < tokens.Count) options.Name = tokens[++index]; break;
                case "--depth": if (index + 1 < tokens.Count && int.TryParse(tokens[++index], out var depth)) options.Depth = depth; break;
                case "--days": if (index + 1 < tokens.Count && int.TryParse(tokens[++index], out var days)) options.Days = days; break;
                case "--format": if (index + 1 < tokens.Count) options.Format = tokens[++index]; break;
                case "--mode": if (index + 1 < tokens.Count) options.Mode = tokens[++index]; break;
                case "--write": options.Write = true; break;
                case "--force": options.Force = true; break;
                case "--enable": options.EnabledFlag = true; break;
                case "--disable": options.EnabledFlag = false; break;
                case "--enabled": if (index + 1 < tokens.Count && bool.TryParse(tokens[++index], out var enabledFlag)) options.EnabledFlag = enabledFlag; break;
                case "--retention": if (index + 1 < tokens.Count && int.TryParse(tokens[++index], out var retentionValue)) options.Retention = retentionValue; break;
                case "--add-target": if (index + 1 < tokens.Count) options.AddTargets.Add(tokens[++index]); break;
                case "--remove-target": if (index + 1 < tokens.Count) options.RemoveTargets.Add(tokens[++index]); break;
                case "--username": if (index + 1 < tokens.Count) options.Username = tokens[++index]; break;
                case "--token": if (index + 1 < tokens.Count) options.Token = tokens[++index]; break;
                case "--target": if (index + 1 < tokens.Count) options.Target = tokens[++index]; break;
                case "--secret-scan": if (index + 1 < tokens.Count && bool.TryParse(tokens[++index], out var secretScan)) options.SecretScanFlag = secretScan; break;
                case "--no-secret-scan": options.SecretScanFlag = false; break;
                case "--repeat": if (index + 1 < tokens.Count && int.TryParse(tokens[++index], out var repeat)) options.Repeat = repeat; break;
                case "--include-trimmed": options.Write = true; break;
                case "--dry-run": break;
                default: options.Positionals.Add(token); break;
            }
        }
        return options;
    }

    private static (string?, List<string>) ExtractRoot(string[] args)
    {
        string? root = null;
        var rest = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (token is "--root" or "-r")
            {
                if (index + 1 >= args.Length) throw new PTreeException(token + " 缺少参数");
                root = args[++index];
            }
            else if (token.StartsWith("--root=", StringComparison.Ordinal)) root = token["--root=".Length..];
            else rest.Add(token);
        }
        return (root, rest);
    }

    private static void WriteJson(string json) => WriteText(json);

    private static void WriteText(string text)
    {
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        writer.Write(text);
        writer.Write('\n');
    }

    private static void PrintIssuesOnly(ValidationPayload payload)
    {
        foreach (var issue in payload.Errors.Concat(payload.Warnings).Concat(payload.Notes))
        {
            var level = payload.Errors.Contains(issue) ? "E" : payload.Warnings.Contains(issue) ? "W" : "I";
            Console.WriteLine("[" + level + "] " + issue.Code + (issue.Node is null ? "" : " nodes." + issue.Node + ":") + " " + issue.Message);
        }
    }

    private static void PrintValidation(ValidationPayload payload)
    {
        PrintIssuesOnly(payload);
        Console.WriteLine("校验完成：E=" + payload.Errors.Count + " W=" + payload.Warnings.Count + " I=" + payload.Notes.Count);
    }

    private static int Health(PTreeRepository repository, Options options)
    {
        var report = HealthCheck.Run(repository);
        if (options.Json) WriteJson(JsonOutput.Serialize(HealthCheck.ToPayload(report)));
        else WriteText(HealthCheck.RenderText(report));
        return 0;
    }

    private static int Changes(PTreeRepository repository, Options options)
    {
        var changes = HealthCheck.ReadChanges(repository.Root, options.Node, options.Days ?? 30, options.Limit);
        if (options.Json)
            WriteJson(JsonOutput.Serialize(new
            {
                count = changes.Count,
                changes = changes.Select(change => new
                {
                    at = change.At,
                    node = change.Node,
                    file = change.File,
                    mtime = change.Mtime,
                    late_days = change.LateDays,
                }),
            }));
        else WriteText(HealthCheck.RenderChanges(changes));
        return 0;
    }

    private static int Relocate(PTreeRepository repository, Options options)
    {
        if (options.Positionals.Count == 0) { Console.Error.WriteLine("ptree-cs: relocate 需要一个节点 id"); return 2; }
        var payload = NodeTools.Relocate(repository.Root, JsonSerializer.SerializeToElement(new
        {
            id = options.Positionals[0],
            files = options.Files,
            parent = options.Parent,
            reason = options.Reason,
        }));
        WriteJson(payload);
        return OkOf(payload) ? 0 : 1;
    }

    private static int DeleteNode(PTreeRepository repository, Options options)
    {
        if (options.Positionals.Count == 0) { Console.Error.WriteLine("ptree-cs: delete 需要一个节点 id"); return 2; }
        var payload = NodeTools.Delete(repository.Root, JsonSerializer.SerializeToElement(new
        {
            id = options.Positionals[0],
            reason = options.Reason,
        }));
        WriteJson(payload);
        return OkOf(payload) ? 0 : 1;
    }

    private static bool OkOf(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int Brief(PTreeRepository repository, bool json)
    {
        var content = BriefRenderer.Render(repository);
        var directory = Path.Combine(repository.Root, GeneratedProducts.GeneratedDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, BriefRenderer.FileName);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        var relative = Path.GetRelativePath(repository.Root, path).Replace('\\', '/');
        var lineCount = content.Split('\n').Length - 1;
        var byteCount = new UTF8Encoding(false).GetByteCount(content);
        if (json)
        {
            WriteJson(JsonOutput.Serialize(new
            {
                path = relative,
                content,
                line_count = lineCount,
                byte_count = byteCount,
                tree_version = repository.Manifest.TreeVersion,
                limits = new { max_lines = BriefRenderer.MaxLines, max_bytes = BriefRenderer.MaxBytes },
            }));
        }
        else
        {
            Console.WriteLine("已生成：" + relative + "（" + lineCount + " 行 / " + byteCount + " 字节，上限 " +
                              BriefRenderer.MaxLines + " 行 / " + BriefRenderer.MaxBytes + " 字节）");
        }
        return 0;
    }

    private static int Init(string? root, Options options)
    {
        var plan = InitRunner.Plan(root, options.Name, options.Force);
        if (plan.Refused)
        {
            Console.Error.WriteLine(plan.Message);
            return 1;
        }
        if (plan.Existing.Count > 0)
        {
            Console.WriteLine("以下文件已存在，将被覆盖：" + plan.Existing.Count + " 个");
            foreach (var relative in plan.Existing) Console.WriteLine("  [覆盖] " + relative);
        }
        if (options.Force) Console.WriteLine("--force 已启用：目标清单若存在将被重建。");
        var result = InitRunner.Write(plan);
        Console.WriteLine("已生成：" + result.Files.Count + " 个文件（根：" + result.Root + "）");
        Console.WriteLine("项目名：" + result.Name + "；节点：demo / core / core.app；阶段：P0");
        Console.WriteLine("自检：validate E=" + result.ValidationErrors + " W=" + result.ValidationWarnings +
                          "；check E=" + result.CheckErrors + " W=" + result.CheckWarnings);
        foreach (var issue in result.Issues) Console.WriteLine(issue);
        Console.WriteLine(result.Message);
        Console.WriteLine("下一步：ptree-cs validate --root \"" + result.Root + "\"；ptree-cs check --root \"" + result.Root + "\"；ptree-cs book --root \"" + result.Root + "\"");
        return result.Ok ? 0 : 1;
    }

    private static int Verify(PTreeRepository repository, bool json)
    {
        var result = VerifyRunner.Run(repository);
        var directory = Path.Combine(repository.Root, "00-程序树", "运维");
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "验证报告.txt");
        var relative = Path.GetRelativePath(repository.Root, reportPath).Replace('\\', '/');
        var report = VerifyRunner.RenderReport(result, relative);
        File.WriteAllText(reportPath, report, new UTF8Encoding(false));
        if (json) WriteJson(JsonOutput.Serialize(result)); else Console.Write(report);
        return result.ExitCode;
    }

    private static int Generate(PTreeRepository repository, string filename, Func<PTreeRepository, ValidationPayload, string> renderer, bool json)
    {
        var payload = Validator.Validate(repository);
        var content = renderer(repository, payload);
        var path = WriteGenerated(repository, filename, content);
        if (json)
        {
            WriteJson(JsonOutput.Serialize(new { ok = payload.Ok, errors = payload.Errors, warnings = payload.Warnings, notes = payload.Notes, path, content }));
        }
        else
        {
            Console.WriteLine("已生成：" + path + "（校验状态：" + (payload.Ok ? "通过（E=0，W=" + payload.Warnings.Count + "）" : "未通过校验（E=" + payload.Errors.Count + "，W=" + payload.Warnings.Count + "）") + "）");
        }
        return payload.Ok ? 0 : 1;
    }

    private static string WriteGenerated(PTreeRepository repository, string filename, string content)
    {
        var directory = Path.Combine(repository.Root, GeneratedProducts.GeneratedDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, filename);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return Path.GetRelativePath(repository.Root, path).Replace('\\', '/');
    }

    private static int Benchmark(PTreeRepository repository, Options options)
    {
        if (options.Node is not null)
        {
            var content = GeneratedProducts.RenderBenchmarkNode(repository, options.Node);
            if (options.Json)
            {
                var bindings = repository.Bindings.Bindings.Where(binding => binding.Node == options.Node).Select(binding => new
                {
                    benchmark = binding.Benchmark,
                    level = binding.Level,
                    target = binding.Target,
                    gaps = binding.Gaps.Select(gap => new { dimension = gap.Dimension, gap = gap.Gap, next = gap.Next, opened = gap.Opened }),
                }).ToList();
                WriteJson(JsonOutput.Serialize(new { node = options.Node, benchmarks = bindings, content }));
            }
            else Console.Write(content);
            return 0;
        }
        var payload = Validator.Validate(repository);
        var board = GeneratedProducts.RenderBenchmark(repository, payload);
        var path = WriteGenerated(repository, GeneratedProducts.BenchmarkFile, board);
        if (options.Json)
        {
            WriteJson(JsonOutput.Serialize(new
            {
                path,
                content = board,
                tree_version = repository.Manifest.TreeVersion,
                benchmark_count = repository.Benchmarks.Benchmarks.Count,
                binding_count = repository.Bindings.Bindings.Count,
                benchmarks = repository.Benchmarks.Benchmarks.Select(benchmark => new
                {
                    id = benchmark.Id,
                    name = benchmark.Name,
                    kind = benchmark.Kind,
                    reference = benchmark.Reference,
                    levels = benchmark.Levels.Select(level => new { level = level.Level, name = level.Name, criteria = level.Criteria, evidence = level.Evidence }),
                }).ToList(),
                bindings = repository.Bindings.Bindings.Select(binding => new
                {
                    node = binding.Node,
                    benchmark = binding.Benchmark,
                    level = binding.Level,
                    target = binding.Target,
                    gaps = binding.Gaps.Select(gap => new { dimension = gap.Dimension, gap = gap.Gap, next = gap.Next, opened = gap.Opened }),
                    next = binding.Gaps.Count > 0 ? binding.Gaps[0].Next : null,
                }).ToList(),
            }));
        }
        else
        {
            Console.WriteLine("已生成：" + path + "（校验状态：" + (payload.Ok ? "通过（E=0，W=" + payload.Warnings.Count + "）" : "未通过校验（E=" + payload.Errors.Count + "，W=" + payload.Warnings.Count + "）") + "）");
            Console.WriteLine("标杆 " + repository.Benchmarks.Benchmarks.Count + " 条；绑定 " + repository.Bindings.Bindings.Count + " 条；问题 E=" + payload.Errors.Count + " W=" + payload.Warnings.Count);
        }
        return payload.Ok ? 0 : 1;
    }

    private static int Log(PTreeRepository repository, Options options)
    {
        var entries = Reporting.ReadLedger(repository);
        if (options.Node is not null)
            entries = entries.Where(entry => entry.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Object && nodes.TryGetProperty(options.Node, out _)).ToList();
        entries = Enumerable.Reverse(entries).Take(options.Limit > 0 ? options.Limit : 20).ToList();
        if (options.Json)
            WriteJson(JsonOutput.Serialize(new { ok = true, entries, count = entries.Count, filter_node = options.Node, limit = options.Limit }));
        else
        {
            foreach (var entry in entries) { Console.WriteLine(entry.GetRawText()); Console.WriteLine(); }
            if (entries.Count == 0) Console.WriteLine("（台账无记录）");
        }
        return 0;
    }

    private static int Tree(PTreeRepository repository, Options options)
    {
        var store = TreeStore.Load(repository.Root);
        if (options.Json)
        {
            WriteJson(JsonOutput.Serialize(new
            {
                trees = store.Documents.Select(document => new
                {
                    kind = document.Kind,
                    title = document.Title,
                    version = document.Version,
                    path = document.RelPath,
                    fields = document.FieldNames,
                    entries = document.Entries.Select(entry => new { id = entry.Id, fields = entry.Fields, line = entry.LineNo }),
                }),
                references = store.References.Select(reference => new { from_kind = reference.FromKind, from = reference.FromId, target_kind = reference.TargetKind, target = reference.TargetId, field = reference.FieldName, line = reference.LineNo }),
                groups = repository.Manifest.Groups.Select(group => new { id = group.Id, name = group.Name, description = group.Description, parent = group.Parent, members = group.Members }),
                node_groups = repository.Nodes.Select(node => new { id = node.Id, groups = repository.GroupsForNode(node.Id ?? "") }),
                program_status = store.Documents.Where(document => document.Kind == "程序树")
                    .SelectMany(document => document.Entries)
                    .Select(entry => new { id = entry.Id, status = entry.Fields.Count > 3 ? entry.Fields[3] : "", mapped = TreeChecker.MapProgramStatus(entry.Fields.Count > 3 ? entry.Fields[3] : "") }),
                parse_errors = store.ParseIssues.Select(issue => new { code = issue.Code, kind = issue.Kind, message = issue.Message, line = issue.Line }),
            }));
        }
        else
        {
            foreach (var document in store.Documents)
            {
                Console.WriteLine(document.Title + "（版本 " + document.Version + "，条目 " + document.Entries.Count + "）：" + document.RelPath);
                foreach (var entry in document.Entries) Console.WriteLine("  " + entry.Id + " ｜ " + string.Join(" ｜ ", entry.Fields.Skip(1)));
            }
            if (store.ParseIssues.Count > 0) Console.WriteLine("解析问题：" + store.ParseIssues.Count);
        }
        return 0;
    }

    private static int ApplyDryRun(PTreeRepository repository, Options options)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.File))
        {
            var path = Path.IsPathRooted(options.File) ? options.File : Path.Combine(repository.Root, options.File);
            if (!File.Exists(path)) { Console.Error.WriteLine("ptree-cs: 提案文件不存在：" + options.File); return 2; }
            paths.Add(path);
        }
        else
        {
            var directory = Path.Combine(repository.Root, "00-程序树", "变更");
            if (Directory.Exists(directory))
                paths.AddRange(Directory.GetFiles(directory, "*.yaml").Concat(Directory.GetFiles(directory, "*.yml")).OrderBy(path => path, StringComparer.Ordinal));
        }
        if (paths.Count == 0) { Console.Error.WriteLine("ptree-cs: 没有待处理提案"); return 0; }
        var result = ProposalEngine.Apply(repository, paths, options.Write, !options.Write, repo => { GeneratedProducts.WriteAll(repo); return "ok"; });
        var payload = new
        {
            ok = result.Ok,
            dry_run = result.DryRun,
            written = result.Written,
            proposals = result.Proposals,
            errors = result.Errors,
            warnings = result.Warnings,
            notes = result.Notes,
            summary = result.Summary,
            ledger_id = result.LedgerId,
            written_files = result.WrittenFiles,
            error = result.Error,
        };
        if (options.Json) WriteJson(JsonOutput.Serialize(payload));
        else
        {
            foreach (var issue in result.Errors) Console.WriteLine("[E] " + issue.Code + " " + (issue.Node ?? "") + " " + issue.Message);
            foreach (var issue in result.Warnings) Console.WriteLine("[W] " + issue.Code + " " + (issue.Node ?? "") + " " + issue.Message);
            foreach (var line in result.Summary) Console.WriteLine("ops：" + line);
            Console.WriteLine("结果：" + (result.Ok ? (result.Written ? "已落地" : "可落地") : "被拒绝") +
                "（E=" + result.Errors.Count + "，W=" + result.Warnings.Count + "）");
        }
        return result.Ok ? 0 : 1;
    }

    private static int CheckPlan(PTreeRepository repository, Options options)
    {
        var file = options.File ?? options.Positionals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file)) { Console.Error.WriteLine("ptree-cs: check-plan 需要 --node <节点片段.yaml>"); return 2; }
        var path = Path.IsPathRooted(file) ? file : Path.Combine(repository.Root, file);
        if (!File.Exists(path)) { Console.Error.WriteLine("ptree-cs: 文件不存在：" + file); return 2; }
        var evaluation = ProposalEngine.CheckPlan(repository, path);
        var payload = new
        {
            ok = evaluation.Ok,
            dry_run = true,
            node = file,
            errors = evaluation.Errors,
            warnings = evaluation.Warnings,
            notes = evaluation.Notes,
            summary = evaluation.Summary,
        };
        if (options.Json) WriteJson(JsonOutput.Serialize(payload));
        else
        {
            foreach (var issue in evaluation.Issues) Console.WriteLine("[" + issue.Level + "] " + issue.Code + " " + (issue.Node ?? "") + " " + issue.Message);
            Console.WriteLine("结果：" + (evaluation.Ok ? "可并入" : "不可并入"));
        }
        return evaluation.Ok ? 0 : 1;
    }

    /// <summary>backup 子命令：status / config / setup / now / list / verify（与 MCP 的 tree_backup_* 同一实现）。</summary>
    private static int Backup(PTreeRepository repository, Options options)
    {
        var sub = options.Positionals.Count > 0 ? options.Positionals[0].ToLowerInvariant() : "status";
        var args = new Dictionary<string, object?>();
        if (options.EnabledFlag is not null) args["enabled"] = options.EnabledFlag.Value;
        if (options.Mode is not null) args["mode"] = options.Mode;
        if (options.Retention is not null) args["retention"] = options.Retention.Value;
        if (options.AddTargets.Count > 0) args["add_targets"] = options.AddTargets;
        if (options.RemoveTargets.Count > 0) args["remove_targets"] = options.RemoveTargets;
        if (options.Username is not null) args["username"] = options.Username;
        if (options.Token is not null) args["token"] = options.Token;
        if (options.Target is not null) args["target"] = options.Target;
        if (options.SecretScanFlag is not null) args["secret_scan"] = options.SecretScanFlag.Value;
        if (options.Limit > 0) args["limit"] = options.Limit;
        if (options.Write) args["include_trimmed"] = true;
        var arguments = args.Count == 0 ? (JsonElement?)null : JsonSerializer.SerializeToElement(args);
        string? text = sub switch
        {
            "status" => BackupService.Status(repository.Root, arguments),
            "config" => BackupService.Config(repository.Root, arguments),
            "setup" => BackupService.Setup(repository.Root, arguments),
            "now" => BackupService.Now(repository.Root, arguments),
            "list" => BackupService.List(repository.Root, arguments),
            "verify" => BackupService.Verify(repository.Root, arguments),
            "scan" => BackupService.Scan(repository.Root, arguments),
            _ => null,
        };
        if (text is null)
        {
            Console.Error.WriteLine("ptree-cs: backup 子命令只能是 status / config / setup / now / list / verify / scan");
            return 2;
        }
        WriteText(text);
        return OkOf(text) ? 0 : 1;
    }

    /// <summary>bench：进程内测命令耗时（不含进程启动）+ 冷启动自测；不开子进程、不起后台。</summary>
    private static int Bench(string? root, Options options)
    {
        var report = BenchRunner.Run(root, options.Repeat ?? BenchRunner.DefaultIterations, options.Node);
        report.Kernel = KernelInfo.Name;
        report.Version = KernelInfo.Version;
        if (options.Json)
        {
            WriteJson(JsonOutput.Serialize(new
            {
                ok = true,
                kernel = report.Kernel,
                version = report.Version,
                build = report.Build,
                root = report.Root,
                node = report.Node,
                iterations = report.Iterations,
                processors = report.Processors,
                framework = report.Framework,
                process_start_ms = report.ProcessStartMs,
                cold_start_ms = report.ColdStartMs,
                items = report.Items.Select(item => new
                {
                    name = item.Name,
                    command = item.Command,
                    iterations = item.Iterations,
                    first_ms = item.FirstMs,
                    min_ms = item.MinMs,
                    median_ms = item.MedianMs,
                    p95_ms = item.P95Ms,
                }).ToList(),
            }));
        }
        else
        {
            WriteText(BenchRunner.RenderText(report));
        }
        return 0;
    }

    private static void PrintUsage() => Console.Error.WriteLine("用法：ptree-cs [--root <仓库根>] <index|node <id>|read-node <id>|status|map [--mode blocks|names|summary]|guide|links [--node <id>]|group <组id> [--mode names|summary]|bench [--repeat N] [--node <id>]|health|changes|validate|check|stats|orphans|view|order|book|brief|benchmark|log|tree|relocate <id> --files ...|delete <id> --reason ...|adapt|git-sync|backup status|config|setup|now|list|verify|scan|apply|check-plan|verify|init|version|mcp> [--json]");
}
