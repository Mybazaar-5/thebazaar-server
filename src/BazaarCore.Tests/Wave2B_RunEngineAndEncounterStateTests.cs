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
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Storage;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2B（D1-P1-14, D2-P1-02/03/04/07/08/09/14, D3-P1-10）：
/// RunEngine 状态机、多选空货架防死锁、BoardOverride 压栈/去重合并、健康/升级/光环/5阶/门票/断线终局、
/// 玩家 4 层属性零反除零双重乘算、EncounterStepEngine 因果 1:N 同步与快照无损往返强断言测试。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave2B_RunEngineAndEncounterStateTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);
    private static readonly Lazy<List<BazaarServer.Data.MonsterTemplate>> Monsters =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.CreateMonstersCopy());
    private static readonly Lazy<LevelUpTable> LevelUps =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.LevelUps);
    private static readonly Lazy<GameModeConfig> Mode =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.Mode);

    private static string CreateTempDbPath(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bazaar_w2b_c_tests");
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
        uint day = 2,
        uint hour = 2,
        ERunState state = ERunState.Encounter,
        int gold = 50,
        int healthMax = 400,
        ushort unlockedSlots = GameDefaults.InitialUnlockedSlotsMask,
        bool startSelectionDone = true,
        AccountStore? accounts = null,
        Persistence? db = null,
        string? accountId = null,
        CustomRunRuleSet? customRules = null)
    {
        var run = new RunState
        {
            RunId = "w2bc_" + Guid.NewGuid().ToString("N")[..10],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Ranked,
            AccountId = accountId ?? Guid.NewGuid().ToString("N"),
            Username = "Wave2BTester",
            Day = day,
            Hour = hour,
            State = state,
            UnlockedSlots = unlockedSlots,
            StartSelectionDone = startSelectionDone,
            CustomRules = (customRules ?? new CustomRunRuleSet()).CloneNormalized(),
            RngSeed = 20261123,
        };
        run.HandGrid.UnlockedMask = unlockedSlots;
        run.SetBasePlayerAttribute(EPlayerAttributeType.Gold, gold);
        run.SetBasePlayerAttribute(EPlayerAttributeType.HealthMax, healthMax);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Health, healthMax);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, GameDefaults.StartingPrestige);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Level, 1);

        var engine = new RunEngine(
            run,
            Pool.Value,
            Monsters.Value,
            LevelUps.Value,
            Mode.Value,
            Repo.Value,
            new PvpGhostPool(),
            accounts: accounts,
            db: db);
        return (run, engine);
    }

    [Fact]
    public void D2_P1_02_D2_P1_03_VanillaDealCardsNullCustomCardId_AndMultiSelectEmptyShelfAutoAdvances()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;

        // 1. D2-P1-02：ConsumeOutOfCombatBatchRequests 对纯原版发牌必须保持 CustomCardId=null 且不增 VisualBindingsRevision；真自定义卡才赋值并自增
        var (run, engine) = CreateRun(state: ERunState.Encounter);
        var vanillaTpl = repo.CardsById.Values.First(c => c.Type == "Item" && Guid.TryParse(c.Id, out _));
        var vanillaInst = new CardInstance(vanillaTpl, CardTier.Bronze, null, "tmp_v_1");

        var customCoreTpl = new CardTemplate
        {
            Id = "mod:w2b_custom_deal_item",
            InternalName = "W2B Custom Deal Item",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Heroes = ["Vanessa"],
            Attributes = new Dictionary<string, double> { ["DamageAmount"] = 12 },
        };
        var customInst = new CardInstance(customCoreTpl, CardTier.Bronze, null, "tmp_c_1");

        var consumeMethod = typeof(RunEngine).GetMethod("ConsumeOutOfCombatBatchRequests", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int revBefore = run.VisualBindingsRevision;
        var batchVanilla = new OutOfCombatBatchResult();
        batchVanilla.DealtSelectionRequests.Add((new List<CardInstance> { vanillaInst }, "Replace"));
        var evsVanilla = new List<IGameSimEvent>();
        consumeMethod.Invoke(engine, new object[] { batchVanilla, evsVanilla });

        Assert.Equal(revBefore, run.VisualBindingsRevision);
        var dealtVanilla = Assert.Single(run.SelectionSet.Select(id => run.Cards[id]));
        Assert.Null(dealtVanilla.CustomCardId);
        Assert.Equal(Guid.Parse(vanillaTpl.Id), dealtVanilla.TemplateId);

        var batchCustom = new OutOfCombatBatchResult();
        batchCustom.DealtSelectionRequests.Add((new List<CardInstance> { customInst }, "Replace"));
        var evsCustom = new List<IGameSimEvent>();
        consumeMethod.Invoke(engine, new object[] { batchCustom, evsCustom });

        Assert.Equal(revBefore + 1, run.VisualBindingsRevision);
        var dealtCustom = Assert.Single(run.SelectionSet.Select(id => run.Cards[id]));
        Assert.Equal("mod:w2b_custom_deal_item", dealtCustom.CustomCardId);

        // 2. D2-P1-03：付费多选不可退出（CanSelectMultiple=true, CanExit=false, MaxSelections=3）但货架仅剩 1 张 !IsReselectable 步卡，点选后先删卡再判空自动推进，绝不空货架死锁
        var (run2, engine2) = CreateRun(day: 2, hour: 2, state: ERunState.Encounter);
        var simpleStepTpl = repo.CardsById.Values.First(c =>
            c.Type == "EncounterStep" &&
            c.IsReselectable != true &&
            (c.NextEncounterOnSelection == null || c.NextEncounterOnSelection.Count == 0) &&
            c.SelectionRequirements == null &&
            (c.Abilities == null || c.Abilities.Values.All(a =>
                a.Action?.Type is not ("TActionGameDealCards" or "TActionGameReroll" or "TActionCardModifyAttribute" or "TActionCardUpgrade" or "TActionCardTransform"))) &&
            Guid.TryParse(c.Id, out _));

        run2.CurrentEncounterId = pool.HourlyEncounters("Vanessa", "EventEncounter").First().Id;
        run2.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = true,
            SelectionIsFree = false,
            CanExit = false,
        };
        run2.CurrentMaxSelections = 3;
        var stepEntry = new RunState.CardEntry
        {
            InstanceId = "stp_last_1",
            TemplateId = Guid.Parse(simpleStepTpl.Id),
            Type = ECardType.EncounterStep,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = null,
            State = ECardState.Alive,
        };
        stepEntry.Attributes[ECardAttributeType.BuyPrice] = 0;
        run2.Cards[stepEntry.InstanceId] = stepEntry;
        run2.SelectionSet.Add(stepEntry.InstanceId);

        var outcome = engine2.Handle(new SelectEncounterCommand
        {
            InstanceId = new InstanceId(stepEntry.InstanceId),
        });
        Assert.Null(outcome.Error);
        Assert.False(run2.State == ERunState.Encounter && run2.SelectionSet.Count == 0,
            "最后一张 !IsReselectable 步卡被选走后不得停留在空 Encounter 货架死锁");

        // 3. 硬不变量：即便外部强制构造空 Encounter 且 CanExit=false，经 Handle 出口硬不变量自动恢复 CanExit=true
        var (run3, engine3) = CreateRun(day: 2, hour: 2, state: ERunState.Encounter);
        run3.SelectionSet.Clear();
        run3.SelectionRules = new TSelectionContextRules { CanExit = false, CanSelectMultiple = false };
        var invMethod = typeof(RunEngine).GetMethod("EnsureEmptyShelfNeverDeadlocks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        invMethod.Invoke(engine3, null);
        Assert.True(run3.SelectionRules.CanExit);
    }

    [Fact]
    public void D2_P1_04_BoardOverride_SuspendedStackPersist_ResumeNoDup_AndSamePacketCoalesce()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var (run, engine) = CreateRun(day: 3, hour: 2, state: ERunState.Encounter);
        string encId = pool.HourlyEncounters("Vanessa", "EventEncounter").First().Id;
        run.CurrentEncounterId = encId;
        run.CurrentBoardId = "expedition_vault_board";
        run.PendingLevelUps = 1;

        // 压栈进入 LevelUp，验证快照持久化 CurrentBoardId 并在反序列化→弹栈后 100% 还原
        var trySuspend = typeof(RunEngine).GetMethod("TryEnterSuspendedLevelUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var suspendEvs = new List<IGameSimEvent>();
        bool entered = (bool)trySuspend.Invoke(engine, new object[] { suspendEvs })!;
        Assert.True(entered);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Single(run.SuspendedStates);
        Assert.Equal("expedition_vault_board", run.SuspendedStates.Peek().CurrentBoardId);

        string json = RunSnapshotSerializer.Serialize(run);
        Assert.True(RunSnapshotSerializer.TryDeserialize(json, repo, out var restored, out _));
        Assert.NotNull(restored);
        Assert.Equal("expedition_vault_board", restored!.CurrentBoardId);
        Assert.Single(restored.SuspendedStates);
        Assert.Equal("expedition_vault_board", restored.SuspendedStates.Peek().CurrentBoardId);

        // 重连：GameStateSync 已携 Board 时 BuildResumeEvents 不得重复发 GameSimEventBoardOverridden
        var resumeNoDup = RunSnapshotSerializer.BuildResumeEvents(restored, stateSyncAlreadyCarriesBoard: true);
        Assert.DoesNotContain(resumeNoDup, e => e is GameSimEventBoardOverridden);

        // 显式 stateSyncAlreadyCarriesBoard: false 兼容独立调用方，精确发 1 条
        var resumeWithBoard = RunSnapshotSerializer.BuildResumeEvents(restored, stateSyncAlreadyCarriesBoard: false);
        var boardEv = Assert.Single(resumeWithBoard.OfType<GameSimEventBoardOverridden>());
        Assert.Equal("expedition_vault_board", boardEv.Board);

        // 弹栈恢复后 CurrentBoardId 完整保留
        var restoredEngine = new RunEngine(restored, pool, Monsters.Value, LevelUps.Value, Mode.Value, repo, new PvpGhostPool());
        restored.CurrentBoardId = "temp_levelup_board";
        var advanceMethod = typeof(RunEngine).GetMethod("AdvanceAfterSubStateToEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var popEvs = new List<IGameSimEvent>();
        advanceMethod.Invoke(restoredEngine, new object[] { popEvs });
        Assert.Equal("expedition_vault_board", restored.CurrentBoardId);
        Assert.Contains(popEvs.OfType<GameSimEventBoardOverridden>(), b => b.Board == "expedition_vault_board");

        // 同包双 BoardOverridden A->B 折叠合并仅留最终指向 B 的 1 条
        var packetEvs = new List<IGameSimEvent>
        {
            new GameSimEventBoardOverridden("board_A", null),
            new GameSimEventStateTransitioned(ERunState.Encounter, encId),
            new GameSimEventBoardOverridden("board_B", "pres_B"),
        };
        RunEngine.CoalesceBoardOverriddenEvents(packetEvs, null);
        var remaining = packetEvs.OfType<GameSimEventBoardOverridden>().ToList();
        Assert.Single(remaining);
        Assert.Equal("board_B", remaining[0].Board);

        // 来回切板 A->null（起点本就是 null）折叠移除全部冗余切板
        var roundTripEvs = new List<IGameSimEvent>
        {
            new GameSimEventBoardOverridden("board_A", null),
            new GameSimEventBoardOverridden(null, null),
        };
        RunEngine.CoalesceBoardOverriddenEvents(roundTripEvs, null);
        Assert.Empty(roundTripEvs.OfType<GameSimEventBoardOverridden>());

        // 进入非 BoardOverride 遭遇且无活跃替换集时对称复位 CurrentBoardId=null
        var nonOverrideTpl = pool.HourlyEncounters("Vanessa", "EventEncounter")
            .First(e => string.IsNullOrWhiteSpace(e.BoardOverride) && string.IsNullOrWhiteSpace(repo.GetCard(e.Id)?.BoardOverride));
        var transMethod = typeof(RunEngine).GetMethod("TransitionToEncounterById", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var transOut = new RunEngine.Outcome();
        restored.ClearActiveReplacementSet();
        restored.CurrentBoardId = "custom_mod_board";
        transMethod.Invoke(restoredEngine, new object?[] { nonOverrideTpl.Id, transOut, null });
        Assert.Null(restored.CurrentBoardId);
    }

    [Fact]
    public void D2_P1_07_HealthMaxPlusLevel_PostTrackerAuraRecalc_Tier5CanUpgrade_TicketPrecheck_AndInterruptResume()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var levelUps = LevelUps.Value;

        // 1. 同批次 +50 HealthMax + +1 Level：血量同时叠加 +50 与升级加血（解除 healthMaxHandled 误吞）
        var (run, engine) = CreateRun(day: 2, hour: 2, state: ERunState.Encounter, healthMax: 400);
        int expectedLvl2Inc = levelUps.HealthIncreaseFor(2);
        Assert.True(expectedLvl2Inc > 0);
        var buildPs = typeof(RunEngine).GetMethod("BuildCorePlayerFromRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var applyBatch = typeof(RunEngine).GetMethod("ApplyOutOfCombatBatchWithDirty", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var ps = (PlayerState)buildPs.Invoke(engine, null)!;
        var batch = new OutOfCombatBatchResult();
        batch.PlayerAttributeChanges.Add(new OutOfCombatPlayerAttributeChange { Attribute = "HealthMax", PreviousValue = 400, CurrentValue = 450 });
        batch.PlayerAttributeChanges.Add(new OutOfCombatPlayerAttributeChange { Attribute = "Level", PreviousValue = 1, CurrentValue = 2 });
        var evs = new List<IGameSimEvent>();
        applyBatch.Invoke(engine, new object?[] { ps, batch, evs, null, true, EExperienceType.Item, null });

        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(400 + 50 + expectedLvl2Inc, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(400 + 50 + expectedLvl2Inc, run.PlayerAttributes[EPlayerAttributeType.Health]);

        // 2. syncBoardDiffs: false 下触发 Gold 增量驱动 Custom_3，needsPostTrackerAuraRecalc 必须真实触发光环重算（Oinkment Silver 起步，1*Custom_3）
        var goldAuraTpl = repo.GetCard("64e3569f-e023-49a5-ab75-f9613c338d53")
            ?? repo.CardsById.Values.First(c => c.Type == "Item" && c.InternalName.Equals("Oinkment", StringComparison.OrdinalIgnoreCase));
        var auraTier = SpawnQueryEngine.ClampToSupportedTier(goldAuraTpl, CardTier.Silver);

        var (runAura, engineAura) = CreateRun(day: 2, hour: 2, state: ERunState.Encounter, gold: 10);
        var auraCore = new CardInstance(goldAuraTpl, auraTier, null, "itm_aura_c3");
        runAura.HandGrid.TryPlaceOrShift(auraCore, 3);
        var auraEntry = new RunState.CardEntry
        {
            InstanceId = auraCore.InstanceId,
            TemplateId = Guid.Parse(goldAuraTpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(goldAuraTpl.Size),
            Tier = ShopEngine.ParseTier(auraTier.ToString()),
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = auraCore,
        };
        runAura.Cards[auraEntry.InstanceId] = auraEntry;
        var recalcMethod = typeof(RunEngine).GetMethod("RecalculateAurasAndSyncBoard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        recalcMethod.Invoke(engineAura, new object?[] { new List<IGameSimEvent>(), null });
        var beforeAttrSnap = new Dictionary<ECardAttributeType, int>(auraEntry.Attributes);

        var psAura = (PlayerState)buildPs.Invoke(engineAura, null)!;
        var goldBatch = new OutOfCombatBatchResult();
        goldBatch.PlayerAttributeChanges.Add(new OutOfCombatPlayerAttributeChange { Attribute = "Gold", PreviousValue = 10, CurrentValue = 110 });
        var dirtyIds = new HashSet<string>(StringComparer.Ordinal);
        applyBatch.Invoke(engineAura, new object?[] { psAura, goldBatch, new List<IGameSimEvent>(), null, false, EExperienceType.Item, dirtyIds });

        Assert.True(runAura.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0) >= 100);
        bool auraCardUpdated = auraEntry.Attributes.Any(kv => beforeAttrSnap.GetValueOrDefault(kv.Key, int.MinValue) != kv.Value)
            || dirtyIds.Contains(auraEntry.InstanceId);
        Assert.True(auraCardUpdated, "syncBoardDiffs=false 但触发 Custom_3 增量时，needsPostTrackerAuraRecalc 必须触发光环重算刷新手牌数值");

        // 3. 5 阶特例卡（如 Maker's Mark 或实库含 Legendary Tiers 定义的 5 阶物品）允许 Diamond -> Legendary，常规 Diamond 仍被拒绝
        var tier5Tpl = repo.CardsById.Values.First(c =>
            c.Type == "Item" &&
            c.Tiers != null &&
            c.Tiers.ContainsKey("Diamond") &&
            c.Tiers.ContainsKey("Legendary") &&
            Guid.TryParse(c.Id, out _));
        var tier4OnlyTpl = repo.CardsById.Values.First(c =>
            c.Type == "Item" &&
            c.Tiers != null &&
            c.Tiers.ContainsKey("Diamond") &&
            !c.Tiers.ContainsKey("Legendary") &&
            Guid.TryParse(c.Id, out _));

        var (run5, engine5) = CreateRun(day: 5, hour: 2, state: ERunState.Pedestal);
        var tier5Core = new CardInstance(tier5Tpl, CardTier.Diamond, null, "itm_tier5_owned");
        run5.HandGrid.TryPlaceOrShift(tier5Core, 3);
        var tier5Entry = new RunState.CardEntry
        {
            InstanceId = tier5Core.InstanceId,
            TemplateId = Guid.Parse(tier5Tpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tier5Tpl.Size),
            Tier = ETier.Diamond,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = tier5Core,
        };
        run5.Cards[tier5Entry.InstanceId] = tier5Entry;

        var tier4Core = new CardInstance(tier4OnlyTpl, CardTier.Diamond, null, "itm_tier4_owned");
        run5.HandGrid.TryPlaceOrShift(tier4Core, 5);
        var tier4Entry = new RunState.CardEntry
        {
            InstanceId = tier4Core.InstanceId,
            TemplateId = Guid.Parse(tier4OnlyTpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tier4OnlyTpl.Size),
            Tier = ETier.Diamond,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_5,
            CoreCard = tier4Core,
        };
        run5.Cards[tier4Entry.InstanceId] = tier4Entry;

        var findFuse = typeof(RunEngine).GetMethod("FindFuseTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var incoming5 = new RunState.CardEntry
        {
            InstanceId = "shf_t5",
            TemplateId = Guid.Parse(tier5Tpl.Id),
            Type = ECardType.Item,
            Size = tier5Entry.Size,
            Tier = ETier.Diamond,
            CoreCard = new CardInstance(tier5Tpl, CardTier.Diamond, null, "shf_t5"),
        };
        var incoming4 = new RunState.CardEntry
        {
            InstanceId = "shf_t4",
            TemplateId = Guid.Parse(tier4OnlyTpl.Id),
            Type = ECardType.Item,
            Size = tier4Entry.Size,
            Tier = ETier.Diamond,
            CoreCard = new CardInstance(tier4OnlyTpl, CardTier.Diamond, null, "shf_t4"),
        };
        Assert.Equal(tier5Entry.InstanceId, ((RunState.CardEntry?)findFuse.Invoke(engine5, new object[] { incoming5 }))?.InstanceId);
        Assert.Null(findFuse.Invoke(engine5, new object[] { incoming4 }));

        // 3b. 使用 repo.CreateWithOverlay + pool.CreateIsolatedOverlay 注入无 SelectionCriteria 的标准 TPedestalBehaviorUpgrade，验证 4 阶 Diamond 拒绝、5 阶 Diamond 升至 Legendary
        string tier5PedId = "mod:w2b_tier5_upgrade_pedestal";
        var tier5PedCore = new CardTemplate
        {
            Id = tier5PedId,
            InternalName = "W2B Tier5 Upgrade Pedestal",
            Type = "PedestalEncounter",
            Size = "Medium",
            StartingTier = "Bronze",
            Behavior = new RuleNode { Type = "TPedestalBehaviorUpgrade" },
        };
        var overlayRepo5 = repo.CreateWithOverlay(additionalCards: [tier5PedCore]);
        var localPool5 = pool.CreateIsolatedOverlay(overlayRepo5);
        var engine5Ped = new RunEngine(run5, localPool5, Monsters.Value, LevelUps.Value, Mode.Value, overlayRepo5, new PvpGhostPool());
        run5.CurrentEncounterId = tier5PedId;

        var rejectT4 = engine5Ped.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(tier4Entry.InstanceId) });
        Assert.NotNull(rejectT4.Error);

        var okT5 = engine5Ped.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(tier5Entry.InstanceId) });
        Assert.Null(okT5.Error);
        Assert.Equal(ETier.Legendary, tier5Entry.Tier);

        // 4. 探险门票校验前置摘卡之前：无票选 exp_ 探险房返回 NotAllowed，货架卡仍在 SelectionSet 且 Visited* 未污染
        var (runExp, engineExp) = CreateRun(day: 6, hour: 1, state: ERunState.Choice);
        string expRoomId = CardPool.CrashSiteExpeditionRoomId;
        var expEntry = new RunState.CardEntry
        {
            InstanceId = "exp_room_0",
            TemplateId = Guid.Parse(expRoomId),
            Type = ECardType.EventEncounter,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = null,
            State = ECardState.Alive,
        };
        runExp.Cards[expEntry.InstanceId] = expEntry;
        runExp.SelectionSet.Add(expEntry.InstanceId);

        var expOut = engineExp.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(expEntry.InstanceId) });
        Assert.NotNull(expOut.Error);
        Assert.Contains(expEntry.InstanceId, runExp.SelectionSet);
        Assert.True(runExp.Cards.ContainsKey(expEntry.InstanceId));
        Assert.DoesNotContain(expRoomId, runExp.VisitedDayEncounterIds);
        Assert.DoesNotContain(expRoomId, runExp.VisitedSingleSpawnEncounterIds);

        // 5. 中断战斗重连：终局幂等补记生涯；非终局保留 SuspendedStates 不丢弃
        string dbPath = CreateTempDbPath("interrupt_resume");
        try
        {
            using var db = new Persistence(dbPath);
            using var accounts = new AccountStore(db);
            var acc = accounts.GetOrCreateByName("w2b_resume_user");

            var deadRun = new RunState
            {
                RunId = "w2b_int_dead_" + Guid.NewGuid().ToString("N")[..8],
                GameModeId = pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Ranked,
                AccountId = acc.AccountId.ToString("N"),
                Username = acc.Username,
                Day = 5,
                Hour = 6,
                State = ERunState.PVPCombat,
                FatesUsed = true,
                StartSelectionDone = true,
            };
            deadRun.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, 0);
            db.SaveRunSnapshot(deadRun, acc.AccountId.ToString("N"), "{}");

            int runsBefore = acc.RunsCompleted;
            _ = new RunEngine(deadRun, pool, Monsters.Value, LevelUps.Value, Mode.Value, repo, new PvpGhostPool(), accounts: accounts, db: db);
            Assert.Equal(ERunState.EndRunDefeat, deadRun.State);
            Assert.Equal(runsBefore + 1, acc.RunsCompleted);
            Assert.False(db.TryMarkCareerRecorded(deadRun.RunId));

            var liveRun = new RunState
            {
                RunId = "w2b_int_live_" + Guid.NewGuid().ToString("N")[..8],
                GameModeId = pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Ranked,
                AccountId = acc.AccountId.ToString("N"),
                Username = acc.Username,
                Day = 3,
                Hour = 3,
                State = ERunState.Combat,
                StartSelectionDone = true,
            };
            liveRun.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, 18);
            string suspendedEncId = pool.HourlyEncounters("Vanessa", "EventEncounter").First().Id;
            liveRun.SuspendedStates.Push(new SuspendedStateSnapshot
            {
                State = ERunState.Encounter,
                CurrentEncounterId = suspendedEncId,
                CurrentBoardId = "preserved_board",
                SelectionSet = new List<string>(),
                SelectionRules = new TSelectionContextRules { CanExit = true },
            });
            _ = new RunEngine(liveRun, pool, Monsters.Value, LevelUps.Value, Mode.Value, repo, new PvpGhostPool());
            Assert.Equal(ERunState.Encounter, liveRun.State);
            Assert.Equal(suspendedEncId, liveRun.CurrentEncounterId);
            Assert.Equal("preserved_board", liveRun.CurrentBoardId);
        }
        finally
        {
            TryDeleteTempDb(dbPath);
        }
    }

    [Fact]
    public void D2_P1_08_PlayerLayeredAttributes_ZeroMultDoesNotDestroyBase_AndNoDoubleAdditiveMult()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var (run, engine) = CreateRun(day: 2, hour: 3, state: ERunState.Encounter, gold: 50, healthMax: 400);

        // 基础值 Gold=50, HealthMax=400，施加 HourMult["HealthMax"]=0.0 以及 DayAdditiveMult["Gold"]=+1.0 & DayMult["Gold"]=1.5
        run.PlayerHourMult["HealthMax"] = 0.0;
        run.PlayerDayAdditiveMult["Gold"] = 1.0;
        run.PlayerDayMult["Gold"] = 1.5;

        for (int round = 0; round < 3; round++)
        {
            var ps = new PlayerState("player", "Player", "Vanessa", 400, 20, 50, 10);
            run.ApplyLayeredPlayerStateTo(ps);
            Assert.Equal(400.0, ps.Attributes["HealthMax"], precision: 3);
            Assert.Equal(0.0, ps.GetAttribute("HealthMax"), precision: 3);
            // (50) * (1 + 1.0) * 1.5 = 150，绝不被二次乘算成 300
            Assert.Equal(150.0, ps.GetAttribute("Gold"), precision: 3);

            run.CaptureLayeredPlayerStateFrom(ps, captureBaseValues: true, includeAllExistingAttrs: true);
            Assert.Equal(400.0, run.BasePlayerAttributes["HealthMax"], precision: 3);
            Assert.Equal(50.0, run.BasePlayerAttributes["Gold"], precision: 3);
            Assert.Equal(150, run.PlayerAttributes[EPlayerAttributeType.Gold]);
            Assert.Equal(0.0, run.PlayerHourMult["HealthMax"], precision: 6);
            Assert.Equal(1.0, run.PlayerDayAdditiveMult["Gold"], precision: 6);
            Assert.Equal(1.5, run.PlayerDayMult["Gold"], precision: 6);
        }

        string snapJson = RunSnapshotSerializer.Serialize(run);
        Assert.True(RunSnapshotSerializer.TryDeserialize(snapJson, repo, out var restored, out _));
        Assert.Equal(400.0, restored!.BasePlayerAttributes["HealthMax"], precision: 3);
        Assert.Equal(0.0, restored.PlayerHourMult["HealthMax"], precision: 6);
        Assert.Equal(1.0, restored.PlayerDayAdditiveMult["Gold"], precision: 6);
        Assert.Equal(1.5, restored.PlayerDayMult["Gold"], precision: 6);

        // 清除 Hour 修饰层后，HealthMax 100% 精准恢复为 400（而非被反除归零）
        var clearHour = typeof(RunEngine).GetMethod("ClearHourModifiersOnRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        clearHour.Invoke(engine, new object?[] { null });
        Assert.Equal(400, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(400.0, run.BasePlayerAttributes["HealthMax"], precision: 3);

        // BuildDummyQueryPlayer 必须装齐 HourAdd / DayAdd / Day / Hour
        run.PlayerHourAdd["Income"] = 7.0;
        run.PlayerDayAdd["Gold"] = 25.0;
        var shop = new ShopEngine(run, pool, repo, run.CustomRules);
        var dummyMethod = typeof(ShopEngine).GetMethod("BuildDummyQueryPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dummyPs = (PlayerState)dummyMethod.Invoke(shop, new object[] { "Vanessa", (int)run.Day })!;
        Assert.Equal((int)run.Day, dummyPs.Day);
        Assert.Equal((int)run.Hour, dummyPs.Hour);
        Assert.Equal(7.0, dummyPs.HourAdd["Income"], precision: 3);
        Assert.Equal(25.0, dummyPs.DayAdd["Gold"], precision: 3);
    }

    [Fact]
    public void D2_P1_09_EncounterStep_UnlockedSocket_ClampedStats_LevelPrestigeZero_AndCausal1ToNSync()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;

        // 1. FindFreeSocketEffectSlot 尊重 Day 1 解锁掩码（Socket_3..6）且满槽时选已解锁槽位覆盖发 GameSimEventCardTransformed
        var (run, _) = CreateRun(day: 1, hour: 2, state: ERunState.Encounter, unlockedSlots: GameDefaults.InitialUnlockedSlotsMask);
        var shop = new ShopEngine(run, pool, repo, run.CustomRules);
        var stepEngine = new EncounterStepEngine(run, pool, repo, shop, new Random(123));

        var findSlot = typeof(EncounterStepEngine).GetMethod("FindFreeSocketEffectSlot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var registerOwned = typeof(EncounterStepEngine).GetMethod("RegisterNewOwnedCoreCard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var seTpl = repo.CardsById.Values.First(c => c.Type == "SocketEffect" && Guid.TryParse(c.Id, out _));

        for (int i = 0; i < 4; i++)
        {
            int slot = (int)findSlot.Invoke(stepEngine, null)!;
            Assert.InRange(slot, 3, 6);
            var seCore = new CardInstance(seTpl, CardTier.Bronze, null, $"se_test_{i}") { StartSlot = slot };
            var res = new EncounterStepResult { Success = true };
            registerOwned.Invoke(stepEngine, new object?[] { seCore, EInventorySection.Hand, res, false });
        }

        int overflowSlot = (int)findSlot.Invoke(stepEngine, null)!;
        Assert.InRange(overflowSlot, 3, 6);
        var seOverflow = new CardInstance(seTpl, CardTier.Bronze, null, "se_test_overflow") { StartSlot = overflowSlot };
        var ovRes = new EncounterStepResult { Success = true };
        registerOwned.Invoke(stepEngine, new object?[] { seOverflow, EInventorySection.Hand, ovRes, false });
        Assert.Single(ovRes.Events.OfType<GameSimEventCardTransformed>());

        // 2. SyncPlayerAttributesBack 强钳制 HealthMax>=1, Health<=HealthMax, Gold>=0, Prestige==0 标记 PrestigeReachedZero, DirectLevelsGained 记账
        var (run2, engine2) = CreateRun(day: 2, hour: 2, state: ERunState.Encounter, gold: 10, healthMax: 300);
        run2.FatesUsed = true;
        var shop2 = new ShopEngine(run2, pool, repo, run2.CustomRules);
        var stepEngine2 = new EncounterStepEngine(run2, pool, repo, shop2, new Random(456));
        var syncAttrs = typeof(EncounterStepEngine).GetMethod("SyncPlayerAttributesBack", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var psMod = new PlayerState("player", "Player", "Vanessa", 300, 20, 10, 10);
        run2.ApplyLayeredPlayerStateTo(psMod);
        var prevEffective = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["HealthMax"] = 300,
            ["Health"] = 300,
            ["Gold"] = 10,
            ["Prestige"] = GameDefaults.StartingPrestige,
            ["Level"] = 1,
        };
        psMod.Attributes["HealthMax"] = -999;
        psMod.Attributes["Gold"] = -500;
        psMod.Attributes["Prestige"] = -100;
        psMod.Attributes["Level"] = 2;
        psMod.Level = 2;

        var attrRes = new EncounterStepResult { Success = true };
        syncAttrs.Invoke(stepEngine2, new object?[] { psMod, prevEffective, attrRes, null, null });
        Assert.Equal(1, run2.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.InRange(run2.PlayerAttributes[EPlayerAttributeType.Health], 0, 1);
        Assert.Equal(0, run2.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(0, run2.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.True(attrRes.PrestigeReachedZero);
        Assert.Equal(1, attrRes.DirectLevelsGained);

        var trigFates = typeof(RunEngine).GetMethod("TryTriggerFatesOrEndDefeatFromStep", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stepOut = new RunEngine.Outcome();
        bool triggered = (bool)trigFates.Invoke(engine2, new object[] { stepOut })!;
        Assert.True(triggered);
        Assert.Equal(ERunState.EndRunDefeat, run2.State);

        // 3. SyncOwnedCardsBack 严格因果对账：销毁 A + 独立新生 B 不得误配变形；真 1:2 变形发单条多目标 CardTransformed；移除附魔发 IsReverted=true
        var (run3, _) = CreateRun(day: 3, hour: 2, state: ERunState.Encounter, unlockedSlots: 1023);
        var shop3 = new ShopEngine(run3, pool, repo, run3.CustomRules);
        var stepEngine3 = new EncounterStepEngine(run3, pool, repo, shop3, new Random(789));

        var smallItems = repo.CardsById.Values.Where(c => c.Type == "Item" && c.CardSize == CardSize.Small && Guid.TryParse(c.Id, out _)).Take(5).ToList();
        var cardA = new CardInstance(smallItems[0], CardTier.Bronze, null, "itm_destroy_A") { StartSlot = 0 };
        var cardTransOrig = new CardInstance(smallItems[1], CardTier.Bronze, null, "itm_tr_orig") { StartSlot = 1 };
        var cardEnch = new CardInstance(smallItems[2], CardTier.Bronze, "Shielded", "itm_ench_rev") { StartSlot = 2 };

        foreach (var c in new[] { cardA, cardTransOrig, cardEnch })
        {
            run3.HandGrid.TryPlaceOrShift(c, c.StartSlot);
            run3.Cards[c.InstanceId] = new RunState.CardEntry
            {
                InstanceId = c.InstanceId,
                TemplateId = Guid.Parse(c.Template.Id),
                Type = ECardType.Item,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                Enchantment = string.IsNullOrEmpty(c.Enchantment) ? null : EEnchantmentType.Shielded,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = (EContainerSocketId)c.StartSlot,
                CoreCard = c,
            };
        }

        var buildSynced = typeof(EncounterStepEngine).GetMethod("BuildSyncedPlayerState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var syncedTuple = buildSynced.Invoke(stepEngine3, null)!;
        var tFields = syncedTuple.GetType().GetFields();
        var ps3 = (PlayerState)tFields[0].GetValue(syncedTuple)!;
        var ownedByCore = (Dictionary<CardInstance, RunState.CardEntry>)tFields[1].GetValue(syncedTuple)!;
        var initialHandSet = (HashSet<CardInstance>)tFields[2].GetValue(syncedTuple)!;
        var initialStashSet = (HashSet<CardInstance>)tFields[3].GetValue(syncedTuple)!;
        var initialSkillSet = (HashSet<CardInstance>)tFields[4].GetValue(syncedTuple)!;
        var initialSocketSet = (HashSet<CardInstance>)tFields[5].GetValue(syncedTuple)!;

        var preSnapsMethod = typeof(EncounterStepEngine).GetMethod("CaptureCardSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
        var preSnaps = preSnapsMethod.Invoke(null, new object[] { ownedByCore })!;

        // (a) 销毁 cardA + 独立新生 cardB（无 TransformedFromInstanceId）
        ps3.Hand.Remove(cardA);
        var cardB = new CardInstance(smallItems[3], CardTier.Bronze, null, "itm_indep_B") { StartSlot = 0 };
        ps3.Hand.TryPlaceOrShift(cardB, 0);

        // (b) 真实 1:2 变形 cardTransOrig -> rep1 + rep2
        ps3.Hand.Remove(cardTransOrig);
        var rep1 = new CardInstance(smallItems[4], CardTier.Bronze, null, "itm_rep_1") { StartSlot = 1, TransformedFromInstanceId = cardTransOrig.InstanceId };
        var rep2 = new CardInstance(smallItems[3], CardTier.Bronze, null, "itm_rep_2") { StartSlot = 4, TransformedFromInstanceId = cardTransOrig.InstanceId };
        ps3.Hand.TryPlaceOrShift(rep1, 1);
        ps3.Hand.TryPlaceOrShift(rep2, 4);
        var transformBatches = new Dictionary<CardInstance, List<CardInstance>>
        {
            [cardTransOrig] = new List<CardInstance> { rep1, rep2 },
        };

        // (c) 移除 cardEnch 附魔
        cardEnch.RemoveEnchantment();

        var syncOwned = typeof(EncounterStepEngine).GetMethod("SyncOwnedCardsBack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var syncRes = new EncounterStepResult { Success = true };
        syncOwned.Invoke(stepEngine3, new object?[] { ps3, ownedByCore, initialHandSet, initialStashSet, initialSkillSet, initialSocketSet, preSnaps, syncRes, "stp_causal_test", transformBatches });

        Assert.Contains(syncRes.Events.OfType<GameSimEventCardDisposed>(), d => d.InstanceId == cardA.InstanceId);
        Assert.DoesNotContain(syncRes.Events.OfType<GameSimEventCardTransformed>(), tr => tr.OriginalInstanceId == cardA.InstanceId);
        Assert.Contains(syncRes.Events.OfType<GameSimEventCardSpawned>(), sp => sp.TemplateId == smallItems[3].Id);

        var multiTr = Assert.Single(syncRes.Events.OfType<GameSimEventCardTransformed>(), tr => tr.OriginalInstanceId == cardTransOrig.InstanceId);
        Assert.Equal(2, multiTr.TransformedCards.Count);

        var revertEv = Assert.Single(syncRes.Events.OfType<GameSimEventCardEnchanted>(), e => e.InstanceId == cardEnch.InstanceId);
        Assert.True(revertEv.IsReverted);
        Assert.Null(revertEv.EnchantmentType);
        Assert.Null(run3.Cards[cardEnch.InstanceId].Enchantment);
    }

    [Fact]
    public void D2_P1_14_D1_P1_14_SnapshotRoundTrip_ExplicitOriginalDiscount_Tendency_NonGuid_AndZeroBaseTag()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;

        var (run, _) = CreateRun(day: 1, hour: 1, state: ERunState.Encounter, startSelectionDone: false);
        run.TendencyLocked = false;
        run.VisualBindingsRevision = 7;
        run.ActiveCombatOpponentCustomCardIds["opp_inst_1"] = "mod:opp_custom_card_1";
        run.PlayerHourAdditiveMult["Income"] = 0.5;
        run.PlayerDayAdditiveMult["Gold"] = 0.25;
        run.PlayerAuraAdditiveMult["HealthMax"] = 0.1;

        var item12Tpl = repo.CardsById.Values.First(c => c.Type == "Item" && Guid.TryParse(c.Id, out _) && c.CardSize == CardSize.Medium);
        var shelfCore = new CardInstance(item12Tpl, CardTier.Silver, null, "shf_disc_12");
        shelfCore.PermanentAdd["DamageAmount"] = 7;
        shelfCore.PermanentMult["DamageAmount"] = 1.5;
        shelfCore.HourAdd["CritChance"] = 10;
        shelfCore.DayMult["ShieldApplyAmount"] = 1.2;
        var shelfEntry = new RunState.CardEntry
        {
            InstanceId = "shf_disc_12",
            TemplateId = Guid.Parse(item12Tpl.Id),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Silver,
            Owner = null,
            IsFreeCandidate = true,
            CoreCard = shelfCore,
        };
        shelfEntry.Attributes[ECardAttributeType.BuyPrice] = 9;
        run.Cards[shelfEntry.InstanceId] = shelfEntry;
        run.SelectionSet.Add(shelfEntry.InstanceId);
        run.ActiveShelfDiscounts[shelfEntry.InstanceId] = 9;
        run.ActiveShelfOriginalPrices[shelfEntry.InstanceId] = 12;

        var modEncId = "mod:custom_guild_hall";
        var donorEncGuid = Guid.Parse(pool.HourlyEncounters("Vanessa", "EventEncounter").First().Id);
        run.CurrentEncounterId = modEncId;
        run.ParentEncounterTemplateId = modEncId;
        run.ActiveEncounterCardInstanceId = null;
        run.Cards["enc_mod_active"] = new RunState.CardEntry
        {
            InstanceId = "enc_mod_active",
            TemplateId = donorEncGuid,
            CustomCardId = modEncId,
            Type = ECardType.EventEncounter,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = null,
            Socket = EContainerSocketId.Socket_5,
            State = ECardState.Alive,
        };

        var suspSnap = new SuspendedStateSnapshot
        {
            State = ERunState.Loot,
            CurrentEncounterId = modEncId,
            CurrentBoardId = "susp_board_42",
            MonsterLootPageIndex = 1,
        };
        suspSnap.ActiveShelfDiscounts["shf_old"] = 6;
        suspSnap.ActiveShelfOriginalPrices["shf_old"] = 8;
        suspSnap.MonsterLootPages.Add(new List<RunState.MonsterLootPageItemState>
        {
            new(item12Tpl.Id, "Bronze", null, 4),
        });
        suspSnap.MonsterLootPages.Add(new List<RunState.MonsterLootPageItemState>
        {
            new(item12Tpl.Id, "Silver", null, 8),
        });
        run.SuspendedStates.Push(suspSnap);

        string json = RunSnapshotSerializer.Serialize(run);
        Assert.True(RunSnapshotSerializer.TryDeserialize(json, repo, out var restored, out _));
        Assert.NotNull(restored);

        Assert.False(restored!.TendencyLocked);
        Assert.Equal(7, restored.VisualBindingsRevision);
        Assert.Equal("mod:opp_custom_card_1", restored.ActiveCombatOpponentCustomCardIds["opp_inst_1"]);
        Assert.Equal(0.5, restored.PlayerHourAdditiveMult["Income"], precision: 6);
        Assert.Equal(0.25, restored.PlayerDayAdditiveMult["Gold"], precision: 6);
        Assert.Equal(0.1, restored.PlayerAuraAdditiveMult["HealthMax"], precision: 6);

        Assert.Equal("enc_mod_active", restored.ActiveEncounterCardInstanceId);

        Assert.Single(restored.SuspendedStates);
        var restoredSusp = restored.SuspendedStates.Peek();
        Assert.Equal("susp_board_42", restoredSusp.CurrentBoardId);
        Assert.Equal(1, restoredSusp.MonsterLootPageIndex);
        Assert.Equal(2, restoredSusp.MonsterLootPages.Count);
        Assert.Equal(8, restoredSusp.ActiveShelfOriginalPrices["shf_old"]);

        var restoredShelf = restored.Cards["shf_disc_12"];
        Assert.True(restoredShelf.IsFreeCandidate);
        Assert.NotNull(restoredShelf.CoreCard);
        Assert.NotNull(PlayerLayerModifierBridge.GetAttachedStandardPrices(restoredShelf.CoreCard!));
        Assert.Equal(7.0, restoredShelf.CoreCard!.PermanentAdd["DamageAmount"], precision: 3);
        Assert.Equal(1.5, restoredShelf.CoreCard.PermanentMult["DamageAmount"], precision: 3);
        Assert.Equal(10.0, restoredShelf.CoreCard.HourAdd["CritChance"], precision: 3);
        Assert.Equal(1.2, restoredShelf.CoreCard.DayMult["ShieldApplyAmount"], precision: 3);

        Assert.Equal(12, restored.ActiveShelfOriginalPrices["shf_disc_12"]);
        var restoredShop = new ShopEngine(restored, pool, repo, restored.CustomRules);
        restoredShop.RestoreActiveShelfDiscountsFromRun();
        Assert.Equal(9, restoredShelf.Attributes[ECardAttributeType.BuyPrice]);
        Assert.Equal(0.75, restoredShelf.CoreCard.HourMult["BuyPrice"], precision: 2);

        // D1-P1-14：零原生标签物品在临时标签清空后，entry.Tags 必须同步清空无残留
        var zeroTagTpl = new CardTemplate
        {
            Id = item12Tpl.Id,
            InternalName = "ZeroTagTestItem",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tags = new List<string>(),
            HiddenTags = new List<string>(),
            Heroes = new List<string> { "Vanessa" },
        };
        var zeroCore = new CardInstance(zeroTagTpl, CardTier.Bronze, null, "itm_zero_tag");
        var zeroEntry = new RunState.CardEntry
        {
            InstanceId = "itm_zero_tag",
            TemplateId = Guid.Parse(item12Tpl.Id),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = zeroCore,
        };
        zeroCore.AuraTags.Add("Weapon");
        restored.SyncEntryFromCoreCardAndDiff(zeroEntry, repo.GameMode.StandardPrices);
        Assert.Contains(ECardTag.Weapon, zeroEntry.Tags);

        zeroCore.AuraTags.Clear();
        restored.SyncEntryFromCoreCardAndDiff(zeroEntry, repo.GameMode.StandardPrices);
        Assert.DoesNotContain(ECardTag.Weapon, zeroEntry.Tags);
        Assert.Empty(zeroEntry.Tags);
    }

    /// <summary>
    /// [复审补测 #143-1]：自定义 StartingHealth > 300（如 1000）执行 StartRun() 后，
    /// 必须先设 HealthMax 再设 Health，确保满血 1000/1000 开局而非被旧默认 300 截成 300/1000 残血。
    /// </summary>
    [Fact]
    public void ReviewFix_StartingHealthAbove300_StartsAtFullHealth()
    {
        var run = new RunState
        {
            RunId = Guid.NewGuid().ToString("N"),
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            RngSeed = 2026,
            CustomRules = new CustomRunRuleSet
            {
                StartingHealth = 1000,
            },
        };
        var engine = new RunEngine(run, Pool.Value, Monsters.Value, LevelUps.Value, Mode.Value, Repo.Value, new PvpGhostPool());
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);
        Assert.Equal(1000, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(1000, run.PlayerAttributes[EPlayerAttributeType.Health]);
        Assert.Equal(1000.0, run.BasePlayerAttributes["HealthMax"], precision: 6);
    }

    /// <summary>
    /// [复审补测 #142-1]：
    /// 1) ApplyPostCombatPermanentSync 同场同时存在战中永久玩家属性增量与限时层变更（含倍率层）时，
    ///    永久基础值必须同步落盘 BasePlayerAttributes 且下一帧 ApplyLayeredPlayerStateTo 绝不蒸发或折半；
    /// 2) GrantExperienceToList 在携带 HealthMax 倍率光环时升级，BasePlayerAttributes 精确累加 hpInc 且展示值严格按倍率派生。
    /// </summary>
    [Fact]
    public void ReviewFix_PostCombatPermanentSyncAndXpLevelUp_PreserveBaseUnderTimedAndMultLayers()
    {
        var (run, engine) = CreateRun(day: 3, hour: 6, state: ERunState.Choice, healthMax: 300, gold: 20);
        // 挂载 2.0x HealthMax 持续整天倍率层与初始基础值 300 -> 展示 600
        run.PlayerDayMult["HealthMax"] = 2.0;
        run.SetBasePlayerAttribute(EPlayerAttributeType.HealthMax, 300);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Health, 600);
        Assert.Equal(600, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);

        var buildPs = typeof(RunEngine).GetMethod("BuildCorePlayerFromRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var p1Core = (PlayerState)buildPs.Invoke(engine, null)!;
        // 模拟战中同时产生：永久 HealthMax +60、永久 Gold +15，以及 DayAdd["HealthMax"] = +100
        p1Core.DayAdd["HealthMax"] = 100.0;
        var permSync = new PostCombatPermanentSyncResult();
        permSync.PlayerAttributeDeltas["HealthMax"] = 60.0;
        permSync.PlayerAttributeDeltas["Gold"] = 15.0;
        permSync.PlayerDayAddDeltas["HealthMax"] = 100.0;

        var evs = new List<IGameSimEvent>();
        engine.ApplyPostCombatPermanentSync(p1Core, permSync, evs);

        // 永久基础必须从 300 -> 360、Gold 从 20 -> 35；有效 HealthMax = (360 + 100) * 2 = 920
        Assert.Equal(360.0, run.BasePlayerAttributes["HealthMax"], precision: 6);
        Assert.Equal(35.0, run.BasePlayerAttributes["Gold"], precision: 6);
        Assert.Equal(920, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(35, run.PlayerAttributes[EPlayerAttributeType.Gold]);

        // 下一帧重新装配入核：基础值与展示值 100% 保持，绝不蒸发回 300 或反除折半
        var nextFramePs = (PlayerState)buildPs.Invoke(engine, null)!;
        Assert.Equal(360.0, nextFramePs.Attributes["HealthMax"], precision: 6);
        Assert.Equal(35.0, nextFramePs.Attributes["Gold"], precision: 6);
        Assert.Equal(920.0, nextFramePs.GetAttribute("HealthMax"), precision: 6);

        // 2. GrantExperienceToList 在携带 2.0x HealthMax 倍率层时升 Lv2：Base 必须精准 += lvl2Inc 且展示严格按倍率派生
        int lvl2Inc = LevelUps.Value.HealthIncreaseFor(2);
        Assert.True(lvl2Inc > 0);
        int xpReq = Math.Max(1, Mode.Value.ExperiencePerLevel);
        var grantXp = typeof(RunEngine).GetMethod("GrantExperienceToList", BindingFlags.Instance | BindingFlags.NonPublic)!;
        grantXp.Invoke(engine, new object[] { evs, xpReq, EExperienceType.Combat });

        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(360.0 + lvl2Inc, run.BasePlayerAttributes["HealthMax"], precision: 6);
        Assert.Equal((360 + lvl2Inc + 100) * 2, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(run.PlayerAttributes[EPlayerAttributeType.HealthMax], run.PlayerAttributes[EPlayerAttributeType.Health]);
    }

    /// <summary>
    /// [复审补测 #142-2]：玩家属性光环（如 Income / HealthMax）在卖出或拖离后被移除，
    /// SyncLayeredPlayerAttributesFromCore 必须识别 previouslyLayeredKeys 令展示值即时精确回落，且 Health 下钳至 <= HealthMax。
    /// </summary>
    [Fact]
    public void ReviewFix_RemovedPlayerAuraLayer_RevertsDisplayValueAndClampsHealth()
    {
        var (run, engine) = CreateRun(day: 2, hour: 2, state: ERunState.Choice, healthMax: 300);
        run.PlayerAuraAdd["Income"] = 7.0;
        run.PlayerAuraAdd["HealthMax"] = 500.0;
        run.SetBasePlayerAttribute(EPlayerAttributeType.Income, 5);
        run.SetBasePlayerAttribute(EPlayerAttributeType.HealthMax, 300);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Health, 800);
        Assert.Equal(12, run.PlayerAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(800, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(800, run.PlayerAttributes[EPlayerAttributeType.Health]);

        // 模拟卖出/拖出携带玩家光环的物品后，ps 上该两项光环已被清除
        var buildPs = typeof(RunEngine).GetMethod("BuildCorePlayerFromRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var psAfterRemove = (PlayerState)buildPs.Invoke(engine, null)!;
        psAfterRemove.ClearAuraModifiers();

        var applyBatch = typeof(RunEngine).GetMethod("ApplyOutOfCombatBatchWithDirty", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var emptyBatch = new OutOfCombatBatchResult();
        var evs = new List<IGameSimEvent>();
        applyBatch.Invoke(engine, new object?[] { psAfterRemove, emptyBatch, evs, null, false, EExperienceType.Item, null });

        // 移除光环后展示值必须即时回落至基础值 5 / 300，绝不卡死在 12 / 800，且 Health 下钳至 300
        Assert.Equal(5, run.PlayerAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(300, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(300, run.PlayerAttributes[EPlayerAttributeType.Health]);
        Assert.Equal(5, run.LastDerivedPlayerAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(300, run.LastDerivedPlayerAttributes[EPlayerAttributeType.HealthMax]);
    }

    /// <summary>
    /// [复审补测 #142-3 + #143-2]：
    /// 奇遇步骤中存在临时层减益（如 HourMult["HealthMax"]=0.0、DayAdd["Gold"]=-200）使有效值跌穿下限时，
    /// EncounterStepEngine.SyncPlayerAttributesBack 仅钳制展示值（HealthMax=1、Gold=0），
    /// 严禁覆写摧毁合法的 BasePlayerAttributes（400 / 50）；清除临时层后精准恢复 400 / 50。
    /// </summary>
    [Fact]
    public void ReviewFix_EncounterSyncPlayerAttributesBack_TempDebuffClampNeverDestroysBaseAttributes()
    {
        var (run, engine) = CreateRun(day: 2, hour: 2, state: ERunState.Encounter, healthMax: 400, gold: 50);
        Assert.Equal(400.0, run.BasePlayerAttributes["HealthMax"], precision: 6);
        Assert.Equal(50.0, run.BasePlayerAttributes["Gold"], precision: 6);

        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
        var stepEngine = new EncounterStepEngine(run, Pool.Value, Repo.Value, shop, new Random(2026));

        var buildPs = typeof(RunEngine).GetMethod("BuildCorePlayerFromRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ps = (PlayerState)buildPs.Invoke(engine, null)!;
        var prevEff = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["HealthMax"] = 400.0,
            ["Health"] = 400.0,
            ["Gold"] = 50.0,
        };

        // 在奇遇步骤内施加 HourMult["HealthMax"] = 0.0 与 DayAdd["Gold"] = -200（永久基础值 400 / 50 合法未动）
        PlayerLayerModifierBridge.ApplyHourLayer(ps, null, new Dictionary<string, double> { ["HealthMax"] = 0.0 }, null);
        PlayerLayerModifierBridge.ApplyDayLayer(ps, new Dictionary<string, double> { ["Gold"] = -200.0 }, null, null);
        Assert.Equal(0.0, ps.GetAttribute("HealthMax"), precision: 6);
        Assert.Equal(-150.0, ps.GetAttribute("Gold"), precision: 6);

        var syncBack = typeof(EncounterStepEngine).GetMethod("SyncPlayerAttributesBack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stepRes = new EncounterStepResult { Success = true };
        syncBack.Invoke(stepEngine, new object?[] { ps, prevEff, stepRes, null, null });

        // 展示值必须安全钳制为 1 / 0，但永久基础值 400 / 50 毫发无损
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Health]);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(400.0, run.BasePlayerAttributes["HealthMax"], precision: 6);
        Assert.Equal(50.0, run.BasePlayerAttributes["Gold"], precision: 6);

        // 清 Hour 层与 Day 层后，展示值必须精准恢复 400 与 50
        var clearHour = typeof(RunEngine).GetMethod("ClearHourModifiersOnRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var clearDay = typeof(RunEngine).GetMethod("ClearDayModifiersOnRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        clearHour.Invoke(engine, new object?[] { null });
        Assert.Equal(400, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Gold]); // DayAdd=-200 仍在时 Gold 钳为 0 不变负

        clearDay.Invoke(engine, new object?[] { null });
        Assert.Equal(400, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(50, run.PlayerAttributes[EPlayerAttributeType.Gold]);
    }
}
