using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
using BazaarLauncher.Core;

namespace BazaarLauncher;

internal static partial class CompatAuditPatch
{
    // ── 12. EncounterController 子事件 / EncounterStep 立绘与 Tooltip 补全 (#19) ──

    private static void PatchEncounterController(SafeHarmonyPatcher patcher)
    {
        try
        {
            _encounterControllerType = GameInspector.FindTypeSafe("EncounterController", "TheBazaar.EncounterController");
            _encounterAssetDataSoType = GameInspector.FindTypeSafe(
                "EncounterAssetDataSO",
                "TheBazaar.Assets.Scripts.ScriptableObjectsScripts.EncounterAssetDataSO");
            var cardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.Card");

            if (_encounterControllerType != null)
            {
                const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _encCurrentEncounterDataObjectProp = _encounterControllerType.GetProperty("CurrentEncounterDataObject", allInst);
                _encCardDataProp = FindPropertyInHierarchy(_encounterControllerType, "CardData");
                _encIsCardVisibleProp = FindPropertyInHierarchy(_encounterControllerType, "IsCardVisible");
                _encIsLockedToSocketProp = FindPropertyInHierarchy(_encounterControllerType, "IsLockedToSocket");
                _encBoxColliderProp = FindPropertyInHierarchy(_encounterControllerType, "BoxCollider");
                _encInitTooltipMethod = FindMethodInHierarchy(_encounterControllerType, "InitializeCardTooltipData", Type.EmptyTypes);
                _encCardTooltipDataField = FindFieldInHierarchy(_encounterControllerType, "_cardTooltipData");
                _encIsInitializedField = FindFieldInHierarchy(_encounterControllerType, "_isInitialized");
                _encCardVisualsField = FindFieldInHierarchy(_encounterControllerType, "cardVisuals");
                _encShowCardMethod = AccessTools.Method(_encounterControllerType, "ShowCard", new[] { typeof(bool) });

                if (_encounterAssetDataSoType != null)
                {
                    _encSetupEncounterControllerMethod = AccessTools.Method(
                        _encounterControllerType,
                        "SetupEncounterController",
                        new[] { _encounterAssetDataSoType });
                }
                _encSetupEncounterControllerMethod ??= AccessTools.Method(_encounterControllerType, "SetupEncounterController");

                var setupMethod = cardType != null
                    ? AccessTools.Method(_encounterControllerType, "Setup", new[] { cardType })
                    : AccessTools.Method(_encounterControllerType, "Setup");
                if (setupMethod != null)
                {
                    patcher.TryPatchMethod(
                        setupMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(EncounterSetupPrefix)),
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(EncounterSetupPostfix)),
                        optional: true,
                        label: "Compat.Encounter.EncounterController.Setup");
                }

                var loadEncMethod = cardType != null
                    ? AccessTools.Method(_encounterControllerType, "LoadEncounterData", new[] { cardType, typeof(ITCard) })
                    : AccessTools.Method(_encounterControllerType, "LoadEncounterData");
                if (loadEncMethod != null && _encounterAssetDataSoType != null)
                {
                    var expectedRet = typeof(Task<>).MakeGenericType(_encounterAssetDataSoType);
                    if (loadEncMethod.ReturnType == expectedRet)
                    {
                        var genericPostfix = typeof(CompatAuditPatch)
                            .GetMethod(nameof(LoadEncounterDataPostfixGeneric), BindingFlags.NonPublic | BindingFlags.Static)
                            ?.MakeGenericMethod(_encounterAssetDataSoType);
                        if (genericPostfix != null)
                        {
                            patcher.TryPatchMethod(
                                loadEncMethod,
                                postfix: genericPostfix,
                                optional: true,
                                label: "Compat.Encounter.EncounterController.LoadEncounterData");
                        }
                    }
                }

                if (_encSetupEncounterControllerMethod != null)
                {
                    patcher.TryPatchMethod(
                        _encSetupEncounterControllerMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(SetupEncounterControllerPrefix)),
                        optional: true,
                        label: "Compat.Encounter.EncounterController.SetupEncounterController");
                }
            }

            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                _bmEncounterAssetSoField = bmType.GetField("_encounterAssetDataSO", BindingFlags.NonPublic | BindingFlags.Instance);
                _bmUpdateEncounterVisMethod = AccessTools.Method(bmType, "UpdateEncounterVisualization", Type.EmptyTypes);
            }

            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
            if (dataType != null)
            {
                _encDataGetStaticMethod = dataType.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

                foreach (var resetName in new[] { "ResetStatics", "ResetEncounter" })
                {
                    var rm = AccessTools.Method(dataType, resetName, Type.EmptyTypes);
                    if (rm != null)
                    {
                        patcher.TryPatchMethod(
                            rm,
                            postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(DataResetEncounterCachePostfix)),
                            optional: true,
                            label: $"Compat.Encounter.Data.{resetName}");
                    }
                }
            }

            var tipDataType = GameInspector.FindTypeSafe("TheBazaar.Tooltips.CardTooltipData");
            if (tipDataType != null)
            {
                var getTitleMethod = AccessTools.Method(tipDataType, "GetTitle", Type.EmptyTypes);
                if (getTitleMethod != null)
                {
                    patcher.TryPatchMethod(
                        getTitleMethod,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(CardTooltipGetTitlePostfix)),
                        optional: true,
                        label: "Compat.Encounter.CardTooltipData.GetTitle");
                }
            }

            var tipHandlerType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.CardTooltipTypeHandler");
            if (tipHandlerType != null)
            {
                var renderPassiveMethod = AccessTools.Method(tipHandlerType, "RenderPassiveEffects");
                if (renderPassiveMethod != null)
                {
                    patcher.TryPatchMethod(
                        renderPassiveMethod,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(CardTooltipRenderPassiveEffectsPostfix)),
                        optional: true,
                        label: "Compat.Encounter.CardTooltipTypeHandler.RenderPassiveEffects");
                }
            }

            var tipCtrlType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.CardTooltipController");
            if (tipCtrlType != null)
            {
                _tooltipCtrlRenderPassiveMethod = AccessTools.Method(tipCtrlType, "RenderPassiveEffectTextBlock");
                _tooltipCtrlQuestDataField = FindFieldInHierarchy(tipCtrlType, "_questData");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchEncounterController: " + ex.Message);
        }
    }

    // ── 13. PrizePassChallengesView 与 EndOfRunChallengesController 空安全与延迟回调守卫 (P1-48) ──

    private static FieldInfo _ppvCachedDailyField;
    private static FieldInfo _ppvCachedWeeklyField;
    private static PropertyInfo _bpcIdProp;

    private static readonly object _eorChallengesLock = new();
    private static readonly HashSet<int> _activeEorChallengeCtrlIds = new();
    private static FieldInfo _eorChallengesViewField;
    private static FieldInfo _eorTierDialField;
    private static FieldInfo _eorPrizePassFxField;
    private static FieldInfo _eorSoundHandlerField;
    private static FieldInfo _eorCachedTierDataField;
    private static FieldInfo _clientCacheSeasonTrackField;
    private static FieldInfo _clientCachePrevSeasonTrackField;
    private static FieldInfo _clientCacheSeasonProgField;
    private static FieldInfo _clientCachePrevSeasonProgField;
    private static MethodInfo _doTweenKillMethod;

    internal static void ClearEndOfRunChallengeGuards()
    {
        lock (_eorChallengesLock)
        {
            _activeEorChallengeCtrlIds.Clear();
        }
    }

    private static void PatchPrizePassChallengesGuard(SafeHarmonyPatcher patcher)
    {
        try
        {
            var viewType = GameInspector.FindTypeSafe("TheBazaar.UI.PrizePassChallengesView");
            if (viewType == null) return;

            _ppvCachedDailyField = viewType.GetField("_cachedDailyChallengeProgress", BindingFlags.NonPublic | BindingFlags.Instance);
            _ppvCachedWeeklyField = viewType.GetField("_cachedWeeklyChallengeProgress", BindingFlags.NonPublic | BindingFlags.Instance);

            var bpcType = GameInspector.FindTypeSafe("TheBazaar.UI.BattlePassChallenge");
            _bpcIdProp = bpcType?.GetProperty("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            var sanitizePrefix = AccessTools.Method(typeof(CompatAuditPatch), nameof(PrizePassChallengesSanitizePrefix));
            var fin = AccessTools.Method(typeof(CompatAuditPatch), nameof(SwallowUiFinalizer));

            foreach (var name in new[] { "Awake", "RefreshChallenges", "ShowChallengesProgress", "OnChallengesUpdated" })
            {
                foreach (var m in viewType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != name) continue;
                    var ps = m.GetParameters();
                    bool hasRespArg = ps.Length > 0
                        && ps[0].ParameterType == typeof(BazaarGameShared.TempoNet.Responses.PlayerChallengesResponse);
                    patcher.TryPatchMethod(
                        m,
                        prefix: hasRespArg ? sanitizePrefix : null,
                        finalizer: fin,
                        optional: true,
                        label: $"Compat.Challenges.PrizePassChallengesView.{name}");
                }
            }

            // P1-48：ShowChallengeProgress 在空数组或缺失进度项时补齐默认 ChallengeProgress，避免首个空项抛 NRE 提前中止循环
            var showOne = viewType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "ShowChallengeProgress") return false;
                    var ps = m.GetParameters();
                    return ps.Length >= 3
                        && ps[1].ParameterType == typeof(BazaarGameShared.TempoNet.Responses.ChallengeProgress[])
                        && ps[2].ParameterType == typeof(BazaarGameShared.TempoNet.Responses.ChallengeProgress[]);
                });
            if (showOne != null)
            {
                patcher.TryPatchMethod(
                    showOne,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PrizePassShowChallengeProgressPrefix)),
                    finalizer: fin,
                    optional: true,
                    label: "Compat.Challenges.PrizePassChallengesView.ShowChallengeProgress");
            }

            var getProg = AccessTools.Method(
                viewType,
                "GetProgressFromId",
                new[] { typeof(BazaarGameShared.TempoNet.Responses.ChallengeProgress[]), typeof(Guid) });
            if (getProg != null)
            {
                patcher.TryPatchMethod(
                    getProg,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(PrizePassGetProgressFromIdPrefix)),
                    optional: true,
                    label: "Compat.Challenges.PrizePassChallengesView.GetProgressFromId");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchPrizePassChallengesGuard: " + ex.Message);
        }
    }

    private static bool PrizePassChallengesSanitizePrefix(object __instance, ref BazaarGameShared.TempoNet.Responses.PlayerChallengesResponse __0)
    {
        try
        {
            if (__instance != null && IsUnityObjectAlive(__instance))
            {
                if (_ppvCachedDailyField != null && _ppvCachedDailyField.GetValue(__instance) == null)
                {
                    _ppvCachedDailyField.SetValue(__instance, Array.Empty<BazaarGameShared.TempoNet.Responses.ChallengeProgress>());
                }
                if (_ppvCachedWeeklyField != null && _ppvCachedWeeklyField.GetValue(__instance) == null)
                {
                    _ppvCachedWeeklyField.SetValue(__instance, Array.Empty<BazaarGameShared.TempoNet.Responses.ChallengeProgress>());
                }
            }

            if (__0.dailyChallenges == null || __0.weeklyChallenges == null)
            {
                __0 = __0 with
                {
                    dailyChallenges = __0.dailyChallenges ?? Array.Empty<BazaarGameShared.TempoNet.Responses.ChallengeProgress>(),
                    weeklyChallenges = __0.weeklyChallenges ?? Array.Empty<BazaarGameShared.TempoNet.Responses.ChallengeProgress>(),
                };
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PrizePassShowChallengeProgressPrefix(
        object challengeInfo,
        ref BazaarGameShared.TempoNet.Responses.ChallengeProgress[] cachedChallengeProgress,
        ref BazaarGameShared.TempoNet.Responses.ChallengeProgress[] currentProgress)
    {
        try
        {
            if (!IsUnityObjectAlive(challengeInfo)) return false;

            cachedChallengeProgress ??= Array.Empty<BazaarGameShared.TempoNet.Responses.ChallengeProgress>();
            currentProgress ??= Array.Empty<BazaarGameShared.TempoNet.Responses.ChallengeProgress>();

            var idProp = _bpcIdProp ?? challengeInfo.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (idProp?.GetValue(challengeInfo) is not Guid challengeId)
                return false;

            bool found = false;
            for (int i = 0; i < currentProgress.Length; i++)
            {
                var p = currentProgress[i];
                if (p != null && p.Id == challengeId)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                // 缺失对应进度项时自动补齐默认 0 进度结构，避免 progressFromId.Progress 抛 NRE 早退
                var expanded = new BazaarGameShared.TempoNet.Responses.ChallengeProgress[currentProgress.Length + 1];
                if (currentProgress.Length > 0)
                {
                    Array.Copy(currentProgress, expanded, currentProgress.Length);
                }
                expanded[currentProgress.Length] = new BazaarGameShared.TempoNet.Responses.ChallengeProgress
                {
                    Id = challengeId,
                    Progress = 0,
                    Acknowledged = false,
                };
                currentProgress = expanded;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PrizePassGetProgressFromIdPrefix(
        BazaarGameShared.TempoNet.Responses.ChallengeProgress[] challengesProgress,
        ref BazaarGameShared.TempoNet.Responses.ChallengeProgress __result)
    {
        if (challengesProgress == null)
        {
            __result = null;
            return false;
        }
        return true;
    }

    private static void PatchEndOfRunChallengesGuard(SafeHarmonyPatcher patcher)
    {
        try
        {
            var eorType = GameInspector.FindTypeSafe("TheBazaar.EndOfRunChallengesController");
            if (eorType == null) return;

            const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _eorChallengesViewField = eorType.GetField("PrizePassChallengesView", anyInst);
            _eorTierDialField = eorType.GetField("PrizePassTierDial", anyInst);
            _eorPrizePassFxField = eorType.GetField("prizePassFX", anyInst);
            _eorSoundHandlerField = eorType.GetField("soundEndOfRunHandler", anyInst);
            _eorCachedTierDataField = eorType.GetField("_cachedTierData", anyInst);

            var cacheType = GameInspector.FindTypeSafe("TheBazaar.ClientCache");
            if (cacheType != null)
            {
                const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                _clientCacheSeasonTrackField = cacheType.GetField("SeasonTrack", anyStatic);
                _clientCachePrevSeasonTrackField = cacheType.GetField("PreviousSeasonTrack", anyStatic);
                _clientCacheSeasonProgField = cacheType.GetField("SeasonTrackProgression", anyStatic);
                _clientCachePrevSeasonProgField = cacheType.GetField("PreviousSeasonTrackProgression", anyStatic);
            }

            var doTweenType = GameInspector.FindTypeSafe("DG.Tweening.DOTween");
            _doTweenKillMethod = doTweenType?.GetMethod("Kill", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object), typeof(bool) }, null);

            var swallowFin = AccessTools.Method(typeof(CompatAuditPatch), nameof(SwallowUiFinalizer));

            var mOnLoad = AccessTools.Method(eorType, "OnLoadUpdateChallengeData", Type.EmptyTypes);
            if (mOnLoad != null)
            {
                patcher.TryPatchMethod(
                    mOnLoad,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(EndOfRunChallengesOnLoadPrefix)),
                    finalizer: swallowFin,
                    optional: true,
                    label: "Compat.Challenges.EndOfRunChallengesController.OnLoadUpdateChallengeData");
            }

            var mOnDisable = AccessTools.Method(eorType, "OnDisable", Type.EmptyTypes);
            if (mOnDisable != null)
            {
                patcher.TryPatchMethod(
                    mOnDisable,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(EndOfRunChallengesOnDisablePrefix)),
                    finalizer: swallowFin,
                    optional: true,
                    label: "Compat.Challenges.EndOfRunChallengesController.OnDisable");
            }

            var mGetTier = AccessTools.Method(eorType, "GetTierData", new[] { typeof(int), typeof(int), typeof(bool) });
            if (mGetTier != null)
            {
                patcher.TryPatchMethod(
                    mGetTier,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(EndOfRunChallengesGetTierDataPrefix)),
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(EndOfRunChallengesObjectResultFinalizer)),
                    optional: true,
                    label: "Compat.Challenges.EndOfRunChallengesController.GetTierData");
            }

            var mOnXpClaimed = AccessTools.Method(eorType, "OnChallengeXPClaimed", new[] { typeof(int) });
            if (mOnXpClaimed != null)
            {
                patcher.TryPatchMethod(
                    mOnXpClaimed,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(EndOfRunChallengesOnXpClaimedPrefix)),
                    finalizer: swallowFin,
                    optional: true,
                    label: "Compat.Challenges.EndOfRunChallengesController.OnChallengeXPClaimed");
            }

            var mExit = AccessTools.Method(eorType, "ExitChallenges", Type.EmptyTypes);
            if (mExit != null)
            {
                patcher.TryPatchMethod(
                    mExit,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(EndOfRunChallengesExitPrefix)),
                    finalizer: swallowFin,
                    optional: true,
                    label: "Compat.Challenges.EndOfRunChallengesController.ExitChallenges");
            }

            var mTryClaimAll = AccessTools.Method(eorType, "TryClaimAllChallenges", Type.EmptyTypes);
            if (mTryClaimAll != null)
            {
                patcher.TryPatchMethod(
                    mTryClaimAll,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(EndOfRunChallengesTryClaimAllPrefix)),
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(EndOfRunChallengesFloatResultFinalizer)),
                    optional: true,
                    label: "Compat.Challenges.EndOfRunChallengesController.TryClaimAllChallenges");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchEndOfRunChallengesGuard: " + ex.Message);
        }
    }

    private static bool IsEndOfRunChallengeControllerActive(object controller)
    {
        if (controller is not Behaviour bhv || bhv == null || !bhv.isActiveAndEnabled || !bhv.gameObject.activeInHierarchy)
            return false;
        int id = bhv.GetInstanceID();
        lock (_eorChallengesLock)
        {
            return _activeEorChallengeCtrlIds.Contains(id);
        }
    }

    private static bool HasObservableNonNullValue(FieldInfo staticCacheField)
    {
        var cacheObj = staticCacheField?.GetValue(null);
        if (cacheObj == null) return false;
        var valProp = cacheObj.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        return valProp != null && valProp.GetValue(cacheObj) != null;
    }

    private static bool EndOfRunChallengesOnLoadPrefix(object __instance)
    {
        try
        {
            if (__instance is not Behaviour bhv || bhv == null || !bhv.isActiveAndEnabled)
                return false;

            var viewObj = _eorChallengesViewField?.GetValue(__instance);
            var dialObj = _eorTierDialField?.GetValue(__instance);
            var fxObj = _eorPrizePassFxField?.GetValue(__instance);
            var soundObj = _eorSoundHandlerField?.GetValue(__instance);
            if (!IsUnityObjectAlive(viewObj) || !IsUnityObjectAlive(dialObj)
                || !IsUnityObjectAlive(fxObj) || !IsUnityObjectAlive(soundObj))
                return false;

            if (!HasObservableNonNullValue(_clientCachePrevSeasonProgField)
                || !HasObservableNonNullValue(_clientCacheSeasonProgField))
                return false;

            int id = bhv.GetInstanceID();
            lock (_eorChallengesLock)
            {
                _activeEorChallengeCtrlIds.Add(id);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EndOfRunChallengesOnDisablePrefix(object __instance)
    {
        try
        {
            if (__instance is Component comp && comp != null)
            {
                int id = comp.GetInstanceID();
                lock (_eorChallengesLock)
                {
                    _activeEorChallengeCtrlIds.Remove(id);
                }
            }

            if (__instance != null && _doTweenKillMethod != null)
            {
                _doTweenKillMethod.Invoke(null, new[] { __instance, (object)false });
                // 同步清理 PrizePassTierDial 及其 transform 上仍在运行的转盘补间，避免离页后收尾回调触碰已销毁 prizePassFX
                var dialObj = _eorTierDialField?.GetValue(__instance);
                if (dialObj != null)
                {
                    _doTweenKillMethod.Invoke(null, new[] { dialObj, (object)false });
                    if (dialObj is Component dialComp && dialComp != null && dialComp.transform != null)
                    {
                        _doTweenKillMethod.Invoke(null, new[] { (object)dialComp.transform, (object)false });
                    }
                }
            }
        }
        catch { }
    }

    private static bool EndOfRunChallengesGetTierDataPrefix(int experience, int challengeXP, bool usePreviousSeasonTrack, ref object __result)
    {
        try
        {
            var trackCacheField = usePreviousSeasonTrack ? _clientCachePrevSeasonTrackField : _clientCacheSeasonTrackField;
            var cacheObj = trackCacheField?.GetValue(null);
            if (cacheObj == null)
            {
                __result = null;
                return false;
            }

            var hasDataProp = cacheObj.GetType().GetProperty("HasData", BindingFlags.Public | BindingFlags.Instance);
            if (hasDataProp != null && hasDataProp.GetValue(cacheObj) is bool hasData && !hasData)
            {
                __result = null;
                return false;
            }

            var valProp = cacheObj.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (valProp?.GetValue(cacheObj) is not BazaarGameShared.TempoNet.Responses.SeasonTrackResponse track)
            {
                __result = null;
                return false;
            }

            var tiers = track.tiers;
            if (tiers == null || tiers.Length == 0)
            {
                __result = null;
                return false;
            }

            int totalXp = experience + challengeXP;
            int maxTierXp = tiers[tiers.Length - 1].requiredXp;
            if (totalXp >= maxTierXp)
            {
                // 防止 infiniteTier 为空或 requiredXp <= 0 触发 InvalidOperationException / while 死循环
                if (!track.infiniteTier.HasValue || track.infiniteTier.Value.requiredXp <= 0)
                {
                    __result = null;
                    return false;
                }
            }
            return true;
        }
        catch
        {
            __result = null;
            return false;
        }
    }

    private static bool EndOfRunChallengesOnXpClaimedPrefix(object __instance)
    {
        try
        {
            // 拦截离开结算页 1 秒后才触发的匿名 DOVirtual.DelayedCall 回调，避免在大厅爆销毁对象 NRE
            if (!IsEndOfRunChallengeControllerActive(__instance))
                return false;

            var dialObj = _eorTierDialField?.GetValue(__instance);
            if (!IsUnityObjectAlive(dialObj))
                return false;

            var cachedTier = _eorCachedTierDataField?.GetValue(__instance);
            if (cachedTier == null)
                return false;

            int tierXpReq = GameInspector.GetMember(cachedTier, "tierXPRequired") is int req ? req : 0;
            var tierResp = GameInspector.GetMember(cachedTier, "tierResponse");
            if (tierXpReq <= 0 && tierResp == null)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool EndOfRunChallengesExitPrefix(object __instance)
    {
        try
        {
            // 拦截离开结算页 3 秒后才触发的匿名 DOVirtual.DelayedCall 回调
            if (!IsEndOfRunChallengeControllerActive(__instance))
                return false;

            var soundObj = _eorSoundHandlerField?.GetValue(__instance);
            return IsUnityObjectAlive(soundObj);
        }
        catch
        {
            return false;
        }
    }

    private static bool EndOfRunChallengesTryClaimAllPrefix(object __instance, ref float __result)
    {
        try
        {
            if (__instance is not Behaviour bhv || bhv == null || !bhv.isActiveAndEnabled)
            {
                __result = 0f;
                return false;
            }
            var viewObj = _eorChallengesViewField?.GetValue(__instance);
            if (!IsUnityObjectAlive(viewObj))
            {
                __result = 0f;
                return false;
            }
            lock (_eorChallengesLock)
            {
                _activeEorChallengeCtrlIds.Add(bhv.GetInstanceID());
            }
            return true;
        }
        catch
        {
            __result = 0f;
            return false;
        }
    }

    private static Exception EndOfRunChallengesObjectResultFinalizer(Exception __exception, ref object __result)
    {
        if (__exception != null)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch[P1-48]: EndOfRunChallengesController.GetTierData guarded: " + __exception.Message);
            __result = null;
        }
        return null;
    }

    private static Exception EndOfRunChallengesFloatResultFinalizer(Exception __exception, ref float __result)
    {
        if (__exception != null)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch[P1-48]: EndOfRunChallengesController.TryClaimAllChallenges guarded: " + __exception.Message);
            __result = 0f;
        }
        return null;
    }

    private static PropertyInfo FindPropertyInHierarchy(Type type, string propName)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null) return p;
        }
        return null;
    }

    private static MethodInfo FindMethodInHierarchy(Type type, string methodName, Type[] argTypes)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, argTypes, null);
            if (m != null) return m;
        }
        return null;
    }

    private static bool IsUnityObjectAlive(object obj)
    {
        if (obj == null) return false;
        if (obj is UnityEngine.Object uo) return uo != null;
        return true;
    }

    private static void ClearParentEncounterCache(string reason)
    {
        try
        {
            lock (_encounterCacheLock)
            {
                _cachedParentEncounterTemplateId = Guid.Empty;
                _cachedParentEncounterArtKey = null;
                _cachedParentEncounterAsset = null;
                _cachedParentEncounterTitle = null;
                _cachedParentEncounterDesc = null;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch.ClearParentEncounterCache({reason}): {ex.Message}");
        }
    }

    private static bool HasValidArtKey(ITCard tpl)
        => tpl != null && !string.IsNullOrEmpty(tpl.ArtKey) && !string.Equals(tpl.ArtKey, "Invalid", StringComparison.OrdinalIgnoreCase);

    private static string ResolveLocalizedTextValue(object localizableTextObj)
    {
        if (localizableTextObj == null) return null;
        try
        {
            Type argType = localizableTextObj.GetType();
            var m = _tooltipGetLocalizedTextByArgType.GetOrAdd(argType, static t =>
            {
                var extType = GameInspector.FindTypeSafe("TheBazaar.Tooltips.TooltipExtensions");
                return extType?.GetMethod(
                    "GetLocalizedText",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { t },
                    null);
            });
            if (m != null)
            {
                var res = m.Invoke(null, new[] { localizableTextObj }) as string;
                if (!string.IsNullOrWhiteSpace(res)) return res;
            }
        }
        catch { }

        return GameInspector.GetMember(localizableTextObj, "Text") as string;
    }

    private static string ResolveTemplateTitleZh(ITCard tpl)
    {
        if (tpl == null) return null;
        try
        {
            var loc = GameInspector.GetMember(tpl, "Localization");
            var titleObj = loc != null ? GameInspector.GetMember(loc, "Title") : null;
            string t = ResolveLocalizedTextValue(titleObj);
            return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveTemplateDescriptionZh(ITCard tpl)
    {
        if (tpl == null) return null;
        try
        {
            var loc = GameInspector.GetMember(tpl, "Localization");
            if (loc == null) return null;

            var descObj = GameInspector.GetMember(loc, "Description");
            string d = ResolveLocalizedTextValue(descObj);
            if (!string.IsNullOrWhiteSpace(d)) return d.Trim();

            if (GameInspector.GetMember(loc, "Tooltips") is IEnumerable tips)
            {
                foreach (var tip in tips)
                {
                    if (tip == null) continue;
                    var contentObj = GameInspector.GetMember(tip, "Content");
                    string tipText = ResolveLocalizedTextValue(contentObj);
                    if (!string.IsNullOrWhiteSpace(tipText)) return tipText.Trim();
                }
            }

            var flavorObj = GameInspector.GetMember(loc, "FlavorText");
            string flavor = ResolveLocalizedTextValue(flavorObj);
            if (!string.IsNullOrWhiteSpace(flavor)) return flavor.Trim();
        }
        catch { }
        return null;
    }

    private static ITCard ResolveCardTemplateById(Guid templateId)
    {
        if (templateId == Guid.Empty) return null;
        try
        {
            if (_encDataGetStaticMethod == null)
            {
                var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
                _encDataGetStaticMethod = dataType?.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            }
            var staticMgr = _encDataGetStaticMethod?.Invoke(null, null);
            if (staticMgr == null) return null;

            _encStaticGetCardByIdMethod ??= staticMgr.GetType().GetMethod(
                "GetCardById",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Guid) },
                null);
            return _encStaticGetCardByIdMethod?.Invoke(staticMgr, new object[] { templateId }) as ITCard;
        }
        catch
        {
            return null;
        }
    }

    private static void CacheParentEncounterIfRoot(object cardData, ITCard tpl, object encounterAsset)
    {
        if (cardData == null || !IsUnityObjectAlive(encounterAsset)) return;
        try
        {
            if (GameInspector.GetMember(cardData, "Type") is not ECardType cardType)
                return;

            // 仅缓存真正的事件/强化基座根遭遇，绝不把战斗或 PvP 头像误缓存为事件兜底图
            if (cardType != ECardType.EventEncounter && cardType != ECardType.PedestalEncounter)
                return;

            Guid tplId = GameInspector.GetMember(cardData, "TemplateId") is Guid g ? g : Guid.Empty;
            tpl ??= GameInspector.GetMember(cardData, "Template") as ITCard ?? ResolveCardTemplateById(tplId);

            bool ownArtValid = HasValidArtKey(tpl);
            lock (_encounterCacheLock)
            {
                // 若当前只是沿用旧父图的无 ArtKey 子事件，切勿覆盖原有的根父事件模板与描述
                if (!ownArtValid && IsUnityObjectAlive(_cachedParentEncounterAsset))
                    return;

                if (tplId != Guid.Empty) _cachedParentEncounterTemplateId = tplId;
                if (ownArtValid) _cachedParentEncounterArtKey = tpl.ArtKey;
                _cachedParentEncounterAsset = encounterAsset;

                string title = ResolveTemplateTitleZh(tpl);
                if (!string.IsNullOrWhiteSpace(title)) _cachedParentEncounterTitle = title;

                string desc = ResolveTemplateDescriptionZh(tpl);
                if (!string.IsNullOrWhiteSpace(desc)) _cachedParentEncounterDesc = desc;
            }
        }
        catch { }
    }

    private static void DataResetEncounterCachePostfix()
    {
        try
        {
            ClearParentEncounterCache("Data.Reset");
        }
        catch { }
    }

    private static bool EncounterSetupPrefix(object __instance, object cardData, ref Task __result)
    {
        try
        {
            if (!IsUnityObjectAlive(__instance) || cardData == null)
            {
                __result = Task.CompletedTask;
                return false;
            }

            if (GameInspector.GetMember(cardData, "Type") is ECardType cardType
                && (cardType == ECardType.PvpEncounter || cardType == ECardType.CombatEncounter))
            {
                ClearParentEncounterCache("EncounterSetup:" + cardType);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.EncounterSetupPrefix: " + ex.Message);
        }
        return true;
    }

    private static void EncounterSetupPostfix(object __instance, object cardData, ref Task __result)
    {
        try
        {
            if (!IsUnityObjectAlive(__instance) || cardData == null || __result == null) return;
            __result = WrapEncounterSetupTask(__result, __instance, cardData);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.EncounterSetupPostfix: " + ex.Message);
        }
    }

    private static bool SetupEncounterControllerPrefix(object __instance, object encounterData, ref Task __result)
    {
        try
        {
            if (!IsUnityObjectAlive(__instance) || encounterData == null)
            {
                __result = Task.CompletedTask;
                return false;
            }

            var cardData = _encCardDataProp?.GetValue(__instance);
            if (cardData != null)
            {
                if (GameInspector.GetMember(cardData, "Type") is ECardType cardType
                    && cardType == ECardType.PvpEncounter)
                {
                    ClearParentEncounterCache("SetupEncounterController:Pvp");
                    return true;
                }
                CacheParentEncounterIfRoot(cardData, null, encounterData);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.SetupEncounterControllerPrefix: " + ex.Message);
        }
        return true;
    }

    private static void LoadEncounterDataPostfixGeneric<TAsset>(
        object cardData,
        object cardTemplate,
        ref Task<TAsset> __result) where TAsset : class
    {
        try
        {
            if (__result == null) return;
            __result = WrapLoadEncounterDataTaskCore(__result, cardData, cardTemplate);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.LoadEncounterDataPostfix: " + ex.Message);
        }
    }

    private static async Task<TAsset> WrapLoadEncounterDataTaskCore<TAsset>(
        Task<TAsset> innerTask,
        object cardData,
        object cardTemplate) where TAsset : class
    {
        TAsset loaded = null;
        try
        {
            if (innerTask != null)
            {
                loaded = await innerTask;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapLoadEncounterData inner: " + ex.Message);
        }

        if (IsUnityObjectAlive(loaded))
        {
            CacheParentEncounterIfRoot(cardData, cardTemplate as ITCard, loaded);
            return loaded;
        }

        try
        {
            if (cardData == null || GameInspector.GetMember(cardData, "Type") is not ECardType cardType)
                return null;

            if (cardType != ECardType.EventEncounter
                && cardType != ECardType.PedestalEncounter
                && cardType != ECardType.EncounterStep)
            {
                return null;
            }

            var fallback = await ResolveFallbackEncounterAssetAsync(cardData, cardTemplate as ITCard);
            if (fallback is TAsset typedFallback && IsUnityObjectAlive(typedFallback))
            {
                RecordCompatHackHit("H-6", ref _encounterPortraitFallbackCount, "LoadEncounterDataFallback");
                return typedFallback;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapLoadEncounterData fallback: " + ex.Message);
        }
        return null;
    }

    private static async Task WrapEncounterSetupTask(Task innerTask, object controller, object cardData)
    {
        try
        {
            if (innerTask != null)
            {
                await innerTask;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapEncounterSetupTask inner: " + ex.Message);
        }

        try
        {
            if (!IsUnityObjectAlive(controller) || cardData == null)
                return;

            // 对象池可能已复用该 EncounterController，若 CardData 已变更则立即退出
            var activeCardData = _encCardDataProp?.GetValue(controller);
            if (!ReferenceEquals(activeCardData, cardData))
                return;

            // 1. 兜底确保非 PvP 遭遇与 EncounterStep 已初始化 Tooltip 数据
            var cardTypeObj = GameInspector.GetMember(cardData, "Type");
            if (cardTypeObj is not ECardType cardType)
                return;

            if (cardType != ECardType.PvpEncounter)
            {
                var tipData = _encCardTooltipDataField?.GetValue(controller);
                if (tipData == null)
                {
                    _encInitTooltipMethod?.Invoke(controller, null);
                    tipData = _encCardTooltipDataField?.GetValue(controller);
                    if (tipData != null && _encIsInitializedField != null)
                    {
                        _encIsInitializedField.SetValue(controller, true);
                    }
                    if (tipData != null)
                    {
                        RecordCompatHackHit("H-6", ref _encounterTooltipFallbackCount, "EncounterTooltipFallback");
                    }
                }
            }

            if (cardType == ECardType.PvpEncounter || cardType == ECardType.CombatEncounter)
                return;

            // 2. 若原生 Setup 因 EncounterStep 跳过加载或子事件无 ArtKey 导致未加载立绘，则自动补全父级 EncounterAssetDataSO
            var currentAsset = _encCurrentEncounterDataObjectProp?.GetValue(controller);
            if (IsUnityObjectAlive(currentAsset))
            {
                CacheParentEncounterIfRoot(cardData, null, currentAsset);
                return;
            }

            Guid tplId = GameInspector.GetMember(cardData, "TemplateId") is Guid g ? g : Guid.Empty;
            var tpl = GameInspector.GetMember(cardData, "Template") as ITCard ?? ResolveCardTemplateById(tplId);
            var fallbackAsset = await ResolveFallbackEncounterAssetAsync(cardData, tpl);
            if (!IsUnityObjectAlive(fallbackAsset))
                return;

            if (!IsUnityObjectAlive(controller))
                return;
            activeCardData = _encCardDataProp?.GetValue(controller);
            if (!ReferenceEquals(activeCardData, cardData))
                return;
            if (IsUnityObjectAlive(_encCurrentEncounterDataObjectProp?.GetValue(controller)))
                return;

            if (_encSetupEncounterControllerMethod != null)
            {
                var setupTask = _encSetupEncounterControllerMethod.Invoke(controller, new[] { fallbackAsset }) as Task;
                if (setupTask != null)
                {
                    await setupTask;
                }
                RecordCompatHackHit("H-6", ref _encounterPortraitFallbackCount, "LoadEncounterDataFallback");
            }

            if (!IsUnityObjectAlive(controller))
                return;

            // 3. 确保顶部中央肖像框可见且允许悬停触发 Tooltip
            bool lockedToSocket = _encIsLockedToSocketProp?.GetValue(controller) is bool l && l;
            bool isStorageOpen = GetStaticDataMember("IsStorageOpen") is bool st && st;
            if (lockedToSocket && !isStorageOpen)
            {
                bool isVisible = _encIsCardVisibleProp?.GetValue(controller) is bool vis && vis;
                var visuals = _encCardVisualsField?.GetValue(controller);
                if (!isVisible && IsUnityObjectAlive(visuals))
                {
                    _encShowCardMethod?.Invoke(controller, new object[] { true });
                }
                var boxObj = _encBoxColliderProp?.GetValue(controller);
                if (IsUnityObjectAlive(boxObj))
                {
                    if (_encColliderEnabledProp == null)
                    {
                        var colliderBaseType = GameInspector.FindTypeSafe("UnityEngine.Collider");
                        _encColliderEnabledProp = colliderBaseType?.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance)
                            ?? boxObj.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
                    }
                    if (_encColliderEnabledProp != null
                        && (_encColliderEnabledProp.DeclaringType == null || _encColliderEnabledProp.DeclaringType.IsInstanceOfType(boxObj))
                        && _encColliderEnabledProp.GetValue(boxObj) is bool colEnabled && !colEnabled)
                    {
                        _encColliderEnabledProp.SetValue(boxObj, true);
                    }
                }

                var curEncCtrl = GetStaticDataMember("CurrentEncounterController");
                if (ReferenceEquals(curEncCtrl, controller))
                {
                    TryUpdateBoardEncounterVisualizationSafe();
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapEncounterSetupTask fallback: " + ex.Message);
        }
    }

    private static async Task<object> ResolveFallbackEncounterAssetAsync(object cardData, ITCard currentTpl)
    {
        object cachedAsset;
        Guid cachedTplId;
        string cachedArtKey;
        lock (_encounterCacheLock)
        {
            cachedAsset = _cachedParentEncounterAsset;
            cachedTplId = _cachedParentEncounterTemplateId;
            cachedArtKey = _cachedParentEncounterArtKey;
        }

        if (IsUnityObjectAlive(cachedAsset))
            return cachedAsset;

        // 1. 从当前存活的 CurrentEncounterController 读取非战斗事件资产
        var curEncCtrl = GetStaticDataMember("CurrentEncounterController");
        if (IsUnityObjectAlive(curEncCtrl))
        {
            var curCard = _encCardDataProp?.GetValue(curEncCtrl);
            if (GameInspector.GetMember(curCard, "Type") is ECardType curType
                && (curType == ECardType.EventEncounter || curType == ECardType.PedestalEncounter))
            {
                var curSo = _encCurrentEncounterDataObjectProp?.GetValue(curEncCtrl);
                if (IsUnityObjectAlive(curSo))
                {
                    CacheParentEncounterIfRoot(curCard, null, curSo);
                    return curSo;
                }
            }
        }

        // 2. 从 BoardManager._encounterAssetDataSO 读取已预载的根遭遇资产
        var bm = GameInspector.GetBoardManagerCached();
        if (IsUnityObjectAlive(bm) && _bmEncounterAssetSoField != null)
        {
            var bmSo = _bmEncounterAssetSoField.GetValue(bm);
            if (IsUnityObjectAlive(bmSo))
            {
                lock (_encounterCacheLock)
                {
                    _cachedParentEncounterAsset = bmSo;
                }
                return bmSo;
            }
        }

        // 3. 根据缓存的父事件 ArtKey / TemplateId 重新通过 AssetLoader 异步加载
        string targetArtKey = cachedArtKey;
        if (string.IsNullOrEmpty(targetArtKey) && cachedTplId != Guid.Empty)
        {
            var parentTpl = ResolveCardTemplateById(cachedTplId);
            if (HasValidArtKey(parentTpl))
            {
                targetArtKey = parentTpl.ArtKey;
            }
        }

        // 4. 若断线重连直接进入子事件而尚未缓存父图，尝试从 Data.Entities 中定位当前在场的根事件卡（非自身 EncounterStep）
        if (string.IsNullOrEmpty(targetArtKey))
        {
            var entities = GameInspector.GetDataEntities();
            if (entities != null)
            {
                foreach (DictionaryEntry kv in entities)
                {
                    var ent = kv.Value;
                    if (ent == null || ReferenceEquals(ent, cardData)) continue;
                    if (GameInspector.GetMember(ent, "Type") is not ECardType entType) continue;
                    if (entType != ECardType.EventEncounter && entType != ECardType.PedestalEncounter) continue;
                    if (GameInspector.GetMember(ent, "Owner") != null) continue;

                    Guid entTplId = GameInspector.GetMember(ent, "TemplateId") is Guid eg ? eg : Guid.Empty;
                    var entTpl = GameInspector.GetMember(ent, "Template") as ITCard ?? ResolveCardTemplateById(entTplId);
                    if (HasValidArtKey(entTpl))
                    {
                        targetArtKey = entTpl.ArtKey;
                        break;
                    }
                }
            }
        }

        // 5. 若当前模板本身是 EventEncounter/PedestalEncounter 且含有效 ArtKey（排除 EncounterStep 的 Reward .png 贴图），尝试直接加载
        if (string.IsNullOrEmpty(targetArtKey)
            && cardData != null
            && GameInspector.GetMember(cardData, "Type") is ECardType selfType
            && (selfType == ECardType.EventEncounter || selfType == ECardType.PedestalEncounter)
            && HasValidArtKey(currentTpl))
        {
            targetArtKey = currentTpl.ArtKey;
        }

        if (string.IsNullOrEmpty(targetArtKey) || string.Equals(targetArtKey, "Invalid", StringComparison.OrdinalIgnoreCase))
            return null;

        var loaded = await LoadEncounterAssetByAddressAsync(targetArtKey);
        if (IsUnityObjectAlive(loaded))
        {
            lock (_encounterCacheLock)
            {
                _cachedParentEncounterArtKey = targetArtKey;
                _cachedParentEncounterAsset = loaded;
            }
            return loaded;
        }
        return null;
    }

    private static async Task<object> LoadEncounterAssetByAddressAsync(string artKey)
    {
        if (string.IsNullOrEmpty(artKey) || _encounterAssetDataSoType == null)
            return null;

        try
        {
            if (_encServicesGetAssetLoaderMethod == null)
            {
                var servicesType = GameInspector.FindTypeSafe("TheBazaar.AppFramework.Services");
                var assetLoaderType = GameInspector.FindTypeSafe("AssetLoader", "TheBazaar.AssetLoader");
                var getGeneric = servicesType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getGeneric != null && assetLoaderType != null)
                {
                    _encServicesGetAssetLoaderMethod = getGeneric.MakeGenericMethod(assetLoaderType);
                }
            }

            var assetLoader = _encServicesGetAssetLoaderMethod?.Invoke(null, null);
            if (!IsUnityObjectAlive(assetLoader))
                return null;

            if (_encAssetLoaderLoadMethod == null)
            {
                var loadDef = assetLoader.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        m.Name == "LoadAssetAsyncByAddress"
                        && m.IsGenericMethodDefinition
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(string));
                if (loadDef != null)
                {
                    _encAssetLoaderLoadMethod = loadDef.MakeGenericMethod(_encounterAssetDataSoType);
                }
            }

            if (_encAssetLoaderLoadMethod == null)
                return null;

            var taskObj = _encAssetLoaderLoadMethod.Invoke(assetLoader, new object[] { artKey }) as Task;
            if (taskObj == null)
                return null;

            await taskObj;
            var resultProp = taskObj.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            return resultProp?.GetValue(taskObj);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch.LoadEncounterAssetByAddressAsync({artKey}): {ex.Message}");
            return null;
        }
    }

    private static void TryUpdateBoardEncounterVisualizationSafe()
    {
        try
        {
            var bm = GameInspector.GetBoardManagerCached();
            if (!IsUnityObjectAlive(bm) || _bmUpdateEncounterVisMethod == null) return;
            _ = _bmUpdateEncounterVisMethod.Invoke(bm, null);
        }
        catch { }
    }

    private static void CardTooltipGetTitlePostfix(object __instance, ref string __result)
    {
        try
        {
            if (__instance == null || !string.IsNullOrWhiteSpace(__result)) return;

            var cardInst = GameInspector.GetMember(__instance, "CardInstance");
            if (cardInst == null || GameInspector.GetMember(cardInst, "Type") is not ECardType cardType)
                return;
            if (cardType != ECardType.EncounterStep
                && cardType != ECardType.EventEncounter
                && cardType != ECardType.PedestalEncounter)
            {
                return;
            }

            var tpl = GameInspector.GetMember(__instance, "CardTemplate") as ITCard;
            string title = ResolveTemplateTitleZh(tpl);
            if (string.IsNullOrWhiteSpace(title))
            {
                lock (_encounterCacheLock)
                {
                    title = _cachedParentEncounterTitle;
                }
            }
            if (string.IsNullOrWhiteSpace(title) && tpl != null && !string.IsNullOrWhiteSpace(tpl.InternalName))
            {
                title = tpl.InternalName.Trim();
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "奇遇事件";
            }

            __result = title;
            RecordCompatHackHit("H-6", ref _encounterTooltipFallbackCount, "EncounterTooltipFallback");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CardTooltipGetTitlePostfix: " + ex.Message);
        }
    }

    private static void CardTooltipRenderPassiveEffectsPostfix(object controller, object tooltipData)
    {
        try
        {
            if (!IsUnityObjectAlive(controller) || tooltipData == null || _tooltipCtrlRenderPassiveMethod == null)
                return;

            var cardInst = GameInspector.GetMember(tooltipData, "CardInstance");
            if (cardInst == null || GameInspector.GetMember(cardInst, "Type") is not ECardType cardType)
                return;

            // 仅对事件/基座/子步骤生效；战斗怪物的 Description 本身故意为空，由怪物盘面渲染器负责展示
            if (cardType != ECardType.EncounterStep
                && cardType != ECardType.EventEncounter
                && cardType != ECardType.PedestalEncounter)
            {
                return;
            }

            var tpl = GameInspector.GetMember(tooltipData, "CardTemplate") as ITCard;
            if (tpl == null) return;

            var loc = GameInspector.GetMember(tpl, "Localization");
            var descObj = loc != null ? GameInspector.GetMember(loc, "Description") : null;
            string nativeDesc = ResolveLocalizedTextValue(descObj);
            if (!string.IsNullOrWhiteSpace(nativeDesc))
                return;

            string fallbackDesc = ResolveTemplateDescriptionZh(tpl);
            if (string.IsNullOrWhiteSpace(fallbackDesc))
            {
                Guid curTplId = GameInspector.GetMember(cardInst, "TemplateId") is Guid g ? g : Guid.Empty;
                string parentTitle;
                string parentDesc;
                Guid parentTplId;
                lock (_encounterCacheLock)
                {
                    parentTitle = _cachedParentEncounterTitle;
                    parentDesc = _cachedParentEncounterDesc;
                    parentTplId = _cachedParentEncounterTemplateId;
                }

                string curTitle = ResolveTemplateTitleZh(tpl) ?? tpl.InternalName;
                if (!string.IsNullOrWhiteSpace(parentDesc) && (cardType == ECardType.EncounterStep || curTplId != parentTplId))
                {
                    fallbackDesc = (!string.IsNullOrWhiteSpace(parentTitle)
                                    && !string.IsNullOrWhiteSpace(curTitle)
                                    && !string.Equals(parentTitle, curTitle, StringComparison.Ordinal))
                        ? $"{parentTitle}：{parentDesc}"
                        : parentDesc;
                }
                else if (cardType == ECardType.EncounterStep || !HasValidArtKey(tpl))
                {
                    if (!string.IsNullOrWhiteSpace(parentTitle)
                        && !string.IsNullOrWhiteSpace(curTitle)
                        && !string.Equals(parentTitle, curTitle, StringComparison.Ordinal))
                    {
                        fallbackDesc = $"{parentTitle} · {curTitle}";
                    }
                    else if (!string.IsNullOrWhiteSpace(curTitle))
                    {
                        fallbackDesc = $"奇遇阶段：{curTitle}";
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(fallbackDesc))
                return;

            var questData = _tooltipCtrlQuestDataField?.GetValue(controller);
            _tooltipCtrlRenderPassiveMethod.Invoke(controller, new[] { fallbackDesc, questData });
            RecordCompatHackHit("H-6", ref _encounterTooltipFallbackCount, "EncounterTooltipFallback");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CardTooltipRenderPassiveEffectsPostfix: " + ex.Message);
        }
    }
}
