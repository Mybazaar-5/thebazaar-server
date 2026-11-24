using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2A 战斗回放协议投影强断言测试套件（无全局静态状态，可并行执行）。
/// 覆盖 P0-19（11 处核心模板解析、官方 131+ 键 VFX 白名单校验、严禁跨附魔回退、vfxOverrideKey 别名反序列化、
/// 非 GUID 战中变形/还原安全 Wire GUID 映射与回调）与 D2-P1-05（BurnRemoved/PoisonRemoved、附魔撤回清晰同步、
/// ActionCostSpent 全资源支持、CaptureSnapshot 含 Skills + CombatSnapshotExtras 差分及归零同步）。
/// </summary>
public sealed class Wave2A_CombatSimConverterAndReplayTests
{
    private static readonly Guid OfficialShellGuid = Guid.Parse("03b1fd16-6b0d-4847-a76f-6c586f7c5958");
    private static readonly Guid OfficialTargetGuid = Guid.Parse("1b955b8d-0ea6-4437-935c-d588be5898c1");
    private const string OfficialLaserVfx = "Projectile_LaserBeam_Red_PV";
    private const string OfficialFireballVfx = "Assets/TheBazaar/Projectiles/Fireworks/Projectile_HarkurvianLauncher_PV.prefab";
    private const string OfficialCustomDbVfx = "Assets/TheBazaar/Projectiles/CustomVanillaUnitTest/Projectile_Vanilla131_PV.prefab";

    private static RuleNode ParseRule(string json) => JsonSerializer.Deserialize<RuleNode>(json)!;

    [Fact]
    public void P019_VfxConfigData_SupportsBothPascalAndCamelCaseOverrideKey()
    {
        var pascal = JsonSerializer.Deserialize<VfxConfigData>("{\"VFXOverrideKey\":\"Fireball\",\"VFXShouldPlay\":true}");
        Assert.NotNull(pascal);
        Assert.Equal("Fireball", pascal!.VFXOverrideKey);

        var camel = JsonSerializer.Deserialize<VfxConfigData>("{\"vfxOverrideKey\":\"LaserRed\",\"vfxShouldPlay\":false}");
        Assert.NotNull(camel);
        Assert.Equal("LaserRed", camel!.VFXOverrideKey);
        Assert.False(camel.VFXShouldPlay);
    }

