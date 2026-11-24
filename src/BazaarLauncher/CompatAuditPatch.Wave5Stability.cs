using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarLauncher.Core;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BazaarLauncher;

/// <summary>
/// Wave 5 客户端稳定性根治补丁（全部方法/字段签名已严格核对 docs/recon 官方反编译源码）：
///   - P1-14 (RC-PEDESTAL-HAND-VISIBILITY-STRANDED)：BaseCardRevealAnimationDriver.Play Finalizer 及切入 Pedestal/Encounter/Choice 稳态点补调手牌 ShowCard(true)；
///   - P2-06 (RC-CLIENT-REVEAL-QUEUE-RACE)：WaitForCardDealVFX / ItemBoardAnimationController.FromBothSections / Play 跨 await 列表快照与空判保护；
///   - P2-07 (RC-CLIENT-REVEAL-DOUBLE-VFX-FACEDOWN)：翻牌动画收尾/Finalizer 恢复丢失的 runtimeAnimatorController、强置 CardFaceUpParam=true 与 Rig 旋转归正；
///   - P2-08 (RC-CLIENT-SCENE-UNLOAD-ASYNC-VOID-NRE)：7 处退场/切场景后 async void 与动画回调轻量存活与 Data.Run!=null 空保护；
///   - P2-09 (RC-UPGRADE-FUSE-ANIM-UNINTERRUPTED-COMBAT)：CombatState/PVPCombatState.OnEnter 兜底销毁活跃 CardFusionAnimationDriver 并归位巨型升级展示卡；
///   - P2-11 (RC-DRIVER-CLOSED-STASH-MOVE-INACTIVE)：BoardManager.HandleCardMoved Postfix 目标 Player(Hand) 兜底强置 SetActive(true) + ShowCard(true)；
///   - P3-05 (RC-PORTRAIT-FAST-EXIT-RACE)：EncounterState 进场及延迟 ClearPortrait 误删后兜底补调 EnsureCurrentEncounterPortraitAsync()。
/// </summary>
internal static partial class CompatAuditPatch
{
    // ── 诊断计数（供 GetSuppressedStats 输出与回归核验）──
    private static int _w5HandVisibilityRecoveredCount;
    private static int _w5RevealQueueSnapshotCount;
    private static int _w5RevealFaceUpRestoredCount;
    private static int _w5SceneUnloadNreGuardedCount;
    private static int _w5FusionAbortedOnCombatCount;
    private static int _w5MovedHandReactivatedCount;
    private static int _w5EncounterPortraitRecoveredCount;

    // ── 跨帧序列号与缓存（切局/切场景统一清理）──
    private static int _w5HandVisibilitySweepSeq;
    private static int _w5EncounterPortraitSeq;
    private static volatile bool _w5RecoveringEncounterPortrait;
    private static readonly object _w5AnimatorCacheLock = new();
    private static readonly Dictionary<int, object> _w5SavedRuntimeAnimators = new();

    // ── 翻牌 / 动画 / 融合驱动反射缓存 ──
    private static FieldInfo _w5RevealPlayableGraphField;
    private static FieldInfo _w5RevealAnimatorCacheField;
    private static FieldInfo _w5BaseAnimCardsForRevealField;
    private static FieldInfo _w5CardCtrlBoardSectionField;
    private static PropertyInfo _w5CardCtrlRigProp;
    private static MethodInfo _w5CardCtrlSetArtLayerDefaultMethod;
    private static MethodInfo _w5CardCtrlRestoreArtLayerMethod;
    private static MethodInfo _w5PlayableGraphIsValidMethod;
    private static MethodInfo _w5PlayableGraphDestroyMethod;
    private static PropertyInfo _w5AnimatorRuntimeCtrlProp;

    // ── 融合驱动（P2-09）反射缓存 ──
    private static FieldInfo _w5BmFusionSelectedField;
    private static FieldInfo _w5BmFusionFusedField;
    private static FieldInfo _w5BmFusionStashField;
    private static FieldInfo _w5BmCardsForFuseField;
    private static MethodInfo _w5BmSetPlayingFusionMethod;
    private static FieldInfo _w5FusionPlayableGraphField;
    private static FieldInfo _w5FusionAnimatorCacheField;

    // ── 立绘补齐（P3-05）反射缓存 ──
    private static MethodInfo _w5BmEnsurePortraitAsyncMethod;

    // ── P2-08（7 处场景卸载异步 NRE 保护）反射缓存 ──
    private static FieldInfo _w5SchSoundManagerField;
    private static FieldInfo _w5SchCarpetUnrollField;
    private static FieldInfo _w5SchLeftEndObjField;
    private static FieldInfo _w5SchRightEndObjField;
    private static FieldInfo _w5SchIsUnrollingLeftField;
    private static FieldInfo _w5SchIsUnrollingRightField;
    private static PropertyInfo _w5SmSfxPlayerProp;
    private static FieldInfo _w5SmSfxPlayerField;
    private static MethodInfo _w5SfxPlay3Method;
    private static MethodInfo _w5SfxTriggerCue3Method;

    private static FieldInfo _w5EbdBannerTextField;
    private static FieldInfo _w5EbdVictoryBannerField;
    private static FieldInfo _w5EbdDefeatBannerField;

    private static FieldInfo _w5CarpetAnimatorField;
    private static FieldInfo _w5CarpetSoundHandlerField;

    private static FieldInfo _w5TmVfxManagerField;

    private static PropertyInfo _w5HbcPropManagerProp;

    private static FieldInfo _w5RcRevealCompleteField;
    private static PropertyInfo _w5CcCardDataProp;

    private static void ClearWave5StabilityRunCaches()
    {
        Interlocked.Increment(ref _w5HandVisibilitySweepSeq);
        Interlocked.Increment(ref _w5EncounterPortraitSeq);
        _w5RecoveringEncounterPortrait = false;
        lock (_w5AnimatorCacheLock)
        {
            _w5SavedRuntimeAnimators.Clear();
        }
    }

    private static void CancelWave5Watchdogs()
    {
        Interlocked.Increment(ref _w5HandVisibilitySweepSeq);
        Interlocked.Increment(ref _w5EncounterPortraitSeq);
    }

    /// <summary>由 ApplyGameSimGroup 安装：P1-14、P2-06、P2-07、P2-08、P2-09、P2-11。</summary>
    private static void PatchWave5Stability(SafeHarmonyPatcher patcher)
    {
        try
        {
            var cardCtrlType = GameInspector.FindTypeSafe("CardController", "TheBazaar.CardController");
            if (cardCtrlType != null)
            {
                const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _w5CardCtrlBoardSectionField = cardCtrlType.GetField("boardSection", anyInst);
                _w5CardCtrlRigProp = cardCtrlType.GetProperty("Rig", anyInst);
                _w5CardCtrlSetArtLayerDefaultMethod = cardCtrlType.GetMethod("SetArtLayerDefault", anyInst, null, Type.EmptyTypes, null);
                _w5CardCtrlRestoreArtLayerMethod = cardCtrlType.GetMethod("RestoreArtLayerAfterHover", anyInst, null, Type.EmptyTypes, null);
                _w5CcCardDataProp = cardCtrlType.GetProperty("CardData", anyInst);
            }

            PatchWave5RevealQueueAndFaceUp(patcher, cardCtrlType);
            PatchWave5SceneUnloadNreGuards(patcher, cardCtrlType);
            PatchWave5CombatFusionAbort(patcher);
            PatchWave5BoardMoveAndStateGuards(patcher);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchWave5Stability: " + ex.Message);
        }
    }

