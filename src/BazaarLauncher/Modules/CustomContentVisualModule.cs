using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Core.Types;
using BazaarLauncher.Core;
using BazaarModSdk;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BazaarLauncher.Modules;

/// <summary>
/// 自定义 PNG/JPEG 卡图 4 大渲染通路热替换引擎 + 逻辑融合隔离 + 野怪自定义悬停预览 + 全生命周期回收（实现 ICardVisualOverrideRegistry）：
///   通路 1：3D 棋盘物品与战报回放（ItemVisualsController / CardController.ClearVisualProxy / BoardManager.HideRecapView）
///   通路 2：3D 技能圆牌与技能代理（SkillIconUpdater.UpdateSkillIconMaterial 落盘拦截 / SkillController.Cleanup Prefix）
///   通路 3：2D UI 预览/图鉴/商店/野怪悬停（CardPreviewBase 4 参 SetUp 上下文穿透 / MonsterBoardTooltip.HandlePooling Prefix）
///   通路 4：遭遇卡背景与战斗立绘（EncounterController.Setup / SetupEncounterController / Cleanup Prefix + MPB 零泄漏背景）
/// </summary>
internal sealed class CustomContentVisualModule : IBuiltInPatchModule, ICardVisualOverrideRegistry
{
    internal static CustomContentVisualModule Instance { get; private set; }

    public ModManifest Manifest { get; } = new()
    {
        Id = "bazaar.visual.custom_content",
        Name = "自定义 PNG 卡图渲染拦截引擎",
        Version = "2.2.0",
        Author = "BazaarModKit",
        Description = "通过 4 大原生渲染通路无缝热替换自定义物品/技能/遭遇 PNG/JPEG 卡图，完整闭环逻辑融合隔离、野怪悬停、2D 穿透与 4 管线快照回收。",
        IsBuiltIn = true,
        DefaultEnabled = true,
        Category = "Visual",
        SupportsHotReload = true,
    };

    public SafeHarmonyPatcher Patcher { get; private set; }

    internal sealed class PreviewCardContext
    {
        public string CustomCardId { get; }
        public string TemplateId { get; }
        public string ModId { get; }

        public PreviewCardContext(string customCardId, string templateId, string modId)
        {
            CustomCardId = customCardId ?? string.Empty;
            TemplateId = templateId ?? string.Empty;
            ModId = modId ?? string.Empty;
        }
    }

    private readonly object _sync = new();
    private readonly List<CardVisualBinding> _bindings = new();
    private readonly List<(int Priority, Func<CardVisualQuery, CardVisualBinding> Resolver)> _dynamicResolvers = new();
    private IDisposable _sceneEventSub;
    private IDisposable _combatEventSub;

    // P0-26：2D 预览与野怪棋盘 CustomCardId 上下文映射（弱引用零泄漏）
    private static readonly ConditionalWeakTable<object, PreviewCardContext> PreviewContextMap = new();
    [ThreadStatic] private static PreviewCardContext _activePreviewSetupContext;
    [ThreadStatic] private static bool _inModSkillIconApply;

    // 反射缓存（零热路径反射查找）
    private static FieldInfo _itemMatInstanceField;
    private static FieldInfo _itemArtRendererField;
    private static FieldInfo _itemFrameAnchorField;
    private static FieldInfo _itemControllerField;
    private static FieldInfo _itemBackVisibleField;
    private static Type _itemVisualsControllerType;
    private static Type _cardControllerType;
    private static Type _recapItemVisualControllerType;
    private static Type _previewBaseType;
    private static Type _previewItemType;
    private static Type _previewSkillType;
    private static Type _previewEncounterType;
    private static Type _encounterControllerType;
    private static Type _skillControllerType;
    private static Type _skillProxyRendererType;
    private static MethodInfo _skillUpdateIconMethod;
    private static FieldInfo _previewItemMaterialField;
    private static FieldInfo _previewCardDataField;
    private static FieldInfo _previewClientCardField;
    private static FieldInfo _previewCardImageField;
    private static FieldInfo _previewEncounterPortraitField;
    private static FieldInfo _encounterPortraitRendererField;
    private static FieldInfo _encounterAnimatedPortraitField;
    private static FieldInfo _encounterBackgroundRendererField;
    private static FieldInfo _encounterInstancedBgMatField;
    private static PropertyInfo _encounterCardDataProp;
    private static MethodInfo _cardFrameUpdateBgMethod;
    private static Type _skillIconUpdaterType;
    private static FieldInfo _monsterRewardViewField;
    private static FieldInfo _monsterBoardViewField;
    private static FieldInfo _monsterBoardActiveCardsField;
    private static FieldInfo _monsterBoardActiveSkillsField;
    private static PropertyInfo _dataRunProp;
    private static PropertyInfo _dataEntitiesProp;
    private static MethodInfo _dataGetStaticMethod;
    private static FieldInfo _boardManagerIsRecapOpenField;
    private static PropertyInfo _boardManagerIsRecapOpenProp;

    private static readonly int BaseMapPropId = Shader.PropertyToID("_BaseMap");
    private static readonly int MainTexPropId = Shader.PropertyToID("_MainTex");

    private sealed class CardPropBag
    {
        public PropertyInfo InstanceId;
        public PropertyInfo TemplateId;
        public PropertyInfo Tier;
        public PropertyInfo Enchantment;
        public PropertyInfo Id;
        public PropertyInfo Owner;
        public PropertyInfo Size;
    }

    private static readonly ConcurrentDictionary<Type, CardPropBag> _cardPropsByType = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo> _itemControllerCardDataPropByType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> _skillUpdaterMethodByType = new();
    private static int _hookErrorCount;

    public void OnLoad(IModContext context)
    {
        Instance = this;
    }

    public void OnEnable()
    {
        Patcher = new SafeHarmonyPatcher(Manifest.Id);
        InstallFourRenderPipelinePatches(Patcher);
        InstallFusionAndMonsterPreviewPatches(Patcher);
        SubscribeLifecycleEvents();
        BazaarLauncherPlugin.Log?.LogInfo(
            $"[CustomVisual] 4 大 PNG 渲染通路 + 融合隔离 + 野怪预览拦截已就绪 (成功={Patcher.SucceededCount}/{Patcher.AttemptedCount})");
    }

    public void OnDisable()
    {
        UnsubscribeLifecycleEvents();
        Patcher?.UnpatchAll();
    }

    public void OnUnload()
    {
        OnDisable();
        CardTextureCache.Instance.DisposeAllOnMain();
        if (Instance == this) Instance = null;
    }

