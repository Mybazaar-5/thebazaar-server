using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;
using BazaarServer.Game;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V12 机制加固专项回归套件：
/// 1. 传奇物品（Legendary）全路径禁止转化双保险（Mandala / 随机 / 相邻位置 / NonSelf / TPedestalBehaviorTransform）；
/// 2. 直接生成物品（TActionGameSpawnCards / TActionCardSpawn）受当前剩余空格限制（剩 1 格必出小型 / 塞不下直接吞掉 / Tiny 减 1 格 / IgnoreSpaceConstraint 与 DealCards/Skill/SocketEffect 豁免）。
/// </summary>
public sealed class V12HardeningMechanicsTests
{
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string BayonetId = "6146cacd-202c-489f-88c0-f77ef3e0fda6";
    private const string MandalaSmallStepId = "76e60ecc-3dbd-4b57-a7a5-8ad6d40f7abb";

    private sealed class Fx
    {
        public string DbPath { get; init; } = "";
        public CardPool Pool { get; init; } = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
        public HashSet<string> ValidCardIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Lazy<Fx> RealFixture = new(() =>
    {
        string? dataDir = Environment.GetEnvironmentVariable("BAZAAR_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir) || !File.Exists(Path.Combine(dataDir, "GameData.db")))
        {
            string? probe = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(probe); i++, probe = Directory.GetParent(probe)?.FullName)
            {
                string candidate = Path.Combine(probe, "dist", "data");
                if (File.Exists(Path.Combine(candidate, "GameData.db")))
                {
                    dataDir = candidate;
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(dataDir) || !File.Exists(Path.Combine(dataDir, "GameData.db")))
            dataDir = @"e:\thebazaar\dist\data";

        string dbPath = Path.Combine(dataDir, "GameData.db");
        var validIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string wlPath = Path.Combine(dataDir, "valid_card_ids.json");
        if (File.Exists(wlPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(wlPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("valid_ids", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    string? id = el.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) validIds.Add(id);
                }
            }
        }

        return new Fx
        {
            DbPath = dbPath,
            Pool = CardPool.Load(dbPath),
            Monsters = MonsterPool.Load(dbPath),
            LevelUps = LevelUpTable.Load(dbPath),
            Mode = GameModeConfig.Load(dbPath),
            Repo = GameDatabaseLoader.Load(dbPath, null),
            Ghosts = PvpGhostPool.Load(dataDir),
            ValidCardIds = validIds,
        };
    });

    private sealed class TempDbOverlay : IDisposable
    {
        public string TempDir { get; }
        public CardPool Pool { get; }
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; }
        public LevelUpTable LevelUps { get; }
        public GameModeConfig Mode { get; }
        public GameDataRepository Repo { get; }
        public PvpGhostPool Ghosts { get; }

        public TempDbOverlay(params (string Id, string Json)[] customCards)
        {
            var real = RealFixture.Value;
            TempDir = Path.Combine(Path.GetTempPath(), "bazaar_v12_harden_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);
            string dbPath = Path.Combine(TempDir, "GameData.db");
            File.Copy(real.DbPath, dbPath, overwrite: true);

            var wl = new HashSet<string>(real.ValidCardIds, StringComparer.OrdinalIgnoreCase);
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                foreach (var (id, json) in customCards)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT OR REPLACE INTO cards (Id, Data) VALUES ($id, $data)";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.Parameters.AddWithValue("$data", Encoding.UTF8.GetBytes(json));
                    cmd.ExecuteNonQuery();
                    wl.Add(id);
                }
            }

            File.WriteAllText(
                Path.Combine(TempDir, "valid_card_ids.json"),
                JsonSerializer.Serialize(new { valid_ids = wl.ToArray() }));

            Pool = CardPool.Load(dbPath);
            Monsters = real.Monsters;
            LevelUps = real.LevelUps;
            Mode = real.Mode;
            Repo = GameDatabaseLoader.Load(dbPath, null);
            Ghosts = real.Ghosts;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(TempDir))
            {
                try { Directory.Delete(TempDir, recursive: true); } catch { }
            }
        }
    }

    private static CardTemplate PickAnyItem(GameDataRepository repo, CardSize size, bool nonLegendary = true)
        => repo.AlwaysSpawnableItems.First(t =>
            t.CardSize == size &&
            (!nonLegendary || t.DefaultTier != CardTier.Legendary));

    private static CardTemplate PickLegendaryItem(GameDataRepository repo, CardSize size = CardSize.Small)
        => repo.CardsById.Values.FirstOrDefault(t => t.Type == "Item" && t.DefaultTier == CardTier.Legendary && t.CardSize == size)
        ?? repo.CardsById.Values.First(t => t.Type == "Item" && t.DefaultTier == CardTier.Legendary);

    private static RuleNode MakeRandomItemSpawnContext(int limit = 1, CardSize? exactSize = null, bool ignoreSpace = false)
    {
        var constraints = new List<object>
        {
            new { __type = "ConstraintCardType", Types = new[] { "Item" }, IsNot = false }
        };
        if (exactSize.HasValue)
            constraints.Add(new { __type = "ConstraintSize", Sizes = new[] { exactSize.Value.ToString() }, IsNot = false });

        var behaviors = new List<object>();
        if (ignoreSpace)
            behaviors.Add(new { __type = "TSpawnBehaviorIgnoreSpaceConstraint" });

        string json = JsonSerializer.Serialize(new
        {
            __type = "TSpawnContextQuery",
            SelectionMethod = "Random",
            Limit = new { __type = "TFixedValue", Value = (double)limit },
            Behaviors = behaviors,
            Groups = new[]
            {
                new
                {
                    __type = "TSpawnGroup",
                    SelectionMethod = "Random",
                    Filters = new[]
                    {
                        new
                        {
                            __type = "TSpawnFilterQuery",
                            Constraints = new { __type = "ConstraintAnd", Constraints = constraints }
                        }
                    }
                }
            }
        }).Replace("\"__type\"", "\"$type\"");

        return JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;
    }

    private static RuleNode MakeIdListSpawnContext(IEnumerable<string> ids, int limit = 1, bool ignoreSpace = false)
    {
        var behaviors = new List<object>();
        if (ignoreSpace)
            behaviors.Add(new { __type = "TSpawnBehaviorIgnoreSpaceConstraint" });

        string json = JsonSerializer.Serialize(new
        {
            __type = "TSpawnContextQuery",
            SelectionMethod = "Sequential",
            Limit = new { __type = "TFixedValue", Value = (double)limit },
            Behaviors = behaviors,
            Groups = new[]
            {
                new
                {
                    __type = "TSpawnGroup",
                    SelectionMethod = "Sequential",
                    Filters = new[]
                    {
                        new
                        {
                            __type = "TSpawnFilterIdList",
                            Ids = ids.ToArray()
                        }
                    }
                }
            }
        }).Replace("\"__type\"", "\"$type\"");

        return JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;
    }

    // ── 1. 传奇物品（Legendary）全路径禁止转化双保险 ─────────────────────────────

