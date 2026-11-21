using System.Text.Json;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
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
string statePath = Path.Combine(
    Environment.GetEnvironmentVariable("BAZAAR_STATE") ?? Path.Combine(AppContext.BaseDirectory, "state"),
    "bazaar.db");
var persistence = new BazaarServer.Storage.Persistence(statePath);
builder.Services.AddSingleton(persistence);

builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<PlayerRuleStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<BazaarServer.Tournament.LobbyStore>();

// 卡池与全量规则引擎：从 GameData.db + zh-CN.bytes 加载（只读）
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

var cardPool = BazaarServer.Data.CardPool.Load(dbPath);
builder.Services.AddSingleton(cardPool);
builder.Services.AddSingleton(BazaarServer.Data.MonsterPool.Load(dbPath));
builder.Services.AddSingleton(BazaarServer.Data.LevelUpTable.Load(dbPath));
builder.Services.AddSingleton(BazaarServer.Data.GameModeConfig.Load(dbPath));
builder.Services.AddSingleton(BazaarServer.Data.PvpGhostPool.Load(dataRoot));

BazaarCore.Data.GameDataRepository coreRepo = BazaarCore.Data.GameDatabaseLoader.Load(
    File.Exists(dbPath) ? dbPath : null,
    File.Exists(zhPath) ? zhPath : null);
builder.Services.AddSingleton(coreRepo);
var archetypeIndex = CardArchetypeIndex.Build(cardPool, coreRepo);
builder.Services.AddSingleton(archetypeIndex);
Console.WriteLine($"[CoreEngine] loaded {coreRepo.CardsById.Count} cards, {coreRepo.MonstersById.Count} monsters, unknownTypes={coreRepo.UnknownRuleTypes.Count}");

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
var cachedCatalog = new Lazy<object>(() => archetypeIndex.ToCatalogDto(cardPool, coreRepo));

TempoNetEndpoints.Map(app);
GameServerEndpoints.Map(app);
BazaarServer.Tournament.TournamentEndpoints.Map(app, lobbyStore);
CustomRuleEndpoints.Map(app, playerRuleStore, accountsStore, () => cachedCatalog.Value, sessionStore, persistence);

// 100-CCU 长跑：每 15 分钟驱逐超过 2 小时无活动的空闲会话，并在优雅停机时冲刷所有防抖快照
var evictionCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
    try
    {
        while (await timer.WaitForNextTickAsync(evictionCts.Token).ConfigureAwait(false))
        {
            int evicted = sessionStore.EvictIdleSessions(TimeSpan.FromHours(2));
            if (evicted > 0)
            {
                SessionRegistry.DetachWhereMissing(sid => sessionStore.Get(sid) != null);
                Console.WriteLine($"[SessionStore] evicted {evicted} idle sessions (>2h)");
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

// 静态数据托管（maintenance.json / manifest.json / GameData.db / translations）
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(dataRoot),
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

// 健康检查
app.MapGet("/healthz", () => Results.Json(new { ok = true, uptime = Environment.TickCount64 / 1000 }));

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
Console.WriteLine(" BazaarServer (self-hosted, protocol-compatible)");
Console.WriteLine($" data root : {dataRoot}");
Console.WriteLine($" listening : http://0.0.0.0:{port}");
Console.WriteLine("======================================================");

app.Run();
