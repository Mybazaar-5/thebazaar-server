using System.Text.Json;
using BazaarCore.Data;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.TempoNet;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

// 持久化：账号与对局状态存 SQLite（必须先于 AccountStore 注册）
string stateDir = Environment.GetEnvironmentVariable("BAZAAR_STATE") ?? Path.Combine(AppContext.BaseDirectory, "state");
Directory.CreateDirectory(stateDir);
string statePath = Path.Combine(stateDir, "bazaar.db");
var persistence = new BazaarServer.Storage.Persistence(statePath);
builder.Services.AddSingleton(persistence);

builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<PlayerRuleStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<BazaarServer.Tournament.LobbyStore>();

// 性能优化（Wave 3B）：共享单次 GameData.db + zh-CN.bytes 加载，彻底消灭启动期 5 次重复 SQLite 解析与 21.5MB 过期 cards.json 读取
string dataRoot = ResolveDataRoot();
Directory.CreateDirectory(dataRoot);
string dbPath = Path.Combine(dataRoot, "GameData.db");
string zhPath = Path.Combine(dataRoot, "translations", "zh-CN.bytes");

static string ResolveDataRoot()
{
    string? env = Environment.GetEnvironmentVariable("BAZAAR_DATA");
    if (!string.IsNullOrWhiteSpace(env)) return env;

    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "data"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "data")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "data")),
        Path.Combine(Environment.CurrentDirectory, "data"),
        Path.Combine(Environment.CurrentDirectory, "dist", "data"),
        "/home/ubuntu/bazaar/data",
    };

    foreach (string dir in candidates)
    {
        if (Directory.Exists(dir) &&
            (File.Exists(Path.Combine(dir, "GameData.db")) ||
             File.Exists(Path.Combine(dir, "ghost_pool_balanced.jsonl"))))
        {
            return dir;
        }
    }

    foreach (string dir in candidates)
    {
        if (Directory.Exists(dir)) return dir;
    }

    return Path.Combine(AppContext.BaseDirectory, "data");
}

static HashSet<string>? TryLoadValidAssetWhitelist(string dataRoot)
{
    var candidates = new[]
    {
        Path.Combine(dataRoot, "valid_card_ids.json"),
        Path.Combine(Environment.CurrentDirectory, "valid_card_ids.json"),
        Path.Combine(Environment.CurrentDirectory, "tools", "data", "valid_card_ids.json"),
    };
    foreach (string p in candidates)
    {
        if (!File.Exists(p)) continue;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var root = doc.RootElement;
            JsonElement arr = default;
            if (root.ValueKind == JsonValueKind.Array)
            {
                arr = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("valid_ids", out var validIdsEl) &&
                     validIdsEl.ValueKind == JsonValueKind.Array)
            {
                arr = validIdsEl;
            }

            if (arr.ValueKind == JsonValueKind.Array)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        set.Add(item.GetString()!);
                }
                if (set.Count > 0)
                    return set;
            }
        }
        catch { }
    }
    return null;
}

// 1. 单次加载权威 GameData.db + 官方简体中文词库
var rawCoreRepo = GameDatabaseLoader.Load(
    File.Exists(dbPath) ? dbPath : null,
    File.Exists(zhPath) ? zhPath : null);
var validVisualWhitelist = TryLoadValidAssetWhitelist(dataRoot);

// 2. 启动服务端 Mod 热加载主机与四层 OverlayLocalizationStore
var modHost = new ServerModHost(rawCoreRepo, dataRoot, stateDir, validVisualWhitelist);
builder.Services.AddSingleton(modHost);
builder.Services.AddSingleton<WorkshopValidatorService>();
builder.Services.AddSingleton<OverlayLocalizationStore>(sp => sp.GetRequiredService<ServerModHost>().ActiveLocalization);
builder.Services.AddSingleton<GameDataRepository>(modHost.BaseRepository);

// 3. 全部池化视图直接基于单次共享的 ActiveRepository 派生（零重复 SQLite IO）
var cardPool = CardPool.FromRepository(
    modHost.ActiveRepository,
    dataRoot,
    validWhitelistOverride: validVisualWhitelist,
    visualBindings: modHost.VisualBindingsByCustomId);
