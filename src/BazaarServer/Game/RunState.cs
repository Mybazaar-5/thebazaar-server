using System.Collections.Frozen;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarServer.Data;

namespace BazaarServer.Game;

/// <summary>
/// 局外中断挂起现场快照（例如商店/奇遇期间触发升级，先压栈保存当前现场再进入 LevelUp）。
/// 字段对齐 RunSnapshotSerializer.SuspendedStateSnapshotDto。
/// </summary>
public sealed class SuspendedStateSnapshot
{
    public ERunState State { get; set; }
    public string? CurrentEncounterId { get; set; }
    /// <summary>触发多步子遭遇前的根父级遭遇模板 ID（#19 顶栏头像跨子步继承与挂起恢复）。</summary>
    public string? ParentEncounterTemplateId { get; set; }
    public string? ActiveEncounterCardInstanceId { get; set; }
    public RunState.CardEntry? ActiveEncounterCard { get; set; }
    public List<string> SelectionSet { get; set; } = new();
    public TSelectionContextRules SelectionRules { get; set; } = new();
    public uint RerollCost { get; set; }
    public uint? BaseRerollCost { get; set; }
    public uint RerollsRemaining { get; set; }
    public int SelectionsMadeInCurrentContext { get; set; }
    public int? CurrentMaxSelections { get; set; }
    /// <summary>P1-32：挂起时刻当前货架绿标/光环折扣缓存（InstanceId → 折扣后买价），弹栈时无损还原。</summary>
    public Dictionary<string, int> ActiveShelfDiscounts { get; } = new(StringComparer.Ordinal);
    /// <summary>D2-P1-14：挂起时刻当前货架商品的显式折扣前原价缓存（InstanceId → 原价），弹栈时无损还原。</summary>
    public Dictionary<string, int> ActiveShelfOriginalPrices { get; } = new(StringComparer.Ordinal);
    /// <summary>D2-P1-04：挂起时刻激活的切板场景覆盖 ID（BoardOverride），压栈/序列化/弹栈无损保留。</summary>
    public string? CurrentBoardId { get; set; }
    /// <summary>D2-P1-14：挂起时刻的分页怪物战利品页列表（如 Loot 中升级压栈后弹栈恢复）。</summary>
    public List<List<RunState.MonsterLootPageItemState>> MonsterLootPages { get; } = new();
    /// <summary>D2-P1-14：挂起时刻的分页怪物战利品当前页索引。</summary>
    public int MonsterLootPageIndex { get; set; }
    /// <summary>P1-32：挂起时刻母遭遇暂存的退出奖励经验，弹栈时无损还原。</summary>
    public int PendingEncounterExitXp { get; set; }
    /// <summary>挂起时刻留在货架上的候选卡牌条目（供内存恢复重放时无损放回 RunState.Cards 并重发 CardDealt）。</summary>
    public List<RunState.CardEntry> SuspendedCards { get; } = new();
}

/// <summary>
/// D6-P1-07 / D8-P1-02：不可变对局公开只读摘要快照，供锦标赛/TempoNet/活跃摘要端点零锁跨线程读取，
/// 彻底消灭无锁直读他人可变 RunState 引发的字典并发异常与跨天 (Day, Hour) 撕裂态。
/// </summary>
public sealed record RunPublicSummarySnapshot(
    string RunId,
    EPlayMode PlayMode,
    Guid? TournamentSessionId,
    EHero Hero,
    ERunState State,
    uint Day,
    uint Hour,
    uint Victories,
    uint Defeats,
    uint LastCompletedPvpDay,
    int Prestige,
    bool HasVisitedFates,
    bool IsTerminal,
    bool IsReadyAtHour6Pvp,
    string SpecialModeIdName,
    CustomRunRuleSet RulesSnapshot);

/// <summary>
/// 一个 Run 的服务端权威状态。
/// 与客户端反编译协议 100% 对齐，同时维护 BazaarCore.Runtime.BoardGrid 以实现精准多格占位与挤位重排。
/// </summary>
public sealed class RunState
{
    public string RunId { get; init; }
    public Guid GameModeId { get; init; }
    public EHero Hero { get; init; }
    public EPlayMode PlayMode { get; init; }
    public string? AccountId { get; set; }
    public string Username { get; set; } = string.Empty;
    public Guid? TournamentSessionId { get; set; }

    private CustomRunRuleSet _customRules = new();

    /// <summary>本局应用的自定义规则集（默认 MonsterLootChooseOne = true；赋值 null 时自动回退为默认规则集）。</summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public CustomRunRuleSet CustomRules
    {
        get => _customRules;
        set => _customRules = value ?? new CustomRunRuleSet();
    }

    /// <summary>本局流派倾向是否已锁定（全模式统一：Day 1 Hour 1 开场确认锁定一次，或推进离开开场后自动焊死，严禁途中篡改）。</summary>
    public bool TendencyLocked { get; set; }

    /// <summary>当前对局是否修改了数值强度（为 true 时幽灵镜像自动隔离，不污染标准匹配池）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCustomRuleRun => CustomRules?.IsCustomPowerModified() ?? false;

    /// <summary>是否为沙盒实验室对局（强制三禁隔离：不入镜像池、不计生涯、不上榜单）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSandboxRun => CustomRules?.IsSandboxRun ?? false;

    /// <summary>Boss 车轮战已通关波次数。</summary>
    public int BossRushWavesCleared { get; set; }

    /// <summary>无尽爬塔模式是否已触发过 10 胜阶段里程碑祝贺。</summary>
    public bool EndlessTenWinCelebrated { get; set; }

    /// <summary>每日同种子挑战当前小时内商店刷新计数（用于派生互不干扰的子种子流）。</summary>
    public int DailyRerollSequence { get; set; }

    /// <summary>本局自定义卡视觉绑定变更版本号（随 Mod 卡生成/销毁递增，供响应头 X-Visual-Bindings-Rev 通知客户端增量拉取）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int VisualBindingsRevision { get; set; }

    /// <summary>
    /// 当前/最近一场战斗中敌方瞬态卡牌（含 PvP 幽灵/锦标赛对手/自定义野怪的物品与技能）的自定义模板映射（InstanceId -> CustomCardId）。
    /// 供 /api/mods/active-bindings 实时下发对手的自定义贴图、中文文案、详情预览与计数器，并在战后保留以支持复盘界面。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, string> ActiveCombatOpponentCustomCardIds { get; } = new(StringComparer.Ordinal);

    /// <summary>标记运行态快照是否已脏（用于 MoveItem 高频拖拽延迟到 HTTP 回包后后台防抖序列化）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SnapshotDirty { get; set; }

    /// <summary>分页怪物战利品模式下的单条持久化条目。</summary>
    public sealed record MonsterLootPageItemState(string TemplateId, string Tier, string? Enchantment, int SlotCost);

    /// <summary>分页怪物战利品缓存页（支持断点重连恢复 Loot 翻页状态）。</summary>
    public List<List<MonsterLootPageItemState>> MonsterLootPages { get; } = new();

    /// <summary>分页怪物战利品当前页索引。</summary>
    public int MonsterLootPageIndex { get; set; }

    public uint Day { get; set; } = 1;
    public uint Hour { get; set; } = 1;
    public uint Victories { get; set; }
    public uint Defeats { get; set; }
    /// <summary>P0-16：本局已完成 PvP 结算的最高天数（覆盖胜/负/平局与托管结算，根治 BossRush/同步平局跨天循环）。</summary>
    public uint LastCompletedPvpDay { get; set; }

    /// <summary>是否已触发过 Fates 濒死救援遭遇（Futura）。</summary>
    public bool FatesUsed { get; set; }

    /// <summary>向后兼容属性：与 FatesUsed 共享同一状态。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasVisitedFates
    {
        get => FatesUsed;
        set => FatesUsed = value;
    }

    public string DataVersion { get; set; } = "5.0.0";

    public ERunState State { get; set; } = ERunState.Choice;
    public string? CurrentEncounterId { get; set; }
    public uint RerollCost { get; set; } = 2;
    /// <summary>P1-18：未叠加玩家 RerollCostModifier（如 Arbitrage / Cloche Hat）前的原始刷新基价，供光环增减与连续刷新精确重算。</summary>
    public uint? BaseRerollCost { get; set; }
    public uint RerollsRemaining { get; set; } = 3;
    public List<string> SelectionSet { get; set; } = new();

    public TSelectionContextRules SelectionRules { get; set; } = new()
    {
        CanSelectMultiple = false,
        SelectionIsFree = true,
        CanExit = false,
    };

    /// <summary>局外中断挂起现场栈（例如升级触发时压栈保存当前商店/奇遇，选完升级后弹出恢复）。</summary>
    public Stack<SuspendedStateSnapshot> SuspendedStates { get; } = new();

    /// <summary>待消费的升级奖励次数（支持单次连升多级逐级发放，以及在遭遇战内延后至退场结算）。</summary>
    public int PendingLevelUps { get; set; }

    /// <summary>Wave 4A #1：Hour 5→6 推进因触发升级而暂缓切至 HoursInADay，待全部连升结束后再推进至 Hour 6 PvP。</summary>
    public bool PendingAdvanceHourAfterLevelUp { get; set; }

    /// <summary>Wave 4A #1：记录已派发过 OnHourStarted 的 (Day, Hour)，防止极端边界回退重试时重复派发同小时起始效果。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (uint Day, uint Hour)? HourStartEffectsDispatchedFor { get; set; }

    /// <summary>向后兼容计算属性：是否存在待消费的升级奖励。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PendingLevelUp => PendingLevelUps > 0;

    /// <summary>当前交互上下文内已完成选卡的次数。</summary>
    public int SelectionsMadeInCurrentContext { get; set; }

    /// <summary>当前选择上下文的显式最大可选次数（持久化以防止断点续玩/翻页丢失多选上限）。</summary>
    public int? CurrentMaxSelections { get; set; }

    /// <summary>P1-33：当前货架商品的折扣价映射（InstanceId → 折后买价，保留既有公开字典签名零破损兼容）。</summary>
    public Dictionary<string, int> ActiveShelfDiscounts { get; } = new(StringComparer.Ordinal);

    /// <summary>D2-P1-14：当前货架打折商品的显式折扣前原价平行映射（InstanceId → 折扣前原价），消除恢复反推失真。</summary>
    public Dictionary<string, int> ActiveShelfOriginalPrices { get; } = new(StringComparer.Ordinal);

    private volatile RunPublicSummarySnapshot? _publicSummary;
    private CustomRunRuleSet? _cachedImmutableRulesSnapshot;
    private IReadOnlyDictionary<string, MutatorMetadata>? _lastEffectiveCustomMutators;

    /// <summary>D6-P1-07 / D8-P1-02：跨线程零锁只读摘要快照（首次访问惰性发布，状态推进后经 PublishPublicSummary 原子刷新）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RunPublicSummarySnapshot PublicSummary => _publicSummary ?? PublishPublicSummary();

    /// <summary>刷新不可变规则快照缓存（仅在 StartRun / DeserializeFromDto / 修改 CustomRules 时调用，避免热路径高频克隆；支持透传隔离 Mod 自定义词条字典）。</summary>
    public void RefreshImmutableRulesSnapshot(IReadOnlyDictionary<string, MutatorMetadata>? extraMutators = null)
    {
        if (extraMutators != null)
            _lastEffectiveCustomMutators = extraMutators;
        var effMutators = extraMutators ?? _lastEffectiveCustomMutators;
        _cachedImmutableRulesSnapshot = (CustomRules ?? new CustomRunRuleSet()).CloneNormalized(
            effMutators,
            preserveUnregisteredMutators: effMutators == null);
    }

    /// <summary>原子发布最新不可变公开摘要快照（零锁，供锦标赛/TempoNet/活跃摘要跨线程安全消费）。</summary>
    public RunPublicSummarySnapshot PublishPublicSummary()
    {
        var rulesSnap = _cachedImmutableRulesSnapshot;
        if (rulesSnap == null)
        {
            var effMutators = _lastEffectiveCustomMutators;
            rulesSnap = (CustomRules ?? new CustomRunRuleSet()).CloneNormalized(
                effMutators,
                preserveUnregisteredMutators: effMutators == null);
            _cachedImmutableRulesSnapshot = rulesSnap;
        }

        var state = State;
        uint day = Day;
        uint hour = Hour;
        bool isTerminal = state is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown;
        bool hasCompletedPvpForDay = LastCompletedPvpDay >= day
            || (LastCompletedPvpDay == 0
                && rulesSnap.SpecialModeId != ESpecialModeId.BossRush
                && (Victories + Defeats) >= day);
        bool isReadyAtHour6Pvp = !isTerminal && (
            state == ERunState.TournamentPvpChoice
            || state == ERunState.TournamentWaitingRoom
            || state == ERunState.PVPCombat
            || (hour >= 6 && hasCompletedPvpForDay)
            || (state == ERunState.Choice && hour >= 6));
        int prestige = PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, GameDefaults.StartingPrestige);

