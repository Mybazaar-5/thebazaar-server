using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Players;
using BazaarGameShared.Domain.Prerequisites;
using BazaarGameShared.Domain.Prerequisites.Conditionals;
using BazaarGameShared.Domain.Targeting;
using BazaarGameShared.Domain.Values;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace BazaarCore.Tests;

/// <summary>
/// v11 官方 BazaarGameShared.dll 差分 Oracle 测试套件：
/// - Test A：扫描 GameData.db 全表所有 "$type" 与全部 3,442 张卡牌，校验 RuleTypeRegistry 100% 覆盖率与官方反序列化。
/// - Test B：提取全部真实 Target / Conditions / Prerequisites / Value 子树及边界合成用例，开展 >= 100,000 次随机状态对拍，目标 0 差异。
/// </summary>
public sealed class V11OfficialDllDiffTests
{
    private readonly ITestOutputHelper _output;

    public V11OfficialDllDiffTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly Lazy<(string DbPath, GameDataRepository Repo)> SharedData = new(() =>
    {
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        OfficialSharedOracle.EnsureDbTemplatesLoaded(shared.DbPath);
        return (shared.DbPath, shared.Repo);
    });

    [Fact]
    public void Audit_GameDataDb_SchemaAndRuleTypeRegistry_Covers100Percent()
    {
        var (dbPath, repo) = SharedData.Value;

        Assert.Empty(repo.UnknownRuleTypes);
        Assert.True(repo.CardsById.Count >= 3442, $"预期加载 >= 3442 张卡牌，实际: {repo.CardsById.Count}");

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        var tableNames = new List<string>();
        using (var tcmd = conn.CreateCommand())
        {
            tcmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var tr = tcmd.ExecuteReader();
            while (tr.Read())
                tableNames.Add(tr.GetString(0));
        }

        var allNonSeasonTypes = new HashSet<string>(StringComparer.Ordinal);
        var perTableCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int deserializedCards = 0;

        foreach (var table in new[] { "cards", "level_ups", "game_modes", "monsters", "challenges", "collectibles", "seasons" })
        {
            if (!tableNames.Contains(table)) continue;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Id, Data FROM {table}";
            using var reader = cmd.ExecuteReader();
            var tableTypes = new HashSet<string>(StringComparer.Ordinal);

            while (reader.Read())
            {
                var blob = reader.GetFieldValue<byte[]>(1);
                var json = Encoding.UTF8.GetString(blob);
                using var doc = JsonDocument.Parse(json);
                CollectJsonTypes(doc.RootElement, tableTypes);

                if (table == "cards")
                {
                    var card = OfficialSharedOracle.DeserializeFromJson<ITCard>(json);
                    Assert.NotNull(card);
                    deserializedCards++;
                }
            }

            perTableCounts[table] = tableTypes.Count;
            if (table != "seasons")
            {
                foreach (var t in tableTypes)
                    allNonSeasonTypes.Add(t);
            }
        }

        Assert.True(deserializedCards >= 3442, $"官方 DLL 反序列化卡牌数应 >= 3442，实际: {deserializedCards}");

        var missingInRegistry = allNonSeasonTypes
            .Where(t => !RuleTypeRegistry.KnownTypes.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(missingInRegistry.Count == 0,
            $"RuleTypeRegistry.KnownTypes 遗漏了 {missingInRegistry.Count} 个非 TSeason $type: {string.Join(", ", missingInRegistry)}");

        // 显式核对 challenges 表中的 5 个官方触发器与关键新类型
        foreach (var required in new[]
        {
            "TTriggerOnCardPerformedCharge",
            "TTriggerOnPlayerLeveledUp",
            "TTriggerOnPlayerLifeStealed",
            "TTriggerOnPlayerShielded",
            "TTriggerOnRunCompleted",
            "TCardConditionalSizeComparison",
            "TCardConditionalTriggerEffectOwner",
            "TPlayerConditionalAnd",
            "TPlayerConditionalOr",
            "TPlayerConditionalAttributeHighest",
            "TPlayerConditionalAttributeLowest",
            "TReferenceValueCardHeroCount",
            "TRunConditionalAnd",
            "TRunConditionalGameMode",
            "TRunConditionalHasVisitedEncounter",
            "TRunConditionalSandstorm",
            "TSpawnBehaviorAvoidAdjacentSocketEffect",
            "TSpawnBehaviorDownShiftTier"
        })
        {
            Assert.Contains(required, RuleTypeRegistry.KnownTypes);
        }

        _output.WriteLine(
            $"DB Schema 审计完成：cards={deserializedCards}，非 TSeason $type 总数={allNonSeasonTypes.Count}，" +
            $"RuleTypeRegistry.KnownTypes={RuleTypeRegistry.KnownTypes.Count}，表分布={string.Join(", ", perTableCounts.Select(kv => $"{kv.Key}:{kv.Value}"))}");
    }

    [Fact]
    public void DifferentialSweep_100000Trials_ZeroMismatchesWithBazaarGameSharedDll()
    {
        var (dbPath, repo) = SharedData.Value;
        var corpus = ExtractSubtreeCorpus(dbPath);

        _output.WriteLine(
            $"已提取真实子树语料：TargetCard={corpus.TargetCards.Count}, TargetPlayer={corpus.TargetPlayers.Count}, " +
            $"CardCond={corpus.CardConditions.Count}, PlayerCond={corpus.PlayerConditions.Count}, " +
            $"RunCond={corpus.RunConditions.Count}, Prereq={corpus.Prerequisites.Count}, Value={corpus.Values.Count}");

        Assert.True(corpus.TargetCards.Count > 100, "应从 DB 提取足够数量的真实 TargetCard 子树");
        Assert.True(corpus.CardConditions.Count > 100, "应从 DB 提取足够数量的真实 CardConditional 子树");
        Assert.True(corpus.Prerequisites.Count > 20, "应从 DB 提取足够数量的真实 Prerequisite 子树");
        Assert.True(corpus.Values.Count > 100, "应从 DB 提取足够数量的真实 Value 子树");

        // 构造固定尺寸测试牌池以加速 100k+ 次场景构建
        var itemPool = repo.CardsById.Values
            .Where(c => c.Type == "Item")
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Take(240)
            .ToList();
        var skillPool = repo.CardsById.Values
            .Where(c => c.Type == "Skill")
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Take(80)
            .ToList();
        var socketPool = repo.CardsById.Values
            .Where(c => c.Type is "SocketEffect" or "MusicNoteSocketEffect")
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Take(60)
            .ToList();
        var encounterPool = repo.CardsById.Values
            .Where(c => c.Type is "CombatEncounter" or "EventEncounter" or "EncounterStep")
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Take(40)
            .ToList();

        Assert.NotEmpty(itemPool);
        Assert.NotEmpty(skillPool);
        Assert.NotEmpty(socketPool);
        Assert.NotEmpty(encounterPool);

        const int TotalScenarios = 15_000;
        int totalTrials = 0;
        var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var mismatches = new List<string>();

        void RecordCategory(string cat)
        {
            totalTrials++;
            categoryCounts[cat] = categoryCounts.GetValueOrDefault(cat) + 1;
        }

        void AddMismatch(string detail)
        {
            if (mismatches.Count < 30)
                mismatches.Add(detail);
        }

        var scenarioMasterRng = new Random(20261121);

        for (int s = 0; s < TotalScenarios; s++)
        {
            int scenarioSeed = scenarioMasterRng.Next();
            var scenario = BuildRandomScenario(
                new Random(scenarioSeed),
                repo,
                itemPool,
                skillPool,
                socketPool,
                encounterPool);

            var allLocalCards = EnumerateAllOwnedCards(scenario.Self);
            var allCards = new List<CardInstance>(allLocalCards);
            if (scenario.Opp != null)
                allCards.AddRange(EnumerateAllOwnedCards(scenario.Opp));

            // 每个随机盘面均测试 8 类差分对拍（共 15,000 * 8 = 120,000 次差分断言）
            // 1. TTargetCard*（标准全量/条件解析）
            {
                var entry = corpus.TargetCards[scenarioMasterRng.Next(corpus.TargetCards.Count)];
                CompareTargetCard(scenario, entry, maxCount: null, rngSeed: scenarioSeed + 11, AddMismatch);
                RecordCategory("TargetCard");
            }

            // 2. TTargetCardRandom（定向双种子同步抽样：既检集合相等又检逐个抽取完全一致）
            {
                var entry = corpus.RandomTargets[scenarioMasterRng.Next(corpus.RandomTargets.Count)];
                int take = scenarioMasterRng.Next(1, 4);
                CompareTargetCard(scenario, entry, maxCount: take, rngSeed: scenarioSeed + 23, AddMismatch);
                RecordCategory("TargetCardRandom");
            }

            // 3. TTargetPlayer*
            {
                var entry = corpus.TargetPlayers[scenarioMasterRng.Next(corpus.TargetPlayers.Count)];
                CompareTargetPlayer(scenario, entry, AddMismatch);
                RecordCategory("TargetPlayer");
            }

            // 4. ITCardConditional（直接对拍候选卡牌，含 AttributeHighest/Lowest 与 SizeLargest/Smallest）
            if (allCards.Count > 0)
            {
                var entry = corpus.CardConditions[scenarioMasterRng.Next(corpus.CardConditions.Count)];
                var candidate = allCards[scenarioMasterRng.Next(allCards.Count)];
                CompareCardCondition(scenario, entry, candidate, AddMismatch);
                RecordCategory("CardCondition");
            }

            // 5. ITPlayerConditional（直接 IsSatisfiedBy 对拍）
            {
                var entry = corpus.PlayerConditions[scenarioMasterRng.Next(corpus.PlayerConditions.Count)];
                var candidatePlayer = scenario.Opp != null && scenarioMasterRng.Next(2) == 1 ? scenario.Opp : scenario.Self;
                ComparePlayerCondition(scenario, entry, candidatePlayer, AddMismatch);
                RecordCategory("PlayerCondition");
            }

            // 6. ITRunConditional
            {
                var entry = corpus.RunConditions[scenarioMasterRng.Next(corpus.RunConditions.Count)];
                CompareRunCondition(scenario, entry, AddMismatch);
                RecordCategory("RunCondition");
            }

            // 7. ITPrerequisite（官方 PrereqContext 要求 SelfCard != null）
            if (scenario.SelfCard != null)
            {
                var entry = corpus.Prerequisites[scenarioMasterRng.Next(corpus.Prerequisites.Count)];
                ComparePrerequisite(scenario, entry, AddMismatch);
                RecordCategory("Prerequisite");
            }

            // 8. ITValue（固定/随机范围/卡牌与玩家属性/数量/标签数量/英雄数量/属性变化）
            {
                var entry = corpus.Values[scenarioMasterRng.Next(corpus.Values.Count)];
                CompareValue(scenario, entry, rngSeed: scenarioSeed + 57, AddMismatch);
                RecordCategory("Value");
            }
        }

        _output.WriteLine(
            $"100k+ 差分 Oracle 对拍完成：总次数={totalTrials}，各维度={string.Join(", ", categoryCounts.Select(kv => $"{kv.Key}:{kv.Value}"))}，差异数={mismatches.Count}");

        if (mismatches.Count > 0)
        {
            Assert.Fail($"发现 {mismatches.Count} 处官方 DLL 行为差异（前 30 条）:\n{string.Join("\n---\n", mismatches)}");
        }

        Assert.True(totalTrials >= 100_000, $"要求至少 100,000 次对拍测试，实际执行 {totalTrials} 次");
    }

    // ------------------------------------------------------------------------
    // 差分对比实现
    // ------------------------------------------------------------------------

    private static void CompareTargetCard(
        TestScenario scenario,
        CorpusEntry<TTargetCardBase> entry,
        int? maxCount,
        int rngSeed,
        Action<string> reportMismatch)
    {
        // 对齐说明：V5OfficialParityTests.P0_06 要求 TargetResolver 允许普通 Item 反查 Occupying 的 SocketEffect，
        // 而官方 DLL 仅对 SocketEffect 执行 Occupying 查询；故测试 Occupying 时若 SelfCard 为普通 Item 则改为使用本地 SocketEffect。
        var effectiveSelfCard = scenario.SelfCard;
        if (entry.Node.Type == "TTargetCardOccupying"
            && (effectiveSelfCard == null || effectiveSelfCard.Template.Type is not ("SocketEffect" or "MusicNoteSocketEffect")))
        {
            effectiveSelfCard = scenario.Self.SocketEffects.FirstOrDefault()
                                ?? scenario.Opp?.SocketEffects.FirstOrDefault();
            if (effectiveSelfCard == null) return;
        }

        // 对齐说明：V9CoreParityStageBTests.Issue34 要求 XMost 在 SelfHandAndStash + Conditions==null 下强制按 StartSlot 排序，
        // 而 DB 仅使用单容器分区（SelfHand/AbsolutePlayerHand/AbsolutePlayerSocketEffects）；因此若遇到无条件跨区 XMost 则跳过。
        if (entry.Node.Type == "TTargetCardXMost"
            && entry.Node.RawConditionsElement == null
            && entry.Node.TargetSection is "SelfHandAndStash" or "OpponentHandAndStash" or "AbsolutePlayerHandAndStash" or "AllHands" or "AllStashes" or "AllHandsAndStashes" or "AllSocketEffects")
        {
            return;
        }

        var localCtx = CloneCtxWithRngAndSelf(scenario, effectiveSelfCard, new Random(rngSeed));
        var officialCtx = CloneCtxWithRngAndSelf(scenario, effectiveSelfCard, new Random(rngSeed));
        var oracle = CreateOracle(scenario, officialCtx);

        // 先对拍无数量限制候选集顺序/集合，再对拍限额抽取结果
        var localUnbounded = TargetResolver.ResolveCards(entry.Node, localCtx, maxCount: null);
        var officialUnbounded = entry.Official.GetTargets(oracle.CreateTargetingContext(), null, null, null);
        var localIdsUnbounded = localUnbounded.Select(c => c.InstanceId).ToList();
        var officialIdsUnbounded = officialUnbounded.Select(c => c.GetInstanceId().ToString()).ToList();

        if (!localIdsUnbounded.SequenceEqual(officialIdsUnbounded))
        {
            // TCardConditionalTag/CardStateFilter 等排查上下文输出
            reportMismatch(
                $"[TargetCard-Unbounded] seed={scenario.Seed} type={entry.Node.Type} scope={scenario.Scope} self={effectiveSelfCard?.Template.InternalName ?? "null"}\n" +
                $"  json={entry.Json}\n" +
                $"  local=[{string.Join(",", localIdsUnbounded.Take(8))}] (n={localIdsUnbounded.Count})\n" +
                $"  offic=[{string.Join(",", officialIdsUnbounded.Take(8))}] (n={officialIdsUnbounded.Count})");
            return;
        }

        if (maxCount.HasValue)
        {
            var localPickedCtx = CloneCtxWithRngAndSelf(scenario, effectiveSelfCard, new Random(rngSeed + 999));
            var officialPickedCtx = CloneCtxWithRngAndSelf(scenario, effectiveSelfCard, new Random(rngSeed + 999));
            var oraclePicked = CreateOracle(scenario, officialPickedCtx);

            var localPicked = TargetResolver.ResolveCards(entry.Node, localPickedCtx, maxCount: maxCount.Value)
                .Select(c => c.InstanceId).ToList();
            var officialPicked = entry.Official.GetTargets(oraclePicked.CreateTargetingContext(), (uint)maxCount.Value, null, null)
                .Select(c => c.GetInstanceId().ToString()).ToList();

            if (!localPicked.SequenceEqual(officialPicked))
            {
                reportMismatch(
                    $"[TargetCard-RandomPick({maxCount})] seed={scenario.Seed}\n" +
                    $"  json={entry.Json}\n" +
                    $"  local=[{string.Join(",", localPicked)}]\n" +
                    $"  offic=[{string.Join(",", officialPicked)}]");
            }
        }
    }

    private static void CompareTargetPlayer(
        TestScenario scenario,
        CorpusEntry<TTargetPlayerBase> entry,
        Action<string> reportMismatch)
    {
        // 官方 DLL 的 TPlayerConditionalAttributeLowest.Filter 存在 "> num" 笔误（IsSatisfiedBy 正常为 "< num"），
        // 避免拿该官方 Filter Bug 当成标准；AttributeLowest 改为在 PlayerCondition 维度直接对拍 IsSatisfiedBy。
        if (entry.Json.Contains("TPlayerConditionalAttributeLowest", StringComparison.Ordinal))
            return;

        var localCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(scenario.Seed + 31));
        var officialCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(scenario.Seed + 31));
        var oracle = CreateOracle(scenario, officialCtx);

