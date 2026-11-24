using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarServer.Data;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1 Batch 1A（Core 战斗与属性底座）官方一致性回归测试：
/// 覆盖 P0-01、P0-03、P0-04、P0-05、P0-06、P0-07、P0-08 及 P0-09/P0-10 Core 日志底座。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class AuditWave1CoreTests
{
    private const string ChillyCharlesId = "05c7cd54-75a3-4eae-9a1a-798c90b0db2d";
    private const string CortekMusicGeneratorId = "6d73917f-7d81-4f73-894d-ade357d034af";
    private const string JoyfulJackId = "6d946fe1-13cc-4141-bc89-84981c4ef77f";
    private const string PveJulesD6Id = "b21565c5-71c6-4e4d-bf3d-5fd9d8602aa8";

    private const string CoolerSocketId = "d5515ae3-b98d-4272-8add-82f441969ffe";
    private const string StoveSocketId = "c017c0dd-af3c-47e0-b510-3661d9dcd7f2";

    private const string BeltId = "4f8bc475-212c-49cf-b3e5-d1f971897a24";
    private const string BanuLeafCloakId = "8169bf55-6b2e-4884-a5fc-a665072620aa";
    private const string WildStrengthId = "fb4a4bf9-581c-4e36-aed7-b77ec7e515bb";
    private const string EagleSigilId = "5b4b3685-aa1d-477e-aed5-805813faa2b2";
    private const string SundererId = "dec20bd9-9c83-4fcb-a5d2-e71352ed78d5";
    private const string AbsorbMemoriesId = "5fff0970-87d1-4e43-a466-b771deaf40fe";
    private const string LangxianId = "53099361-599f-4c1a-ad4a-d2fbb491b756";
    private const string TrailMarkersId = "a4281e9f-7889-4ac1-a140-9152ab54bc6b";

    private static readonly Lazy<string> DbPath = new(() => TestInfrastructure.SharedGameDataFixture.Shared.DbPath);
    private static readonly Lazy<GameDataRepository> SharedRepo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    private static RuleNode ParseNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)
           ?? throw new InvalidOperationException("RuleNode 反序列化失败");

    private static CardInstance MakeCard(GameDataRepository repo, string templateId, CardTier? tier = null, string? enchantment = null, string? instanceId = null)
    {
        var tpl = repo.GetCard(templateId) ?? throw new InvalidOperationException($"未找到模板 {templateId}");
        return new CardInstance(tpl, tier, enchantment, instanceId);
    }

    private static CardTemplate CreateSyntheticItem(
        string id,
        string name,
        Dictionary<string, double> baseAttrs,
        Dictionary<string, AbilityDefinition>? abilities = null,
        Dictionary<string, AuraDefinition>? auras = null,
        List<string>? tags = null,
        List<string>? hiddenTags = null)
    {
        var abIds = abilities?.Keys.ToList() ?? [];
        var auIds = auras?.Keys.ToList() ?? [];
        return new CardTemplate
        {
            Id = id,
            InternalName = name,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tags = tags ?? [],
            HiddenTags = hiddenTags ?? [],
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new Dictionary<string, double>(baseAttrs, StringComparer.OrdinalIgnoreCase),
                    AbilityIds = abIds,
                    AuraIds = auIds
                }
            },
            Abilities = abilities ?? new Dictionary<string, AbilityDefinition>(),
            Auras = auras ?? new Dictionary<string, AuraDefinition>()
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // P0-01：monsters.Player.Socket.Effects 反序列化与双路径加载
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_01_MonsterSocketEffects_AllFourMonstersLoadAllSixCoolerAndStoveSlots()
    {
        var repo = SharedRepo.Value;

        var expected = new (string Id, int Count, string TemplateId, int[] Slots)[]
        {
            (ChillyCharlesId, 2, CoolerSocketId, [0, 1]),
            (CortekMusicGeneratorId, 2, CoolerSocketId, [0, 1]),
            (JoyfulJackId, 1, StoveSocketId, [5]),
            (PveJulesD6Id, 1, CoolerSocketId, [5]),
        };

        // 1. 验证 GameDataRepository（Core Model Player.Socket.Effects）与 CombatSimulator.BuildMonsterPlayerState
        int coreTotal = 0;
        foreach (var (id, count, tplId, slots) in expected)
        {
            var monster = repo.GetMonster(id);
            Assert.NotNull(monster);
            var effects = monster!.Player.GetEffectiveSocketEffects();
            Assert.Equal(count, effects.Count);
            Assert.All(effects, e => Assert.Equal(tplId, e.TemplateId, StringComparer.OrdinalIgnoreCase));
            coreTotal += effects.Count;

            var combatState = CombatSimulator.BuildMonsterPlayerState(repo, monster);
            Assert.Equal(count, combatState.SocketEffects.Count);
            Assert.Equal(slots, combatState.SocketEffects.Select(s => s.StartSlot).OrderBy(x => x).ToArray());
        }
        Assert.Equal(6, coreTotal);

        // 2. 验证 MonsterPool.FromRepository 路径
        var fromRepo = MonsterPool.FromRepository(repo).ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        int repoPoolTotal = 0;
        foreach (var (id, count, tplId, slots) in expected)
        {
            Assert.True(fromRepo.TryGetValue(id, out var m));
            Assert.Equal(count, m!.SocketEffects.Count);
            Assert.All(m.SocketEffects, e => Assert.Equal(tplId, e.TemplateId, StringComparer.OrdinalIgnoreCase));
            var parsedSlots = m.SocketEffects.Select(e => int.Parse(e.SocketId.Split('_')[^1])).OrderBy(x => x).ToArray();
            Assert.Equal(slots, parsedSlots);
            repoPoolTotal += m.SocketEffects.Count;
        }
        Assert.Equal(6, repoPoolTotal);

        // 3. 验证 MonsterPool.Load(dbPath) 直接解析 JSON 路径
        var fromDb = MonsterPool.Load(DbPath.Value).ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        int dbPoolTotal = 0;
        foreach (var (id, count, tplId, slots) in expected)
        {
            Assert.True(fromDb.TryGetValue(id, out var m));
            Assert.Equal(count, m!.SocketEffects.Count);
            Assert.All(m.SocketEffects, e => Assert.Equal(tplId, e.TemplateId, StringComparer.OrdinalIgnoreCase));
            var parsedSlots = m.SocketEffects.Select(e => int.Parse(e.SocketId.Split('_')[^1])).OrderBy(x => x).ToArray();
            Assert.Equal(slots, parsedSlots);
            dbPoolTotal += m.SocketEffects.Count;
        }
        Assert.Equal(6, dbPoolTotal);
    }

    [Fact]
    public void P0_01_MonsterPlayerData_MergesNestedSocketAndLegacySocketEffectsWithoutDuplicates()
    {
        var data = new MonsterPlayerData
        {
            Socket = new MonsterSocketInventoryData
            {
                Effects =
                [
                    new MonsterCardEntry { SocketId = "Socket_0", TemplateId = CoolerSocketId, Tier = "Diamond" }
                ]
            },
            SocketEffects =
            [
                new MonsterCardEntry { SocketId = "Socket_0", TemplateId = CoolerSocketId, Tier = "Diamond" },
                new MonsterCardEntry { SocketId = "Socket_5", TemplateId = StoveSocketId, Tier = "Diamond" }
            ]
        };

        var merged = data.GetEffectiveSocketEffects();
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, e => e.SocketId == "Socket_0" && e.TemplateId == CoolerSocketId);
        Assert.Contains(merged, e => e.SocketId == "Socket_5" && e.TemplateId == StoveSocketId);
    }

    // ════════════════════════════════════════════════════════════════════════
    // P0-03：Regen Duration 隔离、HealthRegen 属性变更触发、Enraged 隔离与持久属性白名单
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_03_CombatTemporaryRegen_DoesNotLeakToPermanentAttributes_AndFiresHealthRegenChanged()
    {
        var repo = SharedRepo.Value;
        var p1 = new PlayerState("p1", "Hero", HeroNames.Mak, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "Dummy", HeroNames.Vanessa, 50, 20, 0, 10);

        // 施加 UntilEndOfCombat 再生（与全库 1,102/1,104 条战斗再生及 Mossy 附魔同构），并挂一个监听 HealthRegen 属性变化的计数器
        var regenItem = new CardInstance(CreateSyntheticItem(
            "test_temp_regen",
            "Temp Regen Vial",
            new() { ["CooldownMax"] = 1000, ["RegenApplyAmount"] = 25, ["Custom_0"] = 0 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionPlayerRegenApply",
                         "Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},
                         "Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
                        """)
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""
                        {"$type":"TTriggerOnPlayerAttributeChanged",
                         "AttributeChanged":"HealthRegen",
                         "Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
                        """),
                    Action = ParseNode("""
                        {"$type":"TActionCardModifyAttribute",
                         "AttributeType":"Custom_0","Operation":"Add",
                         "Value":{"$type":"TFixedValue","Value":1},
                         "Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                },
                ["2"] = new AbilityDefinition
                {
                    Id = "2",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionPlayerDamage",
                         "ReferenceValue":{"$type":"TFixedValue","Value":100},
                         "Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}
                        """)
                }
            }));
        p1.Hand.TryAddAuto(regenItem);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 7, recordTimeline: true, recordEvents: true, syncPermanentChanges: true);

        // 战内临时 Regen 生效（Timeline 记录 P1Regen == 25）且触发 TTriggerOnPlayerAttributeChanged("HealthRegen") 使 Custom_0 +1
        Assert.Contains(res.Timeline, t => t.P1Regen == 25);
        Assert.Contains(res.Events, e =>
            e.Type == CombatEventType.CardAttributeModified
            && e.Detail == "Custom_0"
            && e.TargetCardInstanceId == regenItem.InstanceId
            && e.Value >= 1);

        // 战后零泄漏：原玩家 HealthRegen 保持 0 且 PostCombatPermanentSync.PlayerAttributeDeltas 不含 HealthRegen
        Assert.Equal(0, p1.GetAttribute("HealthRegen"));
        Assert.Equal(0, p1.Attributes.GetValueOrDefault("HealthRegen", 0));
        Assert.NotNull(res.PostCombatPermanentSync);
        Assert.False(res.PostCombatPermanentSync!.PlayerAttributeDeltas.ContainsKey("HealthRegen"));
    }

    [Fact]
    public void P0_03_PermanentRegen_DurationNull_SyncsBackViaExplicitWhitelist()
    {
        var repo = SharedRepo.Value;
        var p1 = new PlayerState("p1", "Hero", HeroNames.Mak, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "Dummy", HeroNames.Vanessa, 50, 20, 0, 10);

        var permRegenItem = new CardInstance(CreateSyntheticItem(
            "test_perm_regen",
            "Permanent Spring",
            new() { ["CooldownMax"] = 1000, ["RegenApplyAmount"] = 7 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionPlayerRegenApply",
                         "Duration":null,
                         "Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
                        """)
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionPlayerDamage",
                         "ReferenceValue":{"$type":"TFixedValue","Value":100},
                         "Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}
                        """)
                }
            }));
        p1.Hand.TryAddAuto(permRegenItem);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 8, syncPermanentChanges: true);

        Assert.Equal(7, p1.GetAttribute("HealthRegen"));
        Assert.Equal(7, res.PostCombatPermanentSync!.PlayerAttributeDeltas.GetValueOrDefault("HealthRegen", 0));
    }

    [Fact]
    public void P0_03_EnragedAndEnragedDuration_DoNotPollutePermanentAttributesOrLeakAfterCombat()
    {
        var repo = SharedRepo.Value;
        var p1 = new PlayerState("p1", "Karnok", HeroNames.Karnok, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "Dummy", HeroNames.Vanessa, 50, 20, 0, 10);

        // 开战瞬间满怒进入狂暴（5000ms），1000ms 击杀对手，在狂暴持续期间结束战斗
        var rageFinisher = new CardInstance(CreateSyntheticItem(
            "test_rage_finisher",
            "Rage Finisher",
            new() { ["CooldownMax"] = 1000, ["RageApplyAmount"] = 100, ["DamageAmount"] = 100 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionPlayerRageApply",
                         "Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
                        """)
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionPlayerDamage",
                         "Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}
                        """)
                },
                ["2"] = new AbilityDefinition
                {
                    Id = "2",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionPlayerModifyAttribute",
                         "AttributeType":"Gold","Operation":"Add","Duration":null,
                         "Value":{"$type":"TFixedValue","Value":3},
                         "Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
                        """)
                }
            }));
        p1.Hand.TryAddAuto(rageFinisher);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 9, recordTimeline: true, recordEvents: true, syncPermanentChanges: true);

        // 战末处于狂暴态（Timeline 最后帧 P1Enraged == true）
        Assert.True(res.Timeline[^1].P1Enraged, "测试前提：玩家应在狂暴持续期内结束战斗");

        // 直接执行 TActionPlayerRageApply 核实 Enraged/EnragedDuration 从不污染 Attributes
        var probeP = new PlayerState("probe", "Karnok", HeroNames.Karnok, 500, 20, 10, 10);
        var probeCtx = new RuleExecutionContext(repo, RuleScope.InCombat, probeP, p2, new Random(1)) { SelfCard = rageFinisher };
        AbilityExecutor.ExecuteAction(rageFinisher.ActiveAbilities[0].Action, rageFinisher, probeCtx);
        Assert.True(probeP.IsEnraged);
        Assert.Equal(1.0, probeP.GetAttribute("Enraged"));
        Assert.True(probeP.GetAttribute("EnragedDuration") > 0);
        Assert.False(probeP.Attributes.ContainsKey("Enraged"), "Enraged 绝不能写入永久 Attributes");
        Assert.False(probeP.Attributes.ContainsKey("EnragedDuration"), "EnragedDuration 绝不能写入永久 Attributes");

        // 战后白名单：允许 Gold 正常回写，彻底阻断 Enraged/EnragedDuration 泄漏
        Assert.Equal(13, p1.Gold);
        Assert.False(p1.Attributes.ContainsKey("Enraged"));
        Assert.False(p1.Attributes.ContainsKey("EnragedDuration"));
        Assert.Equal(0, p1.GetAttribute("Enraged"));
        Assert.Equal(0, p1.GetAttribute("EnragedDuration"));
        Assert.False(res.PostCombatPermanentSync!.PlayerAttributeDeltas.ContainsKey("Enraged"));
        Assert.False(res.PostCombatPermanentSync.PlayerAttributeDeltas.ContainsKey("EnragedDuration"));
        Assert.Equal(3, res.PostCombatPermanentSync.PlayerAttributeDeltas.GetValueOrDefault("Gold", 0));
    }

    // ════════════════════════════════════════════════════════════════════════
    // P0-04：HealthMax 光环满血开战 + PlayerState 四层 AdditiveMult/Mult 拆分
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_04_BeltHealthMaxAura_StartsCombatAtFullPostAuraHealth()
    {
        var repo = SharedRepo.Value;
        var p1 = new PlayerState("p1", "Pyg", HeroNames.Pygmalien, 1000, 20, 10, 10);
        var p2 = new PlayerState("p2", "Enemy", HeroNames.Vanessa, 500, 20, 0, 10);

        // Diamond Belt (+80% MaxHP AdditiveMultiply in GameData.db v5.0.0)
        var belt = MakeCard(repo, BeltId, CardTier.Diamond);
        p1.Hand.TryAddAuto(belt);

        // 额外挂一把 1s 快速武器确保战斗正常推进
        p1.Hand.TryAddAuto(new CardInstance(CreateSyntheticItem(
            "test_finisher_belt",
            "Finisher",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 600 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            })));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 11, recordTimeline: true);
        Assert.NotEmpty(res.Timeline);
        var t0 = res.Timeline[0];
        Assert.Equal(1800, t0.P1MaxHp);
        Assert.Equal(1800, t0.P1Hp);

        // 验证配合 CombatModifiers.HealthMultiplierP1 = 2.0 时仍保持倍率顺序并满血开战（1000 * 2 * (1+0.8) = 3600）
        var modRes = CombatSimulator.Simulate(
            repo,
            p1,
            p2,
            seed: 11,
            combatType: "PVP",
            recordTimeline: true,
            recordEvents: false,
            syncPermanentChanges: false,
            modifiers: new CombatModifiers { HealthMultiplierP1 = 2.0 });
        Assert.Equal(3600, modRes.Timeline[0].P1MaxHp);
        Assert.Equal(3600, modRes.Timeline[0].P1Hp);
    }

    [Fact]
    public void P0_04_PlayerState_AdditiveMultiplyAndMultiply_AreOrderIndependentAcrossAllFourLayers()
    {
        static double EvalOrder(DurationTarget? layer, bool additiveFirst)
        {
            var ps = new PlayerState("p", "P", HeroNames.Pygmalien, 1000, 20, 10, 10);
            void Apply(string op, double val)
            {
                if (layer == null) ps.ApplyAuraAttribute("HealthMax", op, val);
                else ps.ModifyAttribute("HealthMax", op, val, layer.Value);
            }

            if (additiveFirst)
            {
                Apply("AdditiveMultiply", 0.5);
                Apply("AdditiveMultiply", 0.5);
                Apply("Multiply", 0.5);
            }
            else
            {
                Apply("Multiply", 0.5);
                Apply("AdditiveMultiply", 0.5);
                Apply("AdditiveMultiply", 0.5);
            }
            return ps.GetAttribute("HealthMax");
        }

        // 四层全部严格遵循 (base + add) * (1.0 + additiveMult) * mult = 1000 * (1 + 1.0) * 0.5 = 1000，与施加顺序完全无关
        Assert.Equal(1000.0, EvalOrder(null, additiveFirst: true), 6);
        Assert.Equal(1000.0, EvalOrder(null, additiveFirst: false), 6);
        Assert.Equal(1000.0, EvalOrder(DurationTarget.Combat, additiveFirst: true), 6);
        Assert.Equal(1000.0, EvalOrder(DurationTarget.Combat, additiveFirst: false), 6);
        Assert.Equal(1000.0, EvalOrder(DurationTarget.Hour, additiveFirst: true), 6);
        Assert.Equal(1000.0, EvalOrder(DurationTarget.Hour, additiveFirst: false), 6);
        Assert.Equal(1000.0, EvalOrder(DurationTarget.Day, additiveFirst: true), 6);
        Assert.Equal(1000.0, EvalOrder(DurationTarget.Day, additiveFirst: false), 6);

        // 验证 RunEngine 兼容性：Belt AdditiveMultiply(+1.0) 后公开 AuraMult["HealthMax"] 同步为 2.0，可供 RunEngine 反剥离还原裸值 1000
        var runCompatPs = new PlayerState("p", "P", HeroNames.Pygmalien, 1000, 20, 10, 10);
        runCompatPs.ApplyAuraAttribute("HealthMax", "AdditiveMultiply", 1.0);
        Assert.Equal(1.0, runCompatPs.AuraAdditiveMult["HealthMax"], 6);
        Assert.Equal(2.0, runCompatPs.AuraMult["HealthMax"], 6);
        Assert.Equal(2000.0, runCompatPs.GetAttribute("HealthMax"), 6);
        double strippedBase = runCompatPs.GetAttribute("HealthMax") / runCompatPs.AuraMult["HealthMax"];
        Assert.Equal(1000.0, strippedBase, 6);

        // 验证真实卡组合：己方 Belt (AdditiveMultiply +1.0) + 敌方 Banu Leaf Cloak (Multiply) 互不污染
        var repo = SharedRepo.Value;
        var a = new PlayerState("a", "A", HeroNames.Pygmalien, 1000, 20, 10, 10);
        var b = new PlayerState("b", "B", HeroNames.Mak, 1000, 20, 10, 10);
        a.Hand.TryAddAuto(MakeCard(repo, BeltId, CardTier.Diamond));
        b.Hand.TryAddAuto(MakeCard(repo, BanuLeafCloakId, CardTier.Diamond));
        AuraEngine.RecalculateAuras(a, b, RuleScope.InCombat, 1, 1, repo);
        double abHp = a.GetAttribute("HealthMax");

        // 反转双方顺序重算，结果必须完全一致
        var a2 = new PlayerState("a2", "A", HeroNames.Pygmalien, 1000, 20, 10, 10);
        var b2 = new PlayerState("b2", "B", HeroNames.Mak, 1000, 20, 10, 10);
        a2.Hand.TryAddAuto(MakeCard(repo, BeltId, CardTier.Diamond));
        b2.Hand.TryAddAuto(MakeCard(repo, BanuLeafCloakId, CardTier.Diamond));
        AuraEngine.RecalculateAuras(b2, a2, RuleScope.InCombat, 1, 1, repo);
        double baHp = a2.GetAttribute("HealthMax");
        Assert.Equal(abHp, baHp, 6);
    }

    // ════════════════════════════════════════════════════════════════════════
    // P0-05：同次施放共享 1 次暴击 Roll、单次 TTriggerOnCardCritted 及玩家 CritChance
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_05_MultiActionCast_SharesSingleCritRoll_AndDispatchesOnCardCrittedOnce()
    {
        var repo = SharedRepo.Value;

        // 1. 100% 暴击率下，单次施放含 4 个可暴击动作（Damage + Shield + Burn + Heal）仅派发 1 次 TTriggerOnCardCritted
        CardInstance MakeMultiActionCard(double critChance) => new(CreateSyntheticItem(
            "test_multi_crit_item",
            "Multi Action Weapon",
            new()
            {
                ["CooldownMax"] = 1000,
                ["DamageAmount"] = 10,
                ["ShieldApplyAmount"] = 10,
                ["BurnApplyAmount"] = 10,
                ["HealAmount"] = 10,
                ["CritChance"] = critChance
            },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionAnd","Actions":[
                            {"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
                            {"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
                        ]}
                        """)
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionAnd","Actions":[
                            {"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
                            {"$type":"TActionPlayerHeal","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
                        ]}
                        """)
                }
            }));

        CardInstance MakeCritCounterCard() => new(CreateSyntheticItem(
            "test_crit_counter",
            "Crit Counter",
            new() { ["Custom_0"] = 0 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""
                        {"$type":"TTriggerOnCardCritted",
                         "Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}
                        """),
                    Action = ParseNode("""
                        {"$type":"TActionCardModifyAttribute",
                         "AttributeType":"Custom_0","Operation":"Add",
                         "Value":{"$type":"TFixedValue","Value":1},
                         "Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                }
            }));

        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 1000, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 15, 20, 0, 10); // 一发暴击(20)直接结束
        var multi100 = MakeMultiActionCard(100);
        var counter = MakeCritCounterCard();
        p1.Hand.TryAddAuto(multi100);
        p1.Hand.TryAddAuto(counter);

        var res100 = CombatSimulator.Simulate(repo, p1, p2, seed: 100, recordEvents: true);
        // 单次开火 4 个动作均暴击（值均为 20），但 TTriggerOnCardCritted 严格只触发 1 次（修复前会触发 4 次）
        var critCounterEvents = res100.Events.Where(e =>
            e.Type == CombatEventType.CardAttributeModified
            && e.Detail == "Custom_0"
            && e.TargetCardInstanceId == counter.InstanceId).ToList();
        Assert.Single(critCounterEvents);
        Assert.Equal(1, critCounterEvents[0].Value);
        var dmgEv = res100.Events.First(e => e.Type == CombatEventType.DamageApplied);
        var shEv = res100.Events.First(e => e.Type == CombatEventType.ShieldApplied);
        var burnEv = res100.Events.First(e => e.Type == CombatEventType.BurnApplied);
        var healEv = res100.Events.First(e => e.Type == CombatEventType.HealApplied && e.Detail != "RegenGain");
        Assert.Equal(20, dmgEv.Value);
        Assert.Equal(20, shEv.Value);
        Assert.Equal(20, burnEv.Value);
        Assert.Equal(20, healEv.Value);

        // 2. 50% 暴击率跨 40 个随机种子：每次施放的 4 个动作要么全部暴击(20)，要么全部未暴击(10)，绝不出现撕裂暴击
        int critSeeds = 0, nonCritSeeds = 0;
        for (int seed = 1; seed <= 40; seed++)
        {
            var sp1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
            var sp2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 5, 20, 0, 10); // 第 1 发即结束
            sp1.Hand.TryAddAuto(MakeMultiActionCard(50));
            var r = CombatSimulator.Simulate(repo, sp1, sp2, seed: seed, recordEvents: true);
            double d = r.Events.First(e => e.Type == CombatEventType.DamageApplied).Value;
            double s = r.Events.First(e => e.Type == CombatEventType.ShieldApplied).Value;
            double b = r.Events.First(e => e.Type == CombatEventType.BurnApplied).Value;
            double h = r.Events.First(e => e.Type == CombatEventType.HealApplied && e.Detail != "RegenGain").Value;
            Assert.Equal(d, s);
            Assert.Equal(d, b);
            Assert.Equal(d, h);
            if (d >= 20) critSeeds++; else nonCritSeeds++;
        }
        Assert.True(critSeeds > 0 && nonCritSeeds > 0, $"50% 暴击率 40 种子应既有暴击又有非暴击 (crit={critSeeds}, nonCrit={nonCritSeeds})");
    }

    [Fact]
    public void P0_05_PlayerCritChanceAura_WildStrength_AppliesToRollCrit()
    {
        var repo = SharedRepo.Value;
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 15, 20, 0, 10);

        // 挂载 20 个 Wild Strength（每个通过 TAuraActionPlayerModifyAttribute 给玩家 +5 CritChance => 合计 100%）
        for (int i = 0; i < 20; i++)
            p1.Skills.Add(MakeCard(repo, WildStrengthId, CardTier.Diamond));

        // 0% 自身暴击率武器
        var zeroCritWeapon = new CardInstance(CreateSyntheticItem(
            "test_zero_crit_weapon",
            "Zero Crit Sword",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 10, ["CritChance"] = 0 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            }));
        p1.Hand.TryAddAuto(zeroCritWeapon);

        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 1, 1, repo);
        Assert.Equal(100, p1.GetAttribute("CritChance"));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 42, recordEvents: true);
        var dmg = res.Events.First(e => e.Type == CombatEventType.DamageApplied);
        Assert.True(dmg.IsCrit, "RollCrit 应叠加玩家级 CritChance（Wild Strength）");
        Assert.Equal(20, dmg.Value);
    }

    // ════════════════════════════════════════════════════════════════════════
    // P0-06：TReferenceValueCardAttribute/Unscaled Target == null 逐目标与 Modifier.Value 穿透
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_06_EagleSigil_OnEnraged_AddsEachTargetCardsOwnCritChance()
    {
        var repo = SharedRepo.Value;
        var p1 = new PlayerState("p1", "Karnok", HeroNames.Karnok, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "Dummy", HeroNames.Vanessa, 5000, 20, 0, 10);

        var eagleSigil = MakeCard(repo, EagleSigilId, CardTier.Bronze);
        var weaponA = new CardInstance(CreateSyntheticItem(
            "test_eagle_w1", "Weapon A",
            new() { ["DamageAmount"] = 10, ["CritChance"] = 20 },
            tags: ["Weapon"]));
        var weaponB = new CardInstance(CreateSyntheticItem(
            "test_eagle_w2", "Weapon B",
            new() { ["DamageAmount"] = 10, ["CritChance"] = 65 },
            tags: ["Weapon"]));
        p1.Hand.TryAddAuto(eagleSigil);
        p1.Hand.TryAddAuto(weaponA);
        p1.Hand.TryAddAuto(weaponB);

        // 直接执行 Eagle Sigil 的 Ability "1"（TTriggerOnPlayerEnraged -> Target=SelfHand, Value=Target==null CritChance）
        var ab1 = eagleSigil.ActiveAbilities.Single(a => a.Id == "1");
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, p2, new Random(1))
        {
            SelfCard = eagleSigil,
            TriggerSourceCard = eagleSigil
        };
        AbilityExecutor.ExecuteAction(ab1.Action, eagleSigil, ctx);

        Assert.Equal(30, weaponA.GetAttribute("DamageAmount")); // 10 + 20（修复前归零保持 10）
        Assert.Equal(75, weaponB.GetAttribute("DamageAmount")); // 10 + 65（修复前归零保持 10）
    }

    [Fact]
    public void P0_06_Sunderer_ReducesEnemyHighestShieldItemByTenPercent()
    {
        var repo = SharedRepo.Value;
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Pygmalien, 500, 20, 10, 10);

        var sunderer = MakeCard(repo, SundererId, CardTier.Silver);
        var enemyShield = new CardInstance(CreateSyntheticItem(
            "test_enemy_shield", "Big Shield",
            new() { ["ShieldApplyAmount"] = 100 },
            hiddenTags: ["Shield"]));
        p1.Hand.TryAddAuto(sunderer);
        p2.Hand.TryAddAuto(enemyShield);

        var ab1 = sunderer.ActiveAbilities.Single(a => a.Id == "1");
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, p2, new Random(1))
        {
            SelfCard = sunderer,
            TriggerSourceCard = sunderer
        };
        AbilityExecutor.ExecuteAction(ab1.Action, sunderer, ctx);

        // Sunderer Ability 1 读取目标卡 Unscaled ShieldApplyAmount(100) * 0.1 = 10 并 Subtract
        Assert.Equal(90, enemyShield.GetAttribute("ShieldApplyAmount"));
    }

    [Fact]
    public void P0_06_AbsorbMemories_AuraModifierNullTarget_ReadsTargetCustom1()
    {
        var repo = SharedRepo.Value;
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        p1.Attributes["Custom_5"] = 50;

        var absorb = MakeCard(repo, AbsorbMemoriesId);
        // 显式激活 Absorb Memories 的 Aura "9"（Value = Player.Custom_5 - Target(null).Custom_1）
        if (absorb.Template.Auras.TryGetValue("9", out var aura9) && !absorb.ActiveAuras.Contains(aura9))
            absorb.ActiveAuras.Add(aura9);

        var langxian = MakeCard(repo, LangxianId, CardTier.Bronze);
        langxian.PermanentAdd["Custom_1"] = 12;

        p1.Hand.TryAddAuto(absorb);
        p1.Hand.TryAddAuto(langxian);

        AuraEngine.RecalculateAuras(p1, null, RuleScope.OutOfCombat, 1, 1, repo);

        // 50 - 12 = 38（修复前 Modifier.Value 内 Target==null 归零导致 50 - 0 = 50）
        Assert.Equal(38, langxian.GetAttribute("Custom_2"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // P0-07：战斗动态光环依赖掩码与 7 类状态变动点即时重算
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_07_DynamicAuras_ShieldAndSiblingCardFired_ImmediateRecalc()
    {
        var repo = SharedRepo.Value;
        var p1 = new PlayerState("p1", "P1", HeroNames.Pygmalien, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 200, 20, 0, 10);

        // 同一张卡的 2 条兄弟 TTriggerOnCardFired 能力：Ability 0 先加 50 盾，Ability 1 造成等于 DamageAmount 的伤害；
        // 光环动态将自身 DamageAmount 映射为当前 Player.Shield。修复前 Ability 1 会打出 0 伤，修复后应立刻打出 50 伤！
        var shieldBash = new CardInstance(CreateSyntheticItem(
            "test_shield_bash",
            "Shield Bash",
            new() { ["CooldownMax"] = 1000, ["ShieldApplyAmount"] = 50, ["DamageAmount"] = 0 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Priority = "High",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Priority = "Medium",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            },
            auras: new()
            {
                ["0"] = new AuraDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Action = ParseNode("""
                        {"$type":"TAuraActionCardModifyAttribute",
                         "AttributeType":"DamageAmount","Operation":"Add",
                         "Value":{"$type":"TReferenceValuePlayerAttribute","AttributeType":"Shield","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                }
            }));
        p1.Hand.TryAddAuto(shieldBash);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 21, recordEvents: true);
        var firstDmg = res.Events.First(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "p1");
        Assert.Equal(50, firstDmg.Value);
    }

    [Fact]
    public void P0_07_DynamicAuras_LastAmmoDecrement_UpdatesBeforeExecuteCardCast()
    {
        var repo = SharedRepo.Value;
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 200, 20, 0, 10);

        // 1 发弹药手枪：光环规则“当自身 Ammo == 0 时 +100 伤害”。
        // FireCard 扣减最后一发弹药（1->0）后、ExecuteCardCast 前必须刷新光环，使该发子弹即刻打出 10 + 100 = 110 伤害！
        var lastShotPistol = new CardInstance(CreateSyntheticItem(
            "test_last_shot",
            "Last Shot Pistol",
            new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1, ["DamageAmount"] = 10 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            },
            auras: new()
            {
                ["0"] = new AuraDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Prerequisites =
                    [
                        ParseNode("""
                            {"$type":"TPrerequisiteCardCount","Comparison":"GreaterThanOrEqual","Amount":1,
                             "Subject":{"$type":"TTargetCardSelf","Conditions":{"$type":"TCardConditionalAttribute","Attribute":"Ammo","ComparisonOperator":"Equal","ComparisonValue":{"$type":"TFixedValue","Value":0}}}}
                            """)
                    ],
                    Action = ParseNode("""
                        {"$type":"TAuraActionCardModifyAttribute",
                         "AttributeType":"DamageAmount","Operation":"Add",
                         "Value":{"$type":"TFixedValue","Value":100},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                }
            }));
        p1.Hand.TryAddAuto(lastShotPistol);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 22, recordEvents: true);
        var firstDmg = res.Events.First(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "p1");
        Assert.Equal(110, firstDmg.Value);
    }

    [Fact]
    public void P0_07_DynamicAuras_HealthBurnPoisonAndCardStatusExpiry_RecalculateImmediately()
    {
        var repo = SharedRepo.Value;

        // A. 验证 CardStatus（Haste 施加与到期归零）动态光环即时刷新：
        // 物品开战获 200ms Haste，光环“当自身 Haste > 0 时 +50 伤害”；物品 1000ms 开火时 Haste 早已归零，伤害应回落至基础 10（而非滞留在 60）！
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 200, 20, 0, 10);
        var hasteWatcher = new CardInstance(CreateSyntheticItem(
            "test_haste_aura_expiry",
            "Haste Watcher",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 10 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionCardHaste",
                         "ReferenceValue":{"$type":"TFixedValue","Value":200},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            },
            auras: new()
            {
                ["0"] = new AuraDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Prerequisites =
                    [
                        ParseNode("""
                            {"$type":"TPrerequisiteCardCount","Comparison":"GreaterThanOrEqual","Amount":1,
                             "Subject":{"$type":"TTargetCardSelf","Conditions":{"$type":"TCardConditionalAttribute","Attribute":"Haste","ComparisonOperator":"GreaterThan","ComparisonValue":{"$type":"TFixedValue","Value":0}}}}
                            """)
                    ],
                    Action = ParseNode("""
                        {"$type":"TAuraActionCardModifyAttribute",
                         "AttributeType":"DamageAmount","Operation":"Add",
                         "Value":{"$type":"TFixedValue","Value":50},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                }
            }));
        p1.Hand.TryAddAuto(hasteWatcher);

        var hasteRes = CombatSimulator.Simulate(repo, p1, p2, seed: 23, recordTimeline: true, recordEvents: true);
        // t=0ms 获得 200ms Haste 后光环立刻生效 -> DamageAmount == 60
        var t0Card = hasteRes.Timeline[0].Cards.Single(c => c.InstanceId == hasteWatcher.InstanceId);
        Assert.Equal(60, t0Card.DamageAmount);
        // 200ms 到期归零后光环立刻刷新回落 -> 首次开火（~800ms）实际造成基础 10 伤
        var dmgAfterHaste = hasteRes.Events.First(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "p1");
        Assert.Equal(10, dmgAfterHaste.Value);

        // B. 验证 Burn/Poison 施加与 TickBurn 衰减归零后动态光环即时刷新：
        // 开战给敌方上 1 点 Burn，500ms TickBurn 后 Burn 衰减为 0；我方武器 1000ms 开火，光环“敌方 Burn > 0 时 +40 伤害”。
        // t=0ms 应为 50 伤，500ms TickBurn 衰减至 0 后，1000ms 开火应立即回落至 10 伤！
        var bp1 = new PlayerState("bp1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var bp2 = new PlayerState("bp2", "P2", HeroNames.Vanessa, 200, 20, 0, 10);
        var burnCondWeapon = new CardInstance(CreateSyntheticItem(
            "test_burn_tick_aura",
            "Burn Conditional",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 10 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionPlayerBurnApply",
                         "ReferenceValue":{"$type":"TFixedValue","Value":1},
                         "Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}
                        """)
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            },
            auras: new()
            {
                ["0"] = new AuraDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Prerequisites =
                    [
                        ParseNode("""
                            {"$type":"TPrerequisitePlayer",
                             "Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent",
                                 "Conditions":{"$type":"TPlayerConditionalAttribute","Attribute":"Burn","ComparisonOperator":"GreaterThan","ComparisonValue":{"$type":"TFixedValue","Value":0}}}}
                            """)
                    ],
                    Action = ParseNode("""
                        {"$type":"TAuraActionCardModifyAttribute",
                         "AttributeType":"DamageAmount","Operation":"Add",
                         "Value":{"$type":"TFixedValue","Value":40},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                }
            }));
        bp1.Hand.TryAddAuto(burnCondWeapon);

        var burnRes = CombatSimulator.Simulate(repo, bp1, bp2, seed: 24, recordTimeline: true, recordEvents: true);
        Assert.Equal(50, burnRes.Timeline[0].Cards.Single(c => c.InstanceId == burnCondWeapon.InstanceId).DamageAmount);
        var burnCondDmg = burnRes.Events.First(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "bp1");
        Assert.Equal(10, burnCondDmg.Value);

        // C. 验证 ApplyDirectDamage 血量变动（HP < 100）立即触发血量阈值光环
        var hp1 = new PlayerState("hp1", "P1", HeroNames.Vanessa, 120, 20, 10, 10);
        var hp2 = new PlayerState("hp2", "P2", HeroNames.Vanessa, 300, 20, 0, 10);
        // 对手 500ms 打出 40 伤（hp1 跌到 80 < 100，触发光环 +90 伤），hp1 1000ms 反击应立刻打出 10 + 90 = 100 伤
        hp1.Hand.TryAddAuto(new CardInstance(CreateSyntheticItem(
            "test_low_hp_aura",
            "Low HP Berserker",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 10 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            },
            auras: new()
            {
                ["0"] = new AuraDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Prerequisites =
                    [
                        ParseNode("""
                            {"$type":"TPrerequisitePlayer",
                             "Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self",
                                 "Conditions":{"$type":"TPlayerConditionalAttribute","Attribute":"Health","ComparisonOperator":"LessThan","ComparisonValue":{"$type":"TFixedValue","Value":100}}}}
                            """)
                    ],
                    Action = ParseNode("""
                        {"$type":"TAuraActionCardModifyAttribute",
                         "AttributeType":"DamageAmount","Operation":"Add",
                         "Value":{"$type":"TFixedValue","Value":90},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                }
            })));
        hp2.Hand.TryAddAuto(new CardInstance(CreateSyntheticItem(
            "test_fast_poke",
            "Fast Poke",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 40 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            })));

        var hpRes = CombatSimulator.Simulate(repo, hp1, hp2, seed: 25, recordEvents: true);
        var counterStrike = hpRes.Events.First(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "hp1");
        Assert.Equal(100, counterStrike.Value);
    }

    [Fact]
    public void P0_07_DynamicAuras_HealCleanseRegenSandstormAndTempo_RecalculateImmediately()
    {
        var repo = SharedRepo.Value;

        // 1. Heal 净化 Poison 归零 + Regen 增益实时刷新光环：
        // p1 开战获 1 Poison 与 10 Regen，随后开战 1 次 20 治疗（净化 1 Poison -> 0）；
        // 物品光环：若自身 Poison > 0 则 +100 伤害（净化后应立刻清零不再加伤），同时 DamageAmount += 自身 HealthRegen（应立刻 +10）！
        var p1 = new PlayerState("p1", "P1", HeroNames.Mak, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 200, 20, 0, 10);
        p1.Hand.TryAddAuto(new CardInstance(CreateSyntheticItem(
            "test_heal_regen_aura",
            "Purify Regen Blade",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 10, ["RegenApplyAmount"] = 10, ["HealAmount"] = 20 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0", ActiveIn = "HandOnly", Priority = "Highest",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionAnd","Actions":[
                            {"$type":"TActionPlayerPoisonApply","ReferenceValue":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                            {"$type":"TActionPlayerRegenApply","Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                            {"$type":"TActionPlayerHeal","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
                        ]}
                        """)
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1", ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            },
            auras: new()
            {
                ["0"] = new AuraDefinition
                {
                    Id = "0", ActiveIn = "HandOnly",
                    Prerequisites =
                    [
                        ParseNode("""
                            {"$type":"TPrerequisitePlayer",
                             "Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self",
                                 "Conditions":{"$type":"TPlayerConditionalAttribute","Attribute":"Poison","ComparisonOperator":"GreaterThan","ComparisonValue":{"$type":"TFixedValue","Value":0}}}}
                            """)
                    ],
                    Action = ParseNode("""{"$type":"TAuraActionCardModifyAttribute","AttributeType":"DamageAmount","Operation":"Add","Value":{"$type":"TFixedValue","Value":100},"Target":{"$type":"TTargetCardSelf"}}""")
                },
                ["1"] = new AuraDefinition
                {
                    Id = "1", ActiveIn = "HandOnly",
                    Action = ParseNode("""
                        {"$type":"TAuraActionCardModifyAttribute","AttributeType":"DamageAmount","Operation":"Add",
                         "Value":{"$type":"TReferenceValuePlayerAttribute","AttributeType":"HealthRegen","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                }
            })));

        var hrRes = CombatSimulator.Simulate(repo, p1, p2, seed: 26, recordEvents: true);
        var hrDmg = hrRes.Events.First(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "p1");
        // Poison 已被治疗净化清零（+0），HealthRegen=10（+10），总计 10 + 10 = 20
        Assert.Equal(20, hrDmg.Value);

        // 2. TickPassiveTempo（2000ms 自动获 +1 Tempo）实时刷新 Tempo 依赖光环：
        // 武器冷却 2500ms 且携带 HiddenTag("Tempo")，光环将自身 DamageAmount += Player.Tempo * 50；
        // 2000ms 时 TickPassiveTempo 使 Tempo 从 0 -> 1，2500ms 开火必须打出 10 + 50 = 60 伤！
        var tp1 = new PlayerState("tp1", "Hero8", HeroNames.Common, 500, 20, 10, 10);
        var tp2 = new PlayerState("tp2", "Dummy", HeroNames.Vanessa, 200, 20, 0, 10);
        tp1.Hand.TryAddAuto(new CardInstance(CreateSyntheticItem(
            "test_tempo_aura",
            "Tempo Resonator",
            new() { ["CooldownMax"] = 2500, ["DamageAmount"] = 10 },
            hiddenTags: ["Tempo"],
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0", ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            },
            auras: new()
            {
                ["0"] = new AuraDefinition
                {
                    Id = "0", ActiveIn = "HandOnly",
                    Action = ParseNode("""
                        {"$type":"TAuraActionCardModifyAttribute","AttributeType":"DamageAmount","Operation":"Add",
                         "Value":{"$type":"TReferenceValuePlayerAttribute","AttributeType":"Tempo","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"},
                                  "Modifier":{"ModifyMode":"Multiply","Value":{"$type":"TFixedValue","Value":50},"ShouldRound":true}},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                }
            })));

        var tempoRes = CombatSimulator.Simulate(repo, tp1, tp2, seed: 27, recordEvents: true);
        var tempoDmg = tempoRes.Events.First(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "tp1");
        Assert.Equal(60, tempoDmg.Value);

        // 3. Sandstorm 破盾实时刷新护盾条件光环：
        // Sandstorm 从 t=0 启动（SandstormStartOffsetMs=-30000），p1 开战 1 盾，首跳沙暴（t=0ms）打穿 1 盾 -> Shield==0；
        // 光环“当自身 Shield > 0 时 +80 伤害”，1000ms 开火必须回落至 10 伤而非滞留 90 伤！
        var sp1 = new PlayerState("sp1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var sp2 = new PlayerState("sp2", "P2", HeroNames.Vanessa, 500, 20, 10, 10);
        sp1.Hand.TryAddAuto(new CardInstance(CreateSyntheticItem(
            "test_sandstorm_shield_aura",
            "Sandstorm Shield Watch",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 10, ["ShieldApplyAmount"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0", ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1", ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            },
            auras: new()
            {
                ["0"] = new AuraDefinition
                {
                    Id = "0", ActiveIn = "HandOnly",
                    Prerequisites =
                    [
                        ParseNode("""
                            {"$type":"TPrerequisitePlayer",
                             "Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self",
                                 "Conditions":{"$type":"TPlayerConditionalAttribute","Attribute":"Shield","ComparisonOperator":"GreaterThan","ComparisonValue":{"$type":"TFixedValue","Value":0}}}}
                            """)
                    ],
                    Action = ParseNode("""{"$type":"TAuraActionCardModifyAttribute","AttributeType":"DamageAmount","Operation":"Add","Value":{"$type":"TFixedValue","Value":80},"Target":{"$type":"TTargetCardSelf"}}""")
                }
            })));

        var sandRes = CombatSimulator.Simulate(
            repo, sp1, sp2, seed: 28, combatType: "PVP",
            recordTimeline: false, recordEvents: true, syncPermanentChanges: false,
            modifiers: new CombatModifiers { SandstormStartOffsetMs = -30_000 });
        var sandDmg = sandRes.Events.First(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "sp1");
        Assert.Equal(10, sandDmg.Value);
    }

    // ════════════════════════════════════════════════════════════════════════
    // P0-08：CombatZeroMultCount 零乘数引用计数与 Trail Markers 到期无损恢复
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_08_TrailMarkers_TimedChargeAmountMultiplyZero_RestoresLosslesslyAfterDuration()
    {
        var repo = SharedRepo.Value;

        // Gold Trail Markers：开战后 500ms 敌方 ChargeAmount Multiply 0.0，到期后必须恢复原 ChargeAmount（且不破坏叠加的 CombatMult）
        var p1 = new PlayerState("p1", "Karnok", HeroNames.Karnok, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "Target", HeroNames.Dooley, 500, 20, 10, 10);

        var trailMarkers = MakeCard(repo, TrailMarkersId, CardTier.Gold);
        p1.Hand.TryAddAuto(trailMarkers);

        // 敌方充能卡：基础 ChargeAmount = 1000，开战额外再挂一个 Multiply 2.0（理论值 2000），每 1000ms 给自己充能并输出伤害
        var charger = new CardInstance(CreateSyntheticItem(
            "test_charger_card",
            "Battery Charger",
            new() { ["CooldownMax"] = 1000, ["ChargeAmount"] = 1000, ["DamageAmount"] = 100 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionCardModifyAttribute",
                         "AttributeType":"ChargeAmount","Operation":"Multiply",
                         "Value":{"$type":"TFixedValue","Value":2.0},
                         "Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},
                         "Target":{"$type":"TTargetCardSelf"}}
                        """)
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                        {"$type":"TActionAnd","Actions":[
                            {"$type":"TActionCardCharge","Target":{"$type":"TTargetCardSelf"}},
                            {"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}
                        ]}
                        """)
                }
            }));
        p2.Hand.TryAddAuto(charger);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 31, recordEvents: true);

        // 验证 Trail Markers 的 ChargeAmount Multiply 0.0 生效后，精确在 applyTimeMs + 500ms 触发 TCombatDurationExpired，
        // 且 ChargeAmount 从 0 精确恢复为 2000（而非永久锁死在 0）
        var applyEv = res.Events.FirstOrDefault(e =>
            e.Type == CombatEventType.CardAttributeModified
            && e.ActionType == "TActionCardModifyAttribute"
            && e.Detail == "ChargeAmount"
            && e.TargetCardInstanceId == charger.InstanceId
            && e.Value < 0);
        var expiryEv = res.Events.FirstOrDefault(e =>
            e.Type == CombatEventType.CardAttributeModified
            && e.ActionType == "TCombatDurationExpired"
            && e.Detail == "ChargeAmount"
            && e.TargetCardInstanceId == charger.InstanceId);
        Assert.NotNull(applyEv);
        Assert.NotNull(expiryEv);
        Assert.Equal(applyEv!.TimeMs + 500, expiryEv!.TimeMs);
        Assert.Equal(2000, expiryEv.Value);

        // 恢复后敌方卡牌后续开火可正常触发 2000ms 充能
        var chargeEv = res.Events.FirstOrDefault(e =>
            e.Type == CombatEventType.ChargeApplied
            && e.TargetCardInstanceId == charger.InstanceId
            && e.TimeMs >= expiryEv.TimeMs);
        Assert.NotNull(chargeEv);
        Assert.Equal(2000, chargeEv!.Value);
    }

    [Fact]
    public void P0_08_CardAndPlayer_OverlappingZeroMults_RestoreOriginalMultiplierLosslessly()
    {
        var card = new CardInstance(CreateSyntheticItem(
            "test_zero_mult_card", "Zero Mult Card",
            new() { ["ChargeAmount"] = 500 }));
        card.ModifyAttribute("ChargeAmount", "Multiply", 2.0, DurationTarget.Combat);
        Assert.Equal(1000, card.GetAttribute("ChargeAmount"));

        // 叠加两层限时 Multiply 0.0
        card.ModifyAttribute("ChargeAmount", "Multiply", 0.0, DurationTarget.Combat);
        card.ModifyAttribute("ChargeAmount", "Multiply", 0.0, DurationTarget.Combat);
        Assert.Equal(2, card.CombatZeroMultCount["ChargeAmount"]);
        Assert.Equal(2.0, card.CombatMult["ChargeAmount"]);
        Assert.Equal(0, card.GetAttribute("ChargeAmount"));

        // 撤销第 1 层仍为 0
        card.RevertCombatAttributeModifier("ChargeAmount", "Multiply", 0.0);
        Assert.Equal(1, card.CombatZeroMultCount["ChargeAmount"]);
        Assert.Equal(0, card.GetAttribute("ChargeAmount"));

        // 撤销第 2 层无损恢复为 1000
        card.RevertCombatAttributeModifier("ChargeAmount", "Multiply", 0.0);
        Assert.False(card.CombatZeroMultCount.ContainsKey("ChargeAmount"));
        Assert.Equal(1000, card.GetAttribute("ChargeAmount"));

        // 玩家级同源验证
        var ps = new PlayerState("p", "P", HeroNames.Karnok, 500, 20, 10, 10);
        ps.Attributes["HealthRegen"] = 20;
        ps.ModifyAttribute("HealthRegen", "Multiply", 1.5, DurationTarget.Combat);
        Assert.Equal(30, ps.GetAttribute("HealthRegen"));

        ps.ModifyAttribute("HealthRegen", "Multiply", 0.0, DurationTarget.Combat);
        ps.ModifyAttribute("HealthRegen", "Multiply", 0.0, DurationTarget.Combat);
        Assert.Equal(0, ps.GetAttribute("HealthRegen"));

        ps.RevertCombatAttributeModifier("HealthRegen", "Multiply", 0.0);
        Assert.Equal(0, ps.GetAttribute("HealthRegen"));

        ps.RevertCombatAttributeModifier("HealthRegen", "Multiply", 0.0);
        Assert.Equal(30, ps.GetAttribute("HealthRegen"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // P0-09 / P0-10 Core 日志底座：RegenApplied 枚举 + BurnTick/DamageApplied 精准拆盾字段
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_09_And_P0_10_CoreLogFoundation_RecordsExactShieldAbsorbedAndHpDamage()
    {
        // 验证 P0-09 预留枚举存在
        Assert.True(Enum.IsDefined(typeof(CombatEventType), CombatEventType.RegenApplied));

        var repo = SharedRepo.Value;

        // 场景 1：BurnTick 全盾吸收（对手 100 盾，开战吃 20 Burn -> 500ms 跳 10 伤，ShieldAbsorbed=10, HpDamage=0）
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Pygmalien, 500, 20, 10, 10);
        p1.Hand.TryAddAuto(new CardInstance(CreateSyntheticItem(
            "test_burn_starter", "Burn Starter",
            new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 500 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0", ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":20},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1", ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            })));
        p2.Hand.TryAddAuto(new CardInstance(CreateSyntheticItem(
            "test_shield_starter", "Shield Starter",
            new() { ["ShieldApplyAmount"] = 100 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0", ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            })));

        var fullShieldRes = CombatSimulator.Simulate(repo, p1, p2, seed: 41, recordEvents: true);
        var burnFull = fullShieldRes.Events.First(e => e.Type == CombatEventType.BurnTick && e.TargetPlayerId == "p2");
        Assert.Equal(10, (int)burnFull.Value);
        Assert.Equal(10, burnFull.ShieldAbsorbed);
        Assert.Equal(0, burnFull.HpDamage);

        // 场景 2：BurnTick 部分破盾（对手 4 盾，开战吃 20 Burn -> mitigation=min(4,10)=4, dmg=16 -> ShieldAbsorbed=4, HpDamage=12）
        var bp1 = new PlayerState("bp1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var bp2 = new PlayerState("bp2", "P2", HeroNames.Pygmalien, 500, 20, 10, 10);
        bp1.Hand.TryAddAuto(new CardInstance(CreateSyntheticItem(
            "test_burn_starter2", "Burn Starter 2",
            new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 600 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0", ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":20},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1", ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            })));
        bp2.Hand.TryAddAuto(new CardInstance(CreateSyntheticItem(
            "test_small_shield", "Small Shield",
            new() { ["ShieldApplyAmount"] = 4 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0", ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            })));

        var partialRes = CombatSimulator.Simulate(repo, bp1, bp2, seed: 42, recordEvents: true);
        var burnPartial = partialRes.Events.First(e => e.Type == CombatEventType.BurnTick && e.TargetPlayerId == "bp2");
        Assert.Equal(16, (int)burnPartial.Value);
        Assert.Equal(4, burnPartial.ShieldAbsorbed);
        Assert.Equal(12, burnPartial.HpDamage);
        Assert.Equal((int)burnPartial.Value, burnPartial.ShieldAbsorbed + burnPartial.HpDamage);

        // 同时验证普通 DamageApplied 也记录精准 ShieldAbsorbed + HpDamage
        var directDmg = fullShieldRes.Events.First(e => e.Type == CombatEventType.DamageApplied && e.TargetPlayerId == "p2");
        Assert.True(directDmg.ShieldAbsorbed > 0);
        Assert.True(directDmg.HpDamage > 0);
        Assert.Equal((int)directDmg.Value, directDmg.ShieldAbsorbed + directDmg.HpDamage);
    }
}
