using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarGameShared.TempoNet.Enums;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 5A 服务端对局引擎与状态协议专项回归测试套件：
/// 1. P0-03（RC-CORE-ID-SCHISM-TRANSFORM-REVERT）：全量 CardInstance 透传 entry.InstanceId、战后变形还原反查与禁发 Size=0 空壳；
/// 2. P1-03（RC-COMBAT-SPAWN-HP-MULT-MISSING）：CombatSpawnSim 同步 Timeline[0] 倍率缩放后双方血量且不污染局外 HealthMax；
/// 3. P1-09（RC-PVP-DESPAWN-OPP-HP-RESET）：CombatDespawnSim 与非战态包省略 Opponent 更新并清空 OpponentHero/非战态属性；
/// 4. P1-10（RC-JULES-START-SPAWN-DEALT-MIX）：开局固有实体/初始插座与预设物前置落盘，首条 Choice 包严禁混发 CardSpawned + CardDealt；
/// 5. P1-11（RC-SKILL-BOARD-CLONE-EXPLOSION 服务端侧）：升级/奇遇赐技（Inspired by Karnok 等）拆包隔离 CardSpawned 与后续 Choice CardDealt；
/// 6. P1-12（RC-HOUR6-FATES-LEVELUP-CLOCK-LOCK）：Hour == 6 Fates/PvP 升级推迟至跨天 Day+1 Hour 1 消费，并拆包隔离 LevelUp 选技与跨小时 CardDealt；
/// 7. P2-03（RC-KNEEBRACE-FIGHTENDED-HEALTH-DESYNC）：Knee Brace 战后加 MaxHP 等 4 类路径收敛至 SetBasePlayerAttribute(Health)，根治商店残血。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class Wave5A_RunEngineProtocolTests
{
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string LighterId = "3d212e22-12eb-427e-ab96-e7baf1229a7b";
    private const string MacheteId = "05a83727-cfd8-4f9b-a109-e720714de70b";
    private const string KneeBraceId = "d5e2cff2-31b5-48eb-86ff-9543ea0c6d40";
    private const string VirusId = "020a0ec0-21e6-41af-899f-063573ba9ca5";
    private const string ArmsDealerSkillId = "560a532c-73f4-4edd-b197-0127e2ca4cc9";
    private const string FatesTemplateId = "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
    private const string InspiredByKarnokStepId = "0c71aef1-c049-483a-b9a0-19c5005b4fc7";
    private const string InspiredRageSkillId = "0beccea0-bb9b-444b-b063-bf6aeadaddc8";
    private const string PolNormalId = "065f6793-8607-4f02-abfe-db6afb221ab2";

    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);
    private static readonly Lazy<List<BazaarServer.Data.MonsterTemplate>> Monsters =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.CreateMonstersCopy());
    private static readonly Lazy<LevelUpTable> LevelUps =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.LevelUps);
    private static readonly Lazy<GameModeConfig> Mode =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.Mode);

    private static (RunState Run, RunEngine Engine) CreateRun(
        EHero hero = EHero.Vanessa,
        uint day = 2,
        uint hour = 1,
        ERunState state = ERunState.Choice,
        int gold = 30,
        int hp = 500,
        int level = 2,
        int xp = 0,
        int prestige = 20,
        int seed = 51001,
        CustomRunRuleSet? customRules = null,
        PvpGhostPool? ghosts = null)
    {
        var run = new RunState
        {
            RunId = "w5a_" + Guid.NewGuid().ToString("N")[..10],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Ranked,
            AccountId = "acc_w5a_" + Guid.NewGuid().ToString("N")[..6],
            Username = "Wave5ATester",
            Day = day,
            Hour = hour,
            State = state,
            UnlockedSlots = day >= 4 ? (ushort)1023 : day == 3 ? (ushort)0b01_1111_1110 : (ushort)0b00_0111_1000,
            StartSelectionDone = true,
            RngSeed = seed,
            CustomRules = customRules ?? new CustomRunRuleSet(),
        };
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.SetBasePlayerAttribute(EPlayerAttributeType.Gold, gold);
        run.SetBasePlayerAttribute(EPlayerAttributeType.HealthMax, hp);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Health, hp);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, prestige);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Level, level);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Experience, xp);

        var engine = new RunEngine(
            run,
            Pool.Value,
            Monsters.Value,
            LevelUps.Value,
            Mode.Value,
            Repo.Value,
            ghosts ?? new PvpGhostPool());
        return (run, engine);
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        string templateId,
        EInventorySection section,
        int slot,
        ETier tier = ETier.Bronze,
        string? instanceId = null)
    {
        var coreTpl = Repo.Value.GetCard(templateId);
        Assert.NotNull(coreTpl);
        var poolTpl = Pool.Value.Get(templateId);
        string id = instanceId ?? ("itm_w5a_" + Guid.NewGuid().ToString("N")[..8]);
        var core = new CardInstance(coreTpl!, ShopEngine.ToCoreTier(tier), null, id)
        {
            StartSlot = slot,
        };
        var grid = section == EInventorySection.Stash ? run.StashGrid : run.HandGrid;
        grid.UnlockedMask = section == EInventorySection.Stash ? (ushort)1023 : run.UnlockedSlots;
        Assert.True(grid.TryPlaceOrShift(core, slot), $"向 {section}:{slot} 放置 {templateId} 失败");

        var entry = new RunState.CardEntry
        {
            InstanceId = id,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl!.Size),
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)core.StartSlot,
        };
        entry.BindAlignedCoreCard(core);
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, Repo.Value.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static PvpGhostPool.GhostBoard BuildGhostBoard(int day, int ghostHp = 600, string hero = "Dooley")
    {
        return new PvpGhostPool.GhostBoard
        {
            BoardId = $"w5a_ghost_d{day}_{Guid.NewGuid():N}",
            Day = day,
            Hero = hero,
            Level = Math.Max(1, day),
            HealthMax = ghostHp,
            Prestige = 20,
            Wins = Math.Max(0, day - 1),
            PoolPartition = PvpGhostPool.StandardPartition,
            IsCustomRule = false,
            Items =
            [
                new PvpGhostPool.GhostItem { TemplateId = CutlassId, Tier = "Silver", Slot = 3, Width = 2 },
            ],
        };
    }

    private static void InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1. P0-03：全量 CardInstance 构造点透传 entry.InstanceId + CardEntry 绑定守护
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P0_03_ShopEngineAndRunEngine_AllCardEntriesMaintainCoreInstanceIdInvariant()
    {
        // 1) ShopEngine 各构造入口（CreateItemCandidate / CreateSkillCandidate / CreateEntryFromCoreInstance / DealEncounterShelf / DealLevelUpChoices）
        var (run, _) = CreateRun(hero: EHero.Vanessa, day: 3, hour: 2, seed: 50301);
        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);

        var itemTpl = Pool.Value.Get(CutlassId);
        var skillTpl = Pool.Value.Get(ArmsDealerSkillId);
        Assert.NotNull(itemTpl);
        Assert.NotNull(skillTpl);

        var itemCand = shop.CreateItemCandidate(itemTpl!, ETier.Silver, isFree: false);
        Assert.NotNull(itemCand.CoreCard);
        Assert.Equal(itemCand.InstanceId, itemCand.CoreCard.InstanceId);
        Assert.True(itemCand.HasAlignedCoreCardId);

        var skillCand = shop.CreateSkillCandidate(skillTpl!, ETier.Gold, isFree: true);
        Assert.NotNull(skillCand.CoreCard);
        Assert.Equal(skillCand.InstanceId, skillCand.CoreCard.InstanceId);
        Assert.True(skillCand.HasAlignedCoreCardId);

        // 传入带随机 UUID 且携带 BaseAttributes["Tiny"] 覆写的裸 CardInstance -> CreateEntryFromCoreInstance 必须原地对齐 ID、保持同一对象引用且保留 Tiny
        var randomCore = new CardInstance(Repo.Value.GetCard(CutlassId)!, CardTier.Silver);
        randomCore.BaseAttributes["Tiny"] = 1;
        randomCore.MarkAttributeDirty();
        string rawRandomUuid = randomCore.InstanceId;
        var fromCore = shop.CreateEntryFromCoreInstance(randomCore, isFree: false);
        Assert.NotNull(fromCore.CoreCard);
        Assert.Same(randomCore, fromCore.CoreCard);
        Assert.NotEqual(rawRandomUuid, fromCore.InstanceId);
        Assert.Equal(fromCore.InstanceId, fromCore.CoreCard.InstanceId);
        Assert.Equal(1.0, fromCore.CoreCard.BaseAttributes["Tiny"]);
        Assert.Equal(ECardSize.Small, fromCore.Size);
        Assert.True(fromCore.HasAlignedCoreCardId);

        // 商店刷货架（Pol）全部候选卡必须满足 CoreCard.InstanceId == entry.InstanceId
        shop.DealEncounterShelf(PolNormalId, isReroll: false);
        Assert.NotEmpty(run.SelectionSet);
        foreach (string selId in run.SelectionSet)
        {
            var ce = run.Cards[selId];
            if (ce.CoreCard != null)
            {
                Assert.Equal(ce.InstanceId, ce.CoreCard.InstanceId);
                Assert.True(ce.HasAlignedCoreCardId);
            }
        }

        // 升级抽选（DealLevelUpChoices）全部候选卡必须满足 CoreCard.InstanceId == entry.InstanceId
        shop.DealLevelUpChoices(level: 3, LevelUps.Value);
        Assert.NotEmpty(run.SelectionSet);
        foreach (string selId in run.SelectionSet)
        {
            var ce = run.Cards[selId];
            if (ce.CoreCard != null)
            {
                Assert.Equal(ce.InstanceId, ce.CoreCard.InstanceId);
                Assert.True(ce.HasAlignedCoreCardId);
            }
        }

        // 2) RunEngine.StartRun（含 StartingItems / StartingSkills / GoldenStart）全盘实体必须严格对齐
        var goldenRules = new CustomRunRuleSet
        {
            MutatorIds = [MutatorCatalog.GoldenStart],
            StartingItems = [new PresetCardSpec { TemplateId = CutlassId, Tier = "Silver" }],
            StartingSkills = [new PresetCardSpec { TemplateId = ArmsDealerSkillId, Tier = "Gold" }],
        };
        var (startRun, startEngine) = CreateRun(hero: EHero.Jules, day: 1, hour: 1, seed: 50302, customRules: goldenRules);
        var startOut = startEngine.StartRun();
        Assert.Null(startOut.Error);

        var ownedCards = startRun.Cards.Values.Where(c => c.Owner == ECombatantId.Player).ToList();
        Assert.True(ownedCards.Count >= 5, $"应包含 StartingItem + StartingSkill + GoldenStart + 英雄初始插座，实际 {ownedCards.Count}");
        foreach (var ce in ownedCards)
        {
            Assert.NotNull(ce.CoreCard);
            Assert.Equal(ce.InstanceId, ce.CoreCard.InstanceId);
            Assert.True(ce.HasAlignedCoreCardId);
        }

        // 3) CardEntry.BindAlignedCoreCard 对失配 UUID 自动无损校准，且保留永久增益
        var driftedCore = new CardInstance(Repo.Value.GetCard(CutlassId)!, CardTier.Gold, null, "legacy_random_uuid_32chars");
        driftedCore.PermanentAdd["DamageAmount"] = 42;
        var guardedEntry = new RunState.CardEntry
        {
            InstanceId = "itm_guarded_001",
            TemplateId = Guid.Parse(CutlassId),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Gold,
        };
        guardedEntry.BindAlignedCoreCard(driftedCore);
        Assert.Equal("itm_guarded_001", guardedEntry.CoreCard.InstanceId);
        Assert.Equal(42, guardedEntry.CoreCard.PermanentAdd["DamageAmount"]);
        Assert.True(guardedEntry.HasAlignedCoreCardId);
    }

    [Fact]
    public void P0_03_PostCombatTransformRevert_ReverseLookupByCoreId_And_ForbidsZeroSizeShellSimCard()
    {
        var (run, engine) = CreateRun(hero: EHero.Mak, day: 1, hour: 3, state: ERunState.Choice, seed: 50309);

        // 1) 模拟旧存档/历史割裂场景：CardEntry.InstanceId = "itm_0007_134EF"，而 CoreCard.InstanceId 为 32 位随机 UUID
        var lighterTpl = Repo.Value.GetCard(LighterId)!;
        string driftedCoreUuid = Guid.NewGuid().ToString("N");
        var driftedCore = new CardInstance(lighterTpl, CardTier.Silver, null, driftedCoreUuid) { StartSlot = 4 };
        run.HandGrid.TryPlaceOrShift(driftedCore, 4);
        var entry = new RunState.CardEntry
        {
            InstanceId = "itm_0007_134EF",
            TemplateId = Guid.Parse(LighterId),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Silver,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_4,
            CoreCard = driftedCore, // 故意保留历史割裂状态验证反查防线
        };
        entry.Attributes[ECardAttributeType.SellPrice] = 2;
        entry.Attributes[ECardAttributeType.BurnApplyAmount] = 3;
        run.Cards[entry.InstanceId] = entry;

        // 战中 Virus 临时变形记录携带 driftedCoreUuid；战后还原必须按 CoreCard.InstanceId 反查命中真实 itm_0007_134EF
        var tempTransforms = new List<CombatTemporaryTransformRecord>
        {
            new()
            {
                OwnerPlayerId = "player",
                OriginalCardInstanceId = driftedCoreUuid,
                OriginalTemplateId = LighterId,
                OriginalCardType = "Item",
                OriginalSection = "Hand",
                OriginalSlot = 4,
                TransformedInstanceIds = ["virus_tmp_1"],
            },
            // 另塞一条完全不存在于 run.Cards 的孤儿记录，验证直接跳过绝不构造 Size=0 空壳
            new()
            {
                OwnerPlayerId = "player",
                OriginalCardInstanceId = "orphan_missing_uuid_999",
                OriginalTemplateId = VirusId,
                OriginalCardType = "Item",
                OriginalSection = "Hand",
                OriginalSlot = 3,
                TransformedInstanceIds = ["virus_tmp_orphan"],
            },
        };

        var despawnEvents = new List<IGameSimEvent>();
        var dirtyIds = new HashSet<string>(StringComparer.Ordinal);
        InvokePrivate(engine, "EmitPostCombatTemporaryTransformRevertsWithOwner",
            tempTransforms, despawnEvents, dirtyIds, "player", null);

        var reverted = Assert.Single(despawnEvents.OfType<GameSimEventCardTransformReverted>());
        Assert.Equal("itm_0007_134EF", reverted.OriginalCard.InstanceId);
        Assert.Contains("itm_0007_134EF", dirtyIds);

        var sim = run.BuildFullGameSim(despawnEvents);
        Assert.True(sim.Cards.TryGetValue("itm_0007_134EF", out var simCard));
        Assert.NotNull(simCard!.Size);
        Assert.NotEqual((ECardSize)0, simCard.Size!.Value);
        Assert.Equal(ECardSize.Small, simCard.Size.Value);
        Assert.Equal(ETier.Silver, simCard.Tier);
        Assert.True(simCard.Attributes.TryGetValue(ECardAttributeType.SellPrice, out var sellAttr) && sellAttr.Value > 0,
            "还原后的 SimUpdateCard 必须携带合法 SellPrice，杜绝铜锁图标");

        // 2) 直接向 BuildDeltaGameSim / SanitizeWireEventsAndCards 喂入孤儿 CardTransformReverted 与外部 CardTransformed，
        //    验证绝不产出任何 Size == 0 的崩溃空壳 SimUpdateCard
        var rawEvents = new List<IGameSimEvent>
        {
            new GameSimEventCardTransformReverted(
                new SimEventCardTransformation("ghost_uuid_not_in_cards", VirusId, ECardType.Item, ECombatantId.Player, EInventorySection.Hand, EContainerSocketId.Socket_3),
                ["tmp_virus_x"]),
            new GameSimEventCardTransformed(
                "tr_orig_x",
                VirusId,
                [new SimEventCardTransformation("tr_new_x", VirusId, ECardType.Item, ECombatantId.Player, EInventorySection.Hand, EContainerSocketId.Socket_5)]),
        };
        var sanitizedSim = run.BuildDeltaGameSim(rawEvents);
        Assert.DoesNotContain(sanitizedSim.Events, e => e is GameSimEventCardTransformReverted rev && rev.OriginalCard.InstanceId == "ghost_uuid_not_in_cards");
        Assert.False(sanitizedSim.Cards.ContainsKey("ghost_uuid_not_in_cards"), "孤儿还原事件绝不能在 sim.Cards 留下 Size=0 空壳");
        Assert.True(sanitizedSim.Cards.ContainsKey("tr_new_x"));
        Assert.All(sanitizedSim.Cards.Values, sc =>
        {
            Assert.NotNull(sc.Size);
            Assert.NotEqual((ECardSize)0, sc.Size!.Value);
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. P1-03：CombatSpawnSim 优先同步 Timeline[0] 倍率后双方血量，且不污染局外原值
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MutatorCatalog.TitanVitality, 400, 1000, 600, 1500)]
    [InlineData(MutatorCatalog.GlassCannon, 400, 240, 600, 360)]
    public void P1_03_CombatSpawnSim_SyncsScaledHealthMaxFromTimeline0_WithoutPollutingOutOfCombatHealth(
        string mutatorId,
        int basePlayerHp,
        int expectedCombatPlayerHp,
        int baseGhostHp,
        int expectedCombatOppHp)
    {
        var rules = new CustomRunRuleSet
        {
            MutatorIds = [mutatorId],
        };
        var (run, engine) = CreateRun(
            hero: EHero.Vanessa,
            day: 2,
            hour: 6,
            state: ERunState.Choice,
            hp: basePlayerHp,
            level: 2,
            seed: 51301,
            customRules: rules);
        AddOwnedItem(run, CutlassId, EInventorySection.Hand, slot: 3, ETier.Silver);

        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
        shop.DealHourEncounters();
        run.PendingPvpGhost = BuildGhostBoard(day: 2, ghostHp: baseGhostHp);
        string pvpCardId = run.SelectionSet.Single(id => run.Cards[id].Type == ECardType.PvpEncounter);

        var combatOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCardId) });
        Assert.Null(combatOut.Error);
        Assert.NotNull(combatOut.CombatSpawnSim);
        Assert.NotNull(combatOut.Combat);
        Assert.NotNull(combatOut.CombatDespawnSim);

        // 1) CombatSpawnSim 双方 HealthMax 与 Health 必须严格等于经词条倍率缩放后的实战初值
        Assert.Equal(expectedCombatPlayerHp, combatOut.CombatSpawnSim!.Player!.Attributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(expectedCombatPlayerHp, combatOut.CombatSpawnSim.Player.Attributes[EPlayerAttributeType.Health]);
        Assert.NotNull(combatOut.CombatSpawnSim.Opponent);
        Assert.Equal(expectedCombatOppHp, combatOut.CombatSpawnSim.Opponent!.Attributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(expectedCombatOppHp, combatOut.CombatSpawnSim.Opponent.Attributes[EPlayerAttributeType.Health]);

        // 2) 玩家局外持久化 HealthMax 与 DespawnMessage 必须保持未乘倍原值（绝不永久翻倍或扣减）
        Assert.Equal(basePlayerHp, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(basePlayerHp, (int)Math.Round(run.BasePlayerAttributes["HealthMax"]));
        Assert.Equal(basePlayerHp, combatOut.CombatDespawnSim!.Player!.Attributes[EPlayerAttributeType.HealthMax]);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. P1-09：CombatDespawnSim 与非战态包省略 Opponent 更新，清场清空 OpponentHero 与属性
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_09_CombatDespawnAndNonCombatPackets_OmitOpponentReset_AndClearOpponentHero()
    {
        var (run, engine) = CreateRun(
            hero: EHero.Stelle,
            day: 3,
            hour: 6,
            state: ERunState.Choice,
            hp: 700,
            level: 3,
            seed: 51901);
        AddOwnedItem(run, CutlassId, EInventorySection.Hand, slot: 3, ETier.Gold);

        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
        shop.DealHourEncounters();
        run.PendingPvpGhost = BuildGhostBoard(day: 3, ghostHp: 1450, hero: "Pygmalien");
        string pvpCardId = run.SelectionSet.Single(id => run.Cards[id].Type == ECardType.PvpEncounter);

        var pvpOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCardId) });
        Assert.Null(pvpOut.Error);
        Assert.NotNull(pvpOut.CombatSpawnSim);
        Assert.NotNull(pvpOut.CombatDespawnSim);

        // 1) 战斗开场包（PVPCombat 态）必须正常携带真实对手属性（HealthMax = 1450）
        Assert.NotNull(pvpOut.CombatSpawnSim!.Opponent);
        Assert.Equal(1450, pvpOut.CombatSpawnSim.Opponent!.Attributes[EPlayerAttributeType.HealthMax]);

        // 2) 战后退场包（CombatDespawnSim）绝对不得下发重置回 100/100 的 Opponent 属性变更
        Assert.Null(pvpOut.CombatDespawnSim!.Opponent);

        // 3) 战后 RunState 清场：OpponentHero 为 null、CurrentPvpOpponent 为 null、OpponentAttributes 已清空
        Assert.Null(run.OpponentHero);
        Assert.Null(run.CurrentPvpOpponent);
        Assert.Empty(run.OpponentAttributes);

        // 4) 后续非战斗态 Full / Delta 协议包同样省略 Opponent，彻底杜绝虚假负值最大生命飘字
        var nonCombatFull = run.BuildFullGameSim();
        var nonCombatDelta = run.BuildDeltaGameSim();
        Assert.Null(nonCombatFull.Opponent);
        Assert.Null(nonCombatDelta.Opponent);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4. P1-10：全英雄（含 Jules / Hero8）与预设开局首条 Choice 包严禁混发 CardSpawned + CardDealt
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EHero.Vanessa, false)]
    [InlineData(EHero.Pygmalien, false)]
    [InlineData(EHero.Dooley, true)]
    [InlineData(EHero.Mak, false)]
    [InlineData(EHero.Stelle, false)]
    [InlineData(EHero.Jules, false)]
    [InlineData(EHero.Karnok, false)]
    [InlineData(EHero.Hero8, true)]
    public void P1_10_StartRun_AllHeroesAndPresets_NeverMixCardSpawnedAndCardDealtInChoicePacket(
        EHero hero,
        bool withPresetsAndGoldenStart)
    {
        var rules = withPresetsAndGoldenStart
            ? new CustomRunRuleSet
            {
                MutatorIds = [MutatorCatalog.GoldenStart],
                StartingItems = [new PresetCardSpec { TemplateId = CutlassId, Tier = "Silver" }],
                StartingSkills = [new PresetCardSpec { TemplateId = ArmsDealerSkillId, Tier = "Silver" }],
            }
            : new CustomRunRuleSet();

        var (run, engine) = CreateRun(hero: hero, day: 1, hour: 1, seed: 51100 + (int)hero, customRules: rules);
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);
        Assert.Equal(ERunState.Choice, run.State);

        var startSim = run.BuildFullGameSim(startOut.Events);
        Assert.Contains(startSim.Events, e => e is GameSimEventCardDealt);
        Assert.DoesNotContain(startSim.Events, e => e is GameSimEventCardSpawned);

        // 所有开局固有实体（Jules 烤炉 / Hero8 音符 / StartingItems / StartingSkills / GoldenStart）必须已在前置快照落地
        var snap = run.BuildSnapshot();
        if (hero == EHero.Jules)
        {
            Assert.Contains(snap.Cards, c => c.Owner == ECombatantId.Player && c.Type == ECardType.SocketEffect);
            Assert.NotNull(startOut.PreSplitGameSim);
            Assert.Contains(startOut.PreSplitGameSim!.Events, e => e is GameSimEventCardSpawned sp && sp.Type == ECardType.SocketEffect);
            Assert.DoesNotContain(startOut.PreSplitGameSim.Events, e => e is GameSimEventCardDealt);
        }
        if (withPresetsAndGoldenStart)
        {
            Assert.Contains(snap.Cards, c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item);
            Assert.Contains(snap.Cards, c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill);
            Assert.NotNull(startOut.PreSplitGameSim);
            Assert.Contains(startOut.PreSplitGameSim!.Events, e => e is GameSimEventCardSpawned);
            Assert.DoesNotContain(startOut.PreSplitGameSim.Events, e => e is GameSimEventCardDealt);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 5. P1-11（服务端侧）：升级/奇遇赐技（Inspired by Karnok）拆包隔离 CardSpawned 与 Choice CardDealt
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_11_LevelUpInspiredByKarnokGrant_SplitsCardSpawnedSkillFromChoiceCardDealt()
    {
        var (run, engine) = CreateRun(hero: EHero.Mak, day: 4, hour: 3, state: ERunState.LevelUp, level: 5, seed: 51111);
        AddOwnedItem(run, MacheteId, EInventorySection.Hand, slot: 3, ETier.Silver);

        // 构造真实 LvUp 货架，其中包含官服触发 BUG-V1B-02 的 Inspired by Karnok 步骤卡（赐予钻石 Inspired Rage）
        var stepTpl = Repo.Value.GetCard(InspiredByKarnokStepId);
        Assert.NotNull(stepTpl);
        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
        var stepCore = new CardInstance(stepTpl!, CardTier.Bronze, null, "stp_karnok_grant");
        var stepEntry = shop.CreateEntryFromCoreInstance(stepCore, isFree: true, explicitInstanceId: "stp_karnok_grant");
        run.SelectionSet.Clear();
        run.Cards[stepEntry.InstanceId] = stepEntry;
        run.SelectionSet.Add(stepEntry.InstanceId);
        run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = false,
            SelectionIsFree = true,
            CanExit = false,
        };
        run.SelectionsMadeInCurrentContext = 0;

        var cmd = new SelectEncounterCommand { InstanceId = new InstanceId(stepEntry.InstanceId) };
        var outcome = engine.Handle(cmd);
        Assert.Null(outcome.Error);

        // 1) 玩家确实获得钻石技能 Inspired Rage，且最终切回 Choice 态
        var grantedSkill = Assert.Single(run.Cards.Values, c =>
            c.Owner == ECombatantId.Player &&
            c.Type == ECardType.Skill &&
            string.Equals(RunState.ResolveCoreTemplateId(c), InspiredRageSkillId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(grantedSkill.InstanceId, grantedSkill.CoreCard.InstanceId);
        Assert.Equal(ERunState.Choice, run.State);

        // 2) 经 GameServerEndpoints.BuildCommandReply 必须拆成两条独立 NetMessageGameSim：
        //    第 1 包仅含赐技 CardSpawned + PlayerSkillEquipped（零 CardDealt）；
        //    第 2 包仅含后续 Choice CardDealt + StateTransitioned（零 CardSpawned / PlayerSkillEquipped）
        Assert.NotNull(outcome.PreSplitGameSim);
        Assert.NotNull(outcome.SecondaryGameSimEvents);
        var reply = GameServerEndpoints.BuildCommandReply(
            cmd, run, outcome, ERunState.LevelUp, isOwnedSelectMove: false, isFirstAfterReestablish: false,
            out _, out _);
        var agg = Assert.IsType<NetMessageAggregate>(reply);
        var simMsgs = agg.Messages.OfType<NetMessageGameSim>().ToList();
        Assert.Equal(2, simMsgs.Count);

        var firstSim = simMsgs[0].Data;
        var secondSim = simMsgs[1].Data;
        Assert.Contains(firstSim.Events, e => e is GameSimEventCardSpawned sp && sp.Type == ECardType.Skill && sp.InstanceId == grantedSkill.InstanceId);
        Assert.Contains(firstSim.Events, e => e is GameSimEventPlayerSkillEquipped eq && eq.InstanceId == grantedSkill.InstanceId);
        Assert.DoesNotContain(firstSim.Events, e => e is GameSimEventCardDealt);

        Assert.Contains(secondSim.Events, e => e is GameSimEventCardDealt);
        Assert.DoesNotContain(secondSim.Events, e => e is GameSimEventCardSpawned);
        Assert.DoesNotContain(secondSim.Events, e => e is GameSimEventPlayerSkillEquipped);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 6. P1-12：Hour == 6 Fates 赐 XP 推迟至跨天 Day+1 Hour 1 消费 + LevelUp 选技跨小时拆包
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_12_Hour6FatesXpLevelUp_DefersToNextDayHour1_NeverEntersLevelUpAtHour6()
    {
        // 场景 A（BUG-V2-13 L2091）：Day 6 Hour 6 PvP 战败进 Fates，退出/结算时存在 PendingLevelUp
        var (run, engine) = CreateRun(
            hero: EHero.Mak,
            day: 6,
            hour: 6,
            state: ERunState.Encounter,
            hp: 1000,
            level: 6,
            prestige: 1,
            seed: 51121);
        run.FatesUsed = true;
        run.LastCompletedPvpDay = 6;
        run.CurrentEncounterId = FatesTemplateId;
        run.ParentEncounterTemplateId = FatesTemplateId;
        run.SelectionRules = new TSelectionContextRules { CanExit = true };
        run.PendingLevelUps = 1; // 模拟 Fates 步骤赐予 XP 触发升级

        var exitFates = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitFates.Error);

        // 严禁在 Hour == 6 当场进入 LevelUp，必须先跨天至 Day 7 Hour 1 再进入 LevelUp
        Assert.Equal(7u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Contains(exitFates.Events, e => e is GameSimEventRunDayChanged dc && dc.NewDay == 7);
        Assert.Contains(exitFates.Events, e => e is GameSimEventRunHourChanged hc && hc.NewHour == 1);
        Assert.Single(run.SuspendedStates);
        Assert.Equal(ERunState.Choice, run.SuspendedStates.Peek().State);

        // 同时直接校验 ConsumeNextPendingLevelUpToEvents 守卫：在 Hour == 6 且已完成当日 PvP 时自动先跨天再消费升级
        var (runGuard, engGuard) = CreateRun(
            hero: EHero.Vanessa,
            day: 3,
            hour: 6,
            state: ERunState.Encounter,
            level: 4,
            seed: 51122);
        runGuard.LastCompletedPvpDay = 3;
        runGuard.PendingLevelUps = 1;
        var guardEvents = new List<IGameSimEvent>();
        InvokePrivate(engGuard, "ConsumeNextPendingLevelUpToEvents", guardEvents);
        Assert.Equal(4u, runGuard.Day);
        Assert.Equal(1u, runGuard.Hour);
        Assert.Equal(ERunState.LevelUp, runGuard.State);
    }

    [Fact]
    public void P1_12_Hour5LevelUpSkillEquip_AdvancingToHour6Pvp_SplitsSkillEquipAndHour6Dealt()
    {
        // 场景 B（BUG-V2-13 L1349）：Day 7 Hour 5 LevelUp 选技能退出并推进到 Hour 6 PvP Choice
        var (run, engine) = CreateRun(
            hero: EHero.Karnok,
            day: 7,
            hour: 5,
            state: ERunState.LevelUp,
            level: 7,
            seed: 51123);
        run.PendingAdvanceHourAfterLevelUp = true;
        run.GainedHourXpThisHour = true;
        run.GainedAdvanceHourXpThisHour = true;
        run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = false,
            SelectionIsFree = true,
            CanExit = false,
        };

        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
        var skillTpl = Pool.Value.Get(ArmsDealerSkillId);
        Assert.NotNull(skillTpl);
        var skillCand = shop.CreateSkillCandidate(skillTpl!, ETier.Gold, isFree: true);
        run.SelectionSet.Clear();
        run.Cards[skillCand.InstanceId] = skillCand;
        run.SelectionSet.Add(skillCand.InstanceId);

        var cmd = new SelectSkillCommand { InstanceId = new InstanceId(skillCand.InstanceId) };
        var outcome = engine.Handle(cmd);
        Assert.Null(outcome.Error);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(6u, run.Hour);

        // 必须拆为两条独立 NetMessageGameSim，彻底消除 PlayerSkillEquipped + RunHourChanged(->6) + CardDealt 同包 10.1s 时钟死锁
        var reply = GameServerEndpoints.BuildCommandReply(
            cmd, run, outcome, ERunState.LevelUp, isOwnedSelectMove: false, isFirstAfterReestablish: false,
            out _, out _);
        var agg = Assert.IsType<NetMessageAggregate>(reply);
        var simMsgs = agg.Messages.OfType<NetMessageGameSim>().ToList();
        Assert.Equal(2, simMsgs.Count);

        var firstSim = simMsgs[0].Data;
        var secondSim = simMsgs[1].Data;
        Assert.Contains(firstSim.Events, e => e is GameSimEventPlayerSkillEquipped eq && eq.InstanceId == skillCand.InstanceId);
        Assert.DoesNotContain(firstSim.Events, e => e is GameSimEventRunHourChanged);
        Assert.DoesNotContain(firstSim.Events, e => e is GameSimEventCardDealt);

        Assert.Contains(secondSim.Events, e => e is GameSimEventRunHourChanged hc && hc.NewHour == 6);
        Assert.Contains(secondSim.Events, e => e is GameSimEventCardDealt);
        Assert.DoesNotContain(secondSim.Events, e => e is GameSimEventPlayerSkillEquipped);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 7. P2-03：Knee Brace 战后加 MaxHP 后商店满血（BasePlayerAttributes["Health"] 严格同步）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P2_03_KneeBraceFightEndedHealthMaxIncrease_SyncsBaseHealth_NoPostCombatShopMissingHp()
    {
        var (run, engine) = CreateRun(
            hero: EHero.Hero8,
            day: 2,
            hour: 6,
            state: ERunState.Choice,
            hp: 550,
            level: 3,
            seed: 52031);

        // 佩戴官方 Knee Brace（TTriggerOnFightEnded(PVP) -> HealthMax +100）
        AddOwnedItem(run, KneeBraceId, EInventorySection.Hand, slot: 3, ETier.Bronze);
        AddOwnedItem(run, CutlassId, EInventorySection.Hand, slot: 5, ETier.Silver);

        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
        shop.DealHourEncounters();
        run.PendingPvpGhost = BuildGhostBoard(day: 2, ghostHp: 500, hero: "Dooley");
        string pvpCardId = run.SelectionSet.Single(id => run.Cards[id].Type == ECardType.PvpEncounter);

        var pvpOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCardId) });
        Assert.Null(pvpOut.Error);
        Assert.Equal(3u, run.Day);
        Assert.Equal(1u, run.Hour);

        // 战后 Knee Brace 提升 HealthMax 后，PlayerAttributes[Health] 与 BasePlayerAttributes["Health"] 必须同步满血（绝不再出现 550/650）
        int expectedMaxHp = run.PlayerAttributes[EPlayerAttributeType.HealthMax];
        Assert.True(expectedMaxHp > 550, $"Knee Brace PvP 战后 HealthMax 应大于战前 550，实际 {expectedMaxHp}");
        Assert.Equal(expectedMaxHp, run.PlayerAttributes[EPlayerAttributeType.Health]);
        Assert.True(run.BasePlayerAttributes.TryGetValue("Health", out double baseHpAfterPvp));
        Assert.Equal(expectedMaxHp, (int)Math.Round(baseHpAfterPvp));

        // 模拟跨日/进店后重新装配 PlayerState，验证 ApplyLayeredPlayerStateTo 从 BasePlayerAttributes 读取最新满血而非旧 550
        var psAfter = new PlayerState("p1", "Hero8", HeroNames.Hero8, 550, 20, 30, 10);
        run.ApplyLayeredPlayerStateTo(psAfter);
        Assert.Equal(expectedMaxHp, psAfter.CurrentHealth);
        Assert.Equal(expectedMaxHp, psAfter.HealthMax);

        // 同步覆盖 MutatePlayerHealthMax 与 ApplyDirectLevelUps 两条收敛写入路径
        var mutateEvents = new List<IGameSimEvent>();
        InvokePrivate(engine, "MutatePlayerHealthMax", 50, mutateEvents, true);
        int afterMutateMax = run.PlayerAttributes[EPlayerAttributeType.HealthMax];
        Assert.Equal(expectedMaxHp + 50, afterMutateMax);
        Assert.Equal(afterMutateMax, run.PlayerAttributes[EPlayerAttributeType.Health]);
        Assert.Equal(afterMutateMax, (int)Math.Round(run.BasePlayerAttributes["Health"]));

        InvokePrivate(engine, "ApplyDirectLevelUps", 1, mutateEvents, null, EExperienceType.Item);
        int afterDirectLvlMax = run.PlayerAttributes[EPlayerAttributeType.HealthMax];
        Assert.True(afterDirectLvlMax > afterMutateMax);
        Assert.Equal(afterDirectLvlMax, run.PlayerAttributes[EPlayerAttributeType.Health]);
        Assert.Equal(afterDirectLvlMax, (int)Math.Round(run.BasePlayerAttributes["Health"]));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 2（实机反馈）：巧克力（Candy Stash）与给 5 金币（Cache of Riches / Borrow）等直效奇遇
    // 必须走官方瞬发直跳（_currentEncounterIsInstant），绝不进入空 ERunState.Encounter 房间
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bug2_InstantEncounters_CandyStashAndCacheOfRiches_ResolveWithoutEmptyEncounterRoom()
    {
        const string candyStashDay1To3Id = "e6e09e6a-ebe0-46f8-bfbb-716b74f78bc6";
        const string cacheOfRichesDay1To2Id = "937489c5-22bf-4190-89b1-f396894c85f1";

        static RunState.CardEntry InjectChoiceEncounter(RunState run, string encTemplateId)
        {
            var entry = new RunState.CardEntry
            {
                InstanceId = $"enc_{Guid.NewGuid():N}"[..16],
                TemplateId = Guid.Parse(encTemplateId),
                Type = ECardType.EventEncounter,
                Owner = ECombatantId.Opponent,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_0,
                Tier = ETier.Bronze,
                Size = ECardSize.Medium,
            };
            run.Cards[entry.InstanceId] = entry;
            run.SelectionSet.Add(entry.InstanceId);
            return entry;
        }

        // 1) Candy Stash（给 3 块青铜巧克力）在 Day 1 Hour 1：
        //    必须不进空 Encounter，直接发巧克力进手牌/背包并瞬发推进到 Hour 2 Choice，且经 PreSplitGameSim + SecondaryGameSimEvents 严格拆包隔离
        var (runCandy, engineCandy) = CreateRun(hero: EHero.Vanessa, day: 1, hour: 1, seed: 50911);
        int ownedBeforeCandy = runCandy.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item);
        var candyCard = InjectChoiceEncounter(runCandy, candyStashDay1To3Id);

        var candyOutcome = engineCandy.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(candyCard.InstanceId) });
        Assert.Null(candyOutcome.Error);
        Assert.Equal(ERunState.Choice, runCandy.State);
        Assert.Equal(2u, runCandy.Hour);
        Assert.Null(runCandy.CurrentEncounterId);
        Assert.Null(runCandy.ParentEncounterTemplateId);
        int ownedAfterCandy = runCandy.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item);
        Assert.True(ownedAfterCandy >= ownedBeforeCandy + 3, $"Candy Stash 应发 3 块巧克力入包/手牌，实际新增 {ownedAfterCandy - ownedBeforeCandy}");

        Assert.NotNull(candyOutcome.PreSplitGameSim);
        Assert.NotNull(candyOutcome.SecondaryGameSimEvents);
        Assert.Contains(candyOutcome.PreSplitGameSim!.Events, e => e is GameSimEventCardSpawned);
        Assert.DoesNotContain(candyOutcome.PreSplitGameSim.Events, e => e is GameSimEventCardDealt);
        Assert.DoesNotContain(candyOutcome.PreSplitGameSim.Events, e => e is GameSimEventStateTransitioned);
        Assert.Null(candyOutcome.PreSplitGameSim.CurrentState!.CurrentEncounterId);

        Assert.DoesNotContain(candyOutcome.SecondaryGameSimEvents!, e => e is GameSimEventCardSpawned);
        Assert.Contains(candyOutcome.SecondaryGameSimEvents!, e => e is GameSimEventStateTransitioned st && st.ToState == ERunState.Choice);
        Assert.Contains(candyOutcome.SecondaryGameSimEvents!, e => e is GameSimEventRunHourChanged hr && hr.NewHour == 2u);
        Assert.Contains(candyOutcome.SecondaryGameSimEvents!, e => e is GameSimEventCardDealt);

        // 2) Cache of Riches（给金币）在 Day 1 Hour 1：
        //    无发卡，单包瞬发加金币并直接推进至 Hour 2 Choice，绝不进入空 Encounter
        var (runGold, engineGold) = CreateRun(hero: EHero.Vanessa, day: 1, hour: 1, seed: 50912);
        int goldBefore = runGold.PlayerAttributes[EPlayerAttributeType.Gold];
        var goldCard = InjectChoiceEncounter(runGold, cacheOfRichesDay1To2Id);

        var goldOutcome = engineGold.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(goldCard.InstanceId) });
        Assert.Null(goldOutcome.Error);
        Assert.Equal(ERunState.Choice, runGold.State);
        Assert.Equal(2u, runGold.Hour);
        Assert.Null(runGold.CurrentEncounterId);
        Assert.True(runGold.PlayerAttributes[EPlayerAttributeType.Gold] > goldBefore, "Cache of Riches 必须即时发放金币");
        Assert.Null(goldOutcome.PreSplitGameSim);
        Assert.Contains(goldOutcome.Events, e => e is GameSimEventStateTransitioned st && st.ToState == ERunState.Choice);
        Assert.Contains(goldOutcome.Events, e => e is GameSimEventRunHourChanged hr && hr.NewHour == 2u);
        Assert.Contains(goldOutcome.Events, e => e is GameSimEventCardDealt);
        Assert.DoesNotContain(goldOutcome.Events, e => e is GameSimEventStateTransitioned st && st.ToState == ERunState.Encounter);

        // 3) Day 2 Hour 5 选 Cache of Riches 且距离升级仅差 1 XP：
        //    必须瞬发直入 LevelUp（不进空 Encounter），且 Hour 保持 5（绝不提前切到 Hour 6 触发客户端时钟锁死）
        var (runLv, engineLv) = CreateRun(hero: EHero.Vanessa, day: 2, hour: 5, seed: 50913);
        int xpPerLevel = Math.Max(1, Mode.Value.ExperiencePerLevel);
        runLv.SetBasePlayerAttribute(EPlayerAttributeType.Experience, xpPerLevel - 1);
        var goldCardH5 = InjectChoiceEncounter(runLv, cacheOfRichesDay1To2Id);

        var lvOutcome = engineLv.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(goldCardH5.InstanceId) });
        Assert.Null(lvOutcome.Error);
        Assert.Equal(ERunState.LevelUp, runLv.State);
        Assert.Equal(5u, runLv.Hour);
        Assert.DoesNotContain(lvOutcome.Events, e => e is GameSimEventRunHourChanged hr && hr.NewHour == 6u);
    }
}