        var localPlayers = TargetResolver.ResolvePlayers(entry.Node, localCtx)
            .Select(p => ReferenceEquals(p, TargetResolver.GetLocalPlayer(localCtx)) ? "LOCAL" : "OPP")
            .ToList();
        var officialPlayers = entry.Official.GetTargets(oracle.CreateTargetingContext())
            .Where(p => p != null)
            .Select(p => ReferenceEquals(p, oracle.LocalPlayer) ? "LOCAL" : "OPP")
            .ToList();

        if (!localPlayers.SequenceEqual(officialPlayers))
        {
            reportMismatch(
                $"[TargetPlayer] seed={scenario.Seed} scope={scenario.Scope} selfOwner={(scenario.SelfCard != null ? (ReferenceEquals(TargetResolver.FindOwner(scenario.SelfCard, localCtx), TargetResolver.GetLocalPlayer(localCtx)) ? "LOCAL" : "OPP") : "null")}\n" +
                $"  json={entry.Json}\n" +
                $"  local=[{string.Join(",", localPlayers)}] offic=[{string.Join(",", officialPlayers)}]");
        }
    }

    private static void CompareCardCondition(
        TestScenario scenario,
        CorpusEntry<ITCardConditional> entry,
        CardInstance candidate,
        Action<string> reportMismatch)
    {
        var localCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(scenario.Seed + 41));
        var officialCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(scenario.Seed + 41));
        var oracle = CreateOracle(scenario, officialCtx);

        var candidateOwner = TargetResolver.FindOwner(candidate, localCtx);
        var pool = BuildCardConditionalPool(candidateOwner, candidate);
        var cardCondCtx = oracle.CreateCardConditionalContext(pool);
        var oracleCandidate = oracle.MapCard(candidate);

        bool localRes = ValueAndConditionEvaluator.EvaluateCardCondition(entry.Node, candidate, candidateOwner, localCtx);
        bool officialRes = entry.Official.IsSatisfiedBy(oracleCandidate, cardCondCtx);

        if (localRes != officialRes)
        {
            reportMismatch(
                $"[CardCondition] seed={scenario.Seed} cand={candidate.Template.InternalName}(slot={candidate.StartSlot},size={candidate.EffectiveSize},tier={candidate.Tier},ench={candidate.Enchantment ?? "none"},tags=[{string.Join(",", candidate.GetAllTags())}])\n" +
                $"  json={entry.Json}\n" +
                $"  local={localRes} offic={officialRes}");
        }
    }

    private static void ComparePlayerCondition(
        TestScenario scenario,
        CorpusEntry<ITPlayerConditional> entry,
        PlayerState candidatePlayer,
        Action<string> reportMismatch)
    {
        var localCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(scenario.Seed + 47));
        var officialCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(scenario.Seed + 47));
        var oracle = CreateOracle(scenario, officialCtx);

        var pool = new List<PlayerState> { TargetResolver.GetLocalPlayer(localCtx) };
        if (scenario.Opp != null)
        {
            var other = ReferenceEquals(scenario.Self, pool[0]) ? scenario.Opp : scenario.Self;
            if (!ReferenceEquals(other, pool[0])) pool.Add(other);
        }

        bool localRes = ValueAndConditionEvaluator.EvaluatePlayerCondition(entry.Node, candidatePlayer, localCtx, pool);
        var playerCondCtx = oracle.CreatePlayerConditionalContext(pool);
        bool officialRes = entry.Official.IsSatisfiedBy(oracle.MapPlayer(candidatePlayer), playerCondCtx);

        if (localRes != officialRes)
        {
            reportMismatch(
                $"[PlayerCondition] seed={scenario.Seed} player={candidatePlayer.PlayerId}\n" +
                $"  json={entry.Json}\n" +
                $"  local={localRes} offic={officialRes}");
        }
    }

    private static void CompareRunCondition(
        TestScenario scenario,
        CorpusEntry<ITRunConditional> entry,
        Action<string> reportMismatch)
    {
        var localCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(scenario.Seed + 53));
        var officialCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(scenario.Seed + 53));
        var oracle = CreateOracle(scenario, officialCtx);

        bool localRes = ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(entry.Node, localCtx);
        bool officialRes = entry.Official.IsSatisfiedBy(oracle.CreateRunConditionalContext());

        if (localRes != officialRes)
        {
            reportMismatch(
                $"[RunCondition] seed={scenario.Seed} scope={scenario.Scope} day={scenario.CurrentDay} hour={scenario.CurrentHour} timeMs={scenario.TimeMs} enc={scenario.CurrentEncounterId ?? "null"} gm={scenario.Repo.GameMode.Id}\n" +
                $"  json={entry.Json}\n" +
                $"  local={localRes} offic={officialRes}");
        }
    }

    private static void ComparePrerequisite(
        TestScenario scenario,
        CorpusEntry<ITPrerequisite> entry,
        Action<string> reportMismatch)
    {
        var localCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(scenario.Seed + 59));
        var officialCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(scenario.Seed + 59));
        var oracle = CreateOracle(scenario, officialCtx);

        bool localRes = ValueAndConditionEvaluator.EvaluatePrerequisites([entry.Node], localCtx);
        bool officialRes = entry.Official.IsSatisfiedBy(oracle.CreatePrereqContext());

        if (localRes != officialRes)
        {
            reportMismatch(
                $"[Prerequisite] seed={scenario.Seed} self={scenario.SelfCard?.Template.InternalName ?? "null"}\n" +
                $"  json={entry.Json}\n" +
                $"  local={localRes} offic={officialRes}");
        }
    }

    private static void CompareValue(
        TestScenario scenario,
        CorpusEntry<ITValue> entry,
        int rngSeed,
        Action<string> reportMismatch)
    {
        var localCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(rngSeed));
        var officialCtx = CloneCtxWithRngAndSelf(scenario, scenario.SelfCard, new Random(rngSeed));
        var oracle = CreateOracle(scenario, officialCtx);

        double localVal = ValueAndConditionEvaluator.EvaluateValue(entry.Node, localCtx, 0);
        double officialVal = entry.Official.GetValue(oracle.CreateValueContext());

        if (Math.Abs(localVal - officialVal) > 1e-4)
        {
            reportMismatch(
                $"[Value] seed={scenario.Seed} delta={scenario.LastDelta} self={scenario.SelfCard?.Template.InternalName ?? "null"}\n" +
                $"  json={entry.Json}\n" +
                $"  local={localVal} offic={officialVal}");
        }
    }

    // ------------------------------------------------------------------------
    // 与 ValueAndConditionEvaluator.GetAllCandidateCards 完全一致的牌池构造（排除 Destroyed）
    // ------------------------------------------------------------------------

    private static IReadOnlyList<CardInstance> BuildCardConditionalPool(PlayerState owner, CardInstance candidate)
    {
        var list = new List<CardInstance>();
        void AddAll(IEnumerable<CardInstance> cards)
        {
            foreach (var c in cards)
            {
                if (!c.IsDestroyed) list.Add(c);
            }
        }
        AddAll(owner.Hand.Cards);
        AddAll(owner.Stash.Cards);
        AddAll(owner.Skills);
        AddAll(owner.SocketEffects);
        if (!candidate.IsDestroyed && !list.Contains(candidate))
            list.Add(candidate);
        return list;
    }

    // ------------------------------------------------------------------------
    // 测试场景与子树语料生成
    // ------------------------------------------------------------------------

    private sealed class TestScenario
    {
        public required int Seed { get; init; }
        public required GameDataRepository Repo { get; init; }
        public required RuleScope Scope { get; init; }
        public required PlayerState Self { get; init; }
        public required PlayerState? Opp { get; init; }
        public required string LocalPlayerId { get; init; }
        public required CardInstance? SelfCard { get; init; }
        public required CardInstance? TriggerSourceCard { get; init; }
        public required CardInstance? TriggerTargetCard { get; init; }
        public required int CurrentDay { get; init; }
        public required int CurrentHour { get; init; }
        public required int TimeMs { get; init; }
        public required string PlayMode { get; init; }
        public required string? CurrentEncounterId { get; init; }
        public required double LastDelta { get; init; }
        public required List<CardInstance> SelectionCards { get; init; }
    }

    private static RuleExecutionContext CloneCtxWithRngAndSelf(TestScenario sc, CardInstance? selfCard, Random rng)
    {
        var ctx = new RuleExecutionContext(sc.Repo, sc.Scope, sc.Self, sc.Opp, rng)
        {
            LocalPlayerId = sc.LocalPlayerId,
            SelfCard = selfCard,
            TriggerSourceCard = sc.TriggerSourceCard,
            TriggerTargetCard = sc.TriggerTargetCard,
            CurrentDay = sc.CurrentDay,
            CurrentHour = sc.CurrentHour,
            TimeMs = sc.TimeMs,
            PlayMode = sc.PlayMode,
            CurrentEncounterId = sc.CurrentEncounterId,
            LastAttributeChangeDelta = sc.LastDelta,
            SelectionCards = sc.SelectionCards
        };
        return ctx;
    }

    private static OfficialSharedOracle CreateOracle(TestScenario sc, RuleExecutionContext ctx)
    {
        bool includeDelta = Math.Abs(sc.LastDelta) > 0;
        return new OfficialSharedOracle(sc.Self, sc.Opp, ctx, includeDeltaEvent: includeDelta);
    }

    private static TestScenario BuildRandomScenario(
        Random rng,
        GameDataRepository repo,
        List<CardTemplate> items,
        List<CardTemplate> skills,
        List<CardTemplate> sockets,
        List<CardTemplate> encounters)
    {
        int seed = rng.Next();
        var scope = rng.Next(2) == 0 ? RuleScope.InCombat : RuleScope.OutOfCombat;
        bool hasOpp = scope == RuleScope.InCombat || rng.Next(4) > 0;
        bool localIsPlayerA = rng.Next(4) != 0; // 75% 本地玩家主动结算，25% 对手/野怪结算

        var localHero = HeroNames.PlayableHeroes[rng.Next(HeroNames.PlayableHeroes.Length)];
        var oppHero = HeroNames.PlayableHeroes[rng.Next(HeroNames.PlayableHeroes.Length)];

        var localPlayer = new PlayerState("local-p", "Local", localHero) { IsMonster = false };
        PlayerState? oppPlayer = hasOpp
            ? new PlayerState("opp-p", "Opp", oppHero) { IsMonster = rng.Next(2) == 0 }
            : null;

        PopulatePlayer(rng, localPlayer, items, skills, sockets);
        if (oppPlayer != null)
            PopulatePlayer(rng, oppPlayer, items, skills, sockets);

        var selfState = (scope == RuleScope.InCombat && !localIsPlayerA && oppPlayer != null) ? oppPlayer : localPlayer;
        var otherState = ReferenceEquals(selfState, localPlayer) ? oppPlayer : localPlayer;

        var selfPool = EnumerateAllOwnedCards(selfState);
        var allPool = new List<CardInstance>(selfPool);
        if (otherState != null)
            allPool.AddRange(EnumerateAllOwnedCards(otherState));

        // 偶尔允许 SelfCard == null 测试空发起卡守卫
        CardInstance? selfCard = selfPool.Count > 0 && rng.Next(10) != 0
            ? selfPool[rng.Next(selfPool.Count)]
            : null;
        CardInstance? trigSrc = allPool.Count > 0 && rng.Next(5) != 0
            ? allPool[rng.Next(allPool.Count)]
            : null;
        if (trigSrc != null && rng.Next(12) == 0)
        {
            trigSrc = CreateSyntheticCard(rng, items[rng.Next(items.Count)], slot: -1);
            trigSrc.IsDestroyed = true;
        }
        CardInstance? trigTgt = allPool.Count > 0 && rng.Next(5) != 0
            ? allPool[rng.Next(allPool.Count)]
            : null;
        if (trigTgt != null && rng.Next(12) == 0)
        {
            trigTgt = CreateSyntheticCard(rng, items[rng.Next(items.Count)], slot: -1);
            trigTgt.IsDestroyed = true;
        }

        var selCards = new List<CardInstance>();
        int selCount = rng.Next(4);
        for (int i = 0; i < selCount; i++)
        {
            var scCard = CreateSyntheticCard(rng, items[rng.Next(items.Count)], slot: -1);
            selCards.Add(scCard);
        }

        string? encId = rng.Next(3) switch
        {
            0 => null,
            1 => encounters[rng.Next(encounters.Count)].Id,
            _ => Guid.NewGuid().ToString()
        };

        int day = rng.Next(0, 14);
        int hour = rng.Next(0, 6);
        int timeMs = rng.Next(3) switch
        {
            0 => rng.Next(0, 15000),
            1 => rng.Next(29900, 35000),
            _ => rng.Next(0, 45000)
        };
        double delta = rng.Next(4) switch
        {
            0 => 0,
            1 => -rng.Next(1, 30),
            _ => rng.Next(1, 30)
        };

        return new TestScenario
        {
            Seed = seed,
            Repo = repo,
            Scope = scope,
            Self = selfState,
            Opp = otherState,
            LocalPlayerId = localPlayer.PlayerId,
            SelfCard = selfCard,
            TriggerSourceCard = trigSrc,
            TriggerTargetCard = trigTgt,
            CurrentDay = day,
            CurrentHour = hour,
            TimeMs = timeMs,
            PlayMode = rng.Next(2) == 0 ? "Unranked" : "Ranked",
            CurrentEncounterId = encId,
            LastDelta = delta,
            SelectionCards = selCards
        };
    }

    private static void PopulatePlayer(
        Random rng,
        PlayerState p,
        List<CardTemplate> items,
        List<CardTemplate> skills,
        List<CardTemplate> sockets)
    {
        int hpMax = rng.Next(100, 3000);
        int hpCur = Math.Min(hpMax, rng.Next(50, 2500));
        int shield = rng.Next(0, 500);
        int burn = rng.Next(0, 80);
        int poison = rng.Next(0, 80);
        int regen = rng.Next(0, 80);
        p.CurrentHealth = hpCur;
        p.Shield = shield;
        p.Burn = burn;
        p.Poison = poison;
        p.Attributes["Health"] = hpCur;
        p.Attributes["HealthMax"] = hpMax;
        p.Attributes["Shield"] = shield;
        p.Attributes["Burn"] = burn;
        p.Attributes["Poison"] = poison;
        p.Attributes["Regen"] = regen;
        p.Attributes["HealthRegen"] = regen;
        p.Attributes["Gold"] = rng.Next(0, 150);
        p.Attributes["Income"] = rng.Next(0, 30);
        p.Attributes["Level"] = rng.Next(1, 20);
        p.Attributes["Prestige"] = rng.Next(0, 25);

        // 随机解锁掩码（4~10格，连续居中解锁匹配标准棋盘语义）
        int widthRoll = rng.Next(4);
        p.Hand.UnlockedMask = widthRoll switch
        {
            0 => 0b0001111000, // 4格
            1 => 0b0011111100, // 6格
            2 => 0b0111111110, // 8格
            _ => 1023
        };

        // 填充手牌（留空位与满格均覆盖）
        int targetHandItems = rng.Next(0, 8);
        for (int i = 0; i < targetHandItems; i++)
        {
            var tpl = items[rng.Next(items.Count)];
            var ci = CreateSyntheticCard(rng, tpl, slot: -1);
            int width = ci.SlotWidth;
            for (int slot = 0; slot <= BoardGrid.Capacity - width; slot++)
            {
                if (p.Hand.CanFitAt(ci, slot))
                {
                    p.Hand.TryPlaceOrShift(ci, slot);
                    if (rng.Next(10) == 0) ci.IsDisabled = true;
                    break;
                }
            }
        }

        // 填充储藏室
        int targetStashItems = rng.Next(0, 6);
        for (int i = 0; i < targetStashItems; i++)
        {
            var tpl = items[rng.Next(items.Count)];
            var ci = CreateSyntheticCard(rng, tpl, slot: -1);
            int width = ci.SlotWidth;
            for (int slot = 0; slot <= BoardGrid.Capacity - width; slot++)
            {
                if (p.Stash.CanFitAt(ci, slot))
                {
                    p.Stash.TryPlaceOrShift(ci, slot);
                    break;
                }
            }
        }

        // 填充技能
        int skillCount = rng.Next(0, 4);
        for (int i = 0; i < skillCount; i++)
        {
            var sk = CreateSyntheticCard(rng, skills[rng.Next(skills.Count)], slot: -1);
            if (rng.Next(12) == 0) sk.IsDisabled = true;
            p.Skills.Add(sk);
        }

        // 填充 SocketEffects（互不重叠地置于有效槽位）
        int sockCount = rng.Next(0, 4);
        bool[] sockOccupied = new bool[BoardGrid.Capacity];
        for (int i = 0; i < sockCount; i++)
        {
            var se = CreateSyntheticCard(rng, sockets[rng.Next(sockets.Count)], slot: -1);
            int w = se.SlotWidth;
            for (int slot = 0; slot <= BoardGrid.Capacity - w; slot++)
            {
                if (!p.Hand.IsSlotUnlocked(slot)) continue;
                bool free = true;
                for (int k = 0; k < w; k++)
                {
                    if (sockOccupied[slot + k] || !p.Hand.IsSlotUnlocked(slot + k))
                    {
                        free = false;
                        break;
                    }
                }
                if (free)
                {
                    for (int k = 0; k < w; k++) sockOccupied[slot + k] = true;
                    se.StartSlot = slot;
                    p.SocketEffects.Add(se);
                    break;
                }
            }
        }
    }

    private static readonly string[] AllEnchantments =
    {
        "Heavy", "Icy", "Turbo", "Shielded", "Restorative", "Toxic",
        "Fiery", "Shiny", "Deadly", "Radiant", "Obsidian", "Golden"
    };

    private static CardInstance CreateSyntheticCard(Random rng, CardTemplate tpl, int slot)
    {
        var tiers = Enum.GetValues<CardTier>();
        var tier = tiers[rng.Next(tiers.Length)];
        string? ench = null;
        if (tpl.Type == "Item" && rng.Next(3) == 0)
        {
            ench = AllEnchantments[rng.Next(AllEnchantments.Length)];
        }
        var ci = new CardInstance(tpl, tier, ench) { StartSlot = slot };

        if (tpl.Type == "Item" && rng.Next(8) == 0)
            ci.ExtraTags.Add("Tiny");

        // 构造离散数值池以高频触发极值平局（>0 与 0）和常规比较
        ci.CombatAdd["DamageAmount"] = rng.Next(5) * 10;
        ci.CombatAdd["ShieldApplyAmount"] = rng.Next(4) * 10;
        ci.CombatAdd["HealAmount"] = rng.Next(4) * 10;
        ci.CombatAdd["BurnApplyAmount"] = rng.Next(4) * 5;
        ci.CombatAdd["PoisonApplyAmount"] = rng.Next(4) * 5;
        ci.CombatAdd["AmmoMax"] = rng.Next(1, 5);
        ci.CombatAdd["Ammo"] = rng.Next(0, 4);
        ci.CombatAdd["CooldownMax"] = rng.Next(2, 10) * 1000;
        ci.CombatAdd["CritChance"] = rng.Next(0, 5) * 10;
        ci.CombatAdd["Counter"] = rng.Next(0, 6);
        return ci;
    }

    private static List<CardInstance> EnumerateAllOwnedCards(PlayerState p)
    {
        var list = new List<CardInstance>();
        list.AddRange(p.Hand.Cards);
        list.AddRange(p.Stash.Cards);
        list.AddRange(p.Skills);
        list.AddRange(p.SocketEffects);
        return list;
    }

    // ------------------------------------------------------------------------
    // 从 GameData.db 提取全部真实子树 + 补充合成边界子树
    // ------------------------------------------------------------------------

    private sealed record CorpusEntry<T>(string Json, RuleNode Node, T Official);

    private sealed class SubtreeCorpus
    {
        public List<CorpusEntry<TTargetCardBase>> TargetCards { get; } = new();
        public List<CorpusEntry<TTargetCardBase>> RandomTargets { get; } = new();
        public List<CorpusEntry<TTargetPlayerBase>> TargetPlayers { get; } = new();
        public List<CorpusEntry<ITCardConditional>> CardConditions { get; } = new();
        public List<CorpusEntry<ITPlayerConditional>> PlayerConditions { get; } = new();
        public List<CorpusEntry<ITRunConditional>> RunConditions { get; } = new();
        public List<CorpusEntry<ITPrerequisite>> Prerequisites { get; } = new();
        public List<CorpusEntry<ITValue>> Values { get; } = new();
    }

    private static SubtreeCorpus ExtractSubtreeCorpus(string dbPath)
    {
        var corpus = new SubtreeCorpus();
        var seenJson = new HashSet<string>(StringComparer.Ordinal);

        void TryRegisterJson(string rawJson)
        {
            if (!seenJson.Add(rawJson)) return;

            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("$type", out var tp)) return;
            var typeName = tp.GetString();
            if (string.IsNullOrEmpty(typeName)) return;

            // 忽略超出 BazaarCore 本地扩展 States:["Destroyed"] 的场景与内部调试类型
            if (typeName.StartsWith("TTargetCard", StringComparison.Ordinal))
            {
                var node = JsonSerializer.Deserialize<RuleNode>(rawJson, GameDatabaseLoader.JsonOptions);
                var official = OfficialSharedOracle.DeserializeFromJson<TTargetCardBase>(rawJson);
                if (node != null && official != null)
                {
                    var entry = new CorpusEntry<TTargetCardBase>(rawJson, node, official);
                    corpus.TargetCards.Add(entry);
                    if (typeName == "TTargetCardRandom")
                        corpus.RandomTargets.Add(entry);
                }
            }
            else if (typeName.StartsWith("TTargetPlayer", StringComparison.Ordinal))
            {
                var node = JsonSerializer.Deserialize<RuleNode>(rawJson, GameDatabaseLoader.JsonOptions);
                var official = OfficialSharedOracle.DeserializeFromJson<TTargetPlayerBase>(rawJson);
                if (node != null && official != null)
                    corpus.TargetPlayers.Add(new CorpusEntry<TTargetPlayerBase>(rawJson, node, official));
            }
            else if (typeName.StartsWith("TCardConditional", StringComparison.Ordinal))
            {
                var node = JsonSerializer.Deserialize<RuleNode>(rawJson, GameDatabaseLoader.JsonOptions);
                var official = OfficialSharedOracle.DeserializeFromJson<ITCardConditional>(rawJson);
                if (node != null && official != null)
                    corpus.CardConditions.Add(new CorpusEntry<ITCardConditional>(rawJson, node, official));
            }
            else if (typeName.StartsWith("TPlayerConditional", StringComparison.Ordinal))
            {
                var node = JsonSerializer.Deserialize<RuleNode>(rawJson, GameDatabaseLoader.JsonOptions);
                var official = OfficialSharedOracle.DeserializeFromJson<ITPlayerConditional>(rawJson);
                if (node != null && official != null)
                    corpus.PlayerConditions.Add(new CorpusEntry<ITPlayerConditional>(rawJson, node, official));
            }
            else if (typeName.StartsWith("TRunConditional", StringComparison.Ordinal))
            {
                var node = JsonSerializer.Deserialize<RuleNode>(rawJson, GameDatabaseLoader.JsonOptions);
                var official = OfficialSharedOracle.DeserializeFromJson<ITRunConditional>(rawJson);
                if (node != null && official != null)
                    corpus.RunConditions.Add(new CorpusEntry<ITRunConditional>(rawJson, node, official));
            }
            else if (typeName.StartsWith("TPrerequisite", StringComparison.Ordinal))
            {
                var node = JsonSerializer.Deserialize<RuleNode>(rawJson, GameDatabaseLoader.JsonOptions);
                var official = OfficialSharedOracle.DeserializeFromJson<ITPrerequisite>(rawJson);
                if (node != null && official != null)
                    corpus.Prerequisites.Add(new CorpusEntry<ITPrerequisite>(rawJson, node, official));
            }
            else if (typeName is "TFixedValue" or "TRangeValue" || typeName.StartsWith("TReferenceValue", StringComparison.Ordinal))
            {
                var node = JsonSerializer.Deserialize<RuleNode>(rawJson, GameDatabaseLoader.JsonOptions);
                var official = OfficialSharedOracle.DeserializeFromJson<ITValue>(rawJson);
                if (node != null && official != null)
                    corpus.Values.Add(new CorpusEntry<ITValue>(rawJson, node, official));
            }
        }

        void WalkElement(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("$type", out var tp) && tp.ValueKind == JsonValueKind.String)
                {
                    var t = tp.GetString()!;
                    if (t.StartsWith("TTarget", StringComparison.Ordinal)
                        || t.StartsWith("TCardConditional", StringComparison.Ordinal)
                        || t.StartsWith("TPlayerConditional", StringComparison.Ordinal)
                        || t.StartsWith("TRunConditional", StringComparison.Ordinal)
                        || t.StartsWith("TPrerequisite", StringComparison.Ordinal)
                        || t is "TFixedValue" or "TRangeValue"
                        || t.StartsWith("TReferenceValue", StringComparison.Ordinal))
                    {
                        TryRegisterJson(el.GetRawText());
                    }
                }
                foreach (var prop in el.EnumerateObject())
                    WalkElement(prop.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    WalkElement(item);
            }
        }

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Data FROM cards";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var blob = reader.GetFieldValue<byte[]>(0);
            using var doc = JsonDocument.Parse(blob);
            WalkElement(doc.RootElement);
        }

        // 补充 DB 未显式出现或需专项强化测试的官方类型（确保每个枚举和分支均覆盖到）
        foreach (var extra in GetSyntheticEdgeCaseJsons())
            TryRegisterJson(extra);

        return corpus;
    }

    private static IEnumerable<string> GetSyntheticEdgeCaseJsons()
    {
        yield return """{"$type":"TCardConditionalSizeComparison","Comparison":"LessThan","Target":{"$type":"TTargetCardTriggerSource"}}""";
        yield return """{"$type":"TCardConditionalSizeComparison","Comparison":"GreaterThanOrEqual","Target":{"$type":"TTargetCardSelf"}}""";
        yield return """{"$type":"TCardConditionalTriggerEffectOwner","OwnerComparison":"SameOwner"}""";
        yield return """{"$type":"TCardConditionalTriggerEffectOwner","OwnerComparison":"DifferentOwner"}""";
        yield return """{"$type":"TCardConditionalAttributeHighest","AttributeType":"DamageAmount"}""";
        yield return """{"$type":"TCardConditionalAttributeLowest","AttributeType":"DamageAmount"}""";
        yield return """{"$type":"TCardConditionalSizeLargest"}""";
        yield return """{"$type":"TCardConditionalSizeSmallest"}""";
        yield return """{"$type":"TCardConditionalCanCrit"}""";
        yield return """{"$type":"TCardConditionalCanCrit","IsNot":true}""";
        yield return """{"$type":"TCardConditionalHasEnchantment","Enchantment":null}""";
        yield return """{"$type":"TCardConditionalHasEnchantment","Enchantment":null,"IsNot":true}""";
        yield return """{"$type":"TCardConditionalPlayerHero","IsSameAsPlayerHero":false}""";
        yield return """{"$type":"TCardConditionalPlayerHero","IsSameAsPlayerHero":true}""";
        yield return """{"$type":"TCardConditionalState","States":["Disabled"],"Operator":"Any"}""";
        yield return """{"$type":"TCardConditionalState","States":["Disabled"],"Operator":"None"}""";

        yield return """{"$type":"TPlayerConditionalAttributeHighest","AttributeType":"HealthMax"}""";
        yield return """{"$type":"TPlayerConditionalAttributeLowest","AttributeType":"HealthMax"}""";
        yield return """{"$type":"TPlayerConditionalAttribute","Attribute":"Burn","ComparisonOperator":"GreaterThanOrEqual","ComparisonValue":{"$type":"TFixedValue","Value":10}}""";
        yield return """{"$type":"TPlayerConditionalAnd","Conditions":[{"$type":"TPlayerConditionalAttribute","Attribute":"Burn","ComparisonOperator":"GreaterThanOrEqual","ComparisonValue":{"$type":"TFixedValue","Value":5}},{"$type":"TPlayerConditionalAttributeHighest","AttributeType":"HealthMax"}]}""";
        yield return """{"$type":"TPlayerConditionalOr","Conditions":[{"$type":"TPlayerConditionalAttribute","Attribute":"Burn","ComparisonOperator":"GreaterThan","ComparisonValue":{"$type":"TFixedValue","Value":20}},{"$type":"TPlayerConditionalAttributeLowest","AttributeType":"HealthMax"}]}""";

        yield return """{"$type":"TRunConditionalSandstorm"}""";
        yield return """{"$type":"TRunConditionalGameMode","GameModeIds":["11111111-1111-1111-1111-111111111111"],"Operator":"Any"}""";
        yield return """{"$type":"TRunConditionalHasVisitedEncounter","EncounterIds":["22222222-2222-2222-2222-222222222222"],"Operator":"Any"}""";
        yield return """{"$type":"TRunConditionalAnd","Conditions":[{"$type":"TRunConditionalCurrentDay","CurrentDay":1,"ComparisonOperator":"GreaterThanOrEqual"},{"$type":"TRunConditionalSandstorm"}]}""";
        yield return """{"$type":"TRunConditionalPlayMode","PlayModes":["Ranked"],"Operator":"Any"}""";
        yield return """{"$type":"TRunConditionalMusicNoteHasValidSocket"}""";

        yield return """{"$type":"TReferenceValueCardHeroCount","Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"},"IncludeCommon":true,"DefaultValue":0}""";
        yield return """{"$type":"TReferenceValueCardHeroCount","Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"},"IncludeCommon":false,"DefaultValue":2}""";
        yield return """{"$type":"TReferenceValueCardTagCount","Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"},"Distinct":true,"DefaultValue":3}""";
        yield return """{"$type":"TReferenceValueCardTagCount","Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"},"Distinct":false,"DefaultValue":1}""";
        yield return """{"$type":"TReferenceValueAttributeChange","DefaultValue":5,"Modifier":{"ModifyMode":"Multiply","Value":{"$type":"TFixedValue","Value":0.5},"ShouldRound":true}}""";
        yield return """{"$type":"TRangeValue","MinValue":2,"MaxValue":7,"Modifier":{"ModifyMode":"Multiply","Value":{"$type":"TFixedValue","Value":1.5},"ShouldRound":false}}""";

        yield return """{"$type":"TTargetPlayer","TargetMode":"Self"}""";
        yield return """{"$type":"TTargetPlayer","TargetMode":"Opponent"}""";
        yield return """{"$type":"TTargetPlayer","TargetMode":"Both"}""";
        yield return """{"$type":"TTargetPlayer","TargetMode":"AbsolutePlayer"}""";
        yield return """{"$type":"TTargetPlayerAbsolute","TargetMode":"Player"}""";
        yield return """{"$type":"TTargetPlayerAbsolute","TargetMode":"Encounter"}""";
        yield return """{"$type":"TTargetPlayerRelative","TargetMode":"Self"}""";
        yield return """{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}""";
        yield return """{"$type":"TTargetCardPositional","Origin":"TriggerSource","TargetMode":"Neighbor","IncludeOrigin":true}""";
        yield return """{"$type":"TTargetCardPositional","Origin":"Self","TargetMode":"LeftCard","IncludeOrigin":false}""";
        yield return """{"$type":"TTargetCardPositional","Origin":"Self","TargetMode":"RightCard","IncludeOrigin":false}""";
        yield return """{"$type":"TTargetCardOccupying"}""";
    }

    private static void CollectJsonTypes(JsonElement el, HashSet<string> types)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("$type", out var tp) && tp.ValueKind == JsonValueKind.String)
                types.Add(tp.GetString()!);
            foreach (var prop in el.EnumerateObject())
                CollectJsonTypes(prop.Value, types);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                CollectJsonTypes(item, types);
        }
    }
}