        var snap = new RunPublicSummarySnapshot(
            RunId: RunId ?? string.Empty,
            PlayMode: PlayMode,
            TournamentSessionId: TournamentSessionId,
            Hero: Hero,
            State: state,
            Day: day,
            Hour: hour,
            Victories: Victories,
            Defeats: Defeats,
            LastCompletedPvpDay: LastCompletedPvpDay,
            Prestige: prestige,
            HasVisitedFates: FatesUsed,
            IsTerminal: isTerminal,
            IsReadyAtHour6Pvp: isReadyAtHour6Pvp,
            SpecialModeIdName: rulesSnap.SpecialModeId.ToString(),
            RulesSnapshot: rulesSnap);
        _publicSummary = snap;
        return snap;
    }

    /// <summary>P2-26：IsTakeover 遭遇的局内 SpawningChanceIncrement 累积概率表（TemplateId → 已累加增量）。</summary>
    public Dictionary<string, double> TakeoverChanceBonuses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>P1-37：是否已完成开局选卡流程（升级为 RunState 一等持久化属性，避免防抖写盘误用 InferStartSelectionDone 覆盖预设开局）。</summary>
    public bool StartSelectionDone { get; set; }

    /// <summary>P1-35：断线恢复结算中断战斗若直接进入终局，暂存待 BuildResumeEvents 补发的 RunCompleted 事件（纯内存态，不落盘）。</summary>
    public GameSimEventRunCompleted? PendingResumeRunCompletedEvent { get; set; }

    /// <summary>P1-40：进入 LevelUp 态之前已解锁的槽位掩码，用于离开升级态切回 Choice/Encounter 时补发幂等 GameSimEventSocketsUnlocked。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ushort? LevelUpEntryUnlockedSlots { get; set; }

    /// <summary>P1-09：上一轮局外光环收敛后的目标集合快照（key = "{sourceCardId}|{auraId}" -> 目标键集合 "C:{id}" / "P:{id}"），用于差量发射真实 GameSimEventEffectAuraExecuted。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, HashSet<string>> LastOutOfCombatAuraTargets { get; } = new(StringComparer.Ordinal);

    /// <summary>是否在退出当前战败 Loot 退场步骤后清空远征替换集状态（先执行 RewardDefeat 退场步骤再清理远征）。</summary>
    public bool PendingClearExpeditionAfterLoot { get; set; }

    /// <summary>下一次构建全量 GameSim 时需随包临时附带的瞬态卡牌（如技能二合一融合源卡 Placement(Owner=Player)），构建后自动清空。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<CardEntry> PendingTransientSimCards { get; } = new();

    /// <summary>外部沙盒修改后挂起的待同步事件队列（纯内存态，不落盘，由 sandbox_sync 或下一条命令消费）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<IGameSimEvent> PendingExternalSyncEvents { get; } = new();

    /// <summary>外部沙盒复杂修改后标记下一次命令响应需追加全量 GameStateSync（纯内存态，不落盘）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PendingExternalForceStateSync { get; set; }

    /// <summary>
    /// 消费并清空挂起的外部沙盒同步事件与强制同步标记，合并到目标事件列表头部。
    /// </summary>
    public bool ConsumePendingExternalSync(List<IGameSimEvent> targetEvents, ref bool forceStateSync)
    {
        bool hadAny = PendingExternalSyncEvents.Count > 0 || PendingExternalForceStateSync;
        if (!hadAny) return false;

        if (PendingExternalSyncEvents.Count > 0)
        {
            targetEvents.InsertRange(0, PendingExternalSyncEvents);
            PendingExternalSyncEvents.Clear();
        }
        if (PendingExternalForceStateSync)
        {
            forceStateSync = true;
            PendingExternalForceStateSync = false;
        }
        return true;
    }

    /// <summary>单局运行期随机种子（供快照序列化/恢复保持随机序列衔接）。</summary>
    public int RngSeed { get; set; }

    /// <summary>本局主种子，StartRun 定下后不再改（旧存档为 0 时走旧逻辑）。</summary>
    public int MasterSeed { get; set; }

    /// <summary>可读种子码（用于展示与蓝图导出）。</summary>
    public string? SeedText { get; set; }

    /// <summary>仅用于界面显示「种子局」徽标，不影响任何玩法/榜单/镜像池判定。</summary>
    public bool IsCustomSeededRun { get; set; }

    /// <summary>开局命中固定对手剧本时记录的剧本名称（持久化以确保重连/重启后 active-run-tendency 准确展示）。</summary>
    public string? PinnedLineupName { get; set; }

    /// <summary>当前 (Day, Hour) 内每条确定性随机流已用次数，供 RunRandom.Stream 自动递增防撞车。</summary>
    public Dictionary<string, int> StreamOccurrences { get; } = new(StringComparer.Ordinal);

    /// <summary>StreamOccurrences 对应的 Day * 100 + Hour 时间戳，换小时时自动清空。</summary>
    public int StreamOccurrenceStamp { get; set; }

    /// <summary>卡牌实例 ID 递增计数器（持久化以防重连后实例 ID 碰撞与排序偏移）。</summary>
    public int NextInstanceSeq { get; set; }

    /// <summary>本局固定剧本镜像缓存（按需从 DB 加载，非持久化；单测可直接赋值）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<int, PvpGhostPool.GhostBoard>? PinnedGhostCache { get; set; }

    /// <summary>开局一次性中文提示文案（如所选剧本不存在，由客户端拉取展示后清空，非持久化）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PendingStartToastZh { get; set; }

    /// <summary>进入当前奇遇时暂存的退出奖励经验（延迟至离开/推进奇遇时再结算，避免进门先跳升级，修复 Bug #12/#16）。</summary>
    public int PendingEncounterExitXp { get; set; }

    /// <summary>本小时是否已发放过基础小时经验（对齐官方 GainedXpPerHourThisHour 去重闸门，杜绝 1 经验事件双算为 2 经验）。</summary>
    public bool GainedHourXpThisHour { get; set; }

    /// <summary>本小时推进至下一小时时是否已发放过固定小时推进经验（与遭遇自身经验解耦的独立门控）。</summary>
    public bool GainedAdvanceHourXpThisHour { get; set; }

    /// <summary>Hour 6 Choice 阶段锁定并持久化的一致化 PvP 对手镜像（保证选择页展示、断点续玩与进战 100% 为同一对手）。</summary>
    public PvpGhostPool.GhostBoard? PendingPvpGhost { get; set; }

    /// <summary>当前所处遭遇自身对应的核心卡牌实例（用于 Salvage Yard 等 TTriggerOnCardSold/Purchased 遭遇触发，修复 Bug #11）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CardInstance? CurrentEncounterCoreCard { get; set; }

    /// <summary>当前激活遭遇卡在 RunState.Cards 中的实例 ID（保留于顶栏头像槽 Socket_5，直到离开遭遇才销毁）。</summary>
    public string? ActiveEncounterCardInstanceId { get; set; }

    /// <summary>
    /// 当前事件链根父级遭遇模板 ID（进入 <c>EventEncounter</c> / <c>PedestalEncounter</c> 时记录；
    /// 推进到子级 <c>EncounterStep</c> 时继续保留并用于同步顶栏 <c>Socket_5</c> 头像卡与悬停详情，
    /// 直到退出遭遇或进战前清理才清空；用于服务端 #19 修复）。
    /// </summary>
    public string? ParentEncounterTemplateId { get; set; }

    /// <summary>初始 4 格中心地毯：Socket_3..Socket_6 (0b00_0111_1000 = 120)。</summary>
    public ushort UnlockedSlots { get; set; } = 0b00_0111_1000;

    /// <summary>与客户端 HandSocketUnlockMask 语义对齐的同步包装属性。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ushort HandSocketUnlockMask
    {
        get => UnlockedSlots;
        set
        {
            UnlockedSlots = value;
            HandGrid.UnlockedMask = value;
        }
    }

    /// <summary>手牌 10 格与背包 10 格的权威占位网格。</summary>
    public BoardGrid HandGrid { get; } = new(GameDefaults.InitialUnlockedSlotsMask);
    public BoardGrid StashGrid { get; } = new(1023);

    /// <summary>玩家权威属性展示字典（必含 Health/HealthMax/Shield/Joy 等客户端 UI 强依赖键）。</summary>
    public Dictionary<EPlayerAttributeType, int> PlayerAttributes { get; } =
        CreateDefaultCombatantAttributes(
            hp: GameDefaults.StartingHealth,
            hpMax: GameDefaults.StartingHealth,
            prestige: GameDefaults.StartingPrestige,
            level: 1,
            gold: GameDefaults.StartingGold,
            income: GameDefaults.StartingIncome);

    /// <summary>D2-P1-08：玩家永久基础属性独立真源（彻底消灭 mult==0 反除摧毁基础值）。</summary>
    public Dictionary<string, double> BasePlayerAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>D2-P1-08：上次由分层公式推导出的玩家展示属性值，用于感知外部/单测直写 PlayerAttributes 的增量变化。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<EPlayerAttributeType, int> LastDerivedPlayerAttributes { get; } = new();

    /// <summary>玩家局外限时属性层（UntilEndOfHour / UntilEndOfDay），Mult 严格存储纯净 PureMult，AdditiveMult 独立存储。</summary>
    public Dictionary<string, double> PlayerHourAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerHourMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerHourAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerDayAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerDayMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerDayAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>局外玩家属性光环修饰层（Mult 严格存储纯净 PureMult，AdditiveMult 独立存储）。</summary>
    public Dictionary<string, double> PlayerAuraAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerAuraMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerAuraAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>D2-P1-08：装配入核时的永久基础值与有效展示值双快照，供局外批次精确分离真实引擎修饰层变化与外部手工合成批次。</summary>
    public sealed record CoreBuildAttributeSnapshot(
        Dictionary<string, double> BaseAttributes,
        Dictionary<string, double> EffectiveAttributes);

    /// <summary>D2-P1-08：按 PlayerState 实例弱引用记录装配入核时的双快照（支持递归嵌套且零泄漏）。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayerState, CoreBuildAttributeSnapshot> CoreBuildBaseSnapshots = new();

    /// <summary>获取指定 PlayerState 在 ApplyLayeredPlayerStateTo 时刻记录的基础与有效值双快照。</summary>
    public static bool TryGetCoreBuildBaseSnapshot(PlayerState ps, out CoreBuildAttributeSnapshot? snapshot)
        => CoreBuildBaseSnapshots.TryGetValue(ps, out snapshot);

    /// <summary>
    /// D2-P1-08 单一权威正向装配器：将本 RunState 的基础属性与 9 张修饰层（Hour/Day/Aura × Add/PureMult/AdditiveMult）
    /// 无损装载入 <paramref name="ps"/>，全程 0 反除毁档、0 双重乘算，同时兼容外部/单测直写 PlayerAttributes。
    /// </summary>
    public void ApplyLayeredPlayerStateTo(PlayerState ps)
    {
        ArgumentNullException.ThrowIfNull(ps);

        var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in BasePlayerAttributes.Keys) allKeys.Add(k);
        foreach (var ak in PlayerAttributes.Keys) allKeys.Add(ak.ToString());
        foreach (var k in PlayerHourAdd.Keys) allKeys.Add(k);
        foreach (var k in PlayerHourMult.Keys) allKeys.Add(k);
        foreach (var k in PlayerHourAdditiveMult.Keys) allKeys.Add(k);
        foreach (var k in PlayerDayAdd.Keys) allKeys.Add(k);
        foreach (var k in PlayerDayMult.Keys) allKeys.Add(k);
        foreach (var k in PlayerDayAdditiveMult.Keys) allKeys.Add(k);
        foreach (var k in PlayerAuraAdd.Keys) allKeys.Add(k);
        foreach (var k in PlayerAuraMult.Keys) allKeys.Add(k);
        foreach (var k in PlayerAuraAdditiveMult.Keys) allKeys.Add(k);

        foreach (string key in allKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;

            double totalAdd = PlayerHourAdd.GetValueOrDefault(key, 0.0)
                            + PlayerDayAdd.GetValueOrDefault(key, 0.0)
                            + PlayerAuraAdd.GetValueOrDefault(key, 0.0);
            double totalAdditiveMult = PlayerHourAdditiveMult.GetValueOrDefault(key, 0.0)
                                     + PlayerDayAdditiveMult.GetValueOrDefault(key, 0.0)
                                     + PlayerAuraAdditiveMult.GetValueOrDefault(key, 0.0);
            double totalPureMult = PlayerHourMult.GetValueOrDefault(key, 1.0)
                                 * PlayerDayMult.GetValueOrDefault(key, 1.0)
                                 * PlayerAuraMult.GetValueOrDefault(key, 1.0);
            double effectiveMult = (1.0 + totalAdditiveMult) * totalPureMult;

            bool hasAnyModifier = Math.Abs(totalAdd) > 1e-9
                               || Math.Abs(totalAdditiveMult) > 1e-9
                               || Math.Abs(totalPureMult - 1.0) > 1e-9;

            bool hasEnum = Enum.TryParse<EPlayerAttributeType>(key, true, out var ak);
            int shownVal = 0;
            bool hasShown = hasEnum && PlayerAttributes.TryGetValue(ak, out shownVal);

            double resolvedBase;
            if (!BasePlayerAttributes.TryGetValue(key, out double storedBase) || !double.IsFinite(storedBase))
            {
                if (!hasAnyModifier)
                {
                    resolvedBase = hasShown ? shownVal : ps.Attributes.GetValueOrDefault(key, 0.0);
                }
                else if (Math.Abs(effectiveMult) > 1e-6)
                {
                    // 历史旧快照首次载入的一次性安全迁移
                    resolvedBase = (shownVal / effectiveMult) - totalAdd;
                }
                else
                {
                    // effectiveMult == 0 时绝不反除归零，退回合理默认基线
                    resolvedBase = ResolveFallbackBaseAttribute(key);
                }
                BasePlayerAttributes[key] = resolvedBase;
            }
            else if (hasShown && hasEnum)
            {
                if (LastDerivedPlayerAttributes.TryGetValue(ak, out int lastDerived))
                {
                    if (shownVal == lastDerived)
                    {
                        resolvedBase = storedBase;
                    }
                    else if (!hasAnyModifier)
                    {
                        resolvedBase = shownVal;
                        BasePlayerAttributes[key] = resolvedBase;
                    }
                    else if (Math.Abs(effectiveMult) <= 1e-6)
                    {
                        // mult == 0 场景外部直接增量改写展示值时按纯加法增量回写，绝不除以 0 毁档
                        resolvedBase = storedBase + (shownVal - lastDerived);
                        BasePlayerAttributes[key] = resolvedBase;
                    }
                    else
                    {
                        resolvedBase = storedBase + ((shownVal - lastDerived) / effectiveMult);
                        BasePlayerAttributes[key] = resolvedBase;
                    }
                }
                else if (!hasAnyModifier)
                {
                    resolvedBase = shownVal;
                    BasePlayerAttributes[key] = resolvedBase;
                }
                else
                {
                    resolvedBase = storedBase;
                }
            }
            else
            {
                resolvedBase = storedBase;
            }

            ps.Attributes[key] = resolvedBase;
        }

        // 通过同程序集 PlayerLayerModifierBridge 精确同步 9 张修饰层字典（先清旧层，且 pure==0.0 精确落盘，0 双重乘算）
        PlayerLayerModifierBridge.ApplyHourLayer(ps, PlayerHourAdd, PlayerHourMult, PlayerHourAdditiveMult);
        PlayerLayerModifierBridge.ApplyDayLayer(ps, PlayerDayAdd, PlayerDayMult, PlayerDayAdditiveMult);
        PlayerLayerModifierBridge.ApplyAuraLayer(ps, PlayerAuraAdd, PlayerAuraMult, PlayerAuraAdditiveMult);

        // 刷新展示推导缓存，保证下一轮无外部篡改时严格 0 反除
        foreach (EPlayerAttributeType ak in PlayerAttributes.Keys.ToList())
        {
            if (ak == EPlayerAttributeType.Health)
            {
                LastDerivedPlayerAttributes[ak] = PlayerAttributes[ak];
                continue;
            }
            LastDerivedPlayerAttributes[ak] = (int)Math.Round(ps.GetAttribute(ak.ToString()));
        }

        int hp = PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, ps.HealthMax);
        ps.CurrentHealth = Math.Clamp(hp, 0, Math.Max(1, ps.HealthMax));
        var effSnap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in ps.Attributes.Keys)
            effSnap[k] = ps.GetAttribute(k);
        CoreBuildBaseSnapshots.AddOrUpdate(ps, new CoreBuildAttributeSnapshot(
            new Dictionary<string, double>(ps.Attributes, StringComparer.OrdinalIgnoreCase),
            effSnap));
    }

    /// <summary>
    /// D2-P1-08 单一权威正向回写器：从 <paramref name="ps"/> 精确提取 9 张修饰层与永久基础值，
    /// 再正向推导更新展示值 PlayerAttributes 与 LastDerivedPlayerAttributes。
    /// </summary>
    public void CaptureLayeredPlayerStateFrom(
        PlayerState ps,
        bool captureBaseValues = true,
        bool includeAllExistingAttrs = true)
    {
        ArgumentNullException.ThrowIfNull(ps);

        CopyCleanLayerDict(ps.HourAdd, PlayerHourAdd, zeroDefault: 0.0);
        PlayerLayerModifierBridge.CopyHourPureMults(ps, PlayerHourMult);
        CopyCleanLayerDict(ps.HourAdditiveMult, PlayerHourAdditiveMult, zeroDefault: 0.0);

        CopyCleanLayerDict(ps.DayAdd, PlayerDayAdd, zeroDefault: 0.0);
        PlayerLayerModifierBridge.CopyDayPureMults(ps, PlayerDayMult);
        CopyCleanLayerDict(ps.DayAdditiveMult, PlayerDayAdditiveMult, zeroDefault: 0.0);

        CopyCleanLayerDict(ps.AuraAdd, PlayerAuraAdd, zeroDefault: 0.0);
        PlayerLayerModifierBridge.CopyAuraPureMults(ps, PlayerAuraMult);
        CopyCleanLayerDict(ps.AuraAdditiveMult, PlayerAuraAdditiveMult, zeroDefault: 0.0);

        if (captureBaseValues)
        {
            foreach (var (k, v) in ps.Attributes)
            {
                if (!string.IsNullOrWhiteSpace(k) && double.IsFinite(v))
                    BasePlayerAttributes[k] = v;
            }
        }

        var keysToDerive = new HashSet<EPlayerAttributeType>();
        if (includeAllExistingAttrs)
        {
            foreach (var ak in PlayerAttributes.Keys) keysToDerive.Add(ak);
        }
        foreach (var k in ps.Attributes.Keys)
            if (Enum.TryParse<EPlayerAttributeType>(k, true, out var ak)) keysToDerive.Add(ak);
        foreach (var k in PlayerHourAdd.Keys.Concat(PlayerHourMult.Keys).Concat(PlayerHourAdditiveMult.Keys)
                     .Concat(PlayerDayAdd.Keys).Concat(PlayerDayMult.Keys).Concat(PlayerDayAdditiveMult.Keys)
                     .Concat(PlayerAuraAdd.Keys).Concat(PlayerAuraMult.Keys).Concat(PlayerAuraAdditiveMult.Keys))
        {
            if (Enum.TryParse<EPlayerAttributeType>(k, true, out var ak)) keysToDerive.Add(ak);
        }

        foreach (var ak in keysToDerive)
        {
            if (ak == EPlayerAttributeType.Health) continue;
            int eff = (int)Math.Round(ps.GetAttribute(ak.ToString()));
            if (ak == EPlayerAttributeType.HealthMax)
                eff = Math.Max(1, eff);
            else if (ak is EPlayerAttributeType.Gold or EPlayerAttributeType.Prestige or EPlayerAttributeType.Experience)
                eff = Math.Max(0, eff);
            PlayerAttributes[ak] = eff;
            LastDerivedPlayerAttributes[ak] = eff;
        }
    }

    /// <summary>同步设置某玩家属性的永久基础值并立即正向重算其展示值（供开局/升级/直接增减基础资源调用）。</summary>
    public void SetBasePlayerAttribute(EPlayerAttributeType attr, double baseValue)
    {
        if (!double.IsFinite(baseValue)) return;
        string key = attr.ToString();
        BasePlayerAttributes[key] = baseValue;
        int eff = ComputeDerivedPlayerAttribute(attr, baseValue);
        PlayerAttributes[attr] = eff;
        LastDerivedPlayerAttributes[attr] = eff;
    }

    /// <summary>检测并吸收外部/单测直接改写 PlayerAttributes[attr] 所带来的基础值增量（零反除归零）。</summary>
    private void SyncExternalPlayerAttributeMutationIfNeeded(EPlayerAttributeType attr)
    {
        if (attr == EPlayerAttributeType.Health) return;
        string key = attr.ToString();
        if (!PlayerAttributes.TryGetValue(attr, out int shownVal))
            return;

        double totalAdd = PlayerHourAdd.GetValueOrDefault(key, 0.0)
                        + PlayerDayAdd.GetValueOrDefault(key, 0.0)
                        + PlayerAuraAdd.GetValueOrDefault(key, 0.0);
        double totalAdditiveMult = PlayerHourAdditiveMult.GetValueOrDefault(key, 0.0)
                                 + PlayerDayAdditiveMult.GetValueOrDefault(key, 0.0)
                                 + PlayerAuraAdditiveMult.GetValueOrDefault(key, 0.0);
        double totalPureMult = PlayerHourMult.GetValueOrDefault(key, 1.0)
                             * PlayerDayMult.GetValueOrDefault(key, 1.0)
                             * PlayerAuraMult.GetValueOrDefault(key, 1.0);
        double effectiveMult = (1.0 + totalAdditiveMult) * totalPureMult;
        bool hasAnyModifier = Math.Abs(totalAdd) > 1e-9
                           || Math.Abs(totalAdditiveMult) > 1e-9
                           || Math.Abs(totalPureMult - 1.0) > 1e-9;

        if (!BasePlayerAttributes.TryGetValue(key, out double storedBase) || !double.IsFinite(storedBase))
        {
            if (!hasAnyModifier)
                BasePlayerAttributes[key] = shownVal;
            else if (Math.Abs(effectiveMult) > 1e-6)
                BasePlayerAttributes[key] = (shownVal / effectiveMult) - totalAdd;
            else
                BasePlayerAttributes[key] = ResolveFallbackBaseAttribute(key);
            return;
        }

        if (LastDerivedPlayerAttributes.TryGetValue(attr, out int lastDerived))
        {
            if (shownVal == lastDerived)
                return;
            if (!hasAnyModifier)
                BasePlayerAttributes[key] = shownVal;
            else if (Math.Abs(effectiveMult) <= 1e-6)
                BasePlayerAttributes[key] = storedBase + (shownVal - lastDerived);
            else
                BasePlayerAttributes[key] = storedBase + ((shownVal - lastDerived) / effectiveMult);
        }
        else if (!hasAnyModifier)
        {
            BasePlayerAttributes[key] = shownVal;
        }
    }

    /// <summary>对某玩家属性永久基础值做增量调整，并正向重算其展示值（绝不走反除）。</summary>
    public int AdjustBasePlayerAttribute(EPlayerAttributeType attr, int delta, int? minBase = null)
        => AdjustBasePlayerAttributeDouble(attr, delta, minBase);

    /// <summary>对某玩家属性永久基础值做浮点增量调整，并正向重算其展示值（先同步外部直写增量，零反除归零）。</summary>
    public int AdjustBasePlayerAttributeDouble(EPlayerAttributeType attr, double delta, int? minBase = null)
    {
        SyncExternalPlayerAttributeMutationIfNeeded(attr);
        string key = attr.ToString();
        double curBase = BasePlayerAttributes.TryGetValue(key, out double b) && double.IsFinite(b)
            ? b
            : PlayerAttributes.GetValueOrDefault(attr, 0);
        double nextBase = curBase + delta;
        if (minBase.HasValue && nextBase < minBase.Value)
            nextBase = minBase.Value;
        BasePlayerAttributes[key] = nextBase;
        int eff = ComputeDerivedPlayerAttribute(attr, nextBase);
        PlayerAttributes[attr] = eff;
        LastDerivedPlayerAttributes[attr] = eff;
        return eff;
    }

    /// <summary>按当前已落盘的永久基础值与 9 张修饰层正向重算指定玩家属性的展示值（不动 BasePlayerAttributes）。</summary>
    public int RecomputeDerivedPlayerAttribute(EPlayerAttributeType attr)
    {
        SyncExternalPlayerAttributeMutationIfNeeded(attr);
        string key = attr.ToString();
        double curBase = BasePlayerAttributes.TryGetValue(key, out double b) && double.IsFinite(b)
            ? b
            : PlayerAttributes.GetValueOrDefault(attr, 0);
        int eff = ComputeDerivedPlayerAttribute(attr, curBase);
        PlayerAttributes[attr] = eff;
        LastDerivedPlayerAttributes[attr] = eff;
        return eff;
    }

    private int ComputeDerivedPlayerAttribute(EPlayerAttributeType attr, double baseValue)
    {
        if (attr == EPlayerAttributeType.Health)
        {
            int maxHp = Math.Max(1, PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, GameDefaults.StartingHealth));
            return Math.Clamp((int)Math.Round(baseValue), 0, maxHp);
        }
        string key = attr.ToString();
        double totalAdd = PlayerHourAdd.GetValueOrDefault(key, 0.0)
                        + PlayerDayAdd.GetValueOrDefault(key, 0.0)
                        + PlayerAuraAdd.GetValueOrDefault(key, 0.0);
        double totalAdditiveMult = PlayerHourAdditiveMult.GetValueOrDefault(key, 0.0)
                                 + PlayerDayAdditiveMult.GetValueOrDefault(key, 0.0)
                                 + PlayerAuraAdditiveMult.GetValueOrDefault(key, 0.0);
        double totalPureMult = PlayerHourMult.GetValueOrDefault(key, 1.0)
                             * PlayerDayMult.GetValueOrDefault(key, 1.0)
                             * PlayerAuraMult.GetValueOrDefault(key, 1.0);
        double derived = (baseValue + totalAdd) * (1.0 + totalAdditiveMult) * totalPureMult;
        int eff = double.IsFinite(derived) ? (int)Math.Round(derived) : 0;
        if (attr == EPlayerAttributeType.HealthMax)
            return Math.Max(1, eff);
        if (attr is EPlayerAttributeType.Gold or EPlayerAttributeType.Prestige or EPlayerAttributeType.Experience)
            return Math.Max(0, eff);
        return eff;
    }

    private static double ResolveFallbackBaseAttribute(string key) => key.ToLowerInvariant() switch
    {
        "healthmax" or "health" => GameDefaults.StartingHealth,
        "prestige" => GameDefaults.StartingPrestige,
        "income" => GameDefaults.StartingIncome,
        "level" => 1,
        _ => 0.0,
    };

    private static void CopyCleanLayerDict(Dictionary<string, double> src, Dictionary<string, double> dst, double zeroDefault)
    {
        dst.Clear();
        foreach (var (k, v) in src)
        {
            if (!string.IsNullOrWhiteSpace(k) && double.IsFinite(v) && Math.Abs(v - zeroDefault) >= 1e-9)
                dst[k] = v;
        }
    }

    /// <summary>本局已生成/触发过的 IsSingleSpawn 奇遇模板 ID，避免重复出现。</summary>
    public HashSet<string> VisitedSingleSpawnEncounterIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当天已访问过的遭遇模板 ID（用于跨小时同天去重，跨天时清空）。</summary>
    public HashSet<string> VisitedDayEncounterIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 当天常规小时（Hour 1/2/4/5）已在桌面展示过的遭遇模板 ID（含跳过未点击的商人/奇遇/右槽事件），
    /// 与 <see cref="VisitedDayEncounterIds"/> 共同构成官方「同日刷出即锁定（Once-Per-Day）」无放回抽签集合。
    /// </summary>
    public HashSet<string> OfferedDayEncounterIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>记录 <see cref="OfferedDayEncounterIds"/> 对应的游戏天数（跨天或重置时自动清零）。</summary>
    public uint OfferedDayEncounterDay { get; set; }

    /// <summary>记录 <see cref="OfferedDayEncounterIds"/> 最近一次写入时的小时（同小时重发或回退时自动清零）。</summary>
    public uint OfferedDayEncounterLastHour { get; set; }

    /// <summary>清空当日已展示遭遇追踪（跨天/重开时调用）。</summary>
    public void ClearOfferedDayEncounters()
    {
        OfferedDayEncounterIds.Clear();
        OfferedDayEncounterDay = 0;
        OfferedDayEncounterLastHour = 0;
    }

    /// <summary>
    /// P1-01：一等公民替换遭遇集（Replacement Set）GUID（对应 GameData.db cards.NewSetIdOnExit 的 5 个官方 GUID 与 Mod 自定义替换集）；
    /// 非空时由 ShopEngine 数据驱动替换集注册表接管白昼发牌，直至触发 TActionExitReplacementSet 或自然结束时清空。
    /// </summary>
    public string? ActiveReplacementSetId { get; set; }

    /// <summary>
    /// 当前激活的多小时探险替换集（ReplacementSet）根房间 ID（Day 6 CrashSite / Day 9 Jungle Temple / Day 10 Tiny World）；
    /// 与 <see cref="ActiveReplacementSetId"/> 联动维护以兼容既有快照与外部查询。
    /// </summary>
    public string? ActiveExpeditionId { get; set; }

    /// <summary>当前探险分支变体（例如神庙探险 Hour 1 选择的门："Fiery" / "Toxic" / "Obsidian"）。</summary>
    public string? ActiveExpeditionVariant { get; set; }

    /// <summary>当前探险内已进入过的房间 ID 集合（用于去重与多舱室推进）。</summary>
    public HashSet<string> ExpeditionVisitedRoomIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>P1-04：当前生效的切板场景覆盖 ID（源自遭遇卡 BoardOverride，退出探险/切板遭遇时复位为 null，并同步写入 RunStateSnapshotDTO.Board）。</summary>
    public string? CurrentBoardId { get; set; }

    /// <summary>清空当前替换遭遇集与探险上下文状态（不含 CurrentBoardId，由调用方按需伴随 StateTransitioned 发复位事件）。</summary>
    public void ClearActiveReplacementSet()
    {
        ActiveReplacementSetId = null;
        ActiveExpeditionId = null;
        ActiveExpeditionVariant = null;
        ExpeditionVisitedRoomIds.Clear();
    }

    /// <summary>
    /// 跨天遭遇账本清理（与 RunEngine.AdvanceHourOrDay 和搜种推演共用，严格保持原三行顺序）。
    /// </summary>
    public void ApplyNewDayEncounterBookkeeping()
    {
        VisitedDayEncounterIds.Clear();
        ClearOfferedDayEncounters();
        ClearActiveReplacementSet();
    }

    /// <summary>本局运行期动态遭遇排除集（初始化自 GameMode.ExclusionSet，并支持 Burn it Down 等事件动态增删）。</summary>
    public HashSet<string> ExclusionSet { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否已用 GameMode.ExclusionSet 初始化过本局运行期 ExclusionSet（兼容旧快照不重复覆盖动态移除项）。</summary>
    public bool ExclusionSetInitialized { get; set; }

    /// <summary>当前对战对手属性（战斗态有效；非战斗态与战后清场由 ClearNonCombatOpponentState 清理，且非战斗态协议包不再下发）。</summary>
    public Dictionary<EPlayerAttributeType, int> OpponentAttributes { get; set; } =
        CreateDefaultCombatantAttributes(hp: 100, hpMax: 100, prestige: 20, level: 1, gold: 0, income: 5);

    /// <summary>P1-09：当前战斗对手英雄（仅战斗态有效；非战斗态与战后清场置空）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public EHero? OpponentHero { get; set; }

    public SimPvpOpponent CurrentPvpOpponent { get; set; }

    /// <summary>
    /// P1-09：战后清场同步清空对手英雄、PvP 对手摘要与非战斗态对手实体属性，
    /// 避免后续协议包或状态快照残留战中对手血量/英雄信息。
    /// </summary>
    public void ClearNonCombatOpponentState()
    {
        OpponentHero = null;
        PendingPvpGhost = null;
        CurrentPvpOpponent = null;
        OpponentAttributes.Clear();
    }

    /// <summary>牌库与场面上已存在的卡：instanceId → 卡快照信息。</summary>
    public Dictionary<string, CardEntry> Cards { get; } = new();

    public sealed class CardEntry
    {
        public string InstanceId { get; init; }
        /// <summary>下发给官方客户端的合法原版白名单模板 GUID（Mod 自定义卡时为 BaseVisualTemplateId，确保 0 白块 0 崩溃）。</summary>
        public Guid TemplateId { get; init; }
        /// <summary>若本实例为 Mod 自定义卡，记录其逻辑模板 ID（如 bazaar-chronicles-sample:starport_compass_blade）；原版卡为 null。</summary>
        public string? CustomCardId { get; set; }
        public ECardType Type { get; init; } = ECardType.Item;
        public ECardSize Size { get; set; } = ECardSize.Medium;
        public ETier Tier { get; set; } = ETier.Bronze;
        public EEnchantmentType? Enchantment { get; set; }
        public ECombatantId? Owner { get; set; }
        public EContainerSocketId? Socket { get; set; }
        public EInventorySection? Section { get; set; }
        public ECardState State { get; set; } = ECardState.Alive;
        public HashSet<EHero> Heroes { get; } = new();
        public HashSet<ECardTag> Tags { get; } = new();
        public HashSet<EHiddenTag> HiddenTags { get; } = new();
        public Dictionary<ECardAttributeType, int> Attributes { get; } = new();
        /// <summary>已向客户端发送过 QuestCompleted 事件的任务属性集合（防重复派发）。</summary>
        public HashSet<string> SyncedCompletedQuests { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>需要在下一条 NetMessageGameSim.Data.Cards 中强制显式下发的属性归零/校准集合（如战后 Freeze/Slow/Haste/Flying 清零）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public Dictionary<ECardAttributeType, int> PendingForceSyncAttributes { get; } = new();
        /// <summary>P1-11：上次已向客户端下发该卡属性键集（用于检测属性移除并下发 EAttributeDeltaType.Delete，消除 0 值幽灵宝石与价签残留）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public HashSet<ECardAttributeType>? LastEmittedAttributeKeys { get; set; }
        /// <summary>标记本候选卡在免费发牌上下文中生成，防止同步时被拉回正买价；与付费商店的临时免单光环解耦。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsFreeCandidate { get; set; }
        /// <summary>对应的 BazaarCore 运行时实例（用于手牌网格占位及确定性战斗结算）。</summary>
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public CardInstance CoreCard { get; set; } = default!;

        /// <summary>P0-03：校验已绑定 CoreCard.InstanceId 是否与 CardEntry.InstanceId 严格一致。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasAlignedCoreCardId =>
            CoreCard == null ||
            string.IsNullOrEmpty(InstanceId) ||
            string.Equals(CoreCard.InstanceId, InstanceId, StringComparison.Ordinal);

        /// <summary>
        /// P0-03：安全绑定 CoreCard 并强制守护 entry.CoreCard.InstanceId == entry.InstanceId 不变量；
        /// 若传入 CoreCard 携带随机 UUID 导致割裂，则调用 ReassignInstanceId 原地对齐为本条目 InstanceId，
        /// 100% 保持原对象引用、网格引用与自定义覆写的 BaseAttributes（如 Tiny）不丢失。
        /// </summary>
        public void BindAlignedCoreCard(CardInstance? core)
        {
            if (core == null)
            {
                CoreCard = null;
                return;
            }

            if (!string.IsNullOrEmpty(InstanceId) &&
                !string.Equals(core.InstanceId, InstanceId, StringComparison.Ordinal))
            {
                core.ReassignInstanceId(InstanceId);
            }
            CoreCard = core;

            System.Diagnostics.Debug.Assert(
                HasAlignedCoreCardId,
                $"CardEntry.CoreCard.InstanceId ({CoreCard?.InstanceId}) 必须与 CardEntry.InstanceId ({InstanceId}) 严格一致");
        }
    }

    /// <summary>累加指定玩家整型属性（用于追踪 Custom_3..Custom_11 等官方全局计数，同步维护 BasePlayerAttributes）。</summary>
    public void IncrementPlayerAttribute(EPlayerAttributeType attr, int delta)
    {
        if (delta == 0) return;
        AdjustBasePlayerAttribute(attr, delta);
    }

    /// <summary>增减玩家金币，并在实际正向增加时自动累加官方 Custom_3 (Gold Gained)，同步维护 BasePlayerAttributes。</summary>
    public void AddPlayerGold(int delta)
    {
        int curGold = PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        int nextGold = AdjustBasePlayerAttribute(EPlayerAttributeType.Gold, delta, minBase: 0);
        int actualGain = nextGold - curGold;
        if (actualGain > 0)
            IncrementPlayerAttribute(EPlayerAttributeType.Custom_3, actualGain);
    }

    /// <summary>当玩家持有的 Reagent 卡发生永久变形时，累加官方 Custom_9 (Reagents Transformed)。</summary>
    public void TrackReagentTransformIfApplicable(CardInstance? oldCoreCard, CardEntry? oldEntry = null)
    {
        bool isReagent = (oldEntry?.Tags.Contains(ECardTag.Reagent) ?? false)
                      || (oldCoreCard?.GetAllTags().Contains("Reagent", StringComparer.OrdinalIgnoreCase) ?? false);
        if (isReagent)
            IncrementPlayerAttribute(EPlayerAttributeType.Custom_9, 1);
    }

    /// <summary>购买非免费 Item 卡时，按 Weapon / Aquatic 标签累加官方 Custom_4 与 Custom_10。</summary>
    public void TrackItemPurchasedTags(CardEntry? entry, CardInstance? coreCard)
    {
        bool isWeapon = (entry?.Tags.Contains(ECardTag.Weapon) ?? false)
                     || (coreCard?.GetAllTags().Contains("Weapon", StringComparer.OrdinalIgnoreCase) ?? false);
        if (isWeapon)
            IncrementPlayerAttribute(EPlayerAttributeType.Custom_4, 1);

        bool isAquatic = (entry?.Tags.Contains(ECardTag.Aquatic) ?? false)
                      || (coreCard?.GetAllTags().Contains("Aquatic", StringComparer.OrdinalIgnoreCase) ?? false);
        if (isAquatic)
            IncrementPlayerAttribute(EPlayerAttributeType.Custom_10, 1);
    }

    public static Dictionary<EPlayerAttributeType, int> CreateDefaultCombatantAttributes(
        int hp, int hpMax, int prestige = GameDefaults.StartingPrestige, int level = 1, int gold = 0, int income = 0, int experience = 0)
    {
        return new Dictionary<EPlayerAttributeType, int>
        {
            [EPlayerAttributeType.Health] = hp,
            [EPlayerAttributeType.HealthMax] = hpMax,
            [EPlayerAttributeType.Shield] = 0,
            [EPlayerAttributeType.Joy] = 0,
            [EPlayerAttributeType.Burn] = 0,
            [EPlayerAttributeType.Poison] = 0,
            [EPlayerAttributeType.HealthRegen] = 0,
            [EPlayerAttributeType.Rage] = 0,
            [EPlayerAttributeType.RageMax] = 100,
            [EPlayerAttributeType.Tempo] = 0,
            [EPlayerAttributeType.Gold] = gold,
            [EPlayerAttributeType.Income] = income,
            [EPlayerAttributeType.Level] = level,
            [EPlayerAttributeType.Experience] = experience,
            [EPlayerAttributeType.Prestige] = prestige,
        };
    }

    /// <summary>汇总玩家当前已持有物品/技能的最高品质映射（TemplateId → CardTier），供 Wave 2 商店过滤低阶与满阶重复卡使用。</summary>
    public Dictionary<string, CardTier> GetOwnedMaxTiers()
    {
        var owned = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in Cards.Values)
        {
            if (card.Owner != ECombatantId.Player) continue;
            if (card.Type is not (ECardType.Item or ECardType.Skill)) continue;
            string tplId = ResolveCoreTemplateId(card);
            var coreTier = ShopEngine.ToCoreTier(card.Tier);
            if (!owned.TryGetValue(tplId, out var existing) || coreTier > existing)
                owned[tplId] = coreTier;
        }
        return owned;
    }

    /// <summary>按中心对称顺序 [4,5,3,6,2,7,1,8,0,9] 解锁指定新增槽位数并返回新解锁的枚举集合。</summary>
    public HashSet<EContainerSocketId> UnlockCenteredSockets(int addCount)
    {
        int[] order = [4, 5, 3, 6, 2, 7, 1, 8, 0, 9];
        var newlyUnlocked = new HashSet<EContainerSocketId>();
        int added = 0;
        foreach (int idx in order)
        {
            int bit = 1 << idx;
            if ((UnlockedSlots & bit) == 0)
            {
                UnlockedSlots |= (ushort)bit;
                newlyUnlocked.Add((EContainerSocketId)idx);
                if (++added >= addCount) break;
            }
        }
        HandGrid.UnlockedMask = UnlockedSlots;
        return newlyUnlocked;
    }

    /// <summary>
    /// P1-28：统一解析面向官方客户端的合法 CurrentEncounterId（供 BuildSnapshot / BuildFullGameSim / BuildDeltaGameSim / BuildResumeEvents 四处共用）：
    /// 1) 空串或纯空白统一归 null（防止客户端 DataExtensions.cs:71 直接 Guid.Parse 抛 FormatException）；
    /// 2) Encounter 态下处于多步奇遇 EncounterStep 子步时回溯至 ParentEncounterTemplateId（保证顶栏遭遇肖像挂载）；
    /// 3) 非 GUID 自定义 Mod 遭遇 ID（如 "mod:enc_key"）优先映射为当前活跃遭遇卡或牌库中同 CustomCardId 载体卡的合法白名单 TemplateId，仍无法解析则安全返回 null。
    /// </summary>
    public string? ResolveWireCurrentEncounterId(string? rawEncounterId = null)
    {
        string? candidate = rawEncounterId ?? CurrentEncounterId;
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        // Encounter 态下若存在合法父遭遇 ID 且当前指向子步（如 EncounterStep 或 Mod 子步），优先回溯父遭遇 ID
        if (State == ERunState.Encounter &&
            !string.IsNullOrWhiteSpace(ParentEncounterTemplateId) &&
            !string.Equals(candidate, ParentEncounterTemplateId, StringComparison.OrdinalIgnoreCase))
        {
            if (Guid.TryParse(ParentEncounterTemplateId, out _) || !Guid.TryParse(candidate, out _))
            {
                candidate = ParentEncounterTemplateId;
            }
        }

        if (Guid.TryParse(candidate, out _))
            return candidate;

        // 非 GUID 自定义 Mod 遭遇逻辑 ID：通过活跃遭遇卡或已生成载体卡映射为合法白名单原版 GUID
        if (!string.IsNullOrEmpty(ActiveEncounterCardInstanceId) &&
            Cards.TryGetValue(ActiveEncounterCardInstanceId, out var activeCard) &&
            activeCard.TemplateId != Guid.Empty &&
            (string.Equals(activeCard.CustomCardId, candidate, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(activeCard.CustomCardId, ParentEncounterTemplateId, StringComparison.OrdinalIgnoreCase)))
        {
            return activeCard.TemplateId.ToString();
        }

        foreach (var c in Cards.Values)
        {
            if (c.TemplateId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(c.CustomCardId) &&
                string.Equals(c.CustomCardId, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return c.TemplateId.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// P2-14：统一收口 RerollCost 变更；当新旧费用不等（或显式 forceEmit）且传入事件列表时自动追加 GameSimEventRunRerollCostChanged。
    /// </summary>
    public bool SetRerollCost(uint newCost, List<IGameSimEvent>? events = null, bool forceEmit = false)
    {
        bool changed = RerollCost != newCost;
        RerollCost = newCost;
        if (events != null && (changed || forceEmit))
        {
            events.Add(new GameSimEventRunRerollCostChanged(newCost));
        }
        return changed;
    }

    /// <summary>
    /// P2-36：判断当前状态是否具备对外可见的有效重随能力（SelectionRules.RerollRules != null，或 V14.3 ERunState.Loot 多页免费翻页）。
    /// 无有效重随能力时对外 DTO 输出 null，对齐官方客户端 RerollButton / AppState 可空 uint? 硬禁用防线。
    /// </summary>
    public bool HasActiveWireRerollCapability()
    {
        if (SelectionRules?.RerollRules != null)
            return true;
        if (State == ERunState.Loot && MonsterLootPages.Count > 1 && RerollsRemaining > 0)
            return true;
        return false;
    }

    /// <summary>P2-36：解析对外协议 DTO（BuildSnapshot / BuildFullGameSim / BuildDeltaGameSim）下发的 RerollCost / RerollsRemaining。</summary>
    public (uint? RerollCost, uint? RerollsRemaining) ResolveWireRerollFields()
        => HasActiveWireRerollCapability()
            ? (RerollCost, RerollsRemaining)
            : (null, null);

    // ── 生成消息 ────────────────────────────────────────────────────────────

    public GameStateSnapshotDTO BuildSnapshot()
    {
        string? wireEncounterId = ResolveWireCurrentEncounterId();
        var (wireRerollCost, wireRerollsRemaining) = ResolveWireRerollFields();
        var snap = new GameStateSnapshotDTO
        {
            Run = new RunSnapshotDTO
            {
                GameModeId = GameModeId,
                Day = Day,
                Hour = Hour,
                Victories = Victories,
                Defeats = Defeats,
                HasVisitedFates = HasVisitedFates,
                DataVersion = DataVersion,
            },
            CurrentState = new RunStateSnapshotDTO
            {
                StateName = State,
                CurrentEncounterId = wireEncounterId,
                RerollCost = wireRerollCost,
                RerollsRemaining = wireRerollsRemaining,
                SelectionSet = new List<string>(SelectionSet),
                SelectionContextRules = CloneRules(SelectionRules),
                Board = CurrentBoardId,
            },
            Player = new PlayerSnapshotDTO
            {
                Hero = Hero,
                Attributes = new Dictionary<EPlayerAttributeType, int>(PlayerAttributes),
                UnlockedSlots = UnlockedSlots,
            },
        };

        foreach (var c in Cards.Values)
        {
            c.LastEmittedAttributeKeys = new HashSet<ECardAttributeType>(c.Attributes.Keys);
            snap.Cards.Add(new CardSnapshotDTO
            {
                InstanceId = c.InstanceId,
                TemplateId = c.TemplateId,
                Attributes = new Dictionary<ECardAttributeType, int>(c.Attributes),
                Enchantment = c.Enchantment,
                Heroes = new HashSet<EHero>(c.Heroes),
                HiddenTags = new HashSet<EHiddenTag>(c.HiddenTags),
                Tags = new HashSet<ECardTag>(c.Tags),
                Tier = c.Tier,
                Type = c.Type,
                Size = c.Size,
                Owner = c.Owner,
                Socket = c.Socket,
                Section = c.Section,
            });
        }
        return snap;
    }

    public NetMessageRunInitialized BuildRunInitialized() => new(
        RunId,
        "0.0.0-local",
        "Local",
        PlayMode.ToString())
    {
        TournamentSessionId = TournamentSessionId,
    };

    /// <summary>
    /// P1-09：构造下发至客户端的 Opponent 增量更新。
    /// 仅在活跃战斗态（Combat / PVPCombat）下发对手属性；CombatDespawnSim 与所有非战斗态包一律返回 null，
    /// 令官方客户端 DataExtensions.Update(null) 安全早退，彻底根治退战重置 100/100 导致的虚假「最大生命值 -300~-4600」飘字。
    /// </summary>
    private SimUpdatePlayer? BuildWireOpponentUpdate()
    {
        if (State is not (ERunState.Combat or ERunState.PVPCombat))
            return null;

        return new SimUpdatePlayer
        {
            CombatantId = ECombatantId.Opponent,
            Attributes = new Dictionary<EPlayerAttributeType, int>(OpponentAttributes),
        };
    }

    public GameSim BuildFullGameSim(
        IEnumerable<IGameSimEvent> events = null,
        IEnumerable<CardEntry> extraSpawnCards = null,
        SimPvpOpponent pvpOpponent = null)
    {
        var (wireRerollCost, wireRerollsRemaining) = ResolveWireRerollFields();
        var sim = new GameSim
        {
            Player = new SimUpdatePlayer
            {
                CombatantId = ECombatantId.Player,
                Attributes = new Dictionary<EPlayerAttributeType, int>(PlayerAttributes),
            },
            Opponent = BuildWireOpponentUpdate(),
            Run = new SimUpdateRun
            {
                Day = Day,
                Hour = Hour,
                Victories = Victories,
                Defeats = Defeats,
                HasVisitedFates = HasVisitedFates,
                CurrentHourXP = 0,
                DataVersion = DataVersion,
            },
            CurrentState = new SimUpdateRunState
            {
                StateName = State,
                CurrentEncounterId = ResolveWireCurrentEncounterId(),
                RerollCost = wireRerollCost,
                RerollsRemaining = wireRerollsRemaining,
                SelectionSet = new List<string>(SelectionSet),
                SelectionContextRules = CloneRules(SelectionRules),
                PvpOpponent = pvpOpponent ?? CurrentPvpOpponent,
            },
        };

        if (events != null) sim.Events.AddRange(events);

        var movedExistingIds = CollectMovedExistingCardIds(sim.Events);

        List<CardEntry>? transientCards = null;
        if (PendingTransientSimCards.Count > 0)
        {
            transientCards = new List<CardEntry>(PendingTransientSimCards);
            PendingTransientSimCards.Clear();
        }

        IEnumerable<CardEntry> allCards = Cards.Values;
        if (transientCards != null)
            allCards = allCards.Concat(transientCards);
        if (extraSpawnCards != null)
            allCards = allCards.Concat(extraSpawnCards);

        foreach (var c in allCards)
        {
            bool omitPlacement = movedExistingIds.Contains(c.InstanceId);
            sim.Cards[c.InstanceId] = RunSnapshotSerializer.ToSimCard(c, clearPendingForceSync: true, omitPlacement: omitPlacement);
        }
        SanitizeWireEventsAndCards(sim);
        return sim;
    }

    private static HashSet<string> CollectMovedExistingCardIds(IReadOnlyList<IGameSimEvent> events)
    {
        var moved = new HashSet<string>(StringComparer.Ordinal);
        if (events == null || events.Count == 0) return moved;

        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is GameSimEventCardMoved mv && !string.IsNullOrEmpty(mv.InstanceId))
                moved.Add(mv.InstanceId);
        }
        if (moved.Count == 0) return moved;

        // 仅对本帧已存在于客户端的现存卡省略 Placement；本帧新生成/发牌/变形/刚购买卡需保留 Placement 供创建挂载
        for (int i = 0; i < events.Count; i++)
        {
            switch (events[i])
            {
                case GameSimEventCardSpawned sp when !string.IsNullOrEmpty(sp.InstanceId):
                    moved.Remove(sp.InstanceId);
                    break;
                case GameSimEventCardDealt dl when !string.IsNullOrEmpty(dl.InstanceId):
                    moved.Remove(dl.InstanceId);
                    break;
                case GameSimEventCardPurchased pur when !string.IsNullOrEmpty(pur.InstanceId):
                    moved.Remove(pur.InstanceId);
                    break;
                case GameSimEventCardTransformed tr:
                    if (!string.IsNullOrEmpty(tr.OriginalInstanceId))
                        moved.Remove(tr.OriginalInstanceId);
                    if (tr.TransformedCards != null)
                    {
                        foreach (var tc in tr.TransformedCards)
                        {
                            if (!string.IsNullOrEmpty(tc.InstanceId))
                                moved.Remove(tc.InstanceId);
                        }
                    }
                    break;
                case GameSimEventCardTransformReverted rev when rev.OriginalCard != null && !string.IsNullOrEmpty(rev.OriginalCard.InstanceId):
                    moved.Remove(rev.OriginalCard.InstanceId);
                    break;
            }
        }
        return moved;
    }

    /// <summary>
    /// V13.1 拖拽加速（Top 7）：构建轻量增量 GameSim，仅下发被移动、被挤位或光环属性实际发生变更的脏卡集合，
    /// 避免单次 MoveItem 打包 30~40 张无关卡牌并触发客户端全盘属性刷新（体积与客户端刷新耗时直降 80%+）。
    /// </summary>
    public GameSim BuildDeltaGameSim(
        IEnumerable<IGameSimEvent>? events = null,
        IReadOnlyCollection<string>? dirtyCardIds = null,
        SimPvpOpponent? pvpOpponent = null)
    {
        var (wireRerollCost, wireRerollsRemaining) = ResolveWireRerollFields();
        var sim = new GameSim
        {
            Player = new SimUpdatePlayer
            {
                CombatantId = ECombatantId.Player,
                Attributes = new Dictionary<EPlayerAttributeType, int>(PlayerAttributes),
            },
            Opponent = BuildWireOpponentUpdate(),
            Run = new SimUpdateRun
            {
                Day = Day,
                Hour = Hour,
                Victories = Victories,
                Defeats = Defeats,
                HasVisitedFates = HasVisitedFates,
                CurrentHourXP = 0,
                DataVersion = DataVersion,
            },
            CurrentState = new SimUpdateRunState
            {
                StateName = State,
                CurrentEncounterId = ResolveWireCurrentEncounterId(),
                RerollCost = wireRerollCost,
                RerollsRemaining = wireRerollsRemaining,
                SelectionSet = new List<string>(SelectionSet),
                SelectionContextRules = CloneRules(SelectionRules),
                PvpOpponent = pvpOpponent ?? CurrentPvpOpponent,
            },
        };

        var targetIds = new HashSet<string>(StringComparer.Ordinal);
        if (dirtyCardIds != null)
        {
            foreach (string id in dirtyCardIds)
            {
                if (!string.IsNullOrEmpty(id))
                    targetIds.Add(id);
            }
        }

        if (events != null)
        {
            foreach (var ev in events)
                sim.Events.Add(ev);
            CollectEventReferencedCardIds(events, targetIds);
        }

        var movedExistingIds = CollectMovedExistingCardIds(sim.Events);

        foreach (var id in targetIds)
        {
            if (Cards.TryGetValue(id, out var entry))
                sim.Cards[entry.InstanceId] = RunSnapshotSerializer.ToSimCard(
                    entry,
                    clearPendingForceSync: true,
                    omitPlacement: movedExistingIds.Contains(entry.InstanceId));
        }

        // 兜底：若仍有带强制归零/校准属性的卡牌，一并随增量帧下发
        foreach (var entry in Cards.Values)
        {
            if (entry.PendingForceSyncAttributes.Count > 0 && !sim.Cards.ContainsKey(entry.InstanceId))
                sim.Cards[entry.InstanceId] = RunSnapshotSerializer.ToSimCard(
                    entry,
                    clearPendingForceSync: true,
                    omitPlacement: movedExistingIds.Contains(entry.InstanceId));
        }

        SanitizeWireEventsAndCards(sim);
        return sim;
    }

    private static ECardSize ResolveFallbackWireCardSize(ECardType cardType)
        => cardType is ECardType.Skill or ECardType.SocketEffect ? ECardSize.Small : ECardSize.Medium;

    private CardEntry? FindCardEntryByInstanceOrCoreId(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return null;
        if (Cards.TryGetValue(instanceId, out var direct))
            return direct;
        foreach (var ce in Cards.Values)
        {
            if (ce.CoreCard != null && string.Equals(ce.CoreCard.InstanceId, instanceId, StringComparison.Ordinal))
                return ce;
        }
        return null;
    }

    private void SanitizeZeroSizeSimCards(GameSim sim)
    {
        if (sim.Cards == null || sim.Cards.Count == 0) return;

        // P0-03 最终出包铁闸：严禁任何 SimUpdateCard 携带 Size == 0 / null 下发给客户端（避免 AssetLoader.InstantiateItemCard(size=0) 崩溃）
        List<string>? badKeys = null;
        foreach (var kv in sim.Cards)
        {
            var sc = kv.Value;
            if (sc == null)
            {
                badKeys ??= new List<string>();
                badKeys.Add(kv.Key);
                continue;
            }
            if (!sc.Size.HasValue || (int)sc.Size.Value == 0)
            {
                var live = FindCardEntryByInstanceOrCoreId(kv.Key);
                if (live != null && (int)live.Size != 0)
                {
                    sc.Size = live.Size;
                    sc.Tier ??= live.Tier;
                    sc.State = live.State;
                }
                else
                {
                    sc.Size = ECardSize.Medium;
                    sc.Tier ??= ETier.Bronze;
                    if (sc.State == default)
                        sc.State = ECardState.Alive;
                }
            }
        }
        if (badKeys != null)
        {
            foreach (string k in badKeys)
                sim.Cards.Remove(k);
        }
    }

    /// <summary>
    /// Wave 4A #1 + Wave 5A P0-03 反向审计服务端发包硬不变量守卫（根治裸客户端 H-2 / H-4 / H-5a / P1-44 / Size=0 空壳崩溃）：
    /// 1) H-2：剔除对同包已 CardSpawned 或 CardDisposed 实体发出的冗余 CardMoved，防止客户端 OnCardMoved 抛 KeyNotFoundException 或瞬移残影；
    /// 2) H-4 &amp; P0-03：确保 CardTransformed 与 CardTransformReverted 引用的新/原实体在 sim.Cards 中全部具备合法非零 Size 与完整属性；
    ///    查无实体的孤儿 CardTransformReverted 直接丢弃，彻底禁止伪造 Size=0/Tier=Bronze/Attributes={} 崩溃空壳；
    /// 3) H-5a：剔除孤立 PlayerSkillEquipped（目标卡既不在 sim.Cards 也不在 run.Cards，或同帧已被销毁且未重生），并为现存技能补齐 sim.Cards；
    /// 4) P1-44：同一玩家同一 TemplateId 在单包内禁止重复 CardSpawned + PlayerSkillEquipped 导致重复技能叠层与客户端异常。
    /// </summary>
    internal void SanitizeWireEventsAndCards(GameSim sim)
    {
        if (sim.Events.Count == 0)
        {
            SanitizeZeroSizeSimCards(sim);
            return;
        }

        var spawnedIds = new HashSet<string>(StringComparer.Ordinal);
        var spawnedMetaById = new Dictionary<string, (ECombatantId Owner, string TemplateId)>(StringComparer.Ordinal);
        var disposedIds = new HashSet<string>(StringComparer.Ordinal);
        var dropIndices = new HashSet<int>();
        for (int i = 0; i < sim.Events.Count; i++)
        {
            switch (sim.Events[i])
            {
                case GameSimEventCardSpawned sp when !string.IsNullOrEmpty(sp.InstanceId):
                    spawnedIds.Add(sp.InstanceId);
                    spawnedMetaById[sp.InstanceId] = (sp.CombatantId, sp.TemplateId ?? string.Empty);
                    break;
                case GameSimEventCardDisposed dp when !string.IsNullOrEmpty(dp.InstanceId):
                    disposedIds.Add(dp.InstanceId);
                    break;
            }
        }

        // H-4 & P0-03：先补齐所有变形/还原事件目标卡在 sim.Cards 中的缺失条目，严禁构造 Size == 0 空壳
        for (int i = 0; i < sim.Events.Count; i++)
        {
            switch (sim.Events[i])
            {
                case GameSimEventCardTransformed tr:
                    if (tr.TransformedCards != null)
                    {
                        foreach (var tc in tr.TransformedCards)
                        {
                            if (tc == null || string.IsNullOrEmpty(tc.InstanceId)) continue;
                            if (!sim.Cards.ContainsKey(tc.InstanceId))
                            {
                                var ce = FindCardEntryByInstanceOrCoreId(tc.InstanceId);
                                if (ce != null)
                                    sim.Cards[tc.InstanceId] = RunSnapshotSerializer.ToSimCard(ce, clearPendingForceSync: false, omitPlacement: false);
                                else
                                    sim.Cards[tc.InstanceId] = new SimUpdateCard
                                    {
                                        InstanceId = tc.InstanceId,
                                        Size = ResolveFallbackWireCardSize(tc.Type),
                                        Tier = ETier.Bronze,
                                        State = ECardState.Alive,
                                        Placement = new CardDeltaPlacement
                                        {
                                            Owner = tc.CombatantId,
                                            Section = tc.Section,
                                            Socket = tc.Socket,
                                        },
                                    };
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(tr.OriginalInstanceId)
                        && !sim.Cards.ContainsKey(tr.OriginalInstanceId))
                    {
                        var origEntry = FindCardEntryByInstanceOrCoreId(tr.OriginalInstanceId);
                        if (origEntry != null)
                            sim.Cards[tr.OriginalInstanceId] = RunSnapshotSerializer.ToSimCard(origEntry, clearPendingForceSync: false, omitPlacement: false);
                    }
                    break;

                case GameSimEventCardTransformReverted rev when rev.OriginalCard != null && !string.IsNullOrEmpty(rev.OriginalCard.InstanceId):
                    if (!sim.Cards.TryGetValue(rev.OriginalCard.InstanceId, out var existingSimCard)
                        || existingSimCard == null
                        || !existingSimCard.Size.HasValue
                        || (int)existingSimCard.Size.Value == 0)
                    {
                        var revEntry = FindCardEntryByInstanceOrCoreId(rev.OriginalCard.InstanceId);
                        if (revEntry != null)
                        {
                            // 若通过 CoreCard.InstanceId 反查命中真实 CardEntry，重写事件指向真实 InstanceId 并下发完整 SimUpdateCard
                            if (!string.Equals(revEntry.InstanceId, rev.OriginalCard.InstanceId, StringComparison.Ordinal))
                            {
                                sim.Cards.Remove(rev.OriginalCard.InstanceId);
                                var repairedOrig = new SimEventCardTransformation(
                                    revEntry.InstanceId,
                                    revEntry.TemplateId.ToString(),
                                    revEntry.Type,
                                    revEntry.Owner ?? rev.OriginalCard.CombatantId,
                                    revEntry.Section ?? rev.OriginalCard.Section,
                                    revEntry.Socket ?? rev.OriginalCard.Socket);
                                sim.Events[i] = new GameSimEventCardTransformReverted(
                                    repairedOrig,
                                    rev.TransformedCardInstanceIds ?? new List<string>());
                            }
                            sim.Cards[revEntry.InstanceId] = RunSnapshotSerializer.ToSimCard(revEntry, clearPendingForceSync: false, omitPlacement: false);
                        }
                        else
                        {
                            // P0-03：绝对禁止伪造 Size == 0 / Tier == Bronze / Attributes == {} 崩溃空壳，丢弃该条孤儿还原事件
                            sim.Cards.Remove(rev.OriginalCard.InstanceId);
                            dropIndices.Add(i);
                        }
                    }
                    break;
            }
        }

        // 统计重复技能（P1-44）与孤立技能（H-5a）
        var seenSkillInstanceIds = new HashSet<string>(StringComparer.Ordinal);
        var seenSkillOwnerTemplates = new HashSet<(ECombatantId Owner, string TemplateId)>();
        var duplicateSpawnSkillIds = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < sim.Events.Count; i++)
        {
            switch (sim.Events[i])
            {
                case GameSimEventCardMoved mv when !string.IsNullOrEmpty(mv.InstanceId):
                    // H-2：同帧刚 Spawned 或已 Disposed 的实体严禁混发 CardMoved
                    if (spawnedIds.Contains(mv.InstanceId) || disposedIds.Contains(mv.InstanceId))
                        dropIndices.Add(i);
                    break;

                case GameSimEventPlayerSkillEquipped sk:
                {
                    string sid = sk.InstanceId ?? string.Empty;
                    bool disposedWithoutSpawn = disposedIds.Contains(sid) && !spawnedIds.Contains(sid);
                    bool inCards = !string.IsNullOrEmpty(sid) && Cards.TryGetValue(sid, out _);
                    bool inSim = !string.IsNullOrEmpty(sid) && sim.Cards.ContainsKey(sid);
                    if (string.IsNullOrEmpty(sid) || disposedWithoutSpawn || (!inSim && !inCards))
                    {
                        // H-5a：孤立 PlayerSkillEquipped，直接剔除
                        dropIndices.Add(i);
                        break;
                    }

                    if (!inSim && Cards.TryGetValue(sid, out var backfilled))
                        sim.Cards[sid] = RunSnapshotSerializer.ToSimCard(backfilled, clearPendingForceSync: false, omitPlacement: false);

                    // P1-44：同一实体重复装配，或同一玩家同 TemplateId 在本包重复装配不同新生成技能实例时去重
                    if (!seenSkillInstanceIds.Add(sid))
                    {
                        dropIndices.Add(i);
                        break;
                    }

                    string tplIdStr = string.Empty;
                    ECombatantId owner = ECombatantId.Player;
                    if (Cards.TryGetValue(sid, out var ce))
                    {
                        tplIdStr = !string.IsNullOrWhiteSpace(ce.CustomCardId) ? ce.CustomCardId! : ce.TemplateId.ToString("D");
                        owner = ce.Owner ?? ECombatantId.Player;
                    }
                    else if (spawnedMetaById.TryGetValue(sid, out var spMeta))
                    {
                        tplIdStr = spMeta.TemplateId;
                        owner = spMeta.Owner;
                    }

                    if (!string.IsNullOrWhiteSpace(tplIdStr)
                        && !seenSkillOwnerTemplates.Add((owner, tplIdStr.ToLowerInvariant())))
                    {
                        dropIndices.Add(i);
                        if (spawnedIds.Contains(sid))
                            duplicateSpawnSkillIds.Add(sid);
                    }
                    break;
                }
            }
        }

        if (duplicateSpawnSkillIds.Count > 0)
        {
            for (int i = 0; i < sim.Events.Count; i++)
            {
                if (sim.Events[i] is GameSimEventCardSpawned sp
                    && sp.Type == ECardType.Skill
                    && !string.IsNullOrEmpty(sp.InstanceId)
                    && duplicateSpawnSkillIds.Contains(sp.InstanceId))
                {
                    dropIndices.Add(i);
                }
            }
        }

        if (dropIndices.Count > 0)
        {
            var filtered = new List<IGameSimEvent>(sim.Events.Count - dropIndices.Count);
            for (int i = 0; i < sim.Events.Count; i++)
            {
                if (!dropIndices.Contains(i))
                    filtered.Add(sim.Events[i]);
            }
            sim.Events.Clear();
            sim.Events.AddRange(filtered);
        }

        SanitizeZeroSizeSimCards(sim);
    }

    /// <summary>
    /// P0-13：统一扫描事件列表中引用的全部卡牌实体 ID（含 Transformed / TransformReverted / EffectExecuted / EffectAuraExecuted / Fused / Quest 等），
    /// 供 BuildDeltaGameSim 与 MoveOwnedCard 共享，彻底防止客户端 simUpdates[id] 查表抛 KeyNotFoundException。
    /// </summary>
    public static void CollectEventReferencedCardIds(IEnumerable<IGameSimEvent>? events, HashSet<string> targetIds)
    {
        if (events == null || targetIds == null) return;

        static void AddIfValid(HashSet<string> set, string? id)
        {
            if (!string.IsNullOrEmpty(id))
                set.Add(id);
        }

        static void AddEffectTarget(HashSet<string> set, IEffectTarget? target)
        {
            if (target is EffectTargetCard tc)
                AddIfValid(set, tc.Target.Value);
        }

        foreach (var ev in events)
        {
            switch (ev)
            {
                case GameSimEventCardMoved mv:
                    AddIfValid(targetIds, mv.InstanceId);
                    break;
                case GameSimEventCardUpgraded upg:
                    AddIfValid(targetIds, upg.InstanceId);
                    break;
                case GameSimEventCardEnchanted enc:
                    AddIfValid(targetIds, enc.InstanceId);
                    break;
                case GameSimEventCardSpawned sp:
                    AddIfValid(targetIds, sp.InstanceId);
                    break;
                case GameSimEventCardDealt dl:
                    AddIfValid(targetIds, dl.InstanceId);
                    break;
                case GameSimEventCardPurchased pur:
                    AddIfValid(targetIds, pur.InstanceId);
                    break;
                case GameSimEventPlayerSkillEquipped sk:
                    AddIfValid(targetIds, sk.InstanceId);
                    break;
                case GameSimEventCardQuestUpdated qu:
                    AddIfValid(targetIds, qu.InstanceId);
                    break;
                case GameSimEventCardQuestCompleted qc:
                    AddIfValid(targetIds, qc.InstanceId);
                    break;
                case GameSimEventCardFused fz:
                    AddIfValid(targetIds, fz.SelectedInstanceId);
                    AddIfValid(targetIds, fz.FusedInstanceId);
                    break;
                case GameSimEventCardTransformed tr:
                    AddIfValid(targetIds, tr.OriginalInstanceId);
                    if (tr.TransformedCards != null)
                    {
                        foreach (var tc in tr.TransformedCards)
                            AddIfValid(targetIds, tc?.InstanceId);
                    }
                    break;
                case GameSimEventCardTransformReverted rev:
                    AddIfValid(targetIds, rev.OriginalCard?.InstanceId);
                    if (rev.TransformedCardInstanceIds != null)
                    {
                        foreach (var id in rev.TransformedCardInstanceIds)
                            AddIfValid(targetIds, id);
                    }
                    break;
                case GameSimEventEffectAuraExecuted aura:
                    if (aura.Source.HasValue) AddIfValid(targetIds, aura.Source.Value.Value);
                    if (aura.AppliedTo != null)
                    {
                        foreach (var t in aura.AppliedTo) AddEffectTarget(targetIds, t);
                    }
                    if (aura.RemovedFrom != null)
                    {
                        foreach (var t in aura.RemovedFrom) AddEffectTarget(targetIds, t);
                    }
                    break;
                case GameSimEventEffectExecuted eff:
                    if (eff.Source.HasValue) AddIfValid(targetIds, eff.Source.Value.Value);
                    if (eff.TriggerSource.HasValue) AddIfValid(targetIds, eff.TriggerSource.Value.Value);
                    AddEffectTarget(targetIds, eff.Target);
                    break;
            }
        }
    }

    public static TSelectionContextRules CloneRules(TSelectionContextRules? src)
    {
        src ??= new TSelectionContextRules();
        return new TSelectionContextRules
        {
            CanSelectMultiple = src.CanSelectMultiple,
            SelectionIsFree = src.SelectionIsFree,
            CanExit = src.CanExit,
            WillAutoSellOnExit = src.WillAutoSellOnExit,
            NextEncounterOnExit = src.NextEncounterOnExit,
            RerollRules = src.RerollRules == null
                ? null
                : new RerollRules
                {
                    TotalAllowedRerolls = src.RerollRules.TotalAllowedRerolls,
                    CostIncrease = src.RerollRules.CostIncrease,
                    StartingCost = src.RerollRules.StartingCost,
                    CostMax = src.RerollRules.CostMax,
                },
        };
    }

    /// <summary>静态只读卡牌属性名映射表（替代热路径 Enum.TryParse 字符串解析，大幅削减 /commands CPU 与 GC）。</summary>
    private static readonly FrozenDictionary<string, ECardAttributeType> CardAttrLookup =
        Enum.GetValues<ECardAttributeType>()
            .ToFrozenDictionary(v => v.ToString(), v => v, StringComparer.OrdinalIgnoreCase);

    /// <summary>获取卡牌在核心引擎中的真实模板 ID（Mod 自定义卡返回 CustomCardId，原版卡返回 TemplateId）。</summary>
    /// <summary>
    /// D2-P1-02 单一权威 CustomCardId 判定：仅真正的 Mod 自定义卡返回逻辑 ID，
    /// 纯原版卡与原位 Patch 卡（IsPatchOverride=true）一律返回 null，杜绝幽灵隔离误判与修订号噪增。
    /// </summary>
    public static string? ResolveCustomCardId(
        string? coreTemplateId,
        Guid wireTemplateId,
        CardPool.CardTemplate? poolTpl = null)
    {
        if (string.IsNullOrWhiteSpace(coreTemplateId)) return null;
        if (poolTpl != null && poolTpl.IsPatchOverride) return null;
        if (poolTpl != null && poolTpl.IsCustomModCard) return coreTemplateId;
        return !string.Equals(coreTemplateId, wireTemplateId.ToString(), StringComparison.OrdinalIgnoreCase)
            ? coreTemplateId
            : null;
    }

    public static string ResolveCoreTemplateId(CardEntry entry)
        => !string.IsNullOrWhiteSpace(entry.CustomCardId) ? entry.CustomCardId! : entry.TemplateId.ToString();

    private static readonly string[] RenderableCardAttributeNames =
    [
        "DamageAmount", "DamageCrit",
        "ShieldApplyAmount", "ShieldRemoveAmount", "ShieldCrit",
        "BurnApplyAmount", "BurnRemoveAmount", "BurnCrit",
        "PoisonApplyAmount", "PoisonRemoveAmount", "PoisonCrit",
        "HealAmount", "HealCrit",
        "RegenApplyAmount", "RegenRemoveAmount", "RegenCrit",
        "JoyApplyAmount", "JoyRemoveAmount", "JoyCrit",
        "RageApplyAmount", "RageRemoveAmount",
        "TempoApplyAmount", "TempoRemoveAmount", "TempoCost",
        "FlatTempoCostReduction", "PercentTempoCostReduction",
        "CritChance", "Multicast", "Lifesteal",
        "CooldownMax", "FlatCooldownReduction", "PercentCooldownReduction", "CooldownDisabled",
        "Ammo", "AmmoMax", "ReloadAmount", "ReloadTargets",
        "Haste", "HasteAmount", "HasteTargets", "PercentHasteReduction",
        "Slow", "SlowAmount", "SlowTargets", "PercentSlowReduction",
        "Freeze", "FreezeAmount", "FreezeTargets", "PercentFreezeReduction",
        "ChargeAmount", "ChargeTargets", "PercentChargeReduction",
        "Flying", "FlyingTargets",
        "ForceUseTargets", "EnchantTargets", "EnchantRemoveTargets",
        "UpgradeTargets", "DisableTargets", "RepairTargets",
        "DestroyTargets", "DestroyImmunity", "TransformTargets",
        "BuyPrice", "SellPrice", "Counter", "Tiny", "Heated", "Chilled",
        "Custom_0", "Custom_1", "Custom_2", "Custom_3", "Custom_4",
        "Custom_5", "Custom_6", "Custom_7", "Custom_8",
        "Custom_9", "Custom_10", "Custom_11", "Custom_12", "Custom_13",
        "QuestCompletedCount",
        "Quest_1", "Quest_2", "Quest_3", "Quest_4", "Quest_5", "Quest_6",
        "Quest_7", "Quest_8", "Quest_9", "Quest_10", "Quest_11", "Quest_12",
    ];

    /// <summary>
    /// P1-09：基于 AuraEngine.RecalculateAuras(..., onAuraSnapshot) 返回的真实光环目标快照，
    /// 与上一轮 <see cref="LastOutOfCombatAuraTargets"/> 精确做集合差分，
    /// 填充真实 Source / TriggerSource / EffectId / AppliedTo / RemovedFrom 发射 GameSimEventEffectAuraExecuted，
    /// 驱动客户端 SocketEffectController.OnAuraEffectExecuted 准确切换 Jules [Stove]/[Cooler] 等槽位视觉激活与退活。
    /// </summary>
    public HashSet<string> EmitOutOfCombatAuraDiffs(
        IReadOnlyList<BazaarCore.Engine.AuraEngine.AuraEffectTargetsSnapshot>? snapshots,
        List<IGameSimEvent>? events,
        string? triggerSourceId = null,
        HashSet<string>? dirtyCardIds = null)
    {
        var coveredCardIds = new HashSet<string>(StringComparer.Ordinal);
        var currentMap = new Dictionary<string, (string SourceCardId, string AuraId, HashSet<string> Targets)>(StringComparer.Ordinal);
        if (snapshots != null)
        {
            for (int i = 0; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                if (string.IsNullOrEmpty(snap.SourceCardInstanceId) || string.IsNullOrEmpty(snap.AuraId))
                    continue;

                string key = snap.SourceCardInstanceId + "|" + snap.AuraId;
                if (!currentMap.TryGetValue(key, out var entry))
                {
                    entry = (snap.SourceCardInstanceId, snap.AuraId, new HashSet<string>(StringComparer.Ordinal));
                    currentMap[key] = entry;
                }
                if (snap.TargetCardInstanceIds != null)
                {
                    for (int j = 0; j < snap.TargetCardInstanceIds.Count; j++)
                    {
                        string cid = snap.TargetCardInstanceIds[j];
                        if (!string.IsNullOrEmpty(cid))
                            entry.Targets.Add("C:" + cid);
                    }
                }
                if (snap.TargetPlayerIds != null)
                {
                    for (int j = 0; j < snap.TargetPlayerIds.Count; j++)
                    {
                        string pid = snap.TargetPlayerIds[j];
                        if (!string.IsNullOrEmpty(pid))
                            entry.Targets.Add("P:" + pid);
                    }
                }
            }
        }

        static IEffectTarget? ParseAuraTarget(string encoded)
        {
            if (encoded.StartsWith("C:", StringComparison.Ordinal))
                return new EffectTargetCard { Target = new InstanceId(encoded[2..]) };
            if (encoded.StartsWith("P:", StringComparison.Ordinal))
            {
                string p = encoded[2..];
                var cid = string.Equals(p, "opponent", StringComparison.OrdinalIgnoreCase)
                    ? ECombatantId.Opponent
                    : ECombatantId.Player;
                return new EffectTargetPlayer { Target = cid };
            }
            return null;
        }

        // 1. 仍在活跃或新增的光环：对比新增与移除目标
        foreach (var (key, cur) in currentMap)
        {
            LastOutOfCombatAuraTargets.TryGetValue(key, out var prevTargets);
            HashSet<IEffectTarget>? applied = null;
            HashSet<IEffectTarget>? removed = null;

            foreach (string t in cur.Targets)
            {
                if (prevTargets == null || !prevTargets.Contains(t))
                {
                    var et = ParseAuraTarget(t);
                    if (et != null)
                    {
                        applied ??= new HashSet<IEffectTarget>();
                        applied.Add(et);
                        if (t.StartsWith("C:", StringComparison.Ordinal))
                        {
                            string cid = t[2..];
                            dirtyCardIds?.Add(cid);
                            coveredCardIds.Add(cid);
                        }
                    }
                }
            }

            if (prevTargets != null)
            {
                foreach (string t in prevTargets)
                {
                    if (!cur.Targets.Contains(t))
                    {
                        var et = ParseAuraTarget(t);
                        if (et != null)
                        {
                            removed ??= new HashSet<IEffectTarget>();
                            removed.Add(et);
                            if (t.StartsWith("C:", StringComparison.Ordinal))
                            {
                                string cid = t[2..];
                                dirtyCardIds?.Add(cid);
                                coveredCardIds.Add(cid);
                            }
                        }
                    }
                }
            }

            if (events != null && (applied is { Count: > 0 } || removed is { Count: > 0 }))
            {
                string effectiveTrigger = !string.IsNullOrEmpty(triggerSourceId) ? triggerSourceId! : cur.SourceCardId;
                events.Add(new GameSimEventEffectAuraExecuted
                {
                    ExecutionContextId = $"ooc_aura_{Day}_{Hour}_{cur.SourceCardId}_{cur.AuraId}",
                    EffectId = cur.AuraId,
                    Source = new InstanceId(cur.SourceCardId),
                    TriggerSource = new InstanceId(effectiveTrigger),
                    AppliedTo = applied ?? new HashSet<IEffectTarget>(),
                    RemovedFrom = removed ?? new HashSet<IEffectTarget>(),
                });
                dirtyCardIds?.Add(cur.SourceCardId);
                coveredCardIds.Add(cur.SourceCardId);
            }
        }

        // 2. 本轮已彻底失效（不再命中任何目标）的旧光环：AppliedTo 为空集、RemovedFrom 携带全部旧目标（驱动 Stove/Cooler 恢复待机特效）
        foreach (var (key, prevTargets) in LastOutOfCombatAuraTargets)
        {
            if (currentMap.ContainsKey(key) || prevTargets.Count == 0)
                continue;

            int sep = key.IndexOf('|');
            if (sep <= 0 || sep + 1 >= key.Length) continue;
            string sourceId = key[..sep];
            string auraId = key[(sep + 1)..];

            var removed = new HashSet<IEffectTarget>();
            foreach (string t in prevTargets)
            {
                var et = ParseAuraTarget(t);
                if (et != null)
                {
                    removed.Add(et);
                    if (t.StartsWith("C:", StringComparison.Ordinal))
                    {
                        string cid = t[2..];
                        dirtyCardIds?.Add(cid);
                        coveredCardIds.Add(cid);
                    }
                }
            }

            if (events != null && removed.Count > 0)
            {
                string effectiveTrigger = !string.IsNullOrEmpty(triggerSourceId) ? triggerSourceId! : sourceId;
                events.Add(new GameSimEventEffectAuraExecuted
                {
                    ExecutionContextId = $"ooc_aura_{Day}_{Hour}_{sourceId}_{auraId}",
                    EffectId = auraId,
                    Source = new InstanceId(sourceId),
                    TriggerSource = new InstanceId(effectiveTrigger),
                    AppliedTo = new HashSet<IEffectTarget>(),
                    RemovedFrom = removed,
                });
                dirtyCardIds?.Add(sourceId);
                coveredCardIds.Add(sourceId);
            }
        }

        // 3. 覆盖为本轮最新快照
        LastOutOfCombatAuraTargets.Clear();
        foreach (var (key, cur) in currentMap)
        {
            if (cur.Targets.Count > 0)
                LastOutOfCombatAuraTargets[key] = cur.Targets;
        }

        return coveredCardIds;
    }

    /// <summary>
    /// 对比单张卡的 CoreCard 与 CardEntry 快照（全量可渲染属性、Tier、Enchantment、Tags/ExtraTags、Quest 进度），
    /// 发生任何变更时立即写回 CardEntry 并生成客户端原生可消费的 GameSimEvent* 增量事件。
    /// </summary>
    public List<IGameSimEvent> SyncEntryFromCoreCardAndDiff(
        CardEntry? entry,
        StandardPricesSettings? prices = null,
        bool emitAuraRefreshEvent = true)
    {
        var events = new List<IGameSimEvent>();
        if (entry?.CoreCard == null) return events;

        SyncEntryFromCoreCardAndDiffInternal(
            entry,
            prices,
            CustomRules,
            SelectionRules?.SelectionIsFree ?? false,
            events,
            out bool needsUiCardRefresh);

        if (emitAuraRefreshEvent && needsUiCardRefresh && entry.Type == ECardType.Item)
        {
            // 单卡外部无源光环快照时仍保留带源实例的刷新提示（局外主路径优先使用 EmitOutOfCombatAuraDiffs 真实光环事件）
            events.Add(new GameSimEventEffectAuraExecuted
            {
                ExecutionContextId = "sync_" + entry.InstanceId,
                EffectId = "OutOfCombatSync",
                Source = new InstanceId(entry.InstanceId),
                TriggerSource = new InstanceId(entry.InstanceId),
                AppliedTo = new HashSet<IEffectTarget>
                {
                    new EffectTargetCard { Target = new InstanceId(entry.InstanceId) },
                },
            });
        }

        return events;
    }

    /// <summary>
    /// 批量同步指定或全部玩家持有卡的 CoreCard 状态到 CardEntry，并合并生成原生增量事件。
    /// P1-39：将全部 <c>needsUiCardRefresh</c> 卡（含 Skill / SocketEffect，不再仅限 Item）记录入 <paramref name="dirtyCardIds"/>，
    /// 避免拖拽增量同步遗漏受光环影响的技能与槽位效果。
    /// P1-09：对已被 <see cref="EmitOutOfCombatAuraDiffs"/> 覆盖的真实光环目标（<paramref name="alreadyCoveredByRealAuraIds"/>）跳过重复合成刷新，
    /// 而对非光环永久成长（如卖 Insect Wing 减冷却、Langxian/PermBlade 战后永久加攻）仍补发靶向刷新事件确保实机 UI 立即重绘。
    /// </summary>
    public List<IGameSimEvent> SyncAllEntriesFromCoreCardsAndDiff(
        StandardPricesSettings? prices = null,
        IEnumerable<CardEntry>? entries = null,
        HashSet<string>? dirtyCardIds = null,
        bool emitSyntheticRefreshEvent = true,
        IReadOnlySet<string>? alreadyCoveredByRealAuraIds = null)
    {
        var events = new List<IGameSimEvent>();
        var refreshTargets = new HashSet<IEffectTarget>();
        IEnumerable<CardEntry> targetEntries = entries ?? Cards.Values.Where(c => c.Owner == ECombatantId.Player);

        foreach (var entry in targetEntries)
        {
            if (entry.CoreCard == null) continue;
            SyncEntryFromCoreCardAndDiffInternal(
                entry,
                prices,
                CustomRules,
                SelectionRules?.SelectionIsFree ?? false,
                events,
                out bool needsUiCardRefresh);
            if (needsUiCardRefresh)
            {
                dirtyCardIds?.Add(entry.InstanceId);
                if (emitSyntheticRefreshEvent &&
                    entry.Type == ECardType.Item &&
                    (alreadyCoveredByRealAuraIds == null || !alreadyCoveredByRealAuraIds.Contains(entry.InstanceId)))
                {
                    refreshTargets.Add(new EffectTargetCard { Target = new InstanceId(entry.InstanceId) });
                }
            }
        }

        if (emitSyntheticRefreshEvent && refreshTargets.Count > 0)
        {
            events.Add(new GameSimEventEffectAuraExecuted
            {
                ExecutionContextId = "ooc_sync_" + Day + "_" + Hour,
                EffectId = "OutOfCombatAuraSync",
                AppliedTo = refreshTargets,
            });
        }

        return events;
    }

    private static void SyncEntryFromCoreCardAndDiffInternal(
        CardEntry entry,
        StandardPricesSettings? prices,
        CustomRunRuleSet? customRules,
        bool selectionIsFree,
        List<IGameSimEvent> events,
        out bool needsUiCardRefresh)
    {
        needsUiCardRefresh = false;
        var core = entry.CoreCard;
        if (core == null) return;

        // 1. 品质（Tier）差分
        ETier newTier = MapCoreTier(core.Tier);
        if (entry.Tier != newTier)
        {
            entry.Tier = newTier;
            events.Add(new GameSimEventCardUpgraded(entry.InstanceId, newTier));
            needsUiCardRefresh = true;
        }

        // 2. 附魔（Enchantment）差分
        EEnchantmentType? newEnch = null;
        if (!string.IsNullOrEmpty(core.Enchantment) &&
            Enum.TryParse<EEnchantmentType>(core.Enchantment, true, out var parsedEnch))
        {
            newEnch = parsedEnch;
        }
        bool enchantmentChanged = entry.Enchantment != newEnch;
        bool isEnchantmentReverted = false;
        if (enchantmentChanged)
        {
            isEnchantmentReverted = entry.Enchantment.HasValue && !newEnch.HasValue;
            entry.Enchantment = newEnch;
            events.Add(new GameSimEventCardEnchanted
            {
                InstanceId = entry.InstanceId,
                EnchantmentType = newEnch,
                IsReverted = isEnchantmentReverted,
            });
            needsUiCardRefresh = true;
        }

        // 2b. Tiny 物理尺寸同步（Large→Medium，Medium/Small→Small；非 Tiny 还原模板尺寸）
        if (entry.Type == ECardType.Item)
        {
            ECardSize effectiveSize = core.EffectiveSize switch
            {
                CardSize.Large => ECardSize.Large,
                CardSize.Medium => ECardSize.Medium,
                _ => ECardSize.Small,
            };
            if (entry.Size != effectiveSize)
            {
                entry.Size = effectiveSize;
                needsUiCardRefresh = true;
            }
        }

        // 3. 标签（Template.Tags + ExtraTags + CombatTags + AuraTags + Enchantment.Tags）全量统一差分（D1-P1-14：移除 Tags.Count > 0 特判，彻底清除零基础标签物品的幽灵残留标签）
        var expectedTags = new HashSet<ECardTag>();
        foreach (string rawTag in core.GetAllTags())
        {
            if (Enum.TryParse<ECardTag>(rawTag, true, out var parsedTag))
                expectedTags.Add(parsedTag);
        }
        if (!entry.Tags.SetEquals(expectedTags))
        {
            entry.Tags.Clear();
            foreach (var t in expectedTags) entry.Tags.Add(t);
            needsUiCardRefresh = true;
        }

        // 3b. 隐藏标签（Template.HiddenTags + Enchantment.HiddenTags + Tiny + 动态能力 HiddenTags）精确同步，区分 Burn 与 BurnReference
        var expectedHiddenTags = new HashSet<EHiddenTag>();
        foreach (string rawHiddenTag in core.GetAllHiddenTags())
        {
            if (Enum.TryParse<EHiddenTag>(rawHiddenTag, true, out var parsedHiddenTag))
                expectedHiddenTags.Add(parsedHiddenTag);
        }
        bool shouldReplaceHiddenTags = enchantmentChanged
            || isEnchantmentReverted
            || core.Template.HiddenTags.Count > 0
            || !string.IsNullOrEmpty(core.Enchantment)
            || core.IsTiny
            || core.ActiveAbilities.Count > 0;
        if (shouldReplaceHiddenTags)
        {
            if (!entry.HiddenTags.SetEquals(expectedHiddenTags))
            {
                entry.HiddenTags.Clear();
                foreach (var ht in expectedHiddenTags) entry.HiddenTags.Add(ht);
                needsUiCardRefresh = true;
            }
        }
        else
        {
            foreach (var ht in expectedHiddenTags)
            {
                if (entry.HiddenTags.Add(ht))
                    needsUiCardRefresh = true;
            }
        }

        // 4. 汇总全部可渲染属性键（固定核心键 + CardInstance 权威全层键 + QuestProgress / CompletedQuests 键）
        var candidateKeys = new HashSet<string>(RenderableCardAttributeNames, StringComparer.OrdinalIgnoreCase);
        foreach (var k in core.EnumeratePresentAttributeKeys()) candidateKeys.Add(k);
        foreach (var k in core.QuestProgress.Keys) candidateKeys.Add(k);
        foreach (var k in core.CompletedQuests) candidateKeys.Add(k);

        foreach (string key in candidateKeys)
        {
            if (key.Equals("Value", StringComparison.OrdinalIgnoreCase)) continue;
            if (!CardAttrLookup.TryGetValue(key, out var attrType)) continue;

            if (attrType == ECardAttributeType.BuyPrice)
            {
                bool isFreeCandidate = entry.IsFreeCandidate || (entry.Owner == null && selectionIsFree);
                // 若当前为免费候选卡（BuyPrice == 0）且无任何正向 BuyPrice 加成修饰，保持 0 不被拉回正值
                if (isFreeCandidate &&
                    entry.Attributes.TryGetValue(ECardAttributeType.BuyPrice, out int freeCurBuy) &&
                    freeCurBuy == 0 && !HasPositiveBuyPriceAdd(core))
                {
                    continue;
                }

                bool hasExplicitBuyMod =
                    HasAnyAttributeModifier(core, "BuyPrice") ||
                    core.BaseAttributes.GetValueOrDefault("BuyPrice", 0) > 0 ||
                    (!isFreeCandidate && entry.Owner == null && entry.Type is ECardType.Item or ECardType.Skill &&
                     entry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0) == 0);
                if (!hasExplicitBuyMod) continue;
            }

            int newVal;
            if (attrType == ECardAttributeType.BuyPrice)
            {
                newVal = ComputeCardBuyPrice(entry, core, prices);
            }
            else if (attrType == ECardAttributeType.SellPrice)
            {
                newVal = ResolveAuthoritativeSellPrice(entry, core, prices, customRules);
            }
            else if (key.StartsWith("Quest_", StringComparison.OrdinalIgnoreCase) &&
                     !key.Equals("QuestCompletedCount", StringComparison.OrdinalIgnoreCase))
            {
                double qp = core.QuestProgress.TryGetValue(key, out double qVal) ? qVal : 0;
                double attrVal = core.GetAttribute(key);
                double completedFloor = (core.IsQuestCompleted(key)
                    && TooltipFormatter.TryFindNonRepeatableQuestTarget(core.Template, key, out double t))
                    ? t : 0;
                newVal = (int)Math.Round(Math.Max(Math.Max(qp, attrVal), completedFloor));
            }
            else if (attrType == ECardAttributeType.QuestCompletedCount)
            {
                newVal = Math.Max(core.CompletedQuests.Count, (int)Math.Round(core.GetAttribute(key)));
            }
            else if (attrType == ECardAttributeType.Flying)
            {
                newVal = Math.Max(core.IsFlying ? 1 : 0, (int)Math.Round(core.GetAttribute(key)));
            }
            else
            {
                newVal = (int)Math.Round(core.GetAttribute(key));
            }

            bool existed = entry.Attributes.TryGetValue(attrType, out int oldVal);
            bool shouldRemoveZeroKey = existed && newVal == 0 &&
                !core.BaseAttributes.ContainsKey(key) &&
                !HasAnyAttributeModifier(core, key) &&
                attrType != ECardAttributeType.BuyPrice &&
                attrType != ECardAttributeType.SellPrice &&
                attrType != ECardAttributeType.Ammo &&
                attrType != ECardAttributeType.AmmoMax &&
                attrType != ECardAttributeType.Counter &&
                attrType != ECardAttributeType.CooldownMax &&
                attrType != ECardAttributeType.QuestCompletedCount &&
                !key.StartsWith("Quest_", StringComparison.OrdinalIgnoreCase);

            if (shouldRemoveZeroKey)
            {
                // P1-11：非基模且无修饰层的属性归零（如临时附魔撤回或状态增益移除），从 Attributes 移除键，
                // 由 RunSnapshotSerializer.ToSimCard 对比 LastEmittedAttributeKeys 下发 EAttributeDeltaType.Delete；
                // 注意：严禁清除 PendingForceSyncAttributes 中已显式登记的战后首帧归零键（如 Freeze/Slow/Haste/Flying Update=0）。
                entry.Attributes.Remove(attrType);
                needsUiCardRefresh = true;
            }
            else if ((existed && oldVal != newVal) || (!existed && newVal != 0))
            {
                entry.Attributes[attrType] = newVal;
                // 若待强制同步列表（PendingForceSyncAttributes）仍保留该属性旧值，需同步更新，防止后续升级/跨天触发器抬高上限后被战后旧值覆写
                if (entry.PendingForceSyncAttributes.ContainsKey(attrType))
                    entry.PendingForceSyncAttributes[attrType] = newVal;
                needsUiCardRefresh = true;

                if (key.StartsWith("Quest_", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("QuestCompletedCount", StringComparison.OrdinalIgnoreCase) &&
                    newVal != oldVal)
                {
                    var (groupIdx, entryIdx) = ResolveQuestIndices(core, key);
                    events.Add(new GameSimEventCardQuestUpdated(entry.InstanceId, groupIdx, entryIdx, oldVal, newVal));
                }
            }
        }

        // 5. 新完成任务事件同步
        foreach (string completedAttr in core.CompletedQuests)
        {
            if (entry.SyncedCompletedQuests.Add(completedAttr))
            {
                var (groupIdx, entryIdx) = ResolveQuestIndices(core, completedAttr);
                events.Add(new GameSimEventCardQuestCompleted(entry.InstanceId, groupIdx, entryIdx));
                needsUiCardRefresh = true;
            }
        }
    }

    private static bool HasAnyAttributeModifier(CardInstance core, string key)
        => core.PermanentAdd.ContainsKey(key)
        || core.PermanentMult.ContainsKey(key)
        || core.PermanentAdditiveMult.ContainsKey(key)
        || core.HourAdd.ContainsKey(key)
        || core.HourMult.ContainsKey(key)
        || core.HourAdditiveMult.ContainsKey(key)
        || core.DayAdd.ContainsKey(key)
        || core.DayMult.ContainsKey(key)
        || core.DayAdditiveMult.ContainsKey(key)
        || core.AuraAdd.ContainsKey(key)
        || core.AuraMult.ContainsKey(key)
        || core.AuraAdditiveMult.ContainsKey(key)
        || core.EnchantmentBaseAttributes.ContainsKey(key)
        || core.EnchantmentAuraAdd.ContainsKey(key)
        || core.EnchantmentAuraMult.ContainsKey(key)
        || core.EnchantmentAuraAdditiveMult.ContainsKey(key);

    private static bool HasPositiveBuyPriceAdd(CardInstance core)
        => (core.PermanentAdd.GetValueOrDefault("BuyPrice", 0)
          + core.HourAdd.GetValueOrDefault("BuyPrice", 0)
          + core.DayAdd.GetValueOrDefault("BuyPrice", 0)
          + core.AuraAdd.GetValueOrDefault("BuyPrice", 0)) > 0;

    /// <summary>
    /// 计算非免费卡牌的有效购买价：
    /// 若模板无正数显式 BuyPrice（如占位 0 或缺省），对 Item / Skill 从 StandardPrices 取标准买价作为基数，
    /// 再叠加四层 Add 与 Mult 修饰；显式 0 乘区（如 SandboxZeroShopPrice、Thieves Guild Discount、探险门票 0 元光环）
    /// 允许准确归零，正乘区降价保留下限 1。
    /// </summary>
    public static int ComputeCardBuyPrice(
        CardEntry entry,
        CardInstance core,
        StandardPricesSettings? prices)
    {
        double rawBase = core.BaseAttributes.GetValueOrDefault("BuyPrice", 0);
        if (rawBase <= 0)
        {
            if (entry.Type == ECardType.Item)
            {
                rawBase = ShopEngine.PriceFor(ShopEngine.ParseSize(core.Template.Size), MapCoreTier(core.Tier), prices).Buy;
            }
            else if (entry.Type == ECardType.Skill)
            {
                rawBase = (prices ?? new StandardPricesSettings()).GetSkillBuyPrice(core.Tier);
            }
            else
            {
                return entry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0);
            }
        }

        double add = core.PermanentAdd.GetValueOrDefault("BuyPrice", 0)
                   + core.HourAdd.GetValueOrDefault("BuyPrice", 0)
                   + core.DayAdd.GetValueOrDefault("BuyPrice", 0)
                   + core.AuraAdd.GetValueOrDefault("BuyPrice", 0);
        double mult = core.PermanentMult.GetValueOrDefault("BuyPrice", 1.0)
                    * core.HourMult.GetValueOrDefault("BuyPrice", 1.0)
                    * core.DayMult.GetValueOrDefault("BuyPrice", 1.0)
                    * core.AuraMult.GetValueOrDefault("BuyPrice", 1.0);

        if (mult <= 1e-9)
            return 0;

        int computed = (int)Math.Round((rawBase + add) * mult);
        return entry.Type is ECardType.Item or ECardType.Skill
            ? Math.Max(1, computed)
            : Math.Max(0, computed);
    }

    /// <summary>
    /// 单一权威卖价计算函数（供卡面下发、OnSell、ApplyWillAutoSellOnExitIfNeeded 三处共用）：
    /// 统一汇总基础/标准卖价 + CoreCard.GetAttribute("Value") 经济价值增益 + DoubleEconomy 词条翻倍，
    /// 彻底消除三端卖价口径分裂。
    /// </summary>
    public int ResolveAuthoritativeSellPrice(CardEntry entry, StandardPricesSettings? prices = null)
        => ResolveAuthoritativeSellPrice(entry, entry.CoreCard, prices, CustomRules);

    public static int ResolveAuthoritativeSellPrice(
        CardEntry entry,
        CardInstance? core,
        StandardPricesSettings? prices,
        CustomRunRuleSet? rules)
    {
        ArgumentNullException.ThrowIfNull(entry);

        int baseSellPrice;
        if (core != null)
        {
            bool hasValueKey = core.BaseAttributes.ContainsKey("Value")
                            || core.PermanentAdd.ContainsKey("Value")
                            || core.PermanentMult.ContainsKey("Value")
                            || core.HourAdd.ContainsKey("Value")
                            || core.HourMult.ContainsKey("Value")
                            || core.DayAdd.ContainsKey("Value")
                            || core.DayMult.ContainsKey("Value")
                            || core.AuraAdd.ContainsKey("Value")
                            || core.AuraMult.ContainsKey("Value");
            double valueBonus = hasValueKey ? core.GetAttribute("Value") : 0.0;

            if (core.BaseAttributes.ContainsKey("SellPrice"))
            {
                baseSellPrice = Math.Max(0, (int)Math.Round(core.GetAttribute("SellPrice") + valueBonus));
            }
            else if (entry.Type != ECardType.Item && entry.Type != ECardType.Skill)
            {
                baseSellPrice = Math.Max(0, entry.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice, 0));
            }
            else if (prices != null)
            {
                baseSellPrice = Math.Max(0, core.GetSellPrice(prices) + (int)Math.Round(valueBonus));
            }
            else
            {
                bool hasSellMod = HasAnyAttributeModifier(core, "SellPrice");
                if (!hasSellMod && !hasValueKey &&
                    entry.Attributes.TryGetValue(ECardAttributeType.SellPrice, out int existingSell) &&
                    existingSell > 0)
                {
                    // 注意：若缺失 prices 且直接复用卡面 SellPrice，卡面可能已包含 DoubleEconomy 翻倍，直接返回防二次翻倍
                    return existingSell;
                }

                int stdSell = entry.Type == ECardType.Skill
                    ? (prices ?? new StandardPricesSettings()).GetSkillSellPrice(core.Tier)
                    : ShopEngine.PriceFor(ShopEngine.ParseSize(core.Template.Size), MapCoreTier(core.Tier), prices).Sell;

                double mult = core.PermanentMult.GetValueOrDefault("SellPrice", 1.0)
                            * core.HourMult.GetValueOrDefault("SellPrice", 1.0)
                            * core.DayMult.GetValueOrDefault("SellPrice", 1.0)
                            * core.AuraMult.GetValueOrDefault("SellPrice", 1.0);
                double add = core.PermanentAdd.GetValueOrDefault("SellPrice", 0)
                           + core.HourAdd.GetValueOrDefault("SellPrice", 0)
                           + core.DayAdd.GetValueOrDefault("SellPrice", 0)
                           + core.AuraAdd.GetValueOrDefault("SellPrice", 0)
                           + valueBonus;
                baseSellPrice = Math.Max(0, (int)Math.Round((stdSell + add) * mult));
            }
        }
        else
        {
            // 无 CoreCard 兜底：若卡面已有 SellPrice 直接采用（不再重复叠加 DoubleEconomy，避免二次翻倍）
            if (entry.Attributes.TryGetValue(ECardAttributeType.SellPrice, out int attrSell) && attrSell >= 0)
                return attrSell;
            baseSellPrice = entry.Type == ECardType.Item
                ? ShopEngine.PriceFor(entry.Size, entry.Tier, prices).Sell
                : (entry.Type == ECardType.Skill ? (prices ?? new StandardPricesSettings()).GetSkillSellPrice(ShopEngine.ToCoreTier(entry.Tier)) : 0);
        }

        if (baseSellPrice > 0 &&
            entry.Type is ECardType.Item or ECardType.Skill &&
            rules?.HasMutator(MutatorCatalog.DoubleEconomy) == true)
        {
            baseSellPrice *= 2;
        }
        return Math.Max(0, baseSellPrice);
    }

    private static (int GroupIdx, int EntryIdx) ResolveQuestIndices(CardInstance core, string questAttr)
    {
        if (core.Template.Quests != null)
        {
            for (int g = 0; g < core.Template.Quests.Count; g++)
            {
                var entries = core.Template.Quests[g].Entries;
                for (int e = 0; e < entries.Count; e++)
                {
                    if (string.Equals(entries[e].AttributeType, questAttr, StringComparison.OrdinalIgnoreCase))
                        return (g, e);
                }
            }
        }

        if (questAttr.StartsWith("Quest_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(questAttr.AsSpan("Quest_".Length), out int qNum) && qNum >= 1)
        {
            return (0, qNum - 1);
        }
        return (0, 0);
    }

    internal static ETier MapCoreTier(CardTier tier) => tier switch
    {
        CardTier.Silver => ETier.Silver,
        CardTier.Gold => ETier.Gold,
        CardTier.Diamond => ETier.Diamond,
        CardTier.Legendary => ETier.Legendary,
        _ => ETier.Bronze,
    };

    /// <summary>同步 BazaarCore 卡牌模板标签/英雄元数据到 CardEntry。</summary>
    public static void PopulateMetaFromTemplate(CardEntry entry, CardPool.CardTemplate? tpl, BazaarCore.Models.CardTemplate? coreTpl)
    {
        if (coreTpl != null)
        {
            foreach (string h in coreTpl.Heroes)
                if (Enum.TryParse<EHero>(h, true, out var eh)) entry.Heroes.Add(eh);
            foreach (string t in coreTpl.Tags)
                if (Enum.TryParse<ECardTag>(t, true, out var et)) entry.Tags.Add(et);
            foreach (string ht in coreTpl.HiddenTags)
                if (Enum.TryParse<EHiddenTag>(ht, true, out var eht)) entry.HiddenTags.Add(eht);
        }
        else if (tpl != null)
        {
            foreach (string h in tpl.Heroes)
                if (Enum.TryParse<EHero>(h, true, out var eh)) entry.Heroes.Add(eh);
            foreach (string t in tpl.Tags)
                if (Enum.TryParse<ECardTag>(t, true, out var et)) entry.Tags.Add(et);
            foreach (string ht in tpl.HiddenTags)
                if (Enum.TryParse<EHiddenTag>(ht, true, out var eht)) entry.HiddenTags.Add(eht);
        }
    }
}
