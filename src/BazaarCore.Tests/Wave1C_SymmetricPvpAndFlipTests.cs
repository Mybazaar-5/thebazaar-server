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
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1C 回归测试（文件 2）：覆盖 P0-40（锦标赛真人同步 PvP 绝对对称：禁单边倍率、统一完整不可变锁板含 Gold/Income/Stash、
/// 顺序无关唯一规范模拟、FlipPerspective 15 类事件与 Portrait 全字段深拷贝镜像、canonicalP2 独立槽永久/变形同步、重启回源复现与超时平局 0 虚假死亡）。
/// 本文件不触碰 ModRuntimeHooks / SessionRegistry / WebApp，严格归属 ShopEngineStaticState 正交并行集合。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class Wave1C_SymmetricPvpAndFlipTests
{
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";

    private static string BuildCardTemplateJson(
        string id,
        string internalName,
        int cooldownMs,
        int damage,
        object? extraAbilities = null,
        string[]? tags = null,
        string size = "Small")
    {
        var abilities = new Dictionary<string, object>
        {
            ["0"] = new
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardFired" },
                Action = new Dictionary<string, object>
                {
                    ["$type"] = "TActionPlayerDamage",
                    ["Target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["TargetMode"] = "Opponent" }
                }
            }
        };
        if (extraAbilities is Dictionary<string, object> extras)
        {
            foreach (var kv in extras)
                abilities[kv.Key] = kv.Value;
        }
        return JsonSerializer.Serialize(new
        {
            Id = id,
            Type = "Item",
            InternalName = internalName,
            StartingTier = "Bronze",
            Size = size,
            Heroes = new[] { "Common" },
            Tags = tags ?? new[] { "Weapon" },
            HiddenTags = Array.Empty<string>(),
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    AbilityIds = abilities.Keys.ToArray(),
                    AuraIds = Array.Empty<string>(),
                    Attributes = new Dictionary<string, double>
                    {
                        ["BuyPrice"] = 2,
                        ["SellPrice"] = 1,
                        ["CooldownMax"] = cooldownMs,
                        ["DamageAmount"] = damage
                    }
                }
            },
            Abilities = abilities,
            Auras = new Dictionary<string, object>()
        });
    }

    private static (RunState Run, RunEngine Engine, string PvpCardInstanceId) SetupHumanTournamentRunAtHour6(
        SharedGameDataFixture.SharedGameDataSnapshot shared,
        SharedTestOverlay overlay,
        PvpGhostPool ghosts,
        LobbyStore lobbies,
        TournamentPvpCoordinator pvpCoord,
        Persistence db,
        Guid tsid,
        string accountId,
        string username,
        CustomRunRuleSet rules,
        int gold,
        int income,
        int healthMax,
        List<(string TemplateId, string InstanceId, EInventorySection Section, EContainerSocketId Socket)> cards)
    {
        var run = new RunState
        {
            RunId = $"w1c_pvp_{accountId}_{Guid.NewGuid():N}",
            AccountId = accountId,
            Username = username,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Tournament,
            TournamentSessionId = tsid,
            Day = 1,
            Hour = 6,
            State = ERunState.Choice,
            StartSelectionDone = true,
            CustomRules = rules.CloneNormalized(),
            RngSeed = 20260401
        };
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = healthMax;
        run.PlayerAttributes[EPlayerAttributeType.Health] = healthMax;
        run.PlayerAttributes[EPlayerAttributeType.Gold] = gold;
        run.PlayerAttributes[EPlayerAttributeType.Income] = income;
        run.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
        run.PlayerAttributes[EPlayerAttributeType.Level] = 3;

        foreach (var (tplId, instId, sec, sock) in cards)
        {
            var tpl = overlay.Repo.GetCard(tplId)!;
            var ci = new CardInstance(tpl, CardTier.Bronze, null, instId);
            var entry = new RunState.CardEntry
            {
                InstanceId = instId,
                TemplateId = Guid.Parse(tplId),
                Type = ECardType.Item,
                Owner = ECombatantId.Player,
                Section = sec,
                Socket = sock,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                CoreCard = ci
            };
            run.Cards[instId] = entry;
            run.SyncEntryFromCoreCardAndDiff(entry);
        }

        string pvpInstId = $"pvp_enc_{accountId}";
        var pvpEntry = new RunState.CardEntry
        {
            InstanceId = pvpInstId,
            TemplateId = Guid.Parse(ShopEngine.Hour6PvpCardId),
            Type = ECardType.PvpEncounter,
            Section = null,
            Socket = EContainerSocketId.Socket_0,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze
        };
        run.Cards[pvpInstId] = pvpEntry;
        run.SelectionSet.Add(pvpInstId);
        run.SelectionRules = new TSelectionContextRules { CanExit = false, SelectionIsFree = true };

        var engine = new RunEngine(
            run,
            overlay.Pool,
            overlay.Monsters,
            overlay.LevelUps,
            overlay.Mode,
            overlay.Repo,
            ghosts,
            shared.ArchetypeIndex,
            playerRules: null,
            lobbies: lobbies,
            sessions: null,
            pvpCoordinator: pvpCoord,
            db: db);

        return (run, engine, pvpInstId);
    }

    [Fact]
    public void P0_40_CombatModifiers_SymmetricHumanPvp_MustSuppressAllOneSidedEnemyMultipliers()
    {
        var rules = CustomRunRuleSet.CreatePreset(EGameModePreset.ExtremeRoguelike, ESpecialModeId.EndlessTower);
        rules.EnemyStatMultiplier = 2.0;
        rules.MutatorIds = new List<string> { MutatorCatalog.GlassCannon, MutatorCatalog.TurboFrenzy };

        var sym = CombatModifiers.FromRulesAndDay(
            rules,
            day: 25,
            isBossRush: true,
            isPve: false,
            bossRushWave: 25,
            isSymmetricHumanPvp: true);

        Assert.Equal(sym.HealthMultiplierP1, sym.HealthMultiplierP2, 6);
        Assert.Equal(0.60, sym.HealthMultiplierP1, 6);
        Assert.Equal(1.0, sym.OffenseMultiplierP2, 6);
        Assert.Equal(0.0, sym.CooldownReductionP2Pct, 6);
        Assert.Equal(0.35, sym.CooldownReductionPct, 6);
        Assert.Equal(1.80, sym.DamageAndDotMultiplier, 6);
    }

    [Fact]
    public void P0_40_SymmetricHumanPvp_OrderIndependence_GoldIncomeStash_DrawAndCanonicalP2PermSync()
    {
        var shared = SharedGameDataFixture.Shared;

        string goldWeaponId = Guid.NewGuid().ToString("D");
        string stashBoosterId = Guid.NewGuid().ToString("D");
        string permGrowId = Guid.NewGuid().ToString("D");
        string transformTargetId = Guid.NewGuid().ToString("D");

        string goldWeaponJson = JsonSerializer.Serialize(new
        {
            Id = goldWeaponId,
            Type = "Item",
            InternalName = "W1C_GoldScalingBlade",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Weapon" },
            HiddenTags = Array.Empty<string>(),
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                    Attributes = new Dictionary<string, double> { ["BuyPrice"] = 2, ["SellPrice"] = 1, ["CooldownMax"] = 2000, ["DamageAmount"] = 10 }
                }
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardFired" },
                    Action = new Dictionary<string, object>
                    {
                        ["$type"] = "TActionPlayerDamage",
                        ["Target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["TargetMode"] = "Opponent" },
                        ["Value"] = new Dictionary<string, object>
                        {
                            ["$type"] = "TReferenceValuePlayerAttribute",
                            ["Target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["TargetMode"] = "Self" },
                            ["AttributeType"] = "Gold"
                        }
                    }
                }
            },
            Auras = new Dictionary<string, object>()
        });

        string stashBoosterJson = JsonSerializer.Serialize(new
        {
            Id = stashBoosterId,
            Type = "Item",
            InternalName = "W1C_StashRelic",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Tool" },
            HiddenTags = Array.Empty<string>(),
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    AbilityIds = Array.Empty<string>(),
                    AuraIds = new[] { "0" },
                    Attributes = new Dictionary<string, double> { ["BuyPrice"] = 2, ["SellPrice"] = 1 }
                }
            },
            Abilities = new Dictionary<string, object>(),
            Auras = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    ActiveIn = "HandAndStash",
                    Action = new Dictionary<string, object>
                    {
                        ["$type"] = "TAuraActionCardModifyAttribute",
                        ["AttributeType"] = "DamageAmount",
                        ["Operation"] = "Add",
                        ["Value"] = new Dictionary<string, object> { ["$type"] = "TFixedValue", ["Value"] = 150 },
                        ["Target"] = new Dictionary<string, object> { ["$type"] = "TTargetCardSection", ["TargetSection"] = "SelfHand" }
                    }
                }
            }
        });

        string transformTargetJson = BuildCardTemplateJson(transformTargetId, "W1C_TransformedApex", 3000, 30);

        string permGrowJson = JsonSerializer.Serialize(new
        {
            Id = permGrowId,
            Type = "Item",
            InternalName = "W1C_PermGrowAndTransform",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Weapon" },
            HiddenTags = Array.Empty<string>(),
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    AbilityIds = new[] { "0", "1" },
                    AuraIds = Array.Empty<string>(),
                    Attributes = new Dictionary<string, double> { ["BuyPrice"] = 2, ["SellPrice"] = 1, ["CooldownMax"] = 2500, ["DamageAmount"] = 15 }
                }
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnFightStarted" },
                    Action = new Dictionary<string, object>
                    {
                        ["$type"] = "TActionCardModifyAttribute",
                        ["AttributeType"] = "DamageAmount",
                        ["Operation"] = "Add",
                        ["DurationType"] = "Permanent",
                        ["Value"] = new Dictionary<string, object> { ["$type"] = "TFixedValue", ["Value"] = 7 },
                        ["Target"] = new Dictionary<string, object> { ["$type"] = "TTargetCardSelf" }
                    }
                },
                ["1"] = new
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnFightStarted" },
                    Action = new Dictionary<string, object>
                    {
                        ["$type"] = "TActionCardTransform",
                        ["Target"] = new Dictionary<string, object> { ["$type"] = "TTargetCardSelf" },
                        ["SpawnContext"] = new Dictionary<string, object>
                        {
                            ["$type"] = "TSpawnContextQuery",
                            ["Groups"] = new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    ["$type"] = "TSpawnGroup",
                                    ["Filters"] = new object[]
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["$type"] = "TSpawnFilterIdList",
                                            ["Ids"] = new[] { transformTargetId }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            },
            Auras = new Dictionary<string, object>()
        });

        using var overlay = shared.CreateMemoryOverlay(customCards:
        [
            (goldWeaponId, goldWeaponJson),
            (stashBoosterId, stashBoosterJson),
            (permGrowId, permGrowJson),
            (transformTargetId, transformTargetJson)
        ]);

        (string Winner, CombatSim SimA, CombatSim SimB, RunState FinalA, RunState FinalB) RunSymmetricPair(bool orderAFirst)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1c_p040_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            Persistence? db = null;
            try
            {
                db = new Persistence(Path.Combine(tempDir, "state.db"));
                var accounts = new AccountStore(db);
                var lobbies = new LobbyStore(db);
                var pvpCoord = new TournamentPvpCoordinator(db, lobbies);
                var ghosts = new PvpGhostPool();

                var accA = accounts.GetOrCreateByName("SymA", "game", "da");
                var accB = accounts.GetOrCreateByName("SymB", "game", "db");
                string aidA = accA.AccountId.ToString("N");
                string aidB = accB.AccountId.ToString("N");
                string canonP1Id = string.Compare(aidA, aidB, StringComparison.OrdinalIgnoreCase) <= 0 ? aidA : aidB;
                string canonP2Id = canonP1Id == aidA ? aidB : aidA;
                var accP1 = canonP1Id == aidA ? accA : accB;
                var accP2 = canonP2Id == aidA ? accA : accB;

                var rules = CustomRunRuleSet.CreatePreset(EGameModePreset.ExtremeRoguelike, ESpecialModeId.Standard);
                var lobby = lobbies.Create(accP1, "对称PvP房", "Tournament", playerCap: 2, isPublic: true);
                lobby.Rules = rules.CloneNormalized();
                Assert.True(lobbies.JoinWithResult(lobby.Code, accP2).Ok);
                Guid tsid = Guid.NewGuid();
                lobbies.StartCountdown(lobby.LobbyId, tsid);

                lobbies.EnsureAndGetAssignment(lobby, 1u, canonP1Id, null);

                var (runP1, engP1, pvpCardP1) = SetupHumanTournamentRunAtHour6(
                    shared, overlay, ghosts, lobbies, pvpCoord, db, tsid, canonP1Id, "CanonP1", rules,
                    gold: 200, income: 30, healthMax: 600,
                    cards:
                    [
                        (goldWeaponId, "p1_gold_wep", EInventorySection.Hand, EContainerSocketId.Socket_0),
                        (stashBoosterId, "p1_stash_relic", EInventorySection.Stash, EContainerSocketId.Socket_0)
                    ]);

                var (runP2, engP2, pvpCardP2) = SetupHumanTournamentRunAtHour6(
                    shared, overlay, ghosts, lobbies, pvpCoord, db, tsid, canonP2Id, "CanonP2", rules,
                    gold: 10, income: 10, healthMax: 600,
                    cards:
                    [
                        (goldWeaponId, "p2_gold_wep", EInventorySection.Hand, EContainerSocketId.Socket_0),
                        (permGrowId, "p2_perm_card", EInventorySection.Hand, EContainerSocketId.Socket_1)
                    ]);

                lobbies.RecordRunProgress(lobby, canonP1Id, runP1);
                lobbies.RecordRunProgress(lobby, canonP2Id, runP2);

                pvpCoord.LockPlayerBoardForDay(tsid, 1u, new LockedPvpBoard(
                    canonP1Id, "CanonP1", "Vanessa", 1u, 600, 20, 3, 0, 200, 30,
                    RunSnapshotSerializer.Serialize(runP1, startSelectionDone: true)));
                pvpCoord.LockPlayerBoardForDay(tsid, 1u, new LockedPvpBoard(
                    canonP2Id, "CanonP2", "Vanessa", 1u, 600, 20, 3, 0, 10, 10,
                    RunSnapshotSerializer.Serialize(runP2, startSelectionDone: true)));

                RunEngine.Outcome outP1, outP2;
                if (orderAFirst)
                {
                    outP1 = engP1.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCardP1) });
                    outP2 = engP2.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCardP2) });
                }
                else
                {
                    outP2 = engP2.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCardP2) });
                    outP1 = engP1.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCardP1) });
                }

                Assert.NotNull(outP1.Combat);
                Assert.NotNull(outP2.Combat);
                var resolved = pvpCoord.TryGetResolvedMatch(tsid, 1u, canonP1Id, canonP2Id);
                Assert.NotNull(resolved);
                return (resolved!.WinnerAccountId, outP1.Combat!, outP2.Combat!, runP1, runP2);
            }
            finally
            {
                db?.Dispose();
                SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        var (winner1, sim1A, sim1B, _, finalB1) = RunSymmetricPair(orderAFirst: true);
        var (winner2, sim2A, sim2B, _, finalB2) = RunSymmetricPair(orderAFirst: false);

        Assert.Equal(winner1, winner2);
        Assert.Equal(ECombatantId.Player, sim1A.Winner);
        Assert.Equal(ECombatantId.Opponent, sim1B.Winner);
        Assert.Equal(sim1A.Winner, sim2A.Winner);
        Assert.Equal(sim1B.Winner, sim2B.Winner);

        Assert.Equal(sim1A.Frames.Count, sim1B.Frames.Count);
        Assert.Equal(sim2A.Frames.Count, sim2B.Frames.Count);
        Assert.Equal(sim1A.Frames.Count, sim2A.Frames.Count);
        Assert.DoesNotContain(sim1B.Frames.SelectMany(f => f.CardUpdates.Keys), id => id.Value.StartsWith("opp_opp_", StringComparison.Ordinal));
        var allP2CardIds = sim1B.Frames.SelectMany(f => f.CardUpdates.Keys).Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("p2_gold_wep", allP2CardIds);
        Assert.Contains("opp_p1_gold_wep", allP2CardIds);

        foreach (var finalP2 in new[] { finalB1, finalB2 })
        {
            Assert.False(finalP2.Cards.ContainsKey("p2_perm_card"), "canonicalP2 原变形卡应已被永久替换");
            Assert.Contains(finalP2.Cards.Values, c =>
                string.Equals(c.TemplateId.ToString(), transformTargetId, StringComparison.OrdinalIgnoreCase) &&
                !c.InstanceId.StartsWith("opp_", StringComparison.Ordinal));
        }

        // 超时双存平局（result.IsDraw == false, IsTimeout == true, 双方 HP > 0）：
        var timeoutDrawResult = new CombatResult
        {
            WinnerPlayerId = "player",
            LoserPlayerId = "opponent",
            Player1Hero = "Vanessa",
            Player2Hero = "Vanessa",
            IsDraw = false,
            IsTimeout = true,
            IsSimultaneousDeath = false,
            DurationMs = 1500,
            Player1RemainingHp = 200,
            Player2RemainingHp = 200,
            Timeline =
            [
                new CombatTickState { TimeMs = 0, P1Hp = 200, P1MaxHp = 200, P2Hp = 200, P2MaxHp = 200 },
                new CombatTickState { TimeMs = 1500, P1Hp = 200, P1MaxHp = 200, P2Hp = 200, P2MaxHp = 200 }
            ]
        };
        var drawSimP1 = CombatSimConverter.ToCombatSim(
            timeoutDrawResult,
            shared.Repo,
            new Dictionary<string, CardInstance>(),
            new Dictionary<string, CardInstance>(),
            Array.Empty<RunState.CardEntry>(),
            Array.Empty<RunState.CardEntry>(),
            isDrawOverride: true);
        Assert.True(drawSimP1.IsDraw);
        Assert.DoesNotContain(drawSimP1.Frames.SelectMany(f => f.Events), e => e is CombatSimEventCombatantDied);
        var drawSimP2 = CombatSimConverter.FlipPerspective(drawSimP1);
        Assert.True(drawSimP2.IsDraw);
        Assert.DoesNotContain(drawSimP2.Frames.SelectMany(f => f.Events), e => e is CombatSimEventCombatantDied);
    }

    [Fact]
    public void P0_40_FlipPerspective_DoubleFlipIsExactIdentityAcrossAll15EventTypesAndPortrait()
    {
        var mapAtoB = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["p1_c1"] = "opp_p1_c1",
            ["opp_p2_c1"] = "p2_c1"
        };
        var mapBtoA = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["opp_p1_c1"] = "p1_c1",
            ["p2_c1"] = "opp_p2_c1"
        };

        var sim = new CombatSim
        {
            Winner = ECombatantId.Player,
            Loser = ECombatantId.Opponent,
            IsDraw = false,
            VfxKeys = new List<string> { "vfx_fire" },
            PortraitKeys = new List<string> { CombatSimConverter.KarnokBeastFormPortraitArtKey },
            OpponentHealthThresholdsForGold = new List<float> { 100f },
            OpponentHealthThresholdsForXp = new List<float> { 50f },
            CardStats = new Dictionary<string, Dictionary<ECardStats, int>>
            {
                ["p1_c1"] = new() { [ECardStats.DamageDone] = 42 },
                ["opp_p2_c1"] = new() { [ECardStats.ShieldAdded] = 18 }
            }
        };

        var frame = new CombatSimFrame
        {
            PlayerUpdates = new CombatSimPlayerUpdate
            {
                IsPlayerDead = false,
                Attributes = new Dictionary<EPlayerAttributeType, CombatSimPlayerAttributeUpdate>
                {
                    [EPlayerAttributeType.Health] = new() { AttributeType = EPlayerAttributeType.Health, PreviousValue = 262, CurrentValue = 250 }
                },
                HealthAdjustments = new List<CombatSimPlayerHealthAdjustment>
                {
                    new() { DamageType = EDamageType.Damage, AttributeChanged = EPlayerHealthChangeType.Health, Amount = -12, IsCrit = true, IsDamageReduced = false }
                },
                Portrait = new CombatSimPlayerPortraitUpdate { Index = 0 }
            },
            OpponentUpdates = new CombatSimPlayerUpdate
            {
                IsPlayerDead = true,
                Attributes = new Dictionary<EPlayerAttributeType, CombatSimPlayerAttributeUpdate>
                {
                    [EPlayerAttributeType.Health] = new() { AttributeType = EPlayerAttributeType.Health, PreviousValue = 8, CurrentValue = 0 }
                },
                HealthAdjustments = new List<CombatSimPlayerHealthAdjustment>
                {
                    new() { DamageType = EDamageType.Burn, AttributeChanged = EPlayerHealthChangeType.Shield, Amount = -8, IsCrit = false, IsDamageReduced = true }
                },
                Portrait = new CombatSimPlayerPortraitUpdate { Index = null }
            },
            CardUpdates = new Dictionary<InstanceId, CombatSimCardUpdate>
            {
                [new InstanceId("p1_c1")] = new()
                {
                    CardInstanceId = new InstanceId("p1_c1"),
                    Attributes = new Dictionary<ECardAttributeType, CombatSimCardAttributeUpdate>
                    {
                        [ECardAttributeType.DamageAmount] = new() { AttributeType = ECardAttributeType.DamageAmount, PreviousValue = 20, CurrentValue = 25 }
                    },
                    Placement = new CardDeltaPlacement { Owner = ECombatantId.Player, Section = EInventorySection.Hand, Socket = EContainerSocketId.Socket_0 },
                    State = new CombatSimCardStateUpdate { PreviousValue = ECardState.Alive, CurrentValue = ECardState.Disabled }
                },
                [new InstanceId("dyn_spawn_99")] = new()
                {
                    CardInstanceId = new InstanceId("dyn_spawn_99"),
                    Placement = new CardDeltaPlacement { Owner = ECombatantId.Opponent, Section = EInventorySection.Hand, Socket = EContainerSocketId.Socket_3 }
                }
            }
        };

        // 填入全部 15 类 ICombatSimEvent 子类
        frame.Events.Add(new CombatSimEventCombatantDied { CombatantId = ECombatantId.Opponent });
        frame.Events.Add(new CombatSimEventEffectExecuted
        {
            ExecutionContextId = "ctx1",
            EffectId = "eff1",
            ActionType = EActionCommandType.PlayerDamage,
            VfxIndex = 0,
            Source = new InstanceId("p1_c1"),
            TriggerSource = new InstanceId("opp_p2_c1"),
            Target = new EffectTargetPlayer { Target = ECombatantId.Opponent }
        });
        frame.Events.Add(new CombatSimEventEffectTriggered
        {
            ExecutionContextId = "ctx2",
            EffectId = "eff2",
            Source = new InstanceId("p1_c1"),
            TriggerSource = new InstanceId("dyn_spawn_99"),
            Targets = new List<IEffectTarget> { new EffectTargetPlayer { Target = ECombatantId.Player }, new EffectTargetCard { Target = new InstanceId("opp_p2_c1") } }
        });
        frame.Events.Add(new CombatSimEventEffectAuraExecuted
        {
            ExecutionContextId = "ctx3",
            EffectId = "aura1",
            Source = new InstanceId("p1_c1"),
            TriggerSource = new InstanceId("p1_c1"),
            AppliedTo = new HashSet<IEffectTarget> { new EffectTargetCard { Target = new InstanceId("p1_c1") }, new EffectTargetPlayer { Target = ECombatantId.Player } },
            RemovedFrom = new HashSet<IEffectTarget> { new EffectTargetCard { Target = new InstanceId("opp_p2_c1") } }
        });
        frame.Events.Add(new CombatSimEventCardTransformed(
            "ctx4",
            "p1_c1",
            new List<SimEventCardTransformation>
            {
                new("dyn_spawn_99", CutlassId, ECardType.Item, ECombatantId.Player, EInventorySection.Hand, EContainerSocketId.Socket_0)
            }));
        frame.Events.Add(new CombatSimEventCardTransformReverted(
            new SimEventCardTransformation("p1_c1", CutlassId, ECardType.Item, ECombatantId.Player, EInventorySection.Hand, EContainerSocketId.Socket_0),
            new List<string> { "dyn_spawn_99" }));
        frame.Events.Add(new CombatSimEventCardEnchanted { InstanceId = "p1_c1", EnchantmentType = EEnchantmentType.Fiery, IsReverted = false });
        frame.Events.Add(new CombatSimEventCardQuestUpdated("opp_p2_c1", 0, 0, 1, 2));
        frame.Events.Add(new CombatSimEventCardQuestCompleted("opp_p2_c1", 0, 0));
        frame.Events.Add(new CombatSimEventCardActionCostSpent
        {
            ExecutingCard = new InstanceId("p1_c1"),
            CardAttributeSpent = ECardAttributeType.Ammo
        });
        frame.Events.Add(new CombatSimEventCardAttributeDeleted
        {
            InstanceId = new InstanceId("opp_p2_c1"),
            AttributeType = ECardAttributeType.Freeze
        });
        frame.Events.Add(new CombatSimEventSandstormCountdownStarted());
        frame.Events.Add(new CombatSimEventSandstormStarted());
        frame.Events.Add(new CombatSimEventMonsterGoldReceived { HealthAmount = 100f });
        frame.Events.Add(new CombatSimEventMonsterXpReceived { HealthAmount = 50f });

        sim.Frames.Clear();
        sim.Frames.Add(frame);

        var flipped = CombatSimConverter.FlipPerspective(sim, mapAtoB);

        Assert.Equal(ECombatantId.Opponent, flipped.Winner);
        Assert.Equal(ECombatantId.Player, flipped.Loser);
        Assert.Single(flipped.PortraitKeys);
        Assert.NotSame(sim.PortraitKeys, flipped.PortraitKeys);
        Assert.True(flipped.CardStats.ContainsKey("opp_p1_c1"));
        Assert.True(flipped.CardStats.ContainsKey("p2_c1"));

        var fFrame = flipped.Frames[0];
        Assert.True(fFrame.PlayerUpdates!.IsPlayerDead);
        Assert.False(fFrame.OpponentUpdates!.IsPlayerDead);
        Assert.Equal(0, fFrame.OpponentUpdates.Portrait!.Index);
        Assert.NotSame(frame.PlayerUpdates.Portrait, fFrame.OpponentUpdates.Portrait);
        Assert.NotSame(frame.PlayerUpdates.HealthAdjustments, fFrame.OpponentUpdates.HealthAdjustments);
        Assert.True(fFrame.CardUpdates.ContainsKey(new InstanceId("opp_p1_c1")));
        Assert.Equal(ECombatantId.Opponent, fFrame.CardUpdates[new InstanceId("opp_p1_c1")].Placement!.Owner);
        Assert.True(fFrame.CardUpdates.ContainsKey(new InstanceId("dyn_spawn_99")), "战中动态衍生新 ID 必须原样透传");
        Assert.Equal(ECombatantId.Player, fFrame.CardUpdates[new InstanceId("dyn_spawn_99")].Placement!.Owner);
        Assert.Equal(15, fFrame.Events.Count);
        Assert.Equal(ECombatantId.Player, Assert.IsType<CombatSimEventCombatantDied>(fFrame.Events[0]).CombatantId);
        var fExec = Assert.IsType<CombatSimEventEffectExecuted>(fFrame.Events[1]);
        Assert.Equal("opp_p1_c1", fExec.Source!.Value.Value);
        Assert.Equal("p2_c1", fExec.TriggerSource!.Value.Value);
        Assert.Equal(ECombatantId.Player, Assert.IsType<EffectTargetPlayer>(fExec.Target).Target);
        var fTrans = Assert.IsType<CombatSimEventCardTransformed>(fFrame.Events[4]);
        Assert.Equal("opp_p1_c1", fTrans.OriginalInstanceId);
        Assert.Equal("dyn_spawn_99", fTrans.TransformedCards[0].InstanceId);
        Assert.Equal(ECombatantId.Opponent, fTrans.TransformedCards[0].CombatantId);
        var fDel = Assert.IsType<CombatSimEventCardAttributeDeleted>(fFrame.Events[10]);
        Assert.Equal("p2_c1", fDel.InstanceId.Value);

        var roundTrip = CombatSimConverter.FlipPerspective(flipped, mapBtoA);
        Assert.Equal(sim.Winner, roundTrip.Winner);
        Assert.Equal(sim.Loser, roundTrip.Loser);
        Assert.Equal(sim.IsDraw, roundTrip.IsDraw);
        Assert.Equal(sim.PortraitKeys, roundTrip.PortraitKeys);
        Assert.Equal(sim.CardStats["p1_c1"][ECardStats.DamageDone], roundTrip.CardStats["p1_c1"][ECardStats.DamageDone]);
        var rFrame = roundTrip.Frames[0];
        Assert.Equal(frame.PlayerUpdates.IsPlayerDead, rFrame.PlayerUpdates!.IsPlayerDead);
        Assert.Equal(frame.PlayerUpdates.Portrait.Index, rFrame.PlayerUpdates.Portrait!.Index);
        Assert.Equal(frame.PlayerUpdates.HealthAdjustments[0].Amount, rFrame.PlayerUpdates.HealthAdjustments[0].Amount);
        Assert.Equal(frame.OpponentUpdates.IsPlayerDead, rFrame.OpponentUpdates!.IsPlayerDead);
        Assert.Equal(ECombatantId.Player, rFrame.CardUpdates[new InstanceId("p1_c1")].Placement!.Owner);
        Assert.Equal(ECombatantId.Opponent, rFrame.CardUpdates[new InstanceId("dyn_spawn_99")].Placement!.Owner);
        Assert.Equal(ECombatantId.Opponent, Assert.IsType<CombatSimEventCombatantDied>(rFrame.Events[0]).CombatantId);
        Assert.Equal("p1_c1", Assert.IsType<CombatSimEventEffectExecuted>(rFrame.Events[1]).Source!.Value.Value);
        Assert.Equal("opp_p2_c1", Assert.IsType<CombatSimEventCardAttributeDeleted>(rFrame.Events[10]).InstanceId.Value);
        Assert.Equal(100f, Assert.IsType<CombatSimEventMonsterGoldReceived>(rFrame.Events[13]).HealthAmount);
        Assert.Equal(50f, Assert.IsType<CombatSimEventMonsterXpReceived>(rFrame.Events[14]).HealthAmount);
    }

    [Fact]
    public void P0_40_RestartFallback_WithNullCachedSim_ReproducesExactMirroredAnimation()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1c_p040_restart_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Persistence? db1 = null;
        Persistence? db2 = null;
        try
        {
            string dbPath = Path.Combine(tempDir, "state.db");
            string w1Id = Guid.NewGuid().ToString("D");
            string w2Id = Guid.NewGuid().ToString("D");
            using var overlay = shared.CreateMemoryOverlay(customCards:
            [
                (w1Id, BuildCardTemplateJson(w1Id, "W1C_FastBlade", 2000, 60)),
                (w2Id, BuildCardTemplateJson(w2Id, "W1C_SlowClub", 3500, 35))
            ]);

            db1 = new Persistence(dbPath);
            var accounts1 = new AccountStore(db1);
            var lobbies1 = new LobbyStore(db1);
            var coord1 = new TournamentPvpCoordinator(db1, lobbies1);
            var ghosts = new PvpGhostPool();

            var accA = accounts1.GetOrCreateByName("RestartA", "game", "ra");
            var accB = accounts1.GetOrCreateByName("RestartB", "game", "rb");
            string aidA = accA.AccountId.ToString("N");
            string aidB = accB.AccountId.ToString("N");

            var rules = CustomRunRuleSet.CreatePreset(EGameModePreset.PureOfficial, ESpecialModeId.Standard);
            var lobby1 = lobbies1.Create(accA, "重启回源房", "Tournament", playerCap: 2, isPublic: true);
            Assert.True(lobbies1.JoinWithResult(lobby1.Code, accB).Ok);
            Guid tsid = Guid.NewGuid();
            lobbies1.StartCountdown(lobby1.LobbyId, tsid);
            lobbies1.EnsureAndGetAssignment(lobby1, 1u, aidA, null);

            var (runA, engA, pvpCardA) = SetupHumanTournamentRunAtHour6(
                shared, overlay, ghosts, lobbies1, coord1, db1, tsid, aidA, "RestartA", rules,
                gold: 20, income: 10, healthMax: 400,
                cards: [(w1Id, "ra_blade", EInventorySection.Hand, EContainerSocketId.Socket_0)]);
            var (runB, _, _) = SetupHumanTournamentRunAtHour6(
                shared, overlay, ghosts, lobbies1, coord1, db1, tsid, aidB, "RestartB", rules,
                gold: 20, income: 10, healthMax: 400,
                cards: [(w2Id, "rb_club", EInventorySection.Hand, EContainerSocketId.Socket_0)]);

            lobbies1.RecordRunProgress(lobby1, aidA, runA);
            lobbies1.RecordRunProgress(lobby1, aidB, runB);
            coord1.LockPlayerBoardForDay(tsid, 1u, new LockedPvpBoard(
                aidA, "RestartA", "Vanessa", 1u, 400, 20, 3, 0, 20, 10, RunSnapshotSerializer.Serialize(runA, true)));
            coord1.LockPlayerBoardForDay(tsid, 1u, new LockedPvpBoard(
                aidB, "RestartB", "Vanessa", 1u, 400, 20, 3, 0, 20, 10, RunSnapshotSerializer.Serialize(runB, true)));

            var outA = engA.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCardA) });
            Assert.NotNull(outA.Combat);
            db1.FlushAllPending();
            db1.Dispose();
            db1 = null;
            SqliteConnection.ClearAllPools();

            db2 = new Persistence(dbPath);
            var lobbies2 = new LobbyStore(db2);
            var coord2 = new TournamentPvpCoordinator(db2, lobbies2);
            var resolvedFromDb = coord2.TryGetResolvedMatch(tsid, 1u, aidA, aidB);
            Assert.NotNull(resolvedFromDb);
            Assert.Null(resolvedFromDb!.CachedCombatSimForP1);
            Assert.Null(resolvedFromDb.CachedCombatSimForP2);

            var (_, engB2, pvpCardB2) = SetupHumanTournamentRunAtHour6(
                shared, overlay, ghosts, lobbies2, coord2, db2, tsid, aidB, "RestartB", rules,
                gold: 20, income: 10, healthMax: 400,
                cards: [(w2Id, "rb_club", EInventorySection.Hand, EContainerSocketId.Socket_0)]);
            var outB = engB2.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCardB2) });
            Assert.NotNull(outB.Combat);

            Assert.Equal(outA.Combat!.Frames.Count, outB.Combat!.Frames.Count);
            Assert.Equal(
                outA.Combat.Winner == ECombatantId.Player ? ECombatantId.Opponent : ECombatantId.Player,
                outB.Combat.Winner);
        }
        finally
        {
            db1?.Dispose();
            db2?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
