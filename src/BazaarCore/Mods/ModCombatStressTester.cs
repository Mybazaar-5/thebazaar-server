using System.Diagnostics;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Mods;

/// <summary>
/// 单场战斗的确定性计算指标快照（100% 与宿主机 CPU 快慢及并发负载无关）。
/// </summary>
public readonly record struct DeterministicMatchMetrics(
    int TotalTicks,
    int TotalEvents,
    int CardFires,
    int ActionEvents,
    int MaxActionChainEvents,
    int CascadeCapTicks,
    bool IsTimeout)
{
    /// <summary>判断本场战斗是否突破任一确定性事件/触发/因果链/级联撞顶预算上限。</summary>
    public bool ExceedsDeterministicBudget =>
        TotalEvents > ModCombatStressTester.MaxDeterministicEventsPerMatch
        || CardFires > ModCombatStressTester.MaxDeterministicCardFiresPerMatch
        || ActionEvents > ModCombatStressTester.MaxDeterministicActionEventsPerMatch
        || MaxActionChainEvents > ModCombatStressTester.MaxDeterministicActionChainEventsPerContext
        || CascadeCapTicks >= ModCombatStressTester.MaxDeterministicCascadeCapTicksPerMatch;
}

/// <summary>
/// 闸门 #9 聚合诊断输入（将纯评估逻辑与模拟循环解耦，便于单元测试确定性覆盖各种边界与反例）。
/// </summary>
public readonly record struct StressGateEvaluationInput(
    string ModId,
    string? SampleCardId,
    int MatchesExecuted,
    int TestedCardCount,
    int Exceptions,
    int NumericCorruptions,
    int MechanicBreakerTrips,
    int MechanicBudgetViolations,
    string? SampleMechanicDiag,
    int MaxDurationHits,
    int DeterministicStormMatches,
    int StormOrTimeoutUnionMatches,
    int PeakMatchEvents,
    int PeakMatchCardFires,
    int PeakMatchActionEvents,
    int PeakActionChainEvents,
    int PeakCascadeCapTicks,
    int SlowMatchesOver200ms,
    double AvgMatchMs,
    double MaxMatchMs,
    double ModSideWinRatePct,
    bool ExtremeHangDetected,
    string? ExtremeHangDetail);

/// <summary>
/// 闸门 #9 核心引擎：在完全隔离的临时 Overlay 仓库上执行 N 场（默认 50 场）真实 <see cref="CombatSimulator"/> 压力体检。
/// 覆盖：本 Mod 全部自定义/原位魔改物品与技能逐一上场、四阶品阶轮换、13 种附魔混装、相邻联动协同阵与 8 英雄原版对手阵，
/// 严查未捕获异常、确定性计算预算/级联触发风暴/300s 僵局、数值 NaN/Infinity 损坏、自定义机制熔断及极端防死循环墙钟熔断；
/// 宿主机单场墙钟 > 200ms 仅输出非阻断 Warning，彻底消除低配 VPS / 高负载环境误杀合法 Mod。
/// </summary>
public static class ModCombatStressTester
{
    /// <summary>宿主机单场墙钟性能告警线（非阻断 Warning，保持向后兼容）。</summary>
    public const double MaxAllowedSingleMatchMs = 200.0;
    public const double MaxAllowedTimeoutRatio = 0.20;

    /// <summary>确定性计算硬预算上限（与宿主机 CPU 速度无关）。</summary>
    public const int MaxDeterministicTotalTicksPerMatch = CombatSimulator.MaxDurationMs / CombatSimulator.TickMs;
    public const int MaxDeterministicEventsPerMatch = 30_000;
    public const int MaxDeterministicCardFiresPerMatch = 4_000;
    public const int MaxDeterministicActionEventsPerMatch = 20_000;
    public const int MaxDeterministicActionChainEventsPerContext = 256;
    public const int MaxDeterministicCascadeCapTicksPerMatch = 8;

    /// <summary>极端宿主机墙钟防死循环最后熔断线（单场 5s / 全批 60s）。</summary>
    public const double ExtremeSingleMatchTimeoutMs = 5_000.0;
    public const double ExtremeBatchTotalWallClockMs = 60_000.0;

    private static volatile bool _jitWarmedUp;

    /// <summary>
    /// 在隔离 Overlay 上执行真实战斗压力体检，产出结构化报告与闸门 #9 问题列表。
    /// 支持注入候选开放自定义机制 <paramref name="candidateMechanics"/> 并对熔断/步数超预算执行硬阻断。
    /// </summary>
    public static (ModCombatStressReport Report, List<ModValidationIssue> Issues) RunStressTest(
        ModPackage targetPackage,
        GameDataRepository overlayRepo,
        int matches = 50,
        int seedBase = 2026131,
        IReadOnlyDictionary<string, MechanicDefinition>? candidateMechanics = null)
    {
        var (report, issues, _) = RunStressTestWithTelemetry(
            targetPackage, overlayRepo, matches, seedBase, candidateMechanics);
        return (report, issues);
    }

