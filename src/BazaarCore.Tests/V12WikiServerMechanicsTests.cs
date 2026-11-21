using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using BazaarGameShared.TempoNet.Enums;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wiki / BazaarDB B 组（#42–#52 局外日程、经济、商店、升级掉落、奇遇事件 + #37 卖出音符 + #38 局外造访任务）
/// 全量真值对照与服务端回归验证套件（全数直读 GameData.db + RunEngine 黑盒闭环）。
/// </summary>
public sealed class V12WikiServerMechanicsTests
{
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string LighterId = "3d212e22-12eb-427e-ab96-e7baf1229a7b";
    private const string BayonetId = "6146cacd-202c-489f-88c0-f77ef3e0fda6";
    private const string SizeinatorId = "7b30db1a-80c8-4889-ae2f-12b0ad047796";
    private const string DepletedDeviceId = "021639c0-4a9c-49d8-8254-6376994823e2";
    private const string AilaMerchantId = "dc12f4bd-6c33-41f0-b7ad-d62a0be09a47";
    private const string NufuMerchantId = "210003fa-a20d-4ad1-9ada-d5fad5bf9f89";
    private const string ZurphinsSafariId = "b81a1c81-0a16-462a-8247-04653354b79d";
    private const string GoldieMerchantId = "1f72700a-fe11-4792-8a47-9e52b9387f29";
    private const string LuxeMerchantId = "892de564-d894-4e07-ab21-959e16e8cdf8";
    private const string PolNormalId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string PolPygmalienId = "bdf0a9ab-8bc2-4f1b-8f7d-614293ac2db2";
    private const string PolPackageSmallId = "103282bc-65d8-4888-9ccb-d106c1b1d5a7";
    private const string ArmsDealerSkillId = "560a532c-73f4-4edd-b197-0127e2ca4cc9";

    private const string FuturaEventId = "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
    private const string InspiredByKarnokStepId = "0c71aef1-c049-483a-b9a0-19c5005b4fc7";
    private const string InspiredRageSkillId = "0beccea0-bb9b-444b-b063-bf6aeadaddc8";
    private const string KarnoksRageSkillId = "1ac7830c-8506-4e7f-a1b6-8ba302f6a8fe";
    private const string MacheteId = "05a83727-cfd8-4f9b-a109-e720714de70b";

    private const string TravelAgentEventId = "002d8f6d-0e36-4921-89ed-3f0f28682d7b";
    private const string TravelAgentSurpriseMeId = "32dd98de-811c-45bc-98ab-9e416ccefba7";
    private const string TravelAgentBrowseAllId = "cb0fd755-c9d9-4e2b-9096-de13c3773d30";
    private const string GrandOpeningEventId = "39e4aeb6-06f8-45af-a5d1-7e0ca01bd194";
    private const string CrashSiteTicketId = "5bd0a69d-420c-4cd0-93ec-07582c688a5b";
    private const string TempleTicketId = "77c8ef2d-62c8-4214-993d-648915c93e83";
    private const string TinyWorldTicketId = "24dc340b-bb3d-4c9b-8715-a807c0c06a8c";

    private const string ThievesGuildMedallionId = "1e48bd28-cd41-49e9-9e22-b44da30ef6be";
    private const string ThievesGuildEventId = "e792b6a6-2f85-4190-8e3c-002d2736d03d";

    private const string TheCultEventId = "bf1594cc-7f65-4236-b95f-ed2f521739de";
    private const string JoinTheCultDooleyId = "0fc780eb-081d-46a4-a4b1-82e40d26d1a9";
    private const string JoinTheCultOtherId = "982b01a0-6f45-43bf-add2-4f963fc92e00";
    private const string RitualDaggerId = "6076200d-dc08-47a2-a7d2-dfa2e484422a";
    private const string CultMemberSkillId = "0f1c6082-c742-4d6a-bf11-12ded9a8bb29";
    private const string BluenanasId = "3890374d-4952-471d-b550-e230d0ce5343";
    private const string PygsGymId = "3d185981-0b60-47ce-9ed3-58be5d7b2737";

    private const string ShroudedFigureId = "818fcd1d-4cfd-4629-b83f-8d5e753dd8f4";
    private const string ShroudedPaidPowerStepId = "9729a91f-a43b-43bd-b5d2-0ea3e177b758";
    private const string ShroudedCultMemberStepId = "b8fc275e-7fa5-4380-9333-e41e3e4b233e";
    private const string MandalaEventId = "255ae0fa-f203-4e9f-855d-e090e6937b5b";
    private const string MandalaTransformStepId = "76e60ecc-3dbd-4b57-a7a5-8ad6d40f7abb";

