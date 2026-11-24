using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarServer.Mods;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

/// <summary>
/// V14 开放式沙盒机制平台与 Web 创意工坊全量自由拼装回归测试：
/// 1. 零引擎硬编码证明 A：纯外部 JSON 声明式机制实现「自爆亡语（多段间隔伤害 + 友军伤害/冷却百分比 Buff + 语义区别于普通敌方摧毁不触发 PerformedDestruction）」；
/// 2. 零引擎硬编码证明 B：纯外部 JSON 声明式机制实现「格挡 N 次伤害后碎裂且不可被原版 TActionCardRepair 复原 + 登记战后局外永久移除」；
/// 3. 安全铁闸验证 A：恶意无限自触发循环与超大 Repeat 被步数/深度硬预算秒级斩断，且 Gate #9 对抗压测硬阻断带病 Mod；
/// 4. 安全铁闸验证 B：抛异常的 C# 管线拦截器仅熔断自身不影响正常卡牌结算，且极端负冷却/超限缩减被强钳制在安全区间；
/// 5. 全链路验证：ModLoader 扫描 mechanics/*.json → Gate #4 静态校验 → ModRegistry 热重载与 RollbackToBase 零残留；
/// 6. 工坊 API & 全量词缀自由拼装验证：TTriggerOr + TActionAnd 多动作链真战斗执行，及 /api/workshop/mechanics 与 /api/workshop/mechanics/stress-test 端到端通过。
/// </summary>
[Xunit.Collection("GlobalStaticState")]
public sealed class V14_OpenMechanicSandboxTests
{
    private const string SmallItemCarrierId = "03b1fd16-6b0d-4847-a76f-6c586f7c5958";

    private static readonly Lazy<SharedData> Shared = new(LoadShared);

    private sealed record SharedData(string RepoRoot, string DataDir, GameDataRepository Repo, HashSet<string> Whitelist);

