using System.Text;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Mods;

namespace BazaarCore.Tests;

/// <summary>
/// V14.2 Mod 创作平台与通用机制基础设施 7 项实机问题回归测试：
/// 1. 通用非枚举属性与双示例 Mod 校验（clockwork-forge + bazaar-chronicles-sample 0 Error，单选 Common 提醒）；
/// 2. 英雄商店池分流（常规 8 英雄卡可在杰杰等英雄商店刷出，不挤爆 Curio 中立池）；
/// 3. 咖啡店 Jules' Cafe UntilEndOfDay 限时增益（Relax 玩家最大生命 +10% &amp; Strong Brew 武器当日 +25 伤害即时同步且带入战斗）；
/// 4. 战后临时增益不污染永久层且 ClearPostCombatTransientStatusAndSync 强刷所有战斗可变属性回基线；
/// 5. 官方 131 个真实 Unity VFX 别名解析库 + Repeat.initialDelayMs 错峰连发 + CombatSimConverter 不吞显式 VfxKeyOverride；
/// 6. 自定义卡升阶基础伤害随品阶同步（覆盖借皮载体表）+ FindFuseTarget 按 CustomCardId 严格隔离防误合；
/// 7. 任意自定义机制通用实时叠层同步（Custom_0..Custom_8 逐帧下发 + CardAttributeModified 脉冲 + 显式/自动推导 CounterDisplays）。
/// </summary>
[Xunit.Collection("GlobalStaticState")]
public sealed class V14_2_ModPlatformFixesTests
{
    private const string ZSwordDonorId = "34476196-7d30-4664-8fe3-e99411022a86";
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string CafeRelaxStepId = "02ffa56b-c948-4ec2-a934-26eaefc03923";
    private const string CafeStrongBrewStepId = "e1157271-99e9-435f-8b2c-09be0d3de873";
    private const string OverloadCardId = "clockwork-forge:z_sword_custom_d83alo";

    private sealed record Fixture(
        string RepoRoot,
        string DataDir,
        string ModsRoot,
        string DbPath,
        GameDataRepository BaseRepo,
        HashSet<string> Whitelist,
        CardPool Pool,
        List<BazaarServer.Data.MonsterTemplate> Monsters,
        LevelUpTable LevelUps,
        GameModeConfig Mode,
        PvpGhostPool Ghosts);

    private static readonly Lazy<Fixture> Shared = new(LoadFixture);

    private static Fixture LoadFixture()
    {
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        return new Fixture(
            shared.RepoRoot,
            shared.DataDir,
            shared.ModsRoot,
            shared.DbPath,
            shared.Repo,
            shared.CreateValidCardIdsCopy(),
            shared.CreateIsolatedCardPool(),
            shared.CreateMonstersCopy(),
            shared.LevelUps,
            shared.Mode,
            shared.Ghosts);
    }

    private static ModRegistry LoadWorkspaceMods(Fixture fx)
    {
        var registry = new ModRegistry(fx.BaseRepo, fx.Whitelist);
        var report = registry.LoadFromDirectory(fx.ModsRoot);
        Assert.True(
            report.Succeeded,
            "工作区现有 Mod 应零错误加载：" + string.Join("; ", report.Issues.Where(i => i.Severity == ModValidationSeverity.Error).Select(i => i.Message)));
        return registry;
    }

    private static RunEngine CreateEngine(Fixture fx, GameDataRepository repo, CardPool pool, out RunState run)
    {
        run = new RunState
        {
            RunId = "v14_2_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            RngSeed = 20260417,
        };
        var engine = new RunEngine(run, pool, fx.Monsters, fx.LevelUps, fx.Mode, repo, fx.Ghosts);
        engine.StartRun();
        engine.Handle(new CheatCommand { Args = ["skipstart"] });
        engine.Handle(new CheatCommand { Args = ["slots"] });
        engine.Handle(new CheatCommand { Args = ["gold", "200"] });
        return engine;
    }

