using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 5A 核心引擎专项修复单元测试：
/// - P1-05（RC-AURA-NONEXISTENT-ATTR-WRITE）：All Talk / Potent Potables 等无条件输出光环存在性守卫；
/// - P1-06（RC-ACTION-NONEXISTENT-ATTR-WRITE）：Sweeper Drone / Airplane Glue / Sharpen 等无条件输出行动存在性守卫；
/// - P1-04（核心侧 1ms 冷却舍入漂移）：CardInstance CooldownMax 减免补齐 Math.Round；
/// - P1-07（RC-COMBAT-PLAYER-ATTR-NO-EVENT）：TActionPlayerModifyAttribute 补发结构化 PlayerAttributeModified 事件。
/// </summary>
public sealed class Wave5A_CoreEngineFixesTests
{
    private const string AllTalkSkillId = "64727f7e-adfd-4f25-aa4e-1f127b9d5b70";
    private const string PotentPotablesSkillId = "edb9b01b-b4ce-4a77-848e-0a49b8a23c16";
    private const string SweeperDroneItemId = "2bf24a3d-6b2c-424f-9221-442fcc71bf81";
    private const string AirplaneGlueItemId = "004cb876-1ed2-4a4b-88d4-475cea76a03d";
    private const string SharpenStepId = "5b2b307f-6581-470b-b659-21f21127ae06";
    private const string CutlassItemId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string CindersItemId = "d45bb1dd-5342-4c69-b7fe-f56d3fac7d1f";
    private const string HammockItemId = "a4b48bb6-180a-49fd-bb1c-c6beb372b6de";
    private const string CatalystItemId = "694a08fe-e8ff-4a45-9693-4c68f11a1f8b";
    private const string RefrigeratorItemId = "d6eda7aa-d4fa-4992-88c7-87b89fe7f5ec";

    private static SharedGameDataFixture.SharedGameDataSnapshot Fx => SharedGameDataFixture.Shared;
    private static GameDataRepository Repo => Fx.Repo;

    private static RuleNode ParseNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;

    private static CardInstance MakeSyntheticItem(
        string id,
        string name,
        Dictionary<string, double>? baseAttrs = null,
        List<string>? tags = null,
        Dictionary<string, AbilityDefinition>? abilities = null,
        Dictionary<string, AuraDefinition>? auras = null,
        Dictionary<string, EnchantmentData>? enchantments = null,
        string? enchantment = null,
        string cardType = "Item")
    {
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = name,
            Type = cardType,
            Size = "Small",
            StartingTier = "Bronze",
            Tags = tags ?? new List<string>(),
            Attributes = baseAttrs != null
                ? new Dictionary<string, double>(baseAttrs, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            Abilities = abilities ?? new Dictionary<string, AbilityDefinition>(),
            Auras = auras ?? new Dictionary<string, AuraDefinition>(),
            Enchantments = enchantments ?? new Dictionary<string, EnchantmentData>()
        };
        return new CardInstance(tpl, CardTier.Bronze, enchantment, id + "_inst");
    }

    private static (ModRegistry Registry, GameDataRepository Repo) LoadIsolatedSampleMod(string modFolderName)
    {
        var scanIssues = new List<ModValidationIssue>();
        var pkg = ModLoader.LoadSinglePackage(
            Path.Combine(Fx.ModsRoot, modFolderName),
            modFolderName,
            scanIssues);
        Assert.NotNull(pkg);
        Assert.Empty(scanIssues.Where(i => i.Severity == ModValidationSeverity.Error));

        var registry = ModRegistry.CreateIsolated(Fx.Repo, Fx.CreateValidCardIdsCopy());
        var loadReport = registry.LoadPackages([pkg!], Fx.ModsRoot, Fx.Repo, Fx.CreateValidCardIdsCopy(), keepPreviousOnError: false);
        Assert.True(loadReport.Succeeded, string.Join(" | ", loadReport.Issues.Select(i => $"{i.Code}:{i.Message}")));
        return (registry, registry.ActiveRepository);
    }

