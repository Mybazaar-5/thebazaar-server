using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// B站 50 问 A 组（Q1–Q24、Q44、Q48–Q50）核心战斗时序、状态附魔、卡牌联动与英雄机制验证测试集。
/// </summary>
public sealed class V12BiliCombatStatusParityTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());

    private const string AbductedCowId = "03f90f0b-088f-453d-bbf7-ebb5e777afe2";
    private const string JerkyId = "bf480fe7-530e-4a33-b494-1cb9a841f996";
    private const string PhotoBoothId = "df3ab573-d900-43fc-8766-21fbd43b8021";
    private const string BeastCallId = "84f2a5ad-fc48-4763-bfc8-8919a9369f9c";
    private const string LangxianId = "53099361-599f-4c1a-ad4a-d2fbb491b756";
    private const string ColossalClawId = "6840ec1d-f9fc-443c-a7f8-4b9e6cbf0313";
    private const string ChickenCannonId = "d5dd20ef-31ad-4eb7-af8d-3a42eb3008d3";
    private const string MechaDragonId = "894657f8-ab1e-4acf-b52e-538559f00d37";
    private const string DiamondFangsId = "20db8a31-f74d-40d2-a5a5-db28e3261bea";
    private const string BunkerId = "e923019b-fd40-487c-85af-edaa930095b8";
    private const string ForceFieldId = "85f59c88-c391-4a03-8c3b-f312518e81de";
    private const string ArmoredCoreId = "88ca9279-a62c-4bdb-ba2c-06871a7c5b7d";
    private const string IceSwanId = "fdd78910-3e5c-4244-8c13-ac90a223fae3";
    private const string SignalBoosterId = "0b58c58a-0701-44a2-a9b6-faf45b82ea96";
    private const string WaterskinId = "3c9438a9-78a6-4cb3-b1d8-a6410693a879";
    private const string CoffeeId = "833b205c-b5a6-4095-b816-a8df156ef7fa";
    private const string MaracasId = "b469d6e0-2283-4a17-bda9-cc1955791a77";
    private const string NeuralToxinId = "9bdc7dad-5016-4743-9b44-1e20b03a1961";
    private const string SkyspiritAshesId = "94e3d802-62f5-4f39-9c0e-4818ba5f8c9e";
    private const string NullfrostAltarId = "1ae7a7ae-da06-484e-8454-d7a65eb31ff9";
    private const string TheCoreId = "eeeb02b5-9e8d-4af8-9902-2a77b1f838e7";
    private const string LauncherCoreId = "9fafc0a5-6675-4e17-9457-590f13354a39";
    private const string Printer3DId = "723880db-8a97-4833-a9a1-80c75cde4c17";
    private const string CoreInitId = "1596d1df-e943-48b4-bfc6-df2edb9dfe55";
    private const string FocusedCoreId = "bfa28eec-84de-41cb-bc5c-8de700a5d21f";
    private const string PrimalCoreId = "55a80f10-8dac-42d8-9a29-ce1858c3792a";
    private const string HarmadilloId = "6b8ca1d0-f4ff-440d-8a9e-51efc4d7ce01";
    private const string BolasId = "b89ae3b6-6383-47a3-94fe-2571d30a564a";
    private const string FireClawId = "e3070870-881c-497d-879f-2d91e962e0e2";
    private const string TortugaId = "f8a38ad1-5e5a-4c95-9bd1-55c81c31b117";
    private const string MusketId = "5e7abe19-0087-4bb1-a8bf-617706cc67c9";
    private const string CannonId = "c74239b9-563c-4672-b664-9c9f22329f19";
    private const string SatchelId = "3ce7312c-aa95-4711-9a2b-827609a380b4";
    private const string TemporalStrikeId = "1e63e9bd-603a-4965-a6e8-4e180377c0ff";
    private const string BaseTempoEffectId = "e8478722-1851-4b05-9749-1480352ff710";
    private const string BaseRageEffectId = "4472da8a-26a3-4e10-bd9a-e93c2e22f19c";

    private static PlayerState CreatePlayer(string id, string hero, int hp = 3000)
        => new(id, id, hero, initialHealthMax: hp, initialPrestige: 20, initialGold: 10, unlockedHandSlots: 10);

    private static RuleNode Fixed(double v)
        => new() { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement(v) };

    private static JsonElement FixedRaw(double v)
        => JsonDocument.Parse($"{{\"$type\":\"TFixedValue\",\"Value\":{v.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}").RootElement.Clone();

    private static JsonElement CondById(string cardTemplateId)
        => JsonDocument.Parse($"{{\"$type\":\"TCardConditionalId\",\"Ids\":[\"{cardTemplateId}\"]}}").RootElement.Clone();

    private static CardInstance MakeSyntheticItem(
        string id,
        int cooldownMs,
        RuleNode? action = null,
        string triggerType = "TTriggerOnCardFired",
        string size = "Small",
        List<string>? tags = null,
        string? enchantment = null,
        Dictionary<string, double>? extraAttrs = null,
        string? priority = null)
    {
        var attrs = new Dictionary<string, double>();
        if (cooldownMs > 0) attrs["CooldownMax"] = cooldownMs;
        if (extraAttrs != null)
        {
            foreach (var (k, v) in extraAttrs)
                attrs[k] = v;
        }

        var abilities = new Dictionary<string, AbilityDefinition>();
        if (action != null)
        {
            abilities["0"] = new AbilityDefinition
            {
                Id = "0",
                Trigger = new RuleNode { Type = triggerType },
                Action = action,
                Priority = priority ?? "Medium"
            };
        }

        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = "Item",
            Size = size,
            StartingTier = "Bronze",
            Tags = tags ?? [],
            Attributes = attrs,
            Abilities = abilities
        };
        return new CardInstance(tpl, CardTier.Bronze, enchantment, instanceId: id);
    }

    private static CardTemplate FindByPrefix(GameDataRepository repo, string prefix)
        => repo.CardsById.First(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Value;

    // =========================================================================
    // Q1：同帧 Destroy vs Repair、Abducted Cow 亡语先于变形且无无限修理充能
    // =========================================================================

    [Fact]
    public void Q01_AbductedCow_DestroyBeforeTransform_And_RepairDoesNotInfiniteLoop()
    {
        var repo = Repo.Value;
        var cowTpl = repo.GetCard(AbductedCowId)!;

        var p1 = CreatePlayer("p1", HeroNames.Dooley, 4000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 4000);

        var cow = new CardInstance(cowTpl, CardTier.Silver); // Silver: ChargeAmount=3000ms -> Ab[2], Ab[3] -> 2x Jerky
        var gun = MakeSyntheticItem(
            "q1_gun",
            cooldownMs: 4000,
            action: new RuleNode
            {
                Type = "TActionPlayerDamage",
                Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" },
                ReferenceValue = Fixed(25)
            });

        // 同卡/全队修理器（健康卡不得重复触发 CardRepaired）
        var repairWatcher = new CardInstance(new CardTemplate
        {
            Id = "q1_repairer",
            InternalName = "q1_repairer",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Attributes = new Dictionary<string, double> { ["CooldownMax"] = 1000, ["RepairTargets"] = 10, ["ChargeAmount"] = 500, ["ChargeTargets"] = 1 },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Priority = "Lowest",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardRepair",
                        Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" }
                    }
                },
                ["1"] = new()
                {
                    Id = "1",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnCardRepaired",
                        Subject = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionCardCharge",
                        Target = new RuleNode { Type = "TTargetCardXMost", TargetSection = "SelfHand", TargetMode = "LeftMostCard" },
                        ReferenceValue = Fixed(500)
                    }
                }
            }
        });

        Assert.True(p1.Hand.TryPlaceOrShift(gun, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(cow, 1));
        Assert.True(p1.Hand.TryPlaceOrShift(repairWatcher, 3));

        // 对手在开战后 500ms 一次性摧毁 Abducted Cow（先通过 1000ms 禁用/摧毁源）
        p2.Hand.TryAddAuto(MakeSyntheticItem(
            "q1_destroyer",
            cooldownMs: 1000,
            action: new RuleNode
            {
                Type = "TActionCardDestroy",
                Target = new RuleNode
                {
                    Type = "TTargetCardRandom",
                    TargetSection = "OpponentHand",
                    RawConditionsElement = CondById(AbductedCowId)
                }
            },
            priority: "Highest"));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 101);

        // 1. Abducted Cow 被摧毁时必须先完成 Ab[2] 充能 3000ms（给 q1_gun 充能，使其显著早于 4000ms 开火）
        var gunFirstFire = res.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == gun.InstanceId);
        Assert.True(gunFirstFire.TimeMs <= 1200, $"Abducted Cow 亡语充能应先于变形结算，使 4s 枪在 ~1000ms 开火，实际 {gunFirstFire.TimeMs}ms");
        Assert.Contains(res.Events, e => e.Type == CombatEventType.CardTransformed && (e.SourceCardInstanceId == cow.InstanceId || (e.Detail?.StartsWith(cow.InstanceId + "->", StringComparison.Ordinal) ?? false)));

        // 2. 不得因修理健康卡或已变形移除卡产生无限充能
        int repairCharges = res.Events.Count(e => e.Type == CombatEventType.ChargeApplied && e.SourceCardInstanceId == repairWatcher.InstanceId);
        Assert.Equal(0, repairCharges);
    }

    // =========================================================================
    // Q2：Photo Booth / Beast Call 的 Haste Latch 触发序、TriggerSource 与多源 Haste 隔离
    // =========================================================================

    [Fact]
    public void Q02_PhotoBoothAndBeastCall_HasteLatch_CorrectTriggerSourceAndMultiSourceIsolation()
    {
        var repo = Repo.Value;
        var boothTpl = repo.GetCard(PhotoBoothId)!;
        var beastCallTpl = repo.GetCard(BeastCallId)!;
        var friendTpl = repo.GetCard(HarmadilloId)!; // Tags: ['Friend', 'Weapon'], CD=9000

        // A. Photo Booth 与友方及其相邻武器布局：[LeftWpn(0), Friend(1), RightWpn(2), PhotoBooth(3)]
        var p1 = CreatePlayer("p1", HeroNames.Hero8, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);

        var leftWpn = MakeSyntheticItem("left_wpn", 10_000, new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }, ReferenceValue = Fixed(5) });
        var friend = new CardInstance(friendTpl, CardTier.Bronze);
        var rightWpn = MakeSyntheticItem("right_wpn", 10_000, new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }, ReferenceValue = Fixed(5) });
        var booth = new CardInstance(boothTpl, CardTier.Silver); // CD=4000, Haste=2000

        Assert.True(p1.Hand.TryPlaceOrShift(leftWpn, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(friend, 1));
        Assert.True(p1.Hand.TryPlaceOrShift(rightWpn, 3));
        Assert.True(p1.Hand.TryPlaceOrShift(booth, 4));

        var resBooth = CombatSimulator.Simulate(repo, p1, p2, seed: 202);
        var boothSecondHastes = resBooth.Events
            .Where(e => e.Type == CombatEventType.HasteApplied && e.SourceCardInstanceId == booth.InstanceId)
            .ToList();

        Assert.Contains(boothSecondHastes, e => e.TargetCardInstanceId == friend.InstanceId);
        // 关键：Ab[1] 必须对 Friend 的相邻卡（leftWpn & rightWpn）施加加速，而不是 Photo Booth 自己或其右侧邻居
        Assert.Contains(boothSecondHastes, e => e.TargetCardInstanceId == leftWpn.InstanceId);
        Assert.Contains(boothSecondHastes, e => e.TargetCardInstanceId == rightWpn.InstanceId);

        // B. Beast Call 同理必须加速友方及其左右邻居
        var pB1 = CreatePlayer("pb1", HeroNames.Karnok, 5000);
        var pB2 = CreatePlayer("pb2", HeroNames.Vanessa, 5000);
        var bLeft = MakeSyntheticItem("b_left", 10_000);
        var bFriend = new CardInstance(friendTpl, CardTier.Bronze);
        var bRight = MakeSyntheticItem("b_right", 10_000);
        var beastCall = new CardInstance(beastCallTpl, CardTier.Silver);
        Assert.True(pB1.Hand.TryPlaceOrShift(bLeft, 0));
        Assert.True(pB1.Hand.TryPlaceOrShift(bFriend, 1));
        Assert.True(pB1.Hand.TryPlaceOrShift(bRight, 3));
        Assert.True(pB1.Hand.TryPlaceOrShift(beastCall, 4));

        var resBeast = CombatSimulator.Simulate(repo, pB1, pB2, seed: 203);
        Assert.Contains(resBeast.Events, e => e.Type == CombatEventType.HasteApplied && e.SourceCardInstanceId == beastCall.InstanceId && e.TargetCardInstanceId == bLeft.InstanceId);
        Assert.Contains(resBeast.Events, e => e.Type == CombatEventType.HasteApplied && e.SourceCardInstanceId == beastCall.InstanceId && e.TargetCardInstanceId == bRight.InstanceId);

        // C. 多源隔离与零友方防残留：Photo Booth 开火时无友方（Ab[0] 空目标），随后由第三方在战中生成/触发对其他友方加速时，Photo Booth 不得盗触发 Custom_8
        var pIso1 = CreatePlayer("piso1", HeroNames.Hero8, 5000);
        var pIso2 = CreatePlayer("piso2", HeroNames.Vanessa, 5000);
        var lonelyBooth = new CardInstance(boothTpl, CardTier.Silver); // 4000ms CD, 无任何 Friend 时空放 Ab[0]
        var otherHaster = MakeSyntheticItem(
            "other_haster",
            5000,
            new RuleNode
            {
                Type = "TActionCardHaste",
                Target = new RuleNode { Type = "TTargetCardPositional", Origin = "Self", TargetMode = "RightCard" },
                RawValueNode = FixedRaw(1000)
            });
        // 用一个不带 Friend 标签的卡当 otherHaster 的伪目标
        var noFriendTarget = MakeSyntheticItem("no_friend_tgt", 10_000, new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }, ReferenceValue = Fixed(1) });
        Assert.True(pIso1.Hand.TryPlaceOrShift(lonelyBooth, 0));
        Assert.True(pIso1.Hand.TryPlaceOrShift(otherHaster, 2));
        Assert.True(pIso1.Hand.TryPlaceOrShift(noFriendTarget, 3));
        var resNoFriend = CombatSimulator.Simulate(repo, pIso1, pIso2, seed: 204);
        // 同时用带两个来源同帧加速（slot 0: leftNeighbor, slot 1: Friend, slot 3: rightNeighbor, slot 4: sameTickHaster(CD=4000), slot 5: PhotoBooth(CD=4000)）验证：
        // sameTickHaster 与 PhotoBooth 同帧加速 Friend 时，Photo Booth 仅围绕自身释放的那次加速邻接 1 次，绝不被同帧他源加速串台多触发
        var pSameTick1 = CreatePlayer("pst1", HeroNames.Hero8, 5000);
        var pSameTick2 = CreatePlayer("pst2", HeroNames.Vanessa, 5000);
        var wLeft = MakeSyntheticItem("st_left", 10_000);
        var stFriend = new CardInstance(friendTpl, CardTier.Bronze);
        var wRight = MakeSyntheticItem("st_right", 10_000);
        var otherSameTickHaster = MakeSyntheticItem(
            "same_tick_haster",
            4000,
            new RuleNode
            {
                Type = "TActionCardHaste",
                Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", RawConditionsElement = CondById(HarmadilloId) },
                RawValueNode = FixedRaw(1000)
            });
        var stBooth = new CardInstance(boothTpl, CardTier.Silver); // CD=4000
        Assert.True(pSameTick1.Hand.TryPlaceOrShift(wLeft, 0));
        Assert.True(pSameTick1.Hand.TryPlaceOrShift(stFriend, 1));
        Assert.True(pSameTick1.Hand.TryPlaceOrShift(wRight, 3));
        Assert.True(pSameTick1.Hand.TryPlaceOrShift(otherSameTickHaster, 4));
        Assert.True(pSameTick1.Hand.TryPlaceOrShift(stBooth, 5));

        var resSameTick = CombatSimulator.Simulate(repo, pSameTick1, pSameTick2, seed: 205);
        // 第 1 轮（t < 5000ms），stBooth 只应自己释放 1 次友方加速 + 各 1 次左右邻接加速（即对 wLeft 至多 1 次、对 wRight 至多 1 次，不被 otherSameTickHaster 引发额外串台）
        int wLeftEarlyHastes = resSameTick.Events.Count(e => e.TimeMs < 5000 && e.Type == CombatEventType.HasteApplied && e.SourceCardInstanceId == stBooth.InstanceId && e.TargetCardInstanceId == wLeft.InstanceId);
        int wRightEarlyHastes = resSameTick.Events.Count(e => e.TimeMs < 5000 && e.Type == CombatEventType.HasteApplied && e.SourceCardInstanceId == stBooth.InstanceId && e.TargetCardInstanceId == wRight.InstanceId);
        Assert.Equal(1, wLeftEarlyHastes);
        Assert.Equal(1, wRightEarlyHastes);
    }

    // =========================================================================
    // Q3 / Q5 / Q7：20ms 步长、250ms ICD、2x 加速、1s CD 下限与沙暴 / 防死循环
    // =========================================================================

    [Fact]
    public void Q03_Q05_Q07_TickIcdCooldownFloorHasteAndSandstormBoundaries()
    {
        var repo = Repo.Value;
        Assert.Equal(50, CombatSimulator.TickMs);
        // 注：按用户需求放开超 2 分钟沙尘暴长局结算上限至 300_000ms（原官方常量为 120_000ms）
        Assert.Equal(300_000, CombatSimulator.MaxDurationMs);
        Assert.Equal(16, CombatSimulator.MaxForceUseDepth);
        Assert.Equal(250, repo.GameMode.CombatSimEffectDelay);

        // 1. 冷却下限 >= 1000ms（即便被 Flat/Percent 减至极低仍不得低于 1000ms）
        var fastCard = MakeSyntheticItem("floor_cd", 2000, extraAttrs: new() { ["FlatCooldownReduction"] = 1800, ["PercentCooldownReduction"] = 80 });
        Assert.Equal(1000, fastCard.GetAttribute("CooldownMax"));

        // 2. 2x 加速 + 50ms 步长 + 20s 沙暴预警 + 30s 沙暴开吹
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);
        var hastedWpn = MakeSyntheticItem("hasted_wpn", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }, ReferenceValue = Fixed(1) });
        hastedWpn.CombatAdd["Haste"] = 0;
        p1.Hand.TryAddAuto(hastedWpn);
        p1.Hand.TryAddAuto(MakeSyntheticItem(
            "t0_haster",
            0,
            new RuleNode
            {
                Type = "TActionCardHaste",
                Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", RawConditionsElement = CondById("hasted_wpn") },
                RawValueNode = FixedRaw(5000)
            },
            triggerType: "TTriggerOnFightStarted"));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 303);
        var firstFire = res.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == hastedWpn.InstanceId);
        // 4000ms 在全程 2x Haste 下，第 40 tick (t=1950ms) 到达 4000ms 触发开火
        Assert.InRange(firstFire.TimeMs, 1950, 2000);

        var warnEv = res.Events.First(e => e.Type == CombatEventType.SandstormWarning);
        var firstSandTick = res.Events.First(e => e.Type == CombatEventType.SandstormTick);
        Assert.Equal(20_000, warnEv.TimeMs);
        Assert.Equal(30_000, firstSandTick.TimeMs);
        Assert.Equal(1, (int)firstSandTick.Value);
    }

    // =========================================================================
    // Q4 / Q6：开战触发顺序（Langxian 不空发）与残余 0.1s 满条同帧冻结拦截
    // =========================================================================

    [Fact]
    public void Q04_Q06_LangxianNoZeroFrameFire_And_SameTickFreezeBlocksNearReadyCard()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Dooley, 5000);

        // Q4: 狼筅开局不得 0 帧误开火（仅在 8000ms 左右自然开火）
        var langxian = new CardInstance(repo.GetCard(LangxianId)!, CardTier.Bronze);
        p1.Hand.TryAddAuto(langxian);

        // Q6: 对手 4000ms 卡，己方在 3900ms（对方仅差 0.1s=100ms 满条）时用技能/物品施加 2000ms 冻结
        var oppWpn = MakeSyntheticItem("opp_near_ready", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }, ReferenceValue = Fixed(50) });
        p2.Hand.TryAddAuto(oppWpn);
        p1.Hand.TryAddAuto(MakeSyntheticItem(
            "freezer_3900",
            3900,
            new RuleNode
            {
                Type = "TActionCardFreeze",
                Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
                RawValueNode = FixedRaw(2000)
            }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 406);
        var langxianFirstFire = res.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == langxian.InstanceId);
        Assert.True(langxianFirstFire.TimeMs >= 7900, $"Langxian 不得在 0 帧误发，实际首次开火={langxianFirstFire.TimeMs}ms");

        var freezeEv = res.Events.First(e => e.Type == CombatEventType.FreezeApplied && e.TargetCardInstanceId == oppWpn.InstanceId);
        var oppFirstFire = res.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == oppWpn.InstanceId);
        // 3900ms 冻结器于 3850ms 触发，冻结 2000ms 后延至 ~5950ms 才首次开火
        Assert.InRange(freezeEv.TimeMs, 3850, 3920);
        Assert.True(oppFirstFire.TimeMs >= 5800, $"差 0.1s 满条时先挂 FreezeMs 必须完整阻断发射至解冻后，实际 {oppFirstFire.TimeMs}ms");
    }

    // =========================================================================
    // Q8：Colossal Claw + Shielded 附魔 & Chicken Cannon（安全快照无死循环）
    // =========================================================================

    [Fact]
    public void Q08_ColossalClaw_ShieldedEnchant_And_ChickenCannon_NoLoopExactValues()
    {
        var repo = Repo.Value;
        var clawTpl = repo.GetCard(ColossalClawId)!;
        var coreTpl = repo.GetCard(ArmoredCoreId)!; // Silver ShieldApplyAmount = 30
        var chickenTpl = repo.GetCard(ChickenCannonId)!;

        var p1 = CreatePlayer("p1", HeroNames.Dooley, 3000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 3000);

        var claw = new CardInstance(clawTpl, CardTier.Diamond, enchantment: "Shielded");
        var core = new CardInstance(coreTpl, CardTier.Silver); // ShieldApplyAmount = 30
        var chicken = new CardInstance(chickenTpl, CardTier.Silver);

        Assert.True(p1.Hand.TryPlaceOrShift(core, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(claw, 2));
        Assert.True(p1.Hand.TryPlaceOrShift(chicken, 5));

        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 1, 1, repo);
        // Colossal Claw 伤害来自其他友方护盾施加总量 (30)，Shielded 附魔镜像同等护盾 (30)
        Assert.Equal(30, (int)claw.GetAttribute("DamageAmount"));
        Assert.Equal(30, (int)claw.GetAttribute("ShieldApplyAmount"));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 808);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == claw.InstanceId && e.Value >= 30);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == claw.InstanceId && e.Value >= 30);
    }

    // =========================================================================
    // Q9：Mecha Dragon + Shiny 附魔正确叠加为 Multicast = 3（绝不覆盖成 2）
    // =========================================================================

    [Fact]
    public void Q09_MechaDragon_WithShinyEnchantment_StacksMulticastTo3()
    {
        var repo = Repo.Value;
        var mechaTpl = repo.GetCard(MechaDragonId)!;
        var mecha = new CardInstance(mechaTpl, CardTier.Diamond, enchantment: "Shiny");

        Assert.Equal(4000, (int)mecha.GetAttribute("CooldownMax"));
        Assert.Equal(3, (int)mecha.GetAttribute("Multicast"));

        var p1 = CreatePlayer("p1", HeroNames.Hero8, 3000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 3000);
        p1.Hand.TryAddAuto(mecha);
        p2.Hand.TryAddAuto(MakeSyntheticItem("dummy_p2", 10_000));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 909);
        var firstWindowFires = res.Events
            .Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == mecha.InstanceId && e.TimeMs <= 4600)
            .ToList();
        Assert.Equal(3, firstWindowFires.Count);
        Assert.Equal([1, 2, 3], firstWindowFires.Select(e => (int)e.Value).ToArray());
    }

    // =========================================================================
    // Q10 / Q23：Tiny 独立于附魔、Diamond Fangs 触发与 Bunker 微缩邻接实时准确
    // =========================================================================

    [Fact]
    public void Q10_Q23_TinyIndependentFromEnchant_DiamondFangs_And_BunkerNeighbors()
    {
        var repo = Repo.Value;
        var bangsTpl = repo.GetCard(DiamondFangsId)!;
        var bunkerTpl = repo.GetCard(BunkerId)!;
        var forceFieldTpl = repo.GetCard(ForceFieldId)!; // Large, Shield, CD=5000
        var coreTpl = repo.GetCard(ArmoredCoreId)!;       // Medium, Shield, CD=7000

        // 1. Tiny 独立于 Enchantment，互不覆盖；Medium+Tiny -> EffectiveSize=Small, SlotWidth=1
        var core = new CardInstance(coreTpl, CardTier.Diamond, enchantment: "Radiant");
        core.PermanentAdd["Tiny"] = 1;
        core.RebuildTierAndEnchantmentData();
        Assert.Equal("Radiant", core.Enchantment);
        Assert.True(core.IsTiny);
        Assert.Equal(CardSize.Small, core.EffectiveSize);
        Assert.Equal(1, core.SlotWidth);

        // Large+Tiny -> EffectiveSize=Medium, SlotWidth=2
        var ff = new CardInstance(forceFieldTpl, CardTier.Silver, enchantment: "Deadly");
        ff.PermanentAdd["Tiny"] = 1;
        Assert.Equal("Deadly", ff.Enchantment);
        Assert.Equal(CardSize.Medium, ff.EffectiveSize);
        Assert.Equal(2, ff.SlotWidth);

        // 2. Diamond Fangs (Diamond Tier) 应对变成 Small 的 Diamond Medium 卡生效 -50% CD
        var p1 = CreatePlayer("p1", HeroNames.Dooley, 3000);
        p1.Skills.Add(new CardInstance(bangsTpl, CardTier.Diamond));
        var bunker = new CardInstance(bunkerTpl, CardTier.Gold); // Large (3格)
        Assert.True(p1.Hand.TryPlaceOrShift(core, 0));    // slot 0..0 (1格)
        Assert.True(p1.Hand.TryPlaceOrShift(bunker, 1));  // slot 1..3 (3格)
        Assert.True(p1.Hand.TryPlaceOrShift(ff, 4));      // slot 4..5 (2格)

        AuraEngine.RecalculateAuras(p1, null, RuleScope.OutOfCombat, 1, 1, repo);
        Assert.Equal(50.0, core.GetAttribute("PercentCooldownReduction"));
        Assert.Equal(2, (int)core.GetAttribute("Multicast"));
        Assert.Equal(2, (int)ff.GetAttribute("Multicast"));

        // 3. 拖拽/换位移除 core 与 bunker 的邻接（将 core 移至 slot 6），Multicast 必须实时收回
        Assert.True(p1.Hand.Remove(core));
        Assert.True(p1.Hand.TryPlaceOrShift(core, 6));
        AuraEngine.RecalculateAuras(p1, null, RuleScope.OutOfCombat, 1, 1, repo);
        Assert.Equal(1, (int)core.GetAttribute("Multicast"));
        Assert.Equal(2, (int)ff.GetAttribute("Multicast"));
    }

    // =========================================================================
    // Q11 / Q14 / Q15 / Q16：7 张点检卡直读 DB 附魔、闪亮译名、Altar 归属与 >10 技能全生效
    // =========================================================================

    [Fact]
    public void Q11_Q14_Q15_Q16_RadiantDbCompliance_AltarOwnership_And_Over10Skills()
    {
        var repo = Repo.Value;

        // 1. Q11: 冰天鹅 Radiant 拥有自身 100% 免疫与 DestroyImmunity=1；而另外 6 张在 DB 无自身 100% 免疫
        var swanRad = new CardInstance(repo.GetCard(IceSwanId)!, CardTier.Gold, enchantment: "Radiant");
        Assert.Equal(100, (int)swanRad.GetAttribute("PercentFreezeReduction"));
        Assert.Equal(100, (int)swanRad.GetAttribute("PercentSlowReduction"));
        Assert.Equal(1, (int)swanRad.GetAttribute("DestroyImmunity"));

        string[] nonSelfImmuneRadiantIds = [SignalBoosterId, WaterskinId, CoffeeId, MaracasId, NeuralToxinId, SkyspiritAshesId];
        foreach (var id in nonSelfImmuneRadiantIds)
        {
            var card = new CardInstance(repo.GetCard(id)!, enchantment: "Radiant");
            Assert.Equal(0, (int)card.GetAttribute("DestroyImmunity"));
        }

        // 实战测试：Radiant Signal Booster（CD=3000ms, 无自身免疫）在战斗中应真实被 Freeze 与 Slow 作用
        var p1 = CreatePlayer("p1", HeroNames.Dooley, 4000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 4000);
        var booster = new CardInstance(repo.GetCard(SignalBoosterId)!, CardTier.Bronze, enchantment: "Radiant");
        p1.Hand.TryPlaceOrShift(booster, 0);
        p2.Hand.TryAddAuto(MakeSyntheticItem(
            "t0_ctrl",
            0,
            new RuleNode
            {
                Type = "TActionAnd",
                Actions =
                [
                    new RuleNode { Type = "TActionCardFreeze", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, RawValueNode = FixedRaw(1000) },
                    new RuleNode { Type = "TActionCardSlow", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, RawValueNode = FixedRaw(1000) }
                ]
            },
            triggerType: "TTriggerOnFightStarted"));
        var resSig = CombatSimulator.Simulate(repo, p1, p2, seed: 1101);
        Assert.Contains(resSig.Events, e => e.Type == CombatEventType.FreezeApplied && e.TargetCardInstanceId == booster.InstanceId && e.Value == 1000);
        Assert.Contains(resSig.Events, e => e.Type == CombatEventType.SlowApplied && e.TargetCardInstanceId == booster.InstanceId && e.Value == 1000);

        // Radiant Abducted Cow（DB 仅给 100% Slow/Freeze，无 DestroyImmunity）仍可被摧毁并变身 Jerky
        var pCow1 = CreatePlayer("pcow1", HeroNames.Dooley, 3000);
        var pCow2 = CreatePlayer("pcow2", HeroNames.Vanessa, 3000);
        var radCow = new CardInstance(repo.GetCard(AbductedCowId)!, CardTier.Silver, enchantment: "Radiant");
        pCow1.Hand.TryAddAuto(radCow);
        pCow2.Hand.TryAddAuto(MakeSyntheticItem(
            "cow_killer",
            1000,
            new RuleNode { Type = "TActionCardDestroy", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" } }));
        var resCow = CombatSimulator.Simulate(repo, pCow1, pCow2, seed: 1102);
        Assert.Contains(resCow.Events, e => e.Type == CombatEventType.CardDestroyed && e.TargetCardInstanceId == radCow.InstanceId);

        // 2. Q14: 双重附魔覆盖与 preventOverride / Hour 临时覆盖校验
        var enchCard = new CardInstance(repo.GetCard(IceSwanId)!, CardTier.Gold, enchantment: "Deadly");
        enchCard.ApplyEnchantment("Turbo", preventOverride: true);
        Assert.Equal("Deadly", enchCard.Enchantment);
        enchCard.ApplyEnchantment("Shiny", preventOverride: false, DurationTarget.Hour);
        Assert.Equal("Shiny", enchCard.Enchantment);
        enchCard.ClearEndOfHourModifiers();
        Assert.Equal("Deadly", enchCard.Enchantment);

        // 3. Q15: Nullfrost Altar 光环使己方免疫冻结（FreezeApplied=0），同时仅针对己方使用过的物品触发 0ms PerformedFreeze 并为己方武器+10伤害，绝不串台到对手
        var altarP1 = CreatePlayer("ap1", HeroNames.Vanessa, 3000);
        var altarP2 = CreatePlayer("ap2", HeroNames.Dooley, 3000);
        var altar = new CardInstance(repo.GetCard(NullfrostAltarId)!, CardTier.Diamond);
        var p1Wpn = MakeSyntheticItem(
            "ap1_wpn",
            2000,
            new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } },
            tags: ["Weapon"],
            extraAttrs: new() { ["DamageAmount"] = 10 });
        var p2Wpn = MakeSyntheticItem(
            "ap2_wpn",
            2000,
            new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } },
            tags: ["Weapon"],
            extraAttrs: new() { ["DamageAmount"] = 10 });
        altarP1.Hand.TryPlaceOrShift(altar, 0);
        altarP1.Hand.TryPlaceOrShift(p1Wpn, 3);
        altarP2.Hand.TryPlaceOrShift(p2Wpn, 0);
        var resAltar = CombatSimulator.Simulate(repo, altarP1, altarP2, seed: 1501);
        Assert.DoesNotContain(resAltar.Events, e => e.Type == CombatEventType.FreezeApplied && e.SourceCardInstanceId == altar.InstanceId);
        // 第 1 发 DamageAmount=10；使用触发 PerformedFreeze 叠加 +10 后，第 2 发 DamageAmount 应 >= 20；敌方 p2Wpn 始终保持 10
        var p1Hits = resAltar.Events.Where(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == p1Wpn.InstanceId).ToList();
        var p2Hits = resAltar.Events.Where(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == p2Wpn.InstanceId).ToList();
        Assert.True(p1Hits.Count >= 2 && p1Hits[1].Value >= 20, $"Nullfrost Altar 应在己方触发 PerformedFreeze 并叠伤 >= 20，实际 {string.Join(",", p1Hits.Select(x => x.Value))}");
        Assert.All(p2Hits, h => Assert.Equal(10, (int)h.Value));

        // 4. Q16: 超过 10 个技能（15 个主动触发技能）在战斗内全量生效不截断
        var skP1 = CreatePlayer("skp1", HeroNames.Vanessa, 3000);
        var skP2 = CreatePlayer("skp2", HeroNames.Vanessa, 3000);
        for (int i = 0; i < 15; i++)
        {
            skP1.Skills.Add(new CardInstance(new CardTemplate
            {
                Id = $"test_skill_{i}",
                InternalName = $"test_skill_{i}",
                Type = "Skill",
                Size = "Medium",
                StartingTier = "Bronze",
                Abilities = new Dictionary<string, AbilityDefinition>
                {
                    ["0"] = new()
                    {
                        Id = "0",
                        Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                        Action = new RuleNode { Type = "TActionPlayerShieldApply", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" }, ReferenceValue = Fixed(10) }
                    }
                }
            }));
        }
        var resSk = CombatSimulator.Simulate(repo, skP1, skP2, seed: 1601);
        Assert.Equal(150, (int)resSk.Player1TotalShieldGained);
    }

    // =========================================================================
    // Q17–Q22：The Core / Launcher Core / 3D 打印机 & DB 卡真实字段（King Bitey/Mecha/暗改5卡/长青火爪/流星索）
    // =========================================================================

    [Fact]
    public void Q17_To_Q22_Cores3DPrinter_And_DbCardParity()
    {
        var repo = Repo.Value;

        // Q17: The Core 不自充，Launcher Core + Tiny + 3D Printer 复制多份互相充能不卡死/溢出
        var printerTpl = repo.GetCard(Printer3DId)!;
        var launcherTpl = repo.GetCard(LauncherCoreId)!;
        var p1 = CreatePlayer("p1", HeroNames.Dooley, 4000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 4000);

        var tinyLauncher = new CardInstance(launcherTpl, CardTier.Silver);
        tinyLauncher.PermanentAdd["Tiny"] = 1; // Medium -> Small (1格), 满足 3D Printer 左侧 Small 条件
        tinyLauncher.RebuildTierAndEnchantmentData();
        var printer = new CardInstance(printerTpl, CardTier.Diamond);

        Assert.True(p1.Hand.TryPlaceOrShift(tinyLauncher, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(printer, 1));
        var resCore = CombatSimulator.Simulate(repo, p1, p2, seed: 1701);
        Assert.Contains(resCore.Events, e => e.Type == CombatEventType.CardTransformed && (e.SourceCardInstanceId == printer.InstanceId || (e.Detail?.StartsWith(printer.InstanceId + "->", StringComparison.Ordinal) ?? false)));

        // Q18: King Bitey、Q19: Mecha Dragon 4s、Q20 暗改 5 卡、Q21 Fire Claw、Q22 Bolas Tool 标签
        var kingBitey = FindByPrefix(repo, "edb37ff9");
        Assert.Equal("King Bitey", kingBitey.InternalName);
        var mecha = repo.GetCard(MechaDragonId)!;
        Assert.Equal(4000, (int)new CardInstance(mecha, CardTier.Diamond).GetAttribute("CooldownMax"));
        Assert.Equal("Monstrous Marble", FindByPrefix(repo, "aa2ffa22").InternalName);
        Assert.Equal("Paper Clip Bow", FindByPrefix(repo, "5b205ebf").InternalName);
        Assert.Equal("Tortuga", repo.GetCard(TortugaId)!.InternalName);
        Assert.Equal("Inspired Rage", FindByPrefix(repo, "0beccea0").InternalName);
        Assert.Equal("Truffles", FindByPrefix(repo, "2aab9e1b").InternalName);
        Assert.True(repo.GetCard(FireClawId)!.Enchantments?.ContainsKey("Mossy") == true);
        Assert.Contains("Tool", repo.GetCard(BolasId)!.Tags);
    }

    // =========================================================================
    // Q24：Reload 优先选择 CanBeReloaded (Ammo < AmmoMax)，并严守 [0, AmmoMax]
    // =========================================================================

    [Fact]
    public void Q24_ReloadRandom_PrioritizesCanBeReloaded_And_ClampsAmmoBounds()
    {
        var repo = Repo.Value;
        var satchelTpl = repo.GetCard(SatchelId)!;   // TActionCardReload -> TTargetCardRandom
        var musketTpl = repo.GetCard(MusketId)!;     // AmmoMax=1
        var cannonTpl = repo.GetCard(CannonId)!;     // AmmoMax=2

        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var fullMusket = new CardInstance(musketTpl, CardTier.Silver);
        fullMusket.ResetForCombat(null); // CurrentAmmo = 1 == AmmoMax(1)
        var emptyCannon = new CardInstance(cannonTpl, CardTier.Bronze);
        emptyCannon.ResetForCombat(null);
        emptyCannon.CurrentAmmo = 0; // CanBeReloaded = true!

        var satchel = new CardInstance(satchelTpl, CardTier.Silver);
        p1.Hand.TryPlaceOrShift(fullMusket, 0);
        p1.Hand.TryPlaceOrShift(emptyCannon, 1);
        p1.Hand.TryPlaceOrShift(satchel, 3);

        var reloadAction = satchel.ActiveAbilities.First(a => a.Action?.Type == "TActionCardReload").Action!;
        // 遍历多个种子，每一次都必须优先命中不满弹的 emptyCannon，绝不浪费在满弹 fullMusket 上
        for (int seed = 1; seed <= 25; seed++)
        {
            emptyCannon.CurrentAmmo = 0;
            fullMusket.CurrentAmmo = 1;
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(seed)) { SelfCard = satchel };
            AbilityExecutor.ExecuteAction(reloadAction, satchel, ctx);
            Assert.Equal(2, emptyCannon.CurrentAmmo);
            Assert.Equal(1, fullMusket.CurrentAmmo);
        }

        // 无 AmmoMax 卡 (AmmoMax == 0) 装弹后始终为 0
        var zeroAmmoItem = MakeSyntheticItem("zero_ammo", 3000);
        p1.Hand.TryPlaceOrShift(zeroAmmoItem, 5);
        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionCardReload",
            Target = new RuleNode { Type = "TTargetCardSelf" },
            RawValueNode = FixedRaw(3)
        }, zeroAmmoItem, new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(1)) { SelfCard = zeroAmmoItem });
        Assert.Equal(0, zeroAmmoItem.CurrentAmmo);
    }

    // =========================================================================
    // Q44：Hero8 7 种音符插座与 Tempo 体系（全库无 10 音符召唤神龙）
    // =========================================================================

    [Fact]
    public void Q44_Hero8_Has7MusicNotesAndTempoSystem_No10NoteSummonDragonRule()
    {
        var repo = Repo.Value;
        var musicNotes = repo.CardsById.Values
            .Where(c => string.Equals(c.TypeDiscriminator, "TCardMusicNoteSocketEffect", StringComparison.Ordinal) || c.MusicNote.HasValue)
            .ToList();
        Assert.Equal(7, musicNotes.Count);
        Assert.NotNull(repo.GetCard(BaseTempoEffectId));

        // 确认有 Tempo 卡时 2000ms 产生 1 Tempo，无 Tempo 卡时 Base Tempo Gain Cooldown Effect 压制产点
        var pTempo = CreatePlayer("ptempo", HeroNames.Hero8, 3000);
        var pDummy = CreatePlayer("pdum", HeroNames.Vanessa, 3000);
        pTempo.Hand.TryAddAuto(MakeSyntheticItem("tempo_card", 10_000, extraAttrs: new() { ["TempoCost"] = 1 }));
        var res = CombatSimulator.Simulate(repo, pTempo, pDummy, seed: 4401);
        Assert.Contains(res.Timeline, f => f.TimeMs >= 2000 && f.P1Tempo >= 1);
    }

    // =========================================================================
    // Q48：卡诺克狂暴战斗内自动挂载 Base Rage Effect（清控 + -10% 冷却）、不刷新不叠加
    // =========================================================================

    [Fact]
    public void Q48_KarnokEnrage_CombatCleansesAndReducesCooldown_NoRefreshOrStackWhileEnraged()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Karnok, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);

        var wpn = MakeSyntheticItem(
            "karnok_wpn",
            4000,
            new RuleNode
            {
                Type = "TActionPlayerDamage",
                Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" },
                ReferenceValue = Fixed(10)
            });
        // 开局先给武器挂上 Slow 与 Freeze，并在 500ms 施加 100 Rage 触发 Enrage，随后 2000ms 再次施加 100 Rage
        var rager = MakeSyntheticItem(
            "rager_item",
            1000,
            new RuleNode
            {
                Type = "TActionPlayerRageApply",
                Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                ReferenceValue = Fixed(100)
            });
        var preDebuffer = MakeSyntheticItem(
            "pre_debuff",
            0,
            new RuleNode
            {
                Type = "TActionAnd",
                Actions =
                [
                    new RuleNode { Type = "TActionCardFreeze", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, RawValueNode = FixedRaw(3000) },
                    new RuleNode { Type = "TActionCardSlow", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, RawValueNode = FixedRaw(3000) }
                ]
            },
            triggerType: "TTriggerOnFightStarted");

        p1.Hand.TryPlaceOrShift(wpn, 0);
        p1.Hand.TryPlaceOrShift(rager, 1);
        p2.Hand.TryAddAuto(preDebuffer);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 4801);

        // 1. 1000ms 首次狂暴触发后，Base Rage Effect (4472da8a) 必须立即清除 Freeze 与 Slow 并给出 10% CDR（wpn=3600ms）
        var enrageStart = res.Events.First(e => e.Type == CombatEventType.PlayerEnraged && e.TargetPlayerId == "p1");
        var enrageEnd = res.Events.First(e => e.Type == CombatEventType.PlayerEnrageEnded && e.TargetPlayerId == "p1");
        // rager 自身也于 0ms 被冻结 3000ms -> 3000ms 解冻后约 3980ms 开火触发 Enrage，持续 5000ms 于 8960ms 结束
        int expectedEndMin = enrageStart.TimeMs + 4960;
        int expectedEndMax = enrageStart.TimeMs + 5020;
        Assert.InRange(enrageEnd.TimeMs, expectedEndMin, expectedEndMax);

        // 在 Enraged 窗口期间 rager 每 900ms 会再加 100 Rage，但不得刷新延后 enrageEnd，也不得叠加第二个 PlayerEnraged 事件
        int enragedCountBeforeEnd = res.Events.Count(e => e.Type == CombatEventType.PlayerEnraged && e.TargetPlayerId == "p1" && e.TimeMs < enrageEnd.TimeMs);
        Assert.Equal(1, enragedCountBeforeEnd);

        // 验证进入狂暴后下一帧武器冷却上限降至 3600ms (-10%) 且 Freeze/Slow 均清零
        var frameAfterEnrage = res.Timeline.First(f => f.TimeMs >= enrageStart.TimeMs + 40);
        var wpnSnap = frameAfterEnrage.Cards.First(c => c.InstanceId == wpn.InstanceId);
        Assert.Equal(0, wpnSnap.FreezeMs);
        Assert.Equal(0, wpnSnap.SlowMs);
        Assert.Equal(3600, wpnSnap.CooldownMaxMs);
    }

    // =========================================================================
    // Q49：马克随机武器充能（均匀随机、跳过无冷却卡，优先带弹/无限弹冷却武器）
    // =========================================================================

    [Fact]
    public void Q49_MakRandomWeaponCharge_UniformSkipsZeroCdAndPrioritizesAmmo()
    {
        var repo = Repo.Value;
        var skillTpl = repo.GetCard(TemporalStrikeId)!;
        var skill = new CardInstance(skillTpl, CardTier.Gold);

        var p1 = CreatePlayer("p1", HeroNames.Mak, 3000);
        p1.Skills.Add(skill);

        var wpnA = MakeSyntheticItem("wpn_a", 4000, tags: ["Weapon"]);
        var wpnB = MakeSyntheticItem("wpn_b", 4000, tags: ["Weapon"]);
        var wpnNoCd = MakeSyntheticItem("wpn_nocd", 0, tags: ["Weapon"]);
        var wpnEmptyAmmo = MakeSyntheticItem("wpn_empty", 4000, tags: ["Weapon"], extraAttrs: new() { ["AmmoMax"] = 2 });
        wpnEmptyAmmo.CurrentAmmo = 0;

        p1.Hand.TryPlaceOrShift(wpnA, 0);
        p1.Hand.TryPlaceOrShift(wpnB, 1);
        p1.Hand.TryPlaceOrShift(wpnNoCd, 2);
        p1.Hand.TryPlaceOrShift(wpnEmptyAmmo, 3);

        var chargeAction = skill.ActiveAbilities.First(a => a.Action?.Type == "TActionCardCharge").Action!;
        int hitA = 0, hitB = 0;
        for (int seed = 1; seed <= 80; seed++)
        {
            wpnA.CooldownProgressMs = 0;
            wpnB.CooldownProgressMs = 0;
            wpnNoCd.CooldownProgressMs = 0;
            wpnEmptyAmmo.CooldownProgressMs = 0;
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(seed)) { SelfCard = skill };
            AbilityExecutor.ExecuteAction(chargeAction, skill, ctx);

            Assert.Equal(0, wpnNoCd.CooldownProgressMs);
            Assert.Equal(0, wpnEmptyAmmo.CooldownProgressMs);
            if (wpnA.CooldownProgressMs > 0) hitA++;
            if (wpnB.CooldownProgressMs > 0) hitB++;
        }

        Assert.True(hitA >= 25 && hitB >= 25, $"两把有效武器命中应近似均匀 (50/50)，实际 hitA={hitA}, hitB={hitB}");
    }

    // =========================================================================
    // Q50：Core Initialization 9 选 8 活跃核心（Focused Core=Never），Primal Core + Harmadillo
    // =========================================================================

    [Fact]
    public void Q50_CoreInitialization_Has8SpawnableCores_And_PrimalCoreWithHarmadillo()
    {
        var repo = Repo.Value;
        var coreInit = repo.GetCard(CoreInitId)!;
        var ids = coreInit.SelectionContext!.SpawnContext!.Groups![0].Filters![0].Ids!;
        Assert.Equal(9, ids.Count);

        var focused = repo.GetCard(FocusedCoreId)!;
        Assert.Equal("Never", focused.SpawningEligibility);

        int spawnableCount = ids.Count(id => !string.Equals(repo.GetCard(id)!.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(8, spawnableCount);

        // Primal Core + Harmadillo：护盾触发 Harmadillo 充能，Primal Core 触发给自身/Dinosaur 叠加伤害
        var p1 = CreatePlayer("p1", HeroNames.Dooley, 4000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 4000);
        var primal = new CardInstance(repo.GetCard(PrimalCoreId)!, CardTier.Silver);
        var harmadillo = new CardInstance(repo.GetCard(HarmadilloId)!, CardTier.Bronze);
        var shielder = MakeSyntheticItem("q50_shielder", 1000, new RuleNode { Type = "TActionPlayerShieldApply", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" }, ReferenceValue = Fixed(20) });

        p1.Hand.TryPlaceOrShift(primal, 0);
        p1.Hand.TryPlaceOrShift(harmadillo, 2);
        p1.Hand.TryPlaceOrShift(shielder, 3);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 5001);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.ChargeApplied && e.TargetCardInstanceId == harmadillo.InstanceId);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == primal.InstanceId);
    }
}
