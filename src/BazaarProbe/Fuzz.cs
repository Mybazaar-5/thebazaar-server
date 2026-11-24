#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using MessagePack;

namespace BazaarProbe;

/// <summary>
/// 模糊测试与锦标赛马拉松运行配置。
/// </summary>
internal sealed record FuzzOptions(
    int TotalRuns,
    string BaseUrl,
    string? LogPath,
    int Concurrency = 1,
    bool IncludeTournaments = false,
    int TournamentCount = 0,
    string? OutReportPath = null);

/// <summary>
/// Wave 3+ / V11 高压全英雄并发协议模糊测试与 Shadow Invariant 审计器。
/// 覆盖全部 8 位可玩英雄（Vanessa..Hero8）、约 10% 独立账号 Custom Rules、
/// 空间感知买卖/合并/换位/消耗品/多选战利品/基座/重随/断线续玩、以及 12 项官方不变量校验。
/// </summary>
internal static class Fuzz
{
    internal static readonly EHero[] FuzzHeroes =
    [
        EHero.Vanessa,
        EHero.Pygmalien,
        EHero.Dooley,
        EHero.Mak,
        EHero.Jules,
        EHero.Stelle,
        EHero.Karnok,
        EHero.Hero8,
    ];

    internal static readonly JsonSerializerOptions JsonWebOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal sealed class CardShadow
    {
        public string InstanceId { get; init; } = "";
        public string TemplateId { get; set; } = "";
        public ECardType Type { get; set; } = ECardType.Item;
        public ECardSize Size { get; set; } = ECardSize.Medium;
        public ETier Tier { get; set; } = ETier.Bronze;
        public EEnchantmentType? Enchantment { get; set; }
        public ECombatantId? Owner { get; set; }
        public EContainerSocketId? Socket { get; set; }
        public EInventorySection? Section { get; set; }
        public HashSet<ECardTag> Tags { get; } = new();
        public HashSet<EHiddenTag> HiddenTags { get; } = new();
        public HashSet<EHero> Heroes { get; } = new();
        public Dictionary<ECardAttributeType, int> Attributes { get; } = new();
    }

    internal sealed class HeroRunStats
    {
        public int Runs;
        public int Completed;
        public int Victory;
        public int Defeat;
        public int Failed;
    }

    /// <summary>
    /// 线程安全的全局指标与 12 项官方 Shadow Invariant 计数器。
    /// </summary>
    internal sealed class FuzzMetrics
    {
        public int RunsTotal;
        public int RunsCompleted;
        public int RunsVictory;
        public int RunsDefeat;
        public int RunsFailed;
        public int CustomRuleRuns;

        // 12 项官方 Shadow Invariants（任一 > 0 即判定整体失败并返回退出码 1）
        public int Http500Count;
        public int HttpOtherErrorCount;
        public int ProtocolErrorCount;
        public int ExceptionCount;
        public int StallCount;
        public int ResumeMismatchCount;
        public int WlInvariantViolations;
        public int ShopViolationCount;
        public int SlotOverlapOrOverflowCount;
        public int XpAndLevelViolationCount;
        public int PostCombatResidueViolationCount;
        public int PvpGhostViolationCount;

        // 博弈行为覆盖度指标
        public int TotalCommands;
        public int EncounterChoices;
        public int EncounterStepsExecuted;
        public int ItemsPurchased;
        public int ItemsPurchasedToHand;
        public int ItemsPurchasedToStash;
        public int UpgradeMergesObserved;
        public int ItemsSoldOrConsumed;
        public int ItemsMoved;
        public int MoveSwapsAttempted;
        public int Rerolls;
        public int PedestalsCommitted;
        public int LevelUpsCompleted;
        public int MilestoneSingleCardLevelUpsObserved;
        public int MultiPickLootEvents;
        public int SuspendedInterruptsSeen;
        public int AuraRefreshEventsSeen;
        public int PermCombatGrowthObserved;

        public int PveCombats;
        public int PveWins;
        public int PveLosses;
        public int PveDefeatPartialRewards;
        public int PveLootSkillsOrItems;
        public int PvpCombats;
        public int PvpWins;
        public int PvpLosses;
        public int FatesVisitsObserved;
        public int ResumeChecksPassed;

        // 锦标赛马拉松扩展指标
        public int TournamentLobbiesTotal;
        public int TournamentLobbiesPassed;
        public int TournamentLobbiesFailed;
        public int TournamentHumanSessions;
        public int TournamentBarrierBlocksVerified;
        public int TournamentParityChecksPassed;
        public int TournamentNonLethalLockChecksPassed;
        public int TournamentViolations;

        public readonly ConcurrentDictionary<EHero, HeroRunStats> HeroStats = new();
        private readonly object _latLock = new();
        private readonly List<long> _requestLatenciesMs = new(4096);
        private readonly ConcurrentQueue<string> _violationSamples = new();
        private int _violationSampleCount;

        public void RecordLatencyMs(long ms)
        {
            lock (_latLock)
            {
                _requestLatenciesMs.Add(ms);
            }
        }

        public List<long> SnapshotLatenciesSorted()
        {
            lock (_latLock)
            {
                var copy = new List<long>(_requestLatenciesMs);
                copy.Sort();
                return copy;
            }
        }

        public void RecordHeroOutcome(EHero hero, bool ok, ERunState verdict)
        {
            var hs = HeroStats.GetOrAdd(hero, _ => new HeroRunStats());
            Interlocked.Increment(ref hs.Runs);
            if (ok)
            {
                Interlocked.Increment(ref hs.Completed);
                if (verdict == ERunState.EndRunVictory) Interlocked.Increment(ref hs.Victory);
                else Interlocked.Increment(ref hs.Defeat);
            }
            else
            {
                Interlocked.Increment(ref hs.Failed);
            }
        }

        public void AddViolationSample(string message, Action<string>? logFile = null)
        {
            logFile?.Invoke("  [INVARIANT_VIOLATION] " + message);
            if (Interlocked.Increment(ref _violationSampleCount) <= 200)
            {
                _violationSamples.Enqueue(message);
            }
        }

        public IReadOnlyList<string> GetViolationSamples() => _violationSamples.ToArray();

        public long TotalInvariantViolations =>
            (long)Volatile.Read(ref Http500Count) +
            Volatile.Read(ref HttpOtherErrorCount) +
            Volatile.Read(ref ProtocolErrorCount) +
            Volatile.Read(ref ExceptionCount) +
            Volatile.Read(ref StallCount) +
            Volatile.Read(ref ResumeMismatchCount) +
            Volatile.Read(ref WlInvariantViolations) +
            Volatile.Read(ref ShopViolationCount) +
            Volatile.Read(ref SlotOverlapOrOverflowCount) +
            Volatile.Read(ref XpAndLevelViolationCount) +
            Volatile.Read(ref PostCombatResidueViolationCount) +
            Volatile.Read(ref PvpGhostViolationCount) +
            Volatile.Read(ref TournamentViolations);
    }

    /// <summary>
    /// 只读卡牌元数据目录：优先从 GameData.db 读取完整 SelectionContext 与标签/价格属性，
    /// 并与 cards.json + valid_card_ids.json 合并补全，确保商店与预设校验 100% 精准。
    /// </summary>
    internal sealed class CardMetadataCatalog
    {
        public sealed class CardMeta
        {
            public string Id { get; init; } = "";
            public string InternalName { get; init; } = "";
            public string Type { get; init; } = "";
            public string Size { get; init; } = "Medium";
            public string StartingTier { get; init; } = "Bronze";
            public string SpawningEligibility { get; init; } = "Always";
            public List<string> Heroes { get; init; } = new();
            public HashSet<string> Tags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> HiddenTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ZeroBuyPriceTiers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public bool AllowsCommonItemsInPaidShop { get; set; }
            public bool AllowsLootInPaidShop { get; set; }
            public HashSet<string> ExplicitAllowedShopItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);

            public bool IsPureCommonItem =>
                string.Equals(Type, "Item", StringComparison.OrdinalIgnoreCase) &&
                Heroes.Count > 0 &&
                Heroes.All(h => string.Equals(h, "Common", StringComparison.OrdinalIgnoreCase));

            public bool IsLootTagged => Tags.Contains("Loot");
            public bool IsUnsellable => Tags.Contains("Unsellable") || HiddenTags.Contains("Unsellable");

            public bool IsOfficialZeroCostAtTier(string tier)
            {
                bool explicitZero = ZeroBuyPriceTiers.Contains(tier) ||
                                    (ZeroBuyPriceTiers.Count > 0 && ZeroBuyPriceTiers.Contains(StartingTier));
                if (!explicitZero) return false;

                // 仅放行官方明确设计的 0 元 Piggle / 免费礼物包裹类卡
                return InternalName.Contains("Piggle", StringComparison.OrdinalIgnoreCase) ||
                       InternalName.Contains("Free", StringComparison.OrdinalIgnoreCase) ||
                       InternalName.Contains("Gift", StringComparison.OrdinalIgnoreCase) ||
                       Tags.Contains("Package") ||
                       HiddenTags.Contains("Package");
            }
        }