    [Fact]
    public void P019_CoreTemplateResolution_OfficialVfxWhitelist_AndNoCrossEnchantFallback()
    {
        var officialShellTpl = new CardTemplate
        {
            Id = OfficialShellGuid.ToString(),
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            InternalName = "OfficialShell",
            IsCustomModCard = false,
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { AbilityIds = new List<string> { "0" }, Attributes = new Dictionary<string, double> { ["CooldownMax"] = 2000, ["DamageAmount"] = 10 } }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseRule("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseRule("""{"$type":"TActionPlayerDamage"}""")
                }
            },
            Enchantments = new Dictionary<string, EnchantmentData>
            {
                ["Fiery"] = new EnchantmentData
                {
                    Abilities = new Dictionary<string, AbilityDefinition>
                    {
                        ["0"] = new AbilityDefinition
                        {
                            Id = "0",
                            VFXConfig = new VfxConfigData { VFXOverrideKey = OfficialFireballVfx }
                        }
                    }
                }
            }
        };

        var vanillaDbCard = new CardTemplate
        {
            Id = OfficialTargetGuid.ToString(),
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            InternalName = "Vanilla131Holder",
            IsCustomModCard = false,
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition { Id = "0", VFXConfig = new VfxConfigData { VFXOverrideKey = OfficialCustomDbVfx } }
            }
        };

        var customCardTpl = new CardTemplate
        {
            Id = "mod:custom_laser",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            InternalName = "CustomLaser",
            IsCustomModCard = true,
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { AbilityIds = new List<string> { "laser_ok" }, Attributes = new Dictionary<string, double> { ["CooldownMax"] = 2000, ["DamageAmount"] = 15 } }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["laser_ok"] = new AbilityDefinition { Id = "laser_ok", VFXConfig = new VfxConfigData { VFXOverrideKey = "LaserRed" } },
                ["vfx_fake"] = new AbilityDefinition { Id = "vfx_fake", VFXConfig = new VfxConfigData { VFXOverrideKey = "Assets/Evil/Fake.prefab" } },
                ["db_131"] = new AbilityDefinition { Id = "db_131", VFXConfig = new VfxConfigData { VFXOverrideKey = OfficialCustomDbVfx } }
            }
        };

        var repo = SharedGameDataFixture.Shared.Repo.CreateWithOverlay(
            additionalCards: new[] { officialShellTpl, vanillaDbCard, customCardTpl });

        var customCore = new CardInstance(customCardTpl, CardTier.Bronze, "inst_custom_1");
        var shellCore = new CardInstance(officialShellTpl, CardTier.Bronze, "inst_shell_no_ench");

        var p1Board = new List<RunState.CardEntry>
        {
            new()
            {
                InstanceId = "inst_custom_1",
                TemplateId = OfficialShellGuid,
                CustomCardId = "mod:custom_laser",
                CoreCard = customCore,
                Type = ECardType.Item,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_0
            },
            new()
            {
                InstanceId = "inst_shell_no_ench",
                TemplateId = OfficialShellGuid,
                CoreCard = shellCore,
                Type = ECardType.Item,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                Enchantment = null,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_1
            }
        };

        var result = new CombatResult
        {
            WinnerPlayerId = "player",
            DurationMs = 1000,
            Player1RemainingHp = 300,
            Player2RemainingHp = 0,
            Timeline =
            {
                new CombatTickState { TimeMs = 0, P1Hp = 300, P1MaxHp = 300, P2Hp = 300, P2MaxHp = 300 },
                new CombatTickState { TimeMs = 1000, P1Hp = 300, P1MaxHp = 300, P2Hp = 0, P2MaxHp = 300 }
            },
            Events =
            {
                new CombatEvent { TimeMs = 100, Type = CombatEventType.CardFired, SourceCardInstanceId = "inst_custom_1", AbilityId = "laser_ok", TargetPlayerId = "opponent" },
                new CombatEvent { TimeMs = 200, Type = CombatEventType.CardFired, SourceCardInstanceId = "inst_custom_1", AbilityId = "vfx_fake", TargetPlayerId = "opponent" },
                new CombatEvent { TimeMs = 300, Type = CombatEventType.CardFired, SourceCardInstanceId = "inst_custom_1", AbilityId = "db_131", TargetPlayerId = "opponent" },
                new CombatEvent { TimeMs = 400, Type = CombatEventType.CardFired, SourceCardInstanceId = "inst_shell_no_ench", AbilityId = "0", TargetPlayerId = "opponent" }
            }
        };

        var sim = CombatSimConverter.ToCombatSim(
            result,
            repo,
            new Dictionary<string, CardInstance> { ["inst_custom_1"] = customCore, ["inst_shell_no_ench"] = shellCore },
            new Dictionary<string, CardInstance>(),
            p1Board,
            Array.Empty<RunState.CardEntry>());

        var execEvents = sim.Frames.SelectMany(f => f.Events).OfType<CombatSimEventEffectExecuted>().ToList();
        var laserEv = execEvents.FirstOrDefault(e => e.EffectId == "laser_ok");
        var fakeEv = execEvents.FirstOrDefault(e => e.EffectId == "vfx_fake");
        var db131Ev = execEvents.FirstOrDefault(e => e.EffectId == "db_131");
        var noEnchEv = execEvents.FirstOrDefault(e => e.Source?.Value == "inst_shell_no_ench");

        Assert.NotNull(laserEv?.VfxIndex);
        Assert.Equal(OfficialLaserVfx, sim.VfxKeys[laserEv!.VfxIndex!.Value]);

        Assert.NotNull(fakeEv);
        Assert.Null(fakeEv!.VfxIndex);
        Assert.DoesNotContain(sim.VfxKeys, k => k.Contains("Evil", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(db131Ev?.VfxIndex);
        Assert.Equal(OfficialCustomDbVfx, sim.VfxKeys[db131Ev!.VfxIndex!.Value]);

        Assert.NotNull(noEnchEv);
        Assert.Null(noEnchEv!.VfxIndex);
        Assert.DoesNotContain(OfficialFireballVfx, sim.VfxKeys);
    }

    [Fact]
    public void P019_NonGuidMidCombatTransformAndRevert_UsesSafeWireGuid_AndFiresCallback()
    {
        var origTpl = new CardTemplate
        {
            Id = OfficialShellGuid.ToString(),
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            InternalName = "BaseItem",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 3000 } }
            }
        };
        var phoenixTpl = new CardTemplate
        {
            Id = "mod:transformed_phoenix",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            InternalName = "PhoenixBlade",
            IsCustomModCard = true,
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 2500, ["DamageAmount"] = 45 } }
            }
        };

        var repo = SharedGameDataFixture.Shared.Repo.CreateWithOverlay(additionalCards: new[] { origTpl, phoenixTpl });

        var origCore = new CardInstance(origTpl, CardTier.Bronze, "inst_orig_1");
        var p1Board = new List<RunState.CardEntry>
        {
            new()
            {
                InstanceId = "inst_orig_1",
                TemplateId = OfficialShellGuid,
                CoreCard = origCore,
                Type = ECardType.Item,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_0
            }
        };

        var result = new CombatResult
        {
            WinnerPlayerId = "player",
            DurationMs = 1500,
            Player1RemainingHp = 300,
            Player2RemainingHp = 0,
            Timeline =
            {
                new CombatTickState { TimeMs = 0, P1Hp = 300, P1MaxHp = 300, P2Hp = 300, P2MaxHp = 300 },
                new CombatTickState { TimeMs = 1500, P1Hp = 300, P1MaxHp = 300, P2Hp = 0, P2MaxHp = 300 }
            },
            Events =
            {
                new CombatEvent
                {
                    TimeMs = 200,
                    Type = CombatEventType.CardTransformed,
                    SourceCardInstanceId = "inst_orig_1",
                    TargetCardInstanceId = "inst_phoenix_99",
                    Detail = "inst_orig_1->mod:transformed_phoenix"
                },
                new CombatEvent
                {
                    TimeMs = 900,
                    Type = CombatEventType.CardTransformReverted,
                    TargetCardInstanceId = "inst_orig_1",
                    Detail = OfficialShellGuid.ToString(),
                    RevertedTransformedInstanceIds = new List<string> { "inst_phoenix_99" }
                }
            }
        };

        var callbackRecords = new List<(string InstId, string CoreTplId, Guid WireGuid)>();
        var sim = CombatSimConverter.ToCombatSim(
            result,
            repo,
            new Dictionary<string, CardInstance> { ["inst_orig_1"] = origCore },
            new Dictionary<string, CardInstance>(),
            p1Board,
            Array.Empty<RunState.CardEntry>(),
            resolveWireTemplateGuid: id => id == "mod:transformed_phoenix" ? OfficialTargetGuid : null,
            onRuntimeCardTransformed: (instId, coreId, wireGuid) => callbackRecords.Add((instId, coreId, wireGuid)));

        var trnEvent = sim.Frames.SelectMany(f => f.Events).OfType<CombatSimEventCardTransformed>().Single();
        Assert.Equal("inst_orig_1", trnEvent.OriginalInstanceId);
        Assert.Single(trnEvent.TransformedCards);
        Assert.Equal("inst_phoenix_99", trnEvent.TransformedCards[0].InstanceId);
        Assert.Equal(OfficialTargetGuid.ToString(), trnEvent.TransformedCards[0].TemplateId);

        Assert.Single(callbackRecords);
        Assert.Equal(("inst_phoenix_99", "mod:transformed_phoenix", OfficialTargetGuid), callbackRecords[0]);

        var revEvent = sim.Frames.SelectMany(f => f.Events).OfType<CombatSimEventCardTransformReverted>().Single();
        Assert.Equal("inst_orig_1", revEvent.OriginalCard.InstanceId);
        Assert.Contains("inst_phoenix_99", revEvent.TransformedCardInstanceIds);
    }

    [Fact]
    public void D2P105_BurnPoisonRemoved_EnchantRevertSync_AllResourceActionCost_AndSnapshotExtras()
    {
        var itemTpl = new CardTemplate
        {
            Id = OfficialShellGuid.ToString(),
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            InternalName = "CleanseItem",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 2000, ["DamageAmount"] = 10 } }
            }
        };
        var skillTpl = new CardTemplate
        {
            Id = OfficialTargetGuid.ToString(),
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            InternalName = "DynamicSkill",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["HasteAmount"] = 1500 } }
            }
        };

        var repo = SharedGameDataFixture.Shared.Repo.CreateWithOverlay(additionalCards: new[] { itemTpl, skillTpl });

        var p1 = new PlayerState("p1", "P1", "Vanessa", initialHealthMax: 300);
        var p2 = new PlayerState("p2", "P2", "Vanessa", initialHealthMax: 300);
        var itemInst = new CardInstance(itemTpl, CardTier.Bronze, enchantment: null, instanceId: "inst_item_1");
        var skillInst = new CardInstance(skillTpl, CardTier.Bronze, enchantment: null, instanceId: "inst_skill_1");
        skillInst.ModifyAttribute("PercentCooldownReduction", "Add", 15, DurationTarget.Combat);
        p1.Hand.TryAddAuto(itemInst);
        p1.Skills.Add(skillInst);

        var simRun = CombatSimulator.Simulate(repo, p1, p2, seed: 11);
        Assert.True(simRun.Timeline.Count > 0);
        var firstSkillFrame = simRun.Timeline[0].Cards.FirstOrDefault(c => c.InstanceId == "inst_skill_1");
        Assert.NotNull(firstSkillFrame);
        Assert.True(CombatSnapshotExtras.TryGet(firstSkillFrame!, out var capturedExtras));
        Assert.NotNull(capturedExtras);
        Assert.Equal(1500, capturedExtras!["HasteAmount"]);

        var cf0 = new CardCooldownFrame { InstanceId = "inst_skill_1", CooldownMaxMs = 0 };
        CombatSnapshotExtras.Attach(cf0, new Dictionary<string, int>(StringComparer.Ordinal) { ["HasteAmount"] = 1500, ["TempoCost"] = 3 });
        var cf1 = new CardCooldownFrame { InstanceId = "inst_skill_1", CooldownMaxMs = 0 };

        var itemEntry = new RunState.CardEntry
        {
            InstanceId = "inst_item_1",
            TemplateId = OfficialShellGuid,
            CoreCard = itemInst,
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Enchantment = EEnchantmentType.Fiery,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0
        };
        var skillEntry = new RunState.CardEntry
        {
            InstanceId = "inst_skill_1",
            TemplateId = OfficialTargetGuid,
            CoreCard = skillInst,
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0
        };

        var customResult = new CombatResult
        {
            WinnerPlayerId = "player",
            DurationMs = 600,
            Player1RemainingHp = 300,
            Player2RemainingHp = 0,
            Timeline =
            {
                new CombatTickState { TimeMs = 0, P1Hp = 300, P1MaxHp = 300, P2Hp = 300, P2MaxHp = 300, Cards = { cf0 } },
                new CombatTickState { TimeMs = 300, P1Hp = 300, P1MaxHp = 300, P2Hp = 100, P2MaxHp = 300, Cards = { cf1 } },
                new CombatTickState { TimeMs = 600, P1Hp = 300, P1MaxHp = 300, P2Hp = 0, P2MaxHp = 300, Cards = { cf1 } }
            },
            Events =
            {
                new CombatEvent { TimeMs = 100, Type = CombatEventType.BurnRemoved, SourceCardInstanceId = "inst_item_1", TargetPlayerId = "player", Value = 5 },
                new CombatEvent { TimeMs = 150, Type = CombatEventType.PoisonRemoved, SourceCardInstanceId = "inst_item_1", TargetPlayerId = "player", Value = 4 },
                new CombatEvent { TimeMs = 200, Type = CombatEventType.CardEnchanted, SourceCardInstanceId = "inst_item_1", TargetCardInstanceId = "inst_item_1", Detail = "Reverted:Fiery->Toxic", IsReverted = true },
                new CombatEvent { TimeMs = 250, Type = CombatEventType.CardEnchanted, SourceCardInstanceId = "inst_item_1", TargetCardInstanceId = "inst_item_1", Detail = "Reverted:Toxic", IsReverted = true },
                new CombatEvent { TimeMs = 300, Type = CombatEventType.ActionCostSpent, SourceCardInstanceId = "inst_item_1", Detail = "Gold", Value = 2 },
                new CombatEvent { TimeMs = 350, Type = CombatEventType.ActionCostSpent, SourceCardInstanceId = "inst_item_1", Detail = "Ammo", Value = 1 }
            }
        };

        var sim = CombatSimConverter.ToCombatSim(
            customResult,
            repo,
            new Dictionary<string, CardInstance> { ["inst_item_1"] = itemInst, ["inst_skill_1"] = skillInst },
            new Dictionary<string, CardInstance>(),
            new[] { itemEntry, skillEntry },
            Array.Empty<RunState.CardEntry>());

        var allEvents = sim.Frames.SelectMany(f => f.Events).ToList();
        Assert.Contains(allEvents.OfType<CombatSimEventEffectExecuted>(), e => e.ActionType == EActionCommandType.PlayerBurnRemove);
        Assert.Contains(allEvents.OfType<CombatSimEventEffectExecuted>(), e => e.ActionType == EActionCommandType.PlayerPoisonRemove);

        var enchEvents = allEvents.OfType<CombatSimEventCardEnchanted>().ToList();
        Assert.Equal(2, enchEvents.Count);
        Assert.All(enchEvents, e => Assert.True(e.IsReverted));
        Assert.Null(itemEntry.Enchantment);

        var costEvents = allEvents.OfType<CombatSimEventCardActionCostSpent>().ToList();
        Assert.Contains(costEvents, c => c.PlayerAttributeSpent == EPlayerAttributeType.Gold);
        Assert.Contains(costEvents, c => c.CardAttributeSpent == ECardAttributeType.Ammo);

        var skillUpdates = sim.Frames
            .Where(f => f.CardUpdates.ContainsKey(new InstanceId("inst_skill_1")))
            .Select(f => f.CardUpdates[new InstanceId("inst_skill_1")])
            .ToList();
        Assert.Contains(skillUpdates, cu => cu.Attributes.TryGetValue(ECardAttributeType.HasteAmount, out var a) && a.CurrentValue == 1500);
        Assert.Contains(skillUpdates, cu => cu.Attributes.TryGetValue(ECardAttributeType.HasteAmount, out var a) && a.CurrentValue == 0);
        Assert.Contains(skillUpdates, cu => cu.Attributes.TryGetValue(ECardAttributeType.TempoCost, out var a) && a.CurrentValue == 3);
    }
}
