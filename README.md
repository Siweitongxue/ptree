# 程序树（Program Tree）

给 AI 时代的代码库做「可读、可校验、可回滚」的架构登记：每个程序是一个节点，节点有名称、短介绍、说明、契约、联系与状态；工具负责校验、粗读、写回与留痕。

> Program Tree: register every program as a node with contracts and links, validate the whole tree, read it coarse-to-fine, and write back through a guarded tool chain.

## 它解决什么

- 程序乱加、互相引用却不知道：联系必须登记，漏标会被扫出来
- AI 每次都要把整个代码库读一遍：先粗后细五层阅读阶梯，token 花在刀刃上
- 改了结构没人管：校验规则 R01-R46 加跨树检查 T01-T12，写入失败自动回滚

## 上手

需要 .NET 10 SDK（运行只要 Runtime）。

    # 自检（附带示例树）
    dotnet run --project 00-程序树/工具/csharp/src/PTree.Cli -- validate --root examples/demo
    dotnet run --project 00-程序树/工具/csharp/src/PTree.Cli -- check --root examples/demo

    # 阅读阶梯
    dotnet run --project 00-程序树/工具/csharp/src/PTree.Cli -- guide --root examples/demo
    # 最粗一层：功能块与部门分区
    dotnet run --project 00-程序树/工具/csharp/src/PTree.Cli -- map --root examples/demo
    # 单点卡片
    dotnet run --project 00-程序树/工具/csharp/src/PTree.Cli -- read-node core.app --root examples/demo

初始化一棵自己的树：

    dotnet run --project 00-程序树/工具/csharp/src/PTree.Cli -- init 我的项目 --root .

## 三种形态，同一个内核

| 形态 | 怎么用 |
| --- | --- |
| 命令行 | ptree-cs <命令> --root <树根>，命令见 guide 与 docs |
| MCP 服务 | ptree-cs mcp --root <树根>，stdio 起 26 个 tree_* 工具，给 Claude / DSH 这类客户端用 |
| 画布 | 双击 00-程序树/工具/viewer/index.html（示例快照）；实时改就 ptree-serve --root <树根> --port 7788 |

## 阅读阶梯（先粗后细）

1. tree_guard / health 常检
2. map（默认 blocks）只列功能块与部门分区
3. map --mode names 全树名称与依赖
4. map --mode summary 名称 + 一行短介绍
5. group <组id> --mode summary 组内成员 + 简介 + 用法
6. read-node <id> 单点卡片（9 行内）

## 目录

    00-程序树/
      工具/csharp/     内核（Core / Cli / Mcp / Serve）与测试
      工具/viewer/     只读画布（零依赖，file:// 可用）
      工具/适配/       网格数据导出（把 yaml 变成画布 data.js）
      规范/            校验规则 R01-R46、程序树规范、阅读阶梯、备份、程序介绍格式
    examples/demo/     示例树（3 节点，validate E=0）

## 规则与校验

- 清单规则 R01-R46：节点、契约、状态、联系、说明、评价、阅读项、备份配置、证据
- 跨树规则 T01-T12：策划 / 美术 / UI / 全文件 与程序树的对应
- validate 只读；apply --write 才落盘，失败整树回滚并留台账

## 生成画布数据

    python 00-程序树/工具/适配/grid_export.py --root examples/demo --out 00-程序树/工具/viewer/data.js

## 许可

MIT，见 LICENSE。
