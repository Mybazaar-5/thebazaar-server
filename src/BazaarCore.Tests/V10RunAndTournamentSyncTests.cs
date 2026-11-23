using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V10 RunEngine / 局外同步 / PvP / 锦标赛公平与多真人同步专项回归测试：
///   0) 跨分支升级接口约定：断点续玩 LevelUp 传目标新等级（Lv1-&gt;Lv2 传 2）
///   1) #4 每小时固定经验 +1 XP 独立发放（不被 1-XP 商店或野怪吞掉，Day 1 Hour 4-5 升至 Lv2）
///   2) #6 &amp; #20 奥秘之书（Book of Secrets）跨天同步与卖出/离开遭遇产技能同步
///   3) #1、#2、#8 Hour 6 稳停 Choice、进战前清空 SelectionSet/PvP 遭遇卡、Opponent Income != 99 及空战利品兜底不卡死
///   4) #11 RunSnapshotSerializer.ToSimCard 与 SyncEntryFromCoreCardAndDiff 精确同步 Burn / BurnReference HiddenTags
///   5) #13、#15、#16、#19 建房默认 8 人、AI 公平继承房间预设、多真人回合同步屏障及超时解锁、Persistence 清理旧脏镜像
/// </summary>
public sealed class V10RunAndTournamentSyncTests
{
    private const string PolShopId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string FangedIngletCombatId = "1ea147f3-5712-4333-b347-8fd65d08f009";
    private const string AldricEventId = "78cf8b25-5812-4b9a-a9ff-9694c86e7a90";
    private const string ExploitAldricStepId = "29bb1d38-882b-48bb-be8a-d69dca75fd3f";
    private const string BookOfSecretsId = "a34b3465-d460-4f53-9223-8a3f4b77b012";
    private const string SkillVoucherId = "7607b1b9-4982-422d-b84a-edd9334c92c3";
    private const string EagleTalismanId = "0022c409-c839-41e8-8022-65a407457dfe";
    private const string CannonId = "c74239b9-563c-4672-b664-9c9f22329f19";
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string StudyDefaultEventId = "ce38f4a2-f156-4082-b8ce-e8956c71fc0d";

    private sealed class Fx
    {
        public string DataDir { get; init; } = "";
        public string DbPath { get; init; } = "";
        public CardPool Pool { get; init; } = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
    }

    private static readonly Lazy<Fx> RealFixture = new(() =>
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++, dir = Directory.GetParent(dir)?.FullName)
        {
            string candidate = Path.Combine(dir, "dist", "data");
            string candidateDb = Path.Combine(candidate, "GameData.db");
            if (File.Exists(candidateDb) && new FileInfo(candidateDb).Length > 1024 * 1024)
            {
                dir = candidate;
                break;
            }
        }
        string fallbackDb = Path.Combine(dir ?? "", "GameData.db");
        if (string.IsNullOrEmpty(dir) || !File.Exists(fallbackDb) || new FileInfo(fallbackDb).Length <= 1024 * 1024)
            dir = @"e:\thebazaar\dist\data";

