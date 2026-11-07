# PTree C# 内核（现役）

程序树内核已从 Python（ptree.py，2026-11-06 冻结，仅作迁移对照）迁到 C#/.NET 10，现役入口是 ptree-cs。迁移分段记录（历史，已完结）：index/node → validate 骨架 → R01-R24 逐段补齐 → R25-R43 增量规则；当前 **R01-R46** 与跨树 T01-T12 全部在 C# 侧实现，Python 版不再产出判据。

## 结构

- `src/PTree.Core`：清单/契约/标杆/绑定/说明加载，index/node，validate R01-R46（R30 仅 apply/check-plan，不在只读 validate），Bench 提供进程内计时（bench 子命令），LinkKinds 是联系类型白名单唯一来源（写入口、R40、MCP 枚举共用），RawYaml 按 PyYAML 语义解析，ProgramWalker 复刻 os.walk，TreeInstances 读取四份树实例，Reporting 提供 stats/台账/生成物辅助，BackupCrypto/BackupConfig/BackupService 提供自动备份与凭据加密。CLI 逻辑不进入 Core。
- `src/PTree.Cli`：薄外壳。命令：`status`、`validate`、`check`、`verify`、`index`、`node <id>`（仓库节点或树实例 id 回退）、`read-node <id>`、`stats`、`orphans`、`view`、`order`、`book`、`brief`、`benchmark [--node]`、`bench [--repeat N] [--node <id>] [--json]`（进程内测各命令耗时 + 冷启动自测，不开子进程）、`log`、`tree`、`adapt`、`git-sync`、`backup status|config|setup|now|list|verify`（自动备份，默认关闭）、`apply`（默认 dry-run，`--write` 落地）、`check-plan`（dry-run）、`init`（新项目脚手架上手，`--name` 指定项目名、`--force` 覆盖前先打印清单）。
- `tests/PTree.Tests`：xunit + golden/parity + 变异树 + PyYAML oracle + splitlines + walker oracle。

## 构建 / 运行

    dotnet build 00-程序树/工具/csharp/PTree.sln
    dotnet test  00-程序树/工具/csharp/tests/PTree.Tests
    dotnet run --project 00-程序树/工具/csharp/src/PTree.Cli -- index --root "examples/demo"
    dotnet run --project 00-程序树/工具/csharp/src/PTree.Cli -- node core.app --root "examples/demo"
    dotnet run --project 00-程序树/工具/csharp/src/PTree.Cli -- validate --json --root "examples/demo"

`validate --json` 输出键固定为 `ok/errors/warnings/notes`，每个条目恰好 `code/node/message`；未迁移规则在 C# 侧为空。

## parity 对比

`index` / `node` 全文件对比（validate/log 仍由 2026-11-06 冻结的 Python 参考填充，仅作对照，保证 71 文件全量对照）：

    bash 00-程序树/工具/csharp/parity/run-parity.sh

迁移期规则对比脚本（历史基线：R01-R19、R21-R24 的条目与 2026-11-06 冻结的 Python 参考逐条相同；现役 R01-R46 以 C# 为准）：

    dotnet build 00-程序树/工具/csharp/PTree.sln -v q
    dotnet 00-程序树/工具/csharp/src/PTree.Cli/bin/Debug/net10.0/ptree-cs.dll validate --json --root "examples/demo" > 00-程序树/工具/csharp/out/validate-candidate.json
    python 00-程序树/工具/csharp/parity/compare-validate.py --golden 00-程序树/工具/parity/golden/validate.json --candidate 00-程序树/工具/csharp/out/validate-candidate.json

或直接：

    bash 00-程序树/工具/csharp/parity/run-validate-parity.sh

## 当前结果（2026-11-07）

