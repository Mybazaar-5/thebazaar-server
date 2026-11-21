using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarGameShared.TempoNet.Responses;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Tournament;

namespace BazaarServer.Game;

/// <summary>
/// 完整单局服务端引擎（V4 总装版）：
///   · 多英雄官方多样化开局 → Choice 选遭遇 → Encounter 商人/奇遇步/基座 → Hour 3 野怪 PvE → Hour 6 真实 PvP 镜像池
///   · 全生命周期装配 OutOfCombatController（买卖/消耗品先触发再移除/拖拽摆盘光环实时刷新/基座/过小时与过天清理派发/进出遭遇/战后含 opponent 触发）
///   · 接入 EncounterStepEngine（分支校验/扣费/发卡/强化/变形/NextEncounterOnSelection 多步链式跳转与奇遇触发战斗）
///   · 接入 SuspendedStates 局外中断压栈恢复（绝不吞掉当前小时商店或当前小时 Choice）
///   · 接入 CombatSimulator.SyncPermanentChangesBack 战后永久回写、野怪 RewardVictory 全池掉落与战败按伤比例折算跳币
/// </summary>
public sealed class RunEngine
{
    private readonly RunState _run;
    private readonly CardPool _pool;
    private readonly ShopEngine _shop;
    private readonly EncounterStepEngine _stepEngine;
    private readonly List<Data.MonsterTemplate> _monsters;
    private readonly LevelUpTable _levelUps;
    private readonly GameModeConfig _mode;
    private readonly GameDataRepository _repo;
    private readonly PvpGhostPool _ghosts;
    private readonly CardArchetypeIndex? _archetypeIndex;
    private readonly PlayerRuleStore? _playerRules;
    private readonly LobbyStore? _lobbies;
    private readonly SessionStore? _sessions;
    private readonly Random _rng;

    // 玩家局外限时属性层（UntilEndOfHour / UntilEndOfDay），直接绑定 RunState 以便无损快照持久化
    private readonly Dictionary<string, double> _playerHourAdd;
    private readonly Dictionary<string, double> _playerHourMult;
    private readonly Dictionary<string, double> _playerDayAdd;
    private readonly Dictionary<string, double> _playerDayMult;

    private bool _runEnded;
    private Data.MonsterTemplate? _lastCombatMonster;
    private readonly List<string> _boardAuditLogs = new();

    public bool StartSelectionDone { get; private set; }
    public IReadOnlyList<string> BoardAuditLogs => _boardAuditLogs;

    public RunEngine(
        RunState run,
        CardPool pool,
        List<Data.MonsterTemplate> monsters,
        LevelUpTable levelUps,
        GameModeConfig mode,
        GameDataRepository repo,
        PvpGhostPool ghosts,
        CardArchetypeIndex? archetypeIndex = null,
        PlayerRuleStore? playerRules = null,
        LobbyStore? lobbies = null,
        SessionStore? sessions = null)
    {
        _run = run;
        _pool = pool;
        _monsters = monsters ?? new List<Data.MonsterTemplate>();
        _levelUps = levelUps ?? new LevelUpTable();
        _mode = mode ?? new GameModeConfig();
        _repo = repo;
        _ghosts = ghosts;
        _archetypeIndex = archetypeIndex;
        _playerRules = playerRules;
        _lobbies = lobbies;
        _sessions = sessions;
        if (_run.RngSeed == 0)
            _run.RngSeed = Random.Shared.Next(1, int.MaxValue);
        _rng = new Random(_run.RngSeed);
        _playerHourAdd = run.PlayerHourAdd;
        _playerHourMult = run.PlayerHourMult;
        _playerDayAdd = run.PlayerDayAdd;
        _playerDayMult = run.PlayerDayMult;
        _run.CustomRules = (_run.CustomRules ?? new CustomRunRuleSet()).CloneNormalized();
        _shop = new ShopEngine(run, pool, repo, _run.CustomRules, archetypeIndex);
        _stepEngine = new EncounterStepEngine(run, pool, repo, _shop, _rng);
        _runEnded = run.State is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown;
        StartSelectionDone = RunSnapshotSerializer.InferStartSelectionDone(run);
        if (IsTournamentRun())
            _run.PlayerAttributes[EPlayerAttributeType.Custom_11] = 1;

        // 15.22：若恢复会话意外停留于 Combat/PVPCombat，向前结算至战后状态，绝不倒退回战前 Choice
        if (!_runEnded && _run.State is ERunState.Combat or ERunState.PVPCombat)
        {
            SettleInterruptedCombatOnResume();
        }

        // Bug #3 RULE 3：恢复会话时若处于 LevelUp 但 SelectionSet 为空，立即按对应待消费目标新等级重发升级选项自愈卡死存档
        if (!_runEnded && _run.State == ERunState.LevelUp && _run.SelectionSet.Count == 0)
        {
            int healLevel = ResolveNextPendingLevelUpLevel();
            _run.SelectionsMadeInCurrentContext = 0;
            _shop.DealLevelUpChoices(healLevel, _levelUps, BuildCorePlayerFromRun());
        }

        // Bug #11：若恢复会话处于遭遇中，同步重建当前遭遇核心卡（如 Salvage Yard）
        if (!_runEnded && _run.State == ERunState.Encounter && _run.CurrentEncounterCoreCard == null &&
            !string.IsNullOrEmpty(_run.CurrentEncounterId))
        {
            _run.CurrentEncounterCoreCard = CreateEncounterCoreCard(_run.CurrentEncounterId);
        }

        if (!_runEnded)
        {
            if (_run.State is ERunState.Encounter or ERunState.Pedestal or ERunState.Loot &&
                !string.IsNullOrEmpty(_run.CurrentEncounterId) &&
                string.IsNullOrEmpty(_run.ActiveEncounterCardInstanceId))
            {
                string anchorEncId = ResolvePortraitAnchorEncounterId(_run.CurrentEncounterId);
                ActivateEncounterCard(anchorEncId, selectedCard: null, new List<IGameSimEvent>(), emitPurchaseIfSelected: false);
            }
            if (_run.State == ERunState.Choice && _run.Hour == (uint)_mode.HoursInADay)
            {
                EnsurePendingHour6PvpOpponentLocked();
            }
            ReconcileAndVerifyBoardInvariants("ResumeInit");
        }
    }

    public sealed class Outcome
    {
        public List<IGameSimEvent> Events { get; } = new();
        public HashSet<string> DirtyCardIds { get; } = new(StringComparer.Ordinal);
        public GameSim? CombatSpawnSim { get; set; }
        public CombatSim? Combat { get; set; }
        public GameSim? CombatDespawnSim { get; set; }
        public NetMessageError? Error { get; set; }
        /// <summary>为 true 时，响应里额外带一条全量 GameStateSync（用于 GM restart 彻底重置客户端）。</summary>
        public bool ForceStateSync { get; set; }
    }

    // ── 开局 ────────────────────────────────────────────────────────────────

    public Outcome StartRun()
    {
        var o = new Outcome();
        _run.CustomRules = ResolveEffectiveRulesForStart();
        _run.TendencyLocked = !_run.CustomRules.AllowTendencySelection;
        _shop.ActiveRules = _run.CustomRules;
        _shop.ArchetypeIndex = _archetypeIndex;

        _run.Day = 1;
        _run.Hour = 1;
        _run.Victories = 0;
        _run.Defeats = 0;
        _run.FatesUsed = false;
        _run.State = ERunState.Encounter;
        _run.UnlockedSlots = 0b00_0111_1000;
        _run.HandGrid.UnlockedMask = _run.UnlockedSlots;
        _run.HandGrid.Cards.Clear();
        _run.StashGrid.Cards.Clear();
        _run.Cards.Clear();
        _run.SelectionSet.Clear();
        _run.MonsterLootPages.Clear();
        _run.MonsterLootPageIndex = 0;
        _run.SuspendedStates.Clear();
        _run.PendingLevelUps = 0;
        _run.PendingEncounterExitXp = 0;
        _run.GainedHourXpThisHour = false;
        _run.GainedAdvanceHourXpThisHour = false;
        _run.PendingPvpGhost = null;
        _run.CurrentPvpOpponent = null;
        _run.SelectionsMadeInCurrentContext = 0;
        _run.CurrentEncounterCoreCard = null;
        _run.ActiveEncounterCardInstanceId = null;
        _run.ParentEncounterTemplateId = null;
        _run.VisitedSingleSpawnEncounterIds.Clear();
        _run.VisitedDayEncounterIds.Clear();
        _run.ExclusionSet.Clear();
        _run.ExclusionSetInitialized = false;
        EnsureRunExclusionSetInitialized();
        _boardAuditLogs.Clear();
        _runEnded = false;
        ClearTrackedPlayerTempModifiers(clearHour: true, clearDay: true, clearAura: true);

        _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(100, 100, gold: 0, income: 5);
        ApplyStartingStats(_run.CustomRules, o.Events);
        if (IsTournamentRun())
            _run.PlayerAttributes[EPlayerAttributeType.Custom_11] = 1;

        o.Events.Add(new GameSimEventPlayerInitialized
        {
            CombatantId = ECombatantId.Player,
            Hero = _run.Hero,
        });
        o.Events.Add(new GameSimEventPlayerInitialized
        {
            CombatantId = ECombatantId.Opponent,
            Hero = EHero.Common,
        });

        GrantStarterPresets(_run.CustomRules, o.Events);

        // 接入官方多英雄多样化开局（直接进入 Day 1 Hour 1 Choice 并传入当日去重集合）
        _run.SelectionsMadeInCurrentContext = 0;
        o.Events.AddRange(DealHourEncountersAndClearActive());
        StartSelectionDone = _run.State == ERunState.Choice;

        // 派发开局生命周期触发与当前遭遇进入/发牌钩子（Q45：先注入官方开局 PlayerEffect 以触发 Jules 冰火插座 15d06ad8 等）
        var p1Core = BuildCorePlayerFromRun();
        OutOfCombatController.InjectOfficialPlayerEffects(
            p1Core,
            _repo,
            includeTournamentMode: false,
            includeHeroRunStartSpawners: true);
        var startBatch = OutOfCombatController.OnRunStarted(p1Core, _repo, _rng);
        ApplyOutOfCombatBatch(p1Core, startBatch, o.Events, syncBoardDiffs: false);
        if (_run.State is ERunState.Encounter or ERunState.Pedestal && !string.IsNullOrEmpty(_run.CurrentEncounterId))
        {
            _run.CurrentEncounterCoreCard = CreateEncounterCoreCard(_run.CurrentEncounterId);
            ActivateEncounterCard(_run.CurrentEncounterId, selectedCard: null, o.Events, emitPurchaseIfSelected: false);
        }
        TriggerOnEncounterEnteredAndDealt(_run.CurrentEncounterId, o.Events);

        if (_run.PendingLevelUp && _run.State == ERunState.Choice)
        {
            PushChoiceAndEnterLevelUp(o.Events);
        }
        ReconcileAndVerifyBoardInvariants("StartRun", o.DirtyCardIds, o.Events);
        _run.RngSeed = _rng.Next(1, int.MaxValue);
        return o;
    }

    private CustomRunRuleSet ResolveEffectiveRulesForStart()
    {
        var lobby = _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId);
        if (lobby != null)
        {
            var resolved = lobby.Rules.CloneNormalized();
            if (resolved.AllowTendencySelection)
            {
                if (_playerRules != null && !string.IsNullOrWhiteSpace(_run.AccountId))
                    resolved.SelectedArchetypeMask = _playerRules.Get(_run.AccountId).SelectedArchetypeMask;
                else if (_run.CustomRules != null)
                    resolved.SelectedArchetypeMask = _run.CustomRules.SelectedArchetypeMask;
                resolved = resolved.CloneNormalized();
            }
            else
            {
                resolved.SelectedArchetypeMask = 0UL;
            }
            return resolved;
        }

        if (_playerRules != null && !string.IsNullOrWhiteSpace(_run.AccountId))
            return _playerRules.Get(_run.AccountId).CloneNormalized();

