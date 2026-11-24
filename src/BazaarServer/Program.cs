using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using BazaarCore.Data;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.TempoNet;

// Wave 4A #10：记录服务端进程启动单调时间戳，确保 /healthz uptime 反映真实进程运行时长而非宿主机开机时长
long serverStartTimestamp = Stopwatch.GetTimestamp();

var builder = WebApplication.CreateBuilder(args);

BazaarServerRuntimeDiagnostics.ConfigureLogging(builder.Logging);

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

// 2. 启动服务端 Mod 热加载主机、社区 Mod 仓库与四层 OverlayLocalizationStore
var modHost = new ServerModHost(rawCoreRepo, dataRoot, stateDir, validVisualWhitelist);
var communityModStore = new CommunityModStore(modHost, stateDir);
builder.Services.AddSingleton(modHost);
builder.Services.AddSingleton(communityModStore);
builder.Services.AddSingleton<WorkshopValidatorService>();
// 严格保留原始基座仓库注册（#118-3：不破坏历史依赖 BaseRepository 的服务；热重载派生视图统一经 ServerRuntimeSnapshotProvider 提供）
builder.Services.AddSingleton<GameDataRepository>(modHost.BaseRepository);

// 3. 全部池化视图直接基于单次共享的 ActiveRepository 派生（零重复 SQLite IO）
var cardPool = CardPool.FromRepository(
    modHost.ActiveRepository,
    dataRoot,
    validWhitelistOverride: validVisualWhitelist,
    visualBindings: modHost.VisualBindingsByCustomId);
var monsterPoolStore = new MonsterPoolStore(MonsterPool.FromRepository(modHost.ActiveRepository), modHost.ActiveRepository);
var ghostPool = PvpGhostPool.Load(dataRoot);
modHost.BindPools(cardPool, monsterPoolStore);

// D2-P2-04 & D8-P2-06：统一按 ModSetHash 原子刷新 OverlayLocalizationStore / LevelUpTable / GameModeConfig / CardArchetypeIndex 与目录 DTO
var runtimeSnapshots = new ServerRuntimeSnapshotProvider(modHost, cardPool);
builder.Services.AddSingleton(runtimeSnapshots);
builder.Services.AddTransient<OverlayLocalizationStore>(sp =>
    sp.GetRequiredService<ServerRuntimeSnapshotProvider>().Current.Localization);
builder.Services.AddTransient<LevelUpTable>(sp =>
    sp.GetRequiredService<ServerRuntimeSnapshotProvider>().Current.LevelUpTable);
builder.Services.AddTransient<GameModeConfig>(sp =>
    sp.GetRequiredService<ServerRuntimeSnapshotProvider>().Current.GameModeConfig);
builder.Services.AddTransient<CardArchetypeIndex>(sp =>
    sp.GetRequiredService<ServerRuntimeSnapshotProvider>().Current.ArchetypeIndex);

builder.Services.AddSingleton(AdminAccessGuard.Default);
builder.Services.AddSingleton(cardPool);
builder.Services.AddSingleton(monsterPoolStore);
builder.Services.AddTransient<List<BazaarServer.Data.MonsterTemplate>>(sp =>
    sp.GetRequiredService<MonsterPoolStore>().SnapshotList());
builder.Services.AddSingleton(ghostPool);

Console.WriteLine(
    $"[CoreEngine] 单次共享加载完成：卡牌={modHost.ActiveRepository.CardsById.Count}（有效资产={cardPool.TotalCards}），" +
    $"野怪={monsterPoolStore.Count}，已启Mod={modHost.EnabledModCount}，词典条数={modHost.ActiveLocalization.EntryCount}，" +
    $"未知规则类型={modHost.ActiveRepository.UnknownRuleTypes.Count}");

// 4. 注册沙盒实验室桥接实现（必须在 MultiplayerEndpointModule.RegisterServices 之前注册以覆盖默认 Null 实现！）
builder.Services.AddSingleton<ISandboxEngineBridge, SandboxEngineService>();
MultiplayerEndpointModule.RegisterServices(builder.Services);