    /// <summary>由 ApplyEncounterAndChallengeGroup 安装：P3-05 商人立绘快速切店竞态恢复。</summary>
    private static void PatchWave5EncounterPortraitGuard(SafeHarmonyPatcher patcher)
    {
        try
        {
            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType == null) return;

            _w5BmEnsurePortraitAsyncMethod ??= AccessTools.Method(bmType, "EnsureCurrentEncounterPortraitAsync", Type.EmptyTypes);

            var onStateChanged = AccessTools.Method(bmType, "OnStateChanged");
            if (onStateChanged != null)
            {
                patcher.TryPatchMethod(
                    onStateChanged,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5EncounterStatePortraitOnStateChangedPostfix)),
                    optional: true,
                    label: "Wave5.P3_05.BoardManager.OnStateChanged.Portrait");
            }

            var clearPortrait = AccessTools.Method(bmType, "ClearPortrait", Type.EmptyTypes);
            if (clearPortrait != null)
            {
                patcher.TryPatchMethod(
                    clearPortrait,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5ClearPortraitPostfix)),
                    optional: true,
                    label: "Wave5.P3_05.BoardManager.ClearPortrait");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchWave5EncounterPortraitGuard: " + ex.Message);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  P1-14 + P2-06 + P2-07：翻牌队列快照防越界、Play Finalizer 显形兜底与翻面归正
    // ════════════════════════════════════════════════════════════════════════

    private static void PatchWave5RevealQueueAndFaceUp(SafeHarmonyPatcher patcher, Type cardCtrlType)
    {
        try
        {
            // 1. BaseCardBoardAnimationController.WaitForCardDealVFX：跨 await 拷贝列表快照防 116 次越界异常 (P2-06)
            var baseBoardAnimType = GameInspector.FindTypeSafe("TheBazaar.BaseCardBoardAnimationController");
            if (baseBoardAnimType != null)
            {
                _w5BaseAnimCardsForRevealField = baseBoardAnimType.GetField("_cardsForReveal", BindingFlags.NonPublic | BindingFlags.Instance);
                var waitDealMethod = AccessTools.Method(baseBoardAnimType, "WaitForCardDealVFX");
                if (waitDealMethod != null && cardCtrlType != null)
                {
                    var ps = waitDealMethod.GetParameters();
                    var expectedListType = typeof(List<>).MakeGenericType(cardCtrlType);
                    if (ps.Length >= 1 && ps[0].ParameterType == expectedListType)
                    {
                        var prefixGeneric = typeof(CompatAuditPatch)
                            .GetMethod(nameof(Wave5WaitForCardDealVfxPrefixGeneric), BindingFlags.NonPublic | BindingFlags.Static)
                            ?.MakeGenericMethod(cardCtrlType);
                        if (prefixGeneric != null)
                        {
                            patcher.TryPatchMethod(
                                waitDealMethod,
                                prefix: prefixGeneric,
                                optional: true,
                                label: "Wave5.P2_06.BaseCardBoardAnimationController.WaitForCardDealVFX");
                        }
                    }
                }
            }

            // 2. ItemBoardAnimationController.FromBothSections / PlayRevealSequence / PlaySkippedRevealSequence (P2-06 + P2-07)
            var itemBoardAnimType = GameInspector.FindTypeSafe("TheBazaar.ItemBoardAnimationController");
            if (itemBoardAnimType != null)
            {
                var fromBoth = AccessTools.Method(itemBoardAnimType, "FromBothSections");
                if (fromBoth != null && cardCtrlType != null)
                {
                    var prefixGeneric = typeof(CompatAuditPatch)
                        .GetMethod(nameof(Wave5FromBothSectionsPrefixGeneric), BindingFlags.NonPublic | BindingFlags.Static)
                        ?.MakeGenericMethod(cardCtrlType);
                    if (prefixGeneric != null)
                    {
                        patcher.TryPatchMethod(
                            fromBoth,
                            prefix: prefixGeneric,
                            optional: true,
                            label: "Wave5.P2_06.ItemBoardAnimationController.FromBothSections");
                    }
                }

                var playRevealSeq = AccessTools.Method(itemBoardAnimType, "PlayRevealSequence", Type.EmptyTypes);
                if (playRevealSeq != null)
                {
                    patcher.TryPatchMethod(
                        playRevealSeq,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5ItemBoardPlayRevealPrefix)),
                        finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5ItemBoardPlayRevealFinalizer)),
                        optional: true,
                        label: "Wave5.P2_07.ItemBoardAnimationController.PlayRevealSequence");
                }

                var playSkipped = AccessTools.Method(itemBoardAnimType, "PlaySkippedRevealSequence", Type.EmptyTypes);
                if (playSkipped != null)
                {
                    patcher.TryPatchMethod(
                        playSkipped,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5ItemBoardPlaySkippedRevealPrefix)),
                        optional: true,
                        label: "Wave5.P2_06.ItemBoardAnimationController.PlaySkippedRevealSequence");
                }
            }

            // 3. BaseCardRevealAnimationDriver.Play：快照保护 + 保留 runtimeAnimatorController + Finalizer 兜底翻面/归正/手牌显形 (P1-14 + P2-06 + P2-07)
            var revealDriverType = GameInspector.FindTypeSafe("TheBazaar.Game.Cards.Animation.BaseCardRevealAnimationDriver");
            if (revealDriverType != null && cardCtrlType != null)
            {
                _w5RevealPlayableGraphField = revealDriverType.GetField("_playableGraph", BindingFlags.NonPublic | BindingFlags.Instance);
                _w5RevealAnimatorCacheField = revealDriverType.GetField("_animatorCache", BindingFlags.NonPublic | BindingFlags.Instance);

                var expectedListType = typeof(List<>).MakeGenericType(cardCtrlType);
                var playMethod = AccessTools.Method(revealDriverType, "Play", new[] { expectedListType, typeof(bool), typeof(bool) })
                    ?? AccessTools.Method(revealDriverType, "Play");
                if (playMethod != null)
                {
                    var ps = playMethod.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType == expectedListType)
                    {
                        var prefixGeneric = typeof(CompatAuditPatch)
                            .GetMethod(nameof(Wave5RevealDriverPlayPrefixGeneric), BindingFlags.NonPublic | BindingFlags.Static)
                            ?.MakeGenericMethod(cardCtrlType);
                        if (prefixGeneric != null)
                        {
                            patcher.TryPatchMethod(
                                playMethod,
                                prefix: prefixGeneric,
                                finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5RevealDriverPlayFinalizer)),
                                optional: true,
                                label: "Wave5.P1_14_P2_07.BaseCardRevealAnimationDriver.Play");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchWave5RevealQueueAndFaceUp: " + ex.Message);
        }
    }

    /// <summary>P2-06：跨 await 前将 _cardsForReveal 拷贝为独立活对象快照，彻底消除 RemoveCardFromAnimationQueues 原地删表导致的越界异常。</summary>
    private static bool Wave5WaitForCardDealVfxPrefixGeneric<TCardCtrl>(ref List<TCardCtrl> controllers, ref Task __result)
    {
        try
        {
            if (controllers == null || controllers.Count == 0)
            {
                __result = Task.CompletedTask;
                return false;
            }

            var snapshot = new List<TCardCtrl>(controllers.Count);
            for (int i = 0; i < controllers.Count; i++)
            {
                var c = controllers[i];
                if (IsUnityObjectAlive(c))
                {
                    snapshot.Add(c);
                }
            }

            if (snapshot.Count == 0)
            {
                __result = Task.CompletedTask;
                return false;
            }

            controllers = snapshot;
            Interlocked.Increment(ref _w5RevealQueueSnapshotCount);
            return true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>P2-06：空列表/单元素/含已销毁元素的 FromBothSections 安全判空，杜绝访问 cardControllers[0] 抛 ArgumentOutOfRangeException。</summary>
    private static bool Wave5FromBothSectionsPrefixGeneric<TCardCtrl>(List<TCardCtrl> cardControllers, ref bool __result)
    {
        try
        {
            if (cardControllers == null || cardControllers.Count <= 1)
            {
                __result = false;
                return false;
            }

            object firstSection = null;
            bool hasFirst = false;
            for (int i = 0; i < cardControllers.Count; i++)
            {
                var ctrl = cardControllers[i];
                if (!IsUnityObjectAlive(ctrl)) continue;

                object sec = _w5CardCtrlBoardSectionField != null
                    ? _w5CardCtrlBoardSectionField.GetValue(ctrl)
                    : GameInspector.GetMember(ctrl, "boardSection");
                if (!hasFirst)
                {
                    firstSection = sec;
                    hasFirst = true;
                }
                else if (!Equals(firstSection, sec))
                {
                    __result = true;
                    return false;
                }
            }

            __result = false;
            return false;
        }
        catch
        {
            __result = false;
            return false;
        }
    }

    private static bool Wave5ItemBoardPlayRevealPrefix(object __instance, ref Task __result, out List<object> __state)
    {
        __state = null;
        try
        {
            if (!IsUnityObjectAlive(__instance))
            {
                __result = Task.CompletedTask;
                return false;
            }

            if (_w5BaseAnimCardsForRevealField?.GetValue(__instance) is IList revealList)
            {
                var snap = new List<object>(revealList.Count);
                for (int i = revealList.Count - 1; i >= 0; i--)
                {
                    var item = revealList[i];
                    if (!IsUnityObjectAlive(item))
                    {
                        revealList.RemoveAt(i);
                        continue;
                    }
                    snap.Add(item);
                    CaptureCardRuntimeAnimatorController(item);
                }

                if (snap.Count == 0)
                {
                    __result = Task.CompletedTask;
                    return false;
                }

                __state = snap;
            }
        }
        catch { }
        return true;
    }

    private static Exception Wave5ItemBoardPlayRevealFinalizer(List<object> __state, Exception __exception, ref Task __result)
    {
        try
        {
            if (__state == null || __state.Count == 0)
                return __exception;

            if (__exception != null || __result == null || __result.IsCompleted)
            {
                MainThreadDispatcher.Enqueue(() => EnsureRevealedCardsFaceUpAndUprightOnMain(__state, "ItemBoardAnimationController.PlayRevealSequence:Sync"));
                return __exception;
            }

            __result = WrapItemBoardPlayRevealTask(__result, __state);
        }
        catch { }
        return __exception;
    }

    private static async Task WrapItemBoardPlayRevealTask(Task inner, List<object> snapshotCards)
    {
        try
        {
            await inner;
        }
        finally
        {
            MainThreadDispatcher.Enqueue(() => EnsureRevealedCardsFaceUpAndUprightOnMain(snapshotCards, "ItemBoardAnimationController.PlayRevealSequence:Async"));
        }
    }

    private static bool Wave5ItemBoardPlaySkippedRevealPrefix(object __instance, ref Task __result)
    {
        try
        {
            if (!IsUnityObjectAlive(__instance))
            {
                __result = Task.CompletedTask;
                return false;
            }

            if (_w5BaseAnimCardsForRevealField?.GetValue(__instance) is IList revealList)
            {
                for (int i = revealList.Count - 1; i >= 0; i--)
                {
                    if (!IsUnityObjectAlive(revealList[i]))
                    {
                        revealList.RemoveAt(i);
                    }
                }
                if (revealList.Count == 0)
                {
                    __result = Task.CompletedTask;
                    return false;
                }
            }
        }
        catch { }
        return true;
    }

    private static bool Wave5RevealDriverPlayPrefixGeneric<TCardCtrl>(
        object __instance,
        ref List<TCardCtrl> itemControllers,
        ref Task __result,
        out List<object> __state)
    {
        __state = null;
        try
        {
            if (__instance == null || itemControllers == null || itemControllers.Count == 0)
            {
                __result = Task.CompletedTask;
                MainThreadDispatcher.Enqueue(() => ReconcilePlayerHandVisibilityOnMain("RevealDriverPlay:EmptyInput"));
                return false;
            }

            var filtered = new List<TCardCtrl>(itemControllers.Count);
            var snapObjects = new List<object>(itemControllers.Count);
            for (int i = 0; i < itemControllers.Count; i++)
            {
                var ctrl = itemControllers[i];
                if (!IsUnityObjectAlive(ctrl)) continue;
                if (ctrl is Component comp && (comp.gameObject == null || !comp.gameObject.activeInHierarchy)) continue;

                CaptureCardRuntimeAnimatorController(ctrl);
                filtered.Add(ctrl);
                snapObjects.Add(ctrl);
            }

            if (filtered.Count == 0)
            {
                __result = Task.CompletedTask;
                MainThreadDispatcher.Enqueue(() => ReconcilePlayerHandVisibilityOnMain("RevealDriverPlay:AllInactive"));
                return false;
            }

            itemControllers = filtered;
            __state = snapObjects;
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static Exception Wave5RevealDriverPlayFinalizer(
        object __instance,
        List<object> __state,
        Exception __exception,
        ref Task __result)
    {
        try
        {
            // 若 Prefix 因空/全失活列表短路，不干扰可能仍在运行的其他 PlayableGraph
            if (__state == null && __exception == null)
                return null;

            if (__exception != null || __result == null || __result.IsCompleted)
            {
                MainThreadDispatcher.Enqueue(() => FinalizeRevealDriverOnMain(__instance, __state, "BaseCardRevealAnimationDriver.Play:Sync"));
                return __exception;
            }

            __result = WrapRevealDriverPlayTask(__result, __instance, __state);
        }
        catch { }
        return __exception;
    }

    private static async Task WrapRevealDriverPlayTask(Task inner, object driver, List<object> snapshotCards)
    {
        try
        {
            await inner;
        }
        finally
        {
            MainThreadDispatcher.Enqueue(() => FinalizeRevealDriverOnMain(driver, snapshotCards, "BaseCardRevealAnimationDriver.Play:Async"));
        }
    }

    private static void FinalizeRevealDriverOnMain(object driver, List<object> snapshotCards, string reason)
    {
        try
        {
            // 1. 若 Play 内部发生异常跳过末尾销毁，兜底释放残余 PlayableGraph
            if (driver != null && _w5RevealPlayableGraphField != null)
            {
                DestroyNullablePlayableGraphField(driver, _w5RevealPlayableGraphField);
                RestoreAnimatorCacheDictionary(driver, _w5RevealAnimatorCacheField);
            }

            // 2. P2-07：强制校验已翻牌卡牌的 runtimeAnimatorController、CardFaceUpParam=true、Rig 姿态归正与默认图层
            if (snapshotCards != null && snapshotCards.Count > 0)
            {
                EnsureRevealedCardsFaceUpAndUprightOnMain(snapshotCards, reason);
            }

            // 3. P1-14：翻牌驱动结束后兜底扫描玩家手牌中 activeInHierarchy && !IsCardVisible 的滞留隐身卡并补调 ShowCard(true)
            ReconcilePlayerHandVisibilityOnMain(reason);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.FinalizeRevealDriverOnMain: " + ex.Message);
        }
    }

    private static void CaptureCardRuntimeAnimatorController(object ctrl)
    {
        if (ctrl is not Component comp || comp == null) return;
        try
        {
            EnsureCardControllerVisibleCache(ctrl);
            var anim = _ufCardCtrlAnimatorField != null
                ? _ufCardCtrlAnimatorField.GetValue(ctrl)
                : _ufCardCtrlAnimatorProp?.GetValue(ctrl);
            if (!IsUnityObjectAlive(anim)) return;

            _w5AnimatorRuntimeCtrlProp ??= anim.GetType().GetProperty("runtimeAnimatorController", BindingFlags.Public | BindingFlags.Instance);
            var runtimeCtrl = _w5AnimatorRuntimeCtrlProp?.GetValue(anim);
            if (!IsUnityObjectAlive(runtimeCtrl)) return;

            int id = comp.GetInstanceID();
            lock (_w5AnimatorCacheLock)
            {
                if (_w5SavedRuntimeAnimators.Count > 512)
                {
                    _w5SavedRuntimeAnimators.Clear();
                }
                _w5SavedRuntimeAnimators[id] = runtimeCtrl;
            }
        }
        catch { }
    }

    private static bool RestoreCardRuntimeAnimatorControllerIfMissing(object ctrl)
    {
        if (ctrl is not Component comp || comp == null) return false;
        try
        {
            EnsureCardControllerVisibleCache(ctrl);
            var anim = _ufCardCtrlAnimatorField != null
                ? _ufCardCtrlAnimatorField.GetValue(ctrl)
                : _ufCardCtrlAnimatorProp?.GetValue(ctrl);
            if (!IsUnityObjectAlive(anim)) return false;

            _w5AnimatorRuntimeCtrlProp ??= anim.GetType().GetProperty("runtimeAnimatorController", BindingFlags.Public | BindingFlags.Instance);
            if (_w5AnimatorRuntimeCtrlProp == null) return false;

            var curRuntime = _w5AnimatorRuntimeCtrlProp.GetValue(anim);
            if (IsUnityObjectAlive(curRuntime)) return false;

            object saved = null;
            int id = comp.GetInstanceID();
            lock (_w5AnimatorCacheLock)
            {
                _w5SavedRuntimeAnimators.TryGetValue(id, out saved);
            }
            if (IsUnityObjectAlive(saved))
            {
                _w5AnimatorRuntimeCtrlProp.SetValue(anim, saved);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static void RestoreAnimatorCacheDictionary(object driver, FieldInfo cacheField)
    {
        if (driver == null || cacheField == null) return;
        try
        {
            if (cacheField.GetValue(driver) is not IDictionary dict || dict.Count == 0) return;
            foreach (DictionaryEntry kv in dict)
            {
                var ctrl = kv.Key;
                var savedRc = kv.Value;
                if (!IsUnityObjectAlive(ctrl) || !IsUnityObjectAlive(savedRc)) continue;
                EnsureCardControllerVisibleCache(ctrl);
                var anim = _ufCardCtrlAnimatorField != null
                    ? _ufCardCtrlAnimatorField.GetValue(ctrl)
                    : _ufCardCtrlAnimatorProp?.GetValue(ctrl);
                if (!IsUnityObjectAlive(anim)) continue;
                _w5AnimatorRuntimeCtrlProp ??= anim.GetType().GetProperty("runtimeAnimatorController", BindingFlags.Public | BindingFlags.Instance);
                if (_w5AnimatorRuntimeCtrlProp != null && !IsUnityObjectAlive(_w5AnimatorRuntimeCtrlProp.GetValue(anim)))
                {
                    _w5AnimatorRuntimeCtrlProp.SetValue(anim, savedRc);
                }
            }
            dict.Clear();
        }
        catch { }
    }

    private static bool DestroyNullablePlayableGraphField(object owner, FieldInfo graphField)
    {
        if (owner == null || graphField == null) return false;
        try
        {
            object boxedGraph = graphField.GetValue(owner);
            if (boxedGraph == null) return false;

            var gt = boxedGraph.GetType();
            _w5PlayableGraphIsValidMethod ??= gt.GetMethod("IsValid", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            _w5PlayableGraphDestroyMethod ??= gt.GetMethod("Destroy", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

            bool wasValid = _w5PlayableGraphIsValidMethod != null
                && _w5PlayableGraphIsValidMethod.Invoke(boxedGraph, null) is bool v && v;
            if (wasValid)
            {
                _w5PlayableGraphDestroyMethod?.Invoke(boxedGraph, null);
            }
            graphField.SetValue(owner, null);
            return wasValid;
        }
        catch
        {
            return false;
        }
    }

    private static bool ResetCardRigPoseIfSkewed(object ctrl, bool forceReset)
    {
        if (!IsUnityObjectAlive(ctrl)) return false;
        try
        {
            Transform rig = _w5CardCtrlRigProp?.GetValue(ctrl) as Transform;
            if (rig == null) return false;

            bool skewed = forceReset
                || Quaternion.Angle(rig.localRotation, Quaternion.identity) > 0.5f
                || rig.localPosition.sqrMagnitude > 1e-4f
                || (rig.localScale - Vector3.one).sqrMagnitude > 1e-4f;

            Transform child0 = rig.childCount > 0 ? rig.GetChild(0) : null;
            if (!skewed && child0 != null)
            {
                skewed = Quaternion.Angle(child0.localRotation, Quaternion.identity) > 0.5f
                    || child0.localPosition.sqrMagnitude > 1e-4f
                    || (child0.localScale - Vector3.one).sqrMagnitude > 1e-4f;
            }

            if (!skewed) return false;

            rig.localPosition = Vector3.zero;
            rig.localRotation = Quaternion.identity;
            rig.localScale = Vector3.one;
            if (child0 != null)
            {
                child0.localPosition = Vector3.zero;
                child0.localRotation = Quaternion.identity;
                child0.localScale = Vector3.one;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// P2-07（RC-CLIENT-REVEAL-DOUBLE-VFX-FACEDOWN）：翻牌收尾或被中途打断后，
    /// 兜底恢复丢失的 runtimeAnimatorController、强置 FaceUp=true、归正 Rig 旋转与图层并确保可见。
    /// </summary>
    private static void EnsureRevealedCardsFaceUpAndUprightOnMain(List<object> cards, string reason)
    {
        if (cards == null || cards.Count == 0) return;
        try
        {
            int fixedCount = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                var ctrl = cards[i];
                if (ctrl is not Component comp || comp == null || comp.gameObject == null || !comp.gameObject.activeInHierarchy)
                    continue;

                // 跳过正在拖拽中的卡牌，避免干扰玩家拖拽倾角
                if (string.Equals(ctrl.GetType().Name, "ItemController", StringComparison.Ordinal)
                    && GameInspector.GetMember(ctrl, "IsDragging") is bool dragging && dragging)
                {
                    continue;
                }

                bool restoredAnim = RestoreCardRuntimeAnimatorControllerIfMissing(ctrl);
                bool resetPose = ResetCardRigPoseIfSkewed(ctrl, forceReset: restoredAnim);

                EnsureCardControllerVisibleCache(ctrl);
                bool wasInvisible = _ufCardCtrlIsVisibleProp?.GetValue(ctrl) is bool vis && !vis;
                ForceShowCardController(ctrl);

                if (_w5CardCtrlSetArtLayerDefaultMethod != null && _w5CardCtrlSetArtLayerDefaultMethod.DeclaringType.IsInstanceOfType(ctrl))
                {
                    try { _w5CardCtrlSetArtLayerDefaultMethod.Invoke(ctrl, null); } catch { }
                }

                if (restoredAnim || resetPose || wasInvisible)
                {
                    fixedCount++;
                }
            }

            if (fixedCount > 0)
            {
                Interlocked.Add(ref _w5RevealFaceUpRestoredCount, fixedCount);
                BazaarLauncherPlugin.Log?.LogInfo(
                    $"CompatAuditPatch[P2-07]: 翻牌收尾兜底恢复 {fixedCount} 张卡牌 FaceUp=true / 旋转归正 / 动画控制器 ({reason})");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.EnsureRevealedCardsFaceUpAndUprightOnMain: " + ex.Message);
        }
    }

    /// <summary>
    /// P1-14（RC-PEDESTAL-HAND-VISIBILITY-STRANDED）：在翻牌结束及切入 PedestalState/EncounterState/ChoiceState 稳态点，
    /// 对所有位于玩家 Hand 且 activeInHierarchy && !IsCardVisible 的卡牌统一补调 ShowCard(true) 与 SetArtLayerDefault()。
    /// </summary>
    private static void ReconcilePlayerHandVisibilityOnMain(string reason)
    {
        try
        {
            var curAppState = GameInspector.GetCurrentAppState();
            string appStateName = curAppState?.GetType().Name ?? "";
            if (appStateName is not ("PedestalState" or "EncounterState" or "ChoiceState" or "TournamentPvpChoiceState" or "LevelUpState" or "LootState"))
                return;

            if (IsNewDayTransitionActiveOrPending())
                return;

            var run = GameInspector.GetDataRun();
            var player = run != null ? GameInspector.GetMember(run, "Player") : null;
            var entities = GameInspector.GetDataEntities();
            var lookup = GetStaticDataMember("CardAndSkillLookup");
            if (player == null || entities == null || lookup == null) return;

            _getCardControllerMethod ??= lookup.GetType().GetMethod("GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_getCardControllerMethod == null) return;

            int recovered = 0;
            foreach (DictionaryEntry kv in entities)
            {
                var cardObj = kv.Value;
                if (cardObj is not ICard ic || ic.Type != ECardType.Item) continue;
                if (!ReferenceEquals(GameInspector.GetMember(cardObj, "Owner"), player)) continue;
                if (GameInspector.GetMember(cardObj, "Section") is not EInventorySection sec || sec != EInventorySection.Hand)
                    continue;

                var ctrl = _getCardControllerMethod.Invoke(lookup, new[] { cardObj });
                if (ctrl is not Component comp || comp == null || comp.gameObject == null || !comp.gameObject.activeInHierarchy)
                    continue;

                EnsureCardControllerVisibleCache(ctrl);
                if (_ufCardCtrlIsVisibleProp?.GetValue(ctrl) is bool visible && !visible)
                {
                    RestoreCardRuntimeAnimatorControllerIfMissing(ctrl);
                    ResetCardRigPoseIfSkewed(ctrl, forceReset: false);
                    ForceShowCardController(ctrl);
                    if (_w5CardCtrlSetArtLayerDefaultMethod != null && _w5CardCtrlSetArtLayerDefaultMethod.DeclaringType.IsInstanceOfType(ctrl))
                    {
                        try { _w5CardCtrlSetArtLayerDefaultMethod.Invoke(ctrl, null); } catch { }
                    }
                    recovered++;
                }
            }

            if (recovered > 0)
            {
                Interlocked.Add(ref _w5HandVisibilityRecoveredCount, recovered);
                BazaarLauncherPlugin.Log?.LogInfo(
                    $"CompatAuditPatch[P1-14]: 稳态点补调 ShowCard(true) 恢复 {recovered} 张隐身手牌 (state={appStateName}, reason={reason})");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ReconcilePlayerHandVisibilityOnMain: " + ex.Message);
        }
    }

    private static void ScheduleWave5HandVisibilitySettleSweep(string stateName)
    {
        try
        {
            int seq = Interlocked.Increment(ref _w5HandVisibilitySweepSeq);
            var bm = GameInspector.GetBoardManagerCached();
            if (IsUnityObjectAlive(bm) && !IsBoardManagerBusy(bm))
            {
                ReconcilePlayerHandVisibilityOnMain("StateEnterImmediate:" + stateName);
            }

            foreach (int delayMs in new[] { 350, 1100 })
            {
                _ = Task.Delay(delayMs).ContinueWith(_ =>
                {
                    if (Volatile.Read(ref _w5HandVisibilitySweepSeq) != seq) return;
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        if (Volatile.Read(ref _w5HandVisibilitySweepSeq) != seq) return;
                        var curBm = GameInspector.GetBoardManagerCached();
                        if (!IsUnityObjectAlive(curBm) || IsBoardManagerBusy(curBm)) return;
                        ReconcilePlayerHandVisibilityOnMain($"StateEnterSettled({delayMs}ms):{stateName}");
                    });
                }, TaskScheduler.Default);
            }
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  P2-08：退场/切场景后 7 处 async void / 动画回调轻量存活与 Data.Run!=null 保护
    // ════════════════════════════════════════════════════════════════════════

    private static void PatchWave5SceneUnloadNreGuards(SafeHarmonyPatcher patcher, Type cardCtrlType)
    {
        const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // 1. SoundCarpetHandler.PlayCarpetUnroll(string targetState, int cueDelay)
        try
        {
            var schType = GameInspector.FindTypeSafe("TheBazaar.SoundCarpetHandler", "SoundCarpetHandler");
            if (schType != null)
            {
                _w5SchSoundManagerField = schType.GetField("_soundManager", anyInst);
                _w5SchCarpetUnrollField = schType.GetField("CarpetUnroll", anyInst);
                _w5SchLeftEndObjField = schType.GetField("LeftEndObject", anyInst);
                _w5SchRightEndObjField = schType.GetField("RightEndObject", anyInst);
                _w5SchIsUnrollingLeftField = schType.GetField("IsUnrollingLeft", anyInst);
                _w5SchIsUnrollingRightField = schType.GetField("IsUnrollingRight", anyInst);

                var playUnroll = AccessTools.Method(schType, "PlayCarpetUnroll", new[] { typeof(string), typeof(int) });
                if (playUnroll != null)
                {
                    patcher.TryPatchMethod(
                        playUnroll,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5SoundCarpetPlayUnrollPrefix)),
                        optional: true,
                        label: "Wave5.P2_08.SoundCarpetHandler.PlayCarpetUnroll");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.Wave5.SoundCarpetHandler: " + ex.Message);
        }

        // 2. EndBattleDialog.ShowBanner & EndBattleDialog.Reset (+ async void 状态机退场 NRE 收口)
        try
        {
            var ebdType = AccessTools.TypeByName("EndBattleDialog")
                ?? GameInspector.FindTypeSafe("EndBattleDialog", "TheBazaar.EndBattleDialog");
            if (ebdType != null)
            {
                _w5EbdBannerTextField = ebdType.GetField("bannerText", anyInst);
                _w5EbdVictoryBannerField = ebdType.GetField("victoryBanner", anyInst);
                _w5EbdDefeatBannerField = ebdType.GetField("defeatBanner", anyInst);

                var resetMethod = AccessTools.Method(ebdType, "Reset", Type.EmptyTypes);
                if (resetMethod != null)
                {
                    patcher.TryPatchMethod(
                        resetMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5EndBattleDialogResetPrefix)),
                        optional: true,
                        label: "Wave5.P2_08.EndBattleDialog.Reset");
                }

                var showBanner = AccessTools.Method(ebdType, "ShowBanner");
                if (showBanner != null)
                {
                    patcher.TryPatchMethod(
                        showBanner,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5EndBattleDialogShowBannerPrefix)),
                        optional: true,
                        label: "Wave5.P2_08.EndBattleDialog.ShowBanner");
                }

                PatchAsyncVoidStateMachineUnloadFinalizer(patcher, ebdType, "ShowBanner", "Wave5.P2_08.EndBattleDialog.ShowBanner.MoveNext");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.Wave5.EndBattleDialog: " + ex.Message);
        }

        // 3. CarpetController（UnlockPlayerSockets / UnlockSocketsBitmask / UpdateUnrollState / AnimateCarpetToUnlocks / AnimateCarpetsToUnlockInstant / HandleOnLevelUpVisuals）
        try
        {
            var carpetType = GameInspector.FindTypeSafe("TheBazaar.CarpetController", "CarpetController");
            if (carpetType != null)
            {
                _w5CarpetAnimatorField = carpetType.GetField("CarpetAnimator", anyInst);
                _w5CarpetSoundHandlerField = carpetType.GetField("soundCarpetHandler", anyInst);

                var unlockPlayer = AccessTools.Method(carpetType, "UnlockPlayerSockets", Type.EmptyTypes);
                if (unlockPlayer != null)
                {
                    patcher.TryPatchMethod(
                        unlockPlayer,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5CarpetUnlockPlayerSocketsPrefix)),
                        finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5TaskUnloadNreFinalizer)),
                        optional: true,
                        label: "Wave5.P2_08.CarpetController.UnlockPlayerSockets");
                }

                var unlockBitmask = AccessTools.Method(carpetType, "UnlockSocketsBitmask", new[] { typeof(SocketedContainer), typeof(bool) });
                if (unlockBitmask != null)
                {
                    patcher.TryPatchMethod(
                        unlockBitmask,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5CarpetUnlockBitmaskPrefix)),
                        finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5TaskUnloadNreFinalizer)),
                        optional: true,
                        label: "Wave5.P2_08.CarpetController.UnlockSocketsBitmask");
                }

                var updateUnroll = AccessTools.Method(carpetType, "UpdateUnrollState", new[] { typeof(int), typeof(int), typeof(int) });
                if (updateUnroll != null)
                {
                    patcher.TryPatchMethod(
                        updateUnroll,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5CarpetAnimatorAliveTaskPrefix)),
                        finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5TaskUnloadNreFinalizer)),
                        optional: true,
                        label: "Wave5.P2_08.CarpetController.UpdateUnrollState");
                }

                var animToUnlocks = AccessTools.Method(carpetType, "AnimateCarpetToUnlocks", new[] { typeof(int), typeof(int) });
                if (animToUnlocks != null)
                {
                    patcher.TryPatchMethod(
                        animToUnlocks,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5CarpetAnimateToUnlocksPrefix)),
                        finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5TaskUnloadNreFinalizer)),
                        optional: true,
                        label: "Wave5.P2_08.CarpetController.AnimateCarpetToUnlocks");
                }

                var animInstant = AccessTools.Method(carpetType, "AnimateCarpetsToUnlockInstant", new[] { typeof(int), typeof(int) });
                if (animInstant != null)
                {
                    patcher.TryPatchMethod(
                        animInstant,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5CarpetAnimatorAliveVoidPrefix)),
                        optional: true,
                        label: "Wave5.P2_08.CarpetController.AnimateCarpetsToUnlockInstant");
                }

                var handleLvl = AccessTools.Method(carpetType, "HandleOnLevelUpVisuals", new[] { typeof(bool) });
                if (handleLvl != null)
                {
                    patcher.TryPatchMethod(
                        handleLvl,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5RequireAliveAndRunVoidPrefix)),
                        optional: true,
                        label: "Wave5.P2_08.CarpetController.HandleOnLevelUpVisuals");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.Wave5.CarpetController: " + ex.Message);
        }

        // 4. ClearBoardTriggerComponent.Show & HandleNewDayTransition (+ Show 状态机退场 NRE 收口)
        try
        {
            var clearBoardType = GameInspector.FindTypeSafe("TheBazaar.SequenceFramework.VisualSequences.ClearBoardTriggerComponent");
            if (clearBoardType != null)
            {
                var showMethod = AccessTools.Method(clearBoardType, "Show", Type.EmptyTypes);
                if (showMethod != null)
                {
                    patcher.TryPatchMethod(
                        showMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5ClearBoardShowPrefix)),
                        optional: true,
                        label: "Wave5.P2_08.ClearBoardTriggerComponent.Show");
                }

                PatchAsyncVoidStateMachineUnloadFinalizer(patcher, clearBoardType, "Show", "Wave5.P2_08.ClearBoardTriggerComponent.Show.MoveNext");

                var newDayMethod = AccessTools.Method(clearBoardType, "HandleNewDayTransition", Type.EmptyTypes);
                if (newDayMethod != null)
                {
                    patcher.TryPatchMethod(
                        newDayMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5RequireAliveAndRunTaskPrefix)),
                        finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5TaskUnloadNreFinalizer)),
                        optional: true,
                        label: "Wave5.P2_08.ClearBoardTriggerComponent.HandleNewDayTransition");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.Wave5.ClearBoardTriggerComponent: " + ex.Message);
        }

        // 5. TransformManager.StartTransformVFX（泛型 Task<VFXLoopController> 保护）+ OnCombatCardTransformed / OnCardTransformReverted
        try
        {
            var tmType = GameInspector.FindTypeSafe("TheBazaar.Managers.TransformManager", "TransformManager");
            if (tmType != null)
            {
                _w5TmVfxManagerField = tmType.GetField("_vfxManager", anyInst);

                var startVfx = AccessTools.Method(tmType, "StartTransformVFX");
                if (startVfx != null && startVfx.ReturnType.IsGenericType && startVfx.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var retElemType = startVfx.ReturnType.GetGenericArguments()[0];
                    var prefixGen = typeof(CompatAuditPatch)
                        .GetMethod(nameof(Wave5StartTransformVfxPrefixGeneric), BindingFlags.NonPublic | BindingFlags.Static)
                        ?.MakeGenericMethod(retElemType);
                    var postfixGen = typeof(CompatAuditPatch)
                        .GetMethod(nameof(Wave5StartTransformVfxPostfixGeneric), BindingFlags.NonPublic | BindingFlags.Static)
                        ?.MakeGenericMethod(retElemType);
                    if (prefixGen != null && postfixGen != null)
                    {
                        patcher.TryPatchMethod(
                            startVfx,
                            prefix: prefixGen,
                            postfix: postfixGen,
                            optional: true,
                            label: "Wave5.P2_08.TransformManager.StartTransformVFX");
                    }
                }

                var onTrans = tmType.GetMethods(anyInst).FirstOrDefault(m => m.Name == "OnCombatCardTransformed" && m.GetParameters().Length == 1);
                if (onTrans != null)
                {
                    patcher.TryPatchMethod(
                        onTrans,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5RequireAliveAndRunVoidPrefix)),
                        optional: true,
                        label: "Wave5.P2_08.TransformManager.OnCombatCardTransformed");
                }

                var onRev = tmType.GetMethods(anyInst).FirstOrDefault(m => m.Name == "OnCardTransformReverted" && m.GetParameters().Length == 1);
                if (onRev != null)
                {
                    patcher.TryPatchMethod(
                        onRev,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5RequireAliveAndRunVoidPrefix)),
                        optional: true,
                        label: "Wave5.P2_08.TransformManager.OnCardTransformReverted");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.Wave5.TransformManager: " + ex.Message);
        }

        // 6. HeroBoardController.RefreshPropManager（await Task.Yield() 后组件销毁 NRE 防护）
        try
        {
            var hbcType = AccessTools.TypeByName("HeroBoardController")
                ?? GameInspector.FindTypeSafe("HeroBoardController", "TheBazaar.HeroBoardController");
            if (hbcType != null)
            {
                _w5HbcPropManagerProp = hbcType.GetProperty("PropManager", anyInst);
                var refreshMethod = AccessTools.Method(hbcType, "RefreshPropManager", Type.EmptyTypes);
                if (refreshMethod != null)
                {
                    patcher.TryPatchMethod(
                        refreshMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5HeroBoardRefreshPropManagerPrefix)),
                        optional: true,
                        label: "Wave5.P2_08.HeroBoardController.RefreshPropManager");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.Wave5.HeroBoardController: " + ex.Message);
        }

        // 7. RewardController.OnSpawnComplete & CardController.UpdatePriceTag（含 RewardController 继承调用链）
        try
        {
            var rcType = AccessTools.TypeByName("RewardController")
                ?? GameInspector.FindTypeSafe("RewardController", "TheBazaar.RewardController");
            if (rcType != null)
            {
                _w5RcRevealCompleteField = rcType.GetField("revealComplete", anyInst);
                var onSpawnComplete = AccessTools.Method(rcType, "OnSpawnComplete", new[] { typeof(string) });
                if (onSpawnComplete != null)
                {
                    patcher.TryPatchMethod(
                        onSpawnComplete,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5RewardOnSpawnCompletePrefix)),
                        optional: true,
                        label: "Wave5.P2_08.RewardController.OnSpawnComplete");
                }
            }

            if (cardCtrlType != null)
            {
                var updatePriceTag = AccessTools.Method(cardCtrlType, "UpdatePriceTag", new[] { typeof(ERunState) });
                if (updatePriceTag != null)
                {
                    patcher.TryPatchMethod(
                        updatePriceTag,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5CardControllerUpdatePriceTagPrefix)),
                        optional: true,
                        label: "Wave5.P2_08.CardController.UpdatePriceTag");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.Wave5.RewardAndPriceTag: " + ex.Message);
        }
    }

    private static bool IsInGameplaySceneSafe()
    {
        try
        {
            string sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
            return sceneName.IndexOf("Gameplay", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSceneUnloadingOrNoRun()
        => GameInspector.GetDataRun() == null
           || !IsInGameplaySceneSafe()
           || !IsUnityObjectAlive(GameInspector.GetBoardManagerCached());

    private static void PatchAsyncVoidStateMachineUnloadFinalizer(
        SafeHarmonyPatcher patcher,
        Type declaringType,
        string methodName,
        string label)
    {
        try
        {
            string prefix = "<" + methodName + ">d__";
            var smType = declaringType
                .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(nt => nt.Name.StartsWith(prefix, StringComparison.Ordinal)
                    && typeof(IAsyncStateMachine).IsAssignableFrom(nt));
            if (smType == null) return;

            var moveNext = smType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (moveNext != null)
            {
                patcher.TryPatchMethod(
                    moveNext,
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5AsyncVoidUnloadNreFinalizer)),
                    optional: true,
                    label: label);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch.PatchAsyncVoidStateMachineUnloadFinalizer({label}): {ex.Message}");
        }
    }

    private static Exception Wave5AsyncVoidUnloadNreFinalizer(Exception __exception)
    {
        if (__exception == null) return null;
        if ((__exception is NullReferenceException or MissingReferenceException) && IsSceneUnloadingOrNoRun())
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            return null;
        }
        return __exception;
    }

    private static bool Wave5RequireAliveAndRunVoidPrefix(object __instance)
    {
        if (!IsUnityObjectAlive(__instance) || GameInspector.GetDataRun() == null)
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            return false;
        }
        return true;
    }

    private static bool Wave5RequireAliveAndRunTaskPrefix(object __instance, ref Task __result)
    {
        if (!IsUnityObjectAlive(__instance) || GameInspector.GetDataRun() == null)
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            __result = Task.CompletedTask;
            return false;
        }
        return true;
    }

    private static Exception Wave5TaskUnloadNreFinalizer(object __instance, Exception __exception, ref Task __result)
    {
        if (__exception != null)
        {
            if ((__exception is NullReferenceException or MissingReferenceException)
                && (!IsUnityObjectAlive(__instance) || IsSceneUnloadingOrNoRun()))
            {
                Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
                __result = Task.CompletedTask;
                return null;
            }
            return __exception;
        }

        if (__result != null && !__result.IsCompleted)
        {
            __result = WrapUnloadSafeTask(__result, __instance);
        }
        else if (__result != null && __result.IsFaulted)
        {
            var baseEx = __result.Exception?.GetBaseException();
            if ((baseEx is NullReferenceException or MissingReferenceException)
                && (!IsUnityObjectAlive(__instance) || IsSceneUnloadingOrNoRun()))
            {
                Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
                __result = Task.CompletedTask;
            }
        }
        return null;
    }

    private static async Task WrapUnloadSafeTask(Task inner, object owner)
    {
        try
        {
            await inner;
        }
        catch (Exception ex) when ((ex is NullReferenceException or MissingReferenceException)
            && (!IsUnityObjectAlive(owner) || IsSceneUnloadingOrNoRun()))
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
        }
    }

    // ── P2-08 #1：SoundCarpetHandler.PlayCarpetUnroll 安全异步替换 ──
    private static bool Wave5SoundCarpetPlayUnrollPrefix(object __instance, string targetState, int cueDelay)
    {
        try
        {
            if (!IsUnityObjectAlive(__instance) || GameInspector.GetDataRun() == null)
            {
                Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
                return false;
            }

            if (_w5SchSoundManagerField == null || _w5SchCarpetUnrollField == null
                || _w5SchLeftEndObjField == null || _w5SchRightEndObjField == null
                || _w5SchIsUnrollingLeftField == null || _w5SchIsUnrollingRightField == null)
            {
                return true;
            }

            var soundMgr = _w5SchSoundManagerField.GetValue(__instance);
            var sfxPlayer = ResolveSfxPlayer(soundMgr);
            if (!IsUnityObjectAlive(soundMgr) || !IsUnityObjectAlive(sfxPlayer))
            {
                Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
                return false;
            }

            EnsureSfxPlayerMethods(sfxPlayer.GetType());
            if (_w5SfxPlay3Method == null || _w5SfxTriggerCue3Method == null)
            {
                return true;
            }

            _ = SafePlayCarpetUnrollAsync(__instance, targetState, cueDelay);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static object ResolveSfxPlayer(object soundMgr)
    {
        if (soundMgr == null) return null;
        var smt = soundMgr.GetType();
        if (_w5SmSfxPlayerProp == null && _w5SmSfxPlayerField == null)
        {
            _w5SmSfxPlayerProp = smt.GetProperty("SFXPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_w5SmSfxPlayerProp == null)
            {
                _w5SmSfxPlayerField = smt.GetField("SFXPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
        }
        return _w5SmSfxPlayerProp != null ? _w5SmSfxPlayerProp.GetValue(soundMgr) : _w5SmSfxPlayerField?.GetValue(soundMgr);
    }

    private static void EnsureSfxPlayerMethods(Type sfxPlayerType)
    {
        if (_w5SfxPlay3Method != null && _w5SfxTriggerCue3Method != null) return;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var m in sfxPlayerType.GetMethods(flags))
        {
            if (_w5SfxPlay3Method == null && m.Name == "PlaySfx")
            {
                var ps = m.GetParameters();
                if (ps.Length == 3 && ps[1].ParameterType == typeof(GameObject) && ps[2].ParameterType == typeof(string))
                {
                    _w5SfxPlay3Method = m;
                }
            }
            else if (_w5SfxTriggerCue3Method == null && m.Name == "SfxTriggerCue")
            {
                var ps = m.GetParameters();
                if (ps.Length == 3 && ps[1].ParameterType == typeof(bool) && ps[2].ParameterType == typeof(string))
                {
                    _w5SfxTriggerCue3Method = m;
                }
            }
        }
    }

    private static async Task SafePlayCarpetUnrollAsync(object handler, string targetState, int cueDelay)
    {
        FieldInfo flagField;
        FieldInfo endObjField;
        if (string.Equals(targetState, "TargetState_L", StringComparison.Ordinal))
        {
            flagField = _w5SchIsUnrollingLeftField;
            endObjField = _w5SchLeftEndObjField;
        }
        else if (string.Equals(targetState, "TargetState_R", StringComparison.Ordinal))
        {
            flagField = _w5SchIsUnrollingRightField;
            endObjField = _w5SchRightEndObjField;
        }
        else
        {
            return;
        }

        try
        {
            if (!IsUnityObjectAlive(handler)) return;
            if (flagField.GetValue(handler) is bool already && already) return;

            var soundMgr = _w5SchSoundManagerField.GetValue(handler);
            var sfxPlayer = ResolveSfxPlayer(soundMgr);
            if (!IsUnityObjectAlive(soundMgr) || !IsUnityObjectAlive(sfxPlayer)) return;

            object carpetEventRef = _w5SchCarpetUnrollField.GetValue(handler);
            var endObj = endObjField.GetValue(handler) as GameObject;
            _w5SfxPlay3Method.Invoke(sfxPlayer, new object[] { carpetEventRef, endObj, targetState });
            flagField.SetValue(handler, true);

            if (cueDelay > 0)
            {
                await Task.Delay(cueDelay);
            }

            // 切场景/终局退主菜单后组件已销毁或 Data.Run 已清空：安全早退，杜绝 0.35s 延迟 NRE
            if (!IsUnityObjectAlive(handler) || GameInspector.GetDataRun() == null)
            {
                Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
                return;
            }

            var soundMgrAfter = _w5SchSoundManagerField.GetValue(handler);
            var sfxPlayerAfter = ResolveSfxPlayer(soundMgrAfter);
            if (!IsUnityObjectAlive(soundMgrAfter) || !IsUnityObjectAlive(sfxPlayerAfter))
            {
                flagField.SetValue(handler, false);
                Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
                return;
            }

            object carpetEventRefAfter = _w5SchCarpetUnrollField.GetValue(handler);
            _w5SfxTriggerCue3Method.Invoke(sfxPlayerAfter, new object[] { carpetEventRefAfter, true, targetState });
            flagField.SetValue(handler, false);
        }
        catch (Exception ex) when (ex is NullReferenceException or MissingReferenceException or TargetInvocationException)
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
        }
        catch { }
    }

    // ── P2-08 #2：EndBattleDialog.Reset / ShowBanner 销毁空保护 ──
    private static bool Wave5EndBattleDialogResetPrefix(object __instance)
    {
        try
        {
            if (!IsUnityObjectAlive(__instance))
            {
                Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
                return false;
            }

            var bannerText = _w5EbdBannerTextField?.GetValue(__instance) as TMP_Text;
            var victoryBanner = _w5EbdVictoryBannerField?.GetValue(__instance) as Component;
            var defeatBanner = _w5EbdDefeatBannerField?.GetValue(__instance) as Component;
            if (bannerText == null || victoryBanner == null || defeatBanner == null)
            {
                if (bannerText != null)
                {
                    Color c = bannerText.color;
                    c.a = 0f;
                    bannerText.color = c;
                }
                if (victoryBanner != null && victoryBanner.gameObject != null) victoryBanner.gameObject.SetActive(false);
                if (defeatBanner != null && defeatBanner.gameObject != null) defeatBanner.gameObject.SetActive(false);
                Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Wave5EndBattleDialogShowBannerPrefix(object __instance)
    {
        if (!IsUnityObjectAlive(__instance) || GameInspector.GetDataRun() == null)
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            return false;
        }
        var victoryBanner = _w5EbdVictoryBannerField?.GetValue(__instance) as Component;
        var defeatBanner = _w5EbdDefeatBannerField?.GetValue(__instance) as Component;
        if (victoryBanner == null || defeatBanner == null)
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            return false;
        }
        return true;
    }

    // ── P2-08 #3：CarpetController 存活与 Animator 空保护 ──
    private static bool Wave5CarpetUnlockPlayerSocketsPrefix(object __instance, ref Task __result)
    {
        if (!IsUnityObjectAlive(__instance)
            || GameInspector.GetDataRun() == null
            || !IsUnityObjectAlive(_w5CarpetAnimatorField?.GetValue(__instance)))
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            __result = Task.CompletedTask;
            return false;
        }
        return true;
    }

    private static bool Wave5CarpetUnlockBitmaskPrefix(object __instance, SocketedContainer unlockedSockets, ref Task __result)
    {
        if (!IsUnityObjectAlive(__instance)
            || unlockedSockets == null
            || !IsUnityObjectAlive(_w5CarpetAnimatorField?.GetValue(__instance)))
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            __result = Task.CompletedTask;
            return false;
        }
        return true;
    }

    private static bool Wave5CarpetAnimatorAliveTaskPrefix(object __instance, ref Task __result)
    {
        if (!IsUnityObjectAlive(__instance)
            || !IsUnityObjectAlive(_w5CarpetAnimatorField?.GetValue(__instance)))
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            __result = Task.CompletedTask;
            return false;
        }
        return true;
    }

    private static bool Wave5CarpetAnimateToUnlocksPrefix(object __instance, ref Task __result)
    {
        if (!IsUnityObjectAlive(__instance)
            || !IsUnityObjectAlive(_w5CarpetAnimatorField?.GetValue(__instance))
            || (_w5CarpetSoundHandlerField != null && !IsUnityObjectAlive(_w5CarpetSoundHandlerField.GetValue(__instance))))
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            __result = Task.CompletedTask;
            return false;
        }
        return true;
    }

    private static bool Wave5CarpetAnimatorAliveVoidPrefix(object __instance)
    {
        if (!IsUnityObjectAlive(__instance) || !IsUnityObjectAlive(_w5CarpetAnimatorField?.GetValue(__instance)))
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            return false;
        }
        return true;
    }

    // ── P2-08 #4：ClearBoardTriggerComponent.Show 存活与状态空保护 ──
    private static bool Wave5ClearBoardShowPrefix(object __instance)
    {
        if (!IsUnityObjectAlive(__instance)
            || GameInspector.GetDataRun() == null
            || GameInspector.GetDataCurrentState() == null
            || !IsUnityObjectAlive(GameInspector.GetBoardManagerCached()))
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            return false;
        }
        return true;
    }

    // ── P2-08 #5：TransformManager.StartTransformVFX 泛型空安全与销毁保护 ──
    private static bool Wave5StartTransformVfxPrefixGeneric<TVfxLoop>(
        object __instance,
        Transform sourceTransform,
        ECardSize size,
        ref Task<TVfxLoop> __result)
    {
        if (!IsUnityObjectAlive(__instance)
            || !IsUnityObjectAlive(sourceTransform)
            || (int)size == 0
            || GameInspector.GetDataRun() == null
            || _w5TmVfxManagerField?.GetValue(__instance) == null)
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            __result = Task.FromResult<TVfxLoop>(default);
            return false;
        }
        return true;
    }

    private static void Wave5StartTransformVfxPostfixGeneric<TVfxLoop>(
        object __instance,
        Transform sourceTransform,
        ref Task<TVfxLoop> __result)
    {
        if (__result == null || (__result.IsCompleted && !__result.IsFaulted)) return;
        __result = WrapStartTransformVfxTaskGeneric(__result, __instance, sourceTransform);
    }

    private static async Task<TVfxLoop> WrapStartTransformVfxTaskGeneric<TVfxLoop>(
        Task<TVfxLoop> inner,
        object transformMgr,
        Transform sourceTransform)
    {
        try
        {
            return await inner;
        }
        catch (Exception ex) when (ex is NullReferenceException or MissingReferenceException or ArgumentOutOfRangeException)
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            return default;
        }
    }

    // ── P2-08 #6：HeroBoardController.RefreshPropManager 安全异步替换 ──
    private static bool Wave5HeroBoardRefreshPropManagerPrefix(object __instance)
    {
        try
        {
            if (!IsUnityObjectAlive(__instance))
            {
                Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
                return false;
            }
            if (_w5HbcPropManagerProp == null) return true;

            _ = SafeRefreshHeroBoardPropManagerAsync(__instance);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task SafeRefreshHeroBoardPropManagerAsync(object heroBoardCtrl)
    {
        try
        {
            if (!IsUnityObjectAlive(heroBoardCtrl)) return;
            if (_w5HbcPropManagerProp.GetValue(heroBoardCtrl) is not Behaviour propMgr || propMgr == null || !propMgr.isActiveAndEnabled)
                return;

            propMgr.enabled = false;
            await Task.Yield();

            if (!IsUnityObjectAlive(heroBoardCtrl) || !IsUnityObjectAlive(propMgr))
            {
                Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
                return;
            }
            propMgr.enabled = true;
        }
        catch (Exception ex) when (ex is NullReferenceException or MissingReferenceException)
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
        }
        catch { }
    }

    // ── P2-08 #7：RewardController.OnSpawnComplete & CardController.UpdatePriceTag 空保护 ──
    private static bool Wave5RewardOnSpawnCompletePrefix(object __instance)
    {
        if (!IsUnityObjectAlive(__instance)
            || GameInspector.GetDataRun() == null
            || GameInspector.GetDataCurrentState() == null
            || _w5CcCardDataProp?.GetValue(__instance) == null)
        {
            try
            {
                if (IsUnityObjectAlive(__instance))
                {
                    _w5RcRevealCompleteField?.SetValue(__instance, true);
                }
            }
            catch { }
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            return false;
        }
        return true;
    }

    private static bool Wave5CardControllerUpdatePriceTagPrefix(object __instance)
    {
        if (!IsUnityObjectAlive(__instance)
            || GameInspector.GetDataRun() == null
            || _w5CcCardDataProp?.GetValue(__instance) == null)
        {
            Interlocked.Increment(ref _w5SceneUnloadNreGuardedCount);
            return false;
        }
        return true;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  P2-09：CombatState / PVPCombatState.OnEnter 兜底销毁活跃融合升级展示层
    // ════════════════════════════════════════════════════════════════════════

    private static void PatchWave5CombatFusionAbort(SafeHarmonyPatcher patcher)
    {
        try
        {
            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _w5BmFusionSelectedField = bmType.GetField("cardFusionToBoardSelectedAnimationDriver", anyInst);
                _w5BmFusionFusedField = bmType.GetField("cardFusionToBoardFusedAnimationDriver", anyInst);
                _w5BmFusionStashField = bmType.GetField("cardFusionToStashAnimationDriver", anyInst);
                _w5BmCardsForFuseField = bmType.GetField("cardsForFuse", anyInst);
                _w5BmSetPlayingFusionMethod = bmType.GetMethod("SetIsPlayingUpgradeOrFuseAnimation", anyInst, null, new[] { typeof(bool) }, null);
                _bmIsPlayingUpgradeOrFuseAnimProp ??= bmType.GetProperty("IsPlayingUpgradeOrFuseAnimation", anyInst);
            }

            var fusionDriverType = GameInspector.FindTypeSafe("TheBazaar.Game.Cards.Animation.CardFusionAnimationDriver");
            if (fusionDriverType != null)
            {
                _w5FusionPlayableGraphField = fusionDriverType.GetField("_playableGraph", BindingFlags.NonPublic | BindingFlags.Instance);
                _w5FusionAnimatorCacheField = fusionDriverType.GetField("_animatorCache", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            const BindingFlags declaredInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var combatStateType = GameInspector.FindTypeSafe("TheBazaar.CombatState");
            var combatOnEnter = combatStateType?.GetMethod("OnEnter", declaredInst, null, Type.EmptyTypes, null);
            if (combatOnEnter != null)
            {
                patcher.TryPatchMethod(
                    combatOnEnter,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5CombatStateOnEnterPrefix)),
                    optional: true,
                    label: "Wave5.P2_09.CombatState.OnEnter");
            }

            var pvpCombatStateType = GameInspector.FindTypeSafe("TheBazaar.PVPCombatState");
            var pvpOnEnter = pvpCombatStateType?.GetMethod("OnEnter", declaredInst, null, Type.EmptyTypes, null);
            if (pvpOnEnter != null)
            {
                patcher.TryPatchMethod(
                    pvpOnEnter,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5CombatStateOnEnterPrefix)),
                    optional: true,
                    label: "Wave5.P2_09.PVPCombatState.OnEnter");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchWave5CombatFusionAbort: " + ex.Message);
        }
    }

    private static void Wave5CombatStateOnEnterPrefix()
    {
        ForceFinishActiveCardFusionAnimationsOnMain("CombatState.OnEnter");
    }

    /// <summary>
    /// P2-09（RC-UPGRADE-FUSE-ANIM-UNINTERRUPTED-COMBAT）：进入战斗前中止所有仍活跃的 CardFusionAnimationDriver PlayableGraph，
    /// 恢复被挂起的 ItemController 动画控制器、图层、Rig 缩放/位移与卡槽归位，防止 650x650 巨型升级卡带入战斗遮屏。
    /// </summary>
    private static void ForceFinishActiveCardFusionAnimationsOnMain(string reason)
    {
        try
        {
            var bm = GameInspector.GetBoardManagerCached();
            if (!IsUnityObjectAlive(bm)) return;

            bool anyAborted = false;
            var affectedItems = new List<object>();

            FieldInfo[] driverFields = { _w5BmFusionSelectedField, _w5BmFusionFusedField, _w5BmFusionStashField };
            for (int i = 0; i < driverFields.Length; i++)
            {
                var driver = driverFields[i]?.GetValue(bm);
                if (driver == null) continue;

                // 先收集 _animatorCache 中的 ItemController 引用再恢复并清理
                if (_w5FusionAnimatorCacheField?.GetValue(driver) is IDictionary cacheDict && cacheDict.Count > 0)
                {
                    foreach (DictionaryEntry kv in cacheDict)
                    {
                        if (IsUnityObjectAlive(kv.Key))
                        {
                            affectedItems.Add(kv.Key);
                        }
                    }
                    RestoreAnimatorCacheDictionary(driver, _w5FusionAnimatorCacheField);
                    anyAborted = true;
                }

                if (_w5FusionPlayableGraphField != null && DestroyNullablePlayableGraphField(driver, _w5FusionPlayableGraphField))
                {
                    anyAborted = true;
                }
            }

            // 同步收尾正在融合中被消耗但尚未归池的临时卡展示对象
            if (_w5BmCardsForFuseField?.GetValue(bm) is IList fuseCards && fuseCards.Count > 0)
            {
                var lookup = GetStaticDataMember("CardAndSkillLookup");
                var entities = GameInspector.GetDataEntities();
                if (lookup != null)
                {
                    _getCardControllerMethod ??= lookup.GetType().GetMethod("GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    for (int i = 0; i < fuseCards.Count; i++)
                    {
                        var fc = fuseCards[i];
                        if (fc == null) continue;
                        var ctrl = _getCardControllerMethod?.Invoke(lookup, new[] { fc });
                        if (IsUnityObjectAlive(ctrl))
                        {
                            affectedItems.Add(ctrl);
                            // 若被消耗卡已不在实体表，立即隐藏防止巨型副卡悬浮在战场中央
                            object fcId = GameInspector.GetMember(fc, "InstanceId");
                            if (entities != null && fcId != null && !entities.Contains(fcId) && ctrl is Component fcomp && fcomp.gameObject != null)
                            {
                                fcomp.gameObject.SetActive(false);
                            }
                            anyAborted = true;
                        }
                    }
                }
            }

            for (int i = 0; i < affectedItems.Count; i++)
            {
                var ctrl = affectedItems[i];
                if (!IsUnityObjectAlive(ctrl)) continue;

                RestoreCardRuntimeAnimatorControllerIfMissing(ctrl);
                ResetCardRigPoseIfSkewed(ctrl, forceReset: true);

                try
                {
                    if (_w5CardCtrlRestoreArtLayerMethod != null && _w5CardCtrlRestoreArtLayerMethod.DeclaringType.IsInstanceOfType(ctrl))
                        _w5CardCtrlRestoreArtLayerMethod.Invoke(ctrl, null);
                    else if (_w5CardCtrlSetArtLayerDefaultMethod != null && _w5CardCtrlSetArtLayerDefaultMethod.DeclaringType.IsInstanceOfType(ctrl))
                        _w5CardCtrlSetArtLayerDefaultMethod.Invoke(ctrl, null);
                }
                catch { }

                // 解封视觉更新并归位到原卡槽
                try
                {
                    var blockM = ctrl.GetType().GetMethod("BlockVisualUpdates", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                    blockM?.Invoke(ctrl, new object[] { false });
                    var moveBackM = ctrl.GetType().GetMethod("MoveBack", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    moveBackM?.Invoke(ctrl, null);
                }
                catch { }
            }

            bool wasFlagPlaying = _bmIsPlayingUpgradeOrFuseAnimProp?.GetValue(bm) is bool pl && pl;
            if (anyAborted || wasFlagPlaying)
            {
                try { _w5BmSetPlayingFusionMethod?.Invoke(bm, new object[] { false }); } catch { }
                int cnt = Interlocked.Increment(ref _w5FusionAbortedOnCombatCount);
                BazaarLauncherPlugin.Log?.LogWarning(
                    $"CompatAuditPatch[P2-09]: 进战兜底收尾活跃融合升级动画 ({reason}, affectedItems={affectedItems.Count}, count={cnt})");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ForceFinishActiveCardFusionAnimationsOnMain: " + ex.Message);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  P2-11 + P1-14 状态点：BoardManager.HandleCardMoved & OnStateChanged 守卫
    // ════════════════════════════════════════════════════════════════════════

    private static void PatchWave5BoardMoveAndStateGuards(SafeHarmonyPatcher patcher)
    {
        try
        {
            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType == null) return;

            // P2-11：HandleCardMoved(InstanceId, bool) Postfix 目标 Player(Hand) 强激活与显形
            var handleMoved = AccessTools.Method(bmType, "HandleCardMoved", new[] { typeof(InstanceId), typeof(bool) });
            if (handleMoved != null)
            {
                patcher.TryPatchMethod(
                    handleMoved,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5HandleCardMovedPostfix)),
                    optional: true,
                    label: "Wave5.P2_11.BoardManager.HandleCardMoved");
            }

            // P1-14：切入 PedestalState / EncounterState / ChoiceState 稳态点手牌可见性兜底
            var onStateChanged = AccessTools.Method(bmType, "OnStateChanged");
            if (onStateChanged != null)
            {
                patcher.TryPatchMethod(
                    onStateChanged,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(Wave5HandVisibilityOnStateChangedPostfix)),
                    optional: true,
                    label: "Wave5.P1_14.BoardManager.OnStateChanged.HandVisibility");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchWave5BoardMoveAndStateGuards: " + ex.Message);
        }
    }

    /// <summary>
    /// P2-11（RC-DRIVER-CLOSED-STASH-MOVE-INACTIVE）：当卡牌移入目标 section == Player（Hand）时，
    /// 无论 movedLocally 为 true/false，一律兜底强置目标卡牌 GameObject.SetActive(true) 与 ShowCard(true)，
    /// 彻底根治从关闭态 Stash 直移至 Hand 后 3D 卡永久隐身留空蓝毯。
    /// </summary>
    private static void Wave5HandleCardMovedPostfix(InstanceId instanceId)
    {
        try
        {
            var entities = GameInspector.GetDataEntities();
            var lookup = GetStaticDataMember("CardAndSkillLookup");
            if (entities == null || lookup == null || !entities.Contains(instanceId)) return;

            var cardObj = entities[instanceId];
            if (cardObj is not ICard ic || ic.Type != ECardType.Item) return;
            if (GameInspector.GetMember(cardObj, "Section") is not EInventorySection sec || sec != EInventorySection.Hand)
                return;

            _getCardControllerMethod ??= lookup.GetType().GetMethod("GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var ctrl = _getCardControllerMethod?.Invoke(lookup, new[] { cardObj });
            if (ctrl is not Component comp || comp == null || comp.gameObject == null) return;

            // 确认归属玩家区（BazaarBoard.ESections.Player）
            object boardSec = _w5CardCtrlBoardSectionField != null
                ? _w5CardCtrlBoardSectionField.GetValue(ctrl)
                : GameInspector.GetMember(ctrl, "boardSection");
            if (boardSec != null && !string.Equals(boardSec.ToString(), "Player", StringComparison.Ordinal))
                return;

            bool wasInactive = !comp.gameObject.activeSelf;
            if (wasInactive)
            {
                comp.gameObject.SetActive(true);
            }

            EnsureCardControllerVisibleCache(ctrl);
            bool wasInvisible = _ufCardCtrlIsVisibleProp?.GetValue(ctrl) is bool vis && !vis;
            if (wasInactive || wasInvisible)
            {
                ForceShowCardController(ctrl);
                int cnt = Interlocked.Increment(ref _w5MovedHandReactivatedCount);
                BazaarLauncherPlugin.Log?.LogInfo(
                    $"CompatAuditPatch[P2-11]: 移入 Hand 卡牌 {instanceId} 兜底激活并显形 (wasInactive={wasInactive}, wasInvisible={wasInvisible}, count={cnt})");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.Wave5HandleCardMovedPostfix: " + ex.Message);
        }
    }

    private static void Wave5HandVisibilityOnStateChangedPostfix(object obj)
    {
        try
        {
            if (obj == null) return;
            var cur = GameInspector.GetMember(obj, "CurrentState");
            string curStateName = cur?.GetType().Name ?? "";
            if (curStateName is "PedestalState" or "EncounterState" or "ChoiceState" or "TournamentPvpChoiceState")
            {
                ScheduleWave5HandVisibilitySettleSweep(curStateName);
            }
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  P3-05（RC-PORTRAIT-FAST-EXIT-RACE）：商人立绘被 600ms 延迟 ClearPortrait 误删兜底恢复
    // ════════════════════════════════════════════════════════════════════════

    private static void Wave5EncounterStatePortraitOnStateChangedPostfix(object obj)
    {
        try
        {
            if (obj == null) return;
            var cur = GameInspector.GetMember(obj, "CurrentState");
            string curStateName = cur?.GetType().Name ?? "";
            if (!string.Equals(curStateName, "EncounterState", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _w5EncounterPortraitSeq);
                return;
            }

            int seq = Interlocked.Increment(ref _w5EncounterPortraitSeq);
            // 覆盖前店 ClearBoardTriggerComponent.Show() 600ms 延迟 ClearPortrait 竞态窗口（300ms / 750ms 双点复核）
            foreach (int delayMs in new[] { 300, 750 })
            {
                _ = Task.Delay(delayMs).ContinueWith(_ =>
                {
                    if (Volatile.Read(ref _w5EncounterPortraitSeq) != seq) return;
                    MainThreadDispatcher.Enqueue(() => RecoverMissingEncounterPortraitOnMain(seq, $"EncounterEnter({delayMs}ms)"));
                }, TaskScheduler.Default);
            }
        }
        catch { }
    }

    private static void Wave5ClearPortraitPostfix()
    {
        try
        {
            if (_w5RecoveringEncounterPortrait) return;
            var curAppState = GameInspector.GetCurrentAppState();
            if (curAppState == null || !string.Equals(curAppState.GetType().Name, "EncounterState", StringComparison.Ordinal))
                return;

            if (GetStaticDataMember("CurrentEncounterId") is not Guid encId || encId == Guid.Empty)
                return;

            int seq = Volatile.Read(ref _w5EncounterPortraitSeq);
            // 延后至下一帧与 120ms 后复核：若属 AssignNewEncounterToPortrait 内合法切换则下一帧已有新立绘；
            // 若属前店 600ms 延迟 ClearPortrait 误删则立绘为空，立即触发 EnsureCurrentEncounterPortraitAsync() 恢复！
            MainThreadDispatcher.Enqueue(() => RecoverMissingEncounterPortraitOnMain(seq, "ClearPortraitNextFrame"));
            _ = Task.Delay(120).ContinueWith(_ =>
            {
                if (Volatile.Read(ref _w5EncounterPortraitSeq) != seq) return;
                MainThreadDispatcher.Enqueue(() => RecoverMissingEncounterPortraitOnMain(seq, "ClearPortraitDelayed(120ms)"));
            }, TaskScheduler.Default);
        }
        catch { }
    }

    private static void RecoverMissingEncounterPortraitOnMain(int expectedSeq, string reason)
    {
        try
        {
            if (Volatile.Read(ref _w5EncounterPortraitSeq) != expectedSeq) return;
            if (_w5RecoveringEncounterPortrait) return;

            var curAppState = GameInspector.GetCurrentAppState();
            if (curAppState == null || !string.Equals(curAppState.GetType().Name, "EncounterState", StringComparison.Ordinal))
                return;

            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is not ERunState runState || runState != ERunState.Encounter)
                return;

            if (GetStaticDataMember("CurrentEncounterId") is not Guid encId || encId == Guid.Empty)
                return;

            var curPortrait = GetStaticDataMember("CurrentEncounterController");
            bool portraitHealthy = false;
            if (curPortrait is Component pComp && pComp != null && pComp.gameObject != null && pComp.gameObject.activeInHierarchy)
            {
                var pCard = GameInspector.GetMember(curPortrait, "CardData");
                if (pCard != null && GameInspector.GetMember(pCard, "TemplateId") is Guid pTplId && pTplId == encId)
                {
                    portraitHealthy = true;
                }
            }
            if (portraitHealthy) return;

            var bm = GameInspector.GetBoardManagerCached();
            if (!IsUnityObjectAlive(bm)) return;

            _w5BmEnsurePortraitAsyncMethod ??= AccessTools.Method(bm.GetType(), "EnsureCurrentEncounterPortraitAsync", Type.EmptyTypes);
            if (_w5BmEnsurePortraitAsyncMethod == null) return;

            _w5RecoveringEncounterPortrait = true;
            if (_w5BmEnsurePortraitAsyncMethod.Invoke(bm, null) is Task task)
            {
                _ = ObserveRecoverPortraitTask(task);
            }
            else
            {
                _w5RecoveringEncounterPortrait = false;
            }

            int cnt = Interlocked.Increment(ref _w5EncounterPortraitRecoveredCount);
            BazaarLauncherPlugin.Log?.LogInfo(
                $"CompatAuditPatch[P3-05]: 检测到 EncounterState 商人立绘缺失 (encId={encId}, reason={reason}, count={cnt})，已补调 EnsureCurrentEncounterPortraitAsync()");
        }
        catch (Exception ex)
        {
            _w5RecoveringEncounterPortrait = false;
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RecoverMissingEncounterPortraitOnMain: " + ex.Message);
        }
    }

    private static async Task ObserveRecoverPortraitTask(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch[P3-05]: EnsureCurrentEncounterPortraitAsync 异常已捕获: " + ex.Message);
        }
        finally
        {
            _w5RecoveringEncounterPortrait = false;
        }
    }
}
