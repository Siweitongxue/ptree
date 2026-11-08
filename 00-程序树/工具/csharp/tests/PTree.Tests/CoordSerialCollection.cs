using Xunit;

namespace PTree.Tests;

/// <summary>协作相关测试串行跑：环境变量与临时目录互相影响，不能并行。</summary>
[CollectionDefinition("coord-serial", DisableParallelization = true)]
public sealed class CoordSerialCollection
{
}