        string dbPath = Path.Combine(dir, "GameData.db");
        return new Fx
        {
            DataDir = dir,
            DbPath = dbPath,
            Pool = CardPool.Load(dbPath),
            Monsters = MonsterPool.Load(dbPath),
            LevelUps = LevelUpTable.Load(dbPath),
            Mode = GameModeConfig.Load(dbPath),
            Repo = GameDatabaseLoader.Load(dbPath, null),
            Ghosts = PvpGhostPool.Load(dir),
        };
    });

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        CardPool pool,
        List<BazaarServer.Data.MonsterTemplate> monsters,
        LevelUpTable levelUps,
        GameModeConfig mode,
        GameDataRepository repo,
        PvpGhostPool ghosts,
        EHero hero = EHero.Vanessa,
        EPlayMode playMode = EPlayMode.Unranked,
        CustomRunRuleSet? rules = null,
        string? accountId = null,
        Guid? tournamentSessionId = null,
        LobbyStore? lobbies = null,
        SessionStore? sessions = null,
        int seed = 1010)
    {
        var run = new RunState
        {
            RunId = "v10_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = playMode,
            AccountId = accountId,
            TournamentSessionId = tournamentSessionId,
            RngSeed = seed,
            CustomRules = rules ?? new CustomRunRuleSet(),
        };
        var engine = new RunEngine(
            run, pool, monsters, levelUps, mode, repo, ghosts,
            lobbies: lobbies, sessions: sessions);
        engine.StartRun();
        return (run, engine);
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        GameDataRepository repo,
        CardPool pool,
        string templateId,
        EInventorySection section,
        int slot,
        ETier tier = ETier.Bronze,
        string? enchantment = null)
    {
        var coreTpl = repo.GetCard(templateId)!;
        var poolTpl = pool.Get(templateId);
        var coreCard = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), enchantment, "itm_" + Guid.NewGuid().ToString("N")[..8])
        {
            StartSlot = slot,
        };
        if (section == EInventorySection.Stash)
            Assert.True(run.StashGrid.TryPlaceOrShift(coreCard, slot));
        else
            Assert.True(run.HandGrid.TryPlaceOrShift(coreCard, slot));

        EEnchantmentType? enchEnum = Enum.TryParse<EEnchantmentType>(enchantment, true, out var pe) ? pe : null;
        var entry = new RunState.CardEntry
        {
            InstanceId = coreCard.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Enchantment = enchEnum,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)Math.Clamp(coreCard.StartSlot, 0, 9),
            CoreCard = coreCard,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static RunState.CardEntry InjectChoiceEncounter(
        RunState run,
        CardPool pool,
        string encounterId,
        ECardType fallbackType = ECardType.EventEncounter)
    {
        var tpl = pool.Get(encounterId);
        string instId = "inj_" + Guid.NewGuid().ToString("N")[..8];
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(encounterId),
            Type = tpl != null ? ShopEngine.ParseCardType(tpl.Type) : fallbackType,
            Size = tpl != null ? ShopEngine.ParseSize(tpl.Size) : ECardSize.Medium,
            Tier = tpl != null ? ShopEngine.ParseTier(tpl.StartingTier) : ETier.Bronze,
            Owner = null,
            Section = null,
            Socket = null,
        };
        RunState.PopulateMetaFromTemplate(entry, tpl, null);
        run.Cards[instId] = entry;
        run.SelectionSet.Add(instId);
        return entry;
    }

    private static string BuildSkillSpawnEventJson(string eventId, string stepId, string skillId)
    {
        return $$"""
        {
          "$type": "TCardEventEncounter",
          "Id": "{{eventId}}",
          "InternalName": "V10 Exit Skill Event",
          "Type": "EventEncounter",
          "Size": "Medium",
          "StartingTier": "Bronze",
          "Heroes": ["Common"],
          "Tags": [],
          "HiddenTags": [],
          "SelectionContext": {
            "Rules": {
              "CanSelectMultiple": false,
              "SelectionIsFree": true,
              "CanExit": true
            },
            "SpawnContext": {
              "$type": "TSpawnContextQuery",
              "Groups": [{
                "$type": "TSpawnGroup",
                "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{stepId}}"] }],
                "SelectionMethod": "Random"
              }],
              "SelectionMethod": "Sequential",
              "Limit": { "$type": "TFixedValue", "Value": 1 }
            }
          },
          "Abilities": {
            "exit_skill": {
              "Id": "exit_skill",
              "Trigger": { "$type": "TTriggerOnEncounterExited" },
              "ActiveIn": "Anywhere",
              "Action": {
                "$type": "TActionGameSpawnCards",
                "TargetPlayer": { "$type": "TTargetPlayerAbsolute", "TargetMode": "Player" },
                "SpawnContext": {
                  "$type": "TSpawnContextQuery",
                  "Groups": [{
                    "$type": "TSpawnGroup",
                    "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{skillId}}"] }],
                    "SelectionMethod": "Random"
                  }],
                  "SelectionMethod": "Sequential",
                  "Limit": { "$type": "TFixedValue", "Value": 1 },
                  "Behaviors": [{ "$type": "TSpawnBehaviorIgnoreSpaceConstraint" }]
                }
              }
            }
          }
        }
        """;
    }

    private static string BuildSellableStepJson(string stepId)
    {
        return $$"""
        {
          "$type": "TCardEncounterStep",
          "Id": "{{stepId}}",
          "InternalName": "V10 Free Step",
          "Type": "EncounterStep",
          "Size": "Medium",
          "StartingTier": "Bronze",
          "Heroes": ["Common"],
          "Tags": [],
          "HiddenTags": [],
          "Abilities": {
            "0": {
              "Id": "0",
              "Trigger": { "$type": "TTriggerOnCardSelected" },
              "ActiveIn": "Anywhere",
              "Action": {
                "$type": "TActionPlayerModifyAttribute",
                "AttributeType": "Gold",
                "Operation": "Add",
                "Value": { "$type": "TFixedValue", "Value": 1 },
                "Target": { "$type": "TTargetPlayerAbsolute", "TargetMode": "Player" }
              }
            }
          }
        }
        """;
    }

    private static string BuildWeaponJson(string id, string name, int dmg)
    {
        return $$"""
        {
          "$type": "TCardItem",
          "Id": "{{id}}",
          "InternalName": "{{name}}",
          "Type": "Item",
          "Size": "Small",
          "StartingTier": "Bronze",
          "Heroes": ["Vanessa"],
          "Tags": ["Weapon"],
          "HiddenTags": ["Damage"],
          "Tiers": {
            "Bronze": { "Attributes": { "CooldownMax": 2000, "DamageAmount": {{dmg}}, "BuyPrice": 2, "SellPrice": 1 }, "AbilityIds": ["0"], "AuraIds": [] },
            "Silver": { "Attributes": { "CooldownMax": 2000, "DamageAmount": {{dmg * 2}}, "BuyPrice": 4, "SellPrice": 2 }, "AbilityIds": ["0"], "AuraIds": [] },
            "Gold": { "Attributes": { "CooldownMax": 2000, "DamageAmount": {{dmg * 3}}, "BuyPrice": 8, "SellPrice": 4 }, "AbilityIds": ["0"], "AuraIds": [] },
            "Diamond": { "Attributes": { "CooldownMax": 2000, "DamageAmount": {{dmg * 4}}, "BuyPrice": 16, "SellPrice": 8 }, "AbilityIds": ["0"], "AuraIds": [] }
          },
          "Abilities": {
            "0": {
              "Id": "0",
              "Trigger": { "$type": "TTriggerOnCardFired" },
              "ActiveIn": "HandOnly",
              "Action": {
                "$type": "TActionPlayerDamage",
                "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" },
                "Value": { "$type": "TReferenceValueCardAttribute", "AttributeType": "DamageAmount", "Target": { "$type": "TTargetCardSelf" } }
              }
            }
          }
        }
        """;
    }

    private sealed class OverlayContext : IDisposable
    {
        private readonly string _dir;
        public CardPool Pool { get; }
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; }
        public LevelUpTable LevelUps { get; }
        public GameModeConfig Mode { get; }
        public GameDataRepository Repo { get; }
        public PvpGhostPool Ghosts { get; }

        public OverlayContext(IEnumerable<(string Id, string Json)> cards)
        {
            var fx = RealFixture.Value;
            _dir = Path.Combine(Path.GetTempPath(), "bz_v10_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_dir);
            string dbCopy = Path.Combine(_dir, "GameData.db");
            File.Copy(fx.DbPath, dbCopy, overwrite: true);

            using (var conn = new SqliteConnection($"Data Source={dbCopy}"))
            {
                conn.Open();
                foreach (var (id, json) in cards)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT OR REPLACE INTO cards (Id, Data) VALUES ($id, $data);";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.Parameters.AddWithValue("$data", System.Text.Encoding.UTF8.GetBytes(json));
                    cmd.ExecuteNonQuery();
                }
            }

            Pool = CardPool.Load(dbCopy);
            Monsters = fx.Monsters;
            LevelUps = fx.LevelUps;
            Mode = fx.Mode;
            Repo = GameDatabaseLoader.Load(dbCopy, null);
            Ghosts = fx.Ghosts;
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    // ── 0. 跨分支接口约定：断点续玩 LevelUp 空货架自愈必须传目标新等级 2（绝不再减成 1） ──

    [Fact]
    public void CrossBranchContract_LevelUpResumeWithEmptyShelf_ResolvesTargetNewLevel2_Not1()
    {
        var fx = RealFixture.Value;
        var run = new RunState
        {
            RunId = "v10_lvl_resume_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 1,
            Hour = 5,
            State = ERunState.LevelUp,
            PendingLevelUps = 1, // 刚刚升到 Lv2（Level=2, PendingLevelUps=1），SelectionSet 意外为空
        };
        run.PlayerAttributes[EPlayerAttributeType.Level] = 2;
        run.PlayerAttributes[EPlayerAttributeType.Experience] = 0;

        string snap = RunSnapshotSerializer.Serialize(run, startSelectionDone: true, pendingLevelUp: true);
        Assert.True(RunSnapshotSerializer.TryDeserialize(snap, fx.Repo, out var restored, out _));
        Assert.Empty(restored!.SelectionSet);

        // 构造 RunEngine 触发断点自愈重发：不应报错且应按目标新等级 2 重发升级选项
        var engine = new RunEngine(restored, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        Assert.Equal(ERunState.LevelUp, restored.State);
        Assert.NotEmpty(restored.SelectionSet);
        Assert.All(restored.SelectionSet, id => Assert.True(restored.Cards.ContainsKey(id)));
    }

    // ── 1. #4 每小时固定经验 +1 XP 不再被 1-XP 商店/野怪吞掉，Day 1 Hour 4-5 稳步升至 Lv2 ──

    [Fact]
    public void Issue04_AdvanceHourOrDay_GrantsPerHourXpIndependently_ReachesLv2ByHour5_WithoutDoubleEventXp()
    {
        var fx = RealFixture.Value;
        using var ov = new OverlayContext(new[]
        {
            ("11111111-a001-4001-8001-000000000001", BuildWeaponJson("11111111-a001-4001-8001-000000000001", "V10 Test Blade", 80)),
        });
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 404);
        AddOwnedItem(run, ov.Repo, ov.Pool, "11111111-a001-4001-8001-000000000001", EInventorySection.Hand, slot: 3);

        // P0-1 官方单份经验规则：每小时仅获得遭遇经验或固定小时经验（+1 XP）其中一份，绝不叠加双份
        // Hour 1：进出 Pol（1 XP 商店）→ 推进至 Hour 2 = 累计 1 XP
        var pol1 = InjectChoiceEncounter(run, ov.Pool, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol1.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(2u, run.Hour);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(4, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // Hour 2：进出 Pol（1 XP）→ 推进至 Hour 3 = 累计 5 XP
        var pol2 = InjectChoiceEncounter(run, ov.Pool, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol2.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(3u, run.Hour);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(5, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // Hour 3：击败 Lv1 野怪 Fanged Inglet（RewardCombatXp = 3）→ 5+3=8 升至 Lv2，推进至 Hour 4（战斗小时不再额外叠 +1 XP）
        var mobOpt = InjectChoiceEncounter(run, ov.Pool, FangedIngletCombatId, ECardType.CombatEncounter);
        var pveOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) });
        Assert.Null(pveOut.Error);
        Assert.Equal(ERunState.Loot, run.State);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        while (run.State is ERunState.LevelUp or ERunState.Encounter)
        {
            if (run.State == ERunState.Encounter && run.SelectionRules.CanExit)
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            else if (run.SelectionSet.Count > 0)
            {
                var c = run.Cards[run.SelectionSet[0]];
                INetCommand cmd = c.Type is ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter
                    ? new SelectEncounterCommand { InstanceId = new InstanceId(c.InstanceId) }
                    : c.Type == ECardType.Skill
                        ? new SelectSkillCommand { InstanceId = new InstanceId(c.InstanceId) }
                        : new SelectItemCommand { InstanceId = new InstanceId(c.InstanceId) };
                Assert.Null(engine.Handle(cmd).Error);
            }
            else break;
        }
        Assert.Equal(4u, run.Hour);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // Hour 4 & Hour 5 各进出一次商店（各 +1 XP）→ Hour 6 PvP 前达 Lv2 2/8 XP；跨日 +1 XP → Lv2 3/8 XP
        var pol4 = InjectChoiceEncounter(run, ov.Pool, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol4.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(5u, run.Hour);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        var pol5 = InjectChoiceEncounter(run, ov.Pool, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol5.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(6u, run.Hour);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        var pvpOpt = run.Cards[Assert.Single(run.SelectionSet)];
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpOpt.InstanceId) }).Error);

        while (run.State is ERunState.LevelUp or ERunState.Encounter)
        {
            if (run.State == ERunState.Encounter && run.SelectionRules.CanExit)
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            else if (run.SelectionSet.Count > 0)
            {
                var c = run.Cards[run.SelectionSet[0]];
                INetCommand cmd = c.Type is ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter
                    ? new SelectEncounterCommand { InstanceId = new InstanceId(c.InstanceId) }
                    : c.Type == ECardType.Skill
                        ? new SelectSkillCommand { InstanceId = new InstanceId(c.InstanceId) }
                        : new SelectItemCommand { InstanceId = new InstanceId(c.InstanceId) };
                Assert.Null(engine.Handle(cmd).Error);
            }
            else break;
        }

        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(2u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(3, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // 验证事件自身不双算（P0-1）：Aldric(1 XP) + Exploit(1 XP) 同小时严格去重，且不再额外叠加小时推进经验 = 1 XP
        var (aldricRun, aldricEngine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 405);
        var aldricOpt = InjectChoiceEncounter(aldricRun, ov.Pool, AldricEventId);
        Assert.Null(aldricEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(aldricOpt.InstanceId) }).Error);
        var exploit = aldricRun.SelectionSet.Select(id => aldricRun.Cards[id])
            .FirstOrDefault(c => c.TemplateId == Guid.Parse(ExploitAldricStepId));
        if (exploit != null)
        {
            Assert.Null(aldricEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(exploit.InstanceId) }).Error);
            while (aldricRun.State == ERunState.Encounter)
            {
                if (aldricRun.SelectionRules.CanExit)
                    Assert.Null(aldricEngine.Handle(new ExitCurrentStateCommand()).Error);
                else if (aldricRun.SelectionSet.Count > 0)
                    Assert.Null(aldricEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(aldricRun.SelectionSet[0]) }).Error);
                else break;
            }
            Assert.Equal(2u, aldricRun.Hour);
            Assert.Equal(4, aldricRun.PlayerAttributes[EPlayerAttributeType.Experience]);
        }

        // GainedAdvanceHourXpThisHour 快照往返不丢失
        aldricRun.GainedAdvanceHourXpThisHour = true;
        string snap = RunSnapshotSerializer.Serialize(aldricRun);
        Assert.True(RunSnapshotSerializer.TryDeserialize(snap, ov.Repo, out var restored, out _));
        Assert.True(restored!.GainedAdvanceHourXpThisHour);
    }

    // ── 2. #6 & #20：Book of Secrets 跨天技能同步 + 卖出/离开遭遇产技能同步 ──

    [Fact]
    public void Issue06And20_BookOfSecretsCrossDay_And_SellAndEncounterExitSkillSync()
    {
        var fx = RealFixture.Value;
        var anySkill = fx.Pool.Skills("Vanessa").First();
        const string exitEvId = "22222222-b001-4001-8001-000000000001";
        const string exitStepId = "22222222-b001-4001-8001-000000000002";

        using var ov = new OverlayContext(new[]
        {
            (exitStepId, BuildSellableStepJson(exitStepId)),
            (exitEvId, BuildSkillSpawnEventJson(exitEvId, exitStepId, anySkill.Id)),
        });

        // 2a. Book of Secrets (Silver + Radiant，满足基线 HasEnchantment 前置且触发通用 Skill 抽取) 跨天 OnDayStarted：
        //     必须在 RunState.Cards 生成 Skill CardEntry，并发射 GameSimEventCardSpawned + GameSimEventPlayerSkillEquipped
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 620);
        var bookEntry = AddOwnedItem(run, ov.Repo, ov.Pool, BookOfSecretsId, EInventorySection.Hand, slot: 3, ETier.Silver, enchantment: "Radiant");
        Assert.NotNull(bookEntry);

        // 推进到 Day 1 Hour 6 并打完 Day 1 PvP，随后跨入 Day 2 Hour 1 触发 OnDayStarted
        Assert.Null(engine.Handle(new CheatCommand { Args = ["hour", "6"] }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = ["deal"] }).Error);
        var pvpCard = run.Cards[Assert.Single(run.SelectionSet)];
        var pvpOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCard.InstanceId) });
        Assert.Null(pvpOut.Error);

        var allEvents = new List<IGameSimEvent>();
        if (pvpOut.CombatDespawnSim?.Events != null) allEvents.AddRange(pvpOut.CombatDespawnSim.Events);
        allEvents.AddRange(pvpOut.Events);

        // 若打完 PvP 恰好停在 LevelUp 货架之后，选完后触发跨天并收集该步事件流
        while (run.Day == 1 && run.State == ERunState.LevelUp && run.SelectionSet.Count > 0)
        {
            var pick = run.Cards[run.SelectionSet[0]];
            INetCommand cmd = pick.Type == ECardType.Skill
                ? new SelectSkillCommand { InstanceId = new InstanceId(pick.InstanceId) }
                : pick.Type is ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter
                    ? new SelectEncounterCommand { InstanceId = new InstanceId(pick.InstanceId) }
                    : new SelectItemCommand { InstanceId = new InstanceId(pick.InstanceId) };
            var lvOut = engine.Handle(cmd);
            Assert.Null(lvOut.Error);
            allEvents.AddRange(lvOut.Events);
        }

        Assert.Equal(2u, run.Day);
        var spawnedSkills = run.Cards.Values.Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill).ToList();
        Assert.NotEmpty(spawnedSkills);
        Assert.Contains(allEvents, e => e is GameSimEventCardSpawned sp && sp.Type == ECardType.Skill && sp.CombatantId == ECombatantId.Player);
        Assert.Contains(allEvents, e => e is GameSimEventPlayerSkillEquipped eq && eq.Owner == ECombatantId.Player);

        // 跨天产出的技能在 RunState 中已就位且快照序列化可完整恢复
        string snap = RunSnapshotSerializer.Serialize(run, engine.StartSelectionDone, run.PendingLevelUp);
        Assert.True(RunSnapshotSerializer.TryDeserialize(snap, ov.Repo, out var resumed, out _));
        Assert.Contains(resumed!.Cards.Values, c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill);

        // 直接验证非 PvP 路径退出可退出商店触发 AdvanceHourOrDay 跨天：当回合事件流必定包含 GameSimEventCardSpawned + GameSimEventPlayerSkillEquipped
        var (runDirectDay, engDirectDay) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 621);
        AddOwnedItem(runDirectDay, ov.Repo, ov.Pool, BookOfSecretsId, EInventorySection.Hand, slot: 3, ETier.Silver, enchantment: "Radiant");
        runDirectDay.Victories = 1; // 满足每日 PvP 已结算兜底守卫，使退店后可直接从 Hour 6 跨入 Day 2
        Assert.Null(engDirectDay.Handle(new CheatCommand { Args = ["hour", "6"] }).Error);
        var shopChoice = InjectChoiceEncounter(runDirectDay, ov.Pool, PolShopId);
        Assert.Null(engDirectDay.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopChoice.InstanceId) }).Error);
        var crossDayOut = engDirectDay.Handle(new ExitCurrentStateCommand());
        Assert.Null(crossDayOut.Error);
        Assert.Equal(2u, runDirectDay.Day);
        Assert.Contains(crossDayOut.Events, e => e is GameSimEventCardSpawned sp && sp.Type == ECardType.Skill && sp.CombatantId == ECombatantId.Player);
        Assert.Contains(crossDayOut.Events, e => e is GameSimEventPlayerSkillEquipped eq && eq.Owner == ECombatantId.Player);

        // 2a-plain. 无附魔（Plain）Book of Secrets 跨天 OnDayStarted：验证四路合流后（ValueAndConditionEvaluator 修复 Enchantment==null + RunEngine 跨天同步）直接生成并装备新技能
        var (runPlainBook, engPlainBook) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 624);
        var plainBook = AddOwnedItem(runPlainBook, ov.Repo, ov.Pool, BookOfSecretsId, EInventorySection.Hand, slot: 3, ETier.Silver, enchantment: null);
        Assert.Null(plainBook.Enchantment);
        runPlainBook.Victories = 1;
        Assert.Null(engPlainBook.Handle(new CheatCommand { Args = ["hour", "6"] }).Error);
        var plainShop = InjectChoiceEncounter(runPlainBook, ov.Pool, PolShopId);
        Assert.Null(engPlainBook.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(plainShop.InstanceId) }).Error);
        var plainCrossDayOut = engPlainBook.Handle(new ExitCurrentStateCommand());
        Assert.Null(plainCrossDayOut.Error);
        Assert.Equal(2u, runPlainBook.Day);
        var plainBookSkill = Assert.Single(runPlainBook.Cards.Values, c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill);
        Assert.Contains(plainCrossDayOut.Events, e => e is GameSimEventCardSpawned sp && sp.InstanceId == plainBookSkill.InstanceId && sp.Type == ECardType.Skill && sp.CombatantId == ECombatantId.Player);
        Assert.Contains(plainCrossDayOut.Events, e => e is GameSimEventPlayerSkillEquipped eq && eq.InstanceId == plainBookSkill.InstanceId && eq.Owner == ECombatantId.Player);

        // 2b. 卖出 Skill Voucher (Gold + Radiant 与 Plain 无附魔青铜两路验证)：OnSell 立即同步新技能与双重事件
        var (runSell, engSell) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 622);
        var voucher = AddOwnedItem(runSell, ov.Repo, ov.Pool, SkillVoucherId, EInventorySection.Hand, slot: 3, ETier.Gold, enchantment: "Radiant");
        var sellOut = engSell.Handle(new SellCardCommand { InstanceId = new InstanceId(voucher.InstanceId) });
        Assert.Null(sellOut.Error);
        Assert.Contains(runSell.Cards.Values, c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill);
        Assert.Contains(sellOut.Events, e => e is GameSimEventCardSpawned sp && sp.Type == ECardType.Skill && sp.CombatantId == ECombatantId.Player);
        Assert.Contains(sellOut.Events, e => e is GameSimEventPlayerSkillEquipped);

        var (runPlainSell, engPlainSell) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 625);
        var plainVoucher = AddOwnedItem(runPlainSell, ov.Repo, ov.Pool, SkillVoucherId, EInventorySection.Hand, slot: 3, ETier.Bronze, enchantment: null);
        Assert.Null(plainVoucher.Enchantment);
        var plainSellOut = engPlainSell.Handle(new SellCardCommand { InstanceId = new InstanceId(plainVoucher.InstanceId) });
        Assert.Null(plainSellOut.Error);
        var plainSoldSkill = Assert.Single(runPlainSell.Cards.Values, c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill);
        Assert.Contains(plainSellOut.Events, e => e is GameSimEventCardSpawned sp && sp.InstanceId == plainSoldSkill.InstanceId && sp.Type == ECardType.Skill && sp.CombatantId == ECombatantId.Player);
        Assert.Contains(plainSellOut.Events, e => e is GameSimEventPlayerSkillEquipped eq && eq.InstanceId == plainSoldSkill.InstanceId && eq.Owner == ECombatantId.Player);

        // 2c. TriggerOnEncounterExited 产技能同步
        var (runExit, engExit) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 623);
        var injEv = InjectChoiceEncounter(runExit, ov.Pool, exitEvId);
        Assert.Null(engExit.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(injEv.InstanceId) }).Error);
        var exitEvOut = engExit.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitEvOut.Error);
        Assert.Contains(runExit.Cards.Values, c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill && c.TemplateId == Guid.Parse(anySkill.Id));
        Assert.Contains(exitEvOut.Events, e => e is GameSimEventCardSpawned sp && sp.Type == ECardType.Skill && sp.CombatantId == ECombatantId.Player);
        Assert.Contains(exitEvOut.Events, e => e is GameSimEventPlayerSkillEquipped);
    }

    // ── 3. #1、#2、#8：Hour 6 稳停 Choice、进战清 SelectionSet/PvP 卡、Opponent Income != 99、野怪战后不卡死 ──

    [Fact]
    public void Issue01_02_08_Hour6StaysInChoice_ClearsSelectionSetAndPvpPortraitBeforeCombat_OpponentIncomeValid_And_MonsterLootNeverFreezes()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, seed: 808);
        AddOwnedItem(run, fx.Repo, fx.Pool, CutlassId, EInventorySection.Hand, slot: 3, ETier.Gold);

        // 推进 Hour 5 -> Hour 6（绝不能擅自直接开打！必须稳停 Choice 并预锁对手 + 写入 Income != 99）
        Assert.Null(engine.Handle(new CheatCommand { Args = ["hour", "5"] }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = ["deal"] }).Error);
        var h5Card = run.Cards[run.SelectionSet[0]];
        var enterH5 = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(h5Card.InstanceId) });
        Assert.Null(enterH5.Error);
        if (run.State != ERunState.Choice)
        {
            while (run.State is ERunState.Encounter or ERunState.Pedestal or ERunState.LevelUp)
            {
                if (run.SelectionRules.CanExit)
                    Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                else if (run.SelectionSet.Count > 0)
                {
                    var pick = run.Cards[run.SelectionSet[0]];
                    INetCommand cmd = pick.Type is ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter
                        ? new SelectEncounterCommand { InstanceId = new InstanceId(pick.InstanceId) }
                        : pick.Type == ECardType.Skill
                            ? new SelectSkillCommand { InstanceId = new InstanceId(pick.InstanceId) }
                            : new SelectItemCommand { InstanceId = new InstanceId(pick.InstanceId) };
                    Assert.Null(engine.Handle(cmd).Error);
                }
                else break;
            }
        }

        Assert.Equal(6u, run.Hour);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.NotNull(run.PendingPvpGhost);
        Assert.NotNull(run.CurrentPvpOpponent);
        Assert.Equal(0, run.OpponentAttributes[EPlayerAttributeType.Gold]);
        Assert.NotEqual(99, run.OpponentAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(5, run.OpponentAttributes[EPlayerAttributeType.Income]);

        // 故意往 SelectionSet 混入额外垃圾遭遇卡以验证进战前全数清理并发出 CardDisposed
        var strayChoice = InjectChoiceEncounter(run, fx.Pool, PolShopId);
        var pvpCard = run.SelectionSet.Select(id => run.Cards[id]).First(c => c.Type == ECardType.PvpEncounter);

        var pvpOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCard.InstanceId) });
        Assert.Null(pvpOut.Error);
        Assert.NotNull(pvpOut.CombatSpawnSim);
        Assert.NotNull(pvpOut.CombatDespawnSim);

        var spawnSim = pvpOut.CombatSpawnSim!;
        Assert.Equal(ERunState.PVPCombat, spawnSim.CurrentState!.StateName);
        Assert.Equal(0, spawnSim.Opponent!.Attributes[EPlayerAttributeType.Gold]);
        Assert.NotEqual(99, spawnSim.Opponent.Attributes[EPlayerAttributeType.Income]);
        Assert.Equal(5, spawnSim.Opponent.Attributes[EPlayerAttributeType.Income]);

        // PvP 挑战卡与多余遭遇卡在 CombatSpawnSim 前已彻底 Dispose，绝不出现在地毯或 oppEntries 中
        Assert.Contains(spawnSim.Events.OfType<GameSimEventCardDisposed>(), d => d.InstanceId == pvpCard.InstanceId);
        Assert.Contains(spawnSim.Events.OfType<GameSimEventCardDisposed>(), d => d.InstanceId == strayChoice.InstanceId);
        Assert.False(spawnSim.Cards.ContainsKey(pvpCard.InstanceId));
        Assert.False(spawnSim.Cards.ContainsKey(strayChoice.InstanceId));
        Assert.Empty(spawnSim.CurrentState.SelectionSet);
        Assert.DoesNotContain(spawnSim.Cards.Values, c => c.Placement?.Owner == ECombatantId.Opponent && c.Placement?.Socket == EContainerSocketId.Socket_5 && c.Tags == null && c.Size == ECardSize.Medium && c.InstanceId == pvpCard.InstanceId);

        // #1 野怪战斗：无论赢（含 MonsterLootChooseOne=false/true 与取空战利品）还是输，均不卡死且顺畅推进
        // 1a. MonsterLootChooseOne=false 赢下战斗 -> 拿完所有免费战利品自动退出 Loot 推进 Hour 4
        var (pveWinRun, pveWinEng) = CreateStartedEngine(
            fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts,
            rules: new CustomRunRuleSet { MonsterLootChooseOne = false }, seed: 809);
        AddOwnedItem(pveWinRun, fx.Repo, fx.Pool, CutlassId, EInventorySection.Hand, slot: 3, ETier.Diamond);
        Assert.Null(pveWinEng.Handle(new CheatCommand { Args = ["hour", "3"] }).Error);
        Assert.Null(pveWinEng.Handle(new CheatCommand { Args = ["deal"] }).Error);
        var mob1 = pveWinRun.Cards[pveWinRun.SelectionSet[0]];
        var mobWinOut = pveWinEng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mob1.InstanceId) });
        Assert.Null(mobWinOut.Error);
        Assert.Equal(ERunState.Loot, pveWinRun.State);
        Assert.True(pveWinRun.SelectionRules.CanExit);
        while (pveWinRun.State == ERunState.Loot && pveWinRun.SelectionSet.Count > 0)
        {
            var lootPick = pveWinRun.Cards[pveWinRun.SelectionSet[0]];
            INetCommand lootCmd = lootPick.Type == ECardType.Skill
                ? new SelectSkillCommand { InstanceId = new InstanceId(lootPick.InstanceId) }
                : new SelectItemCommand { InstanceId = new InstanceId(lootPick.InstanceId) };
            var pickRes = pveWinEng.Handle(lootCmd);
            if (pickRes.Error != null)
            {
                Assert.Null(pveWinEng.Handle(new ExitCurrentStateCommand()).Error);
                break;
            }
        }
        if (pveWinRun.State == ERunState.Loot)
            Assert.Null(pveWinEng.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(4u, pveWinRun.Hour);

        // 1b. MonsterLootChooseOne=true 分页任选 1 件战利品 -> 选中即自动离开 Loot 并推进 Hour 4
        var (pveChooseRun, pveChooseEng) = CreateStartedEngine(
            fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts,
            rules: new CustomRunRuleSet { MonsterLootChooseOne = true }, seed: 811);
        AddOwnedItem(pveChooseRun, fx.Repo, fx.Pool, CutlassId, EInventorySection.Hand, slot: 3, ETier.Diamond);
        Assert.Null(pveChooseEng.Handle(new CheatCommand { Args = ["hour", "3"] }).Error);
        Assert.Null(pveChooseEng.Handle(new CheatCommand { Args = ["deal"] }).Error);
        var mobChoose = pveChooseRun.Cards[pveChooseRun.SelectionSet[0]];
        var mobChooseOut = pveChooseEng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobChoose.InstanceId) });
        Assert.Null(mobChooseOut.Error);
        Assert.Equal(ERunState.Loot, pveChooseRun.State);
        Assert.True(pveChooseRun.SelectionRules.CanExit);
        Assert.NotEmpty(pveChooseRun.SelectionSet);
        var chooseFirst = pveChooseRun.Cards[pveChooseRun.SelectionSet[0]];
        INetCommand chooseCmd = chooseFirst.Type == ECardType.Skill
            ? new SelectSkillCommand { InstanceId = new InstanceId(chooseFirst.InstanceId) }
            : new SelectItemCommand { InstanceId = new InstanceId(chooseFirst.InstanceId) };
        Assert.Null(pveChooseEng.Handle(chooseCmd).Error);
        Assert.NotEqual(ERunState.Loot, pveChooseRun.State);
        Assert.Equal(4u, pveChooseRun.Hour);

        // 1c. 空战利品货架兜底：即使处于 Loot 且 SelectionSet 为空，ExitCurrentStateCommand 仍顺畅离开 Loot 推进至 Hour 4
        var (pveEmptyLootRun, pveEmptyLootEng) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, seed: 812);
        AddOwnedItem(pveEmptyLootRun, fx.Repo, fx.Pool, CutlassId, EInventorySection.Hand, slot: 3, ETier.Diamond);
        Assert.Null(pveEmptyLootEng.Handle(new CheatCommand { Args = ["hour", "3"] }).Error);
        Assert.Null(pveEmptyLootEng.Handle(new CheatCommand { Args = ["deal"] }).Error);
        var mobEmpty = pveEmptyLootRun.Cards[pveEmptyLootRun.SelectionSet[0]];
        Assert.Null(pveEmptyLootEng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobEmpty.InstanceId) }).Error);
        Assert.Equal(ERunState.Loot, pveEmptyLootRun.State);
        pveEmptyLootRun.SelectionSet.Clear();
        pveEmptyLootRun.MonsterLootPages.Clear();
        Assert.Null(pveEmptyLootEng.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(4u, pveEmptyLootRun.Hour);
        Assert.Equal(ERunState.Choice, pveEmptyLootRun.State);

        // 1d. 真战败（卸空玩家装备并压低生命，确保输给 Day 1 野怪）：不进 Loot、直接推进至 Hour 4 Choice，绝不卡死
        var (pveLoseRun, pveLoseEng) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, seed: 810);
        pveLoseRun.HandGrid.Cards.Clear();
        pveLoseRun.StashGrid.Cards.Clear();
        foreach (string ownedId in pveLoseRun.Cards.Where(kv => kv.Value.Owner == ECombatantId.Player).Select(kv => kv.Key).ToList())
            pveLoseRun.Cards.Remove(ownedId);
        pveLoseRun.PlayerAttributes[EPlayerAttributeType.HealthMax] = 20;
        Assert.Null(pveLoseEng.Handle(new CheatCommand { Args = ["hour", "3"] }).Error);
        Assert.Null(pveLoseEng.Handle(new CheatCommand { Args = ["deal"] }).Error);
        var mob2 = pveLoseRun.Cards[pveLoseRun.SelectionSet[0]];
        var mobLoseOut = pveLoseEng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mob2.InstanceId) });
        Assert.Null(mobLoseOut.Error);
        Assert.Equal(4u, pveLoseRun.Hour);
        Assert.Equal(ERunState.Choice, pveLoseRun.State);
    }

    // ── 4. #11 附魔动态 HiddenTags（区分 Burn 与 BurnReference）随 ToSimCard / SyncEntry 下发 ──

    [Fact]
    public void Issue11_DynamicHiddenTags_DistinguishesBurnAndBurnReference_InToSimCardAndSyncDiff()
    {
        var fx = RealFixture.Value;
        var (run, _) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, seed: 1111);

        var talisman = AddOwnedItem(run, fx.Repo, fx.Pool, EagleTalismanId, EInventorySection.Hand, slot: 3, ETier.Diamond, enchantment: null);
        var cannon = AddOwnedItem(run, fx.Repo, fx.Pool, CannonId, EInventorySection.Hand, slot: 4, ETier.Bronze, enchantment: null);

        // 给两件物品分别附上 Fiery 附魔，调用 SyncEntryFromCoreCardAndDiff 后再调用 ToSimCard
        talisman.CoreCard!.ApplyEnchantment("Fiery", preventOverride: false, isTemporary: false);
        cannon.CoreCard!.ApplyEnchantment("Fiery", preventOverride: false, isTemporary: false);

        run.SyncEntryFromCoreCardAndDiff(talisman, fx.Repo.GameMode.StandardPrices);
        run.SyncEntryFromCoreCardAndDiff(cannon, fx.Repo.GameMode.StandardPrices);

        Assert.Contains(EHiddenTag.BurnReference, talisman.HiddenTags);
        Assert.DoesNotContain(EHiddenTag.Burn, talisman.HiddenTags);
        Assert.Contains(EHiddenTag.Burn, cannon.HiddenTags);

        var simTalisman = RunSnapshotSerializer.ToSimCard(talisman);
        var simCannon = RunSnapshotSerializer.ToSimCard(cannon);

        Assert.NotNull(simTalisman.HiddenTags);
        Assert.Contains(EHiddenTag.BurnReference, simTalisman.HiddenTags!);
        Assert.DoesNotContain(EHiddenTag.Burn, simTalisman.HiddenTags!);

        Assert.NotNull(simCannon.HiddenTags);
        Assert.Contains(EHiddenTag.Burn, simCannon.HiddenTags!);

        var fullSim = run.BuildFullGameSim();
        Assert.Contains(EHiddenTag.BurnReference, fullSim.Cards[talisman.InstanceId].HiddenTags!);
        Assert.DoesNotContain(EHiddenTag.Burn, fullSim.Cards[talisman.InstanceId].HiddenTags!);
        Assert.Contains(EHiddenTag.Burn, fullSim.Cards[cannon.InstanceId].HiddenTags!);
    }

    // ── 5. #13、#15、#16、#19：建房默认 8 人、AI 公平继承预设、双真人同步屏障与超时解锁、脏镜像清理 ──

    [Fact]
    public async System.Threading.Tasks.Task Issue13_15_16_19_TournamentCap8_AiFairPresets_RoundBarrier_And_DirtyGhostCleanup()
    {
        var fx = RealFixture.Value;
        string dir = Path.Combine(Path.GetTempPath(), $"v10_tourney_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "bazaar.db");

        // #19：向 SQLite 写入 1 条正常 Day 1 镜像 + 1 条 Day 1 610HP 脏镜像 + 1 条 Day 4 3065HP 脏镜像 + 1 条账号数据
        var db = new Persistence(dbPath);
        db.SaveAccount(new Persistence.AccountRow(
            Guid.NewGuid(), "KeepAccount", "k1", 1, 1, 0, 1, DateTime.UtcNow, DateTime.UtcNow));

        using (var rawConn = new SqliteConnection($"Data Source={dbPath}"))
        {
            rawConn.Open();
            void InsertRawGhost(int day, int hpMax, string boardId, bool isCustom = false)
            {
                using var c = rawConn.CreateCommand();
                c.CommandText = """
                    INSERT INTO pvp_ghosts (day, hero, wins, losses, health_max, prestige, level, unlocked_slots, board_json, created_at, is_custom_rule)
                    VALUES ($d, 'Vanessa', 1, 0, $hm, 20, 1, 120, $bj, $ca, $icr);
                    """;
                c.Parameters.AddWithValue("$d", day);
                c.Parameters.AddWithValue("$hm", hpMax);
                c.Parameters.AddWithValue("$bj", JsonSerializer.Serialize(new
                {
                    BoardId = boardId,
                    PowerBucket = "Normal",
                    EstimatedWinRate = 0.5,
                    IsCustomRule = isCustom,
                    Items = new[] { new { Slot = 3, Width = 2, TemplateId = CutlassId, Tier = "Bronze" } },
                    Skills = Array.Empty<object>(),
                }));
                c.Parameters.AddWithValue("$ca", DateTime.UtcNow.ToString("O"));
                c.Parameters.AddWithValue("$icr", isCustom ? 1 : 0);
                c.ExecuteNonQuery();
            }

            InsertRawGhost(1, 250, "good_d1");
            InsertRawGhost(1, 610, "dirty_d1_610");
            InsertRawGhost(4, 3065, "dirty_d4_3065");
            InsertRawGhost(4, 1500, "good_d4");
            InsertRawGhost(4, 5000, "custom_d4_5000", isCustom: true);
        }

        // LoadPvpGhosts 过滤掉 610HP / 3065HP 脏镜像，PurgeCorruptedPvpGhosts 清理掉 2 条脏镜像但绝不碰 accounts
        var loadedStd = db.LoadPvpGhosts(includeCustomRule: false);
        Assert.Equal(2, loadedStd.Count);
        Assert.All(loadedStd, g => Assert.True(Persistence.IsSaneGhostForDay(g.Day, g.HealthMax)));

        int purged = db.PurgeCorruptedPvpGhosts();
        Assert.Equal(2, purged);
        Assert.Equal(1, db.CountAccounts());
        Assert.Equal(3, db.CountPvpGhosts()); // good_d1 + good_d4 + custom_d4_5000
        Assert.Equal(0, db.InsertPvpGhost(new Persistence.PvpGhostRow(0, 4, "Vanessa", 2, 1, 3065, 20, 4, 1023, "{}", DateTime.UtcNow), isCustomRule: false));

        // 搭建 HTTP 测试服务以验证 #13、#15、#16
        var nowUtc = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var lobbies = new LobbyStore();
        lobbies.SetClockForTesting(() => nowUtc);
        var sessions = new SessionStore();
        var accounts = new AccountStore(db);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(lobbies);

        await using var app = builder.Build();
        TournamentEndpoints.Map(app, lobbies);
        await app.StartAsync();
        string baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        using var hostHttp = new HttpClient { BaseAddress = new Uri(baseUrl) };
        using var guestHttp = new HttpClient { BaseAddress = new Uri(baseUrl) };
        hostHttp.DefaultRequestHeaders.Add("X-Username", "V10Host");
        guestHttp.DefaultRequestHeaders.Add("X-Username", "V10Guest");

        var attachedSids = new List<string>();
        try
        {
            // #13：未传 rules 且原版客户端硬编码发 playerCap: 16 时，建房必须按标准 8 人（非 16 人）
            var vanillaCreateResp = await hostHttp.PostAsJsonAsync("/lobbies", new
            {
                name = "VanillaClientLobby",
                playMode = "Tournament",
                playerCap = 16,
                isPublic = true,
            });
            vanillaCreateResp.EnsureSuccessStatusCode();
            using var vanillaLobbyDoc = JsonDocument.Parse(await vanillaCreateResp.Content.ReadAsStringAsync());
            Assert.Equal(8, vanillaLobbyDoc.RootElement.GetProperty("PlayerCap").GetInt32());
            Assert.Equal(8, vanillaLobbyDoc.RootElement.GetProperty("MaxPlayers").GetInt32());

            // 房主显式传入 rules.playerCap = 12 时，才采用 12 人上限
            var customCapResp = await hostHttp.PostAsJsonAsync("/lobbies", new
            {
                name = "CustomCap12Lobby",
                playMode = "Tournament",
                playerCap = 16,
                isPublic = true,
                rules = new { playerCap = 12 },
            });
            customCapResp.EnsureSuccessStatusCode();
            using var customCapDoc = JsonDocument.Parse(await customCapResp.Content.ReadAsStringAsync());
            Assert.Equal(12, customCapDoc.RootElement.GetProperty("PlayerCap").GetInt32());

            // 回到标准 8 人房测试 #15 + #16（设默认倒计时 120s、加速倒计时 30s、含自定义起始预设）
            var skillPreset = fx.Pool.Skills("Vanessa").First();
            var matchLobbyResp = await hostHttp.PostAsJsonAsync("/lobbies", new
            {
                name = "V10FairTourney",
                playMode = "Tournament",
                playerCap = 16,
                roundCountdownSeconds = 120,
                acceleratedRoundCountdownSeconds = 30,
                rules = new
                {
                    startingGold = 40,
                    startingIncome = 9,
                    startingHealth = 450,
                    startingItems = new[] { new { templateId = CutlassId, tier = "Silver" } },
                    startingSkills = new[] { new { templateId = skillPreset.Id, tier = "Bronze" } },
                },
            });
            matchLobbyResp.EnsureSuccessStatusCode();
            using var matchLobbyDoc = JsonDocument.Parse(await matchLobbyResp.Content.ReadAsStringAsync());
            string lobbyId = matchLobbyDoc.RootElement.GetProperty("LobbyId").GetString()!;
            string code = matchLobbyDoc.RootElement.GetProperty("Code").GetString()!;
            Assert.Equal(8, matchLobbyDoc.RootElement.GetProperty("PlayerCap").GetInt32());

            // #16 单真人 + 7 AI 房：到达 Hour 6 秒进（CanStartPvp = true）
            var soloAiResp = await hostHttp.PostAsJsonAsync("/lobbies", new { name = "SoloAiRoom", playMode = "Tournament", playerCap = 16 });
            soloAiResp.EnsureSuccessStatusCode();
            using var soloAiDoc = JsonDocument.Parse(await soloAiResp.Content.ReadAsStringAsync());
            string soloLobbyId = soloAiDoc.RootElement.GetProperty("LobbyId").GetString()!;
            (await hostHttp.PutAsync($"/lobbies/{soloLobbyId}/countdown", new StringContent("{}"))).EnsureSuccessStatusCode();

            var hostAcc = accounts.GetOrCreateByName("V10Host");
            string hostAid = hostAcc.AccountId.ToString("N");
            var soloLobbyObj = lobbies.Get(soloLobbyId)!;
            var soloSession = sessions.Create(hostAid, hostAcc.Username, new InitializeRunCommand { PlayMode = EPlayMode.Tournament, TournamentSessionId = soloLobbyObj.TournamentSessionId });
            attachedSids.Add(soloSession.Sid);
            var (soloRun, soloEng) = CreateStartedEngine(
                fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts,
                playMode: EPlayMode.Tournament, accountId: hostAid, tournamentSessionId: soloLobbyObj.TournamentSessionId,
                lobbies: lobbies, sessions: sessions, seed: 1601);
            SessionRegistry.Attach(soloSession.Sid, soloRun, soloEng);
            soloEng.Handle(new CheatCommand { Args = ["hour", "6"] });
            soloEng.Handle(new CheatCommand { Args = ["deal"] });
            lobbies.RecordRunProgress(soloLobbyObj, hostAid, soloRun);

            using (var stSolo = JsonDocument.Parse(await hostHttp.GetStringAsync("/tournament/status")))
            {
                Assert.True(stSolo.RootElement.GetProperty("CanStartPvp").GetBoolean());
                Assert.False(stSolo.RootElement.GetProperty("WaitingForOtherPlayers").GetBoolean());
            }

            // 清理单人房再测双真人房（V10Host + V10Guest + 6 AI）
            SessionRegistry.Detach(soloSession.Sid);
            sessions.Remove(soloSession.Sid);

            // 重建双真人测试房（因为上面 SoloAiRoom 让 host 离开了 matchLobby）
            var twoHumanResp = await hostHttp.PostAsJsonAsync("/lobbies", new
            {
                name = "TwoHumanBarrierRoom",
                playMode = "Tournament",
                playerCap = 16,
                roundCountdownSeconds = 120,
                acceleratedRoundCountdownSeconds = 30,
                rules = new
                {
                    startingGold = 40,
                    startingIncome = 9,
                    startingHealth = 450,
                    startingItems = new[] { new { templateId = CutlassId, tier = "Silver" } },
                    startingSkills = new[] { new { templateId = skillPreset.Id, tier = "Bronze" } },
                },
            });
            twoHumanResp.EnsureSuccessStatusCode();
            using var twoHumanDoc = JsonDocument.Parse(await twoHumanResp.Content.ReadAsStringAsync());
            string h2LobbyId = twoHumanDoc.RootElement.GetProperty("LobbyId").GetString()!;
            string h2Code = twoHumanDoc.RootElement.GetProperty("Code").GetString()!;
            (await guestHttp.PostAsync($"/lobbies/{h2Code}/members", new StringContent(""))).EnsureSuccessStatusCode();
            (await hostHttp.PutAsync($"/lobbies/{h2LobbyId}/countdown", new StringContent("{}"))).EnsureSuccessStatusCode();

            var guestAcc = accounts.GetOrCreateByName("V10Guest");
            string guestAid = guestAcc.AccountId.ToString("N");
            var h2Lobby = lobbies.Get(h2LobbyId)!;

            var hostSess = sessions.Create(hostAid, hostAcc.Username, new InitializeRunCommand { PlayMode = EPlayMode.Tournament, TournamentSessionId = h2Lobby.TournamentSessionId });
            var guestSess = sessions.Create(guestAid, guestAcc.Username, new InitializeRunCommand { PlayMode = EPlayMode.Tournament, TournamentSessionId = h2Lobby.TournamentSessionId });
            attachedSids.Add(hostSess.Sid);
            attachedSids.Add(guestSess.Sid);

            // #15 验证客机与房主同样自动强制继承 lobby.Rules（40 Gold / 9 Income / 450 HP / 初始 Cutlass & 技能）
            var (hostRun, hostEng) = CreateStartedEngine(
                fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts,
                playMode: EPlayMode.Tournament, accountId: hostAid, tournamentSessionId: h2Lobby.TournamentSessionId,
                lobbies: lobbies, sessions: sessions, seed: 1501);
            var (guestRun, guestEng) = CreateStartedEngine(
                fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts,
                playMode: EPlayMode.Tournament, accountId: guestAid, tournamentSessionId: h2Lobby.TournamentSessionId,
                lobbies: lobbies, sessions: sessions, seed: 1502);
            SessionRegistry.Attach(hostSess.Sid, hostRun, hostEng);
            SessionRegistry.Attach(guestSess.Sid, guestRun, guestEng);

            foreach (var r in new[] { hostRun, guestRun })
            {
                Assert.Equal(40, r.PlayerAttributes[EPlayerAttributeType.Gold]);
                Assert.Equal(9, r.PlayerAttributes[EPlayerAttributeType.Income]);
                Assert.Equal(450, r.PlayerAttributes[EPlayerAttributeType.HealthMax]);
                Assert.Contains(r.Cards.Values, c => c.Owner == ECombatantId.Player && c.TemplateId == Guid.Parse(CutlassId));
                Assert.Contains(r.Cards.Values, c => c.Owner == ECombatantId.Player && c.TemplateId == Guid.Parse(skillPreset.Id));
            }

            // Host 先到达 Day 1 Hour 6，Guest 停在 Day 1 Hour 2 → 触发同步屏障（且仅 1 人未就位，自动切为 30s 加速倒计时！）
            hostEng.Handle(new CheatCommand { Args = ["hour", "6"] });
            hostEng.Handle(new CheatCommand { Args = ["deal"] });
            lobbies.RecordRunProgress(h2Lobby, hostAid, hostRun);
            lobbies.RecordRunProgress(h2Lobby, guestAid, guestRun);

            using (var stHostBlocked = JsonDocument.Parse(await hostHttp.GetStringAsync("/tournament/status")))
            {
                Assert.False(stHostBlocked.RootElement.GetProperty("CanStartPvp").GetBoolean());
                Assert.True(stHostBlocked.RootElement.GetProperty("WaitingForOtherPlayers").GetBoolean());
                DateTime dl = stHostBlocked.RootElement.GetProperty("DeadlineAt").GetDateTime();
                double secLeft = (dl.ToUniversalTime() - nowUtc).TotalSeconds;
                Assert.InRange(secLeft, 1, 31); // 仅剩 1 人未就位，切为 AcceleratedRoundCountdownSeconds=30s
            }

            // 锁未解除时尝试直接点 PvP 卡应返回友好阻塞提示且不破坏 Choice 状态
            var hostPvpCard = hostRun.Cards[Assert.Single(hostRun.SelectionSet)];
            var blockedOut = hostEng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(hostPvpCard.InstanceId) });
            Assert.NotNull(blockedOut.Error);
            Assert.Equal(ERunState.Choice, hostRun.State);
            Assert.Contains(hostPvpCard.InstanceId, hostRun.SelectionSet);

            // 验证 Guest 正常追赶到达 Day 1 Hour 6 后，无需等待超时立即双向解锁（CanStartPvp = true, ForcedTransition = false）
            guestEng.Handle(new CheatCommand { Args = ["hour", "6"] });
            guestEng.Handle(new CheatCommand { Args = ["deal"] });
            lobbies.RecordRunProgress(h2Lobby, guestAid, guestRun);
            using (var stBothReady = JsonDocument.Parse(await hostHttp.GetStringAsync("/tournament/status")))
            {
                Assert.True(stBothReady.RootElement.GetProperty("CanStartPvp").GetBoolean());
                Assert.False(stBothReady.RootElement.GetProperty("WaitingForOtherPlayers").GetBoolean());
                Assert.False(stBothReady.RootElement.GetProperty("ForcedTransition").GetBoolean());
            }

            // Host 进入 Day 1 PvP 后推进至 Day 2 Hour 6，而 Guest 仍停留在 Day 1 Hour 6（2 人房再次仅剩 1 人未就位，触发 30s 加速倒计时）
            var hostD1PvpOut = hostEng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(hostPvpCard.InstanceId) });
            Assert.Null(hostD1PvpOut.Error);
            while (hostRun.Day < 2 && hostRun.State == ERunState.LevelUp && hostRun.SelectionSet.Count > 0)
            {
                var lvPick = hostRun.Cards[hostRun.SelectionSet[0]];
                INetCommand lvCmd = lvPick.Type == ECardType.Skill
                    ? new SelectSkillCommand { InstanceId = new InstanceId(lvPick.InstanceId) }
                    : new SelectItemCommand { InstanceId = new InstanceId(lvPick.InstanceId) };
                Assert.Null(hostEng.Handle(lvCmd).Error);
            }
            Assert.Equal(2u, hostRun.Day);
            hostEng.Handle(new CheatCommand { Args = ["hour", "6"] });
            hostEng.Handle(new CheatCommand { Args = ["deal"] });
            lobbies.RecordRunProgress(h2Lobby, hostAid, hostRun);
            using (var stD2Blocked = JsonDocument.Parse(await hostHttp.GetStringAsync("/tournament/status")))
            {
                Assert.False(stD2Blocked.RootElement.GetProperty("CanStartPvp").GetBoolean());
                Assert.True(stD2Blocked.RootElement.GetProperty("WaitingForOtherPlayers").GetBoolean());
            }

            // 强制将时间推进到超时解锁（+35s），让 Host 自动解锁开战并校验超时强制解锁状态
            nowUtc = nowUtc.AddSeconds(35);
            using (var stHostUnlocked = JsonDocument.Parse(await hostHttp.GetStringAsync("/tournament/status")))
            {
                Assert.True(stHostUnlocked.RootElement.GetProperty("CanStartPvp").GetBoolean());
                Assert.True(stHostUnlocked.RootElement.GetProperty("ForcedTransition").GetBoolean());
            }

            // 解锁后进入 Day 2 PvP
            var hostD2PvpCard = hostRun.Cards[Assert.Single(hostRun.SelectionSet)];
            var unlockedOut = hostEng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(hostD2PvpCard.InstanceId) });
            Assert.Null(unlockedOut.Error);
            Assert.NotNull(unlockedOut.CombatSpawnSim);
            Assert.Equal(ERunState.PVPCombat, unlockedOut.CombatSpawnSim!.CurrentState!.StateName);

            // 若当前随机匹配到 AI，校验 AI 继承了 +200 HP 差值、Cutlass 与预设技能；否则另开 1 人 + 7 AI 预设房专验 AI 继承
            var d2Assign = h2Lobby.Matchmaker.GetAssignment(hostAid, 2);
            if (d2Assign != null && d2Assign.OpponentIsBot)
            {
                Assert.Contains(unlockedOut.CombatSpawnSim.Cards.Values,
                    c => c.Placement?.Owner == ECombatantId.Opponent &&
                         unlockedOut.CombatSpawnSim.Events.OfType<GameSimEventCardSpawned>().Any(s => s.InstanceId == c.InstanceId && s.TemplateId.Equals(skillPreset.Id, StringComparison.OrdinalIgnoreCase)));
                Assert.True(unlockedOut.CombatSpawnSim.Opponent!.Attributes[EPlayerAttributeType.HealthMax] >= 400);
            }
            else
            {
                // 另开 1 人 + 7 AI 预设房，100% 必遇 AI，精确断言 AI 公平继承 StartingHealth(+200) / StartingItems / StartingSkills
                var botTestResp = await hostHttp.PostAsJsonAsync("/lobbies", new
                {
                    name = "AiFairCheck",
                    playMode = "Tournament",
                    playerCap = 16,
                    rules = new
                    {
                        startingHealth = 450,
                        startingIncome = 9,
                        startingItems = new[] { new { templateId = CutlassId, tier = "Silver" } },
                        startingSkills = new[] { new { templateId = skillPreset.Id, tier = "Bronze" } },
                    },
                });
                botTestResp.EnsureSuccessStatusCode();
                using var botTestDoc = JsonDocument.Parse(await botTestResp.Content.ReadAsStringAsync());
                string botLid = botTestDoc.RootElement.GetProperty("LobbyId").GetString()!;
                (await hostHttp.PutAsync($"/lobbies/{botLid}/countdown", new StringContent("{}"))).EnsureSuccessStatusCode();
                var botLobby = lobbies.Get(botLid)!;
                var (botRun, botEng) = CreateStartedEngine(
                    fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts,
                    playMode: EPlayMode.Tournament, accountId: hostAid, tournamentSessionId: botLobby.TournamentSessionId,
                    lobbies: lobbies, sessions: sessions, seed: 1509);
                botEng.Handle(new CheatCommand { Args = ["hour", "6"] });
                botEng.Handle(new CheatCommand { Args = ["deal"] });
                var botPvpCard = botRun.Cards[Assert.Single(botRun.SelectionSet)];
                var botPvpOut = botEng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(botPvpCard.InstanceId) });
                Assert.Null(botPvpOut.Error);
                var spawn = botPvpOut.CombatSpawnSim!;
                Assert.True(spawn.Opponent!.Attributes[EPlayerAttributeType.HealthMax] >= 400);
                Assert.Equal(9, spawn.Opponent.Attributes[EPlayerAttributeType.Income]);
                Assert.Contains(spawn.Events.OfType<GameSimEventCardSpawned>(),
                    s => s.CombatantId == ECombatantId.Opponent && s.TemplateId.Equals(CutlassId, StringComparison.OrdinalIgnoreCase));
                Assert.Contains(spawn.Events.OfType<GameSimEventCardSpawned>(),
                    s => s.CombatantId == ECombatantId.Opponent && s.TemplateId.Equals(skillPreset.Id, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            foreach (string sid in attachedSids)
            {
                SessionRegistry.Detach(sid);
                sessions.Remove(sid);
            }
            await app.StopAsync();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
