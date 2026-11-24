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
    /// <summary>W1-A P0-17 接缝：局级不可变 Mod 运行时上下文（非 null 时透传至 CombatSimulator 彻底隔离全局静态 ModRuntimeHooks）。</summary>
    private readonly BazaarCore.Mods.ModRuntimeContext? _modRuntimeContext;
    /// <summary>P0-26 / P0-30 / P0-22：可选的服务端 Mod 宿主、单局 Mod 上下文与局级视觉绑定映射（保持全量历史单测构造零破损）。</summary>
    private readonly BazaarServer.Mods.ServerModHost? _modHost;
    private readonly BazaarServer.Mods.ServerModHost.RunModContext? _runModContext;
    private readonly IReadOnlyDictionary<string, BazaarCore.Mods.CustomCardVisualBinding>? _runVisualBindings;
    private readonly DateTimeOffset _engineAttachedAtUtc = DateTimeOffset.UtcNow;
    private Random _rng;

    // 玩家局外限时属性层（UntilEndOfHour / UntilEndOfDay），直接绑定 RunState 以便无损快照持久化
    private readonly Dictionary<string, double> _playerHourAdd;
    private readonly Dictionary<string, double> _playerHourMult;
    private readonly Dictionary<string, double> _playerDayAdd;
    private readonly Dictionary<string, double> _playerDayMult;

    private bool _runEnded;
    private int _attrChangeDispatchDepth;
    /// <summary>P0-14：记录当前单条命令内是否经 ActivateEncounterCard 内部合成了全新顶栏遭遇实体，供 Handle 收口附加尾部 GameStateSync。</summary>
    private bool _synthesizedEncounterCardThisCommand;
    private Data.MonsterTemplate? _lastCombatMonster;
    private readonly List<string> _boardAuditLogs = new();
    /// <summary>P0-19（合流胶水 #6）：本场战斗内经 CombatSimConverter 运行期变形新出现的自定义逻辑模板 ID 集合，供战报视觉绑定打包采集。</summary>
    private readonly HashSet<string> _combatTransientCustomCardIds = new(StringComparer.OrdinalIgnoreCase);

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
        Persistence? db = null,
        BazaarCore.Mods.ModRuntimeContext? modRuntimeContext = null,
        BazaarServer.Mods.ServerModHost? modHost = null,
        IReadOnlyDictionary<string, BazaarCore.Mods.CustomCardVisualBinding>? runVisualBindings = null,
        BazaarServer.Mods.ServerModHost.RunModContext? runModContext = null)
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
        _runModContext = runModContext;
        _modRuntimeContext = modRuntimeContext ?? runModContext?.RuntimeContext;
        _modHost = modHost;
        _runVisualBindings = runVisualBindings ?? runModContext?.VisualBindingsByCustomId;
        if (_run.RngSeed == 0)
            _run.RngSeed = Random.Shared.Next(1, int.MaxValue);
        _rng = new Random(_run.RngSeed);
        _playerHourAdd = run.PlayerHourAdd;
        _playerHourMult = run.PlayerHourMult;
        _playerDayAdd = run.PlayerDayAdd;
        _playerDayMult = run.PlayerDayMult;
        _run.CustomRules = (_run.CustomRules ?? new CustomRunRuleSet()).CloneNormalized(GetEffectiveCustomMutators());
        _shop = new ShopEngine(run, pool, repo, _run.CustomRules, archetypeIndex)
        {
            ModRuntimeContext = _modRuntimeContext,
            RunModContext = _runModContext,
        };
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
        _run.RefreshImmutableRulesSnapshot(GetEffectiveCustomMutators());
        _run.PublishPublicSummary();
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
            _run.CustomRules = _run.CustomRules.CloneNormalized(GetEffectiveCustomMutators());
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
        _run.LastCompletedPvpDay = 0;
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
        _run.ActiveShelfOriginalPrices.Clear();
        _run.BasePlayerAttributes.Clear();
        _run.LastDerivedPlayerAttributes.Clear();
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

        _run.OpponentHero = null;
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

        // P1-10（RC-JULES-START-SPAWN-DEALT-MIX）：
        // 先在独立 preInitEvents 缓冲区结算开局预设物/技能（StartingItems/StartingSkills/GoldenStart）
        // 以及官方英雄开局固有实体/初始插座（Jules 烤炉、Hero8 音符等），令其实体先行落盘到 _run.Cards（随前置 GameStateSync 落地）；
        // 非实体生成类事件（ExperienceGained/SocketsUnlocked 等）仍留入主事件流，
        // 而实体生成事件绝不与随后首条 Choice/LevelUp 交互包的 CardDealt 同包混发，彻底杜绝 same_batch_spawned_and_dealt_in_choice 与 GetCardContainerBounds 越界崩溃。
        var preInitEvents = new List<IGameSimEvent>();
        GrantStarterPresets(_run.CustomRules, preInitEvents);

        // 先派发开局生命周期触发（Q45：先注入官方开局 PlayerEffect 以触发 Jules 冰火插座 15d06ad8 等），
        // 确保所有开局属性与经验变动全部结算后再决定首屏状态，避免同帧先发 Choice 遭遇再立刻销毁切 LevelUp 导致客户端残留僵尸事件！
        var p1Core = BuildCorePlayerFromRun();
        OutOfCombatController.InjectOfficialPlayerEffects(
            p1Core,
            _repo,
            includeTournamentMode: false,
            includeHeroRunStartSpawners: true);
        var startBatch = OutOfCombatController.OnRunStarted(p1Core, _repo, _rng);
        ApplyOutOfCombatBatch(p1Core, startBatch, preInitEvents, syncBoardDiffs: false);

        var preInitSpawnedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ev in preInitEvents)
        {
            if (ev is GameSimEventCardSpawned sp && !string.IsNullOrEmpty(sp.InstanceId))
                preInitSpawnedIds.Add(sp.InstanceId);
        }

        var preInitSpawnEvents = new List<IGameSimEvent>();
        foreach (var ev in preInitEvents)
        {
            bool isPreInitEntitySpawnEvent = ev is GameSimEventCardSpawned
                || (ev is GameSimEventPlayerSkillEquipped sk && preInitSpawnedIds.Contains(sk.InstanceId))
                || (ev is GameSimEventCardMoved mv && preInitSpawnedIds.Contains(mv.InstanceId));
            if (isPreInitEntitySpawnEvent)
                preInitSpawnEvents.Add(ev);
            else
                o.Events.Add(ev);
        }

        if (preInitSpawnEvents.Count > 0)
        {
            // 留存一份纯粹的前置初始化快照（仅含初始实体生成，不含 Choice CardDealt），供需要分段消费的调用方或测试核验
            var preInitFrameEvents = new List<IGameSimEvent>(o.Events.Count + preInitSpawnEvents.Count);
            preInitFrameEvents.AddRange(o.Events);
            preInitFrameEvents.AddRange(preInitSpawnEvents);
            o.PreSplitGameSim = _run.BuildFullGameSim(preInitFrameEvents);
        }

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

        // 若首包未产出任何 CardDealt（极端空池兜底），仍保留前置生成事件；否则严禁与 CardDealt 同包混发
        bool hasDealtInStart = o.Events.Any(e => e is GameSimEventCardDealt);
        if (preInitSpawnEvents.Count > 0 && !hasDealtInStart)
        {
            o.Events.AddRange(preInitSpawnEvents);
            o.PreSplitGameSim = null;
        }

        var startAuditEvents = hasDealtInStart ? new List<IGameSimEvent>() : o.Events;
        ReconcileAndVerifyBoardInvariants("StartRun", o.DirtyCardIds, startAuditEvents);
        _run.RngSeed = _rng.Next(1, int.MaxValue);
        _run.RefreshImmutableRulesSnapshot(GetEffectiveCustomMutators());
        _run.PublishPublicSummary();
        return o;
    }

    private CustomRunRuleSet ResolveEffectiveRulesForStart()
    {
        var effCustomMutators = GetEffectiveCustomMutators();
        var lobby = _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId);
        if (lobby != null)
        {
            var resolved = lobby.Rules.CloneNormalized(effCustomMutators);
            if (resolved.AllowTendencySelection)
            {
                if (_playerRules != null && !string.IsNullOrWhiteSpace(_run.AccountId))
                    resolved.SelectedArchetypeMask = _playerRules.Get(_run.AccountId).SelectedArchetypeMask;
                else if (_run.CustomRules != null)
                    resolved.SelectedArchetypeMask = _run.CustomRules.SelectedArchetypeMask;
                resolved = resolved.CloneNormalized(effCustomMutators);
            }
            else
            {
                resolved.SelectedArchetypeMask = 0UL;
            }
            return resolved;
        }

        if (_playerRules != null && !string.IsNullOrWhiteSpace(_run.AccountId))
            return _playerRules.Get(_run.AccountId).CloneNormalized(effCustomMutators);

        return (_run.CustomRules ?? new CustomRunRuleSet()).CloneNormalized(effCustomMutators);
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
        var effCustomMutators = GetEffectiveCustomMutators();
        _run.CustomRules = _run.CustomRules.CloneNormalized(effCustomMutators);
        _run.CustomRules.SelectedArchetypeMask = lockedMask;
        _run.TendencyLocked = true;
        _shop.ActiveRules = _run.CustomRules;
        _shop.UpdateActiveArchetypeMask(lockedMask);
        _run.RefreshImmutableRulesSnapshot(effCustomMutators);
        _run.PublishPublicSummary();
        error = null;
        return true;
    }

    private bool IsTournamentRun()
        => _run.PlayMode == EPlayMode.Tournament
        || (_run.TournamentSessionId.HasValue && _run.TournamentSessionId.Value != Guid.Empty)
        || _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId) != null;

    private void ApplyStartingStats(CustomRunRuleSet rules, List<IGameSimEvent> events)
    {
        int startHp = Math.Max(1, rules.StartingHealth != GameDefaults.StartingHealth ? rules.StartingHealth : _mode.StartingHealth);
        int startGold = Math.Max(0, rules.StartingGold != GameDefaults.StartingGold ? rules.StartingGold : _mode.StartingGold);
        int startIncome = Math.Max(0, rules.StartingIncome != GameDefaults.StartingIncome ? rules.StartingIncome : _mode.StartingIncome);
        int startPrestige = Math.Max(1, rules.StartingPrestige != GameDefaults.StartingPrestige ? rules.StartingPrestige : _mode.PrestigeInitial);
        int startExp = Math.Max(0, rules.StartingExperience != GameDefaults.StartingExperience ? rules.StartingExperience : _mode.StartingExperience);

        // P0-22（合流胶水 #4）：必须先执行 IGameModeHook.OnRunSetup 合入 ForcedMutatorIds，
        // 确保自定义模式强制注入的 DoubleEconomy / IronmanNoFates / GoldenStart 在开局全部生效
        var modeSetupAdj = CollectGameModeRunSetup(rules);

        if (rules.HasMutator(MutatorCatalog.DoubleEconomy))
        {
            startGold = Math.Min(9999, startGold * 2);
            startIncome = Math.Min(999, startIncome * 2);
        }
        // D6-P1-13：仅勾选（或自定义模式强制注入）IronmanNoFates 词条时才截断初始声望 <= 10 并关闭死神复活，不再由 DisableFatesRevive 越权砍声望
        if (rules.HasMutator(MutatorCatalog.IronmanNoFates))
        {
            startPrestige = Math.Min(startPrestige, 10);
            _run.FatesUsed = true;
        }
        if (!_run.IsSandboxRun && rules.HasMutator(MutatorCatalog.GoldenStart) && _run.UnlockedSlots == 0b00_0111_1000)
        {
            _run.UnlockedSlots = 0b01_1111_1110;
            _run.HandGrid.UnlockedMask = _run.UnlockedSlots;
        }

        // 叠加自定义模式调节与隔离自定义突变词条的起始数值增量
        ApplyCustomModeAndMutatorDeltas(rules, modeSetupAdj, ref startHp, ref startGold, ref startIncome);

        // 必须先设 HealthMax 再设 Health，否则 ComputeDerivedPlayerAttribute(Health) 会按旧默认 300 上限截断 >300 的自定义起始血量
        _run.SetBasePlayerAttribute(EPlayerAttributeType.HealthMax, startHp);
        _run.SetBasePlayerAttribute(EPlayerAttributeType.Health, startHp);
        _run.SetBasePlayerAttribute(EPlayerAttributeType.Gold, startGold);
        _run.SetBasePlayerAttribute(EPlayerAttributeType.Income, startIncome);
        _run.SetBasePlayerAttribute(EPlayerAttributeType.Level, 1);
        _run.SetBasePlayerAttribute(EPlayerAttributeType.Experience, 0);
        _run.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, startPrestige);

        int xpReq = Math.Max(1, _mode.ExperiencePerLevel);
        if (startExp < xpReq)
        {
            // 官方初始 Experience = 3（< 8），直接赋值且不触发首帧误升级或多余跳字
            _run.SetBasePlayerAttribute(EPlayerAttributeType.Experience, startExp);
        }
        else
        {
            GrantExperienceToList(events, startExp, EExperienceType.Hour);
        }
    }

    private IReadOnlyDictionary<string, MutatorMetadata>? GetEffectiveCustomMutators()
        => _runModContext?.CustomMutatorsById ?? _modRuntimeContext?.EffectiveCustomMutatorsById;

    private CustomMutatorRuleSummary ResolveCustomMutatorSummary(CustomRunRuleSet? rules = null)
    {
        var eff = rules ?? _run.CustomRules;
        if (_runModContext != null)
            return _runModContext.ResolveMutatorRuleSummary(eff);
        return MutatorCatalog.ResolveRuleSummary(eff?.MutatorIds, _modRuntimeContext?.EffectiveCustomMutatorsById);
    }

    private BazaarCore.Mods.IGameModeHook? ResolveActiveGameModeHook(CustomRunRuleSet? rules = null)
    {
        var eff = rules ?? _run.CustomRules;
        string? modeId = !string.IsNullOrWhiteSpace(eff?.CustomModeId)
            ? eff!.CustomModeId!.Trim()
            : (eff != null && eff.SpecialModeId != ESpecialModeId.Standard ? eff.SpecialModeId.ToString() : null);
        if (string.IsNullOrWhiteSpace(modeId))
            return null;
        if (_runModContext != null && _runModContext.TryGetGameModeHook(modeId, out var runHook) && runHook != null)
            return runHook;
        if (_modRuntimeContext?.ModeHooks != null && _modRuntimeContext.ModeHooks.TryGetValue(modeId, out var ctxHook))
            return ctxHook;
        return null;
    }

    private BazaarCore.Mods.GameModeRunSetupAdjustment? CollectGameModeRunSetup(CustomRunRuleSet rules)
    {
        var hook = ResolveActiveGameModeHook(rules);
        if (hook == null)
            return null;

        var adj = new BazaarCore.Mods.GameModeRunSetupAdjustment();
        try
        {
            hook.OnRunSetup(adj, rules);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunEngine] IGameModeHook({hook.ModeId}).OnRunSetup failed: {ex.Message}");
        }

        if (adj.ForcedMutatorIds.Count > 0)
        {
            var merged = new List<string>(rules.MutatorIds ?? new List<string>());
            foreach (var mid in adj.ForcedMutatorIds)
            {
                if (!string.IsNullOrWhiteSpace(mid) && !merged.Contains(mid.Trim(), StringComparer.OrdinalIgnoreCase))
                    merged.Add(mid.Trim());
            }
            rules.MutatorIds = MutatorCatalog.NormalizeAndResolveConflicts(merged, maxCount: 4, GetEffectiveCustomMutators());
        }

        if (Math.Abs(adj.ShopPriceMultiplier - 1.0) > 1e-6)
            rules.ShopPriceMultiplier = Math.Clamp(rules.ShopPriceMultiplier * adj.ShopPriceMultiplier, 0.2, 5.0);
        if (adj.MaxDaysOverride is > 0)
            rules.EndlessMaxDay = Math.Clamp(adj.MaxDaysOverride.Value, 11, 99);
        if (adj.FreeFirstShopReroll)
            _shop.CustomModeFreeFirstShopReroll = true;

        return adj;
    }

    private void ApplyCustomModeAndMutatorDeltas(
        CustomRunRuleSet rules,
        BazaarCore.Mods.GameModeRunSetupAdjustment? adj,
        ref int startHp,
        ref int startGold,
        ref int startIncome)
    {
        if (adj != null)
        {
            startHp = Math.Clamp(startHp + adj.StartingHealthDelta, 50, 10000);
            startGold = Math.Clamp(startGold + adj.StartingGoldDelta, 0, 9999);
            startIncome = Math.Clamp(startIncome + adj.StartingIncomeDelta, 0, 999);
        }

        var mutSummary = ResolveCustomMutatorSummary(rules);
        startGold = Math.Clamp(startGold + mutSummary.StartingGoldDelta, 0, 9999);
        startIncome = Math.Clamp(startIncome + mutSummary.StartingIncomeDelta, 0, 999);
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
                // P0-03：预设开局物品构造时即透传服务端前缀 InstanceId，彻底避免 CoreCard 退化为随机 32 位 UUID
                var presetTpl = !string.IsNullOrWhiteSpace(spec?.TemplateId) ? _repo.GetCard(spec.TemplateId) : null;
                string presetPrefix = ShopEngine.ParseCardType(presetTpl?.Type) switch
                {
                    ECardType.Skill => "skl",
                    ECardType.SocketEffect => "skt",
                    _ => "itm",
                };
                var ci = TryCreatePresetCoreCard(spec, instanceId: _shop.NewId(presetPrefix), allowEnchantment: true);
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
                // P0-03：预设技能构造时即透传服务端前缀 InstanceId
                var ci = TryCreatePresetCoreCard(spec!, instanceId: _shop.NewId("skl"), allowEnchantment: false);
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
                // P0-03：全金开局物品强制透传唯一 InstanceId，杜绝 CoreCard 随机 UUID 割裂
                string instId = _shop.NewId("itm");
                var ci = new CardInstance(coreTpl, tier, null, instId);
                RegisterSpawnedCoreCard(ci, EInventorySection.Hand, preferredSlot: -1, emitSpawnEvent: true, events);
                grantedAny = true;
            }

            // D6-P2-03：CrossHeroMayhem 跨英雄全金开局技能池必须包含 Common 通用技能（及无英雄限定技能）
            var skillPool = _pool.AllSkills
                .Where(c => _pool.IsPlayerSpawnable(c) &&
                            (rules.SpecialModeId == ESpecialModeId.CrossHeroMayhem
                                ? (c.Heroes.Length == 0
                                    || c.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase)
                                    || c.Heroes.Any(h => HeroNames.PlayableHeroes.Contains(h, StringComparer.OrdinalIgnoreCase)))
                                : (c.Heroes.Length == 0
                                    || c.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase)
                                    || c.Heroes.Contains(heroName, StringComparer.OrdinalIgnoreCase))))
                .ToList();
            if (skillPool.Count > 0)
            {
                var skPick = skillPool[startRng.Next(skillPool.Count)];
                var coreTpl = _repo.GetCard(skPick.Id);
                if (coreTpl != null)
                {
                    var tier = SpawnQueryEngine.ClampToSupportedTier(coreTpl, CardTier.Gold);
                    // P0-03：全金开局技能强制透传唯一 InstanceId，杜绝 CoreCard 随机 UUID 割裂
                    string instId = _shop.NewId("skl");
                    var ci = new CardInstance(coreTpl, tier, null, instId);
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

        string? boardIdBeforeCommand = _run.CurrentBoardId;
        _synthesizedEncounterCardThisCommand = false;
        try
        {
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
                // D2-P1-04：同包折叠多余 BoardOverridden 事件，确保客户端 .First() 必命中最终目标板
                CoalesceBoardOverriddenEvents(outcome.Events, boardIdBeforeCommand);
                // D2-P1-03：空货架状态机硬不变量，确保任意空 Encounter/Loot 货架绝不锁死
                EnsureEmptyShelfNeverDeadlocks();

                // P0-14：若本条命令内经 ActivateEncounterCard 内部合成了新顶栏遭遇卡（非货架点选），
                // 在命令出口处于 Encounter/Pedestal/Loot 态时追加尾部 GameStateSync，由客户端原生对齐顶栏且中央货架 0 幽灵卡。
                if (_synthesizedEncounterCardThisCommand &&
                    _run.State is ERunState.Encounter or ERunState.Pedestal or ERunState.Loot)
                {
                    outcome.AppendStateSyncAtTail = true;
                }
                if (!_run.TendencyLocked &&
                    (_run.Day > 1 || _run.Hour > 1 || _run.StartSelectionDone || _run.SelectionsMadeInCurrentContext > 0))
                {
                    _run.TendencyLocked = true;
                }
                _run.RngSeed = _rng.Next(1, int.MaxValue);
            }
            return outcome;
        }
        finally
        {
            _synthesizedEncounterCardThisCommand = false;
            _run.PublishPublicSummary();
        }
    }

    /// <summary>D2-P1-03：状态机硬不变量——处于非终局 Encounter/Loot 且货架与剩余分页均为空时强制允许退出，杜绝死锁。</summary>
    private void EnsureEmptyShelfNeverDeadlocks()
    {
        if (_runEnded) return;
        if (_run.State is not (ERunState.Encounter or ERunState.Loot)) return;
        if (_run.SelectionSet.Count > 0) return;
        if (_run.State == ERunState.Loot && _shop.HasRemainingMonsterLootCandidates()) return;
        if (_run.SelectionRules.CanExit) return;

        var unlocked = RunState.CloneRules(_run.SelectionRules);
        unlocked.CanExit = true;
        _run.SelectionRules = unlocked;
    }

    /// <summary>D2-P1-04：折叠同包内连续多次 BoardOverridden（客户端 GameSimHandler 仅消费 .First()），确保最终目标板生效且零冗余闪烁。</summary>
    public static void CoalesceBoardOverriddenEvents(List<IGameSimEvent> events, string? boardIdBeforeCommand)
    {
        if (events == null || events.Count == 0) return;
        int lastIdx = -1;
        int count = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is GameSimEventBoardOverridden)
            {
                count++;
                lastIdx = i;
            }
        }
        if (count == 0) return;

        var finalEv = (GameSimEventBoardOverridden)events[lastIdx];
        bool sameAsBefore = string.Equals(finalEv.Board ?? "", boardIdBeforeCommand ?? "", StringComparison.OrdinalIgnoreCase);
        if (count == 1 && !sameAsBefore)
            return;

        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is GameSimEventBoardOverridden)
            {
                if (sameAsBefore || i != lastIdx)
                    events.RemoveAt(i);
            }
        }
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
            // D1-P2-04：二合一融合必须按目标卡永久附魔（PermanentEnchantment）判定保留，不被临时 Day/Hour 附魔误遮蔽
            EEnchantmentType? targetPermEnchantment = ResolveEntryPermanentEnchantment(fuseTarget);
            EEnchantmentType? cardPermEnchantment = ResolveEntryPermanentEnchantment(card);
            EEnchantmentType? finalEnchantment = targetPermEnchantment ?? cardPermEnchantment;
            bool enchantmentChanged = finalEnchantment.HasValue && finalEnchantment != targetPermEnchantment;
            if (enchantmentChanged)
            {
                var fuseCore = EnsureCoreCard(fuseTarget);
                fuseCore.ApplyEnchantment(finalEnchantment!.Value.ToString(), DurationTarget.Permanent, preventOverride: false);
                fuseTarget.Enchantment = ParseEnchantmentOrNull(fuseCore.Enchantment) ?? finalEnchantment;
            }
            // P2-48 & D1-P2-04：升阶前通过 CardInstance.MergeFromFusedCard 统一合入非价格类永久属性（Add/Mult/AdditiveMult）、标签与任务进度
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
            // D2-P1-07：退出 LevelUp / Loot 统一传 preserveActiveEncounter: false，防止残留旧顶栏遭遇卡
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
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

    /// <summary>
    /// D2-P1-07：统一委托 BazaarCore CardInstance.CanUpgrade() 单一真源判定升阶资格，
    /// 允许 5 阶特例卡（如 Maker's Mark）与 5 阶 Mod 卡由 Diamond 升至 Legendary，同时精准拒绝无更高 Tiers 定义的卡。
    /// </summary>
    private bool CanCardUpgrade(RunState.CardEntry entry, out ETier nextTier)
    {
        nextTier = entry.Tier;
        if (entry.Type is not (ECardType.Item or ECardType.Skill))
            return false;
        var core = entry.CoreCard ?? EnsureCoreCard(entry);
        if (core == null || !core.CanUpgrade())
            return false;
        CardTier coreNext = core.Tier + 1;
        nextTier = RunState.MapCoreTier(coreNext);
        return nextTier > entry.Tier;
    }

    private RunState.CardEntry? FindFuseTarget(RunState.CardEntry incoming)
    {
        if (!CanCardUpgrade(incoming, out _))
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
                c.Tier == incoming.Tier &&
                CanCardUpgrade(c, out _))
            .OrderBy(c => c.Section == EInventorySection.Hand ? 0 : (c.Section == EInventorySection.Stash ? 1 : 2))
            .ThenBy(c => c.Socket.HasValue ? (int)c.Socket.Value : int.MaxValue)
            .ThenBy(c => c.InstanceId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static EEnchantmentType? ParseEnchantmentOrNull(string? enchName)
        => !string.IsNullOrWhiteSpace(enchName) && Enum.TryParse<EEnchantmentType>(enchName, ignoreCase: true, out var parsed)
            ? parsed
            : null;

    private static EEnchantmentType? ResolveEntryPermanentEnchantment(RunState.CardEntry entry)
    {
        if (entry.CoreCard != null)
            return ParseEnchantmentOrNull(entry.CoreCard.PermanentEnchantment);
        return entry.Enchantment;
    }

    /// <summary>
    /// P2-48 &amp; D1-P2-04：买卡融合（Buy-Fusion）升阶前，统一委托 <see cref="CardInstance.MergeFromFusedCard"/>
    /// 将商店待购卡的非价格类永久加成（PermanentAdd / PermanentMult / PermanentAdditiveMult / 标签 / 任务进度）合入 <paramref name="fuseTarget"/>.CoreCard。
    /// </summary>
    private void MergeShelfPermanentModifiersIntoFuseTarget(RunState.CardEntry shelfCard, RunState.CardEntry fuseTarget)
    {
        var shelfCore = shelfCard.CoreCard;
        if (shelfCore == null) return;
        var targetCore = EnsureCoreCard(fuseTarget);
        targetCore.MergeFromFusedCard(shelfCore);
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
            // P0-03：原地重签自愈历史存档或外部构造导致的 CoreCard.InstanceId != CardEntry.InstanceId 割裂，同时保持网格与引用一致
            if (!entry.HasAlignedCoreCardId)
                entry.BindAlignedCoreCard(entry.CoreCard);
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
                selectionCards: selectionCards,
                movedCard: coreCard,
                rng: _rng,
                currentEncounterId: _run.CurrentEncounterId);
            ApplyOutOfCombatBatchWithDirty(p1Core, moveBatch, o.Events, syncBoardDiffs: true, dirtyCardIds: o.DirtyCardIds);
            if (selectionCards.Count > 0)
                SyncShelfEntriesDiff(o.Events, o.DirtyCardIds);

            // P0-13：统一委托 RunState.CollectEventReferencedCardIds 登记全部事件引用实体 ID（含变形/还原/效果/光环/升级/附魔）
            if (o.Events.Count > prevEventCount)
            {
                RunState.CollectEventReferencedCardIds(o.Events.Skip(prevEventCount), o.DirtyCardIds);
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

    /// <summary>P0-13：检查卡牌或其模板/附魔是否携带 TTriggerOnCardMoved 局外移动触发被动。</summary>
    private static bool HasMoveTriggeredAbility(CardInstance? ci, BazaarCore.Models.CardTemplate? tpl, EEnchantmentType? entryEnchantment = null)
    {
        if (ci != null)
        {
            foreach (var ab in ci.ActiveAbilities)
            {
                if (string.Equals(ab.Trigger?.Type, "TTriggerOnCardMoved", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        if (tpl?.Abilities != null)
        {
            foreach (var ab in tpl.Abilities.Values)
            {
                if (string.Equals(ab.Trigger?.Type, "TTriggerOnCardMoved", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        if (entryEnchantment.HasValue &&
            tpl?.Enchantments != null &&
            tpl.Enchantments.TryGetValue(entryEnchantment.Value.ToString(), out var enchData) &&
            enchData?.Abilities != null)
        {
            foreach (var ab in enchData.Abilities.Values)
            {
                if (string.Equals(ab.Trigger?.Type, "TTriggerOnCardMoved", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
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
                if (HasMoveTriggeredAbility(ci, null, entry.Enchantment))
                    return true;
                foreach (var aura in ci.ActiveAuras)
                {
                    if (IsStashActiveIn(aura.ActiveIn))
                        return true;
                }
                return false;
            }
            var tpl = _repo.GetCard(RunState.ResolveCoreTemplateId(entry));
            if (HasMoveTriggeredAbility(null, tpl, entry.Enchantment))
                return true;
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

        // P1-39 & P0-13：扫描整个 Stash（最多 10 张）以识别原地未动的旁观光环卡或 TTriggerOnCardMoved 移动触发被动卡
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
        if (_run.CurrentEncounterCoreCard != null &&
            (_run.CurrentEncounterCoreCard.ActiveAuras.Count > 0 ||
             HasMoveTriggeredAbility(_run.CurrentEncounterCoreCard, null)))
            return true;

        foreach (var entry in _run.Cards.Values)
        {
            if (entry.Owner != ECombatantId.Player && !_run.SelectionSet.Contains(entry.InstanceId))
                continue;
            var ci = entry.CoreCard;
            if (ci != null)
            {
                if (ci.ActiveAuras.Count > 0 || ci.AuraAdd.Count > 0 || ci.AuraMult.Count > 0 || ci.AuraTags.Count > 0 ||
                    HasMoveTriggeredAbility(ci, null, entry.Enchantment))
                    return true;
            }
            else if (entry.Type is ECardType.Item or ECardType.Skill or ECardType.SocketEffect)
            {
                var tpl = _repo.GetCard(RunState.ResolveCoreTemplateId(entry));
                if (tpl?.Auras is { Count: > 0 } || HasMoveTriggeredAbility(null, tpl, entry.Enchantment))
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

            // P1-38 & D1-P2-04：按目标卡永久附魔（PermanentEnchantment）保留既有附魔，不被临时附魔误遮蔽
            EEnchantmentType? targetPermEnchantment = ResolveEntryPermanentEnchantment(fuseTarget);
            EEnchantmentType? cardPermEnchantment = ResolveEntryPermanentEnchantment(card);
            EEnchantmentType? fusedEnchantment = targetPermEnchantment ?? cardPermEnchantment;
            bool enchantmentChanged = fusedEnchantment.HasValue && fusedEnchantment != targetPermEnchantment;
            if (enchantmentChanged)
            {
                var fuseCore = EnsureCoreCard(fuseTarget);
                fuseCore.ApplyEnchantment(fusedEnchantment!.Value.ToString(), DurationTarget.Permanent, preventOverride: false);
                fuseTarget.Enchantment = ParseEnchantmentOrNull(fuseCore.Enchantment) ?? fusedEnchantment;
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
                ExecutePostActionTransitionWithSplit(o, next => PushChoiceAndEnterLevelUp(next.Events));
                return o;
            }
            _run.SelectionsMadeInCurrentContext = 0;
            ExecutePostActionTransitionWithSplit(o, next => next.Events.AddRange(DealHourEncountersAndClearActive()));
            return o;
        }

        if (_run.State == ERunState.LevelUp && (!_run.SelectionRules.CanSelectMultiple || reachedMax))
        {
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            // P1-11 & P1-12：隔离 LevelUp 选技（PlayerSkillEquipped）与跨小时（RunHourChanged->6）+ Choice CardDealt，消除 10.1s 时钟锁死
            ExecutePostActionTransitionWithSplit(o, AdvanceAfterSubState);
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
            ExecutePostActionTransitionWithSplit(o, AdvanceAfterSubState);
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
            ExecutePostActionTransitionWithSplit(o, next => ExitEncounterOrAdvance(next, nextOnExit, exitingRootId, exitingStepId));
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

        // D2-P1-07：探险房前置只读校验必须位于 OnEncounterSelected/OnCardSelected、修改 Visited* 与 ActivateEncounterCard 摘卡之前，
        // 既避免无票时产生副作用或误摘卡，又避免官方探险入口卡在 OnCardSelected 先消耗门票导致后置复检误报无票
        if (CardPool.IsExpeditionRoomTemplate(encId))
        {
            bool requireMatchingTicket = card.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0) > 0
                || (card.InstanceId?.StartsWith("exp_", StringComparison.Ordinal) == true);
            if (!_stepEngine.CanEnterExpeditionRoom(encId, requireMatchingTicket, out string? preCheckErr))
                return NotAllowed(preCheckErr ?? $"进入探险房间 {encId} 失败");
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

        // 3. Wave 4A #1 架构根治：直效/纯 XP 事件遭遇（无 SpawnContext 与 NextEncounterOnSelection，含 TTriggerOnCardSelected 或 ExperienceAwardUponSelection > 0）
        //    严禁在同包中跳过 Encounter 态直接 Choice -> Choice（会绕过客户端 TransitionOutComponent 的 VisualSequenceGate，导致
        //    BoardManager: PlayRevealAnimations skipped (spawned=True / dealt=True)，在 Hour 5 -> Hour 6 更会永久锁死 _waitForNewDayTransition 致 PvP 空白与整板锁死）。
        //    改为统一落地标准 ERunState.Encounter（保留顶栏事件肖像、CanExit=true、清空刷新态、延迟至离场结算 PendingEncounterExitXp），
        //    使所有直效发卡（如 Candy Stash 巧克力入储物箱）与纯 XP/属性收益平稳展示，玩家点击离开后由 Encounter -> Choice 走原生沙尘门控安全翻牌。
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
                o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: true));
                _run.State = ERunState.Encounter;
                _run.CurrentEncounterId = encId;
                _run.SelectionsMadeInCurrentContext = 0;
                _run.CurrentMaxSelections = null;
                _run.PendingEncounterExitXp = directXp;
                _run.CurrentEncounterCoreCard = CreateEncounterCoreCard(encId);
                _run.SelectionRules = new TSelectionContextRules
                {
                    CanSelectMultiple = false,
                    SelectionIsFree = true,
                    CanExit = true,
                    RerollRules = null,
                };
                _run.BaseRerollCost = null;
                _run.RerollCost = 0;
                _run.RerollsRemaining = 0;

                string? directBoardOverride = coreTpl?.BoardOverride ?? tpl?.BoardOverride;
                string? directPresentation = coreTpl?.Presentation ?? tpl?.Presentation;
                if (!string.IsNullOrWhiteSpace(directBoardOverride))
                {
                    _run.CurrentBoardId = directBoardOverride;
                    o.Events.Add(new GameSimEventBoardOverridden(directBoardOverride, directPresentation));
                }
                else if (!string.IsNullOrWhiteSpace(_run.CurrentBoardId) && string.IsNullOrWhiteSpace(_run.ActiveReplacementSetId))
                {
                    ResetBoardOverrideIfNeeded(o.Events);
                }

                RecalculateAurasAndSyncBoard(o.Events);
                ReconcileAndVerifyBoardInvariants("EnterDirectEncounter", o.DirtyCardIds, o.Events);
                o.Events.Add(new GameSimEventStateTransitioned(ERunState.Encounter, ResolveClientTransitionEncounterId(encId)));
                TriggerOnEncounterEnteredAndDealt(encId, o.Events);
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

        // P1-04 & D2-P1-04：若遭遇配置了 BoardOverride，持久化 CurrentBoardId 并与 StateTransitioned 同包发射 GameSimEventBoardOverridden；
        // 若进入非 BoardOverride 新遭遇且当前不在活跃替换集中，对称复位先前残余的自定义切板。
        string? encBoardOverride = coreTpl?.BoardOverride ?? tpl?.BoardOverride;
        string? encPresentation = coreTpl?.Presentation ?? tpl?.Presentation;
        if (!string.IsNullOrWhiteSpace(encBoardOverride))
        {
            _run.CurrentBoardId = encBoardOverride;
            o.Events.Add(new GameSimEventBoardOverridden(encBoardOverride, encPresentation));
        }
        else if (!string.IsNullOrWhiteSpace(_run.CurrentBoardId) && string.IsNullOrWhiteSpace(_run.ActiveReplacementSetId))
        {
            ResetBoardOverrideIfNeeded(o.Events);
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

        // D2-P1-09：消费奇遇步骤内直接 +Level（走完整升级加血/开槽/PendingLevelUps/OnPlayerLeveledUp 闭环）
        if (stepResult.DirectLevelsGained > 0)
        {
            ApplyDirectLevelUps(stepResult.DirectLevelsGained, o.Events, o.DirtyCardIds);
        }

        // D2-P1-09：若步骤内扣声望跌至 <= 0，统一进入 Fates 濒死救援或判负终局闭环
        if (stepResult.PrestigeReachedZero && !_runEnded)
        {
            int curPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0);
            if (curPrestige <= _mode.FatesSpawnAtOrBelowPrestige && TryTriggerFatesOrEndDefeatFromStep(o))
            {
                return o;
            }
        }

        // 1. 存在多步链式下一跳遭遇（子奇遇 / 基座 / 直接触发野怪战斗）
        if (!string.IsNullOrEmpty(stepResult.NextEncounterId))
        {
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            // P1-11：若本步赐技/生卡（CardSpawned）且下一跳会发卡（CardDealt），拆包隔离避免客户端并发重入
            ExecutePostActionTransitionWithSplit(o, next => TransitionToEncounterById(stepResult.NextEncounterId, next));
            return o;
        }

        // 2. 若当场发出了新的替代卡（DealCards / Reroll）则重置本轮计数并刷新上限，保持在当前遭遇内继续选择，同时确保顶栏父遭遇卡锚定存在
        if (stepResult.DealtNewCards)
        {
            EnsureActiveParentPortraitCardAnchored(o.Events);
            _run.SelectionsMadeInCurrentContext = 0;
            // D2-P1-03：刷新新上下文上限，彻底清除旧步残留的 _run.CurrentMaxSelections
            if (stepResult.MaxSelections.HasValue)
                _run.CurrentMaxSelections = stepResult.MaxSelections.Value;
            else if (_run.SelectionRules.SelectionIsFree && _run.SelectionRules.CanSelectMultiple && _run.SelectionSet.Count > 0)
                _run.CurrentMaxSelections = _run.SelectionSet.Count;
            else
                _run.CurrentMaxSelections = null;
            TriggerOnEncounterCardsDealtOnly(_run.CurrentEncounterId, o.Events);
            return o;
        }

        // D2-P1-03：先消耗非重选步骤卡，再依据剩余 SelectionSet.Count 判定是否留场——杜绝最后一张卡删完后停在空货架死锁
        if (!stepResult.IsReselectable && _run.SelectionSet.Contains(stepEntry.InstanceId))
        {
            _run.SelectionSet.Remove(stepEntry.InstanceId);
            _run.Cards.Remove(stepEntry.InstanceId);
            o.Events.Add(new GameSimEventCardDisposed(stepEntry.InstanceId, ECardDisposalReason.Cleared));
        }

        bool reachedMax = _shop.HasReachedMaxSelections(_run.SelectionsMadeInCurrentContext);
        if (!stepResult.ExitedEncounter && !reachedMax && _run.SelectionSet.Count > 0 &&
            (stepResult.IsReselectable || _run.SelectionRules.CanSelectMultiple))
        {
            EnsureActiveParentPortraitCardAnchored(o.Events);
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
                ExecutePostActionTransitionWithSplit(o, next => PushChoiceAndEnterLevelUp(next.Events));
                return o;
            }
            _run.SelectionsMadeInCurrentContext = 0;
            ExecutePostActionTransitionWithSplit(o, next => next.Events.AddRange(DealHourEncountersAndClearActive()));
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
            // D2-P1-04：离开带 BoardOverride 遭遇且不在活跃替换集中时对称复位切板
            if (stepResult.ExitedReplacementSet || string.IsNullOrWhiteSpace(_run.ActiveReplacementSetId))
                ResetBoardOverrideIfNeeded(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            // P1-11（BUG-V1B-02）：升级步骤赐技（如 Inspired by Karnok）产生 CardSpawned(Skill) + PlayerSkillEquipped，
            // 严禁与后续 Choice CardDealt 同包混发，统一拆为两条独立 NetMessageGameSim 杜绝技能栏 7 分身爆炸
            ExecutePostActionTransitionWithSplit(o, AdvanceAfterSubState);
            return o;
        }

        string? exitingStepId = _run.CurrentEncounterId;
        string? exitingRootId = _run.ParentEncounterTemplateId ?? exitingStepId;
        bool isExitingFates = IsFatesEncounter(exitingRootId) || IsFatesEncounter(exitingStepId);
        TriggerOnEncounterExited(_run.CurrentEncounterId, o.Events);
        ApplyWillAutoSellOnExitIfNeeded(o.Events);
        FlushPendingEncounterExitXp(o.Events);
        if (stepResult.ExitedReplacementSet || string.IsNullOrWhiteSpace(_run.ActiveReplacementSetId))
            ResetBoardOverrideIfNeeded(o.Events);
        _run.CurrentEncounterCoreCard = null;
        o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));

        // P1-12（RC-HOUR6-FATES-LEVELUP-CLOCK-LOCK）：Fates 在 Hour >= HoursInADay（Hour == 6）结算获得 XP 触发升级时，
        // 严禁在 Hour == 6 当场消费 PendingLevelUp 进入 LevelUp，改为先完成跨天（Day+1, Hour=1）再于新日首态安全消费！
        if (isExitingFates && _run.Hour >= (uint)_mode.HoursInADay && _run.SuspendedStates.Count == 0)
        {
            ExecutePostActionTransitionWithSplit(o, next => AdvanceHourOrDay(next.Events, justCompletedPvp: true));
            return o;
        }

        if (stepResult.ShouldProgressHour && ShouldProgressHourOnExit(exitingRootId, exitingStepId))
        {
            if (_run.SuspendedStates.Count > 0 || _run.PendingLevelUp)
            {
                ExecutePostActionTransitionWithSplit(o, AdvanceAfterSubState);
            }
            else
            {
                // 若即将进入下一小时且经验已满足升级：AdvanceHour 内部会在新小时扣收入/经验后压栈 Choice 并切入 LevelUp
                ExecutePostActionTransitionWithSplit(o, AdvanceHour);
            }
            return o;
        }

        // ShouldProgressHour == false（如 Thieves Guild / 免费瞬时事件且无下一跳）→ 不消耗小时数，原地重开当前小时 Choice
        ExecutePostActionTransitionWithSplit(o, next => HandleNoHourProgressEncounterExit(next, exitingRootId, exitingStepId));
        return o;
    }

    private bool IsFatesEncounter(string? encId)
    {
        if (string.IsNullOrWhiteSpace(encId)) return false;
        string fatesTemplateId = !string.IsNullOrWhiteSpace(_mode.FatesTemplateId)
            ? _mode.FatesTemplateId
            : "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
        return string.Equals(encId, fatesTemplateId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(encId, "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// P1-11 &amp; P1-12：拆包隔离守卫。
    /// 当命令前半段已发射 CardSpawned（如升级/奇遇赐技、离场赐技）或 PlayerSkillEquipped，
    /// 且后半段状态推进将下发 CardDealt（含 Choice 遭遇发牌或跨小时 RunHourChanged + CardDealt）时，
    /// 复用 PreSplitGameSim + SecondaryGameSimEvents 拆分为两条独立 NetMessageGameSim：
    /// 1) 阻断 BoardManager.SpawnCards 在 OnCardSpawned 与 OnCardDealt 同帧并发重入导致的技能栏克隆爆炸（P1-11）；
    /// 2) 阻断 LevelUp 选技（PlayerSkillEquipped）与跨小时（RunHourChanged->6）+ CardDealt 混包引发的 10.1s 时钟锁死（P1-12）。
    /// </summary>
    private void ExecutePostActionTransitionWithSplit(Outcome o, Action<Outcome> transitionAction)
    {
        bool hasPreCardSpawned = o.Events.Any(e => e is GameSimEventCardSpawned);
        bool hasPreSkillEquipped = o.Events.Any(e => e is GameSimEventPlayerSkillEquipped);
        if ((!hasPreCardSpawned && !hasPreSkillEquipped) || o.PreSplitGameSim != null)
        {
            transitionAction(o);
            return;
        }

        var preExtraCards = o.ExtraSimCards.Count > 0 ? o.ExtraSimCards : null;
        var preSplitSnapshot = _run.BuildFullGameSim(o.Events, preExtraCards);

        var nextOutcome = new Outcome();
        transitionAction(nextOutcome);

        // 若后半段直接进入真实三段式战斗（CombatSpawnSim/Combat/CombatDespawnSim），保留完整战斗序列不拆散
        if (nextOutcome.CombatSpawnSim != null || nextOutcome.Combat != null || nextOutcome.CombatDespawnSim != null)
        {
            o.Events.AddRange(nextOutcome.Events);
            o.CombatSpawnSim = nextOutcome.CombatSpawnSim;
            o.Combat = nextOutcome.Combat;
            o.CombatDespawnSim = nextOutcome.CombatDespawnSim;
            foreach (string id in nextOutcome.DirtyCardIds) o.DirtyCardIds.Add(id);
            if (nextOutcome.ExtraSimCards.Count > 0) o.ExtraSimCards.AddRange(nextOutcome.ExtraSimCards);
            o.ForceStateSync |= nextOutcome.ForceStateSync;
            o.AppendStateSyncAtTail |= nextOutcome.AppendStateSyncAtTail;
            return;
        }

        bool nextHasCardDealt = nextOutcome.Events.Any(e => e is GameSimEventCardDealt);
        bool nextHasHourOrDayAdvance = nextOutcome.Events.Any(e =>
            e is GameSimEventRunHourChanged or GameSimEventRunDayChanged);
        bool nextEndsInChoice = _run.State == ERunState.Choice;

        bool shouldSplit = nextOutcome.Events.Count > 0 && (
            // P1-11：凡前置含 CardSpawned 且后段含 CardDealt 一律拆包，彻底禁止同包混发
            (hasPreCardSpawned && nextHasCardDealt)
            // P1-12：凡前置含 PlayerSkillEquipped 且后段跨小时/跨天发牌或切回 Choice 发牌一律拆包
            || (hasPreSkillEquipped && nextHasCardDealt && (nextHasHourOrDayAdvance || nextEndsInChoice)));

        if (shouldSplit)
        {
            o.PreSplitGameSim = preSplitSnapshot;
            o.SecondaryGameSimEvents = nextOutcome.Events;
            foreach (string id in nextOutcome.DirtyCardIds) o.DirtyCardIds.Add(id);
            if (nextOutcome.ExtraSimCards.Count > 0) o.ExtraSimCards.AddRange(nextOutcome.ExtraSimCards);
            o.ForceStateSync |= nextOutcome.ForceStateSync;
            o.AppendStateSyncAtTail |= nextOutcome.AppendStateSyncAtTail;
        }
        else
        {
            o.Events.AddRange(nextOutcome.Events);
            foreach (string id in nextOutcome.DirtyCardIds) o.DirtyCardIds.Add(id);
            if (nextOutcome.ExtraSimCards.Count > 0) o.ExtraSimCards.AddRange(nextOutcome.ExtraSimCards);
            o.ForceStateSync |= nextOutcome.ForceStateSync;
            o.AppendStateSyncAtTail |= nextOutcome.AppendStateSyncAtTail;
        }
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
            int curMaxHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, GameDefaults.StartingHealth);
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
        bool isExitingFates = IsFatesEncounter(exitingRootId) || IsFatesEncounter(exitingEncId);
        if (!hasNextEnc && isExitingFates && _run.Hour >= (uint)_mode.HoursInADay && _run.SuspendedStates.Count == 0)
        {
            // P1-12：从 Fates 退出且处于 Hour >= HoursInADay 时先完成跨天（Day+1, Hour=1），绝不在 Hour == 6 直接进入 LevelUp
            ExecutePostActionTransitionWithSplit(o, next => AdvanceHourOrDay(next.Events, justCompletedPvp: true));
            return o;
        }
        // P1-11：若 TriggerOnEncounterExited 赐予了新技能/新卡，拆包隔离 CardSpawned 与下一阶段 CardDealt
        ExecutePostActionTransitionWithSplit(o, next => ExitEncounterOrAdvance(next, nextOnExit, exitingRootId, exitingEncId));
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
            // P1-12：若已处于 Hour >= HoursInADay 且已完成当日 PvP（或刚结束 Fates），严禁在 Hour == 6 进入 LevelUp，改为先跨天至 Day+1 Hour 1
            if (_run.Hour >= (uint)_mode.HoursInADay &&
                (HasCompletedPvpForCurrentDay() || !RequiresPvpOnDay(_run.Day) || IsFatesEncounter(rootEncId) || IsFatesEncounter(currentEncId)))
            {
                AdvanceHourOrDay(o.Events, justCompletedPvp: true);
                return;
            }
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
            string? boardBeforePop = _run.CurrentBoardId;
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

            // D2-P1-04：恢复挂起栈持久化 CurrentBoardId，仅当与弹栈前不同时发射 BoardOverridden
            if (!string.Equals(boardBeforePop ?? "", restored.CurrentBoardId ?? "", StringComparison.OrdinalIgnoreCase))
            {
                _run.CurrentBoardId = restored.CurrentBoardId;
                events.Add(new GameSimEventBoardOverridden(restored.CurrentBoardId, null));
            }
            else
            {
                _run.CurrentBoardId = restored.CurrentBoardId;
            }

            // D2-P1-14：恢复挂起栈内分页怪物战利品状态
            if (restored.MonsterLootPages.Count > 0)
            {
                _run.MonsterLootPages.Clear();
                foreach (var page in restored.MonsterLootPages)
                    _run.MonsterLootPages.Add(new List<RunState.MonsterLootPageItemState>(page));
                _run.MonsterLootPageIndex = Math.Clamp(restored.MonsterLootPageIndex, 0, _run.MonsterLootPages.Count - 1);
                _shop.RestoreMonsterLootPagesFromRun();
            }

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
                _run.ActiveShelfOriginalPrices.Clear();
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
                RestoreSuspendedShelfDiscounts(restored.ActiveShelfDiscounts, restored.ActiveShelfOriginalPrices, idRemap);
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
                RestoreSuspendedShelfDiscounts(restored.ActiveShelfDiscounts, restored.ActiveShelfOriginalPrices, idRemap);
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
                _run.ActiveShelfOriginalPrices.Clear();
                _shop.RestoreActiveShelfDiscountsFromRun();
                _run.SelectionsMadeInCurrentContext = 0;
                events.AddRange(DealHourEncountersAndClearActive());
            }
            else
            {
                RestoreSuspendedShelfDiscounts(restored.ActiveShelfDiscounts, restored.ActiveShelfOriginalPrices, idRemap);
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
        // Wave 4A #1：清除延迟推进至 Hour 6 PvP 标记，继续完成延后的 AdvanceHourOrDay（此时所有连升已清空，安全切至 Hour 6 Choice）
        if (_run.PendingAdvanceHourAfterLevelUp)
        {
            _run.PendingAdvanceHourAfterLevelUp = false;
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

        LockedPvpBoard? selfLockedBoard = null;
        if (isPvp && tourneyLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId))
        {
            // P0-4 / P0-40 并发安全与对称性：进入 PvP 前先将本方当前盘面不可变锁入 ITournamentPvpCoordinator 并取回权威不可变快照
            if (tourneyLobby.TournamentSessionId is Guid lockSid && lockSid != Guid.Empty)
            {
                var proposedSelfBoard = BuildLockedPvpBoardSnapshot();
                _pvpCoordinator?.LockPlayerBoardForDay(lockSid, _run.Day, proposedSelfBoard);
                selfLockedBoard = _pvpCoordinator?.TryGetLockedBoard(lockSid, _run.Day, _run.AccountId) ?? proposedSelfBoard;
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
        string ghostPartition = PvpGhostPool.ResolveGhostPoolPartitionLocal(
            _run.CustomRules,
            PvpGhostPool.HasTrueCustomCard(_run),
            _modHost);

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
                EHero oppHero = PvpGhostPool.ParseHero(ghost.Hero);
                // Wave 4A #1 (H-1) & #11：非锦标赛幽灵 accountId 嵌入 Day 与 BoardId 确保跨天必异触发客户端原生 PvpOpponentChanged；
                // 名牌展示去除 ({ghost.PowerBucket}) 英文后缀，仅下发干净英雄名（锦标赛保留 OpponentUsername）。
                string oppAccountId = tourneyAssignment?.OpponentAccountId
                    ?? (canUseLockedGhost ? _run.CurrentPvpOpponent?.PlayerLoadout?.accountId : null)
                    ?? $"ghost_{oppHero}_d{_run.Day}_{(!string.IsNullOrWhiteSpace(ghost.BoardId) ? ghost.BoardId : Guid.NewGuid().ToString("N"))}";
                string oppDisplayName = tourneyAssignment != null
                    ? tourneyAssignment.OpponentUsername
                    : (string.IsNullOrWhiteSpace(ghost.Hero) ? "Challenger" : ghost.Hero);
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

        // P3-08：统一剔除敌方（野怪/BossRush/镜像）中 SpawningEligibility=="Never" 且 ArtKey 为空的官方调试技能，根治客户端空路径加载报错
        if (p2Core.Skills.RemoveAll(s => MonsterPool.IsBlockedDebugOrBlankArtSkill(s.Template)) > 0)
            p2Core.PrepareForCombat();

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

        // 记录本场战斗敌方（PvP 幽灵/锦标赛对手/自定义野怪）所有自定义 Mod 物品与技能的实例映射，
        // 让客户端 /api/mods/active-bindings 在战斗中与战后复盘均能正确呈现对手的自定义贴图、文案、预览与数值徽章。
        bool hadPrevOppCustom = _run.ActiveCombatOpponentCustomCardIds.Count > 0;
        _run.ActiveCombatOpponentCustomCardIds.Clear();
        for (int i = 0; i < oppEntries.Count; i++)
        {
            var oe = oppEntries[i];
            if (!string.IsNullOrWhiteSpace(oe.InstanceId) && !string.IsNullOrWhiteSpace(oe.CustomCardId))
            {
                _run.ActiveCombatOpponentCustomCardIds[oe.InstanceId] = oe.CustomCardId!;
            }
        }
        if (hadPrevOppCustom || _run.ActiveCombatOpponentCustomCardIds.Count > 0)
        {
            _run.VisualBindingsRevision++;
        }

        _lastCombatMonster = monster;

        // 4. 构造模式/词条/无尽/BossRush 战斗修正器并执行 BazaarCore 完整规则战斗模拟（50ms 采样直降内存与包体积）
        bool isBossRush = _run.CustomRules.SpecialModeId == ESpecialModeId.BossRush;
        Guid symResolveSid = tourneyLobby?.TournamentSessionId ?? Guid.Empty;
        bool isSymmetricHumanPvp = isPvp
            && tourneyLobby != null
            && symResolveSid != Guid.Empty
            && tourneyAssignment != null
            && !tourneyAssignment.OpponentIsBot
            && lockedOppBoard != null
            && selfLockedBoard != null
            && _pvpCoordinator != null
            && !string.IsNullOrWhiteSpace(_run.AccountId);

        int? endlessGhostSourceDay = (isPvp && !isSymmetricHumanPvp)
            ? _run.PendingPvpGhost?.EffectiveSourceDay
            : null;
        var combatModifiers = CombatModifiers.FromRulesAndDay(
            _run.CustomRules,
            (int)_run.Day,
            isBossRush: isBossRush,
            isPve: !isPvp,
            bossRushWave: (int)_run.Day,
            isSymmetricHumanPvp: isSymmetricHumanPvp,
            endlessGhostSourceDay: endlessGhostSourceDay,
            customMutators: GetEffectiveCustomMutators());
        bool sandstormEnabled = (encTpl?.SandstormEnabled ?? true) && combatModifiers.SandstormEnabled;

        // P0-22（合流胶水 #4）：自定义模式钩子战前修正回调（异常隔离）
        var combatModeHook = ResolveActiveGameModeHook();
        if (combatModeHook != null)
        {
            try
            {
                combatModeHook.OnCombatModifiersBuilt(combatModifiers, (int)_run.Day, isPvp);
                combatModeHook.OnCombatStarting(p1Core, p2Core, ref sandstormEnabled);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RunEngine] IGameModeHook({combatModeHook.ModeId}).OnCombat* failed: {ex.Message}");
            }
        }

        // P1-05：在 CombatSimulator.Simulate 之前先快照对手开战初值（避免模拟过程中修改 p2Core.Attributes 污染开战快照）
        _combatTransientCustomCardIds.Clear();
        var p2InitialAttributes = new Dictionary<string, double>(p2Core.Attributes, StringComparer.OrdinalIgnoreCase);

        CombatResult result;
        CombatSim? prebuiltSymmetricSim = null;
        bool selfIsCanonicalP1 = true;
        bool win;
        bool isSyncDraw = false;
        const string SyncDrawWinnerSentinel = "__draw__";

        if (isSymmetricHumanPvp)
        {
            // P0-40：锦标赛真人同步 PvP 绝对对称管线：
            // 1) 统一大小写归一化账号键决定规范顺序（消除 Ordinal vs OrdinalIgnoreCase 反转冲突）；
            // 2) 双端严格从不可变 LockedPvpBoard.RunSnapshotJson 重建 (canonP1, canonP2)，固定 PlayerId = "player" / "opponent"；
            // 3) 先对双端完成 EnsureUniqueCardIds 再构建双向映射表 p1ToP2CardIdMap；
            // 4) 仅跑唯一规范模拟 + FlipPerspective 生成镜像动画并写入 CachedCombatSimForP1/P2；
            // 5) 后手方命中缓存零重复生成动画，并用同一对不可变锁板跑轻量无时间轴规范模拟提取 permSync，战后仍回写至活局 p1Core！
            string selfNormKey = NormalizeAccountIdKey(_run.AccountId);
            string oppNormKey = NormalizeAccountIdKey(tourneyAssignment!.OpponentAccountId);
            selfIsCanonicalP1 = StringComparer.OrdinalIgnoreCase.Compare(selfNormKey, oppNormKey) <= 0;

            string canonicalP1Id = selfIsCanonicalP1 ? _run.AccountId : tourneyAssignment.OpponentAccountId;
            string canonicalP2Id = selfIsCanonicalP1 ? tourneyAssignment.OpponentAccountId : _run.AccountId;
            LockedPvpBoard canonP1Board = selfIsCanonicalP1 ? selfLockedBoard! : lockedOppBoard!;
            LockedPvpBoard canonP2Board = selfIsCanonicalP1 ? lockedOppBoard! : selfLockedBoard!;

            var canonP1 = BuildPlayerStateFromLockedBoard(
                canonP1Board,
                simPlayerId: "player",
                cardIdPrefix: "",
                out var canonP1Entries,
                out var canonP1CoreMap);
            var canonP2 = BuildPlayerStateFromLockedBoard(
                canonP2Board,
                simPlayerId: "opponent",
                cardIdPrefix: "opp_",
                out var canonP2Entries,
                out var canonP2CoreMap);

            // 先对双端完成唯一 ID 保障，再构建双射映射表 p1ToP2CardIdMap（战中新生成未在表内 ID 将由 FlipPerspective 原样透传）
            EnsureUniquePlayerCardIds(canonP1, "p1");
            EnsureUniquePlayerCardIds(canonP2, "opp");
            RefreshBoardEntriesAndMapFromPlayer(canonP1, ECombatantId.Player, canonP1Entries, canonP1CoreMap);
            RefreshBoardEntriesAndMapFromPlayer(canonP2, ECombatantId.Opponent, canonP2Entries, canonP2CoreMap);
            var p1ToP2CardIdMap = BuildSymmetricCardIdRemap(canonP1, canonP2);

            static CombatSim? TryReadCachedCombatSim(byte[]? cachedBytes)
            {
                if (cachedBytes == null || cachedBytes.Length == 0) return null;
                try
                {
                    if (BazaarServer.Protocol.Codec.ReadMessage(cachedBytes) is NetMessageCombatSim msg)
                        return msg.Data;
                }
                catch { }
                return null;
            }

            var resolved = _pvpCoordinator!.TryGetResolvedMatch(
                symResolveSid,
                _run.Day,
                _run.AccountId,
                tourneyAssignment.OpponentAccountId);

            if (resolved != null)
            {
                int authSeed = resolved.BattleSeed != 0 ? resolved.BattleSeed : combatSeed;
                byte[]? cachedBytes = selfIsCanonicalP1 ? resolved.CachedCombatSimForP1 : resolved.CachedCombatSimForP2;
                prebuiltSymmetricSim = TryReadCachedCombatSim(cachedBytes);

                if (prebuiltSymmetricSim != null)
                {
                    // 后手命中缓存：使用完全相同的双端不可变锁板 (canonP1, canonP2) + authSeed 跑轻量规范模拟仅提取 permSync / TemporaryTransforms / PostCombatOps
                    result = CombatSimulator.Simulate(
                        _repo,
                        canonP1,
                        canonP2,
                        seed: authSeed,
                        combatType: "PVP",
                        recordTimeline: false,
                        recordEvents: false,
                        syncPermanentChanges: false,
                        modifiers: combatModifiers,
                        sandstormEnabled: sandstormEnabled,
                        mechanicDefinitionsOverride: _modRuntimeContext?.MechanicsById,
                        pipelineInterceptorsOverride: _modRuntimeContext?.PipelineInterceptors);
                }
                else
                {
                    // 服务重启 DB 回源缓存为空：按双端不可变锁板 + authSeed + 规范序确定性重跑并精确镜像
                    result = CombatSimulator.Simulate(
                        _repo,
                        canonP1,
                        canonP2,
                        seed: authSeed,
                        combatType: "PVP",
                        recordTimeline: true,
                        recordEvents: true,
                        syncPermanentChanges: false,
                        modifiers: combatModifiers,
                        sandstormEnabled: sandstormEnabled,
                        timelineSampleIntervalMs: CombatSimConverter.FrameStepMs,
                        mechanicDefinitionsOverride: _modRuntimeContext?.MechanicsById,
                        pipelineInterceptorsOverride: _modRuntimeContext?.PipelineInterceptors);

                    bool resolvedIsDraw = string.IsNullOrEmpty(resolved.WinnerAccountId)
                        || string.Equals(resolved.WinnerAccountId, SyncDrawWinnerSentinel, StringComparison.OrdinalIgnoreCase);
                    var simForP1 = CombatSimConverter.ToCombatSim(
                        result, _repo, canonP1CoreMap, canonP2CoreMap, canonP1Entries, canonP2Entries,
                        pveGoldReceived: 0,
                        pveXpReceived: 0,
                        isDrawOverride: resolvedIsDraw,
                        resolveWireTemplateGuid: ResolveCombatWireTemplateGuid,
                        onRuntimeCardTransformed: OnCombatRuntimeCardTransformed);
                    prebuiltSymmetricSim = selfIsCanonicalP1
                        ? simForP1
                        : CombatSimConverter.FlipPerspective(simForP1, p1ToP2CardIdMap);
                }
            }
            else
            {
                // 先手方首次结算：唯一规范模拟 + 一次性生成双端严格镜像动画并随裁决写入缓存
                result = CombatSimulator.Simulate(
                    _repo,
                    canonP1,
                    canonP2,
                    seed: combatSeed,
                    combatType: "PVP",
                    recordTimeline: true,
                    recordEvents: true,
                    syncPermanentChanges: false,
                    modifiers: combatModifiers,
                    sandstormEnabled: sandstormEnabled,
                    timelineSampleIntervalMs: CombatSimConverter.FrameStepMs,
                    mechanicDefinitionsOverride: _modRuntimeContext?.MechanicsById,
                    pipelineInterceptorsOverride: _modRuntimeContext?.PipelineInterceptors);

                bool canonicalIsDraw = result.IsDraw
                    || (result.Player1RemainingHp <= 0 && result.Player2RemainingHp <= 0)
                    || (result.Player1RemainingHp > 0 && result.Player2RemainingHp > 0);
                bool canonicalP1Won = !canonicalIsDraw &&
                    string.Equals(result.WinnerPlayerId, "player", StringComparison.OrdinalIgnoreCase);

                var simForP1 = CombatSimConverter.ToCombatSim(
                    result, _repo, canonP1CoreMap, canonP2CoreMap, canonP1Entries, canonP2Entries,
                    pveGoldReceived: 0,
                    pveXpReceived: 0,
                    isDrawOverride: canonicalIsDraw,
                    resolveWireTemplateGuid: ResolveCombatWireTemplateGuid,
                    onRuntimeCardTransformed: OnCombatRuntimeCardTransformed);
                var simForP2 = CombatSimConverter.FlipPerspective(simForP1, p1ToP2CardIdMap);

                byte[] bytesForP1 = BazaarServer.Protocol.Codec.WriteMessage(new NetMessageCombatSim(simForP1));
                byte[] bytesForP2 = BazaarServer.Protocol.Codec.WriteMessage(new NetMessageCombatSim(simForP2));

                string winnerId = canonicalIsDraw ? SyncDrawWinnerSentinel : (canonicalP1Won ? canonicalP1Id : canonicalP2Id);
                string loserId = canonicalIsDraw ? SyncDrawWinnerSentinel : (canonicalP1Won ? canonicalP2Id : canonicalP1Id);

                var proposed = new SymmetricHumanPvpOutcome(
                    WinnerAccountId: winnerId,
                    LoserAccountId: loserId,
                    BattleSeed: combatSeed,
                    LoserPrestigeDamage: canonicalIsDraw ? 0 : (int)_run.Day,
                    ReplayId: $"pvp_{symResolveSid:N}_d{_run.Day}_{combatSeed}",
                    CachedCombatSimForP1: bytesForP1,
                    CachedCombatSimForP2: bytesForP2);
                resolved = _pvpCoordinator.RecordOrGetResolvedMatch(
                    symResolveSid,
                    _run.Day,
                    _run.AccountId,
                    tourneyAssignment.OpponentAccountId,
                    proposed);

                byte[]? authoritativeBytes = selfIsCanonicalP1 ? resolved.CachedCombatSimForP1 : resolved.CachedCombatSimForP2;
                prebuiltSymmetricSim = TryReadCachedCombatSim(authoritativeBytes)
                    ?? (selfIsCanonicalP1 ? simForP1 : simForP2);
            }

            isSyncDraw = string.IsNullOrEmpty(resolved.WinnerAccountId)
                || string.Equals(resolved.WinnerAccountId, SyncDrawWinnerSentinel, StringComparison.OrdinalIgnoreCase);
            win = !isSyncDraw && string.Equals(resolved.WinnerAccountId, _run.AccountId, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // W1-A P0-17 接缝：透传局级不可变 ModRuntimeContext，彻底切断向全局静态 ModRuntimeHooks 串台回退
            result = CombatSimulator.Simulate(
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
                timelineSampleIntervalMs: CombatSimConverter.FrameStepMs,
                mechanicDefinitionsOverride: _modRuntimeContext?.MechanicsById,
                pipelineInterceptorsOverride: _modRuntimeContext?.PipelineInterceptors);

            win = string.Equals(result.WinnerPlayerId, "player", StringComparison.OrdinalIgnoreCase);
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
        _run.OpponentHero = pvpInfo?.Hero ?? EHero.Common;
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

        // P1-03（RC-COMBAT-SPAWN-HP-MULT-MISSING）+ Wave 4A #5：
        // 优先以 Timeline[0] 扣除 TimeMs==0 瞬发事件后的开战初值（经 DailyChallenge titan_vitality ×2.5 / glass_cannon / EndlessTower / Mod 倍率缩放）
        // 同步双方 Health / HealthMax 与静态上限（RageMax / EnragedDurationMax / TempoGainCooldownMax），
        // 其中玩家侧仅覆写 CombatSpawnSim 快照，严禁污染 _run.PlayerAttributes / BasePlayerAttributes（确保战后回店还原局外原值）。
        bool flipTimelinePerspective = isSymmetricHumanPvp && !selfIsCanonicalP1;
        int? combatSpawnPlayerMaxHp = null;
        int? combatSpawnPlayerHp = null;
        if (result.Timeline.Count > 0)
        {
            var initialBaseline = CombatSimConverter.CreateInitialPlayerBaseline(result.Timeline[0], result.Events);
            int selfInitMaxHp = flipTimelinePerspective ? initialBaseline.P2MaxHp : initialBaseline.P1MaxHp;
            int selfInitHp = flipTimelinePerspective ? initialBaseline.P2Hp : initialBaseline.P1Hp;
            int oppInitMaxHp = flipTimelinePerspective ? initialBaseline.P1MaxHp : initialBaseline.P2MaxHp;
            int oppInitHp = flipTimelinePerspective ? initialBaseline.P1Hp : initialBaseline.P2Hp;
            int oppInitRageMax = flipTimelinePerspective ? initialBaseline.P1RageMax : initialBaseline.P2RageMax;
            int oppInitEnragedDurMax = flipTimelinePerspective ? initialBaseline.P1EnragedDurationMax : initialBaseline.P2EnragedDurationMax;
            int oppInitTempoCdMax = flipTimelinePerspective ? initialBaseline.P1TempoGainCooldownMax : initialBaseline.P2TempoGainCooldownMax;

            if (oppInitMaxHp > 0)
            {
                _run.OpponentAttributes[EPlayerAttributeType.HealthMax] = oppInitMaxHp;
                _run.OpponentAttributes[EPlayerAttributeType.Health] = oppInitHp > 0
                    ? Math.Min(oppInitHp, oppInitMaxHp)
                    : oppInitMaxHp;
            }
            if (selfInitMaxHp > 0)
            {
                combatSpawnPlayerMaxHp = selfInitMaxHp;
                combatSpawnPlayerHp = selfInitHp > 0
                    ? Math.Min(selfInitHp, selfInitMaxHp)
                    : selfInitMaxHp;
            }

            _run.OpponentAttributes[EPlayerAttributeType.RageMax] = oppInitRageMax;
            if (oppInitEnragedDurMax > 0)
                _run.OpponentAttributes[EPlayerAttributeType.EnragedDurationMax] = oppInitEnragedDurMax;
            if (oppInitTempoCdMax > 0)
                _run.OpponentAttributes[EPlayerAttributeType.TempoGainCooldownMax] = oppInitTempoCdMax;
        }

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
        if (combatSpawnPlayerMaxHp is int scaledPlayerMaxHp && scaledPlayerMaxHp > 0 && o.CombatSpawnSim.Player?.Attributes != null)
        {
            o.CombatSpawnSim.Player.Attributes[EPlayerAttributeType.HealthMax] = scaledPlayerMaxHp;
            o.CombatSpawnSim.Player.Attributes[EPlayerAttributeType.Health] = combatSpawnPlayerHp ?? scaledPlayerMaxHp;
        }

        // 6. 构造第 2 条消息：CombatSim（同步真人 PvP 复用唯一规范模拟与严格镜像缓存；其余战斗常规转换）
        if (prebuiltSymmetricSim != null)
        {
            o.Combat = prebuiltSymmetricSim;
        }
        else
        {
            var p1BoardEntries = _run.Cards.Values
                .Where(c => c.Owner == ECombatantId.Player &&
                            c.Type is ECardType.Item or ECardType.Skill or ECardType.SocketEffect)
                .ToList();
            var p1ByInst = BuildSafeCoreCardMap(p1BoardEntries, "p1");
            var p2ByInst = BuildSafeCoreCardMap(oppEntries, "p2");

            o.Combat = CombatSimConverter.ToCombatSim(
                result, _repo, p1ByInst, p2ByInst, p1BoardEntries, oppEntries,
                pveGoldReceived: pveGoldForSim,
                pveXpReceived: pveXpForSim,
                isDrawOverride: isSyncDraw ? true : null,
                resolveWireTemplateGuid: ResolveCombatWireTemplateGuid,
                onRuntimeCardTransformed: OnCombatRuntimeCardTransformed);
        }

        // 战报自动入库（沙盒对局除外）：持久化双方盘面摘要、真实 TournamentSessionId/P2AccountId/平局、自定义卡视觉绑定与压缩后 CombatSim MsgPack 载荷
        string? pvpLoadoutAccId = pvpInfo?.PlayerLoadout?.accountId;
        string? replayOppAccountId = (tourneyAssignment != null && !tourneyAssignment.OpponentIsBot)
            ? tourneyAssignment.OpponentAccountId
            : (!string.IsNullOrWhiteSpace(pvpLoadoutAccId) && !pvpLoadoutAccId.StartsWith("ghost_", StringComparison.OrdinalIgnoreCase)
                ? pvpLoadoutAccId
                : null);
        SaveCombatReplayIfEligible(
            isPvp: isPvp,
            win: win,
            combatSeed: combatSeed,
            result: result,
            p1Core: p1Core,
            p2Core: p2Core,
            pvpInfo: pvpInfo,
            lobbyId: tourneyLobby?.LobbyId,
            combatSim: o.Combat,
            isDraw: isSyncDraw,
            opponentAccountId: replayOppAccountId,
            oppEntries: oppEntries);

        Console.WriteLine(
            $"[RunEngine] CombatSim done: {(isSyncDraw ? "DRAW" : (win ? "WIN" : "LOSE"))} dur={result.DurationMs}ms " +
            $"frames={o.Combat.Frames.Count} p1RemHp={result.Player1RemainingHp}/{p1Core.HealthMax} " +
            $"p2RemHp={result.Player2RemainingHp}/{p2Core.HealthMax} pveGold={pveGoldForSim} pveXp={pveXpForSim}");

        // 7. 构造第 3 条消息：DespawnMessage（清理敌方临时卡 + 战斗永久成长回写 + 临时变形还原 + OnFightEnded + 奖励/战利品）
        string? boardBeforeDespawn = _run.CurrentBoardId;
        var despawnEvents = new List<IGameSimEvent>();
        foreach (var oe in oppEntries)
            despawnEvents.Add(new GameSimEventCardDisposed(oe.InstanceId, ECardDisposalReason.Cleared));

        // P1-09（RC-PVP-DESPAWN-OPP-HP-RESET）：
        // 战后清场同步清空 OpponentHero、CurrentPvpOpponent、PendingPvpGhost 与非战斗态对手实体属性，
        // 严禁再将 OpponentAttributes 重置为 100/100 下发至客户端引爆全英雄「最大生命值 -300~-4600」虚假飘字。
        _run.ClearNonCombatOpponentState();

        // 7a. 战后永久状态回写（P0-40：严格传入持 _run.Cards[*].CoreCard 活引用的 p1Core；canonicalP2 走独立槽位并剥离 "opp_" 前缀）
        bool syncAsCanonicalP2 = isSymmetricHumanPvp && !selfIsCanonicalP1;
        string selfSimPlayerId = syncAsCanonicalP2 ? "opponent" : "player";
        string? stripPrefix = syncAsCanonicalP2 ? "opp_" : null;
        var permSync = CombatSimulator.SyncPermanentChangesBack(
            result,
            p1Core,
            syncPlayer2: syncAsCanonicalP2,
            stripSimIdPrefix: stripPrefix);
        ApplyPostCombatPermanentSync(p1Core, permSync, despawnEvents);

        // 7a-0. P1-10 / P0-40：仅消费当前玩家侧的 TemporaryTransforms，按需剥离 "opp_" 前缀
        EmitPostCombatTemporaryTransformRevertsWithOwner(
            result.TemporaryTransforms,
            despawnEvents,
            o.DirtyCardIds,
            selfSimPlayerId: selfSimPlayerId,
            stripSimIdPrefix: stripPrefix);

        // 7a-1. V14 / P0-40：开放自定义机制战后受控结转（按当前玩家侧归属校验与前缀剥离，绝不串改对手卡）
        ApplyPostCombatMechanicOps(p1Core, result.PostCombatOps, despawnEvents, stripSimIdPrefix: stripPrefix);

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
            // P0-16：权威记录本局已完成 PvP 结算的最高天数（覆盖胜/负/同步 PvP 平局及触发 Fates 复活前）
            _run.LastCompletedPvpDay = Math.Max(_run.LastCompletedPvpDay, _run.Day);
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
                _run.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, clampedPrestige);
                despawnEvents.Add(new GameSimEventPlayerPrestigeChanged(actualPrestigeDelta));
            }
        }
        else if (!win && (_mode.PrestigeLossFromMonsters > 0 ||
                 (_run.CustomRules.SpecialModeId == ESpecialModeId.BossRush && !_run.CustomRules.BossRushIncludeMilestonePvp)))
        {
            // P2-04 & D6-P1-14：PvE 战败分支接入 PrestigeLossFromMonsters > 0 以及无里程碑 PvP 的 BossRush 败场扣声望，
            // 杜绝无 PvP 模式下 Boss 战败既不扣声望也不触发 Fates/淘汰的不死局。
            int pveLoss = _mode.PrestigeLossFromMonsters > 0
                ? _mode.PrestigeLossFromMonsters
                : Math.Max(1, (int)_run.Day);
            int rawPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial);
            int clampedPrestige = Math.Max(0, rawPrestige - pveLoss);
            int actualPrestigeDelta = clampedPrestige - rawPrestige;
            if (actualPrestigeDelta != 0)
            {
                _run.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, clampedPrestige);
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
                _run.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, rescued);
                despawnEvents.Add(new GameSimEventPlayerPrestigeChanged(rescued - curPrestige));
                triggeredFates = true;
                Console.WriteLine($"[RunEngine] FATES fallback triggered! Prestige {curPrestige} -> {rescued}");
            }
            else if (_run.FatesUsed && isPvp && botPrestigeLock && !isRealHumanOpp && curPrestige <= 0)
            {
                _run.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, 1);
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

        // D6-P1-16：同步真人 PvP 平局同样回调 RecordHumanMatchResolution（使用 "__draw__" 哨兵，避免匹配器缺失本轮交手记录）
        if (isPvp && tourneyLobby != null && !string.IsNullOrWhiteSpace(_run.AccountId) &&
            tourneyAssignment != null && !tourneyAssignment.OpponentIsBot)
        {
            string winnerId = isSyncDraw
                ? "__draw__"
                : (win ? _run.AccountId : tourneyAssignment.OpponentAccountId);
            int prestigeDmg = (isSyncDraw || win) ? 0 : (int)_run.Day;
            tourneyLobby.Matchmaker.RecordHumanMatchResolution(
                _run.Day,
                _run.AccountId,
                tourneyAssignment.OpponentAccountId,
                winnerId,
                prestigeDmg,
                tourneyAssignment.BattleSeed);
        }

        // 7d. 检查胜负终局条件（含 LastHumanStandingMode & D6-P1-14 BossRush 第 10 波里程碑 PvP 必须获胜）；P1-36：triggeredFates=true 时豁免当回合天数上限判负
        string fatesTemplateId = !string.IsNullOrWhiteSpace(_mode.FatesTemplateId)
            ? _mode.FatesTemplateId
            : "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
        bool ended = TryEndRunToEventsCore(despawnEvents, justFinishedPvp: isPvp, triggeredFates: triggeredFates, lastPvpWon: isPvp ? win : false);

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
                    if (_run.PendingLevelUp && (_run.Hour < (uint)_mode.HoursInADay || _run.SuspendedStates.Count > 0))
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

                    // P1-12：若刚结束 Hour == 6 PvP 且战后获得升级经验，严禁在 Hour == 6 当场进入 LevelUp，
                    // 先走 AdvanceHourOrDay 跨天至 Day+1 Hour 1，再由新日首态安全消费 PendingLevelUp
                    if (_run.PendingLevelUp && !(isPvp || (_run.Hour >= (uint)_mode.HoursInADay && _run.SuspendedStates.Count == 0)))
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
        // D2-P1-04：折叠战斗收尾阶段可能连续产生的双 BoardOverridden 事件，确保客户端单次精确切板
        CoalesceBoardOverriddenEvents(despawnEvents, boardBeforeDespawn);
        o.CombatDespawnSim = _run.BuildFullGameSim(despawnEvents);
    }

    /// <summary>P0-40：规范化账号 ID 键（小写无横杠），确保 RunEngine 与 TournamentPvpCoordinator 排序口径 100% 一致。</summary>
    private static string NormalizeAccountIdKey(string? id)
        => string.IsNullOrWhiteSpace(id) ? "" : id.Trim().Replace("-", "").ToLowerInvariant();

    /// <summary>
    /// P0-40：统一完整锁板反序列化流水线（彻底废除阉割版 BuildOpponentPlayerStateFromRun）：
    /// 完整还原 Gold/Income/HealthMax/Health/Prestige/Level/Day/Hour/Wins/Losses/FatesUsed/ExclusionSet、
    /// 全部 PlayerAttributes 及三层修饰（Hour/Day/Aura），并按确定性序克隆 Hand/Stash（背包协同！）/Skills/SocketEffects 四容器；
    /// 不提前调 PrepareForCombat()，由 CombatSimulator.Simulate 内部 CloneForCombat 对双方一致执行。
    /// </summary>
    private PlayerState BuildSymmetricPlayerStateFromRun(
        RunState srcRun,
        string simPlayerId,
        string displayName,
        string cardIdPrefix,
        out List<RunState.CardEntry> boardEntries,
        out Dictionary<string, CardInstance> coreMap)
    {
        int hpMax = Math.Max(1, srcRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth));
        int hp = srcRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, hpMax);
        int prestige = Math.Max(0, srcRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, _mode.PrestigeInitial));
        int gold = Math.Max(0, srcRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, _mode.StartingGold));
        int level = Math.Max(1, srcRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1));
        string hero = (srcRun.Hero == EHero.Common ? EHero.Vanessa : srcRun.Hero).ToString();

        var ps = new PlayerState(simPlayerId, displayName, hero, hpMax, prestige, gold, 10)
        {
            Day = (int)Math.Max(1u, srcRun.Day),
            Hour = (int)Math.Max(1u, srcRun.Hour),
            Level = level,
            Wins = (int)srcRun.Victories,
            Losses = (int)srcRun.Defeats,
            FatesUsed = srcRun.FatesUsed,
        };
        if (srcRun.ExclusionSetInitialized)
        {
            ps.InitializeExclusionSet(null);
            foreach (string exId in srcRun.ExclusionSet)
                ps.ExclusionSet.Add(exId);
        }

        // D2-P1-08：统一走单一权威正向分层装配器，0 反除、0 双重乘算，且保留双边 100% 对称同构
        srcRun.ApplyLayeredPlayerStateTo(ps);
        ps.Hand.UnlockedMask = srcRun.UnlockedSlots > 0 ? srcRun.UnlockedSlots : GameDefaults.InitialUnlockedSlotsMask;
        ps.Stash.UnlockedMask = 1023;

        boardEntries = new List<RunState.CardEntry>();
        coreMap = new Dictionary<string, CardInstance>(StringComparer.Ordinal);
        ECombatantId sideOwner = string.Equals(simPlayerId, "player", StringComparison.OrdinalIgnoreCase)
            ? ECombatantId.Player
            : ECombatantId.Opponent;

        var orderedCards = srcRun.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player
                && c.Type is ECardType.Item or ECardType.Skill or ECardType.SocketEffect)
            .OrderBy(c => c.Type == ECardType.Item ? (c.Section == EInventorySection.Stash ? 1 : 0)
                        : c.Type == ECardType.Skill ? 2 : 3)
            .ThenBy(c => c.Socket.HasValue ? (int)c.Socket.Value : 99)
            .ThenBy(c => c.InstanceId, StringComparer.Ordinal);

        foreach (var entry in orderedCards)
        {
            string simCardId = cardIdPrefix + entry.InstanceId;
            var srcCore = entry.CoreCard;
            CardInstance clone;
            if (srcCore != null)
            {
                clone = CloneCardWithUniqueId(srcCore, simCardId);
            }
            else
            {
                var tpl = _repo.GetCard(RunState.ResolveCoreTemplateId(entry));
                if (tpl == null) continue;
                clone = new CardInstance(tpl, ShopEngine.ToCoreTier(entry.Tier), entry.Enchantment?.ToString(), simCardId);
            }

            if (entry.Type == ECardType.Skill)
            {
                ps.Skills.Add(clone);
                coreMap[clone.InstanceId] = clone;
                boardEntries.Add(BuildSymmetricBoardEntry(entry, clone, sideOwner, null, null));
            }
            else if (entry.Type == ECardType.SocketEffect)
            {
                int slot = entry.Socket.HasValue ? (int)entry.Socket.Value : 0;
                clone.StartSlot = slot;
                ps.SocketEffects.Add(clone);
                coreMap[clone.InstanceId] = clone;
                boardEntries.Add(BuildSymmetricBoardEntry(entry, clone, sideOwner, EInventorySection.Hand, (EContainerSocketId)Math.Clamp(slot, 0, 9)));
            }
            else if (entry.Section == EInventorySection.Stash)
            {
                int slot = entry.Socket.HasValue ? (int)entry.Socket.Value : 0;
                if (!ps.Stash.TryPlaceOrShift(clone, slot))
                    ps.Stash.TryAddAuto(clone);
                coreMap[clone.InstanceId] = clone;
                boardEntries.Add(BuildSymmetricBoardEntry(entry, clone, sideOwner, EInventorySection.Stash, (EContainerSocketId)Math.Clamp(clone.StartSlot, 0, 9)));
            }
            else
            {
                int slot = entry.Socket.HasValue ? (int)entry.Socket.Value : 3;
                if (!ps.Hand.TryPlaceOrShift(clone, slot))
                    ps.Hand.TryAddAuto(clone);
                coreMap[clone.InstanceId] = clone;
                boardEntries.Add(BuildSymmetricBoardEntry(entry, clone, sideOwner, EInventorySection.Hand, (EContainerSocketId)Math.Clamp(clone.StartSlot, 0, 9)));
            }
        }

        return ps;
    }

    private static RunState.CardEntry BuildSymmetricBoardEntry(
        RunState.CardEntry src,
        CardInstance clone,
        ECombatantId owner,
        EInventorySection? section,
        EContainerSocketId? socket)
        => new()
        {
            InstanceId = clone.InstanceId,
            TemplateId = src.TemplateId,
            CustomCardId = src.CustomCardId,
            Type = src.Type,
            Owner = owner,
            Section = section,
            Socket = socket,
            Size = src.Size,
            Tier = RunState.MapCoreTier(clone.Tier),
            Enchantment = Enum.TryParse<EEnchantmentType>(clone.Enchantment, true, out var cloneEnch) ? cloneEnch : null,
            State = ECardState.Alive,
            CoreCard = clone,
        };

    private static void EnsureUniquePlayerCardIds(PlayerState ps, string prefixTag)
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        int uniqSeq = 0;
        EnsureUniqueListIds(ps.Hand.Cards, prefixTag, usedIds, ref uniqSeq);
        EnsureUniqueListIds(ps.Stash.Cards, prefixTag + "st", usedIds, ref uniqSeq);
        EnsureUniqueListIds(ps.Skills, prefixTag + "skl", usedIds, ref uniqSeq);
        EnsureUniqueListIds(ps.SocketEffects, prefixTag + "se", usedIds, ref uniqSeq);
    }

    private void RefreshBoardEntriesAndMapFromPlayer(
        PlayerState ps,
        ECombatantId owner,
        List<RunState.CardEntry> entries,
        Dictionary<string, CardInstance> coreMap)
    {
        entries.Clear();
        coreMap.Clear();
        var used = new HashSet<string>(StringComparer.Ordinal);

        void AddCard(CardInstance ci, ECardType cardType, EInventorySection? sec, EContainerSocketId? sock)
        {
            if (IsEncounterTypeName(ci.Template.Type)) return;
            if (!used.Add(ci.InstanceId)) return;
            coreMap[ci.InstanceId] = ci;
            var entry = CreateTransientOpponentEntry(ci, cardType, sec, sock, new HashSet<string>(StringComparer.Ordinal));
            entry.Owner = owner;
            entries.Add(entry);
        }

        foreach (var c in ps.Hand.Cards)
            AddCard(c, ECardType.Item, EInventorySection.Hand, (EContainerSocketId)Math.Clamp(c.StartSlot, 0, 9));
        foreach (var c in ps.Stash.Cards)
            AddCard(c, ECardType.Item, EInventorySection.Stash, (EContainerSocketId)Math.Clamp(c.StartSlot, 0, 9));
        foreach (var s in ps.Skills)
            AddCard(s, ECardType.Skill, null, null);
        foreach (var se in ps.SocketEffects)
            AddCard(se, ECardType.SocketEffect, EInventorySection.Hand, (EContainerSocketId)Math.Clamp(se.StartSlot, 0, 9));
    }

    private static Dictionary<string, string> BuildSymmetricCardIdRemap(PlayerState canonP1, PlayerState canonP2)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in canonP1.EnumerateAllOwnedCards())
        {
            if (!string.IsNullOrEmpty(c.InstanceId))
                map[c.InstanceId] = "opp_" + c.InstanceId;
        }
        foreach (var c in canonP2.EnumerateAllOwnedCards())
        {
            if (string.IsNullOrEmpty(c.InstanceId)) continue;
            string stripped = c.InstanceId.StartsWith("opp_", StringComparison.Ordinal)
                ? c.InstanceId[4..]
                : c.InstanceId;
            map[c.InstanceId] = stripped;
        }
        return map;
    }

    private PlayerState BuildOpponentPlayerStateFromRun(RunState oppRun, string oppDisplayName)
        => BuildSymmetricPlayerStateFromRun(oppRun, "opponent", oppDisplayName, "opp_", out _, out _);

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

        // D2-P1-08：统一走单一权威正向分层装配器，完整同步 Hour/Day/Aura × Add/PureMult/AdditiveMult 9 层，彻底杜绝反除归零与双重乘算
        _run.ApplyLayeredPlayerStateTo(ps);
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
                // P3-08：兜底拦截 Never + 空 ArtKey 调试技能，避免异常存档或外部注入进入玩家技能栏
                if (MonsterPool.IsBlockedDebugOrBlankArtSkill(ci.Template)) continue;
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

        // 挂载当前对局流派解析器、自定义规则与已访问遭遇集合（D1-P1-06：使 TRunConditionalHasVisitedEncounter 在局外事件/光环中完整生效）
        OutOfCombatController.AttachSpawnPreferences(ps, ResolveOutOfCombatArchetypeResolver(), _run.CustomRules);
        OutOfCombatController.AttachVisitedEncounters(ps, BuildAllVisitedEncounterIds());

        // 不在这里调用 ps.PrepareForCombat()，保留局外真实 CurrentHealth 及已累计的临时/永久层；
        // CombatSimulator.Simulate 内部 CloneForCombat() 会为战斗克隆体独立 PrepareForCombat()。
        return (ps, map);
    }

    private HashSet<string> BuildAllVisitedEncounterIds()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _run.VisitedSingleSpawnEncounterIds)
            if (!string.IsNullOrEmpty(id)) visited.Add(id);
        foreach (var id in _run.VisitedDayEncounterIds)
            if (!string.IsNullOrEmpty(id)) visited.Add(id);
        foreach (var id in _run.ExpeditionVisitedRoomIds)
            if (!string.IsNullOrEmpty(id)) visited.Add(id);
        return visited;
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
        // W1-B P0-04 接缝：统一收口至 CardInstance.CloneNonCombatState，完整保留三层属性/附魔与临时品阶（删除原 ResetForCombat(null) 抹回临时品阶 Bug）
        var copy = src.CloneNonCombatState(newInstanceId);
        copy.IsFlying = src.IsFlying;
        copy.Heated = src.Heated;
        copy.Chilled = src.Chilled;
        copy.IsCombatTemporary = src.IsCombatTemporary;
        if (src.Tier != src.PermanentTier)
            copy.SetTier(src.Tier, isTemporary: true);
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

            int newMax = _run.AdjustBasePlayerAttribute(EPlayerAttributeType.HealthMax, hpInc, minBase: 1);
            _run.SetBasePlayerAttribute(EPlayerAttributeType.Health, newMax);
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

        _run.SetBasePlayerAttribute(EPlayerAttributeType.Level, lvl);
        _run.SetBasePlayerAttribute(EPlayerAttributeType.Experience, xp);

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

    /// <summary>
    /// P0-16：判定当前天数是否已完成当日 PvP 结算。
    /// - 权威主源为持久化字段 <see cref="RunState.LastCompletedPvpDay"/>（彻底根治 BossRush 里程碑日与同步 PvP 平局跨天死循环）；
    /// - 仅在非 BossRush 模式下保留 <c>Victories + Defeats &gt;= Day</c> 作为历史内存构造/GM 作弊兼容兜底，BossRush 严格仅认 <c>LastCompletedPvpDay</c>。
    /// </summary>
    private bool HasCompletedPvpForCurrentDay()
    {
        if (_run.LastCompletedPvpDay >= _run.Day)
            return true;
        if (_run.CustomRules.SpecialModeId == ESpecialModeId.BossRush)
            return false;
        return _run.Victories + _run.Defeats >= _run.Day;
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
        if (newDay && !justCompletedPvp && RequiresPvpOnDay(_run.Day) && !HasCompletedPvpForCurrentDay())
        {
            // P0-16：以持久化 LastCompletedPvpDay 替代脆弱的 Victories + Defeats < Day，根治 BossRush 里程碑日与同步 PvP 平局跨天死循环
            uint pvpHour = (uint)_mode.HoursInADay;
            Console.WriteLine($"[RunEngine][WARN] Prevented skipping Day {_run.Day} Hour {pvpHour} PvP! (LastCompletedPvpDay={_run.LastCompletedPvpDay}, V={_run.Victories}, D={_run.Defeats}, Day={_run.Day})");
            // Wave 4A #1：若此时存在待消费升级，严禁先将 Hour 推进至 HoursInADay 再进 LevelUp（否则客户端 ProcessLevelUpState 提前置死 _waitForNewDayTransition），
            // 保持旧小时 (< HoursInADay) 先进入 LevelUp，待全部连升结束后再正式推进至 Hour 6 PvP。
            if (_run.PendingLevelUp)
            {
                _run.PendingAdvanceHourAfterLevelUp = true;
                ConsumeNextPendingLevelUpToEvents(events);
                return;
            }
            _run.Hour = pvpHour;
            _run.RerollCost = 0;
            _run.RerollsRemaining = 0;
            events.Add(new GameSimEventRunHourChanged(_run.Hour));
            _run.SelectionsMadeInCurrentContext = 0;
            events.AddRange(DealHourEncountersAndClearActive());
            return;
        }

        // P0-1 根治：仅当本小时尚未通过遭遇离场/战斗/推进获取过每小时经验时才补发 ExperiencePerHour，彻底消灭每小时双份经验
        bool needAdvanceHourXp = !_run.GainedHourXpThisHour && !_run.GainedAdvanceHourXpThisHour;

        // Wave 4A #1（第二条同源致命链根治）：即将推进至 Hour 6（HoursInADay，PvP 小时）前，
        // 先结算旧小时的推进经验并检测升级：若触发升级（含连升多级），保持旧小时 (< HoursInADay) 不发 RunHourChanged(HoursInADay)，
        // 置持久化标记 PendingAdvanceHourAfterLevelUp=true 直接进入 LevelUp；全部升级完成后再由 AdvanceAfterSubStateToEvents
        // 二次调用 AdvanceHourOrDay 正式切至 Hour 6 Choice，彻底杜绝裸客户端 ProcessLevelUpState 提前设死 _waitForNewDayTransition 导致 Hour 6 空桌软锁。
        if (!newDay && nextHour == (uint)_mode.HoursInADay)
        {
            if (needAdvanceHourXp && _mode.ExperiencePerHour > 0)
            {
                _run.GainedAdvanceHourXpThisHour = true;
                needAdvanceHourXp = false;
                GrantExperienceToList(events, _mode.ExperiencePerHour, EExperienceType.Hour);
            }
            if (_run.PendingLevelUp)
            {
                _run.PendingAdvanceHourAfterLevelUp = true;
                ConsumeNextPendingLevelUpToEvents(events);
                return;
            }
        }

        uint prevHourBeforeAdvance = _run.Hour;
        _run.PendingAdvanceHourAfterLevelUp = false;
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
                        // D6-P1-14：无尽里程碑奖金统一走 MutatePlayerGold 触发 Custom_3 / OnPlayerAttributeChanged 与经济光环重算
                        MutatePlayerGold(floorScaling.MilestoneBonusGold, events, trackCustom3OnGain: true);
                        RecalculateAurasAndSyncBoard(events);
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

            // P0-22（合流胶水 #4）：跨新一天派发 IGameModeHook.OnDayStart（异常隔离）
            var dayHook = ResolveActiveGameModeHook();
            if (dayHook != null)
            {
                var dayAdj = new BazaarCore.Mods.GameModeRunSetupAdjustment();
                try
                {
                    dayHook.OnDayStart((int)_run.Day, dayAdj);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RunEngine] IGameModeHook({dayHook.ModeId}).OnDayStart failed: {ex.Message}");
                }
                if (dayAdj.StartingGoldDelta > 0)
                    MutatePlayerGold(dayAdj.StartingGoldDelta, events, trackCustom3OnGain: true);
                else if (dayAdj.StartingGoldDelta < 0)
                    _run.AdjustBasePlayerAttribute(EPlayerAttributeType.Gold, dayAdj.StartingGoldDelta, minBase: 0);
                if (dayAdj.StartingIncomeDelta != 0)
                {
                    _run.AdjustBasePlayerAttribute(EPlayerAttributeType.Income, dayAdj.StartingIncomeDelta, minBase: 0);
                    if (dayAdj.StartingIncomeDelta > 0)
                        events.Add(new GameSimEventPlayerIncomeGained((uint)dayAdj.StartingIncomeDelta));
                }
                if (dayAdj.StartingHealthDelta != 0)
                {
                    int nextHpMax = _run.AdjustBasePlayerAttribute(EPlayerAttributeType.HealthMax, dayAdj.StartingHealthDelta, minBase: 1);
                    int curHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, nextHpMax);
                    int nextHp = dayAdj.StartingHealthDelta > 0
                        ? Math.Min(nextHpMax, curHp + dayAdj.StartingHealthDelta)
                        : Math.Clamp(curHp, 0, nextHpMax);
                    _run.SetBasePlayerAttribute(EPlayerAttributeType.Health, nextHp);
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

        bool isEnteringPvpHour = !newDay && _run.Hour == (uint)_mode.HoursInADay;
        var hourEffects = new List<IGameSimEvent>();

        // 2. 新小时开始派发 TTriggerOnHourStarted（产衍生卡/涨属性/重算光环；同 (Day, Hour) 去重避免极端升级延迟重试时双发）
        var currentDayHour = (_run.Day, _run.Hour);
        if (_run.HourStartEffectsDispatchedFor != currentDayHour)
        {
            _run.HourStartEffectsDispatchedFor = currentDayHour;
            var p1Hour = BuildCorePlayerFromRun();
            var hourBatch = OutOfCombatController.OnHourStarted(
                p1Hour, _repo, _rng, clearEndOfHourModifiers: false,
                currentEncounterId: _run.CurrentEncounterId,
                activeEncounterCard: _run.CurrentEncounterCoreCard);
            ApplyOutOfCombatBatch(p1Hour, hourBatch, hourEffects, syncBoardDiffs: true, xpType: EExperienceType.Hour);
        }

        // 3. 若刚结束的旧小时尚未获得过小时推进经验，则发放固定 ExperiencePerHour（+1 XP）
        if (needAdvanceHourXp && _mode.ExperiencePerHour > 0)
        {
            GrantExperienceToList(hourEffects, _mode.ExperiencePerHour, EExperienceType.Hour);
        }

        // Wave 4A #1 极端边界保护：若进入 Hour 6 时 OnHourStarted 自身触发了升级，暂不发送 RunHourChanged(6) 且回退旧小时先进入 LevelUp，
        // 避免客户端在非 Choice 态见到 Hour == HoursInADay 提前锁死 _waitForNewDayTransition。
        if (isEnteringPvpHour && _run.PendingLevelUp)
        {
            events.AddRange(hourEffects);
            _run.Hour = prevHourBeforeAdvance;
            _run.GainedAdvanceHourXpThisHour = true;
            _run.PendingAdvanceHourAfterLevelUp = true;
            ConsumeNextPendingLevelUpToEvents(events);
            return;
        }

        events.Add(new GameSimEventRunHourChanged(_run.Hour));
        events.AddRange(hourEffects);

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
        => TryEndRunToEventsCore(events, justFinishedPvp, triggeredFates, lastPvpWon: true);

    private bool TryEndRunToEventsCore(
        List<IGameSimEvent> events,
        bool justFinishedPvp,
        bool triggeredFates,
        bool lastPvpWon)
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

        // Boss 车轮战：完成目标波次（默认 10 波）且（无里程碑 PvP 或已赢得该波次里程碑 PvP）即宣告胜利（D6-P1-14：输第 10 波里程碑 PvP 绝不误判夺冠）
        if (specialMode == ESpecialModeId.BossRush)
        {
            int targetWaves = Math.Clamp(_run.CustomRules.BossRushTotalWaves, 1, 30);
            bool needFinalPvp = RequiresPvpOnDay((uint)day);
            if (_run.BossRushWavesCleared >= targetWaves && (!needFinalPvp || (justFinishedPvp && lastPvpWon)))
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
            double mult = MutatorCatalog.CalculateTotalScoreMultiplier(rules.PresetId, rules.MutatorIds, extraMutators: GetEffectiveCustomMutators());
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

            // D6-P1-11：锦标赛优先入 "tournament" 榜，赛季键与 LeaderboardEndpoints.ResolveBoardKey 精确对齐（"s2" / "2" / UTC+8 日期）
            string modeKey = IsTournamentRun()
                ? "tournament"
                : rules.SpecialModeId switch
                {
                    ESpecialModeId.EndlessTower => "endless_tower",
                    ESpecialModeId.CrossHeroMayhem => "cross_hero",
                    ESpecialModeId.MutatorChallenge => "mutator",
                    ESpecialModeId.DailyChallenge => "daily",
                    ESpecialModeId.BossRush => "boss_rush",
                    _ => rules.PresetId == EGameModePreset.PureOfficial ? "standard" : rules.PresetId.ToString().ToLowerInvariant(),
                };
            string seasonOrDateKey = modeKey switch
            {
                "daily" => !string.IsNullOrWhiteSpace(rules.DailyChallengeDate)
                    ? rules.DailyChallengeDate!
                    : DailyChallengeSeedGenerator.GetTodayDateKeyUtc8(),
                "tournament" => "s2",
                _ => "2",
            };

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

            if (!isConceded || victories >= 4 || rules.SpecialModeId == ESpecialModeId.EndlessTower || modeKey == "tournament")
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
                    FinalBoardSummaryJson: boardJson,
                    RunId: _run.RunId,
                    ElapsedSeconds: ComputeRunElapsedSeconds(),
                    Prestige: prestigeRem));
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
                isEnchantBehavior = encName.Contains("Enchant", StringComparison.OrdinalIgnoreCase) || !CanCardUpgrade(card, out _);
            }

            if (!isEnchantBehavior)
            {
                // D2-P1-07：统一走 CanCardUpgrade（底层调用 BazaarCore CardInstance.CanUpgrade），放行 5 阶特例卡（如 Maker's Mark / 5 阶 Mod 卡），准确拦截缺高阶定义卡牌
                if (!CanCardUpgrade(card, out ETier nextTier))
                    return NotAllowed("该卡牌已达最高品阶，无法继续升阶");

                if (!string.IsNullOrEmpty(behavior?.TargetTier) &&
                    Enum.TryParse<ETier>(behavior.TargetTier, true, out var targetTier) &&
                    targetTier > card.Tier)
                {
                    var coreTargetTier = ShopEngine.ToCoreTier(targetTier);
                    if (coreCard.Template.Tiers != null && coreCard.Template.Tiers.ContainsKey(coreTargetTier.ToString()))
                    {
                        nextTier = targetTier;
                    }
                    else
                    {
                        return NotAllowed("该卡牌不支持目标升阶品阶");
                    }
                }

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
                coreCard.ApplyEnchantment(chosen.ToString(), DurationTarget.Permanent, preventOverride: false);

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
        // D1-P1-12：祭坛变形处于局外非战斗场景，opponentPlayer 必须传 null（严禁传 p1Core 污染对手语义），并透传 VisitedEncounterIds
        var txCtx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, p1Core, null, _rng)
        {
            LocalPlayerId = p1Core.PlayerId,
            CurrentDay = (int)_run.Day,
            CurrentHour = (int)_run.Hour,
            CurrentEncounterId = _run.CurrentEncounterId,
            VisitedEncounterIds = BuildAllVisitedEncounterIds(),
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

        // D1-P1-12：与 TActionCardTransform 统一调用 CopyTransformRetentionFrom + RefillAmmoToMax()，保留合法附魔/持久成长与满弹药
        bool disableEnchInherit = spawnCtx.Behaviors != null && spawnCtx.Behaviors.Any(b =>
            b.Type == "TSpawnBehaviorInheritEnchantment" && b.Inherits == false);
        replacement.CopyTransformRetentionFrom(liveCard, DurationTarget.Permanent, inheritEnchantment: !disableEnchInherit);
        replacement.RefillAmmoToMax();

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
        // P1-12（RC-HOUR6-FATES-LEVELUP-CLOCK-LOCK）：
        // 若当前已处于 Hour >= HoursInADay（Hour == 6）且非 Hour 6 Choice 桌面卖卡压栈场景（SuspendedStates 为空）
        // 且当日 PvP 已结算（含 Fates 结算与战后升级）或当日无需 PvP，严禁在 Hour == 6 当场消费 PendingLevelUp；
        // 先推进跨天至 Day+1 Hour 1，再由 AdvanceHourOrDay 末尾在新日首态安全进入 LevelUp。
        if (_run.Hour >= (uint)_mode.HoursInADay
            && _run.SuspendedStates.Count == 0
            && (HasCompletedPvpForCurrentDay() || !RequiresPvpOnDay(_run.Day) || IsFatesEncounter(_run.CurrentEncounterId)))
        {
            AdvanceHourOrDay(events, justCompletedPvp: true);
            return;
        }

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
        // P1-09（RC-PVP-DESPAWN-OPP-HP-RESET）：
        // 战后清场同步清空 OpponentHero、CurrentPvpOpponent、PendingPvpGhost 与非战斗态对手实体属性，
        // 严禁再将 OpponentAttributes 重置为 100/100 下发至客户端引爆全英雄「最大生命值 -300~-4600」虚假飘字。
        _run.ClearNonCombatOpponentState();

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
                // D2-P1-07：中断战斗重连直接终局时补记生涯（AccountStore.RecordRunCompleted 内部自带 TryMarkCareerRecorded + RecordedRunIds 双重幂等防重）
                if (!_run.IsSandboxRun &&
                    !_run.CustomRules.ShouldSkipCareerAndLeaderboard &&
                    _accounts != null &&
                    !string.IsNullOrWhiteSpace(_run.AccountId))
                {
                    var acc = _accounts.FindByAccountId(_run.AccountId);
                    if (acc != null)
                    {
                        _accounts.RecordRunCompleted(acc, _run, isConceded: false, elapsedSeconds: ComputeRunElapsedSeconds());
                    }
                }
            }
            catch { }
        }
        else
        {
            if (_run.PendingLevelUp)
            {
                ConsumeNextPendingLevelUpToEvents(dummy);
            }
            else if (_run.SuspendedStates.Count > 0)
            {
                // D2-P1-07：非终局中断战斗恢复优先弹出既有挂起栈现场，避免丢失压栈中的商店/战利品上下文
                AdvanceAfterSubStateToEvents(dummy);
            }
            else if (originalState == ERunState.Combat || !RequiresPvpOnDay(_run.Day) || HasCompletedPvpForCurrentDay())
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
        _run.PublishPublicSummary();
    }

    private int ComputeRunElapsedSeconds()
    {
        int sec = (int)Math.Round((DateTimeOffset.UtcNow - _engineAttachedAtUtc).TotalSeconds);
        return Math.Max(1, sec);
    }

    private void PushHourChoiceSnapshot()
    {
        _run.SuspendedStates.Push(new SuspendedStateSnapshot
        {
            State = ERunState.Choice,
            CurrentEncounterId = null,
            CurrentBoardId = _run.CurrentBoardId,
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
            CurrentBoardId = _run.CurrentBoardId,
            SelectionSet = new List<string>(_run.SelectionSet),
            SelectionRules = RunState.CloneRules(_run.SelectionRules),
            RerollCost = _run.RerollCost,
            BaseRerollCost = _run.BaseRerollCost,
            RerollsRemaining = _run.RerollsRemaining,
            SelectionsMadeInCurrentContext = _run.SelectionsMadeInCurrentContext,
            CurrentMaxSelections = _run.CurrentMaxSelections,
            PendingEncounterExitXp = _run.PendingEncounterExitXp,
            MonsterLootPageIndex = _run.MonsterLootPageIndex,
        };
        foreach (var kv in _run.ActiveShelfDiscounts)
            snapshot.ActiveShelfDiscounts[kv.Key] = kv.Value;
        foreach (var kv in _run.ActiveShelfOriginalPrices)
            snapshot.ActiveShelfOriginalPrices[kv.Key] = kv.Value;
        foreach (var page in _run.MonsterLootPages)
            snapshot.MonsterLootPages.Add(new List<RunState.MonsterLootPageItemState>(page));

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
        var prevLayeredKeys = CollectCurrentRunLayeredAttributeKeys();
        SyncTrackedPlayerTempModifiersFromCore(ps);

        int goldBeforeBatch = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        int hpMaxBeforeBatch = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth);

        // 1. 同步玩家属性变动（含 Experience 折算升级与 Level 联动血量/解锁）
        // D2-P1-07：移除错误阻断升级加血的 healthMaxHandled 标志，允许同批 HealthMax 与 +Level 独立叠加
        bool needsPostTrackerAuraRecalc = false;
        int xpDeltaToGrant = 0;
        int levelsGainedDirectly = 0;

        bool hasInitBaseSnap = RunState.TryGetCoreBuildBaseSnapshot(ps, out var initBaseSnap) && initBaseSnap != null;
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

            if (!Enum.TryParse<EPlayerAttributeType>(ch.Attribute, true, out var pa)) continue;
            int oldInt = _run.PlayerAttributes.GetValueOrDefault(pa, 0);
            int deltaInt = (int)Math.Round(ch.Delta);
            if (deltaInt == 0) continue;

            // D2-P1-08：优先按构建时基础快照精确分离永久基础增量与 Hour/Day/Aura 修饰层变化，
            // 绝不把光环开关或限时修饰层的增减误扣/双算到 BasePlayerAttributes 上，同时保留外部叠加（如 OnSell 售价）
            int? minBase = pa switch
            {
                EPlayerAttributeType.Gold or EPlayerAttributeType.Prestige or EPlayerAttributeType.Experience => 0,
                EPlayerAttributeType.HealthMax => 1,
                _ => null,
            };
            int newInt;
            if (hasInitBaseSnap)
            {
                string attrKey = pa.ToString();
                double effBefore = initBaseSnap!.EffectiveAttributes.GetValueOrDefault(attrKey, ps.GetAttribute(attrKey));
                double effAfter = ps.GetAttribute(attrKey);
                if (Math.Abs(effAfter - effBefore) > 1e-6)
                {
                    double baseBefore = initBaseSnap.BaseAttributes.GetValueOrDefault(attrKey, 0.0);
                    double baseAfter = ps.Attributes.GetValueOrDefault(attrKey, baseBefore);
                    double exactBaseDelta = baseAfter - baseBefore;
                    newInt = Math.Abs(exactBaseDelta) > 1e-6
                        ? _run.AdjustBasePlayerAttributeDouble(pa, exactBaseDelta, minBase)
                        : _run.RecomputeDerivedPlayerAttribute(pa);
                }
                else
                {
                    // 调用方手工合成 OutOfCombatBatchResult（未改动 ps 本身）时保持 deltaInt 语义兼容
                    newInt = _run.AdjustBasePlayerAttribute(pa, deltaInt, minBase);
                }
            }
            else
            {
                newInt = _run.AdjustBasePlayerAttribute(pa, deltaInt, minBase);
            }
            if (newInt == oldInt) continue;

            int delta = newInt - oldInt;

            if (pa == EPlayerAttributeType.HealthMax && delta > 0)
            {
                // P2-03：通过 SetBasePlayerAttribute 同步更新 BasePlayerAttributes["Health"]，
                // 避免后续 SyncPlayerAttributesFromCore / ApplyLayeredPlayerStateTo 用旧基底覆盖致商店残血（如 Knee Brace 550/650）
                int curHpBefore = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, oldInt);
                _run.SetBasePlayerAttribute(EPlayerAttributeType.Health, Math.Min(newInt, curHpBefore + delta));
            }

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

        // 确保 Health 不越界超出最新 HealthMax（同步收敛至 SetBasePlayerAttribute 保持基底一致）
        int curMaxHp = Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, ps.HealthMax));
        int curHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, ps.CurrentHealth);
        int clampedHp = Math.Clamp(curHp, 0, curMaxHp);
        if (_run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, clampedHp) != clampedHp ||
            !_run.BasePlayerAttributes.TryGetValue("Health", out double baseHpNow) ||
            Math.Abs(baseHpNow - clampedHp) > 1e-6)
        {
            _run.SetBasePlayerAttribute(EPlayerAttributeType.Health, clampedHp);
        }

        if (levelsGainedDirectly > 0)
        {
            ApplyDirectLevelUps(levelsGainedDirectly, events, dirtyCardIds, xpType);
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

        // 5. P1-09 & P1-18 & P1-39 & D2-P1-07：捕获精准光环目标快照并比对发射 GameSimEventEffectAuraExecuted；
        //    当 syncBoardDiffs 或 needsPostTrackerAuraRecalc（Custom_3/Custom_9 追踪增量）任一为 true 时，强制执行光环重算与全盘同步！
        if (syncBoardDiffs || needsPostTrackerAuraRecalc)
        {
            // 先将批次中经增量写入的最新 Gold / Custom_3 / Custom_9 同步至 ps，确保 Cash Cannon / Oinkment / Calcinator 等光环读取权威值
            foreach (var (baseKey, baseVal) in _run.BasePlayerAttributes)
            {
                if (string.IsNullOrWhiteSpace(baseKey) || !double.IsFinite(baseVal)) continue;
                if (baseKey.Equals("Health", StringComparison.OrdinalIgnoreCase) ||
                    baseKey.Equals("Level", StringComparison.OrdinalIgnoreCase) ||
                    baseKey.Equals("Experience", StringComparison.OrdinalIgnoreCase))
                    continue;
                ps.Attributes[baseKey] = baseVal;
            }
            ps.Attributes["Gold"] = _run.BasePlayerAttributes.TryGetValue("Gold", out double baseGold)
                ? baseGold
                : _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
            ps.Attributes["Custom_3"] = _run.BasePlayerAttributes.TryGetValue("Custom_3", out double baseC3)
                ? baseC3
                : _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0);
            ps.Attributes["Custom_9"] = _run.BasePlayerAttributes.TryGetValue("Custom_9", out double baseC9)
                ? baseC9
                : _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_9, 0);

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

            prevLayeredKeys.UnionWith(CollectCurrentRunLayeredAttributeKeys());
            SyncTrackedPlayerTempModifiersFromCore(ps);
            SyncLayeredPlayerAttributesFromCore(ps, prevLayeredKeys);
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
            SyncLayeredPlayerAttributesFromCore(ps, prevLayeredKeys);
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

    /// <summary>
    /// D2-P1-07 & D2-P1-09：统一直接 +Level 升级入口（供 ApplyOutOfCombatBatchWithDirty 与 ExecuteEncounterStepSelection 共用），
    /// 逐级无条件累加升级血量、解锁地毯格、递增 PendingLevelUps 并派发 OnPlayerLeveledUp。
    /// </summary>
    private void ApplyDirectLevelUps(
        int levelsGainedDirectly,
        List<IGameSimEvent> events,
        HashSet<string>? dirtyCardIds = null,
        EExperienceType xpType = EExperienceType.Item)
    {
        if (levelsGainedDirectly <= 0) return;
        var directLeveledTo = new List<int>(levelsGainedDirectly);
        int curLvl = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1);
        for (int i = 0; i < levelsGainedDirectly; i++)
        {
            curLvl++;
            int hpInc = _levelUps.HealthIncreaseFor(curLvl);
            if (hpInc <= 0)
                hpInc = _levelUps.MaxLevel > 0 ? _levelUps.HealthIncreaseFor(_levelUps.MaxLevel) : 150;
            int nextMaxHp = _run.AdjustBasePlayerAttribute(EPlayerAttributeType.HealthMax, hpInc, minBase: 1);
            int curHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, nextMaxHp);
            // P2-03：直接升级加血同步收敛至 SetBasePlayerAttribute，保持 BasePlayerAttributes["Health"] 一致
            _run.SetBasePlayerAttribute(EPlayerAttributeType.Health, Math.Min(nextMaxHp, curHp + hpInc));

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
        _run.SetBasePlayerAttribute(EPlayerAttributeType.Level, curLvl);

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

    /// <summary>
    /// D2-P1-09：奇遇步骤扣除声望跌至 <= 0 时的统一濒死救援（Fates）或判负终局闭环。
    /// </summary>
    private bool TryTriggerFatesOrEndDefeatFromStep(Outcome o)
    {
        if (_runEnded) return true;
        int curPrestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0);
        if (curPrestige > _mode.FatesSpawnAtOrBelowPrestige)
            return false;

        string fatesTemplateId = !string.IsNullOrWhiteSpace(_mode.FatesTemplateId)
            ? _mode.FatesTemplateId
            : "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";

        if (!_run.FatesUsed && (_pool.IsAssetValid(fatesTemplateId) || _repo.GetCard(fatesTemplateId) != null))
        {
            _run.FatesUsed = true;
            int rescued = Math.Max(1, _mode.FatesAfterPrestige);
            _run.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, rescued);
            o.Events.Add(new GameSimEventPlayerPrestigeChanged(rescued - curPrestige));
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            TransitionToEncounterById(fatesTemplateId, o);
            return true;
        }

        if (curPrestige <= 0)
        {
            _run.FatesUsed = true;
            FlushPendingEncounterExitXp(o.Events);
            _run.CurrentEncounterCoreCard = null;
            o.Events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: false));
            return TryEndRunToEvents(o.Events, justFinishedPvp: false, triggeredFates: false);
        }

        return false;
    }

    private void ApplyPostCombatMechanicOps(
        PlayerState p1Core,
        IReadOnlyList<BazaarCore.Mods.PostCombatMutationOp>? ops,
        List<IGameSimEvent> events,
        string? stripSimIdPrefix = null)
    {
        if (ops == null || ops.Count == 0) return;

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op == null || string.IsNullOrWhiteSpace(op.CardInstanceId)) continue;
            string rawCardId = op.CardInstanceId;
            string localCardId;
            if (!string.IsNullOrEmpty(stripSimIdPrefix))
            {
                if (!rawCardId.StartsWith(stripSimIdPrefix, StringComparison.Ordinal))
                    continue;
                localCardId = rawCardId[stripSimIdPrefix.Length..];
            }
            else
            {
                if (rawCardId.StartsWith("opp_", StringComparison.Ordinal))
                    continue;
                localCardId = rawCardId;
            }
            if (!_run.Cards.TryGetValue(localCardId, out var entry) || entry.Owner != ECombatantId.Player)
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
                // P0-32（合流胶水 #3）：战中沙盒与战后持久化统一委托单一权威写入器 MechanicSafeAttributeWriter，
                // 自动执行输出属性固有守卫、0 CD 被动保护、Multiply/CooldownMax/PercentCooldownReduction/Multicast 钳制与四层清洗
                var mechanicsLookup = _modRuntimeContext?.MechanicsById;
                var writeResult = BazaarCore.Mods.MechanicSafeAttributeWriter.ApplyCardAttribute(
                    coreCard,
                    op.Attribute,
                    op.Operation ?? "Add",
                    op.Value,
                    DurationTarget.Permanent,
                    grantIfMissing: op.GrantIfMissing,
                    mechanicsLookup: mechanicsLookup);
                if (!writeResult.Applied)
                    continue;
                if (Enum.TryParse<ECardAttributeType>(op.Attribute, true, out var attrEnum))
                {
                    entry.Attributes[attrEnum] = (int)Math.Round(coreCard.GetAttribute(op.Attribute));
                }
            }
        }
        BazaarCore.Mods.MechanicSafeAttributeWriter.ClampPlayerInvariants(p1Core);
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
        // 1. 永久变形（W1-B P0-06 接缝：落盘全部 1:N 产物并打包进单条 GameSimEventCardTransformed）与非变形更新卡三层同步（P0-04）
        foreach (var delta in permSync.UpdatedCards)
        {
            if (delta.TransformedReplacementCard == null)
            {
                var updatedEntry = _run.Cards.GetValueOrDefault(delta.InstanceId) ?? FindEntryByCoreCard(delta.SourceCard);
                if (updatedEntry?.CoreCard != null)
                {
                    updatedEntry.Tier = RunState.MapCoreTier(updatedEntry.CoreCard.Tier);
                    updatedEntry.Enchantment = Enum.TryParse<EEnchantmentType>(updatedEntry.CoreCard.Enchantment, true, out var updEnch) ? updEnch : null;
                    events.AddRange(_run.SyncEntryFromCoreCardAndDiff(updatedEntry, _repo.GameMode.StandardPrices));
                }
                continue;
            }

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

            // 聚合全部 1..N 替换产物（优先 delta.TransformedReplacementCard + AdditionalTransformedReplacements，兼容 PermanentReplacements）
            var allReps = new List<CardInstance>(1 + delta.AdditionalTransformedReplacements.Count)
            {
                delta.TransformedReplacementCard
            };
            var seenRepRefs = new HashSet<CardInstance>(ReferenceEqualityComparer.Instance) { delta.TransformedReplacementCard };
            foreach (var extra in delta.AdditionalTransformedReplacements)
            {
                if (extra != null && seenRepRefs.Add(extra))
                    allReps.Add(extra);
            }
            if (allReps.Count == 1 &&
                permSync.PermanentReplacements.TryGetValue(delta.InstanceId, out var fullList) &&
                fullList != null && fullList.Count > 1)
            {
                for (int r = 1; r < fullList.Count; r++)
                {
                    if (fullList[r] != null && seenRepRefs.Add(fullList[r]))
                        allReps.Add(fullList[r]);
                }
            }

            var simTransformations = new List<SimEventCardTransformation>(allReps.Count);
            int slotCursor = Math.Max(0, oldSlot);
            for (int r = 0; r < allReps.Count; r++)
            {
                var repCore = allReps[r];
                repCore.IsCombatTemporary = false;
                repCore.TransformedFromInstanceId = null;

                int preferredSlot = r == 0 ? oldSlot : Math.Clamp(slotCursor, 0, 9);
                var newEntry = RegisterSpawnedCoreCard(
                    repCore,
                    oldSec,
                    preferredSlot,
                    emitSpawnEvent: false,
                    events);

                slotCursor += Math.Max(1, repCore.SlotWidth);
                simTransformations.Add(new SimEventCardTransformation(
                    newEntry.InstanceId,
                    newEntry.TemplateId.ToString(),
                    newEntry.Type,
                    ECombatantId.Player,
                    newEntry.Section,
                    newEntry.Socket));
                LogBoardAudit("CombatTransform", newEntry.InstanceId, newEntry.TemplateId.ToString(), oldLoc, $"{newEntry.Section}:{newEntry.Socket}");
            }

            if (simTransformations.Count > 0)
            {
                events.Add(new GameSimEventCardTransformed(
                    "cmb_" + oldEntry.InstanceId,
                    oldEntry.InstanceId,
                    simTransformations));
            }
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

        // 3. 战斗内永久获得的玩家属性增量 + P0-03/P0-04 玩家 Day/Hour 限时层无损同步（严防 TitanVitality 跨战膨胀与 Relax 当日血量烘进永久层）
        int prevEffectiveHealthMax = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth);
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

            int? minBase = pa switch
            {
                EPlayerAttributeType.Gold or EPlayerAttributeType.Prestige or EPlayerAttributeType.Experience or EPlayerAttributeType.HealthRegen => 0,
                EPlayerAttributeType.HealthMax => 1,
                _ => null,
            };
            int oldVal = _run.PlayerAttributes.GetValueOrDefault(pa, 0);
            int nextVal = _run.AdjustBasePlayerAttribute(pa, intDiff, minBase);
            // 保持 p1Core 基础值与 _run.BasePlayerAttributes 一致，避免同场存在限时层时 L6695 丢失战中永久增量
            string paKey = pa.ToString();
            if (_run.BasePlayerAttributes.TryGetValue(paKey, out double syncedBaseVal))
                p1Core.Attributes[paKey] = syncedBaseVal;

            if (pa == EPlayerAttributeType.Gold && nextVal > oldVal)
                _run.IncrementPlayerAttribute(EPlayerAttributeType.Custom_3, nextVal - oldVal);
            else if (pa == EPlayerAttributeType.Income && intDiff > 0)
                events.Add(new GameSimEventPlayerIncomeGained((uint)intDiff));
            else if (pa == EPlayerAttributeType.Prestige)
                events.Add(new GameSimEventPlayerPrestigeChanged(intDiff));
        }

        bool hasTimedPlayerLayerDeltas =
            permSync.PlayerDayAddDeltas.Count > 0 ||
            permSync.PlayerDayAdditiveMultDeltas.Count > 0 ||
            permSync.PlayerDayPureMultChanges.Count > 0 ||
            permSync.PlayerHourAddDeltas.Count > 0 ||
            permSync.PlayerHourAdditiveMultDeltas.Count > 0 ||
            permSync.PlayerHourPureMultChanges.Count > 0;

        if (hasTimedPlayerLayerDeltas)
        {
            var prevLayeredKeys = CollectCurrentRunLayeredAttributeKeys();
            SyncTrackedPlayerTempModifiersFromCore(p1Core);
            SyncLayeredPlayerAttributesFromCore(p1Core, prevLayeredKeys);
        }

        // 统一按有效 HealthMax 净增量校准当前血量（覆盖永久与 Day/Hour 层血上限增量，并同步收敛至 SetBasePlayerAttribute）
        int nextEffectiveHealthMax = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, prevEffectiveHealthMax);
        int effectiveMaxHpDiff = nextEffectiveHealthMax - prevEffectiveHealthMax;
        if (effectiveMaxHpDiff != 0)
        {
            int curHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, prevEffectiveHealthMax);
            int targetHp = effectiveMaxHpDiff > 0
                ? Math.Min(nextEffectiveHealthMax, curHp + effectiveMaxHpDiff)
                : Math.Min(curHp, nextEffectiveHealthMax);
            _run.SetBasePlayerAttribute(EPlayerAttributeType.Health, targetHp);
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

        // W1-B P0-04 接缝：统一收口至 CardInstance.CloneNonCombatState，完整保留三层属性、Day/Hour/Permanent 附魔与临时品阶
        if (!string.Equals(core.InstanceId, instId, StringComparison.Ordinal))
        {
            var aligned = core.CloneNonCombatState(instId);
            aligned.IsFlying = core.IsFlying;
            aligned.Heated = core.Heated;
            aligned.Chilled = core.Chilled;
            if (core.Tier != core.PermanentTier)
                aligned.SetTier(core.Tier, isTemporary: true);
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
        };
        entry.BindAlignedCoreCard(core);
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
        // D2-P1-08：基于独立基础值正向还原玩家 Hour 临时层（含 AdditiveMult 与 PureMult==0 场景零损恢复）
        if (_playerHourAdd.Count > 0 || _playerHourMult.Count > 0 || _run.PlayerHourAdditiveMult.Count > 0)
        {
            var p1 = BuildCorePlayerFromRun();
            p1.ClearEndOfHourModifiers();
            _playerHourAdd.Clear();
            _playerHourMult.Clear();
            _run.PlayerHourAdditiveMult.Clear();
            SyncEffectivePlayerAttributesFromCore(p1);
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
        if (_playerDayAdd.Count > 0 || _playerDayMult.Count > 0 || _run.PlayerDayAdditiveMult.Count > 0)
        {
            var p1 = BuildCorePlayerFromRun();
            p1.ClearEndOfDayModifiers();
            _playerDayAdd.Clear();
            _playerDayMult.Clear();
            _run.PlayerDayAdditiveMult.Clear();
            SyncEffectivePlayerAttributesFromCore(p1);
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
        // D2-P1-08：严格通过 PlayerLayerModifierBridge 提取纯净 PureMult 与独立 AdditiveMult，彻底杜绝 (1+add)^2 双重乘算
        _playerHourAdd.Clear();
        foreach (var (k, v) in ps.HourAdd)
            if (Math.Abs(v) > 1e-9) _playerHourAdd[k] = v;
        PlayerLayerModifierBridge.CopyHourPureMults(ps, _playerHourMult);
        _run.PlayerHourAdditiveMult.Clear();
        foreach (var (k, v) in ps.HourAdditiveMult)
            if (Math.Abs(v) > 1e-9) _run.PlayerHourAdditiveMult[k] = v;

        _playerDayAdd.Clear();
        foreach (var (k, v) in ps.DayAdd)
            if (Math.Abs(v) > 1e-9) _playerDayAdd[k] = v;
        PlayerLayerModifierBridge.CopyDayPureMults(ps, _playerDayMult);
        _run.PlayerDayAdditiveMult.Clear();
        foreach (var (k, v) in ps.DayAdditiveMult)
            if (Math.Abs(v) > 1e-9) _run.PlayerDayAdditiveMult[k] = v;

        _run.PlayerAuraAdd.Clear();
        foreach (var (k, v) in ps.AuraAdd)
            if (Math.Abs(v) > 1e-9) _run.PlayerAuraAdd[k] = v;
        PlayerLayerModifierBridge.CopyAuraPureMults(ps, _run.PlayerAuraMult);
        _run.PlayerAuraAdditiveMult.Clear();
        foreach (var (k, v) in ps.AuraAdditiveMult)
            if (Math.Abs(v) > 1e-9) _run.PlayerAuraAdditiveMult[k] = v;

        if (ps.ExclusionSetInitialized || ps.ExclusionSet.Count > 0)
        {
            _run.ExclusionSet.Clear();
            foreach (string exId in ps.ExclusionSet)
                _run.ExclusionSet.Add(exId);
            _run.ExclusionSetInitialized = true;
        }
    }

    private HashSet<string> CollectCurrentRunLayeredAttributeKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _run.PlayerAuraAdd.Keys) keys.Add(k);
        foreach (var k in _run.PlayerAuraMult.Keys) keys.Add(k);
        foreach (var k in _run.PlayerAuraAdditiveMult.Keys) keys.Add(k);
        foreach (var k in _run.PlayerHourAdd.Keys) keys.Add(k);
        foreach (var k in _run.PlayerHourMult.Keys) keys.Add(k);
        foreach (var k in _run.PlayerHourAdditiveMult.Keys) keys.Add(k);
        foreach (var k in _run.PlayerDayAdd.Keys) keys.Add(k);
        foreach (var k in _run.PlayerDayMult.Keys) keys.Add(k);
        foreach (var k in _run.PlayerDayAdditiveMult.Keys) keys.Add(k);
        return keys;
    }

    private static int ClampEffectivePlayerAttributeDisplay(EPlayerAttributeType pa, int rawEff) => pa switch
    {
        EPlayerAttributeType.HealthMax => Math.Max(1, rawEff),
        EPlayerAttributeType.Gold or EPlayerAttributeType.Prestige or EPlayerAttributeType.Experience => Math.Max(0, rawEff),
        _ => rawEff,
    };

    private void SyncEffectivePlayerAttributesFromCore(PlayerState ps)
    {
        foreach (EPlayerAttributeType pa in Enum.GetValues<EPlayerAttributeType>())
        {
            if (pa is EPlayerAttributeType.Experience or EPlayerAttributeType.Level or EPlayerAttributeType.Health) continue;
            string key = pa.ToString();
            // P1-18 & P0-03 & D2-P1-08：同步 _run.PlayerAttributes 已有键、携带 Aura/Hour/Day 修饰层（含 AdditiveMult）的键，
            // 以及跨战/局外持久化白名单键；严禁把 PlayerState 构造自带的战斗瞬态默认属性反向泄漏回 _run.PlayerAttributes。
            bool hasModifierLayer = ps.AuraAdd.ContainsKey(key)
                || ps.AuraMult.ContainsKey(key)
                || ps.AuraAdditiveMult.ContainsKey(key)
                || ps.HourAdd.ContainsKey(key)
                || ps.HourMult.ContainsKey(key)
                || ps.HourAdditiveMult.ContainsKey(key)
                || ps.DayAdd.ContainsKey(key)
                || ps.DayMult.ContainsKey(key)
                || ps.DayAdditiveMult.ContainsKey(key);
            bool isPersistentWhitelistWithValue = IsPersistentSyncPlayerAttribute(pa, key)
                && ps.Attributes.TryGetValue(key, out double rawVal)
                && Math.Abs(rawVal) > 1e-6;
            if (!_run.PlayerAttributes.ContainsKey(pa) && !hasModifierLayer && !isPersistentWhitelistWithValue) continue;
            if (ps.Attributes.TryGetValue(key, out double baseVal) && double.IsFinite(baseVal))
                _run.BasePlayerAttributes[key] = baseVal;
            int eff = ClampEffectivePlayerAttributeDisplay(pa, (int)Math.Round(ps.GetAttribute(key)));
            if (_run.PlayerAttributes.GetValueOrDefault(pa, 0) != eff)
                _run.PlayerAttributes[pa] = eff;
            _run.LastDerivedPlayerAttributes[pa] = eff;
        }

        // 确保 Health 不越界超出最新有效 HealthMax，并保持 BasePlayerAttributes["Health"] 同步
        int syncedMaxHp = Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth));
        int syncedHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, syncedMaxHp);
        int clampedHp = Math.Clamp(syncedHp, 0, syncedMaxHp);
        if (clampedHp != syncedHp ||
            !_run.BasePlayerAttributes.TryGetValue("Health", out double syncedBaseHp) ||
            Math.Abs(syncedBaseHp - clampedHp) > 1e-6)
        {
            _run.SetBasePlayerAttribute(EPlayerAttributeType.Health, clampedHp);
        }
        else
        {
            _run.LastDerivedPlayerAttributes[EPlayerAttributeType.Health] = clampedHp;
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
    /// 回写当前带有 Aura/Hour/Day 修饰层、或先前曾带修饰层现已移除需要回落的玩家属性及 RerollCostModifier（用于 ApplyOutOfCombatBatch / 战后同步收尾），
    /// 绝不用旧 ps.Attributes 覆盖批次中由外部/TrackReagentTransformIfApplicable 增量写入的 Custom_9/Custom_3/Gold 等基础值，
    /// 并确保光环或限时层移除后展示值即时回落且 Health 同步下钳至 <= HealthMax。
    /// </summary>
    private void SyncLayeredPlayerAttributesFromCore(PlayerState ps, IReadOnlySet<string>? previouslyLayeredKeys = null)
    {
        foreach (EPlayerAttributeType pa in Enum.GetValues<EPlayerAttributeType>())
        {
            if (pa is EPlayerAttributeType.Experience or EPlayerAttributeType.Level or EPlayerAttributeType.Health) continue;
            string key = pa.ToString();
            bool hasModifierLayer = ps.AuraAdd.ContainsKey(key)
                || ps.AuraMult.ContainsKey(key)
                || ps.AuraAdditiveMult.ContainsKey(key)
                || ps.HourAdd.ContainsKey(key)
                || ps.HourMult.ContainsKey(key)
                || ps.HourAdditiveMult.ContainsKey(key)
                || ps.DayAdd.ContainsKey(key)
                || ps.DayMult.ContainsKey(key)
                || ps.DayAdditiveMult.ContainsKey(key);
            bool wasPreviouslyLayered = previouslyLayeredKeys != null && previouslyLayeredKeys.Contains(key);
            if (!hasModifierLayer && !wasPreviouslyLayered && pa != EPlayerAttributeType.RerollCostModifier) continue;
            if (!hasModifierLayer && !wasPreviouslyLayered && !_run.PlayerAttributes.ContainsKey(pa) && !ps.Attributes.ContainsKey(key)) continue;
            int eff = ClampEffectivePlayerAttributeDisplay(pa, (int)Math.Round(ps.GetAttribute(key)));
            if (_run.PlayerAttributes.GetValueOrDefault(pa, 0) != eff)
                _run.PlayerAttributes[pa] = eff;
            _run.LastDerivedPlayerAttributes[pa] = eff;
        }

        int curMaxHp = Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, _mode.StartingHealth));
        int curHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, curMaxHp);
        int clampedHp = Math.Clamp(curHp, 0, curMaxHp);
        if (clampedHp != curHp ||
            !_run.BasePlayerAttributes.TryGetValue("Health", out double layeredBaseHp) ||
            Math.Abs(layeredBaseHp - clampedHp) > 1e-6)
        {
            _run.SetBasePlayerAttribute(EPlayerAttributeType.Health, clampedHp);
        }
        else
        {
            _run.LastDerivedPlayerAttributes[EPlayerAttributeType.Health] = clampedHp;
        }
    }

    private void ClearTrackedPlayerTempModifiers(bool clearHour, bool clearDay, bool clearAura = false)
    {
        if (clearHour)
        {
            _playerHourAdd.Clear();
            _playerHourMult.Clear();
            _run.PlayerHourAdditiveMult.Clear();
        }
        if (clearDay)
        {
            _playerDayAdd.Clear();
            _playerDayMult.Clear();
            _run.PlayerDayAdditiveMult.Clear();
        }
        if (clearAura)
        {
            _run.PlayerAuraAdd.Clear();
            _run.PlayerAuraMult.Clear();
            _run.PlayerAuraAdditiveMult.Clear();
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
            // D3-P1-06：放宽至 ECardType.Item 与 ECardType.Skill，确保技能战中瞬态状态与 Custom_0..8 计数器同样战后归零/还原，
            // 同时保持 Ammo / Flying / Heated / Chilled 仅作用于 Item
            if (entry.Owner != ECombatantId.Player || entry.Type is not (ECardType.Item or ECardType.Skill)) continue;

            var core = entry.CoreCard;
            bool isItem = entry.Type == ECardType.Item;
            if (isItem)
            {
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
            }
            else
            {
                entry.Attributes[ECardAttributeType.Freeze] = 0;
                entry.Attributes[ECardAttributeType.Slow] = 0;
                entry.Attributes[ECardAttributeType.Haste] = 0;
                entry.PendingForceSyncAttributes[ECardAttributeType.Freeze] = 0;
                entry.PendingForceSyncAttributes[ECardAttributeType.Slow] = 0;
                entry.PendingForceSyncAttributes[ECardAttributeType.Haste] = 0;
            }

            foreach (var attrType in postCombatResetAttrs)
            {
                if (!isItem && attrType == ECardAttributeType.SellPrice) continue;
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

    private bool ActivateEncounterCard(
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
            return false;
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
            return false;
        }

        // P0-14：兼容原版 GUID 与自定义非 GUID Mod 遭遇 ID 的顶栏复用判定
        if (!string.IsNullOrEmpty(_run.ActiveEncounterCardInstanceId) &&
            _run.Cards.TryGetValue(_run.ActiveEncounterCardInstanceId, out var currentActive) &&
            string.Equals(RunState.ResolveCoreTemplateId(currentActive), encId, StringComparison.OrdinalIgnoreCase))
        {
            currentActive.Owner = null;
            currentActive.Section = null;
            currentActive.Socket = EContainerSocketId.Socket_5;
            currentActive.State = ECardState.Alive;
            return false;
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
            Guid wireGuid = ResolveSafeWireGuid(encId, cardType);
            bool isCustomNonGuid = !string.Equals(encId, wireGuid.ToString(), StringComparison.OrdinalIgnoreCase);
            newActive = new RunState.CardEntry
            {
                InstanceId = _shop.NewId(prefix),
                TemplateId = wireGuid,
                CustomCardId = isCustomNonGuid ? encId : null,
                Type = cardType,
                Size = ShopEngine.ParseSize(coreTpl?.Size ?? "Medium"),
                Tier = ShopEngine.ParseTier(coreTpl?.StartingTier ?? "Bronze"),
            };
            if (isCustomNonGuid)
                _run.VisualBindingsRevision++;
            RunState.PopulateMetaFromTemplate(newActive, null, coreTpl);
        }

        newActive.Owner = null;
        newActive.Section = null;
        newActive.Socket = EContainerSocketId.Socket_5;
        newActive.State = ECardState.Alive;
        _run.Cards[newActive.InstanceId] = newActive;
        _run.ActiveEncounterCardInstanceId = newActive.InstanceId;
        // P0-14：彻底废除同包伪造 GameSimEventCardDealt + GameSimEventCardPurchased 双事件！
        // 客户端经 StateTransitioned -> BoardManager.OnEncounterPlaced 原生渲染顶栏，并在命令出口按需附带尾部 GameStateSync 精确对齐实例 ID（中央货架 0 幽灵卡）。
        _synthesizedEncounterCardThisCommand = true;
        return true;
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
            string.Equals(RunState.ResolveCoreTemplateId(active), anchorId, StringComparison.OrdinalIgnoreCase))
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
        var coreTpl = stepEntry.CoreCard?.Template ?? _repo.GetCard(RunState.ResolveCoreTemplateId(stepEntry));
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
            HealthMax: Math.Max(100, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, GameDefaults.StartingHealth)),
            Prestige: Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20)),
            Level: Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1)),
            Victories: _run.Victories,
            Gold: Math.Max(0, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0)),
            Income: Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, 5)),
            RunSnapshotJson: RunSnapshotSerializer.Serialize(_run, extraMutators: GetEffectiveCustomMutators()));
    }

    private PlayerState BuildPlayerStateFromLockedBoard(
        LockedPvpBoard board,
        string simPlayerId,
        string cardIdPrefix,
        out List<RunState.CardEntry> boardEntries,
        out Dictionary<string, CardInstance> coreMap)
    {
        try
        {
            if (RunSnapshotSerializer.TryDeserialize(board.RunSnapshotJson, _repo, out var restoredRun, out _, extraMutators: GetEffectiveCustomMutators()) &&
                restoredRun != null)
            {
                return BuildSymmetricPlayerStateFromRun(
                    restoredRun,
                    simPlayerId,
                    board.Username,
                    cardIdPrefix,
                    out boardEntries,
                    out coreMap);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunEngine] 反序列化锁定盘面失败，降级为空白保底盘面: {ex.Message}");
        }

        boardEntries = new List<RunState.CardEntry>();
        coreMap = new Dictionary<string, CardInstance>(StringComparer.Ordinal);
        var fallback = new PlayerState(
            simPlayerId,
            board.Username,
            string.IsNullOrWhiteSpace(board.Hero) ? "Vanessa" : board.Hero,
            Math.Max(100, board.HealthMax),
            Math.Max(1, board.Prestige),
            Math.Max(0, board.Gold),
            10)
        {
            Level = Math.Max(1, board.Level),
            Wins = (int)board.Victories,
        };
        fallback.Attributes["Income"] = Math.Max(1, board.Income);
        fallback.Hand.UnlockedMask = 1023;
        var fbItem = _repo.AlwaysSpawnableItems.FirstOrDefault() ?? _repo.CardsById.Values.FirstOrDefault(c => c.Type == "Item");
        if (fbItem != null)
        {
            var fbCard = new CardInstance(fbItem, CardTier.Bronze, null, cardIdPrefix + "pvp_locked_fb_0");
            fallback.Hand.TryAddAuto(fbCard);
            coreMap[fbCard.InstanceId] = fbCard;
        }
        return fallback;
    }

    private PlayerState BuildOpponentPlayerStateFromLockedBoard(LockedPvpBoard board)
        => BuildPlayerStateFromLockedBoard(board, "opponent", "opp_", out _, out _);

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
            // P3-08：严格过滤 SpawningEligibility=="Never"、空 ArtKey 与 [DEBUG]/TEST 技能，根治 BossRush 对阵 Void Golem 等刷出空路径技能
            var skillCandidates = _repo.CardsById.Values
                .Where(c => string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase) &&
                            !existingIds.Contains(c.Id) &&
                            !MonsterPool.IsBlockedDebugOrBlankArtSkill(c) &&
                            !string.Equals(c.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(c.ArtKey) &&
                            !c.InternalName.Contains("TEST", StringComparison.OrdinalIgnoreCase) &&
                            !c.InternalName.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase))
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
        CombatSim? combatSim,
        bool isDraw = false,
        string? opponentAccountId = null,
        IReadOnlyList<RunState.CardEntry>? oppEntries = null)
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

            // P0-26：采集本场己方与敌方真自定义卡的视觉绑定序列化为 VisualBindingsJson，支持历史战报离线 Mod 皮肤回放
            string? visualBindingsJson = BuildReplayVisualBindingsJson(oppEntries);

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
                CombatSimMsgPack: simPayload,
                TournamentSessionId: _run.TournamentSessionId?.ToString(),
                OpponentAccountId: opponentAccountId,
                IsDraw: isDraw,
                VisualBindingsJson: visualBindingsJson));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunEngine] 保存战斗回放异常（已忽略）: {ex.Message}");
        }
    }

    /// <summary>P0-19（合流胶水 #6）：为战中运行期变形生成的非 GUID 自定义卡解析合法 Wire 借壳 GUID。</summary>
    private Guid? ResolveCombatWireTemplateGuid(string coreTemplateId)
    {
        if (string.IsNullOrWhiteSpace(coreTemplateId)) return null;
        string safe = _pool.ResolveSafeWireTemplateId(coreTemplateId);
        if (Guid.TryParse(safe, out var parsed) && parsed != Guid.Empty)
            return parsed;
        if (Guid.TryParse(coreTemplateId, out var direct) && direct != Guid.Empty)
            return direct;
        return null;
    }

    /// <summary>P0-19（合流胶水 #6）：战中变形产出自定义卡时自动回写临时视觉绑定、登记战报采集集并自增 VisualBindingsRevision。</summary>
    private void OnCombatRuntimeCardTransformed(string newInstanceId, string newCoreTemplateId, Guid resolvedWireGuid)
    {
        if (string.IsNullOrWhiteSpace(newCoreTemplateId)) return;
        string trimmedTpl = newCoreTemplateId.Trim();
        bool isNativeGuid = Guid.TryParse(trimmedTpl, out var parsedGuid) && parsedGuid != Guid.Empty;

        BazaarCore.Mods.CustomCardVisualBinding? srcBinding = null;
        if (_runModContext != null && _runModContext.TryResolveVisualBinding(trimmedTpl, out var runB))
            srcBinding = runB;
        else if (_runVisualBindings != null && _runVisualBindings.TryGetValue(trimmedTpl, out var vb))
            srcBinding = vb;
        else if (_modHost?.VisualBindingsByCustomId != null && _modHost.VisualBindingsByCustomId.TryGetValue(trimmedTpl, out var hb))
            srcBinding = hb;

        // P0-25：严格区分原位 Patch（IsPatchOverride=true）与全新借壳自定义卡，严禁把被原位 Patch 的原版卡误判为自定义卡
        bool isPatchOverride = srcBinding?.IsPatchOverride == true || (_pool.Get(trimmedTpl)?.IsPatchOverride == true);
        bool isCustom = !isPatchOverride && (!isNativeGuid || srcBinding != null || (_pool.Get(trimmedTpl)?.IsCustomModCard == true));
        if (!isCustom) return;

        _combatTransientCustomCardIds.Add(trimmedTpl);
        if (!string.IsNullOrWhiteSpace(newInstanceId) && newInstanceId.StartsWith("opp_", StringComparison.OrdinalIgnoreCase))
            _run.ActiveCombatOpponentCustomCardIds[newInstanceId] = trimmedTpl;

        if (_runModContext != null)
        {
            var transient = srcBinding != null
                ? srcBinding.BindToInstance(newInstanceId)
                : new BazaarCore.Mods.CustomCardVisualBinding
                {
                    CustomTemplateId = trimmedTpl,
                    InstanceId = newInstanceId,
                    BaseVisualTemplateId = resolvedWireGuid != Guid.Empty ? resolvedWireGuid.ToString() : string.Empty,
                };
            if (resolvedWireGuid != Guid.Empty && string.IsNullOrWhiteSpace(transient.BaseVisualTemplateId))
                transient.BaseVisualTemplateId = resolvedWireGuid.ToString();
            _runModContext.RegisterTransientVisualBinding(transient);
        }

        _run.VisualBindingsRevision++;
    }

    private string? BuildReplayVisualBindingsJson(IReadOnlyList<RunState.CardEntry>? oppEntries)
    {
        var sourceBindings = _runModContext?.GetMergedVisualCatalog()
            ?? _runVisualBindings
            ?? _modHost?.VisualBindingsByCustomId;
        if (sourceBindings == null || sourceBindings.Count == 0)
            return null;

        var usedCustomIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _run.Cards.Values)
        {
            if (!string.IsNullOrWhiteSpace(entry.CustomCardId))
                usedCustomIds.Add(entry.CustomCardId!);
        }
        if (oppEntries != null)
        {
            foreach (var oe in oppEntries)
            {
                if (!string.IsNullOrWhiteSpace(oe.CustomCardId))
                    usedCustomIds.Add(oe.CustomCardId!);
            }
        }
        foreach (string oppCid in _run.ActiveCombatOpponentCustomCardIds.Values)
        {
            if (!string.IsNullOrWhiteSpace(oppCid))
                usedCustomIds.Add(oppCid);
        }
        foreach (string transientCid in _combatTransientCustomCardIds)
        {
            if (!string.IsNullOrWhiteSpace(transientCid))
                usedCustomIds.Add(transientCid);
        }

        if (usedCustomIds.Count == 0)
            return null;

        var replayBindings = new Dictionary<string, BazaarCore.Mods.CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (string cid in usedCustomIds)
        {
            if (sourceBindings.TryGetValue(cid, out var binding) && binding != null)
                replayBindings[cid] = binding;
        }
        if (replayBindings.Count == 0)
            return null;

        return JsonSerializer.Serialize(replayBindings);
    }

    private void EnsurePendingHour6PvpOpponentLocked(List<IGameSimEvent>? events = null)
    {
        if (_runEnded || _run.State != ERunState.Choice || _run.Hour != (uint)_mode.HoursInADay)
            return;

        int day = (int)_run.Day;
        var tourneyLobby = _lobbies?.FindLobbyForRun(_run.TournamentSessionId, _run.AccountId);
        MatchupAssignment? tourneyAssignment = null;
        int fallbackIncome = Math.Max(1, tourneyLobby?.Rules?.StartingIncome ?? _run.CustomRules?.StartingIncome ?? _mode.StartingIncome);
        string ghostPartition = PvpGhostPool.ResolveGhostPoolPartitionLocal(
            _run.CustomRules,
            PvpGhostPool.HasTrueCustomCard(_run),
            _modHost);

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
        EHero oppHero = PvpGhostPool.ParseHero(ghost.Hero);
        // Wave 4A #1 (H-1) & #11：非锦标赛幽灵 accountId 嵌入 Day 与 BoardId 保证跨天必异；名牌去除 ({ghost.PowerBucket}) 仅下发干净英雄名
        string oppAccountId = tourneyAssignment?.OpponentAccountId
            ?? $"ghost_{oppHero}_d{_run.Day}_{(!string.IsNullOrWhiteSpace(ghost.BoardId) ? ghost.BoardId : Guid.NewGuid().ToString("N"))}";
        string oppDisplayName = tourneyAssignment != null
            ? tourneyAssignment.OpponentUsername
            : (string.IsNullOrWhiteSpace(ghost.Hero) ? "Challenger" : ghost.Hero);

        int previewHp = Math.Max(100, ghost.HealthMax);
        if (tourneyLobby != null && tourneyAssignment is { OpponentIsBot: true })
        {
            int baseHp = _mode.StartingHealth > 0 ? _mode.StartingHealth : GameDefaults.StartingHealth;
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
        var heroes = HeroNames.PlayableHeroes;
        string hero = heroes[rng.Next(heroes.Length)];
        int hp = clampedDay == 1 ? GameDefaults.StartingHealth : Math.Min(4500, GameDefaults.StartingHealth + (clampedDay - 1) * 350);
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

        int baseStartHp = _mode.StartingHealth > 0 ? _mode.StartingHealth : GameDefaults.StartingHealth;
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
                if (MonsterPool.IsBlockedDebugOrBlankArtSkill(ci.Template))
                    continue;
                p2Core.Skills.Add(ci);
                mutated = true;
            }
        }

        // D6-P2-05：同步锦标赛房间突变词条（AllEnchanted / GoldenStart）与高阶加成（ShopHighTierBonusPct）至 AI 镜像，保证公平对称
        var aiRuleRng = new Random(HashCode.Combine(p2Core.PlayerId, _run.Day, rules.GetHashCode()));
        if (rules.HasMutator(MutatorCatalog.GoldenStart))
        {
            p2Core.Hand.UnlockedMask |= 0b01_1111_1110;
            foreach (var item in p2Core.Hand.Cards)
            {
                var clampedGold = SpawnQueryEngine.ClampToSupportedTier(item.Template, CardTier.Gold);
                if (clampedGold > item.Tier)
                {
                    item.SetTier(clampedGold, isTemporary: false);
                    mutated = true;
                }
            }
            foreach (var sk in p2Core.Skills)
            {
                var clampedGold = SpawnQueryEngine.ClampToSupportedTier(sk.Template, CardTier.Gold);
                if (clampedGold > sk.Tier)
                {
                    sk.SetTier(clampedGold, isTemporary: false);
                    mutated = true;
                }
            }
        }

        if (rules.ShopHighTierBonusPct > 1e-3)
        {
            double upgradeProb = Math.Clamp(rules.ShopHighTierBonusPct / 100.0, 0.0, 1.0);
            foreach (var card in p2Core.Hand.Cards.Concat(p2Core.Skills))
            {
                if (card.Tier >= CardTier.Diamond || card.Tier == CardTier.Legendary) continue;
                if (aiRuleRng.NextDouble() < upgradeProb)
                {
                    var nextTier = SpawnQueryEngine.ClampToSupportedTier(card.Template, card.Tier + 1);
                    if (nextTier > card.Tier)
                    {
                        card.SetTier(nextTier, isTemporary: false);
                        mutated = true;
                    }
                }
            }
        }

        if (rules.HasMutator(MutatorCatalog.AllEnchanted))
        {
            foreach (var item in p2Core.Hand.Cards)
            {
                if (!string.IsNullOrWhiteSpace(item.Enchantment)) continue;
                if (item.Template.Enchantments is not { Count: > 0 } enchs) continue;
                var keys = enchs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                if (keys.Count == 0) continue;
                string pickEnch = keys[aiRuleRng.Next(keys.Count)];
                item.ApplyEnchantment(pickEnch, DurationTarget.Permanent, preventOverride: false);
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
        => EmitPostCombatTemporaryTransformRevertsWithOwner(records, despawnEvents, dirtyCardIds, selfSimPlayerId: "player", stripSimIdPrefix: null);

    private void EmitPostCombatTemporaryTransformRevertsWithOwner(
        IReadOnlyList<CombatTemporaryTransformRecord>? records,
        List<IGameSimEvent> despawnEvents,
        HashSet<string> dirtyCardIds,
        string selfSimPlayerId,
        string? stripSimIdPrefix)
    {
        if (records == null || records.Count == 0) return;

        foreach (var rec in records)
        {
            if (rec == null || string.IsNullOrEmpty(rec.OriginalCardInstanceId))
                continue;
            if (!string.Equals(rec.OwnerPlayerId, selfSimPlayerId, StringComparison.OrdinalIgnoreCase))
                continue;

            string localOrigId = !string.IsNullOrEmpty(stripSimIdPrefix) &&
                rec.OriginalCardInstanceId.StartsWith(stripSimIdPrefix, StringComparison.Ordinal)
                ? rec.OriginalCardInstanceId[stripSimIdPrefix.Length..]
                : rec.OriginalCardInstanceId;

            if (!_run.Cards.TryGetValue(localOrigId, out var origEntry))
            {
                // P0-03 双保险反查：按 CoreCard.InstanceId 反查真实 CardEntry，彻底消除 UUID 割裂导致的查空与错 ID 透传
                origEntry = _run.Cards.Values.FirstOrDefault(c =>
                    c.CoreCard != null &&
                    string.Equals(c.CoreCard.InstanceId, localOrigId, StringComparison.Ordinal));
                if (origEntry == null && rec.OriginalSlot is >= 0 and <= 9 && !string.IsNullOrEmpty(rec.OriginalTemplateId))
                {
                    EInventorySection? origSec = ParseSection(rec.OriginalSection);
                    EContainerSocketId origSock = (EContainerSocketId)rec.OriginalSlot;
                    origEntry = _run.Cards.Values.FirstOrDefault(c =>
                        c.Owner == ECombatantId.Player &&
                        c.Section == origSec &&
                        c.Socket == origSock &&
                        string.Equals(RunState.ResolveCoreTemplateId(c), rec.OriginalTemplateId, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (origEntry == null)
            {
                // P0-03：若原卡在 _run.Cards 中已不存在（绝不透传未知 ID 诱发下游构造 Size=0 空壳崩毁客户端）
                Console.WriteLine($"[RunEngine][WARN] EmitPostCombatTemporaryTransformRevertsWithOwner skipped unresolved original card id={localOrigId}, tpl={rec.OriginalTemplateId}");
                continue;
            }

            ECardType cardType = origEntry.Type;
            string wireTplId = origEntry.TemplateId.ToString();
            EInventorySection? section = origEntry.Section ?? ParseSection(rec.OriginalSection);
            EContainerSocketId? socket = origEntry.Socket
                ?? (rec.OriginalSlot is >= 0 and <= 9 ? (EContainerSocketId)rec.OriginalSlot : null);

            var origTransform = new SimEventCardTransformation(
                origEntry.InstanceId,
                wireTplId,
                cardType,
                ECombatantId.Player,
                section,
                socket);

            var transformedIds = rec.TransformedInstanceIds is { Count: > 0 }
                ? new List<string>(rec.TransformedInstanceIds)
                : new List<string>();

            despawnEvents.Add(new GameSimEventCardTransformReverted(origTransform, transformedIds));
            dirtyCardIds.Add(origEntry.InstanceId);
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
                CurrentBoardId = _run.CurrentBoardId,
                SelectionSet = new List<string>(_run.SelectionSet),
                SelectionRules = RunState.CloneRules(_run.SelectionRules),
                RerollCost = _run.RerollCost,
                BaseRerollCost = _run.BaseRerollCost,
                RerollsRemaining = _run.RerollsRemaining,
                SelectionsMadeInCurrentContext = 0,
                CurrentMaxSelections = _run.CurrentMaxSelections,
                PendingEncounterExitXp = 0,
                MonsterLootPageIndex = _run.MonsterLootPageIndex,
            };
            foreach (var kv in _run.ActiveShelfDiscounts)
                lootSnap.ActiveShelfDiscounts[kv.Key] = kv.Value;
            foreach (var kv in _run.ActiveShelfOriginalPrices)
                lootSnap.ActiveShelfOriginalPrices[kv.Key] = kv.Value;
            foreach (var page in _run.MonsterLootPages)
                lootSnap.MonsterLootPages.Add(new List<RunState.MonsterLootPageItemState>(page));

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
            nextGold = _run.AdjustBasePlayerAttribute(EPlayerAttributeType.Gold, delta, minBase: 0);
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
        int nextMax = _run.AdjustBasePlayerAttribute(EPlayerAttributeType.HealthMax, delta, minBase: 1);
        if (clampHealth)
        {
            // P2-03：统一收敛为 SetBasePlayerAttribute，确保 BasePlayerAttributes["Health"] 同步更新
            int curHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, nextMax);
            int targetHp = delta > 0
                ? Math.Min(nextMax, curHp + delta)
                : Math.Min(curHp, nextMax);
            _run.SetBasePlayerAttribute(EPlayerAttributeType.Health, targetHp);
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

        // W1-B P0-01 接缝：优先消费强类型 DealtSelectionPayloads（完整透传 SelectionContextRules），无载荷时向后兼容 DealtSelectionRequests
        if ((batch.DealtSelectionPayloads.Count > 0 || batch.DealtSelectionRequests.Count > 0) && !_runEnded)
        {
            var payloads = batch.DealtSelectionPayloads.Count > 0
                ? batch.DealtSelectionPayloads
                : batch.DealtSelectionRequests.Select(r => new DealtCardsPayload(r.Cards, r.SpawnMode, null, null)).ToList();

            foreach (var payload in payloads)
            {
                var dealtCards = payload.Cards;
                if (dealtCards == null || dealtCards.Count == 0) continue;
                bool replace = !string.Equals(payload.SpawnMode, "Add", StringComparison.OrdinalIgnoreCase);
                if (replace)
                {
                    events.AddRange(ClearShelfPreservingActiveEncounter(preserveActiveEncounter: true));
                }

                if (payload.SelectionContextRules != null)
                {
                    EncounterStepEngine.ApplySelectionRulesData(_run, _shop, payload.SelectionContextRules, defaultSingleFreeForReplace: replace);
                }

                foreach (var ci in dealtCards)
                {
                    var cardType = ShopEngine.ParseCardType(ci.Template.Type);
                    string prefix = ResolveSuspendedCardPrefix(cardType);
                    string instId = _shop.NewId(prefix);
                    Guid wireGuid = ResolveSafeWireGuid(ci.Template.Id, cardType);
                    // D2-P1-02 & D3-P1-10：仅真自定义 Mod 卡赋 CustomCardId 并递增 VisualBindingsRevision，纯原版/原位 Patch 恒为 null
                    string? customCardId = RunState.ResolveCustomCardId(ci.Template.Id, wireGuid, _pool.Get(ci.Template.Id));
                    var entry = new RunState.CardEntry
                    {
                        InstanceId = instId,
                        TemplateId = wireGuid,
                        CustomCardId = customCardId,
                        Type = cardType,
                        Size = ShopEngine.ParseSize(ci.Template.Size),
                        Tier = RunState.MapCoreTier(ci.Tier),
                        Owner = null,
                        IsFreeCandidate = _run.SelectionRules.SelectionIsFree,
                        State = ECardState.Alive,
                        CoreCard = ci,
                    };
                    if (!string.IsNullOrWhiteSpace(customCardId))
                        _run.VisualBindingsRevision++;
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
            // W1-B P0-04 & P0-07 接缝：统一收口至 CardInstance.CloneNonCombatState，完整保留三层附魔/属性与挂起光环层
            var srcCore = src.CoreCard;
            clonedCore = srcCore.CloneNonCombatState(newInstanceId);
            if (srcCore.Tier != srcCore.PermanentTier)
                clonedCore.SetTier(srcCore.Tier, isTemporary: true);
            foreach (var kv in srcCore.AuraAdd) clonedCore.AuraAdd[kv.Key] = kv.Value;
            foreach (var kv in srcCore.AuraMult) clonedCore.AuraMult[kv.Key] = kv.Value;
            foreach (var tag in srcCore.AuraTags) clonedCore.AuraTags.Add(tag);
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
        Dictionary<string, int>? suspendedOriginalPrices,
        Dictionary<string, string> idRemap)
    {
        _run.ActiveShelfDiscounts.Clear();
        _run.ActiveShelfOriginalPrices.Clear();
        if (suspendedDiscounts != null)
        {
            foreach (var kv in suspendedDiscounts)
            {
                string key = idRemap.TryGetValue(kv.Key, out string? remapped) ? remapped : kv.Key;
                if (!string.IsNullOrWhiteSpace(key))
                    _run.ActiveShelfDiscounts[key] = kv.Value;
            }
        }
        if (suspendedOriginalPrices != null)
        {
            foreach (var kv in suspendedOriginalPrices)
            {
                string key = idRemap.TryGetValue(kv.Key, out string? remapped) ? remapped : kv.Key;
                if (!string.IsNullOrWhiteSpace(key))
                    _run.ActiveShelfOriginalPrices[key] = kv.Value;
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
            string safeAccId = string.IsNullOrWhiteSpace(accountId) ? $"ghost_{hero}_d{_run.Day}_{Guid.NewGuid():N}" : accountId;
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
