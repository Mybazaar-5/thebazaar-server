using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Cards.Enchantments;
using BazaarGameShared.Domain.Cards.Encounter.Pedestal;
using BazaarGameShared.Domain.Cards.Encounter.Pedestal.Behaviors;
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Prerequisites.Conditionals;
using BazaarLauncher.Core;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BazaarLauncher;

/// <summary>
/// 进程内对局驱动器：在游戏进程里直接调 Cmd / Data / AppState，不碰鼠标和前台。
///
/// 对外是一个 HTTP 控制面（只监听 127.0.0.1）：
///   GET  /health         驱动与对局状态健康检查
///   GET  /state          当前对局快照
///   GET  /inspect        读取 Unity UI / 内存实时渲染状态（支持 ?compact=1&amp;ui=0|1）
///   GET  /combataudit    读取战斗触发明细审计日志（?since=seq）
///   POST /encounterstep  点选奇遇选项卡 EncounterStep（支持 waitIdle / includeCompactState）
///   POST /move           精准移动 / 挤位（支持 waitIdle / includeCompactState）
///   POST /resume         主大厅触发点击 Resume Run 继续对局
///   POST /concede        触发放弃当前对局（局内 AbandonRun 或大厅 /runs/active/concede）
///   POST /drive          通用操作分发 {"op":"..."}（支持 waitIdle / includeCompactState）
///   GET  /screenshot     当前画面 PNG
///   GET  /events         事件流
///   GET  /log            最近日志
///   GET  /custom-ui/state  私服玩法浮窗状态快照
///   POST /custom-ui/open   打开/切换私服玩法浮窗 {"open":true,"tab":"normal"|"tournament"}
///   POST /custom-ui/apply  应用并保存 CustomRunRuleSet JSON 到 UI 与服务端
///
/// Unity API 只能在主线程调，所以 HTTP 线程只入队，由 Update() 泵执行；
/// 涉及外部 HTTP 的操作（如 /runs/active/summary 或 /runs/active/concede）在后台线程完成后再切回主线程更新 UI。
/// </summary>
internal sealed class GameDriver : MonoBehaviour
{
    internal static GameDriver Instance;

    private const int DefaultPort = 27182;

    private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private static readonly ConcurrentQueue<IdleWaiter> _idleWaiters = new();
    private static readonly ConcurrentQueue<string> _logRing = new();
    private static HttpListener _listener;
    private static bool _started;
    private static string _activeToken = string.Empty;
    // P2-12：记录最近一次 /resume 成功派发时刻，为慢速重连铺牌提供自适应 pre_wait 宽限期
    private static long _lastResumeAtTicks;
    // P2-10：记录最近一次进入 hovercard 升阶预览的 CardController，便于隐藏或切卡时配对调用 ExitUpgradePreview()
    private static Component _lastHoverUpgradePreviewCtrl;
    // P0-01：缓存祭坛条件求值上下文构造器与 CanCardUpgrade 委托，避免重复反射扫描
    private static ConstructorInfo _cardCondContextCtor;
    private static MethodInfo _canCardUpgradeMethod;

    internal static bool IsRunning => _started && _listener != null && _listener.IsListening;
    internal static string ActiveToken => _activeToken;

    internal static bool ShouldAutoEnable()
    {
        try
        {
            if (BazaarLauncherPlugin.EnableGameDriver?.Value == true) return true;
            string envEnable = Environment.GetEnvironmentVariable("BAZAAR_DRIVER_ENABLE");
            if (string.Equals(envEnable, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(envEnable, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string envPort = Environment.GetEnvironmentVariable("BAZAAR_DRIVER_PORT");
            if (!string.IsNullOrWhiteSpace(envPort)) return true;
        }
        catch { }
        return false;
    }

    internal static void Shutdown()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
        _started = false;
        if (Instance != null)
        {
            var go = Instance.gameObject;
            Instance = null;
            if (go != null) Destroy(go);
        }
    }

    private sealed class IdleWaiter
    {
        public readonly TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly bool IncludeCompactState;
        public readonly bool IncludeUi;
        public readonly long CreatedTimestamp = Stopwatch.GetTimestamp();
        public volatile bool Cancelled;
        public Dictionary<string, object> CompactState;

        public IdleWaiter(bool includeCompactState, bool includeUi)
        {
            IncludeCompactState = includeCompactState;
            IncludeUi = includeUi;
        }
    }

    internal static void EnsureInstalled()
    {
        if (Instance != null) return;
        var go = new GameObject("BazaarGameDriver");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<GameDriver>();
        BazaarLauncherPlugin.Log.LogInfo("GameDriver installed");
    }

    private void Awake()
    {
        Instance = this;
        StartServer();
    }

    private void Update()
    {
        GameInspector.SampleFrame(Time.unscaledDeltaTime);

        // 泵：把 HTTP 线程排进来的活拿到主线程跑
        int guard = 0;
        while (_mainThreadQueue.TryDequeue(out var action) && guard++ < 64)
        {
            try { action(); }
            catch (Exception ex) { BazaarLauncherPlugin.Log.LogError("driver action: " + ex); }
        }

        CombatPatch.TryAutoExitReplayOnUpdate();
        ProcessIdleWaiters();
    }

    private static void ProcessIdleWaiters()
    {
        if (_idleWaiters.IsEmpty) return;
        if (GameInspector.IsClientBusy(out _)) return;

        while (_idleWaiters.TryPeek(out var head) && head.Cancelled)
        {
            _idleWaiters.TryDequeue(out _);
        }
        if (!_idleWaiters.TryPeek(out var oldest)) return;

        double waitedMs = (Stopwatch.GetTimestamp() - oldest.CreatedTimestamp) * 1000.0 / Stopwatch.Frequency;
        if (waitedMs < 400.0 && !AreSelectionBuyTargetsReady(null)) return;

        Dictionary<string, object> sharedUi = null;
        Dictionary<string, object> sharedNoUi = null;

        while (_idleWaiters.TryDequeue(out var waiter))
        {
            if (waiter.Cancelled) continue;
            if (waiter.IncludeCompactState)
            {
                if (waiter.IncludeUi)
                {
                    sharedUi ??= GameInspector.InspectAll(compact: true, includeUi: true);
                    waiter.CompactState = sharedUi;
                }
                else
                {
                    sharedNoUi ??= GameInspector.InspectAll(compact: true, includeUi: false);
                    waiter.CompactState = sharedNoUi;
                }
            }
            waiter.Tcs.TrySetResult(true);
        }
    }

    private void OnDestroy()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch { }
    }

    // ── HTTP 控制面 ─────────────────────────────────────────────────────────

    private static void StartServer()
    {
        if (_started) return;
        _started = true;

        int port = DefaultPort;
        try
        {
            if (BazaarLauncherPlugin.GameDriverPort?.Value > 1024 && BazaarLauncherPlugin.GameDriverPort.Value <= 65535)
            {
                port = BazaarLauncherPlugin.GameDriverPort.Value;
            }
            string env = Environment.GetEnvironmentVariable("BAZAAR_DRIVER_PORT");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out int p)) port = p;
        }
        catch { }