    [Fact(DisplayName = "V12_Hardening_01_LegendaryTransformBan_MandalaRandomPositionalNonSelfAndPedestal")]
    public void V12_Hardening_01_LegendaryTransformBan_AllPaths()
    {
        var fx = RealFixture.Value;
        var repo = fx.Repo;

        var smallLegTpl = PickLegendaryItem(repo, CardSize.Small);
        var smallNormalTpl = PickAnyItem(repo, CardSize.Small, nonLegendary: true);
        var sourceTpl = repo.GetCard(BayonetId)!;

        // 1A. Mandala (76e60ecc Transform leftmost Small)：最左槽位是 Legendary 物品时绝不可转化
        var mandalaStepTpl = repo.GetCard(MandalaSmallStepId)!;
        var pMandala = new PlayerState("pm", "Vanessa", HeroNames.Vanessa, 500, 20, 15, 10);
        var legLeft = new CardInstance(smallLegTpl, CardTier.Legendary, null, "leg_left");
        var normRight = new CardInstance(smallNormalTpl, CardTier.Silver, null, "norm_right");
        Assert.True(pMandala.Hand.TryPlaceOrShift(legLeft, 0));
        Assert.True(pMandala.Hand.TryPlaceOrShift(normRight, 1));
        var mandalaCard = new CardInstance(mandalaStepTpl);
        var mandalaAction = mandalaStepTpl.Abilities["0"].Action!;
        var mandalaCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, pMandala, null, new Random(11))
            .WithCards(mandalaCard);
        AbilityExecutor.ExecuteAction(mandalaAction, mandalaCard, mandalaCtx);
        Assert.Contains(legLeft, pMandala.Hand.Cards);
        Assert.Equal(smallLegTpl.Id, legLeft.Template.Id);
        // Mandala 带 TCardConditionalTier(Not Legendary) 会跳过传奇并把右侧非传奇转化为同尺寸非传奇物品
        Assert.DoesNotContain(pMandala.Hand.Cards, c => c != legLeft && c.Template.DefaultTier == CardTier.Legendary);

        // 1B. TTargetCardRandom：手牌同时含 Legendary 与普通卡，连续随机转化 20 次均只转化普通卡且绝不把任何卡变成 Legendary
        var randTransformAction = new RuleNode
        {
            Type = "TActionCardTransform",
            Target = new RuleNode { Type = "TTargetCardRandom", TargetSection = "SelfHand", ExcludeSelf = true },
            SpawnContext = MakeRandomItemSpawnContext(limit: 1),
        };
        for (int seed = 1; seed <= 20; seed++)
        {
            var pRand = new PlayerState("pr", "Vanessa", HeroNames.Vanessa, 500, 20, 15, 10);
            var src = new CardInstance(sourceTpl, CardTier.Bronze, null, "src");
            var leg = new CardInstance(smallLegTpl, CardTier.Legendary, null, "leg_rand");
            var norm = new CardInstance(smallNormalTpl, CardTier.Bronze, null, "norm_rand");
            Assert.True(pRand.Hand.TryPlaceOrShift(src, 0));
            Assert.True(pRand.Hand.TryPlaceOrShift(leg, 1));
            Assert.True(pRand.Hand.TryPlaceOrShift(norm, 2));

            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, pRand, null, new Random(seed)).WithCards(src);
            AbilityExecutor.ExecuteAction(randTransformAction, src, ctx);

