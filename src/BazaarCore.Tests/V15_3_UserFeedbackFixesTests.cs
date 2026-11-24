using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using BazaarServer.Storage;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V15.3 用户反馈 6 项问题服务端与核心引擎回归测试套件：
///   - 问题 1：商店后置过滤自动回补满槽、显式 IdList 选项不被缺简中翻译误杀（The Travel Agent）
///   - 问题 3：废弃老“获得3件怪物战利品”事件（Trophy Hunt Day 1-3/4-6/7-9）与远征子节点 Sprout 永不进入小时池
///   - 问题 4：多战利品事件/步骤/卖卡与单 ID 多发（Collect Spare Change 4）在空手牌/剩1格/满手牌均全额发放
///   - 问题 5A：Day 1~10 PvP 对手符合正常每日升级分布（不再一刀切锁死 Day 1 Lv1）
///   - 问题 6：Resume 恢复事件流前置下发 GameSimEventSocketsUnlocked，且 SQLite 持久化往返 100% 保留手牌/仓库/技能
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class V15_3_UserFeedbackFixesTests
{
    private sealed class SharedData
    {
        public string DataDir = "";
        public GameDataRepository Repo = null!;
        public CardPool Pool = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters = null!;
        public LevelUpTable LevelUps = null!;
        public GameModeConfig Mode = null!;
        public PvpGhostPool Ghosts = null!;
        public CardArchetypeIndex Arch = null!;
    }

    private static string ResolveDataDir()
        => TestInfrastructure.TestDataLocator.ResolveDataDir();

    private static readonly Lazy<SharedData> Fx = new(() =>
    {
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        return new SharedData
        {
            DataDir = shared.DataDir,
            Repo = shared.Repo,
            Pool = shared.Pool,
            Monsters = shared.CreateMonstersCopy(),
            LevelUps = shared.LevelUps,
            Mode = shared.Mode,
            Ghosts = shared.Ghosts,
            Arch = shared.ArchetypeIndex,
        };
    });

    [Fact]
    public void Issue1_MerchantShelfBackfillsWhenPlayerOwnsHigherTierItems()
    {
        var fx = Fx.Value;
        // 选取标准 3 槽物品商人（如 Mittel / Jay Jay / Pol 等 Always 商人）
        var merchant = fx.Pool.OfType("EventEncounter")
            .FirstOrDefault(e =>
                string.Equals(e.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase) &&
                CardArchetypeIndex.IsMerchantEncounterTemplate(e.Id, e.InternalName, e, fx.Repo.GetCard(e.Id)) &&
                fx.Repo.GetCard(e.Id)?.SelectionContext?.SpawnContext?.Limit?.GetFixedNumericValue() == 3.0);
        Assert.NotNull(merchant);

        // 模拟玩家已持有该英雄大量 Silver 物品（触发 IsBlockedByOwnedMaxTiers 后置过滤）
        var vanessaItems = fx.Repo.CardsById.Values
            .Where(c => c.Type == "Item" &&
                        string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase) &&
                        c.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase) &&
                        c.Size == "Small")
            .Take(25)
            .ToList();
        var ownedMaxTiers = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in vanessaItems)
            ownedMaxTiers[it.Id] = CardTier.Silver;

        for (int trial = 0; trial < 15; trial++)
        {
            var run = new RunState
            {
                RunId = $"v153_shop_{trial}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = 2,
                Hour = 2,
                State = ERunState.Choice,
                UnlockedSlots = 0b0111111110,
                StartSelectionDone = true,
                RngSeed = 7000 + trial,
            };
            run.PlayerAttributes[EPlayerAttributeType.Gold] = 50;
            run.PlayerAttributes[EPlayerAttributeType.Level] = 3;
            var shop = new ShopEngine(run, fx.Pool, fx.Repo);
            var ps = new PlayerState("p1", "Player", "Vanessa", 400, 50, 0, 8) { Level = 3 };

            var evs = shop.DealEncounterShelf(merchant!.Id, isReroll: false, playerState: ps, ownedMaxTiers: ownedMaxTiers);
            var dealt = evs.OfType<GameSimEventCardDealt>().ToList();
            Assert.Equal(3, dealt.Count);
        }
    }

    [Fact]
    public void Issue1_EncounterStepDealCardsBackfillsWhenMerchantItemsBlockedByOwnedMaxTiers()
    {
        var fx = Fx.Value;
        // 验证常规 3 选项奇遇（如 The Docks / Jungle Ruins）与 3 槽商店在玩家已有高阶物品时不会漏掉选项或货架空缺
        const string travelAgentId = CardPool.TravelAgentEncounterId;
        const string surpriseMeId = "32dd98de-811c-45bc-98ab-9e416ccefba7";

        var run = new RunState
        {
            RunId = "v153_travel_agent",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 5,
            Hour = 2,
            State = ERunState.Choice,
            UnlockedSlots = 0b1111111111,
            StartSelectionDone = true,
        };
        run.PlayerAttributes[EPlayerAttributeType.Gold] = 50;
        var shop = new ShopEngine(run, fx.Pool, fx.Repo);
        var ps = new PlayerState("p1", "Player", "Vanessa", 800, 50, 0, 10) { Level = 6 };
        var evs = shop.DealEncounterShelf(travelAgentId, isReroll: false, playerState: ps);
        var dealtTpls = evs.OfType<GameSimEventCardDealt>().Select(e => e.TemplateId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 严守项目汉化门禁（V7ShopExpeditionTests）：已汉化的 Surprise Me 正常发放，未汉化卡不偷跑
        Assert.Contains(surpriseMeId, dealtTpls);
    }

    [Fact]
    public void Issue3_ObsoleteTrophyHuntAndSproutExcludedFromHourlyCatalog()
    {
        var fx = Fx.Value;
        string[] forbiddenIds =
        {
            "9ddb3457-d4a5-45ec-913e-ae52fce9cb22", // Trophy Hunt (Day 1-3)
            "58e800bd-a2ae-4135-a8c3-2eca839c17d2", // Trophy Hunt (Day 4-6)
            "754ff658-3032-46ab-91f3-a2ce3a1cb1da", // Trophy Hunt (Day 7-9)
            "5e1ac00b-1abc-44cb-8f9c-6755327580c5", // Sprout (Day 10 Tiny World 远征子节点)
        };

        var catalog = fx.Pool.GetGuidOnlyHourlyCatalog();
        var allCatalogIds = catalog.TieredSuites.SelectMany(s => s.VariantsByTier.Values.Select(v => v.Id))
            .Concat(catalog.DayRangeSuites.SelectMany(s => s.Variants.Select(v => v.Card.Id)))
            .Concat(catalog.StandaloneEntries.Select(e => e.Card.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string id in forbiddenIds)
            Assert.DoesNotContain(id, allCatalogIds);

        // 在全英雄 Day 1~10 的实际小时发牌中亦永不出现
        var heroes = new[] { EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak, EHero.Stelle, EHero.Jules, EHero.Karnok };
        foreach (var hero in heroes)
        {
            for (int day = 1; day <= 10; day++)
            {
                var run = new RunState
                {
                    RunId = $"v153_excl_{hero}_{day}",
                    GameModeId = fx.Pool.DefaultGameModeId,
                    Hero = hero,
                    Day = (uint)day,
                    Hour = 2,
                    State = ERunState.Choice,
                    StartSelectionDone = true,
                    RngSeed = day * 100 + (int)hero,
                };
                var shop = new ShopEngine(run, fx.Pool, fx.Repo);
                var evs = shop.DealHourEncounters();
                foreach (var dl in evs.OfType<GameSimEventCardDealt>())
                {
                    Assert.DoesNotContain(dl.TemplateId, forbiddenIds, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Theory]
    [InlineData("401bca74-0dc2-442c-a0d6-1a674bd63c9f", 2)] // Free Samples (2 Bronze Loot)
    [InlineData("13eac6e3-a43a-4b09-8bab-a722ae1ac0c3", 2)] // Regular Set (2 Silver Loot)
    [InlineData("c44fe8a3-3e9c-4e91-b76c-83c38c9dc7e4", 2)] // Premium Set (2 Gold Loot)
    [InlineData("9b3a3d54-f958-4151-9849-9a5a5e46eff3", 3)] // Scavenger Hunt (3 Loot)
    [InlineData("11f652c7-3e03-4ed2-9370-66e828f8d3f0", 2)] // Sharpening Stone (Level Up) (2)
    [InlineData("f5e5e60a-6265-4883-8ca8-9f74ad5e7dd2", 2)] // Scrap (Level Up) (2)
    [InlineData("956ca346-2e58-4060-ac50-a13a8fc153e8", 3)] // Forage (3 citrus/coconut)
    [InlineData("d7c85eed-8511-4327-afaf-bce3bc182232", 4)] // Collect Spare Change (4)
    public void Issue4_MultiSpawnStepsGrantFullCountAcrossHandOccupancyModes(string stepId, int expectedCount)
    {
        var fx = Fx.Value;
        var fillerTpl = fx.Repo.CardsById.Values.First(c =>
            c.Type == "Item" && c.Size == "Small" &&
            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase));

        foreach (int fillCount in new[] { 0, 5, 6 }) // Empty / 1FreeHand / FullHand (6 unlocked slots)
        {
            var run = new RunState
            {
                RunId = $"v153_ml_{stepId[..6]}_{fillCount}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = 3,
                Hour = 2,
                State = ERunState.Encounter,
                UnlockedSlots = 0b0011111100, // 6 个已解锁手牌槽（Socket_2..7）
                StartSelectionDone = true,
            };
            run.PlayerAttributes[EPlayerAttributeType.Gold] = 50;
            run.PlayerAttributes[EPlayerAttributeType.Level] = 4;
            run.HandGrid.UnlockedMask = run.UnlockedSlots;
            run.StashGrid.UnlockedMask = 1023;

            for (int fi = 0; fi < fillCount; fi++)
            {
                var fci = new CardInstance(fillerTpl, CardTier.Bronze, null, $"fill_{fi}");
                Assert.True(run.HandGrid.TryAddAuto(fci));
                run.Cards[fci.InstanceId] = new RunState.CardEntry
                {
                    InstanceId = fci.InstanceId,
                    TemplateId = Guid.Parse(fillerTpl.Id),
                    Type = ECardType.Item,
                    Size = ECardSize.Small,
                    Tier = ETier.Bronze,
                    Owner = ECombatantId.Player,
                    Section = EInventorySection.Hand,
                    Socket = (EContainerSocketId)fci.StartSlot,
                    CoreCard = fci,
                };
            }

            var eng = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, fx.Arch);
            var stepEntry = new RunState.CardEntry
            {
                InstanceId = "stp_multi_1",
                TemplateId = Guid.Parse(stepId),
                Type = ECardType.EncounterStep,
                Size = ECardSize.Medium,
                Tier = ETier.Bronze,
                Owner = null,
            };
            run.Cards[stepEntry.InstanceId] = stepEntry;
            run.SelectionSet.Add(stepEntry.InstanceId);

            var outcome = eng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId("stp_multi_1") });
            Assert.Null(outcome.Error);

            var spawned = outcome.Events.OfType<GameSimEventCardSpawned>().ToList();
            var newOwned = run.Cards.Values.Where(c => c.Owner == ECombatantId.Player && !c.InstanceId.StartsWith("fill_")).ToList();
            Assert.Equal(expectedCount, spawned.Count);
            Assert.Equal(expectedCount, newOwned.Count);
            Assert.All(newOwned, card => Assert.NotNull(card.Socket));
        }
    }

    [Fact]
    public void Issue5A_PvpOpponentsMatchLeveledUpProgressionFromDay1()
    {
        var fx = Fx.Value;
        var rng = new Random(202604);

        // Day 1 应包含已升到 Lv2 的正常对手（不再被硬锁在 Level<=1）
        int day1Lv2Count = 0;
        for (int i = 0; i < 40; i++)
        {
            var g = fx.Ghosts.PickOpponent(day: 1, playerWins: 0, playerLosses: 0, rng, fx.Repo);
            Assert.NotNull(g);
            Assert.NotEmpty(g!.Items);
            Assert.InRange(g.Level, 1, 2);
            if (g.Level == 2) day1Lv2Count++;
        }
        Assert.True(day1Lv2Count > 0, "Day 1 PvP 对手应包含正常升级到 Lv2 的对手");

        // Day 2 应主要匹配已升到 Lv3 的对手，而非被迫偏向未升级的 Weak 桶
        int day2Lv3Count = 0;
        for (int i = 0; i < 40; i++)
        {
            var g = fx.Ghosts.PickOpponent(day: 2, playerWins: 1, playerLosses: 0, rng, fx.Repo);
            Assert.NotNull(g);
            Assert.NotEmpty(g!.Items);
            if (g.Level >= 3) day2Lv3Count++;
        }
        Assert.True(day2Lv3Count >= 20, $"Day 2 正常对手中 Lv3+ 应占多数，实际={day2Lv3Count}/40");
    }

    [Fact]
    public void Issue6_ResumeEventsUnlockAllSocketsBeforeSpawningOuterSlotItemsAndPreserveSkills()
    {
        var fx = Fx.Value;
        string tmpDb = Path.Combine(Path.GetTempPath(), $"v153_resume_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Persistence(tmpDb);
            var run = new RunState
            {
                RunId = "v153_resume_run",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = 5,
                Hour = 3,
                State = ERunState.Choice,
                UnlockedSlots = 0b1111111111, // 全 10 槽解锁（含最左 Socket_0 与最右 Socket_9）
                StartSelectionDone = true,
            };
            run.PlayerAttributes[EPlayerAttributeType.Level] = 6;
            run.PlayerAttributes[EPlayerAttributeType.Gold] = 25;
            run.HandGrid.UnlockedMask = run.UnlockedSlots;
            run.StashGrid.UnlockedMask = 1023;

            var smallItems = fx.Repo.CardsById.Values
                .Where(c => c.Type == "Item" && c.Size == "Small" &&
                            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToList();
            var skills = fx.Repo.CardsById.Values
                .Where(c => c.Type == "Skill" &&
                            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .ToList();

            // 在 Socket_0、Socket_1、Socket_8、Socket_9（外侧槽位）以及 Stash 各放入物品
            int[] handSlots = { 0, 1, 4, 8, 9 };
            for (int i = 0; i < handSlots.Length; i++)
            {
                var tpl = smallItems[i];
                var ci = new CardInstance(tpl, CardTier.Silver, null, $"hand_{i}");
                Assert.True(run.HandGrid.TryPlaceOrShift(ci, handSlots[i]));
                run.Cards[ci.InstanceId] = new RunState.CardEntry
                {
                    InstanceId = ci.InstanceId,
                    TemplateId = Guid.Parse(tpl.Id),
                    Type = ECardType.Item,
                    Size = ECardSize.Small,
                    Tier = ETier.Silver,
                    Owner = ECombatantId.Player,
                    Section = EInventorySection.Hand,
                    Socket = (EContainerSocketId)handSlots[i],
                    CoreCard = ci,
                };
            }
            for (int i = 0; i < 3; i++)
            {
                var tpl = smallItems[5 + i];
                var ci = new CardInstance(tpl, CardTier.Bronze, null, $"stash_{i}");
                Assert.True(run.StashGrid.TryPlaceOrShift(ci, i));
                run.Cards[ci.InstanceId] = new RunState.CardEntry
                {
                    InstanceId = ci.InstanceId,
                    TemplateId = Guid.Parse(tpl.Id),
                    Type = ECardType.Item,
                    Size = ECardSize.Small,
                    Tier = ETier.Bronze,
                    Owner = ECombatantId.Player,
                    Section = EInventorySection.Stash,
                    Socket = (EContainerSocketId)i,
                    CoreCard = ci,
                };
            }
            for (int i = 0; i < skills.Count; i++)
            {
                var tpl = skills[i];
                var ci = new CardInstance(tpl, CardTier.Silver, null, $"skill_{i}");
                run.Cards[ci.InstanceId] = new RunState.CardEntry
                {
                    InstanceId = ci.InstanceId,
                    TemplateId = Guid.Parse(tpl.Id),
                    Type = ECardType.Skill,
                    Size = ECardSize.Small,
                    Tier = ETier.Silver,
                    Owner = ECombatantId.Player,
                    Section = null,
                    Socket = null,
                    CoreCard = ci,
                };
            }

            var accId = Guid.NewGuid();
            string json = RunSnapshotSerializer.Serialize(run);
            db.SaveRunSnapshot(run, accId.ToString(), json);
            db.FlushRunImmediate(run.RunId);

            var loaded = db.LoadLatestActiveRun(accId);
            Assert.NotNull(loaded);
            Assert.True(RunSnapshotSerializer.TryDeserialize(loaded!.Snapshot, fx.Repo, out var restored, out _));
            Assert.NotNull(restored);

            var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(restored!);
            int idxUnlock = resumeEvents.FindIndex(e => e is GameSimEventSocketsUnlocked);
            int firstSpawn = resumeEvents.FindIndex(e => e is GameSimEventCardSpawned);
            Assert.True(idxUnlock >= 0, "BuildResumeEvents 必须下发 GameSimEventSocketsUnlocked");
            Assert.True(idxUnlock < firstSpawn, "GameSimEventSocketsUnlocked 必须先于 GameSimEventCardSpawned 下发");

            var unlockEv = (GameSimEventSocketsUnlocked)resumeEvents[idxUnlock];
            Assert.Equal(EInventorySection.Hand, unlockEv.Section);
            Assert.Equal((ushort)0b1111111111, unlockEv.UnlockedSocketsBitmask);

            var spawnedIds = resumeEvents.OfType<GameSimEventCardSpawned>().Select(e => e.InstanceId).ToHashSet();
            for (int i = 0; i < handSlots.Length; i++) Assert.Contains($"hand_{i}", spawnedIds);
            for (int i = 0; i < 3; i++) Assert.Contains($"stash_{i}", spawnedIds);
            for (int i = 0; i < skills.Count; i++) Assert.Contains($"skill_{i}", spawnedIds);
            Assert.Equal(skills.Count, resumeEvents.OfType<GameSimEventPlayerSkillEquipped>().Count());
        }
        finally
        {
            try { if (File.Exists(tmpDb)) File.Delete(tmpDb); } catch { }
        }
    }
}
