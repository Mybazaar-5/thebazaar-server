using System.Collections.Frozen;
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
/// Wave 2A 机制沙盒安全与战中动态闭环强断言 TDD 套件（无全局静态状态依赖，支持并行执行）。
/// 覆盖 P0-31、P0-32、P0-33、D3-P1-03、D3-P1-04、D3-P1-05、D3-P1-06、D3-P1-08、D6-P1-13、D6-P1-18。
/// </summary>
public sealed class Wave2A_MechanicSandboxSafetyTests
{
    private static CardInstance MakeCard(
        string id,
        string type = "Item",
        string size = "Small",
        Dictionary<string, double>? baseAttrs = null,
        List<CardMechanicAttachment>? mechanics = null)
    {
        var attrs = baseAttrs != null
            ? new Dictionary<string, double>(baseAttrs, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var tpl = new CardTemplate
        {
            Id = id,
            Type = type,
            Size = size,
            StartingTier = "Bronze",
            InternalName = id,
            Attributes = new Dictionary<string, double>(attrs, StringComparer.OrdinalIgnoreCase),
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double>(attrs, StringComparer.OrdinalIgnoreCase) }
            },
            CustomMechanics = mechanics
        };
        return new CardInstance(tpl, CardTier.Bronze, $"inst_{id}_{Guid.NewGuid():N}");
    }

    private static CombatMechanicRuntime CreateIsolatedRuntime(
        PlayerState p1,
        PlayerState p2,
        IEnumerable<MechanicDefinition>? defs = null,
        Action? onRecalcAuras = null)
    {
        var frozen = (defs ?? Array.Empty<MechanicDefinition>())
            .ToFrozenDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        return new CombatMechanicRuntime(
            p1,
            p2,
            new Random(42),
            new CombatMechanicHostCallbacks
            {
                GetTimeMs = () => 1000,
                DealDamage = (target, _, dmg, _, _) => target.CurrentHealth -= dmg,
                ApplyStatus = (_, _, _, _, _, _) => { },
                ChangeCardState = (_, _, _, _, _, _) => { },
                ForceUseCard = (_, _) => { },
                RecalculateAuras = onRecalcAuras ?? (() => { }),
                IsValidVfxKey = _ => true
            },
            frozen,
            Array.Empty<ICombatPipelineInterceptor>());
    }

    [Fact]
    public void D6P118_PlayerState_DefaultInitialHealthMax_Is300()
    {
        var p = new PlayerState("p1", "Tester", "Vanessa");
        Assert.Equal(300, p.HealthMax);
        Assert.Equal(300, p.CurrentHealth);
    }

    [Fact]
    public void D6P113_CustomRunRuleSet_IsCustomPowerModified_DetectsArchetypeOverrides()
    {
        var standard = new CustomRunRuleSet();
        Assert.False(standard.IsCustomPowerModified());

        var weightMod = new CustomRunRuleSet { ArchetypeWeightMultiplier = 3.5 };
        Assert.True(weightMod.IsCustomPowerModified());

        var capMod = new CustomRunRuleSet { ArchetypeMultiMatchCap = 5.0 };
        Assert.True(capMod.IsCustomPowerModified());
    }

    [Fact]
    public void P031_ModifyPlayerAttribute_NormalizesRegen_UsesCombatLayer_ClampsHp_AndRecalculatesAuras()
    {
        var p1 = new PlayerState("p1", "Hero", "Vanessa", initialHealthMax: 300);
        var p2 = new PlayerState("p2", "Foe", "Vanessa", initialHealthMax: 300);
        int auraRecalcCount = 0;
        var rt = CreateIsolatedRuntime(p1, p2, onRecalcAuras: () => auraRecalcCount++);

        // 1. Regen 必须归一化至 HealthRegen 且落入 Combat 层（战后 ClearCombatModifiers 应完全归零）
        rt.ModifyPlayerAttribute(p1, "Regen", "Add", 12);
        Assert.Equal(12, p1.GetAttribute("HealthRegen"));
        Assert.Equal(0, p1.Attributes.GetValueOrDefault("Regen", 0));
        Assert.Equal(0, p1.Attributes.GetValueOrDefault("HealthRegen", 0));
        Assert.True(auraRecalcCount >= 1);

        // 2. 降低 HealthMax 时同步钳制 CurrentHealth 不超过新的 HealthMax
        p1.CurrentHealth = 300;
        rt.ModifyPlayerAttribute(p1, "HealthMax", "Add", -100);
        Assert.Equal(200, p1.HealthMax);
        Assert.True(p1.CurrentHealth <= p1.HealthMax);

        // 3. 战后重置战斗层修改后恢复原始 HealthMax 与 0 再生
        p1.PrepareForCombat();
        Assert.Equal(0, p1.GetAttribute("HealthRegen"));
        Assert.Equal(300, p1.HealthMax);

        // 4. ValidateStatic 必须拦截非法玩家属性名
        var badDef = new MechanicDefinition
        {
            Id = "test:bad_player_attr",
            NameZh = "非法玩家属性测试",
            Hooks =
            {
                new MechanicHookDefinition
                {
                    Hook = "OnFightStart",
                    Scope = "OwnerPlayer",
                    Steps =
                    {
                        new MechanicStep
                        {
                            Op = "ModifyPlayerAttribute",
                            Attribute = "ArbitraryHackerField",
                            Operation = "Add",
                            Value = new MechanicValueExpr { Const = 999 }
                        }
                    }
                }
            }
        };
        var issues = new List<ModValidationIssue>();
        MechanicSandboxInterpreter.ValidateStatic(badDef, "test-mod", issues);
        Assert.Contains(issues, i => i.Severity == ModValidationSeverity.Error && i.Code == "MECH_INVALID_PLAYER_ATTR");
    }

    [Fact]
    public void P032_And_D3P105_PassiveCardCooldownProtected_AuraBorrowBlocked_AndP2PostCombatRejected()
    {
        var p1 = new PlayerState("p1", "Hero", "Vanessa", initialHealthMax: 300);
        var p2 = new PlayerState("p2", "Foe", "Vanessa", initialHealthMax: 300);

        // 0 CD 被动卡（无 CooldownMax、无 DamageAmount）
        var passive = MakeCard("passive_shell", baseAttrs: new() { ["SellPrice"] = 3 });
        p1.Hand.TryAddAuto(passive);

        // 敌方卡
        var enemyCard = MakeCard("enemy_blade", baseAttrs: new() { ["CooldownMax"] = 4000, ["DamageAmount"] = 20 });
        p2.Hand.TryAddAuto(enemyCard);

        var rt = CreateIsolatedRuntime(p1, p2);

        // 1. 0 CD 被动卡修改 CooldownMax 严禁被强拉成 1000ms
        rt.ModifyCardAttribute(passive, "CooldownMax", "Add", -500);
        Assert.Equal(0, passive.GetAttribute("CooldownMax"));

        // 2. 光环临时给非输出被动卡借了 10 点 DamageAmount 后，grantIfMissing=false 仍必须拒绝写入（防穿透）
        passive.AuraAdd["DamageAmount"] = 10;
        rt.ModifyCardAttribute(passive, "DamageAmount", "Add", 50, permanent: true, grantIfMissing: false);
        Assert.Equal(0, passive.PermanentAdd.GetValueOrDefault("DamageAmount", 0));

        // 3. QueuePostCombat* 严禁为敌方 P2 卡牌入队
        rt.QueuePostCombatModifyAttr(enemyCard, "DamageAmount", "Add", 99, "evil-mech");
        rt.QueuePostCombatRemoveCard(enemyCard, "evil", "evil-mech");
        Assert.Empty(rt.PostCombatOps);
    }

    [Fact]
    public void D3P103_And_D3P108_UnilateralScope_StopSentinel_AndDeepConditionFailClosed()
    {
        var p1 = new PlayerState("p1", "Hero", "Vanessa", initialHealthMax: 300);
        var p2 = new PlayerState("p2", "Foe", "Vanessa", initialHealthMax: 300);

        // 1. Stop 哨兵在 If 分支内部必须终止整个外层步骤流（后续步骤不得执行）
        var stopDef = new MechanicDefinition
        {
            Id = "test:stop_sentinel",
            NameZh = "Stop 哨兵",
            Hooks =
            {
                new MechanicHookDefinition
                {
                    Hook = "OnFightStart",
                    Scope = "SelfCard",
                    Steps =
                    {
                        new MechanicStep
                        {
                            Op = "If",
                            Condition = new MechanicCondition
                            {
                                Left = new MechanicValueExpr { Const = 1 },
                                Compare = "Eq",
                                Right = new MechanicValueExpr { Const = 1 }
                            },
                            Then = new List<MechanicStep>
                            {
                                new MechanicStep { Op = "ModifyCounter", Key = "step", Operation = "Add", Value = new MechanicValueExpr { Const = 1 } },
                                new MechanicStep { Op = "Stop" },
                                new MechanicStep { Op = "ModifyCounter", Key = "step", Operation = "Add", Value = new MechanicValueExpr { Const = 10 } }
                            }
                        },
                        new MechanicStep { Op = "ModifyCounter", Key = "step", Operation = "Add", Value = new MechanicValueExpr { Const = 100 } }
                    }
                }
            }
        };

        // 2. EvaluateCondition 超深嵌套（depth > MaxAstDepth）必须 Fail-Closed 返回 false
        MechanicCondition deepCond = new MechanicCondition
        {
            Left = new MechanicValueExpr { Const = 1 },
            Compare = "Eq",
            Right = new MechanicValueExpr { Const = 1 }
        };
        for (int i = 0; i < MechanicSandboxInterpreter.MaxAstDepth + 3; i++)
        {
            deepCond = new MechanicCondition { All = new List<MechanicCondition> { deepCond } };
        }
        var deepDef = new MechanicDefinition
        {
            Id = "test:deep_cond",
            NameZh = "超深条件安全失败",
            Hooks =
            {
                new MechanicHookDefinition
                {
                    Hook = "OnFightStart",
                    Scope = "SelfCard",
                    Steps =
                    {
                        new MechanicStep
                        {
                            Op = "If",
                            Condition = deepCond,
                            Then = new List<MechanicStep>
                            {
                                new MechanicStep { Op = "SetFlag", Flag = "SHOULD_NOT_TRIGGER" }
                            }
                        }
                    }
                }
            }
        };

        // 3. SelfCard 对 BeforeStatusApply(TargetCard=友卡) 不得误触发
        var selfCardDef = new MechanicDefinition
        {
            Id = "test:self_status_only",
            NameZh = "仅自身卡牌状态拦截",
            Hooks =
            {
                new MechanicHookDefinition
                {
                    Hook = "BeforeStatusApply",
                    Scope = "SelfCard",
                    Steps =
                    {
                        new MechanicStep { Op = "SetFlag", Flag = "HIT_BY_STATUS" }
                    }
                }
            }
        };

        var holder = MakeCard("holder", mechanics: new()
        {
            new CardMechanicAttachment { MechanicId = stopDef.Id },
            new CardMechanicAttachment { MechanicId = deepDef.Id },
            new CardMechanicAttachment { MechanicId = selfCardDef.Id }
        });
        var ally = MakeCard("ally_card");
        p1.Hand.TryAddAuto(holder);
        p1.Hand.TryAddAuto(ally);

        var rt = CreateIsolatedRuntime(p1, p2, new[] { stopDef, deepDef, selfCardDef });
        rt.DispatchPipeline(new CombatPipelineEvent
        {
            HookName = "OnFightStart",
            TimeMs = 0,
            SourcePlayer = p1,
            TargetPlayer = p2
        });

        Assert.Equal(1, rt.GetCardCounter(holder, "step"));
        Assert.False(rt.HasCardFlag(holder, "SHOULD_NOT_TRIGGER"));

        // 对友卡 ally 施加 Slow，不应当命中 holder 的 SelfCard 钩子
        rt.DispatchPipeline(new CombatPipelineEvent
        {
            HookName = "BeforeStatusApply",
            TimeMs = 500,
            SourcePlayer = p2,
            TargetPlayer = p1,
            TargetCard = ally,
            StatusType = "Slow",
            Amount = 1000
        });
        Assert.False(rt.HasCardFlag(holder, "HIT_BY_STATUS"));

        // 对 holder 本身施加 Slow，应当命中
        rt.DispatchPipeline(new CombatPipelineEvent
        {
            HookName = "BeforeStatusApply",
            TimeMs = 500,
            SourcePlayer = p2,
            TargetPlayer = p1,
            TargetCard = holder,
            StatusType = "Slow",
            Amount = 1000
        });
        Assert.True(rt.HasCardFlag(holder, "HIT_BY_STATUS"));
    }

    [Fact]
    public void P032_MechanicSafeAttributeWriter_Boundaries_ZeroMultPreserved_AndSanitizeHighTowerAndTempo()
    {
        var active = MakeCard("active_sword", baseAttrs: new()
        {
            ["CooldownMax"] = 4000,
            ["DamageAmount"] = 10,
            ["AmmoMax"] = 2,
            ["Multicast"] = 1,
            ["PercentCooldownReduction"] = 0
        });

        // Multiply 单步钳制至 [0.01, 10.0]，累计上限 1000.0
        var r1 = MechanicSafeAttributeWriter.ApplyCardAttribute(active, "DamageAmount", "Multiply", 50.0, DurationTarget.Combat);
        Assert.True(r1.Applied);
        Assert.Equal(100, active.GetAttribute("DamageAmount"), precision: 3);

        for (int i = 0; i < 6; i++)
            MechanicSafeAttributeWriter.ApplyCardAttribute(active, "DamageAmount", "Multiply", 10.0, DurationTarget.Combat);
        Assert.True(active.GetAttribute("DamageAmount") <= 10000.0 + 1e-6);

        // 主动卡 CooldownMax 钳制 [1000, 60000]
        MechanicSafeAttributeWriter.ApplyCardAttribute(active, "CooldownMax", "Set", 100, DurationTarget.Combat);
        Assert.Equal(1000, active.GetAttribute("CooldownMax"), precision: 3);
        MechanicSafeAttributeWriter.ApplyCardAttribute(active, "CooldownMax", "Set", 999999, DurationTarget.Combat);
        Assert.Equal(60000, active.GetAttribute("CooldownMax"), precision: 3);

        // PercentCooldownReduction ∈ [-200, 90]，Multicast ∈ [1, 10]，非负输出钳底 >= 0
        MechanicSafeAttributeWriter.ApplyCardAttribute(active, "PercentCooldownReduction", "Set", 99, DurationTarget.Combat);
        Assert.Equal(90, active.GetAttribute("PercentCooldownReduction"), precision: 3);
        MechanicSafeAttributeWriter.ApplyCardAttribute(active, "PercentCooldownReduction", "Set", -500, DurationTarget.Combat);
        Assert.Equal(-200, active.GetAttribute("PercentCooldownReduction"), precision: 3);
        MechanicSafeAttributeWriter.ApplyCardAttribute(active, "Multicast", "Set", 50, DurationTarget.Combat);
        Assert.Equal(10, active.GetAttribute("Multicast"), precision: 3);
        MechanicSafeAttributeWriter.ApplyCardAttribute(active, "Multicast", "Set", 0, DurationTarget.Combat);
        Assert.Equal(1, active.GetAttribute("Multicast"), precision: 3);
        MechanicSafeAttributeWriter.ApplyCardAttribute(active, "AmmoMax", "Add", -99, DurationTarget.Combat);
        Assert.Equal(0, active.GetAttribute("AmmoMax"), precision: 3);

        // 0 乘与 CombatZeroMultCount 在 ClampCardInvariants 中完整保留
        var zeroCard = MakeCard("zero_mult_card", baseAttrs: new() { ["DamageAmount"] = 25 });
        zeroCard.ModifyAttribute("DamageAmount", "Multiply", 0.0, DurationTarget.Combat);
        Assert.Equal(0, zeroCard.GetAttribute("DamageAmount"), precision: 6);
        MechanicSafeAttributeWriter.ClampCardInvariants(zeroCard);
        Assert.Equal(0, zeroCard.GetAttribute("DamageAmount"), precision: 6);

        // PlayerState.SanitizeAllAttributeLayers 清理 NaN/Infinity、保留无尽高塔 >1_000_000 高血与官方 -1e15 Tempo 压制及 0 乘
        var towerPlayer = new PlayerState("p1", "TowerHero", "Vanessa", initialHealthMax: 2_500_000);
        towerPlayer.CurrentHealth = 2_500_000;
        towerPlayer.Attributes["TempoGainCooldownMax"] = -1e15;
        towerPlayer.CombatAdd["Shield"] = double.NaN;
        towerPlayer.CombatAdd["Burn"] = double.PositiveInfinity;
        towerPlayer.ModifyAttribute("HealthRegen", "Multiply", 0.0, DurationTarget.Combat);
        towerPlayer.ModifyAttribute("HealthRegen", "Add", 50, DurationTarget.Combat);

        MechanicSafeAttributeWriter.ClampPlayerInvariants(towerPlayer);

        Assert.Equal(2_500_000.0, (double)towerPlayer.HealthMax, precision: 1);
        Assert.Equal(2_500_000.0, (double)towerPlayer.CurrentHealth, precision: 1);
        Assert.Equal(-1e15, towerPlayer.Attributes["TempoGainCooldownMax"]);
        Assert.False(double.IsNaN(towerPlayer.Shield));
        Assert.False(double.IsInfinity(towerPlayer.Burn));
        Assert.Equal(0.0, towerPlayer.GetAttribute("HealthRegen"), precision: 6);
    }

    [Fact]
    public void P033_MidCombatTransformAndRevert_PlusSocketAndStash_DynamicMechanicBinding()
    {
        var transformedMech = new MechanicDefinition
        {
            Id = "w2a:temp_transform_shield",
            NameZh = "临时变形护盾",
            Hooks =
            {
                new MechanicHookDefinition
                {
                    Hook = "OnCardUsed",
                    Scope = "SelfCard",
                    Steps =
                    {
                        new MechanicStep
                        {
                            Op = "ModifyPlayerAttribute",
                            Target = "OwnerPlayer",
                            Attribute = "Shield",
                            Operation = "Add",
                            Value = new MechanicValueExpr { Const = 7 }
                        }
                    }
                }
            }
        };

        var stashMech = new MechanicDefinition
        {
            Id = "w2a:stash_passive_regen",
            NameZh = "背包被动再生",
            Hooks =
            {
                new MechanicHookDefinition
                {
                    Hook = "OnFightStart",
                    Scope = "Global",
                    Steps =
                    {
                        new MechanicStep
                        {
                            Op = "ModifyPlayerAttribute",
                            Target = "OwnerPlayer",
                            Attribute = "HealthRegen",
                            Operation = "Add",
                            Value = new MechanicValueExpr { Const = 5 }
                        }
                    }
                }
            }
        };

        var p1 = new PlayerState("p1", "Hero", "Vanessa", initialHealthMax: 500);
        var p2 = new PlayerState("p2", "Dummy", "Vanessa", initialHealthMax: 500);

        var stashCard = MakeCard("stash_relic", mechanics: new() { new CardMechanicAttachment { MechanicId = stashMech.Id } });
        p1.Stash.TryAddAuto(stashCard);

        var plainCard = MakeCard("plain_dagger", baseAttrs: new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 5 });
        p1.Hand.TryAddAuto(plainCard);

        var rt = CreateIsolatedRuntime(p1, p2, new[] { transformedMech, stashMech });
        Assert.True(CombatMechanicRuntime.ShouldActivate(p1, p2, new Dictionary<string, MechanicDefinition>
        {
            [stashMech.Id] = stashMech
        }, hasCsharpInterceptors: false));

        rt.DispatchPipeline(new CombatPipelineEvent
        {
            HookName = "OnFightStart",
            TimeMs = 0,
            SourcePlayer = p1,
            TargetPlayer = p2
        });
        Assert.Equal(5, p1.GetAttribute("HealthRegen"));

        // 战中变形替换 -> 动态注册新卡机制并触发
        var tempBlade = MakeCard("temp_blade", baseAttrs: new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 5 },
            mechanics: new() { new CardMechanicAttachment { MechanicId = transformedMech.Id } });
        rt.ReplaceCard(p1, plainCard, tempBlade);
        Assert.True(CombatMechanicRuntime.CardNeedsMechanicRuntime(tempBlade, new Dictionary<string, MechanicDefinition>
        {
            [transformedMech.Id] = transformedMech
        }));

        rt.DispatchPipeline(new CombatPipelineEvent
        {
            HookName = "OnCardUsed",
            TimeMs = 1000,
            SourcePlayer = p1,
            TargetPlayer = p2,
            SourceCard = tempBlade
        });
        Assert.Equal(7, p1.Shield);

        // 定时变形到期还原 -> 注销临时卡不再产生僵尸触发
        rt.UnregisterCard(tempBlade);
        rt.RegisterCard(p1, plainCard);
        rt.DispatchPipeline(new CombatPipelineEvent
        {
            HookName = "OnCardUsed",
            TimeMs = 2000,
            SourcePlayer = p1,
            TargetPlayer = p2,
            SourceCard = tempBlade
        });
        Assert.Equal(7, p1.Shield);
    }

    [Fact]
    public void D3P104_AbilityExecutor_BeforeCardStateChangeTransform_RespectsCanReplaceCheckAndCanBlock()
    {
        var p1 = new PlayerState("p1", "Hero", "Vanessa", initialHealthMax: 300);
        var p2 = new PlayerState("p2", "Foe", "Vanessa", initialHealthMax: 300);

        var smallSrc = MakeCard("src_small", size: "Small", baseAttrs: new() { ["CooldownMax"] = 2000 });
        var largeTargetTpl = new CardTemplate
        {
            Id = "large_target",
            Type = "Item",
            Size = "Large",
            StartingTier = "Bronze",
            InternalName = "LargeTarget",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 3000 } }
            }
        };
        var smallTargetTpl = new CardTemplate
        {
            Id = "small_target",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            InternalName = "SmallTarget",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 3000 } }
            }
        };

        p1.Hand.TryAddAuto(smallSrc);
        for (int i = 0; i < 9; i++) p1.Hand.TryAddAuto(MakeCard($"filler_h_{i}", size: "Small"));
        for (int i = 0; i < 10; i++) p1.Stash.TryAddAuto(MakeCard($"filler_s_{i}", size: "Small"));

        var repo = SharedGameDataFixture.Shared.Repo.CreateWithOverlay(
            additionalCards: new[] { smallSrc.Template, largeTargetTpl, smallTargetTpl });

        int transformHookCalls = 0;
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, p2, new Random(7))
        {
            SelfCard = smallSrc,
            OnBeforeCardStateChange = (target, src, stateType, _) =>
            {
                if (string.Equals(stateType, "Transform", StringComparison.OrdinalIgnoreCase))
                {
                    transformHookCalls++;
                    return false;
                }
                return true;
            }
        };

        static RuleNode ParseRule(string json) => JsonSerializer.Deserialize<RuleNode>(json)!;

        // 1. 满盘无法替换成 Large 时（CanReplaceCard == false），绝不误触发 OnBeforeCardStateChange("Transform")
        var failAction = ParseRule($$"""
        {
          "$type": "TActionCardTransform",
          "Target": { "$type": "TTargetCardSelf" },
          "SpawnContext": {
            "$type": "TSpawnContextQuery",
            "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{largeTargetTpl.Id}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
          }
        }
        """);
        AbilityExecutor.ExecuteAction(failAction, smallSrc, ctx);
        Assert.Equal(0, transformHookCalls);
        Assert.Contains(smallSrc, p1.Hand.Cards);

        // 2. 同尺寸 Small 替换时（CanReplaceCard == true），精准触发并成功拦截
        var blockableAction = ParseRule($$"""
        {
          "$type": "TActionCardTransform",
          "Target": { "$type": "TTargetCardSelf" },
          "SpawnContext": {
            "$type": "TSpawnContextQuery",
            "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{smallTargetTpl.Id}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
          }
        }
        """);
        AbilityExecutor.ExecuteAction(blockableAction, smallSrc, ctx);
        Assert.Equal(1, transformHookCalls);
        Assert.Contains(smallSrc, p1.Hand.Cards);
    }

    [Fact]
    public void Redline_Heal5PercentCleanseFormula_RemainsExact()
    {
        static RuleNode ParseRule(string json) => JsonSerializer.Deserialize<RuleNode>(json)!;

        var p1 = new PlayerState("player", "Healer", "Vanessa", initialHealthMax: 400);
        var p2 = new PlayerState("opponent", "Foe", "Vanessa", initialHealthMax: 400);

        var healTpl = new CardTemplate
        {
            Id = "redline_healer",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            InternalName = "RedlineHealer",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    AbilityIds = new List<string> { "h0" },
                    Attributes = new Dictionary<string, double> { ["CooldownMax"] = 1000, ["HealAmount"] = 100 }
                }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["h0"] = new AbilityDefinition
                {
                    Id = "h0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseRule("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseRule("""{"$type":"TActionPlayerHeal","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            }
        };
        var debuffSkillTpl = new CardTemplate
        {
            Id = "start_debuff",
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            InternalName = "StartDebuff",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    AbilityIds = new List<string> { "b0", "p0" },
                    Attributes = new Dictionary<string, double> { ["BurnApplyAmount"] = 20, ["PoisonApplyAmount"] = 20 }
                }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["b0"] = new AbilityDefinition
                {
                    Id = "b0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseRule("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseRule("""{"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                },
                ["p0"] = new AbilityDefinition
                {
                    Id = "p0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseRule("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseRule("""{"$type":"TActionPlayerPoisonApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            }
        };

        p1.Hand.TryAddAuto(new CardInstance(healTpl, CardTier.Bronze, "heal_1"));
        p2.Skills.Add(new CardInstance(debuffSkillTpl, CardTier.Bronze, "debuff_1"));

        var repo = SharedGameDataFixture.Shared.Repo.CreateWithOverlay(
            additionalCards: new[] { healTpl, debuffSkillTpl });

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 123);
        var burnRemoved = res.Events.FirstOrDefault(e => e.Type == CombatEventType.BurnRemoved && e.TargetPlayerId == "player");
        var poisonRemoved = res.Events.FirstOrDefault(e => e.Type == CombatEventType.PoisonRemoved && e.TargetPlayerId == "player");
        Assert.NotNull(burnRemoved);
        Assert.NotNull(poisonRemoved);
        Assert.Equal(5, (int)Math.Round(burnRemoved!.Value));
        Assert.Equal(5, (int)Math.Round(poisonRemoved!.Value));
    }
}
