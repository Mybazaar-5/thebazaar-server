using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BazaarCore.Models;

namespace BazaarCore.Data;

/// <summary>
/// 外挂中文词典（custom_zh_overlay.json）单张卡牌的文本覆盖条目。
/// </summary>
public sealed class CardLocalizationOverride
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("flavorText")]
    public string? FlavorText { get; set; }

    [JsonPropertyName("tooltips")]
    public Dictionary<string, string>? Tooltips { get; set; }

    [JsonPropertyName("enchantments")]
    public Dictionary<string, List<string>>? Enchantments { get; set; }
}

/// <summary>
/// 外挂中文词典的 UI 精确匹配覆盖区（区分全局与上下文消歧）。
/// </summary>
public sealed class UiExactOverlay
{
    [JsonPropertyName("global")]
    public Dictionary<string, string> Global { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("contexts")]
    public Dictionary<string, Dictionary<string, string>> Contexts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 外挂中文词典的正则替换规则（供插值文本与动态服务端报错文本汉化）。
/// </summary>
public sealed class UiRegexOverlayRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("replacement")]
    public string Replacement { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public List<string> Scope { get; set; } = new();
}

/// <summary>
/// 外挂中文词典的服务端消息覆盖区（对应 custom_zh_overlay.json 中的 serverMessageOverrides: { exact, regex }）。
/// </summary>
public sealed class ServerMessageOverlay
{
    [JsonPropertyName("exact")]
    public Dictionary<string, string>? Exact { get; set; }

    [JsonPropertyName("regex")]
    public List<UiRegexOverlayRule>? Regex { get; set; }
}

/// <summary>
/// 标准外挂中文词典 Schema（对齐 custom_zh_overlay.json v1 规范）。
/// </summary>
public sealed class LocalizationOverlayDocument
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://bazaar-local.schema/custom-zh-overlay/v1";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("locale")]
    public string Locale { get; set; } = "zh-CN";

    [JsonPropertyName("revision")]
    public string Revision { get; set; } = "1.0.0";

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("minGameDataVersion")]
    public string MinGameDataVersion { get; set; } = "5.0.0";

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "override";

    [JsonPropertyName("hashOverrides")]
    public Dictionary<string, string> HashOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("cardOverrides")]
    public Dictionary<string, CardLocalizationOverride> CardOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("uiExact")]
    public UiExactOverlay UiExact { get; set; } = new();

    [JsonPropertyName("uiRegex")]
    public List<UiRegexOverlayRule> UiRegex { get; set; } = new();

    /// <summary>兼容工具链扁平导出的 UI 全局精确翻译区（自动归并至 UiExact.Global）。</summary>
    [JsonPropertyName("uiExactOverrides")]
    public Dictionary<string, string>? UiExactOverrides { get; set; }

    /// <summary>兼容工具链扁平导出的 UI 上下文消歧翻译区（自动归并至 UiExact.Contexts）。</summary>
    [JsonPropertyName("uiContextOverrides")]
    public Dictionary<string, Dictionary<string, string>>? UiContextOverrides { get; set; }

    /// <summary>兼容工具链扁平导出的 UI 正则翻译区（自动归并至 UiRegex）。</summary>
    [JsonPropertyName("uiRegexOverrides")]
    public List<UiRegexOverlayRule>? UiRegexOverrides { get; set; }

    /// <summary>兼容服务端错误/提示文本嵌套覆盖区（serverMessageOverrides.exact + serverMessageOverrides.regex）。</summary>
    [JsonPropertyName("serverMessageOverrides")]
    public ServerMessageOverlay? ServerMessageOverrides { get; set; }

    /// <summary>兼容服务端错误/提示文本精确翻译区（自动归并至 UiExact.Global 与 server 上下文）。</summary>
    [JsonPropertyName("serverMessageExact")]
    public Dictionary<string, string>? ServerMessageExact { get; set; }

    /// <summary>兼容服务端错误/提示文本正则翻译区（自动归并至 UiRegex）。</summary>
    [JsonPropertyName("serverMessageRegex")]
    public List<UiRegexOverlayRule>? ServerMessageRegex { get; set; }

    /// <summary>兼容 Mod 词典文件（localization/zh-CN.json）的扁平键值 entries 映射。</summary>
    [JsonPropertyName("entries")]
    public Dictionary<string, string>? Entries { get; set; }
}

