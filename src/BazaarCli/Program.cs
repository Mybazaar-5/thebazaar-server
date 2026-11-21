using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarCli;

/// <summary>
/// bz —— 命令行驱动《The Bazaar》对局。
///
/// 全程进程内操作，不碰鼠标、不抢前台、不做识图。
/// 通过游戏内 BepInEx 插件的 HTTP 控制面（127.0.0.1:27182）通信。
///
/// 常用：
///   bz state                       看对局快照
///   bz inspect [--json]            读取 Unity UI 与内存双向核对面板
///   bz start [--hero Mak]          开局（默认 Vanessa）
///   bz resume                      从主大厅恢复未完结对局
///   bz concede [--http]            放弃当前对局
///   bz pick                        选第一个候选（技能/遭遇/奇遇步/商店卡自适应）
///   bz encounter-step [--index N]  点选奇遇选项卡 EncounterStep
///   bz buy [--index N]             买货架第 N 张
///   bz sell <instanceId>           卖卡 / 使用消耗品
///   bz move <id> <socket> [sect]   精准移动/挤位（如 bz move itm_1 2 Hand）
///   bz reroll                      刷新商店
///   bz next [N]                    推进（exit 一次或 N 次）
///   bz auto --until day3           智能自动玩到目标（支持 --runs N --audit <jsonPath>）
///   bz marathon --runs 5 --speed 12 --out report.json  5 局连打性能/UI/词条实机核验器
///   bz shot out.png                截图（不需要窗口在前台）
///   bz watch                       实时打印状态变化
///   bz cheat gold 999              GM
///   bz run "ops.json"              执行批量脚本
/// </summary>
internal static class Program
{
    private static string _base = "http://127.0.0.1:27182";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string env = Environment.GetEnvironmentVariable("BAZAAR_DRIVER_URL");
        if (!string.IsNullOrWhiteSpace(env)) _base = env.TrimEnd('/');

        if (args.Length == 0) { Usage(); return 2; }

