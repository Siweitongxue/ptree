测试快照基线说明
自 2026-11-06 起，校验与 parity 期望值由 C# 内核快照固化；Python 参考版冻结在 09-归档，不再作为判据。
快照文件：
Snapshots/validation_snapshot.json   Validator 错误/警告/提示的 code、node、message 与顺序
Snapshots/parity_snapshot.json       BuildIndex / BuildNode 的完整 JSON 负载
刷新方式（只在人工确认内核口径变化后）：
PTREE_UPDATE_SNAPSHOT=1 dotnet test tests/PTree.Tests -c Release
随后必须人工复核 git diff 中的快照变更，再按正常模式跑一遍，确认 0 失败。
测试数量：256；快照断言不降级为数量或关键词包含，仍比对完整条目列表与顺序。
