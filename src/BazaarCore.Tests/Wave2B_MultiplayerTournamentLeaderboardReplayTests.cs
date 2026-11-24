using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.TempoNet;
using BazaarServer.Tournament;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2B Batch E（多人联机 / 锦标赛 / 排行榜 / 战报回放 / 活跃摘要 / Mod 哈希一致性）高强度自动化验证（#17-#21）。
/// 覆盖 D6-P1-07, D8-P1-02, D6-P1-11, D6-P1-12, P0-26, D6-P1-16, D6-P1-17, D3-P1-12。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave2B_MultiplayerTournamentLeaderboardReplayTests
{
    private static string CreateTempDbPath(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "BazaarWave2BTests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{tag}_{Guid.NewGuid():N}.db");
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { }
        try { if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { }
    }

    /// <summary>
    /// #17 [D6-P1-07 + D8-P1-02]：
    /// 1) 取消后加入返回 LOBBY_NOT_FOUND；
    /// 2) 开赛后 Leave 走软退赛保留席位与统计、Kick AI/已完赛返回 409 CANNOT_KICK_AFTER_START、Kick 掉线未完赛真人走软退赛；
    /// 3) 全员按时到 Hour 6 解锁屏障仅置 AllReadyUnlocked=true（TimedOutUnlocked 必须为 false、StatusFor.ForcedTransition=false）；
    /// 4) 8 写线程 × 8 读线程并发高频改写 RunState.PlayerAttributes/(Day,Hour) 与读 EvaluateRoundBarrier / StatusFor / TempoNet.BuildActiveRunDto，
    ///    验证 0 异常且 0 跨天 (Day, Hour) 撕裂态。
    /// </summary>
    [Fact]
    public async Task D6_P1_07_D8_P1_02_LobbyCancelJoin_PostStartSoftLeave_AllReadyNotTimeout_AndConcurrentSummaryRead()
    {
        string dbPath = CreateTempDbPath("w2b_lobby_conc");
        var attachedSids = new List<string>();
        try
        {
            using var db = new Persistence(dbPath);
            var accounts = new AccountStore(db);
            var lobbies = new LobbyStore(db);
            var sessions = new SessionStore();

            var host = accounts.GetOrCreateByName("W2B_Host");
            var guestA = accounts.GetOrCreateByName("W2B_GuestA");
            var guestB = accounts.GetOrCreateByName("W2B_GuestB");
            var lateJoiner = accounts.GetOrCreateByName("W2B_Late");

            string hostAid = host.AccountId.ToString("N");
            string guestAAid = guestA.AccountId.ToString("N");
            string guestBAid = guestB.AccountId.ToString("N");

            // 1. 已取消房间严禁加入
            var cancelledLobby = lobbies.Create(host, "CancelledRoom", "Tournament", playerCap: 4, isPublic: true);
            cancelledLobby.IsCancelled = true;
            var joinCancelled = lobbies.JoinWithResult(cancelledLobby.Code, lateJoiner);
            Assert.False(joinCancelled.Ok);
            Assert.Equal("LOBBY_NOT_FOUND", joinCancelled.ErrorCode);

            // 2. 正常创建 4 人房（3 真人 + 1 AI），开赛
            var lobby = lobbies.Create(host, "Wave2B_Tourney", "Tournament", playerCap: 4, isPublic: true);
            Assert.True(lobbies.JoinWithResult(lobby.Code, guestA).Ok);
            Assert.True(lobbies.JoinWithResult(lobby.Code, guestB).Ok);
            Guid tsid = Guid.NewGuid();
            lobbies.StartCountdown(lobby.LobbyId, tsid);
            Assert.Equal(4, lobby.Members.Count);

            string aiAccountId = lobby.Members.First(m => m.IsAi).AccountId;

            // 开赛后踢 AI 必须被拒绝（CANNOT_KICK_AFTER_START）
            var kickAi = lobbies.KickMember(lobby, hostAid, aiAccountId);
            Assert.False(kickAi.Ok);
            Assert.Equal("CANNOT_KICK_AFTER_START", kickAi.ErrorCode);
            Assert.Equal(4, lobby.Members.Count);

            // 开赛后 GuestB 主动 Leave：必须走软退赛保留席位，不物理删除
            lobbies.Leave(guestBAid);
            Assert.Equal(4, lobby.Members.Count);
            var memB = lobbies.FindMember(lobby, guestBAid);
            Assert.NotNull(memB);
            Assert.Equal("left", memB!.ConnectionState);
            Assert.True(memB.HasFinishedRun);
            Assert.True(memB.FinalIsConceded);
            Assert.True(memB.FinalIsEliminated);
            Assert.Equal(0, memB.FinalPrestige);

            // 开赛后对已完赛/已退赛成员调用 KickMember 必返回 CANNOT_KICK_AFTER_START
            var kickFinished = lobbies.KickMember(lobby, hostAid, guestBAid);
            Assert.False(kickFinished.Ok);
            Assert.Equal("CANNOT_KICK_AFTER_START", kickFinished.ErrorCode);
            Assert.Equal(4, lobby.Members.Count);

            // 退赛者重发心跳 Touch 绝不可被误改回 online
            lobbies.Touch(guestBAid);
            Assert.Equal("left", memB.ConnectionState);

            // 3. 为剩余 2 名存活真人 Host 与 GuestA 挂载 RunState，全部按时抵达 Day 1 Hour 6
            var hostRun = new RunState
            {
                RunId = Guid.NewGuid().ToString("N"),
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                AccountId = hostAid,
                Username = host.Username,
                TournamentSessionId = tsid,
                Day = 1,
                Hour = 6,
                State = ERunState.Choice,
            };
            hostRun.PublishPublicSummary();

            var guestARun = new RunState
            {
                RunId = Guid.NewGuid().ToString("N"),
                Hero = EHero.Dooley,
                PlayMode = EPlayMode.Tournament,
                AccountId = guestAAid,
                Username = guestA.Username,
                TournamentSessionId = tsid,
                Day = 1,
                Hour = 6,
                State = ERunState.Choice,
            };
            guestARun.PublishPublicSummary();

            var hostSess = sessions.Create(hostAid, host.Username, new InitializeRunCommand
            {
                PlayMode = EPlayMode.Tournament,
                SelectedHero = EHero.Vanessa,
                TournamentSessionId = tsid,
            });
            var guestASess = sessions.Create(guestAAid, guestA.Username, new InitializeRunCommand
            {
                PlayMode = EPlayMode.Tournament,
                SelectedHero = EHero.Dooley,
                TournamentSessionId = tsid,
            });
            attachedSids.Add(hostSess.Sid);
            attachedSids.Add(guestASess.Sid);
            SessionRegistry.Attach(hostSess.Sid, hostRun, null!);
            SessionRegistry.Attach(guestASess.Sid, guestARun, null!);

            // 先制造“1 人就位、1 人未就位”以生成 Day 1 屏障记录，随后第 2 人按时就位解锁
            guestARun.Hour = 3;
            guestARun.PublishPublicSummary();
            var waitDecision = lobbies.EvaluateRoundBarrier(lobby, day: 1, selfAccountId: hostAid, selfRun: hostRun, sessions: sessions);
            Assert.False(waitDecision.CanStartPvp);
            Assert.True(waitDecision.WaitingForOtherPlayers);
            Assert.True(lobby.RoundBarriers.ContainsKey(1u));

            guestARun.Hour = 6;
            guestARun.PublishPublicSummary();
            var allReadyDecision = lobbies.EvaluateRoundBarrier(lobby, day: 1, selfAccountId: hostAid, selfRun: hostRun, sessions: sessions);
            Assert.True(allReadyDecision.CanStartPvp);
            Assert.False(allReadyDecision.WaitingForOtherPlayers);
            Assert.False(allReadyDecision.TimedOutUnlocked, "全员按时就位绝不可误标 TimedOutUnlocked=true");
            Assert.True(lobby.RoundBarriers[1u].AllReadyUnlocked, "全员按时就位必须标记 AllReadyUnlocked=true");
            Assert.False(lobby.RoundBarriers[1u].TimedOutUnlocked, "屏障持久化状态绝不可误标 TimedOutUnlocked=true");

            // StatusFor 返回 ForcedTransition 必须为 false
            string statusJson = JsonSerializer.Serialize(TournamentEndpoints.StatusFor(host, lobbies, sessions));
            using (var doc = JsonDocument.Parse(statusJson))
            {
                Assert.False(doc.RootElement.GetProperty("ForcedTransition").GetBoolean());
                Assert.True(doc.RootElement.GetProperty("CanStartPvp").GetBoolean());
            }

            // 4. D8-P1-02 高并发压测：8 写线程改写 RunState 字典与 (Day, Hour) 配对状态，8 读线程连续 500 轮读取
            //    强制不变量：偶数天必为 Hour==2，奇数天必为 Hour==6 —— 任何一次 (Day, Hour) 撕裂或 Dictionary 并发异常都会立即被捕获！
            var errors = new ConcurrentQueue<Exception>();
            var tornReads = new ConcurrentQueue<string>();
            using var cts = new CancellationTokenSource();

            var runLocks = new[] { new object(), new object() };
            var targetRuns = new[] { hostRun, guestARun };
            hostRun.Day = 1; hostRun.Hour = 6; hostRun.PublishPublicSummary();
            guestARun.Day = 1; guestARun.Hour = 6; guestARun.PublishPublicSummary();

            var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
            {
                var rng = new Random(1000 + w);
                int idx = w % 2;
                var run = targetRuns[idx];
                var gate = runLocks[idx];
                try
                {
                    for (int iter = 0; iter < 500 && !cts.IsCancellationRequested; iter++)
                    {
                        lock (gate)
                        {
                            uint nextDay = (uint)(iter % 20 + 1);
                            uint nextHour = (nextDay % 2 == 0) ? 2u : 6u;
                            run.Day = nextDay;
                            run.Hour = nextHour;
                            run.PlayerAttributes[EPlayerAttributeType.Prestige] = 1 + (iter % 20);
                            run.PlayerAttributes[EPlayerAttributeType.Gold] = rng.Next(0, 200);
                            if ((iter & 1) == 0)
                                run.PlayerAttributes[EPlayerAttributeType.Shield] = iter;
                            else
                                run.PlayerAttributes.Remove(EPlayerAttributeType.Shield);
                            run.PublishPublicSummary();
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            })).ToArray();

            var readers = Enumerable.Range(0, 8).Select(r => Task.Run(() =>
            {
                try
                {
                    for (int iter = 0; iter < 500 && !cts.IsCancellationRequested; iter++)
                    {
                        _ = lobbies.EvaluateRoundBarrier(lobby, day: 1, selfAccountId: hostAid, selfRun: null, sessions: sessions, startOrAdvanceCountdown: false);

                        string sj = JsonSerializer.Serialize(TournamentEndpoints.StatusFor(r % 2 == 0 ? host : guestA, lobbies, sessions));
                        using (var doc = JsonDocument.Parse(sj))
                        {
                            foreach (var p in doc.RootElement.GetProperty("Players").EnumerateArray())
                            {
                                string pid = p.GetProperty("AccountId").GetString() ?? "";
                                if (pid.Equals(hostAid, StringComparison.OrdinalIgnoreCase) ||
                                    pid.Equals(guestAAid, StringComparison.OrdinalIgnoreCase))
                                {
                                    uint d = p.GetProperty("CurrentDay").GetUInt32();
                                    uint h = p.GetProperty("CurrentHour").GetUInt32();
                                    uint expectedH = (d % 2 == 0) ? 2u : 6u;
                                    if (h != expectedH)
                                    {
                                        tornReads.Enqueue($"StatusFor torn read for {pid}: Day={d}, Hour={h}, expected={expectedH}");
                                    }
                                }
                            }
                        }

                        var dto = TempoNetEndpoints.BuildActiveRunDto(r % 2 == 0 ? host : guestA, sessions, db);
                        Assert.NotNull(dto);
                        string dj = JsonSerializer.Serialize(dto);
                        using (var ddoc = JsonDocument.Parse(dj))
                        {
                            uint d = ddoc.RootElement.GetProperty("Day").GetUInt32();
                            uint h = ddoc.RootElement.GetProperty("Hour").GetUInt32();
                            uint expectedH = (d % 2 == 0) ? 2u : 6u;
                            if (h != expectedH)
                            {
                                tornReads.Enqueue($"TempoNet torn read: Day={d}, Hour={h}, expected={expectedH}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            })).ToArray();

            await Task.WhenAll(writers.Concat(readers).ToArray());

            Assert.Empty(errors);
            Assert.Empty(tornReads);

            // 5. 开赛后对掉线未完赛真人调用 KickMember：必须走软退赛保留席位
            var memA = lobbies.FindMember(lobby, guestAAid)!;
            memA.ConnectionState = "disconnected";
            var kickOffline = lobbies.KickMember(lobby, hostAid, guestAAid);
            Assert.True(kickOffline.Ok);
            Assert.Equal(4, lobby.Members.Count);
            Assert.Equal("left", memA.ConnectionState);
            Assert.True(memA.HasFinishedRun);
            Assert.True(memA.FinalIsEliminated);
        }
        finally
        {
            foreach (var sid in attachedSids)
                SessionRegistry.Detach(sid);
            TryDeleteFile(dbPath);
        }
    }

    /// <summary>
    /// #18 [D6-P1-11]：
    /// 1) 锦标赛完赛入 tournament:s2，记录真实 ElapsedSeconds / Prestige；
    /// 2) 确定性 runKey 幂等不增行；
    /// 3) 跨榜隔离首次尝试（先写 ranked + 昨日 daily，今日 daily 第 1 局 IsFirstAttempt=true，第 2 局 false，重复上报第 1 局不覆写）；
    /// 4) 单账号多局按最高分去重，total/myRank 与去重列表完全一致；
    /// 5) IsHttpCheatBlocked 6 级优先级双向全验证（SandboxLab 放行，8 大正式模式全拦，BAZAAR_DISABLE_CHEATS 覆盖 BAZAAR_ALLOW_CHEATS_ALL_MODES）。
    /// </summary>
    [Fact]
    public void D6_P1_11_Leaderboard_TournamentPopulated_IdempotentRunKey_PerAccountDedup_RealFields_AndCheatBlocked()
    {
        string dbPath = CreateTempDbPath("w2b_leaderboard");
        string? origDisable = Environment.GetEnvironmentVariable("BAZAAR_DISABLE_CHEATS");
        string? origAllowAll = Environment.GetEnvironmentVariable("BAZAAR_ALLOW_CHEATS_ALL_MODES");
        try
        {
            using var db = new Persistence(dbPath);
            var sse = new SseEventHub();
            var social = new SocialAndReplayService(db, sse);

            string acc1 = Guid.NewGuid().ToString("N");
            string acc2 = Guid.NewGuid().ToString("N");

            // 1. 锦标赛入 tournament:s2 + 真实 ElapsedSeconds/Prestige + 确定性 runKey 幂等
            string tourneyRunId = "run_tourney_001";
            social.RecordRunLeaderboard(new RunLeaderboardSubmission(
                ModeKey: "tournament",
                SeasonOrDateKey: "s2",
                AccountId: acc1,
                Username: "ChampOne",
                Hero: "Vanessa",
                Score: 2400,
                DayReached: 12,
                Victories: 10,
                Defeats: 1,
                ScoreMultiplier: 1.0,
                FinalBoardSummaryJson: "{}",
                RunId: tourneyRunId,
                ElapsedSeconds: 615,
                Prestige: 14));
            social.RecordRunLeaderboard(new RunLeaderboardSubmission(
                ModeKey: "tournament",
                SeasonOrDateKey: "s2",
                AccountId: acc1,
                Username: "ChampOne",
                Hero: "Vanessa",
                Score: 2450,
                DayReached: 12,
                Victories: 10,
                Defeats: 1,
                ScoreMultiplier: 1.0,
                FinalBoardSummaryJson: "{}",
                RunId: tourneyRunId,
                ElapsedSeconds: 620,
                Prestige: 14));

            var (tItems, tTotal) = db.QueryLeaderboard("tournament:s2", page: 1, limit: 20);
            Assert.Equal(1, tTotal);
            Assert.Single(tItems);
            Assert.Equal(2450, tItems[0].Score);
            Assert.Equal(620, tItems[0].ElapsedSeconds);
            Assert.Equal(14, tItems[0].Prestige);
            Assert.True(tItems[0].IsFirstAttempt);

            // 2. 跨榜隔离首次尝试：先写 ranked 与昨日 daily，再写今日 daily 第 1 局与第 2 局
            social.RecordRunLeaderboard(new RunLeaderboardSubmission("standard", "2", acc1, "ChampOne", "Vanessa", 1500, 10, 10, 0, 1.0, FinalBoardSummaryJson: "{}", RunId: "run_ranked_1", ElapsedSeconds: 300, Prestige: 12));
            social.RecordRunLeaderboard(new RunLeaderboardSubmission("daily", "2026-01-01", acc1, "ChampOne", "Vanessa", 1100, 10, 8, 2, 1.0, FinalBoardSummaryJson: "{}", RunId: "run_daily_yest", ElapsedSeconds: 280, Prestige: 5));

            social.RecordRunLeaderboard(new RunLeaderboardSubmission("daily", "2026-01-02", acc1, "ChampOne", "Vanessa", 1800, 11, 9, 2, 1.0, FinalBoardSummaryJson: "{}", RunId: "run_daily_today_1", ElapsedSeconds: 400, Prestige: 9));
            social.RecordRunLeaderboard(new RunLeaderboardSubmission("daily", "2026-01-02", acc1, "ChampOne", "Vanessa", 1850, 11, 9, 2, 1.0, FinalBoardSummaryJson: "{}", RunId: "run_daily_today_1", ElapsedSeconds: 405, Prestige: 9));
            social.RecordRunLeaderboard(new RunLeaderboardSubmission("daily", "2026-01-02", acc1, "ChampOne", "Vanessa", 2100, 12, 10, 2, 1.0, FinalBoardSummaryJson: "{}", RunId: "run_daily_today_2", ElapsedSeconds: 410, Prestige: 15));

            var (dailyFirstOnly, dailyFirstTotal) = db.QueryLeaderboard("daily:2026-01-02", firstAttemptOnly: true, page: 1, limit: 20);
            Assert.Equal(1, dailyFirstTotal);
            Assert.Single(dailyFirstOnly);
            Assert.Equal("run_daily_today_1", dailyFirstOnly[0].RunId);
            Assert.True(dailyFirstOnly[0].IsFirstAttempt);
            Assert.Equal(1850, dailyFirstOnly[0].Score);

            var (dailyAll, dailyAllTotal) = db.QueryLeaderboard("daily:2026-01-02", firstAttemptOnly: false, page: 1, limit: 20);
            Assert.Equal(1, dailyAllTotal);
            Assert.Single(dailyAll);
            Assert.Equal("run_daily_today_2", dailyAll[0].RunId);
            Assert.False(dailyAll[0].IsFirstAttempt);
            Assert.Equal(2100, dailyAll[0].Score);

            // 3. 单账号多局去重 + total 与 myRank 一致性（500 / 1200 / 800 vs acc2=900）
            social.RecordRunLeaderboard(new RunLeaderboardSubmission("endless_tower", "all", acc1, "ChampOne", "Pygmalien", 500, 12, 10, 2, 1.0, FinalBoardSummaryJson: "{}", RunId: "e1", ElapsedSeconds: 200, Prestige: 10));
            social.RecordRunLeaderboard(new RunLeaderboardSubmission("endless_tower", "all", acc1, "ChampOne", "Pygmalien", 1200, 25, 22, 3, 1.0, FinalBoardSummaryJson: "{}", RunId: "e2", ElapsedSeconds: 500, Prestige: 18));
            social.RecordRunLeaderboard(new RunLeaderboardSubmission("endless_tower", "all", acc1, "ChampOne", "Pygmalien", 800, 18, 15, 3, 1.0, FinalBoardSummaryJson: "{}", RunId: "e3", ElapsedSeconds: 350, Prestige: 12));
            social.RecordRunLeaderboard(new RunLeaderboardSubmission("endless_tower", "all", acc2, "RunnerUp", "Dooley", 900, 20, 17, 3, 1.0, FinalBoardSummaryJson: "{}", RunId: "e4", ElapsedSeconds: 420, Prestige: 11));

            var (endlessItems, endlessTotal) = db.QueryLeaderboard("endless:all", page: 1, limit: 20);
            Assert.Equal(2, endlessTotal);
            Assert.Equal(2, endlessItems.Count);
            Assert.Equal(acc1, endlessItems[0].AccountId);
            Assert.Equal(1200, endlessItems[0].Score);
            Assert.Equal(acc2, endlessItems[1].AccountId);
            Assert.Equal(900, endlessItems[1].Score);

            var (best1, rank1) = db.QueryAccountLeaderboardBest("endless:all", acc1);
            var (best2, rank2) = db.QueryAccountLeaderboardBest("endless:all", acc2);
            Assert.NotNull(best1);
            Assert.Equal(1, rank1);
            Assert.Equal(1200, best1!.Score);
            Assert.NotNull(best2);
            Assert.Equal(2, rank2);
            Assert.Equal(900, best2!.Score);

            // 4. IsHttpCheatBlocked 6 级优先级双向全验证
            Environment.SetEnvironmentVariable("BAZAAR_DISABLE_CHEATS", null);
            Environment.SetEnvironmentVariable("BAZAAR_ALLOW_CHEATS_ALL_MODES", null);

            static RunState MakeRun(EPlayMode pm, ESpecialModeId sm)
            {
                var r = new RunState
                {
                    RunId = Guid.NewGuid().ToString("N"),
                    Hero = EHero.Vanessa,
                    PlayMode = pm,
                };
                r.CustomRules.SpecialModeId = sm;
                return r;
            }

            static CheatCommand MakeCheat(string action) => new() { Args = new List<string> { action } };

            var sandboxRun = MakeRun(EPlayMode.Unranked, ESpecialModeId.SandboxLab);
            Assert.False(GameServerEndpoints.IsHttpCheatBlocked(sandboxRun, MakeCheat("give_gold")));

            var blockedCases = new[]
            {
                MakeRun(EPlayMode.Unranked, ESpecialModeId.Standard),
                MakeRun(EPlayMode.Ranked, ESpecialModeId.Standard),
                MakeRun(EPlayMode.Tournament, ESpecialModeId.Standard),
                MakeRun(EPlayMode.Unranked, ESpecialModeId.DailyChallenge),
                MakeRun(EPlayMode.Unranked, ESpecialModeId.EndlessTower),
                MakeRun(EPlayMode.Unranked, ESpecialModeId.BossRush),
                MakeRun(EPlayMode.Unranked, ESpecialModeId.MutatorChallenge),
                MakeRun(EPlayMode.Unranked, ESpecialModeId.CrossHeroMayhem),
            };
            foreach (var r in blockedCases)
            {
                Assert.True(GameServerEndpoints.IsHttpCheatBlocked(r, MakeCheat("give_gold")),
                    $"默认环境必须拦截 {r.PlayMode}/{r.CustomRules.SpecialModeId} 的 HTTP 作弊指令");
            }

            // 同时设置 DISABLE=1 与 ALLOW_ALL=1：DISABLE 严格优先，仅豁免 SandboxLab 的 sandbox_sync
            Environment.SetEnvironmentVariable("BAZAAR_DISABLE_CHEATS", "1");
            Environment.SetEnvironmentVariable("BAZAAR_ALLOW_CHEATS_ALL_MODES", "1");
            Assert.False(GameServerEndpoints.IsHttpCheatBlocked(sandboxRun, MakeCheat("sandbox_sync")));
            Assert.True(GameServerEndpoints.IsHttpCheatBlocked(sandboxRun, MakeCheat("give_gold")));
            Assert.True(GameServerEndpoints.IsHttpCheatBlocked(blockedCases[0], MakeCheat("sandbox_sync")));
            Assert.True(GameServerEndpoints.IsHttpCheatBlocked(blockedCases[0], MakeCheat("give_gold")));

            // 仅设置 ALLOW_ALL=1（无 DISABLE）：放行非沙盒模式
            Environment.SetEnvironmentVariable("BAZAAR_DISABLE_CHEATS", null);
            Assert.False(GameServerEndpoints.IsHttpCheatBlocked(blockedCases[0], MakeCheat("give_gold")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_DISABLE_CHEATS", origDisable);
            Environment.SetEnvironmentVariable("BAZAAR_ALLOW_CHEATS_ALL_MODES", origAllowAll);
            TryDeleteFile(dbPath);
        }
    }

    /// <summary>
    /// #19 [D6-P1-12 + P0-26 + D6-P1-16]：
    /// 1) 同步 PvP 平局调 TournamentMatchmaker.RecordHumanMatchResolution(..., "__draw__", ...) 必须正确记 Winner=Loser="__draw__"；
    /// 2) SaveCombatReplay 完整持久化真实 TournamentSessionId、P2AccountId、WinnerSide="Draw"、VisualBindingsJson 与双键名摘要；
    /// 3) P2 账号经 QueryReplaysByAccount 必须检索到该场战报。
    /// </summary>
    [Fact]
    public void D6_P1_12_P0_26_D6_P1_16_ReplayPersistsTournamentP2DrawVisualBindings_AndMatchmakerRecordsDraw()
    {
        string dbPath = CreateTempDbPath("w2b_replay_draw");
        try
        {
            using var db = new Persistence(dbPath);
            var sse = new SseEventHub();
            var social = new SocialAndReplayService(db, sse);

            string p1 = Guid.NewGuid().ToString("N");
            string p2 = Guid.NewGuid().ToString("N");
            string tsid = Guid.NewGuid().ToString("D");

            // 1. 同步 PvP 平局写入匹配器
            var matchmaker = new TournamentMatchmaker("lobby_draw_test");
            var res = matchmaker.RecordHumanMatchResolution(day: 3, p1, p2, winnerAccountId: "__draw__", loserPrestigeDamage: 0, battleSeed: 778899);
            Assert.Equal("__draw__", res.WinnerAccountId);
            Assert.Equal("__draw__", res.LoserAccountId);
            Assert.Equal(3u, res.Day);

            // 2. 战报持久化：含 TournamentSessionId / OpponentAccountId / IsDraw / VisualBindingsJson / playerItems+playerRemHp 双键名
            string expectedVisualBindings = "{\"custom_card_01\":\"/api/mod-assets/my_mod/sword.png\",\"custom_card_02\":\"/api/mod-assets/my_mod/shield.png\"}";
            string summaryJson = JsonSerializer.Serialize(new
            {
                mode = "tournament",
                hour = 6,
                playerRemHp = 0,
                opponentRemHp = 0,
                playerItems = new[] { new { id = "custom_card_01", name = "Custom Blade" } },
                opponentItems = new[] { new { id = "custom_card_02", name = "Custom Aegis" } },
            });

            string replayId = social.SaveCombatReplay(new CombatReplayRecord(
                ReplayId: "rep_w2b_draw_01",
                RunId: "run_w2b_p1",
                LobbyId: "lobby_draw_test",
                PlayerAccountId: p1,
                PlayerUsername: "Alice",
                PlayerHero: "Vanessa",
                OpponentName: "Bob",
                OpponentHero: "Dooley",
                CombatType: "PVP",
                Day: 3,
                PlayerWon: false,
                DurationMs: 12340,
                Seed: 778899,
                SummaryJson: summaryJson,
                CombatSimMsgPack: new byte[] { 1, 2, 3, 4 },
                TournamentSessionId: tsid,
                OpponentAccountId: p2,
                IsDraw: true,
                VisualBindingsJson: expectedVisualBindings));

            var loaded = db.LoadReplay(replayId);
            Assert.NotNull(loaded);
            Assert.Equal(tsid, loaded!.TournamentSessionId);
            Assert.Equal(p1, loaded.P1AccountId);
            Assert.Equal(p2, loaded.P2AccountId);
            Assert.Equal("Draw", loaded.WinnerSide);
            Assert.Equal("tournament", loaded.ModeId);
            Assert.Equal(expectedVisualBindings, loaded.VisualBindingsJson);
            Assert.Contains("custom_card_01", loaded.P1BoardJson, StringComparison.Ordinal);
            Assert.Contains("custom_card_02", loaded.P2BoardJson, StringComparison.Ordinal);
            Assert.True(loaded.HasCombatSimBlob);

            // P2 视角必须能查到同一场战报
            var p2List = db.QueryReplaysByAccount(p2);
            Assert.Single(p2List);
            Assert.Equal(replayId, p2List[0].ReplayId);
            Assert.Equal("Draw", p2List[0].WinnerSide);
            Assert.Equal(expectedVisualBindings, p2List[0].VisualBindingsJson);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    /// <summary>
    /// #20 [D6-P1-17]：
    /// 1) ResolveEffectivePrestigeBounds 尊重自定义 StartingPrestige=50 与 IronmanNoFates 上限 10；
    /// 2) ResolveRunRowSpecialModeId 准确映射 mutator / mutator_challenge -> MutatorChallenge；
    /// 3) IsSnapshotSandboxRun 基于 JsonDocument 结构化解析，不被昵称含 "SandboxLabPro" 误判，但对真实 SandboxLab 快照返回 true。
    /// </summary>
    [Fact]
    public void D6_P1_17_ActiveSummaryCustomPrestige_MutatorModeMapping_AndJsonSandboxCheck()
    {
        var defaultMode = new GameModeConfig();

        // 1. 真实 StartingPrestige 边界
        var custom50 = new CustomRunRuleSet { StartingPrestige = 50, SpecialModeId = ESpecialModeId.Standard };
        var (init50, max50) = GameServerEndpoints.ResolveEffectivePrestigeBounds(custom50, defaultMode, currentPrestige: 42);
        Assert.Equal(50, init50);
        Assert.Equal(50, max50);

        var (init50Boosted, max50Boosted) = GameServerEndpoints.ResolveEffectivePrestigeBounds(custom50, defaultMode, currentPrestige: 68);
        Assert.Equal(50, init50Boosted);
        Assert.Equal(68, max50Boosted);

        var ironman = new CustomRunRuleSet
        {
            StartingPrestige = 50,
            MutatorIds = new List<string> { MutatorCatalog.IronmanNoFates },
        };
        var (initIron, maxIron) = GameServerEndpoints.ResolveEffectivePrestigeBounds(ironman, defaultMode, currentPrestige: 8);
        Assert.Equal(10, initIron);
        Assert.Equal(20, maxIron); // baseMax = GameDefaults.StartingPrestige(20) >= initIron(10)

        // 2. mutator 映射
        static Persistence.RunRow MakeRow(string modeId, string snapshot = "{}")
            => new(
                RunId: "r1",
                AccountId: Guid.NewGuid(),
                Hero: EHero.Vanessa,
                PlayMode: EPlayMode.Unranked,
                GameModeId: Guid.Empty,
                Day: 1,
                Hour: 1,
                Victories: 0,
                Defeats: 0,
                Prestige: GameDefaults.StartingPrestige,
                Gold: GameDefaults.StartingGold,
                Income: GameDefaults.StartingIncome,
                State: ERunState.Choice,
                SavedAt: DateTime.UtcNow,
                Snapshot: snapshot,
                ModeId: modeId);

        Assert.Equal("MutatorChallenge", GameServerEndpoints.ResolveRunRowSpecialModeId(MakeRow("mutator")));
        Assert.Equal("MutatorChallenge", GameServerEndpoints.ResolveRunRowSpecialModeId(MakeRow("mutator_challenge")));
        Assert.Equal("MutatorChallenge", GameServerEndpoints.ResolveRunRowSpecialModeId(MakeRow("mutatorchallenge")));

        // 3. 结构化 JSON 沙盒判定（拒绝昵称误报，精准识别真 SandboxLab）
        string fakeNameStandardSnap = JsonSerializer.Serialize(new
        {
            Username = "SandboxLabPro_Player",
            CustomRules = new { SpecialModeId = "Standard" },
        });
        Assert.False(GameServerEndpoints.IsSnapshotSandboxRun(fakeNameStandardSnap));

        string realSandboxStringSnap = JsonSerializer.Serialize(new
        {
            Username = "NormalPlayer",
            CustomRules = new { SpecialModeId = "SandboxLab" },
        });
        Assert.True(GameServerEndpoints.IsSnapshotSandboxRun(realSandboxStringSnap));

        string realSandboxEnumIntSnap = JsonSerializer.Serialize(new
        {
            Username = "NormalPlayer",
            CustomRules = new { SpecialModeId = (int)ESpecialModeId.SandboxLab },
        });
        Assert.True(GameServerEndpoints.IsSnapshotSandboxRun(realSandboxEnumIntSnap));
    }

    /// <summary>
    /// #21 [D3-P1-12]：
    /// 验证 X-Mod-Set-Hash 计算器将本局勾选的社区 Mod 列表 SelectedModIds 纳入哈希，
    /// 同集合乱序/重复/大小写差异确定性归一化一致，不同 Mod 集合产生严格不同哈希。
    /// </summary>
    [Fact]
    public void D3_P1_12_XModSetHashHeader_IncludesSelectedModIds()
    {
        string hEmpty = GameServerEndpoints.ComputeEffectiveModSetHashLocal(Array.Empty<string>(), modHost: null);
        string hNull = GameServerEndpoints.ComputeEffectiveModSetHashLocal(null, modHost: null);
        Assert.Equal(hEmpty, hNull);

        string hSetA = GameServerEndpoints.ComputeEffectiveModSetHashLocal(new[] { "mod.alpha", "mod.beta" }, modHost: null);
        string hSetAPermuted = GameServerEndpoints.ComputeEffectiveModSetHashLocal(new[] { "MOD.BETA", "mod.alpha", "mod.alpha", " " }, modHost: null);
        string hSetB = GameServerEndpoints.ComputeEffectiveModSetHashLocal(new[] { "mod.alpha", "mod.gamma" }, modHost: null);

        Assert.False(string.IsNullOrWhiteSpace(hSetA));
        Assert.NotEqual(hEmpty, hSetA);
        Assert.Equal(hSetA, hSetAPermuted);
        Assert.NotEqual(hSetA, hSetB);
    }
}
