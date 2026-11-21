using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V4 核心测试套件（一）：验证四层 Duration 判定、TCombatDuration 到期撤销、
/// 小时/天临时修正清理、战斗永久成长无损回写及 20ms 全量 VFX 帧采集。
/// </summary>
public sealed class V4DurationAndPermSyncTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());

    private static JsonElement JsonFixedValue(double v)
        => JsonDocument.Parse($"{{\"$type\":\"TFixedValue\",\"Value\":{v.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}").RootElement.Clone();

    private static JsonElement JsonNumber(double v)
        => JsonDocument.Parse(v.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();

    private static RuleNode TargetSelfCard() => new() { Type = "TTargetCardSelf" };
    private static RuleNode TargetSelfPlayer() => new() { Type = "TTargetPlayerRelative", TargetMode = "Self" };
    private static RuleNode TargetOpponentPlayer() => new() { Type = "TTargetPlayerRelative", TargetMode = "Opponent" };

    private static RuleNode DeterminantDuration(string durationType) => new()
    {
        Type = "TDeterminantDuration",
        DurationType = durationType
    };

    private static RuleNode CombatTimedDuration(int ms) => new()
    {
        Type = "TCombatDuration",
        DurationInMs = ms
    };

    private static CardTemplate MakeItemTemplate(
        string id,
        string name,
        double dmg = 20,
        double cdMs = 1500,
        params AbilityDefinition[] abilities)
    {
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = name,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tags = ["Weapon"],
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["DamageAmount"] = dmg,
                        ["CooldownMax"] = cdMs,
                    },
                    AbilityIds = abilities.Select(a => a.Id).ToList(),
                },
                ["Silver"] = new TierData
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["DamageAmount"] = dmg * 2,
                        ["CooldownMax"] = cdMs,
                    },
                    AbilityIds = abilities.Select(a => a.Id).ToList(),
                },
            },
        };
        foreach (var ab in abilities)
            tpl.Abilities[ab.Id] = ab;
        return tpl;
    }

    private static CardInstance MakeWeaponWithSelfBuff(
        string id,
        double baseDmg,
        double buffPerFire,
        RuleNode? duration,
        int cooldownMs = 1000)
    {
        var fireDmg = new AbilityDefinition
        {
            Id = "ab_dmg",
            Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
            Action = new RuleNode
            {
                Type = "TActionPlayerDamage",
                Target = TargetOpponentPlayer(),
            },
        };
        var scaleDmg = new AbilityDefinition
        {
            Id = "ab_scale",
            Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
            Action = new RuleNode
            {
                Type = "TActionCardModifyAttribute",
                AttributeType = "DamageAmount",
                Operation = "Add",
                Duration = duration,
                Target = TargetSelfCard(),
                RawValueNode = JsonFixedValue(buffPerFire),
            },
        };
        return new CardInstance(MakeItemTemplate(id, id, baseDmg, cooldownMs, fireDmg, scaleDmg));
    }

    [Fact]
    public void DurationNull_InCombat_ShouldWriteToPermanentLayer_AndSyncBackToOriginalP1()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, 500, 20, 10, 4);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, 400, 20, 10, 4);

        var growingWeapon = MakeWeaponWithSelfBuff("perm_scaler", baseDmg: 20, buffPerFire: 15, duration: null, cooldownMs: 1000);
        p1.Hand.TryAddAuto(growingWeapon);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 7, syncPermanentChanges: true);

        Assert.True(res.DurationMs >= 3000);
        Assert.NotNull(res.PostCombatPermanentSync);
        Assert.True(res.PostCombatPermanentSync!.Applied);
        // 战斗中每次开火均触发永久 +15 伤害（Duration == null），战后应保留在 originalP1 的 PermanentAdd 中
        Assert.True(growingWeapon.PermanentAdd.GetValueOrDefault("DamageAmount", 0) >= 30,
            $"永久 DamageAmount 增量应 >= 30，实际为 {growingWeapon.PermanentAdd.GetValueOrDefault("DamageAmount", 0)}");
        Assert.True(growingWeapon.GetAttribute("DamageAmount") >= 50);
        Assert.Empty(growingWeapon.CombatAdd);
    }

    [Fact]
    public void DurationUntilEndOfCombat_ShouldRemainTemporary_AndBeDiscardedAfterCombat()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, 500, 20, 10, 4);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, 400, 20, 10, 4);

        var tempWeapon = MakeWeaponWithSelfBuff(
            "combat_temp_scaler",
            baseDmg: 25,
            buffPerFire: 20,
            duration: DeterminantDuration("UntilEndOfCombat"),
            cooldownMs: 1000);
        p1.Hand.TryAddAuto(tempWeapon);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 11, syncPermanentChanges: true);

        // 战中快照应观察到伤害临时上涨（>25），但战后回写不会污染 originalP1
        Assert.Contains(res.Timeline, frame => frame.Cards.Any(c => c.InstanceId == tempWeapon.InstanceId && c.DamageAmount > 25));
        Assert.Equal(0, tempWeapon.PermanentAdd.GetValueOrDefault("DamageAmount", 0));
        Assert.Equal(25, tempWeapon.GetAttribute("DamageAmount"));
    }

    [Fact]
    public void DurationTCombatDuration_ShouldExpireAndRevertAtTimestamp()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, 2000, 20, 10, 4);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, 2000, 20, 10, 4);

        // 仅在开战时给予 +40 伤害，持续 1500ms（TCombatDuration）
        var timedAb = new AbilityDefinition
        {
            Id = "ab_timed",
            Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
            Action = new RuleNode
            {
                Type = "TActionCardModifyAttribute",
                AttributeType = "DamageAmount",
                Operation = "Add",
                Duration = CombatTimedDuration(1500),
                Target = TargetSelfCard(),
                RawValueNode = JsonFixedValue(40),
            },
        };
        var timedCard = new CardInstance(MakeItemTemplate("timed_sword", "Timed Sword", 10, 1000, timedAb));
        p1.Hand.TryAddAuto(timedCard);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 13, syncPermanentChanges: true);

        var at500 = res.Timeline.First(t => t.TimeMs >= 500).Cards.First(c => c.InstanceId == timedCard.InstanceId);
        var at2000 = res.Timeline.First(t => t.TimeMs >= 2000).Cards.First(c => c.InstanceId == timedCard.InstanceId);

        Assert.Equal(50, at500.DamageAmount);
        Assert.Equal(10, at2000.DamageAmount);
        Assert.Equal(10, timedCard.GetAttribute("DamageAmount"));
    }

    [Fact]
    public void DurationUntilEndOfHour_ShouldClearOnHourBoundary_WhileKeepingDayAndPermanent()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 300, 20, 10, 4);
        var card = new CardInstance(MakeItemTemplate("layered_card", "Layered Card", 10, 2000));
        player.Hand.TryAddAuto(card);

        card.ModifyAttribute("DamageAmount", "Add", 5, AbilityExecutor.ResolveDurationTarget(null, RuleScope.OutOfCombat));
        card.ModifyAttribute("DamageAmount", "Add", 7, AbilityExecutor.ResolveDurationTarget(DeterminantDuration("UntilEndOfHour"), RuleScope.OutOfCombat));
        card.ModifyAttribute("DamageAmount", "Add", 11, AbilityExecutor.ResolveDurationTarget(DeterminantDuration("UntilEndOfDay"), RuleScope.OutOfCombat));
        player.ModifyAttribute("Income", "Add", 3, DurationTarget.Hour);
        player.ModifyAttribute("Gold", "Add", 4, DurationTarget.Day);

        Assert.Equal(33, card.GetAttribute("DamageAmount"));
        Assert.Equal(8, player.GetAttribute("Income"));
        Assert.Equal(14, player.GetAttribute("Gold"));

        OutOfCombatController.OnHourStarted(player, repo, new Random(1));

        Assert.Equal(26, card.GetAttribute("DamageAmount")); // 10 + 5(perm) + 11(day)
        Assert.Empty(card.HourAdd);
        Assert.Equal(5, player.GetAttribute("Income"));
        Assert.Equal(14, player.GetAttribute("Gold"));
    }

    [Fact]
    public void DurationUntilEndOfDay_ShouldSurviveHourStart_AndClearOnDayStart()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Pygmalien, 300, 20, 10, 4);
        var card = new CardInstance(MakeItemTemplate("day_card", "Day Card", 12, 2000));
        player.Hand.TryAddAuto(card);

        card.ModifyAttribute("DamageAmount", "Add", 4, DurationTarget.Permanent);
        card.ModifyAttribute("DamageAmount", "Add", 9, DurationTarget.Day);
        card.ModifyAttribute("DamageAmount", "Multiply", 2.0, DurationTarget.Day);
        player.ModifyAttribute("HealthMax", "Add", 50, DurationTarget.Day);

        OutOfCombatController.OnHourStarted(player, repo, new Random(2));
        Assert.Equal((12 + 4 + 9) * 2.0, card.GetAttribute("DamageAmount"));
        Assert.Equal(350, player.HealthMax);

        OutOfCombatController.OnDayStarted(player, repo, new Random(2));
        Assert.Equal(16, card.GetAttribute("DamageAmount")); // 12 + 4(perm)
        Assert.Empty(card.DayAdd);
        Assert.Empty(card.DayMult);
        Assert.Equal(300, player.HealthMax);
    }

    [Fact]
    public void PlayerModifyAttribute_InCombat_DurationNullVsUntilEndOfCombat_ShouldSyncOnlyPermanent()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Pygmalien, 500, 20, 10, 4);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Vanessa, 200, 20, 10, 4);

        var abPermGold = new AbilityDefinition
        {
            Id = "ab_gold",
            Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
            Action = new RuleNode
            {
                Type = "TActionPlayerModifyAttribute",
                AttributeType = "Gold",
                Operation = "Add",
                Duration = null,
                Target = TargetSelfPlayer(),
                RawValueNode = JsonFixedValue(6),
            },
        };
        var abPermMaxHp = new AbilityDefinition
        {
            Id = "ab_hpmax",
            Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
            Action = new RuleNode
            {
                Type = "TActionPlayerModifyAttribute",
                AttributeType = "HealthMax",
                Operation = "Add",
                Duration = null,
                Target = TargetSelfPlayer(),
                RawValueNode = JsonFixedValue(50),
            },
        };
        var abTempIncome = new AbilityDefinition
        {
            Id = "ab_temp_inc",
            Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
            Action = new RuleNode
            {
                Type = "TActionPlayerModifyAttribute",
                AttributeType = "Income",
                Operation = "Add",
                Duration = DeterminantDuration("UntilEndOfCombat"),
                Target = TargetSelfPlayer(),
                RawValueNode = JsonFixedValue(9),
            },
        };
        var abStrike = new AbilityDefinition
        {
            Id = "ab_hit",
            Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
            Action = new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponentPlayer(), ReferenceValue = new RuleNode { Type = "TFixedValue", RawValueNode = JsonFixedValue(100) } },
        };

        p1.Hand.TryAddAuto(new CardInstance(MakeItemTemplate("eco_comb", "Eco", 100, 1000, abPermGold, abPermMaxHp, abTempIncome, abStrike)));
        CombatSimulator.Simulate(repo, p1, p2, seed: 3, syncPermanentChanges: true);

        Assert.Equal(16, p1.Gold);
        Assert.Equal(550, p1.HealthMax);
        Assert.Equal(5, p1.Income); // UntilEndOfCombat 不回写
    }

    [Fact]
    public void CombatEnchantAndUpgrade_PermanentVsTemporary_ShouldSyncOnlyPermanent()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, 500, 20, 10, 4);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Vanessa, 200, 20, 10, 4);

        var permTarget = new CardInstance(MakeItemTemplate("perm_target", "Perm Target", 50, 1000,
            new AbilityDefinition
            {
                Id = "hit",
                Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                Action = new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponentPlayer() },
            }));
        var tempTarget = new CardInstance(MakeItemTemplate("temp_target", "Temp Target", 10, 2000));

        var permEnchAb = new AbilityDefinition
        {
            Id = "ab_pe",
            Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
            Action = new RuleNode
            {
                Type = "TActionCardEnchant",
                Enchantment = "Deadly",
                Duration = null,
                Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" },
            },
        };
        var permUpgAb = new AbilityDefinition
        {
            Id = "ab_pu",
            Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
            Action = new RuleNode
            {
                Type = "TActionCardUpgrade",
                Duration = null,
                Target = TargetSelfCard(),
            },
        };

        var supporter = new CardInstance(MakeItemTemplate("sup_upg", "Sup Upg", 0, 0, permEnchAb, permUpgAb));
        // 先放 permTarget + supporter 测试永久升阶与永久附魔；tempTarget 使用临时方法验证不回写
        p1.Hand.TryAddAuto(permTarget);
        p1.Hand.TryAddAuto(supporter);
        p1.Hand.TryAddAuto(tempTarget);

        // 也测试模拟过程中给 tempTarget 施加临时附魔与临时升阶（通过克隆侧）
        var simP1 = p1.CloneForCombat();
        var simTemp = simP1.Hand.Cards.First(c => c.InstanceId == tempTarget.InstanceId);
        simTemp.ApplyEnchantment("Fiery", preventOverride: false, isTemporary: true);
        simTemp.TryUpgrade(isTemporary: true);

        CombatSimulator.SyncPermanentChangesBack(simP1, p1);
        Assert.Equal("Bronze", tempTarget.Tier.ToString());
        Assert.Null(tempTarget.Enchantment);

        // 真实模拟中由 Duration==null 触发永久升阶与永久附魔
        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 5, syncPermanentChanges: true);
        Assert.Equal(CardTier.Silver, supporter.Tier);
        Assert.Equal("Deadly", permTarget.Enchantment);
        Assert.Equal(CardTier.Silver, supporter.PermanentTier);
    }

    [Fact]
    public void CombatExtraTagsAndQuestProgress_ShouldSyncBackAndUnlockReward()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Mak, 800, 20, 10, 4);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Vanessa, 250, 20, 10, 4);

        var questTpl = MakeItemTemplate("quest_blade", "Quest Blade", 40, 1000,
            new AbilityDefinition
            {
                Id = "hit",
                Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                Action = new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponentPlayer() },
            },
            new AbilityDefinition
            {
                Id = "tag_perm",
                Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                Action = new RuleNode
                {
                    Type = "TActionCardAddTagsList",
                    Tags = ["Relic"],
                    Duration = null,
                    Target = TargetSelfCard(),
                },
            },
            new AbilityDefinition
            {
                Id = "tag_temp",
                Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                Action = new RuleNode
                {
                    Type = "TActionCardAddTagsList",
                    Tags = ["Food"],
                    Duration = DeterminantDuration("UntilEndOfCombat"),
                    Target = TargetSelfCard(),
                },
            });

        questTpl.Quests =
        [
            new QuestDefinition
            {
                Entries =
                [
                    new QuestEntry
                    {
                        AttributeType = "Quest_1",
                        Target = 2,
                        Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                        Reward = new QuestReward
                        {
                            Attributes = new Dictionary<string, double> { ["DamageAmount"] = 30 },
                        },
                    },
                ],
            },
        ];

        var questCard = new CardInstance(questTpl);
        p1.Hand.TryAddAuto(questCard);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 19, syncPermanentChanges: true);

        Assert.Contains("Relic", questCard.ExtraTags);
        Assert.DoesNotContain("Food", questCard.ExtraTags);
        Assert.True(questCard.HasTag("Relic"));
        Assert.False(questCard.HasTag("Food"));
        Assert.Contains("Quest_1", questCard.CompletedQuests);
        Assert.True(questCard.QuestProgress.GetValueOrDefault("Quest_1", 0) >= 2);
        Assert.Equal(70, questCard.GetAttribute("DamageAmount")); // 40 + 30 quest reward
        Assert.Contains(res.Events, e => e.Type == CombatEventType.QuestUpdated);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.QuestCompleted);
    }

    [Fact]
    public void CombatSpawnCard_NonTempVsTemporary_ShouldSyncOnlyPermanentSpawn()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, 500, 20, 10, 4);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, 200, 20, 10, 4);

        const string gumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
        const string chocolateId = "1a3d4cde-333c-4936-b147-263d73f6d200";

        var permSpawn = new AbilityDefinition
        {
            Id = "ab_perm_spawn",
            Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
            Action = new RuleNode
            {
                Type = "TActionGameSpawnCards",
                Duration = null,
                SpawnContext = new RuleNode
                {
                    Type = "TSpawnContextQuery",
                    SelectionMethod = "Sequential",
                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(1) },
                    Groups =
                    [
                        new RuleNode
                        {
                            Type = "TSpawnGroup",
                            Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [gumballId] }],
                            SelectionMethod = "Sequential",
                            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(1) },
                        },
                    ],
                },
            },
        };

        var tempSpawn = new AbilityDefinition
        {
            Id = "ab_temp_spawn",
            Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
            Action = new RuleNode
            {
                Type = "TActionGameSpawnCards",
                Duration = DeterminantDuration("UntilEndOfCombat"),
                SpawnContext = new RuleNode
                {
                    Type = "TSpawnContextQuery",
                    SelectionMethod = "Sequential",
                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(1) },
                    Groups =
                    [
                        new RuleNode
                        {
                            Type = "TSpawnGroup",
                            Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [chocolateId] }],
                            SelectionMethod = "Sequential",
                            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(1) },
                        },
                    ],
                },
            },
        };

        var spawner = new CardInstance(MakeItemTemplate("combat_spawner", "Spawner", 60, 1000,
            permSpawn,
            tempSpawn,
            new AbilityDefinition
            {
                Id = "hit",
                Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                Action = new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponentPlayer() },
            }));
        p1.Hand.TryAddAuto(spawner);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 23, syncPermanentChanges: true);

        Assert.Contains(p1.Hand.Cards, c => c.Template.Id == gumballId);
        Assert.DoesNotContain(p1.Hand.Cards, c => c.Template.Id == chocolateId);
        Assert.Single(res.PostCombatPermanentSync!.SpawnedCards);
    }

    [Fact]
    public void CombatPermanentTransform_ShouldReplaceOriginalCardAndRecordTemplateId()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Mak, 500, 20, 10, 4);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Vanessa, 200, 20, 10, 4);

        const string targetTplId = "6965d291-610d-46b7-a10d-83578b0e1981"; // Dog

        var transformAb = new AbilityDefinition
        {
            Id = "ab_trans",
            Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
            Action = new RuleNode
            {
                Type = "TActionCardTransform",
                Duration = null,
                Target = TargetSelfCard(),
                SpawnContext = new RuleNode
                {
                    Type = "TSpawnContextQuery",
                    Groups =
                    [
                        new RuleNode
                        {
                            Type = "TSpawnGroup",
                            Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [targetTplId] }],
                            SelectionMethod = "Sequential",
                            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonFixedValue(1) },
                        },
                    ],
                },
            },
        };

        var morphCard = new CardInstance(MakeItemTemplate("morph_src", "Morph Source", 50, 1000, transformAb));
        string origId = morphCard.InstanceId;
        p1.Hand.TryAddAuto(morphCard);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 29, syncPermanentChanges: true);

        Assert.Equal(targetTplId, morphCard.TransformReplacementTemplateId);
        Assert.DoesNotContain(morphCard, p1.Hand.Cards);
        var replaced = p1.Hand.Cards.FirstOrDefault(c => c.Template.Id == targetTplId);
        Assert.NotNull(replaced);
        Assert.Equal(origId, replaced!.TransformedFromInstanceId);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.CardTransformed && e.Detail == $"{origId}->{targetTplId}");
    }

    [Fact]
    public void CombatResultSyncPermanentChangesBack_CalledTwice_ShouldBeIdempotent()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, 500, 20, 10, 4);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, 250, 20, 10, 4);

        var scaler = MakeWeaponWithSelfBuff("idempotent_weapon", 25, 10, duration: null, cooldownMs: 1000);
        p1.Hand.TryAddAuto(scaler);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 31, syncPermanentChanges: false);
        Assert.Equal(25, scaler.GetAttribute("DamageAmount")); // 未回写前保持原值

        var sync1 = CombatSimulator.SyncPermanentChangesBack(res, p1);
        double afterFirst = scaler.GetAttribute("DamageAmount");
        var sync2 = CombatSimulator.SyncPermanentChangesBack(res, p1);
        double afterSecond = scaler.GetAttribute("DamageAmount");

        Assert.True(sync1.Applied);
        Assert.True(afterFirst > 25);
        Assert.Equal(afterFirst, afterSecond);
        Assert.Same(sync1, sync2);
    }

    [Fact]
    public void RealCard_Langxian_OnFightEnded_ShouldPermanentlyGrowDamage()
    {
        var repo = Repo.Value;
        var langxianTpl = repo.GetCard("53099361-599f-4c1a-ad4a-d2fbb491b756");
        Assert.NotNull(langxianTpl);

        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 4);
        var langxian = new CardInstance(langxianTpl!);
        player.Hand.TryAddAuto(langxian);

        AuraEngine.RecalculateAuras(player, null, RuleScope.OutOfCombat, 1, 1, repo);
        double dmgBefore = langxian.GetAttribute("DamageAmount");

        var opp = new PlayerState("opp", "Opp", HeroNames.Dooley, 250, 20, 10, 4);
        var batch = OutOfCombatController.OnFightEnded(player, "PVP", "Win", repo, new Random(1), opponent: opp);
        double dmgAfter = langxian.GetAttribute("DamageAmount");

        Assert.True(dmgAfter > dmgBefore, $"Langxian 胜场后 DamageAmount 应永久成长: before={dmgBefore}, after={dmgAfter}");
        Assert.True(langxian.PermanentAdd.GetValueOrDefault("Custom_1", 0) > 0);
        Assert.Contains(langxian, batch.AffectedCards);
    }

    [Fact]
    public void Combat20msCaptureSnapshot_ShouldRecordAllNewVfxAttributes()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, 800, 20, 10, 4);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Dooley, 800, 20, 10, 4);

        var tpl1 = MakeItemTemplate("vfx_hero", "VFX Hero", 35, 2000,
            new AbilityDefinition
            {
                Id = "ab_dmg",
                Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                Action = new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponentPlayer() },
            },
            new AbilityDefinition
            {
                Id = "ab_haste",
                Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                Action = new RuleNode
                {
                    Type = "TActionCardHaste",
                    Target = TargetSelfCard(),
                    RawValueNode = JsonFixedValue(1000),
                },
            },
            new AbilityDefinition
            {
                Id = "ab_fly",
                Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                Action = new RuleNode
                {
                    Type = "TActionCardFlyingStart",
                    Target = TargetSelfCard(),
                },
            },
            new AbilityDefinition
            {
                Id = "ab_heat",
                Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                Action = new RuleNode
                {
                    Type = "TActionCardModifyAttribute",
                    AttributeType = "Heated",
                    Operation = "Add",
                    Duration = DeterminantDuration("UntilEndOfCombat"),
                    Target = TargetSelfCard(),
                    RawValueNode = JsonFixedValue(1),
                },
            });
        tpl1.Tiers!["Bronze"].Attributes["CritChance"] = 25;
        tpl1.Tiers!["Bronze"].Attributes["Multicast"] = 2;
        tpl1.Tiers!["Bronze"].Attributes["Lifesteal"] = 1;
        tpl1.Tiers!["Bronze"].Attributes["AmmoMax"] = 3;
        tpl1.Tiers!["Bronze"].Attributes["SellPrice"] = 6;
        tpl1.Tiers!["Bronze"].Attributes["ShieldApplyAmount"] = 12;

        var c1 = new CardInstance(tpl1);
        p1.Hand.TryAddAuto(c1);

        var tpl2 = MakeItemTemplate("vfx_opp", "VFX Opp", 15, 2000,
            new AbilityDefinition
            {
                Id = "ab_freeze",
                Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                Action = new RuleNode
                {
                    Type = "TActionCardFreeze",
                    Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
                    RawValueNode = JsonFixedValue(500),
                },
            },
            new AbilityDefinition
            {
                Id = "ab_slow",
                Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                Action = new RuleNode
                {
                    Type = "TActionCardSlow",
                    Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
                    RawValueNode = JsonFixedValue(800),
                },
            },
            new AbilityDefinition
            {
                Id = "ab_chill",
                Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                Action = new RuleNode
                {
                    Type = "TActionCardModifyAttribute",
                    AttributeType = "Chilled",
                    Operation = "Add",
                    Duration = DeterminantDuration("UntilEndOfCombat"),
                    Target = TargetSelfCard(),
                    RawValueNode = JsonFixedValue(1),
                },
            });
        var c2 = new CardInstance(tpl2);
        p2.Hand.TryAddAuto(c2);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 42);
        Assert.NotEmpty(res.Timeline);

        var firstFrameC1 = res.Timeline[0].Cards.First(c => c.InstanceId == c1.InstanceId);
        Assert.Equal(3, firstFrameC1.AmmoMax);
        Assert.Equal(2000, firstFrameC1.CooldownMaxMs);
        Assert.Equal(35, firstFrameC1.DamageAmount);
        Assert.Equal(12, firstFrameC1.ShieldApplyAmount);
        Assert.Equal(25, firstFrameC1.CritChance);
        Assert.Equal(2, firstFrameC1.Multicast);
        Assert.Equal(1, firstFrameC1.Lifesteal);
        Assert.Equal(6, firstFrameC1.SellPrice);

        var c1Frames = res.Timeline.SelectMany(f => f.Cards).Where(c => c.InstanceId == c1.InstanceId).ToList();
        Assert.Contains(c1Frames, c => c.IsFlying);
        Assert.Contains(c1Frames, c => c.Heated);
        Assert.Contains(c1Frames, c => c.HasteMs > 0);
        Assert.Contains(c1Frames, c => c.FreezeMs > 0);
        Assert.Contains(c1Frames, c => c.SlowMs > 0);
        Assert.Contains(c1Frames, c => c.Counter > 0);

        var c2Frames = res.Timeline.SelectMany(f => f.Cards).Where(c => c.InstanceId == c2.InstanceId).ToList();
        Assert.Contains(c2Frames, c => c.Chilled);
    }

    [Theory]
    [InlineData(null, "InCombat", DurationTarget.Permanent)]
    [InlineData(null, "OutOfCombat", DurationTarget.Permanent)]
    [InlineData("UntilEndOfCombat", "InCombat", DurationTarget.Combat)]
    [InlineData("TCombatDuration", "InCombat", DurationTarget.Combat)]
    [InlineData("UntilEndOfHour", "OutOfCombat", DurationTarget.Hour)]
    [InlineData("UntilEndOfDay", "OutOfCombat", DurationTarget.Day)]
    public void ResolveDurationTarget_ShouldMapAllDurationTypesAcrossScopes(
        string? durSpec,
        string scopeStr,
        DurationTarget expected)
    {
        RuleNode? durNode = durSpec switch
        {
            null => null,
            "TCombatDuration" => CombatTimedDuration(1000),
            _ => DeterminantDuration(durSpec),
        };
        var scope = scopeStr == "InCombat" ? RuleScope.InCombat : RuleScope.OutOfCombat;

        var actual = AbilityExecutor.ResolveDurationTarget(durNode, scope);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PlayerState_ClearEndOfHourAndDay_ShouldOnlyClearTargetLayer()
    {
        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 300, 20, 10, 4);
        player.ModifyAttribute("Gold", "Add", 4, DurationTarget.Permanent);
        player.ModifyAttribute("Gold", "Add", 5, DurationTarget.Hour);
        player.ModifyAttribute("Gold", "Add", 7, DurationTarget.Day);
        Assert.Equal(26, player.Gold);

        player.ClearEndOfHourModifiers();
        Assert.Equal(21, player.Gold);
        Assert.Empty(player.HourAdd);
        Assert.NotEmpty(player.DayAdd);

        player.ClearEndOfDayModifiers();
        Assert.Equal(14, player.Gold);
        Assert.Empty(player.DayAdd);
    }
}
