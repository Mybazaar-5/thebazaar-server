using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace BazaarLauncher.Core;

/// <summary>
/// Wave 4C（任务 #12）：裸协议黑匣子观测模式开关决策矩阵（零 Unity 依赖）。
/// </summary>
public static class BareClientAuditPolicy
{
    public const string EnvVarName = "BAZAAR_STRICT_BARE_AUDIT";

    public static bool IsStrictAuditEnabled(bool configValue, string envValue = null)
    {
        if (configValue) return true;
        if (string.IsNullOrWhiteSpace(envValue)) return false;

        string trimmed = envValue.Trim();
        return string.Equals(trimmed, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 当 StrictBareClientAuditMode == true 时彻底禁用 CompatAuditPatch / H-7 等自愈兜底干预，
    /// 让裸客户端真实暴露全部协议问题；默认 false 时保持正常玩家自愈防护。
    /// </summary>
    public static bool ShouldInstallCompatSelfHealingPatches(bool strictAuditMode) => !strictAuditMode;

    /// <summary>仅在 StrictBareClientAuditMode == true 时激活黑匣子观测器，默认 false 零挂钩零 I/O。</summary>
    public static bool ShouldInstallBareAuditObserver(bool strictAuditMode) => strictAuditMode;
}

/// <summary>黑匣子 JSONL 异常分类常量。</summary>
public static class BareAuditCategory
{
    public const string ProtocolRevealSkipped = "protocol_reveal_skipped";
    public const string ProtocolAnomaly = "protocol_anomaly";
    public const string UnityErrorOrException = "unity_error_or_exception";
    public const string ShopPurchaseAttributeJump = "shop_purchase_attribute_jump";
    public const string CardArtLoadFailure = "card_art_load_failure";
    public const string StateOrInputDeadlock = "state_or_input_deadlock";
}

/// <summary>环形缓冲中的单条客户端命令/服务端消息/状态轨迹条目。</summary>
public sealed class BareAuditTraceEvent
{
    [JsonProperty("timestampUtc")] public string TimestampUtc { get; set; } = string.Empty;
    [JsonProperty("direction")] public string Direction { get; set; } = string.Empty;
    [JsonProperty("kind")] public string Kind { get; set; } = string.Empty;
    [JsonProperty("summary")] public string Summary { get; set; } = string.Empty;
}

/// <summary>落盘至 logs/bare_client_audit.jsonl 的单行黑匣子记录。</summary>
public sealed class BareAuditRecord
{
    [JsonProperty("timestampUtc")] public string TimestampUtc { get; set; } = string.Empty;
    [JsonProperty("category")] public string Category { get; set; } = string.Empty;
    [JsonProperty("severity")] public string Severity { get; set; } = "error";
    [JsonProperty("signature")] public string Signature { get; set; } = string.Empty;
    [JsonProperty("summary")] public string Summary { get; set; } = string.Empty;
    [JsonProperty("scene")] public string Scene { get; set; } = string.Empty;
    [JsonProperty("appState")] public string AppState { get; set; } = string.Empty;
    [JsonProperty("runState")] public string RunState { get; set; } = string.Empty;
    [JsonProperty("day")] public int Day { get; set; }
    [JsonProperty("hour")] public int Hour { get; set; }
    [JsonProperty("suppressedCount")] public int SuppressedCount { get; set; }
    [JsonProperty("recentEventChain")] public BareAuditTraceEvent[] RecentEventChain { get; set; } = Array.Empty<BareAuditTraceEvent>();
    [JsonProperty("context")] public Dictionary<string, object> Context { get; set; } = new();
}

/// <summary>
/// 线程安全定长环形缓冲（Ring Buffer），O(1) 追加并保持严格从旧到新的事件链顺序。
/// </summary>
public sealed class BareAuditRingBuffer<T>
{
    private readonly object _sync = new();
    private readonly T[] _items;
    private int _head;
    private int _count;

    public int Capacity => _items.Length;

    public int Count
    {
        get { lock (_sync) return _count; }
    }

    public BareAuditRingBuffer(int capacity = 48)
    {
        int safeCap = Math.Max(8, Math.Min(256, capacity));
        _items = new T[safeCap];
    }

    public void Add(T item)
    {
        lock (_sync)
        {
            int writeIdx = (_head + _count) % _items.Length;
            if (_count == _items.Length)
            {
                _items[_head] = item;
                _head = (_head + 1) % _items.Length;
            }
            else
            {
                _items[writeIdx] = item;
                _count++;
            }
        }
    }

    public T[] Snapshot()
    {
        lock (_sync)
        {
            if (_count == 0) return Array.Empty<T>();
            var copy = new T[_count];
            for (int i = 0; i < _count; i++)
            {
                copy[i] = _items[(_head + i) % _items.Length];
            }
            return copy;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_items, 0, _items.Length);
            _head = 0;
            _count = 0;
        }
    }
}

/// <summary>去重 + 全局防风暴速率限制器（避免 Unity 逐帧 NRE 刷爆磁盘或拖垮主线程）。</summary>
public sealed class BareAuditRateLimiter
{
    public readonly struct RateLimitDecision
    {
        public readonly bool Allowed;
        public readonly int SuppressedSinceLastEmit;

        public RateLimitDecision(bool allowed, int suppressedSinceLastEmit)
        {
            Allowed = allowed;
            SuppressedSinceLastEmit = suppressedSinceLastEmit;
        }
    }

    private sealed class SignatureState
    {
        public long LastEmitMs;
        public int SuppressedCount;
    }

    private readonly object _sync = new();
    private readonly long _dedupeWindowMs;
    private readonly int _maxGlobalPerWindow;
    private readonly long _globalWindowMs;
    private readonly Dictionary<string, SignatureState> _bySignature = new(StringComparer.Ordinal);
    private readonly Queue<long> _globalEmitTimestamps = new();
    private const int MaxTrackedSignatures = 2048;

    public BareAuditRateLimiter(long dedupeWindowMs = 5000L, int maxGlobalPerWindow = 40, long globalWindowMs = 10000L)
    {
        _dedupeWindowMs = Math.Max(0L, dedupeWindowMs);
        _maxGlobalPerWindow = Math.Max(1, maxGlobalPerWindow);
        _globalWindowMs = Math.Max(1000L, globalWindowMs);
    }

    public RateLimitDecision TryAcquire(string signature, long nowMs)
    {
        string key = string.IsNullOrEmpty(signature) ? "_" : signature;
        lock (_sync)
        {
            while (_globalEmitTimestamps.Count > 0 && nowMs - _globalEmitTimestamps.Peek() >= _globalWindowMs)
            {
                _globalEmitTimestamps.Dequeue();
            }

            if (_bySignature.TryGetValue(key, out var state))
            {
                if (_dedupeWindowMs > 0 && nowMs - state.LastEmitMs < _dedupeWindowMs)
                {
                    state.SuppressedCount++;
                    return new RateLimitDecision(false, 0);
                }

                if (_globalEmitTimestamps.Count >= _maxGlobalPerWindow)
                {
                    state.SuppressedCount++;
                    return new RateLimitDecision(false, 0);
                }

                int suppressed = state.SuppressedCount;
                state.SuppressedCount = 0;
                state.LastEmitMs = nowMs;
                _globalEmitTimestamps.Enqueue(nowMs);
                return new RateLimitDecision(true, suppressed);
            }

            if (_globalEmitTimestamps.Count >= _maxGlobalPerWindow)
            {
                return new RateLimitDecision(false, 0);
            }

            if (_bySignature.Count >= MaxTrackedSignatures)
            {
                PruneExpiredSignaturesLocked(nowMs);
            }

            _bySignature[key] = new SignatureState
            {
                LastEmitMs = nowMs,
                SuppressedCount = 0,
            };
            _globalEmitTimestamps.Enqueue(nowMs);
            return new RateLimitDecision(true, 0);
        }
    }

    private void PruneExpiredSignaturesLocked(long nowMs)
    {
        var stale = new List<string>();
        long expireAfter = Math.Max(_dedupeWindowMs * 4L, 30_000L);
        foreach (var kv in _bySignature)
        {
            if (nowMs - kv.Value.LastEmitMs >= expireAfter)
            {
                stale.Add(kv.Key);
            }
        }
        for (int i = 0; i < stale.Count; i++)
        {
            _bySignature.Remove(stale[i]);
        }
        if (_bySignature.Count >= MaxTrackedSignatures)
        {
            _bySignature.Clear();
        }
    }
}

/// <summary>单条属性跳变明细。</summary>
public sealed class PurchaseAttributeDiffEntry
{
    [JsonProperty("attribute")] public string Attribute { get; set; } = string.Empty;
    [JsonProperty("before")] public int? Before { get; set; }
    [JsonProperty("after")] public int? After { get; set; }
    [JsonProperty("zeroToNonZero")] public bool ZeroToNonZero { get; set; }
}

/// <summary>
/// P3-01（RC-AUDIT-AURA-PURCHASE-FP）：同包 NetMessageGameSim 携带的 EffectAuraExecuted 光环上下文。
/// 仅记录真实规则光环（排除服务端纯合成 UI 重绘事件），供货架购入属性跳变比对器豁免合法入场光环。
/// </summary>
public sealed class BareAuditPurchaseAuraContext
{
    /// <summary>已知的纯合成 UI 刷新型 EffectId（非真实规则光环，不得用于豁免货架缺失属性/附魔的异常跳变）。</summary>
    private static readonly HashSet<string> SyntheticRefreshEffectIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "OutOfCombatAuraSync",
        "OutOfCombatSync",
        "CardUpgradeAttrSync",
        "EncounterStepAttrSync",
    };

    /// <summary>同包内是否包含至少一条真实规则 EffectAuraExecuted 光环事件。</summary>
    public bool HasAuraExecuted { get; set; }

    /// <summary>同包内真实光环事件条数。</summary>
    public int RealAuraEventCount { get; set; }

    /// <summary>同包内被忽略的纯合成 UI 刷新事件条数。</summary>
    public int SyntheticRefreshEventCount { get; set; }

    /// <summary>同包真实光环事件是否显式声明了目标集合（用于区分玩家目标光环与未提供目标的简略调用）。</summary>
    public bool HasExplicitTargetScope { get; set; }

