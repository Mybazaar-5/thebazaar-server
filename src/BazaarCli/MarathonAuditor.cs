using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarCli;

/// <summary>
/// 实机多局连打与全量性能/UI/词条/战斗触发核验器（bz marathon / bz auto --runs N）。
/// </summary>
internal static class MarathonAuditor
{
    public static readonly string[] DefaultHeroes = { "Vanessa", "Pygmalien", "Dooley", "Mak", "Stelle" };

    public sealed class Config
    {
        public int Runs { get; set; } = 5;
        public float Speed { get; set; } = 12f;
        public int MaxStepsPerRun { get; set; } = 900;
        public string OutPath { get; set; }
        public List<string> Heroes { get; set; } = new(DefaultHeroes);
        public string Until { get; set; } = "end";
        public bool AuditMode { get; set; } = true;
        public bool AutoResume { get; set; } = true;
        public string HeroFallbackMode { get; set; } = "fallback"; // fallback | skip | fail
        public List<string> FallbackHeroes { get; set; } = new() { "Jules", "Vanessa", "Pygmalien", "Dooley", "Mak" };
    }

    public static async Task<int> RunAsync(DriverClient client, Config cfg)
    {
        var globalSw = Stopwatch.StartNew();
        var runReports = new List<RunReport>();
        var failedHeroes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var heroFallbacks = new List<JObject>();

        try
        {
            await client.DriveWaitAsync(new
            {
                op = "combatspeed",
                speed = cfg.Speed,
                autoExitReplay = cfg.AuditMode,
                waitIdle = false
            });

            if (cfg.AuditMode)
            {
                await EnsureCleanMainMenuBeforeStartAsync(client);
            }

            for (int runIdx = 0; runIdx < cfg.Runs; runIdx++)
            {
                string requestedHero = cfg.Heroes[runIdx % cfg.Heroes.Count];
                string activeHero = requestedHero;
                string fallbackReason = null;
                Console.WriteLine($"\n=== [Run {runIdx + 1}/{cfg.Runs}] Hero={requestedHero} Speed={cfg.Speed}x ===");

                var report = new RunReport
                {
                    RunIndex = runIdx + 1,
                    Hero = requestedHero,
                    RequestedHero = requestedHero,
                    StartedAtUtc = DateTime.UtcNow.ToString("O"),
                };

                try
                {
                    if (cfg.AuditMode)
                    {
                        bool started = false;
                        var candidates = BuildHeroCandidatesForRun(cfg, runIdx, requestedHero, failedHeroes);
                        foreach (string candHero in candidates)
                        {
                            if (!string.Equals(candHero, requestedHero, StringComparison.OrdinalIgnoreCase))
                            {
                                Console.Error.WriteLine($"[Run {runIdx + 1}] 尝试回退/替补英雄: {requestedHero} -> {candHero}");
                            }

                            var startRes = await StartFreshRunAndWaitChoiceAsync(client, candHero);
                            if (startRes.Ok)
                            {
                                activeHero = string.IsNullOrEmpty(startRes.ActualHero) ? candHero : startRes.ActualHero;
                                if (!string.Equals(candHero, requestedHero, StringComparison.OrdinalIgnoreCase))
                                {
                                    heroFallbacks.Add(new JObject
                                    {
                                        ["runIndex"] = runIdx + 1,
                                        ["requestedHero"] = requestedHero,
                                        ["fallbackHero"] = activeHero,
                                        ["mode"] = cfg.HeroFallbackMode,
                                        ["reason"] = fallbackReason ?? "hero unavailable",
                                    });
                                }
                                started = true;
                                break;
                            }

                            fallbackReason = startRes.Error ?? "start failed";
                            Console.Error.WriteLine($"[Run {runIdx + 1}] 英雄 {candHero} 开局失败 (restricted={startRes.HeroRestricted}): {fallbackReason}");

                            if (startRes.HeroRestricted)
                            {
                                failedHeroes.Add(candHero);
                            }

                            bool allowFallback = cfg.HeroFallbackMode != "fail" &&
                                                 (startRes.HeroRestricted || candHero is "Stelle" or "Jules");
                            if (!allowFallback)
                            {
                                break;
                            }
                        }

                        if (!started)
                        {
                            Console.Error.WriteLine($"[Run {runIdx + 1}] 开局失败，中止后续对局");
                            report.Completed = false;
                            report.Verdict = "StartFailed";
                            report.Error = fallbackReason ?? "start failed";
                            report.EndedAtUtc = DateTime.UtcNow.ToString("O");
                            runReports.Add(report);
                            break;
                        }
                    }

                    report.Hero = activeHero;
                    report.HeroFallbackReason = fallbackReason;
                    runReports.Add(report);

                    await PlaySingleRunAsync(client, cfg, report);

                    Console.WriteLine(
                        $"[Run {report.RunIndex}] {report.Verdict} | D{report.FinalDay}H{report.FinalHour} " +
                        $"W/L={report.Victories}/{report.Losses} | {report.DurationSec:F2}s " +
                        $"steps={report.TotalSteps} ops/s={report.OpsPerSec:F1} " +
                        $"FPS(avg/p10)={report.FpsAvg:F1}/{report.FpsP10:F1} " +
                        $"Heap={report.MonoHeapStartMB:F1}->{report.MonoHeapEndMB:F1}MB " +
                        $"uiMismatch={report.UiMismatches.Count} zeroTrig={report.LikelyZeroTriggerCards.Count} " +
                        $"invariants=({report.InvariantSummary})");
                    if (!report.InvariantPassed)
                    {
                        Console.Error.WriteLine($"[ALERT][Run {report.RunIndex}] 官方核心规则不变量违例！{report.InvariantSummary}");
                        foreach (string v in report.WlViolationDetails.Concat(report.PrestigeViolationDetails))
                        {
                            Console.Error.WriteLine($"  - {v}");
                        }
                    }

                    if (cfg.AuditMode && runIdx < cfg.Runs - 1)
                    {
                        bool returned = await ReturnToMainMenuAndWaitReadyAsync(client);
                        if (!returned)
                        {
                            Console.Error.WriteLine($"[Run {runIdx + 1}] 返回主大厅超时，尝试 /concede 兜底清理");
                            await SafeRecoverToMainMenuAsync(client);
                        }
                    }
                }
                catch (Exception runEx)
                {
                    if (!runReports.Contains(report)) runReports.Add(report);
                    string err = $"{runEx.GetType().Name}: {runEx.Message}";
                    Console.Error.WriteLine($"[Run {runIdx + 1}] 对局异常中止: {err}");
                    report.Completed = false;
                    if (string.IsNullOrEmpty(report.Error)) report.Error = err;
                    if (report.Verdict == "Incomplete") report.Verdict = "RunError:" + runEx.GetType().Name;
                    report.StepErrors.Add("[RunLoop] " + err);
                    report.EndedAtUtc ??= DateTime.UtcNow.ToString("O");
                    FinalizeRunStats(report);

                    if (cfg.AuditMode && runIdx < cfg.Runs - 1)
                    {
                        await SafeRecoverToMainMenuAsync(client);
                    }
                }
            }
        }
        finally
        {
            try
            {
                await client.DriveWaitAsync(new { op = "combatspeed", speed = 0f, autoExitReplay = false, waitIdle = false });
            }
            catch { }

            globalSw.Stop();

            if (!string.IsNullOrWhiteSpace(cfg.OutPath))
            {
                try
                {
                    var summaryObj = BuildAggregateReport(cfg, runReports, heroFallbacks, globalSw.Elapsed.TotalSeconds);
                    string dir = Path.GetDirectoryName(Path.GetFullPath(cfg.OutPath));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    await File.WriteAllTextAsync(cfg.OutPath, summaryObj.ToString(Formatting.Indented));
                    Console.WriteLine($"\n审计报告已写入: {cfg.OutPath}");
                }
                catch (Exception writeEx)
                {
                    Console.Error.WriteLine($"\n写入审计报告失败 ({cfg.OutPath}): {writeEx.Message}");
                }
            }
        }

        int totalWlViolations = runReports.Sum(r => r.WlInvariantViolations);
        int totalPrestigeViolations = runReports.Sum(r => r.PrestigeInvariantViolations);
        int totalWlChecks = runReports.Sum(r => r.WlChecksCount);
        int totalPvpAudited = runReports.Sum(r => r.PvpAuditedCount);
        bool allInvariantsPassed = totalWlViolations == 0 && totalPrestigeViolations == 0;
        Console.WriteLine(
            $"[InvariantSummary] status={(allInvariantsPassed ? "PASS" : "FAIL")} | " +
            $"wlChecks={totalWlChecks} wlInvariantViolations={totalWlViolations} | " +
            $"pvpAudited={totalPvpAudited} prestigeInvariantViolations={totalPrestigeViolations}");
        if (!allInvariantsPassed)
        {
            Console.Error.WriteLine($"[ALERT][Marathon] 检测到核心不变量违例：wlInvariantViolations={totalWlViolations}, prestigeInvariantViolations={totalPrestigeViolations}");
        }

        bool allCompleted = runReports.Count == cfg.Runs && runReports.All(r => r.Completed);
        return (allCompleted && allInvariantsPassed) ? 0 : 1;
    }

    private static async Task<bool> SafeRecoverToMainMenuAsync(DriverClient client)
    {
        try
        {
            await client.PostAsync("/concede", new { forceHttp = true });
        }
        catch { }

        try
        {
            if (await WaitUntilMainMenuReadyAsync(client, 15000)) return true;
            return await ReturnToMainMenuAndWaitReadyAsync(client);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> BuildHeroCandidatesForRun(Config cfg, int runIdx, string requestedHero, HashSet<string> failedHeroes)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string h)
        {
            if (string.IsNullOrWhiteSpace(h) || failedHeroes.Contains(h) || !seen.Add(h)) return;
            list.Add(h);
        }

        TryAdd(requestedHero);
        if (cfg.HeroFallbackMode == "fail")
        {
            return list;
        }

        if (cfg.HeroFallbackMode == "skip")
        {
            for (int offset = 1; offset <= cfg.Heroes.Count; offset++)
            {
                TryAdd(cfg.Heroes[(runIdx + offset) % cfg.Heroes.Count]);
            }
            foreach (var h in cfg.FallbackHeroes) TryAdd(h);
        }
        else
        {
            foreach (var h in cfg.FallbackHeroes) TryAdd(h);
            for (int offset = 1; offset <= cfg.Heroes.Count; offset++)
            {
                TryAdd(cfg.Heroes[(runIdx + offset) % cfg.Heroes.Count]);
            }
        }

        return list;
    }

    private static bool IsMainMenuScene(string activeScene)
        => string.Equals(activeScene, "MainMenuScene", StringComparison.OrdinalIgnoreCase)
           || string.Equals(activeScene, "HeroSelectScene", StringComparison.OrdinalIgnoreCase);

