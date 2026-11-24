using System.Runtime.CompilerServices;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;

namespace BazaarServer.Game;

/// <summary>
/// 把 BazaarCore.Combat.CombatSimulator 的 CombatResult（50ms 事件与时间轴）
/// 1:1 转换为原版 Unity 客户端 CombatSimHandler 消费的 50ms 步长官方 CombatSim 消息。
/// </summary>
public static class CombatSimConverter
{
    public const int FrameStepMs = 50;

    /// <summary>
    /// 官方 GameData.db collectibles["hero_skin"] 中 Karnok Beast Form（Skin_KAR_01_Creature, Id=a416f947-7a14-7134-cb12-74f1c402bcd6）
    /// 经 CollectionUtility.FixGuidNaming 规范化后的 Addressables 资产地址（P0-11 狂暴变身立绘）。
    /// </summary>
    public const string KarnokBeastFormPortraitArtKey = "a416f9477a147134cb1274f1c402bcd6";

    /// <summary>
    /// 官方 GameData.db 中战中获得金币动作（Concealed Dagger / Pickpocket 等）使用的原生飞币爆裂弹道 VFX 键（P1-07）。
    /// 当战中 TActionPlayerModifyAttribute 正向增加 Gold 且原技能未显式配置 VFXOverrideKey 时作为标准飞币兜底特效。
    /// </summary>
    public const string OfficialCoinBurstVfxKey = "Assets/TheBazaar/Projectiles/Gold/Projectile_CoinBurst_PV.prefab";

