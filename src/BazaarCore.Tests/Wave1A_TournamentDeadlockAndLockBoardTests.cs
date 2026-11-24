using System.Diagnostics;
using BazaarServer.Accounts;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1A 回归测试（文件 4）：覆盖 P0-38（RunWatchdogTick 与 RecordOrGetResolvedMatch 1000 轮高并发 0 死锁）、
/// P0-39（LockPlayerBoardForDay 双边并发锁板与裁决不互相抹空/不污染已裁决胜负、重启回源零缓存串台）、
/// D8-P1-03 横向点（JoinWithResult 房间取消竞态拦截与密码验算不阻塞同房 FindMember）。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1A_TournamentDeadlockAndLockBoardTests
{
    private static LockedPvpBoard MakeBoard(string accountId, string username, uint day, string tag)
        => new(
            AccountId: accountId,
            Username: username,
            Hero: "Vanessa",
            Day: day,
            HealthMax: 250 + (int)day * 100,
            Prestige: 20,
            Level: (int)day,
            Victories: day - 1,
            Gold: 10,
            Income: 5,
            RunSnapshotJson: $"{{\"tag\":\"{tag}\",\"day\":{day}}}");

    [Fact]
    public async Task WatchdogTick_And_RecordOrGetResolvedMatch_HighConcurrency1000Rounds_ZeroDeadlock()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_p038_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Persistence? db = null;
        AccountStore? accounts = null;
        try
        {
            db = new Persistence(Path.Combine(tempDir, "state.db"));
            accounts = new AccountStore(db);
            var sessions = new SessionStore();
            var lobbies = new LobbyStore(db);
            var pvpCoord = new TournamentPvpCoordinator(db, lobbies);

            var pA = accounts.GetOrCreateByName("DeadlockA", "game", "d1");
            var pB = accounts.GetOrCreateByName("DeadlockB", "game", "d2");
            var pC = accounts.GetOrCreateByName("DeadlockC", "game", "d3");
            var pD = accounts.GetOrCreateByName("DeadlockD", "game", "d4");

            var lobby = lobbies.Create(pA, "ABBA压测房", "Tournament", playerCap: 4, isPublic: true);
            Assert.True(lobbies.JoinWithResult(lobby.Code, pB).Ok);
            Assert.True(lobbies.JoinWithResult(lobby.Code, pC).Ok);
            Assert.True(lobbies.JoinWithResult(lobby.Code, pD).Ok);

            Guid tsid = Guid.NewGuid();
            lobbies.StartCountdown(lobby.LobbyId, tsid);

            // 预冻结第 1~3 天配对，确保真人配对全部命中 pairLock
            string aidA = pA.AccountId.ToString("N");
            string aidB = pB.AccountId.ToString("N");
            string aidC = pC.AccountId.ToString("N");
            string aidD = pD.AccountId.ToString("N");
            for (uint d = 1; d <= 3; d++)
            {
                lobbies.EnsureAndGetAssignment(lobby, d, aidA, sessions);
                lobbies.EnsureAndGetAssignment(lobby, d, aidB, sessions);
            }

            // 构造看门狗触发条件：A 与 C 领先在第 3 天在线，B 与 D 滞后在第 1 天并超时进入 ai_takeover
            DateTime oldTime = DateTime.UtcNow.AddMinutes(-10);
            lock (lobby.Members)
            {
                foreach (var m in lobby.Members)
                {
                    if (m.AccountId == aidA || m.AccountId == aidC)
                    {
                        m.CurrentDay = 3;
                        m.ConnectionState = "online";
                        m.LastSeenAt = DateTime.UtcNow;
                    }
                    else
                    {
                        m.CurrentDay = 1;
                        m.ConnectionState = "disconnected";
                        m.LastSeenAt = oldTime;
                    }
                }
            }

            var assignDay1B = lobby.Matchmaker.GetAssignment(aidB, 1u);
            Assert.NotNull(assignDay1B);
            string oppB = assignDay1B!.OpponentAccountId;

            // 4 线程循环跑 RunWatchdogTick + 4 线程循环跑 RecordOrGetResolvedMatch，各 1000 轮（设 15s 死锁超时栅栏）
            using var startGate = new ManualResetEventSlim(false);
            var watchdogTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < 250; i++)
                {
                    lobbies.RunWatchdogTick(sessions, pvpCoord);
                }
            })).ToArray();

            var pvpTasks = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < 250; i++)
                {
                    uint day = (uint)((i % 3) + 1);
                    var assign = lobby.Matchmaker.GetAssignment(aidB, day);
                    string targetOpp = assign?.OpponentAccountId ?? oppB;
                    var proposed = new SymmetricHumanPvpOutcome(
                        WinnerAccountId: (t % 2 == 0) ? aidB : targetOpp,
                        LoserAccountId: (t % 2 == 0) ? targetOpp : aidB,
                        BattleSeed: 1000 + (int)day,
                        LoserPrestigeDamage: (int)day,
                        ReplayId: $"rep_d{day}_{t}",
                        CachedCombatSimForP1: null,
                        CachedCombatSimForP2: null);
                    var resolved = pvpCoord.RecordOrGetResolvedMatch(tsid, day, aidB, targetOpp, proposed);
                    Assert.False(string.IsNullOrWhiteSpace(resolved.WinnerAccountId));
                }
            })).ToArray();

            startGate.Set();
            var allWork = Task.WhenAll(watchdogTasks.Concat(pvpTasks));
            var winner = await Task.WhenAny(allWork, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(allWork, winner);
            await allWork;

            // 校验第 1 天对称裁决唯一性
            var finalDay1 = pvpCoord.TryGetResolvedMatch(tsid, 1u, aidB, oppB);
            Assert.NotNull(finalDay1);
            var finalDay1Rev = pvpCoord.TryGetResolvedMatch(tsid, 1u, oppB, aidB);
            Assert.Equal(finalDay1!.WinnerAccountId, finalDay1Rev!.WinnerAccountId);
        }
        finally
        {
            accounts?.Dispose();
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LockPlayerBoardForDay_ConcurrentDualSideAndResolve_PreservesBothBoardsAndWinner()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_p039_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Persistence? db = null;
        AccountStore? accounts = null;
        try
        {
            db = new Persistence(Path.Combine(tempDir, "state.db"));
            accounts = new AccountStore(db);
            var sessions = new SessionStore();
            var lobbies = new LobbyStore(db);
            var pvpCoord = new TournamentPvpCoordinator(db, lobbies);

            var p1Acc = accounts.GetOrCreateByName("LockSideOne", "game", "d1");
            var p2Acc = accounts.GetOrCreateByName("LockSideTwo", "game", "d2");
            string aid1 = p1Acc.AccountId.ToString("N");
            string aid2 = p2Acc.AccountId.ToString("N");

            var lobby = lobbies.Create(p1Acc, "双边锁板压测房", "Tournament", playerCap: 2, isPublic: true);
            Assert.True(lobbies.JoinWithResult(lobby.Code, p2Acc).Ok);
            Guid tsid = Guid.NewGuid();
            lobbies.StartCountdown(lobby.LobbyId, tsid);

            // 50 天/轮并发双边锁板 + 裁决测试
            for (uint day = 1; day <= 25; day++)
            {
                lobbies.EnsureAndGetAssignment(lobby, day, aid1, sessions);
                string pairKey = TournamentMatchmaker.CanonicalPairKey(aid1, aid2);

                // 先并发双边锁板，确保 P1 与 P2 互不抹空
                using var gate = new ManualResetEventSlim(false);
                var t1 = Task.Run(() =>
                {
                    gate.Wait();
                    pvpCoord.LockPlayerBoardForDay(tsid, day, MakeBoard(aid1, p1Acc.Username, day, $"p1_d{day}"));
                });
                var t2 = Task.Run(() =>
                {
                    gate.Wait();
                    pvpCoord.LockPlayerBoardForDay(tsid, day, MakeBoard(aid2, p2Acc.Username, day, $"p2_d{day}"));
                });
                gate.Set();
                await Task.WhenAll(t1, t2);

                var rowBeforeResolve = db.LoadTournamentMatchup(lobby.LobbyId, day, pairKey);
                Assert.NotNull(rowBeforeResolve);
                Assert.False(string.IsNullOrWhiteSpace(rowBeforeResolve!.P1LockedBoardJson), $"Day {day} P1 锁板不得被 P2 并发抹空");
                Assert.False(string.IsNullOrWhiteSpace(rowBeforeResolve.P2LockedBoardJson), $"Day {day} P2 锁板不得被 P1 并发抹空");

                // 裁决结果（奇数天正常胜负，偶数天模拟同步 PvP 平局非空哨兵 "__sync_draw__"）
                string winnerId = (day % 2 == 1) ? aid1 : "__sync_draw__";
                var committed = pvpCoord.RecordOrGetResolvedMatch(tsid, day, aid1, aid2, new SymmetricHumanPvpOutcome(
                    WinnerAccountId: winnerId,
                    LoserAccountId: aid2,
                    BattleSeed: 9000 + (int)day,
                    LoserPrestigeDamage: (int)day,
                    ReplayId: $"replay_day_{day}",
                    CachedCombatSimForP1: [1, 2, 3],
                    CachedCombatSimForP2: [4, 5, 6]));

                // 裁决后故意再发迟到 LockPlayerBoardForDay 尝试篡改：SQLite 中已落盘锁板与胜负均必须纹丝不动！
                string origP1Json = rowBeforeResolve.P1LockedBoardJson;
                pvpCoord.LockPlayerBoardForDay(tsid, day, MakeBoard(aid1, p1Acc.Username, day, "tampered_late_board"));

                var rowAfterLateLock = db.LoadTournamentMatchup(lobby.LobbyId, day, pairKey);
                Assert.NotNull(rowAfterLateLock);
                Assert.Equal(committed.WinnerAccountId, rowAfterLateLock!.WinnerAccountId);
                Assert.Equal($"replay_day_{day}", rowAfterLateLock.ReplayId);
                Assert.Equal(origP1Json, rowAfterLateLock.P1LockedBoardJson);
                Assert.False(string.IsNullOrWhiteSpace(rowAfterLateLock.P2LockedBoardJson));
            }

            // 模拟进程重启：新建空白内存缓存的 TournamentPvpCoordinator 回源 SQLite，绝不可混入 proposed.CachedCombatSimForP1/P2！
            var restartedCoord = new TournamentPvpCoordinator(db, lobbies);
            var reloaded = restartedCoord.RecordOrGetResolvedMatch(tsid, 1u, aid1, aid2, new SymmetricHumanPvpOutcome(
                WinnerAccountId: aid2, // 故意传入与 DB 权威胜者相反的提案和假动画字节
                LoserAccountId: aid1,
                BattleSeed: 12345,
                LoserPrestigeDamage: 99,
                ReplayId: "fake_replay",
                CachedCombatSimForP1: [9, 9, 9],
                CachedCombatSimForP2: [8, 8, 8]));

            Assert.Equal(aid1, reloaded.WinnerAccountId);
            Assert.Equal("replay_day_1", reloaded.ReplayId);
            Assert.Null(reloaded.CachedCombatSimForP1);
            Assert.Null(reloaded.CachedCombatSimForP2);
        }
        finally
        {
            accounts?.Dispose();
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task JoinWithResult_CancelledLobbyAndConcurrentPasswordCheck_ShouldNotBlockOrJoin()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_h02_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Persistence? db = null;
        AccountStore? accounts = null;
        try
        {
            db = new Persistence(Path.Combine(tempDir, "state.db"));
            accounts = new AccountStore(db);
            var lobbies = new LobbyStore(db);

            var host = accounts.GetOrCreateByName("PwdRoomHost", "game", "d1");
            var guessers = Enumerable.Range(0, 6)
                .Select(i => accounts.GetOrCreateByName($"PwdGuesser{i}", "game", $"g{i}"))
                .ToArray();

            var lobby = lobbies.Create(host, "密码并发房", "Tournament", playerCap: 4, isPublic: true, password: "RoomSecret99");

            // 1. 6 线程同时以错误密码冲击 JoinWithResult（PBKDF2 已移出 lobby.Members 锁），并发 FindMember 延迟保持极低
            using var gate = new ManualResetEventSlim(false);
            long maxFindLatencyMs = 0;

            var joinTasks = guessers.Select(g => Task.Run(() =>
            {
                gate.Wait();
                var res = lobbies.JoinWithResult(lobby.Code, g, password: "WrongPassword!");
                Assert.False(res.Ok);
                Assert.Equal("INVALID_PASSWORD", res.ErrorCode);
            })).ToArray();

            var probeTask = Task.Run(() =>
            {
                gate.Wait();
                for (int i = 0; i < 100; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var m = lobbies.FindMember(lobby, host.AccountId.ToString("N"));
                    sw.Stop();
                    Assert.NotNull(m);
                    if (sw.ElapsedMilliseconds > maxFindLatencyMs)
                        maxFindLatencyMs = sw.ElapsedMilliseconds;
                    Thread.Sleep(1);
                }
            });

            gate.Set();
            await Task.WhenAll(joinTasks.Append(probeTask));
            Assert.True(maxFindLatencyMs < 100, $"PBKDF2 移出 lobby.Members 后 FindMember 最大延迟应 < 100ms，实际：{maxFindLatencyMs}ms");

            // 2. 取消房间后即使携带正确密码 JoinWithResult 也必返回 LOBBY_NOT_FOUND
            lobby.IsCancelled = true;
            var afterCancel = lobbies.JoinWithResult(lobby.Code, guessers[0], password: "RoomSecret99");
            Assert.False(afterCancel.Ok);
            Assert.Equal("LOBBY_NOT_FOUND", afterCancel.ErrorCode);
        }
        finally
        {
            accounts?.Dispose();
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
