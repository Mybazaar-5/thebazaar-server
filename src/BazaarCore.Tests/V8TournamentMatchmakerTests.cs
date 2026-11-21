using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BazaarCore.Models;
using BazaarServer.Accounts;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

public sealed class V8TournamentMatchmakerTests
{
    [Fact]
    public void Matchmaker_1000Tournaments_15Rounds_N8AndN10_AllHumanCounts_ZeroViolations()
    {
        var rng = new Random(20261120);
        var sw = Stopwatch.StartNew();
        int totalRounds = 0;

        for (int n = 8; n <= 10; n += 2)
        {
            for (int h = 2; h <= n; h++)
            {
                for (int t = 0; t < 250; t++)
                {
                    var mm = new TournamentMatchmaker($"sim-{n}-{h}-{t}", rng);
                    var roster = Enumerable.Range(0, n)
                        .Select(i => new MatchupParticipant(i < h ? $"h{i}" : $"b{i}", i < h ? $"Human{i}" : $"Bot{i}", i >= h))
                        .ToList();

                    for (uint day = 1; day <= 15; day++)
                    {
                        var map = mm.AssignRoundMatchups(day, roster);
                        Assert.Equal(n, map.Count);
                        totalRounds++;
                        foreach (var kv in map)
                        {
                            var opp = map[kv.Value.OpponentAccountId];
                            Assert.Equal(kv.Key, opp.OpponentAccountId);
                            Assert.Equal(kv.Value.BattleSeed, opp.BattleSeed);
                        }
                        for (int i = 0; i < h; i++)
                            Assert.InRange(mm.GetConsecutiveBotRounds($"h{i}"), 0, 3);
                    }
                    Assert.Equal(n, mm.AssignRoundMatchups(15, roster).Count);
                }
            }
        }

        sw.Stop();
        double avgMs = sw.Elapsed.TotalMilliseconds / totalRounds;
        Assert.True(avgMs < 0.1, $"Expected <0.1ms/round, actual={avgMs:F4}ms");
    }

    [Fact]
    public void Matchmaker_OddHumansSparringBot_SoftWeights_LargeN_And_ResolutionCache()
    {
        var mmOdd = new TournamentMatchmaker("odd-sparring", new Random(7));
        var oddHumans = new[] { new MatchupParticipant("u1", "Alice", false), new MatchupParticipant("u2", "Bob", false), new MatchupParticipant("u3", "Carol", false) };
        var day1 = mmOdd.AssignRoundMatchups(1, oddHumans, humanMatchEvery4Rounds: true, botNonLethalPrestigeLock: true);
        Assert.Equal(4, day1.Count);
        var sparring = Assert.Single(day1.Values.Where(a => a.OpponentIsSyntheticSparringBot));
        Assert.True(mmOdd.IsOpponentNonLethalBot(sparring.AccountId, 1));
        Assert.True(mmOdd.ShouldSuppressLethalElimination(sparring.AccountId, 1));
        var hPair = day1.Values.Where(a => !a.AccountId.Equals(sparring.OpponentAccountId, StringComparison.OrdinalIgnoreCase) && !a.OpponentIsBot).ToList();
        Assert.Equal(2, hPair.Count);
        Assert.False(mmOdd.ShouldSuppressLethalElimination(hPair[0].AccountId, 1));

        var rec = mmOdd.RecordHumanMatchResolution(1, hPair[0].AccountId, hPair[0].OpponentAccountId, hPair[0].AccountId, 3);
        var rev = mmOdd.TryGetHumanMatchResolution(1, hPair[0].OpponentAccountId, hPair[0].AccountId);
        Assert.NotNull(rev);
        Assert.Equal(rec.WinnerAccountId, rev!.WinnerAccountId);
        Assert.Equal(rec.BattleSeed, rev.BattleSeed);

        int repeats = 0;
        var rng2 = new Random(99);
        for (int trial = 0; trial < 200; trial++)
        {
            var mm = new TournamentMatchmaker($"pen-{trial}", rng2);
            var list = Enumerable.Range(0, 8).Select(i => new MatchupParticipant(i < 2 ? $"h{i}" : $"b{i}", $"P{i}", i >= 2)).ToList();
            if (mm.AssignRoundMatchups(1, list)["h0"].OpponentAccountId == "h1" &&
                mm.AssignRoundMatchups(2, list)["h0"].OpponentAccountId == "h1")
            {
                repeats++;
            }
        }
        Assert.True(repeats < 15);

        var mm12 = new TournamentMatchmaker("n12", new Random(1234));
        var r12 = Enumerable.Range(0, 12).Select(i => new MatchupParticipant($"p{i}", $"P{i}", i >= 5)).ToList();
        for (uint d = 1; d <= 15; d++)
        {
            mm12.AssignRoundMatchups(d, r12);
            for (int i = 0; i < 5; i++) Assert.InRange(mm12.GetConsecutiveBotRounds($"p{i}"), 0, 3);
        }
    }

    [Fact]
    public async Task TournamentEndpoints_BotsRulesDefaults_And_SseEventWakeup()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"v8_tourney_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var db = new Persistence(Path.Combine(dir, "bazaar.db"));
        var lobbies = new LobbyStore();
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(new AccountStore(db));
        builder.Services.AddSingleton(new SessionStore());
        builder.Services.AddSingleton(lobbies);

