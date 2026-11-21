using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BazaarCore.Models;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

public sealed class V8PerfPresetsTests
{
    private static string NewTempDb(out string dir) { dir = Path.Combine(Path.GetTempPath(), "bazaar_v8_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return Path.Combine(dir, "bazaar.db"); }
    private static void CleanupDir(string dir) { SqliteConnection.ClearAllPools(); if (Directory.Exists(dir)) try { Directory.Delete(dir, recursive: true); } catch { } }
    private static RunState MakeRun(string runId, int gold = 15)
    {
        var run = new RunState { RunId = runId, GameModeId = Guid.NewGuid(), Hero = EHero.Vanessa, Day = 1, Hour = 6 };
        run.PlayerAttributes[EPlayerAttributeType.Gold] = gold;
        run.Cards["c1"] = new RunState.CardEntry { InstanceId = "c1", TemplateId = Guid.NewGuid(), Type = ECardType.Item, Size = ECardSize.Small, Tier = ETier.Bronze, Owner = ECombatantId.Player, Section = EInventorySection.Hand, Socket = EContainerSocketId.Socket_3 };
        return run;
    }

    [Fact]
    public void SqliteMigration_And_PlayerRuleStore_RoundTrip_DefaultMonsterLootTrue()
    {
        string dbPath = NewTempDb(out string dir);
        try
        {
            using (var raw = new SqliteConnection($"Data Source={dbPath}"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "CREATE TABLE accounts (account_id TEXT PRIMARY KEY, username TEXT NOT NULL, identity_key TEXT NOT NULL, runs_completed INTEGER NOT NULL DEFAULT 0, victories INTEGER NOT NULL DEFAULT 0, defeats INTEGER NOT NULL DEFAULT 0, best_day INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, last_seen TEXT NOT NULL); CREATE TABLE runs (run_id TEXT PRIMARY KEY, account_id TEXT NOT NULL, hero INTEGER NOT NULL, play_mode INTEGER NOT NULL, game_mode_id TEXT NOT NULL, day INTEGER NOT NULL, hour INTEGER NOT NULL, victories INTEGER NOT NULL, defeats INTEGER NOT NULL, prestige INTEGER NOT NULL, gold INTEGER NOT NULL, income INTEGER NOT NULL, state INTEGER NOT NULL, saved_at TEXT NOT NULL, snapshot TEXT); CREATE TABLE pvp_ghosts (id INTEGER PRIMARY KEY AUTOINCREMENT, day INTEGER NOT NULL, hero TEXT NOT NULL, wins INTEGER NOT NULL, losses INTEGER NOT NULL, health_max INTEGER NOT NULL, prestige INTEGER NOT NULL, level INTEGER NOT NULL, unlocked_slots INTEGER NOT NULL, board_json TEXT NOT NULL, created_at TEXT NOT NULL);";
                cmd.ExecuteNonQuery();
            }
            using var db = new Persistence(dbPath);
            var acc = new AccountStore(db).GetOrCreateByName("v8_tester");
            var store = new PlayerRuleStore(db);
            Assert.True(store.Get(acc.AccountId).MonsterLootChooseOne);
            Assert.False(store.Get(acc.AccountId).IsCustomPowerModified());

            var updated = store.Set(acc.AccountId, new CustomRunRuleSet { SelectedArchetypeMask = ulong.MaxValue, StartingGold = 2000, MonsterLootChooseOne = false });
            Assert.Equal(3, CardArchetypeCatalog.CountBits(updated.SelectedArchetypeMask));
            Assert.Equal(999, updated.StartingGold);
            Assert.False(updated.MonsterLootChooseOne);

            var reloaded = new PlayerRuleStore(db).Get(acc.AccountId.ToString("N"));
            Assert.Equal(999, reloaded.StartingGold);
            Assert.False(reloaded.MonsterLootChooseOne);

            var reset = store.Reset(acc.AccountId);
            Assert.True(reset.MonsterLootChooseOne);
            Assert.Equal(15, reset.StartingGold);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public async Task DebouncedRunSave_Coalesces20WritesInto1_And_ImmediateFlushCancelsDebounce()
    {
        string dbPath = NewTempDb(out string dir);
        try
        {
            using var db = new Persistence(dbPath);
            string aid = Guid.NewGuid().ToString("D");
            var run1 = MakeRun("run_deb_1");
            for (int i = 1; i <= 20; i++) { run1.PlayerAttributes[EPlayerAttributeType.Gold] = i; db.SaveRunSnapshotDebounced(run1, aid, isCustomRule: true); }
            Assert.Equal(1, db.PendingDebounceCount);
            Assert.Equal(0, db.CompletedRunWriteCount);
            Assert.Null(db.LoadRun("run_deb_1"));

            var sw = Stopwatch.StartNew();
            while (db.CompletedRunWriteCount == 0 && sw.ElapsedMilliseconds < 4000) await Task.Delay(50);
            Assert.Equal(1, db.CompletedRunWriteCount);
            Assert.Equal(0, db.PendingDebounceCount);
            var loaded1 = db.LoadRun("run_deb_1");
            Assert.Equal(20, loaded1!.Gold);
            Assert.True(loaded1.IsCustomRule);

            var run2 = MakeRun("run_deb_2", gold: 50);
            db.SaveRunSnapshotDebounced(run2, aid);
            run2.PlayerAttributes[EPlayerAttributeType.Gold] = 99;
            db.SaveRunSnapshot(run2, aid);
            Assert.Equal(0, db.PendingDebounceCount);
            Assert.Equal(2, db.CompletedRunWriteCount);
            Assert.Equal(99, db.LoadRun("run_deb_2")!.Gold);

            var run3 = MakeRun("run_deb_3", gold: 66);
            db.SaveRunSnapshotDebounced(run3, aid);
            db.FlushRunImmediate("run_deb_3");
            Assert.Equal(0, db.PendingDebounceCount);
            Assert.Equal(66, db.LoadRun("run_deb_3")!.Gold);

            var run4 = MakeRun("run_deb_4", gold: 77);
            db.SaveRunSnapshotDebounced(run4, aid);
            db.FlushAllPending();
            Assert.Equal(0, db.PendingDebounceCount);
            Assert.Equal(4, db.CompletedRunWriteCount);
            Assert.Equal(77, db.LoadRun("run_deb_4")!.Gold);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void PvpGhostPool_IsolatesCustomRuleGhosts_FromStandardMatchmaking()
    {
        string dbPath = NewTempDb(out string dir);
        try
        {
            using var db = new Persistence(dbPath);
            var pool = new PvpGhostPool();
            Assert.NotNull(pool.RecordPlayerGhost(MakeRun("std_d1"), db, isCustomRule: false));
            Assert.NotNull(pool.RecordPlayerGhost(MakeRun("cst_d1"), db, isCustomRule: true));
            Assert.Single(db.LoadPvpGhosts());
            Assert.Equal(2, db.LoadPvpGhosts(includeCustomRule: true).Count);

            var rng = new Random(123);
            for (int i = 0; i < 100; i++) { var opp = pool.PickOpponent(1, 0, 0, rng); Assert.False(opp.IsCustomRule); Assert.Equal("player_std_d1_d1", opp.BoardId); }

            var reloaded = new PvpGhostPool();
            Assert.Equal(2, reloaded.LoadFromDb(db));
            Assert.Equal(1, reloaded.CustomRuleGhostCount);
            Assert.Contains(Enumerable.Range(0, 50).Select(_ => reloaded.PickOpponent(1, 0, 0, rng, includeCustomRuleGhosts: true)), b => b.IsCustomRule);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void SessionStore_Bounded64CommandLog_And_EvictsIdleSessionsOver2Hours()
    {
        var store = new SessionStore();
        var sOld = store.Create("acc1", "old_u", new InitializeRunCommand());
        var sNew = store.Create("acc2", "new_u", new InitializeRunCommand());
        sOld.LastCommandAt = DateTime.UtcNow.AddHours(-3);
        sNew.LastCommandAt = DateTime.UtcNow.AddMinutes(-10);
        for (int i = 0; i < 100; i++) sNew.AddCommandLog($"cmd_{i}");
        Assert.Equal(64, sNew.CommandLog.Count);
        Assert.Equal("cmd_36", sNew.CommandLog[0]);
        Assert.Equal("cmd_99", sNew.CommandLog[^1]);
        Assert.Equal(1, store.EvictIdleSessions(TimeSpan.FromHours(2)));
        Assert.Null(store.Get(sOld.Sid));
        Assert.NotNull(store.Get(sNew.Sid));
        Assert.Throws<ObjectDisposedException>(() => sOld.Gate.Wait(0));
    }

    [Fact]
    public async Task CustomRuleEndpoints_CatalogMePutReset_And_AccountResolution()
    {
        string dbPath = NewTempDb(out string dir);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        try
        {
            using var db = new Persistence(dbPath);
            var accounts = new AccountStore(db);
            var alice = accounts.GetOrCreateByName("alice");
            CustomRuleEndpoints.Map(app, new PlayerRuleStore(db), accounts, () => new { archetypes = CardArchetypeCatalog.All.Count });
            await app.StartAsync();

            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var cat = await http.GetFromJsonAsync<JsonElement>("/custom-rules/catalog");
            Assert.Equal(18, cat.GetProperty("archetypes").GetInt32());

            using var getReq = new HttpRequestMessage(HttpMethod.Get, "/custom-rules/me");
            getReq.Headers.Add("Authorization", "Bearer " + alice.AccessToken);
            Assert.True((await (await http.SendAsync(getReq)).Content.ReadFromJsonAsync<CustomRunRuleSet>())!.MonsterLootChooseOne);

            using var putReq = new HttpRequestMessage(HttpMethod.Put, "/custom-rules/me");
            putReq.Headers.Add("X-Account-Id", alice.AccountId.ToString("D"));
            putReq.Content = new StringContent("{\"startingGold\":321,\"monsterLootChooseOne\":false}", Encoding.UTF8, "application/json");
            var putRes = await (await http.SendAsync(putReq)).Content.ReadFromJsonAsync<CustomRunRuleSet>();
            Assert.Equal(321, putRes!.StartingGold);
            Assert.False(putRes.MonsterLootChooseOne);

            using var rstReq = new HttpRequestMessage(HttpMethod.Post, "/custom-rules/me/reset");
            rstReq.Headers.Add("X-Username", "alice");
            var rstRes = await (await http.SendAsync(rstReq)).Content.ReadFromJsonAsync<CustomRunRuleSet>();
            Assert.Equal(15, rstRes!.StartingGold);
            Assert.True(rstRes.MonsterLootChooseOne);
        }
        finally { await app.StopAsync(); CleanupDir(dir); }
    }
}
