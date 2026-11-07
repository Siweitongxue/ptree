using System.Globalization;
using System.Text;

namespace PTree.Core;

/// <summary>init 计划：先算文件与内容，CLI 打印覆盖清单后再 Write；Write 后自跑 validate/check。</summary>
public sealed class InitPlan
{
    public string Root { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Refused { get; set; }
    public string Message { get; set; } = "";
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public List<string> Existing { get; } = new();
}

public sealed class InitResult
{
    public bool Ok { get; set; }
    public string Root { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Files { get; } = new();
    public List<string> Existing { get; } = new();
    public int ValidationErrors { get; set; }
    public int ValidationWarnings { get; set; }
    public int CheckErrors { get; set; }
    public int CheckWarnings { get; set; }
    public List<string> Issues { get; } = new();
    public string Message { get; set; } = "";
}

/// <summary>最小可校验工程脚手架：清单 / 契约 / 标杆 / 四棵树实例 / 节点说明 / 空目录。</summary>
public static class InitRunner
{
    private static readonly string[] EmptyDirs =
    {
        "00-程序树/变更", "00-程序树/生成", "00-程序树/运维",
    };

    public static InitPlan Plan(string? rootArg, string? name, bool force)
    {
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(rootArg) ? Environment.CurrentDirectory : rootArg);
        var plan = new InitPlan { Root = root };
        var manifestPath = Path.Combine(root, "00-程序树", "program-tree.yaml");
        if (File.Exists(manifestPath) && !force)
        {
            plan.Refused = true;
            plan.Message = "目标已存在清单：" + manifestPath + "。未覆盖任何文件；确需重建请加 --force（会先打印覆盖清单）。";
            return plan;
        }
        var projectName = string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(root).Name : name.Trim();
        if (projectName.Length == 0) projectName = "新项目";
        plan.Name = projectName;
        var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var introDemo = Intro(projectName, "根节点", "demo", "整棵程序树的边界与目录约定", "根节点只管树与边界，不承接具体业务；它负责保证程序、契约、说明与四棵树实例之间的连接一致。");
        var introCore = Intro(projectName, "核心部门", "core", "项目的第一条主流程", "部门负责把目标拆成可独立验收的程序，并守住部门边界；它不直接写业务实现，也不替程序决定内部结构。");
        var introApp = Intro(projectName, "起步程序", "core.app", "第一条可运行的主流程", "程序负责把输入契约加工成输出契约，并维护自己的 SPEC、说明与验收；它不承担部门治理，也不越权改其他节点。");

