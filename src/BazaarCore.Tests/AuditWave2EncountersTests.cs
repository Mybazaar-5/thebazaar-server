using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;
using BazaarServer.Game;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2 Batch 2B（遭遇生态、探险闭环、切板演出与野怪初值）全量回归与蒙特卡洛单测：
/// 覆盖 P0-12、P0-13、P0-14、P0-15、P0-16、P1-01、P1-04、P1-05、P1-22、P1-23、P1-24、P1-25。
/// </summary>
[Xunit.Collection("ShopEngineStaticState")]
public sealed class AuditWave2EncountersTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);
    private static readonly Lazy<List<BazaarServer.Data.MonsterTemplate>> Monsters =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.CreateMonstersCopy());
    private static readonly Lazy<LevelUpTable> LevelUps =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.LevelUps);
    private static readonly Lazy<GameModeConfig> Mode =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.Mode);

    private static string ResolveDataPath(string fileName)
        => TestInfrastructure.TestDataLocator.ResolveDataFile(fileName);

    private static (RunState Run, RunEngine Engine) CreateStartedRun(
        EHero hero = EHero.Vanessa,
        CustomRunRuleSet? customRules = null,
        uint day = 2,
        uint hour = 1,
        int gold = 50,
        int healthMax = 400,
        int seed = 20261122,
        ushort unlockedSlots = 0b00_0111_1000)
    {
        var run = new RunState
        {
            RunId = "w2b_" + Guid.NewGuid().ToString("N")[..10],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            Day = day,
            Hour = hour,
            State = ERunState.Choice,
            UnlockedSlots = unlockedSlots,
            CustomRules = (customRules ?? new CustomRunRuleSet()).CloneNormalized(),
            RngSeed = seed,
        };
        run.HandGrid.UnlockedMask = unlockedSlots;
        run.PlayerAttributes[EPlayerAttributeType.Gold] = gold;
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = healthMax;
        run.PlayerAttributes[EPlayerAttributeType.Health] = healthMax;
        run.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
        run.PlayerAttributes[EPlayerAttributeType.Level] = 1;

        var engine = new RunEngine(
            run,
            Pool.Value,
            Monsters.Value,
            LevelUps.Value,
            Mode.Value,
            Repo.Value,
            new PvpGhostPool());

        typeof(RunEngine)
            .GetProperty(nameof(RunEngine.StartSelectionDone))!
            .SetValue(engine, true);

        return (run, engine);
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        string templateId,
        ETier tier,
        EInventorySection section,
        EContainerSocketId socket,
        string? instanceId = null)
    {
        var coreTpl = Repo.Value.GetCard(templateId)!;
        var poolTpl = Pool.Value.Get(templateId);
        var core = new CardInstance(
            coreTpl,
            ShopEngine.ToCoreTier(tier),
            null,
            instanceId ?? ("itm_" + Guid.NewGuid().ToString("N")[..8]))
        {
            StartSlot = (int)socket,
        };
        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = socket,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, Repo.Value.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        var grid = section == EInventorySection.Stash ? run.StashGrid : run.HandGrid;
        grid.UnlockedMask = section == EInventorySection.Stash ? 1023 : run.UnlockedSlots;
        Assert.True(grid.TryPlaceOrShift(core, (int)socket), $"无法将测试卡 {templateId} 放置到 {section}:{socket}");
        entry.Socket = (EContainerSocketId)core.StartSlot;
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static RunState.CardEntry AddOwnedSkill(
        RunState run,
        string templateId,
        ETier tier,
        EContainerSocketId socket,
        string? instanceId = null)
    {
        var coreTpl = Repo.Value.GetCard(templateId)!;
        var poolTpl = Pool.Value.Get(templateId);
        var core = new CardInstance(
            coreTpl,
            ShopEngine.ToCoreTier(tier),
            null,
            instanceId ?? ("skl_" + Guid.NewGuid().ToString("N")[..8]))
        {
            StartSlot = (int)socket,
        };
        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = null,
            Socket = socket,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, Repo.Value.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static RunState.CardEntry InjectChoiceEncounter(RunState run, string templateId, ECardType cardType = ECardType.EventEncounter)
    {
        var poolTpl = Pool.Value.Get(templateId);
        var coreTpl = Repo.Value.GetCard(templateId);
        var entry = new RunState.CardEntry
        {
            InstanceId = "enc_" + Guid.NewGuid().ToString("N")[..8],
            TemplateId = Guid.Parse(templateId),
            Type = cardType,
            Size = ECardSize.Medium,
            Tier = ShopEngine.ParseTier(poolTpl?.StartingTier ?? coreTpl?.StartingTier),
            Owner = null,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.State = ERunState.Choice;
        run.Cards[entry.InstanceId] = entry;
        run.SelectionSet.Clear();
        run.SelectionSet.Add(entry.InstanceId);
        return entry;
    }

    private static RunState.CardEntry InjectSelectionSkill(RunState run, string templateId, ETier tier = ETier.Bronze)
    {
        var coreTpl = Repo.Value.GetCard(templateId)!;
        var poolTpl = Pool.Value.Get(templateId);
        var core = new CardInstance(
            coreTpl,
            ShopEngine.ToCoreTier(tier),
            null,
            "cand_skl_" + Guid.NewGuid().ToString("N")[..8]);
        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = tier,
            Owner = null,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, Repo.Value.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[entry.InstanceId] = entry;
        run.SelectionSet.Add(entry.InstanceId);
        return entry;
    }

    // ── P0-12：The Cult 标准模式 Day 6+ 放行 + 豁免 maxDayTier + IsSingleSpawn ──

    [Fact]
    public void P0_12_TheCult_StandardModeDay6Gate_TierExempt_And_SingleSpawn()
    {
        var cultGuid = Guid.Parse(CardArchetypeIndex.TheCultEncounterId);

        // 1. Day 1..5 标准模式下绝不可刷出 The Cult
        for (uint d = 1; d <= 5; d++)
        {
            var runEarly = new RunState
            {
                RunId = $"cult_d{d}",
                GameModeId = Pool.Value.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = d,
                Hour = 2,
                RngSeed = (int)(12000 + d),
                CustomRules = new CustomRunRuleSet(),
            };
            var shop = new ShopEngine(runEarly, Pool.Value, Repo.Value, runEarly.CustomRules);
            for (int i = 0; i < 200; i++)
            {
                runEarly.VisitedDayEncounterIds.Clear();
                runEarly.VisitedSingleSpawnEncounterIds.Clear();
                shop.DealHourEncounters();
                Assert.DoesNotContain(runEarly.SelectionSet, id => runEarly.Cards[id].TemplateId == cultGuid);
            }
        }

        // 2. Day 6（maxDayTier=Gold，而 The Cult 为 Diamond）标准模式下必须可刷出（豁免 maxDayTier）
        var runDay6 = new RunState
        {
            RunId = "cult_d6_std",
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 6,
            Hour = 2,
            RngSeed = 12060,
            CustomRules = new CustomRunRuleSet(),
        };
        var shop6 = new ShopEngine(runDay6, Pool.Value, Repo.Value, runDay6.CustomRules);
        bool sawCultOnDay6 = false;
        // Patch 20.2 实测 The Cult 在 Day 6 为稀有事件（日级 ~0.46%），5000 次独立抽选确保稳定验证可达性
        for (int i = 0; i < 5000; i++)
        {
            runDay6.VisitedDayEncounterIds.Clear();
            runDay6.VisitedSingleSpawnEncounterIds.Clear();
            shop6.DealHourEncounters();
            if (runDay6.SelectionSet.Any(id => runDay6.Cards[id].TemplateId == cultGuid))
            {
                sawCultOnDay6 = true;
                break;
            }
        }
        Assert.True(sawCultOnDay6, "标准模式下 Day 6（5000 次抽选内）应当能够合法刷出 The Cult (bf1594cc)");

        // 3. 访问一次后写入 VisitedSingleSpawnEncounterIds，Day 6..10 严禁再出（IsSingleSpawn）
        runDay6.VisitedSingleSpawnEncounterIds.Add(CardArchetypeIndex.TheCultEncounterId);
        for (uint d = 6; d <= 10; d++)
        {
            runDay6.Day = d;
            for (int i = 0; i < 100; i++)
            {
                runDay6.VisitedDayEncounterIds.Clear();
                shop6.DealHourEncounters();
                Assert.DoesNotContain(runDay6.SelectionSet, id => runDay6.Cards[id].TemplateId == cultGuid);
            }
        }
    }

    // ── P0-13：Mysterious Portal Day 4+ 接入与 Scan for Anomalies 过滤安全 ──

    [Fact]
    public void P0_13_MysteriousPortal_Day4Gate_And_ScanForAnomaliesSafeFilter()
    {
        var portalGuid = Guid.Parse(CardArchetypeIndex.MysteriousPortalEncounterId);
        const string scanStepId = "377293bf-dbae-46c5-afd3-10e0949d3b51";
        const string weakpointDetectorId = "fc2f0122-129b-41e8-942e-a6a10a1c5962";

        // 校验分类为 EventCultAndMystery
        var portalTpl = Pool.Value.Get(CardArchetypeIndex.MysteriousPortalEncounterId)!;
        Assert.Equal(
            EEncounterWeightCategory.EventCultAndMystery,
            CardArchetypeIndex.ClassifyEncounter(portalTpl, Repo.Value.GetCard(portalTpl.Id)));

        // 1. Patch 20.2 实测 Mysterious Portal 自 Day 6 起解锁，Day 1..5 标准模式下不刷出
        for (uint d = 1; d <= 5; d++)
        {
            var runEarly = new RunState
            {
                RunId = $"portal_d{d}",
                GameModeId = Pool.Value.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = d,
                Hour = 2,
                RngSeed = (int)(13000 + d),
                CustomRules = new CustomRunRuleSet(),
            };
            var shop = new ShopEngine(runEarly, Pool.Value, Repo.Value, runEarly.CustomRules);
            for (int i = 0; i < 200; i++)
            {
                runEarly.VisitedDayEncounterIds.Clear();
                shop.DealHourEncounters();
                Assert.DoesNotContain(runEarly.SelectionSet, id => runEarly.Cards[id].TemplateId == portalGuid);
            }
        }

        // 2. Day 6+ 标准模式可合法刷出 Mysterious Portal（实测 Day 6 ~5.6%）
        var runDay6 = new RunState
        {
            RunId = "portal_d6_std",
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 6,
            Hour = 2,
            RngSeed = 13060,
            CustomRules = new CustomRunRuleSet(),
        };
        var shop6 = new ShopEngine(runDay6, Pool.Value, Repo.Value, runDay6.CustomRules);
        bool sawPortal = false;
        for (int i = 0; i < 1500; i++)
        {
            runDay6.VisitedDayEncounterIds.Clear();
            shop6.DealHourEncounters();
            if (runDay6.SelectionSet.Any(id => runDay6.Cards[id].TemplateId == portalGuid))
            {
                sawPortal = true;
                break;
            }
        }
        Assert.True(sawPortal, "标准模式下 Day 6+ 应当能够刷出 Mysterious Portal (b7acf32d)");

        // 3. 进入 Mysterious Portal 持 Weakpoint Detector 执行 Scan for Anomalies：多次抽选绝不漏出 DEFUNCT/Tutorial/ExclusionSet 锁定卡
        int totalDealtEvents = 0;
        for (int seed = 13100; seed < 13115; seed++)
        {
            var (run, engine) = CreateStartedRun(hero: EHero.Dooley, day: 7, hour: 2, seed: seed);
            AddOwnedItem(run, weakpointDetectorId, ETier.Gold, EInventorySection.Hand, EContainerSocketId.Socket_3);
            var portalEnc = InjectChoiceEncounter(run, CardArchetypeIndex.MysteriousPortalEncounterId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(portalEnc.InstanceId) }).Error);
            Assert.Equal(ERunState.Encounter, run.State);

            var scanStep = run.SelectionSet
                .Select(id => run.Cards[id])
                .FirstOrDefault(c => c.TemplateId == Guid.Parse(scanStepId));
            Assert.NotNull(scanStep);

            var stepOutcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(scanStep!.InstanceId) });
            Assert.Null(stepOutcome.Error);

            foreach (string dealtId in run.SelectionSet)
            {
                var dealt = run.Cards[dealtId];
                if (dealt.Type != ECardType.EventEncounter) continue;
                totalDealtEvents++;
                string tid = dealt.TemplateId.ToString();
                var tpl = Pool.Value.Get(tid);
                Assert.NotNull(tpl);
                Assert.DoesNotContain(tid, run.ExclusionSet, StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain("DEFUNCT", tpl!.InternalName ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Tutorial", tpl.InternalName ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("[DEBUG]", tpl.InternalName ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.False(string.Equals(tpl.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase));
            }
        }
        Assert.True(totalDealtEvents > 0, "Scan for Anomalies 应当成功发出合法高阶 EventEncounter");
    }

    // ── P0-14：EquipCandidateSkill 补齐 TTriggerOnCardSelected 与 ExclusionSet 同步（含新装备与融合双分支） ──

    [Fact]
    public void P0_14_PartnerPortalSkill_NewEquipAndFuse_DispatchesOnCardSelected_AndUnlocksForja()
    {
        const string vanessasFenceSkillId = "afe451fd-7726-4ed6-9ad9-48d917a12012";
        const string partnerPortalForjaVanessaId = "62c1cb63-b94e-4e3d-9dfd-99f5d88054df";
        const string partnerPortalEncounterId = "d16c204b-33d5-41d8-9621-67b240cd1fd5";

        // A. 新装备分支（模拟 Partner Portal 免费选技能 SelectionIsFree=true）
        var (run, engine) = CreateStartedRun(hero: EHero.Dooley, day: 5, hour: 2, seed: 14001);
        Assert.Contains(partnerPortalForjaVanessaId, run.ExclusionSet, StringComparer.OrdinalIgnoreCase);

        run.State = ERunState.Encounter;
        run.CurrentEncounterId = partnerPortalEncounterId;
        run.SelectionRules = new TSelectionContextRules { SelectionIsFree = true, CanSelectMultiple = false, CanExit = true };
        run.SelectionSet.Clear();
        var candSkill = InjectSelectionSkill(run, vanessasFenceSkillId, ETier.Bronze);

        var equipOutcome = engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(candSkill.InstanceId) });
        Assert.Null(equipOutcome.Error);

        Assert.DoesNotContain(partnerPortalForjaVanessaId, run.ExclusionSet, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(partnerPortalEncounterId, run.ExclusionSet, StringComparer.OrdinalIgnoreCase);

        // 解锁后 Partner Portal - Forja (Vanessa) 应能在 Day 5 非野怪 Choice 小时（Hour 4）基座槽合法刷出
        // 注：SelectSkillCommand 结束后会自动推进 Hour 2 -> Hour 3（PvE 野怪小时），切至 Hour 4 测试基座池
        run.Hour = 4;
        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
        bool sawForjaPartner = false;
        var forjaGuid = Guid.Parse(partnerPortalForjaVanessaId);
        for (int i = 0; i < 1200; i++)
        {
            run.VisitedDayEncounterIds.Clear();
            shop.DealHourEncounters();
            if (run.SelectionSet.Any(id => run.Cards[id].TemplateId == forjaGuid))
            {
                sawForjaPartner = true;
                break;
            }
        }
        Assert.True(sawForjaPartner, "装备 Vanessa's Fence 解锁后，Partner Portal - Forja (Vanessa) 应能在小时刷牌中合法出现");

        // B. 融合分支（Vanessa's Fence 实库支持 Diamond / Legendary：已持有 Diamond，再选一张免费高阶 Legendary 同卡走融合升级分支，同样触发 TTriggerOnCardSelected 并同步 ExclusionSet）
        var (runFuse, engineFuse) = CreateStartedRun(hero: EHero.Dooley, day: 5, hour: 2, seed: 14002);
        var existingSkill = AddOwnedSkill(runFuse, vanessasFenceSkillId, ETier.Diamond, EContainerSocketId.Socket_0);
        runFuse.ExclusionSet.Add(partnerPortalForjaVanessaId);
        runFuse.ExclusionSet.Remove(partnerPortalEncounterId);

        runFuse.State = ERunState.Encounter;
        runFuse.CurrentEncounterId = partnerPortalEncounterId;
        runFuse.SelectionRules = new TSelectionContextRules { SelectionIsFree = true, CanSelectMultiple = false, CanExit = true };
        runFuse.SelectionSet.Clear();
        var fuseCand = InjectSelectionSkill(runFuse, vanessasFenceSkillId, ETier.Legendary);

        var fuseOutcome = engineFuse.Handle(new SelectSkillCommand { InstanceId = new InstanceId(fuseCand.InstanceId) });
        Assert.Null(fuseOutcome.Error);
        Assert.Equal(ETier.Legendary, existingSkill.Tier);
        Assert.Contains(fuseOutcome.Events, e => e is GameSimEventCardFused);
        Assert.DoesNotContain(partnerPortalForjaVanessaId, runFuse.ExclusionSet, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(partnerPortalEncounterId, runFuse.ExclusionSet, StringComparer.OrdinalIgnoreCase);
    }

    // ── P0-15：IsTakeover 调度器（8 英雄专属传奇单次奇遇 + Star Chart Treasure） ──

    [Fact]
    public void P0_15_IsTakeover_All8HeroUniques_And_StarChartTreasure()
    {
        var expectedHeroTakeovers = new (EHero Hero, string Id, int MinDay)[]
        {
            (EHero.Vanessa,   "0a46b7df-e0a2-4211-8529-0bfe91974a71", 6), // Bounty Hunters
            (EHero.Dooley,    "2e0acd01-e0b2-4c0d-a7aa-84914c0cbb94", 6), // Dooley's Crib
            (EHero.Pygmalien, "8375c937-5542-4d89-9a87-37c01673c611", 6), // Investment Pitch
            (EHero.Mak,       "778c3493-54c3-45bd-8c1b-f410c5ceb937", 6), // Reflecting Pool
            (EHero.Karnok,    "0f34d701-1529-4dac-aa46-35d97d5a57a1", 7), // [Karnok Unique] Hidden Lake (BazaarDB 实测 Day 7+)
            (EHero.Jules,     "2d23952b-f0e4-4a7a-ac65-11c7def01679", 6), // Laurel's Night Terrors
            (EHero.Stelle,    "d0697c4c-2b63-4505-a946-d396493ac8a6", 8), // Family Reunion (BazaarDB 实测 Day 8+)
            (EHero.Hero8,     "d33129a9-5df2-47e5-9b6a-7469743fe5a8", 6), // The Dragons' Hideout
        };

        // 1. 全 8 英雄专属 IsTakeover 索引完整性 + 门控天数 + 实际刷出 + IsSingleSpawn 校验
        foreach (var (hero, expectedId, minDay) in expectedHeroTakeovers)
        {
            var found = Pool.Value.GetHeroTakeoverEncounters(hero.ToString());
            Assert.Contains(found, t => t.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase));

            var expectedGuid = Guid.Parse(expectedId);
            // 解锁门槛前绝不触发
            var earlyRun = new RunState
            {
                RunId = $"tk_early_{hero}",
                GameModeId = Pool.Value.DefaultGameModeId,
                Hero = hero,
                Day = (uint)(minDay - 1),
                Hour = 2,
                RngSeed = 15000 + (int)hero,
                CustomRules = new CustomRunRuleSet(),
            };
            var earlyShop = new ShopEngine(earlyRun, Pool.Value, Repo.Value, earlyRun.CustomRules);
            for (int i = 0; i < 80; i++)
            {
                earlyRun.VisitedDayEncounterIds.Clear();
                earlyShop.DealHourEncounters();
                Assert.DoesNotContain(earlyRun.SelectionSet, id => earlyRun.Cards[id].TemplateId == expectedGuid);
            }

            // 达到门槛日后可成功触发，且标记单次访问后不再重出
            var activeRun = new RunState
            {
                RunId = $"tk_on_{hero}",
                GameModeId = Pool.Value.DefaultGameModeId,
                Hero = hero,
                Day = (uint)minDay,
                Hour = 2,
                RngSeed = 15100 + (int)hero,
                CustomRules = new CustomRunRuleSet(),
            };
            var activeShop = new ShopEngine(activeRun, Pool.Value, Repo.Value, activeRun.CustomRules);
            bool triggered = false;
            for (int i = 0; i < 150; i++)
            {
                activeRun.VisitedDayEncounterIds.Clear();
                activeRun.VisitedSingleSpawnEncounterIds.Clear();
                activeShop.DealHourEncounters();
                if (activeRun.SelectionSet.Any(id => activeRun.Cards[id].TemplateId == expectedGuid))
                {
                    triggered = true;
                    break;
                }
            }
            Assert.True(triggered, $"英雄 {hero} 在 Day {minDay} 应当触发专属 IsTakeover 奇遇 {expectedId}");

            activeRun.VisitedSingleSpawnEncounterIds.Add(expectedId);
            for (int i = 0; i < 60; i++)
            {
                activeRun.VisitedDayEncounterIds.Clear();
                activeShop.DealHourEncounters();
                Assert.DoesNotContain(activeRun.SelectionSet, id => activeRun.Cards[id].TemplateId == expectedGuid);
            }
        }

        // 2. Star Chart Treasure (34c27d0d)：经 Patch 20.2 多源核验确认已淘汰（0 样本，现行 Star Chart 联动位于 The Docks），
        //    即便持有 Star Chart 也不再在常规小时中自动触发 Takeover。
        var scGuid = Guid.Parse(CardPool.StarChartTreasureEncounterId);
        Assert.Null(Pool.Value.GetStarChartTreasureEncounter());
        var (scRun, _) = CreateStartedRun(hero: EHero.Vanessa, day: 3, hour: 2, seed: 15201);
        AddOwnedItem(scRun, CardPool.StarChartItemId, ETier.Silver, EInventorySection.Hand, EContainerSocketId.Socket_3);
        var scShop = new ShopEngine(scRun, Pool.Value, Repo.Value, scRun.CustomRules);
        for (int i = 0; i < 250; i++)
        {
            scRun.VisitedDayEncounterIds.Clear();
            scRun.VisitedSingleSpawnEncounterIds.Clear();
            scShop.DealHourEncounters();
            Assert.DoesNotContain(scRun.SelectionSet, id => scRun.Cards[id].TemplateId == scGuid);
        }
    }

    // ── P0-16：数据驱动 GuidOnly 白昼奇遇编目（剔除 6 张已淘汰/限时/系统卡 + 按英雄接回 Pol + 全 8 英雄严格隔离） ──

    [Fact]
    public void P0_16_GuidOnlyHourlyCatalog_SuitesSplit_And_DirectEffectEncounters()
    {
        var catalog = Pool.Value.GetGuidOnlyHourlyCatalog();

        // 1. 校验编目精确规模与分流：涵盖按阶与分天白昼奇遇套装，且精准分流商人/额外野怪挑战/剔除禁忌卡
        Assert.True(catalog.TieredSuites.Count >= 35, $"按阶白昼奇遇套数异常偏低：{catalog.TieredSuites.Count}");
        Assert.True(catalog.TotalSuiteCardCount >= 145, $"按阶/分天白昼奇遇总卡数异常偏低：{catalog.TotalSuiteCardCount}");
        Assert.Contains(catalog.DayRangeSuites, s => s.BaseName.Equals("Deadly Duel", StringComparison.OrdinalIgnoreCase) && s.Role == GuidOnlyEncounterRole.BonusCombatChallenge);
        Assert.Contains(catalog.DayRangeSuites, s => s.BaseName.Equals("Epic Battle", StringComparison.OrdinalIgnoreCase) && s.Role == GuidOnlyEncounterRole.BonusCombatChallenge);

        // Advanced Training 分流到 RareOrSkillMerchant 并被识别为商人；官方大件商人 Pol / Pol (Pygmalien) 按英雄接回独立目录
        Assert.Contains(catalog.StandaloneEntries, e => e.Card.Id.Equals(CardArchetypeIndex.AdvancedTrainingEncounterId, StringComparison.OrdinalIgnoreCase) && e.Role == GuidOnlyEncounterRole.RareOrSkillMerchant);
        Assert.True(CardArchetypeIndex.IsMerchantEncounterTemplate(CardArchetypeIndex.AdvancedTrainingEncounterId, "Advanced Training", Pool.Value.Get(CardArchetypeIndex.AdvancedTrainingEncounterId), Repo.Value.GetCard(CardArchetypeIndex.AdvancedTrainingEncounterId)));
        Assert.Contains(catalog.StandaloneEntries, e => e.Card.Id.Equals(CardPool.PolEncounterId, StringComparison.OrdinalIgnoreCase) && e.MinDay == 6);
        Assert.Contains(catalog.StandaloneEntries, e => e.Card.Id.Equals(CardPool.PolPygmalienEncounterId, StringComparison.OrdinalIgnoreCase) && e.MinDay == 4);

        // 严格剔除全部 8 张已淘汰/限时活动/非白昼系统/任务残留卡、Futura、Sprout、Grand Opening 与被引用子事件
        var allCatalogIds = catalog.TieredSuites.SelectMany(s => s.VariantsByTier.Values.Select(c => c.Id))
            .Concat(catalog.DayRangeSuites.SelectMany(s => s.Variants.Select(v => v.Card.Id)))
            .Concat(catalog.StandaloneEntries.Select(e => e.Card.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string forbiddenId in new[]
        {
            "7676de25-e0eb-4aa2-a33c-0570ddf4777a", // 任务残留 1 (The Financial District - Blacksmith Investment)
            "be759478-d828-4bc2-8268-0596f2c731aa", // 任务残留 2 (Furry Return - Large - Pet - Purr)
            "a64974ac-1250-41fe-aada-508b22c93ac4", // 比约恩的礼物交换 (Bjorn's Gift Exchange - Patch 9.2 冬季限时活动)
            "972e6031-4036-47ef-8e6e-1b8feb53fe47", // 地下抵抗组织 (Underground Resistance - 早期赛季末限时活动)
            "8c69a3e3-8c82-4049-aa89-1836ccb6ebc4", // 古奇潜水 (Gulch's Dive - 废案残留)
            CardPool.CoreInitializationEncounterId,  // 核心初始化 (Dooley Lv3 升级专属)
            CardArchetypeIndex.StatiaEncounterId,    // 丝塔奇娅 (Statia - 已更名重做为 Futura)
            CardPool.StarChartTreasureEncounterId,   // 星图宝藏 (Star Chart Treasure - 旧版残留)
            CardPool.SproutEncounterId,              // Sprout (Tiny World 远征子节点)
            CardArchetypeIndex.FuturaEncounterId,
            CardPool.GrandOpeningEncounterId,
            "e6e09e6a-ebe0-46f8-bfbb-716b74f78bc6", // Candy Stash (Day 1-3) — 子事件
        })
        {
            Assert.DoesNotContain(forbiddenId, allCatalogIds);
        }

        // Day 1 投影每套按阶事件仅出现至多 1 个版本，且包含 Borrow (Day 1-2) / Invest in Yourself (Day 1-2) / Study / Snack Time
        var day1Resolved = catalog.ResolveForHeroAndDay(HeroNames.Vanessa, effectiveDay: 1);
        var day1Ids = day1Resolved.Select(x => x.Card.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("036ce025-fc8a-4924-b1bd-a4f6f470ebc7", day1Ids); // Borrow (Day 1-2)
        Assert.Contains("f6430efe-8452-475b-8c8f-5d789b914a3f", day1Ids); // Invest in Yourself (Day 1-2)
        Assert.Contains("ce38f4a2-f156-4082-b8ce-e8956c71fc0d", day1Ids); // Study (Default Option)
        Assert.Contains("e6b0bc78-4284-4bc7-b130-f5d04007ccf6", day1Ids); // Snack Time
        Assert.Contains("fe756e73-bedb-4700-901f-07ef13b3c512", day1Ids); // Regenerative Tincture

        // 每套 TieredSuite 在单日投影中至多命中 1 个阶
        foreach (var suite in catalog.TieredSuites)
        {
            int hits = suite.VariantsByTier.Values.Count(v => day1Ids.Contains(v.Id));
            Assert.InRange(hits, 0, 1);
        }

        // 2. HasPlayableEncounterPayload 必须对直效/纯 XP 卡全部返回 true
        var (checkRun, _) = CreateStartedRun(day: 1, hour: 1);
        var checkShop = new ShopEngine(checkRun, Pool.Value, Repo.Value, checkRun.CustomRules);
        foreach (string directId in new[]
        {
            "036ce025-fc8a-4924-b1bd-a4f6f470ebc7", // Borrow (Day 1-2)
            "f6430efe-8452-475b-8c8f-5d789b914a3f", // Invest in Yourself (Day 1-2)
            "9ddb3457-d4a5-45ec-913e-ae52fce9cb22", // Trophy Hunt (Day 1-3)
            "e6b0bc78-4284-4bc7-b130-f5d04007ccf6", // Snack Time
            "fe756e73-bedb-4700-901f-07ef13b3c512", // Regenerative Tincture
            "ce38f4a2-f156-4082-b8ce-e8956c71fc0d", // Study (Default Option)
        })
        {
            var tpl = Pool.Value.Get(directId);
            Assert.NotNull(tpl);
            Assert.True(checkShop.HasPlayableEncounterPayload(tpl!), $"直效/纯 XP 遭遇 {directId} ({tpl!.InternalName}) 应通过 HasPlayableEncounterPayload");
        }

        // 3. 对齐官方瞬发直效奇遇交互（BoardManager._currentEncounterIsInstant）：选中 Study / Borrow 等无 SelectionContext
        //    与 NextEncounterOnSelection 的直效事件后，立刻结算直效与 XP 并直接由 Choice 推进至下一小时 Choice，绝不落地进空 Encounter 房间。
        {
            var (runStudy, engineStudy) = CreateStartedRun(day: 1, hour: 1, seed: 16001);
            int xpBefore = runStudy.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
            var studyEnc = InjectChoiceEncounter(runStudy, "ce38f4a2-f156-4082-b8ce-e8956c71fc0d");
            var outcome = engineStudy.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(studyEnc.InstanceId) });
            Assert.Null(outcome.Error);
            Assert.Equal(ERunState.Choice, runStudy.State);
            Assert.Equal(2u, runStudy.Hour);
            Assert.Equal(0, runStudy.PendingEncounterExitXp);
            Assert.Equal(xpBefore + 2, runStudy.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0));
            Assert.NotEmpty(runStudy.SelectionSet);
        }
        {
            var (runBorrow, engineBorrow) = CreateStartedRun(day: 1, hour: 1, gold: 10, seed: 16002);
            int goldBefore = runBorrow.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
            int xpBefore = runBorrow.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
            var borrowEnc = InjectChoiceEncounter(runBorrow, "036ce025-fc8a-4924-b1bd-a4f6f470ebc7");
            var outcome = engineBorrow.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(borrowEnc.InstanceId) });
            Assert.Null(outcome.Error);
            Assert.Equal(ERunState.Choice, runBorrow.State);
            Assert.Equal(2u, runBorrow.Hour);
            Assert.True(runBorrow.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0) > goldBefore, "Borrow (Day 1-2) 选中后应立刻执行 TTriggerOnCardSelected 发放金币");
            Assert.Equal(xpBefore + 1, runBorrow.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0));
            Assert.NotEmpty(runBorrow.SelectionSet);
        }
    }

    // ── P1-01：数据驱动 ReplacementSetRegistry（5 个官方 NewSetIdOnExit）与 TActionExitReplacementSet ──

    [Fact]
    public void P1_01_ReplacementSetRegistry_All5OfficialGuids_Persistence_And_Exit()
    {
        // 1. 5 个官方 NewSetIdOnExit GUID 全部注册于 ReplacementSetRegistry
        foreach (string repId in new[]
        {
            CardPool.ReplacementSetCrashSiteId,
            CardPool.ReplacementSetJungleFieryId,
            CardPool.ReplacementSetJungleToxicId,
            CardPool.ReplacementSetJungleObsidianId,
            CardPool.ReplacementSetTinyWorldId,
        })
        {
            Assert.True(ShopEngine.TryGetReplacementSet(repId, out var def) && def != null, $"替换遭遇集 {repId} 未注册");
        }

        // 2. 进入 Jungle Temple Expedition -> 选中 Toxic Door：应写入官方 NewSetIdOnExit (fc9adb79) 且持久化无损
        const string toxicDoorStepId = "c0f1cad2-8561-475a-9e08-a88e8bd62d04";
        var (run, engine) = CreateStartedRun(day: 9, hour: 1, seed: 10101);
        var jungleEnc = InjectChoiceEncounter(run, CardPool.JungleExpeditionRoomId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(jungleEnc.InstanceId) }).Error);
        Assert.Equal(ERunState.Encounter, run.State);

        var toxicStep = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId == Guid.Parse(toxicDoorStepId));
        Assert.NotNull(toxicStep);

        var doorOutcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(toxicStep!.InstanceId) });
        Assert.Null(doorOutcome.Error);
        Assert.Equal(CardPool.ReplacementSetJungleToxicId, run.ActiveReplacementSetId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CardPool.JungleExpeditionRoomId, run.ActiveExpeditionId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Toxic", run.ActiveExpeditionVariant);

        // 序列化往返保留 ActiveReplacementSetId 与 CurrentBoardId
        string json = RunSnapshotSerializer.Serialize(run);
        var restored = RunSnapshotSerializer.Deserialize(json, Repo.Value);
        Assert.Equal(CardPool.ReplacementSetJungleToxicId, restored.ActiveReplacementSetId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Toxic", restored.ActiveExpeditionVariant);

        // 3. 执行 TActionExitReplacementSet (You Awaken Later) 必须清理 ActiveReplacementSetId / ActiveExpeditionId / CurrentBoardId
        const string youAwakenLaterStepId = "1e454a6c-7d4e-43cb-840c-3a6768a07716";
        var (runExit, engineExit) = CreateStartedRun(day: 10, hour: 3, seed: 10102);
        runExit.ActiveReplacementSetId = CardPool.ReplacementSetTinyWorldId;
        runExit.ActiveExpeditionId = CardPool.TinyWorldExpeditionRoomId;
        runExit.CurrentBoardId = "3b249f86c639545b784d8c699cc6edd1";
        runExit.State = ERunState.Loot;
        runExit.SelectionRules = new TSelectionContextRules { CanExit = false, SelectionIsFree = true };
        runExit.SelectionSet.Clear();
        var awakenStep = new RunState.CardEntry
        {
            InstanceId = "stp_awaken",
            TemplateId = Guid.Parse(youAwakenLaterStepId),
            Type = ECardType.EncounterStep,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
        };
        RunState.PopulateMetaFromTemplate(awakenStep, Pool.Value.Get(youAwakenLaterStepId), Repo.Value.GetCard(youAwakenLaterStepId));
        runExit.Cards[awakenStep.InstanceId] = awakenStep;
        runExit.SelectionSet.Add(awakenStep.InstanceId);

        var exitOutcome = engineExit.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(awakenStep.InstanceId) });
        Assert.Null(exitOutcome.Error);
        Assert.Null(runExit.ActiveReplacementSetId);
        Assert.Null(runExit.ActiveExpeditionId);
        Assert.Null(runExit.CurrentBoardId);
        Assert.Contains(exitOutcome.Events, e => e is GameSimEventBoardOverridden bo && bo.Board == null && bo.Presentation == null);
    }

    // ── P1-04：BoardOverride + Presentation 解析、Snapshot.Board 持久化与进出同包发射 GameSimEventBoardOverridden ──

    [Fact]
    public void P1_04_BoardOverride_And_Presentation_Parsed_Synced_Persisted_And_Reset()
    {
        var expectedBoards = new (string RoomId, string BoardId, string Presentation)[]
        {
            (CardPool.TinyWorldExpeditionRoomId, "3b249f86c639545b784d8c699cc6edd1", "BoardTransition_TinyWorld_Data_SO"),
            (CardPool.JungleExpeditionRoomId,    "90aba12db9903584fa42a1f9837ca8c3", "BoardTransition_TempleExpedition_Data_SO.asset"),
            (CardPool.CrashSiteExpeditionRoomId, "015253f62ad7728478c6b3db0badc1bf", "BoardTransition_Default_Data_SO"),
        };

        // 1. Core CardTemplate 与 CardPool.CardTemplate 均完整解析 BoardOverride 与 Presentation
        foreach (var (roomId, expectedBoard, expectedPres) in expectedBoards)
        {
            var coreTpl = Repo.Value.GetCard(roomId);
            var poolTpl = Pool.Value.Get(roomId);
            Assert.NotNull(coreTpl);
            Assert.NotNull(poolTpl);
            Assert.Equal(expectedBoard, coreTpl!.BoardOverride);
            Assert.Equal(expectedPres, coreTpl.Presentation);
            Assert.Equal(expectedBoard, poolTpl!.BoardOverride);
            Assert.Equal(expectedPres, poolTpl.Presentation);
        }

        // 2. 进入 Tiny World Expedition 入口房：同包同时发射 StateTransitioned + BoardOverridden，并写入 Snapshot.Board
        var (run, engine) = CreateStartedRun(day: 10, hour: 1, seed: 10401);
        var twRoom = InjectChoiceEncounter(run, CardPool.TinyWorldExpeditionRoomId);
        var enterOutcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(twRoom.InstanceId) });
        Assert.Null(enterOutcome.Error);
        Assert.Equal("3b249f86c639545b784d8c699cc6edd1", run.CurrentBoardId);
        Assert.Equal("3b249f86c639545b784d8c699cc6edd1", run.BuildSnapshot().CurrentState?.Board);
        Assert.Contains(enterOutcome.Events, e => e is GameSimEventStateTransitioned st && st.ToState == ERunState.Encounter);
        Assert.Contains(enterOutcome.Events, e => e is GameSimEventBoardOverridden bo
            && bo.Board == "3b249f86c639545b784d8c699cc6edd1"
            && bo.Presentation == "BoardTransition_TinyWorld_Data_SO");

        // 快照序列化往返保留 CurrentBoardId 与 BuildSnapshot().CurrentState.Board
        string snapJson = RunSnapshotSerializer.Serialize(run);
        var restored = RunSnapshotSerializer.Deserialize(snapJson, Repo.Value);
        Assert.Equal("3b249f86c639545b784d8c699cc6edd1", restored.CurrentBoardId);
        Assert.Equal("3b249f86c639545b784d8c699cc6edd1", restored.BuildSnapshot().CurrentState?.Board);

        // 3. 离开探险（Hour 5 仍在 Ethergy Nexus 替换集内，Hour 6 PvP 退出替换集）复位 CurrentBoardId=null 并同包发射 GameSimEventBoardOverridden(null, null)
        run.Hour = 5;
        run.ActiveReplacementSetId = CardPool.ReplacementSetTinyWorldId;
        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
        shop.DealHourEncounters();
        Assert.Equal("3b249f86c639545b784d8c699cc6edd1", run.CurrentBoardId);

        run.Hour = 6;
        var exitEvents = shop.DealHourEncounters();
        Assert.Null(run.CurrentBoardId);
        Assert.Null(run.BuildSnapshot().CurrentState?.Board);
        Assert.Contains(exitEvents, e => e is GameSimEventBoardOverridden bo && bo.Board == null && bo.Presentation == null);
        Assert.Contains(exitEvents, e => e is GameSimEventStateTransitioned st && st.ToState == ERunState.Choice);
    }

    // ── P1-05：野怪全量初始属性（HealthRegen / Income / TempoGainCooldownMax / Custom_3 / Custom_4）同步至 OpponentAttributes ──

    [Fact]
    public void P1_05_MonsterInitialAttributes_SyncedToOpponentAttributes_And_CombatSpawnSim()
    {
        var cases = new (string MonsterId, EPlayerAttributeType Attr, int Expected)[]
        {
            ("616ce451-b85d-478a-82a8-8a28105994f5", EPlayerAttributeType.HealthRegen, 20),          // Hulking Experiment
            ("8c211464-5961-449b-b5e9-b222c601e3c1", EPlayerAttributeType.HealthRegen, 10),          // Master Alchemist
            ("ecb2508b-07b1-4712-a82a-fb47c433ed6a", EPlayerAttributeType.Income, 12),               // Loan Shark
            ("6d73917f-7d81-4f73-894d-ade357d034af", EPlayerAttributeType.TempoGainCooldownMax, 1000), // Cortek Music Generator
            ("87365f71-1a50-40ac-b4f6-903177ebebab", EPlayerAttributeType.Custom_3, 50),             // Byg
            ("87365f71-1a50-40ac-b4f6-903177ebebab", EPlayerAttributeType.Custom_4, 15),             // Byg
        };

        foreach (var (monsterId, attrType, expectedVal) in cases)
        {
            var cbtTpl = Pool.Value.OfType("CombatEncounter")
                .FirstOrDefault(c => string.Equals(c.MonsterTemplateId, monsterId, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(cbtTpl);

            var (run, engine) = CreateStartedRun(day: 10, hour: 3, healthMax: 10000, seed: 10500);
            var cbtEntry = InjectChoiceEncounter(run, cbtTpl!.Id, ECardType.CombatEncounter);
            var outcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(cbtEntry.InstanceId) });
            Assert.Null(outcome.Error);
            Assert.NotNull(outcome.CombatSpawnSim);
            Assert.NotNull(outcome.CombatSpawnSim!.Opponent);

            // 验证开战下发给客户端的 CombatSpawnSim.Opponent.Attributes 完整携带初始稀有属性（战后 DespawnMessage 会正常重置 _run.OpponentAttributes）
            Assert.Equal(expectedVal, outcome.CombatSpawnSim.Opponent!.Attributes.GetValueOrDefault(attrType, -1));
            // 确保满血初值不被破坏
            Assert.True(outcome.CombatSpawnSim.Opponent.Attributes[EPlayerAttributeType.HealthMax] > 0);
            Assert.Equal(
                outcome.CombatSpawnSim.Opponent.Attributes[EPlayerAttributeType.HealthMax],
                outcome.CombatSpawnSim.Opponent.Attributes[EPlayerAttributeType.Health]);
        }
    }

    // ── P1-22：The Travel Agent 全 8 英雄 Day 2–5 蒙特卡洛遇见率稳定落于 70%–85%，Day 1 与 Day 6–10 为 0% ──

    [Fact]
    public void P1_22_TheTravelAgent_MonteCarlo_Day2To5_Rate70To85_All8Heroes()
    {
        var taGuid = Guid.Parse(CardPool.TravelAgentEncounterId);
        var allHeroes = new[]
        {
            EHero.Vanessa, EHero.Dooley, EHero.Pygmalien, EHero.Mak,
            EHero.Karnok, EHero.Jules, EHero.Stelle, EHero.Hero8,
        };
        uint[] choiceHours = [1u, 2u, 4u, 5u];
        const int runsPerHero = 1000;

        int[] metCounts = new int[allHeroes.Length];
        Parallel.For(0, allHeroes.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, hIdx =>
        {
            var hero = allHeroes[hIdx];
            int metCount = 0;
            for (int r = 0; r < runsPerHero; r++)
            {
                var run = new RunState
                {
                    RunId = $"mc_{hero}_{r}",
                    GameModeId = Pool.Value.DefaultGameModeId,
                    Hero = hero,
                    Day = 2,
                    Hour = 1,
                    RngSeed = 220000 + (int)hero * 10000 + r,
                    CustomRules = new CustomRunRuleSet(),
                };
                var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
                bool met = false;
                for (uint day = 2; day <= 5 && !met; day++)
                {
                    run.Day = day;
                    run.VisitedDayEncounterIds.Clear();
                    foreach (uint hr in choiceHours)
                    {
                        run.Hour = hr;
                        shop.DealHourEncounters(playerState: null, visitedTodayIds: run.VisitedDayEncounterIds);
                        if (run.SelectionSet.Any(id => run.Cards[id].TemplateId == taGuid))
                        {
                            met = true;
                            break;
                        }
                        foreach (string id in run.SelectionSet)
                            run.VisitedDayEncounterIds.Add(run.Cards[id].TemplateId.ToString());
                    }
                }
                if (met) metCount++;
            }
            metCounts[hIdx] = metCount;
        });

        for (int hIdx = 0; hIdx < allHeroes.Length; hIdx++)
        {
            var hero = allHeroes[hIdx];
            int metCount = metCounts[hIdx];
            // Patch 20.2 + BazaarDB 21.3 万局实测：The Travel Agent Day 2–5 日出现率依次为 4.9% / 2.6% / 2.6% / 1.7%，
            // 4 天累计真实遇见率约 ~11.3%（合理蒙特卡洛验收区间 [6%, 20%]）
            double rate = (double)metCount / runsPerHero;
            Assert.True(
                rate is >= 0.06 and <= 0.20,
                $"英雄 {hero} Day 2–5 The Travel Agent 累计遇见率 = {rate:P2} ({metCount}/{runsPerHero})，偏离 Patch 20.2 官服实测区间 [6%, 20%]");
        }

        // 标准规则下 Day 1 与 Day 6..10 严禁出现 The Travel Agent
        foreach (uint forbiddenDay in new uint[] { 1, 6, 7, 8, 9, 10 })
        {
            var run = new RunState
            {
                RunId = $"ta_forbid_d{forbiddenDay}",
                GameModeId = Pool.Value.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = forbiddenDay,
                Hour = 2,
                RngSeed = (int)(229000 + forbiddenDay),
                CustomRules = new CustomRunRuleSet(),
            };
            var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
            for (int i = 0; i < 150; i++)
            {
                run.VisitedDayEncounterIds.Clear();
                shop.DealHourEncounters();
                Assert.DoesNotContain(run.SelectionSet, id => run.Cards[id].TemplateId == taGuid);
            }
        }
    }

    // ── P1-23：Tiny World Expedition Day 10 严格时间表（H2 Where am I? / H3 Sprout / H4 Bananniboss / H5 Ethergy Nexus / H6 退出） ──

    [Fact]
    public void P1_23_TinyWorldExpedition_StrictSchedule_H2WhereAmI_H3Sprout_H4Bananniboss_H5EthergyNexus()
    {
        var whereAmIGuid = Guid.Parse("875caea4-93c5-440b-a9fd-2886eeaa0fc5");
        var sproutGuid = Guid.Parse(CardPool.SproutEncounterId);
        var banannibossGuid = Guid.Parse("1a3f7d6a-3f24-42dc-ba27-9d4bf199d0b8");
        var barnacleBigwigGuid = Guid.Parse("9f8fa7f6-c322-4ff6-b81f-c379305b97f4");
        var ethergyNexusGuid = Guid.Parse("f418a73c-d442-4ee1-aa7f-ec43f0418d9f");

        var (run, _) = CreateStartedRun(day: 10, hour: 2, seed: 23001);
        run.ActiveReplacementSetId = CardPool.ReplacementSetTinyWorldId;
        run.ActiveExpeditionId = CardPool.TinyWorldExpeditionRoomId;
        run.CurrentBoardId = "3b249f86c639545b784d8c699cc6edd1";
        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);

        // H2 -> Where am I?
        run.Hour = 2;
        shop.DealHourEncounters();
        Assert.Single(run.SelectionSet);
        Assert.Equal(whereAmIGuid, run.Cards[run.SelectionSet[0]].TemplateId);

        // H3 -> Sprout（微型世界专属免费物品池，绝不误发 Barnacle Bigwig）
        run.Hour = 3;
        shop.DealHourEncounters();
        Assert.Single(run.SelectionSet);
        Assert.Equal(sproutGuid, run.Cards[run.SelectionSet[0]].TemplateId);
        Assert.DoesNotContain(run.SelectionSet, id => run.Cards[id].TemplateId == barnacleBigwigGuid);

        // H4 -> 仅 [Tiny World Expedition] Bananniboss，绝不误发 Barnacle Bigwig
        run.Hour = 4;
        shop.DealHourEncounters();
        Assert.Single(run.SelectionSet);
        Assert.Equal(banannibossGuid, run.Cards[run.SelectionSet[0]].TemplateId);
        Assert.DoesNotContain(run.SelectionSet, id => run.Cards[id].TemplateId == barnacleBigwigGuid);

        // H5 -> Ethergy Nexus（最终充能事件，绝不提前退回常规 3 选遭遇）
        run.Hour = 5;
        shop.DealHourEncounters();
        Assert.Single(run.SelectionSet);
        Assert.Equal(ethergyNexusGuid, run.Cards[run.SelectionSet[0]].TemplateId);
        Assert.Equal(CardPool.ReplacementSetTinyWorldId, run.ActiveReplacementSetId);

        // H6 -> 自然退出替换集、复位棋盘并进入当日 PvP
        run.Hour = 6;
        var h6Events = shop.DealHourEncounters();
        Assert.Null(run.ActiveReplacementSetId);
        Assert.Null(run.ActiveExpeditionId);
        Assert.Null(run.CurrentBoardId);
        Assert.Contains(h6Events, e => e is GameSimEventBoardOverridden bo && bo.Board == null && bo.Presentation == null);
        Assert.Single(run.SelectionSet);
    }

    // ── P1-24：Crash Site Expedition Day 6 严格线性时间表（H2 晶核 -> H3 三 Boss -> H4 Command Nexus -> H5 Cargo Hold） ──

    [Fact]
    public void P1_24_CrashSiteExpedition_StrictLinearSchedule_H2ToH5()
    {
        var crystalGrowthGuid = Guid.Parse("2950afee-ce0e-40c1-becb-735570e560c5");
        var commandNexusGuid = Guid.Parse("54a0e045-36fe-4de4-92fb-9254fa73c48f");
        var cargoHoldGuid = Guid.Parse("07139793-c844-4fbb-ad90-9f279843cdc8");
        var bossGuids = new HashSet<Guid>
        {
            Guid.Parse("10504dc3-99cf-46be-b8ac-ea0be24c7b0b"), // [Crash Site Expedition] Augmented Hellbilly
            Guid.Parse("7718437a-cc2e-44d0-b3f1-4a370ea84fdc"), // [Crash Site Expedition] Conspiracy Theorist
            Guid.Parse("4ef60802-3b84-482a-8ba5-640534a94c64"), // [Crash Site Expedition] The Vessel
        };

        var (run, _) = CreateStartedRun(day: 6, hour: 2, seed: 24001);
        run.ActiveReplacementSetId = CardPool.ReplacementSetCrashSiteId;
        run.ActiveExpeditionId = CardPool.CrashSiteExpeditionRoomId;
        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);

        // H2 -> 仅 Crystal Growth Chamber（严禁跳过提前给 Cargo Hold）
        run.Hour = 2;
        shop.DealHourEncounters();
        Assert.Single(run.SelectionSet);
        Assert.Equal(crystalGrowthGuid, run.Cards[run.SelectionSet[0]].TemplateId);
        Assert.DoesNotContain(run.SelectionSet, id => run.Cards[id].TemplateId == cargoHoldGuid);

        // H3 -> 3 个 Crash Site Boss
        run.Hour = 3;
        shop.DealHourEncounters();
        Assert.Equal(3, run.SelectionSet.Count);
        Assert.All(run.SelectionSet, id =>
        {
            Assert.Equal(ECardType.CombatEncounter, run.Cards[id].Type);
            Assert.Contains(run.Cards[id].TemplateId, bossGuids);
        });

        // H4 -> 仅 Command Nexus
        run.Hour = 4;
        shop.DealHourEncounters();
        Assert.Single(run.SelectionSet);
        Assert.Equal(commandNexusGuid, run.Cards[run.SelectionSet[0]].TemplateId);

        // H5 -> 仅 Cargo Hold
        run.Hour = 5;
        shop.DealHourEncounters();
        Assert.Single(run.SelectionSet);
        Assert.Equal(cargoHoldGuid, run.Cards[run.SelectionSet[0]].TemplateId);
    }

    // ── P1-25：过期探险门票判定与全来源（Browse All Tickets / Surprise Me / Jungle Ruins）过滤 ──

    [Fact]
    public void P1_25_ExpiredExpeditionTickets_Predicate_And_AllGrantSourcesFiltered()
    {
        // 1. 边界判定核验：Day 5 Hour 5 / Day 6 Hour 0 均未过期；Day 6 Hour 1 起 CrashSite 过期；Day 9 Hour 1 起 Jungle 过期；Day 10 Hour 1 起全过期
        Assert.False(CardPool.IsExpeditionTicketExpired(CardPool.CrashSiteExpeditionTicketId, 5, 5));
        Assert.False(CardPool.IsExpeditionTicketExpired(CardPool.CrashSiteExpeditionTicketId, 6, 0));
        Assert.True(CardPool.IsExpeditionTicketExpired(CardPool.CrashSiteExpeditionTicketId, 6, 1));
        Assert.False(CardPool.IsExpeditionTicketExpired(CardPool.JungleExpeditionTicketId, 6, 1));
        Assert.False(CardPool.IsExpeditionTicketExpired(CardPool.JungleExpeditionTicketId, 9, 0));
        Assert.True(CardPool.IsExpeditionTicketExpired(CardPool.JungleExpeditionTicketId, 9, 1));
        Assert.False(CardPool.IsExpeditionTicketExpired(CardPool.TinyWorldExpeditionTicketId, 9, 1));
        Assert.False(CardPool.IsExpeditionTicketExpired(CardPool.TinyWorldExpeditionTicketId, 10, 0));
        Assert.True(CardPool.IsExpeditionTicketExpired(CardPool.TinyWorldExpeditionTicketId, 10, 1));

        const string browseAllStepId = "cb0fd755-c9d9-4e2b-9096-de13c3773d30";
        const string surpriseMeStepId = "32dd98de-811c-45bc-98ab-9e416ccefba7";
        const string jungleRuinsGrabLootStepId = "e73e7956-1875-4f40-b5f2-d3c4ed975751";

        var crashTicketGuid = Guid.Parse(CardPool.CrashSiteExpeditionTicketId);
        var jungleTicketGuid = Guid.Parse(CardPool.JungleExpeditionTicketId);
        var tinyTicketGuid = Guid.Parse(CardPool.TinyWorldExpeditionTicketId);

        // 2. Browse All Tickets：Day 6 H1 仅含 Jungle + TinyWorld；Day 9 H1 仅含 TinyWorld；Day 10 H1 不发任何过期门票且不退化为全库随机物品
        {
            var (run6, engine6) = CreateStartedRun(day: 6, hour: 1, gold: 50, seed: 25001);
            var step6 = InjectStepIntoEncounter(run6, CardPool.TravelAgentEncounterId, browseAllStepId);
            Assert.Null(engine6.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step6.InstanceId) }).Error);
            var dealt6 = run6.SelectionSet.Select(id => run6.Cards[id].TemplateId).ToHashSet();
            Assert.DoesNotContain(crashTicketGuid, dealt6);
            Assert.Contains(jungleTicketGuid, dealt6);
            Assert.Contains(tinyTicketGuid, dealt6);
        }
        {
            var (run9, engine9) = CreateStartedRun(day: 9, hour: 1, gold: 50, seed: 25002);
            var step9 = InjectStepIntoEncounter(run9, CardPool.TravelAgentEncounterId, browseAllStepId);
            Assert.Null(engine9.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step9.InstanceId) }).Error);
            var dealt9 = run9.SelectionSet.Select(id => run9.Cards[id].TemplateId).ToHashSet();
            Assert.DoesNotContain(crashTicketGuid, dealt9);
            Assert.DoesNotContain(jungleTicketGuid, dealt9);
            Assert.Contains(tinyTicketGuid, dealt9);
        }
        {
            var (run10, engine10) = CreateStartedRun(day: 10, hour: 1, gold: 50, seed: 25003);
            var step10 = InjectStepIntoEncounter(run10, CardPool.TravelAgentEncounterId, browseAllStepId);
            Assert.Null(engine10.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step10.InstanceId) }).Error);
            foreach (var c in run10.Cards.Values.Where(x => x.Owner == ECombatantId.Player || run10.SelectionSet.Contains(x.InstanceId)))
            {
                Assert.False(CardPool.IsExpeditionTicketExpired(c.TemplateId.ToString(), 10, 1),
                    $"Day 10 Hour 1 Browse All Tickets 绝不可发放过期门票 {c.TemplateId}");
            }
            Assert.Equal(ERunState.Choice, run10.State);
            Assert.DoesNotContain(run10.Cards.Values, c => c.Owner == ECombatantId.Player && CardPool.IsExpeditionTicketTemplate(c.TemplateId.ToString()));
        }

        // 3. Surprise Me 与 [Jungle Ruins] Grab the Loot：在 Day 6 / Day 9 / Day 10 多次抽选绝不发放任何过期门票
        foreach (string stepId in new[] { surpriseMeStepId, jungleRuinsGrabLootStepId })
        {
            foreach (uint testDay in new uint[] { 6, 9, 10 })
            {
                for (int s = 0; s < 12; s++)
                {
                    var (run, engine) = CreateStartedRun(day: testDay, hour: 1, gold: 50, seed: (int)(25100 + testDay * 100 + s));
                    var step = InjectStepIntoEncounter(run, CardPool.TravelAgentEncounterId, stepId);
                    Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step.InstanceId) }).Error);
                    foreach (var c in run.Cards.Values.Where(x => x.Owner == ECombatantId.Player || run.SelectionSet.Contains(x.InstanceId)))
                    {
                        Assert.False(
                            CardPool.IsExpeditionTicketExpired(c.TemplateId.ToString(), (int)testDay, 1),
                            $"步骤 {stepId} 在 Day {testDay} Hour 1 违规发出了过期门票 {c.TemplateId}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Patch20_2_RetiredEventsExcluded_SecondaryPoolsGuarded_HeroPartitionStrict_And_PolRestored()
    {
        var retiredGuids = new HashSet<Guid>
        {
            Guid.Parse("a64974ac-1250-41fe-aada-508b22c93ac4"), // Bjorn's Gift Exchange
            Guid.Parse("972e6031-4036-47ef-8e6e-1b8feb53fe47"), // Underground Resistance
            Guid.Parse("8c69a3e3-8c82-4049-aa89-1836ccb6ebc4"), // Gulch's Dive
            Guid.Parse(CardPool.CoreInitializationEncounterId), // Core Initialization
            Guid.Parse(CardArchetypeIndex.StatiaEncounterId),   // Statia
            Guid.Parse(CardPool.StarChartTreasureEncounterId),  // Star Chart Treasure
            Guid.Parse("7676de25-e0eb-4aa2-a33c-0570ddf4777a"), // Blacksmith Investment
            Guid.Parse("be759478-d828-4bc2-8268-0596f2c731aa"), // Furry Return
            Guid.Parse("9ddb3457-d4a5-45ec-913e-ae52fce9cb22"), // Trophy Hunt (Day 1-3)
            Guid.Parse("58e800bd-a2ae-4135-a8c3-2eca839c17d2"), // Trophy Hunt (Day 4-6)
            Guid.Parse("754ff658-3032-46ab-91f3-a2ce3a1cb1da"), // Trophy Hunt (Day 7-9)
        };

        var all8Heroes = new[]
        {
            EHero.Vanessa, EHero.Dooley, EHero.Pygmalien, EHero.Mak,
            EHero.Karnok, EHero.Jules, EHero.Stelle, EHero.Hero8,
        };

        // 1. 全 8 英雄 × Day 1–10 高频抽样：严禁刷出任何已淘汰/限时/系统事件，且每张发出的遭遇严格匹配当前英雄或 Common
        var polNormalGuid = Guid.Parse(CardPool.PolEncounterId);
        var polPygGuid = Guid.Parse(CardPool.PolPygmalienEncounterId);
        bool sawPolPyg = false;
        bool sawPolNormal = false;

        foreach (var hero in all8Heroes)
        {
            string heroName = hero.ToString();
            var run = new RunState
            {
                RunId = $"hero_part_{heroName}",
                GameModeId = Pool.Value.DefaultGameModeId,
                Hero = hero,
                Day = 1,
                Hour = 2,
                RngSeed = 20260 + (int)hero,
                CustomRules = new CustomRunRuleSet(),
            };
            var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);

            for (uint d = 1; d <= 10; d++)
            {
                run.Day = d;
                run.Hour = 2;
                int trials = (hero == EHero.Pygmalien && d == 5) || (hero == EHero.Vanessa && d == 7) ? 120 : 12;
                for (int t = 0; t < trials; t++)
                {
                    run.VisitedDayEncounterIds.Clear();
                    run.VisitedSingleSpawnEncounterIds.Clear();
                    shop.DealHourEncounters();

                    foreach (string instId in run.SelectionSet)
                    {
                        var card = run.Cards[instId];
                        Assert.DoesNotContain(card.TemplateId, retiredGuids);

                        var tpl = Pool.Value.Get(card.TemplateId.ToString());
                        Assert.NotNull(tpl);
                        Assert.True(
                            tpl!.Heroes.Contains(heroName, StringComparer.OrdinalIgnoreCase) ||
                            tpl.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase),
                            $"跨英雄泄漏！英雄 {heroName} 在 Day {d} 刷到了仅属 [{string.Join(",", tpl.Heroes)}] 的遭遇 {tpl.InternalName} ({tpl.Id})");

                        if (hero == EHero.Pygmalien && card.TemplateId == polPygGuid) sawPolPyg = true;
                        if (hero == EHero.Vanessa && card.TemplateId == polNormalGuid) sawPolNormal = true;
                        if (hero == EHero.Hero8)
                        {
                            Assert.NotEqual(polNormalGuid, card.TemplateId);
                            Assert.NotEqual(polPygGuid, card.TemplateId);
                        }
                    }
                }
            }
        }

        Assert.True(sawPolPyg, "Pygmalien 在 Day 4+ 应能正常刷到官方大件商人 Pol (Pygmalien)");
        Assert.True(sawPolNormal, "Vanessa 等 6 英雄在 Day 6+ 应能正常刷到官方大件商人 Pol");

        // 2. 次级动态召唤池（Aerodrome / Mysterious Portal）同样严格拦截所有已淘汰/系统遭遇且严守英雄归属
        const string aerodromeId = "f1302d1d-24e4-4d17-a434-3550af908ba3";
        foreach (string portalId in new[] { aerodromeId, CardArchetypeIndex.MysteriousPortalEncounterId })
        {
            var pRun = new RunState
            {
                RunId = $"sec_pool_{portalId[..8]}",
                GameModeId = Pool.Value.DefaultGameModeId,
                Hero = EHero.Dooley,
                Day = 8,
                Hour = 2,
                RngSeed = 20299,
                CustomRules = new CustomRunRuleSet(),
            };
            var pShop = new ShopEngine(pRun, Pool.Value, Repo.Value, pRun.CustomRules);
            for (int i = 0; i < 60; i++)
            {
                pShop.DealEncounterShelf(portalId, isReroll: i > 0);
                foreach (string instId in pRun.SelectionSet)
                {
                    var card = pRun.Cards[instId];
                    if (card.Type is not (ECardType.EventEncounter or ECardType.PedestalEncounter))
                        continue;
                    var tpl = Pool.Value.Get(card.TemplateId.ToString());
                    Assert.False(
                        CardPool.IsExcludedFromDynamicEncounterPool(card.TemplateId.ToString(), tpl?.InternalName, tpl),
                        $"次级池 {portalId} 违规刷出禁用/已淘汰遭遇 {tpl?.InternalName} ({card.TemplateId})");
                    if (tpl != null)
                    {
                        Assert.True(
                            tpl.Heroes.Contains("Dooley", StringComparer.OrdinalIgnoreCase) ||
                            tpl.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase),
                            $"次级池 {portalId} 跨英雄泄漏给 Dooley：{tpl.InternalName} [{string.Join(",", tpl.Heroes)}]");
                    }
                }
            }
        }
    }

    [Fact]
    public void SameDayOfferedEncounters_NeverRepeatEvenWhenSkipped_And_PvpGhostPoolPrioritizesRealBuilds()
    {
        // 1. 同日刷出即锁定（含跳过未点进去的左槽商人，如收容所谢尔比）：连续 20 局 × Day 1..5 跨 Hour 1,2,4,5 绝不重复
        for (int trial = 0; trial < 20; trial++)
        {
            var run = new RunState
            {
                RunId = $"dedup_offered_{trial}",
                GameModeId = Pool.Value.DefaultGameModeId,
                Hero = (EHero)((trial % 8) + 1),
                Day = (uint)(1 + (trial % 5)),
                Hour = 1,
                RngSeed = 77001 + trial * 19,
                CustomRules = new CustomRunRuleSet(),
            };
            var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
            var seenMerchantsToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNormalizedKeysToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 模拟玩家全程不把左槽商人写进 VisitedDayEncounterIds（只跳过不点！）
            foreach (uint hr in new uint[] { 1, 2, 4, 5 })
            {
                run.Hour = hr;
                shop.DealHourEncounters(playerState: null, visitedTodayIds: run.VisitedDayEncounterIds);
                Assert.True(run.SelectionSet.Count >= 3);

                foreach (string instId in run.SelectionSet)
                {
                    var card = run.Cards[instId];
                    string tid = card.TemplateId.ToString();
                    var tpl = Pool.Value.Get(tid);
                    string normKey = OfficialEncounterSchedule.NormalizeKey(tpl?.InternalName);

                    if (tpl != null && CardArchetypeIndex.IsMerchantEncounterTemplate(tpl.Id, tpl.InternalName, tpl, Repo.Value.GetCard(tid)))
                    {
                        Assert.True(
                            seenMerchantsToday.Add(tid),
                            $"Day {run.Day} Hero={run.Hero} Hour {hr} 出现同日重复商人 {tpl.InternalName} ({tid})！");
                    }
                    if (!string.IsNullOrEmpty(normKey))
                    {
                        Assert.True(
                            seenNormalizedKeysToday.Add(normKey),
                            $"Day {run.Day} Hero={run.Hero} Hour {hr} 出现同日重复遭遇套系 {tpl?.InternalName} (key={normKey})！");
                    }
                }
            }

            // 快照序列化往返必须完整保留 OfferedDayEncounterIds 与游标
            string snap = RunSnapshotSerializer.Serialize(run);
            Assert.True(RunSnapshotSerializer.TryDeserialize(snap, Repo.Value, out var restored, out _));
            Assert.Equal(run.OfferedDayEncounterIds.Count, restored!.OfferedDayEncounterIds.Count);
            Assert.Equal(run.OfferedDayEncounterDay, restored.OfferedDayEncounterDay);
            Assert.Equal(run.OfferedDayEncounterLastHour, restored.OfferedDayEncounterLastHour);
        }

        // 2. PvpGhostPool 加载期硬剔演菜 AI 人格，且 Day 1..14 全天期压倒性优先抽中真实玩家盘面
        var realGhosts = TestInfrastructure.SharedGameDataFixture.Shared.Ghosts;
        Assert.True(realGhosts.Count >= 10000, $"升级后的镜像池应 >= 10,000 条，实际 {realGhosts.Count}");
        var rng = new Random(20261123);
        int realCommHits = 0;
        int totalSamples = 0;
        for (int day = 1; day <= 14; day++)
        {
            for (int k = 0; k < 20; k++)
            {
                var opp = realGhosts.PickOpponent(day, playerWins: day / 2, playerLosses: 1, rng, Repo.Value);
                totalSamples++;
                Assert.NotEqual("CluelessNewbie", opp.Persona);
                Assert.NotEqual("GreedyBust", opp.Persona);
                Assert.NotEqual("MessyHybrid", opp.Persona);
                Assert.NotEqual("Casual", opp.Persona);
                if (opp.IsRealCommunityGhost) realCommHits++;
            }
        }
        Assert.True(
            realCommHits >= totalSamples * 0.95,
            $"Day 1..14 全天期真实玩家盘面命中率应 >= 95%，实际 {realCommHits}/{totalSamples}");
    }

    private static RunState.CardEntry InjectStepIntoEncounter(RunState run, string encounterId, string stepTemplateId)
    {
        var poolTpl = Pool.Value.Get(stepTemplateId);
        var coreTpl = Repo.Value.GetCard(stepTemplateId);
        var step = new RunState.CardEntry
        {
            InstanceId = "stp_" + Guid.NewGuid().ToString("N")[..8],
            TemplateId = Guid.Parse(stepTemplateId),
            Type = ECardType.EncounterStep,
            Size = ECardSize.Medium,
            Tier = ShopEngine.ParseTier(poolTpl?.StartingTier ?? coreTpl?.StartingTier),
        };
        RunState.PopulateMetaFromTemplate(step, poolTpl, coreTpl);
        run.State = ERunState.Encounter;
        run.CurrentEncounterId = encounterId;
        run.SelectionRules = new TSelectionContextRules { SelectionIsFree = true, CanSelectMultiple = false, CanExit = true };
        run.SelectionSet.Clear();
        run.Cards[step.InstanceId] = step;
        run.SelectionSet.Add(step.InstanceId);
        return step;
    }
}