        private static readonly HashSet<string> KnownCommonShopIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "c6cffe25-8e94-4a7b-a821-3e3c7093333b", // Curio
            "15b88e74-024e-40a3-a811-aa5810e68ca2", // Private Pitchfork
            "002d8f6d-0e36-4921-89ed-3f0f28682d7b", // The Travel Agent
        };

        private static readonly string[] KnownExpeditionTicketIds =
        [
            "77c8ef2d-62c8-4214-993d-648915c93e83",
            "5bd0a69d-420c-4cd0-93ec-07582c688a5b",
            "24dc340b-bb3d-4c9b-8715-a807c0c06a8c",
        ];

        private readonly Dictionary<string, CardMeta> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _validIds = new(StringComparer.OrdinalIgnoreCase);
        public string SourceSummary { get; private set; } = "none";
        public int Count => _byId.Count;

        public CardMeta? Get(string? templateId) =>
            !string.IsNullOrWhiteSpace(templateId) && _byId.TryGetValue(templateId, out var m) ? m : null;

        public bool IsAssetValid(string? id) =>
            !string.IsNullOrWhiteSpace(id) && (_validIds.Count == 0 || _validIds.Contains(id));

        public List<CardMeta> PickPresetItems(int count)
        {
            return _byId.Values
                .Where(c => string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.Size, "Small", StringComparison.OrdinalIgnoreCase) &&
                            !c.IsLootTagged &&
                            !c.IsPureCommonItem &&
                            !c.InternalName.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase) &&
                            !c.InternalName.Contains("Tutorial", StringComparison.OrdinalIgnoreCase) &&
                            IsAssetValid(c.Id))
                .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .ToList();
        }

        public List<CardMeta> PickPresetSkills(int count)
        {
            return _byId.Values
                .Where(c => string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase) &&
                            !c.InternalName.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase) &&
                            !c.InternalName.Contains("Tutorial", StringComparison.OrdinalIgnoreCase) &&
                            IsAssetValid(c.Id))
                .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .ToList();
        }

        public static CardMetadataCatalog LoadReadOnly(Action<string>? log = null)
        {
            var cat = new CardMetadataCatalog();
            string dataDir = ResolveDataDir();
            string whitelistPath = Path.Combine(dataDir, "valid_card_ids.json");
            if (File.Exists(whitelistPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(whitelistPath));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                                cat._validIds.Add(el.GetString()!);
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                             doc.RootElement.TryGetProperty("valid_ids", out var arr) &&
                             arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                                cat._validIds.Add(el.GetString()!);
                        }
                    }
                }
                catch { }
            }

            int dbCount = 0;
            string dbPath = Path.Combine(dataDir, "GameData.db");
            if (File.Exists(dbPath))
            {
                dbCount = cat.TryLoadFromSqliteDb(dbPath, log);
            }

            int jsonCount = 0;
            string cardsJsonPath = Path.Combine(dataDir, "cards.json");
            if (File.Exists(cardsJsonPath))
            {
                jsonCount = cat.TrySupplementFromCardsJson(cardsJsonPath, log);
            }

            // 补全已知特例遭遇（如 Curio / Private Pitchfork / The Travel Agent）
            foreach (string knownId in KnownCommonShopIds)
            {
                if (cat._byId.TryGetValue(knownId, out var m))
                {
                    m.AllowsCommonItemsInPaidShop = true;
                    if (knownId.Equals("002d8f6d-0e36-4921-89ed-3f0f28682d7b", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string tid in KnownExpeditionTicketIds)
                            m.ExplicitAllowedShopItemIds.Add(tid);
                    }
                }
            }

            cat.SourceSummary = $"db={dbCount}, json_supplement={jsonCount}, total={cat._byId.Count}, whitelist={cat._validIds.Count}";
            log?.Invoke($"[CardMetadataCatalog] loaded ({cat.SourceSummary}) from {dataDir}");
            return cat;
        }

        private static string ResolveDataDir()
        {
            string? envData = Environment.GetEnvironmentVariable("BAZAAR_DATA_DIR")
                ?? Environment.GetEnvironmentVariable("BAZAAR_DATA");
            string[] candidates =
            [
                envData ?? string.Empty,
                Path.Combine(Environment.CurrentDirectory, "dist", "data"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dist", "data")),
                Path.Combine(AppContext.BaseDirectory, "data"),
            ];
            return candidates.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                ?? Path.Combine(Environment.CurrentDirectory, "dist", "data");
        }

        private int TryLoadFromSqliteDb(string dbPath, Action<string>? log)
        {
            try
            {
                string? sqliteDllDir = FindSqliteBinDir();
                if (string.IsNullOrEmpty(sqliteDllDir))
                    return 0;

                string sqliteDllPath = Path.Combine(sqliteDllDir, "Microsoft.Data.Sqlite.dll");
                if (!File.Exists(sqliteDllPath))
                    return 0;

                var loadCtx = new SqliteHelperLoadContext(sqliteDllDir);
                Assembly sqliteAsm = loadCtx.LoadFromAssemblyPath(sqliteDllPath);

                // 确保 SQLitePCLRaw.batteries_v2 完成初始化
                string batteriesPath = Path.Combine(sqliteDllDir, "SQLitePCLRaw.batteries_v2.dll");
                if (File.Exists(batteriesPath))
                {
                    var batAsm = loadCtx.LoadFromAssemblyPath(batteriesPath);
                    var batType = batAsm.GetType("SQLitePCL.Batteries_V2");
                    batType?.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                }

                Type? connType = sqliteAsm.GetType("Microsoft.Data.Sqlite.SqliteConnection");
                if (connType == null) return 0;

                string connStr = $"Data Source={dbPath};Mode=ReadOnly";
                using var conn = (DbConnection)Activator.CreateInstance(connType, connStr)!;
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Id, Data FROM cards";
                using var reader = cmd.ExecuteReader();
                int count = 0;
                while (reader.Read())
                {
                    string id = reader.GetString(0);
                    byte[] blob = (byte[])reader.GetValue(1);
                    string json = Encoding.UTF8.GetString(blob);
                    var meta = ParseCardElement(id, json);
                    if (meta != null)
                    {
                        _byId[meta.Id] = meta;
                        count++;
                    }
                }
                return count;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[CardMetadataCatalog] SQLite dynamic load skipped ({ex.Message}); falling back to cards.json");
                return 0;
            }
        }

        private static string? FindSqliteBinDir()
        {
            var candidates = new List<string>
            {
                AppContext.BaseDirectory,
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BazaarServer", "bin", "Release", "net8.0")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BazaarServer", "bin", "Debug", "net8.0")),
                Path.Combine(Environment.CurrentDirectory, "src", "BazaarServer", "bin", "Release", "net8.0"),
                Path.Combine(Environment.CurrentDirectory, "src", "BazaarServer", "bin", "Debug", "net8.0"),
            };

            return candidates.FirstOrDefault(dir =>
                Directory.Exists(dir) &&
                File.Exists(Path.Combine(dir, "Microsoft.Data.Sqlite.dll")) &&
                File.Exists(Path.Combine(dir, "SQLitePCLRaw.core.dll")));
        }

        private sealed class SqliteHelperLoadContext : AssemblyLoadContext
        {
            private readonly string _baseDir;

            public SqliteHelperLoadContext(string baseDir) : base(isCollectible: false)
            {
                _baseDir = baseDir;
            }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                string candidate = Path.Combine(_baseDir, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                    return LoadFromAssemblyPath(candidate);
                return null;
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                string winX64 = Path.Combine(_baseDir, "runtimes", "win-x64", "native", unmanagedDllName + ".dll");
                if (File.Exists(winX64)) return LoadUnmanagedDllFromPath(winX64);
                string direct = Path.Combine(_baseDir, unmanagedDllName + ".dll");
                if (File.Exists(direct)) return LoadUnmanagedDllFromPath(direct);
                return base.LoadUnmanagedDll(unmanagedDllName);
            }
        }

        private int TrySupplementFromCardsJson(string cardsJsonPath, Action<string>? log)
        {
            int added = 0;
            try
            {
                using var stream = File.OpenRead(cardsJsonPath);
                using var doc = JsonDocument.Parse(stream);
                foreach (var verProp in doc.RootElement.EnumerateObject())
                {
                    if (verProp.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var el in verProp.Value.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        string id = el.TryGetProperty("Id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() ?? ""
                            : "";
                        if (string.IsNullOrWhiteSpace(id) || _byId.ContainsKey(id)) continue;

                        var meta = ParseCardObject(id, el);
                        if (meta != null)
                        {
                            _byId[meta.Id] = meta;
                            added++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[CardMetadataCatalog] cards.json parse error: {ex.Message}");
            }
            return added;
        }

        private static CardMeta? ParseCardElement(string id, string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return ParseCardObject(id, doc.RootElement);
            }
            catch
            {
                return null;
            }
        }

        private static CardMeta? ParseCardObject(string id, JsonElement r)
        {
            if (r.ValueKind != JsonValueKind.Object) return null;

            static string Str(JsonElement obj, string prop, string fallback = "") =>
                obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? fallback
                    : fallback;

            var heroes = new List<string>();
            if (r.TryGetProperty("Heroes", out var hEl) && hEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in hEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        heroes.Add(item.GetString()!);
                }
            }

            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (r.TryGetProperty("Tags", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        tags.Add(item.GetString()!);
                }
            }

            var hiddenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (r.TryGetProperty("HiddenTags", out var htEl) && htEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in htEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        hiddenTags.Add(item.GetString()!);
                }
            }

            var zeroBuyPriceTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (r.TryGetProperty("Tiers", out var tiersEl) && tiersEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var tp in tiersEl.EnumerateObject())
                {
                    if (tp.Value.ValueKind == JsonValueKind.Object &&
                        tp.Value.TryGetProperty("Attributes", out var attrsEl) &&
                        attrsEl.ValueKind == JsonValueKind.Object &&
                        attrsEl.TryGetProperty("BuyPrice", out var bpEl) &&
                        bpEl.ValueKind == JsonValueKind.Number &&
                        bpEl.TryGetDouble(out double bpVal) &&
                        bpVal <= 0)
                    {
                        zeroBuyPriceTiers.Add(tp.Name);
                    }
                }
            }

            var meta = new CardMeta
            {
                Id = id,
                InternalName = Str(r, "InternalName"),
                Type = Str(r, "Type"),
                Size = Str(r, "Size", "Medium"),
                StartingTier = Str(r, "StartingTier", "Bronze"),
                SpawningEligibility = Str(r, "SpawningEligibility", "Always"),
                Heroes = heroes,
                Tags = tags,
                HiddenTags = hiddenTags,
                ZeroBuyPriceTiers = zeroBuyPriceTiers,
            };

            if (r.TryGetProperty("SelectionContext", out var scEl) &&
                scEl.ValueKind == JsonValueKind.Object &&
                scEl.TryGetProperty("SpawnContext", out var spawnEl) &&
                spawnEl.ValueKind == JsonValueKind.Object)
            {
                InspectSpawnContext(spawnEl, meta);
            }

            return meta;
        }

        private static void InspectSpawnContext(JsonElement node, CardMeta meta, bool inHeroConstrainedGroup = false)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                string typeStr = node.TryGetProperty("$type", out var tp) && tp.ValueKind == JsonValueKind.String
                    ? tp.GetString() ?? ""
                    : "";

                if (string.Equals(typeStr, "TSpawnGroup", StringComparison.OrdinalIgnoreCase))
                {
                    // 若该分组显式带了非 Common 的正向 ConstraintHero / ConstraintIsOnlyHero（如 Valpak/Silvia 的 Fence 解锁分组），
                    // 其组内 TSpawnBehaviorIgnoreHero 仅用于跨英雄解锁特定英雄池，绝不放行纯 Common 怪卡。
                    inHeroConstrainedGroup = HasPositiveNonCommonHeroConstraint(node);
                }

                if ((string.Equals(typeStr, "TSpawnBehaviorIncludeCommonAndHero", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(typeStr, "TSpawnBehaviorIgnoreHero", StringComparison.OrdinalIgnoreCase)) &&
                    !inHeroConstrainedGroup)
                {
                    meta.AllowsCommonItemsInPaidShop = true;
                }
                else if (string.Equals(typeStr, "ConstraintHero", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(typeStr, "ConstraintIsOnlyHero", StringComparison.OrdinalIgnoreCase))
                {
                    bool isNot = node.TryGetProperty("IsNot", out var inEl) &&
                                 inEl.ValueKind == JsonValueKind.True;
                    if (!isNot && node.TryGetProperty("Heroes", out var hs) && hs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var h in hs.EnumerateArray())
                        {
                            if (h.ValueKind == JsonValueKind.String &&
                                string.Equals(h.GetString(), "Common", StringComparison.OrdinalIgnoreCase))
                            {
                                meta.AllowsCommonItemsInPaidShop = true;
                            }
                        }
                    }
                }
                else if (string.Equals(typeStr, "ConstraintTag", StringComparison.OrdinalIgnoreCase))
                {
                    bool isNot = node.TryGetProperty("IsNot", out var inEl) &&
                                 inEl.ValueKind == JsonValueKind.True;
                    if (!isNot && node.TryGetProperty("Tags", out var ts) && ts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in ts.EnumerateArray())
                        {
                            if (t.ValueKind == JsonValueKind.String &&
                                string.Equals(t.GetString(), "Loot", StringComparison.OrdinalIgnoreCase))
                            {
                                meta.AllowsLootInPaidShop = true;
                            }
                        }
                    }
                }
                else if (string.Equals(typeStr, "TSpawnFilterIdList", StringComparison.OrdinalIgnoreCase))
                {
                    if (node.TryGetProperty("Ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var idEl in ids.EnumerateArray())
                        {
                            if (idEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString()))
                                meta.ExplicitAllowedShopItemIds.Add(idEl.GetString()!);
                        }
                    }
                }

                foreach (var prop in node.EnumerateObject())
                    InspectSpawnContext(prop.Value, meta, inHeroConstrainedGroup);
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                    InspectSpawnContext(item, meta, inHeroConstrainedGroup);
            }
        }

        private static bool HasPositiveNonCommonHeroConstraint(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                string typeStr = node.TryGetProperty("$type", out var tp) && tp.ValueKind == JsonValueKind.String
                    ? tp.GetString() ?? ""
                    : "";
                if (string.Equals(typeStr, "ConstraintHero", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeStr, "ConstraintIsOnlyHero", StringComparison.OrdinalIgnoreCase))
                {
                    bool isNot = node.TryGetProperty("IsNot", out var inEl) &&
                                 inEl.ValueKind == JsonValueKind.True;
                    if (!isNot && node.TryGetProperty("Heroes", out var hs) && hs.ValueKind == JsonValueKind.Array)
                    {
                        bool anyNonCommon = false;
                        bool anyCommon = false;
                        foreach (var h in hs.EnumerateArray())
                        {
                            if (h.ValueKind != JsonValueKind.String) continue;
                            if (string.Equals(h.GetString(), "Common", StringComparison.OrdinalIgnoreCase))
                                anyCommon = true;
                            else if (!string.IsNullOrWhiteSpace(h.GetString()))
                                anyNonCommon = true;
                        }
                        if (anyNonCommon && !anyCommon) return true;
                    }
                }

                foreach (var prop in node.EnumerateObject())
                {
                    if (HasPositiveNonCommonHeroConstraint(prop.Value))
                        return true;
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    if (HasPositiveNonCommonHeroConstraint(item))
                        return true;
                }
            }
            return false;
        }
    }

    public static Task<int> Run(int totalRuns, string baseUrl, string? logPath)
        => Run(new FuzzOptions(totalRuns, baseUrl, logPath));

    public static async Task<int> Run(FuzzOptions options)
    {
        int totalRuns = Math.Max(0, options.TotalRuns);
        int concurrency = Math.Max(1, options.Concurrency);
        string baseUrl = options.BaseUrl.TrimEnd('/');
        string? logPath = options.LogPath;

        if (string.IsNullOrWhiteSpace(logPath))
        {
            string defaultDir = Path.Combine(Environment.CurrentDirectory, "logs");
            logPath = Path.Combine(defaultDir, $"fuzz_{totalRuns}_v11.log");
        }

        string? logDir = Path.GetDirectoryName(Path.GetFullPath(logPath));
        if (!string.IsNullOrEmpty(logDir)) Directory.CreateDirectory(logDir);

        using var logWriter = new StreamWriter(logPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        var logLock = new object();

        void Log(string line)
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
            lock (logLock)
            {
                Console.WriteLine(stamped);
                logWriter.WriteLine(stamped);
            }
        }

        void LogFileOnly(string line)
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
            lock (logLock)
            {
                logWriter.WriteLine(stamped);
            }
        }

        Log($"== BazaarProbe Fuzz V11 start: runs={totalRuns}, concurrency={concurrency}, tournaments={(options.IncludeTournaments ? options.TournamentCount : 0)}, target={baseUrl}, log={logPath} ==");

        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = Math.Max(64, concurrency * 8),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };

        // 先校验服务健康
        {
            var hz = await http.GetAsync(baseUrl + "/healthz");
            if (!hz.IsSuccessStatusCode)
            {
                Log($"FATAL: healthz returned {hz.StatusCode}");
                return 1;
            }
            Log($"healthz OK: {await hz.Content.ReadAsStringAsync()}");
        }

        // 只读加载卡牌元数据用于精准 Shop/Preset 不变量审计
        var catalog = CardMetadataCatalog.LoadReadOnly(Log);

        var metrics = new FuzzMetrics { RunsTotal = totalRuns };
        var swAll = Stopwatch.StartNew();
        int completedCounter = 0;

        // 1. 并发执行单人 8 英雄全流程 Fuzz
        if (totalRuns > 0)
        {
            var indices = Enumerable.Range(0, totalRuns);
            await Parallel.ForEachAsync(
                indices,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency },
                async (i, ct) =>
                {
                    var hero = FuzzHeroes[i % FuzzHeroes.Length];
                    bool preferWeakBoard = (i % 4 == 3);
                    bool preferNonShopEncounters = (i % 2 == 0);
                    bool useCustomRules = (i % 10 == 5);
                    uint resumeTargetDay = (i % 3 == 0) ? 1u : 2u;
                    uint resumeTargetHour = (i % 3 == 0) ? 4u : 2u;

                    var swRun = Stopwatch.StartNew();
                    var (ok, verdict, runSummary) = await RunSingleFuzzGame(
                        runIdx: i,
                        hero: hero,
                        baseUrl: baseUrl,
                        http: http,
                        catalog: catalog,
                        preferWeakBoard: preferWeakBoard,
                        preferNonShopEncounters: preferNonShopEncounters,
                        useCustomRules: useCustomRules,
                        resumeTargetDay: resumeTargetDay,
                        resumeTargetHour: resumeTargetHour,
                        metrics: metrics,
                        logFile: LogFileOnly);
                    swRun.Stop();

                    metrics.RecordHeroOutcome(hero, ok, verdict);
                    if (ok)
                    {
                        Interlocked.Increment(ref metrics.RunsCompleted);
                        if (verdict == ERunState.EndRunVictory) Interlocked.Increment(ref metrics.RunsVictory);
                        else Interlocked.Increment(ref metrics.RunsDefeat);
                    }
                    else
                    {
                        Interlocked.Increment(ref metrics.RunsFailed);
                        Log($"RUN#{i + 1:D4} FAILED ({hero}, custom={useCustomRules}): {runSummary}");
                    }

                    int done = Interlocked.Increment(ref completedCounter);
                    if (done % 20 == 0 || !ok || done == 1 || done == totalRuns)
                    {
                        Log($"[Progress {done:D4}/{totalRuns}] hero={hero} ok={ok} ({verdict}) {swRun.ElapsedMilliseconds}ms | {runSummary}");
                    }
                });
        }

        // 2. 执行多人并发锦标赛马拉松测试（若启用）
        if (options.IncludeTournaments && options.TournamentCount > 0)
        {
            await TournamentMarathon.RunAsync(
                lobbyCount: options.TournamentCount,
                concurrency: Math.Max(1, Math.Min(concurrency, Math.Max(1, options.TournamentCount))),
                baseUrl: baseUrl,
                http: http,
                catalog: catalog,
                metrics: metrics,
                log: Log,
                logFileOnly: LogFileOnly);
        }

        swAll.Stop();

        var sortedLat = metrics.SnapshotLatenciesSorted();
        double p50 = Percentile(sortedLat, 0.50);
        double p95 = Percentile(sortedLat, 0.95);
        double p99 = Percentile(sortedLat, 0.99);

        Log("");
        Log("== Fuzz V11 Summary ==");
        Log($"  Elapsed                   : {swAll.Elapsed.TotalSeconds:F2}s");
        Log($"  RunsCompleted             : {metrics.RunsCompleted}/{metrics.RunsTotal} (Victory={metrics.RunsVictory}, Defeat={metrics.RunsDefeat}, Failed={metrics.RunsFailed}, CustomRuleRuns={metrics.CustomRuleRuns})");
        Log($"  Heroes                    : {string.Join(", ", FuzzHeroes.Select(h => { var s = metrics.HeroStats.GetValueOrDefault(h); return $"{h}={(s?.Runs ?? 0)}({s?.Victory ?? 0}W/{s?.Defeat ?? 0}D/{s?.Failed ?? 0}F)"; }))}");
        Log($"  [Inv 01] HTTP 500         : {metrics.Http500Count}");
        Log($"  [Inv 02] HTTP Other Err   : {metrics.HttpOtherErrorCount}");
        Log($"  [Inv 03] Protocol Errors  : {metrics.ProtocolErrorCount}");
        Log($"  [Inv 04] Exceptions       : {metrics.ExceptionCount}");
        Log($"  [Inv 05] State Stalls     : {metrics.StallCount}");
        Log($"  [Inv 06] Resume Mismatch  : {metrics.ResumeMismatchCount}");
        Log($"  [Inv 07] W+L Violations   : {metrics.WlInvariantViolations}");
        Log($"  [Inv 08] Shop Violations  : {metrics.ShopViolationCount}");
        Log($"  [Inv 09] Slot Overlap/Ovfl: {metrics.SlotOverlapOrOverflowCount}");
        Log($"  [Inv 10] XP/Level Viol.   : {metrics.XpAndLevelViolationCount}");
        Log($"  [Inv 11] PostCombat Resid : {metrics.PostCombatResidueViolationCount}");
        Log($"  [Inv 12] PvP Ghost Viol.  : {metrics.PvpGhostViolationCount}");
        Log($"  Total Commands / Latency  : {metrics.TotalCommands} cmds | p50={p50:F1}ms p95={p95:F1}ms p99={p99:F1}ms");
        Log($"  Purchased (Hand/Stash/Mrg): {metrics.ItemsPurchased} (Hand={metrics.ItemsPurchasedToHand}, Stash={metrics.ItemsPurchasedToStash}, Merges={metrics.UpgradeMergesObserved})");
        Log($"  Sold/Consumed / Moved     : {metrics.ItemsSoldOrConsumed} / {metrics.ItemsMoved} (Swaps={metrics.MoveSwapsAttempted}, AuraRef={metrics.AuraRefreshEventsSeen})");
        Log($"  Rerolls / Pedestals       : {metrics.Rerolls} / {metrics.PedestalsCommitted}");
        Log($"  LevelUps / LootMultiPick  : {metrics.LevelUpsCompleted} (Milestone1Card={metrics.MilestoneSingleCardLevelUpsObserved}) / {metrics.MultiPickLootEvents} (SuspInt={metrics.SuspendedInterruptsSeen})");
        Log($"  PvE (W/L/Part/Loot)       : {metrics.PveCombats} ({metrics.PveWins}W / {metrics.PveLosses}L / Partial={metrics.PveDefeatPartialRewards} / LootPicks={metrics.PveLootSkillsOrItems})");
        Log($"  PvP (W/L) / Fates         : {metrics.PvpCombats} ({metrics.PvpWins}W / {metrics.PvpLosses}L) / Fates={metrics.FatesVisitsObserved}");
        Log($"  PermCombatGrowth / Resume : {metrics.PermCombatGrowthObserved} / {metrics.ResumeChecksPassed}");

        if (options.IncludeTournaments)
        {
            Log($"  Tournaments               : {metrics.TournamentLobbiesPassed}/{metrics.TournamentLobbiesTotal} passed (Failed={metrics.TournamentLobbiesFailed}, Humans={metrics.TournamentHumanSessions}, BarrierProbes={metrics.TournamentBarrierBlocksVerified}, Parity={metrics.TournamentParityChecksPassed}, NonLethal={metrics.TournamentNonLethalLockChecksPassed}, Violations={metrics.TournamentViolations})");
        }

        if (!string.IsNullOrWhiteSpace(options.OutReportPath))
        {
            WriteJsonReport(options.OutReportPath!, options, swAll.Elapsed.TotalSeconds, p50, p95, p99, metrics, catalog);
            Log($"  JSON Report written       : {options.OutReportPath}");
        }

        bool passed = metrics.RunsCompleted == totalRuns
                      && metrics.RunsFailed == 0
                      && metrics.TournamentLobbiesFailed == 0
                      && metrics.TotalInvariantViolations == 0;

        Log(passed ? $"FUZZ_{totalRuns}_V11: ALL_PASS" : $"FUZZ_{totalRuns}_V11: FAILED (violations={metrics.TotalInvariantViolations})");
        return passed ? 0 : 1;
    }

    private static double Percentile(List<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0.0;
        if (sorted.Count == 1) return sorted[0];
        double idx = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx);
        int hi = Math.Min(sorted.Count - 1, lo + 1);
        double frac = idx - lo;
        return sorted[lo] * (1.0 - frac) + sorted[hi] * frac;
    }

    private static void WriteJsonReport(
        string outPath,
        FuzzOptions options,
        double elapsedSeconds,
        double p50,
        double p95,
        double p99,
        FuzzMetrics metrics,
        CardMetadataCatalog catalog)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var heroMap = new Dictionary<string, object>();
        foreach (var hero in FuzzHeroes)
        {
            var hs = metrics.HeroStats.GetValueOrDefault(hero);
            heroMap[hero.ToString()] = new
            {
                runs = hs?.Runs ?? 0,
                completed = hs?.Completed ?? 0,
                victory = hs?.Victory ?? 0,
                defeat = hs?.Defeat ?? 0,
                failed = hs?.Failed ?? 0,
            };
        }

        var report = new
        {
            generatedAtUtc = DateTime.UtcNow,
            baseUrl = options.BaseUrl,
            concurrency = options.Concurrency,
            elapsedSeconds = Math.Round(elapsedSeconds, 3),
            passed = metrics.RunsCompleted == options.TotalRuns &&
                     metrics.RunsFailed == 0 &&
                     metrics.TournamentLobbiesFailed == 0 &&
                     metrics.TotalInvariantViolations == 0,
            cardCatalog = catalog.SourceSummary,
            runs = new
            {
                total = metrics.RunsTotal,
                completed = metrics.RunsCompleted,
                victory = metrics.RunsVictory,
                defeat = metrics.RunsDefeat,
                failed = metrics.RunsFailed,
                customRuleRuns = metrics.CustomRuleRuns,
                byHero = heroMap,
            },
            commands = new
            {
                totalCommands = metrics.TotalCommands,
                latencyMs = new
                {
                    p50 = Math.Round(p50, 2),
                    p95 = Math.Round(p95, 2),
                    p99 = Math.Round(p99, 2),
                },
                encounterChoices = metrics.EncounterChoices,
                encounterStepsExecuted = metrics.EncounterStepsExecuted,
                itemsPurchased = metrics.ItemsPurchased,
                itemsPurchasedToHand = metrics.ItemsPurchasedToHand,
                itemsPurchasedToStash = metrics.ItemsPurchasedToStash,
                upgradeMergesObserved = metrics.UpgradeMergesObserved,
                itemsSoldOrConsumed = metrics.ItemsSoldOrConsumed,
                itemsMoved = metrics.ItemsMoved,
                moveSwapsAttempted = metrics.MoveSwapsAttempted,
                rerolls = metrics.Rerolls,
                pedestalsCommitted = metrics.PedestalsCommitted,
                levelUpsCompleted = metrics.LevelUpsCompleted,
                milestoneSingleCardLevelUpsObserved = metrics.MilestoneSingleCardLevelUpsObserved,
                multiPickLootEvents = metrics.MultiPickLootEvents,
                suspendedInterruptsSeen = metrics.SuspendedInterruptsSeen,
                auraRefreshEventsSeen = metrics.AuraRefreshEventsSeen,
                permCombatGrowthObserved = metrics.PermCombatGrowthObserved,
                resumeChecksPassed = metrics.ResumeChecksPassed,
                fatesVisitsObserved = metrics.FatesVisitsObserved,
                pve = new
                {
                    combats = metrics.PveCombats,
                    wins = metrics.PveWins,
                    losses = metrics.PveLosses,
                    partialRewards = metrics.PveDefeatPartialRewards,
                    lootPicks = metrics.PveLootSkillsOrItems,
                },
                pvp = new
                {
                    combats = metrics.PvpCombats,
                    wins = metrics.PvpWins,
                    losses = metrics.PvpLosses,
                },
            },
            tournaments = new
            {
                enabled = options.IncludeTournaments,
                lobbiesTotal = metrics.TournamentLobbiesTotal,
                lobbiesPassed = metrics.TournamentLobbiesPassed,
                lobbiesFailed = metrics.TournamentLobbiesFailed,
                humanSessions = metrics.TournamentHumanSessions,
                barrierBlocksVerified = metrics.TournamentBarrierBlocksVerified,
                parityChecksPassed = metrics.TournamentParityChecksPassed,
                nonLethalLockChecksPassed = metrics.TournamentNonLethalLockChecksPassed,
                violations = metrics.TournamentViolations,
            },
            invariants = new
            {
                http500Count = metrics.Http500Count,
                httpOtherErrorCount = metrics.HttpOtherErrorCount,
                protocolErrorCount = metrics.ProtocolErrorCount,
                exceptionCount = metrics.ExceptionCount,
                stallCount = metrics.StallCount,
                resumeMismatchCount = metrics.ResumeMismatchCount,
                wlInvariantViolations = metrics.WlInvariantViolations,
                shopViolationCount = metrics.ShopViolationCount,
                slotOverlapOrOverflowCount = metrics.SlotOverlapOrOverflowCount,
                xpAndLevelViolationCount = metrics.XpAndLevelViolationCount,
                postCombatResidueViolationCount = metrics.PostCombatResidueViolationCount,
                pvpGhostViolationCount = metrics.PvpGhostViolationCount,
                totalViolations = metrics.TotalInvariantViolations,
                violationSamples = metrics.GetViolationSamples(),
            },
        };

        File.WriteAllText(outPath, JsonSerializer.Serialize(report, JsonWebOpts), new UTF8Encoding(false));
    }

    internal static async Task<(string Token, string AccountId, bool Ok, string? Error)> AuthenticateUserAsync(
        HttpClient http,
        string baseUrl,
        string username,
        FuzzMetrics metrics,
        Action<string> logFile)
    {
        var sw = Stopwatch.StartNew();
        var loginResp = await http.PostAsync(baseUrl + "/api/auth/login/user",
            new StringContent($"{{\"username\":\"{username}\"}}", Encoding.UTF8, "application/json"));
        sw.Stop();
        metrics.RecordLatencyMs(sw.ElapsedMilliseconds);

        if (!CheckHttpStatus(loginResp.StatusCode, "login", metrics, logFile))
            return ("", "", false, $"login HTTP {loginResp.StatusCode}");

        string loginJson = await loginResp.Content.ReadAsStringAsync();
        string token = JsonDocument.Parse(loginJson).RootElement.GetProperty("AccessToken").GetString() ?? "";

        var bootReq = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/Bootstrap/app");
        bootReq.Headers.Add("Authorization", "Bearer " + token);
        sw.Restart();
        var bootResp = await http.SendAsync(bootReq);
        sw.Stop();
        metrics.RecordLatencyMs(sw.ElapsedMilliseconds);

        if (!CheckHttpStatus(bootResp.StatusCode, "bootstrap", metrics, logFile))
            return ("", "", false, $"bootstrap HTTP {bootResp.StatusCode}");

        string bootJson = await bootResp.Content.ReadAsStringAsync();
        string accountId = JsonDocument.Parse(bootJson).RootElement
            .GetProperty("account").GetProperty("summary").GetProperty("AccountId").GetString() ?? "";
        return (token, accountId, true, null);
    }

    private static async Task<(bool Ok, ERunState Verdict, string Summary)> RunSingleFuzzGame(
        int runIdx,
        EHero hero,
        string baseUrl,
        HttpClient http,
        CardMetadataCatalog catalog,
        bool preferWeakBoard,
        bool preferNonShopEncounters,
        bool useCustomRules,
        uint resumeTargetDay,
        uint resumeTargetHour,
        FuzzMetrics metrics,
        Action<string> logFile)
    {
        var rng = new Random(20261121 + runIdx * 97);
        string prefix = useCustomRules ? $"FuzzCust_{runIdx:D4}" : $"FuzzV11_{runIdx:D4}";
        string username = $"{prefix}_{Guid.NewGuid():N}"[..24];

        string token = "";
        string accountId = "";
        bool customRulesApplied = false;
        int expectedStartingHealth = 250;
        ERunState lastState = ERunState.Choice;

        try
        {
            var auth = await AuthenticateUserAsync(http, baseUrl, username, metrics, logFile);
            if (!auth.Ok) return (false, lastState, auth.Error ?? "auth failed");
            token = auth.Token;
            accountId = auth.AccountId;

            // 约 10% 对局使用独立隔离账号配置自定义规则，并在 finally 中严格 reset 防止污染
            if (useCustomRules)
            {
                Interlocked.Increment(ref metrics.CustomRuleRuns);

                var catReq = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/custom-rules/catalog");
                var catSw = Stopwatch.StartNew();
                var catResp = await http.SendAsync(catReq);
                catSw.Stop();
                metrics.RecordLatencyMs(catSw.ElapsedMilliseconds);
                if (!CheckHttpStatus(catResp.StatusCode, "custom-rules/catalog", metrics, logFile))
                    return (false, lastState, "custom-rules/catalog HTTP error");

                bool hpBoostVariant = ((runIdx / 10) % 2) == 1;
                expectedStartingHealth = hpBoostVariant ? 320 : 250;
                ulong archetypeBit = 1UL << (runIdx % 18);
                var rulePayload = new
                {
                    selectedArchetypeMask = archetypeBit,
                    archetypeWeightMultiplier = 2.5,
                    archetypeMultiMatchCap = 3.0,
                    startingHealth = expectedStartingHealth,
                    startingGold = hpBoostVariant ? 25 : 15,
                    startingIncome = hpBoostVariant ? 6 : 5,
                    monsterLootChooseOne = true,
                };

                var putReq = new HttpRequestMessage(HttpMethod.Put, baseUrl + "/custom-rules/me")
                {
                    Content = new StringContent(JsonSerializer.Serialize(rulePayload), Encoding.UTF8, "application/json"),
                };
                putReq.Headers.Add("Authorization", "Bearer " + token);
                var putSw = Stopwatch.StartNew();
                var putResp = await http.SendAsync(putReq);
                putSw.Stop();
                metrics.RecordLatencyMs(putSw.ElapsedMilliseconds);
                if (!CheckHttpStatus(putResp.StatusCode, "PUT custom-rules/me", metrics, logFile))
                    return (false, lastState, "PUT /custom-rules/me HTTP error");
                customRulesApplied = true;

                // 回读校验自定义规则已落盘到该独立账号
                var getRuleReq = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/custom-rules/me");
                getRuleReq.Headers.Add("Authorization", "Bearer " + token);
                var getRuleResp = await http.SendAsync(getRuleReq);
                if (!CheckHttpStatus(getRuleResp.StatusCode, "GET custom-rules/me", metrics, logFile))
                    return (false, lastState, "GET /custom-rules/me HTTP error");
                string ruleJson = await getRuleResp.Content.ReadAsStringAsync();
                using var rDoc = JsonDocument.Parse(ruleJson);
                if (!rDoc.RootElement.TryGetProperty("monsterLootChooseOne", out var mlc) || !mlc.GetBoolean())
                {
                    Interlocked.Increment(ref metrics.ProtocolErrorCount);
                    return (false, lastState, "custom rule monsterLootChooseOne did not persist");
                }
            }

            var driver = new RunSessionDriver(
                label: $"run#{runIdx:D4}",
                baseUrl: baseUrl,
                http: http,
                token: token,
                accountId: accountId,
                username: username,
                hero: hero,
                playMode: EPlayMode.Unranked,
                tournamentSessionId: null,
                catalog: catalog,
                metrics: metrics,
                rng: rng,
                preferWeakBoard: preferWeakBoard,
                preferNonShopEncounters: preferNonShopEncounters,
                enableResumeCheck: true,
                resumeTargetDay: resumeTargetDay,
                resumeTargetHour: resumeTargetHour,
                isCustomRuleRun: useCustomRules,
                customStartingHealth: expectedStartingHealth,
                logFile: logFile);

            var result = await driver.ExecuteRunAsync();
            lastState = result.Verdict;
            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref metrics.ExceptionCount);
            logFile($"[run#{runIdx:D4}] EXCEPTION: {ex}");
            return (false, lastState, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (customRulesApplied && !string.IsNullOrEmpty(token))
            {
                try
                {
                    var resetReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/custom-rules/me/reset");
                    resetReq.Headers.Add("Authorization", "Bearer " + token);
                    var resetResp = await http.SendAsync(resetReq);
                    if (!resetResp.IsSuccessStatusCode)
                    {
                        logFile($"[run#{runIdx:D4}] WARN: custom-rules reset returned {resetResp.StatusCode}");
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 单个玩家对局会话的完整协议驱动器与 12 项 Shadow Invariant 审计引擎（供单人 Fuzz 与多人锦标赛共同复用）。
    /// </summary>
    internal sealed class RunSessionDriver
    {
        private readonly string _label;
        private readonly string _baseUrl;
        private readonly HttpClient _http;
        private readonly string _token;
        private readonly string _accountId;
        private readonly string _username;
        private readonly EHero _hero;
        private readonly EPlayMode _playMode;
        private readonly Guid? _tournamentSessionId;
        private readonly CardMetadataCatalog _catalog;
        private readonly FuzzMetrics _metrics;
        private readonly Random _rng;
        private readonly bool _preferWeakBoard;
        private readonly bool _preferNonShopEncounters;
        private readonly bool _enableResumeCheck;
        private readonly uint _resumeTargetDay;
        private readonly uint _resumeTargetHour;
        private readonly bool _isCustomRuleRun;
        private readonly int _customStartingHealth;
        private readonly Action<string> _logFile;
        private readonly Func<uint, string, Task<bool>>? _beforeHour6PvpAsync;
        private readonly Action<GameStateSnapshotDTO, Dictionary<string, CardShadow>, Dictionary<EPlayerAttributeType, int>>? _onInitialSnapshot;
        private readonly Action<GameSim, GameSim, NetMessageCombatSim>? _onPvpCombatObserved;
        private readonly Func<GameSim, bool>? _isLiveHumanPvpOpponent;

        private const string LargeAppetitesTemplateId = "19c967e5-f4c0-4c11-bb96-aa039369600a";

        private string _sid = "";
        private long _rid;

        private readonly Dictionary<string, CardShadow> _cards = new(StringComparer.Ordinal);
        private readonly Dictionary<EPlayerAttributeType, int> _playerAttrs = new();
        private readonly List<string> _selectionSet = new();
        private TSelectionContextRules _selectionRules = new();
        private ERunState _state = ERunState.Choice;
        private string? _currentEncounterId;
        private uint _day = 1, _hour = 1, _victories, _defeats, _rerollCost = 2;
        private ushort _unlockedSlots = 0b00_0111_1000;
        private bool _hasVisitedFates;

        // 用于跨命令审计 XP / Level / MaxHP 单调性及 Large Appetites 条件光环阈值跨越
        private int _prevLevel = 1;
        private int _prevXp;
        private int _prevHealthMax = 250;
        private int _prevHandItemCount;
        private bool _baselineCaptured;
        private bool _inCombatOrPostCombatCheckPending;
        private string _lastCheckedShopSignature = "";
        private string _lastCheckedLevelUpSignature = "";

        public RunSessionDriver(
            string label,
            string baseUrl,
            HttpClient http,
            string token,
            string accountId,
            string username,
            EHero hero,
            EPlayMode playMode,
            Guid? tournamentSessionId,
            CardMetadataCatalog catalog,
            FuzzMetrics metrics,
            Random rng,
            bool preferWeakBoard,
            bool preferNonShopEncounters,
            bool enableResumeCheck,
            uint resumeTargetDay,
            uint resumeTargetHour,
            bool isCustomRuleRun,
            int customStartingHealth,
            Action<string> logFile,
            Func<uint, string, Task<bool>>? beforeHour6PvpAsync = null,
            Action<GameStateSnapshotDTO, Dictionary<string, CardShadow>, Dictionary<EPlayerAttributeType, int>>? onInitialSnapshot = null,
            Action<GameSim, GameSim, NetMessageCombatSim>? onPvpCombatObserved = null,
            Func<GameSim, bool>? isLiveHumanPvpOpponent = null)
        {
            _label = label;
            _baseUrl = baseUrl;
            _http = http;
            _token = token;
            _accountId = accountId;
            _username = username;
            _hero = hero;
            _playMode = playMode;
            _tournamentSessionId = tournamentSessionId;
            _catalog = catalog;
            _metrics = metrics;
            _rng = rng;
            _preferWeakBoard = preferWeakBoard;
            _preferNonShopEncounters = preferNonShopEncounters;
            _enableResumeCheck = enableResumeCheck;
            _resumeTargetDay = resumeTargetDay;
            _resumeTargetHour = resumeTargetHour;
            _isCustomRuleRun = isCustomRuleRun;
            _customStartingHealth = customStartingHealth;
            _logFile = logFile;
            _beforeHour6PvpAsync = beforeHour6PvpAsync;
            _onInitialSnapshot = onInitialSnapshot;
            _onPvpCombatObserved = onPvpCombatObserved;
            _isLiveHumanPvpOpponent = isLiveHumanPvpOpponent;
        }

        public async Task<(bool Ok, ERunState Verdict, string Summary)> ExecuteRunAsync()
        {
            try
            {
                if (!await OpenSessionAndSyncAsync(expectResume: false))
                    return (false, _state, "failed to open initial session");

                if (!CheckWlInvariant(_day, _hour, _state, _victories, _defeats, isTerminal: false, out string? initWlErr))
                {
                    Interlocked.Increment(ref _metrics.WlInvariantViolations);
                    _metrics.AddViolationSample($"[{_label}] {initWlErr}", _logFile);
                    return (false, _state, initWlErr!);
                }

                bool resumeTested = false;
                bool moveTestedThisHour = false;
                uint lastMoveHourKey = 0;
                int stepsClickedThisHour = 0;
                int pedestalAttemptsThisHour = 0;
                var blockedCandidates = new HashSet<string>(StringComparer.Ordinal);
                string lastProgressSig = "";
                int sameSigStreak = 0;
                const int MaxSteps = 700;

                for (int step = 0; step < MaxSteps; step++)
                {
                    if (_state is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
                    {
                        if (!CheckWlInvariant(_day, _hour, _state, _victories, _defeats, isTerminal: true, out string? termWlErr))
                        {
                            Interlocked.Increment(ref _metrics.WlInvariantViolations);
                            _metrics.AddViolationSample($"[{_label}] {termWlErr}", _logFile);
                            return (false, _state, termWlErr!);
                        }
                        if (_hasVisitedFates) Interlocked.Increment(ref _metrics.FatesVisitsObserved);
                        _logFile($"[{_label}] Finished hero={_hero} verdict={_state} Day={_day} Hour={_hour} W/L={_victories}/{_defeats} steps={step}");
                        return (true, _state, $"W/L={_victories}/{_defeats} D{_day}H{_hour} steps={step}");
                    }

                    if (!CheckWlInvariant(_day, _hour, _state, _victories, _defeats, isTerminal: false, out string? loopWlErr))
                    {
                        Interlocked.Increment(ref _metrics.WlInvariantViolations);
                        _metrics.AddViolationSample($"[{_label}] {loopWlErr}", _logFile);
                        return (false, _state, loopWlErr!);
                    }

                    // 半途断线 & /runs/active/summary & Resume Run 无损校验
                    if (_enableResumeCheck &&
                        !resumeTested &&
                        ((_day == _resumeTargetDay && _hour >= _resumeTargetHour) || _day > _resumeTargetDay) &&
                        _state is ERunState.Choice or ERunState.Encounter or ERunState.LevelUp)
                    {
                        resumeTested = true;
                        var pre = CaptureResumeCheckpoint(
                            _day, _hour, _victories, _defeats, _state, _currentEncounterId, _unlockedSlots,
                            _playerAttrs, _selectionSet, _cards);

                        var sumReq = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/runs/active/summary");
                        sumReq.Headers.Add("Authorization", "Bearer " + _token);
                        sumReq.Headers.Add("aid", _accountId.Replace("-", ""));
                        sumReq.Headers.Add("uid", _username);
                        var swSum = Stopwatch.StartNew();
                        var sumResp = await _http.SendAsync(sumReq);
                        swSum.Stop();
                        _metrics.RecordLatencyMs(swSum.ElapsedMilliseconds);
                        if (!CheckHttpStatus(sumResp.StatusCode, "runs/active/summary", _metrics, _logFile))
                            return (false, _state, "summary HTTP error");

                        string sumJson = await sumResp.Content.ReadAsStringAsync();
                        using (var doc = JsonDocument.Parse(sumJson))
                        {
                            var root = doc.RootElement;
                            bool hasActive = root.GetProperty("HasActiveRun").GetBoolean();
                            uint sDay = root.GetProperty("Day").GetUInt32();
                            uint sHour = root.GetProperty("Hour").GetUInt32();
                            if (!hasActive || sDay != pre.Day || sHour != pre.Hour)
                            {
                                Interlocked.Increment(ref _metrics.ResumeMismatchCount);
                                _metrics.AddViolationSample($"[{_label}] summary mismatch hasActive={hasActive} D{sDay}H{sHour} vs D{pre.Day}H{pre.Hour}", _logFile);
                                return (false, _state, $"summary mismatch hasActive={hasActive} D{sDay}H{sHour} vs D{pre.Day}H{pre.Hour}");
                            }
                        }

                        var delReq = new HttpRequestMessage(HttpMethod.Delete, _baseUrl + "/sessions");
                        delReq.Headers.Add("sid", _sid);
                        var delResp = await _http.SendAsync(delReq);
                        if (!CheckHttpStatus(delResp.StatusCode, "DELETE /sessions", _metrics, _logFile))
                            return (false, _state, "DELETE /sessions failed");

                        if (!await OpenSessionAndSyncAsync(expectResume: true))
                            return (false, _state, "Resume POST /sessions failed");

                        string? diff = CompareResumeCheckpoint(
                            pre, _day, _hour, _victories, _defeats, _state, _currentEncounterId, _unlockedSlots,
                            _playerAttrs, _selectionSet, _cards);
                        if (diff != null)
                        {
                            Interlocked.Increment(ref _metrics.ResumeMismatchCount);
                            _metrics.AddViolationSample($"[{_label}] RESUME MISMATCH: {diff}", _logFile);
                            return (false, _state, "Resume mismatch: " + diff);
                        }
                        Interlocked.Increment(ref _metrics.ResumeChecksPassed);
                        _logFile($"  [{_label}] Resume verified OK at D{_day}H{_hour} state={_state} cards={pre.OwnedCards.Count}");
                    }

                    // 防卡死检测
                    string sig = $"{_state}|{_day}|{_hour}|{_currentEncounterId}|{string.Join(",", _selectionSet)}|{blockedCandidates.Count}";
                    if (sig == lastProgressSig) sameSigStreak++;
                    else { sameSigStreak = 0; lastProgressSig = sig; }

                    if (sameSigStreak >= 22)
                    {
                        Interlocked.Increment(ref _metrics.StallCount);
                        _metrics.AddViolationSample($"[{_label}] Stalled at {sig}", _logFile);
                        return (false, _state, $"Stalled at {sig}");
                    }

                    uint hourKey = _day * 100 + _hour;
                    if (hourKey != lastMoveHourKey)
                    {
                        lastMoveHourKey = hourKey;
                        moveTestedThisHour = false;
                        stepsClickedThisHour = 0;
                        pedestalAttemptsThisHour = 0;
                        blockedCandidates.Clear();
                    }

                    int gold = _playerAttrs.GetValueOrDefault(EPlayerAttributeType.Gold, 0);

                    // 每小时在合适的非战斗状态执行一次 MoveItemCommand 测试局外换位与光环
                    if (!moveTestedThisHour &&
                        _state is ERunState.Choice or ERunState.Encounter)
                    {
                        var ownedItems = _cards.Values
                            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item)
                            .ToList();
                        if (ownedItems.Count > 0)
                        {
                            moveTestedThisHour = true;
                            var itemToMove = ownedItems[_rng.Next(ownedItems.Count)];
                            bool trySwap = ownedItems.Count >= 2 && _rng.NextDouble() < 0.35;
                            int targetSlot = PickTargetSlotForMove(itemToMove, ownedItems, _unlockedSlots, _rng, trySwap, out EInventorySection targetSec);
                            if (targetSlot >= 0)
                            {
                                if (trySwap) Interlocked.Increment(ref _metrics.MoveSwapsAttempted);
                                var mvCmd = new MoveItemCommand
                                {
                                    InstanceId = new InstanceId(itemToMove.InstanceId),
                                    TargetSockets = new List<EContainerSocketId> { (EContainerSocketId)targetSlot },
                                    Section = targetSec,
                                };
                                var (mvOk, _, _) = await SendCmdAsync(mvCmd, $"Move({itemToMove.InstanceId}->{targetSec}:{targetSlot})");
                                if (mvOk) Interlocked.Increment(ref _metrics.ItemsMoved);
                            }
                        }
                    }

                    // 锦标赛等待室状态支持
                    if (_state is ERunState.TournamentWaitingRoom or ERunState.TournamentPvpChoice)
                    {
                        if (_beforeHour6PvpAsync != null)
                        {
                            bool ready = await _beforeHour6PvpAsync(_day, _selectionSet.FirstOrDefault() ?? "");
                            if (!ready) return (false, _state, $"Tournament barrier stalled at D{_day}H{_hour}");
                        }
                        if (_selectionSet.Count > 0)
                        {
                            var cand = _cards.GetValueOrDefault(_selectionSet[0]);
                            if (cand != null)
                            {
                                var (pOk, pNa, _) = await SendCmdAsync(
                                    new SelectEncounterCommand { InstanceId = new InstanceId(cand.InstanceId) },
                                    "TournamentPvpSelect");
                                if (pOk) continue;
                                if (!pNa) return (false, _state, "TournamentPvpSelect error");
                            }
                        }
                        await Task.Delay(100);
                        continue;
                    }

                    if (_state == ERunState.Choice)
                    {
                        var cands = _selectionSet
                            .Where(id => !blockedCandidates.Contains(id))
                            .Select(id => _cards.GetValueOrDefault(id))
                            .Where(c => c != null)
                            .ToList();

                        if (cands.Count == 0)
                        {
                            if (_selectionRules.CanExit)
                            {
                                var (okEx, _, _) = await SendCmdAsync(new ExitCurrentStateCommand(), "ChoiceEmptyExit");
                                if (!okEx) return (false, _state, "Choice empty and exit failed");
                                continue;
                            }
                            Interlocked.Increment(ref _metrics.StallCount);
                            _metrics.AddViolationSample($"[{_label}] Choice empty with CanExit=false at D{_day}H{_hour}", _logFile);
                            return (false, _state, $"Choice empty with CanExit=false at D{_day}H{_hour}");
                        }

                        CardShadow picked;
                        if (_preferNonShopEncounters)
                        {
                            picked = cands.FirstOrDefault(c => c!.Type == ECardType.PvpEncounter)
                                     ?? cands.FirstOrDefault(c => c!.Type == ECardType.PedestalEncounter)
                                     ?? cands.FirstOrDefault(c => c!.Type == ECardType.EventEncounter && _rng.NextDouble() < 0.7)
                                     ?? cands[_rng.Next(cands.Count)]!;
                        }
                        else
                        {
                            picked = cands.FirstOrDefault(c => c!.Type == ECardType.PvpEncounter)
                                     ?? cands[_rng.Next(cands.Count)]!;
                        }

                        // 若在锦标赛中即将点击 Hour 6 PvP，先协调多真人同步屏障
                        if (picked.Type == ECardType.PvpEncounter && _beforeHour6PvpAsync != null)
                        {
                            bool barrierReady = await _beforeHour6PvpAsync(_day, picked.InstanceId);
                            if (!barrierReady)
                            {
                                Interlocked.Increment(ref _metrics.StallCount);
                                return (false, _state, $"Tournament Hour 6 barrier timeout at D{_day}");
                            }
                        }

                        Interlocked.Increment(ref _metrics.EncounterChoices);
                        var (encOk, notAllowed, _) = await SendCmdAsync(
                            new SelectEncounterCommand { InstanceId = new InstanceId(picked.InstanceId) },
                            $"SelectEncounter({picked.Type})");
                        if (!encOk)
                        {
                            if (notAllowed)
                            {
                                // 锦标赛 Hour 6 多真人屏障拦截属于暂时阻塞，不能把 PvpEncounter 永久加入 blockedCandidates
                                if (picked.Type == ECardType.PvpEncounter && _beforeHour6PvpAsync != null)
                                {
                                    await Task.Delay(120);
                                }
                                else
                                {
                                    blockedCandidates.Add(picked.InstanceId);
                                }
                            }
                            else
                            {
                                return (false, _state, "SelectEncounter fatal error");
                            }
                        }
                        continue;
                    }

                    if (_state == ERunState.Pedestal)
                    {
                        var ownedItems = _cards.Values
                            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item)
                            .ToList();

                        // 智能挑选最适配的祭坛目标：优先无附魔卡或低阶可升阶卡
                        var encMeta = _catalog.Get(_currentEncounterId);
                        bool looksEnchant = encMeta != null && encMeta.InternalName.Contains("Enchant", StringComparison.OrdinalIgnoreCase);
                        var orderedTargets = looksEnchant
                            ? ownedItems.OrderBy(c => c.Enchantment == null ? 0 : 1).ThenBy(c => c.Tier).ToList()
                            : ownedItems.OrderBy(c => c.Tier >= ETier.Diamond ? 1 : 0).ThenBy(c => c.Tier).ToList();

                        var target = orderedTargets.FirstOrDefault(c => !blockedCandidates.Contains("ped:" + c.InstanceId));
                        if (target != null && (pedestalAttemptsThisHour < 4 || !_selectionRules.CanExit))
                        {
                            pedestalAttemptsThisHour++;
                            var (pedOk, pedNa, _) = await SendCmdAsync(
                                new CommitToPedestalCommand { InstanceId = new InstanceId(target.InstanceId) },
                                $"CommitPedestal({target.InstanceId})");
                            if (pedOk) { Interlocked.Increment(ref _metrics.PedestalsCommitted); continue; }
                            if (pedNa) { blockedCandidates.Add("ped:" + target.InstanceId); continue; }
                            return (false, _state, "CommitToPedestal error");
                        }

                        if (!_selectionRules.CanExit && _selectionSet.Count > 0)
                            return (false, _state, $"Pedestal cannot commit or exit (CanExit=false) at D{_day}H{_hour}");
                        var (exOk, _, _) = await SendCmdAsync(new ExitCurrentStateCommand(), "ExitPedestal");
                        if (!exOk) return (false, _state, "ExitPedestal failed");
                        continue;
                    }

                    if (_state == ERunState.LevelUp)
                    {
                        var lvlCand = _selectionSet
                            .Where(id => !blockedCandidates.Contains(id))
                            .Select(id => _cards.GetValueOrDefault(id))
                            .FirstOrDefault(c => c != null);
                        if (lvlCand == null)
                        {
                            if (_selectionRules.CanExit || _selectionSet.Count == 0)
                            {
                                var (exOk, _, _) = await SendCmdAsync(new ExitCurrentStateCommand(), "ExitLevelUp");
                                if (!exOk) return (false, _state, "ExitLevelUp failed");
                                continue;
                            }
                            return (false, _state, $"LevelUp has no selectable candidate with CanExit=false at D{_day}H{_hour}");
                        }

                        INetCommand pickCmd = lvlCand.Type switch
                        {
                            ECardType.Skill => new SelectSkillCommand { InstanceId = new InstanceId(lvlCand.InstanceId) },
                            ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter
                                => new SelectEncounterCommand { InstanceId = new InstanceId(lvlCand.InstanceId) },
                            _ => BuildSmartSelectItemCommand(lvlCand, preferStash: false),
                        };
                        var (okLvl, naLvl, _) = await SendCmdAsync(pickCmd, $"LevelUpPick({lvlCand.Type})");
                        if (okLvl) Interlocked.Increment(ref _metrics.LevelUpsCompleted);
                        else if (naLvl) blockedCandidates.Add(lvlCand.InstanceId);
                        else return (false, _state, "LevelUpPick error");
                        continue;
                    }

                    if (_state == ERunState.Loot)
                    {
                        var lootCands = _selectionSet
                            .Where(id => !blockedCandidates.Contains(id))
                            .Select(id => _cards.GetValueOrDefault(id))
                            .Where(c => c != null)
                            .ToList();

                        if (_selectionRules.CanSelectMultiple && lootCands.Count >= 2)
                            Interlocked.Increment(ref _metrics.MultiPickLootEvents);

                        if (lootCands.Count > 0 && !_preferWeakBoard)
                        {
                            // 优先技能，其次可放入 Hand 或 Stash 的战利品
                            var pick = lootCands.FirstOrDefault(c => c!.Type == ECardType.Skill);
                            if (pick == null)
                            {
                                foreach (var c in lootCands)
                                {
                                    if (c!.Type != ECardType.Item || CanFitItemAnywhere(c))
                                    {
                                        pick = c;
                                        break;
                                    }
                                }
                            }

                            if (pick != null)
                            {
                                INetCommand cmd = pick.Type switch
                                {
                                    ECardType.Skill => new SelectSkillCommand { InstanceId = new InstanceId(pick.InstanceId) },
                                    ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter
                                        => new SelectEncounterCommand { InstanceId = new InstanceId(pick.InstanceId) },
                                    _ => BuildSmartSelectItemCommand(pick, preferStash: !CanFitInSection(pick, EInventorySection.Hand)),
                                };
                                var (okLoot, naLoot, _) = await SendCmdAsync(cmd, $"LootPick({pick.Type})");
                                if (okLoot) { Interlocked.Increment(ref _metrics.PveLootSkillsOrItems); continue; }
                                if (naLoot) { blockedCandidates.Add(pick.InstanceId); continue; }
                                return (false, _state, "LootPick error");
                            }
                        }

                        if (_selectionRules.CanExit || _selectionSet.Count == 0)
                        {
                            var (exLootOk, _, _) = await SendCmdAsync(new ExitCurrentStateCommand(), "ExitLoot");
                            if (!exLootOk) return (false, _state, "ExitLoot failed");
                            continue;
                        }
                        return (false, _state, $"Loot has no selectable candidate with CanExit=false at D{_day}H{_hour}");
                    }

                    // ERunState.Encounter（包含商店、奇遇事件、EncounterStep）
                    var shelfCands = _selectionSet
                        .Where(id => !blockedCandidates.Contains(id))
                        .Select(id => _cards.GetValueOrDefault(id))
                        .Where(c => c != null)
                        .ToList();

                    if (_selectionRules.CanSelectMultiple && _selectionRules.SelectionIsFree && shelfCands.Count >= 2)
                        Interlocked.Increment(ref _metrics.MultiPickLootEvents);

                    var ownedList = _cards.Values
                        .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item)
                        .ToList();

                    // 弱阵型分支（触达战败折算 / Fates / EndRunDefeat）与常规卖卡/消耗品
                    if (_preferWeakBoard && _day == 1 && ownedList.Count > 0)
                    {
                        var toSell = ownedList.FirstOrDefault(c => !IsUnsellable(c) && !blockedCandidates.Contains("sell:" + c.InstanceId));
                        if (toSell != null)
                        {
                            var (sOk, sNa, _) = await SendCmdAsync(new SellCardCommand { InstanceId = new InstanceId(toSell.InstanceId) }, "WeakSell");
                            if (sOk) { Interlocked.Increment(ref _metrics.ItemsSoldOrConsumed); continue; }
                            if (sNa) { blockedCandidates.Add("sell:" + toSell.InstanceId); }
                            else return (false, _state, "WeakSell failed");
                        }
                    }
                    else if (ownedList.Count >= 4 && _rng.NextDouble() < 0.35 && !blockedCandidates.Contains("sold_this_turn"))
                    {
                        // 优先卖掉/消耗 Stash 中的 Loot 消耗品或小件，为商店留出空间并测试 OnCardSoldOrConsumed
                        var sellTarget = ownedList
                            .Where(c => !IsUnsellable(c) && !blockedCandidates.Contains("sell:" + c.InstanceId))
                            .OrderBy(c => c.Section == EInventorySection.Stash ? 0 : 1)
                            .ThenBy(c => IsLootOrConsumable(c) ? 0 : 1)
                            .ThenBy(c => c.Size)
                            .FirstOrDefault();

                        if (sellTarget != null)
                        {
                            blockedCandidates.Add("sold_this_turn");
                            var (sOk, sNa, _) = await SendCmdAsync(new SellCardCommand { InstanceId = new InstanceId(sellTarget.InstanceId) }, "SellOrConsume");
                            if (sOk) { Interlocked.Increment(ref _metrics.ItemsSoldOrConsumed); continue; }
                            if (sNa) blockedCandidates.Add("sell:" + sellTarget.InstanceId);
                        }
                    }

                    // 1) EncounterStep 选项（当 CanExit=true 时最多连续点选 2 次可链式重抽的步骤，避免在 Gumball Machine 等长链事件耗时空转）
                    var stepCand = (!_selectionRules.CanExit || stepsClickedThisHour < 2)
                        ? shelfCands.FirstOrDefault(c =>
                            c!.Type == ECardType.EncounterStep &&
                            (_selectionRules.SelectionIsFree || c.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice) <= gold))
                        : null;
                    if (stepCand != null)
                    {
                        stepsClickedThisHour++;
                        var (stOk, stNa, _) = await SendCmdAsync(
                            new SelectEncounterCommand { InstanceId = new InstanceId(stepCand.InstanceId) },
                            $"EncounterStep({stepCand.TemplateId})");
                        if (stOk) { Interlocked.Increment(ref _metrics.EncounterStepsExecuted); continue; }
                        if (stNa) { blockedCandidates.Add(stepCand.InstanceId); continue; }
                        return (false, _state, "EncounterStep fatal error");
                    }

                    // 2) Skill 选项
                    var skillCand = shelfCands.FirstOrDefault(c =>
                        c!.Type == ECardType.Skill &&
                        (_selectionRules.SelectionIsFree || c.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 4) <= gold));
                    if (skillCand != null)
                    {
                        var (skOk, skNa, _) = await SendCmdAsync(
                            new SelectSkillCommand { InstanceId = new InstanceId(skillCand.InstanceId) },
                            "SelectSkill");
                        if (skOk) continue;
                        if (skNa) { blockedCandidates.Add(skillCand.InstanceId); continue; }
                        return (false, _state, "SelectSkill fatal error");
                    }

                    // 3) 商店/奇遇买卡（尊重 Hand 剩余空格防止溢出；优先二合一升阶；对部分买入刻意走 Stash）
                    if (!_preferWeakBoard)
                    {
                        var ownedMaxTiers = new Dictionary<string, ETier>(StringComparer.OrdinalIgnoreCase);
                        foreach (var oc in ownedList)
                        {
                            if (!string.IsNullOrEmpty(oc.TemplateId) &&
                                (!ownedMaxTiers.TryGetValue(oc.TemplateId, out var curT) || oc.Tier > curT))
                            {
                                ownedMaxTiers[oc.TemplateId] = oc.Tier;
                            }
                        }

                        var affordableItems = shelfCands
                            .Where(c => c!.Type == ECardType.Item &&
                                        (_selectionRules.SelectionIsFree || c.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice) <= gold))
                            .ToList();

                        // 优先挑能触发二合一升阶的同名同阶/高阶物品，否则挑能放进当前空位的物品
                        CardShadow? itemCand = null;
                        bool isExpectedMerge = false;
                        foreach (var cand in affordableItems)
                        {
                            if (!string.IsNullOrEmpty(cand!.TemplateId) &&
                                ownedMaxTiers.TryGetValue(cand.TemplateId, out var ownT) &&
                                ownT < ETier.Diamond && ownT <= cand.Tier)
                            {
                                itemCand = cand;
                                isExpectedMerge = true;
                                break;
                            }
                        }
                        itemCand ??= affordableItems.FirstOrDefault(c => CanFitItemAnywhere(c!));

                        if (itemCand != null && (ownedList.Count < 8 || isExpectedMerge || _rng.NextDouble() < 0.25))
                        {
                            bool canHand = CanFitInSection(itemCand, EInventorySection.Hand);
                            bool canStash = CanFitInSection(itemCand, EInventorySection.Stash);

                            if (canHand || canStash || isExpectedMerge)
                            {
                                bool preferStash = !canHand || (canStash && _rng.NextDouble() < 0.25);
                                var buyCmd = BuildSmartSelectItemCommand(itemCand, preferStash);
                                var (bOk, bNa, _) = await SendCmdAsync(buyCmd, $"BuyItem({itemCand.TemplateId}->{buyCmd.Section})");
                                if (bOk)
                                {
                                    Interlocked.Increment(ref _metrics.ItemsPurchased);
                                    if (buyCmd.Section == EInventorySection.Stash) Interlocked.Increment(ref _metrics.ItemsPurchasedToStash);
                                    else Interlocked.Increment(ref _metrics.ItemsPurchasedToHand);
                                    if (isExpectedMerge) Interlocked.Increment(ref _metrics.UpgradeMergesObserved);
                                    continue;
                                }
                                if (bNa) { blockedCandidates.Add(itemCand.InstanceId); continue; }
                                return (false, _state, "BuyItem fatal error");
                            }
                        }
                    }

                    // 4) 偶尔刷新一次商店
                    if (_selectionRules.CanExit &&
                        _selectionRules.RerollRules != null &&
                        _rerollCost > 0 && _rerollCost <= 6 &&
                        gold >= _rerollCost &&
                        !blockedCandidates.Contains("rerolled") &&
                        _rng.NextDouble() < 0.30 &&
                        shelfCands.Any(c => c!.Type == ECardType.Item))
                    {
                        blockedCandidates.Add("rerolled");
                        var (rrOk, rrNa, _) = await SendCmdAsync(new RerollCommand(), "Reroll");
                        if (rrOk) { Interlocked.Increment(ref _metrics.Rerolls); continue; }
                        if (!rrNa) return (false, _state, "Reroll fatal error");
                    }

                    // 5) 子遭遇跳转
                    var subEncCand = shelfCands.FirstOrDefault(c =>
                        c!.Type is ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter);
                    if (subEncCand != null && !_selectionRules.CanExit)
                    {
                        var (seOk, seNa, _) = await SendCmdAsync(
                            new SelectEncounterCommand { InstanceId = new InstanceId(subEncCand.InstanceId) },
                            $"SubEncounter({subEncCand.Type})");
                        if (seOk) continue;
                        if (seNa) { blockedCandidates.Add(subEncCand.InstanceId); continue; }
                        return (false, _state, "SubEncounter fatal error");
                    }

                    // 6) 退出当前商店/奇遇推进小时
                    if (_selectionRules.CanExit)
                    {
                        var (exOk, _, _) = await SendCmdAsync(new ExitCurrentStateCommand(), "ExitEncounter");
                        if (!exOk) return (false, _state, "ExitEncounter failed");
                        continue;
                    }

                    // 7) 兜底：CanExit == false 时逐一尝试可用候选；若为强制物品且空间/碎片或金币受限，先尝试备选分区、跨区移卡或售卖腾挪
                    if (shelfCands.Count > 0)
                    {
                        var anyCand = shelfCands[0]!;
                        INetCommand fallbackCmd = anyCand.Type switch
                        {
                            ECardType.Skill => new SelectSkillCommand { InstanceId = new InstanceId(anyCand.InstanceId) },
                            ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter
                                => new SelectEncounterCommand { InstanceId = new InstanceId(anyCand.InstanceId) },
                            _ => BuildSmartSelectItemCommand(anyCand, preferStash: !CanFitInSection(anyCand, EInventorySection.Hand)),
                        };
                        var (anyOk, anyNa, _) = await SendCmdAsync(fallbackCmd, $"FallbackSelect({anyCand.Type})");
                        if (anyOk) continue;
                        if (anyNa)
                        {
                            if (anyCand.Type == ECardType.Item && fallbackCmd is SelectItemCommand primaryItemCmd)
                            {
                                var altSection = primaryItemCmd.Section == EInventorySection.Stash
                                    ? EInventorySection.Hand
                                    : EInventorySection.Stash;
                                string altKey = $"altsec:{anyCand.InstanceId}";
                                if (!blockedCandidates.Contains(altKey) && CountFreeSlots(altSection) > 0)
                                {
                                    blockedCandidates.Add(altKey);
                                    int altSlot = FindFirstFreeSlot(CardWidth(anyCand.Size), altSection);
                                    var altCmd = altSlot >= 0
                                        ? new SelectItemCommand
                                        {
                                            InstanceId = new InstanceId(anyCand.InstanceId),
                                            Section = altSection,
                                            TargetSockets = new List<EContainerSocketId> { (EContainerSocketId)altSlot },
                                        }
                                        : new SelectItemCommand
                                        {
                                            InstanceId = new InstanceId(anyCand.InstanceId),
                                            Section = altSection,
                                        };
                                    var (altOk, altNa, _) = await SendCmdAsync(altCmd, $"FallbackSelectAlt({altSection})");
                                    if (altOk) continue;
                                    if (!altNa) return (false, _state, "FallbackSelectAlt fatal error");
                                }

                                // 若两侧存在零散空位且总空位足够，尝试把一侧小件搬运到另一侧腾出连续槽位
                                string xferKey = $"xfer:{anyCand.InstanceId}";
                                if (!blockedCandidates.Contains(xferKey))
                                {
                                    blockedCandidates.Add(xferKey);
                                    int needW = CardWidth(anyCand.Size);
                                    int handFree = CountFreeSlots(EInventorySection.Hand);
                                    int stashFree = CountFreeSlots(EInventorySection.Stash);
                                    if (handFree + stashFree >= needW && TryFindCrossSectionMove(ownedList, handFree, stashFree, needW, out var xferCmd))
                                    {
                                        var (mvOk, mvNa, _) = await SendCmdAsync(xferCmd, "MandatoryRoomMove");
                                        if (mvOk) { Interlocked.Increment(ref _metrics.ItemsMoved); continue; }
                                        if (!mvNa) return (false, _state, "MandatoryRoomMove fatal error");
                                    }
                                }

                                // 仍然放不下或缺少金币时，优先出售一件非核心 Stash/Loot/小件物品腾出位置与金币
                                var mandatorySell = ownedList
                                    .Where(c => !IsUnsellable(c) && !blockedCandidates.Contains("sell:" + c.InstanceId))
                                    .OrderBy(c => c.Section == EInventorySection.Stash ? 0 : 1)
                                    .ThenBy(c => IsLootOrConsumable(c) ? 0 : 1)
                                    .ThenBy(c => c.Size)
                                    .ThenBy(c => c.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice, 0))
                                    .FirstOrDefault();
                                if (mandatorySell != null)
                                {
                                    var (msOk, msNa, _) = await SendCmdAsync(
                                        new SellCardCommand { InstanceId = new InstanceId(mandatorySell.InstanceId) },
                                        "MandatoryRoomSell");
                                    if (msOk) { Interlocked.Increment(ref _metrics.ItemsSoldOrConsumed); continue; }
                                    if (msNa) { blockedCandidates.Add("sell:" + mandatorySell.InstanceId); continue; }
                                    return (false, _state, "MandatoryRoomSell fatal error");
                                }
                            }

                            blockedCandidates.Add(anyCand.InstanceId);
                            continue;
                        }
                        return (false, _state, "FallbackSelect fatal error");
                    }

                    Interlocked.Increment(ref _metrics.StallCount);
                    _metrics.AddViolationSample($"[{_label}] Locked in state={_state} enc={_currentEncounterId} CanExit=false no options", _logFile);
                    return (false, _state, $"Locked in state={_state} enc={_currentEncounterId} with CanExit=false");
                }

                Interlocked.Increment(ref _metrics.StallCount);
                _metrics.AddViolationSample($"[{_label}] Exceeded MaxSteps={MaxSteps} at D{_day}H{_hour} state={_state}", _logFile);
                return (false, _state, $"Exceeded MaxSteps={MaxSteps} at D{_day}H{_hour} state={_state}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(_sid))
                {
                    try
                    {
                        await _http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, _baseUrl + "/sessions")
                        {
                            Headers = { { "sid", _sid } }
                        });
                    }
                    catch { }
                }
            }
        }

        private async Task<bool> OpenSessionAndSyncAsync(bool expectResume)
        {
            var init = new InitializeRunCommand
            {
                PlayMode = _playMode,
                SelectedHero = _hero,
                TournamentSessionId = _tournamentSessionId,
            };
            byte[] initBytes = MessagePackSerializer.Serialize<INetCommand>(init, MessagePackConfig.Options);
            var sessReq = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/sessions")
            {
                Content = new ByteArrayContent(initBytes)
            };
            sessReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/msgpack");
            sessReq.Headers.Add("Authorization", "Bearer " + _token);
            sessReq.Headers.Add("aid", _accountId.Replace("-", ""));
            sessReq.Headers.Add("uid", _username);

            var sw = Stopwatch.StartNew();
            var sessResp = await _http.SendAsync(sessReq);
            sw.Stop();
            _metrics.RecordLatencyMs(sw.ElapsedMilliseconds);

            if (!CheckHttpStatus(sessResp.StatusCode, expectResume ? "resume_sessions" : "sessions", _metrics, _logFile))
                return false;

            byte[] sessBody = await sessResp.Content.ReadAsByteArrayAsync();
            _sid = sessResp.Headers.TryGetValues("sid", out var sv) ? sv.FirstOrDefault() ?? "" : "";
            if (string.IsNullOrEmpty(_sid))
            {
                Interlocked.Increment(ref _metrics.ProtocolErrorCount);
                _metrics.AddViolationSample($"[{_label}] missing sid header on /sessions", _logFile);
                return false;
            }
            _rid = 0;

            var msg = MessagePackSerializer.Deserialize<INetMessage>(sessBody, MessagePackConfig.Options);
            if (msg is not NetMessageAggregate agg)
            {
                Interlocked.Increment(ref _metrics.ProtocolErrorCount);
                _metrics.AddViolationSample($"[{_label}] /sessions reply is not NetMessageAggregate", _logFile);
                return false;
            }

            var sync = agg.Messages.OfType<NetMessageGameStateSync>().FirstOrDefault()?.Data;
            var sim = agg.Messages.OfType<NetMessageGameSim>().LastOrDefault()?.Data;
            if (sync == null || sim == null)
            {
                Interlocked.Increment(ref _metrics.ProtocolErrorCount);
                _metrics.AddViolationSample($"[{_label}] /sessions missing sync or sim", _logFile);
                return false;
            }

            ApplySnapshot(sync);
            ApplyGameSim(sim);

            if (!expectResume)
            {
                _prevLevel = _playerAttrs.GetValueOrDefault(EPlayerAttributeType.Level, 1);
                _prevXp = _playerAttrs.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
                _prevHealthMax = _playerAttrs.GetValueOrDefault(EPlayerAttributeType.HealthMax, _customStartingHealth);
                _prevHandItemCount = CountPlayerHandItems();
                _baselineCaptured = true;
                _onInitialSnapshot?.Invoke(sync, _cards, _playerAttrs);
            }

            AuditNonCombatInvariants(afterCombat: false);
            return true;
        }

        private async Task<(bool Ok, bool IsNotAllowed, INetMessage? Msg)> SendCmdAsync(INetCommand cmd, string label)
        {
            Interlocked.Increment(ref _metrics.TotalCommands);
            byte[] bytes = MessagePackSerializer.Serialize<INetCommand>(cmd, MessagePackConfig.Options);
            var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/commands")
            {
                Content = new ByteArrayContent(bytes)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/msgpack");
            req.Headers.Add("sid", _sid);
            req.Headers.Add("rid", (_rid++).ToString());
            req.Headers.Add("Authorization", "Bearer " + _token);
            req.Headers.Add("aid", _accountId.Replace("-", ""));
            req.Headers.Add("uid", _username);

            var sw = Stopwatch.StartNew();
            var resp = await _http.SendAsync(req);
            sw.Stop();
            _metrics.RecordLatencyMs(sw.ElapsedMilliseconds);

            if (!CheckHttpStatus(resp.StatusCode, label, _metrics, _logFile))
                return (false, false, null);

            byte[] body = await resp.Content.ReadAsByteArrayAsync();
            var msg = MessagePackSerializer.Deserialize<INetMessage>(body, MessagePackConfig.Options);
            if (msg is NetMessageError err)
            {
                if (err.ErrorType == EResponseErrorType.NotAllowed)
                {
                    _logFile($"  [{_label}] NotAllowed on {label}: {err.Message}");
                    return (false, true, err);
                }
                Interlocked.Increment(ref _metrics.ProtocolErrorCount);
                _metrics.AddViolationSample($"[{_label}] NetMessageError on {label}: {err.ErrorType} {err.Message}", _logFile);
                return (false, false, err);
            }

            Dictionary<string, (int Dmg, int Shd, int Burn, int Sell)>? preCombatStats = null;
            bool isCombatReply = msg is NetMessageAggregate aggMsg &&
                                 aggMsg.Messages.OfType<NetMessageCombatSim>().Any();
            if (isCombatReply)
            {
                preCombatStats = new Dictionary<string, (int, int, int, int)>(StringComparer.Ordinal);
                foreach (var c in _cards.Values.Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item))
                {
                    preCombatStats[c.InstanceId] = (
                        c.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount),
                        c.Attributes.GetValueOrDefault(ECardAttributeType.ShieldApplyAmount),
                        c.Attributes.GetValueOrDefault(ECardAttributeType.BurnApplyAmount),
                        c.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice));
                }
            }

            ApplyMessage(msg);

            bool isTerminalAfter = _state is ERunState.EndRunVictory or ERunState.EndRunDefeat;
            if (!CheckWlInvariant(_day, _hour, _state, _victories, _defeats, isTerminalAfter, out string? wlErr))
            {
                Interlocked.Increment(ref _metrics.WlInvariantViolations);
                _metrics.AddViolationSample($"[{_label}] after {label}: {wlErr}", _logFile);
                return (false, false, msg);
            }

            if (isCombatReply && msg is NetMessageAggregate combatAgg)
            {
                var spawnSim = combatAgg.Messages.OfType<NetMessageGameSim>().FirstOrDefault()?.Data;
                var combatSimMsg = combatAgg.Messages.OfType<NetMessageCombatSim>().FirstOrDefault();
                var combatSim = combatSimMsg?.Data;
                var despawnSim = combatAgg.Messages.OfType<NetMessageGameSim>().LastOrDefault()?.Data;

                if (spawnSim == null || combatSim == null || despawnSim == null || combatSim.Frames.Count < 1)
                {
                    Interlocked.Increment(ref _metrics.ProtocolErrorCount);
                    _metrics.AddViolationSample($"[{_label}] INVALID 3-part combat sequence on {label}", _logFile);
                    return (false, false, msg);
                }

                bool isPvp = spawnSim.CurrentState?.StateName == ERunState.PVPCombat;
                if (isPvp)
                {
                    Interlocked.Increment(ref _metrics.PvpCombats);
                    if (combatSim.Winner == ECombatantId.Player) Interlocked.Increment(ref _metrics.PvpWins);
                    else Interlocked.Increment(ref _metrics.PvpLosses);

                    // [Invariant 12] PvP Ghost 校验：Opponent HealthMax 处于合理解区间，且至少拥有 1 张盘面卡牌
                    AuditPvpOpponent(spawnSim);
                    _onPvpCombatObserved?.Invoke(spawnSim, despawnSim, combatSimMsg!);
                }
                else
                {
                    Interlocked.Increment(ref _metrics.PveCombats);
                    if (combatSim.Winner == ECombatantId.Player)
                    {
                        Interlocked.Increment(ref _metrics.PveWins);
                    }
                    else
                    {
                        Interlocked.Increment(ref _metrics.PveLosses);
                        bool hasPveRewardEv = combatSim.Frames.Any(f =>
                            f.Events.Any(e => e is CombatSimEventMonsterGoldReceived or CombatSimEventMonsterXpReceived));
                        if (hasPveRewardEv || combatSim.OpponentHealthThresholdsForGold.Count > 0)
                            Interlocked.Increment(ref _metrics.PveDefeatPartialRewards);
                    }
                }

                if (preCombatStats != null)
                {
                    foreach (var (cid, pre) in preCombatStats)
                    {
                        if (!_cards.TryGetValue(cid, out var post)) continue;
                        int dDmg = post.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount) - pre.Dmg;
                        int dShd = post.Attributes.GetValueOrDefault(ECardAttributeType.ShieldApplyAmount) - pre.Shd;
                        int dBurn = post.Attributes.GetValueOrDefault(ECardAttributeType.BurnApplyAmount) - pre.Burn;
                        int dSell = post.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice) - pre.Sell;
                        if (dDmg > 0 || dShd > 0 || dBurn > 0 || dSell > 0)
                            Interlocked.Increment(ref _metrics.PermCombatGrowthObserved);
                    }
                }

                _inCombatOrPostCombatCheckPending = true;
            }

            // 执行全部局外/战后 Shadow Invariants 校验
            AuditNonCombatInvariants(afterCombat: isCombatReply || _inCombatOrPostCombatCheckPending);
            if (_state is ERunState.Choice or ERunState.Loot or ERunState.LevelUp or ERunState.Encounter or ERunState.Pedestal)
            {
                _inCombatOrPostCombatCheckPending = false;
            }

            return (true, false, msg);
        }

        private void AuditPvpOpponent(GameSim spawnSim)
        {
            int oppHpMax = spawnSim.Opponent?.Attributes?.GetValueOrDefault(EPlayerAttributeType.HealthMax, 0) ?? 0;
            int combatDay = (int)(spawnSim.Run?.Day ?? _day);
            int dayBucket = Math.Clamp(combatDay, 1, 30);
            int baseUpper = MaxSaneGhostHealthForDay(dayBucket);
            if (_isCustomRuleRun || _playMode == EPlayMode.Tournament)
            {
                int delta = Math.Max(0, _customStartingHealth - 250);
                double ratio = _customStartingHealth > 250 ? (_customStartingHealth / 250.0) * 1.35 : 1.35;
                baseUpper = Math.Max(baseUpper + delta, (int)Math.Ceiling(baseUpper * ratio));
            }

            bool isTournamentLiveHuman = _playMode == EPlayMode.Tournament
                && _isLiveHumanPvpOpponent?.Invoke(spawnSim) == true
                && !IsGhostPoolSpawnSim(spawnSim);
            int effectiveUpper = isTournamentLiveHuman ? 255000 : baseUpper;

            var spawnedOppTypes = new Dictionary<string, ECardType>(StringComparer.Ordinal);
            foreach (var ev in spawnSim.Events)
            {
                if (ev is GameSimEventCardSpawned sp && sp.CombatantId == ECombatantId.Opponent)
                    spawnedOppTypes[sp.InstanceId] = sp.Type;
            }

            int oppBoardCards = 0;
            if (spawnSim.Cards != null)
            {
                foreach (var (cid, delta) in spawnSim.Cards)
                {
                    if (delta.Placement?.Owner == ECombatantId.Opponent &&
                        delta.Placement?.Section != EInventorySection.Stash)
                    {
                        var tp = spawnedOppTypes.GetValueOrDefault(cid, InferCardTypeFromId(cid));
                        if (tp == ECardType.Item)
                            oppBoardCards++;
                    }
                }
            }

            if (oppHpMax <= 0 || oppHpMax > effectiveUpper || oppBoardCards < 1)
            {
                Interlocked.Increment(ref _metrics.PvpGhostViolationCount);
                _metrics.AddViolationSample(
                    $"[{_label}] PVP_GHOST_VIOLATION at D{_day}H{_hour}: oppHpMax={oppHpMax} (upper={effectiveUpper}, liveHuman={isTournamentLiveHuman}), oppBoardItems={oppBoardCards}, oppName={spawnSim.CurrentState?.PvpOpponent?.Name}",
                    _logFile);
            }
        }

        private static bool IsGhostPoolSpawnSim(GameSim spawnSim)
        {
            foreach (var ev in spawnSim.Events)
            {
                if (ev is GameSimEventCardSpawned sp && sp.CombatantId == ECombatantId.Opponent && sp.Type == ECardType.Item)
                {
                    if (sp.InstanceId.StartsWith("pvp_fb_", StringComparison.Ordinal))
                        return true;
                    // PvpGhostPool.ToPlayerState 生成固定格式 opp_{idx}_{slot}_{prefix}
                    var parts = sp.InstanceId.Split('_');
                    if (parts.Length >= 4 && parts[0] == "opp" && int.TryParse(parts[1], out _) && int.TryParse(parts[2], out _))
                        return true;
                }
            }
            return false;
        }

        private int CountPlayerHandItems()
            => _cards.Values.Count(c =>
                c.Owner == ECombatantId.Player &&
                c.Type == ECardType.Item &&
                c.Section != EInventorySection.Stash);

        private int GetMaxLargeAppetitesDrop()
        {
            int maxDrop = 0;
            foreach (var c in _cards.Values)
            {
                if (c.Owner != ECombatantId.Player || c.Type != ECardType.Skill) continue;
                if (!string.Equals(c.TemplateId, LargeAppetitesTemplateId, StringComparison.OrdinalIgnoreCase)) continue;
                maxDrop += c.Tier switch
                {
                    ETier.Silver => 500,
                    ETier.Gold => 1000,
                    ETier.Diamond => 2000,
                    _ => 500,
                };
            }
            return maxDrop;
        }

        private void AuditNonCombatInvariants(bool afterCombat)
        {
            // 1. [Invariant 10] XP 与 Level / HealthMax 单调性校验
            if (_baselineCaptured)
            {
                int curLvl = _playerAttrs.GetValueOrDefault(EPlayerAttributeType.Level, 1);
                int curXp = _playerAttrs.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
                int curHpMax = _playerAttrs.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250);
                int curHandItems = CountPlayerHandItems();

                if (curLvl < _prevLevel)
                {
                    Interlocked.Increment(ref _metrics.XpAndLevelViolationCount);
                    _metrics.AddViolationSample($"[{_label}] LEVEL_DROP at D{_day}H{_hour}: {_prevLevel} -> {curLvl}", _logFile);
                }
                else if (curLvl == _prevLevel && curXp < _prevXp)
                {
                    Interlocked.Increment(ref _metrics.XpAndLevelViolationCount);
                    _metrics.AddViolationSample($"[{_label}] XP_SILENT_DROP at D{_day}H{_hour} L{curLvl}: {_prevXp} -> {curXp}", _logFile);
                }
                else if (curLvl > _prevLevel && curHpMax <= _prevHealthMax)
                {
                    // 允许且仅允许：Large Appetites（Hand<=5 赋予 HealthMax 光环）在同指令手牌从 <=5 跨越到 >5 时关闭带来的预期血量下降
                    int largeAppetitesDrop = (_prevHandItemCount <= 5 && curHandItems > 5) ? GetMaxLargeAppetitesDrop() : 0;
                    if (largeAppetitesDrop <= 0 || curHpMax <= _prevHealthMax - largeAppetitesDrop)
                    {
                        Interlocked.Increment(ref _metrics.XpAndLevelViolationCount);
                        _metrics.AddViolationSample($"[{_label}] LEVELUP_HPMAX_NOT_INCREASED at D{_day}H{_hour}: L{_prevLevel}->L{curLvl} hpMax {_prevHealthMax}->{curHpMax} hand {_prevHandItemCount}->{curHandItems}", _logFile);
                    }
                }

                _prevLevel = curLvl;
                _prevXp = curXp;
                _prevHealthMax = curHpMax;
                _prevHandItemCount = curHandItems;
            }

            if (_state == ERunState.LevelUp)
            {
                string lvlSig = $"{_day}|{_hour}|{string.Join(",", _selectionSet)}";
                if (lvlSig != _lastCheckedLevelUpSignature)
                {
                    _lastCheckedLevelUpSignature = lvlSig;
                    var validCands = _selectionSet
                        .Where(id => _cards.ContainsKey(id))
                        .Select(id => _cards[id])
                        .ToList();
                    int curLvl = _playerAttrs.GetValueOrDefault(EPlayerAttributeType.Level, 1);
                    if (validCands.Count == 0)
                    {
                        Interlocked.Increment(ref _metrics.XpAndLevelViolationCount);
                        _metrics.AddViolationSample($"[{_label}] LEVELUP_EMPTY at D{_day}H{_hour} L{curLvl}: sel=[{string.Join(",", _selectionSet)}]", _logFile);
                    }
                    else if (validCands.Count == 1)
                    {
                        var single = validCands[0];
                        // 官方 DB 中部分里程碑等级（如 Lv3 Dooley Core Init / Jules Chef Brolin / Hero8 Random Note，Lv4 Silver item，Lv5/Lv7 Argenta）
                        // 会固定发 1 张 EventEncounter / EncounterStep 传送卡；非里程碑情况（或 Item/Skill 仅发 1 项）则判定为 LevelUp 选卡异常
                        bool isOfficialMilestonePortal =
                            curLvl is 3 or 4 or 5 or 7 &&
                            single.Type is ECardType.EventEncounter or ECardType.EncounterStep;
                        if (isOfficialMilestonePortal)
                        {
                            Interlocked.Increment(ref _metrics.MilestoneSingleCardLevelUpsObserved);
                        }
                        else
                        {
                            Interlocked.Increment(ref _metrics.XpAndLevelViolationCount);
                            _metrics.AddViolationSample(
                                $"[{_label}] LEVELUP_TOO_FEW_CHOICES at D{_day}H{_hour} L{curLvl}: count=1 type={single.Type} tpl={single.TemplateId} sel=[{string.Join(",", _selectionSet)}]",
                                _logFile);
                        }
                    }
                }
            }

            // 仅在稳定非战斗态检查棋盘槽位、商店与战后残余
            bool isStableNonCombat = _state is ERunState.Choice or ERunState.Encounter or ERunState.LevelUp or ERunState.Loot
                or ERunState.Pedestal or ERunState.TournamentWaitingRoom or ERunState.TournamentPvpChoice;
            if (!isStableNonCombat)
                return;

            // 2. [Invariant 9] Hand & Stash 槽位不重叠、边界 0..9 且占用格总数 <= 10
            AuditInventorySlots(EInventorySection.Hand);
            AuditInventorySlots(EInventorySection.Stash);

            // 3. [Invariant 8] 付费商店检查（BuyPrice <= 0 除非官方 0 元 Piggle/免费包，0 Loot 标签卡，0 纯 Common 怪卡）
            if (_state == ERunState.Encounter && !_selectionRules.SelectionIsFree)
            {
                string shopSig = $"{_day}|{_hour}|{_currentEncounterId}|{string.Join(",", _selectionSet)}";
                if (shopSig != _lastCheckedShopSignature)
                {
                    _lastCheckedShopSignature = shopSig;
                    AuditPaidShopShelf();
                }
            }

            // 4. [Invariant 11] 战后回归非战斗态（Choice / Loot / LevelUp）残余状态清零与弹药满格校验
            if (afterCombat && _state is ERunState.Choice or ERunState.Loot or ERunState.LevelUp)
            {
                foreach (var c in _cards.Values.Where(x => x.Owner == ECombatantId.Player && x.Type == ECardType.Item))
                {
                    int freeze = c.Attributes.GetValueOrDefault(ECardAttributeType.Freeze, 0);
                    int slow = c.Attributes.GetValueOrDefault(ECardAttributeType.Slow, 0);
                    int haste = c.Attributes.GetValueOrDefault(ECardAttributeType.Haste, 0);
                    int ammoMax = c.Attributes.GetValueOrDefault(ECardAttributeType.AmmoMax, 0);
                    int ammo = c.Attributes.GetValueOrDefault(ECardAttributeType.Ammo, -1);

                    if (freeze != 0 || slow != 0 || haste != 0 || (ammoMax > 0 && ammo != ammoMax))
                    {
                        Interlocked.Increment(ref _metrics.PostCombatResidueViolationCount);
                        _metrics.AddViolationSample(
                            $"[{_label}] POST_COMBAT_RESIDUE at D{_day}H{_hour} state={_state} card={c.InstanceId}({c.TemplateId}): Freeze={freeze} Slow={slow} Haste={haste} Ammo={ammo}/{ammoMax}",
                            _logFile);
                    }
                }
            }
        }

        private void AuditInventorySlots(EInventorySection section)
        {
            var occupiedBy = new string?[10];
            int totalOccupied = 0;

            foreach (var c in _cards.Values)
            {
                if (c.Owner != ECombatantId.Player || c.Type != ECardType.Item) continue;
                if (c.Section != section) continue;

                if (!c.Socket.HasValue)
                {
                    Interlocked.Increment(ref _metrics.SlotOverlapOrOverflowCount);
                    _metrics.AddViolationSample($"[{_label}] SLOT_MISSING at D{_day}H{_hour} {section}: card={c.InstanceId}", _logFile);
                    continue;
                }

                int left = (int)c.Socket.Value;
                int width = CardWidth(c.Size);
                int right = left + width - 1;

                if (left < 0 || right > 9)
                {
                    Interlocked.Increment(ref _metrics.SlotOverlapOrOverflowCount);
                    _metrics.AddViolationSample($"[{_label}] SLOT_OUT_OF_RANGE at D{_day}H{_hour} {section}: card={c.InstanceId} [{left}..{right}]", _logFile);
                    continue;
                }

                for (int s = left; s <= right; s++)
                {
                    totalOccupied++;
                    if (occupiedBy[s] != null)
                    {
                        Interlocked.Increment(ref _metrics.SlotOverlapOrOverflowCount);
                        _metrics.AddViolationSample(
                            $"[{_label}] SLOT_OVERLAP at D{_day}H{_hour} {section}: slot={s} claimed by {occupiedBy[s]} and {c.InstanceId}",
                            _logFile);
                    }
                    else
                    {
                        occupiedBy[s] = c.InstanceId;
                    }
                }
            }

            if (totalOccupied > 10)
            {
                Interlocked.Increment(ref _metrics.SlotOverlapOrOverflowCount);
                _metrics.AddViolationSample($"[{_label}] SLOT_OVERFLOW at D{_day}H{_hour} {section}: occupied={totalOccupied} > 10", _logFile);
            }
        }

        private void AuditPaidShopShelf()
        {
            var encMeta = _catalog.Get(_currentEncounterId);
            bool allowCommon = encMeta?.AllowsCommonItemsInPaidShop ?? false;
            bool allowLoot = encMeta?.AllowsLootInPaidShop ?? false;

            foreach (string id in _selectionSet)
            {
                if (!_cards.TryGetValue(id, out var c)) continue;
                if (c.Type != ECardType.Item) continue;

                var meta = _catalog.Get(c.TemplateId);
                int buyPrice = c.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0);

                if (buyPrice <= 0 && !(meta?.IsOfficialZeroCostAtTier(c.Tier.ToString()) ?? false))
                {
                    Interlocked.Increment(ref _metrics.ShopViolationCount);
                    _metrics.AddViolationSample(
                        $"[{_label}] SHOP_ZERO_PRICE at D{_day}H{_hour} enc={_currentEncounterId}({encMeta?.InternalName}): item={c.InstanceId} tpl={c.TemplateId}({meta?.InternalName}) tier={c.Tier} buyPrice={buyPrice}",
                        _logFile);
                }

                bool hasLootTag = c.Tags.Contains(ECardTag.Loot) || (meta?.IsLootTagged ?? false);
                if (hasLootTag && !allowLoot)
                {
                    Interlocked.Increment(ref _metrics.ShopViolationCount);
                    _metrics.AddViolationSample(
                        $"[{_label}] SHOP_LOOT_ITEM at D{_day}H{_hour} enc={_currentEncounterId}({encMeta?.InternalName}): item={c.InstanceId} tpl={c.TemplateId}({meta?.InternalName})",
                        _logFile);
                }

                bool isPureCommon = (meta != null && meta.IsPureCommonItem) ||
                                    (meta == null && c.Heroes.Count > 0 && c.Heroes.All(h => h == EHero.Common));
                bool explicitlyAllowedId = encMeta != null && encMeta.ExplicitAllowedShopItemIds.Contains(c.TemplateId);
                if (isPureCommon && !allowCommon && !explicitlyAllowedId)
                {
                    Interlocked.Increment(ref _metrics.ShopViolationCount);
                    _metrics.AddViolationSample(
                        $"[{_label}] SHOP_PURE_COMMON at D{_day}H{_hour} enc={_currentEncounterId}({encMeta?.InternalName}): item={c.InstanceId} tpl={c.TemplateId}({meta?.InternalName})",
                        _logFile);
                }
            }
        }

        private static int CardWidth(ECardSize size) => size switch
        {
            ECardSize.Large => 3,
            ECardSize.Medium => 2,
            _ => 1,
        };

        private bool CanFitItemAnywhere(CardShadow item) =>
            CanFitInSection(item, EInventorySection.Hand) || CanFitInSection(item, EInventorySection.Stash);

        private bool CanFitInSection(CardShadow item, EInventorySection section) =>
            FindFirstFreeSlot(CardWidth(item.Size), section) >= 0;

        /// <summary>统计指定分区中已解锁但当前未被占用的槽位数量（供强制收卡前判断是否需跨区腾挪或售卖）。</summary>
        private int CountFreeSlots(EInventorySection section)
        {
            bool[] used = new bool[10];
            foreach (var c in _cards.Values)
            {
                if (c.Owner != ECombatantId.Player || c.Type != ECardType.Item || c.Section != section || !c.Socket.HasValue)
                    continue;
                int left = (int)c.Socket.Value;
                int w = CardWidth(c.Size);
                for (int s = Math.Max(0, left); s <= Math.Min(9, left + w - 1); s++)
                    used[s] = true;
            }

            ushort mask = section == EInventorySection.Hand ? _unlockedSlots : (ushort)0x3FF;
            int free = 0;
            for (int s = 0; s < 10; s++)
            {
                if ((mask & (1 << s)) != 0 && !used[s])
                    free++;
            }
            return free;
        }

        private bool TryFindCrossSectionMove(
            List<CardShadow> ownedItems,
            int handFree,
            int stashFree,
            int neededWidth,
            out MoveItemCommand cmd)
        {
            foreach (var srcSec in new[] { EInventorySection.Stash, EInventorySection.Hand })
            {
                var dstSec = srcSec == EInventorySection.Stash ? EInventorySection.Hand : EInventorySection.Stash;
                int srcFree = srcSec == EInventorySection.Stash ? stashFree : handFree;
                foreach (var item in ownedItems
                             .Where(c => c.Section == srcSec && c.Socket.HasValue)
                             .OrderBy(c => CardWidth(c.Size)))
                {
                    int w = CardWidth(item.Size);
                    if (srcFree + w < neededWidth) continue;
                    int dstSlot = FindFirstFreeSlot(w, dstSec);
                    if (dstSlot >= 0)
                    {
                        cmd = new MoveItemCommand
                        {
                            InstanceId = new InstanceId(item.InstanceId),
                            Section = dstSec,
                            TargetSockets = new List<EContainerSocketId> { (EContainerSocketId)dstSlot },
                        };
                        return true;
                    }
                }
            }

            cmd = default!;
            return false;
        }

        private int FindFirstFreeSlot(int width, EInventorySection section)
        {
            bool[] used = new bool[10];
            foreach (var c in _cards.Values)
            {
                if (c.Owner != ECombatantId.Player || c.Type != ECardType.Item || c.Section != section || !c.Socket.HasValue)
                    continue;
                int left = (int)c.Socket.Value;
                int w = CardWidth(c.Size);
                for (int s = Math.Max(0, left); s <= Math.Min(9, left + w - 1); s++)
                    used[s] = true;
            }

            ushort mask = section == EInventorySection.Hand ? _unlockedSlots : (ushort)0x3FF;
            for (int start = 0; start <= 10 - width; start++)
            {
                bool ok = true;
                for (int w = 0; w < width; w++)
                {
                    int slot = start + w;
                    if ((mask & (1 << slot)) == 0 || used[slot])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return start;
            }
            return -1;
        }

        private SelectItemCommand BuildSmartSelectItemCommand(CardShadow cand, bool preferStash)
        {
            int w = CardWidth(cand.Size);
            int handSlot = FindFirstFreeSlot(w, EInventorySection.Hand);
            int stashSlot = FindFirstFreeSlot(w, EInventorySection.Stash);

            EInventorySection targetSec;
            int targetSlot;
            if (preferStash && stashSlot >= 0)
            {
                targetSec = EInventorySection.Stash;
                targetSlot = stashSlot;
            }
            else if (handSlot >= 0)
            {
                targetSec = EInventorySection.Hand;
                targetSlot = handSlot;
            }
            else if (stashSlot >= 0)
            {
                targetSec = EInventorySection.Stash;
                targetSlot = stashSlot;
            }
            else
            {
                return new SelectItemCommand
                {
                    InstanceId = new InstanceId(cand.InstanceId),
                    Section = preferStash ? EInventorySection.Stash : EInventorySection.Hand,
                };
            }

            return new SelectItemCommand
            {
                InstanceId = new InstanceId(cand.InstanceId),
                Section = targetSec,
                TargetSockets = new List<EContainerSocketId> { (EContainerSocketId)targetSlot },
            };
        }

        private bool IsUnsellable(CardShadow card)
        {
            if (card.Tags.Contains(ECardTag.Unsellable) || card.HiddenTags.Contains(EHiddenTag.Unsellable))
                return true;
            var meta = _catalog.Get(card.TemplateId);
            return meta?.IsUnsellable ?? false;
        }

        private bool IsLootOrConsumable(CardShadow card)
        {
            if (card.Tags.Contains(ECardTag.Loot)) return true;
            var meta = _catalog.Get(card.TemplateId);
            return meta?.IsLootTagged ?? false;
        }

        private static int PickTargetSlotForMove(
            CardShadow card,
            List<CardShadow> ownedItems,
            ushort unlockedMask,
            Random rng,
            bool trySwap,
            out EInventorySection section)
        {
            int width = CardWidth(card.Size);
            var candidates = new List<(EInventorySection Sec, int Slot)>();

            if (trySwap)
            {
                // 选择另一张相同宽度卡牌的当前位置触发 MoveItemCommand 交换测试
                foreach (var other in ownedItems)
                {
                    if (other.InstanceId == card.InstanceId || !other.Socket.HasValue || !other.Section.HasValue)
                        continue;
                    if (CardWidth(other.Size) == width)
                    {
                        candidates.Add((other.Section.Value, (int)other.Socket.Value));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                for (int s = 0; s <= 10 - width; s++)
                {
                    bool unlocked = true;
                    for (int w = 0; w < width; w++)
                    {
                        if ((unlockedMask & (1 << (s + w))) == 0) { unlocked = false; break; }
                    }
                    if (unlocked && !(card.Section == EInventorySection.Hand && card.Socket == (EContainerSocketId)s))
                        candidates.Add((EInventorySection.Hand, s));
                }

                for (int s = 0; s <= 10 - width; s++)
                {
                    if (!(card.Section == EInventorySection.Stash && card.Socket == (EContainerSocketId)s))
                        candidates.Add((EInventorySection.Stash, s));
                }
            }

            if (candidates.Count == 0)
            {
                section = card.Section ?? EInventorySection.Hand;
                return -1;
            }

            var pick = candidates[rng.Next(candidates.Count)];
            section = pick.Sec;
            return pick.Slot;
        }

        private static int MaxSaneGhostHealthForDay(int day) => day switch
        {
            <= 1 => 550,
            2 => 900,
            3 => 1500,
            4 => 2500,
            5 => 4500,
            6 => 7500,
            7 => 10000,
            8 => 15000,
            9 => 20000,
            10 => 25000,
            11 => 33000,
            12 => 41000,
            13 => 50000,
            14 => 65000,
            _ => 80000 + (day - 15) * 5000,
        };

        private bool CheckWlInvariant(
            uint day, uint hour, ERunState state, uint victories, uint defeats, bool isTerminal, out string? failReason)
        {
            if (isTerminal)
            {
                // 锦标赛 LastHumanStandingMode 在所有其他真人淘汰后，可能于当天的非 Hour 6（如 Hour 3 PvE 后）直接加冕 EndRunVictory；
                // 或超过 25 天安全上限于当日中局触发保护终局，此时 V+L == Day - 1
                bool allowMidDayTerminal =
                    (_playMode == EPlayMode.Tournament && state == ERunState.EndRunVictory && hour < 6) ||
                    (state == ERunState.EndRunDefeat && day > 25 && hour < 6);
                bool valid = allowMidDayTerminal
                    ? (victories + defeats == day - 1 || victories + defeats == day)
                    : (victories + defeats == day);
                if (state is ERunState.EndRunVictory or ERunState.EndRunDefeat && !valid)
                {
                    failReason = $"TERMINAL_WL_INVARIANT_VIOLATION at {state} D{day}H{hour}: V({victories})+L({defeats})={victories + defeats} != Day({day})";
                    return false;
                }
                failReason = null;
                return true;
            }

            if (day >= 2 && (hour < 6 || (hour == 6 && state == ERunState.Choice)))
            {
                if (victories + defeats != day - 1)
                {
                    failReason = $"WL_INVARIANT_VIOLATION at D{day}H{hour} state={state}: V({victories})+L({defeats})={victories + defeats} != Day-1({day - 1})";
                    return false;
                }
            }

            failReason = null;
            return true;
        }

        private void ApplySnapshot(GameStateSnapshotDTO sync)
        {
            _day = sync.Run.Day;
            _hour = sync.Run.Hour;
            _victories = sync.Run.Victories;
            _defeats = sync.Run.Defeats;
            _unlockedSlots = sync.Player.UnlockedSlots;

            _playerAttrs.Clear();
            foreach (var kv in sync.Player.Attributes) _playerAttrs[kv.Key] = kv.Value;

            if (sync.CurrentState != null)
            {
                _state = sync.CurrentState.StateName;
                _currentEncounterId = sync.CurrentState.CurrentEncounterId;
                _rerollCost = sync.CurrentState.RerollCost ?? _rerollCost;
                _selectionSet.Clear();
                _selectionSet.AddRange(sync.CurrentState.SelectionSet ?? new List<string>());
                _selectionRules = sync.CurrentState.SelectionContextRules ?? new TSelectionContextRules();
            }

            _cards.Clear();
            foreach (var c in sync.Cards)
            {
                var sh = new CardShadow
                {
                    InstanceId = c.InstanceId,
                    TemplateId = c.TemplateId.ToString(),
                    Type = c.Type,
                    Size = c.Size,
                    Tier = c.Tier,
                    Enchantment = c.Enchantment,
                    Owner = c.Owner,
                    Socket = c.Socket,
                    Section = c.Section,
                };
                if (c.Tags != null) foreach (var t in c.Tags) sh.Tags.Add(t);
                if (c.HiddenTags != null) foreach (var ht in c.HiddenTags) sh.HiddenTags.Add(ht);
                if (c.Heroes != null) foreach (var h in c.Heroes) sh.Heroes.Add(h);
                foreach (var kv in c.Attributes) sh.Attributes[kv.Key] = kv.Value;
                _cards[c.InstanceId] = sh;
            }
        }

        private void ApplyMessage(INetMessage? msg)
        {
            if (msg is NetMessageGameStateSync sync)
            {
                ApplySnapshot(sync.Data);
                return;
            }
            if (msg is NetMessageGameSim gs)
            {
                ApplyGameSim(gs.Data);
                return;
            }
            if (msg is NetMessageAggregate agg)
            {
                foreach (var inner in agg.Messages) ApplyMessage(inner);
            }
        }

        private void ApplyGameSim(GameSim? sim)
        {
            if (sim == null) return;

            if (sim.Run != null)
            {
                _day = sim.Run.Day;
                _hour = sim.Run.Hour;
                _victories = sim.Run.Victories;
                _defeats = sim.Run.Defeats;
                if (sim.Run.HasVisitedFates) _hasVisitedFates = true;
            }
            if (sim.Player?.Attributes != null && sim.Player.Attributes.Count > 0)
            {
                foreach (var kv in sim.Player.Attributes) _playerAttrs[kv.Key] = kv.Value;
            }
            if (sim.CurrentState != null)
            {
                _state = sim.CurrentState.StateName;
                _currentEncounterId = sim.CurrentState.CurrentEncounterId;
                _rerollCost = sim.CurrentState.RerollCost ?? _rerollCost;
                _selectionSet.Clear();
                if (sim.CurrentState.SelectionSet != null)
                    _selectionSet.AddRange(sim.CurrentState.SelectionSet);
                if (sim.CurrentState.SelectionContextRules != null)
                    _selectionRules = sim.CurrentState.SelectionContextRules;
            }

            var knownTypes = new Dictionary<string, (ECardType Type, string Tpl)>(StringComparer.Ordinal);
            foreach (var kv in _cards)
                knownTypes[kv.Key] = (kv.Value.Type, kv.Value.TemplateId);

            var removedIds = new HashSet<string>(StringComparer.Ordinal);
            bool hasFullCardListTransition = false;
            foreach (var ev in sim.Events)
            {
                switch (ev)
                {
                    case GameSimEventCardDealt dealt:
                        knownTypes[dealt.InstanceId] = (dealt.Type, dealt.TemplateId);
                        hasFullCardListTransition = true;
                        break;
                    case GameSimEventCardSpawned sp:
                        knownTypes[sp.InstanceId] = (sp.Type, sp.TemplateId);
                        break;
                    case GameSimEventCardTransformed tr:
                        hasFullCardListTransition = true;
                        if (tr.TransformedCards != null)
                        {
                            foreach (var nc in tr.TransformedCards)
                                knownTypes[nc.InstanceId] = (nc.Type, nc.TemplateId);
                        }
                        break;
                    case GameSimEventCardDisposed disp when !string.IsNullOrEmpty(disp.InstanceId):
                        removedIds.Add(disp.InstanceId);
                        break;
                    case GameSimEventCardSold sold when !string.IsNullOrEmpty(sold.InstanceId):
                        removedIds.Add(sold.InstanceId);
                        break;
                    case GameSimEventCardFused fused when !string.IsNullOrEmpty(fused.FusedInstanceId):
                        removedIds.Add(fused.FusedInstanceId);
                        break;
                    case GameSimEventStateTransitioned:
                    case GameSimEventInterruptStateEntered:
                    case GameSimEventInterruptStateExited:
                        hasFullCardListTransition = true;
                        break;
                    case GameSimEventSocketsUnlocked su:
                        _unlockedSlots = su.UnlockedSocketsBitmask;
                        break;
                    case GameSimEventEffectAuraExecuted:
                        Interlocked.Increment(ref _metrics.AuraRefreshEventsSeen);
                        break;
                }
            }
            if (sim.Events.OfType<GameSimEventInterruptStateEntered>().Any())
                Interlocked.Increment(ref _metrics.SuspendedInterruptsSeen);

            foreach (var rid in removedIds)
                _cards.Remove(rid);

            if (sim.Cards != null && sim.Cards.Count > 0)
            {
                // 对齐官方客户端 Data.UpdateCardAttributes 增量语义：
                // 仅当收到含全量卡表的非拖拽增量包（发生状态切换/发牌/或完整覆盖全部 SelectionSet 且无纯粹 MoveItem 增量特征）时才清理旧卡；
                // 对 V13.1 BuildDeltaGameSim 拖拽轻量增量包执行原位 Upsert 合并，保留未变动的手牌/储藏箱与桌面 SelectionSet 遭遇卡。
                bool isDeltaOnlyMove = !hasFullCardListTransition
                    && removedIds.Count == 0
                    && (_selectionSet.Any(id => !sim.Cards.ContainsKey(id))
                        || sim.Events.Any(e => e is GameSimEventCardMoved));

                var next = new Dictionary<string, CardShadow>(StringComparer.Ordinal);
                foreach (var (id, delta) in sim.Cards)
                {
                    var meta = knownTypes.GetValueOrDefault(id, (Type: InferCardTypeFromId(id), Tpl: ""));
                    var prev = _cards.GetValueOrDefault(id);
                    var sh = new CardShadow
                    {
                        InstanceId = id,
                        TemplateId = !string.IsNullOrEmpty(meta.Tpl) ? meta.Tpl : (prev?.TemplateId ?? ""),
                        Type = !string.IsNullOrEmpty(meta.Tpl) ? meta.Type : (prev?.Type ?? meta.Type),
                        Size = delta.Size ?? prev?.Size ?? ECardSize.Medium,
                        Tier = delta.Tier ?? prev?.Tier ?? ETier.Bronze,
                        Enchantment = delta.Enchantment ?? prev?.Enchantment,
                        Owner = delta.Placement?.Owner ?? prev?.Owner,
                        Socket = delta.Placement?.Socket ?? prev?.Socket,
                        Section = delta.Placement?.Section ?? prev?.Section,
                    };
                    if (delta.Tags != null) foreach (var t in delta.Tags) sh.Tags.Add(t);
                    else if (prev != null) foreach (var t in prev.Tags) sh.Tags.Add(t);
                    if (delta.HiddenTags != null) foreach (var ht in delta.HiddenTags) sh.HiddenTags.Add(ht);
                    else if (prev != null) foreach (var ht in prev.HiddenTags) sh.HiddenTags.Add(ht);
                    if (delta.Heroes != null) foreach (var h in delta.Heroes) sh.Heroes.Add(h);
                    else if (prev != null) foreach (var h in prev.Heroes) sh.Heroes.Add(h);
                    if (prev != null)
                        foreach (var akv in prev.Attributes) sh.Attributes[akv.Key] = akv.Value;
                    foreach (var akv in delta.Attributes) sh.Attributes[akv.Key] = akv.Value.Value;
                    next[id] = sh;
                }

                if (isDeltaOnlyMove)
                {
                    foreach (var kv in next) _cards[kv.Key] = kv.Value;
                }
                else
                {
                    _cards.Clear();
                    foreach (var kv in next) _cards[kv.Key] = kv.Value;
                }
            }
        }
    }

    internal static bool CheckHttpStatus(HttpStatusCode code, string label, FuzzMetrics metrics, Action<string> logFile)
    {
        if (code == HttpStatusCode.OK) return true;
        if ((int)code >= 500)
        {
            Interlocked.Increment(ref metrics.Http500Count);
            metrics.AddViolationSample($"HTTP 5xx ({(int)code} {code}) at {label}", logFile);
        }
        else
        {
            Interlocked.Increment(ref metrics.HttpOtherErrorCount);
            metrics.AddViolationSample($"HTTP {(int)code} ({code}) at {label}", logFile);
        }
        return false;
    }

    internal static ECardType InferCardTypeFromId(string id)
    {
        if (id.StartsWith("skl_", StringComparison.Ordinal)) return ECardType.Skill;
        if (id.StartsWith("step_", StringComparison.Ordinal) || id.StartsWith("stp_", StringComparison.Ordinal)) return ECardType.EncounterStep;
        if (id.StartsWith("ped_", StringComparison.Ordinal)) return ECardType.PedestalEncounter;
        if (id.StartsWith("cbt_", StringComparison.Ordinal)) return ECardType.CombatEncounter;
        if (id.StartsWith("pvp_", StringComparison.Ordinal)) return ECardType.PvpEncounter;
        if (id.StartsWith("enc_", StringComparison.Ordinal)) return ECardType.EventEncounter;
        return ECardType.Item;
    }

    private sealed record ResumeCheckpoint(
        uint Day,
        uint Hour,
        uint Victories,
        uint Defeats,
        ERunState State,
        string? CurrentEncounterId,
        ushort UnlockedSlots,
        int Gold,
        int HealthMax,
        int Level,
        List<string> SelectionSet,
        Dictionary<string, (string TemplateId, EInventorySection? Section, EContainerSocketId? Socket, ETier Tier, EEnchantmentType? Ench)> OwnedCards);

    private static ResumeCheckpoint CaptureResumeCheckpoint(
        uint day, uint hour, uint victories, uint defeats,
        ERunState state, string? encId, ushort unlockedSlots,
        Dictionary<EPlayerAttributeType, int> playerAttrs,
        List<string> selectionSet,
        Dictionary<string, CardShadow> cards)
    {
        var owned = cards.Values
            .Where(c => c.Owner == ECombatantId.Player)
            .ToDictionary(
                c => c.InstanceId,
                c => (c.TemplateId, c.Section, c.Socket, c.Tier, c.Enchantment),
                StringComparer.Ordinal);
        return new ResumeCheckpoint(
            day, hour, victories, defeats, state, encId, unlockedSlots,
            playerAttrs.GetValueOrDefault(EPlayerAttributeType.Gold),
            playerAttrs.GetValueOrDefault(EPlayerAttributeType.HealthMax),
            playerAttrs.GetValueOrDefault(EPlayerAttributeType.Level),
            new List<string>(selectionSet),
            owned);
    }

    private static string? CompareResumeCheckpoint(
        ResumeCheckpoint pre,
        uint day, uint hour, uint victories, uint defeats,
        ERunState state, string? encId, ushort unlockedSlots,
        Dictionary<EPlayerAttributeType, int> playerAttrs,
        List<string> selectionSet,
        Dictionary<string, CardShadow> cards)
    {
        if (pre.Day != day || pre.Hour != hour) return $"D{pre.Day}H{pre.Hour} != D{day}H{hour}";
        if (pre.Victories != victories || pre.Defeats != defeats) return $"W/L {pre.Victories}/{pre.Defeats} != {victories}/{defeats}";
        if (pre.State != state) return $"state {pre.State} != {state}";
        if (!string.Equals(pre.CurrentEncounterId, encId, StringComparison.OrdinalIgnoreCase))
            return $"enc {pre.CurrentEncounterId} != {encId}";
        if (pre.UnlockedSlots != unlockedSlots) return $"slots 0x{pre.UnlockedSlots:X} != 0x{unlockedSlots:X}";
        if (pre.Gold != playerAttrs.GetValueOrDefault(EPlayerAttributeType.Gold))
            return $"gold {pre.Gold} != {playerAttrs.GetValueOrDefault(EPlayerAttributeType.Gold)}";
        if (pre.HealthMax != playerAttrs.GetValueOrDefault(EPlayerAttributeType.HealthMax))
            return $"hpMax {pre.HealthMax} != {playerAttrs.GetValueOrDefault(EPlayerAttributeType.HealthMax)}";
        if (pre.Level != playerAttrs.GetValueOrDefault(EPlayerAttributeType.Level))
            return $"level {pre.Level} != {playerAttrs.GetValueOrDefault(EPlayerAttributeType.Level)}";
        if (!pre.SelectionSet.SequenceEqual(selectionSet))
            return $"selectionSet [{string.Join(",", pre.SelectionSet)}] != [{string.Join(",", selectionSet)}]";

        var nowOwned = cards.Values.Where(c => c.Owner == ECombatantId.Player).ToDictionary(c => c.InstanceId, StringComparer.Ordinal);
        if (pre.OwnedCards.Count != nowOwned.Count)
            return $"owned count {pre.OwnedCards.Count} != {nowOwned.Count}";

        foreach (var (id, info) in pre.OwnedCards)
        {
            if (!nowOwned.TryGetValue(id, out var cur))
                return $"missing owned card {id}";
            if (!string.Equals(info.TemplateId, cur.TemplateId, StringComparison.OrdinalIgnoreCase))
                return $"card {id} template {info.TemplateId} != {cur.TemplateId}";
            if (info.Section != cur.Section || info.Socket != cur.Socket || info.Tier != cur.Tier || info.Ench != cur.Enchantment)
                return $"card {id} placement/tier/ench changed";
        }
        return null;
    }
}
