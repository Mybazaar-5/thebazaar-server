using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2 三轨合流 7 项跨轨胶水端到端集成验收测试（[Collection("GlobalStaticState")]）。
/// 完整覆盖胶水 1~7：
///   - 胶水 1：CardPool 原位 Patch vs 新卡隔离 + IsUniversalCustomSkill 通用技能放行；
///   - 胶水 2：CardPool.MapFromCoreTemplate 透传 AllowedHours / ModDifficultySlot → ShopEngine 与 OfficialDaySchedule 调度闭环；
///   - 胶水 3：RunEngine.ApplyPostCombatMechanicOps 委托单一权威 MechanicSafeAttributeWriter；
///   - 胶水 4：CustomModes / IGameModeHook / DeclarativeGameModeHook + 隔离单局自定义突变词条（开局 → 商店 → 跨天 → 战斗 → 计分）；
///   - 胶水 5：PvpGhostPool & GameServerEndpoints 统一委托 ServerModHost.ResolveEffectiveGhostPoolPartition / ComputeEffectiveModSetHash；
///   - 胶水 6：RunEngine.RunRealCombatSequence 透传非 GUID 变形借壳解析与临时视觉绑定回调 + VisualBindingsRevision 自增；
///   - 胶水 7：战报持久化 VisualBindingsJson → SpectateEndpoints.BuildReplayDetailDto（GET /api/replays/{id}）+ GameDefaults 全栈统一。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave2Wire_CrossTrackIntegrationTests
{
    private const string SmallItemCarrier = "03b1fd16-6b0d-4847-a76f-6c586f7c5958";
    private const string CutlassId = "7b80a5e9-1895-4c87-a011-7d5c3e10908e";
    private const string CombatEncCarrier = "bd0711a6-d9d9-4a47-8a18-8f991057f7dd";
    private const string EventEncCarrier = "3212ade4-8e23-49cd-b9b5-f729280ba0b5"; // Wishing Fountain

    private static string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>
    /// 胶水 1 + 胶水 2 端到端闭环：
    /// 1) 原位 Patch 卡（IsPatchOverride=true）绝不误判为 IsCustomModCard，且不入借壳映射表 CustomToBaseVisualId；
    /// 2) 通用自定义技能（IsUniversalCustomSkill=true, Common）对全英雄放行，原版非自定义 Common 技能仍隔离；
    /// 3) 自定义事件 AllowedHours=[2, 4] 在 Day 2 Hour 1 屏蔽、Day 2 Hour 2 命中，自定义野怪 ModDifficultySlot="Gold" 透传生效。
    /// </summary>
    [Fact]
    public void Wire1_2_CardPool_PatchVsNewCard_UniversalSkill_AndAllowedHoursDifficultySlot_EndToEnd()
    {
        var shared = SharedGameDataFixture.Shared;
        var baseCutlass = shared.Repo.GetCard(CutlassId)
            ?? shared.Repo.CardsById.Values.First(c => c.Type == "Item" && shared.Pool.IsAssetValid(c.Id));
        var baseEvent = shared.Repo.GetCard(EventEncCarrier)!;

        // 1. 原位 Patch 卡（复用原版 GUID，IsPatchOverride = true）
        var patchedCore = new BazaarCore.Models.CardTemplate
        {
            Id = baseCutlass.Id,
            InternalName = baseCutlass.InternalName,
            Type = baseCutlass.Type,
            Size = baseCutlass.Size,
            StartingTier = baseCutlass.StartingTier,
            Heroes = baseCutlass.Heroes.ToList(),
            Tags = baseCutlass.Tags.ToList(),
            Tiers = baseCutlass.Tiers,
            IsCustomModCard = false,
        };
        var patchBinding = new CustomCardVisualBinding
        {
            ModId = "wire-patch-mod",
            CustomTemplateId = baseCutlass.Id,
            BaseVisualTemplateId = SmallItemCarrier,
            IsPatchOverride = true,
        };

        // 2. 通用自定义技能（IsCustomModCard=true, IsUniversalCustomSkill=true, Common）
        const string customSkillId = "wire-mod:universal_arcane_focus";
        var customSkillCore = new BazaarCore.Models.CardTemplate
        {
            Id = customSkillId,
            InternalName = "Universal Arcane Focus",
            Type = "Skill",
            Size = "Medium",
            StartingTier = "Bronze",
            Heroes = new List<string> { "Common" },
            Tags = new List<string> { "Skill" },
            SpawningEligibility = "Always",
            IsCustomModCard = true,
            IsUniversalCustomSkill = true,
            CustomModOrigin = "wire-mod",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["BuyPrice"] = 4, ["SellPrice"] = 2 } },
            },
        };
        var skillBinding = new CustomCardVisualBinding
        {
            ModId = "wire-mod",
            CustomTemplateId = customSkillId,
            BaseVisualTemplateId = shared.Pool.Skills("Vanessa").First().Id,
            IsPatchOverride = false,
        };

        // 3. 带 AllowedHours=[2, 4] + IsSingleSpawn=true 的自定义事件
        const string customEventId = "wire-mod:hour2_astral_event";
        var customEventCore = new BazaarCore.Models.CardTemplate
        {
            Id = customEventId,
            InternalName = "Astral Hour 2 Event",
            Type = "EventEncounter",
            Size = "Medium",
            StartingTier = "Silver",
            Heroes = new List<string> { "Common" },
            SpawningEligibility = "Always",
            IsSingleSpawn = true,
            IsCustomModCard = true,
            CustomModOrigin = "wire-mod",
            ModMinDay = 2,
            ModMaxDay = 4,
            ModSpawnWeight = 100000.0,
            AllowedHours = new List<int> { 2, 4 },
            SelectionContext = baseEvent.SelectionContext,
        };
        var eventBinding = new CustomCardVisualBinding
        {
            ModId = "wire-mod",
            CustomTemplateId = customEventId,
            BaseVisualTemplateId = EventEncCarrier,
            IsPatchOverride = false,
        };

        // 4. 带 ModDifficultySlot="Gold" 的自定义野怪遭遇（复用有效野怪 CombatantType 以满足可玩战斗校验）
        const string customMonsterEncId = "wire-mod:gold_slot_wyrm";
        var baseCombatEnc = shared.Repo.GetCard(CombatEncCarrier)!;
        var customMonsterCore = new BazaarCore.Models.CardTemplate
        {
            Id = customMonsterEncId,
            InternalName = "Gold Slot Wyrm",
            Type = "CombatEncounter",
            Size = "Medium",
            StartingTier = "Gold",
            Heroes = new List<string> { "Common" },
            SpawningEligibility = "Always",
            IsCustomModCard = true,
            CustomModOrigin = "wire-mod",
            ModMinDay = 3,
            ModMaxDay = 5,
            ModSpawnWeight = 50.0,
            ModDifficultySlot = "Gold",
            CombatantType = baseCombatEnc.CombatantType,
        };
        var monsterBinding = new CustomCardVisualBinding
        {
            ModId = "wire-mod",
            CustomTemplateId = customMonsterEncId,
            BaseVisualTemplateId = CombatEncCarrier,
            IsPatchOverride = false,
        };

        var overlayRepo = shared.Repo.CreateWithOverlay(new[]
        {
            patchedCore,
            customSkillCore,
            customEventCore,
            customMonsterCore,
        });
        var bindings = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase)
        {
            [baseCutlass.Id] = patchBinding,
            [customSkillId] = skillBinding,
            [customEventId] = eventBinding,
            [customMonsterEncId] = monsterBinding,
        };
        var overlayPool = shared.Pool.CreateIsolatedOverlay(overlayRepo, bindings);

        // 断言 1：原位 Patch 卡不被误判为自定义借壳卡，Wire GUID 仍为原版 GUID
        var mappedPatch = overlayPool.Get(baseCutlass.Id)!;
        Assert.True(mappedPatch.IsPatchOverride);
        Assert.False(mappedPatch.IsCustomModCard);
        Assert.Equal(baseCutlass.Id, overlayPool.ResolveSafeWireTemplateId(baseCutlass.Id), ignoreCase: true);

        // 断言 2：通用自定义技能对多英雄放行
        var mappedSkill = overlayPool.Get(customSkillId)!;
        Assert.True(mappedSkill.IsCustomModCard);
        Assert.True(mappedSkill.IsUniversalCustomSkill);
        Assert.Contains(overlayPool.Skills("Vanessa"), s => s.Id == customSkillId);
        Assert.Contains(overlayPool.Skills("Dooley"), s => s.Id == customSkillId);

        // 断言 3：AllowedHours 与 ModDifficultySlot 准确透传
        var mappedEvent = overlayPool.Get(customEventId)!;
        Assert.NotNull(mappedEvent.ModAllowedHours);
        Assert.Equal(new[] { 2, 4 }, mappedEvent.ModAllowedHours);
        Assert.True(mappedEvent.IsSingleSpawn);

        var mappedMonster = overlayPool.Get(customMonsterEncId)!;
        Assert.Equal("Gold", mappedMonster.ModDifficultySlot);
        var goldSlotCustoms = OfficialDaySchedule.GetEligibleCustomMonstersForSlot(day: 3, slotIndex: 2, overlayPool, overlayRepo);
        Assert.Contains(goldSlotCustoms, m => m.Id == customMonsterEncId);
        var bronzeSlotCustoms = OfficialDaySchedule.GetEligibleCustomMonstersForSlot(day: 3, slotIndex: 0, overlayPool, overlayRepo);
        Assert.DoesNotContain(bronzeSlotCustoms, m => m.Id == customMonsterEncId);

        // 断言 4：ShopEngine 真实小时调度遵循 AllowedHours=[2, 4]（Hour 1 不刷，Hour 2 必刷）
        var run = new RunState
        {
            RunId = "w2wire_sched",
            GameModeId = overlayPool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 2,
            Hour = 1,
            State = ERunState.Choice,
            RngSeed = 7771,
        };
        var shop = new ShopEngine(run, overlayPool, overlayRepo, run.CustomRules);
        shop.DealHourEncounters(null, run.VisitedDayEncounterIds);
        Assert.DoesNotContain(
            run.SelectionSet.Select(id => RunState.ResolveCoreTemplateId(run.Cards[id])),
            tid => string.Equals(tid, customEventId, StringComparison.OrdinalIgnoreCase));

        run.Hour = 2;
        shop.DealHourEncounters(null, run.VisitedDayEncounterIds);
        Assert.Contains(
            run.SelectionSet.Select(id => RunState.ResolveCoreTemplateId(run.Cards[id])),
            tid => string.Equals(tid, customEventId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 胶水 3 端到端闭环：
    /// RunEngine.ApplyPostCombatMechanicOps 统一委托 MechanicSafeAttributeWriter：
    /// 1) 主动卡 CooldownMax 极端缩小（Multiply 0.0001）被钳至 >= 1000ms；
    /// 2) 0 CD 被动卡 CooldownMax 不被强拉为 1000ms 主动卡；
    /// 3) 非输出卡 DamageAmount（GrantIfMissing=false）被固有输出守卫拦截；
    /// 4) PercentCooldownReduction 钳至 <= 90，Multicast 钳至 <= 10，且 PlayerState 经 ClampPlayerInvariants 清洗 NaN。
    /// </summary>
    [Fact]
    public void Wire3_RunEngine_PostCombatMechanicOps_DelegatesToMechanicSafeAttributeWriter()
    {
        var shared = SharedGameDataFixture.Shared;
        var activeWeaponTpl = shared.Repo.CardsById.Values.First(c =>
            c.Type == "Item" &&
            c.Tiers != null &&
            c.Tiers.TryGetValue("Bronze", out var td) &&
            td?.Attributes != null &&
            td.Attributes.TryGetValue("CooldownMax", out double cd) && cd >= 2000 &&
            td.Attributes.TryGetValue("DamageAmount", out double dmg) && dmg > 0);
        var passiveNonOutputTpl = new BazaarCore.Models.CardTemplate
        {
            Id = "wire-mod:pure_passive_charm",
            InternalName = "Pure Passive Charm",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Heroes = new List<string> { "Vanessa" },
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["CooldownMax"] = 0,
                        ["BuyPrice"] = 2,
                    },
                },
            },
        };

        var overlayRepo = shared.Repo.CreateWithOverlay(new[] { passiveNonOutputTpl });
        var run = new RunState
        {
            RunId = "w2wire_mech_ops",
            GameModeId = shared.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 3,
            Hour = 6,
            State = ERunState.Choice,
            RngSeed = 7773,
        };
        var engine = new RunEngine(run, shared.Pool, shared.CreateMonstersCopy(), shared.LevelUps, shared.Mode, overlayRepo, new PvpGhostPool());

        var activeCore = new CardInstance(activeWeaponTpl, CardTier.Bronze, null, "itm_active_w3");
        var passiveCore = new CardInstance(passiveNonOutputTpl, CardTier.Bronze, null, "itm_passive_w3");
        run.HandGrid.TryPlaceOrShift(activeCore, 3);
        run.HandGrid.TryPlaceOrShift(passiveCore, 5);
        run.Cards[activeCore.InstanceId] = new RunState.CardEntry
        {
            InstanceId = activeCore.InstanceId,
            TemplateId = Guid.Parse(activeWeaponTpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(activeWeaponTpl.Size),
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = activeCore,
        };
        run.Cards[passiveCore.InstanceId] = new RunState.CardEntry
        {
            InstanceId = passiveCore.InstanceId,
            TemplateId = Guid.Parse(SmallItemCarrier),
            CustomCardId = passiveNonOutputTpl.Id,
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_5,
            CoreCard = passiveCore,
        };

        var p1Core = new PlayerState("player", "Tester", "Vanessa");
        p1Core.Hand.TryPlaceOrShift(activeCore, 3);
        p1Core.Hand.TryPlaceOrShift(passiveCore, 5);
        // 刻意注入 NaN 验证 ClampPlayerInvariants 被战后统一调用
        p1Core.HourAdd["HealthMax"] = double.NaN;

        var ops = new List<PostCombatMutationOp>
        {
            new() { OpKind = "ModifyCardPermanentAttribute", CardInstanceId = activeCore.InstanceId, Attribute = "CooldownMax", Operation = "Multiply", Value = 0.0001 },
            new() { OpKind = "ModifyCardPermanentAttribute", CardInstanceId = activeCore.InstanceId, Attribute = "PercentCooldownReduction", Operation = "Add", Value = 500 },
            new() { OpKind = "ModifyCardPermanentAttribute", CardInstanceId = activeCore.InstanceId, Attribute = "Multicast", Operation = "Add", Value = 50 },
            new() { OpKind = "ModifyCardPermanentAttribute", CardInstanceId = passiveCore.InstanceId, Attribute = "CooldownMax", Operation = "Add", Value = -500 },
            new() { OpKind = "ModifyCardPermanentAttribute", CardInstanceId = passiveCore.InstanceId, Attribute = "DamageAmount", Operation = "Add", Value = 999, GrantIfMissing = false },
        };

        var applyOps = typeof(RunEngine).GetMethod("ApplyPostCombatMechanicOps", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var evs = new List<IGameSimEvent>();
        applyOps.Invoke(engine, new object?[] { p1Core, ops, evs, null });

        Assert.InRange(activeCore.GetAttribute("CooldownMax"), MechanicSafeAttributeWriter.MinCooldownMs, MechanicSafeAttributeWriter.MaxCooldownMs);
        Assert.Equal(MechanicSafeAttributeWriter.MaxPercentCooldownReduction, activeCore.GetAttribute("PercentCooldownReduction"), precision: 3);
        Assert.Equal(MechanicSafeAttributeWriter.MaxMulticast, activeCore.GetAttribute("Multicast"), precision: 3);

        Assert.Equal(0.0, passiveCore.GetAttribute("CooldownMax"), precision: 3);
        Assert.Equal(0.0, passiveCore.GetAttribute("DamageAmount"), precision: 3);
        Assert.True(double.IsFinite(p1Core.HourAdd.GetValueOrDefault("HealthMax", 0.0)), "ClampPlayerInvariants 必须将非法 NaN 修饰层清洗为有限安全值");
        Assert.Equal(0.0, p1Core.HourAdd.GetValueOrDefault("HealthMax", 0.0), precision: 6);
    }

    private sealed class ProbeGameModeHook : IGameModeHook
    {
        public string ModeId => "wire_probe_mode";
        public int SetupCount { get; private set; }
        public int DayStartCount { get; private set; }
        public int ShopGeneratedCount { get; private set; }
        public int CombatModCount { get; private set; }
        public int CombatStartingCount { get; private set; }

        public void OnRunSetup(GameModeRunSetupAdjustment adjustment, CustomRunRuleSet? rules)
        {
            SetupCount++;
            adjustment.StartingHealthDelta += 150;
            adjustment.StartingGoldDelta += 10;
            adjustment.FreeFirstShopReroll = true;
            adjustment.ForcedMutatorIds.Add("mod_golden_frenzy");
        }

        public void OnDayStart(int day, GameModeRunSetupAdjustment dayAdjustment)
        {
            DayStartCount++;
            dayAdjustment.StartingGoldDelta += 7;
        }

        public void OnShopGenerated(int day, int hour, IList<string> offeredCardIds)
        {
            ShopGeneratedCount++;
        }

        public void OnCombatModifiersBuilt(CombatModifiers modifiers, int day, bool isPvp)
        {
            CombatModCount++;
        }

        public void OnCombatStarting(PlayerState player, PlayerState opponent, ref bool sandstormEnabled)
        {
            CombatStartingCount++;
        }
    }

    /// <summary>
    /// 胶水 4 端到端闭环：
    /// 隔离单局 CustomModeHook + CustomMutator 从开局（StartRun）→ 商店（DealEncounterShelf）→ 跨天（AdvanceHourOrDay）→ 战斗（CombatModifiers + Hook）→ 计分全生命周期闭环验证。
    /// </summary>
    [Fact]
    public void Wire4_CustomModeHook_And_CustomMutator_FullLifecycle_SetupShopDayCombatScore()
    {
        var shared = SharedGameDataFixture.Shared;
        var probeHook = new ProbeGameModeHook();

        var customMutator = new MutatorMetadata(
            Id: "mod_golden_frenzy",
            NameZh: "黄金狂热",
            DescZh: "初始+25金币/+4收入，首刷免费，商店5折，+2额外货架，周期灼烧3，得分+50%",
            ConflictGroup: null,
            ScoreMultiplierDelta: 0.50,
            IsPositive: false,
            TargetHook: "All",
            SourceModId: "wire-mode-mod",
            Rules: new Dictionary<string, object?>
            {
                ["startingGoldDelta"] = 25,
                ["startingIncomeDelta"] = 4,
                ["freeFirstShopReroll"] = true,
                ["shopPriceMultiplier"] = 0.5,
                ["extraShopSlots"] = 2,
                ["boostModCardWeight"] = 5.0,
                ["monsterHealthMultiplier"] = 1.5,
                ["combatPeriodicTriggerAfterMs"] = 1000,
                ["combatPeriodicIntervalMs"] = 1000,
                ["combatBothApplyBurn"] = 3,
            });

        var runtimeCtx = new ModRuntimeContext(
            ActionHandlers: FrozenDictionary<string, ICustomActionHandler>.Empty,
            TriggerHandlers: FrozenDictionary<string, ICustomTriggerHandler>.Empty,
            ModeHooks: new Dictionary<string, IGameModeHook>(StringComparer.OrdinalIgnoreCase)
            {
                [probeHook.ModeId] = probeHook,
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            PipelineInterceptors: Array.Empty<ICombatPipelineInterceptor>(),
            MechanicsById: FrozenDictionary<string, MechanicDefinition>.Empty,
            CustomMutatorsById: new Dictionary<string, MutatorMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [customMutator.Id] = customMutator,
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            CustomModesById: FrozenDictionary<string, CustomModeDefinition>.Empty);

        var runModCtx = new ServerModHost.RunModContext(
            ContextKey: "wire4_probe",
            Repository: shared.Repo,
            Pool: shared.Pool,
            Monsters: shared.CreateMonstersCopy(),
            ArchetypeIndex: shared.ArchetypeIndex,
            VisualBindingsByCustomId: FrozenDictionary<string, CustomCardVisualBinding>.Empty,
            ActiveModIds: new[] { "wire-mode-mod" },
            IsolatedRegistry: null,
            RuntimeContext: runtimeCtx,
            EffectiveModSetHash: "abcdef123456");

        var run = new RunState
        {
            RunId = "w2wire_mode_lifecycle",
            GameModeId = shared.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            RngSeed = 7774,
            CustomRules = new CustomRunRuleSet
            {
                CustomModeId = probeHook.ModeId,
                SelectedModIds = new List<string> { "wire-mode-mod" },
            },
        };
        var engine = new RunEngine(
            run, shared.Pool, shared.CreateMonstersCopy(), shared.LevelUps, shared.Mode, shared.Repo, new PvpGhostPool(),
            archetypeIndex: shared.ArchetypeIndex,
            modRuntimeContext: runtimeCtx,
            runModContext: runModCtx);

        // 1. 开局（StartRun）：Hook OnRunSetup (+150 HP, +10 Gold, ForcedMutator) + CustomMutator (+25 Gold, +4 Income) 全生效
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);
        Assert.Equal(1, probeHook.SetupCount);
        Assert.Contains("mod_golden_frenzy", run.CustomRules.MutatorIds, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(GameDefaults.StartingHealth + 150, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(GameDefaults.StartingHealth + 150, run.PlayerAttributes[EPlayerAttributeType.Health]);
        Assert.Equal(GameDefaults.StartingGold + 10 + 25, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(GameDefaults.StartingIncome + 4, run.PlayerAttributes[EPlayerAttributeType.Income]);

        // 2. 商店生成（DealEncounterShelf）：验证首刷免费、半价物价、OnShopGenerated 回调
        var merchantTpl = shared.Pool.HourlyEncounters("Vanessa", "EventEncounter")
            .First(e => CardArchetypeIndex.IsMerchantEncounterTemplate(e.Id, e.InternalName, e, shared.Repo.GetCard(e.Id)));
        var shopField = typeof(RunEngine).GetField("_shop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var shop = (ShopEngine)shopField.GetValue(engine)!;
        shop.DealEncounterShelf(merchantTpl.Id, isReroll: false);
        Assert.Equal(0u, run.RerollCost);
        Assert.True(probeHook.ShopGeneratedCount >= 1);
        Assert.True(run.SelectionSet.Count >= 1);
        var firstShelf = run.Cards[run.SelectionSet.First()];
        if (firstShelf.CoreCard != null)
        {
            Assert.True(firstShelf.CoreCard.PermanentMult.GetValueOrDefault("BuyPrice", 1.0) <= 0.55);
        }

        // 3. 跨新一天（AdvanceHourOrDay -> Day 2）：OnDayStart 触发并赐予额外 +7 金币
        run.Day = 1;
        run.Hour = (uint)shared.Mode.HoursInADay;
        int goldBeforeNewDay = run.PlayerAttributes[EPlayerAttributeType.Gold];
        int incomeBeforeNewDay = run.PlayerAttributes[EPlayerAttributeType.Income];
        var advMethod = typeof(RunEngine).GetMethod("AdvanceHourOrDay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        advMethod.Invoke(engine, new object[] { new List<IGameSimEvent>(), true });
        Assert.Equal(2u, run.Day);
        Assert.Equal(1, probeHook.DayStartCount);
        Assert.Equal(goldBeforeNewDay + incomeBeforeNewDay + 7, run.PlayerAttributes[EPlayerAttributeType.Gold]);

        // 4. 战斗阶段：CombatModifiers.FromRulesAndDay 消费隔离自定义词条（1.5x 怪血 + 周期灼烧3）+ 触发 OnCombatModifiersBuilt / OnCombatStarting
        var mods = CombatModifiers.FromRulesAndDay(
            run.CustomRules,
            day: 2,
            isBossRush: false,
            isPve: true,
            bossRushWave: 0,
            isSymmetricHumanPvp: false,
            endlessGhostSourceDay: null,
            customMutators: runModCtx.CustomMutatorsById);
        Assert.Equal(1.5, mods.HealthMultiplierP2, precision: 3);
        Assert.Contains(mods.PeriodicEffects, pe => pe.SourceMutatorId == "mod_golden_frenzy" && pe.BothApplyBurn == 3);

        // 实跑一场 PvE 遭遇战斗验证 RunEngine.RunRealCombatSequence 真实触发 OnCombatModifiersBuilt & OnCombatStarting
        var monsterEnc = OfficialDaySchedule.SelectDayMonsters(day: 2, count: 1, shared.Pool, shared.Repo, new Random(42)).First();
        var combatMethod = typeof(RunEngine).GetMethod("RunRealCombatSequence", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var combatOut = new RunEngine.Outcome();
        combatMethod.Invoke(engine, new object?[] { combatOut, monsterEnc, false });
        Assert.True(probeHook.CombatModCount >= 1);
        Assert.True(probeHook.CombatStartingCount >= 1);

        // 5. 计分倍率：隔离自定义词条的 +0.50 准确计入
        double scoreMult = MutatorCatalog.CalculateTotalScoreMultiplier(
            run.CustomRules.PresetId,
            run.CustomRules.MutatorIds,
            extraMutators: runModCtx.CustomMutatorsById);
        Assert.Equal(1.50, scoreMult, precision: 3);
    }

    /// <summary>
    /// 胶水 5 端到端闭环：
    /// 验证 PvpGhostPool.ResolveGhostPoolPartitionLocal 与 GameServerEndpoints.ComputeEffectiveModSetHashLocal
    /// 100% 委托 ServerModHost 单一权威函数，且标准规则 + 玩法 Mod 返回 "mod:<hash12>"（无多余 +custom）、
    /// 标准规则 + 仅 cosmetic-only Mod 保持 "standard"（不被误踢出标准池）。
    /// </summary>
    [Fact]
    public void Wire5_GhostPartition_And_ModSetHash_SingleSourceOfTruth_Delegation()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempRoot = CreateTempDir("w2wire_p5");
        try
        {
            string modsDir = Path.Combine(tempRoot, "mods");
            string stateDir = Path.Combine(tempRoot, "state");
            Directory.CreateDirectory(stateDir);

            void WriteMod(string id, string scope)
            {
                string dir = Path.Combine(modsDir, id);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "mod.json"), $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","scope":"{{scope}}"}""");
            }

            WriteMod("wire-skin-only", "cosmetic-only");
            WriteMod("wire-gameplay-a", "multiplayer-safe");

            var host = new ServerModHost(shared.Repo, shared.DataDir, stateDir, shared.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            host.ToggleMod("wire-skin-only", false);
            host.ToggleMod("wire-gameplay-a", false);

            // 1. 纯标准无 Mod -> "standard"
            var stdRules = new CustomRunRuleSet();
            Assert.Equal("standard", PvpGhostPool.ResolveGhostPoolPartitionLocal(stdRules, false, host));
            Assert.Equal(
                ServerModHost.ResolveEffectiveGhostPoolPartition(stdRules, stdRules.SelectedModIds, false, host),
                PvpGhostPool.ResolveGhostPoolPartitionLocal(stdRules, false, host));

            // 2. 标准规则 + 仅 cosmetic-only 皮肤包 -> 保持 "standard" & hash == ""
            var skinOnlyRules = new CustomRunRuleSet { SelectedModIds = new List<string> { "wire-skin-only" } };
            Assert.Equal(string.Empty, GameServerEndpoints.ComputeEffectiveModSetHashLocal(skinOnlyRules.SelectedModIds, host));
            Assert.Equal("standard", PvpGhostPool.ResolveGhostPoolPartitionLocal(skinOnlyRules, false, host));
            Assert.Equal("standard", ServerModHost.ResolveEffectiveGhostPoolPartition(skinOnlyRules, skinOnlyRules.SelectedModIds, false, host));

            // 3. 标准规则 + 玩法 Mod -> 精确 "mod:<hash12>"（无 "+custom"），双入口逐字节相等
            var gameplayModRules = new CustomRunRuleSet { SelectedModIds = new List<string> { "wire-gameplay-a" } };
            string expectedHash = ServerModHost.ComputeEffectiveModSetHash(gameplayModRules.SelectedModIds, host);
            Assert.Equal(12, expectedHash.Length);
            Assert.Equal(expectedHash, GameServerEndpoints.ComputeEffectiveModSetHashLocal(gameplayModRules.SelectedModIds, host));
            Assert.Equal(expectedHash, PvpGhostPool.ComputeEffectiveModHashLocal(gameplayModRules.SelectedModIds, host));
            Assert.Equal($"mod:{expectedHash}", PvpGhostPool.ResolveGhostPoolPartitionLocal(gameplayModRules, false, host));
            Assert.Equal($"mod:{expectedHash}", ServerModHost.ResolveEffectiveGhostPoolPartition(gameplayModRules, gameplayModRules.SelectedModIds, false, host));

            // 4. 无尽高塔 + 玩法 Mod -> "mod:<hash12>+endless_tower"
            var endlessModRules = new CustomRunRuleSet
            {
                SpecialModeId = ESpecialModeId.EndlessTower,
                SelectedModIds = new List<string> { "wire-gameplay-a" },
            };
            Assert.Equal($"mod:{expectedHash}+endless_tower", PvpGhostPool.ResolveGhostPoolPartitionLocal(endlessModRules, false, host));

            // 5. 游离未登记自定义卡 / 沙盒模式 -> "none"
            Assert.Equal("none", PvpGhostPool.ResolveGhostPoolPartitionLocal(stdRules, hasTrueCustomCardInRun: true, host));
            var sandboxRules = new CustomRunRuleSet { SpecialModeId = ESpecialModeId.SandboxLab, SelectedModIds = new List<string> { "wire-gameplay-a" } };
            Assert.Equal("none", PvpGhostPool.ResolveGhostPoolPartitionLocal(sandboxRules, false, host));
        }
        finally
        {
            SafeDeleteDir(tempRoot);
        }
    }

    /// <summary>
    /// 胶水 6 + 胶水 7 + D6-P1-18 端到端黄金闭环：
    /// 非 GUID 战中变形（CombatSimConverter.ToCombatSim + ResolveCombatWireTemplateGuid + OnCombatRuntimeCardTransformed）
    /// → 自动登记 RunModContext 临时视觉绑定并自增 VisualBindingsRevision
    /// → SaveCombatReplayIfEligible 自动序列化入 SQLite ReplayRow.VisualBindingsJson
    /// → SpectateEndpoints.BuildReplayDetailDto（GET /api/replays/{id}）完整返回 visualBindings！
    /// 并同步校验 PlayerState 默认常量与 GameDefaults 统一。
    /// </summary>
    [Fact]
    public void Wire6_7_NonGuidCombatTransform_ToTransientBinding_ReplayPersist_And_SpectateReplayDetail_PlusGameDefaults()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempDir = CreateTempDir("w2wire_p67");
        string dbPath = Path.Combine(tempDir, "wire67.db");
        try
        {
            // D6-P1-18：PlayerState 默认常量统一引用 GameDefaults
            var defaultPs = new PlayerState("p_def", "DefaultPlayer", "Vanessa");
            Assert.Equal(GameDefaults.StartingHealth, defaultPs.HealthMax);
            Assert.Equal(GameDefaults.StartingPrestige, (int)defaultPs.Attributes["Prestige"]);

            // 1. 构造非 GUID 战中变形目标自定义卡与视觉绑定
            const string transformedCustomId = "wire-mod:transformed_astral_dragon";
            var transformedTpl = new BazaarCore.Models.CardTemplate
            {
                Id = transformedCustomId,
                InternalName = "Astral Dragon",
                Type = "Item",
                Size = "Small",
                StartingTier = "Gold",
                Heroes = new List<string> { "Vanessa" },
                IsCustomModCard = true,
                CustomModOrigin = "wire-mod",
                Tiers = new Dictionary<string, TierData>
                {
                    ["Gold"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 4000, ["DamageAmount"] = 88 } },
                },
            };
            var visualBinding = new CustomCardVisualBinding
            {
                ModId = "wire-mod",
                CustomTemplateId = transformedCustomId,
                BaseVisualTemplateId = SmallItemCarrier,
                TitleZh = "星界幼龙",
                TitleEn = "Astral Dragon",
                CustomArtFile = "assets/astral_dragon.png",
                IsPatchOverride = false,
            };

            var overlayRepo = shared.Repo.CreateWithOverlay(new[] { transformedTpl });
            var bindingsDict = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase)
            {
                [transformedCustomId] = visualBinding,
            };
            var overlayPool = shared.Pool.CreateIsolatedOverlay(overlayRepo, bindingsDict);

            var runModCtx = new ServerModHost.RunModContext(
                ContextKey: "wire67_ctx",
                Repository: overlayRepo,
                Pool: overlayPool,
                Monsters: shared.CreateMonstersCopy(),
                ArchetypeIndex: shared.ArchetypeIndex,
                VisualBindingsByCustomId: bindingsDict,
                ActiveModIds: new[] { "wire-mod" },
                IsolatedRegistry: null,
                RuntimeContext: ModRuntimeContext.Empty,
                EffectiveModSetHash: "112233445566");

            using var db = new Persistence(dbPath);
            var social = new SocialAndReplayService(db, new SseEventHub());
            string p1AccountId = Guid.NewGuid().ToString("N");
            var run = new RunState
            {
                RunId = "run_wire67_01",
                GameModeId = overlayPool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Ranked,
                AccountId = p1AccountId,
                Username = "WireHero",
                Day = 4,
                Hour = 6,
                State = ERunState.Choice,
                RngSeed = 7776,
            };

            var srcTpl = overlayRepo.GetCard(SmallItemCarrier)!;
            var srcCore = new CardInstance(srcTpl, CardTier.Bronze, null, "p1_src_0");
            run.HandGrid.TryPlaceOrShift(srcCore, 3);
            var srcEntry = new RunState.CardEntry
            {
                InstanceId = srcCore.InstanceId,
                TemplateId = Guid.Parse(SmallItemCarrier),
                Type = ECardType.Item,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_3,
                CoreCard = srcCore,
            };
            run.Cards[srcEntry.InstanceId] = srcEntry;

            var engine = new RunEngine(
                run, overlayPool, shared.CreateMonstersCopy(), shared.LevelUps, shared.Mode, overlayRepo, new PvpGhostPool(),
                socialSink: social,
                db: db,
                runModContext: runModCtx);

            // 2. 通过 RunEngine 胶水 6 回调调用 CombatSimConverter.ToCombatSim 处理非 GUID 战中变形事件
            var resolveWireMethod = typeof(RunEngine).GetMethod("ResolveCombatWireTemplateGuid", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var onTransformedMethod = typeof(RunEngine).GetMethod("OnCombatRuntimeCardTransformed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Func<string, Guid?> resolveWire = id => (Guid?)resolveWireMethod.Invoke(engine, new object[] { id });
            Action<string, string, Guid> onTransformed = (instId, tplId, wireGuid) =>
                onTransformedMethod.Invoke(engine, new object[] { instId, tplId, wireGuid });

            var combatResult = new CombatResult
            {
                WinnerPlayerId = "player",
                DurationMs = 2000,
                Player1RemainingHp = 280,
                Player2RemainingHp = 0,
                Timeline = new List<CombatTickState>
                {
                    new() { TimeMs = 0, P1Hp = 300, P1MaxHp = 300, P2Hp = 200, P2MaxHp = 200 },
                    new() { TimeMs = 1000, P1Hp = 280, P1MaxHp = 300, P2Hp = 0, P2MaxHp = 200 },
                },
                Events = new List<CombatEvent>
                {
                    new()
                    {
                        TimeMs = 500,
                        Type = CombatEventType.CardTransformed,
                        SourceCardInstanceId = srcCore.InstanceId,
                        TargetCardInstanceId = "p1_tr_dragon_1",
                        Detail = $"{srcCore.InstanceId}->{transformedCustomId}",
                    },
                },
            };

            int revBeforeRun = run.VisualBindingsRevision;
            int revBeforeCtx = runModCtx.VisualBindingsRevision;

            var sim = CombatSimConverter.ToCombatSim(
                combatResult,
                overlayRepo,
                new Dictionary<string, CardInstance> { [srcCore.InstanceId] = srcCore },
                new Dictionary<string, CardInstance>(),
                new List<RunState.CardEntry> { srcEntry },
                new List<RunState.CardEntry>(),
                resolveWireTemplateGuid: resolveWire,
                onRuntimeCardTransformed: onTransformed);

            // 断言胶水 6：发射了安全借壳 Wire GUID 的 CombatSimEventCardTransformed + 临时视觉绑定已登记 + 双版本号自增
            var trEvents = sim.Frames.SelectMany(f => f.Events).OfType<CombatSimEventCardTransformed>().ToList();
            Assert.NotEmpty(trEvents);
            Assert.Contains(trEvents.SelectMany(e => e.TransformedCards), tc =>
                string.Equals(tc.TemplateId, SmallItemCarrier, StringComparison.OrdinalIgnoreCase));
            Assert.True(run.VisualBindingsRevision > revBeforeRun);
            Assert.True(runModCtx.VisualBindingsRevision > revBeforeCtx);
            Assert.True(runModCtx.GetMergedVisualCatalog().ContainsKey(transformedCustomId));

            // 3. 经 SaveCombatReplayIfEligible 自动将临时变形视觉绑定落盘至 SQLite ReplayRow.VisualBindingsJson
            var saveReplayMethod = typeof(RunEngine).GetMethod("SaveCombatReplayIfEligible", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var p1Core = new PlayerState("player", "WireHero", "Vanessa");
            var p2Core = new PlayerState("opponent", "DummyBoss", "Dooley");
            saveReplayMethod.Invoke(engine, new object?[]
            {
                false, true, 9001, combatResult, p1Core, p2Core, null, null, sim, false, null, Array.Empty<RunState.CardEntry>()
            });

            var savedList = db.QueryReplaysByAccount(p1AccountId);
            Assert.Single(savedList);
            var loadedRow = db.LoadReplay(savedList[0].ReplayId);
            Assert.NotNull(loadedRow);
            Assert.Contains(transformedCustomId, loadedRow!.VisualBindingsJson, StringComparison.OrdinalIgnoreCase);

            // 4. 断言胶水 7：SpectateEndpoints.BuildReplayDetailDto（GET /api/replays/{id}）完整输出 visualBindings
            var detailDto = SpectateEndpoints.BuildReplayDetailDto(loadedRow);
            string detailJson = JsonSerializer.Serialize(detailDto);
            using var doc = JsonDocument.Parse(detailJson);
            var vbProp = doc.RootElement.GetProperty("visualBindings");
            Assert.Equal(JsonValueKind.Object, vbProp.ValueKind);
            Assert.True(vbProp.TryGetProperty(transformedCustomId, out var dragonBinding));
            Assert.Equal("assets/astral_dragon.png", dragonBinding.GetProperty("customArtFile").GetString());
            Assert.Equal("星界幼龙", dragonBinding.GetProperty("titleZh").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            SafeDeleteDir(tempDir);
        }
    }

    private sealed class ForcedMutatorModeHook : IGameModeHook
    {
        public string ModeId => "wire_forced_mutators_mode";
        public string DisplayNameZh => "强制词条探针模式";

        public void OnRunSetup(GameModeRunSetupAdjustment adjustment, CustomRunRuleSet? activeRules)
        {
            adjustment.ForcedMutatorIds.Add(MutatorCatalog.DoubleEconomy);
            adjustment.ForcedMutatorIds.Add(MutatorCatalog.IronmanNoFates);
            adjustment.ForcedMutatorIds.Add(MutatorCatalog.GoldenStart);
        }
    }

    private sealed class ShopInjectHook : IGameModeHook
    {
        private readonly string[] _idsToAppend;
        public ShopInjectHook(params string[] idsToAppend) => _idsToAppend = idsToAppend;
        public string ModeId => "wire_shop_inject_mode";
        public string DisplayNameZh => "商店追加类型探针模式";

        public void OnShopGenerated(int day, int hour, IList<string> offeredCardIds)
        {
            foreach (var id in _idsToAppend)
                offeredCardIds.Add(id);
        }
    }

    /// <summary>
    /// 复审回归 1（P0-22 × P0-30）：
    /// 1) 玩家直接勾选隔离 Mod 自定义突变词条（无 CustomModeId）→ StartRun() → TryLockOpeningTendency()
    ///    → RunSnapshotSerializer.Serialize / TryReadHeader / TryDeserialize（无参 & 带上下文）全程不丢词条，
    ///    且 PublicSummary.RulesSnapshot.MutatorIds 完整保活；
    /// 2) 玩法 Mod + 隔离自定义突变词条 调用 ResolveEffectiveGhostPoolPartition / ResolveGhostPoolPartitionLocal
    ///    精确返回 "mod:<hash12>+custom"（绝不因无参 CloneNormalized() 洗空词条而混入 "mod:<hash12>"）。
    /// </summary>
    [Fact]
    public void Wire4_5_DirectIsolatedCustomMutator_SurvivesStartTendencySnapshot_And_SplitsGhostPartition()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempRoot = CreateTempDir("w2wire_mut_keep");
        try
        {
            const string modId = "wire-isolated-mut-mod";
            const string mutId = "mod_isolated_bounty";

            var isolatedMut = new MutatorMetadata(
                Id: mutId,
                NameZh: "隔离赏金",
                DescZh: "初始+15金币/+3收入",
                ConflictGroup: null,
                ScoreMultiplierDelta: 0.25,
                IsPositive: true,
                TargetHook: "RunSetup",
                SourceModId: modId,
                Rules: new Dictionary<string, object?>
                {
                    ["startingGoldDelta"] = 15,
                    ["startingIncomeDelta"] = 3,
                });

            var extraMutators = new Dictionary<string, MutatorMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [mutId] = isolatedMut,
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            var runtimeCtx = new ModRuntimeContext(
                ActionHandlers: FrozenDictionary<string, ICustomActionHandler>.Empty,
                TriggerHandlers: FrozenDictionary<string, ICustomTriggerHandler>.Empty,
                ModeHooks: FrozenDictionary<string, IGameModeHook>.Empty,
                PipelineInterceptors: Array.Empty<ICombatPipelineInterceptor>(),
                MechanicsById: FrozenDictionary<string, MechanicDefinition>.Empty,
                CustomMutatorsById: extraMutators,
                CustomModesById: FrozenDictionary<string, CustomModeDefinition>.Empty);

            var runModCtx = new ServerModHost.RunModContext(
                ContextKey: "wire_iso_mut_ctx",
                Repository: shared.Repo,
                Pool: shared.Pool,
                Monsters: shared.CreateMonstersCopy(),
                ArchetypeIndex: shared.ArchetypeIndex,
                VisualBindingsByCustomId: FrozenDictionary<string, CustomCardVisualBinding>.Empty,
                ActiveModIds: new[] { modId },
                IsolatedRegistry: null,
                RuntimeContext: runtimeCtx,
                EffectiveModSetHash: "fedcba654321");

            // 1. 玩家直接勾选隔离词条（无 CustomModeId）开局 -> 锁定倾向 -> 序列化/反序列化全链路保活
            var run = new RunState
            {
                RunId = "run_wire_direct_mut_01",
                GameModeId = shared.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                RngSeed = 8811,
                CustomRules = new CustomRunRuleSet
                {
                    AllowTendencySelection = true,
                    SelectedModIds = new List<string> { modId },
                    MutatorIds = new List<string> { mutId },
                },
            };

            var engine = new RunEngine(
                run, shared.Pool, shared.CreateMonstersCopy(), shared.LevelUps, shared.Mode, shared.Repo, new PvpGhostPool(),
                archetypeIndex: shared.ArchetypeIndex,
                modRuntimeContext: runtimeCtx,
                runModContext: runModCtx);

            var startOut = engine.StartRun();
            Assert.Null(startOut.Error);
            Assert.Contains(mutId, run.CustomRules.MutatorIds, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(mutId, run.PublicSummary.RulesSnapshot.MutatorIds, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(GameDefaults.StartingGold + 15, run.PlayerAttributes[EPlayerAttributeType.Gold]);
            Assert.Equal(GameDefaults.StartingIncome + 3, run.PlayerAttributes[EPlayerAttributeType.Income]);

            // 开场锁定流派倾向后仍保活
            Assert.True(engine.TryLockOpeningTendency(1UL, out ulong lockedMask, out string? lockErr), lockErr);
            Assert.Equal(1UL, lockedMask);
            Assert.Contains(mutId, run.CustomRules.MutatorIds, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(mutId, run.PublicSummary.RulesSnapshot.MutatorIds, StringComparer.OrdinalIgnoreCase);

            // 快照序列化 / 只读头解析 / 无参反序列化 / 带上下文反序列化 全程不丢隔离词条
            string snapJson = RunSnapshotSerializer.Serialize(run, startSelectionDone: engine.StartSelectionDone);
            Assert.True(RunSnapshotSerializer.TryReadHeader(snapJson, null, out var header));
            Assert.NotNull(header?.CustomRules);
            Assert.Contains(mutId, header!.CustomRules!.MutatorIds, StringComparer.OrdinalIgnoreCase);

            Assert.True(RunSnapshotSerializer.TryDeserialize(snapJson, shared.Repo, out var restoredNoCtx, out _));
            Assert.NotNull(restoredNoCtx);
            Assert.Contains(mutId, restoredNoCtx!.CustomRules.MutatorIds, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(mutId, restoredNoCtx.PublicSummary.RulesSnapshot.MutatorIds, StringComparer.OrdinalIgnoreCase);

            Assert.True(RunSnapshotSerializer.TryDeserialize(snapJson, shared.Repo, out var restoredWithCtx, out _, extraMutators: extraMutators));
            Assert.NotNull(restoredWithCtx);
            Assert.Contains(mutId, restoredWithCtx!.CustomRules.MutatorIds, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(mutId, restoredWithCtx.PublicSummary.RulesSnapshot.MutatorIds, StringComparer.OrdinalIgnoreCase);

            // 2. 幽灵池分区：玩法 Mod + 隔离自定义词条 必须返回 "mod:<hash12>+custom"，与无词条 "mod:<hash12>" 严格分离
            string modsDir = Path.Combine(tempRoot, "mods");
            string stateDir = Path.Combine(tempRoot, "state");
            Directory.CreateDirectory(stateDir);
            string modDir = Path.Combine(modsDir, modId);
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(modDir, "mod.json"), $$"""{"id":"{{modId}}","name":"{{modId}}","version":"1.0.0","scope":"multiplayer-safe"}""");

            var host = new ServerModHost(shared.Repo, shared.DataDir, stateDir, shared.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            host.ToggleMod(modId, false);
            string expectedHash = ServerModHost.ComputeEffectiveModSetHash(new[] { modId }, host);
            Assert.Equal(12, expectedHash.Length);

            var plainModRules = new CustomRunRuleSet { SelectedModIds = new List<string> { modId } };
            var modPlusMutRules = new CustomRunRuleSet
            {
                SelectedModIds = new List<string> { modId },
                MutatorIds = new List<string> { mutId },
            };

            Assert.Equal($"mod:{expectedHash}", ServerModHost.ResolveEffectiveGhostPoolPartition(plainModRules, plainModRules.SelectedModIds, false, host));
            Assert.Equal($"mod:{expectedHash}+custom", ServerModHost.ResolveEffectiveGhostPoolPartition(modPlusMutRules, modPlusMutRules.SelectedModIds, false, host));
            Assert.Equal($"mod:{expectedHash}+custom", PvpGhostPool.ResolveGhostPoolPartitionLocal(modPlusMutRules, false, host));
        }
        finally
        {
            SafeDeleteDir(tempRoot);
        }
    }

    /// <summary>
    /// 复审回归 2（P0-22）：
    /// IGameModeHook.OnRunSetup 通过 ForcedMutatorIds 注入内置词条（DoubleEconomy / IronmanNoFates / GoldenStart）
    /// 必须在 ApplyStartingStats 判定之前合入，确保开局翻倍金币收入、截断声望 <= 10 + 禁用死神、解锁 8 格全部生效。
    /// </summary>
    [Fact]
    public void Wire4_CustomModeHook_ForcedMutators_ApplyBeforeStartingStatsRules()
    {
        var shared = SharedGameDataFixture.Shared;
        var forcedHook = new ForcedMutatorModeHook();

        var runtimeCtx = new ModRuntimeContext(
            ActionHandlers: FrozenDictionary<string, ICustomActionHandler>.Empty,
            TriggerHandlers: FrozenDictionary<string, ICustomTriggerHandler>.Empty,
            ModeHooks: new Dictionary<string, IGameModeHook>(StringComparer.OrdinalIgnoreCase)
            {
                [forcedHook.ModeId] = forcedHook,
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            PipelineInterceptors: Array.Empty<ICombatPipelineInterceptor>(),
            MechanicsById: FrozenDictionary<string, MechanicDefinition>.Empty,
            CustomMutatorsById: FrozenDictionary<string, MutatorMetadata>.Empty,
            CustomModesById: FrozenDictionary<string, CustomModeDefinition>.Empty);

        var runModCtx = new ServerModHost.RunModContext(
            ContextKey: "wire_forced_mut_ctx",
            Repository: shared.Repo,
            Pool: shared.Pool,
            Monsters: shared.CreateMonstersCopy(),
            ArchetypeIndex: shared.ArchetypeIndex,
            VisualBindingsByCustomId: FrozenDictionary<string, CustomCardVisualBinding>.Empty,
            ActiveModIds: new[] { "wire-forced-mod" },
            IsolatedRegistry: null,
            RuntimeContext: runtimeCtx,
            EffectiveModSetHash: "223344556677");

        var run = new RunState
        {
            RunId = "run_wire_forced_mut_01",
            GameModeId = shared.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            RngSeed = 8822,
            CustomRules = new CustomRunRuleSet
            {
                CustomModeId = forcedHook.ModeId,
                StartingPrestige = 20,
            },
        };

        var engine = new RunEngine(
            run, shared.Pool, shared.CreateMonstersCopy(), shared.LevelUps, shared.Mode, shared.Repo, new PvpGhostPool(),
            archetypeIndex: shared.ArchetypeIndex,
            modRuntimeContext: runtimeCtx,
            runModContext: runModCtx);

        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);

        Assert.Contains(MutatorCatalog.DoubleEconomy, run.CustomRules.MutatorIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(MutatorCatalog.IronmanNoFates, run.CustomRules.MutatorIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(MutatorCatalog.GoldenStart, run.CustomRules.MutatorIds, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(shared.Mode.StartingGold * 2, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(shared.Mode.StartingIncome * 2, run.PlayerAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(10, run.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.True(run.FatesUsed);
        Assert.Equal((ushort)0b01_1111_1110, run.UnlockedSlots);
        Assert.Equal((ushort)0b01_1111_1110, run.HandGrid.UnlockedMask);
    }

    /// <summary>
    /// 复审回归 3（P0-22）：
    /// ShopEngine.OnShopGenerated 追加商品必须校验 IsPlayerSpawnable 且按 Type 区分 Item / Skill：
    /// 合法 Skill 走 CreateSkillCandidate（ECardType.Skill），合法 Item 走 CreateItemCandidate（ECardType.Item），
    /// 非法/事件模板（EventEncounter）安全跳过绝不误造为 Item，总槽宽严守 <= MaxShelfSlotWidth(10)。
    /// </summary>
    [Fact]
    public void Wire4_ShopGeneratedHook_ValidatesSpawnableAndDispatchesItemSkill_SkipsEventEncounter()
    {
        var shared = SharedGameDataFixture.Shared;
        var validSkill = shared.Pool.Skills("Vanessa").First(t =>
            string.Equals(t.Type, "Skill", StringComparison.OrdinalIgnoreCase) &&
            shared.Pool.IsPlayerSpawnable(t, allowGuidOnly: false));
        var validSmallItem = shared.Pool.ShopItems("Vanessa").First(t =>
            string.Equals(t.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Size, "Small", StringComparison.OrdinalIgnoreCase) &&
            shared.Pool.IsPlayerSpawnable(t, allowGuidOnly: false) &&
            !string.Equals(t.Id, SmallItemCarrier, StringComparison.OrdinalIgnoreCase));
        var merchantTpl = shared.Pool.HourlyEncounters("Vanessa", "EventEncounter")
            .First(e => CardArchetypeIndex.IsMerchantEncounterTemplate(e.Id, e.InternalName, e, shared.Repo.GetCard(e.Id)));

        var injectHook = new ShopInjectHook(validSkill.Id, EventEncCarrier, validSmallItem.Id);
        var runtimeCtx = new ModRuntimeContext(
            ActionHandlers: FrozenDictionary<string, ICustomActionHandler>.Empty,
            TriggerHandlers: FrozenDictionary<string, ICustomTriggerHandler>.Empty,
            ModeHooks: new Dictionary<string, IGameModeHook>(StringComparer.OrdinalIgnoreCase)
            {
                [injectHook.ModeId] = injectHook,
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            PipelineInterceptors: Array.Empty<ICombatPipelineInterceptor>(),
            MechanicsById: FrozenDictionary<string, MechanicDefinition>.Empty,
            CustomMutatorsById: FrozenDictionary<string, MutatorMetadata>.Empty,
            CustomModesById: FrozenDictionary<string, CustomModeDefinition>.Empty);

        var runModCtx = new ServerModHost.RunModContext(
            ContextKey: "wire_shop_inject_ctx",
            Repository: shared.Repo,
            Pool: shared.Pool,
            Monsters: shared.CreateMonstersCopy(),
            ArchetypeIndex: shared.ArchetypeIndex,
            VisualBindingsByCustomId: FrozenDictionary<string, CustomCardVisualBinding>.Empty,
            ActiveModIds: new[] { "wire-shop-mod" },
            IsolatedRegistry: null,
            RuntimeContext: runtimeCtx,
            EffectiveModSetHash: "334455667788");

        var run = new RunState
        {
            RunId = "run_wire_shop_inject_01",
            GameModeId = shared.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 2,
            Hour = 2,
            RngSeed = 8833,
            CustomRules = new CustomRunRuleSet { CustomModeId = injectHook.ModeId },
        };

        var shop = new ShopEngine(run, shared.Pool, shared.Repo)
        {
            ActiveRules = run.CustomRules,
            ArchetypeIndex = shared.ArchetypeIndex,
            ModRuntimeContext = runtimeCtx,
            RunModContext = runModCtx,
        };

        // 限制初始发牌为 1 件以留足货架空位给 Hook 追加的 Skill(2 格) + Small Item(1 格)
        shop.DealEncounterShelf(
            merchantTpl.Id,
            isReroll: false,
            playerState: null,
            ownedMaxTiers: null,
            customRules: run.CustomRules,
            archetypeIndex: shared.ArchetypeIndex,
            applySyntheticDiscounts: false,
            spawnCountOverride: 1);

        var shelfEntries = run.SelectionSet.Select(id => run.Cards[id]).ToList();
        var addedSkill = shelfEntries.FirstOrDefault(e =>
            string.Equals(RunState.ResolveCoreTemplateId(e), validSkill.Id, StringComparison.OrdinalIgnoreCase));
        var addedItem = shelfEntries.FirstOrDefault(e =>
            string.Equals(RunState.ResolveCoreTemplateId(e), validSmallItem.Id, StringComparison.OrdinalIgnoreCase));
        var illegalEvent = shelfEntries.FirstOrDefault(e =>
            string.Equals(RunState.ResolveCoreTemplateId(e), EventEncCarrier, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(addedSkill);
        Assert.Equal(ECardType.Skill, addedSkill!.Type);
        Assert.NotNull(addedItem);
        Assert.Equal(ECardType.Item, addedItem!.Type);
        Assert.Null(illegalEvent);

        int totalSlotWidth = shelfEntries.Sum(e => e.Type == ECardType.Skill ? 2 : Math.Clamp((int)e.Size, 1, 3));
        Assert.True(totalSlotWidth <= ShopEngine.MaxShelfSlotWidth, $"货架总宽越界: {totalSlotWidth}");
    }

    /// <summary>
    /// 复审回归 4（P0-25）：
    /// 原位 Patch 原版卡（IsPatchOverride=true，原版 GUID）在战中变形回调 OnCombatRuntimeCardTransformed 中
    /// 绝不可被误判为借壳自定义卡，不得递增 VisualBindingsRevision，也不得污染 ActiveCombatOpponentCustomCardIds。
    /// </summary>
    [Fact]
    public void Wire6_InPlacePatchedVanillaCard_CombatTransform_DoesNotIncrementRevisionOrPolluteCustomIds()
    {
        var shared = SharedGameDataFixture.Shared;
        var baseTpl = shared.Repo.GetCard(SmallItemCarrier)!;
        var patchedTpl = new BazaarCore.Models.CardTemplate
        {
            Id = baseTpl.Id,
            InternalName = baseTpl.InternalName,
            Type = baseTpl.Type,
            Size = baseTpl.Size,
            StartingTier = baseTpl.StartingTier,
            Heroes = new List<string>(baseTpl.Heroes),
            Tags = new List<string>(baseTpl.Tags),
            HiddenTags = new List<string>(baseTpl.HiddenTags),
            IsCustomModCard = false,
            CustomModOrigin = "wire-patch-only-mod",
            Tiers = baseTpl.Tiers != null ? new Dictionary<string, TierData>(baseTpl.Tiers) : new Dictionary<string, TierData>(),
        };

        var patchBinding = new CustomCardVisualBinding
        {
            ModId = "wire-patch-only-mod",
            CustomTemplateId = SmallItemCarrier,
            BaseVisualTemplateId = SmallItemCarrier,
            TitleZh = "原位补丁原版卡",
            IsPatchOverride = true,
        };

        var overlayRepo = shared.Repo.CreateWithOverlay(new[] { patchedTpl });
        var bindingsDict = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase)
        {
            [SmallItemCarrier] = patchBinding,
        };
        var overlayPool = shared.Pool.CreateIsolatedOverlay(overlayRepo, bindingsDict);
        Assert.True(overlayPool.Get(SmallItemCarrier)?.IsPatchOverride == true);
        Assert.False(overlayPool.Get(SmallItemCarrier)?.IsCustomModCard == true);

        var runModCtx = new ServerModHost.RunModContext(
            ContextKey: "wire6_patch_only",
            Repository: overlayRepo,
            Pool: overlayPool,
            Monsters: shared.CreateMonstersCopy(),
            ArchetypeIndex: shared.ArchetypeIndex,
            VisualBindingsByCustomId: bindingsDict,
            ActiveModIds: new[] { "wire-patch-only-mod" },
            IsolatedRegistry: null,
            RuntimeContext: ModRuntimeContext.Empty,
            EffectiveModSetHash: "445566778899");

        var run = new RunState
        {
            RunId = "run_wire6_patch_01",
            GameModeId = overlayPool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 3,
            Hour = 6,
            State = ERunState.Choice,
            RngSeed = 8844,
        };

        var engine = new RunEngine(
            run, overlayPool, shared.CreateMonstersCopy(), shared.LevelUps, shared.Mode, overlayRepo, new PvpGhostPool(),
            runModContext: runModCtx);

        int runRevBefore = run.VisualBindingsRevision;
        int ctxRevBefore = runModCtx.VisualBindingsRevision;

        var onTransformedMethod = typeof(RunEngine).GetMethod("OnCombatRuntimeCardTransformed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        onTransformedMethod.Invoke(engine, new object[] { "opp_patch_card_1", SmallItemCarrier, Guid.Parse(SmallItemCarrier) });

        Assert.Equal(runRevBefore, run.VisualBindingsRevision);
        Assert.Equal(ctxRevBefore, runModCtx.VisualBindingsRevision);
        Assert.False(run.ActiveCombatOpponentCustomCardIds.ContainsKey("opp_patch_card_1"));
    }
}