    /// <summary>
    /// 在隔离 Overlay 上执行真实战斗压力体检，除标准报告与问题列表外，一并返回确定性峰值遥测快照（<see cref="StressGateEvaluationInput"/>），
    /// 供压测审计、阈值校准与回归测试读取 PeakEvents/PeakCardFires/PeakActionEvents/PeakActionChainEvents/PeakCascadeCapTicks。
    /// </summary>
    public static (ModCombatStressReport Report, List<ModValidationIssue> Issues, StressGateEvaluationInput Telemetry) RunStressTestWithTelemetry(
        ModPackage targetPackage,
        GameDataRepository overlayRepo,
        int matches = 50,
        int seedBase = 2026131,
        IReadOnlyDictionary<string, MechanicDefinition>? candidateMechanics = null)
    {
        string modId = targetPackage.Manifest.Id;
        int actualMatches = Math.Clamp(matches, 1, 200);
        var issues = new List<ModValidationIssue>();

        // 组装隔离沙盒机制表（合并已有已注册机制 + 外部候选 + 本包 mechanics/*.json，绝不污染全局运行态）
        var effectiveMechanics = new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase);
        var globalMechs = ModRuntimeHooks.GetRegisteredMechanics();
        if (globalMechs != null)
        {
            foreach (var (k, v) in globalMechs) effectiveMechanics[k] = v;
        }
        if (candidateMechanics != null)
        {
            foreach (var (k, v) in candidateMechanics) effectiveMechanics[k] = v;
        }
        foreach (var m in targetPackage.Mechanics)
        {
            if (!string.IsNullOrWhiteSpace(m.Id))
                effectiveMechanics[m.Id.Trim()] = m;
        }

