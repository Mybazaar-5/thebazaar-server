using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BazaarServer.Accounts;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.TempoNet;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

[Xunit.Collection("GlobalStaticState")]
public sealed class V8CosmeticsChestsTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string FindWhitelistFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "dist", "data", "valid_collectible_ids.json");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("dist/data/valid_collectible_ids.json not found");
    }

    private static async Task<(WebApplication App, HttpClient Http, string DbPath)> StartTempoNetHostAsync()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"bazaar_v8_cos_{Guid.NewGuid():N}.db");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new Persistence(dbPath));
        builder.Services.AddSingleton<AccountStore>();
        builder.Services.AddSingleton<SessionStore>();
        var app = builder.Build();
        TempoNetEndpoints.Map(app);
        await app.StartAsync();
        string baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(baseUrl) }, dbPath);
    }

    [Fact]
    public void CatalogWhitelist_ContainsVerifiedNonCardAndCardSkins_AndExcludesInvalidEntries()
    {
        string path = FindWhitelistFile();
        var items = JsonSerializer.Deserialize<List<TempoNetEndpoints.ValidCollectibleEntry>>(File.ReadAllText(path), JsonOpts)!;
        var byType = items.GroupBy(x => x.Type).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        Assert.True(byType["hero_skin"] >= 160);
        Assert.True(byType["board"] >= 16);
        Assert.True(byType["carpet"] >= 95);
        Assert.True(byType["card_back"] >= 40);
        Assert.True(byType["stash"] >= 20);
        Assert.True(byType["bank"] >= 24);
        Assert.True(byType["album"] >= 12);
        Assert.True(byType["card_skin"] >= 800);
        Assert.All(items, x => Assert.True(Guid.TryParse(x.Id, out _)));
        Assert.DoesNotContain(items, x => string.Equals(x.Id, "3809c54a-2d55-af74-0943-4dfa8b0c1631", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HeroesSeasonBootstrap_Loadouts_AndChestOpen_PersistEndToEnd()
    {
        var items = JsonSerializer.Deserialize<List<TempoNetEndpoints.ValidCollectibleEntry>>(
            await File.ReadAllTextAsync(FindWhitelistFile()), JsonOpts)!;
        var heroSkin = items.First(x => x.Type == "hero_skin" && string.Equals(x.HeroId, "Vanessa", StringComparison.OrdinalIgnoreCase));
        var board = items.First(x => x.Type == "board");
        var carpet = items.First(x => x.Type == "carpet");
        int expectedNonCardCount = items.Count(x => !string.Equals(x.Type, "card_skin", StringComparison.OrdinalIgnoreCase));

        var (app, http, dbPath) = await StartTempoNetHostAsync();
        try
        {
            var loginResp = await http.PostAsJsonAsync("/api/auth/login/user", new { username = "CosTester" });
            loginResp.EnsureSuccessStatusCode();
            var loginJson = JsonNode.Parse(await loginResp.Content.ReadAsStringAsync())!;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson["AccessToken"]!.GetValue<string>());

            // 1) 英雄列表（含 Hero8 在内的 8 位真实英雄）与第 2 赛季验证
            var heroesJson = JsonNode.Parse(await http.GetStringAsync("/api/Heroes/owned"))!.AsArray();
            var heroIds = heroesJson.Select(h => h!["heroId"]!.GetValue<string>()).ToList();
            Assert.Equal(8, heroIds.Count);
            Assert.Contains("Hero8", heroIds);
            Assert.Contains("Karnok", heroIds);

            var seasonCur = JsonNode.Parse(await http.GetStringAsync("/api/Seasons/current"))!;
            Assert.Equal(2, seasonCur["id"]!.GetValue<int>());
            Assert.Equal("Season 1", seasonCur["name"]!.GetValue<string>());
            Assert.Equal(0, seasonCur["allowBundleChestAfterSeasonId"]!.GetValue<int>());

            // 2) Bootstrap & CollectionItems 验证（初始仅下发非 CardSkin 藏品 + 30 未开宝箱）
            var boot = JsonNode.Parse(await http.GetStringAsync("/api/Bootstrap/app"))!;
            Assert.Equal(expectedNonCardCount, boot["collection"]!["items"]!.AsArray().Count);
            Assert.Equal(8, boot["collection"]!["loadouts"]!["loadouts"]!.AsArray().Count);
            Assert.Equal(8, boot["heroes"]!["listings"]!["heroes"]!.AsArray().Count);
            Assert.Equal(30, boot["chests"]!["bundledUnopenedCount"]!.GetValue<int>());
            Assert.Empty(boot["chests"]!["legacyChests"]!.AsArray());

            // 2b) 未手动选肤时 Hero8 默认 loadout 返回空 heroSkinId 供客户端安全回退默认皮肤
            var hero8DefaultLoadout = JsonNode.Parse(await http.GetStringAsync("/api/Loadouts/me/Hero8"))!;
            Assert.Equal("Hero8", hero8DefaultLoadout["heroId"]!.GetValue<string>());
            Assert.Equal("", hero8DefaultLoadout["heroSkinId"]!.GetValue<string>());

            var career = JsonNode.Parse(await http.GetStringAsync("/api/PlayerProfiles/me/career"))!;
            Assert.True(career["HasCompletedTutorial"]!.GetValue<bool>());

            // 3) 配装穿戴、批量配置、卸下与返回验证
            var eqSkin = await http.PostAsync($"/api/Loadouts/hero/Vanessa/equip/{heroSkin.Id}", null);
            eqSkin.EnsureSuccessStatusCode();
            var eqBoard = await http.PostAsync($"/api/Loadouts/hero/Vanessa/equip/{board.Id}", null);
            eqBoard.EnsureSuccessStatusCode();
            var eqBatch = await http.PostAsJsonAsync("/api/Loadouts/hero/Vanessa/equip-loadout", new { carpetId = carpet.Id });
            eqBatch.EnsureSuccessStatusCode();

            var loadout = JsonNode.Parse(await http.GetStringAsync("/api/Loadouts/me/Vanessa"))!;
            Assert.Equal(AccountStore.NormalizeCollectibleId(heroSkin.Id), loadout["heroSkinId"]!.GetValue<string>());
            Assert.Equal(AccountStore.NormalizeCollectibleId(board.Id), loadout["boardId"]!.GetValue<string>());
            Assert.Equal(AccountStore.NormalizeCollectibleId(carpet.Id), loadout["carpetId"]!.GetValue<string>());

            var uneq = await http.PostAsync($"/api/Loadouts/hero/Vanessa/unequip/{board.Id}", null);
            uneq.EnsureSuccessStatusCode();
            var loadoutAfter = JsonNode.Parse(await uneq.Content.ReadAsStringAsync())!;
            Assert.Equal("", loadoutAfter["boardId"]!.GetValue<string>());
            Assert.Equal(AccountStore.NormalizeCollectibleId(heroSkin.Id), loadoutAfter["heroSkinId"]!.GetValue<string>());

            // 3b) 第 8 英雄双龙（Hero8）穿戴龙族皮肤 Skin_DRA_02a (b91b7e1e-7c7c-4494-388b-4d1d9a1a9606)
            const string hero8SkinId = "b91b7e1e-7c7c-4494-388b-4d1d9a1a9606";
            Assert.Contains(items, x =>
                string.Equals(x.Type, "hero_skin", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.HeroId, "Hero8", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Id, hero8SkinId, StringComparison.OrdinalIgnoreCase));
            var eqHero8Skin = await http.PostAsync($"/api/Loadouts/hero/Hero8/equip/{hero8SkinId}", null);
            eqHero8Skin.EnsureSuccessStatusCode();
            var hero8Loadout = JsonNode.Parse(await http.GetStringAsync("/api/Loadouts/me/Hero8"))!;
            Assert.Equal(AccountStore.NormalizeCollectibleId(hero8SkinId), hero8Loadout["heroSkinId"]!.GetValue<string>());

            // 4) 宝箱掉落率、授予、购买与 3D 开箱（授予动画卡牌皮肤并同步 CollectionItems）
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/Chests/drop-rates/2")).StatusCode);
            var openBundled = await http.PostAsJsonAsync("/api/app/chests/open-bundled", new { count = 2 });
            openBundled.EnsureSuccessStatusCode();
            var openJson = JsonNode.Parse(await openBundled.Content.ReadAsStringAsync())!;
            Assert.Equal(2, openJson["Chests"]!.AsArray().Count);
            var addedSkins = openJson["State"]!["CollectionItemsAdded"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            Assert.Equal(2, addedSkins.Count);

            var grantResp = await http.PostAsJsonAsync("/api/Chests/grant", new { count = 5 });
            grantResp.EnsureSuccessStatusCode();
            var buyResp = await http.PostAsJsonAsync("/api/app/chests/store-purchase", new { quantity = 2 });
            buyResp.EnsureSuccessStatusCode();
            var openLegacy = await http.PostAsJsonAsync("/api/app/chests/open", new { SeasonId = 2, Count = 1 });
            openLegacy.EnsureSuccessStatusCode();

            var unopened = JsonNode.Parse(await http.GetStringAsync("/api/Chests/unopened"))!;
            Assert.Equal(34, unopened["bundledUnopenedCount"]!.GetValue<int>()); // 30 - 2 + 5 + 2 - 1 = 34

            var collAfter = JsonNode.Parse(await http.GetStringAsync("/api/CollectionItems"))!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            Assert.Equal(expectedNonCardCount + 3, collAfter.Count);

            // 5) 重建 AccountStore 从 SQLite 读盘，验证配装、未开箱数与已开卡牌皮肤持久化
            var reloaded = new AccountStore(new Persistence(dbPath));
            reloaded.LoadAllFromDb();
            var acc = reloaded.FindByUsername("CosTester")!;
            Assert.NotNull(acc);
            Assert.Equal(34, reloaded.GetUnopenedChestCount(acc));
            Assert.Equal(3, reloaded.GetOpenedCardSkinIds(acc).Count);
            Assert.Equal(AccountStore.NormalizeCollectibleId(heroSkin.Id), reloaded.GetHeroLoadout(acc, "Vanessa").HeroSkinId);
            Assert.Equal(AccountStore.NormalizeCollectibleId(hero8SkinId), reloaded.GetHeroLoadout(acc, "Hero8").HeroSkinId);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            http.Dispose();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}
