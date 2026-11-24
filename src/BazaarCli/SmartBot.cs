using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: InternalsVisibleTo("BazaarCore.Tests")]

namespace BazaarCli;

/// <summary>
/// 高速事件驱动对局客户端与真实构筑智能策略引擎：
///   - 使用 waitIdle=true + includeCompactState=true 将每次操控压缩为单次 HTTP 往返；
///   - 支持二合一升阶优先、自动上阵填满解锁地毯、光环/Core 卡与武器邻接摆放、
///     背包近满淘汰低阶过渡卡、金币充裕（>= 14）主动 Reroll、消耗品与基座处理。
/// </summary>
internal sealed class DriverClient
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    public DriverClient(string baseUrl, HttpClient http)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = http;
    }

    public async Task<(JObject Json, double ElapsedMs)> GetAsync(string path)
    {
        var sw = Stopwatch.StartNew();
        var resp = await _http.GetAsync(_baseUrl + path);
        string body = await resp.Content.ReadAsStringAsync();
        sw.Stop();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {Truncate(body, 300)}");
        return (JObject.Parse(body), sw.Elapsed.TotalMilliseconds);
    }

    public async Task<(JObject Json, double ElapsedMs)> PostAsync(string path, object payload)
    {
        var sw = Stopwatch.StartNew();
        string json = JsonConvert.SerializeObject(payload ?? new { });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(_baseUrl + path, content);
        string body = await resp.Content.ReadAsStringAsync();
        sw.Stop();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {Truncate(body, 300)}");
        return (JObject.Parse(body), sw.Elapsed.TotalMilliseconds);
    }

    public Task<(JObject Json, double ElapsedMs)> InspectCompactAsync(bool ui = true)
        => GetAsync($"/inspect?compact=1&ui={(ui ? 1 : 0)}");

    public Task<(JObject Json, double ElapsedMs)> CombatAuditSinceAsync(int sinceSeq)
        => GetAsync($"/combataudit?since={sinceSeq}");

    public Task<(JObject Json, double ElapsedMs)> DriveWaitAsync(object payload)
        => PostAsync("/drive", payload);

    private static string Truncate(string s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + "…");
}

/// <summary>
/// 物品评分、槽位计算、二合一识别与自动铺场/换位决策辅助器。
/// </summary>
internal static class SmartStrategy
{
    internal enum PositionalHazardKind
    {
        None = 0,
        SelfDisableRight = 1,
        SelfDisableLeft = 2,
        SelfDisableNeighbor = 3,
    }

    private sealed class PositionalHazardIndex
    {
        public HashSet<string> SelfDisableRightIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelfDisableLeftIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelfDisableNeighborIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelfDisableRightNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelfDisableLeftNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelfDisableNeighborNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Lazy<PositionalHazardIndex> HazardIndex = new(LoadPositionalHazardIndex);

    private static PositionalHazardIndex LoadPositionalHazardIndex()
    {
        var index = new PositionalHazardIndex();
        // 内置已知基线模板兜底（即使运行目录外无 cards.json 也可立即避险）
        index.SelfDisableRightIds.Add("f929af85-1186-456a-89df-3474b32b443f"); // Tractor Beam
        index.SelfDisableRightNames.Add("Tractor Beam");
        index.SelfDisableLeftIds.Add("ae22e12a-b6a8-4199-bf1c-82bcbad8f395"); // Peacewrought
        index.SelfDisableLeftIds.Add("ad80a892-eddc-4856-9a62-9e8f305dd922"); // Hydraulic Press
        index.SelfDisableLeftNames.Add("Peacewrought");
        index.SelfDisableLeftNames.Add("Hydraulic Press");
        index.SelfDisableNeighborIds.Add("1654faeb-e324-46d6-a1e8-d4b838f951a6"); // Void Disruptor
        index.SelfDisableNeighborNames.Add("Void Disruptor");

        // D8-P2-07：改为只读检索权威 SQLite GameData.db 的 cards 表，彻底移除已废弃的 cards.json 依赖
        try
        {
            string dbPath = ResolveGameDataDbPath();
            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
            {
                string connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString();
                using var conn = new SqliteConnection(connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Data FROM cards";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    string rawJson = reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(rawJson)) continue;
                    try
                    {
                        var card = JObject.Parse(rawJson);
                        ClassifyCardHazards(card, index);
                    }
                    catch { }
                }
            }
        }
        catch { }

        return index;
    }

    private static string ResolveGameDataDbPath()
    {
        string envDb = Environment.GetEnvironmentVariable("BAZAAR_GAMEDATA_DB");
        if (!string.IsNullOrWhiteSpace(envDb) && File.Exists(envDb)) return envDb;

        string envData = Environment.GetEnvironmentVariable("BAZAAR_DATA");
        if (!string.IsNullOrWhiteSpace(envData))
        {
            string fromData = Path.Combine(envData, "GameData.db");
            if (File.Exists(fromData)) return fromData;
        }

        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            string dir = start;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string c1 = Path.Combine(dir, "dist", "data", "GameData.db");
                if (File.Exists(c1)) return c1;
                string c2 = Path.Combine(dir, "data", "GameData.db");
                if (File.Exists(c2)) return c2;
                string c3 = Path.Combine(dir, "GameData.db");
                if (File.Exists(c3)) return c3;
                dir = Path.GetDirectoryName(dir);
            }
        }

