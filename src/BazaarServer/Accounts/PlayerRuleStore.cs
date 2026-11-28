using System.Collections.Concurrent;
using System.Text.Json;
using BazaarCore.Models;
using BazaarServer.Mods;
using BazaarServer.Storage;

namespace BazaarServer.Accounts;

/// <summary>
/// 玩家自定义规则内存缓存与 SQLite 持久化存储。
/// 使用 ConcurrentDictionary + 按账号条带锁保证 100-CCU 并发读写安全。
/// </summary>
public sealed class PlayerRuleStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly Persistence _persistence;
    private readonly ServerModHost? _modHost;
    private readonly ConcurrentDictionary<string, CustomRunRuleSet> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object[] _stripeLocks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    public PlayerRuleStore(Persistence persistence, ServerModHost? modHost = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _modHost = modHost;
    }

    public CustomRunRuleSet Get(Guid accountId) => Get(accountId.ToString("D"));

    /// <summary>获取账号归一化后的自定义规则（MonsterLootChooseOne 默认为 true）。</summary>
    public CustomRunRuleSet Get(string accountId)
    {
        string key = NormalizeKey(accountId);
        if (string.IsNullOrEmpty(key))
            return new CustomRunRuleSet();

        if (_cache.TryGetValue(key, out var cached))
            return NormalizeRules(cached);

        lock (GetStripeLock(key))
        {
            if (_cache.TryGetValue(key, out cached))
                return NormalizeRules(cached);

            string json = _persistence.LoadAccountCustomRules(key);
            var loaded = NormalizeRules(ParseOrDefault(json));
            _cache[key] = loaded;
            return NormalizeRules(loaded);
        }
    }

    public CustomRunRuleSet Set(Guid accountId, CustomRunRuleSet? rules)
        => Set(accountId.ToString("D"), rules);

    /// <summary>归一化、缓存并持久化保存账号自定义规则。</summary>
    public CustomRunRuleSet Set(string accountId, CustomRunRuleSet? rules)
    {
        string key = NormalizeKey(accountId);
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("AccountId cannot be empty.", nameof(accountId));

        var normalized = NormalizeRules(rules);
        string json = JsonSerializer.Serialize(normalized, JsonOpts);

        lock (GetStripeLock(key))
        {
            _persistence.SaveAccountCustomRules(key, json);
            _cache[key] = normalized;
        }

        return NormalizeRules(normalized);
    }

    private CustomRunRuleSet NormalizeRules(CustomRunRuleSet? rules)
    {
        var source = rules ?? new CustomRunRuleSet();
        if (_modHost == null)
            return source.CloneNormalized();

        var extra = _modHost.GetAllAvailableMutators(source.SelectedModIds)
            .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        return source.CloneNormalized(extra, preserveUnregisteredMutators: false);
    }

    public CustomRunRuleSet Reset(Guid accountId) => Reset(accountId.ToString("D"));

    /// <summary>重置账号自定义规则为默认标准配置并返回。</summary>
    public CustomRunRuleSet Reset(string accountId)
        => Set(accountId, new CustomRunRuleSet());

    private object GetStripeLock(string key)
    {
        int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(key);
        return _stripeLocks[(hash & int.MaxValue) % _stripeLocks.Length];
    }

    private static string NormalizeKey(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return string.Empty;
        return Guid.TryParse(accountId, out var g) ? g.ToString("D") : accountId.Trim();
    }

    private static CustomRunRuleSet ParseOrDefault(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new CustomRunRuleSet();

        try
        {
            return JsonSerializer.Deserialize<CustomRunRuleSet>(json, JsonOpts) ?? new CustomRunRuleSet();
        }
        catch
        {
            return new CustomRunRuleSet();
        }
    }
}
