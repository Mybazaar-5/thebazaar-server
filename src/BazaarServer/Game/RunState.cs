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
    public uint RerollsRemaining { get; set; }
    public int SelectionsMadeInCurrentContext { get; set; }
    /// <summary>挂起时刻留在货架上的候选卡牌条目（供内存恢复重放时无损放回 RunState.Cards 并重发 CardDealt）。</summary>
    public List<RunState.CardEntry> SuspendedCards { get; } = new();
}

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
    public Guid? TournamentSessionId { get; set; }

    /// <summary>本局应用的自定义规则集（默认 MonsterLootChooseOne = true）。</summary>
    public CustomRunRuleSet CustomRules { get; set; } = new();

    /// <summary>本局流派倾向是否已锁定（全模式统一：Day 1 Hour 1 开场确认锁定一次，或推进离开开场后自动焊死，严禁途中篡改）。</summary>
    public bool TendencyLocked { get; set; }

    /// <summary>当前对局是否修改了数值强度（为 true 时幽灵镜像自动隔离，不污染标准匹配池）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCustomRuleRun => CustomRules?.IsCustomPowerModified() ?? false;

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
    public string CurrentEncounterId { get; set; }
    public uint RerollCost { get; set; } = 2;
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

    /// <summary>向后兼容计算属性：是否存在待消费的升级奖励。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PendingLevelUp => PendingLevelUps > 0;

    /// <summary>当前交互上下文内已完成选卡的次数。</summary>
    public int SelectionsMadeInCurrentContext { get; set; }

    /// <summary>单局运行期随机种子（供快照序列化/恢复保持随机序列衔接）。</summary>
    public int RngSeed { get; set; }

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
    public BoardGrid HandGrid { get; } = new(0b00_0111_1000);
    public BoardGrid StashGrid { get; } = new(1023);

    /// <summary>玩家权威属性字典（必含 Health/HealthMax/Shield/Joy 等客户端 UI 强依赖键）。</summary>
    public Dictionary<EPlayerAttributeType, int> PlayerAttributes { get; } =
        CreateDefaultCombatantAttributes(hp: 250, hpMax: 250, prestige: 20, level: 1, gold: 15, income: 5);

    /// <summary>玩家局外限时属性层（UntilEndOfHour / UntilEndOfDay），挂于 RunState 便于无损序列化持久化。</summary>
    public Dictionary<string, double> PlayerHourAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerHourMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerDayAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerDayMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>局外玩家属性光环上一轮生效值追踪（每次光环重算前需从 PlayerAttributes 剥离，防止无限叠加）。</summary>
    public Dictionary<string, double> PlayerAuraAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerAuraMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本局已生成/触发过的 IsSingleSpawn 奇遇模板 ID，避免重复出现。</summary>
    public HashSet<string> VisitedSingleSpawnEncounterIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当天已访问过的遭遇模板 ID（用于跨小时同天去重，跨天时清空）。</summary>
    public HashSet<string> VisitedDayEncounterIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本局运行期动态遭遇排除集（初始化自 GameMode.ExclusionSet，并支持 Burn it Down 等事件动态增删）。</summary>
    public HashSet<string> ExclusionSet { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否已用 GameMode.ExclusionSet 初始化过本局运行期 ExclusionSet（兼容旧快照不重复覆盖动态移除项）。</summary>
    public bool ExclusionSetInitialized { get; set; }

    /// <summary>当前对战对手属性（非战斗状态也填充完整默认键，避免 HealthBarController 与 BankToyController 缺键或残留 +99 占位）。</summary>
    public Dictionary<EPlayerAttributeType, int> OpponentAttributes { get; set; } =
        CreateDefaultCombatantAttributes(hp: 100, hpMax: 100, prestige: 20, level: 1, gold: 0, income: 5);

    public SimPvpOpponent CurrentPvpOpponent { get; set; }

    /// <summary>牌库与场面上已存在的卡：instanceId → 卡快照信息。</summary>
    public Dictionary<string, CardEntry> Cards { get; } = new();

    public sealed class CardEntry
    {
        public string InstanceId { get; init; }
        public Guid TemplateId { get; init; }
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
        /// <summary>对应的 BazaarCore 运行时实例（用于手牌网格占位及确定性战斗结算）。</summary>
        public CardInstance CoreCard { get; set; }
    }

    /// <summary>累加指定玩家整型属性（用于追踪 Custom_3..Custom_11 等官方全局计数）。</summary>
    public void IncrementPlayerAttribute(EPlayerAttributeType attr, int delta)
    {
        if (delta == 0) return;
        PlayerAttributes[attr] = PlayerAttributes.GetValueOrDefault(attr, 0) + delta;
    }

    /// <summary>增减玩家金币，并在实际正向增加时自动累加官方 Custom_3 (Gold Gained)。</summary>
    public void AddPlayerGold(int delta)
    {
        int curGold = PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        int nextGold = Math.Max(0, curGold + delta);
        PlayerAttributes[EPlayerAttributeType.Gold] = nextGold;
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
        int hp, int hpMax, int prestige = 20, int level = 1, int gold = 0, int income = 0, int experience = 0)
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
            string tplId = card.TemplateId.ToString();
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

    // ── 生成消息 ────────────────────────────────────────────────────────────

    public GameStateSnapshotDTO BuildSnapshot()
    {
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
                CurrentEncounterId = CurrentEncounterId,
                RerollCost = RerollCost,
                RerollsRemaining = RerollsRemaining,
                SelectionSet = new List<string>(SelectionSet),
                SelectionContextRules = CloneRules(SelectionRules),
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

    public GameSim BuildFullGameSim(
        IEnumerable<IGameSimEvent> events = null,
        IEnumerable<CardEntry> extraSpawnCards = null,
        SimPvpOpponent pvpOpponent = null)
    {
        var sim = new GameSim
        {
            Player = new SimUpdatePlayer
            {
                CombatantId = ECombatantId.Player,
                Attributes = new Dictionary<EPlayerAttributeType, int>(PlayerAttributes),
            },
            Opponent = new SimUpdatePlayer
            {
                CombatantId = ECombatantId.Opponent,
                Attributes = new Dictionary<EPlayerAttributeType, int>(OpponentAttributes),
            },
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
                CurrentEncounterId = CurrentEncounterId,
                RerollCost = RerollCost,
                RerollsRemaining = RerollsRemaining,
                SelectionSet = new List<string>(SelectionSet),
                SelectionContextRules = CloneRules(SelectionRules),
                PvpOpponent = pvpOpponent ?? CurrentPvpOpponent,
            },
        };

        if (events != null) sim.Events.AddRange(events);

        IEnumerable<CardEntry> allCards = extraSpawnCards != null
            ? Cards.Values.Concat(extraSpawnCards)
            : Cards.Values;

        foreach (var c in allCards)
        {
            sim.Cards[c.InstanceId] = RunSnapshotSerializer.ToSimCard(c, clearPendingForceSync: true);
        }
        return sim;
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

        SyncEntryFromCoreCardAndDiffInternal(entry, prices, events, out bool needsUiCardRefresh);

        if (emitAuraRefreshEvent && needsUiCardRefresh && entry.Type == ECardType.Item)
        {
            events.Add(new GameSimEventEffectAuraExecuted
            {
                ExecutionContextId = "sync_" + entry.InstanceId,
                EffectId = "OutOfCombatSync",
                AppliedTo = new HashSet<IEffectTarget>
                {
                    new EffectTargetCard { Target = new InstanceId(entry.InstanceId) },
                },
            });
        }

        return events;
    }

    /// <summary>
    /// 批量同步指定或全部玩家持有卡的 CoreCard 状态到 CardEntry，并合并生成原生增量事件（含单包光环 UI 刷新事件）。
    /// </summary>
    public List<IGameSimEvent> SyncAllEntriesFromCoreCardsAndDiff(
        StandardPricesSettings? prices = null,
        IEnumerable<CardEntry>? entries = null)
    {
        var events = new List<IGameSimEvent>();
        var refreshTargets = new HashSet<IEffectTarget>();
        IEnumerable<CardEntry> targetEntries = entries ?? Cards.Values.Where(c => c.Owner == ECombatantId.Player);

        foreach (var entry in targetEntries)
        {
            if (entry.CoreCard == null) continue;
            SyncEntryFromCoreCardAndDiffInternal(entry, prices, events, out bool needsUiCardRefresh);
            if (needsUiCardRefresh && entry.Type == ECardType.Item)
            {
                refreshTargets.Add(new EffectTargetCard { Target = new InstanceId(entry.InstanceId) });
            }
        }

        if (refreshTargets.Count > 0)
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

        // 3. 标签（Template.Tags + ExtraTags + CombatTags + AuraTags + Enchantment.Tags）差分
        var expectedTags = new HashSet<ECardTag>();
        foreach (string rawTag in core.GetAllTags())
        {
            if (Enum.TryParse<ECardTag>(rawTag, true, out var parsedTag))
                expectedTags.Add(parsedTag);
        }
        if (core.Template.Tags.Count > 0)
        {
            if (!entry.Tags.SetEquals(expectedTags))
            {
                entry.Tags.Clear();
                foreach (var t in expectedTags) entry.Tags.Add(t);
                needsUiCardRefresh = true;
            }
        }
        else
        {
            foreach (var t in expectedTags)
            {
                if (entry.Tags.Add(t))
                    needsUiCardRefresh = true;
            }
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

        // 4. 汇总全部可渲染属性键（固定核心键 + CoreCard 动态键 + QuestProgress 键）
        var candidateKeys = new HashSet<string>(RenderableCardAttributeNames, StringComparer.OrdinalIgnoreCase);
        foreach (var k in core.BaseAttributes.Keys) candidateKeys.Add(k);
        foreach (var k in core.PermanentAdd.Keys) candidateKeys.Add(k);
        foreach (var k in core.PermanentMult.Keys) candidateKeys.Add(k);
        foreach (var k in core.HourAdd.Keys) candidateKeys.Add(k);
        foreach (var k in core.HourMult.Keys) candidateKeys.Add(k);
        foreach (var k in core.DayAdd.Keys) candidateKeys.Add(k);
        foreach (var k in core.DayMult.Keys) candidateKeys.Add(k);
        foreach (var k in core.AuraAdd.Keys) candidateKeys.Add(k);
        foreach (var k in core.AuraMult.Keys) candidateKeys.Add(k);
        foreach (var k in core.QuestProgress.Keys) candidateKeys.Add(k);

        // 若规则节点中修改了 "Value"（部分卡以 Value 表达售价增益），一并计入 SellPrice
        bool hasValueKey = candidateKeys.Contains("Value");

        foreach (string key in candidateKeys)
        {
            if (key.Equals("Value", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Enum.TryParse<ECardAttributeType>(key, out var attrType)) continue;

            if (attrType == ECardAttributeType.BuyPrice)
            {
                bool hasExplicitBuyMod =
                    HasAnyAttributeModifier(core, "BuyPrice") ||
                    core.BaseAttributes.GetValueOrDefault("BuyPrice", 0) > 0;
                if (!hasExplicitBuyMod) continue;

                // 若当前为免费候选卡（BuyPrice == 0）且无任何正向 BuyPrice 加成修饰，保持 0 不被拉回正值
                if (entry.Attributes.TryGetValue(ECardAttributeType.BuyPrice, out int curBuy) &&
                    curBuy == 0 && !HasPositiveBuyPriceAdd(core))
                {
                    continue;
                }
            }

            int newVal;
            if (attrType == ECardAttributeType.BuyPrice)
            {
                newVal = ComputeCardBuyPrice(entry, core, prices);
            }
            else if (attrType == ECardAttributeType.SellPrice)
            {
                newVal = ComputeCardSellPrice(entry, core, prices, includeValueAlias: hasValueKey);
            }
            else if (key.StartsWith("Quest_", StringComparison.OrdinalIgnoreCase) &&
                     !key.Equals("QuestCompletedCount", StringComparison.OrdinalIgnoreCase))
            {
                double qp = core.QuestProgress.TryGetValue(key, out double qVal) ? qVal : 0;
                double attrVal = core.GetAttribute(key);
                newVal = (int)Math.Round(Math.Max(qp, attrVal));
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
            if ((existed && oldVal != newVal) || (!existed && newVal != 0))
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
        || core.HourAdd.ContainsKey(key)
        || core.HourMult.ContainsKey(key)
        || core.DayAdd.ContainsKey(key)
        || core.DayMult.ContainsKey(key)
        || core.AuraAdd.ContainsKey(key)
        || core.AuraMult.ContainsKey(key);

    private static bool HasPositiveBuyPriceAdd(CardInstance core)
        => (core.PermanentAdd.GetValueOrDefault("BuyPrice", 0)
          + core.HourAdd.GetValueOrDefault("BuyPrice", 0)
          + core.DayAdd.GetValueOrDefault("BuyPrice", 0)
          + core.AuraAdd.GetValueOrDefault("BuyPrice", 0)) > 0;

    /// <summary>
    /// 计算非免费卡牌的有效购买价：
    /// 若模板无正数显式 BuyPrice（如占位 0 或缺省），对 Item / Skill 从 StandardPrices 取标准买价作为基数，
    /// 再叠加四层 Add 与 Mult 修饰，对 Item / Skill 严格保证 &gt;= 1。
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
                rawBase = prices != null
                    ? prices.GetItemBuyPrice(core.Template.CardSize, core.Tier)
                    : ShopEngine.PriceFor(ShopEngine.ParseSize(core.Template.Size), MapCoreTier(core.Tier)).Buy;
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

        int computed = (int)Math.Round((rawBase + add) * mult);
        return entry.Type is ECardType.Item or ECardType.Skill
            ? Math.Max(1, computed)
            : Math.Max(0, computed);
    }

    private static int ComputeCardSellPrice(
        CardEntry entry,
        CardInstance core,
        StandardPricesSettings? prices,
        bool includeValueAlias)
    {
        double valueBonus = includeValueAlias ? core.GetAttribute("Value") : 0;
        if (core.BaseAttributes.ContainsKey("SellPrice"))
            return Math.Max(0, (int)Math.Round(core.GetAttribute("SellPrice") + valueBonus));

        if (entry.Type != ECardType.Item && entry.Type != ECardType.Skill)
            return entry.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice, 0);

        if (prices != null)
            return Math.Max(0, core.GetSellPrice(prices) + (int)Math.Round(valueBonus));

        bool hasSellMod = HasAnyAttributeModifier(core, "SellPrice");
        if (!hasSellMod && !includeValueAlias &&
            entry.Attributes.TryGetValue(ECardAttributeType.SellPrice, out int existingSell) &&
            existingSell > 0)
        {
            return existingSell;
        }

        int baseSell = entry.Type == ECardType.Skill
            ? new StandardPricesSettings().GetSkillSellPrice(core.Tier)
            : ShopEngine.PriceFor(ShopEngine.ParseSize(core.Template.Size), MapCoreTier(core.Tier)).Sell;

        double mult = core.PermanentMult.GetValueOrDefault("SellPrice", 1.0)
                    * core.HourMult.GetValueOrDefault("SellPrice", 1.0)
                    * core.DayMult.GetValueOrDefault("SellPrice", 1.0)
                    * core.AuraMult.GetValueOrDefault("SellPrice", 1.0);
        double add = core.PermanentAdd.GetValueOrDefault("SellPrice", 0)
                   + core.HourAdd.GetValueOrDefault("SellPrice", 0)
                   + core.DayAdd.GetValueOrDefault("SellPrice", 0)
                   + core.AuraAdd.GetValueOrDefault("SellPrice", 0)
                   + valueBonus;

        return Math.Max(0, (int)Math.Round((baseSell + add) * mult));
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

    private static ETier MapCoreTier(CardTier tier) => tier switch
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
