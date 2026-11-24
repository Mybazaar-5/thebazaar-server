using Xunit;

namespace BazaarCore.Tests.TestInfrastructure;

/// <summary>
/// 进程级全局状态串行集合（WebApplication / SessionRegistry / ModRegistry / ServerModHost / ModRuntimeHooks / SetEnvironmentVariable / ClearAllPools）：
/// xUnit 天然保证同一 Collection 内的全部测试类彼此严格串行执行；
/// 省略 DisableParallelization=true 以允许本集合与零静态交叉的 ShopEngineStaticState 及纯只读集合并发重叠。
/// </summary>
[CollectionDefinition("GlobalStaticState")]
public sealed class GlobalStaticStateCollection
{
}

/// <summary>
/// 商店/局内引擎静态开关串行集合（仅收敛 ShopEngine.EnforceArtificialSingleSpawnPedestals 写/读测试类）：
/// 组内全部 24 个纯 ShopEngine/RunEngine 测试类彼此严格串行，且经全仓扫描与 GlobalStaticState 零静态交叉（0 处引用 PedestalB1B2/PedestalTheArtist/EnforceArtificialSingleSpawnPedestals）。
/// </summary>
[CollectionDefinition("ShopEngineStaticState")]
public sealed class ShopEngineStaticStateCollection
{
}