/// <summary>
/// 合并导出结果（供服务端 /api/localization/zh-overlay 响应与 ETag 校验）。
/// </summary>
public sealed record MergedLocalizationExport(
    LocalizationOverlayDocument Document,
    string Json,
    string Sha256,
    int TotalOverlayEntryCount);

/// <summary>
/// 四层外挂中文词典叠加存储：
/// 严格按【Mod 自定义覆盖 > 外挂 custom_zh_overlay.json（cardOverrides / uiExact.contexts / hashOverrides / uiExact.global / uiRegex） > 官方 zh-CN.bytes > 英文原文回退】查词；
/// 支持线程安全热刷新、Mod 词典热注入及合并导出 JSON 供服务端下发客户端。
/// </summary>
public sealed class OverlayLocalizationStore : ILocalizationStore
{
    private static readonly JsonSerializerOptions OverlayJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class CompiledRegexEntry
    {
        public required UiRegexOverlayRule Rule { get; init; }
        public required Regex Compiled { get; init; }
        public HashSet<string>? Scopes { get; init; }
    }

    private sealed class OverlayLayer
    {
        public LocalizationOverlayDocument Raw { get; init; } = new();
        public Dictionary<string, string> HashOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CardLocalizationOverride> CardOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> UiGlobal { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, string>> UiContexts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<CompiledRegexEntry> UiRegex { get; init; } = new();

        public int ContextEntryCount
        {
            get
            {
                int sum = 0;
                foreach (var m in UiContexts.Values) sum += m.Count;
                return sum;
            }
        }

        public int EntryCount => HashOverrides.Count + CardOverrides.Count + UiGlobal.Count + ContextEntryCount + UiRegex.Count;
    }

    private sealed class Snapshot
    {
        public required OverlayLayer FileOverlay { get; init; }
        public required OverlayLayer ModOverlay { get; init; }
        public required string? OverlayFilePath { get; init; }
        public required DateTimeOffset LoadedAtUtc { get; init; }
        public required MergedLocalizationExport CachedExport { get; init; }
    }

    private readonly ILocalizationStore _officialBase;
    private readonly object _reloadLock = new();
    private volatile Snapshot _snapshot;

    public OverlayLocalizationStore(
        ILocalizationStore? officialBase,
        string? overlayFilePath = null,
        LocalizationOverlayDocument? initialFileOverlay = null,
        IEnumerable<LocalizationOverlayDocument>? initialModOverlays = null)
    {
        _officialBase = officialBase ?? LocalizationLoader.Empty;
        var fileDoc = initialFileOverlay ?? LoadOverlayDocumentFromFile(overlayFilePath);
        var fileLayer = CompileLayer(fileDoc);
        var modLayer = CompileMergedModLayers(initialModOverlays);
        _snapshot = BuildSnapshot(fileLayer, modLayer, overlayFilePath);
    }

    /// <summary>官方底层只读词库。</summary>
    public ILocalizationStore OfficialBase => _officialBase;

    /// <summary>当前外挂词典文件路径。</summary>
    public string? OverlayFilePath => _snapshot.OverlayFilePath;

    /// <summary>最近一次刷新时间（UTC）。</summary>
    public DateTimeOffset LoadedAtUtc => _snapshot.LoadedAtUtc;

    /// <summary>当前合并后外挂词典的确定性 SHA-256（可用作 HTTP ETag）。</summary>
    public string MergedOverlaySha256 => _snapshot.CachedExport.Sha256;

