项目管维插件 · 交互式网格视图
版本 2 ｜ 2026-11-07 ｜ 工具目录：00-程序树/工具/viewer/
本页是给人看和操作的七张网格视图（程序 / 策划 / 美术 / UI / 音效 / 特效 / 全文件）；数据来自 data.js 快照或本地 ptree-serve 服务。零外部依赖：没有 CDN、没有 npm 包、没有构建步骤。

一 文件
index.html    页面骨架，直接双击或由服务提供，4697 字节
style.css     深色主题与布局，12090 字节
viewer.js     布局、画布、边与功能块、详情、搜索筛选、写操作，89438 字节
bridge.html   file:// 连本地服务时的同源桥接页，1316 字节
data.js       数据快照，397097 字节，96 个程序节点、598 条关系（含 101 条 block）、7 张网格、11 个功能块
README.txt    本文件

二 三种打开方式
1 只读模式：直接双击 index.html，地址是 file:// 开头。
  顶部显示“只读模式 · file://”，数据只来自 data.js，不能写。
2 实时模式（同源）：先启动 ptree-serve，再用浏览器打开服务地址。
  顶部显示“实时模式 · 可写”，详情、状态、写操作都走服务 API。
3 file:// 连本地服务：在 index.html 地址后加 ?api=服务地址，例如
  index.html?api=http://127.0.0.1:7788
  顶部同样显示“实时模式 · 可写”。此时页面通过 bridge.html 里同源的脚本转发请求，避免浏览器对 file:// 的跨域限制。
数据来源优先级：?api 参数 > 当前页是 http/https 时的同源地址 > data.js 只读快照。
?api 只接受 http:// 或 https:// 开头的地址；非法值自动回退只读。

三 启动本地服务
在项目根（含 00-程序树的目录）执行：
dotnet 00-程序树/工具/csharp/src/PTree.Serve/bin/Release/net10.0/ptree-serve.dll --root <项目根> --viewer <仓库>/00-程序树/工具/viewer --port 7788
然后打开 http://127.0.0.1:7788/ 。服务只监听 127.0.0.1，没有鉴权，不要暴露到公网。

四 界面与操作
1 顶部七个页签：程序 / 策划 / 美术 / UI / 音效 / 特效 / 全文件。所有页签默认都是节点图形；每张网格顶部有“图形 / 表格”切换，表格只是次级视图。
2 图形按每张网格 graph.nodes 的 parent 建树、按 layer 分行：程序为 4 层，策划/美术/UI 为 2 到 3 层；左侧显示每层标签与数量。
3 节点卡片样式统一：未完成灰、已完成蓝、已验证绿、退役加深并划线；synthetic 根节点用虚线边框，标题后缀“（视图根）”或“（预留）”。
4 画布：滚轮缩放、拖拽平移、双击适合窗口；“适合窗口”“重置”“− / ＋”按钮可用。
5 “显示到 N 层”控件只显示前 N 层，切换后自动重新布局。
6 搜索按 id、名称和字段值匹配，命中节点黄色描边，并自动聚焦第一个命中；回车再次聚焦。
7 筛选器：状态对所有网格生效；部门（group.dept-*）和阶段（phase）只对程序网格生效。
8 点程序节点走实时 /api/node 详情；点策划/美术/UI/预留节点时，右侧按 graph.nodes[].fields 渲染字段表。
9 关系线开关只对程序网格生效；没有 links 的网格不显示这组按钮。“功能块”开关只在有 blocks 的网格出现。
10 空网格（只有 synthetic 根）在画布中央显示“预留网格：字段与规则已定，暂无条目”，不显示空白画布。
11 页脚状态条：节点数、关系数、当前可见数、画出来的功能块数；程序页额外显示策划条目数和树版本。

五 实时模式下的写操作
详情面板底部有“写操作（实时模式）”区。只读模式下这一区只显示提示，不出现表单。
1 改状态：未完成 / 已完成 / 退役三个按钮。“已验证”按钮禁用，注明“只能由用户确认”。
2 加子节点：填写名称、slug、kind、职责、说明。说明会实时统计去空白字数；少于 200 字内核会以 R31 拒绝，页面会把 errors 原样显示。
   新增成功后自动选中新节点，并把新节点补进当前画布与状态栏。
