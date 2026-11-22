using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

            var cardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.Card");
            // 修正命名空间：真实类型为 BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout，消除启动告警 #5
            var loadoutType = GameInspector.FindTypeSafe(
                "BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout",
                "TheBazaar.BazaarCollectionLoadout");
            if (cardType != null && loadoutType != null)
            {
                patcher.TryPatch(
                    itemVisType,
                    "Setup",
                    new[] { cardType, loadoutType },
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
                    postfix: AccessTools.Method(typeof(CustomContentVisualModule), nameof(ItemVisualsSetupTemplatePostfix)),
                    optional: true,
                    label: "Visual.P1.ItemVisualsController.Setup(ITCard)");
            }

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

    private static void ItemVisualsSetupCardPostfix(Component __instance, object __0, Task __result)
    {
        try
        {
            if (__instance == null || __0 == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.HostController = __instance;
            var query = BuildQueryFromCard(__0, CardVisualTarget.ItemArt);
            agent.SetContext(query);

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

    private static void ItemVisualsSetupTemplatePostfix(Component __instance, object __0, Task __result)
    {
        try
        {
            if (__instance == null || __0 == null || Instance == null) return;
            string tplId = ReadTemplateId(__0);
            if (string.IsNullOrEmpty(tplId)) return;

            var agent = GetOrAddAgent(__instance);
            agent.SetContext(new CardVisualQuery(null, null, tplId, 0, null, CardVisualTarget.ItemArt));

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

    private static void TryApplyItemVisual(Component visualsComp, CardVisualOverrideAgent agent, uint capturedEpoch)
    {
        try
        {
            if (visualsComp == null || agent == null || agent.Epoch != capturedEpoch || Instance == null) return;
            if (!Instance.TryResolveVisual(agent.LastQuery, out var binding) || binding == null) return;

            Material mat = _itemMatInstanceField?.GetValue(visualsComp) as Material;
            if (mat == null) return;

            CardTextureCache.Instance.AcquireTextureAsync(binding.LocalImagePath, binding.ContentSha256, entry =>
            {
                if (entry == null || entry.Texture == null) return;
                try
                {
                    if (visualsComp == null || agent == null || agent.Epoch != capturedEpoch)
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

                    if (CardTextureCache.Instance.ApplyTextureToUrpMaterial(currentMat, entry.Texture, binding.DisableParallaxPremium))
                    {
                        agent.BindTextureEntry(entry);
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

    private static void CardPreviewItemPostfix(Component __instance, Task __result)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            var query = BuildQueryFromPreview(__instance, CardVisualTarget.ItemArt);
            agent.SetContext(query);
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

    private static void CardPreviewSkillPostfix(Component __instance, Task __result)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.SetContext(BuildQueryFromPreview(__instance, CardVisualTarget.SkillIcon));
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

    private static void CardPreviewEncounterPostfix(Component __instance, Task __result)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            var agent = GetOrAddAgent(__instance);
            agent.SetContext(BuildQueryFromPreview(__instance, CardVisualTarget.EncounterPortrait));
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

    /// <summary>挂在对象池视图上的轻量版本号代理：防止池化复用后晚到的异步 PNG 回调污染新卡（铁律四），销毁时归还引用计数（铁律六）。</summary>
    internal sealed class CardVisualOverrideAgent : MonoBehaviour
    {
        private uint _epoch;
        private CardTextureCache.CacheEntry _primaryEntry;
        private CardTextureCache.CacheEntry _secondaryEntry;

        public uint Epoch => _epoch;
        public bool HasContext { get; private set; }
        public CardVisualQuery LastQuery { get; private set; }
        public Component HostController { get; set; }

        public void SetContext(in CardVisualQuery query)
        {
            _epoch++;
            HasContext = true;
            LastQuery = query;
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

        private void OnDisable()
        {
            _epoch++;
            ReleaseBoundEntries();
        }

        private void OnDestroy()
        {
            _epoch++;
            ReleaseBoundEntries();
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
