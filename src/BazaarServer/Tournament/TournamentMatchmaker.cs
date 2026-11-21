using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BazaarCore.Models;

namespace BazaarServer.Tournament;

/// <summary>
/// 锦标赛轮次匹配的参与者快照。
/// </summary>
public sealed record MatchupParticipant(
    string AccountId,
    string Username,
    bool IsBot,
    bool IsEliminated = false,
    bool IsSyntheticSparringBot = false);

/// <summary>
/// 单名参赛者在指定天数的对称匹配结果与战斗种子。
/// </summary>
public sealed record MatchupAssignment(
    uint Day,
    string AccountId,
    string OpponentAccountId,
    string OpponentUsername,
    bool OpponentIsBot,
    bool OpponentIsNonLethalBot,
    bool OpponentIsSyntheticSparringBot,
    int BattleSeed);

/// <summary>
/// 同一天真人对阵真人的对称战斗结果缓存记录。
/// </summary>
public sealed record HumanMatchResolution(
    uint Day,
    string PairKey,
    string WinnerAccountId,
    string LoserAccountId,
    int BattleSeed,
    int LoserPrestigeDamage,
    DateTime ResolvedAtUtc);

/// <summary>
/// 锦标赛双约束完美匹配随机采样器：
/// - 在全随机可预测性与多样性之间，数学上严格保证任意存活真人在连续 4 轮内必遇真人（s(u) <= 3）；
/// - 针对奇数存活真人（如 3 人局）施加奇偶前瞻约束，消除 (3,3,3) 鸽巢死锁；
/// - 维护逐房间/逐日对称配对、连续遇 AI 轮数 s(u)、上次交手轮次、确定性战斗种子与真人战果缓存；
/// - 支持只读预览（PreviewRoundMatchups，绝不提前冻结未来天配对）及 SQLite 重启状态导入导出。
/// </summary>
public sealed class TournamentMatchmaker
{
    private const double BackToBackRepeatPenalty = 0.05;
    private const double UnmetBonusPerRound = 0.25;

    private sealed class MatchmakerStateDto
    {
        public uint LatestAssignedDay { get; set; }
        public Dictionary<string, int> ConsecutiveBotRounds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, uint> LastMetRound { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _gate = new();
    private readonly string _lobbySeed;
    private readonly Random _rng;

    private readonly Dictionary<string, int> _consecutiveBotRounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _lastMetRound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, Dictionary<string, MatchupAssignment>> _cachedByDay = new();
    private readonly Dictionary<(uint Day, string PairKey), int> _pairSeeds = new();
    private readonly Dictionary<(uint Day, string PairKey), HumanMatchResolution> _humanResolutions = new();
    private uint _latestAssignedDay;

    public TournamentMatchmaker(string? lobbySeed = null, Random? rng = null)
    {
        _lobbySeed = string.IsNullOrWhiteSpace(lobbySeed) ? Guid.NewGuid().ToString("N") : lobbySeed.Trim();
        _rng = rng ?? Random.Shared;
    }

    /// <summary>判断第 <paramref name="day"/> 天是否已正式分配并冻结对称配对表。</summary>
    public bool HasAssignedDay(uint day)
    {
        if (day == 0) day = 1;
        lock (_gate) return _cachedByDay.ContainsKey(day);
    }

    /// <summary>导出匹配器内部状态 JSON（连续遇 AI 计数、上次交手天、最新冻结天），供 SQLite 持久化。</summary>
    public string ExportStateJson()
    {
        lock (_gate)
        {
            var dto = new MatchmakerStateDto
            {
                LatestAssignedDay = _latestAssignedDay,
                ConsecutiveBotRounds = new Dictionary<string, int>(_consecutiveBotRounds, StringComparer.OrdinalIgnoreCase),
                LastMetRound = new Dictionary<string, uint>(_lastMetRound, StringComparer.OrdinalIgnoreCase),
            };
            return JsonSerializer.Serialize(dto);
        }
    }

    /// <summary>从 SQLite 持久化 JSON 恢复匹配器内部状态。</summary>
    public void ImportStateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<MatchmakerStateDto>(json);
            if (dto == null) return;
            lock (_gate)
            {
                _latestAssignedDay = dto.LatestAssignedDay;
                _consecutiveBotRounds.Clear();
                if (dto.ConsecutiveBotRounds != null)
                {
                    foreach (var kv in dto.ConsecutiveBotRounds)
                        _consecutiveBotRounds[kv.Key] = kv.Value;
                }
                _lastMetRound.Clear();
                if (dto.LastMetRound != null)
                {
                    foreach (var kv in dto.LastMetRound)
                        _lastMetRound[kv.Key] = kv.Value;
                }
            }
        }
        catch { }
    }