    private const string ANoteId = "9f838069-1801-4fc7-8f7e-3195dbc606e2";
    private const string ANoteSocketId = "a9adbfdc-567e-4fe6-a9e3-7eded0bc1279";
    private const string EternalTorchId = "9b0c1d2e-3f4a-5b6c-7d8e-9f0a1b2c3d4e";
    private const string FrostTotemId = "1d2e3f4a-5b6c-7d8e-9f0a-1b2c3d4e5f6a";
    private const string AtlasId = "03842226-6605-466f-9ea3-a8a2dbea67e1";
    private const string ZosimaEventId = "054e937d-553e-41a5-951b-62550f870f93";
    private const string ApothecaryGoldEventId = "04ac552f-c292-455e-bf5f-438119dfa196";
    private const string YetarianTombLegendaryId = "04f879e5-94fe-42ae-aa7a-273edbd6d410";
    private const string SatchelId = "3ce7312c-aa95-4711-9a2b-827609a380b4";
    private const string PremiumPigglesId = "0c33ab49-364e-4830-8e3f-773f315b3928";

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
        string? dataDir = Environment.GetEnvironmentVariable("BAZAAR_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir) || !File.Exists(Path.Combine(dataDir, "GameData.db")))
        {
            string? probe = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(probe); i++, probe = Directory.GetParent(probe)?.FullName)
            {
                string candidate = Path.Combine(probe, "dist", "data");
                if (File.Exists(Path.Combine(candidate, "GameData.db")) &&
                    new FileInfo(Path.Combine(candidate, "GameData.db")).Length > 1024 * 1024)
                {
                    dataDir = candidate;
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(dataDir) || !File.Exists(Path.Combine(dataDir, "GameData.db")))
            dataDir = @"e:\thebazaar\dist\data";

        string dbPath = Path.Combine(dataDir, "GameData.db");
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
        };
    });

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        EHero hero = EHero.Vanessa,
        EPlayMode playMode = EPlayMode.Unranked,
        CustomRunRuleSet? rules = null,
        int seed = 4201)
    {
        var fx = RealFixture.Value;
        var run = new RunState
        {
            RunId = "v12_wk_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = playMode,
            RngSeed = seed,
            CustomRules = rules ?? new CustomRunRuleSet(),
        };
        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);
        DrainNonChoiceStatesToChoice(engine, run, skipLevelUpChoices: true);
        return (run, engine);
    }

    private static void DrainNonChoiceStatesToChoice(
        RunEngine engine,
        RunState run,
        bool skipLevelUpChoices = false)
    {
        int safety = 0;
        while (run.State is not (ERunState.Choice or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown))
        {
            Assert.True(safety++ < 40, $"遭遇状态未收敛至 Choice: Day={run.Day} Hour={run.Hour} State={run.State} Enc={run.CurrentEncounterId}");
            if (run.SelectionRules.CanExit && (skipLevelUpChoices || run.State != ERunState.LevelUp || run.SelectionSet.Count == 0))
            {
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                continue;
            }
            if (run.SelectionSet.Count > 0)
            {
                bool handled = false;
                foreach (string cid in run.SelectionSet.ToList())
                {
                    if (!run.Cards.TryGetValue(cid, out var cand)) continue;
                    INetCommand cmd = cand.Type switch
                    {
                        ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter
                            => new SelectEncounterCommand { InstanceId = new InstanceId(cand.InstanceId) },
                        ECardType.Skill
                            => new SelectSkillCommand { InstanceId = new InstanceId(cand.InstanceId) },
                        _ => new SelectItemCommand { InstanceId = new InstanceId(cand.InstanceId) },
                    };
                    if (engine.Handle(cmd).Error == null)
                    {
                        handled = true;
                        break;
                    }
                }
                if (handled) continue;
            }
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        }
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        string templateId,
        EInventorySection section,
        int slot,
        ETier tier = ETier.Bronze,
        string? enchantment = null,
        bool tiny = false)
    {
        var fx = RealFixture.Value;
        var coreTpl = fx.Repo.GetCard(templateId) ?? throw new InvalidOperationException($"缺失模板 {templateId}");
        var poolTpl = fx.Pool.Get(templateId);
        var coreCard = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), enchantment, "wk_" + Guid.NewGuid().ToString("N")[..8])
        {
            StartSlot = slot,
        };
        if (tiny)
            coreCard.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);

        if (section == EInventorySection.Stash)
            Assert.True(run.StashGrid.TryPlaceOrShift(coreCard, slot));
        else
            Assert.True(run.HandGrid.TryPlaceOrShift(coreCard, slot));

        EEnchantmentType? enchEnum = !string.IsNullOrEmpty(enchantment) && Enum.TryParse<EEnchantmentType>(enchantment, true, out var pe)
            ? pe
            : null;
        var entry = new RunState.CardEntry
        {
            InstanceId = coreCard.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ShopEngine.ParseCardType(coreTpl.Type),
            Size = ShopEngine.ParseSize(coreCard.EffectiveSize.ToString()),
            Tier = tier,
            Enchantment = enchEnum,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)Math.Clamp(coreCard.StartSlot, 0, 9),
            CoreCard = coreCard,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, fx.Repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static RunState.CardEntry AddOwnedSkill(RunState run, string templateId, ETier tier = ETier.Bronze)
    {
        var fx = RealFixture.Value;
        var coreTpl = fx.Repo.GetCard(templateId) ?? throw new InvalidOperationException($"缺失技能模板 {templateId}");
        var poolTpl = fx.Pool.Get(templateId);
        var coreCard = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), null, "wksk_" + Guid.NewGuid().ToString("N")[..8]);
        var entry = new RunState.CardEntry
        {
            InstanceId = coreCard.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Skill,
            Size = ECardSize.Medium,
            Tier = tier,
            Owner = ECombatantId.Player,
            CoreCard = coreCard,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, fx.Repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static RunState.CardEntry InjectChoiceEncounter(RunState run, string encounterId)
    {
        var fx = RealFixture.Value;
        var tpl = fx.Pool.Get(encounterId) ?? throw new InvalidOperationException($"缺失遭遇模板 {encounterId}");
        string instId = "inj_" + Guid.NewGuid().ToString("N")[..8];
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(encounterId),
            Type = ShopEngine.ParseCardType(tpl.Type),
            Size = ShopEngine.ParseSize(tpl.Size),
            Tier = ShopEngine.ParseTier(tpl.StartingTier),
            Owner = null,
            Section = null,
            Socket = null,
        };
        RunState.PopulateMetaFromTemplate(entry, tpl, fx.Repo.GetCard(encounterId));
        run.Cards[instId] = entry;
        run.SelectionSet.Add(instId);
        return entry;
    }

    [Fact(DisplayName = "Wiki42_Schedule_Xp_Victories_And_4_7_10_Chests")]
    public void Wiki42_Schedule_Xp_Victories_And_4_7_10_Chests()
    {
        var fx = RealFixture.Value;
        Assert.Equal(6, fx.Mode.HoursInADay);
        Assert.Equal(8, fx.Mode.ExperiencePerLevel);
        Assert.Equal(1, fx.Mode.ExperiencePerHour);
        Assert.Equal(10, fx.Mode.VictoriesToWin);
        Assert.Equal(2, fx.Mode.NumCarpetSocketsUnlockPerLevel);

        Assert.Equal(0, RunEngine.ComputeRunChestCount(EPlayMode.Ranked, 3));
        Assert.Equal(1, RunEngine.ComputeRunChestCount(EPlayMode.Ranked, 4));
        Assert.Equal(2, RunEngine.ComputeRunChestCount(EPlayMode.Ranked, 7));
        Assert.Equal(3, RunEngine.ComputeRunChestCount(EPlayMode.Ranked, 10));
        Assert.Equal(0, RunEngine.ComputeRunChestCount(EPlayMode.Unranked, 3));
        Assert.Equal(1, RunEngine.ComputeRunChestCount(EPlayMode.Unranked, 4));
        Assert.Equal(1, RunEngine.ComputeRunChestCount(EPlayMode.Unranked, 7));
        Assert.Equal(1, RunEngine.ComputeRunChestCount(EPlayMode.Unranked, 10));
        Assert.Equal(0, RunEngine.ComputeRunChestCount(EPlayMode.Ranked, 10, isConceded: true));

        // 验证 AccountStore.RecordRunCompleted 对 4/7/10 胜均按阶梯累加 ChestsEarned
        string tempDir = Path.Combine(Path.GetTempPath(), "bazaar_wiki42_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "bazaar.db");
        try
        {
            var store = new AccountStore(new Persistence(dbPath));
            var acc = store.GetOrCreateByName("wiki42_chest_tester");
            store.RecordRunCompleted(acc, "r4", EHero.Vanessa, EPlayMode.Ranked, 6, victories: 4, defeats: 3, prestige: 0, ERunState.EndRunDefeat);
            store.RecordRunCompleted(acc, "r7", EHero.Vanessa, EPlayMode.Ranked, 9, victories: 7, defeats: 2, prestige: 0, ERunState.EndRunDefeat);
            store.RecordRunCompleted(acc, "r10", EHero.Vanessa, EPlayMode.Ranked, 11, victories: 10, defeats: 1, prestige: 12, ERunState.EndRunVictory);
            Assert.Equal(6, acc.HeroStats["Vanessa"].ChestsEarned);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }

        // 端到端 Ranked 4 胜战败时 CompleteRunResult 携带 1 宝箱（绝不再是 0）；10 胜携带 3 宝箱
        var rulesDefeat4 = new CustomRunRuleSet { StartingHealth = 1, StartingPrestige = 1 };
        var (runDefeat4, engDefeat4) = CreateStartedEngine(playMode: EPlayMode.Ranked, rules: rulesDefeat4, seed: 4202);
        runDefeat4.Victories = 4;
        runDefeat4.FatesUsed = true;
        // 直接推进到 Day 1 Hour 6 PvP 并以 1HP 落败
        while (runDefeat4.State != ERunState.EndRunDefeat)
        {
            if (runDefeat4.State == ERunState.Choice)
            {
                var p = runDefeat4.SelectionSet.Select(id => runDefeat4.Cards[id]).FirstOrDefault(c => c.Type == ECardType.PvpEncounter)
                     ?? runDefeat4.SelectionSet.Select(id => runDefeat4.Cards[id]).First();
                var res = engDefeat4.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(p.InstanceId) });
                Assert.Null(res.Error);
                var doneEvt = res.CombatDespawnSim?.Events.OfType<GameSimEventRunCompleted>().FirstOrDefault()
                           ?? res.Events.OfType<GameSimEventRunCompleted>().FirstOrDefault();
                if (doneEvt != null)
                {
                    Assert.Equal(1, doneEvt.Rewards.ChestCount);
                    break;
                }
            }
            DrainNonChoiceStatesToChoice(engDefeat4, runDefeat4, skipLevelUpChoices: true);
        }
        Assert.Equal(ERunState.EndRunDefeat, runDefeat4.State);
    }

    [Fact(DisplayName = "Wiki43_Prestige_And_Fates_ReviveOnce_Futura")]
    public void Wiki43_Prestige_And_Fates_ReviveOnce_Futura()
    {
        var rules = new CustomRunRuleSet { StartingHealth = 1, StartingPrestige = 20 };
        var (run, engine) = CreateStartedEngine(rules: rules, seed: 4301);
        Assert.Equal(20, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0));

        bool sawFatesRevive = false;
        int day1LossDay = 0;
        int safety = 0;
        while (run.State != ERunState.EndRunDefeat && safety++ < 80)
        {
            if (run.State == ERunState.Choice)
            {
                var pvp = run.SelectionSet.Select(id => run.Cards[id]).FirstOrDefault(c => c.Type == ECardType.PvpEncounter);
                var pick = pvp ?? run.SelectionSet.Select(id => run.Cards[id]).First();
                int preBefore = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0);
                uint dayAtFight = run.Day;
                var outRes = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pick.InstanceId) });
                Assert.Null(outRes.Error);
                if (pvp != null && outRes.Combat?.Winner != ECombatantId.Player)
                {
                    int preAfter = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0);
                    if (!sawFatesRevive && preBefore - (int)dayAtFight <= 0)
                    {
                        sawFatesRevive = true;
                        Assert.True(run.FatesUsed);
                        Assert.Equal(1, preAfter);
                        Assert.Equal(ERunState.Encounter, run.State);
                        Assert.Equal(FuturaEventId, run.CurrentEncounterId);
                        Assert.Equal(3, run.SelectionSet.Count);
                    }
                    else if (!sawFatesRevive)
                    {
                        day1LossDay++;
                        Assert.Equal(preBefore - (int)dayAtFight, preAfter);
                    }
                }
            }
            DrainNonChoiceStatesToChoice(engine, run, skipLevelUpChoices: true);
        }

        Assert.True(day1LossDay >= 1);
        Assert.True(sawFatesRevive);
        Assert.True(run.FatesUsed);
        Assert.Equal(ERunState.EndRunDefeat, run.State);
    }

    [Fact(DisplayName = "Wiki44_StandardPrices_SkillFloor_GoldenEnchant_And_SizeinatorOverride")]
    public void Wiki44_StandardPrices_SkillFloor_GoldenEnchant_And_SizeinatorOverride()
    {
        var fx = RealFixture.Value;
        var prices = fx.Repo.GameMode.StandardPrices;

        // 1. Standard Item Buy/Sell 矩阵
        (CardSize sz, CardTier tr, int buy, int sell)[] itemCases =
        [
            (CardSize.Small, CardTier.Bronze, 2, 1),
            (CardSize.Small, CardTier.Legendary, 24, 12),
            (CardSize.Medium, CardTier.Bronze, 4, 2),
            (CardSize.Medium, CardTier.Legendary, 48, 24),
            (CardSize.Large, CardTier.Bronze, 6, 3),
            (CardSize.Large, CardTier.Legendary, 64, 32),
        ];
        foreach (var (sz, tr, buy, sell) in itemCases)
        {
            Assert.Equal(buy, prices.GetItemBuyPrice(sz, tr));
            Assert.Equal(sell, prices.GetItemSellPrice(sz, tr));
        }

        // 2. Skill Buy/Sell 矩阵（Bronze 5/2 = 2 向下取整）
        Assert.Equal(5, prices.GetSkillBuyPrice(CardTier.Bronze));
        Assert.Equal(2, prices.GetSkillSellPrice(CardTier.Bronze));
        Assert.Equal(10, prices.GetSkillBuyPrice(CardTier.Silver));
        Assert.Equal(5, prices.GetSkillSellPrice(CardTier.Silver));
        Assert.Equal(40, prices.GetSkillBuyPrice(CardTier.Diamond));
        Assert.Equal(20, prices.GetSkillSellPrice(CardTier.Diamond));

        // 3. 附魔仅 Golden 翻倍 SellPrice；Sizeinator (Medium Diamond/Legendary) 保留 Sell=0，Buy 按标准 32/48
        var (run, engine) = CreateStartedEngine(rules: new CustomRunRuleSet { StartingGold = 200 }, seed: 4401);
        var shop = new ShopEngine(run, fx.Pool, fx.Repo);
        var cutlassTpl = fx.Repo.GetCard(CutlassId)!;
        var plain = shop.CreateEntryFromCoreInstance(new CardInstance(cutlassTpl, CardTier.Silver), isFree: false);
        var deadly = shop.CreateEntryFromCoreInstance(new CardInstance(cutlassTpl, CardTier.Silver, "Deadly"), isFree: false);
        var golden = shop.CreateEntryFromCoreInstance(new CardInstance(cutlassTpl, CardTier.Silver, "Golden"), isFree: false);
        Assert.Equal(8, plain.Attributes[ECardAttributeType.BuyPrice]);
        Assert.Equal(8, deadly.Attributes[ECardAttributeType.BuyPrice]);
        Assert.Equal(4, deadly.Attributes[ECardAttributeType.SellPrice]);
        Assert.Equal(8, golden.Attributes[ECardAttributeType.SellPrice]);

        var sizeinatorTpl = fx.Repo.GetCard(SizeinatorId)!;
        var szDia = shop.CreateEntryFromCoreInstance(new CardInstance(sizeinatorTpl, CardTier.Diamond), isFree: false);
        Assert.Equal(32, szDia.Attributes[ECardAttributeType.BuyPrice]);
        Assert.Equal(0, szDia.Attributes[ECardAttributeType.SellPrice]);

        // Sizeinator 重建 CoreCard（CoreCard = null 场景）绝不可写入负数 PermanentAdd["SellPrice"]
        var ownedSz = AddOwnedItem(run, SizeinatorId, EInventorySection.Hand, slot: 2, tier: ETier.Legendary);
        ownedSz.CoreCard = null;
        int goldBefore = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        var sellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(ownedSz.InstanceId) });
        Assert.Null(sellOut.Error);
        Assert.Equal(goldBefore, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0));
    }

    [Fact(DisplayName = "Wiki45_ShopTiersFollowDay_NotLevel_And_GoldieLuxeBehavior")]
    public void Wiki45_ShopTiersFollowDay_NotLevel_And_GoldieLuxeBehavior()
    {
        var fx = RealFixture.Value;
        var (run, _) = CreateStartedEngine(rules: new CustomRunRuleSet { StartingGold = 500 }, seed: 4501);
        run.Day = 1;
        run.PlayerAttributes[EPlayerAttributeType.Level] = 6; // 人为拉高 Level，Day 仍保持 1

        var shop = new ShopEngine(run, fx.Pool, fx.Repo);
        for (int i = 0; i < 8; i++)
        {
            shop.DealEncounterShelf(AilaMerchantId, isReroll: false, playerState: null);
            foreach (string id in run.SelectionSet)
            {
                var cand = run.Cards[id];
                Assert.Equal(ETier.Bronze, cand.Tier); // Day 1 表只出 Bronze（即使 Level=6）
            }
        }

        // Goldie (1f72700a -> 固定 Gold + IgnoreTierTable) 与 Luxe (892de564 -> 固定 Diamond + IgnoreTierTable) 即使在 Day 1 也绕过 Day 表定阶
        run.Day = 1;
        shop.DealEncounterShelf(GoldieMerchantId, isReroll: false, playerState: null);
        Assert.NotEmpty(run.SelectionSet);
        Assert.All(run.SelectionSet, id => Assert.Equal(ETier.Gold, run.Cards[id].Tier));

        shop.DealEncounterShelf(LuxeMerchantId, isReroll: false, playerState: null);
        Assert.NotEmpty(run.SelectionSet);
        Assert.All(run.SelectionSet, id => Assert.Equal(ETier.Diamond, run.Cards[id].Tier));

        var luxeTpl = fx.Repo.GetCard(LuxeMerchantId)!;
        var goldieTpl = fx.Repo.GetCard(GoldieMerchantId)!;
        Assert.Contains(goldieTpl.SelectionContext!.SpawnContext!.Behaviors ?? [], b => b.Type == "TSpawnBehaviorIgnoreTierTable");
        Assert.Contains(luxeTpl.SelectionContext!.SpawnContext!.Behaviors ?? [], b => b.Type == "TSpawnBehaviorIgnoreTierTable");
    }

    [Fact(DisplayName = "Wiki46_MerchantRerollCap_PolHeroSplit_And_OwnedMaxTiersForItemsAndSkills")]
    public void Wiki46_MerchantRerollCap_PolHeroSplit_And_OwnedMaxTiersForItemsAndSkills()
    {
        var fx = RealFixture.Value;

        // 1. Nufu & Zurphin's Safari（无 Merchant Tag）通过统一 IsMerchantEncounterTemplate 合法 Reroll 且严格受 MaxRerolls=1 限制
        var (runNufu, engNufu) = CreateStartedEngine(rules: new CustomRunRuleSet { StartingGold = 200 }, seed: 4601);
        runNufu.Day = 4;
        var ownedDiaSkill = AddOwnedSkill(runNufu, ArmsDealerSkillId, ETier.Diamond);
        var nufuCard = InjectChoiceEncounter(runNufu, NufuMerchantId);
        Assert.Null(engNufu.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(nufuCard.InstanceId) }).Error);
        Assert.Equal(1u, runNufu.RerollsRemaining);
        Assert.DoesNotContain(runNufu.SelectionSet, id => runNufu.Cards[id].TemplateId == ownedDiaSkill.TemplateId);

        var reroll1 = engNufu.Handle(new RerollCommand());
        Assert.Null(reroll1.Error);
        Assert.Equal(0u, runNufu.RerollsRemaining);
        Assert.DoesNotContain(runNufu.SelectionSet, id => runNufu.Cards[id].TemplateId == ownedDiaSkill.TemplateId);
        Assert.NotNull(engNufu.Handle(new RerollCommand()).Error);

        var (runZur, engZur) = CreateStartedEngine(rules: new CustomRunRuleSet { StartingGold = 200 }, seed: 4602);
        runZur.Day = 6;
        var zurCard = InjectChoiceEncounter(runZur, ZurphinsSafariId);
        Assert.Null(engZur.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(zurCard.InstanceId) }).Error);
        Assert.Equal(1u, runZur.RerollsRemaining);
        Assert.Null(engZur.Handle(new RerollCommand()).Error);
        Assert.NotNull(engZur.Handle(new RerollCommand()).Error);

        // 2. Diamond 物品（含 Stash）禁出
        var ownedDiaCutlass = AddOwnedItem(runNufu, CutlassId, EInventorySection.Stash, slot: 0, tier: ETier.Diamond);
        var shop = new ShopEngine(runNufu, fx.Pool, fx.Repo);
        var ownedMap = ShopEngine.BuildOwnedMaxTiers(runNufu);
        Assert.True(ownedMap.TryGetValue(CutlassId, out var cTier) && cTier == CardTier.Diamond);
        Assert.True(ownedMap.TryGetValue(ArmsDealerSkillId, out var sTier) && sTier == CardTier.Diamond);
        Assert.True(ShopEngine.IsBlockedByOwnedMaxTiers(CutlassId, CardTier.Diamond, ownedMap));
        Assert.True(ShopEngine.IsBlockedByOwnedMaxTiers(ArmsDealerSkillId, CardTier.Bronze, ownedMap));

        // 3. Pol 同英雄双池严格分离（Pygmalien 绝不出普通 Pol，Vanessa 绝不出 Pygmalien 专属 Pol）
        var (pygRun, _) = CreateStartedEngine(hero: EHero.Pygmalien, seed: 4603);
        pygRun.Day = 5;
        pygRun.Hour = 1;
        var pygShop = new ShopEngine(pygRun, fx.Pool, fx.Repo);
        for (int i = 0; i < 40; i++)
        {
            pygRun.VisitedDayEncounterIds.Clear();
            pygShop.DealHourEncounters();
            Assert.DoesNotContain(pygRun.SelectionSet, id => pygRun.Cards[id].TemplateId == Guid.Parse(PolNormalId));
        }

        var (vanRun, _) = CreateStartedEngine(hero: EHero.Vanessa, seed: 4604);
        vanRun.Day = 5;
        vanRun.Hour = 1;
        var vanShop = new ShopEngine(vanRun, fx.Pool, fx.Repo);
        for (int i = 0; i < 40; i++)
        {
            vanRun.VisitedDayEncounterIds.Clear();
            vanShop.DealHourEncounters();
            Assert.DoesNotContain(vanRun.SelectionSet, id => vanRun.Cards[id].TemplateId == Guid.Parse(PolPygmalienId));
        }
    }

    [Fact(DisplayName = "Wiki47_MonsterLootPool_SlotWeights_And_ZeroSellValuePolicy")]
    public void Wiki47_MonsterLootPool_SlotWeights_And_ZeroSellValuePolicy()
    {
        var fx = RealFixture.Value;
        var dupMonster = fx.Monsters.FirstOrDefault(m =>
            m.Items.GroupBy(i => i.TemplateId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() >= 2) &&
            m.Skills.Count >= 1);
        Assert.NotNull(dupMonster);

        var allLoot = dupMonster!.AllLootCandidates().ToList();
        var maxGroup = dupMonster.Items.GroupBy(i => i.TemplateId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First();
        Assert.Equal(maxGroup.Count(), allLoot.Count(c => c.TemplateId.Equals(maxGroup.Key, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(allLoot, c => dupMonster.Skills.Any(s => s.TemplateId.Equals(c.TemplateId, StringComparison.OrdinalIgnoreCase)));

        // Unsellable 卡绝不可被卖出或自动出售变现
        var (run, engine) = CreateStartedEngine(seed: 4701);
        var dep = AddOwnedItem(run, DepletedDeviceId, EInventorySection.Hand, slot: 2, tier: ETier.Diamond);
        Assert.NotNull(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(dep.InstanceId) }).Error);
    }

    [Fact(DisplayName = "Wiki48_ShopMonsterStepUpgradeFusion_TriggersEvents_And_IncludingStashScope")]
    public void Wiki48_ShopMonsterStepUpgradeFusion_TriggersEvents_And_IncludingStashScope()
    {
        var fx = RealFixture.Value;
        var potionId = fx.Repo.CardsById.Values
            .First(t => t.Type == "Item" && t.Tags.Contains("Potion", StringComparer.OrdinalIgnoreCase) &&
                        t.DefaultTier == CardTier.Bronze && t.Tiers != null && t.Tiers.ContainsKey("Silver"))
            .Id;

        // 1. 商店付费购买熔合触发 "When you buy or upgrade a Potion" (Satchel 起始 Silver，放在 Stash 中!) 恰好一次
        var (runShop, engShop) = CreateStartedEngine(hero: EHero.Mak, rules: new CustomRunRuleSet { StartingGold = 200 }, seed: 4801);
        var satchelShop = AddOwnedItem(runShop, SatchelId, EInventorySection.Stash, slot: 0, tier: ETier.Silver);
        var potionHand1 = AddOwnedItem(runShop, potionId, EInventorySection.Hand, slot: 2, tier: ETier.Bronze);
        int baseRegen = satchelShop.Attributes.GetValueOrDefault(ECardAttributeType.RegenApplyAmount, 0);

        var aila = InjectChoiceEncounter(runShop, AilaMerchantId);
        Assert.Null(engShop.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(aila.InstanceId) }).Error);
        // 往货架注入同阶 Bronze Potion 作为付费商店商品
        var shelfPotionTpl = fx.Repo.GetCard(potionId)!;
        var shelfPotion = new ShopEngine(runShop, fx.Pool, fx.Repo).CreateEntryFromCoreInstance(new CardInstance(shelfPotionTpl, CardTier.Bronze), isFree: false);
        runShop.Cards[shelfPotion.InstanceId] = shelfPotion;
        runShop.SelectionSet.Add(shelfPotion.InstanceId);

        var buyFuseOut = engShop.Handle(new SelectItemCommand { InstanceId = new InstanceId(shelfPotion.InstanceId) });
        Assert.Null(buyFuseOut.Error);
        Assert.Equal(ETier.Silver, potionHand1.Tier);
        Assert.Contains(buyFuseOut.Events, e => e is GameSimEventCardFused);
        int afterShopRegen = satchelShop.Attributes.GetValueOrDefault(ECardAttributeType.RegenApplyAmount, 0);
        Assert.True(afterShopRegen > baseRegen, "付费商店熔合升级 Potion 应触发 Satchel(Stash) 永久增加 Regen");

        // 2. 从免费战利品/Loot 拿取同名同阶牌熔合升级，同样必须触发 "When you buy or upgrade"（回归修复 #48）
        var (runLoot, engLoot) = CreateStartedEngine(hero: EHero.Mak, rules: new CustomRunRuleSet { StartingGold = 50 }, seed: 4802);
        var satchelLoot = AddOwnedItem(runLoot, SatchelId, EInventorySection.Stash, slot: 0, tier: ETier.Silver);
        var potionHand2 = AddOwnedItem(runLoot, potionId, EInventorySection.Hand, slot: 2, tier: ETier.Bronze);
        int baseLootRegen = satchelLoot.Attributes.GetValueOrDefault(ECardAttributeType.RegenApplyAmount, 0);

        runLoot.State = ERunState.Loot;
        runLoot.SelectionRules = new TSelectionContextRules { CanSelectMultiple = false, SelectionIsFree = true, CanExit = true };
        var freeLootPotion = new ShopEngine(runLoot, fx.Pool, fx.Repo).CreateEntryFromCoreInstance(new CardInstance(shelfPotionTpl, CardTier.Bronze), isFree: true);
        runLoot.Cards[freeLootPotion.InstanceId] = freeLootPotion;
        runLoot.SelectionSet.Clear();
        runLoot.SelectionSet.Add(freeLootPotion.InstanceId);

        var lootFuseOut = engLoot.Handle(new SelectItemCommand { InstanceId = new InstanceId(freeLootPotion.InstanceId) });
        Assert.Null(lootFuseOut.Error);
        Assert.Equal(ETier.Silver, potionHand2.Tier);
        Assert.Contains(lootFuseOut.Events, e => e is GameSimEventCardFused);
        int afterLootRegen = satchelLoot.Attributes.GetValueOrDefault(ECardAttributeType.RegenApplyAmount, 0);
        Assert.True(afterLootRegen > baseLootRegen, "免费 Loot 拿牌熔合升级同样必须触发 When you buy or upgrade（此前被 isFree + ExcludeFusion 双杀吞掉）");
        Assert.Equal(afterShopRegen - baseRegen, afterLootRegen - baseLootRegen);
    }

    [Fact(DisplayName = "Wiki49_LevelUps_Milestones_And_OfficialKarnokDbTypoFix")]
    public void Wiki49_LevelUps_Milestones_And_OfficialKarnokDbTypoFix()
    {
        var fx = RealFixture.Value;
        Assert.Equal(30, fx.LevelUps.Count);
        Assert.Equal(100, fx.LevelUps.HealthIncreaseFor(2));
        Assert.Equal(600, fx.LevelUps.HealthIncreaseFor(10)); // TargetNewLevel=10 -> DB Id=9

        // 验证 Lv10（TargetNewLevel=10, DB Id=9）发出 2 个 Diamond/Legendary 附魔祭坛（属于 12 个附魔地标池）
        var player = new PlayerState("p49", "Vanessa", HeroNames.Vanessa, 500, 20, 10, 10) { Level = 10 };
        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, player, null, new Random(4901));
        var lv10Picks = fx.LevelUps.RollRewards(10, ctx, fx.Pool.IsAssetValid);
        Assert.Equal(2, lv10Picks.Count);
        Assert.All(lv10Picks, id => Assert.Equal("PedestalEncounter", fx.Repo.GetCard(id)!.Type));

        // 官服 DB Bug 超越修复验证：Lv2 (Id=1) Inspired by Karnok (0c71aef1) 必须检查实际授予技能 Inspired Rage (0beccea0)
        // （注意 Inspired by Karnok 面向除 Karnok 外的其余 7 英雄开放，测试使用 Vanessa 携带 Machete 触发）
        var pNonKarnok = new PlayerState("pk", "Vanessa", HeroNames.Vanessa, 500, 20, 10, 10) { Level = 2 };
        pNonKarnok.Hand.TryAddAuto(new CardInstance(fx.Repo.GetCard(MacheteId)!, CardTier.Bronze));
        pNonKarnok.Skills.Add(new CardInstance(fx.Repo.GetCard(KarnoksRageSkillId)!, CardTier.Diamond));
        bool sawStepWhenOnlyWrongSkill = false;
        for (int i = 0; i < 160; i++)
        {
            var r = fx.LevelUps.RollRewards(2, new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, pNonKarnok, null, new Random(700 + i)), fx.Pool.IsAssetValid);
            if (r.Contains(InspiredByKarnokStepId, StringComparer.OrdinalIgnoreCase))
            {
                sawStepWhenOnlyWrongSkill = true;
                break;
            }
        }
        Assert.True(sawStepWhenOnlyWrongSkill, "即使持有错配技能 Karnok's Rage，只要未持有 Inspired Rage，Lv2 也应可出 Inspired by Karnok");

        // 2) 一旦玩家持有 Inspired Rage (0beccea0)，Lv2 必须准确屏蔽 Inspired by Karnok！
        pNonKarnok.Skills.Add(new CardInstance(fx.Repo.GetCard(InspiredRageSkillId)!, CardTier.Diamond));
        for (int i = 0; i < 80; i++)
        {
            var r = fx.LevelUps.RollRewards(2, new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, pNonKarnok, null, new Random(800 + i)), fx.Pool.IsAssetValid);
            Assert.DoesNotContain(InspiredByKarnokStepId, r, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact(DisplayName = "Wiki50_TravelAgent_GrandOpening_And_TicketEnchantSellBuff")]
    public void Wiki50_TravelAgent_GrandOpening_And_TicketEnchantSellBuff()
    {
        var fx = RealFixture.Value;
        // 1. Travel Agent (002d8f6d) 下 Surprise Me 与 Browse All Tickets 在当前 v5 DB 均含全部 3 张探险票
        var agentTpl = fx.Repo.GetCard(TravelAgentEventId)!;
        var surpriseTpl = fx.Repo.GetCard(TravelAgentSurpriseMeId)!;
        var browseTpl = fx.Repo.GetCard(TravelAgentBrowseAllId)!;
        Assert.NotNull(agentTpl);
        string surpriseJson = JsonSerializer.Serialize(surpriseTpl.Abilities);
        string browseJson = JsonSerializer.Serialize(browseTpl.Abilities);
        foreach (string ticketId in new[] { CrashSiteTicketId, TempleTicketId, TinyWorldTicketId })
        {
            Assert.Contains(ticketId, surpriseJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ticketId, browseJson, StringComparison.OrdinalIgnoreCase);
        }

        // 2. 买入/获得 Grand Opening 的 Tiny World Ticket 后把 Grand Opening 纳入 ExclusionSet
        var (run, engine) = CreateStartedEngine(seed: 5001);
        var go = InjectChoiceEncounter(run, GrandOpeningEventId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(go.InstanceId) }).Error);
        var goTicket = run.SelectionSet.Select(id => run.Cards[id]).First(c => c.TemplateId == Guid.Parse(TinyWorldTicketId));
        Assert.Null(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(goTicket.InstanceId) }).Error);
        Assert.Contains(TinyWorldTicketId, run.Cards.Values.Where(c => c.Owner == ECombatantId.Player).Select(c => c.TemplateId.ToString()));
        Assert.Contains(GrandOpeningEventId, run.ExclusionSet, StringComparer.OrdinalIgnoreCase);

        // 3. 附魔门票（例如 Fiery Crash Site Ticket）卖出触发永久属性增益到场上手牌左侧带 Burn 标签的物品
        var lighter = AddOwnedItem(run, LighterId, EInventorySection.Hand, slot: 2, tier: ETier.Bronze);
        int burnBefore = lighter.Attributes.GetValueOrDefault(ECardAttributeType.BurnApplyAmount, 0);
        var fieryTicket = AddOwnedItem(run, CrashSiteTicketId, EInventorySection.Stash, slot: 0, tier: ETier.Legendary, enchantment: "Fiery");
        Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(fieryTicket.InstanceId) }).Error);
        int burnAfter = lighter.Attributes.GetValueOrDefault(ECardAttributeType.BurnApplyAmount, 0);
        Assert.True(burnAfter > burnBefore, "卖出 Fiery 门票应向最左符合条件的 Burn 手牌给予永久 Burn 增益");
    }

    [Fact(DisplayName = "Wiki51_PackageSellPassesMerchantId_And_ThievesGuildMedallion")]
    public void Wiki51_PackageSellPassesMerchantId_And_ThievesGuildMedallion()
    {
        // 1. 仅在拜访 Pol（普通/Pygmalien 均可）时卖出 Pol's Package 触发 +1 XP 定向返利
        var (run, engine) = CreateStartedEngine(hero: EHero.Vanessa, rules: new CustomRunRuleSet { StartingGold = 50 }, seed: 5101);
        var pkgOutside = AddOwnedItem(run, PolPackageSmallId, EInventorySection.Stash, slot: 0, tier: ETier.Gold);
        var pkgAtPol = AddOwnedItem(run, PolPackageSmallId, EInventorySection.Stash, slot: 1, tier: ETier.Gold);

        int xp0 = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
        Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(pkgOutside.InstanceId) }).Error);
        Assert.Equal(xp0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0));

        var pol = InjectChoiceEncounter(run, PolNormalId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol.InstanceId) }).Error);
        Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(pkgAtPol.InstanceId) }).Error);
        Assert.Equal(xp0 + 1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0));

        // 2. Thieves Guild Medallion 卖出后移出 ExclusionSet 并加 Custom_12=1
        var (runTg, engTg) = CreateStartedEngine(seed: 5102);
        Assert.Contains(ThievesGuildEventId, runTg.ExclusionSet, StringComparer.OrdinalIgnoreCase);
        var med = AddOwnedItem(runTg, ThievesGuildMedallionId, EInventorySection.Hand, slot: 2, tier: ETier.Legendary);
        Assert.Null(engTg.Handle(new SellCardCommand { InstanceId = new InstanceId(med.InstanceId) }).Error);
        Assert.DoesNotContain(ThievesGuildEventId, runTg.ExclusionSet, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, runTg.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_12, 0));
    }

    [Fact(DisplayName = "Wiki52_TheCult_ShroudedFigure_And_Mandala")]
    public void Wiki52_TheCult_ShroudedFigure_And_Mandala()
    {
        // 1. The Cult: CanLeave=false；Join the Cult 摧毁所有非传奇/非匕首物品返 Value，不触发 When you sell，保留 Tiny，发 Cult Member + 3 XP
        var (runCult, engCult) = CreateStartedEngine(hero: EHero.Vanessa, rules: new CustomRunRuleSet { StartingGold = 0 }, seed: 5201);
        runCult.PlayerAttributes[EPlayerAttributeType.Experience] = 6; // +3 XP 后应触发升级至 Lv2(9->1 XP)并解锁格位
        var normalSmall = AddOwnedItem(runCult, BayonetId, EInventorySection.Hand, slot: 2, tier: ETier.Bronze);       // Sell=1
        var sellTriggerCard = AddOwnedItem(runCult, BluenanasId, EInventorySection.Hand, slot: 3, tier: ETier.Bronze); // Sell=1, When sold +15 MaxHP
        var tinyMed = AddOwnedItem(runCult, PygsGymId, EInventorySection.Stash, slot: 0, tier: ETier.Silver, tiny: true); // Large->Tiny Sell=4
        var legKeep = AddOwnedItem(runCult, ThievesGuildMedallionId, EInventorySection.Hand, slot: 4, tier: ETier.Legendary); // 传奇保留
        var daggerKeep = AddOwnedItem(runCult, RitualDaggerId, EInventorySection.Stash, slot: 2, tier: ETier.Gold);           // 仪式匕首保留

        int maxHpBefore = runCult.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250);
        int expectedRefund = normalSmall.Attributes[ECardAttributeType.SellPrice]
                           + sellTriggerCard.Attributes[ECardAttributeType.SellPrice]
                           + tinyMed.Attributes[ECardAttributeType.SellPrice];

        var cultCard = InjectChoiceEncounter(runCult, TheCultEventId);
        Assert.Null(engCult.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(cultCard.InstanceId) }).Error);
        Assert.False(runCult.SelectionRules.CanExit);
        Assert.NotNull(engCult.Handle(new ExitCurrentStateCommand()).Error); // CanLeave == false

        var joinStep = runCult.SelectionSet.Select(id => runCult.Cards[id])
            .First(c => c.TemplateId == Guid.Parse(JoinTheCultDooleyId) || c.TemplateId == Guid.Parse(JoinTheCultOtherId));
        var joinOut = engCult.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(joinStep.InstanceId) });
        Assert.Null(joinOut.Error);

        Assert.False(runCult.Cards.ContainsKey(normalSmall.InstanceId));
        Assert.False(runCult.Cards.ContainsKey(sellTriggerCard.InstanceId));
        Assert.False(runCult.Cards.ContainsKey(tinyMed.InstanceId));
        Assert.True(runCult.Cards.ContainsKey(legKeep.InstanceId), "Legendary 物品不得被 Join the Cult 摧毁");
        Assert.True(runCult.Cards.ContainsKey(daggerKeep.InstanceId), "Ritual Dagger 不得被 Join the Cult 摧毁");
        Assert.Equal(expectedRefund, runCult.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0));
        Assert.Equal(maxHpBefore + 100, runCult.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250)); // 仅含升 Lv2 的 +100 MaxHP，绝不触发 Bluenanas 卖卡 +15 MaxHP
        Assert.Contains(runCult.Cards.Values, c => c.Owner == ECombatantId.Player && c.TemplateId == Guid.Parse(CultMemberSkillId));
        Assert.True(runCult.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1) >= 2, "Join the Cult +3 XP 应正式触发升级");
        Assert.Contains(runCult.SelectionSet, id => runCult.Cards[id].CoreCard?.GetAttribute("Tiny") >= 1);

        // 2. Shrouded Figure 持有 Cult Member 技能时出现免 30 金选 Power 分支 (b8fc275e) 并隐藏收费项 (9729a91f)
        Assert.Null(engCult.Handle(new ExitCurrentStateCommand()).Error);
        DrainNonChoiceStatesToChoice(engCult, runCult, skipLevelUpChoices: true);
        var sf = InjectChoiceEncounter(runCult, ShroudedFigureId);
        Assert.Null(engCult.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(sf.InstanceId) }).Error);
        Assert.Contains(runCult.SelectionSet, id => runCult.Cards[id].TemplateId == Guid.Parse(ShroudedCultMemberStepId));
        Assert.DoesNotContain(runCult.SelectionSet, id => runCult.Cards[id].TemplateId == Guid.Parse(ShroudedPaidPowerStepId));

        // 3. Mandala CanReroll == false 且跳过最左 Legendary
        var (runMan, engMan) = CreateStartedEngine(seed: 5202);
        var mLeg = AddOwnedItem(runMan, ThievesGuildMedallionId, EInventorySection.Hand, slot: 3, tier: ETier.Legendary);
        var mNorm = AddOwnedItem(runMan, BayonetId, EInventorySection.Hand, slot: 4, tier: ETier.Bronze);
        var mandala = InjectChoiceEncounter(runMan, MandalaEventId);
        Assert.Null(engMan.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mandala.InstanceId) }).Error);
        Assert.NotNull(engMan.Handle(new RerollCommand()).Error);
        var mStep = runMan.SelectionSet.Select(id => runMan.Cards[id]).First(c => c.TemplateId == Guid.Parse(MandalaTransformStepId));
        Assert.Null(engMan.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mStep.InstanceId) }).Error);
        Assert.True(runMan.Cards.ContainsKey(mLeg.InstanceId));
        Assert.False(runMan.Cards.ContainsKey(mNorm.InstanceId));
    }

    [Fact(DisplayName = "Wiki37_MusicNoteSell_GracefulIfAble_And_EnchantedCrossHeroItem")]
    public void Wiki37_MusicNoteSell_GracefulIfAble_And_EnchantedCrossHeroItem()
    {
        var fx = RealFixture.Value;
        // 1. 普通 A Note 卖出：生成 A Note 插座，且 Ability 2 ("Then get an Enchanted small Weapon item from any Hero") 产出 1 件随机附魔跨英雄小件
        var (run, engine) = CreateStartedEngine(hero: EHero.Hero8, seed: 3701);
        var beforePlainIds = run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item)
            .Select(c => c.InstanceId)
            .ToHashSet();
        var plainNote = AddOwnedItem(run, ANoteId, EInventorySection.Hand, slot: 3, tier: ETier.Silver);
        beforePlainIds.Add(plainNote.InstanceId);
        Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(plainNote.InstanceId) }).Error);
        Assert.Single(run.Cards.Values.Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.SocketEffect && c.TemplateId == Guid.Parse(ANoteSocketId)));
        var plainRewardItems = run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item && !beforePlainIds.Contains(c.InstanceId))
            .ToList();
        var plainReward = Assert.Single(plainRewardItems);
        Assert.Equal(ECardSize.Small, plainReward.Size);
        Assert.Equal(ETier.Silver, plainReward.Tier);
        Assert.Contains(ECardTag.Weapon, plainReward.Tags);
        Assert.True(plainReward.Enchantment.HasValue, "未附魔音符 Ability 2 也应产出一件带附魔的跨英雄 Small Weapon");

        // 2. 当所有音符槽均被占位时再卖带 Fiery 附魔 A Note：
        //    应优雅 "if able" 跳过插座生成且不抛错，同时仅触发 Fiery.e2（屏蔽 Ability 2 不双倍发牌），精确产出 1 件 Fiery 附魔跨英雄 Small Weapon
        foreach (int s in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })
        {
            if (run.Cards.Values.Any(c => c.Owner == ECombatantId.Player && c.Type == ECardType.SocketEffect && c.Socket == (EContainerSocketId)s))
                continue;
            var skTpl = fx.Repo.GetCard(ANoteSocketId)!;
            var blocker = new CardInstance(skTpl, CardTier.Gold, null, "blk_" + s) { StartSlot = s };
            var blkEntry = new RunState.CardEntry
            {
                InstanceId = blocker.InstanceId,
                TemplateId = Guid.Parse(ANoteSocketId),
                Type = ECardType.SocketEffect,
                Size = ECardSize.Small,
                Tier = ETier.Gold,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = (EContainerSocketId)s,
                CoreCard = blocker,
            };
            run.Cards[blkEntry.InstanceId] = blkEntry;
        }

        var existingItemIds = run.Cards.Values.Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item).Select(c => c.InstanceId).ToHashSet();
        var enchNote = AddOwnedItem(run, ANoteId, EInventorySection.Stash, slot: 0, tier: ETier.Gold, enchantment: "Fiery");
        existingItemIds.Add(enchNote.InstanceId);
        var sellEnchOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(enchNote.InstanceId) });
        Assert.Null(sellEnchOut.Error);
        var enchRewardItems = run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item && !existingItemIds.Contains(c.InstanceId))
            .ToList();
        var rewardItem = Assert.Single(enchRewardItems);
        Assert.Equal(ECardSize.Small, rewardItem.Size);
        Assert.Equal(ETier.Gold, rewardItem.Tier);
        Assert.Equal(EEnchantmentType.Fiery, rewardItem.Enchantment);
        Assert.Contains(ECardTag.Weapon, rewardItem.Tags);
    }

    [Fact(DisplayName = "Wiki38_OutOfCombatQuests_VisitZosima_And_Atlas_InHandAndStash")]
    public void Wiki38_OutOfCombatQuests_VisitZosima_And_Atlas_InHandAndStash()
    {
        var (run, engine) = CreateStartedEngine(hero: EHero.Mak, seed: 3801);
        var torchStash = AddOwnedItem(run, EternalTorchId, EInventorySection.Stash, slot: 0, tier: ETier.Bronze);
        var totemHand = AddOwnedItem(run, FrostTotemId, EInventorySection.Hand, slot: 3, tier: ETier.Silver);
        var atlasStash = AddOwnedItem(run, AtlasId, EInventorySection.Stash, slot: 2, tier: ETier.Silver);

        int torchCdBefore = torchStash.Attributes[ECardAttributeType.CooldownMax];
        int totemCdBefore = totemHand.Attributes[ECardAttributeType.CooldownMax];

        // 1. 模拟 LevelUp 状态选择 Zosima (Diamond)，同时触发 Hand & Stash 上 Quest_3 (Visit Zosima) 与 Atlas Quest_2 (Diamond)
        run.State = ERunState.LevelUp;
        var zosimaCard = InjectChoiceEncounter(run, ZosimaEventId);
        var visitZosimaOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(zosimaCard.InstanceId) });
        Assert.Null(visitZosimaOut.Error);

        Assert.Contains("Quest_3", torchStash.CoreCard!.CompletedQuests);
        Assert.Contains("Quest_3", totemHand.CoreCard!.CompletedQuests);
        Assert.Contains("Quest_2", atlasStash.CoreCard!.CompletedQuests);
        Assert.Contains(visitZosimaOut.Events, e => e is GameSimEventCardQuestCompleted q && q.InstanceId == torchStash.InstanceId);
        Assert.Contains(visitZosimaOut.Events, e => e is GameSimEventCardQuestCompleted q && q.InstanceId == totemHand.InstanceId);
        Assert.Equal(torchCdBefore - 1000, torchStash.Attributes[ECardAttributeType.CooldownMax]);
        Assert.Equal(totemCdBefore - 1000, totemHand.Attributes[ECardAttributeType.CooldownMax]);

        // 2. 离开 Zosima 回到 Choice，再分别造访 Gold 与 Legendary 遭遇推进 Atlas Quest_1 与 Quest_3
        DrainNonChoiceStatesToChoice(engine, run, skipLevelUpChoices: true);
        var goldEnc = InjectChoiceEncounter(run, ApothecaryGoldEventId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(goldEnc.InstanceId) }).Error);
        Assert.Contains("Quest_1", atlasStash.CoreCard!.CompletedQuests);
        DrainNonChoiceStatesToChoice(engine, run, skipLevelUpChoices: true);

        var legEnc = InjectChoiceEncounter(run, YetarianTombLegendaryId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(legEnc.InstanceId) }).Error);
        Assert.Contains("Quest_3", atlasStash.CoreCard!.CompletedQuests);
        DrainNonChoiceStatesToChoice(engine, run, skipLevelUpChoices: true);

        // 3. 卖出已完成全 3 任务的 Atlas：获得 2 Loot + 2 XP + 1 Legendary Small/Medium 物品
        run.PlayerAttributes[EPlayerAttributeType.Experience] = 6;
        int lvlBeforeSell = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1);
        var sellAtlasOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(atlasStash.InstanceId) });
        Assert.Null(sellAtlasOut.Error);
        Assert.True(run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1) >= lvlBeforeSell + 1, "Atlas Quest_2 卖出授予 2 XP 应触发升级");
        Assert.True(run.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item && c.Tags.Contains(ECardTag.Loot)) >= 2);
        Assert.Contains(run.Cards.Values, c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item && c.Tier == ETier.Legendary);
    }
}