        return (_run.CustomRules ?? new CustomRunRuleSet()).CloneNormalized();
    }

    /// <summary>
    /// 全模式（普通 / 排位 / 锦标赛）统一的开场流派倾向可锁定窗口判定：
    /// 仅在 Day 1 Hour 1 初始 Choice 态、尚未完成任何遭遇选择且未曾锁定过时允许确认一次。
    /// </summary>
    public bool CanLockOpeningTendency =>
        !_runEnded
        && !_run.TendencyLocked
        && (_run.CustomRules?.AllowTendencySelection ?? true)
        && _run.Day == 1
        && _run.Hour == 1
        && _run.State == ERunState.Choice
        && _run.SelectionsMadeInCurrentContext == 0;

    /// <summary>
    /// 全模式统一的开场流派倾向一次性锁定接口：
    /// 确认后同步写入 ShopEngine 并永久焊死 _run.TendencyLocked = true；对局途中或二次调用一律拒绝。
    /// </summary>
    public bool TryLockOpeningTendency(ulong rawMask, out ulong lockedMask, out string? error)
    {
        if (_runEnded || _run.State is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            lockedMask = _run.CustomRules?.SelectedArchetypeMask ?? 0UL;
            error = "对局已结束，无法修改流派倾向。";
            return false;
        }

        _run.CustomRules ??= new CustomRunRuleSet();
        if (!_run.CustomRules.AllowTendencySelection)
        {
            _run.CustomRules.SelectedArchetypeMask = 0UL;
            _run.TendencyLocked = true;
            _shop.ActiveRules = _run.CustomRules;
            _shop.UpdateActiveArchetypeMask(0UL);
            lockedMask = 0UL;
            error = "当前规则已关闭流派倾向自选。";
            return false;
        }

        if (!CanLockOpeningTendency)
        {
            _run.TendencyLocked = true;
            lockedMask = _run.CustomRules.SelectedArchetypeMask;
            error = "本局流派倾向已锁定，严禁对局途中修改。";
            return false;
        }

        lockedMask = CardArchetypeCatalog.ClampMaxBits(rawMask & CardArchetypeCatalog.AllMask, maxBits: 3);
        _run.CustomRules = _run.CustomRules.CloneNormalized();
        _run.CustomRules.SelectedArchetypeMask = lockedMask;
        _run.TendencyLocked = true;
        _shop.ActiveRules = _run.CustomRules;
        _shop.UpdateActiveArchetypeMask(lockedMask);
        error = null;
        return true;
    }

    private bool IsTournamentRun()
        => _run.PlayMode == EPlayMode.Tournament
        || (_run.TournamentSessionId.HasValue && _run.TournamentSessionId.Value != Guid.Empty)
        || _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId) != null;

    private void ApplyStartingStats(CustomRunRuleSet rules, List<IGameSimEvent> events)
    {
        int startHp = Math.Max(1, rules.StartingHealth != 250 ? rules.StartingHealth : _mode.StartingHealth);
        int startGold = Math.Max(0, rules.StartingGold != 15 ? rules.StartingGold : _mode.StartingGold);
        int startIncome = Math.Max(0, rules.StartingIncome != 5 ? rules.StartingIncome : _mode.StartingIncome);
        int startPrestige = Math.Max(1, rules.StartingPrestige != 20 ? rules.StartingPrestige : _mode.PrestigeInitial);
        int startExp = Math.Max(0, rules.StartingExperience != 3 ? rules.StartingExperience : _mode.StartingExperience);

        _run.PlayerAttributes[EPlayerAttributeType.Health] = startHp;
        _run.PlayerAttributes[EPlayerAttributeType.HealthMax] = startHp;
        _run.PlayerAttributes[EPlayerAttributeType.Gold] = startGold;
        _run.PlayerAttributes[EPlayerAttributeType.Income] = startIncome;
        _run.PlayerAttributes[EPlayerAttributeType.Level] = 1;
        _run.PlayerAttributes[EPlayerAttributeType.Experience] = 0;
        _run.PlayerAttributes[EPlayerAttributeType.Prestige] = startPrestige;

        int xpReq = Math.Max(1, _mode.ExperiencePerLevel);
        if (startExp < xpReq)
        {
            // 官方初始 Experience = 0（< 8），直接赋值且不触发首帧误升级或多余跳字
            _run.PlayerAttributes[EPlayerAttributeType.Experience] = startExp;
        }
        else
        {
            GrantExperienceToList(events, startExp, EExperienceType.Hour);
        }
    }

    private CardInstance? TryCreatePresetCoreCard(PresetCardSpec? spec, string? instanceId = null, bool allowEnchantment = true)
    {
        if (spec == null || string.IsNullOrWhiteSpace(spec.TemplateId)) return null;
        var coreTpl = _repo.GetCard(spec.TemplateId);
        if (coreTpl == null) return null;

        var reqTier = TierHelper.ParseTier(string.IsNullOrWhiteSpace(spec.Tier) ? coreTpl.StartingTier : spec.Tier);
        var clampedTier = SpawnQueryEngine.ClampToSupportedTier(coreTpl, reqTier);
        string? ench = null;
        if (allowEnchantment &&
            !string.IsNullOrWhiteSpace(spec.Enchantment) &&
            Enum.TryParse<EEnchantmentType>(spec.Enchantment.Trim(), true, out var parsedEnch))
        {
            ench = parsedEnch.ToString();
        }
        return new CardInstance(coreTpl, clampedTier, ench, instanceId);
    }

    private void GrantStarterPresets(CustomRunRuleSet rules, List<IGameSimEvent> events)
    {
        bool grantedAny = false;
        if (rules.StartingItems is { Count: > 0 })
        {
            foreach (var spec in rules.StartingItems)
            {
                var ci = TryCreatePresetCoreCard(spec, instanceId: null, allowEnchantment: true);
                if (ci == null) continue;
                RegisterSpawnedCoreCard(ci, EInventorySection.Hand, preferredSlot: -1, emitSpawnEvent: true, events);
                grantedAny = true;
            }
        }

        if (rules.StartingSkills is { Count: > 0 })
        {
            foreach (var spec in rules.StartingSkills)
            {
                var ci = TryCreatePresetCoreCard(spec, instanceId: null, allowEnchantment: false);
                if (ci == null) continue;
                RegisterSpawnedCoreCard(ci, null, preferredSlot: -1, emitSpawnEvent: true, events);
                grantedAny = true;
            }
        }

        if (grantedAny)
        {
            RecalculateAurasAndSyncBoard(events);
        }
    }

    public Outcome Handle(INetCommand command)
    {
        _shop.ActiveRules = _run.CustomRules;
        if (_runEnded || _run.State is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            if (command is CheatCommand cheat &&
                cheat.Args is { Count: > 0 } &&
                cheat.Args[0].Equals("restart", StringComparison.OrdinalIgnoreCase))
            {
                var restartOutcome = OnCheat(cheat);
                if (restartOutcome.Error == null)
                    _run.RngSeed = _rng.Next(1, int.MaxValue);
                return restartOutcome;
            }
            return NotAllowed($"command {command.GetType().Name} not allowed after run ended ({_run.State})");
        }

        var outcome = command switch
        {
            SelectItemCommand c => OnSelectItem(c),
            MoveItemCommand c => OnMoveItem(c),
            SelectSkillCommand c => OnSelectSkill(c),
            SelectEncounterCommand c => OnSelectEncounter(c),
            RerollCommand => OnReroll(),
            ExitCurrentStateCommand => OnExitState(),
            SellCardCommand c => OnSell(c),
            CommitToPedestalCommand c => OnCommitPedestal(c),
            CheatCommand c => OnCheat(c),
            AbandonRunCommand => OnAbandon(),
            _ => Err("unsupported command " + command.GetType().Name),
        };
        if (outcome.Error == null)
        {
            if (!_run.TendencyLocked &&
                (_run.Day > 1 || _run.Hour > 1 || _run.State != ERunState.Choice || _run.SelectionsMadeInCurrentContext > 0))
            {
                _run.TendencyLocked = true;
            }
            _run.RngSeed = _rng.Next(1, int.MaxValue);
        }
        return outcome;
    }

    private static Outcome Err(string msg) => new()
    {
        Error = new NetMessageError(null, EResponseErrorType.Error, msg),
    };

    private static Outcome NotFound(string what) => new()
    {
        Error = new NetMessageError(null, EResponseErrorType.NotFound, what),
    };

    private static Outcome NotAllowed(string what) => new()
    {
        Error = new NetMessageError(null, EResponseErrorType.NotAllowed, what),
    };

    // ── 选卡 / 买卡 / 二合一升阶 / EncounterStep 分流 ───────────────────────

    private Outcome OnSelectItem(SelectItemCommand cmd)
    {
        string id = cmd.InstanceId.Value;
        if (string.IsNullOrEmpty(id)) return NotFound("empty instance id");
        if (!_run.Cards.TryGetValue(id, out var card)) return NotFound("card not found: " + id);

        if (_runEnded || _run.State is ERunState.Combat or ERunState.PVPCombat
            or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            return NotAllowed($"SelectItem not allowed in state {_run.State}");
        }

        // 已在玩家手上的卡 → 换位（并重算光环）
        if (card.Owner == ECombatantId.Player)
        {
            return MoveOwnedCard(card, cmd.TargetSockets, cmd.Section ?? card.Section ?? EInventorySection.Hand);
        }

        if (_run.State == ERunState.Pedestal)
            return NotAllowed($"cannot select non-owned card {id} in Pedestal state");
        if (!_run.SelectionSet.Contains(id))
            return NotAllowed($"card {id} is not in current SelectionSet");

        // 若命令行/脚本通过 SelectItemCommand 点选 EncounterStep 或遭遇卡，统一校验状态后交由对应处理器
        if (card.Type == ECardType.EncounterStep)
        {
            if (_run.State is not (ERunState.Encounter or ERunState.LevelUp or ERunState.Loot))
                return NotAllowed($"EncounterStep {id} cannot be selected in state {_run.State}");
            if (_shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext))
                return NotAllowed("max selections reached in current context");
            return ExecuteEncounterStepSelection(card);
        }
        if (card.Type is ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter)
        {
            if (_run.State is not (ERunState.Choice or ERunState.LevelUp or ERunState.Encounter))
                return NotAllowed($"encounter card {id} cannot be selected via SelectItem in state {_run.State}");
            return ExecuteEncounterCardSelection(card);
        }

        if (_run.State is not (ERunState.Encounter or ERunState.Loot or ERunState.LevelUp))
            return NotAllowed($"cannot select/buy card {id} in state {_run.State}");
        if (_shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext))
            return NotAllowed("max selections reached in current context");

        // 候选区技能卡
        if (card.Type == ECardType.Skill)
        {
            return EquipCandidateSkill(card, isStarterOrFree: !StartSelectionDone || _run.SelectionRules.SelectionIsFree);
        }

        var o = new Outcome();

        // 商店 / 战利品 / 奖励 买入物品（对齐 ShopEngine.ActiveShelfDiscounts 25%-off 绿标价）
        int buyPrice = _run.SelectionRules.SelectionIsFree
            ? 0
            : (_shop.TryGetShelfDiscount(id, out var shelfDiscount) && shelfDiscount != null
                ? Math.Min(
                    card.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, shelfDiscount.DiscountedPrice),
                    shelfDiscount.DiscountedPrice)
                : card.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0));
        int gold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        if (gold < buyPrice) return NotAllowed($"not enough gold: need {buyPrice}, have {gold}");

        // 检查是否可与玩家已有同名同阶物品二合一升阶
        var fuseTarget = FindFuseTarget(card);
        if (fuseTarget != null)
        {
            _run.PlayerAttributes[EPlayerAttributeType.Gold] = gold - buyPrice;
            _run.SelectionSet.Remove(id);
            _run.Cards.Remove(id); // 客户端 OnCardFusedHandler 自行清理选中源卡控制器

            ETier nextTier = fuseTarget.Tier + 1;
            EEnchantmentType? prevEnchantment = fuseTarget.Enchantment;
            EEnchantmentType? finalEnchantment = card.Enchantment ?? fuseTarget.Enchantment;
            bool enchantmentChanged = finalEnchantment.HasValue && finalEnchantment != prevEnchantment;
            if (enchantmentChanged)
            {
                fuseTarget.Enchantment = finalEnchantment;
                var fuseCore = EnsureCoreCard(fuseTarget);
                fuseCore.ApplyEnchantment(finalEnchantment!.Value.ToString(), preventOverride: false, isTemporary: false);
            }
            UpgradeCardEntryToTier(fuseTarget, nextTier);
            _run.SelectionsMadeInCurrentContext++;

            o.Events.Add(new GameSimEventCardPurchased(
                id, (uint)buyPrice,
                fuseTarget.Socket ?? EContainerSocketId.Socket_3,
                fuseTarget.Section ?? EInventorySection.Hand,
                ECombatantId.Player));
            o.Events.Add(new GameSimEventCardFused(id, fuseTarget.InstanceId, nextTier, finalEnchantment));
            o.Events.Add(new GameSimEventCardUpgraded(fuseTarget.InstanceId, nextTier));
            if (enchantmentChanged)
            {
                o.Events.Add(new GameSimEventCardEnchanted
                {
                    InstanceId = fuseTarget.InstanceId,
                    EnchantmentType = finalEnchantment,
                    IsReverted = false,
                });
            }
            o.Events.Add(new GameSimEventCardDisposed(id, ECardDisposalReason.Cleared));

            // 触发购买钩子 + 升阶钩子 + 附魔钩子 + 全局光环重算与全盘同步
            if (!_run.SelectionRules.SelectionIsFree)
                _run.TrackItemPurchasedTags(card, card.CoreCard ?? fuseTarget.CoreCard);
            if (fuseTarget.CoreCard != null)
            {
                bool suppressUpgradedBecausePurchased = !_run.SelectionRules.SelectionIsFree;
                var p1Buy = BuildCorePlayerFromRun();
                var buyBatch = OutOfCombatController.OnItemPurchased(
                    p1Buy, fuseTarget.CoreCard, _repo, _rng, _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard,
                    isFree: _run.SelectionRules.SelectionIsFree,
                    isFusion: true);
                ApplyOutOfCombatBatch(p1Buy, buyBatch, o.Events, syncBoardDiffs: false);

                var p1Upg = BuildCorePlayerFromRun();
                var upgBatch = OutOfCombatController.OnCardUpgraded(
                    p1Upg, fuseTarget.CoreCard, _repo, _rng, isFusion: suppressUpgradedBecausePurchased,
                    currentEncounterId: _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard);
                ApplyOutOfCombatBatch(p1Upg, upgBatch, o.Events, syncBoardDiffs: false);

                if (enchantmentChanged)
                {
                    var p1Ench = BuildCorePlayerFromRun();
                    var enchBatch = OutOfCombatController.OnCardEnchanted(
                        p1Ench, fuseTarget.CoreCard, _repo, _rng,
                        currentEncounterId: _run.CurrentEncounterId,
                        activeEncounterCard: _run.CurrentEncounterCoreCard);
                    ApplyOutOfCombatBatch(p1Ench, enchBatch, o.Events, syncBoardDiffs: false);
                }
            }

            RecalculateAurasAndSyncBoard(o.Events);
            ReconcileAndVerifyBoardInvariants("BuyFuse", o.DirtyCardIds, o.Events);
            LogBoardAudit("BuyFuse", fuseTarget.InstanceId, fuseTarget.TemplateId.ToString(), $"Shelf:{id}", $"{fuseTarget.Section}:{fuseTarget.Socket}");
            Console.WriteLine($"[RunEngine] FUSE {card.TemplateId} -> {fuseTarget.InstanceId} tier={nextTier} ench={finalEnchantment}");
            CompletePostPurchaseFlow(o);
            return o;
        }

        // 常规放入 HandGrid 或 StashGrid（D-28：买入手牌时禁止手牌满后静默丢入 Stash；仅显式 target Stash 才入储藏箱）
        var coreCard = EnsureCoreCard(card);
        EInventorySection requestedSection = cmd.Section ?? EInventorySection.Hand;
        EInventorySection targetSection = requestedSection;
        int desiredSlot = cmd.TargetSockets is { Count: > 0 } ? cmd.TargetSockets.Min(s => (int)s) : -1;

        var preMoveSnapshot = SnapshotPlayerItemPlacements();
        var movedEntries = new List<(RunState.CardEntry Entry, EContainerSocketId NewSocket, EInventorySection Section)>();
        bool placed = TryPlaceIntoSection(card, coreCard, targetSection, desiredSlot, movedEntries);

        if (!placed)
        {
            string rejectReason = targetSection == EInventorySection.Stash
                ? "no free space in stash"
                : "no free space in hand";
            LogBoardAudit("BuyCard", id, card.TemplateId.ToString(), "Shelf", $"{requestedSection}:{desiredSlot}", rejectReason);
            return NotAllowed(rejectReason);
        }

        _run.PlayerAttributes[EPlayerAttributeType.Gold] = gold - buyPrice;
        card.Owner = ECombatantId.Player;
        card.Section = targetSection;
        card.State = ECardState.Alive;
        coreCard.StartSlot = card.Socket.HasValue ? (int)card.Socket.Value : coreCard.StartSlot;
        _run.SelectionSet.Remove(id);
        _run.SelectionsMadeInCurrentContext++;

        o.DirtyCardIds.Add(card.InstanceId);
        o.Events.Add(new GameSimEventCardPurchased(
            id, (uint)buyPrice,
            card.Socket ?? EContainerSocketId.Socket_3,
            targetSection,
            ECombatantId.Player));

        foreach (var mv in movedEntries)
        {
            o.DirtyCardIds.Add(mv.Entry.InstanceId);
            if (mv.Entry.InstanceId == card.InstanceId) continue;
            EmitMoveEventIfNotPresent(o.Events, mv.Entry.InstanceId, mv.Section, mv.NewSocket);
        }

        // 购买完成后调用 OutOfCombatController.OnItemPurchased（含 activeEncounterCard、isFree、isFusion）+ 光环重算
        if (!_run.SelectionRules.SelectionIsFree)
            _run.TrackItemPurchasedTags(card, coreCard);
        var p1Core = BuildCorePlayerFromRun();
        var purchaseBatch = OutOfCombatController.OnItemPurchased(
            p1Core, coreCard, _repo, _rng, _run.CurrentEncounterId,
            activeEncounterCard: _run.CurrentEncounterCoreCard,
            isFree: _run.SelectionRules.SelectionIsFree,
            isFusion: false);
        ApplyOutOfCombatBatch(p1Core, purchaseBatch, o.Events, syncBoardDiffs: true);

        if (_run.SelectionRules.SelectionIsFree &&
            coreCard.Template.Abilities.Values.Any(a => TriggerContainsType(a.Trigger, "TTriggerOnCardSelected")))
        {
            var p1Sel = BuildCorePlayerFromRun();
            var selBatch = OutOfCombatController.OnCardSelected(
                p1Sel, coreCard, _repo, _rng,
                currentEncounterId: _run.CurrentEncounterId,
                activeEncounterCard: _run.CurrentEncounterCoreCard);
            ApplyOutOfCombatBatch(p1Sel, selBatch, o.Events, syncBoardDiffs: true);
        }

        ReconcileAndVerifyBoardInvariants("BuyCard", o.DirtyCardIds, o.Events, preMoveSnapshot, skipMoveEventForId: card.InstanceId);
        SyncDisplacedCorePlacementsAndEmitDiffs(p1Core, preMoveSnapshot, o.DirtyCardIds, o.Events, skipMoveEventForId: card.InstanceId);
        LogBoardAudit("BuyCard", id, card.TemplateId.ToString(), "Shelf", $"{card.Section}:{card.Socket}");
        CompletePostPurchaseFlow(o);
        return o;
    }

    private void CompletePostPurchaseFlow(Outcome o)
    {
        bool reachedMax = _shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext);

        if (!StartSelectionDone && _run.State == ERunState.Encounter &&
            (!_run.SelectionRules.CanSelectMultiple || _run.SelectionSet.Count == 0 || reachedMax))
        {
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            StartSelectionDone = true;
            if (_run.PendingLevelUp)
            {
                PushChoiceAndEnterLevelUp(o.Events);
                return;
            }
            _run.SelectionsMadeInCurrentContext = 0;
            o.Events.AddRange(DealHourEncountersAndClearActive());
            return;
        }

        if ((!_run.SelectionRules.CanSelectMultiple || reachedMax || (_run.State == ERunState.Loot && _run.SelectionSet.Count == 0 && _run.MonsterLootPages.Count <= 1)) && _run.State is ERunState.LevelUp or ERunState.Loot)
        {
            bool leavingLoot = _run.State == ERunState.Loot;
            if (leavingLoot)
                ApplyWillAutoSellOnExitIfNeeded(o.Events);
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: !leavingLoot));
            AdvanceAfterSubState(o);
            return;
        }

        // 若单选遭遇或已达 MaxSelections 上限：先按 WillAutoSellOnExit 结算未拿取货架卡，再清空并安全退场（修复 Bug #4 & V7-2.5 & #32a）
        if ((!_run.SelectionRules.CanSelectMultiple || reachedMax) && _run.State == ERunState.Encounter)
        {
            string? exitingStepId = _run.CurrentEncounterId;
            string? exitingRootId = _run.ParentEncounterTemplateId ?? exitingStepId;
            TriggerOnEncounterExited(_run.CurrentEncounterId, o.Events);
            ApplyWillAutoSellOnExitIfNeeded(o.Events);
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            Guid? nextOnExit = _run.SelectionRules.NextEncounterOnExit;
            bool hasNextEnc = nextOnExit is Guid ng && ng != Guid.Empty && (_pool.IsAssetValid(ng.ToString()) || _repo.GetCard(ng.ToString()) != null);
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: hasNextEnc));
            ExitEncounterOrAdvance(o, nextOnExit, exitingRootId, exitingStepId);
            return;
        }

        // V7-1.5：在商店 / 多选 Encounter 内购买触发升级时只累计 PendingLevelUps，不在遭遇内抢跳打断；退出遭遇后统一进入 LevelUp。
    }

    private void FinishSingleChoiceIfNeeded(Outcome o)
    {
        if (!_run.SelectionRules.CanSelectMultiple && _run.State is ERunState.LevelUp or ERunState.Loot)
        {
            bool leavingLoot = _run.State == ERunState.Loot;
            if (leavingLoot)
                ApplyWillAutoSellOnExitIfNeeded(o.Events);
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            AdvanceAfterSubState(o);
        }
    }

    private RunState.CardEntry? FindFuseTarget(RunState.CardEntry incoming)
    {
        if (incoming.Type is not (ECardType.Item or ECardType.Skill) || incoming.Tier >= ETier.Diamond)
            return null;
        return _run.Cards.Values.FirstOrDefault(c =>
            c.Owner == ECombatantId.Player &&
            c.Type == incoming.Type &&
            c.TemplateId == incoming.TemplateId &&
            c.Tier == incoming.Tier);
    }

    private void UpgradeCardEntryToTier(RunState.CardEntry entry, ETier nextTier)
    {
        entry.Tier = nextTier;

        // 保留既有 CoreCard 上的永久成长/任务/附魔/临时层，避免直接重建覆盖
        var core = EnsureCoreCard(entry);
        core.SetTier(ShopEngine.ToCoreTier(nextTier), isTemporary: false);

        if (entry.Type == ECardType.Item)
        {
            var baseSize = ShopEngine.ParseSize(core.Template.Size);
            var (buy, sell) = ShopEngine.PriceFor(baseSize, nextTier);
            entry.Attributes[ECardAttributeType.BuyPrice] = buy;
            if (!core.BaseAttributes.ContainsKey("SellPrice") &&
                !core.PermanentAdd.ContainsKey("SellPrice"))
            {
                entry.Attributes[ECardAttributeType.SellPrice] = sell;
            }
        }
        else if (entry.Type == ECardType.Skill)
        {
            var coreTier = ShopEngine.ToCoreTier(nextTier);
            entry.Attributes[ECardAttributeType.BuyPrice] = _repo.GameMode.StandardPrices.GetSkillBuyPrice(coreTier);
            entry.Attributes[ECardAttributeType.SellPrice] = _repo.GameMode.StandardPrices.GetSkillSellPrice(coreTier);
        }

        // 将 CoreCard 升阶后变化的新基础属性同步进 entry.Attributes（不发重复 Upgrade 事件，由调用方按需派发）
        var dummyEvents = new List<IGameSimEvent>();
        dummyEvents.AddRange(_run.SyncEntryFromCoreCardAndDiff(entry, _repo.GameMode.StandardPrices, emitAuraRefreshEvent: false));
    }

    private bool TryPlaceIntoSection(
        RunState.CardEntry entry,
        CardInstance coreCard,
        EInventorySection section,
        int desiredSlot,
        List<(RunState.CardEntry Entry, EContainerSocketId NewSocket, EInventorySection Section)> moved)
    {
        _run.HandGrid.UnlockedMask = _run.UnlockedSlots;
        _run.StashGrid.UnlockedMask = 1023;
        SyncEntrySizeFromCore(entry, coreCard);

        BoardGrid targetGrid = section == EInventorySection.Stash ? _run.StashGrid : _run.HandGrid;
        BoardGrid? sourceGrid = ResolveCurrentGridForEntry(entry, coreCard);
        bool sameGrid = sourceGrid == targetGrid;

        var preMovePositions = SnapshotPlayerItemPlacements();
        var targetSnapshot = SnapshotGridSlots(targetGrid);
        var sourceSnapshot = sourceGrid != null && !sameGrid ? SnapshotGridSlots(sourceGrid) : null;
        int oldSlot = coreCard.StartSlot;

        if (sourceGrid != null && !sameGrid)
        {
            sourceGrid.Remove(coreCard);
        }

        bool ok = false;
        if (sourceGrid == null)
        {
            // 买卡/衍生发卡：只要目标区已解锁总空位 >= 物品宽度，优先在指定位挤位，失败则自动紧凑滑移现有卡腾位，绝不误丢 Stash
            if (targetGrid.CountFreeUnlockedSlots() >= coreCard.SlotWidth)
            {
                if (desiredSlot >= 0)
                    ok = targetGrid.TryPlaceWithDisplaceOrSwap(coreCard, desiredSlot);
                if (!ok)
                    ok = targetGrid.TryAddAuto(coreCard);
            }
        }
        else if (sameGrid)
        {
            if (desiredSlot >= 0)
                ok = targetGrid.TryPlaceWithDisplaceOrSwap(coreCard, desiredSlot);
            if (!ok && desiredSlot < 0)
                ok = targetGrid.TryAddAuto(coreCard);
        }
        else
        {
            // 跨区移牌：优先直接放进目标区；若目标区空位不足且指定了槽位，执行原子跨区换位（对齐客户端 SocketOperations.SwapItemsOnToBoard）
            if (targetGrid.CountFreeUnlockedSlots() >= coreCard.SlotWidth)
            {
                if (desiredSlot >= 0)
                    ok = targetGrid.TryPlaceWithDisplaceOrSwap(coreCard, desiredSlot);
                if (!ok)
                    ok = targetGrid.TryAddAuto(coreCard);
            }
            else if (desiredSlot >= 0 && sourceGrid != null)
            {
                ok = TryCrossSectionSwap(coreCard, sourceGrid, targetGrid, desiredSlot, oldSlot);
            }
        }

        if (!ok)
        {
            RestoreGridFromSnapshot(targetGrid, targetSnapshot);
            if (sourceGrid != null && sourceSnapshot != null)
                RestoreGridFromSnapshot(sourceGrid, sourceSnapshot);
            coreCard.StartSlot = oldSlot;
            return false;
        }

        WriteGridCoordinatesToEntries(_run.HandGrid, EInventorySection.Hand, preMovePositions, entry, moved);
        WriteGridCoordinatesToEntries(_run.StashGrid, EInventorySection.Stash, preMovePositions, entry, moved);
        return true;
    }

    private BoardGrid? ResolveCurrentGridForEntry(RunState.CardEntry entry, CardInstance coreCard)
    {
        if (_run.HandGrid.Cards.Contains(coreCard)) return _run.HandGrid;
        if (_run.StashGrid.Cards.Contains(coreCard)) return _run.StashGrid;
        if (entry.Owner != ECombatantId.Player) return null;
        return entry.Section == EInventorySection.Stash ? _run.StashGrid : _run.HandGrid;
    }

    private bool TryCrossSectionSwap(
        CardInstance incoming,
        BoardGrid sourceGrid,
        BoardGrid targetGrid,
        int desiredSlot,
        int oldSlot)
    {
        if (!targetGrid.TryNormalizeSlotToUnlocked(desiredSlot, incoming.SlotWidth, out int normSlot))
            return false;

        var blockers = targetGrid.Cards
            .Where(c => c.StartSlot >= 0 && c.StartSlot < normSlot + incoming.SlotWidth && c.StartSlot + c.SlotWidth > normSlot)
            .OrderBy(c => c.StartSlot)
            .ToList();
        if (blockers.Count == 0) return false;

        int blockersWidth = blockers.Sum(b => b.SlotWidth);
        if (sourceGrid.CountFreeUnlockedSlots() < blockersWidth) return false;

        foreach (var b in blockers)
            targetGrid.Remove(b);

        bool placedIncoming = targetGrid.TryPlaceWithDisplaceOrSwap(incoming, normSlot) || targetGrid.TryAddAuto(incoming);
        if (!placedIncoming) return false;

        int fallbackSlot = oldSlot >= 0 ? oldSlot : 0;
        foreach (var b in blockers)
        {
            bool placedBlocker = sourceGrid.TryPlaceWithDisplaceOrSwap(b, fallbackSlot) || sourceGrid.TryAddAuto(b);
            if (!placedBlocker) return false;
            fallbackSlot = Math.Min(BoardGrid.Capacity - 1, b.StartSlot + b.SlotWidth);
        }
        return true;
    }

    private Dictionary<string, (EInventorySection? Section, EContainerSocketId? Socket)> SnapshotPlayerItemPlacements()
    {
        var snap = new Dictionary<string, (EInventorySection? Section, EContainerSocketId? Socket)>(StringComparer.Ordinal);
        foreach (var c in _run.Cards.Values)
        {
            if (c.Owner == ECombatantId.Player && c.Type == ECardType.Item)
                snap[c.InstanceId] = (c.Section, c.Socket);
        }
        return snap;
    }

    private static List<(CardInstance Card, int StartSlot)> SnapshotGridSlots(BoardGrid grid)
        => grid.Cards.Select(c => (c, c.StartSlot)).ToList();

    private static void RestoreGridFromSnapshot(BoardGrid grid, List<(CardInstance Card, int StartSlot)> snapshot)
    {
        grid.Cards.Clear();
        foreach (var (card, slot) in snapshot)
        {
            card.StartSlot = slot;
            grid.Cards.Add(card);
        }
    }

    private void WriteGridCoordinatesToEntries(
        BoardGrid grid,
        EInventorySection section,
        Dictionary<string, (EInventorySection? Section, EContainerSocketId? Socket)> preSnapshot,
        RunState.CardEntry primaryEntry,
        List<(RunState.CardEntry Entry, EContainerSocketId NewSocket, EInventorySection Section)> moved)
    {
        foreach (var gridCard in grid.Cards)
        {
            var item = FindEntryByCoreCard(gridCard);
            if (item == null) continue;

            var gridSocket = (EContainerSocketId)Math.Clamp(gridCard.StartSlot, 0, 9);
            SyncEntrySizeFromCore(item, gridCard);
            item.Section = section;
            item.Socket = gridSocket;

            if (ReferenceEquals(item, primaryEntry)) continue;
            if (!preSnapshot.TryGetValue(item.InstanceId, out var prev) || prev.Section != section || prev.Socket != gridSocket)
            {
                if (!moved.Any(m => m.Entry.InstanceId == item.InstanceId))
                    moved.Add((item, gridSocket, section));
            }
        }
    }

    private static void SyncEntrySizeFromCore(RunState.CardEntry entry, CardInstance coreCard)
    {
        if (entry.Type != ECardType.Item) return;
        entry.Size = coreCard.EffectiveSize switch
        {
            CardSize.Large => ECardSize.Large,
            CardSize.Medium => ECardSize.Medium,
            _ => ECardSize.Small,
        };
    }

    private void SyncGridNeighborSockets(
        BoardGrid grid,
        CardInstance placedCoreCard,
        EInventorySection section,
        List<(RunState.CardEntry Entry, EContainerSocketId NewSocket, EInventorySection Section)> moved)
    {
        foreach (var gridCard in grid.Cards)
        {
            if (ReferenceEquals(gridCard, placedCoreCard)) continue;
            var other = _run.Cards.Values.FirstOrDefault(x =>
                ReferenceEquals(x.CoreCard, gridCard) || x.InstanceId == gridCard.InstanceId);
            if (other == null) continue;

            var gridSocket = (EContainerSocketId)Math.Clamp(gridCard.StartSlot, 0, 9);
            if (other.Socket != gridSocket || other.Section != section)
            {
                other.Socket = gridSocket;
                other.Section = section;
                moved.Add((other, gridSocket, section));
            }
        }
    }

    private CardInstance EnsureCoreCard(RunState.CardEntry entry)
    {
        if (entry.CoreCard != null)
        {
            if (entry.Socket.HasValue && entry.Type is ECardType.Item or ECardType.SocketEffect)
                entry.CoreCard.StartSlot = (int)entry.Socket.Value;
            return entry.CoreCard;
        }

        var coreTpl = _repo.GetCard(entry.TemplateId.ToString());
        if (coreTpl == null)
        {
            coreTpl = new BazaarCore.Models.CardTemplate
            {
                Id = entry.TemplateId.ToString(),
                Type = entry.Type == ECardType.Skill ? "Skill" : entry.Type.ToString(),
                Size = entry.Size.ToString(),
                StartingTier = entry.Tier.ToString(),
            };
        }

        var ci = new CardInstance(coreTpl, ShopEngine.ToCoreTier(entry.Tier), entry.Enchantment?.ToString(), entry.InstanceId);
        foreach (var (ak, av) in entry.Attributes)
        {
            // CurrentAmmo 属于实时弹药计数而非静态基线，不可写入 PermanentAdd["Ammo"] 污染上限计算
            if (ak == ECardAttributeType.Ammo)
                continue;
            string key = ak.ToString();
            if (ak == ECardAttributeType.BuyPrice && av == 0 && !(coreTpl.Attributes?.ContainsKey("BuyPrice") ?? false))
                continue;
            if (ak == ECardAttributeType.SellPrice &&
                !ci.BaseAttributes.ContainsKey("SellPrice") &&
                !(coreTpl.Attributes?.ContainsKey("SellPrice") ?? false))
            {
                int defaultSell = entry.Type == ECardType.Item ? ShopEngine.PriceFor(entry.Size, entry.Tier).Sell : 0;
                if (av == defaultSell) continue;
                ci.PermanentAdd[key] = av - defaultSell;
                continue;
            }
            double baseVal = ci.BaseAttributes.GetValueOrDefault(key, 0);
            if (Math.Abs(av - baseVal) > 1e-6)
                ci.PermanentAdd[key] = av - baseVal;
        }
        ci.RefillAmmoToMax();
        if (entry.Socket.HasValue) ci.StartSlot = (int)entry.Socket.Value;
        entry.CoreCard = ci;
        return ci;
    }

    // ── 移牌 / 选技能 / 选遭遇 / EncounterStep 执行 ─────────────────────────

    private Outcome OnMoveItem(MoveItemCommand cmd)
    {
        if (_runEnded || _run.State is ERunState.Combat or ERunState.PVPCombat
            or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            return NotAllowed($"MoveItem not allowed in state {_run.State}");
        }
        string id = cmd.InstanceId.Value;
        if (string.IsNullOrEmpty(id)) return NotFound("empty instance id");
        if (!_run.Cards.TryGetValue(id, out var card)) return NotFound("card not found: " + id);
        if (card.Owner != ECombatantId.Player) return NotAllowed("cannot move non-player card: " + id);
        if (card.Type != ECardType.Item) return NotAllowed($"only Item cards can be moved, got {card.Type}");
        return MoveOwnedCard(card, cmd.TargetSockets, cmd.Section);
    }

    private Outcome MoveOwnedCard(RunState.CardEntry card, List<EContainerSocketId>? targetSockets, EInventorySection targetSection)
    {
        var coreCard = EnsureCoreCard(card);
        string fromLoc = $"{card.Section}:{card.Socket}";
        int desiredSlot = targetSockets is { Count: > 0 } ? targetSockets.Min(s => (int)s) : -1;
        var preMoveSnapshot = SnapshotPlayerItemPlacements();
        var moved = new List<(RunState.CardEntry Entry, EContainerSocketId NewSocket, EInventorySection Section)>();

        if (!TryPlaceIntoSection(card, coreCard, targetSection, desiredSlot, moved))
        {
            LogBoardAudit("MoveCard", card.InstanceId, card.TemplateId.ToString(), fromLoc, $"{targetSection}:{desiredSlot}", "cannot move card to target socket");
            return NotAllowed("cannot move card to target socket");
        }

        var o = new Outcome();
        o.DirtyCardIds.Add(card.InstanceId);
        EmitMoveEventIfNotPresent(o.Events, card.InstanceId, card.Section ?? targetSection, card.Socket ?? EContainerSocketId.Socket_3);
        foreach (var mv in moved)
        {
            o.DirtyCardIds.Add(mv.Entry.InstanceId);
            EmitMoveEventIfNotPresent(o.Events, mv.Entry.InstanceId, mv.Section, mv.NewSocket);
        }

        // 摆盘/挤位后立刻重算局外光环并全盘对比属性差分，向客户端推送最新卡面数值
        var p1Core = BuildCorePlayerFromRun();
        var moveBatch = OutOfCombatController.OnCardMoved(
            p1Core, _repo, activeEncounterCard: _run.CurrentEncounterCoreCard);
        ApplyOutOfCombatBatch(p1Core, moveBatch, o.Events, syncBoardDiffs: true);

        ReconcileAndVerifyBoardInvariants("MoveCard", o.DirtyCardIds, o.Events, preMoveSnapshot);
        SyncDisplacedCorePlacementsAndEmitDiffs(p1Core, preMoveSnapshot, o.DirtyCardIds, o.Events);
        LogBoardAudit("MoveCard", card.InstanceId, card.TemplateId.ToString(), fromLoc, $"{card.Section}:{card.Socket}");
        return o;
    }

    private Outcome OnSelectSkill(SelectSkillCommand cmd)
    {
        if (_runEnded || _run.State is not (ERunState.Encounter or ERunState.Loot or ERunState.LevelUp))
            return NotAllowed($"SelectSkill not allowed in state {_run.State}");

        string id = cmd.InstanceId.Value;
        if (string.IsNullOrEmpty(id)) return NotFound("empty skill instance id");
        if (!_run.Cards.TryGetValue(id, out var card))
            return NotFound("skill not found: " + id);
        if (card.Owner != null)
            return NotAllowed("skill is already owned: " + id);
        if (!_run.SelectionSet.Contains(id))
            return NotAllowed("skill is not in current SelectionSet: " + id);

        if (card.Type == ECardType.EncounterStep)
        {
            if (_shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext))
                return NotAllowed("max selections reached in current context");
            return ExecuteEncounterStepSelection(card);
        }
        if (card.Type != ECardType.Skill)
            return NotAllowed($"card {id} is not a Skill ({card.Type})");
        if (_shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext))
            return NotAllowed("max selections reached in current context");

        return EquipCandidateSkill(card, isStarterOrFree: !StartSelectionDone || _run.SelectionRules.SelectionIsFree);
    }

    private Outcome EquipCandidateSkill(RunState.CardEntry card, bool isStarterOrFree)
    {
        var o = new Outcome();
        int fallbackPrice = _repo.GameMode.StandardPrices.GetSkillBuyPrice(ShopEngine.ToCoreTier(card.Tier));
        int buyPrice = isStarterOrFree ? 0 : card.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, fallbackPrice);
        int gold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        if (gold < buyPrice) return NotAllowed($"not enough gold: need {buyPrice}, have {gold}");

        // Q38/Q39：技能严格去重与升阶规则：
        //   · 同阶且 < Diamond -> 二合一升至下一阶；
        //   · 已持有更低阶同名技能（如野怪掉落更高阶）-> 原地升阶至新卡阶级（不创建第二个同名技能）；
        //   · 已持有同阶 Diamond+ 或更高阶同名技能 -> 拒绝购买/拾取（且不扣费）。
        var fuseTarget = StartSelectionDone ? FindFuseTarget(card) : null;
        bool isSameTierFuse = fuseTarget != null;
        if (fuseTarget == null)
        {
            var existingOwnedSkill = _run.Cards.Values.FirstOrDefault(c =>
                c.Owner == ECombatantId.Player &&
                c.Type == ECardType.Skill &&
                c.TemplateId == card.TemplateId);
            if (existingOwnedSkill != null)
            {
                if (existingOwnedSkill.Tier >= card.Tier)
                    return NotAllowed($"skill {card.TemplateId} already owned at tier {existingOwnedSkill.Tier}");
                fuseTarget = existingOwnedSkill;
            }
        }

        if (buyPrice > 0) _run.PlayerAttributes[EPlayerAttributeType.Gold] = gold - buyPrice;

        if (fuseTarget != null)
        {
            string selectedId = card.InstanceId;
            _run.Cards.Remove(selectedId);
            _run.SelectionSet.Remove(selectedId);

            // V7-3.8：购买融合时，若新卡自带附魔则覆盖原卡附魔，否则保留原卡既有附魔
            EEnchantmentType? fusedEnchantment = card.Enchantment ?? fuseTarget.Enchantment;
            bool enchantmentChanged = fusedEnchantment != fuseTarget.Enchantment;
            if (fusedEnchantment.HasValue && enchantmentChanged)
            {
                fuseTarget.Enchantment = fusedEnchantment;
                EnsureCoreCard(fuseTarget).ApplyEnchantment(fusedEnchantment.Value.ToString(), preventOverride: false, isTemporary: false);
            }

            ETier nextTier = isSameTierFuse ? fuseTarget.Tier + 1 : card.Tier;
            UpgradeCardEntryToTier(fuseTarget, nextTier);

            o.Events.Add(new GameSimEventCardPurchased(
                selectedId,
                (uint)buyPrice,
                EContainerSocketId.Socket_0,
                EInventorySection.Hand,
                ECombatantId.Player));
            o.Events.Add(new GameSimEventCardFused(selectedId, fuseTarget.InstanceId, nextTier, fusedEnchantment));
            o.Events.Add(new GameSimEventCardUpgraded(fuseTarget.InstanceId, nextTier));
            if (enchantmentChanged && fusedEnchantment.HasValue)
            {
                o.Events.Add(new GameSimEventCardEnchanted
                {
                    InstanceId = fuseTarget.InstanceId,
                    EnchantmentType = fusedEnchantment.Value,
                    IsReverted = false,
                });
            }
            o.Events.Add(new GameSimEventCardDisposed(selectedId, ECardDisposalReason.Cleared));

            if (fuseTarget.CoreCard != null)
            {
                bool suppressUpgradedBecausePurchased = !isStarterOrFree;
                var p1Buy = BuildCorePlayerFromRun();
                var buyBatch = OutOfCombatController.OnItemPurchased(
                    p1Buy, fuseTarget.CoreCard, _repo, _rng, _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard,
                    isFree: isStarterOrFree,
                    isFusion: true);
                ApplyOutOfCombatBatch(p1Buy, buyBatch, o.Events, syncBoardDiffs: false);

                var p1Upg = BuildCorePlayerFromRun();
                var upgBatch = OutOfCombatController.OnCardUpgraded(
                    p1Upg, fuseTarget.CoreCard, _repo, _rng, isFusion: suppressUpgradedBecausePurchased,
                    currentEncounterId: _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard);
                ApplyOutOfCombatBatch(p1Upg, upgBatch, o.Events, syncBoardDiffs: false);

                if (enchantmentChanged && fusedEnchantment.HasValue)
                {
                    var p1Ench = BuildCorePlayerFromRun();
                    var enchBatch = OutOfCombatController.OnCardEnchanted(
                        p1Ench, fuseTarget.CoreCard, _repo, _rng,
                        currentEncounterId: _run.CurrentEncounterId,
                        activeEncounterCard: _run.CurrentEncounterCoreCard);
                    ApplyOutOfCombatBatch(p1Ench, enchBatch, o.Events, syncBoardDiffs: false);
                }
            }

            RecalculateAurasAndSyncBoard(o.Events);
            Console.WriteLine($"[RunEngine] SKILL FUSE {card.TemplateId} -> {fuseTarget.InstanceId} tier={nextTier} ench={fusedEnchantment}");
        }
        else
        {
            // Bug #13：保留原候选技能卡 InstanceId 并变更 Owner=Player（绝不先销毁），
            // 通过 CardPurchased + CardMoved(仅刷新客户端 Skill.Owner) + PlayerSkillEquipped 触发原生飞入左下角技能栏动画！
            _run.SelectionSet.Remove(card.InstanceId);
            card.Owner = ECombatantId.Player;
            card.Socket = null;
            card.Section = null;
            card.Attributes[ECardAttributeType.BuyPrice] = 0;

            var skCore = EnsureCoreCard(card);
            skCore.BaseAttributes["BuyPrice"] = 0;

            o.Events.Add(new GameSimEventCardPurchased(
                card.InstanceId,
                (uint)buyPrice,
                EContainerSocketId.Socket_0,
                EInventorySection.Hand,
                ECombatantId.Player));
            o.Events.Add(new GameSimEventCardMoved(
                card.InstanceId,
                ECombatantId.Player,
                EInventorySection.Hand,
                EContainerSocketId.Socket_0));
            o.Events.Add(new GameSimEventPlayerSkillEquipped(card.InstanceId, ECombatantId.Player));

            // 装备技能后触发购买钩子（开局后，含 activeEncounterCard、isFree、isFusion）并重算局外光环
            var p1Core = BuildCorePlayerFromRun();
            var batch = StartSelectionDone
                ? OutOfCombatController.OnItemPurchased(
                    p1Core, skCore, _repo, _rng, _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard,
                    isFree: isStarterOrFree,
                    isFusion: false)
                : OutOfCombatController.RecalculateOutOfCombatAuras(
                    p1Core, _repo, activeEncounterCard: _run.CurrentEncounterCoreCard);
            ApplyOutOfCombatBatch(p1Core, batch, o.Events, syncBoardDiffs: true);
        }

        _run.SelectionsMadeInCurrentContext++;
        bool reachedMax = _shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext);

        if (!StartSelectionDone)
        {
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            StartSelectionDone = true;
            if (_run.PendingLevelUp)
            {
                PushChoiceAndEnterLevelUp(o.Events);
                return o;
            }
            _run.SelectionsMadeInCurrentContext = 0;
            o.Events.AddRange(DealHourEncountersAndClearActive());
            return o;
        }

        if (_run.State == ERunState.LevelUp && (!_run.SelectionRules.CanSelectMultiple || reachedMax))
        {
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            AdvanceAfterSubState(o);
            return o;
        }

        if (_run.State == ERunState.Loot && (!_run.SelectionRules.CanSelectMultiple || reachedMax || (_run.SelectionSet.Count == 0 && _run.MonsterLootPages.Count <= 1)))
        {
            ApplyWillAutoSellOnExitIfNeeded(o.Events);
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            AdvanceAfterSubState(o);
            return o;
        }

        if ((!_run.SelectionRules.CanSelectMultiple || reachedMax) && _run.State == ERunState.Encounter)
        {
            string? exitingStepId = _run.CurrentEncounterId;
            string? exitingRootId = _run.ParentEncounterTemplateId ?? exitingStepId;
            TriggerOnEncounterExited(_run.CurrentEncounterId, o.Events);
            ApplyWillAutoSellOnExitIfNeeded(o.Events);
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            Guid? nextOnExit = _run.SelectionRules.NextEncounterOnExit;
            bool hasNextEnc = nextOnExit is Guid ng && ng != Guid.Empty && (_pool.IsAssetValid(ng.ToString()) || _repo.GetCard(ng.ToString()) != null);
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: hasNextEnc));
            ExitEncounterOrAdvance(o, nextOnExit, exitingRootId, exitingStepId);
            return o;
        }

        // V7-1.5：多选遭遇（含商店）内获得升级经验时推迟至离开遭遇后再结算 LevelUp。
        return o;
    }

    private Outcome OnSelectEncounter(SelectEncounterCommand cmd)
    {
        if (_runEnded || _run.State is ERunState.Combat or ERunState.PVPCombat or ERunState.Pedestal
            or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            return NotAllowed($"SelectEncounter not allowed in state {_run.State}");
        }

        string id = cmd.InstanceId.Value;
        if (string.IsNullOrEmpty(id)) return NotFound("empty encounter instance id");
        if (!_run.Cards.TryGetValue(id, out var card)) return NotFound("encounter not found: " + id);
        if (!_run.SelectionSet.Contains(id))
            return NotAllowed($"encounter {id} is not in current SelectionSet");

        if (card.Type == ECardType.EncounterStep)
        {
            if (_run.State is not (ERunState.Encounter or ERunState.LevelUp or ERunState.Loot))
                return NotAllowed($"EncounterStep {id} cannot be selected in state {_run.State}");
            return ExecuteEncounterStepSelection(card);
        }

        if (card.Type is not (ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter))
            return NotAllowed($"card {id} is not an encounter ({card.Type})");

        if (_run.State is not (ERunState.Choice or ERunState.LevelUp or ERunState.Encounter))
            return NotAllowed($"encounter {id} cannot be selected in state {_run.State}");

        return ExecuteEncounterCardSelection(card);
    }

    private Outcome ExecuteEncounterCardSelection(RunState.CardEntry card)
    {
        var o = new Outcome();
        string encId = card.TemplateId.ToString();

        // 若选中的是 PvP 战斗卡且处于多真人锦标赛同步屏障期，优先校验拦截，绝不破坏当前 Choice 与棋盘
        if (card.Type == ECardType.PvpEncounter)
        {
            var barrier = CheckTournamentPvpBarrier();
            if (barrier is { CanStartPvp: false })
            {
                return NotAllowed(barrier.BlockReason ?? $"需等待同房真人玩家到达第 {_run.Day} 天第 6 小时才能开启 PvP");
            }
        }

        // 1. 先派发 TTriggerOnEncounterSelected，若该遭遇卡自带 TTriggerOnCardSelected（例如 Pip / Bronze Loot +2 Gold）也同步触发
        var encCore = CreateEncounterCoreCard(encId, card.InstanceId);
        if (encCore != null)
        {
            var p1Sel = BuildCorePlayerFromRun();
            var selBatch = OutOfCombatController.OnEncounterSelected(
                p1Sel, encCore, _repo, _rng, encId, activeEncounterCard: encCore);
            ApplyOutOfCombatBatch(p1Sel, selBatch, o.Events, syncBoardDiffs: true);

            if (encCore.Template.Abilities.Values.Any(a =>
                    string.Equals(a.Trigger?.Type, "TTriggerOnCardSelected", StringComparison.OrdinalIgnoreCase)))
            {
                var p1CardSel = BuildCorePlayerFromRun();
                var cardSelBatch = OutOfCombatController.OnCardSelected(
                    p1CardSel, encCore, _repo, _rng, currentEncounterId: encId, activeEncounterCard: encCore);
                ApplyOutOfCombatBatch(p1CardSel, cardSelBatch, o.Events, syncBoardDiffs: true);
            }
        }

        if (CardPool.IsExpeditionRoomTemplate(encId))
        {
            _run.VisitedSingleSpawnEncounterIds.Add(encId);
            _run.VisitedDayEncounterIds.Add(encId);
            _run.ParentEncounterTemplateId = encId;
            ActivateEncounterCard(encId, card, o.Events, emitPurchaseIfSelected: true);
            var activeProtected = DetachActiveEncounterCardForShelfOp();
            var expRes = _stepEngine.EnterExpeditionRoom(encId, consumeMatchingTicket: true);
            RestoreActiveEncounterCardAfterShelfOp(activeProtected);
            if (!expRes.Success)
                return NotAllowed(expRes.ErrorMessage ?? $"failed to enter expedition room {encId}");

            o.Events.AddRange(expRes.Events);
            _run.SelectionsMadeInCurrentContext = 0;
            var expTpl = _pool.Get(encId);
            _run.PendingEncounterExitXp = Math.Max(0, expTpl?.ExperienceAwardUponSelection ?? 0);
            _run.CurrentEncounterCoreCard = CreateEncounterCoreCard(encId);
            TriggerOnEncounterEnteredAndDealt(encId, o.Events);
            if (_run.State == ERunState.Encounter && _run.SelectionSet.Count == 0 && !_run.SelectionRules.CanExit)
            {
                var rules = RunState.CloneRules(_run.SelectionRules);
                rules.CanExit = true;
                _run.SelectionRules = rules;
            }
            return o;
        }

        TransitionToEncounterById(encId, o, selectedCard: card);
        return o;
    }

    private void TransitionToEncounterById(string encId, Outcome o, RunState.CardEntry? selectedCard = null)
    {
        var tpl = _pool.Get(encId);
        var coreTpl = _repo.GetCard(encId);
        string typeStr = tpl?.Type ?? coreTpl?.Type ?? "EventEncounter";
        ECardType cardType = ShopEngine.ParseCardType(typeStr);

        if ((tpl?.IsSingleSpawn ?? false) || (coreTpl?.IsSingleSpawn ?? false))
        {
            _run.VisitedSingleSpawnEncounterIds.Add(encId);
        }
        if (!string.IsNullOrEmpty(encId))
        {
            _run.VisitedDayEncounterIds.Add(encId);
        }

        _run.CurrentEncounterId = encId;

        // 1. 野怪 PvE 或 Hour 6 PvP 战斗遭遇 → 真实三段式战斗序列
        if (cardType is ECardType.CombatEncounter or ECardType.PvpEncounter)
        {
            _run.ParentEncounterTemplateId = null;
            bool isPvp = encId.Equals(ShopEngine.Hour6PvpCardId, StringComparison.OrdinalIgnoreCase)
                         || (tpl?.InternalName?.Contains("PvP", StringComparison.OrdinalIgnoreCase) ?? false)
                         || (_run.Hour == 6 && cardType == ECardType.PvpEncounter)
                         || (_run.Hour == 6 && string.IsNullOrEmpty(tpl?.MonsterTemplateId));

            if (isPvp)
            {
                var barrier = CheckTournamentPvpBarrier();
                if (barrier is { CanStartPvp: false })
                {
                    o.Error = new NetMessageError(
                        null,
                        EResponseErrorType.NotAllowed,
                        barrier.BlockReason ?? $"需等待同房真人玩家到达第 {_run.Day} 天第 6 小时才能开启 PvP");
                    return;
                }

                // PvP 战斗绝不在顶栏或中央地毯保留 PvP 挑战卡/朱尔斯头像卡，进入 CombatSpawnSim 前全数清理并下发 CardDisposed
                if (selectedCard != null)
                {
                    _run.SelectionSet.Remove(selectedCard.InstanceId);
                    if (_run.Cards.Remove(selectedCard.InstanceId))
                        o.Events.Add(new GameSimEventCardDisposed(selectedCard.InstanceId, ECardDisposalReason.Cleared));
                }
                FlushPendingEncounterExitXp(o.Events);
                _run.CurrentEncounterCoreCard = null;
                o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
                RunRealCombatSequence(o, tpl, isPvp: true);
                return;
            }

            // PvE 野怪战斗：保留所选怪物头像卡在 Socket_5 供战后 Loot 顶栏显示，其余候选遭遇卡全数销毁
            ActivateEncounterCard(encId, selectedCard, o.Events, emitPurchaseIfSelected: selectedCard != null);
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: true));
            RunRealCombatSequence(o, tpl, isPvp: false);
            return;
        }

        // 服务端 #19：进入根级 EventEncounter / PedestalEncounter 时记录 ParentEncounterTemplateId；
        // 进入子级 EncounterStep 时保留既有的 ParentEncounterTemplateId，并将 Socket_5 头像卡持续锚定在父级遭遇模板，
        // 避免客户端 EncounterController/BoardManager 因找不到 EncounterStep 肖像而清屏。
        if (cardType is ECardType.EventEncounter or ECardType.PedestalEncounter)
        {
            _run.ParentEncounterTemplateId = encId;
        }
        string portraitAnchorId = ResolvePortraitAnchorEncounterId(encId);
        RunState.CardEntry? anchorSelectedCard =
            cardType == ECardType.EncounterStep && !string.IsNullOrEmpty(_run.ParentEncounterTemplateId)
                ? null
                : selectedCard;
        ActivateEncounterCard(portraitAnchorId, anchorSelectedCard, o.Events, emitPurchaseIfSelected: anchorSelectedCard != null);

        // 2. 升阶/附魔基座 → 进入 PedestalState（经验延迟到离场结算，且禁止进门同帧抢跳 LevelUp，修复 Bug #12/#16）
        if (cardType == ECardType.PedestalEncounter)
        {
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: true));
            _run.State = ERunState.Pedestal;
            _run.CurrentEncounterId = encId;
            _run.SelectionsMadeInCurrentContext = 0;
            _run.PendingEncounterExitXp = Math.Max(0, tpl?.ExperienceAwardUponSelection ?? 0);
            _run.CurrentEncounterCoreCard = CreateEncounterCoreCard(encId);
            _run.SelectionRules = new TSelectionContextRules
            {
                CanSelectMultiple = false,
                SelectionIsFree = true,
                CanExit = true,
            };
            RecalculateAurasAndSyncBoard(o.Events);
            ReconcileAndVerifyBoardInvariants("EnterPedestal", o.DirtyCardIds, o.Events);
            o.Events.Add(new GameSimEventStateTransitioned(ERunState.Pedestal, _run.CurrentEncounterId));
            TriggerOnEncounterEnteredAndDealt(encId, o.Events);
            return;
        }

        // 3. 商人 / 奇遇事件遭遇（及多步链式 EncounterStep）→ 进入 EncounterState 揭开迷雾并铺开真实选项
        _run.SelectionsMadeInCurrentContext = 0;
        var shelfEvents = DealEncounterShelfPreservingActiveEncounter(
            encId,
            isReroll: false,
            playerState: BuildCorePlayerFromRun(),
            ownedMaxTiers: _run.GetOwnedMaxTiers());
        NormalizeChildStepTransitionEvents(shelfEvents, encId);
        o.Events.AddRange(shelfEvents);
        _run.PendingEncounterExitXp = Math.Max(0, tpl?.ExperienceAwardUponSelection ?? 0);
        _run.CurrentEncounterCoreCard = CreateEncounterCoreCard(encId);
        TriggerOnEncounterEnteredAndDealt(encId, o.Events);

        // 安全防卡死兜底：若奇遇未产出任何选项卡且未允许退出，确保 CanExit=true
        if (_run.State == ERunState.Encounter && _run.SelectionSet.Count == 0 && !_run.SelectionRules.CanExit)
        {
            var rules = RunState.CloneRules(_run.SelectionRules);
            rules.CanExit = true;
            _run.SelectionRules = rules;
        }
    }

    /// <summary>
    /// 消费并执行 ECardType.EncounterStep 分支选项卡：
    ///   · 校验前置条件与费用、执行 TTriggerOnCardSelected 全部动作（扣费/属性/发卡/升级/附魔/变形/局部重抽/刷新）
    ///   · 重算局外光环与全盘同步
    ///   · 处理 NextEncounterOnSelection 多步链式跳转（子奇遇、基座或奇遇内直接触发 CombatEncounter 野怪战斗）
    ///   · 处理升级压栈与小时推进
    /// </summary>
    private Outcome ExecuteEncounterStepSelection(RunState.CardEntry stepEntry)
    {
        if (_shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext))
            return NotAllowed("max selections reached in current context");

        var preStepPlacements = SnapshotPlayerItemPlacements();
        var preStepPlayer = BuildCorePlayerFromRun();
        var activeProtected = DetachActiveEncounterCardForShelfOp();
        EncounterStepResult stepResult;
        try
        {
            stepResult = _stepEngine.ExecuteStep(stepEntry, xp =>
            {
                var evs = new List<IGameSimEvent>();
                GrantExperienceToList(evs, xp, EExperienceType.Hour);
                return evs;
            });
        }
        finally
        {
            RestoreActiveEncounterCardAfterShelfOp(activeProtected);
        }

        if (!stepResult.Success)
            return NotAllowed(stepResult.ErrorMessage ?? "encounter step failed");

        ApplyExclusionSetActionsFromExecutedStep(stepEntry, preStepPlayer);
        NormalizeChildStepTransitionEvents(stepResult.Events, _run.CurrentEncounterId);

        _run.SelectionsMadeInCurrentContext++;
        var o = new Outcome();
        o.Events.AddRange(stepResult.Events);

        // 先对 EncounterStepEngine 的变形/发卡执行单真值网格校验并校准事件坐标，再重算局外光环
        ReconcileAndVerifyBoardInvariants("EncounterStep", o.DirtyCardIds, o.Events, preStepPlacements);
        ReconcileTransformedEventPlacements(o.Events);
        RecalculateAurasAndSyncBoard(o.Events);
        ReconcileAndVerifyBoardInvariants("EncounterStepPostAura", o.DirtyCardIds, o.Events);
        SyncShelfEntriesDiff(o.Events);

        if (stepResult.TransformedCards.Count > 0 || stepResult.SpawnedCards.Count > 0 || o.DirtyCardIds.Count > 0)
        {
            var firstTr = stepResult.TransformedCards.FirstOrDefault() ?? stepResult.SpawnedCards.FirstOrDefault();
            LogBoardAudit(
                stepResult.TransformedCards.Count > 0 ? "Transform" : "EncounterStep",
                firstTr?.InstanceId ?? stepEntry.InstanceId,
                (firstTr?.TemplateId ?? stepEntry.TemplateId).ToString(),
                "Board",
                firstTr != null ? $"{firstTr.Section}:{firstTr.Socket}" : "Synced");
        }

        // 1. 存在多步链式下一跳遭遇（子奇遇 / 基座 / 直接触发野怪战斗）
        if (!string.IsNullOrEmpty(stepResult.NextEncounterId))
        {
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            TransitionToEncounterById(stepResult.NextEncounterId, o);
            return o;
        }

        // 2. 若当场发出了新的替代卡（DealCards / Reroll）则重置本轮计数并保持在当前遭遇内继续选择，同时确保顶栏父遭遇卡锚定存在
        if (stepResult.DealtNewCards)
        {
            EnsureActiveParentPortraitCardAnchored(o.Events);
            _run.SelectionsMadeInCurrentContext = 0;
            TriggerOnEncounterCardsDealtOnly(_run.CurrentEncounterId, o.Events);
            return o;
        }

        bool reachedMax = _shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext);
        if (!stepResult.ExitedEncounter && !reachedMax && _run.SelectionSet.Count > 0 &&
            (stepResult.IsReselectable || _run.SelectionRules.CanSelectMultiple))
        {
            EnsureActiveParentPortraitCardAnchored(o.Events);
            // 如果不是可重复选同一选项卡，消耗掉已选的这一项并留在当前奇遇选其余项
            if (!stepResult.IsReselectable && _run.SelectionSet.Contains(stepEntry.InstanceId))
            {
                _run.SelectionSet.Remove(stepEntry.InstanceId);
                _run.Cards.Remove(stepEntry.InstanceId);
                o.Events.Add(new GameSimEventCardDisposed(stepEntry.InstanceId, ECardDisposalReason.Cleared));
            }
            return o;
        }

        // 3. 当前奇遇步骤完成并退出（含达到 MaxSelections 上限自动清架退出）
        if (!StartSelectionDone)
        {
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            StartSelectionDone = true;
            if (_run.PendingLevelUp)
            {
                PushChoiceAndEnterLevelUp(o.Events);
                return o;
            }
            _run.SelectionsMadeInCurrentContext = 0;
            o.Events.AddRange(DealHourEncountersAndClearActive());
            return o;
        }

        if (_run.State is ERunState.LevelUp or ERunState.Loot)
        {
            if (_run.State == ERunState.Loot)
                ApplyWillAutoSellOnExitIfNeeded(o.Events);
            else
                FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            AdvanceAfterSubState(o);
            return o;
        }

        string? exitingStepId = _run.CurrentEncounterId;
        string? exitingRootId = _run.ParentEncounterTemplateId ?? exitingStepId;
        TriggerOnEncounterExited(_run.CurrentEncounterId, o.Events);
        ApplyWillAutoSellOnExitIfNeeded(o.Events);
        FlushPendingEncounterExitXp(o.Events);
        _run.CurrentEncounterCoreCard = null;
        o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));

        if (stepResult.ShouldProgressHour && ShouldProgressHourOnExit(exitingRootId, exitingStepId))
        {
            if (_run.SuspendedStates.Count > 0 || _run.PendingLevelUp)
            {
                AdvanceAfterSubState(o);
            }
            else
            {
                // 若即将进入下一小时且经验已满足升级：AdvanceHour 内部会在新小时扣收入/经验后压栈 Choice 并切入 LevelUp
                AdvanceHour(o);
            }
            return o;
        }

        // ShouldProgressHour == false（如 Thieves Guild / 免费瞬时事件且无下一跳）→ 不消耗小时数，原地重开当前小时 Choice
        HandleNoHourProgressEncounterExit(o, exitingRootId, exitingStepId);
        return o;
    }

    // ── 刷新 / 离开状态 ─────────────────────────────────────────────────────

    private Outcome OnReroll()
    {
        if (_runEnded)
            return NotAllowed($"reroll not allowed in state {_run.State}");

        if (_run.State == ERunState.Loot && _run.CustomRules.MonsterLootChooseOne)
        {
            if (!_shop.TryCycleMonsterLootPage(out var pageEvents))
                return NotAllowed("monster loot has no additional pages to cycle");

            var lootOutcome = new Outcome();
            lootOutcome.Events.AddRange(pageEvents);
            TriggerOnEncounterCardsDealtOnly(_run.CurrentEncounterId, lootOutcome.Events);
            return lootOutcome;
        }

        if (_run.State != ERunState.Encounter)
            return NotAllowed($"reroll not allowed in state {_run.State}");
        if (!StartSelectionDone)
            return NotAllowed("reroll not allowed during starter selection");

        string encId = _run.CurrentEncounterId ?? "";
        if (string.IsNullOrEmpty(encId))
            return NotAllowed("no active encounter to reroll");

        var coreTpl = _repo.GetCard(encId);
        var poolTpl = _pool.Get(encId);
        string encName = poolTpl?.InternalName ?? coreTpl?.InternalName ?? "";
        bool isMerchant = CardArchetypeIndex.IsMerchantEncounterTemplate(encId, encName, poolTpl, coreTpl);
        bool hasRerollRules = _run.SelectionRules.RerollRules != null;
        bool hasShopGoods = _run.SelectionSet.Any(id =>
            _run.Cards.TryGetValue(id, out var ce) && ce.Type is ECardType.Item or ECardType.Skill);

        if (!hasRerollRules && !isMerchant)
            return NotAllowed("current encounter does not support reroll");
        if (!hasRerollRules && !hasShopGoods)
            return NotAllowed("current encounter has no rerollable shop goods");
        if (_run.RerollsRemaining == 0)
            return NotAllowed("no rerolls remaining");

        int cost = (int)_run.RerollCost;
        int gold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        if (gold < cost) return NotAllowed($"not enough gold to reroll: need {cost}, have {gold}");

        var o = new Outcome();
        _run.PlayerAttributes[EPlayerAttributeType.Gold] = gold - cost;

        int costIncrease = _run.SelectionRules.RerollRules?.CostIncrease ?? 1;
        uint nextCost = (uint)Math.Max(0, (int)_run.RerollCost + costIncrease);
        if (_run.SelectionRules.RerollRules?.CostMax is int costMax && costMax > 0)
            nextCost = Math.Min(nextCost, (uint)costMax);
        _run.RerollCost = nextCost;

        if (_run.RerollsRemaining > 0) _run.RerollsRemaining--;

        var shelfEvents = DealEncounterShelfPreservingActiveEncounter(
            encId,
            isReroll: true,
            playerState: BuildCorePlayerFromRun(),
            ownedMaxTiers: _run.GetOwnedMaxTiers());
        NormalizeChildStepTransitionEvents(shelfEvents, encId);
        o.Events.AddRange(shelfEvents);
        o.Events.Add(new GameSimEventRunRerollCostChanged(_run.RerollCost));

        TriggerOnEncounterCardsDealtOnly(encId, o.Events);
        return o;
    }

    private Outcome OnExitState()
    {
        var o = new Outcome();

        if (_runEnded || _run.State is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            o.Events.Add(new GameSimEventStateTransitioned(_run.State, null));
            return o;
        }

        if (!StartSelectionDone)
        {
            if (!_run.SelectionRules.CanExit && _run.SelectionSet.Count > 0)
                return NotAllowed("cannot exit starter selection without choosing");

            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            StartSelectionDone = true;
            if (_run.PendingLevelUp)
            {
                PushChoiceAndEnterLevelUp(o.Events);
                return o;
            }
            _run.SelectionsMadeInCurrentContext = 0;
            o.Events.AddRange(DealHourEncountersAndClearActive());
            return o;
        }

        // V7-1.6：允许空 Choice（SelectionSet.Count == 0）安全退出推进；仍有可选项时禁止跳过 Choice
        if (_run.State == ERunState.Choice)
        {
            if (_run.SelectionSet.Count > 0)
                return NotAllowed("cannot exit Choice state without selecting an encounter");

            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            if (_run.PendingLevelUp || _run.SuspendedStates.Count > 0)
            {
                AdvanceAfterSubState(o);
            }
            else
            {
                AdvanceHour(o);
            }
            return o;
        }

        if (_run.State is ERunState.Combat or ERunState.PVPCombat)
            return NotAllowed("cannot exit combat state");

        if (_run.State is ERunState.Loot or ERunState.LevelUp)
        {
            if (!_run.SelectionRules.CanExit && _run.SelectionSet.Count > 0)
                return NotAllowed($"state {_run.State} cannot be exited (CanExit=false)");

            if (_run.State == ERunState.Loot)
                ApplyWillAutoSellOnExitIfNeeded(o.Events);
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            AdvanceAfterSubState(o);
            return o;
        }

        if (_run.State is ERunState.Encounter or ERunState.Pedestal)
        {
            if (!_run.SelectionRules.CanExit && _run.SelectionSet.Count > 0)
                return NotAllowed($"state {_run.State} cannot be exited (CanExit=false)");
        }

        // Encounter / Pedestal 退出 → 派发 TTriggerOnEncounterExited + WillAutoSellOnExit + 结算延迟经验，检查 NextEncounterOnExit
        string? exitingEncId = _run.CurrentEncounterId;
        string? exitingRootId = _run.ParentEncounterTemplateId ?? exitingEncId;
        Guid? nextOnExit = _run.SelectionRules.NextEncounterOnExit;
        if (_run.State is ERunState.Encounter or ERunState.Pedestal)
        {
            TriggerOnEncounterExited(exitingEncId, o.Events);
            ApplyWillAutoSellOnExitIfNeeded(o.Events);
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
        }

        bool hasNextEnc = nextOnExit is Guid ng && ng != Guid.Empty && (_pool.IsAssetValid(ng.ToString()) || _repo.GetCard(ng.ToString()) != null);
        o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: hasNextEnc));
        ExitEncounterOrAdvance(o, nextOnExit, exitingRootId, exitingEncId);
        return o;
    }

    private void ExitEncounterOrAdvance(
        Outcome o,
        Guid? nextOnExit = null,
        string? exitingRootId = null,
        string? exitingStepId = null)
    {
        if (nextOnExit is Guid nextGuid && nextGuid != Guid.Empty)
        {
            string nextId = nextGuid.ToString();
            if (_pool.IsAssetValid(nextId) || _repo.GetCard(nextId) != null)
            {
                TransitionToEncounterById(nextId, o);
                return;
            }
        }

        if (!ShouldProgressHourOnExit(exitingRootId, exitingStepId))
        {
            HandleNoHourProgressEncounterExit(o, exitingRootId, exitingStepId);
            return;
        }

        if (_run.PendingLevelUp || _run.SuspendedStates.Count > 0)
        {
            AdvanceAfterSubState(o);
            return;
        }

        // 常规 Encounter / Pedestal 退出 → 推进下一小时
        AdvanceHour(o);
    }

    private bool? GetExplicitShouldProgressHour(string? encId)
    {
        if (string.IsNullOrWhiteSpace(encId)) return null;
        var poolTpl = _pool.Get(encId);
        var coreTpl = _repo.GetCard(encId);
        string? typeStr = poolTpl?.Type ?? coreTpl?.Type;
        if (string.Equals(typeStr, "CombatEncounter", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typeStr, "PvpEncounter", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return coreTpl?.ShouldProgressHour ?? poolTpl?.ShouldProgressHour;
    }

    private bool IsEncounterTemplateId(string encId)
    {
        string? typeStr = _pool.Get(encId)?.Type ?? _repo.GetCard(encId)?.Type;
        return string.Equals(typeStr, "EventEncounter", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(typeStr, "PedestalEncounter", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldProgressHourOnExit(string? rootEncId, string? currentEncId)
    {
        if (string.IsNullOrWhiteSpace(rootEncId) && string.IsNullOrWhiteSpace(currentEncId))
            return true;

        bool? rootFlag = GetExplicitShouldProgressHour(rootEncId);
        bool? curFlag = GetExplicitShouldProgressHour(currentEncId);
        if (rootFlag == false && curFlag != true)
            return false;
        return true;
    }

    private void HandleNoHourProgressEncounterExit(Outcome o, string? rootEncId, string? currentEncId)
    {
        if (!string.IsNullOrWhiteSpace(rootEncId) && IsEncounterTemplateId(rootEncId))
            _run.VisitedDayEncounterIds.Add(rootEncId);
        if (!string.IsNullOrWhiteSpace(currentEncId) && IsEncounterTemplateId(currentEncId))
            _run.VisitedDayEncounterIds.Add(currentEncId);

        if (_run.SuspendedStates.Count > 0)
        {
            AdvanceAfterSubState(o);
            return;
        }

        if (_run.PendingLevelUp)
        {
            // 先压入空 Choice 快照再进入升级：升级完成后回到当前小时重新发牌，绝不误跨下一小时
            PushChoiceAndEnterLevelUp(o.Events);
            return;
        }

        _run.SelectionsMadeInCurrentContext = 0;
        o.Events.AddRange(DealHourEncountersAndClearActive());
    }

    private void AdvanceAfterSubState(Outcome o) => AdvanceAfterSubStateToEvents(o.Events);

    private void AdvanceAfterSubStateToEvents(List<IGameSimEvent> events)
    {
        if (_run.PendingLevelUp)
        {
            if (_run.State != ERunState.LevelUp && _run.SuspendedStates.Count > 0)
            {
                events.Add(new GameSimEventInterruptStateEntered(ERunState.LevelUp, true));
            }
            ConsumeNextPendingLevelUpToEvents(events);
            return;
        }

        // 优先从 SuspendedStates 弹出恢复原商店/奇遇/Day-start Hour 1 Choice 现场
        if (_run.SuspendedStates.Count > 0)
        {
            ERunState exitingState = _run.State;
            var restored = _run.SuspendedStates.Pop();
            _run.State = restored.State;
            _run.CurrentEncounterId = restored.CurrentEncounterId;
            _run.ParentEncounterTemplateId = restored.ParentEncounterTemplateId;
            _run.CurrentEncounterCoreCard = restored.State == ERunState.Encounter && !string.IsNullOrEmpty(restored.CurrentEncounterId)
                ? CreateEncounterCoreCard(restored.CurrentEncounterId)
                : null;
            _run.SelectionRules = RunState.CloneRules(restored.SelectionRules);
            _run.RerollCost = restored.RerollCost;
            _run.RerollsRemaining = restored.RerollsRemaining;
            _run.SelectionsMadeInCurrentContext = restored.SelectionsMadeInCurrentContext;
            _run.SelectionSet.Clear();

            if (exitingState == ERunState.LevelUp)
            {
                events.Add(new GameSimEventInterruptStateExited(ERunState.LevelUp, true));
            }

            if (!string.IsNullOrEmpty(restored.CurrentEncounterId))
            {
                string anchorEncId = ResolvePortraitAnchorEncounterId(restored.CurrentEncounterId);
                ActivateEncounterCard(anchorEncId, selectedCard: null, events, emitPurchaseIfSelected: false);
            }
            else
            {
                _run.ParentEncounterTemplateId = null;
                ActivateEncounterCard(null, selectedCard: null, events, emitPurchaseIfSelected: false);
            }

            string? transitionEncId = ResolveClientTransitionEncounterId(restored.CurrentEncounterId);

            if (restored.SuspendedCards.Count > 0)
            {
                foreach (var ce in restored.SuspendedCards)
                {
                    _run.Cards[ce.InstanceId] = ce;
                    _run.SelectionSet.Add(ce.InstanceId);
                    events.Add(new GameSimEventCardDealt(ce.InstanceId, ce.TemplateId.ToString(), ce.Type));
                }
                events.Add(new GameSimEventStateTransitioned(restored.State, transitionEncId));
            }
            else if (restored.SelectionSet.Count > 0 && restored.SelectionSet.All(id => _run.Cards.ContainsKey(id)))
            {
                foreach (string id in restored.SelectionSet)
                {
                    _run.SelectionSet.Add(id);
                    var ce = _run.Cards[id];
                    events.Add(new GameSimEventCardDealt(ce.InstanceId, ce.TemplateId.ToString(), ce.Type));
                }
                events.Add(new GameSimEventStateTransitioned(restored.State, transitionEncId));
            }
            else if (restored.State == ERunState.Encounter && !string.IsNullOrEmpty(restored.CurrentEncounterId))
            {
                _run.SelectionsMadeInCurrentContext = 0;
                var shelfEvents = DealEncounterShelfPreservingActiveEncounter(
                    restored.CurrentEncounterId,
                    isReroll: false,
                    playerState: BuildCorePlayerFromRun(),
                    ownedMaxTiers: _run.GetOwnedMaxTiers());
                NormalizeChildStepTransitionEvents(shelfEvents, restored.CurrentEncounterId);
                events.AddRange(shelfEvents);
                _run.RerollCost = restored.RerollCost;
                _run.RerollsRemaining = restored.RerollsRemaining;
                _run.SelectionRules = RunState.CloneRules(restored.SelectionRules);
            }
            else if (restored.State == ERunState.Choice)
            {
                _run.SelectionsMadeInCurrentContext = 0;
                events.AddRange(DealHourEncountersAndClearActive());
            }
            else
            {
                events.Add(new GameSimEventStateTransitioned(restored.State, transitionEncId));
            }
            if (_run.State == ERunState.Choice && _run.Hour == (uint)_mode.HoursInADay)
                EnsurePendingHour6PvpOpponentLocked(events);
            return;
        }

        AdvanceHourOrDay(events);
    }

    // ── 真实三段式战斗（BazaarCore.CombatSimulator + 50ms CombatSimConverter + 永久回写）──

    private void RunRealCombatSequence(Outcome o, CardPool.CardTemplate? encTpl, bool isPvp)
    {
        _shop.ActiveRules = _run.CustomRules;
        CleanupSelectionAndEncountersBeforeCombat(o.Events, preserveActiveMonsterPortrait: !isPvp);
        ReconcileAndVerifyBoardInvariants("PreCombat", o.DirtyCardIds, o.Events);

        // 1. 构建玩家 BazaarCore 权威战斗状态（保留已有 CoreCard 全部永久层与任务进度，避免光环双计）
        var (p1Core, _) = BuildCorePlayerAndMapFromRun();

        // 2. 解析锦标赛匹配（若处于锦标赛大厅）与敌方战斗状态
        var tourneyLobby = isPvp
            ? _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId)
            : null;
        MatchupAssignment? tourneyAssignment = null;
        bool isRealHumanOpp = isPvp; // 非锦标赛单人幽灵战沿用标准 Fates 二次致死规则
        bool botPrestigeLock = false;
        int combatSeed = _rng.Next(1, int.MaxValue);

        if (isPvp && tourneyLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId))
        {
            tourneyAssignment = _lobbies!.EnsureAndGetAssignment(tourneyLobby, _run.Day, _run.AccountId, _sessions);
            if (tourneyAssignment != null)
            {
                isRealHumanOpp = !tourneyAssignment.OpponentIsBot;
                botPrestigeLock = tourneyLobby.Matchmaker.ShouldSuppressLethalElimination(
                    _run.AccountId, _run.Day, _run.CustomRules);
                combatSeed = tourneyAssignment.BattleSeed;
            }
        }

        // 3. 构建对手 BazaarCore 战斗状态 + 客户端展示用的临时 Opponent 卡牌实体
        PlayerState p2Core;
        var oppEntries = new List<RunState.CardEntry>();
        SimPvpOpponent? pvpInfo = null;
        Data.MonsterTemplate? monster = null;
        int oppGold = 0;
        int oppIncome = Math.Max(1, tourneyLobby?.Rules?.StartingIncome ?? _run.CustomRules?.StartingIncome ?? _mode.StartingIncome);

        if (isPvp)
        {
            RunState? liveHumanOppRun = null;
            if (tourneyAssignment != null && !tourneyAssignment.OpponentIsBot && _sessions != null)
            {
                var oppMem = _sessions.GetByAccount(tourneyAssignment.OpponentAccountId);
                var candidateRun = oppMem != null ? Endpoints.SessionRegistry.Get(oppMem.Sid)?.Run : null;
                if (candidateRun != null &&
                    candidateRun.Day >= _run.Day &&
                    candidateRun.Cards.Values.Any(c =>
                        c.Owner == ECombatantId.Player &&
                        c.Type == ECardType.Item &&
                        c.Section != EInventorySection.Stash))
                {
                    liveHumanOppRun = candidateRun;
                }
            }

            if (liveHumanOppRun != null && tourneyAssignment != null)
            {
                p2Core = BuildOpponentPlayerStateFromRun(liveHumanOppRun, tourneyAssignment.OpponentUsername);
                oppGold = Math.Max(0, liveHumanOppRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0));
                int liveInc = liveHumanOppRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, oppIncome);
                oppIncome = liveInc > 0 ? liveInc : oppIncome;
                pvpInfo = new SimPvpOpponent(
                    name: tourneyAssignment.OpponentUsername,
                    titlePrefix: "",
                    titleSuffix: "",
                    rank: null,
                    rating: 1200,
                    division: 1,
                    victories: liveHumanOppRun.Victories,
                    prestige: Math.Max(1, liveHumanOppRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20)),
                    level: Math.Max(1, liveHumanOppRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1)),
                    hero: liveHumanOppRun.Hero == EHero.Common ? EHero.Vanessa : liveHumanOppRun.Hero,
                    playerLoadoutResponse: new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout { accountId = tourneyAssignment.OpponentAccountId },
                    playerCollection: null);
                Console.WriteLine($"[RunEngine] PVP Match Day={_run.Day}: vs LIVE HUMAN {tourneyAssignment.OpponentUsername} (HP={p2Core.HealthMax}, items={p2Core.Hand.Cards.Count})");
            }
            else
            {
                int expectedBucketDay = Math.Clamp((int)_run.Day, 1, 15);
                var lockedGhost = _run.PendingPvpGhost;
                bool canUseLockedGhost = lockedGhost != null
                                      && lockedGhost.Day == expectedBucketDay
                                      && (_run.IsCustomRuleRun || (!lockedGhost.IsCustomRule && Storage.Persistence.IsSaneGhostForDay(expectedBucketDay, lockedGhost.HealthMax)));
                var ghostRng = tourneyAssignment != null ? new Random(combatSeed) : _rng;
                var ghost = canUseLockedGhost
                    ? lockedGhost!
                    : _ghosts.PickOpponent(
                        (int)_run.Day,
                        (int)_run.Victories,
                        (int)_run.Defeats,
                        ghostRng,
                        _repo,
                        includeCustomRuleGhosts: _run.IsCustomRuleRun);
                _run.PendingPvpGhost = ghost;
                p2Core = PvpGhostPool.ToPlayerState(ghost, _repo);
                if (p2Core.Hand.Cards.Count == 0)
                {
                    var fallbackTpl = _repo.AlwaysSpawnableItems.FirstOrDefault() ?? _repo.CardsById.Values.FirstOrDefault(c => c.Type == "Item");
                    if (fallbackTpl != null)
                    {
                        p2Core.Hand.TryAddAuto(new CardInstance(fallbackTpl, CardTier.Bronze, null, "pvp_fb_0"));
                        p2Core.PrepareForCombat();
                    }
                }
                if (tourneyLobby != null && tourneyAssignment != null && tourneyAssignment.OpponentIsBot)
                {
                    // #15 锦标赛 AI 机器人公平同步房间开局预设（血量差值、初始装备、初始技能）
                    ApplyTournamentRulesToAiBot(p2Core, tourneyLobby.Rules);
                }
                string oppAccountId = tourneyAssignment?.OpponentAccountId
                    ?? _run.CurrentPvpOpponent?.PlayerLoadout?.accountId
                    ?? (!string.IsNullOrWhiteSpace(ghost.BoardId) ? $"ghost_{ghost.BoardId}" : $"ghost_{Guid.NewGuid():N}");
                string oppDisplayName = tourneyAssignment != null
                    ? tourneyAssignment.OpponentUsername
                    : (string.IsNullOrWhiteSpace(ghost.Hero) ? "Challenger" : $"{ghost.Hero} ({ghost.PowerBucket})");
                EHero oppHero = PvpGhostPool.ParseHero(ghost.Hero);
                pvpInfo = new SimPvpOpponent(
                    name: oppDisplayName,
                    titlePrefix: "",
                    titleSuffix: "",
                    rank: null,
                    rating: 1000 + (int)(ghost.EstimatedWinRate * 500),
                    division: 1,
                    victories: (uint)Math.Max(0, ghost.Wins),
                    prestige: Math.Max(1, ghost.Prestige),
                    level: Math.Max(1, ghost.Level),
                    hero: oppHero,
                    playerLoadoutResponse: new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout { accountId = oppAccountId },
                    playerCollection: null);

                Console.WriteLine($"[RunEngine] PVP Match Day={_run.Day}: vs {oppDisplayName} [{ghost.BoardId}] (Bot={tourneyAssignment?.OpponentIsBot}, CustomPool={_run.IsCustomRuleRun}, HP={p2Core.HealthMax}, items={p2Core.Hand.Cards.Count})");
            }
        }
        else
        {
            monster = ResolveMonster(encTpl);
            if (monster != null && _repo.MonstersById.TryGetValue(monster.Id, out var coreMonster))
            {
                p2Core = CombatSimulator.BuildMonsterPlayerState(_repo, coreMonster);
            }
            else
            {
                p2Core = BuildFallbackMonsterPlayer();
            }

            Console.WriteLine($"[RunEngine] PVE Match Day={_run.Day}: vs {p2Core.DisplayName} (HP={p2Core.HealthMax}, items={p2Core.Hand.Cards.Count})");
        }

        // 统一保障敌方（PvP 镜像与野怪）所有上阵物品/技能/槽位效果具备唯一 InstanceId，
        // 避免同模板多实例或外部传入重复键在战斗模拟及构表阶段发生碰撞；同时严禁任何 Encounter 卡混入 oppEntries。
        EnsureUniqueOpponentCardIds(p2Core);
        var usedOppEntryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in p2Core.Hand.Cards)
        {
            if (IsEncounterTypeName(c.Template.Type)) continue;
            oppEntries.Add(CreateTransientOpponentEntry(c, ECardType.Item, EInventorySection.Hand, (EContainerSocketId)Math.Clamp(c.StartSlot, 0, 9), usedOppEntryIds));
        }
        foreach (var s in p2Core.Skills)
        {
            if (IsEncounterTypeName(s.Template.Type)) continue;
            oppEntries.Add(CreateTransientOpponentEntry(s, ECardType.Skill, null, null, usedOppEntryIds));
        }
        foreach (var se in p2Core.SocketEffects)
        {
            if (IsEncounterTypeName(se.Template.Type)) continue;
            int seSlot = se.StartSlot is >= 0 and <= 9 ? se.StartSlot : 0;
            oppEntries.Add(CreateTransientOpponentEntry(se, ECardType.SocketEffect, EInventorySection.Hand, (EContainerSocketId)seSlot, usedOppEntryIds));
        }

        _lastCombatMonster = monster;

        // 4. 确定性执行 BazaarCore 完整规则战斗模拟
        var result = CombatSimulator.Simulate(
            _repo,
            p1Core,
            p2Core,
            seed: combatSeed,
            combatType: isPvp ? "PVP" : "PVE",
            recordTimeline: true,
            recordEvents: true);

        bool win = string.Equals(result.WinnerPlayerId, "player", StringComparison.OrdinalIgnoreCase);

        // 4. 预先计算 PvE 胜负金币/经验，供 CombatSimConverter 生成血条刻度与跳币/飞经验事件
        //    战斗小时（Hour 3 野怪与 Hour 6 PvP）经验仅源自 RewardCombatXp，绝不额外叠加 RewardVictory 或 ExperiencePerHour
        _run.GainedHourXpThisHour = true;
        int goldGain;
        int xpGain;
        int pveGoldForSim = 0;
        int pveXpForSim = 0;

        if (isPvp)
        {
            goldGain = encTpl?.RewardCombatGold ?? 0;
            xpGain = encTpl?.RewardCombatXp ?? 3;
        }
        else
        {
            int monsterMaxHp = Math.Max(1, p2Core.HealthMax);
            int monsterRemHp = Math.Clamp(result.Player2RemainingHp, 0, monsterMaxHp);
            if (win)
            {
                int baseCombatGold = encTpl?.RewardCombatGold ?? 3;
                int baseCombatXp = encTpl?.RewardCombatXp ?? 3;
                int victoryGold = encTpl?.RewardVictory?.GoldReward ?? encTpl?.RewardVictoryGold ?? 0;
                goldGain = baseCombatGold + victoryGold;
                xpGain = baseCombatXp;
            }
            else
            {
                var defeatRewards = ShopEngine.CalculateMonsterDefeatRewards(monsterMaxHp, monsterRemHp, encTpl);
                goldGain = defeatRewards.GoldAwarded;
                xpGain = defeatRewards.XpAwarded;
            }
            pveGoldForSim = goldGain;
            pveXpForSim = xpGain;
        }

        // 5. 构造第 1 条消息：SpawnMessage（必须带 Combat/PVPCombat 状态 + 敌方初始血量 + 敌方盘面卡）
        ERunState combatState = isPvp ? ERunState.PVPCombat : ERunState.Combat;
        _run.State = combatState;
        _run.CurrentPvpOpponent = pvpInfo;
        _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(
            p2Core.HealthMax, p2Core.HealthMax,
            prestige: pvpInfo?.Prestige ?? 20,
            level: p2Core.Level,
            gold: oppGold,
            income: oppIncome);

        var spawnEvents = new List<IGameSimEvent>(o.Events);
        o.Events.Clear();
        spawnEvents.Add(new GameSimEventPlayerInitialized
        {
            CombatantId = ECombatantId.Opponent,
            Hero = pvpInfo?.Hero ?? EHero.Common,
        });
        spawnEvents.Add(new GameSimEventStateTransitioned(combatState, _run.CurrentEncounterId));
        foreach (var oe in oppEntries)
        {
            spawnEvents.Add(new GameSimEventCardSpawned(
                oe.InstanceId,
                oe.TemplateId.ToString(),
                oe.Type,
                ECombatantId.Opponent,
                oe.Section,
                oe.Socket));
        }
        o.CombatSpawnSim = _run.BuildFullGameSim(spawnEvents, oppEntries, pvpInfo);

        // 6. 构造第 2 条消息：CombatSim（50ms 步长帧流 + 弹道 + PvE 跳币/经验事件 + 统计）
        var p1BoardEntries = _run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player &&
                        c.Type is ECardType.Item or ECardType.Skill or ECardType.SocketEffect)
            .ToList();
        var p1ByInst = BuildSafeCoreCardMap(p1BoardEntries, "p1");
        var p2ByInst = BuildSafeCoreCardMap(oppEntries, "p2");

        o.Combat = CombatSimConverter.ToCombatSim(
            result, _repo, p1ByInst, p2ByInst, p1BoardEntries, oppEntries,
            pveGoldReceived: pveGoldForSim,
            pveXpReceived: pveXpForSim);

        Console.WriteLine(
            $"[RunEngine] CombatSim done: {(win ? "WIN" : "LOSE")} dur={result.DurationMs}ms " +
            $"frames={o.Combat.Frames.Count} p1RemHp={result.Player1RemainingHp}/{p1Core.HealthMax} " +
            $"p2RemHp={result.Player2RemainingHp}/{p2Core.HealthMax} pveGold={pveGoldForSim} pveXp={pveXpForSim}");

        // 7. 构造第 3 条消息：DespawnMessage（清理敌方临时卡 + 战斗永久成长回写 + OnFightEnded + 奖励/战利品）
        var despawnEvents = new List<IGameSimEvent>();
        foreach (var oe in oppEntries)
            despawnEvents.Add(new GameSimEventCardDisposed(oe.InstanceId, ECardDisposalReason.Cleared));

        _run.PendingPvpGhost = null;
        _run.CurrentPvpOpponent = null;
        _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(100, 100, gold: 0, income: 5);

        // 7a. 战后永久状态回写（永久伤害/护盾/价值/附魔/升阶/任务进度/变形/衍生卡/玩家永久属性）
        var permSync = CombatSimulator.SyncPermanentChangesBack(result, p1Core);
        ApplyPostCombatPermanentSync(p1Core, permSync, despawnEvents);

        // 7a-2. 记录官方 [PlayerAttributes] 战斗胜负追踪（Custom_5 总胜场 / Custom_6 总败场 / Custom_7 PvE 败 / Custom_8 PvP 败）
        // 并同步战中可能变更的 Gold / Custom_3 / Custom_9 到 p1Core，确保 OnFightEnded 光环重算读取最新值
        if (win)
        {
            _run.IncrementPlayerAttribute(EPlayerAttributeType.Custom_5, 1);
            p1Core.Attributes["Custom_5"] = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_5, 0);
        }
        else
        {
            _run.IncrementPlayerAttribute(EPlayerAttributeType.Custom_6, 1);
            p1Core.Attributes["Custom_6"] = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_6, 0);
            EPlayerAttributeType splitLossAttr = isPvp ? EPlayerAttributeType.Custom_8 : EPlayerAttributeType.Custom_7;
            _run.IncrementPlayerAttribute(splitLossAttr, 1);
            p1Core.Attributes[splitLossAttr.ToString()] = _run.PlayerAttributes.GetValueOrDefault(splitLossAttr, 0);
        }
        p1Core.Attributes["Gold"] = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        p1Core.Attributes["Custom_3"] = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0);
        p1Core.Attributes["Custom_9"] = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_9, 0);

        // 7b. 派发 TTriggerOnFightEnded（必须传入 opponent: p2Core，使 Langxian 等含 TargetMode="Opponent" 条件的卡生效）
        var fightEndedBatch = OutOfCombatController.OnFightEnded(
            p1Core,
            combatType: isPvp ? "PVP" : "PVE",
            combatOutcome: win ? "Win" : "Lose",
            _repo,
            _rng,
            opponent: p2Core);
        ApplyOutOfCombatBatch(p1Core, fightEndedBatch, despawnEvents, syncBoardDiffs: true, xpType: EExperienceType.Combat);

        // 7b-2. 清除战斗临时状态（Freeze/Slow/Haste/Flying/Heated/Chilled/CombatAdd），并登记战后首帧显式归零下发
        ClearPostCombatTransientStatusAndSync(despawnEvents, o.DirtyCardIds);

        // 7c. 结算胜负、Fates（Scheme A 1-声望 AI Bot 锁血）与本场金币/经验
        bool triggeredFates = false;
        if (isPvp)
        {
            if (win)
            {
                _run.Victories++;
            }
            else
            {
                _run.Defeats++;
                int loss = (int)_run.Day;
                int rawPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
                int prestige = rawPrestige - loss;
                int clampedPrestige = Math.Max(0, prestige);

                if (clampedPrestige <= _mode.FatesSpawnAtOrBelowPrestige && !_run.FatesUsed)
                {
                    // Scheme A：首次声望 <= 0（无论对手是 AI 还是真人），均触发 Fates 并设置 Prestige = 1
                    _run.FatesUsed = true;
                    clampedPrestige = Math.Max(1, _mode.FatesAfterPrestige);
                    triggeredFates = true;
                    Console.WriteLine($"[RunEngine] FATES triggered! Prestige set to {clampedPrestige}, template={_mode.FatesTemplateId}");
                }
                else if (_run.FatesUsed && botPrestigeLock && !isRealHumanOpp && clampedPrestige <= 0)
                {
                    // Scheme A：Fates 已用过且对手为非致死 AI Bot 时，强制锁血 Prestige >= 1，绝不因 Bot 淘汰
                    clampedPrestige = 1;
                }

                int actualPrestigeDelta = clampedPrestige - rawPrestige;
                _run.PlayerAttributes[EPlayerAttributeType.Prestige] = clampedPrestige;
                despawnEvents.Add(new GameSimEventPlayerPrestigeChanged(actualPrestigeDelta));
            }
        }

        // 兜底：即使在 OnFightEnded 等阶段将 Prestige 扣至 <= 0 且尚未使用过 Fates，同样触发一次 Last Chance；
        // 若处于已使用 Fates 的 AI Bot 非致死锁血保护期，则兜底锁血 Prestige = 1
        if (!triggeredFates)
        {
            int curPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
            if (!_run.FatesUsed && curPrestige <= _mode.FatesSpawnAtOrBelowPrestige)
            {
                _run.FatesUsed = true;
                int rescued = Math.Max(1, _mode.FatesAfterPrestige);
                _run.PlayerAttributes[EPlayerAttributeType.Prestige] = rescued;
                despawnEvents.Add(new GameSimEventPlayerPrestigeChanged(rescued - curPrestige));
                triggeredFates = true;
                Console.WriteLine($"[RunEngine] FATES fallback triggered! Prestige {curPrestige} -> {rescued}");
            }
            else if (_run.FatesUsed && isPvp && botPrestigeLock && !isRealHumanOpp && curPrestige <= 0)
            {
                _run.PlayerAttributes[EPlayerAttributeType.Prestige] = 1;
                despawnEvents.Add(new GameSimEventPlayerPrestigeChanged(1 - curPrestige));
            }
        }

        if (goldGain > 0)
        {
            _run.AddPlayerGold(goldGain);
            RecalculateAurasAndSyncBoard(despawnEvents);
        }
        if (xpGain > 0)
        {
            GrantExperienceToList(despawnEvents, xpGain, EExperienceType.Combat);
        }

        if (isPvp && tourneyLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId) &&
            tourneyAssignment != null && !tourneyAssignment.OpponentIsBot)
        {
            tourneyLobby.Matchmaker.RecordHumanMatchResolution(
                _run.Day,
                _run.AccountId,
                tourneyAssignment.OpponentAccountId,
                win ? _run.AccountId : tourneyAssignment.OpponentAccountId,
                win ? 0 : (int)_run.Day,
                tourneyAssignment.BattleSeed);
        }

        // 7d. 检查胜负终局条件（含 LastHumanStandingMode）；未终局则进入 Fates / 战利品 / 升级 / 下一小时
        string fatesTemplateId = !string.IsNullOrWhiteSpace(_mode.FatesTemplateId)
            ? _mode.FatesTemplateId
            : "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
        bool ended = TryEndRunToEvents(despawnEvents, justFinishedPvp: isPvp);

        if (isPvp && tourneyLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId))
        {
            _lobbies!.RecordRunProgress(tourneyLobby, _run.AccountId, _run);
        }

        if (!ended)
        {
            if (triggeredFates &&
                (_pool.IsAssetValid(fatesTemplateId) || _repo.GetCard(fatesTemplateId) != null))
            {
                var fatesOutcome = new Outcome();
                TransitionToEncounterById(fatesTemplateId, fatesOutcome);
                despawnEvents.AddRange(fatesOutcome.Events);
            }
            else if (!isPvp && win)
            {
                _run.SelectionsMadeInCurrentContext = 0;
                // PvE 胜利 → 按 CustomRules.MonsterLootChooseOne（默认 true）决定分期任选 1 件或官方随机 1 掉落
                // 战利品阶段继续保留击败怪物的激活遭遇卡以展示顶栏头像
                var activeProtected = DetachActiveEncounterCardForShelfOp();
                despawnEvents.AddRange(_shop.DealMonsterVictoryLoot(
                    encTpl,
                    _lastCombatMonster,
                    p1Core,
                    chooseOne: _run.CustomRules.MonsterLootChooseOne));
                RestoreActiveEncounterCardAfterShelfOp(activeProtected);

                if (_run.State == ERunState.Loot && !_run.SelectionRules.CanExit)
                {
                    var safeLootRules = RunState.CloneRules(_run.SelectionRules);
                    safeLootRules.CanExit = true;
                    _run.SelectionRules = safeLootRules;
                }

                if (_run.State == ERunState.Loot && _run.SelectionSet.Count == 0)
                {
                    // #1 空战利品兜底：绝不停留在空 Loot 货架卡死，立刻销毁怪物头像卡并顺畅进入升级或下一小时
                    ActivateEncounterCard(null, selectedCard: null, despawnEvents, emitPurchaseIfSelected: false);
                    if (_run.PendingLevelUp)
                        ConsumeNextPendingLevelUpToEvents(despawnEvents);
                    else
                        AdvanceHourOrDay(despawnEvents);
                }
                else
                {
                    TriggerOnEncounterCardsDealtOnly(_run.CurrentEncounterId, despawnEvents);
                }
            }
            else if (_run.PendingLevelUp)
            {
                ActivateEncounterCard(null, selectedCard: null, despawnEvents, emitPurchaseIfSelected: false);
                ConsumeNextPendingLevelUpToEvents(despawnEvents);
            }
            else
            {
                ActivateEncounterCard(null, selectedCard: null, despawnEvents, emitPurchaseIfSelected: false);
                AdvanceHourOrDay(despawnEvents);
            }
        }
        else
        {
            ActivateEncounterCard(null, selectedCard: null, despawnEvents, emitPurchaseIfSelected: false);
        }

        // 在升级/跨天/掉落全部结算完毕后再次补满并同步玩家武器弹药，避免战后推进阶段提升 AmmoMax 后残留旧 Ammo
        RefillPostAdvanceAmmoAndSync(o.DirtyCardIds);
        ReconcileAndVerifyBoardInvariants("PostCombat", o.DirtyCardIds, despawnEvents);
        o.CombatDespawnSim = _run.BuildFullGameSim(despawnEvents);
    }

    private PlayerState BuildOpponentPlayerStateFromRun(RunState oppRun, string oppDisplayName)
    {
        int hpMax = Math.Max(100, oppRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250));
        int prestige = oppRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20);
        int level = Math.Max(1, oppRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1));
        string hero = (oppRun.Hero == EHero.Common ? EHero.Vanessa : oppRun.Hero).ToString();

        var ps = new PlayerState("pvp_" + oppRun.RunId, oppDisplayName, hero, hpMax, prestige, 0, 10)
        {
            Level = level,
            Wins = (int)oppRun.Victories,
            Losses = (int)oppRun.Defeats,
        };
        ps.Hand.UnlockedMask = oppRun.UnlockedSlots > 0 ? oppRun.UnlockedSlots : 120;
        ps.Stash.UnlockedMask = 1023;

        foreach (var entry in oppRun.Cards.Values
                     .Where(c => c.Owner == ECombatantId.Player)
                     .OrderBy(c => c.Socket.HasValue ? (int)c.Socket.Value : 99))
        {
            var src = entry.CoreCard;
            CardInstance clone;
            if (src != null)
            {
                clone = CloneCardWithUniqueId(src, "opp_" + entry.InstanceId);
            }
            else
            {
                var tpl = _repo.GetCard(entry.TemplateId.ToString());
                if (tpl == null) continue;
                clone = new CardInstance(tpl, ShopEngine.ToCoreTier(entry.Tier), entry.Enchantment?.ToString(), "opp_" + entry.InstanceId);
            }

            if (entry.Type == ECardType.Skill)
            {
                ps.Skills.Add(clone);
            }
            else if (entry.Type == ECardType.SocketEffect)
            {
                clone.StartSlot = entry.Socket.HasValue ? (int)entry.Socket.Value : 0;
                ps.SocketEffects.Add(clone);
            }
            else if (entry.Section != EInventorySection.Stash)
            {
                int slot = entry.Socket.HasValue ? (int)entry.Socket.Value : 3;
                if (!ps.Hand.TryPlaceOrShift(clone, slot))
                    ps.Hand.TryAddAuto(clone);
            }
        }

        ps.PrepareForCombat();
        return ps;
    }

    private (PlayerState Player, Dictionary<string, CardInstance> CardMap) BuildCorePlayerAndMapFromRun()
    {
        int hpMax = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth);
        int hp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, hpMax);
        int prestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
        int gold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, _mode.StartingGold);
        int level = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1);

        EnsureRunExclusionSetInitialized();
        if (IsTournamentRun())
            _run.PlayerAttributes[EPlayerAttributeType.Custom_11] = 1;
        var ps = new PlayerState("player", "Player", _run.Hero.ToString(), hpMax, prestige, gold, 10)
        {
            Day = (int)_run.Day,
            Hour = (int)_run.Hour,
            Level = level,
            Wins = (int)_run.Victories,
            Losses = (int)_run.Defeats,
            FatesUsed = _run.FatesUsed,
        };
        if (_run.ExclusionSetInitialized)
        {
            ps.InitializeExclusionSet(null);
            foreach (string exId in _run.ExclusionSet)
                ps.ExclusionSet.Add(exId);
        }

        // P0-1：同时从 _run.PlayerAttributes 剥离小时/天临时层以及上轮光环加成层，
        // 把基础值写入 ps.Attributes，把光环与临时层分别放回 AuraAdd/AuraMult 与 Hour/Day 字段，确保 AuraEngine 完全幂等。
        foreach (var (ak, av) in _run.PlayerAttributes)
        {
            string key = ak.ToString();
            double baseVal = av;
            double mult = _playerHourMult.GetValueOrDefault(key, 1.0)
                        * _playerDayMult.GetValueOrDefault(key, 1.0)
                        * _run.PlayerAuraMult.GetValueOrDefault(key, 1.0);
            if (Math.Abs(mult) > 1e-6 && Math.Abs(mult - 1.0) > 1e-6)
                baseVal /= mult;
            baseVal -= _playerHourAdd.GetValueOrDefault(key, 0.0)
                     + _playerDayAdd.GetValueOrDefault(key, 0.0)
                     + _run.PlayerAuraAdd.GetValueOrDefault(key, 0.0);
            ps.Attributes[key] = baseVal;
        }
        foreach (var (k, v) in _playerHourAdd) ps.HourAdd[k] = v;
        foreach (var (k, v) in _playerHourMult) ps.HourMult[k] = v;
        foreach (var (k, v) in _playerDayAdd) ps.DayAdd[k] = v;
        foreach (var (k, v) in _playerDayMult) ps.DayMult[k] = v;
        foreach (var (k, v) in _run.PlayerAuraAdd) ps.AuraAdd[k] = v;
        foreach (var (k, v) in _run.PlayerAuraMult) ps.AuraMult[k] = v;

        ps.CurrentHealth = Math.Clamp(hp, 0, Math.Max(1, ps.HealthMax));
        ps.Hand.UnlockedMask = _run.UnlockedSlots;
        ps.Stash.UnlockedMask = 1023;

        var map = new Dictionary<string, CardInstance>(StringComparer.Ordinal);
        foreach (var entry in _run.Cards.Values
                     .Where(c => c.Owner == ECombatantId.Player)
                     .OrderBy(c => c.Socket.HasValue ? (int)c.Socket.Value : 99))
        {
            var ci = EnsureCoreCard(entry);
            map[entry.InstanceId] = ci;

            if (entry.Type == ECardType.Skill)
            {
                ps.Skills.Add(ci);
            }
            else if (entry.Type == ECardType.SocketEffect)
            {
                ps.SocketEffects.Add(ci);
            }
            else if (entry.Section == EInventorySection.Stash)
            {
                int slot = entry.Socket.HasValue ? (int)entry.Socket.Value : 0;
                if (!ps.Stash.TryPlaceOrShift(ci, slot)) ps.Stash.TryAddAuto(ci);
            }
            else
            {
                int slot = entry.Socket.HasValue ? (int)entry.Socket.Value : 3;
                if (!ps.Hand.TryPlaceOrShift(ci, slot)) ps.Hand.TryAddAuto(ci);
            }
        }

        // 不在这里调用 ps.PrepareForCombat()，保留局外真实 CurrentHealth 及已累计的临时/永久层；
        // CombatSimulator.Simulate 内部 CloneForCombat() 会为战斗克隆体独立 PrepareForCombat()。
        return (ps, map);
    }

    private PlayerState BuildCorePlayerFromRun() => BuildCorePlayerAndMapFromRun().Player;

    private static void EnsureUniqueOpponentCardIds(PlayerState p2Core)
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        int uniqSeq = 0;

        EnsureUniqueListIds(p2Core.Hand.Cards, "opp", usedIds, ref uniqSeq);
        EnsureUniqueListIds(p2Core.Skills, "oppskl", usedIds, ref uniqSeq);
        EnsureUniqueListIds(p2Core.SocketEffects, "oppse", usedIds, ref uniqSeq);
    }

    private static void EnsureUniqueListIds(
        IList<CardInstance> cards,
        string prefixTag,
        HashSet<string> usedIds,
        ref int uniqSeq)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (!string.IsNullOrWhiteSpace(card.InstanceId) && usedIds.Add(card.InstanceId))
                continue;

            string tplPrefix = card.Template.Id.Length >= 6 ? card.Template.Id[..6] : card.Template.Id;
            string newId;
            do
            {
                newId = $"{prefixTag}_u{uniqSeq++}_{tplPrefix}";
            }
            while (!usedIds.Add(newId));

            cards[i] = CloneCardWithUniqueId(card, newId);
        }
    }

    private static CardInstance CloneCardWithUniqueId(CardInstance src, string newInstanceId)
    {
        var copy = new CardInstance(src.Template, src.PermanentTier, src.PermanentEnchantment, newInstanceId)
        {
            StartSlot = src.StartSlot,
            IsFlying = src.IsFlying,
            Heated = src.Heated,
            Chilled = src.Chilled,
            TransformReplacementTemplateId = src.TransformReplacementTemplateId,
            TransformedFromInstanceId = src.TransformedFromInstanceId,
            IsCombatTemporary = src.IsCombatTemporary,
        };
        foreach (var (k, v) in src.PermanentAdd) copy.PermanentAdd[k] = v;
        foreach (var (k, v) in src.PermanentMult) copy.PermanentMult[k] = v;
        foreach (var (k, v) in src.HourAdd) copy.HourAdd[k] = v;
        foreach (var (k, v) in src.HourMult) copy.HourMult[k] = v;
        foreach (var (k, v) in src.DayAdd) copy.DayAdd[k] = v;
        foreach (var (k, v) in src.DayMult) copy.DayMult[k] = v;
        foreach (var t in src.ExtraTags) copy.ExtraTags.Add(t);
        foreach (var (k, v) in src.QuestProgress) copy.QuestProgress[k] = v;
        foreach (var c in src.CompletedQuests) copy.CompletedQuests.Add(c);
        if (src.Tier != src.PermanentTier)
            copy.SetTier(src.Tier, isTemporary: true);
        copy.RebuildTierAndEnchantmentData();
        copy.ResetForCombat(null);
        return copy;
    }

    private static Dictionary<string, CardInstance> BuildSafeCoreCardMap(
        IEnumerable<RunState.CardEntry> entries,
        string sidePrefix)
    {
        var map = new Dictionary<string, CardInstance>(StringComparer.Ordinal);
        int dupSeq = 0;

        foreach (var entry in entries)
        {
            if (entry.CoreCard == null) continue;

            string key = entry.InstanceId;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = !string.IsNullOrWhiteSpace(entry.CoreCard.InstanceId)
                    ? entry.CoreCard.InstanceId
                    : $"{sidePrefix}_anon_{dupSeq++}";
            }

            if (map.TryGetValue(key, out var existing))
            {
                if (ReferenceEquals(existing, entry.CoreCard))
                    continue;

                string coreId = entry.CoreCard.InstanceId;
                if (!string.IsNullOrWhiteSpace(coreId) && !map.ContainsKey(coreId))
                {
                    key = coreId;
                }
                else
                {
                    string baseKey = !string.IsNullOrWhiteSpace(coreId) ? coreId : key;
                    do
                    {
                        key = $"{baseKey}__dup{dupSeq++}";
                    }
                    while (map.ContainsKey(key));
                }
            }

            map[key] = entry.CoreCard;
        }

        return map;
    }

    private RunState.CardEntry CreateTransientOpponentEntry(
        CardInstance ci,
        ECardType type,
        EInventorySection? section,
        EContainerSocketId? socket,
        HashSet<string>? usedIds = null)
    {
        string instId = ci.InstanceId;
        if (usedIds != null)
        {
            if (string.IsNullOrWhiteSpace(instId) || !usedIds.Add(instId))
            {
                string baseId = string.IsNullOrWhiteSpace(instId) ? "opp_entry" : instId;
                int seq = 0;
                do
                {
                    instId = $"{baseId}_e{seq++}";
                }
                while (!usedIds.Add(instId));
            }
        }

        var tpl = _pool.Get(ci.Template.Id);
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(ci.Template.Id),
            Type = type,
            Size = ShopEngine.ParseSize(ci.Template.Size),
            Tier = ShopEngine.ParseTier(ci.Tier.ToString()),
            Enchantment = Enum.TryParse<EEnchantmentType>(ci.Enchantment, true, out var ench) ? ench : null,
            Owner = ECombatantId.Opponent,
            Section = section,
            Socket = socket,
            CoreCard = ci,
        };
        RunState.PopulateMetaFromTemplate(entry, tpl, ci.Template);
        _run.SyncEntryFromCoreCardAndDiff(entry, _repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        return entry;
    }

    private Data.MonsterTemplate? ResolveMonster(CardPool.CardTemplate? encTpl)
    {
        if (encTpl?.MonsterTemplateId != null)
        {
            var exact = _monsters.FirstOrDefault(m => m.Id == encTpl.MonsterTemplateId);
            if (exact != null) return exact;
        }
        int targetHp = 80 + (int)_run.Day * 120;
        return _monsters
            .Where(m => m.HealthMax > 0 && m.Items.Count > 0)
            .OrderBy(m => Math.Abs(m.HealthMax - targetHp))
            .FirstOrDefault();
    }

    private PlayerState BuildFallbackMonsterPlayer()
    {
        int hp = 100 + (int)_run.Day * 80;
        var ps = new PlayerState("monster_fb", "野怪", "Common", hp, 20, 0, 10) { IsMonster = true };
        var item = _repo.AlwaysSpawnableItems.FirstOrDefault();
        if (item != null) ps.Hand.TryAddAuto(new CardInstance(item, CardTier.Bronze, null, "opp_fb_0"));
        ps.PrepareForCombat();
        return ps;
    }

    // ── 经验、升级、推进小时与终局条件 ───────────────────────────────────────

    private void GrantExperience(Outcome o, int xpGain, EExperienceType expType)
        => GrantExperienceToList(o.Events, xpGain, expType);

    private void GrantExperienceToList(List<IGameSimEvent> events, int xpGain, EExperienceType expType)
    {
        if (xpGain <= 0) return;
        events.Add(new GameSimEventPlayerExperienceGained((uint)xpGain, expType));
        int xp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0) + xpGain;
        int lvl = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1);

        int req = Math.Max(1, _mode.ExperiencePerLevel);
        var leveledTo = new List<int>();
        while (xp >= req)
        {
            xp -= req;
            lvl++;
            int hpInc = _levelUps.HealthIncreaseFor(lvl);
            if (hpInc <= 0)
                hpInc = _levelUps.MaxLevel > 0 ? _levelUps.HealthIncreaseFor(_levelUps.MaxLevel) : 150;

            int newMax = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth) + hpInc;
            _run.PlayerAttributes[EPlayerAttributeType.HealthMax] = newMax;
            _run.PlayerAttributes[EPlayerAttributeType.Health] = newMax;
            _run.PendingLevelUps++;

            var unlocked = _run.UnlockCenteredSockets(Math.Max(2, _mode.NumCarpetSocketsUnlockPerLevel));
            _run.HandSocketUnlockMask = _run.UnlockedSlots;
            if (unlocked.Count > 0)
            {
                events.Add(new GameSimEventSocketsUnlocked(unlocked, _run.UnlockedSlots, EInventorySection.Hand));
            }
            Console.WriteLine($"[RunEngine] LEVEL UP -> L{lvl} MaxHP={newMax} PendingLevelUps={_run.PendingLevelUps} UnlockedMask=0x{_run.UnlockedSlots:X}");
            leveledTo.Add(lvl);
        }

        _run.PlayerAttributes[EPlayerAttributeType.Level] = lvl;
        _run.PlayerAttributes[EPlayerAttributeType.Experience] = xp;

        // 先落盘最新 Level / Experience，再按升阶顺序逐级触发局外升级效应与面板同步（避免回调嵌套加经验时被外层覆盖）
        foreach (int reachedLvl in leveledTo)
        {
            var p1Lvl = BuildCorePlayerFromRun();
            p1Lvl.Level = reachedLvl;
            var encCore = GetOrCreateCurrentEncounterCoreCard(_run.CurrentEncounterId);
            var lvlBatch = OutOfCombatController.OnPlayerLeveledUp(
                p1Lvl,
                _repo,
                _rng,
                newLevel: reachedLvl,
                currentEncounterId: _run.CurrentEncounterId,
                activeEncounterCard: _run.CurrentEncounterCoreCard ?? encCore);
            ApplyOutOfCombatBatch(p1Lvl, lvlBatch, events, syncBoardDiffs: true, xpType: expType);
        }
    }

    private void AdvanceHour(Outcome o) => AdvanceHourOrDay(o.Events);

    private void AdvanceHourToEvents(List<IGameSimEvent> events) => AdvanceHourOrDay(events);

    private void AdvanceHourOrDay(List<IGameSimEvent> events)
    {
        // 1. 切小时前清理 UntilEndOfHour 临时层并回写同步事件
        ClearHourModifiersOnRun(events);

        uint nextHour = _run.Hour + 1;
        bool newDay = nextHour > (uint)_mode.HoursInADay;
        if (newDay && _run.Victories + _run.Defeats < _run.Day)
        {
            // 每日第 6 小时必打 PvP 硬性兜底保护：未结算本日 PvP 绝不允许跨天
            uint pvpHour = (uint)_mode.HoursInADay;
            Console.WriteLine($"[RunEngine][WARN] Prevented skipping Day {_run.Day} Hour {pvpHour} PvP! (V={_run.Victories}, D={_run.Defeats}, Day={_run.Day})");
            _run.Hour = pvpHour;
            _run.RerollCost = 0;
            _run.RerollsRemaining = 0;
            events.Add(new GameSimEventRunHourChanged(_run.Hour));
            if (_run.PendingLevelUp)
            {
                PushChoiceAndEnterLevelUp(events);
                return;
            }
            _run.SelectionsMadeInCurrentContext = 0;
            events.AddRange(DealHourEncountersAndClearActive());
            return;
        }

        // 记录刚刚完成的旧小时是否尚未领取固定小时推进经验（独立于遭遇自身 ExperienceAwardUponSelection 与战斗 XP），
        // 然后切往新小时并重置新小时双门控
        bool needAdvanceHourXp = !_run.GainedAdvanceHourXpThisHour;
        _run.Hour = nextHour;
        _run.RerollCost = 2;
        _run.RerollsRemaining = 3;
        _run.GainedHourXpThisHour = false;
        _run.GainedAdvanceHourXpThisHour = false;

        if (newDay)
        {
            // V7-1.9 严格跨天顺序：
            // 1) 清理 UntilEndOfDay 临时层（并同步回写属性/附魔还原事件）与当日已访问遭遇集合
            ClearDayModifiersOnRun(events);
            _run.VisitedDayEncounterIds.Clear();
            _run.PendingPvpGhost = null;
            _run.CurrentPvpOpponent = null;

            // 2) 推进 Day 并重置 Hour = 1
            _run.Hour = 1;
            _run.Day++;
            events.Add(new GameSimEventRunDayChanged(_run.Day));

            // 3) 先发放当日 Income 收入到 Gold，确保 OnDayStarted() 触发器读取到最新金币
            int income = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, 0);
            if (income > 0)
            {
                _run.AddPlayerGold(income);
                events.Add(new GameSimEventPlayerIncomeGained((uint)income));
            }

            // 4) 跨天派发 OnDayStarted（必须 syncBoardDiffs: true，确保奥秘之书 Book of Secrets 等跨天生成的技能与属性变更立即同步客户端）
            var p1Day = BuildCorePlayerFromRun();
            var dayBatch = OutOfCombatController.OnDayStarted(
                p1Day, _repo, _rng, clearEndOfDayModifiers: false,
                currentEncounterId: _run.CurrentEncounterId,
                activeEncounterCard: _run.CurrentEncounterCoreCard);
            ApplyOutOfCombatBatch(p1Day, dayBatch, events, syncBoardDiffs: true, xpType: EExperienceType.Hour);
        }

        events.Add(new GameSimEventRunHourChanged(_run.Hour));

        // 2. 新小时开始派发 TTriggerOnHourStarted（产衍生卡/涨属性/重算光环）
        var p1Hour = BuildCorePlayerFromRun();
        var hourBatch = OutOfCombatController.OnHourStarted(
            p1Hour, _repo, _rng, clearEndOfHourModifiers: false,
            currentEncounterId: _run.CurrentEncounterId,
            activeEncounterCard: _run.CurrentEncounterCoreCard);
        ApplyOutOfCombatBatch(p1Hour, hourBatch, events, syncBoardDiffs: true, xpType: EExperienceType.Hour);

        // 3. #4 只要推进了常规小时（1->2..6->次日1），基于独立小时推进门控发放固定 ExperiencePerHour（+1 XP），不被商店/野怪经验吞掉
        if (needAdvanceHourXp && _mode.ExperiencePerHour > 0)
        {
            GrantExperienceToList(events, _mode.ExperiencePerHour, EExperienceType.Hour);
        }

        // 4. 若小时推进时升级经验足够，先把新小时的 Choice 现场压入 SuspendedStates 再进入 LevelUp，
        //    确保升级结束后恢复至新小时 Choice；Day 6/9/10 Hour 1 持对应探险门票时直接在 Choice 下发第 4 张探险入口卡。
        //    确保选完升级奖励后恢复到本小时的 Choice，而不是再推进一小时！
        if (_run.PendingLevelUp)
        {
            PushChoiceAndEnterLevelUp(events);
            return;
        }

        _run.SelectionsMadeInCurrentContext = 0;
        events.AddRange(DealHourEncountersAndClearActive());
    }

    private bool TryEndRunToEvents(List<IGameSimEvent> events, bool justFinishedPvp = false)
    {
        if (_runEnded) return true;

        int victories = (int)_run.Victories;
        int prestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
        int day = (int)_run.Day;

        if (prestige <= 0 && _run.FatesUsed)
        {
            return CrownDefeat(events, prestige, day);
        }

        var tourneyLobby = _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId);
        bool suppressTenWinForLastHumanStanding = false;
        bool forceLastHumanStandingWin = false;

        if (tourneyLobby != null && _run.CustomRules.LastHumanStandingMode)
        {
            var (totalHumans, survivingHumans) = _lobbies!.CountHumanStanding(
                tourneyLobby, _run.AccountId, _run, _sessions);
            if (totalHumans >= 2)
            {
                if (survivingHumans >= 2)
                    suppressTenWinForLastHumanStanding = true;
                else if (survivingHumans == 1 && prestige > 0)
                    forceLastHumanStandingWin = true;
            }
        }

        if (forceLastHumanStandingWin || (!suppressTenWinForLastHumanStanding && victories >= _mode.VictoriesToWin))
        {
            return CrownVictory(events, victories, day);
        }

        // 安全上限防止异常无限挂起（打满第 25 天 Hour 6 PvP 即在当日终局，保证 V+L == Day）
        if (day > 25 || (justFinishedPvp && day >= 25))
        {
            return CrownDefeat(events, prestige, day);
        }

        return false;
    }

    /// <summary>
    /// 按官方 RunRewardLevels 口径计算单局结算宝箱数：
    ///   · Ranked：4 胜 = 1，7 胜 = 2，10 胜 = 3；
    ///   · Unranked / 其他模式：4+ 胜 = 1；
    ///   · 未满 4 胜或主动放弃（isConceded）= 0。
    /// </summary>
    public static int ComputeRunChestCount(EPlayMode playMode, int victories, bool isConceded = false)
    {
        if (isConceded || victories < 4) return 0;
        if (playMode == EPlayMode.Ranked)
        {
            if (victories >= 10) return 3;
            if (victories >= 7) return 2;
            return 1;
        }
        return 1;
    }

    private bool CrownVictory(List<IGameSimEvent> events, int victories, int day)
    {
        _run.State = ERunState.EndRunVictory;
        _runEnded = true;
        int chests = ComputeRunChestCount(_run.PlayMode, victories, isConceded: false);
        events.Add(new GameSimEventStateTransitioned(ERunState.EndRunVictory, null));
        events.Add(new GameSimEventRunCompleted(new CompleteRunResult
        {
            XpEarned = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0),
            ChestCount = chests,
        }));
        Console.WriteLine($"[RunEngine] RUN END: VICTORY (wins={victories}/{_mode.VictoriesToWin} day={day} chests={chests})");
        return true;
    }

    private bool CrownDefeat(List<IGameSimEvent> events, int prestige, int day)
    {
        _run.State = ERunState.EndRunDefeat;
        _runEnded = true;
        int chests = ComputeRunChestCount(_run.PlayMode, (int)_run.Victories, isConceded: false);
        events.Add(new GameSimEventStateTransitioned(ERunState.EndRunDefeat, null));
        events.Add(new GameSimEventRunCompleted(new CompleteRunResult
        {
            XpEarned = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0),
            ChestCount = chests,
        }));
        Console.WriteLine($"[RunEngine] RUN END: DEFEAT (prestige={prestige} fatesUsed={_run.FatesUsed} day={day} wins={_run.Victories} chests={chests})");
        return true;
    }

    // ── 卖 / 基座 / GM / 放弃 ───────────────────────────────────────────────

    private Outcome OnSell(SellCardCommand cmd)
    {
        if (_runEnded || _run.State is ERunState.Combat or ERunState.PVPCombat
            or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            LogBoardAudit("SellCard", cmd.InstanceId.Value, null, null, null, $"not allowed in state {_run.State}");
            return NotAllowed($"SellCard not allowed in state {_run.State}");
        }
        string id = cmd.InstanceId.Value;
        if (!_run.Cards.TryGetValue(id, out var card))
        {
            LogBoardAudit("SellCard", id, null, null, null, "card not found");
            return NotFound("card not found: " + id);
        }
        if (card.Owner != ECombatantId.Player)
        {
            LogBoardAudit("SellCard", id, card.TemplateId.ToString(), $"{card.Section}:{card.Socket}", null, "not owned by player");
            return NotAllowed("cannot sell non-player card: " + id);
        }
        if (IsCardUnsellable(card))
        {
            LogBoardAudit("SellCard", id, card.TemplateId.ToString(), $"{card.Section}:{card.Socket}", null, "unsellable");
            var reject = NotAllowed($"card {id} is unsellable");
            if (card.Type == ECardType.Item)
                ReconcileAndVerifyBoardInvariants("SellUnsellableReject", reject.DirtyCardIds, reject.Events);
            reject.DirtyCardIds.Add(card.InstanceId);
            if (card.Type == ECardType.Item && card.Section.HasValue && card.Socket.HasValue)
                EmitMoveEventIfNotPresent(reject.Events, card.InstanceId, card.Section.Value, card.Socket.Value);
            return reject;
        }

        var o = new Outcome();
        if (card.Type == ECardType.Item)
        {
            ReconcileAndVerifyBoardInvariants("PreSell", o.DirtyCardIds, o.Events);
        }

        string fromLoc = $"{card.Section}:{card.Socket}";
        var coreCard = EnsureCoreCard(card);
        card.State = ECardState.Alive;
        coreCard.IsDestroyed = false;
        coreCard.IsDisabled = false;
        coreCard.FreezeMs = 0;
        if (card.Socket.HasValue && card.Type == ECardType.Item)
            coreCard.StartSlot = (int)card.Socket.Value;

        int sell = Math.Max(0, coreCard.GetSellPrice(_repo.GameMode.StandardPrices));
        if (sell == 0 && card.Attributes.TryGetValue(ECardAttributeType.SellPrice, out int fallbackSell) && fallbackSell > 0)
            sell = fallbackSell;

        // 关键顺序：必须在把 card 从 HandGrid/StashGrid 摘除前，携带原始 StartSlot 与卖前 Gold 调用 OutOfCombatController.OnCardSoldOrConsumed（含 activeEncounterCard）
        var p1Core = BuildCorePlayerFromRun();
        var soldBatch = OutOfCombatController.OnCardSoldOrConsumed(
            p1Core, coreCard, _repo, _rng, removeAfterTrigger: true,
            currentEncounterId: _run.CurrentEncounterId,
            activeEncounterCard: _run.CurrentEncounterCoreCard);

        // 先从权威网格与牌库摘除被卖卡（释放 Hand/Stash 槽位），再应用触发器产卡/属性变更并累加基础售价 sell
        RemoveOwnedEntryFromRun(card);
        ApplyOutOfCombatBatch(
            p1Core,
            soldBatch,
            o.Events,
            excludeRemovedInstanceId: id,
            syncBoardDiffs: false,
            xpType: EExperienceType.Item);

        if (sell > 0)
        {
            _run.AddPlayerGold(sell);
        }
        o.Events.Add(new GameSimEventCardSold(id, (uint)sell));
        o.Events.Add(new GameSimEventCardDisposed(id, ECardDisposalReason.Sold));

        // 摘除、产卡与 AddPlayerGold(sell) 更新 Custom_3 后，必须基于最新玩家属性重算局外光环（如 Cash Cannon/Oinkment/Calcinator）并全盘同步
        RecalculateAurasAndSyncBoard(o.Events);
        ReconcileAndVerifyBoardInvariants("SellCard", o.DirtyCardIds, o.Events);
        LogBoardAudit("SellCard", id, card.TemplateId.ToString(), fromLoc, $"Sold(+{sell}g)");

        // V7-1.5：在 Encounter/Pedestal/Loot 中卖卡只累计 PendingLevelUps，不在遭遇内打断；仅在 Choice 态卖卡时可即时切入 LevelUp
        if (_run.PendingLevelUp && _run.State == ERunState.Choice)
        {
            TryEnterSuspendedLevelUp(o.Events);
        }

        return o;
    }

    private Outcome OnCommitPedestal(CommitToPedestalCommand cmd)
    {
        if (_runEnded || _run.State != ERunState.Pedestal)
            return NotAllowed($"CommitToPedestal not allowed in state {_run.State}");

        string id = cmd.InstanceId.Value;
        if (string.IsNullOrEmpty(id))
            return NotFound("empty instance id for pedestal");
        if (!_run.Cards.TryGetValue(id, out var card) || card.Owner != ECombatantId.Player)
            return NotFound("player card not found for pedestal: " + id);
        if (card.Type != ECardType.Item)
            return NotAllowed($"only Item cards can be committed to pedestal, got {card.Type}");

        var o = new Outcome();
        var coreCard = EnsureCoreCard(card);
        string encId = _run.CurrentEncounterId ?? "";
        var pedPoolTpl = _pool.Get(encId);
        var pedCoreTpl = _repo.GetCard(encId);
        var criteria = pedCoreTpl?.SelectionCriteria ?? pedPoolTpl?.SelectionCriteria;
        var behavior = pedCoreTpl?.Behavior ?? pedPoolTpl?.Behavior;

        if (criteria != null)
        {
            var p1Check = BuildCorePlayerFromRun();
            var checkCtx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, p1Check, p1Check, _rng)
            {
                CurrentDay = (int)_run.Day,
                CurrentHour = (int)_run.Hour,
                CurrentEncounterId = encId,
            };
            if (!ValueAndConditionEvaluator.EvaluateCardCondition(criteria, coreCard, p1Check, checkCtx))
                return NotAllowed("card does not meet pedestal selection criteria");
        }

        string behType = behavior?.Type ?? "";
        string encName = pedPoolTpl?.InternalName ?? pedCoreTpl?.InternalName ?? "";
        bool isTransformBehavior = string.Equals(behType, "TPedestalBehaviorTransform", StringComparison.OrdinalIgnoreCase);
        bool isUpgradeBehavior = string.Equals(behType, "TPedestalBehaviorUpgrade", StringComparison.OrdinalIgnoreCase);
        bool isEnchantBehavior = string.Equals(behType, "TPedestalBehaviorEnchant", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(behType, "TPedestalBehaviorEnchantRandom", StringComparison.OrdinalIgnoreCase);

        if (isTransformBehavior)
        {
            if (card.Tier == ETier.Legendary || coreCard.Template.DefaultTier == CardTier.Legendary)
                return NotAllowed("legendary cards cannot be transformed");
            if (!TryApplyTransformPedestal(card, coreCard, behavior, o))
                return NotAllowed("card cannot be transformed at this pedestal");
        }
        else
        {
            if (!isUpgradeBehavior && !isEnchantBehavior)
            {
                isEnchantBehavior = encName.Contains("Enchant", StringComparison.OrdinalIgnoreCase) || card.Tier >= ETier.Diamond;
            }

            if (!isEnchantBehavior)
            {
                ETier nextTier = card.Tier + 1;
                if (!string.IsNullOrEmpty(behavior?.TargetTier) &&
                    Enum.TryParse<ETier>(behavior.TargetTier, true, out var targetTier) &&
                    targetTier > card.Tier)
                {
                    nextTier = targetTier;
                }
                if (card.Tier >= ETier.Diamond || nextTier > ETier.Diamond)
                    return NotAllowed("card cannot be upgraded further");

                UpgradeCardEntryToTier(card, nextTier);
                o.Events.Add(new GameSimEventPedestalActivated());
                o.Events.Add(new GameSimEventCardUpgraded(card.InstanceId, nextTier));

                var p1Core = BuildCorePlayerFromRun();
                var upgBatch = OutOfCombatController.OnCardUpgraded(
                    p1Core, coreCard, _repo, _rng, isFusion: false,
                    currentEncounterId: encId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard);
                ApplyOutOfCombatBatch(p1Core, upgBatch, o.Events, syncBoardDiffs: true);
            }
            else
            {
                var coreTpl = _repo.GetCard(card.TemplateId.ToString());
                EEnchantmentType chosen = PickPedestalEnchantment(behavior, coreTpl, card.Enchantment);
                card.Enchantment = chosen;
                coreCard.ApplyEnchantment(chosen.ToString(), preventOverride: false, isTemporary: false);

                o.Events.Add(new GameSimEventPedestalActivated());
                o.Events.Add(new GameSimEventCardEnchanted
                {
                    InstanceId = card.InstanceId,
                    EnchantmentType = chosen,
                    IsReverted = false,
                });

                var p1Core = BuildCorePlayerFromRun();
                var enchBatch = OutOfCombatController.OnCardEnchanted(
                    p1Core, coreCard, _repo, _rng,
                    currentEncounterId: encId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard);
                ApplyOutOfCombatBatch(p1Core, enchBatch, o.Events, syncBoardDiffs: true);
            }
        }

        _run.SelectionsMadeInCurrentContext++;
        string? exitingStepId = _run.CurrentEncounterId;
        string? exitingRootId = _run.ParentEncounterTemplateId ?? exitingStepId;
        TriggerOnEncounterExited(_run.CurrentEncounterId, o.Events);
        FlushPendingEncounterExitXp(o.Events);
        _run.CurrentEncounterCoreCard = null;
        o.DirtyCardIds.Add(card.InstanceId);
        ReconcileAndVerifyBoardInvariants("Pedestal", o.DirtyCardIds, o.Events);
        LogBoardAudit("Pedestal", card.InstanceId, card.TemplateId.ToString(), $"{card.Section}:{card.Socket}", $"{card.Tier}/{card.Enchantment}");
        Guid? nextOnExit = _run.SelectionRules.NextEncounterOnExit;
        bool hasNextEnc = nextOnExit is Guid ng && ng != Guid.Empty && (_pool.IsAssetValid(ng.ToString()) || _repo.GetCard(ng.ToString()) != null);
        if (!hasNextEnc)
        {
            _run.ParentEncounterTemplateId = null;
            ActivateEncounterCard(null, selectedCard: null, o.Events, emitPurchaseIfSelected: false);
        }
        ExitEncounterOrAdvance(o, nextOnExit, exitingRootId, exitingStepId);
        return o;
    }

    private bool TryApplyTransformPedestal(
        RunState.CardEntry oldEntry,
        CardInstance coreCard,
        RuleNode? behavior,
        Outcome o)
    {
        if (oldEntry.Tier == ETier.Legendary || coreCard.Template.DefaultTier == CardTier.Legendary)
            return false;

        var p1Core = BuildCorePlayerFromRun();
        var liveCard = p1Core.Hand.Cards.FirstOrDefault(c => c.InstanceId == oldEntry.InstanceId)
                    ?? p1Core.Stash.Cards.FirstOrDefault(c => c.InstanceId == oldEntry.InstanceId);
        if (liveCard == null) return false;

        RuleNode spawnCtx = behavior?.SpawnContext ?? new RuleNode
        {
            Type = "TSpawnContextQuery",
            SelectionMethod = "Random",
            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = System.Text.Json.JsonSerializer.SerializeToElement(1.0) },
            Behaviors =
            [
                new RuleNode { Type = "TSpawnBehaviorInheritSize", Inherits = true },
                new RuleNode { Type = "TSpawnBehaviorInheritTier", Inherits = true },
            ],
        };

        var txCtx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, p1Core, p1Core, _rng)
        {
            CurrentDay = (int)_run.Day,
            CurrentHour = (int)_run.Hour,
            CurrentEncounterId = _run.CurrentEncounterId,
            PlayMode = _run.PlayMode.ToString(),
            SelfCard = liveCard,
            TriggerSourceCard = liveCard,
            TriggerTargetCard = liveCard,
        };

        var spawned = SpawnQueryEngine.SpawnCards(
            spawnCtx,
            txCtx,
            defaultCountOverride: 1,
            ownedMaxTiers: null,
            validAssetIds: _pool.WhitelistEnabled ? _pool.ValidCardIds : null,
            archetypeResolver: null,
            rules: _run.CustomRules);
        var replacement = spawned.FirstOrDefault(c =>
            c.Template.DefaultTier != CardTier.Legendary &&
            !string.Equals(c.Template.Id, liveCard.Template.Id, StringComparison.OrdinalIgnoreCase))
            ?? spawned.FirstOrDefault(c => c.Template.DefaultTier != CardTier.Legendary);
        if (replacement == null)
            return false;

        var oldSec = oldEntry.Section;
        int oldSlot = oldEntry.Socket.HasValue ? (int)oldEntry.Socket.Value : liveCard.StartSlot;
        _run.TrackReagentTransformIfApplicable(coreCard, oldEntry);
        RemoveOwnedEntryFromRun(oldEntry);
        _run.HandGrid.Remove(liveCard);
        _run.StashGrid.Remove(liveCard);

        replacement.IsCombatTemporary = false;
        replacement.TransformedFromInstanceId = null;
        var newEntry = RegisterSpawnedCoreCard(replacement, oldSec, oldSlot, emitSpawnEvent: false, o.Events);
        o.DirtyCardIds.Add(newEntry.InstanceId);
        o.Events.Add(new GameSimEventPedestalActivated());
        o.Events.Add(new GameSimEventCardTransformed(
            "ped_" + oldEntry.InstanceId,
            oldEntry.InstanceId,
            [
                new SimEventCardTransformation(
                    newEntry.InstanceId,
                    newEntry.TemplateId.ToString(),
                    newEntry.Type,
                    ECombatantId.Player,
                    newEntry.Section,
                    newEntry.Socket)
            ]));
        return true;
    }

    private EEnchantmentType PickPedestalEnchantment(
        RuleNode? behavior,
        BazaarCore.Models.CardTemplate? cardTpl,
        EEnchantmentType? currentEnchantment)
    {
        if (behavior != null &&
            string.Equals(behavior.Type, "TPedestalBehaviorEnchant", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(behavior.Enchantment) &&
            Enum.TryParse<EEnchantmentType>(behavior.Enchantment, true, out var fixedEnch))
        {
            return fixedEnch;
        }

        var eligibleKeys = cardTpl?.Enchantments?.Keys
            .Where(k => Enum.TryParse<EEnchantmentType>(k, true, out _))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (behavior != null &&
            string.Equals(behavior.Type, "TPedestalBehaviorEnchantRandom", StringComparison.OrdinalIgnoreCase))
        {
            var weighted = behavior.GetWeightedEnchantments()
                .Where(w => Enum.TryParse<EEnchantmentType>(w.Enchantment, true, out _))
                .Where(w => eligibleKeys == null || eligibleKeys.Contains(w.Enchantment))
                .ToList();

            var nonSame = currentEnchantment.HasValue
                ? weighted.Where(w => !w.Enchantment.Equals(currentEnchantment.Value.ToString(), StringComparison.OrdinalIgnoreCase)).ToList()
                : weighted;
            var pool = nonSame.Count > 0 ? nonSame : weighted;
            if (pool.Count > 0)
            {
                double sum = pool.Sum(p => Math.Max(0.0, p.Weight));
                if (sum > 0)
                {
                    double roll = _rng.NextDouble() * sum;
                    double acc = 0;
                    foreach (var p in pool)
                    {
                        acc += Math.Max(0.0, p.Weight);
                        if (roll <= acc && Enum.TryParse<EEnchantmentType>(p.Enchantment, true, out var picked))
                            return picked;
                    }
                }
                if (Enum.TryParse<EEnchantmentType>(pool[_rng.Next(pool.Count)].Enchantment, true, out var fb))
                    return fb;
            }
        }

        if (eligibleKeys is { Count: > 0 })
        {
            var list = currentEnchantment.HasValue
                ? eligibleKeys.Where(k => !k.Equals(currentEnchantment.Value.ToString(), StringComparison.OrdinalIgnoreCase)).ToList()
                : eligibleKeys.ToList();
            if (list.Count == 0)
                list = eligibleKeys.ToList();
            string picked = list[_rng.Next(list.Count)];
            if (Enum.TryParse<EEnchantmentType>(picked, true, out var res))
                return res;
        }
        return EEnchantmentType.Fiery;
    }

    private Outcome OnCheat(CheatCommand cmd)
    {
        var o = new Outcome();
        var a = cmd.Args ?? new List<string>();
        if (a.Count == 0 || string.IsNullOrWhiteSpace(a[0]))
            return NotAllowed("empty cheat command");

        string sub = a[0].ToLowerInvariant();
        if (sub != "restart" && (_runEnded || _run.State is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown))
            return NotAllowed($"cheat '{sub}' not allowed in terminal state {_run.State}");

        switch (sub)
        {
            case "gold":
                if (a.Count <= 1 || !int.TryParse(a[1], out int g) || g < 0)
                    return NotAllowed("invalid gold cheat argument");
                _run.PlayerAttributes[EPlayerAttributeType.Gold] = g;
                break;
            case "day":
                if (a.Count <= 1 || !uint.TryParse(a[1], out uint d) || d < 1 || d > 30)
                    return NotAllowed("invalid day cheat argument");
                _run.Day = d;
                _run.VisitedDayEncounterIds.Clear();
                _run.PendingPvpGhost = null;
                _run.CurrentPvpOpponent = null;
                o.Events.Add(new GameSimEventRunDayChanged(d));
                break;
            case "hour":
                if (a.Count <= 1 || !uint.TryParse(a[1], out uint h) || h < 1 || h > (uint)Math.Max(1, _mode.HoursInADay))
                    return NotAllowed("invalid hour cheat argument");
                _run.Hour = h;
                _run.GainedHourXpThisHour = false;
                _run.GainedAdvanceHourXpThisHour = false;
                if (h != (uint)_mode.HoursInADay)
                {
                    _run.PendingPvpGhost = null;
                    _run.CurrentPvpOpponent = null;
                }
                o.Events.Add(new GameSimEventRunHourChanged(h));
                break;
            case "prestige":
            {
                if (a.Count <= 1 || !int.TryParse(a[1], out int p) || p < 0 || p > 999)
                    return NotAllowed("invalid prestige cheat argument");
                int oldPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
                _run.PlayerAttributes[EPlayerAttributeType.Prestige] = p;
                o.Events.Add(new GameSimEventPlayerPrestigeChanged(p - oldPrestige));
                break;
            }
            case "win":
            {
                int addWins = 1;
                if (a.Count > 1 && (!int.TryParse(a[1], out addWins) || addWins < 1 || addWins > 20))
                    return NotAllowed("invalid win cheat argument");
                _run.Victories += (uint)addWins;
                if (TryEndRunToEvents(o.Events))
                {
                    var tLobby = _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId);
                    if (tLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId))
                        _lobbies!.RecordRunProgress(tLobby, _run.AccountId, _run);
                }
                break;
            }
            case "exp":
                if (a.Count <= 1 || !uint.TryParse(a[1], out uint x) || x > 10000)
                    return NotAllowed("invalid exp cheat argument");
                GrantExperience(o, (int)x, EExperienceType.Cheat);
                break;
            case "deal":
                _run.SelectionsMadeInCurrentContext = 0;
                o.Events.AddRange(DealHourEncountersAndClearActive());
                break;
            case "slots":
                _run.HandSocketUnlockMask = 0x3FF;
                o.Events.Add(new GameSimEventSocketsUnlocked(
                    new HashSet<EContainerSocketId>(Enum.GetValues<EContainerSocketId>()),
                    0x3FF, EInventorySection.Hand));
                ReconcileAndVerifyBoardInvariants("CheatSlots", o.DirtyCardIds, o.Events);
                break;
            case "pvp":
                o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
                RunRealCombatSequence(o, _pool.Get(ShopEngine.Hour6PvpCardId), isPvp: true);
                break;
            case "skipstart":
                if (!StartSelectionDone)
                {
                    StartSelectionDone = true;
                    _run.SelectionsMadeInCurrentContext = 0;
                    o.Events.AddRange(DealHourEncountersAndClearActive());
                }
                break;
            case "restart":
            {
                _run.CustomRules = ResolveEffectiveRulesForStart();
                _run.TendencyLocked = !_run.CustomRules.AllowTendencySelection;
                _shop.ActiveRules = _run.CustomRules;
                _shop.ArchetypeIndex = _archetypeIndex;

                _run.Day = 1;
                _run.Hour = 1;
                _run.Victories = 0;
                _run.Defeats = 0;
                _run.FatesUsed = false;
                _run.PlayerAttributes.Clear();
                foreach (var kv in RunState.CreateDefaultCombatantAttributes(250, 250, 20, 1, 15, 5))
                    _run.PlayerAttributes[kv.Key] = kv.Value;
                _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(100, 100, gold: 0, income: 5);
                _run.HandSocketUnlockMask = 0b00_0111_1000;
                _run.HandGrid.Cards.Clear();
                _run.StashGrid.Cards.Clear();
                _run.Cards.Clear();
                _run.SelectionSet.Clear();
                _run.MonsterLootPages.Clear();
                _run.MonsterLootPageIndex = 0;
                _run.SuspendedStates.Clear();
                _run.PendingLevelUps = 0;
                _run.PendingEncounterExitXp = 0;
                _run.GainedHourXpThisHour = false;
                _run.GainedAdvanceHourXpThisHour = false;
                _run.SelectionsMadeInCurrentContext = 0;
                _run.CurrentEncounterCoreCard = null;
                _run.ActiveEncounterCardInstanceId = null;
                _run.ParentEncounterTemplateId = null;
                _run.VisitedSingleSpawnEncounterIds.Clear();
                _run.VisitedDayEncounterIds.Clear();
                _run.ExclusionSet.Clear();
                _run.ExclusionSetInitialized = false;
                EnsureRunExclusionSetInitialized();
                _boardAuditLogs.Clear();
                ClearTrackedPlayerTempModifiers(clearHour: true, clearDay: true, clearAura: true);
                _run.CurrentEncounterId = null;
                _run.PendingPvpGhost = null;
                _run.CurrentPvpOpponent = null;
                _run.RerollCost = 2;
                _run.RerollsRemaining = 3;
                _runEnded = false;
                _run.State = ERunState.Choice;
                o.ForceStateSync = true;
                ApplyStartingStats(_run.CustomRules, o.Events);
                int startPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
                GrantStarterPresets(_run.CustomRules, o.Events);
                o.Events.Add(new GameSimEventRunDayChanged(1));
                o.Events.Add(new GameSimEventRunHourChanged(1));
                o.Events.Add(new GameSimEventPlayerPrestigeChanged(startPrestige));
                o.Events.AddRange(DealHourEncountersAndClearActive());
                StartSelectionDone = _run.State == ERunState.Choice;
                if (_run.State is ERunState.Encounter or ERunState.Pedestal && !string.IsNullOrEmpty(_run.CurrentEncounterId))
                {
                    _run.CurrentEncounterCoreCard = CreateEncounterCoreCard(_run.CurrentEncounterId);
                    ActivateEncounterCard(_run.CurrentEncounterId, selectedCard: null, o.Events, emitPurchaseIfSelected: false);
                }
                ReconcileAndVerifyBoardInvariants("CheatRestart", o.DirtyCardIds, o.Events);
                _run.RngSeed = _rng.Next(1, int.MaxValue);
                break;
            }
            default:
                return NotAllowed($"unknown cheat command: {sub}");
        }
        return o;
    }

    private Outcome OnAbandon()
    {
        if (_runEnded || _run.State is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            return NotAllowed($"AbandonRun not allowed in terminal state {_run.State}");
        }

        var o = new Outcome();
        _run.State = ERunState.EndRunDefeat;
        _runEnded = true;
        o.Events.Add(new GameSimEventStateTransitioned(ERunState.EndRunDefeat, null));
        o.Events.Add(new GameSimEventRunCompleted(new CompleteRunResult
        {
            XpEarned = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0),
            ChestCount = 0,
        }));
        var tLobby = _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId);
        if (tLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId))
        {
            _lobbies!.RecordRunProgress(tLobby, _run.AccountId, _run);
        }
        return o;
    }

    // ── 局外触发批量回写与状态压栈辅助 ─────────────────────────────────────

    private void RecalculateAurasAndSyncBoard(List<IGameSimEvent> events)
    {
        var p1Core = BuildCorePlayerFromRun();
        var batch = OutOfCombatController.RecalculateOutOfCombatAuras(
            p1Core, _repo, activeEncounterCard: _run.CurrentEncounterCoreCard);
        ApplyOutOfCombatBatch(p1Core, batch, events, syncBoardDiffs: true);
    }

    private void SyncShelfEntriesDiff(List<IGameSimEvent> events)
    {
        var shelfEntries = _run.SelectionSet
            .Select(id => _run.Cards.GetValueOrDefault(id))
            .Where(e => e != null && e.CoreCard != null)!
            .Cast<RunState.CardEntry>();
        events.AddRange(_run.SyncAllEntriesFromCoreCardsAndDiff(_repo.GameMode.StandardPrices, shelfEntries));
    }

    private static bool TriggerContainsType(RuleNode? trigger, string expectedType)
    {
        if (trigger == null) return false;
        if (string.Equals(trigger.Type, expectedType, StringComparison.OrdinalIgnoreCase))
            return true;
        if (trigger.Triggers != null)
        {
            foreach (var child in trigger.Triggers)
            {
                if (TriggerContainsType(child, expectedType))
                    return true;
            }
        }
        return false;
    }

    private CardInstance? CreateEncounterCoreCard(string? encounterId, string? instanceId = null)
    {
        if (string.IsNullOrEmpty(encounterId)) return null;
        var tpl = _repo.GetCard(encounterId);
        if (tpl == null) return null;
        return new CardInstance(tpl, instanceId: instanceId ?? encounterId);
    }

    private void FlushPendingEncounterExitXp(List<IGameSimEvent> events)
    {
        int xp = _run.PendingEncounterExitXp;
        _run.PendingEncounterExitXp = 0;
        if (xp <= 0) return;
        if (_run.GainedHourXpThisHour && xp <= _mode.ExperiencePerHour) return;
        _run.GainedHourXpThisHour = true;
        GrantExperienceToList(events, xp, EExperienceType.Hour);
    }

    private CardInstance? GetOrCreateCurrentEncounterCoreCard(string? encounterId)
    {
        if (string.IsNullOrEmpty(encounterId)) return null;
        if (_run.CurrentEncounterCoreCard != null &&
            string.Equals(_run.CurrentEncounterCoreCard.Template.Id, encounterId, StringComparison.OrdinalIgnoreCase))
        {
            return _run.CurrentEncounterCoreCard;
        }
        return CreateEncounterCoreCard(encounterId);
    }

    private void TriggerOnEncounterEnteredAndDealt(string? encounterId, List<IGameSimEvent> events)
    {
        var encCore = GetOrCreateCurrentEncounterCoreCard(encounterId);
        if (encCore != null)
        {
            var p1Enter = BuildCorePlayerFromRun();
            var enterBatch = OutOfCombatController.OnEncounterEntered(
                p1Enter, encCore, _repo, _rng, encounterId,
                activeEncounterCard: _run.CurrentEncounterCoreCard ?? encCore);
            ApplyOutOfCombatBatch(p1Enter, enterBatch, events, syncBoardDiffs: true);
        }

        TriggerOnEncounterCardsDealtOnly(encounterId, events);
    }

    private static bool ActionModifiesBuyPrice(RuleNode? node)
    {
        if (node == null) return false;
        if (string.Equals(node.Type, "TActionCardModifyAttribute", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(node.AttributeType, "BuyPrice", StringComparison.OrdinalIgnoreCase))
            return true;
        return node.Actions?.Any(ActionModifiesBuyPrice) ?? false;
    }

    private bool HasOfficialDealBuyPriceAbility(string? encounterId)
    {
        if (string.IsNullOrEmpty(encounterId)) return false;
        var coreTpl = _repo.GetCard(encounterId);
        if (coreTpl?.Abilities == null) return false;
        return coreTpl.Abilities.Values.Any(ab =>
            string.Equals(ab.Trigger?.Type, "TTriggerOnEncounterCardsDealt", StringComparison.OrdinalIgnoreCase) &&
            ActionModifiesBuyPrice(ab.Action));
    }

    private void TriggerOnEncounterCardsDealtOnly(string? encounterId, List<IGameSimEvent> events)
    {
        bool officialDealDiscount = HasOfficialDealBuyPriceAbility(encounterId);
        var dealtEntries = new List<RunState.CardEntry>();
        var dealtCoreCards = new List<CardInstance>();
        Dictionary<string, int>? preBuyPrices = officialDealDiscount ? new Dictionary<string, int>(StringComparer.Ordinal) : null;

        foreach (string selId in _run.SelectionSet)
        {
            if (!_run.Cards.TryGetValue(selId, out var ce)) continue;
            var core = EnsureCoreCard(ce);
            if (officialDealDiscount && ce.Type == ECardType.Item)
            {
                // Q33：若遭遇模板自身声明了 DB TTriggerOnEncounterCardsDealt BuyPrice 折扣（如 Curio 等官方商人 20% 概率 0.75x），
                // 剥离任何兜底合成 HourMult["BuyPrice"]，彻底避免 0.75 * 0.75 = 0.5625 双折叠加。
                if (core.HourMult.Remove("BuyPrice") || _shop.TryGetShelfDiscount(ce.InstanceId, out _))
                {
                    _shop.ClearShelfDiscount(ce.InstanceId);
                    ce.Attributes[ECardAttributeType.BuyPrice] = RunState.ComputeCardBuyPrice(ce, core, _repo.GameMode.StandardPrices);
                }
                preBuyPrices![ce.InstanceId] = ce.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0);
            }
            dealtEntries.Add(ce);
            dealtCoreCards.Add(core);
        }
        if (dealtCoreCards.Count == 0) return;

        var encCore = GetOrCreateCurrentEncounterCoreCard(encounterId);
        var p1Dealt = BuildCorePlayerFromRun();
        var dealtBatch = OutOfCombatController.OnEncounterCardsDealt(
            p1Dealt, encCore, dealtCoreCards, _repo, _rng, encounterId,
            activeEncounterCard: _run.CurrentEncounterCoreCard ?? encCore);
        ApplyOutOfCombatBatch(p1Dealt, dealtBatch, events, syncBoardDiffs: true);
        SyncShelfEntriesDiff(events);

        if (officialDealDiscount && preBuyPrices != null)
        {
            foreach (var ce in dealtEntries)
            {
                if (!preBuyPrices.TryGetValue(ce.InstanceId, out int origBuy) || origBuy <= 0) continue;
                int newBuy = ce.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, origBuy);
                if (newBuy > 0 && newBuy < origBuy)
                    _shop.RegisterExternalShelfDiscount(ce, origBuy, newBuy);
            }
        }
    }

    private void TriggerOnEncounterExited(string? encounterId, List<IGameSimEvent> events)
    {
        var encCore = GetOrCreateCurrentEncounterCoreCard(encounterId);
        if (encCore == null) return;

        var p1Exit = BuildCorePlayerFromRun();
        var exitBatch = OutOfCombatController.OnEncounterExited(
            p1Exit, encCore, _repo, _rng, encounterId,
            activeEncounterCard: _run.CurrentEncounterCoreCard ?? encCore);
        ApplyOutOfCombatBatch(p1Exit, exitBatch, events, syncBoardDiffs: true);
    }

    private int ResolveNextPendingLevelUpLevel()
    {
        int currentLvl = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1);
        int remainingAfterThis = Math.Max(0, _run.PendingLevelUps - 1);
        return Math.Max(1, currentLvl - remainingAfterThis);
    }

    private void ConsumeNextPendingLevelUpToEvents(List<IGameSimEvent> events)
    {
        int lvl = ResolveNextPendingLevelUpLevel();
        _run.PendingLevelUps = Math.Max(0, _run.PendingLevelUps - 1);
        _run.SelectionsMadeInCurrentContext = 0;
        events.AddRange(_shop.DealLevelUpChoices(lvl, _levelUps, BuildCorePlayerFromRun()));
    }

    private void SettleInterruptedCombatOnResume()
    {
        var originalState = _run.State;
        _run.GainedHourXpThisHour = true;
        _run.PendingPvpGhost = null;
        _run.CurrentPvpOpponent = null;
        _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(100, 100, gold: 0, income: 5);

        var oppIds = _run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Opponent)
            .Select(c => c.InstanceId)
            .ToList();
        foreach (string oid in oppIds)
        {
            _run.Cards.Remove(oid);
            _run.SelectionSet.Remove(oid);
        }

        var dummy = new List<IGameSimEvent>();
        ClearPostCombatTransientStatusAndSync(dummy);

        // 若 SelectionSet 里已有战利品（或未领奖的待选卡），直接结算为 Loot
        bool hasLootSelection = _run.SelectionSet.Any(id =>
            _run.Cards.TryGetValue(id, out var ce) && ce.Owner == null);
        if (originalState == ERunState.Combat && hasLootSelection)
        {
            _run.State = ERunState.Loot;
            if (!string.IsNullOrEmpty(_run.CurrentEncounterId) && string.IsNullOrEmpty(_run.ActiveEncounterCardInstanceId))
                ActivateEncounterCard(_run.CurrentEncounterId, selectedCard: null, dummy, emitPurchaseIfSelected: false);
            return;
        }

        _shop.ClearShelf();
        _run.ActiveEncounterCardInstanceId = null;

        // 若断线前 Prestige 已跌至 <= 0 且尚未使用 Fates，优先进入 Fates 而非误判负
        string fatesTemplateId = !string.IsNullOrWhiteSpace(_mode.FatesTemplateId)
            ? _mode.FatesTemplateId
            : "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
        int curPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
        if (!_run.FatesUsed && curPrestige <= _mode.FatesSpawnAtOrBelowPrestige &&
            (_pool.IsAssetValid(fatesTemplateId) || _repo.GetCard(fatesTemplateId) != null))
        {
            _run.FatesUsed = true;
            _run.PlayerAttributes[EPlayerAttributeType.Prestige] = Math.Max(1, _mode.FatesAfterPrestige);
            TransitionToEncounterById(fatesTemplateId, new Outcome());
            _run.RngSeed = _rng.Next(1, int.MaxValue);
            return;
        }

        if (!TryEndRunToEvents(dummy, justFinishedPvp: originalState == ERunState.PVPCombat))
        {
            if (_run.PendingLevelUp)
            {
                ConsumeNextPendingLevelUpToEvents(dummy);
            }
            else if (originalState == ERunState.Combat || _run.Victories + _run.Defeats >= _run.Day)
            {
                AdvanceHourOrDay(dummy);
            }
            else
            {
                _run.SelectionsMadeInCurrentContext = 0;
                DealHourEncountersAndClearActive();
            }
        }
        _run.RngSeed = _rng.Next(1, int.MaxValue);
    }

    private void PushHourChoiceSnapshot()
    {
        _run.SuspendedStates.Push(new SuspendedStateSnapshot
        {
            State = ERunState.Choice,
            CurrentEncounterId = null,
            SelectionSet = new List<string>(),
            SelectionRules = new TSelectionContextRules
            {
                CanSelectMultiple = false,
                SelectionIsFree = true,
                CanExit = false,
            },
            RerollCost = 0,
            RerollsRemaining = 0,
            SelectionsMadeInCurrentContext = 0,
        });
    }

    private bool TryEnterSuspendedLevelUp(List<IGameSimEvent> events)
    {
        if (!_run.PendingLevelUp || _runEnded || _run.State == ERunState.LevelUp)
            return false;

        var snapshot = new SuspendedStateSnapshot
        {
            State = _run.State,
            CurrentEncounterId = _run.CurrentEncounterId,
            ParentEncounterTemplateId = _run.ParentEncounterTemplateId,
            ActiveEncounterCardInstanceId = _run.ActiveEncounterCardInstanceId,
            ActiveEncounterCard = !string.IsNullOrEmpty(_run.ActiveEncounterCardInstanceId)
                ? _run.Cards.GetValueOrDefault(_run.ActiveEncounterCardInstanceId)
                : null,
            SelectionSet = new List<string>(_run.SelectionSet),
            SelectionRules = RunState.CloneRules(_run.SelectionRules),
            RerollCost = _run.RerollCost,
            RerollsRemaining = _run.RerollsRemaining,
            SelectionsMadeInCurrentContext = _run.SelectionsMadeInCurrentContext,
        };

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string selId in _run.SelectionSet)
        {
            if (string.Equals(selId, _run.ActiveEncounterCardInstanceId, StringComparison.Ordinal)) continue;
            if (seenIds.Add(selId) && _run.Cards.TryGetValue(selId, out var cand) && cand.Owner == null)
                snapshot.SuspendedCards.Add(cand);
        }
        foreach (var cand in _run.Cards.Values.Where(c => c.Owner == null))
        {
            if (string.Equals(cand.InstanceId, _run.ActiveEncounterCardInstanceId, StringComparison.Ordinal)) continue;
            if (seenIds.Add(cand.InstanceId))
                snapshot.SuspendedCards.Add(cand);
        }

        _run.SuspendedStates.Push(snapshot);
        _run.ActiveEncounterCardInstanceId = null;
        _run.ParentEncounterTemplateId = null;

        // 发射客户端原生支持的 InterruptStateEntered(Union 11)，随后 DealLevelUpChoices 清理旧货架并发出 LevelUp 选项卡
        events.Add(new GameSimEventInterruptStateEntered(ERunState.LevelUp, true));
        ConsumeNextPendingLevelUpToEvents(events);
        return true;
    }

    private void PushChoiceAndEnterLevelUp(List<IGameSimEvent> events)
    {
        PushHourChoiceSnapshot();
        events.Add(new GameSimEventInterruptStateEntered(ERunState.LevelUp, true));
        ConsumeNextPendingLevelUpToEvents(events);
    }

    private void ApplyOutOfCombatBatch(
        PlayerState ps,
        OutOfCombatBatchResult batch,
        List<IGameSimEvent> events,
        string? excludeRemovedInstanceId = null,
        bool syncBoardDiffs = true,
        EExperienceType xpType = EExperienceType.Item)
    {
        SyncTrackedPlayerTempModifiersFromCore(ps);

        // 1. 同步玩家属性变动（含 Experience 折算升级与 Level 联动血量/解锁）
        bool healthMaxHandled = false;
        bool needsPostTrackerAuraRecalc = false;
        int xpDeltaToGrant = 0;
        int levelsGainedDirectly = 0;

        foreach (var ch in batch.PlayerAttributeChanges)
        {
            if (ch.Attribute.Equals("Experience", StringComparison.OrdinalIgnoreCase))
            {
                int d = (int)Math.Round(ch.Delta);
                if (d > 0) xpDeltaToGrant += d;
                continue;
            }
            if (ch.Attribute.Equals("Level", StringComparison.OrdinalIgnoreCase))
            {
                int d = (int)Math.Round(ch.Delta);
                if (d > 0) levelsGainedDirectly += d;
                continue;
            }
            if (ch.Attribute.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
                healthMaxHandled = true;

            if (!Enum.TryParse<EPlayerAttributeType>(ch.Attribute, true, out var pa)) continue;
            int oldInt = _run.PlayerAttributes.GetValueOrDefault(pa, 0);
            int deltaInt = (int)Math.Round(ch.Delta);
            if (deltaInt == 0) continue;

            // 使用增量叠加而非直接覆盖 ch.CurrentValue，防止吞掉批次构建后由外部结算的属性变更（如 OnSell 基础售价）
            int newInt = oldInt + deltaInt;
            if (pa is EPlayerAttributeType.Gold or EPlayerAttributeType.Prestige or EPlayerAttributeType.Experience)
                newInt = Math.Max(0, newInt);
            else if (pa == EPlayerAttributeType.HealthMax)
                newInt = Math.Max(1, newInt);

            if (newInt == oldInt) continue;

            _run.PlayerAttributes[pa] = newInt;
            int delta = newInt - oldInt;

            if (pa == EPlayerAttributeType.Gold && delta > 0 &&
                !batch.TriggeredAbilityLogs.Any(l => l.Contains("Gold Gained", StringComparison.OrdinalIgnoreCase)))
            {
                _run.IncrementPlayerAttribute(EPlayerAttributeType.Custom_3, delta);
                needsPostTrackerAuraRecalc = true;
            }
            if (pa == EPlayerAttributeType.Income && delta > 0)
                events.Add(new GameSimEventPlayerIncomeGained((uint)delta));
            else if (pa == EPlayerAttributeType.Prestige)
                events.Add(new GameSimEventPlayerPrestigeChanged(delta));
        }

        // 确保 Health 不越界超出最新 HealthMax（不覆盖外部独立写入且合法的血量值）
        int curMaxHp = Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, ps.HealthMax));
        int curHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, ps.CurrentHealth);
        int clampedHp = Math.Clamp(curHp, 0, curMaxHp);
        if (_run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, clampedHp) != clampedHp)
        {
            _run.PlayerAttributes[EPlayerAttributeType.Health] = clampedHp;
        }

        if (levelsGainedDirectly > 0)
        {
            int curLvl = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1);
            for (int i = 0; i < levelsGainedDirectly; i++)
            {
                curLvl++;
                if (!healthMaxHandled)
                {
                    int hpInc = _levelUps.HealthIncreaseFor(curLvl);
                    if (hpInc <= 0)
                        hpInc = _levelUps.MaxLevel > 0 ? _levelUps.HealthIncreaseFor(_levelUps.MaxLevel) : 150;
                    curMaxHp += hpInc;
                    _run.PlayerAttributes[EPlayerAttributeType.HealthMax] = curMaxHp;
                    _run.PlayerAttributes[EPlayerAttributeType.Health] = curMaxHp;
                }
                _run.PendingLevelUps++;
                var unlocked = _run.UnlockCenteredSockets(Math.Max(2, _mode.NumCarpetSocketsUnlockPerLevel));
                _run.HandSocketUnlockMask = _run.UnlockedSlots;
                if (unlocked.Count > 0)
                    events.Add(new GameSimEventSocketsUnlocked(unlocked, _run.UnlockedSlots, EInventorySection.Hand));
            }
            _run.PlayerAttributes[EPlayerAttributeType.Level] = curLvl;
        }

        if (xpDeltaToGrant > 0)
        {
            GrantExperienceToList(events, xpDeltaToGrant, xpType);
        }

        // 2. 同步局外卡牌变形（Transform）：严格先摘除旧卡，再放置新卡并全量校准邻卡坐标
        var handledOldCards = new HashSet<CardInstance>();
        bool boardMutated = false;
        foreach (var tr in batch.TransformedCards)
        {
            handledOldCards.Add(tr.OldCard);
            var oldEntry = FindEntryByCoreCard(tr.OldCard);
            string oldId = oldEntry?.InstanceId ?? tr.OldCard.InstanceId;
            string oldLoc = oldEntry != null ? $"{oldEntry.Section}:{oldEntry.Socket}" : $"Core:{tr.StartSlot}";
            int prevCustom9 = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_9, 0);
            _run.TrackReagentTransformIfApplicable(tr.OldCard, oldEntry);
            if (_run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_9, 0) != prevCustom9)
                needsPostTrackerAuraRecalc = true;
            if (oldEntry != null)
            {
                RemoveOwnedEntryFromRun(oldEntry);
            }
            else
            {
                _run.HandGrid.Remove(tr.OldCard);
                _run.StashGrid.Remove(tr.OldCard);
            }

            tr.NewCard.IsCombatTemporary = false;
            tr.NewCard.TransformedFromInstanceId = null;
            EInventorySection? sec = ParseSection(tr.Section);
            var newEntry = RegisterSpawnedCoreCard(tr.NewCard, sec, tr.StartSlot, emitSpawnEvent: false, events);
            events.Add(new GameSimEventCardTransformed(
                "ooc_" + oldId,
                oldId,
                new List<SimEventCardTransformation>
                {
                    new(
                        newEntry.InstanceId,
                        newEntry.TemplateId.ToString(),
                        newEntry.Type,
                        ECombatantId.Player,
                        newEntry.Section,
                        newEntry.Socket)
                }));
            LogBoardAudit("Transform", newEntry.InstanceId, newEntry.TemplateId.ToString(), oldLoc, $"{newEntry.Section}:{newEntry.Socket}");
            boardMutated = true;
        }

        // 3. 同步其他被规则销毁的卡牌（跳过卖卡自身与已变形卡）
        foreach (var rem in batch.RemovedCards)
        {
            if (handledOldCards.Contains(rem)) continue;
            var remEntry = FindEntryByCoreCard(rem);
            if (remEntry == null)
            {
                _run.HandGrid.Remove(rem);
                _run.StashGrid.Remove(rem);
                continue;
            }
            if (!string.IsNullOrEmpty(excludeRemovedInstanceId) && remEntry.InstanceId == excludeRemovedInstanceId)
                continue;

            RemoveOwnedEntryFromRun(remEntry);
            events.Add(new GameSimEventCardDisposed(remEntry.InstanceId, ECardDisposalReason.Cleared));
            boardMutated = true;
        }

        // 4. 同步衍生生成的新卡牌（GameSimEventCardSpawned + 技能装备事件），并完整比对 ps.Skills / Hand / Stash / SocketEffects 防止遗漏
        var removedSet = new HashSet<CardInstance>(batch.RemovedCards);
        var registeredInBatch = new HashSet<CardInstance>();
        foreach (var sp in batch.SpawnedCards)
        {
            registeredInBatch.Add(sp.Card);
            if (!string.IsNullOrEmpty(excludeRemovedInstanceId) &&
                string.Equals(sp.Card.InstanceId, excludeRemovedInstanceId, StringComparison.Ordinal))
            {
                continue;
            }
            if (FindEntryByCoreCard(sp.Card) != null) continue;
            EInventorySection? sec = ParseSection(sp.Section);
            RegisterSpawnedCoreCard(sp.Card, sec, sp.StartSlot, emitSpawnEvent: true, events);
            boardMutated = true;
        }

        void SyncMissingOwnedFromCoreList(IEnumerable<CardInstance> coreList, EInventorySection? defaultSection)
        {
            foreach (var c in coreList)
            {
                if (c == null) continue;
                if (!string.IsNullOrEmpty(excludeRemovedInstanceId) &&
                    string.Equals(c.InstanceId, excludeRemovedInstanceId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (handledOldCards.Contains(c) || removedSet.Contains(c) || registeredInBatch.Contains(c))
                    continue;
                if (FindEntryByCoreCard(c) != null)
                    continue;

                EInventorySection? targetSec = string.Equals(c.Template.Type, "Skill", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : defaultSection;
                int slot = targetSec == null ? -1 : c.StartSlot;
                RegisterSpawnedCoreCard(c, targetSec, slot, emitSpawnEvent: true, events);
                registeredInBatch.Add(c);
                boardMutated = true;
            }
        }

        SyncMissingOwnedFromCoreList(ps.Skills, null);
        SyncMissingOwnedFromCoreList(ps.Hand.Cards, EInventorySection.Hand);
        SyncMissingOwnedFromCoreList(ps.Stash.Cards, EInventorySection.Stash);
        SyncMissingOwnedFromCoreList(ps.SocketEffects, EInventorySection.Hand);

        if (boardMutated)
        {
            ReconcileAndVerifyBoardInvariants("OocBatch", null, events);
            ReconcileTransformedEventPlacements(events);
        }

        // 5. 若批次内新增了 Custom_3 / Custom_9 追踪计数，先补一次轻量光环重算再全盘同步
        if (syncBoardDiffs)
        {
            if (needsPostTrackerAuraRecalc)
            {
                ps.Attributes["Custom_3"] = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0);
                ps.Attributes["Custom_9"] = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_9, 0);
                OutOfCombatController.RecalculateOutOfCombatAuras(
                    ps, _repo, activeEncounterCard: _run.CurrentEncounterCoreCard);
            }
            events.AddRange(_run.SyncAllEntriesFromCoreCardsAndDiff(_repo.GameMode.StandardPrices));
        }
    }

    private void ApplyPostCombatPermanentSync(
        PlayerState p1Core,
        PostCombatPermanentSyncResult permSync,
        List<IGameSimEvent> events)
    {
        // 1. 永久变形（TransformReplacementTemplateId / TransformedReplacementCard）
        foreach (var delta in permSync.UpdatedCards)
        {
            if (delta.TransformedReplacementCard == null) continue;
            var oldEntry = _run.Cards.GetValueOrDefault(delta.InstanceId) ?? FindEntryByCoreCard(delta.SourceCard);
            if (oldEntry == null) continue;

            _run.TrackReagentTransformIfApplicable(delta.SourceCard, oldEntry);
            var oldSec = oldEntry.Section;
            int oldSlot = oldEntry.Socket.HasValue ? (int)oldEntry.Socket.Value : delta.TransformedReplacementCard.StartSlot;
            string oldLoc = $"{oldSec}:{oldEntry.Socket}";
            RemoveOwnedEntryFromRun(oldEntry);
            if (delta.SourceCard != null)
            {
                _run.HandGrid.Remove(delta.SourceCard);
                _run.StashGrid.Remove(delta.SourceCard);
            }

            delta.TransformedReplacementCard.IsCombatTemporary = false;
            delta.TransformedReplacementCard.TransformedFromInstanceId = null;

            var newEntry = RegisterSpawnedCoreCard(
                delta.TransformedReplacementCard,
                oldSec,
                oldSlot,
                emitSpawnEvent: false,
                events);

            events.Add(new GameSimEventCardTransformed(
                "cmb_" + oldEntry.InstanceId,
                oldEntry.InstanceId,
                new List<SimEventCardTransformation>
                {
                    new(
                        newEntry.InstanceId,
                        newEntry.TemplateId.ToString(),
                        newEntry.Type,
                        ECombatantId.Player,
                        newEntry.Section,
                        newEntry.Socket)
                }));
            LogBoardAudit("CombatTransform", newEntry.InstanceId, newEntry.TemplateId.ToString(), oldLoc, $"{newEntry.Section}:{newEntry.Socket}");
        }

        // 2. 战斗中为 P1 永久生成的非临时道具/技能
        foreach (var spawnedCore in permSync.SpawnedCards)
        {
            if (FindEntryByCoreCard(spawnedCore) != null) continue;
            EInventorySection? sec = p1Core.Stash.Cards.Contains(spawnedCore)
                ? EInventorySection.Stash
                : (spawnedCore.Template.Type == "Skill"
                    ? null
                    : EInventorySection.Hand);
            RegisterSpawnedCoreCard(spawnedCore, sec, spawnedCore.StartSlot, emitSpawnEvent: true, events);
        }

        // 3. 战斗内永久获得的玩家属性增量（Gold / Income / HealthMax / Experience 等）
        int xpToGrant = 0;
        foreach (var (attrName, diff) in permSync.PlayerAttributeDeltas)
        {
            int intDiff = (int)Math.Round(diff);
            if (intDiff == 0) continue;

            if (attrName.Equals("Experience", StringComparison.OrdinalIgnoreCase))
            {
                if (intDiff > 0) xpToGrant += intDiff;
                continue;
            }
            if (!Enum.TryParse<EPlayerAttributeType>(attrName, true, out var pa)) continue;

            int oldVal = _run.PlayerAttributes.GetValueOrDefault(pa, 0);
            int nextVal = oldVal + intDiff;
            if (pa is EPlayerAttributeType.Gold or EPlayerAttributeType.Prestige or EPlayerAttributeType.Experience)
                nextVal = Math.Max(0, nextVal);
            else if (pa == EPlayerAttributeType.HealthMax)
                nextVal = Math.Max(1, nextVal);

            _run.PlayerAttributes[pa] = nextVal;
            if (pa == EPlayerAttributeType.Gold && nextVal > oldVal)
                _run.IncrementPlayerAttribute(EPlayerAttributeType.Custom_3, nextVal - oldVal);
            if (pa == EPlayerAttributeType.HealthMax)
            {
                int curHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, oldVal);
                _run.PlayerAttributes[EPlayerAttributeType.Health] = intDiff > 0
                    ? Math.Min(nextVal, curHp + intDiff)
                    : Math.Min(curHp, nextVal);
            }
            else if (pa == EPlayerAttributeType.Income && intDiff > 0)
            {
                events.Add(new GameSimEventPlayerIncomeGained((uint)intDiff));
            }
            else if (pa == EPlayerAttributeType.Prestige)
            {
                events.Add(new GameSimEventPlayerPrestigeChanged(intDiff));
            }
        }

        if (xpToGrant > 0)
        {
            GrantExperienceToList(events, xpToGrant, EExperienceType.Combat);
        }
    }

    private RunState.CardEntry RegisterSpawnedCoreCard(
        CardInstance core,
        EInventorySection? preferredSection,
        int preferredSlot,
        bool emitSpawnEvent,
        List<IGameSimEvent> events)
    {
        ECardType cardType = ShopEngine.ParseCardType(core.Template.Type);
        string prefix = cardType switch
        {
            ECardType.Skill => "skl",
            ECardType.SocketEffect => "skt",
            _ => "itm",
        };
        string instId = !string.IsNullOrEmpty(core.InstanceId) && !_run.Cards.ContainsKey(core.InstanceId)
            ? core.InstanceId
            : _shop.NewId(prefix);

        // 若新生成的 CardInstance 与 RunState 权威 ID 不一致，用同模板克隆保留永久态确保 InstanceId 对齐
        if (!string.Equals(core.InstanceId, instId, StringComparison.Ordinal))
        {
            var aligned = new CardInstance(core.Template, core.PermanentTier, core.PermanentEnchantment, instId)
            {
                StartSlot = core.StartSlot,
                TransformReplacementTemplateId = core.TransformReplacementTemplateId,
                TransformedFromInstanceId = core.TransformedFromInstanceId,
            };
            foreach (var (k, v) in core.PermanentAdd) aligned.PermanentAdd[k] = v;
            foreach (var (k, v) in core.PermanentMult) aligned.PermanentMult[k] = v;
            foreach (var (k, v) in core.HourAdd) aligned.HourAdd[k] = v;
            foreach (var (k, v) in core.HourMult) aligned.HourMult[k] = v;
            foreach (var (k, v) in core.DayAdd) aligned.DayAdd[k] = v;
            foreach (var (k, v) in core.DayMult) aligned.DayMult[k] = v;
            foreach (var t in core.ExtraTags) aligned.ExtraTags.Add(t);
            foreach (var (k, v) in core.QuestProgress) aligned.QuestProgress[k] = v;
            foreach (var q in core.CompletedQuests) aligned.CompletedQuests.Add(q);
            aligned.RebuildTierAndEnchantmentData();
            core = aligned;
        }

        EInventorySection? actualSection = preferredSection;
        EContainerSocketId? actualSocket = null;

        var displacedNeighbors = new List<(RunState.CardEntry Entry, EContainerSocketId NewSocket, EInventorySection Section)>();
        if (cardType == ECardType.Item)
        {
            _run.HandGrid.UnlockedMask = _run.UnlockedSlots;
            _run.StashGrid.UnlockedMask = 1023;
            actualSection ??= EInventorySection.Hand;
            BoardGrid primary = actualSection == EInventorySection.Stash ? _run.StashGrid : _run.HandGrid;
            BoardGrid fallback = actualSection == EInventorySection.Stash ? _run.HandGrid : _run.StashGrid;

            bool placed = false;
            if (primary.CountFreeUnlockedSlots() >= core.SlotWidth)
            {
                placed = preferredSlot >= 0 && primary.TryPlaceWithDisplaceOrSwap(core, preferredSlot);
                if (!placed)
                    placed = primary.TryAddAuto(core);
            }

            BoardGrid placedGrid = primary;
            if (!placed && fallback.CountFreeUnlockedSlots() >= core.SlotWidth)
            {
                placed = fallback.TryAddAuto(core);
                if (placed)
                {
                    actualSection = actualSection == EInventorySection.Stash ? EInventorySection.Hand : EInventorySection.Stash;
                    placedGrid = fallback;
                }
            }

            if (placed && core.StartSlot >= 0)
            {
                actualSocket = (EContainerSocketId)Math.Clamp(core.StartSlot, 0, 9);
                SyncGridNeighborSockets(placedGrid, core, actualSection.Value, displacedNeighbors);
            }
            else
            {
                // 极端情况下（Hand 与 Stash 均满），兜底尝试 HandGrid/StashGrid 或保持可被单真值校正识别
                actualSocket = null;
            }
        }
        else if (cardType == ECardType.SocketEffect)
        {
            // P0-7：SocketEffect 挂在 Owner=Player, Section=Hand 下，需分配有效 LeftSocketId (0..9)，不写入 HandGrid/StashGrid
            actualSection = EInventorySection.Hand;
            int slot = preferredSlot is >= 0 and <= 9
                ? preferredSlot
                : (core.StartSlot is >= 0 and <= 9 ? core.StartSlot : FindFreeSocketEffectSlot());
            core.StartSlot = slot;
            actualSocket = (EContainerSocketId)slot;
        }
        else
        {
            actualSection = null;
        }

        EEnchantmentType? ench = null;
        if (!string.IsNullOrEmpty(core.Enchantment) &&
            Enum.TryParse<EEnchantmentType>(core.Enchantment, true, out var parsedEnch))
        {
            ench = parsedEnch;
        }

        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(core.Template.Id),
            Type = cardType,
            Size = ShopEngine.ParseSize(core.Template.Size),
            Tier = ShopEngine.ParseTier(core.Tier.ToString()),
            Enchantment = ench,
            Owner = ECombatantId.Player,
            Section = actualSection,
            Socket = actualSocket,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, _pool.Get(core.Template.Id), core.Template);
        _run.SyncEntryFromCoreCardAndDiff(entry, _repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        if (cardType == ECardType.Item)
        {
            var baseSize = ShopEngine.ParseSize(core.Template.Size);
            var (buy, sell) = ShopEngine.PriceFor(baseSize, entry.Tier);
            if (!entry.Attributes.ContainsKey(ECardAttributeType.BuyPrice))
                entry.Attributes[ECardAttributeType.BuyPrice] = buy;
            if (!entry.Attributes.ContainsKey(ECardAttributeType.SellPrice))
                entry.Attributes[ECardAttributeType.SellPrice] = sell;
        }

        _run.Cards[instId] = entry;
        foreach (var mv in displacedNeighbors)
        {
            events.Add(new GameSimEventCardMoved(mv.Entry.InstanceId, ECombatantId.Player, mv.Section, mv.NewSocket));
        }
        if (emitSpawnEvent)
        {
            events.Add(new GameSimEventCardSpawned(
                instId,
                entry.TemplateId.ToString(),
                entry.Type,
                ECombatantId.Player,
                entry.Section,
                entry.Socket));
            if (cardType == ECardType.Skill)
                events.Add(new GameSimEventPlayerSkillEquipped(instId, ECombatantId.Player));
        }

        return entry;
    }

    private RunState.CardEntry? FindEntryByCoreCard(CardInstance core)
        => _run.Cards.Values.FirstOrDefault(e => ReferenceEquals(e.CoreCard, core) || e.InstanceId == core.InstanceId);

    private int FindFreeSocketEffectSlot()
    {
        var used = new HashSet<int>();
        foreach (var c in _run.Cards.Values)
        {
            if (c.Owner == ECombatantId.Player && c.Type == ECardType.SocketEffect && c.Socket.HasValue)
                used.Add((int)c.Socket.Value);
        }
        for (int i = 0; i < 10; i++)
        {
            if (!used.Contains(i)) return i;
        }
        return 0;
    }

    private void RemoveOwnedEntryFromRun(RunState.CardEntry entry)
    {
        if (entry.CoreCard != null)
        {
            _run.HandGrid.Remove(entry.CoreCard);
            _run.StashGrid.Remove(entry.CoreCard);
        }
        _run.Cards.Remove(entry.InstanceId);
        _run.SelectionSet.Remove(entry.InstanceId);
    }

    private static EInventorySection? ParseSection(string? sec) => sec switch
    {
        "Stash" => EInventorySection.Stash,
        "Hand" => EInventorySection.Hand,
        _ => null,
    };

    private void ClearHourModifiersOnRun(List<IGameSimEvent>? events = null)
    {
        // 先还原玩家 Hour 临时层到基础值再清空
        if (_playerHourAdd.Count > 0 || _playerHourMult.Count > 0)
        {
            var p1 = BuildCorePlayerFromRun();
            p1.ClearEndOfHourModifiers();
            SyncEffectivePlayerAttributesFromCore(p1);
            _playerHourAdd.Clear();
            _playerHourMult.Clear();
        }
        bool anyCardCleared = false;
        foreach (var entry in _run.Cards.Values)
        {
            if (entry.CoreCard == null) continue;
            if (entry.CoreCard.HourAdd.Count > 0 || entry.CoreCard.HourMult.Count > 0 || !string.IsNullOrEmpty(entry.CoreCard.HourEnchantment))
                anyCardCleared = true;
            entry.CoreCard.ClearEndOfHourModifiers();
        }
        if (anyCardCleared && events != null)
            events.AddRange(_run.SyncAllEntriesFromCoreCardsAndDiff(_repo.GameMode.StandardPrices));
    }

    private void ClearDayModifiersOnRun(List<IGameSimEvent>? events = null)
    {
        if (_playerDayAdd.Count > 0 || _playerDayMult.Count > 0)
        {
            var p1 = BuildCorePlayerFromRun();
            p1.ClearEndOfDayModifiers();
            SyncEffectivePlayerAttributesFromCore(p1);
            _playerDayAdd.Clear();
            _playerDayMult.Clear();
        }
        bool anyCardCleared = false;
        foreach (var entry in _run.Cards.Values)
        {
            if (entry.CoreCard == null) continue;
            if (entry.CoreCard.DayAdd.Count > 0 || entry.CoreCard.DayMult.Count > 0 || !string.IsNullOrEmpty(entry.CoreCard.DayEnchantment))
                anyCardCleared = true;
            entry.CoreCard.ClearEndOfDayModifiers();
        }
        if (anyCardCleared && events != null)
            events.AddRange(_run.SyncAllEntriesFromCoreCardsAndDiff(_repo.GameMode.StandardPrices));
    }

    private void SyncTrackedPlayerTempModifiersFromCore(PlayerState ps)
    {
        _playerHourAdd.Clear();
        foreach (var (k, v) in ps.HourAdd) _playerHourAdd[k] = v;
        _playerHourMult.Clear();
        foreach (var (k, v) in ps.HourMult) _playerHourMult[k] = v;
        _playerDayAdd.Clear();
        foreach (var (k, v) in ps.DayAdd) _playerDayAdd[k] = v;
        _playerDayMult.Clear();
        foreach (var (k, v) in ps.DayMult) _playerDayMult[k] = v;

        _run.PlayerAuraAdd.Clear();
        foreach (var (k, v) in ps.AuraAdd) _run.PlayerAuraAdd[k] = v;
        _run.PlayerAuraMult.Clear();
        foreach (var (k, v) in ps.AuraMult) _run.PlayerAuraMult[k] = v;

        if (ps.ExclusionSetInitialized || ps.ExclusionSet.Count > 0)
        {
            _run.ExclusionSet.Clear();
            foreach (string exId in ps.ExclusionSet)
                _run.ExclusionSet.Add(exId);
            _run.ExclusionSetInitialized = true;
        }
    }

    private void SyncEffectivePlayerAttributesFromCore(PlayerState ps)
    {
        foreach (EPlayerAttributeType pa in Enum.GetValues<EPlayerAttributeType>())
        {
            if (pa is EPlayerAttributeType.Experience or EPlayerAttributeType.Level) continue;
            string key = pa.ToString();
            if (!ps.Attributes.ContainsKey(key) && !_run.PlayerAttributes.ContainsKey(pa)) continue;
            int eff = (int)Math.Round(ps.GetAttribute(key));
            if (_run.PlayerAttributes.GetValueOrDefault(pa, 0) != eff)
                _run.PlayerAttributes[pa] = eff;
        }
    }

    private void ClearTrackedPlayerTempModifiers(bool clearHour, bool clearDay, bool clearAura = false)
    {
        if (clearHour)
        {
            _playerHourAdd.Clear();
            _playerHourMult.Clear();
        }
        if (clearDay)
        {
            _playerDayAdd.Clear();
            _playerDayMult.Clear();
        }
        if (clearAura)
        {
            _run.PlayerAuraAdd.Clear();
            _run.PlayerAuraMult.Clear();
        }
    }

    // ── V9 Stage A：棋盘单真值同步、战后状态归零、激活遭遇卡与 [BoardAudit] 日志 ──

    /// <summary>
    /// 棋盘单真值校准与不变式校验（必须在 BuyCard、MoveCard、SellCard、SelectChoice、战斗结算、Transform、快照恢复后执行）：
    ///   1) 清理 HandGrid/StashGrid 中无效或非玩家物品引用，消除重复/跨区双重占位；
    ///   2) 检测并修复“存在于 RunState.Cards 但未落在任何 BoardGrid（幽灵状态）”或“重叠/越界/未解锁占位/Tiny 宽度不一致”卡牌；
    ///   3) 将 HandGrid/StashGrid 的唯一权威槽位写回 RunState.Cards，同步补齐 GameSimEventCardMoved 与 dirtyCardIds 以便下发最新 SimUpdateCard。
    /// </summary>
    public List<IGameSimEvent> ReconcileAndVerifyBoardInvariants(
        string opName,
        ICollection<string>? dirtyCardIds = null)
    {
        var events = new List<IGameSimEvent>();
        ReconcileAndVerifyBoardInvariants(opName, dirtyCardIds, events, preSnapshot: null);
        return events;
    }

    private static void EmitMoveEventIfNotPresent(
        List<IGameSimEvent>? events,
        string instanceId,
        EInventorySection section,
        EContainerSocketId socket)
    {
        if (events == null) return;
        bool exists = events.Any(ev =>
            ev is GameSimEventCardMoved mv &&
            string.Equals(mv.InstanceId, instanceId, StringComparison.Ordinal) &&
            mv.ToInventory == section &&
            mv.ToSocket == socket);
        if (!exists)
            events.Add(new GameSimEventCardMoved(instanceId, ECombatantId.Player, section, socket));
    }

    private void SyncDisplacedCorePlacementsAndEmitDiffs(
        PlayerState corePlayer,
        IReadOnlyDictionary<string, (EInventorySection? Section, EContainerSocketId? Socket)> preSnapshot,
        ICollection<string>? dirtyCardIds,
        List<IGameSimEvent>? events,
        string? skipMoveEventForId = null)
    {
        void CheckSection(IEnumerable<CardInstance> cards, EInventorySection sec)
        {
            foreach (var core in cards)
            {
                var entry = FindEntryByCoreCard(core);
                if (entry == null || entry.Owner != ECombatantId.Player || entry.Type != ECardType.Item)
                    continue;
                int clampedSlot = Math.Clamp(core.StartSlot, 0, 9);
                core.StartSlot = clampedSlot;
                var socket = (EContainerSocketId)clampedSlot;
                bool entryDesync = entry.Section != sec || entry.Socket != socket;
                if (entryDesync)
                {
                    entry.Section = sec;
                    entry.Socket = socket;
                }

                bool movedFromPre = !preSnapshot.TryGetValue(entry.InstanceId, out var prev)
                                    || prev.Section != sec
                                    || prev.Socket != socket;
                if (entryDesync || movedFromPre)
                {
                    dirtyCardIds?.Add(entry.InstanceId);
                    if (!string.Equals(entry.InstanceId, skipMoveEventForId, StringComparison.Ordinal))
                        EmitMoveEventIfNotPresent(events, entry.InstanceId, sec, socket);
                }
            }
        }

        CheckSection(corePlayer.Hand.Cards, EInventorySection.Hand);
        CheckSection(corePlayer.Stash.Cards, EInventorySection.Stash);
    }

    private List<IGameSimEvent> ReconcileAndVerifyBoardInvariants(
        string opName,
        ICollection<string>? dirtyCardIds,
        List<IGameSimEvent>? events,
        IReadOnlyDictionary<string, (EInventorySection? Section, EContainerSocketId? Socket)>? preSnapshot = null,
        string? skipMoveEventForId = null)
    {
        _run.HandGrid.UnlockedMask = _run.UnlockedSlots;
        _run.StashGrid.UnlockedMask = 1023;

        var emittedEvents = new List<IGameSimEvent>();
        var baseline = preSnapshot ?? SnapshotPlayerItemPlacements();

        var ownedItems = _run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item)
            .OrderBy(c => c.Section == EInventorySection.Stash ? 1 : 0)
            .ThenBy(c => c.Socket.HasValue ? (int)c.Socket.Value : 99)
            .ThenBy(c => c.InstanceId, StringComparer.Ordinal)
            .ToList();

        var ownedCores = new HashSet<CardInstance>();
        foreach (var item in ownedItems)
        {
            // 若该卡已在 HandGrid/StashGrid 中，严禁调用 EnsureCoreCard 用陈旧的 entry.Socket 反向覆盖网格权威 StartSlot！
            CardInstance core;
            if (item.CoreCard != null &&
                (_run.HandGrid.Cards.Contains(item.CoreCard) || _run.StashGrid.Cards.Contains(item.CoreCard)))
            {
                core = item.CoreCard;
            }
            else
            {
                core = EnsureCoreCard(item);
            }
            SyncEntrySizeFromCore(item, core);
            ownedCores.Add(core);
        }

        // 1. 移除网格中不再属于当前玩家 Item 列表的幽灵引用
        _run.HandGrid.Cards.RemoveAll(c => !ownedCores.Contains(c));
        _run.StashGrid.Cards.RemoveAll(c => !ownedCores.Contains(c));

        // 2. 消除同一卡牌同时出现在 HandGrid 与 StashGrid 的双重占位
        var handSet = new HashSet<CardInstance>(_run.HandGrid.Cards);
        for (int i = _run.StashGrid.Cards.Count - 1; i >= 0; i--)
        {
            var sc = _run.StashGrid.Cards[i];
            if (!handSet.Contains(sc)) continue;
            var entry = FindEntryByCoreCard(sc);
            if (entry?.Section == EInventorySection.Stash)
                _run.HandGrid.Remove(sc);
            else
                _run.StashGrid.Remove(sc);
        }

        // 3. 若任一网格违反不变量（重叠/越界/未解锁/宽度），按当前位置重排修复
        if (!_run.HandGrid.ValidateInvariants(out string handErr))
        {
            LogBoardAudit(opName + ":RepairHand", null, null, null, null, handErr);
            RebuildGridDeterministically(_run.HandGrid, _run.StashGrid, EInventorySection.Hand);
        }
        if (!_run.StashGrid.ValidateInvariants(out string stashErr))
        {
            LogBoardAudit(opName + ":RepairStash", null, null, null, null, stashErr);
            RebuildGridDeterministically(_run.StashGrid, _run.HandGrid, EInventorySection.Stash);
        }

        // 4. 补放“在 RunState.Cards 但不在 HandGrid/StashGrid”的幽灵卡牌（如 Transform 宽度变大）
        foreach (var item in ownedItems)
        {
            var core = item.CoreCard!;
            bool inHand = _run.HandGrid.Cards.Contains(core);
            bool inStash = _run.StashGrid.Cards.Contains(core);
            if (inHand || inStash) continue;

            EInventorySection preferredSec = item.Section ?? EInventorySection.Hand;
            BoardGrid primary = preferredSec == EInventorySection.Stash ? _run.StashGrid : _run.HandGrid;
            BoardGrid fallback = preferredSec == EInventorySection.Stash ? _run.HandGrid : _run.StashGrid;
            int preferredSlot = item.Socket.HasValue ? (int)item.Socket.Value : core.StartSlot;

            bool placed = false;
            if (primary.CountFreeUnlockedSlots() >= core.SlotWidth)
            {
                if (preferredSlot >= 0)
                    placed = primary.TryPlaceWithDisplaceOrSwap(core, preferredSlot);
                if (!placed)
                    placed = primary.TryAddAuto(core);
            }
            if (!placed && fallback.CountFreeUnlockedSlots() >= core.SlotWidth)
            {
                placed = fallback.TryAddAuto(core);
            }

            if (placed)
            {
                dirtyCardIds?.Add(item.InstanceId);
            }
            else
            {
                LogBoardAudit(opName + ":GhostUnplaced", item.InstanceId, item.TemplateId.ToString(),
                    $"{item.Section}:{item.Socket}", "Unplaced", "no free space in hand or stash");
            }
        }

        // 5. 将唯一真值（HandGrid/StashGrid）写回 RunState.Cards，并为全部槽位变动卡牌记录 Dirty + GameSimEventCardMoved
        void CommitGrid(BoardGrid grid, EInventorySection sec)
        {
            foreach (var gridCard in grid.Cards)
            {
                var item = FindEntryByCoreCard(gridCard);
                if (item == null) continue;

                var newSocket = (EContainerSocketId)Math.Clamp(gridCard.StartSlot, 0, 9);
                SyncEntrySizeFromCore(item, gridCard);
                bool changed = item.Section != sec || item.Socket != newSocket;
                item.Section = sec;
                item.Socket = newSocket;

                bool changedFromBaseline = !baseline.TryGetValue(item.InstanceId, out var prev)
                                           || prev.Section != sec
                                           || prev.Socket != newSocket;
                if (changed || changedFromBaseline)
                {
                    dirtyCardIds?.Add(item.InstanceId);
                    if (!string.Equals(item.InstanceId, skipMoveEventForId, StringComparison.Ordinal))
                    {
                        bool alreadyEmitted = events != null && events.Any(ev =>
                            ev is GameSimEventCardMoved mv &&
                            mv.InstanceId == item.InstanceId &&
                            mv.ToInventory == sec &&
                            mv.ToSocket == newSocket);
                        if (!alreadyEmitted)
                        {
                            var mvEvt = new GameSimEventCardMoved(item.InstanceId, ECombatantId.Player, sec, newSocket);
                            emittedEvents.Add(mvEvt);
                            events?.Add(mvEvt);
                        }
                    }
                }
            }
        }

        CommitGrid(_run.HandGrid, EInventorySection.Hand);
        CommitGrid(_run.StashGrid, EInventorySection.Stash);
        return emittedEvents;
    }

    private void RebuildGridDeterministically(BoardGrid targetGrid, BoardGrid overflowGrid, EInventorySection targetSection)
    {
        var snapshot = targetGrid.Cards
            .OrderBy(c => c.StartSlot >= 0 ? c.StartSlot : 99)
            .ThenBy(c => c.InstanceId, StringComparer.Ordinal)
            .ToList();
        targetGrid.Cards.Clear();

        foreach (var card in snapshot)
        {
            int desired = card.StartSlot;
            bool placed = false;
            if (targetGrid.CountFreeUnlockedSlots() >= card.SlotWidth)
            {
                if (desired >= 0)
                    placed = targetGrid.TryPlaceWithDisplaceOrSwap(card, desired);
                if (!placed)
                    placed = targetGrid.TryAddAuto(card);
            }
            if (!placed && overflowGrid.CountFreeUnlockedSlots() >= card.SlotWidth)
            {
                if (overflowGrid.TryAddAuto(card))
                {
                    var entry = FindEntryByCoreCard(card);
                    if (entry != null)
                        entry.Section = targetSection == EInventorySection.Hand ? EInventorySection.Stash : EInventorySection.Hand;
                }
            }
        }
    }

    private void ReconcileTransformedEventPlacements(List<IGameSimEvent> events)
    {
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is not GameSimEventCardTransformed tr || tr.TransformedCards == null || tr.TransformedCards.Count == 0)
                continue;

            bool needsUpdate = false;
            var updatedList = new List<SimEventCardTransformation>(tr.TransformedCards.Count);
            foreach (var item in tr.TransformedCards)
            {
                if (_run.Cards.TryGetValue(item.InstanceId, out var entry) &&
                    (entry.Section != item.Section || entry.Socket != item.Socket))
                {
                    needsUpdate = true;
                    updatedList.Add(new SimEventCardTransformation(
                        item.InstanceId,
                        item.TemplateId,
                        item.Type,
                        item.CombatantId,
                        entry.Section,
                        entry.Socket));
                }
                else
                {
                    updatedList.Add(item);
                }
            }

            if (needsUpdate)
            {
                events[i] = new GameSimEventCardTransformed(tr.ExecutionContextId, tr.OriginalInstanceId, updatedList);
            }
        }
    }

    private void ClearPostCombatTransientStatusAndSync(List<IGameSimEvent> events, ICollection<string>? dirtyCardIds = null)
    {
        foreach (var entry in _run.Cards.Values)
        {
            if (entry.Owner != ECombatantId.Player) continue;
            entry.State = ECardState.Alive;
            if (entry.CoreCard == null) continue;

            var core = entry.CoreCard;
            core.FreezeMs = 0;
            core.SlowMs = 0;
            core.HasteMs = 0;
            core.IsFlying = false;
            core.IsDisabled = false;
            core.IsDestroyed = false;
            core.Heated = false;
            core.Chilled = false;
            core.CooldownProgressMs = 0;
            core.TimesFiredThisFight = 0;
            core.CombatAdd.Clear();
            core.CombatMult.Clear();
            core.CombatTags.Clear();
            core.TransformedFromInstanceId = null;
        }

        // 先清除 Combat 临时状态并重算局外光环，确保固有/光环飞行为 1、纯战斗临时状态为 0，弹药恢复至 EffectiveAmmoMax
        RecalculateAurasAndSyncBoard(events);

        foreach (var entry in _run.Cards.Values)
        {
            if (entry.Owner != ECombatantId.Player || entry.Type != ECardType.Item) continue;

            var core = entry.CoreCard;
            core?.RefillAmmoToMax();
            int ammoMax = core != null ? Math.Max(0, core.EffectiveAmmoMax) : entry.Attributes.GetValueOrDefault(ECardAttributeType.AmmoMax, 0);
            int outFlying = (core != null && core.GetAttribute("Flying") > 0) ? 1 : 0;
            int outHeated = (core != null && core.GetAttribute("Heated") > 0) ? 1 : 0;
            int outChilled = (core != null && core.GetAttribute("Chilled") > 0) ? 1 : 0;

            entry.Attributes[ECardAttributeType.Freeze] = 0;
            entry.Attributes[ECardAttributeType.Slow] = 0;
            entry.Attributes[ECardAttributeType.Haste] = 0;
            entry.Attributes[ECardAttributeType.Flying] = outFlying;
            if (ammoMax > 0 || entry.Attributes.ContainsKey(ECardAttributeType.Ammo))
                entry.Attributes[ECardAttributeType.Ammo] = ammoMax;

            // 在战后首帧 NetMessageGameSim.Data.Cards 显式下发这些属性的归零/弹药补满 SimUpdateCard，彻底清除客户端残余飞行/冰冻特效
            entry.PendingForceSyncAttributes[ECardAttributeType.Freeze] = 0;
            entry.PendingForceSyncAttributes[ECardAttributeType.Slow] = 0;
            entry.PendingForceSyncAttributes[ECardAttributeType.Haste] = 0;
            entry.PendingForceSyncAttributes[ECardAttributeType.Flying] = outFlying;
            entry.PendingForceSyncAttributes[ECardAttributeType.Heated] = outHeated;
            entry.PendingForceSyncAttributes[ECardAttributeType.Chilled] = outChilled;
            if (ammoMax > 0 || entry.Attributes.ContainsKey(ECardAttributeType.Ammo))
                entry.PendingForceSyncAttributes[ECardAttributeType.Ammo] = ammoMax;
            dirtyCardIds?.Add(entry.InstanceId);
        }
    }

    /// <summary>在战后升级、跨天与战利品触发完成后重填并强制同步玩家物品弹药，保证局外 Ammo 与最新 AmmoMax 一致。</summary>
    private void RefillPostAdvanceAmmoAndSync(ICollection<string>? dirtyCardIds = null)
    {
        foreach (var entry in _run.Cards.Values)
        {
            if (entry.Owner != ECombatantId.Player || entry.Type != ECardType.Item) continue;

            var core = entry.CoreCard;
            core?.RefillAmmoToMax();
            int ammoMax = core != null
                ? Math.Max(0, core.EffectiveAmmoMax)
                : entry.Attributes.GetValueOrDefault(ECardAttributeType.AmmoMax, 0);

            if (ammoMax > 0)
                entry.Attributes[ECardAttributeType.AmmoMax] = ammoMax;

            if (ammoMax > 0 || entry.Attributes.ContainsKey(ECardAttributeType.Ammo))
            {
                entry.Attributes[ECardAttributeType.Ammo] = ammoMax;
                entry.PendingForceSyncAttributes[ECardAttributeType.Ammo] = ammoMax;
                dirtyCardIds?.Add(entry.InstanceId);
            }
        }
    }

    private void ActivateEncounterCard(
        string? encId,
        RunState.CardEntry? selectedCard,
        List<IGameSimEvent> events,
        bool emitPurchaseIfSelected)
    {
        if (string.IsNullOrEmpty(encId))
        {
            if (!string.IsNullOrEmpty(_run.ActiveEncounterCardInstanceId) &&
                _run.Cards.Remove(_run.ActiveEncounterCardInstanceId, out var oldActive))
            {
                events.Add(new GameSimEventCardDisposed(oldActive.InstanceId, ECardDisposalReason.Cleared));
            }
            _run.ActiveEncounterCardInstanceId = null;
            return;
        }

        if (selectedCard != null)
        {
            if (!string.IsNullOrEmpty(_run.ActiveEncounterCardInstanceId) &&
                !string.Equals(_run.ActiveEncounterCardInstanceId, selectedCard.InstanceId, StringComparison.Ordinal) &&
                _run.Cards.Remove(_run.ActiveEncounterCardInstanceId, out var oldActive))
            {
                events.Add(new GameSimEventCardDisposed(oldActive.InstanceId, ECardDisposalReason.Cleared));
            }

            _run.SelectionSet.Remove(selectedCard.InstanceId);
            selectedCard.Owner = null;
            selectedCard.Section = null;
            selectedCard.Socket = EContainerSocketId.Socket_5;
            selectedCard.State = ECardState.Alive;
            _run.Cards[selectedCard.InstanceId] = selectedCard;
            _run.ActiveEncounterCardInstanceId = selectedCard.InstanceId;

            if (emitPurchaseIfSelected)
            {
                events.Add(new GameSimEventCardPurchased(
                    selectedCard.InstanceId,
                    0u,
                    EContainerSocketId.Socket_5,
                    null,
                    null));
            }
            return;
        }

        if (!string.IsNullOrEmpty(_run.ActiveEncounterCardInstanceId) &&
            _run.Cards.TryGetValue(_run.ActiveEncounterCardInstanceId, out var currentActive) &&
            Guid.TryParse(encId, out var encGuid) &&
            currentActive.TemplateId == encGuid)
        {
            currentActive.Owner = null;
            currentActive.Section = null;
            currentActive.Socket = EContainerSocketId.Socket_5;
            currentActive.State = ECardState.Alive;
            return;
        }

        if (!string.IsNullOrEmpty(_run.ActiveEncounterCardInstanceId) &&
            _run.Cards.Remove(_run.ActiveEncounterCardInstanceId, out var prevActive))
        {
            events.Add(new GameSimEventCardDisposed(prevActive.InstanceId, ECardDisposalReason.Cleared));
        }

        var poolTpl = _pool.Get(encId);
        var coreTpl = _repo.GetCard(encId);
        ECardType cardType = ShopEngine.ParseCardType(poolTpl?.Type ?? coreTpl?.Type ?? "EventEncounter");
        string prefix = cardType switch
        {
            ECardType.PedestalEncounter => "ped",
            ECardType.CombatEncounter => "cbt",
            ECardType.PvpEncounter => "pvp",
            _ => "enc",
        };

        RunState.CardEntry newActive;
        if (poolTpl != null)
        {
            newActive = _shop.CreateEncounterCandidate(poolTpl, cardType, prefix);
        }
        else
        {
            Guid tplGuid = Guid.TryParse(encId, out var parsed) ? parsed : Guid.Empty;
            newActive = new RunState.CardEntry
            {
                InstanceId = _shop.NewId(prefix),
                TemplateId = tplGuid,
                Type = cardType,
                Size = ShopEngine.ParseSize(coreTpl?.Size ?? "Medium"),
                Tier = ShopEngine.ParseTier(coreTpl?.StartingTier ?? "Bronze"),
            };
            RunState.PopulateMetaFromTemplate(newActive, null, coreTpl);
        }

        newActive.Owner = null;
        newActive.Section = null;
        newActive.Socket = EContainerSocketId.Socket_5;
        newActive.State = ECardState.Alive;
        _run.Cards[newActive.InstanceId] = newActive;
        _run.ActiveEncounterCardInstanceId = newActive.InstanceId;
        events.Add(new GameSimEventCardDealt(newActive.InstanceId, newActive.TemplateId.ToString(), newActive.Type));
        events.Add(new GameSimEventCardPurchased(newActive.InstanceId, 0u, EContainerSocketId.Socket_5, null, null));
    }

    private RunState.CardEntry? DetachActiveEncounterCardForShelfOp()
    {
        if (string.IsNullOrEmpty(_run.ActiveEncounterCardInstanceId)) return null;
        if (_run.Cards.Remove(_run.ActiveEncounterCardInstanceId, out var active))
        {
            _run.SelectionSet.Remove(active.InstanceId);
            return active;
        }
        return null;
    }

    private void RestoreActiveEncounterCardAfterShelfOp(RunState.CardEntry? activeCard)
    {
        if (activeCard == null) return;
        activeCard.Owner = null;
        activeCard.Section = null;
        activeCard.Socket = EContainerSocketId.Socket_5;
        activeCard.State = ECardState.Alive;
        _run.Cards[activeCard.InstanceId] = activeCard;
        _run.ActiveEncounterCardInstanceId = activeCard.InstanceId;
    }

    private List<IGameSimEvent> ClearShelfPreservingActiveEncounter(bool preserveActiveEncounter)
    {
        var active = preserveActiveEncounter ? DetachActiveEncounterCardForShelfOp() : null;
        var evs = _shop.ClearShelf();
        if (preserveActiveEncounter)
        {
            RestoreActiveEncounterCardAfterShelfOp(active);
        }
        else
        {
            _run.ActiveEncounterCardInstanceId = null;
            _run.ParentEncounterTemplateId = null;
        }
        return evs;
    }

    private List<IGameSimEvent> DealEncounterShelfPreservingActiveEncounter(
        string encId,
        bool isReroll,
        PlayerState? playerState,
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers)
    {
        var active = DetachActiveEncounterCardForShelfOp();
        bool skipSyntheticDiscounts = HasOfficialDealBuyPriceAbility(encId);
        var evs = _shop.DealEncounterShelf(
            encId,
            isReroll,
            playerState,
            ownedMaxTiers,
            _shop.ActiveRules,
            _shop.ArchetypeIndex,
            applySyntheticDiscounts: !skipSyntheticDiscounts);
        RestoreActiveEncounterCardAfterShelfOp(active);
        return evs;
    }

    private List<IGameSimEvent> DealHourEncountersAndClearActive()
    {
        _run.ActiveEncounterCardInstanceId = null;
        _run.ParentEncounterTemplateId = null;
        var events = _shop.DealHourEncounters(BuildCorePlayerFromRun(), _run.VisitedDayEncounterIds);
        if (_run.State == ERunState.Choice && _run.Hour == (uint)_mode.HoursInADay)
        {
            EnsurePendingHour6PvpOpponentLocked(events);
        }
        else if (_run.State == ERunState.Choice)
        {
            _run.PendingPvpGhost = null;
            _run.CurrentPvpOpponent = null;
        }
        return events;
    }

    private string ResolvePortraitAnchorEncounterId(string encId)
    {
        if (string.IsNullOrWhiteSpace(encId)) return encId;
        var tpl = _pool.Get(encId);
        var core = _repo.GetCard(encId);
        var cardType = ShopEngine.ParseCardType(tpl?.Type ?? core?.Type ?? "EventEncounter");
        if (cardType == ECardType.EncounterStep && !string.IsNullOrWhiteSpace(_run.ParentEncounterTemplateId))
            return _run.ParentEncounterTemplateId!;
        return encId;
    }

    private string? ResolveClientTransitionEncounterId(string? serverEncounterId)
    {
        if (string.IsNullOrWhiteSpace(serverEncounterId) || string.IsNullOrWhiteSpace(_run.ParentEncounterTemplateId))
            return serverEncounterId;
        var tpl = _pool.Get(serverEncounterId);
        var core = _repo.GetCard(serverEncounterId);
        var cardType = ShopEngine.ParseCardType(tpl?.Type ?? core?.Type ?? "EventEncounter");
        return cardType == ECardType.EncounterStep ? _run.ParentEncounterTemplateId : serverEncounterId;
    }

    private void NormalizeChildStepTransitionEvents(List<IGameSimEvent> events, string? serverEncounterId)
    {
        string? effectiveClientId = ResolveClientTransitionEncounterId(serverEncounterId);
        if (string.IsNullOrWhiteSpace(effectiveClientId) ||
            string.Equals(effectiveClientId, serverEncounterId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is GameSimEventStateTransitioned st &&
                st.ToState == ERunState.Encounter)
            {
                events[i] = new GameSimEventStateTransitioned(st.ToState, effectiveClientId);
            }
        }
    }

    private void EnsureActiveParentPortraitCardAnchored(List<IGameSimEvent> events)
    {
        string? candidateId = !string.IsNullOrWhiteSpace(_run.ParentEncounterTemplateId)
            ? _run.ParentEncounterTemplateId
            : _run.CurrentEncounterId;
        if (string.IsNullOrWhiteSpace(candidateId)) return;

        string anchorId = ResolvePortraitAnchorEncounterId(candidateId);
        if (!string.IsNullOrWhiteSpace(_run.ActiveEncounterCardInstanceId) &&
            _run.Cards.TryGetValue(_run.ActiveEncounterCardInstanceId, out var active) &&
            active.Type is ECardType.EventEncounter or ECardType.PedestalEncounter &&
            Guid.TryParse(anchorId, out var anchorGuid) &&
            active.TemplateId == anchorGuid)
        {
            active.Owner = null;
            active.Section = null;
            active.Socket = EContainerSocketId.Socket_5;
            active.State = ECardState.Alive;
            return;
        }

        ActivateEncounterCard(anchorId, selectedCard: null, events, emitPurchaseIfSelected: false);
    }

    private void EnsureRunExclusionSetInitialized()
    {
        if (_run.ExclusionSetInitialized) return;
        if (_repo.GameMode?.ExclusionSet != null)
        {
            foreach (string id in _repo.GameMode.ExclusionSet)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    _run.ExclusionSet.Add(id);
            }
        }
        _run.ExclusionSetInitialized = true;
    }

    private void ApplyExclusionSetActionsFromExecutedStep(RunState.CardEntry stepEntry, PlayerState preStepPlayer)
    {
        var coreTpl = stepEntry.CoreCard?.Template ?? _repo.GetCard(stepEntry.TemplateId.ToString());
        if (coreTpl?.Abilities == null || coreTpl.Abilities.Count == 0) return;

        var preStepCtx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, preStepPlayer, preStepPlayer, _rng)
        {
            CurrentDay = (int)_run.Day,
            CurrentHour = (int)_run.Hour,
            CurrentEncounterId = _run.CurrentEncounterId,
            PlayMode = _run.PlayMode.ToString(),
            SelfCard = stepEntry.CoreCard,
            TriggerSourceCard = stepEntry.CoreCard,
        };

        foreach (var ability in coreTpl.Abilities.Values)
        {
            if (ability.Trigger == null ||
                !string.Equals(ability.Trigger.Type, "TTriggerOnCardSelected", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ability.Prerequisites, preStepCtx))
                continue;

            ApplyExclusionSetRuleNode(ability.Action);
        }
    }

    private void ApplyExclusionSetRuleNode(RuleNode? node)
    {
        if (node == null) return;
        switch (node.Type)
        {
            case "TActionAnd":
                if (node.Actions != null)
                {
                    foreach (var sub in node.Actions)
                        ApplyExclusionSetRuleNode(sub);
                }
                break;

            case "TActionGameAddToExclusionSet":
                EnsureRunExclusionSetInitialized();
                if (node.Ids != null)
                {
                    foreach (string id in node.Ids)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                            _run.ExclusionSet.Add(id);
                    }
                }
                break;

            case "TActionGameRemoveFromExclusionSet":
                EnsureRunExclusionSetInitialized();
                if (node.Ids != null)
                {
                    foreach (string id in node.Ids)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                            _run.ExclusionSet.Remove(id);
                    }
                }
                break;
        }
    }

    private static bool HasFlagAttributeOrTag(
        RunState.CardEntry card,
        BazaarCore.Models.CardTemplate? coreTpl,
        CardPool.CardTemplate? poolTpl,
        string flagName)
    {
        if (card.CoreCard != null)
        {
            if (card.CoreCard.HasTag(flagName) || card.CoreCard.HasHiddenTag(flagName))
                return true;
            if (card.CoreCard.GetAttribute(flagName) > 0 ||
                (card.CoreCard.BaseAttributes.TryGetValue(flagName, out double bVal) && bVal > 0) ||
                (card.CoreCard.PermanentAdd.TryGetValue(flagName, out double pVal) && pVal > 0) ||
                (card.CoreCard.HourAdd.TryGetValue(flagName, out double hVal) && hVal > 0) ||
                (card.CoreCard.DayAdd.TryGetValue(flagName, out double dVal) && dVal > 0) ||
                (card.CoreCard.AuraAdd.TryGetValue(flagName, out double aVal) && aVal > 0))
            {
                return true;
            }
        }
        if (coreTpl != null &&
            ((coreTpl.Tags != null && coreTpl.Tags.Any(t => t.Equals(flagName, StringComparison.OrdinalIgnoreCase))) ||
             (coreTpl.HiddenTags != null && coreTpl.HiddenTags.Any(t => t.Equals(flagName, StringComparison.OrdinalIgnoreCase))) ||
             (coreTpl.Attributes != null && coreTpl.Attributes.TryGetValue(flagName, out double tplVal) && tplVal > 0)))
        {
            return true;
        }
        if (poolTpl != null &&
            ((poolTpl.Tags != null && poolTpl.Tags.Any(t => t.Equals(flagName, StringComparison.OrdinalIgnoreCase))) ||
             (poolTpl.HiddenTags != null && poolTpl.HiddenTags.Any(t => t.Equals(flagName, StringComparison.OrdinalIgnoreCase)))))
        {
            return true;
        }
        return false;
    }

    private bool IsCardUnsellable(RunState.CardEntry card)
    {
        string tplId = card.TemplateId.ToString();
        return HasFlagAttributeOrTag(card, _repo.GetCard(tplId), _pool.Get(tplId), "Unsellable");
    }

    private bool HasCardAutoSellOnExitFlag(RunState.CardEntry card)
    {
        string tplId = card.TemplateId.ToString();
        return HasFlagAttributeOrTag(card, _repo.GetCard(tplId), _pool.Get(tplId), "WillAutoSellOnExit");
    }

    private void ApplyWillAutoSellOnExitIfNeeded(List<IGameSimEvent> events)
    {
        bool ruleAutoSell = _run.SelectionRules.WillAutoSellOnExit;

        var candidates = new List<RunState.CardEntry>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        // 1. 若当前 SelectionRules.WillAutoSellOnExit == true，结算留在货架未拿取（Owner == null）的 Item/Skill
        //    （严禁误卖已拿入 Hand/Stash/Skills 的普通卡）
        if (ruleAutoSell)
        {
            foreach (string selId in _run.SelectionSet.ToList())
            {
                if (string.Equals(selId, _run.ActiveEncounterCardInstanceId, StringComparison.Ordinal))
                    continue;
                if (!_run.Cards.TryGetValue(selId, out var entry)) continue;
                if (entry.Owner != null) continue;
                if (entry.Type is not (ECardType.Item or ECardType.Skill)) continue;
                if (IsCardUnsellable(entry)) continue;
                if (seenIds.Add(entry.InstanceId))
                    candidates.Add(entry);
            }
        }

        // 2. 对明确携带 WillAutoSellOnExit 标记/属性的已持有或货架转移卡，离开 Encounter/Loot 时自动出售兑现
        foreach (var entry in _run.Cards.Values.ToList())
        {
            if (string.Equals(entry.InstanceId, _run.ActiveEncounterCardInstanceId, StringComparison.Ordinal))
                continue;
            if (entry.Type is not (ECardType.Item or ECardType.Skill)) continue;
            if (!HasCardAutoSellOnExitFlag(entry)) continue;
            if (IsCardUnsellable(entry)) continue;
            if (seenIds.Add(entry.InstanceId))
                candidates.Add(entry);
        }

        if (candidates.Count == 0) return;

        int autoSoldGold = 0;
        bool removedOwned = false;
        foreach (var entry in candidates)
        {
            int sell = entry.CoreCard != null
                ? Math.Max(0, entry.CoreCard.GetSellPrice(_repo.GameMode.StandardPrices))
                : 0;
            if (sell == 0 && entry.Attributes.TryGetValue(ECardAttributeType.SellPrice, out int attrSell) && attrSell > 0)
                sell = attrSell;

            autoSoldGold += Math.Max(0, sell);
            if (entry.Owner == ECombatantId.Player)
            {
                RemoveOwnedEntryFromRun(entry);
                removedOwned = true;
            }
            else
            {
                _run.SelectionSet.Remove(entry.InstanceId);
                _run.Cards.Remove(entry.InstanceId);
            }

            events.Add(new GameSimEventCardSold(entry.InstanceId, (uint)Math.Max(0, sell)));
            events.Add(new GameSimEventCardDisposed(entry.InstanceId, ECardDisposalReason.Sold));
        }

        if (autoSoldGold > 0)
        {
            _run.AddPlayerGold(autoSoldGold);
            RecalculateAurasAndSyncBoard(events);
            LogBoardAudit("WillAutoSellOnExit", _run.CurrentEncounterId, _run.CurrentEncounterId, "Shelf", $"AutoSold(+{autoSoldGold}g)");
        }
        if (removedOwned)
        {
            ReconcileAndVerifyBoardInvariants("WillAutoSellOnExit", null, events);
        }
    }

    private void EnsurePendingHour6PvpOpponentLocked(List<IGameSimEvent>? events = null)
    {
        if (_runEnded || _run.State != ERunState.Choice || _run.Hour != (uint)_mode.HoursInADay)
            return;

        int day = (int)_run.Day;
        var tourneyLobby = _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId);
        MatchupAssignment? tourneyAssignment = null;
        int fallbackIncome = Math.Max(1, tourneyLobby?.Rules?.StartingIncome ?? _run.CustomRules?.StartingIncome ?? _mode.StartingIncome);

        if (tourneyLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId))
        {
            tourneyAssignment = _lobbies!.EnsureAndGetAssignment(tourneyLobby, _run.Day, _run.AccountId, _sessions);
            if (tourneyAssignment != null && !tourneyAssignment.OpponentIsBot && _sessions != null)
            {
                var oppMem = _sessions.GetByAccount(tourneyAssignment.OpponentAccountId);
                var liveRun = oppMem != null ? Endpoints.SessionRegistry.Get(oppMem.Sid)?.Run : null;
                if (liveRun != null &&
                    liveRun.Day >= _run.Day &&
                    liveRun.Cards.Values.Any(c =>
                        c.Owner == ECombatantId.Player &&
                        c.Type == ECardType.Item &&
                        c.Section != EInventorySection.Stash))
                {
                    EHero liveHero = liveRun.Hero == EHero.Common ? EHero.Vanessa : liveRun.Hero;
                    int liveHp = Math.Max(100, liveRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250));
                    int livePrestige = Math.Max(1, liveRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20));
                    int liveLevel = Math.Max(1, liveRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1));
                    int liveGold = Math.Max(0, liveRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0));
                    int liveIncome = liveRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, fallbackIncome);
                    _run.CurrentPvpOpponent = new SimPvpOpponent(
                        name: tourneyAssignment.OpponentUsername,
                        titlePrefix: "",
                        titleSuffix: "",
                        rank: null,
                        rating: 1200,
                        division: 1,
                        victories: liveRun.Victories,
                        prestige: livePrestige,
                        level: liveLevel,
                        hero: liveHero,
                        playerLoadoutResponse: new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout { accountId = tourneyAssignment.OpponentAccountId },
                        playerCollection: null);
                    _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(
                        liveHp,
                        liveHp,
                        prestige: livePrestige,
                        level: liveLevel,
                        gold: liveGold,
                        income: liveIncome > 0 ? liveIncome : fallbackIncome);
                    EmitHour6OpponentInitializedIfNeeded(events, liveHero);
                    return;
                }
            }
        }

        int expectedBucketDay = Math.Clamp(day, 1, 15);
        bool ghostValid = _run.PendingPvpGhost != null
                       && _run.PendingPvpGhost.Day == expectedBucketDay
                       && (_run.IsCustomRuleRun || (!_run.PendingPvpGhost.IsCustomRule && Storage.Persistence.IsSaneGhostForDay(expectedBucketDay, _run.PendingPvpGhost.HealthMax)));
        if (!ghostValid)
        {
            var ghostRng = tourneyAssignment != null ? new Random(tourneyAssignment.BattleSeed) : _rng;
            _run.PendingPvpGhost = _ghosts.PickOpponent(
                day,
                (int)_run.Victories,
                (int)_run.Defeats,
                ghostRng,
                _repo,
                includeCustomRuleGhosts: _run.IsCustomRuleRun);
        }

        var ghost = _run.PendingPvpGhost!;
        string oppAccountId = tourneyAssignment?.OpponentAccountId
            ?? (!string.IsNullOrWhiteSpace(ghost.BoardId) ? $"ghost_{ghost.BoardId}" : $"ghost_{Guid.NewGuid():N}");
        string oppDisplayName = tourneyAssignment != null
            ? tourneyAssignment.OpponentUsername
            : (string.IsNullOrWhiteSpace(ghost.Hero) ? "Challenger" : $"{ghost.Hero} ({ghost.PowerBucket})");
        EHero oppHero = PvpGhostPool.ParseHero(ghost.Hero);

        int previewHp = Math.Max(100, ghost.HealthMax);
        if (tourneyLobby != null && tourneyAssignment is { OpponentIsBot: true })
        {
            int baseHp = _mode.StartingHealth > 0 ? _mode.StartingHealth : 250;
            previewHp = Math.Max(50, previewHp + (tourneyLobby.Rules.StartingHealth - baseHp));
        }

        _run.CurrentPvpOpponent = new SimPvpOpponent(
            name: oppDisplayName,
            titlePrefix: "",
            titleSuffix: "",
            rank: null,
            rating: 1000 + (int)(ghost.EstimatedWinRate * 500),
            division: 1,
            victories: (uint)Math.Max(0, ghost.Wins),
            prestige: Math.Max(1, ghost.Prestige),
            level: Math.Max(1, ghost.Level),
            hero: oppHero,
            playerLoadoutResponse: new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout { accountId = oppAccountId },
            playerCollection: null);
        _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(
            previewHp,
            previewHp,
            prestige: Math.Max(1, ghost.Prestige),
            level: Math.Max(1, ghost.Level),
            gold: 0,
            income: fallbackIncome);
        EmitHour6OpponentInitializedIfNeeded(events, oppHero);
    }

    private static void EmitHour6OpponentInitializedIfNeeded(List<IGameSimEvent>? events, EHero oppHero)
    {
        if (events == null) return;
        bool alreadyEmitted = events.Any(e =>
            e is GameSimEventPlayerInitialized init &&
            init.CombatantId == ECombatantId.Opponent &&
            init.Hero == oppHero);
        if (!alreadyEmitted)
        {
            events.Add(new GameSimEventPlayerInitialized
            {
                CombatantId = ECombatantId.Opponent,
                Hero = oppHero,
            });
        }
    }

    private LobbyStore.PvpBarrierDecision? CheckTournamentPvpBarrier()
    {
        if (_lobbies == null || string.IsNullOrWhiteSpace(_run.AccountId))
            return null;
        return _lobbies.CheckPvpBarrier(_run.TournamentSessionId, _run.AccountId, _run.Day, _run, _sessions);
    }

    private static bool IsEncounterTypeName(string? typeName)
        => !string.IsNullOrWhiteSpace(typeName)
        && (typeName.Equals("EventEncounter", StringComparison.OrdinalIgnoreCase)
            || typeName.Equals("CombatEncounter", StringComparison.OrdinalIgnoreCase)
            || typeName.Equals("PvpEncounter", StringComparison.OrdinalIgnoreCase)
            || typeName.Equals("PedestalEncounter", StringComparison.OrdinalIgnoreCase)
            || typeName.Equals("EncounterStep", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 进战（PvE/PvP）生成 CombatSpawnSim 前，彻底销毁所有仍留在 SelectionSet 或处于无主/遭遇态的卡，
    /// 仅按需保留 PvE 击败后要在 Loot 顶栏展示的 ActiveEncounterCardInstanceId（PvP 一律全清）。
    /// </summary>
    private void CleanupSelectionAndEncountersBeforeCombat(List<IGameSimEvent> events, bool preserveActiveMonsterPortrait)
    {
        _run.ParentEncounterTemplateId = null;
        string? keepActiveId = preserveActiveMonsterPortrait ? _run.ActiveEncounterCardInstanceId : null;
        if (!preserveActiveMonsterPortrait)
            _run.ActiveEncounterCardInstanceId = null;

        var disposed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string selId in _run.SelectionSet.ToList())
        {
            if (string.Equals(selId, keepActiveId, StringComparison.Ordinal))
                continue;
            if (_run.Cards.Remove(selId) && disposed.Add(selId))
                events.Add(new GameSimEventCardDisposed(selId, ECardDisposalReason.Cleared));
        }
        _run.SelectionSet.Clear();

        var staleIds = _run.Cards.Values
            .Where(c => !string.Equals(c.InstanceId, keepActiveId, StringComparison.Ordinal) &&
                        (c.Owner == null ||
                         c.Type is ECardType.EventEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter or ECardType.PedestalEncounter or ECardType.EncounterStep))
            .Select(c => c.InstanceId)
            .ToList();
        foreach (string sid in staleIds)
        {
            if (_run.Cards.Remove(sid) && disposed.Add(sid))
                events.Add(new GameSimEventCardDisposed(sid, ECardDisposalReason.Cleared));
        }
    }

    /// <summary>
    /// 将锦标赛房间定制的起始生命差值、初始装备（如有空闲格子）及初始技能公平同步赋予 AI 对手，避免成为单方数值碾压。
    /// </summary>
    private void ApplyTournamentRulesToAiBot(PlayerState p2Core, CustomRunRuleSet? rules)
    {
        if (rules == null) return;
        bool mutated = false;

        int baseStartHp = _mode.StartingHealth > 0 ? _mode.StartingHealth : 250;
        int hpDelta = rules.StartingHealth - baseStartHp;
        if (hpDelta != 0)
        {
            int adjustedHp = Math.Max(50, p2Core.HealthMax + hpDelta);
            p2Core.Attributes["HealthMax"] = adjustedHp;
            p2Core.Attributes["Health"] = adjustedHp;
            p2Core.CurrentHealth = adjustedHp;
            mutated = true;
        }

        if (rules.StartingItems is { Count: > 0 })
        {
            int itemSeq = 0;
            foreach (var spec in rules.StartingItems)
            {
                var ci = TryCreatePresetCoreCard(spec, $"ai_itm_{itemSeq++}", allowEnchantment: true);
                if (ci == null || !string.Equals(ci.Template.Type, "Item", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (p2Core.Hand.CountFreeUnlockedSlots() >= ci.SlotWidth && p2Core.Hand.TryAddAuto(ci))
                    mutated = true;
            }
        }

        if (rules.StartingSkills is { Count: > 0 })
        {
            int skillSeq = 0;
            foreach (var spec in rules.StartingSkills)
            {
                var ci = TryCreatePresetCoreCard(spec, $"ai_skl_{skillSeq++}", allowEnchantment: false);
                if (ci == null || !string.Equals(ci.Template.Type, "Skill", StringComparison.OrdinalIgnoreCase))
                    continue;
                p2Core.Skills.Add(ci);
                mutated = true;
            }
        }

        if (mutated)
        {
            p2Core.PrepareForCombat();
        }
    }

    private void LogBoardAudit(
        string opName,
        string? cardId,
        string? templateId,
        string? fromLoc,
        string? toLoc,
        string? rejectReason = null)
    {
        string maskBits = Convert.ToString(_run.UnlockedSlots & 0x3FF, 2).PadLeft(10, '0');
        string line =
            $"[BoardAudit] op={opName} state={_run.State} card={cardId ?? "-"} tpl={templateId ?? "-"} " +
            $"from={fromLoc ?? "-"} to={toLoc ?? "-"} unlockMask=0x{_run.UnlockedSlots:X3}({maskBits}) " +
            $"hand={_run.HandGrid.FormatOccupancyDiagram()} stash={_run.StashGrid.FormatOccupancyDiagram()}" +
            (string.IsNullOrEmpty(rejectReason) ? "" : $" reject={rejectReason}");
        if (_boardAuditLogs.Count >= 128)
            _boardAuditLogs.RemoveAt(0);
        _boardAuditLogs.Add(line);
        Console.WriteLine(line);
    }
}
