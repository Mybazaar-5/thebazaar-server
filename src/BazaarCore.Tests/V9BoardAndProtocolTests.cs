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
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarServer.Data;
using BazaarServer.Game;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V9 Stage A 回归测试：
/// 覆盖棋盘单真值（#16/#18/#21/#28/#32a）、战后临时异常清除与首帧显式归零（#6/#27）、
/// 顶栏当前事件头像（#19/#24）与升级台悬停预览协议（#14）。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class V9BoardAndProtocolTests
{
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string SwashBuckleId = "69688b74-fd2b-42a2-a018-2b720a13f95a";
    private const string RedGumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
    private const string DuctTapeId = "0966f887-5aaf-44a5-90fa-ecb194270513";
    private const string JetpackId = "94144b09-eb5b-438b-a0fe-3d7494b84c84";
    private const string JetbikeId = "16b9413f-c28b-4c45-84a9-4c214cc681fd";
    private const string FangedIngletCombatId = "1ea147f3-5712-4333-b347-8fd65d08f009";
    private const string EagleTalismanLootId = "0022c409-c839-41e8-8022-65a407457dfe";
    private const string PolShopId = "065f6793-8607-4f02-abfe-db6afb221ab2";

    private sealed class GameFixture
    {
        public string DataDir { get; init; } = "";
        public string DbPath { get; init; } = "";
        public CardPool Pool { get; init; } = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
        public Dictionary<string, string> RawCardsJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RawMonstersJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RawGameModesJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, string> RawLevelUpsJson { get; init; } = new();
        public HashSet<string> ValidCardIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Lazy<GameFixture> RealFixture = new(LoadRealFixture);

    private static string ResolveDataDir()
        => TestInfrastructure.TestDataLocator.ResolveDataDir();

    private static GameFixture LoadRealFixture()
    {
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        return new GameFixture
        {
            DataDir = shared.DataDir,
            DbPath = shared.DbPath,
            Pool = shared.Pool,
            Monsters = shared.CreateMonstersCopy(),
            LevelUps = shared.LevelUps,
            Mode = shared.Mode,
            Repo = shared.Repo,
            Ghosts = shared.Ghosts,
            ValidCardIds = shared.CreateValidCardIdsCopy(),
        };
    }

    private sealed class OverlayContext : IDisposable
    {
        private readonly TestInfrastructure.SharedTestOverlay _inner;
        public string TempDir => _inner.TempDir;
        public string DbPath => TestInfrastructure.SharedGameDataFixture.Shared.DbPath;
        public CardPool Pool => _inner.Pool;
        public List<BazaarServer.Data.MonsterTemplate> Monsters => _inner.Monsters;
        public LevelUpTable LevelUps => _inner.LevelUps;
        public GameModeConfig Mode => _inner.Mode;
        public GameDataRepository Repo => _inner.Repo;
        public PvpGhostPool Ghosts => _inner.Ghosts;

        public OverlayContext(IEnumerable<(string Id, string Json)> customCards)
        {
            _inner = TestInfrastructure.SharedGameDataFixture.Shared.CreateMemoryOverlay(customCards: customCards);
        }

        public void Dispose() => _inner.Dispose();
    }

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        CardPool pool,
        List<BazaarServer.Data.MonsterTemplate> monsters,
        LevelUpTable levelUps,
        GameModeConfig mode,
        GameDataRepository repo,
        PvpGhostPool ghosts,
        EHero hero = EHero.Vanessa,
        int? seed = 1001)
    {
        var run = new RunState
        {
            RunId = "v9_test_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            RngSeed = seed ?? 0,
        };
        var engine = new RunEngine(run, pool, monsters, levelUps, mode, repo, ghosts);
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
        ETier? tierOverride = null)
    {
        var coreTpl = repo.GetCard(templateId)!;
        var poolTpl = pool.Get(templateId);
        ETier tier = tierOverride ?? ShopEngine.ParseTier(coreTpl.StartingTier);
        var core = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), null, "v9_own_" + Guid.NewGuid().ToString("N")[..8])
        {
            StartSlot = slot,
        };
        BoardGrid grid = section == EInventorySection.Stash ? run.StashGrid : run.HandGrid;
        Assert.True(grid.TryPlaceWithDisplaceOrSwap(core, slot), $"无法放置 {templateId} 到 {section} 槽位 {slot}");

        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)core.StartSlot,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.Cards[entry.InstanceId] = entry;
        run.SyncAllEntriesFromCoreCardsAndDiff(repo.GameMode.StandardPrices, new[] { entry });
        return entry;
    }

    private static RunState.CardEntry InjectChoiceEncounter(
        RunState run,
        CardPool pool,
        string encounterTemplateId,
        ECardType fallbackType = ECardType.EventEncounter)
    {
        var tpl = pool.Get(encounterTemplateId);
        string instId = "enc_v9_" + Guid.NewGuid().ToString("N")[..8];
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(encounterTemplateId),
            Type = tpl != null ? ShopEngine.ParseCardType(tpl.Type) : fallbackType,
            Size = ECardSize.Medium,
            Tier = tpl != null ? ShopEngine.ParseTier(tpl.StartingTier) : ETier.Bronze,
            Owner = null,
            Section = null,
            Socket = null,
        };
        run.Cards[instId] = entry;
        run.SelectionSet.Add(instId);
        return entry;
    }

    private static string BuildShopJson(string id, string name, string[] itemIds, bool selectionIsFree = false)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardEncounterEvent",
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Type = "EventEncounter",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            SelectionContext = new
            {
                SpawnContext = new
                {
                    @__type = "TSpawnContextQuery",
                    Groups = new[]
                    {
                        new
                        {
                            @__type = "TSpawnGroup",
                            Filters = new[] { new { @__type = "TSpawnFilterIdList", Ids = itemIds } },
                            SelectionMethod = "Sequential",
                            Limit = new { @__type = "TFixedValue", Value = (double)itemIds.Length },
                            Prerequisites = (object?)null,
                            RandomWeight = 100,
                            Behaviors = (object?)null,
                        },
                    },
                    SelectionMethod = "Sequential",
                    Limit = new { @__type = "TFixedValue", Value = (double)itemIds.Length },
                    Behaviors = (object?)null,
                },
                Rules = new
                {
                    CanSelectMultiple = true,
                    SelectionIsFree = selectionIsFree,
                    CanExit = true,
                    WillAutoSellOnExit = false,
                    NextEncounterOnExit = (string?)null,
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildStepEventJson(string id, string name, string[] stepIds)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardEncounterEvent",
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Type = "EventEncounter",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            SelectionContext = new
            {
                SpawnContext = new
                {
                    @__type = "TSpawnContextQuery",
                    Groups = new[]
                    {
                        new
                        {
                            @__type = "TSpawnGroup",
                            Filters = new[] { new { @__type = "TSpawnFilterIdList", Ids = stepIds } },
                            SelectionMethod = "Sequential",
                            Limit = new { @__type = "TFixedValue", Value = (double)stepIds.Length },
                            Prerequisites = (object?)null,
                            RandomWeight = 100,
                            Behaviors = (object?)null,
                        },
                    },
                    SelectionMethod = "Sequential",
                    Limit = new { @__type = "TFixedValue", Value = (double)stepIds.Length },
                    Behaviors = (object?)null,
                },
                Rules = new
                {
                    CanSelectMultiple = false,
                    SelectionIsFree = true,
                    CanExit = false,
                    WillAutoSellOnExit = false,
                    NextEncounterOnExit = (string?)null,
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildTransformLeftMostStepJson(string stepId, string targetTemplateId)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardEncounterStep",
            Id = stepId,
            Version = "5.0.0",
            InternalName = "V9 Transform LeftMost Step",
            Type = "EncounterStep",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            IsReselectable = false,
            ShouldProgressHour = true,
            ExperienceAwardUponSelection = 0,
            NextEncounterOnSelection = (object?)null,
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    Trigger = new { @__type = "TTriggerOnCardSelected" },
                    ActiveIn = "HandOnly",
                    Action = new
                    {
                        @__type = "TActionCardTransform",
                        Target = new
                        {
                            @__type = "TTargetCardXMost",
                            TargetSection = "SelfHand",
                            TargetMode = "LeftMost",
                        },
                        SpawnContext = new
                        {
                            @__type = "TSpawnContextQuery",
                            Groups = new[]
                            {
                                new
                                {
                                    @__type = "TSpawnGroup",
                                    Filters = new[] { new { @__type = "TSpawnFilterIdList", Ids = new[] { targetTemplateId } } },
                                    SelectionMethod = "Sequential",
                                    Limit = new { @__type = "TFixedValue", Value = 1.0 },
                                    Prerequisites = (object?)null,
                                    RandomWeight = 100,
                                    Behaviors = (object?)null,
                                },
                            },
                            SelectionMethod = "Sequential",
                            Limit = new { @__type = "TFixedValue", Value = 1.0 },
                            Behaviors = (object?)null,
                        },
                    },
                    Prerequisites = (object?)null,
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildSellTransformLeftMostItemJson(string id, string targetTemplateId)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardItem",
            Id = id,
            Version = "5.0.0",
            InternalName = "V9 Sell Transform Trigger",
            Type = "Item",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["SellPrice"] = 1,
                    },
                    AbilityIds = new[] { "ab_sell_tr" },
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new Dictionary<string, object>
            {
                ["ab_sell_tr"] = new
                {
                    Id = "ab_sell_tr",
                    Trigger = new { @__type = "TTriggerOnCardSold", Subject = new { @__type = "TTargetCardSelf" } },
                    ActiveIn = "HandAndStash",
                    Action = new
                    {
                        @__type = "TActionCardTransform",
                        Target = new
                        {
                            @__type = "TTargetCardXMost",
                            TargetSection = "SelfHand",
                            TargetMode = "LeftMost",
                        },
                        SpawnContext = new
                        {
                            @__type = "TSpawnContextQuery",
                            Groups = new[]
                            {
                                new
                                {
                                    @__type = "TSpawnGroup",
                                    Filters = new[] { new { @__type = "TSpawnFilterIdList", Ids = new[] { targetTemplateId } } },
                                    SelectionMethod = "Sequential",
                                    Limit = new { @__type = "TFixedValue", Value = 1.0 },
                                    Prerequisites = (object?)null,
                                    RandomWeight = 100,
                                    Behaviors = (object?)null,
                                },
                            },
                            SelectionMethod = "Sequential",
                            Limit = new { @__type = "TFixedValue", Value = 1.0 },
                            Behaviors = (object?)null,
                        },
                    },
                    Prerequisites = (object?)null,
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildCombatStatusItemJson(string id)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardItem",
            Id = id,
            Version = "5.0.0",
            InternalName = "V9 Combat Status Injector",
            Type = "Item",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["SellPrice"] = 2,
                    },
                    AbilityIds = new[] { "ab_fly", "ab_freeze", "ab_slow", "ab_haste" },
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new Dictionary<string, object>
            {
                ["ab_fly"] = new
                {
                    Id = "ab_fly",
                    Trigger = new { @__type = "TTriggerOnFightStarted" },
                    ActiveIn = "HandOnly",
                    WorksIn = "CombatOnly",
                    Action = new
                    {
                        @__type = "TActionCardFlyingStart",
                        Target = new { @__type = "TTargetCardSelf" },
                    },
                    Prerequisites = (object?)null,
                },
                ["ab_freeze"] = new
                {
                    Id = "ab_freeze",
                    Trigger = new { @__type = "TTriggerOnFightStarted" },
                    ActiveIn = "HandOnly",
                    WorksIn = "CombatOnly",
                    Action = new
                    {
                        @__type = "TActionCardFreeze",
                        Target = new { @__type = "TTargetCardSelf" },
                        Value = new { @__type = "TFixedValue", Value = 10000.0 },
                    },
                    Prerequisites = (object?)null,
                },
                ["ab_slow"] = new
                {
                    Id = "ab_slow",
                    Trigger = new { @__type = "TTriggerOnFightStarted" },
                    ActiveIn = "HandOnly",
                    WorksIn = "CombatOnly",
                    Action = new
                    {
                        @__type = "TActionCardSlow",
                        Target = new { @__type = "TTargetCardSelf" },
                        Value = new { @__type = "TFixedValue", Value = 10000.0 },
                    },
                    Prerequisites = (object?)null,
                },
                ["ab_haste"] = new
                {
                    Id = "ab_haste",
                    Trigger = new { @__type = "TTriggerOnFightStarted" },
                    ActiveIn = "HandOnly",
                    WorksIn = "CombatOnly",
                    Action = new
                    {
                        @__type = "TActionCardHaste",
                        Target = new { @__type = "TTargetCardSelf" },
                        Value = new { @__type = "TFixedValue", Value = 10000.0 },
                    },
                    Prerequisites = (object?)null,
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildFastNukeWeaponJson(string id)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardItem",
            Id = id,
            Version = "5.0.0",
            InternalName = "V9 Fast Nuke Weapon",
            Type = "Item",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Weapon" },
            HiddenTags = new[] { "Damage" },
            SpawningEligibility = "Never",
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["CooldownMax"] = 1000,
                        ["DamageAmount"] = 500,
                        ["SellPrice"] = 2,
                    },
                    AbilityIds = new[] { "ab_dmg" },
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new Dictionary<string, object>
            {
                ["ab_dmg"] = new
                {
                    Id = "ab_dmg",
                    Trigger = new { @__type = "TTriggerOnCardFired" },
                    ActiveIn = "HandOnly",
                    Action = new
                    {
                        @__type = "TActionPlayerDamage",
                        Target = new { @__type = "TTargetPlayerRelative", TargetMode = "Opponent" },
                        Value = new
                        {
                            @__type = "TReferenceValueCardAttribute",
                            AttributeType = "DamageAmount",
                            Target = new { @__type = "TTargetCardSelf" },
                        },
                    },
                    Prerequisites = (object?)null,
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildInnateFlyerItemJson(string id)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardItem",
            Id = id,
            Version = "5.0.0",
            InternalName = "V9 Innate Flyer",
            Type = "Item",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["Flying"] = 1,
                        ["SellPrice"] = 2,
                    },
                    AbilityIds = Array.Empty<string>(),
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    // ── 1. #28：HandSocketUnlockMask 居中开局 4 格 & 升级扩充至 6/8/10 格 ──

    [Fact]
    public void V9_01_HandSocketUnlockMask_CenteredStartAndLevelUpExpansion()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        // Lv1 开局：Socket_3..6 居中 4 格 (0b00_0111_1000 = 0x078)
        Assert.Equal((ushort)0b00_0111_1000, run.UnlockedSlots);
        Assert.Equal((ushort)0b00_0111_1000, run.HandSocketUnlockMask);
        Assert.Equal(4, run.HandGrid.UnlockedCount);

        // 升至 Lv2 -> 6 格 (Socket_2..7 = 0b00_1111_1100)
        int xpToLv2 = Math.Max(1, fx.Mode.ExperiencePerLevel - run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0));
        var lv2Cheats = engine.Handle(new CheatCommand { Args = new List<string> { "exp", xpToLv2.ToString() } });
        Assert.Null(lv2Cheats.Error);
        Assert.True(run.PlayerAttributes[EPlayerAttributeType.Level] >= 2);
        Assert.Equal((ushort)0b00_1111_1100, run.UnlockedSlots);
        Assert.Equal(run.UnlockedSlots, run.HandSocketUnlockMask);
        Assert.Equal(6, run.HandGrid.UnlockedCount);
        Assert.Contains(lv2Cheats.Events, e =>
            e is GameSimEventSocketsUnlocked u &&
            u.UnlockedSocketsBitmask == (ushort)0b00_1111_1100 &&
            u.Section == EInventorySection.Hand);

        // 继续升至 Lv3 -> 8 格 (0b01_1111_1110)，再到 Lv4 -> 10 格 (0b11_1111_1111)
        var lv3Cheats = engine.Handle(new CheatCommand { Args = new List<string> { "exp", (fx.Mode.ExperiencePerLevel * 2).ToString() } });
        Assert.Null(lv3Cheats.Error);
        Assert.True(run.PlayerAttributes[EPlayerAttributeType.Level] >= 4);
        Assert.Equal((ushort)0b11_1111_1111, run.UnlockedSlots);
        Assert.Equal(10, run.HandGrid.UnlockedCount);
    }

    // ── 2. #28：Hand 碎片空位 >= itemWidth 时自动紧凑滑移腾位进场上，禁止误进背包 ──

    [Fact]
    public void V9_02_BuyItemToHand_AutoCompactsFragmentedFreeSlots_AndSyncsNeighbors()
    {
        const string shopId = "90900001-0001-4001-8001-000000000001";
        string shopJson = BuildShopJson(shopId, "V9 AutoCompact Shop", new[] { CutlassId, RedGumballId });
        using var ov = new OverlayContext(new[] { (shopId, shopJson) });
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);

        // 升至 Lv2 解锁 Socket_2..7 共 6 格，保留大量金币
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "gold", "100" } }).Error);
        run.UnlockCenteredSockets(2);
        run.HandSocketUnlockMask = run.UnlockedSlots;
        Assert.Equal((ushort)0b00_1111_1100, run.UnlockedSlots);

        // 在 Socket_2..7 放入 Small(2)、Medium(4..5)、Small(7) -> 剩 Socket_3 和 Socket_6 共 2 个不连续空槽
        var sLeft = AddOwnedItem(run, ov.Repo, ov.Pool, RedGumballId, EInventorySection.Hand, slot: 2);
        var mMid = AddOwnedItem(run, ov.Repo, ov.Pool, SwashBuckleId, EInventorySection.Hand, slot: 4);
        var sRight = AddOwnedItem(run, ov.Repo, ov.Pool, DuctTapeId, EInventorySection.Hand, slot: 7);
        Assert.Equal(2, run.HandGrid.CountFreeUnlockedSlots());

        var shopOpt = InjectChoiceEncounter(run, ov.Pool, shopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopOpt.InstanceId) }).Error);

        var cutlassShelf = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(CutlassId));
        // 模拟客户端碎片满手找不到连续空位时发出的兜底目标 [Socket_0, Socket_1] (锁定边缘)
        var buyOut = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(cutlassShelf.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_0, EContainerSocketId.Socket_1 },
        });
        Assert.Null(buyOut.Error);

        // Cutlass(width=2) 必须通过自动紧凑滑移直接进入 Hand，绝不能被丢进 Stash！
        Assert.Equal(EInventorySection.Hand, cutlassShelf.Section);
        Assert.True(cutlassShelf.Socket.HasValue);
        Assert.True(run.HandGrid.ValidateInvariants(out string err), err);
        Assert.DoesNotContain(run.StashGrid.Cards, c => c.InstanceId == cutlassShelf.InstanceId);

        // 至少有原邻卡发生滑移腾位且事件与 NetMessageGameSim.Cards 完整同步
        var sim = run.BuildFullGameSim(buyOut.Events);
        var snap = run.BuildFullGameSim();
        var movedNeighbors = buyOut.Events.OfType<GameSimEventCardMoved>()
            .Where(m => m.InstanceId != cutlassShelf.InstanceId)
            .ToList();
        Assert.NotEmpty(movedNeighbors);
        foreach (var mv in movedNeighbors)
        {
            Assert.Equal(run.Cards[mv.InstanceId].Socket, mv.ToSocket);
            Assert.Null(sim.Cards[mv.InstanceId].Placement);
            Assert.Equal(run.Cards[mv.InstanceId].Socket, snap.Cards[mv.InstanceId].Placement!.Socket);
            Assert.Equal(EInventorySection.Hand, snap.Cards[mv.InstanceId].Placement!.Section);
        }
        Assert.Contains(engine.BoardAuditLogs, l => l.Contains("[BoardAudit] op=Buy", StringComparison.Ordinal));
    }

    // ── 3. #16：拖上场边界夹紧不弹回 + 跨区原子换位 + 同区挤位全量同步 ──

    [Fact]
    public void V9_03_MoveCard_ClampsLockedBoundary_CrossSectionSwaps_AndSyncsMovedNeighbors()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        // 3a. 边界夹紧：Lv1 仅 Socket_3..6 解锁，Hand 放 Small(3)，Stash 放 Medium(2)
        // 拖 Medium 到锁定边界 Hand Socket_2 -> 自动夹紧放入 Unlocked 槽位（不报错不弹回）
        var sHand = AddOwnedItem(run, fx.Repo, fx.Pool, RedGumballId, EInventorySection.Hand, slot: 3);
        var mStash = AddOwnedItem(run, fx.Repo, fx.Pool, CutlassId, EInventorySection.Stash, slot: 2);
        var clampMove = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(mStash.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_2 },
        });
        Assert.Null(clampMove.Error);
        Assert.Equal(EInventorySection.Hand, mStash.Section);
        Assert.True((int)mStash.Socket!.Value >= 3 && (int)mStash.Socket.Value + 1 <= 6);
        Assert.True(run.HandGrid.ValidateInvariants(out string err1), err1);

        // 3b. 跨区原子换位：填满 Lv1 全部 4 格 (RedGumball 在 3, Swash在4? 先清理并摆 2 张 Medium 填满 3..6)
        run.HandGrid.Remove(sHand.CoreCard!);
        run.Cards.Remove(sHand.InstanceId);
        run.HandGrid.Remove(mStash.CoreCard!);
        run.Cards.Remove(mStash.InstanceId);

        var m1 = AddOwnedItem(run, fx.Repo, fx.Pool, CutlassId, EInventorySection.Hand, slot: 3);
        var m2 = AddOwnedItem(run, fx.Repo, fx.Pool, SwashBuckleId, EInventorySection.Hand, slot: 5);
        var m3 = AddOwnedItem(run, fx.Repo, fx.Pool, CutlassId, EInventorySection.Stash, slot: 2);

        var swapOut = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(m3.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_3, EContainerSocketId.Socket_4 },
        });
        Assert.Null(swapOut.Error);
        Assert.Equal(EInventorySection.Hand, m3.Section);
        Assert.Equal(EContainerSocketId.Socket_3, m3.Socket);
        Assert.Equal(EInventorySection.Stash, m1.Section);
        Assert.True(run.HandGrid.ValidateInvariants(out string err2), err2);
        Assert.True(run.StashGrid.ValidateInvariants(out string err3), err3);

        Assert.Contains(swapOut.Events, e => e is GameSimEventCardMoved m && m.InstanceId == m3.InstanceId && m.ToInventory == EInventorySection.Hand);
        Assert.Contains(swapOut.Events, e => e is GameSimEventCardMoved m && m.InstanceId == m1.InstanceId && m.ToInventory == EInventorySection.Stash);
        var simSwap = run.BuildFullGameSim(swapOut.Events);
        Assert.Null(simSwap.Cards[m3.InstanceId].Placement);
        Assert.Null(simSwap.Cards[m1.InstanceId].Placement);
        var snapSwap = run.BuildFullGameSim();
        Assert.Equal(EInventorySection.Hand, snapSwap.Cards[m3.InstanceId].Placement!.Section);
        Assert.Equal(EInventorySection.Stash, snapSwap.Cards[m1.InstanceId].Placement!.Section);
        Assert.Contains(engine.BoardAuditLogs, l => l.Contains("[BoardAudit] op=Move", StringComparison.Ordinal));
    }

    // ── 4. #21：小卡变大卡（喷射摩托 Jetbike）无重叠/无幽灵卡/可开炮/战后保留 ──

    [Fact]
    public void V9_04_TransformIntoLargeJetbike_ReconcilesWithoutOverlapOrGhost_AndFiresInCombat()
    {
        const string eventId = "90900002-0001-4001-8001-000000000001";
        const string stepId = "90900002-0001-4001-8001-000000000002";
        const string sellTrId = "90900002-0001-4001-8001-000000000003";
        string stepJson = BuildTransformLeftMostStepJson(stepId, JetbikeId);
        string eventJson = BuildStepEventJson(eventId, "V9 Jetbike Transform Event", new[] { stepId });
        string sellTrJson = BuildSellTransformLeftMostItemJson(sellTrId, JetbikeId);

        using var ov = new OverlayContext(new[] { (stepId, stepJson), (eventId, eventJson), (sellTrId, sellTrJson) });

        // 4a. Hand 有 6 格 (Lv2 Socket_2..7)，左到右依次放 Jetpack(1)@3, Cutlass(2)@4, DuctTape(1)@6 -> 总宽4，变 Large(3) 后总宽6刚好通过滑移挤入 Hand
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        run.UnlockCenteredSockets(2);
        run.HandSocketUnlockMask = run.UnlockedSlots;

        var jetpack = AddOwnedItem(run, ov.Repo, ov.Pool, JetpackId, EInventorySection.Hand, slot: 3);
        var cutlass = AddOwnedItem(run, ov.Repo, ov.Pool, CutlassId, EInventorySection.Hand, slot: 4);
        var tape = AddOwnedItem(run, ov.Repo, ov.Pool, DuctTapeId, EInventorySection.Hand, slot: 6);
        string oldJetpackId = jetpack.InstanceId;

        var evOpt = InjectChoiceEncounter(run, ov.Pool, eventId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(evOpt.InstanceId) }).Error);
        var stepCard = run.SelectionSet.Select(id => run.Cards[id]).Single();

        var trOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(stepCard.InstanceId) });
        Assert.Null(trOut.Error);

        // 旧 Jetpack 彻底清除（不在 Cards 也不在 Grid），新 Jetbike 在 Cards 且实体落入 HandGrid（绝非幽灵）
        Assert.False(run.Cards.ContainsKey(oldJetpackId));
        Assert.DoesNotContain(run.HandGrid.Cards, c => c.InstanceId == oldJetpackId);
        var jetbike = run.Cards.Values.Single(c => c.TemplateId == Guid.Parse(JetbikeId));
        Assert.Equal(ECardSize.Large, jetbike.Size);
        Assert.Equal(EInventorySection.Hand, jetbike.Section);
        Assert.Contains(run.HandGrid.Cards, c => c.InstanceId == jetbike.InstanceId);
        Assert.True(run.HandGrid.ValidateInvariants(out string handErr), handErr);
        Assert.True(run.StashGrid.ValidateInvariants(out string stashErr), stashErr);

        // GameSimEventCardTransformed 与邻卡 CardMoved / SimUpdateCard 必须 100% 匹配最终坐标
        var trEvt = trOut.Events.OfType<GameSimEventCardTransformed>().Single();
        Assert.Equal(oldJetpackId, trEvt.OriginalInstanceId);
        var trInfo = Assert.Single(trEvt.TransformedCards);
        Assert.Equal(jetbike.InstanceId, trInfo.InstanceId);
        Assert.Equal(jetbike.Section, trInfo.Section);
        Assert.Equal(jetbike.Socket, trInfo.Socket);
        var simTr = run.BuildFullGameSim(trOut.Events);
        var snapTr = run.BuildFullGameSim();
        Assert.Equal(jetbike.Socket, simTr.Cards[jetbike.InstanceId].Placement!.Socket);
        Assert.Contains(trOut.Events, e => e is GameSimEventCardMoved m && m.InstanceId == cutlass.InstanceId && m.ToSocket == cutlass.Socket);
        Assert.Contains(trOut.Events, e => e is GameSimEventCardMoved m && m.InstanceId == tape.InstanceId && m.ToSocket == tape.Socket);
        Assert.Null(simTr.Cards[cutlass.InstanceId].Placement);
        Assert.Null(simTr.Cards[tape.InstanceId].Placement);
        Assert.Equal(cutlass.Socket, snapTr.Cards[cutlass.InstanceId].Placement!.Socket);
        Assert.Equal(tape.Socket, snapTr.Cards[tape.InstanceId].Placement!.Socket);
        Assert.Contains(engine.BoardAuditLogs, l => l.Contains("[BoardAudit] op=Transform", StringComparison.Ordinal));

        // 4b. 进入后续野怪战斗：Jetbike 必须正常进入 Combat 且不丢失、战后依然保留在 HandGrid
        while (run.State != ERunState.Choice)
        {
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        }
        var mobOpt = InjectChoiceEncounter(run, ov.Pool, FangedIngletCombatId, ECardType.CombatEncounter);
        var combatOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) });
        Assert.Null(combatOut.Error);
        Assert.NotNull(combatOut.Combat);
        Assert.True(run.Cards.ContainsKey(jetbike.InstanceId));
        Assert.Contains(run.HandGrid.Cards, c => c.InstanceId == jetbike.InstanceId);
        Assert.Contains(combatOut.CombatSpawnSim!.Cards, kv => kv.Key == jetbike.InstanceId && kv.Value.Placement?.Section == EInventorySection.Hand);

        // 4c. 极窄 Hand (Lv1 4格填满) 中小变大 -> 通过 OutOfCombatController / ApplyOutOfCombatBatch 溢入 Stash，绝不产生幽灵卡或重叠
        var (run2, engine2) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        var jp2 = AddOwnedItem(run2, ov.Repo, ov.Pool, JetpackId, EInventorySection.Hand, slot: 3);
        var ct2 = AddOwnedItem(run2, ov.Repo, ov.Pool, CutlassId, EInventorySection.Hand, slot: 4);
        var tp2 = AddOwnedItem(run2, ov.Repo, ov.Pool, DuctTapeId, EInventorySection.Hand, slot: 6);
        var triggerSell = AddOwnedItem(run2, ov.Repo, ov.Pool, sellTrId, EInventorySection.Stash, slot: 0);
        var sellTrOut = engine2.Handle(new SellCardCommand { InstanceId = new InstanceId(triggerSell.InstanceId) });
        Assert.Null(sellTrOut.Error);

        var jetbike2 = run2.Cards.Values.Single(c => c.TemplateId == Guid.Parse(JetbikeId));
        Assert.Equal(EInventorySection.Stash, jetbike2.Section);
        Assert.Contains(run2.StashGrid.Cards, c => c.InstanceId == jetbike2.InstanceId);
        Assert.False(run2.Cards.ContainsKey(jp2.InstanceId));
        Assert.True(run2.HandGrid.ValidateInvariants(out string e4), e4);
        Assert.True(run2.StashGrid.ValidateInvariants(out string e5), e5);
        Assert.All(run2.Cards.Values.Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item), entry =>
        {
            BoardGrid g = entry.Section == EInventorySection.Stash ? run2.StashGrid : run2.HandGrid;
            Assert.Contains(g.Cards, c => c.InstanceId == entry.InstanceId && (EContainerSocketId)c.StartSlot == entry.Socket);
        });
        var tr2Evt = sellTrOut.Events.OfType<GameSimEventCardTransformed>().Single();
        var tr2Info = Assert.Single(tr2Evt.TransformedCards);
        Assert.Equal(EInventorySection.Stash, tr2Info.Section);
        Assert.Equal(jetbike2.Socket, tr2Info.Socket);
        Assert.Contains(engine2.BoardAuditLogs, l => l.Contains("[BoardAudit] op=Transform", StringComparison.Ordinal));
    }

    // ── 5. BoardGrid 单真值：ReconcileAndVerifyBoardInvariants 修复注入重叠与幽灵卡 ──

    [Fact]
    public void V9_05_ReconcileAndVerifyBoardInvariants_RepairsOverlapAndGhostCard()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        var c1 = AddOwnedItem(run, fx.Repo, fx.Pool, CutlassId, EInventorySection.Hand, slot: 3);
        // 人工构造一个未进入 HandGrid 且坐标冲突的“幽灵”Owned 物品，再错改 c1.Section 制造不同步
        var ghostTpl = fx.Repo.GetCard(DuctTapeId)!;
        var ghostCore = new CardInstance(ghostTpl, CardTier.Bronze, null, "ghost_tape") { StartSlot = 3 };
        var ghostEntry = new RunState.CardEntry
        {
            InstanceId = ghostCore.InstanceId,
            TemplateId = Guid.Parse(DuctTapeId),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = ghostCore,
        };
        run.Cards[ghostEntry.InstanceId] = ghostEntry;
        c1.Section = EInventorySection.Stash; // 刻意制造坐标与网格归属不一致

        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var events = engine.ReconcileAndVerifyBoardInvariants("UnitAuditRepair", dirty);

        Assert.True(run.HandGrid.ValidateInvariants(out string hErr), hErr);
        Assert.True(run.StashGrid.ValidateInvariants(out string sErr), sErr);
        Assert.Equal(EInventorySection.Hand, c1.Section);
        Assert.Equal((EContainerSocketId)c1.CoreCard!.StartSlot, c1.Socket);
        Assert.True(run.HandGrid.Cards.Any(c => c.InstanceId == ghostEntry.InstanceId) ||
                    run.StashGrid.Cards.Any(c => c.InstanceId == ghostEntry.InstanceId));
        Assert.Contains(ghostEntry.InstanceId, dirty);
        Assert.NotEmpty(events.OfType<GameSimEventCardMoved>());
    }

    // ── 6. #18 & #32a：全非战状态、全物品（含 SellPrice=0 / 附魔战利品 / 曾冰冻）均可出售 ──

    [Fact]
    public void V9_06_SellItems_WorksInAllNonCombatStates_IncludingZeroPriceEnchantedAndFrozen()
    {
        const string shopId = "90900003-0001-4001-8001-000000000001";
        string shopJson = BuildShopJson(shopId, "V9 SellCheck Shop", new[] { RedGumballId });
        using var ov = new OverlayContext(new[] { (shopId, shopJson) });
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        // 准备 5 件待售物品：含 SellPrice=0、附魔战利品、残留 Freeze 属性、网格Section不同步、普通卡
        var itemChoiceZero = AddOwnedItem(run, ov.Repo, ov.Pool, EagleTalismanLootId, EInventorySection.Hand, slot: 0);
        itemChoiceZero.CoreCard!.PermanentAdd["SellPrice"] = -999;
        itemChoiceZero.Attributes[ECardAttributeType.SellPrice] = 0;

        var itemShopEnchanted = AddOwnedItem(run, ov.Repo, ov.Pool, EagleTalismanLootId, EInventorySection.Stash, slot: 0);
        itemShopEnchanted.CoreCard!.ApplyEnchantment("Heavy");
        itemShopEnchanted.Enchantment = EEnchantmentType.Heavy;
        itemShopEnchanted.Attributes[ECardAttributeType.SellPrice] = 0;

        var itemPedestalFrozen = AddOwnedItem(run, ov.Repo, ov.Pool, DuctTapeId, EInventorySection.Hand, slot: 1);
        itemPedestalFrozen.CoreCard!.FreezeMs = 3500;
        itemPedestalFrozen.Attributes[ECardAttributeType.Freeze] = 3500;
        itemPedestalFrozen.Section = EInventorySection.Stash; // 刻意制造 Section 与 HandGrid 失步

        var itemLevelUp = AddOwnedItem(run, ov.Repo, ov.Pool, RedGumballId, EInventorySection.Stash, slot: 2);
        var itemLoot = AddOwnedItem(run, ov.Repo, ov.Pool, RedGumballId, EInventorySection.Hand, slot: 2);
        var combatWinnerWeapon = AddOwnedItem(run, ov.Repo, ov.Pool, CutlassId, EInventorySection.Hand, slot: 4, ETier.Gold);

        // State 1: Choice 下卖 0 售价战利品
        Assert.Equal(ERunState.Choice, run.State);
        var s1 = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(itemChoiceZero.InstanceId) });
        Assert.Null(s1.Error);
        Assert.False(run.Cards.ContainsKey(itemChoiceZero.InstanceId));
        Assert.Contains(s1.Events, e => e is GameSimEventCardSold cs && cs.InstanceId == itemChoiceZero.InstanceId);

        // State 2: Encounter (Shop) 下卖附魔 0 售价背包物品
        var shopOpt = InjectChoiceEncounter(run, ov.Pool, shopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopOpt.InstanceId) }).Error);
        Assert.Equal(ERunState.Encounter, run.State);
        var s2 = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(itemShopEnchanted.InstanceId) });
        Assert.Null(s2.Error);
        Assert.False(run.Cards.ContainsKey(itemShopEnchanted.InstanceId));

        // 退出回 Choice 并获取经验准备 LevelUp；先测 Pedestal
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        var upgradePedTpl = ov.Pool.OfType("PedestalEncounter")
            .First(e => e.Id.StartsWith("693af45b", StringComparison.OrdinalIgnoreCase));
        var pedOpt = InjectChoiceEncounter(run, ov.Pool, upgradePedTpl.Id, ECardType.PedestalEncounter);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pedOpt.InstanceId) }).Error);
        Assert.Equal(ERunState.Pedestal, run.State);

        // State 3: Pedestal 下卖曾冰冻且 Section 曾失步的物品
        var s3 = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(itemPedestalFrozen.InstanceId) });
        Assert.Null(s3.Error);
        Assert.False(run.Cards.ContainsKey(itemPedestalFrozen.InstanceId));

        // State 4: LevelUp 下卖背包物品
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "exp", "10" } }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(ERunState.LevelUp, run.State);
        var s4 = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(itemLevelUp.InstanceId) });
        Assert.Null(s4.Error);
        Assert.False(run.Cards.ContainsKey(itemLevelUp.InstanceId));
        var lvlPick = run.SelectionSet.Select(id => run.Cards[id]).First();
        if (lvlPick.Type == ECardType.Skill)
            Assert.Null(engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(lvlPick.InstanceId) }).Error);
        else if (lvlPick.Type is ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.EncounterStep)
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(lvlPick.InstanceId) }).Error);
        else
            Assert.Null(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(lvlPick.InstanceId), Section = EInventorySection.Hand }).Error);
        while (run.State != ERunState.Choice)
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

        // State 5: Loot 下卖场上物品
        var mobOpt = InjectChoiceEncounter(run, ov.Pool, FangedIngletCombatId, ECardType.CombatEncounter);
        var cbtOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) });
        Assert.Null(cbtOut.Error);
        Assert.Equal(ERunState.Loot, run.State);
        Assert.True(run.Cards.ContainsKey(combatWinnerWeapon.InstanceId));
        var s5 = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(itemLoot.InstanceId) });
        Assert.Null(s5.Error);
        Assert.False(run.Cards.ContainsKey(itemLoot.InstanceId));
        Assert.Contains(engine.BoardAuditLogs, l => l.Contains("[BoardAudit] op=Sell", StringComparison.Ordinal));
    }

    // ── 7. #6 & #27：战后清除 Freeze/Slow/Haste/Flying 并在战后首帧显式发 0 值 Delta ──

    [Fact]
    public void V9_07_PostCombat_ClearsTransientStatusAndFlying_WithExplicitZeroDeltaInFirstFrame()
    {
        const string statusItemId = "90900004-0001-4001-8001-000000000001";
        const string nukeId = "90900004-0001-4001-8001-000000000002";
        const string innateFlyerId = "90900004-0001-4001-8001-000000000003";

        using var ov = new OverlayContext(new[]
        {
            (statusItemId, BuildCombatStatusItemJson(statusItemId)),
            (nukeId, BuildFastNukeWeaponJson(nukeId)),
            (innateFlyerId, BuildInnateFlyerItemJson(innateFlyerId)),
        });

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        var statusCard = AddOwnedItem(run, ov.Repo, ov.Pool, statusItemId, EInventorySection.Hand, slot: 0);
        var nukeCard = AddOwnedItem(run, ov.Repo, ov.Pool, nukeId, EInventorySection.Hand, slot: 2);
        var innateFlyer = AddOwnedItem(run, ov.Repo, ov.Pool, innateFlyerId, EInventorySection.Hand, slot: 4);
        var dirtyStashCard = AddOwnedItem(run, ov.Repo, ov.Pool, DuctTapeId, EInventorySection.Stash, slot: 0);
        dirtyStashCard.CoreCard!.FreezeMs = 2000;
        dirtyStashCard.CoreCard.SlowMs = 1500;
        dirtyStashCard.CoreCard.HasteMs = 1000;
        dirtyStashCard.CoreCard.IsFlying = true;
        dirtyStashCard.Attributes[ECardAttributeType.Freeze] = 2000;
        dirtyStashCard.Attributes[ECardAttributeType.Flying] = 1;

        var mobOpt = InjectChoiceEncounter(run, ov.Pool, FangedIngletCombatId, ECardType.CombatEncounter);
        var combatOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) });
        Assert.Null(combatOut.Error);
        Assert.NotNull(combatOut.Combat);
        Assert.NotNull(combatOut.CombatDespawnSim);
        Assert.Equal(ECombatantId.Player, combatOut.Combat!.Winner);

        // 验证战斗帧里 statusCard 确实触发了飞行与冰冻
        var statusInstId = new InstanceId(statusCard.InstanceId);
        Assert.Contains(combatOut.Combat.Frames, f =>
            f.CardUpdates != null &&
            f.CardUpdates.TryGetValue(statusInstId, out var cu) &&
            cu.Attributes.TryGetValue(ECardAttributeType.Flying, out var fl) && fl.CurrentValue == 1);
        Assert.Contains(combatOut.Combat.Frames, f =>
            f.CardUpdates != null &&
            f.CardUpdates.TryGetValue(statusInstId, out var cu) &&
            cu.Attributes.TryGetValue(ECardAttributeType.Freeze, out var fr) && fr.CurrentValue > 0);

        // 战后 CoreCard 与永久属性必须全部清零，而固有 Flying=1 必须保留
        Assert.Equal(0, statusCard.CoreCard!.FreezeMs);
        Assert.Equal(0, statusCard.CoreCard.SlowMs);
        Assert.Equal(0, statusCard.CoreCard.HasteMs);
        Assert.False(statusCard.CoreCard.IsFlying);
        Assert.Equal(0, dirtyStashCard.CoreCard!.FreezeMs);
        Assert.False(dirtyStashCard.CoreCard.IsFlying);
        Assert.Equal(1, innateFlyer.Attributes.GetValueOrDefault(ECardAttributeType.Flying, 0));

        // 关键：战后首帧 NetMessageGameSim (CombatDespawnSim.Cards) 必须显式包含 Update=0 增量，使客户端 ReplayState 清零缓存！
        var despawnStatusSim = combatOut.CombatDespawnSim!.Cards[statusCard.InstanceId];
        foreach (var attr in new[] { ECardAttributeType.Freeze, ECardAttributeType.Slow, ECardAttributeType.Haste, ECardAttributeType.Flying })
        {
            Assert.True(despawnStatusSim.Attributes.TryGetValue(attr, out var d), $"战后首帧缺少 {attr} 显式归零 Delta");
            Assert.Equal(EAttributeDeltaType.Update, d!.DeltaType);
            Assert.Equal(0, d.Value);
        }

        var despawnStashSim = combatOut.CombatDespawnSim.Cards[dirtyStashCard.InstanceId];
        Assert.Equal(0, despawnStashSim.Attributes[ECardAttributeType.Freeze].Value);
        Assert.Equal(0, despawnStashSim.Attributes[ECardAttributeType.Flying].Value);
    }

    // ── 8. #19 & #24：ActiveEncounterCardInstanceId 与静态模板 CurrentEncounterId 全链路保持 ──

    [Fact]
    public void V9_08_EncounterPortrait_PreservesActiveEncounterCardAcrossStatesAndSnapshots()
    {
        const string stepId = "90900005-0001-4001-8001-000000000001";
        const string eventId = "90900005-0001-4001-8001-000000000002";
        string stepJson = BuildTransformLeftMostStepJson(stepId, JetbikeId);
        string eventJson = BuildStepEventJson(eventId, "V9 Portrait Event", new[] { stepId });
        using var ov = new OverlayContext(new[] { (stepId, stepJson), (eventId, eventJson) });

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        var encCard = InjectChoiceEncounter(run, ov.Pool, eventId, ECardType.EventEncounter);
        string[] otherChoiceIds = run.SelectionSet.Where(id => id != encCard.InstanceId).ToArray();

        // 8a. 选择 EventEncounter：不得销毁选中卡，必须保留于 ActiveEncounterCardInstanceId + Socket_5
        var enterOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(encCard.InstanceId) });
        Assert.Null(enterOut.Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(eventId, run.CurrentEncounterId);
        Assert.Equal(encCard.InstanceId, run.ActiveEncounterCardInstanceId);
        Assert.True(run.Cards.ContainsKey(encCard.InstanceId));
        Assert.Equal(EContainerSocketId.Socket_5, encCard.Socket);
        Assert.Null(encCard.Section);
        Assert.Null(encCard.Owner);
        Assert.DoesNotContain(enterOut.Events.OfType<GameSimEventCardDisposed>(), d => d.InstanceId == encCard.InstanceId);
        foreach (string otherId in otherChoiceIds)
            Assert.Contains(enterOut.Events.OfType<GameSimEventCardDisposed>(), d => d.InstanceId == otherId);

        Assert.Contains(enterOut.Events, e =>
            e is GameSimEventCardPurchased cp &&
            cp.InstanceId == encCard.InstanceId &&
            cp.LeftSocketId == EContainerSocketId.Socket_5 &&
            cp.Section == null &&
            cp.CombatantId == null);
        Assert.Contains(enterOut.Events, e =>
            e is GameSimEventStateTransitioned st &&
            st.ToState == ERunState.Encounter &&
            string.Equals(st.CurrentEncounterId, eventId, StringComparison.OrdinalIgnoreCase));

        var simEnc = run.BuildFullGameSim(enterOut.Events);
        Assert.Equal(eventId, simEnc.CurrentState!.CurrentEncounterId);
        Assert.Equal(EContainerSocketId.Socket_5, simEnc.Cards[encCard.InstanceId].Placement!.Socket);

        // 8b. 快照持久化与恢复：ActiveEncounterCardInstanceId 完整保留且 BuildResumeEvents 不重复发 CardDealt
        string snapJson = RunSnapshotSerializer.Serialize(run, engine.StartSelectionDone, run.PendingLevelUp);
        var restored = RunSnapshotSerializer.Deserialize(snapJson, ov.Repo);
        Assert.Equal(encCard.InstanceId, restored.ActiveEncounterCardInstanceId);
        Assert.True(restored.Cards.ContainsKey(encCard.InstanceId));
        Assert.Equal(EContainerSocketId.Socket_5, restored.Cards[encCard.InstanceId].Socket);
        var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(restored);
        Assert.DoesNotContain(resumeEvents.OfType<GameSimEventCardDealt>(), d => d.InstanceId == encCard.InstanceId);

        // 8c. 战斗胜利进入 Loot：怪物遭遇卡同样保留在 Socket_5 直到离开 Loot 返回 Choice 才销毁
        var (runCbt, engCbt) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        AddOwnedItem(runCbt, ov.Repo, ov.Pool, CutlassId, EInventorySection.Hand, slot: 3, ETier.Gold);
        var mobOpt = InjectChoiceEncounter(runCbt, ov.Pool, FangedIngletCombatId, ECardType.CombatEncounter);
        var cbtOut = engCbt.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) });
        Assert.Null(cbtOut.Error);
        Assert.Equal(ERunState.Loot, runCbt.State);
        Assert.Equal(mobOpt.InstanceId, runCbt.ActiveEncounterCardInstanceId);
        Assert.True(runCbt.Cards.ContainsKey(mobOpt.InstanceId));
        Assert.Equal(EContainerSocketId.Socket_5, cbtOut.CombatDespawnSim!.Cards[mobOpt.InstanceId].Placement!.Socket);

        var exitLootOut = engCbt.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitLootOut.Error);
        Assert.False(runCbt.Cards.ContainsKey(mobOpt.InstanceId));
        Assert.Null(runCbt.ActiveEncounterCardInstanceId);
        Assert.Contains(exitLootOut.Events.OfType<GameSimEventCardDisposed>(), d => d.InstanceId == mobOpt.InstanceId);
    }

    // ── 9. #14：升级台进入时保留基座卡与玩家待升级物模板/品阶同步，悬停预览与升级提交正常 ──

    [Fact]
    public void V9_09_UpgradePedestal_PreservesPedestalCard_AndSyncsUpgradablePlayerCard()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        var cutlass = AddOwnedItem(run, fx.Repo, fx.Pool, CutlassId, EInventorySection.Hand, slot: 3, ETier.Bronze);
        var upgradePedTpl = fx.Pool.OfType("PedestalEncounter")
            .First(e => e.Id.StartsWith("693af45b", StringComparison.OrdinalIgnoreCase));

        var pedOpt = InjectChoiceEncounter(run, fx.Pool, upgradePedTpl.Id, ECardType.PedestalEncounter);
        var enterPed = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pedOpt.InstanceId) });
        Assert.Null(enterPed.Error);

        Assert.Equal(ERunState.Pedestal, run.State);
        Assert.Equal(upgradePedTpl.Id, run.CurrentEncounterId, ignoreCase: true);
        Assert.Equal(pedOpt.InstanceId, run.ActiveEncounterCardInstanceId);
        Assert.True(run.Cards.ContainsKey(pedOpt.InstanceId));

        // 校验 NetMessageGameSim 中 Pedestal 卡（Socket_5）与玩家 Cutlass 的品阶/Placement 均完整下发供客户端 TooltipParentComponent 悬停预览
        var simPed = run.BuildFullGameSim(enterPed.Events);
        Assert.Equal(upgradePedTpl.Id, simPed.CurrentState!.CurrentEncounterId, ignoreCase: true);
        Assert.True(simPed.Cards.TryGetValue(pedOpt.InstanceId, out var pedSimCard));
        Assert.Equal(EContainerSocketId.Socket_5, pedSimCard!.Placement!.Socket);
        Assert.Equal(ETier.Bronze, simPed.Cards[cutlass.InstanceId].Tier);

        var commitOut = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(cutlass.InstanceId) });
        Assert.Null(commitOut.Error);
        Assert.Equal(ETier.Silver, cutlass.Tier);
        Assert.Contains(commitOut.Events, e => e is GameSimEventCardUpgraded upg && upg.InstanceId == cutlass.InstanceId && upg.NewTier == ETier.Silver);
        Assert.True(run.HandGrid.ValidateInvariants(out string err), err);
    }
}
