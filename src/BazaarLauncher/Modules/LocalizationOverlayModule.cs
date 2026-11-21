using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BazaarGameShared.Domain.Core.Types;
using BazaarLauncher.Core;
using BazaarModSdk;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace BazaarLauncher.Modules;

/// <summary>
/// 全链路四层中文词典拦截引擎（实现 ILocalizationOverrideRegistry，全面升级并兼容 TournamentLocalizationPatch）：
///   优先级（严格遵守当前游戏 LanguageCode，非中文模式下不强行篡改，彻底修复 P1-9）：
///     层 1：自定义卡实例/模板覆盖（CardTooltipData.GetTitle 保留附魔前缀 / GetCardDescription / GetCurrentTooltipsOfType + 动态 {attr.*} 数值解析）
///     层 2：外部 Mod 与服务端同步 Mod 词典（zh-CN.json）
///     层 3：外挂四层 custom_zh_overlay.json（hashOverrides / cardOverrides / uiContextOverrides / uiExactOverrides / uiRegexOverrides / serverMessage*）
///     层 4：官方原版 zh-CN.bytes SQLite 词典
/// 支持服务端热更后一键清空 LocalizationService._cache 并触发运行时热刷新！
/// </summary>
internal sealed class LocalizationOverlayModule : IBuiltInPatchModule, ILocalizationOverrideRegistry
{
    internal static LocalizationOverlayModule Instance { get; private set; }

    public ModManifest Manifest { get; } = new()
    {
        Id = "bazaar.l10n.overlay",
        Name = "全链路四层中文词典拦截引擎",
        Version = "2.1.0",
        Author = "BazaarModKit",
        Description = "统一拦截 LocalizationService / LocalizableText / CardTooltipData 与外挂四层 custom_zh_overlay.json，实现全界面中文 0 英文残留与运行期热刷新。",
        IsBuiltIn = true,
        DefaultEnabled = true,
        Category = "Localization",
        SupportsHotReload = true,
    };

    public SafeHarmonyPatcher Patcher { get; private set; }

    private sealed class RegexEntry
    {
        public int Priority;
        public string Id;
        public Regex Compiled;
        public string Replacement;
        public HashSet<string> Scopes;
    }

    private sealed class LocaleLayerStore
    {
        public readonly List<(int Priority, IReadOnlyDictionary<string, string> Map)> HashLayers = new();
        public readonly List<(int Priority, IReadOnlyDictionary<string, string> Map)> GlobalExactLayers = new();
        public readonly Dictionary<string, List<(int Priority, IReadOnlyDictionary<string, string> Map)>> ContextExactLayers = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<RegexEntry> RegexLayers = new();
        public readonly List<CardTextOverride> CardOverrides = new();
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, LocaleLayerStore> _byLocale = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _overlayHandles = new();

    private string _overlayRevision = "builtin";
    private int _overlayTotalEntries;

