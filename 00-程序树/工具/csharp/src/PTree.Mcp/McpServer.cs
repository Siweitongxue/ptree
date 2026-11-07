using System.Text;
using System.Text.Json;
using PTree.Core;

namespace PTree.Mcp;

/// <summary>
/// 手写 MCP stdio 服务（JSON-RPC 2.0，换行分隔帧）。
/// 约定：stdin/stdout 均为 UTF-8，一行一个 JSON 消息；stdout 只出协议帧，日志一律写 stderr。
/// 支持：initialize、notifications/initialized、tools/list、tools/call、ping；另支持 JSON-RPC 批量数组。
/// </summary>
public static class McpServer
{
    private static readonly JsonSerializerOptions FrameOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static int Run(string? root)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Log("ptree-mcp 启动：JSON-RPC 2.0 / 换行分隔帧 / 内核 " + KernelInfo.Version + (root is null ? "" : "（root=" + root + "）"));
        while (true)
        {
            string? line;
            try
            {
                line = stdin.ReadLine();
            }
            catch (IOException exception)
            {
                Log("stdin 读取失败：" + exception.Message);
                return 2;
            }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                HandleLine(line, root, stdout);
            }
            catch (Exception exception)
            {
                Log("处理消息失败：" + exception);
            }
        }
        Log("ptree-mcp 退出：stdin 已关闭");
        return 0;
    }

    private static void HandleLine(string line, string? root, StreamWriter stdout)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            WriteMessage(stdout, ErrorResponse(null, -32700, "Parse error：" + exception.Message));
            return;
        }
        using (document)
        {
            var element = document.RootElement;
            if (element.ValueKind == JsonValueKind.Array)
            {
                var responses = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    var response = HandleMessage(item, root);
                    if (response is not null) responses.Add(response);
                }
                WriteMessage(stdout, responses);
                return;
            }
            var single = HandleMessage(element, root);
            if (single is not null) WriteMessage(stdout, single);
        }
    }

    private static object? HandleMessage(JsonElement message, string? root)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return ErrorResponse(null, -32600, "Invalid Request：消息必须是 JSON 对象");
        }
        JsonElement? id = message.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null
            ? idElement.Clone()
            : null;
        var method = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString()
            : null;
        JsonElement? parameters = message.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object
            ? paramsElement
            : null;
        if (method is null)
        {
            return id is null ? null : ErrorResponse(id, -32600, "Invalid Request：缺少 method");
        }
        if (method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            Log("收到通知：" + method);
            return null;
        }
        if (id is null)
        {
            Log("忽略无 id 的请求：" + method);
            return null;
        }
        switch (method)
        {
            case "initialize":
            {
                var protocolVersion = parameters is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String
                    ? version.GetString()
                    : "2024-11-05";
                return SuccessResponse(id, new Dictionary<string, object?>
                {
                    ["protocolVersion"] = protocolVersion,
                    ["capabilities"] = new Dictionary<string, object?>
                    {
                        ["tools"] = new Dictionary<string, object?> { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = KernelInfo.ServerName,
                        ["version"] = KernelInfo.Version,
                    },
                    ["instructions"] = "程序树 MCP 服务：AI 只通过工具读写，不接触文件、不写 YAML。先 tree_status 确认树是绿的；tree_find 定位；tree_read_node 默认读一张 9 行以内的卡（depth=N 展开 N 层关系，0 = 全部；format=full 才返回原 JSON）；新增节点一次一个用 tree_add_node；关系用 tree_add_link；改字段用 tree_update_node；退役用 tree_retire_node。所有写操作自动校验、失败回滚、追加台账，并返回写入后的 tree_version。另有 tree_guard 常检（一切操作前先跑一次）、tree_relocate 迁移登记路径、tree_delete 彻底删除（不可恢复，退役用 tree_retire_node）、tree_scan 扫描 adapt 登记目录（只读）、tree_commit 把一次写操作的 written_files 归组提交。另有 tree_backup_* 六个工具管理自动备份（附属功能、默认关闭）：启用前必须先 tree_backup_setup 完成配置（token/用户名加密存全局目录），再 tree_backup_config enabled=true；tree_backup_now 立即备份一次，tree_backup_list / tree_backup_verify 查备份点与其可读性。",
                });
            }
            case "ping":
                return SuccessResponse(id, new Dictionary<string, object?>());
            case "tools/list":
                return SuccessResponse(id, new Dictionary<string, object?> { ["tools"] = ToolDefinitions() });
            case "tools/call":
                return CallTool(id, parameters, root);
            default:
                return ErrorResponse(id, -32601, "Method not found：" + method);
        }
    }

    private static object CallTool(JsonElement? id, JsonElement? parameters, string? root)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value)
        {
            return ErrorResponse(id, -32602, "Invalid params：tools/call 需要 params.name 与 params.arguments");
        }
        if (!value.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return ErrorResponse(id, -32602, "Invalid params：缺少工具名 name");
        }
        var toolName = nameElement.GetString() ?? string.Empty;
        JsonElement? arguments = value.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object
            ? argsElement
            : null;
        if (toolName is "program_tree_propose" or "program_tree_apply" or "propose" or "apply")
        {
            return ErrorResponse(id, -32601, "Method not found：工具 " + toolName + " 已撤销；节点级写入请用 tree_add_node / tree_add_link / tree_update_node / tree_retire_node");
        }
        try
        {
            var text = toolName switch
            {
                "tree_status" => NodeTools.Status(root, arguments),
                "tree_map" => NodeTools.Map(root, arguments),
                "tree_guide" => NodeTools.Guide(root),
                "tree_links" => NodeTools.Links(root, arguments),
                "tree_read_group" => NodeTools.ReadGroup(root, arguments),
                "tree_check" => NodeTools.Check(root),
                "tree_add_node" => NodeTools.AddNode(root, arguments),
                "tree_add_group" => NodeTools.AddGroup(root, arguments),
                "tree_update_group" => NodeTools.UpdateGroup(root, arguments),
                "tree_add_link" => NodeTools.AddLink(root, arguments),
                "tree_read_node" => NodeTools.ReadNode(root, arguments),
                "tree_update_node" => NodeTools.UpdateNode(root, arguments),
                "tree_retire_node" => NodeTools.RetireNode(root, arguments),
                "tree_find" => NodeTools.Find(root, arguments),
                "tree_guard" => NodeTools.Guard(root, arguments),
                "tree_relocate" => NodeTools.Relocate(root, arguments),
                "tree_delete" => NodeTools.Delete(root, arguments),
                "tree_scan" => NodeTools.Scan(root),
                "tree_changes" => NodeTools.Changes(root, arguments),
                "tree_commit" => NodeTools.GitSync(root, arguments),
                "tree_backup_status" => BackupService.Status(root, arguments),
                "tree_backup_config" => BackupService.Config(root, arguments),
                "tree_backup_setup" => BackupService.Setup(root, arguments),
                "tree_backup_now" => BackupService.Now(root, arguments),
                "tree_backup_list" => BackupService.List(root, arguments),
                "tree_backup_verify" => BackupService.Verify(root, arguments),
                _ => null,
            };
            if (text is null)
            {
                return ErrorResponse(id, -32601, "Method not found：工具不存在或已撤销：" + toolName);
            }
            return SuccessResponse(id, new Dictionary<string, object?>
            {
                ["content"] = new[]
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
                },
                ["isError"] = IsErrorPayload(text),
            });
        }
        catch (PTreeException exception)
        {
            return SuccessResponse(id, ToolContent("{\"ok\": false, \"error\": " + JsonSerializer.Serialize(exception.Message, FrameOptions) + "}", true));
        }
        catch (Exception exception)
        {
            Log("工具异常 " + toolName + "：" + exception);
            return SuccessResponse(id, ToolContent("{\"ok\": false, \"error\": " + JsonSerializer.Serialize(exception.Message, FrameOptions) + "}", true));
        }
    }

    private static object ToolContent(string text, bool isError) => new Dictionary<string, object?>
    {
        ["content"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } },
        ["isError"] = isError,
    };

    private static bool IsErrorPayload(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                return document.RootElement.TryGetProperty("error", out _);
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<object?> ToolDefinitions() => new()
    {
        Tool("tree_add_node", "在指定 parent 下新增一个节点（一次一个）。自动生成 id（parent 前缀 + 英文短名）、layer、created_at；写入说明正文，落盘后立即全量校验，失败自动回滚并返回 errors。AI 不需要接触任何文件。", new Dictionary<string, object?>
        {
            ["parent"] = Property("string", "父节点 id；新节点挂在它下面"),
            ["name"] = Property("string", "节点名称（中文或英文均可）"),
            ["slug"] = Property("string", "英文短名（小写字母/数字/连字符），用于生成 id；不传则从 name 推导，中文名建议直接传"),
            ["kind"] = Property("string", "节点类型：department / program / step / external", new[] { "department", "program", "step", "external" }),
            ["responsibility"] = Property("string", "一句话职责"),
            ["inputs"] = ArrayProperty("输入契约 id 列表"),
            ["outputs"] = ArrayProperty("输出契约 id 列表"),
            ["depends_on"] = ArrayProperty("依赖的节点 id 列表；不传默认依赖 parent"),
            ["files"] = ArrayProperty("节点代码文件相对路径列表（可空；preset 必须为空）"),
            ["acceptance"] = ArrayProperty("验收清单（可空，程序/步骤会补默认项）"),
            ["preset"] = Property("boolean", "true 表示占位预设节点：状态未完成、files 必须为空、必须带 outputs 或 outputs_none=true"),
            ["outputs_none"] = Property("boolean", "preset 明确没有输出契约时传 true；与 outputs 二选一"),
            ["intro"] = Property("string", "节点说明正文全文：写清定位、边界、代码放哪、怎么改、常见坑"),
            ["short_intro"] = Property("string", "一行短介绍（≤80 字），写这个节点有什么用"),
            ["group"] = Property("string", "可选：加进哪个功能块（组 id），不传则只按部门自动归组"),
            ["phase"] = Property("string", "阶段 id；不传则继承 parent"),
            ["propose_only"] = Property("boolean", "true 时只返回将要新增的节点，不落盘；默认 false"),
        }, new[] { "parent", "name", "kind", "responsibility", "intro" }),
        Tool("tree_add_link", "给两个已存在节点加一条有类型的连接；校验两端存在、type 合法、不重复。type 白名单与 R40 校验共用 LinkKinds 一份：引用（代码互引）/ 测试 / 探针 / 覆盖 / 数据 / 控制 / 基址（A 是 B 的基址或被继承脚本）。", new Dictionary<string, object?>
        {
            ["from"] = Property("string", "起点节点 id"),
            ["to"] = Property("string", "终点节点 id"),
            ["type"] = Property("string", "连接类型（白名单来自 Models.LinkKinds，与 R40 共用）", LinkKinds.All),
            ["note"] = Property("string", "可选说明；写清联系依据（代码位置、命令或文档）"),
        }, new[] { "from", "to", "type" }),
        Tool("tree_read_node", "读一个节点：默认返回一张 9 行以内的纯文本卡（定位行、父子兄、职责、上下游契约、依赖、相关策划/美术/UI 与波及、显式连接、文件与说明路径）。depth=1 是一圈（默认）；depth=N 从该节点往外 N 层；depth=0 全部展开（上限 200 节点 / 400 边）；fmt=full 返回原 JSON。", new Dictionary<string, object?>
        {
            ["id"] = Property("string", "节点 id"),
            ["depth"] = Property("integer", "关系展开层数：1 = 一圈（默认）；N = 从该节点往外 N 层；0 = 全部，直到关联合并完，上限 200 节点 / 400 边；上限 8 层"),
            ["format"] = Property("string", "输出格式：card（默认，纯文本卡/图）；full（原来的完整 JSON，键集合不变）", new[] { "card", "full" }),
        }, new[] { "id" }),
        Tool("tree_update_node", "只修改传入的字段，其余保持不变；返回 diff 摘要。未传的字段不会被清空。改说明正文用 intro：intro 传说明正文全文，会整篇替换；改一行功能摘要用 short_intro：写 00-程序树/短介绍/<id>.txt，去空白后不超过 80 字、不含占位词。网格条目（编号如 D01、M01、F01）只支持 short_intro，写 00-程序树/短介绍/<编号>.txt。", new Dictionary<string, object?>
        {
            ["id"] = Property("string", "节点 id"),
            ["name"] = Property("string", "新名称"),
            ["responsibility"] = Property("string", "新的一句话职责"),
            ["not_responsible"] = ArrayProperty("新的不负责清单"),
            ["inputs"] = ArrayProperty("新的输入契约 id 列表"),
            ["outputs"] = ArrayProperty("新的输出契约 id 列表"),
            ["depends_on"] = ArrayProperty("新的依赖节点 id 列表"),
            ["files"] = ArrayProperty("新的代码文件路径列表"),
            ["acceptance"] = ArrayProperty("新的验收清单"),
            ["status"] = Property("string", "新状态", new[] { "未完成", "已完成", "已验证", "退役" }),
            ["phase"] = Property("string", "新阶段 id"),
            ["spec"] = Property("string", "新 SPEC 路径"),
            ["evaluation"] = Property("string", "评价正文；写了至少 40 字，禁止待补、略等占位"),
            ["preset"] = Property("boolean", "preset 转正时传 false 去掉占位标记；files 非空时不能仍为 true"),
            ["preset_outputs"] = Property("string", "preset 无输出时写 none"),
            ["intro"] = Property("string", "intro 传说明正文全文，会整篇替换（不传则不改说明）"),
            ["short_intro"] = Property("string", "一行短介绍，写 00-程序树/短介绍/<id>.txt；去空白后 ≤80 字，不得含待补/TODO/TBD"),
            ["evidence"] = ArrayProperty("证据清单：每项 {test, at, result: pass|fail, note}；标「已验证」前必须先补，R13/R46 都会拦"),
            ["notes"] = Property("string", "状态注释：为什么是这个状态、还差什么；写给人看的补充说明"),
        }, new[] { "id" }),
        Tool("tree_add_group", "新建功能块（打组）：把多个节点并成一个有名字、一行简介、详细介绍的块，成员可以是节点或别的组，一个节点可以进多个块；id 自动生成 group.<slug>。改介绍用 intro（写 00-程序树/说明/group.<slug>.txt 全文）。", new Dictionary<string, object?>
        {
            ["name"] = Property("string", "功能块名字，例如 程序树工具链"),
            ["slug"] = Property("string", "可选英文短名；不传按 name 生成"),
            ["nodes"] = ArrayProperty("并入的节点 id 列表（至少 1 个；也可以传组 id）"),
            ["description"] = Property("string", "一行简介，不超过 60 字"),
            ["intro"] = Property("string", "详细介绍正文全文，建议 ≥80 字：这个块解决什么问题、边界、成员怎么配合"),
            ["parent"] = Property("string", "可选父组 id；新组会成为它的成员"),
        }, new[] { "name", "nodes" }),
        Tool("tree_update_group", "改功能块：名字、一行简介、父组、成员（members 整体替换，或 add_members / remove_members 增删）、详细介绍 intro。会校验成员与父组真实存在且父组不成环。", new Dictionary<string, object?>
        {
            ["id"] = Property("string", "功能块 id"),
            ["name"] = Property("string", "新名字"),
            ["description"] = Property("string", "新的一行简介（≤60 字）"),
            ["parent"] = Property("string", "新父组 id；传空串表示提升为顶层组"),
            ["members"] = ArrayProperty("成员整体替换"),
            ["add_members"] = ArrayProperty("要加进来的节点或组 id"),
            ["remove_members"] = ArrayProperty("要移出的节点或组 id"),
            ["intro"] = Property("string", "详细介绍正文全文，整篇替换"),
        }, new[] { "id" }),
        Tool("tree_retire_node", "退役节点：不删除，写理由与替代节点，状态改为退役。", new Dictionary<string, object?>
        {
            ["id"] = Property("string", "节点 id"),
            ["reason"] = Property("string", "退役理由（必填）"),
            ["replacement"] = Property("string", "替代节点 id（可选）"),
        }, new[] { "id", "reason" }),
        Tool("tree_guard", "程序树常检：只报三类重要警告（文件消失、已完成/已验证却无实现文件、节点登记目录整体不存在），加 validate 计数，返回 ≤10 行纯文本卡；常规代码 mtime 更新不在此报，见 tree_changes。", new Dictionary<string, object?>
        {
            ["format"] = Property("string", "输出格式：card（默认纯文本卡）；full（完整 JSON）", new[] { "card", "full" }),
            ["limit"] = Property("integer", "卡上最多列几条问题，默认 5，最多 20"),
        }),
        Tool("tree_relocate", "把节点登记的代码路径迁到新位置（例如程序被移动了），可同时换 parent。写入前校验新路径必须存在、新 parent 必须存在且不成环；落盘后复跑 validate，有 E 就回滚。返回旧路径→新路径的 diff 摘要。只改清单登记，不动磁盘上的代码。", new Dictionary<string, object?>
        {
            ["id"] = Property("string", "节点 id"),
            ["files"] = ArrayProperty("新的代码文件相对路径列表（至少 1 条，且每条必须已存在）"),
            ["parent"] = Property("string", "可选新 parent 节点 id；必须存在且不成环"),
            ["reason"] = Property("string", "可选迁移原因"),
        }, new[] { "id", "files" }),
        Tool("tree_delete", "彻底删除一个节点：从 nodes 段删除、从 groups.members 删除、删除以它为 from/to 的 links、从 contracts 的 producers/consumers 删除并移除空契约、写一条台账（chg）。有子节点或仍有节点 depends_on 它时拒绝。删除不可恢复；只想下架请用 tree_retire_node。", new Dictionary<string, object?>
        {
            ["id"] = Property("string", "节点 id"),
            ["reason"] = Property("string", "删除原因（至少 8 个字；删除不可恢复，必须写清）"),
        }, new[] { "id", "reason" }),
        Tool("tree_find", "按关键词或状态找节点，返回 id / 名称 / 状态 / 一句话职责 / short_intro，最多 20 条。", new Dictionary<string, object?>
        {
            ["query"] = Property("string", "关键词：匹配 id、名称、职责"),
            ["status"] = Property("string", "按状态过滤", new[] { "未完成", "已完成", "已验证", "退役" }),
            ["kind"] = Property("string", "按类型过滤", new[] { "root", "department", "program", "step", "external" }),
            ["limit"] = Property("integer", "最多返回条数，默认 20，最大 20"),
        }),
        Tool("tree_status", "树状态：默认不超过 8 行纯文本（版本与节点数、状态分布、层分布、各部门节点数、七张网格条目数、校验计数、入口路径）；format=full 返回原 JSON。数字现算。", new Dictionary<string, object?>
        {
            ["format"] = Property("string", "输出格式：card（默认纯文本）；full（原 JSON）", new[] { "card", "full" }),
        }),
        Tool("tree_map", "整棵树粗读，先粗后细：mode=blocks（默认）只给功能块与部门分区（每块成员数 + 一行简介）；mode=names 给全部名称 + 联系（子数、依赖）；mode=summary 再加一行短介绍。一次看完全项目骨架，比逐点读卡省 token；要契约明细、文件或改法再对单个节点用 tree_read_node。", new Dictionary<string, object?>
        {
            ["mode"] = Property("string", "blocks（功能块/部门分区，默认）；names（名称+联系）；summary（再加短介绍）", new[] { "blocks", "names", "summary" }),
        }),
        Tool("tree_guide", "阅读阶梯一句话版（≤10 行）：先看哪一层、用哪个工具、每层多大。不确定先读什么时先调它。", new Dictionary<string, object?>()),
        Tool("tree_links", "显式联系清单：每行 from -[类型]-> to · 依据；没写依据的标出来。可按节点过滤。联系的要求是「必须标注、要全、要准确」——结构错由 R40 拦，静态引用漏标由 R41 提示。", new Dictionary<string, object?>
        {
            ["node"] = Property("string", "可选：只看与这个节点有关的联系"),
        }),
        Tool("tree_read_group", "组内视图：列出某功能块/部门组里的成员。mode=names 只给成员名；mode=summary 每行再加一行短介绍与用法（首个代码文件，或「无代码」）。组 id 用 tree_map mode=blocks 查。", new Dictionary<string, object?>
        {
            ["id"] = Property("string", "组 id，例如 group.tree-toolchain 或 group.dept-info"),
            ["mode"] = Property("string", "names（默认，成员名）；summary（加简介与用法）", new[] { "names", "summary" }),
        }, new[] { "id" }),
        Tool("tree_check", "跑完整校验（清单 R01-R46 + 跨树 T01-T12），返回错误明细。", new Dictionary<string, object?>()),
        Tool("tree_changes", "查常规文件变更记录（代码 mtime 晚于节点 updated_at，静默记录）：返回短文本，每行「日期 节点id 文件 晚N天」。", new Dictionary<string, object?>
        {
            ["node"] = Property("string", "可选节点 id"),
            ["days"] = Property("integer", "最近天数，默认 30"),
            ["limit"] = Property("integer", "最多返回条数，默认 20"),
        }),
        Tool("tree_scan", "按清单 adapt 段扫描登记目录：列出已归属、未归属与未匹配文件，未归属按最长路径前缀给建议；只读，不写清单。", new Dictionary<string, object?>()),
        Tool("tree_commit", "把一次写操作返回的 written_files 按路径归组后提交；commit message 默认取台账最新 chg 条目。无改动不产生空提交；git 不可用返回 ok=false。", new Dictionary<string, object?>
        {
            ["written_files"] = ArrayProperty("本次写操作返回的 written_files"),
            ["message"] = Property("string", "可选 commit message；不传则用台账最新 chg 条目摘要"),
        }, new[] { "written_files" }),
        Tool("tree_backup_status", "备份状态：enabled、mode、retention、targets 数量、凭据状态（已配置/未配置/脱敏用户名）、上次备份时间、备份点数量、磁盘占用。备份是附属功能、默认关闭；enabled=true 但未配置时会给出警告。", new Dictionary<string, object?>()),
        Tool("tree_backup_config", "改备份配置：enabled / mode / retention / targets（整体替换）或 add_targets / remove_targets（增删）。改 enabled=true 时若全局密钥或加密凭据缺失，拒绝启用并提醒先跑 tree_backup_setup。不传任何字段时只返回当前配置。", new Dictionary<string, object?>
        {
            ["enabled"] = Property("boolean", "是否启用自动备份；默认 false（关闭）"),
            ["mode"] = Property("string", "备份方式", new[] { "git-bundle", "tag", "copy" }),
            ["retention"] = Property("integer", "同一 target 保留多少个备份点，默认 10，范围 1-1000"),
            ["targets"] = ArrayProperty("整体替换 targets：内层 git 仓路径（相对项目根或绝对路径）或节点 id"),
            ["add_targets"] = ArrayProperty("追加 targets（去重）"),
            ["remove_targets"] = ArrayProperty("移除 targets"),
        }),
        Tool("tree_backup_setup", "写入/更新备份凭据：username 与 token 用 AES-256-GCM 加密后落全局目录（%USERPROFILE%\\.ptree\\credentials.enc，不随项目仓库走；可用 PTreeBackupHome 覆盖）。token 传空表示只改用户名。返回值只含脱敏摘要（token 前 4 后 2），明文 token 绝不回显。第一次配置会自动生成 32 字节全局密钥并回报 key_created=true。", new Dictionary<string, object?>
        {
            ["username"] = Property("string", "备份账号用户名（可全显）"),
            ["token"] = Property("string", "访问令牌；传空表示只改用户名，保留原 token"),
        }, new[] { "username" }),
        Tool("tree_backup_now", "立即备份一次：对每个 target 执行 git bundle create（或 tag / copy），记录该仓当时 HEAD 的 commit，写 00-程序树/运维/备份.jsonl，并按 retention 裁掉超出的旧备份点。未启用时拒绝并提示先跑 tree_backup_setup + tree_backup_config。", new Dictionary<string, object?>()),
        Tool("tree_backup_list", "列备份点：按 tree_version 倒序，可按 target 过滤、限制条数；trimmed 记录默认不列（include_trimmed=true 才列）。", new Dictionary<string, object?>
        {
            ["target"] = Property("string", "只看这个 target 的备份点"),
            ["limit"] = Property("integer", "最多返回条数，默认 20，最大 500"),
            ["include_trimmed"] = Property("boolean", "是否包含被 retention 裁掉的 trimmed 记录，默认 false"),
        }),
        Tool("tree_backup_verify", "校验备份点：对 bundle 类型记录跑 git bundle verify，返回每个备份点是否可读。", new Dictionary<string, object?>
        {
            ["target"] = Property("string", "只校验这个 target"),
            ["limit"] = Property("integer", "最多校验条数，默认 50，最大 200"),
        }),
    };

    private static Dictionary<string, object?> Tool(string name, string description, Dictionary<string, object?> properties, string[]? required = null)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required is { Length: > 0 }) schema["required"] = required;
        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }

    private static Dictionary<string, object?> Property(string type, string description, string[]? enumValues = null)
    {
        var property = new Dictionary<string, object?> { ["type"] = type, ["description"] = description };
        if (enumValues is not null) property["enum"] = enumValues;
        return property;
    }

    private static Dictionary<string, object?> ArrayProperty(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
    };

    private static object SuccessResponse(JsonElement? id, object result) => new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static object ErrorResponse(JsonElement? id, int code, string message) => new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
    };

    private static void WriteMessage(StreamWriter stdout, object? payload)
    {
        stdout.WriteLine(JsonSerializer.Serialize(payload, FrameOptions));
    }

    private static void Log(string message)
    {
        Console.Error.WriteLine("[ptree-mcp] " + message);
    }
}