    /// <summary>从持久化配对记录热恢复某天的已冻结配对与已裁决战果。</summary>
    public void RestoreDayAssignments(
        uint day,
        IEnumerable<MatchupAssignment> assignments,
        IEnumerable<HumanMatchResolution>? resolutions = null)
    {
        if (day == 0) day = 1;
        lock (_gate)
        {
            var map = new Dictionary<string, MatchupAssignment>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in assignments)
            {
                map[a.AccountId] = a;
                string pairKey = CanonicalPairKey(a.AccountId, a.OpponentAccountId);
                _pairSeeds[(day, pairKey)] = a.BattleSeed;
            }
            if (map.Count > 0)
            {
                _cachedByDay[day] = map;
                if (day > _latestAssignedDay) _latestAssignedDay = day;
            }

            if (resolutions != null)
            {
                foreach (var res in resolutions)
                {
                    _humanResolutions[(res.Day, res.PairKey)] = res;
                }
            }
        }
    }

    /// <summary>导出第 <paramref name="day"/> 天已冻结的配对字典快照（若尚未冻结返回 null）。</summary>
    public IReadOnlyDictionary<string, MatchupAssignment>? TryGetAssignedDaySnapshot(uint day)
    {
        if (day == 0) day = 1;
        lock (_gate)
        {
            return _cachedByDay.TryGetValue(day, out var map)
                ? new Dictionary<string, MatchupAssignment>(map, StringComparer.OrdinalIgnoreCase)
                : null;
        }
    }

    /// <summary>
    /// 构造无序规范化的双方键值，确保 (A, B) 与 (B, A) 命中同一缓存槽。
    /// </summary>
    public static string CanonicalPairKey(string accountA, string accountB)
    {
        string a = (accountA ?? string.Empty).Trim();
        string b = (accountB ?? string.Empty).Trim();
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{a.ToLowerInvariant()}|{b.ToLowerInvariant()}"
            : $"{b.ToLowerInvariant()}|{a.ToLowerInvariant()}";
    }

    /// <summary>
    /// 只读预览第 <paramref name="day"/> 天的配对表：
    /// 若该天已正式冻结则直接返回冻结结果；否则仅在临时内存生成预览结果，绝不写入 _cachedByDay 也不推进历史计数，
    /// 彻底杜绝 MP-P0-05“个别玩家提前进入下一天导致全房过早冻结脏对阵表”缺陷！
    /// </summary>
    public IReadOnlyDictionary<string, MatchupAssignment> PreviewRoundMatchups(
        uint day,
        IReadOnlyList<MatchupParticipant> participants,
        CustomRunRuleSet? rules)
    {
        if (day == 0) day = 1;
        lock (_gate)
        {
            if (_cachedByDay.TryGetValue(day, out var existing))
                return existing;

            return ComputeRoundMatchupsLocked(
                day,
                participants,
                humanMatchEvery4Rounds: rules?.HumanMatchEvery4Rounds ?? true,
                botNonLethalPrestigeLock: rules?.BotNonLethalPrestigeLock ?? true,
                commitToCache: false);
        }
    }

    /// <summary>
    /// 使用 <see cref="CustomRunRuleSet"/> 分配并冻结第 <paramref name="day"/> 天的全部对称匹配。
    /// 同一天重复调用是幂等的，不会二次累加连续遇 AI 轮数。
    /// </summary>
    public IReadOnlyDictionary<string, MatchupAssignment> AssignRoundMatchups(
        uint day,
        IReadOnlyList<MatchupParticipant> participants,
        CustomRunRuleSet? rules)
    {
        return AssignRoundMatchups(
            day,
            participants,
            humanMatchEvery4Rounds: rules?.HumanMatchEvery4Rounds ?? true,
            botNonLethalPrestigeLock: rules?.BotNonLethalPrestigeLock ?? true);
    }

    /// <summary>
    /// 为第 <paramref name="day"/> 天生成并冻结（或读取已冻结的）对称完美匹配。
    /// </summary>
    public IReadOnlyDictionary<string, MatchupAssignment> AssignRoundMatchups(
        uint day,
        IReadOnlyList<MatchupParticipant> participants,
        bool humanMatchEvery4Rounds = true,
        bool botNonLethalPrestigeLock = true)
    {
        if (day == 0) day = 1;

        lock (_gate)
        {
            if (_cachedByDay.TryGetValue(day, out var existing))
            {
                return existing;
            }

            return ComputeRoundMatchupsLocked(
                day,
                participants,
                humanMatchEvery4Rounds,
                botNonLethalPrestigeLock,
                commitToCache: true);
        }
    }

    private Dictionary<string, MatchupAssignment> ComputeRoundMatchupsLocked(
        uint day,
        IReadOnlyList<MatchupParticipant> participants,
        bool humanMatchEvery4Rounds,
        bool botNonLethalPrestigeLock,
        bool commitToCache)
    {
        var humans = new List<MatchupParticipant>();
        var bots = new List<MatchupParticipant>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (participants != null)
        {
            foreach (var p in participants)
            {
                if (p == null || p.IsEliminated || string.IsNullOrWhiteSpace(p.AccountId))
                    continue;
                if (!seenIds.Add(p.AccountId))
                    continue;

                if (p.IsBot || p.IsSyntheticSparringBot)
                    bots.Add(p);
                else
                    humans.Add(p);
            }
        }

        if (humans.Count == 0 && bots.Count == 0)
        {
            var empty = new Dictionary<string, MatchupAssignment>(StringComparer.OrdinalIgnoreCase);
            if (commitToCache) _cachedByDay[day] = empty;
            return empty;
        }

        if (((humans.Count + bots.Count) & 1) == 1)
        {
            bots.Add(new MatchupParticipant(
                AccountId: DeterministicId($"sparring:{_lobbySeed}:d{day}"),
                Username: "SparringBot",
                IsBot: true,
                IsEliminated: false,
                IsSyntheticSparringBot: true));
        }

        int h = humans.Count;
        int n = h + bots.Count;
        var roster = new MatchupParticipant[n];
        for (int i = 0; i < h; i++) roster[i] = humans[i];
        for (int i = 0; i < bots.Count; i++) roster[h + i] = bots[i];

        var s = new int[h];
        for (int i = 0; i < h; i++)
        {
            s[i] = _consecutiveBotRounds.GetValueOrDefault(roster[i].AccountId, 0);
        }

        var weights = BuildPairWeights(day, roster, h);
        bool enforceConstraints = humanMatchEvery4Rounds && h >= 2;
        int[] partner = SampleMatching(n, h, s, weights, enforceConstraints);

        if (commitToCache)
        {
            bool advanceHistory = day >= _latestAssignedDay;
            if (advanceHistory)
            {
                for (int i = 0; i < h; i++)
                {
                    int j = partner[i];
                    if (j < h)
                    {
                        _consecutiveBotRounds[roster[i].AccountId] = 0;
                        string pairKey = CanonicalPairKey(roster[i].AccountId, roster[j].AccountId);
                        _lastMetRound[pairKey] = day;
                    }
                    else
                    {
                        _consecutiveBotRounds[roster[i].AccountId] = s[i] + 1;
                    }
                }
                _latestAssignedDay = day;
            }
        }

        var result = new Dictionary<string, MatchupAssignment>(n, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
        {
            int j = partner[i];
            var self = roster[i];
            var opp = roster[j];
            string pairKey = CanonicalPairKey(self.AccountId, opp.AccountId);
            int battleSeed = commitToCache
                ? GetOrCreatePairBattleSeedLocked(day, pairKey)
                : ComputeDeterministicSeed(_lobbySeed, day, pairKey);
            bool oppIsBot = opp.IsBot || opp.IsSyntheticSparringBot;
            bool oppNonLethal = opp.IsSyntheticSparringBot || (oppIsBot && botNonLethalPrestigeLock);

            result[self.AccountId] = new MatchupAssignment(
                Day: day,
                AccountId: self.AccountId,
                OpponentAccountId: opp.AccountId,
                OpponentUsername: opp.Username,
                OpponentIsBot: oppIsBot,
                OpponentIsNonLethalBot: oppNonLethal,
                OpponentIsSyntheticSparringBot: opp.IsSyntheticSparringBot,
                BattleSeed: battleSeed);
        }

        if (commitToCache)
            _cachedByDay[day] = result;
        return result;
    }

    public MatchupAssignment? GetAssignment(string accountId, uint day)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return null;
        lock (_gate)
        {
            if (_cachedByDay.TryGetValue(day, out var map) && map.TryGetValue(accountId, out var assignment))
                return assignment;
            return null;
        }
    }

    public int GetConsecutiveBotRounds(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return 0;
        lock (_gate)
        {
            return _consecutiveBotRounds.GetValueOrDefault(accountId, 0);
        }
    }

    public uint? GetLastMetRound(string accountA, string accountB)
    {
        lock (_gate)
        {
            return _lastMetRound.TryGetValue(CanonicalPairKey(accountA, accountB), out uint d) ? d : null;
        }
    }

    public bool IsOpponentNonLethalBot(string accountId, uint day)
        => GetAssignment(accountId, day)?.OpponentIsNonLethalBot ?? false;

    public bool ShouldSuppressLethalElimination(string accountId, uint day, CustomRunRuleSet? rules)
        => ShouldSuppressLethalElimination(accountId, day, rules?.BotNonLethalPrestigeLock ?? true);

    public bool ShouldSuppressLethalElimination(string accountId, uint day, bool botNonLethalPrestigeLock = true)
    {
        var assignment = GetAssignment(accountId, day);
        if (assignment == null) return false;
        if (assignment.OpponentIsSyntheticSparringBot) return true;
        return assignment.OpponentIsBot && botNonLethalPrestigeLock;
    }

    public int GetOrCreatePairBattleSeed(uint day, string accountA, string accountB)
    {
        lock (_gate)
        {
            return GetOrCreatePairBattleSeedLocked(day, CanonicalPairKey(accountA, accountB));
        }
    }

    public HumanMatchResolution? TryGetHumanMatchResolution(uint day, string accountA, string accountB)
    {
        lock (_gate)
        {
            return _humanResolutions.GetValueOrDefault((day, CanonicalPairKey(accountA, accountB)));
        }
    }

    public HumanMatchResolution RecordHumanMatchResolution(
        uint day,
        string accountA,
        string accountB,
        string winnerAccountId,
        int loserPrestigeDamage = 0,
        int? battleSeed = null)
    {
        string pairKey = CanonicalPairKey(accountA, accountB);
        lock (_gate)
        {
            var key = (day, pairKey);
            if (_humanResolutions.TryGetValue(key, out var existing))
            {
                return existing;
            }

            string loser = string.Equals(winnerAccountId, accountA, StringComparison.OrdinalIgnoreCase)
                ? accountB
                : accountA;
            int seed = battleSeed ?? GetOrCreatePairBattleSeedLocked(day, pairKey);

            var resolution = new HumanMatchResolution(
                Day: day,
                PairKey: pairKey,
                WinnerAccountId: winnerAccountId,
                LoserAccountId: loser,
                BattleSeed: seed,
                LoserPrestigeDamage: Math.Max(0, loserPrestigeDamage),
                ResolvedAtUtc: DateTime.UtcNow);
            _humanResolutions[key] = resolution;
            return resolution;
        }
    }

    private int GetOrCreatePairBattleSeedLocked(uint day, string pairKey)
    {
        var key = (day, pairKey);
        if (_pairSeeds.TryGetValue(key, out int existing))
        {
            return existing;
        }

        int seed = ComputeDeterministicSeed(_lobbySeed, day, pairKey);
        _pairSeeds[key] = seed;
        return seed;
    }

    private static int ComputeDeterministicSeed(string lobbySeed, uint day, string pairKey)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in lobbySeed)
            h = (h ^ char.ToLowerInvariant(c)) * 1099511628211UL;
        h = (h ^ day) * 1099511628211UL;
        foreach (char c in pairKey)
            h = (h ^ c) * 1099511628211UL;
        h ^= h >> 30;
        h *= 0xbf58476d1ce4e5b9UL;
        h ^= h >> 27;
        h *= 0x94d049bb133111ebUL;
        h ^= h >> 31;

        int seed = (int)(h & 0x7FFFFFFFUL);
        return seed == 0 ? 1 : seed;
    }

    private double[,] BuildPairWeights(uint day, MatchupParticipant[] roster, int h)
    {
        int n = roster.Length;
        var weights = new double[n, n];

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double w;
                if (i < h && j < h)
                {
                    string pairKey = CanonicalPairKey(roster[i].AccountId, roster[j].AccountId);
                    if (_lastMetRound.TryGetValue(pairKey, out uint lastDay) && day > lastDay)
                    {
                        uint delta = day - lastDay;
                        w = delta == 1
                            ? BackToBackRepeatPenalty
                            : 1.0 + UnmetBonusPerRound * delta;
                    }
                    else
                    {
                        int roundsSince = Math.Max(0, (int)day - 1);
                        w = 1.0 + UnmetBonusPerRound * roundsSince;
                    }
                }
                else
                {
                    w = 1.0;
                }

                weights[i, j] = w;
                weights[j, i] = w;
            }
        }

        return weights;
    }

    private int[] SampleMatching(int n, int h, int[] s, double[,] weights, bool enforceConstraints)
    {
        bool oddAllAtLeast2 = enforceConstraints && (h & 1) == 1 && AllHumansAtLeast(s, h, 2);

        if (n <= 10)
        {
            if (TrySampleExactSmallN(n, h, s, weights, enforceConstraints, oddAllAtLeast2, out var exact))
                return exact;

            if (enforceConstraints && TrySampleExactSmallN(n, h, s, weights, enforceConstraints: false, oddAllAtLeast2: false, out var fallback))
                return fallback;
        }
        else
        {
            if (TrySampleBacktrackingLargeN(n, h, s, weights, enforceConstraints, oddAllAtLeast2, out var large))
                return large;

            if (enforceConstraints && TrySampleBacktrackingLargeN(n, h, s, weights, enforceConstraints: false, oddAllAtLeast2: false, out var fallback))
                return fallback;
        }

        var trivial = new int[n];
        for (int i = 0; i < n; i += 2)
        {
            trivial[i] = i + 1;
            trivial[i + 1] = i;
        }
        return trivial;
    }

    private bool TrySampleExactSmallN(
        int n,
        int h,
        int[] s,
        double[,] weights,
        bool enforceConstraints,
        bool oddAllAtLeast2,
        out int[] chosenPartner)
    {
        var used = new bool[n];
        var current = new int[n];
        var best = new int[n];
        int validCount = 0;
        double totalWeight = 0.0;

        void Dfs(int idx, int hhPairs, double curWeight)
        {
            while (idx < n && used[idx]) idx++;
            if (idx == n)
            {
                if (oddAllAtLeast2 && hhPairs == 0)
                    return;

                validCount++;
                totalWeight += curWeight;
                if (validCount == 1 || _rng.NextDouble() * totalWeight < curWeight)
                {
                    Array.Copy(current, best, n);
                }
                return;
            }

            used[idx] = true;
            bool mustPairWithHuman = enforceConstraints && idx < h && s[idx] >= 3;
            int maxPartner = mustPairWithHuman ? h : n;

            for (int j = idx + 1; j < maxPartner; j++)
            {
                if (used[j]) continue;

                used[j] = true;
                current[idx] = j;
                current[j] = idx;
                int nextHh = hhPairs + (idx < h && j < h ? 1 : 0);
                Dfs(idx + 1, nextHh, curWeight * weights[idx, j]);
                used[j] = false;
            }

            used[idx] = false;
        }

        Dfs(0, 0, 1.0);
        chosenPartner = best;
        return validCount > 0;
    }

    private bool TrySampleBacktrackingLargeN(
        int n,
        int h,
        int[] s,
        double[,] weights,
        bool enforceConstraints,
        bool oddAllAtLeast2,
        out int[] chosenPartner)
    {
        var used = new bool[n];
        var partner = new int[n];

        int initialRemK = 0;
        for (int i = 0; i < h; i++)
        {
            if (s[i] >= 3) initialRemK++;
        }

        bool Recurse(int remH, int remK, int hhPairs)
        {
            int u = PickNextVertex(n, h, s, used, enforceConstraints, oddAllAtLeast2, remK, hhPairs);
            if (u < 0) return true;

            var candidates = new List<(int Vertex, double Score)>(n);
            bool uIsHuman = u < h;
            bool uMustHuman = enforceConstraints && uIsHuman && s[u] >= 3;

            for (int v = 0; v < n; v++)
            {
                if (v == u || used[v]) continue;

                bool vIsHuman = v < h;
                if (uMustHuman && !vIsHuman) continue;
                if (enforceConstraints && vIsHuman && s[v] >= 3 && !uIsHuman) continue;

                int newRemH = remH - (uIsHuman ? 1 : 0) - (vIsHuman ? 1 : 0);
                int newRemK = remK - (uIsHuman && s[u] >= 3 ? 1 : 0) - (vIsHuman && s[v] >= 3 ? 1 : 0);
                int newHhPairs = hhPairs + (uIsHuman && vIsHuman ? 1 : 0);

                if (enforceConstraints && newRemK == newRemH && (newRemK & 1) == 1)
                    continue;
                if (oddAllAtLeast2 && newHhPairs == 0 && newRemH < 2)
                    continue;

                double w = Math.Max(1e-6, weights[u, v]);
                double uRand = Math.Max(1e-12, _rng.NextDouble());
                double score = -Math.Log(uRand) / w;
                candidates.Add((v, score));
            }

            if (candidates.Count == 0) return false;
            candidates.Sort(static (a, b) => a.Score.CompareTo(b.Score));

            used[u] = true;
            foreach (var (v, _) in candidates)
            {
                bool vIsHuman = v < h;
                int newRemH = remH - (uIsHuman ? 1 : 0) - (vIsHuman ? 1 : 0);
                int newRemK = remK - (uIsHuman && s[u] >= 3 ? 1 : 0) - (vIsHuman && s[v] >= 3 ? 1 : 0);
                int newHhPairs = hhPairs + (uIsHuman && vIsHuman ? 1 : 0);

                used[v] = true;
                partner[u] = v;
                partner[v] = u;
                if (Recurse(newRemH, newRemK, newHhPairs))
                    return true;
                used[v] = false;
            }
            used[u] = false;
            return false;
        }

        if (Recurse(h, initialRemK, 0))
        {
            chosenPartner = partner;
            return true;
        }

        chosenPartner = Array.Empty<int>();
        return false;
    }

    private static int PickNextVertex(
        int n,
        int h,
        int[] s,
        bool[] used,
        bool enforceConstraints,
        bool oddAllAtLeast2,
        int remK,
        int hhPairs)
    {
        if (enforceConstraints && remK > 0)
        {
            for (int i = 0; i < h; i++)
            {
                if (!used[i] && s[i] >= 3) return i;
            }
        }

        if (oddAllAtLeast2 && hhPairs == 0)
        {
            for (int i = 0; i < h; i++)
            {
                if (!used[i]) return i;
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (!used[i]) return i;
        }

        return -1;
    }

    private static bool AllHumansAtLeast(int[] s, int h, int threshold)
    {
        for (int i = 0; i < h; i++)
        {
            if (s[i] < threshold) return false;
        }
        return true;
    }

    private static string DeterministicId(string seed)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash).ToString("N");
    }
}