    // 反射缓存：读取 PlayerPreferences.Data.LanguageCode 与 LocalizationService 缓存清理
    private static PropertyInfo _prefsDataProp;
    private static PropertyInfo _prefsLangProp;
    private static PropertyInfo _tooltipCardInstanceProp;
    private static PropertyInfo _tooltipCardTemplateProp;
    private static PropertyInfo _cardInstanceIdProp;
    private static PropertyInfo _cardTemplateIdProp;
    private static PropertyInfo _cardAttributesProp;
    private static PropertyInfo _cardEnchantmentProp;
    private static Type _tLocalizableTextType;
    private static ConstructorInfo _tLocCtor2;
    private static PropertyInfo _tLocTextProp;
    private static FieldInfo _tLocTextField;
    private static readonly Regex AttrPlaceholderRegex = new(@"\{attr\.([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    public string CurrentLocale => DetectCurrentGameLocale();
    internal string OverlayRevision => _overlayRevision;
    internal int OverlayEntryCount => _overlayTotalEntries;

    public void OnLoad(IModContext context)
    {
        Instance = this;
        TryLoadLocalDiskOverlayFallback();
    }

    public void OnEnable()
    {
        Patcher = new SafeHarmonyPatcher(Manifest.Id);
        InstallLocalizationPatches(Patcher);
        TournamentLocalizationPatch.Apply(Patcher);
        BazaarLauncherPlugin.Log?.LogInfo(
            $"[LocalizationOverlay] 四层中文词典引擎已激活 (Locale={CurrentLocale}, 条目={_overlayTotalEntries}, 补丁={Patcher.SucceededCount}/{Patcher.AttemptedCount})");
    }

    public void OnDisable()
    {
        TournamentLocalizationPatch.ResetAppliedFlag();
        Patcher?.UnpatchAll();
    }

    public void OnUnload()
    {
        OnDisable();
        if (Instance == this) Instance = null;
    }

    // ── ILocalizationOverrideRegistry 实现 ───────────────────────────────────

    public IDisposable RegisterHashOverrides(string locale, IReadOnlyDictionary<string, string> hashMap, int priority = 0)
    {
        if (hashMap == null || hashMap.Count == 0) return EmptyDisposable.Instance;
        string loc = NormalizeLocale(locale);
        var entry = (priority, hashMap);
        lock (_sync)
        {
            var store = GetOrCreateLocaleStoreLocked(loc);
            store.HashLayers.Add(entry);
            store.HashLayers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
        return new CallbackDisposable(() =>
        {
            lock (_sync) { if (_byLocale.TryGetValue(loc, out var s)) s.HashLayers.Remove(entry); }
        });
    }

    public IDisposable RegisterExactOverrides(
        string locale,
        IReadOnlyDictionary<string, string> exactMap,
        string contextKey = null,
        int priority = 0)
    {
        if (exactMap == null || exactMap.Count == 0) return EmptyDisposable.Instance;
        string loc = NormalizeLocale(locale);
        var entry = (priority, exactMap);
        lock (_sync)
        {
            var store = GetOrCreateLocaleStoreLocked(loc);
            if (string.IsNullOrWhiteSpace(contextKey))
            {
                store.GlobalExactLayers.Add(entry);
                store.GlobalExactLayers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
            else
            {
                if (!store.ContextExactLayers.TryGetValue(contextKey, out var list))
                {
                    list = new List<(int, IReadOnlyDictionary<string, string>)>();
                    store.ContextExactLayers[contextKey] = list;
                }
                list.Add(entry);
                list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
        }
        return new CallbackDisposable(() =>
        {
            lock (_sync)
            {
                if (!_byLocale.TryGetValue(loc, out var s)) return;
                if (string.IsNullOrWhiteSpace(contextKey)) s.GlobalExactLayers.Remove(entry);
                else if (s.ContextExactLayers.TryGetValue(contextKey, out var list)) list.Remove(entry);
            }
        });
    }

    public IDisposable RegisterRegexOverride(string locale, LocalizationRegexRule rule, int priority = 0)
    {
        if (rule == null || string.IsNullOrWhiteSpace(rule.Pattern)) return EmptyDisposable.Instance;
        Regex compiled;
        try
        {
            compiled = new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch
        {
            return EmptyDisposable.Instance;
        }

        string loc = NormalizeLocale(locale);
        var re = new RegexEntry
        {
            Priority = priority,
            Id = rule.Id ?? string.Empty,
            Compiled = compiled,
            Replacement = rule.Replacement ?? string.Empty,
            Scopes = rule.Scopes != null && rule.Scopes.Length > 0
                ? new HashSet<string>(rule.Scopes, StringComparer.OrdinalIgnoreCase)
                : null,
        };

        lock (_sync)
        {
            var store = GetOrCreateLocaleStoreLocked(loc);
            store.RegexLayers.Add(re);
            store.RegexLayers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        return new CallbackDisposable(() =>
        {
            lock (_sync) { if (_byLocale.TryGetValue(loc, out var s)) s.RegexLayers.Remove(re); }
        });
    }

    public IDisposable RegisterCardTextOverride(CardTextOverride cardOverride)
    {
        if (cardOverride == null) return EmptyDisposable.Instance;
        string loc = NormalizeLocale(cardOverride.Locale);
        lock (_sync)
        {
            var store = GetOrCreateLocaleStoreLocked(loc);
            store.CardOverrides.Add(cardOverride);
        }
        return new CallbackDisposable(() =>
        {
            lock (_sync) { if (_byLocale.TryGetValue(loc, out var s)) s.CardOverrides.Remove(cardOverride); }
        });
    }

    public bool TryResolveText(string locale, string hash, string rawText, string contextKey, out string translated)
    {
        translated = rawText ?? string.Empty;
        string loc = NormalizeLocale(locale);
        if (!IsChineseLocale(loc)) return false;

        bool hasRaw = !string.IsNullOrEmpty(rawText);
        // 避免无条件 Trim() 产生热路径 GC 分配（修复 P1-9）
        string lookupText = rawText;
        if (hasRaw && (char.IsWhiteSpace(rawText[0]) || char.IsWhiteSpace(rawText[rawText.Length - 1])))
        {
            lookupText = rawText.Trim();
        }

        lock (_sync)
        {
            if (!_byLocale.TryGetValue(loc, out var store)) return false;

            // 1. 上下文消歧精确覆盖（最高文本优先级）
            if (!string.IsNullOrWhiteSpace(contextKey) && !string.IsNullOrEmpty(lookupText)
                && store.ContextExactLayers.TryGetValue(contextKey, out var ctxList))
            {
                for (int i = 0; i < ctxList.Count; i++)
                {
                    if (ctxList[i].Map.TryGetValue(lookupText, out string hit) && !string.IsNullOrEmpty(hit))
                    {
                        translated = hit;
                        return true;
                    }
                }
            }

            // 2. Hash O(1) 查表（对齐官方 zh-CN.bytes 与 custom_zh_overlay.json hashOverrides）
            string effectiveHash = !string.IsNullOrEmpty(hash)
                ? hash
                : (hasRaw ? ComputeMd5HexUtf8(rawText) : null);

            if (!string.IsNullOrEmpty(effectiveHash))
            {
                for (int i = 0; i < store.HashLayers.Count; i++)
                {
                    if (store.HashLayers[i].Map.TryGetValue(effectiveHash, out string hit) && !string.IsNullOrEmpty(hit))
                    {
                        translated = hit;
                        return true;
                    }
                }
            }

            // 3. 全局原文精确覆盖（Mod 词典 > 外挂 uiExactOverrides）
            if (!string.IsNullOrEmpty(lookupText))
            {
                for (int i = 0; i < store.GlobalExactLayers.Count; i++)
                {
                    if (store.GlobalExactLayers[i].Map.TryGetValue(lookupText, out string hit) && !string.IsNullOrEmpty(hit))
                    {
                        translated = hit;
                        return true;
                    }
                }

                // 带末尾冒号的表单标签支持（如 "Player Cap:"）
                if (lookupText.Length > 1 && lookupText[lookupText.Length - 1] == ':')
                {
                    string stem = lookupText.Substring(0, lookupText.Length - 1).TrimEnd();
                    for (int i = 0; i < store.GlobalExactLayers.Count; i++)
                    {
                        if (store.GlobalExactLayers[i].Map.TryGetValue(stem, out string hit) && !string.IsNullOrEmpty(hit))
                        {
                            translated = hit + "：";
                            return true;
                        }
                    }
                }
            }

            // 4. 正则覆盖（插值文案与服务端报错）
            if (!string.IsNullOrEmpty(lookupText) && store.RegexLayers.Count > 0)
            {
                for (int i = 0; i < store.RegexLayers.Count; i++)
                {
                    var re = store.RegexLayers[i];
                    if (re.Scopes != null && !string.IsNullOrWhiteSpace(contextKey)
                        && !re.Scopes.Contains(contextKey) && !re.Scopes.Contains("global"))
                    {
                        continue;
                    }
                    try
                    {
                        if (re.Compiled.IsMatch(lookupText))
                        {
                            translated = re.Compiled.Replace(lookupText, re.Replacement);
                            return true;
                        }
                    }
                    catch { }
                }
            }
        }

        return false;
    }

    public bool TryResolveCardText(
        string locale,
        string instanceId,
        string customCardId,
        string templateId,
        out CardTextOverride cardOverride)
    {
        cardOverride = null;
        string loc = NormalizeLocale(locale);
        if (!IsChineseLocale(loc)) return false;

        int bestScore = -1;
        lock (_sync)
        {
            if (!_byLocale.TryGetValue(loc, out var store) || store.CardOverrides.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < store.CardOverrides.Count; i++)
            {
                var c = store.CardOverrides[i];
                int score = -1;
                if (!string.IsNullOrEmpty(instanceId) && !string.IsNullOrEmpty(c.InstanceId)
                    && string.Equals(instanceId, c.InstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    score = 30000 + c.Priority;
                }
                else if (!string.IsNullOrEmpty(customCardId) && !string.IsNullOrEmpty(c.CustomCardId)
                    && string.Equals(customCardId, c.CustomCardId, StringComparison.OrdinalIgnoreCase))
                {
                    score = 20000 + c.Priority;
                }
                else if (!string.IsNullOrEmpty(templateId) && !string.IsNullOrEmpty(c.TemplateId)
                    && string.Equals(templateId, c.TemplateId, StringComparison.OrdinalIgnoreCase))
                {
                    score = 10000 + c.Priority;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    cardOverride = c;
                }
            }
        }

        return cardOverride != null;
    }

    public void NotifyLocaleReloaded(string overlayRevision = "")
    {
        if (!string.IsNullOrWhiteSpace(overlayRevision))
        {
            _overlayRevision = overlayRevision;
        }
        MainThreadDispatcher.Enqueue(() =>
        {
            FlushNativeLocalizationServiceCache();
            BazaarLauncherPlugin.Host?.EventBus.Publish(new LocaleSwitchedModEvent(CurrentLocale, _overlayRevision));
        });
    }

    /// <summary>
    /// 解析并热加载四层 custom_zh_overlay.json（同时兼容 Wave 1 规范字段名与 tools/data/custom_zh_overlay.json 实际字段名）。
    /// </summary>
    internal bool LoadOverlayJson(string jsonText, string sourceTag)
    {
        if (string.IsNullOrWhiteSpace(jsonText)) return false;
        try
        {
            var root = JObject.Parse(jsonText);
            string locale = NormalizeLocale((string)root["locale"] ?? "zh-CN");
            string revision = (string)root["revision"] ?? sourceTag ?? "synced";
            var newHandles = new List<IDisposable>();
            int total = 0;

            // 层 1：hashOverrides
            var hashDict = ParseStringMap(root["hashOverrides"] as JObject, StringComparer.OrdinalIgnoreCase);
            if (hashDict.Count > 0)
            {
                newHandles.Add(RegisterHashOverrides(locale, hashDict, priority: 100));
                total += hashDict.Count;
            }

            // 层 2：cardOverrides
            if (root["cardOverrides"] is JObject cardMap)
            {
                foreach (var prop in cardMap.Properties())
                {
                    if (prop.Value is not JObject co) continue;
                    var ov = new CardTextOverride
                    {
                        ModId = "overlay",
                        TemplateId = prop.Name,
                        Locale = locale,
                        Title = (string)(co["title"] ?? co["titleZh"]),
                        Description = (string)(co["description"] ?? co["descZh"]),
                        Priority = 100,
                    };
                    if (co["tooltips"] is JObject tipObj)
                    {
                        var tips = new List<string>();
                        foreach (var tp in tipObj.Properties())
                        {
                            string s = tp.Value?.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) tips.Add(s);
                        }
                        if (tips.Count > 0) ov.ActiveTooltips = tips.ToArray();
                    }
                    newHandles.Add(RegisterCardTextOverride(ov));
                    total++;
                }
            }

            // 层 3a：uiExactOverrides（支持扁平字典或 { global, contexts } 嵌套格式）
            JObject exactRoot = root["uiExactOverrides"] as JObject ?? root["uiExact"] as JObject;
            if (exactRoot != null)
            {
                if (exactRoot["global"] is JObject globalObj)
                {
                    var gMap = ParseStringMap(globalObj, StringComparer.OrdinalIgnoreCase);
                    if (gMap.Count > 0) { newHandles.Add(RegisterExactOverrides(locale, gMap, null, priority: 100)); total += gMap.Count; }
                }
                else
                {
                    var flatMap = ParseStringMap(exactRoot, StringComparer.OrdinalIgnoreCase);
                    if (flatMap.Count > 0) { newHandles.Add(RegisterExactOverrides(locale, flatMap, null, priority: 100)); total += flatMap.Count; }
                }

                JObject ctxRoot = exactRoot["contexts"] as JObject ?? root["uiContextOverrides"] as JObject;
                if (ctxRoot != null)
                {
                    foreach (var cp in ctxRoot.Properties())
                    {
                        if (cp.Value is not JObject subMap) continue;
                        var m = ParseStringMap(subMap, StringComparer.OrdinalIgnoreCase);
                        if (m.Count > 0)
                        {
                            newHandles.Add(RegisterExactOverrides(locale, m, cp.Name, priority: 120));
                            total += m.Count;
                        }
                    }
                }
            }
            else if (root["uiContextOverrides"] is JObject ctxOnly)
            {
                foreach (var cp in ctxOnly.Properties())
                {
                    if (cp.Value is not JObject subMap) continue;
                    var m = ParseStringMap(subMap, StringComparer.OrdinalIgnoreCase);
                    if (m.Count > 0)
                    {
                        newHandles.Add(RegisterExactOverrides(locale, m, cp.Name, priority: 120));
                        total += m.Count;
                    }
                }
            }

            // 层 3b：uiRegexOverrides / uiRegex
            JArray regexArr = root["uiRegexOverrides"] as JArray ?? root["uiRegex"] as JArray;
            total += RegisterRegexArray(locale, regexArr, defaultScope: null, priority: 100, newHandles);

            // 层 4：serverMessageOverrides { exact, regex } 与扁平别名 serverMessageExact / serverMessageRegex
            if (root["serverMessageOverrides"] is JObject srvObj)
            {
                if (srvObj["exact"] is JObject srvExactNested)
                {
                    var m = ParseStringMap(srvExactNested, StringComparer.OrdinalIgnoreCase);
                    if (m.Count > 0)
                    {
                        newHandles.Add(RegisterExactOverrides(locale, m, "NetMessageError", priority: 110));
                        newHandles.Add(RegisterExactOverrides(locale, m, null, priority: 90));
                        total += m.Count;
                    }
                }
                total += RegisterRegexArray(locale, srvObj["regex"] as JArray, "NetMessageError", priority: 110, newHandles);
            }
            if (root["serverMessageExact"] is JObject srvExact)
            {
                var m = ParseStringMap(srvExact, StringComparer.OrdinalIgnoreCase);
                if (m.Count > 0)
                {
                    newHandles.Add(RegisterExactOverrides(locale, m, "NetMessageError", priority: 110));
                    newHandles.Add(RegisterExactOverrides(locale, m, null, priority: 90));
                    total += m.Count;
                }
            }
            total += RegisterRegexArray(locale, root["serverMessageRegex"] as JArray, "NetMessageError", priority: 110, newHandles);

            lock (_sync)
            {
                for (int i = _overlayHandles.Count - 1; i >= 0; i--)
                {
                    try { _overlayHandles[i]?.Dispose(); } catch { }
                }
                _overlayHandles.Clear();
                _overlayHandles.AddRange(newHandles);
                _overlayRevision = revision;
                _overlayTotalEntries = total;
            }

            NotifyLocaleReloaded(revision);
            BazaarLauncherPlugin.Log?.LogInfo($"[LocalizationOverlay] 已热加载四层词典 ({sourceTag}, rev={revision}, entries={total})");
            return true;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[LocalizationOverlay] 解析词典失败 ({sourceTag}): {ex.Message}");
            return false;
        }
    }

    private int RegisterRegexArray(string locale, JArray arr, string defaultScope, int priority, List<IDisposable> handles)
    {
        if (arr == null) return 0;
        int count = 0;
        foreach (var item in arr)
        {
            if (item is not JObject ro) continue;
            string pat = (string)ro["pattern"];
            string rep = (string)ro["replacement"];
            if (string.IsNullOrWhiteSpace(pat) || rep == null) continue;

            var scopes = new List<string>();
            if (ro["scope"] is JArray sa)
            {
                foreach (var s in sa) if (!string.IsNullOrWhiteSpace(s?.ToString())) scopes.Add(s.ToString());
            }
            else if (ro["scopes"] is JArray sa2)
            {
                foreach (var s in sa2) if (!string.IsNullOrWhiteSpace(s?.ToString())) scopes.Add(s.ToString());
            }
            else if (!string.IsNullOrWhiteSpace(defaultScope))
            {
                scopes.Add(defaultScope);
            }

            handles.Add(RegisterRegexOverride(locale, new LocalizationRegexRule
            {
                Id = (string)ro["id"] ?? string.Empty,
                Pattern = pat,
                Replacement = rep,
                Scopes = scopes.ToArray(),
            }, priority));
            count++;
        }
        return count;
    }

    private void TryLoadLocalDiskOverlayFallback()
    {
        try
        {
            string cached = ModContentSyncClient.Instance.OverlayCachePath;
            if (File.Exists(cached))
            {
                LoadOverlayJson(File.ReadAllText(cached, Encoding.UTF8), "local-cache");
                return;
            }

            string cfgDir = BepInEx.Paths.ConfigPath ?? string.Empty;
            string alt = Path.Combine(cfgDir, "custom_zh_overlay.json");
            if (File.Exists(alt))
            {
                LoadOverlayJson(File.ReadAllText(alt, Encoding.UTF8), "config-overlay");
            }
        }
        catch { }
    }

    // ── Harmony 钩子安装 ─────────────────────────────────────────────────────

    private static void InstallLocalizationPatches(SafeHarmonyPatcher patcher)
    {
        // 1. 底层核心单点拦截：LocalizationService.TryGetText(string hash, string text, out string translation)
        patcher.TryPatch(
            "TheBazaar.Localization.LocalizationService",
            "TryGetText",
            new[] { typeof(string), typeof(string), typeof(string).MakeByRefType() },
            prefix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(LocalizationServiceTryGetTextPrefix)),
            optional: true,
            label: "L10n.LocalizationService.TryGetText(hash,text,out)");

        // 2. LocalizableText.GetLocalizedText 兜底拦截（尊重当前 Locale）
        patcher.TryPatch(
            "TheBazaar.Utilities.LocalizableText",
            "GetLocalizedText",
            postfix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(LocalizableTextGetPostfix)),
            optional: true,
            label: "L10n.LocalizableText.GetLocalizedText");

        // 3. CardTooltipData 实例级标题 / 描述 / 主动被动词条拦截
        var tooltipType = AccessTools.TypeByName("TheBazaar.Tooltips.CardTooltipData");
        if (tooltipType != null)
        {
            _tooltipCardInstanceProp = AccessTools.Property(tooltipType, "CardInstance");
            _tooltipCardTemplateProp = AccessTools.Property(tooltipType, "CardTemplate");

            patcher.TryPatch(
                tooltipType,
                "GetTitle",
                Type.EmptyTypes,
                postfix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(CardTooltipGetTitlePostfix)),
                optional: true,
                label: "L10n.CardTooltipData.GetTitle");

            patcher.TryPatch(
                tooltipType,
                "GetCardDescription",
                Type.EmptyTypes,
                postfix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(CardTooltipGetDescriptionPostfix)),
                optional: true,
                label: "L10n.CardTooltipData.GetCardDescription");

            var eTooltipType = AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.ETooltipType");
            if (eTooltipType != null)
            {
                patcher.TryPatch(
                    tooltipType,
                    "GetCurrentTooltipsOfType",
                    new[] { eTooltipType },
                    postfix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(CardTooltipGetTooltipsOfTypePostfix)),
                    optional: true,
                    label: "L10n.CardTooltipData.GetCurrentTooltipsOfType");
            }
        }
    }

    // ── Harmony 钩子回调 ─────────────────────────────────────────────────────

    private static bool LocalizationServiceTryGetTextPrefix(string hash, string text, out string translation, ref bool __result)
    {
        translation = null;
        var inst = Instance;
        if (inst == null) return true;

        string locale = inst.CurrentLocale;
        if (!IsChineseLocale(locale)) return true;

        if (inst.TryResolveText(locale, hash, text, null, out string zh))
        {
            translation = zh;
            __result = true;
            return false;
        }
        return true;
    }

    private static void LocalizableTextGetPostfix(ref string __result)
    {
        var inst = Instance;
        if (inst == null || string.IsNullOrEmpty(__result)) return;

        string locale = inst.CurrentLocale;
        if (!IsChineseLocale(locale)) return;

        if (inst.TryResolveText(locale, null, __result, null, out string zh))
        {
            __result = zh;
        }
    }

    private static void CardTooltipGetTitlePostfix(object __instance, ref string __result)
    {
        try
        {
            var inst = Instance;
            if (inst == null || __instance == null) return;
            string locale = inst.CurrentLocale;
            if (!IsChineseLocale(locale)) return;

            if (!TryExtractCardOverride(__instance, locale, out var ov, out _)) return;
            if (string.IsNullOrWhiteSpace(ov.Title)) return;

            // 保留原版生成的 <style=Tiny> 或附魔富文本前缀（如 "剧毒\n"），仅替换标题正文段
            if (!string.IsNullOrEmpty(__result))
            {
                int nl = __result.LastIndexOf('\n');
                if (nl >= 0 && nl < __result.Length - 1)
                {
                    __result = __result.Substring(0, nl + 1) + ov.Title;
                    return;
                }
            }
            __result = ov.Title;
        }
        catch { }
    }

    private static void CardTooltipGetDescriptionPostfix(object __instance, ref string __result)
    {
        try
        {
            var inst = Instance;
            if (inst == null || __instance == null) return;
            string locale = inst.CurrentLocale;
            if (!IsChineseLocale(locale)) return;

            if (!TryExtractCardOverride(__instance, locale, out var ov, out var cardInst)) return;
            if (string.IsNullOrWhiteSpace(ov.Description)) return;

            __result = RenderDynamicAttributePlaceholders(ov.Description, cardInst);
        }
        catch { }
    }

    private static void CardTooltipGetTooltipsOfTypePostfix(object __instance, object __0, ref IList __result)
    {
        try
        {
            var inst = Instance;
            if (inst == null || __instance == null || __0 == null) return;
            string locale = inst.CurrentLocale;
            if (!IsChineseLocale(locale)) return;

            if (!TryExtractCardOverride(__instance, locale, out var ov, out var cardInst)) return;

            string tipKind = __0.ToString();
            string[] lines = null;
            if (string.Equals(tipKind, "Active", StringComparison.OrdinalIgnoreCase))
            {
                lines = ov.ActiveTooltips;
            }
            else if (string.Equals(tipKind, "Passive", StringComparison.OrdinalIgnoreCase))
            {
                lines = ov.PassiveTooltips;
            }

            if (lines == null || lines.Length == 0) return;

            EnsureTLocalizableTextMeta();
            if (_tLocalizableTextType == null) return;

            var listType = typeof(List<>).MakeGenericType(_tLocalizableTextType);
            if (Activator.CreateInstance(listType) is not IList customList) return;

            for (int i = 0; i < lines.Length; i++)
            {
                string rendered = RenderDynamicAttributePlaceholders(lines[i], cardInst);
                object locObj = CreateTLocalizableText(rendered);
                if (locObj != null) customList.Add(locObj);
            }

            // 附魔词条自动追加（若自定义卡配置了对应附魔说明）
            if (ov.EnchantmentTooltips != null && cardInst != null && string.Equals(tipKind, "Passive", StringComparison.OrdinalIgnoreCase))
            {
                _cardEnchantmentProp ??= cardInst.GetType().GetProperty("Enchantment", BindingFlags.Public | BindingFlags.Instance);
                string ench = _cardEnchantmentProp?.GetValue(cardInst)?.ToString();
                if (!string.IsNullOrEmpty(ench) && ov.EnchantmentTooltips.TryGetValue(ench, out var enchLines) && enchLines != null)
                {
                    foreach (var el in enchLines)
                    {
                        object locObj = CreateTLocalizableText(RenderDynamicAttributePlaceholders(el, cardInst));
                        if (locObj != null) customList.Add(locObj);
                    }
                }
            }

            if (customList.Count > 0)
            {
                __result = customList;
            }
        }
        catch { }
    }

    // ── 工具方法 ─────────────────────────────────────────────────────────────

    private static bool TryExtractCardOverride(object tooltipData, string locale, out CardTextOverride ov, out object cardInst)
    {
        ov = null;
        cardInst = _tooltipCardInstanceProp?.GetValue(tooltipData);
        string iid = null;
        string tplId = null;

        if (cardInst != null)
        {
            Type ct = cardInst.GetType();
            _cardInstanceIdProp ??= ct.GetProperty("InstanceId", BindingFlags.Public | BindingFlags.Instance);
            _cardTemplateIdProp ??= ct.GetProperty("TemplateId", BindingFlags.Public | BindingFlags.Instance);
            iid = _cardInstanceIdProp?.GetValue(cardInst)?.ToString();
            tplId = _cardTemplateIdProp?.GetValue(cardInst)?.ToString();
        }

        if (string.IsNullOrEmpty(tplId))
        {
            object tpl = _tooltipCardTemplateProp?.GetValue(tooltipData);
            if (tpl != null)
            {
                tplId = tpl.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tpl)?.ToString();
            }
        }

        return Instance != null && Instance.TryResolveCardText(locale, iid, null, tplId, out ov) && ov != null;
    }

