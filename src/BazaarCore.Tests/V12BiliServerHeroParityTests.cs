using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BazaarCore.Combat;
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
/// B站 50 问 B 组（Q25–Q43, Q45–Q47）核验与修复专项服务端/核心回归测试套件。
/// 覆盖：Trip Him Up 经验升级与 Dock Lines 减 CD、Sprout/Tiny 货架尺寸、Jungle Ruins/Ticket、Salvage Yard、
/// 出售附魔收益、Curio/商户防双折、2 级跨职业技能池隔离、技能去重与野怪同/高阶技能升级、
/// UntilEndOfDay 清理事件同步、随机附魔排除当前、Jules 开局/Brolin 非相邻/双跨冰火插座、以及长袍怪商 30g 宝箱与 Waystones。
/// </summary>
public sealed class V12BiliServerHeroParityTests
{
    private const string WhereAmIId = "875caea4-93c5-440b-a9fd-2886eeaa0fc5";
    private const string TripHimUpStepId = "511b1847-fbec-449e-9403-5dc1cbc4fc95";
    private const string DockLinesId = "9ede03ef-5257-4a77-a725-5c4713662208";
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string SproutId = "5e1ac00b-1abc-44cb-8f9c-6755327580c5";
    private const string JungleRuinsId = "1e511488-af8e-4852-a46a-0f27a2b07aac";
    private const string GrabTheLootStepId = "e73e7956-1875-4f40-b5f2-d3c4ed975751";
    private const string TinyWorldTicketId = "24dc340b-bb3d-4c9b-8715-a807c0c06a8c";
    private const string TinyWorldExpeditionId = "42db3205-58d5-4cfe-b537-14091b518ff6";
    private const string SalvageYardId = "a79a4688-752d-4b4f-90b3-c92b3a4d1a92";
    private const string EagleTalismanId = "0022c409-c839-41e8-8022-65a407457dfe";
    private const string CurioId = "c6cffe25-8e94-4a7b-a821-3e3c7093333b";
    private const string PolShopId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string KarnokMeatId = "19e85fa6-95a1-46bd-9ac9-37ad71861475";
    private const string KeenEyeSkillId = "b49dd22b-ef95-4f7e-954e-556e1e431d4a";
    private const string StoveSocketId = "c017c0dd-af3c-47e0-b510-3661d9dcd7f2";
    private const string CoolerSocketId = "d5515ae3-b98d-4272-8add-82f441969ffe";
    private const string ChefBrolinId = "7a4f9be7-9132-45a2-9885-6f2ce40a59be";
    private const string AddStoveStepId = "b8252f75-a642-4db9-9c8a-2870bf77ae47";
    private const string AddCoolerStepId = "90f42a83-a3db-47dc-8b0a-6f62bffa62d5";
    private const string FeastId = "fcfcd84c-8f62-43ae-9cbb-31d3110e4696";
    private const string ShroudedFigureId = "818fcd1d-4cfd-4629-b83f-8d5e753dd8f4";
    private const string MysteriousChestStepId = "9729a91f-a43b-43bd-b5d2-0ea3e177b758";
    private const string MysteriousChestEventId = "56eb7fa9-5a5f-4056-8e75-ec2a5a5c3433";
    private const string TourScheduleId = "b4e92058-ea77-43a6-af6b-858fd73e5750";
    private const string WaystonesId = "de82424c-91e7-41c1-8ed5-edb7408cfad3";

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
            DataDir = dataDir,
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

    private sealed class OverlayFixture : IDisposable
    {
        public string TempDir { get; }
        public string DbPath { get; }
        public CardPool Pool { get; }
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; }
        public LevelUpTable LevelUps { get; }
        public GameModeConfig Mode { get; }
        public GameDataRepository Repo { get; }
        public PvpGhostPool Ghosts { get; }

        public OverlayFixture(
            (string Id, string Json)[] customCards,
            params (int Level, string Json)[] customLevelUps)
        {
            var real = RealFixture.Value;
            TempDir = Path.Combine(Path.GetTempPath(), "bazaar_v12_bili_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);
            DbPath = Path.Combine(TempDir, "GameData.db");
            File.Copy(real.DbPath, DbPath, overwrite: true);

            var wl = new HashSet<string>(real.ValidCardIds, StringComparer.OrdinalIgnoreCase);
            using (var conn = new SqliteConnection($"Data Source={DbPath}"))
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

                foreach (var (lvl, json) in customLevelUps)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT OR REPLACE INTO level_ups (Id, Data) VALUES ($id, $data)";
                    cmd.Parameters.AddWithValue("$id", lvl);
                    cmd.Parameters.AddWithValue("$data", Encoding.UTF8.GetBytes(json));
                    cmd.ExecuteNonQuery();
                }
            }

            File.WriteAllText(
                Path.Combine(TempDir, "valid_card_ids.json"),
                JsonSerializer.Serialize(new { valid_ids = wl.ToArray() }));