// 端口与监听地址：BAZAAR_BIND（默认 0.0.0.0，伴侣纯单机模式可设 127.0.0.1）、BAZAAR_PORT（默认 8080）
string bindHost = Environment.GetEnvironmentVariable("BAZAAR_BIND")?.Trim();
if (string.IsNullOrEmpty(bindHost)) bindHost = "0.0.0.0";
string port = Environment.GetEnvironmentVariable("BAZAAR_PORT") ?? "8080";
builder.WebHost.UseUrls($"http://{bindHost}:{port}");

bool isLoopbackBind = string.Equals(bindHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(bindHost, "localhost", StringComparison.OrdinalIgnoreCase)
    || string.Equals(bindHost, "::1", StringComparison.OrdinalIgnoreCase);
string workshopModeEnv = (Environment.GetEnvironmentVariable("BAZAAR_WORKSHOP_MODE") ?? "auto").Trim();
if (!isLoopbackBind
    && !AdminAccessGuard.Default.IsTokenConfigured
    && !string.Equals(workshopModeEnv, "readonly", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("[Security][WARN] 服务正监听非回环地址且未配置 BAZAAR_WORKSHOP_TOKEN。公网部署请务必在 Nginx 透传 X-Forwarded-For/X-Real-IP 并配置 BAZAAR_WORKSHOP_MODE=token-only 与高熵 BAZAAR_WORKSHOP_TOKEN。");
}

var app = builder.Build();

// 启动时把存档里的账号加载进内存
try { app.Services.GetRequiredService<AccountStore>().LoadAllFromDb(); }
catch (Exception ex) { Console.WriteLine("[startup] LoadAllFromDb failed: " + ex.Message); }

// 路径前缀剥离（/bz-enter、/bz-server、/bz-static-data），须在显式 UseRouting 之前注册，使直连、本地代理与伴侣模式路由完全一致
BazaarStaticDataPipeline.UseBazaarRoutePrefixStrip(app);
app.UseRouting();

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

// 统一复用 ServerRuntimeSnapshotProvider 随 ModSetHash 原子维护的目录 DTO（消除启动期与回调重复重建）
object GetCurrentCatalog() => runtimeSnapshots.Current.CatalogDto;

TempoNetEndpoints.Map(app);
GameServerEndpoints.Map(app);
BazaarServer.Tournament.TournamentEndpoints.Map(app, lobbyStore);
CustomRuleEndpoints.Map(app, playerRuleStore, accountsStore, GetCurrentCatalog, sessionStore, persistence);
ModEndpoints.Map(app);
WorkshopEndpoints.Map(app);
CommunityModEndpoints.Map(app);
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
            int purgedChatBuckets = SocialEndpoints.PurgeStaleRateBuckets();
            int purgedChestBuckets = TempoNetEndpoints.PurgeStaleRateBuckets();
            int evictedChallengeCache = app.Services.GetService<ChallengeService>()?.EvictStaleCacheEntries() ?? 0;
            if (evicted > 0 || detachedHandles > 0 || purgedCaches > 0 || purgedChatBuckets > 0 || purgedChestBuckets > 0 || evictedChallengeCache > 0)
            {
                Console.WriteLine($"[SessionStore] 定时清理完成：驱逐空闲会话={evicted}，回收句柄={detachedHandles}，清理幂等缓存={purgedCaches}，清理限流桶={purgedChatBuckets + purgedChestBuckets}，驱逐挑战缓存={evictedChallengeCache}");
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

// 动态叠加中文词库（合并 custom_zh_overlay.json 与已启 Mod 哈希词条至 zh-CN.bytes，支持稳定 SHA-256 ETag/304）
BazaarStaticDataPipeline.UseBazaarDynamicZhCnOverlay(app, dataRoot, stateDir, modHost);

// 静态数据稳定 SHA-256 ETag 与 If-None-Match -> 304 Not Modified（配合安卓预置数据 0 首次下载）
BazaarStaticDataPipeline.UseBazaarStaticDataStableEtag(app, dataRoot);

// 静态数据托管（manifest.json / GameData.db / GameData.db.zip / translations / valid_card_ids.json）
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
    uptime = BazaarServerRuntimeDiagnostics.ComputeProcessUptimeSeconds(serverStartTimestamp),
    loadedCards = cardPool.TotalCards,
    totalTemplates = modHost.ActiveRepository.CardsById.Count,
    enabledMods = modHost.EnabledModCount,
    communityMods = communityModStore.Count,
    modSetHash = modHost.ModSetHash,
    localizationEntries = modHost.ActiveLocalization.EntryCount,
    activeSessions = SessionRegistry.ActiveCount,
    ghostBoards = ghostPool.Count,
    memoryMb = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 2),
}));

