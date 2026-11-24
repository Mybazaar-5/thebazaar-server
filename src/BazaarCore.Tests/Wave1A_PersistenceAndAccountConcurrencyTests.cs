using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.TempoNet;
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
/// Wave 1A 回归测试（文件 6）：覆盖 P0-49（Persistence / AccountStore 防抖定时器防崩与退避重试）、
/// D2-P1-01（终态防复活双层原子锁、正常断线终局快照兼容与 TempoNetEndpoints 退赛会话锁序）、
/// D8-P1-03（AccountStore 无锁 PBKDF2 不阻塞其他账号、失败限流与并发带密注册防免密抢登）。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1A_PersistenceAndAccountConcurrencyTests
{
    private static RunState MakeRun(Guid accountId, string runId, ERunState state = ERunState.Choice, uint day = 3, uint victories = 2)
    {
        return new RunState
        {
            RunId = runId,
            AccountId = accountId.ToString("N"),
            Username = "Tester",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            GameModeId = SharedGameDataFixture.Shared.CreateIsolatedCardPool().DefaultGameModeId,
            Day = day,
            Hour = 2,
            Victories = victories,
            Defeats = 0,
            State = state
        };
    }

    [Fact]
    public async Task Persistence_DebounceTimerTransientFailure_MustSurviveAndRetrySuccessfully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_p049_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "state.db");
        Persistence? db = null;
        try
        {
            db = new Persistence(dbPath);
            int remainingFaults = 2;
            db.BeforeWriteBatchHookForTesting = () =>
            {
                if (Interlocked.Decrement(ref remainingFaults) >= 0)
                    throw new SqliteException("Simulated SQLITE_BUSY transient lock", 5);
            };

            Guid aid = Guid.NewGuid();
            string runId = "run_retry_" + Guid.NewGuid().ToString("N")[..8];
            var run = MakeRun(aid, runId, ERunState.Choice, day: 4, victories: 3);

            db.SaveRunSnapshotDebounced(run, aid.ToString("N"));

            // 等待防抖 1.5s + 2 次指数退避（500ms + 1000ms）后第 3 次成功落盘，进程绝不崩溃
            var sw = Stopwatch.StartNew();
            Persistence.RunRow? loaded = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(8))
            {
                loaded = db.LoadRun(runId);
                if (loaded != null) break;
                await Task.Delay(100);
            }

            Assert.NotNull(loaded);
            Assert.Equal(4u, loaded!.Day);
            Assert.Equal(3u, loaded.Victories);
            Assert.Equal(2, db.DebounceWriteErrorCount);
        }
        finally
        {
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Persistence_MarkRunEndedAndConcede_MustNotResurrectOrResetCareerRecorded_WhileAllowingFinalSnapshot()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_d2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "state.db");
        Persistence? db = null;
        try
        {
            db = new Persistence(dbPath);
            Guid aid = Guid.NewGuid();

            // 1. 正例（兼容 RunEngine:5302 -> GameServerEndpoints:509 断线重连终局快照序）：
            //    先落初始进行态 -> MarkRunEnded("victory") + TryMarkCareerRecorded -> 紧接着 SaveRunSnapshot 写入 EndRunVictory 终局快照
            string runA = "run_victory_" + Guid.NewGuid().ToString("N")[..8];
            var runAState = MakeRun(aid, runA, ERunState.Choice, day: 9, victories: 9);
            db.SaveRunSnapshot(runAState, aid.ToString("N"));

            db.MarkRunEnded(runA, ERunState.EndRunVictory, "victory");
            Assert.True(db.TryMarkCareerRecorded(runA));

            runAState.State = ERunState.EndRunVictory;
            runAState.Day = 10;
            runAState.Victories = 10;
            db.SaveRunSnapshot(runAState, aid.ToString("N"));

            var afterFinal = db.LoadRun(runA);
            Assert.NotNull(afterFinal);
            Assert.True(afterFinal!.IsEnded);
            Assert.Equal(ERunState.EndRunVictory, afterFinal.State);
            Assert.Equal(10u, afterFinal.Victories);
            Assert.True(afterFinal.CareerRecorded, "终局快照不得清零 career_recorded");

            // 2. 反例 1（防迟到非终态防抖/直写快照复活已结束局或清零 career_recorded）
            var staleRunA = MakeRun(aid, runA, ERunState.Choice, day: 8, victories: 8);
            db.SaveRunSnapshotDebounced(staleRunA, aid.ToString("N"));
            db.SaveRunSnapshot(staleRunA, aid.ToString("N"));
            await Task.Delay(1800);

            var checkA = db.LoadRun(runA);
            Assert.NotNull(checkA);
            Assert.True(checkA!.IsEnded, "已结束对局绝不允许被迟到非终态快照复活");
            Assert.Equal(ERunState.EndRunVictory, checkA.State);
            Assert.Equal(10u, checkA.Victories);
            Assert.True(checkA.CareerRecorded, "career_recorded 必须单调不减保持 true");

            // 3. 反例 2（投降局：防抖挂起 -> ConcedeActiveRuns -> 等待 > 1.8s + 尝试再写入进行态与胜局，均被彻底拦截）
            string runB = "run_concede_" + Guid.NewGuid().ToString("N")[..8];
            var runBState = MakeRun(aid, runB, ERunState.Choice, day: 5, victories: 4);
            db.SaveRunSnapshot(runBState, aid.ToString("N"));
            runBState.Day = 6;
            db.SaveRunSnapshotDebounced(runBState, aid.ToString("N"));
            Assert.True(db.PendingDebounceCount >= 1);

            db.ConcedeActiveRuns(aid);
            Assert.True(db.TryMarkCareerRecorded(runB));
            await Task.Delay(1800);

            // 再强行写入非终态和伪造 EndRunVictory
            db.SaveRunSnapshot(MakeRun(aid, runB, ERunState.Choice, day: 7, victories: 6), aid.ToString("N"));
            db.SaveRunSnapshot(MakeRun(aid, runB, ERunState.EndRunVictory, day: 10, victories: 10), aid.ToString("N"));

            var checkB = db.LoadRun(runB);
            Assert.NotNull(checkB);
            Assert.True(checkB!.IsEnded);
            Assert.Equal(ERunState.EndRunDefeat, checkB.State);
            Assert.Equal("conceded", checkB.EndedReason);
            Assert.True(checkB.CareerRecorded);
            Assert.Null(db.LoadLatestActiveRun(aid));
        }
        finally
        {
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TempoNet_DeleteActiveSave_ConcurrentWithCommands_ShouldSerializeWithoutResurrection()
    {
        var fx = SharedGameDataFixture.Shared;
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_tnet_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "state.db");

        var db = new Persistence(dbPath);
        var accounts = new AccountStore(db);
        var sessions = new SessionStore();

        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(sessions);

        var app = builder.Build();
        TempoNetEndpoints.Map(app);
        await app.StartAsync();

        HttpClient? http = null;
        string? sid = null;
        try
        {
            string baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };

            var acc = accounts.GetOrCreateByName("DeleteSaveHero", "game", "dev1");
            string key = acc.AccountId.ToString("N");
            string runId = "run_del_" + Guid.NewGuid().ToString("N")[..8];
            var pool = fx.CreateIsolatedCardPool();
            var run = MakeRun(acc.AccountId, runId, ERunState.Choice, day: 2, victories: 1);
            var engine = new RunEngine(run, pool, fx.CreateMonstersCopy(), fx.LevelUps, fx.Mode, fx.Repo, new PvpGhostPool(), fx.ArchetypeIndex);
            var sess = sessions.Create(key, acc.Username, new InitializeRunCommand { PlayMode = EPlayMode.Unranked, SelectedHero = EHero.Vanessa });
            sid = sess.Sid;
            SessionRegistry.Attach(sess.Sid, run, engine);
            db.SaveRunSnapshot(run, key);

            // 模拟并发命令在 Session.Gate 下执行高频防抖写盘，同时客户端发起 DELETE /api/game/deleteactivesave
            var gateAcquired = new ManualResetEventSlim(false);
            var commandTask = Task.Run(async () =>
            {
                await sess.Gate.WaitAsync();
                try
                {
                    gateAcquired.Set();
                    await Task.Delay(80);
                    run.Day = 3;
                    db.SaveRunSnapshotDebounced(run, key);
                }
                finally
                {
                    sess.Gate.Release();
                }
            });

            Assert.True(gateAcquired.Wait(TimeSpan.FromSeconds(3)));
            using var req = new HttpRequestMessage(HttpMethod.Delete, "/api/game/deleteactivesave");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acc.AccessToken);
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            await commandTask;
            await Task.Delay(1700);

            Assert.Null(sessions.GetByAccount(key));
            Assert.Null(SessionRegistry.Get(sid));
            var row = db.LoadRun(runId);
            Assert.NotNull(row);
            Assert.True(row!.IsEnded, "并发退赛后绝不可被挂起防抖写回复活");
            Assert.Equal("conceded", row.EndedReason);
        }
        finally
        {
            if (sid != null) SessionRegistry.Detach(sid);
            http?.Dispose();
            try { await app.StopAsync(); } catch { }
            try { await app.DisposeAsync(); } catch { }
            db.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AccountStore_ConcurrentLoginAndSetSecret_MustNotBlockOtherAccountsUnderLock()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_acc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Persistence? db = null;
        AccountStore? store = null;
        try
        {
            db = new Persistence(Path.Combine(tempDir, "state.db"));
            store = new AccountStore(db);

            // 创建受口令保护账号 A 与普通账号 B
            var initA = store.LoginByNameWithSecret("HeavyPbkdfUserA", "InitialPass123!");
            Assert.True(initA.Ok);
            Assert.NotNull(initA.Account);
            var accB = store.GetOrCreateByName("FastReaderUserB", "game", "d2");
            Assert.NotNull(accB);

            using var startGate = new ManualResetEventSlim(false);
            long maxReaderLatencyMs = 0;
            int readerOps = 0;

            // 8 线程对账号 A 并发执行错误口令登录（触发 PBKDF2 并在第 11 次起命中限流）
            var attackers = Enumerable.Range(0, 8).Select(idx => Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < 3; i++)
                {
                    var res = store.LoginByNameWithSecret("HeavyPbkdfUserA", $"WrongPass_{idx}_{i}");
                    Assert.False(res.Ok);
                }
            })).ToArray();

            // 同步并发读取/刷新账号 B：验证全局 _lock 未被 PBKDF2 阻塞（单次读延迟应保持毫秒级，而非数百 ms）
            var readerTask = Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < 200; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var found = store.FindByUsername("FastReaderUserB");
                    sw.Stop();
                    Assert.NotNull(found);
                    long elapsed = sw.ElapsedMilliseconds;
                    long cur;
                    do
                    {
                        cur = Volatile.Read(ref maxReaderLatencyMs);
                        if (elapsed <= cur) break;
                    } while (Interlocked.CompareExchange(ref maxReaderLatencyMs, elapsed, cur) != cur);
                    Interlocked.Increment(ref readerOps);
                    Thread.Sleep(1);
                }
            });

            startGate.Set();
            await Task.WhenAll(attackers.Append(readerTask));

            Assert.Equal(200, readerOps);
            Assert.True(maxReaderLatencyMs < 150, $"PBKDF2 移出全局锁后其他账号读取最大延迟应 < 150ms，实际：{maxReaderLatencyMs}ms");

            // 验证失败限流已生效（第 25 次错误登录无需再耗费 PBKDF2，立即被拒且返回限流提示）
            var throttledSw = Stopwatch.StartNew();
            var throttled = store.LoginByNameWithSecret("HeavyPbkdfUserA", "StillWrongPass");
            throttledSw.Stop();
            Assert.False(throttled.Ok);
            Assert.Contains("次数过多", throttled.ErrorZh ?? string.Empty);

            // 验证 SetOrUpdateSecret 无锁改密成功且新口令可用
            var upd = store.SetOrUpdateSecret(initA.Account!, "InitialPass123!", "UpdatedPass456!");
            Assert.True(upd.Ok, upd.ErrorZh);
            // 另用独立账号验证改密后正确登录
            var accC = store.LoginByNameWithSecret("UpdateSecretUserC", "PassOne111!");
            Assert.True(accC.Ok);
            var updC = store.SetOrUpdateSecret(accC.Account!, "PassOne111!", "PassTwo222!");
            Assert.True(updC.Ok);
            Assert.False(store.LoginByNameWithSecret("UpdateSecretUserC", "PassOne111!").Ok);
            Assert.True(store.LoginByNameWithSecret("UpdateSecretUserC", "PassTwo222!").Ok);
        }
        finally
        {
            store?.Dispose();
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AccountStore_ConcurrentPasswordRegistration_MustNotAllowPasswordlessHijack()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_reg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Persistence? db = null;
        AccountStore? store = null;
        try
        {
            db = new Persistence(Path.Combine(tempDir, "state.db"));
            store = new AccountStore(db);

            // 连续 10 轮全新用户名竞态压测：1 条带密注册线程 + 4 条高频免密抢登线程同时起跑
            for (int round = 0; round < 10; round++)
            {
                string freshUser = $"fresh_sec_user_{round}_{Guid.NewGuid():N}"[..24];
                using var barrier = new ManualResetEventSlim(false);
                int passwordlessSucceededAfterPasswordWin = 0;

                var regTask = Task.Run(() =>
                {
                    barrier.Wait();
                    return store.LoginByNameWithSecret(freshUser, "StrongPass123!", allowAutoBindSecret: true);
                });

                var hijackTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    barrier.Wait();
                    bool anyOk = false;
                    // 每线程 2 次免密抢登（4 × 2 + 1 次 postNoPass = 9 < MaxFailedLoginsPerWindow = 10），
                    // 既完整覆盖并发防劫持竞态，又避免 Release 高速执行下误触 10 次登录限流导致后续正确密码被拒
                    for (int k = 0; k < 2; k++)
                    {
                        var r = store.LoginByNameWithSecret(freshUser, secret: null, allowAutoBindSecret: false);
                        if (r.Ok) anyOk = true;
                    }
                    return anyOk;
                })).ToArray();

                barrier.Set();
                var regResult = await regTask;
                bool[] hijackResults = await Task.WhenAll(hijackTasks);

                // 无论谁先完成，带密注册完成后账号必带密码保护，且之后所有免密登录 100% 失败
                Assert.True(regResult.Ok, $"第 {round} 轮带密注册应成功（即使免密先建空号也允许首次绑定或带密原语建号）");
                var finalAcc = store.FindByUsername(freshUser);
                Assert.NotNull(finalAcc);
                Assert.True(finalAcc!.HasSecretProtection, "带密注册成功后账号必须带有 AuthSecretHash 保护");

                var postNoPass = store.LoginByNameWithSecret(freshUser, secret: null, allowAutoBindSecret: false);
                if (postNoPass.Ok) Interlocked.Increment(ref passwordlessSucceededAfterPasswordWin);
                Assert.Equal(0, passwordlessSucceededAfterPasswordWin);
                Assert.False(postNoPass.Ok, "账号受密码保护后任何免密登录必须被拒");
                Assert.True(store.LoginByNameWithSecret(freshUser, "StrongPass123!").Ok);
            }
        }
        finally
        {
            store?.Dispose();
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
