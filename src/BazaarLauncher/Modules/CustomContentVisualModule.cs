using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using BazaarModSdk;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarLauncher.Modules;

/// <summary>
/// 自定义 PNG 卡图 4 大渲染通路热替换引擎（实现 ICardVisualOverrideRegistry）：
///   通路 1：3D 棋盘物品与战报回放（ItemVisualsController.Setup / SetCardFrameMaterial / OnDestroy）
///   通路 2：3D 技能圆牌与技能代理（SkillController.Setup / UpdateCard、SkillProxyRenderer.Initialize -> SkillIconUpdater）
///   通路 3：2D UI 预览/图鉴/商店（CardPreviewItem.UpdateCardImageMaterial、CardPreviewSkill.LoadArt、CardPreviewEncounter.LoadArt）
///   通路 4：遭遇卡背景与战斗立绘（EncounterController.Setup、PortraitApplyHelper）
/// 所有异步加载均受 CardVisualOverrideAgent.Epoch 版本号保护（铁律四），失败瞬间回退合法原版底座（0 白块 0 粉块 0 NRE）。
/// </summary>
internal sealed class CustomContentVisualModule : IBuiltInPatchModule, ICardVisualOverrideRegistry
{
    internal static CustomContentVisualModule Instance { get; private set; }

    public ModManifest Manifest { get; } = new()
    {
        Id = "bazaar.visual.custom_content",
        Name = "自定义 PNG 卡图渲染拦截引擎",
        Version = "2.1.0",
        Author = "BazaarModKit",
        Description = "通过 4 大原生渲染通路无缝热替换自定义物品/技能/遭遇 PNG 卡图，严格执行 6 道防白块与显存防泄漏铁律。",
        IsBuiltIn = true,
        DefaultEnabled = true,
        Category = "Visual",
        SupportsHotReload = true,
    };

    public SafeHarmonyPatcher Patcher { get; private set; }

    private readonly object _sync = new();
    private readonly List<CardVisualBinding> _bindings = new();
    private readonly List<(int Priority, Func<CardVisualQuery, CardVisualBinding> Resolver)> _dynamicResolvers = new();

    // 反射缓存（零热路径反射查找）
    private static FieldInfo _itemMatInstanceField;
    private static FieldInfo _itemArtRendererField;
    private static FieldInfo _itemControllerField;
    private static FieldInfo _itemBackVisibleField;
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
    private static MethodInfo _cardFrameUpdateBgMethod;
    private static Type _skillIconUpdaterType;

    // 追踪 ItemVisualsController.Setup(Card/ITCard) 进入深度，识别外部直接调用 Setup(Material) 的池化复用场景
    [ThreadStatic] private static int _itemSetupEntryDepth;

    // 按运行时真实类型隔离缓存卡牌反射属性（彻底杜绝 ItemCard.Enchantment 复用到 SkillCard/Card 抛出 InvalidCastException）
    private sealed class CardPropBag
    {
        public PropertyInfo InstanceId;
        public PropertyInfo TemplateId;
        public PropertyInfo Tier;
        public PropertyInfo Enchantment;
        public PropertyInfo Id;
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
        BazaarLauncherPlugin.Log?.LogInfo(
            $"[CustomVisual] 4 大 PNG 渲染通路拦截已就绪 (成功={Patcher.SucceededCount}/{Patcher.AttemptedCount})");
    }

    public void OnDisable()
    {
        Patcher?.UnpatchAll();
    }

    public void OnUnload()
    {
        OnDisable();
        CardTextureCache.Instance.DisposeAllOnMain();
        if (Instance == this) Instance = null;
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
                catch { }
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

    /// <summary>当 /api/mods/active-bindings 同步返回新实例绑定后，在主线程对已在场的 3D 物品卡立即补刷定制 PNG 贴图。</summary>
    internal void ReapplyActiveVisualsOnMainThread()
    {
        try
        {
            var agents = UnityEngine.Object.FindObjectsByType<CardVisualOverrideAgent>(FindObjectsSortMode.None);
            if (agents == null || agents.Length == 0) return;
            for (int i = 0; i < agents.Length; i++)
            {
                var ag = agents[i];
                if (ag == null || !ag.HasContext || ag.HostController == null) continue;
                if (ag.LastQuery.Target == CardVisualTarget.ItemArt)
                {
                    TryApplyItemVisual(ag.HostController, ag, ag.Epoch);
                }
            }
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(ReapplyActiveVisualsOnMainThread), ex);
        }
    }

    // ── 安装 4 大渲染通路 Harmony 钩子 ───────────────────────────────────────