- golden 已由 Lead 于 2026-10-30 22:35 重新 capture（71 文件），当前工作树直接跑 `run-parity.sh` 为 71/71 一致、差异 0。
- `run-parity.sh` 中 validate.json 仍取 2026-11-06 冻结的 Python 参考（仅作对照，全文件对照用）；现役 validate 是 R01-R46，用 `run-validate-parity.sh` 做分段验收，就那段历史规则而言 golden=0 / C#=0、序列一致、差异 0。
- 测试：**463 个 xunit 全绿**；`ValidationTests` 覆盖 golden 过滤对比、同树 C# vs Python 有序相等、R01-R06 的 6 个变异树、R11/R12 的 10 个变异树、R07 的 9 个变异树、R08 的 10 个变异树、R09/R10 的 8 个契约变异树、R14/R16 的 12 个变异树、R13 的 10 个变异树、R15 的 9 个变异树、R17 的 9 个变异树、R21/R22 的 7 个变异树、R18-SPEC 的 10 个变异树、R18-program.yaml 的 8 个变异树、R19 的 walker oracle 与 9 个变异树，以及 R23 的 20/21 边界、R24 的 10 个豁免用例、R40 的基址联系与写入口白名单、R41 的 csproj ProjectReference 与补法参数、R44 预设过期、R45 策划文档缺口；`YamlOracleTests` 有 23 个 PyYAML parse-oracle 用例；`PythonTextTests` 覆盖 splitlines 九类换行、归一化、lower 特例与 %.2f 舍入；`PerformanceTests` 用宽松预算（约实测 3 倍）守 validate / read-node / map 的退化，并校验 bench 的采样与冷启动。

## 运行环境（oracle 依赖）

- Python 3.11.9
- PyYAML 6.0.3（`yaml.safe_load` 为解析语义权威）
- YamlDotNet 18.1.0（仅作为 C# YAML 解析实现）
- oracle 语料在 `tests/PTree.Tests/YamlOracleTests.cs`；升级上述任一版本后重新生成/重跑该测试，差异必须修复或登记为已接受差异。



## 自动备份（附属功能，默认关闭）

备份把「树的每次完整改动」（一个 tree_version 快照）映射到对应内层 git 仓的备份点。默认 `enabled: false`：不开就什么都不做、不产文件。规范见 `00-程序树/规范/备份_v1.txt`。

- 配置两层：项目层 `00-程序树/运维/备份.yaml`（enabled / mode / retention / targets / last_run，非敏感，进 git）；全局层 `%USERPROFILE%\.ptree\global.yaml`（32 字节密钥 + 全局默认值）与 `%USERPROFILE%\.ptree\credentials.enc`（加密凭据），不进 git，可用环境变量 `PTreeBackupHome` 改目录。
- 加密：AES-256-GCM，随机 12 字节 nonce，密文 = base64(nonce|tag|ciphertext)；密钥缺失报「未配置」，不静默生成；任何输出 token 只显示前 4 后 2。
- 产物：`00-程序树/运维/备份/<tree_version>/<repo>.bundle` + 台账 `00-程序树/运维/备份.jsonl`（{at, tree_version, kind, target, commit, bundle, bytes}）；同 target 只留最近 `retention`（默认 10）个，超出删 bundle 并追加 `kind=trimmed` 记录；`00-程序树/运维/备份/` 已进 .gitignore。
- 未配置：`tree_backup_config enabled=true` 若全局密钥或凭据缺失，拒绝启用并提醒先跑 `tree_backup_setup`；`tree_guard` / `health` 在 enabled=true 但未配置时给一条 W；R43 只校验备份配置结构（E/W）。

MCP 工具（6 个，与 CLI `backup` 同实现）：

| 工具 | 作用 |
| --- | --- |
| `tree_backup_status` | enabled、mode、retention、targets 数、凭据状态（已配置/未配置/脱敏用户名）、上次备份时间、备份点数量、磁盘占用 |
| `tree_backup_config` | 改 enabled / mode / retention / targets（整体替换、add_targets、remove_targets）；enabled=true 时做未配置拦截 |
| `tree_backup_setup` | 写入/更新凭据（username、token），加密落全局目录，token 传空只改用户名；返回脱敏摘要 |
| `tree_backup_now` | 立即备份一次，返回每个 target 的 commit、bundle 路径、字节数、耗时 |
| `tree_backup_list` | 列备份点（按 tree_version 倒序，可按 target 过滤、限条数） |
| `tree_backup_verify` | 对 bundle 跑 `git bundle verify`，返回可读性 |

