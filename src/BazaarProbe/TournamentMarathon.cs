#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;

namespace BazaarProbe;

/// <summary>
/// 多人锦标赛并发马拉松验证器：
/// 批量/并发拉起 M 个 8 人锦标赛房间（每房混合 2~3 名虚拟真人会话 + AI 补满 PlayerCap=8），
/// 覆盖 StartingHealth / StartingGold / StartingItems / StartingSkills / BotNonLethalPrestigeLock 等预设规则，
/// 并验证 Hour 6 多真人同步屏障、开局数值公平性与非致死声望锁。
/// </summary>
internal static class TournamentMarathon
{
    private sealed record PresetSpec(string TemplateId, string Tier, string? Enchantment);

    private sealed record LobbyRuleScenario(
        int HumanCount,
        int StartingHealth,
        int StartingGold,
        List<PresetSpec> StartingItems,
        List<PresetSpec> StartingSkills,
        bool BotNonLethalPrestigeLock);

    private sealed class TournamentStatusSnapshot
    {
        public string Phase { get; init; } = "";
        public int TotalPlayers { get; init; }
        public int ActivePlayers { get; init; }
        public bool CanStartPvp { get; init; }
        public bool WaitingForOtherPlayers { get; init; }
        public bool IsEliminated { get; init; }
        public string AssignedOpponentAccountId { get; init; } = "";
        public string AssignedOpponentUsername { get; init; } = "";
        public List<TournamentPlayerSnapshot> Players { get; init; } = new();
    }

    private sealed class TournamentPlayerSnapshot
    {
        public string AccountId { get; init; } = "";
        public string Username { get; init; } = "";
        public int Prestige { get; init; }
        public bool IsEliminated { get; init; }
    }

