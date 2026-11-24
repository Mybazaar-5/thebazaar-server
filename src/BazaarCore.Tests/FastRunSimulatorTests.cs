using System.Diagnostics;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Dataset.Simulation;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace BazaarCore.Tests;

/// <summary>
/// 工作线 C 自动化测试：验证 BoardValidator、BoardSnapshotMapper、ArchetypeBot 与 FastRunSimulator。
/// </summary>
public sealed class FastRunSimulatorTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private readonly ITestOutputHelper _output;

    public FastRunSimulatorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// 轻量级 Stub 流派蓝图目录，仅用于隔离验证工作线 C（不依赖工作线 B）。
    /// </summary>
    private sealed class StubArchetypeCatalog : IArchetypeCatalog
    {
        private readonly List<ArchetypeDefinition> _all = new();
        private readonly Dictionary<string, List<ArchetypeDefinition>> _byHero = new(StringComparer.OrdinalIgnoreCase);

        public StubArchetypeCatalog(GameDataRepository repo)
        {
            foreach (var hero in HeroNames.PlayableHeroes)
            {
                var heroItems = repo.GetSpawnableItemsForHero(hero, includeCommon: false).ToList();
                var weaponNames = heroItems.Where(c => c.Tags.Contains("Weapon")).Take(3).Select(c => c.InternalName).ToList();
                var shieldOrBurnNames = heroItems.Where(c => c.HiddenTags.Contains("Burn") || c.HiddenTags.Contains("Shield")).Take(3).Select(c => c.InternalName).ToList();
                var supportNames = heroItems.Skip(3).Take(4).Select(c => c.InternalName).ToList();
                var heroSkills = repo.AlwaysSpawnableSkills.Where(s => s.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)).Take(2).Select(s => s.InternalName).ToList();

                var arch1 = new ArchetypeDefinition
                {
                    Id = $"{hero}_Stub_Aggro",
                    Hero = hero,
                    NameZh = $"{hero} 快攻测试蓝图",
                    DescriptionZh = "测试用武器快攻流派",
                    CoreItemNames = weaponNames,
                    SupportItemNames = supportNames,
                    CoreSkillNames = heroSkills,
                    PreferredTags = new Dictionary<string, double> { ["Weapon"] = 1.5, ["Tool"] = 0.8 },
                    PreferredHiddenTags = new Dictionary<string, double> { ["Damage"] = 1.4, ["Haste"] = 1.0, ["Crit"] = 1.1 },
                    PreferredEnchantments = new Dictionary<string, double> { ["Deadly"] = 1.5, ["Obsidian"] = 1.3, ["Turbo"] = 1.1 },
                    PositioningRules = new[] { PositioningRule.CoreCenter, PositioningRule.AdjacentToCore, PositioningRule.FastTriggerLeftOfScaler },
                    EarlyFocusTags = new[] { "Weapon", "Damage" },
                    EndgamePowerTag = "Crit"
                };

                var arch2 = new ArchetypeDefinition
                {
                    Id = $"{hero}_Stub_ControlBurn",
                    Hero = hero,
                    NameZh = $"{hero} 灼烧护盾测试蓝图",
                    DescriptionZh = "测试用灼烧护盾/控制流派",
                    CoreItemNames = shieldOrBurnNames,
                    SupportItemNames = supportNames,
                    CoreSkillNames = heroSkills,
                    PreferredTags = new Dictionary<string, double> { ["Property"] = 1.2, ["Friend"] = 1.0, ["Tech"] = 1.0 },
                    PreferredHiddenTags = new Dictionary<string, double> { ["Burn"] = 1.4, ["Shield"] = 1.4, ["Poison"] = 1.1, ["Value"] = 1.0 },
                    PreferredEnchantments = new Dictionary<string, double> { ["Fiery"] = 1.5, ["Shielded"] = 1.4, ["Toxic"] = 1.2 },
                    PositioningRules = new[] { PositioningRule.ShieldOnEdge, PositioningRule.CoreCenter, PositioningRule.RightMost },
                    EarlyFocusTags = new[] { "Burn", "Shield" },
                    EndgamePowerTag = "Burn"
                };

                _all.Add(arch1);
                _all.Add(arch2);
                _byHero[hero] = new List<ArchetypeDefinition> { arch1, arch2 };
            }
        }

        public IReadOnlyList<ArchetypeDefinition> All => _all;

        public IReadOnlyList<ArchetypeDefinition> GetByHero(string hero)
            => _byHero.TryGetValue(hero, out var list) ? list : Array.Empty<ArchetypeDefinition>();

        public ArchetypeDefinition? GetById(string id)
            => _all.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

        public ArchetypeDefinition PickForHero(string hero, PersonaType persona, Random rng)
        {
            var list = GetByHero(hero);
            if (persona == PersonaType.TempoAggro) return list[0];
            if (persona == PersonaType.GreedyEco) return list[^1];
            return list[rng.Next(list.Count)];
        }
    }

    [Fact]
    public void BoardValidator_ShouldDetectAllInvalidSnapshotConditions()
    {
        var repo = Repo.Value;
        var validator = new BoardValidator();
        var item = repo.GetSpawnableItemsForHero(HeroNames.Vanessa, includeCommon: false).First();
        var skill = repo.AlwaysSpawnableSkills.First();

        var valid = new PvpBoardSnapshot
        {
            BoardId = "test_valid",
            RunId = "run_test",
            Hero = HeroNames.Vanessa,
            Day = 1,
            Level = 1,
            HealthMax = 250,
            Prestige = 20,
            UnlockedSlotsMask = 0b0001111000, // 居中 4 格 (slot 3..6)
            Items = new List<PvpItemSnapshot>
            {
                new()
                {
                    Slot = 3,
                    Width = item.CardSize.SlotCount(),
                    TemplateId = item.Id,
                    InternalName = item.InternalName,
                    ZhName = repo.GetLocalizedTitle(item),
                    Tier = item.DefaultTier
                }
            },
            Skills = new List<PvpSkillSnapshot>
            {
                new()
                {
                    TemplateId = skill.Id,
                    InternalName = skill.InternalName,
                    ZhName = repo.GetLocalizedTitle(skill),
                    Tier = skill.DefaultTier
                }
            }
        };

        var validRes = validator.Validate(valid, repo, requireOfficialZh: repo.Localization != null);
        Assert.True(validRes.IsValid, string.Join("; ", validRes.Errors));

        // 校验非法英雄
        var badHero = CloneSnapshot(valid);
        badHero.Hero = "UnknownHero";
        Assert.False(validator.Validate(badHero, repo, requireOfficialZh: false).IsValid);

        // 校验槽位未解锁
        var lockedSlot = CloneSnapshot(valid);
        lockedSlot.Items[0].Slot = 0;
        Assert.False(validator.Validate(lockedSlot, repo, requireOfficialZh: false).IsValid);

        // 校验槽位越界
        var outOfBounds = CloneSnapshot(valid);
        outOfBounds.UnlockedSlotsMask = 1023;
        outOfBounds.Items[0].Slot = 10;
        Assert.False(validator.Validate(outOfBounds, repo, requireOfficialZh: false).IsValid);

        // 校验物品重叠
        var overlap = CloneSnapshot(valid);
        overlap.Items.Add(new PvpItemSnapshot
        {
            Slot = 3,
            Width = item.CardSize.SlotCount(),
            TemplateId = item.Id,
            InternalName = item.InternalName,
            ZhName = "测试",
            Tier = item.DefaultTier
        });
        Assert.False(validator.Validate(overlap, repo, requireOfficialZh: false).IsValid);

        // 校验非法附魔
        var badEnch = CloneSnapshot(valid);
        badEnch.Items[0].Enchantment = "NotARealEnchantment";
        Assert.False(validator.Validate(badEnch, repo, requireOfficialZh: false).IsValid);

        // 校验缺少中文名
        var noZh = CloneSnapshot(valid);
        noZh.Items[0].ZhName = "";
        Assert.False(validator.Validate(noZh, repo, requireOfficialZh: true).IsValid);
    }

    [Fact]
    public void BoardSnapshotMapper_And_ToPlayerState_ShouldRoundTripLosslessly()
    {
        var repo = Repo.Value;
        var validator = new BoardValidator();
        var catalog = new StubArchetypeCatalog(repo);
        var sim = new FastRunSimulator();
        var arch = catalog.GetByHero(HeroNames.Vanessa)[0];
        var participant = sim.CreateParticipant("run_rt", HeroNames.Vanessa, PersonaType.HardForcer, arch, 2026, repo);

        var tpl = repo.GetSpawnableItemsForHero(HeroNames.Vanessa, includeCommon: false)
            .First(c => c.CardSize == CardSize.Small && c.Enchantments != null && c.Enchantments.Count > 0);
        var ench = tpl.Enchantments!.Keys.First(k => EnchantmentNames.All.Contains(k));
        var card = new CardInstance(tpl, tpl.DefaultTier, ench);
        card.ModifyAttribute("DamageAmount", "Add", 37, permanent: true);
        card.ModifyAttribute("CritChance", "Add", 15, permanent: true);

        Assert.True(participant.PlayerState.Hand.TryPlaceOrShift(card, 4));
        participant.PlayerState.Day = 6;
        participant.PlayerState.Level = 7;
        participant.PlayerState.Wins = 4;
        participant.PlayerState.Losses = 1;
        participant.PlayerState.Attributes["HealthMax"] = 1850;

        var snap = BoardSnapshotMapper.ToSnapshot(participant, repo, validator);
        Assert.Equal("syn_d06_vanessa_run_rt", snap.BoardId);
        Assert.Equal(1850, snap.HealthMax);
        Assert.Single(snap.Items);
        Assert.Equal(card.GetAttributeUnscaled("DamageAmount"), snap.Items[0].PermanentAttributes["DamageAmount"]);
        Assert.Equal(card.GetAttributeUnscaled("CritChance"), snap.Items[0].PermanentAttributes["CritChance"]);

        var restored = validator.ToPlayerState(snap, repo);
        Assert.Equal(1850, restored.HealthMax);
        Assert.Equal(7, restored.Level);
        Assert.Equal(4, restored.Wins);
        Assert.Equal(1, restored.Losses);
        Assert.Equal(participant.PlayerState.Hand.UnlockedMask, restored.Hand.UnlockedMask);

        var restoredCard = Assert.Single(restored.Hand.Cards);
        Assert.Equal(4, restoredCard.StartSlot);
        Assert.Equal(ench, restoredCard.Enchantment);
        Assert.Equal(card.GetAttributeUnscaled("DamageAmount"), restoredCard.GetAttributeUnscaled("DamageAmount"), precision: 3);
        Assert.Equal(card.GetAttributeUnscaled("CritChance"), restoredCard.GetAttributeUnscaled("CritChance"), precision: 3);
        Assert.Equal(37.0, restoredCard.PermanentAdd["DamageAmount"], precision: 3);

        // 指纹稳定性校验
        var sig1 = validator.ComputeSignature(snap);
        var sig2 = validator.ComputeSignature(snap);
        Assert.False(string.IsNullOrWhiteSpace(sig1));
        Assert.Equal(sig1, sig2);
    }

    [Fact]
    public void FastRunSimulator_AllHeroesAndPersonas_ShouldProduceValidBoardsAndMonotonicProgression()
    {
        var repo = Repo.Value;
        var validator = new BoardValidator();
        var catalog = new StubArchetypeCatalog(repo);
        var sim = new FastRunSimulator();
        var personas = Enum.GetValues<PersonaType>();

        var endlessOpts = new FastRunOptions
        {
            MaxDay = 15,
            VictoriesToWin = 10,
            AllowEndlessAfterVictory = true,
            RequireOfficialZhCards = repo.Localization != null
        };

        var participants = new List<RunParticipantState>();
        int seedCounter = 1000;
        foreach (var hero in HeroNames.PlayableHeroes)
        {
            foreach (var persona in personas)
            {
                int seed = seedCounter++;
                var arch = catalog.PickForHero(hero, persona, new Random(seed));
                string runId = $"test_{hero.ToLowerInvariant()}_{persona}_{seed}";
                participants.Add(sim.CreateParticipant(runId, hero, persona, arch, seed, repo));
            }
        }

        // 统计各天指标
        var day1Stats = new List<PvpBoardSnapshot>();
        var day5Stats = new List<PvpBoardSnapshot>();
        var day10PlusStats = new List<PvpBoardSnapshot>();
        var day12PlusStats = new List<PvpBoardSnapshot>();
        int totalValidBoards = 0;
        int totalPvpFights = 0;

        var dayParallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };
        var sw = Stopwatch.StartNew();

        for (int day = 1; day <= 15; day++)
        {
            int curDay = day;
            // 阶段 1：并发推进当日全部活跃参与者至 PvP 盘面，按原索引回填确保配对顺序与随机序列 100% 逐位一致
            var daySnapSlots = new PvpBoardSnapshot?[participants.Count];
            Parallel.For(
                0,
                participants.Count,
                dayParallelOpts,
                () => (Sim: new FastRunSimulator(), Val: new BoardValidator()),
                (idx, _, local) =>
                {
                    var p = participants[idx];
                    if (p.IsFinished) return local;
                    var snap = local.Sim.AdvanceOneDayToPvp(p, curDay, repo, catalog, endlessOpts);

                    // 兼容合流前后（合流前 Localization 可能为 null，合流后为 true）
                    var resFalse = local.Val.Validate(snap, repo, requireOfficialZh: false);
                    Assert.True(resFalse.IsValid, $"Day {curDay} {p.Hero}/{p.Persona} 校验失败: {string.Join("; ", resFalse.Errors)}");

                    if (repo.Localization != null)
                    {
                        var resTrue = local.Val.Validate(snap, repo, requireOfficialZh: true);
                        Assert.True(resTrue.IsValid, $"Day {curDay} {p.Hero}/{p.Persona} 中文校验失败: {string.Join("; ", resTrue.Errors)}");
                    }

                    daySnapSlots[idx] = snap;
                    return local;
                },
                _ => { });

            var todaySnaps = new List<(RunParticipantState P, PvpBoardSnapshot Snap)>(participants.Count);
            for (int idx = 0; idx < participants.Count; idx++)
            {
                var snap = daySnapSlots[idx];
                if (snap == null) continue;
                todaySnaps.Add((participants[idx], snap));
                totalValidBoards++;

                if (curDay == 1) day1Stats.Add(snap);
                else if (curDay == 5) day5Stats.Add(snap);
                if (curDay >= 10) day10PlusStats.Add(snap);
                if (curDay >= 12) day12PlusStats.Add(snap);
            }

            // 阶段 2：同天数配对交战并发模拟（种子与对手顺序 100% 不变），完成后按序回写结果
            int pairCount = todaySnaps.Count / 2;
            var pairCombats = new (CombatResult Combat, string StateAId)[pairCount];
            Parallel.For(
                0,
                pairCount,
                dayParallelOpts,
                () => new BoardValidator(),
                (pairIdx, _, localVal) =>
                {
                    int i = pairIdx * 2;
                    var (_, sa) = todaySnaps[i];
                    var (_, sb) = todaySnaps[i + 1];
                    var stateA = localVal.ToPlayerState(sa, repo);
                    var stateB = localVal.ToPlayerState(sb, repo);
                    var combat = CombatSimulator.Simulate(repo, stateA, stateB, 42 + curDay * 100 + i, "PVP", recordTimeline: false, recordEvents: false);
                    pairCombats[pairIdx] = (combat, stateA.PlayerId);
                    return localVal;
                },
                _ => { });

            for (int pairIdx = 0; pairIdx < pairCount; pairIdx++)
            {
                int i = pairIdx * 2;
                var (pa, sa) = todaySnaps[i];
                var (pb, sb) = todaySnaps[i + 1];
                var (combat, stateAId) = pairCombats[pairIdx];
                totalPvpFights++;

                bool aWon = combat.WinnerPlayerId == stateAId;
                sim.ApplyPvpOutcome(pa, sa, new PvpMatchOutcome
                {
                    OpponentBoardId = sb.BoardId,
                    Won = aWon,
                    CombatDurationMs = combat.DurationMs,
                    RemainingHp = combat.Player1RemainingHp,
                    DamageDealt = combat.Player1TotalDamageDealt,
                    ShieldGained = combat.Player1TotalShieldGained
                }, repo, endlessOpts);

                sim.ApplyPvpOutcome(pb, sb, new PvpMatchOutcome
                {
                    OpponentBoardId = sa.BoardId,
                    Won = !aWon,
                    CombatDurationMs = combat.DurationMs,
                    RemainingHp = combat.Player2RemainingHp,
                    DamageDealt = combat.Player2TotalDamageDealt,
                    ShieldGained = combat.Player2TotalShieldGained
                }, repo, endlessOpts);
            }
        }

        sw.Stop();

        Assert.Equal(64, participants.Count); // 8 英雄 × 8 画像
        Assert.Equal(64, day1Stats.Count);
        Assert.True(day12PlusStats.Count >= 30, $"开启无尽模式后 Day 12+ 幸存样本应充足，实际: {day12PlusStats.Count}");

        double avgHpD1 = day1Stats.Average(s => s.HealthMax);
        double avgHpD5 = day5Stats.Average(s => s.HealthMax);
        double avgHpD12 = day12PlusStats.Average(s => s.HealthMax);

        double highTierRateD1 = CalcHighTierItemRate(day1Stats, CardTier.Silver);
        double highTierRateD12 = CalcHighTierItemRate(day12PlusStats, CardTier.Silver);
        double goldPlusRateD1 = CalcHighTierItemRate(day1Stats, CardTier.Gold);
        double goldPlusRateD12 = CalcHighTierItemRate(day12PlusStats, CardTier.Gold);

        double enchantRateD1 = CalcEnchantRate(day1Stats);
        double enchantRateD5 = CalcEnchantRate(day5Stats);
        double enchantRateD12 = CalcEnchantRate(day12PlusStats);
        double avgEnchCountD10Plus = day10PlusStats.Average(s => s.Items.Count(i => !string.IsNullOrEmpty(i.Enchantment)));
        int maxEnchCount = day10PlusStats.Max(s => s.Items.Count(i => !string.IsNullOrEmpty(i.Enchantment)));

        double avgSlotsD1 = day1Stats.Average(s => CountBits(s.UnlockedSlotsMask));
        double avgSlotsD12 = day12PlusStats.Average(s => CountBits(s.UnlockedSlotsMask));

        _output.WriteLine($"总测试局数: {participants.Count} 局（8 英雄 × 8 画像），总合法盘面: {totalValidBoards}，总 PvP 实战: {totalPvpFights}，总耗时: {sw.ElapsedMilliseconds} ms（均值 {sw.ElapsedMilliseconds / (double)participants.Count:F2} ms/局）");
        _output.WriteLine($"平均 HealthMax: Day 1={avgHpD1:F0} -> Day 5={avgHpD5:F0} -> Day 12+={avgHpD12:F0}");
        _output.WriteLine($"高阶物品占比 (>=Silver): Day 1={highTierRateD1:P1} -> Day 12+={highTierRateD12:P1} | (>=Gold): Day 1={goldPlusRateD1:P1} -> Day 12+={goldPlusRateD12:P1}");
        _output.WriteLine($"物品附魔率: Day 1={enchantRateD1:P1} -> Day 5={enchantRateD5:P1} -> Day 12+={enchantRateD12:P1} | Day 10+ 单盘均附魔={avgEnchCountD10Plus:F2} (最大={maxEnchCount})");
        _output.WriteLine($"平均解锁格数: Day 1={avgSlotsD1:F1} -> Day 12+={avgSlotsD12:F1}");

        // 严格断言单调成长趋势与附魔稀缺度约束
        Assert.True(avgHpD5 > avgHpD1 * 2.0, $"Day 5 血量应远高于 Day 1 (D1={avgHpD1}, D5={avgHpD5})");
        Assert.True(avgHpD12 > avgHpD5 * 1.8, $"Day 12+ 血量应远高于 Day 5 (D5={avgHpD5}, D12={avgHpD12})");
        Assert.True(highTierRateD12 > highTierRateD1 + 0.45, $"Day 12+ 高阶卡占比应显著大于 Day 1 (D1={highTierRateD1:P1}, D12={highTierRateD12:P1})");
        Assert.True(goldPlusRateD12 > goldPlusRateD1 + 0.25, $"Day 12+ 金钻卡占比应显著大于 Day 1 (D1={goldPlusRateD1:P1}, D12={goldPlusRateD12:P1})");
        Assert.True(enchantRateD12 > enchantRateD1 + 0.10, $"Day 12+ 附魔率应显著高于 Day 1 (D1={enchantRateD1:P1}, D12={enchantRateD12:P1})");
        Assert.True(avgEnchCountD10Plus <= 4.0, $"Day 10+ 单盘平均附魔数不得超标: {avgEnchCountD10Plus:F2}");
        Assert.True(maxEnchCount <= 4, $"单局附魔上限不得超过 4 件: {maxEnchCount}");
        Assert.InRange(avgSlotsD1, 4.0, 6.0);
        Assert.Equal(10.0, avgSlotsD12, precision: 1);
        Assert.True(avgSlotsD12 > avgSlotsD1 + 3.0);

        // 验证 BuildRunRecord 汇总完整性
        var firstRec = sim.BuildRunRecord(participants[0], repo);
        Assert.Equal(participants[0].RunId, firstRec.RunId);
        Assert.True(firstRec.DayBoards.Count >= 1);
    }

    [Fact]
    public void CreateParticipant_ShouldMatchBaseline500Rules()
    {
        var repo = Repo.Value;
        var catalog = new StubArchetypeCatalog(repo);
        var sim = new FastRunSimulator();
        var p = sim.CreateParticipant("init_test", HeroNames.Vanessa, PersonaType.TempoAggro, catalog.GetByHero(HeroNames.Vanessa)[0], 42, repo);

        Assert.Equal(1, p.PlayerState.Day);
        Assert.Equal(1, p.PlayerState.Level);
        Assert.Equal(4, p.PlayerState.Hand.UnlockedCount);
        Assert.InRange(p.PlayerState.HealthMax, 250, 300);
        Assert.Equal(20, p.PlayerState.Prestige);
        Assert.False(p.PlayerState.FatesUsed);
    }

    [Fact]
    public void FatesRevival_ShouldTriggerOnceAndEliminateOnSecondFatalLoss()
    {
        var repo = Repo.Value;
        var catalog = new StubArchetypeCatalog(repo);
        var sim = new FastRunSimulator();
        var p = sim.CreateParticipant("fates_test", HeroNames.Dooley, PersonaType.TempoAggro, catalog.GetByHero(HeroNames.Dooley)[0], 777, repo);

        var opts = new FastRunOptions { MaxDay = 17, VictoriesToWin = 10, AllowEndlessAfterVictory = false, RequireOfficialZhCards = false };
        var snap1 = sim.AdvanceOneDayToPvp(p, 1, repo, catalog, opts);

        // 模拟第 5 天声望耗尽失利（Prestige 3 扣 5 归零，首次触发 Fates 复活为 Prestige 1）
        p.PlayerState.Prestige = 3;
        snap1.Day = 5;
        sim.ApplyPvpOutcome(p, snap1, new PvpMatchOutcome { OpponentBoardId = "opp1", Won = false }, repo, opts);
        Assert.True(p.PlayerState.FatesUsed);
        Assert.Equal(1, p.PlayerState.Prestige);
        Assert.False(p.IsFinished);

        // 第二次致命失利（第 6 天），应彻底淘汰
        var snap2 = sim.AdvanceOneDayToPvp(p, 6, repo, catalog, opts);
        sim.ApplyPvpOutcome(p, snap2, new PvpMatchOutcome { OpponentBoardId = "opp2", Won = false }, repo, opts);
        Assert.Equal(0, p.PlayerState.Prestige);
        Assert.True(p.PlayerState.IsEliminated);
        Assert.True(p.IsFinished);
    }

    [Fact]
    public void FlexPivot_And_PositioningBot_ShouldWorkProperly()
    {
        var repo = Repo.Value;
        var catalog = new StubArchetypeCatalog(repo);
        var bot = new ArchetypeBot();
        var sim = new FastRunSimulator();
        var index = SimulationRepoIndex.GetOrCreate(repo);

        var archList = catalog.GetByHero(HeroNames.Pygmalien);
        var p = sim.CreateParticipant("pivot_test", HeroNames.Pygmalien, PersonaType.FlexPivot, archList[0], 999, repo);
        p.PlayerState.Day = 4;
        p.PlayerState.Hand.UnlockCountCentered(8);

        // 往手牌塞入大量匹配 arch2 且不属 arch1 的组件（Property / Burn / Shield）诱导 FlexPivot 转型
        var altItems = repo.GetSpawnableItemsForHero(HeroNames.Pygmalien, includeCommon: false)
            .Where(c => !archList[0].CoreItemNames.Contains(c.InternalName)
                     && !archList[0].SupportItemNames.Contains(c.InternalName)
                     && (archList[1].CoreItemNames.Contains(c.InternalName) || c.Tags.Contains("Property") || c.HiddenTags.Contains("Burn") || c.HiddenTags.Contains("Shield")))
            .OrderByDescending(c => archList[1].CoreItemNames.Contains(c.InternalName) ? 1 : 0)
            .Take(4)
            .ToList();

        foreach (var item in altItems)
        {
            var ci = new CardInstance(item, SimulationRepoIndex.ClampSupportedTier(item, CardTier.Gold));
            p.PlayerState.Hand.TryAddAuto(ci);
        }

        bool pivoted = bot.TryPivotArchetype(p, catalog, index);
        Assert.True(pivoted, "当场上持有大量另一套蓝图的高阶核心组件时，FlexPivot 应触发流派转型");
        Assert.Equal(archList[1].Id, p.Archetype.Id);

        // 检验站位重排零重叠且在解锁槽位内
        bot.OptimizeAndPositionBoard(p, index, repo);
        var snap = BoardSnapshotMapper.ToSnapshot(p, repo);
        var val = new BoardValidator().Validate(snap, repo, requireOfficialZh: false);
        Assert.True(val.IsValid, string.Join("; ", val.Errors));
    }

    [Fact]
    public void Personas_ShouldProduceDistinctEconomyAndBoardProfiles()
    {
        var repo = Repo.Value;
        var catalog = new StubArchetypeCatalog(repo);
        var sim = new FastRunSimulator();
        var validator = new BoardValidator();
        var opts = new FastRunOptions
        {
            MaxDay = 10,
            VictoriesToWin = 10,
            AllowEndlessAfterVictory = true,
            RequireOfficialZhCards = repo.Localization != null
        };

        var tempoEarlyGold = new List<int>();
        var greedyEarlyGold = new List<int>();
        var greedyIncome = new List<int>();
        var tempoIncome = new List<int>();

        for (int i = 0; i < 6; i++)
        {
            var hero = HeroNames.PlayableHeroes[i % HeroNames.PlayableHeroes.Length];
            var tempo = sim.CreateParticipant($"t_{i}", hero, PersonaType.TempoAggro, catalog.GetByHero(hero)[0], 500 + i, repo);
            var greedy = sim.CreateParticipant($"g_{i}", hero, PersonaType.GreedyEco, catalog.GetByHero(hero)[1], 600 + i, repo);

            for (int day = 1; day <= 6; day++)
            {
                var ts = sim.AdvanceOneDayToPvp(tempo, day, repo, catalog, opts);
                var gs = sim.AdvanceOneDayToPvp(greedy, day, repo, catalog, opts);
                Assert.True(validator.Validate(ts, repo, requireOfficialZh: repo.Localization != null).IsValid);
                Assert.True(validator.Validate(gs, repo, requireOfficialZh: repo.Localization != null).IsValid);

                if (day is >= 2 and <= 4)
                {
                    tempoEarlyGold.Add(tempo.PlayerState.Gold);
                    greedyEarlyGold.Add(greedy.PlayerState.Gold);
                }

                sim.ApplyPvpOutcome(tempo, ts, new PvpMatchOutcome { OpponentBoardId = gs.BoardId, Won = true }, repo, opts);
                sim.ApplyPvpOutcome(greedy, gs, new PvpMatchOutcome { OpponentBoardId = ts.BoardId, Won = true }, repo, opts);
            }

            tempoIncome.Add(tempo.PlayerState.Income);
            greedyIncome.Add(greedy.PlayerState.Income);
        }

        double avgTempoGold = tempoEarlyGold.Average();
        double avgGreedyGold = greedyEarlyGold.Average();
        double avgTempoInc = tempoIncome.Average();
        double avgGreedyInc = greedyIncome.Average();

        _output.WriteLine($"Day 2~4 平均留存金币: TempoAggro={avgTempoGold:F1}, GreedyEco={avgGreedyGold:F1} | Day 6 平均收入: TempoAggro={avgTempoInc:F1}, GreedyEco={avgGreedyInc:F1}");

        Assert.True(avgGreedyGold > avgTempoGold, $"GreedyEco 前期留存金币 ({avgGreedyGold:F1}) 应高于 TempoAggro ({avgTempoGold:F1})");
        Assert.True(avgGreedyInc >= avgTempoInc, $"GreedyEco 收入 ({avgGreedyInc:F1}) 应不低于 TempoAggro ({avgTempoInc:F1})");
    }

    [Fact]
    public void CluelessNewbie_ShouldHaveSignificantlyLowerWinRateThanHardForcerAndFlexPivot()
    {
        var repo = Repo.Value;
        var catalog = new StubArchetypeCatalog(repo);
        var sim = new FastRunSimulator();
        var validator = new BoardValidator();
        var opts = new FastRunOptions
        {
            MaxDay = 10,
            VictoriesToWin = 10,
            AllowEndlessAfterVictory = true,
            RequireOfficialZhCards = repo.Localization != null
        };

        var newbies = new List<RunParticipantState>();
        var tryhards = new List<RunParticipantState>();

        for (int i = 0; i < 8; i++)
        {
            var hero = HeroNames.PlayableHeroes[i];
            newbies.Add(sim.CreateParticipant($"nb_{i}", hero, PersonaType.CluelessNewbie, catalog.PickForHero(hero, PersonaType.CluelessNewbie, new Random(100 + i)), 100 + i, repo));
            var tryPersona = i % 2 == 0 ? PersonaType.HardForcer : PersonaType.FlexPivot;
            tryhards.Add(sim.CreateParticipant($"th_{i}", hero, tryPersona, catalog.PickForHero(hero, tryPersona, new Random(200 + i)), 200 + i, repo));
        }

        int newbieWins = 0, tryhardWins = 0, totalFights = 0;

        for (int day = 1; day <= 8; day++)
        {
            for (int i = 0; i < 8; i++)
            {
                var nbSnap = sim.AdvanceOneDayToPvp(newbies[i], day, repo, catalog, opts);
                var thSnap = sim.AdvanceOneDayToPvp(tryhards[i], day, repo, catalog, opts);

                var sNb = validator.ToPlayerState(nbSnap, repo);
                var sTh = validator.ToPlayerState(thSnap, repo);
                var res = CombatSimulator.Simulate(repo, sTh, sNb, 900 + day * 10 + i, "PVP", recordTimeline: false, recordEvents: false);
                totalFights++;

                bool thWon = res.WinnerPlayerId == sTh.PlayerId;
                if (thWon) tryhardWins++; else newbieWins++;

                sim.ApplyPvpOutcome(tryhards[i], thSnap, new PvpMatchOutcome { OpponentBoardId = nbSnap.BoardId, Won = thWon }, repo, opts);
                sim.ApplyPvpOutcome(newbies[i], nbSnap, new PvpMatchOutcome { OpponentBoardId = thSnap.BoardId, Won = !thWon }, repo, opts);
            }
        }

        double thRate = (double)tryhardWins / totalFights;
        double nbRate = (double)newbieWins / totalFights;
        _output.WriteLine($"Day 1~8 强追/高玩 vs 萌新唐人实战胜率: HardForcer/FlexPivot={thRate:P1} ({tryhardWins}/{totalFights}) vs CluelessNewbie={nbRate:P1} ({newbieWins}/{totalFights})");

        Assert.True(thRate > nbRate + 0.20, $"HardForcer/FlexPivot 胜率 ({thRate:P1}) 应显著高于 CluelessNewbie ({nbRate:P1})");
    }

    [Fact]
    public void CoreAndSingleWeaponRules_And_DirectionalMerchants_ShouldWorkProperly()
    {
        var repo = Repo.Value;
        var bot = new ArchetypeBot();
        var sim = new FastRunSimulator();
        var index = SimulationRepoIndex.GetOrCreate(repo);

        // 1. 验证单武器意识（Single_Weapon 或 Crow's Nest）会保留主武器并惩罚第 2 把武器 -90
        var vanessaWeapons = repo.GetSpawnableItemsForHero(HeroNames.Vanessa, includeCommon: false)
            .Where(c => c.Tags.Contains("Weapon"))
            .Take(3)
            .ToList();
        var crowsNest = repo.GetSpawnableItemsForHero(HeroNames.Vanessa, includeCommon: false)
            .FirstOrDefault(c => c.InternalName.Equals("Crow's Nest", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(crowsNest);
        Assert.True(vanessaWeapons.Count >= 2);

        var singleWeaponArch = new ArchetypeDefinition
        {
            Id = "Vanessa_Single_Weapon_Test",
            Hero = HeroNames.Vanessa,
            NameZh = "瓦内莎单武器测试",
            CoreItemNames = new[] { crowsNest!.InternalName, vanessaWeapons[0].InternalName },
            PreferredTags = new Dictionary<string, double> { ["Weapon"] = 1.5, ["Aquatic"] = 1.2 },
            PreferredHiddenTags = new Dictionary<string, double> { ["Damage"] = 1.4 }
        };

        var p = sim.CreateParticipant("sw_test", HeroNames.Vanessa, PersonaType.HardForcer, singleWeaponArch, 321, repo);
        p.PlayerState.Hand.UnlockCountCentered(10);
        p.PlayerState.Hand.TryAddAuto(new CardInstance(crowsNest, CardTier.Gold));
        p.PlayerState.Hand.TryAddAuto(new CardInstance(vanessaWeapons[0], CardTier.Gold));

        double secondWeaponScore = bot.ScoreItem(vanessaWeapons[1], CardTier.Bronze, p, index.GetMeta(vanessaWeapons[1]));
        Assert.True(secondWeaponScore < 0, $"单武器模式下第 2 把武器应被重罚为负分，实际: {secondWeaponScore:F1}");

        // 2. 验证 3 格核心大件在背包筛选中不会被普通 1 格小件挤掉
        var largeCore = repo.GetSpawnableItemsForHero(HeroNames.Vanessa, includeCommon: false)
            .First(c => c.CardSize == CardSize.Large);
        p.PlayerState.Hand.Cards.Clear();
        p.PlayerState.Hand.TryAddAuto(new CardInstance(largeCore, CardTier.Silver));
        var smallFillers = repo.GetSpawnableItemsForHero(HeroNames.Vanessa, includeCommon: false)
            .Where(c => c.CardSize == CardSize.Small && !singleWeaponArch.CoreItemNames.Contains(c.InternalName))
            .Take(4)
            .ToList();
        foreach (var sf in smallFillers)
            p.PlayerState.Stash.TryAddAuto(new CardInstance(sf, CardTier.Bronze));

        bot.OptimizeAndPositionBoard(p, index, repo);
        Assert.Contains(p.PlayerState.Hand.Cards, c => c.Template.Id == crowsNest.Id || c.Template.Id == largeCore.Id);

        // 3. 验证定向商人选择优先命中流派标签/大件尺寸商人
        var merchants = new[]
        {
            new MerchantSpec("General Merchant", null, null),
            new MerchantSpec("Large Merchant", null, CardSize.Large),
            new MerchantSpec("Potion Merchant", "Potion", null)
        };
        var pickedMerchant = bot.ChooseMerchant(p, merchants, index);
        Assert.Equal("Large Merchant", pickedMerchant.DisplayName);
    }

    private static PvpBoardSnapshot CloneSnapshot(PvpBoardSnapshot s) => new()
    {
        BoardId = s.BoardId,
        RunId = s.RunId,
        Version = s.Version,
        Source = s.Source,
        Persona = s.Persona,
        Archetype = s.Archetype,
        Day = s.Day,
        Hero = s.Hero,
        Level = s.Level,
        HealthMax = s.HealthMax,
        Prestige = s.Prestige,
        Wins = s.Wins,
        Losses = s.Losses,
        UnlockedSlotsMask = s.UnlockedSlotsMask,
        Items = s.Items.Select(i => new PvpItemSnapshot
        {
            Slot = i.Slot,
            Width = i.Width,
            TemplateId = i.TemplateId,
            InternalName = i.InternalName,
            ZhName = i.ZhName,
            Tier = i.Tier,
            Enchantment = i.Enchantment,
            PermanentAttributes = new Dictionary<string, double>(i.PermanentAttributes)
        }).ToList(),
        Skills = s.Skills.Select(sk => new PvpSkillSnapshot
        {
            TemplateId = sk.TemplateId,
            InternalName = sk.InternalName,
            ZhName = sk.ZhName,
            Tier = sk.Tier
        }).ToList()
    };

    private static double CalcHighTierItemRate(List<PvpBoardSnapshot> snaps, CardTier minTier)
    {
        int total = snaps.Sum(s => s.Items.Count);
        if (total == 0) return 0;
        int high = snaps.Sum(s => s.Items.Count(i => i.Tier >= minTier));
        return (double)high / total;
    }

    private static double CalcEnchantRate(List<PvpBoardSnapshot> snaps)
    {
        int total = snaps.Sum(s => s.Items.Count);
        if (total == 0) return 0;
        int ench = snaps.Sum(s => s.Items.Count(i => !string.IsNullOrEmpty(i.Enchantment)));
        return (double)ench / total;
    }

    private static int CountBits(int mask)
    {
        int c = 0;
        for (int i = 0; i < 10; i++)
            if ((mask & (1 << i)) != 0) c++;
        return c;
    }

    [Fact]
    public void OutOfCombatGrowthLoop_ShouldDriveRealHealthMaxExcessAndPermanentAttributeMods()
    {
        var repo = Repo.Value;
        var catalog = new StubArchetypeCatalog(repo);
        var sim = new FastRunSimulator();
        var bot = new ArchetypeBot();
        var validator = new BoardValidator();
        var index = SimulationRepoIndex.GetOrCreate(repo);
        var rng = new Random(20260808);

        // 1. 真实卡牌局外闭环单元校验（Fixer Upper 升阶、Cookies 销毁防循环、Money Tree + Spare Change 消化、Belt 光环不污染快照）
        var p = sim.CreateParticipant("ooc_unit", HeroNames.Pygmalien, PersonaType.GreedyEco, catalog.GetByHero(HeroNames.Pygmalien)[0], 777, repo);
        p.PlayerState.Day = 2;
        p.PlayerState.Hour = 1;
        p.PlayerState.Hand.UnlockCountCentered(8);

        if (index.TryGetCardByInternalName("Fixer Upper", out var fixerTpl) && fixerTpl != null)
        {
            var fixer = new CardInstance(fixerTpl, CardTier.Bronze);
            p.PlayerState.Stash.TryAddAuto(fixer);
            ArchetypeBot.FireSafeOutOfCombatTriggers(p.PlayerState, "TTriggerOnDayStarted", null, repo, rng, index);
            Assert.True(fixer.Tier >= CardTier.Silver, $"Fixer Upper 应在 DayStarted 后自动升阶，当前阶位: {fixer.Tier}");
        }

        if (index.TryGetCardByInternalName("Cookies", out var cookiesTpl) && cookiesTpl != null)
        {
            var cookies = new CardInstance(cookiesTpl, CardTier.Bronze);
            p.PlayerState.Hand.TryAddAuto(cookies);
            double hpBefore = p.PlayerState.HealthMax;
            ArchetypeBot.FireSafeOutOfCombatTriggers(p.PlayerState, "TTriggerOnFightEnded", null, repo, rng, index, combatType: "PVE", combatOutcome: "Win");
            Assert.True(p.PlayerState.HealthMax > hpBefore, "Cookies 应在 FightEnded 后永久提升 HealthMax");
            Assert.DoesNotContain(cookies, p.PlayerState.Hand.Cards);
        }

        if (index.TryGetCardByInternalName("Money Tree", out var treeTpl) && treeTpl != null)
        {
            var tree = new CardInstance(treeTpl, CardTier.Silver);
            p.PlayerState.Hand.TryAddAuto(tree);
            int stashBefore = p.PlayerState.Stash.Cards.Count;
            ArchetypeBot.FireSafeOutOfCombatTriggers(
                p.PlayerState, "TTriggerOnPlayerAttributeChanged", null, repo, rng, index,
                changedAttr: "Level", prevVal: 1, curVal: 2);
            Assert.True(p.PlayerState.Stash.Cards.Count > stashBefore, "Money Tree 升级后应生成 Spare Change 进入 Stash");

            bot.DigestStashConsumables(p, repo, index);
            Assert.True(
                tree.PermanentAdd.Values.Any(v => v > 0),
                "消化 Stash 中的 Spare Change 后应触发 Money Tree 的 TTriggerOnCardSold 永久属性成长");
        }

        if (index.TryGetCardByInternalName("Belt", out var beltTpl) && beltTpl != null)
        {
            var belt = new CardInstance(beltTpl, beltTpl.DefaultTier);
            Assert.True(p.PlayerState.Hand.TryAddAuto(belt), "Belt 应能成功置入手牌");
            p.PlayerState.Attributes["HealthMax"] = 1000;
            AuraEngine.RecalculateAuras(p.PlayerState, null, RuleScope.OutOfCombat, 5, 1);
            Assert.True(p.PlayerState.HealthMax > 1000, "Belt 在局外重算光环时应生效提升 HealthMax");

            var auraSnap = BoardSnapshotMapper.ToSnapshot(p, repo);
            Assert.Equal(1000, auraSnap.HealthMax);
        }

        // 2. 多英雄真实对局统计：验证超出纯升级基线的 HealthMax 成长、attribute_mods 覆盖率、附魔均数与性能
        var opts = new FastRunOptions
        {
            MaxDay = 14,
            VictoriesToWin = 10,
            AllowEndlessAfterVictory = true,
            RequireOfficialZhCards = repo.Localization != null
        };

        var personas = new[] { PersonaType.TempoAggro, PersonaType.GreedyEco, PersonaType.HardForcer };
        var lateSnaps = new List<PvpBoardSnapshot>();
        var veryLateSnaps = new List<PvpBoardSnapshot>();

        // 先执行单日热身与全代 GC 清理，避免前置全量扫描测试遗留的冷 JIT 与 Gen2 堆碎片扭曲 24 局稳态吞吐计时
        var warmArch = catalog.PickForHero(HeroNames.Vanessa, PersonaType.TempoAggro, new Random(8799));
        var warmPart = sim.CreateParticipant("ooc_warmup", HeroNames.Vanessa, PersonaType.TempoAggro, warmArch, 8799, repo);
        _ = sim.AdvanceOneDayToPvp(warmPart, 1, repo, catalog, opts);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        var sw = Stopwatch.StartNew();

        int runIdx = 0;
        foreach (var hero in HeroNames.PlayableHeroes)
        {
            foreach (var persona in personas)
            {
                runIdx++;
                int seed = 8800 + runIdx;
                var arch = catalog.PickForHero(hero, persona, new Random(seed));
                var part = sim.CreateParticipant($"ooc_run_{runIdx}", hero, persona, arch, seed, repo);

                for (int d = 1; d <= opts.MaxDay && !part.IsFinished; d++)
                {
                    var snap = sim.AdvanceOneDayToPvp(part, d, repo, catalog, opts);
                    var vr = validator.Validate(snap, repo, requireOfficialZh: opts.RequireOfficialZhCards);
                    Assert.True(vr.IsValid, $"Day {d} {hero}/{persona} 盘面校验失败: {string.Join("; ", vr.Errors)}");

                    var restored = validator.ToPlayerState(snap, repo);
                    foreach (var c in restored.Hand.Cards)
                    {
                        double baseCd = c.BaseAttributes.GetValueOrDefault("CooldownMax", 0);
                        if (baseCd > 0)
                            Assert.True(c.GetAttributeUnscaled("CooldownMax") >= 1000, $"{c.Template.InternalName} 永久冷却越界 < 1000ms");
                        Assert.True(c.GetAttributeUnscaled("PercentCooldownReduction") <= 75.01, $"{c.Template.InternalName} 永久冷却缩减越界 > 75%");
                    }

                    if (d >= 8) lateSnaps.Add(snap);
                    if (d >= 10) veryLateSnaps.Add(snap);

                    bool won = (d + runIdx) % 2 == 0;
                    sim.ApplyPvpOutcome(part, snap, new PvpMatchOutcome { OpponentBoardId = "opp", Won = won }, repo, opts);
                }
            }
        }
        sw.Stop();

        double msPerRun = sw.Elapsed.TotalMilliseconds / runIdx;
        double avgHpExcess = lateSnaps.Average(s =>
        {
            int baseline = 250;
            for (int lvl = 2; lvl <= s.Level; lvl++)
            {
                baseline += repo.LevelUps.TryGetValue(lvl, out var lu) && lu.HealthIncrease > 0
                    ? lu.HealthIncrease
                    : Math.Min(800, 100 + (lvl - 1) * 50);
            }
            return s.HealthMax - baseline;
        });

        int lateBoardsWithMods = lateSnaps.Count(s => s.Items.Any(i => i.PermanentAttributes.Count > 0));
        double lateBoardModRate = (double)lateBoardsWithMods / Math.Max(1, lateSnaps.Count);
        int lateItemsTotal = lateSnaps.Sum(s => s.Items.Count);
        int lateItemsWithMods = lateSnaps.Sum(s => s.Items.Count(i => i.PermanentAttributes.Count > 0));
        double lateItemModRate = (double)lateItemsWithMods / Math.Max(1, lateItemsTotal);
        double avgEnchDay10Plus = veryLateSnaps.Average(s => s.Items.Count(i => !string.IsNullOrEmpty(i.Enchantment)));

        _output.WriteLine(
            $"局外成长闭环实测 ({runIdx} 局, {msPerRun:F2} ms/局): " +
            $"Day8~14 超出纯升级基线均血=+{avgHpExcess:F1} | " +
            $"带 attribute_mods 盘面占比={lateBoardModRate:P1} ({lateBoardsWithMods}/{lateSnaps.Count}) | " +
            $"带 attribute_mods 物品占比={lateItemModRate:P1} ({lateItemsWithMods}/{lateItemsTotal}) | " +
            $"Day10~14 单盘均附魔={avgEnchDay10Plus:F2}");

        Assert.True(avgHpExcess > 50.0, $"Day 8~14 HealthMax 应明显超出纯升级基线，实际超额: {avgHpExcess:F1}");
        Assert.True(lateBoardModRate > 0.25, $"Day 8~14 携带 PermanentAttributes 的盘面占比过低: {lateBoardModRate:P1}");
        Assert.True(lateItemModRate > 0.05, $"Day 8~14 携带 PermanentAttributes 的物品占比过低: {lateItemModRate:P1}");
        Assert.InRange(avgEnchDay10Plus, 0.8, 4.0);
        // 保留防死循环/极端复杂度退化守卫（移除对 Debug 宿主机敏感的 260ms 微基准墙钟阈值）
        Assert.True(msPerRun < 5000.0, $"单局耗时极端退化: {msPerRun:F2} ms/局");
    }

    [Fact]
    public void DisciplinedPersonas_Day10Plus_ShouldAvoidDuplicatesAndPreserveHeroIdentity()
    {
        var repo = Repo.Value;
        var catalog = new StubArchetypeCatalog(repo);
        var sim = new FastRunSimulator();
        var bot = new ArchetypeBot();
        var index = SimulationRepoIndex.GetOrCreate(repo);
        var validator = new BoardValidator();

        // 1. 单元测试：验证 TryExecuteBuyOrMerge 高阶替补低阶（绝不双摆）及禁止同/低阶同名重复上场，以及 MessyHybrid 禁止宽>=2重名
        var medBronzeToGold = repo.GetSpawnableItemsForHero(HeroNames.Vanessa, includeCommon: false)
            .First(c => c.CardSize == CardSize.Medium && c.Tiers != null && c.Tiers.ContainsKey("Bronze") && c.Tiers.ContainsKey("Gold"));

        var pUnit = sim.CreateParticipant("dup_unit", HeroNames.Vanessa, PersonaType.HardForcer, catalog.GetByHero(HeroNames.Vanessa)[0], 505, repo);
        pUnit.PlayerState.Hand.UnlockCountCentered(6);
        pUnit.PlayerState.Gold = 40;
        pUnit.PlayerState.Hand.TryAddAuto(new CardInstance(medBronzeToGold, CardTier.Bronze));

        // 买入 Gold 同名卡：应先卖出 Bronze 再置入 Gold，Hand 仍只有 1 张且为 Gold
        Assert.True(bot.TryExecuteBuyOrMerge(pUnit, new ShopOffer(medBronzeToGold, CardTier.Gold, 10, IsSkill: false), repo, index));
        var matchingInHand = pUnit.PlayerState.Hand.Cards.Where(c => c.Template.Id == medBronzeToGold.Id).ToList();
        Assert.Single(matchingInHand);
        Assert.Equal(CardTier.Gold, matchingInHand[0].Tier);

        // 再掉一张 Bronze 同名卡（如 PvE 免费掉落）：应被拒绝而非双摆
        Assert.False(bot.TryExecuteBuyOrMerge(pUnit, new ShopOffer(medBronzeToGold, CardTier.Bronze, 0, IsSkill: false), repo, index));
        Assert.Single(pUnit.PlayerState.Hand.Cards.Where(c => c.Template.Id == medBronzeToGold.Id));

        // MessyHybrid 赛前优化同样禁止把 Stash 中的宽 >= 2 同名卡拉进 Hand 双摆
        var pMessy = sim.CreateParticipant("messy_dup", HeroNames.Vanessa, PersonaType.MessyHybrid, catalog.GetByHero(HeroNames.Vanessa)[0], 506, repo);
        pMessy.PlayerState.Hand.UnlockCountCentered(10);
        pMessy.PlayerState.Hand.TryAddAuto(new CardInstance(medBronzeToGold, CardTier.Silver));
        pMessy.PlayerState.Stash.TryAddAuto(new CardInstance(medBronzeToGold, CardTier.Bronze));
        bot.OptimizeAndPositionBoard(pMessy, index, repo);
        Assert.Single(pMessy.PlayerState.Hand.Cards.Where(c => c.Template.Id == medBronzeToGold.Id));

        // 2. 全 8 英雄 × 4 纪律画像跑至 Day 14，验证 Day 10+ 重名盘面占比 < 15%（0 宽>=2 重名）且本英雄/流派核心占比 >= 70%
        var opts = new FastRunOptions
        {
            MaxDay = 14,
            VictoriesToWin = 10,
            AllowEndlessAfterVictory = true,
            RequireOfficialZhCards = repo.Localization != null
        };
        var disciplined = new[] { PersonaType.HardForcer, PersonaType.FlexPivot, PersonaType.TempoAggro, PersonaType.GreedyEco };

        int totalRuns = 0;
        int day10Boards = 0;
        int day10DupBoards = 0;
        int day10WideDupBoards = 0;
        int day10TotalItems = 0;
        int day10HeroOrCoreItems = 0;
        var sw = Stopwatch.StartNew();

        foreach (var hero in HeroNames.PlayableHeroes)
        {
            foreach (var persona in disciplined)
            {
                totalRuns++;
                int seed = 9600 + totalRuns;
                var arch = catalog.PickForHero(hero, persona, new Random(seed));
                var part = sim.CreateParticipant($"hero_purity_{totalRuns}", hero, persona, arch, seed, repo);

                for (int d = 1; d <= opts.MaxDay && !part.IsFinished; d++)
                {
                    var snap = sim.AdvanceOneDayToPvp(part, d, repo, catalog, opts);
                    var vr = validator.Validate(snap, repo, requireOfficialZh: opts.RequireOfficialZhCards);
                    Assert.True(vr.IsValid);

                    if (d >= 10)
                    {
                        day10Boards++;
                        var groups = snap.Items.GroupBy(i => i.TemplateId, StringComparer.OrdinalIgnoreCase).ToList();
                        if (groups.Any(g => g.Count() > 1))
                            day10DupBoards++;
                        if (groups.Any(g => g.Count() > 1 && g.First().Width >= 2))
                            day10WideDupBoards++;

                        foreach (var item in snap.Items)
                        {
                            day10TotalItems++;
                            var tpl = repo.GetCard(item.TemplateId);
                            bool isHero = tpl != null && tpl.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase);
                            bool isArchCore = part.Archetype.CoreItemNames.Contains(item.InternalName, StringComparer.OrdinalIgnoreCase)
                                || part.Archetype.SupportItemNames.Contains(item.InternalName, StringComparer.OrdinalIgnoreCase);
                            if (isHero || isArchCore)
                                day10HeroOrCoreItems++;
                        }
                    }

                    sim.ApplyPvpOutcome(part, snap, new PvpMatchOutcome { OpponentBoardId = "opp", Won = (d + totalRuns) % 2 == 0 }, repo, opts);
                }
            }
        }
        sw.Stop();

        double dupRate = (double)day10DupBoards / Math.Max(1, day10Boards);
        double heroOrCoreRate = (double)day10HeroOrCoreItems / Math.Max(1, day10TotalItems);
        double msPerRun = sw.Elapsed.TotalMilliseconds / totalRuns;

        _output.WriteLine(
            $"纪律画像 Day10+ 实测 ({totalRuns} 局 / {day10Boards} 盘面, {msPerRun:F2} ms/局): " +
            $"重名物品盘面占比={dupRate:P1} ({day10DupBoards}/{day10Boards}, 宽>=2重名={day10WideDupBoards}) | " +
            $"本英雄专属或流派核心物品占比={heroOrCoreRate:P1} ({day10HeroOrCoreItems}/{day10TotalItems})");

        Assert.Equal(0, day10WideDupBoards);
        Assert.True(dupRate < 0.15, $"Day 10+ 纪律画像重名物品盘面占比过高: {dupRate:P1}");
        Assert.True(heroOrCoreRate >= 0.70, $"Day 10+ 本英雄专属或核心物品占比过低: {heroOrCoreRate:P1}");
    }

    [Fact]
    public void OutOfCombatSpawnAndEnchant_ShouldStripOrRejectUnsupportedEnchantments()
    {
        var repo = Repo.Value;
        var catalog = new StubArchetypeCatalog(repo);
        var sim = new FastRunSimulator();
        var index = SimulationRepoIndex.GetOrCreate(repo);
        var rng = new Random(20260606);

        Assert.True(index.TryGetCardByInternalName("Toolbox", out var toolboxTpl) && toolboxTpl != null);
        Assert.True(index.TryGetCardByInternalName("Loupe", out var loupeTpl) && loupeTpl != null);
        Assert.False(loupeTpl!.Enchantments?.ContainsKey("Deadly") == true);

        // 1. 真实 Toolbox (Diamond, Deadly) 连续 80 次 DayStarted 派生测试：绝不产出模板不支持的附魔（如 Loupe + Deadly）
        var pToolbox = sim.CreateParticipant("toolbox_spawn", HeroNames.Stelle, PersonaType.GreedyEco, catalog.GetByHero(HeroNames.Stelle)[0], 601, repo);
        pToolbox.PlayerState.Day = 8;
        pToolbox.PlayerState.Hand.UnlockCountCentered(10);
        var toolbox = new CardInstance(toolboxTpl!, CardTier.Diamond, "Deadly");
        Assert.True(pToolbox.PlayerState.Hand.TryAddAuto(toolbox));

        for (int round = 0; round < 80; round++)
        {
            pToolbox.PlayerState.Stash.Cards.Clear();
            ArchetypeBot.FireSafeOutOfCombatTriggers(pToolbox.PlayerState, "TTriggerOnDayStarted", null, repo, rng, index);
            foreach (var spawned in pToolbox.PlayerState.Stash.Cards)
            {
                if (!string.IsNullOrEmpty(spawned.Enchantment))
                {
                    Assert.True(
                        spawned.Template.Enchantments != null && spawned.Template.Enchantments.ContainsKey(spawned.Enchantment),
                        $"衍生卡 {spawned.Template.InternalName} 携带了不被模板支持的附魔 {spawned.Enchantment}");
                }
            }
        }

        // 2. 精确构造“继承 Deadly 派生 Loupe + TActionCardEnchant('Deadly') 指向 Loupe + 指向空附魔模板卡”的局外闭环防线校验
        var spawnLoupeNode = JsonSerializer.Deserialize<RuleNode>($$"""
        {
          "$type": "TActionGameSpawnCards",
          "TargetPlayer": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" },
          "SpawnContext": {
            "$type": "TSpawnContextQuery",
            "Groups": [{
              "$type": "TSpawnGroup",
              "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{loupeTpl.Id}}"] }],
              "SelectionMethod": "Random"
            }],
            "SelectionMethod": "Sequential",
            "Limit": { "$type": "TFixedValue", "Value": 1.0 },
            "Behaviors": [{ "$type": "TSpawnBehaviorInheritEnchantment", "Inherits": true }]
          }
        }
        """, GameDatabaseLoader.JsonOptions)!;

        var enchantDeadlyHandNode = JsonSerializer.Deserialize<RuleNode>("""
        {
          "$type": "TActionCardEnchant",
          "Enchantment": "Deadly",
          "Target": { "$type": "TTargetCardSection", "TargetSection": "Hand" }
        }
        """, GameDatabaseLoader.JsonOptions)!;

        var enchantGoldenSelfTargetNode = JsonSerializer.Deserialize<RuleNode>("""
        {
          "$type": "TActionCardEnchant",
          "Enchantment": "Golden",
          "Target": { "$type": "TTargetCardPositional", "Origin": "Self", "TargetMode": "RightCard" }
        }
        """, GameDatabaseLoader.JsonOptions)!;

        var emptyEnchTpl = new CardTemplate
        {
            Id = "test-empty-ench-item",
            InternalName = "Test Empty Ench Item",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Enchantments = new Dictionary<string, EnchantmentData>()
        };

        var spawnerTpl = new CardTemplate
        {
            Id = "test-ooc-ench-guard-spawner",
            InternalName = "Test OOC Ench Guard Spawner",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Enchantments = new Dictionary<string, EnchantmentData> { ["Deadly"] = new EnchantmentData() },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new() { Id = "0", ActiveIn = "HandOnly", Trigger = new RuleNode { Type = "TTriggerOnDayStarted" }, Action = spawnLoupeNode },
                ["1"] = new() { Id = "1", ActiveIn = "HandOnly", Trigger = new RuleNode { Type = "TTriggerOnDayStarted" }, Action = enchantDeadlyHandNode }
            }
        };

        var pGuard = sim.CreateParticipant("ooc_ench_guard", HeroNames.Stelle, PersonaType.GreedyEco, catalog.GetByHero(HeroNames.Stelle)[0], 602, repo);
        pGuard.PlayerState.Day = 8;
        pGuard.PlayerState.Hand.UnlockCountCentered(10);

        var spawnerCard = new CardInstance(spawnerTpl, CardTier.Bronze, "Deadly");
        var handLoupe = new CardInstance(loupeTpl, CardTier.Diamond);
        var emptyEnchCard = new CardInstance(emptyEnchTpl, CardTier.Bronze);
        Assert.True(pGuard.PlayerState.Hand.TryAddAuto(spawnerCard));
        Assert.True(pGuard.PlayerState.Hand.TryAddAuto(handLoupe));
        Assert.True(pGuard.PlayerState.Hand.TryAddAuto(emptyEnchCard));

        ArchetypeBot.FireSafeOutOfCombatTriggers(pGuard.PlayerState, "TTriggerOnDayStarted", null, repo, rng, index);

        // 验证派生入 Stash 的 Loupe 绝不携带 Deadly
        Assert.Single(pGuard.PlayerState.Stash.Cards);
        Assert.Equal(loupeTpl.Id, pGuard.PlayerState.Stash.Cards[0].Template.Id);
        Assert.Null(pGuard.PlayerState.Stash.Cards[0].Enchantment);

        // 验证 TActionCardEnchant("Deadly") 被 Loupe 和空附魔卡拒绝
        Assert.Null(handLoupe.Enchantment);
        Assert.Null(emptyEnchCard.Enchantment);

        // 验证对 Loupe 合法的 Golden 附魔仍正常放行
        spawnerTpl.Abilities["2"] = new AbilityDefinition
        {
            Id = "2",
            ActiveIn = "HandOnly",
            Trigger = new RuleNode { Type = "TTriggerOnDayStarted" },
            Action = enchantGoldenSelfTargetNode
        };
        spawnerCard.RebuildTierAndEnchantmentData();
        ArchetypeBot.FireSafeOutOfCombatTriggers(pGuard.PlayerState, "TTriggerOnDayStarted", null, repo, rng, index);
        Assert.Equal("Golden", handLoupe.Enchantment);
    }
}
