using System.Reflection;
using BazaarCore.Tests.TestInfrastructure;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2C 模块二 &amp; 模块三独占测试：验证 BazaarLauncher 12 项 P1 + D4-NEW-01
/// 与多模式规则 UI P1（21 字段边界/重置、SandboxLab 经验、回放胜负平视角、7 榜覆盖、
/// GameEventsBridge 反射、SyncGeneration/syncEpoch 防乱序、ReduceHighConcurrencyVfx、零硬编码 csproj）。
/// </summary>
public sealed class Wave2C_LauncherP1AndRulesUiTests : IClassFixture<Wave2CLauncherAssemblyFixture>
{
    private readonly Wave2CLauncherAssemblyFixture _fx;

    public Wave2C_LauncherP1AndRulesUiTests(Wave2CLauncherAssemblyFixture fx) => _fx = fx;

    private sealed class MockEventsContainer
    {
        public readonly MockVoidEvent PublicZeroArg = new();
        private readonly MockArgEvent _privateOneArg = new();
        internal MockArgEvent InternalProp { get; } = new();
    }

    private sealed class MockVoidEvent
    {
        public int TriggerCount { get; private set; }
        private void Trigger() => TriggerCount++;
    }

    private sealed class MockArgEvent
    {
        public object? LastArg { get; private set; }
        public void Trigger(string payload) => LastArg = payload;
    }