3 加联系：填写 from、to、type；type 用服务端接受的 引用 / 测试 / 探针 / 覆盖 / 数据 / 控制。
4 每个写操作完成后显示：成功或失败、tree_version 前后变化、written_files 列表、ledger 编号；失败时显示 errors 列表，不吞错。
5 更新接口兼容两套路径：先试 POST /api/node/<id>（插件文档约定），遇到 404/405 或“路由不存在”再回退 POST /api/node/update（当前 ptree-serve 实现）。退役用状态更新完成。
6 读接口：GET /api/health、GET /api/status、GET /api/node/<id>?depth=1..3；详情里的“服务实时”区块来自这些接口。
7 写接口：POST /api/node、POST /api/link；更新走上面第 5 条的兼容顺序。

六 数据字段（data.js 的 window.GRID_DATA）
meta        generated_at、tree_version、updated、source、generator
grids       [{kind, name, instance, fields[], rows[{字段, short?}], graph:{root, nodes:[{id,name,parent,layer,status,kind,short?,synthetic?,fields{}}], links[], blocks[]}}]
            当前 7 张：program / plan / art / ui / extra:音效树 / extra:特效树 / extra:全文件树
            blocks 为 [{id, name, description, members[], parent}]，members 只保留本网格里存在的节点；
            当前 11 个块只出现在程序网格（策划/美术/UI/全文件网格里没有组员）。
nodes       [{id, name, kind, layer, parent, phase, status, responsibility, not_responsible[], inputs[], outputs[], depends_on[], files[], acceptance[], spec, owner, platform, evidence[], created_at, updated_at, attachments[], preset, notes, short}]
            short = 00-程序树/短介绍/<id>.txt 压成一行后截断到 80 字
groups      [{id, name, description, members[], parent?}]，当前 10 组：group.all 与 9 个 group.dept-*（组说明另在 00-程序树/说明/<组id>.txt）
phases      [{id, name}]，P0 到 P7
links       [{from, to, type, note}]，type 为 contains / depends_on / flow / plans / block；当前 598 条
            = 旧 497 条 + 101 条 block（组 → 成员节点，note 是组名）

七 联系与功能块怎么画、开关怎么用
1 四类边，画布线型与图例一一对应
  包含 contains——层级父子，细淡线、无箭头，默认开。
  依赖 depends_on——实线箭头，默认开。
  契约流 flow——虚线箭头，箭头指向消费方；悬停或点选时显示契约 id，默认开。
  对应策划 plans——点线箭头（策划条目 → 程序），默认关，否则一屏全是线。策划条目在程序网格底部作为辅助节点出现，不参与“显示到 N 层”的层级过滤。
2 顶部“关系线”四个复选框分别控制四类边，状态写进 localStorage（键 ptree.viewer.prefs.v1），刷新后保持。
3 画布只画两端都在当前网格、且没有被筛选或折叠隐藏的边。跨网格或没画出来的联系（美术/UI 等其他网格的条目、被“显示到 N 层”或状态筛选挡掉的端点、开关关掉的 plans）在节点详情“跨网格 / 未画出的联系（只列文字，不画线）”里逐条列出原因，不画线。
4 悬停一条边：浮动提示给类型、两端、说明（契约 id / 策划条目名）；点一条边：右侧出“联系卡”，列类型、两端与说明。
5 功能块的数据：导出器把 program-tree.yaml 的 groups 段写进每张网格 graph.blocks（id/name/description/members/parent），members 只留这张网格里存在的节点；同时把 组 → 节点 写进 links（type=block），画布据此算包围盒。
6 组框的画法：半透明底 + 虚线框，左上角标题条写“组名 · N 个成员”。画序是 组框 → 边 → 节点，所以节点永远压在组框上层；大框先画、小框后画，嵌套时小框在上。
7 开关：顶部“功能块”组里，“组框”控制手建的组（默认开），“部门组”控制 group.all 与 group.dept-*（默认关，否则每张网格被一个大框套住）；状态同样进 localStorage。
8 组框操作：点标题条左端的 ▾ / ▸ 折叠——成员节点连同它们的边一起收起，只留一条标题条；折叠状态只存在本次会话，刷新后恢复展开。点标题条其它位置弹出组卡：名字、一行简介、成员清单、详细说明路径 00-程序树/说明/<组id>.txt。
9 短介绍有三个位置：悬停节点或策划条目时浮动提示（CSS 限 2 行）；选中节点时右侧详情第一段“短介绍”；网格条目（策划/美术/UI/全文件）在表格视图里多一列“短介绍”。数据来源 00-程序树/短介绍/<编号>.txt。
10 实时模式的短介绍：优先用 /api/node 返回的 short，payload 没带就回退 data.js 的 short，file:// 与 ?api 两种模式都看得到。