        plan.Files["00-程序树/program-tree.yaml"] = Manifest(projectName, date);
        plan.Files["00-程序树/契约/contracts.yaml"] = Contracts(date);
        plan.Files["00-程序树/标杆/benchmarks.yaml"] = Benchmarks(date);
        plan.Files["00-程序树/标杆/绑定.yaml"] = Bindings(date);
        plan.Files["00-程序树/树实例/程序树.txt"] = ProgramTreeInstance(projectName, date);
        plan.Files["00-程序树/树实例/策划树.txt"] = PlanningTreeInstance(projectName, date);
        plan.Files["00-程序树/树实例/美术树.txt"] = ArtTreeInstance(projectName, date);
        plan.Files["00-程序树/树实例/UI树.txt"] = UiTreeInstance(projectName, date);
        plan.Files["00-程序树/说明/demo.txt"] = introDemo;
        plan.Files["00-程序树/说明/core.txt"] = introCore;
        plan.Files["00-程序树/说明/core.app.txt"] = introApp;
        plan.Files["00-程序树/README.txt"] = Readme(projectName, date);
        foreach (var relative in plan.Files.Keys)
        {
            if (File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))))
            {
                plan.Existing.Add(relative);
            }
        }
        return plan;
    }

    public static InitResult Write(InitPlan plan)
    {
        var result = new InitResult { Root = plan.Root, Name = plan.Name };
        result.Files.AddRange(plan.Files.Keys.OrderBy(path => path, StringComparer.Ordinal));
        result.Existing.AddRange(plan.Existing);
        foreach (var relative in result.Files)
        {
            var path = Path.Combine(plan.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, plan.Files[relative], new UTF8Encoding(false));
        }
        foreach (var relative in EmptyDirs)
        {
            Directory.CreateDirectory(Path.Combine(plan.Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        try
        {
            var repository = PTreeRepository.Load(plan.Root);
            var validation = Validator.Validate(repository);
            result.ValidationErrors = validation.Errors.Count;
            result.ValidationWarnings = validation.Warnings.Count;
            foreach (var issue in validation.Errors.Take(20)) result.Issues.Add("[E] " + issue.Code + " " + (issue.Node ?? "-") + ": " + issue.Message);
            foreach (var issue in validation.Warnings.Take(20)) result.Issues.Add("[W] " + issue.Code + " " + (issue.Node ?? "-") + ": " + issue.Message);
            var store = TreeStore.Load(plan.Root);
            var treeIssues = TreeChecker.Check(store);
            treeIssues.AddRange(ManifestChecker.Check(repository, store));
            result.CheckErrors = treeIssues.Count(issue => issue.Level == "E");
            result.CheckWarnings = treeIssues.Count(issue => issue.Level == "W");
            foreach (var issue in treeIssues.Where(issue => issue.Level == "E").Take(20))
            {
                result.Issues.Add("[E] " + issue.Code + " " + issue.Kind + ": " + issue.Message);
            }
            foreach (var issue in treeIssues.Where(issue => issue.Level == "W").Take(20))
            {
                result.Issues.Add("[W] " + issue.Code + " " + issue.Kind + ": " + issue.Message);
            }
            result.Ok = result.ValidationErrors == 0 && result.CheckErrors == 0;
            result.Message = result.Ok
                ? "脚手架生成完成：validate E=0、check E=0"
                : "脚手架已生成，但自检未通过：validate E=" + result.ValidationErrors + "，check E=" + result.CheckErrors;
        }
        catch (Exception exception)
        {
            result.Ok = false;
            result.Message = "脚手架已写入，但自检失败：" + exception.Message;
            result.Issues.Add(exception.Message);
        }
        return result;
    }

    private static string Q(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Intro(string project, string role, string id, string duty, string boundary)
    {
        return "这是 " + project + " 的起步脚手架节点 " + id + "（" + role + "）的说明。它由 ptree-cs init 生成，是一份可以直接改的起点：先把它改成真实职责，再补代码与验收。" + Environment.NewLine +
               "定位：" + duty + "。边界：" + boundary + Environment.NewLine +
               "代码放哪：程序节点的代码统一放 10-程序/ 下与节点 id 对应的目录，例如 10-程序/" + id.Replace('.', '/') + "/，入口、测试与 SPEC.md 都放在该目录；本脚手架的说明目录是 00-程序树/说明/" + id + ".txt，清单在 00-程序树/program-tree.yaml。" + Environment.NewLine +
               "怎么改：第一步改 responsibility 与 not_responsible，让职责可被验收；第二步补 inputs/outputs 契约并在 00-程序树/契约/contracts.yaml 双向对齐；第三步把 files 与 home 指向真实代码，冒烟验收写上可执行命令；第四步同步策划树、程序树实例与标杆绑定。改完跑 ptree-cs validate 与 ptree-cs check，两条都零 E 才算完成。" + Environment.NewLine +
               "常见坑：只改清单不改契约、只改代码不写说明、把多个职责塞进一个程序、状态还写 planned/built 而清单已统一成未完成/已完成/已验证/退役。";
    }

    private static string Manifest(string project, string date)
    {
        var name = Q(project);
        return "schema_version: 1\n" +
               "tree_version: 1.0.0\n" +
               "updated: " + Q(date) + "\n" +
               "root: demo\n" +
               "conventions:\n" +
               "  program_root: 10-程序\n" +
               "  spec_filename: SPEC.md\n" +
               "  manifest_filename: program.yaml\n" +
               "  contracts_file: 00-程序树/契约/contracts.yaml\n" +
               "phases:\n" +
               "  - id: P0\n" +
               "    name: 起步\n" +
               "    goal: 建起根、部门与第一条程序，并跑通 validate 与 check\n" +
               "    exit_criteria:\n" +
               "      - 清单、契约、四棵树实例与说明齐全\n" +
               "      - ptree-cs validate 与 ptree-cs check 都是 E=0\n" +
               "nodes:\n" +
               "  - id: demo\n" +
               "    name: " + name + "\n" +
               "    kind: root\n" +
               "    layer: 0\n" +
               "    parent: null\n" +
               "    phase: P0\n" +
               "    status: 未完成\n" +
               "    responsibility: 管理整个项目的程序树、契约与交付边界\n" +
               "    not_responsible:\n" +
               "      - 不直接产出业务结果\n" +
               "    inputs: []\n" +
               "    outputs: []\n" +
               "    depends_on: []\n" +
               "    files: []\n" +
               "    acceptance:\n" +
               "      - 人工清单：根节点存在且校验通过\n" +
               "    spec: 00-程序树/README.txt\n" +
               "    created_at: " + Q(date) + "\n" +
               "    updated_at: " + Q(date) + "\n" +
               "  - id: core\n" +
               "    name: 核心部门\n" +
               "    kind: department\n" +
               "    layer: 1\n" +
               "    parent: demo\n" +
               "    phase: P0\n" +
               "    status: 未完成\n" +
               "    responsibility: 承接项目主流程并拆成可独立验收的程序\n" +
               "    not_responsible:\n" +
               "      - 不写具体业务实现\n" +
               "    inputs: []\n" +
               "    outputs: []\n" +
               "    depends_on:\n" +
               "      - demo\n" +
               "    files: []\n" +
               "    acceptance:\n" +
               "      - 人工清单：部门边界与子节点一致\n" +
               "    spec: 00-程序树/README.txt\n" +
               "    created_at: " + Q(date) + "\n" +
               "    updated_at: " + Q(date) + "\n" +
               "  - id: core.app\n" +
               "    name: 起步程序\n" +
               "    kind: program\n" +
               "    layer: 2\n" +
               "    parent: core\n" +
               "    phase: P0\n" +
               "    status: 未完成\n" +
               "    responsibility: 把用户请求加工成第一条结构化结果\n" +
               "    not_responsible:\n" +
               "      - 不负责部门治理\n" +
               "      - 不越权改其他节点\n" +
               "    inputs:\n" +
               "      - user.request\n" +
               "    outputs:\n" +
               "      - app.result\n" +
               "    depends_on:\n" +
               "      - core\n" +
               "    files: []\n" +
               "    acceptance:\n" +
               "      - 测试：python -m unittest discover -s tests -v\n" +
               "    spec: 00-程序树/README.txt\n" +
               "    created_at: " + Q(date) + "\n" +
               "    updated_at: " + Q(date) + "\n" +
               "groups:\n" +
               "  - id: group.all\n" +
               "    name: 全树\n" +
               "    description: 覆盖脚手架的全部节点\n" +
               "    members:\n" +
               "      - demo\n" +
               "      - core\n" +
               "      - core.app\n";
    }

    private static string Contracts(string date) => "schema_version: 1\n" +
        "contracts_version: \"1.0.0\"\n" +
        "updated: " + Q(date) + "\n" +
        "contracts:\n" +
        "  - id: user.request\n" +
        "    name: 用户请求\n" +
        "    version: 1.0.0\n" +
        "    external: true\n" +
        "    purpose: 由用户直接提供、未经加工的原始请求\n" +
        "    fields:\n" +
        "      text:\n" +
        "        type: string\n" +
        "    required:\n" +
        "      - text\n" +
        "    producers: []\n" +
        "    consumers:\n" +
        "      - core.app\n" +
        "  - id: app.result\n" +
        "    name: 程序结果\n" +
        "    version: 1.0.0\n" +
        "    purpose: 起步程序产出的第一份结构化结果\n" +
        "    fields:\n" +
        "      summary:\n" +
        "        type: string\n" +
        "    required:\n" +
        "      - summary\n" +
        "    producers:\n" +
        "      - core.app\n" +
        "    consumers: []\n" +
        "    external_consumers: true\n";

    private static string Benchmarks(string date) => "schema_version: 1\n" +
        "updated: " + Q(date) + "\n" +
        "benchmarks: []\n" +
        "coverage:\n" +
        "  required: []\n";

    private static string Bindings(string date) => "schema_version: 1\n" +
        "updated: " + Q(date) + "\n" +
        "bindings: []\n";

    private static string ProgramTreeInstance(string project, string date)
    {
        return "程序树（" + project + " 起步实例）\n" +
               "版本 1 " + date + "，适用：本项目\n" +
               "一 读法\n" +
               "1 每行字段顺序：id ｜ 名称 ｜ 一句话职责 ｜ 状态 ｜ 退役 ｜ 组 ｜ 时间 ｜ 策划条目\n" +
               "2 本文件只是清单的简读视图，冲突时以 00-程序树/program-tree.yaml 为准。\n" +
               "二 节点\n" +
               "demo ｜ 项目根 ｜ 管理程序树与交付边界 ｜ 未完成 ｜ 无 ｜ group.all ｜ " + date + " ｜ D01\n" +
               "core ｜ 核心部门 ｜ 承接项目主流程 ｜ 未完成 ｜ 无 ｜ group.all ｜ " + date + " ｜ D01\n" +
               "core.app ｜ 起步程序 ｜ 产出第一份结构化结果 ｜ 未完成 ｜ 无 ｜ group.all ｜ " + date + " ｜ D01\n";
    }

    private static string PlanningTreeInstance(string project, string date)
    {
        return "策划树（" + project + " 起步实例）\n" +
               "版本 1 " + date + "，适用：本项目\n" +
               "一 读法\n" +
               "1 每行字段顺序：编号 ｜ 名称 ｜ 目标 ｜ 边界 ｜ 未定 ｜ 对应程序 ｜ 文档形态 ｜ 确认状态 ｜ 时间\n" +
               "2 一条策划对应一到多个程序；未定稿条目必须标未定。\n" +
               "二 条目\n" +
               "D01 ｜ 起步主流程 ｜ 跑通第一条从请求到结果的流程 ｜ 只做最小闭环 ｜ 无 ｜ core.app ｜ 整合稿 ｜ 未确认 ｜ " + date + "\n";
    }

    private static string ArtTreeInstance(string project, string date)
    {
        return "美术树（" + project + " 起步实例）\n" +
               "版本 1 " + date + "，适用：本项目\n" +
               "一 读法\n" +
               "1 每行字段顺序：编号 ｜ 名称 ｜ 分类 ｜ 用处 ｜ 风格 ｜ 状态 ｜ 组 ｜ 时间\n" +
               "二 条目\n" +
               "M01 ｜ 占位视觉 ｜ 界面 ｜ 验证树字段齐全 ｜ 未定 ｜ 未完成 ｜ group.all ｜ " + date + "\n";
    }

    private static string UiTreeInstance(string project, string date)
    {
        return "UI树（" + project + " 起步实例）\n" +
               "版本 1 " + date + "，适用：本项目\n" +
               "一 读法\n" +
               "1 每行字段顺序：编号 ｜ 名称 ｜ 场景 ｜ 时刻 ｜ 父子集 ｜ 字体 ｜ 状态 ｜ 组 ｜ 时间\n" +
               "二 条目\n" +
               "U01 ｜ 主界面 ｜ 首页 ｜ 常驻 ｜ 无 ｜ 正文字 ｜ 未完成 ｜ group.all ｜ " + date + "\n";
    }

    private static string Readme(string project, string date)
    {
        return project + " 程序树起步说明\n" +
               "生成日期：" + date + "\n" +
               "生成命令：ptree-cs init\n" +
               "\n" +
               "一、这里有什么\n" +
               "1 00-程序树/program-tree.yaml：根、1 个部门、1 个程序与 P0 阶段。\n" +
               "2 00-程序树/契约/contracts.yaml：1 条外部输入契约与 1 条程序输出契约。\n" +
               "3 00-程序树/标杆/benchmarks.yaml 与 绑定.yaml：空但结构合法。\n" +
               "4 00-程序树/树实例/：程序树、策划树、美术树、UI树的最小实例。\n" +
               "5 00-程序树/说明/：每个节点的起步说明，写清怎么改、代码该放哪。\n" +
               "6 00-程序树/变更/、生成/、运维/：空目录，留给提案、生成物与验证报告。\n" +
               "\n" +
               "二、下一步\n" +
               "1 改 program-tree.yaml 与说明：把 demo/core/core.app 换成真实职责与边界。\n" +
               "2 把代码写进 10-程序/ 下与节点 id 对应的目录，补 home、files、SPEC.md 与验收命令。\n" +
               "3 同步契约的 producers/consumers、策划树对应程序与标杆绑定。\n" +
               "4 每次改完跑 ptree-cs validate 与 ptree-cs check，两条都零 E 再继续。\n" +
               "\n" +
               "三、常用命令\n" +
               "验证：ptree-cs validate、ptree-cs check\n" +
               "生成：ptree-cs book、ptree-cs view、ptree-cs order、ptree-cs benchmark\n" +
               "提案：ptree-cs apply --file 00-程序树/变更/<提案>.yaml；确认后加 --write\n" +
               "整树自检：ptree-cs verify\n";
    }
}