var monsterPool = MonsterPool.FromRepository(modHost.ActiveRepository);
var levelUpTable = LevelUpTable.FromRepository(modHost.ActiveRepository);
var gameModeConfig = GameModeConfig.FromRepository(modHost.ActiveRepository);
var ghostPool = PvpGhostPool.Load(dataRoot);
modHost.BindPools(cardPool, monsterPool);

builder.Services.AddSingleton(cardPool);
builder.Services.AddSingleton(monsterPool);
builder.Services.AddSingleton(levelUpTable);
builder.Services.AddSingleton(gameModeConfig);
builder.Services.AddSingleton(ghostPool);

var archetypeIndex = CardArchetypeIndex.Build(cardPool, modHost.ActiveRepository);
builder.Services.AddSingleton(archetypeIndex);

Console.WriteLine(
    $"[CoreEngine] 单次共享加载完成：卡牌={modHost.ActiveRepository.CardsById.Count}（有效资产={cardPool.TotalCards}），" +
    $"野怪={monsterPool.Count}，已启Mod={modHost.EnabledModCount}，词典条数={modHost.ActiveLocalization.EntryCount}，" +
    $"未知规则类型={modHost.ActiveRepository.UnknownRuleTypes.Count}");

// 4. 注册沙盒实验室桥接实现（必须在 MultiplayerEndpointModule.RegisterServices 之前注册以覆盖默认 Null 实现！）
builder.Services.AddSingleton<ISandboxEngineBridge, SandboxEngineService>();
MultiplayerEndpointModule.RegisterServices(builder.Services);

// 端口：BAZAAR_PORT（默认 8080）；监听 0.0.0.0 以便公网直连
string port = Environment.GetEnvironmentVariable("BAZAAR_PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// 启动时把存档里的账号加载进内存
try { app.Services.GetRequiredService<AccountStore>().LoadAllFromDb(); }
catch (Exception ex) { Console.WriteLine("[startup] LoadAllFromDb failed: " + ex.Message); }

// 请求日志（只记方法与路径，不记 body）
app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next();
    sw.Stop();
    if (!ctx.Request.Path.StartsWithSegments("/api/time"))
    {
        Console.WriteLine($"{ctx.Request.Method,-6} {ctx.Request.Path}{ctx.Request.QueryString} -> {ctx.Response.StatusCode} ({sw.ElapsedMilliseconds} ms)");
    }
});

var accountsStore = app.Services.GetRequiredService<AccountStore>();
var playerRuleStore = app.Services.GetRequiredService<PlayerRuleStore>();
var sessionStore = app.Services.GetRequiredService<SessionStore>();
var lobbyStore = app.Services.GetRequiredService<BazaarServer.Tournament.LobbyStore>();
var spectateRecorder = app.Services.GetService<ISpectateFrameRecorder>();

// 绑定空闲会话驱逐回调：自动释放 SessionRegistry、RidCaches 与观战帧缓存
sessionStore.SessionEvicted += evictedSession =>
{
    SessionRegistry.Detach(evictedSession.Sid);
    GameServerEndpoints.DetachRidCache(evictedSession.Sid);
    try { spectateRecorder?.ClearForAccount(evictedSession.AccountId); } catch { }
};

// 缓存流派与卡牌目录（随 ModSetHash 自动感知热重载失效重建）
string cachedCatalogHash = "";
object? cachedCatalogObj = null;
object catalogSync = new();
object GetCurrentCatalog()
{
    string curHash = modHost.ModSetHash;
    lock (catalogSync)
    {
        if (cachedCatalogObj != null && string.Equals(cachedCatalogHash, curHash, StringComparison.Ordinal))
            return cachedCatalogObj;
        var activeRepo = modHost.ActiveRepository;
        var freshIndex = CardArchetypeIndex.Build(cardPool, activeRepo);
        cachedCatalogObj = freshIndex.ToCatalogDto(cardPool, activeRepo);
        cachedCatalogHash = curHash;
        return cachedCatalogObj;
    }
}

