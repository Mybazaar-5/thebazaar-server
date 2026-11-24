using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Protocol;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

/// <summary>
/// V13 终极架构升级（Wave 1-4）全链路集成验收测试套件：
/// 1. 真实加载示例 Mod（mods/bazaar-chronicles-sample/）+ 白名单视觉载体校验 + 热重载与开关切换；
/// 2. 真实加载外挂中文词典（dist/data/translations/custom_zh_overlay.json）1,416 条 + 四层优先级 + 活跃缺口命中；
/// 3. SQLite 8 张新表幂等迁移 + 模拟服务重启 100% 热恢复（大厅/规则/成员/多端令牌/聊天/回放/排行榜）；
/// 4. 多人联机安全红线（匿名写 401、非房主 409、多端令牌并存不互踢、防 X-Account-Id 冒充、满房 409）；
/// 5. 对称真人 PvP 唯一裁决（24 线程并发）+ 掉线 AI 托管唤醒阻塞屏障与重连交还 + 4 原版观战端点 + 聊天/回放/Ping；
/// 6. 3 套预设 + 5 大新模式端到端验证（无尽爬塔 Day 15+、跨英雄 40% 偏置、每日同种子、Boss 车轮战、沙盒全操作且 0 污染）；
/// 7. 客户端增量更新与 Mod/词典分发 HTTP 端点（Manifest/SHA-256/ETag 304/路径穿越拦截）。
/// </summary>
[Xunit.Collection("GlobalStaticState")]
public sealed class V13UltimateOverhaulTests
{
    private static readonly Lazy<SharedTestData> Shared = new(LoadSharedData);

    private sealed record SharedTestData(
        string RepoRoot,
        string DataRoot,
        string ModsRoot,
        string OverlayPath,
        GameDataRepository RawRepo,
        HashSet<string> ValidWhitelist,
        CardPool BasePool,
        List<BazaarServer.Data.MonsterTemplate> Monsters,
        LevelUpTable LevelUps,
        GameModeConfig GameMode);

    private static SharedTestData LoadSharedData()
    {
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        string overlayPath = Path.Combine(shared.DataDir, "translations", "custom_zh_overlay.json");
        Assert.True(File.Exists(shared.DbPath), $"未找到权威数据库：{shared.DbPath}");
        Assert.True(File.Exists(shared.ZhCnPath), $"未找到官方汉化包：{shared.ZhCnPath}");
        Assert.True(File.Exists(overlayPath), $"未找到外挂中文词典：{overlayPath}");
        Assert.True(Directory.Exists(Path.Combine(shared.ModsRoot, "bazaar-chronicles-sample")), $"未找到示例 Mod 目录：{shared.ModsRoot}");

        var validWhitelist = shared.CreateValidCardIdsCopy();
        Assert.True(validWhitelist.Count >= 3000, $"白名单资产 ID 数量异常：{validWhitelist.Count}");

        return new SharedTestData(
            shared.RepoRoot,
            shared.DataDir,
            shared.ModsRoot,
            overlayPath,
            shared.Repo,
            validWhitelist,
            shared.CreateIsolatedCardPool(),
            shared.CreateMonstersCopy(),
            shared.LevelUps,
            shared.Mode);
    }

