using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Dataset.Archetypes;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarServer.Data;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V9 第 8 英雄双龙（The Dragons / EHero.Hero8）全链路专项测试：
/// 覆盖 Hero8 元信息、卡池/Always 汉化、12 套流派统计、开局/Day1-5商店/Lv2音符奖励、
/// 主动与被动 Tempo 获能/消耗触发/消耗减免、CombatSimConverter 帧事件同步，以及七音阶音符槽循环占位。
/// </summary>
public sealed class V9Hero8DragonsTests
{
    private const string DragonsHideoutId = "d33129a9-5df2-47e5-9b6a-7469743fe5a8";
    private const string VisorId = "b83790e4-e5a4-4c03-9bf5-55e286caf402";
    private const string SoundEngineerId = "3df1d01b-db01-4970-b045-2401382a5925";
    private const string VocoderId = "98aa3e24-d793-408b-b20b-8ce98cd4708b";
    private const string StraightEdgeId = "43f30c00-1741-4141-8fdc-eb7d597428e0";
    private const string DeathMetalDrumKitId = "3358ca56-8efc-4ace-89de-bea24a684a4e";
    private const string CymbalId = "719c5db8-85ff-41ca-8494-25741c8c5f1d";
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string StoveSocketId = "c017c0dd-af3c-47e0-b510-3661d9dcd7f2";

    private static readonly string[] NoteSocketIds =
    [
        "a9adbfdc-567e-4fe6-a9e3-7eded0bc1279", // A
        "9d043620-fcd5-40d7-94c0-208d31480562", // B
        "c82b1942-e666-495e-8ed2-54ad13e15c97", // C
        "38686c0c-ef93-4fb3-81ce-4f5f6d918f11", // D
        "001092a2-3703-4f7d-b686-ce812f6622ea", // E
        "04eca54a-69bf-4874-8b6d-56d284bb58be", // F
        "681de13d-5388-4638-b2e7-20bc566fdda1", // G
    ];

