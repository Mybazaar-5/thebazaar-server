using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Tests.TestInfrastructure;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 4B 轨 B 自动化回归套件（校验器 3 道逻辑语义闸门 + ModRuleCatalog 全预设 + mods/ 全样例包）：
/// 1. 闸门 1：TPlayerConditionalAttribute / TCardConditionalAttribute 字段误写 attributeType 或缺失 attribute 拦截；
/// 2. 闸门 2：TReferenceValue* 缺失 target 拦截（严格放行 Aura 合法逐目标用法与 TReferenceValueAttributeChange）；
/// 3. 闸门 3：战中触发修改卡牌/玩家属性缺失 duration 意外永久化拦截（保护局外触发器、经济属性与显式永久成长卡）；
/// 4. ModRuleCatalog 全部官方预设逐项组装真跑 ModValidator（0 Error + 新 3 闸门 0 Warning）；
/// 5. mods/ 目录全部样例包逐包真跑 ModValidator（0 Error + 新 3 闸门 0 Warning）。
/// </summary>
public sealed class Wave4B_ModValidatorAndCatalogTests
{
    private const string KukriTemplateId = "03b1fd16-6b0d-4847-a76f-6c586f7c5958"; // Small Item 白名单载体
    private const string StepCarrierId = "0659ca31-9f4d-412e-84b2-d535b502afa6";   // EncounterStep 白名单载体
    private const string EventCarrierId = "11287697-dd45-4edc-9539-780a26001043";  // EventEncounter 白名单载体

    private static readonly string[] Wave4BGateCodes =
    [
        "CONDITIONAL_ATTRIBUTE_FIELD_INVALID",
        "REFERENCE_VALUE_MISSING_TARGET",
        "COMBAT_MODIFY_ATTR_MISSING_DURATION"
    ];

    private static SharedGameDataFixture.SharedGameDataSnapshot Fx => SharedGameDataFixture.Shared;