        return null;
    }

    private static void ClassifyCardHazards(JObject card, PositionalHazardIndex index)
    {
        if (card == null) return;
        string cardId = card["Id"]?.ToString();
        if (string.IsNullOrWhiteSpace(cardId)) return;
        string cardName = card["InternalName"]?.ToString();

        void ScanToken(JToken tok)
        {
            if (tok is JObject obj)
            {
                string type = obj["$type"]?.ToString() ?? "";
                if (type is "TActionCardDisable" or "TActionCardDestroy")
                {
                    if (obj["Target"] is JObject tgt)
                    {
                        string tgtType = tgt["$type"]?.ToString() ?? "";
                        string section = tgt["TargetSection"]?.ToString();
                        string origin = tgt["Origin"]?.ToString();
                        string mode = tgt["TargetMode"]?.ToString() ?? "";

                        bool targetsSelfHand = (string.IsNullOrEmpty(section) || string.Equals(section, "SelfHand", StringComparison.OrdinalIgnoreCase))
                                               && (!string.Equals(tgtType, "TTargetCardPositional", StringComparison.OrdinalIgnoreCase)
                                                   || string.IsNullOrEmpty(origin)
                                                   || string.Equals(origin, "Self", StringComparison.OrdinalIgnoreCase));

                        if (targetsSelfHand)
                        {
                            if (mode is "AllRightCards" or "RightCard")
                            {
                                index.SelfDisableRightIds.Add(cardId);
                                if (!string.IsNullOrWhiteSpace(cardName)) index.SelfDisableRightNames.Add(cardName);
                            }
                            else if (mode is "AllLeftCards" or "LeftCard")
                            {
                                index.SelfDisableLeftIds.Add(cardId);
                                if (!string.IsNullOrWhiteSpace(cardName)) index.SelfDisableLeftNames.Add(cardName);
                            }
                            else if (mode == "Neighbor")
                            {
                                index.SelfDisableNeighborIds.Add(cardId);
                                if (!string.IsNullOrWhiteSpace(cardName)) index.SelfDisableNeighborNames.Add(cardName);
                            }
                        }
                    }
                }

                foreach (var prop in obj.Properties())
                    ScanToken(prop.Value);
            }
            else if (tok is JArray arr)
            {
                foreach (var elem in arr)
                    ScanToken(elem);
            }
        }

        if (card["Abilities"] != null) ScanToken(card["Abilities"]);
        if (card["Auras"] != null) ScanToken(card["Auras"]);
    }

    internal static PositionalHazardKind GetPositionalHazardKind(JObject item)
    {
        if (item == null) return PositionalHazardKind.None;
        var idx = HazardIndex.Value;
        string tpl = item["templateId"]?.ToString();
        string name = item["name"]?.ToString() ?? item["internalName"]?.ToString();

        if (!string.IsNullOrWhiteSpace(tpl))
        {
            if (idx.SelfDisableRightIds.Contains(tpl)) return PositionalHazardKind.SelfDisableRight;
            if (idx.SelfDisableLeftIds.Contains(tpl)) return PositionalHazardKind.SelfDisableLeft;
            if (idx.SelfDisableNeighborIds.Contains(tpl)) return PositionalHazardKind.SelfDisableNeighbor;
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (idx.SelfDisableRightNames.Contains(name)) return PositionalHazardKind.SelfDisableRight;
            if (idx.SelfDisableLeftNames.Contains(name)) return PositionalHazardKind.SelfDisableLeft;
            if (idx.SelfDisableNeighborNames.Contains(name)) return PositionalHazardKind.SelfDisableNeighbor;
        }
        return PositionalHazardKind.None;
    }

    /// <summary>严格只保留真实物品（type == "Item"），排除 SocketEffect、Skill 等非物品实体。</summary>
    public static bool IsBoardItem(JObject item)
        => item != null && string.Equals(item["type"]?.ToString(), "Item", StringComparison.OrdinalIgnoreCase);

    public static int SizeWidth(JObject item)
    {
        string sz = item?["size"]?.ToString() ?? "";
        return sz.Equals("Large", StringComparison.OrdinalIgnoreCase) ? 3
            : sz.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? 2
            : 1;
    }

    public static int SocketIndex(JObject item)
    {
        string sock = item?["socket"]?.ToString() ?? "";
        if (sock.StartsWith("Socket_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(sock.AsSpan("Socket_".Length), out int idx))
            return idx;
        return -1;
    }

    public static int TierRank(string tier)
    {
        if (string.IsNullOrEmpty(tier)) return 0;
        if (tier.Equals("Legendary", StringComparison.OrdinalIgnoreCase)) return 5;
        if (tier.Equals("Diamond", StringComparison.OrdinalIgnoreCase)) return 4;
        if (tier.Equals("Gold", StringComparison.OrdinalIgnoreCase)) return 3;
        if (tier.Equals("Silver", StringComparison.OrdinalIgnoreCase)) return 2;
        if (tier.Equals("Bronze", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    public static bool IsUnsellable(JObject item)
    {
        var hidden = item?["hiddenTags"] as JArray;
        return hidden != null && hidden.Any(t => string.Equals(t.ToString(), "Unsellable", StringComparison.OrdinalIgnoreCase));
    }

    public static bool HasTag(JObject item, string tag)
    {
        var tags = item?["tags"] as JArray;
        return tags != null && tags.Any(t => string.Equals(t.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    public static bool HasHiddenTag(JObject item, string tag)
    {
        var hidden = item?["hiddenTags"] as JArray;
        return hidden != null && hidden.Any(t => string.Equals(t.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsUsableConsumable(JObject item)
    {
        if (!IsBoardItem(item) || IsUnsellable(item)) return false;
        if (HasTag(item, "Loot")) return true;

        string name = item["name"]?.ToString() ?? "";
        return name.Contains("Chocolate", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gumball", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Catalyst", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Spare Change", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Truffle", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Extract", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCoreOrAdjacentBuffer(JObject item)
    {
        if (item == null || IsUsableConsumable(item)) return false;
        if (HasTag(item, "Core")) return true;

        string name = item["name"]?.ToString() ?? "";
        if (name.Contains("Core", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Buckle", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Silencer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Shadowed", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Figurehead", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Wheel", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Sextant", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Astrolabe", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Star Chart", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Holsters", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Telescope", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Spyglass", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Metronome", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Battery", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Workbench", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Cloak", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Nest", StringComparison.OrdinalIgnoreCase))
            return true;

        int cd = item["cooldownMax"]?.Value<int>() ?? 0;
        int dmg = item["damageAmount"]?.Value<int>() ?? 0;
        int sh = item["shieldApplyAmount"]?.Value<int>() ?? 0;
        int brn = item["burnApplyAmount"]?.Value<int>() ?? 0;
        int psn = item["poisonApplyAmount"]?.Value<int>() ?? 0;
        // 无冷却且非纯数值卡的被动/光环/道具优先摆放在输出卡旁测光环联动
        return cd == 0 && dmg == 0 && sh == 0 && brn == 0 && psn == 0 &&
               (HasTag(item, "Tool") || HasTag(item, "Apparel") || HasTag(item, "Property") || HasTag(item, "Tech"));
    }

    public static bool IsPrimaryCombatOutput(JObject item)
    {
        if (item == null || IsUsableConsumable(item)) return false;
        int dmg = item["damageAmount"]?.Value<int>() ?? 0;
        int brn = item["burnApplyAmount"]?.Value<int>() ?? 0;
        int psn = item["poisonApplyAmount"]?.Value<int>() ?? 0;
        return HasTag(item, "Weapon") || dmg > 0 || brn > 0 || psn > 0;
    }

    public static double ScoreCombatPower(JObject item, JArray currentBoard = null)
    {
        if (item == null) return 0;
        if (IsUsableConsumable(item)) return 1.0;

        int tier = TierRank(item["tier"]?.ToString());
        double score = tier switch
        {
            5 => 160,
            4 => 115,
            3 => 58,
            2 => 28,
            1 => 12,
            _ => 8,
        };

        int dmg = item["damageAmount"]?.Value<int>() ?? 0;
        int sh = item["shieldApplyAmount"]?.Value<int>() ?? 0;
        int brn = item["burnApplyAmount"]?.Value<int>() ?? 0;
        int psn = item["poisonApplyAmount"]?.Value<int>() ?? 0;
        int heal = item["healAmount"]?.Value<int>() ?? 0;
        int regen = item["regenApplyAmount"]?.Value<int>() ?? 0;
        int crit = item["critChance"]?.Value<int>() ?? 0;
        int multi = item["multicast"]?.Value<int>() ?? 0;
        int life = item["lifesteal"]?.Value<int>() ?? 0;
        int cd = item["cooldownMax"]?.Value<int>() ?? 0;
        string ench = item["enchantment"]?.ToString();

        score += dmg * 1.15 + sh * 0.95 + brn * 3.4 + psn * 3.8 + heal * 0.85 + regen * 2.4;
        score += crit * 0.45 + (multi > 1 ? 26.0 * (multi - 1) : 0) + (life > 0 ? 18 : 0);
        if (!string.IsNullOrEmpty(ench)) score += 28.0;
        if (cd > 0)
        {
            score += cd <= 4000 ? 18 : (cd <= 6500 ? 11 : 6);
        }
        else if (IsCoreOrAdjacentBuffer(item))
        {
            score += 36.0;
        }

        // 与当前场上流派标签联动加成
        if (currentBoard != null && currentBoard.Count > 0)
        {
            foreach (string synTag in new[] { "Weapon", "Aquatic", "Burn", "Poison", "Shield", "Core", "Friend", "Property", "Potion", "Flying" })
            {
                if (HasTag(item, synTag) && currentBoard.OfType<JObject>().Where(IsBoardItem).Any(b => HasTag(b, synTag)))
                {
                    score += 8.0;
                }
            }
        }

        return score;
    }

    public static bool CanFuseWithOwned(JObject candidate, IEnumerable<JObject> owned)
    {
        if (!IsBoardItem(candidate) || owned == null) return false;
        string tpl = candidate["templateId"]?.ToString();
        string tier = candidate["tier"]?.ToString();
        if (string.IsNullOrEmpty(tpl) || TierRank(tier) >= 4) return false;

        return owned.Where(IsBoardItem).Any(o =>
            string.Equals(o["templateId"]?.ToString(), tpl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o["tier"]?.ToString(), tier, StringComparison.OrdinalIgnoreCase));
    }

    public static int GetUnlockedMask(JObject state)
    {
        int raw = state?["unlockedSlots"]?.Value<int>() ?? 120;
        return raw > 0 ? raw : 120;
    }

    public static List<int> GetUnlockedSocketIndices(int mask)
    {
        var list = new List<int>(10);
        for (int s = 0; s < 10; s++)
        {
            if ((mask & (1 << s)) != 0) list.Add(s);
        }
        return list;
    }

    public static int CountFreeSlots(JArray items, int mask)
    {
        bool[] occ = new bool[10];
        if (items != null)
        {
            foreach (var it in items.OfType<JObject>().Where(IsBoardItem))
            {
                int left = SocketIndex(it);
                int w = SizeWidth(it);
                if (left >= 0)
                {
                    for (int k = 0; k < w && left + k < 10; k++) occ[left + k] = true;
                }
            }
        }
        int free = 0;
        for (int s = 0; s < 10; s++)
        {
            if ((mask & (1 << s)) != 0 && !occ[s]) free++;
        }
        return free;
    }

    public static int MaxContiguousFreeSpan(JArray items, int mask)
    {
        bool[] occ = new bool[10];
        if (items != null)
        {
            foreach (var it in items.OfType<JObject>().Where(IsBoardItem))
            {
                int left = SocketIndex(it);
                int w = SizeWidth(it);
                if (left >= 0)
                {
                    for (int k = 0; k < w && left + k < 10; k++) occ[left + k] = true;
                }
            }
        }
        int best = 0, cur = 0;
        for (int s = 0; s < 10; s++)
        {
            if ((mask & (1 << s)) != 0 && !occ[s]) { cur++; if (cur > best) best = cur; }
            else cur = 0;
        }
        return best;
    }

    public static string BuildLayoutSignature(JObject state)
    {
        int mask = GetUnlockedMask(state);
        var hand = (state["hand"] as JArray ?? new JArray()).OfType<JObject>()
            .Where(IsBoardItem)
            .Select(x => $"{x["instanceId"]}@{x["socket"]}:{x["tier"]}");
        var stash = (state["stash"] as JArray ?? new JArray()).OfType<JObject>()
            .Where(IsBoardItem)
            .Select(x => $"{x["instanceId"]}@{x["socket"]}:{x["tier"]}");
        return $"{mask}|H:{string.Join(",", hand)}|S:{string.Join(",", stash)}";
    }

    /// <summary>
    /// 评估整套摆盘站位（从左至右排列的已选物品列表）的综合战力：
    ///   - 对光环/Core 卡紧邻主力输出卡给与邻接加成；
    ///   - 对含 AllRightCards/RightCard 自禁用或自毁的卡若右侧仍有友方物品，施加重罚；
    ///   - 对含 AllLeftCards/LeftCard 自禁用或自毁的卡若左侧仍有友方物品，施加重罚；
    ///   - 对含 Neighbor 自禁用/自毁的卡惩罚其与主动 CD/输出卡紧邻。
    /// </summary>
    public static double ScoreFormation(IReadOnlyList<JObject> ordered)
    {
        if (ordered == null || ordered.Count == 0) return 0;

        double total = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var cur = ordered[i];
            total += ScoreCombatPower(cur);

            if (IsCoreOrAdjacentBuffer(cur))
            {
                bool hasAdjOutput = (i > 0 && IsPrimaryCombatOutput(ordered[i - 1]))
                                    || (i + 1 < ordered.Count && IsPrimaryCombatOutput(ordered[i + 1]));
                if (hasAdjOutput) total += 40.0;
            }

            var hazard = GetPositionalHazardKind(cur);
            if (hazard == PositionalHazardKind.SelfDisableRight)
            {
                for (int j = i + 1; j < ordered.Count; j++)
                {
                    bool active = (ordered[j]["cooldownMax"]?.Value<int>() ?? 0) > 0 || IsPrimaryCombatOutput(ordered[j]);
                    total -= active ? 20000.0 : 10000.0;
                }
            }
            else if (hazard == PositionalHazardKind.SelfDisableLeft)
            {
                for (int j = 0; j < i; j++)
                {
                    bool active = (ordered[j]["cooldownMax"]?.Value<int>() ?? 0) > 0 || IsPrimaryCombatOutput(ordered[j]);
                    total -= active ? 20000.0 : 10000.0;
                }
            }
            else if (hazard == PositionalHazardKind.SelfDisableNeighbor)
            {
                if (i > 0)
                {
                    bool active = (ordered[i - 1]["cooldownMax"]?.Value<int>() ?? 0) > 0 || IsPrimaryCombatOutput(ordered[i - 1]);
                    total -= active ? 3000.0 : 500.0;
                }
                if (i + 1 < ordered.Count)
                {
                    bool active = (ordered[i + 1]["cooldownMax"]?.Value<int>() ?? 0) > 0 || IsPrimaryCombatOutput(ordered[i + 1]);
                    total -= active ? 3000.0 : 500.0;
                }
            }
        }
        return total;
    }

    /// <summary>
    /// 选出最优上阵组合并调整邻接与避险站位：
    ///   1. 用背包高战力非消耗品填满解锁地毯槽位（优先上阵或替换手牌中的消耗品/低分过渡卡）；
    ///   2. 将 Core / 光环 / 相邻触发卡紧邻主力武器或主力主动输出卡摆放，实测光环与充能联动；
    ///   3. 避开友方自禁用/自毁位置副作用：AllRightCards/RightCard 排最右侧，AllLeftCards/LeftCard 排最左侧，Neighbor 靠边缘隔离。
    /// </summary>
    public static List<(string InstanceId, string Section, string Socket, string Reason)> PlanOptimalBoardMoves(JObject state)
    {
        var plan = new List<(string, string, string, string)>();
        var handArr = (state["hand"] as JArray ?? new JArray()).OfType<JObject>().Where(IsBoardItem).ToList();
        var stashArr = (state["stash"] as JArray ?? new JArray()).OfType<JObject>().Where(IsBoardItem).ToList();
        if (handArr.Count == 0 && stashArr.Count == 0) return plan;

        int mask = GetUnlockedMask(state);
        var unlocked = GetUnlockedSocketIndices(mask);
        if (unlocked.Count == 0) return plan;

        // 解锁区在标准对局中是居中的连续区间 [minU .. maxU]
        int minU = unlocked.Min();
        int maxU = unlocked.Max();
        int cap = maxU - minU + 1;

        var allOwned = handArr.Concat(stashArr).ToList();
        var combatCandidates = allOwned
            .Where(c => !IsUsableConsumable(c))
            .OrderByDescending(c => ScoreCombatPower(c, state["hand"] as JArray) / Math.Max(1, SizeWidth(c)))
            .ThenByDescending(c => ScoreCombatPower(c, state["hand"] as JArray))
            .ToList();

        // 贪心选出总宽 <= cap 且分数最高的场上组合（兼顾尽量填满格位）
        var chosen = new List<JObject>();
        int used = 0;
        foreach (var c in combatCandidates)
        {
            int w = SizeWidth(c);
            if (used + w <= cap)
            {
                chosen.Add(c);
                used += w;
            }
        }

        var chosenIds = new HashSet<string>(chosen.Select(c => c["instanceId"]!.ToString()), StringComparer.Ordinal);

        // 先判断是否已有“背包高战力卡应上阵”或“当前场上存在消耗品/被淘汰低分卡应回包”
        bool needsPromoteFromStash = chosen.Any(c => string.Equals(c["section"]?.ToString(), "Stash", StringComparison.OrdinalIgnoreCase));
        bool needsDemoteFromHand = handArr.Any(c => !chosenIds.Contains(c["instanceId"]!.ToString()));

        // 分离左/右/邻接自禁用避险卡与常规卡
        var leftHazards = chosen.Where(c => GetPositionalHazardKind(c) == PositionalHazardKind.SelfDisableLeft)
            .OrderByDescending(c => ScoreCombatPower(c)).ToList();
        var rightHazards = chosen.Where(c => GetPositionalHazardKind(c) == PositionalHazardKind.SelfDisableRight)
            .OrderByDescending(c => ScoreCombatPower(c)).ToList();
        var neighborHazards = chosen.Where(c => GetPositionalHazardKind(c) == PositionalHazardKind.SelfDisableNeighbor)
            .OrderByDescending(c => ScoreCombatPower(c)).ToList();
        var normalChosen = chosen.Where(c => GetPositionalHazardKind(c) == PositionalHazardKind.None).ToList();

        // 对常规卡按 [输出1, 光环/Core1, 输出2, 光环/Core2, 其余...] 相邻交替紧贴
        var outputs = normalChosen.Where(IsPrimaryCombatOutput).OrderByDescending(c => ScoreCombatPower(c)).ToList();
        var buffers = normalChosen.Where(c => !IsPrimaryCombatOutput(c) && IsCoreOrAdjacentBuffer(c))
            .OrderByDescending(c => ScoreCombatPower(c)).ToList();
        var others = normalChosen.Where(c => !IsPrimaryCombatOutput(c) && !IsCoreOrAdjacentBuffer(c))
            .OrderByDescending(c => ScoreCombatPower(c)).ToList();

        var normalOrdered = new List<JObject>(normalChosen.Count);
        int oi = 0, bi = 0;
        while (oi < outputs.Count || bi < buffers.Count)
        {
            if (oi < outputs.Count) normalOrdered.Add(outputs[oi++]);
            if (bi < buffers.Count) normalOrdered.Add(buffers[bi++]);
        }
        normalOrdered.AddRange(others);

        // 在左右避险前提下组装最佳从左至右阵型，并对比邻接避险顺序
        var candidateOrder = new List<JObject>(chosen.Count);
        candidateOrder.AddRange(leftHazards);
        candidateOrder.AddRange(normalOrdered);
        candidateOrder.AddRange(neighborHazards);
        candidateOrder.AddRange(rightHazards);

        if (neighborHazards.Count > 0 && leftHazards.Count == 0 && normalOrdered.Count > 0)
        {
            var altOrder = new List<JObject>(chosen.Count);
            altOrder.AddRange(neighborHazards);
            altOrder.AddRange(normalOrdered);
            altOrder.AddRange(rightHazards);
            if (ScoreFormation(altOrder) > ScoreFormation(candidateOrder))
                candidateOrder = altOrder;
        }

        // 检查 Core/光环卡是否已紧邻至少一张主力输出卡
        var buffersOnBoard = normalChosen.Where(IsCoreOrAdjacentBuffer).ToList();
        var outputsOnBoard = normalChosen.Where(IsPrimaryCombatOutput).ToList();
        bool needsAdjacencyReorder = false;
        if (!needsPromoteFromStash && !needsDemoteFromHand && buffersOnBoard.Count > 0 && outputsOnBoard.Count > 0)
        {
            foreach (var buf in buffersOnBoard)
            {
                int bL = SocketIndex(buf);
                int bR = bL + SizeWidth(buf) - 1;
                bool hasAdjacentOutput = outputsOnBoard.Any(o =>
                {
                    int oL = SocketIndex(o);
                    int oR = oL + SizeWidth(o) - 1;
                    return oL == bR + 1 || oR + 1 == bL;
                });
                if (!hasAdjacentOutput)
                {
                    needsAdjacencyReorder = true;
                    break;
                }
            }
        }

        // 检查当前盘面是否触发自禁用/自毁位置风险
        bool needsHazardReorder = false;
        if (!needsPromoteFromStash && !needsDemoteFromHand && (leftHazards.Count > 0 || rightHazards.Count > 0 || neighborHazards.Count > 0))
        {
            var currentBoardOrdered = handArr.Where(c => chosenIds.Contains(c["instanceId"]!.ToString()))
                .OrderBy(SocketIndex)
                .ToList();

            foreach (var rh in rightHazards)
            {
                int rSock = SocketIndex(rh);
                if (currentBoardOrdered.Any(o => !ReferenceEquals(o, rh) && SocketIndex(o) > rSock))
                {
                    needsHazardReorder = true;
                    break;
                }
            }
            if (!needsHazardReorder)
            {
                foreach (var lh in leftHazards)
                {
                    int lSock = SocketIndex(lh);
                    if (currentBoardOrdered.Any(o => !ReferenceEquals(o, lh) && SocketIndex(o) < lSock))
                    {
                        needsHazardReorder = true;
                        break;
                    }
                }
            }
            if (!needsHazardReorder && neighborHazards.Count > 0 &&
                ScoreFormation(candidateOrder) > ScoreFormation(currentBoardOrdered) + 100.0)
            {
                needsHazardReorder = true;
            }
        }

        if (!needsPromoteFromStash && !needsDemoteFromHand && !needsAdjacencyReorder && !needsHazardReorder)
        {
            return plan;
        }

        // 先把不应留在 Hand 的卡移至 Stash（若有 Stash 空位），避免 Hand 满挤位失败
        int freeStash = CountFreeSlots(state["stash"] as JArray, 1023);
        foreach (var h in handArr)
        {
            string hid = h["instanceId"]!.ToString();
            if (chosenIds.Contains(hid)) continue;
            if (freeStash >= SizeWidth(h))
            {
                plan.Add((hid, "Stash", "auto", "stash_weaker"));
                freeStash -= SizeWidth(h);
            }
        }

        // 计算每张卡的目标槽位：
        // - SelfDisableRight 显式放在最右侧可用槽位（从 maxU 往左分配）；
        // - SelfDisableLeft 显式放在最左侧可用槽位（从 minU 往右分配）；
        // - SelfDisableNeighbor 靠右侧空隙或边缘隔离；
        // - 常规卡从左游标紧贴排布。
        var targetSlots = new List<(JObject Item, int Slot, string Reason, int Priority)>(candidateOrder.Count);
        int leftCursor = minU;
        int rightCursor = maxU;

        foreach (var c in leftHazards)
        {
            int w = SizeWidth(c);
            if (leftCursor + w - 1 > rightCursor) break;
            targetSlots.Add((c, leftCursor, "avoid_self_disable", 0));
            leftCursor += w;
        }

        // rightHazards 最危险/高价值放最右侧（reverse 确保排序靠后的占 maxU）
        for (int i = rightHazards.Count - 1; i >= 0; i--)
        {
            var c = rightHazards[i];
            int w = SizeWidth(c);
            int slot = rightCursor - w + 1;
            if (slot < leftCursor) break;
            targetSlots.Add((c, slot, "avoid_self_disable", 0));
            rightCursor = slot - 1;
        }

        bool neighborLeftFirst = neighborHazards.Count > 0 && candidateOrder.IndexOf(neighborHazards[0]) == 0;
        if (neighborLeftFirst)
        {
            foreach (var c in neighborHazards)
            {
                int w = SizeWidth(c);
                if (leftCursor + w - 1 > rightCursor) break;
                targetSlots.Add((c, leftCursor, "avoid_neighbor_disable", 1));
                leftCursor += w;
            }
        }
        else
        {
            for (int i = neighborHazards.Count - 1; i >= 0; i--)
            {
                var c = neighborHazards[i];
                int w = SizeWidth(c);
                int slot = rightCursor - w + 1;
                if (slot < leftCursor) break;
                targetSlots.Add((c, slot, "avoid_neighbor_disable", 1));
                rightCursor = slot - 1;
            }
        }

        foreach (var c in normalOrdered)
        {
            int w = SizeWidth(c);
            if (leftCursor + w - 1 > rightCursor) break;
            string reason = IsCoreOrAdjacentBuffer(c) ? "align_aura_adj" : "equip_best";
            targetSlots.Add((c, leftCursor, reason, 2));
            leftCursor += w;
        }

        foreach (var (c, targetSlot, reason, _) in targetSlots.OrderBy(t => t.Priority).ThenBy(t => t.Slot))
        {
            string cid = c["instanceId"]!.ToString();
            string curSec = c["section"]?.ToString() ?? "";
            int curSock = SocketIndex(c);
            if (!string.Equals(curSec, "Hand", StringComparison.OrdinalIgnoreCase) || curSock != targetSlot)
            {
                plan.Add((cid, "Hand", "Socket_" + targetSlot, reason));
            }
        }

        return plan;
    }

    public static JObject FindWeakestSellableInStash(JObject state, IEnumerable<JObject> shopCandidates)
    {
        var stash = (state["stash"] as JArray ?? new JArray()).OfType<JObject>().Where(IsBoardItem).ToList();
        if (stash.Count == 0) return null;

        var shopPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (shopCandidates != null)
        {
            foreach (var sc in shopCandidates.Where(IsBoardItem))
            {
                string t = sc["templateId"]?.ToString();
                string r = sc["tier"]?.ToString();
                if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(r)) shopPairs.Add($"{t}|{r}");
            }
        }

        return stash
            .Where(c => !IsUnsellable(c))
            .OrderBy(c => shopPairs.Contains($"{c["templateId"]}|{c["tier"]}") ? 1000 : 0)
            .ThenBy(c => TierRank(c["tier"]?.ToString()))
            .ThenBy(c => ScoreCombatPower(c))
            .FirstOrDefault();
    }

    /// <summary>
    /// 为 Pedestal（升级/附魔基座）过滤并按战力优先排序候选物品：
    /// 排除非 Item（如 SocketEffect）、不可选标记物品、对附魔基座已附魔的物品、
    /// 对升级基座已满 Diamond 阶或不符合允许品阶的物品，避免重复发送无效 commitPedestal。
    /// </summary>
    public static List<JObject> SelectPedestalCandidates(JObject state, ISet<string> triedInState = null)
    {
        if (state == null) return new List<JObject>();

        var ped = state["pedestal"] as JObject;
        string pedKind = ped?["kind"]?.ToString() ?? "unknown";

        HashSet<string> validIds = null;
        if (ped?["validInstanceIds"] is JArray validArr)
        {
            validIds = new HashSet<string>(
                validArr.Select(x => x?.ToString()).Where(s => !string.IsNullOrEmpty(s))!,
                StringComparer.Ordinal);
        }

        HashSet<string> allowedTiers = null;
        if (ped?["allowedTiers"] is JArray tierArr && tierArr.Count > 0)
        {
            allowedTiers = new HashSet<string>(
                tierArr.Select(x => x?.ToString()).Where(s => !string.IsNullOrEmpty(s))!,
                StringComparer.OrdinalIgnoreCase);
        }

        bool IsEligible(JObject item)
        {
            if (!IsBoardItem(item)) return false;
            string iid = item["instanceId"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(iid)) return false;
            if (triedInState != null && triedInState.Contains("ped:" + iid)) return false;

            if (item["pedestalEligible"]?.Value<bool>() == false ||
                item["canSelect"]?.Value<bool>() == false ||
                item["isSelectable"]?.Value<bool>() == false ||
                HasTag(item, "Unselectable") ||
                HasHiddenTag(item, "Unselectable") ||
                HasHiddenTag(item, "CannotUpgrade") ||
                HasHiddenTag(item, "PedestalDisabled"))
            {
                return false;
            }

            if (validIds != null && !validIds.Contains(iid))
            {
                return false;
            }

            string tier = item["tier"]?.ToString() ?? "";
            int rank = TierRank(tier);
            if (allowedTiers != null && !allowedTiers.Contains(tier))
            {
                return false;
            }

            bool hasEnchant = !string.IsNullOrEmpty(item["enchantment"]?.ToString());
            if (string.Equals(pedKind, "upgrade", StringComparison.OrdinalIgnoreCase))
            {
                if (rank >= 4) return false;
            }
            else if (string.Equals(pedKind, "enchant", StringComparison.OrdinalIgnoreCase))
            {
                if (hasEnchant) return false;
            }
            else if (string.Equals(pedKind, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                if (rank >= 4 && hasEnchant) return false;
            }

            return true;
        }

        var hand = (state["hand"] as JArray ?? new JArray()).OfType<JObject>()
            .Where(IsEligible)
            .OrderByDescending(c => ScoreCombatPower(c));
        var stash = (state["stash"] as JArray ?? new JArray()).OfType<JObject>()
            .Where(IsEligible)
            .OrderByDescending(c => ScoreCombatPower(c));

        return hand.Concat(stash).ToList();
    }

    public static bool IsChoiceState(JObject state)
        => IsChoiceState(state?["appState"]?.ToString(), state?["runState"]?.ToString());

    public static bool IsChoiceState(string appState, string runState)
        => string.Equals(runState, "Choice", StringComparison.OrdinalIgnoreCase)
           || string.Equals(appState, "ChoiceState", StringComparison.OrdinalIgnoreCase);

    public static bool IsPedestalState(JObject state)
        => IsPedestalState(state?["appState"]?.ToString(), state?["runState"]?.ToString());

    public static bool IsPedestalState(string appState, string runState)
        => string.Equals(runState, "Pedestal", StringComparison.OrdinalIgnoreCase)
           || string.Equals(appState, "PedestalState", StringComparison.OrdinalIgnoreCase);

    public static bool IsTerminalState(JObject state)
    {
        string runState = state?["runState"]?.ToString() ?? "";
        string appState = state?["appState"]?.ToString() ?? "";
        return runState is "EndRunVictory" or "EndRunDefeat" or "Shutdown"
               || appState is "EndRunVictoryState" or "EndRunDefeatState";
    }

    public static bool AreStatesAligned(JObject state)
    {
        string appState = state?["appState"]?.ToString() ?? "";
        string runState = state?["runState"]?.ToString() ?? "";
        return (appState == "ChoiceState" && runState == "Choice")
               || (appState == "EncounterState" && runState == "Encounter")
               || (appState == "PedestalState" && runState == "Pedestal")
               || (appState == "LootState" && runState == "Loot")
               || (appState == "LevelUpState" && runState == "LevelUp");
    }

    /// <summary>
    /// 严禁在 ChoiceState、战斗态或 CanExit=false 状态发送 /exit；
    /// 当 selectionRules.canExit 缺失时，绝不对 ChoiceState 默认回退为 true。
    /// </summary>
    public static bool CanSendExitInState(JObject state)
    {
        if (state == null) return false;
        string appState = state["appState"]?.ToString() ?? "";
        string runState = state["runState"]?.ToString() ?? "";

        if (IsChoiceState(appState, runState)) return false;
        if (runState is "Combat" or "PVPCombat" || appState is "CombatState" or "PVPCombatState" or "ReplayState")
            return false;
        if (IsTerminalState(state)) return false;
        if (!AreStatesAligned(state)) return false;

        bool? explicitCanExit = state["selectionRules"]?["canExit"]?.Value<bool?>();
        if (explicitCanExit.HasValue) return explicitCanExit.Value;

        return runState is "Encounter" or "Loot" or "LevelUp" or "Pedestal";
    }

    public static string BuildPedestalVisitKey(JObject state)
        => $"{state?["day"]}|{state?["hour"]}|{state?["currentEncounterId"]}";
}

/// <summary>
/// 客户端状态机安全动作执行器：确保 Pedestal 单次提交即离开、ChoiceState 绝不兜底发 /exit。
/// </summary>
internal static class SmartBotActions
{
    public static async Task<(bool Handled, JObject NextState)> HandlePedestalStateAsync(
        DriverClient client,
        JObject state,
        ISet<string> triedInState,
        ISet<string> committedPedestalKeys,
        Action<double, JObject> recordTiming,
        Action<string, string, JObject, JObject, JObject> auditTransition,
        Action<JObject> samplePerf)
    {
        if (state == null) return (false, null);

        string appStateNow = state["appState"]?.ToString() ?? "";
        string runStateNow = state["runState"]?.ToString() ?? "";
        if (!SmartStrategy.IsPedestalState(appStateNow, runStateNow))
            return (false, state);

        string pedKey = SmartStrategy.BuildPedestalVisitKey(state);
        if (!SmartStrategy.AreStatesAligned(state))
        {
            var aligned = await WaitUntilPedestalAlignedOrSettledAsync(client, state, maxWaitMs: 1200, samplePerf: samplePerf);
            return (true, aligned);
        }

        if (!string.Equals(runStateNow, "Pedestal", StringComparison.OrdinalIgnoreCase) ||
            committedPedestalKeys.Contains(pedKey))
        {
            var settled = await WaitUntilLeftPedestalAsync(client, state, maxWaitMs: 2000, samplePerf: samplePerf);
            return (true, settled);
        }

        var ownedCandidates = SmartStrategy.SelectPedestalCandidates(state, triedInState);
        if (ownedCandidates.Count > 0)
        {
            var owned = ownedCandidates[0];
            string pid = owned["instanceId"]!.ToString();
            triedInState.Add("ped:" + pid);

            var (pr, pMs) = await client.DriveWaitAsync(new
            {
                op = "commitPedestal",
                instanceId = pid,
                waitIdle = true,
                includeCompactState = true,
                includeUi = true
            });
            recordTiming?.Invoke(pMs, pr);
            Console.Write("P");

            var nextD = pr?["compactState"] as JObject;
            if (nextD != null)
            {
                samplePerf?.Invoke(nextD);
            }

            if (pr?["ok"]?.Value<bool>() == true)
            {
                string nextRun = nextD?["runState"]?.ToString() ?? "";
                string nextApp = nextD?["appState"]?.ToString() ?? "";

                if (!string.IsNullOrEmpty(nextRun) &&
                    !string.Equals(nextRun, "Pedestal", StringComparison.OrdinalIgnoreCase))
                {
                    committedPedestalKeys.Add(pedKey);
                    auditTransition?.Invoke("pedestal", pid, state, nextD, pr);
                    if (string.Equals(nextApp, "PedestalState", StringComparison.OrdinalIgnoreCase))
                    {
                        nextD = await WaitUntilLeftPedestalAsync(client, nextD, maxWaitMs: 2000, samplePerf: samplePerf);
                    }
                    return (true, nextD);
                }

                // 客户端 PedestalState 动画可能尚未切状态，轻量等待其离开 Pedestal 后立即 return，绝不连发
                var settled = await WaitUntilLeftPedestalAsync(client, nextD ?? state, maxWaitMs: 1800, samplePerf: samplePerf);
                if (settled != null &&
                    !string.Equals(settled["runState"]?.ToString(), "Pedestal", StringComparison.OrdinalIgnoreCase))
                {
                    committedPedestalKeys.Add(pedKey);
                    auditTransition?.Invoke("pedestal", pid, state, settled, pr);
                }
                return (true, settled ?? nextD);
            }

            return (true, nextD);
        }

        if (string.Equals(runStateNow, "Pedestal", StringComparison.OrdinalIgnoreCase))
        {
            var (xr, xMs) = await client.DriveWaitAsync(new
            {
                op = "exit",
                waitIdle = true,
                includeCompactState = true,
                includeUi = true
            });
            recordTiming?.Invoke(xMs, xr);
            Console.Write(">");
            return (true, xr?["compactState"] as JObject);
        }

        return (true, null);
    }

    private static async Task<JObject> WaitUntilPedestalAlignedOrSettledAsync(
        DriverClient client,
        JObject currentState,
        int maxWaitMs,
        Action<JObject> samplePerf)
    {
        var sw = Stopwatch.StartNew();
        JObject latest = currentState;
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            string app = latest?["appState"]?.ToString() ?? "";
            string run = latest?["runState"]?.ToString() ?? "";
            bool alignedInPedestal = string.Equals(app, "PedestalState", StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(run, "Pedestal", StringComparison.OrdinalIgnoreCase);
            bool leftPedestal = !string.Equals(app, "PedestalState", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(run, "Pedestal", StringComparison.OrdinalIgnoreCase);
            if (alignedInPedestal || leftPedestal)
            {
                try
                {
                    var (full, _) = await client.InspectCompactAsync(ui: true);
                    samplePerf?.Invoke(full);
                    return full;
                }
                catch
                {
                    return latest;
                }
            }

            await Task.Delay(50);
            try
            {
                var (polled, _) = await client.InspectCompactAsync(ui: false);
                samplePerf?.Invoke(polled);
                latest = polled;
            }
            catch
            {
                return null;
            }
        }
        return latest;
    }

    private static async Task<JObject> WaitUntilLeftPedestalAsync(
        DriverClient client,
        JObject currentState,
        int maxWaitMs,
        Action<JObject> samplePerf)
    {
        var sw = Stopwatch.StartNew();
        JObject latest = currentState;
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            string app = latest?["appState"]?.ToString() ?? "";
            string run = latest?["runState"]?.ToString() ?? "";
            if (!string.Equals(app, "PedestalState", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(run, "Pedestal", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var (full, _) = await client.InspectCompactAsync(ui: true);
                    samplePerf?.Invoke(full);
                    return full;
                }
                catch
                {
                    return latest;
                }
            }

            await Task.Delay(60);
            try
            {
                var (polled, _) = await client.InspectCompactAsync(ui: false);
                samplePerf?.Invoke(polled);
                latest = polled;
            }
            catch
            {
                return null;
            }
        }
        return latest;
    }

    /// <summary>
    /// ChoiceState 兜底处理：严禁发送 /exit，仅清理尝试标记并轻量等待重检（必要时 resync）。
    /// </summary>
    public static async Task<JObject> HandleChoiceStateFallbackAsync(
        DriverClient client,
        ISet<string> triedInState,
        bool forceResync,
        Action<double, JObject> recordTiming)
    {
        triedInState?.Clear();
        if (forceResync)
        {
            var (sr, sMs) = await client.DriveWaitAsync(new { op = "resync", waitIdle = false });
            recordTiming?.Invoke(sMs, sr);
            Console.Write("~");
            await Task.Delay(140);
            return null;
        }

        Console.Write("~");
        await Task.Delay(100);
        try
        {
            var (inspected, _) = await client.InspectCompactAsync(ui: true);
            return inspected;
        }
        catch
        {
            return null;
        }
    }
}
