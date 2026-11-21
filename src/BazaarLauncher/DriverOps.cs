using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarLauncher;

/// <summary>
/// 批量操作 + 条件等待 + 事件记录（修复 P0-1 主线程死锁）：
/// 所有批次编排与条件等待均在后台工作线程异步执行（await Task.Delay），
/// 仅将单条原子操作通过 GameDriver.RunOnMain 派发至主线程，严禁在 Unity 主线程调用 Thread.Sleep 或 .Wait()。
/// </summary>
internal static class DriverOps
{
    private static readonly ConcurrentQueue<EventRecord> _events = new();
    private static int _eventSeq;

    internal sealed class EventRecord
    {
        public int Seq;
        public string Type;
        public DateTime At;
        public string Detail;
    }

    internal static void RecordEvent(string type, string detail)
    {
        _events.Enqueue(new EventRecord
        {
            Seq = Interlocked.Increment(ref _eventSeq),
            Type = type,
            At = DateTime.UtcNow,
            Detail = detail,
        });
        while (_events.Count > 2000) _events.TryDequeue(out _);
    }

    internal static List<EventRecord> Snapshot(int sinceSeq = 0)
        => _events.Where(e => e.Seq > sinceSeq).ToList();

    internal static int LastSeq => _eventSeq;

    /// <summary>
    /// 异步批量执行入口（HTTP 工作线程调用，0 主线程阻塞）。
    /// </summary>
    internal static async Task<object> RunBatchAsync(JObject req, CancellationToken ct = default)
    {
        var ops = req?["ops"] as JArray;
        if (ops == null || ops.Count == 0)
            return new { ok = false, error = "ops required" };

        int settleMs = Math.Clamp((int?)req["settleMs"] ?? 120, 0, 5000);
        bool stopOnError = (bool?)req["stopOnError"] ?? true;

        var log = new List<object>();
        var started = DateTime.UtcNow;

        foreach (var item in ops)
        {
            if (ct.IsCancellationRequested)
                return Fail(log, "batch cancelled", started);

            var o = item as JObject;
            if (o == null) continue;

            if (o["wait"] != null)
            {
                var waitSpec = o["wait"] as JObject ?? new JObject();
                int timeout = Math.Clamp((int?)o["timeoutMs"] ?? 10000, 100, 60000);
                var waitResult = await WaitForAsync(waitSpec, timeout, ct).ConfigureAwait(false);
                log.Add(new
                {
                    kind = "wait",
                    spec = waitSpec.ToString(Formatting.None),
                    ok = waitResult.ok,
                    elapsedMs = waitResult.elapsedMs,
                    error = waitResult.error,
                });
                if (!waitResult.ok && stopOnError)
                    return Fail(log, "wait timeout: " + waitResult.error, started);
                continue;
            }

            string op = (string)o["op"] ?? string.Empty;
            if (string.IsNullOrEmpty(op)) continue;

            object result = await ExecuteOneAsync(op, o, ct).ConfigureAwait(false);
            log.Add(new { kind = "op", op, ok = GetOk(result), result });

            if (!GetOk(result) && stopOnError)
                return Fail(log, "op failed: " + op, started);

            if (settleMs > 0)
            {
                await Task.Delay(settleMs, ct).ConfigureAwait(false);
            }
        }

        return new
        {
            ok = true,
            opCount = log.Count,
            elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            log,
        };
    }

    internal static async Task<object> ExecuteOneAsync(string op, JObject req, CancellationToken ct = default)
    {
        string lower = (op ?? string.Empty).Trim().ToLowerInvariant();
        if (lower == "resume")
        {
            return await GameDriver.DoResumeAsync(req).ConfigureAwait(false);
        }
        if (lower == "concede")
        {
            return await GameDriver.DoConcedeAsync(req).ConfigureAwait(false);
        }
        if (lower == "sync")
        {
            return await GameDriver.DoSyncAsync(req, ct).ConfigureAwait(false);
        }
        if (lower is "forcestate" or "force_state")
        {
            return await GameDriver.DoForceStateAsync(req, ct).ConfigureAwait(false);
        }
        if (lower == "wait")
        {
            var spec = req["wait"] as JObject ?? req;
            int timeout = Math.Clamp((int?)req["timeoutMs"] ?? 10000, 100, 60000);
            var w = await WaitForAsync(spec, timeout, ct).ConfigureAwait(false);
            return new { ok = w.ok, op = "wait", elapsedMs = w.elapsedMs, error = w.error };
        }

        return GameDriver.RunOnMain(() => ExecuteOneOnMain(lower, req));
    }