    private void SubscribeLifecycleEvents()
    {
        UnsubscribeLifecycleEvents();
        try
        {
            var bus = BazaarLauncherPlugin.Host?.EventBus;
            if (bus != null)
            {
                _sceneEventSub = bus.Subscribe<SceneChangedModEvent>(OnSceneChangedModEvent);
                _combatEventSub = bus.Subscribe<CombatLifecycleModEvent>(OnCombatLifecycleModEvent);
            }
            SceneManager.activeSceneChanged += OnUnityActiveSceneChanged;
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CustomContentVisualModule:L169", diagEx); }
    }

    private void UnsubscribeLifecycleEvents()
    {
        try { _sceneEventSub?.Dispose(); } catch { }
        _sceneEventSub = null;
        try { _combatEventSub?.Dispose(); } catch { }
        _combatEventSub = null;
        try { SceneManager.activeSceneChanged -= OnUnityActiveSceneChanged; } catch { }
    }

    private static void OnSceneChangedModEvent(SceneChangedModEvent ev)
    {
        if (ev.IsMainMenu || IsMainMenuSceneName(ev.SceneName))
        {
            ModContentSyncClient.Instance?.OnReturnedToMainMenu();
        }
    }

    private static void OnUnityActiveSceneChanged(Scene _, Scene next)
    {
        if (IsMainMenuSceneName(next.name))
        {
            ModContentSyncClient.Instance?.OnReturnedToMainMenu();
        }
    }

    private static void OnCombatLifecycleModEvent(CombatLifecycleModEvent ev)
    {
        if (ev.Started)
        {
            _ = ModContentSyncClient.Instance?.SyncActiveBindingsAsync();
        }
    }

    private static bool IsMainMenuSceneName(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return false;
        return sceneName.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0
            || sceneName.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0
            || sceneName.IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ── 对外协作查询接口（供 CardIdentityResolver / LocalizationOverlay / CompatAuditPatch 调用）──

    internal static string TryGetPreviewCustomCardId(object cardOrComp)
    {
        if (cardOrComp != null && PreviewContextMap.TryGetValue(cardOrComp, out var ctx)
            && !string.IsNullOrEmpty(ctx.CustomCardId))
        {
            return ctx.CustomCardId;
        }
        var active = _activePreviewSetupContext;
        if (active != null && !string.IsNullOrEmpty(active.CustomCardId))
        {
            return active.CustomCardId;
        }
        return null;
    }

    internal bool TryLookupCustomIdentity(string instanceId, string templateId, out string customId, out bool isPatch)
    {
        customId = null;
        isPatch = false;
        lock (_sync)
        {
            if (!string.IsNullOrEmpty(instanceId))
            {
                for (int i = 0; i < _bindings.Count; i++)
                {
                    var b = _bindings[i];
                    if (b == null) continue;
                    if (!string.IsNullOrEmpty(b.InstanceId)
                        && string.Equals(b.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        customId = b.CustomCardId;
                        isPatch = b.IsPatchOverride
                            || (!string.IsNullOrEmpty(b.TemplateId) && string.Equals(b.CustomCardId, b.TemplateId, StringComparison.OrdinalIgnoreCase));
                        return !string.IsNullOrEmpty(customId);
                    }
                }
            }

            if (!string.IsNullOrEmpty(templateId))
            {
                for (int i = 0; i < _bindings.Count; i++)
                {
                    var b = _bindings[i];
                    if (b == null) continue;
                    if (!string.IsNullOrEmpty(b.TemplateId)
                        && string.Equals(b.TemplateId, templateId, StringComparison.OrdinalIgnoreCase)
                        && (b.IsPatchOverride || string.Equals(b.CustomCardId, b.TemplateId, StringComparison.OrdinalIgnoreCase)))
                    {
                        customId = b.CustomCardId;
                        isPatch = true;
                        return !string.IsNullOrEmpty(customId);
                    }
                }
            }
        }
        return false;
    }

    /// <summary>P0-24：供 CompatAuditPatch.WrapEncounterSetupTask 查询当前遭遇卡是否已有自定义视觉绑定。</summary>
    internal static bool HasActiveEncounterVisualOverride(object card)
    {
        var inst = Instance;
        if (inst == null || card == null) return false;
        var pq = BuildQueryFromCard(card, CardVisualTarget.EncounterPortrait);
        if (inst.TryResolveVisual(in pq, out _)) return true;
        var bq = BuildQueryFromCard(card, CardVisualTarget.EncounterBackground);
        return inst.TryResolveVisual(in bq, out _);
    }

    internal bool TryResolveWireTemplateIdForCustomCard(string customCardId, out string templateId)
    {
        templateId = null;
        if (string.IsNullOrWhiteSpace(customCardId)) return false;
        lock (_sync)
        {
            for (int i = 0; i < _bindings.Count; i++)
            {
                var b = _bindings[i];
                if (b != null
                    && string.Equals(b.CustomCardId, customCardId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(b.TemplateId))
                {
                    templateId = b.TemplateId;
                    return true;
                }
            }
        }
        return false;
    }

    // ── ICardVisualOverrideRegistry 实现 ─────────────────────────────────────

    public IDisposable RegisterVisualBinding(CardVisualBinding binding)
    {
        if (binding == null) throw new ArgumentNullException(nameof(binding));
        lock (_sync)
        {
            _bindings.Add(binding);
        }
        return new CallbackDisposable(() =>
        {
            lock (_sync) { _bindings.Remove(binding); }
        });
    }

    public IDisposable RegisterDynamicResolver(Func<CardVisualQuery, CardVisualBinding> resolver, int priority = 0)
    {
        if (resolver == null) throw new ArgumentNullException(nameof(resolver));
        var item = (priority, resolver);
        lock (_sync)
        {
            _dynamicResolvers.Add(item);
            _dynamicResolvers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
        return new CallbackDisposable(() =>
        {
            lock (_sync) { _dynamicResolvers.Remove(item); }
        });
    }

    public bool TryResolveVisual(in CardVisualQuery query, out CardVisualBinding binding)
    {
        binding = null;
        int bestScore = -1;

        lock (_sync)
        {
            for (int i = 0; i < _dynamicResolvers.Count; i++)
            {
                try
                {
                    var dyn = _dynamicResolvers[i].Resolver(query);
                    if (dyn != null && !string.IsNullOrWhiteSpace(dyn.LocalImagePath))
                    {
                        binding = dyn;
                        return true;
                    }
                }
                catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CustomContentVisualModule:L353", diagEx); }
            }

            for (int i = 0; i < _bindings.Count; i++)
            {
                var b = _bindings[i];
                if (b == null || string.IsNullOrWhiteSpace(b.LocalImagePath)) continue;
                if (b.Target != query.Target) continue;

                int score = -1;
                if (!string.IsNullOrEmpty(query.InstanceId) && !string.IsNullOrEmpty(b.InstanceId)
                    && string.Equals(query.InstanceId, b.InstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    score = 30000 + b.Priority;
                }
                else if (!string.IsNullOrEmpty(query.CustomCardId) && !string.IsNullOrEmpty(b.CustomCardId)
                    && string.Equals(query.CustomCardId, b.CustomCardId, StringComparison.OrdinalIgnoreCase))
                {
                    score = 20000 + b.Priority;
                }
                else if (!string.IsNullOrEmpty(query.TemplateId) && !string.IsNullOrEmpty(b.TemplateId)
                    && string.Equals(query.TemplateId, b.TemplateId, StringComparison.OrdinalIgnoreCase))
                {
                    if ((b.IsCustomBorrowShell && !b.IsPatchOverride)
                        || (!string.IsNullOrEmpty(b.CustomCardId)
                            && !string.Equals(b.CustomCardId, b.TemplateId, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    score = 10000 + b.Priority;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    binding = b;
                }
            }
        }

        return binding != null;
    }

    public void InvalidateVisualCache(string customIdOrTemplateId = null)
    {
        CardTextureCache.Instance.ClearUnreferencedOnMain();
    }

    public int GetRegisteredBindingCount()
    {
        lock (_sync) return _bindings.Count;
    }

    /// <summary>D3-P1-13：在主线程重绘所有在场与隐藏仓库卡（FindObjectsInactive.Include）的视觉贴图与徽章。</summary>
    internal void ReapplyActiveVisualsOnMainThread()
    {
        try
        {
            var visited = new HashSet<int>();
            var agents = UnityEngine.Object.FindObjectsByType<CardVisualOverrideAgent>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            if (agents != null)
            {
                for (int i = 0; i < agents.Length; i++)
                {
                    var ag = agents[i];
                    if (ag == null || ag.HostController == null) continue;
                    visited.Add(ag.HostController.GetInstanceID());

                    if (!ag.HasContext && _itemVisualsControllerType != null && _itemVisualsControllerType.IsInstanceOfType(ag.HostController))
                    {
                        object liveCard = ResolveLiveCardFromVisuals(ag.HostController);
                        if (liveCard != null)
                        {
                            ag.BoundCardObj = liveCard;
                            ag.SetContext(BuildQueryFromCard(liveCard, CardVisualTarget.ItemArt));
                        }
                    }

                    if (!ag.HasContext) continue;
                    ag.MarkBadgeDirty();
                    var host = ag.HostController;
                    if (_previewItemType != null && _previewItemType.IsInstanceOfType(host))
                    {
                        TryApplyPreviewItemVisual(host, ag, ag.Epoch, triggerSyncIfMissing: false);
                    }
                    else if (_previewSkillType != null && _previewSkillType.IsInstanceOfType(host))
                    {
                        TryApplyPreviewSkillVisual(host, ag, ag.Epoch, triggerSyncIfMissing: false);
                    }
                    else if (_previewEncounterType != null && _previewEncounterType.IsInstanceOfType(host))
                    {
                        TryApplyPreviewEncounterVisual(host, ag, ag.Epoch, triggerSyncIfMissing: false);
                    }
                    else if (ag.LastQuery.Target == CardVisualTarget.ItemArt)
                    {
                        TryApplyItemVisual(host, ag, ag.Epoch);
                    }
                    else if (ag.LastQuery.Target == CardVisualTarget.SkillIcon)
                    {
                        TryApplySkillVisual(host, ag, ag.Epoch, triggerSyncIfMissing: false);
                    }
                    else if (ag.LastQuery.Target == CardVisualTarget.EncounterPortrait)
                    {
                        TryApplyEncounterVisual(host, ag, ag.Epoch, triggerSyncIfMissing: false);
                    }
                }
            }

            if (_itemVisualsControllerType != null)
            {
                var visualsArr = UnityEngine.Object.FindObjectsByType(
                    _itemVisualsControllerType,
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                if (visualsArr != null)
                {
                    for (int i = 0; i < visualsArr.Length; i++)
                    {
                        if (visualsArr[i] is not Component visComp || visComp == null) continue;
                        if (!visited.Add(visComp.GetInstanceID())) continue;

                        object liveCard = ResolveLiveCardFromVisuals(visComp);
                        if (liveCard == null) continue;
                        var q = BuildQueryFromCard(liveCard, CardVisualTarget.ItemArt);
                        bool hasVisual = TryResolveVisual(in q, out _);
                        var l10n = LocalizationOverlayModule.Instance;
                        bool hasBadge = l10n != null
                            && l10n.TryResolveCardMetadata(q.InstanceId, q.CustomCardId, q.TemplateId, out var ov)
                            && ov?.CounterDisplays != null && ov.CounterDisplays.Length > 0;
                        if (!hasVisual && !hasBadge) continue;

                        var ag = GetOrAddAgent(visComp);
                        ag.HostController = visComp;
                        ag.BoundCardObj = liveCard;
                        if (!ag.HasContext || !ag.MatchesQuery(in q))
                        {
                            ag.SetContext(q);
                        }
                        if (hasVisual)
                        {
                            TryApplyItemVisual(visComp, ag, ag.Epoch);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(ReapplyActiveVisualsOnMainThread), ex);
        }
    }

    // ── 安装 4 大渲染通路 + 回收钩子 ─────────────────────────────────────────

    private static void InstallFourRenderPipelinePatches(SafeHarmonyPatcher patcher)
    {
        // 通路 1：3D 物品与战报回放 (ItemVisualsController / CardController / BoardManager)
        var itemVisType = GameInspector.FindTypeSafe("TheBazaar.Game.CardFrames.ItemVisualsController");
        _recapItemVisualControllerType = GameInspector.FindTypeSafe("TheBazaar.RecapItemVisualController", "RecapItemVisualController");
        _cardControllerType = GameInspector.FindTypeSafe("CardController", "TheBazaar.CardController");

        if (itemVisType != null)
        {
            _itemVisualsControllerType = itemVisType;
            _itemMatInstanceField = AccessTools.Field(itemVisType, "materialInstance");
            _itemArtRendererField = AccessTools.Field(itemVisType, "cardIllustrationRenderer");
            _itemFrameAnchorField = AccessTools.Field(itemVisType, "cardFrameAnchor");
            _itemControllerField = AccessTools.Field(itemVisType, "_itemController");
            _itemBackVisibleField = AccessTools.Field(itemVisType, "_backVisible");

            var cardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.Card");
            var loadoutType = GameInspector.FindTypeSafe(
                "BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout",
                "TheBazaar.BazaarCollectionLoadout");
            if (cardType != null && loadoutType != null)
            {
                patcher.TryPatch(
                    itemVisType,
                    "Setup",
                    new[] { cardType, loadoutType },
                    prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(ItemVisualsSetupCardPrefix)),
                    postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(ItemVisualsSetupCardPostfix)),
                    optional: true,
                    label: "Visual.P1.ItemVisualsController.Setup(Card)");
            }

            var itCardType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Cards.ITCard");
            if (itCardType != null)
            {
                patcher.TryPatch(
                    itemVisType,
                    "Setup",
                    new[] { itCardType },
                    prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(ItemVisualsSetupTemplatePrefix)),
                    postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(ItemVisualsSetupTemplatePostfix)),
                    optional: true,
                    label: "Visual.P1.ItemVisualsController.Setup(ITCard)");
            }

            var tierType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Core.Types.ETier");
            var cardBackType = GameInspector.FindTypeSafe("TheBazaar.Assets.Scripts.ScriptableObjectsScripts.CardBackAssetDataSO");
            var enchType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Core.Types.EEnchantmentType");
            if (tierType != null && cardBackType != null && enchType != null)
            {
                var nullableEnch = typeof(Nullable<>).MakeGenericType(enchType);
                patcher.TryPatch(
                    itemVisType,
                    "Setup",
                    new[] { typeof(Material), tierType, cardBackType, typeof(bool), nullableEnch },
                    prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(ItemVisualsSetupMaterialPrefix)),
                    optional: true,
                    label: "Visual.P1.ItemVisualsController.Setup(Material)");
            }

            patcher.TryPatch(
                itemVisType,
                "Cleanup",
                prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(ItemVisualsCleanupPrefix)),
                optional: true,
                label: "Visual.P1.ItemVisualsController.Cleanup");

            patcher.TryPatch(
                itemVisType,
                "SetCardFrameMaterial",
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(ItemVisualsSetMaterialPostfix)),
                optional: true,
                label: "Visual.P1.ItemVisualsController.SetCardFrameMaterial");
        }

        // P0-29：管线 1 补齐 CardController.ClearVisualProxy Prefix + BoardManager.HideRecapView Postfix（严禁挂 HideRecapDeck）
        if (_cardControllerType != null)
        {
            patcher.TryPatch(
                _cardControllerType,
                "ClearVisualProxy",
                prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardControllerClearVisualProxyPrefix)),
                optional: true,
                label: "Visual.P1.CardController.ClearVisualProxy");
        }

        var boardMgrType = GameInspector.FindTypeSafe("BoardManager", "TheBazaar.BoardManager");
        if (boardMgrType != null)
        {
            _boardManagerIsRecapOpenProp = AccessTools.Property(boardMgrType, "IsRecapViewOpen");
            _boardManagerIsRecapOpenField = AccessTools.Field(boardMgrType, "_isRecapViewOpen");
            patcher.TryPatch(
                boardMgrType,
                "HideRecapView",
                prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(BoardManagerHideRecapViewPrefix)),
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(BoardManagerHideRecapViewPostfix)),
                optional: true,
                label: "Visual.P1.BoardManager.HideRecapView");
        }

        // 通路 2：3D 技能圆牌与技能代理 (SkillController / SkillProxyRenderer / SkillIconUpdater)
        var skillCtrlType = GameInspector.FindTypeSafe("SkillController", "TheBazaar.SkillController");
        _skillControllerType = skillCtrlType;
        if (skillCtrlType != null)
        {
            _skillUpdateIconMethod = AccessTools.Method(skillCtrlType, "UpdateSkillIcon", new[] { typeof(Texture) });
            patcher.TryPatch(
                skillCtrlType,
                "Setup",
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(SkillControllerSetupPostfix)),
                optional: true,
                label: "Visual.P2.SkillController.Setup");
            patcher.TryPatch(
                skillCtrlType,
                "UpdateCard",
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(SkillControllerUpdateCardPostfix)),
                optional: true,
                label: "Visual.P2.SkillController.UpdateCard");
            patcher.TryPatch(
                skillCtrlType,
                "UpdateSkillIcon",
                new[] { typeof(Texture) },
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(SkillUpdateIconPostfix)),
                optional: true,
                label: "Visual.P2.SkillController.UpdateSkillIcon");
            // P0-29：SkillController.Cleanup Prefix 先还原快照与释放租约，再由原版 PoolObject
            patcher.TryPatch(
                skillCtrlType,
                "Cleanup",
                prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(SkillControllerCleanupPrefix)),
                optional: true,
                label: "Visual.P2.SkillController.Cleanup");
        }

        _skillIconUpdaterType = GameInspector.FindTypeSafe("SkillIconUpdater", "TheBazaar.SkillIconUpdater");
        if (_skillIconUpdaterType != null)
        {
            // P0-23：直接挂钩 SkillIconUpdater.UpdateSkillIconMaterial(Texture) 落盘瞬间替换自定义图并留原版快照
            patcher.TryPatch(
                _skillIconUpdaterType,
                "UpdateSkillIconMaterial",
                new[] { typeof(Texture) },
                prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(SkillIconUpdaterMaterialPrefix)),
                optional: true,
                label: "Visual.P2.SkillIconUpdater.UpdateSkillIconMaterial");
        }

        var skillProxyType = GameInspector.FindTypeSafe("SkillProxyRenderer", "TheBazaar.SkillProxyRenderer");
        _skillProxyRendererType = skillProxyType;
        if (skillProxyType != null)
        {
            patcher.TryPatch(
                skillProxyType,
                "Initialize",
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(SkillProxyInitializePostfix)),
                optional: true,
                label: "Visual.P2.SkillProxyRenderer.Initialize");
            patcher.TryPatch(
                skillProxyType,
                "UpdateSkillIcon",
                new[] { typeof(Texture) },
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(SkillUpdateIconPostfix)),
                optional: true,
                label: "Visual.P2.SkillProxyRenderer.UpdateSkillIcon");
        }

        // 通路 3：2D UI 预览 (CardPreviewBase 4 参 SetUp / CardPreviewItem / CardPreviewSkill / CardPreviewEncounter)
        var previewBase = GameInspector.FindTypeSafe("TheBazaar.UI.CardPreviewBase");
        _previewBaseType = previewBase;
        if (previewBase != null)
        {
            _previewCardDataField = AccessTools.Field(previewBase, "_cardData");
            _previewClientCardField = AccessTools.Field(previewBase, "_clientCard");
            _previewCardImageField = AccessTools.Field(previewBase, "_cardImage");

            // P0-26：必须显式指定 4 参类型数组防止 CardPreviewBase.SetUp 双重载引发 AmbiguousMatchException
            var tCardBaseType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Cards.TCardBase");
            var tCardInstType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Cards.TCardInstance");
            if (tCardBaseType != null && tCardInstType != null)
            {
                patcher.TryPatch(
                    previewBase,
                    "SetUp",
                    new[] { tCardBaseType, typeof(bool), tCardInstType, typeof(CancellationToken) },
                    prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardPreviewBaseSetUpPrefix)),
                    postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardPreviewBaseSetUpPostfix)),
                    optional: true,
                    label: "Visual.P3.CardPreviewBase.SetUp(4)");
            }
        }

        var previewItem = GameInspector.FindTypeSafe("TheBazaar.UI.CardPreviewItem");
        if (previewItem != null)
        {
            _previewItemType = previewItem;
            _previewItemMaterialField = AccessTools.Field(previewItem, "_cardMaterial");
            patcher.TryPatch(
                previewItem,
                "UpdateCardImageMaterial",
                prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardPreviewItemPrefix)),
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardPreviewItemPostfix)),
                optional: true,
                label: "Visual.P3.CardPreviewItem.UpdateCardImageMaterial");
        }

        var previewSkill = GameInspector.FindTypeSafe("TheBazaar.UI.CardPreviewSkill");
        if (previewSkill != null)
        {
            _previewSkillType = previewSkill;
            patcher.TryPatch(
                previewSkill,
                "LoadArt",
                prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardPreviewSkillPrefix)),
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardPreviewSkillPostfix)),
                optional: true,
                label: "Visual.P3.CardPreviewSkill.LoadArt");
        }

        var previewEncounter = GameInspector.FindTypeSafe("TheBazaar.UI.CardPreviewEncounter");
        if (previewEncounter != null)
        {
            _previewEncounterType = previewEncounter;
            _previewEncounterPortraitField = AccessTools.Field(previewEncounter, "_portraitImage");
            patcher.TryPatch(
                previewEncounter,
                "LoadArt",
                prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardPreviewEncounterPrefix)),
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardPreviewEncounterPostfix)),
                optional: true,
                label: "Visual.P3.CardPreviewEncounter.LoadArt");
        }

        // 通路 4：遭遇卡与战斗立绘 (EncounterController)
        var encCtrl = GameInspector.FindTypeSafe("EncounterController", "TheBazaar.EncounterController");
        if (encCtrl != null)
        {
            _encounterControllerType = encCtrl;
            _encounterPortraitRendererField = AccessTools.Field(encCtrl, "portrait");
            _encounterAnimatedPortraitField = AccessTools.Field(encCtrl, "_currentAnimatedPortraitInstance");
            _encounterBackgroundRendererField = AccessTools.Field(encCtrl, "backgroundRenderer");
            _encounterInstancedBgMatField = AccessTools.Field(encCtrl, "instancedBackgroundMaterial");
            _encounterCardDataProp = AccessTools.Property(encCtrl, "CardData");

            var frameType = GameInspector.FindTypeSafe(
                "TheBazaar.Game.CardFrames.CardFrameController",
                "CardFrameController",
                "TheBazaar.CardFrameController");
            if (frameType != null)
            {
                _cardFrameUpdateBgMethod = AccessTools.Method(frameType, "UpdateBackgroundMaterial", new[] { typeof(Texture) });
            }

            patcher.TryPatch(
                encCtrl,
                "Setup",
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(EncounterControllerSetupPostfix)),
                optional: true,
                label: "Visual.P4.EncounterController.Setup");

            // P0-24：在 SetupEncounterController 异步装配完成后立即补刷自定义奇遇立绘/背景
            var encSoType = GameInspector.FindTypeSafe(
                "EncounterAssetDataSO",
                "TheBazaar.Assets.Scripts.ScriptableObjectsScripts.EncounterAssetDataSO");
            var setupEncCtrlMethod = encSoType != null
                ? AccessTools.Method(encCtrl, "SetupEncounterController", new[] { encSoType })
                : AccessTools.Method(encCtrl, "SetupEncounterController");
            if (setupEncCtrlMethod != null)
            {
                patcher.TryPatchMethod(
                    setupEncCtrlMethod,
                    postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(EncounterControllerSetupEncounterPostfix)),
                    optional: true,
                    label: "Visual.P4.EncounterController.SetupEncounterController");
            }

            // P0-29：EncounterController.Cleanup Prefix 先还原原版立绘/背景 MPB 与租约
            patcher.TryPatch(
                encCtrl,
                "Cleanup",
                prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(EncounterControllerCleanupPrefix)),
                optional: true,
                label: "Visual.P4.EncounterController.Cleanup");
        }
    }

    // ── 安装 P0-20 融合隔离 + P0-21 野怪悬停 + P0-29 MonsterBoardTooltip 回收 ──

    private static void InstallFusionAndMonsterPreviewPatches(SafeHarmonyPatcher patcher)
    {
        var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
        if (dataType != null)
        {
            _dataRunProp = dataType.GetProperty("Run", BindingFlags.Public | BindingFlags.Static);
            _dataEntitiesProp = dataType.GetProperty("Entities", BindingFlags.Public | BindingFlags.Static);
            _dataGetStaticMethod = dataType.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        }

        // P0-20：融合逻辑身份隔离 3 钩子
        var itemCardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.ItemCard");
        if (itemCardType != null)
        {
            patcher.TryPatch(
                itemCardType,
                "CanFuseWith",
                new[] { itemCardType },
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardCanFuseWithPostfix)),
                optional: true,
                label: "Fusion.ItemCard.CanFuseWith");
        }

        var skillCardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.SkillCard");
        if (skillCardType != null)
        {
            patcher.TryPatch(
                skillCardType,
                "CanFuseWith",
                new[] { skillCardType },
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardCanFuseWithPostfix)),
                optional: true,
                label: "Fusion.SkillCard.CanFuseWith");
        }

        var cardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.Card");
        var cardExtType = GameInspector.FindTypeSafe("TheBazaar.CardExtensions");
        if (cardExtType != null && cardType != null)
        {
            patcher.TryPatch(
                cardExtType,
                "CanFuse",
                new[] { cardType },
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(CardExtensionsCanFusePostfix)),
                optional: true,
                label: "Fusion.CardExtensions.CanFuse");
        }

        // P0-21：野怪悬停自定义预览
        var tooltipDataType = GameInspector.FindTypeSafe("TheBazaar.Tooltips.CardTooltipData");
        if (tooltipDataType != null)
        {
            patcher.TryPatch(
                tooltipDataType,
                "GetPVEEncounterLevel",
                Type.EmptyTypes,
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(GetPveEncounterLevelPostfix)),
                optional: true,
                label: "MonsterPreview.CardTooltipData.GetPVEEncounterLevel");
        }

        var rewardRendererType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.MonsterRewardRenderer");
        if (rewardRendererType != null && tooltipDataType != null)
        {
            _monsterRewardViewField = AccessTools.Field(rewardRendererType, "_view");
            patcher.TryPatch(
                rewardRendererType,
                "Render",
                new[] { tooltipDataType },
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(MonsterRewardRendererRenderPostfix)),
                optional: true,
                label: "MonsterPreview.MonsterRewardRenderer.Render");
        }

        var boardRendererType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.MonsterBoardRenderer");
        var tMonsterType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Monsters.TMonster", "BazaarGameShared.Domain.Cards.Encounter.TMonster");
        if (boardRendererType != null && tooltipDataType != null)
        {
            _monsterBoardViewField = AccessTools.Field(boardRendererType, "_view");
            MethodInfo buildDisplayM = tMonsterType != null
                ? AccessTools.Method(boardRendererType, "BuildDisplay", new[] { tMonsterType, tooltipDataType })
                : AccessTools.Method(boardRendererType, "BuildDisplay");
            if (buildDisplayM != null)
            {
                patcher.TryPatchMethod(
                    buildDisplayM,
                    prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(MonsterBoardRendererBuildDisplayPrefix)),
                    optional: true,
                    label: "MonsterPreview.MonsterBoardRenderer.BuildDisplay");
            }
        }

        // P0-29：MonsterBoardTooltip.HandlePooling Prefix 先还原与释放子项预览租约再交由原版回收
        var boardTooltipType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.MonsterBoardTooltip");
        if (boardTooltipType != null)
        {
            _monsterBoardActiveCardsField = AccessTools.Field(boardTooltipType, "_activeCards");
            _monsterBoardActiveSkillsField = AccessTools.Field(boardTooltipType, "_activeSkills");
            patcher.TryPatch(
                boardTooltipType,
                "HandlePooling",
                Type.EmptyTypes,
                prefix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(MonsterBoardTooltipHandlePoolingPrefix)),
                optional: true,
                label: "Visual.P4.MonsterBoardTooltip.HandlePooling");
        }
    }

    // ── P0-20 融合隔离回调 ───────────────────────────────────────────────────

    private static void CardCanFuseWithPostfix(object __instance, object __0, ref bool __result)
    {
        try
        {
            if (!__result || __instance == null || __0 == null) return;
            string idA = CardIdentityResolver.ResolveLogicalCardIdentity(__instance);
            string idB = CardIdentityResolver.ResolveLogicalCardIdentity(__0);
            if (!string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase))
            {
                __result = false;
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardCanFuseWithPostfix), ex);
        }
    }

    private static void CardExtensionsCanFusePostfix(object __0, ref bool __result)
    {
        try
        {
            if (!__result || __0 == null) return;

            object run = _dataRunProp?.GetValue(null);
            object player = run != null ? run.GetType().GetProperty("Player", BindingFlags.Public | BindingFlags.Instance)?.GetValue(run) : null;
            if (player == null) return;

            var cardBag = GetCardPropBag(__0.GetType());
            object cardOwner = SafeGetPropertyValue(cardBag.Owner, __0);

            // 仅在 cardToFuse.Owner == player 分支执行全量实体逻辑身份复验；
            // Owner != player 分支已由补丁后的 ItemCard/SkillCard.CanFuseWith 精确拦截，绝不误伤原版商店卡！
            if (!ReferenceEquals(cardOwner, player)) return;

            string myId = CardIdentityResolver.ResolveLogicalCardIdentity(__0);
            object myTier = SafeGetPropertyValue(cardBag.Tier, __0);
            if (_dataEntitiesProp?.GetValue(null) is not IDictionary entities) return;

            bool foundLogicalMatch = false;
            foreach (object val in entities.Values)
            {
                if (val == null || ReferenceEquals(val, __0)) continue;
                var otherBag = GetCardPropBag(val.GetType());
                object otherOwner = SafeGetPropertyValue(otherBag.Owner, val);
                if (ReferenceEquals(otherOwner, cardOwner)) continue;
                object otherTier = SafeGetPropertyValue(otherBag.Tier, val);
                if (!Equals(myTier, otherTier)) continue;
                string otherId = CardIdentityResolver.ResolveLogicalCardIdentity(val);
                if (string.Equals(myId, otherId, StringComparison.OrdinalIgnoreCase))
                {
                    foundLogicalMatch = true;
                    break;
                }
            }

            if (!foundLogicalMatch)
            {
                __result = false;
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardExtensionsCanFusePostfix), ex);
        }
    }

    // ── P0-21 野怪悬停自定义预览回调 ─────────────────────────────────────────

    private static void GetPveEncounterLevelPostfix(object __instance, ref int __result)
    {
        try
        {
            if (__instance == null) return;
            if (LocalizationOverlayModule.TryExtractCardMetadata(__instance, out var ov, out _)
                && ov?.MonsterPreview != null && ov.MonsterPreview.Level > 0)
            {
                __result = ov.MonsterPreview.Level;
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(GetPveEncounterLevelPostfix), ex);
        }
    }

    private static void MonsterRewardRendererRenderPostfix(object __instance, object __0)
    {
        try
        {
            if (__instance == null || __0 == null) return;
            if (!LocalizationOverlayModule.TryExtractCardMetadata(__0, out var ov, out _)
                || ov?.MonsterPreview == null)
            {
                return;
            }

            var mp = ov.MonsterPreview;
            object view = _monsterRewardViewField?.GetValue(__instance);
            if (view == null) return;

            int effLevel = mp.Level > 0 ? mp.Level : Math.Max(1, mp.Day);
            int effDay = mp.Day > 0 ? mp.Day : 1;

            // 反编译对齐：MonsterRewardView.Init(string gold, string xp, string level, string day) + ShowAll(true)
            var initM = AccessTools.Method(view.GetType(), "Init", new[] { typeof(string), typeof(string), typeof(string), typeof(string) });
            if (initM != null)
            {
                initM.Invoke(view, new object[]
                {
                    mp.RewardGold.ToString(),
                    mp.RewardXp.ToString(),
                    effLevel.ToString(),
                    effDay.ToString(),
                });
                var showAllM = AccessTools.Method(view.GetType(), "ShowAll", new[] { typeof(bool) });
                showAllM?.Invoke(view, new object[] { true });
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(MonsterRewardRendererRenderPostfix), ex);
        }
    }

    private static bool MonsterBoardRendererBuildDisplayPrefix(object __instance, object __1)
    {
        try
        {
            if (__instance == null || __1 == null) return true;
            if (!LocalizationOverlayModule.TryExtractCardMetadata(__1, out var ov, out _)
                || ov?.MonsterPreview == null)
            {
                return true;
            }

            object view = _monsterBoardViewField?.GetValue(__instance);
            if (view == null) return true;

            var mp = ov.MonsterPreview;
            var tItemType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Cards.Item.TCardInstanceItem");
            var tSkillType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Cards.Skill.TCardInstanceSkill");
            if (tItemType == null || tSkillType == null) return true;

            // 先回收上次预览卡片，杜绝野怪悬停残留叠加
            var handlePoolingM = AccessTools.Method(view.GetType(), "HandlePooling", Type.EmptyTypes);
            handlePoolingM?.Invoke(view, null);

            var itemListType = typeof(List<>).MakeGenericType(tItemType);
            var skillListType = typeof(List<>).MakeGenericType(tSkillType);
            if (Activator.CreateInstance(itemListType) is not IList itemsList
                || Activator.CreateInstance(skillListType) is not IList skillsList)
            {
                return true;
            }

            // 1. 计算居中槽位宽度并构建 List<TCardInstanceItem>（SocketId 严格使用 EContainerSocketId 0..9）
            var srcItems = mp.Items ?? Array.Empty<CardMonsterLoadoutItemInfo>();
            var resolvedEntries = new List<(CardMonsterLoadoutItemInfo Entry, Guid WireGuid, int Width)>(srcItems.Length);
            int totalSlots = 0;
            for (int i = 0; i < srcItems.Length; i++)
            {
                var entry = srcItems[i];
                if (entry == null) continue;
                if (!TryResolveLoadoutWireGuid(entry, out Guid wireGuid)) continue;
                int width = ResolveLoadoutItemWidth(entry, wireGuid);
                resolvedEntries.Add((entry, wireGuid, width));
                totalSlots += width;
            }

            int clampedTotal = Math.Min(10, totalSlots);
            int cursor = Math.Max(0, (10 - clampedTotal) / 2);
            var itemTplIdProp = tItemType.GetProperty("TemplateId", BindingFlags.Public | BindingFlags.Instance);
            var itemTierProp = tItemType.GetProperty("Tier", BindingFlags.Public | BindingFlags.Instance);
            var itemEnchProp = tItemType.GetProperty("EnchantmentType", BindingFlags.Public | BindingFlags.Instance)
                ?? tItemType.GetProperty("Enchantment", BindingFlags.Public | BindingFlags.Instance);
            var itemSocketProp = tItemType.GetProperty("SocketId", BindingFlags.Public | BindingFlags.Instance);

            for (int i = 0; i < resolvedEntries.Count; i++)
            {
                var (entry, wireGuid, width) = resolvedEntries[i];
                object itemInst = Activator.CreateInstance(tItemType);
                if (itemInst == null) continue;

                SafeSetProp(itemTplIdProp, itemInst, wireGuid);
                if (!string.IsNullOrWhiteSpace(entry.Tier) && Enum.TryParse<ETier>(entry.Tier.Trim(), true, out var tierVal))
                {
                    SafeSetProp(itemTierProp, itemInst, tierVal);
                }
                if (!string.IsNullOrWhiteSpace(entry.Enchantment) && Enum.TryParse<EEnchantmentType>(entry.Enchantment.Trim(), true, out var enchVal))
                {
                    SafeSetProp(itemEnchProp, itemInst, enchVal);
                }

                int slotIdx = Math.Min(9, Math.Max(0, cursor));
                AssignSocketIdProperty(itemSocketProp, itemInst, slotIdx);
                cursor = Math.Min(10, cursor + width);

                if (!string.IsNullOrWhiteSpace(entry.CardId))
                {
                    PreviewContextMap.AddOrUpdate(itemInst, new PreviewCardContext(entry.CardId, wireGuid.ToString(), ov.ModId));
                }
                itemsList.Add(itemInst);
            }

            // 2. 构建 List<TCardInstanceSkill>
            var srcSkills = mp.Skills ?? Array.Empty<CardMonsterLoadoutItemInfo>();
            var skillTplIdProp = tSkillType.GetProperty("TemplateId", BindingFlags.Public | BindingFlags.Instance);
            var skillTierProp = tSkillType.GetProperty("Tier", BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < srcSkills.Length; i++)
            {
                var entry = srcSkills[i];
                if (entry == null) continue;
                if (!TryResolveLoadoutWireGuid(entry, out Guid wireGuid)) continue;

                object skillInst = Activator.CreateInstance(tSkillType);
                if (skillInst == null) continue;

                SafeSetProp(skillTplIdProp, skillInst, wireGuid);
                if (!string.IsNullOrWhiteSpace(entry.Tier) && Enum.TryParse<ETier>(entry.Tier.Trim(), true, out var tierVal))
                {
                    SafeSetProp(skillTierProp, skillInst, tierVal);
                }
                if (!string.IsNullOrWhiteSpace(entry.CardId))
                {
                    PreviewContextMap.AddOrUpdate(skillInst, new PreviewCardContext(entry.CardId, wireGuid.ToString(), ov.ModId));
                }
                skillsList.Add(skillInst);
            }

            var handleBoardStateM = AccessTools.Method(view.GetType(), "HandleBoardState");
            if (handleBoardStateM == null) return true;

            handleBoardStateM.Invoke(view, new object[] { itemsList, skillsList, mp.HealthMax });
            return false;
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(MonsterBoardRendererBuildDisplayPrefix), ex);
            return true;
        }
    }

    private static bool TryResolveLoadoutWireGuid(CardMonsterLoadoutItemInfo entry, out Guid wireGuid)
    {
        wireGuid = Guid.Empty;
        if (entry == null) return false;
        if (!string.IsNullOrWhiteSpace(entry.WireTemplateId) && Guid.TryParse(entry.WireTemplateId.Trim(), out wireGuid) && wireGuid != Guid.Empty)
            return true;
        if (!string.IsNullOrWhiteSpace(entry.CardId) && Guid.TryParse(entry.CardId.Trim(), out wireGuid) && wireGuid != Guid.Empty)
            return true;
        if (!string.IsNullOrWhiteSpace(entry.CardId) && Instance != null
            && Instance.TryResolveWireTemplateIdForCustomCard(entry.CardId.Trim(), out string mapped)
            && Guid.TryParse(mapped, out wireGuid) && wireGuid != Guid.Empty)
            return true;
        return false;
    }

    private static int ResolveLoadoutItemWidth(CardMonsterLoadoutItemInfo entry, Guid wireGuid)
    {
        // 双审备忘 #1：优先读取 monsterPreview.items[].size，缺失时回落静态模板 Size
        if (!string.IsNullOrWhiteSpace(entry?.Size))
        {
            int fromEntry = MapCardSizeNameToSlotWidth(entry.Size);
            if (fromEntry > 0) return fromEntry;
        }
        try
        {
            var staticMgr = _dataGetStaticMethod?.Invoke(null, null);
            var getCardM = staticMgr?.GetType().GetMethod("GetCardById", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Guid) }, null);
            object tpl = getCardM?.Invoke(staticMgr, new object[] { wireGuid });
            if (tpl != null)
            {
                var sizeProp = tpl.GetType().GetProperty("Size", BindingFlags.Public | BindingFlags.Instance);
                string sizeName = sizeProp?.GetValue(tpl)?.ToString();
                int fromTpl = MapCardSizeNameToSlotWidth(sizeName);
                if (fromTpl > 0) return fromTpl;
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CustomContentVisualModule:L1185", diagEx); }
        return 1;
    }

    private static int MapCardSizeNameToSlotWidth(string sizeName)
    {
        if (string.IsNullOrWhiteSpace(sizeName)) return 0;
        string s = sizeName.Trim();
        if (string.Equals(s, "Large", StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(s, "Medium", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(s, "Small", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static void AssignSocketIdProperty(PropertyInfo prop, object target, int slotIdx)
    {
        if (prop == null || !prop.CanWrite || target == null) return;
        try
        {
            Type pType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (pType == typeof(EContainerSocketId))
            {
                prop.SetValue(target, (EContainerSocketId)slotIdx);
            }
            else if (pType.IsEnum)
            {
                prop.SetValue(target, Enum.ToObject(pType, slotIdx));
            }
            else if (pType == typeof(int))
            {
                prop.SetValue(target, slotIdx);
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CustomContentVisualModule:L1218", diagEx); }
    }

    private static void SafeSetProp(PropertyInfo prop, object target, object value)
    {
        if (prop == null || !prop.CanWrite || target == null) return;
        try { prop.SetValue(target, value); } catch { }
    }

    private static void MonsterBoardTooltipHandlePoolingPrefix(object __instance)
    {
        try
        {
            if (__instance == null) return;
            CleanupPreviewCollection(_monsterBoardActiveCardsField?.GetValue(__instance) as IEnumerable);
            CleanupPreviewCollection(_monsterBoardActiveSkillsField?.GetValue(__instance) as IEnumerable);
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(MonsterBoardTooltipHandlePoolingPrefix), ex);
        }
    }

    private static void CleanupPreviewCollection(IEnumerable items)
    {
        if (items == null) return;
        foreach (object obj in items)
        {
            if (obj is not Component comp || comp == null) continue;
            var agent = comp.GetComponent<CardVisualOverrideAgent>();
            if (agent != null)
            {
                agent.RestorePreviewVisualIfApplied(comp);
                agent.ClearContext();
            }
            PreviewContextMap.Remove(comp);
            object clientCard = _previewClientCardField?.GetValue(comp);
            if (clientCard != null) PreviewContextMap.Remove(clientCard);
        }
    }

    // ── 通路 1 回调：3D 物品与回放 ───────────────────────────────────────────

    private static void ItemVisualsSetupCardPrefix(Component __instance, object __0)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            if (__0 == null)
            {
                agent.ClearContext();
                return;
            }
            agent.BoundCardObj = __0;
            var query = BuildQueryFromCard(__0, CardVisualTarget.ItemArt);
            agent.BeginTypedSetup(query);
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(ItemVisualsSetupCardPrefix), ex);
        }
    }

    private static void ItemVisualsSetupCardPostfix(Component __instance, object __0, Task __result)
    {
        try
        {
            if (__instance == null || __0 == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            agent.BoundCardObj = __0;
            var query = BuildQueryFromCard(__0, CardVisualTarget.ItemArt);
            if (!agent.HasContext || !agent.MatchesQuery(in query))
            {
                agent.SetContext(query);
            }
            agent.FinishTypedSetupEntry(__result);

            if (!Instance.TryResolveVisual(in query, out _) && !string.IsNullOrEmpty(query.InstanceId))
            {
                ModContentSyncClient.Instance.NotifyUnboundInstanceSeen(query.InstanceId, query.TemplateId);
            }

            if (__result != null && !__result.IsCompleted)
            {
                uint epoch = agent.Epoch;
                _ = __result.ContinueWith(_ => MainThreadDispatcher.Enqueue(() => TryApplyItemVisual(__instance, agent, epoch)), TaskScheduler.Default);
            }
            else
            {
                TryApplyItemVisual(__instance, agent, agent.Epoch);
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(ItemVisualsSetupCardPostfix), ex);
        }
    }

    private static void ItemVisualsSetupTemplatePrefix(Component __instance, object __0)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            agent.BoundCardObj = null;
            string tplId = ReadTemplateId(__0);
            if (string.IsNullOrEmpty(tplId))
            {
                agent.ClearContext();
                return;
            }
            agent.BeginTypedSetup(new CardVisualQuery(null, null, tplId, 0, null, CardVisualTarget.ItemArt));
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(ItemVisualsSetupTemplatePrefix), ex);
        }
    }

    private static void ItemVisualsSetupTemplatePostfix(Component __instance, object __0, Task __result)
    {
        try
        {
            if (__instance == null || __0 == null || Instance == null) return;
            string tplId = ReadTemplateId(__0);
            if (string.IsNullOrEmpty(tplId)) return;

            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            agent.BoundCardObj = null;
            var query = new CardVisualQuery(null, null, tplId, 0, null, CardVisualTarget.ItemArt);
            if (!agent.HasContext || !agent.MatchesQuery(in query))
            {
                agent.SetContext(query);
            }
            agent.FinishTypedSetupEntry(__result);

            if (__result != null && !__result.IsCompleted)
            {
                uint epoch = agent.Epoch;
                _ = __result.ContinueWith(_ => MainThreadDispatcher.Enqueue(() => TryApplyItemVisual(__instance, agent, epoch)), TaskScheduler.Default);
            }
            else
            {
                TryApplyItemVisual(__instance, agent, agent.Epoch);
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(ItemVisualsSetupTemplatePostfix), ex);
        }
    }

    private static void ItemVisualsSetupMaterialPrefix(Component __instance)
    {
        try
        {
            if (__instance == null) return;
            var agent = __instance.GetComponent<CardVisualOverrideAgent>();
            if (agent == null) return;
            if (agent.IsTypedSetupInFlight) return;

            object liveCard = ResolveLiveCardFromVisuals(__instance);
            if (liveCard != null && agent.HasContext && LiveCardMatchesAgent(__instance, agent))
            {
                return;
            }
            agent.ClearContext();
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(ItemVisualsSetupMaterialPrefix), ex);
        }
    }

    private static void ItemVisualsCleanupPrefix(Component __instance)
    {
        try
        {
            if (__instance == null) return;
            var agent = __instance.GetComponent<CardVisualOverrideAgent>();
            if (agent != null)
            {
                Material mat = _itemMatInstanceField?.GetValue(__instance) as Material;
                if (mat != null) agent.RestoreOriginalMaterialIfApplied(mat);
                agent.ClearContext();
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(ItemVisualsCleanupPrefix), ex);
        }
    }

    private static void CardControllerClearVisualProxyPrefix(Component __instance)
    {
        try
        {
            if (__instance == null) return;
            var agents = __instance.GetComponentsInChildren<CardVisualOverrideAgent>(includeInactive: true);
            if (agents == null) return;
            for (int i = 0; i < agents.Length; i++)
            {
                var ag = agents[i];
                if (ag == null) continue;
                if (ag.HostController != null && _itemVisualsControllerType != null && _itemVisualsControllerType.IsInstanceOfType(ag.HostController))
                {
                    Material mat = _itemMatInstanceField?.GetValue(ag.HostController) as Material;
                    if (mat != null) ag.RestoreOriginalMaterialIfApplied(mat);
                }
                ag.ClearContext();
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardControllerClearVisualProxyPrefix), ex);
        }
    }

    private static void BoardManagerHideRecapViewPrefix(object __instance, out bool __state)
    {
        __state = false;
        try
        {
            if (__instance == null) return;
            if (_boardManagerIsRecapOpenProp?.GetValue(__instance) is bool bp) __state = bp;
            else if (_boardManagerIsRecapOpenField?.GetValue(__instance) is bool bf) __state = bf;
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CustomContentVisualModule:L1450", diagEx); }
    }

    private static void BoardManagerHideRecapViewPostfix(object __instance, bool __state)
    {
        try
        {
            if (!__state || __instance == null) return;
            bool nowOpen = false;
            if (_boardManagerIsRecapOpenProp?.GetValue(__instance) is bool bp) nowOpen = bp;
            else if (_boardManagerIsRecapOpenField?.GetValue(__instance) is bool bf) nowOpen = bf;
            if (nowOpen) return;

            var agents = UnityEngine.Object.FindObjectsByType<CardVisualOverrideAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (agents == null) return;
            for (int i = 0; i < agents.Length; i++)
            {
                var ag = agents[i];
                if (ag == null || !ag.IsRecapPooledHost) continue;
                ag.RestoreAllKnownSnapshots();
                ag.ClearContext();
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(BoardManagerHideRecapViewPostfix), ex);
        }
    }

    private static void ItemVisualsSetMaterialPostfix(Component __instance)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = __instance.GetComponent<CardVisualOverrideAgent>();
            if (agent == null || !agent.HasContext) return;
            TryApplyItemVisual(__instance, agent, agent.Epoch);
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(ItemVisualsSetMaterialPostfix), ex);
        }
    }

    private static bool LiveCardMatchesAgent(Component visualsComp, CardVisualOverrideAgent agent)
    {
        if (visualsComp == null || agent == null || !agent.HasContext) return false;
        object liveCard = ResolveLiveCardFromVisuals(visualsComp) ?? agent.BoundCardObj;
        if (liveCard == null) return true;

        var liveQuery = BuildQueryFromCard(liveCard, CardVisualTarget.ItemArt);
        if (!string.IsNullOrEmpty(liveQuery.InstanceId) && !string.IsNullOrEmpty(agent.LastQuery.InstanceId)
            && !string.Equals(liveQuery.InstanceId, agent.LastQuery.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!string.IsNullOrEmpty(liveQuery.TemplateId) && !string.IsNullOrEmpty(agent.LastQuery.TemplateId)
            && !string.Equals(liveQuery.TemplateId, agent.LastQuery.TemplateId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static object ResolveLiveCardFromVisuals(Component visualsComp)
    {
        if (visualsComp == null) return null;
        try
        {
            object itemCtrl = _itemControllerField?.GetValue(visualsComp);
            if (itemCtrl == null) return null;
            var prop = _itemControllerCardDataPropByType.GetOrAdd(
                itemCtrl.GetType(),
                static t => t.GetProperty("CardData", BindingFlags.Public | BindingFlags.Instance));
            return prop?.GetValue(itemCtrl);
        }
        catch
        {
            return null;
        }
    }

    private static void TryApplyItemVisual(Component visualsComp, CardVisualOverrideAgent agent, uint capturedEpoch)
    {
        try
        {
            if (visualsComp == null || agent == null || !agent.HasContext || agent.Epoch != capturedEpoch || Instance == null) return;
            if (!LiveCardMatchesAgent(visualsComp, agent)) return;

            Material mat = _itemMatInstanceField?.GetValue(visualsComp) as Material;
            if (!Instance.TryResolveVisual(agent.LastQuery, out var binding) || binding == null)
            {
                if (mat != null)
                {
                    agent.RestoreOriginalMaterialIfApplied(mat);
                }
                return;
            }

            if (mat == null) return;

            int matId = mat.GetInstanceID();
            if (agent.IsAlreadyAppliedTo(matId, binding.LocalImagePath)) return;

            CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, incomingLease =>
            {
                TextureLease<CardTextureCache.CacheEntry> lease = incomingLease;
                try
                {
                    if (lease == null || lease.Asset == null || lease.Asset.Texture == null) return;
                    if (visualsComp == null || agent == null || !agent.HasContext || agent.Epoch != capturedEpoch
                        || !LiveCardMatchesAgent(visualsComp, agent))
                    {
                        return;
                    }

                    Material currentMat = _itemMatInstanceField?.GetValue(visualsComp) as Material;
                    if (currentMat == null) return;

                    int curMatId = currentMat.GetInstanceID();
                    if (agent.IsAlreadyAppliedTo(curMatId, binding.LocalImagePath)) return;

                    agent.EnsureOriginalMaterialSnapshot(currentMat);

                    if (CardTextureCache.Instance.ApplyTextureToUrpMaterial(currentMat, lease.Asset.Texture, binding.DisableParallaxPremium))
                    {
                        agent.TakePrimaryLease(ref lease);
                        agent.MarkApplied(curMatId, binding.LocalImagePath);
                    }
                }
                catch (Exception ex)
                {
                    LogHookErrorThrottled("TryApplyItemVisual.Callback", ex);
                }
                finally
                {
                    lease?.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(TryApplyItemVisual), ex);
        }
    }

    // ── 通路 2 回调：3D 技能圆牌（P0-23 落盘瞬间拦截 + P0-29 Prefix 还原）─────

    private static void SkillIconUpdaterMaterialPrefix(Component __instance, ref Texture __0)
    {
        try
        {
            if (__instance == null || Instance == null) return;

            Component host = FindSkillHostForUpdater(__instance);
            if (host == null) return;
            var agent = host.GetComponent<CardVisualOverrideAgent>();
            if (agent == null) return;

            if (!_inModSkillIconApply)
            {
                agent.SaveOriginalSkillTexture(__instance, __0);
            }

            if (_inModSkillIconApply || !agent.HasContext || agent.LastQuery.Target != CardVisualTarget.SkillIcon)
                return;

            if (!Instance.TryResolveVisual(agent.LastQuery, out var binding) || binding == null)
                return;

            uint capturedEpoch = agent.Epoch;
            Texture syncTex = null;
            bool finishedSync = false;

            CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, incomingLease =>
            {
                TextureLease<CardTextureCache.CacheEntry> lease = incomingLease;
                try
                {
                    if (lease == null || lease.Asset == null || lease.Asset.Texture == null) return;
                    if (__instance == null || agent == null || !agent.HasContext || agent.Epoch != capturedEpoch) return;

                    if (!finishedSync)
                    {
                        // 缓存已热命中：同步在落盘瞬间零延迟替换 ref Texture __0
                        syncTex = lease.Asset.Texture;
                        agent.TakePrimaryLease(ref lease);
                    }
                    else
                    {
                        // 异步加载完成回主线程：在防重入标记下补调 UpdateSkillIconMaterial
                        agent.TakePrimaryLease(ref lease);
                        ApplySkillIconToUpdaterSafe(__instance, lease.Asset.Texture);
                    }
                }
                catch (Exception ex)
                {
                    LogHookErrorThrottled("SkillIconUpdaterMaterialPrefix.Callback", ex);
                }
                finally
                {
                    lease?.Dispose();
                }
            });

            finishedSync = true;
            if (syncTex != null)
            {
                __0 = syncTex;
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(SkillIconUpdaterMaterialPrefix), ex);
        }
    }

    private static Component FindSkillHostForUpdater(Component updater)
    {
        if (updater == null) return null;
        if (_skillControllerType != null)
        {
            var sc = updater.GetComponentInParent(_skillControllerType);
            if (sc != null) return sc;
        }
        if (_skillProxyRendererType != null)
        {
            var sp = updater.GetComponentInParent(_skillProxyRendererType);
            if (sp != null) return sp;
        }
        return updater.GetComponentInParent<CardVisualOverrideAgent>()?.HostController;
    }

    private static void ApplySkillIconToUpdaterSafe(Component updater, Texture tex)
    {
        if (updater == null || tex == null) return;
        var m = _skillUpdaterMethodByType.GetOrAdd(
            updater.GetType(),
            static ut => ut.GetMethod("UpdateSkillIconMaterial", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Texture) }, null));
        if (m == null) return;

        bool prev = _inModSkillIconApply;
        _inModSkillIconApply = true;
        try
        {
            m.Invoke(updater, new object[] { tex });
        }
        finally
        {
            _inModSkillIconApply = prev;
        }
    }

    private static void SkillUpdateIconPostfix(Component __instance)
    {
        try
        {
            if (__instance == null) return;
            var ag = __instance.GetComponent<CardVisualOverrideAgent>();
            ag?.MarkBadgeDirty();
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CustomContentVisualModule:L1711", diagEx); }
    }

    private static void SkillControllerCleanupPrefix(Component __instance)
    {
        try
        {
            if (__instance == null) return;
            var agent = __instance.GetComponent<CardVisualOverrideAgent>();
            if (agent != null)
            {
                agent.RestoreOriginalSkillTextureIfApplied();
                agent.ClearContext();
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(SkillControllerCleanupPrefix), ex);
        }
    }

    private static void SkillControllerSetupPostfix(Component __instance, object __1, Task __result)
    {
        try
        {
            ScheduleSkillVisual(__instance, __1, __result);
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(SkillControllerSetupPostfix), ex);
        }
    }

    private static void SkillControllerUpdateCardPostfix(Component __instance, object __0, Task __result)
    {
        try
        {
            ScheduleSkillVisual(__instance, __0, __result);
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(SkillControllerUpdateCardPostfix), ex);
        }
    }

    private static void SkillProxyInitializePostfix(Component __instance, object __0)
    {
        try
        {
            ScheduleSkillVisual(__instance, __0, null);
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(SkillProxyInitializePostfix), ex);
        }
    }

    private static void ScheduleSkillVisual(Component skillComp, object cardObj, Task pendingTask)
    {
        if (skillComp == null || cardObj == null || Instance == null) return;
        var agent = GetOrAddAgent(skillComp);
        agent.HostController = skillComp;
        agent.BoundCardObj = cardObj;
        var query = BuildQueryFromCard(cardObj, CardVisualTarget.SkillIcon);
        agent.SetContext(query);
        uint epoch = agent.Epoch;

        void ApplyAction() => TryApplySkillVisual(skillComp, agent, epoch, triggerSyncIfMissing: true);

        if (pendingTask != null && !pendingTask.IsCompleted)
        {
            _ = pendingTask.ContinueWith(_ => MainThreadDispatcher.Enqueue(ApplyAction), TaskScheduler.Default);
        }
        else
        {
            ApplyAction();
        }
    }

    private static void TryApplySkillVisual(Component skillComp, CardVisualOverrideAgent agent, uint epoch, bool triggerSyncIfMissing)
    {
        try
        {
            if (skillComp == null || agent == null || agent.Epoch != epoch || Instance == null) return;
            var query = agent.LastQuery;
            if (!Instance.TryResolveVisual(query, out var binding) || binding == null)
            {
                agent.RestoreOriginalSkillTextureIfApplied();
                if (triggerSyncIfMissing && !string.IsNullOrEmpty(query.InstanceId))
                {
                    ModContentSyncClient.Instance?.NotifyUnboundInstanceSeen(query.InstanceId, query.TemplateId);
                }
                return;
            }

            CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, incomingLease =>
            {
                TextureLease<CardTextureCache.CacheEntry> lease = incomingLease;
                try
                {
                    if (lease == null || lease.Asset == null || lease.Asset.Texture == null) return;
                    if (skillComp == null || agent == null || agent.Epoch != epoch) return;

                    if (_skillIconUpdaterType != null)
                    {
                        var updater = skillComp.GetComponentInChildren(_skillIconUpdaterType, true);
                        if (updater != null)
                        {
                            ApplySkillIconToUpdaterSafe(updater, lease.Asset.Texture);
                            agent.TakePrimaryLease(ref lease);
                            return;
                        }
                    }

                    if (_skillUpdateIconMethod != null && _skillUpdateIconMethod.DeclaringType.IsInstanceOfType(skillComp))
                    {
                        bool prev = _inModSkillIconApply;
                        _inModSkillIconApply = true;
                        try
                        {
                            _skillUpdateIconMethod.Invoke(skillComp, new object[] { lease.Asset.Texture });
                        }
                        finally
                        {
                            _inModSkillIconApply = prev;
                        }
                        agent.TakePrimaryLease(ref lease);
                    }
                }
                catch (Exception ex)
                {
                    LogHookErrorThrottled("TryApplySkillVisual.Callback", ex);
                }
                finally
                {
                    lease?.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(TryApplySkillVisual), ex);
        }
    }

    // ── 通路 3 回调：2D UI 预览（P0-26 4 参 SetUp 上下文穿透） ───────────────

    private static void CardPreviewBaseSetUpPrefix(Component __instance, object __0, object __2, out PreviewCardContext __state)
    {
        __state = null;
        try
        {
            PreviewCardContext ctx = null;
            if (__2 != null && PreviewContextMap.TryGetValue(__2, out var fromInst))
            {
                ctx = fromInst;
            }
            else if (__2 != null)
            {
                var bag = GetCardPropBag(__2.GetType());
                string iid = SafeGetPropertyValue(bag.InstanceId, __2)?.ToString();
                string tplId = SafeGetPropertyValue(bag.TemplateId, __2)?.ToString() ?? ReadTemplateId(__0);
                if (!string.IsNullOrEmpty(iid))
                {
                    if (LocalizationOverlayModule.Instance != null
                        && LocalizationOverlayModule.Instance.TryResolveCardMetadata(iid, null, tplId, out var ov)
                        && !string.IsNullOrWhiteSpace(ov?.CustomCardId))
                    {
                        ctx = new PreviewCardContext(ov.CustomCardId, tplId, ov.ModId);
                    }
                    else if (Instance != null && Instance.TryLookupCustomIdentity(iid, tplId, out string cid, out _))
                    {
                        ctx = new PreviewCardContext(cid, tplId, string.Empty);
                    }
                }
            }

            if (ctx != null)
            {
                __state = ctx;
                _activePreviewSetupContext = ctx;
                if (__instance != null) PreviewContextMap.AddOrUpdate(__instance, ctx);
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardPreviewBaseSetUpPrefix), ex);
        }
    }

    private static void CardPreviewBaseSetUpPostfix(Component __instance, object __2, PreviewCardContext __state, ref Task __result)
    {
        _activePreviewSetupContext = null;
        try
        {
            if (__instance == null || __state == null) return;
            PreviewContextMap.AddOrUpdate(__instance, __state);
            object clientCard = _previewClientCardField?.GetValue(__instance);
            if (clientCard != null)
            {
                PreviewContextMap.AddOrUpdate(clientCard, __state);
            }

            if (__result != null)
            {
                __result = ContinueApplyPreviewAfterSetUpAsync(__result, __instance, __state);
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardPreviewBaseSetUpPostfix), ex);
        }
    }

    private static async Task ContinueApplyPreviewAfterSetUpAsync(Task innerTask, Component previewComp, PreviewCardContext ctx)
    {
        if (innerTask != null)
        {
            await innerTask;
        }
        if (previewComp == null || ctx == null) return;
        MainThreadDispatcher.Enqueue(() =>
        {
            if (previewComp == null || Instance == null) return;
            PreviewContextMap.AddOrUpdate(previewComp, ctx);
            object clientCard = _previewClientCardField?.GetValue(previewComp);
            if (clientCard != null) PreviewContextMap.AddOrUpdate(clientCard, ctx);

            var ag = GetOrAddAgent(previewComp);
            ag.HostController = previewComp;
            if (_previewItemType != null && _previewItemType.IsInstanceOfType(previewComp))
            {
                ag.SetContext(BuildQueryFromPreview(previewComp, CardVisualTarget.ItemArt));
                TryApplyPreviewItemVisual(previewComp, ag, ag.Epoch, triggerSyncIfMissing: false);
            }
            else if (_previewSkillType != null && _previewSkillType.IsInstanceOfType(previewComp))
            {
                ag.SetContext(BuildQueryFromPreview(previewComp, CardVisualTarget.SkillIcon));
                TryApplyPreviewSkillVisual(previewComp, ag, ag.Epoch, triggerSyncIfMissing: false);
            }
            else if (_previewEncounterType != null && _previewEncounterType.IsInstanceOfType(previewComp))
            {
                ag.SetContext(BuildQueryFromPreview(previewComp, CardVisualTarget.EncounterPortrait));
                TryApplyPreviewEncounterVisual(previewComp, ag, ag.Epoch, triggerSyncIfMissing: false);
            }
        });
    }

    private static void CardPreviewItemPrefix(Component __instance)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            agent.SetContext(BuildQueryFromPreview(__instance, CardVisualTarget.ItemArt));
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardPreviewItemPrefix), ex);
        }
    }

    private static void CardPreviewItemPostfix(Component __instance, Task __result)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            var query = BuildQueryFromPreview(__instance, CardVisualTarget.ItemArt);
            if (!agent.HasContext || !agent.MatchesQuery(in query))
            {
                agent.SetContext(query);
            }
            uint epoch = agent.Epoch;

            void ApplyAction() => TryApplyPreviewItemVisual(__instance, agent, epoch, triggerSyncIfMissing: true);

            if (__result != null && !__result.IsCompleted)
                _ = __result.ContinueWith(_ => MainThreadDispatcher.Enqueue(ApplyAction), TaskScheduler.Default);
            else
                ApplyAction();
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardPreviewItemPostfix), ex);
        }
    }

    private static void TryApplyPreviewItemVisual(Component previewComp, CardVisualOverrideAgent agent, uint epoch, bool triggerSyncIfMissing)
    {
        try
        {
            if (previewComp == null || agent == null || agent.Epoch != epoch || Instance == null) return;
            var query = agent.LastQuery;
            if (!Instance.TryResolveVisual(query, out var binding) || binding == null)
            {
                agent.RestorePreviewVisualIfApplied(previewComp);
                if (triggerSyncIfMissing && !string.IsNullOrEmpty(query.InstanceId))
                {
                    ModContentSyncClient.Instance?.NotifyUnboundInstanceSeen(query.InstanceId, query.TemplateId);
                }
                return;
            }

            CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, incomingLease =>
            {
                TextureLease<CardTextureCache.CacheEntry> lease = incomingLease;
                try
                {
                    if (lease == null || lease.Asset == null || lease.Asset.Texture == null) return;
                    if (previewComp == null || agent == null || agent.Epoch != epoch) return;

                    Material mat = _previewItemMaterialField?.GetValue(previewComp) as Material;
                    if (mat != null)
                    {
                        agent.EnsureOriginalMaterialSnapshot(mat);
                        if (CardTextureCache.Instance.ApplyTextureToUrpMaterial(mat, lease.Asset.Texture, binding.DisableParallaxPremium))
                        {
                            agent.TakePrimaryLease(ref lease);
                            return;
                        }
                    }

                    RawImage rawImg = _previewCardImageField?.GetValue(previewComp) as RawImage;
                    if (rawImg != null)
                    {
                        agent.EnsureOriginalPreviewRawImage(rawImg);
                        rawImg.texture = lease.Asset.Texture;
                        agent.TakePrimaryLease(ref lease);
                    }
                }
                catch (Exception ex)
                {
                    LogHookErrorThrottled("TryApplyPreviewItemVisual.Callback", ex);
                }
                finally
                {
                    lease?.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(TryApplyPreviewItemVisual), ex);
        }
    }

    private static void CardPreviewSkillPrefix(Component __instance)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            agent.SetContext(BuildQueryFromPreview(__instance, CardVisualTarget.SkillIcon));
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardPreviewSkillPrefix), ex);
        }
    }

    private static void CardPreviewSkillPostfix(Component __instance, Task __result)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            var query = BuildQueryFromPreview(__instance, CardVisualTarget.SkillIcon);
            if (!agent.HasContext || !agent.MatchesQuery(in query))
            {
                agent.SetContext(query);
            }
            uint epoch = agent.Epoch;

            void ApplyAction() => TryApplyPreviewSkillVisual(__instance, agent, epoch, triggerSyncIfMissing: true);

            if (__result != null && !__result.IsCompleted)
                _ = __result.ContinueWith(_ => MainThreadDispatcher.Enqueue(ApplyAction), TaskScheduler.Default);
            else
                ApplyAction();
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardPreviewSkillPostfix), ex);
        }
    }

    private static void TryApplyPreviewSkillVisual(Component previewComp, CardVisualOverrideAgent agent, uint epoch, bool triggerSyncIfMissing)
    {
        try
        {
            if (previewComp == null || agent == null || agent.Epoch != epoch || Instance == null) return;
            var query = agent.LastQuery;
            if (!Instance.TryResolveVisual(query, out var binding) || binding == null)
            {
                agent.RestorePreviewVisualIfApplied(previewComp);
                if (triggerSyncIfMissing && !string.IsNullOrEmpty(query.InstanceId))
                {
                    ModContentSyncClient.Instance?.NotifyUnboundInstanceSeen(query.InstanceId, query.TemplateId);
                }
                return;
            }

            CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, incomingLease =>
            {
                TextureLease<CardTextureCache.CacheEntry> lease = incomingLease;
                try
                {
                    if (lease == null || lease.Asset == null || lease.Asset.Texture == null) return;
                    if (previewComp == null || agent == null || agent.Epoch != epoch) return;

                    RawImage rawImg = _previewCardImageField?.GetValue(previewComp) as RawImage;
                    if (rawImg != null)
                    {
                        agent.EnsureOriginalPreviewRawImage(rawImg);
                        rawImg.texture = lease.Asset.Texture;
                        agent.TakePrimaryLease(ref lease);
                    }
                }
                catch (Exception ex)
                {
                    LogHookErrorThrottled("TryApplyPreviewSkillVisual.Callback", ex);
                }
                finally
                {
                    lease?.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(TryApplyPreviewSkillVisual), ex);
        }
    }

    private static void CardPreviewEncounterPrefix(Component __instance)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            agent.SetContext(BuildQueryFromPreview(__instance, CardVisualTarget.EncounterPortrait));
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardPreviewEncounterPrefix), ex);
        }
    }

    private static void CardPreviewEncounterPostfix(Component __instance, Task __result)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            var query = BuildQueryFromPreview(__instance, CardVisualTarget.EncounterPortrait);
            if (!agent.HasContext || !agent.MatchesQuery(in query))
            {
                agent.SetContext(query);
            }
            uint epoch = agent.Epoch;

            void ApplyAction() => TryApplyPreviewEncounterVisual(__instance, agent, epoch, triggerSyncIfMissing: true);

            if (__result != null && !__result.IsCompleted)
                _ = __result.ContinueWith(_ => MainThreadDispatcher.Enqueue(ApplyAction), TaskScheduler.Default);
            else
                ApplyAction();
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(CardPreviewEncounterPostfix), ex);
        }
    }

    private static void TryApplyPreviewEncounterVisual(Component previewComp, CardVisualOverrideAgent agent, uint epoch, bool triggerSyncIfMissing)
    {
        try
        {
            if (previewComp == null || agent == null || agent.Epoch != epoch || Instance == null) return;
            var query = agent.LastQuery;
            if (!Instance.TryResolveVisual(query, out var binding) || binding == null)
            {
                agent.RestorePreviewVisualIfApplied(previewComp);
                if (triggerSyncIfMissing && !string.IsNullOrEmpty(query.InstanceId))
                {
                    ModContentSyncClient.Instance?.NotifyUnboundInstanceSeen(query.InstanceId, query.TemplateId);
                }
                return;
            }

            CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, incomingLease =>
            {
                TextureLease<CardTextureCache.CacheEntry> lease = incomingLease;
                try
                {
                    if (lease == null || lease.Asset == null || lease.Asset.Texture == null) return;
                    if (previewComp == null || agent == null || agent.Epoch != epoch) return;

                    Image portraitImg = _previewEncounterPortraitField?.GetValue(previewComp) as Image;
                    Sprite sp = CardTextureCache.Instance.GetOrCreateSpriteOnMain(lease);
                    if (portraitImg != null && sp != null)
                    {
                        agent.EnsureOriginalPreviewImage(portraitImg);
                        portraitImg.sprite = sp;
                        portraitImg.enabled = true;
                        agent.TakePrimaryLease(ref lease);
                    }
                }
                catch (Exception ex)
                {
                    LogHookErrorThrottled("TryApplyPreviewEncounterVisual.Callback", ex);
                }
                finally
                {
                    lease?.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(TryApplyPreviewEncounterVisual), ex);
        }
    }

    // ── 通路 4 回调：遭遇卡与战斗立绘（P0-24 + D4-P1-8 MPB + P0-29 Prefix）──

    private static void EncounterControllerSetupPostfix(Component __instance, object __0, Task __result)
    {
        try
        {
            if (__instance == null || __0 == null || Instance == null) return;
            ScheduleEncounterVisual(__instance, __0, __result);
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(EncounterControllerSetupPostfix), ex);
        }
    }

    private static void EncounterControllerSetupEncounterPostfix(Component __instance, Task __result)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            object cardObj = _encounterCardDataProp?.GetValue(__instance)
                ?? __instance.GetComponent<CardVisualOverrideAgent>()?.BoundCardObj;
            if (cardObj == null) return;
            ScheduleEncounterVisual(__instance, cardObj, __result);
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(EncounterControllerSetupEncounterPostfix), ex);
        }
    }

    private static void ScheduleEncounterVisual(Component encComp, object cardObj, Task pendingTask)
    {
        var agent = GetOrAddAgent(encComp);
        agent.HostController = encComp;
        agent.BoundCardObj = cardObj;
        var portraitQuery = BuildQueryFromCard(cardObj, CardVisualTarget.EncounterPortrait);
        if (!agent.HasContext || !agent.MatchesQuery(in portraitQuery))
        {
            agent.SetContext(portraitQuery);
        }
        uint epoch = agent.Epoch;

        void ApplyAction() => TryApplyEncounterVisual(encComp, agent, epoch, triggerSyncIfMissing: true);

        if (pendingTask != null && !pendingTask.IsCompleted)
            _ = pendingTask.ContinueWith(_ => MainThreadDispatcher.Enqueue(ApplyAction), TaskScheduler.Default);
        else
            ApplyAction();
    }

    private static void EncounterControllerCleanupPrefix(Component __instance)
    {
        try
        {
            if (__instance == null) return;
            var agent = __instance.GetComponent<CardVisualOverrideAgent>();
            if (agent != null)
            {
                agent.RestoreOriginalEncounterVisuals(__instance);
                agent.ClearContext();
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(EncounterControllerCleanupPrefix), ex);
        }
    }

    private static void TryApplyEncounterVisual(Component encComp, CardVisualOverrideAgent agent, uint epoch, bool triggerSyncIfMissing)
    {
        try
        {
            if (encComp == null || agent == null || agent.Epoch != epoch || Instance == null) return;

            var portraitQuery = agent.LastQuery;
            var bgQuery = new CardVisualQuery(
                portraitQuery.InstanceId,
                portraitQuery.CustomCardId,
                portraitQuery.TemplateId,
                portraitQuery.Tier,
                portraitQuery.Enchantment,
                CardVisualTarget.EncounterBackground);

            bool matchedAny = false;

            if (Instance.TryResolveVisual(portraitQuery, out var pBinding) && pBinding != null)
            {
                matchedAny = true;
                CardTextureCache.Instance.AcquireTextureAsync(pBinding.LocalImagePath, pBinding.ContentSha256, incomingLease =>
                {
                    TextureLease<CardTextureCache.CacheEntry> lease = incomingLease;
                    try
                    {
                        if (lease == null || lease.Asset == null || lease.Asset.Texture == null) return;
                        if (encComp == null || agent == null || agent.Epoch != epoch) return;

                        SpriteRenderer sr = _encounterPortraitRendererField?.GetValue(encComp) as SpriteRenderer;
                        Sprite sp = CardTextureCache.Instance.GetOrCreateSpriteOnMain(lease);
                        if (sr != null && sp != null)
                        {
                            GameObject animGo = _encounterAnimatedPortraitField?.GetValue(encComp) as GameObject;
                            agent.EnsureOriginalEncounterPortraitSnapshot(sr, animGo);

                            sr.sprite = sp;
                            // P0-24：确保静态立绘节点处于激活态，避免原版 Spine 激活分支曾将 portrait 隐藏导致不可见
                            if (!sr.gameObject.activeSelf) sr.gameObject.SetActive(true);
                            sr.enabled = true;
                            if (animGo != null)
                            {
                                animGo.SetActive(false);
                            }
                            agent.TakePrimaryLease(ref lease);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogHookErrorThrottled("TryApplyEncounterVisual.PortraitCallback", ex);
                    }
                    finally
                    {
                        lease?.Dispose();
                    }
                });
            }

            if (Instance.TryResolveVisual(bgQuery, out var bgBinding) && bgBinding != null)
            {
                matchedAny = true;
                CardTextureCache.Instance.AcquireTextureAsync(bgBinding.LocalImagePath, bgBinding.ContentSha256, incomingLease =>
                {
                    TextureLease<CardTextureCache.CacheEntry> lease = incomingLease;
                    try
                    {
                        if (lease == null || lease.Asset == null || lease.Asset.Texture == null) return;
                        if (encComp == null || agent == null || agent.Epoch != epoch) return;

                        // D4-P1-8：优先通过 MaterialPropertyBlock 写 backgroundRenderer，严禁访问 bgMr.material 触发隐式材质克隆显存泄漏！
                        if (_encounterBackgroundRendererField?.GetValue(encComp) is Renderer bgRenderer && bgRenderer != null)
                        {
                            var mpb = agent.GetOrCreateEncounterBgMpb();
                            bgRenderer.GetPropertyBlock(mpb);
                            mpb.SetTexture(BaseMapPropId, lease.Asset.Texture);
                            mpb.SetTexture(MainTexPropId, lease.Asset.Texture);
                            bgRenderer.SetPropertyBlock(mpb);
                            agent.TrackEncounterBgRenderer(bgRenderer);
                            agent.TakeSecondaryLease(ref lease);
                            return;
                        }

                        Material bgMat = _encounterInstancedBgMatField?.GetValue(encComp) as Material;
                        if (bgMat != null)
                        {
                            agent.EnsureOriginalMaterialSnapshot(bgMat);
                            if (bgMat.HasProperty(BaseMapPropId)) bgMat.SetTexture(BaseMapPropId, lease.Asset.Texture);
                            else bgMat.mainTexture = lease.Asset.Texture;
                            agent.TakeSecondaryLease(ref lease);
                            return;
                        }

                        if (_cardFrameUpdateBgMethod != null)
                        {
                            var frameComp = encComp.GetComponentInChildren(_cardFrameUpdateBgMethod.DeclaringType, true);
                            if (frameComp != null)
                            {
                                _cardFrameUpdateBgMethod.Invoke(frameComp, new object[] { lease.Asset.Texture });
                                agent.TakeSecondaryLease(ref lease);
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogHookErrorThrottled("TryApplyEncounterVisual.BgCallback", ex);
                    }
                    finally
                    {
                        lease?.Dispose();
                    }
                });
            }

            if (!matchedAny)
            {
                agent.RestoreOriginalEncounterVisuals(encComp);
                if (triggerSyncIfMissing && !string.IsNullOrEmpty(portraitQuery.InstanceId))
                {
                    ModContentSyncClient.Instance?.NotifyUnboundInstanceSeen(portraitQuery.InstanceId, portraitQuery.TemplateId);
                }
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(TryApplyEncounterVisual), ex);
        }
    }

    // ── 辅助工具与防异步竞态代理组件 ─────────────────────────────────────────

    private static void LogHookErrorThrottled(string hookName, Exception ex)
    {
        int cnt = Interlocked.Increment(ref _hookErrorCount);
        if (cnt <= 5 || cnt % 50 == 0)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[CustomVisual] {hookName} 已安全拦截异常 (count={cnt}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static CardPropBag GetCardPropBag(Type runtimeType)
    {
        return _cardPropsByType.GetOrAdd(runtimeType, static t => new CardPropBag
        {
            InstanceId = t.GetProperty("InstanceId", BindingFlags.Public | BindingFlags.Instance),
            TemplateId = t.GetProperty("TemplateId", BindingFlags.Public | BindingFlags.Instance),
            Tier = t.GetProperty("Tier", BindingFlags.Public | BindingFlags.Instance),
            Enchantment = t.GetProperty("Enchantment", BindingFlags.Public | BindingFlags.Instance),
            Id = t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance),
            Owner = t.GetProperty("Owner", BindingFlags.Public | BindingFlags.Instance),
            Size = t.GetProperty("Size", BindingFlags.Public | BindingFlags.Instance),
        });
    }

    private static object SafeGetPropertyValue(PropertyInfo prop, object instance)
    {
        if (prop == null || instance == null) return null;
        if (prop.DeclaringType != null && !prop.DeclaringType.IsInstanceOfType(instance)) return null;
        try
        {
            return prop.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static CardVisualOverrideAgent GetOrAddAgent(Component host)
    {
        var existing = host.GetComponent<CardVisualOverrideAgent>();
        var agent = existing != null ? existing : host.gameObject.AddComponent<CardVisualOverrideAgent>();
        agent.ClassifyHostType(host);
        return agent;
    }

    private static CardVisualQuery BuildQueryFromCard(object cardObj, CardVisualTarget target)
    {
        if (cardObj == null) return new CardVisualQuery(null, null, string.Empty, 0, null, target);

        CardPropBag bag = GetCardPropBag(cardObj.GetType());
        string iid = SafeGetPropertyValue(bag.InstanceId, cardObj)?.ToString();
        string customId = TryGetPreviewCustomCardId(cardObj);
        string tplId = SafeGetPropertyValue(bag.TemplateId, cardObj)?.ToString() ?? ReadTemplateId(cardObj);
        int tier = 0;
        try
        {
            object tv = SafeGetPropertyValue(bag.Tier, cardObj);
            if (tv != null) tier = Convert.ToInt32(tv);
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CustomContentVisualModule:L2501", diagEx); }
        string ench = SafeGetPropertyValue(bag.Enchantment, cardObj)?.ToString();

        return new CardVisualQuery(iid, customId, tplId, tier, ench, target);
    }

    private static CardVisualQuery BuildQueryFromPreview(Component previewComp, CardVisualTarget target)
    {
        string customId = TryGetPreviewCustomCardId(previewComp);
        object clientCard = _previewClientCardField?.GetValue(previewComp);
        if (clientCard != null)
        {
            var q = BuildQueryFromCard(clientCard, target);
            if (string.IsNullOrEmpty(q.CustomCardId) && !string.IsNullOrEmpty(customId))
            {
                return new CardVisualQuery(q.InstanceId, customId, q.TemplateId, q.Tier, q.Enchantment, q.Target);
            }
            return q;
        }

        object cardData = _previewCardDataField?.GetValue(previewComp);
        return new CardVisualQuery(null, customId, ReadTemplateId(cardData), 0, null, target);
    }

    private static string ReadTemplateId(object obj)
    {
        if (obj == null) return string.Empty;
        try
        {
            CardPropBag bag = GetCardPropBag(obj.GetType());
            return SafeGetPropertyValue(bag.Id, obj)?.ToString()
                ?? SafeGetPropertyValue(bag.TemplateId, obj)?.ToString()
                ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal sealed class CardVisualOverrideAgent : MonoBehaviour
    {
        private uint _epoch;
        private readonly TextureLeaseSlot<CardTextureCache.CacheEntry> _primarySlot = new();
        private readonly TextureLeaseSlot<CardTextureCache.CacheEntry> _secondarySlot = new();
        private int _lastAppliedMaterialId;
        private string _lastAppliedImagePath;
        private CardTextureCache.MaterialTextureSnapshot _originalMatSnapshot;
        private bool _inTypedSetupSyncEntry;
        private Task _pendingTypedSetupTask;
        private readonly CardMechanismBadgeView _badgeView = new();

        // 管线 2（技能）快照
        private Component _skillUpdaterRef;
        private Texture _originalSkillTex;
        private bool _hasOriginalSkillTex;

        // 管线 3（2D 预览）快照
        private RawImage _previewRawImageRef;
        private Texture _originalPreviewRawTex;
        private bool _hasOriginalPreviewRawTex;
        private Image _previewImageRef;
        private Sprite _originalPreviewSprite;
        private bool _hasOriginalPreviewSprite;

        // 管线 4（遭遇前景 + 背景 MPB）快照
        private SpriteRenderer _encPortraitSr;
        private Sprite _origEncSprite;
        private bool _origEncGoActive;
        private bool _origEncSrEnabled;
        private GameObject _origEncAnimGo;
        private bool _origEncAnimActive;
        private bool _hasOrigEncPortrait;
        private Renderer _encBgRenderer;
        private MaterialPropertyBlock _encBgMpb;

        public uint Epoch => _epoch;
        public bool HasContext { get; private set; }
        public CardVisualQuery LastQuery { get; private set; }
        public Component HostController { get; set; }
        public object BoundCardObj { get; set; }
        public bool IsRecapPooledHost { get; private set; }
        public bool IsPreviewPooledHost { get; private set; }

        public bool IsTypedSetupInFlight =>
            _inTypedSetupSyncEntry || (_pendingTypedSetupTask != null && !_pendingTypedSetupTask.IsCompleted);

        public void ClassifyHostType(Component host)
        {
            if (host == null) return;
            if (_previewBaseType != null && _previewBaseType.IsInstanceOfType(host))
            {
                IsPreviewPooledHost = true;
            }
            if (_recapItemVisualControllerType != null
                && (_recapItemVisualControllerType.IsInstanceOfType(host)
                    || host.GetComponentInParent(_recapItemVisualControllerType) != null))
            {
                IsRecapPooledHost = true;
            }
        }

        public void MarkBadgeDirty() => _badgeView.MarkDirty();

        public void BeginTypedSetup(in CardVisualQuery query)
        {
            _inTypedSetupSyncEntry = true;
            _pendingTypedSetupTask = null;
            SetContext(query);
        }

        public void FinishTypedSetupEntry(Task pendingTask)
        {
            _inTypedSetupSyncEntry = false;
            _pendingTypedSetupTask = (pendingTask != null && !pendingTask.IsCompleted) ? pendingTask : null;
        }

        public bool MatchesQuery(in CardVisualQuery q)
        {
            return HasContext
                && LastQuery.Target == q.Target
                && string.Equals(LastQuery.InstanceId, q.InstanceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(LastQuery.CustomCardId, q.CustomCardId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(LastQuery.TemplateId, q.TemplateId, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsAlreadyAppliedTo(int materialInstanceId, string imagePath)
        {
            return materialInstanceId != 0
                && _lastAppliedMaterialId == materialInstanceId
                && string.Equals(_lastAppliedImagePath, imagePath, StringComparison.OrdinalIgnoreCase);
        }

        public void MarkApplied(int materialInstanceId, string imagePath)
        {
            _lastAppliedMaterialId = materialInstanceId;
            _lastAppliedImagePath = imagePath;
        }

        public void EnsureOriginalMaterialSnapshot(Material currentMat)
        {
            if (currentMat == null) return;
            int id = currentMat.GetInstanceID();
            if (_originalMatSnapshot != null && _originalMatSnapshot.MaterialInstanceId == id) return;
            _originalMatSnapshot = CardTextureCache.Instance.CaptureOriginalMaterialState(currentMat);
        }

        public void RestoreOriginalMaterialIfApplied(Material currentMat)
        {
            if (currentMat != null && _originalMatSnapshot != null
                && _originalMatSnapshot.MaterialInstanceId == currentMat.GetInstanceID())
            {
                CardTextureCache.Instance.RestoreOriginalMaterialState(currentMat, _originalMatSnapshot);
            }
            _lastAppliedMaterialId = 0;
            _lastAppliedImagePath = null;
            ReleaseBoundEntries();
        }

        public void SaveOriginalSkillTexture(Component updater, Texture origTex)
        {
            if (updater == null) return;
            _skillUpdaterRef = updater;
            _originalSkillTex = origTex;
            _hasOriginalSkillTex = true;
        }

        public void RestoreOriginalSkillTextureIfApplied()
        {
            if (_hasOriginalSkillTex && _skillUpdaterRef != null && _originalSkillTex != null)
            {
                try { ApplySkillIconToUpdaterSafe(_skillUpdaterRef, _originalSkillTex); } catch { }
            }
            _hasOriginalSkillTex = false;
            _originalSkillTex = null;
            _skillUpdaterRef = null;
            ReleaseBoundEntries();
        }

        public void EnsureOriginalPreviewRawImage(RawImage rawImg)
        {
            if (rawImg == null) return;
            if (_hasOriginalPreviewRawTex && ReferenceEquals(_previewRawImageRef, rawImg)) return;
            _previewRawImageRef = rawImg;
            _originalPreviewRawTex = rawImg.texture;
            _hasOriginalPreviewRawTex = true;
        }

        public void EnsureOriginalPreviewImage(Image img)
        {
            if (img == null) return;
            if (_hasOriginalPreviewSprite && ReferenceEquals(_previewImageRef, img)) return;
            _previewImageRef = img;
            _originalPreviewSprite = img.sprite;
            _hasOriginalPreviewSprite = true;
        }

        public void RestorePreviewVisualIfApplied(Component previewComp)
        {
            try
            {
                if (previewComp != null && _previewItemMaterialField != null
                    && _previewItemMaterialField.GetValue(previewComp) is Material mat && mat != null)
                {
                    RestoreOriginalMaterialIfApplied(mat);
                }
                if (_hasOriginalPreviewRawTex && _previewRawImageRef != null)
                {
                    _previewRawImageRef.texture = _originalPreviewRawTex;
                }
                if (_hasOriginalPreviewSprite && _previewImageRef != null)
                {
                    _previewImageRef.sprite = _originalPreviewSprite;
                }
            }
            catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CustomContentVisualModule:L2716", diagEx); }
            _hasOriginalPreviewRawTex = false;
            _originalPreviewRawTex = null;
            _previewRawImageRef = null;
            _hasOriginalPreviewSprite = false;
            _originalPreviewSprite = null;
            _previewImageRef = null;
            ReleaseBoundEntries();
        }

        public void EnsureOriginalEncounterPortraitSnapshot(SpriteRenderer sr, GameObject animGo)
        {
            if (sr == null || _hasOrigEncPortrait) return;
            _encPortraitSr = sr;
            _origEncSprite = sr.sprite;
            _origEncGoActive = sr.gameObject.activeSelf;
            _origEncSrEnabled = sr.enabled;
            _origEncAnimGo = animGo;
            _origEncAnimActive = animGo != null && animGo.activeSelf;
            _hasOrigEncPortrait = true;
        }

        public MaterialPropertyBlock GetOrCreateEncounterBgMpb()
        {
            return _encBgMpb ??= new MaterialPropertyBlock();
        }

        public void TrackEncounterBgRenderer(Renderer r)
        {
            _encBgRenderer = r;
        }

        public void RestoreOriginalEncounterVisuals(Component encComp)
        {
            try
            {
                if (_hasOrigEncPortrait && _encPortraitSr != null)
                {
                    _encPortraitSr.sprite = _origEncSprite;
                    _encPortraitSr.enabled = _origEncSrEnabled;
                    _encPortraitSr.gameObject.SetActive(_origEncGoActive);
                    if (_origEncAnimGo != null)
                    {
                        _origEncAnimGo.SetActive(_origEncAnimActive);
                    }
                }
                if (_encBgRenderer != null)
                {
                    _encBgRenderer.SetPropertyBlock(null);
                }
                if (encComp != null && _encounterInstancedBgMatField?.GetValue(encComp) is Material bgMat && bgMat != null)
                {
                    RestoreOriginalMaterialIfApplied(bgMat);
                }
            }
            catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CustomContentVisualModule:L2771", diagEx); }
            _hasOrigEncPortrait = false;
            _encPortraitSr = null;
            _origEncSprite = null;
            _origEncAnimGo = null;
            _encBgRenderer = null;
            ReleaseBoundEntries();
        }

        public void RestoreAllKnownSnapshots()
        {
            if (HostController != null)
            {
                if (_itemVisualsControllerType != null && _itemVisualsControllerType.IsInstanceOfType(HostController))
                {
                    Material mat = _itemMatInstanceField?.GetValue(HostController) as Material;
                    if (mat != null) RestoreOriginalMaterialIfApplied(mat);
                }
                if (IsPreviewPooledHost) RestorePreviewVisualIfApplied(HostController);
                if (_encounterControllerType != null && _encounterControllerType.IsInstanceOfType(HostController))
                {
                    RestoreOriginalEncounterVisuals(HostController);
                }
            }
            RestoreOriginalSkillTextureIfApplied();
        }

        public void SetContext(in CardVisualQuery query)
        {
            _epoch++;
            HasContext = true;
            LastQuery = query;
            _lastAppliedMaterialId = 0;
            _lastAppliedImagePath = null;
            _originalMatSnapshot = null;
            _hasOriginalSkillTex = false;
            _hasOriginalPreviewRawTex = false;
            _hasOriginalPreviewSprite = false;
            _hasOrigEncPortrait = false;
            _badgeView.HideAndClear();
            ReleaseBoundEntries();
        }

        public void ClearContext()
        {
            _epoch++;
            HasContext = false;
            LastQuery = default;
            BoundCardObj = null;
            _inTypedSetupSyncEntry = false;
            _pendingTypedSetupTask = null;
            _lastAppliedMaterialId = 0;
            _lastAppliedImagePath = null;
            _originalMatSnapshot = null;
            _hasOriginalSkillTex = false;
            _originalSkillTex = null;
            _skillUpdaterRef = null;
            _hasOriginalPreviewRawTex = false;
            _originalPreviewRawTex = null;
            _previewRawImageRef = null;
            _hasOriginalPreviewSprite = false;
            _originalPreviewSprite = null;
            _previewImageRef = null;
            _hasOrigEncPortrait = false;
            _encPortraitSr = null;
            _origEncSprite = null;
            _origEncAnimGo = null;
            _encBgRenderer = null;
            _badgeView.HideAndClear();
            ReleaseBoundEntries();
        }

        public void TakePrimaryLease(ref TextureLease<CardTextureCache.CacheEntry> lease)
        {
            _primarySlot.Take(ref lease);
        }

        public void TakeSecondaryLease(ref TextureLease<CardTextureCache.CacheEntry> lease)
        {
            _secondarySlot.Take(ref lease);
        }

        private void LateUpdate()
        {
            try
            {
                if (!HasContext || HostController == null || IsPreviewPooledHost)
                {
                    _badgeView.HideAndClear();
                    return;
                }

                var l10n = LocalizationOverlayModule.Instance;
                if (l10n == null
                    || !l10n.TryResolveCardMetadata(LastQuery.InstanceId, LastQuery.CustomCardId, LastQuery.TemplateId, out var ov)
                    || ov?.CounterDisplays == null || ov.CounterDisplays.Length == 0)
                {
                    _badgeView.HideAndClear();
                    return;
                }

                if (LastQuery.Target == CardVisualTarget.ItemArt
                    && _itemVisualsControllerType != null && _itemVisualsControllerType.IsInstanceOfType(HostController))
                {
                    object liveCard = ResolveLiveCardFromVisuals(HostController) ?? BoundCardObj;
                    if (liveCard == null || !LiveCardMatchesAgent(HostController, this))
                    {
                        _badgeView.HideAndClear();
                        return;
                    }

                    bool backVisible = _itemBackVisibleField?.GetValue(HostController) is bool bv && bv;
                    Renderer artR = _itemArtRendererField?.GetValue(HostController) as Renderer;
                    Transform frameAnchor = _itemFrameAnchorField?.GetValue(HostController) as Transform;
                    object itemCtrlObj = _itemControllerField?.GetValue(HostController);

                    _badgeView.Sync(HostController, itemCtrlObj, artR, frameAnchor, backVisible, liveCard, ov.CounterDisplays);
                }
                else if (LastQuery.Target == CardVisualTarget.SkillIcon || LastQuery.Target == CardVisualTarget.EncounterPortrait)
                {
                    object liveCard = BoundCardObj;
                    if (liveCard == null && _encounterControllerType != null && _encounterControllerType.IsInstanceOfType(HostController))
                    {
                        liveCard = _encounterCardDataProp?.GetValue(HostController);
                    }
                    if (liveCard == null)
                    {
                        _badgeView.HideAndClear();
                        return;
                    }

                    Renderer boundsR = LastQuery.Target == CardVisualTarget.EncounterPortrait
                        ? (_encounterPortraitRendererField?.GetValue(HostController) as Renderer)
                        : null;
                    _badgeView.SyncForAnyHost(HostController, boundsR, null, liveCard, ov.CounterDisplays);
                }
                else
                {
                    _badgeView.HideAndClear();
                }
            }
            catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CustomContentVisualModule:L2912", diagEx); }
        }

        private void OnDisable()
        {
            // P0-29：仅针对池化 UI 宿主（RecapItemVisualController 与 CardPreviewBase）在 OnDisable 归还租约与隐藏徽章；
            // 绝不在普通棋盘/仓库 CardController 上触发（切 Stash 会临时 SetActive(false)，过早清理会引起白块）！
            if (IsPreviewPooledHost || IsRecapPooledHost)
            {
                RestoreAllKnownSnapshots();
                ClearContext();
            }
        }

        private void OnDestroy()
        {
            _badgeView.DestroyAll();
            RestoreAllKnownSnapshots();
            ClearContext();
        }

        private void ReleaseBoundEntries()
        {
            _primarySlot.Clear();
            _secondarySlot.Clear();
        }
    }

    private sealed class CallbackDisposable : IDisposable
    {
        private Action _onDispose;
        public CallbackDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }
}