    [Fact]
    public void Fix1And2And7_ModsPoolRouting_CommonOnlyWarning_AndGenericCounterDisplays()
    {
        var fx = Shared.Value;
        var registry = LoadWorkspaceMods(fx);

        // 1. 核验《过载蓄能炉》在常规英雄池（含 Vanessa 等 8 英雄），而非仅 Common 中立池
        var overloadTpl = registry.ActiveRepository.GetCard(OverloadCardId);
        Assert.NotNull(overloadTpl);
        Assert.Contains(HeroNames.Vanessa, overloadTpl!.Heroes);
        Assert.Contains(HeroNames.Dooley, overloadTpl.Heroes);
        Assert.DoesNotContain(HeroNames.Common, overloadTpl.Heroes);

        // 2. 核验视觉绑定同时下发 TierAttributes 与 CounterDisplays（过载 0/3）
        Assert.True(registry.VisualBindingsByCustomId.TryGetValue(OverloadCardId, out var binding));
        Assert.NotNull(binding!.TierAttributes);
        Assert.Equal(24, binding.TierAttributes!["Silver"]["DamageAmount"]);
        Assert.NotNull(binding.CounterDisplays);
        var cd = Assert.Single(binding.CounterDisplays!);
        Assert.Equal("过载", cd.LabelZh);
        Assert.Equal("Custom_3", cd.SyncAttribute);
        Assert.Equal(3, cd.MaxValue);

        // 3. 核验门禁 7 对仅选 Common 的常规商店卡发出 COMMON_ONLY_SHOP_POOL 提醒，且未手写 counterDisplay 的自定义机制仍能自动推导角标
        var genericMech = new MechanicDefinition
        {
            Id = "demo-pack:sword_intent",
            NameZh = "剑意积攒",
            DescZh = "任意自定义机制示例：不手写 counterDisplay 也能自动推导卡面计数角标。",
            Parameters = [new MechanicParameterDef { Key = "maxStacks", LabelZh = "上限", Type = "int", DefaultValue = JsonSerializer.SerializeToElement(5) }],
            Hooks =
            [
                new MechanicHookDefinition
                {
                    Hook = "OnCardFired",
                    Scope = "SelfCard",
                    Steps =
                    [
                        new MechanicStep
                        {
                            Op = "ModifyCounter",
                            Operation = "Add",
                            Key = "sword_intent",
                            SyncAttribute = "Custom_5",
                            Target = "Self",
                            Value = new MechanicValueExpr { Const = 1 }
                        }
                    ]
                }
            ]
        };

        var commonCard = new CustomCardDefinition
        {
            CustomId = "demo-pack:common_only_blade",
            BaseVisualTemplateId = ZSwordDonorId,
            Type = "Item",
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Always",
            Heroes = [HeroNames.Common],
            Tags = ["Weapon"],
            TitleZh = "中立测试剑",
            DescZh = "仅在 Curio 中立店刷出。",
            ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害"],
            Tiers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 4000, ["DamageAmount"] = 10, ["BuyPrice"] = 4, ["SellPrice"] = 2 },
                    AbilityIds = ["0"]
                }
            },
            Abilities = new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    ActiveIn = "HandOnly",
                    Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                }
            },
            CustomMechanics = [new CardMechanicAttachment { MechanicId = genericMech.Id }]
        };

        var pkg = new ModPackage
        {
            RootDirectory = "<in-memory>",
            Manifest = new ModManifest { Id = "demo-pack", Name = "Demo", Version = "1.0.0" },
        };
        pkg.Cards.Add(commonCard);
        pkg.Mechanics.Add(genericMech);

        var reg2 = new ModRegistry(fx.BaseRepo, fx.Whitelist);
        var rep2 = reg2.LoadPackages([pkg]);
        Assert.True(rep2.Succeeded);
        Assert.Contains(rep2.Issues, i => i.Code == "COMMON_ONLY_SHOP_POOL" && i.Severity == ModValidationSeverity.Warning);
        Assert.True(reg2.VisualBindingsByCustomId.TryGetValue(commonCard.CustomId, out var demoBinding));
        Assert.NotNull(demoBinding!.CounterDisplays);
        var inferred = Assert.Single(demoBinding.CounterDisplays!);
        Assert.Equal("Custom_5", inferred.SyncAttribute);
        Assert.Equal(5, inferred.MaxValue);
        Assert.Equal("剑意积攒", inferred.LabelZh);
    }

    [Fact]
    public void Fix3_CafeUntilEndOfDayTemporaryBuffs_ApplyToPlayerAndCardsAndEnterCombat()
    {
        var fx = Shared.Value;
        _ = CreateEngine(fx, fx.BaseRepo, fx.Pool, out var run);

        // 放一把原版武器到棋盘
        var cutlassTpl = fx.BaseRepo.GetCard(CutlassId)!;
        var cutlassCore = new CardInstance(cutlassTpl, CardTier.Bronze, null, "itm_cafe_weapon");
        int baseDmg = (int)Math.Round(cutlassCore.GetAttribute("DamageAmount"));
        var cutlassEntry = new RunState.CardEntry
        {
            InstanceId = "itm_cafe_weapon",
            TemplateId = Guid.Parse(CutlassId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(cutlassTpl.Size),
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_4,
            CoreCard = cutlassCore,
        };
        RunState.PopulateMetaFromTemplate(cutlassEntry, fx.Pool.Get(CutlassId), cutlassTpl);
        cutlassEntry.Attributes[ECardAttributeType.DamageAmount] = baseDmg;
        run.Cards[cutlassEntry.InstanceId] = cutlassEntry;
        run.HandGrid.TryPlaceWithDisplaceOrSwap(cutlassCore, 4);

        var shop = new ShopEngine(run, fx.Pool, fx.BaseRepo);
        var stepEngine = new EncounterStepEngine(run, fx.Pool, fx.BaseRepo, shop, new Random(123));

        // 1. 咖啡店 Relax：当日最大生命 +10%（UntilEndOfDay）
        int oldMaxHp = run.PlayerAttributes[EPlayerAttributeType.HealthMax];
        var relaxEntry = new RunState.CardEntry
        {
            InstanceId = "step_cafe_relax",
            TemplateId = Guid.Parse(CafeRelaxStepId),
            Type = ECardType.EncounterStep,
            Owner = ECombatantId.Opponent,
            Attributes = { [ECardAttributeType.BuyPrice] = 0 }
        };
        run.SelectionRules = new TSelectionContextRules { SelectionIsFree = true, CanExit = true };
        var relaxRes = stepEngine.ExecuteStep(relaxEntry);
        Assert.True(relaxRes.Success, relaxRes.ErrorMessage);
        Assert.True(run.PlayerDayAdd.TryGetValue("HealthMax", out double dayHpBonus) && dayHpBonus > 0, "Relax 应把 +10% 最大生命写入 RunState.PlayerDayAdd");
        Assert.Equal(oldMaxHp + (int)Math.Round(dayHpBonus), run.PlayerAttributes[EPlayerAttributeType.HealthMax]);

        // 2. 咖啡店 Strong Brew：己方所有武器当日 +25 伤害（UntilEndOfDay）
        var brewEntry = new RunState.CardEntry
        {
            InstanceId = "step_cafe_brew",
            TemplateId = Guid.Parse(CafeStrongBrewStepId),
            Type = ECardType.EncounterStep,
            Owner = ECombatantId.Opponent,
            Attributes = { [ECardAttributeType.BuyPrice] = 0 }
        };
        var brewRes = stepEngine.ExecuteStep(brewEntry);
        Assert.True(brewRes.Success, brewRes.ErrorMessage);
        Assert.Equal(25, cutlassCore.DayAdd.GetValueOrDefault("DamageAmount", 0));
        Assert.Equal(baseDmg + 25, cutlassEntry.Attributes[ECardAttributeType.DamageAmount]);
        Assert.Equal(baseDmg + 25, cutlassEntry.PendingForceSyncAttributes[ECardAttributeType.DamageAmount]);
        Assert.Contains(cutlassEntry.InstanceId, brewRes.ModifiedCardIds);

        // 3. 验证战斗克隆体保留当日临时加成，不丢失
        var p1 = new PlayerState("player", "Player", "Vanessa", run.PlayerAttributes[EPlayerAttributeType.HealthMax], 20, 10, 10);
        p1.Hand.TryPlaceWithDisplaceOrSwap(cutlassCore, 4);
        var cloned = p1.CloneForCombat();
        var clonedWeapon = Assert.Single(cloned.Hand.Cards);
        Assert.Equal(baseDmg + 25, (int)Math.Round(clonedWeapon.GetAttribute("DamageAmount")));
    }

    [Fact]
    public void Fix5And7And4_RealOfficialVfxResolution_StaggeredFireballs_LiveCustomSync_AndNoPermanentLeak()
    {
        var fx = Shared.Value;
        var registry = LoadWorkspaceMods(fx);

        // 1. 验证所有工坊官方 VFX 预设都映射到 GameData.db 中真实存在的 Addressable Prefab 键
        var allRealDbVfxKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in fx.BaseRepo.CardsById.Values)
        {
            foreach (var ab in card.Abilities.Values)
            {
                if (!string.IsNullOrWhiteSpace(ab.VFXConfig?.VFXOverrideKey))
                    allRealDbVfxKeys.Add(ab.VFXConfig.VFXOverrideKey);
            }
        }
        foreach (var preset in ModRuleCatalog.OfficialVfxPresets)
        {
            string? resolved = ModRuleCatalog.ResolveOfficialVfxKey(preset.Id);
            Assert.False(string.IsNullOrWhiteSpace(resolved), $"预设 {preset.Id} 解析为空");
            Assert.Contains(resolved!, allRealDbVfxKeys);
        }

        // 2. 构造实战棋盘：[快速短刃] [过载蓄能炉] [快速短刃]，运行真实 CombatSimulator + CombatSimConverter
        var overloadTpl = registry.ActiveRepository.GetCard(OverloadCardId)!;
        var fastAllyTpl = new CardTemplate
        {
            TypeDiscriminator = "TCardItem",
            Id = CutlassId,
            Type = "Item",
            InternalName = "Fast Ally Blade",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = [HeroNames.Vanessa],
            Tags = ["Weapon"],
            HiddenTags = ["Damage"],
            Tiers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 1500, ["DamageAmount"] = 5, ["BuyPrice"] = 2, ["SellPrice"] = 1 },
                    AbilityIds = ["0"]
                }
            },
            Abilities = new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    ActiveIn = "HandOnly",
                    Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                }
            }
        };

        var p1 = new PlayerState("player", "Player", "Vanessa", 2000, 20, 10, 10);
        var p2 = new PlayerState("opponent", "Dummy", "Common", 2000, 20, 10, 10);

        var leftAlly = new CardInstance(fastAllyTpl, CardTier.Bronze, null, "itm_left");
        var furnace = new CardInstance(overloadTpl, CardTier.Bronze, null, "itm_furnace");
        var rightAlly = new CardInstance(fastAllyTpl, CardTier.Bronze, null, "itm_right");
        p1.Hand.TryPlaceWithDisplaceOrSwap(leftAlly, 2);
        p1.Hand.TryPlaceWithDisplaceOrSwap(furnace, 3);
        p1.Hand.TryPlaceWithDisplaceOrSwap(rightAlly, 5);

        var result = CombatSimulator.Simulate(
            registry.ActiveRepository,
            p1,
            p2,
            seed: 77,
            combatType: "PVP",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: true,
            modifiers: null,
            mechanicDefinitionsOverride: registry.MechanicsById);

        // 3. 验证问题 5：过载引爆后发出了带 VfxKeyOverride="Fireball" 的多发错峰伤害事件
        var fireballEvents = result.Events
            .Where(e => e.Type == CombatEventType.DamageApplied && string.Equals(e.VfxKeyOverride, "Fireball", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(fireballEvents.Count >= 2, $"应触发至少 2 发错峰火球，实际={fireballEvents.Count}");
        Assert.True(fireballEvents[1].TimeMs - fireballEvents[0].TimeMs >= 100, "连发火球应存在时间错峰间隔");

        // 4. 验证问题 7：过载叠层期间发出了 Custom_3 脉冲事件，且帧快照记录了 Custom_3 变化（1 -> 2 -> 3 -> 0）
        var counterPulses = result.Events
            .Where(e => e.Type == CombatEventType.CardAttributeModified && string.Equals(e.Detail, "Custom_3", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(counterPulses);
        Assert.Contains(result.Timeline, f => f.Cards.Any(c => c.InstanceId == "itm_furnace" && c.CustomAttrs != null && c.CustomAttrs[3] > 0));

        // 5. 验证问题 4：本战友军加伤属于 Combat 临时层，战后 PermanentAdd 零污染
        Assert.Equal(0, leftAlly.PermanentAdd.GetValueOrDefault("DamageAmount", 0));
        Assert.Equal(0, rightAlly.PermanentAdd.GetValueOrDefault("DamageAmount", 0));

        // 6. 验证 CombatSimConverter 转换结果：
        //    · VfxKeys 含真实 Unity Prefab 路径（非虚构字符串 "Fireball"）；
        //    · 存在多条指向该真实 VfxIndex 的 CombatSimEventEffectExecuted；
        //    · 帧内 CardUpdates 含 Custom_3 逐帧更新（让客户端实时跳动过载角标与文案）。
        var p1Entries = new List<RunState.CardEntry>
        {
            new() { InstanceId = "itm_left", TemplateId = Guid.Parse(CutlassId), Type = ECardType.Item, Size = ECardSize.Small, Tier = ETier.Bronze, Owner = ECombatantId.Player, Section = EInventorySection.Hand, Socket = EContainerSocketId.Socket_2, CoreCard = leftAlly },
            new() { InstanceId = "itm_furnace", TemplateId = Guid.Parse(ZSwordDonorId), CustomCardId = OverloadCardId, Type = ECardType.Item, Size = ECardSize.Medium, Tier = ETier.Bronze, Owner = ECombatantId.Player, Section = EInventorySection.Hand, Socket = EContainerSocketId.Socket_3, CoreCard = furnace },
            new() { InstanceId = "itm_right", TemplateId = Guid.Parse(CutlassId), Type = ECardType.Item, Size = ECardSize.Small, Tier = ETier.Bronze, Owner = ECombatantId.Player, Section = EInventorySection.Hand, Socket = EContainerSocketId.Socket_5, CoreCard = rightAlly },
        };
        var p1Map = new Dictionary<string, CardInstance>(StringComparer.Ordinal)
        {
            ["itm_left"] = leftAlly,
            ["itm_furnace"] = furnace,
            ["itm_right"] = rightAlly,
        };
        var sim = CombatSimConverter.ToCombatSim(
            result,
            registry.ActiveRepository,
            p1Map,
            new Dictionary<string, CardInstance>(),
            p1Entries,
            Array.Empty<RunState.CardEntry>());

        string expectedRealPrefab = ModRuleCatalog.ResolveOfficialVfxKey("Fireball")!;
        int vfxIdx = sim.VfxKeys.IndexOf(expectedRealPrefab);
        Assert.True(vfxIdx >= 0, $"CombatSim.VfxKeys 应包含真实官方 Prefab 路径 {expectedRealPrefab}，实际=[{string.Join(", ", sim.VfxKeys)}]");

        int emittedFireballVfxCount = sim.Frames
            .SelectMany(f => f.Events.OfType<CombatSimEventEffectExecuted>())
            .Count(ev => ev.VfxIndex == vfxIdx);
        Assert.True(emittedFireballVfxCount >= 2, $"CombatSimEventEffectExecuted 中指向真实火球 Prefab 的动作应 >= 2，实际={emittedFireballVfxCount}");

        bool sawCustom3FrameUpdate = sim.Frames
            .SelectMany(f => f.CardUpdates.Values)
            .Any(cu => cu.CardInstanceId.Value == "itm_furnace"
                    && cu.Attributes.TryGetValue(ECardAttributeType.Custom_3, out var upd)
                    && upd.CurrentValue > 0);
        Assert.True(sawCustom3FrameUpdate, "CombatSim 帧更新必须逐帧下发 Custom_3 叠层数值给客户端");
    }

    [Fact]
    public void Fix6_CustomCardUpgradeScalesDamageAndForceSyncs_AndFuseIsolatesByCustomCardId()
    {
        var fx = Shared.Value;
        var registry = LoadWorkspaceMods(fx);
        var modPool = CardPool.FromRepository(registry.ActiveRepository, fx.DataDir, fx.Whitelist, registry.VisualBindingsByCustomId);
        var engine = CreateEngine(fx, registry.ActiveRepository, modPool, out var run);

        var overloadTpl = registry.ActiveRepository.GetCard(OverloadCardId)!;
        var furnaceCore = new CardInstance(overloadTpl, CardTier.Bronze, null, "itm_custom_furnace");
        var furnaceEntry = new RunState.CardEntry
        {
            InstanceId = "itm_custom_furnace",
            TemplateId = Guid.Parse(ZSwordDonorId),
            CustomCardId = OverloadCardId,
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = furnaceCore,
        };
        RunState.PopulateMetaFromTemplate(furnaceEntry, modPool.Get(OverloadCardId), overloadTpl);
        furnaceEntry.Attributes[ECardAttributeType.DamageAmount] = 12;
        run.Cards[furnaceEntry.InstanceId] = furnaceEntry;
        run.HandGrid.TryPlaceWithDisplaceOrSwap(furnaceCore, 3);

        // 1. 商店刷出另一张同载体（Z-Sword）但 CustomCardId 不同的卡：购买时绝不可与《过载蓄能炉》误触发合卡
        var otherDonorEntry = new RunState.CardEntry
        {
            InstanceId = "shop_vanilla_zsword",
            TemplateId = Guid.Parse(ZSwordDonorId),
            CustomCardId = null, // 原版 Z-Sword
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Opponent,
            Attributes = { [ECardAttributeType.BuyPrice] = 4, [ECardAttributeType.SellPrice] = 2 }
        };
        run.Cards[otherDonorEntry.InstanceId] = otherDonorEntry;
        run.SelectionSet.Clear();
        run.SelectionSet.Add(otherDonorEntry.InstanceId);
        run.SelectionRules = new TSelectionContextRules { CanSelectMultiple = true, SelectionIsFree = false, CanExit = true };
        run.State = ERunState.Encounter;

        var buyOtherOutcome = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(otherDonorEntry.InstanceId),
        });
        Assert.Null(buyOtherOutcome.Error);
        Assert.Equal(ETier.Bronze, furnaceEntry.Tier); // 未被原版同载体卡误合

        // 2. 商店刷出同名自定义卡《过载蓄能炉》青铜：购买触发正规融合升阶 Bronze -> Silver，基础伤害 12 -> 24 并强刷 PendingForceSyncAttributes
        var dupCustomCore = new CardInstance(overloadTpl, CardTier.Bronze, null, "shop_dup_furnace");
        var dupCustomEntry = new RunState.CardEntry
        {
            InstanceId = "shop_dup_furnace",
            TemplateId = Guid.Parse(ZSwordDonorId),
            CustomCardId = OverloadCardId,
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Opponent,
            CoreCard = dupCustomCore,
            Attributes = { [ECardAttributeType.BuyPrice] = 4, [ECardAttributeType.SellPrice] = 2 }
        };
        run.Cards[dupCustomEntry.InstanceId] = dupCustomEntry;
        run.SelectionSet.Clear();
        run.SelectionSet.Add(dupCustomEntry.InstanceId);

        var fuseOutcome = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(dupCustomEntry.InstanceId),
        });
        Assert.Null(fuseOutcome.Error);
        Assert.Equal(ETier.Silver, furnaceEntry.Tier);
        Assert.Equal(24, furnaceEntry.Attributes[ECardAttributeType.DamageAmount]);
        Assert.Equal(24, furnaceEntry.PendingForceSyncAttributes[ECardAttributeType.DamageAmount]);
        Assert.Contains(furnaceEntry.InstanceId, fuseOutcome.DirtyCardIds);
    }

    [Fact]
    public void Fix8_CustomCardShopSpawnRateIsFairUnownedAndRespectsOwnedTier_AndDonorIsolated()
    {
        var fx = Shared.Value;
        var registry = LoadWorkspaceMods(fx);
        var modPool = CardPool.FromRepository(registry.ActiveRepository, fx.DataDir, fx.Whitelist, registry.VisualBindingsByCustomId);

        const string jayJayId = "85db60ee-3619-4e39-bfb2-2f4ac56f172b";
        var run = new RunState
        {
            RunId = "test_spawn_rate",
            Hero = EHero.Vanessa,
            Day = 1,
            Hour = 1,
            State = ERunState.Encounter,
            CurrentEncounterId = jayJayId,
        };
        var shop = new ShopEngine(run, modPool, registry.ActiveRepository);

        // 1. Day 1 未持有时：模拟 600 次 Jay Jay 商店抽卡，核验自定义卡《过载蓄能炉》与同级别原版中卡享有完全公平的出现概率
        int day1OverloadHits = 0;
        int day1CutlassHits = 0;
        for (int i = 0; i < 600; i++)
        {
            shop.DealEncounterShelf(jayJayId, isReroll: i > 0);
            foreach (var iid in run.SelectionSet)
            {
                if (!run.Cards.TryGetValue(iid, out var ce)) continue;
                string coreId = RunState.ResolveCoreTemplateId(ce);
                if (string.Equals(coreId, OverloadCardId, StringComparison.OrdinalIgnoreCase))
                    day1OverloadHits++;
                else if (string.Equals(coreId, CutlassId, StringComparison.OrdinalIgnoreCase))
                    day1CutlassHits++;
            }
        }
        // Day 1 瓦妮莎青铜合法池约 58 张，每滚 3 格理论均值 ≈ 31 次/600 滚，统计下界保守取 >= 12 次
        Assert.True(
            day1OverloadHits >= 12,
            $"Day 1 未持有《过载蓄能炉》600 次 Jay Jay 商店刷新命中次数偏低: overload={day1OverloadHits}, cutlass={day1CutlassHits}");

        // 2. BuildOwnedMaxTiers 必须记录 CustomCardId 而非原版载体 GUID（防止持有自定义卡误封锁原版 Z-Sword）
        var overloadTpl = registry.ActiveRepository.GetCard(OverloadCardId)!;
        var ownedDiamondCustom = new RunState.CardEntry
        {
            InstanceId = "itm_owned_diamond_overload",
            TemplateId = Guid.Parse(ZSwordDonorId),
            CustomCardId = OverloadCardId,
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Diamond,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = new CardInstance(overloadTpl, CardTier.Diamond, null, "itm_owned_diamond_overload"),
        };
        run.Cards[ownedDiamondCustom.InstanceId] = ownedDiamondCustom;
        var ownedTiers = ShopEngine.BuildOwnedMaxTiers(run);
        Assert.True(ownedTiers.TryGetValue(OverloadCardId, out var recordedCustomTier));
        Assert.Equal(CardTier.Diamond, recordedCustomTier);
        Assert.False(ownedTiers.ContainsKey(ZSwordDonorId), "持有自定义卡绝不应在 BuildOwnedMaxTiers 中污染原版载体 GUID");
    }

    [Fact]
    public void Fix9_PaginatedMonsterLootOver10Slots_EmitsRerollCostChangedAndCyclesAllPages()
    {
        var fx = Shared.Value;
        var registry = LoadWorkspaceMods(fx);
        var modPool = CardPool.FromRepository(registry.ActiveRepository, fx.DataDir, fx.Whitelist, registry.VisualBindingsByCustomId);
        var engine = CreateEngine(fx, registry.ActiveRepository, modPool, out var run);
        run.CustomRules.MonsterLootChooseOne = true;
        run.CustomRules.MonsterLootBonusChoices = 1; // 允许 2 选，验证翻页不覆盖多选次数

        // 构造一只总掉落超过 10 格（6 件 Medium = 12 格）的野怪，验证自动分页 + 下发免费刷新事件 + 翻页完整呈现第 2 页奖励
        var medItems = modPool.OfType("Item")
            .Where(t => modPool.IsPlayerSpawnable(t, allowGuidOnly: false)
                     && string.Equals(t.Size, "Medium", StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToList();
        Assert.Equal(6, medItems.Count);

        var bigMonster = new BazaarServer.Data.MonsterTemplate
        {
            Id = "test_big_loot_monster",
            InternalName = "Test Big Loot Monster",
            Level = 5,
            Items = medItems.Select((t, idx) => new BazaarServer.Data.MonsterTemplate.MonsterItem
            {
                SocketId = $"Socket_{idx}",
                TemplateId = t.Id,
                Tier = t.StartingTier,
            }).ToList(),
        };

        var shopField = typeof(RunEngine).GetField("_shop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var shop = (ShopEngine)shopField.GetValue(engine)!;

        var dealEvents = shop.PreparePaginatedMonsterLoot(bigMonster, combatEncounterTpl: null, p1Core: null);
        Assert.True(shop.MonsterLootPages.Count >= 2, $"12 格战利品应拆分为至少 2 页，实际={shop.MonsterLootPages.Count}");
        Assert.Equal(ERunState.Loot, run.State);
        Assert.Equal(0u, run.RerollCost);
        Assert.Equal(ShopEngine.UnlimitedRerollsCount, run.RerollsRemaining);
        Assert.Equal(2, shop.CurrentMaxSelections);
        Assert.Contains(dealEvents, e => e is GameSimEventRunRerollCostChanged rc && rc.NewCost == 0);

        var page0Tpls = run.SelectionSet
            .Select(id => run.Cards[id].TemplateId.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 点击免费刷新翻页至第 2 页
        var rerollOutcome = engine.Handle(new RerollCommand());
        Assert.Null(rerollOutcome.Error);
        Assert.Equal(1, shop.CurrentMonsterLootPageIndex);
        Assert.Equal(2, shop.CurrentMaxSelections); // 翻页后保留多选次数
        Assert.Contains(rerollOutcome.Events, e => e is GameSimEventRunRerollCostChanged rc && rc.NewCost == 0);

        var page1Tpls = run.SelectionSet
            .Select(id => run.Cards[id].TemplateId.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(page1Tpls);
        var union = new HashSet<string>(page0Tpls, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(page1Tpls);
        Assert.Equal(6, union.Count); // 全部 6 件（12 格）战利品 0 丢失！
    }

    [Fact]
    public void Fix10_ShinyMulticastDefaultBase_CompoundActionCanCrit_AndHiddenTagMismatchWarning()
    {
        var fx = Shared.Value;
        var registry = LoadWorkspaceMods(fx);
        var overloadTpl = registry.ActiveRepository.GetCard(OverloadCardId)!;

        // 1. 《过载蓄能炉》本体不应再误带 Burn 隐藏标签；附魔 Fiery 后才自动带上 Burn
        var plainFurnace = new CardInstance(overloadTpl, CardTier.Diamond, null, "itm_plain");
        Assert.False(plainFurnace.HasHiddenTag("Burn"), "本体不含灼烧动作，不应误带 Burn 隐藏标签");
        var fieryFurnace = new CardInstance(overloadTpl, CardTier.Diamond, "Fiery", "itm_fiery");
        Assert.True(fieryFurnace.HasHiddenTag("Burn"), "附魔 Fiery 后应自动携带 Burn 隐藏标签");

        // 2. 闪亮（Shiny）附魔在未显式声明基础 Multicast=1 的自定义卡上必须真正生效为 Multicast=2
        var shinyFurnace = new CardInstance(overloadTpl, CardTier.Diamond, "Shiny", "itm_shiny");
        var p1 = new PlayerState("p1", "P1", "Vanessa", 2000, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", "Common", 2000, 20, 10, 10);
        p1.Hand.TryPlaceWithDisplaceOrSwap(shinyFurnace, 2);

        // 同时挂上 Keen Eye（b49dd22b）验证复合动作 TActionAnd 能被 EvaluateCanCrit 正确识别并获得 +8% 暴击
        var keenEyeTpl = registry.ActiveRepository.GetCard("b49dd22b-ef95-4f7e-954e-556e1e431d4a")!;
        p1.Skills.Add(new CardInstance(keenEyeTpl, CardTier.Silver, null, "skl_keen_eye"));
        OutOfCombatController.RecalculateOutOfCombatAuras(p1, registry.ActiveRepository);

        Assert.Equal(2, (int)Math.Round(shinyFurnace.GetAttribute("Multicast")));
        Assert.True(shinyFurnace.GetAttribute("CritChance") >= 8, $"闪亮过载蓄能炉应能吃到 Keen Eye 暴击光环，实际 CritChance={shinyFurnace.GetAttribute("CritChance")}");

        // 3. 兼容旧版 attributes: { Multicast: 1 } 写法（含原版 Combat Core）同样必须得到 1+1=2
        var legacyShinyTpl = new CardTemplate
        {
            Id = "test:legacy_shiny_item",
            Type = "Item",
            Size = "Medium",
            StartingTier = "Bronze",
            Tiers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 4000, ["DamageAmount"] = 20 } }
            },
            Enchantments = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Shiny"] = new EnchantmentData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["Multicast"] = 1 } }
            }
        };
        var legacyShinyCard = new CardInstance(legacyShinyTpl, CardTier.Bronze, "Shiny", "itm_legacy_shiny");
        Assert.Equal(2, (int)Math.Round(legacyShinyCard.GetAttribute("Multicast")));

        // 4. 真实战斗中闪亮钻石过载蓄能炉（3.5s CD）在首次开火时应触发 2 次开火事件（Multicast=2）
        var simRes = CombatSimulator.Simulate(
            registry.ActiveRepository,
            p1,
            p2,
            seed: 42,
            combatType: "PVP",
            recordTimeline: false,
            recordEvents: true,
            syncPermanentChanges: false,
            modifiers: null,
            mechanicDefinitionsOverride: registry.MechanicsById);
        int firstCycleFires = simRes.Events.Count(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == "itm_shiny" && e.TimeMs <= 5000);
        Assert.True(firstCycleFires >= 2, $"闪亮过载蓄能炉首个冷却周期内应连发 2 次，实际={firstCycleFires}");

        // 5. ModValidator 门禁 7：真实工作区 Mod（含 clockwork-forge）无误勾告警；故意勾错 Burn 且无灼烧动作时给出 HIDDEN_TAG_WITHOUT_MATCHING_EFFECT 告警
        var realReport = new ModRegistry(fx.BaseRepo, fx.Whitelist).LoadFromDirectory(fx.ModsRoot);
        Assert.True(realReport.Succeeded);
        Assert.DoesNotContain(realReport.Issues, i => i.Code == "HIDDEN_TAG_WITHOUT_MATCHING_EFFECT");

        var bogusPkg = new ModPackage
        {
            RootDirectory = "<in-memory>",
            Manifest = new ModManifest { Id = "test-htag-warn", Name = "HiddenTag Warn", Version = "1.0.0" },
        };
        bogusPkg.Cards.Add(new CustomCardDefinition
        {
            CustomId = "test-htag-warn:fake_burn_sword",
            BaseVisualTemplateId = ZSwordDonorId,
            Type = "Item",
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Always",
            Heroes = [HeroNames.Vanessa],
            Tags = ["Weapon"],
            HiddenTags = ["Burn", "Damage"],
            TitleZh = "假火刃",
            DescZh = "测试误勾灼烧效果标签",
            Tiers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 4000, ["DamageAmount"] = 10, ["BuyPrice"] = 4, ["SellPrice"] = 2 } }
            }
        });
        var bogusReport = new ModRegistry(fx.BaseRepo, fx.Whitelist).LoadPackages([bogusPkg]);
        Assert.Contains(bogusReport.Issues, i => i.Code == "HIDDEN_TAG_WITHOUT_MATCHING_EFFECT" && i.Severity == ModValidationSeverity.Warning);
    }

    [Fact]
    public void Fix_OutputAttributeGuard_PreventsAddingDamageToNonDamageItems_AndSupportsExtendedCounterDisplays()
    {
        var fx = Shared.Value;
        var registry = LoadWorkspaceMods(fx);

        // 1. 构造「过载蓄能炉」+ 一件武器（有 DamageAmount）+ 两件非伤害物品（纯毒/纯盾工具），验证满 3 层过载引爆时：
        //    - 仅给具备伤害能力的武器加 +4 DamageAmount；
        //    - 严禁给纯护盾/纯剧毒/工具卡凭空写入 DamageAmount（防止客户端常显无效红字伤害宝石）。
        var overloadTpl = registry.ActiveRepository.GetCard(OverloadCardId)!;
        var weaponTpl = new CardTemplate
        {
            Id = "test:ally_weapon",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tags = ["Weapon"],
            Tiers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 2000, ["DamageAmount"] = 10 },
                    AbilityIds = ["0"]
                }
            },
            Abilities = new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    ActiveIn = "HandOnly",
                    Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                }
            }
        };
        var nonDamageShieldTpl = new CardTemplate
        {
            Id = "test:ally_shield_only",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tags = ["Tool"],
            Tiers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 2000, ["ShieldApplyAmount"] = 15 },
                    AbilityIds = ["0"]
                }
            },
            Abilities = new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    ActiveIn = "HandOnly",
                    Action = new RuleNode { Type = "TActionPlayerShieldApply", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" } }
                }
            }
        };

        var p1 = new PlayerState("p1", "P1", "Vanessa", 2000, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", "Common", 2000, 20, 10, 10);
        var shieldCard = new CardInstance(nonDamageShieldTpl, CardTier.Bronze, null, "itm_shield_left");
        var furnaceCard = new CardInstance(overloadTpl, CardTier.Bronze, null, "itm_furnace_mid");
        var weaponCard = new CardInstance(weaponTpl, CardTier.Bronze, null, "itm_weapon_right");
        p1.Hand.TryPlaceWithDisplaceOrSwap(shieldCard, 0);
        p1.Hand.TryPlaceWithDisplaceOrSwap(furnaceCard, 1);
        p1.Hand.TryPlaceWithDisplaceOrSwap(weaponCard, 3);

        var sim = CombatSimulator.Simulate(
            registry.ActiveRepository,
            p1,
            p2,
            seed: 77,
            combatType: "PVP",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: false,
            modifiers: null,
            mechanicDefinitionsOverride: registry.MechanicsById);

        // 2s 时左右相邻各开火 1 次 (+2 层)，4s 再各开火 (+2 层 -> 满 3 层)，5s 蓄能炉开火触发过载引爆！
        bool weaponBuffed = sim.Timeline.Any(f =>
            f.Cards.Any(c => c.InstanceId == "itm_weapon_right" && c.DamageAmount >= 14));
        bool nonDamageCorrupted = sim.Timeline.Any(f =>
            f.Cards.Any(c => c.InstanceId == "itm_shield_left" && c.DamageAmount > 0));

        Assert.True(weaponBuffed, "过载引爆应正常给有伤害能力的友方武器增加 +4 DamageAmount");
        Assert.False(nonDamageCorrupted, "严禁给无伤害能力的纯盾/工具物品凭空写入 DamageAmount");

        // 2. 验证卡牌级直配 counterDisplays 及扩展样式字段（style/valueType/format/unit/position）完整解析下发
        var cardLevelBadgeCard = new CustomCardDefinition
        {
            CustomId = "demo-badge:custom_gauge",
            BaseVisualTemplateId = ZSwordDonorId,
            Type = "Item",
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Always",
            Heroes = [HeroNames.Vanessa],
            Tags = ["Tool"],
            TitleZh = "热量压力表",
            DescZh = "测试卡牌级扩展徽章声明。",
            CounterDisplays =
            [
                new MechanicCounterDisplay
                {
                    LabelZh = "热量",
                    SyncAttribute = "Custom_2",
                    MaxConst = 100,
                    ColorHex = "#EF4444",
                    Style = "bar",
                    ValueType = "percent",
                    Unit = "",
                    Position = "bottomRight",
                }
            ],
            Tiers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 4000, ["Custom_2"] = 25, ["BuyPrice"] = 4, ["SellPrice"] = 2 } }
            }
        };
        var badgePkg = new ModPackage
        {
            RootDirectory = "<in-memory>",
            Manifest = new ModManifest { Id = "demo-badge", Name = "Badge Demo", Version = "1.0.0" },
        };
        badgePkg.Cards.Add(cardLevelBadgeCard);
        var regBadge = new ModRegistry(fx.BaseRepo, fx.Whitelist);
        var repBadge = regBadge.LoadPackages([badgePkg]);
        Assert.True(repBadge.Succeeded);
        Assert.True(regBadge.VisualBindingsByCustomId.TryGetValue(cardLevelBadgeCard.CustomId, out var bBind));
        var resolvedCd = Assert.Single(bBind!.CounterDisplays!);
        Assert.Equal("热量", resolvedCd.LabelZh);
        Assert.Equal("bar", resolvedCd.Style);
        Assert.Equal("percent", resolvedCd.ValueType);
        Assert.Equal("bottomRight", resolvedCd.Position);
        Assert.Equal(100, resolvedCd.MaxValue);

        // 3. 验证《过载蓄能炉》全四阶预览属性（含钻石 5 发/96 伤/3500ms）与结构化附魔预览运算表（黑曜石×2、黄金×2、致命+50、闪亮+1）完整下发
        Assert.True(registry.VisualBindingsByCustomId.TryGetValue(OverloadCardId, out var overloadBinding));
        Assert.NotNull(overloadBinding!.TierAttributes);
        Assert.Equal(5, overloadBinding.TierAttributes!["Diamond"]["Custom_0"]);
        Assert.Equal(75, overloadBinding.TierAttributes["Diamond"]["Custom_1"]);
        Assert.Equal(32, overloadBinding.TierAttributes["Diamond"]["Custom_2"]);
        Assert.Equal(96, overloadBinding.TierAttributes["Diamond"]["DamageAmount"]);
        Assert.Equal(3500, overloadBinding.TierAttributes["Diamond"]["CooldownMax"]);
        Assert.Equal(16, overloadBinding.TierAttributes["Diamond"]["SellPrice"]);
        Assert.NotNull(overloadBinding.EnchantmentPreviewOps);
        Assert.True(overloadBinding.EnchantmentPreviewOps!.ContainsKey("Golden"));
        Assert.Contains(overloadBinding.EnchantmentPreviewOps["Golden"], op => op.Attribute == "SellPrice" && op.Operation == "Multiply" && Math.Abs(op.Value - 2) < 1e-6);
        Assert.True(overloadBinding.EnchantmentPreviewOps.ContainsKey("Obsidian"));
        Assert.Contains(overloadBinding.EnchantmentPreviewOps["Obsidian"], op => op.Attribute == "DamageAmount" && op.Operation == "Multiply" && Math.Abs(op.Value - 2) < 1e-6);
        Assert.True(overloadBinding.EnchantmentPreviewOps.ContainsKey("Deadly"));
        Assert.Contains(overloadBinding.EnchantmentPreviewOps["Deadly"], op => op.Attribute == "CritChance" && op.Operation == "Add" && Math.Abs(op.Value - 50) < 1e-6);
        Assert.True(overloadBinding.EnchantmentPreviewOps.ContainsKey("Shiny"));
        Assert.Contains(overloadBinding.EnchantmentPreviewOps["Shiny"], op => op.Attribute == "Multicast" && op.Operation == "Add" && Math.Abs(op.Value - 1) < 1e-6);
    }

    [Fact]
    public void GeneralityHardening_ZeroBaseScalingWeapon_CommunityCounters_AndOpponentActiveBindings()
    {
        var fx = Shared.Value;

        // 1. 验证 0 初始成长型武器（DamageAmount = 0）正常接受伤害增益；非输出物品被拦截；grantIfMissing=true 显式放行
        var zeroScalingWeaponTpl = new CardTemplate
        {
            Id = "test:zero_scaling_weapon",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tags = ["Weapon"],
            Tiers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 3000, ["DamageAmount"] = 0 },
                    AbilityIds = []
                }
            }
        };
        var pureToolTpl = new CardTemplate
        {
            Id = "test:pure_tool_no_dmg",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tags = ["Tool"],
            Tiers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 3000 },
                    AbilityIds = []
                }
            }
        };

        var p1 = new PlayerState("p1", "P1", "Vanessa", 1000, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", "Common", 1000, 20, 10, 10);
        var scalingWeapon = new CardInstance(zeroScalingWeaponTpl, CardTier.Bronze, null, "itm_zero_weapon");
        var pureTool = new CardInstance(pureToolTpl, CardTier.Bronze, null, "itm_pure_tool");
        p1.Hand.TryPlaceWithDisplaceOrSwap(scalingWeapon, 0);
        p1.Hand.TryPlaceWithDisplaceOrSwap(pureTool, 1);

        Assert.True(CombatMechanicRuntime.CardSupportsOutputAttribute(scalingWeapon, "DamageAmount"), "0 初始成长型武器应判定为支持 DamageAmount");
        Assert.False(CombatMechanicRuntime.CardSupportsOutputAttribute(pureTool, "DamageAmount"), "无伤害基础键与动作的纯工具物品应判定为不支持 DamageAmount");

        var callbacks = new CombatMechanicHostCallbacks
        {
            GetTimeMs = () => 1000,
            DealDamage = (_, _, _, _, _) => { },
            ApplyStatus = (_, _, _, _, _, _) => { },
            ChangeCardState = (_, _, _, _, _, _) => { },
            ForceUseCard = (_, _) => { },
            RecalculateAuras = () => { },
            IsValidVfxKey = _ => true
        };
        var runtime = new CombatMechanicRuntime(p1, p2, new Random(1), callbacks, null, Array.Empty<ICombatPipelineInterceptor>());

        runtime.ModifyCardAttribute(scalingWeapon, "DamageAmount", "Add", 10, permanent: false, grantIfMissing: false);
        runtime.ModifyCardAttribute(pureTool, "DamageAmount", "Add", 10, permanent: false, grantIfMissing: false);
        Assert.Equal(10, scalingWeapon.GetAttribute("DamageAmount"));
        Assert.Equal(0, pureTool.GetAttribute("DamageAmount"));

        runtime.ModifyCardAttribute(pureTool, "DamageAmount", "Add", 7, permanent: false, grantIfMissing: true);
        Assert.Equal(7, pureTool.GetAttribute("DamageAmount"));

        runtime.QueuePostCombatModifyAttr(pureTool, "DamageAmount", "Add", 5, "test_mech", grantIfMissing: false);
        runtime.QueuePostCombatModifyAttr(pureTool, "DamageAmount", "Add", 9, "test_mech", grantIfMissing: true);
        Assert.Equal(2, runtime.PostCombatOps.Count);
        Assert.False(runtime.PostCombatOps[0].GrantIfMissing);
        Assert.True(runtime.PostCombatOps[1].GrantIfMissing);

        // 2. 验证 ModLoader.LoadSinglePackage 直接为社区/房间隔离 Mod 填充 CounterDisplays + TierAttributes + EnchantmentPreviewOps
        var loadIssues = new List<ModValidationIssue>();
        var clockworkPkg = ModLoader.LoadSinglePackage(Path.Combine(fx.ModsRoot, "clockwork-forge"), "clockwork-forge", loadIssues);
        Assert.NotNull(clockworkPkg);
        Assert.True(clockworkPkg!.VisualBindingsByCustomId.TryGetValue(OverloadCardId, out var pkgBinding));
        Assert.NotNull(pkgBinding!.CounterDisplays);
        Assert.Contains(pkgBinding.CounterDisplays!, d => d.SyncAttribute == "Custom_3" && d.MaxValue == 3);
        Assert.NotNull(pkgBinding.TierAttributes);
        Assert.Equal(5, pkgBinding.TierAttributes!["Diamond"]["Custom_0"]);
        Assert.NotNull(pkgBinding.EnchantmentPreviewOps);
        Assert.True(pkgBinding.EnchantmentPreviewOps!.ContainsKey("Obsidian"));

        // 3. 验证 ServerModHost.CollectActiveVisualBindings 同时收录玩家卡牌与 PvP/锦标赛对手瞬态自定义卡牌
        //    同时覆盖“单局/房间隔离 SelectedModIds（全局未启用）”与“全局启用”两条真实路径
        string tmpStateDir = Path.Combine(Path.GetTempPath(), $"bz_modhost_test_{Guid.NewGuid():N}");
        try
        {
            var host = new ServerModHost(fx.BaseRepo, fx.DataDir, tmpStateDir, fx.Whitelist, modsRootOverride: fx.ModsRoot);
            host.Reload();

            var run = new RunState { Hero = EHero.Vanessa };
            run.CustomRules.SelectedModIds.Add("clockwork-forge");
            run.Cards["p1_custom_item"] = new RunState.CardEntry
            {
                InstanceId = "p1_custom_item",
                TemplateId = Guid.Parse(ZSwordDonorId),
                CustomCardId = OverloadCardId,
                Type = ECardType.Item,
                Owner = ECombatantId.Player
            };
            run.ActiveCombatOpponentCustomCardIds["opp_custom_item"] = OverloadCardId;

            // 路径 A：单局/房间社区 Mod 隔离模式（全局未启用）
            var activeScoped = host.CollectActiveVisualBindings(run);
            Assert.True(activeScoped.ContainsKey("p1_custom_item"), "房间隔离模式应收录玩家自己的自定义卡实例");
            Assert.True(activeScoped.ContainsKey("opp_custom_item"), "房间隔离模式应收录对手幽灵/锦标赛的自定义卡实例");
            Assert.Equal(OverloadCardId, activeScoped["opp_custom_item"].CustomTemplateId);
            Assert.NotNull(activeScoped["opp_custom_item"].CounterDisplays);
            Assert.Contains(activeScoped["opp_custom_item"].CounterDisplays!, d => d.SyncAttribute == "Custom_3" && d.MaxValue == 3);

            // 路径 B：服务器全局启用模式（不依赖 SelectedModIds）
            run.CustomRules.SelectedModIds.Clear();
            host.SetModEnabledAndReload("clockwork-forge", true);
            var activeGlobal = host.CollectActiveVisualBindings(run);
            Assert.True(activeGlobal.ContainsKey("p1_custom_item"), "全局启用模式应收录玩家自己的自定义卡实例");
            Assert.True(activeGlobal.ContainsKey("opp_custom_item"), "全局启用模式应收录对手幽灵/锦标赛的自定义卡实例");
            Assert.NotNull(activeGlobal["opp_custom_item"].CounterDisplays);
        }
        finally
        {
            if (Directory.Exists(tmpStateDir))
                Directory.Delete(tmpStateDir, recursive: true);
        }
    }
}
