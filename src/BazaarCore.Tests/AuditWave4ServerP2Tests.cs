using System.Reflection;
using System.Text.Json;
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
using BazaarGameShared.TempoNet.Models;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 4 Batch 4B-Server（服务端全部剩余 22 项 P2 收官）回归单测：
/// 覆盖 P1-17(SpawnCount)、P2-04、P2-06(Consumes)、P2-07、P2-08、P2-11、P2-12、P2-14、
/// P2-16、P2-18、P2-26、P2-27、P2-28、P2-29、P2-30、P2-31、P2-32、P2-33、P2-34、P2-35、P2-36、P2-38、P2-48。
/// </summary>
public sealed class AuditWave4ServerP2Tests
{
    private const string CutlassItemId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string PolShopId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string FangedIngletCombatId = "1ea147f3-5712-4333-b347-8fd65d08f009";
    private const string MidsworthId = "b8f55bb0-536e-4d5f-a328-d38f88c6d479";
    private const string CurioId = "c6cffe25-8e94-4a7b-a821-3e3c7093333b";
    private const string NauticaId = "1a21931f-6183-4251-914b-c8f83dc58a30";
    private const string ColtId = "816e6ba0-8f5f-412e-9756-8e1901dd9d49";
    private const string KevsArmoryId = "a4fa13f8-6beb-4b6c-839b-60af167628d9";

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