验证（2026-11-07）：Debug/Release build 0 错误（25 条 xUnit 分析器警告属既有）；dotnet test 463 全过；validate E=0 W=0 I=168（其中 R45 策划文档缺口 59 条 = 策划树 59 条有对应程序的条目，文档列统一「未写」）；check E=0 W=0 I=0；verify 9 PASS；R43 对现役备份配置不产条目、R44 对 3 个近期预设不命中。

CLI：`ptree-cs backup status`、`backup config [--enable|--disable|--enabled true|false] [--mode ...] [--retention N] [--add-target ...] [--remove-target ...]`、`backup setup --username <u> [--token <t>]`、`backup now`、`backup list [--target ...] [--limit N] [--include-trimmed]`、`backup verify`。

## CLI 与树实例

- `tree [--json]`：读取 `00-程序树/树实例/{程序树,策划树,美术树,UI树}.txt`，输出四份摘要（名称、版本、路径、条目数、id 样例）。
- `node <id>`：优先取 program-tree.yaml 节点；不存在时回退到树实例文档，按实例 id 返回名称、字段与原始行。
- `view/order/book/brief/benchmark`：生成到 `00-程序树/生成/`；`log` 读 `00-程序树/变更/台账.jsonl`；`apply` 默认 dry-run，加 `--write` 才写盘（写后整树复跑 validate，出现 E 即回滚），`check-plan` 只做 dry-run 不写盘。

## 已接受差异（R20 近似扫描器）

R20 采用受限自写 Python import 扫描器，不引入 native 依赖。相对 Python 版 ptree.py（2026-11-06 冻结，仅作对照）的 `ast` 判据，明确接受以下差异：

1. 仅识别 `import X` 与 `from X import ...` 的模块名；相对导入（`from .x`）按 Python 规则跳过。
2. 扫描器跳过注释、普通字符串、单/双/三引号字符串与 f-string 文本；不解析 f-string 内部的表达式，也不做完整语法分析；无法解析的 Python 文件不会像 `ast.parse` 那样报 SyntaxError，只做尽力扫描。
3. 标准库名单使用 Python 3.11 的常用 `stdlib_module_names` 快照（硬编码），不是运行时完整集合；遇到未列入且非本地/共享的第三方导入时可能误报跨节点 import。
4. JS/TS 文件只产生“v1 未实现”的 I 级提示，不做 import 提取（与 2026-11-06 冻结的 Python 版一致）。
5. 以上差异只影响 R20 的近似边界；当前工作树 R20 与 golden 完全一致。等 oracle 语料足够后再逐条收紧。

## 通用树模型与跨树检查

- Core 的 TreeStore 用一套模型读取四份树实例：TreeDocument(kind,title,version,fieldNames,entries)、TreeEntry(id,fields,lineNo)、TreeReference；四棵树只靠配置的 id 规则与引用字段区分，没有为某棵树写死解析分支。
- 坏行/无法识别的 id、重复 id、字段数少于声明字段都会产生 T00 错误，不静默跳过。
- ptree-cs check 独立于 validate：T01 程序→策划引用/豁免、T02 策划确认状态、T03 断链、T04 美术字段、T05 UI 字段、T06 状态三档+退役（旧六值给 I 级映射提示）。当前数据下 check 为 E=0 W=0 I=0，退出码 0；check 与 validate 各自独立，互不替代。
- tree --json 输出四棵树的 fields/entries/line、跨树 references、程序树三档状态映射（含旧六值的映射结果），不写 生成/。
- node <id> 优先 program-tree.yaml，其后按程序树、策划树、美术树、UI树顺序跨树查找，冲突时程序树优先。
- 生产命令 view/order/book/benchmark 把全文渲染写入 生成/程序树总览.txt、落地顺序.txt、程序书.txt、标杆看板.txt（纯文本、无 Markdown），并在 stdout 打印已生成路径与校验状态；--json 同时返回 path 与 content。

