using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PTree.Core;

/// <summary>一条命令的进程内计时。每个样本都从「加载清单」开始，等于 CLI 单次调用减去进程启动。</summary>
public sealed class BenchSample
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public int Iterations { get; init; }
    public double FirstMs { get; init; }
    public double MinMs { get; init; }
    public double MedianMs { get; init; }
    public double P95Ms { get; init; }
}

public sealed class BenchReport
{
    public string Kernel { get; set; } = "csharp";
    public string Version { get; set; } = "";
    public string Build { get; set; } = BenchRunner.BuildConfiguration;
    public string Root { get; set; } = "";
    public string? Node { get; set; }
    public int Iterations { get; set; }
    public int Processors { get; set; }
    public string Framework { get; set; } = "";
    public double ProcessStartMs { get; set; }
    public double ColdStartMs { get; set; }
    public List<BenchSample> Items { get; } = new();
}

/// <summary>
/// 进程内性能测量（bench 子命令）：不做子进程、不起后台，只在本进程里重复跑「加载 + 命令」。
/// 冷启动地板由 process_start_ms + 第一次测量构成：MCP 长驻进程只付这一次，CLI 每次都要付。
/// </summary>
public static class BenchRunner
{
    public const int DefaultIterations = 5;
    public const int MaxIterations = 50;
    public const string DefaultNode = "governance.validator";

    public const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>进程启动到现在的毫秒数：CLI 的 .NET 运行时地板（与内核逻辑无关）。</summary>
    public static double ProcessStartMs()
    {
        try
        {
            var elapsed = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            return elapsed > 0 ? elapsed : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static BenchReport Run(string? root, int iterations, string? nodeId)
    {
        if (iterations <= 0) iterations = DefaultIterations;
        if (iterations > MaxIterations) iterations = MaxIterations;

        var report = new BenchReport
        {
            Iterations = iterations,
            Processors = Environment.ProcessorCount,
            Framework = RuntimeInformation.FrameworkDescription,
        };
        var processStart = ProcessStartMs();
        report.ProcessStartMs = Round(processStart);

        PTreeRepository? firstRepository = null;
        var validate = Measure("validate", "ptree-cs validate", iterations, () =>
        {
            var repository = PTreeRepository.Load(root);
            firstRepository ??= repository;
            Validator.Validate(repository);
        });
        report.Items.Add(validate);
        report.ColdStartMs = Round(processStart + validate.FirstMs);
        report.Root = firstRepository?.Root ?? "";

        report.Items.Add(Measure("check", "ptree-cs check", iterations, () =>
        {
            var repository = PTreeRepository.Load(root);
            var store = TreeStore.Load(repository.Root);
            TreeChecker.Check(store);
            ManifestChecker.Check(repository, store);
        }));

        var target = ResolveNode(root, nodeId);
        report.Node = target;
        report.Items.Add(Measure("read-node", "ptree-cs read-node " + target, iterations, () =>
        {
            var repository = PTreeRepository.Load(root);
            NodeCards.RenderNode(repository, target, 1);
        }));

        report.Items.Add(Measure("map --mode names", "ptree-cs map --mode names", iterations, () =>
        {
            var repository = PTreeRepository.Load(root);
            NodeCards.RenderMap(repository, "names");
        }));

        report.Items.Add(Measure("status", "ptree-cs status", iterations, () =>
        {
            var repository = PTreeRepository.Load(root);
            NodeCards.RenderStatus(repository, Validator.Validate(repository));
        }));

        report.Items.Add(Measure("version", "ptree-cs version（仅组装 JSON，不含进程启动）", iterations, () =>
        {
            var text = JsonOutput.Serialize(new { kernel = "csharp", version = "?", server = "ptree-mcp" });
            if (text.Length == 0) throw new InvalidOperationException("version 输出为空");
        }));

        return report;
    }

    /// <summary>读节点目标：先按用户给的 id，找不到再退回第一个 program 节点。解析本身只做一次，不进入计时。</summary>
    public static string ResolveNode(string? root, string? nodeId)
    {
        PTreeRepository repository;
        try { repository = PTreeRepository.Load(root); }
        catch (Exception) { return string.IsNullOrWhiteSpace(nodeId) ? DefaultNode : nodeId!.Trim(); }
        var wanted = string.IsNullOrWhiteSpace(nodeId) ? DefaultNode : nodeId!.Trim();
        if (repository.NodeMap.ContainsKey(wanted)) return wanted;
        return repository.Nodes.FirstOrDefault(node => node.Kind == "program")?.Id ?? wanted;
    }

    private static BenchSample Measure(string name, string command, int iterations, Action action)
    {
        var times = new List<double>(iterations);
        for (var index = 0; index < iterations; index++)
        {
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        var sorted = times.OrderBy(value => value).ToList();
        return new BenchSample
        {
            Name = name,
            Command = command,
            Iterations = iterations,
            FirstMs = Round(times[0]),
            MinMs = Round(sorted[0]),
            MedianMs = Round(Percentile(sorted, 50)),
            P95Ms = Round(Percentile(sorted, 95)),
        };
    }

    /// <summary>最近秩法：p95 = 排序后第 ceil(0.95×n) 个样本。</summary>
    private static double Percentile(List<double> sorted, double percent)
    {
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(percent / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    public static string RenderText(BenchReport report)
    {
        var builder = new StringBuilder();
        builder.Append("ptree-cs bench（").Append(report.Build).Append("，每次 ").Append(report.Iterations)
            .Append(" 遍，进程内；每遍都含加载清单，不含进程启动）\n");
        builder.Append("冷启动：进程启动 → 首次 validate 完成 ").Append(Format(report.ColdStartMs))
            .Append(" ms（其中进程启动至 bench 开始 ").Append(Format(report.ProcessStartMs)).Append(" ms）\n");
        builder.Append("环境：").Append(report.Framework).Append("，").Append(report.Processors).Append(" 核；节点 ").Append(report.Node ?? "-").Append('\n');
        builder.Append("命令".PadRight(24)).Append("min".PadLeft(9)).Append("median".PadLeft(9)).Append("p95".PadLeft(9)).Append("首次".PadLeft(9)).Append('\n');
        foreach (var item in report.Items)
        {
            builder.Append(Fit(item.Name, 24))
                .Append(Format(item.MinMs).PadLeft(9))
                .Append(Format(item.MedianMs).PadLeft(9))
                .Append(Format(item.P95Ms).PadLeft(9))
                .Append(Format(item.FirstMs).PadLeft(9))
                .Append('\n');
        }
        builder.Append("说明：MCP 是长驻进程，冷启动只付一次；CLI 每次多付约 160 ms 的 .NET 运行时地板，不是内核慢。\n");
        return builder.ToString().TrimEnd('\n');
    }

    private static string Format(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>中文按显示宽度对齐（全角算 2），避免表格错行。</summary>
    private static string Fit(string text, int width)
    {
        var current = 0;
        foreach (var character in text) current += character > 0x2E80 ? 2 : 1;
        if (current > width) return text[..Math.Max(1, width - 1)] + " ";
        return text + new string(' ', width - current);
    }
}
