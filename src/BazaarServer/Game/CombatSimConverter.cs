using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;

namespace BazaarServer.Game;

/// <summary>
/// 把 BazaarCore.Combat.CombatSimulator 的 CombatResult（20ms 事件与时间轴）
/// 1:1 转换为原版 Unity 客户端 CombatSimHandler 消费的 50ms 步长官方 CombatSim 消息。
/// </summary>
public static class CombatSimConverter
{
    public const int FrameStepMs = 50;

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
        public ECombatantId Owner { get; init; }
        public Dictionary<ECardAttributeType, int> PrevAttributes { get; } = new();
        public bool PrevDestroyed { get; set; }
        public int SellPriceBaseOffset { get; set; }
        public bool SellPriceOffsetResolved { get; set; }
        public int LastEmittedCdFrame { get; set; } = -100;
        public int LastEmittedCdValue { get; set; } = -1;
        public double LastEmittedCdRate { get; set; } = 1.0;
    }

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
    /// 将战斗模拟结果转换为客户端 <see cref="CombatSim"/> 消息流。
    /// 保留 6 参数默认签名兼容，支持可选传入 PvE 野怪战金币与经验奖励以生成血条节点与跳币/经验特效。
    /// </summary>
    public static CombatSim ToCombatSim(
        CombatResult result,
        GameDataRepository repo,
        IReadOnlyDictionary<string, CardInstance> playerCardsByInstanceId,
        IReadOnlyDictionary<string, CardInstance> opponentCardsByInstanceId,
        IReadOnlyList<RunState.CardEntry> playerBoardCards,
        IReadOnlyList<RunState.CardEntry> opponentBoardCards,
        int pveGoldReceived = 0,
        int pveXpReceived = 0)
    {
        bool playerWon = string.Equals(result.WinnerPlayerId, "player", StringComparison.OrdinalIgnoreCase);
        var sim = new CombatSim
        {
            Winner = playerWon ? ECombatantId.Player : ECombatantId.Opponent,
            Loser = playerWon ? ECombatantId.Opponent : ECombatantId.Player,
            IsDraw = result.IsDraw,
        };
        sim.Frames.Clear();

        // 建立 CoreCard InstanceId / 协议 InstanceId → 渲染状态映射
        var cardEntryByCoreId = new Dictionary<string, CardRenderState>(StringComparer.Ordinal);
        RegisterBoardCards(cardEntryByCoreId, playerBoardCards, playerCardsByInstanceId, ECombatantId.Player);
        RegisterBoardCards(cardEntryByCoreId, opponentBoardCards, opponentCardsByInstanceId, ECombatantId.Opponent);

        var timeline = result.Timeline;
        int totalMs = Math.Max(FrameStepMs * 10, result.DurationMs + FrameStepMs);
        int maxFrames = Math.Max(2400, (CombatSimulator.MaxDurationMs / FrameStepMs) + 16);
        int frameCount = Math.Clamp((totalMs / FrameStepMs) + 1, 12, maxFrames);

        // 配置 PvE 血条奖励阈值（CoinRewardListener / ExperienceRewardListener 按 Ceil(pct * MaxHp) 精确匹配）
        int oppMaxHp = timeline.Count > 0 ? Math.Max(1, timeline[0].P2MaxHp) : 100;
        var rewardTracker = BuildRewardTracker(sim, oppMaxHp, result.Player2RemainingHp, pveGoldReceived, pveXpReceived);

        // 首帧玩家基线：将开战瞬发状态（护盾/灼烧/剧毒/狂暴）清零，确保 TTriggerOnFightStarted 生效即跳数
        CombatTickState? prevSnap = timeline.Count > 0 ? CreateInitialPlayerBaseline(timeline[0]) : null;
        var prevQuestProgress = new Dictionary<(string CardId, int GroupIdx, int EntryIdx), int>();
        var questCompletedCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var itemActionCredits = new Dictionary<(int TimeMs, string CardInstanceId, EActionCommandType ActionType), int>();
        var vfxKeyMap = new Dictionary<string, int>(StringComparer.Ordinal);
        sim.VfxKeys ??= new List<string>();
        sim.VfxKeys.Clear();
        var firedInFrame = new HashSet<CardRenderState>();
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

            // 预扫描落在本帧窗口内的 CardFired 事件，用于确保冷却重置帧精确发回 CooldownMax 以触发客户端 reset 特效
            firedInFrame.Clear();
            for (int scanIdx = evIdx; scanIdx < events.Count && (isLastFrame || events[scanIdx].TimeMs <= frameEndMs); scanIdx++)
            {
                var scanEv = events[scanIdx];
                if (scanEv.Type == CombatEventType.CardFired &&
                    TryResolveCard(cardEntryByCoreId, scanEv.SourceCardInstanceId, out var firedState))
                {
                    firedInFrame.Add(firedState);
                }
            }

            var frame = new CombatSimFrame();

            // 1. 双方血盾烧毒等属性差分与卡牌全量属性/状态差分
            if (curSnap != null && prevSnap != null)
            {
                frame.PlayerUpdates = BuildPlayerUpdate(prevSnap, curSnap, isPlayer: true);
                frame.OpponentUpdates = BuildPlayerUpdate(prevSnap, curSnap, isPlayer: false);

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
                    repo,
                    cardEntryByCoreId,
                    prevQuestProgress,
                    questCompletedCount,
                    itemActionCredits,
                    vfxKeyMap,
                    simHealthState,
                    ref sandstormCountdownEmitted,
                    ref sandstormStartedEmitted);
            }

            // 保护客户端 DataExtensions.Update：若本帧存在 HealthAdjustments 且净额与实际 HP 差分不一致，强制携带最终 Health 校准
            if (curSnap != null && prevSnap != null)
            {
                EnsureHealthAttributeSynced(frame.PlayerUpdates, prevSnap.P1Hp, curSnap.P1Hp);
                EnsureHealthAttributeSynced(frame.OpponentUpdates, prevSnap.P2Hp, curSnap.P2Hp);
            }

            // 3. 最后一帧：先将战斗内临时状态（Freeze/Slow/Haste/Flying/Heated/Chilled）归零并回满弹药，
            //    严格置于 CombatSimEventCombatantDied 之前，彻底修复 #6/#27 客户端在 CombatantDied 截断导致飞行/冰冻特效永不收尾！
            if (isLastFrame)
            {
                EmitCrossedMonsterRewards(frame, rewardTracker, result.Player2RemainingHp, forceFlushRemaining: true);
                EmitPostCombatCleanupIntoFrame(frame, cardEntryByCoreId);

                if (playerWon)
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

        return sim;
    }

    /// <summary>
    /// 在 <see cref="CombatSimEventCombatantDied"/> 触发前将本场所有修改过的瞬态状态（冰冻/减速/急速/飞行/冷热/弹药）
    /// 直接归零并写入末帧 CardUpdates（延迟分配，仅对确实有变动的卡牌创建更新包）。
    /// </summary>
    private static void EmitPostCombatCleanupIntoFrame(
        CombatSimFrame targetFrame,
        Dictionary<string, CardRenderState> cardEntryByCoreId)
    {
        if (cardEntryByCoreId.Count == 0) return;

        var visited = new HashSet<CardRenderState>(ReferenceEqualityComparer.Instance);

        foreach (var state in cardEntryByCoreId.Values)
        {
            if (!visited.Add(state)) continue;

            var entry = state.Entry;
            var cardId = new InstanceId(entry.InstanceId);
            CombatSimCardUpdate? cu = targetFrame.CardUpdates.TryGetValue(cardId, out var existing) ? existing : null;
            bool hasUpdate = cu != null;

            int outFlying = entry.Attributes.GetValueOrDefault(ECardAttributeType.Flying, 0) > 0 ? 1 : 0;
            int outHeated = entry.Attributes.GetValueOrDefault(ECardAttributeType.Heated, 0) > 0 ? 1 : 0;
            int outChilled = entry.Attributes.GetValueOrDefault(ECardAttributeType.Chilled, 0) > 0 ? 1 : 0;

            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Freeze, 0, ref hasUpdate);
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Slow, 0, ref hasUpdate);
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Haste, 0, ref hasUpdate);
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Flying, outFlying, ref hasUpdate);
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Heated, outHeated, ref hasUpdate);
            EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Chilled, outChilled, ref hasUpdate);

            int targetAmmoMax = state.PrevAttributes.GetValueOrDefault(ECardAttributeType.AmmoMax, 0);
            if (targetAmmoMax > 0)
                EmitAttrIfChangedLazy(state, cardId, ref cu, ECardAttributeType.Ammo, targetAmmoMax, ref hasUpdate);

            if (hasUpdate && cu != null)
                targetFrame.CardUpdates[cardId] = cu;
        }
    }

    private static void RegisterBoardCards(
        Dictionary<string, CardRenderState> map,
        IReadOnlyList<RunState.CardEntry> entries,
        IReadOnlyDictionary<string, CardInstance> coreByEntryId,
        ECombatantId owner)
    {
        foreach (var e in entries)
        {
            var state = new CardRenderState
            {
                Entry = e,
                Owner = owner,
            };
            foreach (var kv in e.Attributes)
            {
                state.PrevAttributes[kv.Key] = kv.Value;
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

    private static CombatTickState CreateInitialPlayerBaseline(CombatTickState firstSnap)
    {
        return new CombatTickState
        {
            TimeMs = 0,
            P1Hp = firstSnap.P1MaxHp > 0 ? firstSnap.P1MaxHp : firstSnap.P1Hp,
            P1MaxHp = firstSnap.P1MaxHp,
            P1Shield = 0,
            P1Burn = 0,
            P1Poison = 0,
            P1Regen = 0,
            P1Rage = 0,
            P1Enraged = false,
            P1Tempo = 0,
            P2Hp = firstSnap.P2MaxHp > 0 ? firstSnap.P2MaxHp : firstSnap.P2Hp,
            P2MaxHp = firstSnap.P2MaxHp,
            P2Shield = 0,
            P2Burn = 0,
            P2Poison = 0,
            P2Regen = 0,
            P2Rage = 0,
            P2Enraged = false,
            P2Tempo = 0,
            SandstormActive = false,
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
        bool hasUpdate = cu != null && (cu.Attributes.Count > 0 || cu.State != null || cu.Enchantment.HasValue || cu.Tier.HasValue || cu.Placement != null);

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

        // 4. 销毁状态变化
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
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        Dictionary<(string CardId, int GroupIdx, int EntryIdx), int> prevQuestProgress,
        Dictionary<string, int> questCompletedCount,
        Dictionary<(int TimeMs, string CardInstanceId, EActionCommandType ActionType), int> itemActionCredits,
        Dictionary<string, int> vfxKeyMap,
        FrameCombatantState simHealthState,
        ref bool sandstormCountdownEmitted,
        ref bool sandstormStartedEmitted)
    {
        switch (ev.Type)
        {
            case CombatEventType.CardFired:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var srcMeta))
                {
                    var tpl = repo.GetCard(srcMeta.Entry.TemplateId.ToString());
                    string effectId = ResolveEffectId(ev, tpl);
                    EActionCommandType act = ResolveActionCommandType(ev, tpl);
                    sim.UpdateStats(srcMeta.Entry.InstanceId, ECardStats.UseCount, 1);

                    // 若主动作是卡指卡类型但 CardFired 未携带目标卡，跳过空目标 EffectExecuted（交由后续 Haste/Slow/Freeze 等事件发射真实 EffectTargetCard）
                    if (IsCardTargetedAction(act) && !TryResolveCard(cardEntryByCoreId, ev.TargetCardInstanceId, out _))
                    {
                        if (ev.IsSkillSource || srcMeta.Entry.Type == ECardType.Skill)
                        {
                            var fallbackTarget = new EffectTargetPlayer
                            {
                                Target = srcMeta.Owner == ECombatantId.Player ? ECombatantId.Opponent : ECombatantId.Player,
                            };
                            EmitSkillPulseIfNeeded(frame, ev, srcMeta, frameIndex, eventSeq, effectId, fallbackTarget);
                        }
                        break;
                    }

                    IEffectTarget target = ResolveCardFiredTarget(ev, act, srcMeta, cardEntryByCoreId);
                    EmitSkillPulseIfNeeded(frame, ev, srcMeta, frameIndex, eventSeq, effectId, target);

                    frame.Events.Add(new CombatSimEventEffectExecuted
                    {
                        ExecutionContextId = $"ctx_{frameIndex}_{eventSeq}",
                        EffectId = effectId,
                        ActionType = act,
                        Source = new InstanceId(srcMeta.Entry.InstanceId),
                        TriggerSource = new InstanceId(srcMeta.Entry.InstanceId),
                        Target = target,
                        VfxIndex = ResolveVfxIndex(tpl, effectId, srcMeta.Entry.Enchantment, vfxKeyMap, sim.VfxKeys),
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
                        ApplyDamageWithShieldSplit(frame, simHealthState, targetSide, EDamageType.Damage, amt, ev.IsCrit);
                        if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var dmgSrc))
                        {
                            EmitActionExecutionIfNeeded(frame, ev, dmgSrc, frameIndex, eventSeq, repo, EActionCommandType.PlayerDamage, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, vfxKeyMap, sim.VfxKeys);
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

                        AddHealthAdj(frame, targetSide, EDamageType.Shield, EPlayerHealthChangeType.Shield, amt, false);
                        if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var shSrc))
                        {
                            EmitActionExecutionIfNeeded(frame, ev, shSrc, frameIndex, eventSeq, repo, EActionCommandType.PlayerShieldApply, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, vfxKeyMap, sim.VfxKeys);
                            sim.UpdateStats(shSrc.Entry.InstanceId, ECardStats.ShieldAdded, amt);
                        }
                    }
                }
                break;

            case CombatEventType.HealApplied:
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int amt = Math.Max(0, (int)Math.Round(ev.Value));
                    if (amt > 0)
                    {
                        if (targetSide == ECombatantId.Player)
                            simHealthState.P1Hp = simHealthState.P1MaxHp > 0 ? Math.Min(simHealthState.P1MaxHp, simHealthState.P1Hp + amt) : simHealthState.P1Hp + amt;
                        else
                            simHealthState.P2Hp = simHealthState.P2MaxHp > 0 ? Math.Min(simHealthState.P2MaxHp, simHealthState.P2Hp + amt) : simHealthState.P2Hp + amt;

                        AddHealthAdj(frame, targetSide, EDamageType.Heal, EPlayerHealthChangeType.Health, amt, false);
                        if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var hlSrc))
                        {
                            bool isLifesteal = string.Equals(ev.Detail, "Lifesteal", StringComparison.Ordinal);
                            bool isRegenGain = ev.Detail == "RegenGain";
                            // 8.4：Lifesteal 仅恢复 HP，不计入玩家卡牌统计 HealAdded，也不触发 PlayerHeal 动作执行
                            if (!isLifesteal)
                            {
                                EmitActionExecutionIfNeeded(
                                    frame, ev, hlSrc, frameIndex, eventSeq, repo,
                                    isRegenGain ? EActionCommandType.PlayerRegenApply : EActionCommandType.PlayerHeal,
                                    new EffectTargetPlayer { Target = targetSide },
                                    itemActionCredits,
                                    vfxKeyMap,
                                    sim.VfxKeys);
                                sim.UpdateStats(hlSrc.Entry.InstanceId, isRegenGain ? ECardStats.RegenAdded : ECardStats.HealAdded, amt);
                            }
                        }
                    }
                }
                break;

            case CombatEventType.BurnApplied:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var bSrc))
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    EmitActionExecutionIfNeeded(frame, ev, bSrc, frameIndex, eventSeq, repo, EActionCommandType.PlayerBurnApply, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, vfxKeyMap, sim.VfxKeys);
                    sim.UpdateStats(bSrc.Entry.InstanceId, ECardStats.BurnAdded, Math.Max(0, (int)Math.Round(ev.Value)));
                }
                break;

            case CombatEventType.PoisonApplied:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var pSrc))
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    EmitActionExecutionIfNeeded(frame, ev, pSrc, frameIndex, eventSeq, repo, EActionCommandType.PlayerPoisonApply, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, vfxKeyMap, sim.VfxKeys);
                    sim.UpdateStats(pSrc.Entry.InstanceId, ECardStats.PoisonAdded, Math.Max(0, (int)Math.Round(ev.Value)));
                }
                break;

            case CombatEventType.HasteApplied:
                EmitCardTargetedEffect(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardHaste, vfxKeyMap, sim.VfxKeys);
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var hSrc))
                    sim.UpdateStats(hSrc.Entry.InstanceId, ECardStats.HastedCardsCount, 1);
                break;

            case CombatEventType.SlowApplied:
                EmitCardTargetedEffect(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardSlow, vfxKeyMap, sim.VfxKeys);
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var sSrc))
                    sim.UpdateStats(sSrc.Entry.InstanceId, ECardStats.SlowedCardsCount, 1);
                break;

            case CombatEventType.FreezeApplied:
                EmitCardTargetedEffect(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardFreeze, vfxKeyMap, sim.VfxKeys);
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var fSrc))
                    sim.UpdateStats(fSrc.Entry.InstanceId, ECardStats.FrozenCardsCount, 1);
                break;

            case CombatEventType.ChargeApplied:
                EmitCardTargetedEffect(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardCharge, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardReloaded:
                EmitCardTargetedEffect(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardReload, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardDestroyed:
                EmitCardTargetedEffect(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardDestroy, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.FlyingChanged:
                EmitCardTargetedEffect(
                    frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId,
                    ev.Value > 0 ? EActionCommandType.FlyingStart : EActionCommandType.FlyingStop,
                    vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardAttributeModified:
                EmitCardTargetedEffect(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardModifyAttribute, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardUpgraded:
                HandleCardUpgradedEvent(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardEnchanted:
                HandleCardEnchantedEvent(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.CardTransformed:
                HandleCardTransformedEvent(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, vfxKeyMap, sim.VfxKeys);
                break;

            case CombatEventType.QuestUpdated:
                HandleQuestUpdatedEvent(frame, ev, repo, cardEntryByCoreId, prevQuestProgress);
                break;

            case CombatEventType.QuestCompleted:
                HandleQuestCompletedEvent(frame, ev, repo, cardEntryByCoreId, prevQuestProgress, questCompletedCount);
                break;

            case CombatEventType.PlayerTempoApplied:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var tpSrc))
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    EmitActionExecutionIfNeeded(frame, ev, tpSrc, frameIndex, eventSeq, repo, EActionCommandType.PlayerTempoApply, new EffectTargetPlayer { Target = targetSide }, itemActionCredits, vfxKeyMap, sim.VfxKeys);
                    int gained = Math.Max(0, (int)Math.Round(ev.Value));
                    if (gained > 0)
                        sim.UpdateStats(tpSrc.Entry.InstanceId, ECardStats.TempoAdded, gained);
                }
                break;

            case CombatEventType.ActionCostSpent:
                if (TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var costSrc) &&
                    string.Equals(ev.Detail, "Tempo", StringComparison.OrdinalIgnoreCase))
                {
                    frame.Events.Add(new CombatSimEventCardActionCostSpent
                    {
                        ExecutingCard = new InstanceId(costSrc.Entry.InstanceId),
                        PlayerAttributeSpent = EPlayerAttributeType.Tempo,
                        CardAttributeSpent = null,
                    });
                    int spent = Math.Max(0, (int)Math.Round(ev.Value));
                    if (spent > 0)
                        sim.UpdateStats(costSrc.Entry.InstanceId, ECardStats.TempoSpent, spent);
                }
                break;

            case CombatEventType.BurnTick:
                {
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int amt = Math.Max(0, (int)Math.Round(ev.Value));
                    if (amt > 0)
                    {
                        // Burn 穿透一半护盾（Simulator 已折算净扣血量 ev.Value），同步到帧内血量并允许击杀显示负血
                        if (targetSide == ECombatantId.Player) simHealthState.P1Hp -= amt;
                        else simHealthState.P2Hp -= amt;
                        AddHealthAdj(frame, targetSide, EDamageType.Burn, EPlayerHealthChangeType.Health, -amt, false);
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
                    var targetSide = ParseCombatantSide(ev.TargetPlayerId);
                    int amt = Math.Max(0, (int)Math.Round(ev.Value));
                    if (amt > 0)
                    {
                        if (targetSide == ECombatantId.Player)
                            simHealthState.P1Hp = simHealthState.P1MaxHp > 0 ? Math.Min(simHealthState.P1MaxHp, simHealthState.P1Hp + amt) : simHealthState.P1Hp + amt;
                        else
                            simHealthState.P2Hp = simHealthState.P2MaxHp > 0 ? Math.Min(simHealthState.P2MaxHp, simHealthState.P2Hp + amt) : simHealthState.P2Hp + amt;
                        AddHealthAdj(frame, targetSide, EDamageType.Regen, EPlayerHealthChangeType.Health, amt, false);
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
                    int amt = Math.Max(0, (int)Math.Round(ev.Value));
                    if (amt > 0)
                    {
                        ApplyDamageWithShieldSplit(frame, simHealthState, ECombatantId.Player, EDamageType.Sandstorm, amt, false);
                        ApplyDamageWithShieldSplit(frame, simHealthState, ECombatantId.Opponent, EDamageType.Sandstorm, amt, false);
                    }
                }
                break;
        }
    }

    private static void EmitCardTargetedEffect(
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        EActionCommandType defaultAction,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
    {
        bool hasSrc = TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var srcMeta);
        bool hasTgt = TryResolveCard(cardEntryByCoreId, ev.TargetCardInstanceId, out var tgtMeta);
        if (!hasSrc && !hasTgt) return;

        var effectiveSrc = hasSrc ? srcMeta : tgtMeta;
        var tpl = repo.GetCard(effectiveSrc.Entry.TemplateId.ToString());
        string effectId = ResolveEffectId(ev, tpl);
        EActionCommandType act = !string.IsNullOrEmpty(ev.ActionType)
            ? MapActionTypeString(ev.ActionType, ev.Value, defaultAction)
            : defaultAction;

        // 仅当目标卡可解析时使用 EffectTargetCard（避免客户端对缺失 TargetCard 的 CardAction 抛 NRE）
        if (!hasTgt)
        {
            if (ev.IsSkillSource || effectiveSrc.Entry.Type == ECardType.Skill)
            {
                var fallbackTarget = new EffectTargetPlayer
                {
                    Target = effectiveSrc.Owner == ECombatantId.Player ? ECombatantId.Opponent : ECombatantId.Player,
                };
                EmitSkillPulseIfNeeded(frame, ev, effectiveSrc, frameIndex, eventSeq, effectId, fallbackTarget);
            }
            return;
        }

        IEffectTarget target = new EffectTargetCard { Target = new InstanceId(tgtMeta.Entry.InstanceId) };
        EmitSkillPulseIfNeeded(frame, ev, effectiveSrc, frameIndex, eventSeq, effectId, target);

        frame.Events.Add(new CombatSimEventEffectExecuted
        {
            ExecutionContextId = $"ctx_{frameIndex}_{eventSeq}",
            EffectId = effectId,
            ActionType = act,
            Source = new InstanceId(effectiveSrc.Entry.InstanceId),
            TriggerSource = new InstanceId(effectiveSrc.Entry.InstanceId),
            Target = target,
            VfxIndex = ResolveVfxIndex(tpl, effectId, effectiveSrc.Entry.Enchantment, vfxKeyMap, vfxKeys, ev.VfxKeyOverride),
        });
    }

    private static void EmitSkillPulseIfNeeded(
        CombatSimFrame frame,
        CombatEvent ev,
        CardRenderState srcMeta,
        int frameIndex,
        int eventSeq,
        string effectId,
        IEffectTarget target)
    {
        if (!ev.IsSkillSource && srcMeta.Entry.Type != ECardType.Skill) return;
        var srcId = new InstanceId(srcMeta.Entry.InstanceId);
        frame.Events.Add(new CombatSimEventEffectTriggered
        {
            ExecutionContextId = $"ctx_{frameIndex}_{eventSeq}",
            EffectId = effectId,
            Source = srcId,
            TriggerSource = srcId,
            Targets = new List<IEffectTarget> { target },
        });
    }

    private static void EmitActionExecutionIfNeeded(
        CombatSimFrame frame,
        CombatEvent ev,
        CardRenderState srcMeta,
        int frameIndex,
        int eventSeq,
        GameDataRepository repo,
        EActionCommandType defaultAction,
        IEffectTarget target,
        Dictionary<(int TimeMs, string CardInstanceId, EActionCommandType ActionType), int> itemActionCredits,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
    {
        var tpl = repo.GetCard(srcMeta.Entry.TemplateId.ToString());
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

        EmitSkillPulseIfNeeded(frame, ev, srcMeta, frameIndex, eventSeq, effectId, target);
        frame.Events.Add(new CombatSimEventEffectExecuted
        {
            ExecutionContextId = $"ctx_{frameIndex}_{eventSeq}",
            EffectId = effectId,
            ActionType = act,
            Source = new InstanceId(srcMeta.Entry.InstanceId),
            TriggerSource = new InstanceId(srcMeta.Entry.InstanceId),
            Target = target,
            VfxIndex = ResolveVfxIndex(tpl, effectId, srcMeta.Entry.Enchantment, vfxKeyMap, vfxKeys, ev.VfxKeyOverride),
        });
    }

    private static void HandleCardUpgradedEvent(
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
    {
        EmitCardTargetedEffect(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardUpgrade, vfxKeyMap, vfxKeys);

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
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
    {
        EmitCardTargetedEffect(frame, ev, frameIndex, eventSeq, repo, cardEntryByCoreId, EActionCommandType.CardEnchant, vfxKeyMap, vfxKeys);

        string? tgtKey = !string.IsNullOrEmpty(ev.TargetCardInstanceId) ? ev.TargetCardInstanceId : ev.SourceCardInstanceId;
        if (!TryResolveCard(cardEntryByCoreId, tgtKey, out var tgtMeta)) return;

        if (TryParseEnchantment(ev.Detail, out var enchType))
        {
            var cardId = new InstanceId(tgtMeta.Entry.InstanceId);
            var cu = GetOrCreateCardUpdate(frame, cardId);
            cu.Enchantment = enchType;
            frame.CardUpdates[cardId] = cu;

            frame.Events.Add(new CombatSimEventCardEnchanted
            {
                InstanceId = tgtMeta.Entry.InstanceId,
                EnchantmentType = enchType,
                IsReverted = false,
            });
        }
    }

    private static void HandleCardTransformedEvent(
        CombatSimFrame frame,
        CombatEvent ev,
        int frameIndex,
        int eventSeq,
        GameDataRepository repo,
        Dictionary<string, CardRenderState> cardEntryByCoreId,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys)
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

        string execCtxId = $"ctx_{frameIndex}_{eventSeq}";
        string origInstStr = origMeta.Entry.InstanceId;
        var origInstId = new InstanceId(origInstStr);

        // 先构造与变换事件同 ContextId 的 EffectExecuted 弹道（供 TransformManager 提取播动画）
        var srcMeta = TryResolveCard(cardEntryByCoreId, ev.SourceCardInstanceId, out var sm) ? sm : origMeta;
        var srcTpl = repo.GetCard(srcMeta.Entry.TemplateId.ToString());
        string effectId = ResolveEffectId(ev, srcTpl);
        IEffectTarget target = new EffectTargetCard { Target = origInstId };
        EmitSkillPulseIfNeeded(frame, ev, srcMeta, frameIndex, eventSeq, effectId, target);
        frame.Events.Add(new CombatSimEventEffectExecuted
        {
            ExecutionContextId = execCtxId,
            EffectId = effectId,
            ActionType = EActionCommandType.CardTransform,
            Source = new InstanceId(srcMeta.Entry.InstanceId),
            TriggerSource = new InstanceId(srcMeta.Entry.InstanceId),
            Target = target,
            VfxIndex = ResolveVfxIndex(srcTpl, effectId, srcMeta.Entry.Enchantment, vfxKeyMap, vfxKeys),
        });

        if (!Guid.TryParse(newTemplateId, out var newTemplateGuid))
        {
            return;
        }

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
        var newTpl = repo.GetCard(newTemplateGuid.ToString());
        ECardType newCardType = ParseCoreCardType(newTpl?.Type, origMeta.Entry.Type);
        ECardSize newSize = newTpl != null ? ParseCoreCardSize(newTpl.Size) : origMeta.Entry.Size;
        ETier newTier = origMeta.Entry.Tier;

        var newEntry = new RunState.CardEntry
        {
            InstanceId = newProtoInstId,
            TemplateId = newTemplateGuid,
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
            Owner = origMeta.Owner,
        };
        foreach (var kv in newEntry.Attributes)
            newState.PrevAttributes[kv.Key] = kv.Value;

        cardEntryByCoreId[newProtoInstId] = newState;
        if (!string.IsNullOrEmpty(coreReplacementId))
        {
            cardEntryByCoreId[coreReplacementId] = newState;
        }

        var newInstIdObj = new InstanceId(newProtoInstId);
        var newCardUpdate = GetOrCreateCardUpdate(frame, newInstIdObj);
        newCardUpdate.Size = newSize;
        newCardUpdate.Tier = newTier;
        newCardUpdate.Enchantment = newEntry.Enchantment;
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

        frame.Events.Add(new CombatSimEventCardTransformed(
            execCtxId,
            origInstStr,
            new List<SimEventCardTransformation>
            {
                new(
                    newProtoInstId,
                    newTemplateGuid.ToString(),
                    newCardType,
                    origMeta.Owner,
                    origMeta.Entry.Section,
                    origMeta.Entry.Socket),
            }));
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

        var tpl = repo.GetCard(state.Entry.TemplateId.ToString());
        var (groupIdx, entryIdx, questAttrType, parsedOld, parsedNew) = ResolveQuestLocation(ev.Detail, tpl, (int)Math.Round(ev.Value));
        var progressKey = (state.Entry.InstanceId, groupIdx, entryIdx);
        int oldVal = parsedOld ?? prevQuestProgress.GetValueOrDefault(progressKey, 0);
        int newVal = parsedNew ?? Math.Max(oldVal, (int)Math.Round(ev.Value));
        prevQuestProgress[progressKey] = newVal;

        // 同步更新卡面 Quest_N 属性，确保客户端 Tooltip 与完成判定读取到最新进度
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

        var tpl = repo.GetCard(state.Entry.TemplateId.ToString());
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

    private static CombatSimPlayerUpdate? BuildPlayerUpdate(CombatTickState prev, CombatTickState cur, bool isPlayer)
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

        if (isPlayer)
        {
            Check(EPlayerAttributeType.Health, prev.P1Hp, cur.P1Hp);
            Check(EPlayerAttributeType.HealthMax, prev.P1MaxHp, cur.P1MaxHp);
            Check(EPlayerAttributeType.Shield, prev.P1Shield, cur.P1Shield);
            Check(EPlayerAttributeType.Burn, prev.P1Burn, cur.P1Burn);
            Check(EPlayerAttributeType.Poison, prev.P1Poison, cur.P1Poison);
            Check(EPlayerAttributeType.HealthRegen, prev.P1Regen, cur.P1Regen);
            Check(EPlayerAttributeType.Rage, prev.P1Rage, cur.P1Rage);
            Check(EPlayerAttributeType.Enraged, prev.P1Enraged ? 1 : 0, cur.P1Enraged ? 1 : 0);
            Check(EPlayerAttributeType.Tempo, prev.P1Tempo, cur.P1Tempo);
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
            Check(EPlayerAttributeType.Enraged, prev.P2Enraged ? 1 : 0, cur.P2Enraged ? 1 : 0);
            Check(EPlayerAttributeType.Tempo, prev.P2Tempo, cur.P2Tempo);
        }

        if (attrs.Count == 0) return null;
        return new CombatSimPlayerUpdate
        {
            Attributes = attrs,
        };
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

    /// <summary>
    /// 从官方 GameData.db 的 Ability.VFXConfig.VFXOverrideKey（全库 523 项技能、131 种专属弹道/斩击/激光特效）
    /// 解析当前动作的专属 VFX 键并登记进 <c>CombatSim.VfxKeys</c>，返回客户端 <c>CombatSimHandler.ExtractFrameEffects</c> 所需的 <c>VfxIndex</c>。
    /// </summary>
    private static int? ResolveVfxIndex(
        CardTemplate? tpl,
        string effectId,
        EEnchantmentType? enchantment,
        Dictionary<string, int> vfxKeyMap,
        List<string> vfxKeys,
        string? explicitOverrideKey = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitOverrideKey))
        {
            string customKey = BazaarCore.Mods.ModRuleCatalog.ResolveOfficialVfxKey(explicitOverrideKey) ?? explicitOverrideKey.Trim();
            if (!vfxKeyMap.TryGetValue(customKey, out int customIdx))
            {
                customIdx = vfxKeys.Count;
                vfxKeys.Add(customKey);
                vfxKeyMap[customKey] = customIdx;
            }
            return customIdx;
        }
        if (tpl == null || string.IsNullOrEmpty(effectId)) return null;

        string? overrideKey = null;
        if (tpl.Abilities.TryGetValue(effectId, out var ab))
        {
            overrideKey = ab.VFXConfig?.VFXOverrideKey;
        }

        if (string.IsNullOrEmpty(overrideKey) && enchantment.HasValue && tpl.Enchantments != null)
        {
            string enchName = enchantment.Value.ToString();
            if (tpl.Enchantments.TryGetValue(enchName, out var enchData) &&
                enchData.Abilities.TryGetValue(effectId, out var enchAb))
            {
                overrideKey = enchAb.VFXConfig?.VFXOverrideKey;
            }
        }

        if (string.IsNullOrEmpty(overrideKey) && tpl.Enchantments != null)
        {
            foreach (var enchData in tpl.Enchantments.Values)
            {
                if (enchData.Abilities.TryGetValue(effectId, out var enchAb) &&
                    !string.IsNullOrEmpty(enchAb.VFXConfig?.VFXOverrideKey))
                {
                    overrideKey = enchAb.VFXConfig!.VFXOverrideKey;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(overrideKey)) return null;
        string key = overrideKey.Trim();
        if (!vfxKeyMap.TryGetValue(key, out int idx))
        {
            idx = vfxKeys.Count;
            vfxKeys.Add(key);
            vfxKeyMap[key] = idx;
        }
        return idx;
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

    private static bool TryParseEnchantment(string? detail, out EEnchantmentType enchantmentType)
    {
        enchantmentType = default;
        if (string.IsNullOrWhiteSpace(detail)) return false;
        string token = detail.Trim();
        int sep = token.LastIndexOf(':');
        if (sep >= 0 && sep + 1 < token.Length) token = token[(sep + 1)..].Trim();
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
            if (Guid.TryParse(right, out _)) newTemplateId = right;
            return newTemplateId != null;
        }

        if (Guid.TryParse(trimmed, out _))
        {
            newTemplateId = trimmed;
            return true;
        }

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
        }
        return newTemplateId != null;
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