    private static string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteTempDir(string dir)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 忽略临时目录清理竞争
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 域 1：真实加载示例 Mod（mods/bazaar-chronicles-sample/）+ 白名单防白块 + 热重载与开关
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test01_SampleMod_RealLoadValidation_VisualWhitelist_HotReloadAndToggle()
    {
        var data = Shared.Value;
        string stateDir = CreateTempDir("v13_modhost");
        try
        {
            // 1. 直接扫描 mods/bazaar-chronicles-sample/ 验证 0 错误、6 卡（4 物品 + 2 技能）、2 步链式事件、6 张 PNG
            var (scanned, scanIssues) = ModLoader.ScanDirectory(data.ModsRoot);
            Assert.Empty(scanIssues.Where(i => i.Severity == ModValidationSeverity.Error));
            var samplePkg = Assert.Single(scanned, p => p.Manifest.Id.Equals("bazaar-chronicles-sample", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(6, samplePkg.Cards.Count);
            Assert.Equal(4, samplePkg.Cards.Count(c => string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(2, samplePkg.Cards.Count(c => string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(2, samplePkg.Events.Count);
            Assert.Equal(6, samplePkg.Events.Sum(e => e.Steps.Count));
            Assert.Equal(6, samplePkg.AssetsByLogicalPath.Count);

            // 验证每张 PNG 资产与 assets/art_manifest.json 中登记的 SHA-256 100% 一致
            string artManifestPath = Path.Combine(data.ModsRoot, "bazaar-chronicles-sample", "assets", "art_manifest.json");
            Assert.True(File.Exists(artManifestPath));
            using (var artDoc = JsonDocument.Parse(File.ReadAllText(artManifestPath, Encoding.UTF8)))
            {
                var manifestAssets = artDoc.RootElement.GetProperty("assets");
                Assert.Equal(6, manifestAssets.GetArrayLength());
                foreach (var entry in manifestAssets.EnumerateArray())
                {
                    string file = entry.GetProperty("logicalPath").GetString()!;
                    string expectedSha = entry.GetProperty("sha256").GetString()!;
                    Assert.True(samplePkg.AssetsByLogicalPath.TryGetValue(file, out var loadedAsset), $"缺失贴图资产：{file}");
                    Assert.Equal(expectedSha, loadedAsset!.Sha256, ignoreCase: true);
                    Assert.True(loadedAsset.Width > 0 && loadedAsset.Height > 0);
                }
            }

            // 2. 启动 ServerModHost：验证拓扑校验 0 错误、2 个自定义词条完整加载、全部自定义卡进入 ActiveRepository 与视觉绑定表
            var modHost = new ServerModHost(
                data.RawRepo,
                data.DataRoot,
                stateDir,
                data.ValidWhitelist,
                modsRootOverride: data.ModsRoot,
                zhOverlayPathOverride: data.OverlayPath);

            Assert.False(modHost.LastReport.HasErrors, string.Join(" | ", modHost.LastReport.Issues.Select(i => i.Message)));
            Assert.Equal(0, modHost.LastReport.Issues.Count(i => i.Severity == ModValidationSeverity.Error));
            Assert.Equal(1, modHost.EnabledModCount);
            // ActiveRepository 中合成 6 张自定义卡 + 2 个事件 + 6 个步骤 = 14 个自定义模板
            Assert.Equal(14, modHost.LastReport.TotalCustomCards);
            Assert.Equal(2, modHost.LastReport.TotalCustomEvents);
            Assert.Equal(6, modHost.LastReport.TotalAssets);

            Assert.Equal(2, modHost.ModCustomMutators.Count);
            Assert.Contains(modHost.ModCustomMutators, m => m.Id == "bazaar-chronicles-sample:mutator_starport_boom" && !string.IsNullOrWhiteSpace(m.DescZh) && m.Parameters is { Count: > 0 });
            Assert.Contains(modHost.ModCustomMutators, m => m.Id == "bazaar-chronicles-sample:mutator_ember_tempest" && !string.IsNullOrWhiteSpace(m.DescZh) && m.Parameters is { Count: > 0 });

            foreach (var card in samplePkg.Cards)
            {
                Assert.True(modHost.ActiveRepository.CardsById.ContainsKey(card.CustomId), $"自定义卡未合入 ActiveRepository：{card.CustomId}");
                Assert.True(modHost.VisualBindingsByCustomId.TryGetValue(card.CustomId, out var binding), $"自定义卡缺少视觉绑定：{card.CustomId}");
                Assert.Contains(binding!.BaseVisualTemplateId, data.ValidWhitelist);
                Assert.False(string.IsNullOrWhiteSpace(binding.CustomArtSha256));
            }

            // 3. 强白名单 BaseVisualTemplateId 防白块拦截测试：伪造非法载体 ID 与错位 Size，必须被 ModValidator 拒绝
            var badFakeCarrierPkg = new ModPackage
            {
                RootDirectory = stateDir,
                Manifest = new ModManifest
                {
                    Id = "bad-whitebox-mod",
                    Name = "Bad Whitebox Mod",
                    Version = "1.0.0",
                    Scope = "singleplayer-only"
                },
                Cards =
                {
                    new CustomCardDefinition
                    {
                        CustomId = "bad_whitebox_item",
                        BaseVisualTemplateId = "00000000-0000-0000-0000-000000000000", // 不在白名单
                        Type = "Item",
                        Size = "Small",
                        StartingTier = "Bronze",
                        TitleZh = "非法白块测试卡"
                    }
                }
            };
            var (validBad, badIssues, rejectedBad) = ModValidator.ValidateAndSort([badFakeCarrierPkg], data.RawRepo, data.ValidWhitelist);
            Assert.Empty(validBad);
            Assert.Contains("bad-whitebox-mod", rejectedBad, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(badIssues, i => i.Severity == ModValidationSeverity.Error);

            // 再测同 Type 但错 Size（用 Small 卡载体装 Large 自定义卡 -> 3D 穿模必须拦截）
            var anySmallItem = data.RawRepo.CardsById.Values.First(c => c.Type == "Item" && c.Size == "Small" && data.ValidWhitelist.Contains(c.Id));
            var badSizePkg = new ModPackage
            {
                RootDirectory = stateDir,
                Manifest = new ModManifest
                {
                    Id = "bad-size-mismatch-mod",
                    Name = "Bad Size Mod",
                    Version = "1.0.0",
                    Scope = "singleplayer-only"
                },
                Cards =
                {
                    new CustomCardDefinition
                    {
                        CustomId = "bad_size_large_item",
                        BaseVisualTemplateId = anySmallItem.Id,
                        Type = "Item",
                        Size = "Large",
                        StartingTier = "Bronze",
                        TitleZh = "错尺寸穿模测试卡"
                    }
                }
            };
            var (validSizeBad, sizeIssues, rejectedSizeBad) = ModValidator.ValidateAndSort([badSizePkg], data.RawRepo, data.ValidWhitelist);
            Assert.Empty(validSizeBad);
            Assert.Contains("bad-size-mismatch-mod", rejectedSizeBad, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(sizeIssues, i => i.Severity == ModValidationSeverity.Error);

            // 4. 运行时开关切换（ToggleMod 禁用 -> 撤下自定义卡与词条；再次启用 -> 恢复；Reload 热重载幂等）
            string hashBeforeDisable = modHost.ModSetHash;
            var (foundOff, enabledOff, reportOff) = modHost.ToggleMod("bazaar-chronicles-sample", enabled: false);
            Assert.True(foundOff);
            Assert.False(enabledOff);
            Assert.Equal(0, modHost.EnabledModCount);
            Assert.Empty(modHost.ModCustomMutators);
            Assert.False(modHost.ActiveRepository.CardsById.ContainsKey(samplePkg.Cards[0].CustomId));
            Assert.NotEqual(hashBeforeDisable, modHost.ModSetHash);
            Assert.False(reportOff.HasErrors);

            var (foundOn, enabledOn, reportOn) = modHost.ToggleMod("bazaar-chronicles-sample", enabled: true);
            Assert.True(foundOn);
            Assert.True(enabledOn);
            Assert.Equal(1, modHost.EnabledModCount);
            Assert.Equal(2, modHost.ModCustomMutators.Count);
            Assert.True(modHost.ActiveRepository.CardsById.ContainsKey(samplePkg.Cards[0].CustomId));
            Assert.Equal(hashBeforeDisable, modHost.ModSetHash);
            Assert.False(reportOn.HasErrors);

            var reloadReport = modHost.Reload();
            Assert.False(reloadReport.HasErrors);
            Assert.Equal(14, reloadReport.TotalCustomCards);
        }
        finally
        {
            SafeDeleteTempDir(stateDir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 域 2：真实加载 custom_zh_overlay.json（1,416 条）+ 四层优先级 + 活跃缺口命中
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test02_ZhOverlay_RealLoad1416Entries_FourLayerPriority_AndActiveGapHits()
    {
        var data = Shared.Value;
        var overlayStore = new OverlayLocalizationStore(data.RawRepo.Localization, data.OverlayPath);
        var export = overlayStore.ExportMergedOverlay();

        // 1. 精确验证 1,416 条全部无损反序列化（957 hash + 5 card + 343 uiExact + 10 uiContext + 20 uiRegex + 48 serverExact + 33 serverRegex）
        Assert.Equal(1416, export.TotalOverlayEntryCount);
        Assert.False(string.IsNullOrWhiteSpace(export.Sha256));

        // 2. 验证外挂词典对活跃缺口（卡牌标题、UI 精确词条、上下文消歧、服务端报错精确与正则）100% 命中
        using var rawDoc = JsonDocument.Parse(File.ReadAllText(data.OverlayPath, Encoding.UTF8));
        var root = rawDoc.RootElement;

        // 2a. Hash 缺口覆盖命中
        var firstHash = root.GetProperty("hashOverrides").EnumerateObject().First();
        Assert.True(overlayStore.TryGetText(firstHash.Name, out var resolvedHashText));
        Assert.Equal(firstHash.Value.GetString(), resolvedHashText);

        // 2b. Card 缺口覆盖命中（全部 5 张补缺卡牌）
        foreach (var cardOv in root.GetProperty("cardOverrides").EnumerateObject())
        {
            if (data.RawRepo.CardsById.TryGetValue(cardOv.Name, out var cardTpl) &&
                cardOv.Value.TryGetProperty("title", out var titleProp) &&
                !string.IsNullOrWhiteSpace(titleProp.GetString()))
            {
                Assert.Equal(titleProp.GetString(), overlayStore.GetLocalizedTitle(cardTpl));
            }
        }

        // 2c. UI 精确覆盖与上下文消歧命中
        var firstUiExact = root.GetProperty("uiExactOverrides").EnumerateObject().First();
        Assert.Equal(firstUiExact.Value.GetString(), overlayStore.ResolveText(firstUiExact.Name));

        foreach (var ctxBlock in root.GetProperty("uiContextOverrides").EnumerateObject())
        {
            foreach (var kv in ctxBlock.Value.EnumerateObject())
            {
                string actualCtx = overlayStore.ResolveText(kv.Name, viewContext: ctxBlock.Name);
                Assert.Equal(kv.Value.GetString(), actualCtx);
            }
        }

        // 2d. 服务端消息精确覆盖命中（serverMessageOverrides.exact）
        var firstSrvExact = root.GetProperty("serverMessageOverrides").GetProperty("exact").EnumerateObject().First();
        Assert.Equal(firstSrvExact.Value.GetString(), overlayStore.ResolveText(firstSrvExact.Name));

        // 3. 验证严格四层优先级：Mod > 外挂 Overlay > 官方 zh-CN.bytes > 英文原文回退
        // Layer 4: 未知文本原样回退
        const string unknownEnglish = "V13_Completely_Unknown_Text_Token_9981";
        Assert.Equal(unknownEnglish, overlayStore.ResolveText(unknownEnglish));

        // Layer 3: 官方词库存在而外挂未覆盖的键命中官方
        var officialLoc = data.RawRepo.Localization!;
        var officialCard = data.RawRepo.CardsById.Values.First(c =>
        {
            string? k = c.Localization?.Title?.Key;
            return !string.IsNullOrWhiteSpace(k) &&
                   officialLoc.TryGetText(k, out var zh) &&
                   !string.IsNullOrWhiteSpace(zh) &&
                   !root.GetProperty("hashOverrides").TryGetProperty(k, out _) &&
                   !root.GetProperty("cardOverrides").TryGetProperty(c.Id, out _);
        });
        string officialTitleKey = officialCard.Localization!.Title!.Key!;
        Assert.True(officialLoc.TryGetText(officialTitleKey, out var officialZh));
        Assert.Equal(officialZh, overlayStore.GetLocalizedTitle(officialCard));

        // Layer 1 > Layer 2 > Layer 3: 注入 Mod 覆盖层后立即压制外挂词典与官方词库
        var modDoc = new LocalizationOverlayDocument
        {
            HashOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [firstHash.Name] = "【Mod最高优先级覆盖】",
                [officialTitleKey] = "【Mod覆盖官方卡牌标题】",
            },
            Entries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [firstUiExact.Name] = "【Mod覆盖UI词条】"
            }
        };
        overlayStore.SetModOverlays([modDoc]);

        Assert.True(overlayStore.TryGetText(firstHash.Name, out var modHashHit));
        Assert.Equal("【Mod最高优先级覆盖】", modHashHit);
        Assert.Equal("【Mod覆盖官方卡牌标题】", overlayStore.GetLocalizedTitle(officialCard));
        Assert.Equal("【Mod覆盖UI词条】", overlayStore.ResolveText(firstUiExact.Name));
    }

    // ════════════════════════════════════════════════════════════════════════
    // 域 3：SQLite 8 新表幂等迁移 + 模拟服务重启 100% 热恢复
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test03_Sqlite8Tables_IdempotentMigration_AndSimulatedRestartWarmRecovery()
    {
        string tempDir = CreateTempDir("v13_sqlite_restart");
        string dbPath = Path.Combine(tempDir, "bazaar.db");

        string lobbyId;
        string lobbyCode;
        Guid hostAccId;
        Guid guestAccId;
        string tokenGame;
        string tokenHub;
        string replayId;

        try
        {
            // ── 第一次启动：初始化 + 重复构造幂等迁移检查 + 写入全链路状态 ──
            using (var db1 = new Persistence(dbPath))
            using (var db1Repeat = new Persistence(dbPath))
            {
                // 验证 8 张新表全部存在
                string[] expected8Tables =
                [
                    "schema_migrations",
                    "account_tokens",
                    "tournament_lobbies",
                    "tournament_lobby_members",
                    "tournament_matchups",
                    "mode_leaderboards",
                    "match_history_and_replays",
                    "chat_messages"
                ];
                var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        existingTables.Add(reader.GetString(0));
                }
                foreach (string t in expected8Tables)
                    Assert.Contains(t, existingTables);

                using var accounts1 = new AccountStore(db1);
                var lobbies1 = new LobbyStore(db1);
                var sseHub1 = new SseEventHub();
                var social1 = new SocialAndReplayService(db1, sseHub1);
                lobbies1.AttachRuntimeHooks(sseHub1, social1);

                // 多端登录签发 2 枚令牌（game + hub）
                var hostAcc = accounts1.GetOrCreateByName("RestartHost", "game", "dev-pc");
                tokenGame = hostAcc.AccessToken;
                var hubTokens = accounts1.IssuePersistentTokens(hostAcc, "hub", "dev-hub");
                tokenHub = hubTokens.AccessToken;
                hostAccId = hostAcc.AccountId;

                var guestAcc = accounts1.GetOrCreateByName("RestartGuest", "game", "dev-guest");
                guestAccId = guestAcc.AccountId;

                // 创建锦标赛房间、配置自定义规则并加入成员
                var customRules = PresetCatalog.Create(EGameModePreset.EnhancedFun, ESpecialModeId.EndlessTower);
                customRules.StartingGold = 33;
                var lobby = lobbies1.Create(
                    host: hostAcc,
                    name: "V13 重启恢复测试房",
                    playMode: "Tournament",
                    playerCap: 4,
                    isPublic: true,
                    rules: customRules);
                lobbyId = lobby.LobbyId;
                lobbyCode = lobby.Code;

                var joinRes = lobbies1.JoinWithResult(lobbyCode, guestAcc);
                Assert.True(joinRes.Ok);

                // 写入房间聊天、战报回放与无尽排行榜
                db1.InsertChatMessage("lobby", lobbyId, "chat", hostAccId.ToString("N"), hostAcc.Username, "服务重启前测试发言", "{}");
                replayId = social1.SaveCombatReplay(new CombatReplayRecord(
                    ReplayId: "rep_v13_restart_001",
                    RunId: "run_v13_restart",
                    LobbyId: lobbyId,
                    Day: 12,
                    CombatType: "ENDLESS_PVP",
                    Seed: 20261122,
                    PlayerAccountId: hostAccId.ToString("N"),
                    PlayerUsername: hostAcc.Username,
                    PlayerHero: "Vanessa",
                    OpponentName: "RestartGuest",
                    OpponentHero: "Dooley",
                    PlayerWon: true,
                    DurationMs: 14200,
                    SummaryJson: """{"modeId":"endless_tower","hour":6,"p1RemainingHp":420,"p2RemainingHp":0,"p1DamageDealt":1800,"p2DamageDealt":600}""",
                    CombatSimMsgPack: [0x91, 0xA4, 0x54, 0x45, 0x53, 0x54]));

                social1.RecordRunLeaderboard(new RunLeaderboardSubmission(
                    ModeKey: "endless_tower",
                    AccountId: hostAccId.ToString("N"),
                    Username: hostAcc.Username,
                    Hero: "Vanessa",
                    SeasonOrDateKey: "all",
                    Score: 3450,
                    DayReached: 16,
                    Victories: 14,
                    Defeats: 2,
                    ScoreMultiplier: 1.25,
                    MutatorsJson: "[\"turbo_frenzy\"]",
                    FinalBoardSummaryJson: """{"items":5}"""));

                db1.FlushAllPending();
                _ = db1Repeat.DbPath;
            }

            // ── 模拟服务完全重启：销毁旧对象，使用全新实例从 SQLite 热恢复 ──
            SqliteConnection.ClearAllPools();
            using var db2 = new Persistence(dbPath);
            using var accounts2 = new AccountStore(db2);
            accounts2.LoadAllFromDb();
            var lobbies2 = new LobbyStore(db2);

            // 1. 多端 Token 100% 存活且指向同一账号
            var accFromGame = accounts2.FindByAccessToken(tokenGame);
            var accFromHub = accounts2.FindByAccessToken(tokenHub);
            Assert.NotNull(accFromGame);
            Assert.NotNull(accFromHub);
            Assert.Equal(hostAccId, accFromGame!.AccountId);
            Assert.Equal(hostAccId, accFromHub!.AccountId);

            // 2. 锦标赛房间、自定义规则、全部成员 100% 热恢复
            var restoredLobby = lobbies2.Get(lobbyId);
            Assert.NotNull(restoredLobby);
            Assert.Equal(lobbyCode, restoredLobby!.Code);
            Assert.Equal("V13 重启恢复测试房", restoredLobby.Name);
            Assert.Equal(4, restoredLobby.PlayerCap);
            Assert.Equal(EGameModePreset.EnhancedFun, restoredLobby.Rules.PresetId);
            Assert.Equal(ESpecialModeId.EndlessTower, restoredLobby.Rules.SpecialModeId);
            Assert.Equal(33, restoredLobby.Rules.StartingGold);
            Assert.Equal(2, restoredLobby.Members.Count);
            Assert.NotNull(lobbies2.FindMember(restoredLobby, hostAccId.ToString("N")));
            Assert.NotNull(lobbies2.FindMember(restoredLobby, guestAccId.ToString("N")));

            // 3. 聊天记录、战报回放（含 GZip Blob）与无尽排行榜 100% 热恢复
            var restoredChat = db2.QueryChatMessages("lobby", lobbyId, sinceId: 0, limit: 20);
            Assert.Contains(restoredChat, c => c.Content == "服务重启前测试发言");

            var restoredReplay = db2.LoadReplay(replayId);
            Assert.NotNull(restoredReplay);
            Assert.Equal(12, restoredReplay!.Day);
            Assert.Equal("ENDLESS_PVP", restoredReplay.CombatType);
            byte[]? restoredBlob = db2.LoadReplayMsgPack(replayId);
            Assert.NotNull(restoredBlob);
            Assert.Equal(new byte[] { 0x91, 0xA4, 0x54, 0x45, 0x53, 0x54 }, restoredBlob);

            var (lbItems, lbTotal) = db2.QueryLeaderboard("endless:all", heroFilter: null, firstAttemptOnly: false, page: 1, limit: 10);
            Assert.True(lbTotal >= 1);
            Assert.Contains(lbItems, row => row.AccountId == hostAccId.ToString("N") && row.Score == 3450 && row.DayReached == 16);
        }
        finally
        {
            SafeDeleteTempDir(tempDir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 域 4：多人安全红线（匿名 401、非房主 409、多端不互踢、防 X-Account-Id 冒充、满房 409）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test04_MultiplayerSecurityRedlines_Auth401_NonHost409_MultiDeviceTokens_NoSpoof_FullLobby409()
    {
        var data = Shared.Value;
        string tempDir = CreateTempDir("v13_sec_http");
        var (app, http, services) = await StartIntegrationServerAsync(tempDir, data);
        try
        {
            var accounts = services.GetRequiredService<AccountStore>();

            // 1. 匿名写操作一律 401 Unauthorized
            using (var anonLobby = await http.PostAsJsonAsync("/lobbies", new { name = "AnonRoom", playerCap = 2 }))
                Assert.Equal(HttpStatusCode.Unauthorized, anonLobby.StatusCode);

            using (var anonChat = await http.PostAsJsonAsync("/api/social/chat", new { channel = "global", content = "anonymous hello" }))
                Assert.Equal(HttpStatusCode.Unauthorized, anonChat.StatusCode);

            using (var anonSandbox = await http.PostAsJsonAsync("/api/sandbox/grant-card", new { templateId = "dummy" }))
                Assert.Equal(HttpStatusCode.Unauthorized, anonSandbox.StatusCode);

            // 2. 准备 3 名真实玩家，并为房主签发 2 枚并发多端 Token（game + hub）
            var host = accounts.GetOrCreateByName("SecHost", "game", "pc-1");
            string hostTokenGame = host.AccessToken;
            string hostTokenHub = accounts.IssuePersistentTokens(host, "hub", "hub-1").AccessToken;
            Assert.NotEqual(hostTokenGame, hostTokenHub);

            var guest = accounts.GetOrCreateByName("SecGuest", "game", "pc-2");
            var third = accounts.GetOrCreateByName("SecThird", "game", "pc-3");

            // 3. 严防 X-Account-Id 冒充（无 Bearer Token 仅伪造 X-Account-Id 必须返回 401 ACCOUNT_ID_SPOOF_BLOCKED）
            using (var spoofReq = new HttpRequestMessage(HttpMethod.Post, "/lobbies")
            {
                Content = JsonContent.Create(new { name = "SpoofedRoom", playerCap = 2 })
            })
            {
                spoofReq.Headers.Add("X-Account-Id", host.AccountId.ToString("N"));
                using var spoofResp = await http.SendAsync(spoofReq);
                Assert.Equal(HttpStatusCode.Unauthorized, spoofResp.StatusCode);
                string spoofBody = await spoofResp.Content.ReadAsStringAsync();
                Assert.Contains("ACCOUNT_ID_SPOOF_BLOCKED", spoofBody, StringComparison.OrdinalIgnoreCase);
            }

            // 4. 多端 Token 并存不互踢：先用 game token 建房（rules.playerCap = 2），再用 hub token 发聊天，两枚令牌同时有效
            string lobbyId;
            string lobbyCode;
            using (var createReq = AuthedJson(HttpMethod.Post, "/lobbies", hostTokenGame, new { name = "Cap2Room", isPublic = true, rules = new { playerCap = 2 } }))
            using (var createResp = await http.SendAsync(createReq))
            {
                string createBody = await createResp.Content.ReadAsStringAsync();
                Assert.True(createResp.IsSuccessStatusCode, createBody);
                using var doc = JsonDocument.Parse(createBody);
                lobbyId = doc.RootElement.GetProperty("LobbyId").GetString()!;
                lobbyCode = doc.RootElement.GetProperty("Code").GetString()!;
                Assert.Equal(2, doc.RootElement.GetProperty("PlayerCap").GetInt32());
            }

            using (var hubChatReq = AuthedJson(HttpMethod.Post, "/api/social/chat", hostTokenHub, new { channel = "global", content = "来自 Hub 端并发令牌的问候" }))
            using (var hubChatResp = await http.SendAsync(hubChatReq))
            {
                Assert.Equal(HttpStatusCode.OK, hubChatResp.StatusCode);
            }

            // 验证最初的 game token 依然存活（未被 hub token 踢掉）
            using (var gameMembershipReq = AuthedRequest(HttpMethod.Get, "/membership", hostTokenGame))
            using (var gameMembershipResp = await http.SendAsync(gameMembershipReq))
            {
                Assert.Equal(HttpStatusCode.OK, gameMembershipResp.StatusCode);
            }

            // 5. 第 2 名玩家加入坐满 2/2；非房主改规则/加 AI 返回 409 Conflict
            using (var join2Req = AuthedJson(HttpMethod.Post, $"/lobbies/{lobbyCode}/members", guest.AccessToken, new { hero = "Dooley" }))
            using (var join2Resp = await http.SendAsync(join2Req))
            {
                Assert.True(join2Resp.IsSuccessStatusCode, await join2Resp.Content.ReadAsStringAsync());
            }

            using (var nonHostRuleReq = AuthedJson(HttpMethod.Put, $"/lobbies/{lobbyId}/rules", guest.AccessToken, new { roundCountdownSeconds = 60 }))
            using (var nonHostRuleResp = await http.SendAsync(nonHostRuleReq))
            {
                Assert.Equal(HttpStatusCode.Conflict, nonHostRuleResp.StatusCode);
            }

            using (var nonHostBotReq = AuthedJson(HttpMethod.Post, $"/lobbies/{lobbyId}/bots", guest.AccessToken, new { count = 1 }))
            using (var nonHostBotResp = await http.SendAsync(nonHostBotReq))
            {
                Assert.Equal(HttpStatusCode.Conflict, nonHostBotResp.StatusCode);
            }

            // 6. 满房（2/2）绝不假返回 200：第 3 名真人尝试加入必须被拒（409 Conflict）
            using (var join3Req = AuthedJson(HttpMethod.Post, $"/lobbies/{lobbyCode}/members", third.AccessToken, new { hero = "Pygmalien" }))
            using (var join3Resp = await http.SendAsync(join3Req))
            {
                Assert.Equal(HttpStatusCode.Conflict, join3Resp.StatusCode);
                string body3 = await join3Resp.Content.ReadAsStringAsync();
                Assert.Contains("LOBBY_FULL", body3, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await StopIntegrationServerAsync(app, http, tempDir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 域 5：对称真人 PvP 唯一裁决 + 掉线 AI 托管与重连交还 + 4 原版观战端点 + 聊天/回放/Ping
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test05_SymmetricHumanPvp_DisconnectAiTakeoverAndReconnect_FourSpectateEndpoints_ChatReplayPing()
    {
        var data = Shared.Value;
        string tempDir = CreateTempDir("v13_pvp_spectate");
        var (app, http, services) = await StartIntegrationServerAsync(tempDir, data);
        string? attachedAliceSid = null;
        string? attachedBobSid = null;
        try
        {
            var accounts = services.GetRequiredService<AccountStore>();
            var lobbies = services.GetRequiredService<LobbyStore>();
            var sessions = services.GetRequiredService<SessionStore>();
            var db = services.GetRequiredService<Persistence>();
            var pvpCoord = services.GetRequiredService<TournamentPvpCoordinator>();
            var frameStore = services.GetRequiredService<SpectateFrameStore>();
            var socialSink = services.GetRequiredService<ISocialAndReplaySink>();
            var modHost = services.GetRequiredService<ServerModHost>();
            var cardPool = services.GetRequiredService<CardPool>();
            var ghosts = services.GetRequiredService<PvpGhostPool>();

            var alice = accounts.GetOrCreateByName("PvpAlice", "game");
            var bob = accounts.GetOrCreateByName("PvpBob", "game");
            string aliceAid = alice.AccountId.ToString("N");
            string bobAid = bob.AccountId.ToString("N");

            DateTime fakeNow = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
            lobbies.SetClockForTesting(() => fakeNow);

            var lobby = lobbies.Create(alice, "对称裁决与观战测试房", "Tournament", playerCap: 2, isPublic: true);
            Assert.True(lobbies.JoinWithResult(lobby.Code, bob).Ok);
            Guid tsid = Guid.NewGuid();
            lobbies.StartCountdown(lobby.LobbyId, tsid);

            // 1. 24 线程并发提交互相矛盾的 PvP 胜负提案 -> 必须 100% 锁定唯一裁决且严格满足 A 胜 ⇔ B 负
            const int concurrentCalls = 24;
            var results = new ConcurrentBag<SymmetricHumanPvpOutcome>();
            var barrier = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, concurrentCalls).Select(i => Task.Run(() =>
            {
                barrier.Wait();
                bool proposeAliceWins = (i % 2) == 0;
                var proposed = new SymmetricHumanPvpOutcome(
                    WinnerAccountId: proposeAliceWins ? aliceAid : bobAid,
                    LoserAccountId: proposeAliceWins ? bobAid : aliceAid,
                    BattleSeed: 777001 + i,
                    LoserPrestigeDamage: 1,
                    ReplayId: $"rep_sym_{i}",
                    CachedCombatSimForP1: null,
                    CachedCombatSimForP2: null);
                var chosen = pvpCoord.RecordOrGetResolvedMatch(tsid, day: 1, accountA: proposeAliceWins ? aliceAid : bobAid, accountB: proposeAliceWins ? bobAid : aliceAid, proposed);
                results.Add(chosen);
            })).ToArray();
            barrier.Set();
            await Task.WhenAll(tasks);

            Assert.Equal(concurrentCalls, results.Count);
            var distinctWinners = results.Select(r => r.WinnerAccountId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var distinctLosers = results.Select(r => r.LoserAccountId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Single(distinctWinners);
            Assert.Single(distinctLosers);
            Assert.NotEqual(distinctWinners[0], distinctLosers[0], StringComparer.OrdinalIgnoreCase);

            // 验证“A 胜 ⇔ B 负”双向对称查询完全一致
            var fromA = pvpCoord.TryGetResolvedMatch(tsid, 1, aliceAid, bobAid);
            var fromB = pvpCoord.TryGetResolvedMatch(tsid, 1, bobAid, aliceAid);
            Assert.NotNull(fromA);
            Assert.NotNull(fromB);
            bool aliceWinsFromA = string.Equals(fromA!.WinnerAccountId, aliceAid, StringComparison.OrdinalIgnoreCase);
            bool bobLosesFromB = string.Equals(fromB!.LoserAccountId, bobAid, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(aliceWinsFromA, bobLosesFromB);

            // 2. 掉线 AI 托管唤醒阻塞屏障 + 重连无缝交还
            // Alice 到达 Day 1 Hour 6 进入屏障等待，Bob 停留在 Hour 2 且断联
            var aliceMember = lobbies.FindMember(lobby, aliceAid)!;
            var bobMember = lobbies.FindMember(lobby, bobAid)!;
            aliceMember.CurrentDay = 1;
            aliceMember.CurrentHour = 6;
            aliceMember.IsReadyAtHour6Pvp = true;
            aliceMember.LastSeenAt = fakeNow;
            bobMember.CurrentDay = 1;
            bobMember.CurrentHour = 2;
            bobMember.IsReadyAtHour6Pvp = false;
            bobMember.LastSeenAt = fakeNow;

            var waitingDecision = lobbies.EvaluateRoundBarrier(lobby, day: 1, selfAccountId: aliceAid, sessions: sessions);
            Assert.False(waitingDecision.CanStartPvp);
            Assert.True(waitingDecision.WaitingForOtherPlayers);
            Assert.Equal(1, waitingDecision.NotReadyHumans);

            // 推进 46 秒（Alice 保持心跳，Bob 掉线超 45s -> disconnected，同时屏障首人就位超 30s -> 立刻转 ai_takeover！）
            fakeNow = fakeNow.AddSeconds(46);
            aliceMember.LastSeenAt = fakeNow;
            lobbies.RunWatchdogTick(sessions, pvpCoord);

            Assert.Equal("ai_takeover", bobMember.ConnectionState);
            var unlockedDecision = lobbies.EvaluateRoundBarrier(lobby, day: 1, selfAccountId: aliceAid, sessions: sessions);
            Assert.True(unlockedDecision.CanStartPvp, "Bob 转入 ai_takeover 后必须立刻解锁 Hour 6 屏障，绝不卡死全房！");

            // Bob 重连发心跳 -> 控制权无缝交还恢复 online
            fakeNow = fakeNow.AddSeconds(5);
            lobbies.Touch(bobAid);
            Assert.Equal("online", bobMember.ConnectionState);

            // 3. 为 Alice 与 Bob 挂载活跃会话并录制观战帧，实测全部 4 个原版观战端点 + 扩展 board-dto + 聊天 + 回放 + /api/ping
            var aliceRun = CreateRunWithEngine(aliceAid, alice.Username, EHero.Vanessa, PresetCatalog.Create(EGameModePreset.PureOfficial), cardPool, data.Monsters, data.LevelUps, data.GameMode, modHost.ActiveRepository, ghosts, out var aliceEngine, seed: 5001);
            var bobRun = CreateRunWithEngine(bobAid, bob.Username, EHero.Dooley, PresetCatalog.Create(EGameModePreset.PureOfficial), cardPool, data.Monsters, data.LevelUps, data.GameMode, modHost.ActiveRepository, ghosts, out var bobEngine, seed: 5002);

            var aliceSess = sessions.Create(aliceAid, alice.Username, new InitializeRunCommand { PlayMode = EPlayMode.Tournament, SelectedHero = EHero.Vanessa, TournamentSessionId = tsid });
            var bobSess = sessions.Create(bobAid, bob.Username, new InitializeRunCommand { PlayMode = EPlayMode.Tournament, SelectedHero = EHero.Dooley, TournamentSessionId = tsid });
            attachedAliceSid = aliceSess.Sid;
            attachedBobSid = bobSess.Sid;
            SessionRegistry.Attach(aliceSess.Sid, aliceRun, aliceEngine);
            SessionRegistry.Attach(bobSess.Sid, bobRun, bobEngine);

            // 录制一帧真实 MessagePack Bootstrap 供双轨观战读取
            byte[] fakeBootstrapMsgPack = Codec.WriteMessage(new NetMessageAggregate(new List<INetMessage>()));
            frameStore.RecordBootstrapSnapshot(bobAid, bobRun.RunId, tsid, requestId: 1, fakeBootstrapMsgPack);

            // 3a. 原版观战端点 #1: GET /spectate
            using (var r1 = await http.GetAsync("/spectate"))
            {
                Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
                string s1 = await r1.Content.ReadAsStringAsync();
                Assert.Contains(lobby.Code, s1, StringComparison.OrdinalIgnoreCase);
            }

            // 3b. 原版观战端点 #2: GET /spectate/{code}
            using (var r2 = await http.GetAsync($"/spectate/{lobby.Code}"))
            {
                Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
                string s2 = await r2.Content.ReadAsStringAsync();
                Assert.Contains("PvpAlice", s2, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("PvpBob", s2, StringComparison.OrdinalIgnoreCase);
            }

            // 3c. 原版观战端点 #3: GET /sessions/current/players/{id}/board（局内观战同房对手）
            using (var req3 = AuthedRequest(HttpMethod.Get, $"/sessions/current/players/{bobAid}/board", alice.AccessToken))
            using (var r3 = await http.SendAsync(req3))
            {
                Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
                byte[] bytes3 = await r3.Content.ReadAsByteArrayAsync();
                Assert.NotEmpty(bytes3);
            }

            // 3d. 原版观战端点 #4: GET /tournament/spectate/{watchCode}/players/{id}/board（大厅观战）
            using (var r4 = await http.GetAsync($"/tournament/spectate/{lobby.Code}/players/{bobAid}/board"))
            {
                Assert.Equal(HttpStatusCode.OK, r4.StatusCode);
                byte[] bytes4 = await r4.Content.ReadAsByteArrayAsync();
                Assert.NotEmpty(bytes4);
            }

            // 3e. 扩展观战结构化 JSON 盘面 DTO: GET /api/spectate/{code}/players/{id}/board-dto
            using (var rDto = await http.GetAsync($"/api/spectate/{lobby.Code}/players/{bobAid}/board-dto"))
            {
                Assert.Equal(HttpStatusCode.OK, rDto.StatusCode);
                string dtoJson = await rDto.Content.ReadAsStringAsync();
                Assert.Contains("PvpBob", dtoJson, StringComparison.OrdinalIgnoreCase);
            }

            // 3f. 社交聊天 POST + GET /api/social/chat
            using (var chatPostReq = AuthedJson(HttpMethod.Post, "/api/social/chat", alice.AccessToken, new { channel = "lobby", lobbyId = lobby.LobbyId, content = "准备打第 1 天 PvP！" }))
            using (var chatPostResp = await http.SendAsync(chatPostReq))
                Assert.Equal(HttpStatusCode.OK, chatPostResp.StatusCode);

            using (var chatGetResp = await http.GetAsync($"/api/social/chat?channel=lobby&lobbyId={lobby.LobbyId}"))
            {
                Assert.Equal(HttpStatusCode.OK, chatGetResp.StatusCode);
                using var chatDoc = JsonDocument.Parse(await chatGetResp.Content.ReadAsStringAsync());
                var contents = chatDoc.RootElement.GetProperty("items")
                    .EnumerateArray()
                    .Select(el => el.GetProperty("content").GetString())
                    .ToList();
                Assert.Contains("准备打第 1 天 PvP！", contents);
            }

            // 3g. 战报回放保存 + GET /api/replays/me + GET /api/replays/{id}
            string savedRepId = socialSink.SaveCombatReplay(new CombatReplayRecord(
                ReplayId: "",
                RunId: aliceRun.RunId,
                LobbyId: lobby.LobbyId,
                Day: 1,
                CombatType: "TOURNAMENT_PVP",
                Seed: 888123,
                PlayerAccountId: aliceAid,
                PlayerUsername: alice.Username,
                PlayerHero: "Vanessa",
                OpponentName: bob.Username,
                OpponentHero: "Dooley",
                PlayerWon: true,
                DurationMs: 6400,
                SummaryJson: """{"modeId":"standard","hour":6,"p1RemainingHp":210,"p2RemainingHp":0}""",
                CombatSimMsgPack: fakeBootstrapMsgPack));

            using (var repMeReq = AuthedRequest(HttpMethod.Get, "/api/replays/me", alice.AccessToken))
            using (var repMeResp = await http.SendAsync(repMeReq))
            {
                Assert.Equal(HttpStatusCode.OK, repMeResp.StatusCode);
                Assert.Contains(savedRepId, await repMeResp.Content.ReadAsStringAsync());
            }

            using (var repDetailResp = await http.GetAsync($"/api/replays/{savedRepId}"))
            {
                Assert.Equal(HttpStatusCode.OK, repDetailResp.StatusCode);
                Assert.Contains("TOURNAMENT_PVP", await repDetailResp.Content.ReadAsStringAsync());
            }

            // 3h. 轻量低延迟探针 GET /api/ping
            using (var pingResp = await http.GetAsync("/api/ping?ts=1732200000000&rtt=18"))
            {
                Assert.Equal(HttpStatusCode.OK, pingResp.StatusCode);
                using var pingDoc = JsonDocument.Parse(await pingResp.Content.ReadAsStringAsync());
                Assert.True(pingDoc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal(1732200000000L, pingDoc.RootElement.GetProperty("clientTs").GetInt64());
                Assert.True(pingDoc.RootElement.GetProperty("serverTs").GetInt64() > 0);
            }
        }
        finally
        {
            if (attachedAliceSid != null) SessionRegistry.Detach(attachedAliceSid);
            if (attachedBobSid != null) SessionRegistry.Detach(attachedBobSid);
            await StopIntegrationServerAsync(app, http, tempDir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 域 6：3 套预设 + 5 大新模式端到端验证（含沙盒 0 污染红线）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test06_ThreePresets_And_FiveNewModes_EndToEnd_IncludingSandboxZeroPollution()
    {
        var data = Shared.Value;
        string tempDir = CreateTempDir("v13_modes");
        var (app, http, services) = await StartIntegrationServerAsync(tempDir, data);
        string? sandboxSid = null;
        string? normalSid = null;
        try
        {
            var accounts = services.GetRequiredService<AccountStore>();
            var sessions = services.GetRequiredService<SessionStore>();
            var modHost = services.GetRequiredService<ServerModHost>();
            var cardPool = services.GetRequiredService<CardPool>();
            var ghosts = services.GetRequiredService<PvpGhostPool>();

            // ── 6.1 三套预设参数、积分倍率与标准池隔离校验 ──
            var pure = PresetCatalog.Create(EGameModePreset.PureOfficial);
            var fun = PresetCatalog.Create(EGameModePreset.EnhancedFun);
            var extreme = PresetCatalog.Create(EGameModePreset.ExtremeRoguelike);

            Assert.Equal(15, pure.StartingGold);
            Assert.False(pure.IsNonStandardRun);
            Assert.Equal(25, fun.StartingGold);
            Assert.Equal(350, fun.StartingHealth);
            Assert.True(fun.IsNonStandardRun);
            Assert.Equal(12, extreme.StartingGold);
            Assert.Equal(1.25, extreme.EnemyStatMultiplier);
            Assert.Equal(2.0, PresetCatalog.ById[EGameModePreset.ExtremeRoguelike].ScoreMultiplier);
            Assert.True(extreme.IsNonStandardRun);

            // ── 6.2 无尽爬塔（EndlessTower）：突破 10 胜持续推进至 Day 15+ 且动态缩放防溢出 ──
            var endlessRules = PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.EndlessTower);
            endlessRules.StartingPrestige = 50;
            var endlessRun = CreateRunWithEngine(
                "endless_acc", "EndlessRunner", EHero.Vanessa, endlessRules,
                cardPool, data.Monsters, data.LevelUps, data.GameMode, modHost.ActiveRepository, ghosts,
                out var endlessEngine, seed: 6001);

            // 模拟已达成 10 胜 3 负（V+D=13）且处于 Day 14 Hour 5，自然退出一次遭遇由引擎原生发牌真实 Day 14 Hour 6 PvP 卡，
            // 验证打完第 11 胜后绝不在 10 胜处提前终局，而是推进到 Day 15！
            endlessRun.Victories = 10;
            endlessRun.Defeats = 3;
            endlessRun.PlayerAttributes[EPlayerAttributeType.HealthMax] = 50000;
            endlessRun.Day = 14;
            endlessRun.Hour = 5;
            GrantDirectTestWeapon(endlessRun, modHost.ActiveRepository, damage: 100000);

            string h5Pick = endlessRun.SelectionSet[0];
            Assert.Null(endlessEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(h5Pick) }).Error);
            if (endlessRun.State != ERunState.Choice)
                Assert.Null(endlessEngine.Handle(new ExitCurrentStateCommand()).Error);
            while (endlessRun.State == ERunState.LevelUp)
                Assert.Null(endlessEngine.Handle(new ExitCurrentStateCommand()).Error);

            Assert.Equal(14u, endlessRun.Day);
            Assert.Equal(6u, endlessRun.Hour);
            string realPvpId = Assert.Single(endlessRun.SelectionSet);
            var endlessPvpOut = endlessEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(realPvpId) });
            Assert.Null(endlessPvpOut.Error);
            if (endlessRun.State != ERunState.Choice)
                Assert.Null(endlessEngine.Handle(new ExitCurrentStateCommand()).Error);
            while (endlessRun.State == ERunState.LevelUp)
                Assert.Null(endlessEngine.Handle(new ExitCurrentStateCommand()).Error);

            Assert.NotEqual(ERunState.EndRunVictory, endlessRun.State);
            Assert.True(endlessRun.Day >= 15, $"无尽爬塔突破 10 胜后应推进至 Day 15+，实际 Day={endlessRun.Day}");
            Assert.True(endlessRun.Victories >= 11);

            var scale15 = EndlessTowerScaler.Compute(15);
            var scale20 = EndlessTowerScaler.Compute(20);
            var scale99 = EndlessTowerScaler.Compute(99);
            Assert.True(scale15.IsMilestoneDay && scale15.EnemyHealthMultiplier > 2.0);
            Assert.True(scale20.EnemyHealthMultiplier > scale15.EnemyHealthMultiplier);
            Assert.Equal(CardTier.Legendary, scale20.MinimumGhostItemTier);
            Assert.Equal(EndlessTowerScaler.MaxEnemyHealthCap, EndlessTowerScaler.ScaleEnemyHealth(500_000, 99));
            Assert.True(scale99.EnemyCooldownReductionPct <= EndlessTowerScaler.MaxCooldownReductionPct);

            // ── 6.3 跨英雄混池（CrossHeroMayhem）：40% 本英雄偏置保护 + 真实混入跨英雄卡 ──
            var crossRules = PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.CrossHeroMayhem);
            Assert.Equal(0.40, crossRules.CrossHeroOwnHeroBias);
            var allItemsQuery = JsonSerializer.Deserialize<RuleNode>("""
                {
                  "$type": "TSpawnContextQuery",
                  "Limit": { "$type": "TFixedValue", "Value": 1 },
                  "Groups": [
                    {
                      "Filters": [
                        { "$type": "TSpawnFilterCardType", "CardTypes": ["Item"] }
                      ]
                    }
                  ]
                }
                """)!;
            int ownHeroHits = 0;
            int otherHeroHits = 0;
            const int sampleTrials = 400;
            var crossPlayer = new PlayerState("cross_p1", "Vanessa", HeroNames.Vanessa, 500, 20, 5, 10) { Day = 5 };
            for (int i = 0; i < sampleTrials; i++)
            {
                var spawnCtx = new RuleExecutionContext(modHost.ActiveRepository, RuleScope.OutOfCombat, crossPlayer, null, new Random(9000 + i));
                var picks = SpawnQueryEngine.SpawnCards(
                    allItemsQuery,
                    spawnCtx,
                    defaultCountOverride: 1,
                    ownedMaxTiers: null,
                    validAssetIds: data.ValidWhitelist,
                    archetypeResolver: null,
                    rules: crossRules);
                if (picks.Count == 0) continue;
                var tpl = picks[0].Template;
                bool isOwn = tpl.Heroes.Any(h => h.Equals("Vanessa", StringComparison.OrdinalIgnoreCase));
                bool isOtherHero = tpl.Heroes.Any(h =>
                    !h.Equals("Vanessa", StringComparison.OrdinalIgnoreCase) &&
                    !h.Equals("Common", StringComparison.OrdinalIgnoreCase));
                if (isOwn) ownHeroHits++;
                else if (isOtherHero) otherHeroHits++;
            }
            double ownRatio = ownHeroHits / (double)sampleTrials;
            Assert.True(otherHeroHits > 50, $"跨英雄混池必须真实抽到大量外英雄物品，实际={otherHeroHits}");
            Assert.InRange(ownRatio, 0.25, 0.60);

            // ── 6.4 每日同种子挑战（DailyChallenge）：同日期双账号 100% 确定性一致 ──
            const string fixedDate = "2026-11-22";
            var dailySpecA = DailyChallengeSeedGenerator.GenerateSpec(fixedDate);
            var dailySpecB = DailyChallengeSeedGenerator.GenerateSpec(fixedDate);
            Assert.Equal(dailySpecA.MasterSeed, dailySpecB.MasterSeed);
            Assert.Equal(dailySpecA.FeaturedHero, dailySpecB.FeaturedHero);
            Assert.Equal(dailySpecA.MutatorIds, dailySpecB.MutatorIds);
            Assert.InRange(dailySpecA.MutatorIds.Count, 2, 3);

            var dailyHero = Enum.TryParse<EHero>(dailySpecA.FeaturedHero, true, out var parsedHero) ? parsedHero : EHero.Vanessa;
            var dailyRunA = CreateRunWithEngine("daily_u1", "DailyA", dailyHero, dailySpecA.RuleSet.CloneNormalized(), cardPool, data.Monsters, data.LevelUps, data.GameMode, modHost.ActiveRepository, ghosts, out _, seed: dailySpecA.MasterSeed);
            var dailyRunB = CreateRunWithEngine("daily_u2", "DailyB", dailyHero, dailySpecB.RuleSet.CloneNormalized(), cardPool, data.Monsters, data.LevelUps, data.GameMode, modHost.ActiveRepository, ghosts, out _, seed: dailySpecB.MasterSeed);

            var selTplsA = dailyRunA.SelectionSet.Select(id => dailyRunA.Cards[id].TemplateId).ToList();
            var selTplsB = dailyRunB.SelectionSet.Select(id => dailyRunB.Cards[id].TemplateId).ToList();
            Assert.Equal(selTplsA, selTplsB);

            using (var dailyTodayResp = await http.GetAsync("/api/daily-challenge/today"))
            {
                Assert.Equal(HttpStatusCode.OK, dailyTodayResp.StatusCode);
                using var dDoc = JsonDocument.Parse(await dailyTodayResp.Content.ReadAsStringAsync());
                Assert.True(dDoc.RootElement.GetProperty("masterSeed").GetInt32() != 0);
                Assert.True(dDoc.RootElement.GetProperty("mutators").GetArrayLength() >= 2);
            }

            // ── 6.5 Boss 车轮战（BossRush）：10 波真实 Boss 映射 + 非里程碑跳过 PvP + 波次推进 ──
            for (int wave = 1; wave <= 10; wave++)
            {
                var waveMonsters = BossRushCatalog.ResolveWaveMonsters(wave, modHost.ActiveRepository);
                Assert.True(waveMonsters.Count >= 3, $"Boss 车轮战第 {wave} 波解析到的真实 Boss 不足 3 只");
            }
            Assert.False(BossRushCatalog.GetWave(1).IsMilestonePvpWave);
            Assert.True(BossRushCatalog.GetWave(5).IsMilestonePvpWave);
            Assert.True(BossRushCatalog.GetWave(10).IsMilestonePvpWave);

            var bossRules = PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.BossRush);
            var bossRun = CreateRunWithEngine("boss_acc", "BossSlayer", EHero.Vanessa, bossRules, cardPool, data.Monsters, data.LevelUps, data.GameMode, modHost.ActiveRepository, ghosts, out var bossEngine, seed: 6005);
            bossRun.PlayerAttributes[EPlayerAttributeType.HealthMax] = 25000;
            GrantDirectTestWeapon(bossRun, modHost.ActiveRepository, damage: 30000);

            // 推进 Day 1 到 Hour 3（PvE Boss 战），打赢第 1 波 Boss
            AdvanceToHour3Combat(bossRun, bossEngine);
            Assert.Equal(3u, bossRun.Hour);
            var bossOptionId = bossRun.SelectionSet.First(id => bossRun.Cards[id].Type == ECardType.CombatEncounter);
            Assert.Null(bossEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(bossOptionId) }).Error);
            Assert.Null(bossEngine.Handle(new ExitCurrentStateCommand()).Error);
            Assert.True(bossRun.BossRushWavesCleared >= 1, $"击败第 1 波 Boss 后 BossRushWavesCleared 应递增，实际={bossRun.BossRushWavesCleared}");

            // ── 6.6 沙盒实验室（SandboxLab）：直发/改卡/改属性解锁10格/自镜像测试 + 非沙盒 409 + 标准幽灵池与生涯 0 污染 ──
            var sandboxAcc = accounts.GetOrCreateByName("SandboxLabUser", "game");
            var normalAcc = accounts.GetOrCreateByName("NormalOnlyUser", "game");
            string sandboxAid = sandboxAcc.AccountId.ToString("N");
            string normalAid = normalAcc.AccountId.ToString("N");

            int stdGhostsBefore = ghosts.Count - ghosts.CustomRuleGhostCount;
            int careerRunsBefore = sandboxAcc.RunsCompleted;
            int careerWinsBefore = sandboxAcc.Victories;
            int careerLossesBefore = sandboxAcc.Defeats;

            var sbRules = PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.SandboxLab);
            var sbRun = CreateRunWithEngine(sandboxAid, sandboxAcc.Username, EHero.Vanessa, sbRules, cardPool, data.Monsters, data.LevelUps, data.GameMode, modHost.ActiveRepository, ghosts, out var sbEngine, seed: 6006);
            var sbSess = sessions.Create(sandboxAid, sandboxAcc.Username, new InitializeRunCommand { PlayMode = EPlayMode.Unranked, SelectedHero = EHero.Vanessa });
            sandboxSid = sbSess.Sid;
            SessionRegistry.Attach(sbSess.Sid, sbRun, sbEngine);

            var normRun = CreateRunWithEngine(normalAid, normalAcc.Username, EHero.Vanessa, PresetCatalog.Create(EGameModePreset.PureOfficial), cardPool, data.Monsters, data.LevelUps, data.GameMode, modHost.ActiveRepository, ghosts, out var normEngine, seed: 6007);
            var normSess = sessions.Create(normalAid, normalAcc.Username, new InitializeRunCommand { PlayMode = EPlayMode.Unranked, SelectedHero = EHero.Vanessa });
            normalSid = normSess.Sid;
            SessionRegistry.Attach(normSess.Sid, normRun, normEngine);

            // 非沙盒模式调用沙盒接口必须返回 409 SANDBOX_ONLY
            using (var forbiddenSbReq = AuthedJson(HttpMethod.Post, "/api/sandbox/set-stats", normalAcc.AccessToken, new { gold = 9999 }))
            using (var forbiddenSbResp = await http.SendAsync(forbiddenSbReq))
            {
                Assert.Equal(HttpStatusCode.Conflict, forbiddenSbResp.StatusCode);
                Assert.Contains("SANDBOX_ONLY", await forbiddenSbResp.Content.ReadAsStringAsync());
            }

            // 沙盒：调整属性并一键解锁全部 10 槽位
            using (var statsReq = AuthedJson(HttpMethod.Post, "/api/sandbox/set-stats", sandboxAcc.AccessToken, new { gold = 777, healthMax = 2000, unlockAllSlots = true }))
            using (var statsResp = await http.SendAsync(statsReq))
            {
                Assert.Equal(HttpStatusCode.OK, statsResp.StatusCode);
                Assert.Equal(777, sbRun.PlayerAttributes[EPlayerAttributeType.Gold]);
                Assert.Equal(2000, sbRun.PlayerAttributes[EPlayerAttributeType.HealthMax]);
                Assert.Equal((ushort)1023, sbRun.UnlockedSlots);
            }

            // 沙盒：直发一把真实原版 Small 武器（Fang: 7cbc8056-482b-4ccf-887a-ddb2702c856c）
            const string fangId = "7cbc8056-482b-4ccf-887a-ddb2702c856c";
            string grantedInstId;
            using (var grantReq = AuthedJson(HttpMethod.Post, "/api/sandbox/grant-card", sandboxAcc.AccessToken, new { templateId = fangId, tier = "Gold", targetSection = "Hand" }))
            using (var grantResp = await http.SendAsync(grantReq))
            {
                string grantBody = await grantResp.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, grantResp.StatusCode);
                using var gDoc = JsonDocument.Parse(grantBody);
                Assert.True(gDoc.RootElement.GetProperty("ok").GetBoolean());
                grantedInstId = gDoc.RootElement.GetProperty("data").GetProperty("highlightInstanceId").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(grantedInstId));
            }

            // 沙盒：修改该卡牌品阶至 Diamond、附魔 Fiery、叠加永久伤害 +80
            using (var modReq = AuthedJson(HttpMethod.Post, "/api/sandbox/modify-card", sandboxAcc.AccessToken, new
            {
                instanceId = grantedInstId,
                tier = "Diamond",
                enchantment = "Fiery",
                attributeDeltas = new Dictionary<string, double> { ["DamageAmount"] = 80 }
            }))
            using (var modResp = await http.SendAsync(modReq))
            {
                Assert.Equal(HttpStatusCode.OK, modResp.StatusCode);
                Assert.Equal(ETier.Diamond, sbRun.Cards[grantedInstId].Tier);
                Assert.Equal(EEnchantmentType.Fiery, sbRun.Cards[grantedInstId].Enchantment);
            }

            // 沙盒：执行自镜像零副作用战斗测试
            using (var testCombatReq = AuthedJson(HttpMethod.Post, "/api/sandbox/test-combat", sandboxAcc.AccessToken, new { opponentSource = "self_mirror", seed = 4242 }))
            using (var testCombatResp = await http.SendAsync(testCombatReq))
            {
                string combatBody = await testCombatResp.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, testCombatResp.StatusCode);
                using var cDoc = JsonDocument.Parse(combatBody);
                Assert.True(cDoc.RootElement.GetProperty("ok").GetBoolean());
                Assert.True(cDoc.RootElement.GetProperty("durationMs").GetInt32() > 0);
            }

            // 严格验证沙盒 0 污染红线：标准幽灵池计数不变、账号生涯胜负次数不变！
            Assert.Equal(stdGhostsBefore, ghosts.Count - ghosts.CustomRuleGhostCount);
            Assert.Equal(careerRunsBefore, sandboxAcc.RunsCompleted);
            Assert.Equal(careerWinsBefore, sandboxAcc.Victories);
            Assert.Equal(careerLossesBefore, sandboxAcc.Defeats);
        }
        finally
        {
            if (sandboxSid != null) SessionRegistry.Detach(sandboxSid);
            if (normalSid != null) SessionRegistry.Detach(normalSid);
            await StopIntegrationServerAsync(app, http, tempDir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 域 7：客户端热更新与 Mod / 词典分发 HTTP 端点（Manifest / SHA-256 / ETag 304 / 路径穿越拦截）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test07_ClientUpdate_And_ModDistributionEndpoints_Sha256_ETag304_PathTraversalBlock()
    {
        var data = Shared.Value;
        string tempDir = CreateTempDir("v13_dist_endpoints");
        string payloadDir = Path.Combine(tempDir, "client-payload");
        Directory.CreateDirectory(payloadDir);

        byte[] dummyLauncherBytes = Encoding.UTF8.GetBytes("BAZAAR_LAUNCHER_PAYLOAD_V13_TEST_BINARY");
        string dummyLauncherPath = Path.Combine(payloadDir, "BazaarLauncher.dll");
        File.WriteAllBytes(dummyLauncherPath, dummyLauncherBytes);
        string expectedLauncherSha = Convert.ToHexString(SHA256.HashData(dummyLauncherBytes)).ToLowerInvariant();

        string? prevEnvPayload = Environment.GetEnvironmentVariable("BAZAAR_CLIENT_PAYLOAD");
        Environment.SetEnvironmentVariable("BAZAAR_CLIENT_PAYLOAD", payloadDir);
        var (app, http, _) = await StartIntegrationServerAsync(tempDir, data);
        try
        {
            // 1. GET /api/client-update/manifest + ETag 304 + 文件流下载 SHA-256 校验
            string updateManifestEtag;
            using (var mResp = await http.GetAsync("/api/client-update/manifest"))
            {
                Assert.Equal(HttpStatusCode.OK, mResp.StatusCode);
                updateManifestEtag = mResp.Headers.ETag!.Tag;
                using var doc = JsonDocument.Parse(await mResp.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.GetProperty("payloadDirAvailable").GetBoolean());
                Assert.Equal(1, doc.RootElement.GetProperty("fileCount").GetInt32());
                var file0 = doc.RootElement.GetProperty("files")[0];
                Assert.Equal("BazaarLauncher.dll", file0.GetProperty("name").GetString());
                Assert.Equal(expectedLauncherSha, file0.GetProperty("sha256").GetString());
            }

            using (var m304Req = new HttpRequestMessage(HttpMethod.Get, "/api/client-update/manifest"))
            {
                m304Req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(updateManifestEtag));
                using var m304Resp = await http.SendAsync(m304Req);
                Assert.Equal(HttpStatusCode.NotModified, m304Resp.StatusCode);
            }

            using (var dlResp = await http.GetAsync("/api/client-update/files/BazaarLauncher.dll"))
            {
                Assert.Equal(HttpStatusCode.OK, dlResp.StatusCode);
                byte[] downloaded = await dlResp.Content.ReadAsByteArrayAsync();
                Assert.Equal(dummyLauncherBytes, downloaded);
                Assert.Equal(expectedLauncherSha, dlResp.Headers.GetValues("X-File-Sha256").First());
            }

            // 路径穿越攻击必须被严格拦截返回 400 BadRequest
            using (var travResp = await http.GetAsync("/api/client-update/files/..%2F..%2Fsecret.txt"))
                Assert.Equal(HttpStatusCode.BadRequest, travResp.StatusCode);

            // 2. GET /api/mods/manifest + ETag 304 + GET /api/mods/assets/{sha256} 真实 PNG 校验
            string modManifestEtag;
            string firstAssetSha;
            using (var modResp = await http.GetAsync("/api/mods/manifest"))
            {
                Assert.Equal(HttpStatusCode.OK, modResp.StatusCode);
                modManifestEtag = modResp.Headers.ETag!.Tag;
                using var doc = JsonDocument.Parse(await modResp.Content.ReadAsStringAsync());
                Assert.Equal(6, doc.RootElement.GetProperty("customCards").GetArrayLength());
                Assert.Equal(2, doc.RootElement.GetProperty("customEvents").GetArrayLength());
                Assert.Equal(2, doc.RootElement.GetProperty("customMutators").GetArrayLength());
                var assetsArr = doc.RootElement.GetProperty("assets");
                Assert.Equal(6, assetsArr.GetArrayLength());
                firstAssetSha = assetsArr[0].GetProperty("sha256").GetString()!;
            }

            using (var mod304Req = new HttpRequestMessage(HttpMethod.Get, "/api/mods/manifest"))
            {
                mod304Req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(modManifestEtag));
                using var mod304Resp = await http.SendAsync(mod304Req);
                Assert.Equal(HttpStatusCode.NotModified, mod304Resp.StatusCode);
            }

            using (var assetResp = await http.GetAsync($"/api/mods/assets/{firstAssetSha}"))
            {
                Assert.Equal(HttpStatusCode.OK, assetResp.StatusCode);
                byte[] pngBytes = await assetResp.Content.ReadAsByteArrayAsync();
                // 校验真实 PNG 文件魔数 89 50 4E 47 与内容 SHA-256 精确吻合
                Assert.True(pngBytes.Length > 8);
                Assert.Equal(0x89, pngBytes[0]);
                Assert.Equal(0x50, pngBytes[1]);
                Assert.Equal(0x4E, pngBytes[2]);
                Assert.Equal(0x47, pngBytes[3]);
                string actualSha = Convert.ToHexString(SHA256.HashData(pngBytes)).ToLowerInvariant();
                Assert.Equal(firstAssetSha, actualSha, ignoreCase: true);
            }

            // 3. GET /api/localization/overlay（校验全量 1,416+ 词条、X-Localization-Entries 头与 ETag 304）
            string locEtag;
            using (var locResp = await http.GetAsync("/api/localization/overlay"))
            {
                Assert.Equal(HttpStatusCode.OK, locResp.StatusCode);
                locEtag = locResp.Headers.ETag!.Tag;
                Assert.True(locResp.Headers.TryGetValues("X-Localization-Entries", out var entriesVals));
                int entryCountHeader = int.Parse(entriesVals.First());
                Assert.True(entryCountHeader >= 1416, $"合并 Mod 后的外挂词典条数应 >= 1416，实际={entryCountHeader}");
            }

            using (var loc304Req = new HttpRequestMessage(HttpMethod.Get, "/api/localization/overlay"))
            {
                loc304Req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(locEtag));
                using var loc304Resp = await http.SendAsync(loc304Req);
                Assert.Equal(HttpStatusCode.NotModified, loc304Resp.StatusCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_CLIENT_PAYLOAD", prevEnvPayload);
            await StopIntegrationServerAsync(app, http, tempDir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 内部集成测试辅助方法（进程内真实 Kestrel HTTP 服务器与对局构造器）
    // ════════════════════════════════════════════════════════════════════════

    private static async Task<(WebApplication App, HttpClient Http, IServiceProvider Services)> StartIntegrationServerAsync(
        string tempDir,
        SharedTestData data)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        string dbPath = Path.Combine(tempDir, "bazaar.db");
        string stateDir = Path.Combine(tempDir, "state");
        Directory.CreateDirectory(stateDir);

        var persistence = new Persistence(dbPath);
        var accounts = new AccountStore(persistence);
        var playerRules = new PlayerRuleStore(persistence);
        var sessions = new SessionStore();
        var lobbies = new LobbyStore(persistence);
        var modHost = new ServerModHost(
            data.RawRepo,
            data.DataRoot,
            stateDir,
            data.ValidWhitelist,
            modsRootOverride: data.ModsRoot,
            zhOverlayPathOverride: data.OverlayPath);
        var cardPool = CardPool.FromRepository(
            modHost.ActiveRepository,
            data.DataRoot,
            validWhitelistOverride: data.ValidWhitelist,
            visualBindings: modHost.VisualBindingsByCustomId);
        var monsters = MonsterPool.FromRepository(modHost.ActiveRepository);
        var levelUps = LevelUpTable.FromRepository(modHost.ActiveRepository);
        var gameMode = GameModeConfig.FromRepository(modHost.ActiveRepository);
        var ghosts = TestInfrastructure.SharedGameDataFixture.Shared.Ghosts;
        modHost.BindPools(cardPool, monsters);

        builder.Services.AddSingleton(persistence);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(playerRules);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(lobbies);
        builder.Services.AddSingleton(modHost);
        builder.Services.AddSingleton<OverlayLocalizationStore>(_ => modHost.ActiveLocalization);
        builder.Services.AddSingleton<GameDataRepository>(_ => modHost.BaseRepository);
        builder.Services.AddSingleton(cardPool);
        builder.Services.AddSingleton(monsters);
        builder.Services.AddSingleton(levelUps);
        builder.Services.AddSingleton(gameMode);
        builder.Services.AddSingleton(ghosts);

        builder.Services.AddSingleton<ISandboxEngineBridge, SandboxEngineService>();
        MultiplayerEndpointModule.RegisterServices(builder.Services);

        var app = builder.Build();
        TournamentEndpoints.Map(app, lobbies);
        CustomRuleEndpoints.Map(app, playerRules, accounts, () => new { archetypes = Array.Empty<object>() }, sessions, persistence);
        ModEndpoints.Map(app);
        ClientUpdateEndpoints.Map(app, data.DataRoot);
        MultiplayerEndpointModule.MapEndpoints(app);

        await app.StartAsync();
        string baseUrl = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };
        return (app, http, app.Services);
    }

    private static async Task StopIntegrationServerAsync(WebApplication app, HttpClient http, string tempDir)
    {
        try { http.Dispose(); } catch { }
        try { await app.StopAsync(); } catch { }
        try { await app.DisposeAsync(); } catch { }
        SafeDeleteTempDir(tempDir);
    }

    private static HttpRequestMessage AuthedRequest(HttpMethod method, string path, string bearerToken)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return req;
    }

    private static HttpRequestMessage AuthedJson(HttpMethod method, string path, string bearerToken, object payload)
    {
        var req = AuthedRequest(method, path, bearerToken);
        req.Content = JsonContent.Create(payload);
        return req;
    }

    private static RunState CreateRunWithEngine(
        string accountId,
        string username,
        EHero hero,
        CustomRunRuleSet rules,
        CardPool pool,
        List<BazaarServer.Data.MonsterTemplate> monsters,
        LevelUpTable levelUps,
        GameModeConfig mode,
        GameDataRepository repo,
        PvpGhostPool ghosts,
        out RunEngine engine,
        int seed)
    {
        var run = new RunState
        {
            RunId = "v13_" + Guid.NewGuid().ToString("N")[..10],
            AccountId = accountId,
            Username = username,
            GameModeId = pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            RngSeed = seed,
            CustomRules = rules.CloneNormalized(),
        };
        engine = new RunEngine(run, pool, monsters, levelUps, mode, repo, ghosts);
        engine.StartRun();
        return run;
    }

    private static void GrantDirectTestWeapon(RunState run, GameDataRepository repo, double damage)
    {
        var fangTpl = repo.GetCard("7cbc8056-482b-4ccf-887a-ddb2702c856c")!;
        var core = new CardInstance(fangTpl, CardTier.Diamond);
        core.PermanentAdd["DamageAmount"] = damage;
        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(fangTpl.Id),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Diamond,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = core,
        };
        run.Cards[entry.InstanceId] = entry;
    }

    private static void AdvanceToHour3Combat(RunState run, RunEngine engine)
    {
        int guard = 0;
        while (run.Hour < 3 && guard++ < 10)
        {
            if (run.State == ERunState.Choice && run.SelectionSet.Count > 0)
            {
                string pick = run.SelectionSet[0];
                Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pick) }).Error);
            }
            if (run.State != ERunState.Choice)
            {
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            }
        }
    }
}
