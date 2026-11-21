using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Cards.Enchantments;
using BazaarGameShared.Domain.Cards.Encounter.Pedestal;
using BazaarGameShared.Domain.Cards.Encounter.Pedestal.Behaviors;
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Players;
using BazaarGameShared.Domain.Prerequisites.Conditionals;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Domain.Targeting;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using HarmonyLib;
using UnityEngine;

namespace BazaarLauncher;

internal static partial class CompatAuditPatch
{
    // ── 10. PedestalState：升阶/附魔即时刷新 UI、悬停预览双保险与退出恢复 ─────

    private static void PatchPedestalState(Harmony harmony)
    {
        try
        {
            var pedType = GameInspector.FindTypeSafe("TheBazaar.PedestalState");
            if (pedType == null) return;

            _pedestalAnimCompletedField = pedType.GetField("_animationCompleted", BindingFlags.NonPublic | BindingFlags.Instance);
            _pedestalTemplateField = pedType.GetField("_pedestalTemplate", BindingFlags.NonPublic | BindingFlags.Instance);

            var onEnter = AccessTools.Method(pedType, "OnEnter");
            if (onEnter != null)
            {
                harmony.Patch(onEnter, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalOnEnterPostfix))));
                _patchedCount++;
            }

            var mUpd = AccessTools.Method(pedType, "HandleCardUpgradedEvent", new[] { typeof(NetMessageGameSim) });
            if (mUpd != null)
            {
                harmony.Patch(mUpd,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalUpgradePrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalUpgradeOrEnchantPostfix))));
                _patchedCount++;
            }

            var mEnch = AccessTools.Method(pedType, "HandleCardEnchantmentEvent", new[] { typeof(NetMessageGameSim) });
            if (mEnch != null)
            {
                harmony.Patch(mEnch,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalEnchantPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalUpgradeOrEnchantPostfix))));
                _patchedCount++;
            }

            // 升级台悬停预览双保险 1：兼容复合 SelectionCriteria (如 Partner Portal - Forja)
            var cardExtType = GameInspector.FindTypeSafe("TheBazaar.CardExtensions");
            var canPedUpg = cardExtType == null ? null : AccessTools.Method(cardExtType, "CanUseTPedestalBehaviorUpgrade");
            if (canPedUpg != null)
            {
                harmony.Patch(canPedUpg, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(CanUsePedestalUpgradePostfix))));
                _patchedCount++;
            }

            // 升级台悬停预览双保险 2：CurrentEncounterController 未及时就绪或复合条件时兜底
            var tipParentType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.TooltipParentComponent");
            var isUpgPedState = tipParentType == null ? null : AccessTools.Method(tipParentType, "IsUpgradePedestalState");
            if (isUpgPedState != null)
            {
                harmony.Patch(isUpgPedState, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(IsUpgradePedestalStatePostfix))));
                _patchedCount++;
            }

            // 离开 PedestalState 时恢复所有升级预览标记，并即时销毁未提交/已退出的基座中央卷轴预制体
            var bspType = GameInspector.FindTypeSafe("TheBazaar.BoardStatePresentation", "BoardStatePresentation");
            if (bspType != null)
            {
                _bspType = bspType;
                _bspCleanUpCompleteMethod = AccessTools.Method(bspType, "CleanUpComplete", Type.EmptyTypes);
                _bspCancellationTokenField = bspType.GetField("_cancellationToken", BindingFlags.NonPublic | BindingFlags.Instance);
                _bspHasFeatureMethod = AccessTools.Method(bspType, "HasFeature");
                if (_bspHasFeatureMethod != null)
                {
                    var featParams = _bspHasFeatureMethod.GetParameters();
                    if (featParams.Length == 1 && featParams[0].ParameterType.IsEnum)
                    {
                        _pedestalFeatureValues.Clear();
                        foreach (var enumVal in Enum.GetValues(featParams[0].ParameterType))
                        {
                            string enumName = enumVal?.ToString() ?? "";
                            if (enumName.IndexOf("Pedestal", StringComparison.OrdinalIgnoreCase) >= 0
                                || enumName.IndexOf("Enchant", StringComparison.OrdinalIgnoreCase) >= 0
                                || enumName.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                                || enumName.IndexOf("Artist", StringComparison.OrdinalIgnoreCase) >= 0
                                || enumName.IndexOf("Artisan", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _pedestalFeatureValues.Add(enumVal);
                            }
                        }
                    }
                }

                var bspInit = AccessTools.Method(bspType, "Initialize");
                if (bspInit != null)
                {
                    harmony.Patch(bspInit, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardStatePresentationInitializePostfix))));
                    _patchedCount++;
                }

                var bspCleanUp = AccessTools.Method(bspType, "CleanUp", Type.EmptyTypes);
                if (bspCleanUp != null)
                {
                    harmony.Patch(bspCleanUp, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardStatePresentationCleanUpPrefix))));
                    _patchedCount++;
                }

                var bspOnDestroy = AccessTools.Method(bspType, "OnDestroy", Type.EmptyTypes);
                if (bspOnDestroy != null)
                {
                    harmony.Patch(bspOnDestroy, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardStatePresentationOnDestroyPostfix))));
                    _patchedCount++;
                }
            }

            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                _bmShouldLoadPresMethod = AccessTools.Method(bmType, "ShouldLoadBoardStatePresentation", Type.EmptyTypes);
                _bmRemovePresMethod = AccessTools.Method(bmType, "RemoveBoardStatePresentation", Type.EmptyTypes);
                _bmInvalidateLoadRequestsMethod = AccessTools.Method(bmType, "InvalidateBoardStatePresentationLoadRequests", Type.EmptyTypes);
                _bmBoardStatePresProp = bmType.GetProperty("BoardStatePresentation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _bmIsBoardStatePresLoadingProp = bmType.GetProperty("IsBoardStatePresentationLoading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _bmLoadRequestVersionField = bmType.GetField("_boardStatePresentationLoadRequestVersion", BindingFlags.NonPublic | BindingFlags.Instance);
                _bmPresRefField = bmType.GetField("boardStatePresentationReference", BindingFlags.NonPublic | BindingFlags.Instance);

                var onStateChanged = AccessTools.Method(bmType, "OnStateChanged");
                if (onStateChanged != null)
                {
                    harmony.Patch(onStateChanged, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardManagerOnStateChangedPostfix))));
                    _patchedCount++;
                }

                var determinePres = AccessTools.Method(bmType, "DetermineBoardStatePresentation", Type.EmptyTypes);
                if (determinePres != null)
                {
                    harmony.Patch(determinePres, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(DetermineBoardStatePresentationPostfix))));
                    _patchedCount++;
                }

                var loadPres = AccessTools.Method(bmType, "LoadBoardStatePresentation", Type.EmptyTypes);
                if (loadPres != null)
                {
                    harmony.Patch(loadPres, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(LoadBoardStatePresentationPostfix))));
                    _patchedCount++;
                }

                if (_bmRemovePresMethod != null)
                {
                    harmony.Patch(_bmRemovePresMethod, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(RemoveBoardStatePresentationPrefix))));
                    _patchedCount++;
                }

                var reconcilePres = AccessTools.Method(bmType, "ReconcilePresentationAfterStateSyncAsync");
                if (reconcilePres != null)
                {
                    harmony.Patch(reconcilePres, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(ReconcilePresentationAfterStateSyncPostfix))));
                    _patchedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PedestalState: " + ex.Message);
        }
    }

    private static TCardEncounterPedestal ResolveActiveUpgradePedestalTemplate(object encounterCard = null, object pedestalStateObj = null)
    {
        TCardEncounterPedestal pedTpl = null;
        if (encounterCard != null)
        {
            pedTpl = GameInspector.GetMember(encounterCard, "Template") as TCardEncounterPedestal;
        }

        if (pedTpl == null)
        {
            var curEncCtrl = GetStaticDataMember("CurrentEncounterController");
            if (curEncCtrl != null && !(curEncCtrl is UnityEngine.Object uo && uo == null))
            {
                var encCard = GameInspector.GetMember(curEncCtrl, "CardData");
                pedTpl = GameInspector.GetMember(encCard, "Template") as TCardEncounterPedestal;
            }
        }

        if (pedTpl == null && pedestalStateObj != null && _pedestalTemplateField != null)
        {
            pedTpl = _pedestalTemplateField.GetValue(pedestalStateObj) as TCardEncounterPedestal;
        }

        if (pedTpl == null)
        {
            var curState = GameInspector.GetCurrentAppState();
            if (curState != null && curState.GetType().Name == "PedestalState" && _pedestalTemplateField != null)
            {
                pedTpl = _pedestalTemplateField.GetValue(curState) as TCardEncounterPedestal;
            }
        }

        if (pedTpl == null)
        {
            if (GetStaticDataMember("CurrentEncounterId") is Guid encGuid && encGuid != Guid.Empty)
            {
                var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
                var getStaticM = dataType?.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                var staticMgr = getStaticM?.Invoke(null, null);
                var getCardM = staticMgr?.GetType().GetMethod("GetCardById", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Guid) }, null);
                pedTpl = getCardM?.Invoke(staticMgr, new object[] { encGuid }) as TCardEncounterPedestal;
            }
        }

        if (pedTpl == null) return null;
        return pedTpl.Behavior is TPedestalBehaviorUpgrade ? pedTpl : null;
    }

    private static object GetStaticDataMember(string memberName)
    {
        var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
        if (dataType == null) return null;
        var p = dataType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (p != null) return p.GetValue(null);
        var f = dataType.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return f?.GetValue(null);
    }

    private static bool IsUpgradeablePlayerItem(object card)
    {
        if (card == null) return false;
        if (GameInspector.GetMember(card, "Type") is not ECardType cardType || cardType != ECardType.Item)
            return false;

        var run = GameInspector.GetDataRun();
        var player = run != null ? GameInspector.GetMember(run, "Player") : null;
        var owner = GameInspector.GetMember(card, "Owner");
        if (player == null || !ReferenceEquals(owner, player))
            return false;

        var cardExtensionsType = GameInspector.FindTypeSafe("TheBazaar.CardExtensions");
        _canCardUpgradeMethod ??= cardExtensionsType?.GetMethod("CanCardUpgrade", BindingFlags.Public | BindingFlags.Static);
        if (_canCardUpgradeMethod != null)
        {
            return _canCardUpgradeMethod.Invoke(null, new[] { card }) is bool canUpg && canUpg;
        }

        return GameInspector.GetMember(card, "Tier") is ETier tier && tier < ETier.Diamond;
    }

    private static bool EvaluatePedestalSelectionCriteria(TCardEncounterPedestal pedTpl, object card)
    {
        if (pedTpl == null || card == null) return false;
        if (pedTpl.SelectionCriteria == null) return true;

        try
        {
            var run = GameInspector.GetDataRun();
            if (run != null)
            {
                if (_cardCondContextCtor == null)
                {
                    var ctors = typeof(CardConditionalContext).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _cardCondContextCtor = ctors.FirstOrDefault(c =>
                    {
                        var ps = c.GetParameters();
                        return ps.Length == 4 && ps[0].ParameterType.IsInstanceOfType(run);
                    });
                }
                if (_cardCondContextCtor != null && card is ICard icard)
                {
                    var ctx = _cardCondContextCtor.Invoke(new[] { run, null, null, null }) as CardConditionalContext;
                    if (ctx != null)
                    {
                        return pedTpl.SelectionCriteria.IsSatisfiedBy(icard, ctx);
                    }
                }
            }
        }
        catch { }

        // 若条件上下文构造失败，退回到 PedestalState.CanBeUpgraded
        try
        {
            var curState = GameInspector.GetCurrentAppState();
            if (curState != null && curState.GetType().Name == "PedestalState")
            {
                var mCan = curState.GetType().GetMethod("CanBeUpgraded", BindingFlags.Public | BindingFlags.Instance);
                if (mCan != null)
                {
                    return mCan.Invoke(curState, new[] { card }) is bool ok && ok;
                }
            }
        }
        catch { }

        return false;
    }

    // P2 反射基类显式锚定辅助，避免从派生实例懒缓存导致跨控制器强转风险
    private static object _cachedPedestalUpgradeModeEnum;
    private static object _cachedDefaultTooltipModeEnum;

    private static void EnsurePedestalPreviewReflectionAnchored()
    {
        var cardCtrlType = GameInspector.FindTypeSafe("CardController", "TheBazaar.CardController");
        if (cardCtrlType != null)
        {
            _enterUpgradePreviewMethod ??= cardCtrlType.GetMethod("EnterUpgradePreview", BindingFlags.Public | BindingFlags.Instance);
            _exitUpgradePreviewCardMethod ??= cardCtrlType.GetMethod("ExitUpgradePreview", BindingFlags.Public | BindingFlags.Instance);
            _refreshFullTooltipMethod ??= cardCtrlType.GetMethod("RefreshFullTooltip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        var tipParentType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.TooltipParentComponent");
        if (tipParentType != null)
        {
            _exitUpgradePreviewAllMethod ??= tipParentType.GetMethod("ExitUpgradePreviewOnAllPlayerCards", BindingFlags.Public | BindingFlags.Instance);
            _cardTooltipControllerProp ??= tipParentType.GetProperty("CardTooltipController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        }

        var tipCtrlType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.CardTooltipController");
        if (tipCtrlType != null)
        {
            _getDisplayModeMethod ??= tipCtrlType.GetMethod("GetDisplayMode", BindingFlags.Public | BindingFlags.Instance);
            _setDisplayModeMethod ??= tipCtrlType.GetMethod("SetDisplayMode", BindingFlags.Public | BindingFlags.Instance);
            if (_setDisplayModeMethod != null && (_cachedPedestalUpgradeModeEnum == null || _cachedDefaultTooltipModeEnum == null))
            {
                try
                {
                    var modeType = _setDisplayModeMethod.GetParameters()[0].ParameterType;
                    _cachedPedestalUpgradeModeEnum ??= Enum.Parse(modeType, "PedestalUpgradeTooltip");
                    _cachedDefaultTooltipModeEnum ??= Enum.Parse(modeType, "Default");
                }
                catch { }
            }
        }
    }

    private static void CanUsePedestalUpgradePostfix(object card, object cardToSelect, ref bool __result)
    {
        // 拖拽 Top 10：非升级底座态 O(1) 零反射直接短路！
        if (__result || !_wasInUpgradePedestal || cardToSelect == null) return;
        try
        {
            if (!IsUpgradeablePlayerItem(cardToSelect)) return;
            var pedTpl = ResolveActiveUpgradePedestalTemplate(card);
            if (pedTpl == null) return;

            if (EvaluatePedestalSelectionCriteria(pedTpl, cardToSelect))
            {
                __result = true;
            }
        }
        catch { }
    }

    private static void IsUpgradePedestalStatePostfix(object tooltipData, ref bool __result)
    {
        // 拖拽 Top 10：非升级底座态 O(1) 零反射直接短路！
        if (__result || !_wasInUpgradePedestal || tooltipData == null) return;
        try
        {
            var curAppState = GameInspector.GetCurrentAppState();
            if (curAppState == null || curAppState.GetType().Name != "PedestalState")
                return;

            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is not ERunState runState || runState != ERunState.Pedestal)
                return;

            var cardInst = GameInspector.GetMember(tooltipData, "CardInstance");
            if (!IsUpgradeablePlayerItem(cardInst)) return;

            var pedTpl = ResolveActiveUpgradePedestalTemplate(null, curAppState);
            if (pedTpl == null) return;

            if (EvaluatePedestalSelectionCriteria(pedTpl, cardInst))
            {
                __result = true;
                Interlocked.Increment(ref _pedestalPreviewFallbackCount);
            }
        }
        catch { }
    }

    private static void PedestalOnEnterPostfix(object __instance)
    {
        _pedestalCommitRequested = false;
        _pendingPedestalExitAbort = false;
        _wasInAnyPedestal = true;
        try
        {
            if (__instance == null) return;

            // 若原生 OnEnter 时 _pedestalTemplate 为空，尝试按 Data.CurrentEncounterId 补全
            if (_pedestalTemplateField != null && _pedestalTemplateField.GetValue(__instance) == null)
            {
                var fallbackTpl = ResolveActiveUpgradePedestalTemplate(null, __instance);
                if (fallbackTpl != null)
                {
                    _pedestalTemplateField.SetValue(__instance, fallbackTpl);
                }
            }

            var pedTpl = _pedestalTemplateField?.GetValue(__instance) as TCardEncounterPedestal
                         ?? ResolveActiveUpgradePedestalTemplate(null, __instance);
            if (pedTpl?.Behavior is not TPedestalBehaviorUpgrade)
            {
                _wasInUpgradePedestal = false;
                return;
            }

            _wasInUpgradePedestal = true;
            RefreshHoveredUpgradePreviewOnEnter(pedTpl);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PedestalOnEnterPostfix: " + ex.Message);
        }
    }

    private static void RefreshHoveredUpgradePreviewOnEnter(TCardEncounterPedestal pedTpl)
    {
        EnsurePedestalPreviewReflectionAnchored();
        var lookup = GameInspector.GetDataLookup();
        if (lookup == null) return;
        if (GameInspector.GetMember(lookup, "CardControllerDictionary") is not IDictionary dict) return;

        var tipParent = GetStaticDataMember("TooltipParentComponent");
        foreach (DictionaryEntry kv in dict)
        {
            var ctrl = kv.Value;
            if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) continue;
            if (GameInspector.GetMember(ctrl, "IsCursorOverCard") is not bool over || !over) continue;

            var cardData = GameInspector.GetMember(ctrl, "CardData");
            if (!IsUpgradeablePlayerItem(cardData) || !EvaluatePedestalSelectionCriteria(pedTpl, cardData))
                continue;

            if (_enterUpgradePreviewMethod != null && _enterUpgradePreviewMethod.DeclaringType.IsInstanceOfType(ctrl))
            {
                _enterUpgradePreviewMethod.Invoke(ctrl, null);
            }

            var cardTipData = GameInspector.GetMember(ctrl, "_cardTooltipData");
            cardTipData?.GetType().GetProperty("CanFuse", BindingFlags.Public | BindingFlags.Instance)?.SetValue(cardTipData, true);

            if (tipParent != null && !(tipParent is UnityEngine.Object tpuo && tpuo == null)
                && _cardTooltipControllerProp != null && _cardTooltipControllerProp.DeclaringType.IsInstanceOfType(tipParent))
            {
                var tipCtrl = _cardTooltipControllerProp.GetValue(tipParent);
                if (tipCtrl != null && !(tipCtrl is UnityEngine.Object tcuo && tcuo == null))
                {
                    var tipData = GameInspector.GetMember(tipCtrl, "CurrentTooltipData");
                    var tipCard = tipData != null ? GameInspector.GetMember(tipData, "CardInstance") : null;
                    if (ReferenceEquals(tipCard, cardData))
                    {
                        tipData?.GetType().GetProperty("CanFuse", BindingFlags.Public | BindingFlags.Instance)?.SetValue(tipData, true);
                        if (_setDisplayModeMethod != null && _cachedPedestalUpgradeModeEnum != null
                            && _setDisplayModeMethod.DeclaringType.IsInstanceOfType(tipCtrl))
                        {
                            _setDisplayModeMethod.Invoke(tipCtrl, new[] { _cachedPedestalUpgradeModeEnum });
                        }
                    }
                }
            }

            if (_refreshFullTooltipMethod != null && _refreshFullTooltipMethod.DeclaringType.IsInstanceOfType(ctrl))
            {
                _refreshFullTooltipMethod.Invoke(ctrl, null);
            }
            break;
        }
    }

    private static void BoardManagerOnStateChangedPostfix(object __instance, object obj)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object bmUo && bmUo == null) || obj == null) return;
            ResetShowCardHintDedupeCache();
            var prev = GameInspector.GetMember(obj, "PreviousState");
            var cur = GameInspector.GetMember(obj, "CurrentState");
            string curStateName = cur?.GetType().Name ?? "";
            if (curStateName is "ChoiceState" or "CombatState" or "PVPCombatState" or "EndRunVictoryState" or "EndRunDefeatState")
            {
                ClearParentEncounterCache("OnStateChanged:" + curStateName);
            }
            bool wasPedestal = prev != null && prev.GetType().Name == "PedestalState";
            bool nowPedestal = cur != null && cur.GetType().Name == "PedestalState";
            bool nowPedestalContext = nowPedestal || IsInPedestalRunState() || IsCurrentEncounterPedestal();

            if (nowPedestalContext)
            {
                _wasInAnyPedestal = true;
                _pedestalVerifyNonPedestalExit = true;
                // 断线重连直接切入底座态时兜底同步升级底座标志，确保非底座态 O(1) 短路始终精准
                if (!_wasInUpgradePedestal && ResolveActiveUpgradePedestalTemplate(null, nowPedestal ? cur : null) != null)
                {
                    _wasInUpgradePedestal = true;
                }
                var curPres = GetBoardStatePresentationObj(__instance);
                if (curPres != null)
                {
                    TrackPedestalPresentation(curPres);
                }
            }

            if ((wasPedestal || _wasInUpgradePedestal) && !nowPedestal)
            {
                _wasInUpgradePedestal = false;
                ExitAllUpgradePreviewsSafely();
            }

            if ((wasPedestal || _wasInAnyPedestal || _pedestalVerifyNonPedestalExit) && !nowPedestalContext)
            {
                _wasInAnyPedestal = false;
                CleanupStalePedestalBoardPresentation(__instance, "OnStateChanged", forceImmediate: true, allowWhileInPedestalState: false);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.BoardManagerOnStateChangedPostfix: " + ex.Message);
        }
    }

    private static void BoardStatePresentationInitializePostfix(object __instance)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            if (IsInPedestalContext() || HasPedestalFeature(__instance))
            {
                TrackPedestalPresentation(__instance);
            }
        }
        catch { }
    }

    private static void BoardStatePresentationOnDestroyPostfix(object __instance)
    {
        try
        {
            if (__instance is Component comp)
            {
                int id = comp.GetInstanceID();
                lock (_pedestalPresLock)
                {
                    _pedestalPresentationIds.Remove(id);
                    _forceCleanedPresentationIds.Remove(id);
                    if (_lastPedestalPresentationId == id)
                    {
                        _lastPedestalPresentationId = 0;
                    }
                }
            }
        }
        catch { }
    }

    private static bool BoardStatePresentationCleanUpPrefix(object __instance, ref Task __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null))
            {
                __result = Task.CompletedTask;
                return false;
            }

            // 只要进入销毁流程（CleanUp 仅在移除或替换展示时调用），凡属基座实例/强清实例/基座上下文或已失活对象均立即同步短路销毁，避免连续触发多级基座或切回商店时卡入 10 秒超时
            bool shouldShortCircuit = IsForceCleanedPresentation(__instance)
                || IsTrackedOrPedestalPresentation(__instance)
                || IsInPedestalContext()
                || (__instance is Component c && c.gameObject != null && !c.gameObject.activeInHierarchy);

            if (!shouldShortCircuit)
                return true;

            bool newlyCleaned = ShortCircuitAndDestroyPresentation(__instance);
            if (newlyCleaned)
            {
                Interlocked.Increment(ref _pedestalPresentationCleanedCount);
            }
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.BoardStatePresentationCleanUpPrefix: " + ex.Message);
            return true;
        }
    }

    private static void DetermineBoardStatePresentationPostfix(object __instance, ref Task __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null) || __result == null) return;
            __result = WrapBoardPresentationCheckTask(__result, __instance, "DetermineBoardStatePresentation");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.DetermineBoardStatePresentationPostfix: " + ex.Message);
        }
    }

    private static void LoadBoardStatePresentationPostfix(object __instance, ref Task __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null) || __result == null) return;
            __result = WrapBoardPresentationCheckTask(__result, __instance, "LoadBoardStatePresentation");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.LoadBoardStatePresentationPostfix: " + ex.Message);
        }
    }

    private static void ReconcilePresentationAfterStateSyncPostfix(object __instance, ref Task __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null) || __result == null) return;
            __result = WrapBoardPresentationCheckTask(__result, __instance, "ReconcilePresentationAfterStateSync");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ReconcilePresentationAfterStateSyncPostfix: " + ex.Message);
        }
    }

    private static async Task WrapBoardPresentationCheckTask(Task inner, object boardManager, string sourceTag)
    {
        try
        {
            await inner;
        }
        finally
        {
            PostToMainThread(() =>
            {
                try
                {
                    if (boardManager == null || (boardManager is UnityEngine.Object bmUo && bmUo == null))
                        return;

                    var curPres = GetBoardStatePresentationObj(boardManager);
                    if (IsInPedestalContext())
                    {
                        _wasInAnyPedestal = true;
                        _pedestalVerifyNonPedestalExit = true;
                        if (curPres != null)
                        {
                            TrackPedestalPresentation(curPres);
                        }
                    }
                    else
                    {
                        // 已切换到非基座状态（如 Encounter/Choice/葵仙子商店），二层兜底清理一切残留基座或未替换卷轴
                        CleanupStalePedestalBoardPresentation(boardManager, sourceTag, forceImmediate: false, allowWhileInPedestalState: false);
                    }
                }
                catch (Exception ex)
                {
                    BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapBoardPresentationCheckTask: " + ex.Message);
                }
            });
        }
    }

    private static bool RemoveBoardStatePresentationPrefix(object __instance, ref Task __result)
    {
        if (__instance == null || (__instance is UnityEngine.Object bmUo && bmUo == null)) return true;
        try
        {
            // 关键修复 #12：移除旧补丁的 if (IsInPedestalRunState()) return true 错误放行！
            // 玩家正常用完附魔台/升级台或连续切换多级基座时，客户端调用 RemoveBoardStatePresentation 瞬间往往仍处于 Pedestal 运行态，
            // 若此处放行则会走入原版 10 秒超时死等导致下家商店中央继续挂着蓝色/金色卷轴。
            var curPres = GetBoardStatePresentationObj(__instance);
            bool isLoading = _bmIsBoardStatePresLoadingProp?.GetValue(__instance) is bool b && b;
            bool inPedestalCtx = IsInPedestalContext();
            bool isPedestalPres = curPres != null
                && (IsTrackedOrPedestalPresentation(curPres) || IsForceCleanedPresentation(curPres) || inPedestalCtx);
            bool hasPendingExitAbort = (_pendingPedestalExitAbort || inPedestalCtx) && (curPres != null || isLoading);

            if (!isPedestalPres && !hasPendingExitAbort)
            {
                if (!inPedestalCtx && _pedestalVerifyNonPedestalExit)
                {
                    SweepOrphanPedestalPresentationsInScene(curPres);
                }
                return true;
            }

            // 绝不走原生 await BoardStatePresentation.CleanUp() 10 秒超时分支，直接同步即时销毁并重置全部引用
            CleanupStalePedestalBoardPresentation(__instance, "RemoveBoardStatePresentationPrefix", forceImmediate: true, allowWhileInPedestalState: true);
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RemoveBoardStatePresentationPrefix: " + ex.Message);
            return true;
        }
    }

    private static void CleanupStalePedestalBoardPresentation(object boardManager, string reason, bool forceImmediate, bool allowWhileInPedestalState)
    {
        try
        {
            if (boardManager == null || (boardManager is UnityEngine.Object bmUo && bmUo == null))
                return;

            bool inPedestalCtx = IsInPedestalContext();
            if (inPedestalCtx && !allowWhileInPedestalState)
                return;

            var curPres = GetBoardStatePresentationObj(boardManager);
            bool isLoading = _bmIsBoardStatePresLoadingProp?.GetValue(boardManager) is bool b && b;
            bool shouldLoad = _bmShouldLoadPresMethod != null
                && _bmShouldLoadPresMethod.Invoke(boardManager, null) is bool sl && sl;

            bool isPedestalPres = curPres != null
                && (IsTrackedOrPedestalPresentation(curPres) || IsForceCleanedPresentation(curPres));

            // 二层保险：切换到非基座状态后，若新遭遇模板无独立中央 Presentation（如葵仙子等商店），任何尚未销毁的旧 BoardStatePresentation 均按陈旧卷轴立即清理
            bool staleUnreplacedPres = !inPedestalCtx && curPres != null && !shouldLoad && _pedestalVerifyNonPedestalExit;

            // 若当前已合法进入带独立呈现的后续非基座奇遇，且当前持有的是新呈现而非基座卷轴，仅清扫场景孤立基座实例，不破坏当前正常加载
            if (!inPedestalCtx && shouldLoad && !forceImmediate && !isPedestalPres)
            {
                _pendingPedestalExitAbort = false;
                _pedestalVerifyNonPedestalExit = false;
                SweepOrphanPedestalPresentationsInScene(curPres);
                return;
            }

            if (!forceImmediate && !isPedestalPres && !staleUnreplacedPres && !(_pendingPedestalExitAbort && !shouldLoad))
            {
                if (!inPedestalCtx && curPres == null && !isLoading)
                {
                    _pedestalVerifyNonPedestalExit = false;
                }
                SweepOrphanPedestalPresentationsInScene(!inPedestalCtx && shouldLoad ? curPres : null);
                return;
            }

            // 若退出基座时资源仍在异步加载且暂未实例化出展示对象，保留中止标志以便异步回调后兜底收尾
            _pendingPedestalExitAbort = forceImmediate && curPres == null && isLoading;

            if (curPres != null || isLoading)
            {
                // 仅当新奇遇不加载或正在移除旧基座呈现时才作废加载请求，避免误伤新奇遇正在发起的异步加载
                if (!shouldLoad || isPedestalPres || staleUnreplacedPres || forceImmediate)
                {
                    if (_bmInvalidateLoadRequestsMethod != null)
                    {
                        _bmInvalidateLoadRequestsMethod.Invoke(boardManager, null);
                    }
                    else
                    {
                        BumpBoardStatePresentationLoadVersion(boardManager);
                    }
                    _bmPresRefField?.SetValue(boardManager, null);
                    _bmIsBoardStatePresLoadingProp?.SetValue(boardManager, false);
                }

                if (curPres != null)
                {
                    _bmBoardStatePresProp?.SetValue(boardManager, null);
                }

                bool cleaned = curPres != null && ShortCircuitAndDestroyPresentation(curPres);
                if (cleaned || isLoading)
                {
                    int cnt = Interlocked.Increment(ref _pedestalPresentationCleanedCount);
                    BazaarLauncherPlugin.Log?.LogInfo(
                        $"CompatAuditPatch: immediately recycled stale pedestal presentation via {reason} (hasPres={curPres != null}, wasLoading={isLoading}, allowInPedestal={allowWhileInPedestalState}, count={cnt})");
                }
            }

            SweepOrphanPedestalPresentationsInScene(null);
            if (!inPedestalCtx && !isLoading && GetBoardStatePresentationObj(boardManager) == null)
            {
                _pedestalVerifyNonPedestalExit = false;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CleanupStalePedestalBoardPresentation: " + ex.Message);
        }
    }

    private static float _lastOrphanPedestalSweepTime = -10f;

    private static void SweepOrphanPedestalPresentationsInScene(object validKeepPres)
    {
        try
        {
            if (_bspType == null) return;
            float now = Time.unscaledTime;
            if (validKeepPres == null && now - _lastOrphanPedestalSweepTime < 0.35f)
            {
                return;
            }
            _lastOrphanPedestalSweepTime = now;

            var found = UnityEngine.Object.FindObjectsByType(_bspType, FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (found == null || found.Length == 0) return;

            int keepId = validKeepPres is Component keepComp && keepComp != null ? keepComp.GetInstanceID() : 0;
            foreach (var obj in found)
            {
                if (obj == null || (obj is UnityEngine.Object uo && uo == null)) continue;
                int id = obj is Component comp ? comp.GetInstanceID() : 0;
                if (keepId != 0 && id == keepId) continue;

                bool isKnownPedestal = IsTrackedOrPedestalPresentation(obj) || IsForceCleanedPresentation(obj);
                bool unownedExtraWhileNoKeep = keepId == 0 && !IsInPedestalContext();
                if (isKnownPedestal || unownedExtraWhileNoKeep)
                {
                    if (ShortCircuitAndDestroyPresentation(obj))
                    {
                        Interlocked.Increment(ref _pedestalPresentationCleanedCount);
                    }
                }
            }
        }
        catch { }
    }

    private static bool IsInPedestalContext()
    {
        if (IsInPedestalRunState()) return true;
        var appState = GameInspector.GetCurrentAppState();
        if (appState != null && string.Equals(appState.GetType().Name, "PedestalState", StringComparison.Ordinal))
            return true;
        return IsCurrentEncounterPedestal();
    }

    private static bool IsCurrentEncounterPedestal()
    {
        try
        {
            var curEncCtrl = GetStaticDataMember("CurrentEncounterController");
            if (curEncCtrl != null && !(curEncCtrl is UnityEngine.Object uo && uo == null))
            {
                var encCard = GameInspector.GetMember(curEncCtrl, "CardData");
                if (GameInspector.GetMember(encCard, "Template") is TCardEncounterPedestal)
                    return true;
            }

            if (GetStaticDataMember("CurrentEncounterId") is Guid encGuid && encGuid != Guid.Empty)
            {
                var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
                var getStaticM = dataType?.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                var staticMgr = getStaticM?.Invoke(null, null);
                var getCardM = staticMgr?.GetType().GetMethod("GetCardById", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Guid) }, null);
                if (getCardM?.Invoke(staticMgr, new object[] { encGuid }) is TCardEncounterPedestal)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsInPedestalRunState()
    {
        var dataState = GameInspector.GetDataCurrentState();
        return GameInspector.GetMember(dataState, "StateName") is ERunState runState && runState == ERunState.Pedestal;
    }

    private static bool HasPedestalFeature(object presObj)
    {
        if (presObj == null || (presObj is UnityEngine.Object uo && uo == null)) return false;
        try
        {
            if (_bspHasFeatureMethod != null && _pedestalFeatureValues.Count > 0)
            {
                foreach (var featVal in _pedestalFeatureValues)
                {
                    if (_bspHasFeatureMethod.Invoke(presObj, new[] { featVal }) is bool hasFeat && hasFeat)
                        return true;
                }
            }

            if (presObj is Component comp && comp.gameObject != null)
            {
                string goName = comp.gameObject.name ?? "";
                if (goName.IndexOf("Pedestal", StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Enchant", StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Artisan", StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Artist", StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Scroll", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static void TrackPedestalPresentation(object presObj)
    {
        if (presObj is not Component comp || comp == null) return;
        int id = comp.GetInstanceID();
        lock (_pedestalPresLock)
        {
            if (_pedestalPresentationIds.Count > 128) _pedestalPresentationIds.Clear();
            _pedestalPresentationIds.Add(id);
            _lastPedestalPresentationId = id;
        }
    }

    private static bool IsTrackedOrPedestalPresentation(object presObj)
    {
        if (presObj == null || (presObj is UnityEngine.Object uo && uo == null)) return false;
        if (presObj is Component comp && comp != null)
        {
            int id = comp.GetInstanceID();
            lock (_pedestalPresLock)
            {
                if (_pedestalPresentationIds.Contains(id) || (_lastPedestalPresentationId != 0 && _lastPedestalPresentationId == id))
                    return true;
            }
        }
        return HasPedestalFeature(presObj);
    }

    private static void PostToMainThread(Action action)
    {
        if (action == null) return;
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            action();
            return;
        }

        if (_unitySyncContext != null)
        {
            _unitySyncContext.Post(_ => action(), null);
            return;
        }

        action();
    }

    private static bool IsForceCleanedPresentation(object presObj)
    {
        if (presObj is not Component comp) return false;
        int id = comp.GetInstanceID();
        lock (_pedestalPresLock)
        {
            return _forceCleanedPresentationIds.Contains(id);
        }
    }

    private static bool ShortCircuitAndDestroyPresentation(object presObj)
    {
        if (presObj == null || (presObj is UnityEngine.Object uo && uo == null))
            return false;

        bool newlyMarked = true;
        if (presObj is Component comp)
        {
            int id = comp.GetInstanceID();
            lock (_pedestalPresLock)
            {
                if (_forceCleanedPresentationIds.Count > 128) _forceCleanedPresentationIds.Clear();
                newlyMarked = _forceCleanedPresentationIds.Add(id);
                _pedestalPresentationIds.Remove(id);
            }
        }

        // 1. 立即置位 CleanupComplete = true 并取消等待令牌，瞬间满足 <CleanUp>b__70_0 条件
        try { _bspCleanUpCompleteMethod?.Invoke(presObj, null); } catch { }
        try
        {
            if (_bspCancellationTokenField?.GetValue(presObj) is CancellationTokenSource cts && !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
        }
        catch { }

        // 2. 立即失活隐藏并销毁残余基座 GameObject（Unity 将在当帧末触发 OnDestroy 解绑全部事件，视觉当帧消失）
        if (presObj is Component presComp && presComp.gameObject != null)
        {
            if (presComp.gameObject.activeSelf)
            {
                presComp.gameObject.SetActive(false);
            }
            UnityEngine.Object.Destroy(presComp.gameObject);
        }

        return newlyMarked;
    }

    private static object GetBoardStatePresentationObj(object boardManager)
    {
        if (boardManager == null || _bmBoardStatePresProp == null) return null;
        var val = _bmBoardStatePresProp.GetValue(boardManager);
        if (val == null || (val is UnityEngine.Object uo && uo == null)) return null;
        return val;
    }

    private static void BumpBoardStatePresentationLoadVersion(object boardManager)
    {
        if (boardManager == null || _bmLoadRequestVersionField == null) return;
        if (_bmLoadRequestVersionField.GetValue(boardManager) is int curVer)
        {
            _bmLoadRequestVersionField.SetValue(boardManager, curVer + 1);
        }
    }

    private static void ExitAllUpgradePreviewsSafely()
    {
        EnsurePedestalPreviewReflectionAnchored();
        var tipParent = GetStaticDataMember("TooltipParentComponent");
        if (tipParent != null && !(tipParent is UnityEngine.Object tpuo && tpuo == null))
        {
            if (_exitUpgradePreviewAllMethod != null && _exitUpgradePreviewAllMethod.DeclaringType.IsInstanceOfType(tipParent))
            {
                _exitUpgradePreviewAllMethod.Invoke(tipParent, null);
            }

            if (_cardTooltipControllerProp != null && _cardTooltipControllerProp.DeclaringType.IsInstanceOfType(tipParent))
            {
                var tipCtrl = _cardTooltipControllerProp.GetValue(tipParent);
                if (tipCtrl != null && !(tipCtrl is UnityEngine.Object tcuo && tcuo == null))
                {
                    var mode = (_getDisplayModeMethod != null && _getDisplayModeMethod.DeclaringType.IsInstanceOfType(tipCtrl))
                        ? _getDisplayModeMethod.Invoke(tipCtrl, null)
                        : null;
                    if (mode != null && string.Equals(mode.ToString(), "PedestalUpgradeTooltip", StringComparison.Ordinal))
                    {
                        if (_setDisplayModeMethod != null && _cachedDefaultTooltipModeEnum != null
                            && _setDisplayModeMethod.DeclaringType.IsInstanceOfType(tipCtrl))
                        {
                            _setDisplayModeMethod.Invoke(tipCtrl, new[] { _cachedDefaultTooltipModeEnum });
                        }
                    }
                }
            }
        }

        var lookup = GameInspector.GetDataLookup();
        if (lookup != null && GameInspector.GetMember(lookup, "CardControllerDictionary") is IDictionary dict)
        {
            foreach (DictionaryEntry kv in dict)
            {
                var ctrl = kv.Value;
                if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) continue;
                if (GameInspector.GetMember(ctrl, "_isInUpgradePreview") is bool inPrev && inPrev)
                {
                    if (_exitUpgradePreviewCardMethod != null && _exitUpgradePreviewCardMethod.DeclaringType.IsInstanceOfType(ctrl))
                    {
                        _exitUpgradePreviewCardMethod.Invoke(ctrl, null);
                    }
                }
            }
        }
    }

    private static void PedestalUpgradePrefix(NetMessageGameSim message, out string __state)
    {
        __state = null;
        try
        {
            __state = message?.Data?.Events?.OfType<GameSimEventCardUpgraded>().FirstOrDefault()?.InstanceId;
        }
        catch { }
    }

    private static void PedestalEnchantPrefix(NetMessageGameSim message, out string __state)
    {
        __state = null;
        try
        {
            __state = message?.Data?.Events?.OfType<GameSimEventCardEnchanted>().FirstOrDefault()?.InstanceId;
        }
        catch { }
    }

    private static void PedestalUpgradeOrEnchantPostfix(object __instance, bool __result, string __state)
    {
        if (!__result) return;

        try
        {
            if (!string.IsNullOrEmpty(__state))
            {
                ForceRefreshItemControllerUi(__state);
            }

            if (CombatPatch.SpeedOverride > 1.5f || _pedestalCommitRequested)
            {
                _pedestalCommitRequested = false;
                if (__instance != null && _pedestalAnimCompletedField != null)
                {
                    _pedestalAnimCompletedField.SetValue(__instance, true);
                    Interlocked.Increment(ref _pedestalFastCompletedCount);
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PedestalPostfix: " + ex.Message);
        }
    }

    private static void ForceRefreshItemControllerUi(string instanceId)
    {
        var entities = GameInspector.GetDataEntities();
        var lookup = GameInspector.GetDataLookup();
        if (entities == null || lookup == null) return;

        var iid = new InstanceId(instanceId);
        if (!entities.Contains(iid)) return;
        var entity = entities[iid];
        if (entity == null) return;

        _getCardControllerMethod ??= lookup.GetType().GetMethod(
            "GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var ctrl = _getCardControllerMethod?.Invoke(lookup, new[] { entity });
        if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) return;

        var ctrlType = ctrl.GetType();
        for (var t = ctrlType; t != null; t = t.BaseType)
        {
            var blockField = t.GetField("blockVisualUpdates", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (blockField != null)
            {
                blockField.SetValue(ctrl, false);
                break;
            }
        }

        var mFromEvent = ctrlType.GetMethod("UpdateCardFromEvent", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (mFromEvent != null)
        {
            mFromEvent.Invoke(ctrl, null);
            return;
        }

        var mUpdate = ctrlType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "UpdateCard");
        if (mUpdate != null)
        {
            var ps = mUpdate.GetParameters();
            if (ps.Length == 0)
                mUpdate.Invoke(ctrl, null);
            else if (ps.Length == 2)
                mUpdate.Invoke(ctrl, new object[] { null, false });
            else if (ps.Length == 1)
                mUpdate.Invoke(ctrl, new object[] { null });
        }
    }

    // ── 10. RewardController.PlayDespawnAnimation：高倍速跳过 10s 退场动画死等 ──

    private static void PatchRewardController(Harmony harmony)
    {
        try
        {
            var rcType = GameInspector.FindTypeSafe("RewardController", "TheBazaar.RewardController");
            if (rcType == null) return;

            _rewardHideCompleteField = rcType.GetField("hideComplete", BindingFlags.NonPublic | BindingFlags.Instance);
            _rewardAnimatorProp = rcType.GetProperty("Animator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            _rewardSoundHandlerField = rcType.GetField("soundRewardHandler", BindingFlags.NonPublic | BindingFlags.Instance);

            var mDespawn = AccessTools.Method(rcType, "PlayDespawnAnimation");
            if (mDespawn != null)
            {
                harmony.Patch(mDespawn, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(PlayDespawnAnimationPrefix))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RewardController: " + ex.Message);
        }
    }

    private static bool PlayDespawnAnimationPrefix(object __instance, ref Task __result)
    {
        if (CombatPatch.SpeedOverride <= 1.5f || __instance == null)
            return true;

        try
        {
            if (_rewardHideCompleteField?.GetValue(__instance) is bool alreadyHidden && alreadyHidden)
                return true;

            if (__instance is not Component comp || comp.gameObject == null || !comp.gameObject.activeSelf)
                return true;

            var animator = _rewardAnimatorProp?.GetValue(__instance);
            if (animator != null)
            {
                var setBool = animator.GetType().GetMethod("SetBool", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(bool) }, null);
                setBool?.Invoke(animator, new object[] { "isVisible", false });
            }

            var soundHandler = _rewardSoundHandlerField?.GetValue(__instance);
            if (soundHandler != null)
            {
                var stopLoop = soundHandler.GetType().GetMethod("StopRewardLoop", BindingFlags.Public | BindingFlags.Instance);
                stopLoop?.Invoke(soundHandler, new object[] { comp.gameObject });
            }

            _rewardHideCompleteField?.SetValue(__instance, true);
            Interlocked.Increment(ref _rewardDespawnSkippedCount);
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PlayDespawnAnimationPrefix: " + ex.Message);
            return true;
        }
    }

    // ── 11. 悬停高亮与目标筛选对齐（#11 + 拖拽 Top 1 根治：帧级去重、零装箱、零每帧全表反射）──

    private struct CardTagSyncStamp
    {
        public long GameSimVersion;
        public Guid TemplateId;
        public int EnchantmentCode;
    }

    private static PropertyInfo _cardTagsProp;
    private static PropertyInfo _itemEnchantmentProp;
    private static MethodInfo _itemGetEnchantmentsMethod;
    private static PropertyInfo _cardCtrlCardDataProp;
    private static FieldInfo _cardCtrlBoardSectionField;
    private static MethodInfo _cardCtrlShowCardHintMethod;
    private static Type _clientCardType;

    // 预编译热路径强类型委托（彻底消除 Activator.CreateInstance、非泛型 IDictionary 装箱与逐卡反射 GetValue）
    private static Func<object, HashSet<ECardTag>> _cardGetTagsDelegate;
    private static Func<object, EEnchantmentType?> _itemGetEnchantmentDelegate;
    private static Func<object, ICard> _cardCtrlGetCardDataDelegate;
    private static Func<object, object> _cardCtrlGetBoardSectionDelegate;
    private static Func<object, bool> _itemGetIsDraggingDelegate;
    private static Func<bool> _dataGetIsStorageOpenDelegate;
    private static Func<object, object> _hintGetCardDelegate;
    private static Func<object, int> _hintGetTypeCodeDelegate;
    private static Func<IEnumerable, object, IEnumerable> _filterHintCardsFast;
    private static Func<IEnumerable, ICard, IEnumerable> _dedupeHintsFast;
    private static Action<object, object, bool, bool, HashSet<ECardState>, HashSet<InstanceId>, List<ICard>> _collectEntitiesFast;
    private static Type _itemCardRuntimeType;

    private static readonly Dictionary<InstanceId, (HashSet<ECardTag> Tags, HashSet<EHiddenTag> Hidden)> _enchDynamicSyncedTags = new();
    private static readonly Dictionary<InstanceId, CardTagSyncStamp> _cardTagSyncStamps = new();

    // 拖拽 Top 1：CardController.ShowCardHint 帧级/状态级去重缓存（主线程访问）
    private static bool _hasActiveHintCache;
    private static int _lastHintCardCtrlId;
    private static InstanceId _lastHintCardInstanceId;
    private static bool _lastHintHasSocket;
    private static int _lastHintSocketVal;
    private static int _lastHintSectionVal;
    private static bool _lastHintSellZone;
    private static long _lastHintGameSimVer;

    // 拖拽 Top 1：ItemController.PreviewMove 微位移短路 + BoardManager.SetSellZoneHovered 边沿触发（消灭二次 CheckForSellZone）
    private static WeakReference<Component> _activeDraggingItemRef;
    private static int _lastPreviewItemId;
    private static Vector3 _lastPreviewWorldPos;
    private static bool _lastPreviewStorageOpen;
    private static int _lastPreviewFrame = -100;
    private static bool _currentSellZoneHovered;

    internal static void ResetShowCardHintDedupeCache()
    {
        _hasActiveHintCache = false;
        _lastHintCardCtrlId = 0;
        _lastHintCardInstanceId = default;
        _lastHintHasSocket = false;
        _lastHintSocketVal = int.MinValue;
        _lastHintSectionVal = int.MinValue;
        _lastHintSellZone = false;
        _lastHintGameSimVer = -1;
    }

    private static void ResetDragPreviewDedupeCache()
    {
        _activeDraggingItemRef = null;
        _lastPreviewItemId = 0;
        _lastPreviewFrame = -100;
        _currentSellZoneHovered = false;
        ResetShowCardHintDedupeCache();
    }

    private static void PatchTargetingAndHints(Harmony harmony)
    {
        try
        {
            var xmostGetTargets = AccessTools.Method(
                typeof(TTargetCardXMost),
                nameof(TTargetCardXMost.GetTargets),
                new[]
                {
                    typeof(TargetingContext),
                    typeof(uint?),
                    typeof(List<ITCardConditional>),
                    typeof(HashSet<ECardState>),
                });
            if (xmostGetTargets != null)
            {
                harmony.Patch(xmostGetTargets, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(TargetCardXMostGetTargetsPrefix))));
                _patchedCount++;
            }

            var hintServiceType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Hints.HintService");
            var listHints = hintServiceType == null ? null : AccessTools.Method(hintServiceType, "ListHints");
            if (listHints != null)
            {
                harmony.Patch(
                    listHints,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(HintServiceListHintsPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(HintServiceListHintsPostfix))));
                _patchedCount++;
            }

            var hintType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Hints.Hint");
            if (hintType != null)
            {
                CompileHintAccessors(hintType);
            }

            _clientCardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.Card");
            if (_clientCardType != null)
            {
                _cardTagsProp = _clientCardType.GetProperty("Tags", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _cardGetTagsDelegate = CompilePropertyGetter<HashSet<ECardTag>>(_clientCardType, _cardTagsProp);
                BuildTypedCardHelpers(_clientCardType);
            }

            _itemCardRuntimeType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.ItemCard");
            if (_itemCardRuntimeType != null)
            {
                _itemEnchantmentProp = _itemCardRuntimeType.GetProperty("Enchantment", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _itemGetEnchantmentsMethod = _itemCardRuntimeType.GetMethod("GetEnchantments", BindingFlags.Public | BindingFlags.Instance);
                _itemGetEnchantmentDelegate = CompilePropertyGetter<EEnchantmentType?>(_itemCardRuntimeType, _itemEnchantmentProp);
            }

            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
            if (dataType != null)
            {
                var storageProp = dataType.GetProperty("IsStorageOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (storageProp != null)
                {
                    try
                    {
                        _dataGetIsStorageOpenDelegate = Expression.Lambda<Func<bool>>(Expression.Property(null, storageProp)).Compile();
                    }
                    catch
                    {
                        _dataGetIsStorageOpenDelegate = () => storageProp.GetValue(null) is bool b && b;
                    }
                }
            }

            // CardController.ShowCardHint / HideCardHints：帧级状态去重 + 清除时自动复位
            var cardCtrlType = GameInspector.FindTypeSafe("CardController", "TheBazaar.CardController");
            if (cardCtrlType != null)
            {
                _cardCtrlCardDataProp = cardCtrlType.GetProperty("CardData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                _cardCtrlBoardSectionField = FindFieldInHierarchy(cardCtrlType, "boardSection");
                _cardCtrlShowCardHintMethod = AccessTools.Method(cardCtrlType, "ShowCardHint");

                _cardCtrlGetCardDataDelegate = CompilePropertyGetter<ICard>(cardCtrlType, _cardCtrlCardDataProp);
                if (_cardCtrlBoardSectionField != null)
                {
                    try
                    {
                        var p = Expression.Parameter(typeof(object), "ctrl");
                        var fieldAcc = Expression.Field(Expression.Convert(p, cardCtrlType), _cardCtrlBoardSectionField);
                        _cardCtrlGetBoardSectionDelegate = Expression.Lambda<Func<object, object>>(Expression.Convert(fieldAcc, typeof(object)), p).Compile();
                    }
                    catch
                    {
                        _cardCtrlGetBoardSectionDelegate = obj => _cardCtrlBoardSectionField.GetValue(obj);
                    }
                }

                if (_cardCtrlShowCardHintMethod != null)
                {
                    harmony.Patch(
                        _cardCtrlShowCardHintMethod,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(CardControllerShowCardHintPrefix))));
                    _patchedCount++;
                }

                var hideHints = AccessTools.Method(cardCtrlType, "HideCardHints", Type.EmptyTypes);
                if (hideHints != null)
                {
                    harmony.Patch(
                        hideHints,
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(ResetHintDedupePostfix))));
                    _patchedCount++;
                }
            }

            // ItemController.PreviewMove Prefix 微位移短路 & FinishMove 复位（彻底删除旧 PreviewMovePostfix 的二次 CheckForSellZone！）
            var itemCtrlType = GameInspector.FindTypeSafe("ItemController", "TheBazaar.ItemController");
            if (itemCtrlType != null)
            {
                var isDraggingProp = itemCtrlType.GetProperty("IsDragging", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (isDraggingProp != null)
                {
                    _itemGetIsDraggingDelegate = CompilePropertyGetter<bool>(itemCtrlType, isDraggingProp);
                }

                var previewMove = AccessTools.Method(itemCtrlType, "PreviewMove", new[] { typeof(Vector3) });
                if (previewMove != null)
                {
                    harmony.Patch(
                        previewMove,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(ItemControllerPreviewMovePrefix))));
                    _patchedCount++;
                }

                var finishMove = AccessTools.Method(itemCtrlType, "FinishMove", new[] { typeof(bool) })
                    ?? AccessTools.Method(itemCtrlType, "FinishMove");
                if (finishMove != null)
                {
                    harmony.Patch(
                        finishMove,
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(ItemControllerFinishMovePostfix))));
                    _patchedCount++;
                }
            }

            // BoardManager.SetSellZoneHovered 边沿触发售卖区提示刷新 + ClearCardHighlights 复位去重缓存
            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                var setSellZoneHovered = AccessTools.Method(bmType, "SetSellZoneHovered", new[] { typeof(bool) });
                if (setSellZoneHovered != null)
                {
                    harmony.Patch(
                        setSellZoneHovered,
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardSetSellZoneHoveredPostfix))));
                    _patchedCount++;
                }

                var clearHighlights = AccessTools.Method(bmType, "ClearCardHighlights", Type.EmptyTypes);
                if (clearHighlights != null)
                {
                    harmony.Patch(
                        clearHighlights,
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(ResetHintDedupePostfix))));
                    _patchedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchTargetingAndHints: " + ex.Message);
        }
    }

    private static Func<object, T> CompilePropertyGetter<T>(Type declaringType, PropertyInfo prop)
    {
        if (declaringType == null || prop == null) return null;
        try
        {
            var p = Expression.Parameter(typeof(object), "inst");
            var access = Expression.Property(Expression.Convert(p, declaringType), prop);
            Expression body = typeof(T) == prop.PropertyType ? access : Expression.Convert(access, typeof(T));
            return Expression.Lambda<Func<object, T>>(body, p).Compile();
        }
        catch
        {
            return obj =>
            {
                object v = prop.GetValue(obj);
                return v is T tv ? tv : default;
            };
        }
    }

    private static void CompileHintAccessors(Type hintType)
    {
        var cardProp = hintType.GetProperty("Card", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var cardField = hintType.GetField("Card", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var typeProp = hintType.GetProperty("Type", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var typeField = hintType.GetField("Type", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (cardProp != null)
        {
            _hintGetCardDelegate = CompilePropertyGetter<object>(hintType, cardProp);
        }
        else if (cardField != null)
        {
            try
            {
                var p = Expression.Parameter(typeof(object), "h");
                var acc = Expression.Field(Expression.Convert(p, hintType), cardField);
                _hintGetCardDelegate = Expression.Lambda<Func<object, object>>(Expression.Convert(acc, typeof(object)), p).Compile();
            }
            catch { _hintGetCardDelegate = h => cardField.GetValue(h); }
        }

        if (typeProp != null)
        {
            try
            {
                var p = Expression.Parameter(typeof(object), "h");
                var acc = Expression.Property(Expression.Convert(p, hintType), typeProp);
                _hintGetTypeCodeDelegate = Expression.Lambda<Func<object, int>>(Expression.Convert(acc, typeof(int)), p).Compile();
            }
            catch { _hintGetTypeCodeDelegate = h => Convert.ToInt32(typeProp.GetValue(h) ?? 0); }
        }
        else if (typeField != null)
        {
            try
            {
                var p = Expression.Parameter(typeof(object), "h");
                var acc = Expression.Field(Expression.Convert(p, hintType), typeField);
                _hintGetTypeCodeDelegate = Expression.Lambda<Func<object, int>>(Expression.Convert(acc, typeof(int)), p).Compile();
            }
            catch { _hintGetTypeCodeDelegate = h => Convert.ToInt32(typeField.GetValue(h) ?? 0); }
        }

        try
        {
            var dedupeOpen = typeof(CompatAuditPatch).GetMethod(nameof(DedupeHintsTyped), BindingFlags.NonPublic | BindingFlags.Static);
            if (dedupeOpen != null)
            {
                var closed = dedupeOpen.MakeGenericMethod(hintType);
                _dedupeHintsFast = (Func<IEnumerable, ICard, IEnumerable>)Delegate.CreateDelegate(
                    typeof(Func<IEnumerable, ICard, IEnumerable>), closed);
            }
        }
        catch { }
    }

    private static void BuildTypedCardHelpers(Type clientCardType)
    {
        try
        {
            var filterOpen = typeof(CompatAuditPatch).GetMethod(nameof(FilterHintCardsTyped), BindingFlags.NonPublic | BindingFlags.Static);
            if (filterOpen != null)
            {
                var closed = filterOpen.MakeGenericMethod(clientCardType);
                _filterHintCardsFast = (Func<IEnumerable, object, IEnumerable>)Delegate.CreateDelegate(
                    typeof(Func<IEnumerable, object, IEnumerable>), closed);
            }

            var collectOpen = typeof(CompatAuditPatch).GetMethod(nameof(CollectEntitiesTyped), BindingFlags.NonPublic | BindingFlags.Static);
            if (collectOpen != null)
            {
                var closed = collectOpen.MakeGenericMethod(clientCardType);
                _collectEntitiesFast = (Action<object, object, bool, bool, HashSet<ECardState>, HashSet<InstanceId>, List<ICard>>)Delegate.CreateDelegate(
                    typeof(Action<object, object, bool, bool, HashSet<ECardState>, HashSet<InstanceId>, List<ICard>>), closed);
            }
        }
        catch { }
    }

    private static FieldInfo FindFieldInHierarchy(Type type, string fieldName)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) return f;
        }
        return null;
    }

    // ── 零分配强类型槽位比较器（彻底替换 6 处 LINQ OrderBy/ThenBy/ToList） ──

    private static readonly Comparison<ICard> XMostCardSlotComparison = static (a, b) =>
    {
        int secA = a.Section == EInventorySection.Stash ? 1 : 0;
        int secB = b.Section == EInventorySection.Stash ? 1 : 0;
        int cmp = secA.CompareTo(secB);
        if (cmp != 0) return cmp;
        int sockA = a.LeftSocketId.HasValue ? (int)a.LeftSocketId.Value : int.MaxValue;
        int sockB = b.LeftSocketId.HasValue ? (int)b.LeftSocketId.Value : int.MaxValue;
        return sockA.CompareTo(sockB);
    };

    private static void PruneNullSocketAndSortInPlace(List<ICard> list)
    {
        int write = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c != null && c.LeftSocketId.HasValue)
            {
                list[write++] = c;
            }
        }
        if (write < list.Count)
        {
            list.RemoveRange(write, list.Count - write);
        }
        if (list.Count > 1)
        {
            list.Sort(XMostCardSlotComparison);
        }
    }

    /// <summary>
    /// 修复 #11 + 拖拽 Top 1 零 LINQ/零装箱重构：
    /// 严格对齐服务端 TargetResolver（Collect -> 按需版本同步附魔标签 -> ExcludeSelf(引用+InstanceId) -> 原位槽位排序 -> Conditions 过滤 -> Priority -> LeftMost/RightMost）。
    /// </summary>
    private static bool TargetCardXMostGetTargetsPrefix(
        TTargetCardXMost __instance,
        TargetingContext ctx,
        uint? NumberOfAllowedTargets,
        List<ITCardConditional> targetingPriorities,
        HashSet<ECardState> cardStateFilter,
        ref List<ICard> __result)
    {
        try
        {
            if (__instance == null || ctx.Run == null)
                return true;

            SyncClientCardTagsAndHiddenTags(ctx.TargetingCard);

            var candidates = CollectXMostCandidates(__instance.TargetSection, ctx, cardStateFilter);
            for (int i = 0; i < candidates.Count; i++)
            {
                SyncClientCardTagsAndHiddenTags(candidates[i]);
            }

            // 1. 先按引用与 InstanceId 双重严格排除自身（对齐服务端 ExcludeSelf 优先于条件筛选与极值选取）
            if (ctx.TargetingCard != null && __instance.ExcludeSelf)
            {
                var selfId = ctx.TargetingCard.GetInstanceId();
                for (int i = candidates.Count - 1; i >= 0; i--)
                {
                    var c = candidates[i];
                    if (c == null || ReferenceEquals(c, ctx.TargetingCard) || c.GetInstanceId() == selfId)
                    {
                        candidates.RemoveAt(i);
                    }
                }
            }

            // 2. 原位过滤未入格卡牌并按真实槽位升序稳定排序（Hand 优先于 Stash），0 LINQ 分配
            PruneNullSocketAndSortInPlace(candidates);

            // 3. 应用 Conditions 条件筛选后立即重新按槽位升序排列（防止 HashSet 破坏槽位顺序）
            if (__instance.Conditions != null && candidates.Count > 0)
            {
                var condCtx = new CardConditionalContext(ctx.Run, candidates, ctx.TargetingCard, ctx.EventContext);
                var set = _tsCardSet ??= new HashSet<ICard>();
                set.Clear();
                for (int i = 0; i < candidates.Count; i++) set.Add(candidates[i]);

                var filtered = __instance.Conditions.Filter(set, condCtx);
                candidates.Clear();
                if (filtered != null)
                {
                    foreach (var fc in filtered)
                    {
                        if (fc != null && fc.LeftSocketId.HasValue)
                        {
                            candidates.Add(fc);
                        }
                    }
                    if (candidates.Count > 1)
                    {
                        candidates.Sort(XMostCardSlotComparison);
                    }
                }
            }

            // 4. 若指定了优先级条件列表且允许目标数 > 0，按优先级保留并在同级内继续维持槽位顺序
            if (candidates.Count > 0 && NumberOfAllowedTargets.GetValueOrDefault() > 0 && targetingPriorities != null && targetingPriorities.Count > 0)
            {
                var prioritized = new List<ICard>(candidates.Count);
                var seenIds = _tsSeenInstanceIds ??= new HashSet<InstanceId>();
                seenIds.Clear();
                var fullSet = _tsCardSet ??= new HashSet<ICard>();
                fullSet.Clear();
                for (int i = 0; i < candidates.Count; i++) fullSet.Add(candidates[i]);

                var priScratch = _tsPriScratch ??= new List<ICard>(16);
                var priCtx = new CardConditionalContext(ctx.Run, candidates, ctx.TargetingCard, ctx.EventContext);

                for (int pi = 0; pi < targetingPriorities.Count; pi++)
                {
                    var priCond = targetingPriorities[pi];
                    if (priCond == null) continue;
                    var matched = priCond.Filter(fullSet, priCtx);
                    if (matched == null || matched.Count == 0) continue;

                    priScratch.Clear();
                    foreach (var mc in matched)
                    {
                        if (mc != null && mc.LeftSocketId.HasValue)
                        {
                            priScratch.Add(mc);
                        }
                    }
                    if (priScratch.Count > 1)
                    {
                        priScratch.Sort(XMostCardSlotComparison);
                    }

                    for (int mi = 0; mi < priScratch.Count; mi++)
                    {
                        var mc = priScratch[mi];
                        if (seenIds.Add(mc.GetInstanceId()))
                        {
                            prioritized.Add(mc);
                        }
                    }
                }

                if (prioritized.Count > 0)
                {
                    candidates = prioritized;
                }
            }

            if (candidates.Count == 0)
            {
                __result = new List<ICard>(0);
                return false;
            }

            __result = __instance.TargetMode switch
            {
                ETargetCardXMostTargetMode.LeftMostCard => new List<ICard>(1) { candidates[0] },
                ETargetCardXMostTargetMode.RightMostCard => new List<ICard>(1) { candidates[candidates.Count - 1] },
                _ => new List<ICard>(0),
            };
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.TargetCardXMostGetTargetsPrefix: " + ex.Message);
            return true;
        }
    }

    /// <summary>零装箱强类型 Data.Entities 枚举器（彻底消除非泛型 IDictionary 遍历时的 InstanceId + DictionaryEntry 装箱）。</summary>
    private static void CollectEntitiesTyped<TCard>(
        object entitiesObj,
        object localPlayer,
        bool wantHand,
        bool wantStash,
        HashSet<ECardState> cardStateFilter,
        HashSet<InstanceId> seen,
        List<ICard> list) where TCard : class, ICard
    {
        if (entitiesObj is Dictionary<InstanceId, TCard> typedDict)
        {
            foreach (var kv in typedDict)
            {
                var ent = kv.Value;
                if (ent == null || ent.Type != ECardType.Item || !ReferenceEquals(ent.Owner, localPlayer)) continue;
                if (!ent.LeftSocketId.HasValue) continue;
                var sec = ent.Section;
                if (sec == EInventorySection.Hand)
                {
                    if (!wantHand) continue;
                }
                else if (sec == EInventorySection.Stash)
                {
                    if (!wantStash) continue;
                }
                else
                {
                    continue;
                }
                bool stateOk = cardStateFilter != null ? cardStateFilter.Contains(ent.State) : ent.State == ECardState.Alive;
                if (!stateOk) continue;
                if (seen.Add(kv.Key))
                {
                    list.Add(ent);
                }
            }
            return;
        }

        if (entitiesObj is IDictionary fallbackDict)
        {
            foreach (DictionaryEntry kv in fallbackDict)
            {
                if (kv.Value is not ICard ent) continue;
                if (ent.Type != ECardType.Item || !ReferenceEquals(ent.Owner, localPlayer)) continue;
                if (!ent.LeftSocketId.HasValue) continue;
                if (ent.Section == EInventorySection.Hand && !wantHand) continue;
                if (ent.Section == EInventorySection.Stash && !wantStash) continue;
                if (ent.Section != EInventorySection.Hand && ent.Section != EInventorySection.Stash) continue;
                bool stateOk = cardStateFilter != null ? cardStateFilter.Contains(ent.State) : ent.State == ECardState.Alive;
                if (!stateOk) continue;
                if (seen.Add(ent.GetInstanceId()))
                {
                    list.Add(ent);
                }
            }
        }
    }

    private static List<ICard> CollectXMostCandidates(ETargetCardXMostTargetSection section, TargetingContext ctx, HashSet<ECardState> cardStateFilter)
    {
        IEnumerable<ICard> raw = section switch
        {
            ETargetCardXMostTargetSection.AbsolutePlayerHand => TargetSystem.GetAbsolutePlayerHand(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.AbsolutePlayerStash => TargetSystem.GetAbsolutePlayerStash(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.AbsolutePlayerHandAndStash => TargetSystem.GetAbsolutePlayerHandAndStash(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.AllHands => TargetSystem.GetAllHands(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.AllHandsAndStashes => TargetSystem.GetAllHandsAndStashes(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.AllStashes => TargetSystem.GetAllStashes(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.SelfHand => TargetSystem.GetPlayerHand(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.SelfHandAndStash => TargetSystem.GetPlayerHandAndStash(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.SelfStash => TargetSystem.GetPlayerStash(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.OpponentHand => TargetSystem.GetOpponentHand(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.OpponentHandAndStash => TargetSystem.GetOpponentHandAndStash(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.OpponentStash => TargetSystem.GetOpponentStash(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.AbsolutePlayerSocketEffects => TargetSystem.GetAbsolutePlayerSocketEffects(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.SelfSocketEffects => TargetSystem.GetPlayerSocketEffects(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.OpponentSocketEffects => TargetSystem.GetOpponentSocketEffects(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.AllSocketEffects => TargetSystem.GetAllSocketEffects(ctx.Run, cardStateFilter),
            _ => Array.Empty<ICard>(),
        };

        var list = new List<ICard>(16);
        var seen = _tsCollectSeenIds ??= new HashSet<InstanceId>();
        seen.Clear();
        if (raw != null)
        {
            foreach (var c in raw)
            {
                if (c != null && seen.Add(c.GetInstanceId()))
                {
                    list.Add(c);
                }
            }
        }

        // 若目标为本地玩家的 Hand/Stash，额外结合 Data.Entities 对账，防止客户端拖动/购买后 Container.Sockets 尚未同步导致漏卡或错序
        bool wantHand = section is ETargetCardXMostTargetSection.AbsolutePlayerHand
            or ETargetCardXMostTargetSection.AbsolutePlayerHandAndStash
            or ETargetCardXMostTargetSection.SelfHand
            or ETargetCardXMostTargetSection.SelfHandAndStash;
        bool wantStash = section is ETargetCardXMostTargetSection.AbsolutePlayerStash
            or ETargetCardXMostTargetSection.AbsolutePlayerHandAndStash
            or ETargetCardXMostTargetSection.SelfStash
            or ETargetCardXMostTargetSection.SelfHandAndStash;

        if (wantHand || wantStash)
        {
            var localPlayer = ctx.Run.GetPlayer();
            bool isAbsolute = section is ETargetCardXMostTargetSection.AbsolutePlayerHand
                or ETargetCardXMostTargetSection.AbsolutePlayerStash
                or ETargetCardXMostTargetSection.AbsolutePlayerHandAndStash;
            var resolvedPlayer = isAbsolute ? localPlayer : (ctx.TargetingCard?.Owner ?? localPlayer);

            if (localPlayer != null && ReferenceEquals(resolvedPlayer, localPlayer))
            {
                var entities = GameInspector.GetDataEntities();
                if (entities != null)
                {
                    // 先原位踢掉已离场或已不属于该分区的旧 Container 残留项
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var c = list[i];
                        if (ReferenceEquals(c.Owner, localPlayer)
                            && ((c.Section != EInventorySection.Hand && c.Section != EInventorySection.Stash)
                                || (c.Section == EInventorySection.Hand && !wantHand)
                                || (c.Section == EInventorySection.Stash && !wantStash)))
                        {
                            list.RemoveAt(i);
                        }
                    }

                    seen.Clear();
                    for (int i = 0; i < list.Count; i++)
                    {
                        seen.Add(list[i].GetInstanceId());
                    }

                    if (_collectEntitiesFast != null)
                    {
                        _collectEntitiesFast(entities, localPlayer, wantHand, wantStash, cardStateFilter, seen, list);
                    }
                    else
                    {
                        foreach (DictionaryEntry kv in entities)
                        {
                            if (kv.Value is not ICard ent) continue;
                            if (ent.Type != ECardType.Item || !ReferenceEquals(ent.Owner, localPlayer)) continue;
                            if (!ent.LeftSocketId.HasValue) continue;
                            if (ent.Section == EInventorySection.Hand && !wantHand) continue;
                            if (ent.Section == EInventorySection.Stash && !wantStash) continue;
                            if (ent.Section != EInventorySection.Hand && ent.Section != EInventorySection.Stash) continue;
                            bool stateOk = cardStateFilter != null ? cardStateFilter.Contains(ent.State) : ent.State == ECardState.Alive;
                            if (!stateOk) continue;
                            if (seen.Add(ent.GetInstanceId()))
                            {
                                list.Add(ent);
                            }
                        }
                    }
                }
            }
        }

        return list;
    }

    // 悬停/拖拽热路径 ThreadStatic 复用缓冲池（0 每帧 HashSet/List 分配）
    [ThreadStatic] private static HashSet<ECardTag> _tsDesiredTags;
    [ThreadStatic] private static HashSet<EHiddenTag> _tsDesiredHidden;
    [ThreadStatic] private static HashSet<ECardTag> _tsEnchAddedTags;
    [ThreadStatic] private static HashSet<EHiddenTag> _tsEnchAddedHidden;
    [ThreadStatic] private static HashSet<ICard> _tsCardSet;
    [ThreadStatic] private static HashSet<InstanceId> _tsSeenInstanceIds;
    [ThreadStatic] private static HashSet<InstanceId> _tsCollectSeenIds;
    [ThreadStatic] private static List<ICard> _tsPriScratch;
    [ThreadStatic] private static HashSet<(InstanceId, int)> _tsSeenHintPairs;

    /// <summary>
    /// 拖拽 Top 1 根治：按 (GameSimVersion, TemplateId, Enchantment) 脏标按需同步，同版本同附魔 O(1) 零反射直接返回；
    /// 附魔定义优先走强类型 TCardItem.Enchantments 避免 GetEnchantments() 分配 ReadOnlyDictionary，且未变化时不新建 HashSet！
    /// </summary>
    private static void SyncClientCardTagsAndHiddenTags(ICard card)
    {
        if (card == null) return;
        try
        {
            InstanceId iid = card.GetInstanceId();
            var tpl = card.GetTemplate();
            Guid tplId = tpl != null ? tpl.Id : Guid.Empty;

            EEnchantmentType? enchNullable = null;
            if (_itemCardRuntimeType != null && _itemCardRuntimeType.IsInstanceOfType(card))
            {
                if (_itemGetEnchantmentDelegate != null)
                {
                    enchNullable = _itemGetEnchantmentDelegate(card);
                }
                else if (_itemEnchantmentProp != null && _itemEnchantmentProp.DeclaringType.IsInstanceOfType(card))
                {
                    if (_itemEnchantmentProp.GetValue(card) is EEnchantmentType et) enchNullable = et;
                }
            }

            int enchCode = enchNullable.HasValue ? (int)enchNullable.Value : -1;
            long curVer = Volatile.Read(ref _gameSimVersion);

            if (_cardTagSyncStamps.TryGetValue(iid, out var stamp)
                && stamp.GameSimVersion == curVer
                && stamp.EnchantmentCode == enchCode
                && stamp.TemplateId == tplId)
            {
                return;
            }

            var desiredTags = _tsDesiredTags ??= new HashSet<ECardTag>();
            var desiredHidden = _tsDesiredHidden ??= new HashSet<EHiddenTag>();
            var enchAddedTags = _tsEnchAddedTags ??= new HashSet<ECardTag>();
            var enchAddedHidden = _tsEnchAddedHidden ??= new HashSet<EHiddenTag>();
            desiredTags.Clear();
            desiredHidden.Clear();
            enchAddedTags.Clear();
            enchAddedHidden.Clear();

            if (tpl != null)
            {
                if (tpl.HiddenTags != null)
                {
                    foreach (var ht in tpl.HiddenTags) desiredHidden.Add(ht);
                }
                if (tpl.Tags != null)
                {
                    foreach (var tg in tpl.Tags) desiredTags.Add(tg);
                }
            }

            if (enchNullable.HasValue)
            {
                EEnchantmentType enchType = enchNullable.Value;
                TEnchantment enchDef = null;
                if (tpl is TCardItem itemTpl && itemTpl.Enchantments != null)
                {
                    itemTpl.Enchantments.TryGetValue(enchType, out enchDef);
                }
                else if (_itemGetEnchantmentsMethod != null && _itemGetEnchantmentsMethod.DeclaringType.IsInstanceOfType(card))
                {
                    if (_itemGetEnchantmentsMethod.Invoke(card, null) is IReadOnlyDictionary<EEnchantmentType, TEnchantment> roDict)
                    {
                        roDict.TryGetValue(enchType, out enchDef);
                    }
                }

                if (enchDef != null)
                {
                    if (enchDef.HiddenTags != null)
                    {
                        foreach (var ht in enchDef.HiddenTags)
                        {
                            if (!desiredHidden.Contains(ht)) enchAddedHidden.Add(ht);
                            desiredHidden.Add(ht);
                        }
                    }
                    if (enchDef.Tags != null)
                    {
                        foreach (var tg in enchDef.Tags)
                        {
                            if (!desiredTags.Contains(tg)) enchAddedTags.Add(tg);
                            desiredTags.Add(tg);
                        }
                    }
                }
            }

            HashSet<ECardTag> cardTags = _cardGetTagsDelegate != null && _clientCardType != null && _clientCardType.IsInstanceOfType(card)
                ? _cardGetTagsDelegate(card)
                : (_cardTagsProp != null && _cardTagsProp.DeclaringType.IsInstanceOfType(card) ? _cardTagsProp.GetValue(card) as HashSet<ECardTag> : null);

            bool hasPrev = _enchDynamicSyncedTags.TryGetValue(iid, out var prevSynced);
            if (hasPrev)
            {
                if (card.HiddenTags != null && prevSynced.Hidden.Count > 0)
                {
                    foreach (var oldHt in prevSynced.Hidden)
                    {
                        if (!desiredHidden.Contains(oldHt)) card.HiddenTags.Remove(oldHt);
                    }
                }
                if (cardTags != null && prevSynced.Tags.Count > 0)
                {
                    foreach (var oldTg in prevSynced.Tags)
                    {
                        if (!desiredTags.Contains(oldTg)) cardTags.Remove(oldTg);
                    }
                }
            }

            if (card.HiddenTags != null)
            {
                foreach (var ht in desiredHidden) card.HiddenTags.Add(ht);
            }
            if (cardTags != null)
            {
                foreach (var tg in desiredTags) cardTags.Add(tg);
            }

            if (enchAddedTags.Count > 0 || enchAddedHidden.Count > 0)
            {
                // 仅当附魔动态标签集合真正变化时才替换缓存集合，彻底消灭无意义的 new HashSet×2 分配
                bool unchanged = hasPrev
                    && prevSynced.Tags.Count == enchAddedTags.Count
                    && prevSynced.Hidden.Count == enchAddedHidden.Count
                    && prevSynced.Tags.SetEquals(enchAddedTags)
                    && prevSynced.Hidden.SetEquals(enchAddedHidden);
                if (!unchanged)
                {
                    _enchDynamicSyncedTags[iid] = (new HashSet<ECardTag>(enchAddedTags), new HashSet<EHiddenTag>(enchAddedHidden));
                }
            }
            else if (hasPrev)
            {
                _enchDynamicSyncedTags.Remove(iid);
            }

            if (_cardTagSyncStamps.Count > 256)
            {
                _cardTagSyncStamps.Clear();
            }
            _cardTagSyncStamps[iid] = new CardTagSyncStamp
            {
                GameSimVersion = curVer,
                TemplateId = tplId,
                EnchantmentCode = enchCode,
            };
        }
        catch { }
    }

    private static IEnumerable FilterHintCardsTyped<TCard>(IEnumerable allCards, object me) where TCard : class
    {
        var typedList = new List<TCard>(24);
        ICard meCard = me as ICard;
        InstanceId meId = meCard != null ? meCard.GetInstanceId() : default;

        if (allCards is ICollection<TCard> typedCol)
        {
            foreach (var item in typedCol)
            {
                if (item == null) continue;
                if (item is ICard card)
                {
                    SyncClientCardTagsAndHiddenTags(card);
                    bool isSelf = ReferenceEquals(item, me) || (meCard != null && card.GetInstanceId() == meId);
                    if (!isSelf)
                    {
                        if (card.Owner == null || card.State != ECardState.Alive) continue;
                        if (card.Type == ECardType.Item && card.Section != EInventorySection.Hand && card.Section != EInventorySection.Stash) continue;
                    }
                }
                typedList.Add(item);
            }
            return typedList;
        }

        foreach (var obj in allCards)
        {
            if (obj is not TCard item) continue;
            if (item is ICard card)
            {
                SyncClientCardTagsAndHiddenTags(card);
                bool isSelf = ReferenceEquals(item, me) || (meCard != null && card.GetInstanceId() == meId);
                if (!isSelf)
                {
                    if (card.Owner == null || card.State != ECardState.Alive) continue;
                    if (card.Type == ECardType.Item && card.Section != EInventorySection.Hand && card.Section != EInventorySection.Stash) continue;
                }
            }
            typedList.Add(item);
        }
        return typedList;
    }

    private static IEnumerable DedupeHintsTyped<THint>(IEnumerable rawHints, ICard meCard) where THint : class
    {
        var filteredList = new List<THint>(8);
        InstanceId selfId = meCard.GetInstanceId();
        var seenPairs = _tsSeenHintPairs ??= new HashSet<(InstanceId, int)>();
        seenPairs.Clear();

        foreach (var obj in rawHints)
        {
            if (obj is not THint h) continue;
            var targetObj = _hintGetCardDelegate?.Invoke(h);
            if (targetObj is ICard targetCard)
            {
                InstanceId targetId = targetCard.GetInstanceId();
                if (ReferenceEquals(targetObj, meCard) || targetId == selfId)
                    continue;

                int typeCode = _hintGetTypeCodeDelegate != null ? _hintGetTypeCodeDelegate(h) : 0;
                if (!seenPairs.Add((targetId, typeCode)))
                    continue;
            }
            filteredList.Add(h);
        }

        return filteredList;
    }

    private static void HintServiceListHintsPrefix(object me, ref IEnumerable allCards)
    {
        try
        {
            if (me is ICard meCard)
            {
                SyncClientCardTagsAndHiddenTags(meCard);
            }

            if (allCards == null || _filterHintCardsFast == null) return;
            allCards = _filterHintCardsFast(allCards, me);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.HintServiceListHintsPrefix: " + ex.Message);
        }
    }

    private static void HintServiceListHintsPostfix(object me, ref IEnumerable __result)
    {
        try
        {
            if (__result == null || me is not ICard meCard || _dedupeHintsFast == null) return;
            __result = _dedupeHintsFast(__result, meCard);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.HintServiceListHintsPostfix: " + ex.Message);
        }
    }

    /// <summary>
    /// 拖拽 Top 1：CardController.ShowCardHint 帧级/状态级去重。
    /// 当同一卡牌在同一槽位、同一分区、同一售卖区状态且 GameSim 版本未变化时直接短路，砍掉 95%+ 无效每帧高亮重算！
    /// </summary>
    private static bool CardControllerShowCardHintPrefix(object __instance, EContainerSocketId? socketId, object section)
    {
        try
        {
            if (__instance is not UnityEngine.Object uo || uo == null) return true;

            int ctrlId = uo.GetInstanceID();
            ICard cardData = _cardCtrlGetCardDataDelegate?.Invoke(__instance);
            InstanceId cardId = cardData != null ? cardData.GetInstanceId() : default;
            bool hasSocket = socketId.HasValue;
            int sockVal = hasSocket ? (int)socketId.Value : -1;
            int secVal = section != null ? Convert.ToInt32(section) : -1;
            bool sellZone = _currentSellZoneHovered;
            long simVer = Volatile.Read(ref _gameSimVersion);

            if (_hasActiveHintCache
                && ctrlId == _lastHintCardCtrlId
                && cardId == _lastHintCardInstanceId
                && hasSocket == _lastHintHasSocket
                && sockVal == _lastHintSocketVal
                && secVal == _lastHintSectionVal
                && sellZone == _lastHintSellZone
                && simVer == _lastHintGameSimVer)
            {
                return false;
            }

            _hasActiveHintCache = true;
            _lastHintCardCtrlId = ctrlId;
            _lastHintCardInstanceId = cardId;
            _lastHintHasSocket = hasSocket;
            _lastHintSocketVal = sockVal;
            _lastHintSectionVal = secVal;
            _lastHintSellZone = sellZone;
            _lastHintGameSimVer = simVer;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CardControllerShowCardHintPrefix: " + ex.Message);
        }
        return true;
    }

    private static void ResetHintDedupePostfix()
    {
        try
        {
            ResetShowCardHintDedupeCache();
        }
        catch { }
    }

    /// <summary>
    /// 拖拽 Top 1：ItemController.PreviewMove 微位移短路守卫。
    /// 记录活跃拖拽卡；若连续拖拽帧内世界坐标变化 &lt; 0.002f 且储藏箱开关未变化，仅更新 transform.position 并跳过本帧重复 4 次物理检测与悬停重算。
    /// </summary>
    private static bool ItemControllerPreviewMovePrefix(Component __instance, Vector3 worldPosition, ref bool __result)
    {
        try
        {
            if (__instance == null) return true;
            _activeDraggingItemRef = new WeakReference<Component>(__instance);

            if (_itemGetIsDraggingDelegate != null && !_itemGetIsDraggingDelegate(__instance))
            {
                _lastPreviewItemId = 0;
                return true;
            }

            int id = __instance.GetInstanceID();
            int frame = Time.frameCount;
            bool storageOpen = _dataGetIsStorageOpenDelegate != null && _dataGetIsStorageOpenDelegate();

            if (id == _lastPreviewItemId
                && storageOpen == _lastPreviewStorageOpen
                && frame - _lastPreviewFrame <= 2
                && (worldPosition - _lastPreviewWorldPos).sqrMagnitude < 0.000004f)
            {
                __instance.transform.position = worldPosition;
                _lastPreviewFrame = frame;
                __result = true;
                return false;
            }

            _lastPreviewItemId = id;
            _lastPreviewWorldPos = worldPosition;
            _lastPreviewStorageOpen = storageOpen;
            _lastPreviewFrame = frame;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ItemControllerPreviewMovePrefix: " + ex.Message);
        }
        return true;
    }

    /// <summary>
    /// 拖拽 Top 1：取代旧 PreviewMovePostfix 的每帧二次 CheckForSellZone()！
    /// 仅在 BoardManager.SetSellZoneHovered(bool) 状态真正发生跳变时触发一次，进入售卖区瞬间以真实 LeftSocketId 刷新高亮。
    /// </summary>
    private static void BoardSetSellZoneHoveredPostfix(bool isHovered)
    {
        try
        {
            if (isHovered == _currentSellZoneHovered) return;
            _currentSellZoneHovered = isHovered;
            ResetShowCardHintDedupeCache();

            if (!isHovered || _cardCtrlShowCardHintMethod == null) return;
            if (_activeDraggingItemRef == null || !_activeDraggingItemRef.TryGetTarget(out var draggingItem) || draggingItem == null)
                return;

            var cardData = _cardCtrlGetCardDataDelegate?.Invoke(draggingItem);
            var boardSection = _cardCtrlGetBoardSectionDelegate?.Invoke(draggingItem);
            if (cardData == null || boardSection == null) return;

            _cardCtrlShowCardHintMethod.Invoke(draggingItem, new object[] { cardData.LeftSocketId, boardSection });
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.BoardSetSellZoneHoveredPostfix: " + ex.Message);
        }
    }

    private static void ItemControllerFinishMovePostfix()
    {
        try
        {
            ResetDragPreviewDedupeCache();
            BazaarLauncher.Modules.PerfOptimizerModule.ResetDragHoverCaches();
        }
        catch { }
    }

}