    /// <inheritdoc />
    public int EntryCount
    {
        get
        {
            var snap = _snapshot;
            return _officialBase.EntryCount + snap.FileOverlay.EntryCount + snap.ModOverlay.EntryCount;
        }
    }

    /// <inheritdoc />
    public string? SourcePath => _officialBase.SourcePath ?? _snapshot.OverlayFilePath;

    /// <summary>计算与官方 LocalizationUtility.GetHash 100% 一致的 UTF-8 32 位小写十六进制 MD5 哈希。</summary>
    public static string ComputeOfficialMd5Hash(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return string.Empty;
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(rawText));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 线程安全热刷新磁盘上的 custom_zh_overlay.json（传 null 时重读原路径）。
    /// 若文件解析异常则返回 false 并保留当前健康快照。
    /// </summary>
    public bool ReloadOverlay(string? newOverlayFilePath = null)
    {
        lock (_reloadLock)
        {
            string? targetPath = string.IsNullOrWhiteSpace(newOverlayFilePath)
                ? _snapshot.OverlayFilePath
                : newOverlayFilePath;

            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
            {
                var emptyLayer = CompileLayer(new LocalizationOverlayDocument());
                _snapshot = BuildSnapshot(emptyLayer, _snapshot.ModOverlay, targetPath);
                return false;
            }

            try
            {
                var doc = ParseOverlayJson(File.ReadAllText(targetPath, Encoding.UTF8));
                var fileLayer = CompileLayer(doc);
                _snapshot = BuildSnapshot(fileLayer, _snapshot.ModOverlay, Path.GetFullPath(targetPath));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 直接从内存文档热更新外挂词典层（适合测试与服务端管理热推）。
    /// </summary>
    public void UpdateFileOverlay(LocalizationOverlayDocument? document, string? virtualPath = null)
    {
        lock (_reloadLock)
        {
            var fileLayer = CompileLayer(document ?? new LocalizationOverlayDocument());
            _snapshot = BuildSnapshot(fileLayer, _snapshot.ModOverlay, virtualPath ?? _snapshot.OverlayFilePath);
        }
    }

    /// <summary>
    /// 线程安全更新最高优先级的 Mod 自定义中文覆盖层（按 Mod 拓扑顺序依次覆盖）。
    /// </summary>
    public void SetModOverlays(IEnumerable<LocalizationOverlayDocument>? modOverlays)
    {
        lock (_reloadLock)
        {
            var modLayer = CompileMergedModLayers(modOverlays);
            _snapshot = BuildSnapshot(_snapshot.FileOverlay, modLayer, _snapshot.OverlayFilePath);
        }
    }

    /// <summary>
    /// 导出合并后的外挂词典（Mod 自定义覆盖已叠加于外挂词典之上），附带确定性 SHA-256 供服务端下发客户端。
    /// </summary>
    public MergedLocalizationExport ExportMergedOverlay() => _snapshot.CachedExport;

    /// <summary>导出合并后的外挂词典 JSON 字符串。</summary>
    public string ExportMergedOverlayJson() => _snapshot.CachedExport.Json;

    /// <summary>按优先级解析哈希键：Mod hashOverrides > 外挂 hashOverrides > 官方 zh-CN.bytes。</summary>
    public bool TryGetText(string? hashKey, out string text)
    {
        if (string.IsNullOrWhiteSpace(hashKey))
        {
            text = string.Empty;
            return false;
        }

        var snap = _snapshot;
        if (snap.ModOverlay.HashOverrides.TryGetValue(hashKey, out var modText) && !string.IsNullOrEmpty(modText))
        {
            text = modText;
            return true;
        }
        if (snap.FileOverlay.HashOverrides.TryGetValue(hashKey, out var fileText) && !string.IsNullOrEmpty(fileText))
        {
            text = fileText;
            return true;
        }
        return _officialBase.TryGetText(hashKey, out text);
    }

    /// <summary>
    /// 按四层优先级解析任意 UI / 文本（支持上下文消歧、MD5 哈希、全局精确与正则替换，最终回退官方 zh-CN.bytes 与英文原文）。
    /// </summary>
    public string ResolveText(string? rawEnglishText, string? viewContext = null, string? explicitHashKey = null)
    {
        if (string.IsNullOrEmpty(rawEnglishText))
            return string.Empty;

        var snap = _snapshot;
        string trimmed = rawEnglishText.Trim();
        string hash = !string.IsNullOrWhiteSpace(explicitHashKey)
            ? explicitHashKey
            : ComputeOfficialMd5Hash(rawEnglishText);

        // 第 1 层：Mod 自定义覆盖
        if (TryResolveInLayer(snap.ModOverlay, rawEnglishText, trimmed, hash, viewContext, out var modHit))
            return modHit;

        // 第 2 层：外挂 custom_zh_overlay.json
        if (TryResolveInLayer(snap.FileOverlay, rawEnglishText, trimmed, hash, viewContext, out var fileHit))
            return fileHit;

        // 第 3 层：官方 zh-CN.bytes
        if (!string.IsNullOrEmpty(hash) && _officialBase.TryGetText(hash, out var officialHit) && !string.IsNullOrWhiteSpace(officialHit))
            return officialHit;

        // 第 4 层：英文原文回退
        return rawEnglishText;
    }

    /// <summary>按四层优先级获取指定卡牌的中文标题（Mod cardOverrides > 外挂 cardOverrides > 哈希覆盖 > 官方 zh-CN.bytes > 英文回退）。</summary>
    public string GetLocalizedTitle(CardTemplate card)
    {
        if (card == null) return string.Empty;
        var snap = _snapshot;

        if (snap.ModOverlay.CardOverrides.TryGetValue(card.Id, out var modCard) && !string.IsNullOrWhiteSpace(modCard.Title))
            return modCard.Title!;
        if (snap.FileOverlay.CardOverrides.TryGetValue(card.Id, out var fileCard) && !string.IsNullOrWhiteSpace(fileCard.Title))
            return fileCard.Title!;

        if (TryGetText(card.Localization?.Title?.Key, out var byKey) && !string.IsNullOrWhiteSpace(byKey))
            return byKey;
        if (TryGetText(card.TranslationKey, out var byFallbackKey) && !string.IsNullOrWhiteSpace(byFallbackKey))
            return byFallbackKey;

        var rawTitle = card.DisplayTitle;
        if (!string.IsNullOrEmpty(rawTitle))
        {
            var md5 = ComputeOfficialMd5Hash(rawTitle);
            if (TryGetText(md5, out var byMd5) && !string.IsNullOrWhiteSpace(byMd5))
                return byMd5;
        }
        return rawTitle;
    }

    /// <summary>按四层优先级获取指定卡牌的中文描述。</summary>
    public string? GetLocalizedDescription(CardTemplate card)
    {
        if (card == null) return null;
        var snap = _snapshot;

        if (snap.ModOverlay.CardOverrides.TryGetValue(card.Id, out var modCard) && !string.IsNullOrWhiteSpace(modCard.Description))
            return modCard.Description;
        if (snap.FileOverlay.CardOverrides.TryGetValue(card.Id, out var fileCard) && !string.IsNullOrWhiteSpace(fileCard.Description))
            return fileCard.Description;

        return GetLocalizedText(card.Localization?.Description);
    }

    /// <summary>按四层优先级获取指定卡牌的指定下标 Tooltip 译文。</summary>
    public string? GetLocalizedTooltip(CardTemplate card, int tooltipIndex)
    {
        if (card == null) return null;
        var snap = _snapshot;
        string idxKey = tooltipIndex.ToString();

        if (snap.ModOverlay.CardOverrides.TryGetValue(card.Id, out var modCard)
            && modCard.Tooltips != null
            && modCard.Tooltips.TryGetValue(idxKey, out var modTip)
            && !string.IsNullOrWhiteSpace(modTip))
            return modTip;

        if (snap.FileOverlay.CardOverrides.TryGetValue(card.Id, out var fileCard)
            && fileCard.Tooltips != null
            && fileCard.Tooltips.TryGetValue(idxKey, out var fileTip)
            && !string.IsNullOrWhiteSpace(fileTip))
            return fileTip;

        var entries = card.Localization?.Tooltips;
        if (entries != null && tooltipIndex >= 0 && tooltipIndex < entries.Count)
            return GetLocalizedText(entries[tooltipIndex].Content);
        return null;
    }

    /// <inheritdoc />
    public bool HasOfficialTitle(CardTemplate card)
    {
        if (card == null) return false;
        var snap = _snapshot;
        if (snap.ModOverlay.CardOverrides.TryGetValue(card.Id, out var modCard) && !string.IsNullOrWhiteSpace(modCard.Title))
            return true;
        if (snap.FileOverlay.CardOverrides.TryGetValue(card.Id, out var fileCard) && !string.IsNullOrWhiteSpace(fileCard.Title))
            return true;
        if (TryGetText(card.Localization?.Title?.Key, out var t1) && !string.IsNullOrWhiteSpace(t1))
            return true;
        if (TryGetText(card.TranslationKey, out var t2) && !string.IsNullOrWhiteSpace(t2))
            return true;
        if (!string.IsNullOrEmpty(card.DisplayTitle) && TryGetText(ComputeOfficialMd5Hash(card.DisplayTitle), out var t3) && !string.IsNullOrWhiteSpace(t3))
            return true;
        return false;
    }

    /// <inheritdoc />
    public string? GetLocalizedText(LocalizedText? localized)
    {
        if (localized == null) return null;
        if (TryGetText(localized.Key, out var byKey) && !string.IsNullOrWhiteSpace(byKey))
            return byKey;
        if (!string.IsNullOrEmpty(localized.Text))
            return ResolveText(localized.Text);
        return localized.Text;
    }

    private static bool TryResolveInLayer(
        OverlayLayer layer,
        string raw,
        string trimmed,
        string hash,
        string? viewContext,
        out string result)
    {
        // 1. uiExact.contexts[viewKey]
        if (!string.IsNullOrWhiteSpace(viewContext)
            && layer.UiContexts.TryGetValue(viewContext, out var ctxMap))
        {
            if (ctxMap.TryGetValue(raw, out var exactCtx) && !string.IsNullOrEmpty(exactCtx))
            {
                result = exactCtx;
                return true;
            }
            if (ctxMap.TryGetValue(trimmed, out var trimCtx) && !string.IsNullOrEmpty(trimCtx))
            {
                result = trimCtx;
                return true;
            }
        }

        // 2. hashOverrides[md5]
        if (!string.IsNullOrEmpty(hash)
            && layer.HashOverrides.TryGetValue(hash, out var byHash)
            && !string.IsNullOrEmpty(byHash))
        {
            result = byHash;
            return true;
        }

        // 3. uiExact.global
        if (layer.UiGlobal.TryGetValue(raw, out var exactGlobal) && !string.IsNullOrEmpty(exactGlobal))
        {
            result = exactGlobal;
            return true;
        }
        if (layer.UiGlobal.TryGetValue(trimmed, out var trimGlobal) && !string.IsNullOrEmpty(trimGlobal))
        {
            result = trimGlobal;
            return true;
        }

        // 4. uiRegex
        for (int i = 0; i < layer.UiRegex.Count; i++)
        {
            var entry = layer.UiRegex[i];
            if (entry.Scopes != null && entry.Scopes.Count > 0)
            {
                bool scopeMatch = entry.Scopes.Contains("global")
                    || (!string.IsNullOrWhiteSpace(viewContext) && entry.Scopes.Contains(viewContext));
                if (!scopeMatch) continue;
            }
            if (entry.Compiled.IsMatch(raw))
            {
                result = entry.Compiled.Replace(raw, entry.Rule.Replacement);
                return true;
            }
        }

        result = string.Empty;
        return false;
    }

    private static LocalizationOverlayDocument LoadOverlayDocumentFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new LocalizationOverlayDocument();
        try
        {
            return ParseOverlayJson(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return new LocalizationOverlayDocument();
        }
    }

    public static LocalizationOverlayDocument ParseOverlayJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new LocalizationOverlayDocument();
        return JsonSerializer.Deserialize<LocalizationOverlayDocument>(json, OverlayJsonOptions)
               ?? new LocalizationOverlayDocument();
    }

    private static OverlayLayer CompileMergedModLayers(IEnumerable<LocalizationOverlayDocument>? docs)
    {
        var merged = new LocalizationOverlayDocument();
        if (docs != null)
        {
            foreach (var doc in docs)
            {
                if (doc == null) continue;
                MergeOverlayInto(merged, doc);
            }
        }
        return CompileLayer(merged);
    }

    private static OverlayLayer CompileLayer(LocalizationOverlayDocument doc)
    {
        var hashMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.HashOverrides != null)
        {
            foreach (var (k, v) in doc.HashOverrides)
            {
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrEmpty(v))
                    hashMap[k.Trim()] = v;
            }
        }

        var cardMap = new Dictionary<string, CardLocalizationOverride>(StringComparer.OrdinalIgnoreCase);
        if (doc.CardOverrides != null)
        {
            foreach (var (k, v) in doc.CardOverrides)
            {
                if (!string.IsNullOrWhiteSpace(k) && v != null)
                    cardMap[k.Trim()] = CloneCardOverride(v);
            }
        }

        var uiGlobal = new Dictionary<string, string>(StringComparer.Ordinal);
        var uiContexts = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        void MergeGlobalDict(Dictionary<string, string>? dict)
        {
            if (dict == null) return;
            foreach (var (k, v) in dict)
            {
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    uiGlobal[k] = v;
            }
        }

        void MergeContextDict(Dictionary<string, Dictionary<string, string>>? ctxMap)
        {
            if (ctxMap == null) return;
            foreach (var (ctxKey, entries) in ctxMap)
            {
                if (string.IsNullOrWhiteSpace(ctxKey) || entries == null) continue;
                string trimmedCtx = ctxKey.Trim();
                if (!uiContexts.TryGetValue(trimmedCtx, out var sub))
                {
                    sub = new Dictionary<string, string>(StringComparer.Ordinal);
                    uiContexts[trimmedCtx] = sub;
                }
                foreach (var (ek, ev) in entries)
                {
                    if (!string.IsNullOrEmpty(ek) && !string.IsNullOrEmpty(ev))
                        sub[ek] = ev;
                }
            }
        }

        if (doc.UiExact != null)
        {
            MergeGlobalDict(doc.UiExact.Global);
            MergeContextDict(doc.UiExact.Contexts);
        }
        MergeGlobalDict(doc.UiExactOverrides);
        MergeGlobalDict(doc.ServerMessageOverrides?.Exact);
        MergeGlobalDict(doc.ServerMessageExact);
        MergeGlobalDict(doc.Entries);
        MergeContextDict(doc.UiContextOverrides);

        var regexList = new List<CompiledRegexEntry>();
        void CompileRegexList(List<UiRegexOverlayRule>? rules)
        {
            if (rules == null) return;
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                if (r == null || string.IsNullOrEmpty(r.Pattern)) continue;
                try
                {
                    var compiled = new Regex(r.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    HashSet<string>? scopes = r.Scope is { Count: > 0 }
                        ? new HashSet<string>(r.Scope, StringComparer.OrdinalIgnoreCase)
                        : null;
                    regexList.Add(new CompiledRegexEntry { Rule = r, Compiled = compiled, Scopes = scopes });
                }
                catch
                {
                    // 跳过非法正则表达式，防止单个错误规则破坏整个词典加载
                }
            }
        }
        CompileRegexList(doc.UiRegex);
        CompileRegexList(doc.UiRegexOverrides);
        CompileRegexList(doc.ServerMessageOverrides?.Regex);
        CompileRegexList(doc.ServerMessageRegex);

        return new OverlayLayer
        {
            Raw = doc,
            HashOverrides = hashMap,
            CardOverrides = cardMap,
            UiGlobal = uiGlobal,
            UiContexts = uiContexts,
            UiRegex = regexList
        };
    }