    private static CustomCardDefinition CreateHealthyBaseCard(string modId, string slug = "test_item")
    {
        return new CustomCardDefinition
        {
            CustomId = $"{modId}:{slug}",
            BaseVisualTemplateId = KukriTemplateId,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            SpawningEligibility = "Always",
            Heroes = [HeroNames.Vanessa],
            Tags = ["Weapon", "Tool"],
            HiddenTags = ["Damage"],
            TitleZh = "测试标准短刃",
            DescZh = "用于验证 ModValidator 逻辑语义闸门的标准物品。",
            ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害"],
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new()
                {
                    Attributes = new()
                    {
                        ["BuyPrice"] = 2,
                        ["SellPrice"] = 1,
                        ["CooldownMax"] = 4000,
                        ["DamageAmount"] = 15,
                        ["ShieldApplyAmount"] = 15,
                        ["BurnApplyAmount"] = 3,
                        ["PoisonApplyAmount"] = 3,
                        ["HealAmount"] = 15,
                        ["RegenApplyAmount"] = 3,
                        ["HasteAmount"] = 1000,
                        ["ChargeAmount"] = 1000,
                        ["Custom_0"] = 5,
                        ["Custom_1"] = 2
                    },
                    AbilityIds = [],
                    AuraIds = []
                }
            },
            Abilities = new Dictionary<string, AbilityDefinition>(),
            Auras = new Dictionary<string, AuraDefinition>()
        };
    }

    private static ModPackage WrapSingleCard(string modId, CustomCardDefinition card)
    {
        return new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest
            {
                Id = modId,
                Name = $"测试包-{modId}",
                Version = "1.0.0",
                Scope = "multiplayer-safe"
            },
            Cards = { card }
        };
    }

    private static RuleNode ParseRule(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)
           ?? throw new InvalidOperationException("RuleNode 反序列化失败");

    // ════════════════════════════════════════════════════════════════════════
    // 1. ModRuleCatalog 全部官方预设逐项真跑 ModValidator + 元数据正确性
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Catalog_AllOfficialPresets_PassRealModValidator_WithZeroErrorsAndZeroLogicWarnings()
    {
        var dtoJson = JsonSerializer.Serialize(ModRuleCatalog.BuildCatalogDto(), GameDatabaseLoader.JsonOptions);
        using var doc = JsonDocument.Parse(dtoJson);

        // 核实 durations 元数据中文释义未颠倒（TDeterminantDuration 为战斗/小时/当日结束，TCombatDuration 为固定毫秒时长）
        var durations = doc.RootElement.GetProperty("durations").EnumerateArray().ToList();
        var detDur = durations.First(d => d.GetProperty("id").GetString() == "TDeterminantDuration");
        var cmbDur = durations.First(d => d.GetProperty("id").GetString() == "TCombatDuration");
        Assert.Contains("战斗", detDur.GetProperty("nameZh").GetString() ?? string.Empty);
        Assert.Contains("毫秒", cmbDur.GetProperty("nameZh").GetString() ?? string.Empty);

        var presets = doc.RootElement.GetProperty("presets").EnumerateArray().ToList();
        Assert.True(presets.Count >= 10, $"官方预设数量异常：{presets.Count}");

        var whitelist = Fx.CreateValidCardIdsCopy();
        int abilityOrAuraCount = 0;
        int stepCount = 0;

        foreach (var preset in presets)
        {
            string id = preset.GetProperty("id").GetString() ?? "<unknown>";
            string scope = preset.GetProperty("scope").GetString() ?? string.Empty;
            string descZh = preset.GetProperty("descZh").GetString() ?? string.Empty;
            var templateEl = preset.GetProperty("template");
            string tplRaw = templateEl.GetRawText();

            string modId = $"preset-check-{id.Replace('_', '-')}";
            ModPackage pkg;

            if (scope == "encounterStep")
            {
                stepCount++;
                var ab = JsonSerializer.Deserialize<AbilityDefinition>(tplRaw, GameDatabaseLoader.JsonOptions)!;
                ab.Id = "0";
                var stepCard = new CustomCardDefinition
                {
                    CustomId = $"{modId}:step_{id}",
                    BaseVisualTemplateId = StepCarrierId,
                    Type = "EncounterStep",
                    Size = "Medium",
                    StartingTier = "Bronze",
                    TitleZh = $"预设步骤-{id}",
                    DescZh = descZh,
                    Attributes = new() { ["BuyPrice"] = 0 },
                    Abilities = new() { ["0"] = ab }
                };
                pkg = new ModPackage
                {
                    RootDirectory = Path.GetTempPath(),
                    Manifest = new ModManifest { Id = modId, Name = modId, Version = "1.0.0", Scope = "multiplayer-safe" },
                    Events =
                    {
                        new CustomEventDefinition
                        {
                            EventId = $"{modId}:ev_{id}",
                            EventType = "EventEncounter",
                            BaseVisualTemplateId = EventCarrierId,
                            TitleZh = $"预设事件-{id}",
                            DescZh = descZh,
                            Steps = { stepCard }
                        }
                    }
                };
            }
            else if (scope == "aura")
            {
                abilityOrAuraCount++;
                var card = CreateHealthyBaseCard(modId, $"card_{id}");
                card.PassiveTooltipsZh = [descZh];
                var au = JsonSerializer.Deserialize<AuraDefinition>(tplRaw, GameDatabaseLoader.JsonOptions)!;
                au.Id = "0";
                card.Auras["0"] = au;
                card.Tiers!["Bronze"].AuraIds.Add("0");
                pkg = WrapSingleCard(modId, card);
            }
            else
            {
                abilityOrAuraCount++;
                var card = CreateHealthyBaseCard(modId, $"card_{id}");
                card.PassiveTooltipsZh = [descZh];
                var ab = JsonSerializer.Deserialize<AbilityDefinition>(tplRaw, GameDatabaseLoader.JsonOptions)!;
                ab.Id = "0";
                card.Abilities["0"] = ab;
                card.Tiers!["Bronze"].AbilityIds.Add("0");

                // 专项核实 #2/#3 关键预设的规范字段
                if (id == "neighbor_used_buff_dmg")
                {
                    Assert.NotNull(ab.Action?.Duration);
                    Assert.Equal("TDeterminantDuration", ab.Action!.Duration!.Type);
                    Assert.Equal("UntilEndOfCombat", ab.Action.Duration.DurationType);
                    var valNode = ab.Action.GetValueNode(GameDatabaseLoader.JsonOptions);
                    Assert.NotNull(valNode?.Target);
                    Assert.Equal("TTargetCardSelf", valNode!.Target!.Type);
                }
                pkg = WrapSingleCard(modId, card);
            }

            if (id == "aura_gold_scaling_dmg")
            {
                Assert.Contains("\"attribute\":\"Gold\"", tplRaw.Replace(" ", string.Empty));
                Assert.DoesNotContain("\"attributeType\":\"Gold\"", tplRaw.Replace(" ", string.Empty));
            }

            var report = ModValidator.RunTenGateValidation(
                pkg, Array.Empty<ModPackage>(), Fx.Repo, whitelist, runDynamicGates: false);

            var allIssues = report.Gates.SelectMany(g => g.Issues).ToList();
            Assert.True(report.Passed && report.ErrorCount == 0,
                $"官方预设 '{id}' 未通过 ModValidator：{string.Join(" | ", allIssues.Select(i => $"{i.Severity}:{i.Code}:{i.Message}"))}");

            var newGateIssues = allIssues.Where(i => Wave4BGateCodes.Contains(i.Code, StringComparer.Ordinal)).ToList();
            Assert.Empty(newGateIssues);
        }

        Assert.True(abilityOrAuraCount >= 8);
        Assert.True(stepCount >= 2);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2. mods/ 全部样例包逐包真跑 ModValidator（0 Error + 新 3 闸门 0 违规）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AllSampleModsInRepo_PassRealModValidator_WithZeroErrorsAndZeroWave4BViolations()
    {
        var modDirs = Directory.GetDirectories(Fx.ModsRoot)
            .Where(d => File.Exists(Path.Combine(d, "mod.json")))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains(modDirs, d => Path.GetFileName(d).Equals("bazaar-chronicles-sample", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(modDirs, d => Path.GetFileName(d).Equals("clockwork-forge", StringComparison.OrdinalIgnoreCase));

        var whitelist = Fx.CreateValidCardIdsCopy();
        foreach (string dir in modDirs)
        {
            string dirName = Path.GetFileName(dir);
            var scanIssues = new List<ModValidationIssue>();
            var pkg = ModLoader.LoadSinglePackage(dir, dirName, scanIssues);
            Assert.NotNull(pkg);
            Assert.Empty(scanIssues.Where(i => i.Severity == ModValidationSeverity.Error));

            var report = ModValidator.RunTenGateValidation(
                pkg!, Array.Empty<ModPackage>(), Fx.Repo, whitelist, runDynamicGates: false);

            var allIssues = report.Gates.SelectMany(g => g.Issues).ToList();
            Assert.True(report.Passed && report.ErrorCount == 0,
                $"样例 Mod '{dirName}' 存在校验错误：{string.Join(" | ", allIssues.Select(i => $"{i.Severity}:{i.Code}:{i.Message}"))}");

            var logicViolations = allIssues.Where(i => Wave4BGateCodes.Contains(i.Code, StringComparer.Ordinal)).ToList();
            Assert.True(logicViolations.Count == 0,
                $"样例 Mod '{dirName}' 触发新语义闸门违规：{string.Join(" | ", logicViolations.Select(i => $"{i.Severity}:{i.Code}:{i.Message}"))}");

            // 黄金样例包静态 8 闸门必须全部 PASS（含 0 Warning）
            if (dirName.Equals("bazaar-chronicles-sample", StringComparison.OrdinalIgnoreCase))
            {
                Assert.All(report.Gates.Where(g => g.GateId <= 8), g =>
                    Assert.True(g.Status == "PASS", $"Gate #{g.GateId} 状态为 {g.Status}：{string.Join(" | ", g.Issues.Select(x => $"{x.Code}:{x.Message}"))}"));
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 3. 闸门 1 负例与正例：条件属性字段误写 attributeType / 缺失 attribute
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Gate1_ConditionalAttributeField_InterceptsMisusedAttributeTypeAndMissingAttribute()
    {
        var whitelist = Fx.CreateValidCardIdsCopy();

        // 负例 A：TPlayerConditionalAttribute 误写 attributeType="Gold"（旧版提灯与预设 Bug）
        var badPlayerCondCard = CreateHealthyBaseCard("neg-g1-a", "bad_lantern");
        badPlayerCondCard.Auras["0"] = new AuraDefinition
        {
            Id = "0",
            ActiveIn = "HandOnly",
            Prerequisites =
            [
                ParseRule("""
                {
                  "$type": "TPrerequisitePlayer",
                  "subject": {
                    "$type": "TTargetPlayerAbsolute",
                    "targetMode": "Player",
                    "conditions": {
                      "$type": "TPlayerConditionalAttribute",
                      "attributeType": "Gold",
                      "comparisonOperator": "GreaterThanOrEqual",
                      "comparisonValue": { "$type": "TFixedValue", "value": 20.0 }
                    }
                  }
                }
                """)
            ],
            Action = ParseRule("""
            {
              "$type": "TAuraActionCardModifyAttribute",
              "attributeType": "ShieldApplyAmount",
              "operation": "Multiply",
              "value": { "$type": "TFixedValue", "value": 2.0 },
              "target": { "$type": "TTargetCardSelf" }
            }
            """)
        };
        badPlayerCondCard.Tiers!["Bronze"].AuraIds.Add("0");
        var repA = ModValidator.RunTenGateValidation(WrapSingleCard("neg-g1-a", badPlayerCondCard), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.False(repA.Passed);
        Assert.Contains(repA.Gates[3].Issues, i =>
            i.Severity == ModValidationSeverity.Error
            && i.Code == "CONDITIONAL_ATTRIBUTE_FIELD_INVALID"
            && (i.FixHintZh?.Contains("attribute", StringComparison.OrdinalIgnoreCase) == true));

        // 负例 B：TCardConditionalAttribute 误写 attributeType
        var badCardCondCard = CreateHealthyBaseCard("neg-g1-b", "bad_card_cond");
        badCardCondCard.Abilities["0"] = new AbilityDefinition
        {
            Id = "0",
            Trigger = ParseRule("""
            {
              "$type": "TTriggerOnItemUsed",
              "subject": {
                "$type": "TTargetCardPositional",
                "origin": "Self",
                "targetMode": "Neighbor",
                "conditions": {
                  "$type": "TCardConditionalAttribute",
                  "attributeType": "DamageAmount",
                  "comparisonOperator": "GreaterThan",
                  "comparisonValue": { "$type": "TFixedValue", "value": 0 }
                }
              }
            }
            """),
            Action = ParseRule("""{"$type":"TActionPlayerDamage","target":{"$type":"TTargetPlayerRelative","targetMode":"Opponent"}}""")
        };
        badCardCondCard.Tiers!["Bronze"].AbilityIds.Add("0");
        var repB = ModValidator.RunTenGateValidation(WrapSingleCard("neg-g1-b", badCardCondCard), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.False(repB.Passed);
        Assert.Contains(repB.Gates[3].Issues, i => i.Severity == ModValidationSeverity.Error && i.Code == "CONDITIONAL_ATTRIBUTE_FIELD_INVALID");

        // 负例 C：TPlayerConditionalAttributeHighest 误写 attribute 而缺 attributeType
        var badHighestCard = CreateHealthyBaseCard("neg-g1-c", "bad_highest");
        badHighestCard.Auras["0"] = new AuraDefinition
        {
            Id = "0",
            Prerequisites =
            [
                ParseRule("""
                {
                  "$type": "TPrerequisitePlayer",
                  "subject": {
                    "$type": "TTargetPlayerRelative",
                    "targetMode": "Self",
                    "conditions": {
                      "$type": "TPlayerConditionalAttributeHighest",
                      "attribute": "HealthMax"
                    }
                  }
                }
                """)
            ],
            Action = ParseRule("""{"$type":"TAuraActionCardModifyAttribute","attributeType":"DamageAmount","operation":"Add","value":{"$type":"TFixedValue","value":5},"target":{"$type":"TTargetCardSelf"}}""")
        };
        badHighestCard.Tiers!["Bronze"].AuraIds.Add("0");
        var repC = ModValidator.RunTenGateValidation(WrapSingleCard("neg-g1-c", badHighestCard), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.False(repC.Passed);
        Assert.Contains(repC.Gates[3].Issues, i => i.Severity == ModValidationSeverity.Error && i.Code == "CONDITIONAL_ATTRIBUTE_FIELD_INVALID");

        // 正例：标准 attribute="Gold" 顺利通过零报错
        var goodCard = CreateHealthyBaseCard("pos-g1", "good_lantern");
        goodCard.Auras["0"] = new AuraDefinition
        {
            Id = "0",
            ActiveIn = "HandOnly",
            Prerequisites =
            [
                ParseRule("""
                {
                  "$type": "TPrerequisitePlayer",
                  "subject": {
                    "$type": "TTargetPlayerRelative",
                    "targetMode": "Self",
                    "conditions": {
                      "$type": "TPlayerConditionalAttribute",
                      "attribute": "Gold",
                      "comparisonOperator": "GreaterThanOrEqual",
                      "comparisonValue": { "$type": "TFixedValue", "value": 20.0 }
                    }
                  }
                }
                """)
            ],
            Action = ParseRule("""
            {
              "$type": "TAuraActionCardModifyAttribute",
              "attributeType": "ShieldApplyAmount",
              "operation": "Multiply",
              "value": { "$type": "TFixedValue", "value": 2.0 },
              "target": { "$type": "TTargetCardSelf" }
            }
            """)
        };
        goodCard.Tiers!["Bronze"].AuraIds.Add("0");
        var repGood = ModValidator.RunTenGateValidation(WrapSingleCard("pos-g1", goodCard), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.True(repGood.Passed, repGood.SummaryZh);
        Assert.DoesNotContain(repGood.Gates[3].Issues, i => i.Code == "CONDITIONAL_ATTRIBUTE_FIELD_INVALID");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 4. 闸门 2 负例与正例：TReferenceValue* 缺失 target（含 Aura 合法例外保护）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Gate2_ReferenceValueMissingTarget_InterceptsBrokenRefs_AndAllowsValidAuraTargetNull()
    {
        var whitelist = Fx.CreateValidCardIdsCopy();

        // 负例 A：非 Aura Ability 中 TReferenceValueCardAttribute 缺失 target（旧版短刃/特许状 Bug）
        var badCardRef = CreateHealthyBaseCard("neg-g2-a", "bad_card_ref");
        badCardRef.Abilities["0"] = new AbilityDefinition
        {
            Id = "0",
            Trigger = ParseRule("""{"$type":"TTriggerOnCardFired"}"""),
            Action = ParseRule("""
            {
              "$type": "TActionPlayerModifyAttribute",
              "attributeType": "Gold",
              "operation": "Add",
              "value": { "$type": "TReferenceValueCardAttribute", "attributeType": "Custom_0" },
              "target": { "$type": "TTargetPlayerRelative", "targetMode": "Self" }
            }
            """)
        };
        badCardRef.Tiers!["Bronze"].AbilityIds.Add("0");
        var repA = ModValidator.RunTenGateValidation(WrapSingleCard("neg-g2-a", badCardRef), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.False(repA.Passed);
        Assert.Contains(repA.Gates[3].Issues, i => i.Severity == ModValidationSeverity.Error && i.Code == "REFERENCE_VALUE_MISSING_TARGET");

        // 负例 B：Aura 中 TReferenceValuePlayerAttribute 缺失 target（旧版特许状/宝库舰 Bug，玩家引用一律必选 target）
        var badPlayerRef = CreateHealthyBaseCard("neg-g2-b", "bad_player_ref");
        badPlayerRef.Auras["0"] = new AuraDefinition
        {
            Id = "0",
            ActiveIn = "HandOnly",
            Action = ParseRule("""
            {
              "$type": "TAuraActionCardModifyAttribute",
              "attributeType": "DamageAmount",
              "operation": "Add",
              "value": { "$type": "TReferenceValuePlayerAttribute", "attributeType": "Gold" },
              "target": { "$type": "TTargetCardSelf" }
            }
            """)
        };
        badPlayerRef.Tiers!["Bronze"].AuraIds.Add("0");
        var repB = ModValidator.RunTenGateValidation(WrapSingleCard("neg-g2-b", badPlayerRef), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.False(repB.Passed);
        Assert.Contains(repB.Gates[3].Issues, i => i.Severity == ModValidationSeverity.Error && i.Code == "REFERENCE_VALUE_MISSING_TARGET");

        // 负例 C：非 Aura 中 TReferenceValueCardTagCount 缺失 target（旧版 studio-v14.js 生成 Bug）
        var badTagCount = CreateHealthyBaseCard("neg-g2-c", "bad_tag_count");
        badTagCount.Abilities["0"] = new AbilityDefinition
        {
            Id = "0",
            Trigger = ParseRule("""{"$type":"TTriggerOnCardFired"}"""),
            Action = ParseRule("""
            {
              "$type": "TActionPlayerDamage",
              "value": { "$type": "TReferenceValueCardTagCount", "tags": ["Weapon"], "targetSection": "SelfHand" },
              "target": { "$type": "TTargetPlayerRelative", "targetMode": "Opponent" }
            }
            """)
        };
        badTagCount.Tiers!["Bronze"].AbilityIds.Add("0");
        var repC = ModValidator.RunTenGateValidation(WrapSingleCard("neg-g2-c", badTagCount), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.False(repC.Passed);
        Assert.Contains(repC.Gates[3].Issues, i => i.Severity == ModValidationSeverity.Error && i.Code == "REFERENCE_VALUE_MISSING_TARGET");

        // 正例例外保护：Aura 中 TReferenceValueCardAttribute 省略 target（合法逐目标 CurrentTargetCard 语义）与 TReferenceValueAttributeChange 必须放行！
        var validAuraNullTarget = CreateHealthyBaseCard("pos-g2", "valid_aura_null_target");
        validAuraNullTarget.Auras["0"] = new AuraDefinition
        {
            Id = "0",
            ActiveIn = "HandOnly",
            Action = ParseRule("""
            {
              "$type": "TAuraActionCardModifyAttribute",
              "attributeType": "BurnApplyAmount",
              "operation": "Add",
              "value": {
                "$type": "TReferenceValueCardAttribute",
                "attributeType": "DamageAmount",
                "modifier": { "modifyMode": "Multiply", "value": { "$type": "TFixedValue", "value": 0.5 } }
              },
              "target": { "$type": "TTargetCardSection", "targetSection": "SelfHand" }
            }
            """)
        };
        validAuraNullTarget.Abilities["0"] = new AbilityDefinition
        {
            Id = "0",
            Trigger = ParseRule("""{"$type":"TTriggerOnPlayerAttributeChanged","attributeType":"Gold","changeType":"Gain"}"""),
            Action = ParseRule("""
            {
              "$type": "TActionPlayerShieldApply",
              "value": { "$type": "TReferenceValueAttributeChange", "defaultValue": 0 },
              "target": { "$type": "TTargetPlayerRelative", "targetMode": "Self" }
            }
            """)
        };
        validAuraNullTarget.Tiers!["Bronze"].AuraIds.Add("0");
        validAuraNullTarget.Tiers["Bronze"].AbilityIds.Add("0");

        var repGood = ModValidator.RunTenGateValidation(WrapSingleCard("pos-g2", validAuraNullTarget), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.True(repGood.Passed, repGood.SummaryZh);
        Assert.DoesNotContain(repGood.Gates[3].Issues, i => i.Code == "REFERENCE_VALUE_MISSING_TARGET");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 5. 闸门 3 负例与正例：战中触发修改属性缺失 duration 意外永久化（保护合法永久成长）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Gate3_CombatModifyAttributeMissingDuration_InterceptsSnowballBug_WithoutFalsePositivesOnPermanentGrowth()
    {
        var whitelist = Fx.CreateValidCardIdsCopy();

        // 负例 A：文案声明“本场战斗”但战中触发 TActionCardModifyAttribute 漏写 duration -> Error
        var snowballCard = CreateHealthyBaseCard("neg-g3-a", "snowball_blade");
        snowballCard.PassiveTooltipsZh = ["每当相邻物品使用时，此物品在本场战斗中获得 +{attr.Custom_0} 伤害"];
        snowballCard.Abilities["0"] = new AbilityDefinition
        {
            Id = "0",
            Trigger = ParseRule("""{"$type":"TTriggerOnItemUsed","subject":{"$type":"TTargetCardPositional","origin":"Self","targetMode":"Neighbor"}}"""),
            Action = ParseRule("""
            {
              "$type": "TActionCardModifyAttribute",
              "attributeType": "DamageAmount",
              "operation": "Add",
              "value": { "$type": "TReferenceValueCardAttribute", "attributeType": "Custom_0", "target": { "$type": "TTargetCardSelf" } },
              "target": { "$type": "TTargetCardSelf" }
            }
            """)
        };
        snowballCard.Tiers!["Bronze"].AbilityIds.Add("0");
        var repA = ModValidator.RunTenGateValidation(WrapSingleCard("neg-g3-a", snowballCard), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.False(repA.Passed);
        Assert.Contains(repA.Gates[3].Issues, i =>
            i.Severity == ModValidationSeverity.Error
            && i.Code == "COMBAT_MODIFY_ATTR_MISSING_DURATION"
            && (i.FixHintZh?.Contains("UntilEndOfCombat", StringComparison.Ordinal) == true));

        // 负例 B：开火修改自身伤害未写 duration 且未显式说明永久意图 -> 触发高危告警 COMBAT_MODIFY_ATTR_MISSING_DURATION
        var ambiguousCard = CreateHealthyBaseCard("neg-g3-b", "ambiguous_buff");
        ambiguousCard.PassiveTooltipsZh = ["开火时提升 {attr.Custom_0} 点攻击力"];
        ambiguousCard.Abilities["0"] = new AbilityDefinition
        {
            Id = "0",
            Trigger = ParseRule("""{"$type":"TTriggerOnCardFired"}"""),
            Action = ParseRule("""
            {
              "$type": "TActionCardModifyAttribute",
              "attributeType": "DamageAmount",
              "operation": "Add",
              "value": { "$type": "TReferenceValueCardAttribute", "attributeType": "Custom_0", "target": { "$type": "TTargetCardSelf" } },
              "target": { "$type": "TTargetCardSelf" }
            }
            """)
        };
        ambiguousCard.Tiers!["Bronze"].AbilityIds.Add("0");
        var repB = ModValidator.RunTenGateValidation(WrapSingleCard("neg-g3-b", ambiguousCard), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.Contains(repB.Gates[3].Issues, i =>
            i.Code == "COMBAT_MODIFY_ATTR_MISSING_DURATION"
            && (i.Severity == ModValidationSeverity.Warning || i.Severity == ModValidationSeverity.Error));

        // 正例 1：补齐 TDeterminantDuration(UntilEndOfCombat) -> 零告警
        var fixedTempCard = CreateHealthyBaseCard("pos-g3-temp", "fixed_temp_blade");
        fixedTempCard.PassiveTooltipsZh = ["每当相邻物品使用时，此物品在本场战斗中获得 +{attr.Custom_0} 伤害"];
        fixedTempCard.Abilities["0"] = new AbilityDefinition
        {
            Id = "0",
            Trigger = ParseRule("""{"$type":"TTriggerOnItemUsed","subject":{"$type":"TTargetCardPositional","origin":"Self","targetMode":"Neighbor"}}"""),
            Action = ParseRule("""
            {
              "$type": "TActionCardModifyAttribute",
              "attributeType": "DamageAmount",
              "operation": "Add",
              "duration": { "$type": "TDeterminantDuration", "durationType": "UntilEndOfCombat" },
              "value": { "$type": "TReferenceValueCardAttribute", "attributeType": "Custom_0", "target": { "$type": "TTargetCardSelf" } },
              "target": { "$type": "TTargetCardSelf" }
            }
            """)
        };
        fixedTempCard.Tiers!["Bronze"].AbilityIds.Add("0");
        var repTemp = ModValidator.RunTenGateValidation(WrapSingleCard("pos-g3-temp", fixedTempCard), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.True(repTemp.Passed, repTemp.SummaryZh);
        Assert.DoesNotContain(repTemp.Gates[3].Issues, i => i.Code == "COMBAT_MODIFY_ATTR_MISSING_DURATION");

        // 正例 2：显式永久成长卡 + 战中产金币 + 战中加 SellPrice 均不得误杀！
        var legitPermanentCard = CreateHealthyBaseCard("pos-g3-perm", "legit_perm_growth");
        legitPermanentCard.PassiveTooltipsZh = ["每次开火永久获得 +{attr.Custom_0} 伤害，并获得 2 金币且售价 +1"];
        legitPermanentCard.Abilities["0"] = new AbilityDefinition
        {
            Id = "0",
            Trigger = ParseRule("""{"$type":"TTriggerOnCardFired"}"""),
            Action = ParseRule("""
            {
              "$type": "TActionAnd",
              "actions": [
                {
                  "$type": "TActionCardModifyAttribute",
                  "attributeType": "DamageAmount",
                  "operation": "Add",
                  "value": { "$type": "TReferenceValueCardAttribute", "attributeType": "Custom_0", "target": { "$type": "TTargetCardSelf" } },
                  "target": { "$type": "TTargetCardSelf" }
                },
                {
                  "$type": "TActionPlayerModifyAttribute",
                  "attributeType": "Gold",
                  "operation": "Add",
                  "value": { "$type": "TFixedValue", "value": 2 },
                  "target": { "$type": "TTargetPlayerRelative", "targetMode": "Self" }
                },
                {
                  "$type": "TActionCardModifyAttribute",
                  "attributeType": "SellPrice",
                  "operation": "Add",
                  "value": { "$type": "TFixedValue", "value": 1 },
                  "target": { "$type": "TTargetCardSelf" }
                }
              ]
            }
            """)
        };
        legitPermanentCard.Tiers!["Bronze"].AbilityIds.Add("0");
        var repPerm = ModValidator.RunTenGateValidation(WrapSingleCard("pos-g3-perm", legitPermanentCard), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.True(repPerm.Passed, repPerm.SummaryZh);
        Assert.DoesNotContain(repPerm.Gates[3].Issues, i => i.Code == "COMBAT_MODIFY_ATTR_MISSING_DURATION");

        // 正例 3：局外触发器（TTriggerOnDayStarted / TTriggerOnFightEnded）修改属性无 duration 不得误报
        var outOfCombatCard = CreateHealthyBaseCard("pos-g3-ooc", "day_growth");
        outOfCombatCard.PassiveTooltipsZh = ["每天开始时伤害 +{attr.Custom_0}"];
        outOfCombatCard.Abilities["0"] = new AbilityDefinition
        {
            Id = "0",
            Trigger = ParseRule("""{"$type":"TTriggerOnDayStarted"}"""),
            Action = ParseRule("""
            {
              "$type": "TActionCardModifyAttribute",
              "attributeType": "DamageAmount",
              "operation": "Add",
              "value": { "$type": "TReferenceValueCardAttribute", "attributeType": "Custom_0", "target": { "$type": "TTargetCardSelf" } },
              "target": { "$type": "TTargetCardSelf" }
            }
            """)
        };
        outOfCombatCard.Tiers!["Bronze"].AbilityIds.Add("0");
        var repOoc = ModValidator.RunTenGateValidation(WrapSingleCard("pos-g3-ooc", outOfCombatCard), null, Fx.Repo, whitelist, runDynamicGates: false);
        Assert.True(repOoc.Passed, repOoc.SummaryZh);
        Assert.DoesNotContain(repOoc.Gates[3].Issues, i => i.Code == "COMBAT_MODIFY_ATTR_MISSING_DURATION");

        // 正例 4：校验官方原版永久成长卡（如 Crystal Bonsai、Candy Mail、Super Syrup）按原版结构接入 ModValidator 绝不被闸门 1/2/3 误杀为 Error
        var vanillaPermanentNames = new[] { "Crystal Bonsai", "Candy Mail", "Super Syrup" };
        int verifiedVanillaCount = 0;
        foreach (string vName in vanillaPermanentNames)
        {
            var vTpl = Fx.Repo.CardsById.Values.FirstOrDefault(c =>
                string.Equals(c.InternalName, vName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Type, "Item", StringComparison.Ordinal));
            if (vTpl == null) continue;
            verifiedVanillaCount++;

            var cloned = CreateHealthyBaseCard("pos-g3-van", vName.ToLowerInvariant().Replace(' ', '_'));
            cloned.BaseVisualTemplateId = vTpl.Id;
            cloned.Size = vTpl.Size;
            cloned.DescZh = vTpl.InternalDescription ?? string.Empty;
            cloned.PassiveTooltipsZh = vTpl.Localization?.Tooltips?.Select(t => t.Content?.Text ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];
            cloned.Abilities = vTpl.Abilities ?? new Dictionary<string, AbilityDefinition>();
            cloned.Auras = vTpl.Auras ?? new Dictionary<string, AuraDefinition>();
            cloned.Tiers = vTpl.Tiers ?? cloned.Tiers;

            var repVan = ModValidator.RunTenGateValidation(WrapSingleCard("pos-g3-van", cloned), null, Fx.Repo, whitelist, runDynamicGates: false);
            var gate4Errors = repVan.Gates[3].Issues
                .Where(i => i.Severity == ModValidationSeverity.Error && Wave4BGateCodes.Contains(i.Code, StringComparer.Ordinal))
                .ToList();
            Assert.True(gate4Errors.Count == 0,
                $"官方永久成长卡 '{vName}' 被新闸门误判为 Error：{string.Join(" | ", gate4Errors.Select(e => $"{e.Code}:{e.Message}"))}");
        }
        Assert.True(verifiedVanillaCount >= 2, $"应至少验证 2 张官方原版永久成长卡，实际={verifiedVanillaCount}");
    }
}