    private static string RenderDynamicAttributePlaceholders(string template, object cardInst)
    {
        if (string.IsNullOrEmpty(template) || cardInst == null || template.IndexOf("{attr.", StringComparison.Ordinal) < 0)
        {
            return template ?? string.Empty;
        }

        _cardAttributesProp ??= cardInst.GetType().GetProperty("Attributes", BindingFlags.Public | BindingFlags.Instance);
        if (_cardAttributesProp?.GetValue(cardInst) is not IDictionary attrs)
        {
            return template;
        }

        return AttrPlaceholderRegex.Replace(template, m =>
        {
            string attrName = m.Groups[1].Value;
            foreach (DictionaryEntry kv in attrs)
            {
                if (string.Equals(kv.Key?.ToString(), attrName, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value?.ToString() ?? "0";
                }
            }
            return m.Value;
        });
    }

    private static void EnsureTLocalizableTextMeta()
    {
        if (_tLocalizableTextType != null) return;
        _tLocalizableTextType = AccessTools.TypeByName("BazaarGameShared.Domain.Core.TLocalizableText");
        if (_tLocalizableTextType == null) return;
        _tLocCtor2 = _tLocalizableTextType.GetConstructor(new[] { typeof(string), typeof(string) });
        _tLocTextProp = _tLocalizableTextType.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
        _tLocTextField = _tLocalizableTextType.GetField("Text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private static object CreateTLocalizableText(string text)
    {
        if (_tLocalizableTextType == null) return null;
        try
        {
            if (_tLocCtor2 != null)
            {
                return _tLocCtor2.Invoke(new object[] { text ?? string.Empty, string.Empty });
            }
            object obj = Activator.CreateInstance(_tLocalizableTextType);
            if (_tLocTextProp != null && _tLocTextProp.CanWrite) _tLocTextProp.SetValue(obj, text ?? string.Empty);
            else _tLocTextField?.SetValue(obj, text ?? string.Empty);
            return obj;
        }
        catch
        {
            return null;
        }
    }

    private static void FlushNativeLocalizationServiceCache()
    {
        try
        {
            var svcType = AccessTools.TypeByName("TheBazaar.Localization.LocalizationService");
            if (svcType == null) return;

            var servicesType = AccessTools.TypeByName("TheBazaar.AppFramework.Services");
            var getGeneric = servicesType?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            object locSvc = null;
            if (getGeneric != null && getGeneric.IsGenericMethodDefinition)
            {
                locSvc = getGeneric.MakeGenericMethod(svcType).Invoke(null, null);
            }
            if (locSvc == null) return;

            var cacheField = AccessTools.Field(svcType, "_cache");
            var cacheObj = cacheField?.GetValue(locSvc);
            cacheObj?.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(cacheObj, null);
        }
        catch { }
    }

    internal static string DetectCurrentGameLocale()
    {
        try
        {
            if (_prefsDataProp == null)
            {
                var prefsType = AccessTools.TypeByName("PlayerPreferences");
                _prefsDataProp = prefsType?.GetProperty("Data", BindingFlags.Public | BindingFlags.Static);
            }
            object data = _prefsDataProp?.GetValue(null);
            if (data != null)
            {
                _prefsLangProp ??= data.GetType().GetProperty("LanguageCode", BindingFlags.Public | BindingFlags.Instance);
                string code = _prefsLangProp?.GetValue(data)?.ToString();
                if (!string.IsNullOrWhiteSpace(code)) return NormalizeLocale(code);
            }
        }
        catch { }
        return "zh-CN";
    }

    internal static bool IsChineseLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return true;
        return locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return "zh-CN";
        string s = locale.Trim().Replace('_', '-');
        if (s.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        return s;
    }

    private LocaleLayerStore GetOrCreateLocaleStoreLocked(string loc)
    {
        if (!_byLocale.TryGetValue(loc, out var store))
        {
            store = new LocaleLayerStore();
            _byLocale[loc] = store;
        }
        return store;
    }

    private static Dictionary<string, string> ParseStringMap(JObject obj, StringComparer cmp)
    {
        var dict = new Dictionary<string, string>(cmp);
        if (obj == null) return dict;
        foreach (var prop in obj.Properties())
        {
            if (prop.Value != null && prop.Value.Type == JTokenType.String)
            {
                string val = prop.Value.Value<string>();
                if (!string.IsNullOrEmpty(val))
                {
                    dict[prop.Name] = val;
                }
            }
        }
        return dict;
    }

    private static string ComputeMd5HexUtf8(string text)
    {
        using var md5 = MD5.Create();
        byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(32);
        for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private sealed class CallbackDisposable : IDisposable
    {
        private Action _act;
        public CallbackDisposable(Action act) => _act = act;
        public void Dispose() { _act?.Invoke(); _act = null; }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