    private static bool IsMainMenuReady(JObject d)
    {
        bool inRun = d["inRun"]?.Value<bool>() == true;
        bool trans = d["isSceneTransitioning"]?.Value<bool>() == true;
        bool menuReady = d["menuReady"]?.Value<bool>() == true;
        string appState = d["appState"]?.ToString() ?? "";
        string activeScene = d["activeScene"]?.ToString() ?? "";

        if (inRun || trans) return false;
        if (IsMainMenuScene(activeScene) && menuReady) return true;
        return (appState is "null" or "EmptyState") && menuReady;
    }

    private static bool IsStartChoiceReady(JObject d)
    {
        bool inRun = d["inRun"]?.Value<bool>() == true;
        string appSt = d["appState"]?.ToString() ?? "";
        int selCount = (d["selectionSet"] as JArray)?.Count ?? 0;
        return inRun && selCount > 0 &&
               appSt is "ChoiceState" or "EncounterState" or "LevelUpState" or "LootState";
    }

    private static async Task EnsureCleanMainMenuBeforeStartAsync(DriverClient client)
    {
        var (d, _) = await client.InspectCompactAsync(ui: false);
        bool inRun = d["inRun"]?.Value<bool>() == true;
        bool hasActiveRunFlag = d["hasActiveRunFlag"]?.Value<bool>() == true;
        string appState = d["appState"]?.ToString() ?? "";
        string activeScene = d["activeScene"]?.ToString() ?? "";
        bool menuReady = d["menuReady"]?.Value<bool>() == true;

        if (appState is "EndRunVictoryState" or "EndRunDefeatState")
        {
            await ReturnToMainMenuAndWaitReadyAsync(client);
            return;
        }

        if (IsMainMenuScene(activeScene) && menuReady && !inRun && !hasActiveRunFlag)
        {
            return;
        }

        if (hasActiveRunFlag || inRun)
        {
            Console.WriteLine($"检测到残留活跃对局 (inRun={inRun}, hasActiveRunFlag={hasActiveRunFlag}, scene={activeScene}, appState={appState})，正在放弃并退回主大厅...");
            await client.PostAsync("/concede", new { forceHttp = true });
            if (!await WaitUntilMainMenuReadyAsync(client, 20000))
            {
                await ReturnToMainMenuAndWaitReadyAsync(client);
            }
            return;
        }

        if (!IsMainMenuReady(d))
        {
            await WaitUntilMainMenuReadyAsync(client, 15000);
        }
    }

    private static async Task<bool> ReturnToMainMenuAndWaitReadyAsync(DriverClient client)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var (cur, _) = await client.InspectCompactAsync(ui: false);
            if (IsMainMenuReady(cur))
            {
                return true;
            }