八 重新生成数据
数据由 Python 导出器生成，不要手改 data.js：
python 00-程序树/工具/适配/grid_export.py
生成后刷新页面。实时模式新增的节点与联系只保证详情/状态栏最新；要同步整张画布，重新跑导出并刷新。

九 自检（2026-11-07 改版后实测）
node --check 00-程序树/工具/viewer/viewer.js     通过
python 00-程序树/工具/适配/grid_export.py        退出码 0，写出 data.js 397097 字节
python/node 解析 data.js：JSON 可解析，nodes=96、links=598（497 旧 + 101 block）、grids=7
  程序网格 blocks=11（1 个手建 group.tree-toolchain + 10 个自动组：group.all 1 个成员、9 个 group.dept-* 共 95 个成员关系），其余 6 张网格 blocks=0
  短介绍覆盖：nodes 96/96 有 short；策划 59/59、美术 11/11、UI 7/7、全文件 29/29 行有 short
index.html 内没有 http:// 或 https:// 外链；资源只有 style.css、data.js、viewer.js
七张网格节点数（含视图根）：程序 96、策划 60、美术 11、UI 7、全文件 30；音效/特效各 1（仅预留根，无条目）
Tabbit（Chromium）实测：file:// 打开无 JS 错误，画布 1942×1304 正常出图，状态栏显示“节点 96 · 关系 598 · 可见 96 · 功能块 1/11 · 策划条目 59”
  悬停节点出短介绍（例：核心部门 / core.app）、“包含（层级）demo → core”等边提示正常
  点组框标题条出组卡：名字、一行简介、5 个成员、路径 00-程序树/说明/group.tree-toolchain.txt 都在
  点 ▾ 折叠后组卡显示“已折叠”；勾“部门组”统计变 11/11；勾“对应策划”状态栏出现“画布策划 59”
  localStorage 写入 {"edges":{...},"groups":true,"autoGroups":true}，刷新后复选框状态保持
截图：viewer-check.png（展开态）、viewer-collapsed.png（折叠态），在仓库根目录。

十 实测记录（2026-11-06）
1 file:// 只读：顶部“只读模式 · file://”；点“核心部门”出本地详情；写操作区显示“只读模式”，无表单。
2 http 实时：顶部“实时模式 · 可写”，项目根为测试临时根；状态栏显示服务节点数与“树版本 1.2.6”；详情出现“节点说明（intro 全文）”和写操作表单。
3 成功写入（临时根）：新增子节点成功，返回 tree_version 1.2.3 → 1.2.4、written_files 8 个、ledger chg-20261106-0005；状态栏节点数 73 → 74。
4 成功写入：改状态与加联系均返回成功；加联系返回 1.2.5 → 1.2.6、ledger chg-20261106-0007。
5 故意失败：说明只写“说明太短，会被 R31 拒绝。”，界面显示“失败：新增节点被拒绝”，errors 为“[R31] 说明去空白后只有 13 字，少于 200 字（写清定位、代码、连接、怎么改）”，表单内容保留。
6 file:// + ?api：通过 bridge.html 连上本地服务，顶部变为“实时模式 · 可写”，详情与写操作表单正常出现。
7 写入测试全部在临时项目根上完成，没有改动项目根的真实清单；本次新增文件只在 viewer 目录。

十一 已知不足
1 data.js 是快照：实时写操作后画布只补入新增节点，不重拉全树；整图同步需重跑 grid_export 并刷新。
2 服务端新增联系的类型是 引用/测试/探针/覆盖/数据/控制；画布的四类线来自导出数据，新联系暂不在画布上画线，详情“链接（出/入）”可见。
3 新增 program 节点时内核要求至少一个 output 契约，当前表单只收集名称/slug/kind/职责/说明；program 被拒时先加 step，或由 API 补 outputs。
4 “已验证”只能由用户确认，视图禁用按钮并明确写出。
5 服务无鉴权，只适合本机使用。
6 bridge.html 只转发 /api/*，不代理静态文件。
7 计划条目（plans）在画布中作为辅助节点出现，不参与“显示到 N 层”的层级过滤。
8 功能块的折叠状态不写 localStorage，刷新后恢复展开；折叠也不改布局，成员原来的位置会留白。
9 跨网格联系（美术/UI/全文件等其它网格的条目）只在节点详情里列文字，画布不画线。
10 组框按“成员包围盒”画，嵌套组（部门组里套手建组）会互相压边，不做自动避让；部门组默认关就是为了少压。
11 节点详情的“未画出的联系”会把开关关闭的 plans 也列出来（原因写“开关关闭”），条目多时这一段会比较长。