            Pool = CardPool.Load(DbPath);
            Monsters = real.Monsters;
            LevelUps = LevelUpTable.Load(DbPath);
            Mode = real.Mode;
            Repo = GameDatabaseLoader.Load(DbPath, null);
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

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        Fx fx,
        EHero hero = EHero.Vanessa,
        int seed = 4201)
    {
        return CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, hero, seed);
    }

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        CardPool pool,
        List<BazaarServer.Data.MonsterTemplate> monsters,
        LevelUpTable levelUps,
        GameModeConfig mode,
        GameDataRepository repo,
        PvpGhostPool ghosts,
        EHero hero = EHero.Vanessa,
        int seed = 4201)
    {
        var run = new RunState
        {
            RunId = "bili_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = pool.DefaultGameModeId != Guid.Empty ? pool.DefaultGameModeId : Guid.NewGuid(),
            Hero = hero,
            PlayMode = EPlayMode.Ranked,
            RngSeed = seed,
        };
        var engine = new RunEngine(run, pool, monsters, levelUps, mode, repo, ghosts);
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);
        return (run, engine);
    }

    private static RunState.CardEntry InjectChoiceEncounter(RunState run, CardPool pool, string encounterId)
    {
        var tpl = pool.Get(encounterId);
        string instId = "inj_" + Guid.NewGuid().ToString("N")[..8];
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(encounterId),
            Type = tpl != null ? ShopEngine.ParseCardType(tpl.Type) : ECardType.EventEncounter,
            Size = tpl != null ? ShopEngine.ParseSize(tpl.Size) : ECardSize.Medium,
            Tier = tpl != null ? ShopEngine.ParseTier(tpl.StartingTier) : ETier.Bronze,
        };
        run.State = ERunState.Choice;
        run.CurrentEncounterId = null;
        run.SelectionSet.Clear();
        run.Cards[instId] = entry;
        run.SelectionSet.Add(instId);
        return entry;
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        GameDataRepository repo,
        string templateId,
        EInventorySection section,
        int slot,
        ETier tier = ETier.Bronze,
        string? enchantment = null)
    {
        var coreTpl = repo.GetCard(templateId)!;
        var core = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), enchantment, $"itm_{Guid.NewGuid():N}")
        {
            StartSlot = slot,
        };
        var grid = section == EInventorySection.Stash ? run.StashGrid : run.HandGrid;
        grid.UnlockedMask = section == EInventorySection.Stash ? 1023 : run.UnlockedSlots;
        Assert.True(grid.TryPlaceOrShift(core, slot), $"向 {section}:{slot} 添加物品 {templateId} 失败");

        EEnchantmentType? parsedEnch = !string.IsNullOrEmpty(enchantment) && Enum.TryParse<EEnchantmentType>(enchantment, true, out var ep)
            ? ep
            : null;
        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Enchantment = parsedEnch,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)slot,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, null, coreTpl);
        run.SyncAllEntriesFromCoreCardsAndDiff(repo.GameMode.StandardPrices, [entry]);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static RunState.CardEntry AddOwnedSkill(
        RunState run,
        GameDataRepository repo,
        string templateId,
        ETier tier)
    {
        var coreTpl = repo.GetCard(templateId)!;
        var core = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), null, $"skl_{Guid.NewGuid():N}");
        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = tier,
            Owner = ECombatantId.Player,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, null, coreTpl);
        run.SyncAllEntriesFromCoreCardsAndDiff(repo.GameMode.StandardPrices, [entry]);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    /// <summary>Q25：Dock Lines 解锁隐藏步骤 Trip Him Up（511b1847），实得 +2 XP 跨 8 XP 阈值升级并令 Dock Lines CD -1s。</summary>
    [Fact]
    public void Q25_TinyWorldTripHimUp_GrantsXpTriggersLevelUpAndFlatCooldownReduction()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx, EHero.Vanessa, seed: 2501);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        // 预设 7 XP（距离 ExperiencePerLevel=8 仅差 1 XP），持有 Dock Lines
        run.PlayerAttributes[EPlayerAttributeType.Experience] = 7;
        var dockLines = AddOwnedItem(run, fx.Repo, DockLinesId, EInventorySection.Hand, 0, ETier.Silver);
        int baseCdMs = (int)Math.Round(dockLines.CoreCard!.GetAttribute("CooldownMax"));
        Assert.True(baseCdMs > 1000);

        var whereOpt = InjectChoiceEncounter(run, fx.Pool, WhereAmIId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(whereOpt.InstanceId) }).Error);

        var tripStep = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId.ToString().Equals(TripHimUpStepId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(tripStep);

        var stepOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(tripStep!.InstanceId) });
        Assert.Null(stepOut.Error);

        // 1. 验证 +2 XP 经 grantExperience 跨 8 XP 阈值升至 Lv2（7 + 2 = 9 -> Lv2, Exp=1），并正常推入 LevelUp 状态
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Contains(stepOut.Events, e => e is GameSimEventPlayerExperienceGained xp && xp.Amount == 2);
        Assert.Contains(stepOut.Events, e => e is GameSimEventStateTransitioned st && st.ToState == ERunState.LevelUp);

        // 2. 验证 DB 动作 Ab[1] 为 Dock Lines 注入 FlatCooldownReduction +1000，有效冷却精确 -1000ms
        Assert.Equal(1000, (int)Math.Round(dockLines.CoreCard.GetAttribute("FlatCooldownReduction")));
        int reducedCd = (int)Math.Round(dockLines.CoreCard.GetAttribute("CooldownMax"));
        Assert.Equal(Math.Max(1000, baseCdMs - 1000), reducedCd);
    }

    /// <summary>Q26：核验当前 DB Sprout 不再对物品注入 Tiny，且带有 Tiny=1 的货架物品正确映射缩小尺寸。</summary>
    [Fact]
    public void Q26_SproutCurrentDbAndTinyShelfEffectiveSizeParity()
    {
        var fx = RealFixture.Value;
        var sproutTpl = fx.Repo.GetCard(SproutId)!;
        var sproutJson = JsonSerializer.Serialize(sproutTpl);
        Assert.DoesNotContain("Tiny", sproutJson, StringComparison.OrdinalIgnoreCase);

        var (run, engine) = CreateStartedEngine(fx, EHero.Vanessa, seed: 2601);
        var sproutOpt = InjectChoiceEncounter(run, fx.Pool, SproutId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(sproutOpt.InstanceId) }).Error);

        Assert.NotEmpty(run.SelectionSet);
        Assert.All(run.SelectionSet, id =>
        {
            var entry = run.Cards[id];
            Assert.Equal(ETier.Diamond, entry.Tier);
            Assert.False(entry.CoreCard?.IsTiny ?? false);
        });

        // 同时验证若卡片实例携带 Attributes["Tiny"] = 1，ShopEngine.CreateEntryFromCoreInstance 货架尺寸必须跟随 EffectiveSize 缩小
        var feastTpl = fx.Repo.GetCard(FeastId)!; // Large
        var tinyFeast = new CardInstance(feastTpl, CardTier.Gold);
        tinyFeast.BaseAttributes["Tiny"] = 1;
        Assert.True(tinyFeast.IsTiny);
        Assert.Equal(CardSize.Medium, tinyFeast.EffectiveSize);

        var shop = new ShopEngine(run, fx.Pool, fx.Repo);
        var shelfEntry = shop.CreateEntryFromCoreInstance(tinyFeast, isFree: true);
        Assert.Equal(ECardSize.Medium, shelfEntry.Size);
    }

    /// <summary>Q27：Jungle Ruins 掉落 Tiny World Ticket，第 10 天第 1 小时开启第 4 格入口并在进入后消耗门票。</summary>
    [Fact]
    public void Q27_JungleRuinsAndTinyWorldTicket_OpensDay10FourthSlotAndConsumes()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx, EHero.Vanessa, seed: 2701);

        // 验证 DB Grab the Loot 含 Tiny World Ticket
        var grabTpl = fx.Repo.GetCard(GrabTheLootStepId)!;
        Assert.Contains(TinyWorldTicketId, JsonSerializer.Serialize(grabTpl), StringComparison.OrdinalIgnoreCase);

        AddOwnedItem(run, fx.Repo, TinyWorldTicketId, EInventorySection.Stash, 0, ETier.Legendary);
        run.Day = 10;
        run.Hour = 1;
        run.State = ERunState.Choice;
        var shop = new ShopEngine(run, fx.Pool, fx.Repo);
        shop.DealHourEncounters(null, run.VisitedDayEncounterIds);

        var expOpt = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId.ToString().Equals(TinyWorldExpeditionId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(expOpt);

        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(expOpt!.InstanceId) }).Error);
        Assert.DoesNotContain(run.Cards.Values, c =>
            c.Owner == ECombatantId.Player &&
            c.TemplateId.ToString().Equals(TinyWorldTicketId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Q28：Salvage Yard 对 0 金低价非 Loot 物品正确触发并生成 1 件替换物，对 Loot 物品触发 0 次。</summary>
    [Fact]
    public void Q28_SalvageYard_LowPriceNonLootSpawnsOneAndLootSpawnsZero()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx, EHero.Vanessa, seed: 2801);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        var nonLoot = AddOwnedItem(run, fx.Repo, CutlassId, EInventorySection.Hand, 0, ETier.Bronze);
        nonLoot.Attributes[ECardAttributeType.SellPrice] = 0;
        nonLoot.CoreCard!.BaseAttributes["SellPrice"] = 0;

        var lootItem = AddOwnedItem(run, fx.Repo, EagleTalismanId, EInventorySection.Hand, 4, ETier.Silver);

        var syOpt = InjectChoiceEncounter(run, fx.Pool, SalvageYardId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(syOpt.InstanceId) }).Error);

        int countBefore = run.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item);
        Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(nonLoot.InstanceId) }).Error);
        int countAfterNonLoot = run.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item);
        // 卖掉 1 件 Medium Bronze 非 Loot 武器（Cutlass），DB Ab[479] Limit=2 生成 2 件磨刀石：2 - 1 + 2 = 3
        Assert.Equal(countBefore + 1, countAfterNonLoot);

        // 卖掉 Loot 标签物品不应触发 Salvage Yard 生成新卡：3 - 1 = 2
        Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(lootItem.InstanceId) }).Error);
        int countAfterLoot = run.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item);
        Assert.Equal(countBefore, countAfterLoot);
    }

    /// <summary>Q32：Golden 出售翻倍，且 Obsidian/Restorative/Shielded 附魔出售时直读 DB TTriggerOnCardSold 加成到手牌对应物品。</summary>
    [Fact]
    public void Q32_SellEnchantments_GoldenAndObsidianRestorativeShieldedTriggerDbEffects()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx, EHero.Vanessa, seed: 3201);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        // Golden Cutlass -> 通过换位触发一次局外光环计算，确认 Golden SellPrice*=2 生效并入账翻倍
        var plainCutlass = AddOwnedItem(run, fx.Repo, CutlassId, EInventorySection.Stash, 0, ETier.Silver);
        var goldenCutlass = AddOwnedItem(run, fx.Repo, CutlassId, EInventorySection.Stash, 2, ETier.Silver, "Golden");
        Assert.Null(engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(goldenCutlass.InstanceId),
            Section = EInventorySection.Stash,
            TargetSockets = [EContainerSocketId.Socket_4, EContainerSocketId.Socket_5]
        }).Error);
        int plainSell = plainCutlass.Attributes[ECardAttributeType.SellPrice];
        int goldenSell = goldenCutlass.Attributes[ECardAttributeType.SellPrice];
        Assert.Equal(plainSell * 2, goldenSell);

        int goldBefore = run.PlayerAttributes[EPlayerAttributeType.Gold];
        Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(goldenCutlass.InstanceId) }).Error);
        Assert.Equal(goldBefore + goldenSell, run.PlayerAttributes[EPlayerAttributeType.Gold]);

        // 将 plainCutlass 移入 Hand，再卖出 Obsidian Eagle Talisman（Obsidian 附魔携带 TTriggerOnCardSold 为手牌武器加 DamageAmount）
        Assert.Null(engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(plainCutlass.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = [EContainerSocketId.Socket_0, EContainerSocketId.Socket_1]
        }).Error);

        int dmgBefore = (int)Math.Round(plainCutlass.CoreCard!.GetAttribute("DamageAmount"));
        var obsTalisman = AddOwnedItem(run, fx.Repo, EagleTalismanId, EInventorySection.Stash, 4, ETier.Silver, "Obsidian");
        Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(obsTalisman.InstanceId) }).Error);

        int dmgAfter = (int)Math.Round(plainCutlass.CoreCard.GetAttribute("DamageAmount"));
        Assert.True(dmgAfter > dmgBefore, $"Obsidian 出售应触发 DB TTriggerOnCardSold 加伤: before={dmgBefore}, after={dmgAfter}");
    }

    /// <summary>Q33：Curio 公共青铜池与 1 次 3 金刷新准确无误，且 RunEngine 下任何商人不再叠加 0.75×0.75=0.5625 双重折扣。</summary>
    [Fact]
    public void Q33_CurioAndMerchants_NoDoubleDiscountStacking()
    {
        var fx = RealFixture.Value;

        // 1. 验证 Curio 池与重抽规则
        var (curioRun, curioEngine) = CreateStartedEngine(fx, EHero.Vanessa, seed: 3301);
        var curioOpt = InjectChoiceEncounter(curioRun, fx.Pool, CurioId);
        Assert.Null(curioEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(curioOpt.InstanceId) }).Error);
        Assert.Equal(1u, curioRun.RerollsRemaining);
        Assert.Equal(3u, curioRun.RerollCost);
        Assert.NotEmpty(curioRun.SelectionSet);
        Assert.All(curioRun.SelectionSet, id =>
        {
            var e = curioRun.Cards[id];
            Assert.Equal(ETier.Bronze, e.Tier);
            Assert.Contains("Common", e.CoreCard!.Template.Heroes, StringComparer.OrdinalIgnoreCase);
        });

        // 2. 跨多种子采样 Curio / Pol 商店，验证不存在任何低于 0.75x 原价的 0.5625x 双折叠加
        var prices = fx.Repo.GameMode.StandardPrices;
        for (int seed = 3310; seed < 3330; seed++)
        {
            var (r, eng) = CreateStartedEngine(fx, EHero.Vanessa, seed);
            string encId = seed % 2 == 0 ? CurioId : PolShopId;
            var opt = InjectChoiceEncounter(r, fx.Pool, encId);
            Assert.Null(eng.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(opt.InstanceId) }).Error);

            foreach (string selId in r.SelectionSet)
            {
                var ce = r.Cards[selId];
                if (ce.Type != ECardType.Item || ce.CoreCard == null) continue;
                int stdBuy = prices.GetItemBuyPrice(ce.CoreCard.Template.CardSize, ce.CoreCard.Tier);
                if (stdBuy <= 0) continue;
                int actualBuy = ce.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, stdBuy);
                int minAllowed75 = ShopEngine.ComputeDiscountedBuyPrice(stdBuy, 25);
                Assert.True(actualBuy >= minAllowed75,
                    $"商人 {encId} (seed={seed}) 单品 {ce.CoreCard.Template.InternalName} 出现双折叠加: std={stdBuy}, actual={actualBuy}, min75={minAllowed75}");
            }
        }
    }

    /// <summary>Q37：携带跨职业物品（Karnok Meat）升 2 级时，标准技能池与 TSpawnBehaviorIncludeCommonAndHero 严格只允许当前英雄与 Common。</summary>
    [Fact]
    public void Q37_Level2SkillPool_StrictlyMatchesPlayerHeroPlusCommon()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx, EHero.Vanessa, seed: 3701);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        // 给 Vanessa 装备跨英雄 Karnok 小件，升至 Lv2 并退出任意普通遭遇进入 LevelUp 发牌界面
        AddOwnedItem(run, fx.Repo, KarnokMeatId, EInventorySection.Hand, 0, ETier.Bronze);
        var expOut = engine.Handle(new CheatCommand { Args = new List<string> { "exp", "8" } });
        Assert.Null(expOut.Error);
        var curioOpt = InjectChoiceEncounter(run, fx.Pool, CurioId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(curioOpt.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(ERunState.LevelUp, run.State);

        var dealtIds = run.SelectionSet.ToList();
        Assert.NotEmpty(dealtIds);
        foreach (string id in dealtIds)
        {
            var tpl = fx.Repo.GetCard(run.Cards[id].TemplateId.ToString())!;
            if (string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase))
            {
                bool heroOk = tpl.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase)
                           || tpl.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase);
                Assert.True(heroOk, $"Lv2 升级池不应包含非 Vanessa/Common 技能: {tpl.InternalName} ({string.Join(',', tpl.Heroes)})");
            }
        }

        // 在 OverlayFixture 的 level_ups 表插入带 TSpawnBehaviorIncludeCommonAndHero 的测试组（DB Id=1 对应 targetNewLevel=2），
        // 直接验证修复后的 LevelUpTable.ResolveGroupCandidates 严格放行 Vanessa 技能、拦截 Karnok 专属技能
        const string KarnokOnlySkill = "20000002-0000-4000-8000-000000000037";
        const string VanessaSkill = "20000003-0000-4000-8000-000000000037";
        string kSkillJson = """{"$type":"TCardSkill","Id":"20000002-0000-4000-8000-000000000037","Type":"Skill","InternalName":"TestKarnokOnlySkill","StartingTier":"Bronze","Size":"Small","Heroes":["Karnok"],"Tiers":{"Bronze":{"Attributes":{},"AbilityIds":[],"AuraIds":[]}}}""";
        string vSkillJson = """{"$type":"TCardSkill","Id":"20000003-0000-4000-8000-000000000037","Type":"Skill","InternalName":"TestVanessaSkill","StartingTier":"Bronze","Size":"Small","Heroes":["Vanessa"],"Tiers":{"Bronze":{"Attributes":{},"AbilityIds":[],"AuraIds":[]}}}""";
        string lvl1Json = """{"Level":1,"HealthIncrease":100,"Rewards":{"$type":"TSpawnContextQuery","SelectionMethod":"Random","Limit":{"$type":"TFixedValue","Value":3.0},"Groups":[{"SelectionMethod":"Random","Limit":{"$type":"TFixedValue","Value":3.0},"RandomWeight":10,"Filters":[{"$type":"TSpawnFilterIdList","Ids":["20000002-0000-4000-8000-000000000037","20000003-0000-4000-8000-000000000037"]}],"Behaviors":[{"$type":"TSpawnBehaviorIncludeCommonAndHero"}]}]}}""";

        using var ov = new OverlayFixture(
            [(KarnokOnlySkill, kSkillJson), (VanessaSkill, vSkillJson)],
            (1, lvl1Json));

        var p = new PlayerState("p-q37", "Vanessa", HeroNames.Vanessa, 250, 10, 0, 10);
        p.Hand.TryAddAuto(new CardInstance(ov.Repo.GetCard(KarnokMeatId)!, CardTier.Bronze));
        var ctx = new RuleExecutionContext(ov.Repo, RuleScope.OutOfCombat, p, p, new Random(37)) { CurrentDay = 2, CurrentHour = 1 };

        var rolled = ov.LevelUps.RollRewards(2, ctx, id => ov.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
        Assert.Contains(VanessaSkill, rolled);
        Assert.DoesNotContain(KarnokOnlySkill, rolled);
    }

    /// <summary>Q38 &amp; Q39：已拥有满阶 Diamond 技能不再重复刷新与掉落，已有低阶技能获高阶野怪掉落时原地升阶不双份，且同阶禁止重复。</summary>
    [Fact]
    public void Q38_Q39_SkillDedup_PreventsDiamondDuplicateAndUpgradesInPlace()
    {
        var fx = RealFixture.Value;

        // 1. 已拥有 Diamond 技能时：LevelUpTable / SpawnQueryEngine / MonsterLoot 全部禁止再次发放该技能
        var pDia = new PlayerState("p-dia", "Vanessa", HeroNames.Vanessa, 250, 20, 0, 10);
        pDia.Skills.Add(new CardInstance(fx.Repo.GetCard(KeenEyeSkillId)!, CardTier.Diamond));
        var ctxDia = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, pDia, pDia, new Random(38));
        for (int i = 0; i < 15; i++)
        {
            var rolled = fx.LevelUps.RollRewards(2, ctxDia, id => fx.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
            Assert.DoesNotContain(KeenEyeSkillId, rolled, StringComparer.OrdinalIgnoreCase);
        }

        // SpawnQueryEngine 对 Diamond Skill 也必须拒绝
        var skillQuery = new RuleNode
        {
            Type = "TSpawnContextQuery",
            Groups =
            [
                new RuleNode
                {
                    SelectionMethod = "Random",
                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [KeenEyeSkillId] }],
                    Behaviors = [new RuleNode { Type = "TSpawnBehaviorAllowPlayerDuplicates", AllowPlayerDuplicates = true }],
                }
            ]
        };
        var ownedDia = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase) { [KeenEyeSkillId] = CardTier.Diamond };
        Assert.Empty(SpawnQueryEngine.SpawnCards(skillQuery, ctxDia, defaultCountOverride: 1, ownedMaxTiers: ownedDia));

        // 2. MonsterVictoryLoot（普通与分页）自动跳过已持有不可升阶技能
        var (lootRun, _) = CreateStartedEngine(fx, EHero.Vanessa, seed: 3901);
        var monsterWithSkill = fx.Monsters.First(m => m.Skills.Count > 0 && m.Items.Count > 0);
        var mobSkTplId = monsterWithSkill.Skills[0].TemplateId;
        AddOwnedSkill(lootRun, fx.Repo, mobSkTplId, ETier.Diamond);

        var shopLoot = new ShopEngine(lootRun, fx.Pool, fx.Repo);
        shopLoot.DealMonsterVictoryLoot(null, monsterWithSkill, null, chooseOne: false);
        Assert.DoesNotContain(lootRun.SelectionSet, id => lootRun.Cards[id].TemplateId.ToString().Equals(mobSkTplId, StringComparison.OrdinalIgnoreCase));

        shopLoot.DealMonsterVictoryLoot(null, monsterWithSkill, null, chooseOne: true);
        Assert.DoesNotContain(lootRun.SelectionSet, id => lootRun.Cards[id].TemplateId.ToString().Equals(mobSkTplId, StringComparison.OrdinalIgnoreCase));

        // 3. 已有 Bronze KeenEye -> 购买/拾取 Gold KeenEye 必须原地升为 Gold，绝不产生第 2 张同名技能；之后再遇 Silver 必须被拒绝且不扣金币
        var (skRun, skEngine) = CreateStartedEngine(fx, EHero.Vanessa, seed: 3902);
        skRun.PlayerAttributes[EPlayerAttributeType.Gold] = 100;
        var ownedSk = AddOwnedSkill(skRun, fx.Repo, KeenEyeSkillId, ETier.Bronze);
        var keTpl = fx.Pool.Get(KeenEyeSkillId)!;

        skRun.State = ERunState.Encounter;
        var goldCand = new ShopEngine(skRun, fx.Pool, fx.Repo).CreateSkillCandidate(keTpl, ETier.Gold, isFree: true);
        skRun.Cards[goldCand.InstanceId] = goldCand;
        skRun.SelectionSet.Add(goldCand.InstanceId);

        Assert.Null(skEngine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(goldCand.InstanceId) }).Error);
        var ownedKeenEyes = skRun.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.TemplateId.ToString().Equals(KeenEyeSkillId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(ownedKeenEyes);
        Assert.Equal(ETier.Gold, ownedKeenEyes[0].Tier);
        Assert.Equal(ownedSk.InstanceId, ownedKeenEyes[0].InstanceId);

        // 现持有 Gold KeenEye，再遇 Silver 同名技能（不可融合亦低于当前阶）必须被拒绝且金币 100 不扣除
        skRun.State = ERunState.Encounter;
        var silverCand = new ShopEngine(skRun, fx.Pool, fx.Repo).CreateSkillCandidate(keTpl, ETier.Silver, isFree: false);
        skRun.Cards[silverCand.InstanceId] = silverCand;
        skRun.SelectionSet.Add(silverCand.InstanceId);

        int goldBeforeDup = skRun.PlayerAttributes[EPlayerAttributeType.Gold];
        var dupOut = skEngine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(silverCand.InstanceId) });
        Assert.NotNull(dupOut.Error);
        Assert.Equal(goldBeforeDup, skRun.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Single(skRun.Cards.Values, c => c.Owner == ECombatantId.Player && c.TemplateId.ToString().Equals(KeenEyeSkillId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Q41：UntilEndOfDay（DayAdd 与 DayEnchantment）战后与同日切小时保留，仅在 Day 1 -> Day 2 Hour 1 清零并发出还原事件。</summary>
    [Fact]
    public void Q41_UntilEndOfDayModifiers_SurviveHourAdvanceAndRevertOnNewDay()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx, EHero.Vanessa, seed: 4101);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        var cutlass = AddOwnedItem(run, fx.Repo, CutlassId, EInventorySection.Hand, 0, ETier.Silver);
        int baseDmg = cutlass.Attributes[ECardAttributeType.DamageAmount];

        cutlass.CoreCard!.DayAdd["DamageAmount"] = 30;
        cutlass.CoreCard.ApplyEnchantment("Fiery", preventOverride: false, DurationTarget.Day);
        run.SyncAllEntriesFromCoreCardsAndDiff(fx.Repo.GameMode.StandardPrices, [cutlass]);
        Assert.Equal(EEnchantmentType.Fiery, cutlass.Enchantment);
        Assert.Equal(baseDmg + 30, cutlass.Attributes[ECardAttributeType.DamageAmount]);

        // 1. 同日打一场 PvE 野怪战斗（Boarrior），战后进入 Loot 时 UntilEndOfDay 绝不误清，退出 Loot 切到 Hour 2 后仍保留
        var mobOpt = InjectChoiceEncounter(run, fx.Pool, "b93f5b44-ba9f-4196-a94e-c5de17ab5c5d");
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) }).Error);
        Assert.Equal(1u, run.Day);
        Assert.Equal(EEnchantmentType.Fiery, cutlass.Enchantment);
        Assert.Equal(baseDmg + 30, cutlass.Attributes[ECardAttributeType.DamageAmount]);

        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error); // Loot -> Hour 2
        if (run.State == ERunState.LevelUp)
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(1u, run.Day);
        Assert.True(run.Hour >= 2u);
        Assert.Equal(EEnchantmentType.Fiery, cutlass.Enchantment);
        Assert.Equal(baseDmg + 30, cutlass.Attributes[ECardAttributeType.DamageAmount]);

        // 2. 切到 Hour 6（完成当日 PvP 胜场计数）后进入并退出一次普通遭遇，真实触发 Day 1 Hour 6 -> Day 2 Hour 1 跨天结算
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", "6" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "win", "1" } }).Error);
        var h6Opt = InjectChoiceEncounter(run, fx.Pool, CurioId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(h6Opt.InstanceId) }).Error);
        var newDayOut = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(newDayOut.Error);

        Assert.Equal(2u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Null(cutlass.Enchantment);
        Assert.Null(cutlass.CoreCard.DayEnchantment);
        Assert.Equal(baseDmg, cutlass.Attributes[ECardAttributeType.DamageAmount]);
        Assert.Contains(newDayOut.Events, e => e is GameSimEventCardEnchanted enc && enc.InstanceId == cutlass.InstanceId && enc.IsReverted);
    }

    /// <summary>Q43：TActionCardEnchantRandom 与附魔基座兜底抽选在非防覆盖模式下必须排除卡牌当前已有附魔。</summary>
    [Fact]
    public void Q43_RandomEnchantment_ExcludesCurrentEnchantment()
    {
        var fx = RealFixture.Value;
        var cutlassTpl = fx.Repo.GetCard(CutlassId)!;

        // 1. 测试 AbilityExecutor TActionCardEnchantRandom（preventOverride=false）多次抽取均排除当前 Fiery
        for (int i = 0; i < 20; i++)
        {
            var p = new PlayerState("p-q43", "Vanessa", HeroNames.Vanessa, 250, 10, 0, 10);
            var card = new CardInstance(cutlassTpl, CardTier.Silver, "Fiery", $"c43_{i}");
            p.Hand.TryAddAuto(card);

            var action = new RuleNode
            {
                Type = "TActionCardEnchantRandom",
                Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" },
            };
            var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, p, p, new Random(4300 + i)) { SelfCard = card };
            AbilityExecutor.ExecuteAction(action, card, ctx);

            Assert.NotNull(card.Enchantment);
            Assert.NotEqual("Fiery", card.Enchantment, StringComparer.OrdinalIgnoreCase);
            Assert.True(cutlassTpl.Enchantments!.ContainsKey(card.Enchantment!));
        }

        // 2. 测试 RunEngine Pedestal 兜底随机附魔同样排除当前已有 Fiery
        const string RandomEnchPedestalId = "30000001-0000-4000-8000-000000000043";
        string pedJson = """{"$type":"TCardPedestalEncounter","Id":"30000001-0000-4000-8000-000000000043","Type":"PedestalEncounter","InternalName":"TestRandomEnchantPedestal","StartingTier":"Diamond","Heroes":["Common"],"SelectionContext":{"Rules":{"CanSelectMultiple":false,"SelectionIsFree":true,"CanExit":true},"Behaviors":[{"$type":"TPedestalBehaviorEnchantRandom"}]}}""";
        using var ov = new OverlayFixture([(RandomEnchPedestalId, pedJson)]);

        for (int i = 0; i < 15; i++)
        {
            var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, EHero.Vanessa, seed: 4350 + i);
            engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });
            var owned = AddOwnedItem(run, ov.Repo, CutlassId, EInventorySection.Hand, 0, ETier.Silver, "Fiery");
            var pedOpt = InjectChoiceEncounter(run, ov.Pool, RandomEnchPedestalId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pedOpt.InstanceId) }).Error);
            var pedOut = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(owned.InstanceId) });
            Assert.Null(pedOut.Error);
            Assert.NotEqual(EEnchantmentType.Fiery, owned.Enchantment);
        }
    }

    /// <summary>Q45：Jules 开局自动生成冰/火插座，Brolin 新增插座避开相邻格，且大件物品横跨双插座同时激活 Heated 与 Chilled 效果。</summary>
    [Fact]
    public void Q45_JulesSockets_StartRunSpawns_BrolinNonAdjacent_DualSocketCombatTriggersBoth()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx, EHero.Jules, seed: 4501);

        // 1. Jules StartRun 必须直接注入并生成 1 个初始炉灶或冰柜 SocketEffect
        var startSockets = run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.SocketEffect)
            .ToList();
        Assert.Single(startSockets);
        Assert.Contains(startSockets[0].TemplateId.ToString(), new[] { StoveSocketId, CoolerSocketId });
        int firstSlot = (int)(startSockets[0].Socket ?? EContainerSocketId.Socket_0);

        // 2. 经 Chef Brolin (7a4f9be7) 增加第 2 个插座（[EncounterStep] Add Stove/Cooler），验证与首个插座互不相邻
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });
        var brolinOpt = InjectChoiceEncounter(run, fx.Pool, ChefBrolinId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(brolinOpt.InstanceId) }).Error);
        var addSocketStep = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId.ToString().Equals(AddStoveStepId, StringComparison.OrdinalIgnoreCase)
                              || c.TemplateId.ToString().Equals(AddCoolerStepId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(addSocketStep);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(addSocketStep!.InstanceId) }).Error);

        var allSockets = run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.SocketEffect)
            .ToList();
        Assert.Equal(2, allSockets.Count);
        int secondSlot = (int)allSockets.First(s => s.InstanceId != startSockets[0].InstanceId).Socket!;
        Assert.True(Math.Abs(secondSlot - firstSlot) > 1,
            $"Chef Brolin 新增插座不应与初始插座相邻: first={firstSlot}, second={secondSlot}");

        // 3. 大型物品 Feast（width=3，放在 slot 3 -> 覆盖 3,4,5）同时跨越 Stove(slot 3) 与 Cooler(slot 5)
        var stoveTpl = fx.Repo.GetCard(StoveSocketId)!;
        var coolerTpl = fx.Repo.GetCard(CoolerSocketId)!;
        var p1 = new PlayerState("j1", "Jules", HeroNames.Jules, 1000, 0, 0, 10);
        var p2 = new PlayerState("opp", "Common", HeroNames.Common, 1000, 0, 0, 10);
        p1.SocketEffects.Add(new CardInstance(stoveTpl, CardTier.Diamond) { StartSlot = 3 });
        p1.SocketEffects.Add(new CardInstance(coolerTpl, CardTier.Diamond) { StartSlot = 5 });

        var feast = new CardInstance(fx.Repo.GetCard(FeastId)!, CardTier.Gold) { StartSlot = 3 };
        Assert.True(p1.Hand.TryPlaceOrShift(feast, 3));

        AuraEngine.RecalculateAuras(p1, p2, RuleScope.OutOfCombat, 1, 1, fx.Repo);
        Assert.True(feast.GetAttribute("Heated") > 0, "Feast 覆盖 Stove 插座应激活 Heated > 0");
        Assert.True(feast.GetAttribute("Chilled") > 0, "Feast 覆盖 Cooler 插座应激活 Chilled > 0");

        var combatRes = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 45, recordTimeline: true, recordEvents: true);
        Assert.Contains(combatRes.Events, e => e.Type == CombatEventType.BurnApplied);
        Assert.Contains(combatRes.Events, e => e.Type == CombatEventType.RegenApplied);
    }

    /// <summary>Q47：长袍怪商 30g 神秘宝箱在 Encounter 态可选并触发 Legendary 遭遇任务，同时核验 Waystones 当前 DB 6 条官方任务链。</summary>
    [Fact]
    public void Q47_ShroudedFigure30gChestAndWaystonesDbVerification()
    {
        var fx = RealFixture.Value;

        // 1. Waystones (de82424c) 当前 DB 含 6 条 Quest（1..4, 6, 7），Quest_7 指向 Curio，无传说物品购买任务
        var wsTpl = fx.Repo.GetCard(WaystonesId)!;
        Assert.NotNull(wsTpl.Quests);
        Assert.Equal(6, wsTpl.Quests!.Count);
        var wsJson = JsonSerializer.Serialize(wsTpl.Quests);
        Assert.Contains(CurioId, wsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Quest_5", wsJson, StringComparison.OrdinalIgnoreCase);

        // 2. 长袍怪商 30g 神秘宝箱 -> SelectItemCommand in ERunState.Encounter 正常进场 + 推进 Tour Schedule 传说任务 + 获免费传说物品
        var (run, engine) = CreateStartedEngine(fx, EHero.Hero8, seed: 4701);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });
        run.PlayerAttributes[EPlayerAttributeType.Gold] = 50;

        var tourSchedule = AddOwnedItem(run, fx.Repo, TourScheduleId, EInventorySection.Hand, 0, ETier.Silver);
        int questBefore = tourSchedule.Attributes.GetValueOrDefault(ECardAttributeType.Quest_1, 0);

        var sfOpt = InjectChoiceEncounter(run, fx.Pool, ShroudedFigureId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(sfOpt.InstanceId) }).Error);

        var chestStep = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId.ToString().Equals(MysteriousChestStepId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(chestStep);

        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(chestStep!.InstanceId) }).Error);
        Assert.Equal(20, run.PlayerAttributes[EPlayerAttributeType.Gold]);

        var chestEventCard = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId.ToString().Equals(MysteriousChestEventId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(chestEventCard);

        // 客户端通过 SelectItemCommand 在 ERunState.Encounter 点击 56eb7fa9 遭遇卡进入宝箱事件
        var openChestOut = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(chestEventCard!.InstanceId) });
        Assert.Null(openChestOut.Error);

        Assert.NotEmpty(run.SelectionSet);
        Assert.All(run.SelectionSet, id =>
        {
            var e = run.Cards[id];
            Assert.Equal(ETier.Legendary, e.Tier);
            Assert.Equal(0, e.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0));
        });

        int questAfter = tourSchedule.CoreCard != null
            ? (int)Math.Round(tourSchedule.CoreCard.GetAttribute("Quest_1"))
            : tourSchedule.Attributes.GetValueOrDefault(ECardAttributeType.Quest_1, 0);
        Assert.True(questAfter > questBefore, $"进入 Legendary 神秘宝箱遭遇应推进 Tour Schedule 任务进度: before={questBefore}, after={questAfter}");
    }
}
