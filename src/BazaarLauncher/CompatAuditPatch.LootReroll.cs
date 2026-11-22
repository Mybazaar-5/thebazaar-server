using System;
using System.Collections;
using System.Reflection;
using BazaarGameShared.Domain.Runs;
using BazaarLauncher.Core;
using HarmonyLib;
using UnityEngine;

namespace BazaarLauncher;

internal static partial class CompatAuditPatch
{
    // ── V14.3：野怪战利品超过 10 格分页时的原生免费刷新/翻页支持 ──────────────────
    private static PropertyInfo _lrDataCurrentStateProp;
    private static FieldInfo _lrStateNameField;
    private static PropertyInfo _lrStateNameProp;
    private static PropertyInfo _lrRerollsRemainingProp;
    private static PropertyInfo _lrRerollCostProp;
    private static FieldInfo _lrBmButtonsForRevealField;
    private static FieldInfo _lrBmDefaultCombatButtonsField;
    private static FieldInfo _lrBmDefaultMerchantButtonsField;
    private static MethodInfo _lrBbmShowButtonsMethod;
    private static MethodInfo _lrBbmHideButtonsMethod;
    private static MethodInfo _lrBbmToggleExitMethod;
    private static MethodInfo _lrBbmToggleRerollMethod;
    private static MethodInfo _lrBbmEnableExitMethod;
    private static MethodInfo _lrBbmEnableRerollMethod;
    private static PropertyInfo _lrBbmRerollProp;
    private static MethodInfo _lrRerollUpdateViewMethod;
    private static MethodInfo _lrRerollPrepareCostTextMethod;
    private static FieldInfo _lrRerollCostTextField;
    private static PropertyInfo _lrRerollIsEnabledProp;
    private static Type _lrStateOpsEnumType;
    private static int _lrStateOpsRerollFlag;
    private static bool _lrUpdatingRerollView;

    /// <summary>
    /// 修复野怪战利品超过 10 格自动分页后，客户端在 LootState 下隐藏/拒绝刷新按钮导致第 2 页奖励不可达的问题：
    ///   1. LootState.get_AllowedOps：存在多页战利品刷新次数（RerollsRemaining &gt; 0）时放行 StateOps.Reroll；
    ///   2. BoardManager.LoadBoardButtonsByState / RevealBoardButtons / ShowReplayAndRecapButtons：
    ///      在多页 Loot 状态下切换显示自带免费刷新轮盘的 defaultMerchantBoardButtons，并隐藏 defaultCombatBoardButtons；
    ///   3. RerollButton.UpdateView：在多页 Loot 状态下确保 0 金币刷新费用文本预制体就绪且按钮保持可点击。
    /// </summary>
    private static void PatchLootPaginatedReroll(SafeHarmonyPatcher patcher)
    {
        try
        {
            var dataType = AccessTools.TypeByName("TheBazaar.Data");
            _lrDataCurrentStateProp = dataType?.GetProperty("CurrentState", BindingFlags.Public | BindingFlags.Static);
            var runStateType = _lrDataCurrentStateProp?.PropertyType;
            if (runStateType != null)
            {
                // RunState.StateName 在 BazaarGameClient.dll 中为 public ERunState 字段，非属性；优先按字段读取并保留属性回退
                _lrStateNameField = runStateType.GetField("StateName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _lrStateNameProp = runStateType.GetProperty("StateName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _lrRerollsRemainingProp = runStateType.GetProperty("RerollsRemaining", BindingFlags.Public | BindingFlags.Instance);
                _lrRerollCostProp = runStateType.GetProperty("RerollCost", BindingFlags.Public | BindingFlags.Instance);
            }

            _lrStateOpsEnumType = AccessTools.TypeByName("TheBazaar.StateOps");
            if (_lrStateOpsEnumType != null && Enum.IsDefined(_lrStateOpsEnumType, "Reroll"))
            {
                _lrStateOpsRerollFlag = Convert.ToInt32(Enum.Parse(_lrStateOpsEnumType, "Reroll"));
            }

            var bbmType = AccessTools.TypeByName("TheBazaar.BoardButtonsManager");
            if (bbmType != null)
            {
                _lrBbmShowButtonsMethod = AccessTools.Method(bbmType, "ShowButtons");
                _lrBbmHideButtonsMethod = AccessTools.Method(bbmType, "HideButtons");
                _lrBbmToggleExitMethod = AccessTools.Method(bbmType, "ToggleExitButton");
                _lrBbmToggleRerollMethod = AccessTools.Method(bbmType, "ToggleRerollButton");
                _lrBbmEnableExitMethod = AccessTools.Method(bbmType, "EnableExitButton");
                _lrBbmEnableRerollMethod = AccessTools.Method(bbmType, "EnableRerollButton");
                _lrBbmRerollProp = bbmType.GetProperty("Reroll", BindingFlags.Public | BindingFlags.Instance);
            }

            var rerollBtnType = AccessTools.TypeByName("TheBazaar.RerollButton");
            if (rerollBtnType != null)
            {
                _lrRerollUpdateViewMethod = AccessTools.Method(rerollBtnType, "UpdateView");
                _lrRerollPrepareCostTextMethod = AccessTools.Method(rerollBtnType, "PrepareCostTextAsync");
                _lrRerollCostTextField = AccessTools.Field(rerollBtnType, "costTextContainer");
                _lrRerollIsEnabledProp = rerollBtnType.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Instance);
            }

            // 1. 在 AppState.CanHandleOperation / ExecuteIfAllowed 中为多页 LootState 安全放行 StateOps.Reroll（全程标准 bool/Func<bool> 签名，零枚举拆装箱风险）
            var appStateType = AccessTools.TypeByName("TheBazaar.AppState");
            if (appStateType != null && _lrStateOpsEnumType != null && _lrStateOpsRerollFlag != 0)
            {
                var canHandle = AccessTools.Method(appStateType, "CanHandleOperation", new[] { _lrStateOpsEnumType });
                if (canHandle != null)
                {
                    patcher.TryPatchMethod(
                        canHandle,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(AppStateCanHandleOperationPostfix)),
                        optional: true,
                        label: "Compat.LootReroll.AppState.CanHandleOperation");
                }

                var execIfAllowed = AccessTools.Method(appStateType, "ExecuteIfAllowed", new[] { _lrStateOpsEnumType, typeof(Func<bool>) });
                if (execIfAllowed != null)
                {
                    patcher.TryPatchMethod(
                        execIfAllowed,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(AppStateExecuteIfAllowedPrefix)),
                        optional: true,
                        label: "Compat.LootReroll.AppState.ExecuteIfAllowed");
                }
            }

