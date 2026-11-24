using System.Net;
using System.Text.Json;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Commands;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Storage;
using BazaarServer.Tournament;
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
/// Wave 2A 野怪排期、观战/活跃绑定会话隔离与幽灵池分区纯函数强断言测试套件（[Collection("GlobalStaticState")]）。
/// 覆盖 P0-21、P0-26、P0-30、D6-P1-18。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave2A_MonstersSpectateAndGhostPartitionTests
{
    private const string SmallItemCarrier = "03b1fd16-6b0d-4847-a76f-6c586f7c5958";
    private const string CombatEncCarrier = "bd0711a6-d9d9-4a47-8a18-8f991057f7dd"; // Kyver Drone CombatEncounter

    private static string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void P021_OfficialDaySchedule_IntegratesCustomMonstersByDayAndSlot_AndBuildsMonsterPreview()
    {
        var shared = SharedGameDataFixture.Shared;

        // 1. 纯原版池（0 自定义野怪）：同种子两次 SelectDayMonsters 必须 100% 确定性一致
        var vanilla1 = OfficialDaySchedule.SelectDayMonsters(day: 3, count: 3, shared.Pool, shared.Repo, new Random(12345));
        var vanilla2 = OfficialDaySchedule.SelectDayMonsters(day: 3, count: 3, shared.Pool, shared.Repo, new Random(12345));
        Assert.Equal(vanilla1.Select(c => c.Id), vanilla2.Select(c => c.Id));
        Assert.Empty(OfficialDaySchedule.GetEligibleCustomMonstersForSlot(day: 3, slotIndex: 1, shared.Pool, shared.Repo));

        // 2. 构造自定义野怪遭遇（Day 3..5，中槽 Silver），验证天数/槽位精准命中 + SelectDayMonsters 真实抽取 + MonsterPreview 绑定完整
        var customMonster = new BazaarCore.Models.MonsterTemplate
        {
            Id = "w2a-mon:ember_golem_mob",
            InternalName = "Ember Golem",
            Player = new MonsterPlayerData
            {
                Attributes = new Dictionary<string, double>
                {
                    ["Level"] = 4,
                    ["HealthMax"] = 620
                },
                Hand = new MonsterHandData
                {
                    Items = new List<MonsterCardEntry>
                    {
                        new() { TemplateId = SmallItemCarrier, Tier = "Silver", SocketId = "0" }
                    }
                },
                Skills = new List<MonsterCardEntry>()
            }
        };

        var evDef = new CustomEventDefinition
        {
            EventId = "w2a-mon:enc_ember_golem",
            BaseVisualTemplateId = CombatEncCarrier,
            TitleZh = "余烬魔像",
            TitleEn = "Ember Golem",
            StartingTier = "Silver",
            CombatMonster = customMonster,
            Schedule = new CustomEventSchedule
            {
                MinDay = 3,
                MaxDay = 5,
                DifficultySlot = "Silver",
                Weight = 2.0
            }
        };

        var combatEncBase = shared.Repo.GetCard(CombatEncCarrier)!;
        var encCoreTpl = evDef.ToCardDefinition().ToCardTemplate(combatEncBase, "w2a-mon");
        var isoRepo = shared.Repo.CreateWithOverlay(new[] { encCoreTpl }, new[] { customMonster });
        var monPkgForPool = new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest { Id = "w2a-mon", Name = "野怪包", Version = "1.0.0", Scope = "multiplayer-safe" },
            Events = { evDef }
        };
        ModLoader.BuildVisualBindings(monPkgForPool);
        var isoPool = shared.Pool.CreateIsolatedOverlay(isoRepo, monPkgForPool.VisualBindingsByCustomId);

        // Day 2 超出 MinDay=3 -> 不命中；Day 3 左槽(0)/右槽(2) -> 不命中；Day 3 中槽(1) -> 精准命中！
        Assert.Empty(OfficialDaySchedule.GetEligibleCustomMonstersForSlot(day: 2, slotIndex: 1, isoPool, isoRepo));
        Assert.Empty(OfficialDaySchedule.GetEligibleCustomMonstersForSlot(day: 3, slotIndex: 0, isoPool, isoRepo));
        Assert.Empty(OfficialDaySchedule.GetEligibleCustomMonstersForSlot(day: 3, slotIndex: 2, isoPool, isoRepo));
        var hitMid = OfficialDaySchedule.GetEligibleCustomMonstersForSlot(day: 3, slotIndex: 1, isoPool, isoRepo);
        Assert.Contains(hitMid, c => c.Id == "w2a-mon:enc_ember_golem");

        // 多轮随机抽样，SelectDayMonsters(day=3) 必须真实抽到自定义野怪
        bool sampledCustom = false;
        for (int seed = 1; seed <= 80; seed++)
        {
            var picked = OfficialDaySchedule.SelectDayMonsters(day: 3, count: 3, isoPool, isoRepo, new Random(seed));
            if (picked.Any(c => c.Id == "w2a-mon:enc_ember_golem"))
            {
                sampledCustom = true;
                break;
            }
        }
        Assert.True(sampledCustom, "Day 3 抽样 80 次未能抽中合法中槽自定义野怪");

        // 3. 验证 ModLoader.BuildVisualBindings 为 ev.CombatMonster 与独立 pkg.Monsters 均生成含 MonsterPreview（含 Size）的视觉绑定
        var standaloneMon = new BazaarCore.Models.MonsterTemplate
        {
            Id = "w2a-mon:solo_drake",
            InternalName = "Solo Drake",
            Player = new MonsterPlayerData
            {
                Attributes = new Dictionary<string, double>
                {
                    ["Level"] = 6,
                    ["HealthMax"] = 1100
                },
                Hand = new MonsterHandData
                {
                    Items = new List<MonsterCardEntry>
                    {
                        new() { TemplateId = SmallItemCarrier, Tier = "Gold", SocketId = "0" }
                    }
                }
            }
        };
        var pkg = new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest { Id = "w2a-mon", Name = "野怪包", Version = "1.0.0", Scope = "multiplayer-safe" },
            Events = { evDef },
            Monsters = { standaloneMon }
        };

        ModLoader.BuildVisualBindings(pkg);
        Assert.True(pkg.VisualBindingsByCustomId.TryGetValue("w2a-mon:enc_ember_golem", out var encBinding));
        Assert.True(pkg.VisualBindingsByCustomId.TryGetValue("w2a-mon:solo_drake", out var soloBinding));
        Assert.NotNull(encBinding?.MonsterPreview);
        Assert.Equal(620, encBinding!.MonsterPreview!.HealthMax);
        Assert.Equal("Silver", encBinding.MonsterPreview.DifficultySlot);
        Assert.Single(encBinding.MonsterPreview.Items);
        Assert.Equal("Small", encBinding.MonsterPreview.Items[0].Size);

        Assert.NotNull(soloBinding?.MonsterPreview);
        Assert.Equal(1100, soloBinding!.MonsterPreview!.HealthMax);
        Assert.Single(soloBinding.MonsterPreview.Items);
    }

    [Fact]
    public async Task P026_D6P118_SpectateAndActiveBindings_UseTargetRunModContext_Headers_WatchCode404_AndDefault300Hp()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempRoot = CreateTempDir("w2a_p026");
        WebApplication? app = null;
        HttpClient? http = null;
        string? registeredSid = null;
        try
        {
            string modsDir = Path.Combine(tempRoot, "mods");
            string stateDir = Path.Combine(tempRoot, "state");
            string modA = Path.Combine(modsDir, "spec-mod");
            Directory.CreateDirectory(Path.Combine(modA, "cards"));
            Directory.CreateDirectory(stateDir);

            File.WriteAllText(Path.Combine(modA, "mod.json"), """{"id":"spec-mod","name":"观战测试包","version":"1.0.0","author":"w2a","scope":"multiplayer-safe"}""");
            File.WriteAllText(Path.Combine(modA, "cards", "spec_sword.json"), $$"""
            {
              "customId": "spec-mod:astral_sword",
              "baseVisualTemplateId": "{{SmallItemCarrier}}",
              "type": "Item",
              "size": "Small",
              "startingTier": "Bronze",
              "titleZh": "星界之剑",
              "titleEn": "Astral Sword",
              "attributes": { "CooldownMax": 3000, "DamageAmount": 25 }
            }
            """);

            var db = new Persistence(Path.Combine(stateDir, "state.db"));
            var accounts = new AccountStore(db);
            var sessions = new SessionStore();
            var lobbies = new LobbyStore(db);
            var sseHub = new SseEventHub();
            var frameStore = new SpectateFrameStore(sseHub);
            var modHost = new ServerModHost(shared.Repo, shared.DataDir, stateDir, shared.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            // 全局禁用 spec-mod，仅让目标观战会话单局启用，实测观战是否隔离对齐目标会话的 RunModContext！
            modHost.ToggleMod("spec-mod", false);

            var hostAcc = accounts.GetOrCreateByName("spec_host_w2a", "game", "d1");
            var lobby = lobbies.Create(hostAcc, "观战Mod房", "Tournament", 4, isPublic: true);

            var run = new RunState
            {
                RunId = $"run_spec_{Guid.NewGuid():N}",
                AccountId = hostAcc.AccountId.ToString("N"),
                Username = hostAcc.Username,
                Hero = EHero.Vanessa,
                VisualBindingsRevision = 7,
                CustomRules = new CustomRunRuleSet { SelectedModIds = new List<string> { "spec-mod" } }
            };
            // 不手动设置 HealthMax，验证 D6-P1-18 缺省回退 300
            run.PlayerAttributes.Remove(EPlayerAttributeType.HealthMax);
            run.Cards["inst_astral_1"] = new RunState.CardEntry
            {
                InstanceId = "inst_astral_1",
                TemplateId = Guid.Parse(SmallItemCarrier),
                CustomCardId = "spec-mod:astral_sword",
                Type = ECardType.Item,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_0
            };

            var sess = sessions.Create(hostAcc.AccountId.ToString("N"), hostAcc.Username, new InitializeRunCommand());
            registeredSid = sess.Sid;
            SessionRegistry.Attach(sess.Sid, run, null!);

            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(accounts);
            builder.Services.AddSingleton(sessions);
            builder.Services.AddSingleton(lobbies);
            builder.Services.AddSingleton(sseHub);
            builder.Services.AddSingleton(frameStore);
            builder.Services.AddSingleton(shared.Repo);
            builder.Services.AddSingleton(modHost);

            app = builder.Build();
            ModEndpoints.Map(app);
            SpectateEndpoints.Map(app);
            await app.StartAsync();

            string baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };

            string expectedHash = ServerModHost.ComputeEffectiveModSetHash(run.CustomRules.SelectedModIds, modHost);
            Assert.False(string.IsNullOrEmpty(expectedHash));

            // 1. GET /api/spectate/{code}/players/{aid}/board-dto
            using (var resp = await http.GetAsync($"/api/spectate/{lobby.Code}/players/{hostAcc.AccountId:N}/board-dto"))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("7", resp.Headers.GetValues("X-Visual-Bindings-Rev").Single());
                Assert.Equal(expectedHash, resp.Headers.GetValues("X-Mod-Set-Hash").Single());

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                Assert.Equal(300, root.GetProperty("stats").GetProperty("healthMax").GetInt32());
                var item = root.GetProperty("handItems")[0];
                Assert.Equal("spec-mod:astral_sword", item.GetProperty("customCardId").GetString());
                Assert.Equal("星界之剑", item.GetProperty("nameZh").GetString());
                var vb = item.GetProperty("visualBinding");
                Assert.Equal("inst_astral_1", vb.GetProperty("instanceId").GetString());
                Assert.Equal("Astral Sword", vb.GetProperty("titleEn").GetString());
            }

            // 2. GET /api/mods/active-bindings?watchCode=...&accountId=...
            using (var resp = await http.GetAsync($"/api/mods/active-bindings?watchCode={lobby.Code}&accountId={hostAcc.AccountId:N}"))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("7", resp.Headers.GetValues("X-Visual-Bindings-Rev").Single());
                Assert.Equal(expectedHash, resp.Headers.GetValues("X-Mod-Set-Hash").Single());

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                Assert.True(root.GetProperty("ok").GetBoolean());
                Assert.Equal(run.RunId, root.GetProperty("runId").GetString());
                Assert.Equal(7, root.GetProperty("revision").GetInt32());
                Assert.Equal(expectedHash, root.GetProperty("modSetHash").GetString());
                Assert.True(root.GetProperty("bindingCount").GetInt32() >= 1);
                Assert.True(root.GetProperty("bindings").TryGetProperty("inst_astral_1", out _));
                Assert.True(root.TryGetProperty("templateCatalog", out _));
            }

            // 3. 非法 watchCode 必须返回 404
            using (var badResp = await http.GetAsync("/api/mods/active-bindings?watchCode=NO_SUCH_ROOM_999"))
            {
                Assert.Equal(HttpStatusCode.NotFound, badResp.StatusCode);
            }
        }
        finally
        {
            if (registeredSid != null) SessionRegistry.Detach(registeredSid);
            try { http?.Dispose(); } catch { }
            if (app != null)
            {
                try { await app.StopAsync(); } catch { }
                try { await app.DisposeAsync(); } catch { }
                try { app.Services.GetService<Persistence>()?.Dispose(); } catch { }
            }
            SqliteConnection.ClearAllPools();
            SafeDeleteDir(tempRoot);
        }
    }

    [Fact]
    public void P030_ResolveEffectiveGhostPoolPartition_StandardModCustomNone_AndOrderIndependence()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempRoot = CreateTempDir("w2a_p030");
        try
        {
            string modsDir = Path.Combine(tempRoot, "mods");
            string stateDir = Path.Combine(tempRoot, "state");
            Directory.CreateDirectory(stateDir);

            void WriteMod(string id, string scope)
            {
                string dir = Path.Combine(modsDir, id);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "mod.json"), $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","scope":"{{scope}}"}""");
            }

            WriteMod("cosmetic-pack", "cosmetic-only");
            WriteMod("play-a", "multiplayer-safe");
            WriteMod("play-b", "multiplayer-safe");

            var host = new ServerModHost(shared.Repo, shared.DataDir, stateDir, shared.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            host.ToggleMod("cosmetic-pack", false);
            host.ToggleMod("play-a", false);
            host.ToggleMod("play-b", false);

            // 1. 纯原版或仅 cosmetic-only -> 统一返回 "standard"（对齐 PvpGhostPool.StandardPartition）
            Assert.Equal("standard", ServerModHost.ResolveEffectiveGhostPoolPartition(null, isCustomRule: false, host));
            Assert.Equal("standard", ServerModHost.ResolveEffectiveGhostPoolPartition(new[] { "cosmetic-pack" }, isCustomRule: false, host));

            // 2. 纯自定义规则（无玩法 Mod）-> 返回规则分区
            Assert.Equal("custom", ServerModHost.ResolveEffectiveGhostPoolPartition(null, isCustomRule: true, host));
            var endlessRules = new CustomRunRuleSet { SpecialModeId = ESpecialModeId.EndlessTower };
            Assert.Equal(endlessRules.ResolveGhostPoolPartition(), ServerModHost.ResolveEffectiveGhostPoolPartition(endlessRules, null, hasCustomCardInRun: false, host));

            // 3. 标准规则 + 玩法 Mod -> "mod:<hash12>"，且顺序无关
            string pAB = ServerModHost.ResolveEffectiveGhostPoolPartition(new[] { "play-a", "play-b" }, isCustomRule: false, host);
            string pBA = ServerModHost.ResolveEffectiveGhostPoolPartition(new[] { "play-b", "play-a" }, isCustomRule: false, host);
            Assert.StartsWith("mod:", pAB);
            Assert.Equal(16, pAB.Length); // "mod:" (4) + 12 hex
            Assert.Equal(pAB, pBA);

            // 4. 自定义规则 + 玩法 Mod -> "mod:<hash12>+<rulePart>"
            string pModEndless = ServerModHost.ResolveEffectiveGhostPoolPartition(endlessRules, new[] { "play-a" }, hasCustomCardInRun: false, host);
            Assert.Contains("+", pModEndless);
            Assert.StartsWith("mod:", pModEndless);
            Assert.EndsWith("+" + endlessRules.ResolveGhostPoolPartition(), pModEndless);

            // 5. 局内存在未登记自建卡或沙盒禁用 -> "none"（绝不污染标准池）
            Assert.Equal("none", ServerModHost.ResolveEffectiveGhostPoolPartition(new CustomRunRuleSet(), null, hasCustomCardInRun: true, host));
            var sandboxRules = new CustomRunRuleSet { SpecialModeId = ESpecialModeId.SandboxLab };
            Assert.Equal("none", ServerModHost.ResolveEffectiveGhostPoolPartition(sandboxRules, new[] { "play-a" }, hasCustomCardInRun: false, host));
        }
        finally
        {
            SafeDeleteDir(tempRoot);
        }
    }
}
