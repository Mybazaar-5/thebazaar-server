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
using BazaarLauncher.Core;
using HarmonyLib;
using UnityEngine;

namespace BazaarLauncher;

internal static partial class CompatAuditPatch
{
    // ── 10. PedestalState：升阶/附魔即时刷新 UI、悬停预览双保险与退出恢复 ─────

    // P0-17：祭坛提交拒单/异常中断解砖状态与反射句柄（仅定义于本局部文件，兼容 netstandard2.1）
    private static Type _pedestalStateType;
    private static MethodInfo _bmClearUpgradeOrFuseOrEnchantMethod;
    private static PropertyInfo _bmIsProcessingUpgradeProp;
    private static MethodInfo _bmIsProcessingUpgradeSetter;
    private static PropertyInfo _bmIsPlayingUpgradeOrFuseAnimProp;
    private static FieldInfo _bmEnchantManagerField;
    private static PropertyInfo _enchantMgrIsEnchantingProp;
    private static MethodInfo _bmIsObservedBoardSyncActiveMethod;
    private static MethodInfo _cardCtrlEnableMovementMethod;
    private static FieldInfo _cardCtrlMovementEnabledField;
    private static MethodInfo _itemCtrlMoveBackMethod;
    private static FieldInfo _cmdIsProcessingField;

    // P1-42：祭坛呈现原生 CleanUp + 1.8s 看门狗、DialogOpen/EnableMovement 兜底复位及 Animator PlayableGraph 竞态延后句柄
    private const int PresentationCleanUpWatchdogMs = 1800;
    private static PropertyInfo _bspCleanupCompleteProp;
    private static PropertyInfo _bmDialogOpenProp;
    private static MethodInfo _bmRequestDialogCloseMethod;
    private static FieldInfo _cardCtrlAnimatorField;
    private static PropertyInfo _cardCtrlAnimatorProp;
    private static PropertyInfo _animatorRuntimeControllerProp;
    private static Type _popupManagerType;
    private static PropertyInfo _popupMgrIsAnyPopupOpenProp;
    private static PropertyInfo _popupMgrIsSettingsOpenProp;
    private static MethodInfo _servicesGetMethod;
    private static readonly HashSet<int> _cleaningUpPresentationIds = new();
    private static int _uiRefreshGeneration;

    // P1-13：祭坛升级视觉品阶一致性校验句柄（ItemVisualsController.currentTier vs CardData.Tier）
    private static string _lastPedestalUpgradedCardId;
    private static int _pedestalVisualTierFixedCount;
    private static PropertyInfo _itemCtrlVisualsProp;
    private static FieldInfo _itemCtrlVisualsField;
    private static FieldInfo _ivcCurrentTierField;

    // P2-32：Mod 链式祭坛（Pedestal -> Pedestal/Encounter）在 AppState.SetState 前写回有效下一跳 CurrentEncounterId
    private static PropertyInfo _dataCurrentEncounterIdProp;
    private static MethodInfo _bmTryLoadCurrentEncounterSoMethod;
    private static Guid _pendingChainedPedestalEncounterId;
    private static ERunState _pendingChainedPedestalTargetState;
    private static float _pendingChainedPedestalUntil;

    // P2-42：CardTooltipController.ClearCurrentCard 原生死代码退订修补反射句柄
    private static FieldInfo _ctcIsInitializedField;
    private static FieldInfo _ctcIsLockedField;
    private static FieldInfo _ctcPrimaryControllerField;
    private static MethodInfo _ctcOnPrimaryShowCompletedMethod;
    private static EventInfo _btcOnShowCompletedEvent;

    private static WeakReference<object> _pendingPedestalItemCtrlRef;
    private static InstanceId _pendingPedestalCardId;
    private static volatile bool _hasPendingPedestalCommit;
    private static volatile bool _pedestalCommitInFlight;
    private static int _pedestalCommitGeneration;
    private static InstanceId _lastRecoveredPedestalCardId;
    private static bool _hasLastRecoveredPedestalCardId;
    private static float _lastRecoveredPedestalAt = -100f;
    private static int _pedestalCommitRecoveredCount;
    [ThreadStatic] private static Stack<bool> _pedestalCmdWasBusyStack;
    [ThreadStatic] private static bool _inSendCommitToPedestalCall;

    // P1-27：BoardTransitionController 切板避让与 IsTransitioning 兜底复位反射句柄
    private static FieldInfo _bmBoardTransitionField;
    private static Type _btcType;
    private static PropertyInfo _btcIsTransitioningProp;
    private static MethodInfo _btcIsTransitioningSetter;
    private static FieldInfo _btcIsTransitioningBackingField;
    private static int _activeBoardTransitionCount;
    private static int _boardTransitionResetCount;
    [ThreadStatic] private static Stack<bool> _boardTransitionWasAlreadyRunningStack;

    private static void PatchPedestalState(SafeHarmonyPatcher patcher)
    {
        try
        {
            var pedType = GameInspector.FindTypeSafe("TheBazaar.PedestalState");
            if (pedType == null) return;
            _pedestalStateType = pedType;

            _pedestalAnimCompletedField = pedType.GetField("_animationCompleted", BindingFlags.NonPublic | BindingFlags.Instance);
            _pedestalTemplateField = pedType.GetField("_pedestalTemplate", BindingFlags.NonPublic | BindingFlags.Instance);

            var onEnter = AccessTools.Method(pedType, "OnEnter");
            if (onEnter != null && patcher.TryPatchMethod(
                onEnter,
                postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalOnEnterPostfix)),
                optional: true,
                label: "PedestalState.OnEnter"))
            {
                _pedestalHintsPatchedCount++;
            }