            await client.DriveWaitAsync(new { op = "returnToMenu", waitIdle = false });
            if (await WaitUntilMainMenuReadyAsync(client, 20000))
            {
                await Task.Delay(120);
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> WaitUntilMainMenuReadyAsync(DriverClient client, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var (d, _) = await client.InspectCompactAsync(ui: false);
                if (IsMainMenuReady(d))
                {
                    return true;
                }
            }
            catch { }
            await Task.Delay(80);
        }
        return false;
    }

    private static async Task<(bool Ok, bool HeroRestricted, string Error, string ActualHero)> StartFreshRunAndWaitChoiceAsync(
        DriverClient client, string hero)
    {
        string lastError = null;
        bool lastRestricted = false;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var (r, _) = await client.DriveWaitAsync(new { op = "startRun", hero, waitIdle = false });

            if (r["alreadyInRun"]?.Value<bool>() == true)
            {
                string curApp = r["appState"]?.ToString() ?? "";
                string curHero = null;
                try
                {
                    var (curSnap, _) = await client.InspectCompactAsync(ui: false);
                    curHero = curSnap["hero"]?.ToString();
                }
                catch { }

                Console.WriteLine($"  startRun 检测到已在局内 (appState={curApp}, hero={curHero}, want={hero})，先 /concede 退回主大厅...");
                await client.PostAsync("/concede", new { forceHttp = true });
                if (!await WaitUntilMainMenuReadyAsync(client, 20000))
                {
                    await ReturnToMainMenuAndWaitReadyAsync(client);
                }
                lastError = $"already in run ({curApp})";
                continue;
            }

            if (r["needsMenuWait"]?.Value<bool>() == true)
            {
                if (!await WaitUntilMainMenuReadyAsync(client, 20000))
                {
                    await ReturnToMainMenuAndWaitReadyAsync(client);
                }
                lastError = "needs menu wait";
                continue;
            }

            if (r["ok"]?.Value<bool>() != true)
            {
                lastError = r["error"]?.ToString() ?? "unknown error";
                lastRestricted = r["heroRestricted"]?.Value<bool>() == true
                                 || string.Equals(r["errorCode"]?.ToString(), "INVALID_ARG", StringComparison.OrdinalIgnoreCase);
                Console.Error.WriteLine($"  startRun 失败 (hero={hero}): {lastError}");
                if (lastRestricted)
                {
                    return (false, true, lastError, null);
                }
                await Task.Delay(500);
                continue;
            }

            var sw = Stopwatch.StartNew();
            bool bouncedToMenu = false;
            while (sw.ElapsedMilliseconds < 35000)
            {
                try
                {
                    var (d, _) = await client.InspectCompactAsync(ui: false);
                    if (IsStartChoiceReady(d))
                    {
                        string actualHero = d["hero"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(actualHero) &&
                            !string.Equals(actualHero, hero, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.Error.WriteLine($"  startRun 英雄不匹配: 请求={hero} 实际={actualHero}，清理残局后标记受限");
                            await client.PostAsync("/concede", new { forceHttp = true });
                            await ReturnToMainMenuAndWaitReadyAsync(client);
                            return (false, true, $"hero mismatch: requested={hero}, actual={actualHero}", actualHero);
                        }
                        return (true, false, null, actualHero);
                    }

                    if (sw.ElapsedMilliseconds > 3000 && IsMainMenuReady(d))
                    {
                        bouncedToMenu = true;
                        lastError = $"client returned to main menu before entering {hero} run";
                        Console.Error.WriteLine($"  {lastError}");
                        break;
                    }
                }
                catch { }
                await Task.Delay(80);
            }

            if (!bouncedToMenu)
            {
                lastError = $"timeout 35000ms waiting for {hero} initial Encounter/Choice";
                Console.Error.WriteLine($"  {lastError}");
                await client.PostAsync("/concede", new { forceHttp = true });
                await ReturnToMainMenuAndWaitReadyAsync(client);
            }
        }

        return (false, lastRestricted, lastError ?? "startRun failed after retries", null);
    }

    private static async Task<RunReport> PlaySingleRunAsync(DriverClient client, Config cfg, RunReport report)
    {
        var sw = Stopwatch.StartNew();
        int lastCombatSeq = 0;
        try
        {
            var (initAudit, _) = await client.CombatAuditSinceAsync(0);
            var arr = initAudit["audits"] as JArray;
            if (arr != null && arr.Count > 0)
            {
                lastCombatSeq = arr.Max(a => a["combatSeq"]?.Value<int>() ?? 0);
            }
        }
        catch { }

        (string untilKind, int untilVal) = Program.ParseUntilPublic(cfg.Until);

        int step = 0;
        int combatWaitPolls = 0;
        int stuckCount = 0;
        int mainMenuRetries = 0;
        int stateAlignWaits = 0;
        int rerollsInCurrentShop = 0;
        int movesInCurrentShop = 0;
        string lastLayoutSig = "";
        string lastShopKey = "";
        string lastSig = "";
        bool inCombatObserved = false;
        JObject lastNonCombatSnapshot = null;
        JObject preCombatSnapshot = null;
        var triedInState = new HashSet<string>(StringComparer.Ordinal);
        var committedPedestalKeys = new HashSet<string>(StringComparer.Ordinal);
        var uiMismatchDedup = new HashSet<string>(StringComparer.Ordinal);
        var invariants = new RunInvariantTracker(report);

        JObject d = null;
        try
        {
            var (initD, _) = await client.InspectCompactAsync(ui: true);
            d = initD;
            SamplePerf(report, d, isStart: true);
        }
        catch { }

        try
        {
            while (step < cfg.MaxStepsPerRun)
            {
                try
                {
                    if (d == null)
                    {
                        var (polled, _) = await client.InspectCompactAsync(ui: true);
                        d = polled;
                        SamplePerf(report, d, isStart: false);
                    }

            bool inRun = d["inRun"]?.Value<bool>() == true;
            string appStateNow = d["appState"]?.ToString() ?? "";
            string runStateNow = d["runState"]?.ToString() ?? "";

            // 0. 大厅续连保护
            if (!inRun || appStateNow is "EmptyState" or "null")
            {
                if (!cfg.AuditMode && cfg.AutoResume && mainMenuRetries++ < 5)
                {
                    var (res, _) = await client.PostAsync("/resume", new { });
                    if (res["ok"]?.Value<bool>() == true)
                    {
                        Console.Write("↻");
                        await Task.Delay(500);
                        d = null;
                        continue;
                    }
                }
                await Task.Delay(120);
                d = null;
                continue;
            }
            mainMenuRetries = 0;

            report.FinalDay = d["day"]?.Value<int>() ?? report.FinalDay;
            report.FinalHour = d["hour"]?.Value<int>() ?? report.FinalHour;
            report.Victories = d["victories"]?.Value<int>() ?? report.Victories;
            report.Losses = d["losses"]?.Value<int>() ?? report.Losses;
            report.FinalPrestige = d["prestige"]?.Value<int>() ?? report.FinalPrestige;
            report.FinalLevel = d["level"]?.Value<int>() ?? report.FinalLevel;

            // 记录所有经手卡牌/技能/候选
            TrackTouchedEntities(report, d);

            // 1. 终局判断
            if (SmartStrategy.IsTerminalState(d))
            {
                report.Completed = true;
                report.Verdict = (runStateNow.Contains("Victory") || appStateNow.Contains("Victory")) ? "Victory" : "Defeat";
                await DrainCombatAuditsAsync(client, report, lastCombatSeq, seq => lastCombatSeq = seq,
                    isPvpAudit => { if (isPvpAudit && preCombatSnapshot != null) invariants.MarkPrePvpIfNotPending(preCombatSnapshot); });
                invariants.AuditPostPvpIfPending(d, isTerminal: true);
                invariants.CheckTerminalWl(d);
                SamplePerf(report, d, isStart: false);
                break;
            }

            if (cfg.Until != "end" && Program.ReachedPublic(d, untilKind, untilVal))
            {
                report.Completed = true;
                report.Verdict = $"Reached:{cfg.Until}";
                await DrainCombatAuditsAsync(client, report, lastCombatSeq, seq => lastCombatSeq = seq,
                    isPvpAudit => { if (isPvpAudit && preCombatSnapshot != null) invariants.MarkPrePvpIfNotPending(preCombatSnapshot); });
                invariants.AuditPostPvpIfPending(d, isTerminal: false);
                invariants.CheckStableWl(d);
                SamplePerf(report, d, isStart: false);
                break;
            }

            // 2. 战斗等待（高速轮询 compact&ui=0，不消耗操作步数预算）
            if (appStateNow is "CombatState" or "PVPCombatState")
            {
                if (!inCombatObserved && lastNonCombatSnapshot != null)
                {
                    preCombatSnapshot = lastNonCombatSnapshot;
                }
                if ((appStateNow == "PVPCombatState" || runStateNow == "PVPCombat") && lastNonCombatSnapshot != null)
                {
                    invariants.MarkPrePvpIfNotPending(lastNonCombatSnapshot);
                }
                inCombatObserved = true;
                if (++combatWaitPolls > 1200)
                {
                    Console.Write("!");
                    combatWaitPolls = 0;
                    await client.DriveWaitAsync(new { op = "exit", waitIdle = true });
                }
                else
                {
                    Console.Write("⚔");
                    await Task.Delay(50);
                }
                var (combatD, _) = await client.InspectCompactAsync(ui: false);
                SamplePerf(report, combatD, isStart: false);
                d = combatD;
                continue;
            }

            combatWaitPolls = 0;

            if (appStateNow == "ReplayState")
            {
                if (!inCombatObserved && lastNonCombatSnapshot != null)
                {
                    preCombatSnapshot = lastNonCombatSnapshot;
                }
                inCombatObserved = true;
                if (cfg.AuditMode)
                {
                    // AutoExitReplay 会在数帧内自动退出；若仍停留超过 120ms 再手动兜底 exit
                    await Task.Delay(60);
                    var (repD, _) = await client.InspectCompactAsync(ui: false);
                    if (repD["appState"]?.ToString() == "ReplayState")
                    {
                        var (er, eMs) = await client.DriveWaitAsync(new
                        {
                            op = "exit",
                            waitIdle = true,
                            includeCompactState = true,
                            includeUi = true
                        });
                        RecordOpTiming(report, eMs, er);
                        d = ExtractCompactOrNull(er);
                        Console.Write("R");
                    }
                    else
                    {
                        d = repD;
                    }
                }
                else
                {
                    var (er, eMs) = await client.DriveWaitAsync(new
                    {
                        op = "exit",
                        waitIdle = true,
                        includeCompactState = true,
                        includeUi = true
                    });
                    RecordOpTiming(report, eMs, er);
                    d = ExtractCompactOrNull(er);
                    Console.Write("R");
                }
                continue;
            }

            // 战后回到常规局外状态：拉取战斗审计 + 核对永久成长 + PvP 声望/胜负不变量 + 刷新 UI 检查
            if (inCombatObserved)
            {
                inCombatObserved = false;
                await DrainCombatAuditsAsync(client, report, lastCombatSeq, seq => lastCombatSeq = seq,
                    isPvpAudit => { if (isPvpAudit && preCombatSnapshot != null) invariants.MarkPrePvpIfNotPending(preCombatSnapshot); });

                // 确保在离开 Replay 进入 Choice/Loot 后拿一份带 UI 的紧凑态
                var (postD, _) = await client.InspectCompactAsync(ui: true);
                d = postD;
                SamplePerf(report, d, isStart: false);

                if (invariants.PendingPvpAudit)
                {
                    d = await WaitForPostPvpSettledAsync(client, d, invariants, s => SamplePerf(report, s, isStart: false));
                    invariants.AuditPostPvpIfPending(d, isTerminal: SmartStrategy.IsTerminalState(d));
                    if (SmartStrategy.IsTerminalState(d))
                    {
                        continue;
                    }
                }

                if (preCombatSnapshot != null)
                {
                    AuditPostCombatGrowth(report, preCombatSnapshot, d);
                    preCombatSnapshot = null;
                }
            }

            if (!SmartStrategy.AreStatesAligned(d) && stateAlignWaits++ < 15)
            {
                await Task.Delay(50);
                d = null;
                continue;
            }
            stateAlignWaits = 0;

            step++;
            lastNonCombatSnapshot = d;
            if (invariants.PendingPvpAudit)
            {
                invariants.AuditPostPvpIfPending(d, isTerminal: SmartStrategy.IsTerminalState(d));
            }
            invariants.CheckStableWl(d);

            // 局外 UI 宝石一致性检查（发现疑点才延迟 550ms 二次确认，零误报、零正常耗损）
            await AuditUiConsistencyAsync(client, report, d, uiMismatchDedup);

            var selection = d["selection"] as JArray ?? new JArray();
            var selectionIds = d["selectionSet"] as JArray ?? new JArray();
            var hand = d["hand"] as JArray ?? new JArray();
            var stash = d["stash"] as JArray ?? new JArray();
            int handItemCount = hand.OfType<JObject>().Count(SmartStrategy.IsBoardItem);
            int stashItemCount = stash.OfType<JObject>().Count(SmartStrategy.IsBoardItem);
            int gold = d["gold"]?.Value<int>() ?? 0;
            bool selIsFree = d["selectionRules"]?["selectionIsFree"]?.Value<bool>() == true;
            bool canExit = SmartStrategy.CanSendExitInState(d);
            bool isChoiceState = SmartStrategy.IsChoiceState(appStateNow, runStateNow);

            string shopKey = $"{d["day"]}|{d["hour"]}|{d["currentEncounterId"]}|{runStateNow}";
            if (shopKey != lastShopKey)
            {
                lastShopKey = shopKey;
                rerollsInCurrentShop = 0;
                movesInCurrentShop = 0;
            }

            // 3. 卡死检测（ChoiceState 绝不发送 /exit）
            string sig = $"{appStateNow}|{runStateNow}|{d["day"]}|{d["hour"]}|{gold}|{selectionIds.Count}|{d["currentEncounterId"]}|{handItemCount}|{stashItemCount}";
            if (sig == lastSig) stuckCount++;
            else { stuckCount = 0; lastSig = sig; triedInState.Clear(); }

            if (stuckCount >= 10)
            {
                if (!isChoiceState && canExit)
                {
                    var (xr, xMs) = await client.DriveWaitAsync(new { op = "exit", waitIdle = true, includeCompactState = true, includeUi = true });
                    RecordOpTiming(report, xMs, xr);
                    d = ExtractCompactOrNull(xr);
                    Console.Write("~");
                }
                else if (isChoiceState)
                {
                    d = await SmartBotActions.HandleChoiceStateFallbackAsync(
                        client, triedInState, forceResync: true, (ms, resp) => RecordOpTiming(report, ms, resp));
                }
                else
                {
                    var (sr, sMs) = await client.DriveWaitAsync(new { op = "sync", waitIdle = true, includeCompactState = true, includeUi = true });
                    RecordOpTiming(report, sMs, sr);
                    d = ExtractCompactOrNull(sr);
                    Console.Write("~");
                }
                stuckCount = 0;
                continue;
            }

            // 4. 智能铺场与邻接站位（每当解锁格位/手牌/背包变化时执行，单次 /move 后动态重算避免旧坐标冲突）
            if (runStateNow is "Choice" or "Encounter" && appStateNow is "ChoiceState" or "EncounterState" && movesInCurrentShop < 4)
            {
                string layoutSig = SmartStrategy.BuildLayoutSignature(d);
                if (layoutSig != lastLayoutSig)
                {
                    var moves = SmartStrategy.PlanOptimalBoardMoves(d);
                    if (moves.Count > 0)
                    {
                        var mv = moves[0];
                        string moveKey = $"mv:{mv.InstanceId}:{mv.Section}:{mv.Socket}";
                        if (!triedInState.Contains(moveKey))
                        {
                            triedInState.Add(moveKey);
                            movesInCurrentShop++;
                            var beforeSnap = d;
                            var (mr, mMs) = await client.PostAsync("/move", new
                            {
                                instanceId = mv.InstanceId,
                                section = mv.Section,
                                socket = mv.Socket,
                                waitIdle = true,
                                includeCompactState = true,
                                includeUi = true
                            });
                            RecordOpTiming(report, mMs, mr);
                            if (mr["ok"]?.Value<bool>() == true)
                            {
                                Console.Write("M");
                                var nextD = ExtractCompactOrNull(mr);
                                if (nextD != null)
                                {
                                    AuditOutOfCombatTransition(report, "move", mv.InstanceId, beforeSnap, nextD, mr);
                                    d = nextD;
                                    SamplePerf(report, d, isStart: false);
                                }
                                continue;
                            }
                        }
                    }
                    lastLayoutSig = layoutSig;
                }

                // 5. 自动消耗 Hand / Stash 中的一次性 Loot 消耗品
                var consumable = hand.Concat(stash).OfType<JObject>()
                    .Where(SmartStrategy.IsBoardItem)
                    .FirstOrDefault(c => SmartStrategy.IsUsableConsumable(c) && !triedInState.Contains("use:" + c["instanceId"]));
                if (consumable != null)
                {
                    string cid = consumable["instanceId"]!.ToString();
                    triedInState.Add("use:" + cid);
                    var beforeSnap = d;
                    var (sr, sMs) = await client.DriveWaitAsync(new
                    {
                        op = "sell",
                        instanceId = cid,
                        waitIdle = true,
                        includeCompactState = true,
                        includeUi = true
                    });
                    RecordOpTiming(report, sMs, sr);
                    if (sr["ok"]?.Value<bool>() == true)
                    {
                        Console.Write("U");
                        var nextD = ExtractCompactOrNull(sr);
                        if (nextD != null)
                        {
                            AuditOutOfCombatTransition(report, "consumable", cid, beforeSnap, nextD, sr);
                            d = nextD;
                            SamplePerf(report, d, isStart: false);
                        }
                        continue;
                    }
                }
            }

            // 6. PedestalState（委托至 SmartBotActions 单次提交即离开，绝不向新状态连续盲发 commitPedestal）
            if (SmartStrategy.IsPedestalState(appStateNow, runStateNow))
            {
                var (pedHandled, pedNext) = await SmartBotActions.HandlePedestalStateAsync(
                    client,
                    d,
                    triedInState,
                    committedPedestalKeys,
                    (ms, resp) => RecordOpTiming(report, ms, resp),
                    (opName, targetId, beforeSnap, afterSnap, resp) => AuditOutOfCombatTransition(report, opName, targetId, beforeSnap, afterSnap, resp),
                    s => SamplePerf(report, s, isStart: false));
                if (pedHandled)
                {
                    d = pedNext;
                    continue;
                }
            }

            // 7. LootState / LevelUpState
            var candidates = Program.ResolveSelectionCandidatesPublic(selection, selectionIds);
            if (runStateNow is "Loot" or "LevelUp" || appStateNow is "LootState" or "LevelUpState")
            {
                bool acted = false;
                var allOwned = hand.Concat(stash).OfType<JObject>().Where(SmartStrategy.IsBoardItem).ToList();
                var orderedLoot = candidates
                    .OrderByDescending(c => SmartStrategy.CanFuseWithOwned(c, allOwned) ? 1000.0 : 0.0)
                    .ThenByDescending(c => c["type"]?.ToString() == "Skill" ? 400.0 : 0.0)
                    .ThenByDescending(c => c["type"]?.ToString() == "EncounterStep" ? 350.0 : 0.0)
                    .ThenByDescending(c => c["type"]?.ToString() is "EventEncounter" or "PedestalEncounter" or "CombatEncounter" or "PvpEncounter" ? 320.0 : 0.0)
                    .ThenByDescending(c => SmartStrategy.ScoreCombatPower(c, hand))
                    .ToList();

                foreach (var c in orderedLoot)
                {
                    string cid = c["instanceId"]!.ToString();
                    if (triedInState.Contains(cid)) continue;
                    string ctype = c["type"]?.ToString() ?? "Item";
                    int price = selIsFree ? 0 : (c["buyPrice"]?.Value<int>() ?? 0);
                    if (price > gold) continue;

                    triedInState.Add(cid);
                    var beforeSnap = d;
                    if (ctype == "Skill")
                    {
                        var (sr, sMs) = await client.DriveWaitAsync(new { op = "selectSkill", instanceId = cid, waitIdle = true, includeCompactState = true, includeUi = true });
                        RecordOpTiming(report, sMs, sr);
                        if (sr["ok"]?.Value<bool>() == true)
                        {
                            Console.Write("S");
                            var nextD = ExtractCompactOrNull(sr);
                            if (nextD != null) { AuditOutOfCombatTransition(report, "selectSkill", cid, beforeSnap, nextD, sr); d = nextD; }
                            else d = null;
                            acted = true;
                            break;
                        }
                    }
                    else if (ctype == "EncounterStep")
                    {
                        var (er, eMs) = await client.PostAsync("/encounterstep", new { instanceId = cid, waitIdle = true, includeCompactState = true, includeUi = true });
                        RecordOpTiming(report, eMs, er);
                        if (er["ok"]?.Value<bool>() == true)
                        {
                            Console.Write("T");
                            var nextD = ExtractCompactOrNull(er);
                            if (nextD != null) { AuditOutOfCombatTransition(report, "encounterstep", cid, beforeSnap, nextD, er); d = nextD; }
                            else d = null;
                            acted = true;
                            break;
                        }
                    }
                    else if (ctype is "EventEncounter" or "PedestalEncounter" or "CombatEncounter" or "PvpEncounter")
                    {
                        var (er, eMs) = await client.DriveWaitAsync(new { op = "selectEncounter", instanceId = cid, waitIdle = true, includeCompactState = true, includeUi = true });
                        RecordOpTiming(report, eMs, er);
                        if (er["ok"]?.Value<bool>() == true)
                        {
                            Console.Write("E");
                            var nextD = ExtractCompactOrNull(er);
                            if (nextD != null) { AuditOutOfCombatTransition(report, "selectEncounter", cid, beforeSnap, nextD, er); d = nextD; }
                            else d = null;
                            acted = true;
                            break;
                        }
                    }
                    else
                    {
                        var (br, bMs) = await TrySmartBuyAsync(client, report, d, c, triedInState);
                        if (br != null && br["ok"]?.Value<bool>() == true)
                        {
                            Console.Write("L");
                            d = ExtractCompactOrNull(br);
                            acted = true;
                            break;
                        }
                    }
                }

                if (!acted)
                {
                    if (canExit)
                    {
                        var (xr, xMs) = await client.DriveWaitAsync(new { op = "exit", waitIdle = true, includeCompactState = true, includeUi = true });
                        RecordOpTiming(report, xMs, xr);
                        d = ExtractCompactOrNull(xr);
                        Console.Write(">");
                    }
                    else
                    {
                        await Task.Delay(100);
                        d = null;
                    }
                }
                continue;
            }

            // 8. ChoiceState / EncounterState（智能选择奇遇步、二合一升阶、高战力卡、腾格卖出、金币充裕主动 Reroll）
            if (candidates.Count > 0)
            {
                bool acted = false;
                var allOwned = hand.Concat(stash).OfType<JObject>().Where(SmartStrategy.IsBoardItem).ToList();

                // 8a. 优先处理 EncounterStep（兼容 step_ 与 stp_）
                foreach (var c in candidates.Where(x => x["type"]?.ToString() == "EncounterStep"))
                {
                    string cid = c["instanceId"]!.ToString();
                    if (triedInState.Contains(cid)) continue;
                    int price = selIsFree ? 0 : (c["buyPrice"]?.Value<int>() ?? 0);
                    if (price > gold) continue;

                    triedInState.Add(cid);
                    var beforeSnap = d;
                    var (er, eMs) = await client.PostAsync("/encounterstep", new { instanceId = cid, waitIdle = true, includeCompactState = true, includeUi = true });
                    RecordOpTiming(report, eMs, er);
                    if (er["ok"]?.Value<bool>() == true)
                    {
                        Console.Write("T");
                        var nextD = ExtractCompactOrNull(er);
                        if (nextD != null)
                        {
                            AuditOutOfCombatTransition(report, "encounterstep", cid, beforeSnap, nextD, er);
                            d = nextD;
                        }
                        else d = null;
                        acted = true;
                        break;
                    }
                }

                // 8b. 其次选遭遇（PedestalEncounter > EventEncounter > CombatEncounter/PvpEncounter）
                if (!acted)
                {
                    var enc = candidates
                        .Where(x =>
                        {
                            string cid = x["instanceId"]!.ToString();
                            string t = x["type"]?.ToString() ?? "";
                            int price = selIsFree ? 0 : (x["buyPrice"]?.Value<int>() ?? 0);
                            return !triedInState.Contains(cid) && price <= gold &&
                                   t is "PedestalEncounter" or "EventEncounter" or "CombatEncounter" or "PvpEncounter";
                        })
                        .OrderByDescending(x => x["type"]?.ToString() switch
                        {
                            "PedestalEncounter" => 3,
                            "EventEncounter" => 2,
                            _ => 1,
                        })
                        .FirstOrDefault();

                    if (enc != null)
                    {
                        string cid = enc["instanceId"]!.ToString();
                        string encType = enc["type"]?.ToString() ?? "";
                        int curHour = d["hour"]?.Value<int>() ?? 0;
                        bool isPvpPick = string.Equals(encType, "PvpEncounter", StringComparison.OrdinalIgnoreCase)
                                         || cid.StartsWith("pvp_", StringComparison.OrdinalIgnoreCase)
                                         || (curHour == 6 && string.Equals(runStateNow, "Choice", StringComparison.OrdinalIgnoreCase));
                        triedInState.Add(cid);
                        if (encType is "CombatEncounter" or "PvpEncounter") preCombatSnapshot = d;
                        var beforeSnap = d;
                        var (er, eMs) = await client.DriveWaitAsync(new { op = "selectEncounter", instanceId = cid, waitIdle = true, includeCompactState = true, includeUi = true });
                        RecordOpTiming(report, eMs, er);
                        if (er["ok"]?.Value<bool>() == true)
                        {
                            Console.Write("E");
                            if (isPvpPick) invariants.MarkPrePvp(beforeSnap);
                            var nextD = ExtractCompactOrNull(er);
                            if (nextD != null)
                            {
                                AuditOutOfCombatTransition(report, "selectEncounter", cid, beforeSnap, nextD, er);
                                d = nextD;
                            }
                            else d = null;
                            acted = true;
                        }
                    }
                }

                // 8c. 其次选买得起的 Skill
                if (!acted)
                {
                    var sk = candidates.FirstOrDefault(x =>
                    {
                        string cid = x["instanceId"]!.ToString();
                        int price = selIsFree ? 0 : (x["buyPrice"]?.Value<int>() ?? 0);
                        return !triedInState.Contains(cid) && price <= gold && x["type"]?.ToString() == "Skill";
                    });
                    if (sk != null)
                    {
                        string cid = sk["instanceId"]!.ToString();
                        triedInState.Add(cid);
                        var beforeSnap = d;
                        var (sr, sMs) = await client.DriveWaitAsync(new { op = "selectSkill", instanceId = cid, waitIdle = true, includeCompactState = true, includeUi = true });
                        RecordOpTiming(report, sMs, sr);
                        if (sr["ok"]?.Value<bool>() == true)
                        {
                            Console.Write("S");
                            var nextD = ExtractCompactOrNull(sr);
                            if (nextD != null) { AuditOutOfCombatTransition(report, "selectSkill", cid, beforeSnap, nextD, sr); d = nextD; }
                            else d = null;
                            acted = true;
                        }
                    }
                }

                // 8d. 商店买卡：二合一升阶卡绝对优先，其次按战力价值与当前阵容协同排序
                if (!acted)
                {
                    var shopItems = candidates
                        .Where(x =>
                        {
                            string cid = x["instanceId"]!.ToString();
                            string t = x["type"]?.ToString() ?? "Item";
                            int price = selIsFree ? 0 : (x["buyPrice"]?.Value<int>() ?? 9999);
                            return !triedInState.Contains(cid) && t == "Item" && price <= gold;
                        })
                        .Select(x => new
                        {
                            Item = x,
                            CanFuse = SmartStrategy.CanFuseWithOwned(x, allOwned),
                            Score = SmartStrategy.ScoreCombatPower(x, hand),
                            Price = selIsFree ? 0 : (x["buyPrice"]?.Value<int>() ?? 0),
                        })
                        .OrderByDescending(x => x.CanFuse ? 1 : 0)
                        .ThenByDescending(x => x.Score - x.Price * 0.4)
                        .ToList();

                    // 若背包近满（Stash 剩余 <= 1 格），先卖出低阶过渡卡腾格
                    int freeHand = SmartStrategy.CountFreeSlots(hand, SmartStrategy.GetUnlockedMask(d));
                    int freeStash = SmartStrategy.CountFreeSlots(stash, 1023);
                    if (freeHand == 0 && freeStash <= 1 && stashItemCount > 0)
                    {
                        var weak = SmartStrategy.FindWeakestSellableInStash(d, shopItems.Select(s => s.Item));
                        if (weak != null && !triedInState.Contains("sell_space:" + weak["instanceId"]))
                        {
                            string wid = weak["instanceId"]!.ToString();
                            triedInState.Add("sell_space:" + wid);
                            var beforeSnap = d;
                            var (sr, sMs) = await client.DriveWaitAsync(new { op = "sell", instanceId = wid, waitIdle = true, includeCompactState = true, includeUi = true });
                            RecordOpTiming(report, sMs, sr);
                            if (sr["ok"]?.Value<bool>() == true)
                            {
                                Console.Write("$");
                                var nextD = ExtractCompactOrNull(sr);
                                if (nextD != null) { AuditOutOfCombatTransition(report, "sell_for_space", wid, beforeSnap, nextD, sr); d = nextD; }
                                else d = null;
                                continue;
                            }
                        }
                    }

                    foreach (var entry in shopItems)
                    {
                        // 已有 3 张以上且战力明显不如手牌的昂贵铜卡不盲买，留钱等升阶/刷新
                        bool boardHasEnough = handItemCount >= 3 && freeHand == 0 && stashItemCount >= 4;
                        if (!entry.CanFuse && boardHasEnough && !selIsFree && entry.Score < 20 && gold >= 14)
                        {
                            continue;
                        }

                        var (br, _) = await TrySmartBuyAsync(client, report, d, entry.Item, triedInState, shopItems.Select(s => s.Item));
                        if (br != null && br["ok"]?.Value<bool>() == true)
                        {
                            Console.Write(entry.CanFuse ? "F" : "B");
                            d = ExtractCompactOrNull(br);
                            acted = true;
                            break;
                        }
                    }
                }

                // 8e. 金币充裕（>= 14）且本轮商店未购入合适商品时主动 Reroll
                if (!acted)
                {
                    int rerollCost = d["rerollCost"]?.Value<int>() ?? -1;
                    int rerollsRem = d["rerollsRemaining"]?.Value<int>() ?? 99;
                    bool hasShopItems = candidates.Any(x => x["type"]?.ToString() is "Item" or "Skill");
                    if (hasShopItems && gold >= 14 && rerollCost >= 0 && rerollCost <= gold && rerollsRem > 0 && rerollsInCurrentShop < 2)
                    {
                        rerollsInCurrentShop++;
                        triedInState.Clear();
                        var beforeSnap = d;
                        var (rr, rMs) = await client.DriveWaitAsync(new { op = "reroll", waitIdle = true, includeCompactState = true, includeUi = true });
                        RecordOpTiming(report, rMs, rr);
                        if (rr["ok"]?.Value<bool>() == true)
                        {
                            Console.Write("?");
                            var nextD = ExtractCompactOrNull(rr);
                            if (nextD != null) { AuditOutOfCombatTransition(report, "reroll", "shop", beforeSnap, nextD, rr); d = nextD; }
                            else d = null;
                            continue;
                        }
                    }
                }

                if (!acted)
                {
                    if (isChoiceState)
                    {
                        d = await SmartBotActions.HandleChoiceStateFallbackAsync(
                            client, triedInState, forceResync: false, (ms, resp) => RecordOpTiming(report, ms, resp));
                    }
                    else if (canExit)
                    {
                        preCombatSnapshot = d;
                        var beforeSnap = d;
                        var (xr, xMs) = await client.DriveWaitAsync(new { op = "exit", waitIdle = true, includeCompactState = true, includeUi = true });
                        RecordOpTiming(report, xMs, xr);
                        var nextD = ExtractCompactOrNull(xr);
                        if (nextD != null)
                        {
                            AuditOutOfCombatTransition(report, "exit", d["currentEncounterId"]?.ToString() ?? "", beforeSnap, nextD, xr);
                            d = nextD;
                        }
                        else d = null;
                        Console.Write(">");
                    }
                    else
                    {
                        triedInState.Clear();
                        var (sr, sMs) = await client.DriveWaitAsync(new { op = "sync", waitIdle = true, includeCompactState = true, includeUi = true });
                        RecordOpTiming(report, sMs, sr);
                        d = ExtractCompactOrNull(sr);
                        Console.Write("~");
                    }
                }
            }
            else
            {
                if (isChoiceState)
                {
                    d = await SmartBotActions.HandleChoiceStateFallbackAsync(
                        client, triedInState, forceResync: false, (ms, resp) => RecordOpTiming(report, ms, resp));
                }
                else if (string.Equals(runStateNow, "Encounter", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(appStateNow, "EncounterState", StringComparison.OrdinalIgnoreCase) &&
                         canExit)
                {
                    preCombatSnapshot = d;
                    var (xr, xMs) = await client.DriveWaitAsync(new { op = "exit", waitIdle = true, includeCompactState = true, includeUi = true });
                    RecordOpTiming(report, xMs, xr);
                    d = ExtractCompactOrNull(xr);
                    Console.Write(">");
                }
                else
                {
                    var (sr, sMs) = await client.DriveWaitAsync(new { op = "resync", waitIdle = false });
                    RecordOpTiming(report, sMs, sr);
                    Console.Write("~");
                    await Task.Delay(200);
                    d = null;
                }
            }
                }
                catch (Exception stepEx)
                {
                    string msg = $"[Step {step} D{report.FinalDay}H{report.FinalHour}] {stepEx.GetType().Name}: {stepEx.Message}";
                    Console.Error.WriteLine($"\n[Run {report.RunIndex}] 单步驱动异常: {msg}");
                    report.StepErrors.Add(msg);
                    report.Error = msg;
                    report.Completed = false;
                    report.Verdict = "DriverError:" + stepEx.GetType().Name;
                    await DrainCombatAuditsAsync(client, report, lastCombatSeq, seq => lastCombatSeq = seq);
                    if (cfg.AuditMode)
                    {
                        await SafeRecoverToMainMenuAsync(client);
                    }
                    break;
                }
            }
        }
        finally
        {
            sw.Stop();
            report.DurationSec = Math.Round(sw.Elapsed.TotalSeconds, 3);
            report.TotalSteps = step;
            report.EndedAtUtc = DateTime.UtcNow.ToString("O");
            if (!report.Completed && report.Verdict == "Incomplete")
            {
                report.Verdict = "MaxStepsReached";
            }
            FinalizeRunStats(report);
        }
        return report;
    }

    private static async Task<(JObject Resp, double ElapsedMs)> TrySmartBuyAsync(
        DriverClient client, RunReport report, JObject state, JObject candidate, HashSet<string> triedInState,
        IEnumerable<JObject> shopPool = null)
    {
        string cid = candidate["instanceId"]!.ToString();
        triedInState.Add(cid);
        var beforeSnap = state;

        var hand = state["hand"] as JArray ?? new JArray();
        var stash = state["stash"] as JArray ?? new JArray();
        int width = SmartStrategy.SizeWidth(candidate);
        int handSpan = SmartStrategy.MaxContiguousFreeSpan(hand, SmartStrategy.GetUnlockedMask(state));
        int stashSpan = SmartStrategy.MaxContiguousFreeSpan(stash, 1023);
        bool canFuse = SmartStrategy.CanFuseWithOwned(candidate, hand.Concat(stash).OfType<JObject>().Where(SmartStrategy.IsBoardItem));

        string primarySec = (canFuse || handSpan >= width) ? "Hand" : "Stash";
        var (br, bMs) = await client.DriveWaitAsync(new
        {
            op = "buy",
            instanceId = cid,
            section = primarySec,
            waitIdle = true,
            includeCompactState = true,
            includeUi = true
        });
        RecordOpTiming(report, bMs, br);

        if (br["ok"]?.Value<bool>() != true && primarySec == "Hand" && stashSpan >= width)
        {
            (br, bMs) = await client.DriveWaitAsync(new
            {
                op = "buy",
                instanceId = cid,
                section = "Stash",
                waitIdle = true,
                includeCompactState = true,
                includeUi = true
            });
            RecordOpTiming(report, bMs, br);
        }

        // 若手牌和背包都缺格，且该商品值得购买，则先卖一张最弱过渡卡腾格再买
        if (br["ok"]?.Value<bool>() != true && !canFuse && stashSpan < width)
        {
            var weak = SmartStrategy.FindWeakestSellableInStash(state, shopPool);
            if (weak != null)
            {
                string wid = weak["instanceId"]!.ToString();
                var (sr, sMs) = await client.DriveWaitAsync(new { op = "sell", instanceId = wid, waitIdle = true, includeCompactState = true, includeUi = true });
                RecordOpTiming(report, sMs, sr);
                if (sr["ok"]?.Value<bool>() == true)
                {
                    Console.Write("$");
                    var midState = ExtractCompactOrNull(sr);
                    if (midState != null) AuditOutOfCombatTransition(report, "sell_for_buy", wid, beforeSnap, midState, sr);
                    (br, bMs) = await client.DriveWaitAsync(new
                    {
                        op = "buy",
                        instanceId = cid,
                        section = "Stash",
                        waitIdle = true,
                        includeCompactState = true,
                        includeUi = true
                    });
                    RecordOpTiming(report, bMs, br);
                }
            }
        }

        if (br["ok"]?.Value<bool>() == true)
        {
            var nextD = ExtractCompactOrNull(br);
            if (nextD != null)
            {
                AuditOutOfCombatTransition(report, canFuse ? "buy_fuse" : "buy", cid, beforeSnap, nextD, br);
                SamplePerf(report, nextD, isStart: false);
            }
        }
        return (br, bMs);
    }

    private static JObject ExtractCompactOrNull(JObject actionResp)
        => actionResp?["compactState"] as JObject;

    private static void RecordOpTiming(RunReport report, double elapsedMs, JObject resp)
    {
        report.OpsCount++;
        report.ActionEndToEndMs.Add(elapsedMs);
        var cs = resp?["compactState"] as JObject;
        var perf = cs?["perf"] as JObject;
        if (perf != null)
        {
            double opMs = perf["lastMainThreadOpMs"]?.Value<double>() ?? (cs["inspectMs"]?.Value<double>() ?? 0);
            if (opMs > 0) report.DriverMainThreadMs.Add(opMs);
        }
    }

    private static void SamplePerf(RunReport report, JObject state, bool isStart)
    {
        var perf = state?["perf"] as JObject;
        if (perf == null) return;

        double fps = perf["fps"]?.Value<double>() ?? 0;
        double minFps = perf["minFpsWindow"]?.Value<double>() ?? 0;
        double heap = perf["monoHeapMB"]?.Value<double>() ?? 0;
        double opMs = perf["lastMainThreadOpMs"]?.Value<double>() ?? (state["inspectMs"]?.Value<double>() ?? 0);
        int g0 = perf["gcGen0"]?.Value<int>() ?? 0;
        int g1 = perf["gcGen1"]?.Value<int>() ?? 0;
        int g2 = perf["gcGen2"]?.Value<int>() ?? 0;

        if (fps > 0) report.FpsSamples.Add(fps);
        if (minFps > 0) report.MinWindowFps = report.MinWindowFps <= 0 ? minFps : Math.Min(report.MinWindowFps, minFps);
        if (opMs > 0) report.DriverMainThreadMs.Add(opMs);
        if (heap > 0)
        {
            if (isStart || report.MonoHeapStartMB <= 0) report.MonoHeapStartMB = heap;
            report.MonoHeapEndMB = heap;
            if (heap > report.MonoHeapPeakMB) report.MonoHeapPeakMB = heap;
        }
        if (isStart)
        {
            report.GcStart = (g0, g1, g2);
        }
        report.GcEnd = (g0, g1, g2);
    }

    // ── ① UI 宝石与品阶一致性核验 ───────────────────────────────────────────
    private static async Task AuditUiConsistencyAsync(
        DriverClient client, RunReport report, JObject state, HashSet<string> dedup)
    {
        var candidates = CollectUiCheckMismatches(state, report);
        if (candidates.Count == 0) return;

        // 仅当发现潜在差异时，等待 550ms 排除 SetTextAnimatedPunch 0.5s 跳字补间与 Addressables 异步框体加载后二次复核
        await Task.Delay(550);
        var (settled, _) = await client.InspectCompactAsync(ui: true);
        SamplePerf(report, settled, isStart: false);

        var dummy = new RunReport();
        var confirmed = CollectUiCheckMismatches(settled, dummy);
        if (confirmed.Count == 0) return;

        foreach (var m in confirmed)
        {
            string key = $"{m.InstanceId}|{m.Field}|{m.ServerValue}|{m.UiValue}";
            if (dedup.Add(key))
            {
                report.UiMismatches.Add(m);
            }
        }
    }

    private static List<UiMismatchEntry> CollectUiCheckMismatches(JObject state, RunReport report)
    {
        var mismatches = new List<UiMismatchEntry>();
        int day = state["day"]?.Value<int>() ?? 0;
        int hour = state["hour"]?.Value<int>() ?? 0;
        string appState = state["appState"]?.ToString() ?? "";

        foreach (string zone in new[] { "hand", "stash", "selection", "skills" })
        {
            var arr = state[zone] as JArray;
            if (arr == null) continue;

            foreach (var card in arr.OfType<JObject>())
            {
                var ui = card["ui"] as JObject;
                if (ui == null || ui["hasController"]?.Value<bool>() != true) continue;
                if (ui["isCardVisible"]?.Value<bool>() != true) continue;
                if (ui["activeInHierarchy"]?.Value<bool>() != true) continue;

                report.UiChecksCount++;
                string iid = card["instanceId"]?.ToString() ?? "";
                string tpl = card["templateId"]?.ToString() ?? "";
                string name = card["name"]?.ToString() ?? "";

                string serverTier = card["tier"]?.ToString() ?? "";
                string uiTier = ui["uiTier"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(serverTier) && !string.IsNullOrEmpty(uiTier) &&
                    !string.Equals(serverTier, uiTier, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add(new UiMismatchEntry(day, hour, appState, zone, iid, tpl, name, "tier", serverTier, uiTier));
                }

                if (zone == "skills") continue;

                CheckGemField("damageAmount", "uiDamageText");
                CheckGemField("shieldApplyAmount", "uiShieldText");
                CheckGemField("burnApplyAmount", "uiBurnText");
                CheckGemField("poisonApplyAmount", "uiPoisonText");

                void CheckGemField(string attrField, string uiField)
                {
                    int srvVal = card[attrField]?.Value<int>() ?? 0;
                    string uiTxt = ui[uiField]?.ToString();
                    if (srvVal > 0 && uiTxt != null && uiTxt != srvVal.ToString())
                    {
                        mismatches.Add(new UiMismatchEntry(day, hour, appState, zone, iid, tpl, name, attrField, srvVal.ToString(), uiTxt));
                    }
                }
            }
        }
        return mismatches;
    }

    // ── ② 局外操作与战后永久成长真实性审计 ───────────────────────────────────
    private static void AuditOutOfCombatTransition(
        RunReport report, string op, string targetId, JObject before, JObject after, JObject actionResp)
    {
        if (before == null || after == null) return;

        var entry = new OutOfCombatCheck
        {
            Op = op,
            TargetId = targetId,
            DayBefore = before["day"]?.Value<int>() ?? 0,
            HourBefore = before["hour"]?.Value<int>() ?? 0,
            DayAfter = after["day"]?.Value<int>() ?? 0,
            HourAfter = after["hour"]?.Value<int>() ?? 0,
            GoldDelta = (after["gold"]?.Value<int>() ?? 0) - (before["gold"]?.Value<int>() ?? 0),
            IncomeDelta = (after["income"]?.Value<int>() ?? 0) - (before["income"]?.Value<int>() ?? 0),
            HealthMaxDelta = (after["healthMax"]?.Value<int>() ?? 0) - (before["healthMax"]?.Value<int>() ?? 0),
            XpDelta = (after["experience"]?.Value<int>() ?? 0) - (before["experience"]?.Value<int>() ?? 0),
        };

        var beforeMap = IndexOwnedCards(before);
        var afterMap = IndexOwnedCards(after);

        foreach (var kv in afterMap)
        {
            if (!beforeMap.TryGetValue(kv.Key, out var bCard))
            {
                entry.SpawnedCards.Add($"{kv.Value["name"]}({kv.Key})");
                continue;
            }
            var diffs = DiffCardAttrs(bCard, kv.Value);
            if (diffs.Count > 0)
            {
                entry.ModifiedCards.Add($"{kv.Value["name"]}:{string.Join(",", diffs)}");
            }
        }

        if (op == "move" && entry.ModifiedCards.Count > 0)
        {
            report.AuraMoveDeltaCount += entry.ModifiedCards.Count;
        }
        if (op == "consumable" && entry.GoldDelta == 0 && entry.IncomeDelta == 0 && entry.HealthMaxDelta == 0 &&
            entry.XpDelta == 0 && entry.SpawnedCards.Count == 0 && entry.ModifiedCards.Count == 0)
        {
            report.OutOfCombatAnomalies.Add($"consumable {targetId} produced 0 observable delta at D{entry.DayBefore}H{entry.HourBefore}");
        }

        report.OutOfCombatChecks.Add(entry);
    }

    private static void AuditPostCombatGrowth(RunReport report, JObject preCombat, JObject postCombat)
    {
        var beforeMap = IndexOwnedCards(preCombat);
        var afterMap = IndexOwnedCards(postCombat);

        foreach (var kv in afterMap)
        {
            if (!beforeMap.TryGetValue(kv.Key, out var bCard)) continue;
            var diffs = DiffCardAttrs(bCard, kv.Value);
            if (diffs.Count > 0)
            {
                report.PermCombatGrowths.Add($"{kv.Value["name"]}({kv.Key}): {string.Join(", ", diffs)}");
            }
        }
    }

    private static Dictionary<string, JObject> IndexOwnedCards(JObject state)
    {
        var map = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (string zone in new[] { "hand", "stash", "skills" })
        {
            var arr = state?[zone] as JArray;
            if (arr == null) continue;
            foreach (var c in arr.OfType<JObject>())
            {
                string iid = c["instanceId"]?.ToString();
                if (!string.IsNullOrEmpty(iid)) map[iid] = c;
            }
        }
        return map;
    }

    private static List<string> DiffCardAttrs(JObject before, JObject after)
    {
        var diffs = new List<string>();
        string t1 = before["tier"]?.ToString() ?? "";
        string t2 = after["tier"]?.ToString() ?? "";
        if (t1 != t2) diffs.Add($"tier:{t1}->{t2}");

        string e1 = before["enchantment"]?.ToString() ?? "";
        string e2 = after["enchantment"]?.ToString() ?? "";
        if (e1 != e2) diffs.Add($"ench:{e1}->{e2}");

        foreach (string field in new[]
        {
            "damageAmount", "shieldApplyAmount", "burnApplyAmount", "poisonApplyAmount",
            "healAmount", "regenApplyAmount", "critChance", "multicast", "cooldownMax",
            "sellPrice", "value", "counter"
        })
        {
            int v1 = before[field]?.Value<int>() ?? 0;
            int v2 = after[field]?.Value<int>() ?? 0;
            if (v1 != v2) diffs.Add($"{field}:{v1}->{v2}");
        }
        return diffs;
    }

    private static void TrackTouchedEntities(RunReport report, JObject state)
    {
        int day = state["day"]?.Value<int>() ?? 0;
        int hour = state["hour"]?.Value<int>() ?? 0;
        foreach (string zone in new[] { "hand", "stash", "skills", "socketEffects", "selection" })
        {
            var arr = state[zone] as JArray;
            if (arr == null) continue;
            foreach (var c in arr.OfType<JObject>())
            {
                string iid = c["instanceId"]?.ToString();
                if (string.IsNullOrEmpty(iid)) continue;
                if (!report.TouchedEntities.TryGetValue(iid, out var te))
                {
                    te = new TouchedEntity
                    {
                        InstanceId = iid,
                        TemplateId = c["templateId"]?.ToString() ?? "",
                        Name = c["name"]?.ToString() ?? "",
                        Type = c["type"]?.ToString() ?? "",
                        FirstTier = c["tier"]?.ToString() ?? "",
                        FirstSeen = $"D{day}H{hour}",
                    };
                    report.TouchedEntities[iid] = te;
                }
                te.LatestTier = c["tier"]?.ToString() ?? te.LatestTier;
                string ench = c["enchantment"]?.ToString();
                if (!string.IsNullOrEmpty(ench)) te.Enchantments.Add(ench);
                te.ZonesSeen.Add(zone);
            }
        }
    }

    // ── ③ 战斗触发明细审计（揪出 0 触发哑巴卡）───────────────────────────────
    private static async Task<JObject> WaitForPostPvpSettledAsync(
        DriverClient client,
        JObject current,
        RunInvariantTracker invariants,
        Action<JObject> samplePerf)
    {
        var sw = Stopwatch.StartNew();
        JObject latest = current;
        bool polled = false;
        while (sw.ElapsedMilliseconds < 1500)
        {
            if (latest != null && invariants.IsPostPvpSnapshotReady(latest))
            {
                if (polled)
                {
                    try
                    {
                        var (full, _) = await client.InspectCompactAsync(ui: true);
                        samplePerf?.Invoke(full);
                        return full;
                    }
                    catch { }
                }
                return latest;
            }
            await Task.Delay(60);
            try
            {
                var (next, _) = await client.InspectCompactAsync(ui: false);
                samplePerf?.Invoke(next);
                latest = next;
                polled = true;
            }
            catch
            {
                break;
            }
        }
        return latest;
    }

    private static async Task DrainCombatAuditsAsync(
        DriverClient client, RunReport report, int lastCombatSeq, Action<int> updateSeq,
        Action<bool> onCombatAudit = null)
    {
        try
        {
            var (resp, _) = await client.CombatAuditSinceAsync(lastCombatSeq);
            var audits = resp["audits"] as JArray;
            if (audits == null) return;

            foreach (var a in audits.OfType<JObject>())
            {
                int seq = a["combatSeq"]?.Value<int>() ?? 0;
                if (seq > lastCombatSeq)
                {
                    lastCombatSeq = seq;
                    updateSeq(seq);
                }

                int frameCount = a["frameCount"]?.Value<int>() ?? 0;
                int durationMs = frameCount * 50;
                int day = a["day"]?.Value<int>() ?? 0;
                int hour = a["hour"]?.Value<int>() ?? 0;
                bool isPvp = a["isPvp"]?.Value<bool>() == true;
                onCombatAudit?.Invoke(isPvp);
                string winner = a["winner"]?.ToString() ?? "";

                report.CombatsCount++;
                var cards = a["playerCards"] as JArray ?? new JArray();
                foreach (var c in cards.OfType<JObject>())
                {
                    string iid = c["instanceId"]?.ToString() ?? "";
                    string tpl = c["templateId"]?.ToString() ?? "";
                    string name = c["name"]?.ToString() ?? "";
                    string type = c["type"]?.ToString() ?? "";
                    string tier = c["tier"]?.ToString() ?? "";
                    int cdMax = c["cooldownMax"]?.Value<int>() ?? 0;
                    int trig = c["triggerCount"]?.Value<int>() ?? 0;
                    int trigSrc = c["triggerSourceCount"]?.Value<int>() ?? 0;
                    int aura = c["auraCount"]?.Value<int>() ?? 0;

                    if (!report.CombatCardSummary.TryGetValue(tpl, out var sum))
                    {
                        sum = new CombatCardAgg { TemplateId = tpl, Name = name, Type = type, CooldownMax = cdMax };
                        report.CombatCardSummary[tpl] = sum;
                    }
                    sum.CombatsDeployed++;
                    sum.TotalTriggers += trig;
                    sum.TotalTriggerSources += trigSrc;
                    sum.TotalAuras += aura;

                    if (cdMax > 0 && trig == 0 && trigSrc == 0)
                    {
                        sum.ZeroTriggerCombats++;
                        bool couldFire = durationMs >= cdMax + 500;
                        var z = new ZeroTriggerEntry(seq, day, hour, isPvp, winner, iid, tpl, name, tier, cdMax, durationMs, couldFire);
                        report.AllZeroTriggerEvents.Add(z);
                        if (couldFire) report.LikelyZeroTriggerCards.Add(z);
                    }
                }
            }
        }
        catch { }
    }

    private static void FinalizeRunStats(RunReport report)
    {
        report.OpsPerSec = report.DurationSec > 0 ? Math.Round(report.OpsCount / report.DurationSec, 2) : 0;
        report.FpsAvg = report.FpsSamples.Count > 0 ? Math.Round(report.FpsSamples.Average(), 1) : 0;
        report.FpsP10 = report.FpsSamples.Count > 0 ? Math.Round(Percentile(report.FpsSamples, 0.10), 1) : 0;
        report.MonoHeapDeltaMB = Math.Round(report.MonoHeapEndMB - report.MonoHeapStartMB, 2);
        report.DriverOpStats = ComputeStats(report.DriverMainThreadMs);
        report.ActionE2EStats = ComputeStats(report.ActionEndToEndMs);
    }

    private static LatencyStats ComputeStats(List<double> samples)
    {
        if (samples.Count == 0) return new LatencyStats();
        var sorted = samples.OrderBy(x => x).ToList();
        return new LatencyStats
        {
            Count = sorted.Count,
            AvgMs = Math.Round(sorted.Average(), 2),
            P50Ms = Math.Round(PercentileSorted(sorted, 0.50), 2),
            P95Ms = Math.Round(PercentileSorted(sorted, 0.95), 2),
            MaxMs = Math.Round(sorted[^1], 2),
        };
    }

    private static double Percentile(List<double> samples, double p)
        => PercentileSorted(samples.OrderBy(x => x).ToList(), p);

    private static double PercentileSorted(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int idx = Math.Clamp((int)Math.Floor(p * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[idx];
    }

    private static JObject BuildAggregateReport(Config cfg, List<RunReport> runs, List<JObject> heroFallbacks, double totalSec)
    {
        var allFps = runs.SelectMany(r => r.FpsSamples).ToList();
        var allDriver = runs.SelectMany(r => r.DriverMainThreadMs).ToList();
        var allE2E = runs.SelectMany(r => r.ActionEndToEndMs).ToList();
        int totalOps = runs.Sum(r => r.OpsCount);
        int totalSteps = runs.Sum(r => r.TotalSteps);

        double startHeap = runs.FirstOrDefault()?.MonoHeapStartMB ?? 0;
        double endHeap = runs.LastOrDefault()?.MonoHeapEndMB ?? 0;
        double peakHeap = runs.Count > 0 ? runs.Max(r => r.MonoHeapPeakMB) : 0;
        int totalWlViolations = runs.Sum(r => r.WlInvariantViolations);
        int totalPrestigeViolations = runs.Sum(r => r.PrestigeInvariantViolations);
        int totalWlChecks = runs.Sum(r => r.WlChecksCount);
        int totalPvpAudited = runs.Sum(r => r.PvpAuditedCount);
        bool allInvariantsPassed = totalWlViolations == 0 && totalPrestigeViolations == 0;
        var invariantAlerts = new List<string>();
        foreach (var r in runs)
        {
            foreach (string msg in r.WlViolationDetails)
                invariantAlerts.Add($"[Run {r.RunIndex}][WL] {msg}");
            foreach (string msg in r.PrestigeViolationDetails)
                invariantAlerts.Add($"[Run {r.RunIndex}][Prestige] {msg}");
        }

        var agg = new JObject
        {
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["config"] = new JObject
            {
                ["runsRequested"] = cfg.Runs,
                ["runsCompleted"] = runs.Count(r => r.Completed),
                ["speed"] = cfg.Speed,
                ["heroes"] = new JArray(cfg.Heroes),
                ["heroFallbackMode"] = cfg.HeroFallbackMode,
            },
            ["heroFallbacks"] = new JArray(heroFallbacks),
            ["invariantSummary"] = new JObject
            {
                ["passed"] = allInvariantsPassed,
                ["status"] = allInvariantsPassed ? "PASS" : "FAIL",
                ["wlChecks"] = totalWlChecks,
                ["wlInvariantViolations"] = totalWlViolations,
                ["pvpAudited"] = totalPvpAudited,
                ["prestigeInvariantViolations"] = totalPrestigeViolations,
                ["alerts"] = new JArray(invariantAlerts),
            },
            ["summary"] = new JObject
            {
                ["totalDurationSec"] = Math.Round(totalSec, 2),
                ["totalSteps"] = totalSteps,
                ["totalOps"] = totalOps,
                ["overallOpsPerSec"] = totalSec > 0 ? Math.Round(totalOps / totalSec, 2) : 0,
                ["clientFpsAvg"] = allFps.Count > 0 ? Math.Round(allFps.Average(), 1) : 0,
                ["clientFpsP10"] = allFps.Count > 0 ? Math.Round(Percentile(allFps, 0.10), 1) : 0,
                ["monoHeapStartMB"] = startHeap,
                ["monoHeapEndMB"] = endHeap,
                ["monoHeapDeltaMB"] = Math.Round(endHeap - startHeap, 2),
                ["monoHeapPeakMB"] = peakHeap,
                ["driverMainThreadOpMs"] = JObject.FromObject(ComputeStats(allDriver)),
                ["actionEndToEndMs"] = JObject.FromObject(ComputeStats(allE2E)),
                ["totalCombats"] = runs.Sum(r => r.CombatsCount),
                ["totalUiChecks"] = runs.Sum(r => r.UiChecksCount),
                ["totalUiMismatches"] = runs.Sum(r => r.UiMismatches.Count),
                ["totalOutOfCombatChecks"] = runs.Sum(r => r.OutOfCombatChecks.Count),
                ["totalAuraMoveDeltas"] = runs.Sum(r => r.AuraMoveDeltaCount),
                ["totalPermCombatGrowths"] = runs.Sum(r => r.PermCombatGrowths.Count),
                ["totalLikelyZeroTriggerCards"] = runs.Sum(r => r.LikelyZeroTriggerCards.Count),
                ["totalOutOfCombatAnomalies"] = runs.Sum(r => r.OutOfCombatAnomalies.Count),
                ["invariantsPassed"] = allInvariantsPassed,
                ["totalWlChecks"] = totalWlChecks,
                ["wlInvariantViolations"] = totalWlViolations,
                ["totalPvpAudited"] = totalPvpAudited,
                ["prestigeInvariantViolations"] = totalPrestigeViolations,
            },
            ["runs"] = JArray.FromObject(runs.Select(r => new
            {
                r.RunIndex,
                r.Hero,
                r.RequestedHero,
                r.HeroFallbackReason,
                r.Completed,
                r.Verdict,
                r.Error,
                r.StepErrors,
                r.FinalDay,
                r.FinalHour,
                r.Victories,
                r.Losses,
                r.FinalPrestige,
                r.FinalLevel,
                r.DurationSec,
                r.TotalSteps,
                r.OpsCount,
                r.OpsPerSec,
                r.FpsAvg,
                r.FpsP10,
                r.MinWindowFps,
                r.MonoHeapStartMB,
                r.MonoHeapEndMB,
                r.MonoHeapDeltaMB,
                r.MonoHeapPeakMB,
                gcStart = new { gen0 = r.GcStart.g0, gen1 = r.GcStart.g1, gen2 = r.GcStart.g2 },
                gcEnd = new { gen0 = r.GcEnd.g0, gen1 = r.GcEnd.g1, gen2 = r.GcEnd.g2 },
                r.DriverOpStats,
                r.ActionE2EStats,
                r.CombatsCount,
                r.UiChecksCount,
                uiMismatches = r.UiMismatches,
                outOfCombatChecksCount = r.OutOfCombatChecks.Count,
                r.AuraMoveDeltaCount,
                r.PermCombatGrowths,
                r.OutOfCombatAnomalies,
                likelyZeroTriggerCards = r.LikelyZeroTriggerCards,
                allZeroTriggerCount = r.AllZeroTriggerEvents.Count,
                combatCardSummary = r.CombatCardSummary.Values.ToList(),
                touchedEntitiesCount = r.TouchedEntities.Count,
                touchedEntities = r.TouchedEntities.Values.ToList(),
                invariantPassed = r.InvariantPassed,
                invariantSummary = r.InvariantSummary,
                r.WlChecksCount,
                r.WlInvariantViolations,
                r.WlViolationDetails,
                r.PvpAuditedCount,
                r.PrestigeInvariantViolations,
                r.PrestigeViolationDetails,
                r.StartedAtUtc,
                r.EndedAtUtc,
            })),
        };
        return agg;
    }

    // ── 报告模型 ──────────────────────────────────────────────────────────────
    internal sealed class RunReport
    {
        public int RunIndex;
        public string Hero;
        public string RequestedHero;
        public string HeroFallbackReason;
        public bool Completed;
        public string Verdict = "Incomplete";
        public string Error;
        public readonly List<string> StepErrors = new();
        public int FinalDay;
        public int FinalHour;
        public int Victories;
        public int Losses;
        public int FinalPrestige;
        public int FinalLevel;
        public double DurationSec;
        public int TotalSteps;
        public int OpsCount;
        public double OpsPerSec;
        public double FpsAvg;
        public double FpsP10;
        public double MinWindowFps;
        public double MonoHeapStartMB;
        public double MonoHeapEndMB;
        public double MonoHeapDeltaMB;
        public double MonoHeapPeakMB;
        public (int g0, int g1, int g2) GcStart;
        public (int g0, int g1, int g2) GcEnd;
        public LatencyStats DriverOpStats = new();
        public LatencyStats ActionE2EStats = new();
        public int CombatsCount;
        public int UiChecksCount;
        public int AuraMoveDeltaCount;
        public string StartedAtUtc;
        public string EndedAtUtc;

        [JsonIgnore] public readonly List<double> FpsSamples = new();
        [JsonIgnore] public readonly List<double> DriverMainThreadMs = new();
        [JsonIgnore] public readonly List<double> ActionEndToEndMs = new();

        public readonly List<UiMismatchEntry> UiMismatches = new();
        public readonly List<OutOfCombatCheck> OutOfCombatChecks = new();
        public readonly List<string> OutOfCombatAnomalies = new();
        public readonly List<string> PermCombatGrowths = new();
        public readonly List<ZeroTriggerEntry> AllZeroTriggerEvents = new();
        public readonly List<ZeroTriggerEntry> LikelyZeroTriggerCards = new();
        public readonly Dictionary<string, CombatCardAgg> CombatCardSummary = new(StringComparer.Ordinal);
        public readonly Dictionary<string, TouchedEntity> TouchedEntities = new(StringComparer.Ordinal);

        public int WlChecksCount;
        public int PvpAuditedCount;
        public readonly List<string> WlViolationDetails = new();
        public readonly List<string> PrestigeViolationDetails = new();
        public int WlInvariantViolations => WlViolationDetails.Count;
        public int PrestigeInvariantViolations => PrestigeViolationDetails.Count;
        public bool InvariantPassed => WlInvariantViolations == 0 && PrestigeInvariantViolations == 0;
        public string InvariantSummary =>
            $"status={(InvariantPassed ? "PASS" : "FAIL")}, wlChecks={WlChecksCount}, wlViolations={WlInvariantViolations}, pvpAudited={PvpAuditedCount}, prestigeViolations={PrestigeInvariantViolations}";
    }

    /// <summary>
    /// 官方核心规则实时不变量审计器：
    ///   1. 胜负场与天数严格匹配：进入第 D 天后在 Hour&lt;6 或 Hour==6 ChoiceState 下必须满足 V+L == D-1；终局时 V+L == D。
    ///   2. PvP 胜负与声望/Fates 严格匹配：胜则 V+1 且不掉荣誉；负则 L+1 且扣 Day 点荣誉，首次 &lt;=0 触发 Fates 恢复为正，二次 &lt;=0 立即 EndRunDefeat；满 10 胜立即 EndRunVictory。
    /// </summary>
    internal sealed class RunInvariantTracker
    {
        private const string FuturaTemplateId = "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
        private readonly RunReport _report;
        private readonly HashSet<string> _dedup = new(StringComparer.Ordinal);
        private JObject _prePvpSnap;
        private bool _fatesTriggered;
        private bool _terminalChecked;

        public bool PendingPvpAudit { get; private set; }

        public RunInvariantTracker(RunReport report)
        {
            _report = report;
        }

        public void MarkPrePvp(JObject snap)
        {
            if (snap == null) return;
            _prePvpSnap = snap;
            PendingPvpAudit = true;
        }

        public void MarkPrePvpIfNotPending(JObject snap)
        {
            if (!PendingPvpAudit && snap != null)
            {
                MarkPrePvp(snap);
            }
        }

        public bool IsPostPvpSnapshotReady(JObject snap)
        {
            if (snap == null) return false;
            if (SmartStrategy.IsTerminalState(snap)) return true;

            string app = snap["appState"]?.ToString() ?? "";
            string run = snap["runState"]?.ToString() ?? "";
            if (app is "CombatState" or "PVPCombatState" or "ReplayState" || run is "Combat" or "PVPCombat")
                return false;
            if (!SmartStrategy.AreStatesAligned(snap))
                return false;

            if (_prePvpSnap != null)
            {
                int preV = _prePvpSnap["victories"]?.Value<int>() ?? 0;
                int preL = _prePvpSnap["losses"]?.Value<int>() ?? 0;
                int postV = snap["victories"]?.Value<int>() ?? 0;
                int postL = snap["losses"]?.Value<int>() ?? 0;
                if (postV == preV && postL == preL) return false;
            }
            return true;
        }

        public void CheckStableWl(JObject snap)
        {
            if (snap == null || snap["inRun"]?.Value<bool>() != true) return;
            if (SmartStrategy.IsTerminalState(snap) || !SmartStrategy.AreStatesAligned(snap)) return;

            int day = snap["day"]?.Value<int>() ?? 0;
            int hour = snap["hour"]?.Value<int>() ?? 0;
            int v = snap["victories"]?.Value<int>() ?? 0;
            int l = snap["losses"]?.Value<int>() ?? 0;
            string app = snap["appState"]?.ToString() ?? "";

            if (v >= 10)
            {
                string k10 = $"v10:{day}:{hour}:{v}:{l}:{app}";
                if (_dedup.Add(k10))
                {
                    RecordPrestigeViolation($"Victories={v} (>=10) at D{day}H{hour} ({app}) without EndRunVictory");
                }
            }

            if (day >= 2 && (hour < 6 || (hour == 6 && app == "ChoiceState")))
            {
                string key = $"wl:{day}:{hour}:{app}:{v}:{l}";
                if (_dedup.Add(key))
                {
                    _report.WlChecksCount++;
                    if (v + l != day - 1)
                    {
                        RecordWlViolation($"D{day}H{hour} ({app}): Victories({v})+Losses({l})={v + l} != Day-1({day - 1})");
                    }
                }
            }
        }

        public void CheckTerminalWl(JObject snap)
        {
            if (_terminalChecked || snap == null) return;
            _terminalChecked = true;

            int day = snap["day"]?.Value<int>() ?? 0;
            int hour = snap["hour"]?.Value<int>() ?? 0;
            int v = snap["victories"]?.Value<int>() ?? 0;
            int l = snap["losses"]?.Value<int>() ?? 0;
            string runSt = snap["runState"]?.ToString() ?? "";
            string appSt = snap["appState"]?.ToString() ?? "";

            _report.WlChecksCount++;
            if (v + l != day)
            {
                RecordWlViolation($"Terminal {runSt}/{appSt} at D{day}H{hour}: Victories({v})+Losses({l})={v + l} != Day({day})");
            }
            if ((runSt == "EndRunVictory" || appSt == "EndRunVictoryState") && v < 10)
            {
                RecordPrestigeViolation($"EndRunVictory at D{day}H{hour} with Victories={v} < 10");
            }
        }

        public bool AuditPostPvpIfPending(JObject postSnap, bool isTerminal)
        {
            if (!PendingPvpAudit || _prePvpSnap == null || postSnap == null) return false;

            string postApp = postSnap["appState"]?.ToString() ?? "";
            string postRun = postSnap["runState"]?.ToString() ?? "";
            if (!isTerminal &&
                (postApp is "CombatState" or "PVPCombatState" or "ReplayState" ||
                 postRun is "Combat" or "PVPCombat" ||
                 !SmartStrategy.AreStatesAligned(postSnap)))
            {
                return false;
            }

            PendingPvpAudit = false;
            _report.PvpAuditedCount++;

            int preDay = _prePvpSnap["day"]?.Value<int>() ?? 0;
            int preV = _prePvpSnap["victories"]?.Value<int>() ?? 0;
            int preL = _prePvpSnap["losses"]?.Value<int>() ?? 0;
            int preP = _prePvpSnap["prestige"]?.Value<int>() ?? 0;

            int postV = postSnap["victories"]?.Value<int>() ?? 0;
            int postL = postSnap["losses"]?.Value<int>() ?? 0;
            int postP = postSnap["prestige"]?.Value<int>() ?? 0;
            string postEnc = postSnap["currentEncounterId"]?.ToString() ?? "";

            int dv = postV - preV;
            int dl = postL - preL;
            bool endedVictory = postRun == "EndRunVictory" || postApp == "EndRunVictoryState";
            bool endedDefeat = postRun == "EndRunDefeat" || postApp == "EndRunDefeatState";

            if (dv == 1 && dl == 0)
            {
                if (postP < preP)
                {
                    RecordPrestigeViolation($"PvP Win D{preDay} dropped prestige: {preP}->{postP}");
                }
                if (postV >= 10 && !endedVictory)
                {
                    RecordPrestigeViolation($"PvP Win #10 at D{preDay} did not immediately EndRunVictory (state={postRun}/{postApp})");
                }
                else if (postV < 10 && (endedVictory || endedDefeat))
                {
                    RecordPrestigeViolation($"PvP Win #{postV} (<10) at D{preDay} ended run unexpectedly (state={postRun}/{postApp})");
                }
            }
            else if (dv == 0 && dl == 1)
            {
                int expectedRaw = preP - preDay;
                if (expectedRaw > 0)
                {
                    if (postP != expectedRaw)
                    {
                        RecordPrestigeViolation($"PvP Loss D{preDay} prestige mismatch: {preP}-{preDay}={expectedRaw}, got {postP}");
                    }
                    if (endedDefeat)
                    {
                        RecordPrestigeViolation($"PvP Loss D{preDay} with remaining prestige={postP} ended in EndRunDefeat");
                    }
                }
                else
                {
                    if (!_fatesTriggered)
                    {
                        _fatesTriggered = true;
                        if (postP <= 0 || endedDefeat)
                        {
                            RecordPrestigeViolation($"PvP Loss D{preDay} first prestige<=0 (raw={expectedRaw}) failed Fates recovery: postP={postP}, state={postRun}/{postApp}");
                        }
                    }
                    else
                    {
                        if (postP > 0 || !endedDefeat)
                        {
                            RecordPrestigeViolation($"PvP Loss D{preDay} second prestige<=0 (raw={expectedRaw}) did not EndRunDefeat: postP={postP}, state={postRun}/{postApp}");
                        }
                    }
                }
            }
            else
            {
                RecordPrestigeViolation($"PvP D{preDay} invalid W/L delta: V {preV}->{postV}, L {preL}->{postL}");
            }

            if (string.Equals(postEnc, FuturaTemplateId, StringComparison.OrdinalIgnoreCase))
            {
                _fatesTriggered = true;
            }
            return true;
        }

        private void RecordWlViolation(string msg)
        {
            _report.WlViolationDetails.Add(msg);
            Console.Error.WriteLine($"\n[ALERT][Run {_report.RunIndex}][WL] {msg}");
        }

        private void RecordPrestigeViolation(string msg)
        {
            _report.PrestigeViolationDetails.Add(msg);
            Console.Error.WriteLine($"\n[ALERT][Run {_report.RunIndex}][Prestige] {msg}");
        }
    }

    internal sealed class LatencyStats
    {
        public int Count { get; set; }
        public double AvgMs { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double MaxMs { get; set; }
    }

    internal sealed record UiMismatchEntry(
        int Day, int Hour, string AppState, string Zone, string InstanceId, string TemplateId, string Name,
        string Field, string ServerValue, string UiValue);

    internal sealed class OutOfCombatCheck
    {
        public string Op;
        public string TargetId;
        public int DayBefore, HourBefore, DayAfter, HourAfter;
        public int GoldDelta, IncomeDelta, HealthMaxDelta, XpDelta;
        public readonly List<string> SpawnedCards = new();
        public readonly List<string> ModifiedCards = new();
    }

    internal sealed class TouchedEntity
    {
        public string InstanceId;
        public string TemplateId;
        public string Name;
        public string Type;
        public string FirstTier;
        public string LatestTier;
        public string FirstSeen;
        public readonly HashSet<string> Enchantments = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ZonesSeen = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class CombatCardAgg
    {
        public string TemplateId;
        public string Name;
        public string Type;
        public int CooldownMax;
        public int CombatsDeployed;
        public int TotalTriggers;
        public int TotalTriggerSources;
        public int TotalAuras;
        public int ZeroTriggerCombats;
    }

    internal sealed record ZeroTriggerEntry(
        int CombatSeq, int Day, int Hour, bool IsPvp, string Winner,
        string InstanceId, string TemplateId, string Name, string Tier,
        int CooldownMax, int CombatDurationMs, bool CouldHaveFired);
}
