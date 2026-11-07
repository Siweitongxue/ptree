using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PTree.Core;

/// <summary>
/// 校验内核（分段迁移）。当前实现 R01-R06、R07、R11-R12；未迁移规则不产出任何条目（不做假条目）。
/// 判据逐条对齐 Python 版 ptree.py 的对应 rule_rXX。
/// </summary>
public sealed class Validator
{
    private static readonly string[] TopRequired = { "schema_version", "tree_version", "updated", "root", "phases", "nodes" };
    private static readonly HashSet<string> TopAllowed = new(TopRequired, StringComparer.Ordinal)
    {
        "conventions", "groups", "trees", "links", "adapt",
    };
    private static readonly HashSet<string> ConventionKeys = new(StringComparer.Ordinal)
    {
        "program_root", "spec_filename", "manifest_filename", "contracts_file", "auto_commit",
    };
    private static readonly string[] PhaseRequired = { "id", "name", "goal", "exit_criteria" };
    private static readonly HashSet<string> PhaseAllowed = new(PhaseRequired, StringComparer.Ordinal);
    private static readonly string[] NodeRequired =
    {
        "id", "name", "kind", "layer", "parent", "phase", "status",
        "responsibility", "not_responsible", "inputs", "outputs",
        "depends_on", "files", "acceptance", "spec",
    };
    private static readonly HashSet<string> NodeAllowed = new(NodeRequired, StringComparer.Ordinal)
    {
        "home", "owner", "platform", "evidence", "legacy_reason",
        "authority", "replaced_by", "notes",
        "created_at", "updated_at", "attachments", "planning_exemption", "preset", "preset_outputs", "evaluation",
    };
    private static readonly string[] KindEnum = { "root", "department", "program", "step", "external" };
    private static readonly string[] StatusEnum = { "未完成", "已完成", "已验证", "退役" };
    private static readonly Dictionary<long, string[]> LayerKinds = new()
    {
        [0] = new[] { "root" },
        [1] = new[] { "department" },
        [2] = new[] { "program", "external" },
        [3] = new[] { "step", "external" },
    };
    private static readonly string[] EvidenceRequired = { "test", "at", "result" };
    private static readonly HashSet<string> EvidenceAllowed = new(EvidenceRequired, StringComparer.Ordinal) { "note" };

