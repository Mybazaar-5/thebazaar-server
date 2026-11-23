using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarGameShared.TempoNet.Responses;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Protocol;
using BazaarServer.Storage;
using BazaarServer.TempoNet;
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
    private readonly ITournamentPvpCoordinator? _pvpCoordinator;
    private readonly ISocialAndReplaySink? _socialSink;
    private readonly AccountStore? _accounts;
    private readonly ChallengeService? _challenges;
    private readonly Persistence? _db;
    private Random _rng;

    // 玩家局外限时属性层（UntilEndOfHour / UntilEndOfDay），直接绑定 RunState 以便无损快照持久化
    private readonly Dictionary<string, double> _playerHourAdd;
    private readonly Dictionary<string, double> _playerHourMult;
    private readonly Dictionary<string, double> _playerDayAdd;
    private readonly Dictionary<string, double> _playerDayMult;

    private bool _runEnded;
    private int _attrChangeDispatchDepth;
    private Data.MonsterTemplate? _lastCombatMonster;
    private readonly List<string> _boardAuditLogs = new();

    /// <summary>P1-37：直接代理 RunState.StartSelectionDone，作为唯一持久化真相。</summary>
    public bool StartSelectionDone
    {
        get => _run.StartSelectionDone;
        private set => _run.StartSelectionDone = value;
    }
    public IReadOnlyList<string> BoardAuditLogs => _boardAuditLogs;
    public RunState State => _run;
    /// <summary>本局活跃的核心数据仓库（包含本局生效的全局/社区/按房隔离 Mod）。</summary>
    public GameDataRepository ActiveRepository => _repo;
    /// <summary>本局活跃的卡池实例（与 ActiveRepository 对齐）。</summary>
    public CardPool ActivePool => _pool;
    /// <summary>本局活跃的野怪模板列表。</summary>
    public List<Data.MonsterTemplate> ActiveMonsters => _monsters;
    /// <summary>本局活跃的流派索引。</summary>
    public CardArchetypeIndex? ActiveArchetypeIndex => _archetypeIndex;
    /// <summary>P1-13：记录本局最近一次完赛通行证 XP 实际落盘的赛季 ID（用于隔离并发环境变量波动）。</summary>
    public int LastAwardedPassSeasonId { get; private set; } = 1;

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
        SessionStore? sessions = null,
        ITournamentPvpCoordinator? pvpCoordinator = null,
        ISocialAndReplaySink? socialSink = null,
        AccountStore? accounts = null,
        ChallengeService? challenges = null,
        Persistence? db = null)
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
        _pvpCoordinator = pvpCoordinator;
        _socialSink = socialSink;
        _accounts = accounts;
        _challenges = challenges;
        _db = db;
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
        // P1-37：以 RunState.StartSelectionDone 为权威值；仅对未设置该标志但已越过 Day1/Hour1/Starter 的兼容旧构造补推断，
        // 绝不因玩家持有预设开局物品/技能（StartingItems/GoldenStart）而错判 StartSelectionDone=true
        if (!_run.StartSelectionDone &&
            (_run.Day > 1 || _run.Hour > 1 || _run.State != ERunState.Encounter ||
             (!string.IsNullOrEmpty(_run.CurrentEncounterId) &&
              !string.Equals(_run.CurrentEncounterId, ShopEngine.GetStarterEncounterId(_run.Hero), StringComparison.OrdinalIgnoreCase))))
        {
            _run.StartSelectionDone = true;
        }
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

        // Bug #11 & P2-33：恢复会话处于 Encounter 或 Pedestal 态时均需同步重建当前遭遇核心卡
        if (!_runEnded && _run.State is ERunState.Encounter or ERunState.Pedestal &&
            _run.CurrentEncounterCoreCard == null &&
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
            if (_run.State == ERunState.Choice)
            {
                bool isPvpHour = _run.Hour == (uint)_mode.HoursInADay;
                bool missingHour6Pvp = isPvpHour && !_run.SelectionSet.Any(id =>
                    _run.Cards.TryGetValue(id, out var ce) && ce.Type == ECardType.PvpEncounter);
                if (_run.SelectionSet.Count == 0 || missingHour6Pvp)
                {
                    // V13.1 Hour 6 Resume 自愈：若 Choice 候选集为空或 Hour 6 缺少 PvpEncounter，立即重建防死局
                    _run.SelectionsMadeInCurrentContext = 0;
                    DealHourEncountersAndClearActive();
                }
                if (isPvpHour)
                {
                    EnsurePendingHour6PvpOpponentLocked();
                }
            }
            ReconcileAndVerifyBoardInvariants("ResumeInit");
            // P1-31：恢复会话尾部执行无副作用局外光环重算（重建 CoreCard.AuraAdd/AuraMult/AuraTags 与 PlayerAura，不发射事件、不污染 PermanentAdd）
            if (_run.Cards.Count > 0)
            {
                RecalculateAurasSilentlyOnResume();
            }
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
        /// <summary>为 true 时，在 GameSim 之后末尾追加一条 NetMessageGameStateSync（用于降级落位强制对齐坐标且不吞包）。</summary>
        public bool AppendStateSyncAtTail { get; set; }
        /// <summary>需要临时附带到本帧 GameSim.Cards 的瞬态卡牌（如技能融合源卡 Placement(Owner=Player)）。</summary>
        public List<RunState.CardEntry> ExtraSimCards { get; } = new();
        /// <summary>P2-32：Pedestal 链式跳转（NextEncounterOnExit -> Encounter/Pedestal）时的第一帧 GameSim 快照（仅含祭坛结算事件，不含下一跳 StateTransitioned）。</summary>
        public GameSim? PreSplitGameSim { get; set; }
        /// <summary>P2-32：Pedestal 链式跳转时的第二帧事件流（由 GameServerEndpoints 包装为独立第二条 NetMessageGameSim）。</summary>
        public List<IGameSimEvent>? SecondaryGameSimEvents { get; set; }
    }

    // ── 开局 ────────────────────────────────────────────────────────────────

    public Outcome StartRun()
    {
        var o = new Outcome();
        _run.CustomRules = ResolveEffectiveRulesForStart();
        // 每日同种子挑战：强制对齐当日 UTC+8 确定性配方（主种子 + 词条集合）
        if (_run.CustomRules.SpecialModeId == ESpecialModeId.DailyChallenge)
        {
            var dailySpec = DailyChallengeSeedGenerator.GenerateSpec(_run.CustomRules.DailyChallengeDate);
            _run.CustomRules.DailyChallengeDate = dailySpec.DateKey;
            _run.CustomRules.FixedRunSeed = dailySpec.MasterSeed;
            _run.CustomRules.MutatorIds = new List<string>(dailySpec.MutatorIds);
            if (_run.CustomRules.HasMutator(MutatorCatalog.IronmanNoFates))
            {
                _run.CustomRules.DisableFatesRevive = true;
                _run.CustomRules.StartingPrestige = Math.Min(_run.CustomRules.StartingPrestige, 10);
            }
            _run.CustomRules = _run.CustomRules.CloneNormalized();
        }

        if (_run.CustomRules.FixedRunSeed is int fixedSeed && fixedSeed != 0)
        {
            _run.RngSeed = fixedSeed;
            _rng = new Random(fixedSeed);
        }

        _run.TendencyLocked = !_run.CustomRules.AllowTendencySelection;
        _shop.ActiveRules = _run.CustomRules;
        _shop.ArchetypeIndex = _archetypeIndex;

        _run.Day = 1;
        _run.Hour = 1;
        _run.Victories = 0;
        _run.Defeats = 0;
        _run.BossRushWavesCleared = 0;
        _run.EndlessTenWinCelebrated = false;
        _run.DailyRerollSequence = 0;
        bool disableFates = _run.CustomRules.DisableFatesRevive
            || _run.CustomRules.HasMutator(MutatorCatalog.IronmanNoFates);
        _run.FatesUsed = disableFates;
        _run.State = ERunState.Encounter;

        // 沙盒实验室解锁全 10 格，全金开局（golden_start）解锁 8 格，其余对齐官方 4 格
        _run.UnlockedSlots = (ushort)(_run.IsSandboxRun
            ? 0b11_1111_1111
            : (_run.CustomRules.HasMutator(MutatorCatalog.GoldenStart) ? 0b01_1111_1110 : 0b00_0111_1000));
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
        _run.CurrentMaxSelections = null;
        _run.BaseRerollCost = null;
        _run.LevelUpEntryUnlockedSlots = null;
        _run.PendingResumeRunCompletedEvent = null;
        _run.ActiveShelfDiscounts.Clear();
        _run.LastOutOfCombatAuraTargets.Clear();
        _run.PendingClearExpeditionAfterLoot = false;
        _run.ClearActiveReplacementSet();
        _run.CurrentBoardId = null;
        _run.PendingTransientSimCards.Clear();
        _run.CurrentEncounterCoreCard = null;
        _run.ActiveEncounterCardInstanceId = null;
        _run.ParentEncounterTemplateId = null;
        _run.VisitedSingleSpawnEncounterIds.Clear();
        _run.VisitedDayEncounterIds.Clear();
        _run.ClearOfferedDayEncounters();
        _run.ExclusionSet.Clear();
        _run.ExclusionSetInitialized = false;
        EnsureRunExclusionSetInitialized();
        _boardAuditLogs.Clear();
        _runEnded = false;
        ClearTrackedPlayerTempModifiers(clearHour: true, clearDay: true, clearAura: true);

        _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(100, 100, gold: 0, income: 5);

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

        ApplyStartingStats(_run.CustomRules, o.Events);
        if (IsTournamentRun())
            _run.PlayerAttributes[EPlayerAttributeType.Custom_11] = 1;

        GrantStarterPresets(_run.CustomRules, o.Events);

        // 先派发开局生命周期触发（Q45：先注入官方开局 PlayerEffect 以触发 Jules 冰火插座 15d06ad8 等），
        // 确保所有开局属性与经验变动全部结算后再决定首屏状态，避免同帧先发 Choice 遭遇再立刻销毁切 LevelUp 导致客户端残留僵尸事件！
        var p1Core = BuildCorePlayerFromRun();
        OutOfCombatController.InjectOfficialPlayerEffects(
            p1Core,
            _repo,
            includeTournamentMode: false,
            includeHeroRunStartSpawners: true);
        var startBatch = OutOfCombatController.OnRunStarted(p1Core, _repo, _rng);
        ApplyOutOfCombatBatch(p1Core, startBatch, o.Events, syncBoardDiffs: false);

        if (_run.PendingLevelUp)
        {
            // 开局即满足升级条件（如自定义初始经验 >= 8）：压入空 Choice 快照直接进入 LevelUp，
            // 升级选完后再由 AdvanceAfterSubState 正常发出 Day 1 Hour 1 的 3 个事件，绝不同帧双切发废卡
            StartSelectionDone = true;
            _run.SelectionsMadeInCurrentContext = 0;
            PushChoiceAndEnterLevelUp(o.Events);
        }
        else
        {
            // 接入官方多英雄多样化开局（直接进入 Day 1 Hour 1 Choice 并传入当日去重集合）
            _run.SelectionsMadeInCurrentContext = 0;
            o.Events.AddRange(DealHourEncountersAndClearActive());
            StartSelectionDone = _run.State == ERunState.Choice;

            if (_run.State is ERunState.Encounter or ERunState.Pedestal && !string.IsNullOrEmpty(_run.CurrentEncounterId))
            {
                _run.CurrentEncounterCoreCard = CreateEncounterCoreCard(_run.CurrentEncounterId);
                ActivateEncounterCard(_run.CurrentEncounterId, selectedCard: null, o.Events, emitPurchaseIfSelected: false);
            }
            TriggerOnEncounterEnteredAndDealt(_run.CurrentEncounterId, o.Events);
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
        int startHp = Math.Max(1, rules.StartingHealth != 300 ? rules.StartingHealth : _mode.StartingHealth);
        int startGold = Math.Max(0, rules.StartingGold != 15 ? rules.StartingGold : _mode.StartingGold);
        int startIncome = Math.Max(0, rules.StartingIncome != 5 ? rules.StartingIncome : _mode.StartingIncome);
        int startPrestige = Math.Max(1, rules.StartingPrestige != 20 ? rules.StartingPrestige : _mode.PrestigeInitial);
        int startExp = Math.Max(0, rules.StartingExperience != 3 ? rules.StartingExperience : _mode.StartingExperience);

        if (rules.HasMutator(MutatorCatalog.DoubleEconomy))
        {
            startGold = Math.Min(9999, startGold * 2);
            startIncome = Math.Min(999, startIncome * 2);
        }
        if (rules.DisableFatesRevive || rules.HasMutator(MutatorCatalog.IronmanNoFates))
        {
            startPrestige = Math.Min(startPrestige, 10);
        }

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
            // P1-44：按规范化 TemplateId 对开局预设技能去重，防止重复技能绕过技能唯一约束
            var grantedSkillIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in _run.Cards.Values)
            {
                if (existing.Owner == ECombatantId.Player && existing.Type == ECardType.Skill)
                    grantedSkillIds.Add(RunState.ResolveCoreTemplateId(existing));
            }
            foreach (var spec in rules.StartingSkills)
            {
                string normId = spec?.TemplateId?.Trim() ?? "";
                if (string.IsNullOrEmpty(normId) || !grantedSkillIds.Add(normId))
                    continue;
                var ci = TryCreatePresetCoreCard(spec!, instanceId: null, allowEnchantment: false);
                if (ci == null) continue;
                grantedSkillIds.Add(ci.Template.Id);
                RegisterSpawnedCoreCard(ci, null, preferredSlot: -1, emitSpawnEvent: true, events);
                grantedAny = true;
            }
        }

        // 全金开局词条（golden_start）：自动发放 2 件随机 Gold 物品 + 1 个 Gold 技能
        if (rules.HasMutator(MutatorCatalog.GoldenStart))
        {
            string heroName = _run.Hero == EHero.Common ? "Vanessa" : _run.Hero.ToString();
            var startRng = rules.FixedRunSeed is int seed && seed != 0
                ? DailyChallengeSeedGenerator.CreateStreamRng(seed, "golden_start", 1, 1, 0)
                : _rng;
            var itemPool = _pool.AllItems
                .Where(c => _pool.IsPlayerSpawnable(c) &&
                            (rules.SpecialModeId == ESpecialModeId.CrossHeroMayhem ||
                             c.Heroes.Length == 0 ||
                             c.Heroes.Contains(heroName, StringComparer.OrdinalIgnoreCase) ||
                             c.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase)))
                .ToList();
            var usedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 2 && itemPool.Count > 0; i++)
            {
                var pick = itemPool[startRng.Next(itemPool.Count)];
                if (!usedItemIds.Add(pick.Id) && itemPool.Count > 2)
                {
                    pick = itemPool.FirstOrDefault(x => usedItemIds.Add(x.Id)) ?? pick;
                }
                var coreTpl = _repo.GetCard(pick.Id);
                if (coreTpl == null) continue;
                var tier = SpawnQueryEngine.ClampToSupportedTier(coreTpl, CardTier.Gold);
                var ci = new CardInstance(coreTpl, tier, null, null);
                RegisterSpawnedCoreCard(ci, EInventorySection.Hand, preferredSlot: -1, emitSpawnEvent: true, events);
                grantedAny = true;
            }

            var skillPool = _pool.AllSkills
                .Where(c => _pool.IsPlayerSpawnable(c) &&
                            (rules.SpecialModeId == ESpecialModeId.CrossHeroMayhem
                                ? c.Heroes.Any(h => HeroNames.PlayableHeroes.Contains(h, StringComparer.OrdinalIgnoreCase))
                                : (c.Heroes.Length == 0 || c.Heroes.Contains(heroName, StringComparer.OrdinalIgnoreCase))))
                .ToList();
            if (skillPool.Count > 0)
            {
                var skPick = skillPool[startRng.Next(skillPool.Count)];
                var coreTpl = _repo.GetCard(skPick.Id);
                if (coreTpl != null)
                {
                    var tier = SpawnQueryEngine.ClampToSupportedTier(coreTpl, CardTier.Gold);
                    var ci = new CardInstance(coreTpl, tier, null, null);
                    RegisterSpawnedCoreCard(ci, null, preferredSlot: -1, emitSpawnEvent: true, events);
                    grantedAny = true;
                }
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
            // P1-26：终局态严格禁止原地 cheat restart（避免与 SQLite 已完结状态及客户端 EndRunAppState 脱节）
            return NotAllowed($"对局已结束（{_run.State}），无法执行指令 {command.GetType().Name}");
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
            _ => Err("暂不支持的指令类型：" + command.GetType().Name),
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

    /// <summary>P2-11：官方客户端 NetErrorHandler 静默忽略锦标赛等待轮询的精确英文白名单文案。</summary>
    public const string OfficialTournamentPvpWaitMessage = "Waiting for the remaining tournament players to finish PvP.";

    private static string ResolveErrorMessageId(string? explicitId, string prefix)
        => !string.IsNullOrWhiteSpace(explicitId)
            ? explicitId
            : $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private static Outcome Err(string msg, string? messageId = null) => new()
    {
        Error = new NetMessageError(ResolveErrorMessageId(messageId, "err"), EResponseErrorType.Error, msg),
    };

    private static Outcome NotFound(string what, string? messageId = null) => new()
    {
        Error = new NetMessageError(ResolveErrorMessageId(messageId, "not_found"), EResponseErrorType.NotFound, what),
    };

    private static Outcome NotAllowed(string what, string? messageId = null) => new()
    {
        Error = new NetMessageError(ResolveErrorMessageId(messageId, "not_allowed"), EResponseErrorType.NotAllowed, what),
    };

    // ── 选卡 / 买卡 / 二合一升阶 / EncounterStep 分流 ───────────────────────

    private Outcome OnSelectItem(SelectItemCommand cmd)
    {
        string id = cmd.InstanceId.Value;
        if (string.IsNullOrEmpty(id)) return NotFound("卡牌实例 ID 不能为空");
        if (!_run.Cards.TryGetValue(id, out var card)) return NotFound("未找到对应卡牌：" + id);

        if (_runEnded || _run.State is ERunState.Combat or ERunState.PVPCombat
            or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            return NotAllowed($"当前状态（{_run.State}）不允许操作卡牌");
        }

        // 已在玩家手上的卡 → 换位（并重算光环）
        if (card.Owner == ECombatantId.Player)
        {
            return MoveOwnedCard(card, cmd.TargetSockets, cmd.Section ?? card.Section ?? EInventorySection.Hand);
        }

        if (_run.State == ERunState.Pedestal)
            return NotAllowed($"祭坛状态下无法选择未持有的卡牌：{id}");
        if (!_run.SelectionSet.Contains(id))
            return NotAllowed($"卡牌 {id} 不在当前可选列表中");

        // 若命令行/脚本通过 SelectItemCommand 点选 EncounterStep 或遭遇卡，统一校验状态后交由对应处理器
        if (card.Type == ECardType.EncounterStep)
        {
            if (_run.State is not (ERunState.Encounter or ERunState.LevelUp or ERunState.Loot))
                return NotAllowed($"当前状态（{_run.State}）无法选择奇遇分支 {id}");
            if (_shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext))
                return NotAllowed("当前阶段已达到最大选择次数");
            return ExecuteEncounterStepSelection(card);
        }
        if (card.Type is ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter)
        {
            if (_run.State is not (ERunState.Choice or ERunState.LevelUp or ERunState.Encounter))
                return NotAllowed($"当前状态（{_run.State}）无法直接选择遭遇卡 {id}");
            return ExecuteEncounterCardSelection(card);
        }

        if (_run.State is not (ERunState.Encounter or ERunState.Loot or ERunState.LevelUp))
            return NotAllowed($"当前状态（{_run.State}）无法购买或选择卡牌 {id}");
        if (_shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext))
            return NotAllowed("当前阶段已达到最大选择次数");

        // 候选区技能卡
        if (card.Type == ECardType.Skill)
        {
            return EquipCandidateSkill(card, isStarterOrFree: !StartSelectionDone || _run.SelectionRules.SelectionIsFree);
        }

        var o = new Outcome();

        // P0-22：买价直接以权威卡面 BuyPrice 为唯一真相（商店折扣与词条调价均已写入 CoreCard 并同步至卡面），移除 _activeShelfDiscounts 旁路
        int buyPrice = _run.SelectionRules.SelectionIsFree
            ? 0
            : Math.Max(0, card.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0));
        int gold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        if (gold < buyPrice) return NotAllowed($"金币不足：需要 {buyPrice}，当前仅有 {gold}");

        // 检查是否可与玩家已有同名同阶物品二合一升阶
        var fuseTarget = FindFuseTarget(card);
        if (fuseTarget != null)
        {
            int goldBeforeFuse = gold;
            _run.PlayerAttributes[EPlayerAttributeType.Gold] = gold - buyPrice;
            _run.SelectionSet.Remove(id);
            _run.Cards.Remove(id); // 客户端 OnCardFusedHandler 自行清理选中源卡控制器
            if (_run.State == ERunState.Loot)
                _shop.ConsumeMonsterLootSelection(card);

            ETier nextTier = fuseTarget.Tier + 1;
            EEnchantmentType? prevEnchantment = fuseTarget.Enchantment;
            // P1-38：保留原卡既有附魔；仅当目标卡无附魔且源卡自带附魔时才继承源卡附魔，避免主力附魔被商店卡意外覆写
            EEnchantmentType? finalEnchantment = fuseTarget.Enchantment ?? card.Enchantment;
            bool enchantmentChanged = finalEnchantment.HasValue && finalEnchantment != prevEnchantment;
            if (enchantmentChanged)
            {
                fuseTarget.Enchantment = finalEnchantment;
                var fuseCore = EnsureCoreCard(fuseTarget);
                fuseCore.ApplyEnchantment(finalEnchantment!.Value.ToString(), preventOverride: false, isTemporary: false);
            }
            // P2-48：升阶前将商店待购卡可能携带的非价格类永久加成（PermanentAdd / PermanentMult）合入 fuseTarget.CoreCard
            MergeShelfPermanentModifiersIntoFuseTarget(card, fuseTarget);
            UpgradeCardEntryToTier(fuseTarget, nextTier, o.DirtyCardIds, o.Events);
            _run.SelectionsMadeInCurrentContext++;

            // P0-19 & P1-38：物品二合一融合只发 CardFused + CardDisposed，不向源卡发 CardPurchased，
            // 也不在同帧重发 CardUpgraded/CardEnchanted（规避 EnchantManager 抢锁与重复播放升级 VFX）
            o.Events.Add(new GameSimEventCardFused(id, fuseTarget.InstanceId, nextTier, finalEnchantment));
            o.Events.Add(new GameSimEventCardDisposed(id, ECardDisposalReason.Cleared));

            // 触发购买钩子 + 升阶钩子 + 附魔钩子 + 全局光环重算与全盘同步（P0-3：传透货架卡激活商店光环）
            if (!_run.SelectionRules.SelectionIsFree)
                _run.TrackItemPurchasedTags(card, card.CoreCard ?? fuseTarget.CoreCard);
            if (fuseTarget.CoreCard != null)
            {
                bool suppressUpgradedBecausePurchased = !_run.SelectionRules.SelectionIsFree;
                var selCards = CollectSelectionCoreCards();
                var p1Buy = BuildCorePlayerFromRun();
                var buyBatch = OutOfCombatController.OnItemPurchased(
                    p1Buy, fuseTarget.CoreCard, _repo, _rng, _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard,
                    isFree: _run.SelectionRules.SelectionIsFree,
                    isFusion: true,
                    selectionCards: selCards);
                ApplyOutOfCombatBatch(p1Buy, buyBatch, o.Events, syncBoardDiffs: false);

                var p1Upg = BuildCorePlayerFromRun();
                var upgBatch = OutOfCombatController.OnCardUpgraded(
                    p1Upg, fuseTarget.CoreCard, _repo, _rng, isFusion: suppressUpgradedBecausePurchased,
                    currentEncounterId: _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard,
                    selectionCards: selCards);
                ApplyOutOfCombatBatch(p1Upg, upgBatch, o.Events, syncBoardDiffs: false);

                if (enchantmentChanged)
                {
                    var p1Ench = BuildCorePlayerFromRun();
                    var enchBatch = OutOfCombatController.OnCardEnchanted(
                        p1Ench, fuseTarget.CoreCard, _repo, _rng,
                        currentEncounterId: _run.CurrentEncounterId,
                        activeEncounterCard: _run.CurrentEncounterCoreCard,
                        selectionCards: selCards);
                    ApplyOutOfCombatBatch(p1Ench, enchBatch, o.Events, syncBoardDiffs: false);
                }
            }

            if (buyPrice > 0)
                DispatchPlayerAttributeChanged("Gold", goldBeforeFuse, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0), o.Events);

            RecordChallengeForCard("TTriggerOnCardPurchased", fuseTarget, buyPriceOverride: buyPrice, isFusion: true);
            RecordChallengeForCard("TTriggerOnCardUpgraded", fuseTarget, isFusion: true);

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
        bool placed = TryPlaceIntoSection(card, coreCard, targetSection, desiredSlot, movedEntries, out bool usedDegradedFallback, cmd.TargetSockets);

        if (!placed)
        {
            string rejectReason = targetSection == EInventorySection.Stash
                ? "储藏箱空间不足，无法放入该物品"
                : "手牌区空间不足，无法放入该物品";
            LogBoardAudit("BuyCard", id, card.TemplateId.ToString(), "Shelf", $"{requestedSection}:{desiredSlot}", rejectReason);
            return NotAllowed(rejectReason);
        }

        if (usedDegradedFallback)
            o.AppendStateSyncAtTail = true;

        int goldBeforeBuy = gold;
        _run.PlayerAttributes[EPlayerAttributeType.Gold] = gold - buyPrice;
        card.Owner = ECombatantId.Player;
        card.IsFreeCandidate = false;
        card.Section = targetSection;
        card.State = ECardState.Alive;
        coreCard.StartSlot = card.Socket.HasValue ? (int)card.Socket.Value : coreCard.StartSlot;
        _run.SelectionSet.Remove(id);
        if (_run.State == ERunState.Loot)
            _shop.ConsumeMonsterLootSelection(card);
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

        // 购买完成后调用 OutOfCombatController.OnItemPurchased（含 activeEncounterCard、selectionCards、isFree、isFusion）+ 光环重算
        if (!_run.SelectionRules.SelectionIsFree)
            _run.TrackItemPurchasedTags(card, coreCard);
        var p1Core = BuildCorePlayerFromRun();
        var selectionCardsAfterBuy = CollectSelectionCoreCards();
        var purchaseBatch = OutOfCombatController.OnItemPurchased(
            p1Core, coreCard, _repo, _rng, _run.CurrentEncounterId,
            activeEncounterCard: _run.CurrentEncounterCoreCard,
            isFree: _run.SelectionRules.SelectionIsFree,
            isFusion: false,
            selectionCards: selectionCardsAfterBuy);
        ApplyOutOfCombatBatch(p1Core, purchaseBatch, o.Events, syncBoardDiffs: true);
        if (selectionCardsAfterBuy.Count > 0)
            SyncShelfEntriesDiff(o.Events);

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

        if (buyPrice > 0)
            DispatchPlayerAttributeChanged("Gold", goldBeforeBuy, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0), o.Events);

        RecordChallengeForCard("TTriggerOnCardPurchased", card, buyPriceOverride: buyPrice, isFusion: false);

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

        if (_run.State == ERunState.Loot && !reachedMax && _run.SelectionRules.CanSelectMultiple &&
            _run.SelectionSet.Count == 0 && _shop.HasRemainingMonsterLootCandidates())
        {
            if (_shop.TryDealCurrentMonsterLootPage(out var nextPageEvents))
            {
                o.Events.AddRange(nextPageEvents);
                TriggerOnEncounterCardsDealtOnly(_run.CurrentEncounterId, o.Events);
                return;
            }
        }

        if ((!_run.SelectionRules.CanSelectMultiple || reachedMax || (_run.State == ERunState.Loot && _run.SelectionSet.Count == 0 && !_shop.HasRemainingMonsterLootCandidates())) && _run.State is ERunState.LevelUp or ERunState.Loot)
        {
            bool leavingLoot = _run.State == ERunState.Loot;
            if (leavingLoot)
            {
                ApplyWillAutoSellOnExitIfNeeded(o.Events);
                ClearPendingExpeditionAfterLootIfNeeded(o.Events);
            }
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
            {
                ApplyWillAutoSellOnExitIfNeeded(o.Events);
                ClearPendingExpeditionAfterLootIfNeeded(o.Events);
            }
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            AdvanceAfterSubState(o);
        }
    }

    private void ClearPendingExpeditionAfterLootIfNeeded(List<IGameSimEvent>? events = null)
    {
        if (!_run.PendingClearExpeditionAfterLoot)
            return;
        _run.PendingClearExpeditionAfterLoot = false;
        _run.ClearActiveReplacementSet();
        if (events != null)
            ResetBoardOverrideIfNeeded(events);
    }

    private void ResetBoardOverrideIfNeeded(List<IGameSimEvent> events)
    {
        if (string.IsNullOrWhiteSpace(_run.CurrentBoardId))
            return;
        _run.CurrentBoardId = null;
        events.Add(new GameSimEventBoardOverridden(null, null));
    }

    private RunState.CardEntry? FindFuseTarget(RunState.CardEntry incoming)
    {
        if (incoming.Type is not (ECardType.Item or ECardType.Skill) || incoming.Tier >= ETier.Diamond)
            return null;
        // P1-38 & #71：与官方 Player/CardContainer/SocketedContainer 严格对齐：
        // 物品优先匹配 Hand（按 Socket 0..9 从左至右），其次匹配 Stash（按 Socket 0..9 从左至右）；
        // 同步按 RunState.ResolveCoreTemplateId 隔离自定义 Mod 卡与借皮原版载体，杜绝不同自定义卡误合。
        string incomingCoreId = RunState.ResolveCoreTemplateId(incoming);
        return _run.Cards.Values
            .Where(c =>
                c.Owner == ECombatantId.Player &&
                c.Type == incoming.Type &&
                string.Equals(RunState.ResolveCoreTemplateId(c), incomingCoreId, StringComparison.OrdinalIgnoreCase) &&
                c.Tier == incoming.Tier)
            .OrderBy(c => c.Section == EInventorySection.Hand ? 0 : (c.Section == EInventorySection.Stash ? 1 : 2))
            .ThenBy(c => c.Socket.HasValue ? (int)c.Socket.Value : int.MaxValue)
            .ThenBy(c => c.InstanceId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// P2-48：买卡融合（Buy-Fusion）升阶前，将商店待购卡 <paramref name="shelfCard"/> 上可能携带的非价格类永久加成
    /// （PermanentAdd / PermanentMult，排除 BuyPrice/SellPrice 以免商店折价污染自有物品）合并至 <paramref name="fuseTarget"/>.CoreCard。
    /// </summary>
    private void MergeShelfPermanentModifiersIntoFuseTarget(RunState.CardEntry shelfCard, RunState.CardEntry fuseTarget)
    {
        var shelfCore = shelfCard.CoreCard;
        if (shelfCore == null) return;
        if (shelfCore.PermanentAdd.Count == 0 && shelfCore.PermanentMult.Count == 0) return;

        var targetCore = EnsureCoreCard(fuseTarget);
        foreach (var (key, addVal) in shelfCore.PermanentAdd)
        {
            if (string.Equals(key, "BuyPrice", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "SellPrice", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Math.Abs(addVal) <= 1e-9) continue;
            targetCore.PermanentAdd[key] = targetCore.PermanentAdd.GetValueOrDefault(key, 0.0) + addVal;
        }
        foreach (var (key, multVal) in shelfCore.PermanentMult)
        {
            if (string.Equals(key, "BuyPrice", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "SellPrice", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!double.IsFinite(multVal) || Math.Abs(multVal - 1.0) <= 1e-9) continue;
            targetCore.PermanentMult[key] = targetCore.PermanentMult.GetValueOrDefault(key, 1.0) * multVal;
        }
    }

    private void UpgradeCardEntryToTier(
        RunState.CardEntry entry,
        ETier nextTier,
        ICollection<string>? dirtyCardIds = null,
        List<IGameSimEvent>? uiRefreshEvents = null)
    {
        entry.Tier = nextTier;

        // 保留既有 CoreCard 上的永久成长/任务/附魔/临时层，避免直接重建覆盖
        var core = EnsureCoreCard(entry);
        core.SetTier(ShopEngine.ToCoreTier(nextTier), isTemporary: false);

        if (entry.Type == ECardType.Item)
        {
            var baseSize = ShopEngine.ParseSize(core.Template.Size);
            var (buy, sell) = ShopEngine.PriceFor(baseSize, nextTier, _repo.GameMode.StandardPrices);
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
        _ = _run.SyncEntryFromCoreCardAndDiff(entry, _repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        // V14.2 问题 6 通用根治：
        // 客户端执行 GameSimEventCardUpgraded 时会按原版载体 TemplateId 的升阶表重算面板数值；
        // 必须把服务端升阶后的全量有效属性写入 PendingForceSyncAttributes 并标脏下发 SimUpdateCard，
        // 彻底覆盖载体模板的旧阶/恒定数值，确保任意自定义卡的基础伤害、冷却与 Custom_* 随品阶同步生效。
        foreach (var (attrType, attrVal) in entry.Attributes)
            entry.PendingForceSyncAttributes[attrType] = attrVal;
        dirtyCardIds?.Add(entry.InstanceId);

        if (uiRefreshEvents != null && entry.Type == ECardType.Item)
        {
            uiRefreshEvents.Add(new GameSimEventEffectAuraExecuted
            {
                ExecutionContextId = "upg_sync_" + entry.InstanceId + "_" + nextTier,
                EffectId = "CardUpgradeAttrSync",
                AppliedTo = new HashSet<IEffectTarget>
                {
                    new EffectTargetCard { Target = new BazaarGameShared.Domain.Core.InstanceId(entry.InstanceId) },
                },
            });
        }
    }

    private bool TryPlaceIntoSection(
        RunState.CardEntry entry,
        CardInstance coreCard,
        EInventorySection section,
        int desiredSlot,
        List<(RunState.CardEntry Entry, EContainerSocketId NewSocket, EInventorySection Section)> moved,
        out bool usedDegradedFallback,
        List<EContainerSocketId>? targetSockets = null)
    {
        usedDegradedFallback = false;
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

        List<int>? officialSockets = null;
        if (targetSockets is { Count: > 0 })
        {
            officialSockets = targetSockets.Select(s => (int)s).ToList();
        }
        else if (desiredSlot >= 0)
        {
            officialSockets = new List<int>(coreCard.SlotWidth);
            for (int i = 0; i < coreCard.SlotWidth; i++)
                officialSockets.Add(desiredSlot + i);
        }

        if (sourceGrid != null && !sameGrid)
        {
            sourceGrid.Remove(coreCard);
        }

        bool ok = false;
        if (sourceGrid == null)
        {
            // 买卡/衍生发卡：只要目标区已解锁总空位 >= 物品宽度，优先走 1:1 客户端 SocketOperations.AddItemToContainer 插入算法
            if (targetGrid.CountFreeUnlockedSlots() >= coreCard.SlotWidth)
            {
                ok = targetGrid.TryInsertIntoGridSocketOps(coreCard, officialSockets);
                if (!ok && desiredSlot >= 0)
                {
                    ok = targetGrid.TryPlaceWithDisplaceOrSwap(coreCard, desiredSlot);
                    if (ok) usedDegradedFallback = true;
                }
                if (!ok)
                {
                    ok = targetGrid.TryAddAuto(coreCard);
                    if (ok) usedDegradedFallback = true;
                }
            }
        }
        else if (sameGrid)
        {
            // 同盘移牌：优先走 1:1 客户端官方 SocketOperations.MoveCardToSocketOnSameCarpet 链表保序算法，
            // 确保服务端与客户端在满盘换位/滑动挤位后每个槽位完全一致（彻底杜绝无关卡瞬移至最左导致卖战利品打偏）。
            if (officialSockets != null)
                ok = targetGrid.TryMoveSameGridSocketOps(coreCard, officialSockets);
            if (!ok && desiredSlot >= 0)
            {
                ok = targetGrid.TryPlaceWithDisplaceOrSwap(coreCard, desiredSlot);
                if (ok) usedDegradedFallback = true;
            }
            if (!ok && desiredSlot < 0)
            {
                ok = targetGrid.TryAddAuto(coreCard);
                if (ok) usedDegradedFallback = true;
            }
        }
        else
        {
            // 跨区移牌：优先走 1:1 官方 SocketOperations.MoveCardToSocketOnDifferentCarpet；
            // 若目标区空位不足且指定了槽位，执行原子跨区换位（对齐客户端 SocketOperations.SwapItemsOnToBoard）
            if (targetGrid.CountFreeUnlockedSlots() >= coreCard.SlotWidth)
            {
                ok = targetGrid.TryInsertIntoGridSocketOps(coreCard, officialSockets);
                if (!ok && desiredSlot >= 0)
                {
                    ok = targetGrid.TryPlaceWithDisplaceOrSwap(coreCard, desiredSlot);
                    if (ok) usedDegradedFallback = true;
                }
                if (!ok)
                {
                    ok = targetGrid.TryAddAuto(coreCard);
                    if (ok) usedDegradedFallback = true;
                }
            }
            else if (desiredSlot >= 0 && sourceGrid != null)
            {
                ok = TryCrossSectionSwap(coreCard, sourceGrid, targetGrid, desiredSlot, oldSlot, officialSockets);
            }
        }

        if (!ok)
        {
            RestoreGridFromSnapshot(targetGrid, targetSnapshot);
            if (sourceGrid != null && sourceSnapshot != null)
                RestoreGridFromSnapshot(sourceGrid, sourceSnapshot);
            coreCard.StartSlot = oldSlot;
            usedDegradedFallback = false;
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

    /// <summary>
    /// P0-18：1:1 对齐官方 <c>SocketOperations.SwapItemsOnToBoard</c>：
    /// 1) 首行强验 <c>sockets.Count == incoming.SlotWidth</c>（经 ModifyOfficialSockets 后）；
    /// 2) 仅收集目标槽位上被压住的 blocker；
    /// 3) 回填 blocker 到源棋盘仅查找最左现成连续空槽（<c>TryPlaceAtFirstContiguousEmptySlot</c>），绝不挤位重排源棋盘。
    /// </summary>
    private bool TryCrossSectionSwap(
        CardInstance incoming,
        BoardGrid sourceGrid,
        BoardGrid targetGrid,
        int desiredSlot,
        int oldSlot,
        List<int>? officialSockets = null)
    {
        var modifiedSockets = targetGrid.ModifyOfficialSockets(officialSockets, incoming.SlotWidth);
        if (modifiedSockets == null || modifiedSockets.Count != incoming.SlotWidth)
            return false;

        var blockers = new List<CardInstance>();
        var blockerSet = new HashSet<CardInstance>();
        foreach (int s in modifiedSockets)
        {
            var occ = targetGrid.GetCardAtSlot(s);
            if (occ != null && blockerSet.Add(occ))
                blockers.Add(occ);
        }
        if (blockers.Count == 0) return false;

        int blockersWidth = blockers.Sum(b => b.SlotWidth);
        if (sourceGrid.CountFreeUnlockedSlots() < blockersWidth) return false;

        foreach (var b in blockers)
            targetGrid.Remove(b);

        // 对齐官方：先按顺序把 blocker 放入源棋盘最左现成连续空槽（不挤位），再把 incoming 放入目标棋盘
        foreach (var b in blockers)
        {
            if (!sourceGrid.TryPlaceAtFirstContiguousEmptySlot(b))
                return false;
        }

        if (!targetGrid.TryInsertIntoGridSocketOps(incoming, modifiedSockets))
            return false;

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

        string coreTplId = RunState.ResolveCoreTemplateId(entry);
        var coreTpl = _repo.GetCard(coreTplId);
        if (coreTpl == null)
        {
            coreTpl = new BazaarCore.Models.CardTemplate
            {
                Id = coreTplId,
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
                int normalizedSell = (_run.CustomRules.HasMutator(MutatorCatalog.DoubleEconomy) &&
                                      entry.Type is ECardType.Item or ECardType.Skill &&
                                      av > 0)
                    ? av / 2
                    : av;
                int defaultSell = entry.Type == ECardType.Item
                    ? ShopEngine.PriceFor(entry.Size, entry.Tier, _repo.GameMode.StandardPrices).Sell
                    : 0;
                if (normalizedSell == defaultSell) continue;
                ci.PermanentAdd[key] = normalizedSell - defaultSell;
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
            return NotAllowed($"当前状态（{_run.State}）不允许移动物品");
        }
        string id = cmd.InstanceId.Value;
        if (string.IsNullOrEmpty(id)) return NotFound("卡牌实例 ID 不能为空");
        if (!_run.Cards.TryGetValue(id, out var card)) return NotFound("未找到要移动的卡牌：" + id);
        if (card.Owner != ECombatantId.Player) return NotAllowed("无法移动非玩家拥有的卡牌：" + id);
        if (card.Type != ECardType.Item) return NotAllowed($"只能移动物品类型卡牌，当前类型为 {card.Type}");
        return MoveOwnedCard(card, cmd.TargetSockets, cmd.Section);
    }

    private Outcome MoveOwnedCard(RunState.CardEntry card, List<EContainerSocketId>? targetSockets, EInventorySection targetSection)
    {
        bool coreRecreated = card.CoreCard == null;
        var coreCard = EnsureCoreCard(card);
        EInventorySection? fromSection = card.Section;
        string fromLoc = $"{fromSection}:{card.Socket}";
        int desiredSlot = targetSockets is { Count: > 0 } ? targetSockets.Min(s => (int)s) : -1;
        var preMoveSnapshot = SnapshotPlayerItemPlacements();
        var moved = new List<(RunState.CardEntry Entry, EContainerSocketId NewSocket, EInventorySection Section)>();

        if (!TryPlaceIntoSection(card, coreCard, targetSection, desiredSlot, moved, out bool usedDegradedFallback, targetSockets))
        {
            LogBoardAudit("MoveCard", card.InstanceId, card.TemplateId.ToString(), fromLoc, $"{targetSection}:{desiredSlot}", "目标槽位空间不足，无法移动卡牌");
            return NotAllowed("目标槽位空间不足，无法移动该卡牌");
        }

        var o = new Outcome { AppendStateSyncAtTail = usedDegradedFallback };
        o.DirtyCardIds.Add(card.InstanceId);
        EmitMoveEventIfNotPresent(o.Events, card.InstanceId, card.Section ?? targetSection, card.Socket ?? EContainerSocketId.Socket_3);
        foreach (var mv in moved)
        {
            o.DirtyCardIds.Add(mv.Entry.InstanceId);
            EmitMoveEventIfNotPresent(o.Events, mv.Entry.InstanceId, mv.Section, mv.NewSocket);
        }

        // V13.1 拖拽加速（Top 9）+ P1-39：纯储藏箱内部换位若涉及任意 Stash 卡（含原地未动旁观光环卡）的 Stash/HandAndStash 光环，
        // 仍走完整光环刷新确保价格/属性 100% 精准。
        bool isPureStashReorder = fromSection == EInventorySection.Stash && targetSection == EInventorySection.Stash;
        if (isPureStashReorder && !HasStashRelevantAura(card, moved))
        {
            if (card.Socket.HasValue) coreCard.StartSlot = (int)card.Socket.Value;
            foreach (var mv in moved)
            {
                if (mv.Entry.CoreCard != null)
                    mv.Entry.CoreCard.StartSlot = (int)mv.NewSocket;
            }
            LogBoardAudit("MoveCard", card.InstanceId, card.TemplateId.ToString(), fromLoc, $"{card.Section}:{card.Socket}");
            return o;
        }

        // V13.1 拖拽加速（Top 9）：若场上、技能、货架与当前遭遇均不存在任何活跃光环源或已挂载光环修饰，
        // 直接跳过全量 CorePlayer 重建与 AuraEngine 双趟重算，仅确保网格不变量并同步移位差分。
        bool needsAuraRecalc = HasAnyRelevantAuraForMove();
        if (needsAuraRecalc)
        {
            int prevEventCount = o.Events.Count;
            var p1Core = BuildCorePlayerFromRun();
            var selectionCards = CollectSelectionCoreCards();
            var moveBatch = OutOfCombatController.OnCardMoved(
                p1Core, _repo, activeEncounterCard: _run.CurrentEncounterCoreCard,
                selectionCards: selectionCards);
            ApplyOutOfCombatBatchWithDirty(p1Core, moveBatch, o.Events, syncBoardDiffs: true, dirtyCardIds: o.DirtyCardIds);
            if (selectionCards.Count > 0)
                SyncShelfEntriesDiff(o.Events, o.DirtyCardIds);

            // P1-09 & P1-39：将本轮光环（AppliedTo / RemovedFrom / Source）、品质、附魔与属性发生变化的全部卡牌精准登记进 DirtyCardIds
            for (int i = prevEventCount; i < o.Events.Count; i++)
            {
                switch (o.Events[i])
                {
                    case GameSimEventEffectAuraExecuted auraEv:
                        if (auraEv.Source is { } srcInst && !string.IsNullOrEmpty(srcInst.Value))
                            o.DirtyCardIds.Add(srcInst.Value);
                        if (auraEv.AppliedTo != null)
                        {
                            foreach (var target in auraEv.AppliedTo)
                            {
                                if (target is EffectTargetCard cardTarget && !string.IsNullOrEmpty(cardTarget.Target.Value))
                                    o.DirtyCardIds.Add(cardTarget.Target.Value);
                            }
                        }
                        if (auraEv.RemovedFrom != null)
                        {
                            foreach (var target in auraEv.RemovedFrom)
                            {
                                if (target is EffectTargetCard cardTarget && !string.IsNullOrEmpty(cardTarget.Target.Value))
                                    o.DirtyCardIds.Add(cardTarget.Target.Value);
                            }
                        }
                        break;
                    case GameSimEventCardUpgraded upgEv when !string.IsNullOrEmpty(upgEv.InstanceId):
                        o.DirtyCardIds.Add(upgEv.InstanceId);
                        break;
                    case GameSimEventCardEnchanted encEv when !string.IsNullOrEmpty(encEv.InstanceId):
                        o.DirtyCardIds.Add(encEv.InstanceId);
                        break;
                }
            }

            ReconcileAndVerifyBoardInvariants("MoveCard", o.DirtyCardIds, o.Events, preMoveSnapshot);
            SyncDisplacedCorePlacementsAndEmitDiffs(p1Core, preMoveSnapshot, o.DirtyCardIds, o.Events);
        }
        else
        {
            if (card.Socket.HasValue) coreCard.StartSlot = (int)card.Socket.Value;
            foreach (var mv in moved)
            {
                if (mv.Entry.CoreCard != null)
                    mv.Entry.CoreCard.StartSlot = (int)mv.NewSocket;
            }
            if (coreRecreated)
            {
                o.Events.AddRange(_run.SyncEntryFromCoreCardAndDiff(card, _repo.GameMode.StandardPrices));
            }
            ReconcileAndVerifyBoardInvariants("MoveCard", o.DirtyCardIds, o.Events, preMoveSnapshot);
        }

        LogBoardAudit("MoveCard", card.InstanceId, card.TemplateId.ToString(), fromLoc, $"{card.Section}:{card.Socket}");
        return o;
    }

    private bool HasStashRelevantAura(
        RunState.CardEntry primary,
        List<(RunState.CardEntry Entry, EContainerSocketId NewSocket, EInventorySection Section)> moved)
    {
        bool EntryNeedsStashAura(RunState.CardEntry entry)
        {
            if (entry.Enchantment.HasValue)
                return true;
            var ci = entry.CoreCard;
            if (ci != null)
            {
                if (ci.AuraAdd.Count > 0 || ci.AuraMult.Count > 0 || ci.AuraTags.Count > 0)
                    return true;
                foreach (var aura in ci.ActiveAuras)
                {
                    if (IsStashActiveIn(aura.ActiveIn))
                        return true;
                }
                return false;
            }
            var tpl = _repo.GetCard(RunState.ResolveCoreTemplateId(entry));
            if (tpl?.Auras != null)
            {
                foreach (var aura in tpl.Auras.Values)
                {
                    if (IsStashActiveIn(aura.ActiveIn))
                        return true;
                }
            }
            return false;
        }

        static bool IsStashActiveIn(string? activeIn)
            => !string.IsNullOrEmpty(activeIn) && (
                string.Equals(activeIn, "HandAndStash", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(activeIn, "Stash", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(activeIn, "StashOnly", StringComparison.OrdinalIgnoreCase) ||
                activeIn.Contains("Stash", StringComparison.OrdinalIgnoreCase));

        if (EntryNeedsStashAura(primary)) return true;
        foreach (var mv in moved)
        {
            if (EntryNeedsStashAura(mv.Entry)) return true;
        }

        // P1-39：扫描整个 Stash（最多 10 张）以识别原地未动的旁观光环卡（如 Stash 相邻增伤/减价/加值光环）
        foreach (var entry in _run.Cards.Values)
        {
            if (entry.Owner == ECombatantId.Player &&
                entry.Section == EInventorySection.Stash &&
                EntryNeedsStashAura(entry))
            {
                return true;
            }
        }
        return false;
    }

    private bool HasAnyRelevantAuraForMove()
    {
        if (_run.PlayerAuraAdd.Count > 0 || _run.PlayerAuraMult.Count > 0)
            return true;
        if (_run.CurrentEncounterCoreCard?.ActiveAuras.Count > 0)
            return true;

        foreach (var entry in _run.Cards.Values)
        {
            if (entry.Owner != ECombatantId.Player && !_run.SelectionSet.Contains(entry.InstanceId))
                continue;
            var ci = entry.CoreCard;
            if (ci != null)
            {
                if (ci.ActiveAuras.Count > 0 || ci.AuraAdd.Count > 0 || ci.AuraMult.Count > 0 || ci.AuraTags.Count > 0)
                    return true;
            }
            else if (entry.Type is ECardType.Item or ECardType.Skill or ECardType.SocketEffect)
            {
                var tpl = _repo.GetCard(RunState.ResolveCoreTemplateId(entry));
                if (tpl?.Auras is { Count: > 0 })
                    return true;
            }
        }
        return false;
    }

    private Outcome OnSelectSkill(SelectSkillCommand cmd)
    {
        if (_runEnded || _run.State is not (ERunState.Encounter or ERunState.Loot or ERunState.LevelUp))
            return NotAllowed($"当前状态（{_run.State}）不允许选择技能");

        string id = cmd.InstanceId.Value;
        if (string.IsNullOrEmpty(id)) return NotFound("技能实例 ID 不能为空");
        if (!_run.Cards.TryGetValue(id, out var card))
            return NotFound("未找到对应技能：" + id);
        if (card.Owner != null)
            return NotAllowed("该技能已被持有：" + id);
        if (!_run.SelectionSet.Contains(id))
            return NotAllowed("该技能不在当前可选列表中：" + id);

        if (card.Type == ECardType.EncounterStep)
        {
            if (_shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext))
                return NotAllowed("当前阶段已达到最大选择次数");
            return ExecuteEncounterStepSelection(card);
        }
        if (card.Type != ECardType.Skill)
            return NotAllowed($"卡牌 {id} 不是技能类型（当前为 {card.Type}）");
        if (_shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext))
            return NotAllowed("当前阶段已达到最大选择次数");

        return EquipCandidateSkill(card, isStarterOrFree: !StartSelectionDone || _run.SelectionRules.SelectionIsFree);
    }

    private Outcome EquipCandidateSkill(RunState.CardEntry card, bool isStarterOrFree)
    {
        var o = new Outcome();
        // P1-41：与官方客户端 CardOperationUtility 严格对齐：缺 BuyPrice 键时按 0 金免费放行（ShopEngine/EncounterStepEngine 对收费技能均显式写入 BuyPrice）
        int buyPrice = isStarterOrFree
            ? 0
            : (card.Attributes.TryGetValue(ECardAttributeType.BuyPrice, out int explicitPrice)
                ? Math.Max(0, explicitPrice)
                : 0);
        int gold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        if (gold < buyPrice) return NotAllowed($"金币不足：需要 {buyPrice}，当前仅有 {gold}");

        // Q38/Q39：技能严格去重与升阶规则：
        //   · 同阶且 < Diamond -> 二合一升至下一阶；
        //   · 已持有更低阶同名技能（如野怪掉落更高阶）-> 原地升阶至新卡阶级（不创建第二个同名技能）；
        //   · 已持有同阶 Diamond+ 或更高阶同名技能 -> 拒绝购买/拾取（且不扣费）。
        var fuseTarget = StartSelectionDone ? FindFuseTarget(card) : null;
        bool isSameTierFuse = fuseTarget != null;
        if (fuseTarget == null)
        {
            string cardCoreId = RunState.ResolveCoreTemplateId(card);
            var existingOwnedSkill = _run.Cards.Values.FirstOrDefault(c =>
                c.Owner == ECombatantId.Player &&
                c.Type == ECardType.Skill &&
                string.Equals(RunState.ResolveCoreTemplateId(c), cardCoreId, StringComparison.OrdinalIgnoreCase));
            if (existingOwnedSkill != null)
            {
                if (existingOwnedSkill.Tier >= card.Tier)
                    return NotAllowed($"已持有更高或同品阶的同名技能（当前品阶 {existingOwnedSkill.Tier}）");
                fuseTarget = existingOwnedSkill;
            }
        }

        int goldBeforeSkill = gold;
        if (buyPrice > 0) _run.PlayerAttributes[EPlayerAttributeType.Gold] = gold - buyPrice;

        if (fuseTarget != null)
        {
            string selectedId = card.InstanceId;
            _run.Cards.Remove(selectedId);
            _run.SelectionSet.Remove(selectedId);
            if (_run.State == ERunState.Loot)
                _shop.ConsumeMonsterLootSelection(card);

            // P0-19：技能二合一融合在同包 SimUpdateCard 中临时保留 selectedId 的 Placement(Owner = Player)，
            // 使客户端 BoardManager.OnCardFusedHandler (L3676) 命中原生飞入动画与技能栏刷新分支
            card.Owner = ECombatantId.Player;
            card.IsFreeCandidate = false;
            card.Section = null;
            card.Socket = null;
            o.ExtraSimCards.Add(card);
            _run.PendingTransientSimCards.Add(card);

            // P1-38：保留目标原卡既有附魔；仅当目标无附魔且新卡自带附魔时才继承
            EEnchantmentType? fusedEnchantment = fuseTarget.Enchantment ?? card.Enchantment;
            bool enchantmentChanged = fusedEnchantment != fuseTarget.Enchantment;
            if (fusedEnchantment.HasValue && enchantmentChanged)
            {
                fuseTarget.Enchantment = fusedEnchantment;
                EnsureCoreCard(fuseTarget).ApplyEnchantment(fusedEnchantment.Value.ToString(), preventOverride: false, isTemporary: false);
            }

            ETier nextTier = isSameTierFuse ? fuseTarget.Tier + 1 : card.Tier;
            MergeShelfPermanentModifiersIntoFuseTarget(card, fuseTarget);
            UpgradeCardEntryToTier(fuseTarget, nextTier, o.DirtyCardIds, o.Events);

            // P1-38 & P2-12：技能融合不重复发 CardUpgraded/CardEnchanted，且规范技能无网格槽位（LeftSocketId=null, Section=null）
            o.Events.Add(new GameSimEventCardPurchased(
                selectedId,
                (uint)buyPrice,
                null,
                null,
                ECombatantId.Player));
            o.Events.Add(new GameSimEventCardFused(selectedId, fuseTarget.InstanceId, nextTier, fusedEnchantment));
            o.Events.Add(new GameSimEventCardDisposed(selectedId, ECardDisposalReason.Cleared));

            if (fuseTarget.CoreCard != null)
            {
                bool suppressUpgradedBecausePurchased = !isStarterOrFree;
                var selCards = CollectSelectionCoreCards();
                var p1Buy = BuildCorePlayerFromRun();
                var buyBatch = OutOfCombatController.OnItemPurchased(
                    p1Buy, fuseTarget.CoreCard, _repo, _rng, _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard,
                    isFree: isStarterOrFree,
                    isFusion: true,
                    selectionCards: selCards);
                ApplyOutOfCombatBatch(p1Buy, buyBatch, o.Events, syncBoardDiffs: false);

                var p1Upg = BuildCorePlayerFromRun();
                var upgBatch = OutOfCombatController.OnCardUpgraded(
                    p1Upg, fuseTarget.CoreCard, _repo, _rng, isFusion: suppressUpgradedBecausePurchased,
                    currentEncounterId: _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard,
                    selectionCards: selCards);
                ApplyOutOfCombatBatch(p1Upg, upgBatch, o.Events, syncBoardDiffs: false);

                if (enchantmentChanged && fusedEnchantment.HasValue)
                {
                    var p1Ench = BuildCorePlayerFromRun();
                    var enchBatch = OutOfCombatController.OnCardEnchanted(
                        p1Ench, fuseTarget.CoreCard, _repo, _rng,
                        currentEncounterId: _run.CurrentEncounterId,
                        activeEncounterCard: _run.CurrentEncounterCoreCard,
                        selectionCards: selCards);
                    ApplyOutOfCombatBatch(p1Ench, enchBatch, o.Events, syncBoardDiffs: false);
                }

                // P0-14：技能融合分支补齐 TTriggerOnCardSelected 派发并同步 ExclusionSet（解锁 Partner Portal - Forja 并排除 Partner Portal）
                if (fuseTarget.CoreCard.Template.Abilities.Values.Any(a => TriggerContainsType(a.Trigger, "TTriggerOnCardSelected")))
                {
                    var p1Sel = BuildCorePlayerFromRun();
                    var selBatch = OutOfCombatController.OnCardSelected(
                        p1Sel, fuseTarget.CoreCard, _repo, _rng,
                        currentEncounterId: _run.CurrentEncounterId,
                        activeEncounterCard: _run.CurrentEncounterCoreCard,
                        selectionCards: selCards);
                    ApplyOutOfCombatBatch(p1Sel, selBatch, o.Events, syncBoardDiffs: true);
                }
            }

            if (buyPrice > 0)
                DispatchPlayerAttributeChanged("Gold", goldBeforeSkill, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0), o.Events);

            RecordChallengeForCard("TTriggerOnCardPurchased", fuseTarget, buyPriceOverride: buyPrice, isFusion: true);
            RecordChallengeForCard("TTriggerOnCardUpgraded", fuseTarget, isFusion: true);

            RecalculateAurasAndSyncBoard(o.Events);
            Console.WriteLine($"[RunEngine] SKILL FUSE {card.TemplateId} -> {fuseTarget.InstanceId} tier={nextTier} ench={fusedEnchantment}");
        }
        else
        {
            // P2-12：保留原候选技能卡 InstanceId 并变更 Owner=Player（绝不先销毁），
            // 规范技能无网格槽位（CardPurchased LeftSocketId=null / Section=null，移除冗余 CardMoved(Hand, Socket_0)），
            // 由同包 SimUpdateCard.Placement(Owner=Player)（在 GameSimHandler.ProcessEvents 前生效）+ CardPurchased + PlayerSkillEquipped 驱动客户端原生装备与动画！
            _run.SelectionSet.Remove(card.InstanceId);
            card.Owner = ECombatantId.Player;
            card.IsFreeCandidate = false;
            card.Socket = null;
            card.Section = null;
            card.Attributes[ECardAttributeType.BuyPrice] = 0;
            if (_run.State == ERunState.Loot)
                _shop.ConsumeMonsterLootSelection(card);

            var skCore = EnsureCoreCard(card);
            skCore.BaseAttributes["BuyPrice"] = 0;
            o.DirtyCardIds.Add(card.InstanceId);

            o.Events.Add(new GameSimEventCardPurchased(
                card.InstanceId,
                (uint)buyPrice,
                null,
                null,
                ECombatantId.Player));
            o.Events.Add(new GameSimEventPlayerSkillEquipped(card.InstanceId, ECombatantId.Player));

            // 装备技能后触发购买钩子（开局后，含 activeEncounterCard、selectionCards、isFree、isFusion）并重算局外光环
            var p1Core = BuildCorePlayerFromRun();
            var selCards = CollectSelectionCoreCards();
            var batch = StartSelectionDone
                ? OutOfCombatController.OnItemPurchased(
                    p1Core, skCore, _repo, _rng, _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard,
                    isFree: isStarterOrFree,
                    isFusion: false,
                    selectionCards: selCards)
                : OutOfCombatController.RecalculateOutOfCombatAuras(
                    p1Core, _repo, activeEncounterCard: _run.CurrentEncounterCoreCard,
                    selectionCards: selCards);
            ApplyOutOfCombatBatch(p1Core, batch, o.Events, syncBoardDiffs: true);

            // P0-14：新装备技能分支补齐 TTriggerOnCardSelected 派发（Partner Portal 8 个免费专属技能在 OnItemPurchased 中因 isFree 早退，
            // 必须显式派发 OnCardSelected 并经 ApplyOutOfCombatBatch 同步 ps.ExclusionSet 回 _run.ExclusionSet，彻底解锁 8 座 Forja 祭坛）
            if (skCore.Template.Abilities.Values.Any(a => TriggerContainsType(a.Trigger, "TTriggerOnCardSelected")))
            {
                var p1Sel = BuildCorePlayerFromRun();
                var selBatch = OutOfCombatController.OnCardSelected(
                    p1Sel, skCore, _repo, _rng,
                    currentEncounterId: _run.CurrentEncounterId,
                    activeEncounterCard: _run.CurrentEncounterCoreCard,
                    selectionCards: selCards);
                ApplyOutOfCombatBatch(p1Sel, selBatch, o.Events, syncBoardDiffs: true);
            }

            if (buyPrice > 0)
                DispatchPlayerAttributeChanged("Gold", goldBeforeSkill, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0), o.Events);

            RecordChallengeForCard("TTriggerOnCardPurchased", card, buyPriceOverride: buyPrice, isFusion: false);

            if (selCards.Count > 0)
                SyncShelfEntriesDiff(o.Events);
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

        if (_run.State == ERunState.Loot && !reachedMax && _run.SelectionRules.CanSelectMultiple &&
            _run.SelectionSet.Count == 0 && _shop.HasRemainingMonsterLootCandidates())
        {
            if (_shop.TryDealCurrentMonsterLootPage(out var nextPageEvents))
            {
                o.Events.AddRange(nextPageEvents);
                TriggerOnEncounterCardsDealtOnly(_run.CurrentEncounterId, o.Events);
                return o;
            }
        }

        if (_run.State == ERunState.Loot && (!_run.SelectionRules.CanSelectMultiple || reachedMax || (_run.SelectionSet.Count == 0 && !_shop.HasRemainingMonsterLootCandidates())))
        {
            ApplyWillAutoSellOnExitIfNeeded(o.Events);
            ClearPendingExpeditionAfterLootIfNeeded(o.Events);
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
            return NotAllowed($"当前状态（{_run.State}）不允许选择遭遇");
        }

        string id = cmd.InstanceId.Value;
        if (string.IsNullOrEmpty(id)) return NotFound("遭遇实例 ID 不能为空");
        if (!_run.Cards.TryGetValue(id, out var card)) return NotFound("未找到对应遭遇：" + id);
        if (!_run.SelectionSet.Contains(id))
            return NotAllowed($"遭遇 {id} 不在当前可选列表中");

        if (card.Type == ECardType.EncounterStep)
        {
            if (_run.State is not (ERunState.Encounter or ERunState.LevelUp or ERunState.Loot))
                return NotAllowed($"当前状态（{_run.State}）无法选择奇遇分支 {id}");
            return ExecuteEncounterStepSelection(card);
        }

        if (card.Type is not (ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter))
            return NotAllowed($"卡牌 {id} 不是有效的遭遇卡（当前为 {card.Type}）");

        if (_run.State is not (ERunState.Choice or ERunState.LevelUp or ERunState.Encounter))
            return NotAllowed($"当前状态（{_run.State}）无法选择遭遇 {id}");

        return ExecuteEncounterCardSelection(card);
    }

    private Outcome ExecuteEncounterCardSelection(RunState.CardEntry card)
    {
        var o = new Outcome();
        string encId = RunState.ResolveCoreTemplateId(card);

        // 若选中的是 PvP 战斗卡且处于多真人锦标赛同步屏障期，优先校验拦截，绝不破坏当前 Choice 与棋盘
        if (card.Type == ECardType.PvpEncounter)
        {
            var barrier = CheckTournamentPvpBarrier();
            if (barrier is { CanStartPvp: false })
            {
                // P2-11：对齐客户端 NetErrorHandler 精确英文静默白名单，避免等待轮询刷屏 LogError
                return NotAllowed(OfficialTournamentPvpWaitMessage, messageId: "TournamentPvpWait");
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
                return NotAllowed(expRes.ErrorMessage ?? $"进入探险房间 {encId} 失败");

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

        if (ShopEngine.IsEffectiveSingleSpawnEncounter(tpl, coreTpl))
        {
            _run.VisitedSingleSpawnEncounterIds.Add(encId);
        }
        if (!string.IsNullOrEmpty(encId))
        {
            _run.VisitedDayEncounterIds.Add(encId);
            if (!string.IsNullOrEmpty(_run.ActiveExpeditionId))
                _run.ExpeditionVisitedRoomIds.Add(encId);
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
                    // P2-11：非空 MessageId + 对齐客户端 NetErrorHandler 精确英文静默白名单
                    o.Error = new NetMessageError(
                        "TournamentPvpWait",
                        EResponseErrorType.NotAllowed,
                        OfficialTournamentPvpWaitMessage);
                    return;
                }

                // 官方协议：先发 CardPurchased(Socket=null) 使客户端 OnCardPurchased 把 PvP 对手卡移入 21 号头像槽、
                // 触发 BeginPVPTransition 并绑定 Data.CurrentEncounterController，再移除后端 _run.Cards 并发 CardDisposed；
                // GameSimHandler 固定按 StateTransitioned(PVPCombat) → CardPurchased → CardDisposed 顺序执行，
                // BoardManager.IsActivePvpEncounterPortrait 会精确放行保护顶栏对手立绘，同时保证地毯零残留！
                if (selectedCard != null)
                {
                    _run.SelectionSet.Remove(selectedCard.InstanceId);
                    o.Events.Add(new GameSimEventCardPurchased(
                        selectedCard.InstanceId,
                        0u,
                        null,
                        null,
                        null));
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
                RerollRules = null,
            };
            // P1-34：进入 Pedestal 时显式清空刷新状态，防止继承上一家商店的 RerollCost / RerollsRemaining
            _run.BaseRerollCost = null;
            _run.RerollCost = 0;
            _run.RerollsRemaining = 0;
            RecalculateAurasAndSyncBoard(o.Events);
            ReconcileAndVerifyBoardInvariants("EnterPedestal", o.DirtyCardIds, o.Events);
            o.Events.Add(new GameSimEventStateTransitioned(ERunState.Pedestal, _run.CurrentEncounterId));
            TriggerOnEncounterEnteredAndDealt(encId, o.Events);
            return;
        }

        // 3. P0-16 直效/纯 XP 事件遭遇（无 SpawnContext 与 NextEncounterOnSelection，含 TTriggerOnCardSelected 或 ExperienceAwardUponSelection > 0）：
        //    如 Borrow (Day 1-2) / Invest in Yourself×3 / Trophy Hunt×3 / Snack Time / Regenerative Tincture / Study (Default Option)
        //    直效动作已在 ExecuteEncounterCardSelection 中执行完毕，此处即时结算经验、清架并推进小时（或弹 LevelUp），避免卡在空 Encounter 界面！
        if (cardType == ECardType.EventEncounter)
        {
            bool hasSpawnCtx = (coreTpl?.SelectionContext?.SpawnContext ?? tpl?.SelectionContext?.SpawnContext) != null;
            bool hasNextEnc = (coreTpl?.NextEncounterOnSelection is { Count: > 0 })
                           || (tpl?.NextEncounterOnSelection is { Count: > 0 });
            bool hasDirectCardSelected = coreTpl?.Abilities.Values.Any(a =>
                a.Trigger != null &&
                string.Equals(a.Trigger.Type, "TTriggerOnCardSelected", StringComparison.OrdinalIgnoreCase)) == true;
            int directXp = Math.Max(0, coreTpl?.ExperienceAwardUponSelection ?? tpl?.ExperienceAwardUponSelection ?? 0);

            if (!hasSpawnCtx && !hasNextEnc && (hasDirectCardSelected || directXp > 0))
            {
                _run.PendingEncounterExitXp = directXp;
                FlushPendingEncounterExitXp(o.Events);
                _run.CurrentEncounterCoreCard = null;
                _run.CurrentEncounterId = null;
                _run.ParentEncounterTemplateId = null;
                o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
                ExitEncounterOrAdvance(o, nextOnExit: null, exitingRootId: encId, exitingStepId: encId);
                return;
            }
        }

        // 4. 商人 / 奇遇事件遭遇（及多步链式 EncounterStep）→ 进入 EncounterState 揭开迷雾并铺开真实选项
        _run.SelectionsMadeInCurrentContext = 0;
        var shelfEvents = DealEncounterShelfPreservingActiveEncounter(
            encId,
            isReroll: false,
            playerState: BuildCorePlayerFromRun(),
            ownedMaxTiers: _run.GetOwnedMaxTiers());
        NormalizeChildStepTransitionEvents(shelfEvents, encId);
        o.Events.AddRange(shelfEvents);

        // P1-04：若遭遇配置了 BoardOverride，持久化 CurrentBoardId 并与 StateTransitioned 同包发射 GameSimEventBoardOverridden
        string? encBoardOverride = coreTpl?.BoardOverride ?? tpl?.BoardOverride;
        string? encPresentation = coreTpl?.Presentation ?? tpl?.Presentation;
        if (!string.IsNullOrWhiteSpace(encBoardOverride))
        {
            _run.CurrentBoardId = encBoardOverride;
            o.Events.Add(new GameSimEventBoardOverridden(encBoardOverride, encPresentation));
        }

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
            stepResult = _stepEngine.ExecuteStep(stepEntry, (xp, srcId) =>
            {
                var evs = new List<IGameSimEvent>();
                GrantExperienceWithSourceToList(evs, xp, EExperienceType.Hour, srcId);
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
        if (stepResult.ModifiedCardIds.Count > 0)
        {
            var refreshTargets = new HashSet<IEffectTarget>();
            foreach (string modId in stepResult.ModifiedCardIds)
            {
                o.DirtyCardIds.Add(modId);
                if (_run.Cards.TryGetValue(modId, out var modEntry) && modEntry.Type == ECardType.Item)
                    refreshTargets.Add(new EffectTargetCard { Target = new BazaarGameShared.Domain.Core.InstanceId(modId) });
            }
            if (refreshTargets.Count > 0)
            {
                o.Events.Add(new GameSimEventEffectAuraExecuted
                {
                    ExecutionContextId = "step_sync_" + _run.Day + "_" + _run.Hour,
                    EffectId = "EncounterStepAttrSync",
                    AppliedTo = refreshTargets,
                });
            }
        }

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
            {
                ApplyWillAutoSellOnExitIfNeeded(o.Events);
                ClearPendingExpeditionAfterLootIfNeeded(o.Events);
            }
            else
                FlushPendingEncounterExitXp(o.Events);
            if (stepResult.ExitedReplacementSet)
                ResetBoardOverrideIfNeeded(o.Events);
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
        if (stepResult.ExitedReplacementSet)
            ResetBoardOverrideIfNeeded(o.Events);
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
            return NotAllowed($"对局已结束（{_run.State}），无法刷新");

        if (_run.State == ERunState.Loot && (_run.CustomRules.MonsterLootChooseOne || _shop.MonsterLootPages.Count > 1 || _run.MonsterLootPages.Count > 1))
        {
            if (!_shop.TryCycleMonsterLootPage(out var pageEvents))
                return NotAllowed("当前野怪战利品没有更多可翻页的选项");

            var lootOutcome = new Outcome();
            lootOutcome.Events.AddRange(pageEvents);
            TriggerOnEncounterCardsDealtOnly(_run.CurrentEncounterId, lootOutcome.Events);
            return lootOutcome;
        }

        if (_run.State != ERunState.Encounter)
            return NotAllowed($"当前状态（{_run.State}）不支持刷新商店");
        if (!StartSelectionDone)
            return NotAllowed("开局选择阶段不允许刷新");

        string encId = _run.CurrentEncounterId ?? "";
        if (string.IsNullOrEmpty(encId))
            return NotAllowed("当前没有可刷新的活跃遭遇");

        var coreTpl = _repo.GetCard(encId);
        var poolTpl = _pool.Get(encId);
        string encName = poolTpl?.InternalName ?? coreTpl?.InternalName ?? "";
        bool isMerchant = CardArchetypeIndex.IsMerchantEncounterTemplate(encId, encName, poolTpl, coreTpl);
        bool hasRerollRules = _run.SelectionRules.RerollRules != null;
        bool hasShopGoods = _run.SelectionSet.Any(id =>
            _run.Cards.TryGetValue(id, out var ce) && ce.Type is ECardType.Item or ECardType.Skill);

        if (!hasRerollRules && !isMerchant)
            return NotAllowed("当前遭遇不支持刷新");
        if (!hasRerollRules && !hasShopGoods)
            return NotAllowed("当前商店没有可刷新的商品");

        bool isSandboxFreeReroll = _run.IsSandboxRun && _run.CustomRules.SandboxFreeInfiniteReroll;
        bool isBloodMarket = !isSandboxFreeReroll && _run.CustomRules.HasMutator(MutatorCatalog.BloodMarket);

        if (!isSandboxFreeReroll && _run.RerollsRemaining == 0)
            return NotAllowed("本商店剩余刷新次数已用尽");

        // P1-18：扣费前按最新 RerollCostModifier（含光环与限时层，下限 0）刷新当前有效重随费用
        _shop.RefreshEffectiveRerollCost();
        int cost = (isSandboxFreeReroll || isBloodMarket) ? 0 : (int)_run.RerollCost;
        int gold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        if (gold < cost) return NotAllowed($"金币不足以刷新商店：需要 {cost}，当前仅有 {gold}");

        var o = new Outcome();
        if (isBloodMarket)
        {
            int curMaxHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250);
            int hpCost = Math.Max(50, (int)Math.Round(curMaxHp * 0.05));
            if (curMaxHp <= 50 || curMaxHp - hpCost < 50)
                return NotAllowed($"血色黑市：生命上限过低（当前 {curMaxHp}），无法继续献祭刷新");
            MutatePlayerHealthMax(-hpCost, o.Events, clampHealth: true);
        }
        else if (cost > 0)
        {
            MutatePlayerGold(-cost, o.Events, trackCustom3OnGain: false);
        }

        if (isSandboxFreeReroll)
        {
            _run.BaseRerollCost = 0;
            _run.SetRerollCost(0);
        }
        else if (isBloodMarket)
        {
            _run.BaseRerollCost = 0;
            _run.SetRerollCost(0);
            if (_run.RerollsRemaining > 0) _run.RerollsRemaining--;
        }
        else
        {
            int costIncrease = _run.SelectionRules.RerollRules?.CostIncrease ?? 1;
            if (_run.CustomRules.HasMutator(MutatorCatalog.ShopHalfOff))
                costIncrease = Math.Max(1, (int)Math.Floor(costIncrease * 0.5));
            uint currentBase = _run.BaseRerollCost ?? _run.RerollCost;
            uint nextBase = (uint)Math.Max(0, (int)currentBase + costIncrease);
            if (_run.SelectionRules.RerollRules?.CostMax is int costMax && costMax > 0)
                nextBase = Math.Min(nextBase, (uint)costMax);
            _run.BaseRerollCost = nextBase;
            _run.SetRerollCost(_shop.ComputeEffectiveRerollCost(nextBase));
            if (_run.RerollsRemaining > 0) _run.RerollsRemaining--;
        }

        _run.DailyRerollSequence++;

        var shelfEvents = DealEncounterShelfPreservingActiveEncounter(
            encId,
            isReroll: true,
            playerState: BuildCorePlayerFromRun(),
            ownedMaxTiers: _run.GetOwnedMaxTiers());
        NormalizeChildStepTransitionEvents(shelfEvents, encId);
        o.Events.AddRange(shelfEvents);
        // P2-14：OnReroll 统一走 SetRerollCost(forceEmit: true) 保证刷新后必带 RerollCostChanged 动画反馈
        _run.SetRerollCost(_run.RerollCost, o.Events, forceEmit: true);

        TriggerOnEncounterCardsDealtOnly(encId, o.Events);
        return o;
    }

    private Outcome OnExitState()
    {
        var o = new Outcome();

        // P2-35：终局态已由 Handle() L578 统一前置拦截，此处不再保留不可达死代码

        if (!StartSelectionDone)
        {
            if (!_run.SelectionRules.CanExit && _run.SelectionSet.Count > 0)
                return NotAllowed("开局选择阶段必须先做出选择，无法直接退出");

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
                return NotAllowed("必须先选择一个遭遇，无法直接跳过当前时段");

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
            return NotAllowed("战斗进行中无法退出当前状态");

        if (_run.State is ERunState.Loot or ERunState.LevelUp)
        {
            if (!_run.SelectionRules.CanExit && _run.SelectionSet.Count > 0)
                return NotAllowed($"当前阶段（{_run.State}）必须先完成选择后方可退出");

            if (_run.State == ERunState.Loot)
            {
                ApplyWillAutoSellOnExitIfNeeded(o.Events);
                ClearPendingExpeditionAfterLootIfNeeded(o.Events);
            }
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            AdvanceAfterSubState(o);
            return o;
        }

        if (_run.State is ERunState.Encounter or ERunState.Pedestal)
        {
            if (!_run.SelectionRules.CanExit && _run.SelectionSet.Count > 0)
                return NotAllowed($"当前遭遇（{_run.State}）不允许直接退出");
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
            if (string.IsNullOrEmpty(_run.ActiveReplacementSetId) && string.IsNullOrEmpty(_run.ActiveExpeditionId))
                ResetBoardOverrideIfNeeded(o.Events);
            HandleNoHourProgressEncounterExit(o, exitingRootId, exitingStepId);
            return;
        }

        if (string.IsNullOrEmpty(_run.ActiveReplacementSetId) && string.IsNullOrEmpty(_run.ActiveExpeditionId))
            ResetBoardOverrideIfNeeded(o.Events);

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
            // P2-34：统一由 ConsumeNextPendingLevelUpToEvents 发射成对 InterruptStateEntered（连升时先 Exited 上一级）
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
            _run.CurrentEncounterCoreCard = restored.State is ERunState.Encounter or ERunState.Pedestal && !string.IsNullOrEmpty(restored.CurrentEncounterId)
                ? CreateEncounterCoreCard(restored.CurrentEncounterId)
                : null;
            _run.SelectionRules = RunState.CloneRules(restored.SelectionRules);
            _run.BaseRerollCost = restored.BaseRerollCost;
            _run.SetRerollCost(restored.RerollCost);
            _run.RerollsRemaining = restored.RerollsRemaining;
            _run.SelectionsMadeInCurrentContext = restored.SelectionsMadeInCurrentContext;
            _run.CurrentMaxSelections = restored.CurrentMaxSelections;
            _run.PendingEncounterExitXp = restored.PendingEncounterExitXp;
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

            // V13.1 Hour 6 保底（根因 #5）：若恢复目标是 Hour 6 Choice（如在 Hour 6 桌面卖经验道具触发中途升级后返回），
            // 严禁复用已被 GameSimEventCardDisposed 销毁过的旧 pvp_xxxx InstanceId（避免与客户端 DisposedCards 竞态吞卡），
            // 强制重新发一张全新 InstanceId 的 PvpEncounter 并同步锁定对手镜像！
            if (restored.State == ERunState.Choice && _run.Hour == (uint)_mode.HoursInADay)
            {
                _run.ActiveShelfDiscounts.Clear();
                _shop.RestoreActiveShelfDiscountsFromRun();
                _run.SelectionsMadeInCurrentContext = 0;
                events.AddRange(DealHourEncountersAndClearActive());
                EnsurePendingHour6PvpOpponentLocked(events);
                EmitPostLevelUpSocketsUnlockedIfNeeded(exitingState, events);
                return;
            }

            var idRemap = new Dictionary<string, string>(StringComparer.Ordinal);
            if (restored.SuspendedCards.Count > 0)
            {
                // P1-32：对所有非 Hour 6 弹栈挂起卡一律重签全新 InstanceId，防止客户端 DisposedCards 缓存拒绝已销毁旧 ID
                foreach (var ce in restored.SuspendedCards)
                {
                    string oldId = ce.InstanceId;
                    string freshId = _shop.NewId(ResolveSuspendedCardPrefix(ce.Type));
                    idRemap[oldId] = freshId;
                    var reissued = CloneCardEntryWithNewInstanceId(ce, freshId);
                    _run.Cards[freshId] = reissued;
                    _run.SelectionSet.Add(freshId);
                    events.Add(new GameSimEventCardDealt(freshId, reissued.TemplateId.ToString(), reissued.Type));
                }
                RestoreSuspendedShelfDiscounts(restored.ActiveShelfDiscounts, idRemap);
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
                RestoreSuspendedShelfDiscounts(restored.ActiveShelfDiscounts, idRemap);
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
                _run.BaseRerollCost = restored.BaseRerollCost;
                _run.RerollCost = restored.RerollCost;
                _run.RerollsRemaining = restored.RerollsRemaining;
                _run.SelectionRules = RunState.CloneRules(restored.SelectionRules);
                _shop.RefreshEffectiveRerollCost();
            }
            else if (restored.State == ERunState.Choice)
            {
                _run.ActiveShelfDiscounts.Clear();
                _shop.RestoreActiveShelfDiscountsFromRun();
                _run.SelectionsMadeInCurrentContext = 0;
                events.AddRange(DealHourEncountersAndClearActive());
            }
            else
            {
                RestoreSuspendedShelfDiscounts(restored.ActiveShelfDiscounts, idRemap);
                events.Add(new GameSimEventStateTransitioned(restored.State, transitionEncId));
            }
            _shop.RefreshEffectiveRerollCost(_run.State == ERunState.Encounter ? events : null);
            EmitPostLevelUpSocketsUnlockedIfNeeded(exitingState, events);
            if (_run.State == ERunState.Choice && _run.Hour == (uint)_mode.HoursInADay)
                EnsurePendingHour6PvpOpponentLocked(events);
            return;
        }

        ERunState prevSubState = _run.State;
        // P2-34：非栈式离开 LevelUp 同样对称补发 InterruptStateExited(LevelUp, true)
        if (prevSubState == ERunState.LevelUp)
        {
            events.Add(new GameSimEventInterruptStateExited(ERunState.LevelUp, true));
        }
        AdvanceHourOrDay(events);
        EmitPostLevelUpSocketsUnlockedIfNeeded(prevSubState, events);
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
        int combatSeed = _run.CustomRules.FixedRunSeed is int dailyFixedSeed && dailyFixedSeed != 0
            ? DailyChallengeSeedGenerator.DeriveSubSeed(
                dailyFixedSeed,
                DailyChallengeSeedGenerator.StreamCombat,
                (int)_run.Day,
                (int)_run.Hour)
            : _rng.Next(1, int.MaxValue);

        if (isPvp && tourneyLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId))
        {
            // P0-4 并发安全：进入 PvP 前先将本方当前盘面不可变锁入 ITournamentPvpCoordinator
            if (tourneyLobby.TournamentSessionId is Guid lockSid && lockSid != Guid.Empty)
            {
                _pvpCoordinator?.LockPlayerBoardForDay(
                    lockSid,
                    _run.Day,
                    BuildLockedPvpBoardSnapshot());
            }

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
        LockedPvpBoard? lockedOppBoard = null;
        int oppGold = 0;
        int oppIncome = Math.Max(1, tourneyLobby?.Rules?.StartingIncome ?? _run.CustomRules?.StartingIncome ?? _mode.StartingIncome);
        string ghostPartition = _run.CustomRules.ResolveGhostPoolPartition();

        if (isPvp)
        {
            // P0-4 根治：彻底禁止无锁跨会话直读对方 RunState；仅通过 ITournamentPvpCoordinator 读取同日不可变锁板快照
            if (tourneyLobby != null &&
                tourneyLobby.TournamentSessionId is Guid oppLockSid && oppLockSid != Guid.Empty &&
                tourneyAssignment != null &&
                !tourneyAssignment.OpponentIsBot &&
                _pvpCoordinator != null)
            {
                lockedOppBoard = _pvpCoordinator.TryGetLockedBoard(
                    oppLockSid,
                    _run.Day,
                    tourneyAssignment.OpponentAccountId);
            }

            if (lockedOppBoard != null && tourneyAssignment != null)
            {
                p2Core = BuildOpponentPlayerStateFromLockedBoard(lockedOppBoard);
                oppGold = Math.Max(0, lockedOppBoard.Gold);
                oppIncome = lockedOppBoard.Income > 0 ? lockedOppBoard.Income : oppIncome;
                EHero lockedHero = PvpGhostPool.ParseHero(lockedOppBoard.Hero);
                pvpInfo = BuildRichSimPvpOpponent(
                    displayName: tourneyAssignment.OpponentUsername,
                    accountId: tourneyAssignment.OpponentAccountId,
                    hero: lockedHero,
                    victories: lockedOppBoard.Victories,
                    prestige: Math.Max(1, lockedOppBoard.Prestige),
                    level: Math.Max(1, lockedOppBoard.Level),
                    rating: 1200 + (int)lockedOppBoard.Victories * 40);
                Console.WriteLine($"[RunEngine] PVP Match Day={_run.Day}: vs LOCKED HUMAN {tourneyAssignment.OpponentUsername} (HP={p2Core.HealthMax}, items={p2Core.Hand.Cards.Count})");
            }
            else
            {
                int expectedDay = _run.CustomRules.SpecialModeId == ESpecialModeId.EndlessTower
                    ? (int)_run.Day
                    : Math.Clamp((int)_run.Day, 1, 15);
                var lockedGhost = _run.PendingPvpGhost;
                // V13.2 修复问题 #8：自定义规则模式允许回落抽取标准池幽灵（lockedGhost.PoolPartition="standard" 而 ghostPartition="custom"），
                // 严禁在此错误比对 PoolPartition 导致点击 Hour 6 对手头像时丢弃已锁定预览幽灵、重抽一名完全不同的英雄！
                bool partitionCompatible = lockedGhost != null && (
                    string.Equals(lockedGhost.PoolPartition, ghostPartition, StringComparison.OrdinalIgnoreCase)
                    || (_run.IsCustomRuleRun && string.Equals(lockedGhost.PoolPartition, PvpGhostPool.StandardPartition, StringComparison.OrdinalIgnoreCase)));
                bool canUseLockedGhost = lockedGhost != null
                                      && lockedGhost.Day == expectedDay
                                      && partitionCompatible
                                      && (_run.IsCustomRuleRun || (!lockedGhost.IsCustomRule && Storage.Persistence.IsSaneGhostForDay(Math.Clamp(expectedDay, 1, 15), lockedGhost.HealthMax)));
                var ghostRng = tourneyAssignment != null
                    ? new Random(combatSeed)
                    : (_run.CustomRules.FixedRunSeed is int dSeed && dSeed != 0
                        ? DailyChallengeSeedGenerator.CreateStreamRng(dSeed, DailyChallengeSeedGenerator.StreamPvpOpponent, (int)_run.Day, (int)_run.Hour)
                        : _rng);
                var ghost = canUseLockedGhost
                    ? lockedGhost!
                    : _ghosts.PickOpponent(
                        (int)_run.Day,
                        (int)_run.Victories,
                        (int)_run.Defeats,
                        ghostRng,
                        _repo,
                        includeCustomRuleGhosts: _run.IsCustomRuleRun,
                        poolPartition: ghostPartition);
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
                // 仅当严格复用了预览幽灵时才保留预览 accountId；若极端兜底重抽了新幽灵，使用新幽灵自己的 BoardId 避免旧 accountId + 新英雄张冠李戴
                string oppAccountId = tourneyAssignment?.OpponentAccountId
                    ?? (canUseLockedGhost ? _run.CurrentPvpOpponent?.PlayerLoadout?.accountId : null)
                    ?? (!string.IsNullOrWhiteSpace(ghost.BoardId) ? $"ghost_{ghost.BoardId}" : $"ghost_{Guid.NewGuid():N}");
                string oppDisplayName = tourneyAssignment != null
                    ? tourneyAssignment.OpponentUsername
                    : (string.IsNullOrWhiteSpace(ghost.Hero) ? "Challenger" : $"{ghost.Hero} ({ghost.PowerBucket})");
                EHero oppHero = PvpGhostPool.ParseHero(ghost.Hero);
                pvpInfo = BuildRichSimPvpOpponent(
                    displayName: oppDisplayName,
                    accountId: oppAccountId,
                    hero: oppHero,
                    victories: (uint)Math.Max(0, ghost.Wins),
                    prestige: Math.Max(1, ghost.Prestige),
                    level: Math.Max(1, ghost.Level),
                    rating: 1000 + (int)(ghost.EstimatedWinRate * 500));

                Console.WriteLine($"[RunEngine] PVP Match Day={_run.Day}: vs {oppDisplayName} [{ghost.BoardId}] (Bot={tourneyAssignment?.OpponentIsBot}, Partition={ghostPartition}, HP={p2Core.HealthMax}, items={p2Core.Hand.Cards.Count})");
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

            // Boss 车轮战：按波次强化首领装备最低品阶与额外强化技能
            if (_run.CustomRules.SpecialModeId == ESpecialModeId.BossRush)
            {
                ApplyBossRushWaveReinforcements(p2Core, BossRushCatalog.GetWave((int)_run.Day), combatSeed);
            }

            Console.WriteLine($"[RunEngine] PVE Match Day={_run.Day}: vs {p2Core.DisplayName} (mode={_run.CustomRules.SpecialModeId}, HP={p2Core.HealthMax}, items={p2Core.Hand.Cards.Count})");
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

        // 4. 构造模式/词条/无尽/BossRush 战斗修正器并执行 BazaarCore 完整规则战斗模拟（50ms 采样直降内存与包体积）
        bool isBossRush = _run.CustomRules.SpecialModeId == ESpecialModeId.BossRush;
        var combatModifiers = CombatModifiers.FromRulesAndDay(
            _run.CustomRules,
            (int)_run.Day,
            isBossRush: isBossRush,
            isPve: !isPvp,
            bossRushWave: (int)_run.Day);
        bool sandstormEnabled = (encTpl?.SandstormEnabled ?? true) && combatModifiers.SandstormEnabled;

        // P1-05：在 CombatSimulator.Simulate 之前先快照对手开战初值（避免模拟过程中修改 p2Core.Attributes 污染开战快照）
        var p2InitialAttributes = new Dictionary<string, double>(p2Core.Attributes, StringComparer.OrdinalIgnoreCase);

        var result = CombatSimulator.Simulate(
            _repo,
            p1Core,
            p2Core,
            seed: combatSeed,
            combatType: isPvp ? "PVP" : "PVE",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: false,
            modifiers: combatModifiers,
            sandstormEnabled: sandstormEnabled,
            timelineSampleIntervalMs: CombatSimConverter.FrameStepMs);

        bool win = string.Equals(result.WinnerPlayerId, "player", StringComparison.OrdinalIgnoreCase);
        bool isSyncDraw = false;
        const string SyncDrawWinnerSentinel = "__draw__";

        // P0-4 & P1-55 唯一对称裁决：真人 vs 真人同步 PvP 时，按规范对阵序唯一写入 ITournamentPvpCoordinator 确保 A 胜 <=> B 负；
        // 若双方同帧阵亡或超时共同存活，写入平局哨兵 "__draw__"（兼容 coordinator 非空落盘与重启恢复），双方均判平不扣声望不偏袒 canonicalP1
        if (isPvp &&
            tourneyLobby != null &&
            tourneyLobby.TournamentSessionId is Guid resolveSid && resolveSid != Guid.Empty &&
            tourneyAssignment != null &&
            !tourneyAssignment.OpponentIsBot &&
            lockedOppBoard != null &&
            _pvpCoordinator != null &&
            !string.IsNullOrWhiteSpace(_run.AccountId))
        {
            var resolved = _pvpCoordinator.TryGetResolvedMatch(
                resolveSid,
                _run.Day,
                _run.AccountId,
                tourneyAssignment.OpponentAccountId);

            if (resolved == null)
            {
                bool selfIsCanonicalP1 = StringComparer.Ordinal.Compare(_run.AccountId, tourneyAssignment.OpponentAccountId) <= 0;
                bool canonicalIsDraw;
                bool canonicalP1Won;
                if (selfIsCanonicalP1)
                {
                    canonicalIsDraw = result.IsDraw
                        || (result.Player1RemainingHp <= 0 && result.Player2RemainingHp <= 0)
                        || (result.Player1RemainingHp > 0 && result.Player2RemainingHp > 0);
                    canonicalP1Won = !canonicalIsDraw && win;
                }
                else
                {
                    var canonicalSim = CombatSimulator.Simulate(
                        _repo,
                        p2Core,
                        p1Core,
                        seed: combatSeed,
                        combatType: "PVP",
                        recordTimeline: false,
                        recordEvents: false,
                        syncPermanentChanges: false,
                        modifiers: combatModifiers,
                        sandstormEnabled: sandstormEnabled);
                    canonicalIsDraw = canonicalSim.IsDraw
                        || (canonicalSim.Player1RemainingHp <= 0 && canonicalSim.Player2RemainingHp <= 0)
                        || (canonicalSim.Player1RemainingHp > 0 && canonicalSim.Player2RemainingHp > 0);
                    canonicalP1Won = !canonicalIsDraw && (
                        string.Equals(canonicalSim.WinnerPlayerId, "player", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(canonicalSim.WinnerPlayerId, p2Core.PlayerId, StringComparison.OrdinalIgnoreCase));
                }

                string canonicalP1Id = selfIsCanonicalP1 ? _run.AccountId : tourneyAssignment.OpponentAccountId;
                string canonicalP2Id = selfIsCanonicalP1 ? tourneyAssignment.OpponentAccountId : _run.AccountId;
                string winnerId = canonicalIsDraw ? SyncDrawWinnerSentinel : (canonicalP1Won ? canonicalP1Id : canonicalP2Id);
                string loserId = canonicalIsDraw ? SyncDrawWinnerSentinel : (canonicalP1Won ? canonicalP2Id : canonicalP1Id);

                var proposed = new SymmetricHumanPvpOutcome(
                    WinnerAccountId: winnerId,
                    LoserAccountId: loserId,
                    BattleSeed: combatSeed,
                    LoserPrestigeDamage: canonicalIsDraw ? 0 : (int)_run.Day,
                    ReplayId: $"pvp_{resolveSid:N}_d{_run.Day}_{combatSeed}",
                    CachedCombatSimForP1: null,
                    CachedCombatSimForP2: null);
                resolved = _pvpCoordinator.RecordOrGetResolvedMatch(
                    resolveSid,
                    _run.Day,
                    _run.AccountId,
                    tourneyAssignment.OpponentAccountId,
                    proposed);
            }

            isSyncDraw = string.IsNullOrEmpty(resolved.WinnerAccountId)
                || string.Equals(resolved.WinnerAccountId, SyncDrawWinnerSentinel, StringComparison.OrdinalIgnoreCase);
            win = !isSyncDraw && string.Equals(resolved.WinnerAccountId, _run.AccountId, StringComparison.OrdinalIgnoreCase);
        }

        // 4. 预先计算 PvE 胜负金币/经验，供 CombatSimConverter 生成血条刻度与跳币/飞经验事件
        //    V13.1 问题 #1 权威对齐（Wiki / Mobalytics）：
        //    - Hour 6 PvP 战斗本身给 0 战斗 XP 与 0 战斗金币（严禁再读取 DB 占位残留 RewardCombatXp=3 白送 3 XP！），
        //      保留小时门控未置位，交由战后 AdvanceHourOrDay 跨日推进时自然发放 +1 小时 XP（标准一天总计精确 8 XP，打钻/传怪 9 XP）；
        //    - Hour 3 PvE 野怪战斗发放 RewardCombatXp（铜/银/金=3 XP，钻/传=4 XP）并同步关闭小时双门控，绝不额外叠加 +1 小时 XP。
        int goldGain;
        int xpGain;
        int pveGoldForSim = 0;
        int pveXpForSim = 0;

        if (isPvp)
        {
            goldGain = 0;
            xpGain = 0;
        }
        else
        {
            _run.GainedHourXpThisHour = true;
            _run.GainedAdvanceHourXpThisHour = true;

            int monsterMaxHp = Math.Max(1, p2Core.HealthMax);
            int monsterRemHp = Math.Clamp(result.Player2RemainingHp, 0, monsterMaxHp);
            if (win)
            {
                int baseCombatGold = encTpl?.RewardCombatGold ?? 3;
                int baseCombatXp = encTpl?.RewardCombatXp ?? 3;
                int victoryGold = encTpl?.RewardVictory?.GoldReward ?? encTpl?.RewardVictoryGold ?? 0;
                goldGain = baseCombatGold + victoryGold;
                xpGain = baseCombatXp;

                if (isBossRush)
                {
                    var waveCfg = BossRushCatalog.GetWave((int)_run.Day);
                    _run.BossRushWavesCleared++;
                    goldGain += waveCfg.BonusGoldReward;
                    xpGain += waveCfg.BonusXpReward;
                }
            }
            else
            {
                var defeatRewards = ShopEngine.CalculateMonsterDefeatRewards(monsterMaxHp, monsterRemHp, encTpl);
                goldGain = defeatRewards.GoldAwarded;
                xpGain = defeatRewards.XpAwarded;
            }
            if (_run.CustomRules.HasMutator(MutatorCatalog.DoubleEconomy))
            {
                goldGain *= 2;
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
        // P1-05：完整同步野怪/对手开战前 p2InitialAttributes 至 _run.OpponentAttributes（覆盖 HealthRegen / Income / RageMax / EnragedDurationMax / TempoGainCooldownMax / Custom_* 等稀有属性），
        // 解决 CombatSimConverter 帧间差分发送导致开战恒定属性整场不显示的缺陷，同时保留 Health = HealthMax。
        foreach (var (attrKey, attrVal) in p2InitialAttributes)
        {
            if (string.Equals(attrKey, "Health", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Enum.TryParse<EPlayerAttributeType>(attrKey, ignoreCase: true, out var attrEnum))
            {
                _run.OpponentAttributes[attrEnum] = (int)Math.Round(attrVal);
            }
        }
        _run.OpponentAttributes[EPlayerAttributeType.HealthMax] = p2Core.HealthMax;
        _run.OpponentAttributes[EPlayerAttributeType.Health] = p2Core.HealthMax;

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
        if (isSyncDraw)
        {
            o.Combat.IsDraw = true;
        }

        // 战报自动入库（沙盒对局除外）：持久化双方盘面摘要与压缩后 CombatSim MsgPack 载荷
        SaveCombatReplayIfEligible(
            isPvp: isPvp,
            win: win,
            combatSeed: combatSeed,
            result: result,
            p1Core: p1Core,
            p2Core: p2Core,
            pvpInfo: pvpInfo,
            lobbyId: tourneyLobby?.LobbyId,
            combatSim: o.Combat);

        Console.WriteLine(
            $"[RunEngine] CombatSim done: {(isSyncDraw ? "DRAW" : (win ? "WIN" : "LOSE"))} dur={result.DurationMs}ms " +
            $"frames={o.Combat.Frames.Count} p1RemHp={result.Player1RemainingHp}/{p1Core.HealthMax} " +
            $"p2RemHp={result.Player2RemainingHp}/{p2Core.HealthMax} pveGold={pveGoldForSim} pveXp={pveXpForSim}");

        // 7. 构造第 3 条消息：DespawnMessage（清理敌方临时卡 + 战斗永久成长回写 + 临时变形还原 + OnFightEnded + 奖励/战利品）
        var despawnEvents = new List<IGameSimEvent>();
        foreach (var oe in oppEntries)
            despawnEvents.Add(new GameSimEventCardDisposed(oe.InstanceId, ECardDisposalReason.Cleared));

        _run.PendingPvpGhost = null;
        _run.CurrentPvpOpponent = null;
        _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(100, 100, gold: 0, income: 5);

        // 7a. 战后永久状态回写（永久伤害/护盾/价值/附魔/升阶/任务进度/变形/衍生卡/玩家永久属性）
        var permSync = CombatSimulator.SyncPermanentChangesBack(result, p1Core);
        ApplyPostCombatPermanentSync(p1Core, permSync, despawnEvents);

        // 7a-0. P1-10：消费 CombatResult.TemporaryTransforms，在 DespawnMessage 发射 GameSimEventCardTransformReverted 并同步原卡
        EmitPostCombatTemporaryTransformReverts(result.TemporaryTransforms, despawnEvents, o.DirtyCardIds);

        // 7a-1. V14 开放自定义机制战后受控结转（如永久碎裂移除或永久属性修正，统一走官方协议事件防客户端脱节）
        ApplyPostCombatMechanicOps(p1Core, result.PostCombatOps, despawnEvents);

        // 7a-2. 记录官方 [PlayerAttributes] 战斗胜负追踪（Custom_5 总胜场 / Custom_6 总败场 / Custom_7 PvE 败 / Custom_8 PvP 败）
        // 并同步战中可能变更的 Gold / Custom_3 / Custom_9 到 p1Core，确保 OnFightEnded 光环重算读取最新值
        if (isSyncDraw)
        {
            // P1-55：同步 PvP 平局不计入胜场也不计入败场
        }
        else if (win)
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
        string fightOutcomeStr = isSyncDraw ? "Draw" : (win ? "Win" : "Lose");
        var fightEndedBatch = OutOfCombatController.OnFightEnded(
            p1Core,
            combatType: isPvp ? "PVP" : "PVE",
            combatOutcome: fightOutcomeStr,
            _repo,
            _rng,
            opponent: p2Core);
        ApplyOutOfCombatBatch(p1Core, fightEndedBatch, despawnEvents, syncBoardDiffs: true, xpType: EExperienceType.Combat);
        RecordChallengeFightEnded(isPvp, fightOutcomeStr);

        // 7b-2. 清除战斗临时状态（Freeze/Slow/Haste/Flying/Heated/Chilled/CombatAdd），并登记战后首帧显式归零下发
        ClearPostCombatTransientStatusAndSync(despawnEvents, o.DirtyCardIds);

        // 7c. 结算胜负、Fates（Scheme A 1-声望 AI Bot 锁血）与本场金币/经验
        // #80 & P1-36：锦标赛达到 Day 25 硬上限（isPvp && Day >= maxAllowedDay）时直接跳过 Fates 复活走安全帽完赛，
        // 绝不同时出现“声望复活为 1 + 当帧 CrownDefeat”矛盾；标准非锦标赛局保持 P1-36 首次濒死 Fates 复活豁免。
        int hardMaxDay = _run.IsSandboxRun ? 99 : 25;
        bool suppressFatesForTournamentDayCap = IsTournamentRun() && isPvp && (int)_run.Day >= hardMaxDay;
        bool triggeredFates = false;
        if (isPvp)
        {
            if (isSyncDraw)
            {
                // P1-55：同步 PvP 平局双方均不加胜负计数、不扣声望
            }
            else if (win)
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

                if (!suppressFatesForTournamentDayCap && clampedPrestige <= _mode.FatesSpawnAtOrBelowPrestige && !_run.FatesUsed)
                {
                    // Scheme A：首次声望 <= 0（无论对手是 AI 还是真人），均触发 Fates 并设置 Prestige = 1
                    _run.FatesUsed = true;
                    clampedPrestige = Math.Max(1, _mode.FatesAfterPrestige);
                    triggeredFates = true;
                    Console.WriteLine($"[RunEngine] FATES triggered! Prestige set to {clampedPrestige}, template={_mode.FatesTemplateId}");
                }
                else if (!suppressFatesForTournamentDayCap && _run.FatesUsed && botPrestigeLock && !isRealHumanOpp && clampedPrestige <= 0)
                {
                    // Scheme A：Fates 已用过且对手为非致死 AI Bot 时，强制锁血 Prestige >= 1，绝不因 Bot 淘汰
                    clampedPrestige = 1;
                }

                int actualPrestigeDelta = clampedPrestige - rawPrestige;
                _run.PlayerAttributes[EPlayerAttributeType.Prestige] = clampedPrestige;
                despawnEvents.Add(new GameSimEventPlayerPrestigeChanged(actualPrestigeDelta));
            }
        }
        else if (!win && _mode.PrestigeLossFromMonsters > 0)
        {
            // P2-04：PvE 战败分支接入 game_modes.Prestige.PrestigeLossFromMonsters > 0 扣减声望（供自建服/Mod 规则生效）
            int rawPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
            int clampedPrestige = Math.Max(0, rawPrestige - _mode.PrestigeLossFromMonsters);
            int actualPrestigeDelta = clampedPrestige - rawPrestige;
            if (actualPrestigeDelta != 0)
            {
                _run.PlayerAttributes[EPlayerAttributeType.Prestige] = clampedPrestige;
                despawnEvents.Add(new GameSimEventPlayerPrestigeChanged(actualPrestigeDelta));
            }
        }

        // 兜底：即使在 OnFightEnded 等阶段将 Prestige 扣至 <= 0 且尚未使用过 Fates，同样触发一次 Last Chance；
        // 若处于已使用 Fates 的 AI Bot 非致死锁血保护期，则兜底锁血 Prestige = 1
        if (!triggeredFates && !suppressFatesForTournamentDayCap)
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
            MutatePlayerGold(goldGain, despawnEvents, trackCustom3OnGain: true);
            RecalculateAurasAndSyncBoard(despawnEvents);
        }
        if (xpGain > 0)
        {
            GrantExperienceToList(despawnEvents, xpGain, EExperienceType.Combat);
        }

        if (isPvp && !isSyncDraw && tourneyLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId) &&
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

        // 7d. 检查胜负终局条件（含 LastHumanStandingMode）；P1-36：triggeredFates=true 时豁免当回合天数上限判负
        string fatesTemplateId = !string.IsNullOrWhiteSpace(_mode.FatesTemplateId)
            ? _mode.FatesTemplateId
            : "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
        bool ended = TryEndRunToEvents(despawnEvents, justFinishedPvp: isPvp, triggeredFates: triggeredFates);

        if (isPvp && tourneyLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId))
        {
            _lobbies!.RecordRunProgress(tourneyLobby, _run.AccountId, _run);
        }

        if (!ended)
        {
            if (triggeredFates &&
                (_pool.IsAssetValid(fatesTemplateId) || _repo.GetCard(fatesTemplateId) != null))
            {
                // P1-36：若 PvE 胜利的同时因 OnFightEnded 触发 Fates，先把野怪战利品压入 SuspendedStates，
                // 让玩家先完成 Fates 复活，退出 Fates 后自动弹栈领取野怪战利品（二者皆不丢失）
                if (!isPvp && win)
                {
                    SuspendMonsterVictoryLootForFates(encTpl, p1Core);
                }

                var fatesOutcome = new Outcome();
                TransitionToEncounterById(fatesTemplateId, fatesOutcome);
                despawnEvents.AddRange(fatesOutcome.Events);
            }
            else if (!isPvp && win)
            {
                _run.SelectionsMadeInCurrentContext = 0;
                _run.PendingClearExpeditionAfterLoot = false;
                // P0-02：PvE 胜利 → 无论 MonsterLootChooseOne 是否开启，RewardVictory.SpawnContext != null 优先执行并尊重其 CanExit；
                // 战利品阶段继续保留击败怪物的激活遭遇卡以展示顶栏头像
                var activeProtected = DetachActiveEncounterCardForShelfOp();
                despawnEvents.AddRange(_shop.DealMonsterVictoryLoot(
                    encTpl,
                    _lastCombatMonster,
                    p1Core,
                    chooseOne: _run.CustomRules.MonsterLootChooseOne));
                RestoreActiveEncounterCardAfterShelfOp(activeProtected);

                if (_run.State == ERunState.Loot && _run.SelectionSet.Count == 0)
                {
                    if (!_run.SelectionRules.CanExit)
                    {
                        var safeLootRules = RunState.CloneRules(_run.SelectionRules);
                        safeLootRules.CanExit = true;
                        _run.SelectionRules = safeLootRules;
                    }
                    // #1 空战利品兜底：绝不停留在空 Loot 货架卡死，立刻销毁怪物头像卡并顺畅进入升级/恢复挂起栈/下一小时
                    ActivateEncounterCard(null, selectedCard: null, despawnEvents, emitPurchaseIfSelected: false);
                    if (_run.PendingLevelUp)
                        ConsumeNextPendingLevelUpToEvents(despawnEvents);
                    else if (_run.SuspendedStates.Count > 0)
                        AdvanceAfterSubStateToEvents(despawnEvents);
                    else
                        AdvanceHourOrDay(despawnEvents, justCompletedPvp: isPvp);
                }
                else
                {
                    TriggerOnEncounterCardsDealtOnly(_run.CurrentEncounterId, despawnEvents);
                }
            }
            else
            {
                // P0-02：PvE 战败优先消费官方 RewardDefeat.SpawnContext 与 SelectionContextRules
                // （覆盖 22 战含三大远征 17 战败退场步骤如 You Awaken Later 销毁 Depleted Device、清 Custom_13、TActionExitReplacementSet），
                // 待完成退场步骤后再清远征状态；仅无退场步骤或空货架时立即清远征并推进。
                bool enteredDefeatLoot = false;
                if (!isPvp && !win && encTpl != null)
                {
                    var activeProtected = DetachActiveEncounterCardForShelfOp();
                    enteredDefeatLoot = _shop.TryDealMonsterDefeatLoot(
                        encTpl,
                        _lastCombatMonster,
                        p1Core,
                        out var defeatLootEvents);
                    RestoreActiveEncounterCardAfterShelfOp(activeProtected);

                    if (enteredDefeatLoot && _run.State == ERunState.Loot && _run.SelectionSet.Count > 0)
                    {
                        despawnEvents.AddRange(defeatLootEvents);
                        _run.PendingClearExpeditionAfterLoot =
                            !string.IsNullOrEmpty(_run.ActiveReplacementSetId) ||
                            !string.IsNullOrEmpty(_run.ActiveExpeditionId);
                        TriggerOnEncounterCardsDealtOnly(_run.CurrentEncounterId, despawnEvents);
                    }
                    else
                    {
                        enteredDefeatLoot = false;
                    }
                }

                if (!enteredDefeatLoot)
                {
                    // 无战败退场步骤时按官方规则直接退出探险替换集并复位切板，回归常规事件池
                    if (!isPvp && !win &&
                        (!string.IsNullOrEmpty(_run.ActiveReplacementSetId) || !string.IsNullOrEmpty(_run.ActiveExpeditionId)))
                    {
                        _run.ClearActiveReplacementSet();
                        ResetBoardOverrideIfNeeded(despawnEvents);
                    }

                    if (_run.PendingLevelUp)
                    {
                        ActivateEncounterCard(null, selectedCard: null, despawnEvents, emitPurchaseIfSelected: false);
                        ConsumeNextPendingLevelUpToEvents(despawnEvents);
                    }
                    else if (_run.SuspendedStates.Count > 0)
                    {
                        // V13.1 Hour 6 保底（根因 #6）：若由升级奖励或子状态意外触发战斗，战后优先通过 SuspendedStates 恢复挂起现场避免脏栈残留
                        ActivateEncounterCard(null, selectedCard: null, despawnEvents, emitPurchaseIfSelected: false);
                        AdvanceAfterSubStateToEvents(despawnEvents);
                    }
                    else
                    {
                        ActivateEncounterCard(null, selectedCard: null, despawnEvents, emitPurchaseIfSelected: false);
                        AdvanceHourOrDay(despawnEvents, justCompletedPvp: isPvp);
                    }
                }
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
                var tpl = _repo.GetCard(RunState.ResolveCoreTemplateId(entry));
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

        // 挂载当前对局流派解析器与自定义规则，使全部 17 类 OutOfCombatController 触发链路（铲子/钓竿/曼陀罗等）自动继承流派倾向与高阶掉率
        OutOfCombatController.AttachSpawnPreferences(ps, ResolveOutOfCombatArchetypeResolver(), _run.CustomRules);

        // 不在这里调用 ps.PrepareForCombat()，保留局外真实 CurrentHealth 及已累计的临时/永久层；
        // CombatSimulator.Simulate 内部 CloneForCombat() 会为战斗克隆体独立 PrepareForCombat()。
        return (ps, map);
    }

    private Func<string, ulong>? ResolveOutOfCombatArchetypeResolver()
    {
        var rules = _run.CustomRules;
        if (rules == null || rules.SelectedArchetypeMask == 0UL)
            return null;
        var index = _archetypeIndex ?? _shop.ArchetypeIndex ?? (_shop.ArchetypeIndex = CardArchetypeIndex.Build(_pool, _repo));
        return index.GetArchetypeMask;
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
        Guid wireGuid = ResolveSafeWireGuid(ci.Template.Id, type);
        string? customCardId = !string.Equals(ci.Template.Id, wireGuid.ToString(), StringComparison.OrdinalIgnoreCase)
            ? ci.Template.Id
            : null;
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = wireGuid,
            CustomCardId = customCardId,
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

    private void GrantExperience(Outcome o, int xpGain, EExperienceType expType, string? sourceInstanceId = null)
        => GrantExperienceWithSourceToList(o.Events, xpGain, expType, sourceInstanceId);

    private void GrantExperienceToList(List<IGameSimEvent> events, int xpGain, EExperienceType expType)
        => GrantExperienceWithSourceToList(events, xpGain, expType, sourceInstanceId: null);

    private void GrantExperienceWithSourceToList(List<IGameSimEvent> events, int xpGain, EExperienceType expType, string? sourceInstanceId)
    {
        if (xpGain <= 0) return;
        events.Add(new GameSimEventPlayerExperienceGained((uint)xpGain, expType, sourceInstanceId));
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

            int prevMax = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth);
            int newMax = prevMax + hpInc;
            _run.PlayerAttributes[EPlayerAttributeType.HealthMax] = newMax;
            _run.PlayerAttributes[EPlayerAttributeType.Health] = newMax;
            _run.PendingLevelUps++;

            ushort beforeUnlockMask = _run.UnlockedSlots;
            var unlocked = _run.UnlockCenteredSockets(Math.Max(1, _mode.NumCarpetSocketsUnlockPerLevel));
            _run.HandSocketUnlockMask = _run.UnlockedSlots;
            if (unlocked.Count > 0)
            {
                _run.LevelUpEntryUnlockedSlots ??= beforeUnlockMask;
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
            RecordChallengeLevelUp(reachedLvl);
        }
    }

    private void AdvanceHour(Outcome o) => AdvanceHourOrDay(o.Events);

    private void AdvanceHourToEvents(List<IGameSimEvent> events) => AdvanceHourOrDay(events);

    private bool RequiresPvpOnDay(uint day)
    {
        if (_run.CustomRules.SpecialModeId == ESpecialModeId.BossRush)
        {
            if (!_run.CustomRules.BossRushIncludeMilestonePvp)
                return false;
            return BossRushCatalog.GetWave((int)day).IsMilestonePvpWave;
        }
        return true;
    }

    private void AdvanceHourOrDay(List<IGameSimEvent> events, bool justCompletedPvp = false)
    {
        // 1. 切小时前清理 UntilEndOfHour 临时层并回写同步事件
        ClearHourModifiersOnRun(events);

        uint nextHour = _run.Hour + 1;
        // Boss 车轮战非里程碑波次（非第 5/10/... 波）跳过第 6 小时 PvP，在 Hour 5 结束后直接推进至下一波次
        bool skipHour6PvpForBossRush = nextHour == (uint)_mode.HoursInADay && !RequiresPvpOnDay(_run.Day);
        if (skipHour6PvpForBossRush)
        {
            nextHour = (uint)_mode.HoursInADay + 1;
        }

        bool newDay = nextHour > (uint)_mode.HoursInADay;
        if (newDay && !justCompletedPvp && RequiresPvpOnDay(_run.Day) && _run.Victories + _run.Defeats < _run.Day)
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

        // P0-1 根治：仅当本小时尚未通过遭遇离场/战斗/推进获取过每小时经验时才补发 ExperiencePerHour，彻底消灭每小时双份经验
        bool needAdvanceHourXp = !_run.GainedHourXpThisHour && !_run.GainedAdvanceHourXpThisHour;
        _run.Hour = nextHour;
        _run.RerollCost = _run.IsSandboxRun && _run.CustomRules.SandboxFreeInfiniteReroll ? 0u : 2u;
        _run.RerollsRemaining = 3;
        _run.GainedHourXpThisHour = false;
        _run.GainedAdvanceHourXpThisHour = false;
        _run.DailyRerollSequence = 0;

        if (newDay)
        {
            // V7-1.9 严格跨天顺序：
            // 1) 清理 UntilEndOfDay 临时层（并同步回写属性/附魔还原事件）与当日已访问遭遇集合
            ClearDayModifiersOnRun(events);
            _run.VisitedDayEncounterIds.Clear();
            _run.ClearOfferedDayEncounters();
            _run.ClearActiveReplacementSet();
            ResetBoardOverrideIfNeeded(events);
            _run.PendingPvpGhost = null;
            _run.CurrentPvpOpponent = null;

            // 2) 推进 Day 并重置 Hour = 1
            _run.Hour = 1;
            _run.Day++;
            events.Add(new GameSimEventRunDayChanged(_run.Day));

            // 3) 先发放当日 Income 收入到 Gold（双倍经济词条每日收入 ×2），确保 OnDayStarted() 触发器读取到最新金币
            int income = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, 0);
            if (_run.CustomRules.HasMutator(MutatorCatalog.DoubleEconomy))
                income *= 2;
            if (income > 0)
            {
                events.Add(new GameSimEventPlayerIncomeGained((uint)income));
                MutatePlayerGold(income, events, trackCustom3OnGain: true);
            }

            // 无尽爬塔里程碑日（Day 15/20/25...）：自动回复声望并奖励额外金币与全服播报
            if (_run.CustomRules.SpecialModeId == ESpecialModeId.EndlessTower)
            {
                var floorScaling = EndlessTowerScaler.Compute((int)_run.Day, _run.CustomRules.EndlessMaxDay);
                if (floorScaling.IsMilestoneDay)
                {
                    if (floorScaling.MilestonePrestigeRestore > 0)
                    {
                        int curPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
                        int maxPrestige = Math.Max(_mode.PrestigeMax, Math.Max(_run.CustomRules.StartingPrestige, curPrestige));
                        int nextPrestige = Math.Min(maxPrestige, curPrestige + floorScaling.MilestonePrestigeRestore);
                        if (nextPrestige > curPrestige)
                        {
                            _run.PlayerAttributes[EPlayerAttributeType.Prestige] = nextPrestige;
                            events.Add(new GameSimEventPlayerPrestigeChanged(nextPrestige - curPrestige));
                        }
                    }
                    if (floorScaling.MilestoneBonusGold > 0)
                    {
                        _run.AddPlayerGold(floorScaling.MilestoneBonusGold);
                    }
                    if (!_run.IsSandboxRun && _run.Day is 15 or 20 or 30 or 50 or 75 or 99)
                    {
                        string displayName = string.IsNullOrWhiteSpace(_run.Username) ? _run.AccountId : _run.Username;
                        _socialSink?.PublishSystemBroadcast(
                            lobbyId: null,
                            msgType: "endless_milestone",
                            contentZh: $"【无尽爬塔】玩家 {displayName}（{_run.Hero}）已成功挺进无尽第 {_run.Day} 层！",
                            meta: new { accountId = _run.AccountId, day = _run.Day, victories = _run.Victories });
                    }
                }
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

        // 3. 若刚结束的旧小时尚未获得过小时推进经验，则发放固定 ExperiencePerHour（+1 XP）
        if (needAdvanceHourXp && _mode.ExperiencePerHour > 0)
        {
            GrantExperienceToList(events, _mode.ExperiencePerHour, EExperienceType.Hour);
        }

        // 4. 若小时推进时升级经验足够，先把新小时的 Choice 现场压入 SuspendedStates 再进入 LevelUp，
        //    确保升级结束后恢复至新小时 Choice；Day 6/9/10 Hour 1 持对应探险门票时直接在 Choice 下发第 4 张探险入口卡。
        if (_run.PendingLevelUp)
        {
            PushChoiceAndEnterLevelUp(events);
            return;
        }

        _run.SelectionsMadeInCurrentContext = 0;
        events.AddRange(DealHourEncountersAndClearActive());
    }

    private bool TryEndRunToEvents(List<IGameSimEvent> events, bool justFinishedPvp = false, bool triggeredFates = false)
    {
        if (_runEnded) return true;

        int victories = (int)_run.Victories;
        int prestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
        int day = (int)_run.Day;
        var specialMode = _run.CustomRules.SpecialModeId;

        if (prestige <= 0 && _run.FatesUsed)
        {
            return CrownDefeat(events, prestige, day);
        }

        // Boss 车轮战：完成目标波次（默认 10 波）且（无里程碑 PvP 或已完成该波次 PvP）即宣告胜利
        if (specialMode == ESpecialModeId.BossRush)
        {
            int targetWaves = Math.Clamp(_run.CustomRules.BossRushTotalWaves, 1, 30);
            bool needFinalPvp = RequiresPvpOnDay((uint)day);
            if (_run.BossRushWavesCleared >= targetWaves && (!needFinalPvp || justFinishedPvp))
            {
                return CrownVictory(events, Math.Max(victories, _run.BossRushWavesCleared), day);
            }
            if (!triggeredFates && day > targetWaves + 5)
            {
                return CrownDefeat(events, prestige, day);
            }
            return false;
        }

        // 无尽爬塔模式：达到 10 胜时不强行终局，继续向 EndlessMaxDay（默认 99 层）攀登
        if (specialMode == ESpecialModeId.EndlessTower)
        {
            if (victories >= _mode.VictoriesToWin && !_run.EndlessTenWinCelebrated)
            {
                _run.EndlessTenWinCelebrated = true;
                if (!_run.IsSandboxRun)
                {
                    string displayName = string.IsNullOrWhiteSpace(_run.Username) ? _run.AccountId : _run.Username;
                    _socialSink?.PublishSystemBroadcast(
                        lobbyId: null,
                        msgType: "endless_ten_win",
                        contentZh: $"【无尽爬塔】玩家 {displayName}（{_run.Hero}）已达成 10 胜，正继续挑战无尽高层！",
                        meta: new { accountId = _run.AccountId, day, victories });
                }
            }

            int endlessMaxDay = Math.Clamp(_run.CustomRules.EndlessMaxDay, 11, EndlessTowerScaler.DefaultMaxDay);
            if (!triggeredFates && (day > endlessMaxDay || (justFinishedPvp && day >= endlessMaxDay)))
            {
                return CrownVictory(events, victories, day);
            }
            return false;
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

        // 沙盒模式放宽最大天数至 99 天；标准模式打满第 25 天防异常无限挂起；
        // P1-36：triggeredFates=true 时豁免当回合天数上限判负，优先完成 Fates 复活演出
        int maxAllowedDay = _run.IsSandboxRun ? 99 : 25;
        if (!triggeredFates && (day > maxAllowedDay || (justFinishedPvp && day >= maxAllowedDay)))
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
        var result = BuildCompleteRunResult(isVictory: true, isConceded: false, victoriesOverride: victories);
        events.Add(new GameSimEventStateTransitioned(ERunState.EndRunVictory, null));
        events.Add(new GameSimEventRunCompleted(result));
        PublishEndRunRecords(isVictory: true, isConceded: false);
        RecordChallengeRunCompleted(isVictory: true, isConceded: false);
        Console.WriteLine($"[RunEngine] RUN END: VICTORY (mode={_run.CustomRules.SpecialModeId} wins={victories}/{_mode.VictoriesToWin} day={day} chests={result.ChestCount} passXp={result.XpEarned})");
        return true;
    }

    private bool CrownDefeat(List<IGameSimEvent> events, int prestige, int day)
    {
        _run.State = ERunState.EndRunDefeat;
        _runEnded = true;
        var result = BuildCompleteRunResult(isVictory: false, isConceded: false);
        events.Add(new GameSimEventStateTransitioned(ERunState.EndRunDefeat, null));
        events.Add(new GameSimEventRunCompleted(result));
        PublishEndRunRecords(isVictory: false, isConceded: false);
        RecordChallengeRunCompleted(isVictory: false, isConceded: false);
        Console.WriteLine($"[RunEngine] RUN END: DEFEAT (mode={_run.CustomRules.SpecialModeId} prestige={prestige} fatesUsed={_run.FatesUsed} day={day} wins={_run.Victories} chests={result.ChestCount} passXp={result.XpEarned})");
        return true;
    }

    /// <summary>
    /// 终局排行榜写入与全服荣耀播报（严格遵守沙盒三禁隔离：SandboxLab 绝不上榜、不播报）。
    /// </summary>
    private void PublishEndRunRecords(bool isVictory, bool isConceded)
    {
        if (_socialSink == null || _run.IsSandboxRun || _run.CustomRules.ShouldSkipCareerAndLeaderboard)
            return;
        if (string.IsNullOrWhiteSpace(_run.AccountId))
            return;

        try
        {
            var rules = _run.CustomRules;
            double mult = MutatorCatalog.CalculateTotalScoreMultiplier(rules.PresetId, rules.MutatorIds);
            int dayReached = (int)_run.Day;
            int victories = (int)_run.Victories;
            int defeats = (int)_run.Defeats;
            int prestigeRem = Math.Max(0, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0));

            int rawScore = rules.SpecialModeId switch
            {
                ESpecialModeId.EndlessTower => dayReached * 150 + victories * 100 + prestigeRem * 10,
                ESpecialModeId.BossRush => _run.BossRushWavesCleared * 220 + victories * 100 + prestigeRem * 15 + (isVictory ? 500 : 0),
                ESpecialModeId.DailyChallenge => victories * 200 + prestigeRem * 25 + Math.Max(0, 15 - dayReached) * 20 + (isVictory ? 300 : 0),
                _ => victories * 180 + dayReached * 30 + prestigeRem * 15 + (isVictory ? 250 : 0),
            };
            int finalScore = Math.Max(0, (int)Math.Round(rawScore * mult));

            string modeKey = rules.SpecialModeId switch
            {
                ESpecialModeId.EndlessTower => "endless_tower",
                ESpecialModeId.CrossHeroMayhem => "cross_hero",
                ESpecialModeId.MutatorChallenge => "mutator",
                ESpecialModeId.DailyChallenge => "daily",
                ESpecialModeId.BossRush => "boss_rush",
                _ => rules.PresetId == EGameModePreset.PureOfficial ? "standard" : rules.PresetId.ToString().ToLowerInvariant(),
            };
            string seasonOrDateKey = rules.SpecialModeId == ESpecialModeId.DailyChallenge
                ? (!string.IsNullOrWhiteSpace(rules.DailyChallengeDate)
                    ? rules.DailyChallengeDate!
                    : DailyChallengeSeedGenerator.GetTodayDateKeyUtc8())
                : "S1";

            string displayName = string.IsNullOrWhiteSpace(_run.Username) ? _run.AccountId : _run.Username;
            string? mutatorsJson = rules.MutatorIds is { Count: > 0 }
                ? JsonSerializer.Serialize(rules.MutatorIds)
                : null;
            var boardSummary = _run.Cards.Values
                .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item && c.Section != EInventorySection.Stash)
                .OrderBy(c => c.Socket.HasValue ? (int)c.Socket.Value : 99)
                .Select(c => new
                {
                    templateId = RunState.ResolveCoreTemplateId(c),
                    tier = c.Tier.ToString(),
                    enchantment = c.Enchantment?.ToString(),
                })
                .ToList();
            string boardJson = JsonSerializer.Serialize(boardSummary);

            if (!isConceded || victories >= 4 || rules.SpecialModeId == ESpecialModeId.EndlessTower)
            {
                _socialSink.RecordRunLeaderboard(new RunLeaderboardSubmission(
                    ModeKey: modeKey,
                    SeasonOrDateKey: seasonOrDateKey,
                    AccountId: _run.AccountId,
                    Username: displayName,
                    Hero: _run.Hero.ToString(),
                    Score: finalScore,
                    DayReached: dayReached,
                    Victories: victories,
                    Defeats: defeats,
                    ScoreMultiplier: mult,
                    MutatorsJson: mutatorsJson,
                    FinalBoardSummaryJson: boardJson));
            }

            if (isConceded) return;

            string? lobbyId = _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId)?.LobbyId;
            if (isVictory && IsTournamentRun())
            {
                _socialSink.PublishSystemBroadcast(
                    lobbyId,
                    "tournament_champion",
                    $"【锦标赛夺冠】玩家 {displayName}（{_run.Hero}）以 {victories} 胜夺得本场锦标赛冠军！",
                    new { accountId = _run.AccountId, victories, day = dayReached, score = finalScore });
            }
            else if (isVictory && rules.SpecialModeId == ESpecialModeId.BossRush)
            {
                _socialSink.PublishSystemBroadcast(
                    lobbyId: null,
                    "boss_rush_clear",
                    $"【Boss 车轮战】玩家 {displayName}（{_run.Hero}）连克 {_run.BossRushWavesCleared} 波传奇首领通关，得分 {finalScore}！",
                    new { accountId = _run.AccountId, waves = _run.BossRushWavesCleared, score = finalScore });
            }
            else if (isVictory && rules.SpecialModeId == ESpecialModeId.DailyChallenge)
            {
                _socialSink.PublishSystemBroadcast(
                    lobbyId: null,
                    "daily_victory",
                    $"【每日挑战】玩家 {displayName}（{_run.Hero}）通关 {seasonOrDateKey} 每日挑战，斩获 {finalScore} 分（{mult:0.##}x）！",
                    new { accountId = _run.AccountId, date = seasonOrDateKey, score = finalScore, mult });
            }
            else if (isVictory && victories >= 10)
            {
                _socialSink.PublishSystemBroadcast(
                    lobbyId,
                    "ten_win",
                    $"【荣耀十胜】玩家 {displayName}（{_run.Hero}）在第 {dayReached} 天达成 {victories} 胜完美吃鸡！",
                    new { accountId = _run.AccountId, victories, day = dayReached, mode = modeKey });
            }
            else if (rules.SpecialModeId == ESpecialModeId.EndlessTower && dayReached >= 15)
            {
                _socialSink.PublishSystemBroadcast(
                    lobbyId: null,
                    "endless_finish",
                    $"【无尽爬塔】玩家 {displayName}（{_run.Hero}）止步第 {dayReached} 层（{victories} 胜），最终评分 {finalScore}！",
                    new { accountId = _run.AccountId, day = dayReached, victories, score = finalScore });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunEngine] 终局榜单/播报上报异常（已忽略）: {ex.Message}");
        }
    }

    // ── 卖 / 基座 / GM / 放弃 ───────────────────────────────────────────────

    private Outcome OnSell(SellCardCommand cmd)
    {
        if (_runEnded || _run.State is ERunState.Combat or ERunState.PVPCombat
            or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            LogBoardAudit("SellCard", cmd.InstanceId.Value, null, null, null, $"not allowed in state {_run.State}");
            return NotAllowed($"当前状态（{_run.State}）不允许出售卡牌");
        }
        string id = cmd.InstanceId.Value;
        if (!_run.Cards.TryGetValue(id, out var card))
        {
            LogBoardAudit("SellCard", id, null, null, null, "card not found");
            return NotFound("未找到要出售的卡牌：" + id);
        }
        if (card.Owner != ECombatantId.Player)
        {
            LogBoardAudit("SellCard", id, card.TemplateId.ToString(), $"{card.Section}:{card.Socket}", null, "not owned by player");
            return NotAllowed("无法出售非玩家拥有的卡牌：" + id);
        }
        if (IsCardUnsellable(card))
        {
            LogBoardAudit("SellCard", id, card.TemplateId.ToString(), $"{card.Section}:{card.Socket}", null, "unsellable");
            var reject = NotAllowed($"该卡牌不可出售：{id}");
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

        // P0-22：统一调用单一权威卖价函数（含 Value 与 DoubleEconomy），并先到账金币与 Custom_3 再触发卖卡钩子
        int goldBeforeSell = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        int sell = _run.ResolveAuthoritativeSellPrice(card, _repo.GameMode.StandardPrices);
        if (sell > 0)
        {
            _run.AddPlayerGold(sell);
        }

        // 关键顺序：必须在把 card 从 HandGrid/StashGrid 摘除前，携带原始 StartSlot、已到账的 Gold 与货架卡调用 OutOfCombatController.OnCardSoldOrConsumed
        var p1Core = BuildCorePlayerFromRun();
        var selCards = CollectSelectionCoreCards();
        var soldBatch = OutOfCombatController.OnCardSoldOrConsumed(
            p1Core, coreCard, _repo, _rng, removeAfterTrigger: true,
            currentEncounterId: _run.CurrentEncounterId,
            activeEncounterCard: _run.CurrentEncounterCoreCard,
            selectionCards: selCards.Count > 0 ? selCards : null);

        // 先从权威网格与牌库摘除被卖卡（释放 Hand/Stash 槽位），再应用触发器产卡/属性变更增量
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
            // P1-16：卖卡金币增长驱动 TTriggerOnPlayerAttributeChanged（如 Bootstraps / Tea Set 等监听 Gold 变化）
            DispatchPlayerAttributeChanged("Gold", goldBeforeSell, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0), o.Events);
        }
        RecordChallengeForCard("TTriggerOnCardSold", card);

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
            return NotAllowed($"当前状态（{_run.State}）不允许提交祭坛");

        string id = cmd.InstanceId.Value;
        if (string.IsNullOrEmpty(id))
            return NotFound("祭坛目标卡牌实例 ID 不能为空");
        if (!_run.Cards.TryGetValue(id, out var card) || card.Owner != ECombatantId.Player)
            return NotFound("未找到可提交祭坛的玩家卡牌：" + id);
        if (card.Type != ECardType.Item)
            return NotAllowed($"只有物品卡可提交至祭坛，当前类型为 {card.Type}");

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
                return NotAllowed("该卡牌不满足当前祭坛的选择条件");
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
                return NotAllowed("传奇品阶卡牌无法被变形");
            if (!TryApplyTransformPedestal(card, coreCard, behavior, o))
                return NotAllowed("该卡牌无法在此祭坛进行变形");
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
                    return NotAllowed("该卡牌已达最高品阶，无法继续升阶");

                UpgradeCardEntryToTier(card, nextTier, o.DirtyCardIds, o.Events);
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
                var coreTpl = _repo.GetCard(RunState.ResolveCoreTemplateId(card));
                if (!TryPickPedestalEnchantment(behavior, coreTpl, card.Enchantment, out EEnchantmentType chosen, out string? rejectReason))
                    return NotAllowed(rejectReason ?? "该卡牌不支持当前祭坛附魔或已拥有相同附魔");
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
        // P2-16：变形祭坛已删除旧卡并标脏新卡，不得再把已删旧 card.InstanceId 残留写入 o.DirtyCardIds
        if (!isTransformBehavior)
            o.DirtyCardIds.Add(card.InstanceId);
        ReconcileAndVerifyBoardInvariants("Pedestal", o.DirtyCardIds, o.Events);
        if (!isTransformBehavior)
            LogBoardAudit("Pedestal", card.InstanceId, card.TemplateId.ToString(), $"{card.Section}:{card.Socket}", $"{card.Tier}/{card.Enchantment}");
        Guid? nextOnExit = _run.SelectionRules.NextEncounterOnExit;
        bool hasNextEnc = nextOnExit is Guid ng && ng != Guid.Empty && (_pool.IsAssetValid(ng.ToString()) || _repo.GetCard(ng.ToString()) != null);
        if (!hasNextEnc)
        {
            _run.ParentEncounterTemplateId = null;
            ActivateEncounterCard(null, selectedCard: null, o.Events, emitPurchaseIfSelected: false);
        }

        // P2-32：若 Pedestal 存在 NextEncounterOnExit 跳往 EventEncounter / PedestalEncounter，
        // 先定格第一帧祭坛结算 GameSim（PreSplitGameSim），再把下一跳转换事件写入 SecondaryGameSimEvents，
        // 交由 GameServerEndpoints 拆分为两条独立 NetMessageGameSim，彻底规避客户端 PedestalState 吞并跳转并强清 CurrentEncounterId。
        if (hasNextEnc && nextOnExit is Guid chainedNextGuid)
        {
            string chainedNextId = chainedNextGuid.ToString();
            string? nextTypeStr = _pool.Get(chainedNextId)?.Type ?? _repo.GetCard(chainedNextId)?.Type;
            var nextCardType = ShopEngine.ParseCardType(nextTypeStr);
            if (nextCardType is ECardType.EventEncounter or ECardType.PedestalEncounter)
            {
                var preExtraCards = o.ExtraSimCards.Count > 0 ? o.ExtraSimCards : null;
                o.PreSplitGameSim = _run.BuildFullGameSim(o.Events, preExtraCards);
                var chainedOutcome = new Outcome();
                ExitEncounterOrAdvance(chainedOutcome, nextOnExit, exitingRootId, exitingStepId);
                o.SecondaryGameSimEvents = chainedOutcome.Events;
                return o;
            }
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

        var pedestalArchetypeResolver = ResolveOutOfCombatArchetypeResolver();
        var txCtx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, p1Core, p1Core, _rng)
        {
            CurrentDay = (int)_run.Day,
            CurrentHour = (int)_run.Hour,
            CurrentEncounterId = _run.CurrentEncounterId,
            PlayMode = _run.PlayMode.ToString(),
            SelfCard = liveCard,
            TriggerSourceCard = liveCard,
            TriggerTargetCard = liveCard,
            ArchetypeResolver = pedestalArchetypeResolver,
            CustomRules = _run.CustomRules,
        };

        var spawned = SpawnQueryEngine.SpawnCards(
            spawnCtx,
            txCtx,
            defaultCountOverride: 1,
            ownedMaxTiers: null,
            validAssetIds: _pool.WhitelistEnabled ? _pool.GetEffectiveValidAssetIds() : null,
            archetypeResolver: pedestalArchetypeResolver,
            rules: _run.CustomRules);
        var replacement = spawned.FirstOrDefault(c =>
            c.Template.DefaultTier != CardTier.Legendary &&
            !string.Equals(c.Template.Id, liveCard.Template.Id, StringComparison.OrdinalIgnoreCase))
            ?? spawned.FirstOrDefault(c => c.Template.DefaultTier != CardTier.Legendary);
        if (replacement == null)
            return false;

        var oldSec = oldEntry.Section;
        int oldSlot = oldEntry.Socket.HasValue ? (int)oldEntry.Socket.Value : liveCard.StartSlot;
        bool wasInHand = oldSec != EInventorySection.Stash;
        var prevPlayerAttrSnap = new Dictionary<string, double>(p1Core.Attributes, StringComparer.OrdinalIgnoreCase);

        // P2-16：先在 p1Core 中将 liveCard 替换为 replacement，并复用 AbilityExecutor 执行旧卡 Template.Transform.Abilities 与 behavior.Abilities
        p1Core.Hand.Remove(liveCard);
        p1Core.Stash.Remove(liveCard);
        replacement.IsCombatTemporary = false;
        replacement.TransformedFromInstanceId = null;
        if (wasInHand)
        {
            if (!p1Core.Hand.TryPlaceOrShift(replacement, oldSlot))
                p1Core.Hand.TryAddAuto(replacement);
        }
        else
        {
            if (!p1Core.Stash.TryPlaceOrShift(replacement, oldSlot))
                p1Core.Stash.TryAddAuto(replacement);
        }

        var postTransformCtx = txCtx.WithCards(replacement, liveCard, replacement);
        if (liveCard.Template.Transform?.Abilities is { Count: > 0 } transformAbilities)
        {
            foreach (var tAb in transformAbilities.Values)
            {
                if (tAb.Action == null) continue;
                if (!ValueAndConditionEvaluator.EvaluatePrerequisites(tAb.Prerequisites, postTransformCtx))
                    continue;
                AbilityExecutor.ExecuteAction(tAb.Action, replacement, postTransformCtx);
            }
        }

        if (behavior?.Abilities is { Count: > 0 } behaviorAbilities)
        {
            var pedSource = _run.CurrentEncounterCoreCard ?? replacement;
            var behCtx = txCtx.WithCards(pedSource, pedSource, replacement);
            foreach (var bAb in behaviorAbilities.Values)
            {
                if (bAb.Action == null) continue;
                if (!ValueAndConditionEvaluator.EvaluatePrerequisites(bAb.Prerequisites, behCtx))
                    continue;
                AbilityExecutor.ExecuteAction(bAb.Action, pedSource, behCtx);
            }
        }

        // 将 Transform.Abilities / behavior.Abilities 对玩家属性（如 Chunk of Lead 生金、加血/声望/经验等）的增量同步回 RunState
        foreach (var (attrKey, newVal) in p1Core.Attributes)
        {
            double oldVal = prevPlayerAttrSnap.GetValueOrDefault(attrKey, 0.0);
            int delta = (int)Math.Round(newVal - oldVal);
            if (delta == 0) continue;
            if (attrKey.Equals("Experience", StringComparison.OrdinalIgnoreCase) && delta > 0)
            {
                GrantExperienceWithSourceToList(o.Events, delta, EExperienceType.Hour, replacement.InstanceId);
                continue;
            }
            if (attrKey.Equals("Gold", StringComparison.OrdinalIgnoreCase))
            {
                MutatePlayerGold(delta, o.Events, trackCustom3OnGain: true);
                continue;
            }
            if (attrKey.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
            {
                MutatePlayerHealthMax(delta, o.Events);
                continue;
            }
            if (!Enum.TryParse<EPlayerAttributeType>(attrKey, true, out var pa)) continue;
            int cur = _run.PlayerAttributes.GetValueOrDefault(pa, 0);
            int next = pa is EPlayerAttributeType.Prestige or EPlayerAttributeType.Experience ? Math.Max(0, cur + delta) : cur + delta;
            if (next == cur) continue;
            _run.PlayerAttributes[pa] = next;
            if (pa == EPlayerAttributeType.Income && delta > 0)
                o.Events.Add(new GameSimEventPlayerIncomeGained((uint)delta));
            else if (pa == EPlayerAttributeType.Prestige)
                o.Events.Add(new GameSimEventPlayerPrestigeChanged(next - cur));
        }

        _run.TrackReagentTransformIfApplicable(coreCard, oldEntry);
        RemoveOwnedEntryFromRun(oldEntry);
        _run.HandGrid.Remove(liveCard);
        _run.StashGrid.Remove(liveCard);

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

        // P2-16：派发 OutOfCombatController.OnCardTransformed 并重算全局局外光环与盘面同步
        var p1After = BuildCorePlayerFromRun();
        var trBatch = OutOfCombatController.OnCardTransformed(
            p1After,
            liveCard,
            replacement,
            _repo,
            _rng,
            currentEncounterId: _run.CurrentEncounterId,
            activeEncounterCard: _run.CurrentEncounterCoreCard);
        ApplyOutOfCombatBatchWithDirty(p1After, trBatch, o.Events, syncBoardDiffs: false, dirtyCardIds: o.DirtyCardIds);
        RecalculateAurasAndSyncBoard(o.Events, o.DirtyCardIds);
        LogBoardAudit("PedestalTransform", newEntry.InstanceId, newEntry.TemplateId.ToString(), $"{oldSec}:{oldSlot}", $"{newEntry.Section}:{newEntry.Socket}");
        return true;
    }

    /// <summary>
    /// P0-17：校验祭坛附魔合法性（模板 Enchantments 白名单 + 禁止重复应用同附魔），
    /// 合法时选出最终附魔并返回 true，否则返回 false 与明确拒单原因，杜绝非法附魔与静默脱节。
    /// </summary>
    private bool TryPickPedestalEnchantment(
        RuleNode? behavior,
        BazaarCore.Models.CardTemplate? cardTpl,
        EEnchantmentType? currentEnchantment,
        out EEnchantmentType chosen,
        out string? rejectReason)
    {
        chosen = default;
        rejectReason = null;

        HashSet<string>? eligibleKeys = null;
        if (cardTpl != null)
        {
            eligibleKeys = cardTpl.Enchantments?.Keys
                .Where(k => Enum.TryParse<EEnchantmentType>(k, true, out _))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (eligibleKeys.Count == 0)
            {
                rejectReason = "该卡牌不支持任何附魔";
                return false;
            }
        }

        if (behavior != null &&
            string.Equals(behavior.Type, "TPedestalBehaviorEnchant", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(behavior.Enchantment) ||
                !Enum.TryParse<EEnchantmentType>(behavior.Enchantment, true, out var fixedEnch))
            {
                rejectReason = "祭坛未配置有效的定向附魔类型";
                return false;
            }
            if (eligibleKeys != null && !eligibleKeys.Contains(fixedEnch.ToString()))
            {
                rejectReason = $"该卡牌不支持 {fixedEnch} 附魔";
                return false;
            }
            if (currentEnchantment.HasValue && currentEnchantment.Value == fixedEnch)
            {
                rejectReason = $"该卡牌已拥有 {fixedEnch} 附魔，无法重复附魔";
                return false;
            }
            chosen = fixedEnch;
            return true;
        }

        if (behavior != null &&
            string.Equals(behavior.Type, "TPedestalBehaviorEnchantRandom", StringComparison.OrdinalIgnoreCase))
        {
            var rawWeighted = behavior.GetWeightedEnchantments();
            if (rawWeighted.Count > 0)
            {
                var pool = rawWeighted
                    .Where(w => Enum.TryParse<EEnchantmentType>(w.Enchantment, true, out _))
                    .Where(w => eligibleKeys == null || eligibleKeys.Contains(w.Enchantment))
                    .Where(w => !currentEnchantment.HasValue || !w.Enchantment.Equals(currentEnchantment.Value.ToString(), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (pool.Count == 0)
                {
                    rejectReason = "该卡牌没有可应用的新随机附魔";
                    return false;
                }

                double sum = pool.Sum(p => Math.Max(0.0, p.Weight));
                if (sum > 0)
                {
                    double roll = _rng.NextDouble() * sum;
                    double acc = 0;
                    foreach (var p in pool)
                    {
                        acc += Math.Max(0.0, p.Weight);
                        if (roll <= acc && Enum.TryParse<EEnchantmentType>(p.Enchantment, true, out var picked))
                        {
                            chosen = picked;
                            return true;
                        }
                    }
                }
                if (Enum.TryParse<EEnchantmentType>(pool[_rng.Next(pool.Count)].Enchantment, true, out var fb))
                {
                    chosen = fb;
                    return true;
                }
                rejectReason = "随机附魔解析失败";
                return false;
            }
        }

        if (eligibleKeys is { Count: > 0 })
        {
            var list = currentEnchantment.HasValue
                ? eligibleKeys.Where(k => !k.Equals(currentEnchantment.Value.ToString(), StringComparison.OrdinalIgnoreCase)).ToList()
                : eligibleKeys.ToList();
            if (list.Count == 0)
            {
                rejectReason = "该卡牌已拥有全部可用附魔，无法重复附魔";
                return false;
            }
            string picked = list[_rng.Next(list.Count)];
            if (Enum.TryParse<EEnchantmentType>(picked, true, out var res))
            {
                chosen = res;
                return true;
            }
        }

        rejectReason = "该卡牌没有可用的附魔选项";
        return false;
    }

    private Outcome OnCheat(CheatCommand cmd)
    {
        var o = new Outcome();
        var a = cmd.Args ?? new List<string>();
        if (a.Count == 0 || string.IsNullOrWhiteSpace(a[0]))
            return NotAllowed("调试指令不能为空");

        string sub = a[0].ToLowerInvariant();
        if (sub != "restart" && (_runEnded || _run.State is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown))
            return NotAllowed($"对局已结束（{_run.State}），无法执行调试指令 '{sub}'");

        switch (sub)
        {
            case "gold":
                if (a.Count <= 1 || !int.TryParse(a[1], out int g) || g < 0)
                    return NotAllowed("gold 参数无效，请输入非负整数");
                _run.PlayerAttributes[EPlayerAttributeType.Gold] = g;
                break;
            case "day":
                if (a.Count <= 1 || !uint.TryParse(a[1], out uint d) || d < 1 || d > 99)
                    return NotAllowed("day 参数无效，范围应为 1~99");
                _run.Day = d;
                _run.VisitedDayEncounterIds.Clear();
                _run.ClearOfferedDayEncounters();
                _run.PendingPvpGhost = null;
                _run.CurrentPvpOpponent = null;
                o.Events.Add(new GameSimEventRunDayChanged(d));
                break;
            case "hour":
                if (a.Count <= 1 || !uint.TryParse(a[1], out uint h) || h < 1 || h > (uint)Math.Max(1, _mode.HoursInADay))
                    return NotAllowed($"hour 参数无效，范围应为 1~{Math.Max(1, _mode.HoursInADay)}");
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
                    return NotAllowed("prestige 参数无效，范围应为 0~999");
                int oldPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
                _run.PlayerAttributes[EPlayerAttributeType.Prestige] = p;
                o.Events.Add(new GameSimEventPlayerPrestigeChanged(p - oldPrestige));
                break;
            }
            case "win":
            {
                int addWins = 1;
                if (a.Count > 1 && (!int.TryParse(a[1], out addWins) || addWins < 1 || addWins > 20))
                    return NotAllowed("win 参数无效，范围应为 1~20");
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
                    return NotAllowed("exp 参数无效，范围应为 0~10000");
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
                // P1-26：复用 StartRun() 完整清理与开局流程（覆盖 BossRush/Endless/Expedition/BoardOverride/PlayerEffects/OnRunStarted 等全部状态），
                // 并设置 ForceStateSync=true 由 BuildCommandReply 产出 [GameSim, GameStateSync]
                if (_runEnded || _run.State is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
                    return NotAllowed($"对局已结束（{_run.State}），不允许原地重启");

                _run.PlayerAttributes.Clear();
                var restarted = StartRun();
                o.Events.AddRange(restarted.Events);
                foreach (string dirtyId in restarted.DirtyCardIds)
                    o.DirtyCardIds.Add(dirtyId);
                o.ForceStateSync = true;
                break;
            }
            case "sandbox_sync":
            {
                // 仅限沙盒模式执行：零副作用消费沙盒外部修改挂起的事件与全量同步标记，把新卡/数值即时刷给 Unity 客户端
                if (!_run.IsSandboxRun)
                    return NotAllowed("仅沙盒实验室模式允许执行 sandbox_sync 同步指令");

                bool extForce = false;
                _run.ConsumePendingExternalSync(o.Events, ref extForce);
                if (extForce)
                    o.ForceStateSync = true;
                break;
            }
            default:
                return NotAllowed($"未知的调试指令：{sub}");
        }
        return o;
    }

    private Outcome OnAbandon()
    {
        if (_runEnded || _run.State is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            return NotAllowed($"对局已处于终局状态（{_run.State}），无法重复放弃");
        }

        var o = new Outcome();
        _run.State = ERunState.EndRunDefeat;
        _runEnded = true;
        var abandonResult = BuildCompleteRunResult(isVictory: false, isConceded: true);
        o.Events.Add(new GameSimEventStateTransitioned(ERunState.EndRunDefeat, null));
        o.Events.Add(new GameSimEventRunCompleted(abandonResult));
        PublishEndRunRecords(isVictory: false, isConceded: true);
        RecordChallengeRunCompleted(isVictory: false, isConceded: true);
        var tLobby = _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId);
        if (tLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId))
        {
            _lobbies!.RecordRunProgress(tLobby, _run.AccountId, _run);
        }
        return o;
    }

    // ── 局外触发批量回写与状态压栈辅助 ─────────────────────────────────────

    /// <summary>供沙盒实验室等外部合法入口变更盘面后，统一重算局外光环并校准单真值。</summary>
    public void SyncAfterExternalMutation(List<IGameSimEvent>? events = null)
    {
        var ev = events ?? new List<IGameSimEvent>();
        RecalculateAurasAndSyncBoard(ev);
        ReconcileAndVerifyBoardInvariants("ExternalMutation", null, ev);
    }

    /// <summary>收集当前货架 SelectionSet 中全部非激活遭遇卡的核心实例，用于 P0-3 商店折扣/门票光环实时生效。</summary>
    private List<CardInstance> CollectSelectionCoreCards()
    {
        var list = new List<CardInstance>(_run.SelectionSet.Count);
        foreach (string selId in _run.SelectionSet)
        {
            if (string.Equals(selId, _run.ActiveEncounterCardInstanceId, StringComparison.Ordinal))
                continue;
            if (!_run.Cards.TryGetValue(selId, out var entry)) continue;
            if (entry.Owner != null) continue;
            if (entry.Type is not (ECardType.Item or ECardType.Skill)) continue;
            list.Add(EnsureCoreCard(entry));
        }
        return list;
    }

    /// <summary>将原版卡 ID 或 Mod 自定义卡 ID 安全映射为客户端白名单内的合法 Guid。</summary>
    private Guid ResolveSafeWireGuid(string? templateId, ECardType fallbackType)
    {
        string safe = _pool.ResolveSafeWireTemplateId(templateId);
        if (Guid.TryParse(safe, out var g) && g != Guid.Empty)
            return g;
        var fallback = fallbackType == ECardType.Skill
            ? _pool.AllSkills.FirstOrDefault()
            : _pool.AllItems.FirstOrDefault();
        if (fallback != null && Guid.TryParse(_pool.ResolveSafeWireTemplateId(fallback.Id), out var fg) && fg != Guid.Empty)
            return fg;
        return Guid.Empty;
    }

    private void RecalculateAurasAndSyncBoard(List<IGameSimEvent> events, HashSet<string>? dirtyCardIds = null)
    {
        var p1Core = BuildCorePlayerFromRun();
        var selectionCards = CollectSelectionCoreCards();
        var batch = OutOfCombatController.RecalculateOutOfCombatAuras(
            p1Core,
            _repo,
            activeEncounterCard: _run.CurrentEncounterCoreCard,
            selectionCards: selectionCards.Count > 0 ? selectionCards : null);
        ApplyOutOfCombatBatchWithDirty(p1Core, batch, events, syncBoardDiffs: true, dirtyCardIds: dirtyCardIds);
        if (selectionCards.Count > 0)
            SyncShelfEntriesDiff(events, dirtyCardIds);
    }

    private void SyncShelfEntriesDiff(List<IGameSimEvent> events, HashSet<string>? dirtyCardIds = null)
    {
        bool allowZeroShopPrice = _run.SelectionRules.SelectionIsFree
            || (_run.IsSandboxRun && _run.CustomRules.SandboxZeroShopPrice);
        var shelfEntries = new List<RunState.CardEntry>(_run.SelectionSet.Count);
        foreach (string id in _run.SelectionSet)
        {
            if (string.Equals(id, _run.ActiveEncounterCardInstanceId, StringComparison.Ordinal))
                continue;
            if (!_run.Cards.TryGetValue(id, out var entry) || entry.CoreCard == null)
                continue;
            shelfEntries.Add(entry);
        }
        if (shelfEntries.Count == 0) return;

        var diffs = _run.SyncAllEntriesFromCoreCardsAndDiff(
            _repo.GameMode.StandardPrices,
            shelfEntries,
            dirtyCardIds: dirtyCardIds,
            emitSyntheticRefreshEvent: false);
        // 守住非免费商人货架底价：除非本上下文免费、沙盒 0 元商店或明确免单光环把值归零，否则保持非负合法售价
        if (!allowZeroShopPrice)
        {
            foreach (var entry in shelfEntries)
            {
                if (entry.Type is not (ECardType.Item or ECardType.Skill)) continue;
                if (entry.Attributes.TryGetValue(ECardAttributeType.BuyPrice, out int bp) && bp < 0)
                {
                    entry.Attributes[ECardAttributeType.BuyPrice] = 0;
                }
            }
        }
        events.AddRange(diffs);
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
        // P0-1 根治：遭遇离场已发放本小时经验后，同步关闭推进小时重复发经验门控，彻底终结每小时双份经验
        _run.GainedAdvanceHourXpThisHour = true;
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
        // P2-34：统一在所有进入 LevelUp 入口（含非栈式、战后与连升多级）成对发射 InterruptStateEntered；
        // 连升多级（当前已在 LevelUp 态）先发上一级 Exited 再发下一级 Entered，确保中断事件流严格对称。
        if (_run.State == ERunState.LevelUp)
            events.Add(new GameSimEventInterruptStateExited(ERunState.LevelUp, true));
        events.Add(new GameSimEventInterruptStateEntered(ERunState.LevelUp, true));
        int lvl = ResolveNextPendingLevelUpLevel();
        _run.PendingLevelUps = Math.Max(0, _run.PendingLevelUps - 1);
        _run.SelectionsMadeInCurrentContext = 0;
        events.AddRange(_shop.DealLevelUpChoices(lvl, _levelUps, BuildCorePlayerFromRun()));
    }

    private void SettleInterruptedCombatOnResume()
    {
        var originalState = _run.State;
        // P1-35：仅 PvE（Combat）关闭本小时 XP 双门控；PVPCombat 保留未置位，确保战后跨日推进正常获得 +1 小时 XP
        if (originalState == ERunState.Combat)
        {
            _run.GainedHourXpThisHour = true;
            _run.GainedAdvanceHourXpThisHour = true;
        }
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

        if (TryEndRunToEvents(dummy, justFinishedPvp: originalState == ERunState.PVPCombat))
        {
            // P1-35：若断线恢复结算直接进入终局，保存 GameSimEventRunCompleted 供 BuildResumeEvents 下发，并立即标记数据库终局
            _run.PendingResumeRunCompletedEvent = dummy.OfType<GameSimEventRunCompleted>().LastOrDefault();
            try
            {
                string reason = _run.State == ERunState.EndRunVictory ? "victory" : "defeat";
                _db?.MarkRunEnded(_run.RunId, _run.State, reason);
            }
            catch { }
        }
        else
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
            BaseRerollCost = null,
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
            BaseRerollCost = _run.BaseRerollCost,
            RerollsRemaining = _run.RerollsRemaining,
            SelectionsMadeInCurrentContext = _run.SelectionsMadeInCurrentContext,
            CurrentMaxSelections = _run.CurrentMaxSelections,
            PendingEncounterExitXp = _run.PendingEncounterExitXp,
        };
        foreach (var kv in _run.ActiveShelfDiscounts)
            snapshot.ActiveShelfDiscounts[kv.Key] = kv.Value;

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
        _run.PendingEncounterExitXp = 0;

        // P2-34：InterruptStateEntered 统一在 ConsumeNextPendingLevelUpToEvents 内发射，避免重复
        ConsumeNextPendingLevelUpToEvents(events);
        return true;
    }

    private void PushChoiceAndEnterLevelUp(List<IGameSimEvent> events)
    {
        PushHourChoiceSnapshot();
        ConsumeNextPendingLevelUpToEvents(events);
    }

    private void ApplyOutOfCombatBatch(
        PlayerState ps,
        OutOfCombatBatchResult batch,
        List<IGameSimEvent> events,
        string? excludeRemovedInstanceId = null,
        bool syncBoardDiffs = true,
        EExperienceType xpType = EExperienceType.Item)
        => ApplyOutOfCombatBatchWithDirty(ps, batch, events, excludeRemovedInstanceId, syncBoardDiffs, xpType, dirtyCardIds: null);

    private void ApplyOutOfCombatBatchWithDirty(
        PlayerState ps,
        OutOfCombatBatchResult batch,
        List<IGameSimEvent> events,
        string? excludeRemovedInstanceId = null,
        bool syncBoardDiffs = true,
        EExperienceType xpType = EExperienceType.Item,
        HashSet<string>? dirtyCardIds = null)
    {
        SyncTrackedPlayerTempModifiersFromCore(ps);

        int goldBeforeBatch = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        int hpMaxBeforeBatch = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth);

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
            var directLeveledTo = new List<int>(levelsGainedDirectly);
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
                ushort beforeDirectUnlock = _run.UnlockedSlots;
                var unlocked = _run.UnlockCenteredSockets(Math.Max(1, _mode.NumCarpetSocketsUnlockPerLevel));
                _run.HandSocketUnlockMask = _run.UnlockedSlots;
                if (unlocked.Count > 0)
                {
                    _run.LevelUpEntryUnlockedSlots ??= beforeDirectUnlock;
                    events.Add(new GameSimEventSocketsUnlocked(unlocked, _run.UnlockedSlots, EInventorySection.Hand));
                }
                directLeveledTo.Add(curLvl);
            }
            _run.PlayerAttributes[EPlayerAttributeType.Level] = curLvl;

            // P2-34：直接 +Level 分支同步按升阶顺序逐级派发 OutOfCombatController.OnPlayerLeveledUp
            foreach (int reachedLvl in directLeveledTo)
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
                ApplyOutOfCombatBatchWithDirty(p1Lvl, lvlBatch, events, syncBoardDiffs: true, xpType: xpType, dirtyCardIds: dirtyCardIds);
                RecordChallengeLevelUp(reachedLvl);
            }
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

        // 2b. P0-23：识别同槽位覆盖旧 SocketEffect（如音符覆盖灶台/冰格）：
        //     客户端 GameSimHandler 固定先执行 CardSpawned 后执行 CardDisposed，同包发 Disposed+Spawned 会导致占位拒放；
        //     改发 GameSimEventCardTransformed（先 TryRemoveFromContainer 摘除旧卡再 TryAddToContainer 放新卡）彻底消除冲突。
        var removedSet = new HashSet<CardInstance>(batch.RemovedCards);
        var registeredInBatch = new HashSet<CardInstance>();
        var spawnedSocketEffectPool = new List<(CardInstance Card, int Slot)>();
        foreach (var sp in batch.SpawnedCards)
        {
            if (sp.Card != null &&
                ShopEngine.ParseCardType(sp.Card.Template.Type) == ECardType.SocketEffect &&
                (string.IsNullOrEmpty(excludeRemovedInstanceId) || !string.Equals(sp.Card.InstanceId, excludeRemovedInstanceId, StringComparison.Ordinal)) &&
                FindEntryByCoreCard(sp.Card) == null)
            {
                int slot = sp.StartSlot is >= 0 and < BoardGrid.Capacity ? sp.StartSlot : sp.Card.StartSlot;
                if (slot is >= 0 and < BoardGrid.Capacity)
                    spawnedSocketEffectPool.Add((sp.Card, slot));
            }
        }
        foreach (var se in ps.SocketEffects)
        {
            if (se != null &&
                !handledOldCards.Contains(se) &&
                !removedSet.Contains(se) &&
                FindEntryByCoreCard(se) == null &&
                se.StartSlot is >= 0 and < BoardGrid.Capacity &&
                !spawnedSocketEffectPool.Any(x => ReferenceEquals(x.Card, se)))
            {
                spawnedSocketEffectPool.Add((se, se.StartSlot));
            }
        }

        // 3. 同步其他被规则销毁的卡牌（跳过卖卡自身与已变形卡；同槽位覆盖的 SocketEffect 合并为 CardTransformed）
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

            if (remEntry.Type == ECardType.SocketEffect && remEntry.Socket.HasValue)
            {
                int remSlot = (int)remEntry.Socket.Value;
                int matchIdx = spawnedSocketEffectPool.FindIndex(x => x.Slot == remSlot && !registeredInBatch.Contains(x.Card));
                if (matchIdx >= 0)
                {
                    var (newSeCore, targetSlot) = spawnedSocketEffectPool[matchIdx];
                    spawnedSocketEffectPool.RemoveAt(matchIdx);
                    handledOldCards.Add(rem);
                    registeredInBatch.Add(newSeCore);

                    string oldSeId = remEntry.InstanceId;
                    RemoveOwnedEntryFromRun(remEntry);
                    var newSeEntry = RegisterSpawnedCoreCard(newSeCore, EInventorySection.Hand, targetSlot, emitSpawnEvent: false, events);
                    events.Add(new GameSimEventCardTransformed(
                        "skt_ovr_" + oldSeId,
                        oldSeId,
                        new List<SimEventCardTransformation>
                        {
                            new(
                                newSeEntry.InstanceId,
                                newSeEntry.TemplateId.ToString(),
                                newSeEntry.Type,
                                ECombatantId.Player,
                                newSeEntry.Section,
                                newSeEntry.Socket)
                        }));
                    boardMutated = true;
                    continue;
                }
            }

            RemoveOwnedEntryFromRun(remEntry);
            events.Add(new GameSimEventCardDisposed(remEntry.InstanceId, ECardDisposalReason.Cleared));
            boardMutated = true;
        }

        // 4. 同步衍生生成的新卡牌（GameSimEventCardSpawned + 技能装备事件），并完整比对 ps.Skills / Hand / Stash / SocketEffects 防止遗漏
        foreach (var sp in batch.SpawnedCards)
        {
            if (registeredInBatch.Contains(sp.Card)) continue;
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

        // 5. P1-09 & P1-18 & P1-39：捕获精准光环目标快照并比对发射 GameSimEventEffectAuraExecuted（含真实 Source/AppliedTo/RemovedFrom），
        //    同时同步光环/限时修饰层玩家属性（含 RerollCostModifier）并刷新 RerollCost 与全盘/Delta DirtyCardIds
        if (syncBoardDiffs)
        {
            // 先将批次中经增量写入的最新 Gold / Custom_3 / Custom_9 同步至 ps，确保 Cash Cannon / Oinkment / Calcinator 等光环读取权威值
            ps.Attributes["Gold"] = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
            ps.Attributes["Custom_3"] = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0);
            ps.Attributes["Custom_9"] = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_9, 0);

            IReadOnlyList<AuraEngine.AuraEffectTargetsSnapshot>? auraSnapshots = null;
            var selCoreCards = CollectSelectionCoreCards();
            AuraEngine.RecalculateAuras(
                ps,
                p2: null,
                scope: RuleScope.OutOfCombat,
                day: (int)_run.Day,
                hour: (int)_run.Hour,
                repo: _repo,
                selectionCards: selCoreCards.Count > 0 ? selCoreCards : null,
                onAuraSnapshot: snaps => auraSnapshots = snaps);

            SyncTrackedPlayerTempModifiersFromCore(ps);
            SyncLayeredPlayerAttributesFromCore(ps);
            // P2-14：统一走 RefreshEffectiveRerollCost(events) 自动发射 RerollCostChanged
            _shop.RefreshEffectiveRerollCost(_run.State == ERunState.Encounter ? events : null);

            HashSet<string>? realAuraCoveredIds = null;
            if (auraSnapshots != null)
            {
                realAuraCoveredIds = _run.EmitOutOfCombatAuraDiffs(auraSnapshots, events, dirtyCardIds: dirtyCardIds);
            }
            events.AddRange(_run.SyncAllEntriesFromCoreCardsAndDiff(
                _repo.GameMode.StandardPrices,
                dirtyCardIds: dirtyCardIds,
                emitSyntheticRefreshEvent: true,
                alreadyCoveredByRealAuraIds: realAuraCoveredIds));
        }
        else
        {
            SyncLayeredPlayerAttributesFromCore(ps);
            _shop.RefreshEffectiveRerollCost(_run.State == ERunState.Encounter ? events : null);
        }

        // P2-07：局外技能/物品直接修改单卡或玩家非 PvP 声望且未附带其他光环刷新/PlayerPrestigeChanged 时，按需补发 GameSimEventEffectExecuted（不填客户端 0 消费的 GameSim.VfxKeys）
        EmitOutOfCombatEffectExecutedIfNeeded(batch, events);

        // 6. P1-16：若批次本身改动了玩家 Gold 或 HealthMax，在重入守卫保护下补发 TTriggerOnPlayerAttributeChanged
        int goldAfterBatch = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        if (goldAfterBatch != goldBeforeBatch)
            DispatchPlayerAttributeChanged("Gold", goldBeforeBatch, goldAfterBatch, events);
        int hpMaxAfterBatch = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth);
        if (hpMaxAfterBatch != hpMaxBeforeBatch)
            DispatchPlayerAttributeChanged("HealthMax", hpMaxBeforeBatch, hpMaxAfterBatch, events);

        // 7. P1-17：消费 OutOfCombatBatchResult 中的 RerollRequests / DealtSelectionRequests / ExitSelectionRequested
        ConsumeOutOfCombatBatchRequests(batch, events);
    }

    private void ApplyPostCombatMechanicOps(
        PlayerState p1Core,
        IReadOnlyList<BazaarCore.Mods.PostCombatMutationOp>? ops,
        List<IGameSimEvent> events)
    {
        if (ops == null || ops.Count == 0) return;

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op == null || string.IsNullOrWhiteSpace(op.CardInstanceId)) continue;
            if (!_run.Cards.TryGetValue(op.CardInstanceId, out var entry) || entry.Owner != ECombatantId.Player)
                continue;

            var coreCard = p1Core.Hand.Cards.FirstOrDefault(c => c.InstanceId == entry.InstanceId)
                ?? p1Core.Stash.Cards.FirstOrDefault(c => c.InstanceId == entry.InstanceId)
                ?? p1Core.Skills.FirstOrDefault(c => c.InstanceId == entry.InstanceId);

            if (string.Equals(op.OpKind, "RemoveCard", StringComparison.OrdinalIgnoreCase))
            {
                RemoveOwnedEntryFromRun(entry);
                if (coreCard != null)
                {
                    p1Core.Hand.Remove(coreCard);
                    p1Core.Stash.Remove(coreCard);
                    p1Core.Skills.Remove(coreCard);
                    _run.HandGrid.Remove(coreCard);
                    _run.StashGrid.Remove(coreCard);
                }
                events.Add(new GameSimEventCardDisposed(entry.InstanceId, ECardDisposalReason.Destroyed));
                LogBoardAudit("MechanicPostCombatRemove", entry.InstanceId, entry.TemplateId.ToString(), $"{entry.Section}:{entry.Socket}", op.Reason ?? op.SourceMechanicId ?? "mechanic");
            }
            else if (string.Equals(op.OpKind, "ModifyCardPermanentAttribute", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(op.Attribute)
                     && coreCard != null)
            {
                double safeVal = double.IsFinite(op.Value) ? Math.Clamp(op.Value, -100000, 100000) : 0;
                if (op.Attribute.Equals("CooldownMax", StringComparison.OrdinalIgnoreCase))
                {
                    double curCd = coreCard.GetAttribute("CooldownMax");
                    double targetCd = (op.Operation ?? "Add").ToLowerInvariant() switch
                    {
                        "subtract" => curCd - safeVal,
                        "multiply" => curCd * Math.Clamp(safeVal, 0.1, 10),
                        "set" => safeVal,
                        _ => curCd + safeVal
                    };
                    targetCd = Math.Clamp(targetCd, 1000.0, 60000.0);
                    coreCard.ModifyAttribute("CooldownMax", "Add", targetCd - curCd, DurationTarget.Permanent);
                }
                else
                {
                    coreCard.ModifyAttribute(op.Attribute, op.Operation ?? "Add", safeVal, DurationTarget.Permanent);
                }
                if (Enum.TryParse<ECardAttributeType>(op.Attribute, true, out var attrEnum))
                {
                    entry.Attributes[attrEnum] = (int)Math.Round(coreCard.GetAttribute(op.Attribute));
                }
            }
        }
    }

    /// <summary>
    /// P0-03（服务端收口）：允许跨战持久化回写到 RunState.PlayerAttributes 的属性白名单。
    /// 仅放行 Gold / Income / HealthMax / Experience / Prestige / Custom_* 以及由 Core 层明确输出的永久 HealthRegen，
    /// 严禁瞬态战斗属性（Enraged / EnragedDuration / Shield / Burn / Poison / Rage / Tempo / Health 等）跨战泄漏。
    /// </summary>
    public static bool IsPersistentPostCombatPlayerAttribute(EPlayerAttributeType pa, string attrName)
    {
        if (pa is EPlayerAttributeType.Gold
            or EPlayerAttributeType.Income
            or EPlayerAttributeType.HealthMax
            or EPlayerAttributeType.Experience
            or EPlayerAttributeType.Prestige
            or EPlayerAttributeType.HealthRegen)
        {
            return true;
        }
        return !string.IsNullOrEmpty(attrName)
            && attrName.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);
    }

    public void ApplyPostCombatPermanentSync(
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

        // 3. 战斗内永久获得的玩家属性增量（强制跨战持久属性白名单，拦截瞬态 Enraged/EnragedDuration 等泄漏）
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
            if (!IsPersistentPostCombatPlayerAttribute(pa, attrName)) continue;

            int oldVal = _run.PlayerAttributes.GetValueOrDefault(pa, 0);
            int nextVal = oldVal + intDiff;
            if (pa is EPlayerAttributeType.Gold or EPlayerAttributeType.Prestige or EPlayerAttributeType.Experience or EPlayerAttributeType.HealthRegen)
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
        RunState.CardEntry? overwrittenSocketEffectEntry = null;
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
            // P0-7 & P0-23：SocketEffect 挂在 Owner=Player, Section=Hand 下，严格分配已解锁槽位 (0..9)，不写入 HandGrid/StashGrid；
            // 若目标槽位已存在旧 SocketEffect，稍后以 GameSimEventCardTransformed 替代 CardSpawned 避免客户端占位拒放。
            _run.HandGrid.UnlockedMask = _run.UnlockedSlots;
            actualSection = EInventorySection.Hand;
            int slot = (preferredSlot is >= 0 and < BoardGrid.Capacity && _run.HandGrid.IsSlotUnlocked(preferredSlot))
                ? preferredSlot
                : (core.StartSlot is >= 0 and < BoardGrid.Capacity && _run.HandGrid.IsSlotUnlocked(core.StartSlot)
                    ? core.StartSlot
                    : FindFreeSocketEffectSlot());
            if (slot >= 0)
            {
                core.StartSlot = slot;
                actualSocket = (EContainerSocketId)slot;
                overwrittenSocketEffectEntry = _run.Cards.Values.FirstOrDefault(c =>
                    c.Owner == ECombatantId.Player &&
                    c.Type == ECardType.SocketEffect &&
                    c.Socket == actualSocket &&
                    !string.Equals(c.InstanceId, instId, StringComparison.Ordinal));
                if (overwrittenSocketEffectEntry != null)
                {
                    RemoveOwnedEntryFromRun(overwrittenSocketEffectEntry);
                }
            }
            else
            {
                core.StartSlot = -1;
                actualSocket = null;
            }
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

        Guid wireGuid = ResolveSafeWireGuid(core.Template.Id, cardType);
        string? customCardId = !string.Equals(core.Template.Id, wireGuid.ToString(), StringComparison.OrdinalIgnoreCase)
            ? core.Template.Id
            : null;
        if (customCardId != null)
            _run.VisualBindingsRevision++;

        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = wireGuid,
            CustomCardId = customCardId,
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
            var (buy, _) = ShopEngine.PriceFor(baseSize, entry.Tier, _repo.GameMode.StandardPrices);
            if (!entry.Attributes.ContainsKey(ECardAttributeType.BuyPrice))
                entry.Attributes[ECardAttributeType.BuyPrice] = buy;
            entry.Attributes[ECardAttributeType.SellPrice] = _run.ResolveAuthoritativeSellPrice(entry, _repo.GameMode.StandardPrices);
        }

        _run.Cards[instId] = entry;
        foreach (var mv in displacedNeighbors)
        {
            events.Add(new GameSimEventCardMoved(mv.Entry.InstanceId, ECombatantId.Player, mv.Section, mv.NewSocket));
        }
        if (emitSpawnEvent)
        {
            if (cardType == ECardType.SocketEffect && overwrittenSocketEffectEntry != null)
            {
                events.Add(new GameSimEventCardTransformed(
                    "skt_ovr_" + overwrittenSocketEffectEntry.InstanceId,
                    overwrittenSocketEffectEntry.InstanceId,
                    new List<SimEventCardTransformation>
                    {
                        new(
                            entry.InstanceId,
                            entry.TemplateId.ToString(),
                            entry.Type,
                            ECombatantId.Player,
                            entry.Section,
                            entry.Socket)
                    }));
            }
            else if (cardType != ECardType.SocketEffect || entry.Socket.HasValue)
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
        }

        return entry;
    }

    private RunState.CardEntry? FindEntryByCoreCard(CardInstance core)
        => _run.Cards.Values.FirstOrDefault(e => ReferenceEquals(e.CoreCard, core) || e.InstanceId == core.InstanceId);

    /// <summary>
    /// P0-23：在玩家已解锁的手牌槽位中查找首个空闲 SocketEffect 槽位，严格过滤 <c>_run.HandGrid.IsSlotUnlocked(i)</c>，
    /// 彻底避免 Day 1-2 落入锁定槽 Socket_0 遭客户端静默拒放。
    /// </summary>
    public int FindFreeSocketEffectSlot()
    {
        _run.HandGrid.UnlockedMask = _run.UnlockedSlots;
        var used = new HashSet<int>();
        foreach (var c in _run.Cards.Values)
        {
            if (c.Owner == ECombatantId.Player && c.Type == ECardType.SocketEffect && c.Socket.HasValue)
                used.Add((int)c.Socket.Value);
        }
        for (int i = 0; i < BoardGrid.Capacity; i++)
        {
            if (_run.HandGrid.IsSlotUnlocked(i) && !used.Contains(i))
                return i;
        }
        // 全部已解锁槽均已被占用时，退回首个已解锁槽位（调用方将按同槽覆盖转为 CardTransformed），绝不落入锁定槽
        for (int i = 0; i < BoardGrid.Capacity; i++)
        {
            if (_run.HandGrid.IsSlotUnlocked(i))
                return i;
        }
        return -1;
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
            // P1-18 & P0-03：同步 _run.PlayerAttributes 已有键、携带 Aura/Hour/Day 修饰层的键（如 RerollCostModifier），
            // 以及跨战/局外持久化白名单键（含 Custom_0..Custom_15 计数器，避免漏同步 PlayerEffect 写入的 Custom_9 等）；
            // 严禁把 PlayerState 构造自带的战斗瞬态默认属性（如 EnragedDurationMax=5000）反向泄漏回 _run.PlayerAttributes。
            bool hasModifierLayer = ps.AuraAdd.ContainsKey(key)
                || ps.AuraMult.ContainsKey(key)
                || ps.HourAdd.ContainsKey(key)
                || ps.HourMult.ContainsKey(key)
                || ps.DayAdd.ContainsKey(key)
                || ps.DayMult.ContainsKey(key);
            bool isPersistentWhitelistWithValue = IsPersistentSyncPlayerAttribute(pa, key)
                && ps.Attributes.TryGetValue(key, out double rawVal)
                && Math.Abs(rawVal) > 1e-6;
            if (!_run.PlayerAttributes.ContainsKey(pa) && !hasModifierLayer && !isPersistentWhitelistWithValue) continue;
            int eff = (int)Math.Round(ps.GetAttribute(key));
            if (_run.PlayerAttributes.GetValueOrDefault(pa, 0) != eff)
                _run.PlayerAttributes[pa] = eff;
        }
    }

    private static bool IsPersistentSyncPlayerAttribute(EPlayerAttributeType pa, string key)
        => pa is EPlayerAttributeType.Gold
            or EPlayerAttributeType.Income
            or EPlayerAttributeType.HealthMax
            or EPlayerAttributeType.Prestige
            or EPlayerAttributeType.HealthRegen
            or EPlayerAttributeType.RerollCostModifier
        || key.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 仅回写带有 Aura/Hour/Day 修饰层的玩家属性及 RerollCostModifier（用于 ApplyOutOfCombatBatch 收尾），
    /// 绝不用旧 ps.Attributes 覆盖批次中由外部/TrackReagentTransformIfApplicable 增量写入的 Custom_9/Custom_3/Gold 等基础值。
    /// </summary>
    private void SyncLayeredPlayerAttributesFromCore(PlayerState ps)
    {
        foreach (EPlayerAttributeType pa in Enum.GetValues<EPlayerAttributeType>())
        {
            if (pa is EPlayerAttributeType.Experience or EPlayerAttributeType.Level) continue;
            string key = pa.ToString();
            bool hasModifierLayer = ps.AuraAdd.ContainsKey(key)
                || ps.AuraMult.ContainsKey(key)
                || ps.HourAdd.ContainsKey(key)
                || ps.HourMult.ContainsKey(key)
                || ps.DayAdd.ContainsKey(key)
                || ps.DayMult.ContainsKey(key);
            if (!hasModifierLayer && pa != EPlayerAttributeType.RerollCostModifier) continue;
            if (!hasModifierLayer && !_run.PlayerAttributes.ContainsKey(pa) && !ps.Attributes.ContainsKey(key)) continue;
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
            core.CombatZeroMultCount.Clear();
            core.CombatTags.Clear();
            core.CombatCustomFlags.Clear();
            core.CombatCustomCounters.Clear();
            core.TransformedFromInstanceId = null;
        }

        // 先清除 Combat 临时状态并重算局外光环，确保固有/光环飞行为 1、纯战斗临时状态为 0，弹药恢复至 EffectiveAmmoMax
        RecalculateAurasAndSyncBoard(events);

        // V14.2 问题 4 通用根治：战斗回放可能由原版卡或任意自定义机制临时修改伤害/护盾/冷却/Custom_* 等属性，
        // 战后服务端虽已清除 CombatAdd/CombatMult，但客户端本地卡牌内存仍残留战中最终值；
        // 因此战后首帧必须将所有核心战斗属性强刷回服务端权威局外基线，杜绝“本战临时增益看似永久”的视觉残留。
        ECardAttributeType[] postCombatResetAttrs =
        [
            ECardAttributeType.DamageAmount,
            ECardAttributeType.ShieldApplyAmount,
            ECardAttributeType.BurnApplyAmount,
            ECardAttributeType.PoisonApplyAmount,
            ECardAttributeType.HealAmount,
            ECardAttributeType.RegenApplyAmount,
            ECardAttributeType.CooldownMax,
            ECardAttributeType.HasteAmount,
            ECardAttributeType.SlowAmount,
            ECardAttributeType.FreezeAmount,
            ECardAttributeType.ChargeAmount,
            ECardAttributeType.CritChance,
            ECardAttributeType.Multicast,
            ECardAttributeType.Lifesteal,
            ECardAttributeType.Counter,
            ECardAttributeType.SellPrice,
            ECardAttributeType.Custom_0,
            ECardAttributeType.Custom_1,
            ECardAttributeType.Custom_2,
            ECardAttributeType.Custom_3,
            ECardAttributeType.Custom_4,
            ECardAttributeType.Custom_5,
            ECardAttributeType.Custom_6,
            ECardAttributeType.Custom_7,
            ECardAttributeType.Custom_8,
        ];

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

            // 在战后首帧 NetMessageGameSim.Data.Cards 显式下发这些属性的归零/弹药补满/局外基线还原 SimUpdateCard
            entry.PendingForceSyncAttributes[ECardAttributeType.Freeze] = 0;
            entry.PendingForceSyncAttributes[ECardAttributeType.Slow] = 0;
            entry.PendingForceSyncAttributes[ECardAttributeType.Haste] = 0;
            entry.PendingForceSyncAttributes[ECardAttributeType.Flying] = outFlying;
            entry.PendingForceSyncAttributes[ECardAttributeType.Heated] = outHeated;
            entry.PendingForceSyncAttributes[ECardAttributeType.Chilled] = outChilled;
            if (ammoMax > 0 || entry.Attributes.ContainsKey(ECardAttributeType.Ammo))
                entry.PendingForceSyncAttributes[ECardAttributeType.Ammo] = ammoMax;

            foreach (var attrType in postCombatResetAttrs)
            {
                if (core != null)
                {
                    string attrName = attrType.ToString();
                    bool existsOnCard = entry.Attributes.ContainsKey(attrType)
                        || core.BaseAttributes.ContainsKey(attrName)
                        || core.PermanentAdd.ContainsKey(attrName)
                        || core.HourAdd.ContainsKey(attrName)
                        || core.DayAdd.ContainsKey(attrName)
                        || core.AuraAdd.ContainsKey(attrName);
                    if (!existsOnCard) continue;

                    int restored = attrType == ECardAttributeType.SellPrice
                        ? _run.ResolveAuthoritativeSellPrice(entry, _repo.GameMode.StandardPrices)
                        : (int)Math.Round(core.GetAttribute(attrName));
                    entry.Attributes[attrType] = restored;
                    entry.PendingForceSyncAttributes[attrType] = restored;
                }
                else if (entry.Attributes.TryGetValue(attrType, out int existingVal))
                {
                    entry.PendingForceSyncAttributes[attrType] = existingVal;
                }
            }
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
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers,
        int? spawnCountOverride = null)
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
            applySyntheticDiscounts: !skipSyntheticDiscounts,
            spawnCountOverride: spawnCountOverride);
        RestoreActiveEncounterCardAfterShelfOp(active);
        return evs;
    }

    private List<IGameSimEvent> DealHourEncountersAndClearActive(int? spawnCountOverride = null)
    {
        _run.ActiveEncounterCardInstanceId = null;
        _run.ParentEncounterTemplateId = null;
        var events = _shop.DealHourEncounters(
            BuildCorePlayerFromRun(),
            _run.VisitedDayEncounterIds,
            _shop.ActiveRules,
            _shop.ArchetypeIndex,
            spawnCountOverride: spawnCountOverride);
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
        string tplId = RunState.ResolveCoreTemplateId(card);
        return HasFlagAttributeOrTag(card, _repo.GetCard(tplId), _pool.Get(tplId), "Unsellable");
    }

    private bool HasCardAutoSellOnExitFlag(RunState.CardEntry card)
    {
        string tplId = RunState.ResolveCoreTemplateId(card);
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
        var selCards = CollectSelectionCoreCards();
        foreach (var entry in candidates)
        {
            var coreCard = EnsureCoreCard(entry);
            // P0-22：统一调用单一权威卖价函数（含 Value 与 DoubleEconomy），先到账金币再触发卖卡钩子
            int sell = _run.ResolveAuthoritativeSellPrice(entry, _repo.GameMode.StandardPrices);
            if (sell > 0)
            {
                _run.AddPlayerGold(sell);
                autoSoldGold += sell;
            }

            // P1-14 修复：自动卖出离场前正式派发 TTriggerOnCardSold（让卖卡产衍生/加属性/减价等触发器生效）
            var p1AutoSell = BuildCorePlayerFromRun();
            var soldBatch = OutOfCombatController.OnCardSoldOrConsumed(
                p1AutoSell,
                coreCard,
                _repo,
                _rng,
                removeAfterTrigger: entry.Owner == ECombatantId.Player,
                currentEncounterId: _run.CurrentEncounterId,
                activeEncounterCard: _run.CurrentEncounterCoreCard,
                selectionCards: selCards.Count > 0 ? selCards : null);

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

            ApplyOutOfCombatBatch(
                p1AutoSell,
                soldBatch,
                events,
                excludeRemovedInstanceId: entry.InstanceId,
                syncBoardDiffs: false,
                xpType: EExperienceType.Item);

            events.Add(new GameSimEventCardSold(entry.InstanceId, (uint)Math.Max(0, sell)));
            events.Add(new GameSimEventCardDisposed(entry.InstanceId, ECardDisposalReason.Sold));
        }

        if (autoSoldGold > 0)
        {
            LogBoardAudit("WillAutoSellOnExit", _run.CurrentEncounterId, _run.CurrentEncounterId, "Shelf", $"AutoSold(+{autoSoldGold}g)");
        }
        RecalculateAurasAndSyncBoard(events);
        if (removedOwned)
        {
            ReconcileAndVerifyBoardInvariants("WillAutoSellOnExit", null, events);
        }
    }

    private LockedPvpBoard BuildLockedPvpBoardSnapshot()
    {
        string displayName = string.IsNullOrWhiteSpace(_run.Username) ? _run.AccountId : _run.Username;
        string heroName = (_run.Hero == EHero.Common ? EHero.Vanessa : _run.Hero).ToString();
        return new LockedPvpBoard(
            AccountId: _run.AccountId,
            Username: displayName,
            Hero: heroName,
            Day: _run.Day,
            HealthMax: Math.Max(100, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250)),
            Prestige: Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20)),
            Level: Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1)),
            Victories: _run.Victories,
            Gold: Math.Max(0, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0)),
            Income: Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, 5)),
            RunSnapshotJson: RunSnapshotSerializer.Serialize(_run));
    }

    private PlayerState BuildOpponentPlayerStateFromLockedBoard(LockedPvpBoard board)
    {
        try
        {
            if (RunSnapshotSerializer.TryDeserialize(board.RunSnapshotJson, _repo, out var restoredRun, out _) &&
                restoredRun != null)
            {
                return BuildOpponentPlayerStateFromRun(restoredRun, board.Username);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunEngine] 反序列化锁定盘面失败，降级为空白保底盘面: {ex.Message}");
        }

        var fallback = new PlayerState(
            "pvp_" + board.AccountId,
            board.Username,
            string.IsNullOrWhiteSpace(board.Hero) ? "Vanessa" : board.Hero,
            Math.Max(100, board.HealthMax),
            Math.Max(1, board.Prestige),
            Math.Max(0, board.Gold),
            Math.Max(1, board.Income))
        {
            Level = Math.Max(1, board.Level),
            Wins = (int)board.Victories,
        };
        fallback.Hand.UnlockedMask = 1023;
        var fbItem = _repo.AlwaysSpawnableItems.FirstOrDefault() ?? _repo.CardsById.Values.FirstOrDefault(c => c.Type == "Item");
        if (fbItem != null)
            fallback.Hand.TryAddAuto(new CardInstance(fbItem, CardTier.Bronze, null, "pvp_locked_fb_0"));
        fallback.PrepareForCombat();
        return fallback;
    }

    private void ApplyBossRushWaveReinforcements(PlayerState bossPlayer, BossRushWaveConfig waveCfg, int seed)
    {
        bool mutated = false;
        if (waveCfg.MinimumEquipmentTier > CardTier.Bronze)
        {
            foreach (var item in bossPlayer.Hand.Cards)
            {
                if (item.Tier < waveCfg.MinimumEquipmentTier)
                {
                    var clamped = SpawnQueryEngine.ClampToSupportedTier(item.Template, waveCfg.MinimumEquipmentTier);
                    if (clamped > item.Tier)
                    {
                        item.SetTier(clamped, isTemporary: true);
                        mutated = true;
                    }
                }
            }
        }

        if (waveCfg.BonusReinforceSkills > 0)
        {
            var rng = new Random(seed ^ 0x51A7F3);
            var existingIds = new HashSet<string>(bossPlayer.Skills.Select(s => s.Template.Id), StringComparer.OrdinalIgnoreCase);
            var skillCandidates = _repo.CardsById.Values
                .Where(c => string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase) &&
                            !existingIds.Contains(c.Id) &&
                            !c.InternalName.Contains("TEST", StringComparison.OrdinalIgnoreCase))
                .ToList();
            for (int i = 0; i < waveCfg.BonusReinforceSkills && skillCandidates.Count > 0; i++)
            {
                int idx = rng.Next(skillCandidates.Count);
                var skTpl = skillCandidates[idx];
                skillCandidates.RemoveAt(idx);
                var tier = SpawnQueryEngine.ClampToSupportedTier(skTpl, waveCfg.MinimumEquipmentTier);
                bossPlayer.Skills.Add(new CardInstance(skTpl, tier, null, $"boss_sk_{waveCfg.Wave}_{i}"));
                mutated = true;
            }
        }

        if (mutated)
        {
            bossPlayer.PrepareForCombat();
        }
    }

    private void SaveCombatReplayIfEligible(
        bool isPvp,
        bool win,
        int combatSeed,
        CombatResult result,
        PlayerState p1Core,
        PlayerState p2Core,
        SimPvpOpponent? pvpInfo,
        string? lobbyId,
        CombatSim? combatSim)
    {
        if (_socialSink == null || _run.IsSandboxRun || string.IsNullOrWhiteSpace(_run.AccountId))
            return;

        try
        {
            string combatTypeTag = _run.CustomRules.SpecialModeId switch
            {
                ESpecialModeId.BossRush when !isPvp => "BOSS_RUSH",
                ESpecialModeId.EndlessTower => isPvp ? "ENDLESS_PVP" : "ENDLESS_PVE",
                ESpecialModeId.DailyChallenge => isPvp ? "DAILY_PVP" : "DAILY_PVE",
                _ => isPvp ? "PVP" : "PVE",
            };
            string replayId = $"rep_{_run.RunId}_d{_run.Day}h{_run.Hour}_{Guid.NewGuid():N}"[..Math.Min(48, $"rep_{_run.RunId}_d{_run.Day}h{_run.Hour}_".Length + 12)];
            string summaryJson = JsonSerializer.Serialize(new
            {
                mode = _run.CustomRules.SpecialModeId.ToString(),
                preset = _run.CustomRules.PresetId.ToString(),
                mutators = _run.CustomRules.MutatorIds,
                day = _run.Day,
                hour = _run.Hour,
                playerMaxHp = p1Core.HealthMax,
                playerRemHp = result.Player1RemainingHp,
                opponentMaxHp = p2Core.HealthMax,
                opponentRemHp = result.Player2RemainingHp,
                playerItems = p1Core.Hand.Cards.Select(c => new { id = c.Template.Id, tier = c.Tier.ToString(), ench = c.Enchantment }).ToList(),
                opponentItems = p2Core.Hand.Cards.Select(c => new { id = c.Template.Id, tier = c.Tier.ToString(), ench = c.Enchantment }).ToList(),
            });
            byte[]? simPayload = combatSim != null ? Codec.WriteMessage(new NetMessageCombatSim(combatSim)) : null;
            string displayName = string.IsNullOrWhiteSpace(_run.Username) ? _run.AccountId : _run.Username;
            string oppName = pvpInfo?.Name ?? p2Core.DisplayName ?? "未知对手";
            string oppHero = (pvpInfo?.Hero ?? EHero.Common).ToString();

            _socialSink.SaveCombatReplay(new CombatReplayRecord(
                ReplayId: replayId,
                RunId: _run.RunId,
                LobbyId: lobbyId,
                CombatType: combatTypeTag,
                Day: _run.Day,
                PlayerAccountId: _run.AccountId,
                PlayerUsername: displayName,
                PlayerHero: _run.Hero.ToString(),
                OpponentName: oppName,
                OpponentHero: oppHero,
                PlayerWon: win,
                DurationMs: result.DurationMs,
                Seed: combatSeed,
                SummaryJson: summaryJson,
                CombatSimMsgPack: simPayload));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunEngine] 保存战斗回放异常（已忽略）: {ex.Message}");
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
        string ghostPartition = _run.CustomRules.ResolveGhostPoolPartition();

        if (tourneyLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId))
        {
            // P0-4 并发安全：进入 Day X Hour 6 时锁定本方快照，避免任何跨会话直接读取 RunState
            if (tourneyLobby.TournamentSessionId is Guid previewSid && previewSid != Guid.Empty)
            {
                _pvpCoordinator?.LockPlayerBoardForDay(
                    previewSid,
                    _run.Day,
                    BuildLockedPvpBoardSnapshot());
            }

            tourneyAssignment = _lobbies!.EnsureAndGetAssignment(tourneyLobby, _run.Day, _run.AccountId, _sessions);
            if (tourneyLobby.TournamentSessionId is Guid oppPreviewSid && oppPreviewSid != Guid.Empty &&
                tourneyAssignment != null &&
                !tourneyAssignment.OpponentIsBot &&
                _pvpCoordinator != null)
            {
                var lockedOpp = _pvpCoordinator.TryGetLockedBoard(
                    oppPreviewSid,
                    _run.Day,
                    tourneyAssignment.OpponentAccountId);
                if (lockedOpp != null)
                {
                    EHero liveHero = PvpGhostPool.ParseHero(lockedOpp.Hero);
                    int liveHp = Math.Max(100, lockedOpp.HealthMax);
                    int livePrestige = Math.Max(1, lockedOpp.Prestige);
                    int liveLevel = Math.Max(1, lockedOpp.Level);
                    int liveGold = Math.Max(0, lockedOpp.Gold);
                    int liveIncome = lockedOpp.Income > 0 ? lockedOpp.Income : fallbackIncome;
                    _run.CurrentPvpOpponent = BuildRichSimPvpOpponent(
                        displayName: tourneyAssignment.OpponentUsername,
                        accountId: tourneyAssignment.OpponentAccountId,
                        hero: liveHero,
                        victories: lockedOpp.Victories,
                        prestige: livePrestige,
                        level: liveLevel,
                        rating: 1200 + (int)lockedOpp.Victories * 40);
                    _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(
                        liveHp,
                        liveHp,
                        prestige: livePrestige,
                        level: liveLevel,
                        gold: liveGold,
                        income: liveIncome);
                    EmitHour6OpponentInitializedIfNeeded(events, liveHero);
                    return;
                }
            }
        }

        int expectedDay = _run.CustomRules.SpecialModeId == ESpecialModeId.EndlessTower
            ? day
            : Math.Clamp(day, 1, 15);
        bool ghostPartitionOk = _run.PendingPvpGhost != null && (
            string.Equals(_run.PendingPvpGhost.PoolPartition, ghostPartition, StringComparison.OrdinalIgnoreCase)
            || (_run.IsCustomRuleRun && string.Equals(_run.PendingPvpGhost.PoolPartition, PvpGhostPool.StandardPartition, StringComparison.OrdinalIgnoreCase)));
        bool ghostValid = _run.PendingPvpGhost != null
                       && _run.PendingPvpGhost.Day == expectedDay
                       && ghostPartitionOk
                       && (_run.IsCustomRuleRun || (!_run.PendingPvpGhost.IsCustomRule && Storage.Persistence.IsSaneGhostForDay(Math.Clamp(expectedDay, 1, 15), _run.PendingPvpGhost.HealthMax)));
        if (!ghostValid)
        {
            var ghostRng = tourneyAssignment != null
                ? new Random(tourneyAssignment.BattleSeed)
                : (_run.CustomRules.FixedRunSeed is int dSeed && dSeed != 0
                    ? DailyChallengeSeedGenerator.CreateStreamRng(dSeed, DailyChallengeSeedGenerator.StreamPvpOpponent, day, (int)_run.Hour)
                    : _rng);
            try
            {
                _run.PendingPvpGhost = _ghosts?.PickOpponent(
                    day,
                    (int)_run.Victories,
                    (int)_run.Defeats,
                    ghostRng,
                    _repo,
                    includeCustomRuleGhosts: _run.IsCustomRuleRun,
                    poolPartition: ghostPartition);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RunEngine][WARN] PickOpponent failed on Day {day}, falling back to emergency ghost: {ex.Message}");
                _run.PendingPvpGhost = null;
            }
            _run.PendingPvpGhost ??= BuildEmergencyFallbackGhost(day, ghostPartition, ghostRng);
        }

        var ghost = _run.PendingPvpGhost ?? BuildEmergencyFallbackGhost(day, ghostPartition, _rng);
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

        _run.CurrentPvpOpponent = BuildRichSimPvpOpponent(
            displayName: oppDisplayName,
            accountId: oppAccountId,
            hero: oppHero,
            victories: (uint)Math.Max(0, ghost.Wins),
            prestige: Math.Max(1, ghost.Prestige),
            level: Math.Max(1, ghost.Level),
            rating: 1000 + (int)(ghost.EstimatedWinRate * 500));
        _run.OpponentAttributes = RunState.CreateDefaultCombatantAttributes(
            previewHp,
            previewHp,
            prestige: Math.Max(1, ghost.Prestige),
            level: Math.Max(1, ghost.Level),
            gold: 0,
            income: fallbackIncome);
        EmitHour6OpponentInitializedIfNeeded(events, oppHero);
    }

    /// <summary>
    /// V13.1 Hour 6 保底（根因 #7）：当幽灵镜像池异常、锁超时或为空时，本地构造符合当天合理血量/等级/卡槽的安全兜底对手镜像，
    /// 绝不中断 Hour 6 推进或令客户端拿不到 PvpOpponent。
    /// </summary>
    private PvpGhostPool.GhostBoard BuildEmergencyFallbackGhost(int day, string ghostPartition, Random rng)
    {
        int clampedDay = Math.Max(1, day);
        string[] heroes = ["Vanessa", "Dooley", "Pygmalien", "Mak", "Karnok", "Jules", "Stelle", "Hero8"];
        string hero = heroes[rng.Next(heroes.Length)];
        int hp = clampedDay == 1 ? 250 : Math.Min(4500, 250 + (clampedDay - 1) * 350);
        var items = new List<PvpGhostPool.GhostItem>();
        try
        {
            var pool = _repo.GetSpawnableItemsForHero(hero).Where(c => c.CardSize != CardSize.Large).ToList();
            int slot = 3;
            for (int i = 0; i < 3 && pool.Count > 0 && slot < 7; i++)
            {
                var tpl = pool[rng.Next(pool.Count)];
                int w = tpl.CardSize == CardSize.Medium ? 2 : 1;
                if (slot + w > 8) break;
                items.Add(new PvpGhostPool.GhostItem
                {
                    Slot = slot,
                    Width = w,
                    TemplateId = tpl.Id,
                    InternalName = tpl.InternalName,
                    Tier = clampedDay >= 6 ? "Gold" : clampedDay >= 3 ? "Silver" : "Bronze",
                });
                slot += w;
            }
        }
        catch { }

        return new PvpGhostPool.GhostBoard
        {
            BoardId = $"emergency_fallback_d{clampedDay}_{rng.Next(1000, 9999)}",
            Day = clampedDay,
            Hero = hero,
            Level = clampedDay,
            HealthMax = hp,
            Prestige = 20,
            UnlockedSlotsMask = PvpGhostPool.DefaultUnlockedMaskForDay(clampedDay),
            PowerBucket = "Normal",
            EstimatedWinRate = 0.5,
            IsPlayerGhost = false,
            IsCustomRule = _run.IsCustomRuleRun,
            PoolPartition = string.IsNullOrWhiteSpace(ghostPartition) ? PvpGhostPool.StandardPartition : ghostPartition,
            Items = items,
        };
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

        int baseStartIncome = _mode.StartingIncome > 0 ? _mode.StartingIncome : 5;
        if (rules.StartingIncome != baseStartIncome && rules.StartingIncome > 0)
        {
            p2Core.Attributes["Income"] = rules.StartingIncome;
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

    // ── Wave 3 Batch 3B 协议、触发器、状态机与挑战收口辅助方法 ───────────────────

    /// <summary>
    /// P1-31：恢复构造尾部执行的无副作用局外光环重算：
    /// 仅重建 CoreCard.AuraAdd/AuraMult/AuraTags、PlayerAuraAdd/Mult、有效属性与 LastOutOfCombatAuraTargets，
    /// 绝不发射 GameSimEvent、绝不修改 PermanentAdd/PermanentMult，且刷新 RerollCost。
    /// </summary>
    private void RecalculateAurasSilentlyOnResume()
    {
        try
        {
            _shop.RestoreActiveShelfDiscountsFromRun();
            var p1 = BuildCorePlayerFromRun();
            var selCards = CollectSelectionCoreCards();
            IReadOnlyList<CardInstance>? externalAuras = _run.CurrentEncounterCoreCard != null
                ? new[] { _run.CurrentEncounterCoreCard }
                : null;
            IReadOnlyList<AuraEngine.AuraEffectTargetsSnapshot>? snaps = null;
            AuraEngine.RecalculateAuras(
                p1,
                p2: null,
                scope: RuleScope.OutOfCombat,
                day: (int)_run.Day,
                hour: (int)_run.Hour,
                repo: _repo,
                selectionCards: selCards.Count > 0 ? selCards : null,
                onAuraSnapshot: s => snaps = s,
                externalAuraCards: externalAuras,
                currentEncounterId: _run.CurrentEncounterId);

            SyncTrackedPlayerTempModifiersFromCore(p1);
            SyncEffectivePlayerAttributesFromCore(p1);
            _shop.RefreshEffectiveRerollCost();

            // 静默填充初始光环目标快照，使恢复后的首次移卡也能准确比对出退活（RemovedFrom）集合
            _run.LastOutOfCombatAuraTargets.Clear();
            if (snaps != null)
            {
                foreach (var snap in snaps)
                {
                    if (string.IsNullOrEmpty(snap.SourceCardInstanceId) || string.IsNullOrEmpty(snap.AuraId)) continue;
                    string key = snap.SourceCardInstanceId + "|" + snap.AuraId;
                    if (!_run.LastOutOfCombatAuraTargets.TryGetValue(key, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        _run.LastOutOfCombatAuraTargets[key] = set;
                    }
                    foreach (string cid in snap.TargetCardInstanceIds)
                        if (!string.IsNullOrEmpty(cid)) set.Add("C:" + cid);
                    foreach (string pid in snap.TargetPlayerIds)
                        if (!string.IsNullOrEmpty(pid)) set.Add("P:" + pid);
                }
            }

            // 静默刷新表现属性（丢弃事件列表，仅同步 CoreCard 光环层至 CardEntry.Attributes）
            _ = _run.SyncAllEntriesFromCoreCardsAndDiff(_repo.GameMode.StandardPrices, emitSyntheticRefreshEvent: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunEngine][WARN] RecalculateAurasSilentlyOnResume skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// P1-10：消费 CombatResult.TemporaryTransforms，在 DespawnMessage 发射 GameSimEventCardTransformReverted
    /// 并标记原卡纳入同步，修复客户端战中临时变形卡（如变废品/魔化）战后无法还原展示的问题。
    /// </summary>
    private void EmitPostCombatTemporaryTransformReverts(
        IReadOnlyList<CombatTemporaryTransformRecord>? records,
        List<IGameSimEvent> despawnEvents,
        HashSet<string> dirtyCardIds)
    {
        if (records == null || records.Count == 0) return;

        foreach (var rec in records)
        {
            if (rec == null || string.IsNullOrEmpty(rec.OriginalCardInstanceId))
                continue;
            if (!string.Equals(rec.OwnerPlayerId, "player", StringComparison.OrdinalIgnoreCase))
                continue;

            _run.Cards.TryGetValue(rec.OriginalCardInstanceId, out var origEntry);
            ECardType cardType = origEntry?.Type ?? ShopEngine.ParseCardType(rec.OriginalCardType);
            string wireTplId = origEntry != null
                ? origEntry.TemplateId.ToString()
                : ResolveSafeWireGuid(rec.OriginalTemplateId, cardType).ToString();
            EInventorySection? section = origEntry?.Section ?? ParseSection(rec.OriginalSection);
            EContainerSocketId? socket = origEntry?.Socket
                ?? (rec.OriginalSlot is >= 0 and <= 9 ? (EContainerSocketId)rec.OriginalSlot : null);

            var origTransform = new SimEventCardTransformation(
                origEntry?.InstanceId ?? rec.OriginalCardInstanceId,
                wireTplId,
                cardType,
                ECombatantId.Player,
                section,
                socket);

            var transformedIds = rec.TransformedInstanceIds is { Count: > 0 }
                ? new List<string>(rec.TransformedInstanceIds)
                : new List<string>();

            despawnEvents.Add(new GameSimEventCardTransformReverted(origTransform, transformedIds));

            if (origEntry != null)
            {
                dirtyCardIds.Add(origEntry.InstanceId);
            }
        }
    }

    /// <summary>
    /// P1-36：PvE 胜利同时触发 Fates 复活时，将本场野怪胜利战利品预生成并压入 SuspendedStates，
    /// 让玩家先完成 Fates 复活，退出 Fates 后自动弹栈恢复并重签战利品卡，避免两者丢弃其一。
    /// </summary>
    private void SuspendMonsterVictoryLootForFates(CardPool.CardTemplate? encTpl, PlayerState p1Core)
    {
        string? monsterEncId = _run.CurrentEncounterId;
        string? prevParentId = _run.ParentEncounterTemplateId;
        string? prevActiveId = _run.ActiveEncounterCardInstanceId;
        RunState.CardEntry? prevActiveCard = !string.IsNullOrEmpty(prevActiveId)
            ? _run.Cards.GetValueOrDefault(prevActiveId)
            : null;

        var activeProtected = DetachActiveEncounterCardForShelfOp();
        _ = _shop.DealMonsterVictoryLoot(
            encTpl,
            _lastCombatMonster,
            p1Core,
            chooseOne: _run.CustomRules.MonsterLootChooseOne);
        RestoreActiveEncounterCardAfterShelfOp(activeProtected);

        if (_run.State == ERunState.Loot && _run.SelectionSet.Count > 0)
        {
            var lootSnap = new SuspendedStateSnapshot
            {
                State = ERunState.Loot,
                CurrentEncounterId = monsterEncId,
                ParentEncounterTemplateId = prevParentId,
                ActiveEncounterCardInstanceId = prevActiveId,
                ActiveEncounterCard = prevActiveCard,
                SelectionSet = new List<string>(_run.SelectionSet),
                SelectionRules = RunState.CloneRules(_run.SelectionRules),
                RerollCost = _run.RerollCost,
                BaseRerollCost = _run.BaseRerollCost,
                RerollsRemaining = _run.RerollsRemaining,
                SelectionsMadeInCurrentContext = 0,
                CurrentMaxSelections = _run.CurrentMaxSelections,
                PendingEncounterExitXp = 0,
            };
            foreach (var kv in _run.ActiveShelfDiscounts)
                lootSnap.ActiveShelfDiscounts[kv.Key] = kv.Value;

            foreach (string selId in _run.SelectionSet)
            {
                if (string.Equals(selId, prevActiveId, StringComparison.Ordinal)) continue;
                if (_run.Cards.TryGetValue(selId, out var cand) && cand.Owner == null)
                    lootSnap.SuspendedCards.Add(cand);
            }

            _run.SuspendedStates.Push(lootSnap);
        }

        // 清空战利品临时货架，为即将进入的 Fates 腾出干净现场
        _shop.ClearShelf();
        _run.ActiveEncounterCardInstanceId = null;
        _run.ParentEncounterTemplateId = null;
    }

    /// <summary>
    /// P1-16：统一的局外金币变更入口，维护 Custom_3 追踪并在重入守卫下驱动 TTriggerOnPlayerAttributeChanged("Gold")。
    /// </summary>
    private void MutatePlayerGold(int delta, List<IGameSimEvent> events, bool trackCustom3OnGain = true)
    {
        if (delta == 0) return;
        int prevGold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        int nextGold;
        if (delta > 0 && trackCustom3OnGain)
        {
            _run.AddPlayerGold(delta);
            nextGold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        }
        else
        {
            nextGold = Math.Max(0, prevGold + delta);
            _run.PlayerAttributes[EPlayerAttributeType.Gold] = nextGold;
        }

        if (nextGold != prevGold)
        {
            DispatchPlayerAttributeChanged("Gold", prevGold, nextGold, events);
        }
    }

    /// <summary>
    /// P1-16：统一的局外最大生命变更入口，维护 Health 合法边界并在重入守卫下驱动 TTriggerOnPlayerAttributeChanged("HealthMax")。
    /// </summary>
    private void MutatePlayerHealthMax(int delta, List<IGameSimEvent> events, bool clampHealth = true)
    {
        if (delta == 0) return;
        int prevMax = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth);
        int nextMax = Math.Max(1, prevMax + delta);
        _run.PlayerAttributes[EPlayerAttributeType.HealthMax] = nextMax;
        if (clampHealth)
        {
            int curHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, nextMax);
            _run.PlayerAttributes[EPlayerAttributeType.Health] = delta > 0
                ? Math.Min(nextMax, curHp + delta)
                : Math.Min(curHp, nextMax);
        }

        if (nextMax != prevMax)
        {
            DispatchPlayerAttributeChanged("HealthMax", prevMax, nextMax, events);
        }
    }

    /// <summary>
    /// P1-16：带最大重入深度守卫（上限 4）的玩家属性变化触发器派发（驱动 Bootstraps / Healthy Tip / Tea Set / Hammock 等）。
    /// </summary>
    private void DispatchPlayerAttributeChanged(
        string attributeName,
        int prevValue,
        int curValue,
        List<IGameSimEvent> events)
    {
        if (prevValue == curValue) return;
        if (_attrChangeDispatchDepth >= 4) return;

        _attrChangeDispatchDepth++;
        try
        {
            var ps = BuildCorePlayerFromRun();
            var batch = OutOfCombatController.OnPlayerAttributeChanged(
                ps,
                attributeName,
                prevValue,
                curValue,
                _repo,
                _rng,
                currentEncounterId: _run.CurrentEncounterId,
                activeEncounterCard: _run.CurrentEncounterCoreCard);

            if (batch.PlayerAttributeChanges.Count > 0 ||
                batch.SpawnedCards.Count > 0 ||
                batch.RemovedCards.Count > 0 ||
                batch.TransformedCards.Count > 0 ||
                batch.RerollRequests.Count > 0 ||
                batch.DealtSelectionRequests.Count > 0 ||
                batch.ExitSelectionRequested ||
                batch.TriggeredAbilityLogs.Count > 0)
            {
                ApplyOutOfCombatBatch(ps, batch, events, syncBoardDiffs: true);
            }
        }
        finally
        {
            _attrChangeDispatchDepth--;
        }
    }

    /// <summary>
    /// P2-07：在局外技能/物品直接修改单卡属性或玩家非 PvP 声望、且事件流尚未包含对应光环刷新或 PlayerPrestigeChanged 时，
    /// 按需补发 <see cref="GameSimEventEffectExecuted"/> 对齐官方 ItemController / PrestigeBarController 刷新通道（绝不填充客户端 0 消费的 GameSim.VfxKeys）。
    /// </summary>
    private void EmitOutOfCombatEffectExecutedIfNeeded(OutOfCombatBatchResult batch, List<IGameSimEvent> events)
    {
        if (batch.CardAttributeChanges.Count == 0 && batch.PlayerAttributeChanges.Count == 0)
            return;

        if (batch.CardAttributeChanges.Count > 0)
        {
            var coveredCardIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ev in events)
            {
                if (ev is GameSimEventEffectAuraExecuted auraEv)
                {
                    if (auraEv.AppliedTo != null)
                    {
                        foreach (var t in auraEv.AppliedTo)
                            if (t is EffectTargetCard etc) coveredCardIds.Add(etc.Target.Value);
                    }
                    if (auraEv.RemovedFrom != null)
                    {
                        foreach (var t in auraEv.RemovedFrom)
                            if (t is EffectTargetCard etc) coveredCardIds.Add(etc.Target.Value);
                    }
                }
                else if (ev is GameSimEventEffectExecuted effEv && effEv.Target is EffectTargetCard etc2)
                {
                    coveredCardIds.Add(etc2.Target.Value);
                }
            }

            foreach (var ch in batch.CardAttributeChanges)
            {
                if (ch.Card == null) continue;
                var entry = FindEntryByCoreCard(ch.Card);
                if (entry == null || !_run.Cards.ContainsKey(entry.InstanceId)) continue;
                if (entry.Type != ECardType.Item) continue;
                if (!coveredCardIds.Add(entry.InstanceId)) continue;

                events.Add(new GameSimEventEffectExecuted
                {
                    ExecutionContextId = "ooc_" + entry.InstanceId,
                    EffectId = "ooc_card_modify",
                    ActionType = EActionCommandType.CardModifyAttribute,
                    Target = new EffectTargetCard { Target = new InstanceId(entry.InstanceId) },
                });
            }
        }

        bool hasPrestigeChange = batch.PlayerAttributeChanges.Any(c =>
            c.Attribute.Equals("Prestige", StringComparison.OrdinalIgnoreCase) && Math.Abs(c.Delta) > 1e-6);
        if (hasPrestigeChange &&
            _run.State != ERunState.PVPCombat &&
            !events.Any(e => e is GameSimEventPlayerPrestigeChanged) &&
            !events.Any(e => e is GameSimEventEffectExecuted ee && ee.Target is EffectTargetPlayer { Target: ECombatantId.Player }))
        {
            events.Add(new GameSimEventEffectExecuted
            {
                ExecutionContextId = "ooc_prestige",
                EffectId = "ooc_prestige_modify",
                ActionType = EActionCommandType.PlayerModifyAttribute,
                Target = new EffectTargetPlayer { Target = ECombatantId.Player },
            });
        }
    }

    /// <summary>
    /// P1-17：消费 OutOfCombatBatchResult 中由 TActionGameDealCards / TActionGameReroll（含 SpawnCount 透传）/ TActionExitSelection 产生的队列请求。
    /// </summary>
    private void ConsumeOutOfCombatBatchRequests(OutOfCombatBatchResult batch, List<IGameSimEvent> events)
    {
        if ((batch.RerollEntries.Count > 0 || batch.RerollRequests.Count > 0) && !_runEnded)
        {
            var rerollItems = batch.RerollEntries.Count > 0
                ? batch.RerollEntries
                : batch.RerollRequests.Select(t => (RerollType: t, SpawnCount: (int?)null)).ToList();

            foreach (var (rawType, spawnCount) in rerollItems)
            {
                string rerollType = string.IsNullOrWhiteSpace(rawType) ? "All" : rawType.Trim();
                if (rerollType.Equals("AllEncounters", StringComparison.OrdinalIgnoreCase))
                {
                    if (_run.State == ERunState.Choice)
                    {
                        _run.SelectionsMadeInCurrentContext = 0;
                        events.AddRange(DealHourEncountersAndClearActive(spawnCountOverride: spawnCount));
                    }
                }
                else if (_run.State == ERunState.Encounter && !string.IsNullOrEmpty(_run.CurrentEncounterId))
                {
                    var shelfEvents = DealEncounterShelfPreservingActiveEncounter(
                        _run.CurrentEncounterId,
                        isReroll: true,
                        playerState: BuildCorePlayerFromRun(),
                        ownedMaxTiers: _run.GetOwnedMaxTiers(),
                        spawnCountOverride: spawnCount);
                    NormalizeChildStepTransitionEvents(shelfEvents, _run.CurrentEncounterId);
                    events.AddRange(shelfEvents);
                }
            }
        }

        if (batch.DealtSelectionRequests.Count > 0 && !_runEnded)
        {
            foreach (var (dealtCards, spawnMode) in batch.DealtSelectionRequests)
            {
                if (dealtCards == null || dealtCards.Count == 0) continue;
                bool replace = !string.Equals(spawnMode, "Add", StringComparison.OrdinalIgnoreCase);
                if (replace)
                {
                    events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: true));
                }
                foreach (var ci in dealtCards)
                {
                    var cardType = ShopEngine.ParseCardType(ci.Template.Type);
                    string prefix = ResolveSuspendedCardPrefix(cardType);
                    string instId = _shop.NewId(prefix);
                    Guid wireGuid = ResolveSafeWireGuid(ci.Template.Id, cardType);
                    var entry = new RunState.CardEntry
                    {
                        InstanceId = instId,
                        TemplateId = wireGuid,
                        CustomCardId = ci.Template.Id,
                        Type = cardType,
                        Size = ShopEngine.ParseSize(ci.Template.Size),
                        Tier = RunState.MapCoreTier(ci.Tier),
                        Owner = null,
                        IsFreeCandidate = _run.SelectionRules.SelectionIsFree,
                        State = ECardState.Alive,
                        CoreCard = ci,
                    };
                    if (cardType == ECardType.Skill)
                    {
                        int skBuy = _run.SelectionRules.SelectionIsFree
                            ? 0
                            : _repo.GameMode.StandardPrices.GetSkillBuyPrice(ci.Tier);
                        entry.Attributes[ECardAttributeType.BuyPrice] = skBuy;
                        entry.Attributes[ECardAttributeType.SellPrice] = _repo.GameMode.StandardPrices.GetSkillSellPrice(ci.Tier);
                    }
                    else if (cardType == ECardType.Item)
                    {
                        entry.Attributes[ECardAttributeType.BuyPrice] = _run.SelectionRules.SelectionIsFree
                            ? 0
                            : RunState.ComputeCardBuyPrice(entry, ci, _repo.GameMode.StandardPrices);
                        entry.Attributes[ECardAttributeType.SellPrice] = _run.ResolveAuthoritativeSellPrice(entry, _repo.GameMode.StandardPrices);
                    }
                    _run.Cards[instId] = entry;
                    _run.SelectionSet.Add(instId);
                    events.Add(new GameSimEventCardDealt(instId, entry.TemplateId.ToString(), entry.Type));
                }
            }
        }

        if (batch.ExitSelectionRequested && !_runEnded && _run.State is ERunState.Encounter or ERunState.Pedestal or ERunState.Loot)
        {
            _run.ClearActiveReplacementSet();
            ResetBoardOverrideIfNeeded(events);
            events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            var exitOutcome = new Outcome();
            AdvanceAfterSubStateToEvents(exitOutcome.Events);
            events.AddRange(exitOutcome.Events);
        }
    }

    /// <summary>
    /// P1-40：彻底离开 LevelUpState 切回 Choice/Encounter 等非升级态时，
    /// 若本轮升级期间发生了手牌槽位解锁，补发一次幂等 GameSimEventSocketsUnlocked 驱动 CarpetController 展开地毯。
    /// </summary>
    private void EmitPostLevelUpSocketsUnlockedIfNeeded(ERunState exitingState, List<IGameSimEvent> events)
    {
        if (exitingState != ERunState.LevelUp || _run.State == ERunState.LevelUp)
            return;

        if (_run.LevelUpEntryUnlockedSlots is ushort beforeMask && beforeMask != _run.UnlockedSlots)
        {
            ushort addedMask = (ushort)(_run.UnlockedSlots & ~beforeMask);
            var newlyUnlocked = new HashSet<EContainerSocketId>();
            for (int i = 0; i < BoardGrid.Capacity; i++)
            {
                if ((addedMask & (1 << i)) != 0)
                    newlyUnlocked.Add((EContainerSocketId)i);
            }
            if (newlyUnlocked.Count > 0)
            {
                events.Add(new GameSimEventSocketsUnlocked(newlyUnlocked, _run.UnlockedSlots, EInventorySection.Hand));
            }
        }
        _run.LevelUpEntryUnlockedSlots = null;
    }

    private static string ResolveSuspendedCardPrefix(ECardType type) => type switch
    {
        ECardType.Skill => "skl",
        ECardType.EncounterStep => "stp",
        ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter => "enc",
        ECardType.PvpEncounter => "pvp",
        _ => "itm",
    };

    /// <summary>
    /// P1-32：为弹栈恢复的非 Hour 6 候选卡重签全新 InstanceId 并完整复制其属性与 CoreCard 修饰层，
    /// 彻底规避客户端 DisposedCards 拒收已销毁旧 ID。
    /// </summary>
    private RunState.CardEntry CloneCardEntryWithNewInstanceId(RunState.CardEntry src, string newInstanceId)
    {
        CardInstance? clonedCore = null;
        if (src.CoreCard != null)
        {
            var srcCore = src.CoreCard;
            clonedCore = new CardInstance(srcCore.Template, srcCore.PermanentTier, srcCore.PermanentEnchantment, newInstanceId);
            if (srcCore.Tier != clonedCore.Tier)
                clonedCore.SetTier(srcCore.Tier, isTemporary: true);
            if (!string.IsNullOrEmpty(srcCore.DayEnchantment))
                clonedCore.ApplyEnchantment(srcCore.DayEnchantment, preventOverride: false, DurationTarget.Day);
            if (!string.IsNullOrEmpty(srcCore.HourEnchantment))
                clonedCore.ApplyEnchantment(srcCore.HourEnchantment, preventOverride: false, DurationTarget.Hour);
            foreach (var kv in srcCore.PermanentAdd) clonedCore.PermanentAdd[kv.Key] = kv.Value;
            foreach (var kv in srcCore.PermanentMult) clonedCore.PermanentMult[kv.Key] = kv.Value;
            foreach (var kv in srcCore.HourAdd) clonedCore.HourAdd[kv.Key] = kv.Value;
            foreach (var kv in srcCore.HourMult) clonedCore.HourMult[kv.Key] = kv.Value;
            foreach (var kv in srcCore.DayAdd) clonedCore.DayAdd[kv.Key] = kv.Value;
            foreach (var kv in srcCore.DayMult) clonedCore.DayMult[kv.Key] = kv.Value;
            foreach (var kv in srcCore.AuraAdd) clonedCore.AuraAdd[kv.Key] = kv.Value;
            foreach (var kv in srcCore.AuraMult) clonedCore.AuraMult[kv.Key] = kv.Value;
            foreach (var tag in srcCore.ExtraTags) clonedCore.ExtraTags.Add(tag);
            foreach (var tag in srcCore.AuraTags) clonedCore.AuraTags.Add(tag);
            foreach (var kv in srcCore.QuestProgress) clonedCore.QuestProgress[kv.Key] = kv.Value;
            foreach (var q in srcCore.CompletedQuests) clonedCore.CompletedQuests.Add(q);
            clonedCore.TransformReplacementTemplateId = srcCore.TransformReplacementTemplateId;
        }

        var copy = new RunState.CardEntry
        {
            InstanceId = newInstanceId,
            TemplateId = src.TemplateId,
            CustomCardId = src.CustomCardId,
            Type = src.Type,
            Size = src.Size,
            Tier = src.Tier,
            Enchantment = src.Enchantment,
            Owner = src.Owner,
            Socket = src.Socket,
            Section = src.Section,
            State = src.State,
            IsFreeCandidate = src.IsFreeCandidate,
            CoreCard = clonedCore,
        };
        copy.Heroes.UnionWith(src.Heroes);
        copy.Tags.UnionWith(src.Tags);
        copy.HiddenTags.UnionWith(src.HiddenTags);
        foreach (var kv in src.Attributes)
            copy.Attributes[kv.Key] = kv.Value;
        return copy;
    }

    private void RestoreSuspendedShelfDiscounts(
        Dictionary<string, int> suspendedDiscounts,
        Dictionary<string, string> idRemap)
    {
        _run.ActiveShelfDiscounts.Clear();
        if (suspendedDiscounts != null)
        {
            foreach (var kv in suspendedDiscounts)
            {
                string key = idRemap.TryGetValue(kv.Key, out string? remapped) ? remapped : kv.Key;
                if (!string.IsNullOrWhiteSpace(key))
                    _run.ActiveShelfDiscounts[key] = kv.Value;
            }
        }
        _shop.RestoreActiveShelfDiscountsFromRun();
    }

    /// <summary>
    /// P1-13：统一构造终局 CompleteRunResult：
    /// 1) 依据胜场与天数计算账号通行证 XP（不再错传局内角色当前等级残留经验），并真实写入 SQLite season_progress；
    /// 2) 锦标赛完赛时基于大厅成员实时名次填充 TournamentEndRunPayload。
    /// </summary>
    private CompleteRunResult BuildCompleteRunResult(bool isVictory, bool isConceded, int? victoriesOverride = null)
    {
        int wins = victoriesOverride ?? (int)_run.Victories;
        bool skipMetaRewards = _run.IsSandboxRun || _run.CustomRules.ShouldSkipCareerAndLeaderboard;
        int chests = skipMetaRewards ? 0 : ComputeRunChestCount(_run.PlayMode, wins, isConceded);
        int passXp = skipMetaRewards ? 0 : ComputeAccountPassXpEarned(wins, (int)_run.Day, isVictory, isConceded);

        if (passXp > 0)
        {
            try
            {
                Guid accId = ResolveRunAccountGuid();
                if (accId != Guid.Empty)
                {
                    int seasonId = TempoNetEndpoints.ActiveSeasonId > 0 ? TempoNetEndpoints.ActiveSeasonId : 1;
                    LastAwardedPassSeasonId = seasonId;
                    _db?.AddSeasonTrackExperience(accId, seasonId, passXp);
                }
            }
            catch { }
        }

        return new CompleteRunResult
        {
            XpEarned = passXp,
            ChestCount = chests,
            TournamentEndRun = TryBuildTournamentEndRunPayload(isVictory),
        };
    }

    public static int ComputeAccountPassXpEarned(int victories, int day, bool isVictory, bool isConceded)
    {
        int safeWins = Math.Max(0, victories);
        int safeDay = Math.Max(1, day);
        if (isConceded && safeWins == 0 && safeDay <= 1)
            return 0;
        int xp = safeDay * 25 + safeWins * 50 + (isVictory ? 150 : 0);
        return Math.Max(25, xp);
    }

    private TournamentEndRunPayload? TryBuildTournamentEndRunPayload(bool isVictory)
    {
        if (_run.TournamentSessionId is not Guid tsid || tsid == Guid.Empty)
            return null;

        int totalPlayers = 1;
        int place = isVictory ? 1 : 2;
        bool isChampion = isVictory;

        var lobby = _lobbies?.FindLobbyForRun(tsid, _run.AccountId);
        if (lobby != null)
        {
            lock (lobby.Members)
            {
                totalPlayers = Math.Max(1, lobby.Members.Count);
                var ranked = lobby.Members
                    .OrderBy(m => m.FinalIsEliminated ? 1 : 0)
                    .ThenByDescending(m => m.FinalVictories)
                    .ThenByDescending(m => m.FinalPrestige)
                    .ThenByDescending(m => m.FinalDay)
                    .ToList();
                int idx = ranked.FindIndex(m =>
                    !string.IsNullOrWhiteSpace(_run.AccountId) &&
                    m.AccountId.Equals(_run.AccountId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    place = idx + 1;
                    isChampion = isVictory || (place == 1 && !ranked[idx].FinalIsEliminated);
                }
                else
                {
                    place = isVictory ? 1 : Math.Max(1, totalPlayers);
                }
            }
        }

        return new TournamentEndRunPayload
        {
            TournamentSessionId = tsid,
            Place = Math.Clamp(place, 1, totalPlayers),
            TotalPlayers = totalPlayers,
            IsChampion = isChampion,
        };
    }

    /// <summary>
    /// P1-14：统一构造携带完整段位（Rank）、称号（TitlePrefix/TitleSuffix）、
    /// 英雄装扮（PlayerLoadout）与收藏实例列表（PlayerCollection）的 SimPvpOpponent。
    /// </summary>
    private SimPvpOpponent BuildRichSimPvpOpponent(
        string displayName,
        string accountId,
        EHero hero,
        uint victories,
        int prestige,
        int level,
        int rating)
    {
        int clampedRating = Math.Max(800, rating);
        var acc = !string.IsNullOrWhiteSpace(accountId) ? _accounts?.FindByAccountId(accountId) : null;

        int rankScore = acc != null
            ? acc.Victories * 30 + acc.RunsCompleted * 10 + clampedRating
            : (int)victories * 60 + clampedRating;
        var (rank, division, titlePrefix, titleSuffix) = DeriveOpponentRankAndTitles(rankScore, victories, acc != null);

        BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout loadout;
        var collection = new List<CollectionItemInstanceResponse>();

        if (acc != null)
        {
            string heroKey = hero == EHero.Common ? "Vanessa" : hero.ToString();
            acc.HeroLoadouts.TryGetValue(heroKey, out var heroLoadout);
            heroLoadout ??= acc.HeroLoadouts.Values.FirstOrDefault();

            var cardSkins = heroLoadout?.CardSkinIds is { Count: > 0 }
                ? new List<string>(heroLoadout.CardSkinIds)
                : acc.OpenedCardSkinIds.Take(12).ToList();

            loadout = new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout
            {
                accountId = accountId,
                boardId = NullIfEmpty(heroLoadout?.BoardId),
                carpetId = NullIfEmpty(heroLoadout?.CarpetId),
                cardBackId = NullIfEmpty(heroLoadout?.CardBackId),
                toyId = NullIfEmpty(heroLoadout?.ToyId),
                stashId = NullIfEmpty(heroLoadout?.StashId),
                bankId = NullIfEmpty(heroLoadout?.BankId),
                albumId = NullIfEmpty(heroLoadout?.AlbumId),
                heroSkinIds = !string.IsNullOrWhiteSpace(heroLoadout?.HeroSkinId)
                    ? [heroLoadout.HeroSkinId]
                    : null,
                cardSkinIds = cardSkins.Count > 0 ? cardSkins.ToArray() : null,
            };

            AddCollectionEntryIfValid(collection, loadout.boardId);
            AddCollectionEntryIfValid(collection, loadout.carpetId);
            AddCollectionEntryIfValid(collection, loadout.cardBackId);
            AddCollectionEntryIfValid(collection, loadout.toyId);
            AddCollectionEntryIfValid(collection, loadout.stashId);
            AddCollectionEntryIfValid(collection, loadout.bankId);
            AddCollectionEntryIfValid(collection, loadout.albumId);
            if (loadout.heroSkinIds != null)
                foreach (string hs in loadout.heroSkinIds) AddCollectionEntryIfValid(collection, hs);
            if (loadout.cardSkinIds != null)
                foreach (string cs in loadout.cardSkinIds) AddCollectionEntryIfValid(collection, cs);
        }
        else
        {
            string safeAccId = string.IsNullOrWhiteSpace(accountId) ? $"ghost_{hero}" : accountId;
            loadout = new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout
            {
                accountId = safeAccId,
                boardId = $"default_board_{hero.ToString().ToLowerInvariant()}",
                carpetId = $"default_carpet_{hero.ToString().ToLowerInvariant()}",
                cardBackId = "default_cardback_bazaar",
            };
            AddCollectionEntryIfValid(collection, loadout.boardId);
            AddCollectionEntryIfValid(collection, loadout.carpetId);
            AddCollectionEntryIfValid(collection, loadout.cardBackId);
        }

        return new SimPvpOpponent(
            name: string.IsNullOrWhiteSpace(displayName) ? hero.ToString() : displayName,
            titlePrefix: titlePrefix,
            titleSuffix: titleSuffix,
            rank: rank,
            rating: clampedRating,
            division: division,
            victories: victories,
            prestige: Math.Max(1, prestige),
            level: Math.Max(1, level),
            hero: hero,
            playerLoadoutResponse: loadout,
            playerCollection: collection);
    }

    private static (BazaarGameShared.TempoNet.Enums.ERank Rank, int Division, string Prefix, string Suffix) DeriveOpponentRankAndTitles(
        int rankScore,
        uint victories,
        bool isRealPlayer)
    {
        if (rankScore >= 1700 || victories >= 9)
            return (BazaarGameShared.TempoNet.Enums.ERank.Legendary, 1, "Legendary", isRealPlayer ? "Champion" : "of the Bazaar");
        if (rankScore >= 1450 || victories >= 7)
            return (BazaarGameShared.TempoNet.Enums.ERank.Diamond, 2, "Diamond", "Grandmaster");
        if (rankScore >= 1250 || victories >= 4)
            return (BazaarGameShared.TempoNet.Enums.ERank.Gold, 3, "Gilded", "Merchant");
        if (rankScore >= 1100 || victories >= 2)
            return (BazaarGameShared.TempoNet.Enums.ERank.Silver, 4, "Seasoned", "Trader");
        return (BazaarGameShared.TempoNet.Enums.ERank.Bronze, 5, "Wandering", "Challenger");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void AddCollectionEntryIfValid(List<CollectionItemInstanceResponse> list, string? rawItemId)
    {
        if (string.IsNullOrWhiteSpace(rawItemId)) return;
        Guid itemGuid = Guid.TryParse(rawItemId, out var parsed)
            ? parsed
            : CreateDeterministicGuid("col:" + rawItemId);
        if (list.Any(x => x.ItemId == itemGuid)) return;
        list.Add(new CollectionItemInstanceResponse
        {
            Id = CreateDeterministicGuid("inst:" + rawItemId),
            ItemId = itemGuid,
            MintNumber = 1,
        });
    }

    private static Guid CreateDeterministicGuid(string seed)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        byte[] bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }

    private Guid ResolveRunAccountGuid()
    {
        if (!string.IsNullOrWhiteSpace(_run.AccountId))
        {
            var acc = _accounts?.FindByAccountId(_run.AccountId);
            if (acc != null && acc.AccountId != Guid.Empty)
                return acc.AccountId;
            if (Guid.TryParse(_run.AccountId, out var parsed) && parsed != Guid.Empty)
                return parsed;
        }
        return Guid.Empty;
    }

    /// <summary>P1-02：局内卡牌购买/出售/升阶空安全驱动 ChallengeService 进度（自动跳过沙盒）。</summary>
    private void RecordChallengeForCard(
        string triggerType,
        RunState.CardEntry card,
        int? buyPriceOverride = null,
        bool isFusion = false)
    {
        if (_challenges == null || _run.IsSandboxRun || _run.CustomRules.ShouldSkipCareerAndLeaderboard)
            return;
        try
        {
            Guid accId = ResolveRunAccountGuid();
            if (accId == Guid.Empty) return;
            var tpl = card.CoreCard?.Template ?? _repo.GetCard(RunState.ResolveCoreTemplateId(card));
            if (tpl == null) return;
            var ctx = ChallengeProgressContext.FromCard(
                tpl,
                tierOverride: ShopEngine.ToCoreTier(card.Tier),
                buyPriceOverride: buyPriceOverride,
                hasEnchantment: card.Enchantment.HasValue,
                isFusion: isFusion);
            _challenges.RecordChallengeProgress(accId, triggerType, delta: 1, context: ctx);
        }
        catch { }
    }

    private void RecordChallengeFightEnded(bool isPvp, string outcome)
    {
        if (_challenges == null || _run.IsSandboxRun || _run.CustomRules.ShouldSkipCareerAndLeaderboard)
            return;
        try
        {
            Guid accId = ResolveRunAccountGuid();
            if (accId == Guid.Empty) return;
            _challenges.RecordChallengeProgress(accId, "TTriggerOnFightEnded", delta: 1, context: new ChallengeProgressContext
            {
                CombatType = isPvp ? "PVP" : "PVE",
                CombatOutcome = outcome,
                Hero = _run.Hero.ToString(),
            });
        }
        catch { }
    }

    private void RecordChallengeLevelUp(int newLevel)
    {
        if (_challenges == null || _run.IsSandboxRun || _run.CustomRules.ShouldSkipCareerAndLeaderboard)
            return;
        try
        {
            Guid accId = ResolveRunAccountGuid();
            if (accId == Guid.Empty) return;
            _challenges.RecordChallengeProgress(accId, "TTriggerOnPlayerLeveledUp", delta: 1, context: new ChallengeProgressContext
            {
                NewLevel = newLevel,
                Hero = _run.Hero.ToString(),
            });
        }
        catch { }
    }

    private void RecordChallengeRunCompleted(bool isVictory, bool isConceded)
    {
        if (_challenges == null || _run.IsSandboxRun || _run.CustomRules.ShouldSkipCareerAndLeaderboard)
            return;
        try
        {
            Guid accId = ResolveRunAccountGuid();
            if (accId == Guid.Empty) return;
            _challenges.RecordChallengeProgress(accId, "TTriggerOnRunCompleted", delta: 1, context: new ChallengeProgressContext
            {
                RunOutcome = isVictory ? "Victory" : (isConceded ? "Conceded" : "Defeat"),
                Hero = _run.Hero.ToString(),
            });
        }
        catch { }
    }
}
