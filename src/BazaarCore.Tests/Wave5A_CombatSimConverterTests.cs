using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarServer.Game;
using BazaarServer.Protocol;
using MessagePack;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 5A 战斗回放协议转换（CombatSimConverter）专项单元测试：
/// - P1-04（RC-COMBAT-CLEANUP-ADDED-ATTR-LEAK）：战末清理对新增临时键发 CombatSimEventCardAttributeDeleted、既有临时改动复原基线、保留永久成长与任务进度；
/// - P1-08（RC-COMBAT-TRANSFORM-FRAME-ORDER）：战中 Virus 变形首帧即下发非零毒伤、冷却、多播与卖价；
/// - P1-07（RC-COMBAT-PLAYER-ATTR-NO-EVENT）：战中生金/玩家属性变更产出标准帧属性更新 + PlayerModifyAttribute 动作事件 + 官方飞币 VFX；
/// - P1-03（RC-COMBAT-SPAWN-HP-MULT-MISSING）：倍率血量首帧基线与 CombatSpawnSim 对齐，并支持显式基线校准与 TimeMs==0 逆推。
/// </summary>
public sealed class Wave5A_CombatSimConverterTests
{
    private const string OfficialVirusTemplateId = "020a0ec0-21e6-41af-899f-063573ba9ca5";

    private static readonly Lazy<GameDataRepository> Repo = new(() => SharedGameDataFixture.Shared.Repo);

    private static RuleNode ParseNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;

