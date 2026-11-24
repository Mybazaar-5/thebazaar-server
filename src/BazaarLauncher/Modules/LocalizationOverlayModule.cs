using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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

    /// <summary>无锁只读词典快照：热路径读取 0 锁竞争，并将全局正则与限定 Scope 正则（如 NetMessageError）严格隔离。</summary>
    private sealed class LocaleSnapshot
    {
        public (int Priority, IReadOnlyDictionary<string, string> Map)[] HashLayers = Array.Empty<(int, IReadOnlyDictionary<string, string>)>();
        public (int Priority, IReadOnlyDictionary<string, string> Map)[] GlobalExactLayers = Array.Empty<(int, IReadOnlyDictionary<string, string>)>();
        public Dictionary<string, (int Priority, IReadOnlyDictionary<string, string> Map)[]> ContextExactLayers = new(StringComparer.OrdinalIgnoreCase);
        public RegexEntry[] GlobalRegexLayers = Array.Empty<RegexEntry>();
        public Dictionary<string, RegexEntry[]> ScopedRegexLayers = new(StringComparer.OrdinalIgnoreCase);
        public CardTextOverride[] CardOverrides = Array.Empty<CardTextOverride>();
    }

    private readonly struct ResolveMemoKey : IEquatable<ResolveMemoKey>
    {
        public readonly string Locale;
        public readonly string Hash;
        public readonly string ContextKey;
        public readonly string Text;

        public ResolveMemoKey(string locale, string hash, string contextKey, string text)
        {
            Locale = locale ?? string.Empty;
            Hash = hash ?? string.Empty;
            ContextKey = contextKey ?? string.Empty;
            Text = text ?? string.Empty;
        }

        public bool Equals(ResolveMemoKey other)
            => string.Equals(Text, other.Text, StringComparison.Ordinal)
               && string.Equals(Hash, other.Hash, StringComparison.Ordinal)
               && string.Equals(ContextKey, other.ContextKey, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Locale, other.Locale, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => obj is ResolveMemoKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = StringComparer.Ordinal.GetHashCode(Text);
                h = (h * 397) ^ StringComparer.Ordinal.GetHashCode(Hash);
                h = (h * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(ContextKey);
                h = (h * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Locale);
                return h;
            }
        }
    }

    /// <summary>按运行时真实卡牌类型分桶缓存反射属性，彻底消除跨子类 InvalidCastException 沉睡炸弹。</summary>
    private sealed class CardMemberCache
    {
        public PropertyInfo InstanceId;
        public PropertyInfo TemplateId;
        public PropertyInfo Attributes;
        public PropertyInfo Enchantment;
        public PropertyInfo Tier;
        public PropertyInfo Id;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, LocaleLayerStore> _byLocale = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, LocaleSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ResolveMemoKey, string> _resolveMemo = new();
    private const int MaxResolveMemoEntries = 4096;
    private static readonly string NoMatchSentinel = new('\0', 1);
    private readonly List<IDisposable> _overlayHandles = new();

    private string _overlayRevision = "builtin";
    private int _overlayTotalEntries;

    // 反射缓存：读取 PlayerPreferences.Data.LanguageCode 与 LocalizationService 缓存清理
    private static PropertyInfo _prefsDataProp;
    private static PropertyInfo _prefsLangProp;
    private static PropertyInfo _tooltipCardInstanceProp;
    private static PropertyInfo _tooltipCardTemplateProp;
    private static PropertyInfo _tooltipCanFuseProp;
    private static MethodInfo _lockCreateTierOptionMethod;
    private static MethodInfo _lockCreateNoneEnchOptionMethod;
    private static MethodInfo _lockCreateEnchOptionMethod;
    private static Type _cardTierOptionType;
    private static Type _cardEnchOptionType;
    private static readonly ConcurrentDictionary<Type, CardMemberCache> _cardMembersByType = new();
    private static readonly HashSet<string> DurationAttrNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CooldownMax", "HasteAmount", "SlowAmount", "FreezeAmount", "ChargeAmount"
    };
    // P3-06：允许按整倍/条件比例外推下一阶预览值的核心量级属性集合（排除售价/冷却/弹药/暴击/多重施放等离散或经济属性）
    private static readonly HashSet<string> ScalableUpgradePreviewAttrNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DamageAmount", "ShieldApplyAmount", "HealAmount",
        "BurnApplyAmount", "PoisonApplyAmount", "RegenApplyAmount", "RageApplyAmount",
        "HasteAmount", "SlowAmount", "FreezeAmount", "ChargeAmount", "Value"
    };
    private static Type _tLocalizableTextType;
    private static ConstructorInfo _tLocCtor2;
    private static PropertyInfo _tLocTextProp;
    private static FieldInfo _tLocTextField;
    private static readonly Regex AttrPlaceholderRegex = new(@"\{attr\.([A-Za-z0-9_]+)\}", RegexOptions.Compiled);
    private static int _l10nHookErrorCount;

    // D4-P1-9：原生 LocalizationService.LocaleChanged 订阅句柄
    private object _nativeLocaleSvcObj;
    private EventInfo _nativeLocaleChangedEvent;
    private Delegate _nativeLocaleChangedDelegate;

    // D3-P1-11：TagRenderer 额外标签渲染反射缓存
    private static FieldInfo _tagRendererCtrlField;
    private static MethodInfo _tooltipTagInitMethod;
    private static MethodInfo _tooltipColorTagMethod;
    private static PropertyInfo _dataTooltipTypographyProp;

    [ThreadStatic] private static MD5 _tsMd5;
    [ThreadStatic] private static byte[] _tsUtf8Buf;
    private static readonly char[] HexDigits = "0123456789abcdef".ToCharArray();

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
        SubscribeNativeLocaleChanged();
        Patcher.LogSummary($"[LocalizationOverlay] 四层中文词典引擎已激活 (Locale={CurrentLocale}, 条目={_overlayTotalEntries})");
    }

    public void OnDisable()
    {
        UnsubscribeNativeLocaleChanged();
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
            RebuildSnapshotsLocked();
        }
        return new CallbackDisposable(() =>
        {
            lock (_sync)
            {
                if (_byLocale.TryGetValue(loc, out var s))
                {
                    s.HashLayers.Remove(entry);
                    RebuildSnapshotsLocked();
                }
            }
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
            RebuildSnapshotsLocked();
        }
        return new CallbackDisposable(() =>
        {
            lock (_sync)
            {
                if (!_byLocale.TryGetValue(loc, out var s)) return;
                if (string.IsNullOrWhiteSpace(contextKey)) s.GlobalExactLayers.Remove(entry);
                else if (s.ContextExactLayers.TryGetValue(contextKey, out var list)) list.Remove(entry);
                RebuildSnapshotsLocked();
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
            RebuildSnapshotsLocked();
        }

        return new CallbackDisposable(() =>
        {
            lock (_sync)
            {
                if (_byLocale.TryGetValue(loc, out var s))
                {
                    s.RegexLayers.Remove(re);
                    RebuildSnapshotsLocked();
                }
            }
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
            RebuildSnapshotsLocked();
        }
        return new CallbackDisposable(() =>
        {
            lock (_sync)
            {
                if (_byLocale.TryGetValue(loc, out var s))
                {
                    s.CardOverrides.Remove(cardOverride);
                    RebuildSnapshotsLocked();
                }
            }
        });
    }

    public bool TryResolveText(string locale, string hash, string rawText, string contextKey, out string translated)
    {
        translated = rawText ?? string.Empty;
        string loc = NormalizeLocale(locale);
        if (!IsChineseLocale(loc)) return false;

        var snapshots = _snapshots;
        if (!snapshots.TryGetValue(loc, out var snap)) return false;

        bool hasRaw = !string.IsNullOrEmpty(rawText);
        bool hasHash = !string.IsNullOrEmpty(hash);
        if (!hasRaw && !hasHash) return false;

        // 避免无条件 Trim() 产生热路径 GC 分配（修复 P1-9）
        string lookupText = rawText;
        if (hasRaw && (char.IsWhiteSpace(rawText[0]) || char.IsWhiteSpace(rawText[rawText.Length - 1])))
        {
            lookupText = rawText.Trim();
        }

        bool hasContext = !string.IsNullOrWhiteSpace(contextKey);

        // ── 快速路径 1：纯中文且无待替换英文/占位符的卡面或 UI 文本，O(1) 零分配短路 ──
        // 彻底杜绝已翻译中文文本反复执行 lock + MD5.Create() + 53 条正则遍历（拖拽 Top 3）
        if (!hasHash && hasRaw && IsPureChineseWithoutPendingEnglish(lookupText))
        {
            if (TryResolveExactOnly(snap, lookupText, hasContext ? contextKey : null, out string exactZh))
            {
                translated = exactZh;
                return true;
            }
            return false;
        }

        // ── 快速路径 2：有界并发记忆缓存（同一英文/混排文本 100% O(1) 命中）──
        var memoKey = new ResolveMemoKey(loc, hasHash ? hash : string.Empty, hasContext ? contextKey : string.Empty, lookupText ?? string.Empty);
        if (_resolveMemo.TryGetValue(memoKey, out string memoVal))
        {
            if (ReferenceEquals(memoVal, NoMatchSentinel))
            {
                return false;
            }
            translated = memoVal;
            return true;
        }

        bool matched = TryResolveFromSnapshotSlow(snap, hash, rawText, lookupText, hasContext ? contextKey : null, out string resolved);

        if (_resolveMemo.Count >= MaxResolveMemoEntries)
        {
            _resolveMemo.Clear();
        }
        _resolveMemo[memoKey] = matched ? resolved : NoMatchSentinel;

        if (matched)
        {
            translated = resolved;
            return true;
        }
        return false;
    }

    private static bool TryResolveExactOnly(LocaleSnapshot snap, string lookupText, string contextKey, out string translated)
    {
        translated = null;
        if (string.IsNullOrEmpty(lookupText)) return false;

        if (contextKey != null && snap.ContextExactLayers.TryGetValue(contextKey, out var ctxArr))
        {
            for (int i = 0; i < ctxArr.Length; i++)
            {
                if (ctxArr[i].Map.TryGetValue(lookupText, out string hit) && !string.IsNullOrEmpty(hit))
                {
                    translated = hit;
                    return true;
                }
            }
        }

        var globalArr = snap.GlobalExactLayers;
        for (int i = 0; i < globalArr.Length; i++)
        {
            if (globalArr[i].Map.TryGetValue(lookupText, out string hit) && !string.IsNullOrEmpty(hit))
            {
                translated = hit;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveFromSnapshotSlow(
        LocaleSnapshot snap,
        string hash,
        string rawText,
        string lookupText,
        string contextKey,
        out string translated)
    {
        translated = null;
        bool hasRaw = !string.IsNullOrEmpty(rawText);

        // 1. 上下文消歧精确覆盖（最高文本优先级）
        if (contextKey != null && !string.IsNullOrEmpty(lookupText)
            && snap.ContextExactLayers.TryGetValue(contextKey, out var ctxArr))
        {
            for (int i = 0; i < ctxArr.Length; i++)
            {
                if (ctxArr[i].Map.TryGetValue(lookupText, out string hit) && !string.IsNullOrEmpty(hit))
                {
                    translated = hit;
                    return true;
                }
            }
        }

        // 2. Hash O(1) 查表（仅在存在 hashOverrides 时才按需计算 MD5，且复用 ThreadStatic MD5）
        var hashArr = snap.HashLayers;
        if (hashArr.Length > 0)
        {
            string effectiveHash = !string.IsNullOrEmpty(hash)
                ? hash
                : (hasRaw ? ComputeMd5HexUtf8(rawText) : null);

            if (!string.IsNullOrEmpty(effectiveHash))
            {
                for (int i = 0; i < hashArr.Length; i++)
                {
                    if (hashArr[i].Map.TryGetValue(effectiveHash, out string hit) && !string.IsNullOrEmpty(hit))
                    {
                        translated = hit;
                        return true;
                    }
                }
            }
        }

        // 3. 全局原文精确覆盖（Mod 词典 > 外挂 uiExactOverrides）
        if (!string.IsNullOrEmpty(lookupText))
        {
            var globalArr = snap.GlobalExactLayers;
            for (int i = 0; i < globalArr.Length; i++)
            {
                if (globalArr[i].Map.TryGetValue(lookupText, out string hit) && !string.IsNullOrEmpty(hit))
                {
                    translated = hit;
                    return true;
                }
            }

            // 带末尾冒号的表单标签支持（如 "Player Cap:"）
            if (lookupText.Length > 1 && lookupText[lookupText.Length - 1] == ':')
            {
                string stem = lookupText.Substring(0, lookupText.Length - 1).TrimEnd();
                for (int i = 0; i < globalArr.Length; i++)
                {
                    if (globalArr[i].Map.TryGetValue(stem, out string hit) && !string.IsNullOrEmpty(hit))
                    {
                        translated = hit + "：";
                        return true;
                    }
                }
            }
        }

        // 4. 正则覆盖：严格按 Scope 隔离（修复 L309 反转 Bug，33 条服务端报错正则仅在对应 contextKey 下运行）
        if (!string.IsNullOrEmpty(lookupText))
        {
            if (contextKey != null && snap.ScopedRegexLayers.TryGetValue(contextKey, out var scopedArr))
            {
                for (int i = 0; i < scopedArr.Length; i++)
                {
                    var re = scopedArr[i];
                    try
                    {
                        if (re.Compiled.IsMatch(lookupText))
                        {
                            translated = re.Compiled.Replace(lookupText, re.Replacement);
                            return true;
                        }
                    }
                    catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L524", diagEx); }
                }
            }

            var globalRegexArr = snap.GlobalRegexLayers;
            for (int i = 0; i < globalRegexArr.Length; i++)
            {
                var re = globalRegexArr[i];
                try
                {
                    if (re.Compiled.IsMatch(lookupText))
                    {
                        translated = re.Compiled.Replace(lookupText, re.Replacement);
                        return true;
                    }
                }
                catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L540", diagEx); }
            }
        }

        return false;
    }

    /// <summary>
    /// 零分配快速判定：判断文本是否已为纯中文（忽略 Unity TMP 富文本 &lt;...&gt; 标签）且不包含需要正则翻译的英文字母或占位符。
    /// </summary>
    private static bool IsPureChineseWithoutPendingEnglish(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        bool hasCjk = false;
        bool inTag = false;
        int len = text.Length;
        for (int i = 0; i < len; i++)
        {
            char c = text[i];
            if (c == '<')
            {
                inTag = true;
                continue;
            }
            if (inTag)
            {
                if (c == '>') inTag = false;
                continue;
            }
            if (c == '{')
            {
                // 含动态占位符（如 {attr.*}）不走纯中文短路
                return false;
            }
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                // 标签外存在 ASCII 字母，可能仍有英文词条/混合词待替换
                return false;
            }
            if ((c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3400' && c <= '\u4DBF'))
            {
                hasCjk = true;
            }
        }
        return hasCjk;
    }

    /// <summary>按卡实例/自定义ID/模板ID查询字段级合并后的结构化元数据，不受界面中英文语言限制。</summary>
    internal bool TryResolveCardMetadata(
        string instanceId,
        string customCardId,
        string templateId,
        out CardTextOverride cardOverride)
    {
        cardOverride = null;
        var snapshots = _snapshots;
        if (snapshots == null || snapshots.Count == 0) return false;

        string loc = NormalizeLocale(CurrentLocale);
        return TryMergeCardOverrideAcrossLocales(snapshots, loc, instanceId, customCardId, templateId, out cardOverride);
    }

    public bool TryResolveCardText(
        string locale,
        string instanceId,
        string customCardId,
        string templateId,
        out CardTextOverride cardOverride)
    {
        cardOverride = null;
        var snapshots = _snapshots;
        if (snapshots == null || snapshots.Count == 0) return false;

        string loc = NormalizeLocale(locale);
        return TryMergeCardOverrideAcrossLocales(snapshots, loc, instanceId, customCardId, templateId, out cardOverride);
    }

    /// <summary>P0-18：纯逻辑字段级合并入口（供单元测试与运行时共用）。</summary>
    internal static CardTextOverride MergeCardOverridesForTest(
        CardTextOverride[] candidates,
        string instanceId,
        string customCardId,
        string templateId)
    {
        return MergeCardOverrides(candidates, instanceId, customCardId, templateId);
    }

    /// <summary>P0-27：按当前语言与借壳标识选择单条显示文本；英文态全新借壳卡缺英文时回退中文，绝不跌落原版底座。</summary>
    internal static string PickLocalizedString(bool isZh, string zh, string en, bool isBorrowShell)
    {
        if (isZh)
        {
            if (!string.IsNullOrWhiteSpace(zh)) return zh;
            if (!string.IsNullOrWhiteSpace(en)) return en;
            return null;
        }

        if (!string.IsNullOrWhiteSpace(en)) return en;
        if (isBorrowShell && !string.IsNullOrWhiteSpace(zh)) return zh;
        return null;
    }

    internal static string[] PickLocalizedLines(bool isZh, string[] zh, string[] en, bool isBorrowShell)
    {
        if (isZh)
        {
            if (zh != null && zh.Length > 0) return zh;
            if (en != null && en.Length > 0) return en;
            return null;
        }

        if (en != null && en.Length > 0) return en;
        if (isBorrowShell && zh != null && zh.Length > 0) return zh;
        return null;
    }

    internal static Dictionary<string, string[]> PickLocalizedEnchantTooltips(
        bool isZh,
        Dictionary<string, string[]> zh,
        Dictionary<string, string[]> en,
        bool isBorrowShell)
    {
        if (isZh)
        {
            if (zh != null && zh.Count > 0) return zh;
            if (en != null && en.Count > 0) return en;
            return null;
        }

        if (en != null && en.Count > 0) return en;
        if (isBorrowShell && zh != null && zh.Count > 0) return zh;
        return null;
    }

    /// <summary>P0-27：全新借壳自定义卡未配置 PassiveTooltips 时，必须彻底清空原版借壳底座被动词条防止泄漏。</summary>
    internal static bool ShouldSuppressVanillaPassives(bool isCustomBorrowShell, string[] resolvedPassives)
    {
        return isCustomBorrowShell && (resolvedPassives == null || resolvedPassives.Length == 0);
    }

    /// <summary>D3-P1-02：按附魔预览乘数与加偏移公式计算派生属性值。</summary>
    internal static int EvaluatePreviewAttrDerivedValue(int sourceVal, double? sourceMultiplier, double? sourceAddOffset)
    {
        double mult = sourceMultiplier ?? 1.0;
        double offset = sourceAddOffset ?? 0.0;
        return (int)Math.Round(sourceVal * mult + offset);
    }

    private static bool TryMergeCardOverrideAcrossLocales(
        Dictionary<string, LocaleSnapshot> snapshots,
        string primaryLocale,
        string instanceId,
        string customCardId,
        string templateId,
        out CardTextOverride merged)
    {
        merged = null;
        snapshots.TryGetValue(primaryLocale, out var primarySnap);
        LocaleSnapshot zhSnap = null;
        if (!string.Equals(primaryLocale, "zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            snapshots.TryGetValue("zh-CN", out zhSnap);
        }

        if ((primarySnap == null || primarySnap.CardOverrides.Length == 0)
            && (zhSnap == null || zhSnap.CardOverrides.Length == 0))
        {
            return false;
        }

        if (zhSnap == null || zhSnap.CardOverrides.Length == 0)
        {
            merged = MergeCardOverrides(primarySnap.CardOverrides, instanceId, customCardId, templateId);
            return merged != null;
        }
        if (primarySnap == null || primarySnap.CardOverrides.Length == 0)
        {
            merged = MergeCardOverrides(zhSnap.CardOverrides, instanceId, customCardId, templateId);
            return merged != null;
        }

        var combined = new CardTextOverride[primarySnap.CardOverrides.Length + zhSnap.CardOverrides.Length];
        Array.Copy(primarySnap.CardOverrides, 0, combined, 0, primarySnap.CardOverrides.Length);
        Array.Copy(zhSnap.CardOverrides, 0, combined, primarySnap.CardOverrides.Length, zhSnap.CardOverrides.Length);
        merged = MergeCardOverrides(combined, instanceId, customCardId, templateId);
        return merged != null;
    }

    private static CardTextOverride MergeCardOverrides(
        CardTextOverride[] arr,
        string instanceId,
        string customCardId,
        string templateId)
    {
        if (arr == null || arr.Length == 0) return null;

        List<(int Score, int Order, CardTextOverride Item)> matched = null;
        for (int i = 0; i < arr.Length; i++)
        {
            var c = arr[i];
            if (c == null) continue;

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
                // 防借壳污染：当候选明确标注为借壳自定义卡（非原位 Patch 且 CustomCardId != TemplateId）时，
                // 仅允许通过 InstanceId 或 CustomCardId 命中，绝不通过借壳 TemplateId 误命中原版卡
                if (c.IsCustomBorrowShell
                    && !c.IsPatchOverride
                    && !string.IsNullOrEmpty(c.CustomCardId)
                    && !string.Equals(c.CustomCardId, c.TemplateId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                score = 10000 + c.Priority;
            }

            if (score >= 0)
            {
                matched ??= new List<(int, int, CardTextOverride)>(4);
                matched.Add((score, i, c));
            }
        }

        if (matched == null || matched.Count == 0) return null;
        if (matched.Count == 1) return matched[0].Item;

        matched.Sort((a, b) =>
        {
            int cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });

        var top = matched[0];
        bool topIsBorrowShellNewCard = top.Score >= 20000
            && !top.Item.IsPatchOverride
            && (top.Item.IsCustomBorrowShell
                || (!string.IsNullOrEmpty(top.Item.CustomCardId)
                    && !string.Equals(top.Item.CustomCardId, templateId, StringComparison.OrdinalIgnoreCase)));
        string topCustomCardId = top.Item.CustomCardId ?? string.Empty;

        var result = new CardTextOverride
        {
            Priority = top.Item.Priority,
        };

        bool identityResolved = false;
        for (int i = 0; i < matched.Count; i++)
        {
            var entry = matched[i];
            var c = entry.Item;

            // P0-18/P0-27 跨 Mod 借壳隔离：当最高分项为借壳新卡时，严禁混入仅按原版底座 TemplateId 命中的
            // 原位 Patch（Score < 20000）、显式原位 Patch（IsPatchOverride=true）或其他 CustomCardId 条目，
            // 彻底防止原位 Patch 的文案/被动词条/徽章/数值反灌进借壳新卡并击穿 ShouldSuppressVanillaPassives。
            if (topIsBorrowShellNewCard)
            {
                if (entry.Score < 20000 || c.IsPatchOverride)
                    continue;

                if (!string.IsNullOrEmpty(topCustomCardId))
                {
                    bool sameCustomId = string.Equals(c.CustomCardId ?? string.Empty, topCustomCardId, StringComparison.OrdinalIgnoreCase)
                        || (entry.Score >= 30000 && string.IsNullOrEmpty(c.CustomCardId));
                    if (!sameCustomId)
                        continue;
                }
            }

            if (string.IsNullOrEmpty(result.ModId) && !string.IsNullOrEmpty(c.ModId)) result.ModId = c.ModId;
            if (string.IsNullOrEmpty(result.Locale) && !string.IsNullOrEmpty(c.Locale)) result.Locale = c.Locale;
            if (string.IsNullOrEmpty(result.InstanceId) && !string.IsNullOrEmpty(c.InstanceId)) result.InstanceId = c.InstanceId;
            if (string.IsNullOrEmpty(result.CustomCardId) && !string.IsNullOrEmpty(c.CustomCardId)) result.CustomCardId = c.CustomCardId;
            if (string.IsNullOrEmpty(result.TemplateId) && !string.IsNullOrEmpty(c.TemplateId)) result.TemplateId = c.TemplateId;

            if (!identityResolved && (c.IsCustomBorrowShell || c.IsPatchOverride))
            {
                result.IsPatchOverride = c.IsPatchOverride;
                result.IsCustomBorrowShell = c.IsCustomBorrowShell;
                identityResolved = true;
            }

            if (string.IsNullOrWhiteSpace(result.Title) && !string.IsNullOrWhiteSpace(c.Title)) result.Title = c.Title;
            if (string.IsNullOrWhiteSpace(result.TitleEn) && !string.IsNullOrWhiteSpace(c.TitleEn)) result.TitleEn = c.TitleEn;
            if (string.IsNullOrWhiteSpace(result.Description) && !string.IsNullOrWhiteSpace(c.Description)) result.Description = c.Description;
            if (string.IsNullOrWhiteSpace(result.DescriptionEn) && !string.IsNullOrWhiteSpace(c.DescriptionEn)) result.DescriptionEn = c.DescriptionEn;

            if ((result.ActiveTooltips == null || result.ActiveTooltips.Length == 0) && c.ActiveTooltips != null && c.ActiveTooltips.Length > 0)
                result.ActiveTooltips = c.ActiveTooltips;
            if ((result.ActiveTooltipsEn == null || result.ActiveTooltipsEn.Length == 0) && c.ActiveTooltipsEn != null && c.ActiveTooltipsEn.Length > 0)
                result.ActiveTooltipsEn = c.ActiveTooltipsEn;
            if ((result.PassiveTooltips == null || result.PassiveTooltips.Length == 0) && c.PassiveTooltips != null && c.PassiveTooltips.Length > 0)
                result.PassiveTooltips = c.PassiveTooltips;
            if ((result.PassiveTooltipsEn == null || result.PassiveTooltipsEn.Length == 0) && c.PassiveTooltipsEn != null && c.PassiveTooltipsEn.Length > 0)
                result.PassiveTooltipsEn = c.PassiveTooltipsEn;

            if ((result.TierAttributes == null || result.TierAttributes.Count == 0) && c.TierAttributes != null && c.TierAttributes.Count > 0)
                result.TierAttributes = c.TierAttributes;
            if ((result.EnchantmentPreviewOps == null || result.EnchantmentPreviewOps.Count == 0) && c.EnchantmentPreviewOps != null && c.EnchantmentPreviewOps.Count > 0)
                result.EnchantmentPreviewOps = c.EnchantmentPreviewOps;
            if ((result.EnchantmentTooltips == null || result.EnchantmentTooltips.Count == 0) && c.EnchantmentTooltips != null && c.EnchantmentTooltips.Count > 0)
                result.EnchantmentTooltips = c.EnchantmentTooltips;
            if ((result.EnchantmentTooltipsEn == null || result.EnchantmentTooltipsEn.Count == 0) && c.EnchantmentTooltipsEn != null && c.EnchantmentTooltipsEn.Count > 0)
                result.EnchantmentTooltipsEn = c.EnchantmentTooltipsEn;
            if ((result.CounterDisplays == null || result.CounterDisplays.Length == 0) && c.CounterDisplays != null && c.CounterDisplays.Length > 0)
                result.CounterDisplays = c.CounterDisplays;
            if ((result.ExtraDisplayTags == null || result.ExtraDisplayTags.Length == 0) && c.ExtraDisplayTags != null && c.ExtraDisplayTags.Length > 0)
                result.ExtraDisplayTags = c.ExtraDisplayTags;
            if (result.MonsterPreview == null && c.MonsterPreview != null)
                result.MonsterPreview = c.MonsterPreview;
        }

        if (topIsBorrowShellNewCard)
        {
            result.IsCustomBorrowShell = true;
            result.IsPatchOverride = false;
        }
        if (string.IsNullOrEmpty(result.Locale)) result.Locale = "zh-CN";
        return result;
    }

    public void NotifyLocaleReloaded(string overlayRevision = "")
    {
        if (!string.IsNullOrWhiteSpace(overlayRevision))
        {
            _overlayRevision = overlayRevision;
        }
        _resolveMemo.Clear();
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

    /// <summary>切换服务器时卸载旧服写入的 server_overlay 文本层并刷新原版 LRU 缓存。</summary>
    internal void ClearServerOverlayLayer()
    {
        bool hadEntries;
        lock (_sync)
        {
            hadEntries = _overlayHandles.Count > 0;
            for (int i = _overlayHandles.Count - 1; i >= 0; i--)
            {
                try { _overlayHandles[i]?.Dispose(); } catch { }
            }
            _overlayHandles.Clear();
            _overlayRevision = string.Empty;
            _overlayTotalEntries = 0;
        }
        if (hadEntries)
        {
            NotifyLocaleReloaded("server-overlay-cleared");
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
            string cfgDir = BepInEx.Paths.ConfigPath ?? string.Empty;
            string alt = Path.Combine(cfgDir, "custom_zh_overlay.json");

            // D7-P1-02：比较本地缓存与 BepInEx/config 离线词典的 revision / 最后写入时间，择最新加载
            string picked = OverlayCandidateSelector.PickLatestPath(new[] { cached, alt });
            if (!string.IsNullOrEmpty(picked) && File.Exists(picked))
            {
                string tag = string.Equals(picked, cached, StringComparison.OrdinalIgnoreCase)
                    ? "local-cache"
                    : "config-overlay";
                LoadOverlayJson(File.ReadAllText(picked, Encoding.UTF8), tag);
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L1113", diagEx); }
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
        var tooltipType = GameInspector.FindTypeSafe("TheBazaar.Tooltips.CardTooltipData");
        if (tooltipType != null)
        {
            _tooltipCardInstanceProp = AccessTools.Property(tooltipType, "CardInstance");
            _tooltipCardTemplateProp = AccessTools.Property(tooltipType, "CardTemplate");
            _tooltipCanFuseProp = AccessTools.Property(tooltipType, "CanFuse");

            patcher.TryPatch(
                tooltipType,
                "GetTitle",
                Type.EmptyTypes,
                postfix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(CardTooltipGetTitlePostfix)),
                optional: true,
                label: "L10n.CardTooltipData.GetTitle");

            // CardTooltipData.GetCardDescription() 在当前客户端返回 ValueTuple<string, TooltipSegment?>，
            // 需按真实返回类型动态绑定泛型元组 Postfix（同时兼容旧版 string 返回值），否则会被 SafeHarmonyPatcher 静默跳过。
            var getDescMethod = AccessTools.Method(tooltipType, "GetCardDescription", Type.EmptyTypes);
            if (getDescMethod != null)
            {
                MethodInfo descPostfix = null;
                Type retType = getDescMethod.ReturnType;
                if (retType == typeof(string))
                {
                    descPostfix = AccessTools.Method(typeof(LocalizationOverlayModule), nameof(CardTooltipGetDescriptionStringPostfix));
                }
                else if (retType.IsGenericType
                    && retType.GetGenericTypeDefinition() == typeof(ValueTuple<,>)
                    && retType.GetGenericArguments()[0] == typeof(string))
                {
                    var openGeneric = typeof(LocalizationOverlayModule).GetMethod(
                        nameof(CardTooltipGetDescriptionTuplePostfix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (openGeneric != null && openGeneric.IsGenericMethodDefinition)
                    {
                        descPostfix = openGeneric.MakeGenericMethod(retType.GetGenericArguments()[1]);
                    }
                }

                if (descPostfix != null)
                {
                    patcher.TryPatchMethod(
                        getDescMethod,
                        postfix: descPostfix,
                        optional: true,
                        label: "L10n.CardTooltipData.GetCardDescription");
                }
            }

            // 修正命名空间：真实类型为 BazaarGameShared.Domain.Tooltips.ETooltipType，消除启动告警 #1
            var eTooltipType = GameInspector.FindTypeSafe(
                "BazaarGameShared.Domain.Tooltips.ETooltipType",
                "BazaarGameShared.Domain.Core.Types.ETooltipType");
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

            // V14：自定义 Mod 卡真实四阶升级预览拦截（防止悬停升级预览时串成原版底板卡的四阶数值）
            var eCardAttrType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Core.Types.ECardAttributeType");
            var eTierType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Core.Types.ETier");
            if (eCardAttrType != null)
            {
                patcher.TryPatch(
                    tooltipType,
                    "NextTierHasAttribute",
                    new[] { eCardAttrType },
                    postfix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(CardTooltipNextTierHasAttrPostfix)),
                    optional: true,
                    label: "L10n.CardTooltipData.NextTierHasAttribute");
            }
            if (eCardAttrType != null && eTierType != null)
            {
                patcher.TryPatch(
                    tooltipType,
                    "GetBaseValueFromCardTemplate",
                    new[] { eCardAttrType, eTierType, typeof(float), eTierType },
                    postfix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(CardTooltipGetBaseValuePostfix)),
                    optional: true,
                    label: "L10n.CardTooltipData.GetBaseValueFromCardTemplate");
            }

            // 阶段 8 通用详情/升阶/附魔预览全链路拦截：彻底切断原版借壳模板属性渗入自定义卡预览
            var cardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.Card");
            var itemCardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.ItemCard");
            var itCardType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Cards.ITCard");
            var tCardItemType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Cards.Item.TCardItem");
            var cardExtType = GameInspector.FindTypeSafe("TheBazaar.CardExtensions");
            if (cardExtType != null && cardType != null && itCardType != null)
            {
                patcher.TryPatch(
                    cardExtType,
                    "BuildAttributeDictionaryForTier",
                    new[] { cardType, itCardType, typeof(ETier) },
                    prefix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(BuildAttributeDictionaryForTierPrefix)),
                    optional: true,
                    label: "L10n.CardExtensions.BuildAttributeDictionaryForTier");
            }
            if (cardExtType != null && tCardItemType != null && itemCardType != null)
            {
                patcher.TryPatch(
                    cardExtType,
                    "ApplyPreviewEnchantment",
                    new[] { tCardItemType, itemCardType, typeof(Dictionary<ECardAttributeType, int>), typeof(EEnchantmentType?) },
                    prefix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(ApplyPreviewEnchantmentPrefix)),
                    optional: true,
                    label: "L10n.CardExtensions.ApplyPreviewEnchantment");
            }

            var lockOptType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.LockMode.CardLockVariantOptions");
            if (lockOptType != null)
            {
                _cardTierOptionType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.LockMode.CardTierOption");
                _cardEnchOptionType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.LockMode.CardEnchantmentOption");
                _lockCreateTierOptionMethod = AccessTools.Method(lockOptType, "CreateTierOption", new[] { typeof(ETier) });
                _lockCreateNoneEnchOptionMethod = AccessTools.Method(lockOptType, "CreateNoneEnchantmentOption", Type.EmptyTypes);
                _lockCreateEnchOptionMethod = AccessTools.Method(lockOptType, "CreateEnchantmentOption", new[] { typeof(EEnchantmentType) });

                var buildTierM = AccessTools.Method(lockOptType, "BuildTierOptions", new[] { tooltipType });
                if (buildTierM != null)
                {
                    patcher.TryPatchMethod(
                        buildTierM,
                        postfix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(BuildLockTierOptionsPostfix)),
                        optional: true,
                        label: "L10n.CardLockVariantOptions.BuildTierOptions");
                }
                if (cardType != null)
                {
                    var buildEnchM = AccessTools.Method(lockOptType, "BuildEnchantmentOptions", new[] { cardType });
                    if (buildEnchM != null)
                    {
                        patcher.TryPatchMethod(
                            buildEnchM,
                            postfix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(BuildLockEnchantmentOptionsPostfix)),
                            optional: true,
                            label: "L10n.CardLockVariantOptions.BuildEnchantmentOptions");
                    }
                }
            }

            // D3-P1-11：挂钩 TagRenderer.Render 注入自定义 ExtraDisplayTags（兼容原生 ECardTag 与自定义字符串标签）
            var tagRendererType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.TagRenderer");
            if (tagRendererType != null)
            {
                _tagRendererCtrlField = AccessTools.Field(tagRendererType, "_controller");
                MethodInfo renderMethod = null;
                foreach (var m in tagRendererType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "Render") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 4 && ps[2].ParameterType == typeof(ECardType))
                    {
                        renderMethod = m;
                        break;
                    }
                }
                if (renderMethod != null)
                {
                    patcher.TryPatchMethod(
                        renderMethod,
                        prefix: AccessTools.Method(typeof(LocalizationOverlayModule), nameof(TagRendererRenderPrefix)),
                        optional: true,
                        label: "L10n.TagRenderer.Render");
                }
            }
        }
    }

    // ── Harmony 钩子回调 ─────────────────────────────────────────────────────

    private static void LogL10nHookErrorThrottled(string hookName, Exception ex)
    {
        int cnt = Interlocked.Increment(ref _l10nHookErrorCount);
        if (cnt <= 5 || cnt % 50 == 0)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[LocalizationOverlay] {hookName} 已安全拦截异常 (count={cnt}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool LocalizationServiceTryGetTextPrefix(string hash, string text, out string translation, ref bool __result)
    {
        translation = null;
        try
        {
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
        }
        catch (Exception ex)
        {
            LogL10nHookErrorThrottled(nameof(LocalizationServiceTryGetTextPrefix), ex);
        }
        return true;
    }

    private static void LocalizableTextGetPostfix(ref string __result)
    {
        try
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
        catch (Exception ex)
        {
            LogL10nHookErrorThrottled(nameof(LocalizableTextGetPostfix), ex);
        }
    }

    private static void CardTooltipGetTitlePostfix(object __instance, ref string __result)
    {
        try
        {
            var inst = Instance;
            if (inst == null || __instance == null) return;
            string locale = inst.CurrentLocale;
            bool isZh = IsChineseLocale(locale);

            if (!TryExtractCardMetadata(__instance, out var ov, out _)) return;
            string title = PickLocalizedString(isZh, ov.Title, ov.TitleEn, ov.IsCustomBorrowShell);
            if (string.IsNullOrWhiteSpace(title)) return;

            // 保留原版生成的 <style=Tiny> 或附魔富文本前缀（如 "剧毒\n"），仅替换标题正文段
            if (!string.IsNullOrEmpty(__result))
            {
                int nl = __result.LastIndexOf('\n');
                if (nl >= 0 && nl < __result.Length - 1)
                {
                    __result = __result.Substring(0, nl + 1) + title;
                    return;
                }
            }
            __result = title;
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L1395", diagEx); }
    }

    private static bool TryResolveCardDescriptionOverride(object tooltipData, out string renderedDescription)
    {
        renderedDescription = null;
        var inst = Instance;
        if (inst == null || tooltipData == null) return false;
        string locale = inst.CurrentLocale;
        bool isZh = IsChineseLocale(locale);

        if (!TryExtractCardMetadata(tooltipData, out var ov, out var cardInst)) return false;
        string desc = PickLocalizedString(isZh, ov.Description, ov.DescriptionEn, ov.IsCustomBorrowShell);
        if (string.IsNullOrWhiteSpace(desc)) return false;

        // D3-P1-11：描述渲染同样透传 ov 与 isUpgradePreview，使描述中的 {attr.*} 支持非枚举回退与升阶对比箭头
        bool isUpgradePreview = SafeGetPropertyValue(_tooltipCanFuseProp, tooltipData) is bool cf && cf;
        renderedDescription = RenderDynamicAttributePlaceholders(desc, cardInst, ov, isUpgradePreview);
        return true;
    }

    private static void CardTooltipGetDescriptionStringPostfix(object __instance, ref string __result)
    {
        try
        {
            if (TryResolveCardDescriptionOverride(__instance, out string desc))
            {
                __result = desc;
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L1425", diagEx); }
    }

    private static void CardTooltipGetDescriptionTuplePostfix<TSecond>(object __instance, ref ValueTuple<string, TSecond> __result)
    {
        try
        {
            if (TryResolveCardDescriptionOverride(__instance, out string desc))
            {
                __result = (desc, __result.Item2);
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L1437", diagEx); }
    }

    private static void CardTooltipGetTooltipsOfTypePostfix(object __instance, object __0, ref IList __result)
    {
        try
        {
            var inst = Instance;
            if (inst == null || __instance == null || __0 == null) return;
            string locale = inst.CurrentLocale;
            bool isZh = IsChineseLocale(locale);

            if (!TryExtractCardMetadata(__instance, out var ov, out var cardInst)) return;

            string tipKind = __0.ToString();
            bool isPassive = string.Equals(tipKind, "Passive", StringComparison.OrdinalIgnoreCase);
            string[] lines = null;
            if (string.Equals(tipKind, "Active", StringComparison.OrdinalIgnoreCase))
            {
                lines = PickLocalizedLines(isZh, ov.ActiveTooltips, ov.ActiveTooltipsEn, ov.IsCustomBorrowShell);
            }
            else if (isPassive)
            {
                lines = PickLocalizedLines(isZh, ov.PassiveTooltips, ov.PassiveTooltipsEn, ov.IsCustomBorrowShell);
            }

            string[] enchLinesToAppend = null;
            var enchMap = PickLocalizedEnchantTooltips(isZh, ov.EnchantmentTooltips, ov.EnchantmentTooltipsEn, ov.IsCustomBorrowShell);
            if (enchMap != null && cardInst != null && isPassive)
            {
                var cardMembers = GetCardMemberCache(cardInst.GetType());
                string ench = SafeGetPropertyValue(cardMembers.Enchantment, cardInst)?.ToString();
                if (!string.IsNullOrEmpty(ench) && enchMap.TryGetValue(ench, out var foundEnchLines) && foundEnchLines != null && foundEnchLines.Length > 0)
                    enchLinesToAppend = foundEnchLines;
            }

            bool hasBaseLines = lines != null && lines.Length > 0;
            bool hasEnchLines = enchLinesToAppend != null && enchLinesToAppend.Length > 0;
            bool suppressVanillaPassives = isPassive && ShouldSuppressVanillaPassives(ov.IsCustomBorrowShell, lines);

            if (!hasBaseLines && !hasEnchLines && !suppressVanillaPassives) return;

            EnsureTLocalizableTextMeta();
            if (_tLocalizableTextType == null) return;

            var listType = typeof(List<>).MakeGenericType(_tLocalizableTextType);
            if (Activator.CreateInstance(listType) is not IList customList) return;

            bool isUpgradePreview = SafeGetPropertyValue(_tooltipCanFuseProp, __instance) is bool cf && cf;

            if (hasBaseLines)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string rendered = RenderDynamicAttributePlaceholders(lines[i], cardInst, ov, isUpgradePreview);
                    object locObj = CreateTLocalizableText(rendered);
                    if (locObj != null) customList.Add(locObj);
                }
            }
            else if (!suppressVanillaPassives && __result != null)
            {
                // 仅对非借壳卡（或原位 Patch 卡）保留原列表并追加自定义附魔词条；
                // 全新借壳自定义卡未配 PassiveTooltips 时严禁把借壳底座被动带入 customList
                foreach (var existing in __result) customList.Add(existing);
            }

            if (hasEnchLines)
            {
                foreach (var el in enchLinesToAppend)
                {
                    object locObj = CreateTLocalizableText(RenderDynamicAttributePlaceholders(el, cardInst, ov, isUpgradePreview));
                    if (locObj != null) customList.Add(locObj);
                }
            }

            if (customList.Count > 0 || suppressVanillaPassives)
            {
                __result = customList;
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L1517", diagEx); }
    }

    private static bool TagRendererRenderPrefix(object __instance, object __0, ref HashSet<ECardTag> __1, ECardType __2, object __3)
    {
        try
        {
            if (__instance == null || __0 == null) return true;
            if (!TryResolveOverrideForCardObject(__0, null, out var ov)
                || ov?.ExtraDisplayTags == null || ov.ExtraDisplayTags.Length == 0)
            {
                return true;
            }

            var extraEnums = new List<ECardTag>();
            var customFreeformTags = new List<string>();
            for (int i = 0; i < ov.ExtraDisplayTags.Length; i++)
            {
                string rawTag = ov.ExtraDisplayTags[i];
                if (string.IsNullOrWhiteSpace(rawTag)) continue;
                string trimmed = rawTag.Trim();
                if (Enum.TryParse<ECardTag>(trimmed, ignoreCase: true, out var parsedTag))
                {
                    extraEnums.Add(parsedTag);
                }
                else
                {
                    customFreeformTags.Add(trimmed);
                }
            }

            if (extraEnums.Count == 0 && customFreeformTags.Count == 0)
                return true;

            var mergedSet = __1 != null ? new HashSet<ECardTag>(__1) : new HashSet<ECardTag>();
            for (int i = 0; i < extraEnums.Count; i++) mergedSet.Add(extraEnums[i]);

            // 若全部额外标签皆为原生 ECardTag 枚举，直接并入 HashSet 交由原版管线原生着色渲染
            if (customFreeformTags.Count == 0)
            {
                __1 = mergedSet;
                return true;
            }

            // 存在自定义自由文本标签：尝试直接构造最终字符串列表调用 _controller.InitTooltipTag
            object ctrl = _tagRendererCtrlField?.GetValue(__instance);
            if (ctrl != null)
            {
                _tooltipTagInitMethod ??= AccessTools.Method(ctrl.GetType(), "InitTooltipTag");
                if (_tooltipTagInitMethod != null)
                {
                    var cardSizeProp = __0.GetType().GetProperty("Size", BindingFlags.Public | BindingFlags.Instance);
                    string sizeStr = cardSizeProp?.GetValue(__0)?.ToString() ?? "Medium";

                    var formattedList = new List<string>();
                    foreach (var t in mergedSet)
                    {
                        string colored = TryColorTagWithNativeTypography(t);
                        if (!string.IsNullOrWhiteSpace(colored)) formattedList.Add(colored);
                    }

                    string locale = Instance?.CurrentLocale ?? "zh-CN";
                    for (int i = 0; i < customFreeformTags.Count; i++)
                    {
                        string raw = customFreeformTags[i];
                        string disp = Instance != null && IsChineseLocale(locale)
                            && Instance.TryResolveText(locale, null, raw, "CardTag", out string zh)
                            ? zh
                            : raw;
                        if (!formattedList.Contains(disp))
                        {
                            formattedList.Add(disp);
                        }
                    }

                    _tooltipTagInitMethod.Invoke(ctrl, new object[] { sizeStr, __2, formattedList, __3 });
                    return false;
                }
            }

            // 反射回退：至少把可解析的枚举标签并入原版集合
            __1 = mergedSet;
            return true;
        }
        catch (Exception ex)
        {
            LogL10nHookErrorThrottled(nameof(TagRendererRenderPrefix), ex);
            return true;
        }
    }

    private static string TryColorTagWithNativeTypography(ECardTag tag)
    {
        try
        {
            if (_dataTooltipTypographyProp == null)
            {
                var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
                _dataTooltipTypographyProp = dataType?.GetProperty("TooltipTypography", BindingFlags.Public | BindingFlags.Static);
            }
            object typo = _dataTooltipTypographyProp?.GetValue(null);
            if (typo != null)
            {
                _tooltipColorTagMethod ??= AccessTools.Method(typo.GetType(), "ColorTag", new[] { typeof(ECardTag) })
                    ?? AccessTools.Method(typo.GetType(), "ColorTag", new[] { typeof(string) });
                if (_tooltipColorTagMethod != null)
                {
                    var ps = _tooltipColorTagMethod.GetParameters();
                    object arg = ps.Length == 1 && ps[0].ParameterType == typeof(ECardTag)
                        ? (object)tag
                        : tag.ToString();
                    if (_tooltipColorTagMethod.Invoke(typo, new[] { arg }) is string s && !string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L1633", diagEx); }

        string raw = tag.ToString();
        string locale = Instance?.CurrentLocale ?? "zh-CN";
        if (Instance != null && IsChineseLocale(locale) && Instance.TryResolveText(locale, null, raw, null, out string zh))
            return zh;
        return raw;
    }

    private static void CardTooltipNextTierHasAttrPostfix(object __instance, object __0, ref bool __result)
    {
        try
        {
            var inst = Instance;
            if (inst == null || __instance == null || __0 == null) return;
            if (!TryExtractCardMetadata(__instance, out var ov, out var cardInst)) return;
            if (ov?.TierAttributes == null || ov.TierAttributes.Count == 0 || cardInst == null) return;

            var members = GetCardMemberCache(cardInst.GetType());
            string curTier = SafeGetPropertyValue(members.Tier, cardInst)?.ToString();
            string nextTier = GetNextTierName(curTier, ov.TierAttributes);
            if (nextTier == null)
            {
                __result = false;
                return;
            }
            string attrName = __0.ToString();
            __result = ov.TierAttributes.TryGetValue(nextTier, out var nextMap)
                       && nextMap != null
                       && nextMap.ContainsKey(attrName);
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L1664", diagEx); }
    }

    private static void CardTooltipGetBaseValuePostfix(object __instance, object __0, object __1, float __2, object __3, ref float? __result)
    {
        try
        {
            var inst = Instance;
            if (inst == null || __instance == null || __0 == null || __1 == null || __3 == null) return;
            if (!TryExtractCardMetadata(__instance, out var ov, out var cardInst)) return;
            if (ov?.TierAttributes == null || ov.TierAttributes.Count == 0) return;

            string attrName = __0.ToString();
            string nextTier = __1.ToString();
            string curTier = __3.ToString();
            if (!ov.TierAttributes.TryGetValue(nextTier, out var nextMap) || nextMap == null || !nextMap.TryGetValue(attrName, out double nextRaw))
            {
                // P0-27：全新借壳自定义卡在 TierAttributes 中未声明该属性时，绝不泄露原版借壳底座基值
                if (ov.IsCustomBorrowShell)
                {
                    bool presentOnInstance = false;
                    if (cardInst != null)
                    {
                        var members = GetCardMemberCache(cardInst.GetType());
                        if (SafeGetPropertyValue(members.Attributes, cardInst) is IDictionary curAttrs)
                        {
                            foreach (DictionaryEntry kv in curAttrs)
                            {
                                if (string.Equals(kv.Key?.ToString(), attrName, StringComparison.OrdinalIgnoreCase))
                                {
                                    presentOnInstance = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (!presentOnInstance)
                    {
                        __result = null;
                    }
                }
                return;
            }

            bool isDur = DurationAttrNames.Contains(attrName);
            if (ov.TierAttributes.TryGetValue(curTier, out var curMap) && curMap != null && curMap.TryGetValue(attrName, out double curRaw))
            {
                double curEffectiveRaw = isDur ? __2 * 1000.0 : __2;
                double nextEffectiveRaw = ComputeUpgradePreviewAttrValue(attrName, curEffectiveRaw, curRaw, nextRaw);
                __result = (float)(isDur ? nextEffectiveRaw / 1000.0 : nextEffectiveRaw);
            }
            else
            {
                __result = (float)(isDur ? nextRaw / 1000.0 : nextRaw);
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L1721", diagEx); }
    }

    // ── 通用自定义卡/补丁卡 详情与品阶/附魔预览全链路拦截 ─────────────────────

    private static readonly string[] OrderedTierNames = { "Bronze", "Silver", "Gold", "Diamond", "Legendary" };

    private static bool TryResolveOverrideForCardObject(object cardObj, object cardTemplateObj, out CardTextOverride ov)
    {
        ov = null;
        var inst = Instance;
        if (inst == null) return false;

        string iid = null;
        string customId = CustomContentVisualModule.TryGetPreviewCustomCardId(cardObj);
        string tplId = null;
        if (cardObj != null)
        {
            var members = GetCardMemberCache(cardObj.GetType());
            iid = SafeGetPropertyValue(members.InstanceId, cardObj)?.ToString();
            tplId = SafeGetPropertyValue(members.TemplateId, cardObj)?.ToString();
        }
        if (string.IsNullOrEmpty(tplId) && cardTemplateObj != null)
        {
            var tplMembers = GetCardMemberCache(cardTemplateObj.GetType());
            tplId = SafeGetPropertyValue(tplMembers.Id, cardTemplateObj)?.ToString();
        }
        return inst.TryResolveCardMetadata(iid, customId, tplId, out ov) && ov != null;
    }

    private static Dictionary<string, double> SelectBestTierAttrMap(
        Dictionary<string, Dictionary<string, double>> tierAttrs,
        ETier requestedTier)
    {
        if (tierAttrs == null || tierAttrs.Count == 0) return null;
        string reqName = requestedTier.ToString();
        if (tierAttrs.TryGetValue(reqName, out var exact) && exact != null)
            return exact;

        int reqIdx = Array.FindIndex(OrderedTierNames, t => string.Equals(t, reqName, StringComparison.OrdinalIgnoreCase));
        if (reqIdx >= 0)
        {
            for (int i = reqIdx - 1; i >= 0; i--)
            {
                if (tierAttrs.TryGetValue(OrderedTierNames[i], out var lower) && lower != null)
                    return lower;
            }
            for (int i = reqIdx + 1; i < OrderedTierNames.Length; i++)
            {
                if (tierAttrs.TryGetValue(OrderedTierNames[i], out var higher) && higher != null)
                    return higher;
            }
        }
        foreach (var kv in tierAttrs)
        {
            if (kv.Value != null) return kv.Value;
        }
        return null;
    }

    /// <summary>
    /// 通用拦截 CardExtensions.BuildAttributeDictionaryForTier：
    /// 命中自定义 Mod 卡或原位平衡补丁卡时，直接按服务端同步的真实五阶 TierAttributes 构造预览属性表，
    /// 彻底阻断原版借壳模板（如 Z-Sword 钻石 Custom_0=20、CooldownMax=5000）污染 3D 宝石、冷却圈、售价与词条数值。
    /// </summary>
    private static bool BuildAttributeDictionaryForTierPrefix(
        object cardInstance,
        object cardTemplate,
        ETier tier,
        ref Dictionary<ECardAttributeType, int> __result)
    {
        try
        {
            if (!TryResolveOverrideForCardObject(cardInstance, cardTemplate, out var ov)
                || ov?.TierAttributes == null || ov.TierAttributes.Count == 0)
            {
                return true;
            }

            var targetTierMap = SelectBestTierAttrMap(ov.TierAttributes, tier);
            if (targetTierMap == null)
            {
                return true;
            }

            // 收集该卡任意品阶或附魔运算中出现过的受管属性键：切阶/去附魔时绝不残留旧阶或旧附魔脏值
            var managedKeys = new HashSet<ECardAttributeType>();
            foreach (var tm in ov.TierAttributes.Values)
            {
                if (tm == null) continue;
                foreach (var k in tm.Keys)
                {
                    if (Enum.TryParse<ECardAttributeType>(k, ignoreCase: true, out var e))
                        managedKeys.Add(e);
                }
            }
            if (ov.EnchantmentPreviewOps != null)
            {
                foreach (var ops in ov.EnchantmentPreviewOps.Values)
                {
                    if (ops == null) continue;
                    for (int i = 0; i < ops.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(ops[i]?.Attribute)
                            && Enum.TryParse<ECardAttributeType>(ops[i].Attribute, ignoreCase: true, out var e))
                        {
                            managedKeys.Add(e);
                        }
                    }
                }
            }

            var dict = new Dictionary<ECardAttributeType, int>();

            // 仅保留非受管结构属性（如非品阶/非附魔控制的基础元数据），随后全量写入目标品阶的真实基础数值
            if (cardInstance != null)
            {
                var members = GetCardMemberCache(cardInstance.GetType());
                if (SafeGetPropertyValue(members.Attributes, cardInstance) is IDictionary curAttrs)
                {
                    foreach (DictionaryEntry kv in curAttrs)
                    {
                        if (kv.Key is ECardAttributeType attrEnum && !managedKeys.Contains(attrEnum) && kv.Value != null)
                        {
                            try { dict[attrEnum] = Convert.ToInt32(kv.Value); } catch { }
                        }
                    }
                }
            }

            foreach (var kv in targetTierMap)
            {
                if (Enum.TryParse<ECardAttributeType>(kv.Key, ignoreCase: true, out var attrEnum))
                {
                    dict[attrEnum] = (int)Math.Round(kv.Value);
                }
            }

            __result = dict;
            return false;
        }
        catch (Exception ex)
        {
            LogL10nHookErrorThrottled(nameof(BuildAttributeDictionaryForTierPrefix), ex);
            return true;
        }
    }

    /// <summary>
    /// 通用拦截 CardExtensions.ApplyPreviewEnchantment：
    /// 命中自定义 Mod 卡或原位补丁卡时，按服务端同步的 EnchantmentPreviewOps 应用附魔运算（涵盖 Add/Subtract/Multiply/Set/属性引用），
    /// 彻底避免回退套用原版借壳模板的附魔数值。
    /// </summary>
    private static bool ApplyPreviewEnchantmentPrefix(
        object itemTemplate,
        object previewItemCard,
        Dictionary<ECardAttributeType, int> previewAttributes,
        EEnchantmentType? selectedEnchantment)
    {
        try
        {
            if (previewItemCard == null) return true;
            if (!TryResolveOverrideForCardObject(previewItemCard, itemTemplate, out var ov))
            {
                return true;
            }
            bool isModdedCard = (ov.TierAttributes != null && ov.TierAttributes.Count > 0)
                || (ov.EnchantmentPreviewOps != null && ov.EnchantmentPreviewOps.Count > 0)
                || (ov.EnchantmentTooltips != null && ov.EnchantmentTooltips.Count > 0);
            if (!isModdedCard)
            {
                return true;
            }

            var members = GetCardMemberCache(previewItemCard.GetType());
            if (members.Enchantment != null && members.Enchantment.CanWrite)
            {
                members.Enchantment.SetValue(previewItemCard, selectedEnchantment);
            }

            if (selectedEnchantment.HasValue && previewAttributes != null && ov.EnchantmentPreviewOps != null
                && ov.EnchantmentPreviewOps.TryGetValue(selectedEnchantment.Value.ToString(), out var ops) && ops != null)
            {
                for (int i = 0; i < ops.Length; i++)
                {
                    var op = ops[i];
                    if (op == null || string.IsNullOrWhiteSpace(op.Attribute)) continue;
                    if (!Enum.TryParse<ECardAttributeType>(op.Attribute, ignoreCase: true, out var attrEnum)) continue;

                    double operand = op.Value;
                    if (!string.IsNullOrWhiteSpace(op.SourceAttribute)
                        && Enum.TryParse<ECardAttributeType>(op.SourceAttribute, ignoreCase: true, out var srcEnum)
                        && previewAttributes.TryGetValue(srcEnum, out int srcInt))
                    {
                        operand = EvaluatePreviewAttrDerivedValue(srcInt, op.SourceMultiplier, op.SourceAddOffset);
                    }

                    double cur = previewAttributes.TryGetValue(attrEnum, out int existing)
                        ? existing
                        : (attrEnum == ECardAttributeType.Multicast ? 1 : 0);

                    string opKind = op.Operation?.Trim() ?? "Set";
                    double next;
                    if (string.Equals(opKind, "Add", StringComparison.OrdinalIgnoreCase))
                        next = cur + operand;
                    else if (string.Equals(opKind, "Subtract", StringComparison.OrdinalIgnoreCase))
                        next = Math.Max(0, cur - operand);
                    else if (string.Equals(opKind, "Multiply", StringComparison.OrdinalIgnoreCase))
                        next = cur * operand;
                    else if (string.Equals(opKind, "AdditiveMultiply", StringComparison.OrdinalIgnoreCase))
                        next = cur * (1.0 + operand);
                    else
                        next = operand;

                    previewAttributes[attrEnum] = (int)Math.Round(next);
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            LogL10nHookErrorThrottled(nameof(ApplyPreviewEnchantmentPrefix), ex);
            return true;
        }
    }

    /// <summary>同步对齐详情弹窗的「级别」下拉菜单，确保任意自定义卡（含借用高阶/低阶借壳或传奇卡）显示自身真实品阶列表。</summary>
    private static void BuildLockTierOptionsPostfix(object tooltipData, ref object __result)
    {
        try
        {
            if (tooltipData == null || _lockCreateTierOptionMethod == null || _cardTierOptionType == null) return;
            object cardInst = SafeGetPropertyValue(_tooltipCardInstanceProp, tooltipData);
            object cardTpl = SafeGetPropertyValue(_tooltipCardTemplateProp, tooltipData);
            if (!TryResolveOverrideForCardObject(cardInst, cardTpl, out var ov)
                || ov?.TierAttributes == null || ov.TierAttributes.Count == 0)
            {
                return;
            }

            var tiers = new List<ETier>();
            foreach (string tn in OrderedTierNames)
            {
                if (ov.TierAttributes.ContainsKey(tn) && Enum.TryParse<ETier>(tn, ignoreCase: true, out var parsedTier))
                {
                    tiers.Add(parsedTier);
                }
            }
            if (cardInst != null)
            {
                var members = GetCardMemberCache(cardInst.GetType());
                if (SafeGetPropertyValue(members.Tier, cardInst) is ETier curTier && !tiers.Contains(curTier))
                {
                    tiers.Add(curTier);
                    tiers.Sort();
                }
            }
            if (tiers.Count == 0) return;

            var listType = typeof(List<>).MakeGenericType(_cardTierOptionType);
            if (Activator.CreateInstance(listType) is not IList newList) return;
            for (int i = 0; i < tiers.Count; i++)
            {
                object opt = _lockCreateTierOptionMethod.Invoke(null, new object[] { tiers[i] });
                if (opt != null) newList.Add(opt);
            }
            if (newList.Count > 0) __result = newList;
        }
        catch (Exception ex)
        {
            LogL10nHookErrorThrottled(nameof(BuildLockTierOptionsPostfix), ex);
        }
    }

    /// <summary>同步对齐详情弹窗的「附魔」下拉菜单，确保任意自定义卡显示自身真实支持的附魔选项清单。</summary>
    private static void BuildLockEnchantmentOptionsPostfix(object cardInstance, ref object __result)
    {
        try
        {
            if (cardInstance == null || _lockCreateNoneEnchOptionMethod == null || _lockCreateEnchOptionMethod == null || _cardEnchOptionType == null) return;
            if (!TryResolveOverrideForCardObject(cardInstance, null, out var ov)) return;
            bool hasOps = ov.EnchantmentPreviewOps != null && ov.EnchantmentPreviewOps.Count > 0;
            bool hasTips = ov.EnchantmentTooltips != null && ov.EnchantmentTooltips.Count > 0;
            if (!hasOps && !hasTips) return;

            var enchSet = new HashSet<EEnchantmentType>();
            if (hasOps)
            {
                foreach (var k in ov.EnchantmentPreviewOps.Keys)
                {
                    if (Enum.TryParse<EEnchantmentType>(k, ignoreCase: true, out var e)) enchSet.Add(e);
                }
            }
            if (hasTips)
            {
                foreach (var k in ov.EnchantmentTooltips.Keys)
                {
                    if (Enum.TryParse<EEnchantmentType>(k, ignoreCase: true, out var e)) enchSet.Add(e);
                }
            }
            if (enchSet.Count == 0) return;

            var ordered = new List<EEnchantmentType>(enchSet);
            ordered.Sort();

            var listType = typeof(List<>).MakeGenericType(_cardEnchOptionType);
            if (Activator.CreateInstance(listType) is not IList newList) return;
            object noneOpt = _lockCreateNoneEnchOptionMethod.Invoke(null, null);
            if (noneOpt != null) newList.Add(noneOpt);
            for (int i = 0; i < ordered.Count; i++)
            {
                object opt = _lockCreateEnchOptionMethod.Invoke(null, new object[] { ordered[i] });
                if (opt != null) newList.Add(opt);
            }
            if (newList.Count > 0) __result = newList;
        }
        catch (Exception ex)
        {
            LogL10nHookErrorThrottled(nameof(BuildLockEnchantmentOptionsPostfix), ex);
        }
    }

    // ── 工具方法 ─────────────────────────────────────────────────────────────

    private static string GetNextTierName(string currentTier, Dictionary<string, Dictionary<string, double>> tierAttrs = null)
    {
        if (string.Equals(currentTier, "Bronze", StringComparison.OrdinalIgnoreCase)) return "Silver";
        if (string.Equals(currentTier, "Silver", StringComparison.OrdinalIgnoreCase)) return "Gold";
        if (string.Equals(currentTier, "Gold", StringComparison.OrdinalIgnoreCase)) return "Diamond";
        if (string.Equals(currentTier, "Diamond", StringComparison.OrdinalIgnoreCase)
            && tierAttrs != null
            && tierAttrs.ContainsKey("Legendary"))
        {
            return "Legendary";
        }
        return null;
    }

    private static CardMemberCache GetCardMemberCache(Type runtimeType)
    {
        return _cardMembersByType.GetOrAdd(runtimeType, static t => new CardMemberCache
        {
            InstanceId = t.GetProperty("InstanceId", BindingFlags.Public | BindingFlags.Instance),
            TemplateId = t.GetProperty("TemplateId", BindingFlags.Public | BindingFlags.Instance),
            Attributes = t.GetProperty("Attributes", BindingFlags.Public | BindingFlags.Instance),
            Enchantment = t.GetProperty("Enchantment", BindingFlags.Public | BindingFlags.Instance),
            Tier = t.GetProperty("Tier", BindingFlags.Public | BindingFlags.Instance),
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

    internal static bool TryExtractCardMetadata(object tooltipData, out CardTextOverride ov, out object cardInst)
    {
        ov = null;
        cardInst = SafeGetPropertyValue(_tooltipCardInstanceProp, tooltipData);
        string iid = null;
        string customId = CustomContentVisualModule.TryGetPreviewCustomCardId(cardInst);
        string tplId = null;

        if (cardInst != null)
        {
            var members = GetCardMemberCache(cardInst.GetType());
            iid = SafeGetPropertyValue(members.InstanceId, cardInst)?.ToString();
            tplId = SafeGetPropertyValue(members.TemplateId, cardInst)?.ToString();
        }

        if (string.IsNullOrEmpty(tplId))
        {
            object tpl = SafeGetPropertyValue(_tooltipCardTemplateProp, tooltipData);
            if (tpl != null)
            {
                var tplMembers = GetCardMemberCache(tpl.GetType());
                tplId = SafeGetPropertyValue(tplMembers.Id, tpl)?.ToString();
            }
        }

        return Instance != null && Instance.TryResolveCardMetadata(iid, customId, tplId, out ov) && ov != null;
    }

    private static bool TryExtractCardOverride(object tooltipData, string locale, out CardTextOverride ov, out object cardInst)
    {
        ov = null;
        cardInst = SafeGetPropertyValue(_tooltipCardInstanceProp, tooltipData);
        string iid = null;
        string customId = CustomContentVisualModule.TryGetPreviewCustomCardId(cardInst);
        string tplId = null;

        if (cardInst != null)
        {
            var members = GetCardMemberCache(cardInst.GetType());
            iid = SafeGetPropertyValue(members.InstanceId, cardInst)?.ToString();
            tplId = SafeGetPropertyValue(members.TemplateId, cardInst)?.ToString();
        }

        if (string.IsNullOrEmpty(tplId))
        {
            object tpl = SafeGetPropertyValue(_tooltipCardTemplateProp, tooltipData);
            if (tpl != null)
            {
                var tplMembers = GetCardMemberCache(tpl.GetType());
                tplId = SafeGetPropertyValue(tplMembers.Id, tpl)?.ToString();
            }
        }

        return Instance != null && Instance.TryResolveCardText(locale, iid, customId, tplId, out ov) && ov != null;
    }

    private static string FormatAttrNumber(string attrName, double rawVal)
    {
        bool isDur = DurationAttrNames.Contains(attrName);
        double disp = isDur && Math.Abs(rawVal) >= 100 ? rawVal / 1000.0 : rawVal;
        double rounded = Math.Round(disp, isDur ? 2 : 1);
        return rounded.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string RenderDynamicAttributePlaceholders(
        string template,
        object cardInst,
        CardTextOverride ov = null,
        bool isUpgradePreview = false)
    {
        if (string.IsNullOrEmpty(template) || cardInst == null || template.IndexOf("{attr.", StringComparison.Ordinal) < 0)
        {
            return template ?? string.Empty;
        }

        var members = GetCardMemberCache(cardInst.GetType());
        var attrs = SafeGetPropertyValue(members.Attributes, cardInst) as IDictionary;

        string curTier = SafeGetPropertyValue(members.Tier, cardInst)?.ToString();
        string nextTier = isUpgradePreview ? GetNextTierName(curTier, ov?.TierAttributes) : null;
        Dictionary<string, double> curBaseMap = null;
        Dictionary<string, double> nextBaseMap = null;
        if (ov?.TierAttributes != null && ov.TierAttributes.Count > 0)
        {
            if (!string.IsNullOrEmpty(curTier))
                ov.TierAttributes.TryGetValue(curTier, out curBaseMap);
            if (curBaseMap == null)
            {
                // 兜底：若当前品阶未显式列于 TierAttributes，取第一个可用阶
                foreach (var kv in ov.TierAttributes) { curBaseMap = kv.Value; break; }
            }
            if (nextTier != null)
                ov.TierAttributes.TryGetValue(nextTier, out nextBaseMap);
        }

        return AttrPlaceholderRegex.Replace(template, m =>
        {
            string attrName = m.Groups[1].Value;
            double curRaw = 0;
            bool found = false;

            if (attrs != null)
            {
                foreach (DictionaryEntry kv in attrs)
                {
                    if (string.Equals(kv.Key?.ToString(), attrName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!double.TryParse(kv.Value?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out curRaw))
                            return kv.Value?.ToString() ?? "0";
                        found = true;
                        break;
                    }
                }
            }

            // V14.2 问题 1 通用根治：
            // 当属性不在官方 ECardAttributeType 枚举中（如 GoldGainAmount 或自定义属性）导致运行时字典不含该键时，
            // 自动回退读取服务端同步的四阶 TierAttributes[当前阶]，彻底杜绝界面裸露 "attr.XXX" 乱码，且完整支持升阶预览箭头。
            if (!found && curBaseMap != null && curBaseMap.TryGetValue(attrName, out double tierFallbackVal))
            {
                curRaw = tierFallbackVal;
                found = true;
            }
            if (!found && attrName.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase))
            {
                curRaw = 0;
                found = true;
            }
            if (!found) return m.Value;

            string curStr = FormatAttrNumber(attrName, curRaw);
            if (isUpgradePreview && nextBaseMap != null && nextBaseMap.TryGetValue(attrName, out double nextBaseRaw))
            {
                double nextRaw = nextBaseRaw;
                if (curBaseMap != null && curBaseMap.TryGetValue(attrName, out double curBaseRaw))
                    nextRaw = ComputeUpgradePreviewAttrValue(attrName, curRaw, curBaseRaw, nextBaseRaw);
                string nextStr = FormatAttrNumber(attrName, nextRaw);
                if (!string.Equals(curStr, nextStr, StringComparison.Ordinal))
                    return $"{curStr} → <color=#4ADE80>{nextStr}</color>";
            }
            return curStr;
        });
    }

    /// <summary>
    /// P3-06：计算升级预览时的下一品阶属性值。
    /// 当当前有效属性值相对基础模板值存在明确整倍（如 ≥20 金以太壁垒提灯 35->70 翻倍）或标准半整数条件缩放（0.5x/1.5x/2.5x）时，
    /// 按当前缩放比例外推下一阶预览值（70 -> 140），否则保留基础差值叠加公式 nextBase + (cur - curBase)，避免误伤纯加法成长卡。
    /// </summary>
    internal static double ComputeUpgradePreviewAttrValue(string attrName, double curVal, double curBaseVal, double nextBaseVal)
    {
        double additive = Math.Max(0, nextBaseVal + (curVal - curBaseVal));
        if (string.IsNullOrEmpty(attrName) || !ScalableUpgradePreviewAttrNames.Contains(attrName))
        {
            return additive;
        }
        if (curBaseVal <= 0 || nextBaseVal <= 0 || curVal <= 0)
        {
            return additive;
        }
        if (Math.Abs(curVal - curBaseVal) <= 1e-6 || Math.Abs(nextBaseVal - curBaseVal) <= 1e-6)
        {
            return additive;
        }

        double ratio = curVal / curBaseVal;
        double scaleFactor = 0;

        // 1. 精确整倍缩放（2x / 3x / 4x ...，如条件翻倍光环：35 -> 70；要求 curBase >= 5 避免极小基数加法偶合）
        double nearestInt = Math.Round(ratio);
        if (nearestInt >= 2.0 && nearestInt <= 16.0 && curBaseVal >= 5.0
            && Math.Abs(curVal - curBaseVal * nearestInt) <= 1e-6)
        {
            scaleFactor = nearestInt;
        }
        else
        {
            // 2. 标准半整数条件比例缩放（0.5x / 1.5x / 2.5x ...，如 Shielded +50% 附魔，容忍服务端整数取整 0.5 误差）
            double halfSteps = Math.Round(ratio * 2.0);
            if (halfSteps >= 1.0 && halfSteps <= 16.0 && Math.Abs(halfSteps % 2.0) > 1e-6 && curBaseVal >= 10.0)
            {
                double candidate = halfSteps * 0.5;
                double expected = curBaseVal * candidate;
                if (Math.Abs(curVal - expected) <= 0.5001 && Math.Abs(ratio - candidate) <= 0.02)
                {
                    scaleFactor = candidate;
                }
            }
        }

        if (scaleFactor <= 0)
        {
            return additive;
        }

        double scaled = nextBaseVal * scaleFactor;
        bool isDur = DurationAttrNames.Contains(attrName);
        if (!isDur
            && Math.Abs(curBaseVal - Math.Round(curBaseVal)) <= 1e-6
            && Math.Abs(nextBaseVal - Math.Round(nextBaseVal)) <= 1e-6)
        {
            scaled = Math.Round(scaled, MidpointRounding.AwayFromZero);
        }
        return Math.Max(0, scaled);
    }

    private static void EnsureTLocalizableTextMeta()
    {
        if (_tLocalizableTextType != null) return;
        _tLocalizableTextType = GameInspector.FindTypeSafe("BazaarGameShared.Domain.Core.TLocalizableText");
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
            if (cacheObj == null) return;

            var clearMethod = cacheObj.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (clearMethod != null)
            {
                clearMethod.Invoke(cacheObj, null);
                return;
            }

            // 原版 TheBazaar.Localization.LruCache<string, string> 无 Clear() 方法，
            // 直接反射清空内部 _map (Dictionary) 与 _list (LinkedList) 以真正失效旧译文缓存。
            const BindingFlags privFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            var mapObj = cacheObj.GetType().GetField("_map", privFlags)?.GetValue(cacheObj);
            var listObj = cacheObj.GetType().GetField("_list", privFlags)?.GetValue(cacheObj);
            mapObj?.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(mapObj, null);
            listObj?.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(listObj, null);
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L2296", diagEx); }
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
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L2316", diagEx); }
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

    /// <summary>在 _sync 锁内重建所有语种的无锁只读快照，同时清空记忆缓存。</summary>
    private void RebuildSnapshotsLocked()
    {
        var next = new Dictionary<string, LocaleSnapshot>(_byLocale.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _byLocale)
        {
            var src = kv.Value;
            var snap = new LocaleSnapshot
            {
                HashLayers = src.HashLayers.ToArray(),
                GlobalExactLayers = src.GlobalExactLayers.ToArray(),
                CardOverrides = src.CardOverrides.ToArray(),
            };

            foreach (var ctxKv in src.ContextExactLayers)
            {
                if (ctxKv.Value.Count > 0)
                {
                    snap.ContextExactLayers[ctxKv.Key] = ctxKv.Value.ToArray();
                }
            }

            // 将全局正则与限定 Scope 正则（如 NetMessageError）严格拆分，绝不让服务端报错正则污染卡面/UI 渲染热路径
            var globalRegexList = new List<RegexEntry>();
            var scopedBuckets = new Dictionary<string, List<RegexEntry>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < src.RegexLayers.Count; i++)
            {
                var re = src.RegexLayers[i];
                if (re.Scopes == null || re.Scopes.Count == 0 || re.Scopes.Contains("global"))
                {
                    globalRegexList.Add(re);
                }
                else
                {
                    foreach (var sc in re.Scopes)
                    {
                        if (string.IsNullOrWhiteSpace(sc)) continue;
                        if (!scopedBuckets.TryGetValue(sc, out var bucket))
                        {
                            bucket = new List<RegexEntry>();
                            scopedBuckets[sc] = bucket;
                        }
                        bucket.Add(re);
                    }
                }
            }

            snap.GlobalRegexLayers = globalRegexList.ToArray();
            foreach (var sbKv in scopedBuckets)
            {
                snap.ScopedRegexLayers[sbKv.Key] = sbKv.Value.ToArray();
            }

            next[kv.Key] = snap;
        }

        _snapshots = next;
        _resolveMemo.Clear();
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
        var md5 = _tsMd5 ??= MD5.Create();
        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        var buf = _tsUtf8Buf;
        if (buf == null || buf.Length < maxBytes)
        {
            buf = new byte[Math.Max(256, maxBytes)];
            if (buf.Length <= 16384)
            {
                _tsUtf8Buf = buf;
            }
        }
        int byteLen = Encoding.UTF8.GetBytes(text, 0, text.Length, buf, 0);
        byte[] hashBytes = md5.ComputeHash(buf, 0, byteLen);

        Span<char> hex = stackalloc char[32];
        for (int i = 0; i < 16; i++)
        {
            byte b = hashBytes[i];
            hex[i * 2] = HexDigits[b >> 4];
            hex[i * 2 + 1] = HexDigits[b & 0x0F];
        }
        return new string(hex);
    }

    private void SubscribeNativeLocaleChanged()
    {
        try
        {
            UnsubscribeNativeLocaleChanged();
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

            var ev = svcType.GetEvent("LocaleChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ev == null || ev.EventHandlerType == null) return;

            var invokeM = ev.EventHandlerType.GetMethod("Invoke");
            var ps = invokeM?.GetParameters() ?? Array.Empty<ParameterInfo>();
            Delegate del = null;
            if (ps.Length == 0)
            {
                var target = typeof(LocalizationOverlayModule).GetMethod(nameof(OnNativeLocaleChanged0), BindingFlags.NonPublic | BindingFlags.Static);
                if (target != null) del = Delegate.CreateDelegate(ev.EventHandlerType, target, throwOnBindFailure: false);
            }
            else if (ps.Length == 1)
            {
                var open = typeof(LocalizationOverlayModule).GetMethod(nameof(OnNativeLocaleChanged1), BindingFlags.NonPublic | BindingFlags.Static);
                if (open != null)
                {
                    var closed = open.MakeGenericMethod(ps[0].ParameterType);
                    del = Delegate.CreateDelegate(ev.EventHandlerType, closed, throwOnBindFailure: false);
                }
            }
            else if (ps.Length == 2)
            {
                var open = typeof(LocalizationOverlayModule).GetMethod(nameof(OnNativeLocaleChanged2), BindingFlags.NonPublic | BindingFlags.Static);
                if (open != null)
                {
                    var closed = open.MakeGenericMethod(ps[0].ParameterType, ps[1].ParameterType);
                    del = Delegate.CreateDelegate(ev.EventHandlerType, closed, throwOnBindFailure: false);
                }
            }

            if (del != null)
            {
                ev.AddEventHandler(locSvc, del);
                _nativeLocaleSvcObj = locSvc;
                _nativeLocaleChangedEvent = ev;
                _nativeLocaleChangedDelegate = del;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[LocalizationOverlay] 订阅 LocalizationService.LocaleChanged 失败: {ex.Message}");
        }
    }

    private void UnsubscribeNativeLocaleChanged()
    {
        try
        {
            if (_nativeLocaleChangedEvent != null && _nativeLocaleSvcObj != null && _nativeLocaleChangedDelegate != null)
            {
                _nativeLocaleChangedEvent.RemoveEventHandler(_nativeLocaleSvcObj, _nativeLocaleChangedDelegate);
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L2518", diagEx); }
        finally
        {
            _nativeLocaleSvcObj = null;
            _nativeLocaleChangedEvent = null;
            _nativeLocaleChangedDelegate = null;
        }
    }

    private static void OnNativeLocaleChanged0() => HandleNativeLocaleChanged();
    private static void OnNativeLocaleChanged1<T1>(T1 _) => HandleNativeLocaleChanged();
    private static void OnNativeLocaleChanged2<T1, T2>(T1 _, T2 __) => HandleNativeLocaleChanged();

    private static void HandleNativeLocaleChanged()
    {
        var inst = Instance;
        if (inst == null) return;
        inst._resolveMemo.Clear();
        MainThreadDispatcher.Enqueue(() =>
        {
            string newLocale = DetectCurrentGameLocale();
            FlushNativeLocalizationServiceCache();
            TournamentLocalizationPatch.RefreshOnLocaleChanged(newLocale);
            BazaarLauncherPlugin.Host?.EventBus.Publish(new LocaleSwitchedModEvent(newLocale, inst._overlayRevision));
            CustomContentVisualModule.Instance?.ReapplyActiveVisualsOnMainThread();
        });
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

/// <summary>
/// D7-P1-02：离线词典候选选择器——优先按 JSON 内 revision/version 自然序择高，版本相等或缺失时按最后写入时间择新。
/// </summary>
internal static class OverlayCandidateSelector
{
    private static readonly Regex RevisionFieldRegex = new(
        "\"(?:revision|version)\"\\s*:\\s*\"([^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static string PickLatestPath(IEnumerable<string> candidatePaths)
    {
        if (candidatePaths == null) return null;

        string bestPath = null;
        string bestRev = null;
        DateTime bestUtc = DateTime.MinValue;

        foreach (string path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

            string rev = TryReadRevision(path);
            DateTime utc;
            try { utc = File.GetLastWriteTimeUtc(path); }
            catch { utc = DateTime.MinValue; }

            if (bestPath == null || IsCandidateNewer(rev, utc, bestRev, bestUtc))
            {
                bestPath = path;
                bestRev = rev;
                bestUtc = utc;
            }
        }

        return bestPath;
    }

    private static bool IsCandidateNewer(string candRev, DateTime candUtc, string bestRev, DateTime bestUtc)
    {
        bool candHas = HasMeaningfulRevision(candRev);
        bool bestHas = HasMeaningfulRevision(bestRev);

        if (candHas && bestHas)
        {
            int cmp = CompareNaturalRevision(candRev, bestRev);
            if (cmp != 0) return cmp > 0;
            return candUtc > bestUtc;
        }
        if (candHas != bestHas)
        {
            return candHas;
        }
        return candUtc > bestUtc;
    }

    private static bool HasMeaningfulRevision(string rev)
    {
        if (string.IsNullOrWhiteSpace(rev)) return false;
        string t = rev.Trim();
        return !string.Equals(t, "builtin", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(t, "synced", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryReadRevision(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int readLen = (int)Math.Min(fs.Length, 4096);
            if (readLen <= 0) return null;
            byte[] buf = new byte[readLen];
            int actual = fs.Read(buf, 0, readLen);
            string head = Encoding.UTF8.GetString(buf, 0, actual);
            var m = RevisionFieldRegex.Match(head);
            if (m.Success)
            {
                return m.Groups[1].Value?.Trim();
            }

            if (fs.Length <= 4 * 1024 * 1024)
            {
                fs.Position = 0;
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var obj = JObject.Parse(sr.ReadToEnd());
                return ((string)(obj["revision"] ?? obj["version"]))?.Trim();
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("LocalizationOverlayModule:L2647", diagEx); }
        return null;
    }

    private static int CompareNaturalRevision(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        int ia = 0, ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            bool da = char.IsDigit(a[ia]);
            bool db = char.IsDigit(b[ib]);
            if (da && db)
            {
                long na = 0;
                while (ia < a.Length && char.IsDigit(a[ia]))
                {
                    na = na * 10 + (a[ia] - '0');
                    ia++;
                }
                long nb = 0;
                while (ib < b.Length && char.IsDigit(b[ib]))
                {
                    nb = nb * 10 + (b[ib] - '0');
                    ib++;
                }
                int cmp = na.CompareTo(nb);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = char.ToUpperInvariant(a[ia]).CompareTo(char.ToUpperInvariant(b[ib]));
                if (cmp != 0) return cmp;
                ia++;
                ib++;
            }
        }
        return (a.Length - ia).CompareTo(b.Length - ib);
    }
}
