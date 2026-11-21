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

namespace BazaarLauncher;

internal static partial class CompatAuditPatch
{
    // ── 12. EncounterController 子事件 / EncounterStep 立绘与 Tooltip 补全 (#19) ──

    private static void PatchEncounterController(Harmony harmony)
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
                    harmony.Patch(
                        setupMethod,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(EncounterSetupPrefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(EncounterSetupPostfix))));
                    _patchedCount++;
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
                            harmony.Patch(loadEncMethod, postfix: new HarmonyMethod(genericPostfix));
                            _patchedCount++;
                        }
                    }
                }

                if (_encSetupEncounterControllerMethod != null)
                {
                    harmony.Patch(
                        _encSetupEncounterControllerMethod,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(SetupEncounterControllerPrefix))));
                    _patchedCount++;
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
                        harmony.Patch(rm, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(CompatAuditPatch), nameof(DataResetEncounterCachePostfix))));
                        _patchedCount++;
                    }
                }
            }

            var tipDataType = GameInspector.FindTypeSafe("TheBazaar.Tooltips.CardTooltipData");
            if (tipDataType != null)
            {
                var getTitleMethod = AccessTools.Method(tipDataType, "GetTitle", Type.EmptyTypes);
                if (getTitleMethod != null)
                {
                    harmony.Patch(getTitleMethod, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(CardTooltipGetTitlePostfix))));
                    _patchedCount++;
                }
            }

            var tipHandlerType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.CardTooltipTypeHandler");
            if (tipHandlerType != null)
            {
                var renderPassiveMethod = AccessTools.Method(tipHandlerType, "RenderPassiveEffects");
                if (renderPassiveMethod != null)
                {
                    harmony.Patch(renderPassiveMethod, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(CardTooltipRenderPassiveEffectsPostfix))));
                    _patchedCount++;
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

    // ── 13. PrizePassChallengesView 空安全保护（消除对局结束回大厅 NRE 与 Uploading Crash Report 卡顿）──

    private static void PatchPrizePassChallengesGuard(Harmony harmony)
    {
        try
        {
            var viewType = GameInspector.FindTypeSafe("TheBazaar.UI.PrizePassChallengesView");
            if (viewType == null) return;

            var prefix = new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(PrizePassChallengesSanitizePrefix)));
            var fin = new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(SwallowUiFinalizer)));

            foreach (var name in new[] { "Awake", "RefreshChallenges", "ShowChallengesProgress", "OnChallengesUpdated" })
            {
                foreach (var m in viewType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != name) continue;
                    var ps = m.GetParameters();
                    bool hasRespArg = ps.Length > 0
                        && ps[0].ParameterType == typeof(BazaarGameShared.TempoNet.Responses.PlayerChallengesResponse);
                    harmony.Patch(m, prefix: hasRespArg ? prefix : null, finalizer: fin);
                    _patchedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchPrizePassChallengesGuard: " + ex.Message);
        }
    }

    private static bool PrizePassChallengesSanitizePrefix(ref BazaarGameShared.TempoNet.Responses.PlayerChallengesResponse __0)
    {
        try
        {
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
            if (_tooltipGetLocalizedTextMethod == null)
            {
                var extType = GameInspector.FindTypeSafe("TheBazaar.Tooltips.TooltipExtensions");
                _tooltipGetLocalizedTextMethod = extType?.GetMethod(
                    "GetLocalizedText",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { localizableTextObj.GetType() },
                    null);
            }
            if (_tooltipGetLocalizedTextMethod != null)
            {
                var res = _tooltipGetLocalizedTextMethod.Invoke(null, new[] { localizableTextObj }) as string;
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
        ClearParentEncounterCache("Data.Reset");
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
                Interlocked.Increment(ref _encounterPortraitFallbackCount);
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
                        Interlocked.Increment(ref _encounterTooltipFallbackCount);
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
                Interlocked.Increment(ref _encounterPortraitFallbackCount);
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
                    _encColliderEnabledProp ??= boxObj.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
                    if (_encColliderEnabledProp != null && _encColliderEnabledProp.GetValue(boxObj) is bool colEnabled && !colEnabled)
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
            Interlocked.Increment(ref _encounterTooltipFallbackCount);
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
            Interlocked.Increment(ref _encounterTooltipFallbackCount);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CardTooltipRenderPassiveEffectsPostfix: " + ex.Message);
        }
    }
}
