using System.Net.Http.Json;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Protocol;
using BazaarServer.Storage;
using BazaarServer.TempoNet;
using BazaarServer.Tournament;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

public sealed class V8IntegrationTests
{
    private sealed record Fx(CardPool Pool, List<BazaarServer.Data.MonsterTemplate> Monsters, LevelUpTable LevelUps, GameModeConfig Mode, GameDataRepository Repo, PvpGhostPool Ghosts, CardArchetypeIndex Index);

    private static readonly Lazy<Fx> Shared = new(() =>
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++, dir = Directory.GetParent(dir)?.FullName)
            if (File.Exists(Path.Combine(dir, "dist", "data", "GameData.db"))) { dir = Path.Combine(dir, "dist", "data"); break; }
        dir ??= @"e:\thebazaar\dist\data";
        string db = Path.Combine(dir, "GameData.db");
        var pool = CardPool.Load(db);
        var repo = GameDatabaseLoader.Load(db, null);
        return new Fx(pool, MonsterPool.Load(db), LevelUpTable.Load(db), GameModeConfig.Load(db), repo, PvpGhostPool.Load(dir), CardArchetypeIndex.Build(pool, repo));
    });

    private sealed class TempDb : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "v8i_" + Guid.NewGuid().ToString("N"));
        public Persistence Db { get; }
        public TempDb() { Directory.CreateDirectory(Dir); Db = new Persistence(Path.Combine(Dir, "bazaar.db")); }
        public void Dispose() { Db.Dispose(); SqliteConnection.ClearAllPools(); try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { } }
    }

    private static RunState NewRun(string? aid = null, Guid? tid = null, EPlayMode m = EPlayMode.Unranked) => new() { RunId = "r_" + Guid.NewGuid().ToString("N")[..10], GameModeId = Shared.Value.Pool.DefaultGameModeId, Hero = EHero.Vanessa, PlayMode = m, AccountId = aid, TournamentSessionId = tid };
    private static RunEngine NewEngine(RunState r, PlayerRuleStore? pr = null, LobbyStore? l = null, List<BazaarServer.Data.MonsterTemplate>? m = null) => new(r, Shared.Value.Pool, m ?? Shared.Value.Monsters, Shared.Value.LevelUps, Shared.Value.Mode, Shared.Value.Repo, Shared.Value.Ghosts, Shared.Value.Index, pr, l);
    private static List<string> SpawnableIds(string type, string? size, int count) => Shared.Value.Pool.OfType(type).Where(c => Shared.Value.Pool.IsPlayerSpawnable(c, true) && (size == null || c.Size == size) && Shared.Value.Repo.GetCard(c.Id) != null).Select(c => c.Id).Take(count).ToList();
    private static LobbyStore.Lobby CreateDuelLobby(LobbyStore lobbies, AccountStore.Account h1, AccountStore.Account h2, CustomRunRuleSet? rules = null) { var l = lobbies.Create(h1, "Duel", "Tournament", 2, false, rules); lobbies.Join(l.Code, h2); lobbies.StartCountdown(l.LobbyId, Guid.NewGuid()); return l; }
    private static ByteArrayContent MsgPack(INetCommand cmd) => new(MessagePackSerializer.Serialize(cmd, Codec.Options)) { Headers = { ContentType = new("application/octet-stream") } };

    [Fact]
    public void Test01_PresetStatsAndItems_HandThenStashOverflow_NoFalseLv2_And_SnapshotRoundTrip()
    {
        using var t = new TempDb();
        var store = new PlayerRuleStore(t.Db);
        var acc = new AccountStore(t.Db).GetOrCreateByName("preset_u");
        store.Set(acc.AccountId, new CustomRunRuleSet { StartingGold = 77, StartingIncome = 11, StartingExperience = 5, StartingPrestige = 30, StartingHealth = 420, StartingItems = SpawnableIds("Item", "Small", 6).Select(id => new PresetCardSpec { TemplateId = id, Tier = "Gold", Enchantment = "Fiery" }).ToList(), StartingSkills = [new PresetCardSpec { TemplateId = SpawnableIds("Skill", null, 1)[0], Tier = "Silver" }] });
        var run = NewRun(acc.AccountId.ToString("N"));
        NewEngine(run, store).StartRun();
        Assert.Equal((77, 11, 5, 1, 0), (run.PlayerAttributes[EPlayerAttributeType.Gold], run.PlayerAttributes[EPlayerAttributeType.Income], run.PlayerAttributes[EPlayerAttributeType.Experience], run.PlayerAttributes[EPlayerAttributeType.Level], run.PendingLevelUps));
        Assert.Equal((30, 420), (run.PlayerAttributes[EPlayerAttributeType.Prestige], run.PlayerAttributes[EPlayerAttributeType.HealthMax]));
        var owned = run.Cards.Values.Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item).ToList();
        Assert.Equal((6, 4, 2), (owned.Count, owned.Count(c => c.Section == EInventorySection.Hand), owned.Count(c => c.Section == EInventorySection.Stash)));
        Assert.Single(run.Cards.Values, c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill);
        Assert.All(owned, c => Assert.Equal(EEnchantmentType.Fiery, c.Enchantment));
        Assert.True(RunSnapshotSerializer.TryDeserialize(RunSnapshotSerializer.Serialize(run), Shared.Value.Repo, out var restored, out _));
        Assert.Equal((77, 4, 2), (restored!.CustomRules.StartingGold, restored.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Section == EInventorySection.Hand), restored.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Section == EInventorySection.Stash)));
    }

    [Fact]
    public void Test02_TendencyMergeInTournament_And_GhostPoolIsolation()
    {
        using var t = new TempDb();
        var (store, alice, lobbies) = (new PlayerRuleStore(t.Db), new AccountStore(t.Db).GetOrCreateByName("t_alice"), new LobbyStore());
        store.Set(alice.AccountId, new CustomRunRuleSet { SelectedArchetypeMask = (ulong)ECardArchetype.Burn });
        var lAllow = lobbies.Create(alice, "AllowT", "Tournament", 8, false, new CustomRunRuleSet { AllowTendencySelection = true });
        lobbies.StartCountdown(lAllow.LobbyId, Guid.NewGuid());
        var rAllow = NewRun(alice.AccountId.ToString("N"), lAllow.TournamentSessionId, EPlayMode.Tournament);
        NewEngine(rAllow, store, lobbies).StartRun();
        Assert.Equal(((ulong)ECardArchetype.Burn, false), (rAllow.CustomRules.SelectedArchetypeMask, rAllow.IsCustomRuleRun));
        var lDeny = lobbies.Create(alice, "DenyT", "Tournament", 8, false, new CustomRunRuleSet { AllowTendencySelection = false });
        lobbies.StartCountdown(lDeny.LobbyId, Guid.NewGuid());
        var rDeny = NewRun(alice.AccountId.ToString("N"), lDeny.TournamentSessionId, EPlayMode.Tournament);
        NewEngine(rDeny, store, lobbies).StartRun();
        Assert.Equal(0UL, rDeny.CustomRules.SelectedArchetypeMask);
        var (ghostPool, tplId, rng) = (new PvpGhostPool(), Guid.Parse(SpawnableIds("Item", "Small", 1)[0]), new Random(11));
        rAllow.Cards["g1"] = new RunState.CardEntry { InstanceId = "g1", TemplateId = tplId, Type = ECardType.Item, Size = ECardSize.Small, Tier = ETier.Bronze, Owner = ECombatantId.Player, Section = EInventorySection.Hand, Socket = EContainerSocketId.Socket_3 };
        rDeny.CustomRules.StartingGold = 500;
        rDeny.Cards["g2"] = new RunState.CardEntry { InstanceId = "g2", TemplateId = tplId, Type = ECardType.Item, Size = ECardSize.Small, Tier = ETier.Gold, Owner = ECombatantId.Player, Section = EInventorySection.Hand, Socket = EContainerSocketId.Socket_3 };
        ghostPool.RecordPlayerGhost(rAllow, t.Db, isCustomRule: rAllow.IsCustomRuleRun);
        ghostPool.RecordPlayerGhost(rDeny, t.Db, isCustomRule: rDeny.IsCustomRuleRun);
        for (int i = 0; i < 30; i++) Assert.False(ghostPool.PickOpponent(1, 0, 0, rng, Shared.Value.Repo, includeCustomRuleGhosts: rAllow.IsCustomRuleRun).IsCustomRule);
        Assert.Contains(Enumerable.Range(0, 40).Select(_ => ghostPool.PickOpponent(1, 0, 0, rng, Shared.Value.Repo, includeCustomRuleGhosts: rDeny.IsCustomRuleRun)), g => g.IsCustomRule);
    }

    [Fact]
    public void Test03_MonsterLootChooseOne_DefaultTrue_Vs_OptOutSingleDropAndResume()
    {
        var fx = Shared.Value;
        var encTpl = fx.Pool.OfType("CombatEncounter").First(c => fx.Pool.IsPlayerSpawnable(c, true) && c.MonsterLevel == 1 && !string.IsNullOrEmpty(c.MonsterTemplateId));
        var mon = new BazaarServer.Data.MonsterTemplate { Id = encTpl.MonsterTemplateId!, InternalName = "V8Boss", HealthMax = 50, Items = SpawnableIds("Item", "Large", 4).Select(id => new BazaarServer.Data.MonsterTemplate.MonsterItem { TemplateId = id, Tier = "Bronze" }).ToList() };
        var monsters = new List<BazaarServer.Data.MonsterTemplate> { mon };
        var rSingle = NewRun();
        rSingle.CustomRules = new CustomRunRuleSet { MonsterLootChooseOne = false };
        var eSingle = NewEngine(rSingle, m: monsters);
        eSingle.StartRun();
        Assert.False(rSingle.CustomRules.MonsterLootChooseOne);
        new ShopEngine(rSingle, fx.Pool, fx.Repo, rSingle.CustomRules, fx.Index).DealMonsterVictoryLoot(encTpl, mon, null, chooseOne: rSingle.CustomRules.MonsterLootChooseOne);
        Assert.Equal((ERunState.Loot, 0), (rSingle.State, rSingle.MonsterLootPages.Count));
        Assert.NotNull(eSingle.Handle(new RerollCommand()).Error);

        var rOpt = NewRun();
        rOpt.CustomRules = new CustomRunRuleSet { StartingHealth = 2000, StartingItems = [new PresetCardSpec { TemplateId = "1b955b8d-0ea6-4437-935c-d588be5898c1", Tier = "Diamond" }] };
        var eOpt = NewEngine(rOpt, m: monsters);
        eOpt.StartRun();
        Assert.True(rOpt.CustomRules.MonsterLootChooseOne);
        eOpt.Handle(new CheatCommand { Args = ["hour", "3"] }); eOpt.Handle(new CheatCommand { Args = ["deal"] });
        var cbtId = rOpt.SelectionSet.First();
        rOpt.Cards[cbtId] = new RunState.CardEntry { InstanceId = cbtId, TemplateId = Guid.Parse(encTpl.Id), Type = ECardType.CombatEncounter, Size = ECardSize.Medium };
        Assert.Null(eOpt.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(cbtId) }).Error);
        Assert.Equal((ERunState.Loot, 2, 0u, 999u), (rOpt.State, rOpt.MonsterLootPages.Count, rOpt.RerollCost, rOpt.RerollsRemaining));
        int goldBefore = rOpt.PlayerAttributes[EPlayerAttributeType.Gold];
        Assert.Null(eOpt.Handle(new RerollCommand()).Error);
        Assert.Equal((1, goldBefore), (rOpt.MonsterLootPageIndex, rOpt.PlayerAttributes[EPlayerAttributeType.Gold]));
        Assert.True(RunSnapshotSerializer.TryDeserialize(RunSnapshotSerializer.Serialize(rOpt), fx.Repo, out var resumed, out _));
        Assert.Null(NewEngine(resumed!, m: monsters).Handle(new RerollCommand()).Error);
        Assert.Equal(0, resumed!.MonsterLootPageIndex);
    }

    [Fact]
    public void Test04_TournamentSchemeA_Bot1PrestigeLock_Vs_HumanLethal()
    {
        using var t = new TempDb();
        var (accounts, lobbies) = (new AccountStore(t.Db), new LobbyStore());
        var (h1, h2) = (accounts.GetOrCreateByName("sa_h1"), accounts.GetOrCreateByName("sa_h2"));
        var solo = lobbies.Create(h1, "BotLock", "Tournament", 8, false, new CustomRunRuleSet { BotNonLethalPrestigeLock = true });
        lobbies.StartCountdown(solo.LobbyId, Guid.NewGuid());
        var rBot = NewRun(h1.AccountId.ToString("N"), solo.TournamentSessionId, EPlayMode.Tournament);
        var eBot = NewEngine(rBot, l: lobbies);
        eBot.StartRun(); eBot.Handle(new CheatCommand { Args = ["day", "10"] }); eBot.Handle(new CheatCommand { Args = ["prestige", "5"] });
        Assert.Null(eBot.Handle(new CheatCommand { Args = ["pvp"] }).Error);
        Assert.Equal((true, 1), (rBot.FatesUsed, rBot.PlayerAttributes[EPlayerAttributeType.Prestige]));
        eBot.Handle(new ExitCurrentStateCommand()); eBot.Handle(new CheatCommand { Args = ["day", "11"] });
        Assert.Null(eBot.Handle(new CheatCommand { Args = ["pvp"] }).Error);
        Assert.Equal((1, false), (rBot.PlayerAttributes[EPlayerAttributeType.Prestige], rBot.State == ERunState.EndRunDefeat));
        var duel = CreateDuelLobby(lobbies, h1, h2);
        var rHum = NewRun(h1.AccountId.ToString("N"), duel.TournamentSessionId, EPlayMode.Tournament);
        var eHum = NewEngine(rHum, l: lobbies);
        eHum.StartRun(); rHum.FatesUsed = true;
        eHum.Handle(new CheatCommand { Args = ["day", "10"] }); eHum.Handle(new CheatCommand { Args = ["prestige", "1"] }); eHum.Handle(new CheatCommand { Args = ["pvp"] });
        Assert.Equal(ERunState.EndRunDefeat, rHum.State);
    }

    [Fact]
    public void Test05_TournamentLastHumanStanding_Defers10WinUntilRivalsEliminated()
    {
        using var t = new TempDb();
        var (accounts, lobbies) = (new AccountStore(t.Db), new LobbyStore());
        var (h1, h2) = (accounts.GetOrCreateByName("lhs_h1"), accounts.GetOrCreateByName("lhs_h2"));
        var duel = CreateDuelLobby(lobbies, h1, h2, new CustomRunRuleSet { LastHumanStandingMode = true });
        var (ra, rb) = (NewRun(h1.AccountId.ToString("N"), duel.TournamentSessionId, EPlayMode.Tournament), NewRun(h2.AccountId.ToString("N"), duel.TournamentSessionId, EPlayMode.Tournament));
        var (ea, eb) = (NewEngine(ra, l: lobbies), NewEngine(rb, l: lobbies));
        ea.StartRun(); eb.StartRun();
        ea.Handle(new CheatCommand { Args = ["win", "10"] });
        Assert.NotEqual(ERunState.EndRunVictory, ra.State);
        eb.Handle(new AbandonRunCommand());
        ea.Handle(new CheatCommand { Args = ["win", "1"] });
        Assert.Equal(ERunState.EndRunVictory, ra.State);
    }

    [Fact]
    public async Task Test06_HttpGameServer_MoveItemDebouncedCoalescing_And_Flush()
    {
        var fx = Shared.Value;
        using var t = new TempDb();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var (accounts, rules, sessions, lobbies) = (new AccountStore(t.Db), new PlayerRuleStore(t.Db), new SessionStore(), new LobbyStore());
        builder.Services.AddSingleton(t.Db); builder.Services.AddSingleton(accounts); builder.Services.AddSingleton(rules); builder.Services.AddSingleton(sessions); builder.Services.AddSingleton(lobbies);
        builder.Services.AddSingleton(fx.Pool); builder.Services.AddSingleton(fx.Monsters); builder.Services.AddSingleton(fx.LevelUps); builder.Services.AddSingleton(fx.Mode); builder.Services.AddSingleton(fx.Ghosts); builder.Services.AddSingleton(fx.Repo); builder.Services.AddSingleton(fx.Index);
        await using var app = builder.Build();
        TempoNetEndpoints.Map(app); GameServerEndpoints.Map(app); TournamentEndpoints.Map(app, lobbies);
        CustomRuleEndpoints.Map(app, rules, accounts, () => fx.Index.ToCatalogDto(fx.Pool, fx.Repo));
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
        var login = await (await http.PostAsJsonAsync("/api/auth/login/user", new { username = "deb_u" })).Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Add("Authorization", "Bearer " + login.GetProperty("AccessToken").GetString());
        await http.PutAsJsonAsync("/custom-rules/me", new CustomRunRuleSet { StartingItems = [new PresetCardSpec { TemplateId = SpawnableIds("Item", "Small", 1)[0] }] });
        var sessResp = await http.PostAsync("/sessions", MsgPack(new InitializeRunCommand { PlayMode = EPlayMode.Unranked, SelectedHero = EHero.Vanessa, GameModeId = fx.Pool.DefaultGameModeId }));
        string sid = sessResp.Headers.GetValues("sid").First();
        Assert.Equal(1, t.Db.CompletedRunWriteCount);
        var itemId = new InstanceId(SessionRegistry.Get(sid)!.Run.Cards.Values.First(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item).InstanceId);
        foreach (var sock in new[] { EContainerSocketId.Socket_6, EContainerSocketId.Socket_4, EContainerSocketId.Socket_5, EContainerSocketId.Socket_6 })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/commands") { Content = MsgPack(new MoveItemCommand { InstanceId = itemId, Section = EInventorySection.Hand, TargetSockets = [sock] }) };
            req.Headers.Add("sid", sid);
            (await http.SendAsync(req)).EnsureSuccessStatusCode();
        }
        Assert.Equal((1, 1), (t.Db.PendingDebounceCount, (int)t.Db.CompletedRunWriteCount));
        await app.StopAsync();
        Assert.Equal((0, 2), (t.Db.PendingDebounceCount, (int)t.Db.CompletedRunWriteCount));
        Assert.True(RunSnapshotSerializer.TryDeserialize(t.Db.LoadLatestActiveRun(accounts.GetOrCreateByName("deb_u").AccountId)!.Snapshot, fx.Repo, out var saved, out _));
        Assert.Equal(EContainerSocketId.Socket_6, saved!.Cards[itemId.Value].Socket);
    }
}