TempoNetEndpoints.Map(app);
GameServerEndpoints.Map(app);
BazaarServer.Tournament.TournamentEndpoints.Map(app, lobbyStore);
CustomRuleEndpoints.Map(app, playerRuleStore, accountsStore, GetCurrentCatalog, sessionStore, persistence);
ModEndpoints.Map(app);
WorkshopEndpoints.Map(app);
ClientUpdateEndpoints.Map(app, dataRoot);
MultiplayerEndpointModule.MapEndpoints(app);

// 100-CCU 长跑：每 15 分钟驱逐超过 2 小时无活动的空闲会话，同步清理孤儿 RidCaches
var evictionCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
    try
    {
        while (await timer.WaitForNextTickAsync(evictionCts.Token).ConfigureAwait(false))
        {
            int evicted = sessionStore.EvictIdleSessions(TimeSpan.FromHours(2));
            int detachedHandles = SessionRegistry.DetachWhereMissing(sid => sessionStore.Get(sid) != null);
            int purgedCaches = GameServerEndpoints.PurgeOrphanRidCaches(sid => sessionStore.Get(sid) != null);
            if (evicted > 0 || detachedHandles > 0 || purgedCaches > 0)
            {
                Console.WriteLine($"[SessionStore] 定时清理完成：驱逐空闲会话={evicted}，回收句柄={detachedHandles}，清理幂等缓存={purgedCaches}");
            }
        }
    }
    catch (OperationCanceledException) { }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    try { evictionCts.Cancel(); } catch { }
    try { persistence.FlushAllPending(); } catch { }
});

// 拦截过期的 21.5MB 静态 JSON 文件，防止旧客户端拉取过时数据
var blockedLegacyJsonPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "/cards.json",
    "/monsters.json",
    "/levelups.json",
    "/gamemodes.json",
};
app.Use(async (ctx, next) =>
{
    if (blockedLegacyJsonPaths.Contains(ctx.Request.Path.Value ?? ""))
    {
        ctx.Response.StatusCode = StatusCodes.Status410Gone;
        return;
    }
    await next();
});

// 静态数据托管（maintenance.json / manifest.json / GameData.db / translations / valid_card_ids.json）
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(dataRoot),
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

// 健康检查（保持原有 ok: true 与 uptime 100% 兼容，并附带完整运行态指标摘要）
app.MapGet("/healthz", () => Results.Json(new
{
    ok = true,
    uptime = Environment.TickCount64 / 1000,
    loadedCards = cardPool.TotalCards,
    totalTemplates = modHost.ActiveRepository.CardsById.Count,
    enabledMods = modHost.EnabledModCount,
    modSetHash = modHost.ModSetHash,
    localizationEntries = modHost.ActiveLocalization.EntryCount,
    activeSessions = SessionRegistry.ActiveCount,
    ghostBoards = ghostPool.Count,
    memoryMb = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 2),
}));

// 版本门：客户端读 {DataURL}/maintenance.json，这里也提供一份兜底
app.MapGet("/maintenance.json", (HttpContext ctx) =>
{
    string path = Path.Combine(dataRoot, "maintenance.json");
    if (File.Exists(path))
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.SendFileAsync(path);
    }
    ctx.Response.ContentType = "application/json";
    return ctx.Response.WriteAsync(JsonSerializer.Serialize(new
    {
        status = new
        {
            systems = new
            {
                temponet = new { isAvailable = true },
                tempoidentity = new { isAvailable = true },
                tempoplatform = new { isAvailable = true },
            },
            versions = new Dictionary<string, string>(),
        },
        breakingChange = false,
        httpGameClientTimeouts = new
        {
            defaultRequestSeconds = 60,
            inRunCommandSeconds = 60,
            deleteSessionSeconds = 60,
        },
        locales = new[] { new { code = "zh-CN", name = "简体中文" }, new { code = "en-US", name = "English" } },
    }));
});

Console.WriteLine("======================================================");
Console.WriteLine(" BazaarServer (Wave 3B: Engine + ModHost + 5 Modes)");
Console.WriteLine($" data root : {dataRoot}");
Console.WriteLine($" mods root : {modHost.ModsRootDirectory} (enabled={modHost.EnabledModCount})");
Console.WriteLine($" listening : http://0.0.0.0:{port}");
Console.WriteLine("======================================================");

app.Run();