// 版本门：客户端读 {DataURL}/maintenance.json，同时支持 GET/HEAD，稳定输出仅含 zh-CN 的 locales 与 SHA-256 ETag / 304
app.MapMethods("/maintenance.json", new[] { HttpMethods.Get, HttpMethods.Head }, (HttpContext ctx) => BazaarStaticDataPipeline.ServeMaintenanceJsonAsync(ctx, dataRoot));

Console.WriteLine("======================================================");
Console.WriteLine(" BazaarServer (Wave 3B: Engine + ModHost + 5 Modes)");
Console.WriteLine($" data root : {dataRoot}");
Console.WriteLine($" mods root : {modHost.ModsRootDirectory} (enabled={modHost.EnabledModCount})");
Console.WriteLine($" listening : http://{bindHost}:{port}");
Console.WriteLine("======================================================");

app.Run();

/// <summary>Wave 4A #8 / #10：集中管理服务端日志降噪过滤与真实进程运行时长计算（便于单元测试直调验证）。</summary>
public static class BazaarServerRuntimeDiagnostics
{
    public static void ConfigureLogging(ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.ClearProviders();
        logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        });
        logging.SetMinimumLevel(LogLevel.Information);
        // Wave 4A #8：抑制 ASP.NET Core / Kestrel / Hosting 框架逐请求 Info 日志刷屏，保留清晰业务日志
        logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
    }

    public static long ComputeProcessUptimeSeconds(long startTimestamp, long? nowTimestamp = null)
    {
        long end = nowTimestamp ?? Stopwatch.GetTimestamp();
        if (end <= startTimestamp) return 0L;
        return (long)Stopwatch.GetElapsedTime(startTimestamp, end).TotalSeconds;
    }
}

public static class BazaarStaticDataPipeline
{
    private static readonly PathString[] StrippableRoutePrefixes =
    [
        new PathString("/bz-enter"),
        new PathString("/bz-server"),
        new PathString("/bz-static-data"),
    ];

    private static readonly ConcurrentDictionary<string, (long LastWriteUtcTicks, long Length, string ETag)> FileSha256EtagCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly object DynamicZhBuildLock = new();

    /// <summary>
    /// 动态叠加外挂词典（custom_zh_overlay.json）与已启用 Mod 的 hashOverrides 至官方 SQLite zh-CN.bytes，
    /// 使无客户端插件的安卓官方端也能自动拉取全部 950+ 补全中文词条与 Mod 哈希翻译；无覆盖条目时零开销透传原文件。
    /// </summary>
    public static void UseBazaarDynamicZhCnOverlay(
        IApplicationBuilder app,
        string dataRoot,
        string stateDir,
        ServerModHost modHost)
    {
        string baseZhPath = Path.Combine(Path.GetFullPath(dataRoot), "translations", "zh-CN.bytes");
        string cacheDir = Path.Combine(Path.GetFullPath(stateDir), "cache");

        app.Use(async (ctx, next) =>
        {
            if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
                && string.Equals(ctx.Request.Path.Value, "/translations/zh-CN.bytes", StringComparison.OrdinalIgnoreCase)
                && File.Exists(baseZhPath))
            {
                var exported = modHost.ExportMergedLocalization();
                var hashOverrides = exported.Document.HashOverrides;
                if (hashOverrides != null && hashOverrides.Count > 0)
                {
                    string mergedPath = EnsureMergedZhCnBytesFile(baseZhPath, cacheDir, hashOverrides, exported.Sha256);
                    string etag = ComputeCachedFileSha256Etag(mergedPath);
                    ctx.Response.Headers.ETag = etag;
                    ctx.Response.Headers.CacheControl = "no-cache";

                    if (IsIfNoneMatchSatisfied(ctx.Request.Headers.IfNoneMatch, etag))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status304NotModified;
                        return;
                    }

                    ctx.Response.ContentType = "application/octet-stream";
                    var fi = new FileInfo(mergedPath);
                    ctx.Response.ContentLength = fi.Length;
                    if (HttpMethods.IsHead(ctx.Request.Method))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status200OK;
                        return;
                    }

                    await ctx.Response.SendFileAsync(mergedPath, ctx.RequestAborted);
                    return;
                }
            }

