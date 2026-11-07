demo 程序树起步说明
生成日期：2026-11-07
生成命令：ptree-cs init

一、这里有什么
1 00-程序树/program-tree.yaml：根、1 个部门、1 个程序与 P0 阶段。
2 00-程序树/契约/contracts.yaml：1 条外部输入契约与 1 条程序输出契约。
3 00-程序树/标杆/benchmarks.yaml 与 绑定.yaml：空但结构合法。
4 00-程序树/树实例/：程序树、策划树、美术树、UI树的最小实例。
5 00-程序树/说明/：每个节点的起步说明，写清怎么改、代码该放哪。
6 00-程序树/变更/、生成/、运维/：空目录，留给提案、生成物与验证报告。

二、下一步
1 改 program-tree.yaml 与说明：把 demo/core/core.app 换成真实职责与边界。
2 把代码写进 10-程序/ 下与节点 id 对应的目录，补 home、files、SPEC.md 与验收命令。
3 同步契约的 producers/consumers、策划树对应程序与标杆绑定。
4 每次改完跑 ptree-cs validate 与 ptree-cs check，两条都零 E 再继续。

三、常用命令
验证：ptree-cs validate、ptree-cs check
生成：ptree-cs book、ptree-cs view、ptree-cs order、ptree-cs benchmark
提案：ptree-cs apply --file 00-程序树/变更/<提案>.yaml；确认后加 --write
整树自检：ptree-cs verify