        InitSecurityToken();

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            BazaarLauncherPlugin.Log.LogInfo(
                $"GameDriver 本地诊断服务已监听 http://127.0.0.1:{port}/ (Token鉴权={(string.IsNullOrEmpty(_activeToken) ? "可选关闭(防CSRF开启)" : "已启用")})");
        }
        catch (Exception ex)
        {
            _started = false;
            BazaarLauncherPlugin.Log.LogError($"GameDriver 监听端口 {port} 失败: {ex.Message}");
            return;
        }

        Task.Run(async () =>
        {
            while (_started && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => HandleRequestAsync(ctx));
            }
        });
    }

    private static void InitSecurityToken()
    {
        _activeToken = string.Empty;
        try
        {
            string configured = BazaarLauncherPlugin.GameDriverToken?.Value?.Trim();
            if (!string.IsNullOrEmpty(configured))
            {
                _activeToken = configured;
                return;
            }

            if (BazaarLauncherPlugin.RequireGameDriverToken?.Value == true)
            {
                _activeToken = Guid.NewGuid().ToString("N");
                string tokenPath = Path.Combine(Paths.ConfigPath ?? AppDomain.CurrentDomain.BaseDirectory, "bazaar_driver.token");
                File.WriteAllText(tokenPath, _activeToken, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    private static bool ValidateRequestSecurity(HttpListenerContext ctx, out string rejectReason)
    {
        rejectReason = string.Empty;

        // 1. 仅允许环回客户端
        var remote = ctx.Request.RemoteEndPoint?.Address;
        if (remote != null && !IPAddress.IsLoopback(remote))
        {
            rejectReason = "forbidden non-loopback client";
            return false;
        }

        // 2. Host 校验，防止 DNS Rebinding
        string host = ctx.Request.UserHostName ?? string.Empty;
        int colon = host.IndexOf(':');
        string hostOnly = colon > 0 ? host.Substring(0, colon) : host;
        if (!string.IsNullOrEmpty(hostOnly)
            && !string.Equals(hostOnly, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(hostOnly, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(hostOnly, "[::1]", StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "invalid host header";
            return false;
        }

        // 3. 拦截来自浏览器网页的跨站 CSRF 请求（带 Origin 头一律拒绝，unless 携带合法 Token）
        string origin = ctx.Request.Headers["Origin"];
        bool hasValidToken = IsTokenMatched(ctx);
        if (!string.IsNullOrEmpty(origin) && !hasValidToken)
        {
            rejectReason = "cross-origin browser request blocked";
            return false;
        }

        // 4. 若配置了 Token 或开启强制 Token，则必须校验 X-Bazaar-Token 或 Authorization: Bearer
        if (!string.IsNullOrEmpty(_activeToken) && !hasValidToken)
        {
            rejectReason = "missing or invalid X-Bazaar-Token";
            return false;
        }

        return true;
    }

    private static bool IsTokenMatched(HttpListenerContext ctx)
    {
        if (string.IsNullOrEmpty(_activeToken)) return false;
        string hdr = ctx.Request.Headers["X-Bazaar-Token"];
        if (string.Equals(hdr, _activeToken, StringComparison.Ordinal)) return true;
        string auth = ctx.Request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(auth.Substring(7).Trim(), _activeToken, StringComparison.Ordinal);
        }
        return false;
    }

    private static async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!ValidateRequestSecurity(ctx, out string reject))
            {
                Respond(ctx, 403, Err(reject));
                return;
            }

            string path = (ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "").ToLowerInvariant();
            string method = ctx.Request.HttpMethod.ToUpperInvariant();

            if (method == "GET" && path == "/health")
            {
                var health = RunOnMain(() =>
                {
                    var st = ReadState() as Dictionary<string, object>;
                    return new
                    {
                        ok = true,
                        driverVersion = "2.2.0",
                        inRun = st != null && st.TryGetValue("inRun", out var ir) && ir is bool b && b,
                        appState = st != null && st.TryGetValue("appState", out var a) ? a : null,
                        runState = st != null && st.TryGetValue("runState", out var r) ? r : null,
                        perf = st != null && st.TryGetValue("perf", out var pf) ? pf : null,
                    };
                });
                Respond(ctx, 200, JsonConvert.SerializeObject(health, Formatting.Indented));
                return;
            }

            if (method == "GET" && (path == "/state" || path == ""))
            {
                bool compact = ParseQueryBool(ctx, "compact", false);
                var state = RunOnMain(ReadState);
                Respond(ctx, 200, JsonConvert.SerializeObject(state, compact ? Formatting.None : Formatting.Indented));
                return;
            }

            if ((method == "GET" || method == "POST") && path == "/inspect")
            {
                bool compact = ParseQueryBool(ctx, "compact", false);
                bool includeUi = ParseQueryBool(ctx, "ui", true);
                if (method == "POST")
                {
                    var req = ReadJsonBody(ctx, out string parseErr);
                    if (parseErr != null) { Respond(ctx, 400, Err(parseErr)); return; }
                    if (req["compact"] != null) compact = (bool)req["compact"];
                    if (req["ui"] != null) includeUi = (bool)req["ui"];
                }
                var inspected = RunOnMain(() => (object)GameInspector.InspectAll(compact, includeUi));
                Respond(ctx, 200, JsonConvert.SerializeObject(inspected, compact ? Formatting.None : Formatting.Indented));
                return;
            }

            if (method == "GET" && path == "/combataudit")
            {
                int since = 0;
                string sSeq = ctx.Request.QueryString["since"];
                if (!string.IsNullOrEmpty(sSeq) && int.TryParse(sSeq, out int sv)) since = sv;
                var audits = CombatPatch.GetCombatAuditsSince(since);
                Respond(ctx, 200, JsonConvert.SerializeObject(new { ok = true, audits }, Formatting.None));
                return;
            }

            if (method == "POST" && path == "/encounterstep")
            {
                var req = ReadJsonBody(ctx, out string parseErr);
                if (parseErr != null) { Respond(ctx, 400, Err(parseErr)); return; }
                await HandleActionWithWaitAsync(ctx, req, () => DoEncounterStep(req), "encounterstep").ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/move")
            {
                var req = ReadJsonBody(ctx, out string parseErr);
                if (parseErr != null) { Respond(ctx, 400, Err(parseErr)); return; }
                await HandleActionWithWaitAsync(ctx, req, () => DoMove(req), "move").ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/resume")
            {
                var req = ReadJsonBody(ctx, out string parseErr);
                if (parseErr != null) { Respond(ctx, 400, Err(parseErr)); return; }
                var res = await DoResumeAsync(req).ConfigureAwait(false);
                Respond(ctx, 200, JsonConvert.SerializeObject(res, Formatting.Indented));
                return;
            }

            if (method == "POST" && path == "/concede")
            {
                var req = ReadJsonBody(ctx, out string parseErr);
                if (parseErr != null) { Respond(ctx, 400, Err(parseErr)); return; }
                var res = await DoConcedeAsync(req).ConfigureAwait(false);
                Respond(ctx, 200, JsonConvert.SerializeObject(res, Formatting.Indented));
                return;
            }

            if (method == "POST" && path == "/drive")
            {
                var req = ReadJsonBody(ctx, out string parseErr);
                if (parseErr != null) { Respond(ctx, 400, Err(parseErr)); return; }

                string op = ((string)req["op"] ?? "").Trim();
                string opLower = op.ToLowerInvariant();

                if (opLower == "resume")
                {
                    var res = await DoResumeAsync(req).ConfigureAwait(false);
                    Respond(ctx, 200, JsonConvert.SerializeObject(res, Formatting.Indented));
                    return;
                }
                if (opLower == "concede")
                {
                    var res = await DoConcedeAsync(req).ConfigureAwait(false);
                    Respond(ctx, 200, JsonConvert.SerializeObject(res, Formatting.Indented));
                    return;
                }
                if (opLower == "batch")
                {
                    var res = await DriverOps.RunBatchAsync(req, BazaarLauncher.Core.HttpClientHub.ShutdownToken).ConfigureAwait(false);
                    Respond(ctx, 200, JsonConvert.SerializeObject(res, Formatting.Indented));
                    return;
                }
                if (opLower == "wait")
                {
                    var spec = req["condition"] as JObject ?? req["wait"] as JObject ?? new JObject();
                    int timeout = Math.Clamp((int?)req["timeoutMs"] ?? 10000, 100, 60000);
                    var r = await DriverOps.WaitForAsync(spec, timeout, BazaarLauncher.Core.HttpClientHub.ShutdownToken).ConfigureAwait(false);
                    Respond(ctx, 200, JsonConvert.SerializeObject(new { ok = r.ok, elapsedMs = r.elapsedMs, error = r.error }, Formatting.Indented));
                    return;
                }
                if (opLower == "sync")
                {
                    var res = await DoSyncAsync(req, BazaarLauncher.Core.HttpClientHub.ShutdownToken).ConfigureAwait(false);
                    Respond(ctx, 200, JsonConvert.SerializeObject(res, Formatting.Indented));
                    return;
                }
                if (opLower is "forcestate" or "force_state")
                {
                    var res = await DoForceStateAsync(req, BazaarLauncher.Core.HttpClientHub.ShutdownToken).ConfigureAwait(false);
                    Respond(ctx, 200, JsonConvert.SerializeObject(res, Formatting.Indented));
                    return;
                }
                if (opLower is "grantchests" or "openchests" or "equipcosmetic")
                {
                    var qaReq = new JObject(req) { ["op"] = opLower };
                    var res = await HandleCosmeticsQaAsync(qaReq).ConfigureAwait(false);
                    Respond(ctx, 200, JsonConvert.SerializeObject(res, Formatting.Indented));
                    return;
                }

                await HandleActionWithWaitAsync(ctx, req, () => Execute(op, req), opLower).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/screenshot")
            {
                double scale = 1.0;
                string s = ctx.Request.QueryString["scale"];
                if (!string.IsNullOrEmpty(s) && double.TryParse(s, out double sc)) scale = Math.Clamp(sc, 0.1, 1.0);

                var bytes = RunOnMain(() => CaptureScreenshot(scale));
                if (bytes == null) { Respond(ctx, 500, Err("screenshot failed")); return; }
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "image/png";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
                return;
            }

            if (method == "GET" && path == "/events")
            {
                int since = 0;
                string s2 = ctx.Request.QueryString["since"];
                if (!string.IsNullOrEmpty(s2) && int.TryParse(s2, out int sv)) since = sv;
                var evs = DriverOps.Snapshot(since);
                Respond(ctx, 200, JsonConvert.SerializeObject(new { ok = true, lastSeq = DriverOps.LastSeq, events = evs }, Formatting.Indented));
                return;
            }

            if (method == "GET" && path == "/log")
            {
                var lines = _logRing.ToArray();
                Respond(ctx, 200, string.Join("\n", lines));
                return;
            }

            if (method == "GET" && path == "/custom-ui/state")
            {
                var uiState = RunOnMain(() =>
                {
                    CustomGameplayUI.EnsureInstalled();
                    return CustomGameplayUI.Instance.GetDriverStateSnapshot();
                });
                Respond(ctx, 200, JsonConvert.SerializeObject(uiState, Formatting.Indented));
                return;
            }

            if (method == "POST" && path == "/custom-ui/open")
            {
                var req = ReadJsonBody(ctx, out string parseErr);
                if (parseErr != null) { Respond(ctx, 400, Err(parseErr)); return; }
                bool open = (bool?)req["open"] ?? true;
                string tab = (string)req["tab"];

                var uiState = RunOnMain(() =>
                {
                    CustomGameplayUI.EnsureInstalled();
                    CustomGameplayUI.Instance.SetOpenFromDriver(open, tab);
                    return CustomGameplayUI.Instance.GetDriverStateSnapshot();
                });
                Respond(ctx, 200, JsonConvert.SerializeObject(uiState, Formatting.Indented));
                return;
            }

            if (method == "POST" && path == "/custom-ui/apply")
            {
                var req = ReadJsonBody(ctx, out string parseErr);
                if (parseErr != null) { Respond(ctx, 400, Err(parseErr)); return; }

                string target = (string)req["target"] ?? "normal";
                JObject rulesObj = req["rules"] as JObject ?? req;
                CustomRunRuleSet parsed;
                try
                {
                    parsed = rulesObj.ToObject<CustomRunRuleSet>()
                             ?? new CustomRunRuleSet();
                    parsed.EnsureCollections();
                    parsed.RefreshPowerFlag();
                }
                catch (Exception ex)
                {
                    Respond(ctx, 400, Err("invalid CustomRunRuleSet: " + ex.Message));
                    return;
                }

                RunOnMain(() =>
                {
                    CustomGameplayUI.EnsureInstalled();
                    return true;
                });

                var applyRes = await CustomGameplayUI.Instance
                    .ApplyRulesFromDriverAsync(parsed, target)
                    .ConfigureAwait(false);

                var payload = new
                {
                    ok = applyRes.ok,
                    target,
                    statusCode = applyRes.statusCode,
                    error = string.IsNullOrEmpty(applyRes.error) ? null : applyRes.error,
                    rules = applyRes.appliedRules ?? parsed,
                };
                Respond(ctx, applyRes.ok ? 200 : (applyRes.statusCode > 0 ? applyRes.statusCode : 502),
                    JsonConvert.SerializeObject(payload, Formatting.Indented));
                return;
            }

            if (method == "GET" && path == "/cosmetics-qa")
            {
                var snap = RunOnMain(GetCosmeticsQaSnapshot);
                Respond(ctx, 200, JsonConvert.SerializeObject(snap, Formatting.Indented));
                return;
            }

            if (method == "POST" && path == "/cosmetics-qa")
            {
                var req = ReadJsonBody(ctx, out string parseErr);
                if (parseErr != null) { Respond(ctx, 400, Err(parseErr)); return; }
                var res = await HandleCosmeticsQaAsync(req).ConfigureAwait(false);
                Respond(ctx, 200, JsonConvert.SerializeObject(res, Formatting.Indented));
                return;
            }

            Respond(ctx, 404, Err("unknown endpoint: " + method + " " + path));
        }
        catch (Exception ex)
        {
            try { Respond(ctx, 500, Err(ex.Message)); } catch { }
        }
    }

    private static bool ParseQueryBool(HttpListenerContext ctx, string key, bool defaultVal)
    {
        string raw = ctx.Request.QueryString[key];
        if (string.IsNullOrEmpty(raw)) return defaultVal;
        return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMutatingWaitOp(string opLower)
    {
        return opLower is "buy" or "sell" or "selectskill" or "selectencounter" or "encounterstep" or "encounter_step"
            or "commitpedestal" or "reroll" or "exit" or "move" or "cheat" or "sync"
            or "startcombat" or "start_combat";
    }

    /// <summary>
    /// P2-12：高风险切态/写操作命令集合。当客户端处于忙碌态且 waitIdle 超时时，
    /// 严禁盲目放行发包（防止重连慢铺牌期奇遇卡幽灵残留或动画竞态），必须拒绝执行并返回 retryable_busy。
    /// </summary>
    private static bool IsHighRiskStateTransitionOp(string opLower)
    {
        return opLower is "selectencounter" or "encounterstep" or "encounter_step"
            or "exit" or "commitpedestal" or "reroll"
            or "startcombat" or "start_combat"
            or "buy" or "sell" or "selectskill" or "move";
    }

    private static bool IsWithinPostResumeGraceWindow()
    {
        long resumeTicks = Interlocked.Read(ref _lastResumeAtTicks);
        if (resumeTicks <= 0) return false;
        double elapsedSec = (Stopwatch.GetTimestamp() - resumeTicks) / (double)Stopwatch.Frequency;
        return elapsedSec >= 0.0 && elapsedSec <= 15.0;
    }

    /// <summary>
    /// P2-12：在直接主线程命令入口（如 batch/DriverOps 或前置等待后的 1 帧竞态窗口）兜底检查客户端是否仍处于忙碌态；
    /// 除非调用方显式传 allowBusy=true / force=true / waitIdle=false，否则拒绝发包并返回 retryable_busy。
    /// </summary>
    private static object TryRejectIfClientBusy(string opName, JObject req)
    {
        if (req != null && ((bool?)req["allowBusy"] == true || (bool?)req["force"] == true || (bool?)req["waitIdle"] == false))
        {
            return null;
        }

        if (GameInspector.IsClientBusy(out string busyReason))
        {
            return new
            {
                ok = false,
                op = opName,
                error = "retryable_busy",
                errorCode = "retryable_busy",
                retryable = true,
                dispatched = false,
                waitIdle = true,
                idleTimedOut = true,
                idleWaitMs = 0,
                busyReason = busyReason ?? "busy",
                stage = "dispatch_guard",
            };
        }
        return null;
    }

    private static async Task HandleActionWithWaitAsync(
        HttpListenerContext ctx, JObject req, Func<object> mainAction, string opLower)
    {
        bool waitIdle = (bool?)req["waitIdle"] ?? IsMutatingWaitOp(opLower);
        bool isHighRisk = IsHighRiskStateTransitionOp(opLower);
        bool allowBusy = (bool?)req["allowBusy"] ?? (bool?)req["force"] ?? false;
        bool includeCompactState = (bool?)req["includeCompactState"] ?? false;
        bool includeUi = (bool?)req["includeUi"] ?? true;
        int waitTimeoutMs = Math.Clamp((int?)req["waitTimeoutMs"] ?? 3000, 50, 15000);
        bool compactFmt = includeCompactState || ((bool?)req["compact"] ?? false);

        // P2-12：高风险切态命令派发前必须先确认客户端已空闲；若忙碌则等待至多 waitTimeoutMs（重连宽限期内至少 10s）。
        // 超时（idleTimedOut）时严禁盲目放行发包，直接拒绝执行并显式返回 retryable_busy（附 busyReason 与耗时）交上层退避重试。
        int preIdleWaitMs = 0;
        if (waitIdle && isHighRisk && !allowBusy)
        {
            string preBusyReason = RunOnMain(() => GameInspector.IsClientBusy(out string r) ? (r ?? "busy") : null);
            if (preBusyReason != null)
            {
                int preWaitTimeoutMs = IsWithinPostResumeGraceWindow()
                    ? Math.Max(waitTimeoutMs, 10000)
                    : waitTimeoutMs;
                var (preTimedOut, preElapsedMs, latestBusyReason, preCompactState) =
                    await WaitForClientIdleAsync(preWaitTimeoutMs, includeCompactState, includeUi).ConfigureAwait(false);
                preIdleWaitMs = preElapsedMs;

                if (preTimedOut)
                {
                    var busyObj = new JObject
                    {
                        ["ok"] = false,
                        ["op"] = opLower,
                        ["error"] = "retryable_busy",
                        ["errorCode"] = "retryable_busy",
                        ["retryable"] = true,
                        ["dispatched"] = false,
                        ["waitIdle"] = true,
                        ["idleTimedOut"] = true,
                        ["idleWaitMs"] = preElapsedMs,
                        ["busyReason"] = latestBusyReason ?? preBusyReason ?? "busy",
                        ["stage"] = "pre_wait",
                    };
                    if (includeCompactState && preCompactState != null)
                    {
                        busyObj["compactState"] = JObject.FromObject(preCompactState);
                    }
                    Respond(ctx, 200, busyObj.ToString(compactFmt ? Formatting.None : Formatting.Indented));
                    return;
                }
            }
        }

        if (opLower == "buy")
        {
            string targetId = (string)req["instanceId"];
            var buyWaitSw = Stopwatch.StartNew();
            while (buyWaitSw.ElapsedMilliseconds < 400)
            {
                bool ready = RunOnMain(() => AreSelectionBuyTargetsReady(string.IsNullOrEmpty(targetId) ? FirstSelectionId() : targetId));
                if (ready) break;
                await Task.Delay(15).ConfigureAwait(false);
            }
        }

        var res = RunOnMain(mainAction);
        var resObj = res as JObject ?? JObject.FromObject(res ?? new { ok = false });
        if (preIdleWaitMs > 0) resObj["preIdleWaitMs"] = preIdleWaitMs;
        bool actionOk = resObj["ok"]?.Value<bool>() == true;

        if (waitIdle && actionOk)
        {
            var (timedOut, idleWaitMs, postBusyReason, compactState) =
                await WaitForClientIdleAsync(waitTimeoutMs, includeCompactState, includeUi).ConfigureAwait(false);
            resObj["waitIdle"] = true;
            resObj["idleWaitMs"] = idleWaitMs;
            if (timedOut)
            {
                resObj["idleTimedOut"] = true;
                resObj["busyReason"] = postBusyReason ?? "busy";
                resObj["retryableBusy"] = true;
            }
            if (includeCompactState && compactState != null)
            {
                resObj["compactState"] = JObject.FromObject(compactState);
            }
        }
        else if (includeCompactState)
        {
            var compactState = RunOnMain(() => GameInspector.InspectAll(compact: true, includeUi: includeUi));
            if (compactState != null) resObj["compactState"] = JObject.FromObject(compactState);
        }

        Respond(ctx, 200, resObj.ToString(compactFmt ? Formatting.None : Formatting.Indented));
    }

    private static async Task<(bool timedOut, int elapsedMs, string busyReason, Dictionary<string, object> compactState)> WaitForClientIdleAsync(
        int timeoutMs, bool includeCompactState, bool includeUi)
    {
        var sw = Stopwatch.StartNew();
        var waiter = new IdleWaiter(includeCompactState, includeUi);
        _idleWaiters.Enqueue(waiter);

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var completed = await Task.WhenAny(waiter.Tcs.Task, Task.Delay(8)).ConfigureAwait(false);
            if (waiter.Tcs.Task.IsCompleted)
            {
                return (false, (int)sw.ElapsedMilliseconds, null, waiter.CompactState);
            }
        }

        waiter.Cancelled = true;
        string busyReason = null;
        Dictionary<string, object> fallbackState = null;
        try
        {
            RunOnMain(() =>
            {
                GameInspector.IsClientBusy(out busyReason);
                if (includeCompactState)
                {
                    fallbackState = GameInspector.InspectAll(compact: true, includeUi: includeUi);
                }
                return true;
            });
        }
        catch { }
        return (true, (int)sw.ElapsedMilliseconds, busyReason, fallbackState);
    }

    private static JObject ReadJsonBody(HttpListenerContext ctx, out string parseError)
    {
        parseError = null;
        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = sr.ReadToEnd();
        try
        {
            return JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (Exception ex)
        {
            parseError = "bad json: " + ex.Message;
            return null;
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static string Err(string msg) => JsonConvert.SerializeObject(new { ok = false, error = msg });

    /// <summary>
    /// 把委托丢到 Unity 主线程执行并等结果，同时统计最近主线程耗时。
    /// 即使默认关闭了 GameDriver HTTP 控制面（Instance == null），也会自动经由常驻
    /// <see cref="BazaarLauncher.Core.MainThreadDispatcher"/> 回到主线程执行，绝不静默跳过。
    /// </summary>
    internal static T RunOnMain<T>(Func<T> fn, int timeoutMs = 8000)
    {
        if (fn == null) return default;

        if (BazaarLauncher.Core.MainThreadDispatcher.IsOnMainThread)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                return fn();
            }
            finally
            {
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                GameInspector.RecordMainThreadOpMs(ms);
            }
        }

        bool useDriverQueue = Instance != null;
        if (!useDriverQueue && !BazaarLauncher.Core.MainThreadDispatcher.IsInstalled)
        {
            return default;
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action work = () =>
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                var r = fn();
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                GameInspector.RecordMainThreadOpMs(ms);
                tcs.TrySetResult(r);
            }
            catch (Exception ex)
            {
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                GameInspector.RecordMainThreadOpMs(ms);
                tcs.TrySetException(ex);
            }
        };

        if (useDriverQueue)
        {
            _mainThreadQueue.Enqueue(work);
        }
        else
        {
            BazaarLauncher.Core.MainThreadDispatcher.Enqueue(work);
        }

        if (!tcs.Task.Wait(timeoutMs))
            throw new TimeoutException("main thread did not respond in " + timeoutMs + "ms");
        return tcs.Task.Result;
    }

    // ── 读状态 ──────────────────────────────────────────────────────────────

    private static object ReadState()
    {
        string appStateName = GetAppStateName();
        string activeScene = GameInspector.GetActiveSceneName();
        bool inMainMenu = GameInspector.IsMainMenuSceneName(activeScene);
        var hudVis = CustomGameplayUI.Instance?.GetHudVisibilityForDriver() ?? default;
        var result = new Dictionary<string, object>
        {
            ["inRun"] = false,
            ["appState"] = appStateName,
            ["activeScene"] = activeScene,
            ["hasActiveRunFlag"] = GameInspector.ReadHasActiveRunFlag(),
            ["isReplayState"] = appStateName == "ReplayState",
            ["combatSpeed"] = CombatPatch.SpeedOverride,
            ["autoExitReplay"] = CombatPatch.AutoExitReplay,
            ["isSceneTransitioning"] = GameInspector.IsSceneTransitioning(),
            ["menuReady"] = inMainMenu && (appStateName == "null" || appStateName == "EmptyState") && GameInspector.IsMenuReady(),
            ["currentEncounterId"] = GameInspector.GetDataCurrentEncounterId()?.ToString(),
            ["currentEncounterPortraitVisible"] = GameInspector.IsCurrentEncounterPortraitVisible(),
            ["pvpOpponentHero"] = GameInspector.GetPvpOpponentHero(),
            ["customUiActiveRuleCount"] = CustomGameplayUI.Instance?.GetActiveRuleCountForDriver() ?? 0,
            ["menuRulePillVisible"] = hudVis.menuRulePillVisible,
            ["inRunBadgeVisible"] = hudVis.inRunBadgeVisible,
            ["combatSpeedChipVisible"] = hudVis.combatSpeedChipVisible,
            ["fpsEstimate"] = GameInspector.GetFpsEstimate(),
            ["playerCardsDetail"] = new List<object>(),
            ["shopCardsDetail"] = new List<object>(),
            ["cloudGuard"] = CloudGuard.GetStats(),
            ["compatGuards"] = CompatAuditPatch.PatchedCount,
            ["driverVersion"] = "2.2.0",
            ["perf"] = GameInspector.GetPerfSnapshot(),
        };

        try
        {
            bool canBeInRun = GameInspector.IsActiveInRunState(activeScene, appStateName);
            var run = canBeInRun ? GameInspector.GetDataRun() : null;
            var state = canBeInRun ? GameInspector.GetDataCurrentState() : null;

            if (!canBeInRun || run == null || state == null)
            {
                result["note"] = !canBeInRun ? "not in a run (menu/idle)" : "not in a run";
                return result;
            }

            result["inRun"] = true;

            // RunState
            result["runState"] = GetProp(state, "StateName")?.ToString();
            var selectionList = (GetProp(state, "SelectionSet") as IEnumerable)?.Cast<object>()
                .Select(o => o?.ToString()).Where(x => x != null).ToList() ?? new List<string>();
            result["selectionSet"] = selectionList;
            result["currentEncounterId"] = GetProp(state, "CurrentEncounterId")?.ToString() ?? GameInspector.GetDataCurrentEncounterId()?.ToString();
            result["currentEncounterPortraitVisible"] = GameInspector.IsCurrentEncounterPortraitVisible();
            result["pvpOpponentHero"] = GameInspector.GetPvpOpponentHero();
            result["rerollCost"] = GetProp(state, "RerollCost");
            result["rerollsRemaining"] = GetProp(state, "RerollsRemaining");

            var selRules = GetProp(state, "SelectionContextRules");
            if (selRules != null)
            {
                result["selectionIsFree"] = GetProp(selRules, "SelectionIsFree");
                result["canExit"] = GetProp(selRules, "CanExit");
                result["canSelectMultiple"] = GetProp(selRules, "CanSelectMultiple");
            }

            // Run（BazaarGameClient.Domain.Models.Run）
            var runObj = run;
            result["day"] = GetProp(runObj, "Day");
            result["hour"] = GetProp(runObj, "Hour");
            result["victories"] = GetProp(runObj, "Victories");
            result["losses"] = GetProp(runObj, "Losses");
            result["gameModeId"] = GetProp(runObj, "GameModeId")?.ToString();
            result["playMode"] = GetProp(runObj, "PlayMode")?.ToString();

            // Player 属性与解锁槽位
            var player = GetProp(runObj, "Player");
            if (player != null)
            {
                var attrs = GetProp(player, "Attributes") as IDictionary;
                if (attrs != null)
                {
                    result["gold"] = AttrInt(attrs, "Gold");
                    result["income"] = AttrInt(attrs, "Income");
                    result["prestige"] = AttrInt(attrs, "Prestige");
                    result["health"] = AttrInt(attrs, "Health");
                    result["healthMax"] = AttrInt(attrs, "HealthMax");
                    result["shield"] = AttrInt(attrs, "Shield");
                    result["level"] = AttrInt(attrs, "Level");
                    result["experience"] = AttrInt(attrs, "Experience");
                }
                result["hero"] = GetProp(player, "Hero")?.ToString();
                result["playerCombatantId"] = GetProp(player, "CombatantId")?.ToString();

                var hand = GetProp(player, "Hand");
                var container = GetProp(hand, "Container");
                var unlocked = GetProp(container, "UnlockedSockets");
                if (unlocked != null) result["unlockedSlots"] = unlocked;
            }

            // 卡牌与 Stage C 只读诊断详情
            var entities = GameInspector.GetDataEntities();
            if (entities != null)
            {
                var cards = new List<object>();
                var playerCardsDetail = new List<Dictionary<string, object>>();
                var byInstanceId = new Dictionary<string, object>(StringComparer.Ordinal);
                int oppCount = 0;
                string firstSelectionType = null;
                string firstSelId = selectionList.Count > 0 ? selectionList[0] : null;
                foreach (DictionaryEntry kv in entities)
                {
                    var card = kv.Value;
                    if (card == null) continue;
                    var desc = DescribeCard(card) as Dictionary<string, object>;
                    if (desc != null)
                    {
                        string iid = desc["instanceId"]?.ToString();
                        if (!string.IsNullOrEmpty(iid))
                        {
                            byInstanceId[iid] = card;
                        }
                        string ownerStr = desc["owner"]?.ToString();
                        string typeStr = desc["type"]?.ToString() ?? "";
                        string sectionStr = desc["section"]?.ToString() ?? "";

                        if (string.Equals(ownerStr, "Opponent", StringComparison.OrdinalIgnoreCase))
                            oppCount++;
                        if (firstSelId != null && string.Equals(iid, firstSelId, StringComparison.Ordinal))
                            firstSelectionType = typeStr;

                        if (string.Equals(ownerStr, "Player", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(typeStr, "SocketEffect", StringComparison.OrdinalIgnoreCase) &&
                            (string.Equals(typeStr, "Item", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(sectionStr, "Hand", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(sectionStr, "Stash", StringComparison.OrdinalIgnoreCase)))
                        {
                            playerCardsDetail.Add(BuildPlayerCardDetail(card, desc));
                        }
                    }
                    cards.Add(desc);
                }

                if (playerCardsDetail.Count > 1)
                {
                    playerCardsDetail.Sort((a, b) =>
                    {
                        int secCmp = SectionSortOrder(a["section"]?.ToString()).CompareTo(SectionSortOrder(b["section"]?.ToString()));
                        if (secCmp != 0) return secCmp;
                        return ParseSocketOrder(a["socket"]?.ToString()).CompareTo(ParseSocketOrder(b["socket"]?.ToString()));
                    });
                }

                var shopCardsDetail = new List<object>(selectionList.Count);
                foreach (var sid in selectionList)
                {
                    if (!string.IsNullOrEmpty(sid) && byInstanceId.TryGetValue(sid, out var selCard))
                    {
                        shopCardsDetail.Add(BuildShopCardDetail(selCard));
                    }
                }

                result["cards"] = cards;
                result["cardCount"] = cards.Count;
                result["opponentCardCount"] = oppCount;
                result["playerCardsDetail"] = playerCardsDetail;
                result["shopCardsDetail"] = shopCardsDetail;
                if (firstSelectionType != null) result["selectionFirstType"] = firstSelectionType;
            }
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
        }

        return result;
    }

    private static object DescribeCard(object card)
    {
        var d = new Dictionary<string, object>
        {
            ["instanceId"] = GetProp(card, "InstanceId")?.ToString(),
            ["templateId"] = GetProp(card, "TemplateId")?.ToString(),
            ["type"] = GetProp(card, "Type")?.ToString(),
            ["tier"] = GetProp(card, "Tier")?.ToString(),
            ["size"] = GetProp(card, "Size")?.ToString(),
        };

        var owner = GetProp(card, "Owner");
        d["owner"] = owner == null ? null : GetProp(owner, "CombatantId")?.ToString();
        d["socket"] = GetProp(card, "LeftSocketId")?.ToString();
        d["section"] = GetProp(card, "Section")?.ToString();

        var ench = GetProp(card, "Enchantment") ?? card.GetType().GetMethod("GetEnchantment")?.Invoke(card, null);
        if (ench != null) d["enchantment"] = ench.ToString();

        var attrs = GetProp(card, "Attributes") as IDictionary;
        if (attrs != null)
        {
            var map = new Dictionary<string, int>();
            foreach (DictionaryEntry kv in attrs)
            {
                if (kv.Key == null) continue;
                if (kv.Value is int i) map[kv.Key.ToString()] = i;
            }
            d["attributes"] = map;
        }
        return d;
    }

    private static Dictionary<string, object> BuildPlayerCardDetail(object card, Dictionary<string, object> baseDesc)
    {
        var attrs = GetProp(card, "Attributes") as IDictionary;
        int burnVal = attrs != null ? AttrInt(attrs, "BurnApplyAmount") : 0;
        if (burnVal == 0 && attrs != null)
        {
            burnVal = AttrInt(attrs, "Burn");
        }

        return new Dictionary<string, object>
        {
            ["instanceId"] = baseDesc.GetValueOrDefault("instanceId"),
            ["templateId"] = baseDesc.GetValueOrDefault("templateId"),
            ["name"] = GameInspector.ResolveCardTitlePublic(card),
            ["tier"] = baseDesc.GetValueOrDefault("tier"),
            ["section"] = baseDesc.GetValueOrDefault("section"),
            ["socket"] = baseDesc.GetValueOrDefault("socket"),
            ["size"] = baseDesc.GetValueOrDefault("size"),
            ["freeze"] = attrs != null ? AttrInt(attrs, "Freeze") : 0,
            ["flying"] = attrs != null ? AttrInt(attrs, "Flying") : 0,
            ["burn"] = burnVal,
            ["critChance"] = attrs != null ? AttrInt(attrs, "CritChance") : 0,
            ["ammo"] = attrs != null ? AttrInt(attrs, "Ammo") : 0,
            ["ammoMax"] = attrs != null ? AttrInt(attrs, "AmmoMax") : 0,
        };
    }

    private static Dictionary<string, object> BuildShopCardDetail(object card)
    {
        var attrs = GetProp(card, "Attributes") as IDictionary;
        return new Dictionary<string, object>
        {
            ["instanceId"] = GetProp(card, "InstanceId")?.ToString(),
            ["templateId"] = GetProp(card, "TemplateId")?.ToString(),
            ["name"] = GameInspector.ResolveCardTitlePublic(card),
            ["type"] = GetProp(card, "Type")?.ToString(),
            ["tier"] = GetProp(card, "Tier")?.ToString(),
            ["size"] = GetProp(card, "Size")?.ToString(),
            ["buyPrice"] = attrs != null ? AttrInt(attrs, "BuyPrice") : 0,
            ["socket"] = GetProp(card, "LeftSocketId")?.ToString(),
        };
    }

    private static int SectionSortOrder(string section)
    {
        if (string.Equals(section, "Hand", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(section, "Stash", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    private static int ParseSocketOrder(string socket)
    {
        if (!string.IsNullOrEmpty(socket) && socket.StartsWith("Socket_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(socket.Substring("Socket_".Length), out int idx))
        {
            return idx;
        }
        return 999;
    }

    private static int AttrInt(IDictionary attrs, string name)
    {
        foreach (DictionaryEntry kv in attrs)
        {
            if (kv.Key?.ToString() == name)
            {
                return kv.Value is int i ? i : 0;
            }
        }
        return 0;
    }

    private static object GetProp(object obj, string name) => GameInspector.GetMember(obj, name);

    /// <summary>读 AppState.CurrentState 的类型名。</summary>
    private static string GetAppStateName() => GameInspector.GetAppStateName();

    // ── 执行操作 ────────────────────────────────────────────────────────────

    private static object Execute(string op, JObject req)
    {
        try
        {
            switch (op.ToLowerInvariant())
            {
                case "buy": return DoBuy(req);
                case "sell": return DoSell(req);
                case "selectskill": return DoSelectSkill(req);
                case "selectencounter": return DoSelectEncounter(req);
                case "encounterstep":
                case "encounter_step": return DoEncounterStep(req);
                case "commitpedestal": return DoCommitPedestal(req);
                case "combatspeed": return DoCombatSpeed(req);
                case "autoexitreplay":
                    CombatPatch.AutoExitReplay = (bool?)req["enabled"] ?? (bool?)req["autoExitReplay"] ?? true;
                    return new { ok = true, op = "autoExitReplay", autoExitReplay = CombatPatch.AutoExitReplay };
                case "reroll": return DoSimple("SendReRollSelection", "reroll", req);
                case "exit": return DoExit(req);
                case "returntomenu":
                case "return_to_menu": return ReturnToMenuInternal("returnToMenu");
                case "abandon": return DoSimple("SendAbandonRun", "abandon", req);
                case "move": return DoMove(req);
                case "inspect":
                    return GameInspector.InspectAll((bool?)req["compact"] ?? false, (bool?)req["ui"] ?? true);
                case "cheat": return DoCheat(req);
                case "startrun": return DoStartRun(req);
                case "forcestate": return new { ok = false, error = "forceState must run via async route" };
                case "resync": return DoResync(req);
                case "sync": return new { ok = false, error = "sync must run via async route" };
                case "state": return new { ok = true, state = ReadState() };
                case "batch": return new { ok = false, error = "batch must run via async route" };
                case "wait": return new { ok = false, error = "wait must run via async route" };
                case "events": return new { ok = true, events = DriverOps.Snapshot((int?)req["since"] ?? 0) };
                case "cosmeticsstate": return GetCosmeticsQaSnapshot();
                case "switchhero": return SwitchHeroInMainMenu((string)req["hero"]);
                case "openloadout": return OpenLoadoutPopup();
                case "openheroselect": return OpenHeroSelectPopup();
                case "openchestscene": return OpenChestScene();
                case "returnhome": return ReturnToHeroSelectScene();
                case "hovercard": return DoHoverCard(req);
                default: return new { ok = false, error = "unknown op: " + op };
            }
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.GetType().Name + ": " + ex.Message };
        }
    }

    // ── 给 DriverOps 用的公开包装 ───────────────────────────────────────────
    internal static object DoBuyPublic(JObject req) => DoBuy(req);
    internal static object DoSellPublic(JObject req) => DoSell(req);
    internal static object DoSelectSkillPublic(JObject req) => DoSelectSkill(req);
    internal static object DoSelectEncounterPublic(JObject req) => DoSelectEncounter(req);
    internal static object DoEncounterStepPublic(JObject req) => DoEncounterStep(req);
    internal static object DoSimplePublic(string m, string o) => o == "exit" ? DoExit() : DoSimple(m, o);
    internal static object DoMovePublic(JObject req) => DoMove(req);
    internal static object DoCheatPublic(JObject req) => DoCheat(req);
    internal static object DoStartRunPublic(JObject req) => DoStartRun(req);
    internal static object DoInspectPublic() => GameInspector.InspectAll();
    internal static object ReadStatePublic() => ReadState();

    /// <summary>诊断展示/隐藏指定卡牌的原生 Tooltip 浮窗（支持 upgradePreview=true 预览下一阶数值箭头）。</summary>
    private static object DoHoverCard(JObject req)
    {
        bool hide = (bool?)req["hide"] ?? false;
        string targetId = (string)req["instanceId"];
        bool upgradePreview = (bool?)req["upgradePreview"] ?? false;

        var dataType = GameInspector.FindTypeSafe("TheBazaar.Data", "Data");
        var tipParent = dataType?.GetProperty("TooltipParentComponent", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (tipParent == null) return new { ok = false, error = "TooltipParentComponent unavailable" };

        var ccType = GameInspector.FindTypeSafe("CardController", "TheBazaar.CardController");

        if (hide || string.IsNullOrWhiteSpace(targetId))
        {
            ExitPreviousHoverUpgradePreview(ccType, except: null);
            ForceHideCardTooltip(tipParent, null);
            return new { ok = true, op = "hovercard", hidden = true };
        }

        if (ccType == null) return new { ok = false, error = "CardController type missing" };

        // P2-10：优先走官方 Data.CardAndSkillLookup.GetCardController（与 CardTooltipTypeHandler.SetupCardData 同源），
        // 确保对同一 CardController 实例执行 EnterUpgradePreview()，避免场景池化副本导致 CanFuse() 读错实例。
        Component matched = null;
        var cardObj = FindCard(targetId);
        if (cardObj != null)
        {
            var lookup = GameInspector.GetDataLookup();
            if (GetCardControllerSafe(lookup, cardObj) is Component lookedUpComp && ccType.IsInstanceOfType(lookedUpComp))
            {
                matched = lookedUpComp;
            }
        }
        if (matched == null)
        {
            var allCtrls = UnityEngine.Object.FindObjectsByType(ccType, FindObjectsSortMode.None);
            foreach (var o in allCtrls)
            {
                if (o is not Component c) continue;
                var cd = ccType.GetProperty("CardData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(c);
                string cid = cd?.GetType().GetProperty("InstanceId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(cd)?.ToString();
                if (string.Equals(cid, targetId, StringComparison.OrdinalIgnoreCase))
                {
                    matched = c;
                    break;
                }
            }
        }
        if (matched == null) return new { ok = false, error = "CardController not found: " + targetId };

        var tdField = ccType.GetField("_cardTooltipData", BindingFlags.NonPublic | BindingFlags.Instance);
        var td = tdField?.GetValue(matched);
        if (td == null) return new { ok = false, error = "_cardTooltipData null" };

        // P2-10：先退出旧卡升阶预览并强制隐藏旧同卡弹窗（清零 CurrentTooltipData），
        // 避免 TooltipParentComponent.ShowCardTooltipController 因 CurrentTooltipData == td 直接跳过重绘，
        // 同时避免 HideCardTooltipController 内部调用 ExitUpgradePreview 把新设的预览状态清掉。
        ExitPreviousHoverUpgradePreview(ccType, except: matched);
        ForceHideCardTooltip(tipParent, td);

        // P2-10：改调官方 CardController.EnterUpgradePreview() / ExitUpgradePreview()，
        // 使 CardTooltipTypeHandler.SetupCardData 调用 cardController.CanFuse() 时走官方 _isInUpgradePreview 链路而不被覆写清零。
        if (upgradePreview)
        {
            ccType.GetMethod("EnterUpgradePreview", BindingFlags.Public | BindingFlags.Instance)?.Invoke(matched, null);
            _lastHoverUpgradePreviewCtrl = matched;
        }
        else
        {
            ccType.GetMethod("ExitUpgradePreview", BindingFlags.Public | BindingFlags.Instance)?.Invoke(matched, null);
            if (ReferenceEquals(_lastHoverUpgradePreviewCtrl, matched))
                _lastHoverUpgradePreviewCtrl = null;
        }

        // 兜底：若目标卡未注册在 CardAndSkillLookup 中，SetupCardData 会保留 tooltipData.CanFuse，这里同步对齐
        var canFuseProp = td.GetType().GetProperty("CanFuse", BindingFlags.Public | BindingFlags.Instance);
        canFuseProp?.SetValue(td, upgradePreview);

        var offsetProp = ccType.GetProperty("TooltipOffset", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Vector3 offset = offsetProp?.GetValue(matched) is Vector3 v ? v : Vector3.zero;
        var showM = tipParent.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ShowCardTooltipController" && m.GetParameters().Length >= 3);
        if (showM == null) return new { ok = false, error = "ShowCardTooltipController not found" };
        var parms = showM.GetParameters();
        var args = new object[parms.Length];
        args[0] = matched.transform;
        args[1] = offset;
        args[2] = td;
        for (int i = 3; i < parms.Length; i++) args[i] = parms[i].HasDefaultValue ? parms[i].DefaultValue : null;
        showM.Invoke(tipParent, args);
        return new { ok = true, op = "hovercard", instanceId = targetId, upgradePreview };
    }

    private static void ExitPreviousHoverUpgradePreview(Type ccType, Component except)
    {
        var prev = _lastHoverUpgradePreviewCtrl;
        if (ReferenceEquals(prev, null) || ReferenceEquals(prev, except)) return;
        _lastHoverUpgradePreviewCtrl = null;
        try
        {
            if (prev != null && ccType != null && ccType.IsInstanceOfType(prev))
            {
                ccType.GetMethod("ExitUpgradePreview", BindingFlags.Public | BindingFlags.Instance)?.Invoke(prev, null);
            }
        }
        catch (Exception ex)
        {
            Core.DiagLog.SwallowOnce("GameDriver.ExitPrevHoverUpgradePreview", ex);
        }
    }

    private static void ForceHideCardTooltip(object tipParent, object expectedTooltipData)
    {
        if (tipParent == null) return;
        try
        {
            var tipType = tipParent.GetType();
            // 先解锁锁定态弹窗，防止 HideCardTooltipController 因 IsCardTooltipControllerLocked 提前返回
            tipType.GetMethod("UnlockAllLockedTooltipControllers", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(tipParent, null);
            tipType.GetMethod("HideCardTooltipController", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(tipParent, null);

            // 若卡间过渡（IsCardToCardTransitionActive）导致 HideCardTooltipController 早退，
            // 直接清理 CardTooltipController.CurrentTooltipData 确保后续 ShowCardTooltipController 必然重绘。
            var curTipCtrl = GetProp(tipParent, "CardTooltipController");
            if (curTipCtrl != null && (curTipCtrl is not UnityEngine.Object uo || uo != null))
            {
                var curTd = GetProp(curTipCtrl, "CurrentTooltipData");
                if (expectedTooltipData == null || ReferenceEquals(curTd, expectedTooltipData))
                {
                    curTipCtrl.GetType()
                        .GetMethod("ClearCurrentCard", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.Invoke(curTipCtrl, null);
                }
            }
        }
        catch (Exception ex)
        {
            Core.DiagLog.SwallowOnce("GameDriver.HideTooltip", ex);
        }
    }

    private static object GetCurrentAppState() => GameInspector.GetCurrentAppState();

    private static List<object> BuildSocketSpan(object card, string startSocketName)
    {
        var se = SocketEnum();
        int start = (int)Enum.Parse(se, startSocketName, ignoreCase: true);
        int width = 1;
        var sizeObj = GetProp(card, "Size");
        if (sizeObj != null)
        {
            string s = sizeObj.ToString();
            width = s == "Large" ? 3 : s == "Medium" ? 2 : 1;
        }
        int clampedStart = Math.Clamp(start, 0, Math.Max(0, 10 - width));
        var list = new List<object>(width);
        for (int i = 0; i < width; i++)
        {
            int idx = Math.Min(9, clampedStart + i);
            list.Add(Enum.ToObject(se, idx));
        }
        return list;
    }

    private static object DoBuy(JObject req)
    {
        var busyReject = TryRejectIfClientBusy("buy", req);
        if (busyReject != null) return busyReject;

        string id = (string)req["instanceId"];
        if (string.IsNullOrEmpty(id)) id = FirstSelectionId();
        if (string.IsNullOrEmpty(id)) return new { ok = false, error = "empty selection set" };

        string socketName = NormalizeSocketName((string)req["socket"]);
        string sectionName = (string)req["section"] ?? "Hand";
        var section = Enum.Parse(SectionEnum(), sectionName, ignoreCase: true);

        var card = FindCard(id);
        if (card != null && !IsItemCard(card, out string actualCardType))
        {
            return new
            {
                ok = false,
                op = "buy",
                error = $"cannot buy non-ItemCard ({actualCardType})",
                instanceId = id,
                cardType = actualCardType,
            };
        }

        var cur = GetCurrentAppState();
        if (cur != null && card != null)
        {
            var itemCardType = GetItemCardType();
            if (itemCardType != null && itemCardType.IsInstanceOfType(card))
            {
                if (string.IsNullOrWhiteSpace(socketName))
                {
                    var m3 = cur.GetType().GetMethod("BuyItemCommand",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { itemCardType, SectionEnum(), typeof(bool) },
                        null);
                    if (m3 != null)
                    {
                        bool ok = (bool)m3.Invoke(cur, new object[] { card, section, true });
                        return new { ok, op = "buy", via = "AppState.BuyItemCommand", instanceId = id };
                    }
                }
                else
                {
                    var socketListType = typeof(List<>).MakeGenericType(SocketEnum());
                    var m4 = cur.GetType().GetMethod("BuyItemCommand",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { itemCardType, socketListType, SectionEnum(), typeof(bool) },
                        null);
                    if (m4 != null)
                    {
                        var sockets = ConvertList(BuildSocketSpan(card, socketName), SocketEnum());
                        bool ok = (bool)m4.Invoke(cur, new object[] { card, sockets, section, true });
                        return new { ok, op = "buy", via = "AppState.BuyItemCommand(sockets)", instanceId = id, socket = socketName };
                    }
                }
            }
        }

        var cmd = GetCmd();
        if (cmd == null) return new { ok = false, error = "Cmd not available (not in run?)" };

        var iid = MakeInstanceId(id);
        string fallbackSocket = string.IsNullOrWhiteSpace(socketName) ? "Socket_3" : socketName;
        var rawSockets = card != null ? BuildSocketSpan(card, fallbackSocket) : new List<object> { Enum.Parse(SocketEnum(), fallbackSocket) };
        var m = cmd.GetType().GetMethod("SendSelectItem");
        m.Invoke(cmd, new object[] { iid, ConvertList(rawSockets, SocketEnum()), section, null });

        return new { ok = true, op = "buy", via = "Cmd.SendSelectItem", instanceId = id, socket = fallbackSocket };
    }

    private static object DoSell(JObject req)
    {
        var busyReject = TryRejectIfClientBusy("sell", req);
        if (busyReject != null) return busyReject;

        string id = (string)req["instanceId"];
        if (string.IsNullOrEmpty(id)) return new { ok = false, error = "instanceId required" };

        var card = FindCard(id);
        if (card != null && !IsItemCard(card, out string actualCardType))
        {
            return new
            {
                ok = false,
                op = "sell",
                error = $"cannot sell non-ItemCard ({actualCardType})",
                instanceId = id,
                cardType = actualCardType,
            };
        }

        var cur = GetCurrentAppState();
        var itemCardType = GetItemCardType();
        if (cur != null && card != null && itemCardType != null && itemCardType.IsInstanceOfType(card))
        {
            var mSell = cur.GetType().GetMethod("SellCardCommand",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { itemCardType }, null);
            if (mSell != null)
            {
                mSell.Invoke(cur, new object[] { card });
                return new { ok = true, op = "sell", via = "AppState.SellCardCommand", instanceId = id };
            }
        }

        var cmd = GetCmd();
        if (cmd == null) return new { ok = false, error = "Cmd not available" };

        var m = cmd.GetType().GetMethod("SendSellCard");
        m.Invoke(cmd, new object[] { MakeInstanceId(id), null });
        return new { ok = true, op = "sell", via = "Cmd.SendSellCard", instanceId = id };
    }

    private static object DoSelectSkill(JObject req)
    {
        var busyReject = TryRejectIfClientBusy("selectSkill", req);
        if (busyReject != null) return busyReject;

        string id = (string)req["instanceId"] ?? FirstSelectionId();
        if (string.IsNullOrEmpty(id)) return new { ok = false, error = "empty selection set" };

        var card = FindCard(id);
        var cur = GetCurrentAppState();
        var skillCardType = AccessTools.TypeByName("BazaarGameClient.Domain.Models.Cards.SkillCard");
        if (cur != null && card != null && skillCardType != null && skillCardType.IsInstanceOfType(card))
        {
            var mSkill = cur.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => x.Name == "SelectSkillCommand" && x.GetParameters().Length == 2);
            if (mSkill != null)
            {
                bool ok = (bool)mSkill.Invoke(cur, new object[] { card, null });
                return new { ok, op = "selectSkill", via = "AppState.SelectSkillCommand", instanceId = id };
            }
        }

        var cmd = GetCmd();
        if (cmd == null) return new { ok = false, error = "Cmd not available" };

        var m = cmd.GetType().GetMethod("SendSelectSkill");
        m.Invoke(cmd, new object[] { MakeInstanceId(id) });
        return new { ok = true, op = "selectSkill", via = "Cmd.SendSelectSkill", instanceId = id };
    }

    private static object DoSelectEncounter(JObject req)
    {
        var busyReject = TryRejectIfClientBusy("selectEncounter", req);
        if (busyReject != null) return busyReject;

        string id = (string)req["instanceId"] ?? FirstSelectionId();
        if (string.IsNullOrEmpty(id)) return new { ok = false, error = "empty selection set" };

        var iid = MakeInstanceId(id);
        var cur = GetCurrentAppState();
        if (cur != null)
        {
            var mEnc = cur.GetType().GetMethod("SelectEncounterCommand",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { iid.GetType() }, null);
            if (mEnc != null)
            {
                bool ok = (bool)mEnc.Invoke(cur, new object[] { iid });
                return new { ok, op = "selectEncounter", via = "AppState.SelectEncounterCommand", instanceId = id };
            }
        }

        var cmd = GetCmd();
        if (cmd == null) return new { ok = false, error = "Cmd not available" };

        var m = cmd.GetType().GetMethod("SelectEncounter");
        m.Invoke(cmd, new object[] { iid });
        return new { ok = true, op = "selectEncounter", via = "Cmd.SelectEncounter", instanceId = id };
    }

    /// <summary>点选奇遇选项卡（EncounterStep / EventEncounter）。</summary>
    private static object DoEncounterStep(JObject req)
    {
        var busyReject = TryRejectIfClientBusy("encounterstep", req);
        if (busyReject != null) return busyReject;

        string id = (string)req["instanceId"];
        if (string.IsNullOrEmpty(id))
        {
            var sel = GetSelectionIds();
            if (sel.Count == 0) return new { ok = false, error = "empty selection set" };

            int? index = (int?)req["index"];
            if (index.HasValue)
            {
                if (index.Value < 0 || index.Value >= sel.Count)
                    return new { ok = false, error = $"index {index.Value} out of range (count={sel.Count})" };
                id = sel[index.Value];
            }
            else
            {
                // 优先挑选 EncounterStep，其次 EventEncounter / PedestalEncounter / CombatEncounter，最后回退首位
                id = sel.FirstOrDefault(sid =>
                {
                    var c = FindCard(sid);
                    return string.Equals(GetProp(c, "Type")?.ToString(), "EncounterStep", StringComparison.OrdinalIgnoreCase);
                }) ?? sel.FirstOrDefault(sid =>
                {
                    var c = FindCard(sid);
                    string t = GetProp(c, "Type")?.ToString() ?? "";
                    return t.Contains("Encounter", StringComparison.OrdinalIgnoreCase);
                }) ?? sel[0];
            }
        }

        var card = FindCard(id);
        string cardType = GetProp(card, "Type")?.ToString() ?? "";
        string tplId = GetProp(card, "TemplateId")?.ToString() ?? "";
        int buyPrice = 0;
        if (GetProp(card, "Attributes") is IDictionary attrs) buyPrice = AttrInt(attrs, "BuyPrice");

        var iid = MakeInstanceId(id);
        var cur = GetCurrentAppState();
        if (cur != null)
        {
            var mEnc = cur.GetType().GetMethod("SelectEncounterCommand",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { iid.GetType() }, null);
            if (mEnc != null)
            {
                bool ok = (bool)mEnc.Invoke(cur, new object[] { iid });
                return new
                {
                    ok,
                    op = "encounterstep",
                    via = "AppState.SelectEncounterCommand",
                    instanceId = id,
                    templateId = tplId,
                    cardType,
                    buyPrice,
                    appState = GetAppStateName(),
                };
            }
        }

        var cmd = GetCmd();
        if (cmd == null) return new { ok = false, error = "Cmd not available (not in run?)" };

        var m = cmd.GetType().GetMethod("SelectEncounter");
        m.Invoke(cmd, new object[] { iid });
        return new
        {
            ok = true,
            op = "encounterstep",
            via = "Cmd.SelectEncounter",
            instanceId = id,
            templateId = tplId,
            cardType,
            buyPrice,
            appState = GetAppStateName(),
        };
    }

    private static object DoCommitPedestal(JObject req)
    {
        var busyReject = TryRejectIfClientBusy("commitPedestal", req);
        if (busyReject != null) return busyReject;

        string id = (string)req["instanceId"];
        if (string.IsNullOrEmpty(id)) return new { ok = false, op = "commitPedestal", error = "instanceId required" };

        var cur = GetCurrentAppState();
        string appStateName = cur?.GetType().Name ?? "null";
        var ds = GameInspector.GetDataCurrentState();
        string runStateName = GetProp(ds, "StateName")?.ToString() ?? "";

        // P0-01：提交前校验是否处于有效祭坛态且未在播上一轮升阶/附魔动画，防止并发重复提交触发服务端拒单与 GameStateSync 死锁
        bool inPedestal = string.Equals(appStateName, "PedestalState", StringComparison.Ordinal)
                          || string.Equals(runStateName, "Pedestal", StringComparison.OrdinalIgnoreCase);
        if (!inPedestal)
        {
            return new
            {
                ok = false,
                op = "commitPedestal",
                rejected = true,
                dispatched = false,
                errorCode = "NOT_IN_PEDESTAL_STATE",
                error = $"当前不在祭坛状态（appState={appStateName}, runState={runStateName}），拒绝提交卡牌 {id}",
                instanceId = id,
                appState = appStateName,
                runState = runStateName,
            };
        }

        if (string.Equals(appStateName, "PedestalState", StringComparison.Ordinal)
            && GetProp(cur, "CanHandleCurrentUpgradeMessage") is bool pedHandling && pedHandling
            && GetProp(cur, "_animationCompleted") is bool pedDone && !pedDone)
        {
            return new
            {
                ok = false,
                op = "commitPedestal",
                error = "retryable_busy",
                errorCode = "PEDESTAL_ALREADY_COMMITTED",
                retryable = true,
                dispatched = false,
                busyReason = "pedestalAnimating",
                instanceId = id,
            };
        }

        var card = FindCard(id);
        if (card == null)
        {
            return new
            {
                ok = false,
                op = "commitPedestal",
                rejected = true,
                dispatched = false,
                errorCode = "CARD_NOT_FOUND",
                error = $"未找到目标卡牌实例: {id}",
                instanceId = id,
            };
        }

        if (!IsItemCard(card, out string actualCardType))
        {
            return new
            {
                ok = false,
                op = "commitPedestal",
                rejected = true,
                dispatched = false,
                errorCode = "NOT_ITEM_CARD",
                error = $"祭坛仅接受物品卡（ItemCard），拒绝提交 {actualCardType}: {id}",
                instanceId = id,
                cardType = actualCardType,
            };
        }

        var owner = GetProp(card, "Owner");
        string ownerId = owner == null ? null : GetProp(owner, "CombatantId")?.ToString();
        string sectionStr = GetProp(card, "Section")?.ToString() ?? "";
        if (!string.Equals(ownerId, "Player", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(sectionStr, "Hand", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(sectionStr, "Stash", StringComparison.OrdinalIgnoreCase)))
        {
            return new
            {
                ok = false,
                op = "commitPedestal",
                rejected = true,
                dispatched = false,
                errorCode = "CARD_NOT_OWNED_IN_INVENTORY",
                error = $"卡牌 {id} 不在玩家手牌或背包中（owner={ownerId}, section={sectionStr}），无法提交祭坛",
                instanceId = id,
                owner = ownerId,
                section = sectionStr,
            };
        }

        // P0-01（驱动层预校验）：按当前祭坛 allowedTiers、附魔资格、升阶上限与 SelectionCriteria 预校验；
        // 不满足条件时直接返回清晰业务错误，严禁提前伪造 PedestalRequirementMet / ItemDroppedOnPedestal 视觉吸卡事件！
        if (!ValidatePedestalCommitEligibility(cur, card, id, out object rejectResult))
        {
            return rejectResult;
        }

        var iid = MakeInstanceId(id);
        MethodInfo mPed = cur?.GetType().GetMethod("CommitToPedestalCommand",
            BindingFlags.Public | BindingFlags.Instance, null, new[] { iid.GetType() }, null);

        object cmd = null;
        MethodInfo mCmdSend = null;
        if (mPed == null)
        {
            cmd = GetCmd();
            if (cmd == null) return new { ok = false, op = "commitPedestal", dispatched = false, error = "Cmd not available" };
            mCmdSend = cmd.GetType().GetMethod("SendCommitToPedestal");
            if (mCmdSend == null) return new { ok = false, op = "commitPedestal", dispatched = false, error = "SendCommitToPedestal not found" };
        }

        // 仅在全部预校验与发包入口均通过后，才触发祭坛动画看门狗与合法视觉吸卡事件
        CompatAuditPatch.NotifyPedestalCommitted();
        TryTriggerPedestalDropEvents(id);

        if (mPed != null)
        {
            mPed.Invoke(cur, new object[] { iid });
            return new { ok = true, op = "commitPedestal", dispatched = true, via = "AppState.CommitToPedestalCommand", instanceId = id };
        }

        mCmdSend.Invoke(cmd, new object[] { iid });
        return new { ok = true, op = "commitPedestal", dispatched = true, via = "Cmd.SendCommitToPedestal", instanceId = id };
    }

    /// <summary>
    /// P0-01：提交祭坛前按当前祭坛 allowedTiers、附魔白名单/防重复附魔、升阶上限及 SelectionCriteria 做严格只读预校验。
    /// 任一条件不满足均立即返回清晰业务错误并阻止伪造视觉吸卡与发包。
    /// </summary>
    private static bool ValidatePedestalCommitEligibility(object curState, object card, string instanceId, out object rejectResult)
    {
        rejectResult = null;

        string currentEncounterId = GetProp(GameInspector.GetDataCurrentState(), "CurrentEncounterId")?.ToString()
                                    ?? GameInspector.GetDataCurrentEncounterId()?.ToString();
        var pedInfo = GameInspector.DescribePedestalState(currentEncounterId);
        var pedTpl = ResolveActivePedestalTemplate(curState);

        string pedName = pedTpl?.InternalName
                         ?? (pedInfo != null && pedInfo.TryGetValue("name", out var pn) ? pn?.ToString() : null)
                         ?? currentEncounterId
                         ?? "Pedestal";
        string pedKind = pedInfo != null && pedInfo.TryGetValue("kind", out var pk) ? pk?.ToString() ?? "unknown" : "unknown";

        string cardTierStr = GetProp(card, "Tier")?.ToString() ?? "";
        ETier? cardTier = GetProp(card, "Tier") is ETier et
            ? et
            : (Enum.TryParse<ETier>(cardTierStr, true, out var parsedTier) ? parsedTier : (ETier?)null);

        // 1. 校验祭坛 allowedTiers 白名单（例如 B1&B2 青铜祭坛拒绝银/金/钻卡）
        List<string> allowedTiers = null;
        if (pedInfo != null && pedInfo.TryGetValue("allowedTiers", out var atObj) && atObj is List<string> atList && atList.Count > 0)
        {
            allowedTiers = atList;
        }
        if (allowedTiers != null && allowedTiers.Count > 0
            && !allowedTiers.Any(t => string.Equals(t, cardTierStr, StringComparison.OrdinalIgnoreCase)))
        {
            rejectResult = new
            {
                ok = false,
                op = "commitPedestal",
                rejected = true,
                dispatched = false,
                errorCode = "PEDESTAL_TIER_NOT_ALLOWED",
                error = $"卡牌 {instanceId} 品阶 ({cardTierStr}) 不在当前祭坛 {pedName} 允许品阶 [{string.Join(", ", allowedTiers)}] 内",
                instanceId,
                cardTier = cardTierStr,
                allowedTiers,
                pedestalKind = pedKind,
                pedestalName = pedName,
            };
            return false;
        }

        var itemTpl = GetProp(card, "Template") as TCardItem;
        EEnchantmentType? currentEnchantment = ReadCardEnchantment(card);
        var behavior = pedTpl?.Behavior;

        // 2. 按祭坛行为做专项业务校验（升阶上限 / 变形限制 / 附魔白名单与防重复附魔）
        if (behavior is TPedestalBehaviorTransform || string.Equals(pedKind, "transform", StringComparison.OrdinalIgnoreCase))
        {
            if (cardTier == ETier.Legendary)
            {
                rejectResult = new
                {
                    ok = false,
                    op = "commitPedestal",
                    rejected = true,
                    dispatched = false,
                    errorCode = "PEDESTAL_LEGENDARY_CANNOT_TRANSFORM",
                    error = $"传奇品阶卡牌 {instanceId} 无法在变形祭坛 {pedName} 提交变形",
                    instanceId,
                    cardTier = cardTierStr,
                    pedestalKind = "transform",
                    pedestalName = pedName,
                };
                return false;
            }
        }
        else if (behavior is TPedestalBehaviorEnchant fixedEnchBeh)
        {
            EEnchantmentType targetEnch = fixedEnchBeh.Enchantment;
            if (currentEnchantment.HasValue && currentEnchantment.Value == targetEnch)
            {
                rejectResult = new
                {
                    ok = false,
                    op = "commitPedestal",
                    rejected = true,
                    dispatched = false,
                    errorCode = "PEDESTAL_ENCHANT_DUPLICATE",
                    error = $"卡牌 {instanceId} 已拥有 {targetEnch} 附魔，无法在祭坛 {pedName} 重复附魔",
                    instanceId,
                    currentEnchantment = currentEnchantment.Value.ToString(),
                    targetEnchantment = targetEnch.ToString(),
                    pedestalKind = "enchant",
                    pedestalName = pedName,
                };
                return false;
            }
            if (itemTpl != null && (itemTpl.Enchantments == null || !itemTpl.Enchantments.ContainsKey(targetEnch)))
            {
                rejectResult = new
                {
                    ok = false,
                    op = "commitPedestal",
                    rejected = true,
                    dispatched = false,
                    errorCode = "PEDESTAL_ENCHANT_NOT_ELIGIBLE",
                    error = $"卡牌 {instanceId} 不支持 {targetEnch} 附魔（祭坛 {pedName}）",
                    instanceId,
                    targetEnchantment = targetEnch.ToString(),
                    pedestalKind = "enchant",
                    pedestalName = pedName,
                };
                return false;
            }
        }
        else if (behavior is TPedestalBehaviorEnchantRandom randEnchBeh)
        {
            if (itemTpl != null)
            {
                if (itemTpl.Enchantments == null || itemTpl.Enchantments.Count == 0)
                {
                    rejectResult = new
                    {
                        ok = false,
                        op = "commitPedestal",
                        rejected = true,
                        dispatched = false,
                        errorCode = "PEDESTAL_ENCHANT_NOT_ELIGIBLE",
                        error = $"卡牌 {instanceId} 不支持任何附魔（祭坛 {pedName}）",
                        instanceId,
                        pedestalKind = "enchant",
                        pedestalName = pedName,
                    };
                    return false;
                }

                bool hasCandidate = randEnchBeh.Enchantments != null && randEnchBeh.Enchantments.Count > 0
                    ? randEnchBeh.Enchantments.Any(entry => entry != null
                                                            && itemTpl.Enchantments.ContainsKey(entry.Enchantment)
                                                            && (!currentEnchantment.HasValue || currentEnchantment.Value != entry.Enchantment))
                    : itemTpl.Enchantments.Keys.Any(e => !currentEnchantment.HasValue || currentEnchantment.Value != e);
                if (!hasCandidate)
                {
                    rejectResult = new
                    {
                        ok = false,
                        op = "commitPedestal",
                        rejected = true,
                        dispatched = false,
                        errorCode = "PEDESTAL_ENCHANT_NOT_ELIGIBLE",
                        error = $"卡牌 {instanceId} 没有可应用的新随机附魔（当前附魔={currentEnchantment?.ToString() ?? "无"}，祭坛 {pedName}）",
                        instanceId,
                        currentEnchantment = currentEnchantment?.ToString(),
                        pedestalKind = "enchant",
                        pedestalName = pedName,
                    };
                    return false;
                }
            }
        }
        else if (behavior is TPedestalBehaviorUpgrade upgBeh)
        {
            if (!CanCardUpgradeForPedestal(card, itemTpl, cardTier))
            {
                rejectResult = new
                {
                    ok = false,
                    op = "commitPedestal",
                    rejected = true,
                    dispatched = false,
                    errorCode = "PEDESTAL_MAX_TIER",
                    error = $"卡牌 {instanceId} ({cardTierStr}) 已达最高品阶，无法在升阶祭坛 {pedName} 继续升阶",
                    instanceId,
                    cardTier = cardTierStr,
                    pedestalKind = "upgrade",
                    pedestalName = pedName,
                };
                return false;
            }
            if (upgBeh.TargetTier.HasValue && cardTier.HasValue && cardTier.Value >= upgBeh.TargetTier.Value)
            {
                rejectResult = new
                {
                    ok = false,
                    op = "commitPedestal",
                    rejected = true,
                    dispatched = false,
                    errorCode = "PEDESTAL_TIER_NOT_ALLOWED",
                    error = $"卡牌 {instanceId} ({cardTierStr}) 已不低于祭坛目标品阶 {upgBeh.TargetTier.Value}",
                    instanceId,
                    cardTier = cardTierStr,
                    targetTier = upgBeh.TargetTier.Value.ToString(),
                    pedestalKind = "upgrade",
                    pedestalName = pedName,
                };
                return false;
            }
        }
        else if (string.Equals(pedKind, "enchant", StringComparison.OrdinalIgnoreCase))
        {
            string targetEnchStr = pedInfo != null && pedInfo.TryGetValue("targetEnchantment", out var te) ? te?.ToString() : null;
            if (!string.IsNullOrEmpty(targetEnchStr) && Enum.TryParse<EEnchantmentType>(targetEnchStr, true, out var targetEnch))
            {
                if (currentEnchantment.HasValue && currentEnchantment.Value == targetEnch)
                {
                    rejectResult = new
                    {
                        ok = false,
                        op = "commitPedestal",
                        rejected = true,
                        dispatched = false,
                        errorCode = "PEDESTAL_ENCHANT_DUPLICATE",
                        error = $"卡牌 {instanceId} 已拥有 {targetEnch} 附魔，无法重复附魔",
                        instanceId,
                        currentEnchantment = currentEnchantment.Value.ToString(),
                        targetEnchantment = targetEnch.ToString(),
                        pedestalKind = "enchant",
                        pedestalName = pedName,
                    };
                    return false;
                }
                if (itemTpl != null && (itemTpl.Enchantments == null || !itemTpl.Enchantments.ContainsKey(targetEnch)))
                {
                    rejectResult = new
                    {
                        ok = false,
                        op = "commitPedestal",
                        rejected = true,
                        dispatched = false,
                        errorCode = "PEDESTAL_ENCHANT_NOT_ELIGIBLE",
                        error = $"卡牌 {instanceId} 不支持 {targetEnch} 附魔",
                        instanceId,
                        targetEnchantment = targetEnch.ToString(),
                        pedestalKind = "enchant",
                        pedestalName = pedName,
                    };
                    return false;
                }
            }
            else if (itemTpl != null && (itemTpl.Enchantments == null || itemTpl.Enchantments.Count == 0))
            {
                rejectResult = new
                {
                    ok = false,
                    op = "commitPedestal",
                    rejected = true,
                    dispatched = false,
                    errorCode = "PEDESTAL_ENCHANT_NOT_ELIGIBLE",
                    error = $"卡牌 {instanceId} 不支持任何附魔",
                    instanceId,
                    pedestalKind = "enchant",
                    pedestalName = pedName,
                };
                return false;
            }
        }
        else if (string.Equals(pedKind, "upgrade", StringComparison.OrdinalIgnoreCase))
        {
            if (!CanCardUpgradeForPedestal(card, itemTpl, cardTier))
            {
                rejectResult = new
                {
                    ok = false,
                    op = "commitPedestal",
                    rejected = true,
                    dispatched = false,
                    errorCode = "PEDESTAL_MAX_TIER",
                    error = $"卡牌 {instanceId} ({cardTierStr}) 已达最高品阶，无法继续升阶",
                    instanceId,
                    cardTier = cardTierStr,
                    pedestalKind = "upgrade",
                    pedestalName = pedName,
                };
                return false;
            }
        }

        // 3. 严格只读执行官方 SelectionCriteria.IsSatisfiedBy（覆盖全部复合条件：品阶/附魔资格/标签/尺寸/无附魔等，不改写 PedestalState 状态）
        if (pedTpl?.SelectionCriteria != null && TryEvaluatePedestalSelectionCriteriaReadOnly(pedTpl, card, out bool criteriaSatisfied) && !criteriaSatisfied)
        {
            string targetEnchHint = pedInfo != null && pedInfo.TryGetValue("targetEnchantment", out var te2) ? te2?.ToString() : null;
            rejectResult = new
            {
                ok = false,
                op = "commitPedestal",
                rejected = true,
                dispatched = false,
                errorCode = "PEDESTAL_CRITERIA_NOT_MET",
                error = $"卡牌 {instanceId} ({cardTierStr}) 不满足当前祭坛 {pedName} 的选择条件（SelectionCriteria）",
                instanceId,
                cardTier = cardTierStr,
                allowedTiers,
                currentEnchantment = currentEnchantment?.ToString(),
                targetEnchantment = targetEnchHint,
                pedestalKind = pedKind,
                pedestalName = pedName,
            };
            return false;
        }

        return true;
    }

    private static TCardEncounterPedestal ResolveActivePedestalTemplate(object curState)
    {
        try
        {
            if (curState != null && string.Equals(curState.GetType().Name, "PedestalState", StringComparison.Ordinal))
            {
                if (GetProp(curState, "_pedestalTemplate") is TCardEncounterPedestal fromState)
                    return fromState;
            }

            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data", "Data");
            var curEncCtrl = dataType?.GetProperty("CurrentEncounterController", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (curEncCtrl != null && (curEncCtrl is not UnityEngine.Object uo || uo != null))
            {
                var encCard = GetProp(curEncCtrl, "CardData");
                if (GetProp(encCard, "Template") is TCardEncounterPedestal fromCtrl)
                    return fromCtrl;
            }

            if (GameInspector.GetDataCurrentEncounterId() is Guid encGuid && encGuid != Guid.Empty && dataType != null)
            {
                var getStaticM = dataType.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                var staticMgr = getStaticM?.Invoke(null, null);
                var getCardM = staticMgr?.GetType().GetMethod("GetCardById", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Guid) }, null);
                if (getCardM?.Invoke(staticMgr, new object[] { encGuid }) is TCardEncounterPedestal fromStatic)
                    return fromStatic;
            }
        }
        catch { }
        return null;
    }

    private static EEnchantmentType? ReadCardEnchantment(object card)
    {
        if (card == null) return null;
        try
        {
            var raw = GetProp(card, "Enchantment")
                      ?? card.GetType().GetMethod("GetEnchantment", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(card, null);
            if (raw is EEnchantmentType et) return et;
            if (raw != null && Enum.TryParse<EEnchantmentType>(raw.ToString(), true, out var parsed))
                return parsed;
        }
        catch { }
        return null;
    }

    private static bool CanCardUpgradeForPedestal(object card, TCardItem itemTpl, ETier? cardTier)
    {
        try
        {
            if (_canCardUpgradeMethod == null)
            {
                var cardExtType = GameInspector.FindTypeSafe("TheBazaar.CardExtensions");
                _canCardUpgradeMethod = cardExtType?.GetMethod("CanCardUpgrade", BindingFlags.Public | BindingFlags.Static);
            }
            if (_canCardUpgradeMethod != null)
            {
                return _canCardUpgradeMethod.Invoke(null, new[] { card }) is bool canUpg && canUpg;
            }
        }
        catch { }

        if (!cardTier.HasValue) return true;
        if (itemTpl?.Tiers != null && itemTpl.Tiers.Count > 0)
        {
            return itemTpl.Tiers.Keys.Any(t => t > cardTier.Value);
        }
        return cardTier.Value < ETier.Diamond;
    }

    private static bool TryEvaluatePedestalSelectionCriteriaReadOnly(TCardEncounterPedestal pedTpl, object card, out bool satisfied)
    {
        satisfied = true;
        if (pedTpl?.SelectionCriteria == null || card is not ICard icard)
            return false;
        try
        {
            var run = GameInspector.GetDataRun();
            if (run == null) return false;
            if (_cardCondContextCtor == null)
            {
                var ctors = typeof(CardConditionalContext).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _cardCondContextCtor = ctors.FirstOrDefault(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length == 4 && ps[0].ParameterType.IsInstanceOfType(run);
                });
            }
            if (_cardCondContextCtor == null) return false;
            if (_cardCondContextCtor.Invoke(new[] { run, null, null, null }) is not CardConditionalContext ctx)
                return false;

            satisfied = pedTpl.SelectionCriteria.IsSatisfiedBy(icard, ctx);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryTriggerPedestalDropEvents(string instanceId)
    {
        try
        {
            // 仅当场景存在活跃的 3D BoardStatePresentation 时才派发吸卡视觉事件，避免无呈现载体时伪造事件污染状态机
            var bm = GameInspector.GetBoardManagerCached();
            var pres = bm != null ? GetProp(bm, "BoardStatePresentation") : null;
            if (pres == null || (pres is UnityEngine.Object presUo && presUo == null)) return;

            var card = FindCard(instanceId);
            var lookup = GameInspector.GetDataLookup();
            if (card == null || lookup == null) return;

            var ctrl = GetCardControllerSafe(lookup, card);
            if (ctrl == null) return;

            var itemCtrlType = GameInspector.FindTypeSafe("TheBazaar.ItemController", "ItemController");
            if (itemCtrlType == null || !itemCtrlType.IsInstanceOfType(ctrl)) return;

            // 若合法提交的卡牌来自关闭态背包，先激活并显形，与官方 PrepareObservedPedestalPresentation 保持一致
            if (ctrl is Component comp && comp.gameObject != null && !comp.gameObject.activeSelf)
            {
                comp.gameObject.SetActive(true);
            }
            var showCardM = ctrl.GetType().GetMethod("ShowCard", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            showCardM?.Invoke(ctrl, new object[] { true });

            GameEventsBridge.TryTrigger("PedestalRequirementMet");
            GameEventsBridge.TryTrigger("ItemDroppedOnPedestal", ctrl);
        }
        catch { }
    }

    private static MethodInfo _lookupGetCardController;

    private static object GetCardControllerSafe(object lookup, object card)
    {
        if (lookup == null || card == null) return null;
        _lookupGetCardController ??= lookup.GetType().GetMethod(
            "GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var ctrl = _lookupGetCardController?.Invoke(lookup, new[] { card });
        if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) return null;
        return ctrl;
    }

    private static bool AreSelectionBuyTargetsReady(string targetId)
    {
        try
        {
            var lookup = GameInspector.GetDataLookup();
            if (lookup == null) return true;

            if (!string.IsNullOrEmpty(targetId))
            {
                var card = FindCard(targetId);
                if (card == null || !IsItemCard(card, out _)) return true;
                return GetCardControllerSafe(lookup, card) != null;
            }

            string appState = GetAppStateName();
            var ds = GameInspector.GetDataCurrentState();
            string runState = GetProp(ds, "StateName")?.ToString() ?? "";
            if (appState is not ("EncounterState" or "LootState" or "LevelUpState") &&
                runState is not ("Encounter" or "Loot" or "LevelUp"))
            {
                return true;
            }

            foreach (string sid in GetSelectionIds())
            {
                var card = FindCard(sid);
                if (card != null && IsItemCard(card, out _) && GetCardControllerSafe(lookup, card) == null)
                    return false;
            }
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static object DoCombatSpeed(JObject req)
    {
        if (req["speed"] != null)
        {
            float speed = (float)req["speed"];
            CombatPatch.SpeedOverride = Math.Max(0f, speed);
        }
        if (req["manualSpeed"] != null)
        {
            float mSpeed = (float)req["manualSpeed"];
            CombatPatch.SetManualUiSpeed(mSpeed);
        }
        if (req["autoExitReplay"] != null)
        {
            CombatPatch.AutoExitReplay = (bool)req["autoExitReplay"];
        }
        return new
        {
            ok = true,
            op = "combatspeed",
            speed = CombatPatch.SpeedOverride,
            manualSpeed = CombatPatch.ManualUiSpeed,
            effectiveSpeed = CombatPatch.GetDisplayCombatSpeed(),
            autoExitReplay = CombatPatch.AutoExitReplay,
        };
    }

    private static object DoExit(JObject req = null)
    {
        var busyReject = TryRejectIfClientBusy("exit", req);
        if (busyReject != null) return busyReject;

        var advanced = TryAdvanceNonCommandState();
        if (advanced != null) return advanced;

        string appStateName = GetAppStateName();
        var dataState = GameInspector.GetDataCurrentState();
        string runStateName = GetProp(dataState, "StateName")?.ToString() ?? "";
        var selRules = GetProp(dataState, "SelectionContextRules");
        bool? canExit = selRules != null ? GetProp(selRules, "CanExit") as bool? : null;

        bool isChoice = string.Equals(appStateName, "ChoiceState", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(runStateName, "Choice", StringComparison.OrdinalIgnoreCase);
        bool isLocked = (canExit == false && !string.Equals(appStateName, "ReplayState", StringComparison.OrdinalIgnoreCase))
                        || runStateName is "Combat" or "PVPCombat" or "EndRunVictory" or "EndRunDefeat" or "Shutdown";

        if (isChoice || isLocked)
        {
            return new
            {
                ok = false,
                op = "exit",
                skipped = true,
                reason = "cannot exit Choice/locked state",
                appState = appStateName,
                runState = runStateName,
                canExit,
            };
        }

        var cur = GetCurrentAppState();
        var mExit = cur?.GetType().GetMethod("ExitStateCommand", BindingFlags.Public | BindingFlags.Instance);
        if (mExit != null)
        {
            mExit.Invoke(cur, null);
            return new { ok = true, op = "exit", via = "AppState.ExitStateCommand" };
        }

        var cmd = GetCmd();
        if (cmd == null) return new { ok = false, error = "Cmd not available" };
        var m = cmd.GetType().GetMethod("SendExitCurrentState");
        if (m == null) return new { ok = false, error = "method not found: SendExitCurrentState" };
        m.Invoke(cmd, m.GetParameters().Length == 0 ? null : new object[m.GetParameters().Length]);
        return new { ok = true, op = "exit", via = "Cmd.SendExitCurrentState" };
    }

    private static object DoSimple(string methodName, string opName, JObject req = null)
    {
        if (opName == "exit")
        {
            return DoExit(req);
        }
        else if (opName == "reroll")
        {
            var busyReject = TryRejectIfClientBusy("reroll", req);
            if (busyReject != null) return busyReject;

            var cur = GetCurrentAppState();
            var mReroll = cur?.GetType().GetMethod("RerollCommand", BindingFlags.Public | BindingFlags.Instance);
            if (mReroll != null)
            {
                bool ok = (bool)mReroll.Invoke(cur, null);
                return new { ok, op = opName, via = "AppState.RerollCommand" };
            }
        }

        var cmd = GetCmd();
        if (cmd == null) return new { ok = false, error = "Cmd not available" };
        var m = cmd.GetType().GetMethod(methodName);
        if (m == null) return new { ok = false, error = "method not found: " + methodName };
        m.Invoke(cmd, m.GetParameters().Length == 0 ? null : new object[m.GetParameters().Length]);
        return new { ok = true, op = opName, via = "Cmd." + methodName };
    }

    private static object TryAdvanceNonCommandState()
    {
        try
        {
            var cur = GetCurrentAppState();
            if (cur == null) return null;

            string name = cur.GetType().Name;

            if (name == "ReplayState")
            {
                if (GetProp(cur, "_exitRequested") is bool exitReq && exitReq)
                    return null;
                var ds = GameInspector.GetDataCurrentState();
                string runSt = GetProp(ds, "StateName")?.ToString() ?? "";
                if (string.Equals(runSt, "Choice", StringComparison.OrdinalIgnoreCase)
                    || runSt is "EndRunVictory" or "EndRunDefeat" or "Shutdown")
                    return null;

                var bm = GameInspector.GetBoardManagerCached();
                var forceExit = bm?.GetType().GetMethod("ForceExitReplayState", BindingFlags.Public | BindingFlags.Instance);
                if (forceExit != null)
                {
                    forceExit.Invoke(bm, null);
                    return new { ok = true, op = "exit", via = "BoardManager.ForceExitReplayState" };
                }
                var exit = cur.GetType().GetMethod("Exit", BindingFlags.Public | BindingFlags.Instance);
                if (exit != null)
                {
                    exit.Invoke(cur, null);
                    return new { ok = true, op = "exit", via = "ReplayState.Exit" };
                }
            }

            if (name == "CombatState")
            {
                var go = cur.GetType().GetMethod("GoToNextState", BindingFlags.Public | BindingFlags.Instance);
                if (go != null)
                {
                    go.Invoke(cur, null);
                    return new { ok = true, op = "exit", via = "CombatState.GoToNextState" };
                }
            }

            if (name is "EndRunVictoryState" or "EndRunDefeatState")
            {
                return ReturnToMenuInternal("exit");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("TryAdvanceNonCommandState: " + ex.Message);
        }
        return null;
    }

    // ── Resume / Concede（先在工作线程与服务端 HTTP 交互，再切主线程操作客户端）──

    internal static async Task<object> DoResumeAsync(JObject req)
    {
        bool force = (bool?)req["force"] ?? false;
        bool clickButton = (bool?)req["clickButton"] ?? false;

        // 1. 先检查当前是否已在对局内
        var preCheck = RunOnMain(() =>
        {
            var dataType = AccessTools.TypeByName("TheBazaar.Data");
            var run = dataType?.GetProperty("Run", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            string appName = GetAppStateName();
            bool inActiveState = run != null && appName != "null" &&
                                 appName != "EmptyState" &&
                                 appName != "EndRunVictoryState" &&
                                 appName != "EndRunDefeatState";
            return new { inActiveState, appName };
        });

        if (preCheck.inActiveState && !force)
        {
            return new
            {
                ok = true,
                op = "resume",
                alreadyInRun = true,
                appState = preCheck.appName,
                note = "already inside active run",
            };
        }

        // 2. 在后台线程拉取 /runs/active/summary 确保 ClientCache.RunConfig 同步最新活跃对局
        object activeSummary = null;
        bool hasActiveRun = false;
        string serverHero = null;
        string serverPlayMode = null;
        uint serverDay = 0;
        uint serverHour = 0;
        string summaryErr = null;

        try
        {
            activeSummary = await FetchActiveRunSummaryAsync().ConfigureAwait(false);
            if (activeSummary != null)
            {
                hasActiveRun = GetProp(activeSummary, "HasActiveRun") is bool b && b;
                serverHero = GetProp(activeSummary, "Hero")?.ToString();
                serverPlayMode = GetProp(activeSummary, "PlayMode")?.ToString();
                serverDay = Convert.ToUInt32(GetProp(activeSummary, "Day") ?? 0u);
                serverHour = Convert.ToUInt32(GetProp(activeSummary, "Hour") ?? 0u);
            }
        }
        catch (Exception ex)
        {
            summaryErr = ex.Message;
        }

        // 3. 回主线程更新 RunConfig 并触发 Resume Run 流程
        return RunOnMain(() =>
        {
            if (!hasActiveRun && !force)
            {
                return (object)new
                {
                    ok = false,
                    op = "resume",
                    hasActiveRun = false,
                    error = summaryErr ?? "no active run on server",
                    appState = GetAppStateName(),
                };
            }

            if (activeSummary != null)
            {
                SyncClientRunConfigFromSummary(activeSummary);
            }

            string via = null;
            if (clickButton)
            {
                var viewType = AccessTools.TypeByName("TheBazaar.UI.MainMenu.MainMenuBaseView");
                var view = viewType == null ? null : UnityEngine.Object.FindFirstObjectByType(viewType);
                var playBtn = view == null ? null : GetProp(view, "_playButton");
                var onClick = playBtn == null ? null : GetProp(playBtn, "onClick");
                var invokeM = onClick?.GetType().GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
                if (invokeM != null)
                {
                    invokeM.Invoke(onClick, null);
                    via = "MainMenuBaseView._playButton.onClick";
                }
            }

            if (via == null)
            {
                var giType = ResolveGameInstanceType();
                var gi = giType == null ? null : UnityEngine.Object.FindFirstObjectByType(giType);
                if (gi == null) return new { ok = false, op = "resume", error = "GameInstance not found" };

                // 若前一局停在 EndRunVictoryState / EndRunDefeatState，先回到主菜单再开
                string curName = GetAppStateName();
                if (curName is "EndRunVictoryState" or "EndRunDefeatState")
                {
                    return ReturnToMenuInternal("resume", preferLoadMainMenu: true);
                }

                // 若已有残留 NetworkManager 会话，先释放避免 StartNewRun 拦截
                DisposeNetworkManagerIfInitialized();

                var startNewRun = giType.GetMethod("StartNewRun", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (startNewRun == null) return new { ok = false, op = "resume", error = "StartNewRun not found" };
                startNewRun.Invoke(gi, null);
                via = "GameInstance.StartNewRun (Resume)";
            }

            // P2-12：记录重连派发时刻，为重连后慢铺牌窗口自动提供至少 10s 的 pre_wait 宽限期
            Interlocked.Exchange(ref _lastResumeAtTicks, Stopwatch.GetTimestamp());

            return (object)new
            {
                ok = true,
                op = "resume",
                via,
                hasActiveRun,
                hero = serverHero,
                playMode = serverPlayMode,
                day = serverDay,
                hour = serverHour,
                appState = GetAppStateName(),
            };
        });
    }

    internal static async Task<object> DoConcedeAsync(JObject req)
    {
        bool forceHttp = (bool?)req["forceHttp"] ?? false;

        // 1. 若在局内且当前 AppState 允许 AbandonRun，直接走标准局内投降命令
        if (!forceHttp)
        {
            var inRunResult = RunOnMain(() =>
            {
                var cur = GetCurrentAppState();
                string curName = cur?.GetType().Name ?? "null";
                if (cur != null && curName != "EmptyState" && curName != "EndRunVictoryState" && curName != "EndRunDefeatState")
                {
                    var stateOpsType = AccessTools.TypeByName("TheBazaar.StateOps");
                    var canHandle = cur.GetType().GetMethod("CanHandleOperation", BindingFlags.Public | BindingFlags.Instance);
                    if (stateOpsType != null && canHandle != null)
                    {
                        object abandonOp = Enum.Parse(stateOpsType, "AbandonRun");
                        bool allowed = (bool)canHandle.Invoke(cur, new[] { abandonOp });
                        if (allowed)
                        {
                            var abandonM = cur.GetType().GetMethod("AbandonRunCommand", BindingFlags.Public | BindingFlags.Instance);
                            if (abandonM != null)
                            {
                                abandonM.Invoke(cur, null);
                                return new { handled = true, via = "AppState.AbandonRunCommand", appState = curName };
                            }
                        }
                    }
                }
                return new { handled = false, via = (string)null, appState = curName };
            });

            if (inRunResult.handled)
            {
                return new { ok = true, op = "concede", via = inRunResult.via, appState = inRunResult.appState };
            }
        }

        // 2. 在主大厅或不可 AbandonRun 的状态（如 Combat / 断线状态）：调 HTTP POST /runs/active/concede
        string err = null;
        string accountId = null;
        string runId = null;
        try
        {
            var resp = await ConcedeActiveRunHttpAsync().ConfigureAwait(false);
            if (resp != null)
            {
                accountId = GetProp(resp, "AccountId")?.ToString();
                runId = GetProp(resp, "RunId")?.ToString();
            }
        }
        catch (Exception ex)
        {
            err = ex.Message;
        }

        if (err != null)
        {
            return new { ok = false, op = "concede", via = "POST /runs/active/concede", error = err };
        }

        // 3. 回主线程清理客户端本地缓存并刷新主菜单/回主大厅
        return RunOnMain(() =>
        {
            ClearClientActiveRunFlag();
            DisposeNetworkManagerIfInitialized();

            string activeScene = GameInspector.GetActiveSceneName();
            string curName = GetAppStateName();
            bool returningToMenu = false;
            object returnDetail = null;

            if (GameInspector.IsMainMenuSceneName(activeScene))
            {
                ResetAppStateToEmpty();
            }
            else if ((curName != "null" && curName != "EmptyState") ||
                     string.Equals(activeScene, "Gameplay", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(activeScene, "EndOfRun", StringComparison.OrdinalIgnoreCase))
            {
                returnDetail = ReturnToMenuInternal("concede", preferLoadMainMenu: true);
                returningToMenu = true;
            }

            return (object)new
            {
                ok = true,
                op = "concede",
                via = "CurrentRunHttpClient.ConcedeActiveRun",
                accountId,
                runId,
                returningToMenu,
                returnDetail,
                activeScene = GameInspector.GetActiveSceneName(),
                appState = GetAppStateName(),
            };
        });
    }

    private static async Task<object> FetchActiveRunSummaryAsync()
    {
        var cfgType = AccessTools.TypeByName("TheBazaar.Config");
        string baseUrl = (cfgType?.GetProperty("SocketURL", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string)
                         ?? BazaarLauncherPlugin.ServerBase.Value;
        var clientType = AccessTools.TypeByName("Networking.CurrentRunHttpClient");
        if (clientType == null) throw new InvalidOperationException("CurrentRunHttpClient not found");

        using var client = Activator.CreateInstance(clientType, new object[] { baseUrl }) as IDisposable;
        var m = clientType.GetMethod("CurrentRunData", BindingFlags.Public | BindingFlags.Instance);
        var task = m?.Invoke(client, new object[] { CancellationToken.None }) as Task;
        if (task == null) throw new InvalidOperationException("CurrentRunData failed to start");

        await task.ConfigureAwait(false);
        var resObj = task.GetType().GetProperty("Result")?.GetValue(task);
        bool isOk = resObj != null && (GetProp(resObj, "IsOk") is bool b && b);
        if (!isOk)
        {
            string msg = GetProp(resObj, "ErrorMessage")?.ToString() ?? "CurrentRunData failed";
            throw new InvalidOperationException(msg);
        }
        return GetProp(resObj, "Data");
    }

    private static async Task<object> ConcedeActiveRunHttpAsync()
    {
        var cfgType = AccessTools.TypeByName("TheBazaar.Config");
        string baseUrl = (cfgType?.GetProperty("SocketURL", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string)
                         ?? BazaarLauncherPlugin.ServerBase.Value;
        var clientType = AccessTools.TypeByName("Networking.CurrentRunHttpClient");
        if (clientType == null) throw new InvalidOperationException("CurrentRunHttpClient not found");

        using var client = Activator.CreateInstance(clientType, new object[] { baseUrl }) as IDisposable;
        var m = clientType.GetMethod("ConcedeActiveRun", BindingFlags.Public | BindingFlags.Instance);
        var task = m?.Invoke(client, new object[] { CancellationToken.None }) as Task;
        if (task == null) throw new InvalidOperationException("ConcedeActiveRun failed to start");

        await task.ConfigureAwait(false);
        var resObj = task.GetType().GetProperty("Result")?.GetValue(task);
        bool isOk = resObj != null && (GetProp(resObj, "IsOk") is bool b && b);
        if (!isOk)
        {
            string msg = GetProp(resObj, "ErrorMessage")?.ToString() ?? "ConcedeActiveRun failed";
            throw new InvalidOperationException(msg);
        }
        return GetProp(resObj, "Data");
    }

    private static void SyncClientRunConfigFromSummary(object dto)
    {
        try
        {
            var cacheType = AccessTools.TypeByName("TheBazaar.ClientCache");
            var runCfg = cacheType?.GetField("RunConfig", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (runCfg == null) return;

            bool hasActive = GetProp(dto, "HasActiveRun") is bool b && b;
            string heroStr = GetProp(dto, "Hero")?.ToString();
            string modeStr = GetProp(dto, "PlayMode")?.ToString();

            var heroEnum = AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.EHero");
            var modeEnum = AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.EPlayMode");

            if (!string.IsNullOrEmpty(heroStr) && heroEnum != null && Enum.TryParse(heroEnum, heroStr, true, out var heroVal))
                runCfg.GetType().GetMethod("SetSelectedHero")?.Invoke(runCfg, new[] { heroVal });
            if (!string.IsNullOrEmpty(modeStr) && modeEnum != null && Enum.TryParse(modeEnum, modeStr, true, out var modeVal))
                runCfg.GetType().GetMethod("SetSelectedPlaymode")?.Invoke(runCfg, new[] { modeVal });

            runCfg.GetType().GetMethod("UpdateFromHttpClient", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(runCfg, new[] { dto });

            // UpdateFromHttpClient 不改 HasActiveRun / NumberOfVictories，这里补齐完整同步
            var valProp = runCfg.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            object boxed = valProp?.GetValue(runCfg);
            if (boxed != null)
            {
                var vt = boxed.GetType();
                vt.GetField("HasActiveRun")?.SetValue(boxed, hasActive);
                vt.GetField("NumberOfVictories")?.SetValue(boxed, Convert.ToInt32(GetProp(dto, "Victories") ?? 0));
                valProp.SetValue(runCfg, boxed);
            }
            try
            {
                if (!hasActive)
                {
                    runCfg.GetType().GetMethod("SetTournamentSessionId", BindingFlags.Public | BindingFlags.Instance)?.Invoke(runCfg, new object[] { null });
                    runCfg.GetType().GetMethod("ClearPersistedInitializedRunState", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
                }
                GameEventsBridge.TryTrigger("RunConfigurationUpdated", runCfg);
            }
            catch { }
            CompatAuditPatch.RefreshActiveMainMenuUiFromData();
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("SyncClientRunConfigFromSummary: " + ex.Message);
        }
    }

    private static void ClearClientActiveRunFlag()
    {
        try
        {
            var cacheType = AccessTools.TypeByName("TheBazaar.ClientCache");
            var runCfg = cacheType?.GetField("RunConfig", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (runCfg == null) return;

            var valProp = runCfg.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            object boxed = valProp?.GetValue(runCfg);
            if (boxed != null)
            {
                var vt = boxed.GetType();
                vt.GetField("HasActiveRun")?.SetValue(boxed, false);
                vt.GetField("NumberOfVictories")?.SetValue(boxed, 0);
                vt.GetField("NumberOfDefeats")?.SetValue(boxed, 0);
                valProp.SetValue(runCfg, boxed);
            }
            try
            {
                runCfg.GetType().GetMethod("SetTournamentSessionId", BindingFlags.Public | BindingFlags.Instance)?.Invoke(runCfg, new object[] { null });
                runCfg.GetType().GetMethod("ClearPersistedInitializedRunState", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
                GameEventsBridge.TryTrigger("RunConfigurationUpdated", runCfg);
            }
            catch { }
            CompatAuditPatch.RefreshActiveMainMenuUiFromData();
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("ClearClientActiveRunFlag: " + ex.Message);
        }
    }

    private static object DoResync(JObject req)
    {
        try
        {
            var nmType = AccessTools.TypeByName("Networking.NetworkManager");
            if (nmType == null) return new { ok = false, error = "NetworkManager not found" };

            var nm = GetSingleton(nmType);
            if (nm == null) return new { ok = false, error = "NetworkManager instance not found" };

            var httpClientField = nmType.GetField("_httpClient",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            var httpClient = httpClientField?.GetValue(nm);
            if (httpClient == null) return new { ok = false, error = "_httpClient not found" };

            var reestablish = httpClient.GetType().GetMethod("ReestablishSession",
                BindingFlags.Public | BindingFlags.Instance);
            if (reestablish == null) return new { ok = false, error = "ReestablishSession not found" };

            object task = reestablish.Invoke(httpClient, null);
            _ = (task as Task)?.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    BazaarLauncherPlugin.Log?.LogWarning("resync faulted: " + t.Exception?.GetBaseException().Message);
                else
                    BazaarLauncherPlugin.Log?.LogInfo("resync completed");
            });

            return new { ok = true, op = "resync", via = "HttpGameClient.ReestablishSession", async = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.GetType().Name + ": " + ex.Message };
        }
    }

    private static object GetSingleton(Type t)
    {
        var getInstance = t.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static);
        if (getInstance != null)
        {
            var r = getInstance.Invoke(null, null);
            if (r != null) return r;
        }
        try { return UnityEngine.Object.FindFirstObjectByType(t); } catch { return null; }
    }

    /// <summary>异步状态同步（无主线程 Thread.Sleep 阻塞，修复 P0-1）。</summary>
    internal static async Task<object> DoSyncAsync(JObject req, CancellationToken ct = default)
    {
        try
        {
            bool sent = RunOnMain(() =>
            {
                var cmd = GetCmd();
                if (cmd == null) return false;
                var exitM = cmd.GetType().GetMethod("SendExitCurrentState");
                exitM?.Invoke(cmd, null);
                return true;
            });
            if (!sent) return new { ok = false, error = "Cmd not available" };

            await Task.Delay(400, ct).ConfigureAwait(false);

            return RunOnMain<object>(() =>
            {
                var dataType = AccessTools.TypeByName("TheBazaar.Data");
                var runStateObj = dataType?.GetProperty("CurrentState", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (runStateObj == null) return new { ok = true, op = "sync", note = "no current state" };

                var stateField = runStateObj.GetType().GetField("StateName", BindingFlags.Public | BindingFlags.Instance);
                object targetState = stateField?.GetValue(runStateObj);
                if (targetState == null) return new { ok = true, op = "sync", note = "no state name" };

                var appStateType = AccessTools.TypeByName("TheBazaar.AppState");
                var setState = appStateType?.GetMethod("SetState",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { targetState.GetType() }, null);

                if (setState != null)
                {
                    _ = setState.Invoke(null, new[] { targetState }) as Task;
                }

                return new { ok = true, op = "sync", targetState = targetState.ToString() };
            });
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.GetType().Name + ": " + ex.Message };
        }
    }

    /// <summary>异步强制状态切换（移除主线程 Task.Wait 死锁，修复 P0-1）。</summary>
    internal static async Task<object> DoForceStateAsync(JObject req, CancellationToken ct = default)
    {
        string target = (string)req?["state"];
        if (string.IsNullOrEmpty(target)) return new { ok = false, error = "state required" };

        try
        {
            Task pendingTask = null;
            string error = null;

            RunOnMain(() =>
            {
                try
                {
                    var erunState = AccessTools.TypeByName("BazaarGameShared.Domain.Runs.ERunState");
                    if (erunState == null) { error = "ERunState not found"; return true; }
                    object value = Enum.Parse(erunState, target, ignoreCase: true);

                    var dataType = AccessTools.TypeByName("TheBazaar.Data");
                    var currentState = dataType?.GetProperty("CurrentState", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    if (currentState != null)
                    {
                        var st = currentState.GetType();
                        var stateNameField = st.GetField("StateName", BindingFlags.Public | BindingFlags.Instance);
                        if (stateNameField != null)
                        {
                            stateNameField.SetValue(currentState, value);
                        }
                        else
                        {
                            var prop = st.GetProperty("StateName", BindingFlags.Public | BindingFlags.Instance);
                            if (prop != null && prop.CanWrite) prop.SetValue(currentState, value);
                        }
                    }

                    var appStateType = AccessTools.TypeByName("TheBazaar.AppState");
                    var setState = appStateType?.GetMethod("SetState",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { erunState }, null);
                    if (setState != null)
                    {
                        pendingTask = setState.Invoke(null, new[] { value }) as Task;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                }
                return true;
            });

            if (error != null) return new { ok = false, error };
            if (pendingTask != null)
            {
                await Task.WhenAny(pendingTask, Task.Delay(5000, ct)).ConfigureAwait(false);
            }

            return new { ok = true, op = "forceState", state = target };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.GetType().Name + ": " + ex.Message };
        }
    }

    /// <summary>精准移动与挤位：支持指定 socket/section、数字槽位、自动找空位以及移动前后快照对比。</summary>
    private static object DoMove(JObject req)
    {
        var busyReject = TryRejectIfClientBusy("move", req);
        if (busyReject != null) return busyReject;

        string id = (string)req["instanceId"];
        if (string.IsNullOrEmpty(id)) return new { ok = false, error = "instanceId required" };

        var card = FindCard(id);
        if (card == null) return new { ok = false, error = "card not found: " + id };
        if (!IsItemCard(card, out string actualCardType))
        {
            return new
            {
                ok = false,
                op = "move",
                error = $"cannot move non-ItemCard ({actualCardType})",
                instanceId = id,
                cardType = actualCardType,
            };
        }

        string origSection = GetProp(card, "Section")?.ToString();
        string origSocket = GetProp(card, "LeftSocketId")?.ToString();

        string wantFromSection = (string)req["fromSection"];
        string wantFromSocket = NormalizeSocketName((string)req["fromSocket"]);
        if (!string.IsNullOrWhiteSpace(wantFromSection) &&
            !string.Equals(origSection, wantFromSection, StringComparison.OrdinalIgnoreCase))
        {
            return new { ok = false, error = $"fromSection mismatch: expected {wantFromSection}, actual {origSection}" };
        }
        if (!string.IsNullOrWhiteSpace(wantFromSocket) &&
            !string.Equals(origSocket, wantFromSocket, StringComparison.OrdinalIgnoreCase))
        {
            return new { ok = false, error = $"fromSocket mismatch: expected {wantFromSocket}, actual {origSocket}" };
        }

        string rawSocket = (string)req["socket"];
        string sectionName = (string)req["section"];

        // 兼容 socket="stash" 简写约定
        if (string.Equals(rawSocket, "stash", StringComparison.OrdinalIgnoreCase))
        {
            sectionName = "Stash";
            rawSocket = null;
        }
        else if (string.Equals(rawSocket, "hand", StringComparison.OrdinalIgnoreCase))
        {
            sectionName = "Hand";
            rawSocket = null;
        }

        if (string.IsNullOrWhiteSpace(sectionName))
        {
            sectionName = string.IsNullOrWhiteSpace(origSection) ? "Hand" : origSection;
        }
        var section = Enum.Parse(SectionEnum(), sectionName, ignoreCase: true);

        string socketName = NormalizeSocketName(rawSocket);
        List<object> desiredRawSockets;
        if (string.IsNullOrWhiteSpace(socketName) || string.Equals(socketName, "auto", StringComparison.OrdinalIgnoreCase))
        {
            desiredRawSockets = FindAvailableSocketsForCard(card, section) ?? BuildSocketSpan(card, "Socket_0");
            socketName = desiredRawSockets.Count > 0 ? desiredRawSockets[0].ToString() : "Socket_0";
        }
        else
        {
            desiredRawSockets = BuildSocketSpan(card, socketName);
        }

        var sockets = ConvertList(desiredRawSockets, SocketEnum());
        var beforePositions = SnapshotPlayerItemPositions();

        var cur = GetCurrentAppState();
        var itemCardType = GetItemCardType();
        if (cur != null && itemCardType != null && itemCardType.IsInstanceOfType(card))
        {
            var mMove = cur.GetType().GetMethod("MoveCardCommand",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { itemCardType, typeof(List<>).MakeGenericType(SocketEnum()), SectionEnum() },
                null);
            if (mMove != null)
            {
                mMove.Invoke(cur, new object[] { card, sockets, section });

                string actualSection = GetProp(card, "Section")?.ToString();
                string actualSocket = GetProp(card, "LeftSocketId")?.ToString();
                var displacedToHandIds = new List<string>();
                var displaced = DiffDisplacedCards(beforePositions, id, displacedToHandIds);
                bool moved = !string.Equals(origSection, actualSection, StringComparison.OrdinalIgnoreCase) ||
                             !string.Equals(origSocket, actualSocket, StringComparison.OrdinalIgnoreCase) ||
                             displaced.Count > 0;
                bool alreadyAtTarget = string.Equals(origSection, section.ToString(), StringComparison.OrdinalIgnoreCase) &&
                                       string.Equals(origSocket, socketName, StringComparison.OrdinalIgnoreCase);

                if (!moved && !alreadyAtTarget)
                {
                    return new
                    {
                        ok = false,
                        op = "move",
                        via = "AppState.MoveCardCommand",
                        error = $"Move rejected (card stayed at {actualSection}:{actualSocket}, target was {section}:{socketName})",
                        instanceId = id,
                        fromSection = origSection,
                        fromSocket = origSocket,
                        targetSection = section.ToString(),
                        targetSocket = socketName,
                    };
                }

                // P2-11：关闭态 Stash 移入 Hand 走 movedLocally=true 时，官方 BoardManager.HandleCardMoved 会跳过 SetActive(true)，
                // 致目标 3D 卡永久 activeInHierarchy=false 隐身留空蓝毯；此处对移入 Hand 的主卡及被挤入 Hand 的关联卡强制激活并显形。
                if (string.Equals(actualSection, "Hand", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureMovedHandCardVisible(card);
                }
                foreach (string dispHandId in displacedToHandIds)
                {
                    var dispCard = FindCard(dispHandId);
                    if (dispCard != null) EnsureMovedHandCardVisible(dispCard);
                }

                return new
                {
                    ok = true,
                    op = "move",
                    via = "AppState.MoveCardCommand",
                    instanceId = id,
                    fromSection = origSection,
                    fromSocket = origSocket,
                    targetSection = section.ToString(),
                    targetSocket = socketName,
                    actualSection,
                    actualSocket,
                    alreadyAtTarget,
                    displacedCards = displaced,
                };
            }
        }

        var cmd = GetCmd();
        if (cmd == null) return new { ok = false, error = "Cmd not available" };

        var m = cmd.GetType().GetMethod("SendMoveItem");
        m.Invoke(cmd, new object[] { card, sockets, section, null });
        if (string.Equals(section.ToString(), "Hand", StringComparison.OrdinalIgnoreCase))
        {
            EnsureMovedHandCardVisible(card);
        }
        return new
        {
            ok = true,
            op = "move",
            via = "Cmd.SendMoveItem",
            instanceId = id,
            fromSection = origSection,
            fromSocket = origSocket,
            targetSection = section.ToString(),
            targetSocket = socketName,
        };
    }

    /// <summary>
    /// P2-11：确保移入玩家 Hand 的目标卡牌 3D 对象处于激活且可见状态（SetActive(true) + ShowCard(true)），
    /// 根治关闭态 Stash 直移至 Hand 后永久隐身留空蓝毯的问题。
    /// </summary>
    private static void EnsureMovedHandCardVisible(object card)
    {
        if (card == null) return;
        try
        {
            var lookup = GameInspector.GetDataLookup();
            var ctrl = GetCardControllerSafe(lookup, card);
            if (ctrl == null) return;

            if (ctrl is Component comp && comp.gameObject != null)
            {
                comp.gameObject.SetActive(true);
            }

            var showCardM = ctrl.GetType().GetMethod(
                "ShowCard",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(bool) },
                null);
            showCardM?.Invoke(ctrl, new object[] { true });
        }
        catch (Exception ex)
        {
            Core.DiagLog.SwallowOnce("GameDriver.EnsureMovedHandCardVisible", ex);
        }
    }

    private static string NormalizeSocketName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        if (int.TryParse(s, out int idx)) return "Socket_" + Math.Clamp(idx, 0, 9);
        if (s.StartsWith("Socket_", StringComparison.OrdinalIgnoreCase)) return "Socket_" + s.Substring("Socket_".Length);
        return s;
    }

    private static List<object> FindAvailableSocketsForCard(object card, object sectionEnumVal)
    {
        try
        {
            var util = AccessTools.TypeByName("TheBazaar.CardOperationUtility");
            var m = util?.GetMethod("GetAvailableSockets", BindingFlags.Public | BindingFlags.Static);
            if (m == null) return null;
            if (m.Invoke(null, new[] { card, sectionEnumVal }) is IEnumerable list)
            {
                var res = new List<object>();
                foreach (var item in list) res.Add(item);
                return res.Count > 0 ? res : null;
            }
        }
        catch { }
        return null;
    }

    private static Dictionary<string, (string Section, string Socket)> SnapshotPlayerItemPositions()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var dataType = AccessTools.TypeByName("TheBazaar.Data");
        var entities = dataType?.GetField("Entities", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IDictionary;
        if (entities == null) return map;

        foreach (DictionaryEntry kv in entities)
        {
            var c = kv.Value;
            if (c == null || !IsItemCard(c, out _)) continue;
            var owner = GetProp(c, "Owner");
            string cid = owner == null ? null : GetProp(owner, "CombatantId")?.ToString();
            if (!string.Equals(cid, "Player", StringComparison.OrdinalIgnoreCase)) continue;
            string iid = GetProp(c, "InstanceId")?.ToString();
            if (string.IsNullOrEmpty(iid)) continue;
            map[iid] = (GetProp(c, "Section")?.ToString() ?? "", GetProp(c, "LeftSocketId")?.ToString() ?? "");
        }
        return map;
    }

    private static List<object> DiffDisplacedCards(
        Dictionary<string, (string Section, string Socket)> before,
        string primaryId,
        List<string> movedToHandIds = null)
    {
        var after = SnapshotPlayerItemPositions();
        var list = new List<object>();
        foreach (var kv in after)
        {
            if (string.Equals(kv.Key, primaryId, StringComparison.Ordinal)) continue;
            if (before.TryGetValue(kv.Key, out var prev) &&
                (!string.Equals(prev.Section, kv.Value.Section, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(prev.Socket, kv.Value.Socket, StringComparison.OrdinalIgnoreCase)))
            {
                if (movedToHandIds != null
                    && string.Equals(kv.Value.Section, "Hand", StringComparison.OrdinalIgnoreCase))
                {
                    movedToHandIds.Add(kv.Key);
                }
                list.Add(new
                {
                    instanceId = kv.Key,
                    fromSection = prev.Section,
                    fromSocket = prev.Socket,
                    toSection = kv.Value.Section,
                    toSocket = kv.Value.Socket,
                });
            }
        }
        return list;
    }

    private static object DoCheat(JObject req)
    {
        var args = (req["args"] as JArray)?.Select(t => t.ToString()).ToList();
        if (args == null || args.Count == 0) return new { ok = false, error = "args required" };
        var cmd = GetCmd();
        if (cmd == null) return new { ok = false, error = "Cmd not available" };

        var m = cmd.GetType().GetMethod("SendCheat");
        m.Invoke(cmd, new object[] { args });
        return new { ok = true, op = "cheat", args };
    }

    private static readonly HashSet<string> ActiveRunAppStates = new(StringComparer.Ordinal)
    {
        "ChoiceState",
        "EncounterState",
        "CombatState",
        "PVPCombatState",
        "LootState",
        "LevelUpState",
        "PedestalState",
        "ReplayState",
        "TournamentPvpChoiceState",
    };

    private static Type _cachedGameInstanceType;

    private static Type ResolveGameInstanceType()
    {
        return _cachedGameInstanceType ??=
            AccessTools.TypeByName("TheBazaar.GameInstance")
            ?? AccessTools.TypeByName("TheBazaar.AppFramework.AppSystems.GameInstance");
    }

    private static bool IsNetworkManagerInitialized()
    {
        try
        {
            var nmType = AccessTools.TypeByName("Networking.NetworkManager");
            var nm = nmType?.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            return nm != null && GetProp(nm, "IsInitialized") is bool initd && initd;
        }
        catch { return false; }
    }

    private static void DisposeNetworkManagerIfInitialized()
    {
        try
        {
            var nmType = AccessTools.TypeByName("Networking.NetworkManager");
            var nm = nmType?.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            if (nm != null && GetProp(nm, "IsInitialized") is bool initd && initd)
            {
                nmType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance)?.Invoke(nm, null);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("DisposeNetworkManagerIfInitialized: " + ex.Message);
        }
    }

    private static void ResetAppStateToEmpty()
    {
        try
        {
            string curName = GetAppStateName();
            if (curName is "null" or "EmptyState" or "err") return;
            var appStateType = AccessTools.TypeByName("TheBazaar.AppState");
            appStateType?.GetMethod("Reset", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("ResetAppStateToEmpty: " + ex.Message);
        }
    }

    private static object ReturnToMenuInternal(string opName, bool preferLoadMainMenu = false)
    {
        if (GameInspector.IsSceneTransitioning())
        {
            return new { ok = true, op = opName, needsMenuWait = true, via = "SceneTransitioning" };
        }

        ClearClientActiveRunFlag();

        string activeScene = GameInspector.GetActiveSceneName();
        string curName = GetAppStateName();

        if (GameInspector.IsMainMenuSceneName(activeScene))
        {
            DisposeNetworkManagerIfInitialized();
            ResetAppStateToEmpty();
            return new
            {
                ok = true,
                op = opName,
                needsMenuWait = false,
                via = "AlreadyInMainMenu",
                activeScene,
                appState = GetAppStateName(),
            };
        }

        // 1. 优先走官方 EndOfRunScreenController.ReturnToMenuClicked（等价结算页面点击 Continue 退出并释放场景）
        // 注意：ReturnToMenuClicked 内部调用 RunManager.LoadMainMenu()，不会主动 Reset AppState，因此需补一次 ResetAppStateToEmpty()
        var eorType = AccessTools.TypeByName("TheBazaar.UI.EndOfRun.EndOfRunScreenController");
        if (eorType != null)
        {
            var eor = UnityEngine.Object.FindFirstObjectByType(eorType);
            if (eor != null)
            {
                var mRet = eorType.GetMethod("ReturnToMenuClicked",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mRet != null)
                {
                    try
                    {
                        mRet.Invoke(eor, null);
                        ResetAppStateToEmpty();
                        BazaarLauncherPlugin.Log.LogInfo($"{opName}: EndOfRunScreenController.ReturnToMenuClicked invoked");
                        return new
                        {
                            ok = true,
                            op = opName,
                            needsMenuWait = true,
                            via = "EndOfRunScreenController.ReturnToMenuClicked",
                        };
                    }
                    catch (Exception ex)
                    {
                        BazaarLauncherPlugin.Log.LogWarning("ReturnToMenuClicked failed: " + ex.Message);
                    }
                }
            }
        }

        // 2. 退回到 RunManager（RunManager 位于全局命名空间）
        // 从 EndRun* 或已经走过 HTTP concede / 未联网时，优先走同步切场景的 LoadMainMenu + ResetAppStateToEmpty，
        // 避免 ReturnToMainMenu 内部 DeleteSessionAsync 抛异常导致 MoveToScene 不执行。
        var rmType = AccessTools.TypeByName("RunManager") ?? AccessTools.TypeByName("TheBazaar.RunManager");
        if (rmType != null)
        {
            var rm = GetSingleton(rmType);
            if (rm != null)
            {
                var mLoad = rmType.GetMethod("LoadMainMenu", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var mRet = rmType.GetMethod("ReturnToMainMenu", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                bool useLoadFirst = preferLoadMainMenu
                                    || curName is "EndRunVictoryState" or "EndRunDefeatState" or "null" or "EmptyState"
                                    || !IsNetworkManagerInitialized();
                var targetMethod = useLoadFirst ? (mLoad ?? mRet) : (mRet ?? mLoad);
                if (targetMethod != null)
                {
                    try
                    {
                        if (targetMethod == mLoad)
                        {
                            DisposeNetworkManagerIfInitialized();
                            ResetAppStateToEmpty();
                        }
                        targetMethod.Invoke(rm, null);
                        BazaarLauncherPlugin.Log.LogInfo($"{opName}: RunManager.{targetMethod.Name} dispatched");
                        return new
                        {
                            ok = true,
                            op = opName,
                            needsMenuWait = true,
                            via = "RunManager." + targetMethod.Name,
                        };
                    }
                    catch (Exception ex)
                    {
                        BazaarLauncherPlugin.Log.LogWarning($"RunManager.{targetMethod.Name} failed: " + ex.Message);
                    }
                }
            }
        }

        var giType = ResolveGameInstanceType();
        var gi = giType == null ? null : UnityEngine.Object.FindFirstObjectByType(giType);
        if (gi != null)
        {
            DisposeNetworkManagerIfInitialized();
            ResetAppStateToEmpty();
            var goBack = giType.GetMethod("GoBackToMainMenu", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            goBack?.Invoke(gi, null);
            return new { ok = true, op = opName, needsMenuWait = true, via = "GameInstance.GoBackToMainMenu" };
        }

        return new { ok = false, op = opName, error = "no menu return handler found" };
    }

    private static object DoStartRun(JObject req)
    {
        string heroName = (string)req["hero"] ?? "Vanessa";
        string modeName = (string)req["playMode"] ?? "Unranked";
        bool force = (bool?)req["force"] ?? false;

        var cacheType = AccessTools.TypeByName("TheBazaar.ClientCache");
        var runConfig = cacheType?.GetField("RunConfig", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (runConfig == null) return new { ok = false, error = "RunConfig not available" };

        var heroEnum = AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.EHero");
        var modeEnum = AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.EPlayMode");
        if (heroEnum == null || modeEnum == null) return new { ok = false, error = "enums not found" };

        object hero;
        object mode;
        try
        {
            hero = Enum.Parse(heroEnum, heroName, ignoreCase: true);
            mode = Enum.Parse(modeEnum, modeName, ignoreCase: true);
        }
        catch (Exception ex)
        {
            return new { ok = false, op = "startRun", hero = heroName, errorCode = "INVALID_ARG", error = ex.Message };
        }

        var giType = ResolveGameInstanceType();
        if (giType == null) return new { ok = false, error = "GameInstance type not found" };
        var gi = UnityEngine.Object.FindFirstObjectByType(giType);
        if (gi == null) return new { ok = false, error = "GameInstance instance not found" };

        string curName = GetAppStateName();
        string activeScene = GameInspector.GetActiveSceneName();
        bool inGameplayScene = string.Equals(activeScene, "Gameplay", StringComparison.OrdinalIgnoreCase);

        // 1. 局内真实状态防重入：绝不强行再调用 StartNewRun() 引发 Server session is already initialized 与场景双实例 NRE
        if (inGameplayScene && ActiveRunAppStates.Contains(curName))
        {
            if (force)
            {
                var retRes = ReturnToMenuInternal("startRun", preferLoadMainMenu: false);
                return new
                {
                    ok = true,
                    op = "startRun",
                    hero = heroName,
                    playMode = modeName,
                    resetFrom = curName,
                    needsMenuWait = true,
                    detail = retRes,
                };
            }

            return new
            {
                ok = true,
                op = "startRun",
                alreadyInRun = true,
                appState = curName,
                activeScene,
            };
        }

        // 2. 正在开局初始化状态（StartRunAppState / StartTournamentSpectateAppState）：同样禁止重复 StartNewRun
        if (curName is "StartRunAppState" or "StartTournamentSpectateAppState")
        {
            if (force)
            {
                var retRes = ReturnToMenuInternal("startRun", preferLoadMainMenu: true);
                return new
                {
                    ok = true,
                    op = "startRun",
                    hero = heroName,
                    playMode = modeName,
                    resetFrom = curName,
                    needsMenuWait = true,
                    detail = retRes,
                };
            }

            return new
            {
                ok = true,
                op = "startRun",
                alreadyInRun = true,
                appState = curName,
                activeScene,
            };
        }

        // 3. Gameplay 场景下但处于 null / EmptyState（软锁态），或结算态（EndRunVictoryState / EndRunDefeatState）
        bool isSoftLockedGameplay = inGameplayScene && curName is "null" or "EmptyState" or "err";
        bool isEndRun = curName is "EndRunVictoryState" or "EndRunDefeatState"
                        || string.Equals(activeScene, "EndOfRun", StringComparison.OrdinalIgnoreCase);
        if (isSoftLockedGameplay || isEndRun)
        {
            var retRes = ReturnToMenuInternal("startRun", preferLoadMainMenu: true);
            return new
            {
                ok = true,
                op = "startRun",
                hero = heroName,
                playMode = modeName,
                resetFrom = isSoftLockedGameplay ? $"Gameplay:{curName}" : curName,
                needsMenuWait = true,
                detail = retRes,
            };
        }

        if (GameInspector.IsSceneTransitioning())
        {
            return new
            {
                ok = true,
                op = "startRun",
                hero = heroName,
                playMode = modeName,
                needsMenuWait = true,
                via = "SceneTransitioning",
            };
        }

        // 若已有残留 NetworkManager 会话，先释放避免 StartNewRun 拦截
        DisposeNetworkManagerIfInitialized();

        runConfig.GetType().GetMethod("SetSelectedHero")?.Invoke(runConfig, new[] { hero });
        runConfig.GetType().GetMethod("SetSelectedPlaymode")?.Invoke(runConfig, new[] { mode });

        // 校验客户端是否真正接受所选英雄（防止 Stelle/Jules 受限时静默回落成别的英雄）
        string selectedAfter = GetProp(GetProp(runConfig, "Value"), "Hero")?.ToString();
        if (!string.IsNullOrEmpty(selectedAfter) &&
            !string.Equals(selectedAfter, hero.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                ok = false,
                op = "startRun",
                hero = heroName,
                heroRestricted = true,
                actualSelectedHero = selectedAfter,
                error = $"Hero {heroName} rejected by client RunConfig (actual={selectedAfter})",
            };
        }

        var hmType = AccessTools.TypeByName("TheBazaar.HeroManager");
        var hm = hmType == null ? null : UnityEngine.Object.FindFirstObjectByType(hmType);
        var heroSoType = AccessTools.TypeByName("TheBazaar.HeroSO");
        if (hm != null && heroSoType != null && GetProp(hm, "_initialized") is bool hmInit && hmInit)
        {
            var mTryGet = hmType.GetMethod("TryGetHeroSO", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { heroEnum, heroSoType.MakeByRefType() }, null);
            if (mTryGet != null)
            {
                var args = new object[] { hero, null };
                if (mTryGet.Invoke(hm, args) is bool hasHeroSo && !hasHeroSo)
                {
                    return new
                    {
                        ok = false,
                        op = "startRun",
                        hero = heroName,
                        heroRestricted = true,
                        error = $"Hero {heroName} has no HeroSO data in client HeroManager",
                    };
                }
            }
        }

        var startNewRun = giType.GetMethod("StartNewRun",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (startNewRun == null) return new { ok = false, error = "StartNewRun not found" };

        startNewRun.Invoke(gi, null);
        return new { ok = true, op = "startRun", hero = heroName, playMode = modeName, via = "StartNewRun" };
    }

    // ── 反射小工具 ──────────────────────────────────────────────────────────

    private static object GetCmd() => GameInspector.GetCmdCached();

    private static List<string> GetSelectionIds()
    {
        var state = GameInspector.GetDataCurrentState();
        var sel = GetProp(state, "SelectionSet") as IEnumerable;
        if (sel == null) return new List<string>();
        var list = new List<string>();
        foreach (var o in sel)
        {
            string s = o?.ToString();
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return list;
    }

    private static string FirstSelectionId()
    {
        var list = GetSelectionIds();
        return list.Count > 0 ? list[0] : null;
    }

    private static object FindCard(string instanceId)
    {
        var entities = GameInspector.GetDataEntities();
        if (entities == null) return null;
        foreach (DictionaryEntry kv in entities)
        {
            var card = kv.Value;
            if (card == null) continue;
            if (GetProp(card, "InstanceId")?.ToString() == instanceId) return card;
        }
        return null;
    }

    private static Type _itemCardType;

    private static Type GetItemCardType()
        => _itemCardType ??= GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.ItemCard");

    private static bool IsItemCard(object card, out string actualType)
    {
        actualType = card?.GetType().Name ?? "null";
        if (card == null) return false;
        var itemCardType = GetItemCardType();
        if (itemCardType != null) return itemCardType.IsInstanceOfType(card);
        return string.Equals(actualType, "ItemCard", StringComparison.Ordinal);
    }

    private static object MakeInstanceId(string value)
    {
        var t = AccessTools.TypeByName("BazaarGameShared.Domain.Core.InstanceId");
        if (t == null) return value;
        var ctor = t.GetConstructor(new[] { typeof(string) });
        return ctor != null ? ctor.Invoke(new object[] { value }) : Activator.CreateInstance(t, value);
    }

    private static Type SocketEnum() => AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.EContainerSocketId");
    private static Type SectionEnum() => AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.EInventorySection");

    private static object ConvertList(List<object> items, Type elemType)
    {
        var listType = typeof(List<>).MakeGenericType(elemType);
        var list = (IList)Activator.CreateInstance(listType);
        foreach (var i in items) list.Add(i);
        return list;
    }

    // ── 英雄切换 / 换装弹窗 / 3D 开箱场景共享辅助与 QA 钩子 ─────────────────

    internal static object ResolveGameService(string fullTypeName)
    {
        var t = AccessTools.TypeByName(fullTypeName);
        if (t == null) return null;
        try
        {
            var servicesType = AccessTools.TypeByName("TheBazaar.AppFramework.Services");
            var map = servicesType?.GetField("_serviceMap", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IDictionary;
            if (map != null && map.Contains(t))
            {
                var s = map[t];
                if (s != null && (s is not UnityEngine.Object uo || uo != null)) return s;
            }
        }
        catch { }
        try
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(t))
            {
                return UnityEngine.Object.FindFirstObjectByType(t);
            }
        }
        catch { }
        return null;
    }

    internal static int ReadClientBundledChestCount()
    {
        try
        {
            var cacheType = AccessTools.TypeByName("TheBazaar.ClientCache");
            var chestsObs = cacheType?.GetField("Chests", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var chestsVal = chestsObs?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(chestsObs);
            if (chestsVal == null) return -1;
            var countObj = GetProp(chestsVal, "bundledUnopenedCount");
            return countObj != null ? Convert.ToInt32(countObj) : -1;
        }
        catch { return -1; }
    }

    internal static object GetCosmeticsQaSnapshot()
    {
        var cacheType = AccessTools.TypeByName("TheBazaar.ClientCache");
        var runCfg = cacheType?.GetField("RunConfig", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var runCfgVal = GetProp(runCfg, "Value");
        string selectedHero = GetProp(runCfgVal, "Hero")?.ToString() ?? "";

        var itemsObs = cacheType?.GetField("CollectionItems", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var itemsArr = GetProp(itemsObs, "Value") as Array;

        var ownedObs = cacheType?.GetField("OwnedHeroes", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var ownedArr = GetProp(ownedObs, "Value") as IEnumerable;
        var ownedList = new List<string>();
        if (ownedArr != null)
        {
            foreach (var h in ownedArr)
            {
                string hid = GetProp(h, "heroId")?.ToString();
                if (!string.IsNullOrEmpty(hid)) ownedList.Add(hid);
            }
        }

        var loadoutsObs = cacheType?.GetField("HeroLoadouts", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var loadoutsVal = GetProp(loadoutsObs, "Value");

        var cm = ResolveGameService("TheBazaar.CollectionManager");
        object currentLoadout = null;
        if (cm != null && !string.IsNullOrEmpty(selectedHero))
        {
            var mGetLoadout = cm.GetType().GetMethod("GetHeroLoadout", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            currentLoadout = mGetLoadout?.Invoke(cm, new object[] { selectedHero });
        }

        var walletObs = cacheType?.GetField("Wallet", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var walletVal = GetProp(walletObs, "Value");
        var seasonObs = cacheType?.GetField("CurrentSeason", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var seasonVal = GetProp(seasonObs, "Value");

        var chestMgr = ResolveGameService("TheBazaar.Feature.Chest.Scene.ChestManager");
        int chestTotal = -1;
        if (chestMgr != null)
        {
            var mTotal = chestMgr.GetType().GetMethod("GetTotalChestsCount", BindingFlags.Public | BindingFlags.Instance);
            if (mTotal?.Invoke(chestMgr, null) is int c) chestTotal = c;
        }

        return new
        {
            ok = true,
            activeScene = GameInspector.GetActiveSceneName(),
            appState = GetAppStateName(),
            hasActiveRun = GameInspector.ReadHasActiveRunFlag(),
            selectedHero,
            ownedHeroes = ownedList,
            collectionItemCount = itemsArr?.Length ?? 0,
            bundledUnopenedCount = ReadClientBundledChestCount(),
            chestInventoryTotal = chestTotal,
            currentSeasonId = GetProp(seasonVal, "id"),
            gems = GetProp(walletVal, "Gems"),
            currentHeroLoadout = currentLoadout,
            heroLoadouts = loadoutsVal,
        };
    }

    internal static object SwitchHeroInMainMenu(string heroName)
    {
        if (string.IsNullOrWhiteSpace(heroName) ||
            !Enum.TryParse<EHero>(heroName.Trim(), true, out var hero) ||
            hero == EHero.Common)
        {
            return new { ok = false, op = "switchHero", error = "invalid playable hero: " + heroName };
        }

        // 禁用随机英雄模式并更新 RunConfig / HeroManager / CollectionManager / 主菜单展示
        try
        {
            var prefsType = AccessTools.TypeByName("PlayerPreferences");
            var prefsData = prefsType?.GetProperty("Data", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            prefsData?.GetType().GetProperty("RandomHeroEnabled", BindingFlags.Public | BindingFlags.Instance)?.SetValue(prefsData, false);
        }
        catch { }

        var cacheType = AccessTools.TypeByName("TheBazaar.ClientCache");
        var runCfg = cacheType?.GetField("RunConfig", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        runCfg?.GetType().GetMethod("SetSelectedHero", BindingFlags.Public | BindingFlags.Instance)?.Invoke(runCfg, new object[] { hero });
        runCfg?.GetType().GetMethod("SaveSelectedHero", BindingFlags.Public | BindingFlags.Instance)?.Invoke(runCfg, null);

        var hm = ResolveGameService("TheBazaar.HeroManager");
        hm?.GetType().GetMethod("SelectHero", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(EHero) }, null)
            ?.Invoke(hm, new object[] { hero });

        var cm = ResolveGameService("TheBazaar.CollectionManager");
        cm?.GetType().GetMethod("SetCurrentHero", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(EHero) }, null)
            ?.Invoke(cm, new object[] { hero });

        try
        {
            GameEventsBridge.TryTrigger("HeroChanged", hero);
        }
        catch { }

        var viewType = AccessTools.TypeByName("TheBazaar.UI.MainMenu.MainMenuBaseView");
        var view = viewType == null ? null : UnityEngine.Object.FindFirstObjectByType(viewType);
        viewType?.GetMethod("ForceFullUpdate", BindingFlags.Public | BindingFlags.Instance)?.Invoke(view, null);

        return new
        {
            ok = true,
            op = "switchHero",
            hero = hero.ToString(),
            hasActiveRun = GameInspector.ReadHasActiveRunFlag(),
            activeScene = GameInspector.GetActiveSceneName(),
        };
    }

    internal static object OpenMainMenuPopup(string popupMemberName, string opName)
    {
        var mmcType = AccessTools.TypeByName("TheBazaar.UI.Menu.MainMenuController");
        var mmc = mmcType == null ? null : UnityEngine.Object.FindFirstObjectByType(mmcType);
        if (mmc == null)
        {
            return new { ok = false, op = opName, error = "MainMenuController not found in active scene: " + GameInspector.GetActiveSceneName() };
        }

        if (string.Equals(popupMemberName, "Loadout", StringComparison.Ordinal))
        {
            try
            {
                var prefsType = AccessTools.TypeByName("PlayerPreferences");
                var prefsData = prefsType?.GetProperty("Data", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                prefsData?.GetType().GetProperty("RandomHeroEnabled", BindingFlags.Public | BindingFlags.Instance)?.SetValue(prefsData, false);
            }
            catch { }
        }

        var popupEnum = AccessTools.TypeByName("TheBazaar.UI.Menu.EMainMenuPopup");
        if (popupEnum == null) return new { ok = false, op = opName, error = "EMainMenuPopup not found" };
        object enumVal = Enum.Parse(popupEnum, popupMemberName, true);
        mmcType.GetMethod("RequestPopup", BindingFlags.Public | BindingFlags.Instance, null, new[] { popupEnum }, null)
            ?.Invoke(mmc, new[] { enumVal });

        return new { ok = true, op = opName, popup = popupMemberName, activeScene = GameInspector.GetActiveSceneName() };
    }

    internal static object OpenLoadoutPopup() => OpenMainMenuPopup("Loadout", "openLoadout");
    internal static object OpenHeroSelectPopup() => OpenMainMenuPopup("HeroSelect", "openHeroSelect");

    internal static object TriggerSceneChange(string sceneIdName, string opName)
    {
        var sceneEnum = AccessTools.TypeByName("SceneID") ?? AccessTools.TypeByName("TheBazaar.SceneID");
        if (sceneEnum == null) return new { ok = false, op = opName, error = "SceneID enum not found" };
        object targetScene = Enum.Parse(sceneEnum, sceneIdName, true);

        var optType = AccessTools.TypeByName("TheBazaar.UI.Menu.SceneSelectOptionContainer");
        var mHandle = optType?.GetMethod("HandleSceneChange", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (mHandle != null)
        {
            mHandle.Invoke(null, new[] { targetScene, null });
            return new { ok = true, op = opName, targetScene = sceneIdName, via = "SceneSelectOptionContainer.HandleSceneChange" };
        }

        var loaderType = AccessTools.TypeByName("SceneLoader");
        // SceneLoader.LoadScene 真实签名为 6 参静/异步方法（后 5 参带默认值），反射查找需精确匹配参数列表
        var mLoad6 = loaderType?.GetMethod(
            "LoadScene",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { sceneEnum, typeof(bool), typeof(bool), typeof(Action), typeof(Action), typeof(CancellationToken) },
            null);
        if (mLoad6 != null)
        {
            mLoad6.Invoke(null, new object[] { targetScene, true, true, null, null, CancellationToken.None });
            return new { ok = true, op = opName, targetScene = sceneIdName, via = "SceneLoader.LoadScene(6)" };
        }
        var mLoad1 = loaderType?.GetMethod("LoadScene", BindingFlags.Public | BindingFlags.Static, null, new[] { sceneEnum }, null);
        if (mLoad1 != null)
        {
            mLoad1.Invoke(null, new[] { targetScene });
            return new { ok = true, op = opName, targetScene = sceneIdName, via = "SceneLoader.LoadScene(1)" };
        }
        return new { ok = false, op = opName, error = "scene navigation handler not found" };
    }

    internal static object OpenChestScene() => TriggerSceneChange("ChestScene", "openChestScene");
    internal static object ReturnToHeroSelectScene() => TriggerSceneChange("HeroSelectScene", "returnHome");

    internal static async Task<(bool ok, int chestCount, string error)> GrantChestsAndRefreshAsync(int count = 30)
    {
        await NameAuth.EnsureAccountIdentityAsync().ConfigureAwait(false);
        var resp = await NameAuth.PostJsonAsync("/api/Chests/grant", new { count = Math.Clamp(count, 1, 200) }).ConfigureAwait(false);
        if (!resp.Ok)
        {
            return (false, -1, string.IsNullOrEmpty(resp.Error) ? $"HTTP {resp.StatusCode}" : resp.Error);
        }

        Task refreshTask = RunOnMain(() =>
        {
            var chestMgr = ResolveGameService("TheBazaar.Feature.Chest.Scene.ChestManager");
            var mGet = chestMgr?.GetType().GetMethod("GetUnopenedChests", BindingFlags.Public | BindingFlags.Instance);
            return mGet?.Invoke(chestMgr, new object[] { true }) as Task;
        });
        if (refreshTask != null)
        {
            try { await refreshTask.ConfigureAwait(false); } catch { }
        }

        int finalCount = RunOnMain(ReadClientBundledChestCount);
        return (true, finalCount, "");
    }

    private static async Task<object> HandleCosmeticsQaAsync(JObject req)
    {
        string op = ((string)req["op"] ?? "state").Trim().ToLowerInvariant();
        switch (op)
        {
            case "state":
                return RunOnMain(GetCosmeticsQaSnapshot);
            case "switchhero":
            case "switch_hero":
                return RunOnMain(() => SwitchHeroInMainMenu((string)req["hero"]));
            case "openloadout":
            case "open_loadout":
                return RunOnMain(OpenLoadoutPopup);
            case "openheroselect":
            case "open_hero_select":
                return RunOnMain(OpenHeroSelectPopup);
            case "openchestscene":
            case "open_chest_scene":
                return RunOnMain(OpenChestScene);
            case "returnhome":
            case "return_home":
                return RunOnMain(ReturnToHeroSelectScene);
            case "grantchests":
            case "grant_chests":
            {
                int cnt = (int?)req["count"] ?? 30;
                var r = await GrantChestsAndRefreshAsync(cnt).ConfigureAwait(false);
                return new { ok = r.ok, op = "grantChests", requested = cnt, bundledUnopenedCount = r.chestCount, error = string.IsNullOrEmpty(r.error) ? null : r.error };
            }
            case "openchests":
            case "open_chests":
            {
                int cnt = Math.Clamp((int?)req["count"] ?? 1, 1, 10);
                int seasonId = (int?)req["seasonId"] ?? 2;
                Task openTask = RunOnMain(() =>
                {
                    var chestMgr = ResolveGameService("TheBazaar.Feature.Chest.Scene.ChestManager");
                    var mOpen = chestMgr?.GetType().GetMethod("OpenChests", BindingFlags.Public | BindingFlags.Instance);
                    return mOpen?.Invoke(chestMgr, new object[] { seasonId, cnt }) as Task;
                });
                if (openTask == null) return new { ok = false, op = "openChests", error = "ChestManager.OpenChests unavailable" };
                await openTask.ConfigureAwait(false);
                var rewards = openTask.GetType().GetProperty("Result")?.GetValue(openTask);
                int remaining = RunOnMain(ReadClientBundledChestCount);
                return new { ok = rewards != null, op = "openChests", seasonId, count = cnt, bundledUnopenedCount = remaining, rewards };
            }
            case "equip":
            case "equipcosmetic":
            case "equip_cosmetic":
            {
                string heroId = (string)req["hero"] ?? "Vanessa";
                string cid = (string)req["collectibleId"] ?? "";
                if (string.IsNullOrWhiteSpace(cid)) return new { ok = false, op = "equipCosmetic", error = "collectibleId required" };
                await NameAuth.EnsureAccountIdentityAsync().ConfigureAwait(false);
                var resp = await NameAuth.PostJsonAsync($"/api/Loadouts/hero/{Uri.EscapeDataString(heroId)}/equip/{Uri.EscapeDataString(cid)}", null).ConfigureAwait(false);
                if (!resp.Ok) return new { ok = false, op = "equipCosmetic", statusCode = resp.StatusCode, error = resp.Error };
                bool updatedDirectly = RunOnMain(() =>
                {
                    try
                    {
                        var cm = ResolveGameService("TheBazaar.CollectionManager");
                        var loadoutType = AccessTools.TypeByName("BazaarGameShared.TempoNet.Models.BazaarHeroLoadout");
                        var mUpdate = cm != null && loadoutType != null
                            ? AccessTools.Method(cm.GetType(), "UpdateHeroLoadout", new[] { loadoutType })
                            : null;
                        if (mUpdate != null)
                        {
                            object loadoutObj = JsonConvert.DeserializeObject(resp.Body, loadoutType);
                            if (loadoutObj != null)
                            {
                                mUpdate.Invoke(cm, new[] { loadoutObj });
                                return true;
                            }
                        }
                    }
                    catch { }
                    return false;
                });
                if (!updatedDirectly)
                {
                    Task refreshTask = RunOnMain(() =>
                    {
                        var cm = ResolveGameService("TheBazaar.CollectionManager");
                        var mRefresh = cm?.GetType().GetMethod("RefreshHeroLoadout", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                        return mRefresh?.Invoke(cm, new object[] { heroId }) as Task;
                    });
                    if (refreshTask != null) { try { await refreshTask.ConfigureAwait(false); } catch { } }
                }
                RunOnMain(() =>
                {
                    try
                    {
                        var cm = ResolveGameService("TheBazaar.CollectionManager");
                        var onUpd = cm?.GetType().GetField("OnLoadoutUpdated", BindingFlags.Public | BindingFlags.Instance)?.GetValue(cm) as Action;
                        onUpd?.Invoke();
                        GameEventsBridge.TryTrigger("HeroSkinChanged", new object[] { null });
                        if (Enum.TryParse<EHero>(heroId, true, out var parsedHero))
                        {
                            GameEventsBridge.TryTrigger("HeroLoadoutChanged", parsedHero);
                        }
                    }
                    catch { }
                    return true;
                });
                return new { ok = true, op = "equipCosmetic", hero = heroId, collectibleId = cid, loadout = JToken.Parse(resp.Body) };
            }
            default:
                return new { ok = false, error = "unknown cosmetics-qa op: " + op };
        }
    }

    // ── 截图 ────────────────────────────────────────────────────────────────

    private static byte[] CaptureScreenshot(double scale)
    {
        Texture2D tex = null;
        Texture2D outTex = null;
        RenderTexture rt = null;
        RenderTexture prev = null;
        bool rtBound = false;
        try
        {
            tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex == null) return null;

            outTex = tex;
            if (scale < 0.999)
            {
                int w = Math.Max(1, (int)(tex.width * scale));
                int h = Math.Max(1, (int)(tex.height * scale));
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                prev = RenderTexture.active;
                RenderTexture.active = rt;
                rtBound = true;
                outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                outTex.Apply();
            }

            return outTex.EncodeToPNG();
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogError("screenshot: " + ex);
            return null;
        }
        finally
        {
            // D4-P2-6：无论是否抛异常均严格恢复 RenderTexture.active 并释放临时 RT 与 Texture2D，杜绝 GPU VRAM 泄漏
            if (rtBound) RenderTexture.active = prev;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (outTex != null && outTex != tex) UnityEngine.Object.Destroy(outTex);
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }
    }

    internal static void LogLine(string line)
    {
        _logRing.Enqueue(line);
        while (_logRing.Count > 500) _logRing.TryDequeue(out _);
    }
}