    private static void InstallFourRenderPipelinePatches(SafeHarmonyPatcher patcher)
    {
        // 通路 1：3D 物品与战报回放 (ItemVisualsController)
        var itemVisType = GameInspector.FindTypeSafe("TheBazaar.Game.CardFrames.ItemVisualsController");
        if (itemVisType != null)
        {
            _itemMatInstanceField = AccessTools.Field(itemVisType, "materialInstance");
            _itemArtRendererField = AccessTools.Field(itemVisType, "cardIllustrationRenderer");
            _itemControllerField = AccessTools.Field(itemVisType, "_itemController");
            _itemBackVisibleField = AccessTools.Field(itemVisType, "_backVisible");

            var cardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.Card");
            // 修正命名空间：真实类型为 BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout，消除启动告警 #5
            var loadoutType = GameInspector.FindTypeSafe(
                "BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout",
                "TheBazaar.BazaarCollectionLoadout");
            if (cardType != null && loadoutType != null)
            {
                // 必须在 Prefix 中先切换上下文并递增值班 Epoch，杜绝 AssetLoader 同步命中已缓存原版材质时
                // SetCardFrameMaterial 先于 Postfix 执行并把上一张池化自定义卡 PNG 误刷到食人鱼/刺刀等原版卡上
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

        // 通路 2：3D 技能圆牌与技能代理 (SkillController / SkillProxyRenderer)
        var skillCtrlType = GameInspector.FindTypeSafe("SkillController", "TheBazaar.SkillController");
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
        }

        _skillIconUpdaterType = GameInspector.FindTypeSafe("SkillIconUpdater", "TheBazaar.SkillIconUpdater");

        var skillProxyType = GameInspector.FindTypeSafe("SkillProxyRenderer", "TheBazaar.SkillProxyRenderer");
        if (skillProxyType != null)
        {
            patcher.TryPatch(
                skillProxyType,
                "Initialize",
                postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(SkillProxyInitializePostfix)),
                optional: true,
                label: "Visual.P2.SkillProxyRenderer.Initialize");
        }

        // 通路 3：2D UI 预览 (CardPreviewItem / CardPreviewSkill / CardPreviewEncounter)
        var previewBase = GameInspector.FindTypeSafe("TheBazaar.UI.CardPreviewBase");
        if (previewBase != null)
        {
            _previewCardDataField = AccessTools.Field(previewBase, "_cardData");
            _previewClientCardField = AccessTools.Field(previewBase, "_clientCard");
            _previewCardImageField = AccessTools.Field(previewBase, "_cardImage");
        }

        var previewItem = GameInspector.FindTypeSafe("TheBazaar.UI.CardPreviewItem");
        if (previewItem != null)
        {
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
            _encounterPortraitRendererField = AccessTools.Field(encCtrl, "portrait");
            _encounterAnimatedPortraitField = AccessTools.Field(encCtrl, "_currentAnimatedPortraitInstance");
            _encounterBackgroundRendererField = AccessTools.Field(encCtrl, "backgroundRenderer");
            _encounterInstancedBgMatField = AccessTools.Field(encCtrl, "instancedBackgroundMaterial");

            var frameType = GameInspector.FindTypeSafe("CardFrameController", "TheBazaar.CardFrameController");
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
        }
    }

    // ── 通路 1 回调：3D 物品与回放 ───────────────────────────────────────────

