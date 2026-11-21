using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Mods;
using BazaarServer.Storage;

namespace BazaarServer.Game;

/// <summary>
/// 沙盒构筑实验室引擎桥接实现（实现 <see cref="ISandboxEngineBridge"/>）：
/// - 直发任意原版或已加载 Mod 自定义卡牌/技能至指定槽位；
/// - 实时修改卡牌品阶、附魔、永久属性或移除卡牌；
/// - 实时调整金币、收入、生命上限、声望、等级、经验、天数与小时；
/// - 零副作用即时测试战斗（支持自镜像、指定野怪、指定天数幽灵），输出完整 DPS 与单卡统计；
/// - 一键加载内置流派或自定义预设盘面；
/// - 严格执行沙盒三禁隔离：仅允许 SandboxLab 对局调用，绝不入标准幽灵池、不计生涯胜负、不上标准榜单。
/// </summary>
public sealed class SandboxEngineService : ISandboxEngineBridge
{
    private readonly SessionStore _sessions;
    private readonly CardPool _pool;
    private readonly PvpGhostPool _ghosts;
    private readonly Persistence _persistence;
    private readonly ISocialAndReplaySink _socialSink;
    private readonly ServerModHost _modHost;

    public SandboxEngineService(
        SessionStore sessions,
        CardPool pool,
        PvpGhostPool ghosts,
        Persistence persistence,
        ISocialAndReplaySink socialSink,
        ServerModHost modHost)
    {
        _sessions = sessions;
        _pool = pool;
        _ghosts = ghosts;
        _persistence = persistence;
        _socialSink = socialSink;
        _modHost = modHost;
    }