    /// <summary>受同包真实光环直接影响的卡牌实例 ID 集合（来自 AppliedTo / RemovedFrom 的卡牌目标）。</summary>
    public HashSet<string> AffectedCardInstanceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同包真实光环的来源实例 ID 集合（用于审计日志溯源）。</summary>
    public HashSet<string> SourceInstanceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同包真实光环的 EffectId 列表（去重，最多保留 16 条用于日志溯源）。</summary>
    public List<string> EffectIds { get; set; } = new();

    /// <summary>判定某条 EffectAuraExecuted 是否仅为服务端合成的 UI 刷新事件而非真实规则光环。</summary>
    public static bool IsSyntheticRefreshAuraEvent(string effectId, string executionContextId, bool hasSource)
    {
        if (!string.IsNullOrWhiteSpace(effectId) && SyntheticRefreshEffectIds.Contains(effectId.Trim()))
            return true;

        if (!string.IsNullOrWhiteSpace(executionContextId))
        {
            string ctx = executionContextId.Trim();
            if (ctx.StartsWith("ooc_sync_", StringComparison.OrdinalIgnoreCase)
                || ctx.StartsWith("sync_", StringComparison.OrdinalIgnoreCase)
                || ctx.StartsWith("upg_sync_", StringComparison.OrdinalIgnoreCase)
                || ctx.StartsWith("step_sync_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(effectId) && !hasSource)
            return true;

        return false;
    }

    /// <summary>将一条同包 EffectAuraExecuted 事件登记到当前购买批次光环上下文。</summary>
    public void RecordAuraEvent(
        string effectId,
        string executionContextId,
        string sourceInstanceId,
        IEnumerable<string> targetCardInstanceIds,
        bool hasAnyExplicitTarget)
    {
        bool hasSource = !string.IsNullOrWhiteSpace(sourceInstanceId);
        if (IsSyntheticRefreshAuraEvent(effectId, executionContextId, hasSource))
        {
            SyntheticRefreshEventCount++;
            return;
        }

        HasAuraExecuted = true;
        RealAuraEventCount++;
        if (hasAnyExplicitTarget)
        {
            HasExplicitTargetScope = true;
        }

        if (hasSource)
        {
            SourceInstanceIds.Add(sourceInstanceId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(effectId))
        {
            string trimmedId = effectId.Trim();
            bool exists = false;
            for (int i = 0; i < EffectIds.Count; i++)
            {
                if (string.Equals(EffectIds[i], trimmedId, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            if (!exists && EffectIds.Count < 16)
            {
                EffectIds.Add(trimmedId);
            }
        }

        bool addedAnyCardTarget = false;
        if (targetCardInstanceIds != null)
        {
            foreach (string tid in targetCardInstanceIds)
            {
                if (!string.IsNullOrWhiteSpace(tid))
                {
                    AffectedCardInstanceIds.Add(tid.Trim());
                    addedAnyCardTarget = true;
                }
            }
        }

        // 仅当事件完全未提供 AppliedTo/RemovedFrom 显式目标时，兜底回落关联 Source 自身
        if (!hasAnyExplicitTarget && !addedAnyCardTarget && hasSource)
        {
            AffectedCardInstanceIds.Add(sourceInstanceId.Trim());
        }
    }

    /// <summary>判断指定购入卡牌实例是否被同包真实光环覆盖解释。</summary>
    public bool IsCardAffectedByAura(string instanceId)
    {
        if (!HasAuraExecuted || string.IsNullOrWhiteSpace(instanceId))
            return false;

        if (AffectedCardInstanceIds != null && AffectedCardInstanceIds.Count > 0)
        {
            return AffectedCardInstanceIds.Contains(instanceId.Trim());
        }

        // 若同包光环声明了显式目标但全为非卡牌目标（例如玩家属性光环），不豁免单卡属性跳变
        if (HasExplicitTargetScope)
            return false;

        return true;
    }
}

/// <summary>单次 NetMessageGameSim 购买批次的黑匣子透传状态（供 Harmony Prefix->Postfix 传递）。</summary>
public sealed class BareAuditPurchaseBatchState
{
    public List<string> PurchasedIds { get; }
    public BareAuditPurchaseAuraContext AuraContext { get; }

    public BareAuditPurchaseBatchState(List<string> purchasedIds, BareAuditPurchaseAuraContext auraContext)
    {
        PurchasedIds = purchasedIds ?? new List<string>();
        AuraContext = auraContext;
    }
}

/// <summary>货架购入前后属性跳变比对结果。</summary>
public sealed class PurchaseAttributeJumpResult
{
    public string InstanceId { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string CardTitle { get; set; } = string.Empty;
    public bool HasZeroToNonZeroJump { get; set; }
    public bool ExplainedByAura { get; set; }
    public string[] AuraEffectIds { get; set; } = Array.Empty<string>();
    public string SuggestedSeverity { get; set; } = "warning";
    public Dictionary<string, int> BeforeAttributes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> AfterAttributes { get; set; } = new(StringComparer.Ordinal);
    public List<PurchaseAttributeDiffEntry> Diffs { get; set; } = new();
}

/// <summary>
/// 货架购入前后属性跳变比对器：
///   - 在卡片停留在货架/购买发出前记录展示属性快照；
///   - 在买入手牌/棋盘后对比差异，排除纯交易元数据（BuyPrice/SellPrice 等），精准捕获附魔/衍生属性 0->非0 跳变或数值分裂。
/// </summary>
public sealed class ShopPurchaseAttributeDiffDetector
{
    private sealed class ShelfCardSnapshot
    {
        public string InstanceId;
        public string TemplateId;
        public string CardTitle;
        public long CapturedAtMs;
        public Dictionary<string, int> Attributes;
    }

    private static readonly HashSet<string> DefaultIgnoredMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "BuyPrice",
        "SellPrice",
        "Price",
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, ShelfCardSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignoredKeys;
    private const int MaxTrackedCards = 256;
    private const long SnapshotTtlMs = 180_000L;

    public ShopPurchaseAttributeDiffDetector(IEnumerable<string> ignoredKeys = null)
    {
        _ignoredKeys = ignoredKeys != null
            ? new HashSet<string>(ignoredKeys, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(DefaultIgnoredMetadataKeys, StringComparer.OrdinalIgnoreCase);
    }

    public void CaptureShelfSnapshot(
        string instanceId,
        string templateId,
        string cardTitle,
        IReadOnlyDictionary<string, int> attributes,
        long nowMs)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || attributes == null) return;

        var copy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in attributes)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            copy[kv.Key] = kv.Value;
        }

        lock (_sync)
        {
            if (_snapshots.Count >= MaxTrackedCards)
            {
                PruneExpiredLocked(nowMs);
            }

            _snapshots[instanceId] = new ShelfCardSnapshot
            {
                InstanceId = instanceId,
                TemplateId = templateId ?? string.Empty,
                CardTitle = cardTitle ?? string.Empty,
                CapturedAtMs = nowMs,
                Attributes = copy,
            };
        }
    }

    public bool CompareAfterPurchase(
        string instanceId,
        IReadOnlyDictionary<string, int> afterAttributes,
        long nowMs,
        out PurchaseAttributeJumpResult result)
    {
        return CompareAfterPurchaseWithAura(instanceId, afterAttributes, nowMs, auraContext: null, out result);
    }

    /// <summary>
    /// P3-01（RC-AUDIT-AURA-PURCHASE-FP）：结合同包 EffectAuraExecuted 上下文比对购入前后属性跳变。
    /// 若该卡被同包真实光环命中且不存在基础冷却 0/null->非0 等无法由光环解释的结构缺失，
    /// 则标记 ExplainedByAura = true 并将建议严重度降级为 info；无光环覆盖时保持原有 error/warning 判定。
    /// </summary>
    public bool CompareAfterPurchaseWithAura(
        string instanceId,
        IReadOnlyDictionary<string, int> afterAttributes,
        long nowMs,
        BareAuditPurchaseAuraContext auraContext,
        out PurchaseAttributeJumpResult result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(instanceId) || afterAttributes == null) return false;

        ShelfCardSnapshot beforeSnap;
        lock (_sync)
        {
            if (!_snapshots.TryGetValue(instanceId, out beforeSnap))
            {
                return false;
            }
            _snapshots.Remove(instanceId);
        }

        if (nowMs - beforeSnap.CapturedAtMs > SnapshotTtlMs)
        {
            return false;
        }

        var afterCopy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in afterAttributes)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            afterCopy[kv.Key] = kv.Value;
        }

        var allKeys = new HashSet<string>(beforeSnap.Attributes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string k in afterCopy.Keys) allKeys.Add(k);

        var diffs = new List<PurchaseAttributeDiffEntry>();
        bool hasZeroToNonZero = false;
        bool hasUnexplainableBaseCooldownJump = false;

        foreach (string key in allKeys)
        {
            if (_ignoredKeys.Contains(key)) continue;

            bool hadBefore = beforeSnap.Attributes.TryGetValue(key, out int bVal);
            bool hasAfter = afterCopy.TryGetValue(key, out int aVal);

            int effBefore = hadBefore ? bVal : 0;
            int effAfter = hasAfter ? aVal : 0;
            if (effBefore == effAfter) continue;

            bool zeroToNonZero = effBefore == 0 && effAfter != 0;
            if (zeroToNonZero)
            {
                hasZeroToNonZero = true;
                // 冷却减免光环只会调整既有正值 CooldownMax（如 5000->4500），绝不会凭空将 CooldownMax 从 0/null 造为正值
                if (string.Equals(key, "CooldownMax", StringComparison.OrdinalIgnoreCase))
                {
                    hasUnexplainableBaseCooldownJump = true;
                }
            }

            diffs.Add(new PurchaseAttributeDiffEntry
            {
                Attribute = key,
                Before = hadBefore ? bVal : (int?)null,
                After = hasAfter ? aVal : (int?)null,
                ZeroToNonZero = zeroToNonZero,
            });
        }

        if (diffs.Count == 0) return false;

        diffs.Sort((x, y) => string.Compare(x.Attribute, y.Attribute, StringComparison.OrdinalIgnoreCase));

        bool explainedByAura = auraContext != null
            && !hasUnexplainableBaseCooldownJump
            && auraContext.IsCardAffectedByAura(beforeSnap.InstanceId);

        string[] effectIds = (explainedByAura && auraContext.EffectIds != null && auraContext.EffectIds.Count > 0)
            ? auraContext.EffectIds.ToArray()
            : Array.Empty<string>();

        string suggestedSeverity = explainedByAura
            ? "info"
            : (hasZeroToNonZero ? "error" : "warning");

        result = new PurchaseAttributeJumpResult
        {
            InstanceId = beforeSnap.InstanceId,
            TemplateId = beforeSnap.TemplateId,
            CardTitle = beforeSnap.CardTitle,
            HasZeroToNonZeroJump = hasZeroToNonZero,
            ExplainedByAura = explainedByAura,
            AuraEffectIds = effectIds,
            SuggestedSeverity = suggestedSeverity,
            BeforeAttributes = new Dictionary<string, int>(beforeSnap.Attributes, StringComparer.Ordinal),
            AfterAttributes = new Dictionary<string, int>(afterCopy, StringComparer.Ordinal),
            Diffs = diffs,
        };
        return true;
    }

    public void Clear()
    {
        lock (_sync) _snapshots.Clear();
    }

    private void PruneExpiredLocked(long nowMs)
    {
        var expired = new List<string>();
        foreach (var kv in _snapshots)
        {
            if (nowMs - kv.Value.CapturedAtMs > SnapshotTtlMs)
            {
                expired.Add(kv.Key);
            }
        }
        for (int i = 0; i < expired.Count; i++)
        {
            _snapshots.Remove(expired[i]);
        }
        if (_snapshots.Count >= MaxTrackedCards)
        {
            _snapshots.Clear();
        }
    }
}

/// <summary>状态/输入锁死超过阈值时的诊断结果。</summary>
public sealed class DeadlockTriggerResult
{
    public long StalledMs { get; set; }
    public string Scene { get; set; } = string.Empty;
    public string AppState { get; set; } = string.Empty;
    public string RunState { get; set; } = string.Empty;
    public string[] LockedReasons { get; set; } = Array.Empty<string>();
    public string Signature { get; set; } = string.Empty;
}

/// <summary>
/// 游戏状态/输入锁死超过 10s 检测器（纯 C# 状态机，支持虚拟时钟零延迟单测）。
/// </summary>
public sealed class BareAuditDeadlockDetector
{
    private readonly object _sync = new();
    private readonly long _thresholdMs;
    private readonly long _repeatIntervalMs;

    private bool _trackingLock;
    private long _lockStartMs;
    private long _lastEmitMs;
    private string _lockedStateKey = string.Empty;

    public BareAuditDeadlockDetector(long thresholdMs = 10_000L, long repeatIntervalMs = 15_000L)
    {
        _thresholdMs = Math.Max(1000L, thresholdMs);
        _repeatIntervalMs = Math.Max(1000L, repeatIntervalMs);
    }

    public DeadlockTriggerResult Evaluate(
        long nowMs,
        bool isInRun,
        string scene,
        string appState,
        string runState,
        bool blockInput,
        bool isWaitingForServer,
        bool simHandlersProcessing,
        bool cmdIsProcessing,
        bool isRevealing,
        bool hasCardsToReveal,
        bool isBoardTransitioning,
        bool isProcessingUpgrade,
        bool dialogOpen)
    {
        return Evaluate(
            nowMs, isInRun, scene, appState, runState,
            blockInput, isWaitingForServer, simHandlersProcessing, cmdIsProcessing,
            isRevealing, hasCardsToReveal, isBoardTransitioning, isProcessingUpgrade, dialogOpen,
            isUpdatingSkillBoard: false, hour6WaitForNewDayLock: false);
    }

    public DeadlockTriggerResult Evaluate(
        long nowMs,
        bool isInRun,
        string scene,
        string appState,
        string runState,
        bool blockInput,
        bool isWaitingForServer,
        bool simHandlersProcessing,
        bool cmdIsProcessing,
        bool isRevealing,
        bool hasCardsToReveal,
        bool isBoardTransitioning,
        bool isProcessingUpgrade,
        bool dialogOpen,
        bool isUpdatingSkillBoard,
        bool hour6WaitForNewDayLock)
    {
        lock (_sync)
        {
            if (!isInRun)
            {
                ResetLocked();
                return null;
            }

            var reasons = new List<string>(11);
            if (blockInput) reasons.Add("BlockInput");
            if (isWaitingForServer) reasons.Add("IsWaitingForServerResponse");
            if (simHandlersProcessing) reasons.Add("SimHandlersProcessing");
            if (cmdIsProcessing) reasons.Add("CmdIsProcessing");
            if (isRevealing) reasons.Add("BoardManagerIsRevealing");
            if (hasCardsToReveal) reasons.Add("HasCardsToReveal");
            if (isBoardTransitioning) reasons.Add("BoardTransitioning");
            if (isProcessingUpgrade) reasons.Add("ProcessingUpgradeOrEnchant");
            if (dialogOpen) reasons.Add("DialogOpen");
            if (isUpdatingSkillBoard) reasons.Add("IsUpdatingSkillBoard");
            if (hour6WaitForNewDayLock) reasons.Add("Hour6WaitForNewDayTransitionLock");

            if (reasons.Count == 0)
            {
                ResetLocked();
                return null;
            }

            string stateKey = (appState ?? "") + "|" + (runState ?? "");
            if (!_trackingLock || !string.Equals(_lockedStateKey, stateKey, StringComparison.Ordinal))
            {
                _trackingLock = true;
                _lockStartMs = nowMs;
                _lastEmitMs = -1L;
                _lockedStateKey = stateKey;
                return null;
            }

            long stalledMs = nowMs - _lockStartMs;
            if (stalledMs < _thresholdMs)
            {
                return null;
            }

            if (_lastEmitMs >= 0L && nowMs - _lastEmitMs < _repeatIntervalMs)
            {
                return null;
            }

            _lastEmitMs = nowMs;
            string[] reasonArr = reasons.ToArray();
            return new DeadlockTriggerResult
            {
                StalledMs = stalledMs,
                Scene = scene ?? string.Empty,
                AppState = appState ?? string.Empty,
                RunState = runState ?? string.Empty,
                LockedReasons = reasonArr,
                Signature = "deadlock|" + stateKey + "|" + string.Join(",", reasonArr),
            };
        }
    }

    public void Reset()
    {
        lock (_sync) ResetLocked();
    }

    private void ResetLocked()
    {
        _trackingLock = false;
        _lockStartMs = 0L;
        _lastEmitMs = -1L;
        _lockedStateKey = string.Empty;
    }
}

/// <summary>P3-02（RC-AUDIT-REVEAL-NOISE）：翻牌跳过瞬态窗口去抖判定结果。</summary>
public readonly struct RevealSkipDebounceDecision
{
    public readonly bool ShouldEmit;
    public readonly bool ImmediateBecauseSpawned;
    public readonly long ElapsedMs;
    public readonly int SuppressedTransientCount;
    public readonly string SuppressReason;

    public RevealSkipDebounceDecision(
        bool shouldEmit,
        bool immediateBecauseSpawned,
        long elapsedMs,
        int suppressedTransientCount,
        string suppressReason)
    {
        ShouldEmit = shouldEmit;
        ImmediateBecauseSpawned = immediateBecauseSpawned;
        ElapsedMs = elapsedMs;
        SuppressedTransientCount = suppressedTransientCount;
        SuppressReason = suppressReason ?? string.Empty;
    }
}

/// <summary>
/// P3-02（RC-AUDIT-REVEAL-NOISE）：PlayRevealAnimations 跳过告警去抖器（纯 C#、支持虚拟时钟单测）。
/// 规则对齐官方 BoardManager.cs:1306-1321：
///   1. Data.CardRemoval.HasCardsPendingRemoval() == true 时官方本就静默早退，立即重置窗口并抑制误报；
///   2. 无加载/翻牌阻塞或无待翻牌时，立即重置窗口；
///   3. spawned == true（CardSpawned 并发阻塞翻牌）属高危协议冲突，立即可靠上报不延迟；
///   4. 纯多卡异步 SpawnCard 加载瞬态窗口（dealt=true, spawned=false 等）短于 2.0s 时静默去抖，
///      仅在连续超过 2.0s（且相邻采样间隔未断开）时才上报持续异常。
/// </summary>
public sealed class BareAuditRevealSkipDebouncer
{
    public const long DefaultDebounceWindowMs = 2000L;
    public const long DefaultMaxContinuousGapMs = 1000L;

    private readonly object _sync = new();
    private readonly long _debounceWindowMs;
    private readonly long _maxContinuousGapMs;

    private bool _tracking;
    private string _windowKey = string.Empty;
    private long _firstSkipMs;
    private long _lastSampleMs;
    private int _suppressedCount;

    public long DebounceWindowMs => _debounceWindowMs;

    public BareAuditRevealSkipDebouncer(
        long debounceWindowMs = DefaultDebounceWindowMs,
        long maxContinuousGapMs = DefaultMaxContinuousGapMs)
    {
        _debounceWindowMs = Math.Max(0L, debounceWindowMs);
        _maxContinuousGapMs = Math.Max(200L, maxContinuousGapMs);
    }

    public RevealSkipDebounceDecision Evaluate(
        long nowMs,
        bool hasCardsPendingRemoval,
        bool dealt,
        bool spawned,
        bool collectibles,
        bool revealing,
        bool hasCardsToReveal,
        string appState,
        string runState)
    {
        lock (_sync)
        {
            if (hasCardsPendingRemoval)
            {
                ResetLocked();
                return new RevealSkipDebounceDecision(false, false, 0L, 0, "pending_removal");
            }

            if (!(dealt || spawned || collectibles || revealing) || !hasCardsToReveal)
            {
                ResetLocked();
                return new RevealSkipDebounceDecision(false, false, 0L, 0, "not_skipped");
            }

            // spawned=true（与 Choice/LevelUp 混发 CardSpawned 阻塞翻牌）保持零延迟立即告警
            if (spawned)
            {
                return new RevealSkipDebounceDecision(true, true, 0L, 0, string.Empty);
            }

            string stateKey = (appState ?? string.Empty) + "|" + (runState ?? string.Empty);
            if (!_tracking
                || !string.Equals(_windowKey, stateKey, StringComparison.Ordinal)
                || nowMs < _lastSampleMs
                || (nowMs - _lastSampleMs) > _maxContinuousGapMs)
            {
                _tracking = true;
                _windowKey = stateKey;
                _firstSkipMs = nowMs;
                _lastSampleMs = nowMs;
                _suppressedCount = 1;
                if (_debounceWindowMs == 0L)
                {
                    return new RevealSkipDebounceDecision(true, false, 0L, 0, string.Empty);
                }
                return new RevealSkipDebounceDecision(false, false, 0L, 1, "transient_loading_window");
            }

            _lastSampleMs = nowMs;
            long elapsedMs = nowMs - _firstSkipMs;
            if (elapsedMs < _debounceWindowMs)
            {
                _suppressedCount++;
                return new RevealSkipDebounceDecision(false, false, elapsedMs, _suppressedCount, "transient_loading_window");
            }

            int suppressed = _suppressedCount;
            return new RevealSkipDebounceDecision(true, false, elapsedMs, suppressed, string.Empty);
        }
    }

    public void Reset()
    {
        lock (_sync) ResetLocked();
    }

    private void ResetLocked()
    {
        _tracking = false;
        _windowKey = string.Empty;
        _firstSkipMs = 0L;
        _lastSampleMs = 0L;
        _suppressedCount = 0;
    }

    /// <summary>
    /// 从官方 BoardManager.PlayRevealAnimations skipped 告警文本中解析 (dealt=..., spawned=..., collectibles=..., revealing=...) 标记。
    /// </summary>
    public static bool TryParseRevealSkipFlags(
        string message,
        out bool dealt,
        out bool spawned,
        out bool collectibles,
        out bool revealing)
    {
        dealt = false;
        spawned = false;
        collectibles = false;
        revealing = false;
        if (string.IsNullOrWhiteSpace(message)) return false;

        bool hasDealt = TryReadBoolFlag(message, "dealt=", out dealt);
        bool hasSpawned = TryReadBoolFlag(message, "spawned=", out spawned);
        TryReadBoolFlag(message, "collectibles=", out collectibles);
        TryReadBoolFlag(message, "revealing=", out revealing);
        return hasDealt && hasSpawned;
    }

    private static bool TryReadBoolFlag(string text, string key, out bool value)
    {
        value = false;
        int idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        int valStart = idx + key.Length;
        if (valStart + 4 <= text.Length
            && string.Compare(text, valStart, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
        {
            value = true;
            return true;
        }
        if (valStart + 5 <= text.Length
            && string.Compare(text, valStart, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
        {
            value = false;
            return true;
        }
        return false;
    }
}

/// <summary>协议信号匹配结果。</summary>
public sealed class ProtocolSignalMatch
{
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = "error";
    public string SignalId { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public bool IsExpectedNoise { get; set; }
}

/// <summary>
/// 裸端高危协议信号与 Unity 错误日志分类器（支持运行期追加跨轨同步的新信号关键字）。
/// </summary>
public sealed class BareAuditProtocolSignalClassifier
{
    private readonly object _sync = new();
    private readonly List<(string SignalId, string[] AllSubstrings, string Category, string Severity)> _rules = new();

    public BareAuditProtocolSignalClassifier()
    {
        // 1. 核心高危信号：PlayRevealAnimations skipped（#1 裸端直效发卡同包直跳软锁核心标志）
        RegisterSignalRule("play_reveal_animations_skipped", new[] { "PlayRevealAnimations skipped" }, BareAuditCategory.ProtocolRevealSkipped, "critical");
        RegisterSignalRule("play_reveal_animations_skipped_split", new[] { "PlayRevealAnimations", "skipped" }, BareAuditCategory.ProtocolRevealSkipped, "critical");

        // 2. 反向审计 CompatAuditPatch 家族识别的同类裸端协议缺陷信号（与 track-a #8 对齐）
        RegisterSignalRule("pvp_combat_without_opponent", new[] { "entered PVPCombatState without an assigned PvP opponent" }, BareAuditCategory.ProtocolAnomaly, "critical");
        RegisterSignalRule("skipping_board_movement", new[] { "Skipping board movement for card" }, BareAuditCategory.ProtocolAnomaly, "error");
        RegisterSignalRule("skipping_board_target_update", new[] { "Skipping board target update for card" }, BareAuditCategory.ProtocolAnomaly, "error");
        RegisterSignalRule("upgrade_card_not_found", new[] { "Failed to find card for upgrade" }, BareAuditCategory.ProtocolAnomaly, "error");
        RegisterSignalRule("fusion_card_not_found", new[] { "Failed to find card for fusion" }, BareAuditCategory.ProtocolAnomaly, "error");
        RegisterSignalRule("skill_presentation_duplicate", new[] { "SkillPresentationManager", "ArgumentException" }, BareAuditCategory.ProtocolAnomaly, "critical");
        RegisterSignalRule("skill_board_updating_stuck", new[] { "IsUpdatingSkillBoard" }, BareAuditCategory.ProtocolAnomaly, "error");
        RegisterSignalRule("board_presentation_cleanup_stuck", new[] { "BoardStatePresentation", "CleanUp" }, BareAuditCategory.ProtocolAnomaly, "warning");
        RegisterSignalRule("board_transition_stuck", new[] { "BoardTransitionController", "IsTransitioning" }, BareAuditCategory.ProtocolAnomaly, "warning");
        RegisterSignalRule("cmd_already_in_progress", new[] { "Command already in progress, ignoring new command" }, BareAuditCategory.ProtocolAnomaly, "warning");
        RegisterSignalRule("cmd_failed", new[] { "Command failed:" }, BareAuditCategory.ProtocolAnomaly, "error");
        RegisterSignalRule("cmd_execution_error", new[] { "Command execution error:" }, BareAuditCategory.ProtocolAnomaly, "error");
        RegisterSignalRule("pvp_vfx_loop_end_missing", new[] { "Failed to find VFX loop end time" }, BareAuditCategory.ProtocolAnomaly, "warning");
        RegisterSignalRule("sockets_or_placement_failed", new[] { "PlaceOnSocket" }, BareAuditCategory.ProtocolAnomaly, "error");
    }

    public void RegisterSignalRule(string signalId, string[] requiredSubstrings, string category, string severity = "error")
    {
        if (string.IsNullOrWhiteSpace(signalId) || requiredSubstrings == null || requiredSubstrings.Length == 0)
            return;

        lock (_sync)
        {
            _rules.Add((signalId, requiredSubstrings, category ?? BareAuditCategory.ProtocolAnomaly, severity ?? "error"));
        }
    }

    public ProtocolSignalMatch ClassifyUnityLog(string logType, string message, string stackTrace)
    {
        return ClassifyUnityLogInternal(logType, message, stackTrace, scene: null, appState: null, runState: null, filterExpectedSseCancel: false);
    }

    /// <summary>
    /// P3-03（RC-AUDIT-SSE-CANCEL）：带场景与 AppState 上下文的 Unity 日志分类入口。
    /// 自动识别 StartRunAppState 切入对局取消主菜单 SSE GET /events 时触发的无业务栈 Winsock 995 取消噪音并降级为 info/IsExpectedNoise，
    /// 绝不误杀携带业务堆栈、处于局内活跃态或非取消性质的真实异常。
    /// </summary>
    public ProtocolSignalMatch ClassifyUnityLogWithContext(
        string logType,
        string message,
        string stackTrace,
        string scene,
        string appState,
        string runState)
    {
        return ClassifyUnityLogInternal(logType, message, stackTrace, scene, appState, runState, filterExpectedSseCancel: true);
    }

    private ProtocolSignalMatch ClassifyUnityLogInternal(
        string logType,
        string message,
        string stackTrace,
        string scene,
        string appState,
        string runState,
        bool filterExpectedSseCancel)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        // 绝对禁止捕获黑匣子自身日志造成递归风暴
        if (message.IndexOf("[BareClientAudit]", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return null;
        }

        // 1. 优先匹配高危裸协议信号（无论日志级别为 Warning / Error / Exception 均捕获）
        lock (_sync)
        {
            for (int i = 0; i < _rules.Count; i++)
            {
                var rule = _rules[i];
                bool allMatched = true;
                for (int k = 0; k < rule.AllSubstrings.Length; k++)
                {
                    if (message.IndexOf(rule.AllSubstrings[k], StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        allMatched = false;
                        break;
                    }
                }
                if (allMatched)
                {
                    return new ProtocolSignalMatch
                    {
                        Category = rule.Category,
                        Severity = rule.Severity,
                        SignalId = rule.SignalId,
                        Signature = rule.Category + "|" + rule.SignalId + "|" + BuildCompactSignatureSeed(message),
                    };
                }
            }
        }

        // 2. 捕获全部 Unity Error / Exception / Assert
        bool isException = string.Equals(logType, "Exception", StringComparison.OrdinalIgnoreCase);
        bool isError = string.Equals(logType, "Error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(logType, "Assert", StringComparison.OrdinalIgnoreCase);
        if (!isException && !isError)
        {
            return null;
        }

        // P3-03：识别主菜单 SSE 主动取消触发的底层 Winsock 995 / OperationCanceled 释放噪音
        if (filterExpectedSseCancel && IsExpectedSseCancellation(logType, message, stackTrace, scene, appState, runState))
        {
            return new ProtocolSignalMatch
            {
                Category = BareAuditCategory.UnityErrorOrException,
                Severity = "info",
                SignalId = "expected_sse_cancel",
                Signature = "unity|sse_cancel|" + (appState ?? string.Empty),
                IsExpectedNoise = true,
            };
        }

        string topFrame = ExtractFirstStackLine(stackTrace);
        return new ProtocolSignalMatch
        {
            Category = BareAuditCategory.UnityErrorOrException,
            Severity = isException ? "critical" : "error",
            SignalId = isException ? "unity_exception" : "unity_error",
            Signature = "unity|" + logType + "|" + BuildCompactSignatureSeed(message) + "|" + topFrame,
        };
    }

    /// <summary>
    /// P3-03（RC-AUDIT-SSE-CANCEL）：严格判定某条 Unity 异常是否为 StartRunAppState / 主菜单主动取消 SSE GET /events 的预期释放噪音。
    /// 必须同时满足：
    ///   1. 属于 SocketException / IOException / OperationCanceledException / ObjectDisposedException / HttpRequestException 族；
    ///   2. 携带 Winsock 995（中英文 WSA_OPERATION_ABORTED / NativeErrorCode 995）或 SSE 流主动取消/释放特征；
    ///   3. 堆栈中不包含任何业务 C# 帧（仅允许空栈、Rethrow 包装行、纯 BCL 网络/IO 帧或官方 ReadServerSentEvent/TournamentAssignedLobbyService 帧）；
    ///   4. 发生在 StartRunAppState / 主菜单切局窗口，或堆栈显式指明为官方 SSE 读流取消。
    /// </summary>
    public static bool IsExpectedSseCancellation(
        string logType,
        string message,
        string stackTrace,
        string scene,
        string appState,
        string runState)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        bool isExceptionOrError = string.Equals(logType, "Exception", StringComparison.OrdinalIgnoreCase)
            || string.Equals(logType, "Error", StringComparison.OrdinalIgnoreCase);
        if (!isExceptionOrError) return false;

        string msg = message;
        string st = stackTrace ?? string.Empty;

        // 1. 必须是底层网络/IO/取消/流释放异常族（绝不过滤 NullReferenceException/ArgumentException 等崩溃）
        bool isNetIoCancelEx = ContainsAnyOrdinalIgnoreCase(
            msg,
            "SocketException",
            "IOException",
            "OperationCanceledException",
            "TaskCanceledException",
            "ObjectDisposedException",
            "HttpRequestException")
            || ContainsAnyOrdinalIgnoreCase(
                st,
                "SocketException",
                "IOException",
                "OperationCanceledException",
                "TaskCanceledException",
                "ObjectDisposedException",
                "HttpRequestException");
        if (!isNetIoCancelEx) return false;

        // 2. 必须携带明确的 Winsock 995 / I/O 中止 / SSE 主动取消释放特征（严禁裸匹配 "995" 防误撞毫秒时间戳 .995）
        bool hasWinsock995OrAbort = ContainsAnyOrdinalIgnoreCase(
            msg,
            "The I/O operation has been aborted because of either a thread exit or an application request",
            "I/O operation has been aborted",
            "由于线程退出或应用程序请求，已中止 I/O 操作",
            "已中止 I/O 操作",
            "WSA_OPERATION_ABORTED",
            "OperationAborted",
            "NativeErrorCode=995",
            "NativeErrorCode: 995",
            "NativeErrorCode 995",
            "ErrorCode=995",
            "ErrorCode: 995",
            "ErrorCode 995",
            "error 995",
            "(995)",
            "WSACancelBlockingCall")
            || ContainsAnyOrdinalIgnoreCase(
                st,
                "The I/O operation has been aborted because of either a thread exit or an application request",
                "I/O operation has been aborted",
                "由于线程退出或应用程序请求，已中止 I/O 操作",
                "已中止 I/O 操作",
                "WSA_OPERATION_ABORTED",
                "OperationAborted",
                "NativeErrorCode=995",
                "NativeErrorCode: 995",
                "NativeErrorCode 995",
                "ErrorCode=995",
                "ErrorCode: 995",
                "ErrorCode 995",
                "error 995",
                "(995)",
                "WSACancelBlockingCall");

        bool hasOperationCanceled = ContainsAnyOrdinalIgnoreCase(msg, "OperationCanceledException", "TaskCanceledException")
            || ContainsAnyOrdinalIgnoreCase(st, "OperationCanceledException", "TaskCanceledException");

        bool hasTransportCancel = (ContainsAnyOrdinalIgnoreCase(msg, "Unable to read data from the transport connection")
                || ContainsAnyOrdinalIgnoreCase(st, "Unable to read data from the transport connection"))
            && (ContainsAnyOrdinalIgnoreCase(msg, "aborted", "已中止", "canceled", "cancelled", "已取消", "disposed", "已释放", "closed", "已关闭", "interrupted")
                || ContainsAnyOrdinalIgnoreCase(st, "aborted", "已中止", "canceled", "cancelled", "已取消", "disposed", "已释放", "closed", "已关闭", "interrupted"));

        bool hasSseStreamDisposed = (ContainsAnyOrdinalIgnoreCase(msg, "ObjectDisposedException", "Cannot access a disposed object")
                || ContainsAnyOrdinalIgnoreCase(st, "ObjectDisposedException", "Cannot access a disposed object"))
            && (ContainsAnyOrdinalIgnoreCase(msg, "Stream", "Socket", "HttpResponse", "ReadServerSentEvent", "Chunked")
                || ContainsAnyOrdinalIgnoreCase(st, "Stream", "Socket", "HttpResponse", "ReadServerSentEvent", "Chunked"));

        if (!hasWinsock995OrAbort && !hasOperationCanceled && !hasTransportCancel && !hasSseStreamDisposed)
            return false;

        // 3. 严禁过滤带业务 C# 堆栈帧的真实异常（如 GameSimHandler / NetworkManager / NameAuth / StartRunAppState 自身业务报错）
        if (HasBusinessStackFrame(st))
            return false;

        // 4. 必须处于 StartRunAppState / 主菜单切局 SSE 取消窗口，或堆栈显式指向官方 SSE 读流取消路径
        bool isExplicitSseStack = ContainsAnyOrdinalIgnoreCase(msg, "ReadServerSentEvent", "TournamentAssignedLobbyService")
            || ContainsAnyOrdinalIgnoreCase(st, "ReadServerSentEvent", "TournamentAssignedLobbyService");
        if (isExplicitSseStack)
            return true;

        return IsSseCancelLifecycleWindow(scene, appState);
    }

    private static bool IsSseCancelLifecycleWindow(string scene, string appState)
    {
        if (!string.IsNullOrWhiteSpace(appState))
        {
            string s = appState.Trim();
            if (s.IndexOf("StartRunAppState", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return string.Equals(s, "MainMenuAppState", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "MainMenu", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "MainMenuState", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "TitleAppState", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "InitializationAppState", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "LoadingAppState", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "ShutdownAppState", StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(scene))
            return true;

        string sc = scene.Trim();
        return string.Equals(sc, "MainMenuScene", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sc, "DefaultLoadingScene", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sc, "AppStart", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sc, "Initialization", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 检测堆栈是否包含任意业务 C# 帧。空栈、Rethrow 包装行、纯 BCL 网络/IO 底层帧以及官方 SSE 读流/取消帧返回 false；
    /// 其余任意业务调用帧（如 TheBazaar.*、BazaarLauncher.*、Networking.NetworkManager.* 等）一律返回 true 拒绝过滤。
    /// </summary>
    private static bool HasBusinessStackFrame(string stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return false;

        string[] lines = stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;

            // 跳过 Mono/Unity 异常链包装头行（非堆栈帧）
            if (line.StartsWith("Rethrow as ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("--- End of stack trace", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("--->", StringComparison.Ordinal)
                || line.StartsWith("SocketException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("IOException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("OperationCanceledException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("TaskCanceledException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("ObjectDisposedException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("HttpRequestException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("System.Net.Sockets.SocketException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("System.IO.IOException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("System.OperationCanceledException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("System.Threading.Tasks.TaskCanceledException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("System.ObjectDisposedException", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("System.Net.Http.HttpRequestException", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string frame = line;
            if (frame.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
            {
                frame = frame.Substring(3).TrimStart();
            }
            else if (frame.StartsWith("在 ", StringComparison.Ordinal))
            {
                frame = frame.Substring(2).TrimStart();
            }

            if (IsAllowedLowLevelOrSseFrame(frame))
                continue;

            return true;
        }

        return false;
    }

    private static bool IsAllowedLowLevelOrSseFrame(string frame)
    {
        if (string.IsNullOrEmpty(frame)) return true;

        if (frame.StartsWith("System.Net.", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("System.IO.", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("System.Threading.", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("System.Runtime.", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("System.Buffers.", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("Mono.Net.", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("Mono.Security.", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 仅放行官方 SSE 读流与大厅监听取消链，其他 Networking.*（如 NetworkManager）仍视为业务帧
        if ((frame.StartsWith("Networking.HttpClientHelpers", StringComparison.OrdinalIgnoreCase)
                && frame.IndexOf("ReadServerSentEvent", StringComparison.OrdinalIgnoreCase) >= 0)
            || frame.StartsWith("Networking.TournamentAssignedLobbyService", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsAnyOrdinalIgnoreCase(string source, params string[] needles)
    {
        if (string.IsNullOrEmpty(source) || needles == null) return false;
        for (int i = 0; i < needles.Length; i++)
        {
            string n = needles[i];
            if (!string.IsNullOrEmpty(n) && source.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private static string BuildCompactSignatureSeed(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return string.Empty;
        string trimmed = msg.Trim();
        int nl = trimmed.IndexOf('\n');
        if (nl >= 0) trimmed = trimmed.Substring(0, nl).TrimEnd('\r');
        trimmed = StripLeadingClockPrefix(trimmed);
        if (trimmed.Length > 120) trimmed = trimmed.Substring(0, 120);
        return trimmed;
    }

    /// <summary>剥离 AppLogger 常见前缀时间戳 [HH:mm:ss.fff] / [HH:mm:ss]，避免毫秒时间戳穿透速率去重。</summary>
    private static string StripLeadingClockPrefix(string s)
    {
        if (string.IsNullOrEmpty(s) || s[0] != '[') return s;
        int closeIdx = s.IndexOf(']');
        if (closeIdx < 8 || closeIdx > 16) return s;

        // 校验 [HH:mm:ss...] 格式：下标 1,2 数字，3 为 ':'，4,5 数字，6 为 ':'，7,8 数字
        if (char.IsDigit(s[1]) && char.IsDigit(s[2]) && s[3] == ':'
            && char.IsDigit(s[4]) && char.IsDigit(s[5]) && s[6] == ':'
            && char.IsDigit(s[7]) && char.IsDigit(s[8]))
        {
            return s.Substring(closeIdx + 1).TrimStart();
        }
        return s;
    }

    private static string ExtractFirstStackLine(string stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return string.Empty;
        string[] lines = stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return string.Empty;
        string line = lines[0].Trim();
        return line.Length > 100 ? line.Substring(0, 100) : line;
    }
}

/// <summary>同包 NetMessageGameSim 只读协议不变量描述符（零 Unity 依赖，与 track-a #8 清单逐条对应）。</summary>
public sealed class BareAuditGameSimBatchInput
{
    public string MessageId { get; set; } = string.Empty;
    public string PreviousRunState { get; set; } = string.Empty;
    public string ResultingRunState { get; set; } = string.Empty;
    public int ResultingHour { get; set; }
    public int HoursInADay { get; set; } = 6;
    public bool HasLeftChoiceInBatch { get; set; }
    public bool HasCardDealt { get; set; }
    public int NewlyUnlockedPlayerHandMask { get; set; }
    public int ExistingPlayerHandMask { get; set; }
    public List<string> SpawnedCardInstanceIds { get; set; } = new();
    public List<int> SpawnedPlayerHandSockets { get; set; } = new();
    public List<string> EquippedSkillInstanceIds { get; set; } = new();
    public HashSet<string> ExistingEntityIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> TransformReferencedCardIds { get; set; } = new();
    public HashSet<string> SimUpdateCardKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> MovedCardInstanceIds { get; set; } = new();
}

/// <summary>单条同包协议不变量违例告警。</summary>
public sealed class BareAuditProtocolInvariantViolation
{
    public string Category { get; set; } = BareAuditCategory.ProtocolAnomaly;
    public string Severity { get; set; } = "error";
    public string SignalId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public Dictionary<string, object> Context { get; set; } = new();
}

/// <summary>
/// 裸客户端 NetMessageGameSim 同包协议不变量纯 C# 检验器（只读、零修改、零 Unity 依赖）。
/// </summary>
public static class BareAuditGameSimInvariantChecker
{
    public static List<BareAuditProtocolInvariantViolation> InspectBatch(BareAuditGameSimBatchInput input)
    {
        var violations = new List<BareAuditProtocolInvariantViolation>();
        if (input == null) return violations;

        bool hasSpawned = input.SpawnedCardInstanceIds != null && input.SpawnedCardInstanceIds.Count > 0;
        var spawnedSet = new HashSet<string>(input.SpawnedCardInstanceIds ?? (IEnumerable<string>)Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // 不变量 1：同包同时含 CardSpawned 与 CardDealt 且无状态离开 Choice（直接诱发 PlayRevealAnimations skipped (spawned=True)）
        bool stayedInChoice = string.Equals(input.PreviousRunState, "Choice", StringComparison.OrdinalIgnoreCase)
            && string.Equals(input.ResultingRunState, "Choice", StringComparison.OrdinalIgnoreCase)
            && !input.HasLeftChoiceInBatch;
        if (stayedInChoice && hasSpawned && input.HasCardDealt)
        {
            violations.Add(new BareAuditProtocolInvariantViolation
            {
                Category = BareAuditCategory.ProtocolRevealSkipped,
                Severity = "critical",
                SignalId = "same_batch_spawned_and_dealt_in_choice",
                Summary = $"同包在 Choice->Choice 无离开门控状态下同时包含 CardSpawned({input.SpawnedCardInstanceIds.Count}) 与 CardDealt，高危诱发 PlayRevealAnimations skipped",
                Context = new Dictionary<string, object>
                {
                    ["messageId"] = input.MessageId ?? string.Empty,
                    ["spawnedCount"] = input.SpawnedCardInstanceIds.Count,
                    ["previousRunState"] = input.PreviousRunState ?? string.Empty,
                    ["resultingRunState"] = input.ResultingRunState ?? string.Empty,
                },
            });
        }

        // 不变量 2：处于 LevelUp 且 Hour == HoursInADay（诱发 TryUpdateClock 提前锁死 _waitForNewDayTransition）
        int safeHoursInDay = input.HoursInADay > 0 ? input.HoursInADay : 6;
        if (string.Equals(input.ResultingRunState, "LevelUp", StringComparison.OrdinalIgnoreCase)
            && input.ResultingHour == safeHoursInDay)
        {
            violations.Add(new BareAuditProtocolInvariantViolation
            {
                Category = BareAuditCategory.ProtocolAnomaly,
                Severity = "critical",
                SignalId = "levelup_at_pvp_hour_clock_lock_risk",
                Summary = $"进入 LevelUp 状态时 Hour={input.ResultingHour} 已等于 HoursInADay={safeHoursInDay}，高危诱发 TryUpdateClock 提前锁死 _waitForNewDayTransition",
                Context = new Dictionary<string, object>
                {
                    ["messageId"] = input.MessageId ?? string.Empty,
                    ["hour"] = input.ResultingHour,
                    ["hoursInADay"] = safeHoursInDay,
                },
            });
        }

        // 不变量 3：同包含 SocketsUnlocked 且 CardSpawned 引用了新解锁的槽位（GameSimHandler 先跑 CardSpawned 后跑 SocketsUnlocked）
        if (input.NewlyUnlockedPlayerHandMask != 0 && input.SpawnedPlayerHandSockets != null && input.SpawnedPlayerHandSockets.Count > 0)
        {
            int newlyAddedBits = input.NewlyUnlockedPlayerHandMask & ~input.ExistingPlayerHandMask;
            if (newlyAddedBits != 0)
            {
                var conflictingSockets = new List<int>();
                for (int i = 0; i < input.SpawnedPlayerHandSockets.Count; i++)
                {
                    int sock = input.SpawnedPlayerHandSockets[i];
                    if (sock >= 0 && sock < 16 && (newlyAddedBits & (1 << sock)) != 0)
                    {
                        conflictingSockets.Add(sock);
                    }
                }
                if (conflictingSockets.Count > 0)
                {
                    violations.Add(new BareAuditProtocolInvariantViolation
                    {
                        Category = BareAuditCategory.ProtocolAnomaly,
                        Severity = "error",
                        SignalId = "sockets_unlocked_after_card_spawned_conflict",
                        Summary = $"同包 CardSpawned 引用了由同包 SocketsUnlocked 新解锁的手牌槽位 [{string.Join(",", conflictingSockets)}]，裸端先跑 CardSpawned 会被 PlaceOnSocket 拒放",
                        Context = new Dictionary<string, object>
                        {
                            ["messageId"] = input.MessageId ?? string.Empty,
                            ["existingMask"] = input.ExistingPlayerHandMask,
                            ["newlyUnlockedMask"] = input.NewlyUnlockedPlayerHandMask,
                            ["conflictingSockets"] = conflictingSockets,
                        },
                    });
                }
            }
        }

        // 不变量 4：PlayerSkillEquipped 引用了现有 Entities 与本包 CardSpawned 均不存在的 InstanceId
        if (input.EquippedSkillInstanceIds != null && input.EquippedSkillInstanceIds.Count > 0)
        {
            var orphans = new List<string>();
            for (int i = 0; i < input.EquippedSkillInstanceIds.Count; i++)
            {
                string sid = input.EquippedSkillInstanceIds[i];
                if (string.IsNullOrWhiteSpace(sid)) continue;
                bool exists = (input.ExistingEntityIds != null && input.ExistingEntityIds.Contains(sid)) || spawnedSet.Contains(sid);
                if (!exists) orphans.Add(sid);
            }
            if (orphans.Count > 0)
            {
                violations.Add(new BareAuditProtocolInvariantViolation
                {
                    Category = BareAuditCategory.ProtocolAnomaly,
                    Severity = "error",
                    SignalId = "orphan_player_skill_equipped",
                    Summary = $"PlayerSkillEquipped 引用了客户端 Entities 与本包 CardSpawned 均不存在的技能实例: [{string.Join(",", orphans)}]",
                    Context = new Dictionary<string, object>
                    {
                        ["messageId"] = input.MessageId ?? string.Empty,
                        ["orphanSkillIds"] = orphans,
                    },
                });
            }
        }

        // 不变量 5：CardTransformed / TransformReverted 目标卡在 GameSim.Cards 中缺 Key
        if (input.TransformReferencedCardIds != null && input.TransformReferencedCardIds.Count > 0)
        {
            var missingKeys = new List<string>();
            for (int i = 0; i < input.TransformReferencedCardIds.Count; i++)
            {
                string cid = input.TransformReferencedCardIds[i];
                if (string.IsNullOrWhiteSpace(cid)) continue;
                if (input.SimUpdateCardKeys == null || !input.SimUpdateCardKeys.Contains(cid))
                {
                    missingKeys.Add(cid);
                }
            }
            if (missingKeys.Count > 0)
            {
                violations.Add(new BareAuditProtocolInvariantViolation
                {
                    Category = BareAuditCategory.ProtocolAnomaly,
                    Severity = "error",
                    SignalId = "transform_missing_sim_card_key",
                    Summary = $"CardTransformed/TransformReverted 引用的卡牌在 GameSim.Cards 中缺失 Key: [{string.Join(",", missingKeys)}]",
                    Context = new Dictionary<string, object>
                    {
                        ["messageId"] = input.MessageId ?? string.Empty,
                        ["missingCardIds"] = missingKeys,
                    },
                });
            }
        }

        // 不变量 6：CardMoved 指向同包刚 CardSpawned 的卡（可能触发 SoundEventListener.OnCardMoved 缺 CardController NRE）
        if (input.MovedCardInstanceIds != null && input.MovedCardInstanceIds.Count > 0 && spawnedSet.Count > 0)
        {
            var sameBatchMoved = new List<string>();
            for (int i = 0; i < input.MovedCardInstanceIds.Count; i++)
            {
                string mid = input.MovedCardInstanceIds[i];
                if (!string.IsNullOrWhiteSpace(mid) && spawnedSet.Contains(mid))
                {
                    sameBatchMoved.Add(mid);
                }
            }
            if (sameBatchMoved.Count > 0)
            {
                violations.Add(new BareAuditProtocolInvariantViolation
                {
                    Category = BareAuditCategory.ProtocolAnomaly,
                    Severity = "warning",
                    SignalId = "card_moved_same_batch_as_spawned",
                    Summary = $"同包 CardMoved 指向本包刚 CardSpawned 的卡牌: [{string.Join(",", sameBatchMoved)}]，裸端 SoundEventListener.OnCardMoved 可能缺失 CardController",
                    Context = new Dictionary<string, object>
                    {
                        ["messageId"] = input.MessageId ?? string.Empty,
                        ["cardIds"] = sameBatchMoved,
                    },
                });
            }
        }

        return violations;
    }

    /// <summary>
    /// 不变量 7：Hour 6 Choice 态误锁 _waitForNewDayTransition 且存在待翻卡但并无真正跨日切换（精确命中空桌软锁）。
    /// </summary>
    public static bool IsHour6WaitNewDayRevealLock(
        string runState,
        int hour,
        int hoursInADay,
        bool waitForNewDayTransition,
        bool newDayTransitionActiveOrPending,
        bool hasCardsToReveal)
    {
        if (!waitForNewDayTransition || newDayTransitionActiveOrPending || !hasCardsToReveal)
            return false;
        int safeHours = hoursInADay > 0 ? hoursInADay : 6;
        if (hour != safeHours) return false;
        return string.Equals(runState, "Choice", StringComparison.OrdinalIgnoreCase)
            || string.Equals(runState, "TournamentPvpChoice", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 裸协议黑匣子总引擎（零 Unity 依赖，线程安全 + 异步单写线程安全追加 JSONL）。
/// 当 Enabled == false 时严格零线程、零目录创建、零文件 I/O。
/// </summary>
public sealed class BareClientAuditEngine : IDisposable
{
    private const int MaxPendingWriteQueue = 2048;
    private const int MaxStackTraceChars = 1200;

    private readonly bool _enabled;
    private readonly string _jsonlFilePath;
    private readonly Func<long> _clockMs;
    private readonly BareAuditRingBuffer<BareAuditTraceEvent> _ringBuffer;
    private readonly BareAuditRateLimiter _rateLimiter;
    private readonly ShopPurchaseAttributeDiffDetector _purchaseDetector;
    private readonly BareAuditDeadlockDetector _deadlockDetector;
    private readonly BareAuditProtocolSignalClassifier _signalClassifier;
    private readonly BareAuditRevealSkipDebouncer _directRevealDebouncer;
    private readonly BareAuditRevealSkipDebouncer _logRevealDebouncer;

    private readonly ConcurrentQueue<string> _writeQueue = new();
    private readonly AutoResetEvent _wakeWriter = new(false);
    private readonly ManualResetEventSlim _drainedSignal = new(true);
    private readonly Thread _writerThread;
    private int _pendingCount;
    private int _disposed;
    private long _droppedByQueueOverflow;
    private long _totalWrittenLines;

    public bool Enabled => _enabled;
    public string JsonlFilePath => _jsonlFilePath;
    public long TotalWrittenLines => Volatile.Read(ref _totalWrittenLines);
    public long DroppedByQueueOverflow => Volatile.Read(ref _droppedByQueueOverflow);
    public BareAuditProtocolSignalClassifier SignalClassifier => _signalClassifier;

    public BareClientAuditEngine(
        bool enabled,
        string jsonlFilePath,
        int ringBufferCapacity = 48,
        long dedupeWindowMs = 5000L,
        int maxRecordsPer10Seconds = 40,
        long deadlockThresholdMs = 10000L,
        Func<long> clockMs = null)
    {
        _enabled = enabled;
        _jsonlFilePath = jsonlFilePath ?? string.Empty;
        _clockMs = clockMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _ringBuffer = new BareAuditRingBuffer<BareAuditTraceEvent>(ringBufferCapacity);
        _rateLimiter = new BareAuditRateLimiter(dedupeWindowMs, maxRecordsPer10Seconds, 10_000L);
        _purchaseDetector = new ShopPurchaseAttributeDiffDetector();
        _deadlockDetector = new BareAuditDeadlockDetector(deadlockThresholdMs, 15_000L);
        _signalClassifier = new BareAuditProtocolSignalClassifier();
        _directRevealDebouncer = new BareAuditRevealSkipDebouncer(
            BareAuditRevealSkipDebouncer.DefaultDebounceWindowMs,
            BareAuditRevealSkipDebouncer.DefaultMaxContinuousGapMs);
        _logRevealDebouncer = new BareAuditRevealSkipDebouncer(
            BareAuditRevealSkipDebouncer.DefaultDebounceWindowMs,
            BareAuditRevealSkipDebouncer.DefaultMaxContinuousGapMs);

        if (_enabled && !string.IsNullOrWhiteSpace(_jsonlFilePath))
        {
            _writerThread = new Thread(WriterLoop)
            {
                Name = "BazaarLauncher.BareClientAuditWriter",
                IsBackground = true,
            };
            _writerThread.Start();
        }
    }

    public void RecordTrace(string direction, string kind, string summary)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return;

        _ringBuffer.Add(new BareAuditTraceEvent
        {
            TimestampUtc = FormatTimestampUtc(_clockMs()),
            Direction = direction ?? string.Empty,
            Kind = kind ?? string.Empty,
            Summary = Truncate(summary, 300),
        });
    }

    public BareAuditTraceEvent[] GetRecentEventChain() => _ringBuffer.Snapshot();

    public void CaptureShopItemBeforePurchase(
        string instanceId,
        string templateId,
        string cardTitle,
        IReadOnlyDictionary<string, int> attributes)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return;
        _purchaseDetector.CaptureShelfSnapshot(instanceId, templateId, cardTitle, attributes, _clockMs());
    }

    public bool VerifyShopItemAfterPurchase(
        string instanceId,
        IReadOnlyDictionary<string, int> afterAttributes,
        string scene,
        string appState,
        string runState,
        int day,
        int hour)
    {
        return VerifyShopItemAfterPurchaseWithAura(
            instanceId,
            afterAttributes,
            scene,
            appState,
            runState,
            day,
            hour,
            auraContext: null,
            emitAuraExplainedInfo: false);
    }

    /// <summary>
    /// P3-01（RC-AUDIT-AURA-PURCHASE-FP）：结合同包 EffectAuraExecuted 光环上下文校验货架购入前后属性跳变。
    /// 命中合法入场光环时，写入事件链 trace 并默认豁免误报（返回 false）；若调用方显式要求 emitAuraExplainedInfo=true，
    /// 则以 severity="info" 落盘。无真实光环解释的跳变仍保持原有 error/warning 报警能力。
    /// </summary>
    public bool VerifyShopItemAfterPurchaseWithAura(
        string instanceId,
        IReadOnlyDictionary<string, int> afterAttributes,
        string scene,
        string appState,
        string runState,
        int day,
        int hour,
        BareAuditPurchaseAuraContext auraContext,
        bool emitAuraExplainedInfo = false)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return false;
        long nowMs = _clockMs();
        if (!_purchaseDetector.CompareAfterPurchaseWithAura(instanceId, afterAttributes, nowMs, auraContext, out var jump))
        {
            return false;
        }

        if (jump.ExplainedByAura)
        {
            string auraIdsText = jump.AuraEffectIds != null && jump.AuraEffectIds.Length > 0
                ? string.Join(",", jump.AuraEffectIds)
                : "aura";
            RecordTrace(
                "audit",
                "ShopPurchaseAuraExplained",
                $"{jump.CardTitle} ({jump.InstanceId}) diffs={jump.Diffs.Count} auras=[{auraIdsText}]");

            if (!emitAuraExplainedInfo)
            {
                return false;
            }
        }

        var ctx = new Dictionary<string, object>
        {
            ["instanceId"] = jump.InstanceId,
            ["templateId"] = jump.TemplateId,
            ["cardTitle"] = jump.CardTitle,
            ["hasZeroToNonZeroJump"] = jump.HasZeroToNonZeroJump,
            ["auraExplained"] = jump.ExplainedByAura,
            ["hasBatchRealAura"] = auraContext != null && auraContext.HasAuraExecuted,
            ["auraEffectIds"] = jump.AuraEffectIds ?? Array.Empty<string>(),
            ["diffs"] = jump.Diffs,
            ["beforeAttributes"] = jump.BeforeAttributes,
            ["afterAttributes"] = jump.AfterAttributes,
        };

        string sig = (jump.ExplainedByAura ? "purchase_jump_aura|" : "purchase_jump|") + jump.TemplateId + "|" + jump.InstanceId;
        string summary = jump.ExplainedByAura
            ? $"货架购入合法入场光环属性变化: {jump.CardTitle} ({jump.InstanceId}), 差异字段数={jump.Diffs.Count}, auras=[{string.Join(",", jump.AuraEffectIds ?? Array.Empty<string>())}]"
            : $"货架购入前后属性跳变: {jump.CardTitle} ({jump.InstanceId}), 差异字段数={jump.Diffs.Count}, zeroToNonZero={jump.HasZeroToNonZeroJump}";
        return EmitAnomaly(
            BareAuditCategory.ShopPurchaseAttributeJump,
            jump.SuggestedSeverity,
            sig,
            summary,
            scene,
            appState,
            runState,
            day,
            hour,
            ctx,
            nowMs);
    }

    /// <summary>P3-02（RC-AUDIT-REVEAL-NOISE）：在翻牌恢复正常或存在待移除队列时重置翻牌跳过瞬态去抖窗口。</summary>
    public void ResetRevealSkipDebounce()
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return;
        _directRevealDebouncer.Reset();
        _logRevealDebouncer.Reset();
    }

    /// <summary>
    /// P3-02（RC-AUDIT-REVEAL-NOISE）：评估来自 BoardManager.PlayRevealAnimations 前置钩子的跳过状态。
    /// 若存在 HasCardsPendingRemoval 或属于短于 2.0s 的多卡异步 SpawnCard 瞬态加载窗口，则静默抑制；
    /// 持续超过 2.0s 或 spawned=true 并发阻塞时立即上报 critical 告警。
    /// </summary>
    public bool RecordDirectRevealSkip(
        bool hasCardsPendingRemoval,
        bool dealt,
        bool spawned,
        bool collectibles,
        bool revealing,
        bool hasCardsToReveal,
        string scene,
        string appState,
        string runState,
        int day,
        int hour)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return false;
        long nowMs = _clockMs();
        var decision = _directRevealDebouncer.Evaluate(
            nowMs,
            hasCardsPendingRemoval,
            dealt,
            spawned,
            collectibles,
            revealing,
            hasCardsToReveal,
            appState,
            runState);

        if (!decision.ShouldEmit)
        {
            return false;
        }

        var ctx = new Dictionary<string, object>
        {
            ["dealt"] = dealt,
            ["spawned"] = spawned,
            ["collectibles"] = collectibles,
            ["revealing"] = revealing,
            ["hasCardsToReveal"] = hasCardsToReveal,
            ["hasCardsPendingRemoval"] = false,
            ["elapsedMs"] = decision.ElapsedMs,
            ["debouncedTransientCount"] = decision.SuppressedTransientCount,
            ["signalId"] = "play_reveal_animations_skipped_direct",
        };

        string sig = BareAuditCategory.ProtocolRevealSkipped + "|play_reveal_animations_skipped_direct|" + (appState ?? "") + "|" + (runState ?? "");
        string summary = decision.ImmediateBecauseSpawned
            ? $"PlayRevealAnimations skipped (dealt={dealt}, spawned={spawned}, collectibles={collectibles}, revealing={revealing})"
            : $"PlayRevealAnimations skipped 持续 {decision.ElapsedMs}ms (dealt={dealt}, spawned={spawned}, collectibles={collectibles}, revealing={revealing})";

        return EmitAnomaly(
            BareAuditCategory.ProtocolRevealSkipped,
            "critical",
            sig,
            Truncate(summary, 500),
            scene,
            appState,
            runState,
            day,
            hour,
            ctx,
            nowMs);
    }

    public bool RecordUnityLog(
        string logType,
        string message,
        string stackTrace,
        string scene,
        string appState,
        string runState,
        int day,
        int hour)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return false;

        var matched = _signalClassifier.ClassifyUnityLogWithContext(logType, message, stackTrace, scene, appState, runState);
        if (matched == null) return false;

        // P3-03（RC-AUDIT-SSE-CANCEL）：主菜单/开局切态 SSE 主动取消触发的底层 Winsock 995 降级记录到 trace 并过滤告警
        if (matched.IsExpectedNoise)
        {
            RecordTrace("network", "ExpectedSseCancel", Truncate(message, 200));
            return false;
        }

        long nowMs = _clockMs();
        long revealElapsedMs = 0L;
        int revealDebouncedCount = 0;

        // P3-02（RC-AUDIT-REVEAL-NOISE）：对 Unity 日志流中的纯多卡异步加载瞬态（dealt=True, spawned=False）同步实施 2.0s 去抖
        if (string.Equals(matched.Category, BareAuditCategory.ProtocolRevealSkipped, StringComparison.Ordinal)
            && BareAuditRevealSkipDebouncer.TryParseRevealSkipFlags(message, out bool dealt, out bool spawned, out bool collectibles, out bool revealing)
            && !spawned)
        {
            var decision = _logRevealDebouncer.Evaluate(
                nowMs,
                hasCardsPendingRemoval: false,
                dealt,
                spawned,
                collectibles,
                revealing,
                hasCardsToReveal: true,
                appState,
                runState);
            if (!decision.ShouldEmit)
            {
                return false;
            }
            revealElapsedMs = decision.ElapsedMs;
            revealDebouncedCount = decision.SuppressedTransientCount;
        }

        var ctx = new Dictionary<string, object>
        {
            ["logType"] = logType ?? string.Empty,
            ["signalId"] = matched.SignalId,
            ["stackTrace"] = Truncate(stackTrace, MaxStackTraceChars),
        };
        if (revealElapsedMs > 0L || revealDebouncedCount > 0)
        {
            ctx["elapsedMs"] = revealElapsedMs;
            ctx["debouncedTransientCount"] = revealDebouncedCount;
        }

        return EmitAnomaly(
            matched.Category,
            matched.Severity,
            matched.Signature,
            Truncate(message, 500),
            scene,
            appState,
            runState,
            day,
            hour,
            ctx,
            nowMs);
    }

    public bool RecordProtocolSignal(
        string category,
        string severity,
        string signalId,
        string summary,
        string scene,
        string appState,
        string runState,
        int day,
        int hour,
        Dictionary<string, object> context = null)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return false;

        string safeCat = string.IsNullOrWhiteSpace(category) ? BareAuditCategory.ProtocolAnomaly : category;
        string safeSig = safeCat + "|" + (signalId ?? "signal") + "|" + (appState ?? "") + "|" + (runState ?? "");
        var ctx = context ?? new Dictionary<string, object>();
        if (!ctx.ContainsKey("signalId")) ctx["signalId"] = signalId ?? string.Empty;

        return EmitAnomaly(
            safeCat,
            string.IsNullOrWhiteSpace(severity) ? "error" : severity,
            safeSig,
            Truncate(summary, 500),
            scene,
            appState,
            runState,
            day,
            hour,
            ctx,
            _clockMs());
    }

    public int InspectGameSimBatch(
        BareAuditGameSimBatchInput batch,
        string scene,
        string appState,
        string runState,
        int day,
        int hour)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0 || batch == null) return 0;

        var violations = BareAuditGameSimInvariantChecker.InspectBatch(batch);
        if (violations.Count == 0) return 0;

        int emitted = 0;
        for (int i = 0; i < violations.Count; i++)
        {
            var v = violations[i];
            if (RecordProtocolSignal(v.Category, v.Severity, v.SignalId, v.Summary, scene, appState, runState, day, hour, v.Context))
            {
                emitted++;
            }
        }
        return emitted;
    }

    public bool RecordCardArtLoadFailure(
        string source,
        string assetPath,
        string reason,
        string cardId,
        string scene,
        string appState,
        string runState,
        int day,
        int hour)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return false;

        var ctx = new Dictionary<string, object>
        {
            ["source"] = source ?? string.Empty,
            ["assetPath"] = assetPath ?? string.Empty,
            ["reason"] = reason ?? string.Empty,
            ["cardId"] = cardId ?? string.Empty,
        };

        string sig = "art_fail|" + (source ?? "") + "|" + (reason ?? "") + "|" + (assetPath ?? cardId ?? "");
        string summary = $"卡图加载失败 ({source}): reason={reason}, card={cardId}, path={assetPath}";
        return EmitAnomaly(
            BareAuditCategory.CardArtLoadFailure,
            "error",
            sig,
            Truncate(summary, 500),
            scene,
            appState,
            runState,
            day,
            hour,
            ctx,
            _clockMs());
    }

    public bool SampleDeadlockState(
        bool isInRun,
        string scene,
        string appState,
        string runState,
        int day,
        int hour,
        bool blockInput,
        bool isWaitingForServer,
        bool simHandlersProcessing,
        bool cmdIsProcessing,
        bool isRevealing,
        bool hasCardsToReveal,
        bool isBoardTransitioning,
        bool isProcessingUpgrade,
        bool dialogOpen)
    {
        return SampleDeadlockState(
            isInRun, scene, appState, runState, day, hour,
            blockInput, isWaitingForServer, simHandlersProcessing, cmdIsProcessing,
            isRevealing, hasCardsToReveal, isBoardTransitioning, isProcessingUpgrade, dialogOpen,
            isUpdatingSkillBoard: false, hour6WaitForNewDayLock: false);
    }

    public bool SampleDeadlockState(
        bool isInRun,
        string scene,
        string appState,
        string runState,
        int day,
        int hour,
        bool blockInput,
        bool isWaitingForServer,
        bool simHandlersProcessing,
        bool cmdIsProcessing,
        bool isRevealing,
        bool hasCardsToReveal,
        bool isBoardTransitioning,
        bool isProcessingUpgrade,
        bool dialogOpen,
        bool isUpdatingSkillBoard,
        bool hour6WaitForNewDayLock)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return false;
        long nowMs = _clockMs();
        var hit = _deadlockDetector.Evaluate(
            nowMs,
            isInRun,
            scene,
            appState,
            runState,
            blockInput,
            isWaitingForServer,
            simHandlersProcessing,
            cmdIsProcessing,
            isRevealing,
            hasCardsToReveal,
            isBoardTransitioning,
            isProcessingUpgrade,
            dialogOpen,
            isUpdatingSkillBoard,
            hour6WaitForNewDayLock);

        if (hit == null) return false;

        var ctx = new Dictionary<string, object>
        {
            ["stalledMs"] = hit.StalledMs,
            ["lockedReasons"] = hit.LockedReasons,
        };

        string summary = $"游戏状态/输入锁死超过 {hit.StalledMs / 1000.0:F1}s: [{string.Join(", ", hit.LockedReasons)}]";
        return EmitAnomaly(
            BareAuditCategory.StateOrInputDeadlock,
            "critical",
            hit.Signature,
            summary,
            hit.Scene,
            hit.AppState,
            hit.RunState,
            day,
            hour,
            ctx,
            nowMs);
    }

    public void ResetRunScopedState()
    {
        _purchaseDetector.Clear();
        _deadlockDetector.Reset();
        _directRevealDebouncer.Reset();
        _logRevealDebouncer.Reset();
    }

    public void Flush(int timeoutMs = 2000)
    {
        if (!_enabled || _writerThread == null) return;
        _wakeWriter.Set();
        _drainedSignal.Wait(Math.Max(50, timeoutMs));
    }

    private bool EmitAnomaly(
        string category,
        string severity,
        string signature,
        string summary,
        string scene,
        string appState,
        string runState,
        int day,
        int hour,
        Dictionary<string, object> context,
        long nowMs)
    {
        var decision = _rateLimiter.TryAcquire(signature, nowMs);
        if (!decision.Allowed) return false;

        var record = new BareAuditRecord
        {
            TimestampUtc = FormatTimestampUtc(nowMs),
            Category = category ?? BareAuditCategory.ProtocolAnomaly,
            Severity = severity ?? "error",
            Signature = Truncate(signature, 200),
            Summary = summary ?? string.Empty,
            Scene = scene ?? string.Empty,
            AppState = appState ?? string.Empty,
            RunState = runState ?? string.Empty,
            Day = day,
            Hour = hour,
            SuppressedCount = decision.SuppressedSinceLastEmit,
            RecentEventChain = _ringBuffer.Snapshot(),
            Context = context ?? new Dictionary<string, object>(),
        };

        string line = JsonConvert.SerializeObject(record, Formatting.None);
        if (Interlocked.Increment(ref _pendingCount) > MaxPendingWriteQueue)
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _droppedByQueueOverflow);
            return false;
        }

        _drainedSignal.Reset();
        _writeQueue.Enqueue(line);
        _wakeWriter.Set();
        return true;
    }

    private void WriterLoop()
    {
        var batch = new List<string>(32);
        while (true)
        {
            batch.Clear();
            while (batch.Count < 64 && _writeQueue.TryDequeue(out string line))
            {
                Interlocked.Decrement(ref _pendingCount);
                batch.Add(line);
            }

            if (batch.Count > 0)
            {
                AppendBatchSafely(batch);
                continue;
            }

            if (_writeQueue.IsEmpty)
            {
                _drainedSignal.Set();
                if (Volatile.Read(ref _disposed) != 0)
                {
                    break;
                }
            }

            _wakeWriter.WaitOne(250);
        }
    }

    private void AppendBatchSafely(List<string> lines)
    {
        try
        {
            string dir = Path.GetDirectoryName(_jsonlFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(_jsonlFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            for (int i = 0; i < lines.Count; i++)
            {
                sw.WriteLine(lines[i]);
            }
            sw.Flush();
            Interlocked.Add(ref _totalWrittenLines, lines.Count);
        }
        catch
        {
            // 严禁在后台写盘线程抛出未捕获异常或再次写 Unity 日志引发递归
        }
    }

    private static string FormatTimestampUtc(long unixMs)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("O", CultureInfo.InvariantCulture);
        }
        catch
        {
            return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }
    }

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= maxLen ? s : s.Substring(0, maxLen);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_writerThread != null)
        {
            _wakeWriter.Set();
            try { _writerThread.Join(1500); } catch { }
        }
        _wakeWriter.Dispose();
        _drainedSignal.Dispose();
    }
}