        await using var app = builder.Build();
        TournamentEndpoints.Map(app, lobbies);
        await app.StartAsync();
        string baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var hostHttp = new HttpClient { BaseAddress = new Uri(baseUrl) };
        using var guestHttp = new HttpClient { BaseAddress = new Uri(baseUrl) };
        hostHttp.DefaultRequestHeaders.Add("X-Username", "HostAlpha");
        guestHttp.DefaultRequestHeaders.Add("X-Username", "GuestBeta");

        try
        {
            using var sseResp = await hostHttp.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead);
            using var sseReader = new StreamReader(await sseResp.Content.ReadAsStreamAsync());
            Assert.Equal("data: connected", await sseReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await sseReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var createResp = await hostHttp.PostAsJsonAsync("/lobbies", new
            {
                name = "V8 Cup", roundCountdownSeconds = 90, acceleratedRoundCountdownSeconds = 20, bannedEncounterIds = new[] { "enc-ban-1" },
            });
            createResp.EnsureSuccessStatusCode();
            using var created = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
            string lobbyId = created.RootElement.GetProperty("LobbyId").GetString()!;
            Assert.Equal(8, created.RootElement.GetProperty("PlayerCap").GetInt32());
            Assert.Equal(90, created.RootElement.GetProperty("RoundCountdownSeconds").GetInt32());
            Assert.Equal(20, created.RootElement.GetProperty("AcceleratedRoundCountdownSeconds").GetInt32());
            var rulesEl = created.RootElement.GetProperty("Rules");
            Assert.True(rulesEl.GetProperty("monsterLootChooseOne").GetBoolean());
            Assert.True(rulesEl.GetProperty("botNonLethalPrestigeLock").GetBoolean());
            Assert.True(rulesEl.GetProperty("humanMatchEvery4Rounds").GetBoolean());
            Assert.Equal("enc-ban-1", rulesEl.GetProperty("bannedEncounterIds")[0].GetString());
            Assert.Equal("data: update", await sseReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await sseReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(HttpStatusCode.Forbidden, (await guestHttp.PutAsJsonAsync($"/lobbies/{lobbyId}/rules", new CustomRunRuleSet { PlayerCap = 10 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await guestHttp.PostAsJsonAsync($"/lobbies/{lobbyId}/bots", new { action = "add" })).StatusCode);

            var updResp = await hostHttp.PutAsJsonAsync($"/lobbies/{lobbyId}/rules", new CustomRunRuleSet { PlayerCap = 10 });
            updResp.EnsureSuccessStatusCode();
            Assert.Equal(10, JsonDocument.Parse(await updResp.Content.ReadAsStringAsync()).RootElement.GetProperty("PlayerCap").GetInt32());

            Assert.Equal(2, JsonDocument.Parse(await (await hostHttp.PostAsJsonAsync($"/lobbies/{lobbyId}/bots", new { action = "add" })).Content.ReadAsStringAsync()).RootElement.GetProperty("Members").GetArrayLength());
            using var fillDoc = JsonDocument.Parse(await (await hostHttp.PostAsJsonAsync($"/lobbies/{lobbyId}/bots", new { action = "fill" })).Content.ReadAsStringAsync());
            Assert.Equal("waiting", fillDoc.RootElement.GetProperty("State").GetString());
            var names = fillDoc.RootElement.GetProperty("Members").EnumerateArray().Select(x => x.GetProperty("Username").GetString()!).ToList();
            Assert.Equal(10, names.Count);
            Assert.Equal(10, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains("TempestBot-9", names);
            Assert.Equal(9, JsonDocument.Parse(await (await hostHttp.PostAsJsonAsync($"/lobbies/{lobbyId}/bots", new { action = "remove" })).Content.ReadAsStringAsync()).RootElement.GetProperty("Members").GetArrayLength());
            Assert.Equal(1, JsonDocument.Parse(await (await hostHttp.PostAsJsonAsync($"/lobbies/{lobbyId}/bots", new { action = "clear" })).Content.ReadAsStringAsync()).RootElement.GetProperty("Members").GetArrayLength());

            (await guestHttp.PostAsync($"/lobbies/{created.RootElement.GetProperty("Code").GetString()}/members", new StringContent(""))).EnsureSuccessStatusCode();
            (await hostHttp.PutAsync($"/lobbies/{lobbyId}/countdown", new StringContent("{}"))).EnsureSuccessStatusCode();
            using var hStatus = JsonDocument.Parse(await hostHttp.GetStringAsync("/sessions/current/status"));
            var hPlayers = hStatus.RootElement.GetProperty("Players").EnumerateArray().ToDictionary(p => p.GetProperty("AccountId").GetString()!);
            Assert.Equal(10, hPlayers.Count);
            Assert.All(hPlayers.Values, p => Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("AssignedOpponentAccountId").GetString())));
            string hostAid = created.RootElement.GetProperty("HostAccountId").GetString()!;
            string hostOpp = hStatus.RootElement.GetProperty("AssignedOpponentAccountId").GetString()!;
            Assert.Equal(hostAid, hPlayers[hostOpp].GetProperty("AssignedOpponentAccountId").GetString());

            lobbies.FindMember(lobbies.Get(lobbyId)!, hostAid)!.FinalDay = 15;
            using var d15 = JsonDocument.Parse(await hostHttp.GetStringAsync("/sessions/current/status"));
            Assert.All(d15.RootElement.GetProperty("Players").EnumerateArray(), p =>
            {
                Assert.True(p.GetProperty("Prestige").GetInt32() >= 1);
                Assert.False(p.GetProperty("IsEliminated").GetBoolean());
            });
        }
        finally
        {
            await app.StopAsync();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