    private static CardInstance MakeCustomItem(
        string id,
        string name,
        Dictionary<string, double> attrs,
        Dictionary<string, AbilityDefinition>? abilities = null,
        Dictionary<string, AuraDefinition>? auras = null,
        List<string>? tags = null,
        string size = "Small",
        CardTier tier = CardTier.Bronze,
        string? instanceId = null)
    {
        var abIds = abilities?.Keys.ToList() ?? new List<string>();
        var auraIds = auras?.Keys.ToList() ?? new List<string>();
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = name,
            Type = "Item",
            StartingTier = tier.ToString(),
            Size = size,
            Tags = tags ?? new List<string>(),
            HiddenTags = new List<string>(),
            Heroes = new List<string> { "Common" },
            Attributes = new Dictionary<string, double>(attrs, StringComparer.OrdinalIgnoreCase),
            Abilities = abilities ?? new Dictionary<string, AbilityDefinition>(),
            Auras = auras ?? new Dictionary<string, AuraDefinition>(),
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                [tier.ToString()] = new TierData
                {
                    Attributes = new Dictionary<string, double>(attrs, StringComparer.OrdinalIgnoreCase),
                    AbilityIds = abIds,
                    AuraIds = auraIds,
                },
            },
        };
        return new CardInstance(tpl, tier, enchantment: null, instanceId: instanceId);
    }

    private static (List<RunState.CardEntry> Entries, Dictionary<string, CardInstance> ById) BuildBoardEntries(
        PlayerState side,
        ECombatantId owner)
    {
        var entries = new List<RunState.CardEntry>();
        var map = new Dictionary<string, CardInstance>(StringComparer.Ordinal);
        int fallbackIdx = 0;

        void AddList(IReadOnlyList<CardInstance> cards, EInventorySection section, ECardType cardType)
        {
            foreach (var c in cards)
            {
                Guid tplGuid = Guid.TryParse(c.Template.Id, out var g) ? g : Guid.NewGuid();
                int slot = c.StartSlot >= 0 ? Math.Clamp(c.StartSlot, 0, 9) : Math.Clamp(fallbackIdx++, 0, 9);
                var entry = new RunState.CardEntry
                {
                    InstanceId = c.InstanceId,
                    TemplateId = tplGuid,
                    Type = cardType,
                    Size = c.Template.Size switch
                    {
                        "Large" => ECardSize.Large,
                        "Medium" => ECardSize.Medium,
                        _ => ECardSize.Small,
                    },
                    Tier = (ETier)(int)c.Tier,
                    Owner = owner,
                    Section = section,
                    Socket = (EContainerSocketId)slot,
                    CoreCard = c,
                };
                foreach (var kv in c.BaseAttributes)
                {
                    if (Enum.TryParse<ECardAttributeType>(kv.Key, ignoreCase: true, out var at))
                        entry.Attributes[at] = (int)Math.Round(kv.Value);
                }
                entries.Add(entry);
                map[c.InstanceId] = c;
            }
        }

        AddList(side.Hand.Cards, EInventorySection.Hand, ECardType.Item);
        AddList(side.Skills, EInventorySection.Hand, ECardType.Skill);
        AddList(side.SocketEffects, EInventorySection.Hand, ECardType.SocketEffect);
        return (entries, map);
    }

    private static CombatSim RoundTripCombatSim(CombatSim sim)
    {
        byte[] wire = Codec.WriteMessage(new NetMessageCombatSim(sim));
        Assert.NotNull(wire);
        Assert.True(wire.Length > 20);
        byte[] raw = MessagePackSerializer.Serialize(sim, Codec.Options);
        var restored = MessagePackSerializer.Deserialize<CombatSim>(raw, Codec.Options);
        Assert.NotNull(restored);
        return restored;
    }

    /// <summary>
    /// 模拟官方客户端 CombatSimHandler.Simulate 逐帧执行顺序（先 CardUpdates、再 Events 含 CardAttributeDeleted），
    /// 返回战斗回放全部播放完毕后每张卡在客户端 Data.Entities 中残留的精确属性字典。
    /// </summary>
    private static Dictionary<string, Dictionary<ECardAttributeType, int>> ReplayClientCardAttributes(
        CombatSim sim,
        IEnumerable<RunState.CardEntry> initialEntries)
    {
        var clientAttrs = new Dictionary<string, Dictionary<ECardAttributeType, int>>(StringComparer.Ordinal);
        foreach (var e in initialEntries)
        {
            clientAttrs[e.InstanceId] = new Dictionary<ECardAttributeType, int>(e.Attributes);
        }

        foreach (var frame in sim.Frames)
        {
            if (frame.CardUpdates != null)
            {
                foreach (var (cid, cu) in frame.CardUpdates)
                {
                    if (!clientAttrs.TryGetValue(cid.Value, out var attrs))
                    {
                        attrs = new Dictionary<ECardAttributeType, int>();
                        clientAttrs[cid.Value] = attrs;
                    }
                    if (cu.Attributes != null)
                    {
                        foreach (var (attrType, upd) in cu.Attributes)
                        {
                            attrs[attrType] = upd.CurrentValue;
                        }
                    }
                }
            }

            if (frame.Events != null)
            {
                foreach (var ev in frame.Events)
                {
                    if (ev is CombatSimEventCardAttributeDeleted del &&
                        clientAttrs.TryGetValue(del.InstanceId.Value, out var attrs))
                    {
                        attrs.Remove(del.AttributeType);
                    }
                }
            }
        }

        return clientAttrs;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-04：战末清理新增临时属性发 Deleted + 既有临时属性复原 + 永久成长属性保留
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_04_PostCombatCleanup_DeletesAddedTempAttrs_RevertsTempChanges_PreservesPermanentGrowth()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 1500, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, initialHealthMax: 1500, unlockedHandSlots: 10);

        // 1) 目标卡：战前仅有 CooldownMax=3000, DamageAmount=10, Counter=4, SellPrice=2（无 CritChance/Lifesteal/Freeze/Cooldown）
        var targetBlade = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "BaselineBlade",
            new() { ["CooldownMax"] = 3000, ["DamageAmount"] = 10, ["Counter"] = 4, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}"""),
                },
            },
            tags: ["Weapon"],
            instanceId: "p1_target_blade");

        // 2) 战中临时增益源：1000ms 给左侧 targetBlade 施加 UntilEndOfCombat 临时新增 CritChance(+24)、Lifesteal(+100) 与临时修改 DamageAmount(+25)、Counter(+3)
        var tempBuffer = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "TempBuffer",
            new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionCardModifyAttribute",
                        "AttributeType": "CritChance",
                        "Operation": "Add",
                        "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                        "Target": { "$type": "TTargetCardXMost", "TargetSection": "SelfHand", "TargetMode": "LeftMostCard" },
                        "Value": { "$type": "TFixedValue", "Value": 24 }
                    }
                    """),
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionCardModifyAttribute",
                        "AttributeType": "Lifesteal",
                        "Operation": "Add",
                        "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                        "Target": { "$type": "TTargetCardXMost", "TargetSection": "SelfHand", "TargetMode": "LeftMostCard" },
                        "Value": { "$type": "TFixedValue", "Value": 100 }
                    }
                    """),
                },
                ["2"] = new AbilityDefinition
                {
                    Id = "2",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionCardModifyAttribute",
                        "AttributeType": "DamageAmount",
                        "Operation": "Add",
                        "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                        "Target": { "$type": "TTargetCardXMost", "TargetSection": "SelfHand", "TargetMode": "LeftMostCard" },
                        "Value": { "$type": "TFixedValue", "Value": 25 }
                    }
                    """),
                },
                ["3"] = new AbilityDefinition
                {
                    Id = "3",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionCardModifyAttribute",
                        "AttributeType": "Counter",
                        "Operation": "Add",
                        "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                        "Target": { "$type": "TTargetCardXMost", "TargetSection": "SelfHand", "TargetMode": "LeftMostCard" },
                        "Value": { "$type": "TFixedValue", "Value": 3 }
                    }
                    """),
                },
            },
            instanceId: "p1_temp_buffer");

        // 3) 战中永久成长卡：1200ms 永久 +15 DamageAmount（既有键成长）且永久新增 +10 CritChance（战前不存在但属永久新增）
        //    注：AbilityExecutor.ResolveDurationTarget 约定 Duration == null 即写入 Permanent 永久层
        var permGrower = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "PermanentGrower",
            new() { ["CooldownMax"] = 1200, ["DamageAmount"] = 20, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionCardModifyAttribute",
                        "AttributeType": "DamageAmount",
                        "Operation": "Add",
                        "Target": { "$type": "TTargetCardSelf" },
                        "Value": { "$type": "TFixedValue", "Value": 15 }
                    }
                    """),
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionCardModifyAttribute",
                        "AttributeType": "CritChance",
                        "Operation": "Add",
                        "Target": { "$type": "TTargetCardSelf" },
                        "Value": { "$type": "TFixedValue", "Value": 10 }
                    }
                    """),
                },
            },
            tags: ["Weapon"],
            instanceId: "p1_perm_grower");

        // 4) 终结卡：2500ms 结束战斗
        var finisher = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "Finisher",
            new() { ["CooldownMax"] = 2500, ["DamageAmount"] = 9999, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}"""),
                },
            },
            tags: ["Weapon"],
            instanceId: "p1_finisher");

        // 敌方 1000ms 冰冻我方最左卡 3000ms（确保战斗结束时仍处于冰冻状态，检验先归零停 VFX 再彻底删键不留 Freeze:0）
        var oppFreezer = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "OppFreezer",
            new() { ["CooldownMax"] = 1000, ["FreezeAmount"] = 3000, ["AmmoMax"] = 1, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionCardFreeze","Target":{"$type":"TTargetCardXMost","TargetSection":"OpponentHand","TargetMode":"LeftMostCard"}}"""),
                },
            },
            instanceId: "p2_freezer");

        p1.Hand.TryPlaceOrShift(targetBlade, 0);
        p1.Hand.TryPlaceOrShift(tempBuffer, 1);
        p1.Hand.TryPlaceOrShift(permGrower, 2);
        p1.Hand.TryPlaceOrShift(finisher, 3);
        p2.Hand.TryPlaceOrShift(oppFreezer, 0);

        var res = CombatSimulator.Simulate(
            repo,
            p1,
            p2,
            seed: 50401,
            combatType: "PVP",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: false);

        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = RoundTripCombatSim(CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries));

        var lastFrame = sim.Frames[^1];
        int diedIdx = lastFrame.Events.FindIndex(e => e is CombatSimEventCombatantDied);
        Assert.True(diedIdx >= 0, "末帧必须包含 CombatSimEventCombatantDied");

        var targetId = new InstanceId("p1_target_blade");
        var permId = new InstanceId("p1_perm_grower");

        // 1) 末帧在 CombatantDied 之前，必须对 targetBlade 战中新增的临时键发射 CombatSimEventCardAttributeDeleted
        var targetDeletedBeforeDied = lastFrame.Events
            .Take(diedIdx)
            .OfType<CombatSimEventCardAttributeDeleted>()
            .Where(e => e.InstanceId == targetId)
            .Select(e => e.AttributeType)
            .ToHashSet();

        Assert.Contains(ECardAttributeType.CritChance, targetDeletedBeforeDied);
        Assert.Contains(ECardAttributeType.Lifesteal, targetDeletedBeforeDied);
        Assert.Contains(ECardAttributeType.Freeze, targetDeletedBeforeDied);
        Assert.Contains(ECardAttributeType.Cooldown, targetDeletedBeforeDied);
        // 既有键绝不可被误删
        Assert.DoesNotContain(ECardAttributeType.DamageAmount, targetDeletedBeforeDied);
        Assert.DoesNotContain(ECardAttributeType.Counter, targetDeletedBeforeDied);
        Assert.DoesNotContain(ECardAttributeType.SellPrice, targetDeletedBeforeDied);

        // 冰冻特效在末帧 CardUpdates 必须先归零以优雅收尾 VFX，随后由 Events 中的 CardAttributeDeleted 彻底删键
        Assert.True(lastFrame.CardUpdates.TryGetValue(targetId, out var targetLastCu));
        Assert.True(targetLastCu!.Attributes.TryGetValue(ECardAttributeType.Freeze, out var freezeZero));
        Assert.Equal(0, freezeZero!.CurrentValue);

        // 既有被临时修改的 DamageAmount(10->35) 与 Counter(4->7) 必须在末帧 CardUpdates 复原回战前基线值
        Assert.True(targetLastCu.Attributes.TryGetValue(ECardAttributeType.DamageAmount, out var dmgRevert));
        Assert.Equal(10, dmgRevert!.CurrentValue);
        Assert.True(targetLastCu.Attributes.TryGetValue(ECardAttributeType.Counter, out var counterRevert));
        Assert.Equal(4, counterRevert!.CurrentValue);

        // 2) permGrower 的永久成长属性（DamageAmount 20->50 与永久新增 CritChance 20）绝不可误删或回滚
        var permDeleted = lastFrame.Events
            .OfType<CombatSimEventCardAttributeDeleted>()
            .Where(e => e.InstanceId == permId)
            .Select(e => e.AttributeType)
            .ToHashSet();
        Assert.DoesNotContain(ECardAttributeType.DamageAmount, permDeleted);
        Assert.DoesNotContain(ECardAttributeType.CritChance, permDeleted);

        // 3) 完整模拟官方客户端重放后，验证最终客户端 Data.Entities 属性字典完全洁净且精准
        var finalClientAttrs = ReplayClientCardAttributes(sim, p1Entries.Concat(p2Entries));
        var targetFinal = finalClientAttrs["p1_target_blade"];
        Assert.False(targetFinal.ContainsKey(ECardAttributeType.CritChance), "战中临时 CritChance 不应残留在客户端");
        Assert.False(targetFinal.ContainsKey(ECardAttributeType.Lifesteal), "战中临时 Lifesteal 不应残留在客户端");
        Assert.False(targetFinal.ContainsKey(ECardAttributeType.Freeze), "战中临时 Freeze（含 :0）不应残留在客户端");
        Assert.False(targetFinal.ContainsKey(ECardAttributeType.Cooldown), "战中瞬态 Cooldown 不应残留在客户端");
        Assert.Equal(10, targetFinal[ECardAttributeType.DamageAmount]);
        Assert.Equal(4, targetFinal[ECardAttributeType.Counter]);
        Assert.Equal(2, targetFinal[ECardAttributeType.SellPrice]);

        var permFinal = finalClientAttrs["p1_perm_grower"];
        Assert.True(permFinal[ECardAttributeType.DamageAmount] >= 35, $"永久成长 DamageAmount 应保留，实际={permFinal[ECardAttributeType.DamageAmount]}");
        Assert.True(permFinal.TryGetValue(ECardAttributeType.CritChance, out int permCrit) && permCrit >= 10,
            "永久新增 CritChance 必须保留，不可误清");
    }

    [Fact]
    public void P1_04_PostCombatCleanup_SyntheticAddedBurnDamageAndFlatCdr_AllDeletedWhileQuestPreserved()
    {
        var repo = Repo.Value;
        Guid tplGuid = Guid.NewGuid();
        var item = MakeCustomItem(
            tplGuid.ToString(),
            "NonWeaponRelic",
            new() { ["SellPrice"] = 2, ["CooldownMax"] = 4000 },
            instanceId: "p1_relic");

        var p1Entry = new RunState.CardEntry
        {
            InstanceId = "p1_relic",
            TemplateId = tplGuid,
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = item,
        };
        p1Entry.Attributes[ECardAttributeType.SellPrice] = 2;
        p1Entry.Attributes[ECardAttributeType.CooldownMax] = 4000;

        // 构造战中新增 "for the fight" 临时属性（Burn=7, Damage=8, Counter=7, FlatCooldownReduction=-1000）+ 持久 Quest 进度
        var midCardFrame = new CardCooldownFrame
        {
            InstanceId = "p1_relic",
            Progress01 = 0.5f,
            CooldownMaxMs = 3000,
            BurnApplyAmount = 7,
            DamageAmount = 8,
            Counter = 7,
            SellPrice = 2,
        };
        CombatSnapshotExtras.Attach(midCardFrame, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["FlatCooldownReduction"] = -1000,
        });

        var result = new CombatResult
        {
            WinnerPlayerId = "player",
            DurationMs = 600,
            Player1RemainingHp = 300,
            Player2RemainingHp = 0,
            Timeline =
            {
                new CombatTickState { TimeMs = 0, P1Hp = 300, P1MaxHp = 300, P2Hp = 300, P2MaxHp = 300, Cards = { midCardFrame } },
                new CombatTickState { TimeMs = 600, P1Hp = 300, P1MaxHp = 300, P2Hp = 0, P2MaxHp = 300, Cards = { midCardFrame } },
            },
            Events =
            {
                new CombatEvent
                {
                    TimeMs = 200,
                    Type = CombatEventType.QuestUpdated,
                    TargetCardInstanceId = "p1_relic",
                    Detail = "Quest_1:0:3",
                    Value = 3,
                },
                new CombatEvent
                {
                    TimeMs = 400,
                    Type = CombatEventType.QuestCompleted,
                    TargetCardInstanceId = "p1_relic",
                    Detail = "Quest_1:3:3",
                    Value = 3,
                },
            },
        };

        var sim = RoundTripCombatSim(CombatSimConverter.ToCombatSim(
            result,
            repo,
            new Dictionary<string, CardInstance> { ["p1_relic"] = item },
            new Dictionary<string, CardInstance>(),
            [p1Entry],
            Array.Empty<RunState.CardEntry>()));

        var finalClientAttrs = ReplayClientCardAttributes(sim, [p1Entry]);
        var relicAttrs = finalClientAttrs["p1_relic"];

        // 战前不存在的 BurnApplyAmount / DamageAmount / Counter / FlatCooldownReduction / Cooldown 必须全数彻底删除
        Assert.False(relicAttrs.ContainsKey(ECardAttributeType.BurnApplyAmount));
        Assert.False(relicAttrs.ContainsKey(ECardAttributeType.DamageAmount));
        Assert.False(relicAttrs.ContainsKey(ECardAttributeType.Counter));
        Assert.False(relicAttrs.ContainsKey(ECardAttributeType.FlatCooldownReduction));
        Assert.False(relicAttrs.ContainsKey(ECardAttributeType.Cooldown));

        // 战前存在但被临时缩减的 CooldownMax(4000->3000) 必须复原回 4000
        Assert.Equal(4000, relicAttrs[ECardAttributeType.CooldownMax]);
        Assert.Equal(2, relicAttrs[ECardAttributeType.SellPrice]);

        // 战中任务进度 Quest_1 与 QuestCompletedCount 必须保留
        Assert.Equal(3, relicAttrs[ECardAttributeType.Quest_1]);
        Assert.Equal(1, relicAttrs[ECardAttributeType.QuestCompletedCount]);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-08：战中 Virus 变形首帧即下发非零毒伤、冷却、多播与卖价
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_08_MidCombatVirusTransform_FirstTransformFrameHasNonZeroPoisonCooldownMulticastAndSellPrice()
    {
        var repo = Repo.Value;
        var virusTpl = repo.GetCard(OfficialVirusTemplateId);
        Assert.NotNull(virusTpl);

        var p1 = new PlayerState("player", "P1", HeroNames.Mak, initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, initialHealthMax: 5000, unlockedHandSlots: 10);

        // 敌方持有银阶 Virus（8000ms 开火并把我方小卡临时变形为 Virus），我方持有青铜小卡（复现 Mak D1H3 实机场景）
        var oppVirus = new CardInstance(virusTpl!, CardTier.Silver, enchantment: null, instanceId: "opp_virus_src");
        var p1Victim = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "FloorSpikeMock",
            new() { ["CooldownMax"] = 5000, ["DamageAmount"] = 5, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}"""),
                },
            },
            size: "Small",
            tier: CardTier.Bronze,
            instanceId: "p1_victim_small");

        // 12000ms 终结战局（确保 Virus 8000ms 完成首次变形）
        var p1Finisher = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "LateFinisher",
            new() { ["CooldownMax"] = 12000, ["DamageAmount"] = 99999, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}"""),
                },
            },
            size: "Large",
            tier: CardTier.Gold,
            instanceId: "p1_late_finisher");

        p1.Hand.TryPlaceOrShift(p1Victim, 0);
        p1.Hand.TryPlaceOrShift(p1Finisher, 1);
        p2.Hand.TryPlaceOrShift(oppVirus, 0);

        var res = CombatSimulator.Simulate(
            repo,
            p1,
            p2,
            seed: 50801,
            combatType: "PVE",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: false);

        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = RoundTripCombatSim(CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries));

        // 定位发生 CombatSimEventCardTransformed 的首帧
        CombatSimFrame? transformFrame = null;
        CombatSimEventCardTransformed? transformEv = null;
        foreach (var f in sim.Frames)
        {
            var found = f.Events.OfType<CombatSimEventCardTransformed>()
                .FirstOrDefault(e => e.OriginalInstanceId == "p1_victim_small");
            if (found != null)
            {
                transformFrame = f;
                transformEv = found;
                break;
            }
        }

        Assert.NotNull(transformFrame);
        Assert.NotNull(transformEv);
        Assert.Single(transformEv!.TransformedCards);

        string newVirusInstId = transformEv.TransformedCards[0].InstanceId;
        var newVirusKey = new InstanceId(newVirusInstId);
        Assert.True(transformFrame!.CardUpdates.TryGetValue(newVirusKey, out var newVirusCu),
            "变形首帧 CardUpdates 必须包含新变出 Virus 的完整属性更新");

        // 核心回归断言（P1-08）：首帧即包含 Aura 3 实时计算的毒伤、冷却上限、当前冷却、多播与卖价，彻底告别绿 0 毒伤与缺失冷却/卖价
        Assert.True(newVirusCu!.Attributes.TryGetValue(ECardAttributeType.PoisonApplyAmount, out var poisonUpd),
            "变形首帧必须包含 PoisonApplyAmount");
        Assert.True(poisonUpd!.CurrentValue > 0,
            $"新变出 Virus 首帧 PoisonApplyAmount 必须 > 0（由 Aura 3 计算），实际={poisonUpd.CurrentValue}");

        Assert.True(newVirusCu.Attributes.TryGetValue(ECardAttributeType.CooldownMax, out var cdMaxUpd) && cdMaxUpd!.CurrentValue > 0,
            "变形首帧必须包含非零 CooldownMax");
        Assert.True(newVirusCu.Attributes.TryGetValue(ECardAttributeType.Cooldown, out var cdUpd) && cdUpd!.CurrentValue > 0,
            "变形首帧必须包含非零 Cooldown");
        Assert.True(newVirusCu.Attributes.TryGetValue(ECardAttributeType.Multicast, out var multiUpd) && multiUpd!.CurrentValue >= 1,
            "变形首帧必须包含 Multicast >= 1");
        Assert.True(newVirusCu.Attributes.TryGetValue(ECardAttributeType.SellPrice, out var sellUpd) && sellUpd!.CurrentValue > 0,
            "变形首帧必须包含 SellPrice > 0");
        Assert.Equal(ETier.Silver, newVirusCu.Tier);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-07：战中生金 / 玩家属性修改事件转换（实时属性更新 + 动作审计 + 官方飞币 VFX）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_07_InCombatGoldAndIncomeGain_EmitsPlayerUpdatesActionEventAndOfficialCoinBurstVfx()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Dooley, initialHealthMax: 1500, initialGold: 1, unlockedHandSlots: 10);
        p1.Attributes["Income"] = 5;
        var p2 = new PlayerState("opponent", "P2", HeroNames.Vanessa, initialHealthMax: 1500, unlockedHandSlots: 10);

        // 1) 复刻 starport_compass_blade：同时具备 TActionPlayerDamage（主动作）+ TActionPlayerModifyAttribute(Gold +2, Self)
        var compassBlade = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "StarportCompassBladeMock",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 20, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}"""),
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionPlayerModifyAttribute",
                        "AttributeType": "Gold",
                        "Operation": "Add",
                        "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" },
                        "Value": { "$type": "TFixedValue", "Value": 2 }
                    }
                    """),
                },
            },
            tags: ["Weapon"],
            instanceId: "p1_compass_blade");

        // 2) 纯生金 + 收入物品（主动作即 TActionPlayerModifyAttribute，检验不与 CardFired 双发，且同帧与 compassBlade 合并金币增量）
        var pureGoldCoin = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "PureGoldPrinter",
            new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionPlayerModifyAttribute",
                        "AttributeType": "Gold",
                        "Operation": "Add",
                        "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" },
                        "Value": { "$type": "TFixedValue", "Value": 3 }
                    }
                    """),
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionPlayerModifyAttribute",
                        "AttributeType": "Income",
                        "Operation": "Add",
                        "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" },
                        "Value": { "$type": "TFixedValue", "Value": 1 }
                    }
                    """),
                },
            },
            instanceId: "p1_pure_gold_coin");

        // 3) 2500ms 终结战斗（compassBlade 开火 2 次得 +4 金，pureGoldCoin 开火 1 次得 +3 金 +1 收入，总计 +7 金 +1 收入）
        var finisher = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "Finisher",
            new() { ["CooldownMax"] = 2500, ["DamageAmount"] = 9999, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}"""),
                },
            },
            tags: ["Weapon"],
            instanceId: "p1_finisher");

        p1.Hand.TryPlaceOrShift(compassBlade, 0);
        p1.Hand.TryPlaceOrShift(pureGoldCoin, 1);
        p1.Hand.TryPlaceOrShift(finisher, 2);

        var res = CombatSimulator.Simulate(
            repo,
            p1,
            p2,
            seed: 50701,
            combatType: "PVP",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: false);

        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = RoundTripCombatSim(CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries));

        // 1) 核验帧级 Gold 变更累计值严格等于 PostCombatPermanentSync.PlayerAttributeDeltas["Gold"]（+7，从 1 -> 8）
        var goldFrameUpdates = sim.Frames
            .Where(f => f.PlayerUpdates?.Attributes != null && f.PlayerUpdates.Attributes.ContainsKey(EPlayerAttributeType.Gold))
            .Select(f => f.PlayerUpdates!.Attributes[EPlayerAttributeType.Gold])
            .ToList();
        Assert.NotEmpty(goldFrameUpdates);
        Assert.Equal(1, goldFrameUpdates[0].PreviousValue);
        Assert.Equal(8, goldFrameUpdates[^1].CurrentValue);
        int totalFrameGoldDelta = goldFrameUpdates.Sum(u => u.CurrentValue - u.PreviousValue);
        int expectedPermGoldDelta = (int)Math.Round(res.PostCombatPermanentSync!.PlayerAttributeDeltas["Gold"]);
        Assert.Equal(expectedPermGoldDelta, totalFrameGoldDelta);

        // 2) 核验 Income 变更（5 -> 6）正确落入帧级 PlayerUpdates.Attributes
        var incomeUpdates = sim.Frames
            .Where(f => f.PlayerUpdates?.Attributes != null && f.PlayerUpdates.Attributes.ContainsKey(EPlayerAttributeType.Income))
            .Select(f => f.PlayerUpdates!.Attributes[EPlayerAttributeType.Income])
            .ToList();
        var incomeUpd = Assert.Single(incomeUpdates);
        Assert.Equal(5, incomeUpd.PreviousValue);
        Assert.Equal(6, incomeUpd.CurrentValue);

        // 3) 核验动作事件（CombatSimEventEffectExecuted）：
        //    - compassBlade 每次开火除 PlayerDamage 外精确产出 1 条 PlayerModifyAttribute（共 2 条），且目标为 Player 并携带官方飞币 VFX；
        //    - pureGoldCoin 精确产出 2 条 PlayerModifyAttribute（Gold + Income），不会因 CardFired 重复双发！
        var modAttrEvents = sim.Frames
            .SelectMany(f => f.Events)
            .OfType<CombatSimEventEffectExecuted>()
            .Where(e => e.ActionType == EActionCommandType.PlayerModifyAttribute)
            .ToList();

        var compassGoldEvs = modAttrEvents.Where(e => e.Source?.Value == "p1_compass_blade").ToList();
        Assert.Equal(2, compassGoldEvs.Count);
        Assert.All(compassGoldEvs, e =>
        {
            var tgt = Assert.IsType<EffectTargetPlayer>(e.Target);
            Assert.Equal(ECombatantId.Player, tgt.Target);
            Assert.True(e.VfxIndex.HasValue, "战中生金必须携带官方飞币 VFX 索引以播放飞币动画");
            Assert.Equal(CombatSimConverter.OfficialCoinBurstVfxKey, sim.VfxKeys[e.VfxIndex!.Value]);
        });

        var pureCoinEvs = modAttrEvents.Where(e => e.Source?.Value == "p1_pure_gold_coin").ToList();
        Assert.Equal(2, pureCoinEvs.Count);
        Assert.All(pureCoinEvs, e => Assert.Equal(ECombatantId.Player, Assert.IsType<EffectTargetPlayer>(e.Target).Target));
        Assert.Contains(pureCoinEvs, e => e.VfxIndex.HasValue && sim.VfxKeys[e.VfxIndex.Value] == CombatSimConverter.OfficialCoinBurstVfxKey);

        // 4) 验证 FlipPerspective 镜像同样准确翻转玩家属性更新与目标阵营
        var flipped = CombatSimConverter.FlipPerspective(sim);
        Assert.Contains(flipped.Frames, f =>
            f.OpponentUpdates?.Attributes != null &&
            f.OpponentUpdates.Attributes.TryGetValue(EPlayerAttributeType.Gold, out var g) &&
            g.CurrentValue == 8);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-03：倍率血量首帧基线对齐（默认零虚假差分 + 可选显式基线校准 + TimeMs==0 逆推）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_03_ScaledHealthMultiplier_Frame0BaselineAlignedWithTimeline0_AndOptionalSpawnOverrideCalibrates()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 300, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, initialHealthMax: 400, unlockedHandSlots: 10);

        var striker = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "DailyStriker",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 500, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}"""),
                },
            },
            tags: ["Weapon"],
            instanceId: "p1_daily_striker");
        p1.Hand.TryPlaceOrShift(striker, 0);

        // 模拟每日挑战 titan_vitality（双方 ×2.5 血量：P1 300 -> 750，P2 400 -> 1000）
        var modifiers = new CombatModifiers
        {
            HealthMultiplierP1 = 2.5,
            HealthMultiplierP2 = 2.5,
        };

        var res = CombatSimulator.Simulate(
            repo,
            p1,
            p2,
            seed: 50301,
            combatType: "PVP",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: false,
            modifiers: modifiers);

        Assert.NotEmpty(res.Timeline);
        Assert.Equal(750, res.Timeline[0].P1MaxHp);
        Assert.Equal(1000, res.Timeline[0].P2MaxHp);

        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);

        // 1) 标准路径（RunEngine 已将 Timeline[0] 倍率后 P1MaxHp/P2MaxHp 写入 CombatSpawnSim）：
        //    CreateInitialPlayerBaseline 严格对齐 Timeline[0]（750 / 1000），Frame 0 零虚假 HealthMax 差分
        var baseline = CombatSimConverter.CreateInitialPlayerBaseline(res.Timeline[0], res.Events);
        Assert.Equal(750, baseline.P1MaxHp);
        Assert.Equal(750, baseline.P1Hp);
        Assert.Equal(1000, baseline.P2MaxHp);
        Assert.Equal(1000, baseline.P2Hp);

        var simAligned = CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries);
        var f0Aligned = simAligned.Frames[0];
        Assert.False(f0Aligned.PlayerUpdates?.Attributes.ContainsKey(EPlayerAttributeType.HealthMax) == true,
            "当 CombatSpawnSim 已写入倍率血量时，Frame 0 不应重复发射虚假 HealthMax 差分");
        Assert.False(f0Aligned.OpponentUpdates?.Attributes.ContainsKey(EPlayerAttributeType.HealthMax) == true,
            "当 CombatSpawnSim 已写入倍率血量时，Frame 0 不应重复发射虚假对手 HealthMax 差分");

        // 2) 兜底校准路径（外部调用方传入未乘倍的旧 spawnP1MaxHp=300 / spawnP2MaxHp=400）：
        //    Frame 0 必须自动发射权威 HealthMax 与 Health 校准更新（300->750，400->1000）
        var simCalibrated = CombatSimConverter.ToCombatSim(
            res,
            repo,
            p1Map,
            p2Map,
            p1Entries,
            p2Entries,
            spawnP1MaxHp: 300,
            spawnP2MaxHp: 400);
        var f0Cal = simCalibrated.Frames[0];
        Assert.NotNull(f0Cal.PlayerUpdates);
        Assert.True(f0Cal.PlayerUpdates!.Attributes.TryGetValue(EPlayerAttributeType.HealthMax, out var p1MaxCal));
        Assert.Equal(300, p1MaxCal!.PreviousValue);
        Assert.Equal(750, p1MaxCal.CurrentValue);
        Assert.True(f0Cal.PlayerUpdates.Attributes.TryGetValue(EPlayerAttributeType.Health, out var p1HpCal));
        Assert.Equal(750, p1HpCal!.CurrentValue);

        Assert.NotNull(f0Cal.OpponentUpdates);
        Assert.True(f0Cal.OpponentUpdates!.Attributes.TryGetValue(EPlayerAttributeType.HealthMax, out var p2MaxCal));
        Assert.Equal(400, p2MaxCal!.PreviousValue);
        Assert.Equal(1000, p2MaxCal.CurrentValue);
        Assert.True(f0Cal.OpponentUpdates.Attributes.TryGetValue(EPlayerAttributeType.Health, out var p2HpCal));
        Assert.Equal(1000, p2HpCal!.CurrentValue);

        // 3) TimeMs == 0 开战瞬发 HealthMax 变更逆推校验
        var snapWithStartMaxHp = new CombatTickState
        {
            TimeMs = 0,
            P1Hp = 850,
            P1MaxHp = 850,
            P2Hp = 1000,
            P2MaxHp = 1000,
        };
        var startMaxHpEvs = new List<CombatEvent>
        {
            new()
            {
                TimeMs = 0,
                Type = CombatEventType.PlayerAttributeModified,
                TargetPlayerId = "player",
                Value = 100,
                Detail = CombatEvent.FormatPlayerAttributeModifiedDetail("HealthMax", 750, 850),
            },
        };
        var baselineStartMaxHp = CombatSimConverter.CreateInitialPlayerBaseline(snapWithStartMaxHp, startMaxHpEvs);
        Assert.Equal(750, baselineStartMaxHp.P1MaxHp);
        Assert.Equal(1000, baselineStartMaxHp.P2MaxHp);
    }

    [Fact]
    public void Bug1_InvulnerabilityPotion_TimedPercentDamageReduction_EmitsExpiryAndClearsVisualEffect()
    {
        var repo = Repo.Value;
        var invulnTpl = repo.GetCard("4fdc947b-3646-429e-ba4e-bb693fe44bc2");
        Assert.NotNull(invulnTpl);

        // 1) 完整验证：P2 携带银阶 Invulnerability Potion（7s 触发 1s 100% 无敌，Ammo=1），
        //    P1 每 1s 造成 200 伤害；7s~8s 期间免疫，8s 到期后必须发出 PercentDamageReduction 100->0 且恢复受击死亡。
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Mak, initialHealthMax: 2500, unlockedHandSlots: 10);

        var striker = MakeCustomItem(
            Guid.NewGuid().ToString(),
            "SteadyStriker",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 200, ["SellPrice"] = 2 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}"""),
                },
            },
            tags: ["Weapon"],
            instanceId: "p1_steady_striker");
        p1.Hand.TryPlaceOrShift(striker, 0);

        var invulnCard = new CardInstance(invulnTpl!, CardTier.Silver, "p2_invuln_potion");
        p2.Hand.TryPlaceOrShift(invulnCard, 0);

        var res = CombatSimulator.Simulate(
            repo,
            p1,
            p2,
            seed: 50901,
            combatType: "PVP",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: false);

        // 内核必须未陷入永久无敌：P2 在无敌 1s 到期后被持续打死（P1 胜）
        Assert.Equal("player", res.WinnerPlayerId);
        Assert.Equal(0, res.Player2RemainingHp);

        // 核心事件流必须成对记录 PercentDamageReduction 0->100（开启）与 100->0（1000ms 后 TCombatDurationExpired 到期回落）
        var pdrEvents = res.Events
            .Where(e => e.Type == CombatEventType.PlayerAttributeModified
                     && string.Equals(e.TargetPlayerId, "opponent", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(e.PlayerAttributeName, "PercentDamageReduction", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, pdrEvents.Count);
        Assert.True(pdrEvents[0].TryGetPlayerAttributeModification(out _, out double applyPrev, out double applyCur));
        Assert.Equal(0, applyPrev);
        Assert.Equal(100, applyCur);
        Assert.True(pdrEvents[1].TryGetPlayerAttributeModification(out _, out double expPrev, out double expCur));
        Assert.Equal(100, expPrev);
        Assert.Equal(0, expCur);
        Assert.Equal("TCombatDurationExpired", pdrEvents[1].ActionType);
        Assert.Equal(pdrEvents[0].TimeMs + 1000, pdrEvents[1].TimeMs);

        // 转换后的客户端 CombatSim 帧流必须同样成对下发 OpponentUpdates[PercentDamageReduction] = 100 与 0
        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries);

        var oppPdrFrames = sim.Frames
            .Select((frame, idx) => (idx, upd: frame.OpponentUpdates?.Attributes.TryGetValue(EPlayerAttributeType.PercentDamageReduction, out var u) == true ? u : null))
            .Where(x => x.upd != null)
            .ToList();
        Assert.True(oppPdrFrames.Count >= 2, $"期望至少 2 帧 PercentDamageReduction 更新（开启与到期归零），实际 {oppPdrFrames.Count}");
        Assert.Equal(0, oppPdrFrames[0].upd!.PreviousValue);
        Assert.Equal(100, oppPdrFrames[0].upd!.CurrentValue);
        Assert.Equal(100, oppPdrFrames[1].upd!.PreviousValue);
        Assert.Equal(0, oppPdrFrames[1].upd!.CurrentValue);
        Assert.True(oppPdrFrames[1].idx > oppPdrFrames[0].idx, "无敌到期归零帧必须晚于开启帧");

        // 2) 边界兜底验证：若战斗在无敌未到期时提前结束（例如对手被一击必杀或超时），末帧清理也必须把 PercentDamageReduction 归零
        var syntheticRes = new CombatResult
        {
            WinnerPlayerId = "player",
            Player1RemainingHp = 1000,
            Player2RemainingHp = 0,
            DurationMs = 500,
            Timeline =
            [
                new CombatTickState { TimeMs = 0, P1Hp = 1000, P1MaxHp = 1000, P2Hp = 1000, P2MaxHp = 1000, P2PercentDamageReduction = 0 },
                new CombatTickState { TimeMs = 100, P1Hp = 1000, P1MaxHp = 1000, P2Hp = 1000, P2MaxHp = 1000, P2PercentDamageReduction = 100 },
                new CombatTickState { TimeMs = 500, P1Hp = 1000, P1MaxHp = 1000, P2Hp = 0, P2MaxHp = 1000, P2PercentDamageReduction = 100 },
            ],
            Events =
            [
                new CombatEvent
                {
                    TimeMs = 100,
                    Type = CombatEventType.PlayerAttributeModified,
                    SourcePlayerId = "opponent",
                    TargetPlayerId = "opponent",
                    SourceCardInstanceId = invulnCard.InstanceId,
                    Value = 100,
                    Detail = CombatEvent.FormatPlayerAttributeModifiedDetail("PercentDamageReduction", 0, 100),
                    ActionType = "TActionPlayerModifyAttribute",
                },
            ],
        };
        var simEarlyEnd = CombatSimConverter.ToCombatSim(syntheticRes, repo, p1Map, p2Map, p1Entries, p2Entries);
        var lastFrame = simEarlyEnd.Frames[^1];
        Assert.NotNull(lastFrame.OpponentUpdates);
        Assert.True(lastFrame.OpponentUpdates!.Attributes.TryGetValue(EPlayerAttributeType.PercentDamageReduction, out var endPdr));
        Assert.Equal(100, endPdr!.PreviousValue);
        Assert.Equal(0, endPdr.CurrentValue);
    }
}
