using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;
using BazaarServer.Game;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V12 独立红队对抗测试铁墙套件（严格基于官方 GameData.db 真值与玩家黑盒操作流）：
/// 1. 黑盒 RunEngine 端到端对抗：狼筅吸收回忆、全部 PlayerEffect 追踪器、盗贼行会徽章与零耗小时、探险门票按天精准触发与旅行社、Mandala 传奇禁转冒烟；
/// 2. 全库 104+ 条件事件/步骤与 768+ 条件 TSpawnGroup 自动化前置条件构造及发牌铁墙扫查（含 SelfSkills 与 Limit > 5 不截断）；
/// 3. 全库 1,215 条事件/基座/步骤/追踪器中全部活跃实体的触发器与动作 0 未知 $type、0 未捕获异常执行扫描。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class V12EncounterIronWallTests
{
    // ── 官方 GameData.db 权威模板 GUID 常量 ────────────────────────────────────
    private const string LangxianId = "53099361-599f-4c1a-ad4a-d2fbb491b756";
    private const string ShroudedFigureId = "818fcd1d-4cfd-4629-b83f-8d5e753dd8f4";
    private const string AbsorbMemoriesStepId = "5fff0970-87d1-4e43-a466-b771deaf40fe";
    private const string SacrificeAncientLocketStepId = "a45e8b64-cc5c-4e5f-a3c5-438407c0a3f7";
    private const string SecretMeetingStepId = "494941cc-e837-4bf2-9fec-70ebd67c83e8";
    private const string ShowHospitalityStepId = "d733a96a-be96-4379-943b-b0ce3a1e926f";

    private const string AncientLocketId = "d641c57b-ab42-4b2f-b4f4-addd18eb7100";
    private const string OrangeJulianId = "f3c0551a-782b-44e3-b0da-e3825366d20e";
    private const string LuxuryKiuasId = "25035a36-1105-4ef9-81b0-c5bb6926415b";

    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string BayonetId = "6146cacd-202c-489f-88c0-f77ef3e0fda6";
    private const string AilaMerchantId = "dc12f4bd-6c33-41f0-b7ad-d62a0be09a47";

    private const string PygsGymId = "3d185981-0b60-47ce-9ed3-58be5d7b2737";
    private const string CashCannonId = "8f61677c-aa75-4275-b8b4-3cbaca1fdcbf";
    private const string OinkmentId = "64e3569f-e023-49a5-ab75-f9613c338d53";
    private const string CalcinatorId = "3e3bda1d-81ba-4719-8772-417d8b834e99";
    private const string ChunkOfLeadId = "ef20ec33-7990-41b8-8d8e-47514b503612";
    private const string CatalystId = "694a08fe-e8ff-4a45-9693-4c68f11a1f8b";

    private const string DaboraEventId = "5ea96339-6c5e-42b4-bc5f-028b780348e0";
    private const string DaboraSellRightsStepId = "0d275fc2-b4de-42d6-a0c4-ba7c76c950b2";
    private const string DaboraMonsterSightingsStepId = "3fb151ed-a578-4592-888a-2c9ba7449efb";
    private const string DaboraHeroicEncountersStepId = "a7c9181b-6fa5-48b8-a870-90a9c054dc1d";

    private const string ThievesGuildMedallionId = "1e48bd28-cd41-49e9-9e22-b44da30ef6be";
    private const string ThievesGuildEventId = "e792b6a6-2f85-4190-8e3c-002d2736d03d";
    private const string ThievesGuildGatherIntelStepId = "ba7b1001-9cd4-4f3d-9117-c757c6195e5e";

    private const string CrashSiteTicketId = "5bd0a69d-420c-4cd0-93ec-07582c688a5b";
    private const string CrashSiteRoomId = "ead0a1ed-703e-445e-ba43-9a7df5d64f54";
    private const string TempleTicketId = "77c8ef2d-62c8-4214-993d-648915c93e83";
    private const string TempleRoomId = "91287d25-7f63-4826-8c42-06ed1bf83213";
    private const string TinyWorldTicketId = "24dc340b-bb3d-4c9b-8715-a807c0c06a8c";
    private const string TinyWorldRoomId = "42db3205-58d5-4cfe-b537-14091b518ff6";
    private const string TravelAgentEventId = "002d8f6d-0e36-4921-89ed-3f0f28682d7b";

    private const string MandalaEventId = "255ae0fa-f203-4e9f-855d-e090e6937b5b";
    private const string MandalaTransformStepId = "76e60ecc-3dbd-4b57-a7a5-8ad6d40f7abb";
    private const string StreetFestivalEventId = "2ee65e2b-c679-41b6-812d-5dd59a5cb27a";

    private sealed class Fx
    {
        public string DataDir { get; init; } = "";
        public string DbPath { get; init; } = "";
        public CardPool Pool { get; init; } = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
    }

    private static readonly Lazy<Fx> RealFixture = new(() =>
    {
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        return new Fx
        {
            DataDir = shared.DataDir,
            DbPath = shared.DbPath,
            Pool = shared.Pool,
            Monsters = shared.CreateMonstersCopy(),
            LevelUps = shared.LevelUps,
            Mode = shared.Mode,
            Repo = shared.Repo,
            Ghosts = shared.Ghosts,
        };
    });

    // ── 通用只读辅助方法 ──────────────────────────────────────────────────────

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        EHero hero = EHero.Vanessa,
        EPlayMode playMode = EPlayMode.Unranked,
        CustomRunRuleSet? rules = null,
        string? accountId = null,
        Guid? tournamentSessionId = null,
        int seed = 1010)
    {
        var fx = RealFixture.Value;
        var run = new RunState
        {
            RunId = "v12_iw_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = playMode,
            AccountId = accountId,
            TournamentSessionId = tournamentSessionId,
            RngSeed = seed,
            CustomRules = rules ?? new CustomRunRuleSet(),
        };
        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);
        return (run, engine);
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        string templateId,
        EInventorySection section,
        int slot,
        ETier tier = ETier.Bronze,
        string? enchantment = null)
    {
        var fx = RealFixture.Value;
        var coreTpl = fx.Repo.GetCard(templateId) ?? throw new InvalidOperationException($"缺失模板 {templateId}");
        var poolTpl = fx.Pool.Get(templateId);
        var coreCard = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), enchantment, "iw_" + Guid.NewGuid().ToString("N")[..8])
        {
            StartSlot = slot,
        };
        if (section == EInventorySection.Stash)
            Assert.True(run.StashGrid.TryPlaceOrShift(coreCard, slot), $"Stash 槽位 {slot} 无法放入 {templateId}");
        else
            Assert.True(run.HandGrid.TryPlaceOrShift(coreCard, slot), $"Hand 槽位 {slot} 无法放入 {templateId}");

        EEnchantmentType? enchEnum = !string.IsNullOrEmpty(enchantment) && Enum.TryParse<EEnchantmentType>(enchantment, true, out var parsedEnch)
            ? parsedEnch
            : null;
        var entry = new RunState.CardEntry
        {
            InstanceId = coreCard.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ShopEngine.ParseCardType(coreTpl.Type),
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Enchantment = enchEnum,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)Math.Clamp(coreCard.StartSlot, 0, 9),
            CoreCard = coreCard,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, fx.Repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static RunState.CardEntry InjectChoiceEncounter(RunState run, string encounterId)
    {
        var fx = RealFixture.Value;
        var tpl = fx.Pool.Get(encounterId) ?? throw new InvalidOperationException($"缺失遭遇模板 {encounterId}");
        string instId = "inj_" + Guid.NewGuid().ToString("N")[..8];
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(encounterId),
            Type = ShopEngine.ParseCardType(tpl.Type),
            Size = ShopEngine.ParseSize(tpl.Size),
            Tier = ShopEngine.ParseTier(tpl.StartingTier),
            Owner = null,
            Section = null,
            Socket = null,
        };
        RunState.PopulateMetaFromTemplate(entry, tpl, fx.Repo.GetCard(encounterId));
        run.Cards[instId] = entry;
        run.SelectionSet.Add(instId);
        return entry;
    }

    private static bool? DidPlayerWinCombat(RunEngine.Outcome outcome)
    {
        if (outcome.Combat == null) return null;
        return outcome.Combat.Winner == ECombatantId.Player;
    }

    /// <summary>
    /// 黑盒推进一个 Choice 小时并回到下一个 Choice 状态，同时累计期间实际胜负场数（绝不伪造 Custom_*）。
    /// </summary>
    private static void PlayOneHourThroughChoice(
        RunEngine engine,
        RunState run,
        ref int pveWins,
        ref int pvpWins,
        ref int pveLosses,
        ref int pvpLosses,
        bool skipLevelUpChoices = false)
    {
        Assert.Equal(ERunState.Choice, run.State);
        Assert.NotEmpty(run.SelectionSet);

        // 优先挑选确定可完结的候选：Hour 3 挑最左侧（最低阶）野怪以稳健获胜；常规小时挑非 Pedestal 或任意可退事件
        var candidates = run.SelectionSet
            .Select(id => run.Cards[id])
            .ToList();

        RunState.CardEntry pick = candidates.FirstOrDefault(c => c.Type == ECardType.PvpEncounter)
            ?? candidates.FirstOrDefault(c => c.Type == ECardType.CombatEncounter)
            ?? candidates.FirstOrDefault(c => c.Type == ECardType.EventEncounter)
            ?? candidates[0];

        var selOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pick.InstanceId) });
        Assert.Null(selOut.Error);

        bool? won = DidPlayerWinCombat(selOut);
        if (won.HasValue)
        {
            if (pick.Type == ECardType.PvpEncounter)
            {
                if (won.Value) pvpWins++; else pvpLosses++;
            }
            else
            {
                if (won.Value) pveWins++; else pveLosses++;
            }
        }

        DrainNonChoiceStatesToChoice(engine, run, ref pveWins, ref pvpWins, ref pveLosses, ref pvpLosses, skipLevelUpChoices);
    }

    private static void DrainNonChoiceStatesToChoice(
        RunEngine engine,
        RunState run,
        ref int pveWins,
        ref int pvpWins,
        ref int pveLosses,
        ref int pvpLosses,
        bool skipLevelUpChoices = false)
    {
        int safety = 0;
        while (run.State is not (ERunState.Choice or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown))
        {
            Assert.True(safety++ < 40, $"遭遇流转陷入死循环：Day={run.Day} Hour={run.Hour} State={run.State} Enc={run.CurrentEncounterId}");

            if (run.State == ERunState.Pedestal)
            {
                if (run.SelectionRules.CanExit)
                {
                    Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                    continue;
                }
                var anyItem = run.Cards.Values.FirstOrDefault(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item);
                if (anyItem != null)
                {
                    var pedOut = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(anyItem.InstanceId) });
                    if (pedOut.Error == null) continue;
                }
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
                continue;
            }

            if (run.SelectionRules.CanExit && (skipLevelUpChoices || run.State != ERunState.LevelUp || run.SelectionSet.Count == 0))
            {
                var exitOut = engine.Handle(new ExitCurrentStateCommand());
                Assert.Null(exitOut.Error);
                continue;
            }

            if (run.SelectionSet.Count > 0)
            {
                bool handled = false;
                foreach (string cid in run.SelectionSet.ToList())
                {
                    if (!run.Cards.TryGetValue(cid, out var cand)) continue;
                    INetCommand cmd = cand.Type switch
                    {
                        ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter
                            => new SelectEncounterCommand { InstanceId = new InstanceId(cand.InstanceId) },
                        ECardType.Skill
                            => new SelectSkillCommand { InstanceId = new InstanceId(cand.InstanceId) },
                        _
                            => new SelectItemCommand { InstanceId = new InstanceId(cand.InstanceId) },
                    };
                    var stepOut = engine.Handle(cmd);
                    if (stepOut.Error == null)
                    {
                        bool? subWon = DidPlayerWinCombat(stepOut);
                        if (subWon.HasValue)
                        {
                            if (cand.Type == ECardType.PvpEncounter)
                            {
                                if (subWon.Value) pvpWins++; else pvpLosses++;
                            }
                            else
                            {
                                if (subWon.Value) pveWins++; else pveLosses++;
                            }
                        }
                        handled = true;
                        break;
                    }
                }
                if (handled) continue;
            }

            var fbExit = engine.Handle(new ExitCurrentStateCommand());
            Assert.Null(fbExit.Error);
        }
    }

    private static void AdvanceRunToDayAndHour(RunEngine engine, RunState run, uint targetDay, uint targetHour)
    {
        int pveW = 0, pvpW = 0, pveL = 0, pvpL = 0;
        int guard = 0;
        while ((run.Day < targetDay || (run.Day == targetDay && run.Hour < targetHour)) &&
               run.State is not (ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown))
        {
            Assert.True(guard++ < 120, $"未能推进至 Day {targetDay} Hour {targetHour}，停留在 Day={run.Day} Hour={run.Hour} State={run.State}");
            PlayOneHourThroughChoice(engine, run, ref pveW, ref pvpW, ref pveL, ref pvpL);
        }
    }

    private static int GetSerializedCardDamageAmount(RunState run, string instanceId)
    {
        var sim = run.BuildFullGameSim();
        Assert.True(sim.Cards.TryGetValue(instanceId, out var simCard), $"GameSim 中缺失手牌 {instanceId}");
        if (simCard.Attributes != null && simCard.Attributes.TryGetValue(ECardAttributeType.DamageAmount, out var simDmg))
            return simDmg.Value;

        var snap = run.BuildSnapshot();
        var snapCard = snap.Cards.FirstOrDefault(c => c.InstanceId == instanceId);
        Assert.NotNull(snapCard);
        return snapCard!.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 1. 黑盒真实 RunEngine 端到端对抗测试（严禁作弊塞假 Custom_* 初值！）
    // ════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "V12_IronWall_01_Langxian_AbsorbMemories_EndToEnd_FromRealRunFights")]
    public void Langxian_AbsorbMemories_EndToEnd_FromRealRunFights()
    {
        // 开局：仅配置合法的开局武器与初始血量/金币，绝不写入任何 Custom_* 初值！
        var rules = new CustomRunRuleSet
        {
            StartingHealth = 1200,
            StartingGold = 100,
            StartingPrestige = 40,
            StartingItems =
            [
                new PresetCardSpec { TemplateId = CutlassId, Tier = "Diamond" },
                new PresetCardSpec { TemplateId = BayonetId, Tier = "Diamond" },
            ],
        };
        var (run, engine) = CreateStartedEngine(hero: EHero.Vanessa, rules: rules, seed: 202601);

        int pveWins = 0, pvpWins = 0, pveLosses = 0, pvpLosses = 0;

        // 真实推进 Day 1 (Hour 1..6) -> Day 2 (Hour 1..4)，打满 PvE + PvP 至少 3 场真实胜场
        int safety = 0;
        while ((pveWins + pvpWins < 3 || pveWins < 1 || pvpWins < 1) && safety++ < 30)
        {
            PlayOneHourThroughChoice(engine, run, ref pveWins, ref pvpWins, ref pveLosses, ref pvpLosses);
        }

        int totalRealWins = pveWins + pvpWins;
        Assert.True(totalRealWins >= 3, $"预期至少真实打赢 3 场战斗，实际胜场={totalRealWins} (PvE={pveWins}, PvP={pvpWins})");
        Assert.True(pveWins >= 1 && pvpWins >= 1, $"需包含真实 PvE 与 PvP 胜场，实际 PvE={pveWins}, PvP={pvpWins}");
        Assert.Equal(ERunState.Choice, run.State);

        // 在赢得 >= 3 场之后，再让玩家取得青铜 狼筅 (53099361)（确保此前不在场，Custom_1 严格为 0）
        // 同时故意放入多个在 神秘身影 (818fcd1d) 会触发隐藏选项的物品，制造 > 5 个候选（7 选项 / 14 格 > 10 格）的拥挤货架！
        var langxian = AddOwnedItem(run, LangxianId, EInventorySection.Hand, slot: 8, tier: ETier.Bronze);
        AddOwnedItem(run, AncientLocketId, EInventorySection.Stash, slot: 0, tier: ETier.Silver);
        AddOwnedItem(run, OrangeJulianId, EInventorySection.Stash, slot: 2, tier: ETier.Silver);
        AddOwnedItem(run, LuxuryKiuasId, EInventorySection.Stash, slot: 5, tier: ETier.Silver);

        Assert.Equal(0, langxian.Attributes.GetValueOrDefault(ECardAttributeType.Custom_1, 0));
        int baseCustom0 = langxian.Attributes.GetValueOrDefault(ECardAttributeType.Custom_0, 40);
        int baseDmg = langxian.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 40);

        // 注入并进入 神秘身影 (818fcd1d)
        var shroudedEntry = InjectChoiceEncounter(run, ShroudedFigureId);
        var enterOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shroudedEntry.InstanceId) });
        Assert.Null(enterOut.Error);
        Assert.Equal(ERunState.Encounter, run.State);

        var shelfStepIds = run.SelectionSet
            .Select(id => run.Cards[id].TemplateId.ToString())
            .ToList();

        // 强硬断言：[Shrouded Figure] Absorb Memories (5fff0970) 必须出现，且全部已激活的专属条件选项绝不能因排在后方被无条件组或 10 格截断！
        Assert.Contains(AbsorbMemoriesStepId, shelfStepIds);
        Assert.Contains(SacrificeAncientLocketStepId, shelfStepIds);
        Assert.Contains(SecretMeetingStepId, shelfStepIds);
        Assert.Contains(ShowHospitalityStepId, shelfStepIds);

        // 点选 Absorb Memories (5fff0970)
        var absorbCard = run.SelectionSet
            .Select(id => run.Cards[id])
            .First(c => string.Equals(c.TemplateId.ToString(), AbsorbMemoriesStepId, StringComparison.OrdinalIgnoreCase));
        var absorbOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(absorbCard.InstanceId) });
        Assert.Null(absorbOut.Error);

        // 强硬断言：狼筅的 Custom_1 严格等于此前真实胜场数，且手牌及序列化面板 DamageAmount 真实加上了 胜场数 × Custom_0！
        int actualCustom1 = langxian.Attributes.GetValueOrDefault(ECardAttributeType.Custom_1, 0);
        Assert.Equal(totalRealWins, actualCustom1);

        int expectedDmg = baseDmg + totalRealWins * baseCustom0;
        int entryDmg = langxian.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0);
        int serializedDmg = GetSerializedCardDamageAmount(run, langxian.InstanceId);
        Assert.Equal(expectedDmg, entryDmg);
        Assert.Equal(expectedDmg, serializedDmg);
    }

    [Fact(DisplayName = "V12_IronWall_02_PlayerEffect_Trackers_EndToEnd")]
    public void PlayerEffect_Trackers_EndToEnd()
    {
        // A. 购买 Weapon -> Custom_4 驱动 Pyg's Gym；赚金币 -> Custom_3 驱动 Cash Cannon / Oinkment
        {
            var rules = new CustomRunRuleSet
            {
                StartingGold = 100,
                StartingIncome = 5,
                StartingHealth = 500,
                StartingItems =
                [
                    new PresetCardSpec { TemplateId = PygsGymId, Tier = "Silver" },   // Large (3 格), Custom_0=2
                    new PresetCardSpec { TemplateId = CashCannonId, Tier = "Silver" }, // Medium, 2*Custom_3
                    new PresetCardSpec { TemplateId = OinkmentId, Tier = "Silver" },   // Small, 1*Custom_3
                    new PresetCardSpec { TemplateId = CutlassId, Tier = "Bronze" },    // Weapon in Hand
                ],
            };
            var (run, engine) = CreateStartedEngine(hero: EHero.Pygmalien, rules: rules, seed: 202602);
            // 解锁全部手牌格位以便容纳多件测试装备（等价升级解锁格位，不触碰 Custom_*）
            Assert.Null(engine.Handle(new CheatCommand { Args = ["slots"] }).Error);
            // 将开局默认可能在 Stash 的 Cutlass 与 Pyg's Gym 确保置于 Hand，并记录初始属性
            var pygsGym = run.Cards.Values.First(c => c.TemplateId == Guid.Parse(PygsGymId));
            if (pygsGym.Section != EInventorySection.Hand)
            {
                Assert.Null(engine.Handle(new MoveItemCommand
                {
                    InstanceId = new InstanceId(pygsGym.InstanceId),
                    Section = EInventorySection.Hand,
                    TargetSockets = [EContainerSocketId.Socket_0],
                }).Error);
            }
            var cutlass = run.Cards.Values.First(c => c.TemplateId == Guid.Parse(CutlassId));
            if (cutlass.Section != EInventorySection.Hand)
            {
                Assert.Null(engine.Handle(new MoveItemCommand
                {
                    InstanceId = new InstanceId(cutlass.InstanceId),
                    Section = EInventorySection.Hand,
                    TargetSockets = [EContainerSocketId.Socket_8],
                }).Error);
            }
            int cutlassBaseDmg = cutlass.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 10);
            int gymPerWeaponBonus = pygsGym.Attributes.GetValueOrDefault(ECardAttributeType.Custom_0, 2);

            // 进入武器商人 Aila 并购买一把 Weapon
            var aila = InjectChoiceEncounter(run, AilaMerchantId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(aila.InstanceId) }).Error);
            Assert.Equal(ERunState.Encounter, run.State);

            var weaponOffer = run.SelectionSet
                .Select(id => run.Cards[id])
                .FirstOrDefault(c => c.Type == ECardType.Item && c.Tags.Contains(ECardTag.Weapon));
            Assert.NotNull(weaponOffer);

            int goldBeforeBuy = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
            var buyOut = engine.Handle(new SelectItemCommand
            {
                InstanceId = new InstanceId(weaponOffer!.InstanceId),
                Section = EInventorySection.Stash,
            });
            Assert.Null(buyOut.Error);

            // 验证 Custom_4 == 1 且 Pyg's Gym 实时给手牌中 Weapon 加上了 1 * Custom_0 伤害
            Assert.Equal(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_4, 0));
            Assert.Equal(cutlassBaseDmg + gymPerWeaponBonus, cutlass.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0));

            // 退出 Aila，再通过真实 SellCardCommand 卖出刚买的武器赚取金币，验证 Custom_3 与 Cash Cannon / Oinkment
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            int goldBeforeSell = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
            int custom3BeforeSell = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0);
            var boughtCard = run.Cards[weaponOffer.InstanceId];
            int expectedSellGain = Math.Max(1, boughtCard.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice, 1));
            Assert.Null(engine.Handle(new SellCardCommand { InstanceId = new InstanceId(boughtCard.InstanceId) }).Error);

            int goldAfterSell = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
            int actualGoldGained = goldAfterSell - goldBeforeSell;
            Assert.True(actualGoldGained >= expectedSellGain && goldBeforeBuy > 0);
            int custom3AfterSell = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_3, 0);
            Assert.Equal(custom3BeforeSell + actualGoldGained, custom3AfterSell);

            var cashCannon = run.Cards.Values.First(c => c.TemplateId == Guid.Parse(CashCannonId));
            var oinkment = run.Cards.Values.First(c => c.TemplateId == Guid.Parse(OinkmentId));
            Assert.Equal(2 * custom3AfterSell, cashCannon.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0));
            Assert.Equal(custom3AfterSell, oinkment.Attributes.GetValueOrDefault(ECardAttributeType.HealAmount, 0));
        }

        // B. 转化 Reagent 累加 Custom_9 驱动 Calcinator
        {
            var (run, engine) = CreateStartedEngine(hero: EHero.Mak, seed: 202603);
            var lead = AddOwnedItem(run, ChunkOfLeadId, EInventorySection.Hand, slot: 3, tier: ETier.Bronze);
            var calcinator = AddOwnedItem(run, CalcinatorId, EInventorySection.Hand, slot: 4, tier: ETier.Bronze);
            var catalyst = AddOwnedItem(run, CatalystId, EInventorySection.Hand, slot: 6, tier: ETier.Bronze);

            int baseBurn = calcinator.Attributes.GetValueOrDefault(ECardAttributeType.BurnApplyAmount, 10);
            int custom0 = calcinator.Attributes.GetValueOrDefault(ECardAttributeType.Custom_0, 2);

            // 卖出 Catalyst 触发对其最左侧非传奇物品（Chunk of Lead, Reagent 标签）的真实 TActionCardTransform
            var sellCatOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(catalyst.InstanceId) });
            Assert.Null(sellCatOut.Error);

            Assert.False(run.Cards.ContainsKey(lead.InstanceId), "Chunk of Lead 应已被 Catalyst 转化并移除旧实例");
            Assert.Equal(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_9, 0));
            Assert.Equal(baseBurn + custom0, calcinator.Attributes.GetValueOrDefault(ECardAttributeType.BurnApplyAmount, 0));
        }

        // C. 战败（PvE + PvP）累加 Custom_6/7/8 驱动 Dabora 记者分支
        {
            // 空手牌空技能开局，保证 PvE 与 PvP 真实战败
            var rules = new CustomRunRuleSet
            {
                StartingHealth = 100,
                StartingPrestige = 30,
            };
            var (run, engine) = CreateStartedEngine(hero: EHero.Vanessa, rules: rules, seed: 202604);
            int pveW = 0, pvpW = 0, pveL = 0, pvpL = 0;

            // 推进至 Hour 3 并打完真实 PvE（预期战败）
            while (pveL + pveW == 0)
            {
                PlayOneHourThroughChoice(engine, run, ref pveW, ref pvpW, ref pveL, ref pvpL, skipLevelUpChoices: true);
            }
            Assert.Equal(1, pveL);
            Assert.Equal(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_6, 0));
            Assert.Equal(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_7, 0));
            Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_8, 0));

            // 继续推进至 Hour 6 并打完真实 PvP（预期战败）
            while (pvpL + pvpW == 0)
            {
                PlayOneHourThroughChoice(engine, run, ref pveW, ref pvpW, ref pveL, ref pvpL, skipLevelUpChoices: true);
            }
            Assert.Equal(1, pvpL);
            Assert.Equal(2, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_6, 0));
            Assert.Equal(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_7, 0));
            Assert.Equal(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_8, 0));

            // 进入 Dabora (5ea96339) -> 选择 Heroic Encounters (a7c9181b) -> 声望增加 2 * Custom_8 (+2)
            int prestigeBefore = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0);
            var dabora1 = InjectChoiceEncounter(run, DaboraEventId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(dabora1.InstanceId) }).Error);
            var heroicStep = run.SelectionSet
                .Select(id => run.Cards[id])
                .First(c => c.TemplateId == Guid.Parse(DaboraHeroicEncountersStepId));
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(heroicStep.InstanceId) }).Error);
            DrainNonChoiceStatesToChoice(engine, run, ref pveW, ref pvpW, ref pveL, ref pvpL);
            Assert.Equal(prestigeBefore + 2, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0));

            // 再次进入 Dabora -> 选择 Monster Sightings (3fb151ed) -> 经验增加 Custom_7 (+1)
            // 先把 XP 设为非升级边界以便直接观察增量，或检查总累计经验（等级 * 8 + XP）
            var fx = RealFixture.Value;
            int xpPerLvl = Math.Max(1, fx.Mode.ExperiencePerLevel);
            int totalXpBefore = (run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1) - 1) * xpPerLvl
                              + run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
            var dabora2 = InjectChoiceEncounter(run, DaboraEventId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(dabora2.InstanceId) }).Error);
            var monsterStep = run.SelectionSet
                .Select(id => run.Cards[id])
                .First(c => c.TemplateId == Guid.Parse(DaboraMonsterSightingsStepId));
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(monsterStep.InstanceId) }).Error);
            // 注意 Dabora 本身退出也带 ExperienceAwardUponSelection=1（或受每小时门控限制），至少包含 Monster Sightings 的 Custom_7 (+1)
            DrainNonChoiceStatesToChoice(engine, run, ref pveW, ref pvpW, ref pveL, ref pvpL);
            int totalXpAfter = (run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1) - 1) * xpPerLvl
                             + run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
            Assert.True(totalXpAfter > totalXpBefore, $"Dabora Monster Sightings 应基于 Custom_7={run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_7, 0)} 发放经验，实际 before={totalXpBefore}, after={totalXpAfter}");
        }

        // D. 锦标赛模式对局 -> Custom_11 == 1
        {
            var (tRun, _) = CreateStartedEngine(
                hero: EHero.Vanessa,
                playMode: EPlayMode.Tournament,
                accountId: "v12_tourney_acct",
                tournamentSessionId: Guid.NewGuid(),
                seed: 202605);
            Assert.Equal(1, tRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_11, 0));
        }
    }

    [Fact(DisplayName = "V12_IronWall_03_ThievesGuild_Medallion_And_ZeroHourCost")]
    public void ThievesGuild_Medallion_And_ZeroHourCost()
    {
        var rules = new CustomRunRuleSet
        {
            StartingHealth = 800,
            StartingGold = 100,
            StartingPrestige = 40,
            StartingItems =
            [
                new PresetCardSpec { TemplateId = CutlassId, Tier = "Diamond" },
            ],
        };
        var (run, engine) = CreateStartedEngine(hero: EHero.Vanessa, rules: rules, seed: 202606);

        // 先推进至 Day 2 Hour 1（此时 maxDayTier = Silver，严格低于 Legendary，用以对抗 Legendary 品阶拦截 Bug！）
        AdvanceRunToDayAndHour(engine, run, targetDay: 2, targetHour: 1);
        Assert.Equal(2u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.True(run.ExclusionSet.Contains(ThievesGuildEventId), "开局未卖徽章前 Thieves Guild 必须处于 ExclusionSet 中");

        // 放入并卖出 Thieves Guild Medallion (1e48bd28)
        var medallion = AddOwnedItem(run, ThievesGuildMedallionId, EInventorySection.Hand, slot: 5, tier: ETier.Legendary);
        var sellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(medallion.InstanceId) });
        Assert.Null(sellOut.Error);

        Assert.False(run.ExclusionSet.Contains(ThievesGuildEventId), "卖出徽章后 Thieves Guild 必须从 ExclusionSet 中解锁移除");
        Assert.Equal(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_12, 0));

        // 在随后的 Day 2 常规小时刷新中验证 Thieves Guild 能够合法刷出（绝不能被 Legendary 品阶挡掉！）
        string? dealtThievesGuildInstId = null;
        for (int attempt = 0; attempt < 500; attempt++)
        {
            run.VisitedDayEncounterIds.Clear();
            var dealOut = engine.Handle(new CheatCommand { Args = ["deal"] });
            Assert.Null(dealOut.Error);
            var hit = run.SelectionSet
                .Select(id => run.Cards[id])
                .FirstOrDefault(c => string.Equals(c.TemplateId.ToString(), ThievesGuildEventId, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                dealtThievesGuildInstId = hit.InstanceId;
                break;
            }
        }
        Assert.False(string.IsNullOrEmpty(dealtThievesGuildInstId), "卖出徽章解锁后，Day 2 常规小时刷牌仍无法刷出 Thieves Guild（疑似被 Legendary 品阶拦截！）");

        uint dayBeforeEnter = run.Day;
        uint hourBeforeEnter = run.Hour;

        // 进入 Thieves Guild (e792b6a6)
        var enterTgOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(dealtThievesGuildInstId!) });
        Assert.Null(enterTgOut.Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(ThievesGuildEventId, run.CurrentEncounterId);
        Assert.NotEmpty(run.SelectionSet);

        // 点选一个奖品（Gather Intel ba7b1001）并退出遭遇
        var prize = run.SelectionSet
            .Select(id => run.Cards[id])
            .FirstOrDefault(c => string.Equals(c.TemplateId.ToString(), ThievesGuildGatherIntelStepId, StringComparison.OrdinalIgnoreCase))
            ?? run.Cards[run.SelectionSet[0]];
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(prize.InstanceId) }).Error);

        int pveW = 0, pvpW = 0, pveL = 0, pvpL = 0;
        DrainNonChoiceStatesToChoice(engine, run, ref pveW, ref pvpW, ref pveL, ref pvpL);

        // 强硬断言：Thieves Guild (ShouldProgressHour=false) 退出后 Hour 绝不推进！
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(dayBeforeEnter, run.Day);
        Assert.Equal(hourBeforeEnter, run.Hour);
        Assert.True(run.ExclusionSet.Contains(ThievesGuildEventId), "进入 Thieves Guild 后其 TTriggerOnCardSelected 应将其重新加入 ExclusionSet");
        Assert.Equal(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Custom_12, 0));
    }

    [Fact(DisplayName = "V12_IronWall_04_Expeditions_Day6_Day9_Day10_And_TravelAgent")]
    public void Expeditions_Day6_Day9_Day10_And_TravelAgent()
    {
        // A. Crash Site Ticket (5bd0a69d)：Day 5 Hour 1 绝不提前触发；Day 6 Hour 1 作为第 4 张探险卡出现
        //    A1. 点击第 4 张探险卡 -> 自动扣票且入房展开可选分支
        {
            var rules = new CustomRunRuleSet
            {
                StartingHealth = 1500,
                StartingPrestige = 60,
                StartingGold = 100,
                StartingItems = [new PresetCardSpec { TemplateId = CutlassId, Tier = "Diamond" }],
            };
            var (run, engine) = CreateStartedEngine(hero: EHero.Vanessa, rules: rules, seed: 202607);
            var ticket = AddOwnedItem(run, CrashSiteTicketId, EInventorySection.Stash, slot: 0, tier: ETier.Legendary);

            // 推进到 Day 5 Hour 1
            AdvanceRunToDayAndHour(engine, run, targetDay: 5, targetHour: 1);
            Assert.Equal(5u, run.Day);
            Assert.Equal(1u, run.Hour);
            Assert.Equal(ERunState.Choice, run.State);
            Assert.True(run.Cards.ContainsKey(ticket.InstanceId), "Day 5 Hour 1 绝不能提前消耗 Crash Site Ticket");
            Assert.DoesNotContain(run.SelectionSet, id => run.Cards[id].TemplateId == Guid.Parse(CrashSiteRoomId));

            // 推进到 Day 6 Hour 1
            AdvanceRunToDayAndHour(engine, run, targetDay: 6, targetHour: 1);
            Assert.Equal(6u, run.Day);
            Assert.Equal(1u, run.Hour);
            Assert.Equal(ERunState.Choice, run.State);

            // 强硬断言：第 4 张探险卡出现（共 4 张 Choice 卡，前 3 张常规 + 第 4 张 Crash Site Expedition），且未点击前门票仍在
            Assert.Equal(4, run.SelectionSet.Count);
            Assert.True(run.Cards.ContainsKey(ticket.InstanceId), "展示第 4 张探险卡时尚未点击进入，不得提前扣票");
            var expCandidate = run.SelectionSet
                .Select(id => run.Cards[id])
                .FirstOrDefault(c => c.TemplateId == Guid.Parse(CrashSiteRoomId));
            Assert.NotNull(expCandidate);

            // 点击进入 Crash Site Expedition -> 自动扣票且进入房间展开可选分支
            var enterExpOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(expCandidate!.InstanceId) });
            Assert.Null(enterExpOut.Error);
            Assert.Equal(ERunState.Encounter, run.State);
            Assert.Equal(CrashSiteRoomId, run.CurrentEncounterId);
            Assert.False(run.Cards.ContainsKey(ticket.InstanceId), "点击进入探险房间后必须自动消耗 Crash Site Ticket");
            Assert.NotEmpty(run.SelectionSet);
        }

        //    A2. Day 6 Hour 1 点击另外 3 张普通卡 -> 正常跳过探险且保留门票
        {
            var rules = new CustomRunRuleSet
            {
                StartingHealth = 1500,
                StartingPrestige = 60,
                StartingGold = 100,
                StartingItems = [new PresetCardSpec { TemplateId = CutlassId, Tier = "Diamond" }],
            };
            var (run, engine) = CreateStartedEngine(hero: EHero.Vanessa, rules: rules, seed: 202608);
            var ticket = AddOwnedItem(run, CrashSiteTicketId, EInventorySection.Stash, slot: 0, tier: ETier.Legendary);

            AdvanceRunToDayAndHour(engine, run, targetDay: 6, targetHour: 1);
            Assert.Equal(4, run.SelectionSet.Count);
            var normalCandidate = run.SelectionSet
                .Select(id => run.Cards[id])
                .First(c => c.TemplateId != Guid.Parse(CrashSiteRoomId));
            var skipOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(normalCandidate.InstanceId) });
            Assert.Null(skipOut.Error);
            Assert.NotEqual(CrashSiteRoomId, run.CurrentEncounterId);
            Assert.True(run.Cards.ContainsKey(ticket.InstanceId), "点选另外 3 张普通卡应正常跳过探险且绝不误扣门票");
        }

        // B. Day 9 Temple Expedition (91287d25) 与 Day 10 Tiny World Expedition (42db3205)
        {
            var rules = new CustomRunRuleSet
            {
                StartingHealth = 2000,
                StartingPrestige = 80,
                StartingGold = 100,
                StartingItems = [new PresetCardSpec { TemplateId = CutlassId, Tier = "Diamond" }],
            };
            var (run, engine) = CreateStartedEngine(hero: EHero.Vanessa, rules: rules, seed: 202609);
            var templeTicket = AddOwnedItem(run, TempleTicketId, EInventorySection.Stash, slot: 0, tier: ETier.Legendary);
            var tinyTicket = AddOwnedItem(run, TinyWorldTicketId, EInventorySection.Stash, slot: 1, tier: ETier.Legendary);

            // Day 8 Hour 1：两种票均不应提前触发
            AdvanceRunToDayAndHour(engine, run, targetDay: 8, targetHour: 1);
            Assert.Equal(8u, run.Day);
            Assert.Equal(1u, run.Hour);
            Assert.Equal(3, run.SelectionSet.Count);
            Assert.True(run.Cards.ContainsKey(templeTicket.InstanceId));
            Assert.True(run.Cards.ContainsKey(tinyTicket.InstanceId));

            // Day 9 Hour 1：Temple Expedition 作为第 4 张卡出现（Tiny World 绝不提前出现），进入并扣票展开 3 扇门
            AdvanceRunToDayAndHour(engine, run, targetDay: 9, targetHour: 1);
            Assert.Equal(9u, run.Day);
            Assert.Equal(1u, run.Hour);
            Assert.Equal(4, run.SelectionSet.Count);
            var templeCard = run.SelectionSet.Select(id => run.Cards[id]).FirstOrDefault(c => c.TemplateId == Guid.Parse(TempleRoomId));
            Assert.NotNull(templeCard);
            Assert.DoesNotContain(run.SelectionSet, id => run.Cards[id].TemplateId == Guid.Parse(TinyWorldRoomId));

            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(templeCard!.InstanceId) }).Error);
            Assert.Equal(ERunState.Encounter, run.State);
            Assert.Equal(TempleRoomId, run.CurrentEncounterId);
            Assert.False(run.Cards.ContainsKey(templeTicket.InstanceId), "进入 Temple Expedition 后应自动扣掉 Temple 门票");
            Assert.True(run.Cards.ContainsKey(tinyTicket.InstanceId), "Tiny World 门票此时不得被消耗");
            Assert.NotEmpty(run.SelectionSet);

            // 清理离开 Temple 并继续推进到 Day 10 Hour 1 -> Tiny World Expedition 作为第 4 张卡出现
            int pveW = 0, pvpW = 0, pveL = 0, pvpL = 0;
            DrainNonChoiceStatesToChoice(engine, run, ref pveW, ref pvpW, ref pveL, ref pvpL);
            AdvanceRunToDayAndHour(engine, run, targetDay: 10, targetHour: 1);
            Assert.Equal(10u, run.Day);
            Assert.Equal(1u, run.Hour);
            Assert.Equal(4, run.SelectionSet.Count);
            var tinyCard = run.SelectionSet.Select(id => run.Cards[id]).FirstOrDefault(c => c.TemplateId == Guid.Parse(TinyWorldRoomId));
            Assert.NotNull(tinyCard);

            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(tinyCard!.InstanceId) }).Error);
            Assert.Equal(ERunState.Encounter, run.State);
            Assert.Equal(TinyWorldRoomId, run.CurrentEncounterId);
            Assert.False(run.Cards.ContainsKey(tinyTicket.InstanceId), "进入 Tiny World Expedition 后应自动扣掉 Tiny World 门票");
            Assert.NotEmpty(run.SelectionSet);
        }

        // C. The Travel Agent (002d8f6d)：Day 1 禁出、Day 2..9 可合法入池
        {
            var fx = RealFixture.Value;
            // Day 1 即使反复刷新（含普通权重与放大权重），绝不可刷出 The Travel Agent
            for (int mode = 0; mode < 2; mode++)
            {
                var rules = mode == 0
                    ? new CustomRunRuleSet()
                    : new CustomRunRuleSet
                    {
                        EncounterCategoryMultipliers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["EventExpeditionAgent"] = 10.0,
                        },
                    };
                var day1Run = new RunState
                {
                    RunId = "ta_d1_" + mode,
                    GameModeId = fx.Pool.DefaultGameModeId,
                    Hero = EHero.Vanessa,
                    Day = 1,
                    Hour = 1,
                    RngSeed = 700 + mode,
                    CustomRules = rules,
                };
                var shop = new ShopEngine(day1Run, fx.Pool, fx.Repo, rules);
                for (int i = 0; i < 120; i++)
                {
                    day1Run.VisitedDayEncounterIds.Clear();
                    day1Run.VisitedSingleSpawnEncounterIds.Clear();
                    shop.DealHourEncounters();
                    Assert.DoesNotContain(
                        day1Run.SelectionSet,
                        id => day1Run.Cards[id].TemplateId == Guid.Parse(TravelAgentEventId));
                }
            }

            // P1-25 校准：标准规则下 Day 2..5（代表性校验 Day 2、Day 5）可合法刷出 The Travel Agent；
            // Day 9 起标准规则不再刷出（避免卖出过期门票，自定义放大规则 EventExpeditionAgent=8.0 仍由其他用例覆盖）
            foreach (uint checkDay in new uint[] { 2, 5 })
            {
                bool appeared = false;
                var dayRun = new RunState
                {
                    RunId = "ta_d" + checkDay,
                    GameModeId = fx.Pool.DefaultGameModeId,
                    Hero = EHero.Vanessa,
                    Day = checkDay,
                    Hour = 2,
                    RngSeed = 8000 + (int)checkDay,
                    CustomRules = new CustomRunRuleSet(),
                };
                var shop = new ShopEngine(dayRun, fx.Pool, fx.Repo, dayRun.CustomRules);
                for (int attempt = 0; attempt < 1500; attempt++)
                {
                    dayRun.VisitedDayEncounterIds.Clear();
                    dayRun.VisitedSingleSpawnEncounterIds.Clear();
                    shop.DealHourEncounters();
                    if (dayRun.SelectionSet.Any(id => dayRun.Cards[id].TemplateId == Guid.Parse(TravelAgentEventId)))
                    {
                        appeared = true;
                        break;
                    }
                }
                Assert.True(appeared, $"Day {checkDay} 常规小时刷牌（1500 次内）应能合法刷出 The Travel Agent (002d8f6d)");
            }

            {
                var day9Run = new RunState
                {
                    RunId = "ta_d9_std_forbid",
                    GameModeId = fx.Pool.DefaultGameModeId,
                    Hero = EHero.Vanessa,
                    Day = 9,
                    Hour = 2,
                    RngSeed = 8009,
                    CustomRules = new CustomRunRuleSet(),
                };
                var shop9 = new ShopEngine(day9Run, fx.Pool, fx.Repo, day9Run.CustomRules);
                for (int attempt = 0; attempt < 300; attempt++)
                {
                    day9Run.VisitedDayEncounterIds.Clear();
                    day9Run.VisitedSingleSpawnEncounterIds.Clear();
                    shop9.DealHourEncounters();
                    Assert.DoesNotContain(
                        day9Run.SelectionSet,
                        id => day9Run.Cards[id].TemplateId == Guid.Parse(TravelAgentEventId));
                }
            }
        }
    }

    [Fact(DisplayName = "V12_IronWall_05_Pedestal_And_CardTransform_Smoke")]
    public void Pedestal_And_CardTransform_Smoke()
    {
        // A. Mandala (255ae0fa) 最左侧仅放 Legendary 物品：绝对不可转化
        {
            var (run, engine) = CreateStartedEngine(hero: EHero.Vanessa, seed: 202610);
            var legCard = AddOwnedItem(run, ThievesGuildMedallionId, EInventorySection.Hand, slot: 3, tier: ETier.Legendary);
            string origInstId = legCard.InstanceId;
            Guid origTplId = legCard.TemplateId;

            var mandala = InjectChoiceEncounter(run, MandalaEventId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mandala.InstanceId) }).Error);
            Assert.Equal(ERunState.Encounter, run.State);

            var step = run.SelectionSet
                .Select(id => run.Cards[id])
                .First(c => c.TemplateId == Guid.Parse(MandalaTransformStepId));
            var stepOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step.InstanceId) });
            Assert.Null(stepOut.Error);

            Assert.True(run.Cards.ContainsKey(origInstId), "Mandala 最左侧为 Legendary 卡时绝不能被转化销毁");
            Assert.Equal(origTplId, run.Cards[origInstId].TemplateId);
            Assert.Equal(ETier.Legendary, run.Cards[origInstId].Tier);
        }

        // B. Mandala 最左侧放 Legendary、右侧放普通 Small 卡：跳过传奇、转化普通卡
        {
            var (run, engine) = CreateStartedEngine(hero: EHero.Vanessa, seed: 202611);
            var legLeft = AddOwnedItem(run, ThievesGuildMedallionId, EInventorySection.Hand, slot: 3, tier: ETier.Legendary);
            var normRight = AddOwnedItem(run, BayonetId, EInventorySection.Hand, slot: 4, tier: ETier.Bronze);
            string legLeftId = legLeft.InstanceId;
            string oldNormId = normRight.InstanceId;

            var mandala = InjectChoiceEncounter(run, MandalaEventId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mandala.InstanceId) }).Error);
            var step = run.SelectionSet
                .Select(id => run.Cards[id])
                .First(c => c.TemplateId == Guid.Parse(MandalaTransformStepId));
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step.InstanceId) }).Error);

            Assert.True(run.Cards.ContainsKey(legLeftId), "Mandala 混合手牌最左侧 Legendary 绝不能被转化");
            Assert.False(run.Cards.ContainsKey(oldNormId), "Mandala 跳过最左侧 Legendary 后应转化右侧普通卡");
            var transformedCard = run.Cards.Values.FirstOrDefault(c =>
                c.Owner == ECombatantId.Player &&
                c.Type == ECardType.Item &&
                c.Section == EInventorySection.Hand &&
                c.InstanceId != legLeftId);
            Assert.NotNull(transformedCard);
            Assert.NotEqual(ETier.Legendary, transformedCard!.Tier);
        }

        // C. Mandala 最左侧直接放普通卡正常转化
        {
            var (run, engine) = CreateStartedEngine(hero: EHero.Vanessa, seed: 202612);
            var normLeft = AddOwnedItem(run, BayonetId, EInventorySection.Hand, slot: 3, tier: ETier.Bronze);
            string oldNormId = normLeft.InstanceId;

            var mandala = InjectChoiceEncounter(run, MandalaEventId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mandala.InstanceId) }).Error);
            var step = run.SelectionSet
                .Select(id => run.Cards[id])
                .First(c => c.TemplateId == Guid.Parse(MandalaTransformStepId));
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step.InstanceId) }).Error);

            Assert.False(run.Cards.ContainsKey(oldNormId), "Mandala 最左侧普通卡应正常转化为新卡");
            var transformedCard = run.Cards.Values.FirstOrDefault(c =>
                c.Owner == ECombatantId.Player &&
                c.Type == ECardType.Item &&
                c.Section == EInventorySection.Hand);
            Assert.NotNull(transformedCard);
            Assert.NotEqual(ETier.Legendary, transformedCard!.Tier);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2. 全库条件事件 / 条件 TSpawnGroup 穷举铁墙扫查
    // ════════════════════════════════════════════════════════════════════════

    private sealed record ConditionalGroupRecord(
        string CardId,
        string CardType,
        string InternalName,
        string Path,
        RuleNode SpawnContext,
        RuleNode Group);

    [Fact(DisplayName = "V12_IronWall_06_AllConditionalEncounterGroups_FullDbSweep")]
    public void AllConditionalEncounterGroups_FullDbSweep()
    {
        var fx = RealFixture.Value;
        var repo = fx.Repo;
        var rawRecords = LoadRawCardDocsFromDb(fx.DbPath);

        var conditionalGroups = new List<ConditionalGroupRecord>();
        var activeConditionalCards = new Dictionary<string, (string Type, string Name, CardTemplate Tpl)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, type, name, doc) in rawRecords)
        {
            if (type is not ("EventEncounter" or "EncounterStep")) continue;
            if (IsInactiveDbRecord(doc.RootElement, name)) continue;

            var tpl = repo.GetCard(id);
            Assert.NotNull(tpl);

            var found = new List<(string Path, RuleNode SpawnCtx, RuleNode Group)>();
            if (tpl!.SelectionContext?.SpawnContext != null)
            {
                CollectConditionalGroups(tpl.SelectionContext.SpawnContext, "SelectionContext.SpawnContext", found);
            }
            foreach (var (aid, ab) in tpl.Abilities)
            {
                if (ab.Action != null)
                    CollectConditionalGroupsFromNode(ab.Action, $"Abilities[{aid}].Action", found);
            }

            if (found.Count > 0)
            {
                activeConditionalCards[id] = (type, name, tpl);
                foreach (var (path, sc, grp) in found)
                    conditionalGroups.Add(new ConditionalGroupRecord(id, type, name, path, sc, grp));
            }
        }

        // 核验覆盖规模与 DB 一致（GameData.db 实测 104 张活跃条件事件/步骤、768 条条件 TSpawnGroup）
        Assert.True(activeConditionalCards.Count >= 100, $"预期 >= 100 个活跃条件事件/步骤，实际 {activeConditionalCards.Count}");
        Assert.True(conditionalGroups.Count >= 750, $"预期 >= 750 条条件 TSpawnGroup，实际 {conditionalGroups.Count}");

        var failures = new List<string>();
        int selfSkillsVerified = 0;

        foreach (var rec in conditionalGroups)
        {
            var cardTpl = repo.GetCard(rec.CardId)!;
            var player = new PlayerState("sweep_p1", "SweepPlayer", "Vanessa", 500, 20, 200, 10)
            {
                Day = 5,
                Hour = 2,
                Level = 5,
            };
            player.Hand.UnlockedMask = 0x3FF;
            player.Stash.UnlockedMask = 0x3FF;

            int desiredDay = 5;
            foreach (var prereq in rec.Group.Prerequisites!)
            {
                SatisfyPrerequisite(prereq, player, repo, ref desiredDay);
            }
            player.Day = desiredDay;

            // 使用未入主牌库的事件/步骤卡作为 SelfCard（模拟真实无主事件语境）
            var selfCard = new CardInstance(cardTpl);
            var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(17))
            {
                CurrentDay = desiredDay,
                CurrentHour = 2,
                CurrentEncounterId = rec.CardId,
                PlayMode = "Unranked",
                SelfCard = selfCard,
                TriggerSourceCard = selfCard,
            };

            // 1) 前置条件必须 100% 匹配通过
            if (!ValueAndConditionEvaluator.EvaluatePrerequisites(rec.Group.Prerequisites, ctx))
            {
                failures.Add($"[PrereqFail] {rec.CardId} ({rec.InternalName}) @ {rec.Path}");
                continue;
            }

            // 2) 若条件涉及 SelfSkills / SocketEffects 等易受无主 SelfCard 影响的区段，实测发牌引擎不能因无主区段返回空
            bool usesSelfSkills = rec.Group.Prerequisites.Any(p =>
                string.Equals(p.Subject?.TargetSection, "SelfSkills", StringComparison.OrdinalIgnoreCase));
            if (usesSelfSkills)
            {
                // 2a. 以保底非空池 + 原 Prerequisites 实测发牌：100% 证明 SelfSkills 绝不因无主 SelfCard 失效
                var guaranteedProbeGroup = new RuleNode
                {
                    Type = "TSpawnGroup",
                    SelectionMethod = "Sequential",
                    Prerequisites = rec.Group.Prerequisites,
                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [BayonetId] }],
                    Behaviors =
                    [
                        new RuleNode { Type = "TSpawnBehaviorIgnoreHero", IgnoreHero = true },
                        new RuleNode { Type = "TSpawnBehaviorIgnoreTierTable", IgnoreTierTable = true },
                    ],
                };
                var probeQuery = new RuleNode
                {
                    Type = "TSpawnContextQuery",
                    SelectionMethod = "Sequential",
                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement(1.0) },
                    Groups = [guaranteedProbeGroup],
                };
                var probeSpawned = SpawnQueryEngine.SpawnCards(probeQuery, ctx);
                if (probeSpawned.Count == 0)
                {
                    failures.Add($"[SelfSkillsPrereqDropped] {rec.CardId} ({rec.InternalName}) @ {rec.Path}");
                }
                else
                {
                    selfSkillsVerified++;
                }

                // 2b. 对原组过滤器亦调用真实发牌（仅当 GameData.db 本身存在 >=1 张符合筛选条件的卡时，要求发牌非空）
                var singleGroupQuery = new RuleNode
                {
                    Type = "TSpawnContextQuery",
                    SelectionMethod = "Sequential",
                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement(1.0) },
                    Groups = [rec.Group],
                    Behaviors = rec.SpawnContext.Behaviors,
                };
                var spawned = SpawnQueryEngine.SpawnCards(singleGroupQuery, ctx);
                if (spawned.Count == 0 && GroupHasEligibleDbCandidates(rec.Group, rec.SpawnContext, player.Hero, repo))
                {
                    failures.Add($"[SelfSkillsEmptySpawn] {rec.CardId} ({rec.InternalName}) @ {rec.Path}");
                }
            }
        }

        Assert.True(selfSkillsVerified >= 300, $"预期验证 >= 300 条 SelfSkills 条件组发牌非空，实际 {selfSkillsVerified}");
        Assert.True(failures.Count == 0, $"条件 TSpawnGroup 扫查失败 ({failures.Count} 条):\n" + string.Join("\n", failures.Take(20)));

        // 3) Limit > 5 拥挤事件专属/条件组优先且不被 10 格货架截断校验（Shrouded Figure Limit=7 + Street Festival Limit=8）
        {
            var runSf = new RunState
            {
                RunId = "sweep_sf",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = 6,
                Hour = 2,
                RngSeed = 42,
            };
            runSf.PlayerAttributes[EPlayerAttributeType.Gold] = 100;
            runSf.HandSocketUnlockMask = 0x3FF;
            AddOwnedItem(runSf, AncientLocketId, EInventorySection.Hand, 0, ETier.Silver);
            AddOwnedItem(runSf, LangxianId, EInventorySection.Hand, 1, ETier.Bronze);
            AddOwnedItem(runSf, OrangeJulianId, EInventorySection.Hand, 3, ETier.Silver);
            AddOwnedItem(runSf, LuxuryKiuasId, EInventorySection.Hand, 5, ETier.Silver);

            var shopSf = new ShopEngine(runSf, fx.Pool, repo);
            var runEngineSf = new RunEngine(runSf, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, repo, fx.Ghosts);
            var sfEntry = InjectChoiceEncounter(runSf, ShroudedFigureId);
            Assert.Null(runEngineSf.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(sfEntry.InstanceId) }).Error);

            var dealtSfTpls = runSf.SelectionSet.Select(id => runSf.Cards[id].TemplateId.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains(SacrificeAncientLocketStepId, dealtSfTpls);
            Assert.Contains(AbsorbMemoriesStepId, dealtSfTpls);
            Assert.Contains(SecretMeetingStepId, dealtSfTpls);
            Assert.Contains(ShowHospitalityStepId, dealtSfTpls);
        }

        {
            // Street Festival (2ee65e2b, Sequential, Limit=8)：3 个基础商人 + 5 个条件好友商人，在 Day 7 满足条件时应完整发满 8 个（不被 10 格截成 5 个）
            var streetTpl = repo.GetCard(StreetFestivalEventId)!;
            var streetGroups = streetTpl.SelectionContext!.SpawnContext!.Groups!;
            var runSt = new RunState
            {
                RunId = "sweep_street",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = 7,
                Hour = 2,
                RngSeed = 43,
            };
            runSt.PlayerAttributes[EPlayerAttributeType.Gold] = 100;
            runSt.HandSocketUnlockMask = 0x3FF;
            var stPlayer = new PlayerState("st_p", "st", "Vanessa", 500, 20, 100, 10) { Day = 7, Hour = 2 };
            int stDay = 7;
            foreach (var g in streetGroups)
            {
                if (g.Prerequisites == null) continue;
                foreach (var p in g.Prerequisites)
                    SatisfyPrerequisite(p, stPlayer, repo, ref stDay);
            }
            // 将 stPlayer 构造出的条件物品/技能同步注入 runSt
            foreach (var sk in stPlayer.Skills)
            {
                var e = new RunState.CardEntry
                {
                    InstanceId = sk.InstanceId,
                    TemplateId = Guid.Parse(sk.Template.Id),
                    Type = ECardType.Skill,
                    Tier = ETier.Bronze,
                    Owner = ECombatantId.Player,
                    CoreCard = sk,
                };
                runSt.Cards[e.InstanceId] = e;
            }
            int handCursor = 0;
            int stashCursor = 0;
            foreach (var it in stPlayer.Hand.Cards.Concat(stPlayer.Stash.Cards))
            {
                int w = Math.Clamp((int)it.EffectiveSize, 1, 3);
                if (handCursor + w <= 10)
                {
                    AddOwnedItem(runSt, it.Template.Id, EInventorySection.Hand, handCursor, ShopEngine.ParseTier(it.Template.StartingTier));
                    handCursor += w;
                }
                else if (stashCursor + w <= 10)
                {
                    AddOwnedItem(runSt, it.Template.Id, EInventorySection.Stash, stashCursor, ShopEngine.ParseTier(it.Template.StartingTier));
                    stashCursor += w;
                }
            }
            var runEngineSt = new RunEngine(runSt, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, repo, fx.Ghosts);
            var stEntry = InjectChoiceEncounter(runSt, StreetFestivalEventId);
            Assert.Null(runEngineSt.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(stEntry.InstanceId) }).Error);
            var dealtStreetTpls = runSt.SelectionSet.Select(id => runSt.Cards[id].TemplateId.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] expectedStreetSpecialSteps =
            [
                "a0f4031f-9367-497c-920d-a53fad09ca72",
                "38041aed-5d89-4a5e-90e2-486eac3f3877",
                "b266338c-33b3-44af-bf12-340c33ee8ed5",
                "add086f3-625f-49fa-847c-7ebd09bc3941",
                "34b77323-84b4-44ba-8cd5-627d26fb192c",
            ];
            foreach (string specialStepId in expectedStreetSpecialSteps)
            {
                Assert.Contains(specialStepId, dealtStreetTpls);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 3. 全库活跃事件 / 基座 / 子步骤 / PlayerEffect 全量执行扫描
    // ════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "V12_IronWall_07_AllActiveEncountersAndSteps_ExecutionScan")]
    public void AllActiveEncountersAndSteps_ExecutionScan()
    {
        var fx = RealFixture.Value;
        var repo = fx.Repo;
        var rawRecords = LoadRawCardDocsFromDb(fx.DbPath);

        int rawFourTypeCount = 0;
        var activeCards = new List<(string Id, string Type, string Name, CardTemplate Tpl)>();

        foreach (var (id, type, name, doc) in rawRecords)
        {
            if (type is not ("EventEncounter" or "PedestalEncounter" or "EncounterStep" or "PlayerEffect"))
                continue;

            rawFourTypeCount++;
            if (IsInactiveDbRecord(doc.RootElement, name))
                continue;

            var tpl = repo.GetCard(id);
            Assert.NotNull(tpl);
            activeCards.Add((id, type, name, tpl!));
        }

        // 全库 EventEncounter(561) + PedestalEncounter(41) + EncounterStep(599) + PlayerEffect(14) = 1,215
        Assert.Equal(1215, rawFourTypeCount);
        Assert.True(activeCards.Count >= 860, $"预期活跃四类卡牌 >= 860 张，实际 {activeCards.Count}");

        var unknownTypes = new List<string>();
        var uncaughtExceptions = new List<string>();

        using var scope = RuleExecutionContext.BeginUnknownRuleTypeScope((category, typeName) =>
        {
            unknownTypes.Add($"{category}:{typeName}");
        });

        var sampleBronzes = repo.AlwaysSpawnableItems
            .Where(t => t.DefaultTier != CardTier.Legendary && t.CardSize == CardSize.Small)
            .Take(3)
            .ToList();
        var sampleReagent = repo.GetCard(ChunkOfLeadId);
        var sampleSkills = repo.CardsById.Values
            .Where(t => t.Type == "Skill")
            .Take(2)
            .ToList();

        foreach (var (id, type, name, tpl) in activeCards)
        {
            try
            {
                // 1) 全量 Trigger $type 注册验证（含 TTriggerOr 递归）
                foreach (var (aid, ab) in tpl.Abilities)
                {
                    VerifyTriggerRegisteredRecursive(ab.Trigger, $"{id}({name}).Ability[{aid}]", unknownTypes);
                }

                // 2) 进入 / 发牌 / 执行扫描
                var player = new PlayerState("scan_p1", "Scanner", "Vanessa", 1000, 20, 10000, 10)
                {
                    Day = 6,
                    Hour = 2,
                    Level = 6,
                };
                player.Hand.UnlockedMask = 0x3FF;
                player.Stash.UnlockedMask = 0x3FF;

                int slot = 0;
                foreach (var it in sampleBronzes)
                {
                    player.Hand.TryPlaceOrShift(new CardInstance(it, CardTier.Bronze), slot++);
                }
                if (sampleReagent != null)
                    player.Hand.TryPlaceOrShift(new CardInstance(sampleReagent, CardTier.Bronze), slot++);
                foreach (var sk in sampleSkills)
                    player.Skills.Add(new CardInstance(sk, CardTier.Bronze));

                var selfCard = new CardInstance(tpl);
                var opp = new PlayerState("scan_opp", "Opp", "Common", 500, 20, 0, 5);
                var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, opp, new Random(1337))
                {
                    CurrentDay = 6,
                    CurrentHour = 2,
                    CurrentEncounterId = id,
                    PlayMode = "Unranked",
                    SelfCard = selfCard,
                    TriggerSourceCard = selfCard,
                    TriggerTargetCard = player.Hand.Cards.FirstOrDefault(),
                    LastAttributeChangeDelta = 5,
                    OnUnknownRuleType = (cat, tName) => unknownTypes.Add($"{cat}:{tName}@{id}"),
                };

                if (type == "EventEncounter")
                {
                    var run = new RunState
                    {
                        RunId = "scan_ev",
                        GameModeId = fx.Pool.DefaultGameModeId,
                        Hero = EHero.Vanessa,
                        Day = 6,
                        Hour = 2,
                        RngSeed = 1337,
                    };
                    run.PlayerAttributes[EPlayerAttributeType.Gold] = 10000;
                    run.HandSocketUnlockMask = 0x3FF;
                    foreach (var it in sampleBronzes)
                        AddOwnedItem(run, it.Id, EInventorySection.Hand, run.HandGrid.Cards.Count, ETier.Bronze);

                    var shop = new ShopEngine(run, fx.Pool, repo);
                    shop.DealEncounterShelf(id, isReroll: false, playerState: player);
                    OutOfCombatController.OnEncounterEntered(player, selfCard, repo, ctx.Rng, id, selfCard);
                    OutOfCombatController.OnEncounterCardsDealt(player, selfCard, [], repo, ctx.Rng, id, selfCard);
                }
                else if (type == "PedestalEncounter")
                {
                    OutOfCombatController.OnEncounterEntered(player, selfCard, repo, ctx.Rng, id, selfCard);
                    // 验证基座规则节点与 SelectionCriteria 可被安全求值
                    var targetItem = player.Hand.Cards.FirstOrDefault();
                    if (targetItem != null && tpl.SelectionCriteria != null)
                        ValueAndConditionEvaluator.EvaluateCardCondition(tpl.SelectionCriteria, targetItem, player, ctx);
                    if (tpl.Behavior?.SpawnContext != null)
                        SpawnQueryEngine.SpawnCards(tpl.Behavior.SpawnContext, ctx);
                }
                else if (type == "EncounterStep")
                {
                    var run = new RunState
                    {
                        RunId = "scan_step",
                        GameModeId = fx.Pool.DefaultGameModeId,
                        Hero = EHero.Vanessa,
                        Day = 6,
                        Hour = 2,
                        RngSeed = 1337,
                    };
                    run.PlayerAttributes[EPlayerAttributeType.Gold] = 10000;
                    run.HandSocketUnlockMask = 0x3FF;
                    foreach (var it in sampleBronzes)
                        AddOwnedItem(run, it.Id, EInventorySection.Hand, run.HandGrid.Cards.Count, ETier.Bronze);

                    var stepEntry = new RunState.CardEntry
                    {
                        InstanceId = "step_scan_" + id[..8],
                        TemplateId = Guid.Parse(id),
                        Type = ECardType.EncounterStep,
                        Tier = ShopEngine.ParseTier(tpl.StartingTier),
                        CoreCard = selfCard,
                    };
                    run.Cards[stepEntry.InstanceId] = stepEntry;
                    run.SelectionSet.Add(stepEntry.InstanceId);

                    var shop = new ShopEngine(run, fx.Pool, repo);
                    var stepEngine = new EncounterStepEngine(run, fx.Pool, repo, shop, new Random(1337));
                    stepEngine.ExecuteStep(stepEntry);
                }
                else if (type == "PlayerEffect")
                {
                    // 挂载该 PlayerEffect 并派发其全部触发器与光环
                    player.Hand.Cards.Add(selfCard);
                    OutOfCombatController.OnRunStarted(player, repo, ctx.Rng);
                    OutOfCombatController.OnFightEnded(player, "PVE", "Win", repo, ctx.Rng, opp);
                    OutOfCombatController.OnFightEnded(player, "PVP", "Lose", repo, ctx.Rng, opp);
                    OutOfCombatController.OnPlayerAttributeChanged(player, "Gold", 100, 110, repo, ctx.Rng);
                    OutOfCombatController.RecalculateOutOfCombatAuras(player, repo);
                }

                // 3) 无论前置条件或 Cost 是否阻断，逐一显式执行该卡全部 Ability.Action（递归剥离 Cost 校验并覆盖全部分支）
                foreach (var (aid, ab) in tpl.Abilities)
                {
                    if (ab.Action == null) continue;
                    ExecuteActionTreeWithoutCostGate(ab.Action, selfCard, ctx);
                }
                if (tpl.Auras.Count > 0)
                {
                    AuraEngine.RecalculateAuras(player, opp, RuleScope.OutOfCombat, 6, 2, repo);
                }
            }
            catch (Exception ex)
            {
                uncaughtExceptions.Add($"{type} {id} ({name}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(unknownTypes.Count == 0, $"发现未知规则 $type ({unknownTypes.Count}):\n" + string.Join("\n", unknownTypes.Distinct().Take(30)));
        Assert.True(uncaughtExceptions.Count == 0, $"全量扫描出现未捕获异常 ({uncaughtExceptions.Count}):\n" + string.Join("\n", uncaughtExceptions.Take(30)));
    }

    // ── DB 读取与前置条件自动构造辅助方法 ────────────────────────────────────

    private static List<(string Id, string Type, string Name, JsonDocument Doc)> LoadRawCardDocsFromDb(string dbPath)
    {
        var list = new List<(string, string, string, JsonDocument)>();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Data FROM cards";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            byte[] blob = (byte[])reader.GetValue(1);
            string json = Encoding.UTF8.GetString(blob);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string type = root.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
            string name = root.TryGetProperty("InternalName", out var n) ? n.GetString() ?? "" : "";
            list.Add((id, type, name, doc));
        }
        return list;
    }

    private static bool IsInactiveDbRecord(JsonElement root, string internalName)
    {
        string elig = root.TryGetProperty("SpawningEligibility", out var e) ? e.GetString() ?? "" : "";
        if (string.Equals(elig, "Never", StringComparison.OrdinalIgnoreCase))
            return true;

        string title = "";
        if (root.TryGetProperty("Localization", out var loc) && loc.ValueKind == JsonValueKind.Object &&
            loc.TryGetProperty("Title", out var t) && t.ValueKind == JsonValueKind.Object &&
            t.TryGetProperty("Text", out var txt))
        {
            title = txt.GetString() ?? "";
        }

        bool HasMarker(string s) =>
            s.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Tutorial", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase);

        return HasMarker(internalName) || HasMarker(title);
    }

    private static void CollectConditionalGroups(
        RuleNode spawnCtx,
        string path,
        List<(string Path, RuleNode SpawnCtx, RuleNode Group)> output)
    {
        if (spawnCtx.Groups == null) return;
        for (int i = 0; i < spawnCtx.Groups.Count; i++)
        {
            var g = spawnCtx.Groups[i];
            if (g.Prerequisites is { Count: > 0 })
                output.Add(($"{path}.Groups[{i}]", spawnCtx, g));
        }
    }

    private static void CollectConditionalGroupsFromNode(
        RuleNode node,
        string path,
        List<(string Path, RuleNode SpawnCtx, RuleNode Group)> output)
    {
        if (node.SpawnContext != null)
            CollectConditionalGroups(node.SpawnContext, $"{path}.SpawnContext", output);
        if (node.Actions != null)
        {
            for (int i = 0; i < node.Actions.Count; i++)
                CollectConditionalGroupsFromNode(node.Actions[i], $"{path}.Actions[{i}]", output);
        }
    }

    private static void SatisfyPrerequisite(
        RuleNode prereq,
        PlayerState player,
        GameDataRepository repo,
        ref int desiredDay)
    {
        switch (prereq.Type)
        {
            case "TPrerequisiteRun":
            {
                var cond = prereq.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (cond == null) return;
                SatisfyRunCondition(cond, player, ref desiredDay);
                break;
            }
            case "TPrerequisitePlayer":
            {
                var cond = prereq.Subject?.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (cond == null) return;
                SatisfyPlayerCondition(cond, player);
                break;
            }
            case "TPrerequisiteCardCount":
            {
                SatisfyCardCountPrerequisite(prereq, player, repo);
                break;
            }
        }
    }

    private static bool GroupHasEligibleDbCandidates(RuleNode group, RuleNode spawnCtx, string playerHero, GameDataRepository repo)
    {
        if (group.Filters == null || group.Filters.Count == 0) return false;
        bool ignoreHero = (group.Behaviors?.Any(b => b.Type == "TSpawnBehaviorIgnoreHero" && b.IgnoreHero == true) ?? false)
                       || (spawnCtx.Behaviors?.Any(b => b.Type == "TSpawnBehaviorIgnoreHero" && b.IgnoreHero == true) ?? false);
        foreach (var f in group.Filters)
        {
            if (f.Type == "TSpawnFilterIdList" && f.Ids != null)
            {
                if (f.Ids.Any(id => repo.GetCard(id) != null))
                    return true;
            }
            else if (f.Type == "TSpawnFilterQuery")
            {
                var c = f.GetSingleConstraint(GameDatabaseLoader.JsonOptions);
                foreach (var tpl in repo.CardsById.Values)
                {
                    if (!string.Equals(tpl.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ignoreHero && tpl.Heroes.Count > 0 &&
                        !tpl.Heroes.Contains(playerHero, StringComparer.OrdinalIgnoreCase) &&
                        !tpl.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (ValueAndConditionEvaluator.EvaluateTemplateConstraint(c, tpl, playerHero))
                        return true;
                }
            }
        }
        return false;
    }

    private static void SatisfyRunCondition(RuleNode cond, PlayerState player, ref int desiredDay)
    {
        switch (cond.Type)
        {
            case "TRunConditionalCurrentDay":
            {
                int targetDay = cond.CurrentDay ?? 1;
                desiredDay = cond.ComparisonOperator switch
                {
                    "GreaterThan" => targetDay + 1,
                    "LessThan" => Math.Max(1, targetDay - 1),
                    _ => Math.Max(1, targetDay),
                };
                player.Day = desiredDay;
                break;
            }
            case "TRunConditionalPlayerHero":
            {
                if (cond.Heroes is { Count: > 0 })
                {
                    if (string.Equals(cond.Operator, "None", StringComparison.OrdinalIgnoreCase))
                    {
                        player.Hero = cond.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase) ? "Pygmalien" : "Vanessa";
                    }
                    else
                    {
                        player.Hero = cond.Heroes[0];
                    }
                }
                break;
            }
            case "TRunConditionalMusicNoteHasValidSocket":
            {
                // 若要求存在有效音符插座（IsNot=false），确保玩家手牌解锁全槽且 SocketEffects 留有合法音符空间（清空其他占格音符）
                player.Hand.UnlockedMask = 0x3FF;
                if (cond.IsNot != true)
                {
                    player.SocketEffects.Clear();
                }
                else
                {
                    // 占满全部槽位使其无合法音符空位
                    for (int s = 0; s < 10; s++)
                    {
                        bool occupied = player.SocketEffects.Any(se =>
                            !se.IsDestroyed && se.StartSlot >= 0 && s >= se.StartSlot && s < se.StartSlot + se.SlotWidth);
                        if (!occupied)
                        {
                            var dummy = new CardTemplate { Id = "dummy_se_" + s, Type = "SocketEffect", Size = "Small", StartingTier = "Bronze" };
                            var ci = new CardInstance(dummy) { StartSlot = s };
                            player.SocketEffects.Add(ci);
                        }
                    }
                }
                break;
            }
            case "TRunConditionalAnd":
            {
                foreach (var sub in cond.GetConditionList(GameDatabaseLoader.JsonOptions))
                    SatisfyRunCondition(sub, player, ref desiredDay);
                break;
            }
        }
    }

    private static void SatisfyPlayerCondition(RuleNode cond, PlayerState player)
    {
        if (cond.Type == "TPlayerConditionalAttribute")
        {
            string attr = cond.Attribute ?? "Custom_0";
            string op = cond.ComparisonOperator ?? cond.Comparison ?? "Equal";
            var cmpNode = cond.GetComparisonValueNode(GameDatabaseLoader.JsonOptions);
            double rhs = cond.GetFixedComparisonValue() ?? 0;

            if (cmpNode != null && string.Equals(cmpNode.Type, "TReferenceValuePlayerAttribute", StringComparison.Ordinal))
            {
                string rhsAttr = cmpNode.AttributeType ?? "Custom_1";
                double rhsCur = player.Attributes.GetValueOrDefault(rhsAttr, 0);
                player.Attributes[attr] = op switch
                {
                    "GreaterThan" => rhsCur + 5,
                    "GreaterThanOrEqual" => rhsCur,
                    "LessThan" => Math.Max(0, rhsCur - 1),
                    _ => rhsCur,
                };
                if (op == "LessThan" && rhsCur <= 0)
                {
                    player.Attributes[rhsAttr] = 5;
                    player.Attributes[attr] = 0;
                }
                return;
            }

            player.Attributes[attr] = op switch
            {
                "GreaterThan" => rhs + 2,
                "GreaterThanOrEqual" => rhs,
                "LessThan" => Math.Max(0, rhs - 1),
                "LessThanOrEqual" => rhs,
                "NotEqual" => rhs + 1,
                _ => rhs,
            };
        }
    }

    private static void SatisfyCardCountPrerequisite(RuleNode prereq, PlayerState player, GameDataRepository repo)
    {
        string section = prereq.Subject?.TargetSection ?? "AbsolutePlayerHandAndStash";
        string comp = prereq.Comparison ?? "GreaterThanOrEqual";
        int amount = (int)Math.Round(prereq.Amount ?? 1);
        var cond = prereq.Subject?.GetSingleCondition(GameDatabaseLoader.JsonOptions);

        int needCount = comp switch
        {
            "Equal" => amount,
            "GreaterThan" => amount + 1,
            "GreaterThanOrEqual" => amount,
            "LessThan" => 0,
            "LessThanOrEqual" => 0,
            _ => amount,
        };

        if (needCount <= 0)
        {
            // 若要求 0 张（如不含某技能 / 不含 Package），确保目标区段中不存在匹配该条件的卡即可
            if (cond != null)
            {
                RemoveMatchingCardsFromSection(player, section, cond, repo);
            }
            return;
        }

        for (int i = 0; i < needCount; i++)
        {
            var card = CreateCardMatchingCondition(cond, section, repo, i);
            PlaceCardInSection(player, section, card);
        }
    }

    private static void RemoveMatchingCardsFromSection(PlayerState player, string section, RuleNode cond, GameDataRepository repo)
    {
        var dummyCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(1));
        bool Match(CardInstance c) => ValueAndConditionEvaluator.EvaluateCardCondition(cond, c, player, dummyCtx);
        if (section is "SelfSkills" or "AbsolutePlayerSkills")
            player.Skills.RemoveAll(Match);
        else if (section == "AbsolutePlayerSocketEffects" || section == "SelfSocketEffects")
            player.SocketEffects.RemoveAll(Match);
        else
        {
            foreach (var c in player.Hand.Cards.Where(Match).ToList()) player.Hand.Remove(c);
            foreach (var c in player.Stash.Cards.Where(Match).ToList()) player.Stash.Remove(c);
        }
    }

    private static CardInstance CreateCardMatchingCondition(RuleNode? cond, string section, GameDataRepository repo, int salt)
    {
        bool wantSkill = section is "SelfSkills" or "AbsolutePlayerSkills";
        bool wantSocket = section is "AbsolutePlayerSocketEffects" or "SelfSocketEffects";

        CardTemplate? pickedTpl = null;
        CardTier pickedTier = CardTier.Bronze;

        if (cond != null)
        {
            pickedTpl = FindTemplateForCondition(cond, wantSkill, wantSocket, repo, out pickedTier);
        }

        if (pickedTpl == null)
        {
            if (wantSkill)
                pickedTpl = repo.CardsById.Values.First(t => t.Type == "Skill");
            else if (wantSocket)
                pickedTpl = repo.CardsById.Values.First(t => t.Type is "SocketEffect" or "MusicNoteSocketEffect");
            else
                pickedTpl = repo.AlwaysSpawnableItems.FirstOrDefault(t => t.CardSize == CardSize.Small)
                            ?? repo.CardsById.Values.First(t => t.Type == "Item");
        }

        return new CardInstance(pickedTpl, pickedTier, null, $"sweep_{salt}_{Guid.NewGuid():N}"[..16]);
    }

    private static CardTemplate? FindTemplateForCondition(
        RuleNode cond,
        bool wantSkill,
        bool wantSocket,
        GameDataRepository repo,
        out CardTier tier)
    {
        tier = CardTier.Bronze;
        switch (cond.Type)
        {
            case "TCardConditionalId":
                if (!string.IsNullOrWhiteSpace(cond.Id) && repo.GetCard(cond.Id) is { } idTpl)
                {
                    tier = TierHelper.ParseTier(idTpl.StartingTier);
                    return idTpl;
                }
                break;

            case "TCardConditionalOr":
            case "TCardConditionalAnd":
            {
                var subs = cond.GetConditionList(GameDatabaseLoader.JsonOptions);
                if (cond.Type == "TCardConditionalOr" && subs.Count > 0)
                    return FindTemplateForCondition(subs[0], wantSkill, wantSocket, repo, out tier);

                var dummyPlayer = new PlayerState("match", "m", "Vanessa", 100, 20, 0, 10);
                var dummyCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, dummyPlayer, null, new Random(1));
                foreach (var tpl in repo.CardsById.Values)
                {
                    if (wantSkill && tpl.Type != "Skill") continue;
                    if (!wantSkill && !wantSocket && tpl.Type != "Item") continue;
                    var ci = new CardInstance(tpl, TierHelper.ParseTier(tpl.StartingTier));
                    if (ValueAndConditionEvaluator.EvaluateCardCondition(cond, ci, dummyPlayer, dummyCtx))
                    {
                        tier = ci.Tier;
                        return tpl;
                    }
                }
                break;
            }

            case "TCardConditionalTag":
                if (cond.Tags is { Count: > 0 })
                {
                    var tpl = repo.CardsById.Values.FirstOrDefault(t =>
                        (!wantSkill || t.Type == "Skill") &&
                        (wantSkill || wantSocket || t.Type == "Item") &&
                        cond.Tags.Any(tag => t.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
                    if (tpl != null)
                    {
                        tier = TierHelper.ParseTier(tpl.StartingTier);
                        return tpl;
                    }
                }
                break;

            case "TCardConditionalHiddenTag":
                if (cond.Tags is { Count: > 0 })
                {
                    var tpl = repo.CardsById.Values.FirstOrDefault(t =>
                        (!wantSkill || t.Type == "Skill") &&
                        (wantSkill || wantSocket || t.Type == "Item") &&
                        cond.Tags.Any(tag => t.HiddenTags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
                    if (tpl != null)
                    {
                        tier = TierHelper.ParseTier(tpl.StartingTier);
                        return tpl;
                    }
                }
                break;

            case "TCardConditionalTier":
                if (cond.Tiers is { Count: > 0 })
                {
                    string targetTierStr = cond.IsNot == true
                        ? (cond.Tiers.Contains("Bronze", StringComparer.OrdinalIgnoreCase) ? "Silver" : "Bronze")
                        : cond.Tiers[0];
                    tier = TierHelper.ParseTier(targetTierStr);
                    return wantSkill
                        ? repo.CardsById.Values.FirstOrDefault(t => t.Type == "Skill")
                        : repo.AlwaysSpawnableItems.FirstOrDefault(t => t.CardSize == CardSize.Small)
                          ?? repo.CardsById.Values.First(t => t.Type == "Item");
                }
                break;
        }

        return null;
    }

    private static void PlaceCardInSection(PlayerState player, string section, CardInstance card)
    {
        if (section is "SelfSkills" or "AbsolutePlayerSkills")
        {
            player.Skills.Add(card);
        }
        else if (section is "AbsolutePlayerSocketEffects" or "SelfSocketEffects")
        {
            card.StartSlot = Math.Clamp(player.SocketEffects.Count, 0, 9);
            player.SocketEffects.Add(card);
        }
        else if (section == "AbsolutePlayerHand")
        {
            if (!player.Hand.TryAddAuto(card))
                player.Hand.Cards.Add(card);
        }
        else
        {
            if (!player.Hand.TryAddAuto(card) && !player.Stash.TryAddAuto(card))
                player.Stash.Cards.Add(card);
        }
    }

    private static void VerifyTriggerRegisteredRecursive(RuleNode? trigger, string contextTag, List<string> unknownTypes)
    {
        if (trigger == null) return;
        if (string.Equals(trigger.Type, "TTriggerOr", StringComparison.Ordinal))
        {
            if (!AbilityExecutor.IsKnownTriggerType(trigger.Type))
                unknownTypes.Add($"TTrigger:{trigger.Type}@{contextTag}");
            if (trigger.Triggers != null)
            {
                foreach (var sub in trigger.Triggers)
                    VerifyTriggerRegisteredRecursive(sub, contextTag, unknownTypes);
            }
            return;
        }
        if (!AbilityExecutor.IsKnownTriggerType(trigger.Type))
            unknownTypes.Add($"TTrigger:{trigger.Type}@{contextTag}");
    }

    private static void ExecuteActionTreeWithoutCostGate(RuleNode action, CardInstance sourceCard, RuleExecutionContext ctx)
    {
        if (string.Equals(action.Type, "TActionAnd", StringComparison.Ordinal) && action.Actions != null)
        {
            foreach (var sub in action.Actions)
                ExecuteActionTreeWithoutCostGate(sub, sourceCard, ctx);
            return;
        }

        // 若动作附带 Cost，确保执行时临时剔除 Cost 或靠足够金币绕过阻断（ExecuteAction 本身已对 action.Cost 做判别，10000 金币均可通过）
        AbilityExecutor.ExecuteAction(action, sourceCard, ctx);
    }
}