    /// <summary>供单步主线程派发的安全原子命令执行（严禁含 Sleep/Wait）。</summary>
    internal static object ExecuteOneOnMain(string opLower, JObject req)
    {
        return opLower switch
        {
            "buy" => GameDriver.DoBuyPublic(req),
            "sell" => GameDriver.DoSellPublic(req),
            "selectskill" => GameDriver.DoSelectSkillPublic(req),
            "selectencounter" => GameDriver.DoSelectEncounterPublic(req),
            "encounterstep" or "encounter_step" => GameDriver.DoEncounterStepPublic(req),
            "reroll" => GameDriver.DoSimplePublic("SendReRollSelection", "reroll"),
            "exit" => GameDriver.DoSimplePublic("SendExitCurrentState", "exit"),
            "abandon" => GameDriver.DoSimplePublic("SendAbandonRun", "abandon"),
            "move" => GameDriver.DoMovePublic(req),
            "inspect" => GameDriver.DoInspectPublic(),
            "cheat" => GameDriver.DoCheatPublic(req),
            "startrun" => GameDriver.DoStartRunPublic(req),
            "state" => new { ok = true, state = GameDriver.ReadStatePublic() },
            _ => new { ok = false, error = "unknown op: " + opLower },
        };
    }

    /// <summary>异步条件等待（仅在后台轮询并短切主线程读取状态，0 主线程冻结）。</summary>
    internal static async Task<(bool ok, int elapsedMs, string error)> WaitForAsync(
        JObject spec, int timeoutMs, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string lastReason = string.Empty;

        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            var st = GameDriver.RunOnMain(() => GameDriver.ReadStatePublic() as IDictionary<string, object>);
            if (st != null)
            {
                bool all = true;
                lastReason = string.Empty;

                foreach (var kv in spec)
                {
                    string key = kv.Key;
                    var want = kv.Value;

                    switch (key)
                    {
                        case "appState":
                            if (!StrEq(st, "appState", want?.ToString())) { all = false; lastReason = "appState"; }
                            break;
                        case "runState":
                            if (!StrEq(st, "runState", want?.ToString())) { all = false; lastReason = "runState"; }
                            break;
                        case "minCards":
                            if (IntOf(st, "cardCount") < want.Value<int>()) { all = false; lastReason = "cardCount"; }
                            break;
                        case "minSelection":
                            if (SelectionCount(st) < want.Value<int>()) { all = false; lastReason = "selectionCount"; }
                            break;
                        case "selectionType":
                            if (!StrEq(st, "selectionFirstType", want?.ToString())) { all = false; lastReason = "selectionFirstType"; }
                            break;
                        case "minGold":
                            if (IntOf(st, "gold") < want.Value<int>()) { all = false; lastReason = "gold"; }
                            break;
                        case "maxGold":
                            if (IntOf(st, "gold") > want.Value<int>()) { all = false; lastReason = "gold"; }
                            break;
                        case "goldEquals":
                            if (IntOf(st, "gold") != want.Value<int>()) { all = false; lastReason = "gold"; }
                            break;
                        case "dayEquals":
                            if (IntOf(st, "day") != want.Value<int>()) { all = false; lastReason = "day"; }
                            break;
                        case "hourEquals":
                            if (IntOf(st, "hour") != want.Value<int>()) { all = false; lastReason = "hour"; }
                            break;
                    }

                    if (!all) break;
                }

                if (all) return (true, (int)sw.ElapsedMilliseconds, null);
            }

            await Task.Delay(60, ct).ConfigureAwait(false);
        }

        return (false, (int)sw.ElapsedMilliseconds, "timeout on " + lastReason);
    }

    private static object Fail(List<object> log, string error, DateTime started)
        => new
        {
            ok = false,
            error,
            elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            log,
        };

    private static bool GetOk(object result)
    {
        if (result == null) return false;
        var t = result.GetType();
        var p = t.GetProperty("ok");
        if (p != null) return (bool)(p.GetValue(result) ?? false);
        try
        {
            var j = JObject.FromObject(result);
            return (bool?)j["ok"] ?? false;
        }
        catch { return false; }
    }

    private static bool StrEq(IDictionary<string, object> st, string key, string want)
    {
        if (!st.TryGetValue(key, out var v)) return false;
        return string.Equals(v?.ToString(), want, StringComparison.OrdinalIgnoreCase);
    }

    private static int IntOf(IDictionary<string, object> st, string key)
    {
        if (!st.TryGetValue(key, out var v) || v == null) return 0;
        try { return Convert.ToInt32(v); } catch { return 0; }
    }

    private static int SelectionCount(IDictionary<string, object> st)
    {
        if (!st.TryGetValue("selectionSet", out var v)) return 0;
        if (v is IList list) return list.Count;
        if (v is IEnumerable en) return en.Cast<object>().Count();
        return 0;
    }
}