            Assert.Contains(leg, pRand.Hand.Cards);
            Assert.False(leg.IsDestroyed);
            Assert.Equal(smallLegTpl.Id, leg.Template.Id);
            Assert.DoesNotContain(norm, pRand.Hand.Cards);
            Assert.All(pRand.Hand.Cards.Where(c => c != leg), c => Assert.NotEqual(CardTier.Legendary, c.Template.DefaultTier));
        }

        // 1C. TTargetCardPositional（相邻左/右卡转化）：即使 Legendary 紧贴邻位也绝不转化，另一侧普通邻卡正常转化且不变传奇
        var posTransformAction = new RuleNode
        {
            Type = "TActionCardTransform",
            Target = new RuleNode { Type = "TTargetCardPositional", TargetMode = "Neighbor" },
            SpawnContext = MakeRandomItemSpawnContext(limit: 1),
        };
        var pPos = new PlayerState("pp", "Dooley", HeroNames.Dooley, 500, 20, 15, 10);
        var posLeg = new CardInstance(smallLegTpl, CardTier.Legendary, null, "pos_leg");
        var posSrc = new CardInstance(sourceTpl, CardTier.Bronze, null, "pos_src");
        var posNorm = new CardInstance(smallNormalTpl, CardTier.Bronze, null, "pos_norm");
        Assert.True(pPos.Hand.TryPlaceOrShift(posLeg, 0));
        Assert.True(pPos.Hand.TryPlaceOrShift(posSrc, 1));
        Assert.True(pPos.Hand.TryPlaceOrShift(posNorm, 2));
        AbilityExecutor.ExecuteAction(posTransformAction, posSrc,
            new RuleExecutionContext(repo, RuleScope.InCombat, pPos, null, new Random(23)).WithCards(posSrc));
        Assert.Contains(posLeg, pPos.Hand.Cards);
        Assert.DoesNotContain(posNorm, pPos.Hand.Cards);
        Assert.All(pPos.Hand.Cards.Where(c => c != posLeg), c => Assert.NotEqual(CardTier.Legendary, c.Template.DefaultTier));

        // 1D. NonSelf 显式 ID 锁定目标转化：即便通过 TTargetCardSection + TCardConditionalId 指向非自身 Legendary，仍禁止转化
        string explicitLegJson = JsonSerializer.Serialize(new
        {
            __type = "TCardConditionalId",
            Id = smallLegTpl.Id,
            IsNot = false
        }).Replace("\"__type\"", "\"$type\"");
        var explicitLegAction = new RuleNode
        {
            Type = "TActionCardTransform",
            Target = new RuleNode
            {
                Type = "TTargetCardSection",
                TargetSection = "SelfHand",
                ExcludeSelf = true,
                RawConditionsElement = JsonSerializer.Deserialize<JsonElement>(explicitLegJson)
            },
            SpawnContext = MakeIdListSpawnContext([smallNormalTpl.Id], limit: 1),
        };
        AbilityExecutor.ExecuteAction(explicitLegAction, posSrc,
            new RuleExecutionContext(repo, RuleScope.InCombat, pPos, null, new Random(29)).WithCards(posSrc));
        Assert.Contains(posLeg, pPos.Hand.Cards);
        Assert.False(posLeg.IsDestroyed);

        // 1E. RunEngine.OnCommitPedestal：显式识别 TPedestalBehaviorTransform，对 Legendary 返回 NotAllowed("legendary cards cannot be transformed")
        string pedId = "bbbb1111-2222-3333-4444-555566667777";
        string pedJson = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterPedestal",
            Id = pedId,
            InternalName = "V12 Test Transform Pedestal",
            Type = "PedestalEncounter",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Gold",
            SpawningEligibility = "GuidOnly",
            Behavior = new
            {
                __type = "TPedestalBehaviorTransform",
                SpawnContext = new
                {
                    __type = "TSpawnContextQuery",
                    SelectionMethod = "Random",
                    Limit = new { __type = "TFixedValue", Value = 1.0 },
                    Groups = new[]
                    {
                        new
                        {
                            __type = "TSpawnGroup",
                            SelectionMethod = "Random",
                            Filters = new[]
                            {
                                new
                                {
                                    __type = "TSpawnFilterQuery",
                                    Constraints = new
                                    {
                                        __type = "ConstraintAnd",
                                        Constraints = new object[]
                                        {
                                            new { __type = "ConstraintCardType", Types = new[] { "Item" }, IsNot = false },
                                            new { __type = "ConstraintSize", Sizes = new[] { "Small" }, IsNot = false },
                                            new { __type = "ConstraintTier", Tiers = new[] { "Legendary" }, IsNot = true }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new TempDbOverlay((pedId, pedJson));
        var run = new RunState
        {
            RunId = "v12_ped_tx",
            GameModeId = ov.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Ranked,
            RngSeed = 5050,
        };
        var engine = new RunEngine(run, ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        Assert.Null(engine.StartRun().Error);

        // 添加 1 张 Legendary 物品 + 1 张非 Legendary 物品到 Hand
        var legCore = new CardInstance(smallLegTpl, CardTier.Legendary, null, "ped_leg_itm");
        Assert.True(run.HandGrid.TryPlaceOrShift(legCore, 0));
        var legEntry = new RunState.CardEntry
        {
            InstanceId = legCore.InstanceId,
            TemplateId = Guid.Parse(smallLegTpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(smallLegTpl.Size),
            Tier = ETier.Legendary,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = legCore,
        };
        RunState.PopulateMetaFromTemplate(legEntry, ov.Pool.Get(smallLegTpl.Id), smallLegTpl);
        run.Cards[legEntry.InstanceId] = legEntry;

        var normCore = new CardInstance(smallNormalTpl, CardTier.Silver, null, "ped_norm_itm");
        Assert.True(run.HandGrid.TryPlaceOrShift(normCore, 1));
        var normEntry = new RunState.CardEntry
        {
            InstanceId = normCore.InstanceId,
            TemplateId = Guid.Parse(smallNormalTpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(smallNormalTpl.Size),
            Tier = ETier.Silver,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_1,
            CoreCard = normCore,
        };
        RunState.PopulateMetaFromTemplate(normEntry, ov.Pool.Get(smallNormalTpl.Id), smallNormalTpl);
        run.Cards[normEntry.InstanceId] = normEntry;

        // 进入 TPedestalBehaviorTransform 基座
        var pedCard = new RunState.CardEntry
        {
            InstanceId = "ped_tx_choice",
            TemplateId = Guid.Parse(pedId),
            Type = ECardType.PedestalEncounter,
            Size = ECardSize.Medium,
            Tier = ETier.Gold,
        };
        run.Cards[pedCard.InstanceId] = pedCard;
        run.SelectionSet.Clear();
        run.SelectionSet.Add(pedCard.InstanceId);
        run.State = ERunState.Choice;

        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pedCard.InstanceId) }).Error);
        Assert.Equal(ERunState.Pedestal, run.State);

        var legErrOut = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(legEntry.InstanceId) });
        Assert.NotNull(legErrOut.Error);
        // 服务端报错已全量中文化（“传奇品阶卡牌无法被变形”），兼容检查中文“传奇”或英文“legendary”
        string legErrMsg = legErrOut.Error!.Message ?? "";
        Assert.True(
            legErrMsg.Contains("传奇", StringComparison.OrdinalIgnoreCase)
            || legErrMsg.Contains("legendary", StringComparison.OrdinalIgnoreCase),
            $"预期报错包含传奇不可变形提示，实际：{legErrMsg}");
        Assert.True(run.Cards.ContainsKey(legEntry.InstanceId));
        Assert.Equal(ETier.Legendary, run.Cards[legEntry.InstanceId].Tier);

        // 非传奇物品提交到 TPedestalBehaviorTransform 应真实转化而非报错或静默附魔
        var normOkOut = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(normEntry.InstanceId) });
        Assert.Null(normOkOut.Error);
        Assert.Contains(normOkOut.Events, e => e is GameSimEventCardTransformed);
        Assert.False(run.Cards.ContainsKey(normEntry.InstanceId));
    }

    // ── 2. 直接生成物品（TActionGameSpawnCards / TActionCardSpawn）受当前剩余空格限制 ──

    [Fact(DisplayName = "V12_Hardening_02_DirectSpawnSpaceConstraints_OneSlotSmall_FullSwallow_TinyMinusOne_AndExemptions")]
    public void V12_Hardening_02_DirectSpawnSpaceConstraints_AllCases()
    {
        var fx = RealFixture.Value;
        var repo = fx.Repo;

        var smallTpl = PickAnyItem(repo, CardSize.Small);
        var mediumTpl = PickAnyItem(repo, CardSize.Medium);
        var largeTpl = PickAnyItem(repo, CardSize.Large);
        var skillTpl = repo.AlwaysSpawnableSkills.First();
        var socketTpl = repo.CardsById.Values.First(t => t.Type == "SocketEffect");
        var sourceTpl = repo.GetCard(CutlassId)!;

        PlayerState CreateBoardWithFreeSlots(int freeHandSlots, int freeStashSlots)
        {
            var p = new PlayerState("p", "Vanessa", HeroNames.Vanessa, 500, 20, 15, 10);
            int occHand = Math.Max(0, 10 - freeHandSlots);
            for (int i = 0; i < occHand; i++)
                Assert.True(p.Hand.TryPlaceOrShift(new CardInstance(smallTpl, CardTier.Bronze, null, $"fh_{i}"), i));
            int occStash = Math.Max(0, 10 - freeStashSlots);
            for (int i = 0; i < occStash; i++)
                Assert.True(p.Stash.TryPlaceOrShift(new CardInstance(smallTpl, CardTier.Bronze, null, $"fs_{i}"), i));
            return p;
        }

        // 2A. 手牌与储物箱均仅剩总计 1 个空位（Hand=1, Stash=0）：未限定尺寸的随机物品生成必出 1 格物品并成功放入
        for (int seed = 1; seed <= 15; seed++)
        {
            var p = CreateBoardWithFreeSlots(freeHandSlots: 1, freeStashSlots: 0);
            var src = p.Hand.Cards[0];
            var spawnAction = new RuleNode
            {
                Type = "TActionGameSpawnCards",
                TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                SpawnContext = MakeRandomItemSpawnContext(limit: 1),
            };
            var spawnedList = new List<CardInstance>();
            var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p, null, new Random(seed))
            {
                OnCardSpawned = (_, c) => spawnedList.Add(c)
            }.WithCards(src);

            AbilityExecutor.ExecuteAction(spawnAction, src, ctx);
            var added = Assert.Single(spawnedList);
            Assert.Equal(1, added.SlotWidth);
            Assert.Equal(10, p.Hand.Cards.Sum(c => c.SlotWidth));
            Assert.True(p.Hand.ValidateInvariants(out string err), err);
        }

        // 2B. 仅剩 1 格时显式指定生成 3 格大型物品（无 Tiny）：无法塞入 -> 直接吞掉（0 新增）
        {
            var p = CreateBoardWithFreeSlots(freeHandSlots: 1, freeStashSlots: 0);
            var src = p.Hand.Cards[0];
            var largeOnlyAction = new RuleNode
            {
                Type = "TActionCardSpawn",
                TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                SpawnContext = MakeIdListSpawnContext([largeTpl.Id], limit: 1),
            };
            var spawnedList = new List<CardInstance>();
            var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p, null, new Random(7))
            {
                OnCardSpawned = (_, c) => spawnedList.Add(c)
            }.WithCards(src);
            AbilityExecutor.ExecuteAction(largeOnlyAction, src, ctx);
            Assert.Empty(spawnedList);
            Assert.Equal(1, p.Hand.CountFreeUnlockedSlots());
        }

        // 2C. 仅剩 1 格时显式指定生成 2 格中型物品并携带 action.Attributes["Tiny"]=1 -> 有效占格 2-1=1 格，成功生成并落位！
        {
            var p = CreateBoardWithFreeSlots(freeHandSlots: 0, freeStashSlots: 1);
            var src = p.Hand.Cards[0];
            var tinyMediumAction = new RuleNode
            {
                Type = "TActionGameSpawnCards",
                TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                SpawnContext = MakeIdListSpawnContext([mediumTpl.Id], limit: 1),
                Attributes = new Dictionary<string, RuleNode?>
                {
                    ["Tiny"] = new RuleNode { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement(1.0) }
                },
            };
            var spawnedList = new List<CardInstance>();
            var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p, null, new Random(9))
            {
                OnCardSpawned = (_, c) => spawnedList.Add(c)
            }.WithCards(src);
            AbilityExecutor.ExecuteAction(tinyMediumAction, src, ctx);
            var spawned = Assert.Single(spawnedList);
            Assert.True(spawned.IsTiny);
            Assert.Equal(1, spawned.SlotWidth);
            Assert.Equal(0, p.Stash.CountFreeUnlockedSlots());
            Assert.True(p.Stash.ValidateInvariants(out string err), err);
        }

        // 2D. Large+Tiny 占 2 格：剩 1 格时吞掉，剩 2 格时成功生成落位
        {
            var tinyLargeAction = new RuleNode
            {
                Type = "TActionGameSpawnCards",
                TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                SpawnContext = MakeIdListSpawnContext([largeTpl.Id], limit: 1),
                Attributes = new Dictionary<string, RuleNode?>
                {
                    ["Tiny"] = new RuleNode { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement(1.0) }
                },
            };

            var pOne = CreateBoardWithFreeSlots(freeHandSlots: 1, freeStashSlots: 0);
            var listOne = new List<CardInstance>();
            AbilityExecutor.ExecuteAction(tinyLargeAction, pOne.Hand.Cards[0],
                new RuleExecutionContext(repo, RuleScope.OutOfCombat, pOne, null, new Random(1)) { OnCardSpawned = (_, c) => listOne.Add(c) }.WithCards(pOne.Hand.Cards[0]));
            Assert.Empty(listOne);

            var pTwo = CreateBoardWithFreeSlots(freeHandSlots: 2, freeStashSlots: 0);
            var listTwo = new List<CardInstance>();
            AbilityExecutor.ExecuteAction(tinyLargeAction, pTwo.Hand.Cards[0],
                new RuleExecutionContext(repo, RuleScope.OutOfCombat, pTwo, null, new Random(1)) { OnCardSpawned = (_, c) => listTwo.Add(c) }.WithCards(pTwo.Hand.Cards[0]));
            var fitted = Assert.Single(listTwo);
            Assert.Equal(2, fitted.SlotWidth);
            Assert.Equal(0, pTwo.Hand.CountFreeUnlockedSlots());
        }

        // 2E. 一次生多件（Limit = 2）但仅剩 1 格：第 1 件生成 1 格小件塞满，第 2 件因剩余空间=0 被吞掉
        {
            var p = CreateBoardWithFreeSlots(freeHandSlots: 1, freeStashSlots: 0);
            var multiAction = new RuleNode
            {
                Type = "TActionGameSpawnCards",
                TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                SpawnContext = MakeRandomItemSpawnContext(limit: 2),
            };
            var spawnedList = new List<CardInstance>();
            AbilityExecutor.ExecuteAction(multiAction, p.Hand.Cards[0],
                new RuleExecutionContext(repo, RuleScope.OutOfCombat, p, null, new Random(15))
                {
                    OnCardSpawned = (_, c) => spawnedList.Add(c)
                }.WithCards(p.Hand.Cards[0]));

            var single = Assert.Single(spawnedList);
            Assert.Equal(1, single.SlotWidth);
            Assert.Equal(0, p.Hand.CountFreeUnlockedSlots());
        }

        // 2F. 满格（Hand=0, Stash=0）豁免检查：Item 被吞掉，Skill / SocketEffect / TActionGameDealCards / IgnoreSpaceConstraint 均不受影响！
        {
            var pFull = CreateBoardWithFreeSlots(freeHandSlots: 0, freeStashSlots: 0);
            var src = pFull.Hand.Cards[0];

            // Item -> 吞掉
            var fullItemList = new List<CardInstance>();
            AbilityExecutor.ExecuteAction(new RuleNode
            {
                Type = "TActionGameSpawnCards",
                TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                SpawnContext = MakeRandomItemSpawnContext(limit: 1),
            }, src, new RuleExecutionContext(repo, RuleScope.OutOfCombat, pFull, null, new Random(1))
            {
                OnCardSpawned = (_, c) => fullItemList.Add(c)
            }.WithCards(src));
            Assert.Empty(fullItemList);

            // Skill -> 正常生成
            var skillList = new List<CardInstance>();
            AbilityExecutor.ExecuteAction(new RuleNode
            {
                Type = "TActionGameSpawnCards",
                TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                SpawnContext = MakeIdListSpawnContext([skillTpl.Id], limit: 1),
            }, src, new RuleExecutionContext(repo, RuleScope.OutOfCombat, pFull, null, new Random(1))
            {
                OnCardSpawned = (_, c) => skillList.Add(c)
            }.WithCards(src));
            Assert.Single(skillList);
            Assert.Contains(pFull.Skills, s => s.Template.Id == skillTpl.Id);

            // SocketEffect -> 正常生成
            var socketList = new List<CardInstance>();
            AbilityExecutor.ExecuteAction(new RuleNode
            {
                Type = "TActionGameSpawnCards",
                TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                SpawnContext = MakeIdListSpawnContext([socketTpl.Id], limit: 1),
            }, src, new RuleExecutionContext(repo, RuleScope.OutOfCombat, pFull, null, new Random(1))
            {
                OnCardSpawned = (_, c) => socketList.Add(c)
            }.WithCards(src));
            Assert.Single(socketList);

            // TActionGameDealCards -> 即使满格依然正常发出 Large 候选卡
            List<CardInstance>? dealtCards = null;
            AbilityExecutor.ExecuteAction(new RuleNode
            {
                Type = "TActionGameDealCards",
                SpawnContext = MakeIdListSpawnContext([largeTpl.Id], limit: 1),
            }, src, new RuleExecutionContext(repo, RuleScope.OutOfCombat, pFull, null, new Random(1))
            {
                OnDealCardsToSelection = (cards, _) => dealtCards = cards
            }.WithCards(src));
            Assert.NotNull(dealtCards);
            Assert.Single(dealtCards!);
            Assert.Equal(largeTpl.Id, dealtCards![0].Template.Id);

            // TSpawnBehaviorIgnoreSpaceConstraint -> 允许突破空格限制溢入 Stash
            var ignoreList = new List<CardInstance>();
            AbilityExecutor.ExecuteAction(new RuleNode
            {
                Type = "TActionGameSpawnCards",
                TargetPlayer = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                SpawnContext = MakeIdListSpawnContext([largeTpl.Id], limit: 1, ignoreSpace: true),
            }, src, new RuleExecutionContext(repo, RuleScope.OutOfCombat, pFull, null, new Random(1))
            {
                OnCardSpawned = (_, c) => ignoreList.Add(c)
            }.WithCards(src));
            Assert.Single(ignoreList);
        }
    }

    // ── 3. #262 修复 A：Thieves Guild 动态解锁豁免 & ShouldProgressHour==false 不消耗小时 ──

    [Fact(DisplayName = "V12_Hardening_03_ThievesGuild_DynamicExclusionAndZeroHourExit")]
    public void V12_Hardening_03_ThievesGuild_DynamicExclusionAndZeroHourExit()
    {
        const string thievesGuildId = "e792b6a6-2f85-4190-8e3c-002d2736d03d";
        const string medallionId = "1e48bd28-cd41-49e9-9e22-b44da30ef6be";
        const string raidVaultStepId = "7bb9ab70-b864-4c0a-8d9d-0545e609ce19";
        const string gatherIntelStepId = "ba7b1001-9cd4-4f3d-9117-c757c6195e5e";

        var fx = RealFixture.Value;
        var run = new RunState
        {
            RunId = "v12_thieves_guild",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Ranked,
            RngSeed = 7711,
        };
        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        Assert.Null(engine.StartRun().Error);

        // StartRun 后已位于 Day 1 Hour 1 Choice；跳至 Day 2 Hour 1（此时 maxDayTier == Silver，而 Thieves Guild 为 Legendary）
        if (run.State != ERunState.Choice)
        {
            string startStepId = run.SelectionSet.First();
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(startStepId) }).Error);
        }
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "day", "2" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", "1" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "deal" } }).Error);
        Assert.Contains(thievesGuildId, run.ExclusionSet);

        // 给予 1 枚 Thieves Guild Medallion（真实起始品阶为 Legendary，置于 Stash 留出 Hand 空位给 Raid the Vault），
        // 进入任意 Hour 1 商店并卖出徽章
        var medPoolTpl = fx.Pool.Get(medallionId)!;
        var medCoreTpl = fx.Repo.GetCard(medallionId)!;
        var medTier = ShopEngine.ParseTier(medPoolTpl.StartingTier);
        var medCore = new CardInstance(medCoreTpl, ShopEngine.ToCoreTier(medTier), null, "itm_medallion_1");
        Assert.True(run.StashGrid.TryPlaceOrShift(medCore, 0));
        var medEntry = new RunState.CardEntry
        {
            InstanceId = medCore.InstanceId,
            TemplateId = Guid.Parse(medallionId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(medPoolTpl.Size),
            Tier = medTier,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Stash,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = medCore,
        };
        RunState.PopulateMetaFromTemplate(medEntry, medPoolTpl, medCoreTpl);
        run.Cards[medEntry.InstanceId] = medEntry;

        string hour1ChoiceId = run.SelectionSet.First();
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(hour1ChoiceId) }).Error);
        var sellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(medEntry.InstanceId) });
        Assert.Null(sellOut.Error);
        Assert.DoesNotContain(thievesGuildId, run.ExclusionSet);

        // 退出 Hour 1 遭遇推进到 Day 2 Hour 2：Thieves Guild 虽为 Legendary 仍必须豁免 Silver 天数上限并高优先入棚
        if (run.State == ERunState.Encounter)
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(2u, run.Day);
        Assert.Equal(2u, run.Hour);
        Assert.Equal(ERunState.Choice, run.State);
        var guildCard = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => string.Equals(c.TemplateId.ToString(), thievesGuildId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(guildCard);

        // 进入 Thieves Guild -> 自身 TTriggerOnCardSelected 将其重新加入 ExclusionSet 并记入当日已访问
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(guildCard!.InstanceId) }).Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Contains(thievesGuildId, run.ExclusionSet);
        Assert.Contains(thievesGuildId, run.VisitedDayEncounterIds);

        // 选择 Raid the Vault (ShouldProgressHour == false, DealsCards + CanExit=true)：发出金库物品并拿走/直接退场均不得消耗 Hour 2
        var vaultCard = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => string.Equals(c.TemplateId.ToString(), raidVaultStepId, StringComparison.OrdinalIgnoreCase));
        if (vaultCard == null)
        {
            var vaultTpl = fx.Pool.Get(raidVaultStepId)!;
            var injectedVault = new RunState.CardEntry
            {
                InstanceId = "stp_vault_test",
                TemplateId = Guid.Parse(raidVaultStepId),
                Type = ECardType.EncounterStep,
                Size = ECardSize.Medium,
                Tier = ETier.Legendary,
            };
            RunState.PopulateMetaFromTemplate(injectedVault, vaultTpl, fx.Repo.GetCard(raidVaultStepId));
            run.Cards[injectedVault.InstanceId] = injectedVault;
            run.SelectionSet.Add(injectedVault.InstanceId);
            vaultCard = injectedVault;
        }
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(vaultCard.InstanceId) }).Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.True(run.SelectionSet.Count > 0);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

        // 严禁推进到 Hour 3：原地重开 Day 2 Hour 2 Choice，且不会再次无限刷新出 Thieves Guild
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(2u, run.Day);
        Assert.Equal(2u, run.Hour);
        Assert.Equal(3, run.SelectionSet.Count);
        Assert.DoesNotContain(
            run.SelectionSet,
            id => string.Equals(run.Cards[id].TemplateId.ToString(), thievesGuildId, StringComparison.OrdinalIgnoreCase));

        // 跳到 Day 2 Hour 4（避开 Hour 3 固定野怪小时），用第二枚徽章再次解锁（并清除当日去重标记），
        // 进 Gather Intel 并在退场前设为恰好升级，验证升完级仍停在 Day 2 Hour 5
        run.PendingLevelUps = 0;
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", "4" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "deal" } }).Error);
        var med2 = new CardInstance(medCoreTpl, ShopEngine.ToCoreTier(medTier), null, "itm_medallion_2");
        Assert.True(run.StashGrid.TryPlaceOrShift(med2, 0));
        var medEntry2 = new RunState.CardEntry
        {
            InstanceId = med2.InstanceId,
            TemplateId = Guid.Parse(medallionId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(medPoolTpl.Size),
            Tier = medTier,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Stash,
            Socket = EContainerSocketId.Socket_1,
            CoreCard = med2,
        };
        RunState.PopulateMetaFromTemplate(medEntry2, medPoolTpl, medCoreTpl);
        run.Cards[medEntry2.InstanceId] = medEntry2;
        run.VisitedDayEncounterIds.Remove(thievesGuildId);

        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(run.SelectionSet.First()) }).Error);
        Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(medEntry2.InstanceId) }).Error);
        if (run.State == ERunState.Encounter)
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(2u, run.Day);
        Assert.Equal(5u, run.Hour);
        Assert.Equal(ERunState.Choice, run.State);

        var guildCard2 = run.SelectionSet
            .Select(id => run.Cards[id])
            .First(c => string.Equals(c.TemplateId.ToString(), thievesGuildId, StringComparison.OrdinalIgnoreCase));
        int incBefore = run.PlayerAttributes[EPlayerAttributeType.Income];
        int presBefore = run.PlayerAttributes[EPlayerAttributeType.Prestige];
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(guildCard2.InstanceId) }).Error);

        // 若未刷出 Gather Intel，补挂其真实步骤卡并直接点击；同时置 1 个 PendingLevelUp，验证升完级仍停在 Day 2 Hour 3
        var gatherCard = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => string.Equals(c.TemplateId.ToString(), gatherIntelStepId, StringComparison.OrdinalIgnoreCase));
        if (gatherCard == null)
        {
            var gTpl = fx.Pool.Get(gatherIntelStepId)!;
            var injectedGather = new RunState.CardEntry
            {
                InstanceId = "stp_gather_test",
                TemplateId = Guid.Parse(gatherIntelStepId),
                Type = ECardType.EncounterStep,
                Size = ECardSize.Medium,
                Tier = ETier.Legendary,
            };
            RunState.PopulateMetaFromTemplate(injectedGather, gTpl, fx.Repo.GetCard(gatherIntelStepId));
            run.Cards[injectedGather.InstanceId] = injectedGather;
            run.SelectionSet.Add(injectedGather.InstanceId);
            gatherCard = injectedGather;
        }
        run.PendingLevelUps = 1;
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(gatherCard.InstanceId) }).Error);
        Assert.Equal(incBefore + 10, run.PlayerAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(presBefore + 10, run.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.Equal(ERunState.LevelUp, run.State);

        var postGatherLvl = run.Cards[run.SelectionSet.First()];
        INetCommand gatherLvlCmd = postGatherLvl.Type switch
        {
            ECardType.Skill => new SelectSkillCommand { InstanceId = new InstanceId(postGatherLvl.InstanceId) },
            ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.EncounterStep
                => new SelectEncounterCommand { InstanceId = new InstanceId(postGatherLvl.InstanceId) },
            _ => new SelectItemCommand { InstanceId = new InstanceId(postGatherLvl.InstanceId) },
        };
        Assert.Null(engine.Handle(gatherLvlCmd).Error);
        while (run.State is ERunState.Encounter or ERunState.LevelUp)
        {
            if (run.State == ERunState.LevelUp)
            {
                var lc = run.Cards[run.SelectionSet.First()];
                INetCommand nextCmd = lc.Type switch
                {
                    ECardType.Skill => new SelectSkillCommand { InstanceId = new InstanceId(lc.InstanceId) },
                    ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.EncounterStep
                        => new SelectEncounterCommand { InstanceId = new InstanceId(lc.InstanceId) },
                    _ => new SelectItemCommand { InstanceId = new InstanceId(lc.InstanceId) },
                };
                Assert.Null(engine.Handle(nextCmd).Error);
            }
            else
            {
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            }
        }

        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(2u, run.Day);
        Assert.Equal(5u, run.Hour);
    }

    // ── 4. #262 修复 B：Day 6/9/10 Hour 1 第 4 张探险入口卡 & Travel Agent Day 2..9 ──

    [Fact(DisplayName = "V12_Hardening_04_ExpeditionDay6910FourthCardAndTravelAgentDay2To9")]
    public void V12_Hardening_04_ExpeditionDay6910FourthCardAndTravelAgentDay2To9()
    {
        var fx = RealFixture.Value;

        // 4A. Travel Agent (002d8f6d)：Day 1 哪怕高权重 8.0 仍严禁入池；Day 2..9 正常权重与高权重均合法入池并豁免 maxDayTier
        var agentBiasRules = new CustomRunRuleSet
        {
            EncounterCategoryMultipliers = new(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(EEncounterWeightCategory.EventEconomyStats)] = 0.0,
                [nameof(EEncounterWeightCategory.EventFreeDealTransform)] = 0.0,
                [nameof(EEncounterWeightCategory.EventCultAndMystery)] = 0.0,
                [nameof(EEncounterWeightCategory.EventExpeditionAgent)] = 8.0,
            }
        };

        for (uint d = 1; d <= 9; d++)
        {
            var r = new RunState
            {
                RunId = "v12_ta_d" + d,
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = d,
                Hour = 1,
                RngSeed = (int)(1000 + d),
            };
            new ShopEngine(r, fx.Pool, fx.Repo, agentBiasRules).DealHourEncounters();
            bool hasAgent = r.SelectionSet.Any(id =>
                string.Equals(r.Cards[id].TemplateId.ToString(), CardPool.TravelAgentEncounterId, StringComparison.OrdinalIgnoreCase));
            if (d == 1)
                Assert.False(hasAgent, "Day 1 不允许出现 The Travel Agent");
            else
                Assert.True(hasAgent, $"Day {d} 应当允许 The Travel Agent 入池");
        }

        // 4B. Day 6 / 9 / 10 Hour 1 各自仅当持有当天对应门票时下发第 4 张免费探险入口卡；Hour 2 或非对应日不下发
        (uint Day, string TicketId, string ExpectedRoomId)[] schedule =
        {
            (6u, CardPool.CrashSiteExpeditionTicketId, CardPool.CrashSiteExpeditionRoomId),
            (9u, CardPool.JungleExpeditionTicketId, CardPool.JungleExpeditionRoomId),
            (10u, CardPool.TinyWorldExpeditionTicketId, CardPool.TinyWorldExpeditionRoomId),
        };

        foreach (var (expDay, ticketId, expectedRoomId) in schedule)
        {
            var rExp = new RunState
            {
                RunId = $"v12_exp_d{expDay}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = expDay,
                Hour = 1,
                RngSeed = (int)(2000 + expDay),
            };
            var tCoreTpl = fx.Repo.GetCard(ticketId)!;
            var tPoolTpl = fx.Pool.Get(ticketId)!;
            var tCore = new CardInstance(tCoreTpl, CardTier.Bronze, null, $"tkt_{expDay}");
            Assert.True(rExp.StashGrid.TryPlaceOrShift(tCore, 0));
            var tEntry = new RunState.CardEntry
            {
                InstanceId = tCore.InstanceId,
                TemplateId = Guid.Parse(ticketId),
                Type = ECardType.Item,
                Size = ShopEngine.ParseSize(tPoolTpl.Size),
                Tier = ETier.Bronze,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Stash,
                Socket = EContainerSocketId.Socket_0,
                CoreCard = tCore,
            };
            RunState.PopulateMetaFromTemplate(tEntry, tPoolTpl, tCoreTpl);
            rExp.Cards[tEntry.InstanceId] = tEntry;

            var shop = new ShopEngine(rExp, fx.Pool, fx.Repo);
            shop.DealHourEncounters();
            Assert.Equal(4, rExp.SelectionSet.Count);
            var fourth = rExp.SelectionSet
                .Select(id => rExp.Cards[id])
                .Single(c => string.Equals(c.TemplateId.ToString(), expectedRoomId, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, fourth.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, -1));

            // Hour 2 下不再下发第 4 张
            rExp.Hour = 2;
            shop.DealHourEncounters();
            Assert.Equal(3, rExp.SelectionSet.Count);
        }
    }

    // ── 5. #263 服务端侧：Custom_3..Custom_11 全局追踪器 + 多选项奇遇超 10 格优先保留条件选项 + Absorb Memories 光环同步 ──

    [Fact(DisplayName = "V12_Hardening_05_PlayerEffectsAndMultiOptionPriority_EndToEnd")]
    public void V12_Hardening_05_PlayerEffectsAndMultiOptionPriority_EndToEnd()
    {
        const string shroudedFigureId = "818fcd1d-4cfd-4629-b83f-8d5e753dd8f4";
        const string absorbMemoriesId = "5fff0970-87d1-4e43-a466-b771deaf40fe";
        const string langxianId = "53099361-599f-4c1a-ad4a-d2fbb491b756";
        const string ancientLocketId = "d641c57b-ab42-4b2f-b4f4-addd18eb7100";
        const string orangeJulianId = "f3c0551a-782b-44e3-b0da-e3825366d20e";
        const string wantedPosterId = "25035a36-1105-4ef9-81b0-c5bb6926415b";
        const string aquashieldShopId = "77771111-2222-3333-4444-555566667777";
        const string transformPedId = "77772222-3333-4444-5555-666677778888";

        var fx = RealFixture.Value;
        var weaponAquaticTpl = fx.Repo.AlwaysSpawnableItems.First(t =>
            t.Tags.Contains("Weapon", StringComparer.OrdinalIgnoreCase) &&
            t.Tags.Contains("Aquatic", StringComparer.OrdinalIgnoreCase) &&
            t.CardSize == CardSize.Small);
        var reagentTpl = fx.Repo.CardsById.Values.First(t =>
            t.Type == "Item" &&
            t.DefaultTier != CardTier.Legendary &&
            t.Tags.Contains("Reagent", StringComparer.OrdinalIgnoreCase) &&
            t.CardSize == CardSize.Small);

        string aqShopJson = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterEvent",
            Id = aquashieldShopId,
            InternalName = "V12 Tracking Test Shop",
            Type = "EventEncounter",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "GuidOnly",
            Tags = new[] { "Merchant" },
            SelectionContext = new
            {
                SpawnContext = new
                {
                    __type = "TSpawnContextQuery",
                    SelectionMethod = "Sequential",
                    Limit = new { __type = "TFixedValue", Value = 1.0 },
                    Groups = new[]
                    {
                        new
                        {
                            __type = "TSpawnGroup",
                            SelectionMethod = "Sequential",
                            Filters = new[] { new { __type = "TSpawnFilterIdList", Ids = new[] { weaponAquaticTpl.Id } } }
                        }
                    }
                },
                Rules = new { CanSelectMultiple = true, SelectionIsFree = false, CanExit = true }
            }
        }).Replace("\"__type\"", "\"$type\"");

        string reagentPedJson = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterPedestal",
            Id = transformPedId,
            InternalName = "V12 Reagent Transform Pedestal",
            Type = "PedestalEncounter",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Gold",
            SpawningEligibility = "GuidOnly",
            Behavior = new
            {
                __type = "TPedestalBehaviorTransform",
                SpawnContext = MakeIdListSpawnContext([CutlassId], limit: 1)
            }
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new TempDbOverlay((aquashieldShopId, aqShopJson), (transformPedId, reagentPedJson));

        var tourneyRun = new RunState
        {
            RunId = "v12_pe_track",
            GameModeId = ov.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Tournament,
            TournamentSessionId = Guid.NewGuid(),
            RngSeed = 8844,
        };
        var engine = new RunEngine(tourneyRun, ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        Assert.Null(engine.StartRun().Error);

        // 1. Custom_11 (Tournament Mode) 置 1
        Assert.Equal(1, tourneyRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_11, 0));

        // 2. 购入 Weapon+Aquatic 卡累加 Custom_4 & Custom_10，卖卡获金币累加 Custom_3
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "gold", "100" } }).Error);
        var shopPoolTpl = ov.Pool.Get(aquashieldShopId)!;
        var shopEntry = new RunState.CardEntry
        {
            InstanceId = "enc_track_shop",
            TemplateId = Guid.Parse(aquashieldShopId),
            Type = ECardType.EventEncounter,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
        };
        RunState.PopulateMetaFromTemplate(shopEntry, shopPoolTpl, ov.Repo.GetCard(aquashieldShopId));
        tourneyRun.Cards[shopEntry.InstanceId] = shopEntry;
        tourneyRun.SelectionSet.Add(shopEntry.InstanceId);

        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopEntry.InstanceId) }).Error);
        string buyItemId = tourneyRun.SelectionSet.First(id =>
            string.Equals(tourneyRun.Cards[id].TemplateId.ToString(), weaponAquaticTpl.Id, StringComparison.OrdinalIgnoreCase));
        Assert.Null(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(buyItemId), Section = EInventorySection.Hand }).Error);
        Assert.Equal(1, tourneyRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_4, 0));
        Assert.Equal(1, tourneyRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_10, 0));

        int custom3BeforeSell = tourneyRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0);
        Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(buyItemId) }).Error);
        Assert.True(tourneyRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0) > custom3BeforeSell);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

        // 3. 放入 Reagent 并在 Pedestal 变形累加 Custom_9
        var reagCore = new CardInstance(ov.Repo.GetCard(reagentTpl.Id)!, ShopEngine.ToCoreTier(ShopEngine.ParseTier(reagentTpl.StartingTier)), null, "itm_reag");
        Assert.True(tourneyRun.StashGrid.TryPlaceOrShift(reagCore, 0));
        var reagEntry = new RunState.CardEntry
        {
            InstanceId = reagCore.InstanceId,
            TemplateId = Guid.Parse(reagentTpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(reagentTpl.Size),
            Tier = ShopEngine.ParseTier(reagentTpl.StartingTier),
            Owner = ECombatantId.Player,
            Section = EInventorySection.Stash,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = reagCore,
        };
        RunState.PopulateMetaFromTemplate(reagEntry, ov.Pool.Get(reagentTpl.Id), ov.Repo.GetCard(reagentTpl.Id));
        tourneyRun.Cards[reagEntry.InstanceId] = reagEntry;

        var pedEntry = new RunState.CardEntry
        {
            InstanceId = "ped_reag_test",
            TemplateId = Guid.Parse(transformPedId),
            Type = ECardType.PedestalEncounter,
            Size = ECardSize.Medium,
            Tier = ETier.Gold,
        };
        RunState.PopulateMetaFromTemplate(pedEntry, ov.Pool.Get(transformPedId), ov.Repo.GetCard(transformPedId));
        tourneyRun.Cards[pedEntry.InstanceId] = pedEntry;
        tourneyRun.SelectionSet.Add(pedEntry.InstanceId);

        void DrainToChoice()
        {
            while (tourneyRun.State is ERunState.Loot or ERunState.LevelUp or ERunState.Encounter or ERunState.Pedestal)
            {
                if (tourneyRun.State == ERunState.LevelUp && tourneyRun.SelectionSet.Count > 0)
                {
                    var lc = tourneyRun.Cards[tourneyRun.SelectionSet.First()];
                    INetCommand cmd = lc.Type switch
                    {
                        ECardType.Skill => new SelectSkillCommand { InstanceId = new InstanceId(lc.InstanceId) },
                        ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.EncounterStep
                            => new SelectEncounterCommand { InstanceId = new InstanceId(lc.InstanceId) },
                        _ => new SelectItemCommand { InstanceId = new InstanceId(lc.InstanceId) },
                    };
                    Assert.Null(engine.Handle(cmd).Error);
                }
                else
                {
                    Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                }
            }
        }

        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pedEntry.InstanceId) }).Error);
        Assert.Null(engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(reagEntry.InstanceId) }).Error);
        Assert.Equal(1, tourneyRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_9, 0));
        DrainToChoice();

        // 4. 真实战斗积累 Custom_5 (总胜场 >= 2)；再置强力武器打空手胜战测试（加装两把武器确保打赢 Day 1 Hour 3 野怪与 Day 1 Hour 6 PvP）
        void EquipOwned(string tplId, EInventorySection sec, int slot)
        {
            var cTpl = ov.Repo.GetCard(tplId)!;
            var pTpl = ov.Pool.Get(tplId);
            var tier = ShopEngine.ParseTier(cTpl.StartingTier);
            var c = new CardInstance(cTpl, ShopEngine.ToCoreTier(tier), null, "eq_" + Guid.NewGuid().ToString("N")[..6]);
            BoardGrid g = sec == EInventorySection.Stash ? tourneyRun.StashGrid : tourneyRun.HandGrid;
            Assert.True(g.TryPlaceOrShift(c, slot));
            var e = new RunState.CardEntry
            {
                InstanceId = c.InstanceId,
                TemplateId = Guid.Parse(tplId),
                Type = ECardType.Item,
                Size = ShopEngine.ParseSize(cTpl.Size),
                Tier = tier,
                Owner = ECombatantId.Player,
                Section = sec,
                Socket = (EContainerSocketId)c.StartSlot,
                CoreCard = c,
            };
            RunState.PopulateMetaFromTemplate(e, pTpl, cTpl);
            tourneyRun.Cards[e.InstanceId] = e;
        }

        EquipOwned(CutlassId, EInventorySection.Hand, 3);
        EquipOwned(BayonetId, EInventorySection.Hand, 5);

        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", "3" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "deal" } }).Error);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(tourneyRun.SelectionSet.First()) }).Error);
        DrainToChoice();
        int winsAfterPve = tourneyRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_5, 0);
        int pveLosses = tourneyRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_7, 0);
        Assert.True(winsAfterPve + pveLosses >= 1);

        // 构造胜场确保 Custom_5 >= 2，再放入 Langxian + OrangeJulian + WantedPoster + AncientLocket（4 件特殊条件道具使 Shrouded Figure 超过 5 选项）
        if (winsAfterPve < 2)
        {
            for (int f = 0; f < 2; f++)
            {
                Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", "3" } }).Error);
                Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "deal" } }).Error);
                Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(tourneyRun.SelectionSet.First()) }).Error);
                DrainToChoice();
            }
        }
        int realWins = tourneyRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_5, 0);
        Assert.True(realWins >= 1);

        // 5. 放入狼筅（手牌/背包）以及另外 3 件在 Shrouded Figure 触发特殊分支的道具，使其拥挤超 5 项 (> 10 格)
        EquipOwned(langxianId, EInventorySection.Stash, 2);
        EquipOwned(ancientLocketId, EInventorySection.Stash, 5);
        EquipOwned(orangeJulianId, EInventorySection.Stash, 6);
        EquipOwned(wantedPosterId, EInventorySection.Stash, 8);

        var langxianEntry = tourneyRun.Cards.Values.First(c =>
            c.Owner == ECombatantId.Player &&
            string.Equals(c.TemplateId.ToString(), langxianId, StringComparison.OrdinalIgnoreCase));
        int dmgBeforeAbsorb = langxianEntry.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0);
        int custom0OnLangxian = langxianEntry.Attributes.GetValueOrDefault(ECardAttributeType.Custom_0, 0);

        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", "2" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "deal" } }).Error);
        var sfEntry = new RunState.CardEntry
        {
            InstanceId = "enc_sf_test",
            TemplateId = Guid.Parse(shroudedFigureId),
            Type = ECardType.EventEncounter,
            Size = ECardSize.Medium,
            Tier = ETier.Gold,
        };
        RunState.PopulateMetaFromTemplate(sfEntry, ov.Pool.Get(shroudedFigureId), ov.Repo.GetCard(shroudedFigureId));
        tourneyRun.Cards[sfEntry.InstanceId] = sfEntry;
        tourneyRun.SelectionSet.Add(sfEntry.InstanceId);

        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(sfEntry.InstanceId) }).Error);
        var absorbCard = tourneyRun.SelectionSet
            .Select(id => tourneyRun.Cards[id])
            .FirstOrDefault(c => string.Equals(c.TemplateId.ToString(), absorbMemoriesId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(absorbCard);

        var absorbOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(absorbCard!.InstanceId) });
        Assert.Null(absorbOut.Error);
        Assert.Equal(realWins, langxianEntry.Attributes.GetValueOrDefault(ECardAttributeType.Custom_1, -1));
        if (custom0OnLangxian > 0)
        {
            Assert.True(
                langxianEntry.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0) >= dmgBeforeAbsorb + realWins * custom0OnLangxian,
                "选中 Absorb Memories 后必须立刻重算光环并将 DamageAmount 写回 RunState.Cards");
        }

        // 6. 快照序列化往返后 Custom_3..Custom_11 与 Langxian DamageAmount 保持完整
        string snapJson = RunSnapshotSerializer.Serialize(tourneyRun);
        var restored = RunSnapshotSerializer.Deserialize(snapJson, ov.Repo)!;
        Assert.Equal(1, restored.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_11, 0));
        Assert.Equal(realWins, restored.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_5, 0));
        Assert.Equal(1, restored.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_4, 0));
        Assert.Equal(1, restored.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_10, 0));
        Assert.Equal(1, restored.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_9, 0));
        Assert.True(restored.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0) > 0);
        Assert.Equal(
            langxianEntry.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0),
            restored.Cards[langxianEntry.InstanceId].Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0));
    }
}
