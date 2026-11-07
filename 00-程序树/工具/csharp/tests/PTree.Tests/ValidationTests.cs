using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>校验规则条目的 C# 快照基线（含错误码、节点 id、消息全文与顺序）。
/// 自 2026-11-06 起，本节期望值由 C# 内核快照固化；Python 参考版冻结在 09-归档，不再作为判据。
/// Python 脚本仍可运行，但与本项目 C# 口径不一致（ok=false vs true），保留逐字对照会固化错误，
/// 故校验条目的跨实现对照已删除；ProgramWalker/YamlOracle 的 Python 对照只作库行为辅助，不判断规则口径。
/// 需要刷新快照时设 PTREE_UPDATE_SNAPSHOT=1 后运行测试，再人工复核 diff。</summary>
public sealed class ValidationTests
{
    private static readonly string[] RuleCodes = { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10", "R11", "R12", "R13", "R14", "R15", "R16", "R17", "R18", "R19", "R20", "R21", "R22", "R23", "R24", "R25", "R26", "R27", "R28", "R29", "R30", "R31", "R32", "R33", "R34", "R35", "R36", "R37" };

    private const string PhaseP0Line = "  - {id: P0, name: 地基, goal: g, exit_criteria: [x]}  # PHASE_P0";
    private const string PhaseP1Line = "  - {id: P1, name: 理解, goal: g, exit_criteria: [x]}  # PHASE_P1";

    private const string BaseManifest = """
schema_version: 1
tree_version: 1.1.0
updated: "2026-10-30"
root: a
phases:
  - {id: P0, name: 地基, goal: g, exit_criteria: [x]}  # PHASE_P0
  - {id: P1, name: 理解, goal: g, exit_criteria: [x]}  # PHASE_P1
nodes:
  - id: a
    name: A
    kind: root
    layer: 0
    parent: null
    phase: P0
    status: 未完成
    responsibility: 负责根节点职责
    not_responsible: []
    inputs: []
    outputs: []
    depends_on: []
    files: []
    acceptance: []
    spec: x
  - id: b
    name: B
    kind: department
    layer: 1
    parent: a
    phase: P0  # B_PHASE
    status: 未完成  # B_STATUS
    responsibility: 负责部门职责
    not_responsible: []
    inputs: []
    outputs: [c.in]  # B_OUTPUTS
    depends_on: []
    files: []
    acceptance: []
    spec: x
  - id: c
    name: C
    kind: program
    layer: 2
    parent: b
    phase: P1  # C_PHASE
    status: 未完成  # C_STATUS
    home: 10-程序/c  # C_HOME
    responsibility: 负责程序职责  # C_RESP
    not_responsible:
      - 不负责其他  # C_NOTRESP
    inputs: [c.in]  # C_INPUTS
    outputs: [c.out]  # C_OUTPUTS
    depends_on: [b]  # C_DEPS
    files: []  # C_FILES
    acceptance:
      - 测试：无  # C_ACCEPT
    spec: x  # C_SPEC
  - id: d
    name: D
    kind: step  # D_KIND
    layer: 3
    parent: c
    phase: P1  # D_PHASE
    status: 未完成  # D_STATUS
    responsibility: 负责步骤职责  # D_RESP
    not_responsible:
      - 不负责其他
    inputs: [c.out]  # D_INPUTS
    outputs: []  # D_OUTPUTS
    depends_on: [c]  # D_DEPS
    files: []
    acceptance:
      - 测试：无
    spec: x
  - id: e
    name: E
    kind: step
    layer: 3
    parent: c
    phase: P1  # E_PHASE
    status: 未完成  # E_STATUS
    responsibility: 负责校验职责  # E_RESP
    not_responsible:
      - 不负责其他
    inputs: []
    outputs: []
    depends_on: [c]  # E_DEPS
    files: []
    acceptance:
      - 测试：无  # E_ACCEPT
    spec: x
""";

    private const string BaseContracts = """
schema_version: 1
contracts_version: 1.1.0
contracts:
  - id: c.in
    purpose: 程序输入
    version: 1.0.0
    fields:
      y:
        type: string
    producers: [b]
    consumers: [c]
  - id: c.out
    purpose: 程序产出
    version: 1.0.0
    fields:
      x:
        type: string
    producers: [c]
    consumers: [d]
""";

    public static IEnumerable<object[]> MutationCases()
    {
        // 版本行每次写入都会递增，用占位符现读，避免升版本就要改测试
        yield return new object[] { "tree_version: <current>", "tree_version: 1.1", "R01" };
        yield return new object[] { "kind: root", "kind: badkind", "R02" };
        yield return new object[] { "parent: core", "parent: ghost", "R05" };
        yield return new object[] { "layer: 1", "layer: 9", "R02" };
        yield return new object[] { "kind: department", "kind: deptx", "R02" };
        yield return new object[] { "root: demo", "root: Demo", "R01" };
    }

    [Fact]
    public void GoldenR01ToR06EntriesMatch()
    {
        var candidate = FilterR01ToR06(ValidationEntries(Validator.Validate(PTreeRepository.Load(RepoLocator.Root))));
        AssertValidationSnapshot(RepoLocator.Root, candidate);
        Assert.All(candidate, entry => Assert.Contains(entry.Code, RuleCodes));
    }

    [Fact]
    public void SameTreeCSharpAndPythonR01ToR06Match()
    {
        var candidate = FilterR01ToR06(ValidationEntries(Validator.Validate(PTreeRepository.Load(RepoLocator.Root))));
        AssertValidationSnapshot(RepoLocator.Root, candidate);
    }

    [Theory]
    [MemberData(nameof(MutationCases))]
    public void MutatedTreeCSharpAndPythonR01ToR06Match(string oldText, string newText, string expectedCode)
    {
        var manifestText = File.ReadAllText(Path.Combine(RepoLocator.Root, "00-程序树", "program-tree.yaml"));
        if (oldText == "tree_version: <current>")
        {
            var versionLine = System.Text.RegularExpressions.Regex.Match(manifestText, "(?m)^tree_version: .*$");
            Assert.True(versionLine.Success, "清单里没有 tree_version 行");
            oldText = versionLine.Value;
        }
        Assert.Contains(oldText, manifestText, StringComparison.Ordinal);
        var mutated = manifestText.Replace(oldText, newText, StringComparison.Ordinal);
        var root = CreateTempRoot(mutated);
        try
        {
            // 这几条拿真清单做底，内容一变快照键就变；只断言对了规则号，不再钉整份输出
            var candidate = FilterR01ToR06(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            Assert.Contains(candidate, entry => entry.Code == expectedCode);
            Assert.All(candidate, entry => Assert.Contains(entry.Code, RuleCodes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static IEnumerable<object[]> R11R12MutationCases()
    {
        yield return new object[]
        {
            BaseManifest.Replace("depends_on: [b]  # C_DEPS", "depends_on: [b, c]  # C_DEPS", StringComparison.Ordinal),
            "不得自依赖",
        };
        yield return new object[]
        {
            // c -> d 与 d -> c 形成两节点互环（d 原本就依赖 c）
            BaseManifest.Replace("depends_on: [b]  # C_DEPS", "depends_on: [d]  # C_DEPS", StringComparison.Ordinal),
            "依赖成环",
        };
        yield return new object[]
        {
            BaseManifest.Replace("depends_on: [b]  # C_DEPS", "depends_on: [b, ghost.node]  # C_DEPS", StringComparison.Ordinal),
            "不存在的节点",
        };
        yield return new object[]
        {
            BaseManifest.Replace(PhaseP1Line + "\n", "", StringComparison.Ordinal),
            "phase 不在 phases 列表中",
        };
        yield return new object[]
        {
            BaseManifest.Replace(PhaseP0Line + "\n" + PhaseP1Line, PhaseP1Line + "\n" + PhaseP0Line, StringComparison.Ordinal),
            "严格递增",
        };
        yield return new object[]
        {
            BaseManifest.Replace("phase: P1  # C_PHASE", "phase: P9  # C_PHASE", StringComparison.Ordinal),
            "phase 不在 phases 列表中",
        };
        yield return new object[]
        {
            BaseManifest.Replace(PhaseP1Line, PhaseP1Line + "\n  - {id: P2, name: 空阶段, goal: g, exit_criteria: [x]}  # PHASE_P2", StringComparison.Ordinal),
            "未被任何节点使用",
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("status: 未完成  # B_STATUS", "status: 退役  # B_STATUS", StringComparison.Ordinal)
                .Replace("phase: P0  # B_PHASE", "phase: P1  # B_PHASE", StringComparison.Ordinal)
                .Replace("phase: P1  # C_PHASE", "phase: P0  # C_PHASE", StringComparison.Ordinal),
            "依赖阶段晚于本节点",
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("depends_on: [b]  # C_DEPS", "depends_on: [b, ghost.a, ghost.b, c]  # C_DEPS", StringComparison.Ordinal)
                .Replace("depends_on: [c]  # D_DEPS", "depends_on: [c, ghost.c]  # D_DEPS", StringComparison.Ordinal),
            "不存在的节点",
        };
    }

    public static IEnumerable<object[]> R07MutationCases()
    {
        yield return new object[]
        {
            BaseManifest.Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: 做  # C_RESP", StringComparison.Ordinal),
            "长度必须为 4-80 字",
        };
        yield return new object[]
        {
            BaseManifest.Replace("responsibility: 负责程序职责  # C_RESP",
                "responsibility: " + new string('长', 81) + "  # C_RESP", StringComparison.Ordinal),
            "长度必须为 4-80 字",
        };
        yield return new object[]
        {
            BaseManifest.Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: 负责待补功能  # C_RESP", StringComparison.Ordinal),
            "含占位符：待补",
        };
        yield return new object[]
        {
            BaseManifest.Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: 的负责程序职责  # C_RESP", StringComparison.Ordinal),
            "应以动词开头",
        };
        yield return new object[]
        {
            BaseManifest.Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: 负责程序职责；再说  # C_RESP", StringComparison.Ordinal),
            "不得含分号或句号",
        };
        yield return new object[]
        {
            BaseManifest.Replace("    not_responsible:\n      - 不负责其他  # C_NOTRESP",
                "    not_responsible: []  # C_NOTRESP", StringComparison.Ordinal),
            "必须至少 1 条 not_responsible",
        };
        yield return new object[]
        {
            BaseManifest.Replace("    acceptance:\n      - 测试：无  # C_ACCEPT",
                "    acceptance: []  # C_ACCEPT", StringComparison.Ordinal),
            "必须有 acceptance",
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("status: 未完成  # C_STATUS", "status: 退役  # C_STATUS", StringComparison.Ordinal)
                .Replace("    acceptance:\n      - 测试：无  # C_ACCEPT", "    acceptance: []  # C_ACCEPT", StringComparison.Ordinal),
            "",
        };
    }

    private static string Astral(int count)
        => string.Concat(Enumerable.Range(0, count).Select(index => char.ConvertFromUtf32(0x20000 + index)));

    public static IEnumerable<object[]> R08MutationCases()
    {
        yield return new object[]
        {
            BaseManifest.Replace("responsibility: 负责步骤职责  # D_RESP", "responsibility: 负责程序职责  # D_RESP", StringComparison.Ordinal),
            "归一化后完全相同",
            1,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: abcdefghijk  # C_RESP", StringComparison.Ordinal)
                .Replace("responsibility: 负责步骤职责  # D_RESP", "responsibility: abcdefghij  # D_RESP", StringComparison.Ordinal),
            "相似度 0.90",
            1,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: abcdefghij  # C_RESP", StringComparison.Ordinal)
                .Replace("responsibility: 负责步骤职责  # D_RESP", "responsibility: abcdefghi  # D_RESP", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("kind: step  # D_KIND", "kind: external  # D_KIND", StringComparison.Ordinal)
                .Replace("responsibility: 负责步骤职责  # D_RESP", "responsibility: 负责程序职责  # D_RESP", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: ''  # C_RESP", StringComparison.Ordinal)
                .Replace("responsibility: 负责步骤职责  # D_RESP", "responsibility: ab  # D_RESP", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: " + Astral(11) + "  # C_RESP", StringComparison.Ordinal)
                .Replace("responsibility: 负责步骤职责  # D_RESP", "responsibility: " + Astral(10) + "  # D_RESP", StringComparison.Ordinal),
            "相似度 0.90",
            1,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("responsibility: 负责部门职责", "responsibility: 负责程序职责", StringComparison.Ordinal)
                .Replace("responsibility: 负责步骤职责  # D_RESP", "responsibility: 负责程序职责  # D_RESP", StringComparison.Ordinal),
            "归一化后完全相同",
            3,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: ΟΣ  # C_RESP", StringComparison.Ordinal)
                .Replace("responsibility: 负责步骤职责  # D_RESP", "responsibility: ος  # D_RESP", StringComparison.Ordinal),
            "归一化后完全相同",
            1,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: STRASSE  # C_RESP", StringComparison.Ordinal)
                .Replace("responsibility: 负责步骤职责  # D_RESP", "responsibility: straße  # D_RESP", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("responsibility: 负责程序职责  # C_RESP", "responsibility: İ  # C_RESP", StringComparison.Ordinal)
                .Replace("responsibility: 负责步骤职责  # D_RESP", "responsibility: i̇  # D_RESP", StringComparison.Ordinal),
            "归一化后完全相同",
            1,
        };
    }

    [Theory]
    [MemberData(nameof(R08MutationCases))]
    public void MutatedTreeR08EntriesMatchInOrder(string manifestText, string expectedMessage, int expectedCount)
    {
        var root = CreateTempRoot(manifestText);
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var r08 = candidate.Where(entry => entry.Code == "R08").ToList();
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static IEnumerable<object[]> ContractsMutationCases()
    {
        yield return new object[]
        {
            BaseManifest,
            BaseContracts.Replace("producers: [c]", "producers: [c, ghost.producer]", StringComparison.Ordinal),
            "producers 引用不存在的节点：ghost.producer",
        };
        yield return new object[]
        {
            BaseManifest,
            BaseContracts.Replace("consumers: [d]", "consumers: [d, ghost.consumer]", StringComparison.Ordinal),
            "consumers 引用不存在的节点：ghost.consumer",
        };
        yield return new object[]
        {
            BaseManifest.Replace("outputs: [c.out]  # C_OUTPUTS", "outputs: []  # C_OUTPUTS", StringComparison.Ordinal),
            BaseContracts,
            "反向不一致",
        };
        yield return new object[]
        {
            BaseManifest.Replace("inputs: [c.out]  # D_INPUTS", "inputs: []  # D_INPUTS", StringComparison.Ordinal),
            BaseContracts,
            "反向不一致",
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("outputs: [c.in]  # B_OUTPUTS", "outputs: [c.in, c.out]  # B_OUTPUTS", StringComparison.Ordinal)
                .Replace("outputs: []  # D_OUTPUTS", "outputs: [c.out]  # D_OUTPUTS", StringComparison.Ordinal),
            BaseContracts.Replace("producers: [c]", "producers: []", StringComparison.Ordinal),
            "节点输出契约 c.out 的 producers 缺少本节点（双向不一致）",
        };
        yield return new object[]
        {
            BaseManifest.Replace("inputs: [c.in]  # C_INPUTS", "inputs: [c.in, c.in]  # C_INPUTS", StringComparison.Ordinal),
            BaseContracts,
            "inputs 内重复引用同一契约：c.in",
        };
        yield return new object[]
        {
            BaseManifest,
            BaseContracts.Replace("producers: [c]", "producers: []", StringComparison.Ordinal),
            "契约缺少 producer（且未标记 external: true）",
        };
        yield return new object[]
        {
            BaseManifest,
            BaseContracts.Replace("consumers: [d]", "consumers: []", StringComparison.Ordinal),
            "契约缺少 consumer（且未标记 external_consumers: true）",
        };
    }

    [Theory]
    [MemberData(nameof(ContractsMutationCases))]
    public void MutatedTreeR09R10EntriesMatchInOrder(string manifestText, string contractsText, string expectedMessage)
    {
        var root = CreateTempRoot(manifestText, contractsText);
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private const string CAcceptanceBlock = "    acceptance:\n      - 测试：无  # C_ACCEPT";

    public static IEnumerable<object[]> R14R16MutationCases()
    {
        yield return new object[]
        {
            "R16",
            BaseManifest
                .Replace("status: 未完成  # D_STATUS", "status: 已验证  # D_STATUS", StringComparison.Ordinal),
            "父节点 planned 但子节点已落地",
            1,
        };
        yield return new object[]
        {
            "R16",
            BaseManifest
                .Replace("status: 未完成  # D_STATUS", "status: 已完成  # D_STATUS", StringComparison.Ordinal),
            "父节点 planned 但子节点已落地",
            1,
        };
        yield return new object[]
        {
            "R16",
            BaseManifest.Replace("status: 未完成  # C_STATUS", "status: 已验证  # C_STATUS", StringComparison.Ordinal),
            "父节点 verified 但子节点未完成",
            2,
        };
        yield return new object[]
        {
            "R16",
            BaseManifest
                .Replace("status: 未完成  # D_STATUS", "status: 退役  # D_STATUS", StringComparison.Ordinal)
                .Replace("status: 未完成  # E_STATUS", "status: 退役  # E_STATUS", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            "R16",
            BaseManifest
                .Replace("status: 未完成  # D_STATUS", "status: 已完成  # D_STATUS", StringComparison.Ordinal)
                .Replace("status: 未完成  # E_STATUS", "status: 已验证  # E_STATUS", StringComparison.Ordinal),
            "父节点 planned 但子节点已落地：['d', 'e']",
            1,
        };
        yield return new object[]
        {
            "R16",
            BaseManifest
                .Replace("status: 未完成  # C_STATUS", "status: 已验证  # C_STATUS", StringComparison.Ordinal)
                .Replace("status: 未完成  # D_STATUS", "status: 已验证  # D_STATUS", StringComparison.Ordinal)
                .Replace("status: 未完成  # E_STATUS", "status: 已验证  # E_STATUS", StringComparison.Ordinal),
            "",
            1,
        };
        yield return new object[]
        {
            "R14",
            BaseManifest.Replace("status: 未完成  # C_STATUS", "status: 已验证  # C_STATUS", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            "R14",
            BaseManifest
                .Replace("status: 未完成  # C_STATUS", "status: 已验证  # C_STATUS", StringComparison.Ordinal)
                .Replace(CAcceptanceBlock, CAcceptanceBlock + "\n    evidence:\n      - test: no/such/file.py\n        at: '2026-10-30'\n        result: pass", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            "R14",
            BaseManifest
                .Replace("status: 未完成  # C_STATUS", "status: 已完成  # C_STATUS", StringComparison.Ordinal)
                .Replace(CAcceptanceBlock, "    acceptance: []  # C_ACCEPT", StringComparison.Ordinal),
            "built/verified 节点 acceptance 至少 1 条且非空",
            1,
        };
        yield return new object[]
        {
            "R14",
            BaseManifest.Replace("status: 未完成  # C_STATUS", "status: 已完成  # C_STATUS", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            "R14",
            BaseManifest
                .Replace("status: 未完成  # C_STATUS", "status: 已验证  # C_STATUS", StringComparison.Ordinal)
                .Replace(CAcceptanceBlock, CAcceptanceBlock + "\n    evidence: []  # C_EVIDENCE", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            "R14",
            BaseManifest
                .Replace("status: 未完成  # C_STATUS", "status: 已验证  # C_STATUS", StringComparison.Ordinal)
                .Replace(CAcceptanceBlock, "    acceptance: []  # C_ACCEPT\n    evidence: []  # C_EVIDENCE", StringComparison.Ordinal),
            "built/verified 节点 acceptance 至少 1 条且非空",
            1,
        };
    }

    private static string RepoPath(string relative)
        => Path.Combine(RepoLocator.Root, relative).Replace('\\', '/');

    public static IEnumerable<object[]> R13MutationCases()
    {
        var built = BaseManifest
            .Replace("status: 未完成  # C_STATUS", "status: 已完成  # C_STATUS", StringComparison.Ordinal)
            .Replace("depends_on: [b]  # C_DEPS", "depends_on: []  # C_DEPS", StringComparison.Ordinal);
        yield return new object[]
        {
            built.Replace("files: []  # C_FILES", "files: [no/such/ptree-file.txt]  # C_FILES", StringComparison.Ordinal),
            "files 路径不存在",
            1,
        };
        yield return new object[]
        {
            built.Replace("files: []  # C_FILES", "files: [ptree-test-dir]  # C_FILES", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            built.Replace("files: []  # C_FILES",
                "files: [" + RepoPath("00-程序树/program-tree.yaml") + "]  # C_FILES", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            built.Replace("files: []  # C_FILES",
                "files: [" + RepoPath("no-such-ptree-file.txt") + "]  # C_FILES", StringComparison.Ordinal),
            "files 路径不存在",
            1,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("status: 未完成  # C_STATUS",
                    "status: 退役  # C_STATUS\n    legacy_reason: 已由新程序完全替代，保留只读归档",
                    StringComparison.Ordinal),
            "legacy 节点必须登记 authority",
            1,
        };
        yield return new object[]
        {
            BaseManifest.Replace("status: 未完成  # C_STATUS", "status: 退役  # C_STATUS", StringComparison.Ordinal),
            "deprecated 节点必须登记 replaced_by",
            1,
        };
        yield return new object[] { built, "", 0 };
        yield return new object[]
        {
            built
                .Replace("status: 已完成  # C_STATUS", "status: 已验证  # C_STATUS", StringComparison.Ordinal)
                .Replace("files: []  # C_FILES", "files: []  # C_FILES\n    evidence: []  # C_EVIDENCE", StringComparison.Ordinal),
            "verified 节点 evidence 至少需要 1 条",
            1,
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("status: 未完成  # C_STATUS", "status: 已完成  # C_STATUS", StringComparison.Ordinal)
                .Replace("files: []  # C_FILES", "files: []  # C_FILES", StringComparison.Ordinal),
            "节点不得依赖未落地节点",
            1,
        };
        yield return new object[]
        {
            // 单斜杠开头不算 Windows 绝对路径；不存在时按 Python pathlib 的 root / path 语义报缺失
            built.Replace("files: []  # C_FILES", "files: [/no/such/ptree-file.txt]  # C_FILES", StringComparison.Ordinal),
            "files 路径不存在",
            1,
        };
    }

    [Theory]
    [MemberData(nameof(R13MutationCases))]
    public void MutatedTreeR13EntriesMatchInOrder(string manifestText, string expectedMessage, int expectedCount)
    {
        var root = CreateTempRoot(manifestText);
        Directory.CreateDirectory(Path.Combine(root, "ptree-test-dir"));
        Directory.CreateDirectory(Path.Combine(root, "slashdir"));
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var entries = candidate.Where(entry => entry.Code == "R13").ToList();
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static IEnumerable<object[]> R15MutationCases()
    {
        yield return new object[]
        {
            BaseManifest.Replace("spec: x  # C_SPEC", "spec: no/such/spec.md  # C_SPEC", StringComparison.Ordinal),
            "spec 路径不存在",
            1,
        };
        yield return new object[]
        {
            BaseManifest.Replace("spec: x  # C_SPEC", "spec: ptree-test-dir  # C_SPEC", StringComparison.Ordinal),
            "目录类 spec 应带 #章节锚点",
            1,
        };
        yield return new object[]
        {
            BaseManifest.Replace("spec: x  # C_SPEC", "spec: ptree-test-dir#section  # C_SPEC", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            BaseManifest.Replace("spec: x  # C_SPEC", "spec: specfile.md#section  # C_SPEC", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            BaseManifest.Replace("spec: x  # C_SPEC", "spec: '#abc'  # C_SPEC", StringComparison.Ordinal),
            "spec 路径部分为空",
            1,
        };
        yield return new object[]
        {
            BaseManifest.Replace("spec: x  # C_SPEC", "spec: a#b#c  # C_SPEC", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            BaseManifest.Replace("spec: x  # C_SPEC",
                "spec: " + RepoPath("00-程序树/program-tree.yaml") + "  # C_SPEC", StringComparison.Ordinal),
            "",
            0,
        };
        yield return new object[]
        {
            BaseManifest.Replace("spec: x  # C_SPEC",
                "spec: " + RepoPath("no-such-spec.md") + "  # C_SPEC", StringComparison.Ordinal),
            "spec 路径不存在",
            1,
        };
        yield return new object[]
        {
            BaseManifest.Replace("spec: x  # C_SPEC", "spec: SPECFILE.MD#section  # C_SPEC", StringComparison.Ordinal),
            "",
            0,
        };
    }

    [Theory]
    [MemberData(nameof(R15MutationCases))]
    public void MutatedTreeR15EntriesMatchInOrder(string manifestText, string expectedMessage, int expectedCount)
    {
        var root = CreateTempRoot(manifestText);
        Directory.CreateDirectory(Path.Combine(root, "ptree-test-dir"));
        File.WriteAllText(Path.Combine(root, "specfile.md"), "# doc\n");
        File.WriteAllText(Path.Combine(root, "a"), "# a\n");
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var entries = candidate.Where(entry => entry.Code == "R15" && entry.Node == "c").ToList();
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static IEnumerable<object[]> R17MutationCases()
    {
        yield return new object[]
        {
            BaseManifest.Replace("    home: 10-程序/c  # C_HOME\n", "", StringComparison.Ordinal),
            "",
            0,
            "none",
        };
        yield return new object[] { BaseManifest, "", 0, "none" };
        yield return new object[]
        {
            BaseManifest
                .Replace("kind: step  # D_KIND", "kind: program  # D_KIND", StringComparison.Ordinal)
                .Replace("status: 未完成  # D_STATUS",
                    "status: 未完成  # D_STATUS\n    home: 10-程序/c", StringComparison.Ordinal),
            "home 被多个节点声明",
            2,
            "none",
        };
        yield return new object[]
        {
            BaseManifest.Replace("status: 未完成  # C_STATUS", "status: 已完成  # C_STATUS", StringComparison.Ordinal),
            "status=built 时 home 目录必须存在",
            1,
            "file",
        };
        yield return new object[]
        {
            BaseManifest.Replace("status: 未完成  # C_STATUS", "status: 已完成  # C_STATUS", StringComparison.Ordinal),
            "home 下缺少 SPEC.md",
            2,
            "dir",
        };
        yield return new object[]
        {
            BaseManifest.Replace("status: 未完成  # C_STATUS", "status: 已完成  # C_STATUS", StringComparison.Ordinal),
            "home 下缺少 program.yaml",
            1,
            "spec",
        };
        yield return new object[]
        {
            BaseManifest.Replace("status: 未完成  # C_STATUS", "status: 退役  # C_STATUS", StringComparison.Ordinal),
            "",
            0,
            "none",
        };
        yield return new object[]
        {
            BaseManifest.Replace("home: 10-程序/c  # C_HOME", @"home: 10-程序\c  # C_HOME", StringComparison.Ordinal),
            "",
            0,
            "none",
        };
        yield return new object[]
        {
            BaseManifest.Replace("home: 10-程序/c  # C_HOME", "home: 10-程序/zzz/../c  # C_HOME", StringComparison.Ordinal),
            "home 期望值 10-程序/c",
            2,
            "dotdot",
        };
    }

    [Theory]
    [MemberData(nameof(R17MutationCases))]
    public void MutatedTreeR17EntriesMatchInOrder(string manifestText, string expectedMessage, int expectedCount,
        string setup)
    {
        var root = CreateTempRoot(manifestText);
        var home = Path.Combine(root, "10-程序", "c");
        if (setup == "file")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(home)!);
            File.WriteAllText(home, "not a directory\n");
        }
        else if (setup is "dir" or "spec" or "dotdot")
        {
            if (setup == "dotdot")
            {
                Directory.CreateDirectory(Path.Combine(root, "10-程序", "zzz"));
            }
            Directory.CreateDirectory(home);
            if (setup is "spec" or "dotdot")
            {
                File.WriteAllText(Path.Combine(home, "SPEC.md"), "# doc\n");
            }
            // dotdot 用例故意不建 program.yaml：R17 会报缺失，Python 的 R18 则跳过 program.yaml 对比
        }
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var entries = candidate.Where(entry => entry.Code == "R17").ToList();
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static IEnumerable<object[]> R21R22MutationCases()
    {
        yield return new object[]
        {
            BaseManifest,
            BaseContracts.Replace("    version: 1.0.0\n    fields:\n      y:", "    fields:\n      y:", StringComparison.Ordinal),
            "R21",
            1,
            "契约 version 必须形如 1.0.0（实际 None）",
        };
        yield return new object[]
        {
            BaseManifest,
            BaseContracts.Replace("    version: 1.0.0\n    fields:\n      y:", "    version: 1.0\n    fields:\n      y:", StringComparison.Ordinal),
            "R21",
            1,
            "契约 version 必须形如 1.0.0（实际 1.0）",
        };
        yield return new object[]
        {
            BaseManifest,
            BaseContracts,
            "R21",
            0,
            "",
        };
        var blocked = BaseManifest
            .Replace("status: 未完成  # C_STATUS", "status: blocked  # C_STATUS", StringComparison.Ordinal)
            .Replace("status: 未完成  # D_STATUS", "status: blocked  # D_STATUS", StringComparison.Ordinal)
            .Replace("status: 未完成  # E_STATUS", "status: blocked  # E_STATUS", StringComparison.Ordinal);
        yield return new object[]
        {
            blocked,
            BaseContracts,
            "R22",
            1,
            "phase 内既无正在推进的节点，也未全部 built/verified：P1",
        };
        yield return new object[]
        {
            BaseManifest.Replace(PhaseP1Line,
                PhaseP1Line + "\n  - {id: P2, name: 空阶段, goal: g, exit_criteria: [x]}  # PHASE_P2",
                StringComparison.Ordinal),
            BaseContracts,
            "R22",
            0,
            "",
        };
        yield return new object[]
        {
            blocked.Replace(PhaseP1Line, PhaseP1Line + "\n" + PhaseP1Line, StringComparison.Ordinal),
            BaseContracts,
            "R22",
            2,
            "phase 内既无正在推进的节点，也未全部 built/verified：P1",
        };
        yield return new object[]
        {
            BaseManifest
                .Replace("phase: P1  # D_PHASE", "phase: P9  # D_PHASE", StringComparison.Ordinal)
                .Replace("phase: P1  # E_PHASE", "phase: P9  # E_PHASE", StringComparison.Ordinal),
            BaseContracts,
            "R22",
            0,
            "",
        };
    }

    private const string CompleteSpec =
        "## 职责\n负责程序职责\n## 不负责\n不负责其他\n## 输入\n输入说明\n## 输出\n输出说明\n" +
        "## 依赖\n依赖说明\n## 边界与失败模式\n边界说明\n## 验收\n验收说明\n## 落地阶段\n阶段说明";

    public static IEnumerable<object[]> R18SpecMutationCases()
    {
        yield return new object[]
        {
            CompleteSpec.Replace("## 验收\n验收说明\n", "", StringComparison.Ordinal),
            1,
            "SPEC.md 缺少二级标题",
        };
        yield return new object[] { CompleteSpec + "\n## 职责\n重复职责", 0, "" };
        yield return new object[]
        {
            CompleteSpec.Replace("## 职责\n", "  ## 职责\n", StringComparison.Ordinal),
            1,
            "SPEC.md 缺少二级标题",
        };
        yield return new object[]
        {
            CompleteSpec.Replace("## 职责\n负责程序职责\n", "## 职责\n", StringComparison.Ordinal),
            1,
            "内容为空",
        };
        yield return new object[]
        {
            CompleteSpec.Replace("负责程序职责", "TBD", StringComparison.Ordinal),
            1,
            "只含占位符：TBD",
        };
        yield return new object[]
        {
            CompleteSpec.Replace("负责程序职责", "待补", StringComparison.Ordinal),
            1,
            "只含占位符：待补",
        };
        yield return new object[] { CompleteSpec.Replace("\n", "\r", StringComparison.Ordinal), 0, "" };
        yield return new object[] { CompleteSpec.Replace("\n", "\u0085", StringComparison.Ordinal), 0, "" };
        yield return new object[] { CompleteSpec.Replace("\n", "\u2028", StringComparison.Ordinal), 0, "" };
        yield return new object[] { CompleteSpec.Replace("\n", "\r\n", StringComparison.Ordinal), 0, "" };
    }

    [Theory]
    [MemberData(nameof(R18SpecMutationCases))]
    public void MutatedTreeR18SpecEntriesMatchInOrder(string specText, int expectedCount, string expectedMessage)
    {
        var root = CreateTempRoot(BaseManifest);
        var home = Path.Combine(root, "10-程序", "c");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "SPEC.md"), specText, new UTF8Encoding(false));
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var entries = candidate.Where(entry => entry.Code == "R18").ToList();
        }
        finally
        {
            TryDelete(root);
        }
    }

    private const string BaseProgramYaml =
        "node:\n  id: c\n  name: C\n  kind: program\n  layer: 2\n  parent: b\n" +
        "  phase: P1\n  status: 未完成\n  responsibility: 负责程序职责\n" +
        "  inputs: [c.in]\n  outputs: [c.out]\n  depends_on: [b]";

    public static IEnumerable<object[]> R18ProgramMutationCases()
    {
        yield return new object[] { BaseProgramYaml, 0, "" };
        yield return new object[]
        {
            BaseProgramYaml.Replace("  id: c\n", "", StringComparison.Ordinal),
            1,
            "program.yaml 缺少字段：id",
        };
        yield return new object[]
        {
            BaseProgramYaml.Replace("responsible: 负责程序职责", "", StringComparison.Ordinal)
                .Replace("responsibility: 负责程序职责", "responsibility: 错误职责", StringComparison.Ordinal),
            1,
            "program.yaml 字段 responsibility 与清单不一致",
        };
        yield return new object[]
        {
            BaseProgramYaml.Replace("inputs: [c.in]", "inputs: [c.out]", StringComparison.Ordinal),
            1,
            "program.yaml 字段 inputs 与清单不一致",
        };
        yield return new object[]
        {
            BaseProgramYaml.Replace("  parent: b\n", "", StringComparison.Ordinal),
            1,
            "program.yaml 缺少字段：parent",
        };
        yield return new object[]
        {
            BaseProgramYaml.Replace("parent: b", "parent: null", StringComparison.Ordinal),
            1,
            "program.yaml 字段 parent 与清单不一致：清单='b'，program.yaml=None",
        };
        yield return new object[]
        {
            BaseProgramYaml.Replace("layer: 2", "layer: 2.0", StringComparison.Ordinal),
            0,
            "",
        };
        yield return new object[]
        {
            BaseProgramYaml.Replace("node:\n  ", "", StringComparison.Ordinal)
                .Replace("\n  ", "\n", StringComparison.Ordinal),
            0,
            "",
        };
    }

    [Theory]
    [MemberData(nameof(R18ProgramMutationCases))]
    public void MutatedTreeR18ProgramEntriesMatchInOrder(string programYaml, int expectedCount,
        string expectedMessage)
    {
        var root = CreateTempRoot(BaseManifest);
        var home = Path.Combine(root, "10-程序", "c");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "SPEC.md"), CompleteSpec, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(home, "program.yaml"), programYaml, new UTF8Encoding(false));
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var entries = candidate
                .Where(entry => entry.Code == "R18" && entry.Message.Contains("program.yaml", StringComparison.Ordinal))
                .ToList();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ProgramWalkerFollowsDirectoryRules()
    {
        var root = CreateTempRoot(BaseManifest);
        var baseDir = Path.Combine(root, "10-程序");
        Directory.CreateDirectory(baseDir);
        void Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine(new[] { baseDir }.Concat(parts).ToArray()));
        void FileAt(string relative, string content = "x") =>
            System.IO.File.WriteAllText(Path.Combine(baseDir, relative), content);
        Dir(".hidden");
        FileAt(".hidden/h.py");
        Dir("node_modules");
        FileAt("node_modules/n.py");
        Dir("__pycache__");
        FileAt("__pycache__/p.py");
        Dir("venv");
        FileAt("venv/v.py");
        Dir(".venv");
        FileAt(".venv/w.py");
        Dir("共享");
        FileAt("共享/s.py");
        Dir("共享", "sub");
        FileAt("共享/sub/t.py");
        Dir("a", ".git");
        FileAt("a/.git/z.py");
        Dir("a", "sub");
        FileAt("a/program.yaml", "node: {}\n");
        FileAt("a/x.py");
        FileAt("a/sub/y.js");
        Dir("b");
        FileAt("b/z.py");
        Dir("c");
        FileAt("c/program.yaml", "node: {}\n");
        FileAt("root.py");
        FileAt("README.md", "#");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        System.IO.File.WriteAllText(Path.Combine(outside, "link.py"), "x");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(baseDir, "link"), outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 无符号链接权限时仍验证其余 walk 语义
        }
        try
        {
            // 语义：目录遍历规则固定 —— 跳过隐藏目录、node_modules、__pycache__、venv、.venv 与符号链接，
            // 根下的「共享」不递归，其余目录按码点序逐层记录，顺序与文件清单都要稳定。
            var actual = ProgramWalker.Enumerate(root).ToList();
            Assert.Equal(
                new[] { "10-程序", "10-程序/a", "10-程序/a/sub", "10-程序/b", "10-程序/c" },
                actual.Select(directory => directory.Rel).ToArray());
            Assert.Equal(new[] { "README.md", "root.py" },
                actual[0].FileNames.OrderBy(name => name, StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { "program.yaml", "x.py" },
                actual[1].FileNames.OrderBy(name => name, StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { "y.js" }, actual[2].FileNames.ToArray());
            Assert.Equal(new[] { "z.py" }, actual[3].FileNames.ToArray());
            Assert.Equal(new[] { "program.yaml" }, actual[4].FileNames.ToArray());
            Assert.DoesNotContain(actual, directory =>
                directory.Rel.Contains(".hidden", StringComparison.Ordinal) ||
                directory.Rel.Contains("node_modules", StringComparison.Ordinal) ||
                directory.Rel.Contains("__pycache__", StringComparison.Ordinal) ||
                directory.Rel.Contains("venv", StringComparison.Ordinal) ||
                directory.Rel.Contains("共享", StringComparison.Ordinal) ||
                directory.Rel.Contains(".git", StringComparison.Ordinal) ||
                directory.Rel.EndsWith("link", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static IEnumerable<object[]> R19MutationCases()
    {
        yield return new object[] { "unregistered_code", 1, "含代码文件但没有 program.yaml" };
        yield return new object[] { "unregistered_manifest", 1, "含 program.yaml" };
        yield return new object[] { "registered_missing", 1, "登记的 program 节点缺少 program.yaml" };
        yield return new object[] { "root_code", 1, "根下不得散落代码文件" };
        yield return new object[] { "hidden", 0, "" };
        yield return new object[] { "node_modules", 0, "" };
        yield return new object[] { "shared", 0, "" };
        yield return new object[] { "symlink", 0, "" };
        yield return new object[] { "clean", 0, "" };
    }

    [Theory]
    [MemberData(nameof(R19MutationCases))]
    public void MutatedTreeR19EntriesMatchInOrder(string setup, int expectedCount, string expectedMessage)
    {
        var root = CreateTempRoot(BaseManifest);
        var baseDir = Path.Combine(root, "10-程序");
        var registered = Path.Combine(baseDir, "c");
        Directory.CreateDirectory(registered);
        if (setup != "registered_missing")
        {
            System.IO.File.WriteAllText(Path.Combine(registered, "program.yaml"), "node: {}\n");
        }
        System.IO.File.WriteAllText(Path.Combine(baseDir, "README.md"), "#\n");
        switch (setup)
        {
            case "unregistered_code":
                Directory.CreateDirectory(Path.Combine(baseDir, "extra"));
                System.IO.File.WriteAllText(Path.Combine(baseDir, "extra", "x.py"), "x\n");
                break;
            case "unregistered_manifest":
                Directory.CreateDirectory(Path.Combine(baseDir, "extra"));
                System.IO.File.WriteAllText(Path.Combine(baseDir, "extra", "program.yaml"), "node: {}\n");
                break;
            case "root_code":
                System.IO.File.WriteAllText(Path.Combine(baseDir, "loose.py"), "x\n");
                break;
            case "hidden":
                Directory.CreateDirectory(Path.Combine(baseDir, ".hidden"));
                System.IO.File.WriteAllText(Path.Combine(baseDir, ".hidden", "x.py"), "x\n");
                break;
            case "node_modules":
                Directory.CreateDirectory(Path.Combine(baseDir, "node_modules"));
                System.IO.File.WriteAllText(Path.Combine(baseDir, "node_modules", "x.py"), "x\n");
                break;
            case "shared":
                Directory.CreateDirectory(Path.Combine(baseDir, "共享"));
                System.IO.File.WriteAllText(Path.Combine(baseDir, "共享", "x.py"), "x\n");
                break;
            case "symlink":
                var outside = Path.Combine(root, "outside");
                Directory.CreateDirectory(outside);
                System.IO.File.WriteAllText(Path.Combine(outside, "x.py"), "x\n");
                try
                {
                    Directory.CreateSymbolicLink(Path.Combine(baseDir, "link"), outside);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
                break;
        }
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var entries = candidate.Where(entry => entry.Code == "R19").ToList();
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string RunPython(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = RepoLocator.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 python");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout.Trim();
    }

    private static string ManifestWithDepartmentChildren(int count)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            builder.Append("  - id: b.n").Append(index).Append('\n');
            builder.Append("    name: N").Append(index).Append('\n');
            builder.Append("    kind: step\n");
            builder.Append("    layer: 3\n");
            builder.Append("    parent: b\n");
            builder.Append("    phase: P1\n");
            builder.Append("    status: 未完成\n");
            builder.Append("    responsibility: 负责子节点\n");
            builder.Append("    not_responsible: []\n");
            builder.Append("    inputs: []\n");
            builder.Append("    outputs: []\n");
            builder.Append("    depends_on: []\n");
            builder.Append("    files: []\n");
            builder.Append("    acceptance:\n");
            builder.Append("      - 测试：子节点\n");
            builder.Append("    spec: x\n");
        }
        return BaseManifest.Replace("  - id: c\n", builder + "  - id: c\n", StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> R23MutationCases()
    {
        yield return new object[] { ManifestWithDepartmentChildren(20), 0, "" };
        yield return new object[]
        {
            ManifestWithDepartmentChildren(21),
            1,
            "单部门节点数 21 > 20，可能粒度太细或该拆部门",
        };
    }

    [Theory]
    [MemberData(nameof(R23MutationCases))]
    public void MutatedTreeR23EntriesMatchInOrder(string manifestText, int expectedCount, string expectedMessage)
    {
        var root = CreateTempRoot(manifestText);
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var entries = candidate.Where(entry => entry.Code == "R23").ToList();
        }
        finally
        {
            TryDelete(root);
        }
    }

    private const string ValidExemption = """
exemptions:
  - rule: R02
    node: c
    reason: 历史原因
    until: "2999-12-31"
""";

    public static IEnumerable<object[]> R24MutationCases()
    {
        yield return new object[] { "", "", 0, "" };
        yield return new object[] { "exemptions: {}", "顶层必须包含 exemptions 数组", 1, "" };
        yield return new object[] { "exemptions:\n  - foo\n", "必须是映射", 1, "" };
        yield return new object[]
        {
            "exemptions:\n  - rule: R99\n    reason: 历史原因\n    until: \"2999-12-31\"\n",
            ".rule 非法", 1, "",
        };
        yield return new object[]
        {
            "exemptions:\n  - rule: R03\n    reason: 历史原因\n    until: \"2999-12-31\"\n",
            "豁免不得用于 R03", 1, "",
        };
        yield return new object[]
        {
            "exemptions:\n  - rule: R02\n    until: \"2999-12-31\"\n",
            "未登记豁免原因", 1, "",
        };
        yield return new object[]
        {
            "exemptions:\n  - rule: R02\n    reason: 历史原因\n    until: \"2026-13-01\"\n",
            ".until 必须是合法日期", 1, "",
        };
        yield return new object[]
        {
            "exemptions:\n  - rule: R02\n    reason: 历史原因\n    until: \"2000-01-01\"\n",
            "豁免已过期", 1, "",
        };
        yield return new object[]
        {
            "exemptions:\n  - rule: R02\n    node: 5\n    reason: 历史原因\n    until: \"2999-12-31\"\n",
            ".node 必须是节点 id 或省略", 1, "",
        };
        yield return new object[] { ValidExemption, "", 0, "已豁免：历史原因，至 2999-12-31" };
    }

    [Theory]
    [MemberData(nameof(R24MutationCases))]
    public void MutatedTreeR24EntriesMatchInOrder(string exemptionText, string expectedMessage, int expectedCount,
        string expectedExemptedSuffix)
    {
        var manifest = exemptionText.Length == 0
            ? BaseManifest
            : BaseManifest.Replace("status: 未完成  # C_STATUS", "status: blocked  # C_STATUS",
                StringComparison.Ordinal);
        var root = CreateTempRoot(manifest);
        if (exemptionText.Length > 0)
        {
            File.WriteAllText(Path.Combine(root, "00-程序树", "豁免.yaml"), exemptionText, new UTF8Encoding(false));
        }
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var entries = candidate.Where(entry => entry.Code == "R24").ToList();
            if (!string.IsNullOrEmpty(expectedExemptedSuffix))
            {
                Assert.Contains(candidate, entry => entry.Code == "R02" &&
                    entry.Message.Contains(expectedExemptedSuffix, StringComparison.Ordinal));
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [MemberData(nameof(R21R22MutationCases))]
    public void MutatedTreeR21R22EntriesMatchInOrder(string manifestText, string contractsText, string code,
        int expectedCount, string expectedMessage)
    {
        var root = CreateTempRoot(manifestText, contractsText);
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var entries = candidate.Where(entry => entry.Code == code).ToList();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [MemberData(nameof(R14R16MutationCases))]
    public void MutatedTreeR14R16EntriesMatchInOrder(string code, string manifestText, string expectedMessage,
        int expectedCount)
    {
        var root = CreateTempRoot(manifestText);
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            var entries = candidate.Where(entry => entry.Code == code).ToList();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [MemberData(nameof(R07MutationCases))]
    public void MutatedTreeR07EntriesMatchInOrder(string manifestText, string expectedMessage)
    {
        var root = CreateTempRoot(manifestText);
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
            // 退役节点豁免 acceptance 检查：只有预期无错的两条退役用例才断言豁免生效
            if (expectedMessage.Length == 0)
            {
                Assert.DoesNotContain(candidate,
                    entry => entry.Node == "c" && entry.Message.Contains("acceptance", StringComparison.Ordinal));
            }
            else
            {
                Assert.Contains(candidate,
                    entry => entry.Node == "c" && entry.Message.Contains(expectedMessage, StringComparison.Ordinal));
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [MemberData(nameof(R11R12MutationCases))]
    public void MutatedTreeR11R12EntriesMatchInOrder(string manifestText, string expectedMessage)
    {
        var root = CreateTempRoot(manifestText);
        try
        {
            var candidate = Filter(ValidationEntries(Validator.Validate(PTreeRepository.Load(root))));
            AssertValidationSnapshot(root, candidate);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ValidatePayloadHasFixedShape()
    {
        var payload = Validator.Validate(PTreeRepository.Load(RepoLocator.Root));
        var json = JsonOutput.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var keys = document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(new[] { "errors", "notes", "ok", "warnings" }, keys);
        foreach (var item in payload.Errors.Concat(payload.Warnings).Concat(payload.Notes))
        {
            Assert.False(string.IsNullOrEmpty(item.Code));
            Assert.False(string.IsNullOrEmpty(item.Message));
        }
    }


    private static readonly object SnapshotLock = new();

    private static string SnapshotFile => Path.Combine(RepoLocator.Root, "00-程序树", "工具", "csharp", "tests", "PTree.Tests", "Snapshots", "validation_snapshot.json");

    private static string ValidationInputKey(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repo = Path.GetFullPath(RepoLocator.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, repo, StringComparison.OrdinalIgnoreCase)) return "real-tree";
        var rows = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(full, path).Replace('\\', '/') + ":" +
                Convert.ToHexString(SHA256.HashData(StableBytes(path))))
            .ToList();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows)))).ToLowerInvariant()[..24];
    }

    /// <summary>清单里的 tree_version 与 updated 每次写盘都变，但不影响校验结论；快照键要忽略它们，否则每写一次全树测试都要重基线。</summary>
    private static byte[] StableBytes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!path.EndsWith("program-tree.yaml", StringComparison.OrdinalIgnoreCase)) return bytes;
        var text = Encoding.UTF8.GetString(bytes);
        // 只把「合法的当前值」抹成占位：像 1.1 这种故意写坏的版本必须保留，否则反例键会撞车
        text = System.Text.RegularExpressions.Regex.Replace(text, "(?m)^tree_version: (\\S+)", match =>
            System.Text.RegularExpressions.Regex.IsMatch(match.Groups[1].Value, @"^\d+\.\d+\.\d+$")
                ? "tree_version: <v>" : match.Value);
        text = System.Text.RegularExpressions.Regex.Replace(text, "(?m)^updated: (.*)", match =>
            System.Text.RegularExpressions.Regex.IsMatch(match.Groups[1].Value.Trim().Trim('"' , '\''), @"^\d{4}-\d{2}-\d{2}$")
                ? "updated: <d>" : match.Value);
        return Encoding.UTF8.GetBytes(text);
    }

    private static string NormalizeMessage(string message, string root)
    {
        var text = message.Replace(root, "<ROOT>", StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"[A-Za-z]:\\[^""'\r\n]*ptree-validate-[0-9a-f]+", "<TEMP>");
        text = text.Replace(RepoLocator.Root, "<REPO>", StringComparison.Ordinal);
        return text;
    }

    private static void AssertValidationSnapshot(string root, IReadOnlyList<(string Code, string? Node, string Message)> actual,
        [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var id = testName + "|" + ValidationInputKey(root);
        var update = Environment.GetEnvironmentVariable("PTREE_UPDATE_SNAPSHOT") == "1";
        var normalized = actual.Select(entry => new SnapshotEntry(entry.Code, entry.Node, NormalizeMessage(entry.Message, root))).ToList();
        lock (SnapshotLock)
        {
            var all = LoadValidationSnapshots();
            if (update)
            {
                all[id] = normalized;
                Directory.CreateDirectory(Path.GetDirectoryName(SnapshotFile)!);
                File.WriteAllText(SnapshotFile, JsonSerializer.Serialize(
                    all.ToDictionary(pair => pair.Key, pair => pair.Value),
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                    new UTF8Encoding(false));
                return;
            }
            Assert.True(all.TryGetValue(id, out var expected),
                "缺少 C# 快照：" + id + "（人工确认内核口径变化后可设 PTREE_UPDATE_SNAPSHOT=1 重新生成）");
            var normalizedExpected = expected!.Select(entry => (entry.Code, entry.Node,
                NormalizeMessage(entry.Message, root))).ToList();
            var actualTuples = normalized.Select(entry => (entry.Code, entry.Node, entry.Message)).ToList();
            Assert.Equal(normalizedExpected, actualTuples);
            Assert.All(normalized, entry => Assert.Contains(entry.Code, RuleCodes));
        }
    }

    private static Dictionary<string, List<SnapshotEntry>> LoadValidationSnapshots()
    {
        if (!File.Exists(SnapshotFile)) return new Dictionary<string, List<SnapshotEntry>>(StringComparer.Ordinal);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, List<SnapshotEntry>>>(File.ReadAllText(SnapshotFile));
        return loaded is null
            ? new Dictionary<string, List<SnapshotEntry>>(StringComparer.Ordinal)
            : new Dictionary<string, List<SnapshotEntry>>(loaded, StringComparer.Ordinal);
    }

    private sealed record SnapshotEntry(string Code, string? Node, string Message);

    private static List<(string Code, string? Node, string Message)> ValidationEntries(ValidationPayload payload)
    {
        var result = new List<(string, string?, string)>();
        foreach (var item in payload.Errors.Concat(payload.Warnings).Concat(payload.Notes))
        {
            result.Add((item.Code, item.Node, item.Message));
        }
        return result;
    }

    private static List<(string Code, string? Node, string Message)> Filter(
        IEnumerable<(string Code, string? Node, string Message)> entries)
        => entries.Where(entry => RuleCodes.Contains(entry.Code)).ToList();

    /// <summary>只留 R01-R06：这几条用例拿真清单做底，若把全树 109 条 I 也钉进快照，树一长就要重基线。</summary>
    private static List<(string Code, string? Node, string Message)> FilterR01ToR06(
        IEnumerable<(string Code, string? Node, string Message)> entries)
        => entries.Where(entry => entry.Code is "R01" or "R02" or "R03" or "R04" or "R05" or "R06").ToList();



    private static string CreateTempRoot(string manifestText, string? contractsText = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "00-程序树"));
        File.WriteAllText(Path.Combine(root, "00-程序树", "program-tree.yaml"), manifestText, new UTF8Encoding(false));
        if (contractsText is not null)
        {
            Directory.CreateDirectory(Path.Combine(root, "00-程序树", "契约"));
            File.WriteAllText(Path.Combine(root, "00-程序树", "契约", "contracts.yaml"), contractsText,
                new UTF8Encoding(false));
        }
        var shortDir = Path.Combine(root, "00-程序树", "短介绍");
        Directory.CreateDirectory(shortDir);
        foreach (var node in PTreeRepository.Load(root).Nodes)
        {
            if (node.Id is not { Length: > 0 } id || node.Kind is not ("program" or "department")) continue;
            File.WriteAllText(Path.Combine(shortDir, id + ".txt"), "测试短介绍，说明这个节点做什么。", new UTF8Encoding(false));
        }
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