## 性能实测（2026-11-07，Debug vs Release）

**结论先说**：CLI 每次调用约 **110–125 ms 是 .NET 运行时地板**（进程启动 + 运行时初始化），不是内核慢。MCP 是长驻进程，这 110–125 ms **只付一次**，之后每次工具调用只付内核时间（第二张表的进程内数字，多数命令 35–60 ms）。validate 是唯一真正吃时间的命令。

### 一、整条命令的墙钟时间（含进程启动，7 轮 Debug/Release 交替，min / median，单位 ms）

| 命令 | Debug min | Debug median | Release min | Release median |
| --- | ---: | ---: | ---: | ---: |
| `version`（冷启动地板） | 111.8 | 123.5 | 113.8 | 121.5 |
| `check` | 216.3 | 226.8 | 208.5 | 227.0 |
| `read-node core.app` | 282.0 | 301.0 | 297.1 | 313.1 |
| `map --mode names` | 323.3 | 332.6 | 308.4 | 340.7 |
| `validate` | 463.8 | 496.2 | 444.1 | 476.4 |
| `status` | 524.4 | 541.9 | 513.7 | 534.7 |

采集前提：采样时本机同时有 Unity / Java / DSH 等负载（CPU 使用率约 80%），绝对值偏保守，min 列更接近无干扰成本。Debug 与 Release 的差距落在噪声范围内——成本主要来自进程启动与清单/树实例 IO，不是 C# 编译优化能改变的。

### 二、内核时间（进程内，`bench --repeat N`；每遍都含加载清单，不含进程启动）

| 命令 | Debug min / median | Release min / median |
| --- | ---: | ---: |
| `validate` | 160.8 / 192.8 | 197.1 / 224.0 |
| `check` | 40.5 / 43.0 | 37.2 / 44.2 |
| `read-node` | 41.6 / 49.8 | 33.7 / 37.5 |
| `map --mode names` | 41.2 / 52.1 | 36.1 / 41.4 |
| `status` | 149.5 / 162.4 | 134.7 / 147.6 |
| `version`（仅组装 JSON） | ≈0 | ≈0 |

冷启动自测（`bench` 首屏第一行）：进程启动 → 首次 validate 完成，Debug 440.1 ms / Release 528.5 ms（其中进程启动到 bench 开始 Debug 79.8 ms、Release 78.9 ms）。首次值明显高于后续，是 JIT + 文件缓存冷启的一次性成本；长驻的 MCP 只遇到一次。Debug/Release 在进程内的排序会随负载翻转，两列差异属噪声，不代表哪个构建更快。

### 三、怎么复现

    # 进程内：min / median / p95 / 首次 + 冷启动自测（不开子进程、不起后台进程）
    dotnet build 00-程序树/工具/csharp/PTree.sln -c Release -v q
    dotnet 00-程序树/工具/csharp/src/PTree.Cli/bin/Release/net10.0/ptree-cs.dll bench --repeat 9 --json --root "examples/demo"

    # 整条命令：外面量 5–9 次取 min/median
    Measure-Command { & .\ptree-cs.exe validate --root "examples/demo" }

### 四、性能护栏

`tests/PTree.Tests/PerformanceTests.cs` 给 validate / read-node / map 各设一个宽松上限（约实测中位数的 3 倍：900 / 420 / 480 ms），超了报实测值；这组测试关掉并行（`CollectionDefinition(DisableParallelization = true)`），避免别的测试抢 CPU 时假红。护栏只防「退化一个数量级」，不做微基准。
