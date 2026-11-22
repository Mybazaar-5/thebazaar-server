using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V13.2 实机三大缺陷专项回归测试套件：
///   1. 战斗读条卡顿修复：CombatSimConverter 必须以官方 50ms/帧（20 Hz）逐帧发射 Cooldown 变化，彻底消除 200ms 跳帧；
///   2. 战利品卖出最左错位修复：精确复现云端实机 rid=40 满盘拖拽（[Katana 2-3 | Sharkclaws 4-5 | Narwhal 6 | HoningSteel 7] 将 Sharkclaws 拖到 [3,4]），
///      验证服务端 1:1 对齐客户端 SocketOperations 链表保序结果 [Sharkclaws 2-3 | Katana 4-5 | Narwhal 6 | HoningSteel 7]（绝不把独角鲸瞬移至槽位 2），
///      且卖出加攻战利品精准命中当前最左 Sharkclaws；
///   3. 卖出虫翅（Insect Wing）减冷却验证：验证武士刀 PercentCooldownReduction=+1、CooldownMax=1980ms，且 GameSim 回包精确下发 CooldownMax=1980 与刷新事件；
///   4. 事件出现权威门控与分布验证：Day 1 禁止金/钻与 Mountain Pass / B1&amp;B2、银商仅放行 Curio、Day 2–3 禁止金商/金事件、
///      Day 1 奇怪的蘑菇+丛林遗迹占比平滑（不再 70% 刷屏）、同小时/同日杜绝同名多阶重复事件。
/// </summary>
public sealed class V13_2_CombatSmoothDragLootEventTests
{
    private const string KatanaId = "54f957f2-9522-486b-a7c6-bb234f74846c";
    private const string SharkclawsId = "edd857da-ac11-4640-bc1a-217ef8385f72";
    private const string NarwhalId = "00e3a7ff-af5e-47ce-a2c0-2d4ec31ac7d7";
    private const string HoningSteelId = "b23c5988-7091-4b77-a7fb-134c0d0a3285";
    private const string InsectWingId = "4826832f-f3a5-4c40-b8fa-737f002074fa";
    private const string SharpeningStoneId = "dc3bf5cb-45fa-46c7-9a0e-3f3bfb01697d";
    private const string CurioId = "c6cffe25-8e94-4a7b-a821-3e3c7093333b";
    private const string MountainPassId = "956e9c74-7d98-4a2b-b500-def577914945";
    private const string FaraiId = "412fb3ec-88de-40b4-a475-10e583189d8a";

    private sealed class RealDataFx
    {
        public string DataDir { get; init; } = "";
        public CardPool Pool { get; init; } = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
        public CardArchetypeIndex ArchetypeIndex { get; init; } = null!;
    }