        // 收集本 Mod 涉及的全部可进战斗的物品与技能（含新建卡与原位 Patch）
        var modItems = new List<CardTemplate>();
        var modSkills = new List<CardTemplate>();
        var testedCardIds = new List<string>();
        var seenTested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CollectCard(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !seenTested.Add(id)) return;
            var tpl = overlayRepo.GetCard(id);
            if (tpl == null) return;
            if (string.Equals(tpl.Type, "Item", StringComparison.Ordinal))
            {
                modItems.Add(tpl);
                testedCardIds.Add(tpl.Id);
            }
            else if (string.Equals(tpl.Type, "Skill", StringComparison.Ordinal))
            {
                modSkills.Add(tpl);
                testedCardIds.Add(tpl.Id);
            }
        }

        foreach (var c in targetPackage.Cards) CollectCard(c.CustomId);
        foreach (var p in targetPackage.Patches) CollectCard(p.TargetTemplateId);

        // 从原版仓库准备稳定的辅助卡池与对手卡池（确保即使 Mod 只有 1 张卡也能凑齐真实满格阵型协同测试）
        var vanillaItems = overlayRepo.AlwaysSpawnableItems
            .Where(c => !seenTested.Contains(c.Id) && c.Type == "Item")
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Take(120)
            .ToList();
        var vanillaSkills = overlayRepo.AlwaysSpawnableSkills
            .Where(c => !seenTested.Contains(c.Id) && c.Type == "Skill")
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Take(60)
            .ToList();

        if (vanillaItems.Count == 0)
            vanillaItems = overlayRepo.AllItems.Take(60).ToList();

        // 若为纯奇遇/纯外观/纯词条 Mod，也至少用原版卡 + 隔离仓库跑一轮回归冒烟验证引擎未受损
        EnsureJitWarmup(overlayRepo, vanillaItems);

        var matchTimesMs = new List<double>(actualMatches);
        var reusableChainCounts = new Dictionary<string, int>(64, StringComparer.Ordinal);
        int matchesExecuted = 0;
        int exceptions = 0;
        int maxDurationHits = 0;
        int deterministicStormMatches = 0;
        int stormOrTimeoutUnionMatches = 0;
        int peakMatchEvents = 0;
        int peakMatchCardFires = 0;
        int peakMatchActionEvents = 0;
        int peakActionChainEvents = 0;
        int peakCascadeCapTicks = 0;
        int numericCorruptions = 0;
        int slowMatchesOver200ms = 0;
        int modSideWins = 0;
        int mechanicBreakerTrips = 0;
        int mechanicBudgetViolations = 0;
        bool extremeHangDetected = false;
        string? extremeHangDetail = null;
        var mechanicDiagnostics = new List<MechanicDiagnostic>();
        double totalFightDurationSec = 0;

        // 若本包或待测卡挂载了自定义沙盒机制，预筛选用于对抗测试的摧毁/修复/多段输出/DOT 原版卡
        bool hasCustomMechanicsToStress = targetPackage.Mechanics.Count > 0
            || modItems.Any(c => c.CustomMechanics is { Count: > 0 })
            || modSkills.Any(c => c.CustomMechanics is { Count: > 0 });
        List<CardTemplate>? adversarialOpponents = null;
        List<CardTemplate>? adversarialAllies = null;
        if (hasCustomMechanicsToStress)
        {
            adversarialOpponents = overlayRepo.AllItems
                .Where(c => c.Type == "Item" && !seenTested.Contains(c.Id) && c.Abilities.Values.Any(a =>
                    a.Action?.Type is "TActionCardDisable" or "TActionCardDestroy" or "TActionPlayerBurnApply" or "TActionPlayerPoisonApply" or "TActionPlayerDamage"))
                .Take(24)
                .ToList();
            adversarialAllies = overlayRepo.AllItems
                .Where(c => c.Type == "Item" && !seenTested.Contains(c.Id) && c.Abilities.Values.Any(a =>
                    a.Action?.Type is "TActionCardRepair" or "TActionCardCharge" or "TActionCardHaste"))
                .Take(24)
                .ToList();
        }

        // D3-P2-01：筛选尚未挂载到任何 modItems/modSkills 的“孤儿自定义机制”，构建 3 类载体（纯辅助无输出/0成长/技能）纳入实战压测
        var attachedMechanicIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in modItems)
            if (c.CustomMechanics != null)
                foreach (var m in c.CustomMechanics)
                    if (!string.IsNullOrWhiteSpace(m.MechanicId)) attachedMechanicIds.Add(m.MechanicId.Trim());
        foreach (var s in modSkills)
            if (s.CustomMechanics != null)
                foreach (var m in s.CustomMechanics)
                    if (!string.IsNullOrWhiteSpace(m.MechanicId)) attachedMechanicIds.Add(m.MechanicId.Trim());
        var orphanMechanics = targetPackage.Mechanics
            .Where(m => !string.IsNullOrWhiteSpace(m.Id) && !attachedMechanicIds.Contains(m.Id.Trim()))
            .ToList();

        CardTier[] tiers = [CardTier.Bronze, CardTier.Silver, CardTier.Gold, CardTier.Diamond];
        string[] heroes = HeroNames.PlayableHeroes;
        string[] enchants = EnchantmentNames.All;
        var batchSw = Stopwatch.StartNew();

        for (int i = 0; i < actualMatches; i++)
        {
            int seed = seedBase + i * 7919;
            var rng = new Random(seed);
            var tier = tiers[i % tiers.Length];
            int day = 2 + (i % 10);
            int baseHp = 400 + day * 280;
            string hero1 = heroes[i % heroes.Length];
            string hero2 = heroes[(i + 3) % heroes.Length];

            // D3-P2-01：第 0~1 场开启 recordTimeline + syncPermanentChanges 覆核帧时间轴与战后结转；第 2~3 场将 Mod 阵型放至 P2 测双侧对称性
            bool enableTimelineAndSync = i < 2;
            bool swapModToP2 = actualMatches >= 4 && (i == 2 || i == 3);

            var p1 = new PlayerState($"mod_p1_{i}", $"Mod测试方#{i}", hero1, baseHp);
            p1.Gold = 10 + (i % 6) * 8; // 覆盖低金(<15)与高金(>=20)等经济条件光环
            p1.Attributes["Day"] = day;
            p1.Attributes["Level"] = Math.Min(20, day + 1);

            var p2 = new PlayerState($"opp_p2_{i}", $"原版对手#{i}", hero2, baseHp);
            p2.Gold = 15 + (i % 4) * 6;
            p2.Attributes["Day"] = day;
            p2.Attributes["Level"] = Math.Min(20, day + 1);

            var modSide = swapModToP2 ? p2 : p1;
            var oppSide = swapModToP2 ? p1 : p2;

            // 填充 Mod 侧：优先放入本 Mod 的物品与技能，每场轮换焦点卡与附魔，并用原版卡补齐邻接位激发相邻联动
            int usedSlots = 0;
            int cardSeq = 0;
            if (modItems.Count > 0)
            {
                // 焦点卡必上场；偶数场尽可能塞入多张 Mod 卡做组合协同，奇数场混搭原版工具/武器测邻接协同
                int startIdx = i % modItems.Count;
                for (int k = 0; k < modItems.Count && usedSlots < 10; k++)
                {
                    var tpl = modItems[(startIdx + k) % modItems.Count];
                    int sz = (int)tpl.CardSize;
                    if (usedSlots + sz > 10) continue;

                    var instTier = PickValidTier(tpl, tier);
                    string? ench = (k == 0 && i >= 12) ? enchants[i % enchants.Length] : null;
                    var inst = new CardInstance(tpl, instTier, ench, $"m_{i}_{cardSeq++}");
                    if (modSide.Hand.TryPlaceOrShift(inst, usedSlots))
                        usedSlots += sz;

                    // 奇数场每放一张 Mod 卡就穿插一张原版小型/中型工具或武器测试 Neighbor 触发
                    if (i % 2 == 1 && vanillaItems.Count > 0 && usedSlots < 10)
                    {
                        var fillerTpl = vanillaItems[rng.Next(vanillaItems.Count)];
                        int fsz = (int)fillerTpl.CardSize;
                        if (usedSlots + fsz <= 10)
                        {
                            var filler = new CardInstance(fillerTpl, PickValidTier(fillerTpl, tier), null, $"f_{i}_{cardSeq++}");
                            if (modSide.Hand.TryPlaceOrShift(filler, usedSlots))
                                usedSlots += fsz;
                        }
                    }
                }
            }

            // D3-P2-01：若有孤儿自定义机制，按场次轮换挂入 3 类典型载体（0: 无输出纯辅助物品、1: 0 成长物品、2: Skill 技能）
            if (orphanMechanics.Count > 0)
            {
                var orphan = orphanMechanics[i % orphanMechanics.Count];
                int carrierKind = i % 3;
                var attachList = new List<CardMechanicAttachment> { new() { MechanicId = orphan.Id } };
                if (carrierKind == 2)
                {
                    var skillCarrier = new CardTemplate
                    {
                        Id = $"stress_orphan_skill_{i}",
                        InternalName = "StressOrphanSkill",
                        Type = "Skill",
                        Size = "Small",
                        StartingTier = "Bronze",
                        CustomMechanics = attachList,
                    };
                    modSide.Skills.Add(new CardInstance(skillCarrier, CardTier.Bronze, null, $"os_{i}"));
                }
                else if (usedSlots < 10)
                {
                    var itemCarrier = new CardTemplate
                    {
                        Id = $"stress_orphan_item_{carrierKind}_{i}",
                        InternalName = carrierKind == 0 ? "StressSupportCarrier" : "StressZeroGrowthCarrier",
                        Type = "Item",
                        Size = "Small",
                        StartingTier = "Bronze",
                        Attributes = carrierKind == 0
                            ? new Dictionary<string, double> { ["CooldownMax"] = 3000, ["ShieldApplyAmount"] = 10 }
                            : new Dictionary<string, double> { ["CooldownMax"] = 4000, ["DamageAmount"] = 0 },
                        CustomMechanics = attachList,
                    };
                    var carrierInst = new CardInstance(itemCarrier, CardTier.Bronze, null, $"oi_{i}");
                    if (modSide.Hand.TryPlaceOrShift(carrierInst, usedSlots))
                        usedSlots += 1;
                }
            }

            // 补齐 Mod 侧剩余格子至接近满格（8~10 格）
            int safetyFill = 0;
            while (usedSlots <= 8 && vanillaItems.Count > 0 && safetyFill++ < 20)
            {
                var tpl = vanillaItems[rng.Next(vanillaItems.Count)];
                int sz = (int)tpl.CardSize;
                if (usedSlots + sz <= 10)
                {
                    var inst = new CardInstance(tpl, PickValidTier(tpl, tier), null, $"p1v_{i}_{cardSeq++}");
                    if (modSide.Hand.TryPlaceOrShift(inst, usedSlots))
                        usedSlots += sz;
                }
            }

            // Mod 侧装载本 Mod 技能
            for (int s = 0; s < modSkills.Count; s++)
            {
                var stpl = modSkills[s];
                modSide.Skills.Add(new CardInstance(stpl, PickValidTier(stpl, tier), null, $"ms_{i}_{s}"));
            }
            if (vanillaSkills.Count > 0)
            {
                var vs = vanillaSkills[rng.Next(vanillaSkills.Count)];
                modSide.Skills.Add(new CardInstance(vs, PickValidTier(vs, tier), null, $"vs1_{i}"));
            }

            // 填充对手侧（原版典型对手满格阵）
            int p2Slots = 0;
            int p2Fill = 0;
            while (p2Slots <= 8 && vanillaItems.Count > 0 && p2Fill++ < 25)
            {
                var tpl = vanillaItems[rng.Next(vanillaItems.Count)];
                int sz = (int)tpl.CardSize;
                if (p2Slots + sz <= 10)
                {
                    var inst = new CardInstance(tpl, PickValidTier(tpl, tier), null, $"p2v_{i}_{p2Fill}");
                    if (oppSide.Hand.TryPlaceOrShift(inst, p2Slots))
                        p2Slots += sz;
                }
            }
            if (vanillaSkills.Count > 0)
            {
                var vs2 = vanillaSkills[rng.Next(vanillaSkills.Count)];
                oppSide.Skills.Add(new CardInstance(vs2, PickValidTier(vs2, tier), null, $"vs2_{i}"));
            }

            // 前 5 场含自定义机制对抗压测：为己方混入修复/充能卡、为敌方混入摧毁/禁用/高频伤害/DOT 卡，极限验证事前拦截与不变量
            if (hasCustomMechanicsToStress && i < 5)
            {
                if (adversarialAllies is { Count: > 0 } && usedSlots <= 8)
                {
                    var allyTpl = adversarialAllies[i % adversarialAllies.Count];
                    if (usedSlots + (int)allyTpl.CardSize <= 10)
                    {
                        var allyInst = new CardInstance(allyTpl, PickValidTier(allyTpl, tier), null, $"adv_a_{i}");
                        if (modSide.Hand.TryPlaceOrShift(allyInst, usedSlots))
                            usedSlots += (int)allyTpl.CardSize;
                    }
                }
                if (adversarialOpponents is { Count: > 0 } && p2Slots <= 8)
                {
                    var oppTpl = adversarialOpponents[i % adversarialOpponents.Count];
                    if (p2Slots + (int)oppTpl.CardSize <= 10)
                    {
                        var oppInst = new CardInstance(oppTpl, PickValidTier(oppTpl, tier), null, $"adv_o_{i}");
                        if (oppSide.Hand.TryPlaceOrShift(oppInst, p2Slots))
                            p2Slots += (int)oppTpl.CardSize;
                    }
                }
            }

            // 开启确定性事件采集（前 2 场同步开启 recordTimeline + syncPermanentChanges 覆核回放帧与战后结转，其余场次保持低分配）
            matchesExecuted++;
            var sw = Stopwatch.StartNew();
            try
            {
                var result = CombatSimulator.Simulate(
                    overlayRepo,
                    p1,
                    p2,
                    seed: seed,
                    combatType: "PVP",
                    recordTimeline: enableTimelineAndSync,
                    recordEvents: true,
                    syncPermanentChanges: enableTimelineAndSync,
                    modifiers: null,
                    sandstormEnabled: true,
                    timelineSampleIntervalMs: CombatSimulator.TickMs,
                    mechanicDefinitionsOverride: effectiveMechanics.Count > 0 ? effectiveMechanics : null);
                sw.Stop();

                mechanicBreakerTrips += result.MechanicCircuitBreakerTrips;
                mechanicBudgetViolations += result.MechanicBudgetViolations;
                if (result.MechanicDiagnostics.Count > 0 && mechanicDiagnostics.Count < 30)
                {
                    for (int d = 0; d < result.MechanicDiagnostics.Count && mechanicDiagnostics.Count < 30; d++)
                        mechanicDiagnostics.Add(result.MechanicDiagnostics[d]);
                }

                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                matchTimesMs.Add(elapsedMs);
                totalFightDurationSec += result.DurationMs / 1000.0;

                if (elapsedMs > MaxAllowedSingleMatchMs)
                    slowMatchesOver200ms++;

                var metrics = AnalyzeDeterministicMetrics(result, reusableChainCounts);
                if (metrics.TotalEvents > peakMatchEvents) peakMatchEvents = metrics.TotalEvents;
                if (metrics.CardFires > peakMatchCardFires) peakMatchCardFires = metrics.CardFires;
                if (metrics.ActionEvents > peakMatchActionEvents) peakMatchActionEvents = metrics.ActionEvents;
                if (metrics.MaxActionChainEvents > peakActionChainEvents) peakActionChainEvents = metrics.MaxActionChainEvents;
                if (metrics.CascadeCapTicks > peakCascadeCapTicks) peakCascadeCapTicks = metrics.CascadeCapTicks;

                bool isTimeout = metrics.IsTimeout;
                bool isDeterministicStorm = metrics.ExceedsDeterministicBudget;
                if (isTimeout) maxDurationHits++;
                if (isDeterministicStorm) deterministicStormMatches++;
                if (isTimeout || isDeterministicStorm) stormOrTimeoutUnionMatches++;

                if (!double.IsFinite(result.Player1TotalDamageDealt)
                    || !double.IsFinite(result.Player2TotalDamageDealt)
                    || !double.IsFinite(result.Player1TotalShieldGained)
                    || !double.IsFinite(result.Player2TotalShieldGained))
                {
                    numericCorruptions++;
                }

                if (string.Equals(result.WinnerPlayerId, modSide.PlayerId, StringComparison.Ordinal))
                    modSideWins++;

                if (elapsedMs > ExtremeSingleMatchTimeoutMs)
                {
                    extremeHangDetected = true;
                    extremeHangDetail = $"第 {i + 1} 场战斗模拟（seed={seed}）耗时 {elapsedMs:0.0}ms，超过极端防死循环上限 {ExtremeSingleMatchTimeoutMs:0}ms，已提前中止剩余场次。";
                    break;
                }
                if (batchSw.Elapsed.TotalMilliseconds > ExtremeBatchTotalWallClockMs)
                {
                    extremeHangDetected = true;
                    extremeHangDetail = $"累计已执行 {matchesExecuted}/{actualMatches} 场耗时 {batchSw.Elapsed.TotalMilliseconds:0.0}ms，超过批次总防死循环上限 {ExtremeBatchTotalWallClockMs:0}ms，已提前中止。";
                    break;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                matchTimesMs.Add(sw.Elapsed.TotalMilliseconds);
                exceptions++;
                if (exceptions <= 3)
                {
                    issues.Add(new ModValidationIssue(
                        ModValidationSeverity.Error,
                        modId,
                        "SIM_UNHANDLED_EXCEPTION",
                        $"第 {i + 1} 场战斗模拟（seed={seed}）抛出未捕获异常：{ex.GetType().Name}: {ex.Message}",
                        testedCardIds.FirstOrDefault(),
                        GateId: 9,
                        FixHintZh: "请检查自定义卡的动作目标/数值节点结构是否缺少必需字段或除零。"));
                }
            }
        }

        matchTimesMs.Sort();
        double avgMs = matchTimesMs.Count > 0 ? matchTimesMs.Average() : 0;
        double maxMs = matchTimesMs.Count > 0 ? matchTimesMs[^1] : 0;
        double p95Ms = matchTimesMs.Count > 0
            ? matchTimesMs[Math.Clamp((int)Math.Ceiling(matchTimesMs.Count * 0.95) - 1, 0, matchTimesMs.Count - 1)]
            : 0;
        double winRatePct = matchesExecuted > 0 ? Math.Round(modSideWins * 100.0 / matchesExecuted, 1) : 0;
        double avgFightSec = matchesExecuted > 0 ? Math.Round(totalFightDurationSec / matchesExecuted, 1) : 0;

        var evalInput = new StressGateEvaluationInput(
            ModId: modId,
            SampleCardId: testedCardIds.FirstOrDefault(),
            MatchesExecuted: matchesExecuted,
            TestedCardCount: testedCardIds.Count,
            Exceptions: exceptions,
            NumericCorruptions: numericCorruptions,
            MechanicBreakerTrips: mechanicBreakerTrips,
            MechanicBudgetViolations: mechanicBudgetViolations,
            SampleMechanicDiag: mechanicDiagnostics.FirstOrDefault()?.MessageZh,
            MaxDurationHits: maxDurationHits,
            DeterministicStormMatches: deterministicStormMatches,
            StormOrTimeoutUnionMatches: stormOrTimeoutUnionMatches,
            PeakMatchEvents: peakMatchEvents,
            PeakMatchCardFires: peakMatchCardFires,
            PeakMatchActionEvents: peakMatchActionEvents,
            PeakActionChainEvents: peakActionChainEvents,
            PeakCascadeCapTicks: peakCascadeCapTicks,
            SlowMatchesOver200ms: slowMatchesOver200ms,
            AvgMatchMs: avgMs,
            MaxMatchMs: maxMs,
            ModSideWinRatePct: winRatePct,
            ExtremeHangDetected: extremeHangDetected,
            ExtremeHangDetail: extremeHangDetail);

        var (cascadeGuardTrips, balanceHint) = EvaluateStressIssues(evalInput, issues);

        var report = new ModCombatStressReport
        {
            MatchesRun = matchesExecuted,
            SeedBase = seedBase,
            Exceptions = exceptions,
            MaxDurationHits = maxDurationHits,
            CascadeGuardTrips = cascadeGuardTrips,
            NumericCorruptions = numericCorruptions,
            SlowMatchesOver200ms = slowMatchesOver200ms,
            AvgMatchMs = Math.Round(avgMs, 2),
            P95MatchMs = Math.Round(p95Ms, 2),
            MaxMatchMs = Math.Round(maxMs, 2),
            ModSideWins = modSideWins,
            ModSideWinRatePct = winRatePct,
            AvgFightDurationSec = avgFightSec,
            TestedCardIds = testedCardIds,
            BalanceHintZh = balanceHint,
            MechanicCircuitBreakerTrips = mechanicBreakerTrips,
            MechanicBudgetViolations = mechanicBudgetViolations,
            MechanicDiagnostics = mechanicDiagnostics
        };

        return (report, issues, evalInput);
    }

    /// <summary>
    /// 单遍 O(N) 计算单场战斗结果的确定性指标：
    /// - CascadeCapTicks 严格按 TimeMs 桶每桶至多计 1 次（单 Tick 产生 35+ 次 CardFired 仅计 1 次，绝不重复累加）；
    /// - MaxActionChainEvents 严格排除全盘光环/属性广播事件（AuraExecuted/CardAttributeModified/CardAttributeDeleted/TickSnapshot 等），
    ///   仅度量真实动作与触发因果链长度，杜绝合法全盘光环/协同 Mod 误杀。
    /// </summary>
    public static DeterministicMatchMetrics AnalyzeDeterministicMetrics(
        CombatResult result,
        Dictionary<string, int>? reusableChainCounts = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        int totalTicks = Math.Max(0, result.DurationMs) / CombatSimulator.TickMs;
        bool isTimeout = result.IsTimeout || result.DurationMs >= CombatSimulator.MaxDurationMs;
        var events = result.Events;
        if (events == null || events.Count == 0)
        {
            return new DeterministicMatchMetrics(
                TotalTicks: totalTicks,
                TotalEvents: 0,
                CardFires: 0,
                ActionEvents: 0,
                MaxActionChainEvents: 0,
                CascadeCapTicks: 0,
                IsTimeout: isTimeout);
        }

        var chainActionCounts = reusableChainCounts ?? new Dictionary<string, int>(64, StringComparer.Ordinal);
        chainActionCounts.Clear();

        int cardFires = 0;
        int actionEvents = 0;
        int maxActionChainEvents = 0;
        int cascadeCapTicks = 0;

        int curTickMs = int.MinValue;
        int firesInCurTick = 0;
        bool curTickCapped = false;
        bool sawOutOfOrderTick = false;
        Dictionary<int, int>? outOfOrderTickFires = null;

        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            int t = ev.TimeMs;

            if (t != curTickMs)
            {
                if (t < curTickMs)
                {
                    sawOutOfOrderTick = true;
                }
                curTickMs = t;
                firesInCurTick = 0;
                curTickCapped = false;
            }

            if (ev.Type == CombatEventType.CardFired)
            {
                cardFires++;
                if (!sawOutOfOrderTick)
                {
                    firesInCurTick++;
                    if (!curTickCapped && firesInCurTick >= CombatSimulator.MaxTickCascadeFires)
                    {
                        curTickCapped = true;
                        cascadeCapTicks++;
                    }
                }
            }

            if (IsDeterministicActionEvent(ev.Type))
            {
                actionEvents++;
                if (!string.IsNullOrEmpty(ev.ExecutionContextId))
                {
                    int next = chainActionCounts.GetValueOrDefault(ev.ExecutionContextId, 0) + 1;
                    chainActionCounts[ev.ExecutionContextId] = next;
                    if (next > maxActionChainEvents)
                        maxActionChainEvents = next;
                }
            }
        }

        // 若调用方传入非单调时间序列，按 TimeMs 字典精确聚合确保同一 TimeMs 桶严格至多计 1 次
        if (sawOutOfOrderTick)
        {
            outOfOrderTickFires = new Dictionary<int, int>();
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Type == CombatEventType.CardFired)
                {
                    int t = events[i].TimeMs;
                    outOfOrderTickFires[t] = outOfOrderTickFires.GetValueOrDefault(t, 0) + 1;
                }
            }

            cascadeCapTicks = 0;
            foreach (var count in outOfOrderTickFires.Values)
            {
                if (count >= CombatSimulator.MaxTickCascadeFires)
                    cascadeCapTicks++;
            }
        }

        return new DeterministicMatchMetrics(
            TotalTicks: totalTicks,
            TotalEvents: events.Count,
            CardFires: cardFires,
            ActionEvents: actionEvents,
            MaxActionChainEvents: maxActionChainEvents,
            CascadeCapTicks: cascadeCapTicks,
            IsTimeout: isTimeout);
    }

    /// <summary>
    /// 聚合评判闸门 #9 问题列表并返回去重后的 <c>CascadeGuardTrips</c> 与平衡性提示文案：
    /// - 确定性指标超标 / 300s 僵局率 > 20% / 异常 / 数值损坏 / 机制熔断 / 极端墙钟 > 5000ms -> Error；
    /// - 宿主机单场墙钟 > 200ms（未触发极端熔断） -> 非阻断 Warning（不阻断健康 Mod）。
    /// </summary>
    public static (int CascadeGuardTrips, string BalanceHintZh) EvaluateStressIssues(
        StressGateEvaluationInput input,
        List<ModValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        int executed = Math.Max(0, input.MatchesExecuted);

        if (input.ExtremeHangDetected)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                input.ModId,
                "SIM_EXTREME_HANG_DETECTED",
                input.ExtremeHangDetail ?? $"战斗模拟触发极端墙钟防死循环熔断（最慢 {input.MaxMatchMs:0.0}ms > {ExtremeSingleMatchTimeoutMs:0}ms）。",
                input.SampleCardId,
                GateId: 9,
                FixHintZh: "检查自定义卡或机制是否存在不可终止的死循环或极端高频全盘重算。"));
        }
        else if (input.SlowMatchesOver200ms > 0)
        {
            // D8-P1-05：宿主机墙钟超 200ms 仅输出非阻断 Warning，彻底消除低配 VPS / 高负载误杀合法 Mod
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Warning,
                input.ModId,
                "SIM_PERF_DEGRADATION_OVER_200MS",
                $"共 {input.SlowMatchesOver200ms}/{executed} 场战斗宿主机墙钟耗时超过 {MaxAllowedSingleMatchMs:0}ms（最慢 {input.MaxMatchMs:0.0}ms，均值 {input.AvgMatchMs:0.0}ms）；未违反确定性计算预算，仅作性能关注提示。",
                input.SampleCardId,
                GateId: 9,
                FixHintZh: "性能提示（不阻断发布）：若持续偏慢，可检查冷却与全盘光环复杂度。"));
        }

        if (input.NumericCorruptions > 0)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                input.ModId,
                "SIM_NUMERIC_CORRUPTION",
                $"共 {input.NumericCorruptions} 场战斗出现 NaN / Infinity 数值损坏。",
                input.SampleCardId,
                GateId: 9,
                FixHintZh: "检查光环乘法倍率与属性增长公式，避免指数级溢出。"));
        }

        if (input.MechanicBreakerTrips > 0 || input.MechanicBudgetViolations > 0)
        {
            string sampleDiag = input.SampleMechanicDiag ?? "自定义机制触发熔断或递归步数超预算";
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                input.ModId,
                "SIM_MECHANIC_SAFETY_TRIP",
                $"对抗压力体检检测到自定义机制安全违规（熔断 {input.MechanicBreakerTrips} 次，超预算 {input.MechanicBudgetViolations} 次）：{sampleDiag}",
                input.SampleCardId,
                GateId: 9,
                FixHintZh: "检查自定义机制是否存在互相触发死循环、缺少 maxTriggersPerCombat 上限或异常数值表达式。"));
        }

        bool timeoutStorm = executed > 0 && (double)input.MaxDurationHits / executed > MaxAllowedTimeoutRatio;
        bool deterministicStorm = input.DeterministicStormMatches > 0;

        // 按场去重计次（严格满足 0 <= cascadeGuardTrips <= executed）
        int cascadeGuardTrips = 0;
        if (timeoutStorm && deterministicStorm)
        {
            cascadeGuardTrips = Math.Clamp(
                Math.Max(input.StormOrTimeoutUnionMatches, Math.Max(input.DeterministicStormMatches, input.MaxDurationHits)),
                0,
                executed);
        }
        else if (timeoutStorm)
        {
            cascadeGuardTrips = Math.Clamp(input.MaxDurationHits, 0, executed);
        }
        else if (deterministicStorm)
        {
            cascadeGuardTrips = Math.Clamp(input.DeterministicStormMatches, 0, executed);
        }

        if (timeoutStorm || deterministicStorm)
        {
            var reasons = new List<string>(2);
            if (timeoutStorm)
                reasons.Add($"{input.MaxDurationHits}/{executed} 场撞满 300s/{MaxDeterministicTotalTicksPerMatch} ticks 僵局（超过 {MaxAllowedTimeoutRatio:P0} 警戒线）");
            if (deterministicStorm)
                reasons.Add($"{input.DeterministicStormMatches}/{executed} 场突破确定性计算预算（峰值：事件={input.PeakMatchEvents}/{MaxDeterministicEventsPerMatch}，施放={input.PeakMatchCardFires}/{MaxDeterministicCardFiresPerMatch}，动作={input.PeakMatchActionEvents}/{MaxDeterministicActionEventsPerMatch}，单链动作={input.PeakActionChainEvents}/{MaxDeterministicActionChainEventsPerContext}，级联撞顶Tick={input.PeakCascadeCapTicks}/{MaxDeterministicCascadeCapTicksPerMatch}）");

            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                input.ModId,
                "SIM_CASCADE_STORM_DETECTED",
                $"确定性压力红线触发：{string.Join("；", reasons)}，疑似无限护盾/回血僵局或高频级联触发风暴。",
                input.SampleCardId,
                GateId: 9,
                FixHintZh: "适当下调回复/护盾成长倍率，或收敛超短冷却、互充与高频被动连锁触发上限。"));
        }

        string balanceHint;
        if (input.ExtremeHangDetected)
        {
            balanceHint = $"压测因极端墙钟熔断在第 {executed} 场提前中止（最慢 {input.MaxMatchMs:0.0}ms），请优先修复死循环/性能病理。";
        }
        else if (input.TestedCardCount == 0)
        {
            balanceHint = $"已完成 {executed} 场隔离回归压测（纯事件/词条/外观包，0 异常，平均单场 {input.AvgMatchMs:0.0}ms）。";
        }
        else if (executed > 0 && input.ModSideWinRatePct > 85.0)
        {
            balanceHint = $"{executed} 场模拟胜率 {input.ModSideWinRatePct:0.0}%（偏高 >85%），建议适度微调基础伤害/护盾或延长冷却以保持游戏乐趣。";
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Warning,
                input.ModId,
                "SIM_BALANCE_WARNING",
                balanceHint,
                input.SampleCardId,
                GateId: 9,
                FixHintZh: "平衡建议（不阻断发布）：可适当调低高阶倍率或将冷却延长 0.5~1 秒。"));
        }
        else if (executed > 0 && input.ModSideWinRatePct < 15.0)
        {
            balanceHint = $"{executed} 场模拟胜率 {input.ModSideWinRatePct:0.0}%（偏低 <15%），建议检查自定义卡数值是否偏保守或核心触发条件过严。";
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Warning,
                input.ModId,
                "SIM_BALANCE_WARNING",
                balanceHint,
                input.SampleCardId,
                GateId: 9,
                FixHintZh: "平衡建议（不阻断发布）：可适当提高基础伤害/护盾或放宽触发前提。"));
        }
        else
        {
            balanceHint = $"{executed} 场模拟胜率 {input.ModSideWinRatePct:0.0}%（处于 15%~85% 健康区间），平均耗时 {input.AvgMatchMs:0.0}ms，确定性预算全部达标。";
        }

        return (cascadeGuardTrips, balanceHint);
    }

    private static bool IsDeterministicActionEvent(CombatEventType type)
    {
        return type is not (
            CombatEventType.FightStarted
            or CombatEventType.FightEnded
            or CombatEventType.TickSnapshot
            or CombatEventType.SandstormWarning
            or CombatEventType.AuraExecuted
            or CombatEventType.CardAttributeModified
            or CombatEventType.CardAttributeDeleted);
    }

    private static CardTier PickValidTier(CardTemplate tpl, CardTier desired)
    {
        var start = TierHelper.ParseTier(tpl.StartingTier, CardTier.Bronze);
        return desired < start ? start : desired;
    }

    private static void EnsureJitWarmup(GameDataRepository repo, List<CardTemplate> vanillaItems)
    {
        if (_jitWarmedUp || vanillaItems.Count < 2) return;
        try
        {
            var p1 = new PlayerState("warm1", "w1", HeroNames.Vanessa, 300);
            var p2 = new PlayerState("warm2", "w2", HeroNames.Dooley, 300);
            p1.Hand.TryPlaceOrShift(new CardInstance(vanillaItems[0], vanillaItems[0].DefaultTier, null, "w1"), 0);
            p2.Hand.TryPlaceOrShift(new CardInstance(vanillaItems[1], vanillaItems[1].DefaultTier, null, "w2"), 0);
            CombatSimulator.Simulate(repo, p1, p2, seed: 1, recordTimeline: false, recordEvents: true);
            _jitWarmedUp = true;
        }
        catch
        {
            // 预热失败不影响后续正式统计
        }
    }
}
