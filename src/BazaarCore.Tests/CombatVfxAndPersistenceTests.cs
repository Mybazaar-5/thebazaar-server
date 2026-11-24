using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarGameShared.TempoNet.Enums;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V4 核心测试套件（四）：验证 CombatSimConverter 全量特效属性/弹道/技能脉冲/任务/跳币、
/// RunSnapshotSerializer 无损往返（含 HourAdd/DayAdd/Transform/SuspendedStates）、
/// Persistence SQLite WAL + V3 迁移 + Resume/Concede、PvpGhostPool 真人入库及 AccountStore 生涯统计。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class CombatVfxAndPersistenceTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    private static string ResolveDataDir()
        => TestInfrastructure.TestDataLocator.ResolveDataDir();

    private static RunState.CardEntry MakeBoardItemEntry(
        string entryId,
        string coreId,
        Guid tplId,
        ECombatantId owner,
        EContainerSocketId socket,
        int baseDamage = 20,
        int cdMax = 2000)
    {
        var tpl = new CardTemplate
        {
            Id = tplId.ToString(),
            InternalName = "TestWeapon",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["DamageAmount"] = baseDamage,
                        ["CooldownMax"] = cdMax,
                    },
                },
            },
        };
        var core = new CardInstance(tpl, CardTier.Bronze, null, coreId);
        var entry = new RunState.CardEntry
        {
            InstanceId = entryId,
            TemplateId = tplId,
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = owner,
            Section = EInventorySection.Hand,
            Socket = socket,
            CoreCard = core,
        };
        entry.Attributes[ECardAttributeType.DamageAmount] = baseDamage;
        entry.Attributes[ECardAttributeType.CooldownMax] = cdMax;
        return entry;
    }

    [Fact]
    public void CombatSimConverter_ShouldEmitAllCardAttributeAndStatusVfxDeltas()
    {
        var repo = Repo.Value;
        var p1Entry = MakeBoardItemEntry("ent_p1", "core_p1", Guid.NewGuid(), ECombatantId.Player, EContainerSocketId.Socket_3, 20, 2000);
        var p2Entry = MakeBoardItemEntry("ent_p2", "core_p2", Guid.NewGuid(), ECombatantId.Opponent, EContainerSocketId.Socket_3, 15, 2000);

        var simResult = new CombatResult
        {
            WinnerPlayerId = "player",
            LoserPlayerId = "opponent",
            DurationMs = 150,
            Player1RemainingHp = 280,
            Player2RemainingHp = 0,
            Timeline =
            [
                new CombatTickState
                {
                    TimeMs = 0,
                    P1Hp = 300, P1MaxHp = 300, P2Hp = 200, P2MaxHp = 200,
                    Cards =
                    [
                        new CardCooldownFrame
                        {
                            InstanceId = "core_p1",
                            Progress01 = 0f,
                            CooldownMaxMs = 2000,
                            DamageAmount = 20,
                            Ammo = 3,
                            AmmoMax = 3,
                        },
                    ],
                },
                new CombatTickState
                {
                    TimeMs = 50,
                    P1Hp = 280, P1MaxHp = 300, P2Hp = 0, P2MaxHp = 200,
                    Cards =
                    [
                        new CardCooldownFrame
                        {
                            InstanceId = "core_p1",
                            Progress01 = 0.5f,
                            IsHasted = true,
                            IsSlowed = true,
                            IsFrozen = true,
                            IsFlying = true,
                            Heated = true,
                            Chilled = true,
                            HasteMs = 1000,
                            SlowMs = 500,
                            FreezeMs = 750,
                            Ammo = 2,
                            AmmoMax = 4,
                            CooldownMaxMs = 1600,
                            DamageAmount = 55,
                            ShieldApplyAmount = 30,
                            BurnApplyAmount = 8,
                            PoisonApplyAmount = 6,
                            HealAmount = 12,
                            RegenApplyAmount = 4,
                            CritChance = 40,
                            Multicast = 2,
                            Lifesteal = 1,
                            SellPrice = 10,
                            Counter = 1,
                        },
                    ],
                },
            ],
        };

        var sim = CombatSimConverter.ToCombatSim(
            simResult,
            repo,
            new Dictionary<string, CardInstance> { [p1Entry.InstanceId] = p1Entry.CoreCard! },
            new Dictionary<string, CardInstance> { [p2Entry.InstanceId] = p2Entry.CoreCard! },
            [p1Entry],
            [p2Entry]);

        var p1Updates = sim.Frames
            .SelectMany(f => f.CardUpdates.Values)
            .Where(cu => cu.CardInstanceId.Value == p1Entry.InstanceId)
            .SelectMany(cu => cu.Attributes)
            .ToList();

        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.Freeze && kv.Value.CurrentValue == 750);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.Haste && kv.Value.CurrentValue == 1000);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.Slow && kv.Value.CurrentValue == 500);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.Flying && kv.Value.CurrentValue == 1);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.Heated && kv.Value.CurrentValue == 1);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.Chilled && kv.Value.CurrentValue == 1);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.AmmoMax && kv.Value.CurrentValue == 4);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.Ammo && kv.Value.CurrentValue == 2);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.CooldownMax && kv.Value.CurrentValue == 1600);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.DamageAmount && kv.Value.CurrentValue == 55);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.ShieldApplyAmount && kv.Value.CurrentValue == 30);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.BurnApplyAmount && kv.Value.CurrentValue == 8);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.PoisonApplyAmount && kv.Value.CurrentValue == 6);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.HealAmount && kv.Value.CurrentValue == 12);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.RegenApplyAmount && kv.Value.CurrentValue == 4);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.CritChance && kv.Value.CurrentValue == 40);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.Multicast && kv.Value.CurrentValue == 2);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.Lifesteal && kv.Value.CurrentValue == 1);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.SellPrice && kv.Value.CurrentValue == 10);
        Assert.Contains(p1Updates, kv => kv.Key == ECardAttributeType.Counter && kv.Value.CurrentValue == 1);
    }

    [Fact]
    public void CombatSimConverter_ShouldEmitCardTargetedProjectiles_AndSkillPulses()
    {
        var repo = Repo.Value;
        var p1Weapon = MakeBoardItemEntry("ent_src", "core_src", Guid.NewGuid(), ECombatantId.Player, EContainerSocketId.Socket_3);
        var p2Target = MakeBoardItemEntry("ent_tgt", "core_tgt", Guid.NewGuid(), ECombatantId.Opponent, EContainerSocketId.Socket_3);
        var skillTpl = new CardTemplate { Id = Guid.NewGuid().ToString(), InternalName = "TestSkill", Type = "Skill" };
        var p1Skill = new RunState.CardEntry
        {
            InstanceId = "ent_skl",
            TemplateId = Guid.Parse(skillTpl.Id),
            Type = ECardType.Skill,
            Owner = ECombatantId.Player,
            CoreCard = new CardInstance(skillTpl, CardTier.Bronze, null, "core_skl"),
        };

        var simResult = new CombatResult
        {
            WinnerPlayerId = "player",
            LoserPlayerId = "opponent",
            DurationMs = 200,
            Timeline =
            [
                new CombatTickState { TimeMs = 0, P1Hp = 200, P1MaxHp = 200, P2Hp = 200, P2MaxHp = 200 },
            ],
            Events =
            [
                new CombatEvent
                {
                    TimeMs = 50,
                    Type = CombatEventType.FreezeApplied,
                    SourceCardInstanceId = "core_src",
                    TargetCardInstanceId = "core_tgt",
                    Value = 1000,
                },
                new CombatEvent
                {
                    TimeMs = 100,
                    Type = CombatEventType.HasteApplied,
                    SourceCardInstanceId = "core_skl",
                    TargetCardInstanceId = "core_src",
                    IsSkillSource = true,
                    AbilityId = "sk_haste",
                    ActionType = "TActionCardHaste",
                    Value = 1000,
                },
            ],
        };

        var sim = CombatSimConverter.ToCombatSim(
            simResult,
            repo,
            new Dictionary<string, CardInstance>
            {
                [p1Weapon.InstanceId] = p1Weapon.CoreCard!,
                [p1Skill.InstanceId] = p1Skill.CoreCard!,
            },
            new Dictionary<string, CardInstance> { [p2Target.InstanceId] = p2Target.CoreCard! },
            [p1Weapon, p1Skill],
            [p2Target]);

        var allEvents = sim.Frames.SelectMany(f => f.Events).ToList();

        // 1. 卡指卡冻结弹道：Target 必须是 EffectTargetCard 且指向 ent_tgt
        var freezeExec = allEvents
            .OfType<CombatSimEventEffectExecuted>()
            .FirstOrDefault(e => e.ActionType == EActionCommandType.CardFreeze);
        Assert.NotNull(freezeExec);
        var cardTarget = Assert.IsType<EffectTargetCard>(freezeExec!.Target);
        Assert.Equal("ent_tgt", cardTarget.Target.Value);

        // 2. P2-09：不再冗余发射客户端 0 消费的 CombatSimEventEffectTriggered，技能的 Haste 执行由 CombatSimEventEffectExecuted 完整驱动
        Assert.DoesNotContain(allEvents, e => e is CombatSimEventEffectTriggered);
        var skillExec = allEvents
            .OfType<CombatSimEventEffectExecuted>()
            .FirstOrDefault(e => e.Source == new InstanceId("ent_skl"));
        Assert.NotNull(skillExec);
        var skillTarget = Assert.IsType<EffectTargetCard>(skillExec!.Target);
        Assert.Equal("ent_src", skillTarget.Target.Value);
    }

    [Fact]
    public void CombatSimConverter_ShouldEmitQuestTransformEnchantAndMonsterRewardEvents()
    {
        var repo = Repo.Value;
        var realTargetTpl = repo.AlwaysSpawnableItems.First();
        var p1Item = MakeBoardItemEntry("ent_q", "core_q", Guid.Parse(realTargetTpl.Id), ECombatantId.Player, EContainerSocketId.Socket_4);
        var p2Monster = MakeBoardItemEntry("ent_m", "core_m", Guid.NewGuid(), ECombatantId.Opponent, EContainerSocketId.Socket_4);

        string newRealTplId = repo.AlwaysSpawnableItems.Skip(1).First().Id;

        var simResult = new CombatResult
        {
            WinnerPlayerId = "opponent",
            LoserPlayerId = "player",
            DurationMs = 250,
            Player1RemainingHp = 0,
            Player2RemainingHp = 400, // 1000 -> 400 (打掉 60%)
            Timeline =
            [
                new CombatTickState { TimeMs = 0, P1Hp = 300, P1MaxHp = 300, P2Hp = 1000, P2MaxHp = 1000 },
                new CombatTickState { TimeMs = 100, P1Hp = 100, P1MaxHp = 300, P2Hp = 400, P2MaxHp = 1000 },
            ],
            Events =
            [
                new CombatEvent { TimeMs = 40, Type = CombatEventType.QuestUpdated, TargetCardInstanceId = "core_q", Detail = "Quest_1", Value = 2 },
                new CombatEvent { TimeMs = 80, Type = CombatEventType.QuestCompleted, TargetCardInstanceId = "core_q", Detail = "Quest_1", Value = 3 },
                new CombatEvent { TimeMs = 120, Type = CombatEventType.CardEnchanted, SourceCardInstanceId = "core_q", TargetCardInstanceId = "core_q", Detail = "Fiery" },
                new CombatEvent { TimeMs = 160, Type = CombatEventType.CardTransformed, SourceCardInstanceId = "core_q", TargetCardInstanceId = "core_q_new", Detail = $"core_q->{newRealTplId}" },
            ],
        };

        var sim = CombatSimConverter.ToCombatSim(
            simResult,
            repo,
            new Dictionary<string, CardInstance> { [p1Item.InstanceId] = p1Item.CoreCard! },
            new Dictionary<string, CardInstance> { [p2Monster.InstanceId] = p2Monster.CoreCard! },
            [p1Item],
            [p2Monster],
            pveGoldReceived: 6,
            pveXpReceived: 3);

        var allEvents = sim.Frames.SelectMany(f => f.Events).ToList();
        Assert.Contains(allEvents, e => e is CombatSimEventCardQuestUpdated);
        Assert.Contains(allEvents, e => e is CombatSimEventCardQuestCompleted);
        Assert.Contains(allEvents, e => e is CombatSimEventCardEnchanted enc && enc.EnchantmentType == EEnchantmentType.Fiery);

        var trans = allEvents.OfType<CombatSimEventCardTransformed>().FirstOrDefault();
        Assert.NotNull(trans);
        Assert.Equal("ent_q", trans!.OriginalInstanceId);
        Assert.Single(trans.TransformedCards);
        Assert.Equal(newRealTplId, trans.TransformedCards[0].TemplateId);

        // PvE 跳币与跳经验事件 + 阈值配置
        Assert.NotEmpty(sim.OpponentHealthThresholdsForGold);
        Assert.NotEmpty(sim.OpponentHealthThresholdsForXp);
        Assert.Contains(allEvents, e => e is CombatSimEventMonsterGoldReceived);
        Assert.Contains(allEvents, e => e is CombatSimEventMonsterXpReceived);
    }

    [Fact]
    public void RunSnapshotSerializer_Roundtrip_ShouldPreserveAllStateGridsFourLayersAndSuspendedStack()
    {
        var repo = Repo.Value;
        var tpl1 = repo.AlwaysSpawnableItems.First();
        var tpl2 = repo.AlwaysSpawnableItems.Skip(1).First();
        var skillTpl = repo.AlwaysSpawnableSkills.First();

        var run = new RunState
        {
            RunId = "run_roundtrip_1",
            GameModeId = Guid.NewGuid(),
            Hero = EHero.Pygmalien,
            PlayMode = EPlayMode.Ranked,
            Day = 4,
            Hour = 5,
            Victories = 3,
            Defeats = 1,
            HasVisitedFates = true,
            TendencyLocked = true,
            State = ERunState.Encounter,
            CurrentEncounterId = "002d8f6d-0e36-4921-89ed-3f0f28682d7b",
            RerollCost = 4,
            RerollsRemaining = 2,
            UnlockedSlots = 0b00_1111_1100, // 6 格
            SelectionRules = new TSelectionContextRules
            {
                CanSelectMultiple = true,
                SelectionIsFree = false,
                CanExit = true,
                WillAutoSellOnExit = true,
                NextEncounterOnExit = Guid.NewGuid(),
                RerollRules = new RerollRules { StartingCost = 2, CostIncrease = 2, TotalAllowedRerolls = 3, CostMax = 8 },
            },
            CurrentPvpOpponent = new SimPvpOpponent("GhostRex", "Lord", "of Blades", ERank.Gold, 1500, 2, 3, 18, 5, EHero.Vanessa, null, null),
        };
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.PlayerAttributes[EPlayerAttributeType.Gold] = 42;
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 650;

        // 1. 手牌卡：含 PermanentAdd/Mult, HourAdd/Mult, DayAdd/Mult, ExtraTags, QuestProgress, CompletedQuests, TransformReplacementTemplateId
        var core1 = new CardInstance(tpl1, CardTier.Gold, "Deadly", "itm_hand_1")
        {
            TransformReplacementTemplateId = tpl2.Id,
        };
        core1.PermanentAdd["DamageAmount"] = 25;
        core1.PermanentMult["DamageAmount"] = 1.5;
        core1.HourAdd["DamageAmount"] = 8;
        core1.HourMult["CritChance"] = 1.25;
        core1.DayAdd["ShieldApplyAmount"] = 14;
        core1.DayMult["Value"] = 2.0;
        core1.ExtraTags.Add("Relic");
        core1.QuestProgress["Quest_1"] = 4;
        core1.CompletedQuests.Add("Quest_1");

        Assert.True(run.HandGrid.TryPlaceOrShift(core1, 3));
        var entry1 = new RunState.CardEntry
        {
            InstanceId = core1.InstanceId,
            TemplateId = Guid.Parse(tpl1.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tpl1.Size),
            Tier = ETier.Gold,
            Enchantment = EEnchantmentType.Deadly,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = (EContainerSocketId)core1.StartSlot,
            CoreCard = core1,
        };
        entry1.Attributes[ECardAttributeType.DamageAmount] = (int)Math.Round(core1.GetAttribute("DamageAmount"));
        run.Cards[entry1.InstanceId] = entry1;

        // 2. 背包卡
        var core2 = new CardInstance(tpl2, CardTier.Silver, null, "itm_stash_1");
        Assert.True(run.StashGrid.TryPlaceOrShift(core2, 5));
        run.Cards[core2.InstanceId] = new RunState.CardEntry
        {
            InstanceId = core2.InstanceId,
            TemplateId = Guid.Parse(tpl2.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tpl2.Size),
            Tier = ETier.Silver,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Stash,
            Socket = EContainerSocketId.Socket_5,
            CoreCard = core2,
        };

        // 3. 技能卡与候选货架卡
        var coreSk = new CardInstance(skillTpl, CardTier.Bronze, null, "skl_1");
        run.Cards[coreSk.InstanceId] = new RunState.CardEntry
        {
            InstanceId = coreSk.InstanceId,
            TemplateId = Guid.Parse(skillTpl.Id),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            CoreCard = coreSk,
        };
        run.Cards["cand_1"] = new RunState.CardEntry
        {
            InstanceId = "cand_1",
            TemplateId = Guid.Parse(tpl2.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tpl2.Size),
            Tier = ETier.Bronze,
            Owner = null,
            CoreCard = new CardInstance(tpl2, CardTier.Bronze, null, "cand_1"),
        };
        run.SelectionSet.Add("cand_1");

        var suspended = new List<RunSnapshotSerializer.SuspendedStateSnapshotDto>
        {
            new()
            {
                State = ERunState.LevelUp,
                CurrentEncounterId = null,
                SelectionSet = ["lvl_opt_1"],
                RerollCost = 0,
                RerollsRemaining = 0,
            },
        };

        string json1 = RunSnapshotSerializer.Serialize(run, startSelectionDone: true, pendingLevelUp: true, suspendedStates: suspended);
        Assert.True(RunSnapshotSerializer.TryDeserialize(json1, repo, out var restored, out var dto));
        Assert.NotNull(restored);
        Assert.NotNull(dto);

        // 再序列化对比字节级一致
        string json2 = RunSnapshotSerializer.Serialize(restored!, dto!.StartSelectionDone, dto.PendingLevelUp, dto.SuspendedStateStack);
        Assert.Equal(json1, json2);

        // 验证 Grid、四层属性、Quest、TransformReplacementTemplateId 与挂起栈完全还原
        var rCore1 = restored.Cards["itm_hand_1"].CoreCard!;
        Assert.Equal(25, rCore1.PermanentAdd["DamageAmount"]);
        Assert.Equal(1.5, rCore1.PermanentMult["DamageAmount"]);
        Assert.Equal(8, rCore1.HourAdd["DamageAmount"]);
        Assert.Equal(1.25, rCore1.HourMult["CritChance"]);
        Assert.Equal(14, rCore1.DayAdd["ShieldApplyAmount"]);
        Assert.Equal(2.0, rCore1.DayMult["Value"]);
        Assert.Contains("Relic", rCore1.ExtraTags);
        Assert.Equal(4, rCore1.QuestProgress["Quest_1"]);
        Assert.Contains("Quest_1", rCore1.CompletedQuests);
        Assert.Equal(tpl2.Id, rCore1.TransformReplacementTemplateId);
        Assert.Equal(core1.GetAttribute("DamageAmount"), rCore1.GetAttribute("DamageAmount"));

        Assert.Same(rCore1, restored.HandGrid.GetCardAtSlot(3));
        Assert.Same(restored.Cards["itm_stash_1"].CoreCard, restored.StashGrid.GetCardAtSlot(5));
        Assert.Equal(42, restored.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal("GhostRex", restored.CurrentPvpOpponent?.Name);
        Assert.Single(dto.SuspendedStateStack);
        Assert.True(RunSnapshotSerializer.InferStartSelectionDone(restored));

        var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(restored);
        Assert.Contains(resumeEvents, e => e is GameSimEventCardSpawned s && s.InstanceId == "itm_hand_1");
        Assert.Contains(resumeEvents, e => e is GameSimEventPlayerSkillEquipped sk && sk.InstanceId == "skl_1");
        Assert.Contains(resumeEvents, e => e is GameSimEventCardDealt d && d.InstanceId == "cand_1");
        Assert.Contains(resumeEvents, e => e is GameSimEventStateTransitioned st && st.ToState == ERunState.Encounter);
    }

    [Fact]
    public void Persistence_ShouldUseSqliteWal_MigrateLegacyV3Schema_AndSupportResumeAndConcede()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "bazaar_pers_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "bazaar.db");

        try
        {
            // 先构造旧版 V3 表结构（不含 career_json / is_ended / ended_reason / career_recorded / started_at / pvp_ghosts）
            Guid accId = Guid.NewGuid();
            using (var rawConn = new SqliteConnection($"Data Source={dbPath}"))
            {
                rawConn.Open();
                using var cmd = rawConn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE accounts (
                        account_id TEXT PRIMARY KEY,
                        username TEXT NOT NULL,
                        identity_key TEXT NOT NULL,
                        runs_completed INTEGER NOT NULL DEFAULT 0,
                        victories INTEGER NOT NULL DEFAULT 0,
                        defeats INTEGER NOT NULL DEFAULT 0,
                        best_day INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL,
                        last_seen TEXT NOT NULL
                    );
                    CREATE TABLE runs (
                        run_id TEXT PRIMARY KEY,
                        account_id TEXT NOT NULL,
                        hero INTEGER NOT NULL,
                        play_mode INTEGER NOT NULL,
                        game_mode_id TEXT NOT NULL,
                        day INTEGER NOT NULL,
                        hour INTEGER NOT NULL,
                        victories INTEGER NOT NULL,
                        defeats INTEGER NOT NULL,
                        prestige INTEGER NOT NULL,
                        gold INTEGER NOT NULL,
                        income INTEGER NOT NULL,
                        state INTEGER NOT NULL,
                        saved_at TEXT NOT NULL,
                        snapshot TEXT
                    );
                    """;
                cmd.ExecuteNonQuery();

                using var ins = rawConn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO accounts VALUES ($aid, 'legacy_u', 'name:legacy_u', 1, 10, 0, 10, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                    INSERT INTO runs VALUES ('old_ended', $aid, 1, 0, $gm, 10, 6, 10, 0, 20, 50, 10, 4, '2026-01-01T00:00:00Z', 'legacy_non_json');
                    """;
                ins.Parameters.AddWithValue("$aid", accId.ToString("D"));
                ins.Parameters.AddWithValue("$gm", Guid.NewGuid().ToString("D"));
                ins.ExecuteNonQuery();
            }

            // 构造 Persistence 触发自动 ALTER TABLE 迁移与 WAL
            var db = new Persistence(dbPath);
            using (var chk = new SqliteConnection($"Data Source={dbPath}"))
            {
                chk.Open();
                using var walCmd = chk.CreateCommand();
                walCmd.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("wal", walCmd.ExecuteScalar()?.ToString()?.ToLowerInvariant());
            }

            var oldEnded = db.LoadRun("old_ended");
            Assert.NotNull(oldEnded);
            Assert.True(oldEnded!.IsEnded);
            Assert.Equal("victory", oldEnded.EndedReason);
            Assert.Null(db.LoadLatestActiveRun(accId));

            // 写入一局有效活跃快照 -> LoadLatestActiveRun 应返回 -> ConcedeActiveRuns 后失效
            var activeRun = new RunState { RunId = "active_1", GameModeId = Guid.NewGuid(), Hero = EHero.Vanessa, Day = 2, Hour = 2 };
            string snap = RunSnapshotSerializer.Serialize(activeRun);
            db.SaveRun(new Persistence.RunRow("active_1", accId, EHero.Vanessa, EPlayMode.Unranked, activeRun.GameModeId, 2, 2, 1, 0, 20, 12, 5, ERunState.Choice, DateTime.UtcNow, snap));

            var loadedActive = db.LoadLatestActiveRun(accId);
            Assert.NotNull(loadedActive);
            Assert.Equal("active_1", loadedActive!.RunId);

            var conceded = db.ConcedeActiveRuns(accId);
            Assert.NotNull(conceded);
            Assert.Null(db.LoadLatestActiveRun(accId));
            Assert.Equal("conceded", db.LoadRun("active_1")!.EndedReason);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PvpGhostPool_And_AccountStore_ShouldPersistPlayerGhostsAndIdempotentCareerStats()
    {
        var repo = Repo.Value;
        string tempDir = Path.Combine(Path.GetTempPath(), "bazaar_pvp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "bazaar.db");

        try
        {
            var db = new Persistence(dbPath);
            var pool = PvpGhostPool.Load(ResolveDataDir());
            Assert.True(pool.Count >= 7000, $"应加载静态均衡幽灵池 >= 7000 条，实际: {pool.Count}");

            var run = new RunState
            {
                RunId = "pvp_rec_1",
                GameModeId = Guid.NewGuid(),
                Hero = EHero.Dooley,
                PlayMode = EPlayMode.Ranked,
                Day = 5,
                Hour = 6,
                Victories = 4,
                Defeats = 1,
                State = ERunState.PVPCombat,
            };
            var itemTpl = repo.AlwaysSpawnableItems.First();
            var entry = MakeBoardItemEntry("pvp_itm_1", "pvp_itm_1", Guid.Parse(itemTpl.Id), ECombatantId.Player, EContainerSocketId.Socket_3, 45, 2000);
            run.Cards[entry.InstanceId] = entry;

            var recorded = pool.RecordPlayerGhost(run, db);
            Assert.NotNull(recorded);
            Assert.True(recorded!.IsPlayerGhost);
            Assert.Equal(1, db.CountPvpGhosts());

            // 重建新 pool 验证从 SQLite 热加载真人镜像并可参与匹配
            var reloadedPool = new PvpGhostPool();
            int loaded = reloadedPool.LoadFromDb(db);
            Assert.Equal(1, loaded);
            var picked = reloadedPool.PickOpponent(day: 5, playerWins: 4, playerLosses: 1, new Random(1), repo);
            Assert.Equal(recorded.BoardId, picked.BoardId);

            // 验证 AccountStore 幂等生涯统计与重载
            var accStore = new AccountStore(db);
            var acc = accStore.GetOrCreateByName("tester_v4");
            run.Victories = 10;
            run.Defeats = 0;
            run.State = ERunState.EndRunVictory;

            bool firstRec = accStore.RecordRunCompleted(acc, run, isConceded: false, elapsedSeconds: 300);
            bool dupRec = accStore.RecordRunCompleted(acc, run, isConceded: false, elapsedSeconds: 300);
            Assert.True(firstRec);
            Assert.False(dupRec);

            var accStore2 = new AccountStore(db);
            accStore2.LoadAllFromDb();
            var reloadedAcc = accStore2.FindByUsername("tester_v4")!;
            Assert.Equal(1, reloadedAcc.RunsCompleted);
            Assert.Equal(1, reloadedAcc.HeroStats["Dooley"].TenWinTrophies);
            Assert.Equal(1, reloadedAcc.HeroStats["Dooley"].DiamondTrophies);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CombatSimConverter_CardFired_SelfActionShouldTargetOwner_AndMissingCardTargetShouldSkipNullCardTarget()
    {
        var repo = Repo.Value;
        var p1ShieldItem = MakeBoardItemEntry("ent_sh", "core_sh", Guid.NewGuid(), ECombatantId.Player, EContainerSocketId.Socket_3);
        var p2OppItem = MakeBoardItemEntry("ent_opp", "core_opp", Guid.NewGuid(), ECombatantId.Opponent, EContainerSocketId.Socket_3);

        var simResult = new CombatResult
        {
            WinnerPlayerId = "player",
            LoserPlayerId = "opponent",
            DurationMs = 150,
            Timeline = [new CombatTickState { TimeMs = 0, P1Hp = 200, P1MaxHp = 200, P2Hp = 200, P2MaxHp = 200 }],
            Events =
            [
                // Shield CardFired（Core 习惯填 TargetPlayerId = opp，Converter 应改回 Owner=Player）
                new CombatEvent
                {
                    TimeMs = 40,
                    Type = CombatEventType.CardFired,
                    SourceCardInstanceId = "core_sh",
                    TargetPlayerId = "opponent",
                    ActionType = "TActionPlayerShieldApply",
                },
                // 缺省 TargetCardInstanceId 的 CardHaste 不应发射空目标 EffectTargetCard
                new CombatEvent
                {
                    TimeMs = 80,
                    Type = CombatEventType.CardFired,
                    SourceCardInstanceId = "core_sh",
                    TargetCardInstanceId = null,
                    ActionType = "TActionCardHaste",
                },
            ],
        };

        var sim = CombatSimConverter.ToCombatSim(
            simResult, repo,
            new Dictionary<string, CardInstance> { [p1ShieldItem.InstanceId] = p1ShieldItem.CoreCard! },
            new Dictionary<string, CardInstance> { [p2OppItem.InstanceId] = p2OppItem.CoreCard! },
            [p1ShieldItem], [p2OppItem]);

        var execs = sim.Frames.SelectMany(f => f.Events).OfType<CombatSimEventEffectExecuted>().ToList();
        var shieldExec = Assert.Single(execs);
        Assert.Equal(EActionCommandType.PlayerShieldApply, shieldExec.ActionType);
        var playerTgt = Assert.IsType<EffectTargetPlayer>(shieldExec.Target);
        Assert.Equal(ECombatantId.Player, playerTgt.Target);
    }

    [Fact]
    public void CombatSimConverter_SixArgOverload_AndCodecWriteMessage_ShouldSucceed()
    {
        var repo = Repo.Value;
        var p1Item = MakeBoardItemEntry("ent_c1", "core_c1", Guid.NewGuid(), ECombatantId.Player, EContainerSocketId.Socket_3);
        var p2Item = MakeBoardItemEntry("ent_c2", "core_c2", Guid.NewGuid(), ECombatantId.Opponent, EContainerSocketId.Socket_3);

        var simResult = new CombatResult
        {
            WinnerPlayerId = "player",
            LoserPlayerId = "opponent",
            DurationMs = 100,
            Timeline = [new CombatTickState { TimeMs = 0, P1Hp = 200, P1MaxHp = 200, P2Hp = 0, P2MaxHp = 200 }],
        };

        var sim = CombatSimConverter.ToCombatSim(
            simResult, repo,
            new Dictionary<string, CardInstance> { [p1Item.InstanceId] = p1Item.CoreCard! },
            new Dictionary<string, CardInstance> { [p2Item.InstanceId] = p2Item.CoreCard! },
            [p1Item], [p2Item]);

        byte[] bytes = BazaarServer.Protocol.Codec.WriteMessage(new BazaarGameShared.Infra.Messages.NetMessageCombatSim(sim));
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 20);
    }

    [Fact]
    public void AccountStore_InRunProgressAndTierTrophies_ShouldAccumulateBronzeSilverGold()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "bazaar_acc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "bazaar.db");

        try
        {
            var db = new Persistence(dbPath);
            var store = new AccountStore(db);
            var acc = store.GetOrCreateByName("trophy_hunter");

            store.RecordInRunProgress(acc, EHero.Vanessa, monsterVictoriesDelta: 2, itemsSoldDelta: 3, coinsSpentDelta: 14, damageDealtDelta: 500);
            store.RecordRunCompleted(acc, "r_bronze", EHero.Vanessa, EPlayMode.Ranked, 6, victories: 4, defeats: 3, prestige: 0, ERunState.EndRunDefeat);
            store.RecordRunCompleted(acc, "r_silver", EHero.Vanessa, EPlayMode.Ranked, 9, victories: 7, defeats: 2, prestige: 0, ERunState.EndRunDefeat);
            store.RecordRunCompleted(acc, "r_gold", EHero.Vanessa, EPlayMode.Ranked, 11, victories: 10, defeats: 1, prestige: 12, ERunState.EndRunVictory);

            var stats = acc.HeroStats["Vanessa"];
            Assert.Equal(3, stats.GamesPlayed);
            Assert.Equal(3, stats.RankedGamesPlayed);
            Assert.Equal(1, stats.RunsWon);
            Assert.Equal(2, stats.RunsLost);
            Assert.Equal(1, stats.BronzeTrophies);
            Assert.Equal(1, stats.SilverTrophies);
            Assert.Equal(1, stats.GoldTrophies);
            Assert.Equal(1, stats.TenWinTrophies);
            Assert.Equal(2, stats.MonsterVictories);
            Assert.Equal(3, stats.ItemsSold);
            Assert.Equal(14, stats.CoinsSpent);
            Assert.Equal(500, stats.DamageDealt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