    private static GameDataRepository CloneRepoWithOverrides(
        GameDataRepository baseRepo,
        IReadOnlyDictionary<string, BazaarCore.Models.CardTemplate>? extraCards = null,
        GameModeData? gameModeOverride = null)
    {
        var cards = new Dictionary<string, BazaarCore.Models.CardTemplate>(baseRepo.CardsById, StringComparer.OrdinalIgnoreCase);
        if (extraCards != null)
        {
            foreach (var (k, v) in extraCards)
                cards[k] = v;
        }
        var monsters = new Dictionary<string, BazaarCore.Models.MonsterTemplate>(baseRepo.MonstersById, StringComparer.OrdinalIgnoreCase);
        var levelUps = new Dictionary<int, LevelUpTemplate>(baseRepo.LevelUps);
        var tooltips = new Dictionary<string, string>(baseRepo.TooltipGlossary, StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(baseRepo.ObservedRuleTypes, StringComparer.OrdinalIgnoreCase);
        var unknown = new HashSet<string>(baseRepo.UnknownRuleTypes, StringComparer.OrdinalIgnoreCase);
        return new GameDataRepository(
            baseRepo.SourceZipPath,
            gameModeOverride ?? baseRepo.GameMode,
            cards,
            monsters,
            levelUps,
            tooltips,
            observed,
            unknown,
            baseRepo.Localization,
            baseRepo.SourceDataPath);
    }

    private static (RunState Run, RunEngine Engine) CreateRun(
        EHero hero = EHero.Vanessa,
        uint day = 2,
        uint hour = 1,
        ERunState state = ERunState.Choice,
        int gold = 60,
        int healthMax = 400,
        ushort unlockedSlots = 0b00_0111_1000,
        bool startSelectionDone = true,
        GameModeConfig? modeOverride = null,
        GameDataRepository? repoOverride = null,
        CustomRunRuleSet? customRules = null,
        LobbyStore? lobbies = null,
        string? accountId = null,
        Guid? tournamentSessionId = null)
    {
        var run = new RunState
        {
            RunId = "w4b_" + Guid.NewGuid().ToString("N")[..10],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = tournamentSessionId.HasValue ? EPlayMode.Tournament : EPlayMode.Ranked,
            AccountId = accountId ?? Guid.NewGuid().ToString("N"),
            Username = "Wave4Tester",
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
            modeOverride ?? Mode.Value,
            repoOverride ?? Repo.Value,
            new PvpGhostPool(),
            lobbies: lobbies);
        return (run, engine);
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        string templateId,
        EInventorySection section = EInventorySection.Hand,
        EContainerSocketId socket = EContainerSocketId.Socket_3,
        ETier tier = ETier.Bronze,
        GameDataRepository? repo = null)
    {
        var r = repo ?? Repo.Value;
        var coreTpl = r.GetCard(templateId)!;
        string instId = "itm_" + Guid.NewGuid().ToString("N")[..8];
        var core = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), null, instId)
        {
            StartSlot = (int)socket,
        };
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = socket,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, Pool.Value.Get(templateId), coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, r.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[instId] = entry;
        if (section == EInventorySection.Hand)
            run.HandGrid.TryPlaceOrShift(core, (int)socket);
        else
            run.StashGrid.TryPlaceOrShift(core, (int)socket);
        return entry;
    }

    private static object? InvokePrivate(object target, string methodName, params object?[] args)
    {
        var m = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(mi => mi.Name == methodName && mi.GetParameters().Length == args.Length);
        return m.Invoke(target, args);
    }

    // ── 1. P1-17（SpawnCount 透传）：ConsumeOutOfCombatBatchRequests 与 EncounterStepEngine ──

    [Fact]
    public void P1_17_RerollSpawnCount_OverridesHourlyAndShelfDealtCounts()
    {
        var (run, engine) = CreateRun(day: 2, hour: 1, state: ERunState.Choice);
        var batch = new OutOfCombatBatchResult();
        batch.RerollRequests.Add("AllEncounters");
        batch.RerollEntries.Add(("AllEncounters", 2));

        var events = new List<IGameSimEvent>();
        InvokePrivate(engine, "ConsumeOutOfCombatBatchRequests", batch, events);

        Assert.Equal(2, run.SelectionSet.Count);
        Assert.Equal(2, events.OfType<GameSimEventCardDealt>().Count());

        // 商店货架重随透传 SpawnCount=1
        var shop = new ShopEngine(run, Pool.Value, Repo.Value);
        var shelfEvents = shop.DealEncounterShelf(
            PolShopId,
            isReroll: true,
            playerState: null,
            ownedMaxTiers: null,
            customRules: run.CustomRules,
            archetypeIndex: null,
            applySyntheticDiscounts: true,
            spawnCountOverride: 1);
        Assert.Single(run.SelectionSet);
        Assert.Single(shelfEvents.OfType<GameSimEventCardDealt>());
    }

    // ── 2. P2-04：PvE 战败扣声望 + NumCarpetSocketsUnlockPerLevel 下限放开至 1 ──

    [Fact]
    public void P2_04_PveDefeatPrestigeLoss_AndSingleSocketUnlockPerLevel()
    {
        var customMode = new GameModeConfig
        {
            NumberOfDays = Mode.Value.NumberOfDays,
            HoursInADay = Mode.Value.HoursInADay,
            StartingHealth = 10,
            StartingGold = Mode.Value.StartingGold,
            StartingIncome = Mode.Value.StartingIncome,
            StartingExperience = 0,
            ExperiencePerLevel = 4,
            ExperiencePerHour = 1,
            PrestigeInitial = 20,
            PrestigeMax = 20,
            PrestigeLossFromMonsters = 3,
            NumCarpetSocketsUnlockPerLevel = 1,
            FatesTemplateId = Mode.Value.FatesTemplateId,
            FatesSpawnAtOrBelowPrestige = 0,
            FatesAfterPrestige = 1,
        };

        var (run, engine) = CreateRun(day: 1, hour: 3, state: ERunState.Choice, healthMax: 10, modeOverride: customMode);

        // 1) 单级解锁槽位下限放开至 1：初始 4 格（0b00_0111_1000），升级 1 次仅解锁 1 格变为 5 格
        var xpEvents = new List<IGameSimEvent>();
        InvokePrivate(engine, "GrantExperienceToList", xpEvents, 4, EExperienceType.Hour);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        var unlockEv = Assert.Single(xpEvents.OfType<GameSimEventSocketsUnlocked>());
        Assert.Single(unlockEv.UnlockedSockets);

        // 2) 直接 +Level 同样遵循下限 1
        var p1 = new PlayerState("p", "P", "Vanessa", 200, 20, 0, 10);
        var lvlBatch = new OutOfCombatBatchResult();
        lvlBatch.PlayerAttributeChanges.Add(new OutOfCombatPlayerAttributeChange
        {
            Attribute = "Level",
            PreviousValue = 2,
            CurrentValue = 3,
        });
        var directEvents = new List<IGameSimEvent>();
        InvokePrivate(engine, "ApplyOutOfCombatBatch", p1, lvlBatch, directEvents, null, true, EExperienceType.Item);
        var unlockEv2 = Assert.Single(directEvents.OfType<GameSimEventSocketsUnlocked>());
        Assert.Single(unlockEv2.UnlockedSockets);

        // 3) PvE 战败（空手 10HP 打野怪）按 PrestigeLossFromMonsters=3 扣除声望
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 10;
        run.PlayerAttributes[EPlayerAttributeType.Health] = 10;
        run.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
        run.PendingLevelUps = 0;
        var mobEntry = new RunState.CardEntry
        {
            InstanceId = "mob_p204",
            TemplateId = Guid.Parse(FangedIngletCombatId),
            Type = ECardType.CombatEncounter,
            Tier = ETier.Bronze,
            Owner = null,
        };
        run.Cards[mobEntry.InstanceId] = mobEntry;
        run.SelectionSet.Clear();
        run.SelectionSet.Add(mobEntry.InstanceId);

        var combatOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobEntry.InstanceId) });
        Assert.Null(combatOut.Error);
        Assert.NotNull(combatOut.Combat);
        Assert.Equal(ECombatantId.Opponent, combatOut.Combat!.Winner);
        Assert.Equal(17, run.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.Contains(combatOut.CombatDespawnSim!.Events, e => e is GameSimEventPlayerPrestigeChanged pc && pc.Delta == -3);
    }

    // ── 3. P2-06：数组兼容 SelectionRequirements 与 TSelectionRequirement.Consumes 消耗销毁 ──

    [Fact]
    public void P2_06_SelectionRequirementsArray_AndConsumes_ValidatesAndDestroysTargetCard()
    {
        const string stepId = "99999999-4444-4444-8444-444444440006";
        var consumesJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($$"""
        {
          "Consumes": {
            "$type": "TTargetCardSection",
            "TargetSection": "SelfHand",
            "Conditions": {
              "$type": "TCardConditionalId",
              "Id": "{{CutlassItemId}}",
              "IsNot": false
            }
          }
        }
        """)!;
        var reqNode = new RuleNode
        {
            Type = "TSelectionRequirement",
            CombinedSelectionRequirements =
            [
                new RuleNode
                {
                    Type = "TSelectionRequirement",
                    Prerequisites =
                    [
                        new RuleNode
                        {
                            Type = "TPrerequisitePlayer",
                            AttributeType = "Gold",
                            ComparisonOperator = "GreaterThanOrEqual",
                            RawComparisonValueElement = JsonSerializer.SerializeToElement(5.0),
                        }
                    ],
                    ExtensionData = consumesJson,
                }
            ],
        };

        var stepTpl = new BazaarCore.Models.CardTemplate
        {
            Id = stepId,
            Type = "EncounterStep",
            InternalName = "P2-06 Consumes Test Step",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = ["Common"],
            SpawningEligibility = "Always",
            ShouldProgressHour = false,
            SelectionRequirements = reqNode,
        };
        var patchedRepo = CloneRepoWithOverrides(Repo.Value, new Dictionary<string, BazaarCore.Models.CardTemplate>
        {
            [stepId] = stepTpl,
        });

        var (run, _) = CreateRun(gold: 20, state: ERunState.Encounter, repoOverride: patchedRepo);
        var shop = new ShopEngine(run, Pool.Value, patchedRepo);
        var stepEngine = new EncounterStepEngine(run, Pool.Value, patchedRepo, shop, new Random(42));

        var stepEntry = new RunState.CardEntry
        {
            InstanceId = "stp_p206",
            TemplateId = Guid.Parse(stepId),
            Type = ECardType.EncounterStep,
            Tier = ETier.Bronze,
            Owner = null,
        };
        run.Cards[stepEntry.InstanceId] = stepEntry;
        run.SelectionSet.Add(stepEntry.InstanceId);

        // 1) 未持有消耗卡 Cutlass -> 校验拒绝
        var failRes = stepEngine.ExecuteStep(stepEntry);
        Assert.False(failRes.Success);

        // 2) 持有消耗卡 Cutlass -> 校验通过并销毁消耗卡，发射 CardDisposed
        var ticketCard = AddOwnedItem(run, CutlassItemId, EInventorySection.Hand, EContainerSocketId.Socket_3, ETier.Bronze, patchedRepo);
        var okRes = stepEngine.ExecuteStep(stepEntry);
        Assert.True(okRes.Success, okRes.ErrorMessage);
        Assert.DoesNotContain(ticketCard.InstanceId, run.Cards.Keys);
        Assert.Contains(okRes.Events, e => e is GameSimEventCardDisposed d && d.InstanceId == ticketCard.InstanceId);
    }

    // ── 4. P2-07：局外单卡/声望直接修改按需补发 GameSimEventEffectExecuted（不填 GameSim.VfxKeys）──

    [Fact]
    public void P2_07_OutOfCombatDirectMutation_EmitsEffectExecutedWithoutPopulatingVfxKeys()
    {
        var (run, engine) = CreateRun(state: ERunState.Encounter);
        var item = AddOwnedItem(run, CutlassItemId, EInventorySection.Hand, EContainerSocketId.Socket_3, ETier.Bronze);

        var ps = new PlayerState("p", "P", "Vanessa", 400, 20, 50, 10);
        ps.Hand.TryPlaceOrShift(item.CoreCard!, 3);
        var batch = new OutOfCombatBatchResult();
        batch.CardAttributeChanges.Add(new OutOfCombatCardAttributeChange
        {
            Card = item.CoreCard!,
            Attribute = "DamageAmount",
            PreviousValue = 10,
            CurrentValue = 15,
        });

        var events = new List<IGameSimEvent>();
        // syncBoardDiffs=false 场景无光环刷新事件，必须按需补发 GameSimEventEffectExecuted(EffectTargetCard)
        InvokePrivate(engine, "ApplyOutOfCombatBatch", ps, batch, events, null, false, EExperienceType.Item);

        Assert.Contains(events, e =>
            e is GameSimEventEffectExecuted eff &&
            eff.Target is EffectTargetCard etc &&
            etc.Target.Value == item.InstanceId);

        var sim = run.BuildFullGameSim(events);
        Assert.True(sim.VfxKeys == null || sim.VfxKeys.Count == 0);
    }

    // ── 5. P2-08：GameSimEventPlayerExperienceGained 透传 SourceInstanceId ──

    [Fact]
    public void P2_08_EncounterStepExperience_EmitsSourceInstanceId()
    {
        const string stepId = "99999999-4444-4444-8444-444444440008";
        var stepTpl = new BazaarCore.Models.CardTemplate
        {
            Id = stepId,
            Type = "EncounterStep",
            InternalName = "P2-08 XP Source Step",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = ["Common"],
            SpawningEligibility = "Always",
            ShouldProgressHour = false,
            ExperienceAwardUponSelection = 2,
        };
        var patchedRepo = CloneRepoWithOverrides(Repo.Value, new Dictionary<string, BazaarCore.Models.CardTemplate>
        {
            [stepId] = stepTpl,
        });

        var (run, engine) = CreateRun(state: ERunState.Encounter, repoOverride: patchedRepo);
        var stepEntry = new RunState.CardEntry
        {
            InstanceId = "stp_xp_src_8",
            TemplateId = Guid.Parse(stepId),
            Type = ECardType.EncounterStep,
            Tier = ETier.Bronze,
            Owner = null,
        };
        run.Cards[stepEntry.InstanceId] = stepEntry;
        run.SelectionSet.Add(stepEntry.InstanceId);

        var pickOut = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(stepEntry.InstanceId) });
        Assert.Null(pickOut.Error);
        var xpEv = Assert.Single(pickOut.Events.OfType<GameSimEventPlayerExperienceGained>());
        Assert.Equal(2u, xpEv.Amount);
        Assert.Equal(stepEntry.InstanceId, xpEv.SourceInstanceId);
    }

    // ── 6. P2-11：全量 NetMessageError 非空 MessageId、RateLimited 及锦标赛精确英文等待文案 ──

    [Fact]
    public void P2_11_NetMessageErrors_HaveNonEmptyMessageIds_AndExactTournamentWaitText()
    {
        var (_, engine) = CreateRun(state: ERunState.Choice);
        var notFound = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId("missing_card_id") });
        Assert.NotNull(notFound.Error);
        Assert.False(string.IsNullOrWhiteSpace(notFound.Error!.MessageId));

        var rateLimited = GameServerEndpoints.CreateRateLimitedError();
        Assert.Equal("RateLimited", rateLimited.MessageId);

        // 构造双人锦标赛未就位同步锁场景，验证精确英文白名单文案与非空 MessageId
        string dbPath = Path.Combine(Path.GetTempPath(), $"p211_wait_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Persistence(dbPath);
            using var accounts = new AccountStore(db);
            var acc1 = accounts.GetOrCreateByName("TourneyWaitP1");
            var acc2 = accounts.GetOrCreateByName("TourneyWaitP2");

            var lobbies = new LobbyStore(db);
            var lobby = lobbies.Create(acc1, "WaitTest", "Tournament", playerCap: 2, isPublic: false, rules: new CustomRunRuleSet());
            lobbies.Join(lobby.Code, acc2);
            var tsid = Guid.NewGuid();
            lobby.TournamentSessionId = tsid;
            lobby.HasStarted = true;

            var (tRun, tEngine) = CreateRun(
                day: 1,
                hour: 6,
                state: ERunState.Choice,
                lobbies: lobbies,
                accountId: acc1.AccountId.ToString("N"),
                tournamentSessionId: tsid);
            lobbies.RecordRunProgress(lobby, acc1.AccountId.ToString("N"), tRun);

            var pvpEntry = new RunState.CardEntry
            {
                InstanceId = "pvp_wait_1",
                TemplateId = Guid.Parse(ShopEngine.Hour6PvpCardId),
                Type = ECardType.PvpEncounter,
                Tier = ETier.Bronze,
                Owner = null,
            };
            tRun.Cards[pvpEntry.InstanceId] = pvpEntry;
            tRun.SelectionSet.Add(pvpEntry.InstanceId);

            var waitOut = tEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpEntry.InstanceId) });
            Assert.NotNull(waitOut.Error);
            Assert.False(string.IsNullOrWhiteSpace(waitOut.Error!.MessageId));
            Assert.Equal("Waiting for the remaining tournament players to finish PvP.", waitOut.Error.Message);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
            try { if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal"); } catch { }
            try { if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm"); } catch { }
        }
    }

    // ── 7. P2-12：技能购买/融合规范 LeftSocketId=null/Section=null 且移除冗余 CardMoved ──

    [Fact]
    public void P2_12_SkillPurchaseAndFusion_NormalizeNullSocketSection_AndOmitCardMoved()
    {
        var (run, engine) = CreateRun(gold: 100, state: ERunState.Encounter);
        var shop = new ShopEngine(run, Pool.Value, Repo.Value);
        shop.DealStarterSkills(count: 3);
        string sk1Id = run.SelectionSet[0];
        var sk1TplId = run.Cards[sk1Id].TemplateId;
        var sk1Tier = run.Cards[sk1Id].Tier;

        // 1) 普通技能购买
        var buyOut = engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(sk1Id) });
        Assert.Null(buyOut.Error);
        var buyEv = Assert.Single(buyOut.Events.OfType<GameSimEventCardPurchased>(), e => e.InstanceId == sk1Id);
        Assert.Null(buyEv.LeftSocketId);
        Assert.Null(buyEv.Section);
        Assert.Equal(ECombatantId.Player, buyEv.CombatantId);
        Assert.DoesNotContain(buyOut.Events.OfType<GameSimEventCardMoved>(), e => e.InstanceId == sk1Id);
        Assert.Contains(sk1Id, buyOut.DirtyCardIds);
        Assert.Contains(buyOut.Events.OfType<GameSimEventPlayerSkillEquipped>(), e => e.InstanceId == sk1Id);

        // 2) 同模板同阶技能融合
        run.State = ERunState.Encounter;
        run.SelectionRules = new TSelectionContextRules { CanSelectMultiple = true, SelectionIsFree = true, CanExit = true };
        var dupSkill = new RunState.CardEntry
        {
            InstanceId = "skl_fuse_src",
            TemplateId = sk1TplId,
            Type = ECardType.Skill,
            Tier = sk1Tier,
            Owner = null,
            IsFreeCandidate = true,
            CoreCard = new CardInstance(Repo.Value.GetCard(sk1TplId.ToString())!, ShopEngine.ToCoreTier(sk1Tier), null, "skl_fuse_src"),
        };
        run.Cards[dupSkill.InstanceId] = dupSkill;
        run.SelectionSet.Clear();
        run.SelectionSet.Add(dupSkill.InstanceId);

        var fuseOut = engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(dupSkill.InstanceId) });
        Assert.Null(fuseOut.Error);
        var fuseBuyEv = Assert.Single(fuseOut.Events.OfType<GameSimEventCardPurchased>(), e => e.InstanceId == dupSkill.InstanceId);
        Assert.Null(fuseBuyEv.LeftSocketId);
        Assert.Null(fuseBuyEv.Section);
        Assert.Equal(ECombatantId.Player, fuseBuyEv.CombatantId);
        Assert.DoesNotContain(fuseOut.Events.OfType<GameSimEventCardMoved>(), e => e.InstanceId == dupSkill.InstanceId);
    }

    // ── 8. P2-14：RerollCost 统一收口并自动发射 RerollCostChanged ──

    [Fact]
    public void P2_14_SetRerollCostAndAuraRefresh_EmitsRerollCostChanged()
    {
        var (run, _) = CreateRun(state: ERunState.Encounter);
        run.SelectionRules = new TSelectionContextRules
        {
            CanExit = true,
            RerollRules = new RerollRules { StartingCost = 3, CostIncrease = 1, TotalAllowedRerolls = 2 },
        };
        run.BaseRerollCost = 3;
        run.RerollCost = 3;
        run.RerollsRemaining = 2;

        var shop = new ShopEngine(run, Pool.Value, Repo.Value);
        var events = new List<IGameSimEvent>();

        // 无变化时不重复发事件
        Assert.False(shop.RefreshEffectiveRerollCost(events));
        Assert.Empty(events);

        // 光环降费 -2 后自动发射 GameSimEventRunRerollCostChanged(1)
        run.PlayerAuraAdd["RerollCostModifier"] = -2;
        Assert.True(shop.RefreshEffectiveRerollCost(events));
        Assert.Equal(1u, run.RerollCost);
        var rcEv = Assert.Single(events.OfType<GameSimEventRunRerollCostChanged>());
        Assert.Equal(1u, rcEv.NewCost);
    }

    // ── 9. P2-16：Transform 祭坛补齐 Transform.Abilities / behavior.Abilities / OnCardTransformed 且无残留旧 DirtyCardId ──

    [Fact]
    public void P2_16_TransformPedestal_RunsAbilities_DispatchesTransform_AndOmitsDeletedOldDirtyId()
    {
        const string srcItemId = "99999999-4444-4444-8444-444444440016";
        const string pedId = "99999999-4444-4444-8444-444444440116";

        // 源卡带 Transform.Abilities（变形时玩家 +7 Gold）
        var srcTpl = new BazaarCore.Models.CardTemplate
        {
            Id = srcItemId,
            Type = "Item",
            InternalName = "P2-16 Reagent Source",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = ["Vanessa"],
            Tags = ["Reagent"],
            SpawningEligibility = "Always",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new() { Attributes = new Dictionary<string, double> { ["BuyPrice"] = 2, ["SellPrice"] = 1 } },
            },
            Transform = new TransformDefinition
            {
                Abilities = new Dictionary<string, AbilityDefinition>
                {
                    ["0"] = new()
                    {
                        Id = "0",
                        Action = new RuleNode
                        {
                            Type = "TActionPlayerModifyAttribute",
                            AttributeType = "Gold",
                            Operation = "Add",
                            RawValueNode = JsonSerializer.SerializeToElement(7.0),
                            TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        },
                    },
                },
            },
        };

        // 变形祭坛带 behavior.Abilities（额外 +50 HealthMax）
        var pedTpl = new BazaarCore.Models.CardTemplate
        {
            Id = pedId,
            Type = "PedestalEncounter",
            InternalName = "P2-16 Custom Transform Pedestal",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = ["Common"],
            SpawningEligibility = "Always",
            Behavior = new RuleNode
            {
                Type = "TPedestalBehaviorTransform",
                SpawnContext = new RuleNode
                {
                    Type = "TSpawnContextQuery",
                    SelectionMethod = "Random",
                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement(1.0) },
                    Groups =
                    [
                        new RuleNode
                        {
                            Type = "TSpawnGroup",
                            Filters =
                            [
                                new RuleNode
                                {
                                    Type = "TSpawnFilterIdList",
                                    Ids = [CutlassItemId],
                                }
                            ],
                        }
                    ],
                },
                Abilities = new Dictionary<string, AbilityDefinition>
                {
                    ["0"] = new()
                    {
                        Id = "0",
                        Action = new RuleNode
                        {
                            Type = "TActionPlayerModifyAttribute",
                            AttributeType = "HealthMax",
                            Operation = "Add",
                            RawValueNode = JsonSerializer.SerializeToElement(50.0),
                            TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        },
                    },
                },
            },
        };

        var patchedRepo = CloneRepoWithOverrides(Repo.Value, new Dictionary<string, BazaarCore.Models.CardTemplate>
        {
            [srcItemId] = srcTpl,
            [pedId] = pedTpl,
        });

        var (run, engine) = CreateRun(gold: 10, healthMax: 300, state: ERunState.Pedestal, repoOverride: patchedRepo);
        run.CurrentEncounterId = pedId;
        run.SelectionRules = new TSelectionContextRules { CanExit = true };
        var oldEntry = AddOwnedItem(run, srcItemId, EInventorySection.Hand, EContainerSocketId.Socket_3, ETier.Bronze, patchedRepo);
        string oldId = oldEntry.InstanceId;

        var commitOut = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(oldId) });
        Assert.Null(commitOut.Error);
        Assert.DoesNotContain(oldId, run.Cards.Keys);
        // 旧卡 ID 绝不可残留于 DirtyCardIds
        Assert.DoesNotContain(oldId, commitOut.DirtyCardIds);
        var trEv = Assert.Single(commitOut.Events.OfType<GameSimEventCardTransformed>());
        Assert.Equal(oldId, trEv.OriginalInstanceId);
        string newId = trEv.TransformedCards[0].InstanceId;
        Assert.Contains(newId, commitOut.DirtyCardIds);
        Assert.Equal(17, run.PlayerAttributes[EPlayerAttributeType.Gold]);       // Transform.Abilities +7 Gold
        Assert.Equal(350, run.PlayerAttributes[EPlayerAttributeType.HealthMax]); // behavior.Abilities +50 HealthMax
        Assert.Equal(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_9, 0));
    }

    // ── 10. P2-18：EncounterStepEngine SelectionSet 动作读取真实玩家状态且不再受 4 动作白名单拦截 ──

    [Fact]
    public void P2_18_EncounterStepSelectionSet_UsesRealSelfPlayerAndSupportsNonWhitelistedActions()
    {
        const string stepId = "99999999-4444-4444-8444-444444440018";
        var stepTpl = new BazaarCore.Models.CardTemplate
        {
            Id = stepId,
            Type = "EncounterStep",
            InternalName = "P2-18 Real Self SelectionSet Step",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = ["Common"],
            SpawningEligibility = "Always",
            ShouldProgressHour = false,
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                // 动作 1：用真实玩家 Gold（33）作为数值给货架卡加 DamageAmount（旧虚拟空壳 shelfState 读不到真实 Gold=33！）
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSelected" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardModifyAttribute",
                        AttributeType = "DamageAmount",
                        Operation = "Add",
                        RawValueNode = JsonSerializer.SerializeToElement(new RuleNode
                        {
                            Type = "TReferenceValuePlayerAttribute",
                            AttributeType = "Gold",
                            Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        }, GameDatabaseLoader.JsonOptions),
                        Target = new RuleNode
                        {
                            Type = "TTargetCardSection",
                            TargetSection = "SelectionSet",
                            TargetMode = "All",
                        },
                    },
                },
                // 动作 2：超出旧 4 动作白名单的 TActionCardAddTagsList 作用于 SelectionSet
                ["1"] = new()
                {
                    Id = "1",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSelected" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardAddTagsList",
                        Tags = ["Relic"],
                        Target = new RuleNode
                        {
                            Type = "TTargetCardSection",
                            TargetSection = "SelectionSet",
                            TargetMode = "All",
                        },
                    },
                },
            },
        };
        var patchedRepo = CloneRepoWithOverrides(Repo.Value, new Dictionary<string, BazaarCore.Models.CardTemplate>
        {
            [stepId] = stepTpl,
        });

        var (run, _) = CreateRun(gold: 33, state: ERunState.Encounter, repoOverride: patchedRepo);
        var shop = new ShopEngine(run, Pool.Value, patchedRepo);
        var stepEngine = new EncounterStepEngine(run, Pool.Value, patchedRepo, shop, new Random(7));

        var shelfCore = new CardInstance(patchedRepo.GetCard(CutlassItemId)!, CardTier.Bronze, null, "shf_p218");
        int baseDmg = (int)Math.Round(shelfCore.GetAttribute("DamageAmount"));
        var shelfEntry = new RunState.CardEntry
        {
            InstanceId = "shf_p218",
            TemplateId = Guid.Parse(CutlassItemId),
            Type = ECardType.Item,
            Tier = ETier.Bronze,
            Owner = null,
            CoreCard = shelfCore,
        };
        run.SyncEntryFromCoreCardAndDiff(shelfEntry, patchedRepo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[shelfEntry.InstanceId] = shelfEntry;
        run.SelectionSet.Add(shelfEntry.InstanceId);

        var stepEntry = new RunState.CardEntry
        {
            InstanceId = "stp_p218",
            TemplateId = Guid.Parse(stepId),
            Type = ECardType.EncounterStep,
            Tier = ETier.Bronze,
            Owner = null,
        };
        run.Cards[stepEntry.InstanceId] = stepEntry;

        var res = stepEngine.ExecuteStep(stepEntry);
        Assert.True(res.Success, res.ErrorMessage);
        Assert.Equal(baseDmg + 33, (int)Math.Round(shelfCore.GetAttribute("DamageAmount")));
        Assert.Contains("Relic", shelfCore.ExtraTags, StringComparer.OrdinalIgnoreCase);
    }

    // ── 11. P2-26：SpawningChance 不降权普通池 + Takeover 递增表 + EncounterSpawnTierPercentages 前中期缩放 ──

    [Fact]
    public void P2_26_RegularPoolEqualWeight_TakeoverIncrementPersists_AndTierWeightsScaleWithConfig()
    {
        // 1) 普通池中 SpawningChance=0.1 的卡不应被降权 10 倍，与普通卡 1:1 接近
        var lowChanceCard = new CardPool.CardTemplate { Id = "c_low", Type = "EventEncounter", SpawningChance = 0.1 };
        var normalCard = new CardPool.CardTemplate { Id = "c_norm", Type = "EventEncounter", SpawningChance = 0.0 };
        var rng = new Random(12345);
        int lowHits = 0;
        for (int i = 0; i < 600; i++)
        {
            var picked = Pool.Value.RollWeightedEncounter([lowChanceCard, normalCard], null, null, rng);
            if (picked?.Id == "c_low") lowHits++;
        }
        Assert.InRange(lowHits, 240, 360);

        // 2) Takeover 独立掷骰递增表（未命中递增、命中清零，且随快照往返持久化）
        var (run, _) = CreateRun();
        var shop = new ShopEngine(run, Pool.Value, Repo.Value);
        var takeoverTpl = new CardPool.CardTemplate
        {
            Id = "tk_inc_test",
            Type = "EventEncounter",
            IsTakeover = true,
            SpawningChance = 0.0,
            SpawningChanceIncrement = 0.25,
        };
        bool hit1 = shop.RollTakeoverChanceWithIncrement(takeoverTpl, defaultBaseChance: 0.0, mult: 1.0, new Random(1));
        Assert.False(hit1);
        Assert.Equal(0.25, run.TakeoverChanceBonuses["tk_inc_test"], 6);

        string snap = RunSnapshotSerializer.Serialize(run);
        var restored = RunSnapshotSerializer.Deserialize(snap, Repo.Value);
        Assert.Equal(0.25, restored.TakeoverChanceBonuses["tk_inc_test"], 6);

        // 3) EncounterSpawnTierPercentages 改动时，Day 1–6 前中期权重按比例缩放
        var baseGm = Repo.Value.GameMode;
        var scaledGm = new GameModeData
        {
            Id = baseGm.Id,
            NumberOfDays = baseGm.NumberOfDays,
            HoursInADay = baseGm.HoursInADay,
            StandardPrices = baseGm.StandardPrices,
            EncounterSpawnTierPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = 0.4,
                ["Silver"] = 0.6, // 相对标准 0.3 翻倍
                ["Gold"] = 0.2,
                ["Diamond"] = 0.1,
            },
        };
        var scaledRepo = CloneRepoWithOverrides(Repo.Value, gameModeOverride: scaledGm);
        var shopBase = new ShopEngine(run, Pool.Value, Repo.Value);
        var shopScaled = new ShopEngine(run, Pool.Value, scaledRepo);
        double wBaseD1Silver = shopBase.GetEncounterTierBaseWeight(1, ETier.Silver, isMerchantPool: false);
        double wScaledD1Silver = shopScaled.GetEncounterTierBaseWeight(1, ETier.Silver, isMerchantPool: false);
        Assert.Equal(wBaseD1Silver * 2.0, wScaledD1Silver, 6);
    }

    // ── 12. P2-27：ZurphinsSafari GUID 修正 + 全仓硬编码遭遇 GUID 存在性校验 + GetEffectiveValidAssetIds ──

    [Fact]
    public void P2_27_ZurphinsSafariFixed_AllHardcodedEncounterGuidsExistInDb_AndCustomIdsIncluded()
    {
        Assert.Equal("b81a1c81-0a16-462a-8247-04653354b79d", CardArchetypeIndex.ZurphinsSafariEncounterId);
        Assert.NotNull(Repo.Value.GetCard(CardArchetypeIndex.ZurphinsSafariEncounterId));

        // 扫描 CardArchetypeIndex 与 ShopEngine 中所有硬编码遭遇/基座 GUID 常量，确保 100% 存在于 GameData.db
        var typesToScan = new[] { typeof(CardArchetypeIndex), typeof(ShopEngine) };
        int checkedCount = 0;
        foreach (var t in typesToScan)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!f.IsLiteral || f.FieldType != typeof(string)) continue;
                string? val = f.GetRawConstantValue() as string;
                if (string.IsNullOrWhiteSpace(val) || !Guid.TryParse(val, out _)) continue;
                checkedCount++;
                Assert.True(
                    Repo.Value.GetCard(val) != null || Pool.Value.Get(val) != null,
                    $"硬编码 GUID 常量 {t.Name}.{f.Name} = {val} 在 GameData.db 中不存在！");
            }
        }
        Assert.True(checkedCount >= 15, $"应扫描至少 15 个硬编码 GUID 常量，实际 {checkedCount}");

        var effectiveValid = Pool.Value.GetEffectiveValidAssetIds();
        Assert.NotNull(effectiveValid);
        Assert.NotEmpty(effectiveValid!);
    }

    // ── 13. P2-28：人工单局唯一基座可配置 + 回退链优先放宽 allowExceedDayCap 保留 visitedSingleSpawn ──

    [Fact]
    public void P2_28_ArtificialSingleSpawnPedestalsConfigurable_AndFallbackRespectsVisitedSingleSpawn()
    {
        var b1b2Tpl = Pool.Value.Get(CardPool.PedestalB1B2Id)!;
        var artistTpl = Pool.Value.Get(CardPool.PedestalTheArtistId)!;
        bool origFlag = ShopEngine.EnforceArtificialSingleSpawnPedestals;
        try
        {
            ShopEngine.EnforceArtificialSingleSpawnPedestals = false;
            Assert.False(ShopEngine.IsEffectiveSingleSpawnEncounter(b1b2Tpl));
            Assert.True(ShopEngine.IsEffectiveSingleSpawnEncounter(artistTpl));

            ShopEngine.EnforceArtificialSingleSpawnPedestals = true;
            Assert.True(ShopEngine.IsEffectiveSingleSpawnEncounter(b1b2Tpl));
        }
        finally
        {
            ShopEngine.EnforceArtificialSingleSpawnPedestals = origFlag;
        }

        // 回退链保留 visitedSingleSpawn：仅含 1 张已访问 SingleSpawn + 1 张超当日阶级常规卡时，应先放宽 allowExceedDayCap 抽高阶常规卡而非穿墙重出已访问 SingleSpawn
        var (run, _) = CreateRun(day: 1, hour: 1);
        var visitedSingle = new CardPool.CardTemplate { Id = "ss_visited", Type = "EventEncounter", StartingTier = "Bronze", IsSingleSpawn = true };
        var higherRegular = new CardPool.CardTemplate { Id = "reg_gold", Type = "EventEncounter", StartingTier = "Gold", IsSingleSpawn = false };
        run.VisitedSingleSpawnEncounterIds.Add(visitedSingle.Id);

        var pickedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shop = new ShopEngine(run, Pool.Value, Repo.Value);
        var m = typeof(ShopEngine).GetMethod("RollDayTieredEncounterWithDailyDedup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var strict = m.Invoke(shop, new object?[] { new List<CardPool.CardTemplate> { visitedSingle, higherRegular }, ETier.Bronze, pickedIds, run.VisitedSingleSpawnEncounterIds, null, false, true, null, null, null, 1, false });
        Assert.Null(strict);
        var relaxedCap = (CardPool.CardTemplate?)m.Invoke(shop, new object?[] { new List<CardPool.CardTemplate> { visitedSingle, higherRegular }, ETier.Bronze, pickedIds, run.VisitedSingleSpawnEncounterIds, null, true, true, null, null, null, 1, false });
        Assert.NotNull(relaxedCap);
        Assert.Equal("reg_gold", relaxedCap!.Id);
    }

    // ── 14. P2-29：Patch 20.2 Day 1 官方 10 名银商放行（Valpak/Herma/Nautica/Colt/Eli/Tinker 等），Curio/Midsworth/Quixel/Barkun/Silvia/Orion 延迟解锁 ──

    [Fact]
    public void P2_29_Day1AllowsEarlyNeutralSilverMerchants_AndBlocksSpecialtySilverMerchants()
    {
        var midsworth = Pool.Value.Get(MidsworthId)!;
        var curio = Pool.Value.Get(CurioId)!;
        var nautica = Pool.Value.Get(NauticaId)!;
        var colt = Pool.Value.Get(ColtId)!;
        var kevs = Pool.Value.Get(KevsArmoryId)!;

        // Patch 7.0 + Patch 9.2 + BazaarDB 21.3 万局实测：
        // Nautica / Colt 等 10 名银商自 Day 1 起开放；Curio（9.2 移出 Day 1，限 D2–5）、Midsworth（7.0 改 Day 2+）、Kev's Armory（Day 2+）在 Day 1 不刷出
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(nautica, day: 1, hour: 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(colt, day: 1, hour: 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(midsworth, day: 1, hour: 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(curio, day: 1, hour: 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(kevs, day: 1, hour: 1));

        // Day 2 正常解锁 Midsworth / Curio / Kev's Armory；Day 6 起 Curio 封顶关闭
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(midsworth, day: 2, hour: 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(curio, day: 2, hour: 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(kevs, day: 2, hour: 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(curio, day: 6, hour: 1));
    }

    // ── 15. P2-30（CardPool Day 12 平滑）+ P2-31（Stelle/Dooley 前 6 天非商池核验）──

    [Fact]
    public void P2_30_And_P2_31_CardPoolDay12DiamondSmoothed_AndEarlyHeroNonMerchantCoverage()
    {
        // Patch 20.2 标准 Base GameMode（NumberOfDays=10）：Day 10+ 收敛于 BazaarDB 实测稳态 (0 / 0.20 / 0.70 / 0.10)
        var stdTable = TierChances.FromGameModeMap(Repo.Value.GameMode.ItemSkillSpawnTierPercantagesByDay);
        var stdChances = new TierChances(stdTable);
        var d12Std = stdChances.GetWeightsForDay(12);
        Assert.Equal(0.0, d12Std.Bronze, 6);
        Assert.Equal(0.20, d12Std.Silver, 6);
        Assert.Equal(0.70, d12Std.Gold, 6);
        Assert.Equal(0.10, d12Std.Diamond, 6);

        // 自定义 Mode 显式放开 clampMaxDay=20 时：读取完整高天表且保留 P2-30 Day 12 (0.80 -> Diamond=0.25) 防呆
        var extTable = TierChances.FromGameModeMap(Repo.Value.GameMode.ItemSkillSpawnTierPercantagesByDay, clampMaxDay: 20);
        var extChances = new TierChances(extTable);
        var d12Ext = extChances.GetWeightsForDay(12);
        Assert.Equal(0.0, d12Ext.Bronze, 6);
        Assert.Equal(0.20, d12Ext.Silver, 6);
        Assert.Equal(0.55, d12Ext.Gold, 6);
        Assert.Equal(0.25, d12Ext.Diamond, 6);

        // P2-31：核验 Stelle 与 Dooley 在 Day 1–6 通过 P0-15 / P0-16 已具备非空常规非商遭遇覆盖
        var catalog = Pool.Value.GetGuidOnlyHourlyCatalog();
        foreach (string hero in new[] { HeroNames.Stelle, HeroNames.Dooley })
        {
            var resolvedD1To6 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int d = 1; d <= 6; d++)
            {
                foreach (var (card, _, _) in catalog.ResolveForHeroAndDay(hero, d))
                    resolvedD1To6.Add(card.Id);
            }
            Assert.True(resolvedD1To6.Count >= 10, $"{hero} Day 1-6 GuidOnly 白昼遭遇覆盖不足: {resolvedD1To6.Count}");
            Assert.NotEmpty(Pool.Value.GetHeroTakeoverEncounters(hero));
        }
    }

    // ── 16. P2-32：Pedestal NextEncounterOnExit 跳往 Encounter/Pedestal 拆分为两条独立 NetMessageGameSim ──

    [Fact]
    public void P2_32_PedestalNextEncounterOnExit_SplitsIntoTwoIndependentNetMessageGameSims()
    {
        var (run, engine) = CreateRun(day: 4, hour: 2, state: ERunState.Pedestal);
        run.CurrentEncounterId = CardPool.PedestalB1B2Id;
        run.SelectionRules = new TSelectionContextRules
        {
            CanExit = true,
            NextEncounterOnExit = Guid.Parse(PolShopId),
        };
        var item = AddOwnedItem(run, CutlassItemId, EInventorySection.Hand, EContainerSocketId.Socket_3, ETier.Bronze);

        var commitOut = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(item.InstanceId) });
        Assert.Null(commitOut.Error);
        Assert.NotNull(commitOut.PreSplitGameSim);
        Assert.NotNull(commitOut.SecondaryGameSimEvents);
        Assert.NotEmpty(commitOut.SecondaryGameSimEvents!);
        // 第一帧不含跳转至下一遭遇的 StateTransitioned，第二帧包含跳转至 PolShop 的 StateTransitioned
        Assert.DoesNotContain(commitOut.PreSplitGameSim!.Events, e => e is GameSimEventStateTransitioned);
        Assert.Contains(commitOut.SecondaryGameSimEvents!, e =>
            e is GameSimEventStateTransitioned st &&
            st.ToState == ERunState.Encounter &&
            string.Equals(st.CurrentEncounterId, PolShopId, StringComparison.OrdinalIgnoreCase));

        // 经 GameServerEndpoints.BuildCommandReply 包装后，NetMessageAggregate 内包含 2 条独立 NetMessageGameSim
        var msg = GameServerEndpoints.BuildCommandReply(
            new CommitToPedestalCommand { InstanceId = new InstanceId(item.InstanceId) },
            run,
            commitOut,
            ERunState.Pedestal,
            isOwnedSelectMove: false,
            isFirstAfterReestablish: false,
            out _,
            out _);
        var agg = Assert.IsType<NetMessageAggregate>(msg);
        var gameSims = agg.Messages.OfType<NetMessageGameSim>().ToList();
        Assert.Equal(2, gameSims.Count);
        Assert.DoesNotContain(gameSims[0].Data.Events, e => e is GameSimEventStateTransitioned);
        Assert.Contains(gameSims[1].Data.Events, e => e is GameSimEventStateTransitioned);
    }

    // ── 17. P2-33：Pedestal 恢复重建 CurrentEncounterCoreCard + Username & PlayerLoadout 完整持久化（AccountId==null 幂等）──

    [Fact]
    public void P2_33_PedestalResumeRebuildsCoreCard_AndSnapshotPersistsUsernameAndLoadoutIdempotently()
    {
        var (run, _) = CreateRun(state: ERunState.Pedestal);
        run.TendencyLocked = true;
        run.Username = "CaptainBazaar";
        run.CurrentEncounterId = CardPool.PedestalB1B2Id;
        run.CurrentPvpOpponent = new SimPvpOpponent
        {
            Name = "GhostOpp",
            Hero = EHero.Jules,
            PlayerLoadout = new BazaarCollectionLoadout
            {
                accountId = null!,
                boardId = "board_1",
                carpetId = "carpet_2",
                cardBackId = "cb_3",
                toyId = "toy_4",
                stashId = "stash_5",
                bankId = "bank_6",
                albumId = "album_7",
                heroSkinIds = ["hs_1"],
                cardSkinIds = ["cs_1"],
            },
        };

        string json1 = RunSnapshotSerializer.Serialize(run);
        var restored = RunSnapshotSerializer.Deserialize(json1, Repo.Value);
        string json2 = RunSnapshotSerializer.Serialize(restored);
        Assert.Equal(json1, json2);
        Assert.Equal("CaptainBazaar", restored.Username);
        Assert.Null(restored.CurrentPvpOpponent!.PlayerLoadout.accountId);
        Assert.Equal("board_1", restored.CurrentPvpOpponent.PlayerLoadout.boardId);
        Assert.Equal("album_7", restored.CurrentPvpOpponent.PlayerLoadout.albumId);

        // Pedestal 恢复构造重建 CurrentEncounterCoreCard
        restored.CurrentEncounterCoreCard = null;
        _ = new RunEngine(restored, Pool.Value, Monsters.Value, LevelUps.Value, Mode.Value, Repo.Value, new PvpGhostPool());
        Assert.NotNull(restored.CurrentEncounterCoreCard);
        Assert.Equal(CardPool.PedestalB1B2Id, restored.CurrentEncounterCoreCard!.Template.Id, StringComparer.OrdinalIgnoreCase);
    }

    // ── 18. P2-34：直接 +Level 派发 OnPlayerLeveledUp + 非栈式与连升成对 InterruptStateEntered/Exited ──

    [Fact]
    public void P2_34_DirectLevelGainAndMultiLevelUp_EmitPairedInterruptEvents()
    {
        var (run, engine) = CreateRun(day: 2, hour: 1, state: ERunState.Choice);
        run.PendingLevelUps = 2;

        // 1) 非栈式直接进入第 1 次 LevelUp → Entered
        var evs1 = new List<IGameSimEvent>();
        InvokePrivate(engine, "AdvanceAfterSubStateToEvents", evs1);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Single(evs1.OfType<GameSimEventInterruptStateEntered>(), e => e.EnteredState == ERunState.LevelUp && e.IsInterrupt);
        Assert.Empty(evs1.OfType<GameSimEventInterruptStateExited>());

        // 2) 连升第 2 次 LevelUp → 先 Exited 上一级，再 Entered 下一级
        var evs2 = new List<IGameSimEvent>();
        InvokePrivate(engine, "AdvanceAfterSubStateToEvents", evs2);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Single(evs2.OfType<GameSimEventInterruptStateExited>(), e => e.ExitedState == ERunState.LevelUp && e.IsInterrupt);
        Assert.Single(evs2.OfType<GameSimEventInterruptStateEntered>(), e => e.EnteredState == ERunState.LevelUp && e.IsInterrupt);

        // 3) 非栈式彻底离开 LevelUp → 补发 Exited
        var evs3 = new List<IGameSimEvent>();
        InvokePrivate(engine, "AdvanceAfterSubStateToEvents", evs3);
        Assert.NotEqual(ERunState.LevelUp, run.State);
        Assert.Single(evs3.OfType<GameSimEventInterruptStateExited>(), e => e.ExitedState == ERunState.LevelUp && e.IsInterrupt);
    }

    // ── 19. P2-35：BuildResumeEvents 清理孤儿无主卡 + SQLite 活跃局 SpecialModeId 解析 ──

    [Fact]
    public void P2_35_BuildResumeEventsPurgesOrphanOwnerlessCards_AndResolvesSqliteSpecialModeId()
    {
        var (run, _) = CreateRun(state: ERunState.Encounter);
        var validCand = new RunState.CardEntry
        {
            InstanceId = "cand_valid",
            TemplateId = Guid.Parse(CutlassItemId),
            Type = ECardType.Item,
            Owner = null,
        };
        var orphanCand = new RunState.CardEntry
        {
            InstanceId = "cand_orphan_dirty",
            TemplateId = Guid.Parse(CutlassItemId),
            Type = ECardType.Item,
            Owner = null,
        };
        run.Cards[validCand.InstanceId] = validCand;
        run.Cards[orphanCand.InstanceId] = orphanCand;
        run.SelectionSet.Add(validCand.InstanceId);

        var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(run);
        Assert.Contains(resumeEvents.OfType<GameSimEventCardDealt>(), d => d.InstanceId == validCand.InstanceId);
        Assert.DoesNotContain(resumeEvents.OfType<GameSimEventCardDealt>(), d => d.InstanceId == orphanCand.InstanceId);
        Assert.DoesNotContain(orphanCand.InstanceId, run.Cards.Keys);

        // SQLite RunRow SpecialModeId 提取（优先 RulesJson，次之 ModeId）
        var rulesJson = JsonSerializer.Serialize(new CustomRunRuleSet { SpecialModeId = ESpecialModeId.EndlessTower });
        var rowFromRules = new Persistence.RunRow(
            "r1", Guid.NewGuid(), EHero.Vanessa, EPlayMode.Unranked, Guid.Empty,
            1, 1, 0, 0, 20, 0, 5, ERunState.Choice, DateTime.UtcNow, "{}",
            ModeId: "standard", RulesJson: rulesJson);
        Assert.Equal("EndlessTower", GameServerEndpoints.ResolveRunRowSpecialModeId(rowFromRules));

        var rowFromModeId = rowFromRules with { RulesJson = "", ModeId = "boss_rush" };
        Assert.Equal("BossRush", GameServerEndpoints.ResolveRunRowSpecialModeId(rowFromModeId));
    }

    // ── 20. P2-36：无重随状态下对外 DTO 输出 RerollCost/RerollsRemaining = null，同时保留 Loot 多页免费翻页 ──

    [Fact]
    public void P2_36_WireDtosNullRerollFieldsWhenNoRerollRules_AndPreserveLootPaging()
    {
        var (run, _) = CreateRun(state: ERunState.Choice);
        run.RerollCost = 2;
        run.RerollsRemaining = 3;
        run.SelectionRules = new TSelectionContextRules { CanExit = false, RerollRules = null };

        var snap = run.BuildSnapshot();
        var full = run.BuildFullGameSim();
        var delta = run.BuildDeltaGameSim();
        Assert.Null(snap.CurrentState!.RerollCost);
        Assert.Null(snap.CurrentState.RerollsRemaining);
        Assert.Null(full.CurrentState!.RerollCost);
        Assert.Null(full.CurrentState.RerollsRemaining);
        Assert.Null(delta.CurrentState!.RerollCost);
        Assert.Null(delta.CurrentState.RerollsRemaining);

        // V14.3 ERunState.Loot 多页免费翻页必须保留 RerollCost=0u 与 RerollsRemaining=999u
        run.State = ERunState.Loot;
        run.RerollCost = 0;
        run.RerollsRemaining = ShopEngine.UnlimitedRerollsCount;
        run.MonsterLootPages.Add([new RunState.MonsterLootPageItemState(CutlassItemId, "Bronze", null, 2)]);
        run.MonsterLootPages.Add([new RunState.MonsterLootPageItemState(CutlassItemId, "Silver", null, 2)]);
        var lootFull = run.BuildFullGameSim();
        Assert.Equal(0u, lootFull.CurrentState!.RerollCost);
        Assert.Equal(ShopEngine.UnlimitedRerollsCount, lootFull.CurrentState.RerollsRemaining);
    }

    // ── 21. P2-38：价格基数统一收口至 GameMode.StandardPrices 单一信源 ──

    [Fact]
    public void P2_38_PriceForAndRunState_UseStandardPricesSingleSourceOfTruth()
    {
        var customPrices = new StandardPricesSettings
        {
            ItemBuyPrices = new Dictionary<string, Dictionary<string, int>>
            {
                ["Medium"] = new() { ["Bronze"] = 11, ["Silver"] = 22 },
            },
            ItemSellPrices = new Dictionary<string, Dictionary<string, int>>
            {
                ["Medium"] = new() { ["Bronze"] = 5, ["Silver"] = 9 },
            },
        };

        var (buy, sell) = ShopEngine.PriceFor(ECardSize.Medium, ETier.Silver, customPrices);
        Assert.Equal(22, buy);
        Assert.Equal(9, sell);

        var tpl = new BazaarCore.Models.CardTemplate
        {
            Id = "99999999-4444-4444-8444-444444440038",
            Type = "Item",
            Size = "Medium",
            StartingTier = "Silver",
        };
        var core = new CardInstance(tpl, CardTier.Silver, null, "p238_c");
        var entry = new RunState.CardEntry
        {
            InstanceId = "p238_c",
            TemplateId = Guid.Parse(tpl.Id),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Silver,
            CoreCard = core,
        };
        Assert.Equal(22, RunState.ComputeCardBuyPrice(entry, core, customPrices));
        Assert.Equal(9, RunState.ResolveAuthoritativeSellPrice(entry, core, customPrices, rules: null));
    }

    // ── 22. P2-48：买卡融合升阶前合并商店卡非价格类永久加成至 fuseTarget.CoreCard ──

    [Fact]
    public void P2_48_BuyFusion_MergesShelfPermanentModifiersIntoFuseTarget_ExcludingPriceKeys()
    {
        var (run, engine) = CreateRun(gold: 100, state: ERunState.Encounter);
        run.SelectionRules = new TSelectionContextRules { CanSelectMultiple = true, SelectionIsFree = false, CanExit = true };

        var owned = AddOwnedItem(run, CutlassItemId, EInventorySection.Hand, EContainerSocketId.Socket_3, ETier.Bronze);
        owned.CoreCard!.PermanentAdd["DamageAmount"] = 5;

        // 商店待购同名同阶卡携带非价格永久加成 +12 与 ×1.5，以及折价 BuyPrice 乘区 0.5（不得泄漏到目标卡）
        var shelfCore = new CardInstance(Repo.Value.GetCard(CutlassItemId)!, CardTier.Bronze, null, "shf_fuse_p248");
        shelfCore.PermanentAdd["DamageAmount"] = 12;
        shelfCore.PermanentMult["DamageAmount"] = 1.5;
        shelfCore.PermanentMult["BuyPrice"] = 0.5;
        var shelfEntry = new RunState.CardEntry
        {
            InstanceId = "shf_fuse_p248",
            TemplateId = Guid.Parse(CutlassItemId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(shelfCore.Template.Size),
            Tier = ETier.Bronze,
            Owner = null,
            CoreCard = shelfCore,
        };
        shelfEntry.Attributes[ECardAttributeType.BuyPrice] = 4;
        run.Cards[shelfEntry.InstanceId] = shelfEntry;
        run.SelectionSet.Add(shelfEntry.InstanceId);

        var fuseOut = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(shelfEntry.InstanceId) });
        Assert.Null(fuseOut.Error);
        Assert.Equal(ETier.Silver, owned.Tier);
        Assert.Equal(17.0, owned.CoreCard.PermanentAdd["DamageAmount"], 6);
        Assert.Equal(1.5, owned.CoreCard.PermanentMult["DamageAmount"], 6);
        Assert.False(owned.CoreCard.PermanentMult.ContainsKey("BuyPrice"));
        Assert.Contains(owned.InstanceId, fuseOut.DirtyCardIds);
    }
}
