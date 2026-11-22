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
using BazaarServer.Data;
using BazaarServer.Game;

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
        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        string? repoRoot = null;
        while (cur != null)
        {
            if (File.Exists(Path.Combine(cur.FullName, "Bazaar.sln")))
            {
                repoRoot = cur.FullName;
                break;
            }
            cur = cur.Parent;
        }
        Assert.False(string.IsNullOrWhiteSpace(repoRoot), "未定位到 Bazaar.sln 根目录");
        string dataDir = Path.Combine(repoRoot!, "dist", "data");
        string modsRoot = Path.Combine(repoRoot!, "mods");
        string dbPath = Path.Combine(dataDir, "GameData.db");
        string? zhPath = SteamPathResolver.ResolveZhCnTranslationPath(dataDir);
        var baseRepo = GameDatabaseLoader.Load(dbPath, zhPath);

        var wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "valid_card_ids.json"), Encoding.UTF8)))
        {
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("valid_ids");
            foreach (var it in arr.EnumerateArray())
                if (it.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(it.GetString()))
                    wl.Add(it.GetString()!);
        }

        return new Fixture(
            repoRoot!,
            dataDir,
            modsRoot,
            dbPath,
            baseRepo,
            wl,
            CardPool.Load(dbPath),
            MonsterPool.Load(dbPath),
            LevelUpTable.Load(dbPath),
            GameModeConfig.Load(dbPath),
            PvpGhostPool.Load(dataDir));
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
}