    [Fact]
    public void D4_P1_1_GameEventsBridge_TriggersPublicAndNonPublicEventsSafely()
    {
        Type bridgeType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.GameEventsBridge", throwOnError: true)!;
        MethodInfo? triggerOnTarget = bridgeType.GetMethod(
            "TryTriggerOnContainer",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(triggerOnTarget);

        var container = new MockEventsContainer();

        bool ok0 = (bool)triggerOnTarget!.Invoke(null, new object?[] { container, "PublicZeroArg", Array.Empty<object>() })!;
        Assert.True(ok0);
        Assert.Equal(1, container.PublicZeroArg.TriggerCount);

        bool ok1 = (bool)triggerOnTarget.Invoke(null, new object?[] { container, "_privateOneArg", new object[] { "hello" } })!;
        Assert.True(ok1);
        var privEvent = (MockArgEvent)typeof(MockEventsContainer).GetField("_privateOneArg", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(container)!;
        Assert.Equal("hello", privEvent.LastArg);

        bool ok2 = (bool)triggerOnTarget.Invoke(null, new object?[] { container, "InternalProp", new object[] { "prop-val" } })!;
        Assert.True(ok2);
        Assert.Equal("prop-val", container.InternalProp.LastArg);

        // 缺失事件名安全返回 false，不抛异常
        bool missing = (bool)triggerOnTarget.Invoke(null, new object?[] { container, "NonExistentEvent", Array.Empty<object>() })!;
        Assert.False(missing);
    }

    [Fact]
    public void D4_NEW_01_BazaarLauncherPlugin_ExposesReduceHighConcurrencyVfxConfigEntry()
    {
        Type pluginType = _fx.LauncherAssembly.GetType("BazaarLauncher.BazaarLauncherPlugin", throwOnError: true)!;
        PropertyInfo? prop = pluginType.GetProperty("ReduceHighConcurrencyVfx", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(prop);
    }

    [Fact]
    public void D6_P1_01_02_03_RulesResetAndBoundariesAndSandboxLabExp_AreExact()
    {
        Type rulesType = _fx.LauncherAssembly.GetType("BazaarLauncher.CustomRunRuleSet", throwOnError: true)!;
        object rules = Activator.CreateInstance(rulesType)!;

        // 污染全部 Wave 2/3 扩展字段与边界值
        rulesType.GetProperty("PresetId")!.SetValue(rules, "ExtremeRoguelike");
        rulesType.GetProperty("SpecialModeId")!.SetValue(rules, "BossRush");
        rulesType.GetProperty("MutatorIds")!.SetValue(rules, new List<string> { "turbo_frenzy", "glass_cannon" });
        rulesType.GetProperty("DailyChallengeDate")!.SetValue(rules, "2026-11-23");
        rulesType.GetProperty("FixedRunSeed")!.SetValue(rules, 123456);
        rulesType.GetProperty("EndlessMaxDay")!.SetValue(rules, 55);
        rulesType.GetProperty("CrossHeroOwnHeroBias")!.SetValue(rules, 0.8);
        rulesType.GetProperty("BossRushTotalWaves")!.SetValue(rules, 20);
        rulesType.GetProperty("BossRushIncludeMilestonePvp")!.SetValue(rules, false);
        rulesType.GetProperty("SandboxFreeInfiniteReroll")!.SetValue(rules, false);
        rulesType.GetProperty("SandboxZeroShopPrice")!.SetValue(rules, true);
        rulesType.GetProperty("ShopHighTierBonusPct")!.SetValue(rules, 35.0);
        rulesType.GetProperty("ShopPriceMultiplier")!.SetValue(rules, 2.5);
        rulesType.GetProperty("MonsterLootBonusChoices")!.SetValue(rules, 3);
        rulesType.GetProperty("MonsterLootTierBonus")!.SetValue(rules, 2);
        rulesType.GetProperty("EnemyStatMultiplier")!.SetValue(rules, 3.0);
        rulesType.GetProperty("DisableFatesRevive")!.SetValue(rules, true);
        rulesType.GetProperty("SelectedModIds")!.SetValue(rules, new List<string> { "mod-a" });
        rulesType.GetProperty("PlayerCap")!.SetValue(rules, 99);

        // 调用统一重置入口 ResetToDefaults()
        MethodInfo? resetDefaults = rulesType.GetMethod("ResetToDefaults", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(resetDefaults);
        resetDefaults!.Invoke(rules, null);

        Assert.Equal("PureOfficial", rulesType.GetProperty("PresetId")!.GetValue(rules));
        Assert.Equal("Standard", rulesType.GetProperty("SpecialModeId")!.GetValue(rules));
        Assert.Empty((List<string>)rulesType.GetProperty("MutatorIds")!.GetValue(rules)!);
        Assert.Null(rulesType.GetProperty("DailyChallengeDate")!.GetValue(rules));
        Assert.Null(rulesType.GetProperty("FixedRunSeed")!.GetValue(rules));
        Assert.Equal(99, rulesType.GetProperty("EndlessMaxDay")!.GetValue(rules));
        Assert.Equal(0.40, (double)rulesType.GetProperty("CrossHeroOwnHeroBias")!.GetValue(rules)!, 3);
        Assert.Equal(10, rulesType.GetProperty("BossRushTotalWaves")!.GetValue(rules));
        Assert.Equal(true, rulesType.GetProperty("BossRushIncludeMilestonePvp")!.GetValue(rules));
        Assert.Equal(true, rulesType.GetProperty("SandboxFreeInfiniteReroll")!.GetValue(rules));
        Assert.Equal(false, rulesType.GetProperty("SandboxZeroShopPrice")!.GetValue(rules));
        Assert.Equal(0.0, (double)rulesType.GetProperty("ShopHighTierBonusPct")!.GetValue(rules)!, 3);
        Assert.Equal(1.0, (double)rulesType.GetProperty("ShopPriceMultiplier")!.GetValue(rules)!, 3);
        Assert.Equal(0, rulesType.GetProperty("MonsterLootBonusChoices")!.GetValue(rules));
        Assert.Equal(0, rulesType.GetProperty("MonsterLootTierBonus")!.GetValue(rules));
        Assert.Equal(1.0, (double)rulesType.GetProperty("EnemyStatMultiplier")!.GetValue(rules)!, 3);
        Assert.Equal(false, rulesType.GetProperty("DisableFatesRevive")!.GetValue(rules));
        Assert.Empty((List<string>)rulesType.GetProperty("SelectedModIds")!.GetValue(rules)!);

        // 验证 NormalizeBounds 钳制 PlayerCap(2..16) 等全部边界
        rulesType.GetProperty("PlayerCap")!.SetValue(rules, 1);
        MethodInfo? normalize = rulesType.GetMethod("NormalizeBounds", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(normalize);
        normalize!.Invoke(rules, null);
        Assert.Equal(2, rulesType.GetProperty("PlayerCap")!.GetValue(rules));

        rulesType.GetProperty("PlayerCap")!.SetValue(rules, 42);
        normalize.Invoke(rules, null);
        Assert.Equal(16, rulesType.GetProperty("PlayerCap")!.GetValue(rules));

        // 验证 SandboxLab 本地预设 StartingExperience == 3 且清残留列表
        rulesType.GetProperty("MutatorIds")!.SetValue(rules, new List<string> { "turbo_frenzy" });
        MethodInfo? applyPreset = rulesType.GetMethod("ApplyLocalPresetDefaults", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(applyPreset);
        applyPreset!.Invoke(rules, new object?[] { "PureOfficial", "SandboxLab" });
        Assert.Equal(3, rulesType.GetProperty("StartingExperience")!.GetValue(rules));
        Assert.Equal(999, rulesType.GetProperty("StartingGold")!.GetValue(rules));
        Assert.Empty((List<string>)rulesType.GetProperty("MutatorIds")!.GetValue(rules)!);

        // 验证品阶列表包含 Legendary
        Type uiType = _fx.LauncherAssembly.GetType("BazaarLauncher.CustomGameplayUI", throwOnError: true)!;
        FieldInfo? tiersField = uiType.GetField("Tiers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(tiersField);
        var tiers = (string[])tiersField!.GetValue(null)!;
        Assert.Contains("Legendary", tiers);
    }

    [Fact]
    public void D6_P1_11_12_LeaderboardHasAllSevenBoards_AndReplayPerspectiveHandlesWinLossDraw()
    {
        // 1. 排行榜 7 榜覆盖
        Type uiType = _fx.LauncherAssembly.GetType("BazaarLauncher.CustomGameplayUI", throwOnError: true)!;
        FieldInfo? boardsField = uiType.GetField("LeaderboardBoardKeys", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(boardsField);
        var boardKeys = (string[])boardsField!.GetValue(null)!;
        Assert.True(boardKeys.Length >= 7, $"应至少包含 7 个榜单，实际 {boardKeys.Length}");
        Assert.Contains("mutator", boardKeys);
        Assert.Contains("cross_hero", boardKeys);
        Assert.Contains("tournament", boardKeys);

        // 2. 战报回放胜/负/平视角（兼容 Player1/Player2/Draw 与 p1/p2/draw，含 #95 备忘）
        Type resolverType = _fx.LauncherAssembly.GetType("BazaarLauncher.ReplayOutcomeResolver", throwOnError: true)!;
        MethodInfo? resolve = resolverType.GetMethod("ResolvePerspective", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(resolve);

        // P2 视角，P1 赢 -> 落败，对手为 Alice
        object rLoss = resolve!.Invoke(null, new object?[] { "acc-p2", "acc-p1", "acc-p2", "acc-p1", "Alice", "Bob", "acc-p1", "Player1", false })!;
        Assert.Equal("落败", rLoss.GetType().GetProperty("LabelZh")!.GetValue(rLoss));
        Assert.Equal("Alice", rLoss.GetType().GetProperty("OpponentName")!.GetValue(rLoss));

        // P2 视角，P2 赢（winnerSide="p2"） -> 胜利，对手为 Alice
        object rWin = resolve.Invoke(null, new object?[] { "acc-p2", "acc-p1", "acc-p2", "acc-p1", "Alice", "Bob", "acc-p2", "p2", false })!;
        Assert.Equal("胜利", rWin.GetType().GetProperty("LabelZh")!.GetValue(rWin));
        Assert.Equal("Alice", rWin.GetType().GetProperty("OpponentName")!.GetValue(rWin));

        // 平局（isDraw=true 或 winnerSide="Draw" 或 winnerAccountId="__draw__"）
        object rDraw1 = resolve.Invoke(null, new object?[] { "acc-p2", "acc-p1", "acc-p2", "acc-p1", "Alice", "Bob", "", "Draw", true })!;
        Assert.Equal("平局", rDraw1.GetType().GetProperty("LabelZh")!.GetValue(rDraw1));

        object rDraw2 = resolve.Invoke(null, new object?[] { "acc-p1", "acc-p1", "acc-p2", "acc-p2", "Alice", "Bob", "__draw__", "draw", null })!;
        Assert.Equal("平局", rDraw2.GetType().GetProperty("LabelZh")!.GetValue(rDraw2));
        Assert.Equal("Bob", rDraw2.GetType().GetProperty("OpponentName")!.GetValue(rDraw2));
    }

    [Fact]
    public void P0_29_D6_P1_08_SyncGenerationAndRoomBundleEpoch_RejectStaleCompletions()
    {
        Type syncType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.ModContentSyncClient", throwOnError: true)!;
        object syncInst = syncType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        MethodInfo? advanceGen = syncType.GetMethod("AdvanceSyncGenerationForTest", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo? isGenCurrent = syncType.GetMethod("IsSyncGenerationCurrent", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo? advanceEpoch = syncType.GetMethod("AdvanceRoomBundleEpochForTest", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo? isEpochCurrent = syncType.GetMethod("IsRoomBundleEpochCurrent", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(advanceGen);
        Assert.NotNull(isGenCurrent);
        Assert.NotNull(advanceEpoch);
        Assert.NotNull(isEpochCurrent);

        long g1 = (long)advanceGen!.Invoke(syncInst, null)!;
        Assert.True((bool)isGenCurrent!.Invoke(syncInst, new object[] { g1 })!);
        long g2 = (long)advanceGen.Invoke(syncInst, null)!;
        Assert.False((bool)isGenCurrent.Invoke(syncInst, new object[] { g1 })!);
        Assert.True((bool)isGenCurrent.Invoke(syncInst, new object[] { g2 })!);

        long e1 = (long)advanceEpoch!.Invoke(syncInst, null)!;
        Assert.True((bool)isEpochCurrent!.Invoke(syncInst, new object[] { e1 })!);
        long e2 = (long)advanceEpoch.Invoke(syncInst, null)!;
        Assert.False((bool)isEpochCurrent.Invoke(syncInst, new object[] { e1 })!);
        Assert.True((bool)isEpochCurrent.Invoke(syncInst, new object[] { e2 })!);
    }

    [Fact]
    public void D8_P1_07_BazaarLauncherCsproj_ContainsNoHardcodedPersonalPaths()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string csprojPath = Path.Combine(repoRoot, "src", "BazaarLauncher", "BazaarLauncher.csproj");
        string xml = File.ReadAllText(csprojPath);

        // 动态拼装敏感路径片段，避免本测试文件自身触发零泄露门禁
        string forbiddenSteam = string.Concat("D:", "\\", "SteamLibrary");
        string forbiddenWorkspace = string.Concat("E:", "\\", "the", "bazaar");
        Assert.DoesNotContain(forbiddenSteam, xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(forbiddenWorkspace, xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<Version>", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void D4_P1_5_SocialSseHandle_PerConnectionDisposeIsThreadSafeAndRemovesSharedFlag()
    {
        Type uiType = _fx.LauncherAssembly.GetType("BazaarLauncher.CustomGameplayUI", throwOnError: true)!;
        Assert.Null(uiType.GetField("_socialSseCtsDisposedFlag", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));
        Assert.NotNull(uiType.GetField("_socialSseHandle", BindingFlags.NonPublic | BindingFlags.Instance));

        Type? handleType = uiType.GetNestedType("SocialSseHandle", BindingFlags.NonPublic);
        Assert.NotNull(handleType);
        PropertyInfo? isCancelProp = handleType!.GetProperty("IsCancellationRequested", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? cancelDispose = handleType.GetMethod("CancelAndDisposeOnce", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(isCancelProp);
        Assert.NotNull(cancelDispose);

        var cts = new CancellationTokenSource();
        object handle = Activator.CreateInstance(handleType, cts)!;
        Assert.Equal(false, isCancelProp!.GetValue(handle));

        // 16 并发线程同时触发取消释放与状态读取：必须幂等、零异常、无双释放
        Parallel.For(0, 16, i =>
        {
            if (i % 2 == 0)
            {
                cancelDispose!.Invoke(handle, null);
            }
            else
            {
                _ = isCancelProp.GetValue(handle);
                cancelDispose!.Invoke(handle, null);
            }
        });

        Assert.Equal(true, isCancelProp.GetValue(handle));
        // 再次重复释放也必须安全幂等
        cancelDispose!.Invoke(handle, null);
        Assert.Equal(true, isCancelProp.GetValue(handle));
    }

    [Fact]
    public void D4_NEW_01_PerfOptimizerStartupVfxThrottle_TreatsBepInExCfgAsSingleSourceOfTruth()
    {
        Type perfType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.PerfOptimizerModule", throwOnError: true)!;
        MethodInfo? resolve = perfType.GetMethod("ResolveStartupVfxThrottle", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(resolve);

        // 场景 1：旧 JSON 残留 vfxThrottle=true，但用户在 Hub 取消勾选或恢复默认（.cfg = false）
        bool jsonVal = true;
        bool effectiveOff = (bool)resolve!.Invoke(null, new object?[]
        {
            (bool?)false,
            new Func<bool, bool>(_ => jsonVal),
            new Action<bool>(v => jsonVal = v)
        })!;
        Assert.False(effectiveOff);
        Assert.False(jsonVal);

        // 场景 2：旧 JSON 为 false，用户在 Hub 开启（.cfg = true）
        bool effectiveOn = (bool)resolve.Invoke(null, new object?[]
        {
            (bool?)true,
            new Func<bool, bool>(_ => jsonVal),
            new Action<bool>(v => jsonVal = v)
        })!;
        Assert.True(effectiveOn);
        Assert.True(jsonVal);

        // 场景 3：无 BepInEx 配置项（null）时退回读取 JSON 值
        jsonVal = false;
        bool fallback = (bool)resolve.Invoke(null, new object?[]
        {
            (bool?)null,
            new Func<bool, bool>(_ => jsonVal),
            new Action<bool>(v => jsonVal = v)
        })!;
        Assert.False(fallback);
    }
}
