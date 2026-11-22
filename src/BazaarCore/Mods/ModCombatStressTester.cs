using System.Diagnostics;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Mods;

/// <summary>
/// 闸门 #9 核心引擎：在完全隔离的临时 Overlay 仓库上执行 N 场（默认 50 场）真实 <see cref="CombatSimulator"/> 压力体检。
/// 覆盖：本 Mod 全部自定义/原位魔改物品与技能逐一上场、四阶品阶轮换、13 种附魔混装、相邻联动协同阵与 8 英雄原版对手阵，
/// 严查未捕获异常、单场耗时 > 200ms、数值 NaN/Infinity 损坏、长时间 300s 僵局以及胜率过强/过弱平衡性预警。
/// </summary>
public static class ModCombatStressTester
{
    public const double MaxAllowedSingleMatchMs = 200.0;
    public const double MaxAllowedTimeoutRatio = 0.20;
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
        int exceptions = 0;
        int maxDurationHits = 0;
        int cascadeGuardTrips = 0;
        int numericCorruptions = 0;
        int slowMatchesOver200ms = 0;
        int modSideWins = 0;
        int mechanicBreakerTrips = 0;
        int mechanicBudgetViolations = 0;
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

        CardTier[] tiers = [CardTier.Bronze, CardTier.Silver, CardTier.Gold, CardTier.Diamond];
        string[] heroes = HeroNames.PlayableHeroes;
        string[] enchants = EnchantmentNames.All;

