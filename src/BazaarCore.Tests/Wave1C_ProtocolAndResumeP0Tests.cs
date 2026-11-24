using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Protocol;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1C 回归测试（文件 1）：覆盖 P0-13（MoveItem 局外变形退全量快照 + 事件实体收集 + TTriggerOnCardMoved 补检）、
/// P0-14（ActivateEncounterCard 严禁伪造同包 CardDealt+CardPurchased）、
/// P0-15（POST /sessions 两阶段反序列化 + 锦标赛路由 LoadLatestTournamentRun + 规范化 SelectedModIds + 缺包占位空安全）、
/// P0-16（持久化 LastCompletedPvpDay 根治 BossRush 与同步 PvP 平局跨天死循环）。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1C_ProtocolAndResumeP0Tests
{
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";

    private static byte[] SerializeCommand(INetCommand cmd)
        => MessagePackSerializer.Serialize<INetCommand>(cmd, Codec.Options);

    private static CardTemplate PickSmallItemTemplate(GameDataRepository repo, string? excludeId = null)
        => repo.CardsById.Values.First(c =>
            string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Size, "Small", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(c.Id, out _) &&
            !string.Equals(c.Id, excludeId, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void P0_13_MoveItem_TriggeringOutOfCombatTransform_MustFallbackToFullSimAndIncludeNewCard()
    {
        var shared = SharedGameDataFixture.Shared;
        var targetTpl = PickSmallItemTemplate(shared.Repo, excludeId: CutlassId);

        // 构造一张无任何光环（Auras 为空）、仅挂 TTriggerOnCardMoved 触发局外变形的自定义小件物品
        string moverGuid = Guid.NewGuid().ToString("D");
        var moverCardJson = JsonSerializer.Serialize(new
        {
            Id = moverGuid,
            Type = "Item",
            InternalName = "Wave1C_MoveTransformProbe",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Tool" },
            HiddenTags = Array.Empty<string>(),
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                    Attributes = new Dictionary<string, double>
                    {
                        ["BuyPrice"] = 2,
                        ["SellPrice"] = 1,
                        ["CooldownMax"] = 4000
                    }
                }
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    ActiveIn = "HandAndStash",
                    Trigger = new Dictionary<string, object>
                    {
                        ["$type"] = "TTriggerOnCardMoved",
                        ["Subject"] = new Dictionary<string, object> { ["$type"] = "TTargetCardSelf" }
                    },
                    Action = new Dictionary<string, object>
                    {
                        ["$type"] = "TActionCardTransform",
                        ["Target"] = new Dictionary<string, object> { ["$type"] = "TTargetCardSelf" },
                        ["SpawnContext"] = new Dictionary<string, object>
                        {
                            ["$type"] = "TSpawnContextQuery",
                            ["Groups"] = new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    ["$type"] = "TSpawnGroup",
                                    ["Filters"] = new object[]
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["$type"] = "TSpawnFilterIdList",
                                            ["Ids"] = new[] { targetTpl.Id }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            },
            Auras = new Dictionary<string, object>()
        });

        using var overlay = shared.CreateMemoryOverlay(customCards: [(moverGuid, moverCardJson)]);
        var run = new RunState
        {
            RunId = $"w1c_p013_{Guid.NewGuid():N}",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 2,
            Hour = 2,
            State = ERunState.Choice,
            StartSelectionDone = true,
            RngSeed = 20260113
        };
        var engine = new RunEngine(
            run,
            overlay.Pool,
            overlay.Monsters,
            overlay.LevelUps,
            overlay.Mode,
            overlay.Repo,
            shared.Ghosts,
            shared.ArchetypeIndex);
        engine.StartRun();
        run.State = ERunState.Choice;

        // 1. 测试 Stash 内部纯换位（命中 HasStashRelevantAura 快路径）：必须识别 TTriggerOnCardMoved 不跳过
        var stashMoverTpl = overlay.Repo.GetCard(moverGuid)!;
        var stashCore = new CardInstance(stashMoverTpl, CardTier.Bronze, null, "w1c_stash_mover");
        var stashEntry = new RunState.CardEntry
        {
            InstanceId = stashCore.InstanceId,
            TemplateId = Guid.Parse(moverGuid),
            Type = ECardType.Item,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Stash,
            Socket = EContainerSocketId.Socket_0,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            CoreCard = stashCore
        };
        run.Cards[stashEntry.InstanceId] = stashEntry;
        run.SyncEntryFromCoreCardAndDiff(stashEntry);

        var moveCmd = new MoveItemCommand
        {
            InstanceId = new InstanceId(stashEntry.InstanceId),
            Section = EInventorySection.Stash,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_2 }
        };
        var outcome = engine.Handle(moveCmd);

        // 必须触发了变形事件且原卡已替换为目标卡
        var transformEv = outcome.Events.OfType<GameSimEventCardTransformed>().FirstOrDefault();
        Assert.NotNull(transformEv);
        Assert.Equal(stashEntry.InstanceId, transformEv!.OriginalInstanceId);
        Assert.NotEmpty(transformEv.TransformedCards);
        string newCardId = transformEv.TransformedCards[0].InstanceId;
        Assert.True(run.Cards.ContainsKey(newCardId), "变形产物卡必须登记在 RunState.Cards 中");

        // 2. GameServerEndpoints.BuildCommandReply 对触发变形的 MoveItem 必须退回全量 GameSim 且 sim.Cards 必含新卡 ID
        var reply = GameServerEndpoints.BuildCommandReply(
            moveCmd, run, outcome, ERunState.Choice,
            isOwnedSelectMove: false, isFirstAfterReestablish: false,
            out bool isPureBoardMove, out _);
        Assert.False(isPureBoardMove, "触发 GameSimEventCardTransformed 的 MoveItem 严禁走增量纯移动快路径");
        var agg = Assert.IsType<NetMessageAggregate>(reply);
        var gameSimMsg = agg.Messages.OfType<NetMessageGameSim>().First();
        Assert.True(gameSimMsg.Data.Cards.ContainsKey(newCardId), "全量回包 sim.Cards 必须包含变形新卡 ID");

        // 3. 直接对 RunState.BuildDeltaGameSim 喂入 Transformed + TransformReverted 事件（无手传 dirtyCardIds），
        //    协议层必须自动采集全部事件引用实体 ID，客户端 simUpdates[id] 查表 0 KeyNotFoundException
        var revertedOrigTpl = PickSmallItemTemplate(shared.Repo, excludeId: targetTpl.Id);
        var revertedEntry = new RunState.CardEntry
        {
            InstanceId = "w1c_reverted_orig",
            TemplateId = Guid.Parse(revertedOrigTpl.Id),
            Type = ECardType.Item,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_4,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            CoreCard = new CardInstance(revertedOrigTpl, CardTier.Bronze, null, "w1c_reverted_orig")
        };
        run.Cards[revertedEntry.InstanceId] = revertedEntry;
        var revertEv = new GameSimEventCardTransformReverted(
            new SimEventCardTransformation(
                revertedEntry.InstanceId,
                revertedEntry.TemplateId.ToString(),
                ECardType.Item,
                ECombatantId.Player,
                EInventorySection.Hand,
                EContainerSocketId.Socket_4),
            new List<string> { newCardId });

        var deltaSim = run.BuildDeltaGameSim(
            new IGameSimEvent[] { transformEv, revertEv },
            dirtyCardIds: Array.Empty<string>());
        Assert.True(deltaSim.Cards.ContainsKey(newCardId), "BuildDeltaGameSim 必须自动收集 GameSimEventCardTransformed 新卡 ID");
        Assert.True(deltaSim.Cards.ContainsKey(revertedEntry.InstanceId), "BuildDeltaGameSim 必须自动收集 GameSimEventCardTransformReverted 还原原卡 ID");
    }

    [Fact]
    public void P0_14_ActivateEncounterCard_SynthesizedEncounter_MustNotEmitCardDealtAndCardPurchasedPair()
    {
        var shared = SharedGameDataFixture.Shared;
        var parentEnc = shared.Repo.CardsById.Values.First(c =>
            string.Equals(c.Type, "EventEncounter", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(c.Id, out _));

        var run = new RunState
        {
            RunId = $"w1c_p014_{Guid.NewGuid():N}",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 2,
            Hour = 2,
            State = ERunState.LevelUp,
            StartSelectionDone = true,
            RngSeed = 20260114
        };
        var engine = new RunEngine(
            run,
            shared.Pool,
            shared.CreateMonstersCopy(),
            shared.LevelUps,
            shared.Mode,
            shared.Repo,
            shared.Ghosts,
            shared.ArchetypeIndex);
        engine.StartRun();

        // 构造 SuspendedStates 挂起遭遇（无原顶栏实例，弹栈退出 LevelUp 时强制走 ActivateEncounterCard(anchorEncId, selectedCard: null) 内部合成新顶栏卡）
        run.Cards.Clear();
        run.ActiveEncounterCardInstanceId = null;
        run.SuspendedStates.Clear();
        run.SuspendedStates.Push(new SuspendedStateSnapshot
        {
            State = ERunState.Encounter,
            CurrentEncounterId = parentEnc.Id,
            ActiveEncounterCardInstanceId = null,
            SelectionSet = new List<string>(),
            SelectionRules = new TSelectionContextRules { CanExit = true, CanSelectMultiple = false, SelectionIsFree = true }
        });
        run.State = ERunState.LevelUp;
        run.CurrentEncounterId = null;
        run.SelectionSet.Clear();
        run.SelectionRules = new TSelectionContextRules { CanExit = true, CanSelectMultiple = false, SelectionIsFree = true };

        var exitLevelUpOutcome = engine.Handle(new ExitCurrentStateCommand());

        // 1. 绝不可在同一回包中对同一 InstanceId 伪造 GameSimEventCardDealt + GameSimEventCardPurchased 双事件
        var dealtIds = exitLevelUpOutcome.Events
            .OfType<GameSimEventCardDealt>()
            .Select(e => e.InstanceId)
            .ToHashSet(StringComparer.Ordinal);
        var purchasedIds = exitLevelUpOutcome.Events
            .OfType<GameSimEventCardPurchased>()
            .Select(e => e.InstanceId)
            .ToHashSet(StringComparer.Ordinal);
        var forgedPair = dealtIds.Intersect(purchasedIds, StringComparer.Ordinal).ToList();
        Assert.Empty(forgedPair);

        // 2. 合成的新顶栏卡必须在 Cards 中有效、标记为 ActiveEncounterCardInstanceId、且绝不在 SelectionSet 中
        Assert.False(string.IsNullOrEmpty(run.ActiveEncounterCardInstanceId));
        Assert.True(run.Cards.ContainsKey(run.ActiveEncounterCardInstanceId!));
        Assert.DoesNotContain(run.ActiveEncounterCardInstanceId!, run.SelectionSet);
        Assert.True(exitLevelUpOutcome.AppendStateSyncAtTail, "内部合成新顶栏卡的命令必须置 AppendStateSyncAtTail=true 以由客户端 GameStateSync 对齐顶栏");

        // 3. 模拟客户端 GameStateHandler.SyncItems:162-171 过滤口径：货架卡片 TemplateId != CurrentEncounterId，0 幽灵卡残留
        var syncSnap = run.BuildSnapshot();
        string? curEncWire = run.ResolveWireCurrentEncounterId();
        var shelfSet = run.SelectionSet.ToHashSet(StringComparer.Ordinal);
        var shelfCards = syncSnap.Cards
            .Where(c => shelfSet.Contains(c.InstanceId))
            .ToList();
        Assert.DoesNotContain(shelfCards, c => string.Equals(c.InstanceId, run.ActiveEncounterCardInstanceId, StringComparison.Ordinal));
        Assert.DoesNotContain(shelfCards, c => string.Equals(c.TemplateId.ToString(), curEncWire, StringComparison.OrdinalIgnoreCase));

        // 4. 正常点击货架遭遇卡进入遭遇仍须正常发 CardPurchased（兼容 V9BoardAndProtocolTests）
        run.State = ERunState.Choice;
        run.ActiveEncounterCardInstanceId = null;
        run.CurrentEncounterId = null;
        var shelfEncEntry = new RunState.CardEntry
        {
            InstanceId = "w1c_shelf_enc",
            TemplateId = Guid.Parse(parentEnc.Id),
            Type = ECardType.EventEncounter,
            Section = null,
            Socket = EContainerSocketId.Socket_0,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            CoreCard = new CardInstance(parentEnc, CardTier.Bronze, null, "w1c_shelf_enc")
        };
        run.Cards[shelfEncEntry.InstanceId] = shelfEncEntry;
        run.SelectionSet.Clear();
        run.SelectionSet.Add(shelfEncEntry.InstanceId);

        var pickOutcome = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shelfEncEntry.InstanceId) });
        Assert.Contains(pickOutcome.Events.OfType<GameSimEventCardPurchased>(), p => p.InstanceId == shelfEncEntry.InstanceId);
        Assert.DoesNotContain(pickOutcome.Events.OfType<GameSimEventCardDealt>(), d => d.InstanceId == shelfEncEntry.InstanceId);
    }

    [Fact]
    public async Task P0_15_CrossModResume_TwoPhaseDeserialize_MustPreserveModTemplateAndRouteTournament()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1c_p015_{Guid.NewGuid():N}");
        string modsDir = Path.Combine(tempDir, "mods");
        string stateDir = Path.Combine(tempDir, "state");
        string communityDir = Path.Combine(tempDir, "community_mods");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(communityDir);

        string modId = "w1c-alpha";
        string customCardId = $"{modId}:obsidian_blade";

        WebApplication? app = null;
        HttpClient? http = null;
        Persistence? db = null;
        try
        {
            // 构建一个社区 Mod（借壳 Cutlass GUID 的合法平衡自定义卡，拥有明确可识别的 DamageAmount=27）
            string modSrcDir = Path.Combine(tempDir, "mod_src");
            Directory.CreateDirectory(Path.Combine(modSrcDir, "cards"));
            File.WriteAllText(Path.Combine(modSrcDir, "mod.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                id = modId,
                name = "Wave1C Alpha Mod",
                version = "1.0.0",
                author = "w1c",
                scope = "multiplayer-safe",
                affectsGhostPool = false
            }), Encoding.UTF8);
            File.WriteAllText(Path.Combine(modSrcDir, "cards", "custom_cards.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                cards = new[]
                {
                    new CustomCardDefinition
                    {
                        CustomId = customCardId,
                        BaseVisualTemplateId = CutlassId,
                        Type = "Item",
                        Size = "Medium",
                        StartingTier = "Bronze",
                        Heroes = [HeroNames.Vanessa, HeroNames.Common],
                        TitleZh = "黑曜测试长刃",
                        DescZh = "测试跨Mod两阶段反序列化",
                        ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害"],
                        Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Bronze"] = new TierData
                            {
                                Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 4, ["SellPrice"] = 2, ["CooldownMax"] = 4000, ["DamageAmount"] = 27 },
                                AbilityIds = ["0"]
                            },
                            ["Silver"] = new TierData
                            {
                                Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 8, ["SellPrice"] = 4, ["CooldownMax"] = 4000, ["DamageAmount"] = 54 },
                                AbilityIds = ["0"]
                            },
                            ["Gold"] = new TierData
                            {
                                Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 16, ["SellPrice"] = 8, ["CooldownMax"] = 3500, ["DamageAmount"] = 90 },
                                AbilityIds = ["0"]
                            },
                            ["Diamond"] = new TierData
                            {
                                Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 32, ["SellPrice"] = 16, ["CooldownMax"] = 3000, ["DamageAmount"] = 150 },
                                AbilityIds = ["0"]
                            }
                        },
                        Abilities = new Dictionary<string, AbilityDefinition>
                        {
                            ["0"] = new AbilityDefinition
                            {
                                Id = "0",
                                ActiveIn = "HandOnly",
                                Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                                Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                            }
                        }
                    }
                }
            }), Encoding.UTF8);

            string zipPath = Path.Combine(tempDir, $"{modId}.bazaarmod.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(modSrcDir, zipPath);
            byte[] zipBytes = File.ReadAllBytes(zipPath);

            db = new Persistence(Path.Combine(stateDir, "state.db"));
            var accounts = new AccountStore(db);
            var sessions = new SessionStore();
            var playerRules = new PlayerRuleStore(db);
            var lobbies = new LobbyStore(db);
            var pvpCoord = new TournamentPvpCoordinator(db, lobbies);
            var ghosts = new PvpGhostPool();
            var isolatedPool = shared.CreateIsolatedCardPool();
            var monstersCopy = shared.CreateMonstersCopy();
            var whitelist = shared.CreateValidCardIdsCopy();

            var modHost = new ServerModHost(
                shared.Repo,
                shared.DataDir,
                stateDir,
                whitelist,
                modsRootOverride: modsDir);
            var communityStore = new CommunityModStore(modHost, communityDir);
            var upRes = await communityStore.UploadZipAsync(zipBytes, "tester", "Tester", isAdmin: true);
            Assert.True(upRes.Ok, upRes.Error ?? "上传社区测试 Mod 失败");

            // 确认全局 ActiveRepository 不含该社区 Mod（仅按局 SelectedModIds 启用！）
            Assert.Null(modHost.ActiveRepository.GetCard(customCardId));

            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(accounts);
            builder.Services.AddSingleton(sessions);
            builder.Services.AddSingleton(playerRules);
            builder.Services.AddSingleton(lobbies);
            builder.Services.AddSingleton<ITournamentPvpCoordinator>(pvpCoord);
            builder.Services.AddSingleton(isolatedPool);
            builder.Services.AddSingleton(monstersCopy);
            builder.Services.AddSingleton(shared.LevelUps);
            builder.Services.AddSingleton(shared.Mode);
            builder.Services.AddSingleton(shared.Repo);
            builder.Services.AddSingleton(ghosts);
            builder.Services.AddSingleton(shared.ArchetypeIndex);
            builder.Services.AddSingleton(modHost);

            app = builder.Build();
            GameServerEndpoints.Map(app);
            await app.StartAsync();

            string baseUrl = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };

            var acc = accounts.GetOrCreateByName("W1C_CrossModPlayer", "game", "dev_p015");
            string accKey = acc.AccountId.ToString("N");
            string bearer = accounts.IssuePersistentTokens(acc, "game", "dev_p015").AccessToken;

            async Task<(HttpResponseMessage Resp, string? Sid, RunState? Run)> PostSessionAsync(InitializeRunCommand cmd)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/sessions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                req.Content = new ByteArrayContent(SerializeCommand(cmd));
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var resp = await http.SendAsync(req);
                string? sid = sessions.GetByAccount(accKey)?.Sid;
                var run = sid != null ? SessionRegistry.Get(sid)?.Run : null;
                return (resp, sid, run);
            }

            // 场景 B：构造并落盘带社区 Mod 的冷快照，确认冷恢复经两阶段反序列化完整保留自定义卡模板（即使大厅主仓库不含该 Mod）
            var modCtx = modHost.ResolveRunModContext([modId], isolatedPool, monstersCopy, shared.ArchetypeIndex);
            var customTpl = modCtx.Repository.GetCard(customCardId);
            Assert.NotNull(customTpl);

            var modRules = new CustomRunRuleSet { SelectedModIds = new List<string> { modId } }.CloneNormalized();
            var coldRun = new RunState
            {
                RunId = $"w1c_cold_mod_{Guid.NewGuid():N}",
                AccountId = accKey,
                Username = acc.Username,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                Day = 3,
                Hour = 2,
                State = ERunState.Choice,
                StartSelectionDone = true,
                CustomRules = modRules.CloneNormalized()
            };
            var customCore = new CardInstance(customTpl!, CardTier.Bronze, null, "w1c_mod_blade_1");
            var customEntry = new RunState.CardEntry
            {
                InstanceId = customCore.InstanceId,
                TemplateId = Guid.Parse(CutlassId),
                CustomCardId = customCardId,
                Type = ECardType.Item,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_0,
                Size = ECardSize.Medium,
                Tier = ETier.Bronze,
                CoreCard = customCore
            };
            coldRun.Cards[customEntry.InstanceId] = customEntry;
            coldRun.SyncEntryFromCoreCardAndDiff(customEntry);
            db.SaveRunSnapshot(coldRun, accKey, RunSnapshotSerializer.Serialize(coldRun, startSelectionDone: true), isCustomRule: true);

            // 发起冷恢复（大小写不同的规范化 Mod ID 验证）
            playerRules.Set(accKey, new CustomRunRuleSet { SelectedModIds = new List<string> { modId.ToUpperInvariant() } }.CloneNormalized());
            var initSolo = new InitializeRunCommand { SelectedHero = EHero.Vanessa, PlayMode = EPlayMode.Unranked };
            var (respB, sidB, resumedB) = await PostSessionAsync(initSolo);
            Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
            Assert.NotNull(resumedB);
            Assert.Equal(coldRun.RunId, resumedB!.RunId);
            Assert.True(resumedB.Cards.TryGetValue(customEntry.InstanceId, out var restoredCard));
            Assert.Equal(customCardId, restoredCard!.CoreCard!.Template.Id);
            Assert.Equal(27, restoredCard.CoreCard.GetAttribute("DamageAmount"), 1);

            // 场景 A：玩家在大厅取消勾选社区 Mod 后重连 → CanResumeRunForInit 必须拒绝续玩旧 Mod 档并开启新局
            if (sidB != null) { SessionRegistry.Detach(sidB); sessions.Remove(sidB); }
            playerRules.Set(accKey, new CustomRunRuleSet { SelectedModIds = new List<string>() }.CloneNormalized());
            var (respA, sidA, runA) = await PostSessionAsync(initSolo);
            Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
            Assert.NotNull(runA);
            Assert.NotEqual(coldRun.RunId, runA!.RunId);
            if (sidA != null) { SessionRegistry.Detach(sidA); sessions.Remove(sidA); }

            // 场景 C：同账号同时存在较早的锦标赛活跃局与更晚的单人活跃局（且内存大厅为 null 模拟重启窗口，单人规则已改 Mod）
            //         带 TournamentSessionId 调 POST /sessions 必须强制经 LoadLatestTournamentRun 恢复锦标赛局且不被单人 Mod 误拒！
            Guid tsid = Guid.NewGuid();
            var tourneyRun = new RunState
            {
                RunId = $"w1c_tourney_run_{Guid.NewGuid():N}",
                AccountId = accKey,
                Username = acc.Username,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                TournamentSessionId = tsid,
                Day = 4,
                Hour = 3,
                State = ERunState.Choice,
                StartSelectionDone = true,
                CustomRules = modRules.CloneNormalized()
            };
            tourneyRun.Cards[customEntry.InstanceId] = customEntry;
            db.SaveRunSnapshot(tourneyRun, accKey, RunSnapshotSerializer.Serialize(tourneyRun, startSelectionDone: true), isCustomRule: true);

            Thread.Sleep(20);
            var newerSoloRun = new RunState
            {
                RunId = $"w1c_newer_solo_{Guid.NewGuid():N}",
                AccountId = accKey,
                Username = acc.Username,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                Day = 1,
                Hour = 1,
                State = ERunState.Choice,
                StartSelectionDone = true,
                CustomRules = new CustomRunRuleSet()
            };
            db.SaveRunSnapshot(newerSoloRun, accKey, RunSnapshotSerializer.Serialize(newerSoloRun, startSelectionDone: true));

            playerRules.Set(accKey, new CustomRunRuleSet { SelectedModIds = new List<string>() }.CloneNormalized());
            var initTourney = new InitializeRunCommand { SelectedHero = EHero.Vanessa, PlayMode = EPlayMode.Tournament, TournamentSessionId = tsid };
            var (respC, sidC, resumedC) = await PostSessionAsync(initTourney);
            Assert.Equal(HttpStatusCode.OK, respC.StatusCode);
            Assert.NotNull(resumedC);
            Assert.Equal(tourneyRun.RunId, resumedC!.RunId);
            Assert.Equal(customCardId, resumedC.Cards[customEntry.InstanceId].CoreCard!.Template.Id);
            if (sidC != null) { SessionRegistry.Detach(sidC); sessions.Remove(sidC); }

            // 场景 D：极端缺包场景 —— 使用不含 mod-alpha 的基础库反序列化含借壳卡快照：
            // 绝不可静默回退篡改为原版 Cutlass 壳卡，且占位模板必须全字段非 null（0 NRE）
            string snapWithMod = RunSnapshotSerializer.Serialize(coldRun, startSelectionDone: true);
            bool okMissing = RunSnapshotSerializer.TryDeserialize(snapWithMod, shared.Repo, out var missingRun, out _);
            Assert.True(okMissing);
            Assert.NotNull(missingRun);
            var placeholderCore = missingRun!.Cards[customEntry.InstanceId].CoreCard!;
            Assert.Equal(customCardId, placeholderCore.Template.Id);
            Assert.NotEqual(CutlassId, placeholderCore.Template.Id);
            Assert.NotNull(placeholderCore.Template.Tiers);
            Assert.True(placeholderCore.Template.Tiers!.ContainsKey(placeholderCore.Template.StartingTier));
            Assert.NotNull(placeholderCore.Template.Abilities);
            Assert.Empty(placeholderCore.Template.Abilities);
            Assert.NotNull(placeholderCore.Template.Auras);
            Assert.NotNull(placeholderCore.Template.Tags);
            Assert.NotNull(placeholderCore.Template.HiddenTags);
            Assert.NotNull(placeholderCore.Template.Heroes);

            var dummyP1 = new PlayerState("player", "P1", "Vanessa", 300);
            dummyP1.Hand.TryAddAuto(placeholderCore);
            var dummyP2 = new PlayerState("opponent", "P2", "Vanessa", 300);
            var simRes = CombatSimulator.Simulate(shared.Repo, dummyP1, dummyP2, seed: 42, recordTimeline: false, recordEvents: false);
            Assert.NotNull(simRes);
        }
        finally
        {
            if (http != null) { try { http.Dispose(); } catch { } }
            if (app != null) { try { await app.StopAsync(); await app.DisposeAsync(); } catch { } }
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void P0_16_BossRushMilestoneAndSyncDraw_MustNotLoopHour6PvpViaLastCompletedPvpDay()
    {
        var shared = SharedGameDataFixture.Shared;

        // 场景 A：BossRush Day 5 里程碑日（Victories + Defeats == 1 < Day 5），打完 Day 5 PvP 触发升级/Fates 退出后必须推进至 Day 6 Hour 1
        var bossRules = CustomRunRuleSet.CreatePreset(EGameModePreset.PureOfficial, ESpecialModeId.BossRush);
        var bossRun = new RunState
        {
            RunId = $"w1c_p016_boss_{Guid.NewGuid():N}",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 5,
            Hour = 6,
            Victories = 1,
            Defeats = 0,
            LastCompletedPvpDay = 5,
            State = ERunState.LevelUp,
            StartSelectionDone = true,
            CustomRules = bossRules,
            RngSeed = 20260116
        };
        bossRun.SelectionRules = new TSelectionContextRules { CanExit = true, SelectionIsFree = true };
        var bossEngine = new RunEngine(
            bossRun,
            shared.Pool,
            shared.CreateMonstersCopy(),
            shared.LevelUps,
            shared.Mode,
            shared.Repo,
            shared.Ghosts,
            shared.ArchetypeIndex);
        bossRun.State = ERunState.LevelUp;
        bossRun.Day = 5;
        bossRun.Hour = 6;
        bossRun.Victories = 1;
        bossRun.Defeats = 0;
        bossRun.LastCompletedPvpDay = 5;
        bossRun.SuspendedStates.Clear();

        bossEngine.Handle(new ExitCurrentStateCommand());
        Assert.Equal(6u, bossRun.Day);
        Assert.Equal(1u, bossRun.Hour);
        Assert.Equal(5u, bossRun.LastCompletedPvpDay);

        // 场景 B：锦标赛同步 PvP 平局（Day 1 Hour 6, Victories=0, Defeats=0, LastCompletedPvpDay=1）
        var drawRun = new RunState
        {
            RunId = $"w1c_p016_draw_{Guid.NewGuid():N}",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Tournament,
            TournamentSessionId = Guid.NewGuid(),
            Day = 1,
            Hour = 6,
            Victories = 0,
            Defeats = 0,
            LastCompletedPvpDay = 1,
            State = ERunState.LevelUp,
            StartSelectionDone = true,
            RngSeed = 20260117
        };
        drawRun.SelectionRules = new TSelectionContextRules { CanExit = true, SelectionIsFree = true };
        Assert.True(LobbyStore.ComputeIsAtOrPastHour6Pvp(drawRun), "平局已完成当日 PvP 时 ComputeIsAtOrPastHour6Pvp 必须识别为已就位/已过 Hour 6");

        string snap = RunSnapshotSerializer.Serialize(drawRun, startSelectionDone: true);
        Assert.True(RunSnapshotSerializer.TryDeserialize(snap, shared.Repo, out var restoredDraw, out _));
        Assert.NotNull(restoredDraw);
        Assert.Equal(1u, restoredDraw!.LastCompletedPvpDay);

        var drawEngine = new RunEngine(
            restoredDraw,
            shared.Pool,
            shared.CreateMonstersCopy(),
            shared.LevelUps,
            shared.Mode,
            shared.Repo,
            shared.Ghosts,
            shared.ArchetypeIndex);
        restoredDraw.State = ERunState.LevelUp;
        restoredDraw.SuspendedStates.Clear();
        drawEngine.Handle(new ExitCurrentStateCommand());
        Assert.Equal(2u, restoredDraw.Day);
        Assert.Equal(1u, restoredDraw.Hour);
    }
}