    public async Task<SandboxOpResult> GrantCardAsync(string accountId, SandboxGrantCardRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TemplateId))
            return new SandboxOpResult(false, "卡牌模板 ID 不能为空");

        var (ok, err, session, handle) = await TryEnterSandboxSessionAsync(accountId, ct);
        if (!ok || session == null || handle == null)
            return new SandboxOpResult(false, err ?? "未找到活跃的沙盒对局");

        try
        {
            var run = handle.Run;
            var repo = _modHost.ActiveRepository;
            var coreTpl = ResolveCardTemplate(repo, req.TemplateId);
            if (coreTpl == null)
                return new SandboxOpResult(false, $"未找到卡牌模板：{req.TemplateId}");

            if (!string.Equals(coreTpl.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(coreTpl.Type, "Skill", StringComparison.OrdinalIgnoreCase))
            {
                return new SandboxOpResult(false, $"沙盒直发仅支持 Item 或 Skill 类型，当前模板类型为 {coreTpl.Type}");
            }

            var (granted, grantErr, entry) = GrantCoreCardIntoRun(run, repo, coreTpl, req);
            if (!granted || entry == null)
                return new SandboxOpResult(false, grantErr ?? "发放卡牌失败");

            handle.Engine.SyncAfterExternalMutation();
            PersistSandboxRun(run, accountId);

            return new SandboxOpResult(
                true,
                $"已发放 {ResolveDisplayName(repo, coreTpl)}（{entry.Tier}）",
                BuildBoardSnapshotDto(run, repo, entry.InstanceId));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<SandboxOpResult> ModifyCardAsync(string accountId, SandboxModifyCardRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.InstanceId))
            return new SandboxOpResult(false, "卡牌实例 ID 不能为空");

        var (ok, err, session, handle) = await TryEnterSandboxSessionAsync(accountId, ct);
        if (!ok || session == null || handle == null)
            return new SandboxOpResult(false, err ?? "未找到活跃的沙盒对局");

        try
        {
            var run = handle.Run;
            var repo = _modHost.ActiveRepository;
            if (!run.Cards.TryGetValue(req.InstanceId.Trim(), out var entry) || entry.Owner != ECombatantId.Player)
                return new SandboxOpResult(false, $"未找到玩家持有的卡牌实例：{req.InstanceId}");

            if (req.RemoveCard)
            {
                RemoveOwnedCardFromRun(run, entry);
                handle.Engine.SyncAfterExternalMutation();
                PersistSandboxRun(run, accountId);
                return new SandboxOpResult(true, $"已移除卡牌 {entry.InstanceId}", BuildBoardSnapshotDto(run, repo));
            }

            var coreCard = EnsureSandboxCoreCard(entry, repo);

            // 1. 修改品阶（支持升降阶）
            if (!string.IsNullOrWhiteSpace(req.Tier))
            {
                var targetCoreTier = SpawnQueryEngine.ClampToSupportedTier(coreCard.Template, TierHelper.ParseTier(req.Tier));
                coreCard.SetTier(targetCoreTier, isTemporary: false);
                entry.Tier = ShopEngine.ParseTier(coreCard.Tier.ToString());
            }

            // 2. 修改或清除附魔
            if (req.Enchantment != null)
            {
                string trimmedEnch = req.Enchantment.Trim();
                if (string.IsNullOrEmpty(trimmedEnch) ||
                    trimmedEnch.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                    trimmedEnch.Equals("Clear", StringComparison.OrdinalIgnoreCase))
                {
                    coreCard.RemoveEnchantment();
                    entry.Enchantment = null;
                }
                else if (Enum.TryParse<EEnchantmentType>(trimmedEnch, true, out var parsedEnch))
                {
                    coreCard.ApplyEnchantment(parsedEnch.ToString(), preventOverride: false, isTemporary: false);
                    entry.Enchantment = parsedEnch;
                }
                else
                {
                    return new SandboxOpResult(false, $"未知的附魔类型：{req.Enchantment}");
                }
            }

            // 3. 叠加永久属性增量
            if (req.AttributeDeltas is { Count: > 0 })
            {
                foreach (var (attrKey, delta) in req.AttributeDeltas)
                {
                    if (string.IsNullOrWhiteSpace(attrKey) || delta == 0) continue;
                    string cleanKey = attrKey.Trim();
                    coreCard.PermanentAdd[cleanKey] = coreCard.PermanentAdd.GetValueOrDefault(cleanKey, 0.0) + delta;
                }
                coreCard.RefillAmmoToMax();
            }

            run.SyncEntryFromCoreCardAndDiff(entry, repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
            handle.Engine.SyncAfterExternalMutation();
            PersistSandboxRun(run, accountId);

            return new SandboxOpResult(
                true,
                $"已更新卡牌 {entry.InstanceId}（品阶={entry.Tier}，附魔={entry.Enchantment?.ToString() ?? "无"}）",
                BuildBoardSnapshotDto(run, repo, entry.InstanceId));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<SandboxOpResult> SetStatsAsync(string accountId, SandboxSetStatsRequest req, CancellationToken ct)
    {
        var (ok, err, session, handle) = await TryEnterSandboxSessionAsync(accountId, ct);
        if (!ok || session == null || handle == null)
            return new SandboxOpResult(false, err ?? "未找到活跃的沙盒对局");

        try
        {
            var run = handle.Run;
            var repo = _modHost.ActiveRepository;
            ApplyStatsToRun(run, req);
            handle.Engine.SyncAfterExternalMutation();
            PersistSandboxRun(run, accountId);

            return new SandboxOpResult(true, "已更新沙盒玩家数值状态", BuildBoardSnapshotDto(run, repo));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<SandboxCombatTestResult> RunTestCombatAsync(string accountId, SandboxTestCombatRequest req, CancellationToken ct)
    {
        var (ok, err, session, handle) = await TryEnterSandboxSessionAsync(accountId, ct);
        if (!ok || session == null || handle == null)
            return new SandboxCombatTestResult(false, err ?? "未找到活跃的沙盒对局");

        try
        {
            var run = handle.Run;
            var repo = _modHost.ActiveRepository;
            int seed = req.Seed is int s && s != 0 ? s : Random.Shared.Next(1, int.MaxValue);
            string source = string.IsNullOrWhiteSpace(req.OpponentSource)
                ? "self_mirror"
                : req.OpponentSource.Trim().ToLowerInvariant();

            var p1Core = BuildPlayerStateSnapshotForSandbox(run, repo, "player", string.IsNullOrWhiteSpace(run.Username) ? "沙盒玩家" : run.Username);
            if (p1Core.Hand.Cards.Count == 0)
                return new SandboxCombatTestResult(false, "当前手牌区为空，请先摆放至少 1 件物品后再开始沙盒测试战斗");

            PlayerState p2Core;
            string oppLabel;
            bool isPve = false;

            if (source is "monster" or "pve" or "boss")
            {
                isPve = true;
                BazaarCore.Models.MonsterTemplate? monsterTpl = null;
                if (!string.IsNullOrWhiteSpace(req.MonsterTemplateId))
                {
                    string key = req.MonsterTemplateId.Trim();
                    repo.MonstersById.TryGetValue(key, out monsterTpl);
                    monsterTpl ??= repo.MonstersById.Values.FirstOrDefault(m =>
                        m.InternalName.Contains(key, StringComparison.OrdinalIgnoreCase));
                }
                monsterTpl ??= repo.MonstersById.Values
                    .Where(m => m.Player.Hand.Items.Count > 0 && !m.InternalName.Contains("TEST", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => Math.Abs(m.Player.Attributes.GetValueOrDefault("HealthMax", 250) - (100 + (int)run.Day * 150)))
                    .FirstOrDefault();

                if (monsterTpl == null)
                    return new SandboxCombatTestResult(false, $"未找到指定的测试野怪模板：{req.MonsterTemplateId}");

                p2Core = CombatSimulator.BuildMonsterPlayerState(repo, monsterTpl);
                oppLabel = string.IsNullOrWhiteSpace(monsterTpl.InternalName) ? monsterTpl.Id : monsterTpl.InternalName;
            }
            else if (source is "ghost" or "pvp")
            {
                int targetDay = Math.Clamp(req.GhostDay ?? (int)run.Day, 1, 15);
                var ghost = _ghosts.PickOpponent(
                    targetDay,
                    (int)run.Victories,
                    (int)run.Defeats,
                    new Random(seed),
                    repo,
                    includeCustomRuleGhosts: false,
                    poolPartition: "standard");
                p2Core = PvpGhostPool.ToPlayerState(ghost, repo);
                oppLabel = $"{ghost.Hero}（Day {ghost.Day} · {ghost.PowerBucket}）";
            }
            else
            {
                // 默认：自镜像对战（克隆玩家自身盘面作为对手，校验对称强度与爆发曲线）
                p2Core = BuildPlayerStateSnapshotForSandbox(run, repo, "opponent", "自镜像对手", idPrefix: "mirror_");
                oppLabel = "自镜像对手";
            }

            var modifiers = CombatModifiers.FromRulesAndDay(run.CustomRules, (int)run.Day, isBossRush: false, isPve: isPve);
            var simResult = CombatSimulator.Simulate(
                repo,
                p1Core,
                p2Core,
                seed: seed,
                combatType: "SANDBOX",
                recordTimeline: false,
                recordEvents: true,
                syncPermanentChanges: false,
                modifiers: modifiers,
                sandstormEnabled: modifiers.SandstormEnabled);

            bool playerWon = string.Equals(simResult.WinnerPlayerId, "player", StringComparison.OrdinalIgnoreCase);
            double seconds = Math.Max(0.1, simResult.DurationMs / 1000.0);
            double p1TotalDamage = simResult.Player1TotalDamageDealt > 0
                ? simResult.Player1TotalDamageDealt
                : Math.Max(0, p2Core.HealthMax - simResult.Player2RemainingHp);
            double p2TotalDamage = simResult.Player2TotalDamageDealt > 0
                ? simResult.Player2TotalDamageDealt
                : Math.Max(0, p1Core.HealthMax - simResult.Player1RemainingHp);

            var cardStats = AggregateCardStatsFromEvents(simResult.Events, p1Core, p2Core);

            var summary = new
            {
                opponentSource = source,
                opponentName = oppLabel,
                seed,
                durationMs = simResult.DurationMs,
                playerMaxHp = p1Core.HealthMax,
                playerRemainingHp = simResult.Player1RemainingHp,
                opponentMaxHp = p2Core.HealthMax,
                opponentRemainingHp = simResult.Player2RemainingHp,
                playerTotalDamage = Math.Round(p1TotalDamage, 1),
                playerTotalShield = Math.Round(simResult.Player1TotalShieldGained, 1),
                playerEffectiveDps = Math.Round(p1TotalDamage / seconds, 1),
                opponentEffectiveDps = Math.Round(p2TotalDamage / seconds, 1),
                cardStats,
            };

            string replayId = $"sandbox_{run.RunId}_{Guid.NewGuid():N}"[..Math.Min(48, 22 + run.RunId.Length)];
            try
            {
                // 仅保存沙盒测试战报用于回看数值，绝不入榜单、不播报、不污染幽灵池
                _socialSink.SaveCombatReplay(new CombatReplayRecord(
                    ReplayId: replayId,
                    RunId: run.RunId,
                    LobbyId: null,
                    CombatType: "SANDBOX",
                    Day: run.Day,
                    PlayerAccountId: run.AccountId,
                    PlayerUsername: string.IsNullOrWhiteSpace(run.Username) ? run.AccountId : run.Username,
                    PlayerHero: run.Hero.ToString(),
                    OpponentName: oppLabel,
                    OpponentHero: p2Core.Hero,
                    PlayerWon: playerWon,
                    DurationMs: simResult.DurationMs,
                    Seed: seed,
                    SummaryJson: JsonSerializer.Serialize(summary),
                    CombatSimMsgPack: null));
            }
            catch
            {
                replayId = "";
            }

            string outcomeText = playerWon ? "胜利" : "落败";
            return new SandboxCombatTestResult(
                Ok: true,
                MessageZh: $"沙盒测试战斗结束：{outcomeText}（对手：{oppLabel}，耗时 {seconds:0.0} 秒，剩余血量 {simResult.Player1RemainingHp}/{p1Core.HealthMax}）",
                PlayerWon: playerWon,
                DurationMs: simResult.DurationMs,
                PlayerRemainingHp: simResult.Player1RemainingHp,
                OpponentRemainingHp: simResult.Player2RemainingHp,
                ReplayId: string.IsNullOrEmpty(replayId) ? null : replayId,
                Summary: summary);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<SandboxOpResult> ApplyPresetBoardAsync(string accountId, SandboxPresetBoardRequest req, CancellationToken ct)
    {
        var (ok, err, session, handle) = await TryEnterSandboxSessionAsync(accountId, ct);
        if (!ok || session == null || handle == null)
            return new SandboxOpResult(false, err ?? "未找到活跃的沙盒对局");

        try
        {
            var run = handle.Run;
            var repo = _modHost.ActiveRepository;

            // 清空现有玩家卡牌与网格，强制开放全部 10 格手牌位
            var ownedIds = run.Cards.Values
                .Where(c => c.Owner == ECombatantId.Player)
                .Select(c => c.InstanceId)
                .ToList();
            foreach (string oid in ownedIds)
                run.Cards.Remove(oid);
            run.HandGrid.Cards.Clear();
            run.StashGrid.Cards.Clear();
            run.UnlockedSlots = 0b11_1111_1111;
            run.HandGrid.UnlockedMask = run.UnlockedSlots;
            run.StashGrid.UnlockedMask = 0b11_1111_1111;

            if (req.Stats != null)
                ApplyStatsToRun(run, req.Stats);

            int grantedCount = 0;
            if (req.Cards is { Count: > 0 })
            {
                foreach (var cardReq in req.Cards)
                {
                    var tpl = ResolveCardTemplate(repo, cardReq.TemplateId);
                    if (tpl == null) continue;
                    var (gOk, _, _) = GrantCoreCardIntoRun(run, repo, tpl, cardReq);
                    if (gOk) grantedCount++;
                }
            }
            else
            {
                grantedCount = PopulateBuiltInPresetBoard(run, repo, req.PresetName);
            }

            handle.Engine.SyncAfterExternalMutation();
            PersistSandboxRun(run, accountId);

            return new SandboxOpResult(
                true,
                $"已载入预设盘面「{req.PresetName}」，共布置 {grantedCount} 张卡牌",
                BuildBoardSnapshotDto(run, repo));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    // ── 内部辅助实现 ────────────────────────────────────────────────────────

    private async Task<(bool Ok, string? Error, Session? Session, SessionRegistry.Handle? Handle)> TryEnterSandboxSessionAsync(
        string accountId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return (false, "玩家账号未认证", null, null);

        var session = _sessions.GetByAccount(accountId);
        if (session == null)
            return (false, "当前账号尚未进入游戏对局，请先以沙盒实验室模式开局", null, null);

        await session.Gate.WaitAsync(ct);
        var handle = SessionRegistry.Get(session.Sid);
        if (handle == null)
        {
            session.Gate.Release();
            return (false, "未找到活跃对局实例，请重新进入游戏", null, null);
        }

        if (!handle.Run.IsSandboxRun)
        {
            session.Gate.Release();
            return (false, "当前对局并非沙盒实验室模式，严禁调用沙盒调试接口", null, null);
        }

        return (true, null, session, handle);
    }

    private static BazaarCore.Models.CardTemplate? ResolveCardTemplate(GameDataRepository repo, string idOrName)
    {
        string key = idOrName.Trim();
        var exact = repo.GetCard(key);
        if (exact != null) return exact;

        return repo.CardsById.Values.FirstOrDefault(c =>
            (string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase)) &&
            (string.Equals(c.InternalName, key, StringComparison.OrdinalIgnoreCase) ||
             c.InternalName.Contains(key, StringComparison.OrdinalIgnoreCase)));
    }

    private (bool Ok, string? Error, RunState.CardEntry? Entry) GrantCoreCardIntoRun(
        RunState run,
        GameDataRepository repo,
        BazaarCore.Models.CardTemplate coreTpl,
        SandboxGrantCardRequest req)
    {
        bool isSkill = string.Equals(coreTpl.Type, "Skill", StringComparison.OrdinalIgnoreCase);
        var clampedTier = SpawnQueryEngine.ClampToSupportedTier(coreTpl, TierHelper.ParseTier(req.Tier));
        string? enchName = null;
        EEnchantmentType? enchEnum = null;
        if (!isSkill && !string.IsNullOrWhiteSpace(req.Enchantment) &&
            Enum.TryParse<EEnchantmentType>(req.Enchantment.Trim(), true, out var parsedEnch))
        {
            enchEnum = parsedEnch;
            enchName = parsedEnch.ToString();
        }

        string safeWireId = _pool.ResolveSafeWireTemplateId(coreTpl.Id);
        if (!Guid.TryParse(safeWireId, out var wireGuid) || wireGuid == Guid.Empty)
        {
            var fallback = isSkill ? _pool.AllSkills.FirstOrDefault() : _pool.AllItems.FirstOrDefault();
            if (fallback == null || !Guid.TryParse(_pool.ResolveSafeWireTemplateId(fallback.Id), out wireGuid))
                return (false, "无法为该卡牌解析合法的客户端视觉载体模板", null);
        }

        string? customCardId = !string.Equals(coreTpl.Id, wireGuid.ToString(), StringComparison.OrdinalIgnoreCase)
            ? coreTpl.Id
            : null;
        if (customCardId != null)
            run.VisualBindingsRevision++;

        string instId = $"{(isSkill ? "sbskl" : "sbitm")}_{Guid.NewGuid():N}"[..16];
        var coreCard = new CardInstance(coreTpl, clampedTier, enchName, instId);

        EInventorySection? section = null;
        EContainerSocketId? socket = null;

        if (!isSkill)
        {
            run.UnlockedSlots = 0b11_1111_1111;
            run.HandGrid.UnlockedMask = run.UnlockedSlots;
            run.StashGrid.UnlockedMask = 0b11_1111_1111;

            bool preferStash = string.Equals(req.Section, "Stash", StringComparison.OrdinalIgnoreCase);
            var primary = preferStash ? run.StashGrid : run.HandGrid;
            var secondary = preferStash ? run.HandGrid : run.StashGrid;
            EInventorySection primarySec = preferStash ? EInventorySection.Stash : EInventorySection.Hand;
            EInventorySection secondarySec = preferStash ? EInventorySection.Hand : EInventorySection.Stash;

            bool placed = false;
            if (req.PreferredSlot is int slot && slot is >= 0 and <= 9 && primary.CountFreeUnlockedSlots() >= coreCard.SlotWidth)
            {
                placed = primary.TryPlaceWithDisplaceOrSwap(coreCard, slot);
            }
            if (!placed && primary.CountFreeUnlockedSlots() >= coreCard.SlotWidth)
            {
                placed = primary.TryAddAuto(coreCard);
            }
            if (placed)
            {
                section = primarySec;
                socket = (EContainerSocketId)Math.Clamp(coreCard.StartSlot, 0, 9);
            }
            else if (secondary.CountFreeUnlockedSlots() >= coreCard.SlotWidth && secondary.TryAddAuto(coreCard))
            {
                section = secondarySec;
                socket = (EContainerSocketId)Math.Clamp(coreCard.StartSlot, 0, 9);
            }
            else
            {
                return (false, "手牌区与储藏箱均无足够空间，请先移除或调整已有物品", null);
            }
        }

        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = wireGuid,
            CustomCardId = customCardId,
            Type = isSkill ? ECardType.Skill : ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = ShopEngine.ParseTier(coreCard.Tier.ToString()),
            Enchantment = enchEnum,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = socket,
            CoreCard = coreCard,
        };
        RunState.PopulateMetaFromTemplate(entry, _pool.Get(coreTpl.Id), coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[instId] = entry;
        return (true, null, entry);
    }

    private static CardInstance EnsureSandboxCoreCard(RunState.CardEntry entry, GameDataRepository repo)
    {
        if (entry.CoreCard != null) return entry.CoreCard;
        string tplId = RunState.ResolveCoreTemplateId(entry);
        var coreTpl = repo.GetCard(tplId) ?? new BazaarCore.Models.CardTemplate
        {
            Id = tplId,
            Type = entry.Type == ECardType.Skill ? "Skill" : "Item",
            Size = entry.Size.ToString(),
            StartingTier = entry.Tier.ToString(),
        };
        var ci = new CardInstance(coreTpl, ShopEngine.ToCoreTier(entry.Tier), entry.Enchantment?.ToString(), entry.InstanceId);
        if (entry.Socket.HasValue) ci.StartSlot = (int)entry.Socket.Value;
        entry.CoreCard = ci;
        return ci;
    }

    private static void RemoveOwnedCardFromRun(RunState run, RunState.CardEntry entry)
    {
        if (entry.CoreCard != null)
        {
            run.HandGrid.Remove(entry.CoreCard);
            run.StashGrid.Remove(entry.CoreCard);
        }
        else
        {
            var inHand = run.HandGrid.Cards.FirstOrDefault(c => c.InstanceId == entry.InstanceId);
            if (inHand != null) run.HandGrid.Remove(inHand);
            var inStash = run.StashGrid.Cards.FirstOrDefault(c => c.InstanceId == entry.InstanceId);
            if (inStash != null) run.StashGrid.Remove(inStash);
        }
        run.Cards.Remove(entry.InstanceId);
    }

    private static void ApplyStatsToRun(RunState run, SandboxSetStatsRequest req)
    {
        if (req.Gold is int g)
            run.PlayerAttributes[EPlayerAttributeType.Gold] = Math.Clamp(g, 0, 99999);
        if (req.Income is int inc)
            run.PlayerAttributes[EPlayerAttributeType.Income] = Math.Clamp(inc, 0, 9999);
        if (req.HealthMax is int hp)
        {
            int clampedHp = Math.Clamp(hp, 50, 999999);
            run.PlayerAttributes[EPlayerAttributeType.HealthMax] = clampedHp;
            run.PlayerAttributes[EPlayerAttributeType.Health] = clampedHp;
        }
        if (req.Prestige is int p)
            run.PlayerAttributes[EPlayerAttributeType.Prestige] = Math.Clamp(p, 1, 999);
        if (req.Level is int lvl)
            run.PlayerAttributes[EPlayerAttributeType.Level] = Math.Clamp(lvl, 1, 99);
        if (req.Experience is int exp)
            run.PlayerAttributes[EPlayerAttributeType.Experience] = Math.Clamp(exp, 0, 99999);
        if (req.Day is int d)
        {
            run.Day = (uint)Math.Clamp(d, 1, 99);
            run.PendingPvpGhost = null;
        }
        if (req.Hour is int h)
        {
            run.Hour = (uint)Math.Clamp(h, 1, 6);
        }
        if (req.UnlockAllSlots == true)
        {
            run.UnlockedSlots = 0b11_1111_1111;
            run.HandGrid.UnlockedMask = run.UnlockedSlots;
            run.StashGrid.UnlockedMask = 0b11_1111_1111;
        }
    }

    private int PopulateBuiltInPresetBoard(RunState run, GameDataRepository repo, string? presetName)
    {
        string key = (presetName ?? "default").Trim().ToLowerInvariant();
        string targetHero = key switch
        {
            var s when s.Contains("pyg") || s.Contains("shield") => "Pygmalien",
            var s when s.Contains("dooley") || s.Contains("tech") => "Dooley",
            var s when s.Contains("mak") || s.Contains("poison") => "Mak",
            var s when s.Contains("stelle") => "Stelle",
            var s when s.Contains("jules") => "Jules",
            _ => run.Hero == EHero.Common ? "Vanessa" : run.Hero.ToString(),
        };
        string? keywordTag = key switch
        {
            var s when s.Contains("burn") => "Burn",
            var s when s.Contains("shield") => "Shield",
            var s when s.Contains("poison") => "Poison",
            var s when s.Contains("tech") => "Tech",
            var s when s.Contains("weapon") => "Weapon",
            _ => null,
        };

        var itemCandidates = _pool.AllItems
            .Where(c => _pool.IsPlayerSpawnable(c) &&
                        (c.Heroes.Length == 0 || c.Heroes.Contains(targetHero, StringComparer.OrdinalIgnoreCase)) &&
                        (keywordTag == null || c.Tags.Contains(keywordTag, StringComparer.OrdinalIgnoreCase) || c.HiddenTags.Contains(keywordTag, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        if (itemCandidates.Count < 4)
        {
            itemCandidates = _pool.AllItems
                .Where(c => _pool.IsPlayerSpawnable(c) &&
                            (c.Heroes.Length == 0 || c.Heroes.Contains(targetHero, StringComparer.OrdinalIgnoreCase)))
                .ToList();
        }

        int count = 0;
        var usedSlots = 0;
        var rng = new Random(StringComparer.OrdinalIgnoreCase.GetHashCode(key) ^ 0x4F2A19);
        var shuffled = itemCandidates.OrderBy(_ => rng.Next()).ToList();
        foreach (var item in shuffled)
        {
            int width = item.Size switch { "Large" => 3, "Medium" => 2, _ => 1 };
            if (usedSlots + width > 10) continue;
            var coreTpl = repo.GetCard(item.Id);
            if (coreTpl == null) continue;
            var (ok, _, _) = GrantCoreCardIntoRun(
                run,
                repo,
                coreTpl,
                new SandboxGrantCardRequest(item.Id, Section: "Hand", Tier: "Diamond"));
            if (ok)
            {
                usedSlots += width;
                count++;
                if (usedSlots >= 9 || count >= 6) break;
            }
        }

        // 附加 2 个同英雄 Gold 技能
        var skills = _pool.AllSkills
            .Where(s => _pool.IsPlayerSpawnable(s) &&
                        (s.Heroes.Length == 0 || s.Heroes.Contains(targetHero, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(_ => rng.Next())
            .Take(2);
        foreach (var sk in skills)
        {
            var coreTpl = repo.GetCard(sk.Id);
            if (coreTpl == null) continue;
            var (ok, _, _) = GrantCoreCardIntoRun(
                run,
                repo,
                coreTpl,
                new SandboxGrantCardRequest(sk.Id, Section: "Hand", Tier: "Gold"));
            if (ok) count++;
        }

        return count;
    }

    private static PlayerState BuildPlayerStateSnapshotForSandbox(
        RunState run,
        GameDataRepository repo,
        string playerId,
        string displayName,
        string idPrefix = "")
    {
        int hpMax = Math.Max(50, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 1000));
        int prestige = Math.Max(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 99));
        int gold = Math.Max(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 999));
        int income = Math.Max(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, 100));
        string hero = (run.Hero == EHero.Common ? EHero.Vanessa : run.Hero).ToString();

        var ps = new PlayerState(playerId, displayName, hero, hpMax, prestige, gold, income)
        {
            Level = Math.Max(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1)),
            Wins = (int)run.Victories,
            Losses = (int)run.Defeats,
        };
        ps.Hand.UnlockedMask = run.UnlockedSlots > 0 ? run.UnlockedSlots : 1023;
        ps.Stash.UnlockedMask = 1023;

        foreach (var entry in run.Cards.Values
                     .Where(c => c.Owner == ECombatantId.Player)
                     .OrderBy(c => c.Socket.HasValue ? (int)c.Socket.Value : 99))
        {
            var src = EnsureSandboxCoreCard(entry, repo);
            string cloneId = string.IsNullOrEmpty(idPrefix) ? entry.InstanceId : idPrefix + entry.InstanceId;
            var clone = new CardInstance(src.Template, src.PermanentTier, src.PermanentEnchantment, cloneId)
            {
                StartSlot = entry.Socket.HasValue ? (int)entry.Socket.Value : src.StartSlot,
            };
            foreach (var (k, v) in src.PermanentAdd) clone.PermanentAdd[k] = v;
            foreach (var (k, v) in src.PermanentMult) clone.PermanentMult[k] = v;
            foreach (var t in src.ExtraTags) clone.ExtraTags.Add(t);
            clone.RebuildTierAndEnchantmentData();
            clone.RefillAmmoToMax();

            if (entry.Type == ECardType.Skill)
            {
                ps.Skills.Add(clone);
            }
            else if (entry.Type == ECardType.SocketEffect)
            {
                ps.SocketEffects.Add(clone);
            }
            else if (entry.Section != EInventorySection.Stash)
            {
                int slot = entry.Socket.HasValue ? (int)entry.Socket.Value : 0;
                if (!ps.Hand.TryPlaceOrShift(clone, slot))
                    ps.Hand.TryAddAuto(clone);
            }
        }

        ps.PrepareForCombat();
        return ps;
    }

    private void PersistSandboxRun(RunState run, string accountId)
    {
        try
        {
            string snap = RunSnapshotSerializer.Serialize(run);
            _persistence.SaveRunSnapshot(run, accountId, snap, isCustomRule: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SandboxEngineService] 持久化沙盒对局快照失败（已忽略）: {ex.Message}");
        }
    }

    private sealed class MutableSandboxCardStat
    {
        public required string InstanceId { get; init; }
        public required string TemplateId { get; init; }
        public required string Owner { get; init; }
        public int Triggers { get; set; }
        public double Damage { get; set; }
        public double Shield { get; set; }
        public double Heal { get; set; }
        public double Burn { get; set; }
        public double Poison { get; set; }
        public int CritCount { get; set; }
    }

    private static List<object> AggregateCardStatsFromEvents(
        IReadOnlyList<CombatEvent> events,
        PlayerState p1,
        PlayerState p2)
    {
        var metaById = new Dictionary<string, (string TemplateId, string Owner)>(StringComparer.Ordinal);
        foreach (var c in p1.Hand.Cards) metaById[c.InstanceId] = (c.Template.Id, "player");
        foreach (var s in p1.Skills) metaById[s.InstanceId] = (s.Template.Id, "player");
        foreach (var c in p2.Hand.Cards) metaById[c.InstanceId] = (c.Template.Id, "opponent");
        foreach (var s in p2.Skills) metaById[s.InstanceId] = (s.Template.Id, "opponent");

        var stats = new Dictionary<string, MutableSandboxCardStat>(StringComparer.Ordinal);
        foreach (var ev in events)
        {
            if (string.IsNullOrWhiteSpace(ev.SourceCardInstanceId)) continue;
            string cid = ev.SourceCardInstanceId;
            if (!stats.TryGetValue(cid, out var st))
            {
                var (tplId, owner) = metaById.TryGetValue(cid, out var m)
                    ? m
                    : (cid, ev.SourcePlayerId ?? "unknown");
                st = new MutableSandboxCardStat { InstanceId = cid, TemplateId = tplId, Owner = owner };
                stats[cid] = st;
            }

            switch (ev.Type)
            {
                case CombatEventType.CardFired:
                    st.Triggers++;
                    if (ev.IsCrit) st.CritCount++;
                    break;
                case CombatEventType.DamageApplied:
                    st.Damage += Math.Max(0, ev.Value);
                    if (ev.IsCrit) st.CritCount++;
                    break;
                case CombatEventType.ShieldApplied:
                    st.Shield += Math.Max(0, ev.Value);
                    break;
                case CombatEventType.HealApplied:
                    st.Heal += Math.Max(0, ev.Value);
                    break;
                case CombatEventType.BurnApplied:
                    st.Burn += Math.Max(0, ev.Value);
                    break;
                case CombatEventType.PoisonApplied:
                    st.Poison += Math.Max(0, ev.Value);
                    break;
            }
        }

        return stats.Values
            .OrderByDescending(cs => cs.Damage + cs.Burn + cs.Poison + cs.Shield)
            .Select(cs => (object)new
            {
                instanceId = cs.InstanceId,
                templateId = cs.TemplateId,
                owner = cs.Owner,
                triggers = cs.Triggers,
                damage = Math.Round(cs.Damage, 1),
                shield = Math.Round(cs.Shield, 1),
                heal = Math.Round(cs.Heal, 1),
                burn = Math.Round(cs.Burn, 1),
                poison = Math.Round(cs.Poison, 1),
                critCount = cs.CritCount,
            })
            .ToList();
    }

    private static string ResolveDisplayName(GameDataRepository repo, BazaarCore.Models.CardTemplate tpl)
    {
        var zh = repo.Localization.GetLocalizedText(tpl.Localization?.Title);
        return !string.IsNullOrWhiteSpace(zh) ? zh : tpl.InternalName;
    }

    private static object BuildBoardSnapshotDto(RunState run, GameDataRepository repo, string? highlightInstanceId = null)
    {
        var hand = new List<object>();
        var stash = new List<object>();
        var skills = new List<object>();

        foreach (var c in run.Cards.Values.Where(x => x.Owner == ECombatantId.Player).OrderBy(x => x.Socket.HasValue ? (int)x.Socket.Value : 99))
        {
            string coreId = RunState.ResolveCoreTemplateId(c);
            var tpl = repo.GetCard(coreId);
            var dto = new
            {
                instanceId = c.InstanceId,
                templateId = coreId,
                wireTemplateId = c.TemplateId.ToString(),
                name = tpl != null ? ResolveDisplayName(repo, tpl) : coreId,
                type = c.Type.ToString(),
                size = c.Size.ToString(),
                tier = c.Tier.ToString(),
                enchantment = c.Enchantment?.ToString(),
                section = c.Section?.ToString(),
                socket = c.Socket.HasValue ? (int)c.Socket.Value : (int?)null,
                attributes = c.Attributes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            };
            if (c.Type == ECardType.Skill) skills.Add(dto);
            else if (c.Section == EInventorySection.Stash) stash.Add(dto);
            else hand.Add(dto);
        }

        return new
        {
            runId = run.RunId,
            day = run.Day,
            hour = run.Hour,
            hero = run.Hero.ToString(),
            highlightInstanceId,
            visualBindingsRevision = run.VisualBindingsRevision,
            stats = run.PlayerAttributes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            hand,
            stash,
            skills,
        };
    }
}
