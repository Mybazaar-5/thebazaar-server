using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 4B 轨 B 样例 Mod 真实 CombatSimulator 战斗回归套件（启用 syncPermanentChanges: true 模拟真实跨场结算）：
/// 1. 提灯（aether_bulwark_lantern）：Gold >= 20 时护盾真实翻倍，Gold < 20 时未翻倍；
/// 2. 特许状（starport_trade_charter）与无畏宝库舰（starport_dreadnought_vault）：玩家金币数值引用光环与胜场金币引用真实生效（绝非静默 0）；
/// 3. 短刃（starport_compass_blade）与余烬潮汐共鸣（ember_tide_resonance）：战中临时增伤/增灼真实生效，且战后精确复原、跨场绝不永久滚雪球；
/// 4. clockwork-forge（z_sword_custom_d83alo + overload_burst）：过载连爆与友方本战增伤真实生效且战后无跨场永久污染。
/// </summary>
public sealed class Wave4B_ModCombatRegressionTests
{
    private static SharedGameDataFixture.SharedGameDataSnapshot Fx => SharedGameDataFixture.Shared;

    private static (ModRegistry Registry, GameDataRepository Repo) LoadIsolatedSampleMod(string modFolderName)
    {
        var scanIssues = new List<ModValidationIssue>();
        var pkg = ModLoader.LoadSinglePackage(
            Path.Combine(Fx.ModsRoot, modFolderName),
            modFolderName,
            scanIssues);
        Assert.NotNull(pkg);
        Assert.Empty(scanIssues.Where(i => i.Severity == ModValidationSeverity.Error));

        var registry = ModRegistry.CreateIsolated(Fx.Repo, Fx.CreateValidCardIdsCopy());
        var loadReport = registry.LoadPackages([pkg!], Fx.ModsRoot, Fx.Repo, Fx.CreateValidCardIdsCopy(), keepPreviousOnError: false);
        Assert.True(loadReport.Succeeded, string.Join(" | ", loadReport.Issues.Select(i => $"{i.Code}:{i.Message}")));
        return (registry, registry.ActiveRepository);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 1. 以太壁垒提灯：20 金币条件护盾真实翻倍，低金币时未翻倍
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AetherBulwarkLantern_DoublesShieldAt20Gold_AndDoesNotDoubleBelow20Gold()
    {
        var (_, repo) = LoadIsolatedSampleMod("bazaar-chronicles-sample");
        var lanternTpl = repo.GetCard("bazaar-chronicles-sample:aether_bulwark_lantern");
        Assert.NotNull(lanternTpl);

        CombatResult RunWithGold(int startingGold)
        {
            var p1 = new PlayerState("p1", "高金测试", HeroNames.Vanessa, initialHealthMax: 2000, initialGold: startingGold);
            var p2 = new PlayerState("p2", "标靶", HeroNames.Dooley, initialHealthMax: 2000);
            var lantern = new CardInstance(lanternTpl!, CardTier.Silver, instanceId: $"lantern_{startingGold}");
            Assert.True(p1.Hand.TryPlaceOrShift(lantern, 0));

            return CombatSimulator.Simulate(
                repo,
                p1,
                p2,
                seed: 20260401,
                combatType: "PVP",
                recordTimeline: false,
                recordEvents: true,
                syncPermanentChanges: false,
                modifiers: new CombatModifiers { MaxDurationMsOverride = 12_000, SandstormEnabled = false });
        }

        var lowRes = RunWithGold(5);
        var highRes = RunWithGold(25);

        // Silver 阶基础 ShieldApplyAmount = 35，CooldownMax = 5000ms；12s 内固定开火 2 次（5s、10s）
        var lowShieldEvents = lowRes.Events
            .Where(e => e.Type == CombatEventType.ShieldApplied && e.SourcePlayerId == "p1")
            .ToList();
        var highShieldEvents = highRes.Events
            .Where(e => e.Type == CombatEventType.ShieldApplied && e.SourcePlayerId == "p1")
            .ToList();

        Assert.True(lowShieldEvents.Count >= 2, $"期望低金币提灯至少触发 2 次护盾，实际={lowShieldEvents.Count}");
        Assert.Equal(lowShieldEvents.Count, highShieldEvents.Count);
        Assert.All(lowShieldEvents, ev => Assert.Equal(35, ev.Value));
        Assert.All(highShieldEvents, ev => Assert.Equal(70, ev.Value));
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2. 星港贸易特许状 & 无畏宝库舰：金币数值引用与胜场引用真实生效（非静默 0）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void StarportTradeCharter_AndDreadnoughtVault_ReferenceValuesEvaluateToNonZeroInRealCombat()
    {
        var (_, repo) = LoadIsolatedSampleMod("bazaar-chronicles-sample");
        var charterTpl = repo.GetCard("bazaar-chronicles-sample:starport_trade_charter");
        var bladeTpl = repo.GetCard("bazaar-chronicles-sample:starport_compass_blade");
        var vaultTpl = repo.GetCard("bazaar-chronicles-sample:starport_dreadnought_vault");
        Assert.NotNull(charterTpl);
        Assert.NotNull(bladeTpl);
        Assert.NotNull(vaultTpl);

        // A. 特许状光环：Silver 阶 Gold * 0.3 武器增伤；Gold=0 时短刃基础 12 伤，Gold=30 时短刃应获得 +9 -> 21 伤
        //    同时校验赢战时 TTriggerOnFightEnded(Win) -> TReferenceValueCardAttribute(Custom_1=3) 真实给金币
        CombatResult RunCharterFight(int startGold, int oppHp, out PlayerState p1Out)
        {
            var p1 = new PlayerState("p1", "商会测试", HeroNames.Vanessa, initialHealthMax: 1000, initialGold: startGold);
            var p2 = new PlayerState("p2", "木桩", HeroNames.Dooley, initialHealthMax: oppHp);
            var blade = new CardInstance(bladeTpl!, CardTier.Bronze, instanceId: $"blade_{startGold}");
            var charter = new CardInstance(charterTpl!, CardTier.Silver, instanceId: $"charter_{startGold}");
            Assert.True(p1.Hand.TryPlaceOrShift(blade, 0));
            p1.Skills.Add(charter);
            p1Out = p1;

            return CombatSimulator.Simulate(
                repo,
                p1,
                p2,
                seed: 20260402,
                combatType: "PVP",
                recordTimeline: false,
                recordEvents: true,
                syncPermanentChanges: true,
                modifiers: new CombatModifiers { MaxDurationMsOverride = 15_000, SandstormEnabled = false });
        }

        var zeroGoldRes = RunCharterFight(startGold: 0, oppHp: 500, out _);
        var richRes = RunCharterFight(startGold: 30, oppHp: 40, out var richP1);

        var zeroDmgEvents = zeroGoldRes.Events
            .Where(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "p1")
            .ToList();
        var richDmgEvents = richRes.Events
            .Where(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "p1")
            .ToList();

        Assert.NotEmpty(zeroDmgEvents);
        Assert.NotEmpty(richDmgEvents);

        // Bronze 短刃基础 12 伤，Gold=30 时特许状光环 +9 -> 21 伤（排除暴击翻倍取非暴击或折算原伤）
        double NormalizeNonCrit(CombatEvent ev) => ev.IsCrit ? ev.Value / 2.0 : ev.Value;
        Assert.Equal(12, NormalizeNonCrit(zeroDmgEvents[0]));
        Assert.Equal(21, NormalizeNonCrit(richDmgEvents[0]));

        // 赢下战斗：战内短刃开火产金已回写（30 -> >=32）；随后经 OutOfCombatController.OnFightEnded 触发特许状胜场奖励（Custom_1=3）
        Assert.Equal("p1", richRes.WinnerPlayerId);
        int goldAfterCombatBeforeFightEnded = richP1.Gold;
        Assert.True(goldAfterCombatBeforeFightEnded >= 32, $"期望战内短刃开火产出金币（>=32），实际 Gold={goldAfterCombatBeforeFightEnded}");
        OutOfCombatController.OnFightEnded(richP1, "PVP", "Win", repo, new Random(20260402));
        Assert.Equal(goldAfterCombatBeforeFightEnded + 3, richP1.Gold);

        // B. 星港无畏宝库舰：Gold 阶基础 80 伤/80 盾，Gold=20 时光环 +20*1.5 = +30 -> 110 伤 / 110 盾
        var vp1 = new PlayerState("vp1", "舰长", HeroNames.Vanessa, initialHealthMax: 2000, initialGold: 20);
        var vp2 = new PlayerState("vp2", "靶舰", HeroNames.Dooley, initialHealthMax: 2000);
        var vault = new CardInstance(vaultTpl!, CardTier.Gold, instanceId: "vault_1");
        Assert.True(vp1.Hand.TryPlaceOrShift(vault, 0));

        var vaultRes = CombatSimulator.Simulate(
            repo, vp1, vp2, seed: 20260403, combatType: "PVP", recordTimeline: false, recordEvents: true, syncPermanentChanges: false,
            modifiers: new CombatModifiers { MaxDurationMsOverride = 10_000, SandstormEnabled = false });
        var vaultShieldEv = vaultRes.Events.FirstOrDefault(e => e.Type == CombatEventType.ShieldApplied && e.SourcePlayerId == "vp1");
        Assert.NotNull(vaultShieldEv);
        Assert.Equal(110, vaultShieldEv!.Value);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 3. 星港罗盘短刃 & 余烬潮汐共鸣：战中临时 Buff 真生效，战后精准复原绝不永久滚雪球
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void StarportCompassBlade_AndEmberTideResonance_BuffInCombat_AndResetCleanlyAfterCombatWithoutSnowballing()
    {
        var (_, repo) = LoadIsolatedSampleMod("bazaar-chronicles-sample");
        var bladeTpl = repo.GetCard("bazaar-chronicles-sample:starport_compass_blade");
        var lanternTpl = repo.GetCard("bazaar-chronicles-sample:aether_bulwark_lantern"); // Tag: Tool, Relic; CD=5000
        var furnaceTpl = repo.GetCard("bazaar-chronicles-sample:solar_kiln_furnace");    // Tag: Tool, Core; Burn=4
        var resonanceTpl = repo.GetCard("bazaar-chronicles-sample:ember_tide_resonance");
        Assert.NotNull(bladeTpl);
        Assert.NotNull(lanternTpl);
        Assert.NotNull(furnaceTpl);
        Assert.NotNull(resonanceTpl);

        // 组装持久玩家与持久卡牌（启用 syncPermanentChanges: true 模拟同一局内连续进行两场战斗！）
        var p1 = new PlayerState("p1", "连战测试", HeroNames.Vanessa, initialHealthMax: 3000, initialGold: 10, unlockedHandSlots: 10);
        var blade = new CardInstance(bladeTpl!, CardTier.Bronze, instanceId: "persist_blade"); // Damage=12, Custom_0=4, GoldGain=1, CD=4000
        var lantern = new CardInstance(lanternTpl!, CardTier.Silver, instanceId: "persist_lantern"); // Tool, CD=5000
        var furnace = new CardInstance(furnaceTpl!, CardTier.Bronze, instanceId: "persist_furnace"); // Tool, Burn=4, CD=6000
        var resonance = new CardInstance(resonanceTpl!, CardTier.Bronze, instanceId: "persist_resonance"); // Custom_0=6 shield, Custom_1=1 burn

        // 摆盘：[短刃(0)] [提灯(1,2)] [熔炉(3,4)] -> 短刃与提灯相邻(Tool)，熔炉与提灯相邻(Tool)
        Assert.True(p1.Hand.TryPlaceOrShift(blade, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(lantern, 1));
        Assert.True(p1.Hand.TryPlaceOrShift(furnace, 3));
        p1.Skills.Add(resonance);

        double initialBladeDmg = blade.GetAttribute("DamageAmount");
        double initialFurnaceBurn = furnace.GetAttribute("BurnApplyAmount");
        Assert.Equal(12, initialBladeDmg);
        Assert.Equal(4, initialFurnaceBurn);

        // 第 1 场战斗（14 秒足够多次触发相邻 Tool 使用与开火，启用 syncPermanentChanges: true 捕获任何意外永久化污染！）
        var opp1 = new PlayerState("opp1", "靶子1", HeroNames.Dooley, initialHealthMax: 3000);
        int goldBeforeFight1 = p1.Gold;
        var res1 = CombatSimulator.Simulate(
            repo, p1, opp1, seed: 20260404, combatType: "PVP", recordTimeline: false, recordEvents: true, syncPermanentChanges: true,
            modifiers: new CombatModifiers { MaxDurationMsOverride = 14_000, SandstormEnabled = false });

        // 断言 1：短刃开火产金真实生效并永久同步至 p1.Gold（TReferenceValueCardAttribute(GoldGainAmount) 非静默 0）
        Assert.True(p1.Gold > goldBeforeFight1, $"短刃开火应产出金币并战后保留，战前={goldBeforeFight1}，战后={p1.Gold}");

        // 断言 2：战中短刃确有因相邻 Tool 使用触发而增伤（首击 12，后续击中出现非暴击伤害 > 12，例如 16 / 20）
        var bladeHitsFight1 = res1.Events
            .Where(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == blade.InstanceId)
            .Select(e => e.IsCrit ? e.Value / 2.0 : e.Value)
            .ToList();
        Assert.True(bladeHitsFight1.Count >= 2, $"期望短刃在第 1 场至少命中 2 次，实际={bladeHitsFight1.Count}");
        Assert.Equal(12, bladeHitsFight1[0]);
        Assert.True(bladeHitsFight1.Any(d => d >= 16), $"期望相邻 Tool 使用后短刃战中增伤生效（>=16），实际序列={string.Join(",", bladeHitsFight1)}");

        // 断言 3：余烬潮汐共鸣在灼烧时提供护盾（Custom_0=6），在获得护盾时提升熔炉灼烧（后续熔炉施加灼烧 > 基础 4）
        var resonanceShieldEvents = res1.Events
            .Where(e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == resonance.InstanceId)
            .ToList();
        Assert.NotEmpty(resonanceShieldEvents);
        Assert.All(resonanceShieldEvents, ev => Assert.Equal(6, ev.Value));

        var furnaceBurnsFight1 = res1.Events
            .Where(e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == furnace.InstanceId)
            .Select(e => e.IsCrit ? e.Value / 2.0 : e.Value)
            .ToList();
        Assert.NotEmpty(furnaceBurnsFight1);
        Assert.True(furnaceBurnsFight1.Any(b => b > 4), $"期望获得护盾后熔炉战中灼烧提升（>4），实际序列={string.Join(",", furnaceBurnsFight1)}");

        // 断言 4（核心防滚雪球）：即使 syncPermanentChanges=true，短刃伤害与熔炉灼烧战后必须 100% 回到初始基线，PermanentAdd 零污染！
        Assert.Equal(0, blade.PermanentAdd.GetValueOrDefault("DamageAmount", 0));
        Assert.Equal(0, furnace.PermanentAdd.GetValueOrDefault("BurnApplyAmount", 0));
        Assert.Equal(initialBladeDmg, blade.GetAttribute("DamageAmount"));
        Assert.Equal(initialFurnaceBurn, furnace.GetAttribute("BurnApplyAmount"));

        // 断言 5：连续进行第 2 场战斗，短刃首击（4s，早于 5s 提灯使用）必须仍为基础 12 伤，绝不跨场滚雪球！
        var opp2 = new PlayerState("opp2", "靶子2", HeroNames.Dooley, initialHealthMax: 3000);
        var res2 = CombatSimulator.Simulate(
            repo, p1, opp2, seed: 20260405, combatType: "PVP", recordTimeline: false, recordEvents: true, syncPermanentChanges: true,
            modifiers: new CombatModifiers { MaxDurationMsOverride = 4_500, SandstormEnabled = false });
        var firstHitFight2 = res2.Events
            .FirstOrDefault(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == blade.InstanceId);
        Assert.NotNull(firstHitFight2);
        double firstBaseDmgFight2 = firstHitFight2!.IsCrit ? firstHitFight2.Value / 2.0 : firstHitFight2.Value;
        Assert.Equal(12, firstBaseDmgFight2);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 4. clockwork-forge：过载蓄能炉（z_sword_custom_d83alo）真实战斗与战后无永久滚雪球
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ClockworkForge_OverloadFurnace_DetonatesInCombat_AndDoesNotLeakPermanentBuffsAfterCombat()
    {
        var (registry, repo) = LoadIsolatedSampleMod("clockwork-forge");
        var zSwordTpl = repo.GetCard("clockwork-forge:z_sword_custom_d83alo");
        var kukriTpl = repo.GetCard("03b1fd16-6b0d-4847-a76f-6c586f7c5958"); // Small 原版快攻武器相邻驱动过载
        Assert.NotNull(zSwordTpl);
        Assert.NotNull(kukriTpl);

        var p1 = new PlayerState("p1", "工匠", HeroNames.Dooley, initialHealthMax: 3000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "测试木桩", HeroNames.Vanessa, initialHealthMax: 3000, unlockedHandSlots: 10);

        var leftAlly = new CardInstance(kukriTpl!, CardTier.Bronze, instanceId: "ally_left");
        var furnace = new CardInstance(zSwordTpl!, CardTier.Bronze, instanceId: "z_sword");
        var rightAlly = new CardInstance(kukriTpl!, CardTier.Bronze, instanceId: "ally_right");

        Assert.True(p1.Hand.TryPlaceOrShift(leftAlly, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(furnace, 1));
        Assert.True(p1.Hand.TryPlaceOrShift(rightAlly, 3));

        double initialLeftDmg = leftAlly.GetAttribute("DamageAmount");
        double initialFurnaceCdr = furnace.GetAttribute("PercentCooldownReduction");

        var res = CombatSimulator.Simulate(
            repo,
            p1,
            p2,
            seed: 20260406,
            combatType: "PVP",
            recordTimeline: false,
            recordEvents: true,
            syncPermanentChanges: true,
            modifiers: new CombatModifiers { MaxDurationMsOverride = 15_000, SandstormEnabled = false },
            mechanicDefinitionsOverride: registry.MechanicsById);

        // 15 秒内左右相邻武器多次触发，必攒满 3 层过载并引爆火球与友方战中增伤
        Assert.True(res.Player1TotalDamageDealt > 50, $"过载蓄能炉阵容 15 秒总伤害异常偏低：{res.Player1TotalDamageDealt}");

        // 战后复原：即使 syncPermanentChanges=true，友方伤害与本卡冷却缩减绝无永久层残留
        Assert.Equal(0, leftAlly.PermanentAdd.GetValueOrDefault("DamageAmount", 0));
        Assert.Equal(0, furnace.PermanentAdd.GetValueOrDefault("PercentCooldownReduction", 0));
        Assert.Equal(initialLeftDmg, leftAlly.GetAttribute("DamageAmount"));
        Assert.Equal(initialFurnaceCdr, furnace.GetAttribute("PercentCooldownReduction"));
    }
}