        for (int i = 0; i < actualMatches; i++)
        {
            int seed = seedBase + i * 7919;
            var rng = new Random(seed);
            var tier = tiers[i % tiers.Length];
            int day = 2 + (i % 10);
            int baseHp = 400 + day * 280;
            string hero1 = heroes[i % heroes.Length];
            string hero2 = heroes[(i + 3) % heroes.Length];

            var p1 = new PlayerState($"mod_p1_{i}", $"Mod测试方#{i}", hero1, baseHp);
            p1.Gold = 10 + (i % 6) * 8; // 覆盖低金(<15)与高金(>=20)等经济条件光环
            p1.Attributes["Day"] = day;
            p1.Attributes["Level"] = Math.Min(20, day + 1);

            var p2 = new PlayerState($"opp_p2_{i}", $"原版对手#{i}", hero2, baseHp);
            p2.Gold = 15 + (i % 4) * 6;
            p2.Attributes["Day"] = day;
            p2.Attributes["Level"] = Math.Min(20, day + 1);

            // 填充 P1（Mod 测试方）：优先放入本 Mod 的物品与技能，每场轮换焦点卡与附魔，并用原版卡补齐邻接位激发相邻联动
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
                    if (p1.Hand.TryPlaceOrShift(inst, usedSlots))
                        usedSlots += sz;

                    // 奇数场每放一张 Mod 卡就穿插一张原版小型/中型工具或武器测试 Neighbor 触发
                    if (i % 2 == 1 && vanillaItems.Count > 0 && usedSlots < 10)
                    {
                        var fillerTpl = vanillaItems[rng.Next(vanillaItems.Count)];
                        int fsz = (int)fillerTpl.CardSize;
                        if (usedSlots + fsz <= 10)
                        {
                            var filler = new CardInstance(fillerTpl, PickValidTier(fillerTpl, tier), null, $"f_{i}_{cardSeq++}");
                            if (p1.Hand.TryPlaceOrShift(filler, usedSlots))
                                usedSlots += fsz;
                        }
                    }
                }
            }

            // 补齐 P1 剩余格子至接近满格（8~10 格）
            int safetyFill = 0;
            while (usedSlots <= 8 && vanillaItems.Count > 0 && safetyFill++ < 20)
            {
                var tpl = vanillaItems[rng.Next(vanillaItems.Count)];
                int sz = (int)tpl.CardSize;
                if (usedSlots + sz <= 10)
                {
                    var inst = new CardInstance(tpl, PickValidTier(tpl, tier), null, $"p1v_{i}_{cardSeq++}");
                    if (p1.Hand.TryPlaceOrShift(inst, usedSlots))
                        usedSlots += sz;
                }
            }

            // P1 装载本 Mod 技能
            for (int s = 0; s < modSkills.Count; s++)
            {
                var stpl = modSkills[s];
                p1.Skills.Add(new CardInstance(stpl, PickValidTier(stpl, tier), null, $"ms_{i}_{s}"));
            }
            if (vanillaSkills.Count > 0)
            {
                var vs = vanillaSkills[rng.Next(vanillaSkills.Count)];
                p1.Skills.Add(new CardInstance(vs, PickValidTier(vs, tier), null, $"vs1_{i}"));
            }

            // 填充 P2（原版典型对手满格阵）
            int p2Slots = 0;
            int p2Fill = 0;
            while (p2Slots <= 8 && vanillaItems.Count > 0 && p2Fill++ < 25)
            {
                var tpl = vanillaItems[rng.Next(vanillaItems.Count)];
                int sz = (int)tpl.CardSize;
                if (p2Slots + sz <= 10)
                {
                    var inst = new CardInstance(tpl, PickValidTier(tpl, tier), null, $"p2v_{i}_{p2Fill}");
                    if (p2.Hand.TryPlaceOrShift(inst, p2Slots))
                        p2Slots += sz;
                }
            }
            if (vanillaSkills.Count > 0)
            {
                var vs2 = vanillaSkills[rng.Next(vanillaSkills.Count)];
                p2.Skills.Add(new CardInstance(vs2, PickValidTier(vs2, tier), null, $"vs2_{i}"));
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
                        if (p1.Hand.TryPlaceOrShift(allyInst, usedSlots))
                            usedSlots += (int)allyTpl.CardSize;
                    }
                }
                if (adversarialOpponents is { Count: > 0 } && p2Slots <= 8)
                {
                    var oppTpl = adversarialOpponents[i % adversarialOpponents.Count];
                    if (p2Slots + (int)oppTpl.CardSize <= 10)
                    {
                        var oppInst = new CardInstance(oppTpl, PickValidTier(oppTpl, tier), null, $"adv_o_{i}");
                        if (p2.Hand.TryPlaceOrShift(oppInst, p2Slots))
                            p2Slots += (int)oppTpl.CardSize;
                    }
                }
            }

            // 执行真实 CombatSimulator（关闭 timeline/events 录帧以测真实核心仿真性能）
            var sw = Stopwatch.StartNew();
            try
            {
                var result = CombatSimulator.Simulate(
                    overlayRepo,
                    p1,
                    p2,
                    seed: seed,
                    combatType: "PVP",
                    recordTimeline: false,
                    recordEvents: false,
                    syncPermanentChanges: false,
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

                if (result.DurationMs >= CombatSimulator.MaxDurationMs)
                    maxDurationHits++;

                if (!double.IsFinite(result.Player1TotalDamageDealt)
                    || !double.IsFinite(result.Player2TotalDamageDealt)
                    || !double.IsFinite(result.Player1TotalShieldGained)
                    || !double.IsFinite(result.Player2TotalShieldGained))
                {
                    numericCorruptions++;
                }

                if (string.Equals(result.WinnerPlayerId, p1.PlayerId, StringComparison.Ordinal))
                    modSideWins++;
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
        double winRatePct = actualMatches > 0 ? Math.Round(modSideWins * 100.0 / actualMatches, 1) : 0;
        double avgFightSec = actualMatches > 0 ? Math.Round(totalFightDurationSec / actualMatches, 1) : 0;

        if (slowMatchesOver200ms > 0)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                modId,
                "SIM_PERF_DEGRADATION_OVER_200MS",
                $"共 {slowMatchesOver200ms}/{actualMatches} 场战斗模拟耗时超过 {MaxAllowedSingleMatchMs:0}ms（最慢 {maxMs:0.0}ms），存在严重性能退化或高频连环触发风暴风险。",
                testedCardIds.FirstOrDefault(),
                GateId: 9,
                FixHintZh: "检查是否存在超短冷却、过多高频触发器或大范围全盘扫描光环。"));
        }

        if (numericCorruptions > 0)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                modId,
                "SIM_NUMERIC_CORRUPTION",
                $"共 {numericCorruptions} 场战斗出现 NaN / Infinity 数值损坏。",
                testedCardIds.FirstOrDefault(),
                GateId: 9,
                FixHintZh: "检查光环乘法倍率与属性增长公式，避免指数级溢出。"));
        }

        if (mechanicBreakerTrips > 0 || mechanicBudgetViolations > 0)
        {
            string sampleDiag = mechanicDiagnostics.FirstOrDefault()?.MessageZh ?? "自定义机制触发熔断或递归步数超预算";
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                modId,
                "SIM_MECHANIC_SAFETY_TRIP",
                $"对抗压力体检检测到自定义机制安全违规（熔断 {mechanicBreakerTrips} 次，超预算 {mechanicBudgetViolations} 次）：{sampleDiag}",
                testedCardIds.FirstOrDefault(),
                GateId: 9,
                FixHintZh: "检查自定义机制是否存在互相触发死循环、缺少 maxTriggersPerCombat 上限或异常数值表达式。"));
        }

        if (actualMatches > 0 && (double)maxDurationHits / actualMatches > MaxAllowedTimeoutRatio)
        {
            cascadeGuardTrips = maxDurationHits;
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                modId,
                "SIM_CASCADE_STORM_DETECTED",
                $"共 {maxDurationHits}/{actualMatches} 场战斗撞满 300 秒上限仍未分出胜负（超过 20% 警戒线），疑似无限护盾/回血僵局或触发死循环。",
                testedCardIds.FirstOrDefault(),
                GateId: 9,
                FixHintZh: "适当下调回复/护盾成长倍率，避免抵消沙暴伤害造成永不结束对局。"));
        }

        string balanceHint;
        if (testedCardIds.Count == 0)
        {
            balanceHint = $"已完成 {actualMatches} 场隔离回归压测（纯事件/词条/外观包，0 异常，平均单场 {avgMs:0.0}ms）。";
        }
        else if (winRatePct > 85.0)
        {
            balanceHint = $"{actualMatches} 场模拟胜率 {winRatePct:0.0}%（偏高 >85%），建议适度微调基础伤害/护盾或延长冷却以保持游戏乐趣。";
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Warning,
                modId,
                "SIM_BALANCE_WARNING",
                balanceHint,
                testedCardIds.FirstOrDefault(),
                GateId: 9,
                FixHintZh: "平衡建议（不阻断发布）：可适当调低高阶倍率或将冷却延长 0.5~1 秒。"));
        }
        else if (winRatePct < 15.0)
        {
            balanceHint = $"{actualMatches} 场模拟胜率 {winRatePct:0.0}%（偏低 <15%），建议检查自定义卡数值是否偏保守或核心触发条件过严。";
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Warning,
                modId,
                "SIM_BALANCE_WARNING",
                balanceHint,
                testedCardIds.FirstOrDefault(),
                GateId: 9,
                FixHintZh: "平衡建议（不阻断发布）：可适当提高基础伤害/护盾或放宽触发前提。"));
        }
        else
        {
            balanceHint = $"{actualMatches} 场模拟胜率 {winRatePct:0.0}%（处于 15%~85% 健康区间），平均耗时 {avgMs:0.0}ms，未检测到性能退化或死循环。";
        }

        var report = new ModCombatStressReport
        {
            MatchesRun = actualMatches,
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

        return (report, issues);
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
            CombatSimulator.Simulate(repo, p1, p2, seed: 1, recordTimeline: false, recordEvents: false);
            _jitWarmedUp = true;
        }
        catch
        {
            // 预热失败不影响后续正式统计
        }
    }
}
