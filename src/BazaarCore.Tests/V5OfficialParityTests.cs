using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BazaarCli;
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
using BazaarServer.Storage;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V5 官方机制全量对标测试套件（覆盖 P0-1 ~ P0-10、P1-1 ~ P1-10 与 P2-2 / P2-3）。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class V5OfficialParityTests
{
    private const string PawnShopId = "31b35938-9402-4990-b4d9-473ce5887af9";
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string EagleTalismanId = "0022c409-c839-41e8-8022-65a407457dfe";
    private const string PolShopId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string FuturaEncounterId = "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
    private const string DooleyCoreInitId = "1596d1df-e943-48b4-bfc6-df2edb9dfe55";
    private const string JulesChefBrolinId = "7a4f9be7-9132-45a2-9885-6f2ce40a59be";
    private const string JulesGourmetKitchenId = "83f4bee7-1dfe-4a97-b7b3-147d0947b850";
    private const string StoveSocketId = "c017c0dd-af3c-47e0-b510-3661d9dcd7f2";
    private const string NoteASocketId = "a9adbfdc-567e-4fe6-a9e3-7eded0bc1279";
    private const string NoteCSocketId = "c82b1942-e666-495e-8ed2-54ad13e15c97";
    private const string NoteDSocketId = "38686c0c-ef93-4fb3-81ce-4f5f6d918f11";
    private const string TeaSetId = "0c501dff-aa3a-4091-9c1e-2df43faef2e1";
    private const string ChimericEggId = "28024275-df05-422c-800c-9960d83c24fa";
    private const string YetarianTombPedestalId = "04f879e5-94fe-42ae-aa7a-273edbd6d410";
    private const string UpgradePedestalId = "2e1e5a86-3ace-4f78-8b73-6641ca59e834";
    private const string CatalystId = "694a08fe-e8ff-4a45-9693-4c68f11a1f8b";
    private const string ArmsDealerSkillId = "560a532c-73f4-4edd-b197-0127e2ca4cc9";

    private sealed class RealDataFixture
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

    private static readonly Lazy<RealDataFixture> Fixture = new(() =>
    {
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        return new RealDataFixture
        {
            DataDir = shared.DataDir,
            DbPath = shared.DbPath,
            Pool = shared.Pool,
            Monsters = shared.CreateMonstersCopy(),
            LevelUps = shared.LevelUps,
            Mode = shared.Mode,
            Repo = shared.Repo,
            Ghosts = shared.Ghosts,
        };
    });

    private static RuleNode DeserializeNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(EHero hero = EHero.Vanessa)
    {
        var fx = Fixture.Value;
        var run = new RunState
        {
            RunId = "v5_test_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
        };
        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        engine.StartRun();
        engine.Handle(new CheatCommand { Args = ["skipstart"] });
        engine.Handle(new CheatCommand { Args = ["slots"] });
        engine.Handle(new CheatCommand { Args = ["gold", "100"] });
        return (run, engine);
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        string templateId,
        int slot,
        ETier? tierOverride = null,
        EInventorySection section = EInventorySection.Hand)
    {
        var fx = Fixture.Value;
        var coreTpl = fx.Repo.GetCard(templateId)!;
        var poolTpl = fx.Pool.Get(templateId);
        ETier tier = tierOverride ?? ShopEngine.ParseTier(coreTpl.StartingTier);
        var core = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), null, "own_" + Guid.NewGuid().ToString("N")[..8])
        {
            StartSlot = slot,
        };
        var grid = section == EInventorySection.Stash ? run.StashGrid : run.HandGrid;
        Assert.True(grid.TryPlaceOrShift(core, slot));

        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)slot,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.Cards[entry.InstanceId] = entry;
        run.SyncAllEntriesFromCoreCardsAndDiff(fx.Repo.GameMode.StandardPrices, [entry]);
        return entry;
    }

    private static RunState.CardEntry InjectChoiceEncounter(RunState run, string encounterId)
    {
        var fx = Fixture.Value;
        var tpl = fx.Pool.Get(encounterId);
        string instId = "enc_" + Guid.NewGuid().ToString("N")[..8];
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(encounterId),
            Type = tpl != null ? ShopEngine.ParseCardType(tpl.Type) : ECardType.EventEncounter,
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

    private static CardTemplate MakeCooldownItemTemplate(
        string id,
        string size = "Small",
        double cooldownMs = 3000,
        double dmg = 10) => new()
    {
        Id = id,
        InternalName = id,
        Type = "Item",
        Size = size,
        StartingTier = "Bronze",
        Tags = ["Weapon"],
        Tiers = new Dictionary<string, TierData>
        {
            ["Bronze"] = new TierData
            {
                Attributes = new Dictionary<string, double>
                {
                    ["CooldownMax"] = cooldownMs,
                    ["DamageAmount"] = dmg,
                },
            },
        },
    };

    // ── P0-1：局外玩家属性光环幂等（Pawn Shop 连续 5 次移动 HealthMax 恒定） ──

    [Fact]
    public void P0_01_PawnShop_PlayerHealthMaxAura_ConstantAcrossFiveMovesAndSnapshotRoundtrip()
    {
        var fx = Fixture.Value;
        var (run, engine) = CreateStartedEngine(EHero.Pygmalien);

        var pol = InjectChoiceEncounter(run, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol.InstanceId) }).Error);

        int baseHealthMax = run.PlayerAttributes[EPlayerAttributeType.HealthMax];
        var pawnShop = AddOwnedItem(run, PawnShopId, slot: 0, ETier.Silver);

        // 连续 5 次移动 Pawn Shop（含首次移动触发 OnCardMoved + 光环同步）
        int[] targetSlots = [2, 0, 3, 1, 4];
        int? expectedHealthMax = null;
        foreach (int slot in targetSlots)
        {
            var mvOut = engine.Handle(new MoveItemCommand
            {
                InstanceId = new InstanceId(pawnShop.InstanceId),
                Section = EInventorySection.Hand,
                TargetSockets = [(EContainerSocketId)slot],
            });
            Assert.Null(mvOut.Error);

            int curHpMax = run.PlayerAttributes[EPlayerAttributeType.HealthMax];
            expectedHealthMax ??= curHpMax;
            Assert.True(curHpMax > baseHealthMax, $"Pawn Shop 光环应提升 HealthMax，base={baseHealthMax}, cur={curHpMax}");
            Assert.Equal(expectedHealthMax.Value, curHpMax);
        }

        Assert.True(run.PlayerAuraAdd.GetValueOrDefault("HealthMax", 0) > 0);

        // 序列化往返恢复后再移动一次，HealthMax 仍严格保持不变
        string json = RunSnapshotSerializer.Serialize(run, startSelectionDone: true);
        var restored = RunSnapshotSerializer.Deserialize(json, fx.Repo);
        var restoredEngine = new RunEngine(restored, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        var mvAfterRestore = restoredEngine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(pawnShop.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = [EContainerSocketId.Socket_0],
        });
        Assert.Null(mvAfterRestore.Error);
        Assert.Equal(expectedHealthMax!.Value, restored.PlayerAttributes[EPlayerAttributeType.HealthMax]);
    }

    // ── P0-2：Flying 映射、TCardConditionalAttribute("Flying") 与 LeftSocketId ──

    [Fact]
    public void P0_02_FlyingAttribute_ConditionalEvaluation_CombatTrigger_AndLeftSocketId()
    {
        var fx = Fixture.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Stelle, 300, 20, 10, 10);
        var opp = new PlayerState("p2", "P2", HeroNames.Vanessa, 300, 20, 10, 10);

        var flyer = new CardInstance(MakeCooldownItemTemplate("test_flyer", cooldownMs: 2000, dmg: 10))
        {
            StartSlot = 4,
            IsFlying = false,
        };
        Assert.Equal(4, flyer.LeftSocketId);
        Assert.Equal(0.0, flyer.GetAttribute("Flying"));

        var flyingCond = DeserializeNode("""
            {
              "$type": "TCardConditionalAttribute",
              "Attribute": "Flying",
              "ComparisonOperator": "GreaterThanOrEqual",
              "ComparisonValue": 1.0
            }
            """);

        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.InCombat, player, opp, new Random(1));
        Assert.False(ValueAndConditionEvaluator.EvaluateCardCondition(flyingCond, flyer, player, ctx));

        flyer.IsFlying = true;
        Assert.Equal(1.0, flyer.GetAttribute("Flying"));
        Assert.True(ValueAndConditionEvaluator.EvaluateCardCondition(flyingCond, flyer, player, ctx));

        // 战斗内：开战令 flyer 起飞（TActionCardFlyingStart），随后由 TTriggerOnCardStartedFlying
        // 按 TCardConditionalAttribute("Flying") >= 1 仅给起飞卡 +25 伤害（不误加地面卡）
        var flyerCombatTpl = MakeCooldownItemTemplate("test_flyer_cbt", cooldownMs: 2000, dmg: 10);
        flyerCombatTpl.Tiers!["Bronze"].AbilityIds.Add("start_fly");
        flyerCombatTpl.Abilities["start_fly"] = new AbilityDefinition
        {
            Id = "start_fly",
            WorksIn = "CombatOnly",
            Trigger = DeserializeNode("""{"$type":"TTriggerOnFightStarted"}"""),
            Action = DeserializeNode("""
                {
                  "$type": "TActionCardFlyingStart",
                  "Target": { "$type": "TTargetCardSelf" }
                }
                """),
        };
        var flyerCombat = new CardInstance(flyerCombatTpl);
        var grounded = new CardInstance(MakeCooldownItemTemplate("test_grounded", cooldownMs: 2000, dmg: 10));
        var bufferTpl = new CardTemplate
        {
            Id = "flying_buffer",
            InternalName = "Flying Buffer",
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["buff_fly"] = new AbilityDefinition
                {
                    Id = "buff_fly",
                    WorksIn = "CombatOnly",
                    Trigger = DeserializeNode("""
                        {
                          "$type": "TTriggerOnCardStartedFlying",
                          "Subject": { "$type": "TTargetCardSection", "TargetSection": "SelfHand" }
                        }
                        """),
                    Action = DeserializeNode("""
                        {
                          "$type": "TActionCardModifyAttribute",
                          "AttributeType": "DamageAmount",
                          "Operation": "Add",
                          "Value": { "$type": "TFixedValue", "Value": 25.0 },
                          "Target": {
                            "$type": "TTargetCardSection",
                            "TargetSection": "SelfHand",
                            "Conditions": {
                              "$type": "TCardConditionalAttribute",
                              "Attribute": "Flying",
                              "ComparisonOperator": "GreaterThanOrEqual",
                              "ComparisonValue": 1.0
                            }
                          }
                        }
                        """),
                },
            },
        };
        player.Hand.TryAddAuto(flyerCombat);
        player.Hand.TryAddAuto(grounded);
        player.Skills.Add(new CardInstance(bufferTpl));

        var sim = CombatSimulator.Simulate(fx.Repo, player, opp, seed: 42, recordEvents: true);
        var flyerBuff = sim.Events.FirstOrDefault(e =>
            e.Type == CombatEventType.CardAttributeModified &&
            e.TargetCardInstanceId == flyerCombat.InstanceId &&
            e.Detail == "DamageAmount");
        var groundedBuff = sim.Events.FirstOrDefault(e =>
            e.Type == CombatEventType.CardAttributeModified &&
            e.TargetCardInstanceId == grounded.InstanceId &&
            e.Detail == "DamageAmount");
        Assert.NotNull(flyerBuff);
        Assert.Equal(25.0, flyerBuff!.Value);
        Assert.Null(groundedBuff);
    }

    // ── P0-3：*Targets 属性覆盖多目标随机动作（不被截断为 1） ──

    [Fact]
    public void P0_03_MultiTargetAttributes_HasteAndSlowTargets_ApplyToTwoDistinctCards()
    {
        var fx = Fixture.Value;
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 500, 20, 10, 10);

        var sourceTpl = new CardTemplate
        {
            Id = "multi_haste_src",
            InternalName = "Multi Haste Source",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["HasteAmount"] = 2000,
                        ["HasteTargets"] = 2,
                        ["SlowAmount"] = 2000,
                        ["SlowTargets"] = 2,
                    },
                },
            },
        };
        var src = new CardInstance(sourceTpl);
        p1.Hand.TryAddAuto(src);
        for (int i = 0; i < 3; i++)
        {
            p1.Hand.TryAddAuto(new CardInstance(MakeCooldownItemTemplate($"ally_{i}")));
            p2.Hand.TryAddAuto(new CardInstance(MakeCooldownItemTemplate($"enemy_{i}")));
        }

        var hasted = new List<CardInstance>();
        var slowed = new List<CardInstance>();
        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.InCombat, p1, p2, new Random(7))
        {
            SelfCard = src,
            OnHasteApplied = (target, ms, _) => { target.HasteMs += ms; hasted.Add(target); },
            OnSlowApplied = (target, ms, _) => { target.SlowMs += ms; slowed.Add(target); },
        };

        var hasteAction = DeserializeNode("""
            {
              "$type": "TActionCardHaste",
              "TargetCount": null,
              "Target": { "$type": "TTargetCardRandom", "TargetSection": "SelfHand", "ExcludeSelf": true }
            }
            """);
        var slowAction = DeserializeNode("""
            {
              "$type": "TActionCardSlow",
              "TargetCount": null,
              "Target": { "$type": "TTargetCardRandom", "TargetSection": "OpponentHand" }
            }
            """);

        Assert.True(AbilityExecutor.ExecuteAction(hasteAction, src, ctx));
        Assert.True(AbilityExecutor.ExecuteAction(slowAction, src, ctx));
        Assert.Equal(2, hasted.Select(c => c.InstanceId).Distinct().Count());
        Assert.Equal(2, slowed.Select(c => c.InstanceId).Distinct().Count());
    }

    // ── P0-4：缺 BaseAttributes["SellPrice"] 回落标准底价与 Golden 翻倍并被 EvaluateValue 读取 ──

    [Fact]
    public void P0_04_SellPriceFallback_StandardAndGoldenMultiplier_ReadByEvaluateValue()
    {
        var fx = Fixture.Value;
        var prices = fx.Repo.GameMode.StandardPrices;
        var talismanTpl = fx.Repo.GetCard(EagleTalismanId)!;
        Assert.False(talismanTpl.Tiers!["Silver"].Attributes.ContainsKey("SellPrice"));

        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, 10);
        var talisman = new CardInstance(talismanTpl, CardTier.Silver);
        player.Hand.TryAddAuto(talisman);

        int expectedBaseSell = prices.GetItemSellPrice(CardSize.Small, CardTier.Silver);
        Assert.True(expectedBaseSell > 0);
        Assert.Equal(expectedBaseSell, talisman.GetAttribute("SellPrice", prices));

        talisman.ApplyEnchantment("Golden");
        AuraEngine.RecalculateAuras(player, null, RuleScope.OutOfCombat, 1, 1, fx.Repo);

        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, player, null, new Random(1))
        {
            SelfCard = talisman,
        };
        var readSellNode = DeserializeNode("""
            {
              "$type": "TReferenceValueCardAttribute",
              "AttributeType": "SellPrice",
              "Target": { "$type": "TTargetCardSelf" }
            }
            """);

        double evaluated = ValueAndConditionEvaluator.EvaluateValue(readSellNode, ctx);
        Assert.Equal(expectedBaseSell * 2, talisman.GetSellPrice(prices));
        Assert.Equal(expectedBaseSell * 2, evaluated);
    }

    // ── P0-5：LevelUpTable 对 Dooley Core Init / Jules Chef Brolin & Gourmet Kitchen 权重 0 必出及 EncounterStep 退场 ──

    [Fact]
    public void P0_05_LevelUpTable_ZeroWeightConditionalGroups_AndEncounterStepExit()
    {
        var fx = Fixture.Value;

        // 1. Dooley Lv2->Lv3（targetNewLevel=3 -> DB Id=2）必出 Core Initialization（RandomWeight == 0）
        var dooley = new PlayerState("p1", "P1", HeroNames.Dooley, 250, 20, 10, 10) { Level = 3, Day = 2, Hour = 2 };
        var dooleyCtx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, dooley, dooley, new Random(1))
        {
            CurrentDay = 2,
            CurrentHour = 2,
        };
        var dooleyRewards = fx.LevelUps.RollRewards(3, dooleyCtx, fx.Pool.IsAssetValid);
        Assert.Contains(DooleyCoreInitId, dooleyRewards, StringComparer.OrdinalIgnoreCase);

        // 2. Jules Lv2->Lv3（targetNewLevel=3 -> DB Id=2）必出 Chef Brolin (Level Up)，Lv5->Lv6（targetNewLevel=6 -> DB Id=5）可抽到 Gourmet Kitchen (Level Up)
        var jules = new PlayerState("p1", "P1", HeroNames.Jules, 250, 20, 10, 10) { Level = 3, Day = 2, Hour = 2 };
        var julesLv3Ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, jules, jules, new Random(1))
        {
            CurrentDay = 2,
            CurrentHour = 2,
        };
        var julesLv3Rewards = fx.LevelUps.RollRewards(3, julesLv3Ctx, fx.Pool.IsAssetValid);
        Assert.Contains(JulesChefBrolinId, julesLv3Rewards, StringComparer.OrdinalIgnoreCase);

        jules.Level = 6;
        bool sawGourmetKitchen = false;
        for (int seed = 0; seed < 40 && !sawGourmetKitchen; seed++)
        {
            var julesLv6Ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, jules, jules, new Random(seed))
            {
                CurrentDay = 3,
                CurrentHour = 2,
            };
            var rewards = fx.LevelUps.RollRewards(6, julesLv6Ctx, fx.Pool.IsAssetValid);
            if (rewards.Contains(JulesGourmetKitchenId, StringComparer.OrdinalIgnoreCase))
                sawGourmetKitchen = true;
        }
        Assert.True(sawGourmetKitchen, "Jules Lv5->Lv6（DB Id=5）权重 0 组应包含并可抽到 Gourmet Kitchen (Level Up)");

        // 3. Jules 实跑 RunEngine 先升至 Lv2 并消费 Lv2 奖励，再进入商店并离开推进小时触发 Lv2->Lv3 升级 → 进 Chef Brolin 选子项 → 正常退场回 Choice
        var (run, engine) = CreateStartedEngine(EHero.Jules);
        Assert.Null(engine.Handle(new CheatCommand { Args = ["exp", fx.Mode.ExperiencePerLevel.ToString()] }).Error);
        var pol1 = InjectChoiceEncounter(run, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol1.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);

        var pol = InjectChoiceEncounter(run, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol.InstanceId) }).Error);
        int curXp = run.PlayerAttributes[EPlayerAttributeType.Experience];
        // P0-1 官方单份经验规则：商店离场仅发 1 份经验（ExperienceAwardUponSelection=1），不再额外叠发 ExperiencePerHour
        int almostLvl3 = Math.Max(0, fx.Mode.ExperiencePerLevel - curXp - 1);
        if (almostLvl3 > 0)
            Assert.Null(engine.Handle(new CheatCommand { Args = ["exp", almostLvl3.ToString()] }).Error);
        var exitToLvl = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitToLvl.Error);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Equal(3, run.PlayerAttributes[EPlayerAttributeType.Level]);

        var brolinEntry = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId == Guid.Parse(JulesChefBrolinId));
        Assert.NotNull(brolinEntry);

        var pickOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(brolinEntry!.InstanceId) });
        Assert.Null(pickOut.Error);

        if (run.State == ERunState.Encounter && run.SelectionSet.Count > 0)
        {
            string stepChoiceId = run.SelectionSet.First();
            var stepOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(stepChoiceId) });
            Assert.Null(stepOut.Error);
        }
        if (run.State == ERunState.Encounter && run.SelectionRules.CanExit)
        {
            var exitOut = engine.Handle(new ExitCurrentStateCommand());
            Assert.Null(exitOut.Error);
        }
        Assert.Equal(ERunState.Choice, run.State);

        // 4. 在 LevelUp 状态下直接点选 Gourmet Kitchen (EncounterStep) 也能完整执行并顺利退场
        run.State = ERunState.LevelUp;
        run.CurrentEncounterId = null;
        run.SelectionRules = new TSelectionContextRules { CanSelectMultiple = false, SelectionIsFree = true, CanExit = true };
        var gkEntry = InjectChoiceEncounter(run, JulesGourmetKitchenId);
        var gkOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(gkEntry.InstanceId) });
        Assert.Null(gkOut.Error);
        if (run.State == ERunState.Encounter && run.SelectionRules.CanExit)
        {
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        }
        Assert.Equal(ERunState.Choice, run.State);
    }

    // ── P0-6：PvP 战败声望归零切入 Futura 濒死三选一 ──

    [Fact]
    public void P0_06_PvpDefeatAtZeroPrestige_EntersFuturaFatesChoice()
    {
        var fx = Fixture.Value;
        var (run, engine) = CreateStartedEngine(EHero.Vanessa);

        Assert.Null(engine.Handle(new CheatCommand { Args = ["day", "3"] }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = ["prestige", "1"] }).Error);
        Assert.False(run.HasVisitedFates);

        // 空场打第 3 天 PvP（必然战败，扣 3 点声望跌破 0）
        var pvpOut = engine.Handle(new CheatCommand { Args = ["pvp"] });
        Assert.Null(pvpOut.Error);
        Assert.NotNull(pvpOut.Combat);
        Assert.Equal(ECombatantId.Opponent, pvpOut.Combat!.Winner);

        Assert.True(run.HasVisitedFates);
        Assert.Equal(Math.Max(1, fx.Mode.FatesAfterPrestige), run.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(FuturaEncounterId, run.CurrentEncounterId);
        Assert.NotEmpty(run.SelectionSet);
    }

    // ── P0-7 (Core)：SocketEffects 生成放置、防相邻与 TTargetCardOccupying 多格区间碰撞 ──

    [Fact]
    public void P0_07_SocketEffects_SpawnPlacement_AvoidAdjacent_AndMultiSlotOccupyingOverlap()
    {
        var fx = Fixture.Value;
        var stoveTpl = fx.Repo.GetCard(StoveSocketId)!;
        var player = new PlayerState("p1", "P1", HeroNames.Jules, 300, 20, 10, 10);

        // 1. TSpawnBehaviorAvoidAdjacentSocketEffect：已有 slot 0 的 Stove 时，通过 AbilityExecutor 生成第 2 个 Stove 不贴邻放到 slot 1
        var firstStove = new CardInstance(stoveTpl) { StartSlot = 0 };
        player.SocketEffects.Add(firstStove);

        var spawnAction = DeserializeNode($$"""
            {
              "$type": "TActionGameSpawnCards",
              "SpawnContext": {
                "$type": "TSpawnContextQuery",
                "Limit": { "$type": "TFixedValue", "Value": 1.0 },
                "Behaviors": [
                  { "$type": "TSpawnBehaviorAvoidAdjacentSocketEffect" },
                  { "$type": "TSpawnBehaviorAllowDuplicates", "AllowDuplicates": true }
                ],
                "Groups": [
                  {
                    "$type": "TSpawnGroup",
                    "Limit": { "$type": "TFixedValue", "Value": 1.0 },
                    "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{StoveSocketId}}"] }]
                  }
                ]
              }
            }
            """);
        var spawnCtx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, player, null, new Random(1));
        Assert.True(AbilityExecutor.ExecuteAction(spawnAction, firstStove, spawnCtx));
        Assert.Equal(2, player.SocketEffects.Count);
        var secondStove = player.SocketEffects[1];
        Assert.InRange(secondStove.StartSlot, 2, 9);

        // 2. TTargetCardOccupying 多格重叠双向扫描：Large(宽3) 占 slot 2..4，Stove 位于 slot 3
        player.SocketEffects.Clear();
        var centerStove = new CardInstance(stoveTpl) { StartSlot = 3 };
        player.SocketEffects.Add(centerStove);

        var largeItem = new CardInstance(MakeCooldownItemTemplate("large_pan", size: "Large")) { StartSlot = 2 };
        Assert.True(player.Hand.TryPlaceOrShift(largeItem, 2));

        var occupyingTarget = DeserializeNode("""{ "$type": "TTargetCardOccupying" }""");

        // Large(2..4) 查询占据的 SocketEffect(3)
        var ctxFromItem = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, player, null, new Random(1))
        {
            SelfCard = largeItem,
        };
        var foundSockets = TargetResolver.ResolveCards(occupyingTarget, ctxFromItem);
        Assert.Contains(foundSockets, c => c.InstanceId == centerStove.InstanceId);

        // SocketEffect(3) 反向查询覆盖它的 Large(2..4)（StartSlot 3 != 2，验证区间重叠而非等值比较）
        var ctxFromSocket = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, player, null, new Random(1))
        {
            SelfCard = centerStove,
        };
        var foundItems = TargetResolver.ResolveCards(occupyingTarget, ctxFromSocket);
        Assert.Contains(foundItems, c => c.InstanceId == largeItem.InstanceId);
    }

    // ── P0-7 & P0-10 (Server)：SocketEffects 全链路同步、快照往返与 BuildDummyQueryPlayer ──

    [Fact]
    public void P0_07_And_P0_10_ServerSocketEffects_RoundtripAndDummyQueryPreservesSocketAndAura()
    {
        var fx = Fixture.Value;
        var (run, engine) = CreateStartedEngine(EHero.Jules);

        var stoveTpl = fx.Repo.GetCard(StoveSocketId)!;
        var stoveCore = new CardInstance(stoveTpl, CardTier.Bronze, null, "skt_stove_test") { StartSlot = 4 };
        var stoveEntry = new RunState.CardEntry
        {
            InstanceId = stoveCore.InstanceId,
            TemplateId = Guid.Parse(StoveSocketId),
            Type = ECardType.SocketEffect,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_4,
            CoreCard = stoveCore,
        };
        run.Cards[stoveEntry.InstanceId] = stoveEntry;
        Assert.Empty(run.HandGrid.Cards); // SocketEffect 不占物品格

        // 加入 Pawn Shop 触发局外光环
        AddOwnedItem(run, PawnShopId, slot: 0, ETier.Silver);

        // 快照序列化往返后，SocketEffect 保持 Type=SocketEffect、Socket=Socket_4 且仍不占 HandGrid
        string snapJson = RunSnapshotSerializer.Serialize(run, startSelectionDone: true);
        var restored = RunSnapshotSerializer.Deserialize(snapJson, fx.Repo);
        Assert.True(restored.Cards.TryGetValue(stoveEntry.InstanceId, out var restoredStove));
        Assert.Equal(ECardType.SocketEffect, restoredStove!.Type);
        Assert.Equal(EContainerSocketId.Socket_4, restoredStove.Socket);
        Assert.DoesNotContain(restored.HandGrid.Cards, c => c.InstanceId == stoveEntry.InstanceId);

        // ShopEngine 使用 BuildDummyQueryPlayer 为发牌查询构造玩家态（如 DealLevelUpChoices），
        // 必须保留 SocketEffects 不漏入 Hand 且不破坏 RunState 原数据
        var shop = new ShopEngine(restored, fx.Pool, fx.Repo);
        var lvlEvents = shop.DealLevelUpChoices(2, fx.LevelUps);
        Assert.NotEmpty(lvlEvents);
        Assert.Equal(ECardType.SocketEffect, restored.Cards[stoveEntry.InstanceId].Type);
        Assert.Equal(EContainerSocketId.Socket_4, restored.Cards[stoveEntry.InstanceId].Socket);
        Assert.DoesNotContain(restored.HandGrid.Cards, c => c.InstanceId == stoveEntry.InstanceId);
    }

    // ── P0-8：WorksIn="CombatOnly" 与 "OutOfCombatOnly" 双向严格隔离 ──

    [Fact]
    public void P0_08_WorksInIsolation_CombatOnlySkippedInOOC_AndOutOfCombatOnlySkippedInCombat()
    {
        var fx = Fixture.Value;

        // 校验官方真实模板 WorksIn 反序列化：Tea Set 含 CombatOnly，Chimeric Egg 含 OutOfCombatOnly
        var teaSet = fx.Repo.GetCard(TeaSetId)!;
        var egg = fx.Repo.GetCard(ChimericEggId)!;
        Assert.Contains(teaSet.Abilities.Values, a => string.Equals(a.WorksIn, "CombatOnly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(egg.Abilities.Values, a => string.Equals(a.WorksIn, "OutOfCombatOnly", StringComparison.OrdinalIgnoreCase));

        // 1. CombatOnly 触发器不得在局外触发
        var combatOnlyOnSellTpl = new CardTemplate
        {
            Id = "combat_only_sell_guard",
            InternalName = "CombatOnly Guard",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab0"] = new AbilityDefinition
                {
                    Id = "ab0",
                    WorksIn = "CombatOnly",
                    ActiveIn = "HandAndStash",
                    Trigger = DeserializeNode("""{"$type":"TTriggerOnDayStarted"}"""),
                    Action = DeserializeNode("""
                        {
                          "$type": "TActionPlayerModifyAttribute",
                          "AttributeType": "Gold",
                          "Operation": "Add",
                          "Value": { "$type": "TFixedValue", "Value": 99.0 },
                          "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
                        }
                        """),
                },
            },
        };
        var oocPlayer = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, 10);
        oocPlayer.Hand.TryAddAuto(new CardInstance(combatOnlyOnSellTpl));
        OutOfCombatController.OnDayStarted(oocPlayer, fx.Repo, new Random(1));
        Assert.Equal(10, oocPlayer.Gold);

        // 2. OutOfCombatOnly 触发器不得在战斗内触发
        var oocOnlyInCombatTpl = new CardTemplate
        {
            Id = "ooc_only_fight_guard",
            InternalName = "OOCOnly Guard",
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab0"] = new AbilityDefinition
                {
                    Id = "ab0",
                    WorksIn = "OutOfCombatOnly",
                    ActiveIn = "Anywhere",
                    Trigger = DeserializeNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = DeserializeNode("""
                        {
                          "$type": "TActionPlayerDamage",
                          "Value": { "$type": "TFixedValue", "Value": 50.0 },
                          "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" }
                        }
                        """),
                },
            },
        };
        var cbtP1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, 10);
        var cbtP2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 500, 20, 10, 10);
        var oocSkill = new CardInstance(oocOnlyInCombatTpl);
        cbtP1.Skills.Add(oocSkill);
        // 给对手一把 1s 300 伤真实开火武器在沙暴前结束战斗，隔离验证 OutOfCombatOnly 技能未触发伤害
        var finisherTpl = MakeCooldownItemTemplate("opp_finisher", cooldownMs: 1000, dmg: 300);
        finisherTpl.Tiers!["Bronze"].AbilityIds.Add("fire");
        finisherTpl.Abilities["fire"] = new AbilityDefinition
        {
            Id = "fire",
            Trigger = DeserializeNode("""{"$type":"TTriggerOnCardFired"}"""),
            Action = DeserializeNode("""
                {
                  "$type": "TActionPlayerDamage",
                  "Value": { "$type": "TReferenceValueCardAttribute", "AttributeType": "DamageAmount", "Target": { "$type": "TTargetCardSelf" } },
                  "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" }
                }
                """),
        };
        cbtP2.Hand.TryAddAuto(new CardInstance(finisherTpl));
        var sim = CombatSimulator.Simulate(fx.Repo, cbtP1, cbtP2, seed: 1, recordEvents: true);
        Assert.Equal(500, sim.Player2RemainingHp);
        Assert.Equal(0, sim.Player1TotalDamageDealt);
        Assert.DoesNotContain(sim.Events, e => e.SourceCardInstanceId == oocSkill.InstanceId);
    }

    // ── P0-9：TTargetPlayerAbsolute / SelfBoard 含 Stash+Skills / 跳过 IsDestroyed / 极值先滤后算 ──

    [Fact]
    public void P0_09_TargetResolver_AbsolutePlayer_SelfBoard_SkipDestroyed_AndExtremePool()
    {
        var fx = Fixture.Value;
        var local = new PlayerState("player", "Player", HeroNames.Vanessa, 300, 20, 10, 10) { IsMonster = false };
        var monster = new PlayerState("monster_1", "Monster", HeroNames.Common, 300, 20, 0, 10) { IsMonster = true };

        var handCard = new CardInstance(MakeCooldownItemTemplate("hand_item", dmg: 20));
        var destroyedCard = new CardInstance(MakeCooldownItemTemplate("destroyed_item", dmg: 999)) { IsDestroyed = true };
        var noCdHighDmg = new CardInstance(MakeCooldownItemTemplate("passive_high", cooldownMs: 0, dmg: 500));
        var stashCard = new CardInstance(MakeCooldownItemTemplate("stash_item", dmg: 5));
        var skillCard = new CardInstance(fx.Repo.GetCard(ArmsDealerSkillId)!);

        local.Hand.TryAddAuto(handCard);
        local.Hand.TryAddAuto(destroyedCard);
        local.Hand.TryAddAuto(noCdHighDmg);
        local.Stash.TryAddAuto(stashCard);
        local.Skills.Add(skillCard);

        // 1. 野怪为 SelfPlayer 时，TTargetPlayerAbsolute(Player) 仍指向本地玩家
        var monsterCtx = new RuleExecutionContext(fx.Repo, RuleScope.InCombat, monster, local, new Random(1))
        {
            LocalPlayerId = local.PlayerId,
        };
        var absTarget = DeserializeNode("""{ "$type": "TTargetPlayerAbsolute", "TargetMode": "Player" }""");
        var resolvedPlayers = TargetResolver.ResolvePlayers(absTarget, monsterCtx);
        Assert.Single(resolvedPlayers);
        Assert.Same(local, resolvedPlayers[0]);

        // 2. SelfBoard 覆盖 Hand + Stash + Skills，且默认跳过 IsDestroyed
        var playerCtx = new RuleExecutionContext(fx.Repo, RuleScope.InCombat, local, monster, new Random(1))
        {
            LocalPlayerId = local.PlayerId,
        };
        var boardTarget = DeserializeNode("""{ "$type": "TTargetCardSection", "TargetSection": "SelfBoard" }""");
        var boardCards = TargetResolver.ResolveCards(boardTarget, playerCtx);
        Assert.Contains(boardCards, c => c.InstanceId == handCard.InstanceId);
        Assert.Contains(boardCards, c => c.InstanceId == stashCard.InstanceId);
        Assert.Contains(boardCards, c => c.InstanceId == skillCard.InstanceId);
        Assert.DoesNotContain(boardCards, c => c.InstanceId == destroyedCard.InstanceId);

        // 显式指定 Destroyed 状态时可命中已摧毁卡牌
        var destroyedTarget = DeserializeNode("""
            {
              "$type": "TTargetCardSection",
              "TargetSection": "SelfHand",
              "Conditions": { "$type": "TCardConditionalState", "States": ["Destroyed"] }
            }
            """);
        var destroyedFound = TargetResolver.ResolveCards(destroyedTarget, playerCtx);
        Assert.Single(destroyedFound);
        Assert.Same(destroyedCard, destroyedFound[0]);

        // 3. 极值条件先应用前置 CooldownMax > 0 过滤，再在候选集中求 DamageAmount 最大值（排除 passive_high=500）
        var filteredExtremeTarget = DeserializeNode("""
            {
              "$type": "TTargetCardSection",
              "TargetSection": "SelfHand",
              "Conditions": {
                "$type": "TCardConditionalAnd",
                "Conditions": [
                  {
                    "$type": "TCardConditionalAttribute",
                    "Attribute": "CooldownMax",
                    "ComparisonOperator": "GreaterThan",
                    "ComparisonValue": 0.0
                  },
                  {
                    "$type": "TCardConditionalAttributeHighest",
                    "AttributeType": "DamageAmount"
                  }
                ]
              }
            }
            """);
        var extremeResult = TargetResolver.ResolveCards(filteredExtremeTarget, playerCtx);
        Assert.Single(extremeResult);
        Assert.Same(handCard, extremeResult[0]);
    }

    // ── P1-1：货架 Aura 型附魔（Deadly / Golden）局部光环预计算 ──

    [Fact]
    public void P1_01_ShelfLocalAura_DeadlyCritAndGoldenSellPrice_PrecomputedOnCandidateEntry()
    {
        var fx = Fixture.Value;
        var (run, _) = CreateStartedEngine(EHero.Vanessa);
        var shop = new ShopEngine(run, fx.Pool, fx.Repo);
        var cutlassTpl = fx.Repo.GetCard(CutlassId)!;

        var plainEntry = shop.CreateEntryFromCoreInstance(new CardInstance(cutlassTpl, CardTier.Bronze), isFree: false);
        var deadlyEntry = shop.CreateEntryFromCoreInstance(new CardInstance(cutlassTpl, CardTier.Bronze, "Deadly"), isFree: false);
        var goldenEntry = shop.CreateEntryFromCoreInstance(new CardInstance(cutlassTpl, CardTier.Bronze, "Golden"), isFree: false);

        int plainCrit = plainEntry.Attributes.GetValueOrDefault(ECardAttributeType.CritChance, 0);
        int deadlyCrit = deadlyEntry.Attributes.GetValueOrDefault(ECardAttributeType.CritChance, 0);
        int plainSell = plainEntry.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice, 0);
        int goldenSell = goldenEntry.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice, 0);

        Assert.True(deadlyCrit > plainCrit, $"Deadly 附魔货架卡应预计算 CritChance > {plainCrit}，实际={deadlyCrit}");
        Assert.True(plainSell > 0);
        Assert.Equal(plainSell * 2, goldenSell);
    }

    // ── P1-2：Skill 买价/卖价全阶对齐官方 StandardPrices（5/10/20/40/50） ──

    [Fact]
    public void P1_02_SkillBuyAndSellPrices_MatchStandardPricesAcrossAllTiers()
    {
        var fx = Fixture.Value;
        var (run, _) = CreateStartedEngine(EHero.Vanessa);
        var shop = new ShopEngine(run, fx.Pool, fx.Repo);
        var skillTpl = fx.Repo.GetCard(ArmsDealerSkillId)!;

        (CardTier Tier, int ExpectedBuy, int ExpectedSell)[] cases =
        [
            (CardTier.Bronze, 5, 2),
            (CardTier.Silver, 10, 5),
            (CardTier.Gold, 20, 10),
            (CardTier.Diamond, 40, 20),
            (CardTier.Legendary, 50, 25),
        ];

        foreach (var (tier, expBuy, expSell) in cases)
        {
            var entry = shop.CreateEntryFromCoreInstance(new CardInstance(skillTpl, tier), isFree: false);
            Assert.Equal(expBuy, entry.Attributes[ECardAttributeType.BuyPrice]);
            Assert.Equal(expSell, entry.Attributes[ECardAttributeType.SellPrice]);
        }
    }

    // ── P1-3：OnCommitPedestal 真实执行附魔（Yetarian Tomb -> Icy）与升阶（B1&B2 -> Silver） ──

    [Fact]
    public void P1_03_OnCommitPedestal_ExecutesEnchantAndUpgradeBehaviors()
    {
        var (run, engine) = CreateStartedEngine(EHero.Vanessa);
        var cutlass = AddOwnedItem(run, CutlassId, slot: 0, ETier.Bronze);

        // 1. 升级基座 B1&B2：Bronze -> Silver
        var upgPed = InjectChoiceEncounter(run, UpgradePedestalId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(upgPed.InstanceId) }).Error);
        Assert.Equal(ERunState.Pedestal, run.State);

        var upgCommit = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(cutlass.InstanceId) });
        Assert.Null(upgCommit.Error);
        Assert.Equal(ETier.Silver, cutlass.Tier);
        Assert.Contains(upgCommit.Events, e => e is GameSimEventCardUpgraded u && u.InstanceId == cutlass.InstanceId && u.NewTier == ETier.Silver);

        // 2. 附魔基座 Yetarian Tomb：施加 Icy
        var enchPed = InjectChoiceEncounter(run, YetarianTombPedestalId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(enchPed.InstanceId) }).Error);
        Assert.Equal(ERunState.Pedestal, run.State);

        var enchCommit = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(cutlass.InstanceId) });
        Assert.Null(enchCommit.Error);
        Assert.Equal(EEnchantmentType.Icy, cutlass.Enchantment);
        Assert.Contains(enchCommit.Events, e => e is GameSimEventCardEnchanted enc && enc.InstanceId == cutlass.InstanceId && enc.EnchantmentType == EEnchantmentType.Icy);
    }

    // ── P1-7：Skill 同名同阶二合一融合升阶 ──

    [Fact]
    public void P1_07_SkillFusion_BuyingIdenticalBronzeSkillUpgradesExistingToSilver()
    {
        var fx = Fixture.Value;
        var (run, engine) = CreateStartedEngine(EHero.Vanessa);

        var skillTpl = fx.Repo.GetCard(ArmsDealerSkillId)!;
        var ownedCore = new CardInstance(skillTpl, CardTier.Bronze, null, "skl_owned_test");
        var ownedEntry = new RunState.CardEntry
        {
            InstanceId = ownedCore.InstanceId,
            TemplateId = Guid.Parse(ArmsDealerSkillId),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = null,
            Socket = null,
            CoreCard = ownedCore,
        };
        RunState.PopulateMetaFromTemplate(ownedEntry, fx.Pool.Get(ArmsDealerSkillId), skillTpl);
        run.Cards[ownedEntry.InstanceId] = ownedEntry;

        var pol = InjectChoiceEncounter(run, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol.InstanceId) }).Error);

        var shop = new ShopEngine(run, fx.Pool, fx.Repo);
        var candidateSkill = shop.CreateEntryFromCoreInstance(new CardInstance(skillTpl, CardTier.Bronze), isFree: false);
        run.Cards[candidateSkill.InstanceId] = candidateSkill;
        run.SelectionSet.Add(candidateSkill.InstanceId);

        int goldBefore = run.PlayerAttributes[EPlayerAttributeType.Gold];
        var buyOut = engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(candidateSkill.InstanceId) });
        Assert.Null(buyOut.Error);

        Assert.Equal(goldBefore - 5, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(ETier.Silver, ownedEntry.Tier);
        Assert.False(run.Cards.ContainsKey(candidateSkill.InstanceId));
        Assert.Single(run.Cards.Values.Where(c => c.Owner == ECombatantId.Player && c.TemplateId == Guid.Parse(ArmsDealerSkillId)));
        Assert.Contains(buyOut.Events, e => e is GameSimEventCardFused f && f.FusedInstanceId == ownedEntry.InstanceId && f.NewTier == ETier.Silver);
    }

    // ── P1-5：ExclusionSet 初始化、增删指令与 SpawnQueryEngine 过滤 ──

    [Fact]
    public void P1_05_ExclusionSet_InitializedFromGameMode_AddAndRemove_FiltersSpawn()
    {
        var fx = Fixture.Value;
        Assert.NotEmpty(fx.Repo.GameMode.ExclusionSet);

        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, 10);
        player.InitializeExclusionSet(fx.Repo.GameMode);
        foreach (string id in fx.Repo.GameMode.ExclusionSet)
            Assert.Contains(id, player.ExclusionSet);

        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, player, null, new Random(1));
        var dummySrc = new CardInstance(fx.Repo.GetCard(CutlassId)!);

        // 动态加入 Cutlass 到 ExclusionSet，随后 SpawnQueryEngine 不得生成 Cutlass
        var addNode = DeserializeNode($$"""
            {
              "$type": "TActionGameAddToExclusionSet",
              "Ids": ["{{CutlassId}}"]
            }
            """);
        Assert.True(AbilityExecutor.ExecuteAction(addNode, dummySrc, ctx));
        Assert.Contains(CutlassId, player.ExclusionSet);

        var spawnCutlassOnly = DeserializeNode($$"""
            {
              "$type": "TSpawnContextQuery",
              "Limit": { "$type": "TFixedValue", "Value": 1.0 },
              "Groups": [
                {
                  "$type": "TSpawnGroup",
                  "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{CutlassId}}"] }],
                  "Limit": { "$type": "TFixedValue", "Value": 1.0 }
                }
              ]
            }
            """);
        Assert.Empty(SpawnQueryEngine.SpawnCards(spawnCutlassOnly, ctx));

        // 移出 ExclusionSet 后恢复可生成
        var removeNode = DeserializeNode($$"""
            {
              "$type": "TActionGameRemoveFromExclusionSet",
              "Ids": ["{{CutlassId}}"]
            }
            """);
        Assert.True(AbilityExecutor.ExecuteAction(removeNode, dummySrc, ctx));
        Assert.DoesNotContain(CutlassId, player.ExclusionSet);
        Assert.Single(SpawnQueryEngine.SpawnCards(spawnCutlassOnly, ctx));
    }

    // ── P1-6：TSpawnBehaviorInheritSize（Mak Catalyst 等同尺寸衍生） ──

    [Fact]
    public void P1_06_InheritSize_SpawnBehavior_FiltersBySelfCardSize()
    {
        var fx = Fixture.Value;
        var catalystTpl = fx.Repo.GetCard(CatalystId)!;
        Assert.Equal("Small", catalystTpl.Size);

        var player = new PlayerState("p1", "P1", HeroNames.Mak, 250, 20, 10, 10);
        var selfMedium = new CardInstance(MakeCooldownItemTemplate("medium_src", size: "Medium"));
        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, player, null, new Random(123))
        {
            SelfCard = selfMedium,
        };

        var query = DeserializeNode("""
            {
              "$type": "TSpawnContextQuery",
              "Limit": { "$type": "TFixedValue", "Value": 3.0 },
              "Behaviors": [
                { "$type": "TSpawnBehaviorInheritSize", "Inherits": true },
                { "$type": "TSpawnBehaviorIgnoreTierTable", "IgnoreTierTable": true }
              ],
              "Groups": [
                {
                  "$type": "TSpawnGroup",
                  "Limit": { "$type": "TFixedValue", "Value": 3.0 },
                  "Filters": [
                    {
                      "$type": "TSpawnFilterQuery",
                      "Constraints": { "$type": "ConstraintCardType", "Types": ["Item"] }
                    }
                  ]
                }
              ]
            }
            """);

        var spawned = SpawnQueryEngine.SpawnCards(query, ctx);
        Assert.Equal(3, spawned.Count);
        Assert.All(spawned, c => Assert.Equal("Medium", c.Template.Size));
    }

    // ── P1-10：MusicNoteHasValidSocket 模 7 音阶合法/非法槽位判定 ──

    [Fact]
    public void P1_10_MusicNoteHasValidSocket_Modulo7ScaleValidation()
    {
        var fx = Fixture.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Jules, 250, 20, 10, 10);
        player.Hand.UnlockedMask = 0x3FF;

        // 在 slot 2 放置 C 音符（index=2）作为音阶锚点
        var noteC = new CardInstance(fx.Repo.GetCard(NoteCSocketId)!) { StartSlot = 2 };
        player.SocketEffects.Add(noteC);

        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, player, null, new Random(1));
        var condD = new RuleNode { Type = "TRunConditionalMusicNoteHasValidSocket", MusicNote = JsonDocument.Parse("\"D\"").RootElement.Clone() };
        var condA = new RuleNode { Type = "TRunConditionalMusicNoteHasValidSocket", MusicNote = JsonDocument.Parse("\"A\"").RootElement.Clone() };

        // D（index=3）与 C（index=2）相差 +1 → 仅 slot 3（3 + 7 = 10 越界）合法
        Assert.True(ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(condD, ctx));

        // slot 3 若放普通 Stove（可覆盖），D 仍合法
        var stove = new CardInstance(fx.Repo.GetCard(StoveSocketId)!) { StartSlot = 3 };
        player.SocketEffects.Add(stove);
        Assert.True(ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(condD, ctx));

        // slot 3 若已被另一个音符占据（不可覆盖），D 变为非法
        player.SocketEffects.Remove(stove);
        var noteD = new CardInstance(fx.Repo.GetCard(NoteDSocketId)!) { StartSlot = 3 };
        player.SocketEffects.Add(noteD);
        Assert.False(ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(condD, ctx));

        // A（index=0）与 C（index=2）模 7 对应 slot 0 与 slot 7；只堵住 slot 0 时 slot 7 仍可用
        var noteA0 = new CardInstance(fx.Repo.GetCard(NoteASocketId)!) { StartSlot = 0 };
        player.SocketEffects.Add(noteA0);
        Assert.True(ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(condA, ctx));

        // 同时堵住 slot 7 后，A 无合法槽位返回 false
        var noteA7 = new CardInstance(fx.Repo.GetCard(NoteASocketId)!) { StartSlot = 7 };
        player.SocketEffects.Add(noteA7);
        Assert.False(ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(condA, ctx));
    }

    // ── P2-2 & P2-3：PercentChargeReduction 减免充能，以及 Level > 30 沿用 +800 HP ──

    [Fact]
    public void P2_02_And_P2_03_PercentChargeReduction_And_LevelAbove30HpIncrease()
    {
        var fx = Fixture.Value;
        Assert.Equal(800, fx.LevelUps.HealthIncreaseFor(30));
        Assert.Equal(800, fx.LevelUps.HealthIncreaseFor(31));
        Assert.Equal(800, fx.LevelUps.HealthIncreaseFor(45));

        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 500, 20, 10, 10);

        var chargerTpl = new CardTemplate
        {
            Id = "charge_src",
            InternalName = "Charger",
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["c0"] = new AbilityDefinition
                {
                    Id = "c0",
                    Trigger = DeserializeNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = DeserializeNode("""
                        {
                          "$type": "TActionCardCharge",
                          "Value": { "$type": "TFixedValue", "Value": 1000.0 },
                          "Target": { "$type": "TTargetCardSection", "TargetSection": "SelfHand" }
                        }
                        """),
                },
            },
        };
        var targetTpl = new CardTemplate
        {
            Id = "reduced_target",
            InternalName = "Reduced Target",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["CooldownMax"] = 5000,
                        ["DamageAmount"] = 10,
                        ["PercentChargeReduction"] = 50,
                    },
                },
            },
        };

        p1.Skills.Add(new CardInstance(chargerTpl));
        p1.Hand.TryAddAuto(new CardInstance(targetTpl));

        var sim = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 1, recordEvents: true);
        var chargeEvent = sim.Events.FirstOrDefault(e => e.Type == CombatEventType.ChargeApplied);
        Assert.NotNull(chargeEvent);
        Assert.Equal(500, chargeEvent!.Value);
    }

    // ── V5.1 专项测试 1：同 Tick 武器双死判玩家胜、零平局 ──

    [Fact]
    public void V51_01_SimultaneousWeaponLethal_SameTick_PlayerWins()
    {
        var fx = Fixture.Value;
        var p1 = new PlayerState("player", "Player", HeroNames.Vanessa, 100, 20, 1, 5);
        var p2 = new PlayerState("opp", "Opponent", HeroNames.Vanessa, 100, 20, 1, 5);

        CardTemplate MakeWeapon(string id)
        {
            var tpl = MakeCooldownItemTemplate(id, cooldownMs: 2000, dmg: 100);
            tpl.Tiers!["Bronze"].AbilityIds.Add("hit");
            tpl.Abilities["hit"] = new AbilityDefinition
            {
                Id = "hit",
                WorksIn = "CombatOnly",
                Trigger = DeserializeNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = DeserializeNode("""
                    {
                      "$type": "TActionPlayerDamage",
                      "ReferenceValue": {
                        "$type": "TReferenceValueCardAttribute",
                        "AttributeType": "DamageAmount",
                        "Target": { "$type": "TTargetCardSelf" }
                      },
                      "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" }
                    }
                    """),
            };
            return tpl;
        }

        p1.Hand.TryAddAuto(new CardInstance(MakeWeapon("v51_w1"), CardTier.Bronze, null, "v51_w1"));
        p2.Hand.TryAddAuto(new CardInstance(MakeWeapon("v51_w2"), CardTier.Bronze, null, "v51_w2"));

        var result = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 1, combatType: "PVP", recordEvents: true);
        Assert.Equal("player", result.WinnerPlayerId);
        Assert.False(result.IsDraw);
        Assert.Equal(0, result.Player1RemainingHp);
        Assert.Equal(0, result.Player2RemainingHp);
        Assert.Equal(2000, result.DurationMs);
        Assert.Equal(2, result.Events.Count(e => e.TimeMs == 1950 && e.Type == CombatEventType.DamageApplied));
    }

    // ── V5.1 专项测试 2：同毫秒 readyBatch 敌方致死触发器排在玩家前仍双死判玩家胜 ──

    [Fact]
    public void V51_02_SimultaneousIcdTriggerLethal_OpponentQueuedFirst_PlayerStillWins()
    {
        var fx = Fixture.Value;
        var p1 = new PlayerState("player", "Player", HeroNames.Vanessa, 100, 20, 1, 5);
        var p2 = new PlayerState("opp", "Opponent", HeroNames.Vanessa, 100, 20, 1, 5);

        CardTemplate MakeShieldTriggerItem(string id, int cooldownMs)
        {
            var tpl = new CardTemplate
            {
                Id = id,
                InternalName = id,
                Type = "Item",
                Size = "Small",
                StartingTier = "Bronze",
                Tiers = new Dictionary<string, TierData>
                {
                    ["Bronze"] = new TierData
                    {
                        Attributes = new Dictionary<string, double>
                        {
                            ["CooldownMax"] = cooldownMs,
                            ["ShieldApplyAmount"] = 1,
                        },
                        AbilityIds = ["sh"],
                    },
                },
                Abilities = new Dictionary<string, AbilityDefinition>
                {
                    ["sh"] = new AbilityDefinition
                    {
                        Id = "sh",
                        Trigger = DeserializeNode("""{"$type":"TTriggerOnCardFired"}"""),
                        Action = DeserializeNode("""
                            {
                              "$type": "TActionPlayerShieldApply",
                              "ReferenceValue": { "$type": "TFixedValue", "Value": 1.0 },
                              "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
                            }
                            """),
                    },
                },
            };
            return tpl;
        }

        CardTemplate MakeOnShieldDamageSkill(string id, int dmg) => new()
        {
            Id = id,
            InternalName = id,
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["trig"] = new AbilityDefinition
                {
                    Id = "trig",
                    Trigger = DeserializeNode("""
                        {
                          "$type": "TTriggerOnCardPerformedShield",
                          "Subject": { "$type": "TTargetCardSection", "TargetSection": "SelfHand" }
                        }
                        """),
                    Action = DeserializeNode($$"""
                        {
                          "$type": "TActionPlayerDamage",
                          "ReferenceValue": { "$type": "TFixedValue", "Value": {{dmg}}.0 },
                          "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" }
                        }
                        """),
                },
            },
        };

        // p2 于 1000ms 先入队并创建 triggerQueues 首槽，打 60 伤；2000ms p2 先出队再打 60 伤致死 p1，同帧 p1 仍执行致死 150 伤击杀含 2 盾的 p2
        p1.Hand.TryAddAuto(new CardInstance(MakeShieldTriggerItem("p1_sh", 2000), CardTier.Bronze, null, "p1_sh"));
        p1.Skills.Add(new CardInstance(MakeOnShieldDamageSkill("p1_lethal", 150), CardTier.Bronze, null, "p1_lethal"));

        p2.Hand.TryAddAuto(new CardInstance(MakeShieldTriggerItem("p2_sh", 1000), CardTier.Bronze, null, "p2_sh"));
        p2.Skills.Add(new CardInstance(MakeOnShieldDamageSkill("p2_lethal", 60), CardTier.Bronze, null, "p2_lethal"));

        var result = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 7, combatType: "PVP", recordEvents: true);
        var tick1950Dmgs = result.Events
            .Where(e => e.TimeMs == 1950 && e.Type == CombatEventType.DamageApplied)
            .ToList();
        Assert.Equal(2, tick1950Dmgs.Count);
        Assert.Equal("player", tick1950Dmgs[0].SourcePlayerId);
        Assert.Equal("opp", tick1950Dmgs[1].SourcePlayerId);
        Assert.Equal("player", result.WinnerPlayerId);
        Assert.False(result.IsDraw);
        Assert.Equal(0, result.Player1RemainingHp);
        Assert.Equal(0, result.Player2RemainingHp);
    }

    // ── V5.1 专项测试 3：沙尘暴同跳双死 & 同秒 Poison+Regen 净伤双死，且 RunEngine 真实 PvP 判胜加胜场不扣 Prestige ──

    [Fact]
    public void V51_03_SimultaneousSandstormAndDotLethal_PlayerWinsAndGainsPvpVictory()
    {
        var fx = Fixture.Value;

        // 3a. 沙尘暴同跳双死
        var sandP1 = new PlayerState("player", "P1", HeroNames.Vanessa, 1, 20, 1, 1);
        var sandP2 = new PlayerState("opp", "P2", HeroNames.Vanessa, 1, 20, 1, 1);
        var sandRes = CombatSimulator.Simulate(fx.Repo, sandP1, sandP2, seed: 1, combatType: "PVP", recordEvents: true);
        Assert.Equal("player", sandRes.WinnerPlayerId);
        Assert.False(sandRes.IsDraw);
        Assert.Equal(0, sandRes.Player1RemainingHp);
        Assert.Equal(0, sandRes.Player2RemainingHp);

        // 3b. 同秒 Poison + Regen 净伤双死（7 HP，Poison 10 - Regen 3 = 净伤 7 -> 1000ms 同死）与净存活对比（8 HP -> 1 HP 存活）
        CardTemplate MakeStartPoisonRegenSkill(string id, int poisonToOpp, int selfRegen) => new()
        {
            Id = id,
            InternalName = id,
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["dot"] = new AbilityDefinition
                {
                    Id = "dot",
                    Trigger = DeserializeNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = DeserializeNode($$"""
                        {
                          "$type": "TActionAnd",
                          "Actions": [
                            {
                              "$type": "TActionPlayerPoisonApply",
                              "ReferenceValue": { "$type": "TFixedValue", "Value": {{poisonToOpp}}.0 },
                              "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" }
                            },
                            {
                              "$type": "TActionPlayerRegenApply",
                              "ReferenceValue": { "$type": "TFixedValue", "Value": {{selfRegen}}.0 },
                              "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
                            }
                          ]
                        }
                        """),
                },
            },
        };

        var dotP1 = new PlayerState("player", "P1", HeroNames.Vanessa, 7, 20, 1, 1);
        var dotP2 = new PlayerState("opp", "P2", HeroNames.Vanessa, 7, 20, 1, 1);
        dotP1.Skills.Add(new CardInstance(MakeStartPoisonRegenSkill("dot_s1", 10, 3)));
        dotP2.Skills.Add(new CardInstance(MakeStartPoisonRegenSkill("dot_s2", 10, 3)));
        var dotTieRes = CombatSimulator.Simulate(fx.Repo, dotP1, dotP2, seed: 2, combatType: "PVP");
        Assert.Equal("player", dotTieRes.WinnerPlayerId);
        Assert.False(dotTieRes.IsDraw);
        Assert.Equal(0, dotTieRes.Player1RemainingHp);
        Assert.Equal(0, dotTieRes.Player2RemainingHp);
        Assert.True(dotTieRes.DurationMs <= 1050);

        var dotSurvP1 = new PlayerState("player", "P1", HeroNames.Vanessa, 8, 20, 1, 1);
        var dotSurvP2 = new PlayerState("opp", "P2", HeroNames.Vanessa, 7, 20, 1, 1);
        dotSurvP1.Skills.Add(new CardInstance(MakeStartPoisonRegenSkill("dot_s3", 10, 3)));
        dotSurvP2.Skills.Add(new CardInstance(MakeStartPoisonRegenSkill("dot_s4", 10, 3)));
        var dotSurvRes = CombatSimulator.Simulate(fx.Repo, dotSurvP1, dotSurvP2, seed: 3, combatType: "PVP");
        Assert.Equal("player", dotSurvRes.WinnerPlayerId);
        Assert.Equal(1, dotSurvRes.Player1RemainingHp);
        Assert.Equal(0, dotSurvRes.Player2RemainingHp);

        // 3c. 走真实 RunEngine 规则：Day 1 Hour 6 PvP 沙尘暴同跳双死 → 玩家获得胜场且 Prestige 不扣减
        string tempGhostDir = Path.Combine(Path.GetTempPath(), "bazaar_v51_ghost_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempGhostDir);
        try
        {
            string ghostLine = JsonSerializer.Serialize(new
            {
                boardId = "v51_sandstorm_tie_d1",
                day = 1,
                hero = "Vanessa",
                level = 1,
                healthMax = fx.Mode.StartingHealth,
                prestige = 20,
                wins = 0,
                losses = 0,
                unlockedSlotsMask = 120,
                powerBucket = "Normal",
                estimatedWinRate = 0.5,
                items = new[]
                {
                    new
                    {
                        slot = 3,
                        width = 1,
                        templateId = "6d6199b4-82a4-441d-9329-f4164737ac6b", // Red Gumball（无战斗内伤害/治疗/护盾）
                        internalName = "Red Gumball",
                        zhName = "Red Gumball",
                        tier = "Bronze",
                    },
                    new
                    {
                        slot = 4,
                        width = 1,
                        templateId = "6d6199b4-82a4-441d-9329-f4164737ac6b", // 凑足 Day 1 静态卫生过滤 totalWidth >= 2（仍保持 0 伤害/治疗/护盾）
                        internalName = "Red Gumball",
                        zhName = "Red Gumball",
                        tier = "Bronze",
                    },
                },
                skills = Array.Empty<object>(),
            });
            File.WriteAllText(Path.Combine(tempGhostDir, "ghost_pool_balanced.jsonl"), ghostLine + "\n");
            var customGhosts = PvpGhostPool.Load(tempGhostDir);

            var run = new RunState
            {
                RunId = "v51_sand_pvp_" + Guid.NewGuid().ToString("N")[..8],
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
            };
            var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, customGhosts);
            engine.StartRun();
            Assert.Equal(fx.Mode.StartingHealth, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
            engine.Handle(new CheatCommand { Args = ["skipstart"] });
            engine.Handle(new CheatCommand { Args = ["hour", "6"] });
            engine.Handle(new CheatCommand { Args = ["deal"] });

            Assert.Equal(ERunState.Choice, run.State);
            var pvpEntry = run.Cards[Assert.Single(run.SelectionSet)];
            Assert.Equal(ECardType.PvpEncounter, pvpEntry.Type);

            int prestigeBefore = run.PlayerAttributes[EPlayerAttributeType.Prestige];
            var pvpOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpEntry.InstanceId) });
            Assert.Null(pvpOut.Error);
            Assert.NotNull(pvpOut.Combat);
            Assert.Equal(ECombatantId.Player, pvpOut.Combat!.Winner);
            // V13.2 还原官方负血击杀视觉：致命沙尘暴/DoT 允许把生命值打到 <= 0（如负血溢出）
            Assert.Contains(pvpOut.Combat.Frames, f =>
                f.PlayerUpdates?.Attributes != null &&
                f.PlayerUpdates.Attributes.TryGetValue(EPlayerAttributeType.Health, out var u) &&
                u.CurrentValue <= 0);
            Assert.Contains(pvpOut.Combat.Frames, f =>
                f.OpponentUpdates?.Attributes != null &&
                f.OpponentUpdates.Attributes.TryGetValue(EPlayerAttributeType.Health, out var u) &&
                u.CurrentValue <= 0);
            Assert.Equal(1u, run.Victories);
            Assert.Equal(0u, run.Defeats);
            Assert.Equal(prestigeBefore, run.PlayerAttributes[EPlayerAttributeType.Prestige]);
            Assert.Equal(2u, run.Day);
            Assert.Equal(1u, run.Hour);
        }
        finally
        {
            if (Directory.Exists(tempGhostDir)) Directory.Delete(tempGhostDir, recursive: true);
        }
    }

    // ── V5.1 专项测试 4：TTriggerOnPlayerDied 濒死回血救活玩家、反杀获胜且整场仅触发一次 ──

    [Fact]
    public void V51_04_OnPlayerDied_ReviveSavesPlayerBeforeDefeatCheck()
    {
        var fx = Fixture.Value;

        CardTemplate MakeReviveSkill(string id, int healAmount) => new()
        {
            Id = id,
            InternalName = id,
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["revive"] = new AbilityDefinition
                {
                    Id = "revive",
                    Trigger = DeserializeNode("""
                        {
                          "$type": "TTriggerOnPlayerDied",
                          "Subject": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
                        }
                        """),
                    Action = DeserializeNode($$"""
                        {
                          "$type": "TActionPlayerHeal",
                          "ReferenceValue": { "$type": "TFixedValue", "Value": {{healAmount}}.0 },
                          "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
                        }
                        """),
                },
            },
        };

        CardTemplate MakeWeapon(string id, int cdMs, int dmg)
        {
            var tpl = MakeCooldownItemTemplate(id, cooldownMs: cdMs, dmg: dmg);
            tpl.Tiers!["Bronze"].AbilityIds.Add("hit");
            tpl.Abilities["hit"] = new AbilityDefinition
            {
                Id = "hit",
                WorksIn = "CombatOnly",
                Trigger = DeserializeNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = DeserializeNode("""
                    {
                      "$type": "TActionPlayerDamage",
                      "ReferenceValue": {
                        "$type": "TReferenceValueCardAttribute",
                        "AttributeType": "DamageAmount",
                        "Target": { "$type": "TTargetCardSelf" }
                      },
                      "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" }
                    }
                    """),
            };
            return tpl;
        }

        // 4a. 1980ms 玩家先被击毙（含溢伤 20），触发 OnPlayerDied 归零溢伤并回血 80 复活，2980ms 反杀敌方获胜
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, 100, 20, 1, 5);
        var p2 = new PlayerState("opp", "P2", HeroNames.Vanessa, 100, 20, 1, 5);
        p1.Skills.Add(new CardInstance(MakeReviveSkill("revive_once", 80), CardTier.Bronze, null, "revive_once"));
        p1.Hand.TryAddAuto(new CardInstance(MakeWeapon("p1_slow", 3000, 120), CardTier.Bronze, null, "p1_slow"));
        p2.Hand.TryAddAuto(new CardInstance(MakeWeapon("p2_fast", 2000, 120), CardTier.Bronze, null, "p2_fast"));

        var reviveWin = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 1, combatType: "PVP", recordEvents: true);
        Assert.Equal("player", reviveWin.WinnerPlayerId);
        Assert.Equal(80, reviveWin.Player1RemainingHp);
        Assert.Equal(0, reviveWin.Player2RemainingHp);
        Assert.True(reviveWin.DurationMs >= 3000);
        Assert.Single(reviveWin.Events, e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == "revive_once");

        // 4b. 带 Custom_0==0 限次的复活技能（对齐官方 Memento Mori / Fiery Rebirth）整场仅触发 1 次，第 2 次直接阵亡
        var p1NoWep = new PlayerState("player", "P1", HeroNames.Vanessa, 100, 20, 1, 5);
        var p2Heavy = new PlayerState("opp", "P2", HeroNames.Vanessa, 200, 20, 1, 5);
        var onceSkillTpl = MakeReviveSkill("revive_once_only", 80);
        onceSkillTpl.Attributes = new Dictionary<string, double> { ["Custom_0"] = 0 };
        onceSkillTpl.Abilities["revive"].Prerequisites =
        [
            DeserializeNode("""
            {
              "$type": "TPrerequisiteCardCount",
              "Subject": {
                "$type": "TTargetCardSelf",
                "Conditions": {
                  "$type": "TCardConditionalAttribute",
                  "Attribute": "Custom_0",
                  "ComparisonOperator": "Equal",
                  "ComparisonValue": { "$type": "TFixedValue", "Value": 0.0 }
                }
              },
              "Amount": 1
            }
            """)
        ];
        onceSkillTpl.Abilities["revive_limit"] = new AbilityDefinition
        {
            Id = "revive_limit",
            Trigger = DeserializeNode("""
                {
                  "$type": "TTriggerOnPlayerDied",
                  "Subject": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
                }
                """),
            Action = DeserializeNode("""
                {
                  "$type": "TActionCardModifyAttribute",
                  "Target": { "$type": "TTargetCardSelf" },
                  "AttributeType": "Custom_0",
                  "Operation": "Add",
                  "Value": { "$type": "TFixedValue", "Value": 1.0 }
                }
                """)
        };
        p1NoWep.Skills.Add(new CardInstance(onceSkillTpl, CardTier.Bronze, null, "revive_once_only"));
        p2Heavy.Hand.TryAddAuto(new CardInstance(MakeWeapon("p2_repeat", 2000, 120), CardTier.Bronze, null, "p2_repeat"));

        var secondDeath = CombatSimulator.Simulate(fx.Repo, p1NoWep, p2Heavy, seed: 2, combatType: "PVP", recordEvents: true);
        Assert.Equal("opp", secondDeath.WinnerPlayerId);
        Assert.Equal(0, secondDeath.Player1RemainingHp);
        Assert.Single(secondDeath.Events, e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == "revive_once_only");
    }

    // ── V5.1 专项测试 5：状态机护栏全面拦截 Choice/Combat/!CanExit/过期货架下的非法命令 ──

    [Fact]
    public void V51_05_StateGuards_RejectCommitPedestalAndExitInChoiceState()
    {
        var fx = Fixture.Value;
        var run = new RunState
        {
            RunId = "v51_guard_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
        };
        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        engine.StartRun();

        // 1. V6 官方 1.0.13122 开局直接进入 Day 1 Hour 1 Choice（StartSelectionDone=true）：拒绝 Reroll / Exit / CommitToPedestal
        Assert.True(engine.StartSelectionDone);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.NotNull(engine.Handle(new RerollCommand()).Error);
        Assert.NotNull(engine.Handle(new ExitCurrentStateCommand()).Error);

        engine.Handle(new CheatCommand { Args = ["slots"] });
        engine.Handle(new CheatCommand { Args = ["slots"] });
        engine.Handle(new CheatCommand { Args = ["gold", "100"] });
        var cutlass = AddOwnedItem(run, CutlassId, slot: 0, ETier.Bronze);

        // 2. Hour 1 Choice 状态：拒绝 CommitToPedestal / Exit / Reroll / 过期或不存在货架 ID
        Assert.Equal(ERunState.Choice, run.State);
        Assert.NotNull(engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(cutlass.InstanceId) }).Error);
        Assert.NotNull(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.NotNull(engine.Handle(new RerollCommand()).Error);
        Assert.NotNull(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId("enc_stale_invalid_999") }).Error);
        Assert.NotNull(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId("itm_stale_invalid_999"), Section = EInventorySection.Hand }).Error);
        Assert.Equal(1u, run.Day);
        Assert.Equal(1u, run.Hour);

        // 3. Hour 6 PvP Choice 状态：同样拒绝 Exit / CommitToPedestal / Reroll 且不推进天/小时
        engine.Handle(new CheatCommand { Args = ["hour", "6"] });
        engine.Handle(new CheatCommand { Args = ["deal"] });
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(ECardType.PvpEncounter, run.Cards[Assert.Single(run.SelectionSet)].Type);
        Assert.NotNull(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.NotNull(engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(cutlass.InstanceId) }).Error);
        Assert.NotNull(engine.Handle(new RerollCommand()).Error);
        Assert.Equal(1u, run.Day);
        Assert.Equal(6u, run.Hour);

        // 4. Pedestal 状态下拒绝过期货架 ID 选卡、拒绝提交非 Item/不存在的卡
        var ped = InjectChoiceEncounter(run, UpgradePedestalId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(ped.InstanceId) }).Error);
        Assert.Equal(ERunState.Pedestal, run.State);
        Assert.NotNull(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId("itm_stale_invalid_999"), Section = EInventorySection.Hand }).Error);
        Assert.NotNull(engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId("nonexistent_card") }).Error);

        // 5. Combat 状态：拒绝 CommitToPedestal / Exit / Reroll
        run.State = ERunState.Combat;
        Assert.NotNull(engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(cutlass.InstanceId) }).Error);
        Assert.NotNull(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.NotNull(engine.Handle(new RerollCommand()).Error);
    }

    // ── V5.1 专项测试 6：连续 3 天全天序与跨天强制防漏 PvP 兜底，严格满足 W + L == D - 1 ──

    [Fact]
    public void V51_06_MultiDayRun_WinPlusLossStrictlyEqualsCompletedDays()
    {
        var fx = Fixture.Value;
        var run = new RunState
        {
            RunId = "v51_multiday_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
        };
        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        engine.StartRun();
        engine.Handle(new CheatCommand { Args = ["gold", "120"] });
        engine.Handle(new CheatCommand { Args = ["slots"] });

        RunEngine.Outcome ExecCand(RunState.CardEntry cand) => cand.Type switch
        {
            ECardType.Skill => engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(cand.InstanceId) }),
            ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter
                => engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(cand.InstanceId) }),
            _ => engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(cand.InstanceId), Section = EInventorySection.Hand }),
        };

        // 1. 完成开局选包直到进入 Day 1 Hour 1 Choice
        for (int s = 0; s < 6 && !engine.StartSelectionDone; s++)
        {
            Assert.NotEmpty(run.SelectionSet);
            var cand = run.Cards[run.SelectionSet[0]];
            Assert.Null(ExecCand(cand).Error);
        }
        Assert.True(engine.StartSelectionDone);
        Assert.Equal(ERunState.Choice, run.State);

        bool didBuyItem = false;
        bool didEventStep = false;
        bool didPedestal = false;
        bool didLevelUp = false;

        // 2. 完整跑通 Day 1 ~ Day 3 全天序（商店、事件步、基座、升级、Hour 3 PvE、Hour 6 PvpEncounter）
        int guard = 0;
        while (run.Day <= 3 && guard++ < 150)
        {
            if (run.Day >= 2 && (run.Hour < 6 || (run.Hour == 6 && run.State == ERunState.Choice)))
            {
                Assert.Equal(run.Day - 1, run.Victories + run.Defeats);
            }

            if (run.State == ERunState.Choice)
            {
                if (run.Hour == 6)
                {
                    var pvp = run.Cards[Assert.Single(run.SelectionSet)];
                    Assert.Equal(ECardType.PvpEncounter, pvp.Type);
                    Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvp.InstanceId) }).Error);
                    continue;
                }

                // 若尚未购入任何物品，在 Day 1 Hour 2 注入一次武器商店确保首日必成单，保证后续 Day 2 Hour 4 基座有卡可用
                if (run.Day == 1 && run.Hour == 2 && !didBuyItem)
                {
                    var shop = InjectChoiceEncounter(run, PolShopId);
                    Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shop.InstanceId) }).Error);
                    continue;
                }

                // 在 Day 2 Hour 2 主动触发一次升级（LevelUp）与一次基座（PedestalEncounter）
                if (run.Day == 2 && run.Hour == 2 && !didLevelUp)
                {
                    didLevelUp = true;
                    engine.Handle(new CheatCommand { Args = ["exp", "20"] });
                    if (run.State == ERunState.LevelUp) continue;
                }
                if (run.Day >= 2 && run.Hour == 4 && !didPedestal &&
                    run.Cards.Values.Any(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item))
                {
                    bool hasBronze = run.Cards.Values.Any(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item && c.Tier == ETier.Bronze);
                    string pedTpl = hasBronze ? UpgradePedestalId : YetarianTombPedestalId;
                    var ped = InjectChoiceEncounter(run, pedTpl);
                    Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(ped.InstanceId) }).Error);
                    continue;
                }
                if (run.Day >= 2 && run.Hour == 5 && !didEventStep)
                {
                    var evt = InjectChoiceEncounter(run, FuturaEncounterId);
                    Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(evt.InstanceId) }).Error);
                    continue;
                }

                // Day 1 Hour 1 先买 1 件物品，之后白天优先探索事件或商店
                var choiceCands = run.SelectionSet.Select(id => run.Cards[id]).ToList();
                var chosen = (run.Day == 1 && run.Hour == 1)
                    ? choiceCands[0]
                    : (choiceCands.FirstOrDefault(c => c.Type == ECardType.PedestalEncounter)
                       ?? (!didEventStep
                           ? (choiceCands.LastOrDefault(c => c.Type == ECardType.EventEncounter) ?? choiceCands.Last())
                           : choiceCands[0]));
                Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(chosen.InstanceId) }).Error);
                continue;
            }

            if (run.State == ERunState.Pedestal)
            {
                if (!didPedestal)
                {
                    foreach (var own in run.Cards.Values
                        .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item)
                        .OrderBy(c => c.Tier))
                    {
                        var pedOut = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(own.InstanceId) });
                        if (pedOut.Error == null)
                        {
                            didPedestal = true;
                            break;
                        }
                    }
                }
                if (run.State == ERunState.Pedestal)
                    Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                continue;
            }

            if (run.State == ERunState.LevelUp)
            {
                didLevelUp = true;
                if (run.SelectionSet.Count > 0)
                {
                    var lvCand = run.Cards[run.SelectionSet[0]];
                    ExecCand(lvCand);
                }
                if (run.State == ERunState.LevelUp && run.SelectionRules.CanExit)
                    Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                continue;
            }

            if (run.State == ERunState.Loot)
            {
                if (run.SelectionRules.CanExit)
                    Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                else if (run.SelectionSet.Count > 0)
                    Assert.Null(ExecCand(run.Cards[run.SelectionSet[0]]).Error);
                continue;
            }

            // ERunState.Encounter
            var shelfCands = run.SelectionSet.Select(id => run.Cards[id]).ToList();
            if (!didBuyItem && shelfCands.Any(c => c.Type == ECardType.Item))
            {
                var item = shelfCands.First(c => c.Type == ECardType.Item);
                var buyOut = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(item.InstanceId), Section = EInventorySection.Hand });
                if (buyOut.Error == null)
                {
                    didBuyItem = true;
                    continue;
                }
            }

            if (shelfCands.Any(c => c.Type == ECardType.EncounterStep))
            {
                var step = shelfCands.First(c => c.Type == ECardType.EncounterStep);
                var stOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step.InstanceId) });
                if (stOut.Error == null)
                {
                    didEventStep = true;
                    continue;
                }
            }

            if (run.SelectionRules.CanExit)
            {
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                continue;
            }

            Assert.NotEmpty(shelfCands);
            Assert.Null(ExecCand(shelfCands[0]).Error);
        }

        Assert.Equal(4u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(3u, run.Victories + run.Defeats);
        Assert.True(didBuyItem, "expected didBuyItem");
        Assert.True(didEventStep, "expected didEventStep");
        Assert.True(didPedestal, "expected didPedestal");
        Assert.True(didLevelUp, "expected didLevelUp");

        // 3. 验证跨天防漏 PvP 兜底：推进至 Day 4 Hour 6 Choice 后，强行注入普通商店进出试图跨天，必须被兜底拦回 Hour 6 并重发 PvP
        int d4Guard = 0;
        while ((run.Hour < 6 || (run.Hour == 6 && run.State != ERunState.Choice)) && d4Guard++ < 80)
        {
            if (run.State == ERunState.Choice)
            {
                Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(run.SelectionSet[0]) }).Error);
            }
            else if (run.State == ERunState.LevelUp)
            {
                if (run.SelectionSet.Count > 0)
                    Assert.Null(ExecCand(run.Cards[run.SelectionSet[0]]).Error);
                else if (run.SelectionRules.CanExit)
                    Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            }
            else if (run.SelectionRules.CanExit)
            {
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            }
            else
            {
                Assert.Null(ExecCand(run.Cards[run.SelectionSet[0]]).Error);
            }
        }

        Assert.Equal(4u, run.Day);
        Assert.Equal(6u, run.Hour);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(3u, run.Victories + run.Defeats);

        var bypassShop = InjectChoiceEncounter(run, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(bypassShop.InstanceId) }).Error);
        int bypassLvGuard = 0;
        while ((run.State == ERunState.LevelUp || (run.State == ERunState.Encounter && run.CurrentEncounterId != PolShopId) || run.State == ERunState.Pedestal) && bypassLvGuard++ < 15)
        {
            if (run.State == ERunState.LevelUp && run.SelectionSet.Count > 0)
                Assert.Null(ExecCand(run.Cards[run.SelectionSet[0]]).Error);
            else if (run.SelectionRules.CanExit)
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            else if (run.SelectionSet.Count > 0)
                Assert.Null(ExecCand(run.Cards[run.SelectionSet[0]]).Error);
            else
                break;
        }
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(PolShopId, run.CurrentEncounterId);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

        // Pol 在数据库中具有 ExperienceAwardUponSelection=1，且自 V6 起延迟到离场结算；
        // 若此前随机路径使当前经验恰好处于差 1 升级阈值，离场会先触发 LevelUp（及可能附带的子基座/遭遇），完成升级后再由跨天兜底退回 Hour 6 Choice 补发 PvP
        int postBypassGuard = 0;
        while ((run.State == ERunState.LevelUp || run.State == ERunState.Pedestal || run.State == ERunState.Encounter) && postBypassGuard++ < 15)
        {
            if (run.State == ERunState.Pedestal || (run.SelectionRules.CanExit && run.State != ERunState.LevelUp))
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            else if (run.SelectionSet.Count > 0)
                Assert.Null(ExecCand(run.Cards[run.SelectionSet[0]]).Error);
            else if (run.SelectionRules.CanExit)
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            else
                break;
        }

        // 兜底机制必须阻止跨入 Day 5，停留在 Day 4 Hour 6 Choice 并补发 PvpEncounter
        Assert.Equal(4u, run.Day);
        Assert.Equal(6u, run.Hour);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(3u, run.Victories + run.Defeats);
        var reissuedPvp = run.Cards[Assert.Single(run.SelectionSet)];
        Assert.Equal(ECardType.PvpEncounter, reissuedPvp.Type);

        // 正常打完 Day 4 Hour 6 PvP 后（若战后经验满触发升级或子遭遇则正常完成）顺利跨入 Day 5 Hour 1，且 W+L == 4
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(reissuedPvp.InstanceId) }).Error);
        int postPvpGuard = 0;
        while (run.Day == 4 && run.State != ERunState.Choice && postPvpGuard++ < 25)
        {
            if (run.State == ERunState.Pedestal || (run.SelectionRules.CanExit && run.State != ERunState.LevelUp))
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            else if (run.SelectionSet.Count > 0)
                Assert.Null(ExecCand(run.Cards[run.SelectionSet[0]]).Error);
            else if (run.SelectionRules.CanExit)
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            else
                break;
        }
        Assert.Equal(5u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(4u, run.Victories + run.Defeats);
    }

    // ── V5.2 专项测试 1：PvpGhostPool 单日桶上限淘汰最旧 player_ 动态幽灵、保留种子并同步清理 _knownBoardIds ──

    [Fact]
    public void V52_01_PvpGhostPool_BoundsDynamicGhostsPerDayBucket()
    {
        var fx = Fixture.Value;
        string tempDir = Path.Combine(Path.GetTempPath(), "bazaar_v52_ghost_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            const int seedCount = 5;
            var seedLines = new List<string>(seedCount);
            for (int s = 0; s < seedCount; s++)
            {
                seedLines.Add(JsonSerializer.Serialize(new
                {
                    boardId = $"seed_d3_{s:D3}",
                    day = 3,
                    hero = "Vanessa",
                    level = 3,
                    healthMax = 600,
                    prestige = 20,
                    wins = 1,
                    losses = 1,
                    unlockedSlotsMask = 120,
                    powerBucket = "Normal",
                    estimatedWinRate = 0.5,
                    items = new[]
                    {
                        new
                        {
                            slot = 2,
                            width = 2,
                            templateId = CutlassId,
                            internalName = "Cutlass",
                            zhName = "Cutlass",
                            tier = "Bronze",
                        },
                        new
                        {
                            slot = 4,
                            width = 2,
                            templateId = "07df297d-e116-42a4-9596-4af674764d1b", // Globe（凑足 Day 3 静态卫生过滤 totalWidth >= 5 且无 3 件同卡异常）
                            internalName = "Globe",
                            zhName = "Globe",
                            tier = "Bronze",
                        },
                        new
                        {
                            slot = 6,
                            width = 1,
                            templateId = "03b1fd16-6b0d-4847-a76f-6c586f7c5958", // Kukri
                            internalName = "Kukri",
                            zhName = "Kukri",
                            tier = "Bronze",
                        },
                    },
                    skills = Array.Empty<object>(),
                }));
            }
            File.WriteAllLines(Path.Combine(tempDir, "ghost_pool_balanced.jsonl"), seedLines);

            var pool = PvpGhostPool.Load(tempDir);
            Assert.Equal(seedCount, pool.Count);
            Assert.Equal(0, pool.PlayerGhostCount);

            var db = new Persistence(Path.Combine(tempDir, "ghosts.db"));
            var cutlassTpl = fx.Repo.GetCard(CutlassId)!;

            RunState MakePlayerRun(string runId)
            {
                var run = new RunState
                {
                    RunId = runId,
                    Hero = EHero.Vanessa,
                    Day = 3,
                    Hour = 6,
                    Victories = 1,
                    Defeats = 1,
                    UnlockedSlots = 120,
                };
                run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 600;
                run.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
                run.PlayerAttributes[EPlayerAttributeType.Level] = 3;

                var inst = new CardInstance(cutlassTpl, CardTier.Bronze, null, "itm_" + runId)
                {
                    StartSlot = 3,
                };
                run.Cards[inst.InstanceId] = new RunState.CardEntry
                {
                    InstanceId = inst.InstanceId,
                    TemplateId = Guid.Parse(CutlassId),
                    Type = ECardType.Item,
                    Size = ECardSize.Small,
                    Tier = ETier.Bronze,
                    Owner = ECombatantId.Player,
                    Section = EInventorySection.Hand,
                    Socket = EContainerSocketId.Socket_3,
                    CoreCard = inst,
                };
                return run;
            }

            int totalDynamic = PvpGhostPool.MaxBoardsPerDayBucket + 60;
            for (int i = 0; i < totalDynamic; i++)
            {
                var recorded = pool.RecordPlayerGhost(MakePlayerRun($"v52_dyn_{i:D4}"), db);
                Assert.NotNull(recorded);
            }

            // 单日桶与池总数严格有界，且 5 条内置种子幽灵全部保留未被误淘汰
            Assert.Equal(PvpGhostPool.MaxBoardsPerDayBucket, pool.Count);
            Assert.Equal(PvpGhostPool.MaxBoardsPerDayBucket - seedCount, pool.PlayerGhostCount);

            // 最旧动态幽灵已被淘汰且其 boardId 已从 _knownBoardIds 移除，允许再次录入；最新动态幽灵仍在池内被去重拒绝
            var reAddedOldest = pool.RecordPlayerGhost(MakePlayerRun("v52_dyn_0000"), db);
            Assert.NotNull(reAddedOldest);
            Assert.Equal(PvpGhostPool.MaxBoardsPerDayBucket, pool.Count);
            Assert.Equal(PvpGhostPool.MaxBoardsPerDayBucket - seedCount, pool.PlayerGhostCount);

            var dupNewest = pool.RecordPlayerGhost(MakePlayerRun($"v52_dyn_{totalDynamic - 1:D4}"), db);
            Assert.Null(dupNewest);

            var picked = pool.PickOpponent(day: 3, playerWins: 1, playerLosses: 1, new Random(42), fx.Repo);
            Assert.NotNull(picked);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    // ── V5.2 专项测试 2：SmartBot 识别 Tractor Beam 的 AllRightCards 自禁用副作用并排至最右侧可用槽位 ──

    [Fact]
    public void V52_02_SmartBot_PlacesSelfDisableRightCardAtRightmostSlot()
    {
        const string tractorBeamId = "f929af85-1186-456a-89df-3474b32b443f";

        static JObject MakeItem(
            string iid, string tplId, string name, int socket,
            int cdMs, int dmg, params string[] tags) => new()
        {
            ["instanceId"] = iid,
            ["templateId"] = tplId,
            ["name"] = name,
            ["type"] = "Item",
            ["size"] = "Small",
            ["tier"] = "Silver",
            ["section"] = "Hand",
            ["socket"] = "Socket_" + socket,
            ["cooldownMax"] = cdMs,
            ["damageAmount"] = dmg,
            ["shieldApplyAmount"] = 0,
            ["burnApplyAmount"] = 0,
            ["poisonApplyAmount"] = 0,
            ["healAmount"] = 0,
            ["regenApplyAmount"] = 0,
            ["critChance"] = 0,
            ["multicast"] = 1,
            ["lifesteal"] = 0,
            ["tags"] = new JArray(tags),
            ["hiddenTags"] = new JArray(),
        };

        var tractor = MakeItem("itm_tractor", tractorBeamId, "Tractor Beam", 3, 6000, 150, "Tool", "Tech");
        var weapon1 = MakeItem("itm_wep1", CutlassId, "Cutlass", 4, 4000, 30, "Weapon");
        var weapon2 = MakeItem("itm_wep2", EagleTalismanId, "Katana", 5, 3000, 25, "Weapon");

        Assert.Equal(SmartStrategy.PositionalHazardKind.SelfDisableRight, SmartStrategy.GetPositionalHazardKind(tractor));
        Assert.Equal(SmartStrategy.PositionalHazardKind.None, SmartStrategy.GetPositionalHazardKind(weapon1));
        Assert.True(
            SmartStrategy.ScoreFormation(new[] { weapon1, weapon2, tractor }) >
            SmartStrategy.ScoreFormation(new[] { tractor, weapon1, weapon2 }));

        var state = new JObject
        {
            ["unlockedSlots"] = 120, // Socket_3 .. Socket_6 共 4 个解锁槽位
            ["hand"] = new JArray(tractor, weapon1, weapon2),
            ["stash"] = new JArray(),
        };

        // 首次规划必须优先把处于最左(Socket_3)的 Tractor Beam 挪到最右可用槽位(Socket_6)
        var initialMoves = SmartStrategy.PlanOptimalBoardMoves(state);
        Assert.NotEmpty(initialMoves);
        Assert.Equal("itm_tractor", initialMoves[0].InstanceId);
        Assert.Equal("Hand", initialMoves[0].Section);
        Assert.Equal("Socket_6", initialMoves[0].Socket);
        Assert.Equal("avoid_self_disable", initialMoves[0].Reason);

        // 模拟按 MarathonAuditor 单步执行 PlanOptimalBoardMoves[0]（含挤位让道）直到盘面收敛
        var items = new List<JObject> { tractor, weapon1, weapon2 };
        for (int step = 0; step < 8; step++)
        {
            var moves = SmartStrategy.PlanOptimalBoardMoves(state);
            if (moves.Count == 0) break;

            var mv = moves[0];
            Assert.Equal("Hand", mv.Section);
            int targetSock = int.Parse(mv.Socket.Substring("Socket_".Length));
            var movingItem = items.Single(x => x["instanceId"]!.ToString() == mv.InstanceId);

            // 若目标槽位被其他物品占用，把占格物品让到空余解锁槽位（模拟客户端 MoveCardCommand 挤位）
            var occupant = items.FirstOrDefault(x => !ReferenceEquals(x, movingItem) && SmartStrategy.SocketIndex(x) == targetSock);
            if (occupant != null)
            {
                int freeSock = new[] { 3, 4, 5, 6 }.First(s => items.All(x => SmartStrategy.SocketIndex(x) != s));
                occupant["socket"] = "Socket_" + freeSock;
            }
            movingItem["socket"] = mv.Socket;
        }

        int tractorSock = SmartStrategy.SocketIndex(tractor);
        int wep1Sock = SmartStrategy.SocketIndex(weapon1);
        int wep2Sock = SmartStrategy.SocketIndex(weapon2);

        Assert.Equal(6, tractorSock);
        Assert.True(tractorSock > wep1Sock && tractorSock > wep2Sock);
        Assert.Empty(SmartStrategy.PlanOptimalBoardMoves(state));
    }
}
