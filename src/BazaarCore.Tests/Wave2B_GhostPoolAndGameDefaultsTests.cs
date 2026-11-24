using System.Reflection;
using System.Text.RegularExpressions;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Storage;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2B（D2-P1-10, P0-30, D6-P1-13/14/18, P0-32, D3-P1-06）：
/// 幽灵池 SQLite 分区列/6 类真实战力字段/技能与插槽校验/自定义 Day 1、BossRush 第 10 波终局与败场扣声望、
/// 非分页掉落与无尽奖金、DisableFates 声望独立与 GameDefaults 300 HP / 3 XP、战后安全钳制与 Skill 瞬态清理强断言测试。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave2B_GhostPoolAndGameDefaultsTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);
    private static readonly Lazy<List<BazaarServer.Data.MonsterTemplate>> Monsters =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.CreateMonstersCopy());
    private static readonly Lazy<LevelUpTable> LevelUps =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.LevelUps);
    private static readonly Lazy<GameModeConfig> Mode =
        new(() => TestInfrastructure.SharedGameDataFixture.Shared.Mode);

    private static string CreateTempDbPath(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bazaar_w2b_d_tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{tag}_{Guid.NewGuid():N}.db");
    }

    private static void TryDeleteTempDb(string dbPath)
    {
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        try { if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal"); } catch { }
        try { if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm"); } catch { }
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "BazaarServer")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("无法定位仓库根目录");
    }

    [Fact]
    public void D2_P1_10_P0_30_GhostPool_PartitionColumn_6CombatFields_SkillValidation_AndCustomDay1()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        string dbPath = CreateTempDbPath("ghost_pool_6fields");
        try
        {
            using var db = new Persistence(dbPath);

            // 1. P0-30 单一分区函数规范校验
            var stdRules = new CustomRunRuleSet();
            Assert.Equal("standard", PvpGhostPool.ResolveGhostPoolPartitionLocal(stdRules, hasTrueCustomCardInRun: false, modHost: null));

            var sandboxRules = new CustomRunRuleSet { SpecialModeId = ESpecialModeId.SandboxLab };
            Assert.Equal("none", PvpGhostPool.ResolveGhostPoolPartitionLocal(sandboxRules, hasTrueCustomCardInRun: false, modHost: null));

            // 游离自建卡无 Mod 哈希 → 返回 "none" 防污染公共池
            Assert.Equal("none", PvpGhostPool.ResolveGhostPoolPartitionLocal(stdRules, hasTrueCustomCardInRun: true, modHost: null));

            // 勾选局级社区 Mod → 返回 "mod:<hash12>"；叠加自定义规则返回 "mod:<hash12>+<rulePart>"
            var modStdRules = new CustomRunRuleSet { SelectedModIds = ["my-custom-mod"] };
            string modPart = PvpGhostPool.ResolveGhostPoolPartitionLocal(modStdRules, hasTrueCustomCardInRun: true, modHost: null);
            Assert.StartsWith("mod:", modPart);
            Assert.DoesNotContain("+", modPart);

            var modEndlessRules = new CustomRunRuleSet { SpecialModeId = ESpecialModeId.EndlessTower, SelectedModIds = ["my-custom-mod"] };
            string modEndlessPart = PvpGhostPool.ResolveGhostPoolPartitionLocal(modEndlessRules, hasTrueCustomCardInRun: true, modHost: null);
            Assert.StartsWith("mod:", modEndlessPart);
            Assert.EndsWith("+endless_tower", modEndlessPart);

            // 2. 录制含 6 类战力字段（PermanentAdd/Mult、CompletedQuests、QuestProgress、ExtraTags、SocketEffect、Skill 永久属性/标签）的自定义高血 Day 1 幽灵
            var run = new RunState
            {
                RunId = "w2bd_ghost_" + Guid.NewGuid().ToString("N")[..8],
                GameModeId = pool.DefaultGameModeId,
                Hero = EHero.Jules,
                Day = 1,
                Hour = 6,
                CustomRules = new CustomRunRuleSet { StartingHealth = 1000 }.CloneNormalized(),
            };
            run.SetBasePlayerAttribute(EPlayerAttributeType.HealthMax, 1000);
            run.SetBasePlayerAttribute(EPlayerAttributeType.Health, 1000);
            run.SetBasePlayerAttribute(EPlayerAttributeType.Level, 3);

            var questItemTpl = repo.CardsById.Values.FirstOrDefault(c =>
                c.Type == "Item" && c.Quests is { Count: > 0 } && Guid.TryParse(c.Id, out _))
                ?? repo.CardsById.Values.First(c => c.Type == "Item" && Guid.TryParse(c.Id, out _));
            string questId = "quest_w2b_done";
            var itemCore = new CardInstance(questItemTpl, CardTier.Silver, "Shielded", "itm_g_1") { StartSlot = 3 };
            itemCore.PermanentAdd["DamageAmount"] = 18;
            itemCore.PermanentMult["DamageAmount"] = 1.5;
            itemCore.QuestProgress[questId] = 5;
            itemCore.CompletedQuests.Add(questId);
            itemCore.ExtraTags.Add("Aquatic");
            run.Cards[itemCore.InstanceId] = new RunState.CardEntry
            {
                InstanceId = itemCore.InstanceId,
                TemplateId = Guid.Parse(questItemTpl.Id),
                Type = ECardType.Item,
                Size = ShopEngine.ParseSize(questItemTpl.Size),
                Tier = ETier.Silver,
                Enchantment = EEnchantmentType.Shielded,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_3,
                CoreCard = itemCore,
            };

            var skillTpl = repo.CardsById.Values.First(c => c.Type == "Skill" && Guid.TryParse(c.Id, out _));
            var skillCore = new CardInstance(skillTpl, CardTier.Gold, null, "skl_g_1");
            skillCore.PermanentAdd["Custom_0"] = 9;
            skillCore.PermanentMult["Custom_0"] = 2.0;
            skillCore.ExtraTags.Add("Tool");
            run.Cards[skillCore.InstanceId] = new RunState.CardEntry
            {
                InstanceId = skillCore.InstanceId,
                TemplateId = Guid.Parse(skillTpl.Id),
                Type = ECardType.Skill,
                Size = ECardSize.Small,
                Tier = ETier.Gold,
                Owner = ECombatantId.Player,
                CoreCard = skillCore,
            };

            var seTpl = repo.CardsById.Values.First(c => c.Type == "SocketEffect" && Guid.TryParse(c.Id, out _));
            var seCore = new CardInstance(seTpl, CardTier.Bronze, null, "se_g_1") { StartSlot = 4 };
            seCore.PermanentAdd["Custom_1"] = 4;
            run.Cards[seCore.InstanceId] = new RunState.CardEntry
            {
                InstanceId = seCore.InstanceId,
                TemplateId = Guid.Parse(seTpl.Id),
                Type = ECardType.SocketEffect,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_4,
                CoreCard = seCore,
            };

            var ghostPool = new PvpGhostPool();
            var recorded = ghostPool.RecordPlayerGhost(run, db, isCustomRule: true, poolPartition: "custom_highhp");
            Assert.NotNull(recorded);

            // SQLite 列强校验：pool_partition 精确落盘为 "custom_highhp"；LoadPvpGhosts("standard") 严禁含自定义行
            var customRows = db.LoadPvpGhosts(limit: 50, includeCustomRule: true, poolPartition: "custom_highhp");
            var dbRow = Assert.Single(customRows);
            Assert.True(dbRow.IsCustomRule);
            Assert.Equal("custom_highhp", dbRow.PoolPartition);

            // 尝试违规以 isCustomRule=true + partition="standard" 写入另一条独立 boardId，Persistence 强不变量必须纠偏为 "custom" 且绝不进入 standard 池
            string tamperedJson = dbRow.BoardJson.Replace(recorded!.BoardId, recorded.BoardId + "_tampered", StringComparison.Ordinal);
            db.InsertPvpGhost(dbRow with { BoardJson = tamperedJson, PoolPartition = "standard", IsCustomRule = true }, isCustomRule: true, poolPartition: "standard");
            var standardRows = db.LoadPvpGhosts(limit: 50, includeCustomRule: false, poolPartition: "standard");
            Assert.Empty(standardRows);
            var coercedCustomRows = db.LoadPvpGhosts(limit: 50, includeCustomRule: true, poolPartition: "custom");
            Assert.NotEmpty(coercedCustomRows);

            // 从 DB 重新加载进全新 PvpGhostPool，校验自定义高血 Day 1（HP=1000, Lv=3）可正常被同分区匹配（单样本时 50% 概率走同分区，多次采样必命中）
            var reloadedPool = new PvpGhostPool();
            Assert.True(reloadedPool.LoadFromDb(db, includeCustomRule: true) >= 1);
            PvpGhostPool.GhostBoard? matched = null;
            for (int seed = 1; seed <= 32; seed++)
            {
                var cand = reloadedPool.PickOpponent(1, 0, 0, new Random(seed), repo, includeCustomRuleGhosts: true, poolPartition: "custom_highhp");
                if (cand.BoardId == recorded!.BoardId)
                {
                    matched = cand;
                    break;
                }
            }
            Assert.NotNull(matched);
            Assert.Equal(recorded!.BoardId, matched!.BoardId);
            Assert.Equal(1000, matched.HealthMax);

            // ToPlayerState 100% 还原全部 6 类真实战力字段
            var restoredPs = PvpGhostPool.ToPlayerState(matched, repo);
            var restoredItem = Assert.Single(restoredPs.Hand.Cards);
            Assert.Equal(18.0, restoredItem.PermanentAdd["DamageAmount"], precision: 3);
            Assert.Equal(1.5, restoredItem.PermanentMult["DamageAmount"], precision: 3);
            Assert.Contains(questId, restoredItem.CompletedQuests);
            Assert.Equal(5.0, restoredItem.QuestProgress[questId], precision: 3);
            Assert.Contains("Aquatic", restoredItem.ExtraTags);

            var restoredSkill = Assert.Single(restoredPs.Skills);
            Assert.Equal(9.0, restoredSkill.PermanentAdd["Custom_0"], precision: 3);
            Assert.Equal(2.0, restoredSkill.PermanentMult["Custom_0"], precision: 3);
            Assert.Contains("Tool", restoredSkill.ExtraTags);

            var restoredSe = Assert.Single(restoredPs.SocketEffects);
            Assert.Equal(4, restoredSe.StartSlot);
            Assert.Equal(4.0, restoredSe.PermanentAdd["Custom_1"], precision: 3);

            // HasInvalidRepoItem 必须同时拦截未知 Skill 与未知 SocketEffect
            var invalidCheck = typeof(PvpGhostPool).GetMethod("HasInvalidRepoItem", BindingFlags.Static | BindingFlags.NonPublic)!;
            var badSkillBoard = new PvpGhostPool.GhostBoard
            {
                BoardId = "bad_sk",
                Items = [new PvpGhostPool.GhostItem { TemplateId = questItemTpl.Id }],
                Skills = [new PvpGhostPool.GhostSkill { TemplateId = "nonexistent_mod_skill_999" }],
            };
            Assert.True((bool)invalidCheck.Invoke(null, new object?[] { badSkillBoard, repo })!);

            var badSeBoard = new PvpGhostPool.GhostBoard
            {
                BoardId = "bad_se",
                Items = [new PvpGhostPool.GhostItem { TemplateId = questItemTpl.Id }],
                SocketEffects = [new PvpGhostPool.GhostSocketEffect { TemplateId = "nonexistent_mod_se_999" }],
            };
            Assert.True((bool)invalidCheck.Invoke(null, new object?[] { badSeBoard, repo })!);
        }
        finally
        {
            TryDeleteTempDb(dbPath);
        }
    }

    [Fact]
    public void D6_P1_14_BossRushWave10Defeat_NoPvpLossPrestige_UnifiedLoot_AndEndlessGoldMutation()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var mode = Mode.Value;

        // 1. BossRush 第 10 波赢 Boss 后输掉里程碑 PvP（声望仍 > 0）→ 绝不误判 CrownVictory
        var bossRules = new CustomRunRuleSet
        {
            SpecialModeId = ESpecialModeId.BossRush,
            BossRushTotalWaves = 10,
            BossRushIncludeMilestonePvp = true,
            MonsterLootBonusChoices = 1,
        }.CloneNormalized();
        var runBr = new RunState
        {
            RunId = "w2bd_br10",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 10,
            Hour = 6,
            BossRushWavesCleared = 10,
            State = ERunState.Choice,
            CustomRules = bossRules,
        };
        runBr.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, 10);
        var engineBr = new RunEngine(runBr, pool, Monsters.Value, LevelUps.Value, mode, repo, new PvpGhostPool());
        var tryEndCore = typeof(RunEngine).GetMethod("TryEndRunToEventsCore", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var loseEvents = new List<IGameSimEvent>();
        bool endedOnLose = (bool)tryEndCore.Invoke(engineBr, new object[] { loseEvents, true, false, false })!;
        Assert.False(endedOnLose, "BossRush 第 10 波里程碑 PvP 战败且声望 > 0 时绝不可判夺冠");
        Assert.NotEqual(ERunState.EndRunVictory, runBr.State);

        var winEvents = new List<IGameSimEvent>();
        bool endedOnWin = (bool)tryEndCore.Invoke(engineBr, new object[] { winEvents, true, false, true })!;
        Assert.True(endedOnWin);
        Assert.Equal(ERunState.EndRunVictory, runBr.State);

        // 2. 非分页掉落（MonsterLootChooseOne=false）同样获得 MonsterLootTierBonus + AllEnchanted + BossRush 叠加 MonsterLootBonusChoices
        var lootRules = new CustomRunRuleSet
        {
            SpecialModeId = ESpecialModeId.BossRush,
            MonsterLootChooseOne = false,
            MonsterLootTierBonus = 1,
            MonsterLootBonusChoices = 1,
            MutatorIds = [MutatorCatalog.AllEnchanted],
        }.CloneNormalized();
        var runLoot = new RunState
        {
            RunId = "w2bd_loot",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 4,
            Hour = 3,
            CustomRules = lootRules,
        };
        var shopLoot = new ShopEngine(runLoot, pool, repo, lootRules);
        var maxSelMethod = typeof(ShopEngine).GetMethod("ResolveConfiguredMonsterLootMaxSelections", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int brMaxSelections = (int)maxSelMethod.Invoke(shopLoot, null)!;
        // BossRush 基线 2 + MonsterLootBonusChoices(1) = 3
        Assert.True(brMaxSelections >= 3, $"BossRush 应叠加 MonsterLootBonusChoices，期望 >= 3 实际 {brMaxSelections}");

        // 找一只无显式 RewardVictory.SpawnContext 的常规野怪，验证非分页掉落也应用升阶与 AllEnchanted 附魔
        var plainMonster = Monsters.Value.First(m => m.Items.Count > 0);
        var psLoot = new PlayerState("p", "p", "Vanessa", 300);
        var lootEvs = shopLoot.DealMonsterVictoryLoot(null, plainMonster, psLoot, chooseOne: false);
        Assert.NotEmpty(lootEvs);
        var dealtItems = runLoot.SelectionSet
            .Select(id => runLoot.Cards[id])
            .Where(c => c.Type == ECardType.Item)
            .ToList();
        Assert.NotEmpty(dealtItems);
        Assert.All(dealtItems, it => Assert.NotNull(it.Enchantment));

        // 3. 无尽里程碑日（跨入 Day 15）：奖金经 MutatePlayerGold 驱动 Custom_3 累加
        var endlessRules = new CustomRunRuleSet
        {
            SpecialModeId = ESpecialModeId.EndlessTower,
            EndlessMaxDay = 50,
        }.CloneNormalized();
        var runEnd = new RunState
        {
            RunId = "w2bd_endless15",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 14,
            Hour = (uint)mode.HoursInADay,
            LastCompletedPvpDay = 14,
            State = ERunState.Choice,
            StartSelectionDone = true,
            CustomRules = endlessRules,
        };
        runEnd.SetBasePlayerAttribute(EPlayerAttributeType.Gold, 10);
        runEnd.SetBasePlayerAttribute(EPlayerAttributeType.Income, 0);
        var engineEnd = new RunEngine(runEnd, pool, Monsters.Value, LevelUps.Value, mode, repo, new PvpGhostPool());
        var advanceMethod = typeof(RunEngine).GetMethod("AdvanceHourOrDay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var advEvs = new List<IGameSimEvent>();
        advanceMethod.Invoke(engineEnd, new object[] { advEvs, true });
        Assert.Equal(15u, runEnd.Day);
        int expectedMilestoneGold = EndlessTowerScaler.Compute(15, endlessRules.EndlessMaxDay).MilestoneBonusGold;
        Assert.True(expectedMilestoneGold > 0);
        Assert.True(runEnd.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0) >= expectedMilestoneGold,
            "无尽里程碑奖金必须走 MutatePlayerGold 累加 Custom_3 追踪器");
    }

    [Fact]
    public void D6_P1_13_D6_P1_18_DisableFatesPrestigePreserved_GameDefaults300And3Xp_Zero250Drift()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;

        // 1. D6-P1-13：StartingPrestige=50 + DisableFatesRevive=true（无 IronmanNoFates）→ 初始声望保持 50
        var noFatesRules = new CustomRunRuleSet
        {
            StartingPrestige = 50,
            DisableFatesRevive = true,
        }.CloneNormalized();
        var runNoFates = new RunState
        {
            RunId = "w2bd_nofates",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            CustomRules = noFatesRules,
        };
        var engineNoFates = new RunEngine(runNoFates, pool, Monsters.Value, LevelUps.Value, Mode.Value, repo, new PvpGhostPool());
        engineNoFates.StartRun();
        Assert.Equal(50, runNoFates.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.True(runNoFates.FatesUsed);

        // 仅 IronmanNoFates 才截断声望 <= 10
        var ironRules = new CustomRunRuleSet
        {
            StartingPrestige = 50,
            MutatorIds = [MutatorCatalog.IronmanNoFates],
        }.CloneNormalized();
        var runIron = new RunState
        {
            RunId = "w2bd_iron",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            CustomRules = ironRules,
        };
        var engineIron = new RunEngine(runIron, pool, Monsters.Value, LevelUps.Value, Mode.Value, repo, new PvpGhostPool());
        engineIron.StartRun();
        Assert.Equal(10, runIron.PlayerAttributes[EPlayerAttributeType.Prestige]);

        // 2. D6-P1-18：DB GameModeData.StartingExperience=0 时，FromGameModeData 归一为 GameDefaults.StartingExperience (3)
        var modeFromZeroDb = GameModeConfig.FromGameModeData(new GameModeData { StartingExperience = 0 });
        Assert.Equal(GameDefaults.StartingExperience, modeFromZeroDb.StartingExperience);
        Assert.Equal(GameDefaults.StartingHealth, modeFromZeroDb.StartingHealth);
        Assert.Equal(GameDefaults.StartingGold, modeFromZeroDb.StartingGold);
        Assert.Equal(GameDefaults.StartingIncome, modeFromZeroDb.StartingIncome);
        Assert.Equal(GameDefaults.StartingPrestige, modeFromZeroDb.PrestigeInitial);

        // 默认规则开局精确得 3 XP / 300 HP / 15 Gold / 5 Income / 20 Prestige（且自定义 StartingExperience=0 仍精确得 0 XP）
        var runDef = new RunState
        {
            RunId = "w2bd_def",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            CustomRules = new CustomRunRuleSet(),
        };
        var engDef = new RunEngine(runDef, pool, Monsters.Value, LevelUps.Value, modeFromZeroDb, repo, new PvpGhostPool());
        var applyStart = typeof(RunEngine).GetMethod("ApplyStartingStats", BindingFlags.Instance | BindingFlags.NonPublic)!;
        applyStart.Invoke(engDef, new object[] { runDef.CustomRules, new List<IGameSimEvent>() });
        Assert.Equal(300, runDef.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(15, runDef.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(5, runDef.PlayerAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(3, runDef.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(20, runDef.PlayerAttributes[EPlayerAttributeType.Prestige]);

        var runZeroXp = new RunState
        {
            RunId = "w2bd_0xp",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            CustomRules = new CustomRunRuleSet { StartingExperience = 0 },
        };
        var engZeroXp = new RunEngine(runZeroXp, pool, Monsters.Value, LevelUps.Value, modeFromZeroDb, repo, new PvpGhostPool());
        applyStart.Invoke(engZeroXp, new object[] { runZeroXp.CustomRules, new List<IGameSimEvent>() });
        Assert.Equal(0, runZeroXp.PlayerAttributes[EPlayerAttributeType.Experience]);

        // 应急幽灵 Day 1 血量 >= 300
        var buildEmergency = typeof(RunEngine).GetMethod("BuildEmergencyFallbackGhost", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var emergencyGhost = (PvpGhostPool.GhostBoard)buildEmergency.Invoke(engDef, new object[] { 1, "standard", new Random(42) })!;
        Assert.True(emergencyGhost.HealthMax >= GameDefaults.StartingHealth);

        // 静态扫描本轨道服务端文件：0 处残余 250 HP 默认值兜底
        string root = LocateRepoRoot();
        string[] checkedFiles =
        [
            "src/BazaarServer/Data/GameModeConfig.cs",
            "src/BazaarServer/Data/PvpGhostPool.cs",
            "src/BazaarServer/Game/RunState.cs",
            "src/BazaarServer/Game/RunEngine.cs",
            "src/BazaarServer/Game/ShopEngine.cs",
            "src/BazaarServer/Game/EncounterStepEngine.cs",
        ];
        var driftPattern = new Regex(@"(HealthMax\s*,\s*250\b|StartingHealth\s*>\s*0\s*\?\s*[^:]+:\s*250\b|HealthMax\s*\{\s*get;\s*init;\s*\}\s*=\s*250\b|GetInt\([^)]*""healthMax""\s*,\s*250\b)", RegexOptions.CultureInvariant);
        foreach (string rel in checkedFiles)
        {
            string txt = File.ReadAllText(Path.Combine(root, rel));
            Assert.False(driftPattern.IsMatch(txt), $"文件 {rel} 仍存在残余 250 HP 兜底漂移");
        }
    }

    [Fact]
    public void P0_32_D3_P1_06_PostCombatSafeClamp_NoZeroCdCorruption_AndSkillTransientCleanup()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var run = new RunState
        {
            RunId = "w2bd_post_combat",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 3,
            Hour = 3,
            State = ERunState.Combat,
        };
        var engine = new RunEngine(run, pool, Monsters.Value, LevelUps.Value, Mode.Value, repo, new PvpGhostPool());

        // 1. P0-32：0 CD 被动卡不被 Add/Multiply 强拉 1000ms；主动卡 CooldownMax / PCR / Multicast / Multiply 全部精准钳制
        var passiveTpl = repo.CardsById.Values.First(c =>
            c.Type == "Item" &&
            Guid.TryParse(c.Id, out _) &&
            (c.Attributes == null || !c.Attributes.TryGetValue("CooldownMax", out double cd) || cd <= 0) &&
            (c.Tiers == null || !c.Tiers.TryGetValue("Bronze", out var td) || td.Attributes == null || !td.Attributes.TryGetValue("CooldownMax", out double tcd) || tcd <= 0));
        var activeTpl = repo.CardsById.Values.First(c =>
            c.Type == "Item" &&
            Guid.TryParse(c.Id, out _) &&
            c.Tiers != null && c.Tiers.TryGetValue("Bronze", out var td) &&
            td.Attributes != null && td.Attributes.TryGetValue("CooldownMax", out double tcd) && tcd >= 3000);
        var skillTpl = repo.CardsById.Values.First(c => c.Type == "Skill" && Guid.TryParse(c.Id, out _));

        var passiveCore = new CardInstance(passiveTpl, CardTier.Bronze, null, "itm_passive_0cd");
        var activeCore = new CardInstance(activeTpl, CardTier.Bronze, null, "itm_active_clamp");
        var skillCore = new CardInstance(skillTpl, CardTier.Bronze, null, "skl_post_clean");
        skillCore.FreezeMs = 2000;
        skillCore.CombatAdd["Custom_0"] = 99;
        skillCore.PermanentAdd["Custom_0"] = 3;

        run.HandGrid.TryPlaceOrShift(passiveCore, 3);
        run.HandGrid.TryPlaceOrShift(activeCore, 4);
        run.Cards[passiveCore.InstanceId] = new RunState.CardEntry
        {
            InstanceId = passiveCore.InstanceId,
            TemplateId = Guid.Parse(passiveTpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(passiveTpl.Size),
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = passiveCore,
        };
        run.Cards[activeCore.InstanceId] = new RunState.CardEntry
        {
            InstanceId = activeCore.InstanceId,
            TemplateId = Guid.Parse(activeTpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(activeTpl.Size),
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_4,
            CoreCard = activeCore,
        };
        var skillEntry = new RunState.CardEntry
        {
            InstanceId = skillCore.InstanceId,
            TemplateId = Guid.Parse(skillTpl.Id),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            CoreCard = skillCore,
        };
        skillEntry.Attributes[ECardAttributeType.Custom_0] = 102; // 模拟战中残留值
        skillEntry.Attributes[ECardAttributeType.Freeze] = 2000;
        run.Cards[skillEntry.InstanceId] = skillEntry;

        var buildPs = typeof(RunEngine).GetMethod("BuildCorePlayerFromRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var p1Core = (PlayerState)buildPs.Invoke(engine, null)!;

        var ops = new List<PostCombatMutationOp>
        {
            new() { OpKind = "ModifyCardPermanentAttribute", CardInstanceId = passiveCore.InstanceId, Attribute = "CooldownMax", Operation = "Add", Value = -500, GrantIfMissing = false, SourceMechanicId = "test_mod", Reason = "test" },
            new() { OpKind = "ModifyCardPermanentAttribute", CardInstanceId = activeCore.InstanceId, Attribute = "CooldownMax", Operation = "Multiply", Value = 0.0001, GrantIfMissing = false, SourceMechanicId = "test_mod", Reason = "test" },
            new() { OpKind = "ModifyCardPermanentAttribute", CardInstanceId = activeCore.InstanceId, Attribute = "PercentCooldownReduction", Operation = "Add", Value = 999, GrantIfMissing = false, SourceMechanicId = "test_mod", Reason = "test" },
            new() { OpKind = "ModifyCardPermanentAttribute", CardInstanceId = activeCore.InstanceId, Attribute = "Multicast", Operation = "Add", Value = 50, GrantIfMissing = false, SourceMechanicId = "test_mod", Reason = "test" },
        };

        var applyOps = typeof(RunEngine).GetMethod("ApplyPostCombatMechanicOps", BindingFlags.Instance | BindingFlags.NonPublic)!;
        applyOps.Invoke(engine, new object?[] { p1Core, ops, new List<IGameSimEvent>(), null });

        Assert.Equal(0.0, passiveCore.GetAttribute("CooldownMax"), precision: 3);
        Assert.InRange(activeCore.GetAttribute("CooldownMax"), 1000.0, 60000.0);
        Assert.InRange(activeCore.GetAttribute("PercentCooldownReduction"), -200.0, 90.0);
        Assert.InRange(activeCore.GetAttribute("Multicast"), 1.0, 10.0);

        // 2. D3-P1-06：ClearPostCombatTransientStatusAndSync 放宽至 ECardType.Skill，清理瞬态并强刷 Custom_0 回局外权威基线（Base + Permanent），且不给 Skill 误写 Ammo/Flying
        int expectedSkillCustom0 = (int)Math.Round(skillCore.BaseAttributes.GetValueOrDefault("Custom_0", 0) + 3);
        var clearMethod = typeof(RunEngine).GetMethod("ClearPostCombatTransientStatusAndSync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dirtyIds = new HashSet<string>(StringComparer.Ordinal);
        clearMethod.Invoke(engine, new object?[] { new List<IGameSimEvent>(), dirtyIds });

        Assert.Equal(0, skillCore.FreezeMs);
        Assert.Equal(0, skillEntry.Attributes[ECardAttributeType.Freeze]);
        Assert.Equal(expectedSkillCustom0, skillEntry.Attributes[ECardAttributeType.Custom_0]);
        Assert.Equal(expectedSkillCustom0, skillEntry.PendingForceSyncAttributes[ECardAttributeType.Custom_0]);
        Assert.Contains(skillEntry.InstanceId, dirtyIds);
        Assert.False(skillEntry.PendingForceSyncAttributes.ContainsKey(ECardAttributeType.Ammo));
        Assert.False(skillEntry.PendingForceSyncAttributes.ContainsKey(ECardAttributeType.Flying));
    }
}
