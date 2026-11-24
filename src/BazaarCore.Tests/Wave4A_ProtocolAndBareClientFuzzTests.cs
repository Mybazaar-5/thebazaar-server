using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarGameShared.TempoNet.Enums;
using BazaarServer.Data;
using BazaarServer.Game;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 4A（#1 裸客户端软锁状态机根治与反向审计发包守卫 / #11 升级 4 选 1 图标多样性与 PvP 名牌 / 裸客户端全流程 100 局 Fuzz 不变量套件）。
/// </summary>
[Xunit.Collection("ShopEngineStaticState")]
public sealed class Wave4A_ProtocolAndBareClientFuzzTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);
    private static readonly Lazy<List<BazaarServer.Data.MonsterTemplate>> Monsters =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.CreateMonstersCopy());
    private static readonly Lazy<LevelUpTable> LevelUps =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.LevelUps);
    private static readonly Lazy<GameModeConfig> Mode =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.Mode);

    private static (RunState Run, RunEngine Engine) CreateStartedRun(
        EHero hero = EHero.Vanessa,
        uint day = 3,
        uint hour = 5,
        int gold = 30,
        int level = 3,
        int experience = 0,
        int seed = 40101,
        PvpGhostPool? ghosts = null)
    {
        var run = new RunState
        {
            RunId = "w4a_" + Guid.NewGuid().ToString("N")[..10],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Ranked,
            AccountId = "acc_" + Guid.NewGuid().ToString("N")[..8],
            Username = "Wave4ATester",
            Day = day,
            Hour = hour,
            State = ERunState.Choice,
            UnlockedSlots = day >= 4 ? (ushort)1023 : day == 3 ? (ushort)0b01_1111_1110 : (ushort)0b00_0111_1000,
            StartSelectionDone = true,
            RngSeed = seed,
        };
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.SetBasePlayerAttribute(EPlayerAttributeType.Gold, gold);
        run.SetBasePlayerAttribute(EPlayerAttributeType.HealthMax, 500 + (int)day * 150);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Health, 500 + (int)day * 150);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, 20);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Level, level);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Experience, experience);

        var engine = new RunEngine(
            run,
            Pool.Value,
            Monsters.Value,
            LevelUps.Value,
            Mode.Value,
            Repo.Value,
            ghosts ?? new PvpGhostPool());

        // 确保 Choice 态具备有效候选
        if (run.SelectionSet.Count == 0)
        {
            var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
            shop.DealHourEncounters();
        }

        return (run, engine);
    }

    private static RunState.CardEntry InjectChoiceEncounter(RunState run, string encTemplateId)
    {
        var coreTpl = Repo.Value.GetCard(encTemplateId);
        var poolTpl = Pool.Value.Get(encTemplateId);
        Assert.True(coreTpl != null || poolTpl != null, $"未找到遭遇模板 {encTemplateId}");

        var entry = new RunState.CardEntry
        {
            InstanceId = "enc_w4a_" + Guid.NewGuid().ToString("N")[..8],
            TemplateId = Guid.Parse(encTemplateId),
            Type = ShopEngine.ParseCardType(poolTpl?.Type ?? coreTpl?.Type ?? "EventEncounter"),
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.Cards[entry.InstanceId] = entry;
        run.SelectionSet.Add(entry.InstanceId);
        return entry;
    }

    private static string FindDirectCardSpawningEventEncounterId()
    {
        // 通用数据驱动查找：在 GameData.db 中自动定位无 SpawnContext / 无 NextEncounterOnSelection 且含 TTriggerOnCardSelected + 发卡动作的真实直效事件（如 Candy Stash / Snack Time 等）
        foreach (var tpl in Repo.Value.CardsById.Values)
        {
            if (!string.Equals(tpl.Type, "EventEncounter", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Guid.TryParse(tpl.Id, out _)) continue;
            if (tpl.SelectionContext?.SpawnContext != null) continue;
            if (tpl.NextEncounterOnSelection is { Count: > 0 }) continue;
            bool spawnsCards = tpl.Abilities.Values.Any(ab =>
                string.Equals(ab.Trigger?.Type, "TTriggerOnCardSelected", StringComparison.OrdinalIgnoreCase)
                && ActionHasCardSpawn(ab.Action));
            if (spawnsCards)
                return tpl.Id;
        }
        // 回退至任意直效 EventEncounter（如 Snack Time）
        return "e6b0bc78-4284-4bc7-b130-f5d04007ccf6";
    }

    private static bool ActionHasCardSpawn(RuleNode? action, int depth = 0)
    {
        if (action == null || depth > 6) return false;
        if (string.Equals(action.Type, "TActionGameSpawnCards", StringComparison.OrdinalIgnoreCase))
            return true;
        if (action.Actions != null)
        {
            foreach (var sub in action.Actions)
            {
                if (ActionHasCardSpawn(sub, depth + 1)) return true;
            }
        }
        return false;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #1 (A)：Day 3 Hour 5 选直效发卡事件（巧克力等）必须落地 Encounter，绝不同包 Choice -> Choice
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task1_Day3Hour5_DirectCardSpawnEncounter_LandsInEncounter_NoSamePacketChoiceConflict()
    {
        string directSpawnEncId = FindDirectCardSpawningEventEncounterId();
        var (run, engine) = CreateStartedRun(hero: EHero.Vanessa, day: 3, hour: 5, seed: 40102);

        var encCard = InjectChoiceEncounter(run, directSpawnEncId);
        ERunState prevState = run.State;
        var selectOutcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(encCard.InstanceId) });
        Assert.Null(selectOutcome.Error);

        var selectSim = run.BuildFullGameSim(selectOutcome.Events);
        // 1. 严禁 Choice -> Choice 同包直跳下一小时
        Assert.Equal(ERunState.Choice, prevState);
        Assert.Equal(ERunState.Encounter, selectSim.CurrentState!.StateName);
        Assert.Equal(5u, selectSim.Run!.Hour);
        Assert.True(selectSim.CurrentState.SelectionContextRules!.CanExit, "直效事件落地 Encounter 必须开放 CanExit=true");
        // 2. 本包包含直效发卡（CardSpawned）且绝不混发下一小时 Choice 的 CardDealt
        Assert.Contains(selectSim.Events, e => e is GameSimEventStateTransitioned st && st.ToState == ERunState.Encounter);
        Assert.DoesNotContain(selectSim.Events, e => e is GameSimEventCardDealt);
        Assert.DoesNotContain(selectSim.Events, e => e is GameSimEventRunHourChanged);

        // 3. 玩家点击离开事件 -> 走标准 Encounter -> Choice 沙尘门控安全推进至 Hour 6 PvP
        var exitOutcome = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitOutcome.Error);
        var exitSim = run.BuildFullGameSim(exitOutcome.Events);

        Assert.Equal(ERunState.Choice, exitSim.CurrentState!.StateName);
        Assert.Equal(6u, exitSim.Run!.Hour);
        Assert.NotEmpty(exitSim.CurrentState.SelectionSet!);
        Assert.NotNull(exitSim.CurrentState.PvpOpponent);
        Assert.Contains("_d3_", exitSim.CurrentState.PvpOpponent!.PlayerLoadout?.accountId ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #1 (B)：Hour 5 -> Hour 6 升级插队（含连升 2 级与快照持久化）绝不提前发射 RunHourChanged(6)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task1_Hour5ToHour6_LevelUpAndMultiLevelUp_DefersRunHourChanged6_And_PersistsAcrossSnapshot()
    {
        int xpPerLevel = Math.Max(1, Mode.Value.ExperiencePerLevel);
        // 场景 A：Hour 5 常规无额外离场 XP 遭遇退出，由 AdvanceHourOrDay 发放的 +1 小时 XP 刚好触发升级 -> 必置 PendingAdvanceHourAfterLevelUp=true 且不发 RunHourChanged(6)
        var (run, engine) = CreateStartedRun(
            hero: EHero.Vanessa,
            day: 2,
            hour: 5,
            level: 2,
            experience: xpPerLevel - 1,
            seed: 40105);

        run.State = ERunState.Encounter;
        run.CurrentEncounterId = null;
        run.PendingEncounterExitXp = 0;
        run.GainedHourXpThisHour = false;
        run.GainedAdvanceHourXpThisHour = false;
        run.SelectionRules = new TSelectionContextRules { CanExit = true };

        var exitRes = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitRes.Error);
        var simAfterExit = run.BuildFullGameSim(exitRes.Events);

        // 1. 退出 Hour 5 由小时推进 XP 触发升级插队时：必须保持 Hour == 5 (< HoursInADay) 且绝不发射 RunHourChanged！
        Assert.Equal(ERunState.LevelUp, simAfterExit.CurrentState!.StateName);
        Assert.Equal(5u, simAfterExit.Run!.Hour);
        Assert.True(run.PendingAdvanceHourAfterLevelUp, "应持久化 PendingAdvanceHourAfterLevelUp=true");
        Assert.DoesNotContain(simAfterExit.Events, e => e is GameSimEventRunHourChanged);

        // 2. 快照持久化往返不丢 PendingAdvanceHourAfterLevelUp
        string snapJson = RunSnapshotSerializer.Serialize(run, startSelectionDone: true);
        var restored = RunSnapshotSerializer.Deserialize(snapJson, Repo.Value);
        Assert.NotNull(restored);
        Assert.True(restored!.PendingAdvanceHourAfterLevelUp);
        Assert.Equal(5u, restored.Hour);
        Assert.Equal(ERunState.LevelUp, restored.State);

        // 3. 模拟连升 2 级（再额外增加 1 次 PendingLevelUp）：
        //    消费全部连升 LevelUp 奖励（若某级升级卡转入子 Encounter/Pedestal/Loot，自动走完子态后回到下一级 LevelUp），
        //    验证在整个连升与子遭遇期间 Hour 严格保持 5 (< HoursInADay)，直到全部升级结束才正式切入 Hour 6 PvP Choice！
        run.PendingLevelUps += 1;
        int levelChoicesPicked = 0;
        int guard = 0;
        while (run.State != ERunState.Choice && guard++ < 20)
        {
            Assert.True(run.Hour < (uint)Mode.Value.HoursInADay,
                $"升级及升级子遭遇期间（State={run.State}）Hour 必须严格小于 HoursInADay，实际 {run.Hour}");
            if (run.State == ERunState.LevelUp)
            {
                var pickRes = SelectAnyShelfCard(run, engine, run.SelectionSet[0]);
                Assert.NotNull(pickRes);
                Assert.Null(pickRes!.Error);
                levelChoicesPicked++;
            }
            else if (run.SelectionRules.CanExit)
            {
                var subExit = engine.Handle(new ExitCurrentStateCommand());
                Assert.Null(subExit.Error);
            }
            else if (run.SelectionSet.Count > 0)
            {
                var subPick = SelectAnyShelfCard(run, engine, run.SelectionSet[0]);
                Assert.NotNull(subPick);
                Assert.Null(subPick!.Error);
            }
        }

        Assert.Equal(2, levelChoicesPicked);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(6u, run.Hour);
        Assert.False(run.PendingAdvanceHourAfterLevelUp);
        Assert.NotEmpty(run.SelectionSet);
        Assert.NotNull(run.CurrentPvpOpponent);

        // 场景 B：Hour 5 选 Study (+2 离场 XP) 触发连升 2 级，同样全程保持 Hour < 6 并在升级结束后安全进入 Hour 6 PvP
        var (runStudy, engineStudy) = CreateStartedRun(
            hero: EHero.Vanessa,
            day: 3,
            hour: 5,
            level: 2,
            experience: xpPerLevel - 1,
            seed: 40106);
        var studyCard = InjectChoiceEncounter(runStudy, "ce38f4a2-f156-4082-b8ce-e8956c71fc0d");
        Assert.Null(engineStudy.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(studyCard.InstanceId) }).Error);
        Assert.Equal(ERunState.Encounter, runStudy.State);
        runStudy.PendingLevelUps = 1;
        var studyExit = engineStudy.Handle(new ExitCurrentStateCommand());
        Assert.Null(studyExit.Error);
        Assert.Equal(ERunState.LevelUp, runStudy.State);
        Assert.Equal(5u, runStudy.Hour);
        Assert.DoesNotContain(studyExit.Events, e => e is GameSimEventRunHourChanged);
        int studyGuard = 0;
        while (runStudy.State != ERunState.Choice && studyGuard++ < 20)
        {
            Assert.Equal(5u, runStudy.Hour);
            if (runStudy.State == ERunState.LevelUp || !runStudy.SelectionRules.CanExit)
            {
                var pick = SelectAnyShelfCard(runStudy, engineStudy, runStudy.SelectionSet[0]);
                Assert.NotNull(pick);
                Assert.Null(pick!.Error);
            }
            else
            {
                Assert.Null(engineStudy.Handle(new ExitCurrentStateCommand()).Error);
            }
        }
        Assert.Equal(ERunState.Choice, runStudy.State);
        Assert.Equal(6u, runStudy.Hour);
        Assert.NotNull(runStudy.CurrentPvpOpponent);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #1 (C)：反向审计服务端发包守卫 SanitizeWireEventsAndCards（H-2 / H-4 / H-5a / P1-44）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task1_SanitizeWireEventsAndCards_Enforces_H2_H4_H5a_P1_44_HardInvariants()
    {
        var (run, _) = CreateStartedRun(day: 2, hour: 2, seed: 40109);
        Guid skillTplGuid = Guid.NewGuid();
        Guid transformTplGuid = Guid.NewGuid();

        var skill1 = new RunState.CardEntry
        {
            InstanceId = "sk_live_1",
            TemplateId = skillTplGuid,
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
        };
        var skill2Dup = new RunState.CardEntry
        {
            InstanceId = "sk_dup_2",
            TemplateId = skillTplGuid, // 同一玩家同模板重复技能（P1-44）
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
        };
        run.Cards[skill1.InstanceId] = skill1;
        run.Cards[skill2Dup.InstanceId] = skill2Dup;

        var rawEvents = new List<IGameSimEvent>
        {
            // H-2：同包 CardSpawned + CardMoved（应剔除冗余 CardMoved）
            new GameSimEventCardSpawned("it_spawn_1", Guid.NewGuid().ToString(), ECardType.Item, ECombatantId.Player, EInventorySection.Stash, EContainerSocketId.Socket_0),
            new GameSimEventCardMoved("it_spawn_1", ECombatantId.Player, EInventorySection.Stash, EContainerSocketId.Socket_0),
            // H-2：同包 CardDisposed + CardMoved（应剔除冗余 CardMoved）
            new GameSimEventCardDisposed("it_disp_1", ECardDisposalReason.Cleared),
            new GameSimEventCardMoved("it_disp_1", ECombatantId.Player, EInventorySection.Hand, EContainerSocketId.Socket_1),
            // H-4：CardTransformed 的新产物不在 run.Cards 里（应自动补齐 sim.Cards["tr_new_1"]）
            new GameSimEventCardTransformed("tr_orig_1", transformTplGuid.ToString(),
            [
                new SimEventCardTransformation("tr_new_1", transformTplGuid.ToString(), ECardType.Item, ECombatantId.Player, EInventorySection.Hand, EContainerSocketId.Socket_2)
            ]),
            // H-5a：孤立 PlayerSkillEquipped（目标卡不存在于 run.Cards/sim.Cards，应剔除）
            new GameSimEventPlayerSkillEquipped("sk_orphan_ghost", ECombatantId.Player),
            // P1-44：同一玩家同一 TemplateId 重复 CardSpawned + PlayerSkillEquipped（应仅保留第 1 条）
            new GameSimEventCardSpawned("sk_live_1", skillTplGuid.ToString(), ECardType.Skill, ECombatantId.Player, null, null),
            new GameSimEventPlayerSkillEquipped("sk_live_1", ECombatantId.Player),
            new GameSimEventCardSpawned("sk_dup_2", skillTplGuid.ToString(), ECardType.Skill, ECombatantId.Player, null, null),
            new GameSimEventPlayerSkillEquipped("sk_dup_2", ECombatantId.Player),
        };

        var sim = run.BuildDeltaGameSim(rawEvents);

        // H-2 断言
        Assert.DoesNotContain(sim.Events, e => e is GameSimEventCardMoved mv && mv.InstanceId is "it_spawn_1" or "it_disp_1");
        // H-4 断言
        Assert.True(sim.Cards.ContainsKey("tr_new_1"), "H-4：CardTransformed 目标实体必须存在于 sim.Cards 中");
        // H-5a 断言
        Assert.DoesNotContain(sim.Events, e => e is GameSimEventPlayerSkillEquipped sk && sk.InstanceId == "sk_orphan_ghost");
        // P1-44 断言
        Assert.Single(sim.Events.OfType<GameSimEventPlayerSkillEquipped>(), sk => sk.InstanceId == "sk_live_1");
        Assert.DoesNotContain(sim.Events, e => e is GameSimEventPlayerSkillEquipped sk && sk.InstanceId == "sk_dup_2");
        Assert.DoesNotContain(sim.Events, e => e is GameSimEventCardSpawned sp && sp.InstanceId == "sk_dup_2");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #11 (① + ②)：升级 4 选 1 图标多样性（告别 4 个同款青铜宝箱）+ PvP 名牌去除 (Normal) 后缀 & H-1 跨天 accountId 必异
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task11_LevelUpRewards_EliminatesFourIdenticalBronzeChests_And_CleanPvpNameplate()
    {
        var repo = Repo.Value;
        var levelUps = LevelUps.Value;

        // 1. 升级 4 选 1 图标多样性：Lv3（映射 DB Id=2，4 选 1）连续抽样 80 次，绝不再出现 4 张完全同 ArtKey 的青铜宝箱
        int fourIdenticalArtRuns = 0;
        int totalDistinctArtKeys = 0;
        for (int s = 0; s < 80; s++)
        {
            var ps = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10) { Level = 3, Day = 2, Hour = 4 };
            var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, ps, null, new Random(41100 + s))
            {
                CurrentDay = 2,
                CurrentHour = 4,
            };
            var picks = levelUps.RollRewardsWithTier(level: 3, ctx, isAssetValid: Pool.Value.IsAssetValid);
            Assert.True(picks.Count >= 3, $"Lv3 应产出至少 3 张候选卡，实际 {picks.Count}");

            var artKeys = picks
                .Select(p => repo.GetCard(p.TemplateId)?.ArtKey)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList();
            if (artKeys.Count >= 4 && artKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                fourIdenticalArtRuns++;
            totalDistinctArtKeys += artKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }
        Assert.Equal(0, fourIdenticalArtRuns);
        Assert.True(totalDistinctArtKeys >= 80 * 2, "同屏 ArtKey 多样性应显著高于单一青铜宝箱重复");

        // 2. PvP 名牌清爽展示（去 (Normal)/(Strong) 后缀）+ H-1 跨天 accountId 必异
        var (run, engine) = CreateStartedRun(hero: EHero.Vanessa, day: 1, hour: 5, seed: 41199);
        var seenAccountIds = new HashSet<string>(StringComparer.Ordinal);
        for (uint d = 1; d <= 3; d++)
        {
            run.Day = d;
            run.Hour = 5;
            run.State = ERunState.Choice;
            run.PendingPvpGhost = null;
            run.CurrentPvpOpponent = null!;
            run.SelectionSet.Clear();
            var study = InjectChoiceEncounter(run, "ce38f4a2-f156-4082-b8ce-e8956c71fc0d");
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(study.InstanceId) }).Error);
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

            while (run.State == ERunState.LevelUp)
            {
                var lvPick = SelectAnyShelfCard(run, engine, run.SelectionSet[0]);
                Assert.NotNull(lvPick);
                Assert.Null(lvPick!.Error);
            }

            Assert.Equal(6u, run.Hour);
            Assert.NotNull(run.CurrentPvpOpponent);
            string oppName = run.CurrentPvpOpponent!.Name ?? "";
            Assert.DoesNotContain("(Normal)", oppName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("(Strong)", oppName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("(Weak)", oppName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("(MetaBoss)", oppName, StringComparison.OrdinalIgnoreCase);

            string accId = run.CurrentPvpOpponent.PlayerLoadout?.accountId ?? "";
            Assert.Contains($"_d{d}_", accId, StringComparison.OrdinalIgnoreCase);
            Assert.True(seenAccountIds.Add(accId), $"跨天 Day {d} 的幽灵 accountId 必须唯一：{accId}");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 硬门禁：裸客户端全流程 100 局 Fuzz 不变量测试
    // 覆盖：100 局跨日推进 + 强制周期注入直效发卡事件 + Hour 5->6 升级插队 + 货架附魔物品买进前后一致性 +
    //       PvE/PvP 战斗首帧零虚假差分 + 零 PlayRevealAnimations 冲突包 + 零 H-2/H-4/H-5a/P1-44 协议违例
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BareClient_FullFlow_100Runs_FuzzInvariantSuite()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        string directSpawnEncId = FindDirectCardSpawningEventEncounterId();
        var heroes = new[]
        {
            EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak,
            EHero.Stelle, EHero.Jules, EHero.Karnok, EHero.Hero8
        };

        // 预先从真实 DB 挑选一批带附魔衍生属性的物品模板，用于 Fuzz 中抽检货架与手牌属性一致性
        var enchantedProbePairs = new List<(CardTemplate Tpl, string Ench, ECardAttributeType Attr)>();
        foreach (var (enchName, attrName, attrEnum) in new (string, string, ECardAttributeType)[]
        {
            ("Fiery", "BurnApplyAmount", ECardAttributeType.BurnApplyAmount),
            ("Toxic", "PoisonApplyAmount", ECardAttributeType.PoisonApplyAmount),
            ("Shielded", "ShieldApplyAmount", ECardAttributeType.ShieldApplyAmount),
            ("Restorative", "HealAmount", ECardAttributeType.HealAmount),
            ("Obsidian", "DamageAmount", ECardAttributeType.DamageAmount),
        })
        {
            var found = repo.CardsById.Values.FirstOrDefault(t =>
                t.Type == "Item"
                && (t.Attributes == null || !t.Attributes.ContainsKey(attrName))
                && t.Enchantments != null
                && t.Enchantments.TryGetValue(enchName, out var ed)
                && ed != null
                && ((ed.Attributes != null && ed.Attributes.ContainsKey(attrName))
                    || (ed.Auras != null && ed.Auras.Values.Any(a => string.Equals(a.Action?.AttributeType, attrName, StringComparison.OrdinalIgnoreCase)))));
            if (found != null)
                enchantedProbePairs.Add((found, enchName, attrEnum));
        }

        int totalCommandsExecuted = 0;
        int totalCombatsVerified = 0;
        int totalDirectEventsVerified = 0;
        int totalHour5LevelUpsVerified = 0;
        int totalEnchantedShelfChecks = 0;

        for (int runIdx = 0; runIdx < 100; runIdx++)
        {
            var rng = new Random(20264000 + runIdx * 131);
            var hero = heroes[runIdx % heroes.Length];
            var (run, engine) = CreateStartedRun(
                hero: hero,
                day: 1,
                hour: 1,
                gold: 40,
                level: 1,
                experience: 0,
                seed: 20265000 + runIdx * 37);

            // 模拟裸客户端追踪状态
            ERunState clientState = run.State;
            uint clientHour = run.Hour;
            uint hoursInDay = (uint)Mode.Value.HoursInADay;
            bool injectedDirectAtHour5 = false;
            bool injectedLevelUpAtHour5 = false;
            var prevDayGhostAccountIds = new HashSet<string>(StringComparer.Ordinal);

            // 每局推进至少跨完 Day 1..Day 3（含多场 PvE 与 PvP、商店、事件、升级），最多 45 步防死循环
            for (int step = 0; step < 45 && run.State is not (ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown) && run.Day <= 3; step++)
            {
                // 1. 定期在 Hour 5 注入直效发卡事件（模拟 Day 3 Hour 5 选巧克力），压测无 Choice->Choice 软锁
                if (!injectedDirectAtHour5 && run.State == ERunState.Choice && run.Hour == 5 && run.Day == (uint)((runIdx % 3) + 1))
                {
                    injectedDirectAtHour5 = true;
                    var directCard = InjectChoiceEncounter(run, directSpawnEncId);
                    var directOutcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(directCard.InstanceId) });
                    Assert.Null(directOutcome.Error);
                    VerifyBareClientPacketInvariants(run, directOutcome, clientState, clientHour, hoursInDay, runIdx, step, ref totalCombatsVerified);
                    Assert.Equal(ERunState.Encounter, run.State);
                    Assert.True(run.SelectionRules.CanExit);
                    clientState = run.State;
                    clientHour = run.Hour;
                    totalCommandsExecuted++;
                    totalDirectEventsVerified++;
                    continue;
                }

                // 2. 定期在 Hour 5 退出前将经验调至差 1 XP 升级，压测 Hour 5 -> 6 升级插队不提前发 RunHourChanged(6)
                if (!injectedLevelUpAtHour5 && run.State == ERunState.Encounter && run.Hour == 5 && run.SelectionRules.CanExit)
                {
                    injectedLevelUpAtHour5 = true;
                    int req = Math.Max(1, Mode.Value.ExperiencePerLevel);
                    run.SetBasePlayerAttribute(EPlayerAttributeType.Experience, req - 1);
                    var exitLvOutcome = engine.Handle(new ExitCurrentStateCommand());
                    Assert.Null(exitLvOutcome.Error);
                    VerifyBareClientPacketInvariants(run, exitLvOutcome, clientState, clientHour, hoursInDay, runIdx, step, ref totalCombatsVerified);
                    if (run.State == ERunState.LevelUp)
                    {
                        Assert.True(run.Hour < hoursInDay, $"Run #{runIdx} Hour5->6 升级插队禁止提前切到 Hour {hoursInDay}");
                        totalHour5LevelUpsVerified++;
                    }
                    clientState = run.State;
                    clientHour = run.Hour;
                    totalCommandsExecuted++;
                    continue;
                }

                // 3. 每 5 局抽检 1 次货架附魔物品属性同步一致性（货架预览 == 买进手牌）
                if (step == 0 && runIdx % 5 == 0 && enchantedProbePairs.Count > 0)
                {
                    var probe = enchantedProbePairs[(runIdx / 5) % enchantedProbePairs.Count];
                    VerifyShelfVsHandEnchantedAttr(run, pool, repo, probe.Tpl, probe.Ench, probe.Attr);
                    totalEnchantedShelfChecks++;
                }

                // 4. 按当前状态自动选择合法客户端命令推进对局
                RunEngine.Outcome? outcome = null;
                if (run.State == ERunState.Choice)
                {
                    if (run.Hour == hoursInDay && run.CurrentPvpOpponent != null)
                    {
                        string accId = run.CurrentPvpOpponent.PlayerLoadout?.accountId ?? "";
                        if (!string.IsNullOrEmpty(accId))
                            prevDayGhostAccountIds.Add(accId);
                        string oppName = run.CurrentPvpOpponent.Name ?? "";
                        Assert.DoesNotContain("(Normal)", oppName, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("(Strong)", oppName, StringComparison.OrdinalIgnoreCase);
                    }

                    if (run.SelectionSet.Count > 0)
                    {
                        string chosenId = run.SelectionSet[rng.Next(run.SelectionSet.Count)];
                        outcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(chosenId) });
                    }
                }
                else if (run.State == ERunState.LevelUp)
                {
                    if (run.SelectionSet.Count > 0)
                    {
                        string chosenId = run.SelectionSet[rng.Next(run.SelectionSet.Count)];
                        outcome = SelectAnyShelfCard(run, engine, chosenId);
                    }
                }
                else if (run.State is ERunState.Encounter or ERunState.Pedestal or ERunState.Loot)
                {
                    // 在商店/事件/战利品中以概率尝试选一张或直接离场
                    bool trySelect = run.SelectionSet.Count > 0 && (!run.SelectionRules.CanExit || rng.NextDouble() < 0.45);
                    if (trySelect)
                    {
                        foreach (string cid in run.SelectionSet)
                        {
                            var attempt = SelectAnyShelfCard(run, engine, cid);
                            if (attempt != null && attempt.Error == null)
                            {
                                outcome = attempt;
                                break;
                            }
                        }
                    }

                    if (outcome == null && run.SelectionRules.CanExit)
                    {
                        outcome = engine.Handle(new ExitCurrentStateCommand());
                    }
                    else if (outcome == null && run.SelectionSet.Count > 0)
                    {
                        outcome = SelectAnyShelfCard(run, engine, run.SelectionSet[0]);
                    }
                }

                if (outcome == null || outcome.Error != null)
                {
                    // 若当前遭遇选项因金币/槽位不足不可选且不允许退出，则安全退出兜底检查
                    if (run.SelectionRules.CanExit)
                    {
                        outcome = engine.Handle(new ExitCurrentStateCommand());
                        Assert.Null(outcome.Error);
                    }
                    else
                    {
                        break;
                    }
                }

                VerifyBareClientPacketInvariants(run, outcome, clientState, clientHour, hoursInDay, runIdx, step, ref totalCombatsVerified);
                clientState = run.State;
                clientHour = run.Hour;
                totalCommandsExecuted++;
            }
        }

        Assert.True(totalCommandsExecuted >= 1500, $"100 局 Fuzz 应执行足够多步命令，实际 {totalCommandsExecuted}");
        Assert.True(totalCombatsVerified >= 150, $"100 局 Fuzz 应校验足够多场 PvE/PvP 战斗首帧，实际 {totalCombatsVerified}");
        Assert.True(totalDirectEventsVerified >= 80, $"100 局 Fuzz 应充分压测直效发卡事件，实际 {totalDirectEventsVerified}");
        Assert.True(totalHour5LevelUpsVerified >= 50, $"100 局 Fuzz 应充分压测 Hour5->6 升级插队，实际 {totalHour5LevelUpsVerified}");
        Assert.True(totalEnchantedShelfChecks >= 20, $"100 局 Fuzz 应抽检货架附魔一致性，实际 {totalEnchantedShelfChecks}");
    }

    private static void VerifyBareClientPacketInvariants(
        RunState run,
        RunEngine.Outcome outcome,
        ERunState prevState,
        uint prevHour,
        uint hoursInDay,
        int runIdx,
        int step,
        ref int combatsVerified)
    {
        var simsToCheck = new List<GameSim>();
        if (outcome.CombatSpawnSim != null) simsToCheck.Add(outcome.CombatSpawnSim);
        if (outcome.CombatDespawnSim != null) simsToCheck.Add(outcome.CombatDespawnSim);
        if (outcome.CombatSpawnSim == null && outcome.CombatDespawnSim == null)
            simsToCheck.Add(run.BuildFullGameSim(outcome.Events));

        ERunState trackingState = prevState;
        uint trackingHour = prevHour;

        foreach (var sim in simsToCheck)
        {
            ERunState nextState = sim.CurrentState!.StateName;
            uint nextHour = sim.Run!.Hour;

            // 不变量 1（#1 核心）：严禁 Choice -> Choice 同包携带 CardSpawned 或跨小时 CardDealt（防 PlayRevealAnimations skipped）
            bool hasSpawned = sim.Events.Any(e => e is GameSimEventCardSpawned);
            bool hasDealt = sim.Events.Any(e => e is GameSimEventCardDealt);
            bool hourChanged = sim.Events.Any(e => e is GameSimEventRunHourChanged or GameSimEventRunDayChanged);
            if (trackingState == ERunState.Choice && nextState == ERunState.Choice && (hasSpawned || (hasDealt && hourChanged)))
            {
                Assert.Fail($"Run#{runIdx} Step#{step}: 检测到致命 Choice -> Choice 冲突包 (hasSpawned={hasSpawned}, hasDealt={hasDealt}, hourChanged={hourChanged})");
            }

            // 不变量 2（#1 时钟门控）：严禁从 Hour < HoursInADay 推进直接落在非 Choice 态且 Hour == HoursInADay（防止 _waitForNewDayTransition 锁死）
            if (trackingHour < hoursInDay && nextHour == hoursInDay && nextState != ERunState.Choice)
            {
                Assert.Fail($"Run#{runIdx} Step#{step}: 非 Choice 态 ({nextState}) 提前出现 Hour == {hoursInDay}，将触发裸端 _waitForNewDayTransition 死锁");
            }

            // 不变量 3（H-2）：同包严禁对已 CardSpawned 或 CardDisposed 的实体同时发送 CardMoved
            var spawnedOrDisposed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ev in sim.Events)
            {
                if (ev is GameSimEventCardSpawned sp && !string.IsNullOrEmpty(sp.InstanceId)) spawnedOrDisposed.Add(sp.InstanceId);
                if (ev is GameSimEventCardDisposed dp && !string.IsNullOrEmpty(dp.InstanceId)) spawnedOrDisposed.Add(dp.InstanceId);
            }
            foreach (var ev in sim.Events)
            {
                if (ev is GameSimEventCardMoved mv && !string.IsNullOrEmpty(mv.InstanceId))
                    Assert.False(spawnedOrDisposed.Contains(mv.InstanceId), $"Run#{runIdx} Step#{step}: H-2 违例，{mv.InstanceId} 同包 Spawn/Dispose + Move");
            }

            // 不变量 4（H-4）：CardTransformed / CardTransformReverted 引用的目标实体必须存在于 sim.Cards
            foreach (var ev in sim.Events)
            {
                if (ev is GameSimEventCardTransformed tr && tr.TransformedCards != null)
                {
                    foreach (var tc in tr.TransformedCards)
                        if (tc != null && !string.IsNullOrEmpty(tc.InstanceId))
                            Assert.True(sim.Cards.ContainsKey(tc.InstanceId), $"Run#{runIdx} Step#{step}: H-4 违例，变形产物 {tc.InstanceId} 缺失于 sim.Cards");
                }
                if (ev is GameSimEventCardTransformReverted rev && rev.OriginalCard != null && !string.IsNullOrEmpty(rev.OriginalCard.InstanceId))
                {
                    Assert.True(sim.Cards.ContainsKey(rev.OriginalCard.InstanceId), $"Run#{runIdx} Step#{step}: H-4 违例，还原卡 {rev.OriginalCard.InstanceId} 缺失于 sim.Cards");
                }
            }

            // 不变量 5（H-5a + P1-44）：PlayerSkillEquipped 必须非孤立且无重复实体
            var seenEquipped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ev in sim.Events.OfType<GameSimEventPlayerSkillEquipped>())
            {
                Assert.False(string.IsNullOrEmpty(ev.InstanceId));
                Assert.True(sim.Cards.ContainsKey(ev.InstanceId), $"Run#{runIdx} Step#{step}: H-5a 违例，孤立技能 {ev.InstanceId}");
                Assert.True(seenEquipped.Add(ev.InstanceId), $"Run#{runIdx} Step#{step}: P1-44 违例，重复装配技能 {ev.InstanceId}");
            }

            trackingState = nextState;
            trackingHour = nextHour;
        }

        // 不变量 6（#5 首帧虚假差分）：检查每场战斗 CombatSim 首帧无恒定静态上限虚假差分
        if (outcome.Combat != null && outcome.Combat.Frames.Count > 0)
        {
            combatsVerified++;
            var f0 = outcome.Combat.Frames[0];
            Assert.False(f0.PlayerUpdates?.Attributes.ContainsKey(EPlayerAttributeType.RageMax) == true,
                $"Run#{runIdx} Step#{step}: 玩家首帧出现虚假 RageMax 差分");
            Assert.False(f0.OpponentUpdates?.Attributes.ContainsKey(EPlayerAttributeType.RageMax) == true,
                $"Run#{runIdx} Step#{step}: 敌方首帧出现虚假 RageMax 差分");
            Assert.False(f0.PlayerUpdates?.Attributes.ContainsKey(EPlayerAttributeType.EnragedDurationMax) == true,
                $"Run#{runIdx} Step#{step}: 玩家首帧出现虚假 EnragedDurationMax 差分");
            Assert.False(f0.OpponentUpdates?.Attributes.ContainsKey(EPlayerAttributeType.EnragedDurationMax) == true,
                $"Run#{runIdx} Step#{step}: 敌方首帧出现虚假 EnragedDurationMax 差分");
            Assert.False(f0.PlayerUpdates?.Attributes.ContainsKey(EPlayerAttributeType.TempoGainCooldownMax) == true,
                $"Run#{runIdx} Step#{step}: 玩家首帧出现虚假 TempoGainCooldownMax 差分");
            Assert.False(f0.OpponentUpdates?.Attributes.ContainsKey(EPlayerAttributeType.TempoGainCooldownMax) == true,
                $"Run#{runIdx} Step#{step}: 敌方首帧出现虚假 TempoGainCooldownMax 差分");
        }
    }

    private static void VerifyShelfVsHandEnchantedAttr(
        RunState run,
        CardPool pool,
        GameDataRepository repo,
        CardTemplate tpl,
        string enchName,
        ECardAttributeType attrEnum)
    {
        var shop = new ShopEngine(run, pool, repo, run.CustomRules);
        var tier = SpawnQueryEngine.ClampToSupportedTier(tpl, CardTier.Silver);
        var shelfCore = new CardInstance(tpl, tier, enchName, "fuzz_sh_" + Guid.NewGuid().ToString("N")[..6]);
        var shelfEntry = new RunState.CardEntry
        {
            InstanceId = shelfCore.InstanceId,
            TemplateId = Guid.Parse(tpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tpl.Size),
            Tier = ShopEngine.ParseTier(tier.ToString()),
            Enchantment = Enum.Parse<EEnchantmentType>(enchName, ignoreCase: true),
            Owner = ECombatantId.Player,
            CoreCard = shelfCore,
        };
        RunState.PopulateMetaFromTemplate(shelfEntry, pool.Get(tpl.Id), tpl);

        var syncMethod = typeof(ShopEngine).GetMethod(
            "ApplyLocalShelfAuraAndSync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        syncMethod.Invoke(shop, [shelfEntry, shelfCore, false]);

        var handCore = new CardInstance(tpl, tier, enchName, "fuzz_hd_" + Guid.NewGuid().ToString("N")[..6]);
        var ps = new PlayerState(
            "player", "P1", run.Hero.ToString(),
            run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 500),
            run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20),
            run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0),
            10)
        {
            Day = (int)run.Day,
            Hour = (int)run.Hour,
            Level = Math.Max(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1)),
        };
        ps.Attributes["Income"] = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, 0);
        ps.Hand.TryAddAuto(handCore);
        AuraEngine.RecalculateAuras(ps, null, RuleScope.OutOfCombat, (int)run.Day, (int)run.Hour, repo);

        var handEntry = new RunState.CardEntry
        {
            InstanceId = handCore.InstanceId,
            TemplateId = Guid.Parse(tpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tpl.Size),
            Tier = ShopEngine.ParseTier(tier.ToString()),
            Enchantment = Enum.Parse<EEnchantmentType>(enchName, ignoreCase: true),
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = handCore,
        };
        run.SyncEntryFromCoreCardAndDiff(handEntry, repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        int handVal = handEntry.Attributes.GetValueOrDefault(attrEnum, 0);
        int shelfVal = shelfEntry.Attributes.GetValueOrDefault(attrEnum, 0);
        Assert.True(handVal > 0);
        Assert.Equal(handVal, shelfVal);
    }

    private static RunEngine.Outcome? SelectAnyShelfCard(RunState run, RunEngine engine, string cid)
    {
        if (!run.Cards.TryGetValue(cid, out var ce)) return null;
        if (ce.Type == ECardType.Item)
        {
            var dest = FindFreeSlotForSize(run, ce.Size);
            if (!dest.HasValue) return null;
            int w = Math.Clamp((int)ce.Size, 1, 3);
            var sockets = new List<EContainerSocketId>(w);
            for (int k = 0; k < w; k++)
                sockets.Add((EContainerSocketId)((int)dest.Value.Socket + k));
            return engine.Handle(new SelectItemCommand
            {
                InstanceId = new InstanceId(cid),
                Section = dest.Value.Section,
                TargetSockets = sockets,
            });
        }
        if (ce.Type == ECardType.Skill)
            return engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(cid) });
        return engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(cid) });
    }

    private static (EInventorySection Section, EContainerSocketId Socket)? FindFreeSlotForSize(RunState run, ECardSize size)
    {
        int w = Math.Clamp((int)size, 1, 3);
        for (int s = 0; s <= 10 - w; s++)
        {
            bool ok = true;
            for (int k = 0; k < w; k++)
            {
                if ((run.UnlockedSlots & (1 << (s + k))) == 0 || run.HandGrid.GetCardAtSlot(s + k) != null)
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return (EInventorySection.Hand, (EContainerSocketId)s);
        }
        for (int s = 0; s <= 10 - w; s++)
        {
            bool ok = true;
            for (int k = 0; k < w; k++)
            {
                if (run.StashGrid.GetCardAtSlot(s + k) != null)
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return (EInventorySection.Stash, (EContainerSocketId)s);
        }
        return null;
    }
}