    public static async Task RunAsync(
        int lobbyCount,
        int concurrency,
        string baseUrl,
        HttpClient http,
        Fuzz.CardMetadataCatalog catalog,
        Fuzz.FuzzMetrics metrics,
        Action<string> log,
        Action<string> logFileOnly)
    {
        if (lobbyCount <= 0) return;

        var presetItemPool = catalog.PickPresetItems(6);
        var presetSkillPool = catalog.PickPresetSkills(6);
        log($"== TournamentMarathon start: lobbies={lobbyCount}, concurrency={concurrency}, presetItems={presetItemPool.Count}, presetSkills={presetSkillPool.Count} ==");

        var indices = Enumerable.Range(0, lobbyCount);
        int doneCount = 0;

        await Parallel.ForEachAsync(
            indices,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency) },
            async (lobbyIdx, ct) =>
            {
                var sw = Stopwatch.StartNew();
                var scenario = BuildScenario(lobbyIdx, presetItemPool, presetSkillPool);
                var (ok, summary) = await RunSingleLobbyAsync(
                    lobbyIdx, scenario, baseUrl, http, catalog, metrics, logFileOnly);
                sw.Stop();

                Interlocked.Increment(ref metrics.TournamentLobbiesTotal);
                if (ok)
                    Interlocked.Increment(ref metrics.TournamentLobbiesPassed);
                else
                    Interlocked.Increment(ref metrics.TournamentLobbiesFailed);

                int done = Interlocked.Increment(ref doneCount);
                if (!ok || done % 5 == 0 || done == 1 || done == lobbyCount)
                {
                    log($"[Tourney {done:D3}/{lobbyCount}] ok={ok} {sw.ElapsedMilliseconds}ms | {summary}");
                }
            });
    }

    private static LobbyRuleScenario BuildScenario(
        int lobbyIdx,
        List<Fuzz.CardMetadataCatalog.CardMeta> itemPool,
        List<Fuzz.CardMetadataCatalog.CardMeta> skillPool)
    {
        int humanCount = (lobbyIdx % 2 == 0) ? 2 : 3;
        int[] hpChoices = [350, 400, 280, 320];
        int[] goldChoices = [30, 40, 25, 20];

        var items = new List<PresetSpec>();
        if (itemPool.Count > 0)
        {
            var item1 = itemPool[lobbyIdx % itemPool.Count];
            string? ench = (lobbyIdx % 3 == 0) ? "Heavy" : null;
            items.Add(new PresetSpec(item1.Id, item1.StartingTier, ench));
        }

        var skills = new List<PresetSpec>();
        if (skillPool.Count > 0)
        {
            var skill1 = skillPool[lobbyIdx % skillPool.Count];
            skills.Add(new PresetSpec(skill1.Id, skill1.StartingTier, null));
        }

        return new LobbyRuleScenario(
            HumanCount: humanCount,
            StartingHealth: hpChoices[lobbyIdx % hpChoices.Length],
            StartingGold: goldChoices[lobbyIdx % goldChoices.Length],
            StartingItems: items,
            StartingSkills: skills,
            BotNonLethalPrestigeLock: true);
    }

    private static async Task<(bool Ok, string Summary)> RunSingleLobbyAsync(
        int lobbyIdx,
        LobbyRuleScenario scenario,
        string baseUrl,
        HttpClient http,
        Fuzz.CardMetadataCatalog catalog,
        Fuzz.FuzzMetrics metrics,
        Action<string> logFileOnly)
    {
        var humans = new List<(string Username, string Token, string AccountId, EHero Hero)>(scenario.HumanCount);

        try
        {
            // 1. 为 2~3 名虚拟真人登录并拉取 AccountId
            for (int h = 0; h < scenario.HumanCount; h++)
            {
                string uname = $"TrnV11_{lobbyIdx:D3}_H{h}_{Guid.NewGuid():N}"[..24];
                var auth = await Fuzz.AuthenticateUserAsync(http, baseUrl, uname, metrics, logFileOnly);
                if (!auth.Ok)
                    return (false, $"human#{h} auth failed: {auth.Error}");
                var hero = Fuzz.FuzzHeroes[(lobbyIdx * 3 + h) % Fuzz.FuzzHeroes.Length];
                humans.Add((uname, auth.Token, auth.AccountId, hero));
                Interlocked.Increment(ref metrics.TournamentHumanSessions);
            }

            // 2. 房主建房（先以默认规则创建，再通过 PUT /lobbies/{id}/rules 下发完整预设规则以覆盖两条协议路径）
            var host = humans[0];
            var createBody = new
            {
                name = $"MarathonLobby_{lobbyIdx:D3}",
                playMode = "Tournament",
                playerCap = 8,
                isPublic = false,
                roundCountdownSeconds = 18,
                acceleratedRoundCountdownSeconds = 6,
                rules = BuildRulesPayload(scenario),
            };
            var createReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/lobbies")
            {
                Content = new StringContent(JsonSerializer.Serialize(createBody), Encoding.UTF8, "application/json"),
            };
            createReq.Headers.Add("Authorization", "Bearer " + host.Token);
            var createResp = await http.SendAsync(createReq);
            if (!Fuzz.CheckHttpStatus(createResp.StatusCode, "POST /lobbies", metrics, logFileOnly))
                return (false, $"POST /lobbies HTTP {createResp.StatusCode}");

            string createJson = await createResp.Content.ReadAsStringAsync();
            string lobbyId, lobbyCode;
            using (var doc = JsonDocument.Parse(createJson))
            {
                lobbyId = GetStringCI(doc.RootElement, "LobbyId") ?? "";
                lobbyCode = GetStringCI(doc.RootElement, "Code") ?? "";
            }
            if (string.IsNullOrEmpty(lobbyId) || string.IsNullOrEmpty(lobbyCode))
            {
                Interlocked.Increment(ref metrics.ProtocolErrorCount);
                return (false, "POST /lobbies returned empty LobbyId/Code");
            }

            // 显式测试 PUT /lobbies/{id}/rules
            var putRulesBody = new
            {
                roundCountdownSeconds = 18,
                acceleratedRoundCountdownSeconds = 6,
                rules = BuildRulesPayload(scenario),
            };
            var putRulesReq = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/lobbies/{lobbyId}/rules")
            {
                Content = new StringContent(JsonSerializer.Serialize(putRulesBody), Encoding.UTF8, "application/json"),
            };
            putRulesReq.Headers.Add("Authorization", "Bearer " + host.Token);
            var putRulesResp = await http.SendAsync(putRulesReq);
            if (!Fuzz.CheckHttpStatus(putRulesResp.StatusCode, "PUT /lobbies/{id}/rules", metrics, logFileOnly))
                return (false, $"PUT /lobbies/{{id}}/rules HTTP {putRulesResp.StatusCode}");

            // 3. 其余真人按码加入房间
            for (int h = 1; h < humans.Count; h++)
            {
                var joinReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/lobbies/{lobbyCode}/members");
                joinReq.Headers.Add("Authorization", "Bearer " + humans[h].Token);
                var joinResp = await http.SendAsync(joinReq);
                if (!Fuzz.CheckHttpStatus(joinResp.StatusCode, "POST /lobbies/{code}/members", metrics, logFileOnly))
                    return (false, $"human#{h} join HTTP {joinResp.StatusCode}");
            }

            // 4. 房主触发倒计时开赛（服务端自动补齐 AI 机器人至 PlayerCap = 8）
            var cdReq = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/lobbies/{lobbyId}/countdown");
            cdReq.Headers.Add("Authorization", "Bearer " + host.Token);
            var cdResp = await http.SendAsync(cdReq);
            if (!Fuzz.CheckHttpStatus(cdResp.StatusCode, "PUT /lobbies/{id}/countdown", metrics, logFileOnly))
                return (false, $"countdown HTTP {cdResp.StatusCode}");

            string cdJson = await cdResp.Content.ReadAsStringAsync();
            Guid tournamentSessionId;
            using (var cdDoc = JsonDocument.Parse(cdJson))
            {
                string? tidStr = GetStringCI(cdDoc.RootElement, "tournamentSessionId");
                if (!Guid.TryParse(tidStr, out tournamentSessionId) || tournamentSessionId == Guid.Empty)
                {
                    Interlocked.Increment(ref metrics.ProtocolErrorCount);
                    return (false, "countdown missing tournamentSessionId");
                }
            }

            // 校验房间成员已补满 PlayerCap = 8 且记录 AI 账号清单
            var lobbyCheckReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/lobbies/{lobbyId}");
            lobbyCheckReq.Headers.Add("Authorization", "Bearer " + host.Token);
            var lobbyCheckResp = await http.SendAsync(lobbyCheckReq);
            if (!Fuzz.CheckHttpStatus(lobbyCheckResp.StatusCode, "GET /lobbies/{id}", metrics, logFileOnly))
                return (false, $"GET /lobbies/{{id}} HTTP {lobbyCheckResp.StatusCode}");

            var aiAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var lDoc = JsonDocument.Parse(await lobbyCheckResp.Content.ReadAsStringAsync()))
            {
                int cap = GetIntCI(lDoc.RootElement, "PlayerCap");
                if (!TryGetPropertyCI(lDoc.RootElement, "Members", out var membersEl) || membersEl.ValueKind != JsonValueKind.Array)
                {
                    Interlocked.Increment(ref metrics.TournamentViolations);
                    return (false, "lobby Members missing");
                }
                int memberCount = membersEl.GetArrayLength();
                if (cap != 8 || memberCount != 8)
                {
                    Interlocked.Increment(ref metrics.TournamentViolations);
                    metrics.AddViolationSample($"[Tourney#{lobbyIdx:D3}] PlayerCap={cap} Members={memberCount} != 8", logFileOnly);
                    return (false, $"PlayerCap={cap} Members={memberCount} != 8");
                }
                foreach (var m in membersEl.EnumerateArray())
                {
                    if (TryGetBoolCI(m, "IsAi", out bool isAi) && isAi)
                    {
                        string? aid = GetStringCI(m, "AccountId");
                        if (!string.IsNullOrEmpty(aid)) aiAccountIds.Add(aid);
                    }
                }
            }

            // 5. 并发驱动本房全部真人会话直到各自完赛/淘汰，同时协调 Hour 6 同步屏障与公平性校验
            var humanAccountIds = new HashSet<string>(
                humans.Select(x => x.AccountId.Replace("-", "")),
                StringComparer.OrdinalIgnoreCase);
            int barrierProbedOnce = 0;
            var humanTasks = new List<Task<(bool Ok, ERunState Verdict, string Summary)>>(humans.Count);

            for (int h = 0; h < humans.Count; h++)
            {
                int humanIdx = h;
                var hu = humans[h];
                humanTasks.Add(Task.Run(async () =>
                {
                    var rng = new Random(20261122 + lobbyIdx * 131 + humanIdx * 17);
                    bool preferWeak = (humanIdx == humans.Count - 1) && (lobbyIdx % 2 == 1);
                    string lastOppAccountId = "";
                    string lastOppUsername = "";
                    int prePvpPrestige = 20;

                    async Task<TournamentStatusSnapshot?> FetchStatusAsync()
                    {
                        var stReq = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/tournament/status");
                        stReq.Headers.Add("Authorization", "Bearer " + hu.Token);
                        stReq.Headers.Add("aid", hu.AccountId.Replace("-", ""));
                        stReq.Headers.Add("uid", hu.Username);
                        var stResp = await http.SendAsync(stReq);
                        if (!Fuzz.CheckHttpStatus(stResp.StatusCode, "GET /tournament/status", metrics, logFileOnly))
                            return null;
                        string raw = await stResp.Content.ReadAsStringAsync();
                        return ParseStatus(raw);
                    }

                    async Task<bool> BeforeHour6PvpAsync(uint day, string pvpCardId)
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(45);
                        while (DateTime.UtcNow < deadline)
                        {
                            var st = await FetchStatusAsync();
                            if (st == null) return false;

                            lastOppAccountId = st.AssignedOpponentAccountId;
                            lastOppUsername = st.AssignedOpponentUsername;
                            var selfRow = st.Players.FirstOrDefault(p =>
                                p.AccountId.Equals(hu.AccountId.Replace("-", ""), StringComparison.OrdinalIgnoreCase));
                            if (selfRow != null) prePvpPrestige = selfRow.Prestige;

                            // 校验非致死声望锁：BotNonLethalPrestigeLock=true 时，所有 AI 机器人 Prestige >= 1 且不被淘汰
                            if (scenario.BotNonLethalPrestigeLock)
                            {
                                foreach (var p in st.Players)
                                {
                                    if (aiAccountIds.Contains(p.AccountId))
                                    {
                                        if (p.Prestige < 1 || p.IsEliminated)
                                        {
                                            Interlocked.Increment(ref metrics.TournamentViolations);
                                            metrics.AddViolationSample(
                                                $"[Tourney#{lobbyIdx:D3}] AI bot {p.Username} violated BotNonLethalPrestigeLock: prestige={p.Prestige} elim={p.IsEliminated}",
                                                logFileOnly);
                                            return false;
                                        }
                                    }
                                }
                                Interlocked.Increment(ref metrics.TournamentNonLethalLockChecksPassed);
                            }

                            if (st.CanStartPvp || st.IsEliminated || string.Equals(st.Phase, "RunEnded", StringComparison.OrdinalIgnoreCase))
                                return true;

                            // 首个早到 Hour 6 的玩家先刻意放行 1 次 SelectEncounter 测试服务端屏障拦截（返回 NotAllowed 且不坏状态），随后轮询等待
                            if (st.WaitingForOtherPlayers && Interlocked.CompareExchange(ref barrierProbedOnce, 1, 0) == 0)
                            {
                                Interlocked.Increment(ref metrics.TournamentBarrierBlocksVerified);
                                return true;
                            }

                            await Task.Delay(80);
                        }

                        Interlocked.Increment(ref metrics.TournamentViolations);
                        metrics.AddViolationSample($"[Tourney#{lobbyIdx:D3}-H{humanIdx}] Hour 6 barrier timeout at Day={day}", logFileOnly);
                        return false;
                    }

                    void OnInitialSnapshot(
                        GameStateSnapshotDTO sync,
                        Dictionary<string, Fuzz.CardShadow> cards,
                        Dictionary<EPlayerAttributeType, int> attrs)
                    {
                        int actualHp = attrs.GetValueOrDefault(EPlayerAttributeType.HealthMax, 0);
                        int actualGold = attrs.GetValueOrDefault(EPlayerAttributeType.Gold, -1);

                        if (actualHp != scenario.StartingHealth || actualGold != scenario.StartingGold)
                        {
                            Interlocked.Increment(ref metrics.TournamentViolations);
                            metrics.AddViolationSample(
                                $"[Tourney#{lobbyIdx:D3}-H{humanIdx}] PARITY_STAT_MISMATCH: HP={actualHp} (exp={scenario.StartingHealth}), Gold={actualGold} (exp={scenario.StartingGold})",
                                logFileOnly);
                            return;
                        }

                        var ownedItemTpls = cards.Values
                            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item)
                            .Select(c => c.TemplateId)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var ownedSkillTpls = cards.Values
                            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill)
                            .Select(c => c.TemplateId)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        foreach (var itemSpec in scenario.StartingItems)
                        {
                            if (!ownedItemTpls.Contains(itemSpec.TemplateId))
                            {
                                Interlocked.Increment(ref metrics.TournamentViolations);
                                metrics.AddViolationSample(
                                    $"[Tourney#{lobbyIdx:D3}-H{humanIdx}] PARITY_MISSING_START_ITEM: {itemSpec.TemplateId}",
                                    logFileOnly);
                                return;
                            }
                        }

                        foreach (var skillSpec in scenario.StartingSkills)
                        {
                            if (!ownedSkillTpls.Contains(skillSpec.TemplateId))
                            {
                                Interlocked.Increment(ref metrics.TournamentViolations);
                                metrics.AddViolationSample(
                                    $"[Tourney#{lobbyIdx:D3}-H{humanIdx}] PARITY_MISSING_START_SKILL: {skillSpec.TemplateId}",
                                    logFileOnly);
                                return;
                            }
                        }

                        Interlocked.Increment(ref metrics.TournamentParityChecksPassed);
                    }

                    void OnPvpCombatObserved(GameSim spawnSim, GameSim despawnSim, NetMessageCombatSim combatMsg)
                    {
                        bool oppIsAi = aiAccountIds.Contains(lastOppAccountId) ||
                                       lastOppAccountId.StartsWith("sparring", StringComparison.OrdinalIgnoreCase);

                        if (oppIsAi)
                        {
                            // 校验 AI 对手也获得房间 StartingSkills 公平同步
                            var oppSkillTpls = spawnSim.Events
                                .OfType<GameSimEventCardSpawned>()
                                .Where(ev => ev.CombatantId == ECombatantId.Opponent && ev.Type == ECardType.Skill)
                                .Select(ev => ev.TemplateId)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

                            foreach (var skillSpec in scenario.StartingSkills)
                            {
                                if (!oppSkillTpls.Contains(skillSpec.TemplateId))
                                {
                                    Interlocked.Increment(ref metrics.TournamentViolations);
                                    metrics.AddViolationSample(
                                        $"[Tourney#{lobbyIdx:D3}-H{humanIdx}] AI_BOT_MISSING_START_SKILL: opp={lastOppUsername} skill={skillSpec.TemplateId}",
                                        logFileOnly);
                                }
                            }

                            // 校验 AI 对手负于非致死规则时不会导致玩家在预估低声望时被 Bot 直接致死（若玩家本局输给了 Bot）
                            if (scenario.BotNonLethalPrestigeLock && combatMsg.Data?.Winner == ECombatantId.Opponent)
                            {
                                int postPrestige = despawnSim.Player?.Attributes?.GetValueOrDefault(EPlayerAttributeType.Prestige, 1) ?? 1;
                                ERunState postState = despawnSim.CurrentState?.StateName ?? ERunState.Choice;
                                int dayNow = (int)(despawnSim.Run?.Day ?? 1);
                                if (prePvpPrestige <= dayNow && (postPrestige < 1 || postState == ERunState.EndRunDefeat))
                                {
                                    Interlocked.Increment(ref metrics.TournamentViolations);
                                    metrics.AddViolationSample(
                                        $"[Tourney#{lobbyIdx:D3}-H{humanIdx}] HUMAN_LETHAL_BY_BOT: vs {lastOppUsername} prePrestige={prePvpPrestige} postPrestige={postPrestige} state={postState}",
                                        logFileOnly);
                                }
                            }

                            Interlocked.Increment(ref metrics.TournamentParityChecksPassed);
                        }
                    }

                    bool IsLiveHumanPvpOpponent(GameSim _)
                    {
                        string normOpp = lastOppAccountId.Replace("-", "");
                        return !string.IsNullOrEmpty(normOpp)
                            && humanAccountIds.Contains(normOpp)
                            && !aiAccountIds.Contains(lastOppAccountId)
                            && !lastOppAccountId.StartsWith("sparring", StringComparison.OrdinalIgnoreCase)
                            && !lastOppAccountId.StartsWith("ghost_", StringComparison.OrdinalIgnoreCase);
                    }

                    var driver = new Fuzz.RunSessionDriver(
                        label: $"trn#{lobbyIdx:D3}-H{humanIdx}",
                        baseUrl: baseUrl,
                        http: http,
                        token: hu.Token,
                        accountId: hu.AccountId,
                        username: hu.Username,
                        hero: hu.Hero,
                        playMode: EPlayMode.Tournament,
                        tournamentSessionId: tournamentSessionId,
                        catalog: catalog,
                        metrics: metrics,
                        rng: rng,
                        preferWeakBoard: preferWeak,
                        preferNonShopEncounters: (humanIdx % 2 == 0),
                        enableResumeCheck: false,
                        resumeTargetDay: 99,
                        resumeTargetHour: 99,
                        isCustomRuleRun: true,
                        customStartingHealth: scenario.StartingHealth,
                        logFile: logFileOnly,
                        beforeHour6PvpAsync: BeforeHour6PvpAsync,
                        onInitialSnapshot: OnInitialSnapshot,
                        onPvpCombatObserved: OnPvpCombatObserved,
                        isLiveHumanPvpOpponent: IsLiveHumanPvpOpponent);

                    return await driver.ExecuteRunAsync();
                }));
            }

            var results = await Task.WhenAll(humanTasks);
            bool allOk = results.All(r => r.Ok);
            string verdicts = string.Join(", ", results.Select((r, idx) => $"H{idx}:{r.Verdict}({r.Ok})"));

            // 6. 检查终局观战视图并清理房内成员关系
            var specReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/spectate/{lobbyCode}");
            var specResp = await http.SendAsync(specReq);
            if (!Fuzz.CheckHttpStatus(specResp.StatusCode, "GET /spectate/{code}", metrics, logFileOnly))
                return (false, $"spectate HTTP {specResp.StatusCode}; {verdicts}");

            return (allOk, $"code={lobbyCode} humans={humans.Count} HP={scenario.StartingHealth} G={scenario.StartingGold} | {verdicts}");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref metrics.ExceptionCount);
            logFileOnly($"[Tourney#{lobbyIdx:D3}] EXCEPTION: {ex}");
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (var hu in humans)
            {
                try
                {
                    var leaveReq = new HttpRequestMessage(HttpMethod.Delete, baseUrl + "/membership");
                    leaveReq.Headers.Add("Authorization", "Bearer " + hu.Token);
                    await http.SendAsync(leaveReq);
                }
                catch { }
            }
        }
    }

    private static object BuildRulesPayload(LobbyRuleScenario s) => new
    {
        startingHealth = s.StartingHealth,
        startingGold = s.StartingGold,
        startingItems = s.StartingItems.Select(x => new
        {
            templateId = x.TemplateId,
            tier = x.Tier,
            enchantment = x.Enchantment,
        }).ToArray(),
        startingSkills = s.StartingSkills.Select(x => new
        {
            templateId = x.TemplateId,
            tier = x.Tier,
            enchantment = x.Enchantment,
        }).ToArray(),
        playerCap = 8,
        botNonLethalPrestigeLock = s.BotNonLethalPrestigeLock,
        humanMatchEvery4Rounds = true,
        lastHumanStandingMode = true,
    };

    private static TournamentStatusSnapshot ParseStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var players = new List<TournamentPlayerSnapshot>();
        if (TryGetPropertyCI(r, "Players", out var pArr) && pArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in pArr.EnumerateArray())
            {
                players.Add(new TournamentPlayerSnapshot
                {
                    AccountId = GetStringCI(el, "AccountId") ?? "",
                    Username = GetStringCI(el, "Username") ?? "",
                    Prestige = GetIntCI(el, "Prestige"),
                    IsEliminated = TryGetBoolCI(el, "IsEliminated", out bool elim) && elim,
                });
            }
        }

        return new TournamentStatusSnapshot
        {
            Phase = GetStringCI(r, "Phase") ?? "",
            TotalPlayers = GetIntCI(r, "TotalPlayers"),
            ActivePlayers = GetIntCI(r, "ActivePlayers"),
            CanStartPvp = TryGetBoolCI(r, "CanStartPvp", out bool csp) && csp,
            WaitingForOtherPlayers = TryGetBoolCI(r, "WaitingForOtherPlayers", out bool wf) && wf,
            IsEliminated = TryGetBoolCI(r, "IsEliminated", out bool ie) && ie,
            AssignedOpponentAccountId = GetStringCI(r, "AssignedOpponentAccountId") ?? "",
            AssignedOpponentUsername = GetStringCI(r, "AssignedOpponentUsername") ?? "",
            Players = players,
        };
    }

    private static bool TryGetPropertyCI(JsonElement el, string name, out JsonElement val)
    {
        val = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                val = p.Value;
                return true;
            }
        }
        return false;
    }

    private static string? GetStringCI(JsonElement el, string name) =>
        TryGetPropertyCI(el, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetIntCI(JsonElement el, string name) =>
        TryGetPropertyCI(el, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

    private static bool TryGetBoolCI(JsonElement el, string name, out bool val)
    {
        val = false;
        if (TryGetPropertyCI(el, name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            val = v.GetBoolean();
            return true;
        }
        return false;
    }
}