            // 2. BoardManager：多页野怪战利品改用带免费刷新轮盘的 defaultMerchantBoardButtons
            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                _lrBmButtonsForRevealField = AccessTools.Field(bmType, "buttonsForReveal");
                _lrBmDefaultCombatButtonsField = AccessTools.Field(bmType, "defaultCombatBoardButtons");
                _lrBmDefaultMerchantButtonsField = AccessTools.Field(bmType, "defaultMerchantBoardButtons");

                var loadButtons = AccessTools.Method(bmType, "LoadBoardButtonsByState");
                if (loadButtons != null)
                {
                    patcher.TryPatchMethod(
                        loadButtons,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardManagerLoadButtonsPostfix)),
                        optional: true,
                        label: "Compat.LootReroll.BoardManager.LoadBoardButtonsByState");
                }

                var revealButtons = AccessTools.Method(bmType, "RevealBoardButtons");
                if (revealButtons != null)
                {
                    patcher.TryPatchMethod(
                        revealButtons,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardManagerRevealButtonsPostfix)),
                        optional: true,
                        label: "Compat.LootReroll.BoardManager.RevealBoardButtons");
                }

                var showReplayRecap = AccessTools.Method(bmType, "ShowReplayAndRecapButtons");
                if (showReplayRecap != null)
                {
                    patcher.TryPatchMethod(
                        showReplayRecap,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardManagerShowReplayRecapPostfix)),
                        optional: true,
                        label: "Compat.LootReroll.BoardManager.ShowReplayAndRecapButtons");
                }
            }

            // 3. RerollButton.UpdateView：多页 Loot 时确保费用标签就绪并点亮免费刷新按钮
            if (_lrRerollUpdateViewMethod != null)
            {
                patcher.TryPatchMethod(
                    _lrRerollUpdateViewMethod,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(RerollButtonUpdateViewPostfix)),
                    optional: true,
                    label: "Compat.LootReroll.RerollButton.UpdateView");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[LootReroll] 安装多页战利品刷新补丁失败: {ex.Message}");
        }
    }

    private static bool IsPaginatedLootRerollActive()
    {
        try
        {
            object curState = _lrDataCurrentStateProp?.GetValue(null);
            if (curState == null) return false;

            object stateNameObj = _lrStateNameField != null
                ? _lrStateNameField.GetValue(curState)
                : _lrStateNameProp?.GetValue(curState);
            if (stateNameObj == null) return false;
            if (stateNameObj is ERunState ers)
            {
                if (ers != ERunState.Loot) return false;
            }
            else if (!string.Equals(stateNameObj.ToString(), "Loot", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            object remObj = _lrRerollsRemainingProp?.GetValue(curState);
            if (remObj == null) return false;
            uint remaining = Convert.ToUInt32(remObj);
            if (remaining == 0) return false;

            object costObj = _lrRerollCostProp?.GetValue(curState);
            if (costObj == null) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLootRerollOperation(object appStateInstance, object opObj)
    {
        if (appStateInstance == null || opObj == null || _lrStateOpsRerollFlag == 0) return false;
        if (!string.Equals(appStateInstance.GetType().Name, "LootState", StringComparison.Ordinal)) return false;
        try
        {
            return (Convert.ToInt32(opObj) & _lrStateOpsRerollFlag) != 0 && IsPaginatedLootRerollActive();
        }
        catch
        {
            return false;
        }
    }

    private static void AppStateCanHandleOperationPostfix(object __instance, object __0, ref bool __result)
    {
        try
        {
            if (!__result && IsLootRerollOperation(__instance, __0))
            {
                __result = true;
            }
        }
        catch { }
    }

    private static bool AppStateExecuteIfAllowedPrefix(object __instance, object __0, Func<bool> __1, ref bool __result)
    {
        try
        {
            if (IsLootRerollOperation(__instance, __0))
            {
                __result = __1 != null && __1();
                return false;
            }
        }
        catch { }
        return true;
    }

    private static void BoardManagerLoadButtonsPostfix(object __instance)
    {
        try
        {
            if (__instance == null || !IsPaginatedLootRerollActive()) return;
            var combatBtns = _lrBmDefaultCombatButtonsField?.GetValue(__instance);
            var merchantBtns = _lrBmDefaultMerchantButtonsField?.GetValue(__instance);
            if (merchantBtns == null) return;

            if (_lrBmButtonsForRevealField?.GetValue(__instance) is IList list)
            {
                if (combatBtns != null) list.Remove(combatBtns);
                if (!list.Contains(merchantBtns)) list.Add(merchantBtns);
            }

            if (combatBtns != null)
            {
                _lrBbmHideButtonsMethod?.Invoke(combatBtns, null);
            }
        }
        catch { }
    }

    private static void BoardManagerRevealButtonsPostfix(object __instance)
    {
        try
        {
            if (__instance == null || !IsPaginatedLootRerollActive()) return;
            ActivateMerchantButtonsForPaginatedLoot(__instance);
        }
        catch { }
    }

    private static void BoardManagerShowReplayRecapPostfix(object __instance, bool show)
    {
        try
        {
            if (__instance == null || !IsPaginatedLootRerollActive()) return;
            var merchantBtns = _lrBmDefaultMerchantButtonsField?.GetValue(__instance);
            if (merchantBtns == null) return;

            if (show)
            {
                _lrBbmHideButtonsMethod?.Invoke(merchantBtns, null);
            }
            else
            {
                ActivateMerchantButtonsForPaginatedLoot(__instance);
            }
        }
        catch { }
    }

    private static void ActivateMerchantButtonsForPaginatedLoot(object boardManager)
    {
        var combatBtns = _lrBmDefaultCombatButtonsField?.GetValue(boardManager);
        var merchantBtns = _lrBmDefaultMerchantButtonsField?.GetValue(boardManager);
        if (merchantBtns == null) return;

        if (combatBtns != null)
        {
            _lrBbmHideButtonsMethod?.Invoke(combatBtns, null);
        }

        _lrBbmShowButtonsMethod?.Invoke(merchantBtns, null);
        _lrBbmToggleExitMethod?.Invoke(merchantBtns, new object[] { true });
        _lrBbmToggleRerollMethod?.Invoke(merchantBtns, new object[] { true });
        _lrBbmEnableExitMethod?.Invoke(merchantBtns, new object[] { true });
        _lrBbmEnableRerollMethod?.Invoke(merchantBtns, new object[] { true });

        object rerollBtn = _lrBbmRerollProp?.GetValue(merchantBtns);
        if (rerollBtn != null)
        {
            EnsureLootRerollButtonReady(rerollBtn);
        }
    }

    private static void RerollButtonUpdateViewPostfix(object __instance)
    {
        try
        {
            if (__instance == null || _lrUpdatingRerollView || !IsPaginatedLootRerollActive()) return;
            if (_lrRerollCostTextField?.GetValue(__instance) == null)
            {
                EnsureLootRerollButtonReady(__instance);
            }
        }
        catch { }
    }

    private static void EnsureLootRerollButtonReady(object rerollBtn)
    {
        if (rerollBtn == null || _lrUpdatingRerollView) return;
        _lrUpdatingRerollView = true;
        try
        {
            _lrRerollIsEnabledProp?.SetValue(rerollBtn, true);
            if (_lrRerollCostTextField?.GetValue(rerollBtn) == null && _lrRerollPrepareCostTextMethod != null)
            {
                var taskObj = _lrRerollPrepareCostTextMethod.Invoke(rerollBtn, null);
                if (taskObj is System.Threading.Tasks.Task t && !t.IsCompleted)
                {
                    _ = t.ContinueWith(_ => Core.MainThreadDispatcher.Enqueue(() =>
                    {
                        try
                        {
                            if (rerollBtn is UnityEngine.Object uo && uo == null) return;
                            _lrUpdatingRerollView = true;
                            try { _lrRerollUpdateViewMethod?.Invoke(rerollBtn, null); }
                            finally { _lrUpdatingRerollView = false; }
                        }
                        catch { }
                    }), System.Threading.Tasks.TaskScheduler.Default);
                    return;
                }
            }
            _lrRerollUpdateViewMethod?.Invoke(rerollBtn, null);
        }
        catch { }
        finally
        {
            _lrUpdatingRerollView = false;
        }
    }
}