            await next();
        });
    }

    /// <summary>
    /// 基于原版 zh-CN.bytes 与当前叠加词条哈希构造并缓存独立的 SQLite 词库文件（线程安全、原子落盘）。
    /// </summary>
    public static string EnsureMergedZhCnBytesFile(
        string baseZhPath,
        string cacheDir,
        IReadOnlyDictionary<string, string> hashOverrides,
        string overlaySha256)
    {
        string baseEtag = ComputeCachedFileSha256Etag(baseZhPath).Trim('"');
        string keyRaw = $"{baseEtag}|{overlaySha256}|{hashOverrides.Count}";
        string keyHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(keyRaw)))
            .ToLowerInvariant()[..16];

        Directory.CreateDirectory(cacheDir);
        string outPath = Path.Combine(cacheDir, $"zh-CN-overlay-{keyHash}.bytes");
        if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
            return outPath;

        lock (DynamicZhBuildLock)
        {
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                return outPath;

            string tmpPath = outPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(baseZhPath, tmpPath, overwrite: true);

                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = tmpPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                };
                using (var conn = new SqliteConnection(csb.ToString()))
                {
                    conn.Open();
                    using (var pragma = conn.CreateCommand())
                    {
                        pragma.CommandText = "PRAGMA journal_mode=MEMORY; PRAGMA synchronous=OFF;";
                        pragma.ExecuteNonQuery();
                    }
                    using var tx = conn.BeginTransaction();
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO translation(hash, text) VALUES ($h, $t)";
                    var pHash = cmd.CreateParameter();
                    pHash.ParameterName = "$h";
                    var pText = cmd.CreateParameter();
                    pText.ParameterName = "$t";
                    cmd.Parameters.Add(pHash);
                    cmd.Parameters.Add(pText);

                    foreach (var (k, v) in hashOverrides)
                    {
                        if (string.IsNullOrWhiteSpace(k) || string.IsNullOrEmpty(v)) continue;
                        pHash.Value = k.Trim();
                        pText.Value = v;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    using (var seal = conn.CreateCommand())
                    {
                        seal.CommandText = "PRAGMA journal_mode=DELETE;";
                        seal.ExecuteNonQuery();
                    }
                }

                File.Move(tmpPath, outPath, overwrite: true);

                // 清理历史旧缓存（保留最新 4 份防止磁盘膨胀）
                try
                {
                    var oldFiles = Directory.GetFiles(cacheDir, "zh-CN-overlay-*.bytes")
                        .Select(p => new FileInfo(p))
                        .OrderByDescending(fi => fi.LastWriteTimeUtc)
                        .Skip(4);
                    foreach (var old in oldFiles) old.Delete();
                }
                catch { }
            }
            finally
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }

            return outPath;
        }
    }

    /// <summary>
    /// 剥离安卓本地代理 / 伴侣等长前缀（/bz-enter、/bz-server、/bz-static-data），须在 app.UseRouting() 之前调用。
    /// </summary>
    public static void UseBazaarRoutePrefixStrip(IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            PathString path = ctx.Request.Path;
            for (int i = 0; i < StrippableRoutePrefixes.Length; i++)
            {
                if (path.StartsWithSegments(StrippableRoutePrefixes[i], StringComparison.OrdinalIgnoreCase, out PathString remaining))
                {
                    ctx.Request.Path = remaining.HasValue ? remaining : new PathString("/");
                    break;
                }
            }
            await next();
        });
    }

    /// <summary>
    /// 为 /GameData.db.zip 与 /translations/*.bytes 提供基于文件内容 SHA-256 的稳定 ETag 与 If-None-Match -> 304 校验，
    /// 同时保留下游 StaticFileMiddleware 的 Range/206 断点续传与 HEAD 支持。
    /// </summary>
    public static void UseBazaarStaticDataStableEtag(IApplicationBuilder app, string dataRoot)
    {
        string fullRoot = Path.GetFullPath(dataRoot);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        app.Use(async (ctx, next) =>
        {
            if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
            {
                string reqPath = ctx.Request.Path.Value ?? "";
                string? canonicalReqPath = null;
                if (string.Equals(reqPath, "/GameData.db.zip", StringComparison.OrdinalIgnoreCase))
                {
                    canonicalReqPath = "/GameData.db.zip";
                }
                else if (reqPath.StartsWith("/translations/", StringComparison.OrdinalIgnoreCase)
                    && reqPath.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(reqPath, "/translations/zh-CN.bytes", StringComparison.OrdinalIgnoreCase))
                    {
                        canonicalReqPath = "/translations/zh-CN.bytes";
                    }
                    else
                    {
                        string fileName = reqPath["/translations/".Length..];
                        if (!fileName.Contains('/') && !fileName.Contains('\\'))
                        {
                            string transDir = Path.Combine(fullRoot, "translations");
                            string exactCandidate = Path.Combine(transDir, fileName);
                            if (File.Exists(exactCandidate))
                            {
                                canonicalReqPath = "/translations/" + fileName;
                            }
                            else if (Directory.Exists(transDir))
                            {
                                string? matched = Directory.EnumerateFiles(transDir, "*.bytes")
                                    .Select(Path.GetFileName)
                                    .FirstOrDefault(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));
                                if (!string.IsNullOrEmpty(matched))
                                    canonicalReqPath = "/translations/" + matched;
                            }
                        }
                    }
                }

                if (canonicalReqPath != null)
                {
                    string relPath = canonicalReqPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    string candidate = Path.GetFullPath(Path.Combine(fullRoot, relPath));
                    if (candidate.StartsWith(fullRoot, pathComparison) && File.Exists(candidate))
                    {
                        // 规范化请求路径为磁盘真实大小写，确保 Linux 大小写敏感文件系统下 ETag 中间件与下游 StaticFileMiddleware 行为一致
                        if (!string.Equals(reqPath, canonicalReqPath, StringComparison.Ordinal))
                            ctx.Request.Path = new PathString(canonicalReqPath);

                        string etag = ComputeCachedFileSha256Etag(candidate);
                        if (IsIfNoneMatchSatisfied(ctx.Request.Headers.IfNoneMatch, etag))
                        {
                            ctx.Response.Headers.ETag = etag;
                            ctx.Response.Headers.CacheControl = "no-cache";
                            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
                            return;
                        }

                        // 移除原始 If-None-Match，防止下游 StaticFileMiddleware 使用 mtime ETag 产生误判，并在响应头写出前覆写为 SHA-256 ETag
                        ctx.Request.Headers.Remove("If-None-Match");
                        ctx.Response.OnStarting(() =>
                        {
                            if (ctx.Response.StatusCode is >= 200 and < 300)
                            {
                                ctx.Response.Headers.ETag = etag;
                                ctx.Response.Headers.CacheControl = "no-cache";
                            }
                            return Task.CompletedTask;
                        });
                    }
                }
            }

            await next();
        });
    }

    /// <summary>
    /// 计算并缓存单个静态文件的强 SHA-256 ETag（格式："\"<64位小写十六进制>\""）。
    /// </summary>
    public static string ComputeCachedFileSha256Etag(string filePath)
    {
        var fi = new FileInfo(filePath);
        long ticks = fi.LastWriteTimeUtc.Ticks;
        long len = fi.Length;
        if (FileSha256EtagCache.TryGetValue(filePath, out var cached)
            && cached.LastWriteUtcTicks == ticks
            && cached.Length == len)
        {
            return cached.ETag;
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] hash = SHA256.HashData(fs);
        string etag = $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
        FileSha256EtagCache[filePath] = (ticks, len, etag);
        return etag;
    }

    public static bool IsIfNoneMatchSatisfied(Microsoft.Extensions.Primitives.StringValues headerValues, string expectedQuotedEtag)
    {
        if (headerValues.Count == 0 || string.IsNullOrWhiteSpace(expectedQuotedEtag))
            return false;

        string expectedBare = StripWeakAndQuotes(expectedQuotedEtag);
        foreach (string? rawHeader in headerValues)
        {
            if (string.IsNullOrWhiteSpace(rawHeader)) continue;
            foreach (string token in rawHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token == "*") return true;
                if (string.Equals(StripWeakAndQuotes(token), expectedBare, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string StripWeakAndQuotes(string tag)
    {
        string s = tag.Trim();
        if (s.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            s = s[2..].Trim();
        return s.Trim('"');
    }

    /// <summary>
    /// 构建标准化 maintenance.json（默认仅保留 zh-CN locale，防止移动端首启下载 8 国多余翻译包）并支持 SHA-256 ETag / 304。
    /// </summary>
    public static byte[] BuildNormalizedMaintenanceJsonBytes(string dataRoot)
    {
        string path = Path.Combine(dataRoot, "maintenance.json");
        JsonObject rootObj;
        if (File.Exists(path))
        {
            try
            {
                rootObj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                rootObj = new JsonObject();
            }
        }
        else
        {
            rootObj = new JsonObject();
        }

        var statusObj = rootObj["status"] as JsonObject;
        if (statusObj == null)
        {
            statusObj = new JsonObject
            {
                ["systems"] = new JsonObject
                {
                    ["temponet"] = new JsonObject { ["isAvailable"] = true },
                    ["tempoidentity"] = new JsonObject { ["isAvailable"] = true },
                    ["tempoplatform"] = new JsonObject { ["isAvailable"] = true },
                },
                ["versions"] = new JsonObject(),
                ["breakingChange"] = false,
                ["httpGameClientTimeouts"] = new JsonObject
                {
                    ["defaultRequestSeconds"] = 60,
                    ["inRunCommandSeconds"] = 60,
                    ["deleteSessionSeconds"] = 60,
                },
            };
            rootObj["status"] = statusObj;
        }

        // 强制 status.locales 仅保留简体中文 zh-CN（避免安卓客户端首启拉取 7.5 MB 其余语言包）
        statusObj["locales"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "zh-CN",
                ["displayName"] = "简体中文",
            },
        };
        if (rootObj["breakingChange"] == null)
            rootObj["breakingChange"] = false;
        if (rootObj["httpGameClientTimeouts"] == null)
        {
            rootObj["httpGameClientTimeouts"] = new JsonObject
            {
                ["defaultRequestSeconds"] = 60,
                ["inRunCommandSeconds"] = 60,
                ["deleteSessionSeconds"] = 60,
            };
        }

        return System.Text.Encoding.UTF8.GetBytes(rootObj.ToJsonString());
    }

    public static Task ServeMaintenanceJsonAsync(HttpContext ctx, string dataRoot)
    {
        byte[] payload = BuildNormalizedMaintenanceJsonBytes(dataRoot);
        string etag = $"\"{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}\"";
        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.CacheControl = "no-cache";
        if (IsIfNoneMatchSatisfied(ctx.Request.Headers.IfNoneMatch, etag))
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }

        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength = payload.Length;
        return HttpMethods.IsHead(ctx.Request.Method)
            ? Task.CompletedTask
            : ctx.Response.Body.WriteAsync(payload, 0, payload.Length);
    }
}