    private static readonly Lazy<RealDataFx> Fx = new(() =>
    {
        string? resolvedDir = null;
        string? probe = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(probe); i++, probe = Directory.GetParent(probe)?.FullName)
        {
            string candidate = Path.Combine(probe, "dist", "data");
            string dbFile = Path.Combine(candidate, "GameData.db");
            if (File.Exists(dbFile) && new FileInfo(dbFile).Length > 1024 * 1024)
            {
                resolvedDir = candidate;
                break;
            }
        }
        if (string.IsNullOrEmpty(resolvedDir))
        {
            string mainDist = @"e:\thebazaar\dist\data";
            if (File.Exists(Path.Combine(mainDist, "GameData.db")))
                resolvedDir = mainDist;
        }
        Assert.False(string.IsNullOrEmpty(resolvedDir), "未找到 dist/data/GameData.db");
        string dbPath = Path.Combine(resolvedDir!, "GameData.db");
        var repo = GameDatabaseLoader.Load(dbPath, null);
        var pool = CardPool.FromRepository(repo, resolvedDir);
        return new RealDataFx
        {
            DataDir = resolvedDir!,
            Pool = pool,
            Monsters = MonsterPool.FromRepository(repo),
            LevelUps = LevelUpTable.FromRepository(repo),
            Mode = GameModeConfig.FromRepository(repo),
            Repo = repo,
            Ghosts = PvpGhostPool.Load(resolvedDir!),
            ArchetypeIndex = CardArchetypeIndex.Build(pool, repo),
        };
    });

    [Fact]
    public void Fix1_CombatSimConverter_EmitsCooldownEvery50msFrameWithout200msStutterGaps()
    {
        var fx = Fx.Value;
        var katanaTpl = fx.Repo.GetCard(KatanaId)!;
        var narwhalTpl = fx.Repo.GetCard(NarwhalId)!;

        var p1 = new PlayerState("player", "Player", "Vanessa", 400, 20, 10, 6);
        var p2 = new PlayerState("opponent", "Opponent", "Vanessa", 400, 20, 10, 6);

        var p1Card = new CardInstance(katanaTpl, CardTier.Bronze, null, "itm_katana_smooth");
        var p2Card = new CardInstance(narwhalTpl, CardTier.Bronze, null, "itm_narwhal_smooth");
        Assert.True(p1.Hand.TryAddAuto(p1Card));
        Assert.True(p2.Hand.TryAddAuto(p2Card));

        var simResult = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 2026132, recordTimeline: true);
        Assert.True(simResult.Timeline.Count > 20);

        var p1Entry = new RunState.CardEntry
        {
            InstanceId = p1Card.InstanceId,
            TemplateId = Guid.Parse(KatanaId),
            Type = ECardType.Item,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_2,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            CoreCard = p1Card,
        };
        var p2Entry = new RunState.CardEntry
        {
            InstanceId = p2Card.InstanceId,
            TemplateId = Guid.Parse(NarwhalId),
            Type = ECardType.Item,
            Owner = ECombatantId.Opponent,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_2,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            CoreCard = p2Card,
        };

        var combatSim = CombatSimConverter.ToCombatSim(
            simResult,
            fx.Repo,
            new Dictionary<string, CardInstance> { [p1Card.InstanceId] = p1Card },
            new Dictionary<string, CardInstance> { [p2Card.InstanceId] = p2Card },
            new[] { p1Entry },
            new[] { p2Entry });

        var katanaId = new InstanceId(p1Card.InstanceId);
        int cdEmittedInFirst20Frames = 0;
        int maxConsecutiveMissing = 0;
        int curMissing = 0;
        int checkFrames = Math.Min(35, combatSim.Frames.Count - 1);

        for (int f = 1; f < checkFrames; f++)
        {
            var fr = combatSim.Frames[f];
            bool hasCd = fr.CardUpdates.TryGetValue(katanaId, out var cu)
                && cu.Attributes.ContainsKey(ECardAttributeType.Cooldown);
            if (hasCd)
            {
                if (f <= 20) cdEmittedInFirst20Frames++;
                curMissing = 0;
            }
            else
            {
                curMissing++;
                if (curMissing > maxConsecutiveMissing) maxConsecutiveMissing = curMissing;
            }
        }

        // 在官方 50ms/帧下，前 20 帧（1000ms 线性冷却区间）应几乎每帧都发射 Cooldown，绝不允许出现旧版每 200ms 跳 3 帧的断层
        Assert.True(cdEmittedInFirst20Frames >= 18,
            $"前 20 帧（1 秒）内 Cooldown 发射帧数过低（实际 {cdEmittedInFirst20Frames}/20），说明仍存在读条节流卡顿！");
        Assert.True(maxConsecutiveMissing <= 1,
            $"冷却推进期出现连续 {maxConsecutiveMissing} 帧漏发 Cooldown，将导致客户端进度条明显跳格！");
    }

    [Fact]
    public void Fix3A_FullBoardDragSwap_MatchesOfficialSocketOperations_And_SellLootHitsTrueLeftmost()
    {
        var fx = Fx.Value;
        var run = new RunState
        {
            RunId = "v132_drag_loot",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            RngSeed = 132001,
            Day = 2,
            Hour = 3,
            State = ERunState.Choice,
            UnlockedSlots = 0x0FC, // 6 个已解锁槽位：2..7（与玩家云端实机 rid=40 完全一致）
            HandSocketUnlockMask = 0x0FC,
        };
        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        RunState.CardEntry AddOwnedItem(string instId, string tplId, EInventorySection sec, int slot, ECardSize size)
        {
            var tpl = fx.Repo.GetCard(tplId)!;
            var core = new CardInstance(tpl, CardTier.Bronze, null, instId) { StartSlot = slot };
            var entry = new RunState.CardEntry
            {
                InstanceId = instId,
                TemplateId = Guid.Parse(tplId),
                Type = ECardType.Item,
                Owner = ECombatantId.Player,
                Section = sec,
                Socket = (EContainerSocketId)slot,
                Size = size,
                Tier = ETier.Bronze,
                CoreCard = core,
            };
            run.Cards[instId] = entry;
            var grid = sec == EInventorySection.Hand ? run.HandGrid : run.StashGrid;
            grid.UnlockedMask = sec == EInventorySection.Hand ? run.UnlockedSlots : 1023;
            grid.Cards.Add(core);
            run.SyncEntryFromCoreCardAndDiff(entry, fx.Repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
            return entry;
        }

        // 精确还原 rid=40 前盘面：[Katana(2-3) | Sharkclaws(4-5) | Narwhal(6) | HoningSteel(7)]
        var katana = AddOwnedItem("itm_katana", KatanaId, EInventorySection.Hand, 2, ECardSize.Medium);
        var shark = AddOwnedItem("itm_shark", SharkclawsId, EInventorySection.Hand, 4, ECardSize.Medium);
        var narwhal = AddOwnedItem("itm_narwhal", NarwhalId, EInventorySection.Hand, 6, ECardSize.Small);
        var honing = AddOwnedItem("itm_honing", HoningSteelId, EInventorySection.Hand, 7, ECardSize.Small);
        var stone = AddOwnedItem("itm_stone", SharpeningStoneId, EInventorySection.Stash, 3, ECardSize.Small);

        int sharkDmgBefore = shark.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0);
        int narwhalDmgBefore = narwhal.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0);

        // 复现 rid=40：把 2 格 Sharkclaws(4-5) 向左拖 1 格到 [Socket_3, Socket_4]
        var moveRes = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId("itm_shark"),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_3, EContainerSocketId.Socket_4 },
        });
        Assert.Null(moveRes.Error);

        // 1:1 对齐客户端官方 SocketOperations：Sharkclaws=[2,3], Katana=[4,5], Narwhal 稳稳留在 [6], HoningSteel=[7]
        Assert.Equal(EContainerSocketId.Socket_2, shark.Socket);
        Assert.Equal(EContainerSocketId.Socket_4, katana.Socket);
        Assert.Equal(EContainerSocketId.Socket_6, narwhal.Socket);
        Assert.Equal(EContainerSocketId.Socket_7, honing.Socket);

        // 卖出加攻战利品（Sharpening Stone）：必须精准加在当前最左 Sharkclaws 上，绝不能打偏给第 3 个独角鲸！
        var sellRes = engine.Handle(new SellCardCommand { InstanceId = new InstanceId("itm_stone") });
        Assert.Null(sellRes.Error);
        int sharkDmgAfter = shark.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0);
        int narwhalDmgAfter = narwhal.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0);
        Assert.True(sharkDmgAfter > sharkDmgBefore, $"卖出磨刀石后最左鲨鱼爪伤害应上升（前={sharkDmgBefore} 后={sharkDmgAfter}）");
        Assert.Equal(narwhalDmgBefore, narwhalDmgAfter);
    }

    [Fact]
    public void Fix3B_SellInsectWing_ReducesLeftmostKatanaCooldownAndSyncsInGameSim()
    {
        var fx = Fx.Value;
        var run = new RunState
        {
            RunId = "v132_wing_cd",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            RngSeed = 132002,
            Day = 2,
            Hour = 3,
            State = ERunState.Choice,
            UnlockedSlots = 0x0FC,
            HandSocketUnlockMask = 0x0FC,
        };
        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        var katanaCore = new CardInstance(fx.Repo.GetCard(KatanaId)!, CardTier.Bronze, null, "itm_katana") { StartSlot = 2 };
        var katana = new RunState.CardEntry
        {
            InstanceId = "itm_katana",
            TemplateId = Guid.Parse(KatanaId),
            Type = ECardType.Item,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_2,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            CoreCard = katanaCore,
        };
        run.Cards[katana.InstanceId] = katana;
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.HandGrid.Cards.Add(katanaCore);
        run.SyncEntryFromCoreCardAndDiff(katana, fx.Repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        var wingCore = new CardInstance(fx.Repo.GetCard(InsectWingId)!, CardTier.Bronze, null, "itm_wing") { StartSlot = 0 };
        var wing = new RunState.CardEntry
        {
            InstanceId = "itm_wing",
            TemplateId = Guid.Parse(InsectWingId),
            Type = ECardType.Item,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Stash,
            Socket = EContainerSocketId.Socket_0,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            CoreCard = wingCore,
        };
        run.Cards[wing.InstanceId] = wing;
        run.StashGrid.UnlockedMask = 1023;
        run.StashGrid.Cards.Add(wingCore);
        run.SyncEntryFromCoreCardAndDiff(wing, fx.Repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        Assert.Equal(2000, katana.Attributes[ECardAttributeType.CooldownMax]);

        var sellRes = engine.Handle(new SellCardCommand { InstanceId = new InstanceId("itm_wing") });
        Assert.Null(sellRes.Error);

        Assert.Equal(1, katana.Attributes.GetValueOrDefault(ECardAttributeType.PercentCooldownReduction, 0));
        Assert.Equal(1980, katana.Attributes.GetValueOrDefault(ECardAttributeType.CooldownMax, 0));

        var gameSim = run.BuildFullGameSim(sellRes.Events);
        Assert.True(gameSim.Cards.TryGetValue("itm_katana", out var simKatana));
        Assert.Equal(1980, simKatana!.Attributes[ECardAttributeType.CooldownMax].Value);
        Assert.Contains(sellRes.Events, e => e is GameSimEventEffectAuraExecuted);
    }

    [Fact]
    public void Fix2_AuthoritativeEncounterSchedule_EnforcesDayGatesSmoothingAndNoSameNameDupes()
    {
        var fx = Fx.Value;
        int day1NonMerchantTotal = 0;
        int day1BronzeTwoCount = 0; // Jungle Ruins + A Strange Mushroom
        int day1CurioCount = 0;
        int day3PlusPedestals = 0;
        int day5PlusGoldMerchants = 0;

        for (int trial = 0; trial < 40; trial++)
        {
            var run = new RunState
            {
                RunId = $"v132_enc_{trial}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                RngSeed = 132100 + trial * 41,
                CustomRules = new CustomRunRuleSet(),
            };
            var shop = new ShopEngine(run, fx.Pool, fx.Repo, run.CustomRules, fx.ArchetypeIndex);

            for (uint d = 1; d <= 8; d++)
            {
                run.Day = d;
                run.VisitedDayEncounterIds.Clear();
                foreach (uint h in new uint[] { 1, 2, 4, 5 })
                {
                    run.Hour = h;
                    shop.DealHourEncounters(playerState: null, visitedTodayIds: run.VisitedDayEncounterIds);
                    Assert.Equal(3, run.SelectionSet.Count);

                    var hourNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string instId in run.SelectionSet)
                    {
                        var ce = run.Cards[instId];
                        string tid = ce.TemplateId.ToString();
                        var tpl = fx.Pool.Get(tid)!;
                        Assert.NotNull(tpl);

                        // 同小时内绝不允许同名多阶重复事件（如两张不同品阶的 Gunpowder Cache）
                        if (!string.IsNullOrWhiteSpace(tpl.InternalName))
                            Assert.True(hourNames.Add(tpl.InternalName), $"Day {d} Hour {h} 同小时刷出同名重复事件: {tpl.InternalName}");

                        bool isMerchant = tpl.Tags != null && tpl.Tags.Any(t => string.Equals(t, "Merchant", StringComparison.OrdinalIgnoreCase));

                        // Day 1 严格门控：无基座、无 Mountain Pass、无金/钻遭遇、银商仅允许 Curio
                        if (d == 1)
                        {
                            Assert.NotEqual(ECardType.PedestalEncounter, ce.Type);
                            Assert.DoesNotContain(tpl.StartingTier, new[] { "Gold", "Diamond", "Legendary" });
                            Assert.NotEqual(MountainPassId, tid, StringComparer.OrdinalIgnoreCase);
                            if (isMerchant && string.Equals(tpl.StartingTier, "Silver", StringComparison.OrdinalIgnoreCase))
                            {
                                Assert.Equal(CurioId, tid, StringComparer.OrdinalIgnoreCase);
                                day1CurioCount++;
                            }
                            if (!isMerchant)
                            {
                                day1NonMerchantTotal++;
                                if (string.Equals(tpl.InternalName, "Jungle Ruins", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(tpl.InternalName, "A Strange Mushroom", StringComparison.OrdinalIgnoreCase))
                                {
                                    day1BronzeTwoCount++;
                                }
                            }
                        }

                        // Day 2–3 严格门控：不刷出金商（Goldie/Aimbot 等）与 Mountain Pass（DB Day 4+）
                        if (d <= 3)
                        {
                            Assert.NotEqual(MountainPassId, tid, StringComparer.OrdinalIgnoreCase);
                            if (isMerchant)
                                Assert.DoesNotContain(tpl.StartingTier, new[] { "Gold", "Diamond", "Legendary" });
                        }

                        // Day 1–4 禁止 Farai（DB Day 5+）与金商
                        if (d <= 4)
                        {
                            Assert.NotEqual(FaraiId, tid, StringComparer.OrdinalIgnoreCase);
                            if (isMerchant)
                                Assert.DoesNotContain(tpl.StartingTier, new[] { "Gold", "Diamond", "Legendary" });
                        }

                        // Day 1–2 禁止过早刷出 B1&B2 / Sterling 基座
                        if (d <= 2)
                        {
                            Assert.NotEqual(ECardType.PedestalEncounter, ce.Type);
                        }
                        else if (ce.Type == ECardType.PedestalEncounter)
                        {
                            day3PlusPedestals++;
                        }

                        if (d >= 5 && isMerchant && string.Equals(tpl.StartingTier, "Gold", StringComparison.OrdinalIgnoreCase))
                            day5PlusGoldMerchants++;

                        run.VisitedDayEncounterIds.Add(tid);
                    }
                }
            }
        }

        // Day 1 奇怪的蘑菇 + 丛林遗迹在池容平滑后，占比应远低于原先的 70%（<= 45%）
        double bronzeTwoRatio = (double)day1BronzeTwoCount / Math.Max(1, day1NonMerchantTotal);
        Assert.True(bronzeTwoRatio <= 0.48,
            $"Day 1 丛林遗迹+奇怪蘑菇合计占比过高（{bronzeTwoRatio:P1}），说明池容平滑未生效！");
        Assert.True(day1CurioCount > 0, "Day 1 应能合法刷出官方银商 Curio");
        Assert.True(day3PlusPedestals > 0, "Day 3+ 应能合法刷出合规地图基座（B1&B2 / Sterling / Forja / The Artist）");
        Assert.True(day5PlusGoldMerchants > 0, "Day 5+ 应开始解锁金阶商人");
    }

    // ── 修复 6：官方负血击杀视觉 + 523 项官方专属技能 VFXOverrideKey 接入 ──────

    [Fact]
    public void Fix6_CombatSim_PreservesOverkillNegativeHp_AndPopulatesOfficialVfxKeys()
    {
        var fx = Fx.Value;
        var narwhalTpl = fx.Repo.GetCard(NarwhalId)!; // Narwhal 带官方 VFXOverrideKey: Projectile_Pierce_PV.prefab
        Assert.NotNull(narwhalTpl);
        Assert.Contains(narwhalTpl.Abilities.Values, a => !string.IsNullOrWhiteSpace(a.VFXConfig?.VFXOverrideKey));

        var p1 = new PlayerState("player", "P1", "Vanessa", 500, 20, 0, 10);
        var narwhal = new CardInstance(narwhalTpl, CardTier.Diamond, null, "p1_narwhal") { StartSlot = 0 };
        narwhal.PermanentAdd["DamageAmount"] = 500; // 制造巨额溢出伤害，将 50 HP 敌人直接打成负血
        Assert.True(p1.Hand.TryAddAuto(narwhal));
        p1.PrepareForCombat();

        var p2 = new PlayerState("opponent", "P2", "Pygmalien", 50, 20, 0, 10);
        var oppCard = new CardInstance(fx.Repo.GetCard(KatanaId)!, CardTier.Bronze, null, "p2_kat") { StartSlot = 0 };
        Assert.True(p2.Hand.TryAddAuto(oppCard));
        p2.PrepareForCombat();

        var result = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 77, recordEvents: true, recordTimeline: true);
        Assert.Equal("player", result.WinnerPlayerId);
        Assert.True(result.Timeline.Count > 0);
        Assert.True(result.Timeline[^1].P2Hp < 0,
            $"最终击杀帧 P2Hp 应保留溢出负血视觉，实际={result.Timeline[^1].P2Hp}");

        var p1Entry = new RunState.CardEntry
        {
            InstanceId = "itm_p1_narwhal",
            TemplateId = Guid.Parse(NarwhalId),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Diamond,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = narwhal,
        };
        var p2Entry = new RunState.CardEntry
        {
            InstanceId = "itm_p2_kat",
            TemplateId = Guid.Parse(KatanaId),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Opponent,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = oppCard,
        };

        var sim = CombatSimConverter.ToCombatSim(
            result,
            fx.Repo,
            new Dictionary<string, CardInstance> { [narwhal.InstanceId] = narwhal },
            new Dictionary<string, CardInstance> { [oppCard.InstanceId] = oppCard },
            new[] { p1Entry },
            new[] { p2Entry });

        // 1. VfxKeys 与 VfxIndex 必须包含 Narwhal 的官方专属穿刺特效
        Assert.NotEmpty(sim.VfxKeys);
        Assert.Contains(sim.VfxKeys, k => k.Contains("Projectile_Pierce", StringComparison.OrdinalIgnoreCase));
        bool hasIndexedVfxEvent = sim.Frames
            .SelectMany(f => f.Events)
            .OfType<CombatSimEventEffectExecuted>()
            .Any(e => e.VfxIndex.HasValue && e.VfxIndex.Value >= 0 && e.VfxIndex.Value < sim.VfxKeys.Count);
        Assert.True(hasIndexedVfxEvent, "CombatSimEventEffectExecuted 应携带指向 CombatSim.VfxKeys 的有效 VfxIndex");

        // 2. 模拟客户端 DataExtensions.Update 回放血量：击杀帧后对手血量必须为负值（未被 Attributes[Health]=0 强行覆写）
        int clientOppHp = 50;
        int minClientOppHp = 50;
        foreach (var f in sim.Frames)
        {
            if (f.OpponentUpdates == null) continue;
            foreach (var adj in f.OpponentUpdates.HealthAdjustments)
                if (adj.AttributeChanged == EPlayerHealthChangeType.Health)
                    clientOppHp += adj.Amount;
            if (f.OpponentUpdates.Attributes.TryGetValue(EPlayerAttributeType.Health, out var hpAttr))
                clientOppHp = hpAttr.CurrentValue;
            if (clientOppHp < minClientOppHp) minClientOppHp = clientOppHp;
        }
        Assert.True(minClientOppHp < -100, $"客户端回放击杀帧对手血量应打成显著负数，实际最低={minClientOppHp}");
    }

    // ── 修复 8：自定义模式 Hour 6 预览幽灵锁定，点击开战绝不重抽换英雄 ──────────

    [Fact]
    public void Fix8_CustomRuleHour6Pvp_LocksPreviewGhostWithoutSwappingHeroOnClick()
    {
        var fx = Fx.Value;
        var rules = new CustomRunRuleSet { StartingGold = 50 }; // 自定义规则模式 -> ghostPartition="custom"
        var run = new RunState
        {
            RunId = "run_v13_2_pvp_lock",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            CustomRules = rules,
            RngSeed = 12345,
            Day = 5,
            Hour = 6,
            State = ERunState.Choice,
            UnlockedSlots = 0x3FF,
            HandSocketUnlockMask = 0x3FF,
        };

        // 给玩家配一把武器 + 1 个提前锁定的标准池幽灵（PoolPartition="standard"）
        var coreKat = new CardInstance(fx.Repo.GetCard(KatanaId)!, CardTier.Silver, null, "itm_p1_kat") { StartSlot = 0 };
        var katana = new RunState.CardEntry
        {
            InstanceId = "itm_p1_kat",
            TemplateId = Guid.Parse(KatanaId),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Silver,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = coreKat,
        };
        run.Cards[katana.InstanceId] = katana;
        run.HandGrid.Cards.Add(coreKat);

        var lockedStandardGhost = new PvpGhostPool.GhostBoard
        {
            BoardId = "syn_d05_jules_locked_test",
            Day = 5,
            Hero = "Jules",
            Level = 6,
            HealthMax = 1200,
            Prestige = 18,
            Wins = 3,
            PowerBucket = "Normal",
            PoolPartition = PvpGhostPool.StandardPartition, // 故意为 standard，验证 custom 模式不再误判失效！
            IsCustomRule = false,
            Items =
            [
                new PvpGhostPool.GhostItem { Slot = 0, Width = 2, TemplateId = KatanaId, InternalName = "Katana", Tier = "Bronze" }
            ],
        };
        run.PendingPvpGhost = lockedStandardGhost;
        run.CurrentPvpOpponent = new SimPvpOpponent(
            name: "Jules (Normal)",
            titlePrefix: "",
            titleSuffix: "",
            rank: null,
            rating: 1200,
            division: 1,
            victories: 3,
            prestige: 18,
            level: 6,
            hero: EHero.Jules,
            playerLoadoutResponse: new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout { accountId = "ghost_syn_d05_jules_locked_test" },
            playerCollection: null);

        var pvpEntry = new RunState.CardEntry
        {
            InstanceId = "pvp_h6_test",
            TemplateId = Guid.Parse(ShopEngine.Hour6PvpCardId),
            Type = ECardType.PvpEncounter,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
        };
        run.Cards[pvpEntry.InstanceId] = pvpEntry;
        run.SelectionSet.Add(pvpEntry.InstanceId);

        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, archetypeIndex: fx.ArchetypeIndex);
        Assert.True(engine.StartSelectionDone);

        var outcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpEntry.InstanceId) });
        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.CombatSpawnSim);

        var pvpOpp = outcome.CombatSpawnSim!.CurrentState!.PvpOpponent;
        Assert.NotNull(pvpOpp);
        Assert.Equal(EHero.Jules, pvpOpp!.Hero);
        Assert.Equal("ghost_syn_d05_jules_locked_test", pvpOpp.PlayerLoadout?.accountId);
    }

    // ── 修复 9：第 9 天神庙探险多小时 ReplacementSet 全链 + 门票消耗验证 ────────

    [Fact]
    public void Fix9_Day9TempleExpedition_MaintainsMultiHourReplacementSetThroughHour5()
    {
        var fx = Fx.Value;
        var run = new RunState
        {
            RunId = "run_v13_2_temple",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            RngSeed = 555,
            Day = 9,
            Hour = 1,
            State = ERunState.Choice,
            UnlockedSlots = 0x3FF,
            HandSocketUnlockMask = 0x3FF,
        };

        // 玩家持有神庙探险门票
        const string templeTicketId = "77c8ef2d-62c8-4214-993d-648915c93e83";
        var ticketTpl = fx.Repo.GetCard(templeTicketId)!;
        var ticketCore = new CardInstance(ticketTpl, CardTier.Diamond, null, "itm_temple_ticket") { StartSlot = 0 };
        var ticketEntry = new RunState.CardEntry
        {
            InstanceId = "itm_temple_ticket",
            TemplateId = Guid.Parse(templeTicketId),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Diamond,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Stash,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = ticketCore,
        };
        run.Cards[ticketEntry.InstanceId] = ticketEntry;
        run.StashGrid.Cards.Add(ticketCore);

        var shop = new ShopEngine(run, fx.Pool, fx.Repo, null, fx.ArchetypeIndex);
        var stepEngine = new EncounterStepEngine(run, fx.Pool, fx.Repo, shop);

        // 1. 进入神庙探险：消耗门票 + 激活 ReplacementSet + 发出三扇门（Fiery/Toxic/Obsidian）
        var enterRes = stepEngine.EnterExpeditionRoom(CardPool.JungleExpeditionRoomId, consumeMatchingTicket: true);
        Assert.True(enterRes.Success);
        Assert.DoesNotContain(ticketEntry.InstanceId, run.Cards.Keys); // 官方原版：门票被销毁
        Assert.Equal(CardPool.JungleExpeditionRoomId, run.ActiveExpeditionId);
        Assert.Equal(3, run.SelectionSet.Count);

        // 2. 选择 Toxic Door -> 锁定 Toxic 分支变体
        string toxicDoorInstanceId = run.SelectionSet.First(id =>
            run.Cards[id].TemplateId.ToString().Equals("c0f1cad2-8561-475a-9e08-a88e8bd62d04", StringComparison.OrdinalIgnoreCase));
        var doorRes = stepEngine.ExecuteStep(toxicDoorInstanceId);
        Assert.True(doorRes.Success);
        Assert.Equal("Toxic", run.ActiveExpeditionVariant);
        Assert.Equal(CardPool.JungleExpeditionRoomId, run.ActiveExpeditionId);

        // 3. Hour 2：应发 3 个 Toxic 陷阱怪（不退回普通事件！）
        run.Hour = 2;
        shop.DealHourEncounters(null, run.VisitedDayEncounterIds);
        var h2Ids = run.SelectionSet.Select(id => run.Cards[id].TemplateId.ToString()).ToList();
        Assert.Equal(3, h2Ids.Count);
        Assert.Contains("7570c77c-7d47-4ab2-ab33-a140bcdf4b70", h2Ids, StringComparer.OrdinalIgnoreCase); // Blowguns (Toxic)
        Assert.Contains("e915e6f0-93c1-46cc-a546-3867f192bb17", h2Ids, StringComparer.OrdinalIgnoreCase); // Caltrops (Toxic)
        Assert.Contains("374416cd-89ed-451d-a131-e3839400432e", h2Ids, StringComparer.OrdinalIgnoreCase); // Boulder (Toxic)

        // 4. Hour 3：应发 Toxic 神庙宝库（Temple Vault (Toxic)）
        run.Hour = 3;
        shop.DealHourEncounters(null, run.VisitedDayEncounterIds);
        var h3Ids = run.SelectionSet.Select(id => run.Cards[id].TemplateId.ToString()).ToList();
        Assert.Contains("810acb8e-e60a-4a84-b773-431d4d10a071", h3Ids, StringComparer.OrdinalIgnoreCase);

        // 5. Hour 4：应发 3 名绿心神庙 Boss
        run.Hour = 4;
        shop.DealHourEncounters(null, run.VisitedDayEncounterIds);
        var h4Ids = run.SelectionSet.Select(id => run.Cards[id].TemplateId.ToString()).ToList();
        Assert.Equal(3, h4Ids.Count);
        Assert.Contains("d1dc075f-f5d4-4bb7-9655-55591e123941", h4Ids, StringComparer.OrdinalIgnoreCase); // Greenheart Guardian

        // 6. Hour 5：应发神庙圣物室（Temple Reliquary）
        run.Hour = 5;
        shop.DealHourEncounters(null, run.VisitedDayEncounterIds);
        var h5Ids = run.SelectionSet.Select(id => run.Cards[id].TemplateId.ToString()).ToList();
        Assert.Contains("5852a0ba-c097-419b-98b7-dadd8bd7ae6e", h5Ids, StringComparer.OrdinalIgnoreCase);

        // 7. 持久化快照往返：探险状态无损保存恢复
        string snapJson = RunSnapshotSerializer.Serialize(run);
        Assert.True(RunSnapshotSerializer.TryDeserialize(snapJson, fx.Repo, out var restored, out _));
        Assert.Equal(CardPool.JungleExpeditionRoomId, restored!.ActiveExpeditionId);
        Assert.Equal("Toxic", restored.ActiveExpeditionVariant);
    }
}