        var argv = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--url" && i + 1 < args.Length) { _base = args[++i].TrimEnd('/'); continue; }
            argv.Add(args[i]);
        }
        if (argv.Count == 0) { Usage(); return 2; }

        string cmd = argv[0].ToLowerInvariant();
        var rest = argv.Skip(1).ToList();

        try
        {
            return cmd switch
            {
                "state" or "s" => await CmdState(),
                "inspect" or "i" => await CmdInspect(rest),
                "start" => await CmdStart(rest),
                "resume" => await CmdResume(rest),
                "concede" => await CmdConcede(rest),
                "pick" => await CmdPick(),
                "buy" => await CmdBuy(rest),
                "sell" => await CmdSell(rest),
                "move" or "mv" => await CmdMove(rest),
                "skill" => await CmdSkill(rest),
                "encounter" => await CmdEncounter(rest),
                "encounter-step" or "encounterstep" or "step" => await CmdEncounterStep(rest),
                "reroll" or "r" => await CmdSimple("reroll"),
                "next" or "n" => await CmdNext(rest),
                "auto" => await CmdAuto(rest),
                "marathon" => await CmdMarathon(rest),
                "speed" => await CmdSpeed(rest),
                "shot" => await CmdShot(rest),
                "watch" => await CmdWatch(),
                "cheat" => await CmdCheat(rest),
                "run" => await CmdRun(rest),
                "events" => await CmdEvents(rest),
                "wait" => await CmdWait(rest),
                "health" => await CmdHealth(),
                _ => Unknown(cmd),
            };
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("连不上驱动。游戏在跑吗？插件加载了吗？");
            Console.Error.WriteLine("检查: " + _base + "/health");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("错误: " + ex.Message);
            return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine(@"bz —— 命令行驱动《The Bazaar》（进程内，不抢鼠标）

  bz state                        对局快照
  bz inspect [--json] [--compact] [--no-ui] [--section S] Unity UI 与内存双向核对面板
  bz start [--hero X] [--force]   开局
  bz resume [--force]             从主大厅恢复活跃对局
  bz concede [--http]             放弃当前对局
  bz pick                         选第一个候选（自适应技能/遭遇/奇遇步/卡）
  bz encounter-step [id|--index N] 点选奇遇选项卡 EncounterStep
  bz buy [--index N]              买货架第 N 张（默认 0）
  bz sell <id>                    卖卡 / 使用消耗品
  bz move <id> [--socket S] [--section Hand|Stash]  精准移动/挤位卡牌
  bz skill [id]                   选技能
  bz encounter [id]               选遭遇
  bz reroll                       刷新商店
  bz next [N]                     推进 N 次（默认 1）
  bz auto --until day3 [--runs N --audit out.json]  智能自动游玩
  bz marathon --runs 5 --speed 12 --out out.json [--heroes Vanessa,Pygmalien,Dooley,Mak,Stelle] [--hero-fallback fallback|skip|fail]
  bz shot [file]                  截图
  bz watch                        实时状态
  bz cheat gold 999               GM
  bz run ops.json                 批量脚本
  bz events [--since N]           事件流
  bz wait --app ChoiceState       等条件
  bz health                       驱动健康检查

全局: --url http://127.0.0.1:27182");
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine("未知命令: " + cmd);
        Usage();
        return 2;
    }

    // ── HTTP ────────────────────────────────────────────────────────────────

    private static async Task<JObject> Get(string path)
    {
        var resp = await Http.GetAsync(_base + path);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {Truncate(body, 300)}");
        return JObject.Parse(body);
    }

    private static async Task<JObject> PostJson(string path, object payload)
    {
        string json = JsonConvert.SerializeObject(payload ?? new { });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync(_base + path, content);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {Truncate(body, 300)}");
        return JObject.Parse(body);
    }

    private static Task<JObject> Drive(object payload) => PostJson("/drive", payload);

    private static string Truncate(string s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + "…");

    // ── 命令实现 ────────────────────────────────────────────────────────────

    private static async Task<int> CmdHealth()
    {
        var s = await Get("/health");
        Console.WriteLine("驱动在线 ✓");
        Console.WriteLine($"  version={s["driverVersion"]} inRun={s["inRun"]} appState={s["appState"]} runState={s["runState"]}");
        return 0;
    }

    private static async Task<int> CmdState()
    {
        var s = await Get("/state");
        PrintState(s);
        return 0;
    }

    private static async Task<int> CmdInspect(List<string> rest)
    {
        bool rawJson = rest.Contains("--json");
        bool compact = rest.Contains("--compact");
        bool noUi = rest.Contains("--no-ui");
        string sectionFilter = null;
        for (int i = 0; i < rest.Count; i++)
            if (rest[i] == "--section" && i + 1 < rest.Count) sectionFilter = rest[++i].ToLowerInvariant();

        string url = (compact || noUi) ? $"/inspect?compact={(compact ? 1 : 0)}&ui={(noUi ? 0 : 1)}" : "/inspect";
        var d = await Get(url);
        if (rawJson)
        {
            Console.WriteLine(d.ToString(compact ? Formatting.None : Formatting.Indented));
            return 0;
        }

        Console.WriteLine($"=== INSPECT ({d["appState"]} / {d["runState"]}) inRun={d["inRun"]} hasActiveRun={d["hasActiveRunFlag"]} inspectMs={d["inspectMs"]} ===");
        if (d["perf"] is JObject pf)
        {
            Console.WriteLine($"  Perf: FPS={pf["fps"]} (minWin={pf["minFpsWindow"]}) Frame={pf["avgFrameTimeMs"]}ms Heap={pf["monoHeapMB"]}MB GC=({pf["gcGen0"]}/{pf["gcGen1"]}/{pf["gcGen2"]}) OpMs={pf["lastMainThreadOpMs"]} busy={pf["busy"]}");
        }
        if (d["inRun"]?.Value<bool>() != true)
        {
            Console.WriteLine($"  {d["note"] ?? "当前不在对局中"}");
            return 0;
        }

        Console.WriteLine($"  Day {d["day"]} Hour {d["hour"]} | Hero={d["hero"]} Mode={d["playMode"]} | W/L={d["victories"]}/{d["losses"]} | Slots={d["unlockedSlots"]}");
        var p = d["player"] as JObject;
        if (p != null)
        {
            Console.WriteLine($"  Player: HP={p["health"]}/{p["healthMax"]} Shield={p["shield"]} Gold={p["gold"]} Income={p["income"]} Prestige={p["prestige"]} Lv={p["level"]} XP={p["experience"]}");
        }
        var opp = d["opponent"] as JObject;
        if (opp != null && (opp["healthMax"]?.Value<int>() ?? 0) > 0)
        {
            Console.WriteLine($"  Opponent: Hero={opp["hero"]} HP={opp["health"]}/{opp["healthMax"]} Shield={opp["shield"]} Burn={opp["burn"]} Poison={opp["poison"]} Regen={opp["regen"]}");
        }

        void PrintZone(string title, string key)
        {
            if (!string.IsNullOrEmpty(sectionFilter) && !key.ToLowerInvariant().Contains(sectionFilter)) return;
            var arr = d[key] as JArray;
            if (arr == null || arr.Count == 0) return;

            Console.WriteLine($"\n[{title}] ({arr.Count}):");
            foreach (var c in arr)
            {
                string id = c["instanceId"]?.ToString() ?? "";
                string name = c["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) name = c["templateId"]?.ToString() ?? "";
                string tier = c["tier"]?.ToString() ?? "-";
                string ench = c["enchantment"]?.ToString();
                string sock = c["socket"]?.ToString() ?? c["skillSocket"]?.ToString() ?? "-";
                string size = c["size"]?.ToString() ?? "-";

                var ui = c["ui"] as JObject;
                string uiTier = ui?["uiTier"]?.ToString();
                string tierDisp = (!string.IsNullOrEmpty(uiTier) && uiTier != tier) ? $"{tier}(UI:{uiTier})" : tier;
                string enchDisp = string.IsNullOrEmpty(ench) ? "" : $" [{ench}]";

                var metrics = new List<string>();
                int dmg = c["damageAmount"]?.Value<int>() ?? 0;
                int sh = c["shieldApplyAmount"]?.Value<int>() ?? 0;
                int brn = c["burnApplyAmount"]?.Value<int>() ?? 0;
                int psn = c["poisonApplyAmount"]?.Value<int>() ?? 0;
                int heal = c["healAmount"]?.Value<int>() ?? 0;
                int cd = c["cooldownMax"]?.Value<int>() ?? 0;
                int ammo = c["ammo"]?.Value<int>() ?? 0;
                int ammoMax = c["ammoMax"]?.Value<int>() ?? 0;
                int buyP = c["buyPrice"]?.Value<int>() ?? 0;
                int sellP = c["sellPrice"]?.Value<int>() ?? 0;

                string uiDmg = ui?["uiDamageText"]?.ToString();
                if (dmg > 0 || !string.IsNullOrEmpty(uiDmg))
                    metrics.Add(uiDmg != null && uiDmg != dmg.ToString() ? $"DMG={dmg}(UI:{uiDmg})" : $"DMG={dmg}");
                if (sh > 0) metrics.Add($"SHD={sh}");
                if (brn > 0) metrics.Add($"BRN={brn}");
                if (psn > 0) metrics.Add($"PSN={psn}");
                if (heal > 0) metrics.Add($"HEL={heal}");
                if (cd > 0) metrics.Add($"CD={cd}ms");
                if (ammoMax > 0) metrics.Add($"AMMO={ammo}/{ammoMax}");
                if (buyP > 0) metrics.Add($"Buy={buyP}g");
                if (sellP > 0) metrics.Add($"Sell={sellP}g");

                var fxList = new List<string>();
                var fx = ui?["effects"] as JObject;
                if (fx != null)
                {
                    if (fx["frozen"]?.Value<bool>() == true) fxList.Add("FROZEN");
                    if (fx["hasted"]?.Value<bool>() == true) fxList.Add("HASTED");
                    if (fx["slowed"]?.Value<bool>() == true) fxList.Add("SLOWED");
                    if (fx["heated"]?.Value<bool>() == true) fxList.Add("HEATED");
                    if (fx["chilled"]?.Value<bool>() == true) fxList.Add("CHILLED");
                    if (fx["flying"]?.Value<bool>() == true) fxList.Add("FLYING");
                    if (fx["disabled"]?.Value<bool>() == true) fxList.Add("DISABLED");
                }
                string fxStr = fxList.Count > 0 ? " <" + string.Join(",", fxList) + ">" : "";

                Console.WriteLine($"  {id,-22} {Truncate(name, 22),-22} {tierDisp,-10} {size,-6} {sock,-8}{enchDisp} {string.Join(" ", metrics)}{fxStr}");
            }
        }

        PrintZone("Hand", "hand");
        PrintZone("Stash", "stash");
        PrintZone("Skills", "skills");
        PrintZone("Selection", "selection");
        PrintZone("OpponentHand", "opponentHand");
        PrintZone("OpponentSkills", "opponentSkills");
        return 0;
    }

    private static void PrintState(JObject s)
    {
        Console.WriteLine($"inRun={s["inRun"]}  appState={s["appState"]}  runState={s["runState"]}  unlockedSlots={s["unlockedSlots"]}");
        Console.WriteLine($"Day {s["day"]}  Hour {s["hour"]}   Gold {s["gold"]}  Income {s["income"]}  " +
                          $"Prestige {s["prestige"]}  HP {s["health"]}/{s["healthMax"]}  Shield {s["shield"]}  Lv {s["level"]}  " +
                          $"W/L {s["victories"]}/{s["losses"]}");
        Console.WriteLine($"hero={s["hero"]}  playMode={s["playMode"]}  combatSpeed={s["combatSpeed"]}  cloudGuard={s["cloudGuard"]}");

        var sel = s["selectionSet"] as JArray;
        if (sel != null && sel.Count > 0)
            Console.WriteLine($"selection({sel.Count}): {string.Join(", ", sel.Select(t => t.ToString()))}");

        var cards = s["cards"] as JArray;
        if (cards != null && cards.Count > 0)
        {
            Console.WriteLine($"cards({cards.Count}):");
            foreach (var c in cards)
            {
                string owner = c["owner"]?.ToString() ?? "-";
                string socket = c["socket"]?.ToString() ?? "-";
                string tier = c["tier"]?.ToString() ?? "-";
                string size = c["size"]?.ToString() ?? "-";
                string type = c["type"]?.ToString() ?? "-";
                var attrs = c["attributes"] as JObject;
                string extra = "";
                if (attrs != null)
                {
                    var parts = new List<string>();
                    foreach (var a in attrs)
                    {
                        if (a.Key == "BuyPrice" || a.Key == "SellPrice" ||
                            a.Key == "CooldownMax" || a.Key == "DamageAmount")
                            parts.Add($"{a.Key}={a.Value}");
                    }
                    if (parts.Count > 0) extra = "  " + string.Join(" ", parts);
                }
                Console.WriteLine($"  {c["instanceId"],-24} {type,-16} {tier,-9} {size,-7} {owner,-8} {socket,-9}{extra}");
            }
        }
    }

    private static async Task<int> CmdStart(List<string> rest)
    {
        string hero = "Vanessa";
        bool force = rest.Contains("--force");
        for (int i = 0; i < rest.Count; i++)
            if (rest[i] == "--hero" && i + 1 < rest.Count) hero = rest[++i];

        var r = await Drive(new { op = "startRun", hero, force });
        if (r["ok"]?.Value<bool>() != true)
        {
            Console.WriteLine("开局失败: " + (r["error"] ?? "unknown"));
            return 1;
        }

        if (r["alreadyInRun"]?.Value<bool>() == true)
        {
            Console.WriteLine($"已在局内 ({r["appState"]})，未重开（如需重开请传 --force）");
            await CmdState();
            return 0;
        }

        Console.WriteLine("开局: 已触发");

        if (r["needsMenuWait"]?.Value<bool>() == true)
        {
            Console.WriteLine("  上一局已结束或需退回主大厅，正在等待主菜单就绪...");
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(500);
                try
                {
                    var s = await Get("/state");
                    string st = s["appState"]?.ToString() ?? "";
                    string scene = s["activeScene"]?.ToString() ?? "";
                    bool inRun = s["inRun"]?.Value<bool>() == true;
                    bool menuReady = s["menuReady"]?.Value<bool>() == true;
                    if (!inRun && (menuReady || scene is "MainMenuScene" or "HeroSelectScene") &&
                        (st == "null" || st == "EmptyState"))
                    {
                        Console.WriteLine($"  菜单就绪（{i * 500}ms）");
                        break;
                    }
                }
                catch { }
            }
            var r2 = await Drive(new { op = "startRun", hero });
            if (r2["ok"]?.Value<bool>() != true)
            {
                Console.WriteLine("  重新开局失败: " + (r2["error"] ?? "unknown"));
                return 1;
            }
            Console.WriteLine("  重新开局: 已触发");
        }

        var sw = Stopwatch.StartNew();
        bool ready = false;
        while (sw.ElapsedMilliseconds < 30000)
        {
            try
            {
                var s = await Get("/state");
                bool inRun = s["inRun"]?.Value<bool>() == true;
                string appSt = s["appState"]?.ToString() ?? "";
                int selCount = (s["selectionSet"] as JArray)?.Count ?? 0;
                if (inRun && selCount > 0 &&
                    appSt is "ChoiceState" or "EncounterState" or "LevelUpState" or "LootState")
                {
                    Console.WriteLine($"开局就绪: {appSt}（{sw.ElapsedMilliseconds} ms，候选 {selCount} 项）");
                    ready = true;
                    break;
                }
            }
            catch { }
            await Task.Delay(200);
        }

        if (!ready)
        {
            Console.WriteLine("等待开局就绪超时（30000 ms）");
            await CmdState();
            return 1;
        }

        await CmdState();
        return 0;
    }

    private static async Task<int> CmdResume(List<string> rest)
    {
        bool force = rest.Contains("--force");
        bool click = rest.Contains("--click");

        var r = await PostJson("/resume", new { force, clickButton = click });
        if (r["ok"]?.Value<bool>() != true)
        {
            Console.WriteLine("恢复对局失败: " + (r["error"] ?? "unknown"));
            return 1;
        }

        Console.WriteLine($"恢复对局已触发: via={r["via"] ?? "alreadyInRun"} hero={r["hero"]} day={r["day"]} hour={r["hour"]}");
        if (r["alreadyInRun"]?.Value<bool>() == true)
        {
            await CmdState();
            return 0;
        }

        if (r["needsMenuWait"]?.Value<bool>() == true)
        {
            Console.WriteLine("  正在退回主菜单，等待后重新 Resume...");
            for (int i = 0; i < 24; i++)
            {
                await Task.Delay(500);
                var s = await Get("/state");
                string st = s["appState"]?.ToString() ?? "";
                if (st == "null" || st == "EmptyState" || st.Contains("MainMenu")) break;
            }
            r = await PostJson("/resume", new { force, clickButton = click });
            if (r["ok"]?.Value<bool>() != true)
            {
                Console.WriteLine("  重新恢复失败: " + r["error"]);
                return 1;
            }
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 25000)
        {
            var s = await Get("/state");
            string appSt = s["appState"]?.ToString() ?? "";
            if (s["inRun"]?.Value<bool>() == true &&
                appSt is not "EmptyState" and not "StartRunAppState" and not "null")
            {
                Console.WriteLine($"对局已恢复（{sw.ElapsedMilliseconds}ms）");
                PrintState(s);
                return 0;
            }
            await Task.Delay(350);
        }

        Console.WriteLine("等待进入恢复对局超时");
        return 1;
    }

    private static async Task<int> CmdConcede(List<string> rest)
    {
        bool forceHttp = rest.Contains("--http");
        var r = await PostJson("/concede", new { forceHttp });
        bool ok = r["ok"]?.Value<bool>() == true;
        Console.WriteLine(ok
            ? $"已放弃对局: via={r["via"]} runId={r["runId"]}"
            : $"放弃对局失败: {r["error"]}");
        return ok ? 0 : 1;
    }

    private static async Task<int> CmdEncounterStep(List<string> rest)
    {
        string id = null;
        int? index = null;
        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--index" && i + 1 < rest.Count) index = int.Parse(rest[++i]);
            else if (!rest[i].StartsWith("--")) id = rest[i];
        }

        var r = await PostJson("/encounterstep", new { instanceId = id, index });
        bool ok = r["ok"]?.Value<bool>() == true;
        Console.WriteLine(ok
            ? $"奇遇步已选择: {r["instanceId"]} ({r["cardType"]}, price={r["buyPrice"]}g) via={r["via"]}"
            : $"奇遇步失败: {r["error"]}");
        return ok ? 0 : 1;
    }

    private static async Task<int> CmdMove(List<string> rest)
    {
        if (rest.Count == 0)
        {
            Console.WriteLine("用法: bz move <instanceId> [--socket Socket_2|2|stash|auto] [--section Hand|Stash] [--from Socket_0]");
            Console.WriteLine("  或简写: bz move <instanceId> <socket> [section]");
            return 2;
        }

        string id = null;
        string socket = null;
        string section = null;
        string fromSocket = null;
        var positional = new List<string>();

        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--socket" && i + 1 < rest.Count) socket = rest[++i];
            else if (rest[i] == "--section" && i + 1 < rest.Count) section = rest[++i];
            else if (rest[i] == "--from" && i + 1 < rest.Count) fromSocket = rest[++i];
            else positional.Add(rest[i]);
        }

        if (positional.Count > 0) id = positional[0];
        if (string.IsNullOrEmpty(socket) && positional.Count > 1) socket = positional[1];
        if (string.IsNullOrEmpty(section) && positional.Count > 2) section = positional[2];

        if (string.IsNullOrEmpty(id))
        {
            Console.WriteLine("缺少 instanceId");
            return 2;
        }

        var r = await PostJson("/move", new { instanceId = id, socket, section, fromSocket });
        bool ok = r["ok"]?.Value<bool>() == true;
        if (!ok)
        {
            Console.WriteLine("移动失败: " + r["error"]);
            return 1;
        }

        Console.WriteLine($"已移动 {r["instanceId"]}: {r["fromSection"]}:{r["fromSocket"]} -> {r["actualSection"] ?? r["targetSection"]}:{r["actualSocket"] ?? r["targetSocket"]} (via={r["via"]})");
        if (r["displacedCards"] is JArray disp && disp.Count > 0)
        {
            Console.WriteLine($"  挤位联动 ({disp.Count} 张):");
            foreach (var d in disp)
                Console.WriteLine($"    {d["instanceId"]}: {d["fromSection"]}:{d["fromSocket"]} -> {d["toSection"]}:{d["toSocket"]}");
        }
        return 0;
    }

    private static async Task<int> CmdPick()
    {
        var s = await Get("/state");
        var sel = s["selectionSet"] as JArray;
        if (sel == null || sel.Count == 0) { Console.WriteLine("没有可选项"); return 1; }
        string id = sel[0].ToString();

        string type = FirstSelectionType(s);
        if (type == "EncounterStep")
        {
            var er = await PostJson("/encounterstep", new { instanceId = id });
            Console.WriteLine($"encounterstep {id}: " + (er["ok"]?.Value<bool>() == true ? "ok" : "失败 " + er["error"]));
            return er["ok"]?.Value<bool>() == true ? 0 : 1;
        }

        string op = type switch
        {
            "Skill" => "selectSkill",
            "CombatEncounter" or "PvpEncounter" or "EventEncounter" or "PedestalEncounter" => "selectEncounter",
            _ => "buy",
        };

        var r = await Drive(new { op, instanceId = id });
        Console.WriteLine($"{op} {id}: " + (r["ok"]?.Value<bool>() == true ? "ok" : "失败 " + r["error"]));
        return 0;
    }

    private static string FirstSelectionType(JObject s)
    {
        var sel = s["selectionSet"] as JArray;
        if (sel == null || sel.Count == 0) return "Item";
        string first = sel[0].ToString();

        var cards = s["cards"] as JArray;
        if (cards != null)
        {
            foreach (var c in cards)
                if (c["instanceId"]?.ToString() == first)
                    return c["type"]?.ToString() ?? "Item";
        }

        if (first.StartsWith("skl_")) return "Skill";
        if (first.StartsWith("cbt_")) return "CombatEncounter";
        if (first.StartsWith("pvp_")) return "PvpEncounter";
        if (first.StartsWith("ped_")) return "PedestalEncounter";
        if (first.StartsWith("step_") || first.StartsWith("stp_")) return "EncounterStep";
        if (first.StartsWith("enc_")) return "EventEncounter";
        if (first.StartsWith("rew_")) return "Item";
        if (first.StartsWith("itm_")) return "Item";

        return "Item";
    }

    private static async Task<int> CmdBuy(List<string> rest)
    {
        int index = 0;
        string section = "Hand";
        string socket = null;
        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--index" && i + 1 < rest.Count) index = int.Parse(rest[++i]);
            else if (rest[i] == "--section" && i + 1 < rest.Count) section = rest[++i];
            else if (rest[i] == "--socket" && i + 1 < rest.Count) socket = rest[++i];
        }

        var s = await Get("/state");
        var sel = s["selectionSet"] as JArray;
        if (sel == null || sel.Count == 0) { Console.WriteLine("货架是空的"); return 1; }
        if (index >= sel.Count) { Console.WriteLine($"索引越界（只有 {sel.Count} 张）"); return 1; }

        string id = sel[index].ToString();
        int goldBefore = s["gold"]?.Value<int>() ?? 0;
        var r = await Drive(new { op = "buy", instanceId = id, section, socket });
        bool ok = r["ok"]?.Value<bool>() == true;
        Console.WriteLine(ok ? $"买入 {id}" : "失败: " + r["error"]);

        if (ok)
        {
            var s2 = await Get("/state");
            int goldAfter = s2["gold"]?.Value<int>() ?? 0;
            Console.WriteLine($"gold {goldBefore} -> {goldAfter}");
        }
        return ok ? 0 : 1;
    }

    private static async Task<int> CmdSell(List<string> rest)
    {
        if (rest.Count == 0) { Console.WriteLine("用法: bz sell <instanceId>"); return 2; }
        var r = await Drive(new { op = "sell", instanceId = rest[0] });
        Console.WriteLine(r["ok"]?.Value<bool>() == true ? "已卖出" : "失败: " + r["error"]);
        return 0;
    }

    private static async Task<int> CmdSkill(List<string> rest)
    {
        string id = rest.Count > 0 ? rest[0] : null;
        var r = await Drive(new { op = "selectSkill", instanceId = id });
        Console.WriteLine(r["ok"]?.Value<bool>() == true ? "技能已选" : "失败: " + r["error"]);
        return 0;
    }

    private static async Task<int> CmdEncounter(List<string> rest)
    {
        string id = rest.Count > 0 ? rest[0] : null;
        var r = await Drive(new { op = "selectEncounter", instanceId = id });
        Console.WriteLine(r["ok"]?.Value<bool>() == true ? "遭遇已选" : "失败: " + r["error"]);
        return 0;
    }

    private static async Task<int> CmdSimple(string op)
    {
        var r = await Drive(new { op });
        Console.WriteLine(r["ok"]?.Value<bool>() == true ? op + " ok" : "失败: " + r["error"]);
        return 0;
    }

    private static async Task<int> CmdNext(List<string> rest)
    {
        int n = 1;
        if (rest.Count > 0 && int.TryParse(rest[0], out int parsed)) n = parsed;

        for (int i = 0; i < n; i++)
        {
            var r = await Drive(new { op = "exit", waitIdle = true });
            if (r["ok"]?.Value<bool>() != true) { Console.WriteLine($"第 {i + 1} 次推进失败: " + r["error"]); return 1; }
        }
        await CmdState();
        return 0;
    }

    private static async Task<int> CmdSpeed(List<string> rest)
    {
        float speed = rest.Count > 0 && float.TryParse(rest[0], out float sp) ? sp : 0f;
        bool autoExit = rest.Contains("--auto-exit-replay");
        var r = await Drive(new { op = "combatspeed", speed, autoExitReplay = autoExit });
        Console.WriteLine(r["ok"]?.Value<bool>() == true ? $"combatSpeed -> {r["speed"]} autoExitReplay={r["autoExitReplay"]}" : "失败: " + r["error"]);
        return 0;
    }

    private static async Task<int> CmdMarathon(List<string> rest)
    {
        var cfg = new MarathonAuditor.Config
        {
            Runs = 5,
            Speed = 12f,
            MaxStepsPerRun = 900,
            Until = "end",
            AuditMode = true,
            AutoResume = false,
            OutPath = $"marathon_audit_{DateTime.Now:yyyyMMdd_HHmmss}.json",
        };

        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--runs" && i + 1 < rest.Count) cfg.Runs = Math.Max(1, int.Parse(rest[++i]));
            else if (rest[i] == "--speed" && i + 1 < rest.Count) cfg.Speed = float.Parse(rest[++i]);
            else if ((rest[i] == "--out" || rest[i] == "--audit") && i + 1 < rest.Count) cfg.OutPath = rest[++i];
            else if (rest[i] == "--max" && i + 1 < rest.Count) cfg.MaxStepsPerRun = int.Parse(rest[++i]);
            else if (rest[i] == "--heroes" && i + 1 < rest.Count)
            {
                var list = rest[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (list.Count > 0) cfg.Heroes = list;
            }
            else if (rest[i] == "--hero-fallback" && i + 1 < rest.Count) cfg.HeroFallbackMode = rest[++i].Trim().ToLowerInvariant();
            else if (rest[i] == "--fallback-heroes" && i + 1 < rest.Count)
            {
                var list = rest[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (list.Count > 0) cfg.FallbackHeroes = list;
            }
        }

        Console.WriteLine($"启动 Marathon 实机审计：runs={cfg.Runs}, speed={cfg.Speed}x, heroes={string.Join(",", cfg.Heroes)}, out={cfg.OutPath}");
        var client = new DriverClient(_base, Http);
        return await MarathonAuditor.RunAsync(client, cfg);
    }

    /// <summary>
    /// 智能自动玩：全面兼容多步 EncounterStep（step_/stp_）、二合一升阶、智能上阵与光环邻接、
    /// 背包腾格、金币充裕主动刷新、消耗品/基座处理，并支持 --runs N --audit <jsonPath>。
    /// </summary>
    private static async Task<int> CmdAuto(List<string> rest)
    {
        string until = "day2";
        int maxSteps = 450;
        float speed = 0f;
        int runs = 1;
        string auditPath = null;
        bool autoResume = !rest.Contains("--no-resume");
        var heroes = new List<string>(MarathonAuditor.DefaultHeroes);
        string heroFallbackMode = "fallback";
        List<string> fallbackHeroes = null;

        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--until" && i + 1 < rest.Count) until = rest[++i];
            else if (rest[i] == "--max" && i + 1 < rest.Count) maxSteps = int.Parse(rest[++i]);
            else if (rest[i] == "--speed" && i + 1 < rest.Count) speed = float.Parse(rest[++i]);
            else if (rest[i] == "--runs" && i + 1 < rest.Count) runs = Math.Max(1, int.Parse(rest[++i]));
            else if ((rest[i] == "--audit" || rest[i] == "--out") && i + 1 < rest.Count) auditPath = rest[++i];
            else if (rest[i] == "--heroes" && i + 1 < rest.Count)
            {
                var list = rest[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (list.Count > 0) heroes = list;
            }
            else if (rest[i] == "--hero-fallback" && i + 1 < rest.Count) heroFallbackMode = rest[++i].Trim().ToLowerInvariant();
            else if (rest[i] == "--fallback-heroes" && i + 1 < rest.Count)
            {
                var list = rest[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (list.Count > 0) fallbackHeroes = list;
            }
        }

        bool multiRunAudit = runs > 1 || !string.IsNullOrWhiteSpace(auditPath);
        if (multiRunAudit && until == "day2" && !rest.Contains("--until"))
        {
            until = "end";
        }
        if (multiRunAudit && speed <= 0f)
        {
            speed = 12f;
        }

        var cfg = new MarathonAuditor.Config
        {
            Runs = runs,
            Speed = speed,
            MaxStepsPerRun = maxSteps,
            OutPath = auditPath,
            Heroes = heroes,
            Until = until,
            AuditMode = multiRunAudit,
            AutoResume = autoResume,
            HeroFallbackMode = heroFallbackMode,
        };
        if (fallbackHeroes != null) cfg.FallbackHeroes = fallbackHeroes;

        Console.WriteLine($"智能自动游玩：runs={cfg.Runs}, until={cfg.Until}, maxSteps={cfg.MaxStepsPerRun}" +
                          (cfg.Speed > 0 ? $", speed={cfg.Speed}x" : "") +
                          (string.IsNullOrEmpty(cfg.OutPath) ? "" : $", audit={cfg.OutPath}"));

        var client = new DriverClient(_base, Http);
        int rc = await MarathonAuditor.RunAsync(client, cfg);
        Console.WriteLine();
        if (!multiRunAudit) await CmdInspect(new List<string>());
        return rc;
    }

    internal static List<JObject> ResolveSelectionCandidatesPublic(JArray selection, JArray selectionIds)
        => ResolveSelectionCandidates(selection, selectionIds);

    internal static (string kind, int value) ParseUntilPublic(string s) => ParseUntil(s);
    internal static bool ReachedPublic(JObject s, string kind, int value) => Reached(s, kind, value);

    private static List<JObject> ResolveSelectionCandidates(JArray selection, JArray selectionIds)
    {
        var list = new List<JObject>();
        var byId = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (var item in selection.OfType<JObject>())
        {
            string id = item["instanceId"]?.ToString();
            if (!string.IsNullOrEmpty(id)) byId[id] = item;
        }

        foreach (var idToken in selectionIds)
        {
            string id = idToken.ToString();
            if (byId.TryGetValue(id, out var obj))
            {
                list.Add(obj);
                continue;
            }

            string inferredType = id.StartsWith("skl_") ? "Skill"
                : id.StartsWith("cbt_") ? "CombatEncounter"
                : id.StartsWith("pvp_") ? "PvpEncounter"
                : id.StartsWith("ped_") ? "PedestalEncounter"
                : (id.StartsWith("step_") || id.StartsWith("stp_")) ? "EncounterStep"
                : id.StartsWith("enc_") ? "EventEncounter"
                : "Item";

            list.Add(new JObject
            {
                ["instanceId"] = id,
                ["type"] = inferredType,
                ["buyPrice"] = 0,
            });
        }
        return list;
    }

    private static (string kind, int value) ParseUntil(string s)
    {
        s = s.ToLowerInvariant().Replace(" ", "");
        if (s is "end" or "victory" or "finish") return ("end", 1);
        if (s.StartsWith("day")) return ("day", int.Parse(s[3..]));
        if (s.StartsWith("hour")) return ("hour", int.Parse(s[4..]));
        if (s.StartsWith("gold")) return ("gold", int.Parse(s[4..]));
        if (s.StartsWith("win")) return ("victories", int.Parse(s[3..]));
        return ("day", 2);
    }

    private static bool Reached(JObject s, string kind, int value)
    {
        int cur = kind switch
        {
            "day" => s["day"]?.Value<int>() ?? 0,
            "hour" => s["hour"]?.Value<int>() ?? 0,
            "gold" => s["gold"]?.Value<int>() ?? 0,
            "victories" => s["victories"]?.Value<int>() ?? 0,
            _ => 0,
        };
        return cur >= value;
    }

    private static async Task<int> CmdShot(List<string> rest)
    {
        string file = rest.Count > 0 ? rest[0] : $"shot-{DateTime.Now:HHmmss}.png";
        double scale = 1.0;
        for (int i = 0; i < rest.Count; i++)
            if (rest[i] == "--scale" && i + 1 < rest.Count) scale = double.Parse(rest[++i]);

        var resp = await Http.GetAsync($"{_base}/screenshot?scale={scale}");
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine("截图失败: " + resp.StatusCode);
            return 1;
        }
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync(file, bytes);
        Console.WriteLine($"已保存 {file} ({bytes.Length / 1024} KB)");
        return 0;
    }

    private static async Task<int> CmdWatch()
    {
        Console.WriteLine("监听状态变化（Ctrl+C 退出）");
        string last = "";
        while (true)
        {
            try
            {
                var s = await Get("/state");
                string sig = $"{s["appState"]}|{s["runState"]}|{s["day"]}|{s["hour"]}|{s["gold"]}|{s["cardCount"]}|{(s["selectionSet"] as JArray)?.Count}";
                if (sig != last)
                {
                    last = sig;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s["appState"],-18} {s["runState"],-10} " +
                                      $"D{s["day"]}H{s["hour"]} gold={s["gold"],-4} cards={s["cardCount"],-3} sel={(s["selectionSet"] as JArray)?.Count}");
                }
            }
            catch (Exception ex) { Console.WriteLine("  " + ex.Message); }
            await Task.Delay(400);
        }
    }

    private static async Task<int> CmdCheat(List<string> rest)
    {
        if (rest.Count == 0) { Console.WriteLine("用法: bz cheat <args...>"); return 2; }
        var r = await Drive(new { op = "cheat", args = rest });
        Console.WriteLine(r["ok"]?.Value<bool>() == true ? "ok" : "失败: " + r["error"]);

        if (rest[0].Equals("restart", StringComparison.OrdinalIgnoreCase))
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(1000);
                try
                {
                    var s = await Get("/state");
                    string st = s["appState"]?.ToString() ?? "";
                    int selCount = (s["selectionSet"] as JArray)?.Count ?? 0;
                    int cards = s["cardCount"]?.Value<int>() ?? 0;

                    if (st is "EndRunVictoryState" or "EndRunDefeatState")
                    {
                        await Drive(new { op = "forceState", state = "Choice" });
                        Console.WriteLine($"  [{attempt}] 从 {st} 强制切到 Choice");
                        continue;
                    }

                    if (selCount > 0 && cards > 0)
                    {
                        Console.WriteLine($"  restart 完成: appState={st} day={s["day"]} gold={s["gold"]} " +
                                          $"cards={cards} sel={selCount}");
                        return 0;
                    }

                    Console.WriteLine($"  [{attempt}] 等待同步: appState={st} cards={cards} sel={selCount}");
                }
                catch (Exception ex) { Console.WriteLine("  " + ex.Message); }
            }
            Console.WriteLine("  restart 后未能等到 selectionSet，可能需要重开客户端");
        }
        return 0;
    }

    private static async Task<int> CmdRun(List<string> rest)
    {
        if (rest.Count == 0) { Console.WriteLine("用法: bz run ops.json"); return 2; }
        string json = File.ReadAllText(rest[0]);
        var payload = JObject.Parse(json);
        payload["op"] = "batch";

        var r = await Drive(payload);
        Console.WriteLine(JsonConvert.SerializeObject(r, Formatting.Indented));
        return r["ok"]?.Value<bool>() == true ? 0 : 1;
    }

    private static async Task<int> CmdEvents(List<string> rest)
    {
        int since = 0;
        for (int i = 0; i < rest.Count; i++)
            if (rest[i] == "--since" && i + 1 < rest.Count) since = int.Parse(rest[++i]);

        var r = await Get($"/events?since={since}");
        Console.WriteLine($"lastSeq={r["lastSeq"]}");
        var evs = r["events"] as JArray;
        if (evs != null)
            foreach (var e in evs)
                Console.WriteLine($"  #{e["Seq"],-5} {e["Type"],-20} {e["Detail"]}");
        return 0;
    }

    private static async Task<int> CmdWait(List<string> rest)
    {
        var spec = new JObject();
        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--app" && i + 1 < rest.Count) spec["appState"] = rest[++i];
            else if (rest[i] == "--state" && i + 1 < rest.Count) spec["runState"] = rest[++i];
            else if (rest[i] == "--cards" && i + 1 < rest.Count) spec["minCards"] = int.Parse(rest[++i]);
            else if (rest[i] == "--gold" && i + 1 < rest.Count) spec["minGold"] = int.Parse(rest[++i]);
            else if (rest[i] == "--day" && i + 1 < rest.Count) spec["dayEquals"] = int.Parse(rest[++i]);
        }
        await WaitForCondition(spec, 15000, quiet: false);
        return 0;
    }

    private static async Task<bool> WaitForCondition(JObject spec, int timeoutMs, bool quiet)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var r = await Drive(new { op = "wait", condition = spec, timeoutMs = 400 });
                if (r["ok"]?.Value<bool>() == true)
                {
                    if (!quiet) Console.WriteLine($"条件达成（{sw.ElapsedMilliseconds} ms）");
                    return true;
                }
            }
            catch { }
            await Task.Delay(120);
        }
        if (!quiet) Console.WriteLine($"等待超时（{timeoutMs} ms）");
        return false;
    }
}
