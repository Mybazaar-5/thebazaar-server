using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BazaarLauncher;

/// <summary>
/// 批量操作 + 条件等待 + 事件记录。
///
/// 设计目标：**一次 HTTP 往返完成一串操作**，而不是每条命令一次 round-trip。
/// 配合 GameDriver 的主线程泵，整条链在游戏进程内同步执行，延迟接近零。
/// </summary>
internal static class DriverOps
{
    // ── 事件记录（供 /events 与 wait 使用）──────────────────────────────────

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

    // ── 批量执行 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 执行一串操作。每个 op 之间默认等 settleMs（默认 120ms），
    /// 也可以给单条 op 指定 "wait" 条件。
    ///
    /// 请求体：
    /// {
    ///   "op": "batch",
    ///   "settleMs": 120,
    ///   "stopOnError": true,
    ///   "ops": [
    ///     {"op":"startRun","hero":"Vanessa"},
    ///     {"wait":{"appState":"ChoiceState","minCards":3},"timeoutMs":15000},
    ///     {"op":"selectSkill"},
    ///     {"wait":{"selectionType":"Item"},"timeoutMs":8000},
    ///     {"op":"buy"},
    ///     {"op":"exit"}, {"op":"exit"}
    ///   ]
    /// }
    /// </summary>
    internal static object RunBatch(JObject req)
    {
        var ops = req["ops"] as JArray;
        if (ops == null || ops.Count == 0)
            return new { ok = false, error = "ops required" };

        int settleMs = (int?)req["settleMs"] ?? 120;
        bool stopOnError = (bool?)req["stopOnError"] ?? true;

        var log = new List<object>();
        var started = DateTime.UtcNow;

        foreach (var item in ops)
        {
            var o = item as JObject;
            if (o == null) continue;

            // wait 节点
            if (o["wait"] != null)
            {
                var waitSpec = o["wait"] as JObject ?? new JObject();
                int timeout = (int?)o["timeoutMs"] ?? 10000;
                var waitResult = WaitFor(waitSpec, timeout);
                log.Add(new { kind = "wait", spec = waitSpec.ToString(Formatting.None), ok = waitResult.ok, elapsedMs = waitResult.elapsedMs, error = waitResult.error });
                if (!waitResult.ok && stopOnError) return Fail(log, "wait timeout: " + waitResult.error, started);
                continue;
            }

            // 普通 op
            string op = (string)o["op"] ?? "";
            if (string.IsNullOrEmpty(op)) continue;

            var result = ExecuteOne(op, o);
            log.Add(new { kind = "op", op, ok = GetOk(result), result });

            if (!GetOk(result) && stopOnError)
                return Fail(log, "op failed: " + op, started);

            if (settleMs > 0) Thread.Sleep(settleMs);
        }

        return new
        {
            ok = true,
            opCount = log.Count,
            elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            log,
        };
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
        // 匿名类型 → 转 JSON 再看
        try
        {
            var j = JObject.FromObject(result);
            return (bool?)j["ok"] ?? false;
        }
        catch { return false; }
    }

    // ── 单条 op 分发（GameDriver 用）────────────────────────────────────────

    internal static object ExecuteOne(string op, JObject req)
    {
        return op.ToLowerInvariant() switch
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
            "wait" => new { ok = true, op = "wait", note = "use batch for waits" },
            "state" => new { ok = true, state = GameDriver.ReadStatePublic() },
            _ => new { ok = false, error = "unknown op: " + op },
        };
    }

    // ── 条件等待 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 轮询状态直到条件满足。条件字段：
    ///   appState      : 期望的 AppState 类名（如 "ChoiceState"）
    ///   runState      : 期望的 ERunState 名（如 "Choice"）
    ///   minCards      : 卡牌数 >= N
    ///   minSelection  : selectionSet 数量 >= N
    ///   selectionType : selectionSet 里第一张卡的 Type（"Item"/"Skill"/...）
    ///   minGold       : gold >= N
    ///   maxGold       : gold <= N
    ///   goldEquals    : gold == N
    ///   dayEquals     : day == N
    ///   hourEquals    : hour == N
    /// </summary>
    internal static (bool ok, int elapsedMs, string error) WaitFor(JObject spec, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string lastReason = "";

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var st = GameDriver.ReadStatePublic() as IDictionary<string, object>;
            if (st != null)
            {
                bool all = true;
                lastReason = "";

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
                        default:
                            break;
                    }

                    if (!all) break;
                }

                if (all) return (true, (int)sw.ElapsedMilliseconds, null);
            }

            Thread.Sleep(60);
        }

        return (false, (int)sw.ElapsedMilliseconds, "timeout on " + lastReason);
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