    private static Snapshot BuildSnapshot(OverlayLayer fileLayer, OverlayLayer modLayer, string? filePath)
    {
        var merged = new LocalizationOverlayDocument
        {
            Schema = fileLayer.Raw.Schema,
            SchemaVersion = fileLayer.Raw.SchemaVersion > 0 ? fileLayer.Raw.SchemaVersion : 1,
            Locale = string.IsNullOrWhiteSpace(fileLayer.Raw.Locale) ? "zh-CN" : fileLayer.Raw.Locale,
            Revision = string.IsNullOrWhiteSpace(fileLayer.Raw.Revision) ? "1.0.0" : fileLayer.Raw.Revision,
            MinGameDataVersion = string.IsNullOrWhiteSpace(fileLayer.Raw.MinGameDataVersion) ? "5.0.0" : fileLayer.Raw.MinGameDataVersion,
            Priority = string.IsNullOrWhiteSpace(fileLayer.Raw.Priority) ? "override" : fileLayer.Raw.Priority
        };
        MergeOverlayInto(merged, fileLayer.Raw);
        MergeOverlayInto(merged, modLayer.Raw);
        merged.Sha256 = null;

        // 先计算无 sha256 字段时的规范哈希，再回填 sha256 序列化导出
        string canonicalBody = JsonSerializer.Serialize(merged, OverlayJsonOptions);
        string sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalBody))).ToLowerInvariant();
        merged.Sha256 = sha256;
        string finalJson = JsonSerializer.Serialize(merged, OverlayJsonOptions);

        int ctxCount = 0;
        foreach (var map in merged.UiExact.Contexts.Values) ctxCount += map.Count;
        int totalCount = merged.HashOverrides.Count + merged.CardOverrides.Count + merged.UiExact.Global.Count + ctxCount + merged.UiRegex.Count;
        var export = new MergedLocalizationExport(merged, finalJson, sha256, totalCount);

        return new Snapshot
        {
            FileOverlay = fileLayer,
            ModOverlay = modLayer,
            OverlayFilePath = filePath,
            LoadedAtUtc = DateTimeOffset.UtcNow,
            CachedExport = export
        };
    }

    private static void MergeOverlayInto(LocalizationOverlayDocument target, LocalizationOverlayDocument src)
    {
        if (src.HashOverrides != null)
        {
            foreach (var (k, v) in src.HashOverrides)
            {
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrEmpty(v))
                    target.HashOverrides[k.Trim()] = v;
            }
        }

        if (src.CardOverrides != null)
        {
            foreach (var (cardId, ov) in src.CardOverrides)
            {
                if (string.IsNullOrWhiteSpace(cardId) || ov == null) continue;
                var key = cardId.Trim();
                if (!target.CardOverrides.TryGetValue(key, out var existing))
                {
                    target.CardOverrides[key] = CloneCardOverride(ov);
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(ov.Title)) existing.Title = ov.Title;
                if (!string.IsNullOrWhiteSpace(ov.Description)) existing.Description = ov.Description;
                if (!string.IsNullOrWhiteSpace(ov.FlavorText)) existing.FlavorText = ov.FlavorText;
                if (ov.Tooltips is { Count: > 0 })
                {
                    existing.Tooltips ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (tk, tv) in ov.Tooltips) existing.Tooltips[tk] = tv;
                }
                if (ov.Enchantments is { Count: > 0 })
                {
                    existing.Enchantments ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (ek, ev) in ov.Enchantments) existing.Enchantments[ek] = new List<string>(ev);
                }
            }
        }

        if (src.UiExact != null)
        {
            if (src.UiExact.Global != null)
            {
                foreach (var (k, v) in src.UiExact.Global)
                {
                    if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                        target.UiExact.Global[k] = v;
                }
            }
            if (src.UiExact.Contexts != null)
            {
                foreach (var (ctxKey, map) in src.UiExact.Contexts)
                {
                    if (string.IsNullOrWhiteSpace(ctxKey) || map == null) continue;
                    if (!target.UiExact.Contexts.TryGetValue(ctxKey, out var destMap))
                    {
                        destMap = new Dictionary<string, string>(StringComparer.Ordinal);
                        target.UiExact.Contexts[ctxKey] = destMap;
                    }
                    foreach (var (k, v) in map)
                    {
                        if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                            destMap[k] = v;
                    }
                }
            }
        }

        if (src.UiExactOverrides != null)
        {
            foreach (var (k, v) in src.UiExactOverrides)
            {
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    target.UiExact.Global[k] = v;
            }
        }
        if (src.ServerMessageOverrides?.Exact != null)
        {
            foreach (var (k, v) in src.ServerMessageOverrides.Exact)
            {
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    target.UiExact.Global[k] = v;
            }
        }
        if (src.ServerMessageExact != null)
        {
            foreach (var (k, v) in src.ServerMessageExact)
            {
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    target.UiExact.Global[k] = v;
            }
        }
        if (src.Entries != null)
        {
            foreach (var (k, v) in src.Entries)
            {
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    target.UiExact.Global[k] = v;
            }
        }
        if (src.UiContextOverrides != null)
        {
            foreach (var (ctxKey, map) in src.UiContextOverrides)
            {
                if (string.IsNullOrWhiteSpace(ctxKey) || map == null) continue;
                if (!target.UiExact.Contexts.TryGetValue(ctxKey, out var destMap))
                {
                    destMap = new Dictionary<string, string>(StringComparer.Ordinal);
                    target.UiExact.Contexts[ctxKey] = destMap;
                }
                foreach (var (k, v) in map)
                {
                    if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                        destMap[k] = v;
                }
            }
        }

        void AppendRegexList(List<UiRegexOverlayRule>? srcRules)
        {
            if (srcRules is not { Count: > 0 }) return;
            var existingIdxById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < target.UiRegex.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(target.UiRegex[i].Id))
                    existingIdxById[target.UiRegex[i].Id] = i;
            }
            foreach (var r in srcRules)
            {
                if (r == null || string.IsNullOrEmpty(r.Pattern)) continue;
                var clone = new UiRegexOverlayRule
                {
                    Id = r.Id,
                    Pattern = r.Pattern,
                    Replacement = r.Replacement,
                    Scope = r.Scope != null ? new List<string>(r.Scope) : new List<string>()
                };
                if (!string.IsNullOrWhiteSpace(clone.Id) && existingIdxById.TryGetValue(clone.Id, out int idx))
                    target.UiRegex[idx] = clone;
                else
                {
                    if (!string.IsNullOrWhiteSpace(clone.Id))
                        existingIdxById[clone.Id] = target.UiRegex.Count;
                    target.UiRegex.Add(clone);
                }
            }
        }

        AppendRegexList(src.UiRegex);
        AppendRegexList(src.UiRegexOverrides);
        AppendRegexList(src.ServerMessageOverrides?.Regex);
        AppendRegexList(src.ServerMessageRegex);
    }

    private static CardLocalizationOverride CloneCardOverride(CardLocalizationOverride src)
        => new()
        {
            Title = src.Title,
            Description = src.Description,
            FlavorText = src.FlavorText,
            Tooltips = src.Tooltips != null ? new Dictionary<string, string>(src.Tooltips, StringComparer.OrdinalIgnoreCase) : null,
            Enchantments = src.Enchantments != null
                ? src.Enchantments.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.OrdinalIgnoreCase)
                : null
        };
}
