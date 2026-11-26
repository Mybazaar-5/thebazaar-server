using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarServer.Data;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Stage 7 实机反馈 4 类核心 Bug 触类旁通系统性回归测试：
/// - Bug 1：全部 PercentDamageReduction 来源（6 张 TCombatDuration 限时无敌卡、叠层无敌、5 张减伤光环、Hunker Down 与提前结束清零）；
/// - Bug 2：全部 21 张可玩直效 EventEncounter 免空房间瞬发 + OnHourStarted 衍生卡嵌套拆包（零同包混发、零非 Choice Hour==6）；
/// - Bug 3：Day 10 微型世界远征完整 H1->H2->H3(Sprout)->H4(Bananniboss)->H5(Ethergy Nexus)->H6 闭环、Depleted Device 充能变形 Sizeinator 与全部 5 套远征日程校验；
/// - Bug 4：全 8 名伙伴（Partner Portal -> 伙伴技能 -> 40 名商人含 Jay Jay / Aila 刷出伙伴物品 -> 25% 折扣光环与实扣 -> Day 2+ 伙伴熔炉解锁与均衡权重 -> 图书馆导师 -> 快照往返）。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class Wave5D_RealPlaySystemicFixesTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => SharedGameDataFixture.Shared.Pool);
    private static readonly Lazy<List<BazaarServer.Data.MonsterTemplate>> Monsters =
        new(() => SharedGameDataFixture.Shared.CreateMonstersCopy());
    private static readonly Lazy<LevelUpTable> LevelUps =
        new(() => SharedGameDataFixture.Shared.LevelUps);
    private static readonly Lazy<GameModeConfig> Mode =
        new(() => SharedGameDataFixture.Shared.Mode);

    private static (RunState Run, RunEngine Engine) CreateStartedRun(
        EHero hero = EHero.Vanessa,
        uint day = 2,
        uint hour = 1,
        int gold = 100,
        int healthMax = 1000,
        int seed = 57001,
        ushort unlockedSlots = 0b11_1111_1111)
    {
        var run = new RunState
        {
            RunId = "w5d_" + Guid.NewGuid().ToString("N")[..10],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            Day = day,
            Hour = hour,
            State = ERunState.Choice,
            UnlockedSlots = unlockedSlots,
            CustomRules = new CustomRunRuleSet().CloneNormalized(),
            RngSeed = seed,
        };
        run.HandGrid.UnlockedMask = unlockedSlots;
        run.PlayerAttributes[EPlayerAttributeType.Gold] = gold;
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = healthMax;
        run.PlayerAttributes[EPlayerAttributeType.Health] = healthMax;
        run.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
        run.PlayerAttributes[EPlayerAttributeType.Level] = 1;

        var engine = new RunEngine(
            run,
            Pool.Value,
            Monsters.Value,
            LevelUps.Value,
            Mode.Value,
            Repo.Value,
            new PvpGhostPool());

        typeof(RunEngine)
            .GetProperty(nameof(RunEngine.StartSelectionDone))!
            .SetValue(engine, true);

        return (run, engine);
    }

    private static RunState.CardEntry InjectChoiceEncounter(
        RunState run,
        string templateId,
        ECardType cardType = ECardType.EventEncounter)
    {
        var poolTpl = Pool.Value.Get(templateId);
        var coreTpl = Repo.Value.GetCard(templateId);
        var entry = new RunState.CardEntry
        {
            InstanceId = "enc_" + Guid.NewGuid().ToString("N")[..8],
            TemplateId = Guid.Parse(templateId),
            Type = cardType,
            Size = ECardSize.Medium,
            Tier = ShopEngine.ParseTier(poolTpl?.StartingTier ?? coreTpl?.StartingTier),
            Owner = null,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.State = ERunState.Choice;
        run.CurrentEncounterId = null;
        run.Cards[entry.InstanceId] = entry;
        run.SelectionSet.Clear();
        run.SelectionSet.Add(entry.InstanceId);
        return entry;
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        string templateId,
        ETier tier,
        EInventorySection section,
        EContainerSocketId socket,
        string? instanceId = null,
        Dictionary<ECardAttributeType, int>? attrOverrides = null)
    {
        var coreTpl = Repo.Value.GetCard(templateId)!;
        var poolTpl = Pool.Value.Get(templateId);
        var core = new CardInstance(
            coreTpl,
            ShopEngine.ToCoreTier(tier),
            null,
            instanceId ?? ("itm_" + Guid.NewGuid().ToString("N")[..8]))
        {
            StartSlot = (int)socket,
        };
        if (attrOverrides != null)
        {
            foreach (var (k, v) in attrOverrides)
                core.PermanentAdd[k.ToString()] = v;
        }
        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = socket,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, Repo.Value.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        var grid = section == EInventorySection.Stash ? run.StashGrid : run.HandGrid;
        grid.UnlockedMask = section == EInventorySection.Stash ? 1023 : run.UnlockedSlots;
        Assert.True(grid.TryPlaceOrShift(core, (int)socket), $"无法将测试卡 {templateId} 放置到 {section}:{socket}");
        entry.Socket = (EContainerSocketId)core.StartSlot;
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static RunState.CardEntry AddOwnedSkill(
        RunState run,
        string templateId,
        ETier tier,
        EContainerSocketId socket,
        string? instanceId = null)
    {
        var coreTpl = Repo.Value.GetCard(templateId)!;
        var poolTpl = Pool.Value.Get(templateId);
        var core = new CardInstance(
            coreTpl,
            ShopEngine.ToCoreTier(tier),
            null,
            instanceId ?? ("skl_" + Guid.NewGuid().ToString("N")[..8]))
        {
            StartSlot = (int)socket,
        };
        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = null,
            Socket = socket,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, Repo.Value.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static RunState.CardEntry InjectSelectionSkill(RunState run, string templateId, ETier tier = ETier.Legendary)
    {
        var coreTpl = Repo.Value.GetCard(templateId)!;
        var poolTpl = Pool.Value.Get(templateId);
        var core = new CardInstance(
            coreTpl,
            ShopEngine.ToCoreTier(tier),
            null,
            "cand_skl_" + Guid.NewGuid().ToString("N")[..8]);
        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = tier,
            Owner = null,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, Repo.Value.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[entry.InstanceId] = entry;
        run.SelectionSet.Add(entry.InstanceId);
        return entry;
    }

    private static int CountOwnedItems(RunState run)
        => run.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item && c.Section is EInventorySection.Hand or EInventorySection.Stash);

    private static bool HasOwnedSkill(RunState run, string skillTemplateId)
    {
        var g = Guid.Parse(skillTemplateId);
        return run.Cards.Values.Any(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill && c.TemplateId == g);
    }

    private static int ComputeUndiscountedBaseBuyPrice(CardTemplate coreTpl, ETier tier)
    {
        var freshCore = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), null, "price_probe");
        var probeEntry = new RunState.CardEntry
        {
            InstanceId = freshCore.InstanceId,
            TemplateId = Guid.Parse(coreTpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            CoreCard = freshCore,
        };
        return RunState.ComputeCardBuyPrice(probeEntry, freshCore, Repo.Value.GameMode.StandardPrices);
    }

    private static void InitRunExclusionSet(RunState run)
    {
        if (run.ExclusionSetInitialized) return;
        foreach (string id in Repo.Value.GameMode.ExclusionSet)
        {
            if (!string.IsNullOrWhiteSpace(id))
                run.ExclusionSet.Add(id);
        }
        run.ExclusionSetInitialized = true;
    }

    private static (List<RunState.CardEntry> Entries, Dictionary<string, CardInstance> ById) BuildBoardEntries(
        PlayerState side,
        ECombatantId owner)
    {
        var entries = new List<RunState.CardEntry>();
        var byId = new Dictionary<string, CardInstance>(StringComparer.Ordinal);
        int slot = 0;
        foreach (var c in side.Hand.Cards)
        {
            byId[c.InstanceId] = c;
            entries.Add(new RunState.CardEntry
            {
                InstanceId = c.InstanceId,
                TemplateId = Guid.Parse(c.Template.Id),
                Type = ECardType.Item,
                Size = ShopEngine.ParseSize(c.Template.Size),
                Tier = Enum.TryParse<ETier>(c.Tier.ToString(), true, out var t) ? t : ETier.Bronze,
                Owner = owner,
                Section = EInventorySection.Hand,
                Socket = (EContainerSocketId)Math.Min(slot, 9),
                CoreCard = c,
            });
            slot += c.Template.CardSize.SlotCount();
        }
        foreach (var sk in side.Skills)
        {
            byId[sk.InstanceId] = sk;
            entries.Add(new RunState.CardEntry
            {
                InstanceId = sk.InstanceId,
                TemplateId = Guid.Parse(sk.Template.Id),
                Type = ECardType.Skill,
                Size = ECardSize.Small,
                Tier = Enum.TryParse<ETier>(sk.Tier.ToString(), true, out var t) ? t : ETier.Bronze,
                Owner = owner,
                CoreCard = sk,
            });
        }
        return (entries, byId);
    }

    private static CardInstance MakeSteadyWeapon(
        string id,
        string name,
        int damage,
        int cooldownMs,
        int critChance = 0,
        string? instanceId = null)
    {
        var attrs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["CooldownMax"] = cooldownMs,
            ["DamageAmount"] = damage,
            ["CritChance"] = critChance,
        };
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = name,
            Type = "Item",
            StartingTier = "Bronze",
            Size = "Small",
            Tags = ["Weapon"],
            HiddenTags = ["Damage"],
            Heroes = ["Common"],
            Attributes = new Dictionary<string, double>(attrs, StringComparer.OrdinalIgnoreCase),
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" },
                    },
                    ActiveIn = "HandOnly",
                },
            },
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new Dictionary<string, double>(attrs, StringComparer.OrdinalIgnoreCase),
                    AbilityIds = ["0"],
                },
            },
        };
        return new CardInstance(tpl, CardTier.Bronze, enchantment: null, instanceId: instanceId);
    }

    // =========================================================================
    // Bug 1：全部 PercentDamageReduction 来源（限时无敌 / 叠层 / 光环 / 全战减伤）
    // =========================================================================

    [Fact]
    public void Bug1_AllTimedInvulnerabilityItems_ActivateBlockDamageAndExpireCleanly()
    {
        var repo = Repo.Value;

        // 1) Button Shield（TTriggerOnCardFired，Diamond 4000ms 冷却、1000ms+ 无敌）
        {
            var buttonShieldTpl = repo.GetCard("9b40ee52-8c2b-4d83-851d-75905c511e41")!;
            var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 5000, unlockedHandSlots: 10);
            var p2 = new PlayerState("opponent", "P2", HeroNames.Vanessa, initialHealthMax: 1500, unlockedHandSlots: 10);
            p1.Hand.TryPlaceOrShift(MakeSteadyWeapon("11111111-1111-4111-8111-111111111101", "TestDagger", damage: 250, cooldownMs: 1000, instanceId: "p1_w1"), 0);
            var bs = new CardInstance(buttonShieldTpl, CardTier.Diamond, enchantment: null, instanceId: "p2_bs");
            bs.PermanentAdd["FlatCooldownReduction"] = 2500; // 4000 -> 1500ms
            p2.Hand.TryPlaceOrShift(bs, 0);

            var res = CombatSimulator.Simulate(repo, p1, p2, seed: 57101, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false);
            Assert.Equal("player", res.WinnerPlayerId);
            Assert.Equal(0, res.Player2RemainingHp);

            var pdrEvs = res.Events
                .Where(e => e.Type == CombatEventType.PlayerAttributeModified
                         && e.TargetPlayerId == "opponent"
                         && string.Equals(e.PlayerAttributeName, "PercentDamageReduction", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(pdrEvs.Count >= 2, $"Button Shield 应成对触发无敌开启与到期，实际事件数={pdrEvs.Count}");
            Assert.Contains(pdrEvs, e => e.ActionType == "TCombatDurationExpired" && e.TryGetPlayerAttributeModification(out _, out _, out double cur) && cur == 0);

            var (p1E, p1M) = BuildBoardEntries(p1, ECombatantId.Player);
            var (p2E, p2M) = BuildBoardEntries(p2, ECombatantId.Opponent);
            var sim = CombatSimConverter.ToCombatSim(res, repo, p1M, p2M, p1E, p2E);
            var pdrFrames = sim.Frames
                .Select(f => f.OpponentUpdates?.Attributes.TryGetValue(EPlayerAttributeType.PercentDamageReduction, out var u) == true ? u : null)
                .Where(u => u != null)
                .ToList();
            Assert.Contains(pdrFrames, u => u!.CurrentValue == 100);
            Assert.Equal(0, pdrFrames[^1]!.CurrentValue);
        }

        // 2) Memento Mori（TTriggerOnPlayerDied，Gold 1000ms 无敌：免死触发后在 1000ms 到期归零，随后被击杀）
        {
            var mementoTpl = repo.GetCard("34618f7b-f262-4d49-b395-de92bef0508c")!;
            var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 5000, unlockedHandSlots: 10);
            var p2 = new PlayerState("opponent", "P2", HeroNames.Mak, initialHealthMax: 200, unlockedHandSlots: 10);
            p1.Hand.TryPlaceOrShift(MakeSteadyWeapon("11111111-1111-4111-8111-111111111102", "TestHammer", damage: 250, cooldownMs: 1000, instanceId: "p1_w2"), 0);
            p2.Hand.TryPlaceOrShift(new CardInstance(mementoTpl, CardTier.Gold, enchantment: null, instanceId: "p2_mm"), 0);

            var res = CombatSimulator.Simulate(repo, p1, p2, seed: 57102, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false);
            Assert.Equal("player", res.WinnerPlayerId);
            Assert.Equal(0, res.Player2RemainingHp);
            Assert.True(res.DurationMs >= 2000, $"Memento Mori 应在 1000ms 免死触发后至少存活至无敌到期（2000ms+），实际 DurationMs={res.DurationMs}");
            var pdrEvs = res.Events
                .Where(e => e.Type == CombatEventType.PlayerAttributeModified
                         && e.TargetPlayerId == "opponent"
                         && string.Equals(e.PlayerAttributeName, "PercentDamageReduction", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(pdrEvs.Count >= 2, $"Memento Mori 应记录无敌开启与到期归零，实际={pdrEvs.Count}");
            Assert.Contains(pdrEvs, e => e.ActionType == "TCombatDurationExpired");
        }

        // 3) Staff of the Moose（TTriggerOnPlayerAttributeChanged 跌破半血触发 1000ms 无敌，到期后被击杀）
        {
            var mooseTpl = repo.GetCard("c135f23d-b8b4-4aa8-9a11-b288ae05908c")!;
            var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 5000, unlockedHandSlots: 10);
            var p2 = new PlayerState("opponent", "P2", HeroNames.Pygmalien, initialHealthMax: 1000, unlockedHandSlots: 10);
            p1.Hand.TryPlaceOrShift(MakeSteadyWeapon("11111111-1111-4111-8111-111111111103", "TestBlade", damage: 600, cooldownMs: 1000, instanceId: "p1_w3"), 0);
            p2.Hand.TryPlaceOrShift(new CardInstance(mooseTpl, CardTier.Gold, enchantment: null, instanceId: "p2_moose"), 0);

            var res = CombatSimulator.Simulate(repo, p1, p2, seed: 57103, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false);
            Assert.Equal("player", res.WinnerPlayerId);
            Assert.Equal(0, res.Player2RemainingHp);
            var pdrEvs = res.Events
                .Where(e => e.Type == CombatEventType.PlayerAttributeModified
                         && e.TargetPlayerId == "opponent"
                         && string.Equals(e.PlayerAttributeName, "PercentDamageReduction", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(pdrEvs.Count >= 2, $"Staff of the Moose 应触发半血无敌与 1s 到期归零，实际={pdrEvs.Count}");
            Assert.Contains(pdrEvs, e => e.ActionType == "TCombatDurationExpired");
        }

        // 4) Ghillie Suit（TTriggerOnCardCritted 首次暴击触发 1000ms 无敌，到期后被击杀）
        {
            var ghillieTpl = repo.GetCard("d25ccf21-f789-4e06-8c38-b7f5f47e66af")!;
            var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 5000, unlockedHandSlots: 10);
            var p2 = new PlayerState("opponent", "P2", HeroNames.Vanessa, initialHealthMax: 600, unlockedHandSlots: 10);
            p1.Hand.TryPlaceOrShift(MakeSteadyWeapon("11111111-1111-4111-8111-111111111104", "TestAxe", damage: 250, cooldownMs: 1000, instanceId: "p1_w4"), 0);
            p2.Hand.TryPlaceOrShift(new CardInstance(ghillieTpl, CardTier.Bronze, enchantment: null, instanceId: "p2_ghillie"), 0);
            p2.Hand.TryPlaceOrShift(MakeSteadyWeapon("11111111-1111-4111-8111-111111111105", "CritTrigger", damage: 10, cooldownMs: 1500, critChance: 100, instanceId: "p2_crit"), 2);

            var res = CombatSimulator.Simulate(repo, p1, p2, seed: 57104, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false);
            Assert.Equal("player", res.WinnerPlayerId);
            Assert.Equal(0, res.Player2RemainingHp);
            var pdrEvs = res.Events
                .Where(e => e.Type == CombatEventType.PlayerAttributeModified
                         && e.TargetPlayerId == "opponent"
                         && string.Equals(e.PlayerAttributeName, "PercentDamageReduction", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(pdrEvs.Count >= 2, $"Ghillie Suit 应触发暴击无敌与 1s 到期归零，实际={pdrEvs.Count}");
            Assert.Contains(pdrEvs, e => e.ActionType == "TCombatDurationExpired");
        }

        // 5) Ancient Locket（TTriggerOnBeforeCardDestroyed 被摧毁时触发 2000ms 无敌，到期后被击杀）
        {
            var locketTpl = repo.GetCard("d641c57b-ab42-4b2f-b4f4-addd18eb7100")!;
            var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 5000, unlockedHandSlots: 10);
            var p2 = new PlayerState("opponent", "P2", HeroNames.Karnok, initialHealthMax: 1500, unlockedHandSlots: 10);
            p1.Hand.TryPlaceOrShift(MakeSteadyWeapon("11111111-1111-4111-8111-111111111106", "TestSpear", damage: 250, cooldownMs: 1000, instanceId: "p1_w6"), 0);
            var destroyerTpl = new CardTemplate
            {
                Id = "11111111-1111-4111-8111-111111111199",
                InternalName = "TestDestroyer",
                Type = "Item",
                StartingTier = "Bronze",
                Size = "Small",
                Tags = ["Weapon"],
                Heroes = ["Common"],
                Attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 1200, ["AmmoMax"] = 1 },
                Abilities = new Dictionary<string, AbilityDefinition>
                {
                    ["0"] = new AbilityDefinition
                    {
                        Id = "0",
                        Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                        Action = new RuleNode
                        {
                            Type = "TActionCardDestroy",
                            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
                        },
                        ActiveIn = "HandOnly",
                    },
                },
                Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Bronze"] = new TierData
                    {
                        Attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 1200, ["AmmoMax"] = 1 },
                        AbilityIds = ["0"],
                    },
                },
            };
            p1.Hand.TryPlaceOrShift(new CardInstance(destroyerTpl, CardTier.Bronze, enchantment: null, instanceId: "p1_destroyer"), 1);
            p2.Hand.TryPlaceOrShift(new CardInstance(locketTpl, CardTier.Silver, enchantment: null, instanceId: "p2_locket"), 0);

            var res = CombatSimulator.Simulate(repo, p1, p2, seed: 57105, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false);
            Assert.Equal("player", res.WinnerPlayerId);
            Assert.Equal(0, res.Player2RemainingHp);
            var pdrEvs = res.Events
                .Where(e => e.Type == CombatEventType.PlayerAttributeModified
                         && e.TargetPlayerId == "opponent"
                         && string.Equals(e.PlayerAttributeName, "PercentDamageReduction", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(pdrEvs.Count >= 2, $"Ancient Locket 应触发被摧毁无敌与 2s 到期归零，实际={pdrEvs.Count}");
            Assert.Contains(pdrEvs, e => e.ActionType == "TCombatDurationExpired");
        }
    }

    [Fact]
    public void Bug1_OverlappingTimedInvulnerability_StaysAt100UntilLastExpires_ThenRevertsTo0()
    {
        var repo = Repo.Value;
        var invulnTpl = repo.GetCard("4fdc947b-3646-429e-ba4e-bb693fe44bc2")!;

        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "P2", HeroNames.Mak, initialHealthMax: 1000, unlockedHandSlots: 10);
        p1.Hand.TryPlaceOrShift(MakeSteadyWeapon("11111111-1111-4111-8111-111111111107", "FastDagger", damage: 200, cooldownMs: 1000, instanceId: "p1_fast"), 0);

        // 两瓶错峰 500ms 的银阶无敌药水（AmmoMax=1，基础冷却 7000ms）：
        // pot1 (FlatCDR=5500 -> 1500ms 触发，窗口 [1500, 2500]) 与 pot2 (FlatCDR=5000 -> 2000ms 触发，窗口 [2000, 3000])；
        // 2500ms 首瓶到期仍须保持 100%，3000ms 次瓶到期后精确归零
        var pot1 = new CardInstance(invulnTpl, CardTier.Silver, enchantment: null, instanceId: "p2_pot1");
        pot1.PermanentAdd["FlatCooldownReduction"] = 5500;
        var pot2 = new CardInstance(invulnTpl, CardTier.Silver, enchantment: null, instanceId: "p2_pot2");
        pot2.PermanentAdd["FlatCooldownReduction"] = 5000;
        p2.Hand.TryPlaceOrShift(pot1, 0);
        p2.Hand.TryPlaceOrShift(pot2, 1);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 57106, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false);
        Assert.Equal("player", res.WinnerPlayerId);
        Assert.Equal(0, res.Player2RemainingHp);

        var snapAt2700 = res.Timeline.Last(t => t.TimeMs <= 2700);
        var snapAt3200 = res.Timeline.Last(t => t.TimeMs <= 3200);
        Assert.Equal(100, snapAt2700.P2PercentDamageReduction);
        Assert.Equal(0, snapAt3200.P2PercentDamageReduction);

        var (p1E, p1M) = BuildBoardEntries(p1, ECombatantId.Player);
        var (p2E, p2M) = BuildBoardEntries(p2, ECombatantId.Opponent);
        var sim = CombatSimConverter.ToCombatSim(res, repo, p1M, p2M, p1E, p2E);
        var pdrFrames = sim.Frames
            .Select(f => f.OpponentUpdates?.Attributes.TryGetValue(EPlayerAttributeType.PercentDamageReduction, out var u) == true ? u : null)
            .Where(u => u != null)
            .ToList();
        Assert.True(pdrFrames.Count >= 2);
        Assert.Equal(100, pdrFrames[0]!.CurrentValue);
        Assert.Equal(0, pdrFrames[^1]!.CurrentValue);
    }

    [Fact]
    public void Bug1_AuraAndUntilEndOfCombatPercentDamageReduction_SyncedOnTimelineAndClearedOnLastFrame()
    {
        var repo = Repo.Value;

        // 1) 光环减伤：Colossal Claw (+20% PercentDamageReduction)，战斗期间有效且末帧清零
        {
            var clawTpl = repo.GetCard("6840ec1d-f9fc-443c-a7f8-4b9e6cbf0313")!;
            var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 5000, unlockedHandSlots: 10);
            var p2 = new PlayerState("opponent", "P2", HeroNames.Vanessa, initialHealthMax: 400, unlockedHandSlots: 10);
            p1.Hand.TryPlaceOrShift(MakeSteadyWeapon("11111111-1111-4111-8111-111111111108", "HeavyHit", damage: 200, cooldownMs: 1000, instanceId: "p1_h"), 0);
            p2.Hand.TryPlaceOrShift(new CardInstance(clawTpl, CardTier.Diamond, enchantment: null, instanceId: "p2_claw"), 0);

            var res = CombatSimulator.Simulate(repo, p1, p2, seed: 57107, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false);
            Assert.Equal("player", res.WinnerPlayerId);
            Assert.Equal(20, res.Timeline[0].P2PercentDamageReduction);

            var (p1E, p1M) = BuildBoardEntries(p1, ECombatantId.Player);
            var (p2E, p2M) = BuildBoardEntries(p2, ECombatantId.Opponent);
            var sim = CombatSimConverter.ToCombatSim(res, repo, p1M, p2M, p1E, p2E);
            var lastFrame = sim.Frames[^1];
            Assert.NotNull(lastFrame.OpponentUpdates);
            Assert.True(lastFrame.OpponentUpdates!.Attributes.TryGetValue(EPlayerAttributeType.PercentDamageReduction, out var endUpd));
            Assert.Equal(20, endUpd!.PreviousValue);
            Assert.Equal(0, endUpd.CurrentValue);
        }

        // 2) Hunker Down（UntilEndOfCombat 沙暴减伤）：沙暴触发后生效并在末帧清理归零
        {
            var hunkerTpl = repo.GetCard("369a6704-2727-4cea-be27-01b76c82df45")!;
            var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 1000, unlockedHandSlots: 10);
            var p2 = new PlayerState("opponent", "P2", HeroNames.Pygmalien, initialHealthMax: 1000, unlockedHandSlots: 10);
            p2.Skills.Add(new CardInstance(hunkerTpl, CardTier.Diamond, enchantment: null, instanceId: "p2_hunker"));

            var res = CombatSimulator.Simulate(repo, p1, p2, seed: 57108, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false);
            Assert.Equal("opponent", res.WinnerPlayerId);
            Assert.Contains(res.Timeline, t => t.P2PercentDamageReduction > 0);

            var (p1E, p1M) = BuildBoardEntries(p1, ECombatantId.Player);
            var (p2E, p2M) = BuildBoardEntries(p2, ECombatantId.Opponent);
            var sim = CombatSimConverter.ToCombatSim(res, repo, p1M, p2M, p1E, p2E);
            var lastFrame = sim.Frames[^1];
            Assert.NotNull(lastFrame.OpponentUpdates);
            Assert.True(lastFrame.OpponentUpdates!.Attributes.TryGetValue(EPlayerAttributeType.PercentDamageReduction, out var endUpd));
            Assert.Equal(0, endUpd!.CurrentValue);
        }
    }

    // =========================================================================
    // Bug 2：全 21 张可玩直效 EventEncounter 瞬发免空房间 + OnHourStarted 嵌套拆包
    // =========================================================================

    [Fact]
    public void Bug2_All21PlayableDirectEffectEventEncounters_ResolveInstantlyWithoutEmptyRoom()
    {
        var cases = new (string Id, string Label, string Kind)[]
        {
            ("036ce025-fc8a-4924-b1bd-a4f6f470ebc7", "Borrow (Day 1-2)", "gold"),
            ("937489c5-22bf-4190-89b1-f396894c85f1", "Cache of Riches (Day 1 to 2)", "gold"),
            ("7d832f18-dd4c-4d67-8e4f-573618f7fa38", "Cache of Riches (Day 3 to 4)", "gold"),
            ("af188bbf-4e10-42c8-803c-12594f4d80d7", "Cache of Riches (Day 5+)", "gold"),
            ("e6e09e6a-ebe0-46f8-bfbb-716b74f78bc6", "Candy Stash (Day 1-3)", "spawn"),
            ("33a881fa-66fc-4a98-8744-de38c1d44baf", "Candy Stash (Day 4-6)", "spawn"),
            ("384bd291-99b1-4736-8cbc-ebdac4d11d63", "Candy Stash (Day 7-9)", "spawn"),
            ("cbcb84f9-631a-4d85-bed3-cfd2f86fb8d6", "Candy Stash (Day 10)", "spawn"),
            ("f6430efe-8452-475b-8c8f-5d789b914a3f", "Invest in Yourself (Day 1-2)", "income"),
            ("8ece0ce0-1b20-41da-a3d9-262f2038af58", "Invest in Yourself (Day 3-4)", "income"),
            ("b69e0b6d-d4c2-4017-a4b4-43e4369600b1", "Invest in Yourself (Day 5-6)", "income"),
            ("cbe0d294-09db-47ea-a7e8-8127605c1173", "Look for Spare Change (Day 1-3)", "spawn"),
            ("39961216-13c2-4013-8da5-6435a86e0e76", "Look for Spare Change (Day 4-6)", "spawn"),
            ("ef60b13f-35cb-49ef-bf9c-55847e3ab9ce", "Look for Spare Change (Day 7-9)", "spawn"),
            ("b3f47a45-98bd-4200-9e94-5ddbc32e60cf", "Look for Spare Change (Day 10)", "spawn"),
            ("fe756e73-bedb-4700-901f-07ef13b3c512", "Regenerative Tincture", "regen"),
            ("e6b0bc78-4284-4bc7-b130-f5d04007ccf6", "Snack Time", "hpmax"),
            ("ce38f4a2-f156-4082-b8ce-e8956c71fc0d", "Study (Default Option)", "xp"),
            ("9ddb3457-d4a5-45ec-913e-ae52fce9cb22", "Trophy Hunt (Day 1-3)", "spawn"),
            ("58e800bd-a2ae-4135-a8c3-2eca839c17d2", "Trophy Hunt (Day 4-6)", "spawn"),
            ("754ff658-3032-46ab-91f3-a2ce3a1cb1da", "Trophy Hunt (Day 7-9)", "spawn"),
        };

        int seed = 57200;
        foreach (var (id, label, kind) in cases)
        {
            var (run, engine) = CreateStartedRun(day: 4, hour: 1, gold: 20, healthMax: 600, seed: ++seed);
            int goldBefore = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
            int incomeBefore = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, 0);
            int regenBefore = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthRegen, 0);
            int hpMaxBefore = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 0);
            int xpBefore = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
            int ownedBefore = CountOwnedItems(run);

            var enc = InjectChoiceEncounter(run, id);
            var outcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(enc.InstanceId) });
            Assert.True(outcome.Error == null, $"{label} 选中失败: {outcome.Error}");

            Assert.NotEqual(ERunState.Encounter, run.State);
            Assert.True(run.State is ERunState.Choice or ERunState.LevelUp, $"{label} 应直接处于 Choice 或 LevelUp，实际={run.State}");
            Assert.Null(run.CurrentEncounterId);
            Assert.NotEmpty(run.SelectionSet);

            var packets = new List<GameSim>();
            if (outcome.PreSplitGameSim != null && outcome.SecondaryGameSimEvents is { Count: > 0 })
            {
                packets.Add(outcome.PreSplitGameSim);
                if (outcome.AdditionalPreSplitGameSims is { Count: > 0 })
                    packets.AddRange(outcome.AdditionalPreSplitGameSims);
                packets.Add(run.BuildFullGameSim(outcome.SecondaryGameSimEvents));
            }
            else
            {
                packets.Add(run.BuildFullGameSim(outcome.Events));
            }

            foreach (var pkt in packets)
            {
                Assert.Null(pkt.CurrentState!.CurrentEncounterId);
                bool hasSp = pkt.Events.Any(e => e is GameSimEventCardSpawned);
                bool hasDl = pkt.Events.Any(e => e is GameSimEventCardDealt);
                Assert.False(hasSp && hasDl, $"{label}: 单包禁止同时混发 CardSpawned 与 CardDealt");
            }

            switch (kind)
            {
                case "gold":
                    Assert.True(run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0) > goldBefore, $"{label} 应立刻发放金币");
                    break;
                case "income":
                    Assert.True(run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, 0) > incomeBefore, $"{label} 应立刻增加收入");
                    break;
                case "regen":
                    Assert.True(run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthRegen, 0) > regenBefore, $"{label} 应立刻增加回血");
                    break;
                case "hpmax":
                    Assert.True(run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 0) > hpMaxBefore, $"{label} 应立刻增加最大生命值");
                    break;
                case "xp":
                    Assert.True(run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0) >= xpBefore + 2 || run.State == ERunState.LevelUp, $"{label} 应立刻结算 2 XP");
                    break;
                case "spawn":
                    Assert.True(CountOwnedItems(run) > ownedBefore, $"{label} 应立刻生成衍生卡入包");
                    Assert.NotNull(outcome.PreSplitGameSim);
                    Assert.NotNull(outcome.SecondaryGameSimEvents);
                    break;
            }
        }
    }

    [Fact]
    public void Bug2_InstantEncounterAtHour5WithOnHourStartedSpawn_SplitsCleanlyWithoutHour6Deadlock()
    {
        var hourSpawnTpl = Repo.Value.CardsById.Values
            .FirstOrDefault(c => c.Type == "Item"
                              && Pool.Value.IsAssetValid(c.Id)
                              && c.Abilities.Values.Any(a =>
                                  a.Trigger != null
                                  && string.Equals(a.Trigger.Type, "TTriggerOnHourStarted", StringComparison.OrdinalIgnoreCase)
                                  && a.Action != null
                                  && string.Equals(a.Action.Type, "TActionGameSpawnCards", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(hourSpawnTpl);

        var (run, engine) = CreateStartedRun(day: 2, hour: 5, gold: 20, healthMax: 600, seed: 57299);
        run.PlayerAttributes[EPlayerAttributeType.Experience] = 0;
        AddOwnedItem(run, hourSpawnTpl!.Id, ETier.Diamond, EInventorySection.Hand, EContainerSocketId.Socket_0);

        var enc = InjectChoiceEncounter(run, "e6e09e6a-ebe0-46f8-bfbb-716b74f78bc6");
        var outcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(enc.InstanceId) });
        Assert.Null(outcome.Error);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(6u, run.Hour);

        var packets = new List<GameSim>();
        if (outcome.PreSplitGameSim != null && outcome.SecondaryGameSimEvents is { Count: > 0 })
        {
            packets.Add(outcome.PreSplitGameSim);
            if (outcome.AdditionalPreSplitGameSims is { Count: > 0 })
                packets.AddRange(outcome.AdditionalPreSplitGameSims);
            packets.Add(run.BuildFullGameSim(outcome.SecondaryGameSimEvents));
        }
        else
        {
            packets.Add(run.BuildFullGameSim(outcome.Events));
        }

        Assert.True(packets.Count >= 2, "直效发卡叠加跨小时发卡必须拆包，严禁单包混发");
        foreach (var pkt in packets)
        {
            bool hasSp = pkt.Events.Any(e => e is GameSimEventCardSpawned);
            bool hasDl = pkt.Events.Any(e => e is GameSimEventCardDealt);
            Assert.False(hasSp && hasDl, "任意拆包帧均严禁混发 CardSpawned 与 CardDealt");
            if (pkt.Run!.Hour == 6u)
                Assert.Equal(ERunState.Choice, pkt.CurrentState!.StateName);
        }
    }

    // =========================================================================
    // Bug 3：Day 10 微型世界远征完整 H1–H5–H6 闭环与 Depleted Device -> Sizeinator
    // =========================================================================

    [Fact]
    public void Bug3_TinyWorldExpedition_FullEndToEnd_H1ToH5ChargeDepletedDeviceToSizeinator_AndH6Exit()
    {
        const string tinyWorldTicketId = "24dc340b-bb3d-4c9b-8715-a807c0c06a8c";
        const string depletedDeviceId = "021639c0-4a9c-49d8-8254-6376994823e2";
        const string sizeinatorId = "7b30db1a-80c8-4889-ae2f-12b0ad047796";
        const string shrunkStepId = "dd328ec0-7452-43d8-9d85-559b70947a91";
        const string whereAmIId = "875caea4-93c5-440b-a9fd-2886eeaa0fc5";
        const string barnacleBigwigId = "9f8fa7f6-c322-4ff6-b81f-c379305b97f4";
        const string banannibossId = "1a3f7d6a-3f24-42dc-ba27-9d4bf199d0b8";
        const string ethergyNexusId = "f418a73c-d442-4ee1-aa7f-ec43f0418d9f";
        const string chargeStepId = "6a365885-9bc4-438b-bbff-30e12aa7818d";

        var (run, engine) = CreateStartedRun(hero: EHero.Vanessa, day: 10, hour: 1, gold: 80, healthMax: 50000, seed: 57301);
        run.PlayerAttributes[EPlayerAttributeType.Level] = 15;
        run.PlayerAttributes[EPlayerAttributeType.Experience] = 0;

        AddOwnedItem(run, tinyWorldTicketId, ETier.Legendary, EInventorySection.Stash, EContainerSocketId.Socket_0);
        var weaponTpl = Repo.Value.CardsById.Values.First(c =>
            c.Type == "Item"
            && c.Tags.Contains("Weapon")
            && c.Tiers != null
            && c.Tiers.Values.Any(t => t.Attributes != null && t.Attributes.ContainsKey("DamageAmount"))
            && Pool.Value.IsAssetValid(c.Id));
        AddOwnedItem(run, weaponTpl.Id, ETier.Diamond, EInventorySection.Hand, EContainerSocketId.Socket_0,
            attrOverrides: new Dictionary<ECardAttributeType, int>
            {
                [ECardAttributeType.DamageAmount] = 20000,
                [ECardAttributeType.FlatCooldownReduction] = 3000,
            });

        // ── H1：Day 10 Hour 1 持票必发 Tiny World Expedition ──
        var shop = new ShopEngine(run, Pool.Value, Repo.Value, run.CustomRules);
        shop.DealHourEncounters(playerState: null);
        var twEntry = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId == Guid.Parse(CardPool.TinyWorldExpeditionRoomId));
        Assert.NotNull(twEntry);

        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(twEntry!.InstanceId) }).Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal("3b249f86c639545b784d8c699cc6edd1", run.CurrentBoardId);

        var shrunkCard = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId == Guid.Parse(shrunkStepId));
        Assert.NotNull(shrunkCard);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shrunkCard!.InstanceId) }).Error);

        var devShelf = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId == Guid.Parse(depletedDeviceId));
        Assert.NotNull(devShelf);
        var buyDev = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(devShelf!.InstanceId),
            Section = EInventorySection.Stash,
        });
        Assert.Null(buyDev.Error);
        string deviceInstanceId = devShelf.InstanceId;
        Assert.Equal(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_13, 0));
        Assert.Equal(CardPool.ReplacementSetTinyWorldId, run.ActiveReplacementSetId, StringComparer.OrdinalIgnoreCase);

        void DrainLevelUpToChoice()
        {
            for (int guard = 0; guard < 12 && run.State != ERunState.Choice; guard++)
            {
                if (run.State == ERunState.LevelUp && run.SelectionSet.Count > 0)
                {
                    Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(run.SelectionSet[0]) }).Error);
                }
                else if (run.State is ERunState.Pedestal or ERunState.Encounter or ERunState.Loot)
                {
                    Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                }
                else
                {
                    break;
                }
            }
        }

        DrainLevelUpToChoice();

        // ── H2：Where am I? -> 击败 Barnacle Bigwig ──
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(2u, run.Hour);
        Assert.Single(run.SelectionSet);
        Assert.Equal(Guid.Parse(whereAmIId), run.Cards[run.SelectionSet[0]].TemplateId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(run.SelectionSet[0]) }).Error);

        var bigwigCard = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId == Guid.Parse(barnacleBigwigId));
        Assert.NotNull(bigwigCard);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(bigwigCard!.InstanceId) }).Error);
        DrainLevelUpToChoice();

        // ── H3：Sprout（官方专属免费物品池，绝不提前跳过） ──
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(3u, run.Hour);
        Assert.Single(run.SelectionSet);
        Assert.Equal(Guid.Parse(CardPool.SproutEncounterId), run.Cards[run.SelectionSet[0]].TemplateId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(run.SelectionSet[0]) }).Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.NotEmpty(run.SelectionSet);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        DrainLevelUpToChoice();

        // ── H4：Bananniboss（首领战） ──
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(4u, run.Hour);
        Assert.Single(run.SelectionSet);
        Assert.Equal(Guid.Parse(banannibossId), run.Cards[run.SelectionSet[0]].TemplateId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(run.SelectionSet[0]) }).Error);
        DrainLevelUpToChoice();

        // ── H5：Ethergy Nexus -> Charge the Depleted Device -> 传奇变形为 Sizeinator ──
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(5u, run.Hour);
        Assert.Single(run.SelectionSet);
        Assert.Equal(Guid.Parse(ethergyNexusId), run.Cards[run.SelectionSet[0]].TemplateId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(run.SelectionSet[0]) }).Error);
        Assert.Equal(ERunState.Encounter, run.State);

        var chargeCard = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId == Guid.Parse(chargeStepId));
        Assert.NotNull(chargeCard);
        var chargeOutcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(chargeCard!.InstanceId) });
        Assert.Null(chargeOutcome.Error);

        // 核心验收：背包里的 Depleted Device 必须彻底变形为 Sizeinator，不得残留任何不可出售的耗尽装置！
        Assert.DoesNotContain(run.Cards.Values, c => c.TemplateId == Guid.Parse(depletedDeviceId));
        var transformedEntry = run.Cards.Values.FirstOrDefault(c =>
            c.Owner == ECombatantId.Player && c.TemplateId == Guid.Parse(sizeinatorId));
        Assert.NotNull(transformedEntry);
        Assert.Equal(ETier.Legendary, transformedEntry!.Tier);
        Assert.Equal(EInventorySection.Stash, transformedEntry.Section);
        Assert.Contains(chargeOutcome.Events, e =>
            e is GameSimEventCardTransformed tr
            && tr.TransformedCards.Any(tc => tc.InstanceId == transformedEntry.InstanceId));

        // ── H6：自然退出微型世界替换集、复位棋盘覆盖并进入 Day 10 PvP ──
        DrainLevelUpToChoice();
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(6u, run.Hour);
        Assert.Null(run.ActiveReplacementSetId);
        Assert.Null(run.ActiveExpeditionId);
        Assert.Null(run.CurrentBoardId);

        // 验证变形后的 Sizeinator 可正常出售清出背包（原版 SellPrice=0，出售触发 OnCardSold 缩小效果，不再是不可出售的永久垃圾）
        var sellOutcome = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(transformedEntry.InstanceId) });
        Assert.Null(sellOutcome.Error);
        Assert.DoesNotContain(run.Cards.Values, c => c.InstanceId == transformedEntry.InstanceId);
    }

    [Fact]
    public void Bug3_ChargeDepletedDevice_WorksInBothHandAndStash_AndAll5ReplacementSetsCompleteH2ToH5()
    {
        const string depletedDeviceId = "021639c0-4a9c-49d8-8254-6376994823e2";
        const string sizeinatorId = "7b30db1a-80c8-4889-ae2f-12b0ad047796";
        const string ethergyNexusId = "f418a73c-d442-4ee1-aa7f-ec43f0418d9f";
        const string chargeStepId = "6a365885-9bc4-438b-bbff-30e12aa7818d";

        foreach (var section in new[] { EInventorySection.Hand, EInventorySection.Stash })
        {
            var (run, engine) = CreateStartedRun(day: 10, hour: 5, seed: section == EInventorySection.Hand ? 57311 : 57312);
            AddOwnedItem(run, depletedDeviceId, ETier.Legendary, section, EContainerSocketId.Socket_0);
            var nexus = InjectChoiceEncounter(run, ethergyNexusId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(nexus.InstanceId) }).Error);

            var chargeCard = run.SelectionSet
                .Select(id => run.Cards[id])
                .First(c => c.TemplateId == Guid.Parse(chargeStepId));
            var res = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(chargeCard.InstanceId) });
            Assert.Null(res.Error);
            Assert.DoesNotContain(run.Cards.Values, c => c.TemplateId == Guid.Parse(depletedDeviceId));
            Assert.Contains(run.Cards.Values, c =>
                c.Owner == ECombatantId.Player
                && c.Section == section
                && c.TemplateId == Guid.Parse(sizeinatorId));
        }

        // 触类旁通：校验全部 5 套官方远征替换集 H2/H3/H4/H5 均完整非空且资产有效
        foreach (string repId in new[]
        {
            CardPool.ReplacementSetCrashSiteId,
            CardPool.ReplacementSetJungleFieryId,
            CardPool.ReplacementSetJungleToxicId,
            CardPool.ReplacementSetJungleObsidianId,
            CardPool.ReplacementSetTinyWorldId,
        })
        {
            Assert.True(ShopEngine.TryGetReplacementSet(repId, out var def) && def != null, $"替换集 {repId} 未注册");
            for (int hr = 2; hr <= 5; hr++)
            {
                Assert.True(def!.Hours.TryGetValue(hr, out var list) && list is { Count: > 0 }, $"替换集 {repId} 缺少 Hour {hr} 配置");
                foreach (var entry in list!)
                    Assert.NotNull(Pool.Value.GetIfAssetValid(entry.TemplateId));
            }
        }
    }

    // =========================================================================
    // Bug 4：全 8 名伙伴完整端到端实证（商店刷新 / 25% 折扣 / Day 2+ 伙伴熔炉 / 导师 / 快照往返）
    // =========================================================================

    [Fact]
    public void Bug4_All8PartnerSkills_MerchantsOfferPartnerItems_With25PercentDiscount_AndExactGoldCharged()
    {
        const string partnerPortalId = "d16c204b-33d5-41d8-9621-67b240cd1fd5";
        const string jayJayId = "85db60ee-3619-4e39-bfb2-2f4ac56f172b";
        const string ailaId = "dc12f4bd-6c33-41f0-b7ad-d62a0be09a47";

        var allPartners = new (EHero PlayerHero, EHero PartnerHero, string SkillId, string ForjaId)[]
        {
            (EHero.Pygmalien, EHero.Vanessa,   "afe451fd-7726-4ed6-9ad9-48d917a12012", "62c1cb63-b94e-4e3d-9dfd-99f5d88054df"),
            (EHero.Vanessa,   EHero.Pygmalien, "645e8d63-24be-4ce8-a633-7df736826d95", "2443e495-209d-485d-a5a2-39e46cff9c9e"),
            (EHero.Vanessa,   EHero.Dooley,    "2f43270c-1f5b-456c-aa86-5c186e6d3fea", "7aa5a3e1-b819-4d1c-afbe-5748f5de19c1"),
            (EHero.Vanessa,   EHero.Mak,       "3bc339db-f463-4390-bf2e-31f2a5008b3f", "cd23f4c6-9131-434c-b773-abb540a2ef42"),
            (EHero.Vanessa,   EHero.Jules,     "2e70face-81e0-4d1e-8c03-3bcc3efc2625", "a5d40e13-cbfd-406f-b010-3b2b7bb8fc85"),
            (EHero.Vanessa,   EHero.Stelle,    "aff2654a-a1d4-4c04-8438-4e3f18e136cd", "b2957f45-afb8-4f46-923e-c4253f9bbd4a"),
            (EHero.Vanessa,   EHero.Karnok,    "84092bda-9617-431a-a12c-6f93edd024a9", "b13a0aec-de7b-4735-b3ab-bc24074bc487"),
            (EHero.Vanessa,   EHero.Hero8,     "c5541759-7bcb-4892-8fa5-b4c865eef40d", "bad273f7-29cd-49dd-b459-29f05c4fc2f4"),
        };

        int seed = 57400;
        foreach (var (playerHero, partnerHero, skillId, forjaId) in allPartners)
        {
            string partnerHeroName = partnerHero.ToString();
            string playerHeroName = playerHero.ToString();

            var (run, engine) = CreateStartedRun(hero: playerHero, day: 4, hour: 1, gold: 200, seed: ++seed);

            // 1) 进入 Partner Portal 并选择目标伙伴技能（确保触发 OnCardSelected 解除 Forja 排除并排除 Partner Portal）
            var portalEnc = InjectChoiceEncounter(run, partnerPortalId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(portalEnc.InstanceId) }).Error);
            var targetSkillEntry = run.SelectionSet
                .Select(id => run.Cards[id])
                .FirstOrDefault(c => c.TemplateId == Guid.Parse(skillId))
                ?? InjectSelectionSkill(run, skillId, ETier.Legendary);

            Assert.Null(engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(targetSkillEntry.InstanceId) }).Error);
            Assert.True(HasOwnedSkill(run, skillId), $"应已装备伙伴技能 {skillId}");
            Assert.Contains(partnerPortalId, run.ExclusionSet);
            Assert.DoesNotContain(forjaId, run.ExclusionSet);

            // 2) 在 Jay Jay（无 IgnoreHero）与 Aila（含 IgnoreHero）两类典型商人上均实证刷出伙伴英雄专属物品，并检验 25% 折扣光环与扣款
            foreach (var merchantId in new[] { jayJayId, ailaId })
            {
                bool sawPartnerItem = false;
                bool verifiedDiscountAndPurchase = false;

                for (int attempt = 0; attempt < 18 && !verifiedDiscountAndPurchase; attempt++)
                {
                    run.Hour = 2;
                    var merch = InjectChoiceEncounter(run, merchantId);
                    Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(merch.InstanceId) }).Error);
                    Assert.Equal(ERunState.Encounter, run.State);

                    foreach (string shelfId in run.SelectionSet.ToList())
                    {
                        var shelfCard = run.Cards[shelfId];
                        var coreTpl = Repo.Value.GetCard(shelfCard.TemplateId.ToString());
                        if (coreTpl == null) continue;
                        bool isPartnerExclusive = coreTpl.Heroes.Contains(partnerHeroName, StringComparer.OrdinalIgnoreCase)
                                               && !coreTpl.Heroes.Contains(playerHeroName, StringComparer.OrdinalIgnoreCase);
                        if (!isPartnerExclusive) continue;

                        sawPartnerItem = true;
                        double auraBuyMult = shelfCard.CoreCard?.AuraMult.GetValueOrDefault("BuyPrice", 1.0) ?? 1.0;
                        Assert.True(
                            auraBuyMult <= 0.7501,
                            $"伙伴 {partnerHeroName} 物品 {coreTpl.InternalName} 必须挂载 25% 折扣光环 (AuraMult<=0.75)，实际={auraBuyMult:F4}");

                        int basePrice = ComputeUndiscountedBaseBuyPrice(coreTpl, shelfCard.Tier);
                        int effectivePrice = shelfCard.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, -1);
                        int expectedMaxDiscounted = Math.Max(1, (int)Math.Round(basePrice * 0.75));
                        Assert.True(
                            effectivePrice > 0 && effectivePrice <= expectedMaxDiscounted && (basePrice < 4 || effectivePrice < basePrice),
                            $"伙伴 {partnerHeroName} 物品 {coreTpl.InternalName} ({shelfCard.Tier}) 在商人 {merchantId} 应享 25% 折扣：base={basePrice}, actual={effectivePrice}, expected<={expectedMaxDiscounted}");

                        int goldBeforeBuy = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
                        var buyRes = engine.Handle(new SelectItemCommand
                        {
                            InstanceId = new InstanceId(shelfId),
                            Section = EInventorySection.Stash,
                        });
                        if (buyRes.Error == null)
                        {
                            int goldAfterBuy = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
                            Assert.Equal(goldBeforeBuy - effectivePrice, goldAfterBuy);
                            if (run.Cards.TryGetValue(shelfId, out var bought) && bought.CoreCard != null)
                                run.StashGrid.Remove(bought.CoreCard);
                            run.Cards.Remove(shelfId);
                            verifiedDiscountAndPurchase = true;
                            break;
                        }
                    }
                    Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                }

                Assert.True(sawPartnerItem, $"装备伙伴技能 {skillId} ({partnerHeroName}) 后，商人 {merchantId} 18 次刷新均未刷出任何伙伴物品！");
                Assert.True(verifiedDiscountAndPurchase, $"装备伙伴技能 {skillId} ({partnerHeroName}) 后，未完成商人 {merchantId} 的 25% 折扣核价与购买验证！");
            }
        }

        // 3) 对照组：未装备任何伙伴技能时，Vanessa 在 Jay Jay 刷新 20 次绝不刷出 Pygmalien 专属物品
        {
            var (ctrlRun, ctrlEngine) = CreateStartedRun(hero: EHero.Vanessa, day: 4, hour: 2, gold: 100, seed: 57499);
            for (int i = 0; i < 20; i++)
            {
                ctrlRun.Hour = 2;
                var merch = InjectChoiceEncounter(ctrlRun, jayJayId);
                Assert.Null(ctrlEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(merch.InstanceId) }).Error);
                foreach (string shelfId in ctrlRun.SelectionSet)
                {
                    var coreTpl = Repo.Value.GetCard(ctrlRun.Cards[shelfId].TemplateId.ToString());
                    if (coreTpl == null) continue;
                    bool isPygOnly = coreTpl.Heroes.Contains("Pygmalien", StringComparer.OrdinalIgnoreCase)
                                  && !coreTpl.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase);
                    Assert.False(isPygOnly, $"对照组无伙伴技能不应在 Jay Jay 刷出 Pygmalien 专属卡 {coreTpl.InternalName}");
                }
                Assert.Null(ctrlEngine.Handle(new ExitCurrentStateCommand()).Error);
            }
        }
    }

    [Fact]
    public void Bug4_PartnerPortalForja_UnlocksOnDay2_BalancedWeight_UpgradesPartnerOnly_LibraryMentor_AndSnapshotRoundTrip()
    {
        const string pygSkillId = "645e8d63-24be-4ce8-a633-7df736826d95";
        const string pygForjaId = "2443e495-209d-485d-a5a2-39e46cff9c9e";
        const string libraryId = "48a25657-baf6-440d-9085-bd7aeda1638a";
        const string mentorStepId = "0a32923c-44af-453a-a5ea-8e6d70a25198";
        const string pygTrainerId = "35e74c18-1bd6-4c91-ab21-2fd64adf6cc6";
        const string jayJayId = "85db60ee-3619-4e39-bfb2-2f4ac56f172b";

        // 1) 权重均衡校验：Partner Portal - Forja 不再退化为 fallbackWeight=1.0 碾压 B1&B2
        var pygForjaTpl = Pool.Value.Get(pygForjaId)!;
        var b1b2Tpl = Pool.Value.Get("9a025568-f876-468d-a280-7627f84e18f8")!;
        double forjaWeightDay2 = OfficialEncounterSchedule.GetEmpiricalDrawWeight(pygForjaTpl, day: 2);
        double forjaWeightDay3 = OfficialEncounterSchedule.GetEmpiricalDrawWeight(pygForjaTpl, day: 3);
        double b1b2WeightDay3 = OfficialEncounterSchedule.GetEmpiricalDrawWeight(b1b2Tpl, day: 3);
        Assert.InRange(forjaWeightDay2, 0.005, 0.08);
        Assert.InRange(forjaWeightDay3, 0.005, 0.08);
        Assert.True(forjaWeightDay3 < b1b2WeightDay3 * 2.0, $"Partner Portal - Forja Day 3 权重({forjaWeightDay3:F4})不应异常碾压 B1&B2({b1b2WeightDay3:F4})");

        // 2) Day 2 实测蒙特卡洛：解锁 Partner Portal - Forja 后 Day 2 即可出现（此前被 actualDay >= 3 永久封锁），且不淹没中槽
        int forjaHitsDay2 = 0;
        const int day2Trials = 400;
        for (int i = 0; i < day2Trials; i++)
        {
            var (mcRun, _) = CreateStartedRun(hero: EHero.Vanessa, day: 2, hour: 1, seed: 57500 + i);
            InitRunExclusionSet(mcRun);
            mcRun.ExclusionSet.Remove(pygForjaId);
            var mcShop = new ShopEngine(mcRun, Pool.Value, Repo.Value, mcRun.CustomRules);
            bool hitToday = false;
            foreach (uint hr in new uint[] { 1, 2, 4, 5 })
            {
                mcRun.Hour = hr;
                mcShop.DealHourEncounters(playerState: null, visitedTodayIds: mcRun.VisitedDayEncounterIds);
                if (mcRun.SelectionSet.Any(id => mcRun.Cards[id].TemplateId == Guid.Parse(pygForjaId)))
                {
                    hitToday = true;
                    break;
                }
                foreach (string id in mcRun.SelectionSet)
                    mcRun.VisitedDayEncounterIds.Add(mcRun.Cards[id].TemplateId.ToString());
            }
            if (hitToday) forjaHitsDay2++;
        }
        double day2Rate = (double)forjaHitsDay2 / day2Trials;
        Assert.True(day2Rate >= 0.01 && day2Rate <= 0.15, $"Day 2 解锁后的 Partner Portal - Forja 出现率应位于合理区间 [1%, 15%]，实际={day2Rate:P2} ({forjaHitsDay2}/{day2Trials})");

        // 3) 伙伴熔炉仅升级伙伴英雄物品，拒绝非伙伴物品
        var (run, engine) = CreateStartedRun(hero: EHero.Vanessa, day: 3, hour: 1, gold: 100, seed: 57901);
        InitRunExclusionSet(run);
        run.ExclusionSet.Remove(pygForjaId);
        AddOwnedSkill(run, pygSkillId, ETier.Legendary, EContainerSocketId.Socket_0, instanceId: "skl_pyg_partner");

        var vanessaBronze = Repo.Value.CardsById.Values.First(c =>
            c.Type == "Item" && c.StartingTier == "Bronze" && c.Heroes.Contains("Vanessa") && !c.Heroes.Contains("Pygmalien") && Pool.Value.IsAssetValid(c.Id));
        var pygBronze = Repo.Value.CardsById.Values.First(c =>
            c.Type == "Item" && c.StartingTier == "Bronze" && c.Heroes.Contains("Pygmalien") && !c.Heroes.Contains("Vanessa") && Pool.Value.IsAssetValid(c.Id));

        var vanCard = AddOwnedItem(run, vanessaBronze.Id, ETier.Bronze, EInventorySection.Hand, EContainerSocketId.Socket_0);
        var pygCard = AddOwnedItem(run, pygBronze.Id, ETier.Bronze, EInventorySection.Hand, EContainerSocketId.Socket_3);

        var forjaEnc = InjectChoiceEncounter(run, pygForjaId, ECardType.PedestalEncounter);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(forjaEnc.InstanceId) }).Error);
        Assert.Equal(ERunState.Pedestal, run.State);

        var rejectRes = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(vanCard.InstanceId) });
        Assert.NotNull(rejectRes.Error);
        Assert.Equal(ETier.Bronze, run.Cards[vanCard.InstanceId].Tier);

        var upgradeRes = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(pygCard.InstanceId) });
        Assert.Null(upgradeRes.Error);
        Assert.Equal(ETier.Silver, run.Cards[pygCard.InstanceId].Tier);

        // 4) Library -> Meet Your Partner's Mentor 正确下发伙伴技能导师
        var libEnc = InjectChoiceEncounter(run, libraryId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(libEnc.InstanceId) }).Error);
        var mentorCard = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => c.TemplateId == Guid.Parse(mentorStepId));
        Assert.NotNull(mentorCard);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mentorCard!.InstanceId) }).Error);
        Assert.Contains(run.SelectionSet, id => run.Cards[id].TemplateId == Guid.Parse(pygTrainerId));
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

        // 5) 快照序列化往返（模拟断线重连/服务重启）后伙伴技能、Forja 解锁与商人刷伙伴物品+折扣持续有效
        string snapJson = RunSnapshotSerializer.Serialize(run);
        var restored = RunSnapshotSerializer.Deserialize(snapJson, Repo.Value);
        Assert.True(HasOwnedSkill(restored, pygSkillId));
        Assert.DoesNotContain(pygForjaId, restored.ExclusionSet);

        var restoredEngine = new RunEngine(
            restored, Pool.Value, Monsters.Value, LevelUps.Value, Mode.Value, Repo.Value, new PvpGhostPool());
        typeof(RunEngine).GetProperty(nameof(RunEngine.StartSelectionDone))!.SetValue(restoredEngine, true);

        bool sawAfterRestore = false;
        for (int attempt = 0; attempt < 18 && !sawAfterRestore; attempt++)
        {
            restored.Hour = 2;
            var jj = InjectChoiceEncounter(restored, jayJayId);
            Assert.Null(restoredEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(jj.InstanceId) }).Error);
            foreach (string id in restored.SelectionSet)
            {
                var tpl = Repo.Value.GetCard(restored.Cards[id].TemplateId.ToString());
                if (tpl != null && tpl.Heroes.Contains("Pygmalien", StringComparer.OrdinalIgnoreCase) && !tpl.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase))
                {
                    var cardEntry = restored.Cards[id];
                    double auraMult = cardEntry.CoreCard?.AuraMult.GetValueOrDefault("BuyPrice", 1.0) ?? 1.0;
                    Assert.True(auraMult <= 0.7501, $"重连恢复后伙伴 25% 折扣光环应持续生效，实际 AuraMult={auraMult:F4}");
                    int basePrice = ComputeUndiscountedBaseBuyPrice(tpl, cardEntry.Tier);
                    int effPrice = cardEntry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, -1);
                    int expectedMax = Math.Max(1, (int)Math.Round(basePrice * 0.75));
                    Assert.True(effPrice > 0 && effPrice <= expectedMax && (basePrice < 4 || effPrice < basePrice),
                        $"重连恢复后伙伴折扣价不符: base={basePrice}, actual={effPrice}, expected<={expectedMax}");
                    sawAfterRestore = true;
                    break;
                }
            }
            Assert.Null(restoredEngine.Handle(new ExitCurrentStateCommand()).Error);
        }
        Assert.True(sawAfterRestore, "快照恢复后 Jay Jay 应继续刷出伙伴（Pygmalien）专属打折物品");
    }
}
