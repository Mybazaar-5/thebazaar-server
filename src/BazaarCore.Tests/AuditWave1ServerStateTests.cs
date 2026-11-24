using System.Security.Cryptography;
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
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Protocol;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1 Batch 1B（服务端状态机与协议核心 P0）全量回归单测：
/// 覆盖 P0-02、P0-03（服务端收口）、P0-17、P0-18、P0-19、P0-20、P0-21、P0-22、P0-23。
/// </summary>
[Xunit.Collection("ShopEngineStaticState")]
public sealed class AuditWave1ServerStateTests
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
        ushort unlockedSlots = 0b00_0111_1000)
    {
        var run = new RunState
        {
            RunId = "w1b_" + Guid.NewGuid().ToString("N")[..10],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            Day = day,
            Hour = hour,
            State = ERunState.Choice,
            UnlockedSlots = unlockedSlots,
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
            new PvpGhostPool());

        // 标记开局已完成，便于直接进入目标交互态测试
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
        EEnchantmentType? enchantment = null,
        string? instanceId = null)
    {
        var coreTpl = Repo.Value.GetCard(templateId)!;
        var poolTpl = Pool.Value.Get(templateId);
        var core = new CardInstance(
            coreTpl,
            ShopEngine.ToCoreTier(tier),
            enchantment?.ToString(),
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
            Enchantment = enchantment,
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

    private static CardInstance MakeSyntheticItem(string id, CardSize size)
    {
        var tpl = new BazaarCore.Models.CardTemplate
        {
            Id = id,
            Type = "Item",
            Size = size.ToString(),
            StartingTier = "Bronze",
        };
        return new CardInstance(tpl, CardTier.Bronze, null, id);
    }

    // ── P0-02：RewardVictory.SpawnContext 优先执行 + RewardDefeat.SpawnContext 退场步骤 ──

    [Fact]
    public void P0_02_RewardVictorySpawnContext_TakesPriorityOverPaginatedLoot_AndRespectsCanExit()
    {
        const string blowgunsFieryId = "14854ca0-bd1f-4f63-951b-97c1afb3af3b";
        const string exitTrapRoomStepId = "3580fc86-3a8c-48b6-8f24-f732908077bd";
        const string sparringPartnerId = "60be5dca-6908-439c-843a-92dcb5b5dc4e";
        const string scrapItemId = "6ba3778c-e760-44f3-b3b4-a4b4ca267cc2";

        // 1. 丛林陷阱怪在 MonsterLootChooseOne = true 下仍必须执行 RewardVictory.SpawnContext -> Exit Trap Room，且 CanExit == false
        var (runTrap, engineTrap) = CreateStartedRun(
            customRules: new CustomRunRuleSet { MonsterLootChooseOne = true },
            day: 9,
            hour: 2,
            healthMax: 50000);
        runTrap.ActiveExpeditionId = "91287d25-c4c4-4209-8383-f576f1c52d27";
        runTrap.ActiveExpeditionVariant = "Fiery";

        var blowgunsTpl = Pool.Value.Get(blowgunsFieryId)!;
        var monster = Monsters.Value.First(m => m.Id.Equals(blowgunsTpl.MonsterTemplateId, StringComparison.OrdinalIgnoreCase));
        var shopTrap = new ShopEngine(runTrap, Pool.Value, Repo.Value, runTrap.CustomRules);
        var vicEvents = shopTrap.DealMonsterVictoryLoot(blowgunsTpl, monster, p1Core: null, chooseOne: true);

        Assert.Equal(ERunState.Loot, runTrap.State);
        Assert.False(runTrap.SelectionRules.CanExit);
        Assert.Empty(shopTrap.MonsterLootPages);
        Assert.Single(runTrap.SelectionSet);
        var dealtStep = runTrap.Cards[runTrap.SelectionSet[0]];
        Assert.Equal(ECardType.EncounterStep, dealtStep.Type);
        Assert.Equal(Guid.Parse(exitTrapRoomStepId), dealtStep.TemplateId);
        Assert.Contains(vicEvents, e => e is GameSimEventStateTransitioned st && st.ToState == ERunState.Loot);

        // 验证 CanExit == false 时拒绝对未选退出直接离开
        var exitReject = engineTrap.Handle(new ExitCurrentStateCommand());
        Assert.NotNull(exitReject.Error);

        // 2. Sparring Partner 在 MonsterLootChooseOne = true 下优先执行 RewardVictory.SpawnContext -> 固定掉落 Scrap，且保留 CanExit == true
        var (runSpar, _) = CreateStartedRun(customRules: new CustomRunRuleSet { MonsterLootChooseOne = true }, day: 3, hour: 3);
        var sparTpl = Pool.Value.Get(sparringPartnerId)!;
        var sparMonster = Monsters.Value.FirstOrDefault(m => m.Id.Equals(sparTpl.MonsterTemplateId, StringComparison.OrdinalIgnoreCase));
        var shopSpar = new ShopEngine(runSpar, Pool.Value, Repo.Value, runSpar.CustomRules);
        shopSpar.DealMonsterVictoryLoot(sparTpl, sparMonster, p1Core: null, chooseOne: true);

        Assert.Equal(ERunState.Loot, runSpar.State);
        Assert.True(runSpar.SelectionRules.CanExit);
        Assert.Single(runSpar.SelectionSet);
        Assert.Equal(Guid.Parse(scrapItemId), runSpar.Cards[runSpar.SelectionSet[0]].TemplateId);
    }

    [Fact]
    public void P0_02_RewardDefeatSpawnContext_TinyWorldBossDefeat_ExecutesYouAwakenLaterBeforeClearingExpedition()
    {
        const string tinyWorldExpId = "42db3205-0023-4ef5-a519-063be1ea0f99";
        const string barnacleBigwigId = "9f8fa7f6-c322-4ff6-b81f-c379305b97f4";
        const string youAwakenLaterId = "1e454a6c-7d4e-43cb-840c-3a6768a07716";
        const string depletedDeviceId = "021639c0-4a9c-49d8-8254-6376994823e2";

        var (run, engine) = CreateStartedRun(day: 10, hour: 2, healthMax: 1, unlockedSlots: 0b11_1111_1111);
        run.ActiveExpeditionId = tinyWorldExpId;
        run.PlayerAttributes[EPlayerAttributeType.Custom_13] = 1;
        var deviceEntry = AddOwnedItem(run, depletedDeviceId, ETier.Legendary, EInventorySection.Hand, EContainerSocketId.Socket_3);

        // 放一张 Barnacle Bigwig 遭遇卡到 Choice 并触发必败战斗（玩家 1 HP 无武器）
        var bossEntry = new RunState.CardEntry
        {
            InstanceId = "cbt_barnacle",
            TemplateId = Guid.Parse(barnacleBigwigId),
            Type = ECardType.CombatEncounter,
            Size = ECardSize.Medium,
            Tier = ETier.Diamond,
            Owner = null,
        };
        run.Cards[bossEntry.InstanceId] = bossEntry;
        run.SelectionSet.Add(bossEntry.InstanceId);

        var fightOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(bossEntry.InstanceId) });
        Assert.Null(fightOut.Error);
        Assert.NotNull(fightOut.Combat);
        Assert.Equal(ECombatantId.Opponent, fightOut.Combat!.Winner);

        // 战败后必须进入 Loot 展示不可跳过的 You Awaken Later，且此时远征状态与 Depleted Device 尚未提前清除
        Assert.Equal(ERunState.Loot, run.State);
        Assert.False(run.SelectionRules.CanExit);
        Assert.Equal(tinyWorldExpId, run.ActiveExpeditionId);
        Assert.True(run.PendingClearExpeditionAfterLoot);
        Assert.Single(run.SelectionSet);
        var awakenStep = run.Cards[run.SelectionSet[0]];
        Assert.Equal(ECardType.EncounterStep, awakenStep.Type);
        Assert.Equal(Guid.Parse(youAwakenLaterId), awakenStep.TemplateId);

        // 点选 You Awaken Later：销毁 Depleted Device、清零 Custom_13、执行 TActionExitReplacementSet 清空远征状态
        var stepOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(awakenStep.InstanceId) });
        Assert.Null(stepOut.Error);
        Assert.DoesNotContain(deviceEntry.InstanceId, run.Cards.Keys);
        Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_13, 0));
        Assert.Null(run.ActiveExpeditionId);
        Assert.False(run.PendingClearExpeditionAfterLoot);
    }

    // ── P0-03：服务端 ApplyPostCombatPermanentSync 持久属性白名单收口 ──

    [Fact]
    public void P0_03_ApplyPostCombatPermanentSync_AllowsOnlyPersistentWhitelist_AndBlocksTransientLeak()
    {
        var (run, engine) = CreateStartedRun(gold: 10, healthMax: 300);
        run.PlayerAttributes[EPlayerAttributeType.Income] = 5;
        run.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
        run.PlayerAttributes[EPlayerAttributeType.HealthRegen] = 2;
        run.PlayerAttributes[EPlayerAttributeType.Custom_5] = 1;

        var p1Core = new PlayerState("p1", "Tester", "Vanessa", 300, 20, 10, 5);
        var permSync = new PostCombatPermanentSyncResult();
        // 允许白名单
        permSync.PlayerAttributeDeltas["Gold"] = 7;
        permSync.PlayerAttributeDeltas["Income"] = 3;
        permSync.PlayerAttributeDeltas["HealthMax"] = 40;
        permSync.PlayerAttributeDeltas["Prestige"] = -2;
        permSync.PlayerAttributeDeltas["Custom_5"] = 4;
        permSync.PlayerAttributeDeltas["HealthRegen"] = 5;
        // 禁止瞬态属性（必须被拦截）
        permSync.PlayerAttributeDeltas["Enraged"] = 1;
        permSync.PlayerAttributeDeltas["EnragedDuration"] = 4000;
        permSync.PlayerAttributeDeltas["EnragedDurationMax"] = 6000;
        permSync.PlayerAttributeDeltas["Shield"] = 120;
        permSync.PlayerAttributeDeltas["Burn"] = 35;
        permSync.PlayerAttributeDeltas["Poison"] = 18;
        permSync.PlayerAttributeDeltas["Rage"] = 90;
        permSync.PlayerAttributeDeltas["Tempo"] = 6;
        permSync.PlayerAttributeDeltas["Health"] = 999;

        var events = new List<IGameSimEvent>();
        engine.ApplyPostCombatPermanentSync(p1Core, permSync, events);

        Assert.Equal(17, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(8, run.PlayerAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(340, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(340, run.PlayerAttributes[EPlayerAttributeType.Health]);
        Assert.Equal(18, run.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.Equal(5, run.PlayerAttributes[EPlayerAttributeType.Custom_5]);
        Assert.Equal(7, run.PlayerAttributes[EPlayerAttributeType.HealthRegen]);

        // 瞬态属性全部拦截，不得污染 RunState.PlayerAttributes
        Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Enraged, 0));
        Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.EnragedDuration, 0));
        Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.EnragedDurationMax, 0));
        Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Shield, 0));
        Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Burn, 0));
        Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Poison, 0));
        Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Rage, 0));
        Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Tempo, 0));
    }

    // ── P0-17：业务拒单包装 [Error, GameStateSync] + 祭坛附魔模板/防重校验 ──

    [Fact]
    public void P0_17_CommandErrorReply_WrapsTrailingGameStateSyncInInteractiveStates_AndSkipsCombatOrEndRun()
    {
        var (run, _) = CreateStartedRun();
        var errOutcome = new RunEngine.Outcome
        {
            Error = new NetMessageError(null, EResponseErrorType.NotAllowed, "rejected"),
        };

        // 交互态拒单（非祭坛命令）：必须包装 NetMessageAggregate([Error, GameStateSync])，GameStateSync 居尾且后无 GameSim
        run.State = ERunState.Encounter;
        INetCommand[] optimisticCmds =
        [
            new SelectItemCommand { InstanceId = new InstanceId("x") },
            new MoveItemCommand { InstanceId = new InstanceId("x"), Section = EInventorySection.Hand },
            new SellCardCommand { InstanceId = new InstanceId("x") },
            new SelectSkillCommand { InstanceId = new InstanceId("x") },
            new SelectEncounterCommand { InstanceId = new InstanceId("x") },
            new ExitCurrentStateCommand(),
            new RerollCommand(),
        ];
        foreach (var cmd in optimisticCmds)
        {
            var reply = GameServerEndpoints.BuildCommandReply(
                cmd, run, errOutcome, run.State, isOwnedSelectMove: false, isFirstAfterReestablish: false,
                out bool isPureMove, out bool isCombat);
            Assert.False(isPureMove);
            Assert.False(isCombat);
            var agg = Assert.IsType<NetMessageAggregate>(reply);
            Assert.Equal(2, agg.Messages.Count);
            Assert.IsType<NetMessageError>(agg.Messages[0]);
            Assert.IsType<NetMessageGameStateSync>(agg.Messages[1]);
            Assert.DoesNotContain(agg.Messages, m => m is NetMessageGameSim);
        }

        // Wave5A P0-01/P0-02：CommitToPedestalCommand 拒单（即便当前已切至 Encounter）绝不附发 GameStateSync
        var pedCmdReply = GameServerEndpoints.BuildCommandReply(
            new CommitToPedestalCommand { InstanceId = new InstanceId("x") },
            run, errOutcome, run.State, isOwnedSelectMove: false, isFirstAfterReestablish: false,
            out _, out _);
        Assert.IsType<NetMessageError>(pedCmdReply);

        // Combat / PVPCombat / EndRunVictory / EndRunDefeat / Pedestal 态拒单：不得附带 GameStateSync
        foreach (var blockedState in new[] { ERunState.Combat, ERunState.PVPCombat, ERunState.EndRunVictory, ERunState.EndRunDefeat, ERunState.Pedestal })
        {
            run.State = blockedState;
            var reply = GameServerEndpoints.BuildCommandReply(
                new SellCardCommand { InstanceId = new InstanceId("x") },
                run, errOutcome, blockedState, isOwnedSelectMove: false, isFirstAfterReestablish: true,
                out _, out _);
            Assert.IsType<NetMessageError>(reply);
        }
    }

    [Fact]
    public void P0_17_PedestalEnchantment_RejectsDuplicateAndUnsupportedEnchantments()
    {
        // 从真实 DB 找一座定向附魔祭坛（TPedestalBehaviorEnchant）与一张支持的物品卡
        var fixedPedTpl = Repo.Value.CardsById.Values.First(c =>
            c.Type == "PedestalEncounter" &&
            string.Equals(c.Behavior?.Type, "TPedestalBehaviorEnchant", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(c.Behavior?.Enchantment) &&
            Enum.TryParse<EEnchantmentType>(c.Behavior.Enchantment, true, out _));
        var pedEnch = Enum.Parse<EEnchantmentType>(fixedPedTpl.Behavior!.Enchantment!, true);

        // 找一张支持该附魔的物品，以及一张不支持该附魔的物品
        var supportedItemTpl = Repo.Value.CardsById.Values.First(c =>
            c.Type == "Item" &&
            c.Enchantments != null &&
            c.Enchantments.ContainsKey(pedEnch.ToString()) &&
            Guid.TryParse(c.Id, out _));
        var unsupportedItemTpl = Repo.Value.CardsById.Values.First(c =>
            c.Type == "Item" &&
            (c.Enchantments == null || !c.Enchantments.ContainsKey(pedEnch.ToString())) &&
            Guid.TryParse(c.Id, out _));

        // 场景 A：物品已拥有相同附魔 -> 拒单且不消耗祭坛
        var (runDup, engineDup) = CreateStartedRun(unlockedSlots: 0b11_1111_1111);
        runDup.State = ERunState.Pedestal;
        runDup.CurrentEncounterId = fixedPedTpl.Id;
        var dupCard = AddOwnedItem(runDup, supportedItemTpl.Id, ETier.Silver, EInventorySection.Hand, EContainerSocketId.Socket_3, enchantment: pedEnch);

        var dupOut = engineDup.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(dupCard.InstanceId) });
        Assert.NotNull(dupOut.Error);
        Assert.Equal(EResponseErrorType.NotAllowed, dupOut.Error!.ErrorType);
        Assert.Equal(ERunState.Pedestal, runDup.State);
        Assert.Equal(pedEnch, dupCard.Enchantment);

        // 场景 B：物品模板不支持该附魔 -> 拒单且不写入非法附魔
        var (runUnsup, engineUnsup) = CreateStartedRun(unlockedSlots: 0b11_1111_1111);
        runUnsup.State = ERunState.Pedestal;
        runUnsup.CurrentEncounterId = fixedPedTpl.Id;
        var unsupCard = AddOwnedItem(runUnsup, unsupportedItemTpl.Id, ETier.Silver, EInventorySection.Hand, EContainerSocketId.Socket_3);

        var unsupOut = engineUnsup.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(unsupCard.InstanceId) });
        Assert.NotNull(unsupOut.Error);
        Assert.Equal(EResponseErrorType.NotAllowed, unsupOut.Error!.ErrorType);
        Assert.Null(unsupCard.Enchantment);
        Assert.Equal(ERunState.Pedestal, runUnsup.State);
    }

    // ── P0-18：SocketOperations 对齐、TryCrossSectionSwap、降级同步与观战移动 Placement=null ──

    [Fact]
    public void P0_18_BoardGrid_ShortListPreservesOrderAndLength_AndMatchesOfficialEdgeDrop()
    {
        // Day 1 解锁 4 格 [3..6]
        var grid = new BoardGrid(unlockedMask: 0b00_0111_1000);

        // 短列表 [6] 不得补齐为 [5,6]；乱序 [5,4] 不得强制 Sort
        var shortEdge = grid.ModifyOfficialSockets([6], width: 2);
        Assert.NotNull(shortEdge);
        Assert.Equal(new[] { 6 }, shortEdge!);

        var unsorted = grid.ModifyOfficialSockets([5, 4], width: 2);
        Assert.NotNull(unsorted);
        Assert.Equal(new[] { 5, 4 }, unsorted!);

        // 超出左边界 [2] 按官方平移 +1 到 [3]（仍保持短列表长度 1）
        var shiftedLeft = grid.ModifyOfficialSockets([2], width: 3);
        Assert.NotNull(shiftedLeft);
        Assert.Equal(new[] { 3 }, shiftedLeft!);

        // 边缘释放：3 格空位 + 1 张 Small 在槽 3，将 Large(width=3) 拖放到右边缘短列表 [6] -> 应严密落在 [4,5,6] 且 Small 保留在 [3]
        var small = MakeSyntheticItem("s1", CardSize.Small);
        var large = MakeSyntheticItem("l1", CardSize.Large);
        Assert.True(grid.TryPlaceAtFirstContiguousEmptySlot(small));
        Assert.Equal(3, small.StartSlot);

        Assert.True(grid.TryInsertIntoGridSocketOps(large, [6]));
        Assert.Equal(3, small.StartSlot);
        Assert.Equal(4, large.StartSlot);
        Assert.True(grid.ValidateInvariants(out _));
    }

    [Fact]
    public void P0_18_TryCrossSectionSwap_EnforcesExactSocketCount_AndNoSourceGridCompaction()
    {
        var (run, engine) = CreateStartedRun(unlockedSlots: 0b00_0111_1000); // Hand 解锁 4 格: 3..6
        var smallTplId = Repo.Value.AlwaysSpawnableItems.First(c => c.CardSize == CardSize.Small && Guid.TryParse(c.Id, out _)).Id;
        var medTplId = Repo.Value.AlwaysSpawnableItems.First(c => c.CardSize == CardSize.Medium && Guid.TryParse(c.Id, out _)).Id;

        // 填满 Hand [3..6]：两张 Medium 占 [3,4] 与 [5,6]
        var handM1 = AddOwnedItem(run, medTplId, ETier.Bronze, EInventorySection.Hand, EContainerSocketId.Socket_3);
        var handM2 = AddOwnedItem(run, medTplId, ETier.Bronze, EInventorySection.Hand, EContainerSocketId.Socket_5);

        // Stash 10 格中构造碎片空位：放入 S0@0, incomingM@1(占1,2), S1@3, S2@4, S3@5, S4@6, S5@7, S6@8, S7@9
        // 再移除 S0@0 与 S2@4 后，如果 incomingM 是 Small(1) 移出，Stash 仅有碎片 1+1 空位无法放 Medium(2)
        // 为精确测碎片：放 Small(1)@0(空), Small@1, incoming(Small)@2, Small@3, Med@4..5, Med@6..7, Med@8..9
        // 当 incoming(Small) 离开槽 2 后，Stash 仅剩空位 0 与 2（共 2 格但不连续！），无法不挤位回填 width=2 的 blocker!
        var stashS1 = AddOwnedItem(run, smallTplId, ETier.Bronze, EInventorySection.Stash, EContainerSocketId.Socket_1);
        var stashIncoming = AddOwnedItem(run, smallTplId, ETier.Bronze, EInventorySection.Stash, EContainerSocketId.Socket_2);
        var stashS3 = AddOwnedItem(run, smallTplId, ETier.Bronze, EInventorySection.Stash, EContainerSocketId.Socket_3);
        var stashM4 = AddOwnedItem(run, medTplId, ETier.Bronze, EInventorySection.Stash, EContainerSocketId.Socket_4);
        var stashM6 = AddOwnedItem(run, medTplId, ETier.Bronze, EInventorySection.Stash, EContainerSocketId.Socket_6);
        var stashM8 = AddOwnedItem(run, medTplId, ETier.Bronze, EInventorySection.Stash, EContainerSocketId.Socket_8);

        // 1) 碎片空位（0 与 2，无连续 2 空槽）跨区换位 width=2 blocker -> 必须拒绝且不得重排 Stash 既有卡牌
        var fragSwap = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(stashIncoming.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = [EContainerSocketId.Socket_3],
        });
        Assert.NotNull(fragSwap.Error);
        Assert.Equal(EContainerSocketId.Socket_1, stashS1.Socket);
        Assert.Equal(EContainerSocketId.Socket_2, stashIncoming.Socket);
        Assert.Equal(EContainerSocketId.Socket_3, stashS3.Socket);
        Assert.Equal(EInventorySection.Hand, handM1.Section);
        Assert.Equal(EInventorySection.Hand, handM2.Section);

        // 2) 短列表跨区换位：将 Medium(width=2) 拖向满盘 Hand 仅传 1 个槽位 [Socket_3] -> 首行 sockets.Count != SlotWidth 必拒
        var shortSwap = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(stashM4.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = [EContainerSocketId.Socket_3],
        });
        Assert.NotNull(shortSwap.Error);
        Assert.Equal(EInventorySection.Stash, stashM4.Section);
        Assert.Equal(EContainerSocketId.Socket_4, stashM4.Socket);

        // 3) 合法完整槽位列表 [Socket_3, Socket_4] 跨区换位（stashM4 移出后槽 4..5 为现成连续空槽）-> 成功，handM1 回填到 [4,5]，其余卡纹丝不动
        var okSwap = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(stashM4.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = [EContainerSocketId.Socket_3, EContainerSocketId.Socket_4],
        });
        Assert.Null(okSwap.Error);
        Assert.Equal(EInventorySection.Hand, stashM4.Section);
        Assert.Equal(EContainerSocketId.Socket_3, stashM4.Socket);
        Assert.Equal(EInventorySection.Stash, handM1.Section);
        Assert.Equal(EContainerSocketId.Socket_4, handM1.Socket);
        Assert.Equal(EContainerSocketId.Socket_1, stashS1.Socket);
        Assert.Equal(EContainerSocketId.Socket_3, stashS3.Socket);
        Assert.Equal(EContainerSocketId.Socket_6, stashM6.Socket);
        Assert.Equal(EContainerSocketId.Socket_8, stashM8.Socket);
    }

    [Fact]
    public void P0_18_DegradedPlacementAppendsGameStateSyncAtTail_AndMovedCardOmitsPlacementInSimUpdate()
    {
        var (run, engine) = CreateStartedRun(unlockedSlots: 0b00_0111_1000, gold: 50); // 解锁 3,4,5,6
        run.State = ERunState.Encounter;
        run.SelectionRules = new TSelectionContextRules { CanSelectMultiple = true, SelectionIsFree = false, CanExit = true };

        var smallTplId = Repo.Value.AlwaysSpawnableItems.First(c => c.CardSize == CardSize.Small && Guid.TryParse(c.Id, out _)).Id;
        var medTplId = Repo.Value.AlwaysSpawnableItems.First(c => c.CardSize == CardSize.Medium && Guid.TryParse(c.Id, out _)).Id;

        // Hand 构造碎片 2 空槽：槽 3=Small, 槽 4=空, 槽 5=Small, 槽 6=空
        var s3 = AddOwnedItem(run, smallTplId, ETier.Bronze, EInventorySection.Hand, EContainerSocketId.Socket_3);
        var s5 = AddOwnedItem(run, smallTplId, ETier.Bronze, EInventorySection.Hand, EContainerSocketId.Socket_5);

        // 1) 普通同盘移牌（s3 移到槽 4）-> Delta/Full GameSim 中已移动现存卡的 Placement 必须为 null（触发观战 ProcessCardMovedEvents）
        var mvOut = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(s3.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = [EContainerSocketId.Socket_4],
        });
        Assert.Null(mvOut.Error);
        var deltaSim = run.BuildDeltaGameSim(mvOut.Events, mvOut.DirtyCardIds);
        Assert.Contains(mvOut.Events, e => e is GameSimEventCardMoved cm && cm.InstanceId == s3.InstanceId);
        Assert.True(deltaSim.Cards.TryGetValue(s3.InstanceId, out var movedSimCard));
        Assert.Null(movedSimCard!.Placement);

        // 移回槽 3 恢复碎片格局，然后无 TargetSockets 购买 Medium(2) 触发降级落位（官方 GetSocketsForItem 找不到连续 2 空槽，走 TryAddAuto 紧缩）
        engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(s3.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = [EContainerSocketId.Socket_3],
        });

        var shop = new ShopEngine(run, Pool.Value, Repo.Value);
        var shelfMed = shop.CreateItemCandidate(Pool.Value.Get(medTplId)!, ETier.Bronze, isFree: false);
        run.Cards[shelfMed.InstanceId] = shelfMed;
        run.SelectionSet.Add(shelfMed.InstanceId);

        var buyCmd = new SelectItemCommand { InstanceId = new InstanceId(shelfMed.InstanceId), Section = EInventorySection.Hand };
        var buyOut = engine.Handle(buyCmd);
        Assert.Null(buyOut.Error);
        Assert.True(buyOut.AppendStateSyncAtTail);

        var reply = GameServerEndpoints.BuildCommandReply(
            buyCmd, run, buyOut, run.State, isOwnedSelectMove: false, isFirstAfterReestablish: false,
            out bool isPureMove, out _);
        Assert.False(isPureMove);
        var agg = Assert.IsType<NetMessageAggregate>(reply);
        Assert.IsType<NetMessageGameSim>(agg.Messages[0]);
        Assert.IsType<NetMessageGameStateSync>(agg.Messages[^1]);
        // 验证同时位移的现存邻卡（s5）在 GameSim 中省略 Placement，而刚买的 shelfMed 保留 Placement
        var gameSim = ((NetMessageGameSim)agg.Messages[0]).Data;
        Assert.NotNull(gameSim.Cards[shelfMed.InstanceId].Placement);
        if (buyOut.Events.OfType<GameSimEventCardMoved>().Any(m => m.InstanceId == s5.InstanceId))
        {
            Assert.Null(gameSim.Cards[s5.InstanceId].Placement);
        }
    }

    // ── P0-19：物品融合去冗余 CardPurchased；技能融合保留 selectedId Placement(Owner=Player) ──

    [Fact]
    public void P0_19_ItemFusion_OmitsCardPurchased_AndSkillFusion_KeepsTransientOwnerPlayerPlacement()
    {
        var (run, engine) = CreateStartedRun(gold: 50, unlockedSlots: 0b11_1111_1111);
        run.State = ERunState.Encounter;
        run.SelectionRules = new TSelectionContextRules { CanSelectMultiple = true, SelectionIsFree = false, CanExit = true };
        var shop = new ShopEngine(run, Pool.Value, Repo.Value);

        // 1. 物品二合一融合：不得再对源卡 selectedId 下发冗余 GameSimEventCardPurchased
        var itemTpl = Repo.Value.AlwaysSpawnableItems.First(c =>
            c.DefaultTier == CardTier.Bronze && Guid.TryParse(c.Id, out _) && Pool.Value.Get(c.Id) != null);
        var ownedItem = AddOwnedItem(run, itemTpl.Id, ETier.Bronze, EInventorySection.Hand, EContainerSocketId.Socket_3);
        var shelfItem = shop.CreateItemCandidate(Pool.Value.Get(itemTpl.Id)!, ETier.Bronze, isFree: false);
        run.Cards[shelfItem.InstanceId] = shelfItem;
        run.SelectionSet.Add(shelfItem.InstanceId);

        var itemFuseOut = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(shelfItem.InstanceId),
            Section = EInventorySection.Hand,
        });
        Assert.Null(itemFuseOut.Error);
        Assert.Equal(ETier.Silver, ownedItem.Tier);
        Assert.Contains(itemFuseOut.Events, e => e is GameSimEventCardFused f && f.SelectedInstanceId == shelfItem.InstanceId && f.FusedInstanceId == ownedItem.InstanceId);
        // P1-38（#74 批准校准）：买卡二合一融合同帧不再重复发射 CardUpgraded，避免客户端 EnchantManager 抢锁与重复播放升级 VFX
        Assert.DoesNotContain(itemFuseOut.Events, e => e is GameSimEventCardUpgraded);
        Assert.Contains(itemFuseOut.Events, e => e is GameSimEventCardDisposed d && d.InstanceId == shelfItem.InstanceId);
        Assert.DoesNotContain(itemFuseOut.Events, e => e is GameSimEventCardPurchased p && p.InstanceId == shelfItem.InstanceId);

        // 2. 技能二合一融合：selectedId 虽从 RunState.Cards 移除，但同包 SimUpdateCard 中必须临时包含 Placement(Owner=Player)
        var skillPoolTpl = Pool.Value.OfType("Skill").First(s =>
            Pool.Value.IsPlayerSpawnable(s, allowGuidOnly: true) &&
            ShopEngine.ParseTier(s.StartingTier) == ETier.Bronze &&
            Repo.Value.GetCard(s.Id) != null);
        var ownedSkill = shop.CreateSkillCandidate(skillPoolTpl, ETier.Bronze, isFree: true);
        ownedSkill.Owner = ECombatantId.Player;
        ownedSkill.IsFreeCandidate = false;
        run.Cards[ownedSkill.InstanceId] = ownedSkill;

        var shelfSkill = shop.CreateSkillCandidate(skillPoolTpl, ETier.Bronze, isFree: false);
        run.Cards[shelfSkill.InstanceId] = shelfSkill;
        run.SelectionSet.Add(shelfSkill.InstanceId);

        var skillFuseCmd = new SelectSkillCommand { InstanceId = new InstanceId(shelfSkill.InstanceId) };
        var skillFuseOut = engine.Handle(skillFuseCmd);
        Assert.Null(skillFuseOut.Error);
        Assert.Equal(ETier.Silver, ownedSkill.Tier);
        Assert.DoesNotContain(shelfSkill.InstanceId, run.Cards.Keys);
        Assert.Contains(skillFuseOut.Events, e => e is GameSimEventCardFused f && f.SelectedInstanceId == shelfSkill.InstanceId && f.FusedInstanceId == ownedSkill.InstanceId);

        var skillReply = GameServerEndpoints.BuildCommandReply(
            skillFuseCmd, run, skillFuseOut, run.State, isOwnedSelectMove: false, isFirstAfterReestablish: false,
            out _, out _);
        var agg = Assert.IsType<NetMessageAggregate>(skillReply);
        var simMsg = agg.Messages.OfType<NetMessageGameSim>().Single();
        Assert.True(simMsg.Data.Cards.TryGetValue(shelfSkill.InstanceId, out var selectedSimCard));
        Assert.NotNull(selectedSimCard!.Placement);
        Assert.Equal(ECombatantId.Player, selectedSimCard.Placement!.Owner);
    }

    // ── P0-20：CurrentMaxSelections 迁入 RunState 持久化 + 翻页防覆写与防重拿 ──

    [Fact]
    public void P0_20_PaginatedMonsterLoot_PersistsMaxSelections_PreservesOnCycle_AndRemovesPickedCard()
    {
        var rules = new CustomRunRuleSet
        {
            MonsterLootChooseOne = true,
            MonsterLootBonusChoices = 1, // 允许选 2 件战利品
        };
        var (run, engine) = CreateStartedRun(customRules: rules, day: 5, hour: 3, unlockedSlots: 0b11_1111_1111);

        var largeItems = Pool.Value.OfType("Item").Where(c => Pool.Value.IsPlayerSpawnable(c, true) && c.Size == "Large").Take(4).ToList();
        var smallItems = Pool.Value.OfType("Item").Where(c => Pool.Value.IsPlayerSpawnable(c, true) && c.Size == "Small").Take(4).ToList();
        var monster = new BazaarServer.Data.MonsterTemplate
        {
            Id = "p0_20_multi_page_boss",
            InternalName = "Multi-Page Test Boss",
            Items = largeItems.Select(x => new BazaarServer.Data.MonsterTemplate.MonsterItem { TemplateId = x.Id, Tier = "Silver" }).ToList(),
            StashItems = smallItems.Select(x => new BazaarServer.Data.MonsterTemplate.MonsterItem { TemplateId = x.Id, Tier = "Bronze" }).ToList(),
        };

        var shop = new ShopEngine(run, Pool.Value, Repo.Value, rules);
        shop.PreparePaginatedMonsterLoot(monster);

        Assert.Equal(ERunState.Loot, run.State);
        Assert.Equal(2, run.CurrentMaxSelections);
        Assert.True(run.MonsterLootPages.Count >= 2);

        // 1. 验证 CurrentMaxSelections 完整通过快照序列化/反序列化持久化
        string json = RunSnapshotSerializer.Serialize(run, startSelectionDone: true);
        var restored = RunSnapshotSerializer.Deserialize(json, Repo.Value);
        Assert.Equal(2, restored.CurrentMaxSelections);

        // 2. 翻页（OnReroll）不得把 CurrentMaxSelections 覆盖为 1
        var rerollOut = engine.Handle(new RerollCommand());
        Assert.Null(rerollOut.Error);
        Assert.Equal(2, run.CurrentMaxSelections);
        Assert.Equal(1, run.MonsterLootPageIndex);

        // 3. 在第 2 页选第 1 件物品 -> 停留在 Loot、且该模板已从分页缓存移除
        string firstPickId = run.SelectionSet[0];
        var firstPickTplId = run.Cards[firstPickId].TemplateId;
        var pick1Out = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(firstPickId), Section = EInventorySection.Hand });
        Assert.Null(pick1Out.Error);
        Assert.Equal(ERunState.Loot, run.State);
        Assert.Equal(1, run.SelectionsMadeInCurrentContext);
        Assert.Equal(2, run.CurrentMaxSelections);

        // 翻一圈切回原页：已选过的卡绝不能重现
        int pageCount = run.MonsterLootPages.Count;
        for (int i = 0; i < pageCount; i++)
        {
            if (run.MonsterLootPages.Count > 1)
            {
                var cyc = engine.Handle(new RerollCommand());
                Assert.Null(cyc.Error);
            }
            Assert.DoesNotContain(run.SelectionSet, id => run.Cards[id].TemplateId == firstPickTplId);
        }

        // 4. 选第 2 件物品 -> 达到 CurrentMaxSelections=2，正常退场离开 Loot
        string secondPickId = run.SelectionSet[0];
        var pick2Out = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(secondPickId), Section = EInventorySection.Hand });
        Assert.Null(pick2Out.Error);
        Assert.NotEqual(ERunState.Loot, run.State);
    }

    // ── P0-21：runId 维度跨 sid 保留最近 8 条幂等缓存 + ReestablishSession 首包过期拒单补同步 ──

    [Fact]
    public void P0_21_IdempotencyCache_SurvivesSidReestablish_KeepsLatest8_AndForcesStateSyncOnFirstStaleReject()
    {
        string runId = "p0_21_run_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var payloadTemplate = Codec.WriteMessage(new NetMessageError(null, EResponseErrorType.NotAllowed, "cached"));
            var hashes = new byte[10][];
            for (int rid = 1; rid <= 10; rid++)
            {
                hashes[rid - 1] = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"cmd_{rid}"));
                GameServerEndpoints.RecordIdempotentResponse(runId, rid, hashes[rid - 1], payloadTemplate, responseRid: rid + 1);
            }

            // 模拟旧 sid 被移除并经 /sessions 重建新 sid：runId 幂等缓存必须跨 sid 保留
            GameServerEndpoints.MarkSessionReestablished("sid_old", runId);
            GameServerEndpoints.DetachRidCache("sid_old");
            GameServerEndpoints.MarkSessionReestablished("sid_new", runId);

            // 最早 2 条（rid 1, 2）已被淘汰，最近 8 条（rid 3..10）必须全部命中重放
            Assert.False(GameServerEndpoints.TryReplayIdempotentCommand(runId, 1, hashes[0], out _, out _));
            Assert.False(GameServerEndpoints.TryReplayIdempotentCommand(runId, 2, hashes[1], out _, out _));
            for (int rid = 3; rid <= 10; rid++)
            {
                Assert.True(GameServerEndpoints.TryReplayIdempotentCommand(runId, rid, hashes[rid - 1], out var replayed, out long respRid));
                Assert.Equal(payloadTemplate, replayed);
                Assert.Equal(rid + 1, respRid);
            }

            // 同 reqRid 但不同命令体 SHA256 不得误命中
            Assert.False(GameServerEndpoints.TryReplayIdempotentCommand(runId, 10, hashes[0], out _, out _));

            // ReestablishSession 后首次命令命中过期状态/旧 InstanceId 拒单 -> 必须附带末尾 GameStateSync
            Assert.True(GameServerEndpoints.ConsumeSessionReestablished("sid_new"));
            Assert.False(GameServerEndpoints.ConsumeSessionReestablished("sid_new"));

            var (run, _) = CreateStartedRun();
            run.State = ERunState.Choice;
            var staleErr = new RunEngine.Outcome
            {
                Error = new NetMessageError(null, EResponseErrorType.NotFound, "未找到对应卡牌：stale_id"),
            };
            var reply = GameServerEndpoints.BuildCommandReply(
                new CheatCommand { Args = ["noop"] }, // 即便非普通乐观命令，ReestablishSession 首包拒单也必须补发 GameStateSync
                run, staleErr, run.State, isOwnedSelectMove: false, isFirstAfterReestablish: true,
                out _, out _);
            var agg = Assert.IsType<NetMessageAggregate>(reply);
            Assert.Equal(2, agg.Messages.Count);
            Assert.IsType<NetMessageError>(agg.Messages[0]);
            Assert.IsType<NetMessageGameStateSync>(agg.Messages[1]);
        }
        finally
        {
            GameServerEndpoints.DetachRidCache("sid_new");
            GameServerEndpoints.DetachRunRidCache(runId);
        }
    }

    // ── P0-22：单一权威卖价（Value + DoubleEconomy）& 商店词条调价写 CoreCard + 0 元免单光环 ──

    [Fact]
    public void P0_22_AuthoritativeSellPrice_MatchesAcrossCardFaceOnSellAndAutoSell_WithValueAndDoubleEconomy()
    {
        var rules = new CustomRunRuleSet
        {
            MutatorIds = [MutatorCatalog.DoubleEconomy],
        };
        var (run, engine) = CreateStartedRun(customRules: rules, gold: 10, unlockedSlots: 0b11_1111_1111);
        var itemTpl = Repo.Value.AlwaysSpawnableItems.First(c =>
            c.CardSize == CardSize.Small &&
            c.DefaultTier == CardTier.Bronze &&
            !c.Tags.Contains("Unsellable", StringComparer.OrdinalIgnoreCase) &&
            Guid.TryParse(c.Id, out _));

        // 1. 玩家持有卡带 Value=+8 且启用 DoubleEconomy：卡面、ResolveAuthoritativeSellPrice 与 OnSell 实际到账必须三处完全一致
        var owned = AddOwnedItem(run, itemTpl.Id, ETier.Bronze, EInventorySection.Hand, EContainerSocketId.Socket_3);
        owned.CoreCard!.PermanentAdd["Value"] = 8;
        run.SyncEntryFromCoreCardAndDiff(owned, Repo.Value.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        int baseSell = owned.CoreCard!.GetSellPrice(Repo.Value.GameMode.StandardPrices);
        int expectedSell = (baseSell + 8) * 2;
        Assert.Equal(expectedSell, run.ResolveAuthoritativeSellPrice(owned, Repo.Value.GameMode.StandardPrices));
        Assert.Equal(expectedSell, owned.Attributes[ECardAttributeType.SellPrice]);

        int goldBeforeSell = run.PlayerAttributes[EPlayerAttributeType.Gold];
        var sellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(owned.InstanceId) });
        Assert.Null(sellOut.Error);
        var soldEv = Assert.Single(sellOut.Events.OfType<GameSimEventCardSold>());
        Assert.Equal((uint)expectedSell, soldEv.SellPrice);
        Assert.Equal(goldBeforeSell + expectedSell, run.PlayerAttributes[EPlayerAttributeType.Gold]);

        // 2. WillAutoSellOnExit 离场自动卖同样走 ResolveAuthoritativeSellPrice（含 Value + DoubleEconomy）
        run.State = ERunState.Encounter;
        run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = true,
            SelectionIsFree = true,
            CanExit = true,
            WillAutoSellOnExit = true,
        };
        var shop = new ShopEngine(run, Pool.Value, Repo.Value, rules);
        var shelfCard = shop.CreateItemCandidate(Pool.Value.Get(itemTpl.Id)!, ETier.Bronze, isFree: true);
        shelfCard.CoreCard!.PermanentAdd["Value"] = 5;
        run.SyncEntryFromCoreCardAndDiff(shelfCard, Repo.Value.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[shelfCard.InstanceId] = shelfCard;
        run.SelectionSet.Add(shelfCard.InstanceId);

        int expectedAutoSell = (shelfCard.CoreCard!.GetSellPrice(Repo.Value.GameMode.StandardPrices) + 5) * 2;
        Assert.Equal(expectedAutoSell, shelfCard.Attributes[ECardAttributeType.SellPrice]);

        int goldBeforeExit = run.PlayerAttributes[EPlayerAttributeType.Gold];
        var exitOut = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitOut.Error);
        var autoSoldEv = Assert.Single(exitOut.Events.OfType<GameSimEventCardSold>(), e => e.InstanceId == shelfCard.InstanceId);
        Assert.Equal((uint)expectedAutoSell, autoSoldEv.SellPrice);
        Assert.True(run.PlayerAttributes[EPlayerAttributeType.Gold] >= goldBeforeExit + expectedAutoSell);
    }

    [Fact]
    public void P0_22_ShopHalfOffAndZeroPriceAura_PersistThroughSyncShelfDiff_AndChargeExactFacePrice()
    {
        var prices = Repo.Value.GameMode.StandardPrices;
        var itemTpl = Pool.Value.OfType("Item").First(c =>
            Pool.Value.IsPlayerSpawnable(c, allowGuidOnly: false) &&
            c.Size == "Medium" &&
            Repo.Value.GetCard(c.Id) != null);

        // 1. ShopHalfOff 写入 CoreCard.PermanentMult["BuyPrice"]，在 SyncAllEntriesFromCoreCardsAndDiff 后绝不反弹回原价
        var halfRules = new CustomRunRuleSet { MutatorIds = [MutatorCatalog.ShopHalfOff] };
        var (runHalf, engineHalf) = CreateStartedRun(customRules: halfRules, gold: 50, unlockedSlots: 0b11_1111_1111);
        var shopHalf = new ShopEngine(runHalf, Pool.Value, Repo.Value, halfRules);
        var merchantTpl = Pool.Value.HourlyEncounters("Vanessa", "EventEncounter").First(e =>
            CardArchetypeIndex.IsMerchantEncounterTemplate(e.Id, e.InternalName, e, Repo.Value.GetCard(e.Id)));
        shopHalf.DealEncounterShelf(merchantTpl.Id);

        var paidItem = runHalf.SelectionSet
            .Select(id => runHalf.Cards[id])
            .First(c => c.Type == ECardType.Item && c.CoreCard != null);
        Assert.True(paidItem.CoreCard!.PermanentMult.TryGetValue("BuyPrice", out double pm) && Math.Abs(pm - 0.5) < 1e-6);
        int discountedFace = paidItem.Attributes[ECardAttributeType.BuyPrice];

        runHalf.SyncAllEntriesFromCoreCardsAndDiff(prices, [paidItem]);
        Assert.Equal(discountedFace, paidItem.Attributes[ECardAttributeType.BuyPrice]);

        // 购买实际扣费与卡面标价完全一致
        int goldBeforeBuy = runHalf.PlayerAttributes[EPlayerAttributeType.Gold];
        var buyOut = engineHalf.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(paidItem.InstanceId),
            Section = EInventorySection.Hand,
        });
        Assert.Null(buyOut.Error);
        Assert.Equal(goldBeforeBuy - discountedFace, runHalf.PlayerAttributes[EPlayerAttributeType.Gold]);

        // 2. 显式 0 乘区（SandboxZeroShopPrice & 免费光环 AuraMult["BuyPrice"] = 0.0）允许准确归零，不得拉回 1 金
        var (runZero, _) = CreateStartedRun(gold: 0);
        runZero.SelectionRules = new TSelectionContextRules { CanSelectMultiple = true, SelectionIsFree = false, CanExit = true };
        var shopZero = new ShopEngine(runZero, Pool.Value, Repo.Value);
        var card = shopZero.CreateItemCandidate(itemTpl, ETier.Silver, isFree: false);
        Assert.True(card.Attributes[ECardAttributeType.BuyPrice] > 1);

        card.CoreCard!.AuraMult["BuyPrice"] = 0.0;
        Assert.Equal(0, RunState.ComputeCardBuyPrice(card, card.CoreCard!, prices));
        runZero.SyncAllEntriesFromCoreCardsAndDiff(prices, [card]);
        Assert.Equal(0, card.Attributes[ECardAttributeType.BuyPrice]);

        // 光环移除后恢复正常正买价
        card.CoreCard!.AuraMult.Remove("BuyPrice");
        runZero.SyncAllEntriesFromCoreCardsAndDiff(prices, [card]);
        Assert.True(card.Attributes[ECardAttributeType.BuyPrice] > 1);

        // SandboxZeroShopPrice 写入 PermanentMult["BuyPrice"] = 0.0 且同步后保持 0 元
        var sandboxRules = new CustomRunRuleSet { SpecialModeId = ESpecialModeId.SandboxLab, SandboxZeroShopPrice = true };
        var (runSandbox, _) = CreateStartedRun(customRules: sandboxRules, gold: 0);
        var shopSandbox = new ShopEngine(runSandbox, Pool.Value, Repo.Value, sandboxRules);
        shopSandbox.DealEncounterShelf(merchantTpl.Id);
        var sandboxGoods = runSandbox.SelectionSet.Select(id => runSandbox.Cards[id]).Where(c => c.Type == ECardType.Item && c.CoreCard != null).ToList();
        Assert.NotEmpty(sandboxGoods);
        runSandbox.SyncAllEntriesFromCoreCardsAndDiff(prices, sandboxGoods);
        Assert.All(sandboxGoods, g =>
        {
            Assert.Equal(0, g.Attributes[ECardAttributeType.BuyPrice]);
            Assert.Equal(0.0, g.CoreCard!.PermanentMult["BuyPrice"]);
        });
    }

    // ── P0-23：SocketEffect 解锁槽位过滤 + 同槽位覆盖改发 GameSimEventCardTransformed ──

    [Fact]
    public void P0_23_FindFreeSocketEffectSlot_RespectsUnlockedMask_AndSameSlotOverwriteEmitsCardTransformed()
    {
        // Day 1 仅解锁 3..6（0,1,2,7,8,9 均为锁定槽）
        var (run, engine) = CreateStartedRun(day: 1, hour: 1, unlockedSlots: 0b00_0111_1000);
        Assert.Equal(3, engine.FindFreeSocketEffectSlot());

        var stoveTpl = Repo.Value.CardsById.Values.First(c =>
            string.Equals(c.Type, "SocketEffect", StringComparison.OrdinalIgnoreCase) &&
            !c.MusicNote.HasValue &&
            Guid.TryParse(c.Id, out _));
        var noteTpl = Repo.Value.CardsById.Values.First(c =>
            string.Equals(c.Type, "SocketEffect", StringComparison.OrdinalIgnoreCase) &&
            c.MusicNote.HasValue &&
            Guid.TryParse(c.Id, out _));

        // 占满槽 3 后，FindFreeSocketEffectSlot 应跳到 4（绝不选锁定槽 0）
        var oldStoveCore = new CardInstance(stoveTpl, CardTier.Bronze, null, "skt_stove_3") { StartSlot = 3 };
        var oldStoveEntry = new RunState.CardEntry
        {
            InstanceId = oldStoveCore.InstanceId,
            TemplateId = Guid.Parse(stoveTpl.Id),
            Type = ECardType.SocketEffect,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = oldStoveCore,
        };
        run.Cards[oldStoveEntry.InstanceId] = oldStoveEntry;
        Assert.Equal(4, engine.FindFreeSocketEffectSlot());

        // 同槽位（槽 3）音符覆盖旧灶台：经 ApplyPostCombatPermanentSync 登记新 SocketEffect 到槽 3 时，
        // 必须发射 GameSimEventCardTransformed（先摘旧再放新），绝不同包混发 CardSpawned + CardDisposed
        var newNoteCore = new CardInstance(noteTpl, CardTier.Bronze, null, "skt_note_3") { StartSlot = 3 };
        var p1Core = new PlayerState("p1", "Tester", "Jules", 300, 20, 10, 5);
        p1Core.SocketEffects.Add(newNoteCore);
        var permSync = new PostCombatPermanentSyncResult();
        permSync.SpawnedCards.Add(newNoteCore);

        var events = new List<IGameSimEvent>();
        engine.ApplyPostCombatPermanentSync(p1Core, permSync, events);

        Assert.DoesNotContain(oldStoveEntry.InstanceId, run.Cards.Keys);
        Assert.True(run.Cards.TryGetValue(newNoteCore.InstanceId, out var installedNote));
        Assert.Equal(EContainerSocketId.Socket_3, installedNote!.Socket);

        var tr = Assert.Single(events.OfType<GameSimEventCardTransformed>());
        Assert.Equal(oldStoveEntry.InstanceId, tr.OriginalInstanceId);
        Assert.Single(tr.TransformedCards);
        Assert.Equal(newNoteCore.InstanceId, tr.TransformedCards[0].InstanceId);
        Assert.Equal(EContainerSocketId.Socket_3, tr.TransformedCards[0].Socket);
        Assert.DoesNotContain(events, e => e is GameSimEventCardSpawned sp && sp.InstanceId == newNoteCore.InstanceId);
        Assert.DoesNotContain(events, e => e is GameSimEventCardDisposed dp && dp.InstanceId == oldStoveEntry.InstanceId);
    }

    [Fact]
    public void P0_23_ApplyOutOfCombatBatch_SameSlotSocketEffectOverwrite_EmitsCardTransformed()
    {
        var (run, engine) = CreateStartedRun(day: 1, hour: 1, unlockedSlots: 0b00_0111_1000);

        var stoveTpl = Repo.Value.CardsById.Values.First(c =>
            string.Equals(c.Type, "SocketEffect", StringComparison.OrdinalIgnoreCase) &&
            !c.MusicNote.HasValue &&
            Guid.TryParse(c.Id, out _));
        var noteTpl = Repo.Value.CardsById.Values.First(c =>
            string.Equals(c.Type, "SocketEffect", StringComparison.OrdinalIgnoreCase) &&
            c.MusicNote.HasValue &&
            Guid.TryParse(c.Id, out _));

        var oldStove = new CardInstance(stoveTpl, CardTier.Bronze, null, "skt_ooc_old") { StartSlot = 4 };
        run.Cards[oldStove.InstanceId] = new RunState.CardEntry
        {
            InstanceId = oldStove.InstanceId,
            TemplateId = Guid.Parse(stoveTpl.Id),
            Type = ECardType.SocketEffect,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_4,
            CoreCard = oldStove,
        };

        var newNote = new CardInstance(noteTpl, CardTier.Bronze, null, "skt_ooc_new") { StartSlot = 4 };
        var p1Core = new PlayerState("p1", "Tester", "Jules", 300, 20, 10, 5);
        p1Core.SocketEffects.Add(newNote);

        var batch = new OutOfCombatBatchResult();
        batch.RemovedCards.Add(oldStove);
        batch.SpawnedCards.Add(new OutOfCombatSpawnedCard
        {
            Card = newNote,
            Section = "Hand",
            StartSlot = 4,
        });

        var events = new List<IGameSimEvent>();
        var applyMethod = typeof(RunEngine).GetMethod(
            "ApplyOutOfCombatBatch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        applyMethod.Invoke(engine, [p1Core, batch, events, null, false, EExperienceType.Item]);

        Assert.DoesNotContain(oldStove.InstanceId, run.Cards.Keys);
        Assert.True(run.Cards.TryGetValue(newNote.InstanceId, out var installed));
        Assert.Equal(EContainerSocketId.Socket_4, installed!.Socket);

        var tr = Assert.Single(events.OfType<GameSimEventCardTransformed>());
        Assert.Equal(oldStove.InstanceId, tr.OriginalInstanceId);
        Assert.Equal(newNote.InstanceId, Assert.Single(tr.TransformedCards).InstanceId);
        Assert.DoesNotContain(events, e => e is GameSimEventCardDisposed dp && dp.InstanceId == oldStove.InstanceId);
        Assert.DoesNotContain(events, e => e is GameSimEventCardSpawned sp && sp.InstanceId == newNote.InstanceId);
    }

    [Fact]
    public void P0_18_BoardGrid_RandomizedFuzzParityWithOfficialSocketOperations()
    {
        // 对照官方 SocketOperations.cs（ModifySockets / AddItemToContainer / MoveCardToSocketOnSameCarpet）
        // 在 3,000 组随机棋盘（含 Lv1~Lv4 解锁掩码、Small/Medium/Large 混合、边缘短列表、乱序/超长列表）上验证 100% 槽位一致
        var rng = new Random(20261122);
        int[] masks = [0b00_0111_1000, 0b00_1111_1100, 0b01_1111_1110, 0b11_1111_1111];
        CardSize[] sizes = [CardSize.Small, CardSize.Medium, CardSize.Large];

        for (int trial = 0; trial < 3000; trial++)
        {
            int mask = masks[rng.Next(masks.Length)];
            var grid = new BoardGrid(mask);
            var refArr = new string?[10];
            var refSizes = new Dictionary<string, int>();
            var cards = new List<CardInstance>();

            int lowest = Enumerable.Range(0, 10).First(s => (mask & (1 << s)) != 0);
            int highest = Enumerable.Range(0, 10).Last(s => (mask & (1 << s)) != 0);

            // 随机填充若干张既有卡
            int cursor = lowest;
            int idx = 0;
            while (cursor <= highest)
            {
                if (rng.Next(3) == 0)
                {
                    cursor++;
                    continue;
                }
                var sz = sizes[rng.Next(sizes.Length)];
                int w = (int)sz;
                if (cursor + w - 1 > highest)
                {
                    cursor++;
                    continue;
                }
                string id = $"c{idx++}";
                var card = MakeSyntheticItem(id, sz);
                card.StartSlot = cursor;
                grid.Cards.Add(card);
                cards.Add(card);
                refSizes[id] = w;
                for (int k = 0; k < w; k++) refArr[cursor + k] = id;
                cursor += w;
            }

            bool testSameMove = cards.Count > 0 && rng.Next(2) == 0;
            if (testSameMove)
            {
                var moving = cards[rng.Next(cards.Count)];
                int origStart = moving.StartSlot;
                int rawLen = rng.Next(1, moving.SlotWidth + 2); // 含短列表与超长列表
                var raw = new List<int>(rawLen);
                int start = rng.Next(0, 10);
                bool reversed = rawLen > 1 && rng.Next(4) == 0;
                for (int k = 0; k < rawLen; k++)
                {
                    int s = Math.Clamp(start + (reversed ? -k : k), 0, 9);
                    raw.Add(s);
                }

                bool actualOk = grid.TryMoveSameGridSocketOps(moving, raw);
                var (refOk, refResult) = OfficialRefMoveSameGrid(refArr, refSizes, mask, moving.InstanceId, moving.SlotWidth, raw);
                // 注：当目标起始槽等于当前起始槽时，官方 MoveCardToSocketOnSameCarpet 返回 false(no-op)，BoardGrid 幂等返回 true 且盘面不变
                var modSockets = OfficialRefModifySockets(mask, raw.Take(Math.Min(moving.SlotWidth, raw.Count)).ToList());
                bool isNoOpSameStart = modSockets.Count > 0 && origStart == modSockets[0];
                if (isNoOpSameStart)
                {
                    Assert.True(actualOk);
                    Assert.Equal(refArr, ExportGridSlots(grid));
                }
                else
                {
                    Assert.Equal(refOk, actualOk);
                    if (refOk)
                    {
                        Assert.Equal(refResult, ExportGridSlots(grid));
                    }
                }
            }
            else
            {
                var incoming = MakeSyntheticItem("new", sizes[rng.Next(sizes.Length)]);
                refSizes["new"] = incoming.SlotWidth;
                List<int>? raw = null;
                if (rng.Next(5) != 0)
                {
                    int rawLen = rng.Next(1, incoming.SlotWidth + 2);
                    raw = new List<int>(rawLen);
                    int start = rng.Next(0, 10);
                    for (int k = 0; k < rawLen; k++)
                        raw.Add(Math.Clamp(start + k, 0, 9));
                }

                bool actualOk = grid.TryInsertIntoGridSocketOps(incoming, raw);
                var (refOk, refResult) = OfficialRefInsert(refArr, refSizes, mask, "new", incoming.SlotWidth, raw);
                Assert.Equal(refOk, actualOk);
                if (refOk)
                {
                    Assert.Equal(refResult, ExportGridSlots(grid));
                }
            }
        }
    }

    private static string?[] ExportGridSlots(BoardGrid grid)
    {
        var arr = new string?[10];
        foreach (var c in grid.Cards)
        {
            for (int k = 0; k < c.SlotWidth; k++)
                arr[c.StartSlot + k] = c.InstanceId;
        }
        return arr;
    }

    private sealed class RefNode(string? id, int size, List<int> sockets, bool locked = false)
    {
        public readonly string? Id = id;
        public readonly int Size = size;
        public readonly List<int> Sockets = sockets;
        public readonly bool Locked = locked;
    }

    private static List<int> OfficialRefModifySockets(int mask, List<int> sockets)
    {
        int lowest = Enumerable.Range(0, 10).First(s => (mask & (1 << s)) != 0);
        int highest = Enumerable.Range(0, 10).Last(s => (mask & (1 << s)) != 0);
        int maxDist = 0, shift = 0;
        foreach (int s in sockets)
        {
            int clamped = Math.Clamp(s, lowest, highest);
            int dist = Math.Abs(clamped - s);
            if (dist > maxDist)
            {
                maxDist = dist;
                shift = clamped - s;
            }
        }
        var copy = new List<int>(sockets);
        if (shift != 0)
        {
            for (int i = 0; i < copy.Count; i++) copy[i] += shift;
        }
        return copy;
    }

    private static (bool Ok, string?[] Arr) OfficialRefMoveSameGrid(
        string?[] origArr, Dictionary<string, int> sizes, int mask, string id, int width, List<int> rawSockets)
    {
        var truncated = width < rawSockets.Count ? rawSockets.Take(width).ToList() : new List<int>(rawSockets);
        var sockets = OfficialRefModifySockets(mask, truncated);
        if (sockets.Count == 0 || sockets.Any(s => s < 0 || s >= 10)) return (false, origArr);
        int curStart = Array.IndexOf(origArr, id);
        if (curStart < 0 || curStart == sockets[0]) return (false, origArr);
        int dir = curStart > sockets[0] ? -1 : 1;

        var arr = (string?[])origArr.Clone();
        for (int i = 0; i < 10; i++) if (arr[i] == id) arr[i] = null;

        var list = new LinkedList<RefNode>();
        for (int i = 0; i < 10; i++)
        {
            if ((mask & (1 << i)) == 0)
            {
                list.AddLast(new RefNode(null, 1, [i], locked: true));
                continue;
            }
            string? c = arr[i];
            int w = c != null ? sizes[c] : 1;
            var occ = Enumerable.Range(i, w).ToList();
            list.AddLast(new RefNode(c, w, occ));
            i += w - 1;
        }

        int anchor = dir < 0 ? sockets[0] : sockets[^1];
        var found = list.First;
        while (found != null && !found.Value.Sockets.Contains(anchor)) found = found.Next;
        var cardNode = new RefNode(id, width, sockets);
        if (found == null) list.AddLast(cardNode);
        else if (dir >= 0) list.AddAfter(found, cardNode);
        else list.AddBefore(found, cardNode);

        int rem = width;
        if (dir >= 0)
        {
            rem -= RefRemoveLeft(list, id, rem);
            if (rem > 0) rem -= RefRemoveRight(list, id, rem);
        }
        else
        {
            rem -= RefRemoveRight(list, id, rem);
            if (rem > 0) rem -= RefRemoveLeft(list, id, rem);
        }
        if (rem > 0) return (false, origArr);

        var built = RefBuildSockets(list);
        return built != null ? (true, built) : (false, origArr);
    }

    private static (bool Ok, string?[] Arr) OfficialRefInsert(
        string?[] origArr, Dictionary<string, int> sizes, int mask, string id, int width, List<int>? rawSockets)
    {
        int emptyUnlocked = Enumerable.Range(0, 10).Count(i => (mask & (1 << i)) != 0 && origArr[i] == null);
        if (emptyUnlocked < width) return (false, origArr);

        List<int> sockets;
        if (rawSockets is { Count: > 0 })
        {
            var truncated = width < rawSockets.Count ? rawSockets.Take(width).ToList() : new List<int>(rawSockets);
            sockets = OfficialRefModifySockets(mask, truncated);
            if (sockets.Any(s => s < 0 || s >= 10)) return (false, origArr);
        }
        else
        {
            int runLen = 0, start = -1;
            for (int i = 0; i < 10; i++)
            {
                if ((mask & (1 << i)) != 0 && origArr[i] == null)
                {
                    if (++runLen == width) { start = i - width + 1; break; }
                }
                else runLen = 0;
            }
            if (start < 0) return (false, origArr);
            sockets = Enumerable.Range(start, width).ToList();
        }

        var list = new LinkedList<RefNode>();
        var first = list.AddFirst(new RefNode(id, width, sockets));
        var rightAnchor = first;
        int leftRem = sockets[0];
        int rightRem = 10 - sockets[^1] - 1;

        bool IsRangeUnlocked(int st, int sz)
        {
            for (int j = st; j < st + sz && j < 10; j++)
                if ((mask & (1 << j)) == 0) return false;
            return true;
        }

        for (int i = 0; i < 10; i++)
        {
            string? c = origArr[i];
            if ((mask & (1 << i)) == 0)
            {
                if (i < sockets[0]) list.AddFirst(new RefNode(null, 1, [], locked: true));
                else if (i > sockets[^1]) list.AddLast(new RefNode(null, 1, [], locked: true));
                continue;
            }
            int sz = c != null ? sizes[c] : 0;
            int step = c != null ? sz - 1 : 0;
            if (i <= sockets[0] && sz <= leftRem && IsRangeUnlocked(i, sz))
            {
                list.AddBefore(first, new RefNode(c, c != null ? sz : 1, []));
                i += step; leftRem -= sz;
            }
            else if (i <= sockets[0] && sz <= rightRem)
            {
                var n = new RefNode(c, c != null ? sz : 1, []);
                list.AddAfter(rightAnchor, n);
                rightAnchor = list.Find(n)!;
                i += step; rightRem -= sz;
            }
            else if (i >= sockets[^1] && sz <= rightRem)
            {
                list.AddLast(new RefNode(c, c != null ? sz : 1, []));
                i += step; rightRem -= sz;
            }
            else if (i >= sockets[^1] && sz <= leftRem)
            {
                list.AddBefore(first, new RefNode(c, c != null ? sz : 1, []));
                i += step; leftRem -= sz;
            }
            else if (i >= sockets[0] && sz <= leftRem)
            {
                list.AddBefore(first, new RefNode(c, c != null ? sz : 1, []));
                i += step; leftRem -= sz;
            }
            else if (i >= sockets[0] && sz <= rightRem)
            {
                var n = new RefNode(c, c != null ? sz : 1, []);
                list.AddAfter(rightAnchor, n);
                rightAnchor = list.Find(n)!;
                i += step; rightRem -= sz;
            }
            else if (i <= sockets[0])
            {
                list.AddBefore(first, new RefNode(c, c != null ? sz : 1, []));
                i += step; leftRem -= sz;
            }
            else if (i >= sockets[^1])
            {
                list.AddLast(new RefNode(c, c != null ? sz : 1, []));
                i += step; rightRem -= sz;
            }
        }

        if (rightRem < 0) RefRemoveLeft(list, id, Math.Abs(rightRem));
        if (leftRem < 0) RefRemoveLeft(list, id, Math.Abs(leftRem));

        int total = list.Sum(n => n.Id == null ? 1 : n.Size);
        int before = 0;
        for (var cur = list.First; cur != null && cur != first; cur = cur.Next)
            before += cur.Value.Id == null ? 1 : cur.Value.Size;
        if (before >= sockets[0]) total -= RefRemoveLeft(list, id, before - sockets[0]);
        if (total > 10) total -= RefRemoveRight(list, id, total - 10);
        if (total > 10) total -= RefRemoveLeft(list, id, total - 10);
        while (total < 10) { list.AddAfter(first, new RefNode(null, 1, [])); total++; }

        var built = RefBuildSockets(list);
        return built != null ? (true, built) : (false, origArr);
    }

    private static int RefRemoveLeft(LinkedList<RefNode> list, string id, int count)
    {
        if (count <= 0) return 0;
        var cur = list.First;
        while (cur != null && cur.Value.Id != id) cur = cur.Next;
        int removed = 0;
        while (cur != null && count > 0)
        {
            var prev = cur.Previous;
            if (cur.Value.Id == null && !cur.Value.Locked) { list.Remove(cur); count--; removed++; }
            cur = prev;
        }
        return removed;
    }

    private static int RefRemoveRight(LinkedList<RefNode> list, string id, int count)
    {
        if (count <= 0) return 0;
        var cur = list.First;
        while (cur != null && cur.Value.Id != id) cur = cur.Next;
        int removed = 0;
        while (cur != null && count > 0)
        {
            var next = cur.Next;
            if (cur.Value.Id == null && !cur.Value.Locked) { list.Remove(cur); count--; removed++; }
            cur = next;
        }
        return removed;
    }

    private static string?[]? RefBuildSockets(LinkedList<RefNode> list)
    {
        var arr = new string?[10];
        int cursor = 0;
        foreach (var n in list)
        {
            int w = n.Id == null ? 1 : n.Size;
            if (cursor + w > 10) return null;
            for (int k = 0; k < w; k++) arr[cursor + k] = n.Id;
            cursor += w;
        }
        return arr;
    }
}