    private static void ItemVisualsSetupCardPrefix(Component __instance, object __0)
    {
        try
        {
            _itemSetupEntryDepth++;
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
            agent.SetContext(query);
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
            if (_itemSetupEntryDepth > 0) _itemSetupEntryDepth--;
            if (__instance == null || __0 == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            agent.BoundCardObj = __0;
            var query = BuildQueryFromCard(__0, CardVisualTarget.ItemArt);
            if (!agent.HasContext || !agent.MatchesQuery(in query))
            {
                agent.SetContext(query);
            }

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
            _itemSetupEntryDepth++;
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
            agent.SetContext(new CardVisualQuery(null, null, tplId, 0, null, CardVisualTarget.ItemArt));
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
            if (_itemSetupEntryDepth > 0) _itemSetupEntryDepth--;
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
            if (_itemSetupEntryDepth > 0 || __instance == null) return;
            // 未经过 Setup(Card) 或 Setup(ITCard) 直接调用 Setup(Material) 时，必须清空池化视图上可能残留的旧自定义卡上下文
            var agent = __instance.GetComponent<CardVisualOverrideAgent>();
            agent?.ClearContext();
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
            agent?.ClearContext();
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(ItemVisualsCleanupPrefix), ex);
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

        // 第二道铁律防线：对照当前所属 ItemController.CardData（或 BoundCardObj）的实时 InstanceId/TemplateId，
        // 哪怕对象池在极端路径下绕过 Setup(Card)，也绝不允许把旧自定义卡的 PNG 写入原版卡的新材质！
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
            if (!Instance.TryResolveVisual(agent.LastQuery, out var binding) || binding == null) return;

            Material mat = _itemMatInstanceField?.GetValue(visualsComp) as Material;
            if (mat == null) return;

            int matId = mat.GetInstanceID();
            if (agent.IsAlreadyAppliedTo(matId, binding.LocalImagePath)) return;

            CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, entry =>
            {
                if (entry == null || entry.Texture == null) return;
                try
                {
                    if (visualsComp == null || agent == null || !agent.HasContext || agent.Epoch != capturedEpoch
                        || !LiveCardMatchesAgent(visualsComp, agent))
                    {
                        CardTextureCache.Instance.Release(entry);
                        return;
                    }

                    Material currentMat = _itemMatInstanceField?.GetValue(visualsComp) as Material;
                    if (currentMat == null)
                    {
                        CardTextureCache.Instance.Release(entry);
                        return;
                    }

                    int curMatId = currentMat.GetInstanceID();
                    if (agent.IsAlreadyAppliedTo(curMatId, binding.LocalImagePath))
                    {
                        CardTextureCache.Instance.Release(entry);
                        return;
                    }

                    if (CardTextureCache.Instance.ApplyTextureToUrpMaterial(currentMat, entry.Texture, binding.DisableParallaxPremium))
                    {
                        agent.BindTextureEntry(entry);
                        agent.MarkApplied(curMatId, binding.LocalImagePath);
                    }
                    else
                    {
                        CardTextureCache.Instance.Release(entry);
                    }
                }
                catch (Exception ex)
                {
                    CardTextureCache.Instance.Release(entry);
                    LogHookErrorThrottled("TryApplyItemVisual.Callback", ex);
                }
            });
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(TryApplyItemVisual), ex);
        }
    }

    // ── 通路 2 回调：3D 技能圆牌 ────────────────────────────────────────────

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
        agent.SetContext(BuildQueryFromCard(cardObj, CardVisualTarget.SkillIcon));
        uint epoch = agent.Epoch;

        void ApplyAction()
        {
            try
            {
                if (skillComp == null || agent == null || agent.Epoch != epoch || Instance == null) return;
                if (!Instance.TryResolveVisual(agent.LastQuery, out var binding) || binding == null) return;

                CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, entry =>
                {
                    if (entry == null || entry.Texture == null) return;
                    if (skillComp == null || agent == null || agent.Epoch != epoch)
                    {
                        CardTextureCache.Instance.Release(entry);
                        return;
                    }

                    try
                    {
                        if (_skillUpdateIconMethod != null && _skillUpdateIconMethod.DeclaringType.IsInstanceOfType(skillComp))
                        {
                            _skillUpdateIconMethod.Invoke(skillComp, new object[] { entry.Texture });
                            agent.BindTextureEntry(entry);
                            return;
                        }

                        // SkillProxyRenderer 兼容兜底：查找子组件 SkillIconUpdater
                        if (_skillIconUpdaterType != null)
                        {
                            var updater = skillComp.GetComponentInChildren(_skillIconUpdaterType, true);
                            if (updater != null)
                            {
                                var m = _skillUpdaterMethodByType.GetOrAdd(
                                    updater.GetType(),
                                    static ut => ut.GetMethod("UpdateSkillIconMaterial", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Texture) }, null));
                                if (m != null)
                                {
                                    m.Invoke(updater, new object[] { entry.Texture });
                                    agent.BindTextureEntry(entry);
                                    return;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogHookErrorThrottled("ScheduleSkillVisual.Callback", ex);
                    }

                    CardTextureCache.Instance.Release(entry);
                });
            }
            catch (Exception ex)
            {
                LogHookErrorThrottled("ScheduleSkillVisual.ApplyAction", ex);
            }
        }

        if (pendingTask != null && !pendingTask.IsCompleted)
        {
            _ = pendingTask.ContinueWith(_ => MainThreadDispatcher.Enqueue(ApplyAction), TaskScheduler.Default);
        }
        else
        {
            ApplyAction();
        }
    }

    // ── 通路 3 回调：2D UI 预览 (物品/技能/遭遇) ─────────────────────────────

    private static void CardPreviewItemPrefix(Component __instance)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
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
            var query = BuildQueryFromPreview(__instance, CardVisualTarget.ItemArt);
            if (!agent.HasContext || !agent.MatchesQuery(in query))
            {
                agent.SetContext(query);
            }
            uint epoch = agent.Epoch;

            void ApplyAction()
            {
                try
                {
                    if (__instance == null || agent == null || agent.Epoch != epoch || Instance == null) return;
                    if (!Instance.TryResolveVisual(agent.LastQuery, out var binding) || binding == null) return;

                    CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, entry =>
                    {
                        if (entry == null || entry.Texture == null) return;
                        try
                        {
                            if (__instance == null || agent == null || agent.Epoch != epoch)
                            {
                                CardTextureCache.Instance.Release(entry);
                                return;
                            }

                            Material mat = _previewItemMaterialField?.GetValue(__instance) as Material;
                            if (mat != null && CardTextureCache.Instance.ApplyTextureToUrpMaterial(mat, entry.Texture, binding.DisableParallaxPremium))
                            {
                                agent.BindTextureEntry(entry);
                                return;
                            }

                            RawImage rawImg = _previewCardImageField?.GetValue(__instance) as RawImage;
                            if (rawImg != null)
                            {
                                rawImg.texture = entry.Texture;
                                agent.BindTextureEntry(entry);
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogHookErrorThrottled("CardPreviewItem.Callback", ex);
                        }

                        CardTextureCache.Instance.Release(entry);
                    });
                }
                catch (Exception ex)
                {
                    LogHookErrorThrottled("CardPreviewItem.ApplyAction", ex);
                }
            }

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

    private static void CardPreviewSkillPrefix(Component __instance)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
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
            var query = BuildQueryFromPreview(__instance, CardVisualTarget.SkillIcon);
            if (!agent.HasContext || !agent.MatchesQuery(in query))
            {
                agent.SetContext(query);
            }
            uint epoch = agent.Epoch;

            void ApplyAction()
            {
                try
                {
                    if (__instance == null || agent == null || agent.Epoch != epoch || Instance == null) return;
                    if (!Instance.TryResolveVisual(agent.LastQuery, out var binding) || binding == null) return;

                    CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, entry =>
                    {
                        if (entry == null || entry.Texture == null) return;
                        try
                        {
                            if (__instance == null || agent == null || agent.Epoch != epoch)
                            {
                                CardTextureCache.Instance.Release(entry);
                                return;
                            }

                            RawImage rawImg = _previewCardImageField?.GetValue(__instance) as RawImage;
                            if (rawImg != null)
                            {
                                rawImg.texture = entry.Texture;
                                agent.BindTextureEntry(entry);
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogHookErrorThrottled("CardPreviewSkill.Callback", ex);
                        }

                        CardTextureCache.Instance.Release(entry);
                    });
                }
                catch (Exception ex)
                {
                    LogHookErrorThrottled("CardPreviewSkill.ApplyAction", ex);
                }
            }

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

    private static void CardPreviewEncounterPrefix(Component __instance)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
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
            var query = BuildQueryFromPreview(__instance, CardVisualTarget.EncounterPortrait);
            if (!agent.HasContext || !agent.MatchesQuery(in query))
            {
                agent.SetContext(query);
            }
            uint epoch = agent.Epoch;

            void ApplyAction()
            {
                try
                {
                    if (__instance == null || agent == null || agent.Epoch != epoch || Instance == null) return;
                    if (!Instance.TryResolveVisual(agent.LastQuery, out var binding) || binding == null) return;

                    CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, entry =>
                    {
                        if (entry == null || entry.Texture == null) return;
                        try
                        {
                            if (__instance == null || agent == null || agent.Epoch != epoch)
                            {
                                CardTextureCache.Instance.Release(entry);
                                return;
                            }

                            Image portraitImg = _previewEncounterPortraitField?.GetValue(__instance) as Image;
                            Sprite sp = CardTextureCache.Instance.GetOrCreateSpriteOnMain(entry);
                            if (portraitImg != null && sp != null)
                            {
                                portraitImg.sprite = sp;
                                portraitImg.enabled = true;
                                agent.BindTextureEntry(entry);
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogHookErrorThrottled("CardPreviewEncounter.Callback", ex);
                        }

                        CardTextureCache.Instance.Release(entry);
                    });
                }
                catch (Exception ex)
                {
                    LogHookErrorThrottled("CardPreviewEncounter.ApplyAction", ex);
                }
            }

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

    // ── 通路 4 回调：遭遇卡与战斗立绘 ────────────────────────────────────────

    private static void EncounterControllerSetupPostfix(Component __instance, object __0, Task __result)
    {
        try
        {
            if (__instance == null || __0 == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            var portraitQuery = BuildQueryFromCard(__0, CardVisualTarget.EncounterPortrait);
            var bgQuery = BuildQueryFromCard(__0, CardVisualTarget.EncounterBackground);
            agent.SetContext(portraitQuery);
            uint epoch = agent.Epoch;

            void ApplyAction()
            {
                try
                {
                    if (__instance == null || agent == null || agent.Epoch != epoch || Instance == null) return;

                    if (Instance.TryResolveVisual(portraitQuery, out var pBinding) && pBinding != null)
                    {
                        CardTextureCache.Instance.AcquireTextureAsync(pBinding.LocalImagePath, pBinding.ContentSha256, entry =>
                        {
                            if (entry == null || entry.Texture == null) return;
                            try
                            {
                                if (__instance == null || agent == null || agent.Epoch != epoch)
                                {
                                    CardTextureCache.Instance.Release(entry);
                                    return;
                                }

                                SpriteRenderer sr = _encounterPortraitRendererField?.GetValue(__instance) as SpriteRenderer;
                                Sprite sp = CardTextureCache.Instance.GetOrCreateSpriteOnMain(entry);
                                if (sr != null && sp != null)
                                {
                                    sr.sprite = sp;
                                    sr.enabled = true;
                                    // 隐藏可能重叠穿帮的 Spine 动态骨骼立绘
                                    if (_encounterAnimatedPortraitField?.GetValue(__instance) is GameObject animGo && animGo != null)
                                    {
                                        animGo.SetActive(false);
                                    }
                                    agent.BindTextureEntry(entry);
                                    return;
                                }
                            }
                            catch (Exception ex)
                            {
                                LogHookErrorThrottled("EncounterSetup.PortraitCallback", ex);
                            }

                            CardTextureCache.Instance.Release(entry);
                        });
                    }

                    if (Instance.TryResolveVisual(bgQuery, out var bgBinding) && bgBinding != null)
                    {
                        CardTextureCache.Instance.AcquireTextureAsync(bgBinding.LocalImagePath, bgBinding.ContentSha256, entry =>
                        {
                            if (entry == null || entry.Texture == null) return;
                            try
                            {
                                if (__instance == null || agent == null || agent.Epoch != epoch)
                                {
                                    CardTextureCache.Instance.Release(entry);
                                    return;
                                }

                                Material bgMat = _encounterInstancedBgMatField?.GetValue(__instance) as Material;
                                if (bgMat == null && _encounterBackgroundRendererField?.GetValue(__instance) is MeshRenderer bgMr && bgMr != null)
                                {
                                    bgMat = bgMr.material;
                                }
                                if (bgMat != null)
                                {
                                    try
                                    {
                                        if (bgMat.HasProperty("_BaseMap")) bgMat.SetTexture("_BaseMap", entry.Texture);
                                        else bgMat.mainTexture = entry.Texture;
                                        agent.BindSecondaryTextureEntry(entry);
                                        return;
                                    }
                                    catch { }
                                }
                                if (_cardFrameUpdateBgMethod != null)
                                {
                                    var frameComp = __instance.GetComponentInChildren(_cardFrameUpdateBgMethod.DeclaringType, true);
                                    if (frameComp != null)
                                    {
                                        try
                                        {
                                            _cardFrameUpdateBgMethod.Invoke(frameComp, new object[] { entry.Texture });
                                            agent.BindSecondaryTextureEntry(entry);
                                            return;
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                LogHookErrorThrottled("EncounterSetup.BgCallback", ex);
                            }

                            CardTextureCache.Instance.Release(entry);
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogHookErrorThrottled("EncounterSetup.ApplyAction", ex);
                }
            }

            if (__result != null && !__result.IsCompleted)
                _ = __result.ContinueWith(_ => MainThreadDispatcher.Enqueue(ApplyAction), TaskScheduler.Default);
            else
                ApplyAction();
        }
        catch (Exception ex)
        {
            LogHookErrorThrottled(nameof(EncounterControllerSetupPostfix), ex);
        }
    }

    // ── 辅助工具与防异步竞态代理组件（铁律四） ──────────────────────────────

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
        return existing != null ? existing : host.gameObject.AddComponent<CardVisualOverrideAgent>();
    }

    private static CardVisualQuery BuildQueryFromCard(object cardObj, CardVisualTarget target)
    {
        if (cardObj == null) return new CardVisualQuery(null, null, string.Empty, 0, null, target);

        CardPropBag bag = GetCardPropBag(cardObj.GetType());
        string iid = SafeGetPropertyValue(bag.InstanceId, cardObj)?.ToString();
        string tplId = SafeGetPropertyValue(bag.TemplateId, cardObj)?.ToString() ?? ReadTemplateId(cardObj);
        int tier = 0;
        try
        {
            object tv = SafeGetPropertyValue(bag.Tier, cardObj);
            if (tv != null) tier = Convert.ToInt32(tv);
        }
        catch { }
        string ench = SafeGetPropertyValue(bag.Enchantment, cardObj)?.ToString();

        return new CardVisualQuery(iid, null, tplId, tier, ench, target);
    }

    private static CardVisualQuery BuildQueryFromPreview(Component previewComp, CardVisualTarget target)
    {
        object clientCard = _previewClientCardField?.GetValue(previewComp);
        if (clientCard != null)
        {
            return BuildQueryFromCard(clientCard, target);
        }

        object cardData = _previewCardDataField?.GetValue(previewComp);
        return new CardVisualQuery(null, null, ReadTemplateId(cardData), 0, null, target);
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

    /// <summary>挂在对象池视图上的轻量版本号代理：防止池化复用后晚到的异步 PNG 回调污染新卡（铁律四），回收/销毁时彻底清零上下文与引用计数（铁律六），并承载通用自定义机制实时计数胶囊角标。</summary>
    internal sealed class CardVisualOverrideAgent : MonoBehaviour
    {
        private static readonly ConcurrentDictionary<Type, PropertyInfo> AttrsPropCache = new();
        private static readonly Vector3[] CornerBuf = new Vector3[8];
        private static GUIStyle _pillTextStyle;
        private static Texture2D _solidWhiteTex;
        private static Font _cnFont;

        private uint _epoch;
        private CardTextureCache.CacheEntry _primaryEntry;
        private CardTextureCache.CacheEntry _secondaryEntry;
        private int _lastAppliedMaterialId;
        private string _lastAppliedImagePath;
        private readonly Dictionary<string, int> _lastCounterValues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _counterPulseUntil = new(StringComparer.OrdinalIgnoreCase);

        public uint Epoch => _epoch;
        public bool HasContext { get; private set; }
        public CardVisualQuery LastQuery { get; private set; }
        public Component HostController { get; set; }
        public object BoundCardObj { get; set; }

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

        public void SetContext(in CardVisualQuery query)
        {
            _epoch++;
            HasContext = true;
            LastQuery = query;
            _lastAppliedMaterialId = 0;
            _lastAppliedImagePath = null;
            _lastCounterValues.Clear();
            _counterPulseUntil.Clear();
            ReleaseBoundEntries();
        }

        /// <summary>对象池回收（Cleanup）或销毁时彻底清空全部旧卡上下文，严禁跨卡污染。</summary>
        public void ClearContext()
        {
            _epoch++;
            HasContext = false;
            LastQuery = default;
            BoundCardObj = null;
            _lastAppliedMaterialId = 0;
            _lastAppliedImagePath = null;
            _lastCounterValues.Clear();
            _counterPulseUntil.Clear();
            ReleaseBoundEntries();
        }

        public void BindTextureEntry(CardTextureCache.CacheEntry entry)
        {
            if (ReferenceEquals(_primaryEntry, entry)) return;
            CardTextureCache.Instance.Release(_primaryEntry);
            _primaryEntry = entry;
        }

        public void BindSecondaryTextureEntry(CardTextureCache.CacheEntry entry)
        {
            if (ReferenceEquals(_secondaryEntry, entry)) return;
            CardTextureCache.Instance.Release(_secondaryEntry);
            _secondaryEntry = entry;
        }

        private void OnGUI()
        {
            try
            {
                if (!HasContext || LastQuery.Target != CardVisualTarget.ItemArt || HostController == null) return;
                if (Event.current == null || Event.current.type != EventType.Repaint) return;

                var l10n = LocalizationOverlayModule.Instance;
                if (l10n == null) return;
                if (!l10n.TryResolveCardText(l10n.CurrentLocale, LastQuery.InstanceId, LastQuery.CustomCardId, LastQuery.TemplateId, out var ov)
                    || ov?.CounterDisplays == null || ov.CounterDisplays.Length == 0)
                {
                    return;
                }

                // 优先从所属 ItemController.CardData 读取最新活体 Card，避免切场景/战斗切换导致旧引用失效
                object liveCard = ResolveLiveCardFromVisuals(HostController) ?? BoundCardObj;
                if (liveCard == null) return;
                if (!LiveCardMatchesAgent(HostController, this)) return;

                // 卡牌翻面朝下（卡背可见）时自动隐藏角标
                if (_itemBackVisibleField?.GetValue(HostController) is bool backVisible && backVisible)
                {
                    return;
                }

                // 严格取 cardIllustrationRenderer 精确卡画网格，绝不用 GetComponentInChildren<Renderer> 误抓卡背/阴影/外框
                Renderer artR = _itemArtRendererField?.GetValue(HostController) as Renderer;
                if (artR == null || !artR.enabled || !artR.gameObject.activeInHierarchy || !artR.isVisible)
                {
                    return;
                }

                Camera cam = Camera.main;
                if (cam == null) return;

                if (!TryProjectCardArtScreenRect(artR, cam, out float minGuiX, out float maxGuiX, out float minGuiY, out float maxGuiY))
                {
                    return;
                }

                float cardW = maxGuiX - minGuiX;
                float cardH = maxGuiY - minGuiY;
                if (cardW < 24f || cardH < 30f) return;

                var attrsProp = AttrsPropCache.GetOrAdd(
                    liveCard.GetType(),
                    static t => t.GetProperty("Attributes", BindingFlags.Public | BindingFlags.Instance));
                var attrs = attrsProp?.GetValue(liveCard) as IDictionary;

                EnsurePillResources();
                _pillTextStyle.fontSize = cardW < 88f ? 11 : 12;

                // 锚定在 3D 卡画真实屏幕空间的底部安全带（距离顶部伤害/护盾/灼烧/剧毒原生角标保持 >50% 卡高间距）
                float centerX = (minGuiX + maxGuiX) * 0.5f;
                float bottomInset = Mathf.Clamp(cardH * 0.11f, 7f, 16f);
                float baseBottomY = maxGuiY - bottomInset;
                float minSafeTopY = minGuiY + cardH * 0.40f;
                float offsetY = 0f;
                float now = Time.unscaledTime;

                for (int i = 0; i < ov.CounterDisplays.Length; i++)
                {
                    var cd = ov.CounterDisplays[i];
                    if (cd == null || string.IsNullOrWhiteSpace(cd.SyncAttribute)) continue;

                    int curVal = ReadLiveIntAttr(attrs, cd.SyncAttribute);
                    if (_lastCounterValues.TryGetValue(cd.SyncAttribute, out int prevVal))
                    {
                        if (prevVal != curVal)
                        {
                            _lastCounterValues[cd.SyncAttribute] = curVal;
                            _counterPulseUntil[cd.SyncAttribute] = now + 0.40f;
                        }
                    }
                    else
                    {
                        _lastCounterValues[cd.SyncAttribute] = curVal;
                    }

                    if (cd.HideWhenZero && curVal <= 0) continue;

                    bool hasMax = cd.MaxValue.HasValue && cd.MaxValue.Value > 0;
                    bool isReady = hasMax && curVal >= cd.MaxValue.Value;
                    bool isPulsing = _counterPulseUntil.TryGetValue(cd.SyncAttribute, out float pulseUntil) && now < pulseUntil;
                    float pulseBoost = isPulsing ? 2f : 0f;

                    string label = string.IsNullOrWhiteSpace(cd.LabelZh) ? "层数" : cd.LabelZh;
                    string text = hasMax ? $"{label} {curVal}/{cd.MaxValue.Value}" : $"{label} {curVal}";

                    Vector2 sz = _pillTextStyle.CalcSize(new GUIContent(text));
                    float pillW = Mathf.Clamp(sz.x + 14f + pulseBoost * 2f, 52f, Mathf.Max(82f, cardW * 0.94f));
                    float pillH = Mathf.Max(20f, sz.y + 5f + pulseBoost);
                    float pillTopY = Mathf.Max(minSafeTopY, baseBottomY - pillH - offsetY);
                    Rect rect = new Rect(centerX - pillW * 0.5f, pillTopY, pillW, pillH);

                    Color baseAccent = ParseHexColor(cd.ColorHex, new Color(0.98f, 0.45f, 0.09f, 0.96f));
                    Color accent = isReady
                        ? new Color(1f, 0.84f, 0.14f, 1f)
                        : (isPulsing ? Color.Lerp(baseAccent, Color.white, 0.45f) : baseAccent);
                    Color bg = isReady
                        ? new Color(0.26f, 0.14f, 0.02f, 0.94f)
                        : (isPulsing ? new Color(0.14f, 0.12f, 0.22f, 0.94f) : new Color(0.05f, 0.08f, 0.15f, 0.88f));

                    Color prevColor = GUI.color;
                    // 柔暗投影底边增强对比度
                    GUI.color = new Color(0f, 0f, 0f, 0.52f);
                    GUI.DrawTexture(new Rect(rect.x - 1f, rect.y + 1f, rect.width + 2f, rect.height + 2f), _solidWhiteTex);
                    // 外框高光边
                    float border = isPulsing || isReady ? 2f : 1.5f;
                    GUI.color = accent;
                    GUI.DrawTexture(new Rect(rect.x - border, rect.y - border, rect.width + border * 2f, rect.height + border * 2f), _solidWhiteTex);
                    // 内核深底
                    GUI.color = bg;
                    GUI.DrawTexture(rect, _solidWhiteTex);
                    // 亮色文字
                    GUI.color = isReady ? new Color(1f, 0.96f, 0.68f, 1f) : Color.white;
                    GUI.Label(rect, text, _pillTextStyle);
                    GUI.color = prevColor;

                    offsetY += pillH + 3f;
                }
            }
            catch { }
        }

        /// <summary>将 3D 卡画网格的 8 个顶点投影到 GUI 屏幕空间，准确得到无论平铺/悬停/倾斜时的卡顶(minGuiY)与卡底(maxGuiY)。</summary>
        private static bool TryProjectCardArtScreenRect(
            Renderer artRenderer,
            Camera cam,
            out float minGuiX,
            out float maxGuiX,
            out float minGuiY,
            out float maxGuiY)
        {
            minGuiX = float.MaxValue;
            maxGuiX = float.MinValue;
            minGuiY = float.MaxValue;
            maxGuiY = float.MinValue;

            bool usedLocalMesh = false;
            var mf = artRenderer.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Bounds lb = mf.sharedMesh.bounds;
                if (lb.size.sqrMagnitude > 0.00001f)
                {
                    Transform tf = artRenderer.transform;
                    Vector3 lmin = lb.min;
                    Vector3 lmax = lb.max;
                    CornerBuf[0] = tf.TransformPoint(new Vector3(lmin.x, lmin.y, lmin.z));
                    CornerBuf[1] = tf.TransformPoint(new Vector3(lmax.x, lmin.y, lmin.z));
                    CornerBuf[2] = tf.TransformPoint(new Vector3(lmin.x, lmax.y, lmin.z));
                    CornerBuf[3] = tf.TransformPoint(new Vector3(lmax.x, lmax.y, lmin.z));
                    CornerBuf[4] = tf.TransformPoint(new Vector3(lmin.x, lmin.y, lmax.z));
                    CornerBuf[5] = tf.TransformPoint(new Vector3(lmax.x, lmin.y, lmax.z));
                    CornerBuf[6] = tf.TransformPoint(new Vector3(lmin.x, lmax.y, lmax.z));
                    CornerBuf[7] = tf.TransformPoint(new Vector3(lmax.x, lmax.y, lmax.z));
                    usedLocalMesh = true;
                }
            }

            if (!usedLocalMesh)
            {
                Bounds wb = artRenderer.bounds;
                if (wb.size.sqrMagnitude < 0.00001f) return false;
                Vector3 wmin = wb.min;
                Vector3 wmax = wb.max;
                CornerBuf[0] = new Vector3(wmin.x, wmin.y, wmin.z);
                CornerBuf[1] = new Vector3(wmax.x, wmin.y, wmin.z);
                CornerBuf[2] = new Vector3(wmin.x, wmax.y, wmin.z);
                CornerBuf[3] = new Vector3(wmax.x, wmax.y, wmin.z);
                CornerBuf[4] = new Vector3(wmin.x, wmin.y, wmax.z);
                CornerBuf[5] = new Vector3(wmax.x, wmin.y, wmax.z);
                CornerBuf[6] = new Vector3(wmin.x, wmax.y, wmax.z);
                CornerBuf[7] = new Vector3(wmax.x, wmax.y, wmax.z);
            }

            int validCorners = 0;
            float sh = Screen.height;
            for (int i = 0; i < 8; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(CornerBuf[i]);
                if (sp.z <= 0.05f) continue;
                validCorners++;
                float gx = sp.x;
                float gy = sh - sp.y;
                if (gx < minGuiX) minGuiX = gx;
                if (gx > maxGuiX) maxGuiX = gx;
                if (gy < minGuiY) minGuiY = gy;
                if (gy > maxGuiY) maxGuiY = gy;
            }

            if (validCorners < 4) return false;
            if (maxGuiX < 0f || minGuiX > Screen.width || maxGuiY < 0f || minGuiY > sh) return false;
            return true;
        }

        private static int ReadLiveIntAttr(IDictionary attrs, string attrName)
        {
            if (attrs == null || string.IsNullOrEmpty(attrName)) return 0;
            foreach (DictionaryEntry kv in attrs)
            {
                if (string.Equals(kv.Key?.ToString(), attrName, StringComparison.OrdinalIgnoreCase))
                {
                    if (kv.Value != null && double.TryParse(kv.Value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                        return (int)Math.Round(d);
                    return 0;
                }
            }
            return 0;
        }

        private static Color ParseHexColor(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            string s = hex.Trim();
            if (!s.StartsWith("#", StringComparison.Ordinal)) s = "#" + s;
            return ColorUtility.TryParseHtmlString(s, out Color parsed) ? parsed : fallback;
        }

        private static void EnsurePillResources()
        {
            if (_solidWhiteTex == null)
            {
                _solidWhiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _solidWhiteTex.SetPixel(0, 0, Color.white);
                _solidWhiteTex.Apply();
                UnityEngine.Object.DontDestroyOnLoad(_solidWhiteTex);
            }
            if (_pillTextStyle == null)
            {
                try { _cnFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 12); }
                catch { _cnFont = null; }

                _pillTextStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false,
                };
                if (_cnFont != null) _pillTextStyle.font = _cnFont;
                _pillTextStyle.normal.textColor = Color.white;
            }
        }

        private void OnDisable()
        {
            // 注意：切储物箱或进出战斗时 Unity 会临时禁用/启用卡牌物体，绝不可在 OnDisable 清空 BoundCardObj、HasContext 或释放贴图引用，
            // 否则整场战斗都无法显示实时计数角标且储物箱内自定义卡纹理可能被提前驱逐；
            // 对象池回收由 ItemVisualsCleanupPrefix -> ClearContext() 精准清理，销毁由 OnDestroy() 清理。
        }

        private void OnDestroy()
        {
            ClearContext();
        }

        private void ReleaseBoundEntries()
        {
            if (_primaryEntry != null)
            {
                CardTextureCache.Instance.Release(_primaryEntry);
                _primaryEntry = null;
            }
            if (_secondaryEntry != null)
            {
                CardTextureCache.Instance.Release(_secondaryEntry);
                _secondaryEntry = null;
            }
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