    private static readonly Regex VersionRe = new(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex DateRe = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex PhaseRe = new(@"^P[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex ContractRefRe =
        new(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*(\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex RootIdRe = new(@"^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SegmentRe = new(@"^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant);

    private readonly PTreeRepository _repository;
    private readonly List<Issue> _issues = new();
    private readonly RawMap _manifest;
    private readonly List<object?> _nodes;
    private readonly Dictionary<string, RawMap> _nodeMap = new(StringComparer.Ordinal);
    private readonly List<string> _nodeOrder = new();

    private Validator(PTreeRepository repository)
    {
        _repository = repository;
        _manifest = repository.ManifestRaw;
        _nodes = _manifest.TryGetValue("nodes", out var value) && value is List<object?> list
            ? list
            : new List<object?>();
        foreach (var item in _nodes)
        {
            if (item is RawMap map && map.TryGetValue("id", out var idValue) && idValue is string id &&
                !_nodeMap.ContainsKey(id))
            {
                _nodeMap[id] = map;
                _nodeOrder.Add(id);
            }
            if (item is RawMap statusMap && statusMap.TryGetValue("status", out var statusValue) &&
                statusValue is string statusText)
            {
                statusMap.Set("status", StatusValues.Normalize(statusText));
            }
        }
        DeriveLayers();
    }

    private void DeriveLayers()
    {
        foreach (var id in _nodeOrder)
        {
            var derived = DerivedLayer(id, new HashSet<string>(StringComparer.Ordinal));
            if (derived is null) continue;
            var node = _nodeMap[id];
            if (!node.TryGetValue("layer", out var current) || current is null)
            {
                node.Set("layer", derived.Value);
                continue;
            }
            if (current is long number && number != derived.Value)
            {
                Add("W", "R02", "layer 与 parent 链推导不一致，已按推导值 " + derived.Value + " 处理（原值 " + number + "）", id);
            }
            node.Set("layer", derived.Value);
        }
    }

    private long? DerivedLayer(string id, HashSet<string> visiting)
    {
        if (!visiting.Add(id) || !_nodeMap.TryGetValue(id, out var node)) return null;
        var kind = node.TryGetValue("kind", out var kindValue) ? kindValue as string : null;
        var parent = node.TryGetValue("parent", out var parentValue) ? parentValue as string : null;
        if (kind == "root" || string.IsNullOrWhiteSpace(parent)) return 0;
        var parentLayer = DerivedLayer(parent, visiting);
        return parentLayer is null ? null : parentLayer + 1;
    }

    public static ValidationPayload Validate(PTreeRepository repository)
    {
        var validator = new Validator(repository);
        validator.Run();
        return ValidationPayload.From(validator._issues);
    }

    public static ValidationPayload Orphans(PTreeRepository repository)
    {
        var validator = new Validator(repository);
        validator.R19();
        return ValidationPayload.From(validator._issues);
    }

    private void Run()
    {
        R01();
        R02();
        R03();
        R04();
        R05();
        R06();
        R07();
        R08();
        R09();
        R10();
        R11();
        R12();
        R13();
        R14();
        R15();
        R16();
        R17();
        R18();
        R19();
        R20();
        R21();
        R22();
        R23();
        R25();
        R26();
        R27();
        R28();
        R29();
        R31();
        R32();
        R33();
        R34();
        R35();
        R36();
        R37();
        R38();
        R39();
        R40();
        R41();
        R42();
        R43();
        R44();
        R45();
        R46();
        Exemptions();
        // R30、R24 豁免与 R30 之外全部只读规则已迁移；R30 仅 apply/check-plan
    }

    private void Add(string level, string code, string message, string? node = null)
        => _issues.Add(new Issue(level, code, message, node));

    private object? RootId => _manifest.TryGetValue("root", out var value) ? value : null;

    private string? NodeId(RawMap node)
        => node.TryGetValue("id", out var value) && value is string id ? id : null;

    private static bool IsValidDate(string text)
        => DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool Matches(Regex regex, string text) => regex.IsMatch(text);

    private static string KindList() => "[" + string.Join(", ", KindEnum.Select(PyValue.Repr)) + "]";

    private static string StatusList() => "[" + string.Join(", ", StatusEnum.Select(PyValue.Repr)) + "]";

    // ------------------------------------------------------------------
    // R01 顶层结构
    // ------------------------------------------------------------------

    private void CheckPhaseItem(object? item, int index)
    {
        if (item is not RawMap phase)
        {
            Add("E", "R01", "phases[" + index + "] 必须是映射");
            return;
        }
        var label = phase.TryGetValue("id", out var idValue) && idValue is string id ? id : "[" + index + "]";
        foreach (var key in PhaseRequired)
        {
            if (!phase.ContainsKey(key))
            {
                Add("E", "R01", "phases[" + label + "] 缺少必填键：" + key, label);
            }
        }
        foreach (var key in phase.Keys)
        {
            if (!PhaseAllowed.Contains(key))
            {
                Add("E", "R01", "phases[" + label + "] 含未知键：" + key, label);
            }
        }
        if (!(phase.TryGetValue("id", out var pid) && pid is string pidText && Matches(PhaseRe, pidText)))
        {
            Add("E", "R01", "phases[" + label + "].id 必须形如 P0（实际 " + PyValue.Repr(pid) + "）", label);
        }
        foreach (var key in new[] { "name", "goal" })
        {
            if (!(phase.TryGetValue(key, out var value) && PyValue.NonEmptyString(value)))
            {
                Add("E", "R01", "phases[" + label + "]." + key + " 必须是非空字符串", label);
            }
        }
        if (!(phase.TryGetValue("exit_criteria", out var criteria) && criteria is List<object?> criteriaList && criteriaList.Count > 0))
        {
            Add("E", "R01", "phases[" + label + "].exit_criteria 必须是非空数组", label);
        }
        else
        {
            for (var ci = 0; ci < criteriaList.Count; ci++)
            {
                if (!PyValue.NonEmptyString(criteriaList[ci]))
                {
                    Add("E", "R01", "phases[" + label + "].exit_criteria[" + ci + "] 必须是非空字符串", label);
                }
            }
        }
    }

    private void R01()
    {
        foreach (var key in TopRequired)
        {
            if (!_manifest.ContainsKey(key))
            {
                Add("E", "R01", "缺少必填顶层键：" + key);
            }
        }
        foreach (var key in _manifest.Keys)
        {
            if (!TopAllowed.Contains(key))
            {
                Add("E", "R01", "未知顶层键：" + key);
            }
        }
        var schemaVersion = _manifest.TryGetValue("schema_version", out var sv) ? sv : null;
        var schemaVersionIsOne = schemaVersion switch
        {
            bool => false,
            long number => number == 1,
            double number => number == 1.0,
            _ => false,
        };
        if (schemaVersion is bool || !schemaVersionIsOne)
        {
            Add("E", "R01", "schema_version 必须为 1（实际 " + PyValue.Repr(schemaVersion) + "）");
        }
        var treeVersion = _manifest.TryGetValue("tree_version", out var tv) ? tv : null;
        if (!(treeVersion is string treeVersionText && Matches(VersionRe, treeVersionText)))
        {
            Add("E", "R01", "tree_version 必须形如 1.0.0（实际 " + PyValue.Repr(treeVersion) + "）");
        }
        var updated = _manifest.TryGetValue("updated", out var up) ? up : null;
        if (!(updated is string updatedText && Matches(DateRe, updatedText)))
        {
            Add("E", "R01", "updated 必须形如 2026-10-24（实际 " + PyValue.Repr(updated) + "）");
        }
        else if (!IsValidDate(updatedText))
        {
            Add("E", "R01", "updated 不是合法日期：" + updatedText);
        }
        var rootId = RootId;
        if (!(rootId is string rootText && Matches(RootIdRe, rootText)))
        {
            Add("E", "R01", "root 必须是单段 id（如 my-project）（实际 " + PyValue.Repr(rootId) + "）");
        }
        if (_manifest.TryGetValue("conventions", out var conventionsValue) && conventionsValue is not null)
        {
            if (conventionsValue is not RawMap conventions)
            {
                Add("E", "R01", "conventions 必须是映射");
            }
            else
            {
                foreach (var key in conventions.Keys)
                {
                    if (!ConventionKeys.Contains(key))
                    {
                        Add("E", "R01", "conventions 含未知键：" + key);
                    }
                }
                foreach (var entry in conventions)
                {
                    if (entry.Key == "auto_commit")
                    {
                        if (entry.Value is not bool)
                        {
                            Add("E", "R01", "conventions.auto_commit 必须是 true 或 false");
                        }
                        continue;
                    }
                    if (!PyValue.NonEmptyString(entry.Value))
                    {
                        Add("E", "R01", "conventions." + entry.Key + " 必须是非空字符串");
                    }
                }
            }
        }
        if (!_manifest.TryGetValue("phases", out var phasesValue))
        {
            Add("E", "R01", "phases 必须是数组");
        }
        else if (phasesValue is not List<object?> phases)
        {
            Add("E", "R01", "phases 必须是数组");
        }
        else if (phases.Count == 0)
        {
            Add("E", "R01", "phases 不能为空");
        }
        else
        {
            for (var index = 0; index < phases.Count; index++)
            {
                CheckPhaseItem(phases[index], index);
            }
        }
        if (!_manifest.TryGetValue("nodes", out var nodesValue))
        {
            Add("E", "R01", "nodes 必须是数组");
        }
        else if (nodesValue is not List<object?> nodes)
        {
            Add("E", "R01", "nodes 必须是数组");
        }
        else if (nodes.Count == 0)
        {
            Add("E", "R01", "nodes 不能为空");
        }
    }

    // ------------------------------------------------------------------
    // R02 节点字段
    // ------------------------------------------------------------------

    private void CheckNodeShape(object? item, int index)
    {
        if (item is not RawMap node)
        {
            Add("E", "R02", "nodes[" + index + "] 必须是映射（实际 " + PyValue.TypeName(item) + "）");
            return;
        }
        var idValue = NodeId(node);
        var label = !string.IsNullOrEmpty(idValue) ? idValue : "[" + index + "]";
        void Check(string message) => Add("E", "R02", message, label);

        var requiredKind = node.TryGetValue("kind", out var kindValue) ? kindValue as string : null;
        foreach (var key in NodeRequired)
        {
            if (requiredKind == "root" && key == "parent") continue;
            if (requiredKind == "department" && key is "inputs" or "outputs" or "depends_on") continue;
            if (!node.ContainsKey(key))
            {
                Check("缺少必填字段：" + key);
            }
        }
        foreach (var key in node.Keys)
        {
            if (!NodeAllowed.Contains(key))
            {
                Check("含未知字段：" + key);
            }
        }
        if (node.TryGetValue("name", out var name) && !PyValue.NonEmptyString(name))
        {
            Check("name 必须是非空字符串");
        }
        if (node.TryGetValue("kind", out var kind) && !(kind is string kindText && KindEnum.Contains(kindText, StringComparer.Ordinal)))
        {
            Check("kind 非法：" + PyValue.Repr(kind) + "（允许 " + KindList() + "）");
        }
        if (node.TryGetValue("layer", out var layerValue))
        {
            var validLayer = !PyValue.IsBool(layerValue) && layerValue is long layerNumber && layerNumber is >= 0 and <= 3;
            if (!validLayer)
            {
                Check("layer 必须是 0-3 的整数（实际 " + PyValue.Repr(layerValue) + "）");
            }
        }
        if (node.TryGetValue("parent", out var parentValue))
        {
            if (parentValue is not null && !PyValue.NonEmptyString(parentValue))
            {
                Check("parent 必须是节点 id 或 null（实际 " + PyValue.Repr(parentValue) + "）");
            }
        }
        if (node.TryGetValue("phase", out var phaseValue))
        {
            if (!(phaseValue is string phaseText && Matches(PhaseRe, phaseText)))
            {
                Check("phase 必须形如 P0（实际 " + PyValue.Repr(phaseValue) + "）");
            }
        }
        if (node.TryGetValue("status", out var status) && !(status is string statusText && StatusEnum.Contains(statusText, StringComparer.Ordinal)))
        {
            Check("status 非法：" + PyValue.Repr(status) + "（允许 " + StatusList() + "）");
        }
        if (node.TryGetValue("responsibility", out var responsibility) && responsibility is not string)
        {
            Check("responsibility 必须是字符串");
        }
        if (node.TryGetValue("not_responsible", out var notResponsible))
        {
            if (notResponsible is not List<object?> notResponsibleList)
            {
                Check("not_responsible 必须是数组");
            }
            else
            {
                for (var i = 0; i < notResponsibleList.Count; i++)
                {
                    if (notResponsibleList[i] is not string)
                    {
                        Check("not_responsible[" + i + "] 必须是字符串");
                    }
                }
            }
        }
        foreach (var field in new[] { "inputs", "outputs" })
        {
            if (!node.TryGetValue(field, out var refs))
            {
                continue;
            }
            if (refs is not List<object?> refList)
            {
                Check(field + " 必须是数组");
            }
            else
            {
                for (var i = 0; i < refList.Count; i++)
                {
                    if (refList[i] is not string refText)
                    {
                        Check(field + "[" + i + "] 必须是字符串");
                    }
                    else if (!Matches(ContractRefRe, refText))
                    {
                        Check(field + "[" + i + "] 契约 id 格式不合法：" + PyValue.Repr(refText) +
                              "（期望小写字母/数字/连字符 + 点分段，如 scenario.brief）");
                    }
                }
            }
        }
        if (node.TryGetValue("depends_on", out var dependsOn))
        {
            if (dependsOn is not List<object?> dependsList)
            {
                Check("depends_on 必须是数组");
            }
            else
            {
                for (var i = 0; i < dependsList.Count; i++)
                {
                    if (dependsList[i] is not string)
                    {
                        Check("depends_on[" + i + "] 必须是字符串");
                    }
                }
            }
        }
        if (node.TryGetValue("files", out var files))
        {
            if (files is not List<object?> filesList || filesList.Any(entry => entry is not string))
            {
                Check("files 必须是字符串数组");
            }
        }
        if (node.TryGetValue("home", out var home) && !PyValue.NonEmptyString(home))
        {
            Check("home 必须是非空字符串");
        }
        if (node.TryGetValue("acceptance", out var acceptance))
        {
            if (acceptance is not List<object?> acceptanceList)
            {
                Check("acceptance 必须是数组");
            }
            else
            {
                for (var i = 0; i < acceptanceList.Count; i++)
                {
                    if (!PyValue.NonEmptyString(acceptanceList[i]))
                    {
                        Check("acceptance[" + i + "] 必须是非空字符串");
                    }
                }
            }
        }
        if (node.TryGetValue("spec", out var spec) && !PyValue.NonEmptyString(spec))
        {
            Check("spec 必须是非空字符串");
        }
        foreach (var field in new[] { "owner", "platform", "notes", "legacy_reason" })
        {
            if (node.TryGetValue(field, out var value) && value is not string)
            {
                Check(field + " 必须是字符串");
            }
        }
        foreach (var field in new[] { "authority", "replaced_by" })
        {
            if (node.TryGetValue(field, out var value) && value is not null && !PyValue.NonEmptyString(value))
            {
                Check(field + " 必须是节点 id 或 null（实际 " + PyValue.Repr(value) + "）");
            }
        }
        if (node.TryGetValue("evidence", out var evidence))
        {
            if (evidence is not List<object?> evidenceList)
            {
                Check("evidence 必须是数组");
            }
            else
            {
                for (var i = 0; i < evidenceList.Count; i++)
                {
                    if (evidenceList[i] is not RawMap entry)
                    {
                        Check("evidence[" + i + "] 必须是映射");
                        continue;
                    }
                    foreach (var key in EvidenceRequired)
                    {
                        if (!entry.ContainsKey(key))
                        {
                            Check("evidence[" + i + "] 缺少字段：" + key);
                        }
                    }
                    foreach (var key in entry.Keys)
                    {
                        if (!EvidenceAllowed.Contains(key))
                        {
                            Check("evidence[" + i + "] 含未知字段：" + key);
                        }
                    }
                    if (entry.TryGetValue("test", out var test) && !PyValue.NonEmptyString(test))
                    {
                        Check("evidence[" + i + "].test 必须是非空字符串");
                    }
                    if (entry.TryGetValue("at", out var at))
                    {
                        if (!(at is string atText && Matches(DateRe, atText) && IsValidDate(atText)))
                        {
                            Check("evidence[" + i + "].at 必须是合法日期 YYYY-MM-DD（实际 " + PyValue.Repr(at) + "）");
                        }
                    }
                    if (entry.TryGetValue("result", out var result) &&
                        !(result is string resultText && resultText is "pass" or "fail"))
                    {
                        Check("evidence[" + i + "].result 必须是 pass/fail（实际 " + PyValue.Repr(result) + "）");
                    }
                    if (entry.TryGetValue("note", out var note) && note is not string)
                    {
                        Check("evidence[" + i + "].note 必须是字符串");
                    }
                }
            }
        }
        var nid = NodeId(node);
        if (nid is not null)
        {
            var rootId = RootId;
            var nodeKind = node.TryGetValue("kind", out var k) ? k : null;
            var parent = node.TryGetValue("parent", out var p) ? p : null;
            var isRoot = PyValue.EqualsPy(nodeKind, "root") || PyValue.EqualsPy(nid, rootId);
            var topLevel = rootId is string rootIdText && parent is string parentText &&
                           string.Equals(parentText, rootIdText, StringComparison.Ordinal);
            if (isRoot)
            {
                if (!Matches(RootIdRe, nid))
                {
                    Check("root 节点 id 必须是单段小写字母/数字/连字符（实际 " + PyValue.Repr(nid) + "）");
                }
            }
            else if (topLevel)
            {
                if (!Matches(RootIdRe, nid))
                {
                    Check("root 直接子节点 id 必须是单段小写字母/数字/连字符（实际 " + PyValue.Repr(nid) + "）");
                }
            }
            else
            {
                if (!Matches(ContractRefRe, nid))
                {
                    Check("节点 id 格式不合法：" + PyValue.Repr(nid) + "（期望 parent 前缀 + 点 + 末段）");
                }
                if (parent is string parentId && parentId.Length > 0)
                {
                    var prefix = parentId + ".";
                    if (!nid.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        Add("I", "R02", "命名建议：节点 id 以 parent 前缀开头更清晰，期望 " + prefix + "<末段>，实际 " + PyValue.Repr(nid), nid);
                    }
                    else
                    {
                        var last = nid[prefix.Length..];
                        if (last.Length == 0 || last.Contains('.') || !Matches(SegmentRe, last))
                        {
                            Check("节点 id 末段必须单段小写字母/数字/连字符：" + PyValue.Repr(nid));
                        }
                    }
                }
            }
        }
    }

    private void R02()
    {
        for (var index = 0; index < _nodes.Count; index++)
        {
            CheckNodeShape(_nodes[index], index);
        }
    }

    // ------------------------------------------------------------------
    // R03 id 唯一 / R04 根节点 / R05 父子完整性 / R06 层级与类型
    // ------------------------------------------------------------------

    private void R03()
    {
        var buckets = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var order = new List<string>();
        for (var index = 0; index < _nodes.Count; index++)
        {
            if (_nodes[index] is not RawMap node || NodeId(node) is not { } nid || nid.Length == 0)
            {
                continue;
            }
            if (!buckets.TryGetValue(nid, out var indexes))
            {
                indexes = new List<long>();
                buckets[nid] = indexes;
                order.Add(nid);
            }
            indexes.Add(index);
        }
        foreach (var nid in order)
        {
            var indexes = buckets[nid];
            if (indexes.Count > 1)
            {
                Add("E", "R03",
                    "节点 id 重复 " + indexes.Count + " 次（nodes 下标 " +
                    PyValue.Str(indexes.Cast<object?>().ToList()) + "）", nid);
            }
        }
    }

    private void R04()
    {
        var rootId = RootId;
        if (!(rootId is string rootText && rootText.Length > 0))
        {
            Add("E", "R04", "root 字段必须是非空字符串");
            return;
        }
        if (!_nodeMap.TryGetValue(rootText, out var rootNode))
        {
            Add("E", "R04", "root 指向的节点不存在：" + rootText, rootText);
        }
        else
        {
            var kind = rootNode.TryGetValue("kind", out var k) ? k : null;
            if (!PyValue.EqualsPy(kind, "root"))
            {
                Add("E", "R04", "root 节点 kind 必须为 root（实际 " + PyValue.Repr(kind) + "）", rootText);
            }
            var layer = rootNode.TryGetValue("layer", out var l) ? l : null;
            if (!PyValue.EqualsPy(layer, 0L))
            {
                Add("E", "R04", "root 节点 layer 必须为 0（实际 " + PyValue.Repr(layer) + "）", rootText);
            }
            var parent = rootNode.TryGetValue("parent", out var p) ? p : null;
            if (parent is not null)
            {
                Add("E", "R04", "root 节点 parent 必须为 null（实际 " + PyValue.Repr(parent) + "）", rootText);
            }
        }
        var roots = new List<object?>();
        foreach (var item in _nodes)
        {
            if (item is RawMap node && node.TryGetValue("kind", out var kind) && PyValue.EqualsPy(kind, "root"))
            {
                var nid = NodeId(node);
                if (nid is not null && nid.Length > 0)
                {
                    roots.Add(nid);
                }
            }
        }
        if (roots.Count != 1)
        {
            Add("E", "R04", "全树只能有一个 kind=root（实际 " + roots.Count + " 个：" + PyValue.Str(roots) + "）");
        }
    }

    private void R05()
    {
        var flagged = new HashSet<string>(StringComparer.Ordinal);
        var rootId = RootId;
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            if (PyValue.EqualsPy(nid, rootId))
            {
                continue;
            }
            var parent = node.TryGetValue("parent", out var p) ? p : null;
            if (parent is null)
            {
                Add("E", "R05", "非 root 节点必须声明 parent（parent=null）", nid);
                flagged.Add(nid);
            }
            else if (parent is not string parentText || !_nodeMap.ContainsKey(parentText))
            {
                Add("E", "R05", "parent 不存在：" + PyValue.Repr(parent), nid);
                flagged.Add(nid);
            }
            else
            {
                var parentLayer = _nodeMap[parentText].TryGetValue("layer", out var pLayer) ? pLayer : null;
                var layer = node.TryGetValue("layer", out var l) ? l : null;
                if (PyValue.IsIntLike(parentLayer) && PyValue.IsIntLike(layer))
                {
                    var parentNumber = PyValue.AsLong(parentLayer)!.Value;
                    var layerNumber = PyValue.AsLong(layer)!.Value;
                    if (parentNumber != layerNumber - 1)
                    {
                        Add("W", "R05",
                            "parent 层级不匹配：parent " + parentText + " layer=" + PyValue.Str(parentLayer) +
                            "，本节点 layer=" + PyValue.Str(layer) + "，期望 parent.layer=" + (layerNumber - 1),
                            nid);
                        flagged.Add(nid);
                    }
                }
            }
        }
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        void Dfs(string nid)
        {
            state[nid] = 1;
            stack.Add(nid);
            var node = _nodeMap.TryGetValue(nid, out var current) ? current : null;
            if (node is not null && node.TryGetValue("parent", out var parentValue) && parentValue is string parentId &&
                _nodeMap.ContainsKey(parentId))
            {
                if (state.TryGetValue(parentId, out var parentState) && parentState == 1)
                {
                    var start = stack.IndexOf(parentId);
                    var cycle = stack.Skip(start).Concat(new[] { parentId }).ToList();
                    var key = string.Join("|", cycle.Distinct().OrderBy(value => value, StringComparer.Ordinal));
                    if (reported.Add(key))
                    {
                        Add("E", "R05", "parent 链成环：" + string.Join(" -> ", cycle), parentId);
                    }
                }
                else if (!state.TryGetValue(parentId, out var nextState) || nextState == 0)
                {
                    Dfs(parentId);
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[nid] = 2;
        }

        foreach (var nid in _nodeOrder)
        {
            if (!state.TryGetValue(nid, out var currentState) || currentState == 0)
            {
                Dfs(nid);
            }
        }
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var nid in _nodeOrder)
        {
            var node = _nodeMap[nid];
            if (node.TryGetValue("parent", out var parentValue) && parentValue is string parentId)
            {
                if (!children.TryGetValue(parentId, out var list))
                {
                    list = new List<string>();
                    children[parentId] = list;
                }
                list.Add(nid);
            }
        }
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        if (rootId is string rootText && _nodeMap.ContainsKey(rootText))
        {
            var queue = new List<string> { rootText };
            while (queue.Count > 0)
            {
                var current = queue[^1];
                queue.RemoveAt(queue.Count - 1);
                if (!reachable.Add(current))
                {
                    continue;
                }
                if (children.TryGetValue(current, out var kids))
                {
                    queue.AddRange(kids);
                }
            }
        }
        foreach (var nid in _nodeOrder)
        {
            if (!reachable.Contains(nid) && !flagged.Contains(nid))
            {
                Add("E", "R05", "节点从 root 不可达（孤儿节点）", nid);
            }
        }
    }

    private void R06()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var layer = node.TryGetValue("layer", out var l) ? l : null;
            var kind = node.TryGetValue("kind", out var k) ? k : null;
            if (!PyValue.IsBool(layer) && layer is long layerNumber && LayerKinds.ContainsKey(layerNumber) &&
                kind is string kindText)
            {
                if (!LayerKinds[layerNumber].Contains(kindText, StringComparer.Ordinal))
                {
                    var allowed = "[" + string.Join(", ", LayerKinds[layerNumber].Select(PyValue.Repr)) + "]";
                    Add("E", "R06",
                        "kind 与 layer 不匹配：layer=" + layerNumber + " 只允许 " + allowed + "（实际 kind=" + kindText + "）",
                        nid);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // R11 依赖图 / R12 落地阶段
    // ------------------------------------------------------------------

    private void R11()
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var graphOrder = new List<string>();
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var dependencies = node.TryGetValue("depends_on", out var value) && value is List<object?> list
                ? list
                : new List<object?>();
            if (!graph.ContainsKey(nid))
            {
                graphOrder.Add(nid);
            }
            var edges = new List<string>();
            graph[nid] = edges;
            foreach (var dependency in dependencies)
            {
                if (dependency is not string depText)
                {
                    continue;
                }
                if (string.Equals(depText, nid, StringComparison.Ordinal))
                {
                    Add("E", "R11", "depends_on 不得自依赖", nid);
                    continue;
                }
                if (!_nodeMap.ContainsKey(depText))
                {
                    Add("E", "R11", "depends_on 引用不存在的节点：" + depText, nid);
                    continue;
                }
                edges.Add(depText);
            }
        }
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        void Dfs(string nid)
        {
            state[nid] = 1;
            stack.Add(nid);
            if (graph.TryGetValue(nid, out var edges))
            {
                foreach (var dependency in edges)
                {
                    if (state.TryGetValue(dependency, out var dependencyState) && dependencyState == 1)
                    {
                        var start = stack.IndexOf(dependency);
                        var cycle = stack.Skip(start).Concat(new[] { dependency }).ToList();
                        var key = string.Join("|", cycle.Distinct().OrderBy(value => value, StringComparer.Ordinal));
                        if (reported.Add(key))
                        {
                            Add("E", "R11", "依赖成环：" + string.Join(" -> ", cycle), dependency);
                        }
                    }
                    else if (!state.TryGetValue(dependency, out var nextState) || nextState == 0)
                    {
                        Dfs(dependency);
                    }
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[nid] = 2;
        }

        foreach (var nid in graphOrder)
        {
            if (!state.TryGetValue(nid, out var currentState) || currentState == 0)
            {
                Dfs(nid);
            }
        }
    }

    private void R12()
    {
        var phaseIds = new List<string>();
        if (_manifest.TryGetValue("phases", out var phasesValue) && phasesValue is List<object?> phases)
        {
            foreach (var item in phases)
            {
                if (item is RawMap phase && phase.TryGetValue("id", out var idValue) && idValue is string phaseId)
                {
                    phaseIds.Add(phaseId);
                }
            }
        }
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < phaseIds.Count; index++)
        {
            order[phaseIds[index]] = index;
        }
        var phaseNumbers = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var phaseId in phaseIds)
        {
            if (Matches(PhaseRe, phaseId) && long.TryParse(phaseId[1..], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var number))
            {
                phaseNumbers[phaseId] = number;
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long? previousNumber = null;
        for (var index = 0; index < phaseIds.Count; index++)
        {
            var phaseId = phaseIds[index];
            if (!seen.Add(phaseId))
            {
                Add("E", "R12", "phases 中 id 重复：" + phaseId, phaseId);
            }
            if (!phaseNumbers.TryGetValue(phaseId, out var number))
            {
                continue;
            }
            if (previousNumber is not null && number <= previousNumber.Value)
            {
                Add("E", "R12",
                    "phases 必须按序号严格递增：" + phaseIds[index - 1] + " 之后出现 " + phaseId,
                    phaseId);
            }
            previousNumber = number;
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var phase = node.TryGetValue("phase", out var phaseValue) ? phaseValue : null;
            if (phase is not string phaseText)
            {
                continue;
            }
            if (!order.ContainsKey(phaseText))
            {
                Add("E", "R12", "phase 不在 phases 列表中：" + phaseText, nid);
            }
            else
            {
                used.Add(phaseText);
            }
        }
        foreach (var phaseId in phaseIds)
        {
            if (!used.Contains(phaseId))
            {
                Add("E", "R12", "phase 未被任何节点使用（空阶段应删除）：" + phaseId, phaseId);
            }
        }
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var phase = node.TryGetValue("phase", out var phaseValue) ? phaseValue : null;
            if (phase is not string phaseText || !order.ContainsKey(phaseText))
            {
                continue;
            }
            var dependencies = node.TryGetValue("depends_on", out var depsValue) && depsValue is List<object?> list
                ? list
                : new List<object?>();
            foreach (var dependency in dependencies)
            {
                if (dependency is not string depText || !_nodeMap.ContainsKey(depText))
                {
                    continue;
                }
                var depPhase = _nodeMap[depText].TryGetValue("phase", out var depPhaseValue) ? depPhaseValue : null;
                if (depPhase is string depPhaseText && order.ContainsKey(depPhaseText) &&
                    order[depPhaseText] > order[phaseText])
                {
                    Add("E", "R12",
                        "依赖阶段晚于本节点：依赖 " + depText + "(" + depPhaseText + ") 晚于本节点(" + phaseText + ")",
                        nid);
                }
            }
            var parent = node.TryGetValue("parent", out var parentValue) ? parentValue : null;
            if (parent is string parentText && _nodeMap.ContainsKey(parentText))
            {
                var parentPhase = _nodeMap[parentText].TryGetValue("phase", out var parentPhaseValue)
                    ? parentPhaseValue
                    : null;
                if (parentPhase is string parentPhaseText && order.ContainsKey(parentPhaseText) &&
                    order[parentPhaseText] > order[phaseText])
                {
                    Add("E", "R12",
                        "父节点阶段晚于子节点：parent " + parentText + "(" + parentPhaseText + ") 晚于本节点(" + phaseText + ")",
                        nid);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // R07 单节点职责规则
    // ------------------------------------------------------------------

    private static readonly Regex PlaceholderRe =
        new(@"(TBD|待补|待定|\?\?\?|FIXME)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DutyPunctuationRe =
        new(@"[;；。]", RegexOptions.CultureInvariant);

    private void R07()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            if (node.TryGetValue("responsibility", out var responsibility) && responsibility is string responsibilityText)
            {
                var text = responsibilityText.Trim();
                if (text.Length == 0)
                {
                    Add("E", "R07", "responsibility 不能为空", nid);
                }
                else
                {
                    var length = PyValue.Length(text);
                    if (length is < 4 or > 80)
                    {
                        Add("E", "R07", "responsibility 长度必须为 4-80 字（实际 " + length + "）", nid);
                    }
                    var placeholder = PlaceholderRe.Match(text);
                    if (placeholder.Success)
                    {
                        Add("E", "R07", "responsibility 含占位符：" + placeholder.Value, nid);
                    }
                    var firstRune = text.EnumerateRunes().First();
                    if (firstRune.Value is '的' or '了' or '是')
                    {
                        Add("E", "R07",
                            "responsibility 应以动词开头，不得以“的/了/是”开头：" + PyValue.Head(text, 8), nid);
                    }
                    if (DutyPunctuationRe.IsMatch(text))
                    {
                        Add("E", "R07", "responsibility 只能写一句职责，不得含分号或句号", nid);
                    }
                }
            }
            var kind = node.TryGetValue("kind", out var kindValue) ? kindValue : null;
            if (kind is string kindText && (kindText == "program" || kindText == "step"))
            {
                var notResponsible = node.TryGetValue("not_responsible", out var nrValue) ? nrValue : null;
                if (notResponsible is not List<object?> notResponsibleList || notResponsibleList.Count == 0)
                {
                    Add("E", "R07", "program/step 必须至少 1 条 not_responsible", nid);
                }
                else
                {
                    for (var index = 0; index < notResponsibleList.Count; index++)
                    {
                        if (notResponsibleList[index] is not string entry || PyValue.Length(entry.Trim()) < 2)
                        {
                            Add("E", "R07", "not_responsible[" + index + "] 必须至少 2 字", nid);
                        }
                    }
                }
                var acceptance = node.TryGetValue("acceptance", out var acceptanceValue) ? acceptanceValue : null;
                var hasAcceptance = acceptance is List<object?> acceptanceList &&
                                    acceptanceList.Any(PyValue.NonEmptyString);
                var status = node.TryGetValue("status", out var statusValue) ? statusValue : null;
                var exempt = status is string statusText && (statusText == "退役" || statusText == "退役");
                if (!hasAcceptance && !exempt)
                {
                    Add("E", "R07", "program/step 必须有 acceptance", nid);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // R08 跨节点职责重复
    // ------------------------------------------------------------------

    private void R08()
    {
        var candidates = new List<(string Id, string Normalized)>();
        foreach (var item in _nodes)
        {
            if (item is not RawMap node)
            {
                continue;
            }
            if (node.TryGetValue("kind", out var kind) && PyValue.EqualsPy(kind, "external"))
            {
                continue;
            }
            var nid = NodeId(node);
            if (nid is null)
            {
                continue;
            }
            if (!node.TryGetValue("responsibility", out var responsibility) || responsibility is not string text)
            {
                continue;
            }
            var normalized = PythonText.Normalize(text);
            if (normalized.Length == 0)
            {
                continue;
            }
            candidates.Add((nid, normalized));
        }
        for (var i = 0; i < candidates.Count; i++)
        {
            var (idA, normA) = candidates[i];
            for (var j = i + 1; j < candidates.Count; j++)
            {
                var (idB, normB) = candidates[j];
                if (string.Equals(normA, normB, StringComparison.Ordinal))
                {
                    Add("E", "R08", "职责重复：" + idA + " 与 " + idB + " 归一化后完全相同", idA);
                    continue;
                }
                if (PyValue.Length(normA) < 4 || PyValue.Length(normB) < 4)
                {
                    continue;
                }
                var similarity = PythonText.Jaccard(normA, normB);
                if (similarity >= 0.90)
                {
                    Add("W", "R08",
                        "职责高度相似：" + idA + " ≈ " + idB + "（相似度 " + PythonText.Format2(similarity) + "）",
                        idA);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // R09 契约引用 / R10 契约双向一致性
    // ------------------------------------------------------------------

    private List<object?> ContractItems()
    {
        if (_repository.ContractsRaw is RawMap document &&
            document.TryGetValue("contracts", out var value) && value is List<object?> list)
        {
            return list;
        }
        return _repository.ContractsRaw is List<object?> rootList ? rootList : new List<object?>();
    }

    private Dictionary<string, RawMap> ContractMap()
    {
        var mapping = new Dictionary<string, RawMap>(StringComparer.Ordinal);
        foreach (var item in ContractItems())
        {
            if (item is RawMap contract && contract.TryGetValue("id", out var idValue) && idValue is string id)
            {
                mapping.TryAdd(id, contract);
            }
        }
        return mapping;
    }

    private void R09()
    {
        if (_repository.ContractsRaw is null)
        {
            Add("E", "R09",
                "contracts.yaml 不存在或未加载，无法校验契约引用：" + _repository.ContractsPath, null);
            return;
        }
        var contractIds = new HashSet<string>(ContractMap().Keys, StringComparer.Ordinal);
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            foreach (var field in new[] { "inputs", "outputs" })
            {
                if (!node.TryGetValue(field, out var refsValue) || refsValue is not List<object?> refs)
                {
                    continue;
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var reference in refs)
                {
                    if (reference is not string referenceText)
                    {
                        continue;
                    }
                    if (!seen.Add(referenceText))
                    {
                        Add("E", "R09", field + " 内重复引用同一契约：" + referenceText, nid);
                    }
                    if (!contractIds.Contains(referenceText))
                    {
                        Add("E", "R09", field + " 引用未定义契约：" + referenceText, nid);
                    }
                }
            }
            var kind = node.TryGetValue("kind", out var kindValue) ? kindValue : null;
            if (kind is string kindText && (kindText == "program" || kindText == "step"))
            {
                var outputs = node.TryGetValue("outputs", out var outputsValue) ? outputsValue : null;
                var hasOutput = outputs is List<object?> outputsList &&
                                outputsList.Any(entry => entry is string text && text.Length > 0);
                var status = node.TryGetValue("status", out var statusValue) ? statusValue : null;
                var exempt = status is string statusText && (statusText == "退役" || statusText == "退役");
                var presetNone = node.TryGetValue("preset", out var presetFlag) && presetFlag is true &&
                                 string.Equals(StringOf(node, "preset_outputs"), "none", StringComparison.Ordinal);
                if (!hasOutput && !exempt && !presetNone)
                {
                    Add("E", "R09", "program/step 至少需要 1 个 output 契约", nid);
                }
            }
        }
    }

    private static object? ContractFields(RawMap contract)
    {
        if (contract.TryGetValue("fields", out var fields) && fields is not null)
        {
            return fields;
        }
        if (contract.TryGetValue("schema", out var schema) && schema is RawMap schemaMap)
        {
            return schemaMap.TryGetValue("fields", out var schemaFields) ? schemaFields : null;
        }
        return null;
    }

    private static object? ContractRequired(RawMap contract)
    {
        if (contract.TryGetValue("required", out var required) && required is not null)
        {
            return required;
        }
        if (contract.TryGetValue("schema", out var schema) && schema is RawMap schemaMap)
        {
            return schemaMap.TryGetValue("required", out var schemaRequired) ? schemaRequired : null;
        }
        return null;
    }

    private void CheckContractShape(RawMap contract, int index)
    {
        var idValue = contract.TryGetValue("id", out var rawId) ? rawId : null;
        var label = idValue is string idText && idText.Length > 0 ? idText : "[" + index + "]";
        if (!(idValue is string contractId && Matches(ContractRefRe, contractId)))
        {
            Add("E", "R10",
                "契约 id 格式不合法：" + PyValue.Repr(idValue) + "（期望小写字母/数字/连字符 + 点分段）",
                label);
        }
        var purpose = contract.TryGetValue("purpose", out var purposeValue) ? purposeValue : null;
        if (!PyValue.NonEmptyString(purpose))
        {
            Add("E", "R10", "契约 purpose 不能为空", label);
        }
        var fields = ContractFields(contract);
        if (fields is not RawMap fieldsMap || fieldsMap.Count == 0)
        {
            var actual = fields is null ? null : PyValue.TypeName(fields);
            Add("E", "R10",
                "契约 fields 必须是非空映射（实际 " + PyValue.Repr(actual) + "）", label);
        }
        var required = ContractRequired(contract);
        if (required is not null)
        {
            if (required is not List<object?> requiredList ||
                requiredList.Any(entry => entry is not string))
            {
                Add("E", "R10", "契约 required 必须是字符串数组", label);
            }
            else if (fields is RawMap declaredFields)
            {
                var missing = requiredList
                    .Where(entry => entry is string name && !declaredFields.ContainsKey(name))
                    .ToList();
                if (missing.Count > 0)
                {
                    Add("E", "R10", "契约 required 不在 fields 中：" + PyValue.Str(missing), label);
                }
            }
        }
        var producersValue = contract.TryGetValue("producers", out var producersRaw)
            ? producersRaw
            : new List<object?>();
        var consumersValue = contract.TryGetValue("consumers", out var consumersRaw)
            ? consumersRaw
            : new List<object?>();
        if (producersValue is not List<object?> producers ||
            producers.Any(entry => entry is not string))
        {
            Add("E", "R10", "契约 producers 必须是字符串数组", label);
            producers = new List<object?>();
        }
        if (consumersValue is not List<object?> consumers ||
            consumers.Any(entry => entry is not string))
        {
            Add("E", "R10", "契约 consumers 必须是字符串数组", label);
            consumers = new List<object?>();
        }
        var external = contract.TryGetValue("external", out var externalValue) && PyValue.IsTruthy(externalValue);
        var externalConsumers = contract.TryGetValue("external_consumers", out var externalConsumersValue) &&
                                PyValue.IsTruthy(externalConsumersValue);
        if (!external && producers.Count == 0)
        {
            Add("E", "R10", "契约缺少 producer（且未标记 external: true）", label);
        }
        if (!externalConsumers && consumers.Count == 0)
        {
            Add("E", "R10", "契约缺少 consumer（且未标记 external_consumers: true）", label);
        }
    }

    private void R10()
    {
        if (_repository.ContractsRaw is null)
        {
            return;
        }
        var contracts = ContractItems();
        var nodeIds = new HashSet<string>(_nodeMap.Keys, StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < contracts.Count; index++)
        {
            if (contracts[index] is not RawMap contract)
            {
                Add("E", "R10", "contracts[" + index + "] 必须是映射", null);
                continue;
            }
            var idValue = contract.TryGetValue("id", out var rawId) ? rawId : null;
            if (idValue is string contractId && contractId.Length > 0)
            {
                if (!seenIds.Add(contractId))
                {
                    Add("E", "R10", "契约 id 重复：" + contractId, contractId);
                }
            }
            CheckContractShape(contract, index);
            foreach (var field in new[] { "producers", "consumers" })
            {
                if (!contract.TryGetValue(field, out var values) || values is not List<object?> references)
                {
                    continue;
                }
                foreach (var reference in references)
                {
                    if (reference is string referenceText && !nodeIds.Contains(referenceText))
                    {
                        Add("E", "R10", "契约 " + field + " 引用不存在的节点：" + referenceText,
                            idValue is string idText ? idText : null);
                    }
                }
            }
        }
        var contractMap = ContractMap();
        foreach (var entry in contractMap)
        {
            var cid = entry.Key;
            var contract = entry.Value;
            foreach (var pair in new[] { (Field: "producers", NodeField: "outputs"), (Field: "consumers", NodeField: "inputs") })
            {
                if (!contract.TryGetValue(pair.Field, out var values) || values is not List<object?> references)
                {
                    continue;
                }
                foreach (var reference in references)
                {
                    if (reference is not string referenceText || !_nodeMap.TryGetValue(referenceText, out var node))
                    {
                        continue;
                    }
                    var declared = node.TryGetValue(pair.NodeField, out var declaredValue) &&
                                   declaredValue is List<object?> declaredList
                        ? declaredList
                        : new List<object?>();
                    if (!declared.Any(value => value is string text && text == cid))
                    {
                        Add("E", "R10",
                            "契约 " + cid + " 的 " + pair.Field + " 含节点 " + referenceText +
                            "，但该节点 " + pair.NodeField + " 未声明此契约（反向不一致）",
                            referenceText);
                    }
                }
            }
        }
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            if (node.TryGetValue("preset", out var presetValue) && presetValue is true)
            {
                continue;
            }
            foreach (var pair in new[] { (Field: "outputs", ContractField: "producers"), (Field: "inputs", ContractField: "consumers") })
            {
                if (!node.TryGetValue(pair.Field, out var values) || values is not List<object?> references)
                {
                    continue;
                }
                foreach (var reference in references)
                {
                    if (reference is not string referenceText ||
                        !contractMap.TryGetValue(referenceText, out var contract))
                    {
                        continue;
                    }
                    var declared = contract.TryGetValue(pair.ContractField, out var declaredValue) &&
                                   declaredValue is List<object?> declaredList
                        ? declaredList
                        : null;
                    var present = declared is not null &&
                                  declared.Any(value => value is string text && text == nid);
                    if (!present)
                    {
                        var prefix = pair.Field == "outputs" ? "节点输出契约 " : "节点输入契约 ";
                        Add("E", "R10",
                            prefix + referenceText + " 的 " + pair.ContractField + " 缺少本节点（双向不一致）",
                            nid);
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // R14 验收与证据 / R16 父子完成度
    // ------------------------------------------------------------------

    private void R14()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var status = node.TryGetValue("status", out var statusValue) ? statusValue : null;
            var acceptance = node.TryGetValue("acceptance", out var acceptanceValue) ? acceptanceValue : null;
            var hasAcceptance = acceptance is List<object?> acceptanceList &&
                                acceptanceList.Any(PyValue.NonEmptyString);
            if (status is string statusText && (statusText == "已完成" || statusText == "已验证") && !hasAcceptance)
            {
                Add("E", "R14", "built/verified 节点 acceptance 至少 1 条且非空", nid);
            }
            if (status is not string verifiedText || verifiedText != "已验证")
            {
                continue;
            }
            var evidence = node.TryGetValue("evidence", out var evidenceValue) && evidenceValue is List<object?> list
                ? list
                : new List<object?>();
            for (var index = 0; index < evidence.Count; index++)
            {
                if (evidence[index] is not RawMap entry)
                {
                    Add("E", "R14", "evidence[" + index + "] 必须是映射", nid);
                    continue;
                }
                var test = entry.TryGetValue("test", out var testValue) ? testValue : null;
                if (!PyValue.NonEmptyString(test))
                {
                    Add("E", "R14", "evidence[" + index + "].test 不能为空", nid);
                }
                var at = entry.TryGetValue("at", out var atValue) ? atValue : null;
                if (!(at is string atText && Matches(DateRe, atText) && IsValidDate(atText)))
                {
                    Add("E", "R14",
                        "evidence[" + index + "].at 必须是合法日期 YYYY-MM-DD（实际 " + PyValue.Repr(at) + "）",
                        nid);
                }
                var result = entry.TryGetValue("result", out var resultValue) ? resultValue : null;
                if (!PyValue.EqualsPy(result, "pass"))
                {
                    Add("E", "R14",
                        "evidence[" + index + "].result 必须为 pass（实际 " + PyValue.Repr(result) + "）",
                        nid);
                }
            }
        }
    }

    private void R16()
    {
        var children = new Dictionary<string, List<RawMap>>(StringComparer.Ordinal);
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var parent = node.TryGetValue("parent", out var parentValue) ? parentValue : null;
            if (parent is not string parentText)
            {
                continue;
            }
            if (!children.TryGetValue(parentText, out var list))
            {
                list = new List<RawMap>();
                children[parentText] = list;
            }
            list.Add(node);
        }
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            if (!children.TryGetValue(nid, out var kids) || kids.Count == 0)
            {
                continue;
            }
            var status = node.TryGetValue("status", out var statusValue) ? statusValue : null;
            if (status is string verifiedText && verifiedText == "已验证")
            {
                var bad = new List<object?>();
                foreach (var kid in kids)
                {
                    var kidStatus = kid.TryGetValue("status", out var kidStatusValue) ? kidStatusValue : null;
                    var complete = kidStatus is string text &&
                                   (text == "已完成" || text == "已验证" || text == "退役");
                    if (!complete)
                    {
                        bad.Add(NodeId(kid));
                    }
                }
                if (bad.Count > 0)
                {
                    Add("E", "R16", "父节点 verified 但子节点未完成：" + PyValue.Str(bad), nid);
                }
            }
            else if (status is string builtText && builtText == "已完成")
            {
                var bad = new List<object?>();
                foreach (var kid in kids)
                {
                    var kidStatus = kid.TryGetValue("status", out var kidStatusValue) ? kidStatusValue : null;
                    if (kidStatus is string text && (text == "未完成" || text == "未完成"))
                    {
                        bad.Add(NodeId(kid));
                    }
                }
                if (bad.Count > 0)
                {
                    Add("W", "R16", "父节点 built 但存在未完成子节点：" + PyValue.Str(bad), nid);
                }
            }
            else if (status is string plannedText && plannedText == "__已停用__")
            {
                var bad = new List<object?>();
                foreach (var kid in kids)
                {
                    var kidStatus = kid.TryGetValue("status", out var kidStatusValue) ? kidStatusValue : null;
                    if (kidStatus is string text && (text == "已完成" || text == "已验证"))
                    {
                        bad.Add(NodeId(kid));
                    }
                }
                if (bad.Count > 0)
                {
                    Add("E", "R16", "父节点 planned 但子节点已落地：" + PyValue.Str(bad) + "（必须提升父状态）", nid);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // R13 状态与产物一致性
    // ------------------------------------------------------------------

    private static bool IsPythonAbsolutePath(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal) ||
           (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');

    private string ResolvePath(string relative)
    {
        var trimmed = relative.Trim();
        return IsPythonAbsolutePath(trimmed) ? trimmed : Path.Combine(_repository.Root, trimmed);
    }

    private bool PathExists(object? relative)
    {
        if (relative is not string text || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var path = ResolvePath(text);
        return File.Exists(path) || Directory.Exists(path);
    }

    private static bool AcceptanceOk(RawMap node)
    {
        return node.TryGetValue("acceptance", out var acceptance) && acceptance is List<object?> list &&
               list.Any(PyValue.NonEmptyString);
    }

    private void R13()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var status = node.TryGetValue("status", out var statusValue) ? statusValue : null;
            var files = node.TryGetValue("files", out var filesValue) && filesValue is List<object?> fileList
                ? fileList
                : new List<object?>();
            var evidence = node.TryGetValue("evidence", out var evidenceValue) && evidenceValue is List<object?> evidenceList
                ? evidenceList
                : new List<object?>();
            if (status is string landedText && (landedText == "已完成" || landedText == "已验证"))
            {
                foreach (var relative in files)
                {
                    if (!PathExists(relative))
                    {
                        Add("E", "R13", "files 路径不存在：" + PyValue.Str(relative), nid);
                    }
                }
                if (!AcceptanceOk(node))
                {
                    Add("E", "R13", "built/verified 节点 acceptance 不能为空", nid);
                }
                var dependsOn = node.TryGetValue("depends_on", out var dependsValue) &&
                                dependsValue is List<object?> dependencyList
                    ? dependencyList
                    : new List<object?>();
                foreach (var dependency in dependsOn)
                {
                    if (dependency is not string depText || !_nodeMap.TryGetValue(depText, out var dependencyNode))
                    {
                        continue;
                    }
                    var depStatus = dependencyNode.TryGetValue("status", out var depStatusValue) ? depStatusValue : null;
                    if (depStatus is string depStatusText && depStatusText == "退役")
                    {
                        Add("E", "R13",
                            landedText + " 节点不得依赖未落地节点：" + depText + " status=" + PyValue.Str(depStatus),
                            nid);
                    }
                }
                if (landedText == "已验证" && evidence.Count == 0)
                {
                    Add("E", "R13", "verified 节点 evidence 至少需要 1 条", nid);
                }
            }
            else if (status is string legacyText && legacyText == "退役")
            {
                var reason = node.TryGetValue("legacy_reason", out var reasonValue) ? reasonValue : null;
                if (reason is not string reasonText || PyValue.Length(reasonText.Trim()) < 8)
                {
                    Add("E", "R13", "legacy 节点必须登记 legacy_reason（至少 8 字）", nid);
                }
                if (!node.ContainsKey("authority"))
                {
                    Add("E", "R13", "legacy 节点必须登记 authority（节点 id 或 null）", nid);
                }
                else
                {
                    var authority = node.TryGetValue("authority", out var authorityValue) ? authorityValue : null;
                    if (authority is not null)
                    {
                        if (authority is not string authorityText || !_nodeMap.TryGetValue(authorityText, out var target))
                        {
                            Add("E", "R13",
                                "legacy authority 指向不存在的节点：" + PyValue.Repr(authority), nid);
                        }
                        else
                        {
                            var targetKind = target.TryGetValue("kind", out var kindValue) ? kindValue : null;
                            if (!(targetKind is string kindText && (kindText == "program" || kindText == "step")))
                            {
                                Add("E", "R13",
                                    "legacy authority 必须指向 program/step 节点：" + authorityText + " kind=" +
                                    PyValue.Repr(targetKind), nid);
                            }
                            var targetStatus = target.TryGetValue("status", out var targetStatusValue)
                                ? targetStatusValue
                                : null;
                            if (!(targetStatus is string targetStatusText &&
                                  (targetStatusText == "已完成" || targetStatusText == "已验证")))
                            {
                                Add("E", "R13",
                                    "legacy authority 必须指向 built/verified 节点：" + authorityText + " status=" +
                                    PyValue.Repr(targetStatus), nid);
                            }
                        }
                    }
                }
            }
            else if (status is string deprecatedText && deprecatedText == "退役")
            {
                if (!node.ContainsKey("replaced_by"))
                {
                    Add("E", "R13", "deprecated 节点必须登记 replaced_by（节点 id 或 null）", nid);
                }
                else
                {
                    var replaced = node.TryGetValue("replaced_by", out var replacedValue) ? replacedValue : null;
                    if (replaced is not null)
                    {
                        if (replaced is not string replacedText || !_nodeMap.TryGetValue(replacedText, out var target))
                        {
                            Add("E", "R13",
                                "deprecated replaced_by 指向不存在的节点：" + PyValue.Repr(replaced), nid);
                        }
                        else
                        {
                            var targetStatus = target.TryGetValue("status", out var targetStatusValue)
                                ? targetStatusValue
                                : null;
                            if (!(targetStatus is string targetStatusText &&
                                  (targetStatusText == "已完成" || targetStatusText == "已验证")))
                            {
                                Add("E", "R13",
                                    "deprecated replaced_by 必须指向 built/verified 节点：" + replacedText + " status=" +
                                    PyValue.Repr(targetStatus), nid);
                            }
                        }
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // R15 spec 路径 / 目录类锚点
    // ------------------------------------------------------------------

    private void R15()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var spec = node.TryGetValue("spec", out var specValue) ? specValue : null;
            if (!(spec is string specText && !string.IsNullOrWhiteSpace(specText)))
            {
                Add("E", "R15", "spec 不能为空", nid);
                continue;
            }
            var pathPart = specText.Split('#', 2)[0].Trim();
            if (pathPart.Length == 0)
            {
                Add("E", "R15", "spec 路径部分为空：" + PyValue.Repr(specText), nid);
                continue;
            }
            var target = ResolvePath(pathPart);
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                Add("E", "R15", "spec 路径不存在（相对仓库根）：" + pathPart, nid);
            }
            else if (Directory.Exists(target) && specText.IndexOf('#') < 0)
            {
                Add("W", "R15", "目录类 spec 应带 #章节锚点：" + specText, nid);
            }
        }
    }

    // ------------------------------------------------------------------
    // R17 home 归一化与存在性
    // ------------------------------------------------------------------

    private string ExpectedHome(string nid)
    {
        var rootId = RootId;
        var relative = nid;
        if (rootId is string rootText && nid.StartsWith(rootText + ".", StringComparison.Ordinal))
        {
            relative = nid[(rootText.Length + 1)..];
        }
        return "10-程序/" + relative.Replace('.', '/');
    }

    private void R17()
    {
        var seenHomes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var kind = node.TryGetValue("kind", out var kindValue) ? kindValue : null;
            if (!(kind is string kindText && kindText == "program"))
            {
                continue;
            }
            var status = node.TryGetValue("status", out var statusValue) ? statusValue : null;
            var home = node.TryGetValue("home", out var homeValue) ? homeValue : null;
            if (!(home is string homeText && !string.IsNullOrWhiteSpace(homeText)))
            {
                continue;
            }
            var normalized = homeText.Replace('\\', '/').Trim().TrimEnd('/');
            var hasDrivePrefix = homeText.Length >= 2 && char.IsLetter(homeText[0]) && homeText[1] == ':';
            if (IsPythonAbsolutePath(homeText) || hasDrivePrefix)
            {
                Add("E", "R17", "home 必须是相对路径（实际 " + homeText + "）", nid);
            }
            if (!normalized.StartsWith("10-程序/", StringComparison.Ordinal))
            {
                Add("E", "R17", "home 必须以 10-程序/ 开头（实际 " + normalized + "）", nid);
            }
            var expected = ExpectedHome(nid);
            if (!string.Equals(normalized, expected, StringComparison.Ordinal))
            {
                Add("E", "R17", "home 期望值 " + expected + "（实际 " + normalized + "）", nid);
            }
            if (seenHomes.TryGetValue(normalized, out var previous) &&
                !string.Equals(previous, nid, StringComparison.Ordinal))
            {
                Add("E", "R17", "home 被多个节点声明：" + normalized + "（" + previous + " 与 " + nid + "）", nid);
            }
            else
            {
                seenHomes[normalized] = nid;
            }
            var homePath = ResolvePath(normalized);
            var existsAsDirectory = Directory.Exists(homePath);
            if (status is string statusText &&
                (statusText == "已完成" || statusText == "已验证") &&
                !existsAsDirectory)
            {
                Add("E", "R17", "status=" + statusText + " 时 home 目录必须存在：" + normalized, nid);
            }
            if (existsAsDirectory)
            {
                if (!File.Exists(Path.Combine(homePath, "SPEC.md")))
                {
                    Add("E", "R17", "home 下缺少 SPEC.md：" + normalized, nid);
                }
                if (!File.Exists(Path.Combine(homePath, "program.yaml")))
                {
                    Add("E", "R17", "home 下缺少 program.yaml：" + normalized, nid);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // R21 契约版本 / R22 阶段覆盖
    // ------------------------------------------------------------------

    private List<string> PhaseIds()
    {
        var result = new List<string>();
        if (_manifest.TryGetValue("phases", out var phasesValue) && phasesValue is List<object?> phases)
        {
            foreach (var item in phases)
            {
                if (item is RawMap phase && phase.TryGetValue("id", out var idValue) && idValue is string phaseId)
                {
                    result.Add(phaseId);
                }
            }
        }
        return result;
    }

    private void R21()
    {
        foreach (var item in ContractItems())
        {
            if (item is not RawMap contract)
            {
                continue;
            }
            var idValue = contract.TryGetValue("id", out var rawId) ? rawId : null;
            var node = idValue is string contractId ? contractId : null;
            var version = contract.TryGetValue("version", out var versionValue) ? versionValue : null;
            if (!(version is string versionText && Matches(VersionRe, versionText)))
            {
                Add("W", "R21",
                    "契约 version 必须形如 1.0.0（实际 " + PyValue.Repr(version) + "）", node);
            }
        }
    }

    private void R22()
    {
        var nodesByPhase = new Dictionary<string, List<RawMap>>(StringComparer.Ordinal);
        foreach (var item in _nodes)
        {
            if (item is not RawMap node)
            {
                continue;
            }
            var phase = node.TryGetValue("phase", out var phaseValue) ? phaseValue : null;
            if (phase is not string phaseText)
            {
                continue;
            }
            if (!nodesByPhase.TryGetValue(phaseText, out var list))
            {
                list = new List<RawMap>();
                nodesByPhase[phaseText] = list;
            }
            list.Add(node);
        }
        foreach (var phaseId in PhaseIds())
        {
            if (!nodesByPhase.TryGetValue(phaseId, out var nodes) || nodes.Count == 0)
            {
                continue;
            }
            var advancing = false;
            var completed = true;
            foreach (var node in nodes)
            {
                var status = node.TryGetValue("status", out var statusValue) ? statusValue : null;
                if (status is string statusText && (statusText == "未完成" || statusText == "未完成"))
                {
                    advancing = true;
                }
                if (!(status is string completedText &&
                      (completedText == "已完成" || completedText == "已验证" ||
                       completedText == "退役" || completedText == "退役")))
                {
                    completed = false;
                }
            }
            if (!advancing && !completed)
            {
                Add("W", "R22",
                    "phase 内既无正在推进的节点，也未全部 built/verified：" + phaseId, phaseId);
            }
        }
    }

    // ------------------------------------------------------------------
    // R18 SPEC.md（program.yaml 对比暂未迁移）
    // ------------------------------------------------------------------

    private static readonly string[] SpecHeadings =
    {
        "职责", "不负责", "输入", "输出", "依赖", "边界与失败模式", "验收", "落地阶段",
    };

    private static readonly string[] NodeCompareFields =
    {
        "id", "name", "kind", "layer", "parent", "phase", "status",
        "responsibility", "inputs", "outputs", "depends_on",
    };

    private static readonly Regex RuleForExemptionRe =
        new(@"^R(0[1-9]|1[0-9]|2[0-4])$", RegexOptions.CultureInvariant);

    private static readonly Regex MarkdownHeadingRe =
        new(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.CultureInvariant);

    private void R18()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var kind = node.TryGetValue("kind", out var kindValue) ? kindValue : null;
            if (!(kind is string kindText && kindText == "program"))
            {
                continue;
            }
            var home = node.TryGetValue("home", out var homeValue) ? homeValue : null;
            if (home is not string homeText || string.IsNullOrWhiteSpace(homeText))
            {
                continue;
            }
            var normalized = homeText.Replace('\\', '/').Trim().TrimEnd('/');
            if (!normalized.StartsWith("10-程序/", StringComparison.Ordinal))
            {
                continue;
            }
            var homePath = ResolvePath(normalized);
            if (!Directory.Exists(homePath))
            {
                continue;
            }
            var specPath = Path.Combine(homePath, "SPEC.md");
            if (File.Exists(specPath))
            {
            string text;
            try
            {
                var bytes = File.ReadAllBytes(specPath);
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                Add("E", "R18", "SPEC.md 无法读取：" + exception.Message, nid);
                continue;
            }
            var lines = PythonText.SplitLines(text);
            var headings = new List<(int Level, string Title, int Index)>();
            for (var index = 0; index < lines.Count; index++)
            {
                var match = MarkdownHeadingRe.Match(lines[index]);
                if (match.Success)
                {
                    headings.Add((match.Groups[1].Value.Length, match.Groups[2].Value.Trim(), index));
                }
            }
            var h2Titles = headings.Where(heading => heading.Level == 2)
                .Select(heading => heading.Title)
                .ToList();
            var missing = SpecHeadings.Where(title => !h2Titles.Contains(title, StringComparer.Ordinal)).ToList();
            if (missing.Count > 0)
            {
                Add("E", "R18",
                    "SPEC.md 缺少二级标题：" + PyValue.Str(missing.Cast<object?>().ToList()) + "（8 个标题必须文字精确）",
                    nid);
            }
            foreach (var wanted in SpecHeadings)
            {
                var position = headings.FirstOrDefault(heading =>
                    heading.Level == 2 && string.Equals(heading.Title, wanted, StringComparison.Ordinal)).Index;
                if (!headings.Any(heading =>
                        heading.Level == 2 && string.Equals(heading.Title, wanted, StringComparison.Ordinal)))
                {
                    continue;
                }
                var contentLines = new List<string>();
                for (var index = position + 1; index < lines.Count; index++)
                {
                    var match = MarkdownHeadingRe.Match(lines[index]);
                    if (match.Success && match.Groups[1].Value.Length <= 2)
                    {
                        break;
                    }
                    contentLines.Add(lines[index]);
                }
                var content = string.Join("\n", contentLines).Trim();
                if (content.Length == 0)
                {
                    Add("E", "R18", "SPEC.md 章节“" + wanted + "”内容为空", nid);
                }
                else
                {
                    var placeholder = PlaceholderRe.Match(content);
                    if (placeholder.Success)
                    {
                        Add("E", "R18",
                            "SPEC.md 章节“" + wanted + "”只含占位符：" + placeholder.Value, nid);
                    }
                }
            }
            }
            var programPath = Path.Combine(homePath, "program.yaml");
            if (!File.Exists(programPath))
            {
                continue;
            }
            object? data;
            try
            {
                data = YamlLoader.LoadRawDocument(programPath);
            }
            catch (PTreeException exception)
            {
                Add("E", "R18", "program.yaml 无法解析：" + exception.Message, nid);
                continue;
            }
            if (data is RawMap document && document.TryGetValue("node", out var nodeValue) &&
                nodeValue is RawMap wrapped)
            {
                data = wrapped;
            }
            if (data is not RawMap programYaml)
            {
                Add("E", "R18", "program.yaml 必须是映射", nid);
                continue;
            }
            foreach (var field in NodeCompareFields)
            {
                var expected = node.TryGetValue(field, out var expectedValue) ? expectedValue : null;
                if (!programYaml.ContainsKey(field))
                {
                    Add("E", "R18", "program.yaml 缺少字段：" + field, nid);
                    continue;
                }
                var actual = programYaml[field];
                if (field == "status" && actual is string actualStatus)
                {
                    actual = StatusValues.Normalize(actualStatus);
                }
                if (!PyValue.EqualsPy(expected, actual))
                {
                    Add("E", "R18",
                        "program.yaml 字段 " + field + " 与清单不一致：清单=" + PyValue.Repr(expected) +
                        "，program.yaml=" + PyValue.Repr(actual), nid);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // R19 未登记代码扫描
    // ------------------------------------------------------------------

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.Ordinal)
    {
        ".py", ".js", ".ts", ".tsx", ".jsx", ".mjs", ".cjs", ".vue", ".go", ".rs",
    };

    private List<(string Home, string NodeId)> ProgramHomes()
    {
        var result = new List<(string Home, string NodeId)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid)
            {
                continue;
            }
            var kind = node.TryGetValue("kind", out var kindValue) ? kindValue : null;
            if (!(kind is string kindText && kindText == "program"))
            {
                continue;
            }
            var home = node.TryGetValue("home", out var homeValue) ? homeValue : null;
            if (home is not string homeText)
            {
                continue;
            }
            var normalized = homeText.Replace('\\', '/').Trim().TrimEnd('/');
            if (normalized.StartsWith("10-程序/", StringComparison.Ordinal) && seen.Add(normalized))
            {
                result.Add((normalized, nid));
            }
        }
        return result;
    }

    private void R19()
    {
        var registered = ProgramHomes();
        var registeredHomes = registered.Select(entry => entry.Home).ToHashSet(StringComparer.Ordinal);
        var basePath = Path.Combine(_repository.Root, "10-程序");
        if (!Directory.Exists(basePath))
        {
            if (registered.Count > 0)
            {
                Add("E", "R19", "10-程序 目录不存在，但存在已登记 home 的 program 节点");
            }
            return;
        }
        var externalRoots = new List<string>();
        foreach (var directory in ProgramWalker.Enumerate(_repository.Root))
        {
            var externalPath = ResolvePath(directory.Rel);
            if (Directory.Exists(Path.Combine(externalPath, ".git")) ||
                File.Exists(Path.Combine(externalPath, "package.json")))
            {
                externalRoots.Add(directory.Rel);
            }
        }
        foreach (var directory in ProgramWalker.Enumerate(_repository.Root))
        {
            var rel = directory.Rel;
            if (externalRoots.Any(root => rel == root || rel.StartsWith(root + "/", StringComparison.Ordinal)))
            {
                continue;
            }
            if (rel == "10-程序")
            {
                continue;
            }
            if (rel == "10-程序/共享" || rel.StartsWith("10-程序/共享/", StringComparison.Ordinal))
            {
                continue;
            }
            if (registeredHomes.Any(home => rel == home || rel.StartsWith(home + "/", StringComparison.Ordinal)))
            {
                continue;
            }
            var directoryPath = ResolvePath(rel);
            var hasManifest = File.Exists(Path.Combine(directoryPath, "program.yaml"));
            var codeFiles = directory.FileNames
                .Where(name => CodeExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
                .ToList();
            if (hasManifest)
            {
                if (!registeredHomes.Contains(rel))
                {
                    Add("E", "R19",
                        "未登记代码：目录 " + rel + " 含 program.yaml，但没有 kind=program 且 home=" + rel + " 的节点");
                }
            }
            else if (codeFiles.Count > 0)
            {
                Add("E", "R19", "未登记代码：目录 " + rel + " 含代码文件但没有 program.yaml");
            }
        }
        foreach (var entry in registered)
        {
            var programPath = Path.Combine(ResolvePath(entry.Home), "program.yaml");
            if (!File.Exists(programPath))
            {
                Add("E", "R19", "登记的 program 节点缺少 program.yaml：home=" + entry.Home, entry.NodeId);
            }
        }
        foreach (var file in Directory.EnumerateFiles(basePath))
        {
            var name = Path.GetFileName(file);
            if (CodeExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()) && name != "README.md")
            {
                Add("E", "R19", "10-程序 根下不得散落代码文件：" + name);
            }
        }
    }

    // ------------------------------------------------------------------
    // R23 规模统计
    // ------------------------------------------------------------------

    private void R23()
    {
        var departments = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || !node.TryGetValue("kind", out var kind) ||
                kind is not string kindText || kindText != "department")
            {
                continue;
            }
            if (!node.TryGetValue("id", out var idValue) || idValue is not string id || id.Length == 0)
            {
                continue;
            }
            var count = 0;
            foreach (var otherItem in _nodes)
            {
                if (otherItem is RawMap other && other.TryGetValue("parent", out var parentValue) &&
                    parentValue is string parentText && string.Equals(parentText, id, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            departments[id] = count;
        }
        foreach (var entry in departments)
        {
            if (entry.Value > 20)
            {
                Add("I", "R23", "单部门节点数 " + entry.Value + " > 20，可能粒度太细或该拆部门", entry.Key);
            }
        }
    }

    // ------------------------------------------------------------------
    // R24 豁免登记
    // ------------------------------------------------------------------

    private sealed record Exemption(string Rule, string? Node, string Reason, string Until);

    private void Exemptions()
    {
        var path = Path.Combine(_repository.Root, "00-程序树", "豁免.yaml");
        if (!File.Exists(path))
        {
            return;
        }
        object? doc;
        try
        {
            doc = YamlLoader.LoadRawDocument(path);
        }
        catch (PTreeException)
        {
            return;
        }
        var issues = new List<Issue>();
        if (doc is not RawMap document || !document.TryGetValue("exemptions", out var exemptionsValue) ||
            exemptionsValue is not List<object?> exemptions)
        {
            issues.Add(new Issue("E", "R24", "豁免.yaml 顶层必须包含 exemptions 数组"));
            AppendIssues(issues);
            return;
        }
        var today = DateTime.Today;
        var map = new Dictionary<(string Rule, string? Node), Exemption>();
        for (var index = 0; index < exemptions.Count; index++)
        {
            var label = "exemptions[" + index + "]";
            if (exemptions[index] is not RawMap entry)
            {
                issues.Add(new Issue("E", "R24", label + " 必须是映射"));
                continue;
            }
            var rule = entry.TryGetValue("rule", out var ruleValue) ? ruleValue : null;
            var node = entry.TryGetValue("node", out var nodeValue) ? nodeValue : null;
            var reason = entry.TryGetValue("reason", out var reasonValue) ? reasonValue : null;
            var until = entry.TryGetValue("until", out var untilValue) ? untilValue : null;
            var ruleText = rule as string;
            var reasonText = reason as string;
            var untilText = until as string;
            var valid = true;
            if (!(ruleText is not null && RuleForExemptionRe.IsMatch(ruleText)))
            {
                issues.Add(new Issue("E", "R24",
                    label + ".rule 非法：" + PyValue.Repr(rule) + "（必须为 R01-R24）"));
                valid = false;
            }
            else if (ruleText is "R03" or "R04" or "R05" or "R10")
            {
                issues.Add(new Issue("E", "R24",
                    "豁免不得用于 " + ruleText + "（R03/R04/R05/R10 树结构与契约一致性不可豁免）"));
                valid = false;
            }
            if (!(reasonText is not null && !string.IsNullOrWhiteSpace(reasonText)))
            {
                issues.Add(new Issue("E", "R24", label + " 未登记豁免原因（reason）"));
                valid = false;
            }
            var untilValid = untilText is not null && DateRe.IsMatch(untilText) &&
                             DateTime.TryParseExact(untilText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                 DateTimeStyles.None, out _);
            if (!untilValid)
            {
                issues.Add(new Issue("E", "R24",
                    label + ".until 必须是合法日期 YYYY-MM-DD（实际 " + PyValue.Repr(until) + "）"));
                valid = false;
            }
            else if (untilText is not null)
            {
                var untilDate = DateTime.ParseExact(untilText, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date;
                if (untilDate < today)
                {
                    issues.Add(new Issue("E", "R24", label + " 豁免已过期：until=" + untilText));
                    valid = false;
                }
            }
            if (node is not null && !(node is string nodeText && !string.IsNullOrWhiteSpace(nodeText)))
            {
                issues.Add(new Issue("E", "R24", label + ".node 必须是节点 id 或省略"));
                valid = false;
            }
            if (valid && ruleText is not null && reasonText is not null && untilText is not null)
            {
                map[(ruleText, node as string)] = new Exemption(ruleText, node as string, reasonText, untilText);
            }
        }
        if (map.Count > 0)
        {
            for (var index = 0; index < _issues.Count; index++)
            {
                var issue = _issues[index];
                if (issue.Level != "E")
                {
                    continue;
                }
                if (!map.TryGetValue((issue.Code, issue.Node), out var exemption) &&
                    !map.TryGetValue((issue.Code, null), out exemption))
                {
                    continue;
                }
                _issues[index] = new Issue("W", issue.Code,
                    issue.Message + "（已豁免：" + exemption.Reason + "，至 " + exemption.Until + "）", issue.Node);
            }
        }
        AppendIssues(issues);
    }


    // ------------------------------------------------------------------
    // R20 Python import 扫描（受限扫描器，近似）
    // ------------------------------------------------------------------

    private void R20()
    {
        var registered = ProgramHomes();
        if (registered.Count == 0) return;
        var sharedRoot = Path.Combine(_repository.Root, "10-程序", "共享");
        var sharedNames = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(sharedRoot))
            foreach (var entry in Directory.EnumerateFileSystemEntries(sharedRoot))
            {
                sharedNames.Add(Path.GetFileNameWithoutExtension(entry));
                sharedNames.Add(Path.GetFileName(entry));
            }
        var own = registered.Select(entry => (
            entry.NodeId,
            entry.Home,
            Parts: entry.Home["10-程序/".Length..].Split('/'))).ToList();
        foreach (var (nid, home, ownParts) in own)
        {
            var homeDir = ResolvePath(home);
            if (!Directory.Exists(homeDir)) continue;
            var foreign = own.Where(other => other.NodeId != nid).Select(other => (other.Parts, other.NodeId)).ToList();
            var localNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(homeDir))
            {
                localNames.Add(Path.GetFileName(entry));
                if (File.Exists(entry)) localNames.Add(Path.GetFileNameWithoutExtension(entry));
            }
            var jsFiles = false;
            foreach (var file in EnumerateFilesNoLinks(homeDir))
            {
                var suffix = Path.GetExtension(file).ToLowerInvariant();
                if (suffix is ".js" or ".ts" or ".tsx" or ".jsx") jsFiles = true;
                if (suffix != ".py") continue;
                string text;
                try { text = File.ReadAllText(file, new UTF8Encoding(false, true)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    Add("E", "R20", "Python 文件无法解析：" + RelPath(file) + "（" + exception.Message + "）", nid);
                    continue;
                }
                foreach (var module in PythonImports.Scan(text))
                {
                    if (module.StartsWith('.')) continue;
                    var parts = module.Split('.');
                    var top = parts[0];
                    if (StdlibNames.Contains(top) || localNames.Contains(top) || top == "src" || sharedNames.Contains(top)) continue;
                    if (parts.Length >= ownParts.Length && parts.Take(ownParts.Length).SequenceEqual(ownParts, StringComparer.Ordinal)) continue;
                    string? flagged = null;
                    foreach (var (foreignParts, foreignId) in foreign)
                        if (parts.Length >= foreignParts.Length && parts.Take(foreignParts.Length).SequenceEqual(foreignParts, StringComparer.Ordinal))
                        {
                            flagged = foreignId;
                            break;
                        }
                    if (flagged is not null)
                        Add("E", "R20", "跨节点 import 禁止：" + module + "（文件 " + RelPath(file) + " 属于节点 " + nid +
                            "，却引用节点 " + flagged + " 的内部模块）", nid);
                }
            }
            if (jsFiles) Add("I", "R20", "存在 JS/TS 文件，跨节点 import 检查 v1 未实现，请用 depends_on 人工保证：" + nid, nid);
        }
    }

    private string RelPath(string file) => Path.GetRelativePath(_repository.Root, file).Replace('\\', '/');

    private static IEnumerable<string> EnumerateFilesNoLinks(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            List<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current).ToList(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (name is "__pycache__" or "node_modules" or ".git") continue;
                    try { if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue; }
                    catch (Exception) { continue; }
                    stack.Push(entry);
                }
                else yield return entry;
            }
        }
    }

    private static readonly HashSet<string> StdlibNames = new(StringComparer.Ordinal)
    {
        "abc","argparse","ast","base64","collections","contextlib","copy","csv","dataclasses","datetime","decimal","enum",
        "functools","glob","hashlib","html","io","itertools","json","logging","math","os","pathlib","pickle","re","shutil",
        "sqlite3","statistics","string","subprocess","sys","tempfile","textwrap","threading","time","traceback","typing",
        "unicodedata","urllib","uuid","warnings","xml","yaml","concurrent","asyncio","bisect","calendar","codecs","ctypes",
        "difflib","errno","fileinput","fnmatch","getpass","gzip","hmac","importlib","inspect","ipaddress","keyword","locale",
        "mimetypes","multiprocessing","operator","platform","pprint","queue","random","secrets","select","socket","ssl",
        "struct","tarfile","tokenize","unittest","venv","webbrowser","zipfile","zlib"
    };

    private List<object?> ListOf(RawMap map, string key)
        => map.TryGetValue(key, out var value) && value is List<object?> list ? list : new List<object?>();

    private string? StringOf(RawMap map, string key)
        => map.TryGetValue(key, out var value) && value is string text ? text : null;

    private void R25()
    {
        if (_repository.BenchmarksRaw is null) { Add("E", "R25", "标杆定义文件不存在：00-程序树/标杆/benchmarks.yaml（v1 起必须有）"); return; }
        if (_repository.BenchmarksRaw is not RawMap doc) { Add("E", "R25", "00-程序树/标杆/benchmarks.yaml 顶层必须是映射"); return; }
        if (!doc.TryGetValue("schema_version", out var schema) || !PyValue.EqualsPy(schema, 1L))
            Add("E", "R25", "schema_version 必须为 1（实际 " + PyValue.Repr(schema) + "）");
        var updated = StringOf(doc, "updated");
        if (!(updated is not null && DateRe.IsMatch(updated) && IsValidDate(updated)))
            Add("E", "R25", "updated 必须是合法日期 YYYY-MM-DD（实际 " + PyValue.Repr(updated) + "）");
        var benchmarks = ListOf(doc, "benchmarks");
        if (benchmarks.Count == 0) Add("I", "R25", "尚未立标杆：benchmarks 为空数组，允许第一版清单先通过");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < benchmarks.Count; index++)
        {
            if (benchmarks[index] is not RawMap item) { Add("E", "R25", "benchmarks[" + index + "] 必须是映射"); continue; }
            var id = StringOf(item, "id");
            if (id is not null && !seen.Add(id)) Add("E", "R25", "标杆 id 重复：" + id, id);
            var numbers = ListOf(item, "levels").OfType<RawMap>()
                .Select(level => level.TryGetValue("level", out var value) && value is long number ? number : -1).ToList();
            if (numbers.Count != 5 || numbers.Distinct().Count() != 5 || numbers.Any(number => number is < 0 or > 4))
                Add("E", "R25", (id ?? "benchmarks[" + index + "]") + ".levels 的 level 集合必须恰好 {0,1,2,3,4}");
        }
        if (doc.TryGetValue("coverage", out var coverage) && coverage is RawMap coverageMap)
        {
            var covered = new HashSet<string>(StringComparer.Ordinal);
            var required = ListOf(coverageMap, "required");
            for (var index = 0; index < required.Count; index++)
            {
                if (required[index] is not RawMap item) continue;
                var nid = StringOf(item, "node");
                if (nid is not null && _nodeMap.ContainsKey(nid) && !covered.Add(nid))
                    Add("E", "R25", "coverage.required 节点重复：" + nid, nid);
            }
        }
    }

    private void R26()
    {
        if (_repository.BindingsRaw is null) { Add("E", "R26", "标杆绑定文件不存在：00-程序树/标杆/绑定.yaml（v1 起必须有）"); return; }
        if (_repository.BindingsRaw is not RawMap doc) { Add("E", "R26", "00-程序树/标杆/绑定.yaml 顶层必须是映射"); return; }
        if (!doc.TryGetValue("schema_version", out var schema) || !PyValue.EqualsPy(schema, 1L))
            Add("E", "R26", "schema_version 必须为 1（实际 " + PyValue.Repr(schema) + "）");
        var updated = StringOf(doc, "updated");
        if (!(updated is not null && DateRe.IsMatch(updated) && IsValidDate(updated)))
            Add("E", "R26", "updated 必须是合法日期 YYYY-MM-DD（实际 " + PyValue.Repr(updated) + "）");
        var benchmarks = new HashSet<string>(StringComparer.Ordinal);
        if (_repository.BenchmarksRaw is RawMap bdoc)
            foreach (var item in ListOf(bdoc, "benchmarks").OfType<RawMap>())
                if (StringOf(item, "id") is { } bid) benchmarks.Add(bid);
        var seenPairs = new HashSet<(string, string)>();
        var bindings = ListOf(doc, "bindings");
        for (var index = 0; index < bindings.Count; index++)
        {
            if (bindings[index] is not RawMap binding) { Add("E", "R26", "bindings[" + index + "] 必须是映射"); continue; }
            var nid = StringOf(binding, "node");
            var bid = StringOf(binding, "benchmark");
            var label = nid is not null ? nid + " × " + bid : "bindings[" + index + "]";
            if (nid is null || !_nodeMap.ContainsKey(nid)) Add("E", "R26", label + ".node 不存在于 program-tree.yaml：" + nid, nid);
            if (bid is null || !benchmarks.Contains(bid)) Add("E", "R26", label + ".benchmark 不存在于 benchmarks.yaml：" + bid, nid);
            if (nid is not null && bid is not null && !seenPairs.Add((nid, bid)))
                Add("E", "R26", label + " 重复绑定：同一 (node, benchmark) 只能出现一次", nid);
            var level = binding.TryGetValue("level", out var lv) && lv is long ln && ln is >= 0 and <= 4 ? (int?)ln : null;
            var target = binding.TryGetValue("target", out var tv) && tv is long tn && tn is >= 0 and <= 4 ? (int?)tn : null;
            if (level is null) Add("E", "R26", label + ".level 必须为 0-4 的整数", nid);
            if (target is null) Add("E", "R26", label + ".target 必须为 0-4 的整数", nid);
            if (level > target) Add("E", "R26", label + " 必须满足 level ≤ target", nid);
            if (level >= 2 && (!binding.TryGetValue("evidence", out var ev) || ev is not List<object?> evidence || evidence.Count == 0))
                Add("E", "R26", label + " level=" + level + " ≥ 2，必须提供 evidence（当前等级的证据，至少 1 条）", nid);
            var history = ListOf(binding, "history");
            if (history.Count == 0) Add("E", "R26", label + ".history 至少 1 条（等级历史不能为空）", nid);
            int? previous = null;
            foreach (var item in history)
            {
                if (item is not RawMap entry) { Add("E", "R26", label + ".history 条目必须是映射", nid); previous = null; continue; }
                var from = entry.TryGetValue("from", out var fv) && fv is long fl && fl is >= 0 and <= 4 ? (int?)fl : null;
                var to = entry.TryGetValue("to", out var ttv) && ttv is long tl && tl is >= 0 and <= 4 ? (int?)tl : null;
                if (from is null) Add("E", "R26", label + ".history.from 必须为 0-4 的整数", nid);
                if (to is null) Add("E", "R26", label + ".history.to 必须为 0-4 的整数", nid);
                if (previous is not null && from is not null && from != previous)
                    Add("E", "R26", label + ".history 与上一条不连续：上一条 to=" + previous + "，本条 from=" + from, nid);
                previous = to;
            }
            if (previous is not null && level is not null && previous != level)
                Add("E", "R26", label + " 最后一条 history.to=" + previous + " 必须等于当前 level=" + level, nid);
        }
    }

    private void R27()
    {
        if (_repository.BenchmarksRaw is null || _repository.BindingsRaw is null) return;
        var boundNodes = new HashSet<string>(StringComparer.Ordinal);
        var boundBenchmarks = new HashSet<string>(StringComparer.Ordinal);
        if (_repository.BindingsRaw is RawMap bindingsDoc)
            foreach (var item in ListOf(bindingsDoc, "bindings").OfType<RawMap>())
            {
                if (StringOf(item, "node") is { } nid) boundNodes.Add(nid);
                if (StringOf(item, "benchmark") is { } bid) boundBenchmarks.Add(bid);
            }
        if (_repository.BenchmarksRaw is RawMap benchmarkDoc)
        {
            if (benchmarkDoc.TryGetValue("coverage", out var coverage) && coverage is RawMap coverageMap)
                foreach (var item in ListOf(coverageMap, "required").OfType<RawMap>())
                    if (StringOf(item, "node") is { } nid && !boundNodes.Contains(nid))
                        Add("E", "R27", "coverage.required 节点未绑定任何标杆：" + nid + "（要么 bench_bind，要么从 coverage 中移除）", nid);
            foreach (var item in ListOf(benchmarkDoc, "benchmarks").OfType<RawMap>())
                if (StringOf(item, "id") is { } bid && !boundBenchmarks.Contains(bid))
                    Add("W", "R27", "标杆没有任何节点绑定：" + bid + "（要么 bench_bind，要么用 bench_prune 修剪）", bid);
        }
    }

    private void R28()
    {
        var updated = _manifest.TryGetValue("updated", out var updatedValue) ? updatedValue as string : null;
        if (!(updated is not null && DateRe.IsMatch(updated) && IsValidDate(updated))) return;
        var baseline = DateTime.ParseExact(updated, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date;
        if (_repository.BindingsRaw is not RawMap doc) return;
        foreach (var binding in ListOf(doc, "bindings").OfType<RawMap>())
        {
            var nid = StringOf(binding, "node");
            var bid = StringOf(binding, "benchmark");
            foreach (var gap in ListOf(binding, "gaps").OfType<RawMap>())
            {
                var opened = StringOf(gap, "opened");
                if (!(opened is not null && DateRe.IsMatch(opened) && IsValidDate(opened))) continue;
                var days = (baseline - DateTime.ParseExact(opened, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date).Days;
                if (days > 120)
                    Add("W", "R28", "差距已躺 " + days + " 天未关（节点 " + nid + " / 标杆 " + bid + " / 维度 " + StringOf(gap, "dimension") + "）：要么关要么重排", nid);
            }
        }
    }

    private void R29()
    {
        if (_repository.BenchmarksRaw is not RawMap benchmarkDoc || _repository.BindingsRaw is not RawMap bindingsDoc) return;
        var benchmarks = ListOf(benchmarkDoc, "benchmarks").OfType<RawMap>().Count();
        if (benchmarks > 12) Add("I", "R29", "标杆条数 " + benchmarks + " > 12，超规模上限，提示合并或裁剪");
        var perNode = new Dictionary<string, int>(StringComparer.Ordinal);
        var perBenchmark = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var binding in ListOf(bindingsDoc, "bindings").OfType<RawMap>())
        {
            if (StringOf(binding, "node") is { } nid) perNode[nid] = perNode.GetValueOrDefault(nid) + 1;
            if (StringOf(binding, "benchmark") is { } bid) perBenchmark[bid] = perBenchmark.GetValueOrDefault(bid) + 1;
        }
        foreach (var entry in perNode.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            if (entry.Value > 3) Add("I", "R29", "单节点绑定 " + entry.Value + " 条 > 3，提示合并或裁剪", entry.Key);
        foreach (var entry in perBenchmark.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            if (entry.Value > 8) Add("I", "R29", "单标杆被 " + entry.Value + " 个节点绑定 > 8，提示合并或裁剪", entry.Key);
    }

    private void R31()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid) continue;
            var kind = StringOf(node, "kind");
            var status = StringOf(node, "status");
            var introPath = Path.Combine(_repository.Root, "00-程序树", "说明", nid + ".txt");
            string? text = null;
            if (File.Exists(introPath))
                try { text = File.ReadAllText(introPath, new UTF8Encoding(false, true)).Replace("\r\n", "\n").Replace('\r', '\n'); }
                catch (Exception) { text = null; }
            if (kind == "program")
            {
                if (text is null && status is "未完成" or "未完成" or "已完成" or "已验证")
                    Add("E", "R31", "缺少节点说明：00-程序树/说明/" + nid + ".txt（status=" + status + " 的程序必须有说明）", nid);
                else if (text is null && status is "退役" or "退役")
                    Add("W", "R31", "缺少节点说明：00-程序树/说明/" + nid + ".txt（status=" + status + " 的程序建议保留历史说明）", nid);
            }
            else if (kind == "department" && text is null)
                Add("W", "R31", "缺少节点说明：00-程序树/说明/" + nid + ".txt（部门建议写说明，说明整体职责与边界）", nid);
            if (text is null) continue;
            var compact = PythonText.LengthWithoutWhitespace(text);
            if (compact < 200) Add("E", "R31", "说明去空白后只有 " + compact + " 字，少于 200 字（写清定位、代码、连接、怎么改）", nid);
            var placeholder = Regex.Match(text, "(TBD|待补|TODO|以后再填)", RegexOptions.IgnoreCase);
            if (placeholder.Success) Add("E", "R31", "说明含占位符 " + PyValue.Repr(placeholder.Value) + "（TBD/待补/TODO/以后再填一律禁止）", nid);
            var markdown = new List<int>();
            var numbered = new List<int>();
            var lines = PythonText.SplitLines(text);
            for (var index = 0; index < lines.Count; index++)
            {
                var stripped = lines[index].TrimStart();
                if (stripped.Length > 0 && (stripped.StartsWith('#') || stripped.StartsWith('*') || stripped.StartsWith('-') ||
                    stripped.StartsWith('>') || stripped.StartsWith("\u0060\u0060\u0060", StringComparison.Ordinal)))
                    markdown.Add(index + 1);
                if (Regex.IsMatch(lines[index], @"^\s*[0-9]+[.、)]")) numbered.Add(index + 1);
            }
            if (markdown.Count > 0) Add("W", "R31", "说明行首出现 Markdown 标记（行 " + string.Join("、", markdown.Take(5)) + "；纯文本禁用 #、*、-、>、代码块）", nid);
            if (numbered.Count > 0) Add("W", "R31", "说明行首出现数字编号（行 " + string.Join("、", numbered.Take(5)) + "；纯文本禁用 1. 2、 编号）", nid);
            var hasPath = new[] { ".py", ".js", ".yaml", ".md", "00-程序树", "10-程序" }.Any(text.Contains) || Regex.IsMatch(text, @"[\w.\-]+/[\w.\-]+");
            if (!hasPath) Add("W", "R31", "说明通篇没有出现任何真实路径片段（如 src/模块、01-当前架构/xxx）或文件关键词（.py/.js/.yaml/.md），疑似与代码无关的空话", nid);
        }
    }

    private void R32()
    {
        var ledgerPath = Path.Combine(_repository.Root, "00-程序树", "变更", "台账.jsonl");
        var historyDir = Path.Combine(_repository.Root, "00-程序树", "变更", "历史");
        var archives = Directory.Exists(historyDir) ? Directory.GetFiles(historyDir, "*.yaml").Length : 0;
        if (!File.Exists(ledgerPath))
        {
            var message = "台账不存在：00-程序树/变更/台账.jsonl（尚未有提案通过 apply --write 落地；首次落地会自动生成并追加一条记录，此告警随之消失）";
            if (archives > 0) message += "；变更/历史/ 已有 " + archives + " 份归档，建议先补一条基线记录";
            Add("W", "R32", message);
            return;
        }
        List<string> lines;
        try { lines = PythonText.SplitLines(File.ReadAllText(ledgerPath, new UTF8Encoding(false, true))); }
        catch (Exception) { Add("E", "R32", "无法读取台账：00-程序树/变更/台账.jsonl"); return; }
        if (lines.All(string.IsNullOrWhiteSpace))
        {
            var message = "台账为空：00-程序树/变更/台账.jsonl";
            if (archives > 0) message += "；变更/历史/ 已有 " + archives + " 份归档，建议补基线记录";
            Add("W", "R32", message);
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            try
            {
                using var document = JsonDocument.Parse(lines[index]);
                if (document.RootElement.ValueKind != JsonValueKind.Object) { Add("E", "R32", "台账第 " + (index + 1) + " 行必须是 JSON 对象"); continue; }
                var element = document.RootElement;
                var missing = new[] { "id", "at", "actor", "proposal", "reason", "ops", "validation", "result" }.Where(key => !element.TryGetProperty(key, out _)).ToList();
                if (missing.Count > 0) Add("E", "R32", "台账第 " + (index + 1) + " 行缺少必填字段：" + string.Join("、", missing));
                if (element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                {
                    var id = idElement.GetString();
                    if (id is null || !Regex.IsMatch(id, "^chg-[0-9]{8}-[0-9]{4}$"))
                        Add("E", "R32", "台账第 " + (index + 1) + " 行.id 必须形如 chg-YYYYMMDD-NNNN（实际 " + PyValue.Repr(id) + "）", id);
                    else if (!seen.Add(id)) Add("E", "R32", "台账第 " + (index + 1) + " 行.id 重复：" + id, id);
                }
                if (element.TryGetProperty("validation", out var validation) && validation.ValueKind == JsonValueKind.Object)
                    foreach (var key in new[] { "errors", "warnings" })
                        if (!validation.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0)
                            Add("E", "R32", "台账第 " + (index + 1) + " 行.validation." + key + " 必须是非负整数");
                if (element.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                {
                    var text = result.GetString();
                    if (text is not ("applied" or "rolled_back"))
                        Add("E", "R32", "台账第 " + (index + 1) + " 行.result 必须是 applied 或 rolled_back（实际 " + PyValue.Repr(text) + "）");
                }
                if (element.TryGetProperty("proposal", out var proposal) && proposal.ValueKind == JsonValueKind.String)
                {
                    var text = proposal.GetString();
                    if (text is not null)
                    {
                        var target = Path.IsPathRooted(text) ? text : Path.Combine(_repository.Root, text);
                        if (!File.Exists(target) && !Directory.Exists(target))
                            Add("E", "R32", "台账第 " + (index + 1) + " 行.proposal 指向的文件不存在：" + text);
                    }
                }
            }
            catch (JsonException exception) { Add("E", "R32", "台账第 " + (index + 1) + " 行不是合法 JSON：" + exception.Message); }
        }
    }

    private void R33()
    {
        var denseHits = new List<(string Message, string Node)>();
        var contractHits = new List<(string Message, string Node)>();
        var seenDeps = new HashSet<(string, string)>();
        var contractMap = new Dictionary<string, RawMap>(StringComparer.Ordinal);
        foreach (var item in ContractItems())
            if (item is RawMap contract && StringOf(contract, "id") is { } cid) contractMap.TryAdd(cid, contract);
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid || IsDeadStatus(node)) continue;
            var inputs = ListOf(node, "inputs");
            foreach (var depValue in ListOf(node, "depends_on"))
            {
                if (depValue is not string dep || !_nodeMap.TryGetValue(dep, out var depNode)) continue;
                if (IsDeadStatus(depNode) || StringOf(depNode, "kind") == "external") continue;
                if (dep == StringOf(node, "parent") || StringOf(depNode, "parent") == nid) continue;
                if (!seenDeps.Add((nid, dep))) continue;
                var supported = false;
                foreach (var cidValue in inputs)
                    if (cidValue is string cid && contractMap.TryGetValue(cid, out var contract) && ListOf(contract, "producers").Any(v => v is string s && s == dep))
                    {
                        supported = true;
                        break;
                    }
                if (!supported)
                    denseHits.Add(("依赖无契约支撑：" + nid + " depends_on " + dep + "，但 " + nid + " 的输入里没有 " + dep + " 产出的契约（触发依赖：" + dep + "）", nid));
            }
        }
        var pairs = new Dictionary<(string Producer, string Consumer), List<string>>();
        foreach (var item in ContractItems())
        {
            if (item is not RawMap contract || StringOf(contract, "id") is not { } cid) continue;
            foreach (var producerValue in ListOf(contract, "producers"))
            {
                if (producerValue is not string producer || !_nodeMap.TryGetValue(producer, out var producerNode) || IsDeadStatus(producerNode)) continue;
                foreach (var consumerValue in ListOf(contract, "consumers"))
                {
                    if (consumerValue is not string consumer || consumer == producer || !_nodeMap.TryGetValue(consumer, out var consumerNode) || IsDeadStatus(consumerNode)) continue;
                    var key = (producer, consumer);
                    if (!pairs.TryGetValue(key, out var list)) { list = new List<string>(); pairs[key] = list; }
                    list.Add(cid);
                }
            }
        }
        foreach (var pair in pairs)
        {
            var (producer, consumer) = pair.Key;
            if (Ancestors(consumer).Contains(producer) || Ancestors(producer).Contains(consumer)) continue;
            var common = NearestCommonAncestor(producer, consumer);
            if (common is not null && _nodeMap.TryGetValue(common, out var commonNode) && StringOf(commonNode, "kind") == "program") continue;
            if (DependsClosure(consumer).Contains(producer)) continue;
            contractHits.Add(("契约连接缺依赖声明：" + string.Join("、", pair.Value) + " 的 producer " + producer + " 与 consumer " + consumer +
                "，但 " + consumer + " 未 depends_on " + producer + " 且 " + producer + " 不是 " + consumer + " 的祖先（触发契约：" + string.Join("、", pair.Value) + "）", consumer));
        }
        var denseLevel = "I";
        var denseSuffix = "（共 " + denseHits.Count + " 条，仅提示，不阻断）";
        foreach (var hit in denseHits) Add(denseLevel, "R33", hit.Message + denseSuffix, hit.Node);
        var contractLevel = contractHits.Count <= 15 ? "W" : "I";
        var contractSuffix = contractLevel == "W" ? "" : "（共 " + contractHits.Count + " 条，仅提示，不阻断）";
        foreach (var hit in contractHits) Add(contractLevel, "R33", hit.Message + contractSuffix, hit.Node);
    }

    private void R34()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node) continue;
            var nid = NodeId(node);
            var fileSpace = nid is { Length: > 0 }
                ? EvaluationSpace.ReadOnly(_repository.EvaluationPath(nid))
                : new EvaluationSpace();
            if (fileSpace.Has) continue;
            var fileExists = nid is { Length: > 0 } && File.Exists(_repository.EvaluationPath(nid));
            if (node.TryGetValue("evaluation", out var value))
            {
                if (value is not string text)
                {
                    Add("E", "R34", "evaluation 必须是字符串", nid);
                    continue;
                }
                var trimmed = text.Trim();
                if (trimmed.Length == 0)
                {
                    Add("E", "R34", "evaluation 写了就不能为空", nid);
                    continue;
                }
                var length = trimmed.Count(ch => !char.IsWhiteSpace(ch));
                if (length < 40)
                    Add("E", "R34", "evaluation 至少 40 字（实际 " + length + " 字）", nid);
                if (trimmed.Contains("待补", StringComparison.Ordinal) ||
                    Regex.IsMatch(trimmed, @"(^|[\s，。；;、])略([\s，。；;、]|$)", RegexOptions.CultureInvariant) ||
                    Regex.IsMatch(trimmed, @"(^|[\s，。；;、])TBD([\s，。；;、]|$)", RegexOptions.IgnoreCase) ||
                    trimmed.Contains("TODO", StringComparison.OrdinalIgnoreCase))
                    Add("E", "R34", "evaluation 不得使用待补、略、TBD、TODO 等占位", nid);
                continue;
            }
            if (fileExists && !fileSpace.Has)
                Add("W", "R34", "评价文件 " + _repository.EvaluationRel(nid!) + " 缺判断行（必须有/应该有/可有可无/不该有）", nid);
            else
                Add("W", "R34", "评价缺失：评价/" + (nid ?? "?") + ".txt 与 yaml evaluation 都没有，编码前先写四行（判断 位置 做法 缺）", nid);
        }
    }

    private void R35()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node) continue;
            var nid = NodeId(node);
            if (!node.TryGetValue("preset", out var presetValue)) continue;
            if (presetValue is not bool preset)
            {
                Add("E", "R35", "preset 必须是布尔值", nid);
                continue;
            }
            if (!preset) continue;
            var status = StringOf(node, "status");
            if (!string.Equals(StatusValues.Normalize(status), "未完成", StringComparison.Ordinal))
                Add("E", "R35", "preset 占位节点状态必须为未完成（实际 " + PyValue.Repr(status) + "）", nid);
            var files = ListOf(node, "files");
            if (files.Count > 0)
                Add("E", "R35", "preset 转正后必须去掉 preset 标记：已写 files 仍为 preset", nid);
            var outputs = ListOf(node, "outputs");
            var explicitNone = string.Equals(StringOf(node, "preset_outputs"), "none", StringComparison.Ordinal);
            if (outputs.Count == 0 && !explicitNone)
                Add("E", "R35", "preset 必须带 outputs 或显式 preset_outputs=none", nid);
            if (node.TryGetValue("preset_outputs", out var presetOutputs) && !explicitNone)
                Add("E", "R35", "preset_outputs 只允许 none（实际 " + PyValue.Repr(presetOutputs) + "）", nid);
        }
    }

    private void R36()
    {
        if (!_manifest.TryGetValue("adapt", out var adaptValue)) return;
        if (adaptValue is not List<object?> items)
        {
            Add("E", "R36", "adapt 必须是数组");
            return;
        }
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not RawMap map)
            {
                Add("E", "R36", "adapt[" + index + "] 必须是映射");
                continue;
            }
            var dir = StringOf(map, "dir");
            if (string.IsNullOrWhiteSpace(dir))
            {
                Add("E", "R36", "adapt[" + index + "].dir 不能为空");
            }
            else
            {
                var normalized = dir.Replace('\\', '/').TrimEnd('/');
                if (Path.IsPathRooted(dir) || normalized.StartsWith("../", StringComparison.Ordinal) || normalized.Contains("/../", StringComparison.Ordinal))
                    Add("E", "R36", "adapt[" + index + "].dir 必须是仓库内相对路径", null);
                else if (!Directory.Exists(Path.Combine(_repository.Root, normalized.Replace('/', Path.DirectorySeparatorChar))))
                    Add("E", "R36", "adapt[" + index + "].dir 目录不存在：" + dir, null);
            }
            if (!map.TryGetValue("patterns", out var patternValue) || patternValue is not List<object?> patterns || patterns.Count == 0)
                Add("E", "R36", "adapt[" + index + "].patterns 必须是非空数组");
            else if (patterns.Any(pattern => pattern is not string text || string.IsNullOrWhiteSpace(text)))
                Add("E", "R36", "adapt[" + index + "].patterns 每项必须是非空字符串");
        }
    }

    private void R37()
    {
        foreach (var node in _nodes.OfType<RawMap>())
        {
            var kind = StringOf(node, "kind");
            if (kind is not ("program" or "department")) continue;
            var nid = NodeId(node);
            if (string.IsNullOrWhiteSpace(nid)) continue;
            var path = Path.Combine(_repository.Root, "00-程序树", "短介绍", nid + ".txt");
            if (!File.Exists(path))
            {
                Add("W", "R37", "缺短介绍：00-程序树/短介绍/" + nid + ".txt", nid);
                continue;
            }
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception exception)
            {
                Add("E", "R37", "短介绍无法读取：" + exception.Message, nid);
                continue;
            }
            var compact = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            if (compact.Length > 80)
                Add("E", "R37", "短介绍去空白后超过 80 字（实际 " + compact.Length + " 字）", nid);
            if (compact.Contains("待补", StringComparison.Ordinal) ||
                Regex.IsMatch(compact, "TBD|TODO", RegexOptions.IgnoreCase))
                Add("E", "R37", "短介绍不得含待补、TBD、TODO 等占位", nid);
        }
    }

    // ------------------------------------------------------------------
    // R38 功能块（打组）
    // ------------------------------------------------------------------

    private static readonly Regex GroupIdRe = new(@"^group\.[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant);

    private void R38()
    {
        if (_manifest.TryGetValue("groups", out var rawGroups) && rawGroups is not List<object?> groups)
        {
            Add("E", "R38", "groups 必须是数组");
            return;
        }
        var groupList = _manifest.TryGetValue("groups", out var groupValue) && groupValue is List<object?> value
            ? value.OfType<RawMap>().ToList()
            : new List<RawMap>();
        var groupIds = new HashSet<string>(groupList
            .Select(group => StringOf(group, "id") ?? "")
            .Where(id => id.Length > 0), StringComparer.Ordinal);
        foreach (var group in groupList)
        {
            var id = StringOf(group, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                Add("E", "R38", "组缺 id");
                continue;
            }
            if (!GroupIdRe.IsMatch(id))
                Add("E", "R38", "组 id 必须形如 group.<slug>（小写、连字符）：" + id, id);
            var name = StringOf(group, "name");
            if (string.IsNullOrWhiteSpace(name)) Add("E", "R38", "组缺 name：" + id, id);

            var parent = StringOf(group, "parent");
            if (!string.IsNullOrWhiteSpace(parent) && !groupIds.Contains(parent))
                Add("E", "R38", "组的 parent 不存在：" + parent, id);

            var members = group.TryGetValue("members", out var rawMembers) && rawMembers is List<object?> list
                ? list.OfType<string>().ToList()
                : new List<string>();
            if (members.Count == 0) Add("W", "R38", "组内没有成员：" + id, id);
            foreach (var member in members)
            {
                if (!_nodeMap.ContainsKey(member) && !groupIds.Contains(member))
                    Add("E", "R38", "组成员不存在：" + member, id);
            }

            // 只有人建的功能块要求简介与详细介绍；group.all 与 group.dept-* 是自动派生的部门组
            if (id == "group.all" || id.StartsWith("group.dept-", StringComparison.Ordinal)) continue;
            var description = StringOf(group, "description");
            if (string.IsNullOrWhiteSpace(description) || description.Length > 60)
                Add("E", "R38", "功能块要有一行简介，且不超过 60 字：" + id, id);
            var introPath = Path.Combine(_repository.Root, "00-程序树", "说明", id + ".txt");
            if (!File.Exists(introPath))
                Add("W", "R38", "缺功能块详细介绍：00-程序树/说明/" + id + ".txt", id);
            else
            {
                var text = File.ReadAllText(introPath).Trim();
                if (text.Length < 80)
                    Add("W", "R38", "功能块详细介绍太短（去空白后 " + text.Length + " 字，建议 ≥80）：" + id, id);
            }
        }
    }

    // ------------------------------------------------------------------
    // R39 网格阅读项（网格条目与短介绍）
    // ------------------------------------------------------------------

    /// <summary>
    /// 树实例里每个有编号的条目都要有一行短介绍（程序树网格除外：它的条目就是节点，由 R37 管）。
    /// 条目名单与读工具共用 GridReadings，组行 MA0–MA4、U0–U2 也算条目。
    /// </summary>
    private void R39()
    {
        TreeStore store;
        try
        {
            store = TreeStore.Load(_repository.Root);
        }
        catch (Exception exception)
        {
            Add("E", "R39", "树实例无法读取：" + exception.Message);
            return;
        }
        foreach (var item in GridReadings.Items(store, _repository.Root))
        {
            var rel = GridReadings.ShortRel(item.Id);
            var path = GridReadings.ShortPath(_repository.Root, item.Id);
            if (!File.Exists(path))
            {
                Add("W", "R39", "缺网格短介绍：" + rel + "（" + item.Kind + " 的 " + item.Id + "）", item.Id);
                continue;
            }
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception exception)
            {
                Add("E", "R39", "网格短介绍无法读取：" + exception.Message, item.Id);
                continue;
            }
            var compact = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            if (compact.Length == 0)
                Add("W", "R39", "网格短介绍是空的：" + rel, item.Id);
            if (compact.Length > 80)
                Add("E", "R39", "网格短介绍去空白后超过 80 字（实际 " + compact.Length + " 字）：" + item.Id, item.Id);
            if (compact.Contains("待补", StringComparison.Ordinal) ||
                Regex.IsMatch(compact, "TBD|TODO", RegexOptions.IgnoreCase))
                Add("E", "R39", "网格短介绍不得含待补、TBD、TODO 等占位：" + item.Id, item.Id);
        }
    }

    // ------------------------------------------------------------------
    // R40 联系登记（links 段结构 + 完整性提示）
    // ------------------------------------------------------------------

    /// <summary>网格条目编号：与 R39 共用 GridReadings 的名单，links 的 from/to 也认这些编号。</summary>
    private HashSet<string> ReadingIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var store = TreeStore.Load(_repository.Root);
            foreach (var item in GridReadings.Items(store, _repository.Root)) ids.Add(item.Id);
        }
        catch (Exception)
        {
            // 树实例读不动时 R39 已经报过；这里只按节点 id 判存在，不重复报错
        }
        return ids;
    }

    private void R40()
    {
        if (!_manifest.TryGetValue("links", out var rawLinks))
        {
            Add("I", "R40", "links 段还没有登记任何联系：代码互引只能靠 depends_on 体现，见 R41 提示");
            return;
        }
        if (rawLinks is not List<object?> links)
        {
            Add("E", "R40", "links 必须是数组");
            return;
        }
        if (links.Count == 0)
        {
            Add("I", "R40", "links 段是空的：还没有登记任何联系（" + LinkKinds.Written + "）");
            return;
        }
        var readings = ReadingIds();
        var seen = new HashSet<(string, string, string)>();
        for (var index = 0; index < links.Count; index++)
        {
            var label = "links[" + index + "]";
            if (links[index] is not RawMap link)
            {
                Add("E", "R40", label + " 必须是映射");
                continue;
            }
            var from = StringOf(link, "from");
            var to = StringOf(link, "to");
            var type = StringOf(link, "type");
            var valid = true;
            if (string.IsNullOrWhiteSpace(from))
            {
                Add("E", "R40", label + ".from 不能为空");
                valid = false;
            }
            else if (!_nodeMap.ContainsKey(from) && !readings.Contains(from))
            {
                Add("E", "R40", label + ".from 不存在：" + from + "（既不是节点 id，也不是网格条目编号）");
                valid = false;
            }
            if (string.IsNullOrWhiteSpace(to))
            {
                Add("E", "R40", label + ".to 不能为空");
                valid = false;
            }
            else if (!_nodeMap.ContainsKey(to) && !readings.Contains(to))
            {
                Add("E", "R40", label + ".to 不存在：" + to + "（既不是节点 id，也不是网格条目编号）");
                valid = false;
            }
            if (string.IsNullOrWhiteSpace(type))
            {
                Add("E", "R40", label + ".type 不能为空（只允许 " + LinkKinds.Written + "）");
                valid = false;
            }
            else if (!LinkKinds.All.Contains(type, StringComparer.Ordinal))
            {
                Add("E", "R40", label + ".type 非法：" + type + "（只允许 " + LinkKinds.Written + "）");
                valid = false;
            }
            if (!string.IsNullOrWhiteSpace(from) && string.Equals(from, to, StringComparison.Ordinal))
            {
                Add("E", "R40", label + " 不允许自连接：" + from);
                valid = false;
            }
            if (valid && from is not null && to is not null && type is not null && !seen.Add((from, to, type)))
            {
                Add("E", "R40", label + " 重复：" + from + " -[" + type + "]-> " + to + "（同一 from+to+type 只能登记一次）");
                valid = false;
            }
            var hasNote = link.TryGetValue("note", out var noteValue) && noteValue is string note && note.Trim().Length > 0;
            if (valid && !hasNote)
                Add("I", "R40", label + " " + from + " -[" + type + "]-> " + to + " 没有 note：联系依据要写在 note 里");
        }
    }

    // ------------------------------------------------------------------
    // R41 代码引用未登记（只提示，不阻断）
    // ------------------------------------------------------------------

    private static readonly string[] ReferenceScanPrefixes = { "00-程序树/工具/csharp/", "10-程序/" };
    private static readonly HashSet<string> ReferenceExtensions = new(StringComparer.Ordinal) { ".cs", ".js", ".mjs" };
    private static readonly Regex CsNamespaceRe = new(@"(?m)^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*[;{]", RegexOptions.CultureInvariant);
    private static readonly Regex CsUsingRe = new(@"(?m)^\s*using\s+([A-Za-z_][A-Za-z0-9_.]*)\s*;", RegexOptions.CultureInvariant);
    private static readonly Regex JsImportRe = new(@"(?:\bfrom\s*|\brequire\s*\(\s*|\bimport\s*\(\s*|\bimport\s+)['""]([^'""\r\n]+)['""]", RegexOptions.CultureInvariant);
    private static readonly Regex ProjectReferenceRe = new(@"<ProjectReference\b[^>]*\bInclude\s*=\s*""([^""]+)""", RegexOptions.CultureInvariant);

    private void R41()
    {
        var ownerOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var filesOfNode = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid) continue;
            foreach (var fileValue in ListOf(node, "files"))
            {
                if (fileValue is not string raw) continue;
                var rel = raw.Replace('\\', '/').Trim();
                if (rel.Length == 0) continue;
                if (!ReferenceScanPrefixes.Any(prefix => rel.StartsWith(prefix, StringComparison.Ordinal))) continue;
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (!ReferenceExtensions.Contains(Path.GetExtension(rel).ToLowerInvariant())) continue;
                if (!File.Exists(ResolvePath(rel))) continue;
                if (!ownerOf.TryAdd(rel, nid)) continue;
                if (!filesOfNode.TryGetValue(nid, out var list)) filesOfNode[nid] = list = new List<string>();
                list.Add(rel);
            }
        }
        if (filesOfNode.Count == 0) return;

        var declaring = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in filesOfNode.Values.SelectMany(list => list))
        {
            string text;
            try { text = File.ReadAllText(ResolvePath(rel), new UTF8Encoding(false, true)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException) { continue; }
            texts[rel] = text;
            if (!rel.EndsWith(".cs", StringComparison.Ordinal)) continue;
            foreach (Match match in CsNamespaceRe.Matches(text))
            {
                var ns = match.Groups[1].Value;
                if (!declaring.TryGetValue(ns, out var files)) declaring[ns] = files = new List<string>();
                if (!files.Contains(rel)) files.Add(rel);
            }
        }

        // csproj 的 ProjectReference 与 using 并列：项目级引用同样意味着 A 的代码依赖 B 的代码。
        // 归属口径：文件所在项目 = 从文件目录往上第一个恰好含一个 *.csproj 的目录（多项目目录不猜）；
        // 项目归属节点 = 该项目内已登记文件的多数归属节点（与 NamespaceOwner 同口径）。
        string? ProjectOf(string rel)
        {
            var dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
            while (dir.Length > 0)
            {
                var absolute = Path.Combine(_repository.Root, dir.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(absolute))
                {
                    var candidates = Directory.GetFiles(absolute, "*.csproj").OrderBy(path => path, StringComparer.Ordinal).ToList();
                    if (candidates.Count == 1) return dir + "/" + Path.GetFileName(candidates[0]);
                    if (candidates.Count > 1) return null;
                }
                dir = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
            }
            return null;
        }

        var projectOfFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in ownerOf.Keys)
        {
            var project = ProjectOf(rel);
            if (project is not null) projectOfFile[rel] = project;
        }

        var projectOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in projectOfFile.GroupBy(pair => pair.Value, StringComparer.Ordinal))
        {
            projectOwner[group.Key] = group.Select(pair => ownerOf[pair.Key])
                .GroupBy(owner => owner, StringComparer.Ordinal)
                .OrderByDescending(items => items.Count())
                .ThenBy(items => items.Key, StringComparer.Ordinal)
                .First().Key;
        }

        var projectTargets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var project in projectOwner.Keys)
        {
            var absolute = ResolvePath(project);
            string text;
            try { text = File.ReadAllText(absolute, new UTF8Encoding(false, true)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException) { continue; }
            var projectDir = project.Contains('/') ? project[..project.LastIndexOf('/')] : "";
            foreach (Match match in ProjectReferenceRe.Matches(text))
            {
                var include = match.Groups[1].Value.Trim().Replace('\\', '/');
                if (include.Length == 0) continue;
                var resolved = NormalizeRel(projectDir.Length == 0 ? include : projectDir + "/" + include);
                if (resolved is null || !projectOwner.ContainsKey(resolved)) continue;
                if (!projectTargets.TryGetValue(project, out var list)) projectTargets[project] = list = new List<string>();
                if (!list.Contains(resolved)) list.Add(resolved);
            }
        }

        string? NamespaceOwner(string ns)
        {
            if (!declaring.TryGetValue(ns, out var files)) return null;
            var groups = files.Where(file => ownerOf.ContainsKey(file))
                .GroupBy(file => ownerOf[file], StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();
            return groups.Count == 0 ? null : groups[0].Key;
        }

        string NamespaceFile(string ns, string owner)
            => declaring[ns].Where(file => ownerOf[file] == owner).OrderBy(file => file, StringComparer.Ordinal).First();

        // 已登记的代码联系：type=引用 的 link 也算登记（与 depends_on 等价）
        var referencePairs = new HashSet<(string, string)>();
        foreach (var link in LinksOfManifest())
        {
            if (StringOf(link, "type") != "引用") continue;
            if (StringOf(link, "from") is { } linkFrom && StringOf(link, "to") is { } linkTo)
                referencePairs.Add((linkFrom, linkTo));
        }

        var pairs = new List<(string Owner, string Target, string SourceRel, string TargetRel)>();
        var seenPair = new HashSet<(string, string)>();
        foreach (var (owner, rels) in filesOfNode)
        {
            var depends = new HashSet<string>(ListOf(_nodeMap[owner], "depends_on").OfType<string>(), StringComparer.Ordinal);
            foreach (var rel in rels.OrderBy(file => file, StringComparer.Ordinal))
            {
                if (!texts.TryGetValue(rel, out var text)) continue;
                var references = new List<(string Target, string File)>();
                if (rel.EndsWith(".cs", StringComparison.Ordinal))
                {
                    foreach (Match match in CsUsingRe.Matches(text))
                    {
                        var ns = match.Groups[1].Value;
                        if (ns == "static") continue;
                        var target = NamespaceOwner(ns);
                        if (target is null || target == owner) continue;
                        references.Add((target, NamespaceFile(ns, target)));
                    }
                    if (projectOfFile.TryGetValue(rel, out var project) &&
                        projectTargets.TryGetValue(project, out var targets))
                    {
                        foreach (var targetProject in targets)
                        {
                            var target = projectOwner[targetProject];
                            if (target == owner) continue;
                            references.Add((target, targetProject));
                        }
                    }
                }
                else
                {
                    var directory = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
                    foreach (Match match in JsImportRe.Matches(text))
                    {
                        var spec = match.Groups[1].Value;
                        if (!spec.StartsWith('.')) continue;
                        var resolved = NormalizeRel(directory.Length == 0 ? spec : directory + "/" + spec);
                        if (resolved is null) continue;
                        var candidate = OwnedFile(resolved, ownerOf);
                        if (candidate is null) continue;
                        var target = ownerOf[candidate];
                        if (target == owner) continue;
                        references.Add((target, candidate));
                    }
                }
                foreach (var (target, targetRel) in references)
                {
                    if (depends.Contains(target) || referencePairs.Contains((owner, target))) continue;
                    if (!seenPair.Add((owner, target))) continue;
                    pairs.Add((owner, target, rel, targetRel));
                }
            }
        }
        if (pairs.Count == 0) return;

        var perOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        var listed = 0;
        foreach (var pair in pairs)
        {
            var used = perOwner.TryGetValue(pair.Owner, out var count) ? count : 0;
            if (used >= 3 || listed >= 20) continue;
            perOwner[pair.Owner] = used + 1;
            listed++;
            Add("I", "R41", pair.Owner + " 的代码引用了 " + pair.Target + " 的 " + pair.TargetRel +
                "，但没有登记联系（来源 " + pair.SourceRel + "；既没有 depends_on，也没有 type=引用 的 link）；补法：" +
                "tree_add_link {\"from\":\"" + pair.Owner + "\",\"to\":\"" + pair.Target +
                "\",\"type\":\"引用\",\"note\":\"来源 " + pair.SourceRel + "\"}", pair.Owner);
        }
        Add("I", "R41", "共 " + pairs.Count + " 条代码引用未登记" +
            (listed < pairs.Count ? "，已列出 " + listed + " 条（每个节点最多 3 条，全树最多 20 条）" : ""));
    }

    private List<RawMap> LinksOfManifest()
        => _manifest.TryGetValue("links", out var raw) && raw is List<object?> list
            ? list.OfType<RawMap>().ToList()
            : new List<RawMap>();

    private static string? OwnedFile(string resolved, Dictionary<string, string> ownerOf)
    {
        foreach (var candidate in new[] { resolved, resolved + ".js", resolved + ".mjs", resolved + ".cjs",
                     resolved + "/index.js", resolved + "/index.mjs" })
            if (ownerOf.ContainsKey(candidate)) return candidate;
        return null;
    }

    private static string? NormalizeRel(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return parts.Count == 0 ? null : string.Join("/", parts);
    }

    // ------------------------------------------------------------------
    // R42 预设转正一致性
    // ------------------------------------------------------------------

    private static readonly string[] PresetPlaceholderPhrases = { "尚未建成", "未建成", "预设占位" };

    private void R42()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid) continue;
            // preset=true 的节点由 R35 管：files 非空已经报 E，这里不重复
            if (node.TryGetValue("preset", out var presetValue) && presetValue is true) continue;
            if (ListOf(node, "files").Count == 0) continue;
            var status = StatusValues.Normalize(StringOf(node, "status"));
            if (status is not ("已完成" or "已验证")) continue;
            var sources = new List<(string Rel, string Text)>();
            var introPath = _repository.IntroPath(nid);
            if (File.Exists(introPath))
            {
                try { sources.Add((_repository.IntroRel(nid), File.ReadAllText(introPath))); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            var shortPath = Path.Combine(_repository.Root, "00-程序树", "短介绍", nid + ".txt");
            if (File.Exists(shortPath))
            {
                try { sources.Add(("00-程序树/短介绍/" + nid + ".txt", File.ReadAllText(shortPath))); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            foreach (var (rel, text) in sources)
            {
                var phrase = PresetPlaceholderPhrases.FirstOrDefault(word => text.Contains(word, StringComparison.Ordinal));
                if (phrase is null) continue;
                Add("W", "R42", "节点已按建成登记（preset 不为 true、files 非空、状态" + status + "），但 " + rel +
                    " 仍写「" + phrase + "」：请改口，按实际功能描述，不要再说没建成", nid);
                break;
            }
        }
    }


    // ------------------------------------------------------------------
    // R43 备份配置结构（只校验 00-程序树/运维/备份.yaml 的形状与取值，不碰密钥/凭据）
    // ------------------------------------------------------------------

    private static readonly string[] BackupConfigKeys =
    {
        "enabled", "mode", "retention", "targets", "last_run", "last_tree_version", "note",
    };

    private void R43()
    {
        var path = Path.Combine(_repository.Root, "00-程序树", "运维", "备份.yaml");
        if (!File.Exists(path)) return;
        object? raw;
        try
        {
            raw = YamlLoader.LoadRawDocument(path);
        }
        catch (PTreeException exception)
        {
            Add("E", "R43", "备份配置无法解析：" + exception.Message);
            return;
        }
        if (raw is not RawMap map)
        {
            Add("E", "R43", "备份配置必须是映射（enabled / mode / retention / targets）");
            return;
        }
        foreach (var key in map.Keys)
        {
            if (!BackupConfigKeys.Contains(key, StringComparer.Ordinal))
                Add("W", "R43", "备份配置有未知字段：" + key + "（只认 " + string.Join(" / ", BackupConfigKeys) + "）");
        }
        bool enabled = false;
        if (map.TryGetValue("enabled", out var enabledValue))
        {
            var parsed = BackupSettings.ToBool(enabledValue);
            if (parsed is null) Add("E", "R43", "备份配置 enabled 必须是布尔值（true / false）");
            else enabled = parsed.Value;
        }
        if (map.TryGetValue("mode", out var modeValue))
        {
            if (modeValue is not string modeText) Add("E", "R43", "备份配置 mode 必须是字符串：git-bundle / tag / copy");
            else if (!BackupLayout.IsKnownMode(modeText))
                Add("E", "R43", "备份配置 mode 非法：" + modeText + "（只允许 git-bundle / tag / copy）");
        }
        if (map.TryGetValue("retention", out var retentionValue))
        {
            var retention = BackupSettings.ToInt(retentionValue);
            if (retention is null) Add("E", "R43", "备份配置 retention 必须是整数");
            else if (retention < 1 || retention > BackupService.MaxRetention)
                Add("E", "R43", "备份配置 retention 必须在 1-" + BackupService.MaxRetention + " 之间（收到 " + retention + "）");
        }
        if (map.TryGetValue("targets", out var targetsValue))
        {
            if (targetsValue is not List<object?> targets)
            {
                Add("E", "R43", "备份配置 targets 必须是列表");
            }
            else
            {
                foreach (var item in targets)
                {
                    if (item is not string text || string.IsNullOrWhiteSpace(text))
                        Add("E", "R43", "备份配置 targets 的每一项都必须是非空字符串（仓路径或节点 id）");
                }
                if (enabled && targets.Count == 0)
                    Add("W", "R43", "备份已启用但 targets 为空：不会产出任何备份点");
            }
        }
        else if (enabled)
        {
            Add("W", "R43", "备份已启用但缺少 targets 段：不会产出任何备份点");
        }
        if (map.TryGetValue("last_run", out var lastRun) && lastRun is not null && lastRun is not string)
            Add("E", "R43", "备份配置 last_run 必须是字符串或 null");
        if (map.TryGetValue("last_tree_version", out var lastVersion) && lastVersion is not null && lastVersion is not string)
            Add("E", "R43", "备份配置 last_tree_version 必须是字符串或 null");
    }

    // ------------------------------------------------------------------
    // R44 预设节点长期未转正（只提示）
    // ------------------------------------------------------------------

    /// <summary>预设转正的宽限天数：超过这么多天还挂着 preset 就是「占着位子不干活」。</summary>
    public const int PresetStaleDays = 30;

    private void R44()
    {
        var today = DateTime.Now.Date;
        foreach (var item in _nodes)
        {
            if (item is not RawMap node || NodeId(node) is not { } nid) continue;
            if (!(node.TryGetValue("preset", out var presetValue) && presetValue is true)) continue;
            var created = StringOf(node, "created_at");
            if (created is null || !DateTime.TryParseExact(created, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var createdDate)) continue;
            var days = (today - createdDate.Date).Days;
            if (days <= PresetStaleDays) continue;
            Add("I", "R44", "预设节点已 " + days + " 天没有转正（created_at " + created + "，宽限 " + PresetStaleDays +
                " 天）：要么转正（tree_update_node 传 preset=false 并补 files），要么退役（tree_retire_node）", nid);
        }
    }

    // ------------------------------------------------------------------
    // R45 策划文档缺口（只提示；只看策划树，程序树网格不受影响）
    // ------------------------------------------------------------------

    private static readonly string[] DocumentPlaceholders = { "", "未写" };
    private static readonly string[] UnmappedProgramPhrases = { "无", "-", "—", "未定", "待定", "待补" };

    private void R45()
    {
        TreeStore store;
        try { store = TreeStore.Load(_repository.Root); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PTreeException) { return; }
        var planning = store.FindDocument("策划树");
        if (planning is null) return;
        var names = planning.FieldNames.ToList();
        var programIndex = names.FindIndex(name => name == "对应程序");
        var documentIndex = names.FindIndex(name => name == "文档");
        // 没有文档列的老数据不报：缺列说明还没启用这套口径，不能凭空造缺口
        if (documentIndex < 0) return;
        foreach (var entry in planning.Entries)
        {
            if (entry.Fields.Count <= documentIndex) continue;
            var document = entry.Fields[documentIndex].Trim();
            if (!DocumentPlaceholders.Contains(document, StringComparer.Ordinal)) continue;
            var programs = programIndex >= 0 && entry.Fields.Count > programIndex ? entry.Fields[programIndex].Trim() : "";
            // 还没落到程序上的策划不该先有实现文档：空、占位词都跳过
            if (programs.Length == 0 || UnmappedProgramPhrases.Contains(programs, StringComparer.Ordinal)) continue;
            Add("I", "R45", "策划条目 " + entry.Id + " 有对应程序（" + Trim(programs, 60) + "）但没有文档：文档列现在是「" +
                (document.Length == 0 ? "空" : document) + "」，先写策划文档再把真实路径填进文档列", entry.Id);
        }
    }

    // ------------------------------------------------------------------
    // R46 已验证必须带证据
    // ------------------------------------------------------------------

    private void R46()
    {
        foreach (var item in _nodes)
        {
            if (item is not RawMap node) continue;
            if (StatusValues.Normalize(StringOf(node, "status")) != StatusValues.Verified) continue;
            var id = NodeId(node);
            if (id is null) continue;
            var evidence = node.TryGetValue("evidence", out var raw) && raw is List<object?> list
                ? list.OfType<RawMap>().ToList()
                : new List<RawMap>();
            if (evidence.Count == 0)
            {
                Add("E", "R46", "已验证却没有证据：evidence 至少要有 1 条 result=pass 的记录（用户确认写进 note）", id);
                continue;
            }
            var passed = evidence.Any(entry => string.Equals(StringOf(entry, "result"), "pass", StringComparison.Ordinal));
            if (!passed)
                Add("E", "R46", "已验证但 evidence 里没有 result=pass 的记录：验证结论必须可查", id);
        }
    }

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private bool IsDeadStatus(RawMap node)
        => node.TryGetValue("status", out var status) && status is string text && text is "退役" or "退役";

    private HashSet<string> Ancestors(string nid)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var current = _nodeMap.TryGetValue(nid, out var node) ? node : null;
        var guard = 0;
        while (current is not null && guard++ < 100)
        {
            var parent = StringOf(current, "parent");
            if (parent is null || !result.Add(parent)) break;
            current = _nodeMap.TryGetValue(parent, out var next) ? next : null;
        }
        return result;
    }

    private List<string> Lineage(string nid)
    {
        var chain = new List<string> { nid };
        var current = _nodeMap.TryGetValue(nid, out var node) ? node : null;
        var guard = 0;
        while (current is not null && guard++ < 100)
        {
            var parent = StringOf(current, "parent");
            if (parent is null || chain.Contains(parent)) break;
            chain.Add(parent);
            current = _nodeMap.TryGetValue(parent, out var next) ? next : null;
        }
        return chain;
    }

    private string? NearestCommonAncestor(string left, string right)
    {
        var rightSet = Lineage(right).ToHashSet(StringComparer.Ordinal);
        foreach (var nid in Lineage(left)) if (rightSet.Contains(nid)) return nid;
        return null;
    }

    private HashSet<string> DependsClosure(string nid)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        if (_nodeMap.TryGetValue(nid, out var node))
            foreach (var dep in ListOf(node, "depends_on")) if (dep is string text) stack.Push(text);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current)) continue;
            if (_nodeMap.TryGetValue(current, out var other))
                foreach (var dep in ListOf(other, "depends_on")) if (dep is string text) stack.Push(text);
        }
        seen.Remove(nid);
        return seen;
    }

    private void AppendIssues(List<Issue> issues) => _issues.AddRange(issues);
}
