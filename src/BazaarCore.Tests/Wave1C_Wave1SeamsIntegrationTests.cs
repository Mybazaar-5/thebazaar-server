using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1C 回归测试（文件 4）：覆盖全部 5 项 Wave 1 预留接缝闭环（S1 P0-17、S2 P0-01、S3 P0-06、S4 P0-03/04、S5 P0-07）
/// 以及 P0-42 看门狗阶段 4 × RecordRunProgress × 同步 PvP 并发锁序压测。
/// 因用例 #13 写入 ModRuntimeHooks、用例 #18 触及 SessionRegistry，严格标注 [Collection("GlobalStaticState")] 严守 Wave 0 并行约定。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1C_Wave1SeamsIntegrationTests
{
    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    [Fact]
    public void Seam_P0_17_RunEngine_Simulate_UsesIsolatedModRuntimeContext_EvenWhenGlobalHooksCleared()
    {
        var shared = SharedGameDataFixture.Shared;
        string mechId = "w1c_seam:isolated_shield_pulse";
        string cardGuid = Guid.NewGuid().ToString("D");

        var mechDef = new MechanicDefinition
        {
            Id = mechId,
            NameZh = "隔离护盾脉冲",
            DescZh = "开火时通过局级隔离 ModRuntimeContext 获得护盾",
            Parameters = [new MechanicParameterDef { Key = "shieldAmt", LabelZh = "护盾", Type = "int", DefaultValue = J(33), Min = 1, Max = 300 }],
            Hooks =
            [
                new MechanicHookDefinition
                {
                    Id = "on_fire_shield",
                    Hook = "OnCardFired",
                    Scope = "SelfCard",
                    Filter = new MechanicHookFilter { OnlyWhenSelfSource = true },
                    Steps =
                    [
                        new MechanicStep
                        {
                            Op = "ApplyStatus",
                            Status = "Shield",
                            PlayerTarget = "Self",
                            Amount = new MechanicValueExpr { Param = "shieldAmt" }
                        }
                    ]
                }
            ]
        };

        var cardJson = JsonSerializer.Serialize(new
        {
            Id = cardGuid,
            Type = "Item",
            InternalName = "W1C_IsolatedMechItem",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Weapon" },
            HiddenTags = Array.Empty<string>(),
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                    Attributes = new Dictionary<string, double> { ["BuyPrice"] = 2, ["SellPrice"] = 1, ["CooldownMax"] = 2000, ["DamageAmount"] = 8 }
                }
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardFired" },
                    Action = new Dictionary<string, object>
                    {
                        ["$type"] = "TActionPlayerDamage",
                        ["Target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["TargetMode"] = "Opponent" }
                    }
                }
            },
            Auras = new Dictionary<string, object>(),
            CustomMechanics = new[]
            {
                new CardMechanicAttachment { MechanicId = mechId, Params = new() { ["shieldAmt"] = J(33) } }
            }
        });

        using var overlay = shared.CreateMemoryOverlay(customCards: [(cardGuid, cardJson)]);
        var isolatedCtx = new ModRuntimeContext(
            MechanicsById: new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase) { [mechId] = mechDef }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            ActionHandlers: FrozenDictionary<string, ICustomActionHandler>.Empty,
            TriggerHandlers: FrozenDictionary<string, ICustomTriggerHandler>.Empty,
            ModeHooks: FrozenDictionary<string, IGameModeHook>.Empty,
            PipelineInterceptors: Array.Empty<ICombatPipelineInterceptor>());

        ModRuntimeHooks.ClearAll();
        try
        {
            var run = new RunState
            {
                RunId = $"w1c_seam_p017_{Guid.NewGuid():N}",
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                Day = 1,
                Hour = 3,
                State = ERunState.Choice,
                StartSelectionDone = true,
                RngSeed = 20260017
            };
            run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 500;
            run.PlayerAttributes[EPlayerAttributeType.Health] = 500;

            var itemTpl = overlay.Repo.GetCard(cardGuid)!;
            var itemCore = new CardInstance(itemTpl, CardTier.Bronze, null, "w1c_iso_item");
            var itemEntry = new RunState.CardEntry
            {
                InstanceId = itemCore.InstanceId,
                TemplateId = Guid.Parse(cardGuid),
                Type = ECardType.Item,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_0,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                CoreCard = itemCore
            };
            run.Cards[itemEntry.InstanceId] = itemEntry;
            run.SyncEntryFromCoreCardAndDiff(itemEntry);

            var combatEncTpl = overlay.Repo.CardsById.Values.First(c =>
                string.Equals(c.Type, "CombatEncounter", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(c.Id, out _));
            var encEntry = new RunState.CardEntry
            {
                InstanceId = "w1c_pve_enc",
                TemplateId = Guid.Parse(combatEncTpl.Id),
                Type = ECardType.CombatEncounter,
                Section = null,
                Socket = EContainerSocketId.Socket_0,
                Size = ECardSize.Medium,
                Tier = ETier.Bronze
            };
            run.Cards[encEntry.InstanceId] = encEntry;
            run.SelectionSet.Add(encEntry.InstanceId);

            var engine = new RunEngine(
                run,
                overlay.Pool,
                overlay.Monsters,
                overlay.LevelUps,
                overlay.Mode,
                overlay.Repo,
                shared.Ghosts,
                shared.ArchetypeIndex,
                modRuntimeContext: isolatedCtx);

            var outcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(encEntry.InstanceId) });
            Assert.NotNull(outcome.Combat);
            bool playerGainedShield = outcome.Combat!.Frames.Any(f =>
                f.PlayerUpdates?.Attributes != null &&
                f.PlayerUpdates.Attributes.TryGetValue(EPlayerAttributeType.Shield, out var sh) &&
                sh.CurrentValue >= 33);
            Assert.True(playerGainedShield, "全局 ModRuntimeHooks 清空时，RunEngine 必须透传局级 ModRuntimeContext 触发机制 +33 护盾！");
        }
        finally
        {
            ModRuntimeHooks.ClearAll();
        }
    }

    [Fact]
    public void Seam_P0_01_RunEngineAndEncounterStep_ConsumeTypedSelectionRulesData_AndNoVanillaCustomCardId()
    {
        var shared = SharedGameDataFixture.Shared;
        var nonDefaultStep = shared.Repo.CardsById.Values.FirstOrDefault(c =>
            string.Equals(c.Type, "EncounterStep", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(c.Id, out _) &&
            c.Abilities.Values.Any(a =>
                a.Action != null &&
                string.Equals(a.Action.Type, "TActionGameDealCards", StringComparison.OrdinalIgnoreCase) &&
                a.Action.GetSelectionContextRulesData() is { } sr &&
                (!sr.SelectionIsFree || sr.CanSelectMultiple || sr.WillAutoSellOnExit || sr.MaxSelections.HasValue)));
        Assert.NotNull(nonDefaultStep);

        var expectedRules = nonDefaultStep!.Abilities.Values
            .Select(a => a.Action)
            .First(act => act != null
                && string.Equals(act.Type, "TActionGameDealCards", StringComparison.OrdinalIgnoreCase)
                && act.GetSelectionContextRulesData() is { } sr
                && (!sr.SelectionIsFree || sr.CanSelectMultiple || sr.WillAutoSellOnExit || sr.MaxSelections.HasValue))!
            .GetSelectionContextRulesData()!;

        // 1. 验证 EncounterStepEngine 路径消费强类型 SelectionRulesData
        var runStep = new RunState
        {
            RunId = $"w1c_p001_step_{Guid.NewGuid():N}",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 3,
            Hour = 2,
            State = ERunState.Encounter,
            StartSelectionDone = true,
            RngSeed = 20260001
        };
        var stepEntry = new RunState.CardEntry
        {
            InstanceId = "w1c_step_inst",
            TemplateId = Guid.Parse(nonDefaultStep.Id),
            Type = ECardType.EncounterStep,
            Section = null,
            Socket = EContainerSocketId.Socket_0,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            CoreCard = new CardInstance(nonDefaultStep, CardTier.Bronze, null, "w1c_step_inst")
        };
        runStep.Cards[stepEntry.InstanceId] = stepEntry;
        runStep.SelectionSet.Add(stepEntry.InstanceId);

        var shop = new ShopEngine(runStep, shared.Pool, shared.Repo, runStep.CustomRules, shared.ArchetypeIndex);
        var stepEngine = new EncounterStepEngine(runStep, shared.Pool, shared.Repo, shop, new Random(20260001));
        var stepRes = stepEngine.ExecuteStep(stepEntry);
        Assert.True(stepRes.Success);
        Assert.Equal(expectedRules.SelectionIsFree, runStep.SelectionRules.SelectionIsFree);
        Assert.Equal(expectedRules.CanSelectMultiple, runStep.SelectionRules.CanSelectMultiple);
        Assert.Equal(expectedRules.WillAutoSellOnExit, runStep.SelectionRules.WillAutoSellOnExit);
        if (expectedRules.MaxSelections.HasValue)
            Assert.Equal(expectedRules.MaxSelections.Value, shop.CurrentMaxSelections);

        // 2. 验证 RunEngine.ConsumeOutOfCombatBatchRequests（经 OutOfCombatController.DealtSelectionPayloads）路径
        //    同时校验：纯原版发牌卡的 CustomCardId 必须为 null（不再误把纯原版局踢出标准幽灵池！）
        var vanillaItemTpl = shared.Repo.CardsById.Values.First(c =>
            string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(c.Id, out _));
        string triggerDealGuid = Guid.NewGuid().ToString("D");
        string triggerDealJson = JsonSerializer.Serialize(new
        {
            Id = triggerDealGuid,
            Type = "Item",
            InternalName = "W1C_DealWithRulesProbe",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Tool" },
            HiddenTags = Array.Empty<string>(),
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                    Attributes = new Dictionary<string, double> { ["BuyPrice"] = 2, ["SellPrice"] = 1 }
                }
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    ActiveIn = "HandAndStash",
                    Trigger = new Dictionary<string, object>
                    {
                        ["$type"] = "TTriggerOnCardSold",
                        ["Subject"] = new Dictionary<string, object> { ["$type"] = "TTargetCardSelf" }
                    },
                    Action = new Dictionary<string, object>
                    {
                        ["$type"] = "TActionGameDealCards",
                        ["SpawnMode"] = "Replace",
                        ["SelectionContextRules"] = new Dictionary<string, object>
                        {
                            ["CanSelectMultiple"] = true,
                            ["SelectionIsFree"] = false,
                            ["CanExit"] = true,
                            ["WillAutoSellOnExit"] = true,
                            ["MaxSelections"] = 2
                        },
                        ["SpawnContext"] = new Dictionary<string, object>
                        {
                            ["$type"] = "TSpawnContextQuery",
                            ["Groups"] = new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    ["$type"] = "TSpawnGroup",
                                    ["Filters"] = new object[]
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["$type"] = "TSpawnFilterIdList",
                                            ["Ids"] = new[] { vanillaItemTpl.Id }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            },
            Auras = new Dictionary<string, object>()
        });

        using var overlay = shared.CreateMemoryOverlay(customCards: [(triggerDealGuid, triggerDealJson)]);
        var runOoc = new RunState
        {
            RunId = $"w1c_p001_ooc_{Guid.NewGuid():N}",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 2,
            Hour = 2,
            State = ERunState.Choice,
            StartSelectionDone = true,
            RngSeed = 20260002
        };
        var engine = new RunEngine(
            runOoc, overlay.Pool, overlay.Monsters, overlay.LevelUps,
            overlay.Mode, overlay.Repo, shared.Ghosts, shared.ArchetypeIndex);
        engine.StartRun();
        runOoc.State = ERunState.Choice;

        var probeTpl = overlay.Repo.GetCard(triggerDealGuid)!;
        var probeEntry = new RunState.CardEntry
        {
            InstanceId = "w1c_sell_deal_probe",
            TemplateId = Guid.Parse(triggerDealGuid),
            Type = ECardType.Item,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            CoreCard = new CardInstance(probeTpl, CardTier.Bronze, null, "w1c_sell_deal_probe")
        };
        runOoc.Cards[probeEntry.InstanceId] = probeEntry;
        runOoc.SyncEntryFromCoreCardAndDiff(probeEntry);

        engine.Handle(new SellCardCommand { InstanceId = new InstanceId(probeEntry.InstanceId) });

        Assert.True(runOoc.SelectionRules.CanSelectMultiple);
        Assert.False(runOoc.SelectionRules.SelectionIsFree);
        Assert.True(runOoc.SelectionRules.WillAutoSellOnExit);
        Assert.NotEmpty(runOoc.SelectionSet);
        var dealtEntry = runOoc.Cards[runOoc.SelectionSet[0]];
        Assert.Equal(Guid.Parse(vanillaItemTpl.Id), dealtEntry.TemplateId);
        Assert.Null(dealtEntry.CustomCardId);
    }

    [Fact]
    public void Seam_P0_06_PostCombatSync_PersistsAllAdditionalTransformedReplacements_InSingleEvent()
    {
        var shared = SharedGameDataFixture.Shared;
        var largeTpl = shared.Repo.CardsById.Values.First(c =>
            string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Size, "Large", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(c.Id, out _));
        var smallTpls = shared.Repo.CardsById.Values.Where(c =>
            string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Size, "Small", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(c.Id, out _)).Take(3).ToList();
        Assert.Equal(3, smallTpls.Count);

        var run = new RunState
        {
            RunId = $"w1c_p006_{Guid.NewGuid():N}",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 3,
            Hour = 6,
            State = ERunState.Choice,
            StartSelectionDone = true,
            RngSeed = 20260006
        };
        var engine = new RunEngine(
            run, shared.Pool, shared.CreateMonstersCopy(), shared.LevelUps,
            shared.Mode, shared.Repo, shared.Ghosts, shared.ArchetypeIndex);

        var origCore = new CardInstance(largeTpl, CardTier.Bronze, null, "w1c_large_orig");
        var origEntry = new RunState.CardEntry
        {
            InstanceId = origCore.InstanceId,
            TemplateId = Guid.Parse(largeTpl.Id),
            Type = ECardType.Item,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            Size = ECardSize.Large,
            Tier = ETier.Bronze,
            CoreCard = origCore
        };
        run.Cards[origEntry.InstanceId] = origEntry;

        var rep1 = new CardInstance(smallTpls[0], CardTier.Bronze, null, "w1c_rep_1") { TransformedFromInstanceId = origCore.InstanceId };
        var rep2 = new CardInstance(smallTpls[1], CardTier.Bronze, null, "w1c_rep_2") { TransformedFromInstanceId = origCore.InstanceId };
        var rep3 = new CardInstance(smallTpls[2], CardTier.Bronze, null, "w1c_rep_3") { TransformedFromInstanceId = origCore.InstanceId };

        var p1Core = new PlayerState("player", "P1", "Vanessa", 500);
        var permSync = new PostCombatPermanentSyncResult
        {
            Applied = true,
            UpdatedCards =
            {
                new CardPermanentSyncDelta
                {
                    InstanceId = origCore.InstanceId,
                    SourceCard = origCore,
                    TransformReplacementTemplateId = smallTpls[0].Id,
                    TransformedReplacementCard = rep1,
                    AdditionalTransformedReplacements = { rep2, rep3 }
                }
            }
        };
        permSync.PermanentReplacements[origCore.InstanceId] = new List<CardInstance> { rep1, rep2, rep3 };

        var despawnEvents = new List<IGameSimEvent>();
        engine.ApplyPostCombatPermanentSync(p1Core, permSync, despawnEvents);

        Assert.False(run.Cards.ContainsKey(origCore.InstanceId));
        var persistedReps = run.Cards.Values.Where(c => c.Type == ECardType.Item).ToList();
        Assert.Equal(3, persistedReps.Count);

        var transEvents = despawnEvents.OfType<GameSimEventCardTransformed>().ToList();
        Assert.Single(transEvents);
        Assert.Equal(origCore.InstanceId, transEvents[0].OriginalInstanceId);
        Assert.Equal(3, transEvents[0].TransformedCards.Count);
        foreach (var tc in transEvents[0].TransformedCards)
            Assert.True(run.Cards.ContainsKey(tc.InstanceId));
    }

    [Fact]
    public void Seam_P0_03_P0_04_RunEngine_NoHealthCompounding_PreservesDayHourLayers_AndCloneNonCombatState()
    {
        var shared = SharedGameDataFixture.Shared;
        string dayHpItemGuid = Guid.NewGuid().ToString("D");
        string dayCdItemGuid = Guid.NewGuid().ToString("D");
        string dayHpItemJson = JsonSerializer.Serialize(new
        {
            Id = dayHpItemGuid,
            Type = "Item",
            InternalName = "W1C_RelaxDayHpProbe",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Tool" },
            HiddenTags = Array.Empty<string>(),
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                    Attributes = new Dictionary<string, double> { ["BuyPrice"] = 2, ["SellPrice"] = 1, ["CooldownMax"] = 4000 }
                }
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnFightStarted" },
                    Action = new Dictionary<string, object>
                    {
                        ["$type"] = "TActionPlayerModifyAttribute",
                        ["AttributeType"] = "HealthMax",
                        ["Operation"] = "Add",
                        ["Duration"] = new Dictionary<string, object>
                        {
                            ["$type"] = "TDeterminantDuration",
                            ["DurationType"] = "UntilEndOfDay"
                        },
                        ["Value"] = new Dictionary<string, object> { ["$type"] = "TFixedValue", ["Value"] = 100 },
                        ["Target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["TargetMode"] = "Self" }
                    }
                }
            },
            Auras = new Dictionary<string, object>()
        });

        string dayCdItemJson = JsonSerializer.Serialize(new
        {
            Id = dayCdItemGuid,
            Type = "Item",
            InternalName = "W1C_DayCdAndLayerProbe",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Weapon" },
            HiddenTags = Array.Empty<string>(),
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                    Attributes = new Dictionary<string, double> { ["BuyPrice"] = 2, ["SellPrice"] = 1, ["CooldownMax"] = 5000, ["DamageAmount"] = 80 }
                },
                ["Silver"] = new
                {
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                    Attributes = new Dictionary<string, double> { ["BuyPrice"] = 4, ["SellPrice"] = 2, ["CooldownMax"] = 4500, ["DamageAmount"] = 120 }
                }
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnFightStarted" },
                    Action = new Dictionary<string, object>
                    {
                        ["$type"] = "TActionCardModifyAttribute",
                        ["AttributeType"] = "CooldownMax",
                        ["Operation"] = "Subtract",
                        ["Duration"] = new Dictionary<string, object>
                        {
                            ["$type"] = "TDeterminantDuration",
                            ["DurationType"] = "UntilEndOfDay"
                        },
                        ["Value"] = new Dictionary<string, object> { ["$type"] = "TFixedValue", ["Value"] = 500 },
                        ["Target"] = new Dictionary<string, object> { ["$type"] = "TTargetCardSelf" }
                    }
                }
            },
            Auras = new Dictionary<string, object>()
        });

        using var overlay = shared.CreateMemoryOverlay(customCards:
        [
            (dayHpItemGuid, dayHpItemJson),
            (dayCdItemGuid, dayCdItemJson)
        ]);

        var titanRules = CustomRunRuleSet.CreatePreset(EGameModePreset.PureOfficial, ESpecialModeId.Standard);
        titanRules.MutatorIds = new List<string> { MutatorCatalog.TitanVitality };

        var run = new RunState
        {
            RunId = $"w1c_p003_p004_{Guid.NewGuid():N}",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 2,
            Hour = 2,
            State = ERunState.Choice,
            StartSelectionDone = true,
            CustomRules = titanRules,
            RngSeed = 20260034
        };
        const int baseHp = 400;
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = baseHp;
        run.PlayerAttributes[EPlayerAttributeType.Health] = baseHp;

        var hpCore = new CardInstance(overlay.Repo.GetCard(dayHpItemGuid)!, CardTier.Bronze, null, "w1c_relax_card");
        var cdCore = new CardInstance(overlay.Repo.GetCard(dayCdItemGuid)!, CardTier.Bronze, null, "w1c_cd_card");
        cdCore.ApplyEnchantment("Heavy", DurationTarget.Permanent, preventOverride: false);
        cdCore.ApplyEnchantment("Fiery", DurationTarget.Day, preventOverride: false);
        cdCore.ApplyEnchantment("Turbo", DurationTarget.Hour, preventOverride: false);
        cdCore.SetTier(CardTier.Silver, isTemporary: false);

        var hpEntry = new RunState.CardEntry
        {
            InstanceId = hpCore.InstanceId,
            TemplateId = Guid.Parse(dayHpItemGuid),
            Type = ECardType.Item,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            CoreCard = hpCore
        };
        var cdEntry = new RunState.CardEntry
        {
            InstanceId = cdCore.InstanceId,
            TemplateId = Guid.Parse(dayCdItemGuid),
            Type = ECardType.Item,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_1,
            Size = ECardSize.Small,
            Tier = ETier.Silver,
            CoreCard = cdCore
        };
        run.Cards[hpEntry.InstanceId] = hpEntry;
        run.Cards[cdEntry.InstanceId] = cdEntry;
        run.SyncEntryFromCoreCardAndDiff(hpEntry);
        run.SyncEntryFromCoreCardAndDiff(cdEntry);

        var engine = new RunEngine(
            run, overlay.Pool, overlay.Monsters, overlay.LevelUps,
            overlay.Mode, overlay.Repo, shared.Ghosts, shared.ArchetypeIndex);

        var combatEncTpl = overlay.Repo.CardsById.Values.First(c =>
            string.Equals(c.Type, "CombatEncounter", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(c.Id, out _));

        for (int fight = 1; fight <= 2; fight++)
        {
            run.State = ERunState.Choice;
            run.Day = 2;
            run.Hour = (uint)(fight + 1);
            run.SelectionSet.Clear();
            string encInst = $"w1c_fight_{fight}";
            run.Cards[encInst] = new RunState.CardEntry
            {
                InstanceId = encInst,
                TemplateId = Guid.Parse(combatEncTpl.Id),
                Type = ECardType.CombatEncounter,
                Section = null,
                Socket = EContainerSocketId.Socket_0,
                Size = ECardSize.Medium,
                Tier = ETier.Bronze
            };
            run.SelectionSet.Add(encInst);
            var outFight = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(encInst) });
            Assert.NotNull(outFight.Combat);
        }

        Assert.Equal(200.0, run.PlayerDayAdd.GetValueOrDefault("HealthMax", 0), 3);
        Assert.Equal(baseHp + 200, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);

        Assert.Equal(-1000.0, cdEntry.CoreCard!.DayAdd.GetValueOrDefault("CooldownMax", 0), 3);
        Assert.Equal("Heavy", cdEntry.CoreCard.PermanentEnchantment);
        Assert.Equal("Fiery", cdEntry.CoreCard.DayEnchantment);
        Assert.Equal("Turbo", cdEntry.CoreCard.HourEnchantment);

        var spawnSync = new PostCombatPermanentSyncResult { Applied = true };
        var spawnedRaw = cdCore.CloneNonCombatState("w1c_spawned_cd_raw");
        spawnSync.SpawnedCards.Add(spawnedRaw);
        engine.ApplyPostCombatPermanentSync(new PlayerState("player", "P1", "Vanessa", baseHp), spawnSync, new List<IGameSimEvent>());
        var clonedEntry = run.Cards.Values.First(c => c.InstanceId != cdEntry.InstanceId && c.TemplateId == cdEntry.TemplateId);
        Assert.Equal("Heavy", clonedEntry.CoreCard!.PermanentEnchantment);
        Assert.Equal("Fiery", clonedEntry.CoreCard.DayEnchantment);
        Assert.Equal("Turbo", clonedEntry.CoreCard.HourEnchantment);
        Assert.Equal(-1000.0, clonedEntry.CoreCard.DayAdd.GetValueOrDefault("CooldownMax", 0), 3);
        Assert.Equal(CardTier.Silver, clonedEntry.CoreCard.Tier);
    }

    [Fact]
    public void Seam_P0_07_BazaarServer_ZeroLegacyApplyEnchantmentCalls_AndPreservesDayHourOnPermEnchant()
    {
        var shared = SharedGameDataFixture.Shared;
        string serverRoot = Path.Combine(shared.RepoRoot, "src", "BazaarServer");
        Assert.True(Directory.Exists(serverRoot), $"未找到目录：{serverRoot}");

        var legacyPattern = new Regex(
            @"ApplyEnchantment\s*\([^)]*(isTemporary\s*:|,\s*(?:true|false)\s*,\s*DurationTarget\.)",
            RegexOptions.Compiled);
        var violations = new List<string>();
        foreach (string csFile in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(csFile);
            for (int i = 0; i < lines.Length; i++)
            {
                if (legacyPattern.IsMatch(lines[i]))
                    violations.Add($"{Path.GetRelativePath(shared.RepoRoot, csFile)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.Empty(violations);

        var itemTpl = shared.Repo.CardsById.Values.First(c =>
            string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(c.Id, out _));
        var core = new CardInstance(itemTpl, CardTier.Bronze, null, "w1c_ench_layers");
        core.ApplyEnchantment("Heavy", DurationTarget.Permanent, preventOverride: false);
        core.ApplyEnchantment("Fiery", DurationTarget.Day, preventOverride: false);
        core.ApplyEnchantment("Turbo", DurationTarget.Hour, preventOverride: false);

        var run = new RunState
        {
            RunId = $"w1c_p007_{Guid.NewGuid():N}",
            Hero = EHero.Vanessa,
            Day = 2,
            Hour = 2
        };
        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(itemTpl.Id),
            Type = ECardType.Item,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            CoreCard = core
        };
        run.Cards[entry.InstanceId] = entry;
        run.SyncEntryFromCoreCardAndDiff(entry);

        string json = RunSnapshotSerializer.Serialize(run, startSelectionDone: true);
        Assert.True(RunSnapshotSerializer.TryDeserialize(json, shared.Repo, out var restored, out _));
        var rc = restored!.Cards[entry.InstanceId].CoreCard!;
        Assert.Equal("Heavy", rc.PermanentEnchantment);
        Assert.Equal("Fiery", rc.DayEnchantment);
        Assert.Equal("Turbo", rc.HourEnchantment);
        Assert.Equal("Turbo", rc.Enchantment);

        rc.ApplyEnchantment("Icy", DurationTarget.Permanent, preventOverride: false);
        Assert.Equal("Icy", rc.PermanentEnchantment);
        Assert.Equal("Fiery", rc.DayEnchantment);
        Assert.Equal("Turbo", rc.HourEnchantment);
    }

    [Fact]
    public async Task Seam_P0_42_WatchdogAndRunProgress_ZeroDeadlockUnderConcurrency()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1c_lockdag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Persistence? db = null;
        try
        {
            db = new Persistence(Path.Combine(tempDir, "state.db"));
            var accounts = new AccountStore(db);
            var sessions = new SessionStore();
            var lobbies = new LobbyStore(db);
            var pvpCoord = new TournamentPvpCoordinator(db, lobbies);

            var pA = accounts.GetOrCreateByName("DagA", "game", "dag_a");
            var pB = accounts.GetOrCreateByName("DagB", "game", "dag_b");
            string aidA = pA.AccountId.ToString("N");
            string aidB = pB.AccountId.ToString("N");

            var lobby = lobbies.Create(pA, "W1C锁序压测房", "Tournament", playerCap: 2, isPublic: true);
            Assert.True(lobbies.JoinWithResult(lobby.Code, pB).Ok);
            Guid tsid = Guid.NewGuid();
            lobbies.StartCountdown(lobby.LobbyId, tsid);

            for (uint d = 1; d <= 3; d++)
                lobbies.EnsureAndGetAssignment(lobby, d, aidA, sessions);

            var initB = new InitializeRunCommand { SelectedHero = EHero.Vanessa, PlayMode = EPlayMode.Tournament, TournamentSessionId = tsid };
            var sessB = sessions.Create(aidB, pB.Username, initB);
            var runB = new RunState
            {
                RunId = sessB.RunId,
                AccountId = aidB,
                Username = pB.Username,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                TournamentSessionId = tsid,
                Day = 1,
                Hour = 2,
                State = ERunState.Choice,
                StartSelectionDone = true
            };
            var engB = new RunEngine(
                runB, shared.Pool, shared.CreateMonstersCopy(), shared.LevelUps,
                shared.Mode, shared.Repo, shared.Ghosts, shared.ArchetypeIndex,
                lobbies: lobbies, sessions: sessions, pvpCoordinator: pvpCoord, db: db);
            SessionRegistry.Attach(sessB.Sid, runB, engB);

            lock (lobby.Members)
            {
                var mA = lobby.Members.First(x => x.AccountId == aidA);
                mA.CurrentDay = 3;
                mA.IsReadyAtHour6Pvp = true;
                mA.ConnectionState = "online";
                mA.LastSeenAt = DateTime.UtcNow;

                var mB = lobby.Members.First(x => x.AccountId == aidB);
                mB.CurrentDay = 1;
                mB.ConnectionState = "ai_takeover";
                mB.LastSeenAt = DateTime.UtcNow.AddMinutes(-10);
            }

            var runA = new RunState
            {
                RunId = $"dag_run_a_{Guid.NewGuid():N}",
                AccountId = aidA,
                Username = pA.Username,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                TournamentSessionId = tsid,
                Day = 3,
                Hour = 6,
                State = ERunState.Choice,
                StartSelectionDone = true
            };
            var staleDay1RunB = new RunState
            {
                RunId = sessB.RunId,
                AccountId = aidB,
                Username = pB.Username,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                TournamentSessionId = tsid,
                Day = 1,
                Hour = 2,
                State = ERunState.Choice,
                StartSelectionDone = true
            };

            using var startGate = new ManualResetEventSlim(false);
            var t1 = Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < 200; i++)
                    lobbies.RunWatchdogTick(sessions, pvpCoord);
            });
            var t2 = Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < 200; i++)
                    lobbies.RecordRunProgress(lobby, aidA, runA);
            });
            var t3 = Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < 200; i++)
                {
                    uint d = (uint)((i % 3) + 1);
                    pvpCoord.LockPlayerBoardForDay(tsid, d, new LockedPvpBoard(
                        aidA, "DagA", "Vanessa", d, 400, 20, 2, 0, 10, 5, "{}"));
                    pvpCoord.RecordOrGetResolvedMatch(tsid, d, aidA, aidB, new SymmetricHumanPvpOutcome(
                        aidA, aidB, 100 + (int)d, (int)d, "", null, null));
                }
            });

            var sw = Stopwatch.StartNew();
            startGate.Set();
            var all = Task.WhenAll(t1, t2, t3);
            var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(ReferenceEquals(finished, all), $"并发锁序压测疑似死锁，耗时已超 15s（当前 {sw.Elapsed.TotalSeconds:F1}s）");
            await all;

            // 并发压测结束后再回传陈旧 Day 1 进度给 aidB，验证单调守卫绝不回退已托管推进/淘汰状态
            lobbies.RecordRunProgress(lobby, aidB, staleDay1RunB);
            lock (lobby.Members)
            {
                var mB = lobby.Members.First(x => x.AccountId == aidB);
                Assert.True(mB.CurrentDay >= 2 || mB.HasFinishedRun);
            }
        }
        finally
        {
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
