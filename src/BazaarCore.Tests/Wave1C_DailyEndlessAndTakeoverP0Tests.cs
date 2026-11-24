using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Protocol;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1C 回归测试（文件 3）：覆盖 P0-41（每日同种子挑战单信源收口）、
/// P0-42（锦标赛掉线托管阶段 4 同步推进/终结 RunState、无误拦单调守卫、AFK 淘汰后 /sessions 阻断重开 Day 1 僵尸局、收紧对在线真人缺席判负）、
/// P0-43（无尽爬塔 Day 11+ 同层真镜像 1.0x 与低层补位相对缩放）。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1C_DailyEndlessAndTakeoverP0Tests
{
    private static byte[] SerializeCommand(INetCommand cmd)
        => MessagePackSerializer.Serialize<INetCommand>(cmd, Codec.Options);

    [Fact]
    public async Task P0_41_DailyChallengeTodayEndpoint_MatchesCoreSeedGeneratorExactly()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1c_p041_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        WebApplication? app = null;
        HttpClient? http = null;
        Persistence? db = null;
        try
        {
            db = new Persistence(Path.Combine(tempDir, "state.db"));
            var accounts = new AccountStore(db);

            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(accounts);

            app = builder.Build();
            LeaderboardEndpoints.Map(app);
            await app.StartAsync();

            string baseUrl = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };

            var resp = await http.GetAsync("/api/daily-challenge/today");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string apiDateKey = root.GetProperty("dateKey").GetString()!;
            int apiSeed = root.GetProperty("masterSeed").GetInt32();
            string apiHero = root.GetProperty("featuredHero").GetString()!;
            double apiScoreMult = root.GetProperty("scoreMultiplier").GetDouble();
            var apiMutators = root.GetProperty("mutators")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString()!)
                .ToList();

            var coreSpec = DailyChallengeSeedGenerator.GenerateSpec(apiDateKey);
            double coreScoreMult = MutatorCatalog.CalculateTotalScoreMultiplier(EGameModePreset.PureOfficial, coreSpec.MutatorIds);
            Assert.Equal(coreSpec.DateKey, apiDateKey);
            Assert.Equal(coreSpec.MasterSeed, apiSeed);
            Assert.Equal(coreSpec.FeaturedHero, apiHero);
            Assert.Equal(coreSpec.MutatorIds, apiMutators);
            Assert.Equal(coreScoreMult, apiScoreMult, 4);
            Assert.Equal(DailyChallengeSeedGenerator.GetTodayDateKeyUtc8(), LeaderboardEndpoints.GetChinaTodayDateKey());

            var dailyRules = CustomRunRuleSet.CreatePreset(EGameModePreset.PureOfficial, ESpecialModeId.DailyChallenge);
            dailyRules.DailyChallengeDate = apiDateKey;
            var run = new RunState
            {
                RunId = $"w1c_daily_{Guid.NewGuid():N}",
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                CustomRules = dailyRules
            };
            var engine = new RunEngine(
                run, shared.Pool, shared.CreateMonstersCopy(), shared.LevelUps,
                shared.Mode, shared.Repo, shared.Ghosts, shared.ArchetypeIndex);
            engine.StartRun();
            Assert.Equal(apiSeed, run.CustomRules.FixedRunSeed);
            Assert.Equal(apiMutators, run.CustomRules.MutatorIds);
        }
        finally
        {
            if (http != null) { try { http.Dispose(); } catch { } }
            if (app != null) { try { await app.StopAsync(); await app.DisposeAsync(); } catch { } }
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task P0_42_TakeoverAdvanceAfkElimination_SyncsRunState_BlocksZombieNewRun_AndAllowsDay2Reconnect()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1c_p042_sync_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        WebApplication? app = null;
        HttpClient? http = null;
        Persistence? db = null;
        try
        {
            db = new Persistence(Path.Combine(tempDir, "state.db"));
            var accounts = new AccountStore(db);
            var sessions = new SessionStore();
            var playerRules = new PlayerRuleStore(db);
            var lobbies = new LobbyStore(db);
            var pvpCoord = new TournamentPvpCoordinator(db, lobbies);
            var ghosts = new PvpGhostPool();
            var isolatedPool = shared.CreateIsolatedCardPool();
            var monstersCopy = shared.CreateMonstersCopy();

            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(accounts);
            builder.Services.AddSingleton(sessions);
            builder.Services.AddSingleton(playerRules);
            builder.Services.AddSingleton(lobbies);
            builder.Services.AddSingleton<ITournamentPvpCoordinator>(pvpCoord);
            builder.Services.AddSingleton(isolatedPool);
            builder.Services.AddSingleton(monstersCopy);
            builder.Services.AddSingleton(shared.LevelUps);
            builder.Services.AddSingleton(shared.Mode);
            builder.Services.AddSingleton(shared.Repo);
            builder.Services.AddSingleton(ghosts);
            builder.Services.AddSingleton(shared.ArchetypeIndex);

            app = builder.Build();
            GameServerEndpoints.Map(app);
            TournamentEndpoints.Map(app, lobbies);
            await app.StartAsync();

            string baseUrl = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };

            var accA = accounts.GetOrCreateByName("TakeoverA", "game", "tk_a");
            var accB = accounts.GetOrCreateByName("TakeoverB", "game", "tk_b");
            string aidA = accA.AccountId.ToString("N");
            string aidB = accB.AccountId.ToString("N");
            string bearerB = accounts.IssuePersistentTokens(accB, "game", "tk_b").AccessToken;

            var lobby = lobbies.Create(accA, "托管同步房", "Tournament", playerCap: 2, isPublic: true);
            Assert.True(lobbies.JoinWithResult(lobby.Code, accB).Ok);
            Guid tsid = Guid.NewGuid();
            lobbies.StartCountdown(lobby.LobbyId, tsid);

            var initB = new InitializeRunCommand { SelectedHero = EHero.Vanessa, PlayMode = EPlayMode.Tournament, TournamentSessionId = tsid };
            var sessB = sessions.Create(aidB, accB.Username, initB);
            var runB = new RunState
            {
                RunId = sessB.RunId,
                AccountId = aidB,
                Username = accB.Username,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                TournamentSessionId = tsid,
                Day = 1,
                Hour = 2,
                State = ERunState.Choice,
                StartSelectionDone = true
            };
            runB.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
            var engB = new RunEngine(
                runB, isolatedPool, monstersCopy, shared.LevelUps,
                shared.Mode, shared.Repo, ghosts, shared.ArchetypeIndex,
                playerRules, lobbies, sessions, pvpCoord, db: db);
            SessionRegistry.Attach(sessB.Sid, runB, engB);
            db.SaveRunSnapshot(runB, aidB, RunSnapshotSerializer.Serialize(runB, true));

            lobbies.EnsureAndGetAssignment(lobby, 1u, aidA, sessions);
            lobbies.EnsureAndGetAssignment(lobby, 2u, aidA, sessions);
            lobbies.EnsureAndGetAssignment(lobby, 3u, aidA, sessions);

            lock (lobby.Members)
            {
                var mA = lobby.Members.First(x => x.AccountId == aidA);
                mA.CurrentDay = 2;
                mA.CurrentHour = 1;
                mA.IsReadyAtHour6Pvp = false;
                mA.ConnectionState = "online";
                mA.LastSeenAt = DateTime.UtcNow;

                var mB = lobby.Members.First(x => x.AccountId == aidB);
                mB.CurrentDay = 1;
                mB.CurrentHour = 2;
                mB.ConnectionState = "ai_takeover";
                mB.LastSeenAt = DateTime.UtcNow.AddMinutes(-5);
            }

            lobbies.RunWatchdogTick(sessions, pvpCoord);

            // 断言 ①：B 内存旧会话已被分离防空 SelectionSet 僵尸，SQLite 快照已推进到 Day 2 Hour 1 且 LastCompletedPvpDay == 1
            Assert.Null(sessions.GetByAccount(aidB));
            var dbRowAfterAdvance = db.LoadLatestTournamentRun(accB.AccountId, tsid);
            Assert.NotNull(dbRowAfterAdvance);
            Assert.False(dbRowAfterAdvance!.IsEnded);
            Assert.Equal(2u, dbRowAfterAdvance.Day);
            Assert.Equal(1u, dbRowAfterAdvance.Hour);
            Assert.True(RunSnapshotSerializer.TryDeserialize(dbRowAfterAdvance.Snapshot, shared.Repo, out var snapAfterAdvance, out _));
            Assert.Equal(1u, snapAfterAdvance!.LastCompletedPvpDay);

            // 断言 ②：陈旧 Day 1 RunState 调 RecordRunProgress 必须被单调守卫彻底拦截，绝不倒流
            var staleDay1Run = new RunState
            {
                RunId = runB.RunId,
                AccountId = aidB,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                TournamentSessionId = tsid,
                Day = 1,
                Hour = 3,
                Victories = 0,
                Defeats = 0,
                LastCompletedPvpDay = 0,
                State = ERunState.Choice
            };
            staleDay1Run.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
            lobbies.RecordRunProgress(lobby, aidB, staleDay1Run);
            lock (lobby.Members)
            {
                var mB = lobby.Members.First(x => x.AccountId == aidB);
                Assert.Equal(2u, mB.CurrentDay);
                Assert.True(mB.FinalDefeats >= 1);
                Assert.True(mB.FinalPrestige < 20);
            }

            // 断言 ③：曾托管 1 天的 B 重连后正常玩 Day 2 Hour 1 -> Hour 6，RecordRunProgress 必须放行更新 CurrentHour 与 IsReadyAtHour6Pvp = true
            var reconnectedDay2Run = new RunState
            {
                RunId = runB.RunId,
                AccountId = aidB,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                TournamentSessionId = tsid,
                Day = 2,
                Hour = 6,
                Victories = 0,
                Defeats = 1,
                LastCompletedPvpDay = 1,
                State = ERunState.Choice
            };
            reconnectedDay2Run.PlayerAttributes[EPlayerAttributeType.Prestige] = 19;
            lobbies.RecordRunProgress(lobby, aidB, reconnectedDay2Run);
            lock (lobby.Members)
            {
                var mB = lobby.Members.First(x => x.AccountId == aidB);
                Assert.Equal(2u, mB.CurrentDay);
                Assert.Equal(6u, mB.CurrentHour);
                Assert.True(mB.IsReadyAtHour6Pvp, "曾托管 1 天后重连打 Day 2 的玩家在 Hour 6 必须正常标记 IsReadyAtHour6Pvp=true，绝不可被错误拦截卡死屏障");
            }

            // 断言 ④：模拟 B 再次掉线滞后至第 5 天触发连续 3 天托管 AFK 淘汰
            var sessB2 = sessions.Create(aidB, accB.Username, initB);
            var runB2 = new RunState
            {
                RunId = runB.RunId,
                AccountId = aidB,
                Username = accB.Username,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                TournamentSessionId = tsid,
                Day = 2,
                Hour = 2,
                LastCompletedPvpDay = 1,
                State = ERunState.Choice,
                StartSelectionDone = true
            };
            runB2.PlayerAttributes[EPlayerAttributeType.Prestige] = 19;
            var engB2 = new RunEngine(
                runB2, isolatedPool, monstersCopy, shared.LevelUps,
                shared.Mode, shared.Repo, ghosts, shared.ArchetypeIndex,
                playerRules, lobbies, sessions, pvpCoord, db: db);
            SessionRegistry.Attach(sessB2.Sid, runB2, engB2);

            lock (lobby.Members)
            {
                var mA = lobby.Members.First(x => x.AccountId == aidA);
                mA.CurrentDay = 5;
                mA.ConnectionState = "online";
                mA.LastSeenAt = DateTime.UtcNow;

                var mB = lobby.Members.First(x => x.AccountId == aidB);
                mB.CurrentDay = 2;
                mB.ConnectionState = "ai_takeover";
                mB.LastSeenAt = DateTime.UtcNow.AddMinutes(-10);
            }
            lobbies.EnsureAndGetAssignment(lobby, 4u, aidA, sessions);

            lobbies.RunWatchdogTick(sessions, pvpCoord);

            Assert.Equal(ERunState.EndRunDefeat, runB2.State);
            Assert.Null(sessions.GetByAccount(aidB));
            var endedRow = db.LoadLatestTournamentRun(accB.AccountId, tsid);
            Assert.NotNull(endedRow);
            Assert.True(endedRow!.IsEnded, "AFK 淘汰必须写入 runs.is_ended = 1");
            Assert.Equal(ERunState.EndRunDefeat, endedRow.State);
            Assert.True(RunSnapshotSerializer.TryDeserialize(endedRow.Snapshot, shared.Repo, out var endedSnap, out _));
            Assert.Equal(ERunState.EndRunDefeat, endedSnap!.State);

            // 断言 ⑤：GET /tournament/status 对已 AFK 淘汰玩家准确返回 isEliminated=true & endRun=true
            using (var statusReq = new HttpRequestMessage(HttpMethod.Get, "/tournament/status"))
            {
                statusReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerB);
                var statusResp = await http.SendAsync(statusReq);
                Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
                using var sDoc = JsonDocument.Parse(await statusResp.Content.ReadAsStringAsync());
                var rootNode = sDoc.RootElement;
                Assert.True(rootNode.GetProperty("IsEliminated").GetBoolean());
                Assert.True(rootNode.GetProperty("EndRun").GetBoolean());
            }

            // 断言 ⑥：已 AFK 淘汰玩家带同 TournamentSessionId 再调 POST /sessions，必须返回 HTTP 409 Conflict（绝不重开 Day 1 僵尸局）
            using (var sessReq = new HttpRequestMessage(HttpMethod.Post, "/sessions"))
            {
                sessReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerB);
                sessReq.Content = new ByteArrayContent(SerializeCommand(initB));
                sessReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var sessResp = await http.SendAsync(sessReq);
                Assert.Equal(HttpStatusCode.Conflict, sessResp.StatusCode);
                string body = await sessResp.Content.ReadAsStringAsync();
                Assert.Contains("tournament_run_finished", body);
            }
            var afterBlockedRow = db.LoadLatestTournamentRun(accB.AccountId, tsid);
            Assert.Equal(endedRow.RunId, afterBlockedRow!.RunId);
            Assert.True(afterBlockedRow.IsEnded);
            Assert.Null(sessions.GetByAccount(aidB));
        }
        finally
        {
            if (http != null) { try { http.Dispose(); } catch { } }
            if (app != null) { try { await app.StopAsync(); await app.DisposeAsync(); } catch { } }
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void P0_42_TakeoverMustNotPreemptActiveHumanOpponentOnThirdPartyAdvance()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1c_p042_preempt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Persistence? db = null;
        try
        {
            db = new Persistence(Path.Combine(tempDir, "state.db"));
            var accounts = new AccountStore(db);
            var sessions = new SessionStore();
            var lobbies = new LobbyStore(db);
            var pvpCoord = new TournamentPvpCoordinator(db, lobbies);

            var pA = accounts.GetOrCreateByName("ThirdPartyA", "game", "tp_a");
            var pB = accounts.GetOrCreateByName("TakeoverVictimB", "game", "tp_b");
            var pC = accounts.GetOrCreateByName("ActiveHumanC", "game", "tp_c");
            string aidA = pA.AccountId.ToString("N");
            string aidB = pB.AccountId.ToString("N");
            string aidC = pC.AccountId.ToString("N");

            var lobby = lobbies.Create(pA, "三人防截胡房", "Tournament", playerCap: 4, isPublic: true);
            Assert.True(lobbies.JoinWithResult(lobby.Code, pB).Ok);
            Assert.True(lobbies.JoinWithResult(lobby.Code, pC).Ok);
            Guid tsid = Guid.NewGuid();
            lobbies.StartCountdown(lobby.LobbyId, tsid);

            lobbies.EnsureAndGetAssignment(lobby, 1u, aidA, sessions);
            var allHumans = new[] { aidA, aidB, aidC };
            string? offlineId = null;
            string? activeOppId = null;
            foreach (string h in allHumans)
            {
                var asg = lobby.Matchmaker.GetAssignment(h, 1u);
                if (asg != null && !asg.OpponentIsBot)
                {
                    offlineId = h;
                    activeOppId = asg.OpponentAccountId;
                    break;
                }
            }
            Assert.NotNull(offlineId);
            Assert.NotNull(activeOppId);
            string leaderId = allHumans.First(x => x != offlineId && x != activeOppId);

            lock (lobby.Members)
            {
                foreach (var m in lobby.Members)
                {
                    if (m.AccountId == leaderId)
                    {
                        m.CurrentDay = 2;
                        m.CurrentHour = 1;
                        m.ConnectionState = "online";
                        m.LastSeenAt = DateTime.UtcNow;
                    }
                    else if (m.AccountId == offlineId)
                    {
                        m.CurrentDay = 1;
                        m.CurrentHour = 2;
                        m.ConnectionState = "ai_takeover";
                        m.LastSeenAt = DateTime.UtcNow.AddMinutes(-5);
                    }
                    else if (m.AccountId == activeOppId)
                    {
                        m.CurrentDay = 1;
                        m.CurrentHour = 3;
                        m.IsReadyAtHour6Pvp = false;
                        m.ConnectionState = "online";
                        m.LastSeenAt = DateTime.UtcNow;
                    }
                }
            }

            lobbies.RunWatchdogTick(sessions, pvpCoord);
            Assert.Null(pvpCoord.TryGetResolvedMatch(tsid, 1u, offlineId!, activeOppId!));
            lock (lobby.Members)
            {
                var mOff = lobby.Members.First(x => x.AccountId == offlineId);
                Assert.Equal(1u, mOff.CurrentDay);
                Assert.Equal(0, mOff.MissedPvpDays);
            }

            lock (lobby.Members)
            {
                var mOpp = lobby.Members.First(x => x.AccountId == activeOppId);
                mOpp.CurrentHour = 6;
                mOpp.IsReadyAtHour6Pvp = true;
            }
            lobbies.RunWatchdogTick(sessions, pvpCoord);
            Assert.NotNull(pvpCoord.TryGetResolvedMatch(tsid, 1u, offlineId!, activeOppId!));
            lock (lobby.Members)
            {
                var mOff = lobby.Members.First(x => x.AccountId == offlineId);
                Assert.Equal(2u, mOff.CurrentDay);
                Assert.Equal(1, mOff.MissedPvpDays);
            }
        }
        finally
        {
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void P0_43_EndlessTower_SameFloorRealGhost_Gets1x_AndLowerFloorFallback_GetsRelativeScale()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1c_p043_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Persistence? db = null;
        try
        {
            db = new Persistence(Path.Combine(tempDir, "state.db"));
            var endlessRules = CustomRunRuleSet.CreatePreset(EGameModePreset.PureOfficial, ESpecialModeId.EndlessTower);

            var ghosts = new PvpGhostPool();
            string someItemId = shared.Repo.CardsById.Values.First(c =>
                string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(c.Id, out _)).Id;

            void SeedRealEndlessGhosts(int day, int count, int healthMax)
            {
                for (int i = 0; i < count; i++)
                {
                    var run = new RunState
                    {
                        RunId = $"w1c_endless_d{day}_{i}",
                        Hero = EHero.Vanessa,
                        PlayMode = EPlayMode.Unranked,
                        Day = (uint)day,
                        Hour = 6,
                        CustomRules = endlessRules.CloneNormalized()
                    };
                    run.PlayerAttributes[EPlayerAttributeType.HealthMax] = healthMax;
                    run.PlayerAttributes[EPlayerAttributeType.Health] = healthMax;
                    var tpl = shared.Repo.GetCard(someItemId)!;
                    var entry = new RunState.CardEntry
                    {
                        InstanceId = $"eg_{day}_{i}",
                        TemplateId = Guid.Parse(someItemId),
                        Type = ECardType.Item,
                        Owner = ECombatantId.Player,
                        Section = EInventorySection.Hand,
                        Socket = EContainerSocketId.Socket_0,
                        Size = ECardSize.Small,
                        Tier = ETier.Diamond,
                        CoreCard = new CardInstance(tpl, CardTier.Diamond, null, $"eg_{day}_{i}")
                    };
                    run.Cards[entry.InstanceId] = entry;
                    var recorded = ghosts.RecordPlayerGhost(run, db, isCustomRule: true, poolPartition: "endless_tower");
                    Assert.NotNull(recorded);
                }
            }

            SeedRealEndlessGhosts(day: 15, count: 6, healthMax: 4500);
            SeedRealEndlessGhosts(day: 25, count: 6, healthMax: 12000);

            // 1. Day 25 同层真实池命中（>=5 条）：EffectiveSourceDay == 25 == EffectiveTargetDay，CombatModifiers 精确 1.0x 绝不二次暴涨！
            var pickedSameFloor = ghosts.PickOpponent(25, 10, 0, new Random(12345), shared.Repo, includeCustomRuleGhosts: true, poolPartition: "endless_tower");
            Assert.NotNull(pickedSameFloor);
            Assert.Equal(25, pickedSameFloor.EffectiveSourceDay);
            Assert.Equal(25, pickedSameFloor.EffectiveTargetDay);
            var modsSameFloor = CombatModifiers.FromRulesAndDay(
                endlessRules,
                day: 25,
                isBossRush: false,
                isPve: false,
                bossRushWave: 0,
                isSymmetricHumanPvp: false,
                endlessGhostSourceDay: pickedSameFloor.EffectiveSourceDay);
            Assert.Equal(1.0, modsSameFloor.HealthMultiplierP2, 6);
            Assert.Equal(1.0, modsSameFloor.OffenseMultiplierP2, 6);
            Assert.Equal(0.0, modsSameFloor.CooldownReductionP2Pct, 6);

            // 2. Day 30 池空（<5 条触发低层补位，源来自高天强镜像 Day 15/25）：SourceDay < 30 && TargetDay == 30，精确乘相对比！
            var pickedFallback = ghosts.PickOpponent(30, 10, 0, new Random(12345), shared.Repo, includeCustomRuleGhosts: true, poolPartition: "endless_tower");
            Assert.NotNull(pickedFallback);
            Assert.Equal(30, pickedFallback.EffectiveTargetDay);
            Assert.True(pickedFallback.EffectiveSourceDay < 30, $"低层补位幽灵必须保留原始录制 SourceDay < 30，实际为 {pickedFallback.EffectiveSourceDay}");
            var modsFallback = CombatModifiers.FromRulesAndDay(
                endlessRules,
                day: 30,
                isBossRush: false,
                isPve: false,
                bossRushWave: 0,
                isSymmetricHumanPvp: false,
                endlessGhostSourceDay: pickedFallback.EffectiveSourceDay);
            var full30 = EndlessTowerScaler.Compute(30, endlessRules.EndlessMaxDay);
            var fullSrc = EndlessTowerScaler.Compute(pickedFallback.EffectiveSourceDay, endlessRules.EndlessMaxDay);
            double expectedHpRel = Math.Round(full30.EnemyHealthMultiplier / fullSrc.EnemyHealthMultiplier, 4);
            Assert.Equal(expectedHpRel, modsFallback.HealthMultiplierP2, 4);
            Assert.True(modsFallback.HealthMultiplierP2 > 1.0 && modsFallback.HealthMultiplierP2 < full30.EnemyHealthMultiplier);

            // 3. 源为 Day 10 时平滑退化为完整 Compute(30)
            var relFrom10 = EndlessTowerScaler.ComputeRelativeScaling(10, 30, endlessRules.EndlessMaxDay);
            Assert.Equal(full30.EnemyHealthMultiplier, relFrom10.HealthMultiplier, 4);
            Assert.Equal(full30.EnemyOffenseMultiplier, relFrom10.OffenseMultiplier, 4);
            Assert.Equal(full30.EnemyCooldownReductionPct, relFrom10.CooldownReductionPct, 4);
        }
        finally
        {
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
