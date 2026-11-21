using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarServer.Game;
using Microsoft.Data.Sqlite;

namespace BazaarServer.Data;

/// <summary>
/// 野怪模板（来自 GameData.db 的 monsters 表）。
///
/// 结构（实测）：
///   { "Id": "...", "InternalName": "Flame Juggler", "Version": "5.0.0",
///     "Player": {
///        "Attributes": { "Prestige": 50, "HealthMax": 1200, "Level": 1, ... },
///        "Hand": { "UnlockedSlots": 252,
///                  "Items": [ { "SocketId": "Socket_2", "TemplateId": "24dc340b-...", "Tier": "Legendary", ... } ] },
///        "Skills": [ { "TemplateId": "...", "Tier": "Bronze", ... } ],
///        "Stash": { "Items": [ ... ] },
///        "SocketEffects": [ ... ] } }
/// </summary>
public sealed class MonsterTemplate
{
    public string Id { get; init; }
    public string InternalName { get; init; }
    public int Prestige { get; init; }
    public int HealthMax { get; init; }
    public int Level { get; init; }
    public Dictionary<string, int> Attributes { get; init; } = new();
    public List<MonsterItem> Items { get; init; } = new();
    /// <summary>野怪装备的专属圆形技能卡列表（来自 Player.Skills）。</summary>
    public List<MonsterItem> Skills { get; init; } = new();
    /// <summary>野怪背包物品列表（来自 Player.Stash.Items）。</summary>
    public List<MonsterItem> StashItems { get; init; } = new();
    public List<MonsterItem> SocketEffects { get; init; } = new();

    public sealed class MonsterItem
    {
        public string SocketId { get; init; }
        public string TemplateId { get; init; }
        public string Tier { get; init; }
        public string EnchantmentType { get; init; }
        /// <summary>该 item 在 Tier 下的额外属性覆盖（通常为空，用模板默认值）。</summary>
        public Dictionary<string, int> Attributes { get; init; } = new();
    }

    /// <summary>枚举该野怪可供胜利战利品池抽取的所有候选物品、专属技能与背包物品。</summary>
    public IEnumerable<MonsterItem> AllLootCandidates()
    {
        foreach (var it in Items) yield return it;
        foreach (var sk in Skills) yield return sk;
        foreach (var st in StashItems) yield return st;
    }

    /// <summary>
    /// 估算该野怪在指定等级的实战强度。
    ///
    /// ⚠ `monsters.Player.Attributes.HealthMax` 是**该野怪的终局数值**（例如 5500），
    /// 而 `CombatantType.Level` 才决定它在当前天数的实际缩放。
    /// 直接拿 HealthMax 会导致 Day1 的玩家（战力 20-50）面对 5500 血的怪，必输。
    ///
    /// 所以这里按 Level 缩放：Level 1 取 8%，每升 1 级 +6%，上限 100%。
    /// </summary>
    public int ScaledHealth(int encounterLevel)
    {
        int lvl = encounterLevel <= 0 ? 1 : encounterLevel;
        double factor = Math.Min(1.0, 0.08 + 0.06 * (lvl - 1));
        return Math.Max(30, (int)(HealthMax * factor));
    }

    /// <summary>按等级缩放的战力。</summary>
    public int ScaledPower(CardPool pool, int encounterLevel)
    {
        int lvl = encounterLevel <= 0 ? 1 : encounterLevel;
        double factor = Math.Min(1.0, 0.15 + 0.08 * (lvl - 1));
        return Math.Max(1, (int)(EstimatePower(pool) * factor));
    }

    /// <summary>
    /// 估算该野怪的总输出：把每件装备的 DamageAmount 加起来，乘以品质系数。
    /// 这是简化模型（不做 ICD/冷却），用于自建服的胜负判定。
    /// </summary>
    public int EstimatePower(CardPool pool)
    {
        int total = 0;
        foreach (var it in Items)
        {
            var tpl = pool.Get(it.TemplateId);
            if (tpl == null) continue;

            string tier = it.Tier ?? tpl.StartingTier ?? "Bronze";
            int dmg = 0;
            if (tpl.TierAttributes.TryGetValue(tier, out var attrs) &&
                attrs.TryGetValue("DamageAmount", out int d))
            {
                dmg = d;
            }
            else if (tpl.TierAttributes.TryGetValue(tpl.StartingTier ?? "Bronze", out var baseAttrs) &&
                     baseAttrs.TryGetValue("DamageAmount", out int bd))
            {
                dmg = bd;
            }
            total += dmg;
        }
        return total;
    }
}

