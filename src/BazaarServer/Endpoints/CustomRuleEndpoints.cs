using System.Text.Encodings.Web;
using System.Text.Json;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Storage;

namespace BazaarServer.Endpoints;

/// <summary>
/// 玩家自定义局内规则、3 套预设档位、5 大新玩法模式、14+Mod 词条与流派目录 HTTP 端点。
/// 100% 向后兼容原有 /custom-rules/* 接口，并扩展目录与快捷切换能力。
/// </summary>
public static class CustomRuleEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class TendencyUpdateDto
    {
        public ulong SelectedArchetypeMask { get; set; }
    }

    private sealed class LockOpeningTendencyDto
    {
        public ulong SelectedArchetypeMask { get; set; }
        public string? RunId { get; set; }
    }

    private sealed record SpecialModeCatalogEntry(
        ESpecialModeId ModeId,
        string Key,
        string Category,
        string NameZh,
        string DescZh,
        string BadgeZh,
        bool AllowsStandardGhostPool,
        bool AllowsLeaderboard,
        CustomRunRuleSet DefaultRules);

    public static void Map(
        WebApplication app,
        PlayerRuleStore store,
        AccountStore accounts,
        Func<object> catalogProvider,
        SessionStore? sessions = null,
        Persistence? persistence = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(accounts);

        var modHost = app.Services.GetService<ServerModHost>();

        // ── 目录查询：保留原有 archetypes/encounterCategories/items/skills，非破坏性扩展 presets/specialModes/mutators/dailyChallengeInfo ──
        app.MapGet("/custom-rules/catalog", () =>
        {
            object rawBase = catalogProvider?.Invoke() ?? new { };
            var presets = PresetCatalog.All.Select(p => new
            {
                presetId = p.PresetId.ToString(),
                key = p.Key,
                nameZh = p.NameZh,
                descZh = p.DescZh,
                scoreMultiplier = p.ScoreMultiplier,
                allowsStandardGhostPool = p.AllowsStandardGhostPool,
                defaultRules = PresetCatalog.Create(p.PresetId, ESpecialModeId.Standard),
            }).ToList();

            var specialModes = BuildSpecialModesCatalog();

            var mutators = new List<object>(MutatorCatalog.All.Count + (modHost?.ModCustomMutators.Count ?? 0));
            foreach (var m in MutatorCatalog.All)
            {
                mutators.Add(new
                {
                    id = m.Id,
                    nameZh = m.NameZh,
                    descZh = m.DescZh,
                    conflictGroup = m.ConflictGroup,
                    scoreMultiplierDelta = m.ScoreMultiplierDelta,
                    isPositive = m.IsPositive,
                    targetHook = m.TargetHook,
                    parameters = m.Parameters,
                    source = "official",
                });
            }
            if (modHost?.ModCustomMutators != null)
            {
                foreach (var m in modHost.ModCustomMutators)
                {
                    mutators.Add(new
                    {
                        id = m.Id,
                        nameZh = m.NameZh,
                        descZh = m.DescZh,
                        conflictGroup = m.ConflictGroup,
                        scoreMultiplierDelta = m.ScoreMultiplierDelta,
                        isPositive = m.IsPositive,
                        targetHook = m.TargetHook,
                        parameters = m.Parameters,
                        source = "mod",
                    });
                }
            }

            var dailySpec = DailyChallengeSeedGenerator.GenerateSpec();
            var dailyMutatorDetails = dailySpec.MutatorIds
                .Select(id => MutatorCatalog.ById.TryGetValue(id, out var meta)
                    ? new { id = meta.Id, nameZh = meta.NameZh, descZh = meta.DescZh, scoreMultiplierDelta = meta.ScoreMultiplierDelta }
                    : new { id, nameZh = id, descZh = "", scoreMultiplierDelta = 0.0 })
                .ToList();
            long secondsUntilReset = Math.Max(0, (long)Math.Ceiling((dailySpec.NextResetUtc - DateTimeOffset.UtcNow).TotalSeconds));

            var dailyChallengeInfo = new
            {
                dateKey = dailySpec.DateKey,
                masterSeed = dailySpec.MasterSeed,
                featuredHero = dailySpec.FeaturedHero,
                mutatorIds = dailySpec.MutatorIds,
                mutatorDetails = dailyMutatorDetails,
                nextResetUtc = dailySpec.NextResetUtc,
                secondsUntilReset,
                scoreMultiplier = MutatorCatalog.CalculateTotalScoreMultiplier(dailySpec.RuleSet.PresetId, dailySpec.MutatorIds),
                ruleSet = dailySpec.RuleSet,
            };

            if (rawBase is CardTaxonomyCatalogDto tax)
            {
                return Results.Json(new
                {
                    archetypes = tax.Archetypes,
                    encounterCategories = tax.EncounterCategories,
                    items = tax.Items,
                    skills = tax.Skills,
                    presets,
                    specialModes,
                    mutators,
                    dailyChallengeInfo,
                }, JsonOpts);
            }

            // 向后兼容任意自定义 catalogProvider 返回对象：平铺其顶层字段并附加扩展字段
            var mergedObj = new System.Text.Json.Nodes.JsonObject();
            if (JsonSerializer.SerializeToNode(rawBase, JsonOpts) is System.Text.Json.Nodes.JsonObject baseObj)
            {
                foreach (var kv in baseObj)
                    mergedObj[kv.Key] = kv.Value?.DeepClone();
            }
            else
            {
                mergedObj["baseCatalog"] = JsonSerializer.SerializeToNode(rawBase, JsonOpts);
            }
            mergedObj["presets"] = JsonSerializer.SerializeToNode(presets, JsonOpts);
            mergedObj["specialModes"] = JsonSerializer.SerializeToNode(specialModes, JsonOpts);
            mergedObj["mutators"] = JsonSerializer.SerializeToNode(mutators, JsonOpts);
            mergedObj["dailyChallengeInfo"] = JsonSerializer.SerializeToNode(dailyChallengeInfo, JsonOpts);

            return Results.Json(mergedObj, JsonOpts);
        });

        app.MapGet("/custom-rules/me", (HttpContext ctx) =>
        {
            string accountId = ResolveAccountId(ctx, accounts);
            return Results.Json(store.Get(accountId), JsonOpts);
        });

        app.MapPut("/custom-rules/me", async (HttpContext ctx) =>
        {
            CustomRunRuleSet? incoming;
            try
            {
                incoming = await JsonSerializer.DeserializeAsync<CustomRunRuleSet>(
                    ctx.Request.Body,
                    JsonOpts,
                    ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "自定义规则 JSON 格式无效，请检查字段类型与括号匹配。" });
            }

            if (incoming == null)
                return Results.BadRequest(new { error = "请求体不能为空。" });

            string accountId = ResolveAccountId(ctx, accounts);
            var updated = store.Set(accountId, incoming);
            return Results.Json(updated, JsonOpts);
        });

        // ── 快捷切换预设档位（PureOfficial / EnhancedFun / ExtremeRoguelike / Custom）──
        app.MapPost("/custom-rules/me/preset/{presetId}", (HttpContext ctx, string presetId) =>
        {
            if (!TryParsePresetId(presetId, out var parsedPreset))
            {
                return Results.BadRequest(new
                {
                    error = $"未知的预设档位：{presetId}，可选值：PureOfficial、EnhancedFun、ExtremeRoguelike、Custom",
                });
            }

            string accountId = ResolveAccountId(ctx, accounts);
            var current = store.Get(accountId);
            bool resetMode = string.Equals(ctx.Request.Query["resetMode"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            var targetMode = resetMode ? ESpecialModeId.Standard : current.SpecialModeId;

            CustomRunRuleSet next;
            if (parsedPreset == EGameModePreset.Custom)
            {
                next = current.CloneNormalized();
                next.PresetId = EGameModePreset.Custom;
                next.SpecialModeId = targetMode;
            }
            else
            {
                next = PresetCatalog.Create(parsedPreset, targetMode);
                next.SelectedArchetypeMask = current.SelectedArchetypeMask;
                if (current.MutatorIds is { Count: > 0 } && targetMode is ESpecialModeId.MutatorChallenge or ESpecialModeId.EndlessTower or ESpecialModeId.CrossHeroMayhem or ESpecialModeId.BossRush)
                {
                    next.MutatorIds = new List<string>(current.MutatorIds);
                }
            }

            var updated = store.Set(accountId, next);
            return Results.Json(new
            {
                ok = true,
                presetId = updated.PresetId.ToString(),
                specialModeId = updated.SpecialModeId.ToString(),
                messageZh = $"已切换至「{ResolvePresetNameZh(updated.PresetId)}」档位",
                rules = updated,
            }, JsonOpts);
        });

        // ── 快捷切换玩法模式（Standard + 5 大新模式 + DailyChallenge）──
        app.MapPost("/custom-rules/me/mode/{modeId}", (HttpContext ctx, string modeId) =>
        {
            if (!TryParseSpecialModeId(modeId, out var parsedMode))
            {
                return Results.BadRequest(new
                {
                    error = $"未知的玩法模式：{modeId}，可选值：Standard、EndlessTower、CrossHeroMayhem、MutatorChallenge、DailyChallenge、BossRush、SandboxLab",
                });
            }

            string accountId = ResolveAccountId(ctx, accounts);
            var current = store.Get(accountId);
            CustomRunRuleSet next;

            if (parsedMode == ESpecialModeId.DailyChallenge)
            {
                var spec = DailyChallengeSeedGenerator.GenerateSpec();
                next = spec.RuleSet.CloneNormalized();
                next.SelectedArchetypeMask = current.SelectedArchetypeMask;
            }
            else
            {
                var basePreset = current.PresetId == EGameModePreset.Custom
                    ? EGameModePreset.PureOfficial
                    : current.PresetId;
                next = PresetCatalog.Create(basePreset, parsedMode);
                next.SelectedArchetypeMask = current.SelectedArchetypeMask;

                if (parsedMode == ESpecialModeId.MutatorChallenge && current.MutatorIds is { Count: > 0 })
                {
                    next.MutatorIds = new List<string>(current.MutatorIds);
                }
            }

            var updated = store.Set(accountId, next);
            return Results.Json(new
            {
                ok = true,
                presetId = updated.PresetId.ToString(),
                specialModeId = updated.SpecialModeId.ToString(),
                messageZh = $"已切换至「{ResolveModeNameZh(updated.SpecialModeId)}」模式",
                rules = updated,
            }, JsonOpts);
        });

        app.MapPut("/custom-rules/me/tendency", async (HttpContext ctx) =>
        {
            TendencyUpdateDto? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<TendencyUpdateDto>(
                    ctx.Request.Body,
                    JsonOpts,
                    ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "流派倾向 JSON 格式无效。" });
            }

            if (req == null)
                return Results.BadRequest(new { error = "请求体不能为空。" });

            string accountId = ResolveAccountId(ctx, accounts);
            var current = store.Get(accountId);
            current.SelectedArchetypeMask = req.SelectedArchetypeMask;
            var updated = store.Set(accountId, current);
            return Results.Json(updated, JsonOpts);
        });

        app.MapPost("/custom-rules/me/reset", (HttpContext ctx) =>
        {
            string accountId = ResolveAccountId(ctx, accounts);
            var reset = store.Reset(accountId);
            return Results.Json(reset, JsonOpts);
        });

        app.MapGet("/custom-rules/me/active-run-tendency", async (HttpContext ctx) =>
        {
            string accountId = ResolveAccountId(ctx, accounts);
            var session = FindSessionForAccount(sessions, accountId);
            if (session == null)
            {
                return Results.Json(new
                {
                    hasActiveRun = false,
                    allowTendencySelection = false,
                    tendencyLocked = true,
                    canLockNow = false,
                    selectedArchetypeMask = 0UL,
                }, JsonOpts);
            }

            try
            {
                await session.Gate.WaitAsync(ctx.RequestAborted);
            }
            catch (ObjectDisposedException)
            {
                return Results.Json(new
                {
                    hasActiveRun = false,
                    allowTendencySelection = false,
                    tendencyLocked = true,
                    canLockNow = false,
                    selectedArchetypeMask = 0UL,
                }, JsonOpts);
            }

            try
            {
                var handle = SessionRegistry.Get(session.Sid);
                if (handle == null || Persistence.IsTerminalState(handle.Run.State))
                {
                    return Results.Json(new
                    {
                        hasActiveRun = false,
                        allowTendencySelection = false,
                        tendencyLocked = true,
                        canLockNow = false,
                        selectedArchetypeMask = 0UL,
                    }, JsonOpts);
                }

                bool canLock = handle.Engine.CanLockOpeningTendency;
                return Results.Json(new
                {
                    hasActiveRun = true,
                    runId = handle.Run.RunId,
                    playMode = handle.Run.PlayMode.ToString(),
                    specialModeId = handle.Run.CustomRules.SpecialModeId.ToString(),
                    hero = handle.Run.Hero.ToString(),
                    day = handle.Run.Day,
                    hour = handle.Run.Hour,
                    state = handle.Run.State.ToString(),
                    allowTendencySelection = handle.Run.CustomRules?.AllowTendencySelection ?? true,
                    tendencyLocked = !canLock,
                    canLockNow = canLock,
                    selectedArchetypeMask = handle.Run.CustomRules?.SelectedArchetypeMask ?? 0UL,
                }, JsonOpts);
            }
            finally
            {
                try { session.Gate.Release(); } catch (ObjectDisposedException) { }
            }
        });

        app.MapPost("/custom-rules/me/lock-opening-tendency", async (HttpContext ctx) =>
        {
            LockOpeningTendencyDto? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<LockOpeningTendencyDto>(
                    ctx.Request.Body,
                    JsonOpts,
                    ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "开场流派锁定请求 JSON 格式无效。" });
            }

            if (req == null)
                return Results.BadRequest(new { error = "请求体不能为空。" });

            string accountId = ResolveAccountId(ctx, accounts);
            var session = FindSessionForAccount(sessions, accountId);
            if (session == null)
            {
                return Results.Json(new
                {
                    locked = true,
                    canLockNow = false,
                    error = "当前没有进行中的对局，请进入对局开场后再锁定倾向。",
                }, JsonOpts, statusCode: 404);
            }

            try
            {
                await session.Gate.WaitAsync(ctx.RequestAborted);
            }
            catch (ObjectDisposedException)
            {
                return Results.Json(new
                {
                    locked = true,
                    canLockNow = false,
                    error = "会话已关闭。",
                }, JsonOpts, statusCode: 409);
            }

            try
            {
                var handle = SessionRegistry.Get(session.Sid);
                if (handle == null)
                {
                    return Results.Json(new
                    {
                        locked = true,
                        canLockNow = false,
                        error = "当前会话未挂载运行态。",
                    }, JsonOpts, statusCode: 404);
                }

                if (!string.IsNullOrWhiteSpace(req.RunId) &&
                    !string.Equals(req.RunId.Trim(), handle.Run.RunId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(new
                    {
                        locked = true,
                        canLockNow = false,
                        runId = handle.Run.RunId,
                        selectedArchetypeMask = handle.Run.CustomRules?.SelectedArchetypeMask ?? 0UL,
                        error = "当前活跃对局编号不匹配。",
                    }, JsonOpts, statusCode: 409);
                }

                bool ok = handle.Engine.TryLockOpeningTendency(
                    req.SelectedArchetypeMask,
                    out ulong lockedMask,
                    out string? error);

                if (!ok)
                {
                    return Results.Json(new
                    {
                        locked = true,
                        canLockNow = false,
                        runId = handle.Run.RunId,
                        selectedArchetypeMask = lockedMask,
                        error = error ?? "本局流派倾向已锁定，严禁对局途中修改。",
                    }, JsonOpts, statusCode: 409);
                }

                if (persistence != null)
                {
                    try
                    {
                        string snapJson = RunSnapshotSerializer.Serialize(
                            handle.Run,
                            startSelectionDone: handle.Engine.StartSelectionDone,
                            pendingLevelUp: handle.Run.PendingLevelUp);
                        persistence.SaveRunSnapshot(
                            handle.Run,
                            session.AccountId,
                            snapJson,
                            isCustomRule: handle.Run.IsCustomRuleRun);
                    }
                    catch { }
                }

                var currentPreset = store.Get(accountId);
                currentPreset.SelectedArchetypeMask = lockedMask;
                store.Set(accountId, currentPreset);

                return Results.Json(new
                {
                    locked = true,
                    canLockNow = false,
                    runId = handle.Run.RunId,
                    selectedArchetypeMask = lockedMask,
                }, JsonOpts);
            }
            finally
            {
                try { session.Gate.Release(); } catch (ObjectDisposedException) { }
            }
        });
    }

    private static List<SpecialModeCatalogEntry> BuildSpecialModesCatalog() =>
    [
        new(
            ESpecialModeId.Standard,
            "standard",
            GamePlayMode.Standard.ToString(),
            "标准经典模式",
            "原汁原味的 10 胜制大巴扎对局，支持搭配三套预设档位",
            "经典",
            AllowsStandardGhostPool: true,
            AllowsLeaderboard: true,
            PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.Standard)),
        new(
            ESpecialModeId.EndlessTower,
            "endless_tower",
            GamePlayMode.EndlessTower.ToString(),
            "无尽爬塔模式",
            "10 胜后不停局，持续攀登至最高 99 层！Day 11+ 动态缩放野怪与强敌，每 5 层里程碑回复声望并奖励高额金币",
            "无尽",
            AllowsStandardGhostPool: false,
            AllowsLeaderboard: true,
            PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.EndlessTower)),
        new(
            ESpecialModeId.CrossHeroMayhem,
            "cross_hero",
            GamePlayMode.CrossHeroMayhem.ToString(),
            "跨职业大乱斗",
            "打破英雄卡池壁垒！保留 40% 本英雄核心保底，其余 60% 全英雄物品与技能跨界混出，创造终极神仙联动",
            "混池",
            AllowsStandardGhostPool: false,
            AllowsLeaderboard: true,
            PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.CrossHeroMayhem)),
        new(
            ESpecialModeId.MutatorChallenge,
            "mutator",
            GamePlayMode.MutatorAndDailyChallenge.ToString(),
            "肉鸽词条挑战",
            "自由搭配 14 条官方肉鸽突变词条与 Mod 词条，自定义刺激对局并按词条危险度叠加积分倍率",
            "词条",
            AllowsStandardGhostPool: false,
            AllowsLeaderboard: true,
            PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.MutatorChallenge)),
        new(
            ESpecialModeId.DailyChallenge,
            "daily",
            GamePlayMode.MutatorAndDailyChallenge.ToString(),
            "每日同种子挑战",
            "每日零点（UTC+8）全服同步锁定同一随机种子与词条组合，绝对公平角逐每日排行榜榜首",
            "每日",
            AllowsStandardGhostPool: false,
            AllowsLeaderboard: true,
            DailyChallengeSeedGenerator.GenerateSpec().RuleSet),
        new(
            ESpecialModeId.BossRush,
            "boss_rush",
            GamePlayMode.BossRush.ToString(),
            "Boss 车轮战",
            "连续迎战 10 波强化传奇首领！高额战利品多选、每波强化祭坛加持，第 5/10 波迎接巅峰镜像决战",
            "首领",
            AllowsStandardGhostPool: false,
            AllowsLeaderboard: true,
            PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.BossRush)),
        new(
            ESpecialModeId.SandboxLab,
            "sandbox",
            GamePlayMode.SandboxLab.ToString(),
            "沙盒构筑实验室",
            "全 10 格解锁、无限免费刷新，支持直发任意原版/Mod 卡牌、编辑附魔属性与零副作用自镜像/野怪实战测试（严格三禁隔离）",
            "沙盒",
            AllowsStandardGhostPool: false,
            AllowsLeaderboard: false,
            PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.SandboxLab)),
    ];

    private static bool TryParsePresetId(string? raw, out EGameModePreset preset)
    {
        preset = EGameModePreset.PureOfficial;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string key = raw.Trim().ToLowerInvariant().Replace("-", "_");
        switch (key)
        {
            case "pureofficial":
            case "pure_official":
            case "official":
            case "standard":
                preset = EGameModePreset.PureOfficial;
                return true;
            case "enhancedfun":
            case "enhanced_fun":
            case "enhanced":
            case "fun":
                preset = EGameModePreset.EnhancedFun;
                return true;
            case "extremeroguelike":
            case "extreme_roguelike":
            case "extreme":
            case "roguelike":
                preset = EGameModePreset.ExtremeRoguelike;
                return true;
            case "custom":
                preset = EGameModePreset.Custom;
                return true;
            default:
                return Enum.TryParse(raw.Trim(), ignoreCase: true, out preset);
        }
    }

    private static bool TryParseSpecialModeId(string? raw, out ESpecialModeId mode)
    {
        mode = ESpecialModeId.Standard;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string key = raw.Trim().ToLowerInvariant().Replace("-", "_");
        switch (key)
        {
            case "standard":
            case "normal":
            case "classic":
                mode = ESpecialModeId.Standard;
                return true;
            case "endless":
            case "endlesstower":
            case "endless_tower":
                mode = ESpecialModeId.EndlessTower;
                return true;
            case "crosshero":
            case "cross_hero":
            case "crossheromayhem":
            case "cross_hero_mayhem":
                mode = ESpecialModeId.CrossHeroMayhem;
                return true;
            case "mutator":
            case "mutatorchallenge":
            case "mutator_challenge":
                mode = ESpecialModeId.MutatorChallenge;
                return true;
            case "daily":
            case "dailychallenge":
            case "daily_challenge":
                mode = ESpecialModeId.DailyChallenge;
                return true;
            case "bossrush":
            case "boss_rush":
            case "boss":
                mode = ESpecialModeId.BossRush;
                return true;
            case "sandbox":
            case "sandboxlab":
            case "sandbox_lab":
                mode = ESpecialModeId.SandboxLab;
                return true;
            default:
                return Enum.TryParse(raw.Trim(), ignoreCase: true, out mode);
        }
    }

    private static string ResolvePresetNameZh(EGameModePreset preset) => preset switch
    {
        EGameModePreset.PureOfficial => "经典官方",
        EGameModePreset.EnhancedFun => "爽玩增强",
        EGameModePreset.ExtremeRoguelike => "极限肉鸽",
        _ => "自定义",
    };

    private static string ResolveModeNameZh(ESpecialModeId mode) => mode switch
    {
        ESpecialModeId.EndlessTower => "无尽爬塔",
        ESpecialModeId.CrossHeroMayhem => "跨职业大乱斗",
        ESpecialModeId.MutatorChallenge => "肉鸽词条挑战",
        ESpecialModeId.DailyChallenge => "每日同种子挑战",
        ESpecialModeId.BossRush => "Boss 车轮战",
        ESpecialModeId.SandboxLab => "沙盒构筑实验室",
        _ => "标准经典",
    };

    private static Session? FindSessionForAccount(SessionStore? sessions, string accountId)
    {
        if (sessions == null || string.IsNullOrWhiteSpace(accountId))
            return null;

        if (Guid.TryParse(accountId, out var parsed))
        {
            return sessions.GetByAccount(parsed.ToString("N"))
                ?? sessions.GetByAccount(parsed.ToString("D"));
        }
        return sessions.GetByAccount(accountId);
    }

    /// <summary>
    /// 按 Bearer Token -> X-Account-Id (aid) -> X-Username (uid) -> guest 顺序解析账号标识。
    /// </summary>
    public static string ResolveAccountId(HttpContext ctx, AccountStore accounts)
    {
        string auth = ctx.Request.Headers["Authorization"].ToString();
        var byToken = accounts.FindByAccessToken(auth);
        if (byToken != null)
            return byToken.AccountId.ToString("D");

        string rawAccountId = ctx.Request.Headers["X-Account-Id"].ToString().Trim();
        if (string.IsNullOrEmpty(rawAccountId))
            rawAccountId = ctx.Request.Headers["aid"].ToString().Trim();

        if (!string.IsNullOrEmpty(rawAccountId))
        {
            var byId = accounts.FindByAccountId(rawAccountId);
            if (byId != null)
                return byId.AccountId.ToString("D");
        }

        string rawUsername = ctx.Request.Headers["X-Username"].ToString().Trim();
        if (string.IsNullOrEmpty(rawUsername))
            rawUsername = ctx.Request.Headers["uid"].ToString().Trim();

        if (!string.IsNullOrEmpty(rawUsername))
        {
            var byName = accounts.FindByUsername(rawUsername) ?? accounts.GetOrCreateByName(rawUsername);
            return byName.AccountId.ToString("D");
        }

        if (!string.IsNullOrEmpty(rawAccountId))
            return Guid.TryParse(rawAccountId, out var g) ? g.ToString("D") : rawAccountId;

        return accounts.GetOrCreateByName("guest").AccountId.ToString("D");
    }
}