            // D4-P2-3：PedestalState 若未声明重写 OnExit，显式瞄准其基类 TheBazaar.AppState.OnExit，消除基类方法隐式漂移
            var appStateType = GameInspector.FindTypeSafe("TheBazaar.AppState");
            var onExit = pedType.GetMethod("OnExit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null)
                ?? (appStateType != null ? AccessTools.Method(appStateType, "OnExit", Type.EmptyTypes) : AccessTools.Method(pedType, "OnExit", Type.EmptyTypes));
            if (onExit != null && patcher.TryPatchMethod(
                onExit,
                prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalOnExitPrefix)),
                optional: true,
                label: onExit.DeclaringType == pedType ? "PedestalState.OnExit" : "AppState.OnExit(PedestalGuard)"))
            {
                _pedestalHintsPatchedCount++;
            }

            var onGameSim = AccessTools.Method(pedType, "OnGameSimMessage", new[] { typeof(NetMessageGameSim) });
            if (onGameSim != null && patcher.TryPatchMethod(
                onGameSim,
                prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalOnGameSimMessagePrefix)),
                optional: true,
                label: "PedestalState.OnGameSimMessage"))
            {
                _pedestalHintsPatchedCount++;
            }

            var mUpd = AccessTools.Method(pedType, "HandleCardUpgradedEvent", new[] { typeof(NetMessageGameSim) });
            if (mUpd != null && patcher.TryPatchMethod(
                mUpd,
                prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalUpgradePrefix)),
                postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalUpgradeOrEnchantPostfix)),
                optional: true,
                label: "PedestalState.HandleCardUpgradedEvent"))
            {
                _pedestalHintsPatchedCount++;
            }

            var mEnch = AccessTools.Method(pedType, "HandleCardEnchantmentEvent", new[] { typeof(NetMessageGameSim) });
            if (mEnch != null && patcher.TryPatchMethod(
                mEnch,
                prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalEnchantPrefix)),
                postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalUpgradeOrEnchantPostfix)),
                optional: true,
                label: "PedestalState.HandleCardEnchantmentEvent"))
            {
                _pedestalHintsPatchedCount++;
            }

            // P1-13：PedestalState.CompleteTransition 收尾后核验视觉品阶与 CardData.Tier 是否一致
            var mCompleteTrans = AccessTools.Method(pedType, "CompleteTransition", Type.EmptyTypes);
            if (mCompleteTrans != null && patcher.TryPatchMethod(
                mCompleteTrans,
                postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalCompleteTransitionPostfix)),
                optional: true,
                label: "PedestalState.CompleteTransition"))
            {
                _pedestalHintsPatchedCount++;
            }

            // 升级台悬停预览双保险 1：兼容复合 SelectionCriteria (如 Partner Portal - Forja)
            var cardExtType = GameInspector.FindTypeSafe("TheBazaar.CardExtensions");
            var canPedUpg = cardExtType == null ? null : AccessTools.Method(cardExtType, "CanUseTPedestalBehaviorUpgrade");
            if (canPedUpg != null && patcher.TryPatchMethod(
                canPedUpg,
                postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(CanUsePedestalUpgradePostfix)),
                optional: true,
                label: "CardExtensions.CanUseTPedestalBehaviorUpgrade"))
            {
                _pedestalHintsPatchedCount++;
            }

            // 升级台悬停预览双保险 2：CurrentEncounterController 未及时就绪或复合条件时兜底
            var tipParentType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.TooltipParentComponent");
            var isUpgPedState = tipParentType == null ? null : AccessTools.Method(tipParentType, "IsUpgradePedestalState");
            if (isUpgPedState != null && patcher.TryPatchMethod(
                isUpgPedState,
                postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(IsUpgradePedestalStatePostfix)),
                optional: true,
                label: "TooltipParentComponent.IsUpgradePedestalState"))
            {
                _pedestalHintsPatchedCount++;
            }

            // 离开 PedestalState 时恢复所有升级预览标记，并按原生 CleanUp + 1.8s 看门狗安全回收祭坛呈现
            var bspType = GameInspector.FindTypeSafe("TheBazaar.BoardStatePresentation", "BoardStatePresentation");
            if (bspType != null)
            {
                _bspType = bspType;
                _bspCleanUpCompleteMethod = AccessTools.Method(bspType, "CleanUpComplete", Type.EmptyTypes);
                _bspCleanupCompleteProp = bspType.GetProperty("CleanupComplete", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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
                if (bspInit != null && patcher.TryPatchMethod(
                    bspInit,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardStatePresentationInitializePostfix)),
                    optional: true,
                    label: "BoardStatePresentation.Initialize"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var bspCleanUp = AccessTools.Method(bspType, "CleanUp", Type.EmptyTypes);
                if (bspCleanUp != null && patcher.TryPatchMethod(
                    bspCleanUp,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardStatePresentationCleanUpPrefix)),
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardStatePresentationCleanUpPostfix)),
                    optional: true,
                    label: "BoardStatePresentation.CleanUp"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var bspOnDestroy = AccessTools.Method(bspType, "OnDestroy", Type.EmptyTypes);
                if (bspOnDestroy != null && patcher.TryPatchMethod(
                    bspOnDestroy,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardStatePresentationOnDestroyPostfix)),
                    optional: true,
                    label: "BoardStatePresentation.OnDestroy"))
                {
                    _pedestalHintsPatchedCount++;
                }

                // P0-17：捕获拖入祭坛的 ItemController，供服务端拒单或异常中断时恢复拖拽与归位
                var bspItemDroppedOnPedestal = bspType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ItemDroppedOnPedestalHandler" && m.GetParameters().Length == 1);
                if (bspItemDroppedOnPedestal != null && patcher.TryPatchMethod(
                    bspItemDroppedOnPedestal,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ItemDroppedOnPedestalHandlerPostfix)),
                    optional: true,
                    label: "BoardStatePresentation.ItemDroppedOnPedestalHandler"))
                {
                    _pedestalHintsPatchedCount++;
                }
            }

            // P1-42：缓存 PopupManager / Services.Get 句柄，供看门狗复位 DialogOpen 前避让原生弹窗
            _popupManagerType = GameInspector.FindTypeSafe("TheBazaar.PopupManager", "PopupManager");
            if (_popupManagerType != null)
            {
                _popupMgrIsAnyPopupOpenProp = _popupManagerType.GetProperty("IsAnyPopupOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _popupMgrIsSettingsOpenProp = _popupManagerType.GetProperty("IsSettingsOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            var servicesType = GameInspector.FindTypeSafe("TheBazaar.AppFramework.Services", "TheBazaar.Services", "Services");
            if (servicesType != null && _popupManagerType != null)
            {
                var getOpen = servicesType
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getOpen != null)
                {
                    try { _servicesGetMethod = getOpen.MakeGenericMethod(_popupManagerType); } catch { }
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
                _bmEncounterAssetSoField ??= bmType.GetField("_encounterAssetDataSO", BindingFlags.NonPublic | BindingFlags.Instance);
                _bmOpponentBoardAnchorField = bmType.GetField("opponentBoardAnchor", BindingFlags.NonPublic | BindingFlags.Instance);
                _bmBoardTransitionField = bmType.GetField("_boardTransition", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _bmClearUpgradeOrFuseOrEnchantMethod = AccessTools.Method(bmType, "ClearUpgradeOrFuseOrEnchantProcessing", Type.EmptyTypes);
                _bmIsProcessingUpgradeProp = bmType.GetProperty("IsProcessingUpgradeOrFuseOrEnchant", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _bmIsProcessingUpgradeSetter = _bmIsProcessingUpgradeProp?.GetSetMethod(nonPublic: true);
                _bmIsPlayingUpgradeOrFuseAnimProp = bmType.GetProperty("IsPlayingUpgradeOrFuseAnimation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _bmEnchantManagerField = bmType.GetField("_enchantManager", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _bmIsObservedBoardSyncActiveMethod = bmType.GetMethod("IsObservedBoardSyncActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                _bmDialogOpenProp = bmType.GetProperty("DialogOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _bmRequestDialogCloseMethod = AccessTools.Method(bmType, "RequestDialogClose", Type.EmptyTypes);
                _cardSocketCtrlType = GameInspector.FindTypeSafe("CardSocketController", "TheBazaar.CardSocketController");
                _cardCtrlBaseType = GameInspector.FindTypeSafe("CardController", "TheBazaar.CardController");
                _socketEffectCtrlType = GameInspector.FindTypeSafe("SocketEffectController", "TheBazaar.SocketEffectController");
                if (_cardCtrlBaseType != null)
                {
                    _cardCtrlAnimatorField ??= FindFieldInHierarchy(_cardCtrlBaseType, "Animator");
                    _cardCtrlAnimatorProp ??= _cardCtrlBaseType.GetProperty("Animator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                }

                // 锚定 Unity.VisualScripting.CustomEvent.Trigger(GameObject, string, object[]) 供看门狗兜底销毁前触发原生 "Clean Up"
                var vsCustomEventType = GameInspector.FindTypeSafe("Unity.VisualScripting.CustomEvent");
                if (vsCustomEventType != null)
                {
                    _vsCustomEventTriggerMethod = vsCustomEventType
                        .GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "Trigger") return false;
                            var ps = m.GetParameters();
                            return ps.Length == 3
                                && ps[0].ParameterType == typeof(GameObject)
                                && ps[1].ParameterType == typeof(string)
                                && ps[2].ParameterType == typeof(object[]);
                        });
                }

                var onStateChanged = AccessTools.Method(bmType, "OnStateChanged");
                if (onStateChanged != null && patcher.TryPatchMethod(
                    onStateChanged,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardManagerOnStateChangedPostfix)),
                    optional: true,
                    label: "BoardManager.OnStateChanged"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var determinePres = AccessTools.Method(bmType, "DetermineBoardStatePresentation", Type.EmptyTypes);
                if (determinePres != null && patcher.TryPatchMethod(
                    determinePres,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(DetermineBoardStatePresentationPostfix)),
                    optional: true,
                    label: "BoardManager.DetermineBoardStatePresentation"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var loadPres = AccessTools.Method(bmType, "LoadBoardStatePresentation", Type.EmptyTypes);
                if (loadPres != null && patcher.TryPatchMethod(
                    loadPres,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(LoadBoardStatePresentationPrefix)),
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(LoadBoardStatePresentationPostfix)),
                    optional: true,
                    label: "BoardManager.LoadBoardStatePresentation"))
                {
                    _pedestalHintsPatchedCount++;
                }

                if (_bmRemovePresMethod != null && patcher.TryPatchMethod(
                    _bmRemovePresMethod,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(RemoveBoardStatePresentationPrefix)),
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(RemoveBoardStatePresentationPostfix)),
                    optional: true,
                    label: "BoardManager.RemoveBoardStatePresentation"))
                {
                    _pedestalHintsPatchedCount++;
                }

                // P1-13：当 PedestalState 中 BoardStatePresentation == null 时，BoardManager.OnCardUpgradedHandler 会跳过 UpdateCard()，此处补兜底刷新
                var onCardUpgraded = AccessTools.Method(bmType, "OnCardUpgradedHandler", new[] { typeof(GameSimEventCardUpgraded) });
                if (onCardUpgraded != null && patcher.TryPatchMethod(
                    onCardUpgraded,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardManagerOnCardUpgradedPostfix)),
                    optional: true,
                    label: "BoardManager.OnCardUpgradedHandler"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var reconcilePres = AccessTools.Method(bmType, "ReconcilePresentationAfterStateSyncAsync");
                if (reconcilePres != null && patcher.TryPatchMethod(
                    reconcilePres,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ReconcilePresentationAfterStateSyncPostfix)),
                    optional: true,
                    label: "BoardManager.ReconcilePresentationAfterStateSyncAsync"))
                {
                    _pedestalHintsPatchedCount++;
                }
            }

            // P2-32：缓存 Data.CurrentEncounterId（属性）并补丁 AppState.SetState(ERunState)，在 PedestalState.cs:66 清空后写回链式下一跳 ID
            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
            _dataCurrentEncounterIdProp ??= dataType?.GetProperty("CurrentEncounterId", BindingFlags.Public | BindingFlags.Static);
            var chainedAppStateType = GameInspector.FindTypeSafe("TheBazaar.AppState");
            var setStateByRunState = chainedAppStateType == null ? null : AccessTools.Method(chainedAppStateType, "SetState", new[] { typeof(ERunState) });
            if (setStateByRunState != null && patcher.TryPatchMethod(
                setStateByRunState,
                prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(AppStateSetStateChainedPedestalPrefix)),
                optional: true,
                label: "AppState.SetState.ChainedPedestal"))
            {
                _pedestalHintsPatchedCount++;
            }

            // P2-16：补丁 TransformManager.OnCombatCardTransformed / OnCardTransformReverted，在局外变形完成时兜底清零 IsProcessingUpgradeOrFuseOrEnchant
            var transformMgrType = GameInspector.FindTypeSafe("TheBazaar.Managers.TransformManager", "TransformManager");
            if (transformMgrType != null)
            {
                var onTransformed = transformMgrType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "OnCombatCardTransformed" && m.GetParameters().Length == 1);
                if (onTransformed != null && patcher.TryPatchMethod(
                    onTransformed,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(TransformManagerOnCardTransformedPostfix)),
                    optional: true,
                    label: "TransformManager.OnCombatCardTransformed"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var onReverted = transformMgrType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "OnCardTransformReverted" && m.GetParameters().Length == 1);
                if (onReverted != null && patcher.TryPatchMethod(
                    onReverted,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(TransformManagerOnCardTransformedPostfix)),
                    optional: true,
                    label: "TransformManager.OnCardTransformReverted"))
                {
                    _pedestalHintsPatchedCount++;
                }
            }

            PatchPedestalRejectionRecovery(patcher);
            PatchBoardTransitionController(patcher);
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

    private static void PedestalOnEnterPostfix(object __instance, Task __result)
    {
        _pedestalCommitRequested = false;
        _pendingPedestalExitAbort = false;
        _wasInAnyPedestal = true;
        ResetPendingPedestalCommitTracking();

        ApplyPedestalOnEnterRefresh(__instance, checkStillCurrent: false);

        // PedestalState.OnEnter 为 async Task，原版在 await base.OnEnter() 之后才赋值 _pedestalTemplate，
        // 因此需挂载 __result 完成回调，回到主线程后再补刷一次模板与悬停升级预览。
        if (__result != null && !__result.IsCompleted && __instance != null)
        {
            var captured = __instance;
            __result.ContinueWith(_ =>
            {
                BazaarLauncher.Core.MainThreadDispatcher.Enqueue(() => ApplyPedestalOnEnterRefresh(captured, checkStillCurrent: true));
            }, TaskScheduler.Default);
        }
    }

    private static void ApplyPedestalOnEnterRefresh(object pedestalInstance, bool checkStillCurrent)
    {
        try
        {
            if (pedestalInstance == null) return;
            if (checkStillCurrent)
            {
                if (!_wasInAnyPedestal) return;
                var curState = GetStaticDataMember("CurrentState");
                if (curState != null && !ReferenceEquals(curState, pedestalInstance)) return;
            }

            // 若原生 OnEnter 时 _pedestalTemplate 为空，尝试按 Data.CurrentEncounterId 补全
            if (_pedestalTemplateField != null && _pedestalTemplateField.GetValue(pedestalInstance) == null)
            {
                var fallbackTpl = ResolveActiveUpgradePedestalTemplate(null, pedestalInstance);
                if (fallbackTpl != null)
                {
                    _pedestalTemplateField.SetValue(pedestalInstance, fallbackTpl);
                }
            }

            var pedTpl = _pedestalTemplateField?.GetValue(pedestalInstance) as TCardEncounterPedestal
                         ?? ResolveActiveUpgradePedestalTemplate(null, pedestalInstance);
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
                // P1-13：切离祭坛时核验目标及玩家物品 3D 视觉品阶（uiTier）与 CardData.Tier 是否一致
                ReconcilePedestalUpgradedItemVisuals(_lastPedestalUpgradedCardId, "BoardManager.OnStateChanged:LeavePedestal", scanAllPlayerItems: true);
            }

            if ((wasPedestal || _wasInAnyPedestal || _pedestalVerifyNonPedestalExit) && !nowPedestalContext)
            {
                _wasInAnyPedestal = false;
                if (_hasPendingPedestalCommit || IsPedestalProcessingFlagStuck(__instance))
                {
                    RecoverPendingPedestalCommit("BoardManager.OnStateChanged:LeavePedestal");
                }
                // V13.2 修复问题 #4（根因 2）：PedestalState.OnGameSimMessage 移除了 GameSimEventStateTransitioned，
                // 导致 GameSimHandler 不会调用 TryLoadCurrentEncounterSO()，BoardManager._encounterAssetDataSO 会残留指向旧基座资产！
                // 离开基座或无当前遭遇 ID 时立即置空 _encounterAssetDataSO，杜绝旧附魔/升级台被后续事件二次加载！
                if (!HasActiveCurrentEncounterId())
                {
                    ClearStaleBoardManagerEncounterAssetSo(__instance);
                }
                // P0-02：离开祭坛切入其他态时兜底清扫孤儿 3D 祭坛对象并确保退出升级预览高光
                ExitAllUpgradePreviewsSafely();
                CleanupStalePedestalBoardPresentation(__instance, "OnStateChanged", forceImmediate: false, allowWhileInPedestalState: false);
                var keepOnLeave = GetBoardStatePresentationObj(__instance);
                SweepOrphanPedestalPresentationsInScene(
                    keepOnLeave != null && !IsTrackedOrPedestalPresentation(keepOnLeave) ? keepOnLeave : null,
                    forceBypassThrottle: true);
                SweepOrphanPedestalChildrenUnderOpponentAnchor(__instance);
            }
            else if (curStateName is "EncounterState" or "ChoiceState" or "CombatState" or "PVPCombatState" or "LootState")
            {
                // P0-02：进入 Encounter/Choice/Combat/Loot 等非祭坛态时，统一清扫孤儿 3D 祭坛 GameObject 并执行 ExitUpgradePreview()
                if (!nowPedestalContext)
                {
                    ExitAllUpgradePreviewsSafely();
                    if (!HasActiveCurrentEncounterId())
                    {
                        ClearStaleBoardManagerEncounterAssetSo(__instance);
                    }
                    var curPres = GetBoardStatePresentationObj(__instance);
                    if (curPres != null && (IsTrackedOrPedestalPresentation(curPres) || curStateName is "ChoiceState" or "CombatState" or "PVPCombatState" or "LootState"))
                    {
                        CleanupStalePedestalBoardPresentation(__instance, "OnStateChanged:" + curStateName, forceImmediate: false, allowWhileInPedestalState: false);
                    }
                    var keepPres = GetBoardStatePresentationObj(__instance);
                    SweepOrphanPedestalPresentationsInScene(
                        keepPres != null && !IsTrackedOrPedestalPresentation(keepPres) ? keepPres : null,
                        forceBypassThrottle: true);
                    SweepOrphanPedestalChildrenUnderOpponentAnchor(__instance);
                }
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
            // P1-27：切板过场期间不将切板演出实例错误登记为祭坛实例
            if (!IsBoardTransitioning() && (IsInPedestalContext() || HasPedestalFeature(__instance)))
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
                    _cleaningUpPresentationIds.Remove(id);
                    if (_lastPedestalPresentationId == id)
                    {
                        _lastPedestalPresentationId = 0;
                    }
                }
            }
        }
        catch { }
    }

    private static bool IsPresentationCleanupComplete(object presObj)
    {
        if (presObj == null || (presObj is UnityEngine.Object uo && uo == null)) return true;
        try
        {
            return _bspCleanupCompleteProp?.GetValue(presObj) is bool done && done;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPresentationCleaningUp(int instanceId)
    {
        if (instanceId == 0) return false;
        lock (_pedestalPresLock)
        {
            return _cleaningUpPresentationIds.Contains(instanceId);
        }
    }

    private static void MarkPresentationCleaningUp(object presObj)
    {
        if (presObj is not Component comp || comp == null) return;
        int id = comp.GetInstanceID();
        lock (_pedestalPresLock)
        {
            if (_cleaningUpPresentationIds.Count > 128) _cleaningUpPresentationIds.Clear();
            _cleaningUpPresentationIds.Add(id);
        }
    }

    private static void UnmarkPresentationCleaningUp(object presObj)
    {
        if (presObj is not Component comp) return;
        int id = comp.GetInstanceID();
        lock (_pedestalPresLock)
        {
            _cleaningUpPresentationIds.Remove(id);
        }
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

            // P1-27：BoardTransitionController 切板期间放行非祭坛切板过场实例的原生 CleanUp，防止打断切板演出或提前销毁
            int instId = __instance is Component instComp ? instComp.GetInstanceID() : 0;
            if (IsBoardTransitioning() && !IsTrackedPedestalPresentationId(instId) && !IsForceCleanedPresentation(__instance))
            {
                return true;
            }

            // P1-42：仅在对象已失活（Visual Scripting 蓝图无法执行 "Clean Up"）、已被强制清理或已完成清理时短路；
            // 活跃祭坛/呈现对象一律放行原生 CleanUp()，并在 Postfix 中挂载 1.8s 看门狗兜底，避免同帧硬销毁导致 DialogOpen/EnableMovement 未成对归位！
            bool isInactive = __instance is Component c && c.gameObject != null && !c.gameObject.activeInHierarchy;
            if (isInactive || IsForceCleanedPresentation(__instance) || IsPresentationCleanupComplete(__instance))
            {
                bool newlyCleaned = ShortCircuitAndDestroyPresentation(__instance);
                if (newlyCleaned)
                {
                    Interlocked.Increment(ref _pedestalPresentationCleanedCount);
                }
                __result = Task.CompletedTask;
                return false;
            }

            MarkPresentationCleaningUp(__instance);
            return true;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.BoardStatePresentationCleanUpPrefix: " + ex.Message);
            return true;
        }
    }

    private static void BoardStatePresentationCleanUpPostfix(object __instance, ref Task __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null) || __result == null)
                return;

            int instId = __instance is Component instComp ? instComp.GetInstanceID() : 0;
            // P1-27：切板过场中的非祭坛实例不挂看门狗，保持原生切板完整生命周期
            if (IsBoardTransitioning() && !IsTrackedPedestalPresentationId(instId))
                return;

            if (__result.IsCompleted)
            {
                UnmarkPresentationCleaningUp(__instance);
                return;
            }

            __result = WrapBoardStatePresentationCleanUpWithWatchdog(__result, __instance);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.BoardStatePresentationCleanUpPostfix: " + ex.Message);
        }
    }

    private static async Task WrapBoardStatePresentationCleanUpWithWatchdog(Task inner, object presObj)
    {
        try
        {
            var delayTask = Task.Delay(PresentationCleanUpWatchdogMs);
            var finished = await Task.WhenAny(inner, delayTask);
            if (finished == inner)
            {
                await inner;
                return;
            }

            // 观察超时后原 Task 的潜在取消/异常，避免 UnobservedTaskException
            _ = inner.ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

            // 超过 1.8s 仍未完成 CleanUp：在主线程强制回收该呈现对象，并兜底复位 DialogOpen 与玩家卡牌移动态
            var tcs = new TaskCompletionSource<bool>();
            PostToMainThread(() =>
            {
                try
                {
                    bool stillAlive = presObj != null && !(presObj is UnityEngine.Object uo && uo == null);
                    if (stillAlive && !IsPresentationCleanupComplete(presObj))
                    {
                        bool newlyCleaned = ShortCircuitAndDestroyPresentation(presObj);
                        if (newlyCleaned)
                        {
                            Interlocked.Increment(ref _pedestalPresentationCleanedCount);
                        }
                        RestoreBoardDialogAndPlayerItemMovementAfterWatchdog("BoardStatePresentation.CleanUpWatchdog");
                        BazaarLauncherPlugin.Log?.LogWarning(
                            $"CompatAuditPatch[P1-42]: BoardStatePresentation.CleanUp 超过 {PresentationCleanUpWatchdogMs}ms 未完成，已触发看门狗回收并兜底恢复交互/拖拽态");
                    }
                }
                catch (Exception ex)
                {
                    BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CleanUpWatchdog: " + ex.Message);
                }
                finally
                {
                    tcs.TrySetResult(true);
                }
            });
            await tcs.Task;
        }
        finally
        {
            UnmarkPresentationCleaningUp(presObj);
        }
    }

    private static bool IsAnyNativePopupOpen()
    {
        try
        {
            if (_servicesGetMethod == null || (_popupMgrIsAnyPopupOpenProp == null && _popupMgrIsSettingsOpenProp == null))
                return false;
            var popupMgr = _servicesGetMethod.Invoke(null, null);
            if (popupMgr == null || (popupMgr is UnityEngine.Object puo && puo == null))
                return false;
            if (_popupMgrIsAnyPopupOpenProp?.GetValue(popupMgr) is bool anyOpen && anyOpen)
                return true;
            if (_popupMgrIsSettingsOpenProp?.GetValue(popupMgr) is bool settingsOpen && settingsOpen)
                return true;
        }
        catch { }
        return false;
    }

    private static void RestoreBoardDialogAndPlayerItemMovementAfterWatchdog(string reason)
    {
        try
        {
            var bm = GameInspector.GetBoardManagerCached();
            if (bm != null && !(bm is UnityEngine.Object bmUo && bmUo == null))
            {
                // 仅当 BoardManager.DialogOpen 为 true 且当前无原生 Popup/设置弹窗打开时，成对调用 RequestDialogClose()
                if (_bmDialogOpenProp?.GetValue(bm) is bool dialogOpen && dialogOpen && !IsAnyNativePopupOpen())
                {
                    _bmRequestDialogCloseMethod?.Invoke(bm, null);
                }

                // 若此时没有正在播放的升阶/附魔动画，兜底恢复被祭坛蓝图锁死的玩家手牌/储物箱物品 EnableMovement(true)
                if (!IsUpgradeOrEnchantAnimationActivelyPlaying(bm))
                {
                    RestoreDisabledPlayerItemControllersMovement(reason);
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RestoreBoardDialogAndPlayerItemMovementAfterWatchdog: " + ex.Message);
        }
    }

    private static void RestoreDisabledPlayerItemControllersMovement(string reason)
    {
        try
        {
            var run = GameInspector.GetDataRun();
            var player = run != null ? GameInspector.GetMember(run, "Player") : null;
            var lookup = GameInspector.GetDataLookup();
            if (player == null || lookup == null) return;
            if (GameInspector.GetMember(lookup, "CardControllerDictionary") is not IDictionary dict) return;

            int restored = 0;
            foreach (DictionaryEntry kv in dict)
            {
                var ctrl = kv.Value;
                if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) continue;
                var cardData = GameInspector.GetMember(ctrl, "CardData");
                if (cardData == null) continue;
                if (GameInspector.GetMember(cardData, "Type") is not ECardType cardType || cardType != ECardType.Item)
                    continue;
                if (!ReferenceEquals(GameInspector.GetMember(cardData, "Owner"), player))
                    continue;
                if (_itemGetIsDraggingDelegate != null && ctrl.GetType().Name == "ItemController" && _itemGetIsDraggingDelegate(ctrl))
                    continue;

                var ctrlType = ctrl.GetType();
                var movField = _cardCtrlMovementEnabledField != null && _cardCtrlMovementEnabledField.DeclaringType.IsInstanceOfType(ctrl)
                    ? _cardCtrlMovementEnabledField
                    : FindFieldInHierarchy(ctrlType, "movementEnabled");
                if (movField?.GetValue(ctrl) is bool enabled && !enabled)
                {
                    var enableMethod = _cardCtrlEnableMovementMethod != null && _cardCtrlEnableMovementMethod.DeclaringType.IsInstanceOfType(ctrl)
                        ? _cardCtrlEnableMovementMethod
                        : AccessTools.Method(ctrlType, "EnableMovement", new[] { typeof(bool) });
                    enableMethod?.Invoke(ctrl, new object[] { true });
                    restored++;
                }
            }

            if (restored > 0)
            {
                BazaarLauncherPlugin.Log?.LogWarning(
                    $"CompatAuditPatch[P1-42]: 看门狗恢复 {restored} 张被锁移动态的玩家物品 EnableMovement(true) (reason={reason})");
            }
        }
        catch { }
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

    private static bool LoadBoardStatePresentationPrefix(object __instance, ref Task __result, out object __state)
    {
        __state = null;
        if (__instance == null || (__instance is UnityEngine.Object bmUo && bmUo == null)) return true;
        try
        {
            // P0-02：捕获加载前的原 BoardStatePresentation 局部引用，防止 LoadBoardStatePresentation 内 await CleanUp() 并发覆写漏毁旧 3D 祭坛
            __state = GetBoardStatePresentationObj(__instance);

            // V13.2 修复问题 #4：无激活遭遇 ID（例如刚退出附魔/升级台回到 ChoiceState）时，立即清掉陈旧 _encounterAssetDataSO
            // 与残留 BoardStatePresentation，阻止把上一跳的附魔/升级台再次加载到中间 3 格！
            if (!HasActiveCurrentEncounterId())
            {
                ClearStaleBoardManagerEncounterAssetSo(__instance);
                var curPres = __state;
                bool isLoading = _bmIsBoardStatePresLoadingProp?.GetValue(__instance) is bool b && b;
                if (curPres != null || isLoading)
                {
                    CleanupStalePedestalBoardPresentation(__instance, "LoadBoardStatePresentationPrefix:NoEncounter", forceImmediate: false, allowWhileInPedestalState: false);
                }
                SweepOrphanPedestalPresentationsInScene(null, forceBypassThrottle: true);
                SweepOrphanPedestalChildrenUnderOpponentAnchor(__instance);
                __state = null;
                __result = Task.CompletedTask;
                return false;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.LoadBoardStatePresentationPrefix: " + ex.Message);
        }
        return true;
    }

    private static void LoadBoardStatePresentationPostfix(object __instance, object __state, ref Task __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null) || __result == null) return;
            __result = WrapBoardPresentationCheckTask(__result, __instance, "LoadBoardStatePresentation", __state);
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
            __result = WrapBoardPresentationCheckTask(__result, __instance, "ReconcilePresentationAfterStateSync", capturedOldPres: null);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ReconcilePresentationAfterStateSyncPostfix: " + ex.Message);
        }
    }

    private static async Task WrapBoardPresentationCheckTask(Task inner, object boardManager, string sourceTag, object capturedOldPres = null)
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

                    // P0-02：若加载前捕获的旧 3D 祭坛已被新实例覆写却仍存活在场景中，立即兜底销毁避免孤儿滞留
                    if (capturedOldPres != null
                        && IsUnityObjectAlive(capturedOldPres)
                        && !ReferenceEquals(capturedOldPres, curPres)
                        && (IsTrackedOrPedestalPresentation(capturedOldPres) || !IsInPedestalContext()))
                    {
                        if (ShortCircuitAndDestroyPresentation(capturedOldPres))
                        {
                            Interlocked.Increment(ref _pedestalPresentationCleanedCount);
                        }
                    }

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
                        // P0-02：同步恢复/切换至非祭坛态后，兜底清理一切残留孤儿 3D 祭坛并执行 ExitUpgradePreview()
                        CleanupStalePedestalBoardPresentation(boardManager, sourceTag, forceImmediate: false, allowWhileInPedestalState: false);
                        var keepPres = GetBoardStatePresentationObj(boardManager);
                        SweepOrphanPedestalPresentationsInScene(
                            keepPres != null && !IsTrackedOrPedestalPresentation(keepPres) ? keepPres : null,
                            forceBypassThrottle: true);
                        SweepOrphanPedestalChildrenUnderOpponentAnchor(boardManager);
                        ExitAllUpgradePreviewsSafely();
                    }
                }
                catch (Exception ex)
                {
                    BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapBoardPresentationCheckTask: " + ex.Message);
                }
            });
        }
    }

    private static bool RemoveBoardStatePresentationPrefix(object __instance, ref Task __result, out object __state)
    {
        __state = null;
        if (__instance == null || (__instance is UnityEngine.Object bmUo && bmUo == null)) return true;
        try
        {
            // V13.2：若当前无激活遭遇 ID，提前清掉陈旧 _encounterAssetDataSO，避免后续重入加载旧基座资产
            if (!HasActiveCurrentEncounterId())
            {
                ClearStaleBoardManagerEncounterAssetSo(__instance);
            }

            var curPres = GetBoardStatePresentationObj(__instance);
            bool inPedestalCtx = IsInPedestalContext();

            // P0-02：在 await CleanUp() 前捕获原 BoardStatePresentation 局部引用，彻底避免并发覆写/置空导致官方第 927 行 Destroy 漏毁旧 3D 祭坛！
            __state = curPres;

            if (curPres == null)
            {
                _pendingPedestalExitAbort = false;
                if (!inPedestalCtx)
                {
                    SweepOrphanPedestalPresentationsInScene(null, forceBypassThrottle: true);
                    SweepOrphanPedestalChildrenUnderOpponentAnchor(__instance);
                    ExitAllUpgradePreviewsSafely();
                }
            }
            else if (curPres is Component curComp && curComp.gameObject != null && !curComp.gameObject.activeInHierarchy)
            {
                CleanupStalePedestalBoardPresentation(__instance, "RemoveBoardStatePresentationPrefix:Inactive", forceImmediate: true, allowWhileInPedestalState: true);
                SweepOrphanPedestalPresentationsInScene(null, forceBypassThrottle: true);
                SweepOrphanPedestalChildrenUnderOpponentAnchor(__instance);
                if (!inPedestalCtx) ExitAllUpgradePreviewsSafely();
                __state = null;
                __result = Task.CompletedTask;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RemoveBoardStatePresentationPrefix: " + ex.Message);
            return true;
        }
    }

    private static void RemoveBoardStatePresentationPostfix(object __instance, object __state, ref Task __result)
    {
        try
        {
            if (__state == null) return;
            if (__result == null || __result.IsCompleted)
            {
                EnsureCapturedPresentationDestroyedOnMain(__instance, __state, "RemoveBoardStatePresentation:Sync");
                return;
            }
            __result = WrapRemoveBoardStatePresentationTask(__result, __instance, __state);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RemoveBoardStatePresentationPostfix: " + ex.Message);
        }
    }

    private static async Task WrapRemoveBoardStatePresentationTask(Task inner, object boardManager, object capturedPres)
    {
        try
        {
            await inner;
        }
        finally
        {
            PostToMainThread(() => EnsureCapturedPresentationDestroyedOnMain(boardManager, capturedPres, "RemoveBoardStatePresentation:Async"));
        }
    }

    /// <summary>
    /// P0-02 根治：RemoveBoardStatePresentation 的 await CleanUp() 结束后，无论属性 BoardStatePresentation 是否已被并发置空或覆写，
    /// 均依据事前捕获的局部引用 capturedPres 强制销毁旧 3D 祭坛 GameObject 并清理升级预览高光。
    /// </summary>
    private static void EnsureCapturedPresentationDestroyedOnMain(object boardManager, object capturedPres, string reason)
    {
        try
        {
            if (capturedPres != null && IsUnityObjectAlive(capturedPres))
            {
                bool bmAlive = IsUnityObjectAlive(boardManager);
                object curPres = bmAlive ? GetBoardStatePresentationObj(boardManager) : null;
                if (bmAlive && ReferenceEquals(curPres, capturedPres))
                {
                    _bmBoardStatePresProp?.SetValue(boardManager, null);
                    _bmIsBoardStatePresLoadingProp?.SetValue(boardManager, false);
                }

                if (ShortCircuitAndDestroyPresentation(capturedPres))
                {
                    int cnt = Interlocked.Increment(ref _pedestalPresentationCleanedCount);
                    BazaarLauncherPlugin.Log?.LogWarning(
                        $"CompatAuditPatch[P0-02]: {reason} 捕获旧 BoardStatePresentation 局部引用在 CleanUp 后仍存活，已强制销毁防孤儿滞留 (count={cnt})");
                }
            }

            if (!IsInPedestalContext())
            {
                object keepPres = IsUnityObjectAlive(boardManager) ? GetBoardStatePresentationObj(boardManager) : null;
                SweepOrphanPedestalPresentationsInScene(
                    keepPres != null && !IsTrackedOrPedestalPresentation(keepPres) ? keepPres : null,
                    forceBypassThrottle: true);
                if (IsUnityObjectAlive(boardManager))
                {
                    SweepOrphanPedestalChildrenUnderOpponentAnchor(boardManager);
                }
                ExitAllUpgradePreviewsSafely();
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.EnsureCapturedPresentationDestroyedOnMain: " + ex.Message);
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

            // 二层保险：切换到非基座状态后，若新遭遇模板无独立中央 Presentation（如葵仙子等商店），任何尚未销毁的旧 BoardStatePresentation 均按陈旧卷轴回收
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
                // P1-42：若当前呈现仍活跃且非强制同步路径，优先委托原生 RemoveBoardStatePresentation()（由 1.8s 看门狗护航）优雅退出，绝不同帧强拆蓝图
                int curPresId = curPres is Component presComp && presComp != null ? presComp.GetInstanceID() : 0;
                bool curPresActive = curPres is Component activeComp && activeComp != null && activeComp.gameObject != null && activeComp.gameObject.activeInHierarchy;
                if (!forceImmediate && curPres != null && curPresActive && _bmRemovePresMethod != null)
                {
                    if (!IsPresentationCleaningUp(curPresId))
                    {
                        MarkPresentationCleaningUp(curPres);
                        _ = _bmRemovePresMethod.Invoke(boardManager, null) as Task;
                        BazaarLauncherPlugin.Log?.LogInfo(
                            $"CompatAuditPatch[P1-42]: triggered graceful RemoveBoardStatePresentation via {reason} (presId={curPresId})");
                    }
                    SweepOrphanPedestalPresentationsInScene(curPres);
                    return;
                }

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
                        $"CompatAuditPatch: recycled stale pedestal presentation via {reason} (hasPres={curPres != null}, wasLoading={isLoading}, allowInPedestal={allowWhileInPedestalState}, count={cnt})");
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

    /// <summary>
    /// P1-43：跨局/切场景时统一清零全部祭坛静态字段（8 项 + Batch 1C 挂起提交态）与拖拽/高亮去重静态字段（4+ 项），
    /// 彻底杜绝跨局状态污染或悬挂弱引用。
    /// </summary>
    internal static void ResetPedestalAndDragRunScopedCaches()
    {
        // 8 个祭坛静态字段 + 清理追踪与挂起提交状态
        _pedestalCommitRequested = false;
        _wasInUpgradePedestal = false;
        _wasInAnyPedestal = false;
        _pendingPedestalExitAbort = false;
        _pedestalVerifyNonPedestalExit = false;
        _lastOrphanPedestalSweepTime = -10f;
        lock (_pedestalPresLock)
        {
            _lastPedestalPresentationId = 0;
            _pedestalPresentationIds.Clear();
            _forceCleanedPresentationIds.Clear();
            _cleaningUpPresentationIds.Clear();
        }
        ResetPendingPedestalCommitTracking();
        _hasLastRecoveredPedestalCardId = false;
        _lastRecoveredPedestalCardId = default;
        _lastRecoveredPedestalAt = -100f;
        _pendingChainedPedestalEncounterId = Guid.Empty;
        _pendingChainedPedestalUntil = 0f;
        _lastPedestalUpgradedCardId = null;
        Interlocked.Increment(ref _uiRefreshGeneration);

        // 4+ 个拖拽/售卖区悬停去重静态字段
        _activeDraggingItemRef = null;
        _lastPreviewItemId = 0;
        _lastPreviewWorldPos = Vector3.zero;
        _lastPreviewStorageOpen = false;
        _lastPreviewFrame = -100;
        _currentSellZoneHovered = false;
        ResetShowCardHintDedupeCache();
    }

    private static void SweepOrphanPedestalPresentationsInScene(object validKeepPres, bool forceBypassThrottle = false)
    {
        try
        {
            if (_bspType == null) return;

            bool isBoardTransitioning = IsBoardTransitioning();
            // P1-27：切板过场期间非强制扫描直接避让；强制扫描模式下仍仅清理明确具备祭坛特征或已登记祭坛的孤儿实例，绝不触碰切板演出实例
            if (isBoardTransitioning && !forceBypassThrottle)
                return;

            // P0-02：常规热路径无已追踪祭坛时 O(1) 短路；在进入 Encounter/Choice/Combat 或 Reconcile 后强制扫描兜底未登记孤儿 3D 祭坛
            bool hasTrackedPedestals;
            lock (_pedestalPresLock)
            {
                hasTrackedPedestals = _pedestalPresentationIds.Count > 0;
            }
            if (!hasTrackedPedestals && !forceBypassThrottle)
                return;

            float now = Time.unscaledTime;
            if (!forceBypassThrottle && validKeepPres == null && now - _lastOrphanPedestalSweepTime < 0.35f)
            {
                return;
            }
            _lastOrphanPedestalSweepTime = now;

            var found = UnityEngine.Object.FindObjectsByType(_bspType, FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (found == null || found.Length == 0) return;

            int keepId = validKeepPres is Component keepComp && keepComp != null ? keepComp.GetInstanceID() : 0;
            bool inPedestalCtx = IsInPedestalContext();
            foreach (var obj in found)
            {
                if (obj == null || (obj is UnityEngine.Object uo && uo == null)) continue;
                int id = obj is Component comp ? comp.GetInstanceID() : 0;
                if (id == 0) continue;
                if (keepId != 0 && id == keepId) continue;

                // P1-42：正在走原生 CleanUp() + 1.8s 看门狗优雅退出的实例不提前强拆
                if (IsPresentationCleaningUp(id)) continue;

                bool isTracked = IsTrackedPedestalPresentationId(id);
                bool hasPedestalFeat = !inPedestalCtx && HasPedestalFeature(obj);
                if (isTracked || (forceBypassThrottle && hasPedestalFeat))
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

    private static bool IsTrackedPedestalPresentationId(int instanceId)
    {
        if (instanceId == 0) return false;
        lock (_pedestalPresLock)
        {
            return _pedestalPresentationIds.Contains(instanceId);
        }
    }

    private static bool IsInPedestalContext()
    {
        if (IsInPedestalRunState()) return true;
        var appState = GameInspector.GetCurrentAppState();
        if (appState != null && string.Equals(appState.GetType().Name, "PedestalState", StringComparison.Ordinal))
            return true;
        return IsCurrentEncounterPedestal();
    }

    private static bool HasActiveCurrentEncounterId()
    {
        try
        {
            return GetStaticDataMember("CurrentEncounterId") is Guid encGuid && encGuid != Guid.Empty;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearStaleBoardManagerEncounterAssetSo(object boardManager)
    {
        if (boardManager == null || _bmEncounterAssetSoField == null) return;
        try
        {
            if (_bmEncounterAssetSoField.GetValue(boardManager) != null)
            {
                _bmEncounterAssetSoField.SetValue(boardManager, null);
            }
        }
        catch { }
    }

    private static bool IsCurrentEncounterPedestal()
    {
        try
        {
            // V13.2 修复问题 #4（根因 1）：PedestalState 退出调用 AppState.SetState(ChoiceState) 瞬间，
            // Data.CurrentEncounterId 已被置为 null，但 Data.CurrentEncounterController 尚未被 ClearPortrait() 清除！
            // 若继续回退读取旧 CurrentEncounterController，会误判当前仍在基座态，导致 OnStateChanged 跳过清理！
            if (GetStaticDataMember("CurrentEncounterId") is not Guid encGuid || encGuid == Guid.Empty)
                return false;

            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
            var getStaticM = dataType?.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var staticMgr = getStaticM?.Invoke(null, null);
            var getCardM = staticMgr?.GetType().GetMethod("GetCardById", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Guid) }, null);
            if (getCardM?.Invoke(staticMgr, new object[] { encGuid }) is TCardEncounterPedestal)
                return true;

            var curEncCtrl = GetStaticDataMember("CurrentEncounterController");
            if (curEncCtrl != null && !(curEncCtrl is UnityEngine.Object uo && uo == null))
            {
                var encCard = GameInspector.GetMember(curEncCtrl, "CardData");
                if (GameInspector.GetMember(encCard, "Template") is TCardEncounterPedestal)
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

        // 0. V13.2 修复问题 #4（根因 3）：在销毁 BoardStatePresentation.gameObject 前，先同步触发原生 Visual Scripting "Clean Up" 事件，
        //    让基座蓝图自行清理它在 OnInitialize 中挂载到 BoardOpponentAnchor 或场景外部的附魔/升级台实体与粒子，防止台座遗留挡住中间 3 格！
        if (presObj is Component vsComp && vsComp.gameObject != null && vsComp.gameObject.activeInHierarchy)
        {
            InvokeVisualScriptingCleanUpEvent(vsComp.gameObject);
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

    private static void InvokeVisualScriptingCleanUpEvent(GameObject targetGo)
    {
        if (targetGo == null || _vsCustomEventTriggerMethod == null) return;
        try
        {
            _vsCustomEventTriggerMethod.Invoke(null, new object[] { targetGo, "Clean Up", Array.Empty<object>() });
        }
        catch
        {
            // 忽略已失活或不含 Visual Scripting ScriptMachine 的对象异常
        }
    }

    private static void SweepOrphanPedestalChildrenUnderOpponentAnchor(object boardManager)
    {
        if (boardManager == null || _bmOpponentBoardAnchorField == null) return;
        try
        {
            if (IsBoardTransitioning(boardManager)) return;
            if (IsInPedestalContext()) return;
            var curPres = GetBoardStatePresentationObj(boardManager);
            if (curPres != null) return;
            if (_bmOpponentBoardAnchorField.GetValue(boardManager) is not Transform oppAnchor || oppAnchor == null)
                return;

            int childCount = oppAnchor.childCount;
            for (int i = childCount - 1; i >= 0; i--)
            {
                var child = oppAnchor.GetChild(i);
                if (child == null || child.gameObject == null) continue;
                var go = child.gameObject;

                // 严格保护合法卡槽、卡牌与插槽特效子物体
                if (_cardSocketCtrlType != null && go.GetComponent(_cardSocketCtrlType) != null) continue;
                if (_cardCtrlBaseType != null && go.GetComponent(_cardCtrlBaseType) != null) continue;
                if (_socketEffectCtrlType != null && go.GetComponent(_socketEffectCtrlType) != null) continue;

                bool hasBsp = _bspType != null && go.GetComponentInChildren(_bspType, includeInactive: true) != null;
                string nm = go.name ?? "";
                bool looksLikePedestalVisual = hasBsp
                    || nm.IndexOf("Pedestal", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("Enchant", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("Artisan", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("Artist", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("Forge", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("Anvil", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("Altar", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("Presentation", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("Scroll", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!looksLikePedestalVisual) continue;

                InvokeVisualScriptingCleanUpEvent(go);
                if (go.activeSelf) go.SetActive(false);
                UnityEngine.Object.Destroy(go);
                Interlocked.Increment(ref _pedestalPresentationCleanedCount);
            }
        }
        catch { }
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

        // 服务端已确认升阶/附魔成功，清除挂起提交追踪，避免后续无关事件误触发回退归位
        ClearPendingPedestalCommitTracking();

        try
        {
            if (!string.IsNullOrEmpty(__state))
            {
                _lastPedestalUpgradedCardId = __state;
                ForceRefreshItemControllerUi(__state);
                // P1-13：祭坛升级动画正常结束窗口（600ms）与 6s 超时收尾窗口（6200ms）各做一次视觉阶一致性核验，
                // 防止祭坛蓝图延迟置位 blockVisualUpdates 或未触发 UpdateCard 导致 3D 边框/伤害/卖价冻结在旧阶
                SchedulePedestalVisualTierReconcile(__state, 600, 6200);
            }

            // P2-15 (O-1)：将基座演出完成与战斗倍速 CombatPatch.SpeedOverride 解耦，仅在显式请求快进提交时才跳过祭坛演出，
            // 避免玩家开启 >=2x 战斗倍速时连带跳过附魔/升级祭坛动画
            if (_pedestalCommitRequested)
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

    private static void PedestalCompleteTransitionPostfix()
    {
        try
        {
            string targetId = _lastPedestalUpgradedCardId;
            PostToMainThread(() => ReconcilePedestalUpgradedItemVisuals(targetId, "PedestalState.CompleteTransition", scanAllPlayerItems: true));
            SchedulePedestalVisualTierReconcile(targetId, 200);
        }
        catch { }
    }

    private static void BoardManagerOnCardUpgradedPostfix(object __instance, GameSimEventCardUpgraded data)
    {
        try
        {
            if (data == null || string.IsNullOrEmpty(data.InstanceId)) return;
            _lastPedestalUpgradedCardId = data.InstanceId;

            // P1-13：若处于 PedestalState 且 BoardStatePresentation 缺失（BoardManager.cs:3770 分支全跳过），立即兜底刷新目标 ItemController
            bool inPedestal = IsInPedestalContext();
            if (inPedestal && GetBoardStatePresentationObj(__instance) == null)
            {
                string cardId = data.InstanceId;
                PostToMainThread(() => ForceRefreshItemControllerUi(cardId));
                SchedulePedestalVisualTierReconcile(cardId, 250, 1200);
            }
            else if (inPedestal)
            {
                SchedulePedestalVisualTierReconcile(data.InstanceId, 800, 6200);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.BoardManagerOnCardUpgradedPostfix: " + ex.Message);
        }
    }

    private static void SchedulePedestalVisualTierReconcile(string targetCardId, params int[] delaysMs)
    {
        if (delaysMs == null || delaysMs.Length == 0) return;
        try
        {
            int gen = Volatile.Read(ref _uiRefreshGeneration);
            for (int i = 0; i < delaysMs.Length; i++)
            {
                int delay = delaysMs[i];
                _ = Task.Delay(delay).ContinueWith(_ =>
                {
                    if (gen != Volatile.Read(ref _uiRefreshGeneration)) return;
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        if (gen != Volatile.Read(ref _uiRefreshGeneration)) return;
                        ReconcilePedestalUpgradedItemVisuals(targetCardId, $"PedestalTimeoutReconcile({delay}ms)", scanAllPlayerItems: true);
                    });
                }, TaskScheduler.Default);
            }
        }
        catch { }
    }

    private static bool TryGetItemControllerUiTier(object itemCtrl, out ETier? uiTier)
    {
        uiTier = null;
        if (itemCtrl == null || (itemCtrl is UnityEngine.Object uo && uo == null)) return false;
        try
        {
            var t = itemCtrl.GetType();
            if (_itemCtrlVisualsProp == null || !_itemCtrlVisualsProp.DeclaringType.IsInstanceOfType(itemCtrl))
            {
                _itemCtrlVisualsProp = t.GetProperty("VisualsController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            object visuals = _itemCtrlVisualsProp?.GetValue(itemCtrl);
            if (visuals == null)
            {
                if (_itemCtrlVisualsField == null || !_itemCtrlVisualsField.DeclaringType.IsInstanceOfType(itemCtrl))
                {
                    _itemCtrlVisualsField = FindFieldInHierarchy(t, "visualsController");
                }
                visuals = _itemCtrlVisualsField?.GetValue(itemCtrl);
            }
            if (visuals == null || (visuals is UnityEngine.Object vuo && vuo == null)) return false;

            if (_ivcCurrentTierField == null || !_ivcCurrentTierField.DeclaringType.IsInstanceOfType(visuals))
            {
                _ivcCurrentTierField = visuals.GetType().GetField("currentTier", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            }
            if (_ivcCurrentTierField == null) return false;

            object rawTier = _ivcCurrentTierField.GetValue(visuals);
            if (rawTier is ETier et)
            {
                uiTier = et;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// P1-13（RC-PEDESTAL-VISUAL-UPDATE-DROPPED）：检测玩家 ItemController 的视觉阶（ItemVisualsController.currentTier）
    /// 与逻辑阶（CardData.Tier）是否一致；若不一致则解除视觉锁定并强制调用 UpdateCard() 刷新 3D 边框、伤害与卖价。
    /// </summary>
    private static void ReconcilePedestalUpgradedItemVisuals(string preferredInstanceId, string reason, bool scanAllPlayerItems = true)
    {
        try
        {
            var run = GameInspector.GetDataRun();
            var player = run != null ? GameInspector.GetMember(run, "Player") : null;
            var lookup = GameInspector.GetDataLookup();
            if (player == null || lookup == null) return;

            int refreshed = 0;

            void CheckAndRefreshController(object ctrl, object cardData)
            {
                if (ctrl == null || (ctrl is UnityEngine.Object cuo && cuo == null)) return;
                if (!string.Equals(ctrl.GetType().Name, "ItemController", StringComparison.Ordinal)) return;
                if (cardData == null) cardData = GameInspector.GetMember(ctrl, "CardData");
                if (cardData == null) return;
                if (GameInspector.GetMember(cardData, "Tier") is not ETier logicalTier) return;

                if (!TryGetItemControllerUiTier(ctrl, out ETier? uiTier)) return;
                if (uiTier.HasValue && uiTier.Value == logicalTier) return;

                string iid = GameInspector.GetMember(cardData, "InstanceId")?.ToString() ?? "";
                if (IsItemAnimatorBusyWithPlayableGraph(ctrl))
                {
                    if (!string.IsNullOrEmpty(iid))
                    {
                        int gen = Volatile.Read(ref _uiRefreshGeneration);
                        _ = DeferForceRefreshItemControllerUiAsync(iid, gen);
                    }
                    return;
                }

                ApplyItemControllerUiRefreshNow(ctrl);
                refreshed++;
                Interlocked.Increment(ref _pedestalVisualTierFixedCount);
                BazaarLauncherPlugin.Log?.LogInfo(
                    $"CompatAuditPatch[P1-13]: 检测到 ItemController 视觉阶滞后 (card={iid}, uiTier={uiTier?.ToString() ?? "null"}, logicalTier={logicalTier}, reason={reason})，已强制调用 UpdateCard()");
            }

            if (!string.IsNullOrEmpty(preferredInstanceId))
            {
                var targetCtrl = ResolveLiveCardControllerForUiRefresh(preferredInstanceId);
                if (targetCtrl != null)
                {
                    CheckAndRefreshController(targetCtrl, null);
                }
            }

            if (scanAllPlayerItems && GameInspector.GetMember(lookup, "CardControllerDictionary") is IDictionary dict)
            {
                foreach (DictionaryEntry kv in dict)
                {
                    var cardObj = kv.Key;
                    var ctrl = kv.Value;
                    if (cardObj == null || ctrl == null) continue;
                    if (!ReferenceEquals(GameInspector.GetMember(cardObj, "Owner"), player)) continue;
                    if (GameInspector.GetMember(cardObj, "Type") is not ECardType ct || ct != ECardType.Item) continue;
                    CheckAndRefreshController(ctrl, cardObj);
                }
            }

            if (refreshed > 0 && !IsInPedestalContext())
            {
                _lastPedestalUpgradedCardId = null;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ReconcilePedestalUpgradedItemVisuals: " + ex.Message);
        }
    }

    // ── P0-17：祭坛提交拒单/命令失败/状态同步回弹/异常离场解砖 ─────────────────

    private static void PatchPedestalRejectionRecovery(SafeHarmonyPatcher patcher)
    {
        try
        {
            var cardCtrlType = _cardCtrlBaseType ?? GameInspector.FindTypeSafe("CardController", "TheBazaar.CardController");
            if (cardCtrlType != null)
            {
                _cardCtrlEnableMovementMethod ??= AccessTools.Method(cardCtrlType, "EnableMovement", new[] { typeof(bool) });
                _cardCtrlMovementEnabledField ??= FindFieldInHierarchy(cardCtrlType, "movementEnabled");
                _cardCtrlAnimatorField ??= FindFieldInHierarchy(cardCtrlType, "Animator");
                _cardCtrlAnimatorProp ??= cardCtrlType.GetProperty("Animator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            }

            var itemCtrlType = GameInspector.FindTypeSafe("ItemController", "TheBazaar.ItemController");
            if (itemCtrlType != null)
            {
                _itemCtrlMoveBackMethod ??= AccessTools.Method(itemCtrlType, "MoveBack", Type.EmptyTypes);
            }

            var cmdType = GameInspector.FindTypeSafe("TheBazaar.Cmd");
            if (cmdType != null)
            {
                _cmdIsProcessingField = cmdType.GetField("_isProcessing", BindingFlags.NonPublic | BindingFlags.Instance);

                var sendCommit = AccessTools.Method(cmdType, "SendCommitToPedestal", new[] { typeof(InstanceId) })
                    ?? AccessTools.Method(cmdType, "SendCommitToPedestal");
                if (sendCommit != null && patcher.TryPatchMethod(
                    sendCommit,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(SendCommitToPedestalPrefix)),
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(SendCommitToPedestalFinalizer)),
                    optional: true,
                    label: "Cmd.SendCommitToPedestal"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var execAndProcess = cmdType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ExecuteAndProcess" && typeof(Task).IsAssignableFrom(m.ReturnType));
                if (execAndProcess != null && patcher.TryPatchMethod(
                    execAndProcess,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(CmdExecuteAndProcessPostfix)),
                    optional: true,
                    label: "Cmd.ExecuteAndProcess"))
                {
                    _pedestalHintsPatchedCount++;
                }
            }

            var nmType = GameInspector.FindTypeSafe("Networking.NetworkManager");
            if (nmType != null)
            {
                var handleCmdErr = AccessTools.Method(nmType, "HandleCommandErrorMessage", new[] { typeof(NetMessageError) });
                if (handleCmdErr != null && patcher.TryPatchMethod(
                    handleCmdErr,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(NetworkManagerHandleCommandErrorPostfix)),
                    optional: true,
                    label: "NetworkManager.HandleCommandErrorMessage"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var handleCmdFail = AccessTools.Method(nmType, "HandleCommandFailure", new[] { typeof(Exception) });
                if (handleCmdFail != null && patcher.TryPatchMethod(
                    handleCmdFail,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(NetworkManagerHandleCommandFailurePostfix)),
                    optional: true,
                    label: "NetworkManager.HandleCommandFailure"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var handleRecFail = AccessTools.Method(nmType, "HandleRecoveryFailure", new[] { typeof(string) });
                if (handleRecFail != null && patcher.TryPatchMethod(
                    handleRecFail,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(NetworkManagerHandleRecoveryFailurePostfix)),
                    optional: true,
                    label: "NetworkManager.HandleRecoveryFailure"))
                {
                    _pedestalHintsPatchedCount++;
                }
            }

            // 关键：当服务端返回 NetMessageAggregate([NetMessageError, NetMessageGameStateSync]) 时，
            // Cmd.ExecuteAndProcess 不会调用 NetworkManager.HandleCommandErrorMessage，而是经 NetMessageProcessor 分发至 AppState.OnErrorMessageReceived
            var appStateType = GameInspector.FindTypeSafe("TheBazaar.AppState");
            var onErrMsg = appStateType == null ? null : AccessTools.Method(appStateType, "OnErrorMessageReceived", new[] { typeof(NetMessageError) });
            if (onErrMsg != null && patcher.TryPatchMethod(
                onErrMsg,
                postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(AppStateOnErrorMessageReceivedPostfix)),
                optional: true,
                label: "AppState.OnErrorMessageReceived"))
            {
                _pedestalHintsPatchedCount++;
            }

            var gshType = GameInspector.FindTypeSafe("TheBazaar.GameStateHandler");
            var gshHandle = gshType == null ? null : AccessTools.Method(gshType, "HandleMessage", new[] { typeof(NetMessageGameStateSync) });
            if (gshHandle != null && typeof(Task).IsAssignableFrom(gshHandle.ReturnType) && patcher.TryPatchMethod(
                gshHandle,
                prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(GameStateHandlerHandleMessagePrefix)),
                postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(GameStateHandlerHandleMessagePostfix)),
                optional: true,
                label: "GameStateHandler.HandleMessage"))
            {
                _pedestalHintsPatchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchPedestalRejectionRecovery: " + ex.Message);
        }
    }

    private static bool IsObservedBoardSyncActiveSafe()
    {
        try
        {
            return _bmIsObservedBoardSyncActiveMethod != null
                && _bmIsObservedBoardSyncActiveMethod.Invoke(null, null) is bool active
                && active;
        }
        catch
        {
            return false;
        }
    }

    private static void ResetPendingPedestalCommitTracking()
    {
        Interlocked.Increment(ref _pedestalCommitGeneration);
        _hasPendingPedestalCommit = false;
        _pedestalCommitInFlight = false;
        _pendingPedestalItemCtrlRef = null;
        _pendingPedestalCardId = default;
    }

    private static void ClearPendingPedestalCommitTracking()
    {
        _hasPendingPedestalCommit = false;
        _pedestalCommitInFlight = false;
        _pendingPedestalItemCtrlRef = null;
        _pendingPedestalCardId = default;
    }

    private static void TrackPendingPedestalCommit(InstanceId cardId, object itemController, bool markInFlight)
    {
        Interlocked.Increment(ref _pedestalCommitGeneration);
        _hasPendingPedestalCommit = true;
        if (markInFlight)
        {
            _pedestalCommitInFlight = true;
        }
        if (cardId != default)
        {
            if (_pendingPedestalCardId != default && _pendingPedestalCardId != cardId)
            {
                _pendingPedestalItemCtrlRef = null;
            }
            _pendingPedestalCardId = cardId;
        }

        if (itemController != null && !(itemController is UnityEngine.Object uo && uo == null))
        {
            _pendingPedestalItemCtrlRef = new WeakReference<object>(itemController);
            if (_pendingPedestalCardId == default)
            {
                var cardData = GameInspector.GetMember(itemController, "CardData");
                if (cardData is ICard ic)
                {
                    _pendingPedestalCardId = ic.GetInstanceId();
                }
            }
        }
        else if (cardId != default)
        {
            var resolved = ResolveItemControllerByInstanceId(cardId);
            if (resolved != null)
            {
                _pendingPedestalItemCtrlRef = new WeakReference<object>(resolved);
            }
        }
    }

    private static object ResolveItemControllerByInstanceId(InstanceId cardId)
    {
        if (cardId == default) return null;
        try
        {
            var entities = GameInspector.GetDataEntities();
            var lookup = GameInspector.GetDataLookup();
            if (entities != null && lookup != null && entities.Contains(cardId))
            {
                var entity = entities[cardId];
                if (entity != null)
                {
                    _getCardControllerMethod ??= lookup.GetType().GetMethod(
                        "GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var ctrl = _getCardControllerMethod?.Invoke(lookup, new[] { entity });
                    if (ctrl != null && !(ctrl is UnityEngine.Object uo && uo == null))
                    {
                        return ctrl;
                    }
                }
            }

            if (lookup != null && GameInspector.GetMember(lookup, "CardControllerDictionary") is IDictionary dict)
            {
                foreach (DictionaryEntry kv in dict)
                {
                    if (kv.Key is ICard ic && ic.GetInstanceId() == cardId)
                    {
                        var ctrl = kv.Value;
                        if (ctrl != null && !(ctrl is UnityEngine.Object uo && uo == null))
                        {
                            return ctrl;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static bool IsUpgradeOrEnchantAnimationActivelyPlaying(object boardManager)
    {
        if (boardManager == null || (boardManager is UnityEngine.Object bmUo && bmUo == null))
            return false;
        try
        {
            if (_bmIsPlayingUpgradeOrFuseAnimProp?.GetValue(boardManager) is bool playing && playing)
                return true;

            var enchMgr = _bmEnchantManagerField?.GetValue(boardManager);
            if (enchMgr != null && !(enchMgr is UnityEngine.Object emUo && emUo == null))
            {
                _enchantMgrIsEnchantingProp ??= enchMgr.GetType().GetProperty("IsEnchanting", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_enchantMgrIsEnchantingProp?.GetValue(enchMgr) is bool enchanting && enchanting)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsPedestalProcessingFlagStuck(object boardManager = null)
    {
        try
        {
            boardManager ??= GameInspector.GetBoardManagerCached();
            if (boardManager == null || (boardManager is UnityEngine.Object bmUo && bmUo == null))
                return false;
            if (_bmIsProcessingUpgradeProp?.GetValue(boardManager) is not bool isProc || !isProc)
                return false;
            return !IsUpgradeOrEnchantAnimationActivelyPlaying(boardManager);
        }
        catch
        {
            return false;
        }
    }

    private static bool ClearBoardUpgradeOrFuseOrEnchantProcessing(object boardManager = null)
    {
        try
        {
            boardManager ??= GameInspector.GetBoardManagerCached();
            if (boardManager == null || (boardManager is UnityEngine.Object bmUo && bmUo == null))
                return false;

            if (IsUpgradeOrEnchantAnimationActivelyPlaying(boardManager))
                return false;

            bool wasSet = _bmIsProcessingUpgradeProp?.GetValue(boardManager) is bool b && b;
            if (!wasSet) return false;

            if (_bmClearUpgradeOrFuseOrEnchantMethod != null)
            {
                _bmClearUpgradeOrFuseOrEnchantMethod.Invoke(boardManager, null);
                return true;
            }

            if (_bmIsProcessingUpgradeSetter != null)
            {
                _bmIsProcessingUpgradeSetter.Invoke(boardManager, new object[] { false });
                return true;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ClearBoardUpgradeOrFuseOrEnchantProcessing: " + ex.Message);
        }
        return false;
    }

    private static bool RestoreItemControllerMovementAndMoveBack(object ctrl, bool onlyIfMovementDisabled = false)
    {
        if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null))
            return false;
        try
        {
            var ctrlType = ctrl.GetType();
            var enableMethod = _cardCtrlEnableMovementMethod != null && _cardCtrlEnableMovementMethod.DeclaringType.IsInstanceOfType(ctrl)
                ? _cardCtrlEnableMovementMethod
                : AccessTools.Method(ctrlType, "EnableMovement", new[] { typeof(bool) });

            if (onlyIfMovementDisabled)
            {
                var movField = _cardCtrlMovementEnabledField != null && _cardCtrlMovementEnabledField.DeclaringType.IsInstanceOfType(ctrl)
                    ? _cardCtrlMovementEnabledField
                    : FindFieldInHierarchy(ctrlType, "movementEnabled");
                if (movField?.GetValue(ctrl) is bool enabled && enabled)
                {
                    return false;
                }
            }

            // 必须先 EnableMovement(true)，否则 ItemController.MoveBack() 内部的 if (movementEnabled) 会直接跳过归位
            enableMethod?.Invoke(ctrl, new object[] { true });

            var moveBackMethod = _itemCtrlMoveBackMethod != null && _itemCtrlMoveBackMethod.DeclaringType.IsInstanceOfType(ctrl)
                ? _itemCtrlMoveBackMethod
                : AccessTools.Method(ctrlType, "MoveBack", Type.EmptyTypes);
            moveBackMethod?.Invoke(ctrl, null);
            return true;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RestoreItemControllerMovementAndMoveBack: " + ex.Message);
            return false;
        }
    }

    private static void RecoverPendingPedestalCommit(string reason)
    {
        PostToMainThread(() => RecoverPendingPedestalCommitOnMainThread(reason));
    }

    private static void RecoverPendingPedestalCommitIfGenerationMatches(int generation, string reason)
    {
        if (generation != Volatile.Read(ref _pedestalCommitGeneration))
            return;
        if (!_pedestalCommitInFlight && !_hasPendingPedestalCommit)
            return;
        PostToMainThread(() =>
        {
            if (generation != Volatile.Read(ref _pedestalCommitGeneration))
                return;
            if (!_pedestalCommitInFlight && !_hasPendingPedestalCommit)
                return;
            RecoverPendingPedestalCommitOnMainThread(reason);
        });
    }

    private static void RecoverPendingPedestalCommitOnMainThread(string reason)
    {
        try
        {
            var bm = GameInspector.GetBoardManagerCached();
            bool hadPending = _hasPendingPedestalCommit || _pedestalCommitInFlight;
            bool flagStuck = IsPedestalProcessingFlagStuck(bm);
            if (!hadPending && !flagStuck)
                return;

            object targetCtrl = null;
            if (_pendingPedestalItemCtrlRef != null
                && _pendingPedestalItemCtrlRef.TryGetTarget(out var cachedCtrl)
                && cachedCtrl != null
                && !(cachedCtrl is UnityEngine.Object cuo && cuo == null))
            {
                targetCtrl = cachedCtrl;
            }

            InstanceId cardId = _pendingPedestalCardId;
            if (targetCtrl == null && cardId != default)
            {
                targetCtrl = ResolveItemControllerByInstanceId(cardId);
            }

            // 先清理待处理态，确保可重入安全；短暂保留 cardId 供紧随其后的 NetMessageGameStateSync 做二次核验
            _hasPendingPedestalCommit = false;
            _pedestalCommitInFlight = false;
            _pendingPedestalItemCtrlRef = null;
            _pendingPedestalCardId = default;
            if (cardId != default)
            {
                _lastRecoveredPedestalCardId = cardId;
                _hasLastRecoveredPedestalCardId = true;
                _lastRecoveredPedestalAt = Time.unscaledTime;
            }

            bool restoredItem = RestoreItemControllerMovementAndMoveBack(targetCtrl, onlyIfMovementDisabled: false);
            bool clearedFlag = ClearBoardUpgradeOrFuseOrEnchantProcessing(bm);

            if (restoredItem || clearedFlag || hadPending)
            {
                int cnt = Interlocked.Increment(ref _pedestalCommitRecoveredCount);
                BazaarLauncherPlugin.Log?.LogWarning(
                    $"CompatAuditPatch[P0-17]: 祭坛提交拒单/异常中断已解砖 (reason={reason}, cardId={cardId}, restoredItem={restoredItem}, clearedProcessingFlag={clearedFlag}, count={cnt})");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RecoverPendingPedestalCommitOnMainThread: " + ex.Message);
        }
    }

    private static void ItemDroppedOnPedestalHandlerPostfix(object controller)
    {
        try
        {
            if (controller == null || (controller is UnityEngine.Object uo && uo == null))
                return;
            // 锦标赛观战回放触发的本地演出不发送 CommitToPedestal，避免误记为待提交命令
            if (IsObservedBoardSyncActiveSafe())
                return;
            if (!IsInPedestalContext())
                return;

            InstanceId cardId = default;
            var cardData = GameInspector.GetMember(controller, "CardData");
            if (cardData is ICard ic)
            {
                cardId = ic.GetInstanceId();
            }
            TrackPendingPedestalCommit(cardId, controller, markInFlight: false);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ItemDroppedOnPedestalHandlerPostfix: " + ex.Message);
        }
    }

    private static void SendCommitToPedestalPrefix(object __instance, InstanceId cardId)
    {
        bool wasAlreadyProcessing = false;
        _inSendCommitToPedestalCall = true;
        try
        {
            wasAlreadyProcessing = __instance != null
                && _cmdIsProcessingField?.GetValue(__instance) is bool proc
                && proc;
            TrackPendingPedestalCommit(cardId, null, markInFlight: true);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.SendCommitToPedestalPrefix: " + ex.Message);
        }
        (_pedestalCmdWasBusyStack ??= new Stack<bool>(2)).Push(wasAlreadyProcessing);
    }

    private static Exception SendCommitToPedestalFinalizer(Exception __exception)
    {
        _inSendCommitToPedestalCall = false;
        bool wasAlreadyProcessing = false;
        if (_pedestalCmdWasBusyStack != null && _pedestalCmdWasBusyStack.Count > 0)
        {
            wasAlreadyProcessing = _pedestalCmdWasBusyStack.Pop();
        }

        if (__exception != null)
        {
            RecoverPendingPedestalCommit("SendCommitToPedestalException:" + __exception.GetType().Name);
        }
        else if (wasAlreadyProcessing)
        {
            // Cmd.ExecuteAndProcess 在 _isProcessing == true 时会直接忽略本次提交，立即解砖
            RecoverPendingPedestalCommit("SendCommitToPedestalIgnoredWhileBusy");
        }
        return __exception;
    }

    private static void CmdExecuteAndProcessPostfix(ref Task __result)
    {
        try
        {
            if (!_inSendCommitToPedestalCall || !_pedestalCommitInFlight)
                return;

            int gen = Volatile.Read(ref _pedestalCommitGeneration);
            if (__result == null || __result.IsCompleted)
            {
                RecoverPendingPedestalCommitIfGenerationMatches(gen, "CmdExecuteAndProcessCompletedSyncWithoutUpgrade");
                return;
            }

            __result = WrapPedestalCommitCommandTask(__result, gen);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CmdExecuteAndProcessPostfix: " + ex.Message);
        }
    }

    private static async Task WrapPedestalCommitCommandTask(Task inner, int generation)
    {
        try
        {
            await inner;
        }
        finally
        {
            RecoverPendingPedestalCommitIfGenerationMatches(generation, "CmdExecuteAndProcessCompletedWithoutUpgrade");
        }
    }

    private static void PedestalOnGameSimMessagePrefix(NetMessageGameSim message)
    {
        try
        {
            var events = message?.Data?.Events;
            if (events == null) return;

            bool hasUpgradeOrEnchant = false;
            bool hasOtherPedestalSuccess = false;
            bool leavesPedestal = false;
            GameSimEventStateTransitioned lastTransition = null;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt is GameSimEventCardUpgraded || evt is GameSimEventCardEnchanted)
                {
                    hasUpgradeOrEnchant = true;
                }
                else if (evt is GameSimEventCardTransformed || evt is GameSimEventPedestalActivated)
                {
                    hasOtherPedestalSuccess = true;
                }
                else if (evt is GameSimEventStateTransitioned st)
                {
                    lastTransition = st;
                    if (st.ToState != ERunState.Pedestal)
                    {
                        leavesPedestal = true;
                    }
                }
            }

            // P2-32：捕获 Mod 链式祭坛（Pedestal -> Pedestal/Encounter）的下一跳有效 CurrentEncounterId，
            // 供原生 PedestalState.cs:66 清空 Data.CurrentEncounterId = null 后在 AppState.SetState 前写回
            if (lastTransition != null)
            {
                if (lastTransition.ToState is ERunState.Pedestal or ERunState.Encounter)
                {
                    Guid nextEncId = Guid.Empty;
                    if (!string.IsNullOrEmpty(lastTransition.CurrentEncounterId))
                    {
                        Guid.TryParse(lastTransition.CurrentEncounterId, out nextEncId);
                    }
                    if (nextEncId == Guid.Empty)
                    {
                        string runStateEncId = message?.Data?.CurrentState?.CurrentEncounterId;
                        if (!string.IsNullOrEmpty(runStateEncId))
                        {
                            Guid.TryParse(runStateEncId, out nextEncId);
                        }
                    }

                    if (nextEncId != Guid.Empty)
                    {
                        _pendingChainedPedestalTargetState = lastTransition.ToState;
                        _pendingChainedPedestalEncounterId = nextEncId;
                        _pendingChainedPedestalUntil = Time.unscaledTime + 12f;
                    }
                    else
                    {
                        _pendingChainedPedestalEncounterId = Guid.Empty;
                    }
                }
                else
                {
                    _pendingChainedPedestalEncounterId = Guid.Empty;
                }
            }

            if (hasUpgradeOrEnchant)
            {
                // 服务端已接受升阶/附魔提交，立即关闭挂起标记，由原生升阶/附魔动画结束后复位 IsProcessingUpgradeOrFuseOrEnchant
                ClearPendingPedestalCommitTracking();
            }
            else if (hasOtherPedestalSuccess)
            {
                // 变形祭坛（TPedestalBehaviorTransform 等）成功提交时发 PedestalActivated / CardTransformed 而非升阶/附魔事件：
                // 仅清除挂起追踪并归零 IsProcessingUpgradeOrFuseOrEnchant（防离场 10 秒死等），不对正要变形销毁的旧卡调用 MoveBack() 或误报拒单
                ClearPendingPedestalCommitTracking();
                ClearBoardUpgradeOrFuseOrEnchantProcessing();
            }
            else if (leavesPedestal && (_hasPendingPedestalCommit || IsPedestalProcessingFlagStuck()))
            {
                RecoverPendingPedestalCommit("PedestalStateTransitionedWithoutUpgrade");
            }
        }
        catch { }
    }

    private static void AppStateSetStateChainedPedestalPrefix(ERunState runState)
    {
        try
        {
            if (_pendingChainedPedestalEncounterId == Guid.Empty) return;

            if (Time.unscaledTime > _pendingChainedPedestalUntil)
            {
                _pendingChainedPedestalEncounterId = Guid.Empty;
                return;
            }

            if (runState == _pendingChainedPedestalTargetState
                && (runState is ERunState.Pedestal or ERunState.Encounter))
            {
                Guid nextEncId = _pendingChainedPedestalEncounterId;
                _pendingChainedPedestalEncounterId = Guid.Empty;

                if (_dataCurrentEncounterIdProp == null)
                {
                    var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
                    _dataCurrentEncounterIdProp = dataType?.GetProperty("CurrentEncounterId", BindingFlags.Public | BindingFlags.Static);
                }

                if (_dataCurrentEncounterIdProp != null)
                {
                    var curVal = _dataCurrentEncounterIdProp.GetValue(null);
                    bool curMissing = curVal == null || (curVal is Guid g && g == Guid.Empty);
                    if (curMissing)
                    {
                        _dataCurrentEncounterIdProp.SetValue(null, (Guid?)nextEncId);
                        // 同步触发 BoardManager.TryLoadCurrentEncounterSO()，避免 PedestalState 移除 StateTransitioned 后残留旧基座 _encounterAssetDataSO
                        var bm = GameInspector.GetBoardManagerCached();
                        if (IsUnityObjectAlive(bm))
                        {
                            _bmTryLoadCurrentEncounterSoMethod ??= bm.GetType().GetMethod("TryLoadCurrentEncounterSO", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                            if (_bmTryLoadCurrentEncounterSoMethod != null && _bmTryLoadCurrentEncounterSoMethod.DeclaringType.IsInstanceOfType(bm))
                            {
                                _ = _bmTryLoadCurrentEncounterSoMethod.Invoke(bm, null) as Task;
                            }
                        }
                        BazaarLauncherPlugin.Log?.LogInfo(
                            $"CompatAuditPatch[P2-32]: 链式祭坛跳转在 AppState.SetState({runState}) 前写回下一跳 CurrentEncounterId={nextEncId}");
                    }
                }
            }
            else
            {
                _pendingChainedPedestalEncounterId = Guid.Empty;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.AppStateSetStateChainedPedestalPrefix: " + ex.Message);
        }
    }

    private static void TransformManagerOnCardTransformedPostfix()
    {
        try
        {
            var appStateName = GameInspector.GetAppStateName();
            if (appStateName is "CombatState" or "PVPCombatState" or "ReplayState")
                return;

            // P2-16：局外变形（TPedestalBehaviorTransform 等）触发后立即尝试清零 IsProcessingUpgradeOrFuseOrEnchant，
            // 并因 OnCombatCardTransformed 为 async void，补挂 650ms 主线程延迟复核确保 SpawnCardInstantly 完成后不会残留标志
            ClearBoardUpgradeOrFuseOrEnchantProcessing();
            int gen = Volatile.Read(ref _uiRefreshGeneration);
            _ = ClearUpgradeFlagAfterTransformDelayAsync(gen);
        }
        catch { }
    }

    private static async Task ClearUpgradeFlagAfterTransformDelayAsync(int generation)
    {
        try
        {
            await Task.Delay(650);
            if (generation != Volatile.Read(ref _uiRefreshGeneration))
                return;
            PostToMainThread(() =>
            {
                if (generation != Volatile.Read(ref _uiRefreshGeneration))
                    return;
                var appStateName = GameInspector.GetAppStateName();
                if (appStateName is "CombatState" or "PVPCombatState" or "ReplayState")
                    return;
                ClearBoardUpgradeOrFuseOrEnchantProcessing();
            });
        }
        catch { }
    }

    private static void PedestalOnExitPrefix(object __instance)
    {
        try
        {
            // PedestalState 未重写 OnExit()，AccessTools.Method 会挂到基类 AppState.OnExit()；必须按实例类型过滤，避免非祭坛态退出误触发
            if (__instance == null) return;
            bool isPedestal = (_pedestalStateType != null && _pedestalStateType.IsInstanceOfType(__instance))
                || string.Equals(__instance.GetType().Name, "PedestalState", StringComparison.Ordinal);
            if (!isPedestal) return;

            if (_hasPendingPedestalCommit || IsPedestalProcessingFlagStuck())
            {
                RecoverPendingPedestalCommit("PedestalState.OnExit");
            }
            // P2-16：离开 PedestalState 时无论挂起追踪是否已被变形事件提前清空，均兜底归零 IsProcessingUpgradeOrFuseOrEnchant
            ClearBoardUpgradeOrFuseOrEnchantProcessing();
            // P1-13：切出祭坛时兜底核验所有玩家 ItemController 的 uiTier 与 CardData.Tier 一致性，缺失刷新则强调 UpdateCard()
            ReconcilePedestalUpgradedItemVisuals(_lastPedestalUpgradedCardId, "PedestalState.OnExit", scanAllPlayerItems: true);
        }
        catch { }
    }

    private static void NetworkManagerHandleCommandErrorPostfix(NetMessageError message)
    {
        try
        {
            if (_hasPendingPedestalCommit || _pedestalCommitInFlight || (IsInPedestalContext() && IsPedestalProcessingFlagStuck()))
            {
                RecoverPendingPedestalCommit($"NetworkManager.HandleCommandErrorMessage({message?.ErrorType})");
            }
        }
        catch { }
    }

    private static void NetworkManagerHandleCommandFailurePostfix(Exception error)
    {
        try
        {
            if (_hasPendingPedestalCommit || _pedestalCommitInFlight || (IsInPedestalContext() && IsPedestalProcessingFlagStuck()))
            {
                RecoverPendingPedestalCommit($"NetworkManager.HandleCommandFailure({error?.GetType().Name})");
            }
        }
        catch { }
    }

    private static void NetworkManagerHandleRecoveryFailurePostfix(string error)
    {
        try
        {
            if (_hasPendingPedestalCommit || _pedestalCommitInFlight || IsPedestalProcessingFlagStuck())
            {
                RecoverPendingPedestalCommit("NetworkManager.HandleRecoveryFailure");
            }
        }
        catch { }
    }

    private static void AppStateOnErrorMessageReceivedPostfix(NetMessageError message)
    {
        try
        {
            if (_hasPendingPedestalCommit || _pedestalCommitInFlight || (IsInPedestalContext() && IsPedestalProcessingFlagStuck()))
            {
                RecoverPendingPedestalCommit($"AppState.OnErrorMessageReceived({message?.ErrorType})");
            }
        }
        catch { }
    }

    private static void GameStateHandlerHandleMessagePrefix()
    {
        try
        {
            if (_hasPendingPedestalCommit || _pedestalCommitInFlight)
            {
                RecoverPendingPedestalCommit("GameStateSyncRollback:BeforeSync");
            }
            else if (IsInPedestalContext() && IsPedestalProcessingFlagStuck())
            {
                ClearBoardUpgradeOrFuseOrEnchantProcessing();
            }
        }
        catch { }
    }

    private static void GameStateHandlerHandleMessagePostfix(ref Task __result)
    {
        try
        {
            bool recentRecovery = _hasLastRecoveredPedestalCardId && (Time.unscaledTime - _lastRecoveredPedestalAt <= 3f);
            if (!_hasPendingPedestalCommit && !_pedestalCommitInFlight && !recentRecovery && !IsPedestalProcessingFlagStuck())
                return;

            InstanceId verifyCardId = _pendingPedestalCardId != default ? _pendingPedestalCardId : _lastRecoveredPedestalCardId;
            if (__result == null || __result.IsCompleted)
            {
                PostToMainThread(() => VerifyPedestalPostStateSyncRecovery(verifyCardId));
                return;
            }

            __result = WrapPedestalStateSyncRecoveryTask(__result, verifyCardId);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.GameStateHandlerHandleMessagePostfix: " + ex.Message);
        }
    }

    private static async Task WrapPedestalStateSyncRecoveryTask(Task inner, InstanceId verifyCardId)
    {
        try
        {
            await inner;
        }
        finally
        {
            PostToMainThread(() => VerifyPedestalPostStateSyncRecovery(verifyCardId));
        }
    }

    private static void VerifyPedestalPostStateSyncRecovery(InstanceId verifyCardId)
    {
        try
        {
            if (_hasPendingPedestalCommit || _pedestalCommitInFlight)
            {
                RecoverPendingPedestalCommitOnMainThread("GameStateSyncRollback:AfterSync");
                return;
            }

            ClearBoardUpgradeOrFuseOrEnchantProcessing();
            if (verifyCardId != default)
            {
                var ctrl = ResolveItemControllerByInstanceId(verifyCardId);
                if (ctrl != null)
                {
                    RestoreItemControllerMovementAndMoveBack(ctrl, onlyIfMovementDisabled: true);
                }
            }
        }
        catch { }
    }

    // ── P1-27：BoardTransitionController 切板避让与 IsTransitioning Finalizer 兜底 ──

    private static void PatchBoardTransitionController(SafeHarmonyPatcher patcher)
    {
        try
        {
            _btcType = GameInspector.FindTypeSafe("TheBazaar.BoardTransitionController", "BoardTransitionController");
            if (_btcType == null) return;

            _btcIsTransitioningProp = _btcType.GetProperty("IsTransitioning", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _btcIsTransitioningSetter = _btcIsTransitioningProp?.GetSetMethod(nonPublic: true);
            _btcIsTransitioningBackingField = _btcType.GetField("<IsTransitioning>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

            var prefix = AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardTransitionPrefix));
            var finalizer = AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardTransitionFinalizer));

            var mTransitionTo = AccessTools.Method(_btcType, "TransitionTo", new[] { typeof(string), typeof(string) })
                ?? _btcType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "TransitionTo" && typeof(Task).IsAssignableFrom(m.ReturnType));
            if (mTransitionTo != null && typeof(Task).IsAssignableFrom(mTransitionTo.ReturnType) && patcher.TryPatchMethod(
                mTransitionTo,
                prefix: prefix,
                finalizer: finalizer,
                optional: true,
                label: "BoardTransitionController.TransitionTo"))
            {
                _pedestalHintsPatchedCount++;
            }

            var mTransitionToPlayer = AccessTools.Method(_btcType, "TransitionToPlayerBoard", new[] { typeof(string) })
                ?? _btcType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "TransitionToPlayerBoard" && typeof(Task).IsAssignableFrom(m.ReturnType));
            if (mTransitionToPlayer != null && typeof(Task).IsAssignableFrom(mTransitionToPlayer.ReturnType) && patcher.TryPatchMethod(
                mTransitionToPlayer,
                prefix: prefix,
                finalizer: finalizer,
                optional: true,
                label: "BoardTransitionController.TransitionToPlayerBoard"))
            {
                _pedestalHintsPatchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchBoardTransitionController: " + ex.Message);
        }
    }

    private static bool IsControllerTransitioning(object controller)
    {
        if (controller == null || _btcIsTransitioningProp == null) return false;
        try
        {
            return _btcIsTransitioningProp.GetValue(controller) is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBoardTransitioning(object boardManager = null)
    {
        try
        {
            if (Volatile.Read(ref _activeBoardTransitionCount) > 0)
                return true;

            boardManager ??= GameInspector.GetBoardManagerCached();
            if (boardManager == null || (boardManager is UnityEngine.Object bmUo && bmUo == null) || _bmBoardTransitionField == null)
                return false;

            var btc = _bmBoardTransitionField.GetValue(boardManager);
            return IsControllerTransitioning(btc);
        }
        catch
        {
            return false;
        }
    }

    private static void ResetBoardTransitionFlagIfStuck(object controller, string methodName)
    {
        if (controller == null) return;
        try
        {
            if (!IsControllerTransitioning(controller))
                return;

            bool resetOk = false;
            if (_btcIsTransitioningSetter != null)
            {
                _btcIsTransitioningSetter.Invoke(controller, new object[] { false });
                resetOk = true;
            }
            else if (_btcIsTransitioningBackingField != null)
            {
                _btcIsTransitioningBackingField.SetValue(controller, false);
                resetOk = true;
            }

            if (resetOk)
            {
                int cnt = Interlocked.Increment(ref _boardTransitionResetCount);
                BazaarLauncherPlugin.Log?.LogWarning(
                    $"CompatAuditPatch[P1-27]: BoardTransitionController.{methodName} 异常或提前退出，已兜底归零 IsTransitioning = false (count={cnt})");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ResetBoardTransitionFlagIfStuck: " + ex.Message);
        }
    }

    private static void BoardTransitionPrefix(object __instance)
    {
        bool wasAlreadyTransitioning = false;
        try
        {
            wasAlreadyTransitioning = IsControllerTransitioning(__instance);
        }
        catch { }
        (_boardTransitionWasAlreadyRunningStack ??= new Stack<bool>(2)).Push(wasAlreadyTransitioning);
    }

    private static Exception BoardTransitionFinalizer(object __instance, MethodBase __originalMethod, Exception __exception, ref Task __result)
    {
        bool wasAlreadyTransitioning = false;
        if (_boardTransitionWasAlreadyRunningStack != null && _boardTransitionWasAlreadyRunningStack.Count > 0)
        {
            wasAlreadyTransitioning = _boardTransitionWasAlreadyRunningStack.Pop();
        }

        // 若调用前外层切板已在进行中，本次重入调用会直接返回，绝不可误清外层 IsTransitioning
        if (wasAlreadyTransitioning)
            return __exception;

        string methodName = __originalMethod?.Name ?? "Transition";
        if (__exception != null || __result == null)
        {
            ResetBoardTransitionFlagIfStuck(__instance, methodName);
            return __exception;
        }

        if (__result.IsCompleted)
        {
            // async Task 状态机内若在首个 await 前同步抛错/取消，Roslyn 会返回已 Faulted/Canceled 的 Task
            ResetBoardTransitionFlagIfStuck(__instance, methodName);
            return __exception;
        }

        __result = WrapBoardTransitionTask(__result, __instance, methodName);
        return __exception;
    }

    private static async Task WrapBoardTransitionTask(Task inner, object controller, string methodName)
    {
        Interlocked.Increment(ref _activeBoardTransitionCount);
        try
        {
            await inner;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning(
                $"CompatAuditPatch[P1-27]: BoardTransitionController.{methodName} 异步切板异常: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _activeBoardTransitionCount);
            PostToMainThread(() => ResetBoardTransitionFlagIfStuck(controller, methodName));
        }
    }

    private static bool IsItemAnimatorBusyWithPlayableGraph(object ctrl)
    {
        if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) return false;
        try
        {
            object animatorObj = null;
            var animField = _cardCtrlAnimatorField != null && _cardCtrlAnimatorField.DeclaringType.IsInstanceOfType(ctrl)
                ? _cardCtrlAnimatorField
                : FindFieldInHierarchy(ctrl.GetType(), "Animator");
            if (animField != null)
            {
                animatorObj = animField.GetValue(ctrl);
            }
            else
            {
                var animProp = _cardCtrlAnimatorProp != null && _cardCtrlAnimatorProp.DeclaringType.IsInstanceOfType(ctrl)
                    ? _cardCtrlAnimatorProp
                    : ctrl.GetType().GetProperty("Animator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                animatorObj = animProp?.GetValue(ctrl);
            }

            if (animatorObj != null && !(animatorObj is UnityEngine.Object auo && auo == null))
            {
                _animatorRuntimeControllerProp ??= animatorObj.GetType().GetProperty("runtimeAnimatorController", BindingFlags.Public | BindingFlags.Instance);
                // PlayEnchantmentVisual 期间会临时将 runtimeAnimatorController 置 null 并由 PlayableGraph 接管；
                // 若此时强解 blockVisualUpdates 并刷新 UI 会导致动画竞态/模型卡态（兼顾 Unity 伪空对象判断）
                if (_animatorRuntimeControllerProp != null)
                {
                    var rcVal = _animatorRuntimeControllerProp.GetValue(animatorObj);
                    if (rcVal == null || (rcVal is UnityEngine.Object rUo && rUo == null))
                        return true;
                }
            }
        }
        catch { }

        try
        {
            var bm = GameInspector.GetBoardManagerCached();
            if (bm != null && !(bm is UnityEngine.Object bmUo && bmUo == null))
            {
                var enchMgr = _bmEnchantManagerField?.GetValue(bm);
                if (enchMgr != null && !(enchMgr is UnityEngine.Object emUo && emUo == null))
                {
                    _enchantMgrIsEnchantingProp ??= enchMgr.GetType().GetProperty("IsEnchanting", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_enchantMgrIsEnchantingProp?.GetValue(enchMgr) is bool enchanting && enchanting)
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static void ForceRefreshItemControllerUi(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return;
        var ctrl = ResolveLiveCardControllerForUiRefresh(instanceId);
        if (ctrl == null) return;

        // P1-42：若 ItemController.PlayEnchantmentVisual 正通过 PlayableGraph 接管动画（runtimeAnimatorController == null 或 IsEnchanting），
        // 延后到动画控制器恢复后再解封 blockVisualUpdates 并刷新 UI，彻底避免竞态！
        if (IsItemAnimatorBusyWithPlayableGraph(ctrl))
        {
            int gen = Volatile.Read(ref _uiRefreshGeneration);
            _ = DeferForceRefreshItemControllerUiAsync(instanceId, gen);
            return;
        }

        ApplyItemControllerUiRefreshNow(ctrl);
    }

    private static object ResolveLiveCardControllerForUiRefresh(string instanceId)
    {
        var entities = GameInspector.GetDataEntities();
        var lookup = GameInspector.GetDataLookup();
        if (entities == null || lookup == null) return null;

        var iid = new InstanceId(instanceId);
        if (!entities.Contains(iid)) return null;
        var entity = entities[iid];
        if (entity == null) return null;

        _getCardControllerMethod ??= lookup.GetType().GetMethod(
            "GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var ctrl = _getCardControllerMethod?.Invoke(lookup, new[] { entity });
        if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) return null;
        return ctrl;
    }

    private static async Task DeferForceRefreshItemControllerUiAsync(string instanceId, int generation)
    {
        try
        {
            // 最长轮询 4.0s（40 × 100ms），等待 PlayEnchantmentVisual 的 finally 恢复 runtimeAnimatorController
            for (int attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(100);
                if (generation != Volatile.Read(ref _uiRefreshGeneration))
                    return;

                bool completed = false;
                var tcs = new TaskCompletionSource<bool>();
                PostToMainThread(() =>
                {
                    try
                    {
                        if (generation != Volatile.Read(ref _uiRefreshGeneration))
                        {
                            completed = true;
                            return;
                        }

                        var ctrl = ResolveLiveCardControllerForUiRefresh(instanceId);
                        if (ctrl == null)
                        {
                            completed = true;
                            return;
                        }

                        if (!IsItemAnimatorBusyWithPlayableGraph(ctrl))
                        {
                            ApplyItemControllerUiRefreshNow(ctrl);
                            completed = true;
                        }
                    }
                    catch
                    {
                        completed = true;
                    }
                    finally
                    {
                        tcs.TrySetResult(true);
                    }
                });
                await tcs.Task;
                if (completed) return;
            }

            // 超时兜底：仅当当前控制器依然存活且运行代次一致时执行一次刷新
            PostToMainThread(() =>
            {
                try
                {
                    if (generation != Volatile.Read(ref _uiRefreshGeneration)) return;
                    var ctrl = ResolveLiveCardControllerForUiRefresh(instanceId);
                    if (ctrl != null && !IsItemAnimatorBusyWithPlayableGraph(ctrl))
                    {
                        ApplyItemControllerUiRefreshNow(ctrl);
                    }
                }
                catch { }
            });
        }
        catch { }
    }

    private static void ApplyItemControllerUiRefreshNow(object ctrl)
    {
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

    private static void PatchRewardController(SafeHarmonyPatcher patcher)
    {
        try
        {
            var rcType = GameInspector.FindTypeSafe("RewardController", "TheBazaar.RewardController");
            if (rcType == null) return;

            _rewardHideCompleteField = rcType.GetField("hideComplete", BindingFlags.NonPublic | BindingFlags.Instance);
            // RewardController 继承自 CardController，其 Animator 为公有字段而非属性，优先按层级查字段并保留属性兜底
            _rewardAnimatorField = FindFieldInHierarchy(rcType, "Animator");
            _rewardAnimatorProp = rcType.GetProperty("Animator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            _rewardSoundHandlerField = rcType.GetField("soundRewardHandler", BindingFlags.NonPublic | BindingFlags.Instance);

            var mDespawn = AccessTools.Method(rcType, "PlayDespawnAnimation");
            if (mDespawn != null && patcher.TryPatchMethod(
                mDespawn,
                prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PlayDespawnAnimationPrefix)),
                optional: true,
                label: "RewardController.PlayDespawnAnimation"))
            {
                _pedestalHintsPatchedCount++;
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

            var animator = _rewardAnimatorField?.GetValue(__instance) ?? _rewardAnimatorProp?.GetValue(__instance);
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
    private static Func<object, int> _cardCtrlGetBoardSectionIntDelegate;
    private static int _opponentBoardSectionIntValue = int.MinValue;
    private static Func<object, bool> _itemGetIsDraggingDelegate;
    // P1-47：ItemController.PreviewMove 补齐 CanPreviewMove() 与售卖区/储物区检测句柄
    private static Func<object, bool> _itemCanPreviewMoveDelegate;
    private static Func<object, bool> _itemCheckForSellZoneDelegate;
    private static Func<object, bool> _itemCheckForStorageToyDelegate;
    private static Func<object, bool> _itemGetHoveringStorageDelegate;
    private static Action<object, bool> _itemSetHoveringStorageDelegate;
    private static Action<object, bool> _bmSetSellZoneHoveredDelegate;
    private static FieldInfo _bmActiveStorageToyField;
    private static MethodInfo _storageToyOpenMethod;
    private static MethodInfo _storageToyCloseMethod;
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
        _lastPreviewWorldPos = Vector3.zero;
        _lastPreviewStorageOpen = false;
        _lastPreviewFrame = -100;
        _currentSellZoneHovered = false;
        ResetShowCardHintDedupeCache();
    }

    private static void PatchTargetingAndHints(SafeHarmonyPatcher patcher)
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
            if (xmostGetTargets != null && patcher.TryPatchMethod(
                xmostGetTargets,
                prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(TargetCardXMostGetTargetsPrefix)),
                optional: true,
                label: "TTargetCardXMost.GetTargets"))
            {
                _pedestalHintsPatchedCount++;
            }

            var hintServiceType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Hints.HintService");
            var listHints = hintServiceType == null ? null : AccessTools.Method(hintServiceType, "ListHints");
            if (listHints != null && patcher.TryPatchMethod(
                listHints,
                prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(HintServiceListHintsPrefix)),
                postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(HintServiceListHintsPostfix)),
                optional: true,
                label: "HintService.ListHints"))
            {
                _pedestalHintsPatchedCount++;
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
                        _cardCtrlGetBoardSectionIntDelegate = Expression.Lambda<Func<object, int>>(Expression.Convert(fieldAcc, typeof(int)), p).Compile();
                    }
                    catch
                    {
                        _cardCtrlGetBoardSectionDelegate = obj => _cardCtrlBoardSectionField.GetValue(obj);
                        _cardCtrlGetBoardSectionIntDelegate = obj => Convert.ToInt32(_cardCtrlBoardSectionField.GetValue(obj) ?? -1);
                    }

                    if (_cardCtrlBoardSectionField.FieldType.IsEnum)
                    {
                        try
                        {
                            _opponentBoardSectionIntValue = Convert.ToInt32(Enum.Parse(_cardCtrlBoardSectionField.FieldType, "Opponent"));
                        }
                        catch { }
                    }
                }

                if (_cardCtrlShowCardHintMethod != null && patcher.TryPatchMethod(
                    _cardCtrlShowCardHintMethod,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(CardControllerShowCardHintPrefix)),
                    optional: true,
                    label: "CardController.ShowCardHint"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var hideHints = AccessTools.Method(cardCtrlType, "HideCardHints", Type.EmptyTypes);
                if (hideHints != null && patcher.TryPatchMethod(
                    hideHints,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ResetHintDedupePostfix)),
                    optional: true,
                    label: "CardController.HideCardHints"))
                {
                    _pedestalHintsPatchedCount++;
                }
            }

            // ItemController.PreviewMove Prefix（含 CanPreviewMove + 售卖区/储物区检测保真）& FinishMove / CancelDragFromSystemTransition 复位
            var itemCtrlType = GameInspector.FindTypeSafe("ItemController", "TheBazaar.ItemController");
            if (itemCtrlType != null)
            {
                var isDraggingProp = itemCtrlType.GetProperty("IsDragging", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (isDraggingProp != null)
                {
                    _itemGetIsDraggingDelegate = CompilePropertyGetter<bool>(itemCtrlType, isDraggingProp);
                }

                // P1-47：编译 PreviewMove 前置校验与售卖区/储物区检测热路径委托
                var canPreviewMethod = AccessTools.Method(itemCtrlType, "CanPreviewMove", Type.EmptyTypes);
                _itemCanPreviewMoveDelegate = CompileBoolMethod(itemCtrlType, canPreviewMethod);

                var checkSellZoneMethod = AccessTools.Method(itemCtrlType, "CheckForSellZone", Type.EmptyTypes);
                _itemCheckForSellZoneDelegate = CompileBoolMethod(itemCtrlType, checkSellZoneMethod);

                var checkStorageToyMethod = AccessTools.Method(itemCtrlType, "CheckForStorageToy", Type.EmptyTypes);
                _itemCheckForStorageToyDelegate = CompileBoolMethod(itemCtrlType, checkStorageToyMethod);

                var hoveringStorageField = FindFieldInHierarchy(itemCtrlType, "hoveringStorage");
                if (hoveringStorageField != null && hoveringStorageField.FieldType == typeof(bool))
                {
                    try
                    {
                        var pObj = Expression.Parameter(typeof(object), "inst");
                        var pVal = Expression.Parameter(typeof(bool), "val");
                        var acc = Expression.Field(Expression.Convert(pObj, itemCtrlType), hoveringStorageField);
                        _itemGetHoveringStorageDelegate = Expression.Lambda<Func<object, bool>>(acc, pObj).Compile();
                        _itemSetHoveringStorageDelegate = Expression.Lambda<Action<object, bool>>(Expression.Assign(acc, pVal), pObj, pVal).Compile();
                    }
                    catch
                    {
                        _itemGetHoveringStorageDelegate = obj => hoveringStorageField.GetValue(obj) is bool b && b;
                        _itemSetHoveringStorageDelegate = (obj, val) => hoveringStorageField.SetValue(obj, val);
                    }
                }

                var previewMove = AccessTools.Method(itemCtrlType, "PreviewMove", new[] { typeof(Vector3) });
                if (previewMove != null && patcher.TryPatchMethod(
                    previewMove,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ItemControllerPreviewMovePrefix)),
                    optional: true,
                    label: "ItemController.PreviewMove"))
                {
                    _pedestalHintsPatchedCount++;
                }

                var finishMove = AccessTools.Method(itemCtrlType, "FinishMove", new[] { typeof(bool) })
                    ?? AccessTools.Method(itemCtrlType, "FinishMove");
                if (finishMove != null && patcher.TryPatchMethod(
                    finishMove,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ItemControllerFinishMovePostfix)),
                    optional: true,
                    label: "ItemController.FinishMove"))
                {
                    _pedestalHintsPatchedCount++;
                }

                // P1-43：系统切态强行取消拖拽时同步清零拖拽/售卖区悬停缓存
                var cancelDrag = AccessTools.Method(itemCtrlType, "CancelDragFromSystemTransition", Type.EmptyTypes);
                if (cancelDrag != null && patcher.TryPatchMethod(
                    cancelDrag,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ItemControllerCancelDragPostfix)),
                    optional: true,
                    label: "ItemController.CancelDragFromSystemTransition"))
                {
                    _pedestalHintsPatchedCount++;
                }
            }

            // BoardManager.SetSellZoneHovered 边沿触发售卖区提示刷新 + ClearCardHighlights 复位去重缓存
            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                var setSellZoneHovered = AccessTools.Method(bmType, "SetSellZoneHovered", new[] { typeof(bool) });
                if (setSellZoneHovered != null)
                {
                    try
                    {
                        var pBm = Expression.Parameter(typeof(object), "bm");
                        var pFlag = Expression.Parameter(typeof(bool), "hovered");
                        _bmSetSellZoneHoveredDelegate = Expression.Lambda<Action<object, bool>>(
                            Expression.Call(Expression.Convert(pBm, bmType), setSellZoneHovered, pFlag), pBm, pFlag).Compile();
                    }
                    catch
                    {
                        _bmSetSellZoneHoveredDelegate = (bm, hovered) => setSellZoneHovered.Invoke(bm, new object[] { hovered });
                    }

                    if (patcher.TryPatchMethod(
                        setSellZoneHovered,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardSetSellZoneHoveredPostfix)),
                        optional: true,
                        label: "BoardManager.SetSellZoneHovered"))
                    {
                        _pedestalHintsPatchedCount++;
                    }
                }

                _bmActiveStorageToyField = bmType.GetField("activeStorageToy", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_bmActiveStorageToyField != null)
                {
                    _storageToyOpenMethod = AccessTools.Method(_bmActiveStorageToyField.FieldType, "OpenStorageToy", Type.EmptyTypes);
                    _storageToyCloseMethod = AccessTools.Method(_bmActiveStorageToyField.FieldType, "CloseStorageToy", Type.EmptyTypes);
                }

                var clearHighlights = AccessTools.Method(bmType, "ClearCardHighlights", Type.EmptyTypes);
                if (clearHighlights != null && patcher.TryPatchMethod(
                    clearHighlights,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ResetHintDedupePostfix)),
                    optional: true,
                    label: "BoardManager.ClearCardHighlights"))
                {
                    _pedestalHintsPatchedCount++;
                }
            }

            // P2-42：修补原生 CardTooltipController.ClearCurrentCard 先置空 _primaryTooltipController 再退订 OnShowCompleted 的死代码泄漏
            var ctcType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.CardTooltipController");
            if (ctcType != null)
            {
                _ctcIsInitializedField = ctcType.GetField("_isInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
                _ctcIsLockedField = FindFieldInHierarchy(ctcType, "isLocked");
                _ctcPrimaryControllerField = ctcType.GetField("_primaryTooltipController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _ctcOnPrimaryShowCompletedMethod = ctcType.GetMethod("OnPrimaryShowCompletedThenPulse", BindingFlags.NonPublic | BindingFlags.Instance);
                _btcOnShowCompletedEvent = ctcType.GetEvent("OnShowCompleted", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                var clearCurrentCard = AccessTools.Method(ctcType, "ClearCurrentCard", Type.EmptyTypes);
                if (clearCurrentCard != null && _ctcPrimaryControllerField != null && _ctcOnPrimaryShowCompletedMethod != null && _btcOnShowCompletedEvent != null
                    && patcher.TryPatchMethod(
                        clearCurrentCard,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(CardTooltipClearCurrentCardPrefix)),
                        optional: true,
                        label: "CardTooltipController.ClearCurrentCard"))
                {
                    _pedestalHintsPatchedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchTargetingAndHints: " + ex.Message);
        }
    }

    private static void CardTooltipClearCurrentCardPrefix(object __instance)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            // 严格对齐 CardTooltipController.cs:1124 条件：仅在 _isInitialized && !isLocked 时才执行清理退订
            if (_ctcIsInitializedField != null && _ctcIsInitializedField.GetValue(__instance) is bool init && !init)
                return;
            if (_ctcIsLockedField != null && _ctcIsLockedField.GetValue(__instance) is bool locked && locked)
                return;

            var primary = _ctcPrimaryControllerField?.GetValue(__instance);
            if (primary == null || (primary is UnityEngine.Object puo && puo == null))
                return;

            if (_btcOnShowCompletedEvent != null && _ctcOnPrimaryShowCompletedMethod != null)
            {
                var handler = Delegate.CreateDelegate(
                    _btcOnShowCompletedEvent.EventHandlerType,
                    __instance,
                    _ctcOnPrimaryShowCompletedMethod,
                    throwOnBindFailure: false);
                if (handler != null)
                {
                    _btcOnShowCompletedEvent.RemoveEventHandler(primary, handler);
                }
            }
        }
        catch { }
    }

    private static Func<object, bool> CompileBoolMethod(Type declaringType, MethodInfo method)
    {
        if (declaringType == null || method == null || method.ReturnType != typeof(bool) || method.GetParameters().Length != 0)
            return null;
        try
        {
            var p = Expression.Parameter(typeof(object), "inst");
            var call = Expression.Call(Expression.Convert(p, declaringType), method);
            return Expression.Lambda<Func<object, bool>>(call, p).Compile();
        }
        catch
        {
            return obj => method.Invoke(obj, null) is bool b && b;
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

    // ── P1-47：零分配稳定槽位排序（严格对齐官方 TargetSystem：Section -> Owner(Opponent 先于 Player) -> LeftSocketId -> 原始索引）──

    [ThreadStatic] private static Dictionary<InstanceId, int> _tsXMostOriginalIndex;

    private static int CompareXMostCards(ICard a, ICard b, IPlayer opponentPlayer, IPlayer localPlayer, Dictionary<InstanceId, int> originalIndex)
    {
        int secA = a.Section == EInventorySection.Hand ? 0 : (a.Section == EInventorySection.Stash ? 1 : 2);
        int secB = b.Section == EInventorySection.Hand ? 0 : (b.Section == EInventorySection.Stash ? 1 : 2);
        int cmp = secA.CompareTo(secB);
        if (cmp != 0) return cmp;

        // 对齐官方 TargetSystem.GetAllHands / GetAllStashes / GetAllHandsAndStashes：Opponent 先于 Player
        int ownerA = ReferenceEquals(a.Owner, opponentPlayer) ? 0 : (ReferenceEquals(a.Owner, localPlayer) ? 1 : 2);
        int ownerB = ReferenceEquals(b.Owner, opponentPlayer) ? 0 : (ReferenceEquals(b.Owner, localPlayer) ? 1 : 2);
        cmp = ownerA.CompareTo(ownerB);
        if (cmp != 0) return cmp;

        int sockA = a.LeftSocketId.HasValue ? (int)a.LeftSocketId.Value : int.MaxValue;
        int sockB = b.LeftSocketId.HasValue ? (int)b.LeftSocketId.Value : int.MaxValue;
        cmp = sockA.CompareTo(sockB);
        if (cmp != 0) return cmp;

        if (originalIndex != null)
        {
            int idxA = originalIndex.TryGetValue(a.GetInstanceId(), out int ia) ? ia : int.MaxValue;
            int idxB = originalIndex.TryGetValue(b.GetInstanceId(), out int ib) ? ib : int.MaxValue;
            return idxA.CompareTo(idxB);
        }
        return 0;
    }

    private static void StableSortXMostInPlace(List<ICard> list, IPlayer opponentPlayer, IPlayer localPlayer, Dictionary<InstanceId, int> originalIndex)
    {
        // 原位稳定插入排序：卡牌数 <= 20 时 O(N) 接近有序且 0 额外堆分配，严格保持稳定序
        for (int i = 1; i < list.Count; i++)
        {
            var key = list[i];
            int j = i - 1;
            while (j >= 0 && CompareXMostCards(list[j], key, opponentPlayer, localPlayer, originalIndex) > 0)
            {
                list[j + 1] = list[j];
                j--;
            }
            list[j + 1] = key;
        }
    }

    private static void PruneNullSocketAndSortInPlace(List<ICard> list, IPlayer opponentPlayer, IPlayer localPlayer, Dictionary<InstanceId, int> originalIndex)
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
            StableSortXMostInPlace(list, opponentPlayer, localPlayer, originalIndex);
        }
    }

    /// <summary>
    /// 修复 #11 + P1-47 零 LINQ/零装箱稳定排序重构：
    /// 严格对齐服务端 TargetResolver（Collect -> 按需版本同步附魔标签 -> ExcludeSelf(引用+InstanceId) -> 记录原始顺序 -> 原位稳定排序 -> Conditions 过滤 -> Priority -> LeftMost/RightMost）。
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

            // 记录原始集合顺序，确保跨 HashSet 条件过滤与平局比较均严格保序
            var origIndex = _tsXMostOriginalIndex ??= new Dictionary<InstanceId, int>(24);
            origIndex.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c != null)
                {
                    var cid = c.GetInstanceId();
                    if (!origIndex.ContainsKey(cid))
                    {
                        origIndex[cid] = i;
                    }
                }
            }

            var localPlayer = ctx.Run.GetPlayer();
            var opponentPlayer = ctx.Run.GetOpponent();

            // 2. 原位过滤未入格卡牌并按 (Section, Owner, LeftSocketId, OriginalIndex) 稳定排序，0 LINQ 分配
            PruneNullSocketAndSortInPlace(candidates, opponentPlayer, localPlayer, origIndex);

            // 3. 应用 Conditions 条件筛选后立即重新按稳定键恢复顺序（防止 HashSet 破坏槽位与原始顺序）
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
                        StableSortXMostInPlace(candidates, opponentPlayer, localPlayer, origIndex);
                    }
                }
            }

            // 4. 若指定了优先级条件列表且允许目标数 > 0，按优先级保留并在同级内继续维持稳定槽位顺序
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
                        StableSortXMostInPlace(priScratch, opponentPlayer, localPlayer, origIndex);
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
    /// 拖拽 Top 1 + P1-47：ItemController.PreviewMove 微位移短路守卫。
    /// 严格先执行原生 CanPreviewMove() 校验；微位移短路时仍执行售卖区（SetSellZoneHovered(CheckForSellZone())）
    /// 与储物区开关（CheckForStorageToy / OpenStorageToy / CloseStorageToy）检测，仅跳过重复卡槽射线与悬停提示重算。
    /// </summary>
    private static bool ItemControllerPreviewMovePrefix(Component __instance, Vector3 worldPosition, ref bool __result)
    {
        try
        {
            if (__instance == null) return true;

            // P1-47：必须先通过原生 CanPreviewMove() 校验（含锁定 Tooltip、PvP 战后敌方卡、储物箱开关区隔等），绝不越权移动或短路返回 true
            if (_itemCanPreviewMoveDelegate == null)
                return true;

            if (!_itemCanPreviewMoveDelegate(__instance))
            {
                _lastPreviewItemId = 0;
                return true;
            }

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
                // 必须具备完整的售卖区/储物区检测委托与 BoardManager 实例，否则放行原方法以免吞掉区域判定
                var bm = GameInspector.GetBoardManagerCached();
                if (bm == null || (bm is UnityEngine.Object bmUo && bmUo == null)
                    || _itemCheckForSellZoneDelegate == null
                    || _itemCheckForStorageToyDelegate == null
                    || _itemGetHoveringStorageDelegate == null
                    || _itemSetHoveringStorageDelegate == null
                    || _bmSetSellZoneHoveredDelegate == null
                    || _bmActiveStorageToyField == null
                    || _cardCtrlGetBoardSectionIntDelegate == null)
                {
                    return true;
                }

                __instance.transform.position = worldPosition;

                int sectionInt = _cardCtrlGetBoardSectionIntDelegate(__instance);
                if (sectionInt != _opponentBoardSectionIntValue)
                {
                    _bmSetSellZoneHoveredDelegate(bm, _itemCheckForSellZoneDelegate(__instance));
                }

                bool prevHoveringStorage = _itemGetHoveringStorageDelegate(__instance);
                bool nowHoveringStorage = _itemCheckForStorageToyDelegate(__instance);
                _itemSetHoveringStorageDelegate(__instance, nowHoveringStorage);
                if (prevHoveringStorage != nowHoveringStorage)
                {
                    var toy = _bmActiveStorageToyField.GetValue(bm);
                    if (toy != null && !(toy is UnityEngine.Object toyUo && toyUo == null))
                    {
                        if (prevHoveringStorage && !nowHoveringStorage && !storageOpen)
                        {
                            _storageToyCloseMethod?.Invoke(toy, null);
                        }
                        else if (nowHoveringStorage && !prevHoveringStorage)
                        {
                            _storageToyOpenMethod?.Invoke(toy, null);
                        }
                    }
                }

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

    private static void ItemControllerCancelDragPostfix()
    {
        try
        {
            ResetDragPreviewDedupeCache();
            BazaarLauncher.Modules.PerfOptimizerModule.ResetDragHoverCaches();
        }
        catch { }
    }

}