public static class MonsterPool
{
    /// <summary>
    /// 判断怪物模板名称是否属于测试/教学/调试/占位模板，供 MonsterPool 与 ShopEngine 统一过滤。
    /// </summary>
    public static bool IsExcludedDevOrTestMonsterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("PvP", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Tutorial", StringComparison.OrdinalIgnoreCase)
            || name.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Training Dummy", StringComparison.OrdinalIgnoreCase)
            || name.Contains("QA Test", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Sparring Partner", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Monster_Battle_Player_Template", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Skill Board Test", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Art Test", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Design Testing", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LOADOUT TEMPLATE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Greenheart", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Test Subject Alpha", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Big Bad Wolf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断独立 MonsterTemplate 是否为可实战野怪（排除 20HP/10000HP 等占位怪与调试名）。
    /// </summary>
    public static bool IsPlayableMonsterTemplate(MonsterTemplate? monster)
    {
        if (monster == null) return false;
        if (IsExcludedDevOrTestMonsterName(monster.InternalName)) return false;
        return monster.HealthMax > 30 && monster.HealthMax < 10000;
    }

    /// <summary>
    /// 从权威仓库读取野怪遭遇的最大生命值（HealthMax）；不存在时返回 0。
    /// </summary>
    public static int GetMonsterHealthMax(CardPool.CardTemplate? enc, GameDataRepository repo)
    {
        if (enc == null || repo == null || string.IsNullOrEmpty(enc.MonsterTemplateId))
            return 0;
        if (repo.MonstersById.TryGetValue(enc.MonsterTemplateId, out var coreMonster) &&
            coreMonster.Player?.Attributes != null &&
            coreMonster.Player.Attributes.TryGetValue("HealthMax", out double hp))
        {
            return (int)Math.Round(hp);
        }
        return 0;
    }

    /// <summary>读取野怪遭遇卡的品阶枚举（Bronze &lt; Silver &lt; Gold &lt; Diamond &lt; Legendary）。</summary>
    public static CardTier GetMonsterTier(CardPool.CardTemplate? enc)
        => TierHelper.ParseTier(enc?.StartingTier);

    /// <summary>
    /// 读取野怪遭遇的有效等级（优先使用卡表 MonsterLevel，其次回退怪物模板自身 Level，默认 1）。
    /// </summary>
    public static int GetEffectiveMonsterLevel(CardPool.CardTemplate? enc, GameDataRepository repo)
    {
        if (enc == null) return 1;
        if (enc.MonsterLevel > 0) return enc.MonsterLevel;
        if (repo != null &&
            !string.IsNullOrEmpty(enc.MonsterTemplateId) &&
            repo.MonstersById.TryGetValue(enc.MonsterTemplateId, out var coreMonster) &&
            coreMonster.Player?.Attributes != null &&
            coreMonster.Player.Attributes.TryGetValue("Level", out double lv) &&
            lv > 0)
        {
            return (int)Math.Round(lv);
        }
        return 1;
    }

    /// <summary>
    /// 判断一张 <c>CombatEncounter</c> 卡牌是否为正式可实战野怪：
    /// 必须存在于 DB、通过资源白名单校验、非测试/教学/占位名、对应真实 MonsterTemplate 且拥有非空 Hand 盘面及合理 HP（31..9999）。
    /// </summary>
    public static bool IsPlayableCombatEncounter(
        CardPool.CardTemplate? card,
        CardPool pool,
        GameDataRepository repo,
        bool requireAlwaysSpawning = true)
    {
        if (card == null || pool == null || repo == null)
            return false;
        if (!string.Equals(card.Type, "CombatEncounter", StringComparison.OrdinalIgnoreCase))
            return false;
        if (requireAlwaysSpawning && !string.Equals(card.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!pool.IsAssetValid(card.Id))
            return false;
        if (string.IsNullOrWhiteSpace(card.MonsterTemplateId) ||
            string.Equals(card.Id, ShopEngine.Hour6PvpCardId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (CardPool.IsExcludedDevOrStoryTemplate(card) || IsExcludedDevOrTestMonsterName(card.InternalName))
            return false;
        if (!repo.MonstersById.TryGetValue(card.MonsterTemplateId, out var coreMonster))
            return false;
        if (IsExcludedDevOrTestMonsterName(coreMonster.InternalName))
            return false;
        if (coreMonster.Player?.Hand?.Items is not { Count: > 0 })
            return false;

        int hp = GetMonsterHealthMax(card, repo);
        return hp > 30 && hp < 10000;
    }

    /// <summary>
    /// 收集全库通过资产与实战校验的 <c>CombatEncounter</c> 池，并按可选 <paramref name="bannedIds"/> 过滤。
    /// </summary>
    public static List<CardPool.CardTemplate> CollectPlayableCombatEncounters(
        CardPool pool,
        GameDataRepository repo,
        ISet<string>? bannedIds = null)
    {
        var all = pool.OfType("CombatEncounter")
            .Where(c => IsPlayableCombatEncounter(c, pool, repo, requireAlwaysSpawning: true))
            .ToList();

        if (bannedIds is { Count: > 0 })
        {
            var unbanned = all.Where(c => !bannedIds.Contains(c.Id)).ToList();
            if (unbanned.Count > 0) return unbanned;
        }
        return all;
    }

    /// <summary>
    /// 当某一天的精选怪表因禁用或精简数据库不足时，按 V10/官方等级与血量梯度从 <see cref="MonsterPool"/> 全池中回退选怪。
    /// </summary>
    public static CardPool.CardTemplate? SelectSlotFallback(
        int day,
        EMonsterSlotTier slot,
        IReadOnlyList<CardPool.CardTemplate> all,
        HashSet<string> used,
        GameDataRepository repo,
        Random rng,
        ISet<string>? visitedTodayIds,
        int minLevel,
        int minHp,
        bool preferStrictlyHigherHp,
        CardTier minTier = CardTier.Bronze)
    {
        CardPool.CardTemplate? Pick(IReadOnlyList<CardPool.CardTemplate> primary, IReadOnlyList<CardPool.CardTemplate>? secondary = null)
        {
            var pool = primary.Where(c => !used.Contains(c.Id)).ToList();
            if (pool.Count == 0 && secondary != null)
                pool = secondary.Where(c => !used.Contains(c.Id)).ToList();
            if (pool.Count == 0) return null;

            if (visitedTodayIds is { Count: > 0 })
            {
                var unvisited = pool.Where(c => !visitedTodayIds.Contains(c.Id)).ToList();
                if (unvisited.Count > 0) pool = unvisited;
            }
            return pool[rng.Next(pool.Count)];
        }

        CardTier desiredSlotTier = slot switch
        {
            EMonsterSlotTier.LeftBronze => CardTier.Bronze,
            EMonsterSlotTier.MidSilver => CardTier.Silver,
            _ => CardTier.Gold,
        };

        List<CardPool.CardTemplate> PreferTierAndMonotonic(IReadOnlyList<CardPool.CardTemplate> source)
        {
            var exactSlot = source
                .Where(c => GetMonsterTier(c) >= minTier
                         && (slot == EMonsterSlotTier.RightGold ? GetMonsterTier(c) >= desiredSlotTier : GetMonsterTier(c) == desiredSlotTier))
                .ToList();
            if (exactSlot.Count > 0) return exactSlot;

            var monotonic = source.Where(c => GetMonsterTier(c) >= minTier).ToList();
            return monotonic.Count > 0 ? monotonic : source.ToList();
        }

        if (day <= 1)
        {
            return slot switch
            {
                EMonsterSlotTier.LeftBronze => Pick(
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) == 1 && GetMonsterHealthMax(c, repo) is >= 75 and <= 150).ToList()),
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) == 1).ToList())),
                EMonsterSlotTier.MidSilver => Pick(
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) == 1 && GetMonsterHealthMax(c, repo) is >= 75 and <= 150 && GetMonsterHealthMax(c, repo) >= minHp).ToList()),
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) is 1 or 2 && GetMonsterHealthMax(c, repo) is >= 100 and <= 200 && GetMonsterHealthMax(c, repo) >= minHp).ToList())),
                _ => Pick(
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) == 2 && GetMonsterHealthMax(c, repo) is >= 200 and <= 300 && GetMonsterHealthMax(c, repo) > minHp).ToList()),
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) == 2 && GetMonsterHealthMax(c, repo) is >= 200 and <= 300).ToList())),
            };
        }

        if (day == 2)
        {
            return slot switch
            {
                EMonsterSlotTier.LeftBronze => Pick(
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) == 2 && GetMonsterHealthMax(c, repo) is >= 100 and <= 200).ToList()),
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) is 1 or 2 && GetMonsterHealthMax(c, repo) <= 200).ToList())),
                EMonsterSlotTier.MidSilver => Pick(
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) is 2 or 3 && GetMonsterHealthMax(c, repo) is >= 200 and <= 300 && GetMonsterHealthMax(c, repo) >= minHp).ToList()),
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) is 2 or 3 && GetMonsterHealthMax(c, repo) is >= 200 and <= 350 && GetMonsterHealthMax(c, repo) >= minHp).ToList())),
                _ => Pick(
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) is 3 or 4 && GetMonsterHealthMax(c, repo) is >= 350 and <= 600 && GetMonsterHealthMax(c, repo) > minHp).ToList()),
                    PreferTierAndMonotonic(all.Where(c => GetEffectiveMonsterLevel(c, repo) is 3 or 4 && GetMonsterHealthMax(c, repo) is >= 350 and <= 600 && GetMonsterHealthMax(c, repo) >= minHp).ToList())),
            };
        }

        int maxAvailLv = all.Count > 0 ? all.Max(c => GetEffectiveMonsterLevel(c, repo)) : day + 1;
        List<CardPool.CardTemplate> GetBucketWithDirection(int targetLv, int dir)
        {
            for (int step = 0; step <= 4; step++)
            {
                int probe = targetLv + dir * step;
                if (probe >= 1 && probe <= maxAvailLv)
                {
                    var found = all.Where(c => GetEffectiveMonsterLevel(c, repo) == probe).ToList();
                    if (found.Count > 0) return found;
                }
                if (step > 0)
                {
                    int opp = targetLv - dir * step;
                    if (opp >= 1 && opp <= maxAvailLv)
                    {
                        var found = all.Where(c => GetEffectiveMonsterLevel(c, repo) == opp).ToList();
                        if (found.Count > 0) return found;
                    }
                }
            }
            return all.OrderBy(c => Math.Abs(GetEffectiveMonsterLevel(c, repo) - targetLv)).ToList();
        }

        var (targetLevel, dir) = slot switch
        {
            EMonsterSlotTier.LeftBronze => (Math.Max(1, day - 1), -1),
            EMonsterSlotTier.MidSilver => (day, -1),
            _ => (day + 1, +1),
        };
        var bucket = GetBucketWithDirection(targetLevel, dir);
        if (day == 3 && slot == EMonsterSlotTier.LeftBronze)
        {
            var filtered = PreferTierAndMonotonic(bucket.Where(c => GetMonsterHealthMax(c, repo) is >= 200 and <= 450).ToList());
            if (filtered.Count > 0)
                return Pick(filtered, PreferTierAndMonotonic(bucket));
        }

        var orderedPref = PreferTierAndMonotonic(
            bucket.Where(c => GetMonsterTier(c) >= minTier
                           && GetEffectiveMonsterLevel(c, repo) >= minLevel
                           && (preferStrictlyHigherHp ? GetMonsterHealthMax(c, repo) > minHp : GetMonsterHealthMax(c, repo) >= minHp)
                           && (day != 3 || slot != EMonsterSlotTier.MidSilver || GetMonsterHealthMax(c, repo) <= 450))
                  .ToList());
        if (orderedPref.Count == 0)
        {
            orderedPref = PreferTierAndMonotonic(
                all.Where(c => !used.Contains(c.Id)
                            && GetMonsterTier(c) >= minTier
                            && GetEffectiveMonsterLevel(c, repo) >= minLevel
                            && (preferStrictlyHigherHp ? GetMonsterHealthMax(c, repo) > minHp : GetMonsterHealthMax(c, repo) >= minHp))
                   .OrderBy(c => Math.Abs(GetEffectiveMonsterLevel(c, repo) - targetLevel))
                   .ThenBy(c => GetMonsterHealthMax(c, repo))
                   .Take(6)
                   .ToList());
        }
        if (orderedPref.Count == 0 && preferStrictlyHigherHp)
        {
            orderedPref = PreferTierAndMonotonic(
                all.Where(c => !used.Contains(c.Id)
                            && GetMonsterTier(c) >= minTier
                            && GetEffectiveMonsterLevel(c, repo) >= minLevel
                            && GetMonsterHealthMax(c, repo) >= minHp)
                   .OrderBy(c => Math.Abs(GetEffectiveMonsterLevel(c, repo) - targetLevel))
                   .ToList());
        }
        return Pick(orderedPref, PreferTierAndMonotonic(bucket));
    }

    /// <summary>若过滤条件过于严苛导致槽位不足，按最接近当前天数补足剩余无重复战斗槽位。</summary>
    public static void FillMissingSlotsNearestDay(
        int day,
        int targetCount,
        List<CardPool.CardTemplate> slots,
        HashSet<string> used,
        IReadOnlyList<CardPool.CardTemplate> all,
        GameDataRepository repo)
    {
        if (slots.Count >= targetCount) return;
        var nearest = all
            .Where(c => !used.Contains(c.Id))
            .OrderBy(c => Math.Abs(GetEffectiveMonsterLevel(c, repo) - day))
            .ThenBy(c => GetMonsterHealthMax(c, repo))
            .ToList();
        foreach (var c in nearest)
        {
            if (slots.Count >= targetCount) break;
            if (used.Add(c.Id)) slots.Add(c);
        }
    }

    /// <summary>
    /// 直接从已单次加载的 <see cref="GameDataRepository"/> 映射全部野怪模板（零 SQLite IO 与零二次 JSON 反序列化）。
    /// </summary>
    public static List<MonsterTemplate> FromRepository(GameDataRepository repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var list = new List<MonsterTemplate>(repo.MonstersById.Count);
        foreach (var core in repo.MonstersById.Values)
        {
            if (core == null || string.IsNullOrWhiteSpace(core.Id)) continue;
            var allPlayerAttrs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (core.Player?.Attributes != null)
            {
                foreach (var kv in core.Player.Attributes)
                    allPlayerAttrs[kv.Key] = (int)Math.Round(kv.Value);
            }

            static List<MonsterTemplate.MonsterItem> MapEntries(IReadOnlyList<MonsterCardEntry>? src)
            {
                var res = new List<MonsterTemplate.MonsterItem>(src?.Count ?? 0);
                if (src == null) return res;
                for (int i = 0; i < src.Count; i++)
                {
                    var it = src[i];
                    if (it == null || string.IsNullOrWhiteSpace(it.TemplateId)) continue;
                    var itemAttrs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (it.Attributes != null)
                    {
                        foreach (var kv in it.Attributes)
                            itemAttrs[kv.Key] = (int)Math.Round(kv.Value);
                    }
                    res.Add(new MonsterTemplate.MonsterItem
                    {
                        SocketId = it.SocketId ?? string.Empty,
                        TemplateId = it.TemplateId,
                        Tier = it.Tier ?? "Bronze",
                        EnchantmentType = it.EnchantmentType ?? string.Empty,
                        Attributes = itemAttrs,
                    });
                }
                return res;
            }

            list.Add(new MonsterTemplate
            {
                Id = core.Id,
                InternalName = core.InternalName ?? string.Empty,
                Prestige = allPlayerAttrs.GetValueOrDefault("Prestige", 20),
                HealthMax = allPlayerAttrs.GetValueOrDefault("HealthMax", 100),
                Level = allPlayerAttrs.GetValueOrDefault("Level", 1),
                Attributes = allPlayerAttrs,
                Items = MapEntries(core.Player?.Hand?.Items),
                Skills = MapEntries(core.Player?.Skills),
                StashItems = MapEntries(core.Player?.Stash?.Items),
                SocketEffects = MapEntries(core.Player?.SocketEffects),
            });
        }
        Console.WriteLine($"[MonsterPool] (shared repo) mapped {list.Count} monsters (withSkills={list.Count(m => m.Skills.Count > 0)})");
        return list;
    }

    public static List<MonsterTemplate> Load(string dbPath)
    {
        var list = new List<MonsterTemplate>();
        if (!File.Exists(dbPath)) return list;

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Data FROM monsters";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string id = r.GetString(0);
            byte[] blob = (byte[])r.GetValue(1);
            var m = Parse(id, System.Text.Encoding.UTF8.GetString(blob));
            if (m != null) list.Add(m);
        }
        Console.WriteLine($"[MonsterPool] loaded {list.Count} monsters (withSkills={list.Count(m => m.Skills.Count > 0)})");
        return list;
    }

    private static MonsterTemplate Parse(string id, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var player = root.TryGetProperty("Player", out var p) ? p : default;

            var attrsEl = player.ValueKind == JsonValueKind.Object &&
                          player.TryGetProperty("Attributes", out var a) ? a : default;

            var allPlayerAttrs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (attrsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in attrsEl.EnumerateObject())
                {
                    if (kv.Value.ValueKind == JsonValueKind.Number && kv.Value.TryGetInt32(out int av))
                        allPlayerAttrs[kv.Name] = av;
                }
            }

            int prestige = GetInt(attrsEl, "Prestige", 20);
            int health = GetInt(attrsEl, "HealthMax", 100);
            int level = GetInt(attrsEl, "Level", 1);

            var items = player.ValueKind == JsonValueKind.Object &&
                        player.TryGetProperty("Hand", out var hand) &&
                        hand.TryGetProperty("Items", out var itemsEl)
                ? ParseCardList(itemsEl)
                : new List<MonsterTemplate.MonsterItem>();

            var stashItems = player.ValueKind == JsonValueKind.Object &&
                             player.TryGetProperty("Stash", out var stash) &&
                             stash.TryGetProperty("Items", out var stashItemsEl)
                ? ParseCardList(stashItemsEl)
                : new List<MonsterTemplate.MonsterItem>();

            var skills = player.ValueKind == JsonValueKind.Object &&
                         player.TryGetProperty("Skills", out var skillsEl)
                ? ParseCardList(skillsEl)
                : new List<MonsterTemplate.MonsterItem>();

            var socketEffects = player.ValueKind == JsonValueKind.Object &&
                                player.TryGetProperty("SocketEffects", out var seEl)
                ? ParseCardList(seEl)
                : new List<MonsterTemplate.MonsterItem>();

            return new MonsterTemplate
            {
                Id = id,
                InternalName = GetStr(root, "InternalName"),
                Prestige = prestige,
                HealthMax = health,
                Level = level,
                Attributes = allPlayerAttrs,
                Items = items,
                Skills = skills,
                StashItems = stashItems,
                SocketEffects = socketEffects,
            };
        }
        catch { return null; }
    }

    private static List<MonsterTemplate.MonsterItem> ParseCardList(JsonElement arr)
    {
        var list = new List<MonsterTemplate.MonsterItem>();
        if (arr.ValueKind != JsonValueKind.Array) return list;

        foreach (var it in arr.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            var itemAttrs = new Dictionary<string, int>();
            if (it.TryGetProperty("Attributes", out var ia) && ia.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in ia.EnumerateObject())
                    if (kv.Value.ValueKind == JsonValueKind.Number && kv.Value.TryGetInt32(out int v))
                        itemAttrs[kv.Name] = v;
            }
            string tid = GetStr(it, "TemplateId");
            if (string.IsNullOrWhiteSpace(tid)) continue;
            list.Add(new MonsterTemplate.MonsterItem
            {
                SocketId = GetStr(it, "SocketId"),
                TemplateId = tid,
                Tier = GetStr(it, "Tier"),
                EnchantmentType = GetStr(it, "EnchantmentType"),
                Attributes = itemAttrs,
            });
        }
        return list;
    }

    private static string GetStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int GetInt(JsonElement el, string name, int fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
}