    private static SharedData LoadShared()
    {
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        return new SharedData(shared.RepoRoot, shared.DataDir, shared.Repo, shared.CreateValidCardIdsCopy());
    }

    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    private static CardTemplate MakeSimpleWeapon(string id, string title, int cdMs, int dmg, List<CardMechanicAttachment>? mechs = null)
    {
        return new CardTemplate
        {
            TypeDiscriminator = "TCardItem",
            Id = id,
            Type = "Item",
            InternalName = title,
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = [HeroNames.Common],
            Tags = ["Weapon"],
            HiddenTags = ["Damage"],
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = cdMs, ["DamageAmount"] = dmg, ["BuyPrice"] = 4, ["SellPrice"] = 2 },
                    AbilityIds = ["0"]
                }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }
                    }
                }
            },
            CustomMechanics = mechs
        };
    }

    [Fact]
    public void V14_01_SelfDestructDeathrattle_PureExternalJson_MultiHit_AllyBuff_DistinctFromNormalDestroy()
    {
        var data = Shared.Value;

        // 纯外部 JSON 定义的“自爆亡语”机制（引擎代码零硬编码）：
        // 开火时以 reason="self_destruct" + suppressNormalDestroyTriggers=true 禁用自身并广播自爆事件，
        // 触发 3 段间隔 100ms 伤害（附官方白名单特效）+ 其余友军 +30 伤害 + 20% 冷却缩减。
        var selfDestructMech = new MechanicDefinition
        {
            Id = "demo:volatile_core",
            NameZh = "不稳核心自爆",
            DescZh = "开火时自爆（不触发普通摧毁联动），造成多段伤害并强化友军攻击与冷却",
            Parameters =
            [
                new MechanicParameterDef { Key = "hits", LabelZh = "自爆段数", Type = "int", DefaultValue = J(3), Min = 1, Max = 10 },
                new MechanicParameterDef { Key = "hitDmg", LabelZh = "每段伤害", Type = "int", DefaultValue = J(25), Min = 1, Max = 500 },
                new MechanicParameterDef { Key = "allyDmg", LabelZh = "友军加攻", Type = "int", DefaultValue = J(30), Min = 0, Max = 500 },
                new MechanicParameterDef { Key = "allyCdr", LabelZh = "友军减冷却%", Type = "int", DefaultValue = J(20), Min = 0, Max = 60 }
            ],
            Hooks =
            [
                new MechanicHookDefinition
                {
                    Id = "detonate",
                    Hook = "OnCardFired",
                    Scope = "SelfCard",
                    Filter = new MechanicHookFilter { OnlyWhenSelfSource = true },
                    MaxTriggersPerCombat = 1,
                    Steps =
                    [
                        new MechanicStep { Op = "SetFlag", Target = "Self", Flag = "self_destructed" },
                        new MechanicStep
                        {
                            Op = "ChangeCardState",
                            TargetSpec = new MechanicTargetSpec { Mode = "Self" },
                            ChangeKind = "Disable",
                            Reason = "self_destruct",
                            SuppressNormalDestroyTriggers = true,
                            VfxKey = "Explosion"
                        },
                        new MechanicStep { Op = "EmitCustomEvent", EventName = "volatile_detonated", PayloadValue = new MechanicValueExpr { Param = "hitDmg" } }
                    ]
                },
                new MechanicHookDefinition
                {
                    Id = "aftermath",
                    Hook = "OnCustomEvent",
                    Scope = "SelfCard",
                    Filter = new MechanicHookFilter { CustomEventName = "volatile_detonated", IncludeDisabledSelf = true },
                    Steps =
                    [
                        new MechanicStep
                        {
                            Op = "Repeat",
                            Count = new MechanicValueExpr { Param = "hits" },
                            IntervalMs = new MechanicValueExpr { Const = 100 },
                            Steps =
                            [
                                new MechanicStep
                                {
                                    Op = "DealDamage",
                                    PlayerTarget = "Opponent",
                                    Amount = new MechanicValueExpr { Param = "hitDmg" },
                                    VfxKey = "Fireball"
                                }
                            ]
                        },
                        new MechanicStep
                        {
                            Op = "ModifyCardAttribute",
                            TargetSpec = new MechanicTargetSpec { Mode = "OtherAllies" },
                            Attribute = "DamageAmount",
                            Operation = "Add",
                            Value = new MechanicValueExpr { Param = "allyDmg" },
                            Duration = "Combat"
                        },
                        new MechanicStep
                        {
                            Op = "ModifyCardAttribute",
                            TargetSpec = new MechanicTargetSpec { Mode = "OtherAllies" },
                            Attribute = "PercentCooldownReduction",
                            Operation = "Add",
                            Value = new MechanicValueExpr { Param = "allyCdr" },
                            Duration = "Combat"
                        }
                    ]
                }
            ]
        };

        var bombTpl = MakeSimpleWeapon("test:bomb", "自爆核心", cdMs: 1500, dmg: 0,
            mechs: [new CardMechanicAttachment { MechanicId = "demo:volatile_core", Params = new() { ["hits"] = J(3), ["hitDmg"] = J(25), ["allyDmg"] = J(30), ["allyCdr"] = J(20) } }]);
        bombTpl.Abilities.Clear(); // 纯靠自定义机制触发，无默认伤害干扰断言
        bombTpl.Tiers!["Bronze"].AbilityIds.Clear();

        var allySwordTpl = MakeSimpleWeapon("test:ally_sword", "友军长剑", cdMs: 4000, dmg: 20);

        // 敌方放一张监听“普通摧毁事件 TTriggerOnCardPerformedDestruction”的探针卡，验证自爆未误触发普通摧毁联动
        var destroyWatcherTpl = new CardTemplate
        {
            TypeDiscriminator = "TCardItem",
            Id = "test:destroy_watcher",
            Type = "Item",
            InternalName = "DestroyWatcher",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = [HeroNames.Common],
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["ShieldApplyAmount"] = 999 }, AbilityIds = ["0"] }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = new RuleNode { Type = "TTriggerOnCardPerformedDestruction" },
                    Action = new RuleNode { Type = "TActionPlayerShieldApply", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" } }
                }
            }
        };

        var overlayRepo = data.Repo.CreateWithOverlay([bombTpl, allySwordTpl, destroyWatcherTpl], [], null);
        var p1 = new PlayerState("p1", "测试玩家", HeroNames.Vanessa, 500);
        var p2 = new PlayerState("p2", "靶子", HeroNames.Dooley, 500);

        var bombInst = new CardInstance(bombTpl, CardTier.Bronze, null, "bomb_1");
        var swordInst = new CardInstance(allySwordTpl, CardTier.Bronze, null, "sword_1");
        var watcherInst = new CardInstance(destroyWatcherTpl, CardTier.Bronze, null, "watcher_1");
        Assert.True(p1.Hand.TryPlaceOrShift(bombInst, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(swordInst, 1));
        Assert.True(p2.Hand.TryPlaceOrShift(watcherInst, 0));

        var mechMap = new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase) { [selfDestructMech.Id] = selfDestructMech };
        var result = CombatSimulator.Simulate(
            overlayRepo, p1, p2,
            seed: 42, combatType: "PVP", recordTimeline: false, recordEvents: true, syncPermanentChanges: false,
            modifiers: null, sandstormEnabled: true, timelineSampleIntervalMs: 50,
            mechanicDefinitionsOverride: mechMap);

        Assert.Equal(0, result.MechanicCircuitBreakerTrips);
        Assert.Equal(0, result.MechanicBudgetViolations);

        // 1. 自爆卡 1500ms 禁用，发出 3 段 25 点带 Fireball VFX 的伤害（1500 / 1600 / 1700ms）
        var bombHits = result.Events
            .Where(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == "bomb_1")
            .ToList();
        Assert.Equal(3, bombHits.Count);
        Assert.All(bombHits, h =>
        {
            Assert.Equal(25, h.Value);
            Assert.Equal("Fireball", h.VfxKeyOverride);
        });
        Assert.True(bombHits[1].TimeMs >= bombHits[0].TimeMs + 100);
        Assert.True(bombHits[2].TimeMs >= bombHits[1].TimeMs + 100);

        // 2. 敌方“普通摧毁监听器”0 触发（护盾=0），证实自爆与普通摧毁语义解耦
        Assert.Equal(0, result.Player2TotalShieldGained);

        // 3. 友军长剑伤害由 20 提升至 50（+30），且因 20% 冷却缩减在 4000ms 前提前开火！
        var firstSwordHit = result.Events.FirstOrDefault(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == "sword_1");
        Assert.NotNull(firstSwordHit);
        Assert.Equal(50, firstSwordHit!.Value);
        Assert.True(firstSwordHit.TimeMs < 4000, $"友军长剑应因 +20% 冷却缩减提前于 4000ms 开火，实际={firstSwordHit.TimeMs}ms");
    }

    [Fact]
    public void V14_02_BlockNDamageThenIrreversibleShatter_CancelsVanillaRepair_AndQueuesPostCombatRemove()
    {
        var data = Shared.Value;

        // 纯外部 JSON 定义的“格挡 N 次伤害后永久碎裂且不可被 Repair 复原”机制（零引擎硬编码）
        var aegisMech = new MechanicDefinition
        {
            Id = "demo:fragile_aegis",
            NameZh = "脆弱神盾（格挡碎裂不可复原）",
            Parameters =
            [
                new MechanicParameterDef { Key = "blocks", LabelZh = "格挡次数", Type = "int", DefaultValue = J(2), Min = 1, Max = 10 },
                new MechanicParameterDef { Key = "permanentBreak", LabelZh = "战后永久碎裂移除", Type = "bool", DefaultValue = J(true) }
            ],
            Hooks =
            [
                new MechanicHookDefinition
                {
                    Id = "init_blocks",
                    Hook = "OnFightStart",
                    Scope = "SelfCard",
                    Steps =
                    [
                        new MechanicStep { Op = "ModifyCounter", Target = "Self", Key = "blocks_left", Operation = "Set", Value = new MechanicValueExpr { Param = "blocks" }, SyncAttribute = "Custom_0" }
                    ]
                },
                new MechanicHookDefinition
                {
                    Id = "block_incoming",
                    Hook = "BeforeDamage",
                    Scope = "OwnerPlayer",
                    Filter = new MechanicHookFilter { OnlyWhenOwnerTarget = true, DamageTypes = ["Direct"] },
                    Steps =
                    [
                        new MechanicStep
                        {
                            Op = "If",
                            Condition = new MechanicCondition { Left = new MechanicValueExpr { Counter = "blocks_left", Source = "Self" }, Compare = "Gt", Right = new MechanicValueExpr { Const = 0 } },
                            Then =
                            [
                                new MechanicStep { Op = "CancelHook" },
                                new MechanicStep { Op = "ModifyCounter", Target = "Self", Key = "blocks_left", Operation = "Subtract", Value = new MechanicValueExpr { Const = 1 }, SyncAttribute = "Custom_0" },
                                new MechanicStep
                                {
                                    Op = "If",
                                    Condition = new MechanicCondition { Left = new MechanicValueExpr { Counter = "blocks_left", Source = "Self" }, Compare = "Lte", Right = new MechanicValueExpr { Const = 0 } },
                                    Then =
                                    [
                                        new MechanicStep { Op = "SetFlag", Target = "Self", Flag = "unrepairable_shatter" },
                                        new MechanicStep { Op = "ChangeCardState", TargetSpec = new MechanicTargetSpec { Mode = "Self" }, ChangeKind = "Disable", Reason = "aegis_shattered" }
                                    ]
                                }
                            ]
                        }
                    ]
                },
                new MechanicHookDefinition
                {
                    Id = "forbid_repair",
                    Hook = "BeforeCardStateChange",
                    Scope = "SelfCard",
                    Filter = new MechanicHookFilter { ChangeKinds = ["Repair"], OnlyWhenSelfTarget = true, IncludeDisabledSelf = true },
                    Steps =
                    [
                        new MechanicStep
                        {
                            Op = "If",
                            Condition = new MechanicCondition { HasFlag = "unrepairable_shatter", FlagTarget = "Self" },
                            Then = [new MechanicStep { Op = "CancelHook" }]
                        }
                    ]
                },
                new MechanicHookDefinition
                {
                    Id = "post_remove",
                    Hook = "PostCombatCommit",
                    Scope = "SelfCard",
                    Filter = new MechanicHookFilter { RequireCardFlags = ["unrepairable_shatter"], IncludeDisabledSelf = true },
                    Steps =
                    [
                        new MechanicStep
                        {
                            Op = "If",
                            Condition = new MechanicCondition { ParamBool = "permanentBreak" },
                            Then = [new MechanicStep { Op = "QueuePostCombatRemoveCard", TargetSpec = new MechanicTargetSpec { Mode = "Self", IncludeDisabled = true }, Reason = "aegis_shattered" }]
                        }
                    ]
                }
            ]
        };

        var aegisTpl = new CardTemplate
        {
            TypeDiscriminator = "TCardItem",
            Id = "test:fragile_aegis",
            Type = "Item",
            InternalName = "FragileAegis",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = [HeroNames.Common],
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["Custom_0"] = 2 } }
            },
            CustomMechanics = [new CardMechanicAttachment { MechanicId = "demo:fragile_aegis", Params = new() { ["blocks"] = J(2), ["permanentBreak"] = J(true) } }]
        };

        // 友方放一张原版语义的 TActionCardRepair 修理扳手，每 1200ms 尝试修复所有受损己方物品
        var repairWrenchTpl = new CardTemplate
        {
            TypeDiscriminator = "TCardItem",
            Id = "test:repair_wrench",
            Type = "Item",
            InternalName = "RepairWrench",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = [HeroNames.Common],
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 1200 }, AbilityIds = ["0"] }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardRepair",
                        Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" }
                    }
                }
            }
        };

        // 敌方每 1000ms 打 100 点直伤；玩家只有 250 血 -> 若挡住前 2 次（1000ms, 2000ms），第 3/4/5 次（3000/4000/5000ms）共吃 300 致死
        // 如果修理扳手在 2400ms 把护盾复原了，玩家不会在第 5000ms 阵亡！
        var enemyHammerTpl = MakeSimpleWeapon("test:enemy_hammer", "敌方重锤", cdMs: 1000, dmg: 100);

        var overlayRepo = data.Repo.CreateWithOverlay([aegisTpl, repairWrenchTpl, enemyHammerTpl], [], null);
        var p1 = new PlayerState("p1", "守方", HeroNames.Pygmalien, 250);
        var p2 = new PlayerState("p2", "攻方", HeroNames.Vanessa, 2000);
        Assert.True(p1.Hand.TryPlaceOrShift(new CardInstance(aegisTpl, CardTier.Bronze, null, "aegis_1"), 0));
        Assert.True(p1.Hand.TryPlaceOrShift(new CardInstance(repairWrenchTpl, CardTier.Bronze, null, "wrench_1"), 1));
        Assert.True(p2.Hand.TryPlaceOrShift(new CardInstance(enemyHammerTpl, CardTier.Bronze, null, "hammer_1"), 0));

        var mechMap = new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase) { [aegisMech.Id] = aegisMech };
        var result = CombatSimulator.Simulate(
            overlayRepo, p1, p2,
            seed: 99, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false,
            modifiers: null, sandstormEnabled: false, timelineSampleIntervalMs: 50,
            mechanicDefinitionsOverride: mechMap);

        Assert.Equal(0, result.MechanicCircuitBreakerTrips);
        Assert.Equal(0, result.MechanicBudgetViolations);

        // 前 2 次重锤（980ms, 1980ms）被 BeforeDamage 完全格挡，只有第 3/4/5 次（2980/3980/4980ms）造成伤害
        var realHits = result.Events.Where(e => e.Type == CombatEventType.DamageApplied && e.TargetPlayerId == "p1").ToList();
        Assert.Equal(3, realHits.Count);
        Assert.True(realHits[0].TimeMs >= 2900, $"前两次伤害应被格挡，首次掉血应 >= 2900ms，实际={realHits[0].TimeMs}ms");

        // 碎裂后的护盾尽管经历了多轮 TActionCardRepair 依然在客户端时间轴上保持 IsDestroyed/Disabled（不可复原）
        var lastFrame = result.Timeline[^1];
        var aegisFrame = lastFrame.Cards.First(c => c.InstanceId == "aegis_1");
        Assert.True(aegisFrame.IsDestroyed, "带有 unrepairable_shatter 标记的神盾必须拦截 TActionCardRepair 保持不可复原");

        // 战后结转队列包含永久移除该护盾的操作
        Assert.Contains(result.PostCombatOps, op => op.OpKind == "RemoveCard" && op.CardInstanceId == "aegis_1");
    }

    [Fact]
    public void V14_03_SafetyShield_InfiniteRecursionAndHugeRepeat_AbortedByBudget_AndBlockedByGate9()
    {
        var data = Shared.Value;

        // 恶意/Bug 机制：ping 触发 ping 无限递归 + Repeat 设超大值
        var infiniteLoopMech = new MechanicDefinition
        {
            Id = "bad:infinite_ping",
            NameZh = "无限递归自爆死循环",
            Hooks =
            [
                new MechanicHookDefinition
                {
                    Id = "start",
                    Hook = "OnFightStart",
                    Scope = "SelfCard",
                    Steps = [new MechanicStep { Op = "EmitCustomEvent", EventName = "ping" }]
                },
                new MechanicHookDefinition
                {
                    Id = "loop",
                    Hook = "OnCustomEvent",
                    Scope = "SelfCard",
                    Filter = new MechanicHookFilter { CustomEventName = "ping" },
                    Steps =
                    [
                        new MechanicStep { Op = "EmitCustomEvent", EventName = "ping" },
                        new MechanicStep { Op = "DealDamage", PlayerTarget = "Opponent", Amount = new MechanicValueExpr { Const = 1 } }
                    ]
                }
            ]
        };

        var cardDef = new CustomCardDefinition
        {
            CustomId = "bad-mod:looper",
            BaseVisualTemplateId = SmallItemCarrierId,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Heroes = [HeroNames.Common],
            TitleZh = "死循环测试卡",
            DescZh = "用于测试 5 道安全铁闸",
            ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害"],
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 2, ["SellPrice"] = 1, ["CooldownMax"] = 4000, ["DamageAmount"] = 10 }, AbilityIds = ["0"] },
                ["Silver"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 4, ["SellPrice"] = 2, ["CooldownMax"] = 4000, ["DamageAmount"] = 20 }, AbilityIds = ["0"] },
                ["Gold"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 8, ["SellPrice"] = 4, ["CooldownMax"] = 4000, ["DamageAmount"] = 40 }, AbilityIds = ["0"] },
                ["Diamond"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 16, ["SellPrice"] = 8, ["CooldownMax"] = 4000, ["DamageAmount"] = 80 }, AbilityIds = ["0"] }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                }
            },
            CustomMechanics = [new CardMechanicAttachment { MechanicId = "bad:infinite_ping" }]
        };

        var badPkg = new ModPackage
        {
            Manifest = new ModManifest { Id = "bad-mod", Name = "坏机制测试", Version = "1.0.0", Author = "test" },
            RootDirectory = Path.Combine(Path.GetTempPath(), "bazaar_v14_bad_loop")
        };
        badPkg.Cards.Add(cardDef);
        badPkg.Mechanics.Add(infiniteLoopMech);

        // 10 道铁闸在隔离沙盒里必须秒级跑完（不挂死）并在 Gate #9 硬阻断 SIM_MECHANIC_SAFETY_TRIP
        var report = ModValidator.RunTenGateValidation(badPkg, null, data.Repo, data.Whitelist, combatMatches: 8, runDynamicGates: true);
        Assert.Equal(10, report.Gates.Count);
        Assert.False(report.Passed);
        var gate9 = report.Gates.First(g => g.GateId == 9);
        Assert.Equal("FAIL", gate9.Status);
        Assert.Contains(gate9.Issues, i => i.Code == "SIM_MECHANIC_SAFETY_TRIP");
        Assert.NotNull(report.CombatStressReport);
        Assert.True(report.CombatStressReport!.MechanicBudgetViolations > 0);
    }

    private sealed class ThrowingTestInterceptor : ICombatPipelineInterceptor
    {
        public string InterceptorId => "test:throwing_interceptor";
        public int CallCount { get; private set; }
        public void OnPipelineEvent(CombatPipelineEvent pipeEvent, ICombatMutationApi api)
        {
            if (pipeEvent.HookName == "OnCardFired")
            {
                CallCount++;
                throw new InvalidOperationException("模拟第三方插件崩溃异常");
            }
        }
    }

    [Fact]
    public void V14_04_SafetyShield_ThrowingInterceptorAndNegativeCooldown_IsolatedAndClamped()
    {
        var data = Shared.Value;
        var badCdrMech = new MechanicDefinition
        {
            Id = "demo:crazy_cdr",
            NameZh = "极端越界属性测试",
            Hooks =
            [
                new MechanicHookDefinition
                {
                    Id = "corrupt",
                    Hook = "OnFightStart",
                    Scope = "SelfCard",
                    Steps =
                    [
                        new MechanicStep { Op = "ModifyCardAttribute", TargetSpec = new MechanicTargetSpec { Mode = "Self" }, Attribute = "CooldownMax", Operation = "Set", Value = new MechanicValueExpr { Const = -99999 } },
                        new MechanicStep { Op = "ModifyCardAttribute", TargetSpec = new MechanicTargetSpec { Mode = "Self" }, Attribute = "PercentCooldownReduction", Operation = "Add", Value = new MechanicValueExpr { Const = 500 } }
                    ]
                }
            ]
        };

        var weaponTpl = MakeSimpleWeapon("test:clamped_weapon", "被钳制武器", cdMs: 2000, dmg: 100,
            mechs: [new CardMechanicAttachment { MechanicId = "demo:crazy_cdr" }]);
        var dummyEnemyTpl = MakeSimpleWeapon("test:dummy_enemy", "木桩武器", cdMs: 5000, dmg: 1);

        var overlayRepo = data.Repo.CreateWithOverlay([weaponTpl, dummyEnemyTpl], [], null);
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500);
        var p2 = new PlayerState("p2", "P2", HeroNames.Dooley, 300);
        Assert.True(p1.Hand.TryPlaceOrShift(new CardInstance(weaponTpl, CardTier.Bronze, null, "w1"), 0));
        Assert.True(p2.Hand.TryPlaceOrShift(new CardInstance(dummyEnemyTpl, CardTier.Bronze, null, "e1"), 0));

        var throwingInterceptor = new ThrowingTestInterceptor();
        var mechMap = new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase) { [badCdrMech.Id] = badCdrMech };
        var result = CombatSimulator.Simulate(
            overlayRepo, p1, p2,
            seed: 7, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false,
            modifiers: null, sandstormEnabled: true, timelineSampleIntervalMs: 50,
            mechanicDefinitionsOverride: mechMap,
            pipelineInterceptorsOverride: [throwingInterceptor]);

        // 1. 抛异常的 C# 拦截器只执行 1 次即被熔断隔离，不崩溃且不影响正常武器击杀对手
        Assert.Equal(1, throwingInterceptor.CallCount);
        Assert.Equal(1, result.MechanicCircuitBreakerTrips);
        Assert.Equal("p1", result.WinnerPlayerId);

        // 2. 负冷却被强钳制 >= 1000ms，百分比缩减不会制造 0 秒无限开火（通过客户端时间轴帧验证）
        var weaponFrame = result.Timeline[^1].Cards.First(c => c.InstanceId == "w1");
        Assert.True(weaponFrame.CooldownMaxMs >= 1000, $"有效 CooldownMaxMs 应被钳制 >= 1000ms，实际={weaponFrame.CooldownMaxMs}ms");
    }

    [Fact]
    public void V14_05_DiskModWithMechanicsJson_StaticAudit_HotReload_AndRollbackZeroResidue()
    {
        var data = Shared.Value;
        string tempRoot = Path.Combine(Path.GetTempPath(), $"bazaar_v14_disk_{Guid.NewGuid():N}");
        string modDir = Path.Combine(tempRoot, "open-mech-demo");
        Directory.CreateDirectory(Path.Combine(modDir, "mechanics"));
        Directory.CreateDirectory(Path.Combine(modDir, "cards"));

        try
        {
            File.WriteAllText(Path.Combine(modDir, "mod.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                id = "open-mech-demo",
                name = "开放机制磁盘集成包",
                version = "1.0.0",
                author = "test",
                scope = "multiplayer-safe",
                affectsGhostPool = false
            }), Encoding.UTF8);

            File.WriteAllText(Path.Combine(modDir, "mechanics", "custom_mechanics.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                mechanics = new[]
                {
                    new MechanicDefinition
                    {
                        Id = "open-mech-demo:shield_pulse",
                        NameZh = "开火护盾脉冲",
                        DescZh = "开火时额外获得固定护盾",
                        Parameters = [new MechanicParameterDef { Key = "shieldAmt", LabelZh = "护盾值", Type = "int", DefaultValue = J(12), Min = 1, Max = 200 }],
                        Hooks =
                        [
                            new MechanicHookDefinition
                            {
                                Id = "pulse",
                                Hook = "OnCardFired",
                                Scope = "SelfCard",
                                Filter = new MechanicHookFilter { OnlyWhenSelfSource = true },
                                Steps = [new MechanicStep { Op = "ApplyStatus", Status = "Shield", PlayerTarget = "Self", Amount = new MechanicValueExpr { Param = "shieldAmt" } }]
                            }
                        ]
                    }
                }
            }), Encoding.UTF8);

            File.WriteAllText(Path.Combine(modDir, "cards", "custom_cards.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                cards = new[]
                {
                    new CustomCardDefinition
                    {
                        CustomId = "open-mech-demo:pulse_dagger",
                        BaseVisualTemplateId = SmallItemCarrierId,
                        Type = "Item",
                        Size = "Small",
                        StartingTier = "Bronze",
                        Heroes = [HeroNames.Common],
                        TitleZh = "脉冲短刃",
                        DescZh = "附带开放自定义机制的演示短刃",
                        ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害"],
                        Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Bronze"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 2, ["SellPrice"] = 1, ["CooldownMax"] = 3500, ["DamageAmount"] = 15 }, AbilityIds = ["0"] },
                            ["Silver"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 4, ["SellPrice"] = 2, ["CooldownMax"] = 3500, ["DamageAmount"] = 30 }, AbilityIds = ["0"] },
                            ["Gold"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 8, ["SellPrice"] = 4, ["CooldownMax"] = 3000, ["DamageAmount"] = 60 }, AbilityIds = ["0"] },
                            ["Diamond"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 16, ["SellPrice"] = 8, ["CooldownMax"] = 3000, ["DamageAmount"] = 120 }, AbilityIds = ["0"] }
                        },
                        Abilities = new Dictionary<string, AbilityDefinition>
                        {
                            ["0"] = new AbilityDefinition
                            {
                                Id = "0",
                                ActiveIn = "HandOnly",
                                Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                                Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                            }
                        },
                        CustomMechanics = [new CardMechanicAttachment { MechanicId = "open-mech-demo:shield_pulse", Params = new() { ["shieldAmt"] = J(18) } }]
                    }
                }
            }), Encoding.UTF8);

            var (pkgs, scanIssues) = ModLoader.ScanDirectory(tempRoot);
            Assert.Empty(scanIssues.Where(i => i.Severity == ModValidationSeverity.Error));
            Assert.Single(pkgs);
            Assert.Single(pkgs[0].Mechanics);

            var tenGate = ModValidator.RunTenGateValidation(pkgs[0], null, data.Repo, data.Whitelist, combatMatches: 15, runDynamicGates: true);
            Assert.True(tenGate.Passed, $"磁盘机制包应全绿通过，实际问题：{string.Join(" | ", tenGate.Gates.SelectMany(g => g.Issues).Select(i => i.Message))}");

            // 热加载进 ModRegistry 并验证注册至全局运行态，再回滚验证零残留
            var registry = new ModRegistry(data.Repo, data.Whitelist);
            var loadReport = registry.LoadFromDirectory(tempRoot);
            Assert.True(loadReport.Succeeded);
            Assert.Equal(1, loadReport.TotalCustomMechanics);
            Assert.True(registry.MechanicsById.ContainsKey("open-mech-demo:shield_pulse"));
            Assert.True(ModRuntimeHooks.HasAnyCustomMechanics);

            // 反向检验 Gate #4 静态审计拦截未知 Op 与未声明参数
            var badMech = new MechanicDefinition
            {
                Id = "open-mech-demo:broken",
                NameZh = "非法指令机制",
                Hooks = [new MechanicHookDefinition { Hook = "OnCardFired", Steps = [new MechanicStep { Op = "ExecShellHack" }] }]
            };
            var badIssues = new List<ModValidationIssue>();
            MechanicSandboxInterpreter.ValidateStatic(badMech, "open-mech-demo", badIssues);
            Assert.Contains(badIssues, i => i.Code == "MECHANIC_UNKNOWN_OP");

            registry.RollbackToBase();
            Assert.Empty(registry.MechanicsById);
            Assert.False(ModRuntimeHooks.HasAnyCustomMechanics);
        }
        finally
        {
            ModRuntimeHooks.ClearAll();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task V14_06_WorkshopEndpoints_MechanicsCatalog_And_LiveAdversarialStressTestApi()
    {
        var data = Shared.Value;
        string tempMods = Path.Combine(Path.GetTempPath(), $"bazaar_v14_web_{Guid.NewGuid():N}");
        string tempState = Path.Combine(tempMods, "_state");
        Directory.CreateDirectory(tempMods);
        Directory.CreateDirectory(tempState);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var modHost = new ServerModHost(
            data.Repo,
            data.DataDir,
            tempState,
            data.Whitelist,
            modsRootOverride: tempMods);
        builder.Services.AddSingleton(modHost);
        builder.Services.AddSingleton<WorkshopValidatorService>();

        var app = builder.Build();
        WorkshopEndpoints.Map(app);
        await app.StartAsync();

        try
        {
            string baseAddr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(baseAddr) };

            // 1. rule-catalog 包含 mechanicSchema 与官方 131 种 VFX 键
            var catResp = await http.GetStringAsync("/api/workshop/rule-catalog");
            using var catDoc = JsonDocument.Parse(catResp);
            Assert.True(catDoc.RootElement.TryGetProperty("mechanicSchema", out var mSchema));
            Assert.True(mSchema.GetProperty("hooks").GetArrayLength() >= 10);
            Assert.True(mSchema.GetProperty("ops").GetArrayLength() >= 18);
            Assert.True(catDoc.RootElement.GetProperty("vfxKeys").GetArrayLength() >= 100);

            // 2. /api/workshop/mechanics 正常返回
            var mechsResp = await http.GetAsync("/api/workshop/mechanics");
            Assert.Equal(HttpStatusCode.OK, mechsResp.StatusCode);

            // 3. /api/workshop/mechanics/stress-test 对合法机制通过、对死循环机制拦截
            var goodMech = new MechanicDefinition
            {
                Id = "lab:haste_pulse",
                NameZh = "开火加速相邻",
                Parameters = [new MechanicParameterDef { Key = "ms", LabelZh = "加速毫秒", Type = "int", DefaultValue = J(1000), Min = 200, Max = 5000 }],
                Hooks =
                [
                    new MechanicHookDefinition
                    {
                        Hook = "OnCardFired",
                        Scope = "SelfCard",
                        Filter = new MechanicHookFilter { OnlyWhenSelfSource = true },
                        Steps = [new MechanicStep { Op = "ApplyStatus", Status = "Haste", TargetSpec = new MechanicTargetSpec { Mode = "Adjacent" }, Amount = new MechanicValueExpr { Param = "ms" } }]
                    }
                ]
            };

            var goodResp = await http.PostAsJsonAsync("/api/workshop/mechanics/stress-test", new { mechanic = goodMech, matches = 8 });
            Assert.Equal(HttpStatusCode.OK, goodResp.StatusCode);
            using var goodDoc = JsonDocument.Parse(await goodResp.Content.ReadAsStringAsync());
            Assert.True(goodDoc.RootElement.GetProperty("passed").GetBoolean());

            var badMech = new MechanicDefinition
            {
                Id = "lab:bad_loop",
                NameZh = "死循环测试",
                Hooks =
                [
                    new MechanicHookDefinition { Hook = "OnFightStart", Scope = "SelfCard", Steps = [new MechanicStep { Op = "EmitCustomEvent", EventName = "x" }] },
                    new MechanicHookDefinition { Hook = "OnCustomEvent", Scope = "SelfCard", Filter = new MechanicHookFilter { CustomEventName = "x" }, Steps = [new MechanicStep { Op = "EmitCustomEvent", EventName = "x" }] }
                ]
            };
            var badResp = await http.PostAsJsonAsync("/api/workshop/mechanics/stress-test", new { mechanic = badMech, matches = 6 });
            Assert.Equal(HttpStatusCode.OK, badResp.StatusCode);
            using var badDoc = JsonDocument.Parse(await badResp.Content.ReadAsStringAsync());
            Assert.False(badDoc.RootElement.GetProperty("passed").GetBoolean());
        }
        finally
        {
            await app.StopAsync();
            ModRuntimeHooks.ClearAll();
            if (Directory.Exists(tempMods)) Directory.Delete(tempMods, recursive: true);
        }
    }
}
