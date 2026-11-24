using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using BazaarServer.Storage;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V15.3 服务端与核心引擎官方对齐深度审计回归测试套件：
/// 覆盖卖卡提前腾格（Junkyard Club / Junkyard Repairbot 满手牌+满仓库卖卡产战利品）、
/// 卖卡目标排除与边界（Eagle Talisman / Feather）、TSpawnBehaviorAllowPlayerDuplicates 端到端生效、
/// PvP 真人镜像零双倍光环叠算与双重冷却缩减、Resume 切板事件与货架折扣持久化还原、
/// 以及奇遇步骤满格防幽灵卡等真实闭环场景。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class V15_3_ParityAuditFixesTests
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

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        CardTemplate tpl,
        CardTier tier,
        EInventorySection section,
        int startSlot,
        string instanceId)
    {
        var ci = new CardInstance(tpl, tier, null, instanceId);
        var grid = section == EInventorySection.Stash ? run.StashGrid : run.HandGrid;
        Assert.True(grid.TryPlaceOrShift(ci, startSlot), $"放置物品 {tpl.InternalName} 到 {section}:{startSlot} 失败");
        var entry = new RunState.CardEntry
        {
            InstanceId = instanceId,
            TemplateId = Guid.Parse(tpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tpl.Size),
            Tier = ShopEngine.ParseTier(tier.ToString()),
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)ci.StartSlot,
            CoreCard = ci,
        };
        RunState.PopulateMetaFromTemplate(entry, null, tpl);
        run.Cards[instanceId] = entry;
        return entry;
    }

    [Theory]
    [InlineData("bede53aa-8d47-4d6c-b302-2e684e8c3fcf", "Junkyard Club")]
    [InlineData("51696368-e409-4771-8baf-22c6da533cd8", "Junkyard Repairbot")]
    public void SellCard_JunkyardItemsOnCompletelyFullHandAndStash_VacatesSlotsAndSpawnsBothLootItems(
        string templateId,
        string cardName)
    {
        var fx = Fx.Value;
        var targetTpl = fx.Repo.GetCard(templateId);
        Assert.NotNull(targetTpl);

        var fillerTpl = fx.Repo.CardsById.Values.First(c =>
            c.Type == "Item" && c.Size == "Small" &&
            !string.Equals(c.Id, templateId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase));

        var run = new RunState
        {
            RunId = $"audit_sell_{cardName.Replace(" ", "_")}",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 3,
            Hour = 2,
            State = ERunState.Choice,
            UnlockedSlots = 0b0011111100, // Socket_2..7 共 6 格手牌
            StartSelectionDone = true,
            RngSeed = 20260418,
        };
        run.PlayerAttributes[EPlayerAttributeType.Gold] = 10;
        run.PlayerAttributes[EPlayerAttributeType.Level] = 3;
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.StashGrid.UnlockedMask = 1023;

        // 铺满全部 6 格手牌（Socket_2..3 放 2 格目标卡，Socket_4..7 放 4 张 Small 填充卡）
        AddOwnedItem(run, targetTpl!, CardTier.Bronze, EInventorySection.Hand, 2, "target_sell_item");
        for (int s = 4; s <= 7; s++)
            AddOwnedItem(run, fillerTpl, CardTier.Bronze, EInventorySection.Hand, s, $"hand_fill_{s}");

        // 铺满全部 10 格仓库（0 剩余空间）
        for (int s = 0; s < 10; s++)
            AddOwnedItem(run, fillerTpl, CardTier.Bronze, EInventorySection.Stash, s, $"stash_fill_{s}");

        var eng = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, fx.Arch);
        var outcome = eng.Handle(new SellCardCommand { InstanceId = new InstanceId("target_sell_item") });

        Assert.Null(outcome.Error);
        Assert.DoesNotContain("target_sell_item", run.Cards.Keys);

        var spawnedEvents = outcome.Events.OfType<GameSimEventCardSpawned>().ToList();
        Assert.Equal(2, spawnedEvents.Count);
        Assert.All(spawnedEvents, ev =>
        {
            Assert.Equal(EInventorySection.Hand, ev.Section);
            Assert.NotNull(ev.Socket);
            Assert.InRange((int)ev.Socket!.Value, 2, 3);
        });

        var newOwned = run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player &&
                        !c.InstanceId.StartsWith("hand_fill_") &&
                        !c.InstanceId.StartsWith("stash_fill_"))
            .ToList();
        Assert.Equal(2, newOwned.Count);
        Assert.All(newOwned, card => Assert.NotNull(card.Socket));
    }

    [Fact]
    public void SellCard_EagleTalismanAndFeather_TargetRemainingItemsAccurately()
    {
        var fx = Fx.Value;
        var eagleTpl = fx.Repo.GetCard("0022c409-c839-41e8-8022-65a407457dfe"); // Eagle Talisman
        var featherTpl = fx.Repo.GetCard("55f11b65-77fa-4d70-9250-8a40d03961ad"); // Feather
        Assert.NotNull(eagleTpl);
        Assert.NotNull(featherTpl);

        // 选取一件带主动冷却且可暴击的普通 Small 青铜武器（通过实例化读取该品阶合并后的 BaseAttributes）
        var weaponTpl = fx.Repo.CardsById.Values.First(c =>
        {
            if (c.Type != "Item" || c.Size != "Small" || !c.Tags.Contains("Weapon", StringComparer.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(c.StartingTier, "Bronze", StringComparison.OrdinalIgnoreCase))
                return false;
            var probe = new CardInstance(c, CardTier.Bronze);
            return probe.BaseAttributes.GetValueOrDefault("DamageAmount", 0) > 0
                && probe.BaseAttributes.GetValueOrDefault("CooldownMax", 0) >= 3000;
        });

        var run = new RunState
        {
            RunId = "audit_eagle_feather",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 4,
            Hour = 2,
            State = ERunState.Choice,
            UnlockedSlots = 0b0111111110,
            StartSelectionDone = true,
            RngSeed = 777,
        };
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.StashGrid.UnlockedMask = 1023;

        // 把 Eagle Talisman 放在最左（Socket_1），武器放在 Socket_2，Feather 放在 Socket_3
        AddOwnedItem(run, eagleTpl!, CardTier.Silver, EInventorySection.Hand, 1, "eagle_1");
        var weaponEntry = AddOwnedItem(run, weaponTpl, CardTier.Bronze, EInventorySection.Hand, 2, "weapon_1");
        AddOwnedItem(run, featherTpl!, CardTier.Gold, EInventorySection.Hand, 3, "feather_1");

        double critBefore = weaponEntry.CoreCard!.GetAttribute("CritChance");
        double cdBefore = weaponEntry.CoreCard!.EffectiveCooldownMs;

        var eng = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, fx.Arch);

        // 卖出位于最左的 Eagle Talisman：不得把自己当最左，必须给右侧 weapon_1 加暴击
        var o1 = eng.Handle(new SellCardCommand { InstanceId = new InstanceId("eagle_1") });
        Assert.Null(o1.Error);
        Assert.True(weaponEntry.CoreCard.GetAttribute("CritChance") > critBefore, "卖出最左 Eagle Talisman 应为剩余最左可暴击武器增加暴击率");

        // 卖出 Feather：应使 weapon_1 获得 PercentCooldownReduction 并实际降低有效 CooldownMax
        var o2 = eng.Handle(new SellCardCommand { InstanceId = new InstanceId("feather_1") });
        Assert.Null(o2.Error);
        Assert.True(weaponEntry.CoreCard.GetAttribute("PercentCooldownReduction") > 0, "卖出 Feather 应赋予 PercentCooldownReduction");
        Assert.True(weaponEntry.CoreCard.EffectiveCooldownMs < cdBefore, "Feather 冷却缩减应真实降低武器有效冷却");
    }

    [Fact]
    public void SpawnQueryEngine_AllowPlayerDuplicates_NotBlockedByOwnedDiamondMaxTier()
    {
        var fx = Fx.Value;
        var itemTpl = fx.Repo.CardsById.Values.First(c =>
            c.Type == "Item" && c.Size == "Small" &&
            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase) &&
            c.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase));

        var player = new PlayerState("p1", "Player", "Vanessa", 500, 20, 30, 10) { Day = 5, Hour = 2, Level = 6 };
        var ownedCard = new CardInstance(itemTpl, CardTier.Diamond, null, "owned_dia");
        Assert.True(player.Hand.TryAddAuto(ownedCard));
        var ownedMaxTiers = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase)
        {
            [itemTpl.Id] = CardTier.Diamond,
        };

        RuleNode BuildSingleIdQuery(bool allowPlayerDup) => new()
        {
            Type = "TSpawnContextQuery",
            SelectionMethod = "Sequential",
            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement(1.0) },
            Behaviors = allowPlayerDup
                ? new List<RuleNode>
                {
                    new() { Type = "TSpawnBehaviorAllowPlayerDuplicates", AllowPlayerDuplicates = true },
                    new() { Type = "TSpawnBehaviorIgnoreTierTable", IgnoreTierTable = true },
                }
                : new List<RuleNode>
                {
                    new() { Type = "TSpawnBehaviorIgnoreTierTable", IgnoreTierTable = true },
                },
            Groups = new List<RuleNode>
            {
                new()
                {
                    Type = "TSpawnGroup",
                    SelectionMethod = "Random",
                    Filters = new List<RuleNode>
                    {
                        new() { Type = "TSpawnFilterIdList", Ids = new List<string> { itemTpl.Id } },
                    },
                },
            },
        };

        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, player, player, new Random(123))
        {
            CurrentDay = 5,
            CurrentHour = 2,
        };

        // 未声明 AllowPlayerDuplicates：已有 Diamond 满阶物品时必须被 ownedMaxTiers 拦截
        var blocked = SpawnQueryEngine.SpawnCards(BuildSingleIdQuery(false), ctx, 1, ownedMaxTiers);
        Assert.Empty(blocked);

        // 显式声明 AllowPlayerDuplicates：Item 必须豁免 ownedMaxTiers 拦截并成功抽出
        var allowed = SpawnQueryEngine.SpawnCards(BuildSingleIdQuery(true), ctx, 1, ownedMaxTiers);
        Assert.Single(allowed);
        Assert.Equal(itemTpl.Id, allowed[0].Template.Id, StringComparer.OrdinalIgnoreCase);

        // 对照验证：已持有满阶 Diamond Skill 时，即便显式声明 AllowPlayerDuplicates 也必须严格拦截，禁止重复刷出
        var skillTpl = fx.Repo.CardsById.Values.First(c =>
            c.Type == "Skill" &&
            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase) &&
            c.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase));
        player.Skills.Add(new CardInstance(skillTpl, CardTier.Diamond, null, "owned_dia_skill"));
        ownedMaxTiers[skillTpl.Id] = CardTier.Diamond;
        var skillQueryWithAllowDup = new RuleNode
        {
            Type = "TSpawnContextQuery",
            SelectionMethod = "Sequential",
            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement(1.0) },
            Behaviors = new List<RuleNode>
            {
                new() { Type = "TSpawnBehaviorAllowPlayerDuplicates", AllowPlayerDuplicates = true },
                new() { Type = "TSpawnBehaviorIgnoreTierTable", IgnoreTierTable = true },
            },
            Groups = new List<RuleNode>
            {
                new()
                {
                    Type = "TSpawnGroup",
                    SelectionMethod = "Random",
                    Filters = new List<RuleNode>
                    {
                        new() { Type = "TSpawnFilterIdList", Ids = new List<string> { skillTpl.Id } },
                    },
                },
            },
        };
        Assert.Empty(SpawnQueryEngine.SpawnCards(skillQueryWithAllowDup, ctx, 1, ownedMaxTiers));
    }

    [Fact]
    public void PvpGhostPool_RecordPlayerGhost_DoesNotBakeAuraOrDerivedCooldownIntoPermanentAttributes()
    {
        var fx = Fx.Value;
        string tmpDb = Path.Combine(Path.GetTempPath(), $"audit_ghost_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Persistence(tmpDb);
            var weaponTpl = fx.Repo.CardsById.Values.First(c =>
            {
                if (c.Type != "Item" || c.Size != "Small" || !string.Equals(c.StartingTier, "Bronze", StringComparison.OrdinalIgnoreCase))
                    return false;
                var probe = new CardInstance(c, CardTier.Bronze);
                return probe.BaseAttributes.GetValueOrDefault("DamageAmount", 0) > 0
                    && probe.BaseAttributes.GetValueOrDefault("CooldownMax", 0) >= 3000;
            });

            var run = new RunState
            {
                RunId = "audit_ghost_aura_run",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = 4,
                Hour = 6,
                State = ERunState.Choice,
                UnlockedSlots = 1023,
                StartSelectionDone = true,
            };
            run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 1000;
            run.PlayerAttributes[EPlayerAttributeType.Health] = 1000;
            run.PlayerAttributes[EPlayerAttributeType.Level] = 4;
            run.HandGrid.UnlockedMask = 1023;

            var entry = AddOwnedItem(run, weaponTpl, CardTier.Bronze, EInventorySection.Hand, 2, "w_aura_1");
            var core = entry.CoreCard!;
            double baseDmg = core.BaseAttributes.GetValueOrDefault("DamageAmount", 0);
            double baseCd = core.BaseAttributes.GetValueOrDefault("CooldownMax", 0);

            // 模拟：永久 +10% 冷却缩减 + 永久 +15 伤害；同时叠加 +80 局外光环伤害（绝不应被焊进镜像永久属性！）
            core.PermanentAdd["DamageAmount"] = 15;
            core.PermanentAdd["PercentCooldownReduction"] = 10;
            core.AuraAdd["DamageAmount"] = 80;

            // 模拟 entry.Attributes 已被局外光环与派生冷却同步（含光环 80 伤害 + 折算后的 CooldownMax）
            entry.Attributes[ECardAttributeType.DamageAmount] = (int)Math.Round(baseDmg + 15 + 80);
            entry.Attributes[ECardAttributeType.PercentCooldownReduction] = 10;
            entry.Attributes[ECardAttributeType.CooldownMax] = (int)Math.Round(core.EffectiveCooldownMs);

            var ghostPool = new PvpGhostPool();
            var recorded = ghostPool.RecordPlayerGhost(run, db, isCustomRule: false, poolPartition: PvpGhostPool.StandardPartition);
            Assert.NotNull(recorded);
            Assert.Single(recorded!.Items);

            var savedItem = recorded.Items[0];
            // 必须仅记录真实永久伤害 (baseDmg + 15)，不能包含 +80 光环
            Assert.Equal((int)Math.Round(baseDmg + 15), savedItem.PermanentAttributes["DamageAmount"]);
            // 必须保留 PercentCooldownReduction=10，且不得把折算后的 CooldownMax 写入永久层造成二次折算
            Assert.Equal(10, savedItem.PermanentAttributes["PercentCooldownReduction"]);
            Assert.False(savedItem.PermanentAttributes.ContainsKey("CooldownMax"));

            var oppState = PvpGhostPool.ToPlayerState(recorded, fx.Repo);
            var restoredWeapon = Assert.Single(oppState.Hand.Cards);
            Assert.Equal(baseDmg + 15, restoredWeapon.GetAttribute("DamageAmount"), precision: 3);
            double expectedCd = Math.Max(1000, baseCd * 0.9);
            Assert.Equal(expectedCd, restoredWeapon.EffectiveCooldownMs, precision: 1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(tmpDb)) File.Delete(tmpDb); } catch { }
        }
    }

    [Fact]
    public void Resume_EmitsBoardOverriddenInOrder_AndPreservesShelfDiscountsAfterSilentAuraRecalc()
    {
        var fx = Fx.Value;
        var smallItem = fx.Repo.CardsById.Values.First(c =>
            c.Type == "Item" && c.Size == "Small" &&
            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase) &&
            c.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase));
        var skillTpl = fx.Repo.CardsById.Values.First(c =>
            c.Type == "Skill" &&
            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase));

        var run = new RunState
        {
            RunId = "audit_resume_board_discount",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 6,
            Hour = 2,
            State = ERunState.Encounter,
            CurrentEncounterId = CardPool.CrashSiteExpeditionRoomId,
            CurrentBoardId = "Board_Expedition_CrashSite",
            UnlockedSlots = 0b1111111111,
            StartSelectionDone = true,
        };
        run.PlayerAttributes[EPlayerAttributeType.Level] = 6;
        run.PlayerAttributes[EPlayerAttributeType.Gold] = 40;
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.StashGrid.UnlockedMask = 1023;

        AddOwnedItem(run, smallItem, CardTier.Silver, EInventorySection.Hand, 0, "owned_left");
        var skCore = new CardInstance(skillTpl, CardTier.Bronze, null, "owned_skl");
        run.Cards["owned_skl"] = new RunState.CardEntry
        {
            InstanceId = "owned_skl",
            TemplateId = Guid.Parse(skillTpl.Id),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            CoreCard = skCore,
        };

        // 构造一张货架打折卡（仅在 ActiveShelfDiscounts 记折扣价、故意不预先写 CoreCard.HourMult，验证重连静默光环重算不会把折扣抹回原价）
        var shelfCore = new CardInstance(smallItem, CardTier.Silver, null, "shelf_disc_1");
        int baseBuy = RunState.ComputeCardBuyPrice(
            new RunState.CardEntry { Type = ECardType.Item, Size = ECardSize.Small, Tier = ETier.Silver, CoreCard = shelfCore },
            shelfCore,
            fx.Repo.GameMode.StandardPrices);
        int discountedBuy = Math.Max(1, baseBuy / 2);
        var shelfEntry = new RunState.CardEntry
        {
            InstanceId = "shelf_disc_1",
            TemplateId = Guid.Parse(smallItem.Id),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Silver,
            Owner = null,
            CoreCard = shelfCore,
        };
        shelfEntry.Attributes[ECardAttributeType.BuyPrice] = baseBuy;
        run.Cards[shelfEntry.InstanceId] = shelfEntry;
        run.SelectionSet.Add(shelfEntry.InstanceId);
        run.ActiveShelfDiscounts[shelfEntry.InstanceId] = discountedBuy;

        string json = RunSnapshotSerializer.Serialize(run);
        Assert.True(RunSnapshotSerializer.TryDeserialize(json, fx.Repo, out var restored, out _));
        Assert.NotNull(restored);

        // 经 RunEngine 恢复构造（内部会执行 RestoreActiveShelfDiscountsFromRun + RecalculateAurasSilentlyOnResume）
        _ = new RunEngine(restored!, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, fx.Arch);

        var restoredShelf = restored!.Cards["shelf_disc_1"];
        Assert.True(restoredShelf.CoreCard!.HourMult.ContainsKey("BuyPrice"), "恢复货架折扣时必须同步写入 CoreCard.HourMult[BuyPrice]");
        Assert.True(restoredShelf.Attributes[ECardAttributeType.BuyPrice] < baseBuy, "静默光环重算后货架折扣价不得被冲回原价");

        var evs = RunSnapshotSerializer.BuildResumeEvents(restored);
        int idxInit = evs.FindIndex(e => e is GameSimEventPlayerInitialized);
        int idxUnlock = evs.FindIndex(e => e is GameSimEventSocketsUnlocked);
        int idxSpawn = evs.FindIndex(e => e is GameSimEventCardSpawned);
        int idxSkill = evs.FindIndex(e => e is GameSimEventPlayerSkillEquipped);
        int idxDealt = evs.FindIndex(e => e is GameSimEventCardDealt);
        int idxBoard = evs.FindIndex(e => e is GameSimEventBoardOverridden);
        int idxState = evs.FindIndex(e => e is GameSimEventStateTransitioned);

        Assert.True(idxInit >= 0 && idxUnlock > idxInit && idxSpawn > idxUnlock && idxSkill > idxSpawn && idxDealt > idxSkill);
        Assert.True(idxBoard > idxDealt, "Resume 处于切板态时必须在 CardDealt 之后补发 GameSimEventBoardOverridden");
        Assert.True(idxState > idxBoard);
        Assert.Equal("Board_Expedition_CrashSite", ((GameSimEventBoardOverridden)evs[idxBoard]).Board);
    }

    [Fact]
    public void EncounterStep_OverflowSpawnOnCompletelyFullHandAndStash_DoesNotCreateNullSocketGhostCard()
    {
        var fx = Fx.Value;
        var fillerTpl = fx.Repo.CardsById.Values.First(c =>
            c.Type == "Item" && c.Size == "Small" &&
            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase));

        var run = new RunState
        {
            RunId = "audit_overflow_no_ghost",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 3,
            Hour = 2,
            State = ERunState.Encounter,
            UnlockedSlots = 0b0011111100, // 6 格已解锁
            StartSelectionDone = true,
        };
        run.PlayerAttributes[EPlayerAttributeType.Gold] = 20;
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.StashGrid.UnlockedMask = 1023;

        // 手牌 6/6 全满 + 仓库 10/10 全满
        for (int s = 2; s <= 7; s++)
            AddOwnedItem(run, fillerTpl, CardTier.Bronze, EInventorySection.Hand, s, $"h_{s}");
        for (int s = 0; s < 10; s++)
            AddOwnedItem(run, fillerTpl, CardTier.Bronze, EInventorySection.Stash, s, $"s_{s}");

        // 执行 Free Samples（产出 2 件战利品），全满时绝不得残留 Socket==null 的幽灵玩家物品卡
        const string freeSamplesStepId = "401bca74-0dc2-442c-a0d6-1a674bd63c9f";
        var eng = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, fx.Arch);
        run.Cards["stp_ovf"] = new RunState.CardEntry
        {
            InstanceId = "stp_ovf",
            TemplateId = Guid.Parse(freeSamplesStepId),
            Type = ECardType.EncounterStep,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = null,
        };
        run.SelectionSet.Add("stp_ovf");

        var outcome = eng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId("stp_ovf") });
        Assert.Null(outcome.Error);

        var ownedItems = run.Cards.Values.Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item).ToList();
        Assert.All(ownedItems, item => Assert.NotNull(item.Socket));
        Assert.Equal(16, ownedItems.Count);
    }
}
