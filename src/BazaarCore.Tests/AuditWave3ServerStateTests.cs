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
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.TempoNet;
using BazaarServer.Tournament;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 3 Batch 3B（服务端剩余全部 24 项 P1 收口）全量回归单测：
/// 覆盖 P1-02、P1-09、P1-10、P1-11、P1-13、P1-14、P1-16、P1-17、P1-18、
/// P1-26、P1-28、P1-30~P1-41、P1-44、P1-55。
/// </summary>
public sealed class AuditWave3ServerStateTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());
    private static readonly Lazy<CardPool> Pool = new(() => CardPool.Load(ResolveDataPath("GameData.db")));
    private static readonly Lazy<List<BazaarServer.Data.MonsterTemplate>> Monsters =
        new(() => MonsterPool.Load(ResolveDataPath("GameData.db")));
    private static readonly Lazy<LevelUpTable> LevelUps =
        new(() => LevelUpTable.Load(ResolveDataPath("GameData.db")));
    private static readonly Lazy<GameModeConfig> Mode =
        new(() => GameModeConfig.Load(ResolveDataPath("GameData.db")));

    private static string ResolveDataPath(string fileName)
    {
        for (string? dir = AppContext.BaseDirectory; dir != null; dir = Directory.GetParent(dir)?.FullName)
        {
            string candidate = Path.Combine(dir, "dist", "data", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(@"e:\thebazaar\dist\data", fileName);
    }

    private static string CreateTempDbPath(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bazaar_w3b_tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{tag}_{Guid.NewGuid():N}.db");
    }

    private static void TryDeleteTempDb(string dbPath)
    {
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        try { if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal"); } catch { }
        try { if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm"); } catch { }
    }

    private static (RunState Run, RunEngine Engine) CreateRun(
        EHero hero = EHero.Vanessa,
        CustomRunRuleSet? customRules = null,
        uint day = 2,
        uint hour = 1,
        ERunState state = ERunState.Choice,
        int gold = 50,
        int healthMax = 400,
        ushort unlockedSlots = 0b00_0111_1000,
        bool startSelectionDone = true,
        AccountStore? accounts = null,
        ChallengeService? challenges = null,
        Persistence? db = null,
        LobbyStore? lobbies = null,
        ITournamentPvpCoordinator? pvpCoordinator = null,
        string? accountId = null,
        Guid? tournamentSessionId = null)
    {
        var run = new RunState
        {
            RunId = "w3b_" + Guid.NewGuid().ToString("N")[..10],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = tournamentSessionId.HasValue ? EPlayMode.Tournament : EPlayMode.Ranked,
            AccountId = accountId ?? Guid.NewGuid().ToString("N"),
            Username = "Wave3Tester",
            TournamentSessionId = tournamentSessionId,
            Day = day,
            Hour = hour,
            State = state,
            UnlockedSlots = unlockedSlots,
            StartSelectionDone = startSelectionDone,
            CustomRules = (customRules ?? new CustomRunRuleSet()).CloneNormalized(),
            RngSeed = 20261122,
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
            new PvpGhostPool(),
            lobbies: lobbies,
            pvpCoordinator: pvpCoordinator,
            accounts: accounts,
            challenges: challenges,
            db: db);

        return (run, engine);
    }

    private static CardTemplate FindSmallItemTemplate(string? excludeId = null)
    {
        return Repo.Value.CardsById.Values.First(c =>
            string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Size, "Small", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(c.Id, out _) &&
            !string.Equals(c.Id, excludeId, StringComparison.OrdinalIgnoreCase) &&
            !c.Tags.Contains("Unsellable", StringComparer.OrdinalIgnoreCase) &&
            c.Tiers != null &&
            c.Tiers.ContainsKey("Bronze") &&
            c.Tiers.ContainsKey("Silver"));
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        CardTemplate tpl,
        ETier tier,
        EInventorySection section,
        int slot,
        EEnchantmentType? ench = null,
        string? instanceId = null)
    {
        string id = instanceId ?? "itm_" + Guid.NewGuid().ToString("N")[..8];
        var core = new CardInstance(tpl, ShopEngine.ToCoreTier(tier), ench?.ToString(), id);
        var grid = section == EInventorySection.Stash ? run.StashGrid : run.HandGrid;
        Assert.True(grid.TryPlaceOrShift(core, slot), $"未能放置测试物品到 {section}:{slot}");

        var entry = new RunState.CardEntry
        {
            InstanceId = id,
            TemplateId = Guid.Parse(tpl.Id),
            CustomCardId = tpl.Id,
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tpl.Size),
            Tier = tier,
            Enchantment = ench,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)core.StartSlot,
            State = ECardState.Alive,
            CoreCard = core,
        };
        var (buy, sell) = ShopEngine.PriceFor(entry.Size, tier);
        entry.Attributes[ECardAttributeType.BuyPrice] = buy;
        entry.Attributes[ECardAttributeType.SellPrice] = sell;
        run.Cards[id] = entry;
        return entry;
    }

    // ── P1-09：局外光环目标差分精确发射 AppliedTo / RemovedFrom ─────────────

    [Fact]
    public void P1_09_OutOfCombatAuraDiffs_EmitsExactAppliedAndRemovedTargets()
    {
        var (run, _) = CreateRun();
        var events1 = new List<IGameSimEvent>();
        var dirty1 = new HashSet<string>(StringComparer.Ordinal);

        // 第一轮：Stove 光环激活作用于 itm_a
        var snap1 = new List<AuraEngine.AuraEffectTargetsSnapshot>
        {
            new("stove_1", "aura_heat", new[] { "itm_a" }, Array.Empty<string>())
        };
        run.EmitOutOfCombatAuraDiffs(snap1, events1, dirtyCardIds: dirty1);

        var appliedEv = Assert.Single(events1.OfType<GameSimEventEffectAuraExecuted>());
        Assert.Equal("stove_1", appliedEv.Source?.Value);
        Assert.Equal("aura_heat", appliedEv.EffectId);
        Assert.Contains(appliedEv.AppliedTo!, t => t is EffectTargetCard c && c.Target.Value == "itm_a");
        Assert.True(appliedEv.RemovedFrom == null || appliedEv.RemovedFrom.Count == 0);
        Assert.Contains("stove_1", dirty1);
        Assert.Contains("itm_a", dirty1);

        // 第二轮：物品离开炉灶格，光环快照目标清空，应精准发射 RemovedFrom 驱动退活视觉
        var events2 = new List<IGameSimEvent>();
        var dirty2 = new HashSet<string>(StringComparer.Ordinal);
        run.EmitOutOfCombatAuraDiffs(Array.Empty<AuraEngine.AuraEffectTargetsSnapshot>(), events2, dirtyCardIds: dirty2);

        var removedEv = Assert.Single(events2.OfType<GameSimEventEffectAuraExecuted>());
        Assert.Equal("stove_1", removedEv.Source?.Value);
        Assert.Equal("aura_heat", removedEv.EffectId);
        Assert.Contains(removedEv.RemovedFrom!, t => t is EffectTargetCard c && c.Target.Value == "itm_a");
        Assert.True(removedEv.AppliedTo == null || removedEv.AppliedTo.Count == 0);
        Assert.Contains("stove_1", dirty2);
        Assert.Contains("itm_a", dirty2);
    }

    // ── P1-10：战后临时变形还原发射 GameSimEventCardTransformReverted ──────────

    [Fact]
    public void P1_10_PostCombatTemporaryTransforms_EmitsCardTransformRevertedAndSyncsOriginalCard()
    {
        var (run, engine) = CreateRun();
        var tpl = FindSmallItemTemplate();
        var owned = AddOwnedItem(run, tpl, ETier.Bronze, EInventorySection.Hand, 3, instanceId: "itm_orig_tr");

        var records = new List<CombatTemporaryTransformRecord>
        {
            new()
            {
                OwnerPlayerId = "player",
                OriginalCardInstanceId = owned.InstanceId,
                OriginalTemplateId = tpl.Id,
                OriginalCardType = "Item",
                OriginalSection = "Hand",
                OriginalSlot = 3,
                TransformedInstanceIds = new List<string> { "tmp_trans_99" },
            }
        };

        var despawnEvents = new List<IGameSimEvent>();
        var dirty = new HashSet<string>(StringComparer.Ordinal);

        var method = typeof(RunEngine).GetMethod(
            "EmitPostCombatTemporaryTransformReverts",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(engine, new object?[] { records, despawnEvents, dirty });

        var reverted = Assert.Single(despawnEvents.OfType<GameSimEventCardTransformReverted>());
        Assert.Equal(owned.InstanceId, reverted.OriginalCard.InstanceId);
        Assert.Equal(owned.TemplateId.ToString(), reverted.OriginalCard.TemplateId);
        Assert.Equal(EInventorySection.Hand, reverted.OriginalCard.Section);
        Assert.Equal(EContainerSocketId.Socket_3, reverted.OriginalCard.Socket);
        Assert.Contains("tmp_trans_99", reverted.TransformedCardInstanceIds);
        Assert.Contains(owned.InstanceId, dirty);

        var despawnSim = run.BuildFullGameSim(despawnEvents);
        Assert.True(despawnSim.Cards.ContainsKey(owned.InstanceId));
    }

    // ── P1-11：属性键移除发射 EAttributeDeltaType.Delete，同时保护战后强制归零 ─

    [Fact]
    public void P1_11_AttributeKeyRemoved_EmitsDeleteDeltaWhilePreservingPendingForceSync()
    {
        var (run, _) = CreateRun();
        var tpl = FindSmallItemTemplate();
        var item = AddOwnedItem(run, tpl, ETier.Bronze, EInventorySection.Hand, 3);

        // 先附加一个非基模临时属性，模拟此前已向客户端下发过
        item.Attributes[ECardAttributeType.BurnApplyAmount] = 8;
        item.Attributes[ECardAttributeType.Freeze] = 1000;
        var firstEmit = RunSnapshotSerializer.ToSimCard(item, clearPendingForceSync: true);
        Assert.Equal(EAttributeDeltaType.Update, firstEmit.Attributes[ECardAttributeType.BurnApplyAmount].DeltaType);

        // 战后显式归零 Freeze，但 BurnApplyAmount 直接因临时修饰撤回归零移除
        item.PendingForceSyncAttributes[ECardAttributeType.Freeze] = 0;
        _ = run.SyncEntryFromCoreCardAndDiff(item, Repo.Value.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        Assert.False(item.Attributes.ContainsKey(ECardAttributeType.BurnApplyAmount));
        Assert.True(item.PendingForceSyncAttributes.ContainsKey(ECardAttributeType.Freeze));

        var secondEmit = RunSnapshotSerializer.ToSimCard(item, clearPendingForceSync: true);
        Assert.True(secondEmit.Attributes.TryGetValue(ECardAttributeType.BurnApplyAmount, out var delDelta));
        Assert.Equal(EAttributeDeltaType.Delete, delDelta!.DeltaType);

        Assert.True(secondEmit.Attributes.TryGetValue(ECardAttributeType.Freeze, out var zeroDelta));
        Assert.Equal(EAttributeDeltaType.Update, zeroDelta!.DeltaType);
        Assert.Equal(0, zeroDelta.Value);
    }

    // ── P1-13：完赛结算通行证 XP 落盘 + 锦标赛 TournamentEndRunPayload ───────

    [Fact]
    public void P1_13_RunCompleted_PersistsAccountPassXpAndBuildsTournamentEndRunPayload()
    {
        string dbPath = CreateTempDbPath("p1_13");
        try
        {
            using var db = new Persistence(dbPath);
            using var accounts = new AccountStore(db);
            var acc = accounts.GetOrCreateByName("TourneyWinner");
            var tsid = Guid.NewGuid();

            var lobbies = new LobbyStore(db);
            var lobby = lobbies.Create(
                acc,
                "P1-13 Test Lobby",
                "Tournament",
                playerCap: 4,
                isPublic: true,
                rules: new CustomRunRuleSet());
            lobby.TournamentSessionId = tsid;

            var (run, engine) = CreateRun(
                day: 10,
                hour: 6,
                state: ERunState.Choice,
                accounts: accounts,
                db: db,
                lobbies: lobbies,
                accountId: acc.AccountId.ToString("N"),
                tournamentSessionId: tsid);
            run.Victories = 10;
            run.PlayerAttributes[EPlayerAttributeType.Experience] = 2; // 局内残留小额经验，不应错当通行证 XP

            var tryEnd = typeof(RunEngine).GetMethod("TryEndRunToEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var events = new List<IGameSimEvent>();
            bool ended = (bool)tryEnd.Invoke(engine, new object[] { events, true, false })!;

            Assert.True(ended);
            var completed = Assert.Single(events.OfType<GameSimEventRunCompleted>());
            int expectedXp = RunEngine.ComputeAccountPassXpEarned(10, 10, isVictory: true, isConceded: false);
            Assert.True(expectedXp > 100);
            Assert.Equal(expectedXp, completed.Rewards.XpEarned);
            Assert.NotNull(completed.Rewards.TournamentEndRun);
            Assert.Equal(tsid, completed.Rewards.TournamentEndRun!.TournamentSessionId);
            Assert.Equal(1, completed.Rewards.TournamentEndRun.Place);
            Assert.True(completed.Rewards.TournamentEndRun.TotalPlayers >= 1);
            Assert.True(completed.Rewards.TournamentEndRun.IsChampion);

            var seasonProg = db.LoadSeasonTrackProgress(acc.AccountId, engine.LastAwardedPassSeasonId);
            Assert.True(seasonProg.Experience >= expectedXp);
        }
        finally
        {
            TryDeleteTempDb(dbPath);
        }
    }

    // ── P1-14：SimPvpOpponent 填充并无损序列化 Rank / Titles / Loadout / Collection ─

    [Fact]
    public void P1_14_PvpOpponent_PopulatesAndRoundTripsRankTitlesLoadoutAndCollection()
    {
        string dbPath = CreateTempDbPath("p1_14");
        try
        {
            using var db = new Persistence(dbPath);
            using var accounts = new AccountStore(db);
            var acc = accounts.GetOrCreateByName("RichPvpPlayer");
            acc.Victories = 25;
            acc.RunsCompleted = 40;
            string boardGuid = Guid.NewGuid().ToString("D");
            string carpetGuid = Guid.NewGuid().ToString("D");
            string skinGuid = Guid.NewGuid().ToString("D");
            acc.HeroLoadouts["Vanessa"] = new AccountStore.HeroLoadoutState
            {
                HeroId = "Vanessa",
                BoardId = boardGuid,
                CarpetId = carpetGuid,
                CardBackId = Guid.NewGuid().ToString("D"),
                HeroSkinId = skinGuid,
                CardSkinIds = new List<string> { Guid.NewGuid().ToString("D") },
            };

            var (run, engine) = CreateRun(accounts: accounts);
            var buildMethod = typeof(RunEngine).GetMethod("BuildRichSimPvpOpponent", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var opp = (SimPvpOpponent)buildMethod.Invoke(engine, new object[]
            {
                acc.Username,
                acc.AccountId.ToString("N"),
                EHero.Vanessa,
                8u,
                14,
                9,
                1500
            })!;

            Assert.NotNull(opp.Rank);
            Assert.False(string.IsNullOrWhiteSpace(opp.TitlePrefix));
            Assert.False(string.IsNullOrWhiteSpace(opp.TitleSuffix));
            Assert.NotNull(opp.PlayerLoadout);
            Assert.Equal(boardGuid, opp.PlayerLoadout!.boardId);
            Assert.Equal(carpetGuid, opp.PlayerLoadout.carpetId);
            Assert.NotNull(opp.PlayerCollection);
            Assert.True(opp.PlayerCollection!.Count >= 3);

            run.CurrentPvpOpponent = opp;
            string json = RunSnapshotSerializer.Serialize(run, startSelectionDone: true);
            var restored = RunSnapshotSerializer.Deserialize(json, Repo.Value);

            Assert.NotNull(restored.CurrentPvpOpponent);
            Assert.Equal(opp.Rank, restored.CurrentPvpOpponent!.Rank);
            Assert.Equal(opp.TitlePrefix, restored.CurrentPvpOpponent.TitlePrefix);
            Assert.Equal(opp.TitleSuffix, restored.CurrentPvpOpponent.TitleSuffix);
            Assert.Equal(boardGuid, restored.CurrentPvpOpponent.PlayerLoadout?.boardId);
            Assert.Equal(opp.PlayerCollection.Count, restored.CurrentPvpOpponent.PlayerCollection?.Count);
        }
        finally
        {
            TryDeleteTempDb(dbPath);
        }
    }

    // ── P1-16：MutatePlayerGold / MutatePlayerHealthMax 驱动 OnPlayerAttributeChanged ─

    [Fact]
    public void P1_16_MutatePlayerGoldAndHealthMax_UpdatesStateAndRespectsReentrancyGuard()
    {
        var (run, engine) = CreateRun(gold: 20, healthMax: 400);

        // 尝试从真实数据库寻找带 TTriggerOnPlayerAttributeChanged 触发器的物品装入盘面验证完整联动
        var listenerTpl = Repo.Value.CardsById.Values.FirstOrDefault(c =>
            string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(c.Id, out _) &&
            c.Abilities.Values.Any(a => string.Equals(a.Trigger?.Type, "TTriggerOnPlayerAttributeChanged", StringComparison.OrdinalIgnoreCase)));
        if (listenerTpl != null)
        {
            AddOwnedItem(run, listenerTpl, ETier.Bronze, EInventorySection.Hand, 3);
        }

        var mutateGold = typeof(RunEngine).GetMethod("MutatePlayerGold", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mutateHp = typeof(RunEngine).GetMethod("MutatePlayerHealthMax", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var events = new List<IGameSimEvent>();

        mutateGold.Invoke(engine, new object[] { 15, events, true });
        Assert.True(run.PlayerAttributes[EPlayerAttributeType.Gold] >= 35);
        Assert.True(run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0) >= 15);

        mutateHp.Invoke(engine, new object[] { 100, events, true });
        Assert.True(run.PlayerAttributes[EPlayerAttributeType.HealthMax] >= 500);
        Assert.True(run.PlayerAttributes[EPlayerAttributeType.Health] <= run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
    }

    // ── P1-17：OutOfCombatBatch 请求队列消费（Reroll / DealSelection / ExitSelection）─

    [Fact]
    public void P1_17_OutOfCombatBatch_ConsumesRerollDealAndExitRequests()
    {
        var (run, engine) = CreateRun(state: ERunState.Choice, day: 2, hour: 2);
        var consumeMethod = typeof(RunEngine).GetMethod("ConsumeOutOfCombatBatchRequests", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // 1. AllEncounters 刷新应重新铺开当前小时 Choice 遭遇
        var rerollBatch = new OutOfCombatBatchResult();
        rerollBatch.RerollRequests.Add("AllEncounters");
        var rerollEvents = new List<IGameSimEvent>();
        consumeMethod.Invoke(engine, new object[] { rerollBatch, rerollEvents });
        Assert.Equal(ERunState.Choice, run.State);
        Assert.True(run.SelectionSet.Count > 0);
        Assert.Contains(rerollEvents, e => e is GameSimEventCardDealt);

        // 2. DealtSelectionRequests 应把卡牌发入货架 SelectionSet
        run.State = ERunState.Encounter;
        var tpl = FindSmallItemTemplate();
        var dealBatch = new OutOfCombatBatchResult();
        dealBatch.DealtSelectionRequests.Add((new List<CardInstance> { new(tpl, CardTier.Bronze, null, "dealt_1") }, "Replace"));
        var dealEvents = new List<IGameSimEvent>();
        consumeMethod.Invoke(engine, new object[] { dealBatch, dealEvents });
        Assert.Single(run.SelectionSet);
        Assert.Contains(dealEvents, e => e is GameSimEventCardDealt);

        // 3. ExitSelectionRequested 应清空替换集并安全退出当前 Encounter
        run.ActiveReplacementSetId = "rep_test";
        var exitBatch = new OutOfCombatBatchResult { ExitSelectionRequested = true };
        var exitEvents = new List<IGameSimEvent>();
        consumeMethod.Invoke(engine, new object[] { exitBatch, exitEvents });
        Assert.Null(run.ActiveReplacementSetId);
        Assert.NotEqual(ERunState.Encounter, run.State);
    }

    // ── P1-18：RerollCostModifier 叠加与 0 下限 ──────────────────────────────

    [Fact]
    public void P1_18_RerollCostModifier_AppliesFloorZeroAndSyncsFromAuraLayer()
    {
        var (run, engine) = CreateRun(state: ERunState.Encounter, gold: 20);
        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);

        // 纯光环层负修正：基础 2 + 光环 -5 => effective 0（不得为负）
        run.BaseRerollCost = 2;
        run.PlayerAuraAdd["RerollCostModifier"] = -5;
        Assert.Equal(0u, shop.ComputeEffectiveRerollCost(2));
        shop.RefreshEffectiveRerollCost();
        Assert.Equal(0u, run.RerollCost);

        // 有效总修正为 -2 时：基础 4 + (-2) => effective 2
        run.PlayerAttributes[EPlayerAttributeType.RerollCostModifier] = -2;
        Assert.Equal(2u, shop.ComputeEffectiveRerollCost(4));

        // 验证 SyncEffectivePlayerAttributesFromCore 不漏同步仅存在于 AuraAdd 的 RerollCostModifier
        run.PlayerAttributes.Remove(EPlayerAttributeType.RerollCostModifier);
        var ps = new PlayerState("player", "P1", "Vanessa", 400, 20, 20, 5);
        ps.AuraAdd["RerollCostModifier"] = -2;
        var syncMethod = typeof(RunEngine).GetMethod("SyncEffectivePlayerAttributesFromCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        syncMethod.Invoke(engine, new object[] { ps });
        Assert.Equal(-2, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.RerollCostModifier, 0));
    }

    // ── P1-26：聚合包 [GameSim, GameStateSync] 顺序 & cheat restart 终局拦截与彻底清理 ─

    [Fact]
    public void P1_26_AggregateSyncOrderAndCheatRestart_EnforcesOrderAndRejectsTerminal()
    {
        var (run, engine) = CreateRun(state: ERunState.Choice);
        run.BossRushWavesCleared = 4;
        run.EndlessTenWinCelebrated = true;
        run.DailyRerollSequence = 3;
        run.ActiveExpeditionId = "exp_temple";
        run.CurrentBoardId = "board_override_x";
        run.ActiveShelfDiscounts["itm_old"] = 2;

        // 1. 非终局 cheat restart 复用 StartRun() 全量清理并设置 ForceStateSync=true
        var restartCmd = new CheatCommand { Args = new List<string> { "restart" } };
        var restartOut = engine.Handle(restartCmd);
        Assert.Null(restartOut.Error);
        Assert.True(restartOut.ForceStateSync);
        Assert.Equal(0, run.BossRushWavesCleared);
        Assert.False(run.EndlessTenWinCelebrated);
        Assert.Equal(0, run.DailyRerollSequence);
        Assert.Null(run.ActiveExpeditionId);
        Assert.Null(run.CurrentBoardId);
        Assert.Empty(run.ActiveShelfDiscounts);

        // 2. BuildCommandReply 顺序必须是 [NetMessageGameSim, NetMessageGameStateSync]（GameStateSync 居尾且不重复）
        var reply = GameServerEndpoints.BuildCommandReply(
            restartCmd,
            run,
            restartOut,
            ERunState.Choice,
            isOwnedSelectMove: false,
            isFirstAfterReestablish: false,
            out _,
            out _);
        var agg = Assert.IsType<NetMessageAggregate>(reply);
        Assert.Equal(2, agg.Messages.Count);
        Assert.IsType<NetMessageGameSim>(agg.Messages[0]);
        Assert.IsType<NetMessageGameStateSync>(agg.Messages[1]);

        // 3. 终局态拒绝 cheat restart
        run.State = ERunState.EndRunDefeat;
        var blocked = engine.Handle(new CheatCommand { Args = new List<string> { "restart" } });
        Assert.NotNull(blocked.Error);
        Assert.Equal(EResponseErrorType.NotAllowed, blocked.Error!.ErrorType);
    }

    // ── P1-28：ResolveWireCurrentEncounterId 统一 4 处下发路径 ───────────────

    [Fact]
    public void P1_28_ResolveWireCurrentEncounterId_NormalizesEmptyChildStepAndModIds()
    {
        var (run, _) = CreateRun(state: ERunState.Encounter);
        string validParentGuid = Guid.NewGuid().ToString("D");

        // 空串 / 空白 → null
        run.CurrentEncounterId = "   ";
        run.ParentEncounterTemplateId = null;
        Assert.Null(run.ResolveWireCurrentEncounterId());
        Assert.Null(run.BuildSnapshot().CurrentState!.CurrentEncounterId);

        // 非 GUID Mod 遭遇且有合法父级 GUID → 安全回溯到父级 GUID
        run.CurrentEncounterId = "mod_custom_step_1";
        run.ParentEncounterTemplateId = validParentGuid;
        Assert.Equal(validParentGuid, run.ResolveWireCurrentEncounterId());
        Assert.Equal(validParentGuid, run.BuildFullGameSim(new List<IGameSimEvent>()).CurrentState!.CurrentEncounterId);

        // 子步 EncounterStep 优先回溯到合法父级 EventEncounter GUID
        var stepTpl = Repo.Value.CardsById.Values.FirstOrDefault(c =>
            string.Equals(c.Type, "EncounterStep", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(c.Id, out _));
        if (stepTpl != null)
        {
            run.CurrentEncounterId = stepTpl.Id;
            run.ParentEncounterTemplateId = validParentGuid;
            string? resolved = run.ResolveWireCurrentEncounterId();
            Assert.Equal(validParentGuid, resolved);
            var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(run);
            var trans = Assert.Single(resumeEvents.OfType<GameSimEventStateTransitioned>());
            Assert.Equal(validParentGuid, trans.CurrentEncounterId);
        }
    }

    // ── P1-30：品阶与持久/当日/本小时三层附魔无损快照持久化 ──────────────────

    [Fact]
    public void P1_30_CardSnapshot_RoundTripsPermanentAndTemporaryTierAndEnchantmentLayers()
    {
        var (run, _) = CreateRun();
        var tpl = FindSmallItemTemplate();
        var item = AddOwnedItem(run, tpl, ETier.Bronze, EInventorySection.Hand, 3, ench: EEnchantmentType.Fiery);

        item.CoreCard!.SetTier(CardTier.Gold, isTemporary: true);
        item.Tier = ETier.Gold;
        item.CoreCard.ApplyEnchantment("Heavy", preventOverride: false, DurationTarget.Day);
        item.CoreCard.ApplyEnchantment("Icy", preventOverride: false, DurationTarget.Hour);
        item.Enchantment = EEnchantmentType.Icy;

        string json = RunSnapshotSerializer.Serialize(run, startSelectionDone: true);
        var restored = RunSnapshotSerializer.Deserialize(json, Repo.Value);

        var rItem = restored.Cards[item.InstanceId];
        Assert.NotNull(rItem.CoreCard);
        Assert.Equal(CardTier.Bronze, rItem.CoreCard!.PermanentTier);
        Assert.Equal(CardTier.Gold, rItem.CoreCard.Tier);
        Assert.Equal("Fiery", rItem.CoreCard.PermanentEnchantment);
        Assert.Equal("Heavy", rItem.CoreCard.DayEnchantment);
        Assert.Equal("Icy", rItem.CoreCard.HourEnchantment);
        Assert.Equal("Icy", rItem.CoreCard.Enchantment);

        // 过小时后回退到 Day 附魔（Heavy），过天后回退到 Permanent 附魔（Fiery）
        rItem.CoreCard.ClearEndOfHourModifiers();
        Assert.Equal("Heavy", rItem.CoreCard.Enchantment);
        rItem.CoreCard.ClearEndOfDayModifiers();
        Assert.Equal("Fiery", rItem.CoreCard.Enchantment);
    }

    // ── P1-31：恢复构造尾部无副作用光环重算 & EncounterStepEngine 防叠层膨胀 ──

    [Fact]
    public void P1_31_ResumeSilentlyRecalculatesAurasAndStepSyncPreventsPermanentBloat()
    {
        var (run, _) = CreateRun();
        var tpl = FindSmallItemTemplate();
        var item = AddOwnedItem(run, tpl, ETier.Bronze, EInventorySection.Hand, 3);
        item.CoreCard!.PermanentAdd["DamageAmount"] = 5;
        item.CoreCard.AuraAdd["DamageAmount"] = 10;
        item.Attributes[ECardAttributeType.DamageAmount] = (int)Math.Round(item.CoreCard.GetAttribute("DamageAmount"));

        // 1. 恢复构造 RunEngine 不污染 PermanentAdd
        string json = RunSnapshotSerializer.Serialize(run, startSelectionDone: true);
        var restoredRun = RunSnapshotSerializer.Deserialize(json, Repo.Value);
        _ = new RunEngine(restoredRun, Pool.Value, Monsters.Value, LevelUps.Value, Mode.Value, Repo.Value, new PvpGhostPool());
        Assert.Equal(5, restoredRun.Cards[item.InstanceId].CoreCard!.PermanentAdd.GetValueOrDefault("DamageAmount", 0));

        // 2. EncounterStepEngine.EnsureSyncedCoreCard 面对含光环/临时层的 CoreCard 绝不把 AuraAdd 叠写进 PermanentAdd
        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
        var stepEngine = new EncounterStepEngine(run, Pool.Value, Repo.Value, shop, new Random(1));
        var syncMethod = typeof(EncounterStepEngine).GetMethod("EnsureSyncedCoreCard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var syncedCore = (CardInstance)syncMethod.Invoke(stepEngine, new object[] { item })!;
        Assert.Equal(5, syncedCore.PermanentAdd.GetValueOrDefault("DamageAmount", 0));
    }

    // ── P1-32 & P1-33：SuspendedStates 弹栈重签 ID、折扣重映射、MaxSelections 与 ExitXp 持久化 ─

    [Fact]
    public void P1_32_And_P1_33_SuspendedStates_ReissuesIdsAndPersistsDiscountsAndExitXp()
    {
        var (run, engine) = CreateRun(state: ERunState.Encounter, day: 2, hour: 2);
        var tpl = FindSmallItemTemplate();
        string oldCardId = "itm_old_shelf_1";
        var shelfCore = new CardInstance(tpl, CardTier.Bronze, null, oldCardId);
        var shelfCard = new RunState.CardEntry
        {
            InstanceId = oldCardId,
            TemplateId = Guid.Parse(tpl.Id),
            CustomCardId = tpl.Id,
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tpl.Size),
            Tier = ETier.Bronze,
            Owner = null,
            State = ECardState.Alive,
            CoreCard = shelfCore,
        };
        shelfCard.Attributes[ECardAttributeType.BuyPrice] = 3;

        run.CurrentEncounterId = Pool.Value.OfType("EventEncounter").First(e => Guid.TryParse(e.Id, out _)).Id;
        run.Cards[oldCardId] = shelfCard;
        run.SelectionSet.Add(oldCardId);
        run.CurrentMaxSelections = 2;
        run.PendingEncounterExitXp = 3;
        run.BaseRerollCost = 2;
        run.ActiveShelfDiscounts[oldCardId] = 1;
        run.PendingLevelUps = 1;

        var tryEnterLvl = typeof(RunEngine).GetMethod("TryEnterSuspendedLevelUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var enterEvents = new List<IGameSimEvent>();
        bool entered = (bool)tryEnterLvl.Invoke(engine, new object[] { enterEvents })!;
        Assert.True(entered);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Equal(0, run.PendingEncounterExitXp);

        // 序列化往返验证 SuspendedStates 完整保存
        string snapJson = RunSnapshotSerializer.Serialize(run, startSelectionDone: true);
        var restoredRun = RunSnapshotSerializer.Deserialize(snapJson, Repo.Value);
        var restoredEngine = new RunEngine(restoredRun, Pool.Value, Monsters.Value, LevelUps.Value, Mode.Value, Repo.Value, new PvpGhostPool());
        Assert.Single(restoredRun.SuspendedStates);

        // 弹栈恢复：非 Hour 6 挂起卡必须获得全新 InstanceId，且折扣/MaxSelections/PendingEncounterExitXp 全部还原
        var advMethod = typeof(RunEngine).GetMethod("AdvanceAfterSubStateToEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var popEvents = new List<IGameSimEvent>();
        advMethod.Invoke(restoredEngine, new object[] { popEvents });

        Assert.Equal(ERunState.Encounter, restoredRun.State);
        Assert.Equal(2, restoredRun.CurrentMaxSelections);
        Assert.Equal(3, restoredRun.PendingEncounterExitXp);
        Assert.Equal(2u, restoredRun.BaseRerollCost);
        string newCardId = Assert.Single(restoredRun.SelectionSet);
        Assert.NotEqual(oldCardId, newCardId);
        Assert.True(restoredRun.ActiveShelfDiscounts.TryGetValue(newCardId, out int remappedDiscount));
        Assert.Equal(1, remappedDiscount);
    }

    // ── P1-34：Pedestal 清零刷新状态 ─────────────────────────────────────────

    [Fact]
    public void P1_34_EnterPedestal_ResetsRerollRulesAndCountsToZero()
    {
        var pedTpl = Pool.Value.OfType("PedestalEncounter").FirstOrDefault()
            ?? Repo.Value.CardsById.Values
                .Where(c => string.Equals(c.Type, "PedestalEncounter", StringComparison.OrdinalIgnoreCase))
                .Select(c => Pool.Value.Get(c.Id))
                .FirstOrDefault(x => x != null);
        Assert.NotNull(pedTpl);

        var (run, engine) = CreateRun(state: ERunState.Choice);
        run.RerollCost = 4;
        run.BaseRerollCost = 4;
        run.RerollsRemaining = 3;
        run.SelectionRules = new TSelectionContextRules
        {
            RerollRules = new RerollRules { StartingCost = 2, TotalAllowedRerolls = 3 },
        };

        var transMethod = typeof(RunEngine).GetMethod("TransitionToEncounterById", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var outcome = new RunEngine.Outcome();
        transMethod.Invoke(engine, new object?[] { pedTpl!.Id, outcome, null });

        Assert.Equal(ERunState.Pedestal, run.State);
        Assert.Null(run.SelectionRules.RerollRules);
        Assert.Null(run.BaseRerollCost);
        Assert.Equal(0u, run.RerollCost);
        Assert.Equal(0u, run.RerollsRemaining);
    }

    // ── P1-35：SettleInterruptedCombatOnResume 不吞 PvP XP 且终局补 RunCompleted + MarkRunEnded ─

    [Fact]
    public void P1_35_SettleInterruptedCombatOnResume_PreservesPvpXpAndMarksTerminalInDb()
    {
        // A. PVPCombat 中断恢复（非终局）不吞小时 XP，跨日推进后获得 +1 小时 XP
        var pvpRun = new RunState
        {
            RunId = "w3b_pvp_res_" + Guid.NewGuid().ToString("N")[..6],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 1,
            Hour = 6,
            Victories = 1,
            State = ERunState.PVPCombat,
            StartSelectionDone = true,
            GainedHourXpThisHour = false,
            GainedAdvanceHourXpThisHour = false,
            RngSeed = 42,
        };
        pvpRun.PlayerAttributes[EPlayerAttributeType.Experience] = 0;
        pvpRun.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
        pvpRun.PlayerAttributes[EPlayerAttributeType.Level] = 1;
        pvpRun.PlayerAttributes[EPlayerAttributeType.HealthMax] = 250;
        pvpRun.PlayerAttributes[EPlayerAttributeType.Health] = 250;

        _ = new RunEngine(pvpRun, Pool.Value, Monsters.Value, LevelUps.Value, Mode.Value, Repo.Value, new PvpGhostPool());
        Assert.Equal(2u, pvpRun.Day);
        Assert.True(pvpRun.PlayerAttributes[EPlayerAttributeType.Experience] >= 1, "PVPCombat 恢复跨日后应获得 +1 小时 XP");

        // B. PVPCombat 中断恢复若直接进入终局，应缓存 RunCompleted 供 BuildResumeEvents 下发并立即 MarkRunEnded
        string dbPath = CreateTempDbPath("p1_35");
        try
        {
            using var db = new Persistence(dbPath);
            Guid accId = Guid.NewGuid();
            var lethalRun = new RunState
            {
                RunId = "w3b_pvp_end_" + Guid.NewGuid().ToString("N")[..6],
                GameModeId = Pool.Value.DefaultGameModeId,
                AccountId = accId.ToString("N"),
                Hero = EHero.Vanessa,
                Day = 5,
                Hour = 6,
                State = ERunState.PVPCombat,
                FatesUsed = true,
                StartSelectionDone = true,
                RngSeed = 43,
            };
            lethalRun.PlayerAttributes[EPlayerAttributeType.Prestige] = 0;
            lethalRun.PlayerAttributes[EPlayerAttributeType.Level] = 5;
            lethalRun.PlayerAttributes[EPlayerAttributeType.HealthMax] = 600;
            lethalRun.PlayerAttributes[EPlayerAttributeType.Health] = 600;
            db.SaveRunSnapshot(lethalRun, accId.ToString("N"), isCustomRule: false);

            _ = new RunEngine(lethalRun, Pool.Value, Monsters.Value, LevelUps.Value, Mode.Value, Repo.Value, new PvpGhostPool(), db: db);
            Assert.Equal(ERunState.EndRunDefeat, lethalRun.State);
            Assert.NotNull(lethalRun.PendingResumeRunCompletedEvent);
            Assert.Null(db.LoadLatestActiveRun(accId));

            var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(lethalRun);
            Assert.Contains(resumeEvents, e => e is GameSimEventRunCompleted);
            Assert.Null(lethalRun.PendingResumeRunCompletedEvent);
        }
        finally
        {
            TryDeleteTempDb(dbPath);
        }
    }

    // ── P1-36：Fates 豁免 Day 25 上限判负 & PvE 胜+Fates 压栈串联 ──────────────

    [Fact]
    public void P1_36_FatesPriority_ExemptsDay25AndSuspendsPveVictoryLoot()
    {
        var (run, engine) = CreateRun(day: 25, hour: 6, state: ERunState.Choice);
        run.PlayerAttributes[EPlayerAttributeType.Prestige] = 1;
        run.FatesUsed = true;

        var tryEnd = typeof(RunEngine).GetMethod("TryEndRunToEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var eventsWithFates = new List<IGameSimEvent>();
        bool endedWithFates = (bool)tryEnd.Invoke(engine, new object[] { eventsWithFates, true, true })!;
        Assert.False(endedWithFates, "triggeredFates=true 时当回合不得因 Day 25 上限直接判负");

        var eventsWithoutFates = new List<IGameSimEvent>();
        bool endedWithoutFates = (bool)tryEnd.Invoke(engine, new object[] { eventsWithoutFates, true, false })!;
        Assert.True(endedWithoutFates);
        Assert.Equal(ERunState.EndRunDefeat, run.State);
    }

    // ── P1-37：账号级会话互斥锁 & StartSelectionDone 一等持久化 ──────────────

    [Fact]
    public void P1_37_AccountSessionLockAndStartSelectionDoneFirstClassPersistence()
    {
        string accKey = "acc_" + Guid.NewGuid().ToString("N");
        var lock1 = GameServerEndpoints.GetAccountSessionLock(accKey);
        var lock2 = GameServerEndpoints.GetAccountSessionLock(accKey.ToUpperInvariant());
        Assert.Same(lock1, lock2);

        // 预设开局（携带起始物品但 StartSelectionDone=false）经 Persistence 保存与反序列化后，必须保持 StartSelectionDone=false
        string dbPath = CreateTempDbPath("p1_37");
        try
        {
            using var db = new Persistence(dbPath);
            Guid accId = Guid.NewGuid();
            var tpl = FindSmallItemTemplate();
            var (run, _) = CreateRun(
                day: 1,
                hour: 1,
                state: ERunState.Encounter,
                startSelectionDone: false,
                accountId: accId.ToString("N"));
            run.CurrentEncounterId = ShopEngine.GetStarterEncounterId(run.Hero);
            AddOwnedItem(run, tpl, ETier.Bronze, EInventorySection.Hand, 3);

            db.SaveRunSnapshot(run, accId.ToString("N"), isCustomRule: false);
            var row = db.LoadLatestActiveRun(accId);
            Assert.NotNull(row);
            Assert.True(RunSnapshotSerializer.TryDeserialize(row!.Snapshot, Repo.Value, out var restored, out var dto));
            Assert.False(dto!.StartSelectionDone);
            Assert.False(restored!.StartSelectionDone);
        }
        finally
        {
            TryDeleteTempDb(dbPath);
        }
    }

    // ── P1-38：FindFuseTarget Hand(0→9)→Stash(0→9) 优先 & 买卡融合不重发/保原附魔 ─

    [Fact]
    public void P1_38_FusionTargetPriorityAndSingleFusedEventPreservingOriginalEnchantment()
    {
        var (run, engine) = CreateRun(state: ERunState.Encounter, gold: 50);
        var tpl = FindSmallItemTemplate();

        // 先放 Stash(0)，再放 Hand(4)，字典迭代顺序里 Stash 在前但按 P1-38 必须优先融合 Hand(4)
        var stashCopy = AddOwnedItem(run, tpl, ETier.Bronze, EInventorySection.Stash, 0, instanceId: "itm_00_stash");
        var handCopy = AddOwnedItem(run, tpl, ETier.Bronze, EInventorySection.Hand, 4, ench: EEnchantmentType.Fiery, instanceId: "itm_99_hand");

        // 构造带不同附魔（Icy）的货架同名同阶卡
        var shelfCore = new CardInstance(tpl, CardTier.Bronze, "Icy", "itm_shelf_fuse");
        var shelfItem = new RunState.CardEntry
        {
            InstanceId = "itm_shelf_fuse",
            TemplateId = Guid.Parse(tpl.Id),
            CustomCardId = tpl.Id,
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tpl.Size),
            Tier = ETier.Bronze,
            Enchantment = EEnchantmentType.Icy,
            Owner = null,
            State = ECardState.Alive,
            CoreCard = shelfCore,
        };
        shelfItem.Attributes[ECardAttributeType.BuyPrice] = 4;
        run.Cards[shelfItem.InstanceId] = shelfItem;
        run.SelectionSet.Add(shelfItem.InstanceId);

        var res = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(shelfItem.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_4 },
        });

        Assert.Null(res.Error);
        // 优先融合 Hand 卡，Stash 卡保持 Bronze 不变
        Assert.Equal(ETier.Silver, handCopy.Tier);
        Assert.Equal(ETier.Bronze, stashCopy.Tier);
        // 保留原主力卡既有 Fiery 附魔，不被商店卡 Icy 覆写
        Assert.Equal(EEnchantmentType.Fiery, handCopy.Enchantment);

        // 同帧仅发 CardFused + CardDisposed，不再重发 CardUpgraded / CardEnchanted
        Assert.Contains(res.Events, e => e is GameSimEventCardFused f && f.FusedInstanceId == handCopy.InstanceId && f.NewTier == ETier.Silver);
        Assert.DoesNotContain(res.Events, e => e is GameSimEventCardUpgraded);
        Assert.DoesNotContain(res.Events, e => e is GameSimEventCardEnchanted);

        // #71：不同 CustomCardId 的 Mod 卡即使共用同一原版载体 TemplateId 也绝不误合
        var findFuseMethod = typeof(RunEngine).GetMethod("FindFuseTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var modProbe = new RunState.CardEntry
        {
            InstanceId = "itm_mod_probe",
            TemplateId = Guid.Parse(tpl.Id),
            CustomCardId = "mod_custom_distinct_card",
            Type = ECardType.Item,
            Tier = ETier.Bronze,
        };
        Assert.Null(findFuseMethod.Invoke(engine, new object[] { modProbe }));
    }

    // ── P1-39：DirtyCardIds 覆盖 Skill/SocketEffect & HasStashRelevantAura 全扫描 ─

    [Fact]
    public void P1_39_DirtyCardIdsCoversSkillAndHasStashRelevantAuraScansBystanders()
    {
        var (run, engine) = CreateRun(state: ERunState.Choice);

        // 1. SyncAllEntriesFromCoreCardsAndDiff 应把发生变化的 Skill 登记进 dirtyCardIds
        var skTpl = Repo.Value.CardsById.Values.First(c =>
            string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(c.Id, out _));
        var skCore = new CardInstance(skTpl, CardTier.Bronze, null, "skl_dirty_1");
        skCore.AuraAdd["DamageAmount"] = 7;
        var skEntry = new RunState.CardEntry
        {
            InstanceId = "skl_dirty_1",
            TemplateId = Guid.Parse(skTpl.Id),
            CustomCardId = skTpl.Id,
            Type = ECardType.Skill,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            CoreCard = skCore,
        };
        run.Cards[skEntry.InstanceId] = skEntry;

        var dirty = new HashSet<string>(StringComparer.Ordinal);
        _ = run.SyncAllEntriesFromCoreCardsAndDiff(Repo.Value.GameMode.StandardPrices, dirtyCardIds: dirty, emitSyntheticRefreshEvent: false);
        Assert.Contains("skl_dirty_1", dirty);

        // 2. Stash 内存在旁观带 Stash/HandAndStash 光环卡时，移动普通无附魔 Stash 卡仍应判定 HasStashRelevantAura=true
        var plainTpl = FindSmallItemTemplate();
        var movedPlain = AddOwnedItem(run, plainTpl, ETier.Bronze, EInventorySection.Stash, 0);
        var bystander = AddOwnedItem(run, plainTpl, ETier.Bronze, EInventorySection.Stash, 5, ench: EEnchantmentType.Golden);
        Assert.NotNull(bystander);

        var hasStashAura = typeof(RunEngine).GetMethod("HasStashRelevantAura", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool detected = (bool)hasStashAura.Invoke(engine, new object[]
        {
            movedPlain,
            new List<(RunState.CardEntry, EContainerSocketId, EInventorySection)>()
        })!;
        Assert.True(detected, "储藏箱内存在旁观光环/附魔卡时，不能因被移动卡本身无光环而误短路");
    }

    // ── P1-40：离开 LevelUp 切回非升级态时补发幂等 SocketsUnlocked ────────────

    [Fact]
    public void P1_40_ExitingLevelUp_EmitsIdempotentSocketsUnlockedForExpandedSlots()
    {
        var (run, engine) = CreateRun(state: ERunState.Choice, day: 1, hour: 2, unlockedSlots: 0b00_0111_1000);

        // 触发升级扩槽（4→6 格）并压栈进入 LevelUp
        var grantXp = typeof(RunEngine).GetMethod("GrantExperienceToList", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pushLvl = typeof(RunEngine).GetMethod("PushChoiceAndEnterLevelUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var xpEvents = new List<IGameSimEvent>();
        grantXp.Invoke(engine, new object[] { xpEvents, Math.Max(8, Mode.Value.ExperiencePerLevel), EExperienceType.Hour });
        Assert.NotNull(run.LevelUpEntryUnlockedSlots);

        pushLvl.Invoke(engine, new object[] { xpEvents });
        Assert.Equal(ERunState.LevelUp, run.State);

        // 离开 LevelUp 切回 Choice，必须补发幂等 GameSimEventSocketsUnlocked 并清空跟踪掩码
        var advMethod = typeof(RunEngine).GetMethod("AdvanceAfterSubStateToEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var exitEvents = new List<IGameSimEvent>();
        advMethod.Invoke(engine, new object[] { exitEvents });

        Assert.Equal(ERunState.Choice, run.State);
        var unlockEv = Assert.Single(exitEvents.OfType<GameSimEventSocketsUnlocked>());
        Assert.Equal(run.UnlockedSlots, unlockEv.UnlockedSocketsBitmask);
        Assert.NotEmpty(unlockEv.UnlockedSockets);
        Assert.Null(run.LevelUpEntryUnlockedSlots);
    }

    // ── P1-41：候选技能显式 BuyPrice & 缺键与客户端一致按 0 金放行 ────────────

    [Fact]
    public void P1_41_SelectSkill_MissingBuyPriceIsFreeConsistentWithClient()
    {
        var (run, engine) = CreateRun(state: ERunState.Encounter, gold: 0);
        run.SelectionRules = new TSelectionContextRules { CanSelectMultiple = false, SelectionIsFree = false, CanExit = true };

        var skTpl = Repo.Value.CardsById.Values.First(c =>
            string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(c.Id, out _));
        var skEntry = new RunState.CardEntry
        {
            InstanceId = "skl_nobuy_1",
            TemplateId = Guid.Parse(skTpl.Id),
            CustomCardId = skTpl.Id,
            Type = ECardType.Skill,
            Tier = ETier.Gold,
            Owner = null,
            CoreCard = new CardInstance(skTpl, CardTier.Gold, null, "skl_nobuy_1"),
        };
        // 故意不写入 BuyPrice 键：按 P1-41 双端对齐语义，0 金应免费放行，不回退 StandardPrices 收费
        run.Cards[skEntry.InstanceId] = skEntry;
        run.SelectionSet.Add(skEntry.InstanceId);

        var res = engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(skEntry.InstanceId) });
        Assert.Null(res.Error);
        Assert.Equal(ECombatantId.Player, skEntry.Owner);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Gold]);
    }

    // ── P1-44：GrantStarterPresets 对 StartingSkills 去重 ────────────────────

    [Fact]
    public void P1_44_GrantStarterPresets_DeduplicatesStartingSkills()
    {
        var skTpl = Repo.Value.CardsById.Values.First(c =>
            string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(c.Id, out _));

        var rules = new CustomRunRuleSet
        {
            StartingSkills = new List<PresetCardSpec>
            {
                new() { TemplateId = skTpl.Id, Tier = "Bronze" },
                new() { TemplateId = skTpl.Id.ToUpperInvariant(), Tier = "Silver" },
                new() { TemplateId = skTpl.Id, Tier = "Gold" },
            }
        };

        var (run, engine) = CreateRun(day: 1, hour: 1, customRules: rules, startSelectionDone: false);
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);

        int count = run.Cards.Values.Count(c =>
            c.Owner == ECombatantId.Player &&
            c.Type == ECardType.Skill &&
            string.Equals(RunState.ResolveCoreTemplateId(c), skTpl.Id, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, count);
    }

    // ── P1-55 & P1-02：同步真人 PvP 对称判平 & ChallengeService 局内安全挂钩 ──

    [Fact]
    public void P1_55_And_P1_02_SyncHumanPvpDrawIsSymmetricAndChallengesTrackNonSandbox()
    {
        string dbPath = CreateTempDbPath("p1_55_02");
        try
        {
            using var db = new Persistence(dbPath);
            using var accounts = new AccountStore(db);
            var challenges = new ChallengeService(db, Repo.Value, accounts);
            var accA = accounts.GetOrCreateByName("SyncPlayerA");
            var accB = accounts.GetOrCreateByName("SyncPlayerB");
            string idA = accA.AccountId.ToString("N");
            string idB = accB.AccountId.ToString("N");

            // 1. 验证同步真人 PvP：预置双方零武器（纯沙尘暴同血同死平局），经 ITournamentPvpCoordinator 对称判平
            var lobbies = new LobbyStore(db);
            var lobby = lobbies.Create(accA, "DrawTest", "Tournament", playerCap: 2, isPublic: true, rules: new CustomRunRuleSet());
            lobbies.Join(lobby.Code, accB);
            var tsid = Guid.NewGuid();
            lobby.TournamentSessionId = tsid;
            lobby.HasStarted = true;

            var coord = new TournamentPvpCoordinator(db, lobbies);
            var emptyOppRun = new RunState
            {
                RunId = "opp_empty_" + Guid.NewGuid().ToString("N")[..6],
                GameModeId = Pool.Value.DefaultGameModeId,
                AccountId = idB,
                Username = accB.Username,
                Hero = EHero.Vanessa,
                Day = 1,
                Hour = 6,
                State = ERunState.Choice,
                UnlockedSlots = 0b00_0111_1000,
                StartSelectionDone = true,
            };
            emptyOppRun.PlayerAttributes[EPlayerAttributeType.HealthMax] = 250;
            emptyOppRun.PlayerAttributes[EPlayerAttributeType.Health] = 250;
            emptyOppRun.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
            emptyOppRun.PlayerAttributes[EPlayerAttributeType.Level] = 1;
            var emptyOppBoard = new LockedPvpBoard(
                AccountId: idB,
                Username: accB.Username,
                Hero: "Vanessa",
                Day: 1,
                HealthMax: 250,
                Prestige: 20,
                Level: 1,
                Victories: 0,
                Gold: 0,
                Income: 5,
                RunSnapshotJson: RunSnapshotSerializer.Serialize(emptyOppRun, startSelectionDone: true));
            coord.LockPlayerBoardForDay(tsid, 1, emptyOppBoard);

            var (runA, engineA) = CreateRun(
                day: 1,
                hour: 6,
                state: ERunState.Choice,
                healthMax: 250,
                accounts: accounts,
                challenges: challenges,
                db: db,
                lobbies: lobbies,
                pvpCoordinator: coord,
                accountId: idA,
                tournamentSessionId: tsid);

            // 向锦标赛大厅报告双方均已到达 Day 1 Hour 6 就位，穿越同步屏障执行真实真人 PvP 战斗
            lobbies.RecordRunProgress(lobby, idA, runA);
            lobbies.RecordRunProgress(lobby, idB, emptyOppRun);

            // 强制当天匹配 A vs B 并执行真实 PvP 战斗
            var assignment = lobbies.EnsureAndGetAssignment(lobby, 1, idA, sessions: null);
            Assert.NotNull(assignment);
            Assert.False(assignment!.OpponentIsBot);

            var pvpCard = new RunState.CardEntry
            {
                InstanceId = "pvp_draw_1",
                TemplateId = Guid.Parse(ShopEngine.Hour6PvpCardId),
                CustomCardId = ShopEngine.Hour6PvpCardId,
                Type = ECardType.PvpEncounter,
                Owner = null,
            };
            runA.Cards[pvpCard.InstanceId] = pvpCard;
            runA.SelectionSet.Add(pvpCard.InstanceId);

            var combatOut = engineA.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCard.InstanceId) });
            Assert.Null(combatOut.Error);
            Assert.NotNull(combatOut.Combat);
            Assert.True(combatOut.Combat!.IsDraw, "双方空盘同血同归于尽时，同步真人 PvP 必须对称判平 IsDraw=true");
            Assert.Equal(0u, runA.Victories);
            Assert.Equal(0u, runA.Defeats);
            Assert.Equal(20, runA.PlayerAttributes[EPlayerAttributeType.Prestige]);
            Assert.Equal(2u, runA.Day); // 平局后正常推进至次日，不会卡回 Hour 6

            // 2. 验证 P1-02：非沙盒局调用挑战钩子安全落盘不抛异常，且沙盒局严格跳过
            var initialChallenges = challenges.GetChallenges(accA.AccountId);
            Assert.NotEmpty(initialChallenges.dailyChallenges);
            var tpl = FindSmallItemTemplate();
            var soldItem = AddOwnedItem(runA, tpl, ETier.Bronze, EInventorySection.Hand, 3);
            var sellOut = engineA.Handle(new SellCardCommand { InstanceId = new InstanceId(soldItem.InstanceId) });
            Assert.Null(sellOut.Error);

            // 沙盒对局应静默跳过挑战进度记录
            var sbRules = new CustomRunRuleSet { SpecialModeId = ESpecialModeId.SandboxLab };
            var (sbRun, sbEngine) = CreateRun(customRules: sbRules, challenges: challenges, accountId: accB.AccountId.ToString("N"));
            var sbBefore = challenges.GetChallenges(accB.AccountId);
            var sbItem = AddOwnedItem(sbRun, tpl, ETier.Bronze, EInventorySection.Hand, 3);
            var sbSellOut = sbEngine.Handle(new SellCardCommand { InstanceId = new InstanceId(sbItem.InstanceId) });
            Assert.Null(sbSellOut.Error);
            var sbAfter = challenges.GetChallenges(accB.AccountId);
            Assert.Equal(
                sbBefore.dailyChallenges.OfType<ChallengeProgressItemDto>().Sum(c => c.Progress),
                sbAfter.dailyChallenges.OfType<ChallengeProgressItemDto>().Sum(c => c.Progress));
        }
        finally
        {
            TryDeleteTempDb(dbPath);
        }
    }
}
