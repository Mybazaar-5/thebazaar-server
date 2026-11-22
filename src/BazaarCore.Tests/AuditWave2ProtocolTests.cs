using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
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
/// Wave 2 Batch 2A（战斗血盾协议、Karnok 视听与战斗协议 P1）专项端到端回归测试：
/// 覆盖 P0-09、P0-10、P0-11、P1-07、P1-08、P1-09、P1-10、P1-11、P1-12、P1-15 及官方 MessagePack 序列化往返。
/// </summary>
public sealed class AuditWave2ProtocolTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());

    private static RuleNode ParseNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;

    private static CardInstance MakeCustomItem(
        string id,
        string name,
        Dictionary<string, double> attrs,
        Dictionary<string, AbilityDefinition>? abilities = null,
        Dictionary<string, AuraDefinition>? auras = null,
        List<string>? tags = null,
        List<string>? hiddenTags = null,
        List<string>? heroes = null,
        Dictionary<string, EnchantmentData>? enchantments = null,
        string? enchantment = null,
        string type = "Item")
    {
        var abIds = abilities?.Keys.ToList() ?? new List<string>();
        var auraIds = auras?.Keys.ToList() ?? new List<string>();
        var tpl = new CardTemplate
        {
            Id = id,
            Type = type,
            StartingTier = "Bronze",
            Size = "Small",
            Tags = tags ?? new List<string>(),
            HiddenTags = hiddenTags ?? new List<string>(),
            Heroes = heroes ?? new List<string> { "Common" },
            Attributes = new Dictionary<string, double>(attrs, StringComparer.OrdinalIgnoreCase),
            Abilities = abilities ?? new Dictionary<string, AbilityDefinition>(),
            Auras = auras ?? new Dictionary<string, AuraDefinition>(),
            Enchantments = enchantments ?? new Dictionary<string, EnchantmentData>(StringComparer.OrdinalIgnoreCase),
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData { AbilityIds = abIds, AuraIds = auraIds },
                ["Silver"] = new TierData { AbilityIds = abIds, AuraIds = auraIds },
                ["Gold"] = new TierData { AbilityIds = abIds, AuraIds = auraIds },
                ["Diamond"] = new TierData { AbilityIds = abIds, AuraIds = auraIds }
            },
            Localization = new LocalizationBlock { Title = new LocalizedText { Text = name } }
        };
        return new CardInstance(tpl, CardTier.Bronze, enchantment, id);
    }

    private static (List<RunState.CardEntry> Entries, Dictionary<string, CardInstance> ById) BuildBoardEntries(
        PlayerState side,
        ECombatantId owner)
    {
        var entries = new List<RunState.CardEntry>();
        var map = new Dictionary<string, CardInstance>(StringComparer.Ordinal);

        void AddList(IEnumerable<CardInstance> cards, EInventorySection section, ECardType cardType)
        {
            int fallbackIdx = 0;
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
                        _ => ECardSize.Small
                    },
                    Tier = (ETier)(int)c.Tier,
                    Owner = owner,
                    Section = section,
                    Socket = (EContainerSocketId)slot,
                    CoreCard = c
                };
                if (!string.IsNullOrEmpty(c.Enchantment) && Enum.TryParse<EEnchantmentType>(c.Enchantment, true, out var ench))
                    entry.Enchantment = ench;
                foreach (var kv in c.BaseAttributes)
                {
                    if (Enum.TryParse<ECardAttributeType>(kv.Key, true, out var at))
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

    // ──────────────────────────────────────────────────────────────────────────
    // P0-09：RegenApplied 与 HealApplied 彻底解耦，禁止误加 HP 或发 EDamageType.Heal 飘字
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P0_09_RegenApplied_DecoupledFromHeal_NoFakeHpGainOrHealFloatingText()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 500, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, initialHealthMax: 500, unlockedHandSlots: 10);

        // 先让 P2 在 500ms 打掉 P1 100 血（确保 P1 处于残血态，若 Regen 误走 Heal 分支一定会错发 Heal 飘字与假加血）
        var oppHit = MakeCustomItem(
            Guid.NewGuid().ToString(), "OppHit",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 100, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        // P1 在 1500ms 施加 25 点再生（仅施加再生，不治疗）
        var regenItem = MakeCustomItem(
            Guid.NewGuid().ToString(), "RegenWand",
            new() { ["CooldownMax"] = 1500, ["RegenApplyAmount"] = 25, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerRegenApply","Duration":{"$type":"TCombatDuration","DurationType":"UntilEndOfCombat"},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });
        // 4000ms 快速结束战斗
        var finisher = MakeCustomItem(
            Guid.NewGuid().ToString(), "Finisher",
            new() { ["CooldownMax"] = 4000, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        p1.Hand.TryPlaceOrShift(regenItem, 0);
        p1.Hand.TryPlaceOrShift(finisher, 1);
        p2.Hand.TryPlaceOrShift(oppHit, 0);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2009, recordTimeline: true, recordEvents: true);

        // 1. Core 事件流：必须发射独立 CombatEventType.RegenApplied，绝不发 HealApplied
        var regenEv = Assert.Single(res.Events, e => e.Type == CombatEventType.RegenApplied && e.SourceCardInstanceId == regenItem.InstanceId);
        Assert.Equal(25, regenEv.Value);
        Assert.DoesNotContain(res.Events, e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == regenItem.InstanceId);

        // 2. CombatSimConverter：绝无 EDamageType.Heal 飘字，仅发 PlayerRegenApply + HealthRegen 属性同步 + ECardStats.RegenAdded
        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries);

        var allPlayerAdjs = sim.Frames
            .Where(f => f.PlayerUpdates != null)
            .SelectMany(f => f.PlayerUpdates!.HealthAdjustments)
            .ToList();
        Assert.DoesNotContain(allPlayerAdjs, a => a.DamageType == EDamageType.Heal);
        Assert.Contains(allPlayerAdjs, a => a.DamageType == EDamageType.Regen && a.Amount == 25);

        Assert.Contains(sim.Frames, f =>
            f.PlayerUpdates?.Attributes.TryGetValue(EPlayerAttributeType.HealthRegen, out var upd) == true && upd.CurrentValue == 25);
        Assert.Contains(sim.Frames.SelectMany(f => f.Events).OfType<CombatSimEventEffectExecuted>(),
            e => e.ActionType == EActionCommandType.PlayerRegenApply && e.Source == new InstanceId(regenItem.InstanceId));

        Assert.True(sim.CardStats.TryGetValue(regenItem.InstanceId, out var stats));
        Assert.Equal(25, stats.GetValueOrDefault(ECardStats.RegenAdded, 0));
        Assert.Equal(0, stats.GetValueOrDefault(ECardStats.HealAdded, 0));

        // 3. 兼容兜底：旧合成 HealApplied + Detail="RegenGain" 同样严禁发 EDamageType.Heal
        var legacyRes = new CombatResult
        {
            WinnerPlayerId = "player",
            LoserPlayerId = "opponent",
            DurationMs = 200,
            Timeline =
            [
                new CombatTickState { TimeMs = 0, P1Hp = 300, P1MaxHp = 500, P2Hp = 500, P2MaxHp = 500 },
                new CombatTickState { TimeMs = 100, P1Hp = 300, P1MaxHp = 500, P1Regen = 15, P2Hp = 0, P2MaxHp = 500 }
            ],
            Events =
            [
                new CombatEvent
                {
                    TimeMs = 50,
                    Type = CombatEventType.HealApplied,
                    SourcePlayerId = "player",
                    TargetPlayerId = "player",
                    SourceCardInstanceId = regenItem.InstanceId,
                    Value = 15,
                    Detail = "RegenGain"
                }
            ]
        };
        var legacySim = CombatSimConverter.ToCombatSim(legacyRes, repo, p1Map, p2Map, p1Entries, p2Entries);
        Assert.DoesNotContain(
            legacySim.Frames.Where(f => f.PlayerUpdates != null).SelectMany(f => f.PlayerUpdates!.HealthAdjustments),
            a => a.DamageType == EDamageType.Heal);
        Assert.Equal(15, legacySim.CardStats[regenItem.InstanceId].GetValueOrDefault(ECardStats.RegenAdded, 0));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P0-10：BurnTick 消费 ShieldAbsorbed / HpDamage 精准拆盾扣血
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P0_10_BurnTick_SplitsShieldAndHpHealthAdjustmentsAccurately()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 500, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, initialHealthMax: 500, unlockedHandSlots: 10);

        // 开战瞬间给 P2 12 盾与 40 灼烧：
        // 500ms BurnTick：mitigation = min(12, (40+1)/2) = 12，dmg = 40 - 12 = 28 -> ShieldAbsorbed = 12, HpDamage = 16
        var opener = MakeCustomItem(
            Guid.NewGuid().ToString(), "BurnShieldSetup",
            new() { ["BurnApplyAmount"] = 40 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        var oppShield = MakeCustomItem(
            Guid.NewGuid().ToString(), "OppShield12",
            new() { ["ShieldApplyAmount"] = 12 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });
        var finisher = MakeCustomItem(
            Guid.NewGuid().ToString(), "Finisher",
            new() { ["CooldownMax"] = 1200, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        p1.Hand.TryPlaceOrShift(opener, 0);
        p1.Hand.TryPlaceOrShift(finisher, 1);
        p2.Hand.TryPlaceOrShift(oppShield, 0);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2010, recordTimeline: true, recordEvents: true);
        var firstBurnTick = res.Events.First(e => e.Type == CombatEventType.BurnTick && e.TargetPlayerId == "opponent");
        Assert.Equal(28, firstBurnTick.Value);
        Assert.Equal(12, firstBurnTick.ShieldAbsorbed);
        Assert.Equal(16, firstBurnTick.HpDamage);

        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries);

        var oppBurnAdjs = sim.Frames
            .Where(f => f.OpponentUpdates != null)
            .SelectMany(f => f.OpponentUpdates!.HealthAdjustments)
            .Where(a => a.DamageType == EDamageType.Burn)
            .ToList();

        Assert.Contains(oppBurnAdjs, a => a.AttributeChanged == EPlayerHealthChangeType.Shield && a.Amount == -12);
        Assert.Contains(oppBurnAdjs, a => a.AttributeChanged == EPlayerHealthChangeType.Health && a.Amount == -16);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P0-11 (A)：Karnok 狂暴立绘变身 + 5 项战斗玩家属性 + ECardStats.RageAdded
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P0_11_Karnok_EnragePortraitSwap_PlayerAttributes_And_RageStats()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "KarnokP1", HeroNames.Karnok, initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "DummyP2", HeroNames.Vanessa, initialHealthMax: 5000, unlockedHandSlots: 10);

        // 1000ms 时施加 100 Rage 立刻触发狂暴（默认 5000ms，6000ms 时退出狂暴并重置立绘）
        var rageTotem = MakeCustomItem(
            Guid.NewGuid().ToString(), "RageTotem",
            new() { ["CooldownMax"] = 1000, ["RageApplyAmount"] = 100, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerRageApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });
        // 8000ms 结束战斗
        var finisher = MakeCustomItem(
            Guid.NewGuid().ToString(), "Finisher",
            new() { ["CooldownMax"] = 8000, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        p1.Hand.TryPlaceOrShift(rageTotem, 0);
        p1.Hand.TryPlaceOrShift(finisher, 1);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2011, recordTimeline: true, recordEvents: true);

        Assert.Equal(HeroNames.Karnok, res.Player1Hero);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.PlayerRageApplied && e.SourceCardInstanceId == rageTotem.InstanceId && e.Value == 100);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.PlayerEnraged && e.TargetPlayerId == "player");
        Assert.Contains(res.Events, e => e.Type == CombatEventType.PlayerEnrageEnded && e.TargetPlayerId == "player");

        // d2ba43c2 自动挂接：狂暴期间 PortraitIndex > 0，结束后复位回 0
        Assert.Contains(res.Timeline, t => t.P1Enraged && t.P1PortraitIndex > 0 && t.P1EnragedDuration > 0 && t.P1EnragedDurationMax == 5000);
        Assert.True(res.Timeline[^1].P1PortraitIndex == 0 && !res.Timeline[^1].P1Enraged, "狂暴结束后立绘应复位至 0");

        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = RoundTripCombatSim(CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries));

        // 1. PortraitKeys 含 Karnok Beast Form Addressables Key，且帧流先后下发变身 Index=0 与复位 Index=null
        Assert.NotNull(sim.PortraitKeys);
        int beastIdx = sim.PortraitKeys.IndexOf(CombatSimConverter.KarnokBeastFormPortraitArtKey);
        Assert.True(beastIdx >= 0, "CombatSim.PortraitKeys 必须包含 Karnok Beast Form 立绘键");

        var portraitUpdates = sim.Frames
            .Where(f => f.PlayerUpdates?.Portrait != null)
            .Select(f => f.PlayerUpdates!.Portrait!)
            .ToList();
        Assert.True(portraitUpdates.Count >= 2, $"应包含变身与复位两次 Portrait 更新，实际 {portraitUpdates.Count}");
        Assert.Equal(beastIdx, portraitUpdates[0].Index);
        Assert.Null(portraitUpdates[^1].Index);

        // 2. 战斗玩家属性补齐 EnragedDuration / EnragedDurationMax 差分
        Assert.Contains(sim.Frames, f => f.PlayerUpdates?.Attributes.ContainsKey(EPlayerAttributeType.EnragedDuration) == true);
        Assert.Contains(sim.Frames, f => f.PlayerUpdates?.Attributes.TryGetValue(EPlayerAttributeType.EnragedDurationMax, out var u) == true && u.CurrentValue == 5000);

        // 3. ECardStats.RageAdded 精准统计
        Assert.True(sim.CardStats.TryGetValue(rageTotem.InstanceId, out var rageStats));
        Assert.Equal(100, rageStats.GetValueOrDefault(ECardStats.RageAdded, 0));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P0-11 (B)：Tempo 消费追加 PlayerTempoRemove + TempoGainCooldownMax + Joy 统计
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P0_11_TempoCostSpent_EmitsPlayerTempoRemove_And_JoyStatsSynced()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "Hero8P1", HeroNames.Hero8, initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "DummyP2", HeroNames.Vanessa, initialHealthMax: 5000, unlockedHandSlots: 10);

        // 开战给 3 Tempo + 15 Joy，随后 1000ms 卡牌消费 2 Tempo 造成伤害
        var tempoJoyStarter = MakeCustomItem(
            Guid.NewGuid().ToString(), "TempoJoyStarter",
            new() { ["TempoApplyAmount"] = 3, ["JoyApplyAmount"] = 15 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerTempoApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerJoyApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            },
            hiddenTags: ["Tempo"]);

        var tempoSpender = MakeCustomItem(
            Guid.NewGuid().ToString(), "TempoSpender",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 50, ["TempoCost"] = 2, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionPlayerDamage",
                        "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" },
                        "Cost": { "$type": "TActionCostPlayerAttribute", "PlayerAttributeType": "Tempo", "Value": { "$type": "TFixedValue", "Value": 2 } }
                    }
                    """)
                }
            },
            hiddenTags: ["Tempo"]);

        var finisher = MakeCustomItem(
            Guid.NewGuid().ToString(), "Finisher",
            new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        p1.Hand.TryPlaceOrShift(tempoJoyStarter, 0);
        p1.Hand.TryPlaceOrShift(tempoSpender, 1);
        p1.Hand.TryPlaceOrShift(finisher, 2);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2012, recordTimeline: true, recordEvents: true);
        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = RoundTripCombatSim(CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries));

        var allEvents = sim.Frames.SelectMany(f => f.Events).ToList();
        Assert.Contains(allEvents.OfType<CombatSimEventCardActionCostSpent>(),
            e => e.ExecutingCard == new InstanceId(tempoSpender.InstanceId) && e.PlayerAttributeSpent == EPlayerAttributeType.Tempo);
        // 必须同步发射 PlayerTempoRemove 且 Target 指向消费方玩家，驱动 BoardUIController.PlayActiveTempoSpendBurst()
        Assert.Contains(allEvents.OfType<CombatSimEventEffectExecuted>(),
            e => e.ActionType == EActionCommandType.PlayerTempoRemove
              && e.Source == new InstanceId(tempoSpender.InstanceId)
              && e.Target is EffectTargetPlayer tp && tp.Target == ECombatantId.Player);

        // TempoGainCooldownMax 与 Joy 属性同步 + JoyAdded 统计
        Assert.Contains(sim.Frames, f => f.PlayerUpdates?.Attributes.TryGetValue(EPlayerAttributeType.TempoGainCooldownMax, out var u) == true && u.CurrentValue == 2000);
        Assert.Contains(sim.Frames, f => f.PlayerUpdates?.Attributes.TryGetValue(EPlayerAttributeType.Joy, out var u) == true && u.CurrentValue == 15);
        Assert.Equal(15, sim.CardStats[tempoJoyStarter.InstanceId].GetValueOrDefault(ECardStats.JoyAdded, 0));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-07：护盾与治疗暴击 isCrit 透传至 CombatEvent 与 HealthAdjustments
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P1_07_ShieldAndHealCrit_PropagatesIsCritToHealthAdjustments()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Pygmalien, initialHealthMax: 500, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Vanessa, initialHealthMax: 500, unlockedHandSlots: 10);

        // 对手 1000ms 打掉 P1 120 血，确保 P1 1500ms 暴击治疗能产生真实有效回血
        var oppDmg = MakeCustomItem(
            Guid.NewGuid().ToString(), "OppHit",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 120, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        // 100% 暴击的护盾+治疗双动作卡（1500ms 开火，位于对手攻击之后）
        var critShieldHeal = MakeCustomItem(
            Guid.NewGuid().ToString(), "CritShieldHeal",
            new() { ["CooldownMax"] = 1500, ["ShieldApplyAmount"] = 30, ["HealAmount"] = 40, ["CritChance"] = 100, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerHeal","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });

        var finisher = MakeCustomItem(
            Guid.NewGuid().ToString(), "Finisher",
            new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        p1.Hand.TryPlaceOrShift(critShieldHeal, 0);
        p1.Hand.TryPlaceOrShift(finisher, 1);
        p2.Hand.TryPlaceOrShift(oppDmg, 0);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2107, recordTimeline: true, recordEvents: true);
        var shEv = Assert.Single(res.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == critShieldHeal.InstanceId);
        var hlEv = Assert.Single(res.Events, e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == critShieldHeal.InstanceId);
        Assert.True(shEv.IsCrit);
        Assert.True(hlEv.IsCrit);
        Assert.Equal(60, shEv.Value);
        Assert.Equal(80, hlEv.Value);

        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries);

        var adjs = sim.Frames.Where(f => f.PlayerUpdates != null).SelectMany(f => f.PlayerUpdates!.HealthAdjustments).ToList();
        Assert.Contains(adjs, a => a.DamageType == EDamageType.Shield && a.AttributeChanged == EPlayerHealthChangeType.Shield && a.Amount == 60 && a.IsCrit);
        Assert.Contains(adjs, a => a.DamageType == EDamageType.Heal && a.AttributeChanged == EPlayerHealthChangeType.Health && a.Amount == 80 && a.IsCrit);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-08：被动反应事件真实 TriggerSource 透传
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P1_08_ReactiveTrigger_PropagatesTrueTriggerSourceCardId()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 1000, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, initialHealthMax: 1000, unlockedHandSlots: 10);

        var cardA = MakeCustomItem(
            Guid.NewGuid().ToString(), "TriggerSourceWeapon",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 25, ["AmmoMax"] = 1 },
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
            tags: ["Weapon"]);

        // 技能 B 监听己方 Weapon 使用后为己方施加 30 盾
        var skillB = MakeCustomItem(
            Guid.NewGuid().ToString(), "ReactiveShieldSkill",
            new() { ["ShieldApplyAmount"] = 30 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","Conditions":{"$type":"TCardConditionalTag","Tags":["Weapon"],"Operator":"Any"}}}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            },
            type: "Skill");

        var finisher = MakeCustomItem(
            Guid.NewGuid().ToString(), "Finisher",
            new() { ["CooldownMax"] = 2500, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        p1.Hand.TryPlaceOrShift(cardA, 0);
        p1.Hand.TryPlaceOrShift(finisher, 1);
        p1.Skills.Add(skillB);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2108, recordTimeline: true, recordEvents: true);
        var reactiveShieldEv = Assert.Single(res.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == skillB.InstanceId);
        Assert.Equal(cardA.InstanceId, reactiveShieldEv.TriggerSourceCardInstanceId);

        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries);

        var allEvents = sim.Frames.SelectMany(f => f.Events).ToList();
        var skillExec = Assert.Single(allEvents.OfType<CombatSimEventEffectExecuted>(),
            e => e.Source == new InstanceId(skillB.InstanceId) && e.ActionType == EActionCommandType.PlayerShieldApply);
        Assert.Equal(new InstanceId(cardA.InstanceId), skillExec.TriggerSource);

        // P2-09：客户端 0 消费的冗余 CombatSimEventEffectTriggered 已移除，技能演出统一由 CombatSimEventEffectExecuted 驱动
        Assert.DoesNotContain(allEvents, e => e is CombatSimEventEffectTriggered);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-09（战内）：CombatSimEventEffectAuraExecuted 生效与撤销差量播报
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P1_09_InCombatAura_EmitsEffectAuraExecuted_OnApplyAndRemoval()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Jules, initialHealthMax: 1000, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Vanessa, initialHealthMax: 1000, unlockedHandSlots: 10);

        // 光环源卡（Slot 0）：给右侧相邻卡 +20 DamageAmount；1000ms 时自毁以验证光环撤销 RemovedFrom
        var auraSource = MakeCustomItem(
            Guid.NewGuid().ToString(), "AuraBoostSource",
            new() { ["CooldownMax"] = 1000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionCardDestroy","Target":{"$type":"TTargetCardSelf"}}""")
                }
            },
            auras: new()
            {
                ["a0"] = new AuraDefinition
                {
                    Id = "a0",
                    ActiveIn = "HandOnly",
                    Action = ParseNode("""
                    {
                        "$type": "TAuraActionCardModifyAttribute",
                        "AttributeType": "DamageAmount",
                        "Operation": "Add",
                        "Target": { "$type": "TTargetCardPositional", "Origin": "Self", "TargetMode": "Neighbor", "IncludeOrigin": false },
                        "Value": { "$type": "TFixedValue", "Value": 20 }
                    }
                    """)
                }
            });

        var boostedWeapon = MakeCustomItem(
            Guid.NewGuid().ToString(), "BoostedWeapon",
            new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        p1.Hand.TryPlaceOrShift(auraSource, 0);
        p1.Hand.TryPlaceOrShift(boostedWeapon, 1);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2109, recordTimeline: true, recordEvents: true);
        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = RoundTripCombatSim(CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries));

        var auraEvents = sim.Frames
            .SelectMany(f => f.Events)
            .OfType<CombatSimEventEffectAuraExecuted>()
            .Where(e => e.Source == new InstanceId(auraSource.InstanceId) && e.EffectId == "a0")
            .ToList();

        Assert.True(auraEvents.Count >= 2, $"应至少包含光环生效与销毁撤销两条事件，实际 {auraEvents.Count}");
        Assert.Contains(auraEvents[0].AppliedTo, t => t is EffectTargetCard c && c.Target == new InstanceId(boostedWeapon.InstanceId));
        Assert.Empty(auraEvents[0].RemovedFrom);
        Assert.Contains(auraEvents[^1].RemovedFrom, t => t is EffectTargetCard c && c.Target == new InstanceId(boostedWeapon.InstanceId));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-10（战内 + CombatResult 接口）：UntilEndOfCombat 记录 + 限时变形到期还原事件
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P1_10_TemporaryTransform_RecordsUntilEndOfCombat_And_TimedRevertEmitsEvent()
    {
        var repo = Repo.Value;
        // 取库内任意一张真实 Small 物品作为变形产物模板
        var targetTpl = repo.CardsById.Values.First(c => c.Type == "Item" && c.Size == "Small" && c.Tags.Count > 0 && Guid.TryParse(c.Id, out _));

        // 场景 A：UntilEndOfCombat 临时变形写入 CombatResult.TemporaryTransforms
        var p1A = new PlayerState("player", "P1A", HeroNames.Vanessa, initialHealthMax: 1000, unlockedHandSlots: 10);
        var p2A = new PlayerState("opponent", "P2A", HeroNames.Dooley, initialHealthMax: 1000, unlockedHandSlots: 10);
        var tempTransformer = MakeCustomItem(
            Guid.NewGuid().ToString(), "TempTransformer",
            new() { ["CooldownMax"] = 1000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode($$"""
                    {
                        "$type": "TActionCardTransform",
                        "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                        "Target": { "$type": "TTargetCardSelf" },
                        "SpawnContext": {
                            "$type": "TSpawnContextQuery",
                            "Limit": { "$type": "TFixedValue", "Value": 1 },
                            "Groups": [
                                {
                                    "$type": "TSpawnGroup",
                                    "Filters": [
                                        { "$type": "TSpawnFilterIdList", "Ids": ["{{targetTpl.Id}}"] }
                                    ]
                                }
                            ]
                        }
                    }
                    """)
                }
            });
        var finisherA = MakeCustomItem(
            Guid.NewGuid().ToString(), "FinisherA",
            new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        p1A.Hand.TryPlaceOrShift(tempTransformer, 0);
        p1A.Hand.TryPlaceOrShift(finisherA, 1);

        var resA = CombatSimulator.Simulate(repo, p1A, p2A, seed: 2110, recordTimeline: true, recordEvents: true);
        var rec = Assert.Single(resA.TemporaryTransforms);
        Assert.Equal("player", rec.OwnerPlayerId);
        Assert.Equal(tempTransformer.InstanceId, rec.OriginalCardInstanceId);
        Assert.Equal(tempTransformer.Template.Id, rec.OriginalTemplateId);
        Assert.NotEmpty(rec.TransformedInstanceIds);
        // 绝不误进入永久变形表
        Assert.True(resA.PostCombatPermanentSync == null || !resA.PostCombatPermanentSync.PermanentReplacements.ContainsKey(tempTransformer.InstanceId));

        // 场景 B：TCombatDuration 战内 1000ms 限时变形到期还原并经 CombatSimConverter 发射 CombatSimEventCardTransformReverted
        var p1B = new PlayerState("player", "P1B", HeroNames.Vanessa, initialHealthMax: 1000, unlockedHandSlots: 10);
        var p2B = new PlayerState("opponent", "P2B", HeroNames.Dooley, initialHealthMax: 1000, unlockedHandSlots: 10);
        var timedOrig = MakeCustomItem(
            Guid.NewGuid().ToString(), "TimedOrig",
            new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode($$"""
                    {
                        "$type": "TActionCardTransform",
                        "Duration": { "$type": "TCombatDuration", "DurationInMs": 1000 },
                        "Target": { "$type": "TTargetCardSelf" },
                        "SpawnContext": {
                            "$type": "TSpawnContextQuery",
                            "Limit": { "$type": "TFixedValue", "Value": 1 },
                            "Groups": [
                                {
                                    "$type": "TSpawnGroup",
                                    "Filters": [
                                        { "$type": "TSpawnFilterIdList", "Ids": ["{{targetTpl.Id}}"] }
                                    ]
                                }
                            ]
                        }
                    }
                    """)
                }
            });
        var finisherB = MakeCustomItem(
            Guid.NewGuid().ToString(), "FinisherB",
            new() { ["CooldownMax"] = 3000, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        p1B.Hand.TryPlaceOrShift(timedOrig, 0);
        p1B.Hand.TryPlaceOrShift(finisherB, 1);

        var resB = CombatSimulator.Simulate(repo, p1B, p2B, seed: 2111, recordTimeline: true, recordEvents: true);
        var revertCoreEv = Assert.Single(resB.Events, e => e.Type == CombatEventType.CardTransformReverted && e.TargetCardInstanceId == timedOrig.InstanceId);
        Assert.NotNull(revertCoreEv.RevertedTransformedInstanceIds);
        Assert.NotEmpty(revertCoreEv.RevertedTransformedInstanceIds!);

        var (p1EntriesB, p1MapB) = BuildBoardEntries(p1B, ECombatantId.Player);
        var (p2EntriesB, p2MapB) = BuildBoardEntries(p2B, ECombatantId.Opponent);
        var simB = RoundTripCombatSim(CombatSimConverter.ToCombatSim(resB, repo, p1MapB, p2MapB, p1EntriesB, p2EntriesB));

        var revertSimEv = Assert.Single(simB.Frames.SelectMany(f => f.Events).OfType<CombatSimEventCardTransformReverted>());
        Assert.Equal(timedOrig.InstanceId, revertSimEv.OriginalCard.InstanceId);
        Assert.NotEmpty(revertSimEv.TransformedCardInstanceIds);

        // 同时验证 P1-12 变形新卡首帧在 CardUpdates 中完整初始化了 Tags/Heroes 元数据
        var transformedId = new InstanceId(revertSimEv.TransformedCardInstanceIds[0]);
        var newCardInitUpdate = simB.Frames
            .Where(f => f.CardUpdates.TryGetValue(transformedId, out var cu) && cu.Placement != null)
            .Select(f => f.CardUpdates[transformedId])
            .FirstOrDefault();
        Assert.NotNull(newCardInitUpdate);
        Assert.NotNull(newCardInitUpdate!.Tags);
        Assert.NotEmpty(newCardInitUpdate.Tags!);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-11（战内）：附魔移除与限时非基模属性到期彻底清除 -> CombatSimEventCardAttributeDeleted
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P1_11_EnchantRemoveAndTimedExpiry_EmitCombatSimEventCardAttributeDeleted()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 1000, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, initialHealthMax: 1000, unlockedHandSlots: 10);

        // 带 Fiery 附魔（提供独有属性 BurnApplyAmount=10）的物品，以及对其移除附魔 + 施加 1000ms 限时 Lifesteal 的卡
        var enchantedCard = MakeCustomItem(
            Guid.NewGuid().ToString(), "EnchantedBlade",
            new() { ["CooldownMax"] = 5000, ["DamageAmount"] = 10 },
            enchantments: new()
            {
                ["Fiery"] = new EnchantmentData
                {
                    Attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["BurnApplyAmount"] = 10 }
                }
            },
            enchantment: "Fiery");

        var cleanser = MakeCustomItem(
            Guid.NewGuid().ToString(), "DispelAndTempBuff",
            new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionCardEnchantRemove","Target":{"$type":"TTargetCardXMost","TargetSection":"SelfHand","TargetMode":"LeftMostCard"}}""")
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
                        "Duration": { "$type": "TCombatDuration", "DurationInMs": 1000 },
                        "Target": { "$type": "TTargetCardXMost", "TargetSection": "SelfHand", "TargetMode": "LeftMostCard" },
                        "Value": { "$type": "TFixedValue", "Value": 100 }
                    }
                    """)
                }
            });

        var finisher = MakeCustomItem(
            Guid.NewGuid().ToString(), "Finisher",
            new() { ["CooldownMax"] = 3000, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        p1.Hand.TryPlaceOrShift(enchantedCard, 0);
        p1.Hand.TryPlaceOrShift(cleanser, 1);
        p1.Hand.TryPlaceOrShift(finisher, 2);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2111, recordTimeline: true, recordEvents: true);
        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = RoundTripCombatSim(CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries));

        var deletedEvs = sim.Frames.SelectMany(f => f.Events).OfType<CombatSimEventCardAttributeDeleted>().ToList();
        Assert.Contains(deletedEvs, e => e.InstanceId == new InstanceId(enchantedCard.InstanceId) && e.AttributeType == ECardAttributeType.BurnApplyAmount);
        Assert.Contains(deletedEvs, e => e.InstanceId == new InstanceId(enchantedCard.InstanceId) && e.AttributeType == ECardAttributeType.Lifesteal);
        // 基模固有的 DamageAmount 绝不可误报删除
        Assert.DoesNotContain(deletedEvs, e => e.InstanceId == new InstanceId(enchantedCard.InstanceId) && e.AttributeType == ECardAttributeType.DamageAmount);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-12：战斗中动态 Tags / HiddenTags / Heroes 差分回写与变形新卡初始化
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P1_12_DynamicCardTagsAndTransformedCardMetadata_SyncedInCardUpdates()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 1000, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, initialHealthMax: 1000, unlockedHandSlots: 10);

        var plainCard = MakeCustomItem(
            Guid.NewGuid().ToString(), "PlainRelic",
            new() { ["CooldownMax"] = 5000 },
            tags: ["Tool"],
            heroes: ["Vanessa"]);

        var tagGranter = MakeCustomItem(
            Guid.NewGuid().ToString(), "TagGranter",
            new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                        "$type": "TActionCardAddTagsList",
                        "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                        "Target": { "$type": "TTargetCardXMost", "TargetSection": "SelfHand", "TargetMode": "LeftMostCard" },
                        "Tags": ["Aquatic", "Weapon"]
                    }
                    """)
                }
            });

        var finisher = MakeCustomItem(
            Guid.NewGuid().ToString(), "Finisher",
            new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        p1.Hand.TryPlaceOrShift(plainCard, 0);
        p1.Hand.TryPlaceOrShift(tagGranter, 1);
        p1.Hand.TryPlaceOrShift(finisher, 2);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2112, recordTimeline: true, recordEvents: true);
        var (p1Entries, p1Map) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2Entries, p2Map) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = RoundTripCombatSim(CombatSimConverter.ToCombatSim(res, repo, p1Map, p2Map, p1Entries, p2Entries));

        var plainId = new InstanceId(plainCard.InstanceId);
        // 第 0 帧无变化时不冗余下发 Tags
        Assert.False(sim.Frames[0].CardUpdates.TryGetValue(plainId, out var firstCu) && firstCu.Tags != null);

        // 1000ms 追加标签后应下发包含 Tool + Aquatic + Weapon 的完整集合
        var tagUpdate = sim.Frames
            .Where(f => f.CardUpdates.TryGetValue(plainId, out var cu) && cu.Tags != null)
            .Select(f => f.CardUpdates[plainId])
            .FirstOrDefault();
        Assert.NotNull(tagUpdate);
        Assert.Contains(ECardTag.Tool, tagUpdate!.Tags!);
        Assert.Contains(ECardTag.Aquatic, tagUpdate.Tags!);
        Assert.Contains(ECardTag.Weapon, tagUpdate.Tags!);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P1-15：满血零回血不发 HealthAdjustments，残血部分回血按净回血量同步且保留名义统计
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void P1_15_FullHpHealLifestealRegen_SkipHealthAdjustments_PartialHealUsesEffectiveAmount()
    {
        var repo = Repo.Value;

        // 场景 A：全程满血（Heal + Lifesteal + RegenTick）-> 零条 Heal/Regen HealthAdjustments，但 HealAdded 统计保留名义量
        var p1Full = new PlayerState("player", "P1Full", HeroNames.Pygmalien, initialHealthMax: 500, unlockedHandSlots: 10);
        p1Full.Attributes["HealthRegen"] = 10;
        var p2Full = new PlayerState("opponent", "P2Full", HeroNames.Dooley, initialHealthMax: 500, unlockedHandSlots: 10);

        var fullHealer = MakeCustomItem(
            Guid.NewGuid().ToString(), "FullHealer",
            new() { ["CooldownMax"] = 1000, ["HealAmount"] = 50, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerHeal","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });
        var vampBlade = MakeCustomItem(
            Guid.NewGuid().ToString(), "VampBlade",
            new() { ["CooldownMax"] = 1500, ["DamageAmount"] = 20, ["Lifesteal"] = 100, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        var finisherA = MakeCustomItem(
            Guid.NewGuid().ToString(), "FinisherA",
            new() { ["CooldownMax"] = 2500, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        p1Full.Hand.TryPlaceOrShift(fullHealer, 0);
        p1Full.Hand.TryPlaceOrShift(vampBlade, 1);
        p1Full.Hand.TryPlaceOrShift(finisherA, 2);

        var resFull = CombatSimulator.Simulate(repo, p1Full, p2Full, seed: 2115, recordTimeline: true, recordEvents: true);
        var fullHealEv = Assert.Single(resFull.Events, e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == fullHealer.InstanceId);
        Assert.Equal(50, fullHealEv.Value);
        Assert.Equal(0, fullHealEv.EffectiveHpRestored);

        var vampHealEv = Assert.Single(resFull.Events, e => e.Type == CombatEventType.HealApplied && e.Detail == "Lifesteal");
        Assert.Equal(20, vampHealEv.Value);
        Assert.Equal(0, vampHealEv.EffectiveHpRestored);

        var regenTicksFull = resFull.Events.Where(e => e.Type == CombatEventType.RegenTick && e.TargetPlayerId == "player").ToList();
        Assert.NotEmpty(regenTicksFull);
        Assert.All(regenTicksFull, r => Assert.Equal(0, r.EffectiveHpRestored));

        var (p1EntriesF, p1MapF) = BuildBoardEntries(p1Full, ECombatantId.Player);
        var (p2EntriesF, p2MapF) = BuildBoardEntries(p2Full, ECombatantId.Opponent);
        var simFull = CombatSimConverter.ToCombatSim(resFull, repo, p1MapF, p2MapF, p1EntriesF, p2EntriesF);
        var fullPlayerAdjs = simFull.Frames.Where(f => f.PlayerUpdates != null).SelectMany(f => f.PlayerUpdates!.HealthAdjustments).ToList();
        Assert.DoesNotContain(fullPlayerAdjs, a => a.DamageType is EDamageType.Heal or EDamageType.Regen);
        Assert.Equal(50, simFull.CardStats[fullHealer.InstanceId].GetValueOrDefault(ECardStats.HealAdded, 0));

        // 场景 B：掉 30 血后施加名义 80 治疗 -> HealthAdjustments 精确发射 +30，而 HealAdded 仍计 80
        var p1Part = new PlayerState("player", "P1Part", HeroNames.Pygmalien, initialHealthMax: 500, unlockedHandSlots: 10);
        var p2Part = new PlayerState("opponent", "P2Part", HeroNames.Dooley, initialHealthMax: 500, unlockedHandSlots: 10);

        var oppTap = MakeCustomItem(
            Guid.NewGuid().ToString(), "OppTap30",
            new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 30, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        var bigHealer = MakeCustomItem(
            Guid.NewGuid().ToString(), "BigHealer80",
            new() { ["CooldownMax"] = 1500, ["HealAmount"] = 80, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerHeal","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });
        var finisherB = MakeCustomItem(
            Guid.NewGuid().ToString(), "FinisherB",
            new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 9999 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        p1Part.Hand.TryPlaceOrShift(bigHealer, 0);
        p1Part.Hand.TryPlaceOrShift(finisherB, 1);
        p2Part.Hand.TryPlaceOrShift(oppTap, 0);

        var resPart = CombatSimulator.Simulate(repo, p1Part, p2Part, seed: 2116, recordTimeline: true, recordEvents: true);
        var partHealEv = Assert.Single(resPart.Events, e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == bigHealer.InstanceId);
        Assert.Equal(80, partHealEv.Value);
        Assert.Equal(30, partHealEv.EffectiveHpRestored);

        var (p1EntriesP, p1MapP) = BuildBoardEntries(p1Part, ECombatantId.Player);
        var (p2EntriesP, p2MapP) = BuildBoardEntries(p2Part, ECombatantId.Opponent);
        var simPart = CombatSimConverter.ToCombatSim(resPart, repo, p1MapP, p2MapP, p1EntriesP, p2EntriesP);
        var healAdj = Assert.Single(
            simPart.Frames.Where(f => f.PlayerUpdates != null).SelectMany(f => f.PlayerUpdates!.HealthAdjustments),
            a => a.DamageType == EDamageType.Heal);
        Assert.Equal(30, healAdj.Amount);
        Assert.Equal(80, simPart.CardStats[bigHealer.InstanceId].GetValueOrDefault(ECardStats.HealAdded, 0));
    }
}