    private static readonly HashSet<string> Hero8Lv2NoteStepIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "89a23d96-b41c-462b-8513-0ef7a0d666a3",
        "c9a6b5ee-fb0e-42fb-9613-41ae3c75b940",
        "76f14255-0093-44b4-b8f3-e295460c8ef4",
        "6068cb90-fbbb-4000-beaa-561084a5222a",
    };

    private sealed class RealFixture
    {
        public string DataDir { get; init; } = "";
        public string DbPath { get; init; } = "";
        public GameDataRepository Repo { get; init; } = null!;
        public CardPool Pool { get; init; } = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
        public CardArchetypeIndex Archetypes { get; init; } = null!;
    }

    private static readonly Lazy<RealFixture> Fx = new(() =>
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++, dir = Directory.GetParent(dir)?.FullName)
        {
            string candidate = Path.Combine(dir, "dist", "data");
            if (File.Exists(Path.Combine(candidate, "ghost_pool_balanced.jsonl")))
            {
                dir = candidate;
                break;
            }
        }
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            dir = @"e:\thebazaar\dist\data";

        string dbPath = File.Exists(Path.Combine(dir, "GameData.db"))
            ? Path.Combine(dir, "GameData.db")
            : (SteamPathResolver.ResolveGameDataSourcePath() ?? Path.Combine(dir, "GameData.db"));
        string? zhPath = File.Exists(Path.Combine(dir, "translations", "zh-CN.bytes"))
            ? Path.Combine(dir, "translations", "zh-CN.bytes")
            : SteamPathResolver.ResolveZhCnTranslationPath();

        var repo = GameDatabaseLoader.Load(dbPath, zhPath);
        var pool = CardPool.Load(dbPath);
        var archetypes = CardArchetypeIndex.Build(pool, repo);
        return new RealFixture
        {
            DataDir = dir,
            DbPath = dbPath,
            Repo = repo,
            Pool = pool,
            Monsters = MonsterPool.Load(dbPath),
            LevelUps = LevelUpTable.Load(dbPath),
            Mode = GameModeConfig.Load(dbPath),
            Ghosts = PvpGhostPool.Load(dir),
            Archetypes = archetypes,
        };
    });

    [Fact]
    public void Test01_Hero8_Meta_CardPool_OfficialZh_And_ArchetypeCatalog()
    {
        var fx = Fx.Value;

        var meta = HeroNames.GetHeroProfile(HeroNames.Hero8);
        Assert.Equal("双龙 (The Dragons)", meta.DisplayName);
        Assert.Equal("双生乐龙", meta.Title);
        Assert.Contains("节奏值", meta.Summary);

        // 验证 Hero8 合法 Always 卡池与 zh-CN 汉化全覆盖
        Assert.True(fx.Pool.ZhTranslationLoaded, "应已加载官方 zh-CN.bytes 翻译词库");
        var hero8AlwaysCards = fx.Pool.OfType("Item")
            .Concat(fx.Pool.OfType("Skill"))
            .Where(t => fx.Pool.IsPlayerSpawnable(t, allowGuidOnly: false) &&
                        t.Heroes.Contains(HeroNames.Hero8, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(hero8AlwaysCards.Count >= 240, $"Hero8 合法 Always 物品/技能卡应 >= 240，实际 {hero8AlwaysCards.Count}");
        Assert.True(hero8AlwaysCards.Count(c => c.Type == "Item") >= 100);
        Assert.True(hero8AlwaysCards.Count(c => c.Type == "Skill") >= 100);
        Assert.All(hero8AlwaysCards, card =>
        {
            Assert.True(fx.Pool.IsAssetValid(card.Id), $"{card.InternalName} ({card.Id}) 应通过资产白名单");
            Assert.True(fx.Pool.HasZhTranslation(card), $"{card.InternalName} ({card.Id}) 应具备官方 zh-CN 译名");
        });

        // 验证 CardArchetypeIndex 必须为 Hero8 返回 18 维流派可用卡数统计与全部流派蓝图
        var heroCounts = fx.Archetypes.GetHeroCounts();
        Assert.True(heroCounts.TryGetValue(HeroNames.Hero8, out var hero8Counts), "GetHeroCounts 应包含 Hero8");
        Assert.Equal(CardArchetypeCatalog.All.Count, hero8Counts!.Count);
        Assert.True(hero8Counts.Values.Sum() > 0, "Hero8 在 18 维流派下的可用卡统计应大于 0");

        var catalog = new ArchetypeCatalog(fx.Repo);
        var hero8Archetypes = catalog.GetByHero(HeroNames.Hero8);
        Assert.Equal(12, hero8Archetypes.Count);
    }

    [Fact]
    public void Test02_Hero8_RunStart_Day1To5Shops_Hideout_And_Level2Notes()
    {
        var fx = Fx.Value;

        var run = new RunState
        {
            RunId = "hero8_run_" + Guid.NewGuid().ToString("N")[..8],
            Hero = EHero.Hero8,
            RngSeed = 20260415,
        };
        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, fx.Archetypes);
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);
        Assert.Equal(EHero.Hero8, run.Hero);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(0b00_0111_1000, run.UnlockedSlots);
        Assert.True(run.PlayerAttributes.TryGetValue(EPlayerAttributeType.Tempo, out int initialTempo));
        Assert.Equal(0, initialTempo);

        // 验证开局房间映射为 The Dragons' Hideout
        var shop = new ShopEngine(run, fx.Pool, fx.Repo, run.CustomRules, fx.Archetypes);
        Assert.Equal(DragonsHideoutId, ShopEngine.GetStarterEncounterId(EHero.Hero8));
        Assert.Equal(DragonsHideoutId, shop.ResolveValidStarterEncounterId(EHero.Hero8));

        // 连续模拟 Day 1 ~ Day 5 遭遇与商店刷卡，均能稳定产出合法且已汉化的双龙物品/技能
        var chances = new TierChances();
        int totalHero8ExclusiveOffers = 0;
        for (int day = 1; day <= 5; day++)
        {
            run.Day = (uint)day;
            run.Hour = 1;
            var ps = new PlayerState("p1", "Dragons", HeroNames.Hero8, 300 + day * 150, 20, 20, 10)
            {
                Day = day,
                Hour = 1,
                Level = day,
            };
            shop.DealHourEncounters(ps);
            Assert.Equal(3, run.SelectionSet.Count);

            var offers = fx.Pool.RollShopItems(HeroNames.Hero8, day, 5, chances);
            Assert.Equal(5, offers.Count);
            foreach (var (card, _) in offers)
            {
                Assert.True(fx.Pool.IsAssetValid(card.Id));
                Assert.True(fx.Pool.HasZhTranslation(card));
                if (card.Heroes.Contains(HeroNames.Hero8, StringComparer.OrdinalIgnoreCase))
                    totalHero8ExclusiveOffers++;
            }
        }
        Assert.True(totalHero8ExclusiveOffers >= 5, $"Day 1~5 商店应频繁刷出 Hero8 专属物品，实际 {totalHero8ExclusiveOffers}");

        // 验证 Hero8 Lv2->Lv3（targetNewLevel=3 -> DB Id=2）专属非标准单选门槛，且 RollRewards 稳定给出 Add a Random Note (Level Up)
        var lv3Player = new PlayerState("p1", "Dragons", HeroNames.Hero8, 350, 20, 10, 10) { Level = 3, Day = 2, Hour = 2 };
        lv3Player.Hand.UnlockedMask = 0x3FF;
        var lv3Ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, lv3Player, lv3Player, new Random(7))
        {
            CurrentDay = 2,
            CurrentHour = 2,
        };
        Assert.False(fx.LevelUps.IsStandardMultiChoiceLevel(3, lv3Ctx));
        var lv3Rewards = fx.LevelUps.RollRewards(3, lv3Ctx, id => fx.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
        Assert.NotEmpty(lv3Rewards);
        Assert.Contains(lv3Rewards, id => Hero8Lv2NoteStepIds.Contains(id));
    }

    [Fact]
    public void Test03_Hero8_TempoApply_CostSpend_Triggers_And_CombatSimConverter()
    {
        var fx = Fx.Value;

        // 1. 验证 PercentTempoCostReduction 对 Death Metal Drum Kit 的实际扣费缩减（Gold TempoCost=4，Shiny 减免 50% -> 2）
        var drumTpl = fx.Repo.GetCard(DeathMetalDrumKitId)!;
        var pDrum = new PlayerState("p1", "P1", HeroNames.Hero8, 1000, 20, 0, 10);
        var drum = new CardInstance(drumTpl, CardTier.Gold, "Shiny") { StartSlot = 0 };
        pDrum.Hand.Cards.Add(drum);
        Assert.Equal(4, (int)Math.Round(drum.GetAttribute("TempoCost")));
        AuraEngine.RecalculateAuras(pDrum, null, RuleScope.InCombat, 1, 1, fx.Repo);
        Assert.Equal(2, (int)Math.Round(drum.GetAttribute("TempoCost")));

        // 2. 实战联合验证：Visor(产Tempo) + Sound Engineer(耗1Tempo提升Crit) + Vocoder(耗Tempo触发加速) + Straight Edge(耗Tempo提升Crit)
        var p1 = new PlayerState("player", "Dragons", HeroNames.Hero8, 2500, 20, 0, 10);
        var p2 = new PlayerState("opponent", "Dummy", HeroNames.Vanessa, 2500, 20, 0, 10);
        p1.Hand.UnlockedMask = 0x3FF;
        p2.Hand.UnlockedMask = 0x3FF;

        var visor = new CardInstance(fx.Repo.GetCard(VisorId)!, CardTier.Gold) { StartSlot = 0 };             // 4s +2 Tempo
        var engineer = new CardInstance(fx.Repo.GetCard(SoundEngineerId)!, CardTier.Silver) { StartSlot = 1 }; // 2s 耗 1 Tempo
        var vocoder = new CardInstance(fx.Repo.GetCard(VocoderId)!, CardTier.Bronze) { StartSlot = 2 };        // 耗 Tempo 时加速
        var straightEdge = new CardInstance(fx.Repo.GetCard(StraightEdgeId)!, CardTier.Bronze) { StartSlot = 3 }; // 耗 Tempo 时加 CritChance
        p1.Hand.Cards.AddRange([visor, engineer, vocoder, straightEdge]);

        // 对手放置普通武器做靶标
        var oppCard = new CardInstance(fx.Repo.GetCard(CutlassId)!, CardTier.Bronze) { StartSlot = 0 };
        p2.Hand.Cards.Add(oppCard);

        int initialEdgeCrit = (int)Math.Round(straightEdge.GetAttribute("CritChance"));
        var simResult = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 42, combatType: "PVP", recordTimeline: true, recordEvents: true);

        Assert.Contains(simResult.Events, e => e.Type == CombatEventType.PlayerTempoApplied && e.SourceCardInstanceId == visor.InstanceId);
        Assert.Contains(simResult.Events, e => e.Type == CombatEventType.ActionCostSpent
                                            && e.SourceCardInstanceId == engineer.InstanceId
                                            && string.Equals(e.Detail, "Tempo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(simResult.Timeline, t => t.P1Tempo > 0);
        Assert.Contains(simResult.Timeline, t => t.Cards.Any(cf => cf.InstanceId == straightEdge.InstanceId && cf.CritChance > initialEdgeCrit));

        // 3. 验证 CombatSimConverter 将 Tempo 属性变化、CostSpent 与 PlayerTempoApply VFX 转换为客户端协议帧
        static RunState.CardEntry ToEntry(CardInstance ci, ECombatantId owner, EContainerSocketId sock) => new()
        {
            InstanceId = "ent_" + ci.InstanceId,
            TemplateId = Guid.Parse(ci.Template.Id),
            Type = ECardType.Item,
            Tier = Enum.Parse<ETier>(ci.Tier.ToString()),
            Owner = owner,
            Section = EInventorySection.Hand,
            Socket = sock,
            CoreCard = ci,
        };

        var p1Entries = new List<RunState.CardEntry>
        {
            ToEntry(visor, ECombatantId.Player, EContainerSocketId.Socket_0),
            ToEntry(engineer, ECombatantId.Player, EContainerSocketId.Socket_1),
            ToEntry(vocoder, ECombatantId.Player, EContainerSocketId.Socket_2),
            ToEntry(straightEdge, ECombatantId.Player, EContainerSocketId.Socket_3),
        };
        var p2Entries = new List<RunState.CardEntry>
        {
            ToEntry(oppCard, ECombatantId.Opponent, EContainerSocketId.Socket_0),
        };
        var p1Map = p1Entries.ToDictionary(e => e.InstanceId, e => e.CoreCard!);
        var p2Map = p2Entries.ToDictionary(e => e.InstanceId, e => e.CoreCard!);

        var combatSim = CombatSimConverter.ToCombatSim(simResult, fx.Repo, p1Map, p2Map, p1Entries, p2Entries);

        var tempoAttrUpdates = combatSim.Frames
            .Where(f => f.PlayerUpdates?.Attributes != null && f.PlayerUpdates.Attributes.ContainsKey(EPlayerAttributeType.Tempo))
            .Select(f => f.PlayerUpdates!.Attributes[EPlayerAttributeType.Tempo])
            .ToList();
        Assert.NotEmpty(tempoAttrUpdates);
        Assert.Contains(tempoAttrUpdates, u => u.CurrentValue > u.PreviousValue);

        var costSpentEvents = combatSim.Frames
            .SelectMany(f => f.Events)
            .OfType<CombatSimEventCardActionCostSpent>()
            .ToList();
        Assert.Contains(costSpentEvents, ev => ev.PlayerAttributeSpent == EPlayerAttributeType.Tempo
                                            && ev.ExecutingCard.Value == "ent_" + engineer.InstanceId);

        var tempoApplyEffects = combatSim.Frames
            .SelectMany(f => f.Events)
            .OfType<CombatSimEventEffectExecuted>()
            .Where(ev => ev.ActionType == EActionCommandType.PlayerTempoApply)
            .ToList();
        Assert.Contains(tempoApplyEffects, ev => ev.Source?.Value == "ent_" + visor.InstanceId);

        Assert.True(combatSim.CardStats.ContainsKey("ent_" + visor.InstanceId));
        Assert.True(combatSim.CardStats["ent_" + visor.InstanceId][ECardStats.TempoAdded] > 0);
        Assert.True(combatSim.CardStats.ContainsKey("ent_" + engineer.InstanceId));
        Assert.True(combatSim.CardStats["ent_" + engineer.InstanceId][ECardStats.TempoSpent] > 0);
    }

    [Fact]
    public void Test04_Hero8_PassiveTempoGainCooldown_OnlyActivatesWithTempoCards()
    {
        var fx = Fx.Value;

        // P1 仅携带有 TempoCost=1 的 Cymbal（无其他产 Tempo 卡）；P2 无任何节奏卡
        var p1 = new PlayerState("player", "Dragons", HeroNames.Hero8, 1200, 20, 0, 10);
        var p2 = new PlayerState("opponent", "Vanilla", HeroNames.Vanessa, 1200, 20, 0, 10);
        p1.Hand.UnlockedMask = 0x3FF;
        p2.Hand.UnlockedMask = 0x3FF;

        var cymbal = new CardInstance(fx.Repo.GetCard(CymbalId)!, CardTier.Bronze) { StartSlot = 0 };
        var cutlass = new CardInstance(fx.Repo.GetCard(CutlassId)!, CardTier.Bronze) { StartSlot = 0 };
        p1.Hand.Cards.Add(cymbal);
        p2.Hand.Cards.Add(cutlass);

        var result = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 99, combatType: "PVP", recordTimeline: true, recordEvents: true);

        // 到 5000ms 时 P1 应通过被动 TempoGainCooldown (2000ms/点) 积累到 Tempo，支持 Cymbal 消耗 1 点 Tempo 开火
        Assert.Contains(result.Timeline, t => t.TimeMs >= 2000 && t.P1Tempo >= 1);
        Assert.Contains(result.Events, e => e.Type == CombatEventType.ActionCostSpent
                                         && e.SourceCardInstanceId == cymbal.InstanceId
                                         && string.Equals(e.Detail, "Tempo", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Timeline, t => Assert.Equal(0, t.P2Tempo));
    }

    [Fact]
    public void Test05_Hero8_MusicNoteSockets_Modulo7_And_RunSnapshotRoundTrip()
    {
        var fx = Fx.Value;
        var rng = new Random(2026);

        // 1. 验证 7 张官方音符槽效果模板（A~G）全部可从 Repo 加载且带合法 MusicNote
        for (int i = 0; i < NoteSocketIds.Length; i++)
        {
            var tpl = fx.Repo.GetCard(NoteSocketIds[i]);
            Assert.NotNull(tpl);
            Assert.Equal("TCardMusicNoteSocketEffect", tpl!.TypeDiscriminator);
            Assert.True(tpl.MusicNote.HasValue);
        }

        // 2. 验证 TActionGameSpawnCards 模 7 循环槽位放置与 TRunConditionalMusicNoteHasValidSocket 一致性
        var ps = new PlayerState("p1", "Dragons", HeroNames.Hero8, 300, 20, 10, 10);
        ps.Hand.UnlockedMask = 0x3FF; // 全部 10 格解锁

        var noteB = new CardInstance(fx.Repo.GetCard(NoteSocketIds[1])!) { StartSlot = 1 };
        ps.SocketEffects.Add(noteB); // B(index=1) 落在槽 1 作为锚点

        // C(index=2) 与 B(index=1) 差 +1 -> 仅允许槽 2 与槽 9；先把普通灶台放槽 2 验证可覆盖
        var stove = new CardInstance(fx.Repo.GetCard(StoveSocketId)!) { StartSlot = 2 };
        ps.SocketEffects.Add(stove);

        var condC = new RuleNode
        {
            Type = "TRunConditionalMusicNoteHasValidSocket",
            MusicNote = JsonDocument.Parse("\"C\"").RootElement.Clone(),
        };
        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, ps, null, rng);
        Assert.True(ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(condC, ctx));

        var spawnNoteC = JsonSerializer.Deserialize<RuleNode>($$"""
            {
              "$type": "TActionGameSpawnCards",
              "SpawnContext": {
                "$type": "TSpawnContextQuery",
                "Limit": { "$type": "TFixedValue", "Value": 1.0 },
                "Behaviors": [{ "$type": "TSpawnBehaviorAllowDuplicates", "AllowDuplicates": true }],
                "Groups": [
                  {
                    "$type": "TSpawnGroup",
                    "Limit": { "$type": "TFixedValue", "Value": 1.0 },
                    "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{NoteSocketIds[2]}}"] }]
                  }
                ]
              }
            }
            """, GameDatabaseLoader.JsonOptions)!;

        Assert.True(AbilityExecutor.ExecuteAction(spawnNoteC, noteB, ctx));
        var placedC = ps.SocketEffects.Single(se => se.Template.Id == NoteSocketIds[2]);
        Assert.True(placedC.StartSlot is 2 or 9, $"C 音符应按模 7 规律落入槽 2 或 9，实际 slot={placedC.StartSlot}");

        // 再通过 TActionGameSpawnCards 放置第 2 枚 C 音符：将覆盖普通灶台并填满另一个模 7 槽位，随后 condC 返回 false
        Assert.True(AbilityExecutor.ExecuteAction(spawnNoteC, noteB, ctx));
        Assert.True(stove.IsDestroyed, "普通灶台应被后续同音阶音符覆盖销毁");
        var allNotesC = ps.SocketEffects.Where(se => se.Template.Id == NoteSocketIds[2]).ToList();
        Assert.Equal(2, allNotesC.Count);
        Assert.Equal([2, 9], allNotesC.Select(se => se.StartSlot).OrderBy(x => x).ToArray());
        Assert.False(ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(condC, ctx));

        // 3. 验证含音符槽 SocketEffect 与 Hero8 状态的 RunSnapshot 序列化往返无损
        var run = new RunState
        {
            RunId = "hero8_snap_" + Guid.NewGuid().ToString("N")[..8],
            Hero = EHero.Hero8,
            UnlockedSlots = 0x3FF,
        };
        run.PlayerAttributes[EPlayerAttributeType.Tempo] = 3;
        var seEntry = new RunState.CardEntry
        {
            InstanceId = "skt_b_1",
            TemplateId = Guid.Parse(NoteSocketIds[1]),
            Type = ECardType.SocketEffect,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_1,
            CoreCard = new CardInstance(fx.Repo.GetCard(NoteSocketIds[1])!) { StartSlot = 1 },
        };
        run.Cards[seEntry.InstanceId] = seEntry;

        string json = RunSnapshotSerializer.Serialize(run);
        var restored = RunSnapshotSerializer.Deserialize(json, fx.Repo);
        Assert.Equal(EHero.Hero8, restored.Hero);
        Assert.Equal(3, restored.PlayerAttributes[EPlayerAttributeType.Tempo]);
        var restoredSe = Assert.Single(restored.Cards.Values, c => c.Type == ECardType.SocketEffect);
        Assert.Equal(Guid.Parse(NoteSocketIds[1]), restoredSe.TemplateId);
        Assert.Equal(EContainerSocketId.Socket_1, restoredSe.Socket);
    }
}