    private static readonly ECardAttributeType[] SyncedCustomAttrs =
    [
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

    private sealed class CardRenderState
    {
        public RunState.CardEntry Entry { get; set; } = null!;
        public string? CoreTemplateId { get; set; }
        public ECombatantId Owner { get; init; }
        /// <summary>战前权威基线属性快照（P1-04：用于末帧对比删除战中动态新增键与复原临时修改键）。</summary>
        public Dictionary<ECardAttributeType, int> InitialAttributes { get; } = new();
        /// <summary>战中已同步的任务进度等持久属性键（防止无 PostCombatPermanentSync 合成场景误清）。</summary>
        public HashSet<ECardAttributeType> RuntimePersistentAttributes { get; } = new();
        /// <summary>标识该渲染状态是否源自战中动态变形新产物。</summary>
        public bool IsRuntimeTransformed { get; init; }
        public Dictionary<ECardAttributeType, int> PrevAttributes { get; } = new();
        public HashSet<ECardAttributeType> PrevExtraAttrKeys { get; } = new();
        public HashSet<ECardTag> PrevTags { get; } = new();
        public HashSet<EHiddenTag> PrevHiddenTags { get; } = new();
        public HashSet<EHero> PrevHeroes { get; } = new();
        public bool PrevDestroyed { get; set; }
        public int SellPriceBaseOffset { get; set; }
        public bool SellPriceOffsetResolved { get; set; }
        public int LastEmittedCdFrame { get; set; } = -100;
        public int LastEmittedCdValue { get; set; } = -1;
        public double LastEmittedCdRate { get; set; } = 1.0;
    }

    /// <summary>
    /// P0-19（#92-2）：按 <see cref="GameDataRepository"/> 懒构建并缓存官方合法 VFX 白名单
    /// （合并 ModRuleCatalog.OfficialVfxAliasMap.Values 与原版全库非自定义卡携带的所有 VFXOverrideKey，涵盖全部 131+ 官方特效且零污染 ModRuleCatalog）。
    /// </summary>
    private static readonly ConditionalWeakTable<GameDataRepository, HashSet<string>> OfficialVfxKeyWhitelistCache = new();

    private sealed class RewardThresholdTracker
    {
        public List<(float Percent, int ThresholdHp, bool Emitted)> GoldMarkers { get; } = new();
        public List<(float Percent, int ThresholdHp, bool Emitted)> XpMarkers { get; } = new();
    }

    private sealed class FrameCombatantState
    {
        public int P1Hp { get; set; }
        public int P1Shield { get; set; }
        public int P1MaxHp { get; set; }
        public int P2Hp { get; set; }
        public int P2Shield { get; set; }
        public int P2MaxHp { get; set; }
    }

    /// <summary>
    /// P0-40：将规范视角（canonicalP1）的 <see cref="CombatSim"/> 严格深拷贝镜像为对手视角（canonicalP2）。
    /// - 顶层翻转 Winner/Loser，深拷贝 VfxKeys/PortraitKeys/OpponentHealthThresholdsForGold/Xp，重映射并深拷贝 CardStats；
    /// - 每帧互换 PlayerUpdates 与 OpponentUpdates（显式深拷贝 IsPlayerDead/Attributes/HealthAdjustments/Portrait）；
    /// - CardUpdates 逐键重映射并深拷贝全部属性/标签/状态与 Placement（翻转 Owner），战中动态衍生新 ID 不在映射表内时原样透传仅翻阵营；
    /// - Events 穷举 switch 全部 15 类 ICombatSimEvent 子类并深拷贝翻转所有阵营与卡牌实体 ID，零共享可变引用。
    /// </summary>
    public static CombatSim FlipPerspective(
        CombatSim source,
        IReadOnlyDictionary<string, string>? cardIdRemap = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        static ECombatantId FlipSide(ECombatantId side)
            => side == ECombatantId.Player ? ECombatantId.Opponent : ECombatantId.Player;

        string RemapId(string id)
        {
            if (string.IsNullOrEmpty(id) || cardIdRemap == null)
                return id;
            return cardIdRemap.TryGetValue(id, out var mapped) && !string.IsNullOrEmpty(mapped) ? mapped : id;
        }

        InstanceId RemapInstanceId(InstanceId id)
        {
            if (string.IsNullOrEmpty(id.Value)) return id;
            string mapped = RemapId(id.Value);
            return string.Equals(mapped, id.Value, StringComparison.Ordinal) ? id : new InstanceId(mapped);
        }

        InstanceId? RemapNullableInstanceId(InstanceId? id)
            => id.HasValue ? RemapInstanceId(id.Value) : null;

        IEffectTarget? FlipEffectTarget(IEffectTarget? target) => target switch
        {
            null => null,
            EffectTargetPlayer tp => new EffectTargetPlayer { Target = FlipSide(tp.Target) },
            EffectTargetCard tc => new EffectTargetCard { Target = RemapInstanceId(tc.Target) },
            _ => target
        };

        SimEventCardTransformation CloneAndFlipTransformation(SimEventCardTransformation t)
            => new(
                RemapId(t.InstanceId),
                t.TemplateId,
                t.Type,
                FlipSide(t.CombatantId),
                t.Section,
                t.Socket);

        static CombatSimPlayerUpdate? ClonePlayerUpdate(CombatSimPlayerUpdate? src)
        {
            if (src == null) return null;
            var dst = new CombatSimPlayerUpdate
            {
                IsPlayerDead = src.IsPlayerDead,
                Portrait = src.Portrait != null
                    ? new CombatSimPlayerPortraitUpdate { Index = src.Portrait.Index }
                    : null,
            };
            if (src.Attributes != null)
            {
                dst.Attributes = new Dictionary<EPlayerAttributeType, CombatSimPlayerAttributeUpdate>(src.Attributes.Count);
                foreach (var (k, v) in src.Attributes)
                {
                    dst.Attributes[k] = new CombatSimPlayerAttributeUpdate
                    {
                        AttributeType = v.AttributeType,
                        PreviousValue = v.PreviousValue,
                        CurrentValue = v.CurrentValue,
                    };
                }
            }
            if (src.HealthAdjustments != null)
            {
                dst.HealthAdjustments = new List<CombatSimPlayerHealthAdjustment>(src.HealthAdjustments.Count);
                foreach (var adj in src.HealthAdjustments)
                {
                    dst.HealthAdjustments.Add(new CombatSimPlayerHealthAdjustment
                    {
                        DamageType = adj.DamageType,
                        AttributeChanged = adj.AttributeChanged,
                        Amount = adj.Amount,
                        IsCrit = adj.IsCrit,
                        IsDamageReduced = adj.IsDamageReduced,
                    });
                }
            }
            return dst;
        }

        CombatSimCardUpdate CloneAndFlipCardUpdate(CombatSimCardUpdate src)
        {
            var dst = new CombatSimCardUpdate
            {
                CardInstanceId = RemapInstanceId(src.CardInstanceId),
                Enchantment = src.Enchantment,
                Size = src.Size,
                Tier = src.Tier,
                Heroes = src.Heroes != null ? new HashSet<EHero>(src.Heroes) : null,
                Tags = src.Tags != null ? new HashSet<ECardTag>(src.Tags) : null,
                HiddenTags = src.HiddenTags != null ? new HashSet<EHiddenTag>(src.HiddenTags) : null,
                Placement = src.Placement != null
                    ? new CardDeltaPlacement
                    {
                        Owner = src.Placement.Owner.HasValue ? FlipSide(src.Placement.Owner.Value) : null,
                        Section = src.Placement.Section,
                        Socket = src.Placement.Socket,
                    }
                    : null,
                State = src.State != null
                    ? new CombatSimCardStateUpdate
                    {
                        PreviousValue = src.State.PreviousValue,
                        CurrentValue = src.State.CurrentValue,
                    }
                    : null,
            };
            if (src.Attributes != null)
            {
                dst.Attributes = new Dictionary<ECardAttributeType, CombatSimCardAttributeUpdate>(src.Attributes.Count);
                foreach (var (k, v) in src.Attributes)
                {
                    dst.Attributes[k] = new CombatSimCardAttributeUpdate
                    {
                        AttributeType = v.AttributeType,
                        PreviousValue = v.PreviousValue,
                        CurrentValue = v.CurrentValue,
                    };
                }
            }
            return dst;
        }

        ICombatSimEvent CloneAndFlipEvent(ICombatSimEvent ev) => ev switch
        {
            CombatSimEventCombatantDied died => new CombatSimEventCombatantDied
            {
                CombatantId = FlipSide(died.CombatantId)
            },
            CombatSimEventEffectExecuted exec => new CombatSimEventEffectExecuted
            {
                ExecutionContextId = exec.ExecutionContextId,
                EffectId = exec.EffectId,
                ActionType = exec.ActionType,
                VfxIndex = exec.VfxIndex,
                Source = RemapNullableInstanceId(exec.Source),
                TriggerSource = RemapNullableInstanceId(exec.TriggerSource),
                Target = FlipEffectTarget(exec.Target)
            },
            CombatSimEventEffectTriggered trig => new CombatSimEventEffectTriggered
            {
                ExecutionContextId = trig.ExecutionContextId,
                EffectId = trig.EffectId,
                Source = RemapNullableInstanceId(trig.Source),
                TriggerSource = RemapNullableInstanceId(trig.TriggerSource),
                Targets = trig.Targets?.Select(t => FlipEffectTarget(t)!).ToList()
            },
            CombatSimEventEffectAuraExecuted aura => new CombatSimEventEffectAuraExecuted
            {
                ExecutionContextId = aura.ExecutionContextId,
                EffectId = aura.EffectId,
                Source = RemapNullableInstanceId(aura.Source),
                TriggerSource = RemapNullableInstanceId(aura.TriggerSource),
                AppliedTo = aura.AppliedTo != null
                    ? new HashSet<IEffectTarget>(aura.AppliedTo.Select(t => FlipEffectTarget(t)!))
                    : null,
                RemovedFrom = aura.RemovedFrom != null
                    ? new HashSet<IEffectTarget>(aura.RemovedFrom.Select(t => FlipEffectTarget(t)!))
                    : null
            },
            CombatSimEventCardTransformed trans => new CombatSimEventCardTransformed(
                trans.ExecutionContextId,
                RemapId(trans.OriginalInstanceId),
                trans.TransformedCards?.Select(CloneAndFlipTransformation).ToList() ?? new List<SimEventCardTransformation>()),
            CombatSimEventCardTransformReverted rev => new CombatSimEventCardTransformReverted(
                rev.OriginalCard != null ? CloneAndFlipTransformation(rev.OriginalCard) : null!,
                rev.TransformedCardInstanceIds?.Select(RemapId).ToList() ?? new List<string>()),
            CombatSimEventCardEnchanted ench => new CombatSimEventCardEnchanted
            {
                InstanceId = RemapId(ench.InstanceId),
                EnchantmentType = ench.EnchantmentType,
                IsReverted = ench.IsReverted
            },
            CombatSimEventCardQuestUpdated qu => new CombatSimEventCardQuestUpdated(
                RemapId(qu.InstanceId),
                qu.QuestGroupIndex,
                qu.QuestEntryIndex,
                qu.OldProgress,
                qu.NewProgress),
            CombatSimEventCardQuestCompleted qc => new CombatSimEventCardQuestCompleted(
                RemapId(qc.InstanceId),
                qc.QuestGroupIndex,
                qc.QuestEntryIndex),
            CombatSimEventCardActionCostSpent cost => new CombatSimEventCardActionCostSpent
            {
                ExecutingCard = RemapInstanceId(cost.ExecutingCard),
                PlayerAttributeSpent = cost.PlayerAttributeSpent,
                CardAttributeSpent = cost.CardAttributeSpent
            },
            CombatSimEventCardAttributeDeleted del => new CombatSimEventCardAttributeDeleted
            {
                InstanceId = RemapInstanceId(del.InstanceId),
                AttributeType = del.AttributeType
            },
            CombatSimEventSandstormCountdownStarted => new CombatSimEventSandstormCountdownStarted(),
            CombatSimEventSandstormStarted => new CombatSimEventSandstormStarted(),
            CombatSimEventMonsterGoldReceived mg => new CombatSimEventMonsterGoldReceived { HealthAmount = mg.HealthAmount },
            CombatSimEventMonsterXpReceived mx => new CombatSimEventMonsterXpReceived { HealthAmount = mx.HealthAmount },
            _ => throw new NotSupportedException($"FlipPerspective 遇到未覆盖的 ICombatSimEvent 子类：{ev.GetType().FullName}")
        };

        var flipped = new CombatSim
        {
            Winner = FlipSide(source.Winner),
            Loser = FlipSide(source.Loser),
            IsDraw = source.IsDraw,
            VfxKeys = source.VfxKeys != null ? new List<string>(source.VfxKeys) : new List<string>(),
            PortraitKeys = source.PortraitKeys != null ? new List<string>(source.PortraitKeys) : new List<string>(),
            OpponentHealthThresholdsForGold = source.OpponentHealthThresholdsForGold != null
                ? new List<float>(source.OpponentHealthThresholdsForGold)
                : new List<float>(),
            OpponentHealthThresholdsForXp = source.OpponentHealthThresholdsForXp != null
                ? new List<float>(source.OpponentHealthThresholdsForXp)
                : new List<float>(),
            CardStats = new Dictionary<string, Dictionary<ECardStats, int>>(StringComparer.Ordinal),
        };

        if (source.CardStats != null)
        {
            foreach (var (cid, statsDict) in source.CardStats)
            {
                string mappedCid = RemapId(cid);
                flipped.CardStats[mappedCid] = statsDict != null
                    ? new Dictionary<ECardStats, int>(statsDict)
                    : new Dictionary<ECardStats, int>();
            }
        }

        flipped.Frames.Clear();
        if (source.Frames != null)
        {
            foreach (var sf in source.Frames)
            {
                var df = new CombatSimFrame
                {
                    PlayerUpdates = ClonePlayerUpdate(sf.OpponentUpdates),
                    OpponentUpdates = ClonePlayerUpdate(sf.PlayerUpdates),
                    CardUpdates = new Dictionary<InstanceId, CombatSimCardUpdate>(),
                    Events = new List<ICombatSimEvent>(sf.Events?.Count ?? 0),
                };

                if (sf.CardUpdates != null)
                {
                    foreach (var (cid, cu) in sf.CardUpdates)
                    {
                        var mappedKey = RemapInstanceId(cid);
                        df.CardUpdates[mappedKey] = CloneAndFlipCardUpdate(cu);
                    }
                }

                if (sf.Events != null)
                {
                    foreach (var ev in sf.Events)
                    {
                        df.Events.Add(CloneAndFlipEvent(ev));
                    }
                }

                flipped.Frames.Add(df);
            }
        }

        return flipped;
    }

    /// <summary>
    /// 将战斗模拟结果转换为客户端 <see cref="CombatSim"/> 消息流。
    /// 保留 6 参数默认签名兼容，支持可选传入 PvE 野怪战金币与经验奖励以生成血条节点与跳币/经验特效，
    /// 以及 P0-40 同步 PvP 权威平局覆盖参数 <paramref name="isDrawOverride"/>（同步作用于顶层 IsDraw 与末帧死亡事件判定）。
    /// </summary>
    public static CombatSim ToCombatSim(
        CombatResult result,
        GameDataRepository repo,
        IReadOnlyDictionary<string, CardInstance> playerCardsByInstanceId,
        IReadOnlyDictionary<string, CardInstance> opponentCardsByInstanceId,
        IReadOnlyList<RunState.CardEntry> playerBoardCards,
        IReadOnlyList<RunState.CardEntry> opponentBoardCards,
        int pveGoldReceived = 0,
        int pveXpReceived = 0,
        bool? isDrawOverride = null,
        Func<string, Guid?>? resolveWireTemplateGuid = null,
        Action<string, string, Guid>? onRuntimeCardTransformed = null,
        int? spawnP1MaxHp = null,
        int? spawnP2MaxHp = null)
    {
        bool effectiveIsDraw = isDrawOverride ?? result.IsDraw;
        bool playerWon = string.Equals(result.WinnerPlayerId, "player", StringComparison.OrdinalIgnoreCase);
        var sim = new CombatSim
        {
            Winner = playerWon ? ECombatantId.Player : ECombatantId.Opponent,
            Loser = playerWon ? ECombatantId.Opponent : ECombatantId.Player,
            IsDraw = effectiveIsDraw,
        };
        sim.Frames.Clear();

        // 建立 CoreCard InstanceId / 协议 InstanceId → 渲染状态映射
        var cardEntryByCoreId = new Dictionary<string, CardRenderState>(StringComparer.Ordinal);
        RegisterBoardCards(cardEntryByCoreId, repo, playerBoardCards, playerCardsByInstanceId, ECombatantId.Player);
        RegisterBoardCards(cardEntryByCoreId, repo, opponentBoardCards, opponentCardsByInstanceId, ECombatantId.Opponent);

        var timeline = result.Timeline;
        int totalMs = Math.Max(FrameStepMs * 10, result.DurationMs + FrameStepMs);
        int maxFrames = Math.Max(2400, (CombatSimulator.MaxDurationMs / FrameStepMs) + 16);
        int frameCount = Math.Clamp((totalMs / FrameStepMs) + 1, 12, maxFrames);

        // 配置 PvE 血条奖励阈值（CoinRewardListener / ExperienceRewardListener 按 Ceil(pct * MaxHp) 精确匹配）
        int oppMaxHp = timeline.Count > 0 ? Math.Max(1, timeline[0].P2MaxHp) : 100;
        var rewardTracker = BuildRewardTracker(sim, oppMaxHp, result.Player2RemainingHp, pveGoldReceived, pveXpReceived);

        // Wave 4A #5 + P1-03：首帧玩家基线默认严格对齐 firstSnap（包含 CombatSpawnSim 同步后的倍率血量 P1MaxHp/P2MaxHp），
        // 并依据 TimeMs == 0 的开战瞬发事件精确回推开战前基线；若外部调用方显式传入未乘倍的 spawnP1MaxHp/spawnP2MaxHp，
        // 则在 Frame 0 自动发射权威 HealthMax/Health 校准更新。
        CombatTickState? prevSnap = timeline.Count > 0
            ? CreateInitialPlayerBaseline(timeline[0], result.Events, spawnP1MaxHp, spawnP2MaxHp)
            : null;
        var prevQuestProgress = new Dictionary<(string CardId, int GroupIdx, int EntryIdx), int>();
        var questCompletedCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var itemActionCredits = new Dictionary<(int TimeMs, string CardInstanceId, EActionCommandType ActionType), int>();
        var vfxKeyMap = new Dictionary<string, int>(StringComparer.Ordinal);
        sim.VfxKeys ??= new List<string>();
        sim.VfxKeys.Clear();
        var firedInFrame = new HashSet<CardRenderState>();
        var playerAttrModifiedInFrame = new HashSet<(int TimeMs, string CardInstanceId)>();
        var passiveUseRecorded = new HashSet<(int TimeMs, string CardInstanceId, string ChainKey)>();
        bool sandstormCountdownEmitted = false;
        bool sandstormStartedEmitted = false;
        int tlIdx = 0;
        int evIdx = 0;
        var events = result.Events;

        for (int f = 0; f < frameCount; f++)
        {
            int frameStartMs = f * FrameStepMs;
            int frameEndMs = frameStartMs + FrameStepMs;
            bool isFirstFrame = f == 0;
            bool isLastFrame = f == frameCount - 1;

            // 找到不晚于 frameEndMs 的最新时间轴快照
            while (tlIdx + 1 < timeline.Count && timeline[tlIdx + 1].TimeMs <= frameEndMs)
                tlIdx++;
            CombatTickState? curSnap = tlIdx < timeline.Count ? timeline[tlIdx] : prevSnap;

            // 预扫描落在本帧窗口内的 CardFired 与 PlayerAttributeModified 事件：
            // 1) CardFired 用于确保冷却重置帧精确发回 CooldownMax 触发客户端 reset 特效；
            // 2) PlayerAttributeModified 用于让纯 PlayerModifyAttribute 卡跳过 CardFired 阶段的无目标盲发，交由结构化事件发射精准目标与飞币 VFX。
            firedInFrame.Clear();
            playerAttrModifiedInFrame.Clear();
            for (int scanIdx = evIdx; scanIdx < events.Count && (isLastFrame || events[scanIdx].TimeMs <= frameEndMs); scanIdx++)
            {
                var scanEv = events[scanIdx];
                if (scanEv.Type == CombatEventType.CardFired &&
                    TryResolveCard(cardEntryByCoreId, scanEv.SourceCardInstanceId, out var firedState))
                {
                    firedInFrame.Add(firedState);
                }
                else if (scanEv.Type == CombatEventType.PlayerAttributeModified &&
                    TryResolveCard(cardEntryByCoreId, scanEv.SourceCardInstanceId, out var modSrcState))
                {
                    playerAttrModifiedInFrame.Add((scanEv.TimeMs, modSrcState.Entry.InstanceId));
                }
            }

            var frame = new CombatSimFrame();

            // 1. 双方血盾烧毒狂暴节拍属性差分、狂暴立绘变身与卡牌全量属性/标签/状态差分
            if (curSnap != null && prevSnap != null)
            {
                frame.PlayerUpdates = BuildPlayerUpdate(sim, prevSnap, curSnap, isPlayer: true, result.Player1Hero);
                frame.OpponentUpdates = BuildPlayerUpdate(sim, prevSnap, curSnap, isPlayer: false, result.Player2Hero);

                if (curSnap.SandstormActive && !sandstormStartedEmitted)
                {
                    if (!sandstormCountdownEmitted)
                    {
                        frame.Events.Add(new CombatSimEventSandstormCountdownStarted());
                        sandstormCountdownEmitted = true;
                    }
                    frame.Events.Add(new CombatSimEventSandstormStarted());
                    sandstormStartedEmitted = true;
                }

                // 血条跨过 PvE 奖励阈值时即时触发飞币/飞经验事件
                EmitCrossedMonsterRewards(frame, rewardTracker, curSnap.P2Hp, forceFlushRemaining: false);

                foreach (var cf in curSnap.Cards)
                {
                    if (!cardEntryByCoreId.TryGetValue(cf.InstanceId, out var state)) continue;
                    ApplyCardFrameDiff(frame, state, cf, f, isFirstFrame, isLastFrame, firedInFrame.Contains(state));
                }
            }

            // 帧内增量追踪双方 HP/Shield，用于拆分伤害的护盾吸收与实际扣血
            var simHealthState = new FrameCombatantState
            {
                P1Hp = prevSnap?.P1Hp ?? 0,
                P1Shield = prevSnap?.P1Shield ?? 0,
                P1MaxHp = curSnap?.P1MaxHp ?? prevSnap?.P1MaxHp ?? 0,
                P2Hp = prevSnap?.P2Hp ?? 0,
                P2Shield = prevSnap?.P2Shield ?? 0,
                P2MaxHp = curSnap?.P2MaxHp ?? prevSnap?.P2MaxHp ?? 0,
            };

            // 2. 落在本 50ms 帧窗口内的战斗事件（尾帧兜底吸收剩余事件防止丢失）
            while (evIdx < events.Count && (isLastFrame || events[evIdx].TimeMs <= frameEndMs))
            {
                var ev = events[evIdx++];
                ProcessCombatEvent(
                    sim,
                    frame,
                    ev,
                    f,
                    evIdx,
                    isLastFrame,
                    curSnap,
                    repo,
                    cardEntryByCoreId,
                    prevQuestProgress,
                    questCompletedCount,
                    itemActionCredits,
                    playerAttrModifiedInFrame,
                    passiveUseRecorded,
                    vfxKeyMap,
                    simHealthState,
                    ref sandstormCountdownEmitted,
                    ref sandstormStartedEmitted,
                    resolveWireTemplateGuid,
                    onRuntimeCardTransformed);
            }

            // 保护客户端 DataExtensions.Update：若本帧存在 HealthAdjustments 且净额与实际 HP 差分不一致，强制携带最终 Health 校准
            if (curSnap != null && prevSnap != null)
            {
                EnsureHealthAttributeSynced(frame.PlayerUpdates, prevSnap.P1Hp, curSnap.P1Hp);
                EnsureHealthAttributeSynced(frame.OpponentUpdates, prevSnap.P2Hp, curSnap.P2Hp);
            }

            // 3. 最后一帧：在 CombatSimEventCombatantDied 触发前完成全量战后清理（P1-04）：
            //    先归位瞬态视觉状态（关闭冰冻/飞行特效），再对战前不存在的战中动态新增属性发射 CombatSimEventCardAttributeDeleted，
            //    并对战前存在但被临时改动的非永久属性复原至战前基线值（完整保留真正的永久/当日/本小时战中成长属性）。
            if (isLastFrame)
            {
                EmitCrossedMonsterRewards(frame, rewardTracker, result.Player2RemainingHp, forceFlushRemaining: true);
                EmitPostCombatCleanupIntoFrame(frame, cardEntryByCoreId, result, repo);

                if (effectiveIsDraw)
                {
                    // 修复 P1-55 / P0-40：平局模式下严格按 IsSimultaneousDeath / IsTimeout 区分双死与超时双存，绝不误把存活方置死
                    if (result.IsSimultaneousDeath || (result.Player1RemainingHp <= 0 && result.Player2RemainingHp <= 0))
                    {
                        frame.PlayerUpdates ??= new CombatSimPlayerUpdate();
                        frame.PlayerUpdates.IsPlayerDead = true;
                        frame.OpponentUpdates ??= new CombatSimPlayerUpdate();
                        frame.OpponentUpdates.IsPlayerDead = true;
                        frame.Events.Add(new CombatSimEventCombatantDied { CombatantId = ECombatantId.Player });
                        frame.Events.Add(new CombatSimEventCombatantDied { CombatantId = ECombatantId.Opponent });
                    }
                    else if (!result.IsTimeout)
                    {
                        if (result.Player2RemainingHp <= 0)
                        {
                            frame.OpponentUpdates ??= new CombatSimPlayerUpdate();
                            frame.OpponentUpdates.IsPlayerDead = true;
                            frame.Events.Add(new CombatSimEventCombatantDied { CombatantId = ECombatantId.Opponent });
                        }
                        if (result.Player1RemainingHp <= 0)
                        {
                            frame.PlayerUpdates ??= new CombatSimPlayerUpdate();
                            frame.PlayerUpdates.IsPlayerDead = true;
                            frame.Events.Add(new CombatSimEventCombatantDied { CombatantId = ECombatantId.Player });
                        }
                    }
                }
                else if (playerWon)
                {
                    frame.OpponentUpdates ??= new CombatSimPlayerUpdate();
                    frame.OpponentUpdates.IsPlayerDead = true;
                    frame.Events.Add(new CombatSimEventCombatantDied { CombatantId = ECombatantId.Opponent });
                }
                else
                {
                    frame.PlayerUpdates ??= new CombatSimPlayerUpdate();
                    frame.PlayerUpdates.IsPlayerDead = true;
                    frame.Events.Add(new CombatSimEventCombatantDied { CombatantId = ECombatantId.Player });
                }
            }

            sim.Frames.Add(frame);
            prevSnap = curSnap;
        }

        // P0-11：将本次战斗通过 sim.CachePortrait 收集的变身立绘地址按索引顺序提交至 CombatSim.PortraitKeys 供客户端 PortraitSwapSystem 消费
        sim.CommitPortraitKeys();
        return sim;
    }

    /// <summary>
    /// 在 <see cref="CombatSimEventCombatantDied"/> 触发前完成全量战后属性清理（P1-04）：
    /// 1) 先将冰冻/减速/急速/飞行/冷热等瞬态视觉特效属性归位，确保客户端特效优雅收尾（兼容 #6/#27）；
    /// 2) 对比每张卡的战前权威基线快照 <see cref="CardRenderState.InitialAttributes"/> 与战中非永久属性：
    ///    - 对战前不存在、战中动态新增（for the fight / UntilEndOfCombat）的属性键，一律发射
    ///      <see cref="CombatSimEventCardAttributeDeleted"/> 彻底从客户端 <c>Card.Attributes</c> 删键（连 :0 都不残留）；
    ///    - 对战前已存在但被战中临时改动的非永久属性键，发射属性复原更新回到战前基线值；
    ///    - 对 <see cref="CombatResult.PostCombatPermanentSync"/> / 任务进度确认的真正永久/当日/本小时战中成长属性完整保留，绝不误清。
    /// </summary>
    private static void EmitPostCombatCleanupIntoFrame(
        CombatSimFrame targetFrame,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        CombatResult result,
        GameDataRepository repo)
    {
        if (cardEntryByCoreId.Count == 0) return;

        var persistentAttrsByCardId = BuildPostCombatPersistentAttributesMap(result, repo);
        var visited = new HashSet<CardRenderState>(ReferenceEqualityComparer.Instance);

        foreach (var state in cardEntryByCoreId.Values)
        {
            if (!visited.Add(state)) continue;

            var entry = state.Entry;
            var cardId = new InstanceId(entry.InstanceId);
            CombatSimCardUpdate? cu = targetFrame.CardUpdates.TryGetValue(cardId, out var existing) ? existing : null;
            bool hasUpdate = cu != null;

            // Step 1：先让 6 个瞬态战斗视觉特效属性归位（在 CombatSimHandler 同帧顺序中 CardUpdates 先于 Events 执行，
            // 可先触发 CardAttributeChanged 关闭冰冻/飞行/冷热特效，随后若战前不存在该键再由 CardAttributeDeleted 彻底删键）
            int outFreeze = state.InitialAttributes.GetValueOrDefault(ECardAttributeType.Freeze, 0);
            int outSlow = state.InitialAttributes.GetValueOrDefault(ECardAttributeType.Slow, 0);
            int outHaste = state.InitialAttributes.GetValueOrDefault(ECardAttributeType.Haste, 0);
            int outFlying = state.InitialAttributes.GetValueOrDefault(ECardAttributeType.Flying, 0) > 0 ? 1 : 0;
            int outHeated = state.InitialAttributes.GetValueOrDefault(ECardAttributeType.Heated, 0) > 0 ? 1 : 0;
            int outChilled = state.InitialAttributes.GetValueOrDefault(ECardAttributeType.Chilled, 0) > 0 ? 1 : 0;

            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Freeze, outFreeze, ref hasUpdate);
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Slow, outSlow, ref hasUpdate);
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Haste, outHaste, ref hasUpdate);
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Flying, outFlying, ref hasUpdate);
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Heated, outHeated, ref hasUpdate);
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Chilled, outChilled, ref hasUpdate);

            // 战中动态变形产物卡：仅收尾瞬态状态键并删除临时战中态键（临时变形将在 DespawnMessage 中销毁，永久变形由 DespawnMessage 权威落盘）
            if (state.IsRuntimeTransformed)
            {
                ECardAttributeType[] transientKeys =
                [
                    ECardAttributeType.Cooldown,
                    ECardAttributeType.Freeze,
                    ECardAttributeType.Slow,
                    ECardAttributeType.Haste,
                    ECardAttributeType.Flying,
                    ECardAttributeType.Heated,
                    ECardAttributeType.Chilled,
                ];
                foreach (var tAttr in transientKeys)
                {
                    if (state.PrevAttributes.ContainsKey(tAttr) && !state.InitialAttributes.ContainsKey(tAttr))
                    {
                        targetFrame.Events.Add(new CombatSimEventCardAttributeDeleted
                        {
                            InstanceId = cardId,
                            AttributeType = tAttr,
                        });
                        state.PrevAttributes.Remove(tAttr);
                    }
                }

                int trAmmoMax = state.PrevAttributes.GetValueOrDefault(ECardAttributeType.AmmoMax, 0);
                if (trAmmoMax > 0)
                    EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Ammo, trAmmoMax, ref hasUpdate);

                if (hasUpdate && cu != null)
                    targetFrame.CardUpdates[cardId] = cu;
                continue;
            }

            // 汇总本卡受保护的永久/当日/本小时战中成长属性集合
            HashSet<ECardAttributeType>? persistentAttrs = ResolvePersistentAttributesForCard(state, persistentAttrsByCardId);

            // Step 2：处理 AmmoMax / Ammo（若 AmmoMax 非永久且战前存在则先复原，有效弹药上限 > 0 时回满弹药）
            bool ammoMaxPersistent = persistentAttrs != null && persistentAttrs.Contains(ECardAttributeType.AmmoMax);
            if (!ammoMaxPersistent && state.InitialAttributes.TryGetValue(ECardAttributeType.AmmoMax, out int initAmmoMax))
            {
                EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.AmmoMax, initAmmoMax, ref hasUpdate);
            }
            int effectiveAmmoMax = (ammoMaxPersistent || state.InitialAttributes.ContainsKey(ECardAttributeType.AmmoMax))
                ? state.PrevAttributes.GetValueOrDefault(ECardAttributeType.AmmoMax, 0)
                : 0;
            if (effectiveAmmoMax > 0)
            {
                int targetAmmo = state.InitialAttributes.TryGetValue(ECardAttributeType.Ammo, out int initAmmo) && !ammoMaxPersistent
                    ? initAmmo
                    : effectiveAmmoMax;
                EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Ammo, targetAmmo, ref hasUpdate);
            }

            // Step 3：逐键比对 state.PrevAttributes 与战前基线 InitialAttributes（按枚举值升序保证确定性输出）
            var trackedKeys = state.PrevAttributes.Keys.ToList();
            trackedKeys.Sort();
            for (int i = 0; i < trackedKeys.Count; i++)
            {
                var attr = trackedKeys[i];

                // 已在 Step 2 回满的有效弹药不再删键或二次改写
                if (attr == ECardAttributeType.Ammo && effectiveAmmoMax > 0)
                    continue;

                // 真正的永久/当日/本小时战中成长属性（以及任务进度）完整保留，绝不误删或回滚
                if (persistentAttrs != null && persistentAttrs.Contains(attr))
                    continue;

                if (!state.InitialAttributes.TryGetValue(attr, out int baselineVal))
                {
                    // 战前不存在、战中动态新增的临时属性键（含 BurnApplyAmount/DamageAmount/CritChance/Counter/FlatCooldownReduction/Cooldown/Freeze 等）：
                    // 发射官方原生 CombatSimEventCardAttributeDeleted，从客户端 Card.Attributes 彻底删除该键
                    targetFrame.Events.Add(new CombatSimEventCardAttributeDeleted
                    {
                        InstanceId = cardId,
                        AttributeType = attr,
                    });
                    state.PrevAttributes.Remove(attr);
                }
                else if (state.PrevAttributes.TryGetValue(attr, out int curVal) && curVal != baselineVal)
                {
                    // 战前已存在但被战中临时修改的非永久属性键：复原回战前基线值
                    EmitAttrIfChangedLazy(state, cardId, ref cu, attr, baselineVal, ref hasUpdate);
                }
            }

            if (hasUpdate && cu != null)
                targetFrame.CardUpdates[cardId] = cu;
        }
    }

    /// <summary>
    /// 从 <see cref="CombatResult.PostCombatPermanentSync"/> 与 <see cref="CombatResult.PostCombatPermanentSyncP2"/>
    /// 提取每张卡在战中真正发生过的非瞬态（Permanent / UntilEndOfDay / UntilEndOfHour / Quest / 永久升阶与附魔）变更属性集合，
    /// 供末帧清理跳过保护，避免误删或误回滚永久战中成长属性。
    /// </summary>
    private static Dictionary<string, HashSet<ECardAttributeType>> BuildPostCombatPersistentAttributesMap(
        CombatResult result,
        GameDataRepository repo)
    {
        var map = new Dictionary<string, HashSet<ECardAttributeType>>(StringComparer.Ordinal);

        void CollectFromSync(PostCombatPermanentSyncResult? sync)
        {
            if (sync?.UpdatedCards == null || sync.UpdatedCards.Count == 0) return;

            for (int i = 0; i < sync.UpdatedCards.Count; i++)
            {
                var delta = sync.UpdatedCards[i];
                var attrs = new HashSet<ECardAttributeType>();

                static void AddParsedAttrKeys(IEnumerable<string>? keys, HashSet<ECardAttributeType> sink)
                {
                    if (keys == null) return;
                    foreach (string raw in keys)
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        if (Enum.TryParse<ECardAttributeType>(raw.Trim(), ignoreCase: true, out var parsed))
                        {
                            sink.Add(parsed);
                            if (parsed is ECardAttributeType.FlatCooldownReduction or ECardAttributeType.PercentCooldownReduction)
                                sink.Add(ECardAttributeType.CooldownMax);
                        }
                    }
                }

                AddParsedAttrKeys(delta.PermanentAddChanges.Keys, attrs);
                AddParsedAttrKeys(delta.PermanentMultChanges.Keys, attrs);
                AddParsedAttrKeys(delta.DayAddChanges.Keys, attrs);
                AddParsedAttrKeys(delta.DayMultChanges.Keys, attrs);
                AddParsedAttrKeys(delta.HourAddChanges.Keys, attrs);
                AddParsedAttrKeys(delta.HourMultChanges.Keys, attrs);
                AddParsedAttrKeys(delta.UpdatedQuestProgress.Keys, attrs);

                if (delta.NewlyCompletedQuests.Count > 0)
                    attrs.Add(ECardAttributeType.QuestCompletedCount);

                // 永久/当日/本小时附魔变更：保留新附魔自带的属性键
                if (delta.EnchantmentChanged || delta.DayEnchantmentChanged || delta.HourEnchantmentChanged)
                {
                    string? activeEnch = delta.NewHourEnchantment ?? delta.NewDayEnchantment ?? delta.NewEnchantment;
                    var tpl = delta.SourceCard?.Template;
                    if (!string.IsNullOrWhiteSpace(activeEnch) && tpl?.Enchantments != null &&
                        tpl.Enchantments.TryGetValue(activeEnch, out var enchData) && enchData != null)
                    {
                        AddParsedAttrKeys(enchData.Attributes?.Keys, attrs);
                        if (enchData.Auras != null)
                        {
                            foreach (var aura in enchData.Auras.Values)
                            {
                                string? auraAttr = aura.Action?.AttributeType;
                                if (!string.IsNullOrWhiteSpace(auraAttr) &&
                                    Enum.TryParse<ECardAttributeType>(auraAttr.Trim(), ignoreCase: true, out var parsedAuraAttr))
                                {
                                    attrs.Add(parsedAuraAttr);
                                }
                            }
                        }
                    }
                }

                // 永久升阶：保留新阶模板属性键，防止把升阶后提升的数值回滚回旧阶
                if (delta.TierChanged && delta.SourceCard?.Template != null)
                {
                    var tierAttrs = new Dictionary<ECardAttributeType, int>();
                    ETier targetTier = delta.NewTier.HasValue
                        ? (ETier)(int)delta.NewTier.Value
                        : (ETier)(int)delta.SourceCard.Tier;
                    PopulateTemplateAttributesForTier(tierAttrs, delta.SourceCard.Template, targetTier);
                    foreach (var k in tierAttrs.Keys)
                        attrs.Add(k);
                }

                // 纯瞬态战斗状态键绝不作为持久属性保护
                attrs.Remove(ECardAttributeType.Cooldown);
                attrs.Remove(ECardAttributeType.Freeze);
                attrs.Remove(ECardAttributeType.Slow);
                attrs.Remove(ECardAttributeType.Haste);

                if (attrs.Count == 0) continue;

                void BindCardKey(string? id)
                {
                    if (string.IsNullOrWhiteSpace(id)) return;
                    if (!map.TryGetValue(id, out var existingSet))
                    {
                        map[id] = new HashSet<ECardAttributeType>(attrs);
                    }
                    else
                    {
                        foreach (var a in attrs) existingSet.Add(a);
                    }
                }

                BindCardKey(delta.InstanceId);
                BindCardKey(delta.SourceCard?.InstanceId);
            }
        }

        CollectFromSync(result.PostCombatPermanentSync);
        CollectFromSync(result.PostCombatPermanentSyncP2);
        return map;
    }

    private static HashSet<ECardAttributeType>? ResolvePersistentAttributesForCard(
        CardRenderState state,
        Dictionary<string, HashSet<ECardAttributeType>> persistentAttrsByCardId)
    {
        HashSet<ECardAttributeType>? merged = null;

        void MergeByKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!persistentAttrsByCardId.TryGetValue(key, out var set)) return;
            merged ??= new HashSet<ECardAttributeType>();
            foreach (var a in set) merged.Add(a);
        }

        MergeByKey(state.Entry.InstanceId);
        MergeByKey(state.Entry.CoreCard?.InstanceId);

        if (state.RuntimePersistentAttributes.Count > 0)
        {
            merged ??= new HashSet<ECardAttributeType>();
            foreach (var a in state.RuntimePersistentAttributes) merged.Add(a);
        }

        return merged;
    }

    private static void RegisterBoardCards(
        Dictionary<string, CardRenderState> map,
        GameDataRepository repo,
        IReadOnlyList<RunState.CardEntry> entries,
        IReadOnlyDictionary<string, CardInstance> coreByEntryId,
        ECombatantId owner)
    {
        foreach (var e in entries)
        {
            CardInstance? coreCard = e.CoreCard;
            if (coreCard == null && coreByEntryId.TryGetValue(e.InstanceId, out var resolvedCore))
                coreCard = resolvedCore;

            string coreTplId = !string.IsNullOrWhiteSpace(coreCard?.Template?.Id)
                ? coreCard!.Template.Id
                : (!string.IsNullOrWhiteSpace(e.CustomCardId) ? e.CustomCardId! : e.TemplateId.ToString());

            var state = new CardRenderState
            {
                Entry = e,
                CoreTemplateId = coreTplId,
                Owner = owner,
            };
            foreach (var kv in e.Attributes)
            {
                state.InitialAttributes[kv.Key] = kv.Value;
                state.PrevAttributes[kv.Key] = kv.Value;
            }

            if (coreCard != null)
            {
                PopulateParsedEnumSet(coreCard.GetAllTags(), state.PrevTags);
                PopulateParsedEnumSet(coreCard.GetAllHiddenTags(), state.PrevHiddenTags);
                PopulateParsedEnumSet(coreCard.GetAllHeroes(), state.PrevHeroes);
            }
            else
            {
                var tpl = ResolveTemplateForCard(repo, state);
                if (tpl != null)
                {
                    PopulateParsedEnumSet(tpl.Tags, state.PrevTags);
                    PopulateParsedEnumSet(tpl.HiddenTags, state.PrevHiddenTags);
                    PopulateParsedEnumSet(tpl.Heroes, state.PrevHeroes);
                }
            }

            map[e.InstanceId] = state;
            if (e.CoreCard != null)
            {
                map[e.CoreCard.InstanceId] = state;
            }
            if (coreByEntryId.TryGetValue(e.InstanceId, out var linkedCore))
            {
                map[linkedCore.InstanceId] = state;
            }
        }
    }

    public static CombatTickState CreateInitialPlayerBaseline(
        CombatTickState firstSnap,
        IReadOnlyList<CombatEvent>? events = null,
        int? spawnP1MaxHp = null,
        int? spawnP2MaxHp = null)
    {
        // 仅对 TimeMs == 0 实际发生过的开战瞬发事件逆推开战前基线；无 TimeMs == 0 变更的字段全部严格对齐 firstSnap，彻底消灭首帧虚假差分。
        int p1ShieldDelta = 0, p2ShieldDelta = 0;
        int p1BurnDelta = 0, p2BurnDelta = 0;
        int p1PoisonDelta = 0, p2PoisonDelta = 0;
        int p1RegenDelta = 0, p2RegenDelta = 0;
        int p1RageDelta = 0, p2RageDelta = 0;
        int p1TempoDelta = 0, p2TempoDelta = 0;
        int p1JoyDelta = 0, p2JoyDelta = 0;
        int p1MaxHpDelta = 0, p2MaxHpDelta = 0;
        int p1HpNetDelta = 0, p2HpNetDelta = 0;
        bool p1EnragedAtStart = false, p2EnragedAtStart = false;
        bool p1HasStartHpChange = false, p2HasStartHpChange = false;

        if (events != null)
        {
            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (ev.TimeMs > 0) break;
                if (ev.TimeMs < 0) continue;

                bool isP1 = string.Equals(ev.TargetPlayerId, "player", StringComparison.OrdinalIgnoreCase);
                int amt = Math.Max(0, (int)Math.Round(ev.Value));
                switch (ev.Type)
                {
                    case CombatEventType.ShieldApplied:
                        if (isP1) p1ShieldDelta += amt; else p2ShieldDelta += amt;
                        break;
                    case CombatEventType.BurnApplied:
                        if (isP1) p1BurnDelta += amt; else p2BurnDelta += amt;
                        break;
                    case CombatEventType.BurnRemoved:
                        if (isP1) p1BurnDelta -= amt; else p2BurnDelta -= amt;
                        break;
                    case CombatEventType.PoisonApplied:
                        if (isP1) p1PoisonDelta += amt; else p2PoisonDelta += amt;
                        break;
                    case CombatEventType.PoisonRemoved:
                        if (isP1) p1PoisonDelta -= amt; else p2PoisonDelta -= amt;
                        break;
                    case CombatEventType.RegenApplied:
                        if (isP1) p1RegenDelta += amt; else p2RegenDelta += amt;
                        break;
                    case CombatEventType.PlayerRageApplied:
                        if (isP1) p1RageDelta += amt; else p2RageDelta += amt;
                        break;
                    case CombatEventType.PlayerEnraged:
                        if (isP1) p1EnragedAtStart = true; else p2EnragedAtStart = true;
                        break;
                    case CombatEventType.PlayerTempoApplied:
                        if (isP1) p1TempoDelta += amt; else p2TempoDelta += amt;
                        break;
                    case CombatEventType.PlayerTempoRemoved:
                        if (isP1) p1TempoDelta -= amt; else p2TempoDelta -= amt;
                        break;
                    case CombatEventType.PlayerJoyApplied:
                        if (isP1) p1JoyDelta += amt; else p2JoyDelta += amt;
                        break;
                    case CombatEventType.PlayerAttributeModified:
                        {
                            // P1-03 / P1-07：若开战 TimeMs == 0 即触发最大生命值修改，逆推其开战前基线
                            int signedDelta = ev.TryGetPlayerAttributeModification(out string attrName, out double prevAttr, out double curAttr)
                                ? (int)Math.Round(curAttr - prevAttr, MidpointRounding.AwayFromZero)
                                : (int)Math.Round(ev.Value, MidpointRounding.AwayFromZero);
                            string effectiveAttr = !string.IsNullOrWhiteSpace(attrName) ? attrName : (ev.PlayerAttributeName ?? string.Empty);
                            if (string.Equals(effectiveAttr, "HealthMax", StringComparison.OrdinalIgnoreCase) && signedDelta != 0)
                            {
                                if (isP1) p1MaxHpDelta += signedDelta; else p2MaxHpDelta += signedDelta;
                            }
                            else if (string.Equals(effectiveAttr, "Health", StringComparison.OrdinalIgnoreCase) && signedDelta != 0)
                            {
                                if (isP1) { p1HpNetDelta += signedDelta; p1HasStartHpChange = true; }
                                else { p2HpNetDelta += signedDelta; p2HasStartHpChange = true; }
                            }
                            break;
                        }
                    case CombatEventType.DamageApplied:
                        {
                            int hpDmg = ev.HpDamage > 0 ? ev.HpDamage : (ev.ShieldAbsorbed > 0 ? 0 : amt);
                            if (hpDmg > 0)
                            {
                                if (isP1) { p1HpNetDelta -= hpDmg; p1HasStartHpChange = true; }
                                else { p2HpNetDelta -= hpDmg; p2HasStartHpChange = true; }
                            }
                            if (ev.ShieldAbsorbed > 0)
                            {
                                if (isP1) p1ShieldDelta -= ev.ShieldAbsorbed;
                                else p2ShieldDelta -= ev.ShieldAbsorbed;
                            }
                            break;
                        }
                    case CombatEventType.HealApplied:
                        {
                            int restored = ev.EffectiveHpRestored ?? amt;
                            if (restored > 0)
                            {
                                if (isP1) { p1HpNetDelta += restored; p1HasStartHpChange = true; }
                                else { p2HpNetDelta += restored; p2HasStartHpChange = true; }
                            }
                            break;
                        }
                }
            }
        }

        // P1-03：若调用方未显式传入开战前未乘倍的 spawnP1MaxHp/spawnP2MaxHp，则严格对齐 firstSnap（扣除 TimeMs==0 变更），
        // 与 RunEngine 在 CombatSpawnSim 写入的倍率后 P1MaxHp/P2MaxHp 100% 一致且零首帧虚假差分；
        // 若调用方显式传入了与 firstSnap 不同的开战基线（如外部回放/沙盒路径），则由 Frame 0 自动发射权威 HealthMax/Health 校准。
        int p1InitMaxHp = spawnP1MaxHp.HasValue && spawnP1MaxHp.Value > 0
            ? spawnP1MaxHp.Value
            : Math.Max(1, firstSnap.P1MaxHp - p1MaxHpDelta);
        int p2InitMaxHp = spawnP2MaxHp.HasValue && spawnP2MaxHp.Value > 0
            ? spawnP2MaxHp.Value
            : Math.Max(1, firstSnap.P2MaxHp - p2MaxHpDelta);

        int p1InitHp = p1HasStartHpChange
            ? Math.Clamp(firstSnap.P1Hp - p1HpNetDelta, 0, Math.Max(p1InitMaxHp, firstSnap.P1Hp))
            : (spawnP1MaxHp.HasValue && spawnP1MaxHp.Value > 0 && spawnP1MaxHp.Value != firstSnap.P1MaxHp
                ? spawnP1MaxHp.Value
                : firstSnap.P1Hp);
        int p2InitHp = p2HasStartHpChange
            ? Math.Clamp(firstSnap.P2Hp - p2HpNetDelta, 0, Math.Max(p2InitMaxHp, firstSnap.P2Hp))
            : (spawnP2MaxHp.HasValue && spawnP2MaxHp.Value > 0 && spawnP2MaxHp.Value != firstSnap.P2MaxHp
                ? spawnP2MaxHp.Value
                : firstSnap.P2Hp);

        return new CombatTickState
        {
            TimeMs = 0,
            P1Hp = p1InitHp,
            P1MaxHp = p1InitMaxHp,
            P1Shield = Math.Max(0, firstSnap.P1Shield - p1ShieldDelta),
            P1Burn = Math.Max(0, firstSnap.P1Burn - p1BurnDelta),
            P1Poison = Math.Max(0, firstSnap.P1Poison - p1PoisonDelta),
            P1Regen = Math.Max(0, firstSnap.P1Regen - p1RegenDelta),
            P1Rage = Math.Max(0, firstSnap.P1Rage - p1RageDelta),
            P1RageMax = firstSnap.P1RageMax,
            P1Enraged = p1EnragedAtStart ? false : firstSnap.P1Enraged,
            P1EnragedDuration = p1EnragedAtStart ? 0 : firstSnap.P1EnragedDuration,
            P1EnragedDurationMax = firstSnap.P1EnragedDurationMax,
            P1Tempo = Math.Max(0, firstSnap.P1Tempo - p1TempoDelta),
            P1TempoGainCooldownMax = firstSnap.P1TempoGainCooldownMax,
            P1Joy = Math.Max(0, firstSnap.P1Joy - p1JoyDelta),
            P1PortraitIndex = p1EnragedAtStart ? 0 : firstSnap.P1PortraitIndex,
            P2Hp = p2InitHp,
            P2MaxHp = p2InitMaxHp,
            P2Shield = Math.Max(0, firstSnap.P2Shield - p2ShieldDelta),
            P2Burn = Math.Max(0, firstSnap.P2Burn - p2BurnDelta),
            P2Poison = Math.Max(0, firstSnap.P2Poison - p2PoisonDelta),
            P2Regen = Math.Max(0, firstSnap.P2Regen - p2RegenDelta),
            P2Rage = Math.Max(0, firstSnap.P2Rage - p2RageDelta),
            P2RageMax = firstSnap.P2RageMax,
            P2Enraged = p2EnragedAtStart ? false : firstSnap.P2Enraged,
            P2EnragedDuration = p2EnragedAtStart ? 0 : firstSnap.P2EnragedDuration,
            P2EnragedDurationMax = firstSnap.P2EnragedDurationMax,
            P2Tempo = Math.Max(0, firstSnap.P2Tempo - p2TempoDelta),
            P2TempoGainCooldownMax = firstSnap.P2TempoGainCooldownMax,
            P2Joy = Math.Max(0, firstSnap.P2Joy - p2JoyDelta),
            P2PortraitIndex = p2EnragedAtStart ? 0 : firstSnap.P2PortraitIndex,
            SandstormActive = firstSnap.SandstormActive,
            SandstormNextDamage = firstSnap.SandstormNextDamage,
        };
    }

    private static void ApplyCardFrameDiff(
        CombatSimFrame frame,
        CardRenderState state,
        CardCooldownFrame cf,
        int frameIndex,
        bool isFirstFrame,
        bool isLastFrame,
        bool firedThisFrame)
    {
        var entry = state.Entry;
        var cardId = new InstanceId(entry.InstanceId);
        // 延迟分配 CombatSimCardUpdate：仅当本帧确实产生属性/状态差分时才实例化（Top #4 消除短命对象风暴）
        CombatSimCardUpdate? cu = frame.CardUpdates.TryGetValue(cardId, out var existingCu) ? existingCu : null;
        bool hasUpdate = cu != null && (cu.Attributes.Count > 0 || cu.State != null || cu.Enchantment.HasValue || cu.Tier.HasValue || cu.Placement != null || cu.Tags != null || cu.HiddenTags != null || cu.Heroes != null);

        // 1. 先同步 CooldownMax（确保客户端读到最新冷却上限），再用差分压缩发射 Cooldown
        int prevCdMax = state.PrevAttributes.GetValueOrDefault(ECardAttributeType.CooldownMax, 0);
        int effectiveCdMax = cf.CooldownMaxMs > 0 ? cf.CooldownMaxMs : prevCdMax;
        bool cdMaxChanged = false;
        if (cf.CooldownMaxMs > 0 || prevCdMax > 0)
        {
            int targetCdMax = cf.CooldownMaxMs > 0 ? cf.CooldownMaxMs : prevCdMax;
            if (targetCdMax != prevCdMax)
            {
                EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.CooldownMax, targetCdMax, ref hasUpdate);
                cdMaxChanged = true;
            }
            effectiveCdMax = targetCdMax;
        }

        // 记录瞬态速率状态变化（冰冻/急速/减速变化会影响冷却推进速率，需同步下发 Cooldown 校准）
        int prevFreeze = state.PrevAttributes.GetValueOrDefault(ECardAttributeType.Freeze, 0);
        int prevHaste = state.PrevAttributes.GetValueOrDefault(ECardAttributeType.Haste, 0);
        int prevSlow = state.PrevAttributes.GetValueOrDefault(ECardAttributeType.Slow, 0);
        double curRate = cf.FreezeMs > 0 ? 0.0 : (cf.HasteMs > 0 && cf.SlowMs <= 0 ? 2.0 : (cf.SlowMs > 0 && cf.HasteMs <= 0 ? 0.5 : 1.0));
        bool paceChanged = Math.Abs(curRate - state.LastEmittedCdRate) > 0.01
                        || (prevFreeze > 0) != (cf.FreezeMs > 0)
                        || (prevHaste > 0) != (cf.HasteMs > 0)
                        || (prevSlow > 0) != (cf.SlowMs > 0);

        if (effectiveCdMax > 0)
        {
            int curCd = firedThisFrame
                ? effectiveCdMax
                : (int)Math.Clamp(Math.Round((1.0 - cf.Progress01) * effectiveCdMax), 0, effectiveCdMax);
            int prevCd = state.PrevAttributes.TryGetValue(ECardAttributeType.Cooldown, out var existingCd)
                ? existingCd
                : effectiveCdMax;

            // V13.2 修复战斗读条 5 FPS 跳帧卡顿：
            // 反编译 ItemVFXController.CooldownEffect.UpdatePosition 直接将 localPosition.y 硬切到服务端下发的 Cooldown 值，
            // 客户端原生没有任何 200ms 间补间插值。必须严格对齐官方 50ms/帧（20 Hz）步进，只要冷却推进变化即逐帧发射 Cooldown。
            bool cdChanged = curCd != prevCd;
            if (firedThisFrame || isFirstFrame || isLastFrame || cdMaxChanged || paceChanged || cdChanged)
            {
                cu ??= new CombatSimCardUpdate { CardInstanceId = cardId };
                cu.Attributes[ECardAttributeType.Cooldown] = new CombatSimCardAttributeUpdate
                {
                    AttributeType = ECardAttributeType.Cooldown,
                    PreviousValue = prevCd,
                    CurrentValue = curCd,
                };
                state.PrevAttributes[ECardAttributeType.Cooldown] = curCd;
                state.LastEmittedCdFrame = frameIndex;
                state.LastEmittedCdValue = curCd;
                state.LastEmittedCdRate = curRate;
                hasUpdate = true;
            }
        }

        // 2. 状态特效属性（毫秒或布尔开关；对连续倒计时 Freeze/Haste/Slow 仅在启停/延长跃变或每 200ms 下发，大幅减包）
        EmitDurationStatusDiff(state, cardId, ref cu, ECardAttributeType.Freeze, cf.FreezeMs, frameIndex, isFirstFrame || isLastFrame, ref hasUpdate);
        EmitDurationStatusDiff(state, cardId, ref cu, ECardAttributeType.Haste, cf.HasteMs, frameIndex, isFirstFrame || isLastFrame, ref hasUpdate);
        EmitDurationStatusDiff(state, cardId, ref cu, ECardAttributeType.Slow, cf.SlowMs, frameIndex, isFirstFrame || isLastFrame, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Flying, cf.IsFlying ? 1 : 0, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Heated, cf.Heated ? 1 : 0, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Chilled, cf.Chilled ? 1 : 0, ref hasUpdate);

        // 3. 弹药与动态数值跳变属性
        int prevAmmoMax = state.PrevAttributes.GetValueOrDefault(ECardAttributeType.AmmoMax, 0);
        int targetAmmoMax = cf.AmmoMax > 0 || prevAmmoMax == 0 ? cf.AmmoMax : prevAmmoMax;
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.AmmoMax, targetAmmoMax, ref hasUpdate);
        if (targetAmmoMax > 0 || state.PrevAttributes.ContainsKey(ECardAttributeType.Ammo) || cf.Ammo > 0)
        {
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Ammo, cf.Ammo, ref hasUpdate);
        }

        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.DamageAmount, cf.DamageAmount, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.ShieldApplyAmount, cf.ShieldApplyAmount, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.BurnApplyAmount, cf.BurnApplyAmount, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.PoisonApplyAmount, cf.PoisonApplyAmount, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.HealAmount, cf.HealAmount, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.RegenApplyAmount, cf.RegenApplyAmount, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.CritChance, cf.CritChance, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Multicast, cf.Multicast, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Lifesteal, cf.Lifesteal, ref hasUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Counter, cf.Counter, ref hasUpdate);

        // V14.2 问题 7 通用根治：逐帧差分同步 Custom_0..Custom_9，使任意自定义机制的黑板计数/层数在战斗中实时刷新到客户端
        for (int ci = 0; ci < SyncedCustomAttrs.Length; ci++)
        {
            var cattr = SyncedCustomAttrs[ci];
            int cval = cf.CustomAttrs != null && ci < cf.CustomAttrs.Length ? cf.CustomAttrs[ci] : 0;
            if (cval != 0 || state.PrevAttributes.ContainsKey(cattr))
                EmitAttrIfChangedLazy(state, cardId, ref cu, cattr, cval, ref hasUpdate);
        }

        // D2-P1-05：消费 CombatSnapshotExtras 稀疏扩展属性并做差分与归零同步（覆盖 Item 与 Skill）
        HashSet<ECardAttributeType>? seenExtraKeys = null;
        if (CombatSnapshotExtras.TryGet(cf, out var extras) && extras != null && extras.Count > 0)
        {
            seenExtraKeys = new HashSet<ECardAttributeType>();
            foreach (var (extraKey, extraVal) in extras)
            {
                if (Enum.TryParse<ECardAttributeType>(extraKey, ignoreCase: true, out var attrEnum))
                {
                    seenExtraKeys.Add(attrEnum);
                    state.PrevExtraAttrKeys.Add(attrEnum);
                    EmitAttrIfChangedLazy(state, cardId, ref cu, attrEnum, extraVal, ref hasUpdate);
                }
            }
        }

        if (state.PrevExtraAttrKeys.Count > 0)
        {
            foreach (var trackedAttr in state.PrevExtraAttrKeys)
            {
                if (seenExtraKeys == null || !seenExtraKeys.Contains(trackedAttr))
                {
                    EmitAttrIfChangedLazy(state, cardId, ref cu, trackedAttr, 0, ref hasUpdate);
                }
            }
        }

        // 兼容 Core 侧可能使用 GetAttribute("SellPrice") 或 GetSellPrice() 两种初始价口径，防止把商店售价误归零
        if (!state.SellPriceOffsetResolved)
        {
            state.SellPriceOffsetResolved = true;
            int entrySell = state.PrevAttributes.GetValueOrDefault(ECardAttributeType.SellPrice, 0);
            bool hasBaseSellAttr = entry.CoreCard?.BaseAttributes.ContainsKey("SellPrice") ?? false;
            if (isFirstFrame && cf.SellPrice == 0 && entrySell > 0 && !hasBaseSellAttr)
            {
                state.SellPriceBaseOffset = entrySell;
            }
        }
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.SellPrice, state.SellPriceBaseOffset + cf.SellPrice, ref hasUpdate);

        // 4. 动态标签 / 隐藏标签 / 英雄集合差分（P1-12）
        if (cf.Tags != null)
        {
            var curTags = ParseEnumSet<ECardTag>(cf.Tags);
            if (!curTags.SetEquals(state.PrevTags))
            {
                cu ??= new CombatSimCardUpdate { CardInstanceId = cardId };
                cu.Tags = curTags;
                state.PrevTags.Clear();
                foreach (var t in curTags) state.PrevTags.Add(t);
                hasUpdate = true;
            }
        }
        if (cf.HiddenTags != null)
        {
            var curHidden = ParseEnumSet<EHiddenTag>(cf.HiddenTags);
            if (!curHidden.SetEquals(state.PrevHiddenTags))
            {
                cu ??= new CombatSimCardUpdate { CardInstanceId = cardId };
                cu.HiddenTags = curHidden;
                state.PrevHiddenTags.Clear();
                foreach (var ht in curHidden) state.PrevHiddenTags.Add(ht);
                hasUpdate = true;
            }
        }
        if (cf.Heroes != null)
        {
            var curHeroes = ParseEnumSet<EHero>(cf.Heroes);
            if (!curHeroes.SetEquals(state.PrevHeroes))
            {
                cu ??= new CombatSimCardUpdate { CardInstanceId = cardId };
                cu.Heroes = curHeroes;
                state.PrevHeroes.Clear();
                foreach (var h in curHeroes) state.PrevHeroes.Add(h);
                hasUpdate = true;
            }
        }

        // 5. 销毁状态变化
        if (cf.IsDestroyed != state.PrevDestroyed)
        {
            cu ??= new CombatSimCardUpdate { CardInstanceId = cardId };
            cu.State = new CombatSimCardStateUpdate
            {
                PreviousValue = state.PrevDestroyed ? ECardState.Disabled : ECardState.Alive,
                CurrentValue = cf.IsDestroyed ? ECardState.Disabled : ECardState.Alive,
            };
            state.PrevDestroyed = cf.IsDestroyed;
            hasUpdate = true;
        }

        if (hasUpdate && cu != null)
        {
            frame.CardUpdates[cardId] = cu;
        }
    }

    private static void EmitDurationStatusDiff(
        CardRenderState state,
        InstanceId cardId,
        ref CombatSimCardUpdate? cu,
        ECardAttributeType attrType,
        int currentValue,
        int frameIndex,
        bool forceEmit,
        ref bool hasUpdate)
    {
        int prevValue = state.PrevAttributes.GetValueOrDefault(attrType, 0);
        if (currentValue == prevValue) return;

        // 启停边界（0 <-> >0）、新增/续期（值变大）或强制帧立刻下发；单纯线性递减按每 4 帧（200ms）合批下发
        bool boundaryOrIncrease = (prevValue == 0) != (currentValue == 0) || currentValue > prevValue;
        if (!forceEmit && !boundaryOrIncrease && (frameIndex % 4 != 0))
            return;

        EmitAttrIfChangedLazy(state, cardId, ref cu, attrType, currentValue, ref hasUpdate);
    }

    private static void EmitAttrIfChangedLazy(
        CardRenderState state,
        InstanceId cardId,
        ref CombatSimCardUpdate? cu,
        ECardAttributeType attrType,
        int currentValue,
        ref bool hasUpdate)
    {
        int prevValue = state.PrevAttributes.GetValueOrDefault(attrType, 0);
        if (currentValue == prevValue) return;

        cu ??= new CombatSimCardUpdate { CardInstanceId = cardId };
        cu.Attributes[attrType] = new CombatSimCardAttributeUpdate
        {
            AttributeType = attrType,
            PreviousValue = prevValue,
            CurrentValue = currentValue,
        };
        state.PrevAttributes[attrType] = currentValue;
        hasUpdate = true;
    }

    private static CombatSimCardUpdate GetOrCreateCardUpdate(CombatSimFrame frame, InstanceId cardId)
    {
        if (!frame.CardUpdates.TryGetValue(cardId, out var cu))
        {
            cu = new CombatSimCardUpdate { CardInstanceId = cardId };
        }
        return cu;
    }

    private static void ProcessCombatEvent(
        CombatSim sim,
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        bool isLastFrame,
        CombatTickState? curSnap,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        Dictionary<(string CardId, int GroupIdx, int EntryIdx), int> prevQuestProgress,
        Dictionary<string, int> questCompletedCount,
        Dictionary<(int TimeMs, string CardInstanceId, EActionCommandType ActionType), int> itemActionCredits,
        HashSet<(int TimeMs, string CardInstanceId)> playerAttrModifiedInFrame,
        HashSet<(int TimeMs, string CardInstanceId, string ChainKey)> passiveUseRecorded,
        Dictionary<string, int> vfxKeyMap,
        FrameCombatantState simHealthState,
        ref bool sandstormCountdownEmitted,
        ref bool sandstormStartedEmitted,
        Func<string, Guid?>? resolveWireTemplateGuid,
        Action<string, string, Guid>? onRuntimeCardTransformed)
    {
        switch (ev.Type)
        {
            case CombatEventType.CardFired:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var srcMeta))
                {
                    var tpl = ResolveTemplateForCard(repo, srcMeta);
                    string effectId = ResolveEffectId(ev, tpl);
                    EActionCommandType act = ResolveActionCommandType(ev, tpl);
                    sim.UpdateStats(srcMeta.Entry.InstanceId, ECardStats.UseCount, 1);
                    passiveUseRecorded.Add((ev.TimeMs, srcMeta.Entry.InstanceId, BuildPassiveUseChainKey(ev)));

                    // 若主动作是卡指卡类型但 CardFired 未携带目标卡，跳过空目标 EffectExecuted（交由后续 Haste/Slow/Freeze 等事件发射真实 EffectTargetCard）
                    if (IsCardTargetedAction(act) && !TryResolveCard(cardEntryByCoreId, ev.TargetCardInstanceId, out _))
                        break;

                    // P1-07：若主动作是 PlayerModifyAttribute 且同一时间点存在同源卡的结构化 PlayerAttributeModified 事件，
                    // 跳过 CardFired 阶段的无属性盲发，交由 PlayerAttributeModified 精准发射目标阵营与飞币 VFX，杜绝同次触发重复发 2 条动作事件
                    if (act == EActionCommandType.PlayerModifyAttribute &&
                        playerAttrModifiedInFrame.Contains((ev.TimeMs, srcMeta.Entry.InstanceId)))
                        break;

                    IEffectTarget target = ResolveCardFiredTarget(ev, act, srcMeta, cardEntryByCoreId);
                    frame.Events.Add(new CombatSimEventEffectExecuted
                    {
                        ExecutionContextId = ResolveExecutionContextId(ev, frameIndex, eventSeq),
                        EffectId = effectId,
                        ActionType = act,
                        Source = new InstanceId(srcMeta.Entry.InstanceId),
                        TriggerSource = ResolveTriggerSourceId(ev, srcMeta, cardEntryByCoreId),
                        Target = target,
                        VfxIndex = ResolveVfxIndex(repo, tpl, effectId, srcMeta.Entry.Enchantment, vfxKeyMap, sim.VfxKeys, ev.VfxKeyOverride),
                    });

                    // 记录本时间点该卡由 CardFired 覆盖的动作，防止主效果重复发射 EffectExecuted
                    if (!ev.IsSkillSource && srcMeta.Entry.Type != ECardType.Skill)
                    {
                        var key = (ev.TimeMs, srcMeta.Entry.InstanceId, act);
                        itemActionCredits[key] = itemActionCredits.GetValueOrDefault(key, 0) + 1;
                    }
                }
                break;

            case CombatEventType.DamageApplied:
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int amt = Math.Max(0, (int)Math.Round(ev.Value));
                    if (amt > 0)
                    {
                        if (ev.ShieldAbsorbed > 0 || ev.HpDamage > 0)
                            ApplyExplicitDamageSplit(frame, simHealthState, targetSide, EDamageType.Damage, ev.ShieldAbsorbed, ev.HpDamage, ev.IsCrit);
                        else
                            ApplyDamageWithShieldSplit(frame, simHealthState, targetSide, EDamageType.Damage, amt, ev.IsCrit);

                        if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var dmgSrc))
                        {
                            EmitActionExecutionIfNeeded(sim, frame, ev, dmgSrc, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.PlayerDamage, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                            sim.UpdateStats(dmgSrc.Entry.InstanceId, ECardStats.DamageDone, amt);
                        }
                    }
                }
                break;

            case CombatEventType.ShieldApplied:
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int amt = Math.Max(0, (int)Math.Round(ev.Value));
                    if (amt > 0)
                    {
                        if (targetSide == ECombatantId.Player) simHealthState.P1Shield += amt;
                        else simHealthState.P2Shield += amt;

                        // P1-07：透传护盾暴击标记 ev.IsCrit
                        AddHealthAdj(frame, targetSide, EDamageType.Shield, EPlayerHealthChangeType.Shield, amt, ev.IsCrit);
                        if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var shSrc))
                        {
                            EmitActionExecutionIfNeeded(sim, frame, ev, shSrc, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.PlayerShieldApply, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                            sim.UpdateStats(shSrc.Entry.InstanceId, ECardStats.ShieldAdded, amt);
                        }
                    }
                }
                break;

            case CombatEventType.RegenApplied:
                {
                    // P0-09：施加再生仅同步 HealthRegen（由 BuildPlayerUpdate 差分下发）、发射 PlayerRegenApply 动作与累计 RegenAdded，绝不加 HP 或发 EDamageType.Heal
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int amt = Math.Max(0, (int)Math.Round(ev.Value));
                    if (amt > 0 && TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var rgSrc))
                    {
                        EmitActionExecutionIfNeeded(
                            sim, frame, ev, rgSrc, frameIndex, eventSeq, repo, cardEntryByCoreId,
                            EActionCommandType.PlayerRegenApply,
                            new EffectTargetPlayer { Target = targetSide },
                            itemActionCredits,
                            passiveUseRecorded,
                            vfxKeyMap,
                            sim.VfxKeys);
                        sim.UpdateStats(rgSrc.Entry.InstanceId, ECardStats.RegenAdded, amt);
                    }
                }
                break;

            case CombatEventType.HealApplied:
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int nominalAmt = Math.Max(0, (int)Math.Round(ev.Value));
                    bool isRegenGain = string.Equals(ev.Detail, "RegenGain", StringComparison.Ordinal);
                    if (isRegenGain)
                    {
                        // P0-09 兜底：兼容旧合成事件 HealApplied+Detail="RegenGain"，同样绝不污染 HP / HealthAdjustments
                        if (nominalAmt > 0 && TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var legacyRgSrc))
                        {
                            EmitActionExecutionIfNeeded(
                                sim, frame, ev, legacyRgSrc, frameIndex, eventSeq, repo, cardEntryByCoreId,
                                EActionCommandType.PlayerRegenApply,
                                new EffectTargetPlayer { Target = targetSide },
                                itemActionCredits,
                                passiveUseRecorded,
                                vfxKeyMap,
                                sim.VfxKeys);
                            sim.UpdateStats(legacyRgSrc.Entry.InstanceId, ECardStats.RegenAdded, nominalAmt);
                        }
                        break;
                    }

                    if (nominalAmt > 0)
                    {
                        // P1-15：区分名义治疗量（nominalAmt）与实际净回血量（effectiveRestored），满血零回血不发 HealthAdjustments
                        int effectiveRestored;
                        if (ev.EffectiveHpRestored.HasValue)
                        {
                            effectiveRestored = Math.Max(0, ev.EffectiveHpRestored.Value);
                        }
                        else
                        {
                            int curHp = targetSide == ECombatantId.Player ? simHealthState.P1Hp : simHealthState.P2Hp;
                            int maxHp = targetSide == ECombatantId.Player ? simHealthState.P1MaxHp : simHealthState.P2MaxHp;
                            effectiveRestored = maxHp > 0 ? Math.Min(nominalAmt, Math.Max(0, maxHp - curHp)) : nominalAmt;
                        }

                        if (effectiveRestored > 0)
                        {
                            if (targetSide == ECombatantId.Player)
                                simHealthState.P1Hp = simHealthState.P1MaxHp > 0 ? Math.Min(simHealthState.P1MaxHp, simHealthState.P1Hp + effectiveRestored) : simHealthState.P1Hp + effectiveRestored;
                            else
                                simHealthState.P2Hp = simHealthState.P2MaxHp > 0 ? Math.Min(simHealthState.P2MaxHp, simHealthState.P2Hp + effectiveRestored) : simHealthState.P2Hp + effectiveRestored;

                            // P1-07：透传治疗暴击标记 ev.IsCrit
                            AddHealthAdj(frame, targetSide, EDamageType.Heal, EPlayerHealthChangeType.Health, effectiveRestored, ev.IsCrit);
                        }

                        if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var hlSrc))
                        {
                            bool isLifesteal = string.Equals(ev.Detail, "Lifesteal", StringComparison.Ordinal);
                            // 8.4：Lifesteal 仅恢复 HP，不计入玩家卡牌统计 HealAdded，也不触发 PlayerHeal 动作执行
                            if (!isLifesteal)
                            {
                                EmitActionExecutionIfNeeded(
                                    sim, frame, ev, hlSrc, frameIndex, eventSeq, repo, cardEntryByCoreId,
                                    EActionCommandType.PlayerHeal,
                                    new EffectTargetPlayer { Target = targetSide },
                                    itemActionCredits,
                                    passiveUseRecorded,
                                    vfxKeyMap,
                                    sim.VfxKeys);
                                sim.UpdateStats(hlSrc.Entry.InstanceId, ECardStats.HealAdded, nominalAmt);
                            }
                        }
                    }
                }
                break;

            case CombatEventType.BurnApplied:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var bSrc))
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    EmitActionExecutionIfNeeded(sim, frame, ev, bSrc, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.PlayerBurnApply, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                    sim.UpdateStats(bSrc.Entry.InstanceId, ECardStats.BurnAdded, Math.Max(0, (int)Math.Round(ev.Value)));
                }
                break;

            case CombatEventType.PoisonApplied:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var pSrc))
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    EmitActionExecutionIfNeeded(sim, frame, ev, pSrc, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.PlayerPoisonApply, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                    sim.UpdateStats(pSrc.Entry.InstanceId, ECardStats.PoisonAdded, Math.Max(0, (int)Math.Round(ev.Value)));
                }
                break;

            case CombatEventType.BurnRemoved:
                // D2-P1-05：灼烧净化动作发射 PlayerBurnRemove（属性差分仍由快照驱动，不重复改值）
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var brSrc))
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    EmitActionExecutionIfNeeded(sim, frame, ev, brSrc, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.PlayerBurnRemove, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                }
                break;

            case CombatEventType.PoisonRemoved:
                // D2-P1-05：剧毒净化动作发射 PlayerPoisonRemove（属性差分仍由快照驱动，不重复改值）
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var prSrc))
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    EmitActionExecutionIfNeeded(sim, frame, ev, prSrc, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.PlayerPoisonRemove, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                }
                break;

            case CombatEventType.HasteApplied:
                EmitCardTargetedEffect(sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardHaste, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var hSrc))
                    sim.UpdateStats(hSrc.Entry.InstanceId, ECardStats.HastedCardsCount, 1);
                break;

            case CombatEventType.SlowApplied:
                EmitCardTargetedEffect(sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardSlow, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var sSrc))
                    sim.UpdateStats(sSrc.Entry.InstanceId, ECardStats.SlowedCardsCount, 1);
                break;

            case CombatEventType.FreezeApplied:
                EmitCardTargetedEffect(sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardFreeze, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var fSrc))
                    sim.UpdateStats(fSrc.Entry.InstanceId, ECardStats.FrozenCardsCount, 1);
                break;

            case CombatEventType.ChargeApplied:
                EmitCardTargetedEffect(sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardCharge, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardReloaded:
                EmitCardTargetedEffect(sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardReload, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardDestroyed:
                EmitCardTargetedEffect(sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardDestroy, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.FlyingChanged:
                EmitCardTargetedEffect(
                    sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId,
                    ev.Value > 0 ? EActionCommandType.FlyingStart : EActionCommandType.FlyingStop,
                    passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardAttributeModified:
                EmitCardTargetedEffect(sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardModifyAttribute, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardAttributeDeleted:
                HandleCardAttributeDeletedEvent(frame, ev, cardEntryByCoreId);
                break;

            case CombatEventType.CardUpgraded:
                HandleCardUpgradedEvent(sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardEnchanted:
                HandleCardEnchantedEvent(sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardTransformed:
                HandleCardTransformedEvent(sim, frame, ev, frameIndex, eventSeq, isLastFrame, curSnap, repo, cardEntryByCoreId, passiveUseRecorded, vfxKeyMap, sim.VfxKeys, resolveWireTemplateGuid, onRuntimeCardTransformed);
                break;

            case CombatEventType.CardTransformReverted:
                HandleCardTransformRevertedEvent(frame, ev, repo, cardEntryByCoreId, resolveWireTemplateGuid);
                break;

            case CombatEventType.AuraExecuted:
                HandleAuraExecutedEvent(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.QuestUpdated:
                HandleQuestUpdatedEvent(frame, ev, repo, cardEntryByCoreId, prevQuestProgress);
                break;

            case CombatEventType.QuestCompleted:
                HandleQuestCompletedEvent(frame, ev, repo, cardEntryByCoreId, prevQuestProgress, questCompletedCount);
                break;

            case CombatEventType.PlayerRageApplied:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var rageSrc))
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int gained = Math.Max(0, (int)Math.Round(ev.Value));
                    if (gained > 0)
                    {
                        EmitActionExecutionIfNeeded(sim, frame, ev, rageSrc, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.PlayerRageApply, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                        sim.UpdateStats(rageSrc.Entry.InstanceId, ECardStats.RageAdded, gained);
                    }
                }
                break;

            case CombatEventType.PlayerJoyApplied:
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int gained = Math.Max(0, (int)Math.Round(ev.Value));
                    if (gained > 0)
                    {
                        AddHealthAdj(frame, targetSide, EDamageType.Joy, EPlayerHealthChangeType.Joy, gained, ev.IsCrit);
                        if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var joySrc))
                        {
                            EmitActionExecutionIfNeeded(sim, frame, ev, joySrc, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.PlayerJoyApply, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                            sim.UpdateStats(joySrc.Entry.InstanceId, ECardStats.JoyAdded, gained);
                        }
                    }
                }
                break;

            case CombatEventType.PlayerTempoApplied:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var tpSrc))
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    EmitActionExecutionIfNeeded(sim, frame, ev, tpSrc, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.PlayerTempoApply, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                    int gained = Math.Max(0, (int)Math.Round(ev.Value));
                    if (gained > 0)
                        sim.UpdateStats(tpSrc.Entry.InstanceId, ECardStats.TempoAdded, gained);
                }
                break;

            case CombatEventType.PlayerTempoRemoved:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var tpRemSrc))
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    EmitActionExecutionIfNeeded(sim, frame, ev, tpRemSrc, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.PlayerTempoRemove, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, passiveUseRecorded, vfxKeyMap, sim.VfxKeys);
                }
                break;

            case CombatEventType.PlayerAttributeModified:
                HandlePlayerAttributeModifiedEvent(
                    sim,
                    frame,
                    ev,
                    frameIndex,
                    eventSeq,
                    hasTimelineSnap: curSnap != null,
                    repo,
                    cardEntryByCoreId,
                    itemActionCredits,
                    passiveUseRecorded,
                    vfxKeyMap,
                    sim.VfxKeys);
                break;

            case CombatEventType.ActionCostSpent:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var costSrc) && !string.IsNullOrWhiteSpace(ev.Detail))
                {
                    string costToken = ev.Detail.Trim();
                    if (string.Equals(costToken, "Tempo", StringComparison.OrdinalIgnoreCase))
                    {
                        frame.Events.Add(new CombatSimEventCardActionCostSpent
                        {
                            ExecutingCard = new InstanceId(costSrc.Entry.InstanceId),
                            PlayerAttributeSpent = EPlayerAttributeType.Tempo,
                            CardAttributeSpent = null,
                        });
                        // P0-11：同步发射 PlayerTempoRemove 动作事件（Target 指向消费方玩家），触发 BoardUIController.OnEffectExecuted -> PlayActiveTempoSpendBurst()
                        var costTpl = ResolveTemplateForCard(repo, costSrc);
                        string costEffectId = ResolveEffectId(ev, costTpl);
                        frame.Events.Add(new CombatSimEventEffectExecuted
                        {
                            ExecutionContextId = ResolveExecutionContextId(ev, frameIndex, eventSeq, "ctx_tempo"),
                            EffectId = costEffectId,
                            ActionType = EActionCommandType.PlayerTempoRemove,
                            Source = new InstanceId(costSrc.Entry.InstanceId),
                            TriggerSource = ResolveTriggerSourceId(ev, costSrc, cardEntryByCoreId),
                            Target = new EffectTargetPlayer { Target = costSrc.Owner },
                            VfxIndex = ResolveVfxIndex(repo, costTpl, costEffectId, costSrc.Entry.Enchantment, vfxKeyMap, sim.VfxKeys, ev.VfxKeyOverride),
                        });
                        TryRecordPassiveUseCount(sim, ev, costSrc, passiveUseRecorded);
                        int spent = Math.Max(0, (int)Math.Round(ev.Value));
                        if (spent > 0)
                            sim.UpdateStats(costSrc.Entry.InstanceId, ECardStats.TempoSpent, spent);
                    }
                    else if (Enum.TryParse<EPlayerAttributeType>(costToken, ignoreCase: true, out var playerCostAttr))
                    {
                        // D2-P1-05：解除仅限 Tempo 限制，支持任意玩家资源消耗（如 Health/Shield/Gold/Rage/Joy）
                        frame.Events.Add(new CombatSimEventCardActionCostSpent
                        {
                            ExecutingCard = new InstanceId(costSrc.Entry.InstanceId),
                            PlayerAttributeSpent = playerCostAttr,
                            CardAttributeSpent = null,
                        });
                        TryRecordPassiveUseCount(sim, ev, costSrc, passiveUseRecorded);
                    }
                    else if (Enum.TryParse<ECardAttributeType>(costToken, ignoreCase: true, out var cardCostAttr))
                    {
                        // D2-P1-05：支持任意卡牌自身资源消耗（如 Ammo/Counter/Custom_0 等）
                        frame.Events.Add(new CombatSimEventCardActionCostSpent
                        {
                            ExecutingCard = new InstanceId(costSrc.Entry.InstanceId),
                            PlayerAttributeSpent = null,
                            CardAttributeSpent = cardCostAttr,
                        });
                        TryRecordPassiveUseCount(sim, ev, costSrc, passiveUseRecorded);
                    }
                }
                break;

            case CombatEventType.BurnTick:
                {
                    // P0-10：精准消费 ev.ShieldAbsorbed 与 ev.HpDamage 分别扣减护盾/生命并下发 Shield 与 Health 两条 HealthAdjustments
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    if (ev.ShieldAbsorbed > 0 || ev.HpDamage > 0)
                    {
                        ApplyExplicitDamageSplit(frame, simHealthState, targetSide, EDamageType.Burn, ev.ShieldAbsorbed, ev.HpDamage, isCrit: false);
                    }
                    else
                    {
                        int amt = Math.Max(0, (int)Math.Round(ev.Value));
                        if (amt > 0)
                            ApplyDamageWithShieldSplit(frame, simHealthState, targetSide, EDamageType.Burn, amt, isCrit: false);
                    }
                }
                break;

            case CombatEventType.PoisonTick:
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int amt = Math.Max(0, (int)Math.Round(ev.Value));
                    if (amt > 0)
                    {
                        if (targetSide == ECombatantId.Player) simHealthState.P1Hp -= amt;
                        else simHealthState.P2Hp -= amt;
                        AddHealthAdj(frame, targetSide, EDamageType.Poison, EPlayerHealthChangeType.Health, -amt, false);
                    }
                }
                break;

            case CombatEventType.RegenTick:
                {
                    // P1-15：满血零回血不再发射 Regen HealthAdjustments，仅在有效净回血 > 0 时同步并飘字
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int nominalAmt = Math.Max(0, (int)Math.Round(ev.Value));
                    if (nominalAmt > 0)
                    {
                        int effectiveRestored;
                        if (ev.EffectiveHpRestored.HasValue)
                        {
                            effectiveRestored = Math.Max(0, ev.EffectiveHpRestored.Value);
                        }
                        else
                        {
                            int curHp = targetSide == ECombatantId.Player ? simHealthState.P1Hp : simHealthState.P2Hp;
                            int maxHp = targetSide == ECombatantId.Player ? simHealthState.P1MaxHp : simHealthState.P2MaxHp;
                            effectiveRestored = maxHp > 0 ? Math.Min(nominalAmt, Math.Max(0, maxHp - curHp)) : nominalAmt;
                        }

                        if (effectiveRestored > 0)
                        {
                            if (targetSide == ECombatantId.Player)
                                simHealthState.P1Hp = simHealthState.P1MaxHp > 0 ? Math.Min(simHealthState.P1MaxHp, simHealthState.P1Hp + effectiveRestored) : simHealthState.P1Hp + effectiveRestored;
                            else
                                simHealthState.P2Hp = simHealthState.P2MaxHp > 0 ? Math.Min(simHealthState.P2MaxHp, simHealthState.P2Hp + effectiveRestored) : simHealthState.P2Hp + effectiveRestored;
                            AddHealthAdj(frame, targetSide, EDamageType.Regen, EPlayerHealthChangeType.Health, effectiveRestored, false);
                        }
                    }
                }
                break;

            case CombatEventType.SandstormWarning:
                if (!sandstormCountdownEmitted)
                {
                    frame.Events.Add(new CombatSimEventSandstormCountdownStarted());
                    sandstormCountdownEmitted = true;
                }
                break;

            case CombatEventType.SandstormTick:
                {
                    if (!sandstormStartedEmitted)
                    {
                        frame.Events.Add(new CombatSimEventSandstormStarted());
                        sandstormStartedEmitted = true;
                    }
                    // D1-P2-09：优先消费按玩家拆分的实际减伤后伤害与盾/血明细，避免 100% 减伤或固定减伤下对双方误播原伤
                    bool hasPerSideSplit = ev.P1SandstormDamage.HasValue || ev.P2SandstormDamage.HasValue;
                    if (hasPerSideSplit)
                    {
                        int p1Shield = Math.Max(0, ev.P1SandstormShieldAbsorbed);
                        int p1Hp = Math.Max(0, ev.P1SandstormHpDamage);
                        int p1Total = Math.Max(0, ev.P1SandstormDamage ?? (p1Shield + p1Hp));
                        if (p1Shield > 0 || p1Hp > 0)
                            ApplyExplicitDamageSplit(frame, simHealthState, ECombatantId.Player, EDamageType.Sandstorm, p1Shield, p1Hp, isCrit: false);
                        else if (p1Total > 0)
                            ApplyDamageWithShieldSplit(frame, simHealthState, ECombatantId.Player, EDamageType.Sandstorm, p1Total, false);

                        int p2Shield = Math.Max(0, ev.P2SandstormShieldAbsorbed);
                        int p2Hp = Math.Max(0, ev.P2SandstormHpDamage);
                        int p2Total = Math.Max(0, ev.P2SandstormDamage ?? (p2Shield + p2Hp));
                        if (p2Shield > 0 || p2Hp > 0)
                            ApplyExplicitDamageSplit(frame, simHealthState, ECombatantId.Opponent, EDamageType.Sandstorm, p2Shield, p2Hp, isCrit: false);
                        else if (p2Total > 0)
                            ApplyDamageWithShieldSplit(frame, simHealthState, ECombatantId.Opponent, EDamageType.Sandstorm, p2Total, false);
                    }
                    else
                    {
                        int amt = Math.Max(0, (int)Math.Round(ev.Value));
                        if (amt > 0)
                        {
                            ApplyDamageWithShieldSplit(frame, simHealthState, ECombatantId.Player, EDamageType.Sandstorm, amt, false);
                            ApplyDamageWithShieldSplit(frame, simHealthState, ECombatantId.Opponent, EDamageType.Sandstorm, amt, false);
                        }
                    }
                }
                break;
        }
    }

    /// <summary>P2-10：同一触发链共享模拟器传递的 ExecutionContextId，缺省回退按帧序号唯一生成。</summary>
    private static string ResolveExecutionContextId(CombatEvent ev, int frameIndex, int eventSeq, string fallbackPrefix = "ctx")
        => !string.IsNullOrEmpty(ev.ExecutionContextId)
            ? ev.ExecutionContextId
            : $"{fallbackPrefix}_{frameIndex}_{eventSeq}";

    private static string BuildPassiveUseChainKey(CombatEvent ev)
        => !string.IsNullOrEmpty(ev.ExecutionContextId)
            ? $"{ev.ExecutionContextId}|{ev.AbilityId ?? string.Empty}"
            : $"{ev.AbilityId ?? ev.ActionType ?? string.Empty}";

    /// <summary>
    /// P2-13：为无主动冷却的纯被动物品及技能按单次触发链去重累加 ECardStats.UseCount（避免 TActionAnd 多子动作重复计数）。
    /// </summary>
    private static void TryRecordPassiveUseCount(
        CombatSim sim,
        CombatEvent ev,
        CardRenderState srcMeta,
        HashSet<(int TimeMs, string CardInstanceId, string ChainKey)> passiveUseRecorded)
    {
        bool isSkill = ev.IsSkillSource || srcMeta.Entry.Type == ECardType.Skill;
        bool hasCooldown = srcMeta.Entry.Attributes.TryGetValue(ECardAttributeType.CooldownMax, out int cdMax) && cdMax > 0;
        if (!isSkill && hasCooldown)
            return;

        if (passiveUseRecorded.Add((ev.TimeMs, srcMeta.Entry.InstanceId, BuildPassiveUseChainKey(ev))))
            sim.UpdateStats(srcMeta.Entry.InstanceId, ECardStats.UseCount, 1);
    }

    private static void EmitCardTargetedEffect(
        CombatSim sim,
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        EActionCommandType defaultAction,
        HashSet<(int TimeMs, string CardInstanceId, string ChainKey)> passiveUseRecorded,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
    {
        bool hasSrc = TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var srcMeta);
        bool hasTgt = TryResolveCard(cardEntryByCoreId, ev.TargetCardInstanceId, out var tgtMeta);
        if (!hasSrc && !hasTgt) return;

        var effectiveSrc = hasSrc ? srcMeta : tgtMeta;
        var tpl = ResolveTemplateForCard(repo, effectiveSrc);
        string effectId = ResolveEffectId(ev, tpl);
        EActionCommandType act = !string.IsNullOrEmpty(ev.ActionType)
            ? MapActionTypeString(ev.ActionType, ev.Value, defaultAction)
            : defaultAction;

        // 仅当目标卡可解析时使用 EffectTargetCard（避免客户端对缺失 TargetCard 的 CardAction 抛 NRE）
        if (!hasTgt)
            return;

        IEffectTarget target = new EffectTargetCard { Target = new InstanceId(tgtMeta.Entry.InstanceId) };
        frame.Events.Add(new CombatSimEventEffectExecuted
        {
            ExecutionContextId = ResolveExecutionContextId(ev, frameIndex, eventSeq),
            EffectId = effectId,
            ActionType = act,
            Source = new InstanceId(effectiveSrc.Entry.InstanceId),
            TriggerSource = ResolveTriggerSourceId(ev, effectiveSrc, cardEntryByCoreId),
            Target = target,
            VfxIndex = ResolveVfxIndex(repo, tpl, effectId, effectiveSrc.Entry.Enchantment, vfxKeyMap, vfxKeys, ev.VfxKeyOverride),
        });
        if (hasSrc)
            TryRecordPassiveUseCount(sim, ev, srcMeta, passiveUseRecorded);
    }

    private static void EmitActionExecutionIfNeeded(
        CombatSim sim,
        CombatSimFrame frame,
        CombatEvent ev,
        CardRenderState srcMeta,
        int frameIndex,
        int eventSeq,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        EActionCommandType defaultAction,
        IEffectTarget target,
        Dictionary<(int TimeMs, string CardInstanceId, EActionCommandType ActionType), int> itemActionCredits,
        HashSet<(int TimeMs, string CardInstanceId, string ChainKey)> passiveUseRecorded,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
    {
        var tpl = ResolveTemplateForCard(repo, srcMeta);
        string effectId = ResolveEffectId(ev, tpl);
        EActionCommandType act = !string.IsNullOrEmpty(ev.ActionType)
            ? MapActionTypeString(ev.ActionType, ev.Value, defaultAction)
            : defaultAction;

        bool isSkill = ev.IsSkillSource || srcMeta.Entry.Type == ECardType.Skill;
        bool hasExplicitVfx = !string.IsNullOrWhiteSpace(ev.VfxKeyOverride);
        if (!isSkill && !hasExplicitVfx)
        {
            var key = (ev.TimeMs, srcMeta.Entry.InstanceId, act);
            if (itemActionCredits.TryGetValue(key, out int credits) && credits > 0)
            {
                if (credits == 1) itemActionCredits.Remove(key);
                else itemActionCredits[key] = credits - 1;
                return;
            }
        }

        // P2-09：已移除客户端 0 消费的冗余 CombatSimEventEffectTriggered，直接由 CombatSimEventEffectExecuted 驱动完整动画与脉冲
        frame.Events.Add(new CombatSimEventEffectExecuted
        {
            ExecutionContextId = ResolveExecutionContextId(ev, frameIndex, eventSeq),
            EffectId = effectId,
            ActionType = act,
            Source = new InstanceId(srcMeta.Entry.InstanceId),
            TriggerSource = ResolveTriggerSourceId(ev, srcMeta, cardEntryByCoreId),
            Target = target,
            VfxIndex = ResolveVfxIndex(repo, tpl, effectId, srcMeta.Entry.Enchantment, vfxKeyMap, vfxKeys, ev.VfxKeyOverride),
        });
        TryRecordPassiveUseCount(sim, ev, srcMeta, passiveUseRecorded);
    }

    private static void HandlePlayerAttributeModifiedEvent(
        CombatSim sim,
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        bool hasTimelineSnap,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        Dictionary<(int TimeMs, string CardInstanceId, EActionCommandType ActionType), int> itemActionCredits,
        HashSet<(int TimeMs, string CardInstanceId, string ChainKey)> passiveUseRecorded,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
    {
        bool hasTriple = ev.TryGetPlayerAttributeModification(out string attrName, out double prevVal, out double curVal);
        if (!hasTriple)
        {
            attrName = ev.PlayerAttributeName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(attrName)) return;
            prevVal = 0;
            curVal = ev.Value;
        }

        double rawDelta = hasTriple ? (curVal - prevVal) : ev.Value;
        if (Math.Abs(rawDelta) <= 1e-6) return;

        var targetSide = ParseCombatantSide(ev.TargetPlayerId);
        int prevInt = (int)Math.Round(prevVal, MidpointRounding.AwayFromZero);
        int curInt = (int)Math.Round(curVal, MidpointRounding.AwayFromZero);

        // 1. 将玩家属性变化（如 Gold / Income / Prestige / Experience / Level 等）写入本帧 PlayerUpdates / OpponentUpdates，
        //    驱动客户端 CombatSimHandler.ProcessPlayerUpdate -> Data.Run.Player.Update + Events.PlayerAttributeChanged -> BankToyController 实时入账飘字。
        if (Enum.TryParse<EPlayerAttributeType>(attrName, ignoreCase: true, out var playerAttrType))
        {
            CombatSimPlayerUpdate targetUpdate;
            if (targetSide == ECombatantId.Player)
            {
                frame.PlayerUpdates ??= new CombatSimPlayerUpdate();
                targetUpdate = frame.PlayerUpdates;
            }
            else
            {
                frame.OpponentUpdates ??= new CombatSimPlayerUpdate();
                targetUpdate = frame.OpponentUpdates;
            }

            bool isTimelineTracked = IsTimelineTrackedPlayerAttribute(playerAttrType);
            if (targetUpdate.Attributes.TryGetValue(playerAttrType, out var existingAttr))
            {
                // 同帧内多次修改非 Timeline 追踪属性（或无 Timeline 的纯合成场景），保留首前值并推进到最新终值；
                // 当存在权威 Timeline 快照时，Timeline 已追踪属性（如 HealthMax / Health / Shield）一律以 BuildPlayerUpdate 为唯一权威源。
                if (!isTimelineTracked || !hasTimelineSnap)
                {
                    existingAttr.CurrentValue = curInt;
                }
            }
            else if (!isTimelineTracked || (!hasTimelineSnap && prevInt != curInt))
            {
                targetUpdate.Attributes[playerAttrType] = new CombatSimPlayerAttributeUpdate
                {
                    AttributeType = playerAttrType,
                    PreviousValue = prevInt,
                    CurrentValue = curInt,
                };
            }
        }

        // 2. 发射标准 CombatSimEventEffectExecuted(ActionType = PlayerModifyAttribute, Target = EffectTargetPlayer)，
        //    使客户端战斗审计完整记录该动作；若为战中生金（Gold 正向增量）且源技能未配置自定义 VFX，自动回退官方飞币爆裂弹道 VFX。
        if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var srcMeta))
        {
            string? effectiveVfxOverride = ev.VfxKeyOverride;
            if (string.IsNullOrWhiteSpace(effectiveVfxOverride)
                && string.Equals(attrName, "Gold", StringComparison.OrdinalIgnoreCase)
                && rawDelta > 0)
            {
                var tpl = ResolveTemplateForCard(repo, srcMeta);
                string effectId = ResolveEffectId(ev, tpl);
                int? existingVfx = ResolveVfxIndex(repo, tpl, effectId, srcMeta.Entry.Enchantment, vfxKeyMap, vfxKeys);
                if (!existingVfx.HasValue)
                {
                    effectiveVfxOverride = OfficialCoinBurstVfxKey;
                }
            }

            var effectiveEv = string.Equals(effectiveVfxOverride, ev.VfxKeyOverride, StringComparison.Ordinal)
                ? ev
                : new CombatEvent
                {
                    TimeMs = ev.TimeMs,
                    Type = ev.Type,
                    SourcePlayerId = ev.SourcePlayerId,
                    TargetPlayerId = ev.TargetPlayerId,
                    SourceCardInstanceId = ev.SourceCardInstanceId,
                    TriggerSourceCardInstanceId = ev.TriggerSourceCardInstanceId,
                    TargetCardInstanceId = ev.TargetCardInstanceId,
                    Value = ev.Value,
                    Detail = ev.Detail,
                    AbilityId = ev.AbilityId,
                    ActionType = ev.ActionType,
                    IsSkillSource = ev.IsSkillSource,
                    ExecutionContextId = ev.ExecutionContextId,
                    VfxKeyOverride = effectiveVfxOverride,
                };

            EmitActionExecutionIfNeeded(
                sim,
                frame,
                effectiveEv,
                srcMeta,
                frameIndex,
                eventSeq,
                repo,
                cardEntryByCoreId,
                EActionCommandType.PlayerModifyAttribute,
                new EffectTargetPlayer { Target = targetSide },
                itemActionCredits,
                passiveUseRecorded,
                vfxKeyMap,
                vfxKeys);
        }
    }

    private static bool IsTimelineTrackedPlayerAttribute(EPlayerAttributeType attr) => attr is
        EPlayerAttributeType.Health or
        EPlayerAttributeType.HealthMax or
        EPlayerAttributeType.Shield or
        EPlayerAttributeType.Burn or
        EPlayerAttributeType.Poison or
        EPlayerAttributeType.HealthRegen or
        EPlayerAttributeType.Rage or
        EPlayerAttributeType.RageMax or
        EPlayerAttributeType.Enraged or
        EPlayerAttributeType.EnragedDuration or
        EPlayerAttributeType.EnragedDurationMax or
        EPlayerAttributeType.Tempo or
        EPlayerAttributeType.TempoGainCooldownMax or
        EPlayerAttributeType.Joy;

    private static void HandleCardUpgradedEvent(
        CombatSim sim,
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        HashSet<(int TimeMs, string CardInstanceId, string ChainKey)> passiveUseRecorded,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
    {
        EmitCardTargetedEffect(sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardUpgrade, passiveUseRecorded, vfxKeyMap, vfxKeys);

        string? tgtKey = !string.IsNullOrEmpty(ev.TargetCardInstanceId) ? ev.TargetCardInstanceId : ev.SourceCardInstanceId;
        if (!TryResolveCard(cardEntryByCoreId, tgtKey, out var tgtMeta)) return;

        if (TryParseTier(ev.Detail, out var newTier))
        {
            var cardId = new InstanceId(tgtMeta.Entry.InstanceId);
            var cu = GetOrCreateCardUpdate(frame, cardId);
            cu.Tier = newTier;
            frame.CardUpdates[cardId] = cu;
        }
    }

    private static void HandleCardEnchantedEvent(
        CombatSim sim,
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        HashSet<(int TimeMs, string CardInstanceId, string ChainKey)> passiveUseRecorded,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
    {
        // P2-10：同时识别结构化 ev.IsReverted 与 "Reverted:" 明细前缀，准确透传至 CombatSimEventCardEnchanted.IsReverted
        bool isReverted = ev.IsReverted
            || (ev.Detail != null && ev.Detail.StartsWith("Reverted:", StringComparison.OrdinalIgnoreCase));
        EmitCardTargetedEffect(
            sim, frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId,
            isReverted ? EActionCommandType.CardEnchantRemove : EActionCommandType.CardEnchant,
            passiveUseRecorded, vfxKeyMap, vfxKeys);

        string? tgtKey = !string.IsNullOrEmpty(ev.TargetCardInstanceId) ? ev.TargetCardInstanceId : ev.SourceCardInstanceId;
        if (!TryResolveCard(cardEntryByCoreId, tgtKey, out var tgtMeta)) return;

        if (TryParseEnchantmentDetail(ev.Detail, out var enchType, out var restoredEnch))
        {
            var cardId = new InstanceId(tgtMeta.Entry.InstanceId);
            if (!isReverted)
            {
                tgtMeta.Entry.Enchantment = enchType;
                var cu = GetOrCreateCardUpdate(frame, cardId);
                cu.Enchantment = enchType;
                frame.CardUpdates[cardId] = cu;
            }
            else
            {
                // D2-P1-05：附魔撤回时同步清除或恢复内部 Entry.Enchantment，防止后续帧按旧附魔查错 VFX
                tgtMeta.Entry.Enchantment = restoredEnch;
                if (restoredEnch.HasValue)
                {
                    var cu = GetOrCreateCardUpdate(frame, cardId);
                    cu.Enchantment = restoredEnch.Value;
                    frame.CardUpdates[cardId] = cu;
                }
            }

            frame.Events.Add(new CombatSimEventCardEnchanted
            {
                InstanceId = tgtMeta.Entry.InstanceId,
                EnchantmentType = enchType,
                IsReverted = isReverted,
            });
        }
    }

    private static void HandleCardAttributeDeletedEvent(
        CombatSimFrame frame,
        CombatEvent ev,
        Dictionary<string, CardRenderState> cardEntryByCoreId)
    {
        if (string.IsNullOrWhiteSpace(ev.Detail)) return;
        if (!Enum.TryParse<ECardAttributeType>(ev.Detail.Trim(), ignoreCase: true, out var attrType)) return;
        string? tgtKey = !string.IsNullOrEmpty(ev.TargetCardInstanceId) ? ev.TargetCardInstanceId : ev.SourceCardInstanceId;
        if (!TryResolveCard(cardEntryByCoreId, tgtKey, out var tgtMeta)) return;

        tgtMeta.PrevAttributes.Remove(attrType);
        frame.Events.Add(new CombatSimEventCardAttributeDeleted
        {
            InstanceId = new InstanceId(tgtMeta.Entry.InstanceId),
            AttributeType = attrType,
        });
    }

    private static void HandleAuraExecutedEvent(
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
    {
        var appliedTo = new HashSet<IEffectTarget>();
        var removedFrom = new HashSet<IEffectTarget>();

        void AddCardTargets(IReadOnlyList<string>? ids, HashSet<IEffectTarget> sink)
        {
            if (ids == null) return;
            for (int i = 0; i < ids.Count; i++)
            {
                var protoId = TryResolveCard(cardEntryByCoreId, ids[i], out var meta)
                    ? meta.Entry.InstanceId
                    : ids[i];
                if (!string.IsNullOrEmpty(protoId))
                    sink.Add(new EffectTargetCard { Target = new InstanceId(protoId) });
            }
        }

        void AddPlayerTargets(IReadOnlyList<string>? ids, HashSet<IEffectTarget> sink)
        {
            if (ids == null) return;
            for (int i = 0; i < ids.Count; i++)
                sink.Add(new EffectTargetPlayer { Target = ParseCombatantSide(ids[i]) });
        }

        AddCardTargets(ev.AuraAppliedCardIds, appliedTo);
        AddPlayerTargets(ev.AuraAppliedPlayerIds, appliedTo);
        AddCardTargets(ev.AuraRemovedCardIds, removedFrom);
        AddPlayerTargets(ev.AuraRemovedPlayerIds, removedFrom);

        if (appliedTo.Count == 0 && removedFrom.Count == 0) return;

        bool hasSrcMeta = TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var srcMeta);
        InstanceId? srcId = hasSrcMeta ? new InstanceId(srcMeta.Entry.InstanceId) : TryResolveInstanceId(cardEntryByCoreId, ev.SourceCardInstanceId);
        InstanceId? trigId = TryResolveInstanceId(cardEntryByCoreId, ev.TriggerSourceCardInstanceId) ?? srcId;
        string auraId = ev.AbilityId ?? string.Empty;

        // P2-01 / P0-19：同步检索 AuraDefinition.VFXConfig.VFXOverrideKey（仅查当前持有附魔 + 官方白名单校验）并预注册至 CombatSim.VfxKeys
        if (hasSrcMeta)
        {
            var srcTpl = ResolveTemplateForCard(repo, srcMeta);
            RegisterAuraVfxKeyIfAny(repo, srcTpl, auraId, srcMeta.Entry.Enchantment, vfxKeyMap, vfxKeys, ev.VfxKeyOverride);
        }
        else if (!string.IsNullOrWhiteSpace(ev.VfxKeyOverride))
        {
            RegisterAuraVfxKeyIfAny(repo, null, auraId, null, vfxKeyMap, vfxKeys, ev.VfxKeyOverride);
        }

        frame.Events.Add(new CombatSimEventEffectAuraExecuted
        {
            ExecutionContextId = ResolveExecutionContextId(ev, frameIndex, eventSeq, "aura"),
            EffectId = auraId,
            Source = srcId,
            TriggerSource = trigId,
            AppliedTo = appliedTo,
            RemovedFrom = removedFrom,
        });
    }

    private static void HandleCardTransformRevertedEvent(
        CombatSimFrame frame,
        CombatEvent ev,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        Func<string, Guid?>? resolveWireTemplateGuid)
    {
        string? origKey = !string.IsNullOrEmpty(ev.TargetCardInstanceId) ? ev.TargetCardInstanceId : ev.SourceCardInstanceId;
        if (!TryResolveCard(cardEntryByCoreId, origKey, out var origMeta)) return;

        var transformedIds = new List<string>();
        if (ev.RevertedTransformedInstanceIds != null)
        {
            for (int i = 0; i < ev.RevertedTransformedInstanceIds.Count; i++)
            {
                string rawId = ev.RevertedTransformedInstanceIds[i];
                string protoId = TryResolveCard(cardEntryByCoreId, rawId, out var repMeta)
                    ? repMeta.Entry.InstanceId
                    : rawId;
                if (!string.IsNullOrEmpty(protoId))
                {
                    transformedIds.Add(protoId);
                    var repInstId = new InstanceId(protoId);
                    var repCu = GetOrCreateCardUpdate(frame, repInstId);
                    repCu.State = new CombatSimCardStateUpdate
                    {
                        PreviousValue = ECardState.Alive,
                        CurrentValue = ECardState.Disabled,
                    };
                    frame.CardUpdates[repInstId] = repCu;
                }
            }
        }

        var origInstId = new InstanceId(origMeta.Entry.InstanceId);
        var origUpdate = GetOrCreateCardUpdate(frame, origInstId);
        origUpdate.State = new CombatSimCardStateUpdate
        {
            PreviousValue = ECardState.Disabled,
            CurrentValue = ECardState.Alive,
        };
        origUpdate.Placement = new CardDeltaPlacement
        {
            Owner = origMeta.Owner,
            Section = origMeta.Entry.Section,
            Socket = origMeta.Entry.Socket,
        };
        origMeta.PrevDestroyed = false;
        frame.CardUpdates[origInstId] = origUpdate;

        string origCoreTemplateId = !string.IsNullOrWhiteSpace(ev.Detail)
            ? ev.Detail.Trim()
            : ResolveCoreTemplateId(origMeta);
        var origTpl = repo.GetCard(origCoreTemplateId) ?? ResolveTemplateForCard(repo, origMeta);
        origMeta.CoreTemplateId = origCoreTemplateId;
        Guid origWireGuid = ResolveSafeWireTemplateGuid(origCoreTemplateId, origTpl, origMeta.Entry.TemplateId, repo, resolveWireTemplateGuid);
        string origWireTemplateStr = origWireGuid != Guid.Empty ? origWireGuid.ToString() : origMeta.Entry.TemplateId.ToString();
        ECardType origType = ParseCoreCardType(origTpl?.Type, origMeta.Entry.Type);

        frame.Events.Add(new CombatSimEventCardTransformReverted(
            new SimEventCardTransformation(
                origMeta.Entry.InstanceId,
                origWireTemplateStr,
                origType,
                origMeta.Owner,
                origMeta.Entry.Section,
                origMeta.Entry.Socket),
            transformedIds));
    }

    private static void HandleCardTransformedEvent(
        CombatSim sim,
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        bool isLastFrame,
        CombatTickState? curSnap,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        HashSet<(int TimeMs, string CardInstanceId, string ChainKey)> passiveUseRecorded,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys,
        Func<string, Guid?>? resolveWireTemplateGuid,
        Action<string, string, Guid>? onRuntimeCardTransformed)
    {
        TryParseTransformDetail(ev.Detail, out string? detailOrigOrNewId, out string? newTemplateId, out bool isArrowFormat);

        CardRenderState origMeta;
        if (isArrowFormat && TryResolveCard(cardEntryByCoreId, detailOrigOrNewId, out var arrowOrig))
        {
            origMeta = arrowOrig;
        }
        else if (TryResolveCard(cardEntryByCoreId, ev.TargetCardInstanceId, out var tgtOrig))
        {
            origMeta = tgtOrig;
        }
        else if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var srcOrig))
        {
            origMeta = srcOrig;
        }
        else
        {
            return;
        }

        string execCtxId = ResolveExecutionContextId(ev, frameIndex, eventSeq);
        string origInstStr = origMeta.Entry.InstanceId;
        var origInstId = new InstanceId(origInstStr);

        // 先构造与变换事件同 ContextId 的 EffectExecuted 弹道（供 TransformManager 提取播动画）
        var srcMeta = TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var sm) ? sm : origMeta;
        var srcTpl = ResolveTemplateForCard(repo, srcMeta);
        string effectId = ResolveEffectId(ev, srcTpl);
        IEffectTarget target = new EffectTargetCard { Target = origInstId };
        frame.Events.Add(new CombatSimEventEffectExecuted
        {
            ExecutionContextId = execCtxId,
            EffectId = effectId,
            ActionType = EActionCommandType.CardTransform,
            Source = new InstanceId(srcMeta.Entry.InstanceId),
            TriggerSource = ResolveTriggerSourceId(ev, srcMeta, cardEntryByCoreId),
            Target = target,
            VfxIndex = ResolveVfxIndex(repo, srcTpl, effectId, srcMeta.Entry.Enchantment, vfxKeyMap, vfxKeys, ev.VfxKeyOverride),
        });
        TryRecordPassiveUseCount(sim, ev, srcMeta, passiveUseRecorded);

        if (string.IsNullOrWhiteSpace(newTemplateId))
        {
            return;
        }

        // P0-19：放行非 GUID 自定义模板 ID，通过安全 Wire GUID 映射表/官方同尺寸借壳生成合法 Wire GUID 并触发回调
        string newCoreTemplateId = newTemplateId.Trim();
        var newTpl = repo.GetCard(newCoreTemplateId);
        Guid newWireTemplateGuid = ResolveSafeWireTemplateGuid(newCoreTemplateId, newTpl, origMeta.Entry.TemplateId, repo, resolveWireTemplateGuid);
        if (newWireTemplateGuid == Guid.Empty)
        {
            return;
        }
        bool isNativeGuid = Guid.TryParse(newCoreTemplateId, out var parsedNative) && parsedNative != Guid.Empty;
        newTpl ??= repo.GetCard(newWireTemplateGuid.ToString());

        // CombatSimHandler 严格要求 frame.CardUpdates 同时包含原卡和新卡条目，否则抛 KeyNotFoundException
        var origUpdate = GetOrCreateCardUpdate(frame, origInstId);
        origUpdate.State ??= new CombatSimCardStateUpdate
        {
            PreviousValue = origMeta.PrevDestroyed ? ECardState.Disabled : ECardState.Alive,
            CurrentValue = ECardState.Disabled,
        };
        origMeta.PrevDestroyed = true;
        frame.CardUpdates[origInstId] = origUpdate;

        string? coreReplacementId = null;
        if (isArrowFormat && !string.IsNullOrEmpty(ev.TargetCardInstanceId) &&
            !string.Equals(ev.TargetCardInstanceId, origMeta.Entry.InstanceId, StringComparison.Ordinal) &&
            !string.Equals(ev.TargetCardInstanceId, origMeta.Entry.CoreCard?.InstanceId, StringComparison.Ordinal))
        {
            coreReplacementId = ev.TargetCardInstanceId;
        }
        else if (!isArrowFormat && !string.IsNullOrEmpty(detailOrigOrNewId))
        {
            coreReplacementId = detailOrigOrNewId;
        }

        string newProtoInstId = !string.IsNullOrEmpty(coreReplacementId) ? coreReplacementId : $"trn_{frameIndex}_{eventSeq}";
        ECardType newCardType = ParseCoreCardType(newTpl?.Type, origMeta.Entry.Type);
        ECardSize newSize = newTpl != null ? ParseCoreCardSize(newTpl.Size) : origMeta.Entry.Size;
        ETier newTier = origMeta.Entry.Tier;
        if (newTpl != null &&
            !string.IsNullOrWhiteSpace(newTpl.StartingTier) &&
            Enum.TryParse<ETier>(newTpl.StartingTier, ignoreCase: true, out var minStartTier) &&
            newTier < minStartTier)
        {
            newTier = minStartTier;
        }

        var newEntry = new RunState.CardEntry
        {
            InstanceId = newProtoInstId,
            TemplateId = newWireTemplateGuid,
            CustomCardId = isNativeGuid ? null : newCoreTemplateId,
            Type = newCardType,
            Size = newSize,
            Tier = newTier,
            Enchantment = origMeta.Entry.Enchantment,
            Owner = origMeta.Owner,
            Section = origMeta.Entry.Section,
            Socket = origMeta.Entry.Socket,
        };
        PopulateTemplateAttributesForTier(newEntry.Attributes, newTpl, newTier);

        var newState = new CardRenderState
        {
            Entry = newEntry,
            CoreTemplateId = newCoreTemplateId,
            Owner = origMeta.Owner,
            IsRuntimeTransformed = true,
        };
        foreach (var kv in newEntry.Attributes)
        {
            newState.InitialAttributes[kv.Key] = kv.Value;
            newState.PrevAttributes[kv.Key] = kv.Value;
        }

        cardEntryByCoreId[newProtoInstId] = newState;
        if (!string.IsNullOrEmpty(coreReplacementId))
        {
            cardEntryByCoreId[coreReplacementId] = newState;
        }

        // P0-19：通知上层（如 RunEngine / RunModContext）为新生成的战中变形实例回写临时视觉绑定
        onRuntimeCardTransformed?.Invoke(newProtoInstId, newCoreTemplateId, newWireTemplateGuid);

        var newInstIdObj = new InstanceId(newProtoInstId);
        var newCardUpdate = GetOrCreateCardUpdate(frame, newInstIdObj);
        newCardUpdate.Size = newSize;
        newCardUpdate.Tier = newTier;
        newCardUpdate.Enchantment = newEntry.Enchantment;
        // P1-12：初始化战中变形新卡的 Tags / HiddenTags / Heroes
        if (newTpl != null)
        {
            var newTags = ParseEnumSet<ECardTag>(newTpl.Tags);
            var newHidden = ParseEnumSet<EHiddenTag>(newTpl.HiddenTags);
            var newHeroes = ParseEnumSet<EHero>(newTpl.Heroes);
            if (newTags.Count > 0)
            {
                newCardUpdate.Tags = newTags;
                foreach (var t in newTags) newState.PrevTags.Add(t);
            }
            if (newHidden.Count > 0)
            {
                newCardUpdate.HiddenTags = newHidden;
                foreach (var ht in newHidden) newState.PrevHiddenTags.Add(ht);
            }
            if (newHeroes.Count > 0)
            {
                newCardUpdate.Heroes = newHeroes;
                foreach (var h in newHeroes) newState.PrevHeroes.Add(h);
            }
        }
        newCardUpdate.Placement = new CardDeltaPlacement
        {
            Owner = origMeta.Owner,
            Section = origMeta.Entry.Section,
            Socket = origMeta.Entry.Socket,
        };
        foreach (var kv in newEntry.Attributes)
        {
            newCardUpdate.Attributes[kv.Key] = new CombatSimCardAttributeUpdate
            {
                AttributeType = kv.Key,
                PreviousValue = 0,
                CurrentValue = kv.Value,
            };
        }
        frame.CardUpdates[newInstIdObj] = newCardUpdate;

        // P1-08（RC-COMBAT-TRANSFORM-FRAME-ORDER）：
        // 因同一帧内先执行了全盘 ApplyCardFrameDiff 后才执行本变形事件注册 newState，
        // 必须在此立即按当帧快照 curSnap.Cards 对新变形卡补跑一次完整属性快照 diff（含实时光环计算后的
        // PoisonApplyAmount / Cooldown / CooldownMax / Multicast / SellPrice / CustomAttrs / Tags 等），
        // 彻底消除 Virus 等战中变形新卡首帧毒伤与冷却归 0。
        CardCooldownFrame? transformedCf = FindCardFrameForTransformedCard(curSnap, newProtoInstId, coreReplacementId);
        if (transformedCf != null)
        {
            ApplyCardFrameDiff(
                frame,
                newState,
                transformedCf,
                frameIndex,
                isFirstFrame: true,
                isLastFrame: isLastFrame,
                firedThisFrame: false);
        }
        else if (newEntry.Attributes.TryGetValue(ECardAttributeType.CooldownMax, out int fallbackCdMax)
                 && fallbackCdMax > 0
                 && !newCardUpdate.Attributes.ContainsKey(ECardAttributeType.Cooldown))
        {
            // 无快照兜底路径（如精简合成时间轴）：至少补齐满额初始 Cooldown，避免新变形卡首帧缺冷却进度条
            newCardUpdate.Attributes[ECardAttributeType.Cooldown] = new CombatSimCardAttributeUpdate
            {
                AttributeType = ECardAttributeType.Cooldown,
                PreviousValue = fallbackCdMax,
                CurrentValue = fallbackCdMax,
            };
            newState.PrevAttributes[ECardAttributeType.Cooldown] = fallbackCdMax;
            newState.LastEmittedCdFrame = frameIndex;
            newState.LastEmittedCdValue = fallbackCdMax;
        }

        frame.Events.Add(new CombatSimEventCardTransformed(
            execCtxId,
            origInstStr,
            new List<SimEventCardTransformation>
            {
                new(
                    newProtoInstId,
                    newWireTemplateGuid.ToString(),
                    newCardType,
                    origMeta.Owner,
                    origMeta.Entry.Section,
                    origMeta.Entry.Socket),
            }));
    }

    private static CardCooldownFrame? FindCardFrameForTransformedCard(
        CombatTickState? curSnap,
        string newProtoInstId,
        string? coreReplacementId)
    {
        if (curSnap?.Cards == null || curSnap.Cards.Count == 0)
            return null;

        for (int i = 0; i < curSnap.Cards.Count; i++)
        {
            var cf = curSnap.Cards[i];
            if (string.Equals(cf.InstanceId, newProtoInstId, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(coreReplacementId)
                    && string.Equals(cf.InstanceId, coreReplacementId, StringComparison.Ordinal)))
            {
                return cf;
            }
        }
        return null;
    }

    private static void HandleQuestUpdatedEvent(
        CombatSimFrame frame,
        CombatEvent ev,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        Dictionary<(string CardId, int GroupIdx, int EntryIdx), int> prevQuestProgress)
    {
        string? cardKey = !string.IsNullOrEmpty(ev.TargetCardInstanceId) ? ev.TargetCardInstanceId : ev.SourceCardInstanceId;
        if (!TryResolveCard(cardEntryByCoreId, cardKey, out var state)) return;

        var tpl = ResolveTemplateForCard(repo, state);
        var (groupIdx, entryIdx, questAttrType, parsedOld, parsedNew) = ResolveQuestLocation(ev.Detail, tpl, (int)Math.Round(ev.Value));
        var progressKey = (state.Entry.InstanceId, groupIdx, entryIdx);
        int oldVal = parsedOld ?? prevQuestProgress.GetValueOrDefault(progressKey, 0);
        int newVal = parsedNew ?? Math.Max(oldVal, (int)Math.Round(ev.Value));
        prevQuestProgress[progressKey] = newVal;

        // 同步更新卡面 Quest_N 属性，确保客户端 Tooltip 与完成判定读取到最新进度（并记为持久属性，战末不误清）
        state.RuntimePersistentAttributes.Add(questAttrType);
        var cardId = new InstanceId(state.Entry.InstanceId);
        CombatSimCardUpdate? cu = GetOrCreateCardUpdate(frame, cardId);
        bool hasAttrUpdate = true;
        EmitAttrIfChangedLazy(state, cardId, ref cu, questAttrType, newVal, ref hasAttrUpdate);
        if (cu != null && cu.Attributes.Count > 0)
            frame.CardUpdates[cardId] = cu;

        frame.Events.Add(new CombatSimEventCardQuestUpdated(
            state.Entry.InstanceId,
            groupIdx,
            entryIdx,
            oldVal,
            newVal));
    }

    private static void HandleQuestCompletedEvent(
        CombatSimFrame frame,
        CombatEvent ev,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        Dictionary<(string CardId, int GroupIdx, int EntryIdx), int> prevQuestProgress,
        Dictionary<string, int> questCompletedCount)
    {
        string? cardKey = !string.IsNullOrEmpty(ev.TargetCardInstanceId) ? ev.TargetCardInstanceId : ev.SourceCardInstanceId;
        if (!TryResolveCard(cardEntryByCoreId, cardKey, out var state)) return;

        var tpl = ResolveTemplateForCard(repo, state);
        var (groupIdx, entryIdx, questAttrType, _, parsedNew) = ResolveQuestLocation(ev.Detail, tpl, (int)Math.Round(ev.Value));

        // 若模板定义了任务目标值，确保在 CombatSimEventCardQuestCompleted 之前把 Quest_N 与 QuestCompletedCount 推进到目标值
        int targetProgress = parsedNew ?? 0;
        if (tpl?.Quests != null &&
            groupIdx >= 0 && groupIdx < tpl.Quests.Count &&
            entryIdx >= 0 && entryIdx < tpl.Quests[groupIdx].Entries.Count)
        {
            int tplTarget = (int)Math.Ceiling(tpl.Quests[groupIdx].Entries[entryIdx].Target);
            targetProgress = Math.Max(targetProgress, tplTarget);
        }

        var progressKey = (state.Entry.InstanceId, groupIdx, entryIdx);
        int prevVal = prevQuestProgress.GetValueOrDefault(progressKey, 0);
        int finalVal = Math.Max(prevVal, targetProgress);
        prevQuestProgress[progressKey] = finalVal;

        int nextCompleted = questCompletedCount.GetValueOrDefault(state.Entry.InstanceId, 0) + 1;
        questCompletedCount[state.Entry.InstanceId] = nextCompleted;

        state.RuntimePersistentAttributes.Add(questAttrType);
        state.RuntimePersistentAttributes.Add(ECardAttributeType.QuestCompletedCount);
        var cardId = new InstanceId(state.Entry.InstanceId);
        CombatSimCardUpdate? cu = GetOrCreateCardUpdate(frame, cardId);
        bool hasAttrUpdate = true;
        if (finalVal > 0)
            EmitAttrIfChangedLazy(state, cardId, ref cu, questAttrType, finalVal, ref hasAttrUpdate);
        EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.QuestCompletedCount, nextCompleted, ref hasAttrUpdate);
        if (cu != null && cu.Attributes.Count > 0)
            frame.CardUpdates[cardId] = cu;

        frame.Events.Add(new CombatSimEventCardQuestCompleted(
            state.Entry.InstanceId,
            groupIdx,
            entryIdx));
    }

    private static RewardThresholdTracker BuildRewardTracker(
        CombatSim sim,
        int oppMaxHp,
        int oppRemHp,
        int pveGoldReceived,
        int pveXpReceived)
    {
        var tracker = new RewardThresholdTracker();
        float remainingRatio = Math.Clamp(oppRemHp / (float)Math.Max(1, oppMaxHp), 0f, 1f);

        void Populate(int rewardAmount, List<float> simThresholds, List<(float Percent, int ThresholdHp, bool Emitted)> list)
        {
            if (rewardAmount <= 0) return;

            int markerCount = Math.Clamp(rewardAmount, 1, 4);
            var candidates = new List<float>();

            if (remainingRatio >= 0.999f)
            {
                // 极罕见零伤却有兜底奖励的场景：放一个 1.0f 终点刻度保证视觉与事件一致
                candidates.Add(1.0f);
            }
            else
            {
                // 按造成伤害的区间均匀布点，保证至少 1 个阈值落在 [remainingRatio, 1.0) 内，且胜场最后点落在 0.0f
                float span = 1.0f - remainingRatio;
                for (int i = 1; i <= markerCount; i++)
                {
                    float pct = i == markerCount && remainingRatio <= 0.001f
                        ? 0.0f
                        : MathF.Round(1.0f - (span * i / markerCount), 2);
                    if (pct < remainingRatio) pct = MathF.Round(remainingRatio, 2);
                    if (!candidates.Any(c => MathF.Abs(c - pct) < 0.01f))
                        candidates.Add(pct);
                }
            }

            // 按从高到低排序（战斗中敌方血量从高往下扣，依次经过每个阈值）
            candidates.Sort((a, b) => b.CompareTo(a));
            foreach (float pct in candidates)
            {
                simThresholds.Add(pct);
                int thresholdHp = (int)MathF.Ceiling(pct * oppMaxHp);
                list.Add((pct, thresholdHp, false));
            }
        }

        Populate(pveGoldReceived, sim.OpponentHealthThresholdsForGold, tracker.GoldMarkers);
        Populate(pveXpReceived, sim.OpponentHealthThresholdsForXp, tracker.XpMarkers);
        return tracker;
    }

    private static void EmitCrossedMonsterRewards(
        CombatSimFrame frame,
        RewardThresholdTracker tracker,
        int currentOppHp,
        bool forceFlushRemaining)
    {
        for (int i = 0; i < tracker.GoldMarkers.Count; i++)
        {
            var m = tracker.GoldMarkers[i];
            if (m.Emitted) continue;
            if (forceFlushRemaining || currentOppHp <= m.ThresholdHp)
            {
                tracker.GoldMarkers[i] = (m.Percent, m.ThresholdHp, true);
                frame.Events.Add(new CombatSimEventMonsterGoldReceived { HealthAmount = m.ThresholdHp });
            }
        }

        for (int i = 0; i < tracker.XpMarkers.Count; i++)
        {
            var m = tracker.XpMarkers[i];
            if (m.Emitted) continue;
            if (forceFlushRemaining || currentOppHp <= m.ThresholdHp)
            {
                tracker.XpMarkers[i] = (m.Percent, m.ThresholdHp, true);
                frame.Events.Add(new CombatSimEventMonsterXpReceived { HealthAmount = m.ThresholdHp });
            }
        }
    }

    private static CombatSimPlayerUpdate? BuildPlayerUpdate(
        CombatSim sim,
        CombatTickState prev,
        CombatTickState cur,
        bool isPlayer,
        string? hero)
    {
        var attrs = new Dictionary<EPlayerAttributeType, CombatSimPlayerAttributeUpdate>();

        void Check(EPlayerAttributeType type, int p, int c)
        {
            if (p != c)
            {
                attrs[type] = new CombatSimPlayerAttributeUpdate
                {
                    AttributeType = type,
                    PreviousValue = p,
                    CurrentValue = c,
                };
            }
        }

        int prevPortraitIdx;
        int curPortraitIdx;
        if (isPlayer)
        {
            Check(EPlayerAttributeType.Health, prev.P1Hp, cur.P1Hp);
            Check(EPlayerAttributeType.HealthMax, prev.P1MaxHp, cur.P1MaxHp);
            Check(EPlayerAttributeType.Shield, prev.P1Shield, cur.P1Shield);
            Check(EPlayerAttributeType.Burn, prev.P1Burn, cur.P1Burn);
            Check(EPlayerAttributeType.Poison, prev.P1Poison, cur.P1Poison);
            Check(EPlayerAttributeType.HealthRegen, prev.P1Regen, cur.P1Regen);
            Check(EPlayerAttributeType.Rage, prev.P1Rage, cur.P1Rage);
            Check(EPlayerAttributeType.RageMax, prev.P1RageMax, cur.P1RageMax);
            Check(EPlayerAttributeType.Enraged, prev.P1Enraged ? 1 : 0, cur.P1Enraged ? 1 : 0);
            Check(EPlayerAttributeType.EnragedDuration, prev.P1EnragedDuration, cur.P1EnragedDuration);
            Check(EPlayerAttributeType.EnragedDurationMax, prev.P1EnragedDurationMax, cur.P1EnragedDurationMax);
            Check(EPlayerAttributeType.Tempo, prev.P1Tempo, cur.P1Tempo);
            Check(EPlayerAttributeType.TempoGainCooldownMax, prev.P1TempoGainCooldownMax, cur.P1TempoGainCooldownMax);
            Check(EPlayerAttributeType.Joy, prev.P1Joy, cur.P1Joy);
            prevPortraitIdx = prev.P1PortraitIndex;
            curPortraitIdx = cur.P1PortraitIndex;
        }
        else
        {
            Check(EPlayerAttributeType.Health, prev.P2Hp, cur.P2Hp);
            Check(EPlayerAttributeType.HealthMax, prev.P2MaxHp, cur.P2MaxHp);
            Check(EPlayerAttributeType.Shield, prev.P2Shield, cur.P2Shield);
            Check(EPlayerAttributeType.Burn, prev.P2Burn, cur.P2Burn);
            Check(EPlayerAttributeType.Poison, prev.P2Poison, cur.P2Poison);
            Check(EPlayerAttributeType.HealthRegen, prev.P2Regen, cur.P2Regen);
            Check(EPlayerAttributeType.Rage, prev.P2Rage, cur.P2Rage);
            Check(EPlayerAttributeType.RageMax, prev.P2RageMax, cur.P2RageMax);
            Check(EPlayerAttributeType.Enraged, prev.P2Enraged ? 1 : 0, cur.P2Enraged ? 1 : 0);
            Check(EPlayerAttributeType.EnragedDuration, prev.P2EnragedDuration, cur.P2EnragedDuration);
            Check(EPlayerAttributeType.EnragedDurationMax, prev.P2EnragedDurationMax, cur.P2EnragedDurationMax);
            Check(EPlayerAttributeType.Tempo, prev.P2Tempo, cur.P2Tempo);
            Check(EPlayerAttributeType.TempoGainCooldownMax, prev.P2TempoGainCooldownMax, cur.P2TempoGainCooldownMax);
            Check(EPlayerAttributeType.Joy, prev.P2Joy, cur.P2Joy);
            prevPortraitIdx = prev.P2PortraitIndex;
            curPortraitIdx = cur.P2PortraitIndex;
        }

        // P0-11：追踪狂暴变身与复位立绘变更，通过 sim.CachePortrait 写入 CombatSim.PortraitKeys 索引
        CombatSimPlayerPortraitUpdate? portrait = null;
        if (prevPortraitIdx != curPortraitIdx)
        {
            if (curPortraitIdx > 0)
            {
                string? artKey = ResolveAlternatePortraitArtKey(hero, curPortraitIdx);
                if (!string.IsNullOrEmpty(artKey))
                {
                    int? cachedIdx = sim.CachePortrait(artKey);
                    if (cachedIdx.HasValue)
                        portrait = new CombatSimPlayerPortraitUpdate { Index = cachedIdx.Value };
                }
            }
            else if (ResolveAlternatePortraitArtKey(hero, prevPortraitIdx) != null)
            {
                portrait = new CombatSimPlayerPortraitUpdate { Index = null };
            }
        }

        if (attrs.Count == 0 && portrait == null) return null;
        return new CombatSimPlayerUpdate
        {
            Attributes = attrs,
            Portrait = portrait,
        };
    }

    private static string? ResolveAlternatePortraitArtKey(string? hero, int portraitIndex)
    {
        if (portraitIndex <= 0) return null;
        if (string.Equals(hero, HeroNames.Karnok, StringComparison.OrdinalIgnoreCase))
            return KarnokBeastFormPortraitArtKey;
        return null;
    }

    private static void ApplyExplicitDamageSplit(
        CombatSimFrame frame,
        FrameCombatantState state,
        ECombatantId targetSide,
        EDamageType dmgType,
        int shieldAbsorbed,
        int hpLost,
        bool isCrit)
    {
        int absorbed = Math.Max(0, shieldAbsorbed);
        int hpDmg = Math.Max(0, hpLost);
        if (absorbed <= 0 && hpDmg <= 0) return;

        if (targetSide == ECombatantId.Player)
        {
            state.P1Shield = Math.Max(0, state.P1Shield - absorbed);
            state.P1Hp -= hpDmg;
        }
        else
        {
            state.P2Shield = Math.Max(0, state.P2Shield - absorbed);
            state.P2Hp -= hpDmg;
        }

        if (absorbed > 0)
            AddHealthAdj(frame, targetSide, dmgType, EPlayerHealthChangeType.Shield, -absorbed, isCrit);
        if (hpDmg > 0)
            AddHealthAdj(frame, targetSide, dmgType, EPlayerHealthChangeType.Health, -hpDmg, isCrit);
    }

    private static InstanceId ResolveTriggerSourceId(
        CombatEvent ev,
        CardRenderState fallbackSource,
        Dictionary<string, CardRenderState> map)
    {
        if (TryResolveCard(map, ev.TriggerSourceCardInstanceId, out var trigMeta))
            return new InstanceId(trigMeta.Entry.InstanceId);
        return new InstanceId(fallbackSource.Entry.InstanceId);
    }

    private static InstanceId? TryResolveInstanceId(
        Dictionary<string, CardRenderState> map,
        string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return null;
        if (TryResolveCard(map, instanceId, out var meta))
            return new InstanceId(meta.Entry.InstanceId);
        return new InstanceId(instanceId);
    }

    private static HashSet<TEnum> ParseEnumSet<TEnum>(IEnumerable<string>? values) where TEnum : struct, Enum
    {
        var set = new HashSet<TEnum>();
        PopulateParsedEnumSet(values, set);
        return set;
    }

    private static void PopulateParsedEnumSet<TEnum>(IEnumerable<string>? values, HashSet<TEnum> sink) where TEnum : struct, Enum
    {
        if (values == null) return;
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Enum.TryParse<TEnum>(raw.Trim(), ignoreCase: true, out var parsed))
                sink.Add(parsed);
        }
    }

    private static void ApplyDamageWithShieldSplit(
        CombatSimFrame frame,
        FrameCombatantState state,
        ECombatantId targetSide,
        EDamageType dmgType,
        int amount,
        bool isCrit)
    {
        if (amount <= 0) return;

        int shield = targetSide == ECombatantId.Player ? state.P1Shield : state.P2Shield;
        int hp = targetSide == ECombatantId.Player ? state.P1Hp : state.P2Hp;

        int shieldAbsorbed = Math.Min(shield, amount);
        int hpLost = amount - shieldAbsorbed;

        shield -= shieldAbsorbed;
        // 对齐官方负血击杀视觉（HealthBarController.cs:781 原生支持显示负血数字如 -420），不再强制钳制在 0
        hp -= hpLost;

        if (targetSide == ECombatantId.Player)
        {
            state.P1Shield = shield;
            state.P1Hp = hp;
        }
        else
        {
            state.P2Shield = shield;
            state.P2Hp = hp;
        }

        if (shieldAbsorbed > 0)
            AddHealthAdj(frame, targetSide, dmgType, EPlayerHealthChangeType.Shield, -shieldAbsorbed, isCrit);
        if (hpLost > 0)
            AddHealthAdj(frame, targetSide, dmgType, EPlayerHealthChangeType.Health, -hpLost, isCrit);
    }

    private static void EnsureHealthAttributeSynced(CombatSimPlayerUpdate? update, int prevHp, int curHp)
    {
        if (update == null || update.HealthAdjustments.Count == 0) return;
        if (update.Attributes.ContainsKey(EPlayerAttributeType.Health)) return;

        int netAdj = 0;
        for (int i = 0; i < update.HealthAdjustments.Count; i++)
            netAdj += update.HealthAdjustments[i].Amount;

        if (netAdj != curHp - prevHp)
        {
            update.Attributes[EPlayerAttributeType.Health] = new CombatSimPlayerAttributeUpdate
            {
                AttributeType = EPlayerAttributeType.Health,
                PreviousValue = prevHp,
                CurrentValue = curHp,
            };
        }
    }

    private static void AddHealthAdj(
        CombatSimFrame frame,
        ECombatantId target,
        EDamageType dmgType,
        EPlayerHealthChangeType changeType,
        int amount,
        bool isCrit)
    {
        var adj = new CombatSimPlayerHealthAdjustment
        {
            DamageType = dmgType,
            AttributeChanged = changeType,
            Amount = amount,
            IsCrit = isCrit,
            IsDamageReduced = false,
        };
        if (target == ECombatantId.Player)
        {
            frame.PlayerUpdates ??= new CombatSimPlayerUpdate();
            frame.PlayerUpdates.HealthAdjustments.Add(adj);
        }
        else
        {
            frame.OpponentUpdates ??= new CombatSimPlayerUpdate();
            frame.OpponentUpdates.HealthAdjustments.Add(adj);
        }
    }

    private static bool TryResolveCard(
        Dictionary<string, CardRenderState> map,
        string? instanceId,
        out CardRenderState state)
    {
        if (!string.IsNullOrEmpty(instanceId) && map.TryGetValue(instanceId, out state!))
            return true;
        state = null!;
        return false;
    }

    private static ECombatantId ParseCombatantSide(string? playerId)
        => string.Equals(playerId, "player", StringComparison.OrdinalIgnoreCase)
            ? ECombatantId.Player
            : ECombatantId.Opponent;

    private static string ResolveEffectId(CombatEvent ev, CardTemplate? tpl)
    {
        if (!string.IsNullOrEmpty(ev.AbilityId)) return ev.AbilityId;
        return tpl?.Abilities.Keys.FirstOrDefault() ?? string.Empty;
    }

    private static string ResolveCoreTemplateId(CardRenderState state)
    {
        if (!string.IsNullOrWhiteSpace(state.CoreTemplateId))
            return state.CoreTemplateId!;
        if (!string.IsNullOrWhiteSpace(state.Entry.CoreCard?.Template?.Id))
            return state.Entry.CoreCard!.Template.Id;
        if (!string.IsNullOrWhiteSpace(state.Entry.CustomCardId))
            return state.Entry.CustomCardId!;
        return state.Entry.TemplateId.ToString();
    }

    private static CardTemplate? ResolveTemplateForCard(GameDataRepository repo, CardRenderState state)
    {
        string coreId = ResolveCoreTemplateId(state);
        return repo.GetCard(coreId) ?? repo.GetCard(state.Entry.TemplateId.ToString());
    }

    private static Guid ResolveSafeWireTemplateGuid(
        string coreTemplateId,
        CardTemplate? tpl,
        Guid fallbackOrigGuid,
        GameDataRepository repo,
        Func<string, Guid?>? resolveWireTemplateGuid)
    {
        if (Guid.TryParse(coreTemplateId, out var directGuid) && directGuid != Guid.Empty)
            return directGuid;

        if (resolveWireTemplateGuid != null)
        {
            var resolved = resolveWireTemplateGuid(coreTemplateId);
            if (resolved.HasValue && resolved.Value != Guid.Empty)
                return resolved.Value;
        }

        // 若目标自定义卡可查到模板，优先按同 Type + Size 匹配官方非自定义模板 GUID 借壳，确保 Unity Addressables 绝不空引用
        if (tpl != null)
        {
            Guid? sameTypeFallback = null;
            foreach (var cand in repo.CardsById.Values)
            {
                if (cand.IsCustomModCard) continue;
                if (!Guid.TryParse(cand.Id, out var candGuid) || candGuid == Guid.Empty) continue;
                if (!string.Equals(cand.Type, tpl.Type, StringComparison.OrdinalIgnoreCase)) continue;
                sameTypeFallback ??= candGuid;
                if (string.Equals(cand.Size, tpl.Size, StringComparison.OrdinalIgnoreCase))
                    return candGuid;
            }
            if (sameTypeFallback.HasValue)
                return sameTypeFallback.Value;
        }

        return fallbackOrigGuid;
    }

    private static HashSet<string> GetValidOfficialVfxKeySet(GameDataRepository repo)
    {
        return OfficialVfxKeyWhitelistCache.GetValue(repo, static r =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in ModRuleCatalog.OfficialVfxPresets)
            {
                string? official = ModRuleCatalog.ResolveOfficialVfxKey(preset.Id);
                if (!string.IsNullOrWhiteSpace(official))
                    set.Add(official.Trim());
            }
            // 补充公开别名表中内置于 ModRuleCatalog 的额外官方键及官方原生飞币特效键（不触碰 ModRuleCatalog.cs）
            set.Add(OfficialCoinBurstVfxKey);
            foreach (string extraAlias in new[] { "FireClaw", "Slash" })
            {
                string? official = ModRuleCatalog.ResolveOfficialVfxKey(extraAlias);
                if (!string.IsNullOrWhiteSpace(official))
                    set.Add(official.Trim());
            }

            foreach (var tpl in r.CardsById.Values)
            {
                if (tpl.IsCustomModCard) continue;
                CollectTemplateOfficialVfxKeys(tpl, set);
            }

            return set;
        });
    }

    private static void CollectTemplateOfficialVfxKeys(CardTemplate tpl, HashSet<string> sink)
    {
        foreach (var ab in tpl.Abilities.Values)
        {
            string? key = ab.VFXConfig?.VFXOverrideKey;
            if (!string.IsNullOrWhiteSpace(key))
                sink.Add(key.Trim());
        }
        foreach (var aura in tpl.Auras.Values)
        {
            string? key = aura.VFXConfig?.VFXOverrideKey;
            if (!string.IsNullOrWhiteSpace(key))
                sink.Add(key.Trim());
        }
        if (tpl.Enchantments != null)
        {
            foreach (var ench in tpl.Enchantments.Values)
            {
                foreach (var ab in ench.Abilities.Values)
                {
                    string? key = ab.VFXConfig?.VFXOverrideKey;
                    if (!string.IsNullOrWhiteSpace(key))
                        sink.Add(key.Trim());
                }
                foreach (var aura in ench.Auras.Values)
                {
                    string? key = aura.VFXConfig?.VFXOverrideKey;
                    if (!string.IsNullOrWhiteSpace(key))
                        sink.Add(key.Trim());
                }
            }
        }
    }

    private static bool TryNormalizeAndValidateOfficialVfxKey(
        string? rawKey,
        GameDataRepository repo,
        out string validKey)
    {
        validKey = string.Empty;
        if (string.IsNullOrWhiteSpace(rawKey))
            return false;

        string candidate = ModRuleCatalog.ResolveOfficialVfxKey(rawKey) ?? rawKey.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var whitelist = GetValidOfficialVfxKeySet(repo);
        if (whitelist.TryGetValue(candidate, out var canonical))
        {
            validKey = canonical;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 从官方 GameData.db 的 Ability.VFXConfig.VFXOverrideKey（全库 523 项技能、131+ 种专属弹道/斩击/激光特效）
    /// 解析当前动作的专属 VFX 键并登记进 <c>CombatSim.VfxKeys</c>，返回客户端 <c>CombatSimHandler.ExtractFrameEffects</c> 所需的 <c>VfxIndex</c>。
    /// P0-19：严禁跨附魔回退遍历，且所有候选键均须经 <see cref="ModRuleCatalog.ResolveOfficialVfxKey"/> 归一化并通过官方白名单校验。
    /// </summary>
    private static int? ResolveVfxIndex(
        GameDataRepository repo,
        CardTemplate? tpl,
        string effectId,
        EEnchantmentType? enchantment,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys,
        string? explicitOverrideKey = null)
    {
        string? candidate = null;
        if (!string.IsNullOrWhiteSpace(explicitOverrideKey))
        {
            candidate = explicitOverrideKey;
        }
        else if (tpl != null && !string.IsNullOrEmpty(effectId))
        {
            if (tpl.Abilities.TryGetValue(effectId, out var ab))
            {
                candidate = ab.VFXConfig?.VFXOverrideKey;
            }

            // 仅查当前卡实际持有的附魔，严禁遍历其它无关附魔回退
            if (string.IsNullOrEmpty(candidate) && enchantment.HasValue && tpl.Enchantments != null)
            {
                string enchName = enchantment.Value.ToString();
                if (tpl.Enchantments.TryGetValue(enchName, out var enchData) &&
                    enchData.Abilities.TryGetValue(effectId, out var enchAb))
                {
                    candidate = enchAb.VFXConfig?.VFXOverrideKey;
                }
            }
        }

        if (!TryNormalizeAndValidateOfficialVfxKey(candidate, repo, out string key))
            return null;

        if (!vfxKeyMap.TryGetValue(key, out int idx))
        {
            idx = vfxKeys.Count;
            vfxKeys.Add(key);
            vfxKeyMap[key] = idx;
        }
        return idx;
    }

    /// <summary>
    /// P2-01 / P0-19：从 AuraDefinition.VFXConfig.VFXOverrideKey（仅查基础光环与当前实际附魔光环）检索官方合法特效键并登记至 CombatSim.VfxKeys。
    /// </summary>
    private static void RegisterAuraVfxKeyIfAny(
        GameDataRepository repo,
        CardTemplate? tpl,
        string auraId,
        EEnchantmentType? enchantment,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys,
        string? explicitOverrideKey = null)
    {
        string? candidate = !string.IsNullOrWhiteSpace(explicitOverrideKey) ? explicitOverrideKey : null;

        if (string.IsNullOrEmpty(candidate) && tpl != null && !string.IsNullOrEmpty(auraId))
        {
            if (tpl.Auras.TryGetValue(auraId, out var aura))
                candidate = aura.VFXConfig?.VFXOverrideKey;

            // 仅查当前卡实际持有的附魔，严禁跨附魔回退
            if (string.IsNullOrEmpty(candidate) && enchantment.HasValue && tpl.Enchantments != null)
            {
                string enchName = enchantment.Value.ToString();
                if (tpl.Enchantments.TryGetValue(enchName, out var enchData)
                    && enchData.Auras.TryGetValue(auraId, out var enchAura))
                {
                    candidate = enchAura.VFXConfig?.VFXOverrideKey;
                }
            }
        }

        if (!TryNormalizeAndValidateOfficialVfxKey(candidate, repo, out string key))
            return;

        if (!vfxKeyMap.ContainsKey(key))
        {
            int idx = vfxKeys.Count;
            vfxKeys.Add(key);
            vfxKeyMap[key] = idx;
        }
    }

    private static IEffectTarget ResolveCardFiredTarget(
        CombatEvent ev,
        EActionCommandType act,
        CardRenderState srcMeta,
        Dictionary<string, CardRenderState> cardEntryByCoreId)
    {
        if (TryResolveCard(cardEntryByCoreId, ev.TargetCardInstanceId, out var tgtMeta))
        {
            return new EffectTargetCard { Target = new InstanceId(tgtMeta.Entry.InstanceId) };
        }

        ECombatantId oppSide = srcMeta.Owner == ECombatantId.Player ? ECombatantId.Opponent : ECombatantId.Player;
        if (IsSelfTargetedPlayerAction(act))
        {
            return new EffectTargetPlayer { Target = srcMeta.Owner };
        }

        if (!string.IsNullOrEmpty(ev.TargetPlayerId))
        {
            return new EffectTargetPlayer { Target = ParseCombatantSide(ev.TargetPlayerId) };
        }

        // PlayerModifyAttribute 若事件未显式标注 TargetPlayerId，默认作用于施放方自身（如战中生金/加收入/提上限）
        if (act == EActionCommandType.PlayerModifyAttribute)
        {
            return new EffectTargetPlayer { Target = srcMeta.Owner };
        }

        return new EffectTargetPlayer { Target = oppSide };
    }

    private static bool IsCardTargetedAction(EActionCommandType act) => act is
        EActionCommandType.CardHaste or
        EActionCommandType.CardSlow or
        EActionCommandType.CardFreeze or
        EActionCommandType.CardCharge or
        EActionCommandType.CardReload or
        EActionCommandType.CardUpgrade or
        EActionCommandType.CardTransform or
        EActionCommandType.CardTransformDestroyed or
        EActionCommandType.CardDestroy or
        EActionCommandType.CardDisable or
        EActionCommandType.CardRepair or
        EActionCommandType.CardForceUse or
        EActionCommandType.CardModifyAttribute or
        EActionCommandType.CardEnchant or
        EActionCommandType.CardEnchantRemove or
        EActionCommandType.FlyingStart or
        EActionCommandType.FlyingStop or
        EActionCommandType.CardAddTags or
        EActionCommandType.CardAddTagsRandom or
        EActionCommandType.CardRemoveTags or
        EActionCommandType.CardTiny;

    private static bool IsSelfTargetedPlayerAction(EActionCommandType act) => act is
        EActionCommandType.PlayerShieldApply or
        EActionCommandType.PlayerHeal or
        EActionCommandType.PlayerRegenApply or
        EActionCommandType.PlayerMaxHealthIncrease or
        EActionCommandType.PlayerRageApply or
        EActionCommandType.PlayerTempoApply;

    private static EActionCommandType ResolveActionCommandType(CombatEvent ev, CardTemplate? tpl)
    {
        if (!string.IsNullOrEmpty(ev.ActionType))
        {
            var mapped = MapActionTypeString(ev.ActionType, ev.Value, EActionCommandType.None);
            if (mapped != EActionCommandType.None) return mapped;
        }

        if (tpl != null && !string.IsNullOrEmpty(ev.AbilityId))
        {
            if (tpl.Abilities.TryGetValue(ev.AbilityId, out var specificAb) &&
                !string.IsNullOrEmpty(specificAb.Action?.Type))
            {
                var specificMapped = MapActionTypeString(specificAb.Action.Type, ev.Value, EActionCommandType.None);
                if (specificMapped != EActionCommandType.None) return specificMapped;
            }
            if (tpl.Enchantments != null)
            {
                foreach (var ench in tpl.Enchantments.Values)
                {
                    if (ench.Abilities.TryGetValue(ev.AbilityId, out var enchAb) &&
                        !string.IsNullOrEmpty(enchAb.Action?.Type))
                    {
                        var enchMapped = MapActionTypeString(enchAb.Action.Type, ev.Value, EActionCommandType.None);
                        if (enchMapped != EActionCommandType.None) return enchMapped;
                    }
                }
            }
        }

        return InferPrimaryAction(tpl);
    }

    private static EActionCommandType InferPrimaryAction(CardTemplate? tpl)
    {
        if (tpl == null) return EActionCommandType.PlayerDamage;
        foreach (var ab in tpl.Abilities.Values)
        {
            string? actionType = ab.Action?.Type;
            if (string.IsNullOrEmpty(actionType)) continue;
            var mapped = MapActionTypeString(actionType, 1, EActionCommandType.None);
            if (mapped != EActionCommandType.None) return mapped;
        }
        if (tpl.Tags.Contains("Weapon")) return EActionCommandType.PlayerDamage;
        return EActionCommandType.PlayerDamage;
    }

    private static EActionCommandType MapActionTypeString(string actionType, double eventValue, EActionCommandType fallback)
    {
        string normalized = actionType.Trim();
        if (normalized.StartsWith("TAction", StringComparison.Ordinal))
        {
            normalized = normalized["TAction".Length..];
        }

        switch (normalized)
        {
            case "PlayerDamage": return EActionCommandType.PlayerDamage;
            case "PlayerShieldApply": return EActionCommandType.PlayerShieldApply;
            case "PlayerShieldRemove": return EActionCommandType.PlayerShieldRemove;
            case "PlayerBurnApply": return EActionCommandType.PlayerBurnApply;
            case "PlayerBurnRemove": return EActionCommandType.PlayerBurnRemove;
            case "PlayerPoisonApply": return EActionCommandType.PlayerPoisonApply;
            case "PlayerPoisonRemove": return EActionCommandType.PlayerPoisonRemove;
            case "PlayerHeal":
            case "PlayerReviveHeal": return EActionCommandType.PlayerHeal;
            case "PlayerRegenApply": return EActionCommandType.PlayerRegenApply;
            case "PlayerRegenRemove": return EActionCommandType.PlayerRegenRemove;
            case "PlayerJoyApply": return EActionCommandType.PlayerJoyApply;
            case "PlayerJoyRemove": return EActionCommandType.PlayerJoyRemove;
            case "PlayerRageApply": return EActionCommandType.PlayerRageApply;
            case "PlayerRageRemove": return EActionCommandType.PlayerRageRemove;
            case "PlayerTempoApply": return EActionCommandType.PlayerTempoApply;
            case "PlayerTempoRemove": return EActionCommandType.PlayerTempoRemove;
            case "PlayerGoldSteal": return EActionCommandType.PlayerGoldSteal;
            case "PlayerMaxHealthIncrease": return EActionCommandType.PlayerMaxHealthIncrease;
            case "PlayerMaxHealthDecrease": return EActionCommandType.PlayerMaxHealthDecrease;
            case "PlayerModifyAttribute": return EActionCommandType.PlayerModifyAttribute;
            case "PlayerPortraitNext": return EActionCommandType.PlayerPortraitNext;
            case "PlayerPortraitReset": return EActionCommandType.PlayerPortraitReset;

            case "CardHaste": return EActionCommandType.CardHaste;
            case "CardSlow": return EActionCommandType.CardSlow;
            case "CardFreeze": return EActionCommandType.CardFreeze;
            case "CardCharge": return EActionCommandType.CardCharge;
            case "CardReload": return EActionCommandType.CardReload;
            case "CardUpgrade": return EActionCommandType.CardUpgrade;
            case "CardTransform": return EActionCommandType.CardTransform;
            case "CardTransformDestroyed": return EActionCommandType.CardTransformDestroyed;
            case "CardDestroy": return EActionCommandType.CardDestroy;
            case "CardDisable": return EActionCommandType.CardDisable;
            case "CardRepair": return EActionCommandType.CardRepair;
            case "CardForceUse": return EActionCommandType.CardForceUse;
            case "CardModifyAttribute": return EActionCommandType.CardModifyAttribute;
            case "CardEnchant":
            case "CardEnchantRandom": return EActionCommandType.CardEnchant;
            case "CardEnchantRemove": return EActionCommandType.CardEnchantRemove;
            case "CardFlyingStart":
            case "FlyingStart": return EActionCommandType.FlyingStart;
            case "CardFlyingStop":
            case "FlyingStop": return EActionCommandType.FlyingStop;
            case "CardFlyingToggle":
                return eventValue > 0 ? EActionCommandType.FlyingStart : EActionCommandType.FlyingStop;
            case "CardAddTags":
            case "CardAddTagsList":
            case "CardAddTagsBySource": return EActionCommandType.CardAddTags;
            case "CardAddTagsRandom": return EActionCommandType.CardAddTagsRandom;
            case "CardRemoveTags": return EActionCommandType.CardRemoveTags;
            case "CardBeginSandstorm": return EActionCommandType.CardBeginSandstorm;
            case "CardTiny": return EActionCommandType.CardTiny;

            case "GameDealCards": return EActionCommandType.GameDealCards;
            case "GameSpawnCards": return EActionCommandType.GameSpawnCards;
            case "GameReroll": return EActionCommandType.GameReroll;
            case "GameModifyTime": return EActionCommandType.GameModifyTime;
            case "GameScheduleEncounter": return EActionCommandType.GameScheduleEncounter;
            case "GameUnscheduleEncounter": return EActionCommandType.GameUnscheduleEncounter;
            case "GameStartCombat": return EActionCommandType.GameStartCombat;
            case "ExitReplacementSet": return EActionCommandType.ExitReplacementSet;
        }

        if (Enum.TryParse<EActionCommandType>(normalized, ignoreCase: true, out var direct))
            return direct;

        return fallback;
    }

    private static bool TryParseEnchantmentDetail(
        string? detail,
        out EEnchantmentType enchantmentType,
        out EEnchantmentType? restoredEnchantment)
    {
        enchantmentType = default;
        restoredEnchantment = null;
        if (string.IsNullOrWhiteSpace(detail)) return false;

        string token = detail.Trim();
        int sep = token.LastIndexOf(':');
        if (sep >= 0 && sep + 1 < token.Length)
            token = token[(sep + 1)..].Trim();

        int arrowIdx = token.IndexOf("->", StringComparison.Ordinal);
        if (arrowIdx >= 0)
        {
            string left = token[..arrowIdx].Trim();
            string right = token[(arrowIdx + 2)..].Trim();
            if (!Enum.TryParse(left, ignoreCase: true, out enchantmentType))
                return false;

            if (!string.IsNullOrEmpty(right) &&
                !right.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<EEnchantmentType>(right, ignoreCase: true, out var restored))
            {
                restoredEnchantment = restored;
            }
            return true;
        }

        return Enum.TryParse(token, ignoreCase: true, out enchantmentType);
    }

    private static bool TryParseTransformDetail(string? detail, out string? firstToken, out string? newTemplateId, out bool isArrowFormat)
    {
        firstToken = null;
        newTemplateId = null;
        isArrowFormat = false;
        if (string.IsNullOrWhiteSpace(detail)) return false;

        string trimmed = detail.Trim();
        int arrowIdx = trimmed.IndexOf("->", StringComparison.Ordinal);
        if (arrowIdx >= 0)
        {
            isArrowFormat = true;
            string left = trimmed[..arrowIdx].Trim();
            string right = trimmed[(arrowIdx + 2)..].Trim();
            if (!string.IsNullOrEmpty(left)) firstToken = left;
            // P0-19：放行非 GUID 自定义模板 ID（如 "mod:custom_blade"），不再硬拦截 Guid.TryParse
            if (!string.IsNullOrEmpty(right)) newTemplateId = right;
            return newTemplateId != null;
        }

        if (Guid.TryParse(trimmed, out _))
        {
            newTemplateId = trimmed;
            return true;
        }

        if (trimmed.IndexOfAny(['|', ',', ';']) >= 0)
        {
            string[] parts = trimmed.Split(['|', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string p in parts)
            {
                if (Guid.TryParse(p, out _) && newTemplateId == null)
                {
                    newTemplateId = p;
                }
                else if (firstToken == null && !p.Equals("-", StringComparison.Ordinal))
                {
                    firstToken = p;
                }
                else if (newTemplateId == null && !p.Equals("-", StringComparison.Ordinal))
                {
                    newTemplateId = p;
                }
            }
            if (newTemplateId == null && firstToken != null)
            {
                newTemplateId = firstToken;
                firstToken = null;
            }
            return newTemplateId != null;
        }

        // 单段非 GUID 自定义模板 ID（如 "mod:custom_blade"）
        newTemplateId = trimmed;
        return true;
    }

    private static (int GroupIdx, int EntryIdx, ECardAttributeType AttrType, int? OldVal, int? NewVal) ResolveQuestLocation(
        string? detail,
        CardTemplate? tpl,
        int fallbackVal)
    {
        int groupIdx = 0;
        int entryIdx = 0;
        ECardAttributeType attrType = ECardAttributeType.Quest_1;
        int? oldVal = null;
        int? newVal = null;
        string? attrName = null;

        if (!string.IsNullOrWhiteSpace(detail))
        {
            string[] parts = detail.Split(['|', ':', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 1)
            {
                if (parts[0].StartsWith("Quest_", StringComparison.OrdinalIgnoreCase))
                    attrName = parts[0];
            }
            else if (parts.Length >= 2)
            {
                if (parts[0].StartsWith("Quest_", StringComparison.OrdinalIgnoreCase))
                {
                    attrName = parts[0];
                    if (int.TryParse(parts[1], out int nv)) newVal = nv;
                    if (parts.Length >= 3 && int.TryParse(parts[2], out int nv2))
                    {
                        oldVal = newVal;
                        newVal = nv2;
                    }
                }
                else if (int.TryParse(parts[0], out int g) && int.TryParse(parts[1], out int e))
                {
                    groupIdx = Math.Max(0, g);
                    entryIdx = Math.Max(0, e);
                    if (parts.Length >= 3 && int.TryParse(parts[2], out int ov)) oldVal = ov;
                    if (parts.Length >= 4 && int.TryParse(parts[3], out int nv)) newVal = nv;
                }
            }
        }

        if (!string.IsNullOrEmpty(attrName) && Enum.TryParse<ECardAttributeType>(attrName, ignoreCase: true, out var parsedAttr))
        {
            attrType = parsedAttr;
        }

        if (tpl?.Quests != null)
        {
            if (!string.IsNullOrEmpty(attrName))
            {
                for (int g = 0; g < tpl.Quests.Count; g++)
                {
                    var entries = tpl.Quests[g].Entries;
                    for (int e = 0; e < entries.Count; e++)
                    {
                        if (string.Equals(entries[e].AttributeType, attrName, StringComparison.OrdinalIgnoreCase))
                        {
                            groupIdx = g;
                            entryIdx = e;
                            break;
                        }
                    }
                }
            }
            else if (groupIdx >= 0 && groupIdx < tpl.Quests.Count &&
                     entryIdx >= 0 && entryIdx < tpl.Quests[groupIdx].Entries.Count)
            {
                string tplAttr = tpl.Quests[groupIdx].Entries[entryIdx].AttributeType;
                if (Enum.TryParse<ECardAttributeType>(tplAttr, ignoreCase: true, out var parsedTplAttr))
                    attrType = parsedTplAttr;
            }
        }

        newVal ??= fallbackVal > 0 ? fallbackVal : null;
        return (groupIdx, entryIdx, attrType, oldVal, newVal);
    }

    private static bool TryParseTier(string? detail, out ETier tier)
    {
        tier = default;
        if (string.IsNullOrWhiteSpace(detail)) return false;
        string token = detail.Trim();
        int arrow = token.LastIndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0 && arrow + 2 < token.Length) token = token[(arrow + 2)..].Trim();
        int colon = token.LastIndexOf(':');
        if (colon >= 0 && colon + 1 < token.Length) token = token[(colon + 1)..].Trim();
        return Enum.TryParse(token, ignoreCase: true, out tier);
    }

    private static void PopulateTemplateAttributesForTier(
        Dictionary<ECardAttributeType, int> target,
        CardTemplate? tpl,
        ETier tier)
    {
        if (tpl == null) return;
        if (tpl.Attributes != null)
        {
            foreach (var kv in tpl.Attributes)
            {
                if (Enum.TryParse<ECardAttributeType>(kv.Key, out var at))
                    target[at] = (int)Math.Round(kv.Value);
            }
        }
        if (tpl.Tiers == null || tpl.Tiers.Count == 0) return;

        ETier[] order = [ETier.Bronze, ETier.Silver, ETier.Gold, ETier.Diamond, ETier.Legendary];
        foreach (var t in order)
        {
            if (t > tier) break;
            if (!tpl.Tiers.TryGetValue(t.ToString(), out var td)) continue;
            foreach (var kv in td.Attributes)
            {
                if (Enum.TryParse<ECardAttributeType>(kv.Key, out var at))
                    target[at] = (int)Math.Round(kv.Value);
            }
        }
    }

    private static ECardSize ParseCoreCardSize(string? size) => size switch
    {
        "Small" => ECardSize.Small,
        "Large" => ECardSize.Large,
        _ => ECardSize.Medium,
    };

    private static ECardType ParseCoreCardType(string? coreType, ECardType fallback) => coreType switch
    {
        "Item" => ECardType.Item,
        "Skill" => ECardType.Skill,
        "CombatEncounter" => ECardType.CombatEncounter,
        "EncounterStep" => ECardType.EncounterStep,
        "EventEncounter" => ECardType.EventEncounter,
        "PedestalEncounter" => ECardType.PedestalEncounter,
        "PvpEncounter" => ECardType.PvpEncounter,
        "SocketEffect" => ECardType.SocketEffect,
        "PlayerEffect" => ECardType.PlayerEffect,
        _ => fallback,
    };
}