    // ════════════════════════════════════════════════════════════════════════
    // P1-05（RC-AURA-NONEXISTENT-ATTR-WRITE）：光环引擎输出量级属性存在性守卫
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_05_AllTalk_UnconditionalDamageAura_OnlyBuffsCardsWithDamageAmount_AndSkipsNonWeapons()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "P1", HeroNames.Pygmalien, initialHealthMax: 500, initialGold: 10, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, initialHealthMax: 500, initialGold: 10, unlockedHandSlots: 10);

        // Silver All Talk：半血以上时无条件给 SelfHand +30 DamageAmount（Conditions == null）
        var allTalk = new CardInstance(repo.GetCard(AllTalkSkillId)!, CardTier.Silver, instanceId: "skl_alltalk");
        var cutlass = new CardInstance(repo.GetCard(CutlassItemId)!, CardTier.Bronze, instanceId: "itm_cutlass");
        var cinders = new CardInstance(repo.GetCard(CindersItemId)!, CardTier.Bronze, instanceId: "itm_cinders");
        var hammock = new CardInstance(repo.GetCard(HammockItemId)!, CardTier.Bronze, instanceId: "itm_hammock");
        var catalyst = new CardInstance(repo.GetCard(CatalystItemId)!, CardTier.Bronze, instanceId: "itm_catalyst");

        // 原本无 DamageAmount 但通过 Obsidian 附魔获得 DamageAmount 的道具（应被 All Talk 正常加成）
        var obsidianUtility = MakeSyntheticItem(
            "w5a_obsidian_tool",
            "Obsidian Tool",
            baseAttrs: new() { ["CooldownMax"] = 4000, ["ShieldApplyAmount"] = 10 },
            enchantments: new()
            {
                ["Obsidian"] = new EnchantmentData
                {
                    Attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["DamageAmount"] = 15 }
                }
            },
            enchantment: "Obsidian");

        double cutlassBaseDmg = cutlass.GetAttribute("DamageAmount");
        Assert.True(cutlassBaseDmg > 0);
        Assert.True(cutlass.HasNonAuraAttribute("DamageAmount"));
        Assert.True(obsidianUtility.HasNonAuraAttribute("DamageAmount"));
        Assert.False(cinders.HasNonAuraAttribute("DamageAmount"));
        Assert.False(hammock.HasNonAuraAttribute("DamageAmount"));
        Assert.False(catalyst.HasNonAuraAttribute("DamageAmount"));

        p1.Skills.Add(allTalk);
        Assert.True(p1.Hand.TryAddAuto(cutlass));
        Assert.True(p1.Hand.TryAddAuto(cinders));
        Assert.True(p1.Hand.TryAddAuto(hammock));
        Assert.True(p1.Hand.TryAddAuto(catalyst));
        Assert.True(p1.Hand.TryAddAuto(obsidianUtility));

        foreach (var scope in new[] { RuleScope.OutOfCombat, RuleScope.InCombat })
        {
            IReadOnlyList<AuraEngine.AuraEffectTargetsSnapshot>? capturedSnapshots = null;
            AuraEngine.RecalculateAuras(
                p1,
                scope == RuleScope.InCombat ? p2 : null,
                scope,
                day: 5,
                hour: 3,
                repo: repo,
                selectionCards: null,
                onFlyingTransition: null,
                onAuraSnapshot: snaps => capturedSnapshots = snaps);

            // 武器卡与附魔获得 DamageAmount 的卡精确获得 +30 伤害
            Assert.Equal(cutlassBaseDmg + 30.0, cutlass.GetAttribute("DamageAmount"), precision: 3);
            Assert.Equal(45.0, obsidianUtility.GetAttribute("DamageAmount"), precision: 3);

            // 非武器卡绝不被凭空写入 DamageAmount（杜绝红标 30 伤害角标）
            foreach (var nonWeapon in new[] { cinders, hammock, catalyst })
            {
                Assert.False(nonWeapon.AuraAdd.ContainsKey("DamageAmount"),
                    $"[{scope}] 非武器卡 {nonWeapon.Template.InternalName} 不应被 All Talk 写入 AuraAdd[DamageAmount]");
                Assert.False(nonWeapon.HasAnyAttributeKey("DamageAmount"),
                    $"[{scope}] 非武器卡 {nonWeapon.Template.InternalName} 不应出现 DamageAmount 键");
                Assert.False(nonWeapon.HasAttribute("DamageAmount"),
                    $"[{scope}] 非武器卡 {nonWeapon.Template.InternalName} HasAttribute(DamageAmount) 应为 false");
                Assert.Equal(0.0, nonWeapon.GetAttribute("DamageAmount"), precision: 3);
            }

            // 光环生效目标快照同样只包含真实受影响的卡牌
            Assert.NotNull(capturedSnapshots);
            var allTalkSnap = Assert.Single(capturedSnapshots!, s => s.SourceCardInstanceId == allTalk.InstanceId);
            Assert.Contains(cutlass.InstanceId, allTalkSnap.TargetCardInstanceIds);
            Assert.Contains(obsidianUtility.InstanceId, allTalkSnap.TargetCardInstanceIds);
            Assert.DoesNotContain(cinders.InstanceId, allTalkSnap.TargetCardInstanceIds);
            Assert.DoesNotContain(hammock.InstanceId, allTalkSnap.TargetCardInstanceIds);
            Assert.DoesNotContain(catalyst.InstanceId, allTalkSnap.TargetCardInstanceIds);
        }
    }

    [Fact]
    public void P1_05_PotentPotables_AndUtilityOrSelfAuras_RespectGuardBoundaries()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "P1", HeroNames.Mak, initialHealthMax: 500, initialGold: 10, unlockedHandSlots: 10);

        // 1. 官方实卡 Potent Potables（无条件给 SelfHand 每瓶药水 +1 PoisonApplyAmount 与 +1 BurnApplyAmount）
        var potentPotables = new CardInstance(repo.GetCard(PotentPotablesSkillId)!, CardTier.Silver, instanceId: "skl_potent");
        var cutlass = new CardInstance(repo.GetCard(CutlassItemId)!, CardTier.Bronze, instanceId: "itm_cutlass");
        var plainPotion = MakeSyntheticItem(
            "w5a_plain_potion",
            "Plain Shield Potion",
            baseAttrs: new() { ["CooldownMax"] = 4000, ["ShieldApplyAmount"] = 12 },
            tags: ["Potion"]);
        var burnCard = MakeSyntheticItem(
            "w5a_burn_card",
            "Torch",
            baseAttrs: new() { ["CooldownMax"] = 4000, ["BurnApplyAmount"] = 4 });
        var poisonCard = MakeSyntheticItem(
            "w5a_poison_card",
            "Vial",
            baseAttrs: new() { ["CooldownMax"] = 4000, ["PoisonApplyAmount"] = 3 });

        // 2. 通用效用/状态属性无条件光环（Heated / Chilled / SellPrice / PercentCooldownReduction / CritChance / Multicast / Lifesteal）
        var utilityAuraSkill = MakeSyntheticItem(
            "w5a_utility_skill",
            "Utility Aura Skill",
            cardType: "Skill",
            auras: new()
            {
                ["a_heat"] = new AuraDefinition
                {
                    Id = "a_heat",
                    ActiveIn = "HandOnly",
                    Action = ParseNode("""{"$type":"TAuraActionCardModifyAttribute","AttributeType":"Heated","Operation":"Add","Value":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""")
                },
                ["a_cdr"] = new AuraDefinition
                {
                    Id = "a_cdr",
                    ActiveIn = "HandOnly",
                    Action = ParseNode("""{"$type":"TAuraActionCardModifyAttribute","AttributeType":"PercentCooldownReduction","Operation":"Add","Value":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""")
                },
                ["a_crit"] = new AuraDefinition
                {
                    Id = "a_crit",
                    ActiveIn = "HandOnly",
                    Action = ParseNode("""{"$type":"TAuraActionCardModifyAttribute","AttributeType":"CritChance","Operation":"Add","Value":{"$type":"TFixedValue","Value":15},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""")
                },
                ["a_sell"] = new AuraDefinition
                {
                    Id = "a_sell",
                    ActiveIn = "HandOnly",
                    Action = ParseNode("""{"$type":"TAuraActionCardModifyAttribute","AttributeType":"SellPrice","Operation":"Add","Value":{"$type":"TFixedValue","Value":2},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""")
                },
                ["a_lifesteal"] = new AuraDefinition
                {
                    Id = "a_lifesteal",
                    ActiveIn = "HandOnly",
                    Action = ParseNode("""{"$type":"TAuraActionCardModifyAttribute","AttributeType":"Lifesteal","Operation":"Add","Value":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""")
                }
            });

        // 3. 自身定向光环（TTargetCardSelf）即使基础属性不含 DamageAmount 仍必须正常放行
        var selfFormulaCard = MakeSyntheticItem(
            "w5a_self_formula",
            "Self Formula Gadget",
            baseAttrs: new() { ["CooldownMax"] = 5000, ["Custom_0"] = 25 },
            auras: new()
            {
                ["a_self"] = new AuraDefinition
                {
                    Id = "a_self",
                    ActiveIn = "HandOnly",
                    Action = ParseNode("""{"$type":"TAuraActionCardModifyAttribute","AttributeType":"DamageAmount","Operation":"Add","Value":{"$type":"TFixedValue","Value":25},"Target":{"$type":"TTargetCardSelf"}}""")
                }
            });

        double plainPotionBaseSell = plainPotion.GetAttribute("SellPrice");

        p1.Skills.Add(potentPotables);
        p1.Skills.Add(utilityAuraSkill);
        Assert.True(p1.Hand.TryAddAuto(cutlass));
        Assert.True(p1.Hand.TryAddAuto(plainPotion));
        Assert.True(p1.Hand.TryAddAuto(burnCard));
        Assert.True(p1.Hand.TryAddAuto(poisonCard));
        Assert.True(p1.Hand.TryAddAuto(selfFormulaCard));

        AuraEngine.RecalculateAuras(p1, null, RuleScope.OutOfCombat, day: 3, hour: 2, repo: repo);

        // Potent Potables 仅作用于具备 BurnApplyAmount / PoisonApplyAmount 的卡
        Assert.Equal(5.0, burnCard.GetAttribute("BurnApplyAmount"), precision: 3);
        Assert.False(burnCard.HasAnyAttributeKey("PoisonApplyAmount"));

        Assert.Equal(4.0, poisonCard.GetAttribute("PoisonApplyAmount"), precision: 3);
        Assert.False(poisonCard.HasAnyAttributeKey("BurnApplyAmount"));

        Assert.False(cutlass.HasAnyAttributeKey("BurnApplyAmount"));
        Assert.False(cutlass.HasAnyAttributeKey("PoisonApplyAmount"));
        Assert.False(plainPotion.HasAnyAttributeKey("BurnApplyAmount"));
        Assert.False(plainPotion.HasAnyAttributeKey("PoisonApplyAmount"));

        // 通用状态与效用属性无条件光环全部正常放行
        Assert.Equal(1.0, plainPotion.GetAttribute("Heated"), precision: 3);
        Assert.Equal(10.0, plainPotion.GetAttribute("PercentCooldownReduction"), precision: 3);
        Assert.Equal(15.0, plainPotion.GetAttribute("CritChance"), precision: 3);
        Assert.Equal(plainPotionBaseSell + 2.0, plainPotion.GetAttribute("SellPrice"), precision: 3);
        Assert.Equal(1.0, plainPotion.GetAttribute("Lifesteal"), precision: 3);

        // 自身定向光环（TTargetCardSelf）正常生效
        Assert.Equal(25.0, selfFormulaCard.GetAttribute("DamageAmount"), precision: 3);
        // 且 selfFormulaCard 的光环层 DamageAmount 不应被 HasNonAuraAttribute 误判为基础属性
        Assert.False(selfFormulaCard.HasNonAuraAttribute("DamageAmount"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // P1-06（RC-ACTION-NONEXISTENT-ATTR-WRITE）：行动引擎输出量级属性存在性守卫
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_06_SweeperDrone_AndAirplaneGlue_SkipNonExistentOutputAttrs_AndBuffExistingOutputAttrs()
    {
        var repo = Repo;

        // A. Sweeper Drone 真实战斗：仅给具备 BurnApplyAmount 的灼烧卡累加灼烧，跳过自身/Refrigerator/Catalyst
        var p1 = new PlayerState("p1", "Mak", HeroNames.Mak, initialHealthMax: 3000, initialGold: 10, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "Dummy", HeroNames.Vanessa, initialHealthMax: 3000, initialGold: 10, unlockedHandSlots: 10);

        var sweeper = new CardInstance(repo.GetCard(SweeperDroneItemId)!, CardTier.Bronze, instanceId: "itm_sweeper");
        var fridge = new CardInstance(repo.GetCard(RefrigeratorItemId)!, CardTier.Bronze, instanceId: "itm_fridge");
        var catalyst = new CardInstance(repo.GetCard(CatalystItemId)!, CardTier.Bronze, instanceId: "itm_catalyst");
        var realBurnItem = MakeSyntheticItem(
            "w5a_real_burn",
            "Real Burn Lantern",
            baseAttrs: new() { ["CooldownMax"] = 8000, ["BurnApplyAmount"] = 5 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        Assert.True(p1.Hand.TryAddAuto(sweeper));
        Assert.True(p1.Hand.TryAddAuto(fridge));
        Assert.True(p1.Hand.TryAddAuto(catalyst));
        Assert.True(p1.Hand.TryAddAuto(realBurnItem));

        var res = CombatSimulator.Simulate(
            repo,
            p1,
            p2,
            seed: 5106,
            combatType: "PVP",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: false,
            modifiers: new CombatModifiers { MaxDurationMsOverride = 11_500, SandstormEnabled = false });

        // Sweeper Drone 冷却 5000ms，在 5000ms 与 10000ms 至少触发 2 次 -> realBurnItem 从 5 升至 7
        var lateSnap = res.Timeline.Last();
        var burnSnap = Assert.Single(lateSnap.Cards, c => c.InstanceId == realBurnItem.InstanceId);
        Assert.True(burnSnap.BurnApplyAmount >= 7, $"真灼烧卡应获 Sweeper Drone 加成 >= 7，实际={burnSnap.BurnApplyAmount}");

        // Sweeper Drone 自身、Refrigerator、Catalyst 均不得被写入 BurnApplyAmount
        foreach (var nonBurnId in new[] { sweeper.InstanceId, fridge.InstanceId, catalyst.InstanceId })
        {
            Assert.All(res.Timeline, frame =>
            {
                var snap = frame.Cards.FirstOrDefault(c => c.InstanceId == nonBurnId);
                if (snap != null)
                    Assert.Equal(0, snap.BurnApplyAmount);
            });
            Assert.DoesNotContain(res.Events, e =>
                e.Type == CombatEventType.CardAttributeModified
                && e.TargetCardInstanceId == nonBurnId
                && string.Equals(e.Detail, "BurnApplyAmount", StringComparison.OrdinalIgnoreCase));
        }

        // B. Airplane Glue（Abilities["1"]: DamageAmount, Abilities["2"]: ShieldApplyAmount，均为 TTargetCardTriggerSource + Conditions == null）
        var glueTpl = repo.GetCard(AirplaneGlueItemId)!;
        var glue = new CardInstance(glueTpl, CardTier.Bronze, instanceId: "itm_glue");
        var dmgAction = glueTpl.Abilities["1"].Action!;
        var shieldAction = glueTpl.Abilities["2"].Action!;

        var weaponOnly = MakeSyntheticItem("w5a_wpn_only", "WeaponOnly", baseAttrs: new() { ["CooldownMax"] = 3000, ["DamageAmount"] = 10 });
        var shieldOnly = MakeSyntheticItem("w5a_shd_only", "ShieldOnly", baseAttrs: new() { ["CooldownMax"] = 3000, ["ShieldApplyAmount"] = 12 });
        var toolOnly = MakeSyntheticItem("w5a_tool_only", "ToolOnly", baseAttrs: new() { ["CooldownMax"] = 3000 });

        var gp1 = new PlayerState("gp1", "Stelle", HeroNames.Stelle, initialHealthMax: 500, unlockedHandSlots: 10);
        Assert.True(gp1.Hand.TryAddAuto(glue));
        Assert.True(gp1.Hand.TryAddAuto(weaponOnly));
        Assert.True(gp1.Hand.TryAddAuto(shieldOnly));
        Assert.True(gp1.Hand.TryAddAuto(toolOnly));

        foreach (var trigSrc in new[] { weaponOnly, shieldOnly, toolOnly })
        {
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, gp1)
            {
                SelfCard = glue,
                TriggerSourceCard = trigSrc
            };
            AbilityExecutor.ExecuteAction(dmgAction, glue, ctx);
            AbilityExecutor.ExecuteAction(shieldAction, glue, ctx);
        }

        Assert.Equal(18.0, weaponOnly.GetAttribute("DamageAmount"), precision: 3);
        Assert.False(weaponOnly.HasAnyAttributeKey("ShieldApplyAmount"));

        Assert.Equal(20.0, shieldOnly.GetAttribute("ShieldApplyAmount"), precision: 3);
        Assert.False(shieldOnly.HasAnyAttributeKey("DamageAmount"));

        Assert.False(toolOnly.HasAnyAttributeKey("DamageAmount"));
        Assert.False(toolOnly.HasAnyAttributeKey("ShieldApplyAmount"));
    }

    [Fact]
    public void P1_06_Sharpen_PermanentDamageAction_SkipsNonWeapons_WhileUtilityAndExplicitActionsApply()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "Vanessa", HeroNames.Vanessa, initialHealthMax: 500, unlockedHandSlots: 10);

        var sharpenTpl = repo.GetCard(SharpenStepId)!;
        var sharpen = new CardInstance(sharpenTpl, CardTier.Bronze, instanceId: "stp_sharpen");
        var handCutlass = new CardInstance(repo.GetCard(CutlassItemId)!, CardTier.Bronze, instanceId: "itm_hand_cutlass");
        var stashCutlass = new CardInstance(repo.GetCard(CutlassItemId)!, CardTier.Bronze, instanceId: "itm_stash_cutlass");
        var catalyst = new CardInstance(repo.GetCard(CatalystItemId)!, CardTier.Bronze, instanceId: "itm_catalyst");
        var fridge = new CardInstance(repo.GetCard(RefrigeratorItemId)!, CardTier.Bronze, instanceId: "itm_fridge");

        double handCutlassBase = handCutlass.GetAttribute("DamageAmount");
        double stashCutlassBase = stashCutlass.GetAttribute("DamageAmount");

        Assert.True(p1.Hand.TryAddAuto(handCutlass));
        Assert.True(p1.Hand.TryAddAuto(catalyst));
        Assert.True(p1.Stash.TryAddAuto(stashCutlass));
        Assert.True(p1.Stash.TryAddAuto(fridge));

        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1)
        {
            SelfCard = sharpen
        };
        AbilityExecutor.ExecuteAction(sharpenTpl.Abilities["0"].Action!, sharpen, ctx);

        // 有 DamageAmount 的手牌与背包武器均永久 +30 伤
        Assert.Equal(handCutlassBase + 30.0, handCutlass.GetAttribute("DamageAmount"), precision: 3);
        Assert.Equal(stashCutlassBase + 30.0, stashCutlass.GetAttribute("DamageAmount"), precision: 3);

        // 无 DamageAmount 的非武器卡不得被 Sharpen 永久写脏
        Assert.False(catalyst.PermanentAdd.ContainsKey("DamageAmount"));
        Assert.False(catalyst.HasAnyAttributeKey("DamageAmount"));
        Assert.False(fridge.PermanentAdd.ContainsKey("DamageAmount"));
        Assert.False(fridge.HasAnyAttributeKey("DamageAmount"));

        // 通用状态/效用属性无条件行动（CritChance / SellPrice / PercentCooldownReduction / Heated）必须正常生效
        double catalystSellBefore = catalyst.GetAttribute("SellPrice");
        var buffCrit = ParseNode("""{"$type":"TActionCardModifyAttribute","AttributeType":"CritChance","Operation":"Add","Value":{"$type":"TFixedValue","Value":20},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""");
        var buffSell = ParseNode("""{"$type":"TActionCardModifyAttribute","AttributeType":"SellPrice","Operation":"Add","Value":{"$type":"TFixedValue","Value":3},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""");
        var buffCdr = ParseNode("""{"$type":"TActionCardModifyAttribute","AttributeType":"PercentCooldownReduction","Operation":"Add","Value":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""");
        var buffHeated = ParseNode("""{"$type":"TActionCardModifyAttribute","AttributeType":"Heated","Operation":"Add","Value":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""");

        AbilityExecutor.ExecuteAction(buffCrit, sharpen, ctx);
        AbilityExecutor.ExecuteAction(buffSell, sharpen, ctx);
        AbilityExecutor.ExecuteAction(buffCdr, sharpen, ctx);
        AbilityExecutor.ExecuteAction(buffHeated, sharpen, ctx);

        Assert.Equal(20.0, catalyst.GetAttribute("CritChance"), precision: 3);
        Assert.Equal(catalystSellBefore + 3.0, catalyst.GetAttribute("SellPrice"), precision: 3);
        Assert.Equal(10.0, catalyst.GetAttribute("PercentCooldownReduction"), precision: 3);
        Assert.Equal(1.0, catalyst.GetAttribute("Heated"), precision: 3);

        // 显式 Operation = "Set" 放行豁免：允许显式赋予新属性初值
        var explicitSet = ParseNode("""{"$type":"TActionCardModifyAttribute","AttributeType":"DamageAmount","Operation":"Set","Value":{"$type":"TFixedValue","Value":12},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""");
        AbilityExecutor.ExecuteAction(explicitSet, sharpen, ctx);
        Assert.Equal(12.0, catalyst.GetAttribute("DamageAmount"), precision: 3);
    }

    // ════════════════════════════════════════════════════════════════════════
    // P1-04（核心侧 1ms 舍入漂移）：CooldownMax 冷却减免 Math.Round
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(5000, 0, 0, 5000)]
    [InlineData(5000, 0, 7, 4650)]
    [InlineData(5000, 0, 10, 4500)]
    [InlineData(5000, 0, 14, 4300)]
    [InlineData(5000, 0, 29, 3550)]
    [InlineData(5000, 0, -10, 5500)]
    [InlineData(4000, 0, 7, 3720)]
    [InlineData(4000, 0, 14, 3440)]
    [InlineData(4000, 0, 29, 2840)]
    [InlineData(4000, 0, -25, 5000)]
    [InlineData(7000, 0, 29, 4970)]
    [InlineData(5000, 1000, 14, 3440)]
    [InlineData(2001, 0, 50, 1001)]
    public void P1_04_CooldownMaxReduction_RoundsToExactIntegerMilliseconds_WithoutOneMsDrift(
        double baseCooldownMs,
        double flatReduction,
        double percentReduction,
        double expectedCooldownMs)
    {
        var card = MakeSyntheticItem(
            "w5a_cd_probe",
            "Cooldown Probe",
            baseAttrs: new() { ["CooldownMax"] = baseCooldownMs });

        if (Math.Abs(flatReduction) > 1e-9)
            card.ModifyAttribute("FlatCooldownReduction", "Add", flatReduction, DurationTarget.Combat);
        if (Math.Abs(percentReduction) > 1e-9)
            card.ModifyAttribute("PercentCooldownReduction", "Add", percentReduction, DurationTarget.Combat);

        double actual = card.GetAttribute("CooldownMax");
        double actualZeroFloor = card.GetAttributeWithCooldownFloor("CooldownMax", minCooldownFloorMs: 0.0);

        Assert.Equal(expectedCooldownMs, actual);
        Assert.Equal(expectedCooldownMs, actualZeroFloor);
        // 严格无浮点尾差：Ceiling 与 Floor 必须完全相等，彻底消除 5001 / 4001ms 1ms 毛刺
        Assert.Equal(actual, Math.Ceiling(actual));
        Assert.Equal(actual, Math.Floor(actual));
    }

    [Fact]
    public void P1_04_CooldownMaxReduction_StillRespectsFloorLimit()
    {
        var card = MakeSyntheticItem(
            "w5a_cd_floor",
            "Cooldown Floor Probe",
            baseAttrs: new() { ["CooldownMax"] = 1500 });
        card.ModifyAttribute("PercentCooldownReduction", "Add", 50, DurationTarget.Combat);

        // 默认下限 1000ms；自定义下限 0ms 时为精确 750ms
        Assert.Equal(1000.0, card.GetAttribute("CooldownMax"));
        Assert.Equal(750.0, card.GetAttributeWithCooldownFloor("CooldownMax", minCooldownFloorMs: 0.0));
    }

    // ════════════════════════════════════════════════════════════════════════
    // P1-07（RC-COMBAT-PLAYER-ATTR-NO-EVENT）：TActionPlayerModifyAttribute 补发结构化 CombatEvent
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_07_StarportCompassBlade_InCombatGoldGain_EmitsStructuredPlayerAttributeModifiedEvents()
    {
        var (_, modRepo) = LoadIsolatedSampleMod("bazaar-chronicles-sample");
        var bladeTpl = modRepo.GetCard("bazaar-chronicles-sample:starport_compass_blade");
        Assert.NotNull(bladeTpl);

        var p1 = new PlayerState("p1", "Dooley", HeroNames.Dooley, initialHealthMax: 2000, initialGold: 1, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "Dummy", HeroNames.Vanessa, initialHealthMax: 2000, initialGold: 0, unlockedHandSlots: 10);
        var blade = new CardInstance(bladeTpl!, CardTier.Bronze, instanceId: "itm_compass_blade");
        Assert.True(p1.Hand.TryPlaceOrShift(blade, 0));

        // Bronze 短刃 CooldownMax = 4000ms，GoldGainAmount = 1；12500ms 内精确开火 3 次（4s / 8s / 12s）
        var res = CombatSimulator.Simulate(
            modRepo,
            p1,
            p2,
            seed: 5107,
            combatType: "PVP",
            recordTimeline: false,
            recordEvents: true,
            syncPermanentChanges: true,
            modifiers: new CombatModifiers { MaxDurationMsOverride = 12_500, SandstormEnabled = false });

        var goldEvents = res.Events
            .Where(e => e.Type == CombatEventType.PlayerAttributeModified && e.PlayerAttributeName == "Gold")
            .ToList();

        Assert.Equal(3, goldEvents.Count);
        for (int i = 0; i < goldEvents.Count; i++)
        {
            var ev = goldEvents[i];
            Assert.Equal("p1", ev.SourcePlayerId);
            Assert.Equal("p1", ev.TargetPlayerId);
            Assert.Equal(blade.InstanceId, ev.SourceCardInstanceId);
            Assert.Equal("TActionPlayerModifyAttribute", ev.ActionType);
            Assert.False(ev.IsSkillSource);
            Assert.Equal(1.0, ev.Value, precision: 6);

            Assert.True(ev.TryGetPlayerAttributeModification(out var attrName, out var prevVal, out var curVal));
            Assert.Equal("Gold", attrName);
            Assert.Equal(1.0 + i, prevVal, precision: 6);
            Assert.Equal(2.0 + i, curVal, precision: 6);
            Assert.Equal(prevVal, ev.PlayerAttributePreviousValue);
            Assert.Equal(curVal, ev.PlayerAttributeCurrentValue);
        }

        // 同步核对账本落袋：战前 1 金 + 战中 3 次 +1 金 = 4 金
        Assert.Equal(4, p1.Gold);
    }

    [Fact]
    public void P1_07_PlayerModifyAttribute_HandlesIncomeHealthMaxZeroDelta_AndDetailHelpers()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, initialHealthMax: 500, initialGold: 0, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Dooley, initialHealthMax: 500, initialGold: 0, unlockedHandSlots: 10);

        var srcCard = MakeSyntheticItem(
            "w5a_player_attr_emitter",
            "Ledger Emitter",
            baseAttrs: new() { ["CooldownMax"] = 2000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionAnd",
                        "Actions": [
                            {
                                "$type": "TActionPlayerModifyAttribute",
                                "AttributeType": "Income",
                                "Operation": "Add",
                                "Value": { "$type": "TFixedValue", "Value": 2 },
                                "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
                            },
                            {
                                "$type": "TActionPlayerModifyAttribute",
                                "AttributeType": "HealthMax",
                                "Operation": "Add",
                                "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                                "Value": { "$type": "TFixedValue", "Value": 50 },
                                "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
                            },
                            {
                                "$type": "TActionPlayerModifyAttribute",
                                "AttributeType": "Gold",
                                "Operation": "Add",
                                "Value": { "$type": "TFixedValue", "Value": 0 },
                                "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
                            }
                        ]
                    }
                    """)
                }
            });

        Assert.True(p1.Hand.TryPlaceOrShift(srcCard, 0));

        var res = CombatSimulator.Simulate(
            repo,
            p1,
            p2,
            seed: 5108,
            combatType: "PVP",
            recordTimeline: false,
            recordEvents: true,
            syncPermanentChanges: false,
            modifiers: new CombatModifiers { MaxDurationMsOverride = 1000, SandstormEnabled = false });

        var attrEvents = res.Events
            .Where(e => e.Type == CombatEventType.PlayerAttributeModified)
            .ToList();

        // Gold +0（delta == 0）不应发射冗余事件；仅 Income 与 HealthMax 两条有效变更
        Assert.Equal(2, attrEvents.Count);

        var incomeEv = Assert.Single(attrEvents, e => e.PlayerAttributeName == "Income");
        Assert.True(incomeEv.TryGetPlayerAttributeModification(out var incName, out var incPrev, out var incCur));
        Assert.Equal("Income", incName);
        Assert.Equal(5.0, incPrev, precision: 6);
        Assert.Equal(7.0, incCur, precision: 6);
        Assert.Equal(2.0, incomeEv.Value, precision: 6);

        var maxHpEv = Assert.Single(attrEvents, e => e.PlayerAttributeName == "HealthMax");
        Assert.True(maxHpEv.TryGetPlayerAttributeModification(out var hpName, out var hpPrev, out var hpCur));
        Assert.Equal("HealthMax", hpName);
        Assert.Equal(500.0, hpPrev, precision: 6);
        Assert.Equal(550.0, hpCur, precision: 6);
        Assert.Equal(50.0, maxHpEv.Value, precision: 6);

        // 辅助方法边界校验：非 PlayerAttributeModified 事件不误解析；纯属性名 Detail 兼容退化
        var cardModEv = new CombatEvent { Type = CombatEventType.CardAttributeModified, Detail = "DamageAmount|10|20", Value = 10 };
        Assert.Null(cardModEv.PlayerAttributeName);
        Assert.False(cardModEv.TryGetPlayerAttributeModification(out _, out _, out _));

        var legacyPlayerEv = new CombatEvent { Type = CombatEventType.PlayerAttributeModified, Detail = "Gold", Value = 3 };
        Assert.Equal("Gold", legacyPlayerEv.PlayerAttributeName);
        Assert.Null(legacyPlayerEv.PlayerAttributePreviousValue);
        Assert.Null(legacyPlayerEv.PlayerAttributeCurrentValue);
        Assert.False(legacyPlayerEv.TryGetPlayerAttributeModification(out _, out _, out _));
    }

    [Fact]
    public void P0_03_CardInstance_ReassignInstanceId_PreservesReferenceAndBaseAttributeOverrides()
    {
        var card = MakeSyntheticItem("w5a_reassign", "Reassign Probe", baseAttrs: new() { ["DamageAmount"] = 10 });
        card.BaseAttributes["Tiny"] = 1;

        var originalRef = card;
        card.ReassignInstanceId("itm_0042_ALIGNED");

        Assert.Same(originalRef, card);
        Assert.Equal("itm_0042_ALIGNED", card.InstanceId);
        Assert.Equal(1.0, card.BaseAttributes["Tiny"]);
        Assert.Throws<ArgumentException>(() => card.ReassignInstanceId("   "));
    }
}
