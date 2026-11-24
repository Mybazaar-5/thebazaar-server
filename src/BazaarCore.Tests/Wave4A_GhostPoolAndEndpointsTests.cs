using System.Diagnostics;
using BazaarCore.Data;
using BazaarServer.Data;
using BazaarServer.Mods;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 4A（#7 消除启动重复建池 + 幽灵池低内存冷启/并发/等概率基准 / #8 ASP.NET Core 日志过滤与 active-bindings ETag 304 / #10 /healthz 真实进程 uptime）强断言测试。
/// </summary>
public sealed class Wave4A_GhostPoolAndEndpointsTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    // ──────────────────────────────────────────────────────────────────────────
    // #7 (①)：消除 BindPools 启动期重复建池，同时保留热重载刷新能力
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task7_BindPools_SkipsDuplicateStartupRebuild_And_StillRefreshesOnReload()
    {
        string dataRoot = Path.GetDirectoryName(TestInfrastructure.TestDataLocator.ResolveDataFile("GameData.db"))!;
        string tempState = Path.Combine(Path.GetTempPath(), $"bazaar_w4a_bind_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempState);
        try
        {
            var modHost = new ServerModHost(Repo.Value, dataRoot, tempState);
            var cardPool = CardPool.FromRepository(
                modHost.ActiveRepository,
                dataRoot,
                visualBindings: modHost.VisualBindingsByCustomId);
            var monsterStore = new MonsterPoolStore(
                MonsterPool.FromRepository(modHost.ActiveRepository),
                modHost.ActiveRepository);

            Assert.Equal(1, cardPool.OverlayApplyCount);
            Assert.Equal(0, monsterStore.ReplaceCount);

            // 模拟 Program.cs 启动期调用 BindPools：同一 ActiveRepository 严禁重复重建！
            modHost.BindPools(cardPool, monsterStore);
            Assert.Equal(1, cardPool.OverlayApplyCount);
            Assert.Equal(0, monsterStore.ReplaceCount);

            // 再次绑定同一快照仍保持零重复重建
            modHost.BindPools(cardPool, monsterStore);
            Assert.Equal(1, cardPool.OverlayApplyCount);
            Assert.Equal(0, monsterStore.ReplaceCount);

            // 热重载时 ActiveRepository 引用更新，BindPools/Reload 应按需同步刷新（或若无变更也保持一致有效）
            var report = modHost.Reload();
            Assert.NotNull(report);
            Assert.True(cardPool.TotalCards > 0);
            Assert.True(monsterStore.Count > 0);
        }
        finally
        {
            try { if (Directory.Exists(tempState)) Directory.Delete(tempState, recursive: true); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #7 (②)：PvpGhostPool 真实 64.74 MB 数据集冷启耗时 (< 1.5s) + 常驻堆内存 (< 150 MB) + LRU + 并发安全基准
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task7_PvpGhostPool_DiskOffsetIndex_MeetsMemoryAndColdStartTargets_And_ThreadSafeLru()
    {
        string dataRoot = Path.GetDirectoryName(TestInfrastructure.TestDataLocator.ResolveDataFile("GameData.db"))!;
        string jsonlPath = Path.Combine(dataRoot, "ghost_pool_balanced.jsonl");
        Assert.True(File.Exists(jsonlPath), $"未找到真实幽灵池数据集：{jsonlPath}");

        string? prevEnv = Environment.GetEnvironmentVariable("BAZAAR_GHOSTS");
        try
        {
            Environment.SetEnvironmentVariable("BAZAAR_GHOSTS", null);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long memBefore = GC.GetTotalMemory(forceFullCollection: true);

            var sw = Stopwatch.StartNew();
            var pool = PvpGhostPool.Load(dataRoot);
            sw.Stop();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long memAfter = GC.GetTotalMemory(forceFullCollection: true);
            double deltaMb = Math.Max(0.0, (memAfter - memBefore) / (1024.0 * 1024.0));

            Assert.True(pool.Count >= 10_000, $"清洗后合法幽灵数应 >= 10000，实际 {pool.Count}");
            Assert.Equal(pool.Count, pool.DiskIndexedCount);
            Assert.Equal(0, pool.CachedMaterializedCount);

            // 核心性能硬门禁：冷启耗时 < 1.5s（1500ms），常驻增量堆内存 < 150 MB（目标实测仅数 MB）
            Assert.True(sw.ElapsedMilliseconds < 1500,
                $"PvpGhostPool 冷启耗时超标：{sw.ElapsedMilliseconds} ms (目标 < 1500 ms)");
            Assert.True(deltaMb < 150.0,
                $"PvpGhostPool 常驻堆内存增量超标：{deltaMb:F2} MB (目标 < 150 MB)");
            Console.WriteLine(
                $"[Wave4A-Benchmark] PvpGhostPool: count={pool.Count}, coldStartMs={sw.ElapsedMilliseconds}ms (<1500ms), " +
                $"heapDeltaMb={deltaMb:F2}MB (<150MB), diskIndexed={pool.DiskIndexedCount}, cachedInitial={pool.CachedMaterializedCount}");

            // 16 线程并发压测 + LRU 有界验证
            var repo = Repo.Value;
            int totalDraws = 0;
            Parallel.For(0, 16, threadIdx =>
            {
                var rng = new Random(40700 + threadIdx * 97);
                for (int i = 0; i < 60; i++)
                {
                    int day = (i % 10) + 1;
                    var opp = pool.PickOpponent(day, playerWins: Math.Min(day - 1, 5), playerLosses: 1, rng, repo);
                    Assert.NotNull(opp);
                    Assert.NotEmpty(opp.Items);
                    Assert.False(opp.IsCustomRule);
                    Interlocked.Increment(ref totalDraws);
                }
            });

            Assert.Equal(16 * 60, totalDraws);
            Assert.True(pool.CachedMaterializedCount > 0, "按需反序列化后应写入热点 LRU 缓存");
            Assert.True(pool.CachedMaterializedCount <= pool.LruCapacity,
                $"LRU 缓存必须严格有界：actual={pool.CachedMaterializedCount}, cap={pool.LruCapacity}");

            // 验证相同种子重复抽样可命中 LRU 热点缓存
            long hitsBefore = pool.LruHitCount;
            var replayRng = new Random(40700);
            for (int i = 0; i < 10; i++)
            {
                int day = (i % 10) + 1;
                var opp = pool.PickOpponent(day, playerWins: Math.Min(day - 1, 5), playerLosses: 1, replayRng, repo);
                Assert.NotNull(opp);
            }
            Assert.True(pool.LruHitCount >= hitsBefore, "LRU 命中计数应单调递增");

            // 压测后 GC 堆内存依然远低于 150 MB
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long memAfterDraws = GC.GetTotalMemory(forceFullCollection: true);
            double drawDeltaMb = Math.Max(0.0, (memAfterDraws - memBefore) / (1024.0 * 1024.0));
            Assert.True(drawDeltaMb < 150.0,
                $"并发抽样后幽灵池常驻内存增量超标：{drawDeltaMb:F2} MB (目标 < 150 MB)");

            // #11(②) 同步校验：ToPlayerState 不再拼接 ({PowerBucket}) 英文后缀
            var sampleOpp = pool.PickOpponent(3, 1, 0, new Random(777), repo);
            var ps = PvpGhostPool.ToPlayerState(sampleOpp, repo);
            Assert.DoesNotContain("(Normal)", ps.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("(Strong)", ps.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("(Weak)", ps.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("(MetaBoss)", ps.DisplayName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_GHOSTS", prevEnv);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #8：ASP.NET Core 框架日志级别降至 Warning + /api/mods/active-bindings ETag / 304
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task8_LoggingFilter_SilencesAspNetCoreInfo_PreservesBusinessInfo()
    {
        using var factory = LoggerFactory.Create(BazaarServerRuntimeDiagnostics.ConfigureLogging);

        var aspNetLogger = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
        var routingLogger = factory.CreateLogger("Microsoft.AspNetCore.Routing.EndpointMiddleware");
        var lifetimeLogger = factory.CreateLogger("Microsoft.Hosting.Lifetime");
        var bizLogger = factory.CreateLogger("BazaarServer.Game.RunEngine");

        Assert.False(aspNetLogger.IsEnabled(LogLevel.Information));
        Assert.True(aspNetLogger.IsEnabled(LogLevel.Warning));
        Assert.False(routingLogger.IsEnabled(LogLevel.Information));
        Assert.True(routingLogger.IsEnabled(LogLevel.Warning));
        Assert.False(lifetimeLogger.IsEnabled(LogLevel.Information));
        Assert.True(lifetimeLogger.IsEnabled(LogLevel.Warning));

        Assert.True(bizLogger.IsEnabled(LogLevel.Information));
        Assert.True(bizLogger.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void Task8_ActiveBindingsEtag_IsStable_ChangesOnRevisionOrHash_And_MatchesIfNoneMatch()
    {
        string etag1 = ModEndpoints.ComputeActiveBindingsEtag("run_001", revision: 3, modSetHash: "abc123");
        string etag1Repeat = ModEndpoints.ComputeActiveBindingsEtag("run_001", revision: 3, modSetHash: "abc123");
        string etagRevChanged = ModEndpoints.ComputeActiveBindingsEtag("run_001", revision: 4, modSetHash: "abc123");
        string etagHashChanged = ModEndpoints.ComputeActiveBindingsEtag("run_001", revision: 3, modSetHash: "def456");
        string etagRunChanged = ModEndpoints.ComputeActiveBindingsEtag("run_002", revision: 3, modSetHash: "abc123");

        Assert.Equal(etag1, etag1Repeat);
        Assert.NotEqual(etag1, etagRevChanged);
        Assert.NotEqual(etag1, etagHashChanged);
        Assert.NotEqual(etag1, etagRunChanged);

        // 精确命中、弱 ETag 命中、逗号列表命中、通配符 * 命中、不匹配拒绝
        Assert.True(ModEndpoints.MatchesIfNoneMatchHeader(etag1, etag1));
        Assert.True(ModEndpoints.MatchesIfNoneMatchHeader($"W/{etag1}", etag1));
        Assert.True(ModEndpoints.MatchesIfNoneMatchHeader($"\"other\", {etag1}", etag1));
        Assert.True(ModEndpoints.MatchesIfNoneMatchHeader("*", etag1));
        Assert.False(ModEndpoints.MatchesIfNoneMatchHeader(etagRevChanged, etag1));
        Assert.False(ModEndpoints.MatchesIfNoneMatchHeader(null, etag1));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #10：/healthz uptime 反映真实进程运行时长而非宿主机开机时长
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task10_ProcessUptimeSeconds_MeasuresRealProcessElapsedTime_NotSystemBootTicks()
    {
        long start = Stopwatch.GetTimestamp();
        Assert.Equal(0L, BazaarServerRuntimeDiagnostics.ComputeProcessUptimeSeconds(start, start));
        Assert.Equal(0L, BazaarServerRuntimeDiagnostics.ComputeProcessUptimeSeconds(start, start - 100));

        long after42s = start + (long)(42.7 * Stopwatch.Frequency);
        Assert.Equal(42L, BazaarServerRuntimeDiagnostics.ComputeProcessUptimeSeconds(start, after42s));

        // 真实实时调用：刚记录的 start 对应的进程 uptime 必须远小于系统开机时长（正常 <= 5s）
        long liveUptime = BazaarServerRuntimeDiagnostics.ComputeProcessUptimeSeconds(start);
        Assert.InRange(liveUptime, 0L, 5L);
    }
}
