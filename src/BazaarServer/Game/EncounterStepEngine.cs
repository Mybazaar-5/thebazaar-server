using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarServer.Data;

namespace BazaarServer.Game;

/// <summary>
/// 奇遇事件步执行结果（供 Wave 2 RunEngine 统一消费状态转移、升级、战斗与日志）。
/// </summary>
public sealed class EncounterStepResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public bool ShouldProgressHour { get; set; } = true;
    public bool IsReselectable { get; set; }
    public bool ExitedEncounter { get; set; }
    public bool ExitedReplacementSet { get; set; }
    public bool DealtNewCards { get; set; }
    public bool RerollTriggered { get; set; }
    /// <summary>P1-17：记录 TActionGameReroll 的 RerollType（AllEncounters / All），驱动对应刷新语义。</summary>
    public string? RerollType { get; set; }
    /// <summary>P1-17：记录 TActionGameReroll 透传的可选 SpawnCount 覆盖值。</summary>
    public int? RerollSpawnCount { get; set; }
    public string? NextEncounterId { get; set; }
    public ECardType? NextEncounterType { get; set; }
    public bool TriggersCombat { get; set; }
    public bool TransitionsToPedestal { get; set; }
    public bool TransitionsToSubEncounter { get; set; }
    public int GoldSpent { get; set; }
    public int ExperienceAwarded { get; set; }
    /// <summary>D2-P1-09：奇遇步骤内直接 +Level 的级数，交由 RunEngine 统一执行升级加血/解锁格/OnPlayerLeveledUp 闭环。</summary>
    public int DirectLevelsGained { get; set; }
    /// <summary>D2-P1-09：奇遇步骤内声望扣至 &lt;= 0，交由 RunEngine 统一触发 Fates 救援或判负。</summary>
    public bool PrestigeReachedZero { get; set; }
    public int? MaxSelections { get; set; }

    public List<string> ChangeLogs { get; } = new();
    public List<IGameSimEvent> Events { get; } = new();
    public List<RunState.CardEntry> DealtCards { get; } = new();
    public List<RunState.CardEntry> SpawnedCards { get; } = new();
    public List<RunState.CardEntry> TransformedCards { get; } = new();
    /// <summary>执行期间发生属性/品阶/附魔变更且需向客户端下发 SimUpdateCard 强同步的玩家卡牌实例 ID。</summary>
    public HashSet<string> ModifiedCardIds { get; } = new(StringComparer.Ordinal);

    public static EncounterStepResult Fail(string reason) => new()
    {
        Success = false,
        ErrorMessage = reason,
        ShouldProgressHour = false,
    };
}

/// <summary>玩家当前持有的 Expedition 门票实例信息。</summary>
public sealed record HeldExpeditionTicket(
    string InstanceId,
    string TicketTemplateId,
    string RoomEncounterId,
    EInventorySection? Section,
    EContainerSocketId? Socket);

/// <summary>Day-start Expedition 检测与切换规划结果（供 Wave 2 RunEngine 在跨日后直接消费）。</summary>
public sealed record ExpeditionTransitionPlan(
    bool ShouldTrigger,
    bool RequiresChoice,
    string? DirectRoomEncounterId,
    IReadOnlyList<HeldExpeditionTicket> HeldTickets,
    IReadOnlyList<string> CandidateRoomEncounterIds);

/// <summary>
/// `ECardType.EncounterStep`（以及即时动作型 `EventEncounter`）执行引擎：
///   1. 校验选项卡自身 `SelectionRequirements` 与费用（`BuyPrice` / `Cost`）；
///   2. 触发 `TTriggerOnCardSelected` 并通过 `AbilityExecutor` 执行全部动作
///      （属性增减、强化/升级/附魔/变形、发卡、当场 `DealCards`/`Reroll`/`ExitReplacementSet`）；
///   3. 结算 `ExperienceAwardUponSelection`；
///   4. 按权重与 `Prerequisites` 计算 `NextEncounterOnSelection`（实现子奇遇多步跳转、基座或直接触发野怪战）；
///   5. 返回结构化结果（`ShouldProgressHour`、`IsReselectable`、变更日志、`NextEncounterId` 等）。
/// </summary>
public sealed class EncounterStepEngine
{
    private readonly RunState _run;
    private readonly CardPool _pool;
    private readonly GameDataRepository _repo;
    private readonly ShopEngine _shop;
    private readonly Random _rng;

    public EncounterStepEngine(
        RunState run,
        CardPool pool,
        GameDataRepository repo,
        ShopEngine shop,
        Random? rng = null)
    {
        _run = run;
        _pool = pool;
        _repo = repo;
        _shop = shop;
        _rng = rng ?? new Random();
    }

    public EncounterStepResult ExecuteStep(
        string instanceId,
        Func<int, List<IGameSimEvent>>? grantExperience = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || !_run.Cards.TryGetValue(instanceId, out var stepEntry))
            return EncounterStepResult.Fail($"未找到奇遇选项：{instanceId}");

        return ExecuteStep(stepEntry, grantExperience != null ? (xp, _) => grantExperience(xp) : null);
    }

    public EncounterStepResult ExecuteStep(
        RunState.CardEntry stepEntry,
        Func<int, List<IGameSimEvent>>? grantExperience = null)
        => ExecuteStep(stepEntry, grantExperience != null ? (xp, _) => grantExperience(xp) : null);

    public EncounterStepResult ExecuteStep(
        RunState.CardEntry stepEntry,
        Func<int, string?, List<IGameSimEvent>>? grantExperienceWithSource)
    {
        string stepTplId = RunState.ResolveCoreTemplateId(stepEntry);
        var coreTpl = _repo.GetCard(stepTplId);
        var poolTpl = _pool.Get(stepTplId);

        // 1. 校验选项卡费用
        int buyPrice = _run.SelectionRules.SelectionIsFree
            ? 0
            : stepEntry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0);
        int currentGold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        if (buyPrice > 0 && currentGold < buyPrice)
            return EncounterStepResult.Fail($"金币不足：需要 {buyPrice} 金币，当前仅有 {currentGold} 金币");

        // 2. 构建权威 PlayerState 与真实 SelectionCards（P2-18：移除虚拟空壳 shelfState，由 TargetResolver 原生消费 ctx.SelectionCards），并校验 SelectionRequirements
        var (playerState, ownedByCore, initialHandSet, initialStashSet, initialSkillSet, initialSocketSet) = BuildSyncedPlayerState();
        var (shelfCards, shelfByCore) = BuildShelfCards();

        var stepInstance = stepEntry.CoreCard ?? BuildStepCardInstance(stepEntry, coreTpl);
        var result = new EncounterStepResult
        {
            Success = true,
            IsReselectable = coreTpl?.IsReselectable ?? poolTpl?.IsReselectable ?? false,
            ShouldProgressHour = coreTpl?.ShouldProgressHour ?? poolTpl?.ShouldProgressHour ?? true,
            MaxSelections = _shop.CurrentMaxSelections,
        };

        // D2-P1-09：订阅 AbilityExecutor 局外 1:N / 链式 / Skill 因果变形回调，彻底消灭“销毁 A + 独立新生 B 误配对变形”
        var transformBatches = new Dictionary<CardInstance, List<CardInstance>>();
        var producedToRoot = new Dictionary<CardInstance, CardInstance>();

        var stepArchetypeResolver = ResolveStepArchetypeResolver();
        var ctx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, playerState, playerState, _rng)
        {
            CurrentDay = (int)_run.Day,
            CurrentHour = (int)_run.Hour,
            CurrentEncounterId = _run.CurrentEncounterId,
            PlayMode = _run.PlayMode.ToString(),
            SelfCard = stepInstance,
            TriggerSourceCard = stepInstance,
            SelectionCards = shelfCards,
            ArchetypeResolver = stepArchetypeResolver,
            CustomRules = _run.CustomRules,
            OnCardTransformBatch = (ownerPs, oldCard, placedAll, _, _, _, _) =>
            {
                if (!ReferenceEquals(ownerPs, playerState) || oldCard == null) return;
                CardInstance root = producedToRoot.TryGetValue(oldCard, out var prevRoot) ? prevRoot : oldCard;
                if (!transformBatches.TryGetValue(root, out var list))
                {
                    list = new List<CardInstance>();
                    transformBatches[root] = list;
                }
                else if (!ReferenceEquals(root, oldCard))
                {
                    // 步内链式变形 A -> B -> C：移除中间产物 B，归并最终产物 C 到根卡 A 下
                    list.Remove(oldCard);
                }

                if (placedAll != null)
                {
                    foreach (var newCard in placedAll)
                    {
                        if (newCard == null) continue;
                        newCard.TransformedFromInstanceId ??= root.InstanceId;
                        if (!list.Contains(newCard))
                            list.Add(newCard);
                        producedToRoot[newCard] = root;
                    }
                }
            },
            OnRerollEncounter = rerollType =>
            {
                result.RerollTriggered = true;
                result.RerollType = rerollType;
                result.RerollSpawnCount = null;
                result.ChangeLogs.Add($"Reroll({rerollType})");
            },
            OnRerollEncounterWithCount = (rerollType, spawnCount) =>
            {
                result.RerollTriggered = true;
                result.RerollType = rerollType;
                result.RerollSpawnCount = spawnCount;
                result.ChangeLogs.Add(spawnCount.HasValue ? $"Reroll({rerollType},Count={spawnCount.Value})" : $"Reroll({rerollType})");
            },
            OnExitSelection = () =>
            {
                result.ExitedEncounter = true;
                result.ExitedReplacementSet = true;
                // P1-01：TActionExitReplacementSet 主动退出当前替换遭遇集，恢复常规事件池
                _run.ClearActiveReplacementSet();
                result.ChangeLogs.Add("ExitReplacementSet");
            },
            OnDealCardsToSelectionWithRules = payload =>
            {
                ApplyDealtCardsToSelection(
                    payload.Cards,
                    payload.SpawnMode,
                    payload.RawSelectionContextRules,
                    result,
                    playerState: playerState,
                    hasExplicitSpawnContext: true,
                    typedRules: payload.SelectionContextRules);
                RefreshShelfCards(shelfCards, shelfByCore);
            },
            OnDealCardsToSelection = (dealtCards, spawnMode) =>
            {
                ApplyDealtCardsToSelection(
                    dealtCards,
                    spawnMode,
                    rulesNode: null,
                    result,
                    playerState: playerState,
                    hasExplicitSpawnContext: true);
                RefreshShelfCards(shelfCards, shelfByCore);
            },
        };

        if (!ValidateSelectionRequirements(poolTpl, coreTpl, ctx, out string? reqErr))
            return EncounterStepResult.Fail(reqErr ?? "未满足该选项的触发前提条件");

        // 记录执行前玩家各属性的有效快照（含 Aura / Hour / Day 限时层），确保 UntilEndOfDay/Hour 增益可被精确识别并下发
        var prevEffectivePlayerAttrs = CaptureEffectivePlayerAttributes(playerState);

        // 扣费
        if (buyPrice > 0)
        {
            playerState.ModifyAttribute("Gold", "Subtract", buyPrice);
            result.GoldSpent = buyPrice;
        }

        // P2-06：消费 TSelectionRequirement.Consumes 指定的消耗卡（如门票/钥匙）
        ConsumeSelectionRequirementCards(poolTpl, coreTpl, ctx, playerState, result);

        var prevCardSnapshot = CaptureCardSnapshot(ownedByCore);

        // 3. 触发 TTriggerOnCardSelected 并按官方 Priority（Immediate > Highest > High > Medium > Low）稳定排序执行符合前置条件的 Ability
        var abilitiesToRun = (stepInstance.ActiveAbilities.Count > 0
            ? stepInstance.ActiveAbilities
            : coreTpl?.Abilities?.Values.ToList() ?? new List<AbilityDefinition>())
            .Select((ab, idx) => (Ability: ab, Index: idx))
            .OrderBy(x => GetPriorityRank(x.Ability.Priority))
            .ThenBy(x => x.Index)
            .Select(x => x.Ability)
            .ToList();

        for (int abIdx = 0; abIdx < abilitiesToRun.Count; abIdx++)
        {
            var ability = abilitiesToRun[abIdx];
            if (ability.Trigger == null) continue;
            bool matchesSelected = AbilityExecutor.MatchesTrigger(
                ability.Trigger,
                "TTriggerOnCardSelected",
                stepInstance,
                playerState,
                stepInstance,
                stepInstance,
                playerState,
                null,
                0,
                0,
                "OutOfCombat",
                "",
                ctx);

            if (!matchesSelected) continue;
            if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ability.Prerequisites, ctx)) continue;

            ExecuteStepAction(ability.Action, stepInstance, ctx, result, () => RefreshShelfCards(shelfCards, shelfByCore));

            // P0-02：同一步骤包含多条顺序 Ability 时（如 You Awaken Later 先清 Custom_13 再 TActionCardDestroy 销毁 Depleted Device），
            // 需在下一条 Ability 前重算局外光环，防止前一条已清除前提但旧 AuraAdd（如 DestroyImmunity）仍残留阻断后续动作。
            if (abIdx + 1 < abilitiesToRun.Count)
                OutOfCombatController.RecalculateOutOfCombatAuras(playerState, _repo, _run.CurrentEncounterCoreCard);
        }

        // P1-17：遵循 TActionGameReroll 的 RerollType 与 SpawnCount 覆盖
        if (result.RerollTriggered && !result.DealtNewCards)
        {
            if (string.Equals(result.RerollType, "AllEncounters", StringComparison.OrdinalIgnoreCase))
            {
                result.Events.AddRange(_shop.DealHourEncounters(
                    playerState,
                    visitedTodayIds: null,
                    rules: _shop.ActiveRules,
                    archetypeIndex: _shop.ArchetypeIndex,
                    spawnCountOverride: result.RerollSpawnCount));
                result.DealtNewCards = _run.SelectionSet.Count > 0;
            }
            else if (!string.IsNullOrEmpty(_run.CurrentEncounterId))
            {
                result.Events.AddRange(_shop.DealEncounterShelf(
                    _run.CurrentEncounterId,
                    isReroll: true,
                    playerState: playerState,
                    ownedMaxTiers: null,
                    customRules: _shop.ActiveRules,
                    archetypeIndex: _shop.ArchetypeIndex,
                    applySyntheticDiscounts: true,
                    spawnCountOverride: result.RerollSpawnCount));
                result.DealtNewCards = _run.SelectionSet.Count > 0;
            }
        }

        // 4. 将 PlayerState 变动（含 Hour/Day/Aura 全部 9 层）无损回写 RunState，并生成完整增量事件与变更日志
        SyncPlayerAttributesBack(playerState, prevEffectivePlayerAttrs, result, grantExperienceWithSource, stepEntry.InstanceId);
        SyncOwnedCardsBack(playerState, ownedByCore, initialHandSet, initialStashSet, initialSkillSet, initialSocketSet, prevCardSnapshot, result, stepEntry.InstanceId, transformBatches);
        SyncShelfChangesBack(shelfByCore, result);

        // 5. 结算 ExperienceAwardUponSelection（对齐小时基础 XP 闸门，防止与母遭遇 PendingEncounterExitXp 或 AdvanceHourOrDay 重复发）
        //    ERunState.LevelUp 升级奖励步独立结算，不消耗新小时基础 XP 配额
        int xpAward = coreTpl?.ExperienceAwardUponSelection ?? poolTpl?.ExperienceAwardUponSelection ?? 0;
        if (xpAward > 0)
        {
            bool isLevelUpStep = _run.State == ERunState.LevelUp;
            if (isLevelUpStep || !_run.GainedHourXpThisHour)
            {
                if (!isLevelUpStep)
                {
                    _run.GainedHourXpThisHour = true;
                    _run.GainedAdvanceHourXpThisHour = true;
                    _run.PendingEncounterExitXp = Math.Max(0, _run.PendingEncounterExitXp - xpAward);
                }
                result.ExperienceAwarded = xpAward;
                result.ChangeLogs.Add($"XP+{xpAward}");
                if (grantExperienceWithSource != null)
                {
                    result.Events.AddRange(grantExperienceWithSource(xpAward, stepEntry.InstanceId));
                }
                else
                {
                    int curXp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
                    _run.PlayerAttributes[EPlayerAttributeType.Experience] = curXp + xpAward;
                    result.Events.Add(new GameSimEventPlayerExperienceGained((uint)xpAward, EExperienceType.Hour, stepEntry.InstanceId));
                }
            }
            else if (_run.PendingEncounterExitXp > 0)
            {
                _run.PendingEncounterExitXp = Math.Max(0, _run.PendingEncounterExitXp - xpAward);
            }
        }

        // 6. P1-01：消费官方 NewSetIdOnExit（替换遭遇集 GUID）写入 ActiveReplacementSetId，移除误按卡牌 GUID 查询的死代码
        string? exitReplacementSetId = coreTpl?.NewSetIdOnExit ?? poolTpl?.NewSetIdOnExit;
        if (!string.IsNullOrWhiteSpace(exitReplacementSetId) && !result.ExitedReplacementSet)
        {
            _run.ActiveReplacementSetId = exitReplacementSetId;
            if (ShopEngine.TryGetReplacementSet(exitReplacementSetId, out var repSet) && repSet != null)
            {
                if (!string.IsNullOrWhiteSpace(repSet.ExpeditionRoomId))
                    _run.ActiveExpeditionId = repSet.ExpeditionRoomId;
                _run.ActiveExpeditionVariant = repSet.Variant;
            }
            result.ChangeLogs.Add($"ReplacementSet->{exitReplacementSetId}");
        }

        // 7. 计算 NextEncounterOnSelection（按权重与 Prerequisites 条件抽取下一跳遭遇）
        var nextOptions = coreTpl?.NextEncounterOnSelection ?? poolTpl?.NextEncounterOnSelection;
        string? nextEncounterId = PickNextEncounter(nextOptions, ctx);

        if (!string.IsNullOrEmpty(nextEncounterId))
        {
            var nextTpl = _pool.Get(nextEncounterId);
            var nextCore = _repo.GetCard(nextEncounterId);
            string nextTypeStr = nextTpl?.Type ?? nextCore?.Type ?? "EventEncounter";
            ECardType nextCardType = ShopEngine.ParseCardType(nextTypeStr);

            result.NextEncounterId = nextEncounterId;
            result.NextEncounterType = nextCardType;
            result.ShouldProgressHour = false;
            result.ChangeLogs.Add($"NextEncounter->{nextCardType}:{nextEncounterId}");

            if (nextCardType == ECardType.CombatEncounter)
                result.TriggersCombat = true;
            else if (nextCardType == ECardType.PedestalEncounter)
                result.TransitionsToPedestal = true;
            else
                result.TransitionsToSubEncounter = true;
        }
        else if (result.DealtNewCards)
        {
            // 仍在当前奇遇的下一组替代卡界面交互中，暂不推进小时
            result.ShouldProgressHour = false;
        }
        else if (result.IsReselectable)
        {
            // 可重复选择且未结束：从货架保留其余选项（移除已用当前卡或保留由上层决定）
            result.ShouldProgressHour = false;
        }
        else
        {
            // 单步奇遇结算完成，清理当前步骤候选并标记可离开/推进小时
            if (_run.SelectionSet.Contains(stepEntry.InstanceId))
            {
                _run.SelectionSet.Remove(stepEntry.InstanceId);
                _run.Cards.Remove(stepEntry.InstanceId);
                result.Events.Add(new GameSimEventCardDisposed(stepEntry.InstanceId, ECardDisposalReason.Cleared));
            }
            if (!_run.SelectionRules.CanSelectMultiple || result.ExitedEncounter)
            {
                result.Events.AddRange(_shop.ClearShelf());
                result.ExitedEncounter = true;
            }
        }

        return result;
    }

    // ── 动作拦截与执行 ──────────────────────────────────────────────────────

    private static int GetPriorityRank(string? priority) => priority switch
    {
        "Immediate" => 0,
        "Highest" => 1,
        "High" => 2,
        "Medium" => 3,
        "Low" => 4,
        _ => 3,
    };

    private void ExecuteStepAction(
        RuleNode? action,
        CardInstance stepCard,
        RuleExecutionContext ctx,
        EncounterStepResult result,
        Action? refreshShelfCards = null)
    {
        if (action == null) return;

        switch (action.Type)
        {
            case "TActionAnd":
                if (action.Cost != null && !AbilityExecutor.ExecuteAction(action.Cost, stepCard, ctx))
                    return;
                if (action.Actions != null)
                {
                    foreach (var sub in action.Actions)
                        ExecuteStepAction(sub, stepCard, ctx, result, refreshShelfCards);
                }
                return;

            case "TActionGameDealCards":
                if (action.Cost != null && !AbilityExecutor.ExecuteAction(action.Cost, stepCard, ctx))
                    return;
                var normalizedDealCtx = action.SpawnContext != null
                    ? ShopEngine.NormalizeSequentialSingleGroupQuery(action.SpawnContext)
                    : null;
                normalizedDealCtx = FilterExpiredExpeditionTicketsInSpawnContext(
                    normalizedDealCtx,
                    (int)_run.Day,
                    (int)_run.Hour);
                IReadOnlySet<string>? validAssetIds = _pool.WhitelistEnabled ? _pool.GetEffectiveValidAssetIds() : null;
                bool isStepMerchant = IsCurrentEncounterMerchant();
                IReadOnlyDictionary<string, CardTier>? ownedMaxTiers = isStepMerchant
                    ? ShopEngine.BuildOwnedMaxTiers(_run, ctx.SelfPlayer)
                    : null;
                bool stepAllowPlayerDup = ShopEngine.HasAllowPlayerDuplicatesBehavior(action.SpawnContext);
                var typedStepRules = action.GetSelectionContextRulesData();
                bool stepIsFree = typedStepRules != null
                    ? typedStepRules.SelectionIsFree
                    : (action.SelectionContextRules?.ExtensionData is { Count: > 0 } ruleExt
                        ? GetExtBool(ruleExt, "SelectionIsFree", _run.SelectionRules.SelectionIsFree)
                        : (string.Equals(action.SpawnMode ?? "Replace", "Replace", StringComparison.OrdinalIgnoreCase) && !isStepMerchant
                            ? true
                            : _run.SelectionRules.SelectionIsFree));
                bool stepIsPaid = !stepIsFree;
                bool stepIsReplace = string.Equals(action.SpawnMode ?? "Replace", "Replace", StringComparison.OrdinalIgnoreCase);
                bool stepAllowDup = (stepIsFree && _run.SelectionRules.CanSelectMultiple)
                                 || ShopEngine.HasAllowDuplicatesBehavior(action.SpawnContext);
                if (typedStepRules != null)
                {
                    if (stepIsFree && typedStepRules.CanSelectMultiple)
                        stepAllowDup = true;
                }
                else if (action.SelectionContextRules?.ExtensionData is { Count: > 0 } dupRuleExt)
                {
                    bool stepCanMulti = GetExtBool(dupRuleExt, "CanSelectMultiple", _run.SelectionRules.CanSelectMultiple);
                    if (stepIsFree && stepCanMulti)
                        stepAllowDup = true;
                }
                var stepUsedTemplates = stepIsReplace
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(
                        _run.SelectionSet
                            .Where(sid => _run.Cards.ContainsKey(sid))
                            .Select(sid => RunState.ResolveCoreTemplateId(_run.Cards[sid])),
                        StringComparer.OrdinalIgnoreCase);
                int stepUsedWidth = stepIsReplace
                    ? 0
                    : _run.SelectionSet.Sum(sid => _run.Cards.TryGetValue(sid, out var se) ? ShopEngine.GetShelfSlotWidth(se) : 0);
                var dealt = SpawnQueryEngine.SpawnCards(
                    normalizedDealCtx,
                    ctx,
                    defaultCountOverride: null,
                    ownedMaxTiers: ownedMaxTiers,
                    validAssetIds: validAssetIds,
                    archetypeResolver: ResolveStepArchetypeResolver(),
                    rules: _run.CustomRules,
                    maxItemSlotWidthProvider: () => Math.Max(0, ShopEngine.MaxShelfSlotWidth - stepUsedWidth),
                    spawnAppliesTiny: false,
                    tryAcceptCard: inst =>
                    {
                        if (!_pool.IsPlayerSpawnableId(inst.Template.Id, allowGuidOnly: true))
                            return false;
                        if (CardPool.IsExpeditionTicketExpired(inst.Template.Id, (int)_run.Day, (int)_run.Hour))
                            return false;
                        var pTpl = _pool.Get(inst.Template.Id);
                        if (inst.Template.Type is "EventEncounter" or "PedestalEncounter")
                        {
                            if (_run.ExclusionSet.Contains(inst.Template.Id)) return false;
                            if (pTpl != null && CardPool.IsExcludedDevOrStoryTemplate(pTpl)) return false;
                            if (inst.Template.InternalName is string en &&
                                (en.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase) ||
                                 en.Contains("Tutorial", StringComparison.OrdinalIgnoreCase) ||
                                 en.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase)))
                                return false;
                            if (!ShopEngine.HasExplicitIdListFor(normalizedDealCtx, inst.Template.Id) &&
                                CardPool.IsExcludedFromDynamicEncounterPool(inst.Template.Id, inst.Template.InternalName, pTpl))
                                return false;
                        }
                        if (inst.Template.Type is "EncounterStep" or "EventEncounter" or "PedestalEncounter" or "CombatEncounter" &&
                            !ShopEngine.CheckChildOptionRunPrerequisites(pTpl, inst.Template, ctx))
                            return false;
                        if (stepIsPaid && inst.Template.Type is "Item" or "Skill" &&
                            _shop.IsZeroBuyPriceOrFreePackage(pTpl, inst.Template, inst.Tier.ToString()))
                            return false;
                        if (isStepMerchant && !stepAllowPlayerDup && inst.Template.Type is "Item" or "Skill" &&
                            ShopEngine.IsBlockedByOwnedMaxTiers(inst.Template.Id, inst.Tier, ownedMaxTiers))
                            return false;
                        if (!stepAllowDup && stepUsedTemplates.Contains(inst.Template.Id))
                            return false;
                        bool isItem = string.Equals(inst.Template.Type, "Item", StringComparison.OrdinalIgnoreCase);
                        int w = isItem ? ShopEngine.GetShelfSlotWidth(inst) : 0;
                        if (isItem && stepUsedWidth + w > ShopEngine.MaxShelfSlotWidth)
                            return false;
                        if (!stepAllowDup)
                            stepUsedTemplates.Add(inst.Template.Id);
                        if (isItem) stepUsedWidth += w;
                        return true;
                    });
                ApplyDealtCardsToSelection(
                    dealt,
                    action.SpawnMode ?? "Replace",
                    action.SelectionContextRules,
                    result,
                    playerState: ctx.SelfPlayer,
                    hasExplicitSpawnContext: action.SpawnContext != null,
                    spawnContextNode: action.SpawnContext,
                    evalCtx: ctx,
                    typedRules: typedStepRules);
                refreshShelfCards?.Invoke();
                return;

            default:
                // P2-18：真实 ctx 已注入 SelectionCards = shelfCards，由 TargetResolver 原生解析 SelectionSet 与真实 SelfPlayer，
                // 彻底移除旧虚拟空壳 shelfState 与 4 动作白名单拦截。
                AbilityExecutor.ExecuteAction(action, stepCard, ctx);
                return;
        }
    }

    private Func<string, ulong>? ResolveStepArchetypeResolver()
    {
        var rules = _run.CustomRules;
        if (rules == null || rules.SelectedArchetypeMask == 0UL)
            return null;
        var index = _shop.ArchetypeIndex ?? (_shop.ArchetypeIndex = CardArchetypeIndex.Build(_pool, _repo));
        return index.GetArchetypeMask;
    }

    private bool IsCurrentEncounterMerchant()
    {
        if (string.IsNullOrEmpty(_run.CurrentEncounterId)) return false;
        var poolTpl = _pool.Get(_run.CurrentEncounterId);
        var coreTpl = _repo.GetCard(_run.CurrentEncounterId);
        string name = poolTpl?.InternalName ?? coreTpl?.InternalName ?? "";
        return CardArchetypeIndex.IsMerchantEncounterTemplate(_run.CurrentEncounterId, name, poolTpl, coreTpl);
    }

    private void ApplyDealtCardsToSelection(
        List<CardInstance> dealtCards,
        string spawnMode,
        RuleNode? rulesNode,
        EncounterStepResult result,
        PlayerState? playerState = null,
        bool hasExplicitSpawnContext = false,
        RuleNode? spawnContextNode = null,
        RuleExecutionContext? evalCtx = null,
        SelectionRulesData? typedRules = null)
    {
        bool isReplaceMode = string.Equals(spawnMode, "Replace", StringComparison.OrdinalIgnoreCase);
        bool isMerchant = IsCurrentEncounterMerchant();
        if (isReplaceMode)
        {
            result.Events.AddRange(_shop.ClearShelf());
        }

        // Bug #4 / P0-01：优先消费强类型 SelectionRulesData，完整透传全部 7 项官方选择规则；
        // 若规则为空且为非商人 Replace，强制默认单选免费可退出并清空旧重随规则。
        bool defaultSingleFreeForReplace = isReplaceMode && !isMerchant;
        if (typedRules != null)
        {
            ApplySelectionRulesData(_run, _shop, typedRules, defaultSingleFreeForReplace);
        }
        else if (rulesNode != null)
        {
            ApplyRuleNodeToSelectionRules(rulesNode, defaultSingleFreeForReplace);
        }
        else if (defaultSingleFreeForReplace)
        {
            _shop.SetCurrentMaxSelections(1);
            _run.BaseRerollCost = null;
            _run.RerollCost = 0;
            _run.RerollsRemaining = 0;
            _run.SelectionRules = new TSelectionContextRules
            {
                CanSelectMultiple = false,
                SelectionIsFree = true,
                CanExit = true,
                RerollRules = null,
                WillAutoSellOnExit = false,
                NextEncounterOnExit = null,
            };
        }

        result.MaxSelections = _shop.CurrentMaxSelections;

        bool isPaidContext = !_run.SelectionRules.SelectionIsFree;
        // 三战利品（Grab the Loot / Mystery Bundle）等 SelectionIsFree && CanSelectMultiple 或显式 AllowDuplicates 时，保留全部独立实例
        bool allowDuplicates = (_run.SelectionRules.SelectionIsFree && _run.SelectionRules.CanSelectMultiple)
                            || ShopEngine.HasAllowDuplicatesBehavior(spawnContextNode);
        bool allowPlayerDuplicates = ShopEngine.HasAllowPlayerDuplicatesBehavior(spawnContextNode);
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers = isMerchant
            ? ShopEngine.BuildOwnedMaxTiers(_run, playerState)
            : null;

        var usedTemplates = new HashSet<string>(
            _run.SelectionSet
                .Where(id => _run.Cards.ContainsKey(id))
                .Select(id => RunState.ResolveCoreTemplateId(_run.Cards[id])),
            StringComparer.OrdinalIgnoreCase);

        int usedWidth = 0;
        foreach (string existingId in _run.SelectionSet)
        {
            if (_run.Cards.TryGetValue(existingId, out var existingEntry))
                usedWidth += ShopEngine.GetShelfSlotWidth(existingEntry);
        }

        var runPrereqCtx = evalCtx ?? new RuleExecutionContext(
            _repo,
            RuleScope.OutOfCombat,
            playerState ?? new PlayerState("p1", "Player", _run.Hero.ToString(), GameDefaults.StartingHealth, GameDefaults.StartingPrestige, 0, 10),
            null,
            _rng)
        {
            CurrentDay = (int)_run.Day,
            CurrentHour = (int)_run.Hour,
            CurrentEncounterId = _run.CurrentEncounterId,
            PlayMode = _run.PlayMode.ToString(),
        };

        foreach (var inst in dealtCards)
        {
            if (!_pool.IsPlayerSpawnableId(inst.Template.Id, allowGuidOnly: true)) continue;
            // D2-P1-09：补齐动态遭遇池（含 PedestalEncounter）与 ExclusionSet 过滤，防已淘汰/调试/非白昼遭遇混入
            var dealtPoolTpl = _pool.Get(inst.Template.Id);
            if (inst.Template.Type is "EventEncounter" or "PedestalEncounter")
            {
                if (_run.ExclusionSet.Contains(inst.Template.Id)) continue;
                if (dealtPoolTpl != null && CardPool.IsExcludedDevOrStoryTemplate(dealtPoolTpl)) continue;
                if (!ShopEngine.HasExplicitIdListFor(spawnContextNode, inst.Template.Id) &&
                    CardPool.IsExcludedFromDynamicEncounterPool(inst.Template.Id, inst.Template.InternalName, dealtPoolTpl))
                    continue;
            }
            // P1-25：过滤已过期探险门票（覆盖 Browse All Tickets / Surprise Me / [Jungle Ruins] Grab the Loot 等全部发卡点）
            if (CardPool.IsExpeditionTicketExpired(inst.Template.Id, (int)_run.Day, (int)_run.Hour))
                continue;
            var poolTpl = _pool.Get(inst.Template.Id);
            // P0-13：对动态抽取出的 EventEncounter（如 Mysterious Portal / Scan for Anomalies）严格拦截 DEFUNCT/教程/未解锁 ExclusionSet 卡
            if (string.Equals(inst.Template.Type, "EventEncounter", StringComparison.OrdinalIgnoreCase))
            {
                if (_run.ExclusionSet.Contains(inst.Template.Id)) continue;
                if (poolTpl != null && CardPool.IsExcludedDevOrStoryTemplate(poolTpl)) continue;
                if (inst.Template.InternalName is string en &&
                    (en.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase) ||
                     en.Contains("Tutorial", StringComparison.OrdinalIgnoreCase) ||
                     en.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase)))
                    continue;
            }
            if (inst.Template.Type is "EncounterStep" or "EventEncounter" or "PedestalEncounter" or "CombatEncounter" &&
                !ShopEngine.CheckChildOptionRunPrerequisites(poolTpl, inst.Template, runPrereqCtx))
            {
                continue;
            }
            if (isPaidContext && inst.Template.Type is "Item" or "Skill")
            {
                if (_shop.IsZeroBuyPriceOrFreePackage(poolTpl, inst.Template, inst.Tier.ToString()))
                    continue;
            }
            if (isMerchant && !allowPlayerDuplicates &&
                inst.Template.Type is "Item" or "Skill" &&
                ShopEngine.IsBlockedByOwnedMaxTiers(inst.Template.Id, inst.Tier, ownedMaxTiers))
            {
                continue;
            }
            if (!allowDuplicates && !usedTemplates.Add(inst.Template.Id)) continue;

            bool isItemOption = string.Equals(inst.Template.Type, "Item", StringComparison.OrdinalIgnoreCase);
            int width = isItemOption ? ShopEngine.GetShelfSlotWidth(inst) : 0;
            // V13.1 问题 #6：MaxShelfSlotWidth(10 格) 仅对物品货架生效，不对 EncounterStep/EventEncounter/PedestalEncounter 选项截断
            if (isItemOption && usedWidth + width > ShopEngine.MaxShelfSlotWidth)
                continue;

            var entry = _shop.CreateEntryFromCoreInstance(inst, _run.SelectionRules.SelectionIsFree);
            // 同步应用模式与词条修正（万物附魔 / 五折商店 / 预设物价倍率 / 沙盒 0 元商店），避免步骤发牌旁路词条生效
            _shop.ApplyModeAndMutatorModifiersToEntry(entry, _run.SelectionRules.SelectionIsFree, isMerchant, _rng);
            // P1-41：确保所有候选技能显式携带 BuyPrice 键（免费为 0），保证双端免费判定一致
            if (entry.Type == ECardType.Skill && !entry.Attributes.ContainsKey(ECardAttributeType.BuyPrice))
            {
                entry.Attributes[ECardAttributeType.BuyPrice] = _run.SelectionRules.SelectionIsFree
                    ? 0
                    : RunState.ComputeCardBuyPrice(entry, inst, _repo.GameMode.StandardPrices);
            }
            bool allowZeroForSandbox = _run.CustomRules is { IsSandboxRun: true, SandboxZeroShopPrice: true };
            if (isPaidContext && !allowZeroForSandbox && entry.Type is ECardType.Item or ECardType.Skill &&
                entry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0) <= 0)
            {
                continue;
            }

            _run.Cards[entry.InstanceId] = entry;
            _run.SelectionSet.Add(entry.InstanceId);
            result.DealtCards.Add(entry);
            result.Events.Add(new GameSimEventCardDealt(entry.InstanceId, entry.TemplateId.ToString(), entry.Type));
            if (isItemOption)
                usedWidth += width;
        }

        if (result.DealtCards.Count > 0)
        {
            if (isMerchant && !_run.SelectionRules.SelectionIsFree)
            {
                _shop.ApplyMerchantDiscounts(result.DealtCards);
            }
            // #5 & #18：对显式声明 CanSelectMultiple=true && SelectionIsFree=true 且未硬写 MaxSelections 的全拿事件
            // （如 [Jungle Ruins] Grab the Loot / Grab the Loot / Mystery Bundle），自动按实际发牌数设置可多选上限。
            if (!isMerchant &&
                _run.SelectionRules.CanSelectMultiple &&
                _run.SelectionRules.SelectionIsFree &&
                _shop.CurrentMaxSelections == null)
            {
                int targetMax = isReplaceMode
                    ? result.DealtCards.Count
                    : Math.Max(result.DealtCards.Count, _run.SelectionSet.Count);
                _shop.SetCurrentMaxSelections(Math.Max(1, targetMax));
            }
            result.DealtNewCards = true;
            result.ChangeLogs.Add($"DealCards({spawnMode}):+{result.DealtCards.Count}");
        }
        else if (hasExplicitSpawnContext && !isMerchant && _run.SelectionRules.SelectionIsFree && _run.SelectionSet.Count == 0)
        {
            // 免费发牌奇遇若抽牌池为空：给予安全金币补偿并强制开放 CanExit=true 防止软锁
            if (playerState != null)
            {
                playerState.ModifyAttribute("Gold", "Add", ShopEngine.EmptyFreeDealFallbackGold);
            }
            else
            {
                _run.AddPlayerGold(ShopEngine.EmptyFreeDealFallbackGold);
            }

            if (!_run.SelectionRules.CanExit)
            {
                var unlockedRules = RunState.CloneRules(_run.SelectionRules);
                unlockedRules.CanExit = true;
                _run.SelectionRules = unlockedRules;
            }
            result.ChangeLogs.Add($"EmptyFreeDealFallback:Gold+{ShopEngine.EmptyFreeDealFallbackGold}");
        }

        // 统一在发牌与自适应多选上限确定后回填 result.MaxSelections
        result.MaxSelections = _shop.CurrentMaxSelections;
    }

    /// <summary>
    /// P0-01：将强类型 <see cref="SelectionRulesData"/> 统一落盘至 <see cref="RunState.SelectionRules"/> 与 <see cref="ShopEngine"/>，
    /// 供 EncounterStepEngine 与 RunEngine.ConsumeOutOfCombatBatchRequests 共享同一口径。
    /// </summary>
    public static void ApplySelectionRulesData(
        RunState run,
        ShopEngine shop,
        SelectionRulesData rules,
        bool defaultSingleFreeForReplace = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(shop);
        ArgumentNullException.ThrowIfNull(rules);

        bool canSelectMultiple = rules.CanSelectMultiple;
        if (rules.MaxSelections is int explicitMax)
            canSelectMultiple = explicitMax > 1;

        if (rules.MaxSelections.HasValue)
        {
            shop.SetCurrentMaxSelections(Math.Max(1, rules.MaxSelections.Value));
        }
        else if (canSelectMultiple)
        {
            shop.SetCurrentMaxSelections(null);
        }
        else if (defaultSingleFreeForReplace)
        {
            shop.SetCurrentMaxSelections(1);
        }

        Guid? nextOnExit = run.SelectionRules.NextEncounterOnExit;
        if (rules.NextEncounterOnExit.HasValue &&
            rules.NextEncounterOnExit.Value.ValueKind == JsonValueKind.String &&
            Guid.TryParse(rules.NextEncounterOnExit.Value.GetString(), out var g))
        {
            nextOnExit = g;
        }
        else if (defaultSingleFreeForReplace)
        {
            nextOnExit = null;
        }

        RerollRules? reroll = null;
        if (rules.RerollRules != null)
        {
            var (resolvedRules, startingCost, rerollsRemaining) = ShopEngine.ResolveRerollSettings(rules.RerollRules);
            reroll = resolvedRules;
            run.BaseRerollCost = startingCost;
            run.RerollCost = shop.ComputeEffectiveRerollCost(startingCost);
            run.RerollsRemaining = rerollsRemaining;
        }
        else
        {
            run.BaseRerollCost = null;
            run.RerollCost = 0;
            run.RerollsRemaining = 0;
        }

        run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = canSelectMultiple,
            SelectionIsFree = rules.SelectionIsFree,
            CanExit = rules.CanExit,
            RerollRules = reroll,
            WillAutoSellOnExit = rules.WillAutoSellOnExit,
            NextEncounterOnExit = nextOnExit,
        };
    }

    private void ApplyRuleNodeToSelectionRules(RuleNode node, bool defaultSingleFreeForReplace = false)
    {
        var ext = node.ExtensionData;
        if (ext == null || ext.Count == 0)
        {
            if (defaultSingleFreeForReplace)
            {
                _shop.SetCurrentMaxSelections(1);
                _run.SelectionRules = new TSelectionContextRules
                {
                    CanSelectMultiple = false,
                    SelectionIsFree = true,
                    CanExit = true,
                    RerollRules = null,
                    WillAutoSellOnExit = false,
                    NextEncounterOnExit = null,
                };
            }
            return;
        }

        int? maxSelections = GetExtInt(ext, "MaxSelections");
        bool canSelectMultiple = GetExtBool(
            ext,
            "CanSelectMultiple",
            defaultSingleFreeForReplace ? false : _run.SelectionRules.CanSelectMultiple);
        if (maxSelections is int explicitMax)
            canSelectMultiple = explicitMax > 1;

        // 必须先判定 CanSelectMultiple 再决定 MaxSelections：
        // 若官方规则写明了 CanSelectMultiple=true 但未写 MaxSelections，禁止一刀切覆盖成 1！
        if (maxSelections.HasValue)
        {
            _shop.SetCurrentMaxSelections(Math.Max(1, maxSelections.Value));
        }
        else if (canSelectMultiple)
        {
            _shop.SetCurrentMaxSelections(null);
        }
        else if (defaultSingleFreeForReplace)
        {
            _shop.SetCurrentMaxSelections(1);
        }

        bool selectionIsFree = GetExtBool(
            ext,
            "SelectionIsFree",
            defaultSingleFreeForReplace ? true : _run.SelectionRules.SelectionIsFree);
        bool canExit = GetExtBool(
            ext,
            "CanExit",
            defaultSingleFreeForReplace ? true : _run.SelectionRules.CanExit);
        bool autoSell = GetExtBool(ext, "WillAutoSellOnExit", _run.SelectionRules.WillAutoSellOnExit);

        Guid? nextOnExit = _run.SelectionRules.NextEncounterOnExit;
        if (ext.TryGetValue("NextEncounterOnExit", out var neEl) &&
            neEl.ValueKind == JsonValueKind.String &&
            Guid.TryParse(neEl.GetString(), out var g))
        {
            nextOnExit = g;
        }

        // P1-34：子步未显式声明 RerollRules 时一律清零 RerollRules / RerollCost / BaseRerollCost / RerollsRemaining，
        // 严禁继承母遭遇重随规则导致不可重随子步页误亮重随按钮。
        RerollRules? reroll = null;
        if (ext.TryGetValue("RerollRules", out var rrEl) && rrEl.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var rrData = rrEl.Deserialize<RerollRulesData>(GameDatabaseLoader.JsonOptions);
                if (rrData != null)
                {
                    var (resolvedRules, startingCost, rerollsRemaining) = ShopEngine.ResolveRerollSettings(rrData);
                    reroll = resolvedRules;
                    _run.BaseRerollCost = startingCost;
                    _run.RerollCost = _shop.ComputeEffectiveRerollCost(startingCost);
                    _run.RerollsRemaining = rerollsRemaining;
                }
            }
            catch { /* ignore malformed optional reroll node */ }
        }
        if (reroll == null)
        {
            _run.BaseRerollCost = null;
            _run.RerollCost = 0;
            _run.RerollsRemaining = 0;
        }

        _run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = canSelectMultiple,
            SelectionIsFree = selectionIsFree,
            CanExit = canExit,
            RerollRules = reroll,
            WillAutoSellOnExit = autoSell,
            NextEncounterOnExit = nextOnExit,
        };
    }

    private static int? GetExtInt(Dictionary<string, JsonElement> ext, string key)
    {
        if (!ext.TryGetValue(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v)) return v;
        return null;
    }

    private static bool GetExtBool(Dictionary<string, JsonElement> ext, string key, bool fallback)
    {
        if (!ext.TryGetValue(key, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        return fallback;
    }

    // ── 前置条件与链式跳转计算 ──────────────────────────────────────────────

    private bool ValidateSelectionRequirements(
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl,
        RuleExecutionContext ctx,
        out string? reason)
    {
        reason = null;

        // P2-06：消费数组兼容 CombinedSelectionRequirements 并校验 TSelectionRequirement.Consumes 所需消耗卡存在性
        foreach (var req in ShopEngine.EnumerateSelectionRequirementNodes(poolTpl, coreTpl))
        {
            if (!ShopEngine.EvaluateRequirementNode(req, ctx))
            {
                reason = ShopEngine.TryExtractConsumesTarget(req) != null
                    ? "selection requirement consumes target not met"
                    : "selection requirement prerequisites not met";
                return false;
            }
        }

        return true;
    }

    /// <summary>P2-06：按 SelectionRequirements 中各条目的 Consumes 定义销毁玩家对应消耗卡（如门票/钥匙）。</summary>
    private static void ConsumeSelectionRequirementCards(
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl,
        RuleExecutionContext ctx,
        PlayerState playerState,
        EncounterStepResult result)
    {
        foreach (var req in ShopEngine.EnumerateSelectionRequirementNodes(poolTpl, coreTpl))
        {
            var consumesTarget = ShopEngine.TryExtractConsumesTarget(req);
            if (consumesTarget == null) continue;
            var targets = TargetResolver.ResolveCards(consumesTarget, ctx);
            foreach (var targetCard in targets)
            {
                if (targetCard == null || targetCard.IsDestroyed) continue;
                targetCard.IsDestroyed = true;
                playerState.Hand.Remove(targetCard);
                playerState.Stash.Remove(targetCard);
                playerState.Skills.Remove(targetCard);
                playerState.SocketEffects.Remove(targetCard);
                result.ChangeLogs.Add($"Consume:{targetCard.InstanceId}");
            }
        }
    }

    private string? PickNextEncounter(List<NextEncounterOption>? options, RuleExecutionContext ctx)
    {
        if (options == null || options.Count == 0) return null;

        var eligible = new List<NextEncounterOption>();
        foreach (var opt in options)
        {
            if (string.IsNullOrWhiteSpace(opt.Id)) continue;
            if (!ValueAndConditionEvaluator.EvaluatePrerequisites(opt.Prerequisites, ctx)) continue;

            // 必须存在且通过资产白名单（或为 CombatEncounter 特殊系统占位卡）
            var tpl = _pool.Get(opt.Id);
            if (tpl == null && _repo.GetCard(opt.Id) == null) continue;
            if (!_pool.IsAssetValid(opt.Id) && opt.Id != ShopEngine.Hour6PvpCardId) continue;

            eligible.Add(opt);
        }

        if (eligible.Count == 0) return null;

        double totalWeight = eligible.Sum(o => Math.Max(0.0, o.Weight));
        if (totalWeight <= 0)
            return eligible[_rng.Next(eligible.Count)].Id;

        double roll = _rng.NextDouble() * totalWeight;
        double acc = 0.0;
        foreach (var opt in eligible)
        {
            acc += Math.Max(0.0, opt.Weight);
            if (roll <= acc) return opt.Id;
        }
        return eligible[^1].Id;
    }

    // ── RunState <-> PlayerState 双向同步 ───────────────────────────────────

    private (
        PlayerState Player,
        Dictionary<CardInstance, RunState.CardEntry> OwnedByCore,
        HashSet<CardInstance> HandSet,
        HashSet<CardInstance> StashSet,
        HashSet<CardInstance> SkillSet,
        HashSet<CardInstance> SocketSet
    ) BuildSyncedPlayerState()
    {
        int hpMax = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, GameDefaults.StartingHealth);
        int hp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, hpMax);
        int prestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, GameDefaults.StartingPrestige);
        int gold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, GameDefaults.StartingGold);
        int level = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1);

        var ps = new PlayerState("player", "Player", _run.Hero.ToString(), hpMax, prestige, gold, 10)
        {
            Day = (int)_run.Day,
            Hour = (int)_run.Hour,
            Level = level,
            Wins = (int)_run.Victories,
            Losses = (int)_run.Defeats,
            FatesUsed = _run.HasVisitedFates,
        };
        if (_run.ExclusionSetInitialized)
        {
            ps.InitializeExclusionSet(null);
            foreach (string exId in _run.ExclusionSet)
                ps.ExclusionSet.Add(exId);
        }
        // D2-P1-08：使用单一共享正向分层装配器，0 反除、0 双重乘算，完整装载 Hour/Day/Aura 全部 9 层
        _run.ApplyLayeredPlayerStateTo(ps);

        ps.CurrentHealth = Math.Clamp(hp, 0, Math.Max(1, ps.HealthMax));
        ps.Hand.UnlockedMask = _run.UnlockedSlots;
        ps.Stash.UnlockedMask = 1023;

        var ownedByCore = new Dictionary<CardInstance, RunState.CardEntry>();
        var handSet = new HashSet<CardInstance>();
        var stashSet = new HashSet<CardInstance>();
        var skillSet = new HashSet<CardInstance>();
        var socketSet = new HashSet<CardInstance>();

        foreach (var entry in _run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player)
            .OrderBy(c => c.Socket.HasValue ? (int)c.Socket.Value : int.MaxValue))
        {
            var core = EnsureSyncedCoreCard(entry);
            if (core == null) continue;

            ownedByCore[core] = entry;
            if (entry.Type == ECardType.SocketEffect)
            {
                if (entry.Socket.HasValue) core.StartSlot = (int)entry.Socket.Value;
                ps.SocketEffects.Add(core);
                socketSet.Add(core);
            }
            else if (entry.Type == ECardType.Skill)
            {
                ps.Skills.Add(core);
                skillSet.Add(core);
            }
            else if (entry.Section == EInventorySection.Stash)
            {
                int slot = entry.Socket.HasValue ? (int)entry.Socket.Value : 0;
                if (!ps.Stash.TryPlaceOrShift(core, slot)) ps.Stash.TryAddAuto(core);
                stashSet.Add(core);
            }
            else
            {
                int slot = entry.Socket.HasValue ? (int)entry.Socket.Value : 3;
                if (!ps.Hand.TryPlaceOrShift(core, slot)) ps.Hand.TryAddAuto(core);
                handSet.Add(core);
            }
        }

        return (ps, ownedByCore, handSet, stashSet, skillSet, socketSet);
    }

    private (List<CardInstance> ShelfCards, Dictionary<CardInstance, RunState.CardEntry> ShelfByCore) BuildShelfCards()
    {
        var shelfCards = new List<CardInstance>();
        var map = new Dictionary<CardInstance, RunState.CardEntry>();
        RefreshShelfCards(shelfCards, map);
        return (shelfCards, map);
    }

    private void RefreshShelfCards(List<CardInstance> shelfCards, Dictionary<CardInstance, RunState.CardEntry> map)
    {
        shelfCards.Clear();
        map.Clear();
        foreach (string id in _run.SelectionSet)
        {
            if (!_run.Cards.TryGetValue(id, out var entry)) continue;
            var core = EnsureSyncedCoreCard(entry);
            if (core == null) continue;
            map[core] = entry;
            shelfCards.Add(core);
        }
    }

    private CardInstance? EnsureSyncedCoreCard(RunState.CardEntry entry)
    {
        // P1-31：使用 RunState.ResolveCoreTemplateId 解析逻辑模板 ID（兼容 Mod 自定义卡），避免退化为原版载体基模板
        string logicalTplId = RunState.ResolveCoreTemplateId(entry);
        var coreTpl = _repo.GetCard(logicalTplId) ?? _repo.GetCard(entry.TemplateId.ToString());
        bool createdFresh = entry.CoreCard == null;
        if (entry.CoreCard == null)
        {
            if (coreTpl == null)
            {
                coreTpl = new BazaarCore.Models.CardTemplate
                {
                    Id = logicalTplId,
                    Type = entry.Type.ToString(),
                    Size = entry.Size.ToString(),
                    StartingTier = entry.Tier.ToString(),
                };
            }
            entry.CoreCard = new CardInstance(coreTpl, ShopEngine.ToCoreTier(entry.Tier), entry.Enchantment?.ToString(), entry.InstanceId);
        }

        var ci = entry.CoreCard;
        if (entry.Socket.HasValue) ci.StartSlot = (int)entry.Socket.Value;

        // P1-31：跳过 Ammo/价格/任务完成数等派生属性，并在反推 PermanentAdd 时完整扣除 PermanentMult/Hour/Day/Aura 各层，
        // 严禁把光环/临时层差值永久焊死进 PermanentAdd 导致断线重连叠层膨胀。
        foreach (var (ak, av) in entry.Attributes)
        {
            if (ak is ECardAttributeType.Ammo or ECardAttributeType.BuyPrice or ECardAttributeType.SellPrice or ECardAttributeType.QuestCompletedCount)
                continue;
            string key = ak.ToString();
            if (key.StartsWith("Quest_", StringComparison.OrdinalIgnoreCase))
            {
                // 兼容尚未合入 P2-19(GetAttribute 回退 QuestProgress) 前的 CoreCard.GetAttribute("Quest_X") 读取：
                // 同步确保 QuestProgress 与 PermanentAdd 与单调任务进度一致，且不叠写任何光环/临时层
                if (av > 0)
                {
                    if (ci.QuestProgress.GetValueOrDefault(key, 0) < av)
                        ci.QuestProgress[key] = av;
                    int curQuestAttr = (int)Math.Round(ci.GetAttribute(key));
                    if (curQuestAttr < av)
                    {
                        double baseQuest = ci.BaseAttributes.GetValueOrDefault(key, 0);
                        ci.PermanentAdd[key] = av - baseQuest;
                    }
                }
                continue;
            }
            if ((int)Math.Round(ci.GetAttribute(key)) == av) continue;

            // 若已有活体 CoreCard 且该属性已存在任何临时层/光环层/乘区，信任 CoreCard 自身分层状态，不盲目反写 PermanentAdd
            if (!createdFresh &&
                (ci.HourAdd.ContainsKey(key) || ci.HourMult.ContainsKey(key) ||
                 ci.DayAdd.ContainsKey(key) || ci.DayMult.ContainsKey(key) ||
                 ci.AuraAdd.ContainsKey(key) || ci.AuraMult.ContainsKey(key) ||
                 ci.PermanentMult.ContainsKey(key)))
            {
                continue;
            }
            double totalMult = ci.PermanentMult.GetValueOrDefault(key, 1.0)
                             * ci.HourMult.GetValueOrDefault(key, 1.0)
                             * ci.DayMult.GetValueOrDefault(key, 1.0)
                             * ci.AuraMult.GetValueOrDefault(key, 1.0);
            double effectiveBase = Math.Abs(totalMult) > 1e-6 && Math.Abs(totalMult - 1.0) > 1e-6
                ? av / totalMult
                : av;
            effectiveBase -= ci.HourAdd.GetValueOrDefault(key, 0.0)
                           + ci.DayAdd.GetValueOrDefault(key, 0.0)
                           + ci.AuraAdd.GetValueOrDefault(key, 0.0);
            double baseV = ci.BaseAttributes.GetValueOrDefault(key, 0);
            double desiredPermAdd = effectiveBase - baseV;
            if (Math.Abs(desiredPermAdd - ci.PermanentAdd.GetValueOrDefault(key, 0.0)) > 1e-6)
                ci.PermanentAdd[key] = desiredPermAdd;
        }
        return ci;
    }

    private CardInstance BuildStepCardInstance(RunState.CardEntry entry, BazaarCore.Models.CardTemplate? coreTpl)
    {
        // D3-P1-10：支持非 GUID 自定义 Mod 步骤卡
        var tpl = coreTpl ?? new BazaarCore.Models.CardTemplate
        {
            Id = RunState.ResolveCoreTemplateId(entry),
            Type = entry.Type.ToString(),
            Size = entry.Size.ToString(),
            StartingTier = entry.Tier.ToString(),
        };
        var ci = new CardInstance(tpl, ShopEngine.ToCoreTier(entry.Tier), entry.Enchantment?.ToString(), entry.InstanceId);
        foreach (var (ak, av) in entry.Attributes)
        {
            string key = ak.ToString();
            ci.PermanentAdd[key] = av - ci.BaseAttributes.GetValueOrDefault(key, 0);
        }
        return ci;
    }

    private sealed record CardSnap(
        CardTier Tier,
        string? Enchantment,
        int StartSlot,
        Dictionary<string, double> Attrs,
        int TagCount);

    private static Dictionary<CardInstance, CardSnap> CaptureCardSnapshot(
        Dictionary<CardInstance, RunState.CardEntry> owned)
    {
        var snap = new Dictionary<CardInstance, CardSnap>();
        foreach (var (ci, entry) in owned)
        {
            var attrs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, _) in ci.BaseAttributes) attrs[k] = ci.GetAttribute(k);
            foreach (var (k, _) in ci.PermanentAdd) attrs[k] = ci.GetAttribute(k);
            foreach (var (k, _) in ci.HourAdd) attrs[k] = ci.GetAttribute(k);
            foreach (var (k, _) in ci.HourMult) attrs[k] = ci.GetAttribute(k);
            foreach (var (k, _) in ci.DayAdd) attrs[k] = ci.GetAttribute(k);
            foreach (var (k, _) in ci.DayMult) attrs[k] = ci.GetAttribute(k);
            foreach (var (k, _) in ci.AuraAdd) attrs[k] = ci.GetAttribute(k);
            snap[ci] = new CardSnap(ci.Tier, ci.Enchantment, ci.StartSlot, attrs, ci.ExtraTags.Count);
        }
        return snap;
    }

    private static double ComputeEffectivePlayerAttr(PlayerState ps, EPlayerAttributeType attrType)
    {
        if (attrType == EPlayerAttributeType.Health)
            return ps.CurrentHealth;
        // D2-P1-08：直接走 PlayerState.GetAttribute 权威公式（完整包含 Hour/Day/Combat/Aura 四层 Add + AdditiveMult + PureMult）
        return ps.GetAttribute(attrType.ToString());
    }

    private static Dictionary<string, double> CaptureEffectivePlayerAttributes(PlayerState ps)
    {
        var snap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (EPlayerAttributeType attrType in Enum.GetValues<EPlayerAttributeType>())
            snap[attrType.ToString()] = ComputeEffectivePlayerAttr(ps, attrType);
        return snap;
    }

    private void SyncPlayerAttributesBack(
        PlayerState ps,
        Dictionary<string, double> prevEffectiveAttrs,
        EncounterStepResult result,
        Func<int, string?, List<IGameSimEvent>>? grantExperience = null,
        string? sourceInstanceId = null)
    {
        // 同步动态 ExclusionSet 变更
        if (ps.ExclusionSetInitialized || ps.ExclusionSet.Count > 0)
        {
            _run.ExclusionSetInitialized = true;
            _run.ExclusionSet.Clear();
            foreach (string exId in ps.ExclusionSet)
                _run.ExclusionSet.Add(exId);
        }

        // Health 特殊处理：PlayerState.CurrentHealth 反映当前血量
        ps.Attributes["Health"] = ps.CurrentHealth;

        // D2-P1-08：经统一正向回写器精确提取全部 9 张修饰层（PureMult 与 AdditiveMult 分离，含 Aura 层）与永久基础值
        int prevLevelInRun = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1);
        double prevLevelBase = _run.BasePlayerAttributes.TryGetValue("Level", out double lb) ? lb : prevLevelInRun;
        int prevXpInRun = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
        double prevXpBase = _run.BasePlayerAttributes.TryGetValue("Experience", out double xb) ? xb : prevXpInRun;

        _run.CaptureLayeredPlayerStateFrom(ps, captureBaseValues: true, includeAllExistingAttrs: false);

        int deferredXpDelta = 0;
        int deferredLevelDelta = 0;
        int healthMaxDeltaForHeal = 0;
        bool healthExplicitlyChanged = false;

        foreach (EPlayerAttributeType attrType in Enum.GetValues<EPlayerAttributeType>())
        {
            string key = attrType.ToString();
            bool hasInAnyLayer = ps.Attributes.ContainsKey(key)
                              || ps.HourAdd.ContainsKey(key) || ps.HourMult.ContainsKey(key) || ps.HourAdditiveMult.ContainsKey(key)
                              || ps.DayAdd.ContainsKey(key) || ps.DayMult.ContainsKey(key) || ps.DayAdditiveMult.ContainsKey(key)
                              || ps.AuraAdd.ContainsKey(key) || ps.AuraMult.ContainsKey(key) || ps.AuraAdditiveMult.ContainsKey(key);
            if (!hasInAnyLayer && attrType != EPlayerAttributeType.Health) continue;

            double effectiveVal = ComputeEffectivePlayerAttr(ps, attrType);
            // 仅回写执行期有效值真实发生变动的属性（覆盖 Base / Hour / Day / Aura 全部层级），同时防止未变动属性覆写经验升级结果
            bool changedInStep = !prevEffectiveAttrs.TryGetValue(key, out double prevVal) || Math.Abs(effectiveVal - prevVal) > 1e-6;
            if (!changedInStep) continue;

            bool hasModifierLayer = ps.HourAdd.ContainsKey(key) || ps.HourMult.ContainsKey(key) || ps.HourAdditiveMult.ContainsKey(key)
                                 || ps.DayAdd.ContainsKey(key) || ps.DayMult.ContainsKey(key) || ps.DayAdditiveMult.ContainsKey(key)
                                 || ps.AuraAdd.ContainsKey(key) || ps.AuraMult.ContainsKey(key) || ps.AuraAdditiveMult.ContainsKey(key);
            int? minBound = attrType switch
            {
                EPlayerAttributeType.Gold or EPlayerAttributeType.Prestige or EPlayerAttributeType.Experience => 0,
                EPlayerAttributeType.HealthMax or EPlayerAttributeType.Level => 1,
                _ => null,
            };

            int oldInt = prevEffectiveAttrs.TryGetValue(key, out double pVal)
                ? (int)Math.Round(pVal)
                : (_run.PlayerAttributes.TryGetValue(attrType, out int oldV) ? oldV : 0);
            int rawNewInt = (int)Math.Round(effectiveVal);
            // D2-P1-09：统一强钳制（Gold >= 0、Prestige >= 0、Experience >= 0、HealthMax >= 1）
            int newInt = minBound.HasValue ? Math.Max(minBound.Value, rawNewInt) : rawNewInt;
            if (newInt == oldInt && !(attrType == EPlayerAttributeType.Prestige && newInt == 0 && rawNewInt <= 0 && oldInt > 0))
            {
                // D2-P1-08：仅当永久基础值本身越界（或无修饰层）时才下钳 BasePlayerAttributes；
                // 若因临时/光环减益（如 HourMult=0.0 或负 DayAdd）拉低有效展示值，绝不摧毁合法的永久基础值！
                if (rawNewInt != newInt)
                {
                    double rawBase = ps.Attributes.GetValueOrDefault(key, 0.0);
                    if (!hasModifierLayer || (minBound.HasValue && rawBase < minBound.Value))
                    {
                        _run.BasePlayerAttributes[key] = minBound ?? newInt;
                    }
                    _run.PlayerAttributes[attrType] = newInt;
                    _run.LastDerivedPlayerAttributes[attrType] = newInt;
                }
                continue;
            }

            int delta = newInt - oldInt;
            // 奇遇步骤内直接加经验延迟到属性同步循环完成后经 grantExperience 正规结算升级
            if (attrType == EPlayerAttributeType.Experience && delta > 0 && grantExperience != null)
            {
                _run.PlayerAttributes[EPlayerAttributeType.Experience] = prevXpInRun;
                _run.BasePlayerAttributes["Experience"] = prevXpBase;
                _run.LastDerivedPlayerAttributes[EPlayerAttributeType.Experience] = prevXpInRun;
                deferredXpDelta += delta;
                continue;
            }
            // D2-P1-09：奇遇步骤内直接 +Level 交由 RunEngine 走完整升级加血/开槽/PendingLevelUps/OnPlayerLeveledUp 闭环
            if (attrType == EPlayerAttributeType.Level && delta > 0)
            {
                _run.PlayerAttributes[EPlayerAttributeType.Level] = prevLevelInRun;
                _run.BasePlayerAttributes["Level"] = prevLevelBase;
                _run.LastDerivedPlayerAttributes[EPlayerAttributeType.Level] = prevLevelInRun;
                deferredLevelDelta += delta;
                continue;
            }

            if (rawNewInt != newInt)
            {
                // D2-P1-08：永久基础值合法但被临时/光环减益拉低触底时，仅钳制展示值，绝不抹杀永久基础值
                double rawBase = ps.Attributes.GetValueOrDefault(key, 0.0);
                if (!hasModifierLayer || (minBound.HasValue && rawBase < minBound.Value))
                {
                    _run.BasePlayerAttributes[key] = minBound ?? newInt;
                }
            }
            _run.PlayerAttributes[attrType] = newInt;
            _run.LastDerivedPlayerAttributes[attrType] = newInt;

            if (attrType == EPlayerAttributeType.HealthMax && delta > 0)
                healthMaxDeltaForHeal = delta;
            if (attrType == EPlayerAttributeType.Health)
                healthExplicitlyChanged = true;
            if (attrType == EPlayerAttributeType.Gold && delta > 0)
                _run.IncrementPlayerAttribute(EPlayerAttributeType.Custom_3, delta);
            result.ChangeLogs.Add($"{key}{(delta >= 0 ? "+" : "")}{delta}");

            if (attrType == EPlayerAttributeType.Income && delta > 0)
                result.Events.Add(new GameSimEventPlayerIncomeGained((uint)delta));
            else if (attrType == EPlayerAttributeType.Prestige)
            {
                result.Events.Add(new GameSimEventPlayerPrestigeChanged(delta));
                if (newInt <= 0)
                    result.PrestigeReachedZero = true;
            }
        }

        // D2-P1-09：无论 HealthMax 上涨还是下降，始终同步更新并下钳 Health <= HealthMax（HealthMax >= 1）
        int maxHp = Math.Max(1, _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, GameDefaults.StartingHealth));
        int oldHp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, maxHp);
        int targetHp = (healthMaxDeltaForHeal > 0 && !healthExplicitlyChanged)
            ? Math.Clamp(oldHp + healthMaxDeltaForHeal, 0, maxHp)
            : Math.Clamp(oldHp, 0, maxHp);
        if (targetHp != oldHp)
        {
            _run.PlayerAttributes[EPlayerAttributeType.Health] = targetHp;
            _run.BasePlayerAttributes["Health"] = targetHp;
            _run.LastDerivedPlayerAttributes[EPlayerAttributeType.Health] = targetHp;
        }

        if (deferredLevelDelta > 0)
        {
            result.DirectLevelsGained += deferredLevelDelta;
            result.ChangeLogs.Add($"Level+{deferredLevelDelta}");
        }

        if (deferredXpDelta > 0 && grantExperience != null)
        {
            result.Events.AddRange(grantExperience(deferredXpDelta, sourceInstanceId));
            result.ChangeLogs.Add($"Experience+{deferredXpDelta}");
        }
    }

    private void SyncOwnedCardsBack(
        PlayerState ps,
        Dictionary<CardInstance, RunState.CardEntry> ownedByCore,
        HashSet<CardInstance> initialHand,
        HashSet<CardInstance> initialStash,
        HashSet<CardInstance> initialSkills,
        HashSet<CardInstance> initialSocketEffects,
        Dictionary<CardInstance, CardSnap> prevSnap,
        EncounterStepResult result,
        string? sourceInstanceId = null,
        Dictionary<CardInstance, List<CardInstance>>? transformBatches = null)
    {
        var currentHand = new HashSet<CardInstance>(ps.Hand.Cards);
        var currentStash = new HashSet<CardInstance>(ps.Stash.Cards);
        var currentSkills = new HashSet<CardInstance>(ps.Skills);
        var currentSocketEffects = new HashSet<CardInstance>(ps.SocketEffects);
        var matchedReplacements = new HashSet<CardInstance>();

        bool IsStillOwnedInPs(CardInstance c)
            => !c.IsDestroyed && (currentHand.Contains(c) || currentStash.Contains(c) || currentSkills.Contains(c) || currentSocketEffects.Contains(c));

        EInventorySection? ResolveCurrentSection(CardInstance c)
        {
            if (currentStash.Contains(c)) return EInventorySection.Stash;
            if (currentSkills.Contains(c)) return null;
            return EInventorySection.Hand;
        }

        // 1. 检查已存在卡牌的因果变形（1:N / Skill / 同槽 SocketEffect）、销毁、变阶、附魔增删、属性与标签更新
        foreach (var (core, entry) in ownedByCore)
        {
            bool stillPresent = currentHand.Contains(core)
                             || currentStash.Contains(core)
                             || currentSkills.Contains(core)
                             || currentSocketEffects.Contains(core);
            if (!stillPresent || core.IsDestroyed)
            {
                // D2-P1-09：严格按因果来源对账（OnCardTransformBatch -> TransformedFromInstanceId -> 同槽 SocketEffect），
                // 彻底根除“同步骤内销毁 A + 独立新生 B 被误配对为变形”
                var causalReplacements = new List<CardInstance>();
                if (transformBatches != null && transformBatches.TryGetValue(core, out var batchTargets))
                {
                    foreach (var t in batchTargets)
                    {
                        if (t != null && !ownedByCore.ContainsKey(t) && !matchedReplacements.Contains(t) && IsStillOwnedInPs(t))
                            causalReplacements.Add(t);
                    }
                }
                if (causalReplacements.Count == 0)
                {
                    // 次级因果：直接检查 TransformedFromInstanceId == entry.InstanceId（覆盖非 batch 路径设置的因果标签）
                    foreach (var cand in ps.Hand.Cards.Concat(ps.Stash.Cards).Concat(ps.Skills).Concat(ps.SocketEffects))
                    {
                        if (!ownedByCore.ContainsKey(cand) &&
                            !matchedReplacements.Contains(cand) &&
                            !cand.IsDestroyed &&
                            string.Equals(cand.TransformedFromInstanceId, entry.InstanceId, StringComparison.Ordinal))
                        {
                            causalReplacements.Add(cand);
                        }
                    }
                }
                if (causalReplacements.Count == 0 && initialSocketEffects.Contains(core))
                {
                    // 同槽位覆盖旧 SocketEffect（如音符覆盖灶台）必须合成为 CardTransformed
                    var sameSlotSe = ps.SocketEffects.FirstOrDefault(c =>
                        !ownedByCore.ContainsKey(c) && !matchedReplacements.Contains(c) && !c.IsDestroyed && c.StartSlot == core.StartSlot);
                    if (sameSlotSe != null)
                        causalReplacements.Add(sameSlotSe);
                }

                RemoveEntryFromGridsAndDict(entry);

                if (causalReplacements.Count > 0)
                {
                    _run.TrackReagentTransformIfApplicable(core, entry);
                    var transformItems = new List<SimEventCardTransformation>(causalReplacements.Count);
                    foreach (var replacement in causalReplacements)
                    {
                        matchedReplacements.Add(replacement);
                        replacement.TransformedFromInstanceId = null;
                        var sec = ResolveCurrentSection(replacement);
                        var newEntry = RegisterNewOwnedCoreCard(replacement, sec, result, isTransform: true);
                        if (newEntry != null)
                        {
                            result.TransformedCards.Add(newEntry);
                            transformItems.Add(new SimEventCardTransformation(
                                newEntry.InstanceId,
                                newEntry.TemplateId.ToString(),
                                newEntry.Type,
                                ECombatantId.Player,
                                newEntry.Section,
                                newEntry.Socket));
                        }
                    }

                    if (transformItems.Count > 0)
                    {
                        result.Events.Add(new GameSimEventCardTransformed(
                            "step_" + entry.InstanceId,
                            entry.InstanceId,
                            transformItems));
                        result.ChangeLogs.Add($"Transform:{entry.TemplateId}->{string.Join(",", transformItems.Select(x => x.TemplateId))}");
                    }
                    else
                    {
                        result.Events.Add(new GameSimEventCardDisposed(entry.InstanceId, ECardDisposalReason.Cleared));
                        result.ChangeLogs.Add($"TransformOverflowAutoSold:{entry.TemplateId}");
                    }
                }
                else
                {
                    result.Events.Add(new GameSimEventCardDisposed(entry.InstanceId, ECardDisposalReason.Cleared));
                    result.ChangeLogs.Add($"Destroy:{entry.InstanceId}");
                }
                continue;
            }

            if (prevSnap.TryGetValue(core, out var snap))
            {
                if (core.Tier != snap.Tier)
                {
                    entry.Tier = ShopEngine.ParseTier(core.Tier.ToString());
                    result.Events.Add(new GameSimEventCardUpgraded(entry.InstanceId, entry.Tier));
                    result.ChangeLogs.Add($"Upgrade:{entry.InstanceId}->{entry.Tier}");
                    result.ModifiedCardIds.Add(entry.InstanceId);
                }
                if (!string.Equals(core.Enchantment, snap.Enchantment, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(core.Enchantment) &&
                        Enum.TryParse<EEnchantmentType>(core.Enchantment, true, out var enchEnum))
                    {
                        entry.Enchantment = enchEnum;
                        result.Events.Add(new GameSimEventCardEnchanted
                        {
                            InstanceId = entry.InstanceId,
                            EnchantmentType = enchEnum,
                            IsReverted = false,
                        });
                        result.ChangeLogs.Add($"Enchant:{entry.InstanceId}->{enchEnum}");
                        result.ModifiedCardIds.Add(entry.InstanceId);
                    }
                    else if (string.IsNullOrEmpty(core.Enchantment) && !string.IsNullOrEmpty(snap.Enchantment))
                    {
                        // D2-P1-09：补齐附魔移除（Disenchant/Revert）同步，发射 IsReverted=true
                        entry.Enchantment = null;
                        result.Events.Add(new GameSimEventCardEnchanted
                        {
                            InstanceId = entry.InstanceId,
                            EnchantmentType = null,
                            IsReverted = true,
                        });
                        result.ChangeLogs.Add($"Disenchant:{entry.InstanceId}");
                        result.ModifiedCardIds.Add(entry.InstanceId);
                    }
                }
            }

            // D1-P1-14：与 RunState 同步口径对齐，全量比对 core.GetAllTags() 避免残留已移除标签
            var expectedTags = new HashSet<ECardTag>();
            foreach (string rawTag in core.GetAllTags())
            {
                if (Enum.TryParse<ECardTag>(rawTag, true, out var parsedTag))
                    expectedTags.Add(parsedTag);
            }
            if (!entry.Tags.SetEquals(expectedTags))
            {
                entry.Tags.Clear();
                foreach (var t in expectedTags) entry.Tags.Add(t);
                result.ModifiedCardIds.Add(entry.InstanceId);
            }

            bool forceAllAttrs = result.ModifiedCardIds.Contains(entry.InstanceId) && !string.IsNullOrWhiteSpace(entry.CustomCardId);
            if (SyncEntryAttributesFromCore(entry, core, recordForceSync: true, forceAll: forceAllAttrs))
            {
                result.ModifiedCardIds.Add(entry.InstanceId);
                // P2-07：局外奇遇步直接修改玩家单卡属性且未附带光环刷新事件时，按需补发 GameSimEventEffectExecuted 刷新客户端卡面
                if (entry.Type == ECardType.Item)
                {
                    result.Events.Add(new GameSimEventEffectExecuted
                    {
                        ExecutionContextId = "step_" + entry.InstanceId,
                        EffectId = "step_attr_modify",
                        ActionType = EActionCommandType.CardModifyAttribute,
                        Source = !string.IsNullOrWhiteSpace(sourceInstanceId) ? new InstanceId(sourceInstanceId) : null,
                        TriggerSource = !string.IsNullOrWhiteSpace(sourceInstanceId) ? new InstanceId(sourceInstanceId) : null,
                        Target = new EffectTargetCard { Target = new InstanceId(entry.InstanceId) },
                    });
                }
            }
        }

        // 2. 检查新生成（SpawnCards / 非变形式生成）的 Hand / Stash / Skill / SocketEffect 卡牌
        var registeredNew = new HashSet<CardInstance>(result.TransformedCards.Where(c => c.CoreCard != null).Select(c => c.CoreCard!));
        foreach (var c in ps.Hand.Cards)
        {
            if (ownedByCore.ContainsKey(c) || registeredNew.Contains(c)) continue;
            var newEntry = RegisterNewOwnedCoreCard(c, EInventorySection.Hand, result, isTransform: false);
            if (newEntry != null)
                result.SpawnedCards.Add(newEntry);
            registeredNew.Add(c);
        }
        foreach (var c in ps.Stash.Cards)
        {
            if (ownedByCore.ContainsKey(c) || registeredNew.Contains(c)) continue;
            var newEntry = RegisterNewOwnedCoreCard(c, EInventorySection.Stash, result, isTransform: false);
            if (newEntry != null)
                result.SpawnedCards.Add(newEntry);
            registeredNew.Add(c);
        }
        foreach (var s in ps.Skills)
        {
            if (ownedByCore.ContainsKey(s) || registeredNew.Contains(s)) continue;
            var newEntry = RegisterNewOwnedCoreCard(s, null, result, isTransform: false);
            if (newEntry != null)
                result.SpawnedCards.Add(newEntry);
            registeredNew.Add(s);
        }
        foreach (var se in ps.SocketEffects)
        {
            if (ownedByCore.ContainsKey(se) || registeredNew.Contains(se)) continue;
            var newEntry = RegisterNewOwnedCoreCard(se, EInventorySection.Hand, result, isTransform: false);
            if (newEntry != null)
                result.SpawnedCards.Add(newEntry);
            registeredNew.Add(se);
        }
    }

    private void SyncShelfChangesBack(
        Dictionary<CardInstance, RunState.CardEntry> shelfByCore,
        EncounterStepResult result)
    {
        foreach (var (core, entry) in shelfByCore)
        {
            if (!_run.Cards.ContainsKey(entry.InstanceId)) continue;
            if (core.IsDestroyed)
            {
                _run.SelectionSet.Remove(entry.InstanceId);
                _run.Cards.Remove(entry.InstanceId);
                result.Events.Add(new GameSimEventCardDisposed(entry.InstanceId, ECardDisposalReason.Cleared));
                continue;
            }
            entry.Tier = ShopEngine.ParseTier(core.Tier.ToString());
            if (entry.Type == ECardType.Item)
            {
                entry.Size = core.EffectiveSize switch
                {
                    CardSize.Small => ECardSize.Small,
                    CardSize.Medium => ECardSize.Medium,
                    CardSize.Large => ECardSize.Large,
                    _ => ShopEngine.ParseSize(core.Template.Size),
                };
            }
            if (!string.IsNullOrEmpty(core.Enchantment) &&
                Enum.TryParse<EEnchantmentType>(core.Enchantment, true, out var ench))
            {
                entry.Enchantment = ench;
            }
            SyncEntryAttributesFromCore(entry, core);
        }
    }

    private RunState.CardEntry? RegisterNewOwnedCoreCard(
        CardInstance core,
        EInventorySection? section,
        EncounterStepResult result,
        bool isTransform)
    {
        var cardType = ShopEngine.ParseCardType(core.Template.Type);
        string prefix = cardType switch
        {
            ECardType.Skill => "skl",
            ECardType.SocketEffect => "skt",
            _ => "itm",
        };
        string instId = !string.IsNullOrEmpty(core.InstanceId) && !_run.Cards.ContainsKey(core.InstanceId)
            ? core.InstanceId
            : _shop.NewId(prefix);

        EContainerSocketId? socket = null;
        RunState.CardEntry? overwrittenSocketEffect = null;
        var displacedNeighbors = new List<(RunState.CardEntry Entry, EInventorySection Section, EContainerSocketId NewSocket)>();
        if (cardType == ECardType.SocketEffect)
        {
            // D2-P1-09：严格尊重 UnlockedSlots；若目标槽已有旧 SocketEffect，以同槽覆盖合成为 CardTransformed（绝不落未解锁槽或重叠 CardSpawned）
            _run.HandGrid.UnlockedMask = _run.UnlockedSlots;
            section = EInventorySection.Hand;
            int resolvedSlot = (core.StartSlot is >= 0 and <= 9 && _run.HandGrid.IsSlotUnlocked(core.StartSlot))
                ? core.StartSlot
                : FindFreeSocketEffectSlot();
            if (resolvedSlot < 0)
                return null;
            core.StartSlot = resolvedSlot;
            socket = (EContainerSocketId)resolvedSlot;
            overwrittenSocketEffect = _run.Cards.Values.FirstOrDefault(c =>
                c.Owner == ECombatantId.Player &&
                c.Type == ECardType.SocketEffect &&
                c.Socket == socket &&
                !string.Equals(c.InstanceId, instId, StringComparison.Ordinal));
            if (overwrittenSocketEffect != null)
                RemoveEntryFromGridsAndDict(overwrittenSocketEffect);
        }
        else if (cardType != ECardType.Skill && section.HasValue)
        {
            _run.HandGrid.UnlockedMask = _run.UnlockedSlots;
            _run.StashGrid.UnlockedMask = 1023;
            var primary = section == EInventorySection.Stash ? _run.StashGrid : _run.HandGrid;
            var fallback = section == EInventorySection.Stash ? _run.HandGrid : _run.StashGrid;
            BoardGrid placedGrid = primary;
            bool placed = false;

            // 非变形生成优先不挤位直接落位或自动寻空；变形或显式目标槽被占时才执行移位并同步邻卡新槽位
            if (core.StartSlot >= 0)
            {
                if (!isTransform && primary.CanFitAt(core, core.StartSlot))
                {
                    placed = primary.TryPlaceOrShift(core, core.StartSlot);
                }
                else if (isTransform)
                {
                    placed = primary.TryPlaceOrShift(core, core.StartSlot);
                }
                if (!placed)
                    placed = primary.TryAddAuto(core);
                if (!placed && !isTransform)
                    placed = primary.TryPlaceOrShift(core, core.StartSlot);
            }
            else
            {
                placed = primary.TryAddAuto(core);
            }

            if (!placed && fallback.TryAddAuto(core))
            {
                placed = true;
                placedGrid = fallback;
                section = section == EInventorySection.Stash ? EInventorySection.Hand : EInventorySection.Stash;
            }

            if (!placed)
            {
                // 手牌与仓库全满时禁止注册 Section=Hand, Socket=null 的幽灵卡，按权威卖价（含 Value 与双倍经济）折算金币并跳过注册
                var tempEntry = new RunState.CardEntry
                {
                    InstanceId = instId,
                    Type = cardType,
                    Size = ShopEngine.ParseSize(core.Template.Size),
                    Tier = ShopEngine.ParseTier(core.Tier.ToString()),
                    CoreCard = core,
                };
                int autoSellGold = RunState.ResolveAuthoritativeSellPrice(tempEntry, core, _repo.GameMode.StandardPrices, _run.CustomRules);
                if (autoSellGold > 0)
                    _run.AddPlayerGold(autoSellGold);
                result.ChangeLogs.Add($"OverflowAutoSell:{core.Template.Id}:+{autoSellGold}g");
                return null;
            }

            if (core.StartSlot >= 0)
            {
                socket = (EContainerSocketId)Math.Clamp(core.StartSlot, 0, 9);
                // 同步因挤位而移动的同区已有邻卡坐标，确保 RunState.Cards 与客户端容器零漂移
                foreach (var gridCard in placedGrid.Cards)
                {
                    if (ReferenceEquals(gridCard, core) || gridCard.StartSlot < 0) continue;
                    var neighborEntry = _run.Cards.Values.FirstOrDefault(e =>
                        ReferenceEquals(e.CoreCard, gridCard) || string.Equals(e.InstanceId, gridCard.InstanceId, StringComparison.Ordinal));
                    if (neighborEntry == null) continue;
                    var nextSock = (EContainerSocketId)Math.Clamp(gridCard.StartSlot, 0, 9);
                    if (neighborEntry.Section != section.Value || neighborEntry.Socket != nextSock)
                    {
                        neighborEntry.Section = section.Value;
                        neighborEntry.Socket = nextSock;
                        displacedNeighbors.Add((neighborEntry, section.Value, nextSock));
                    }
                }
            }
        }

        EEnchantmentType? ench = null;
        if (!string.IsNullOrEmpty(core.Enchantment) &&
            Enum.TryParse<EEnchantmentType>(core.Enchantment, true, out var e))
        {
            ench = e;
        }

        // 解析安全原版 Wire GUID，完整兼容 Mod 自定义非 GUID 模板并维护 CustomCardId，防止 FormatException 崩溃
        string safeWireId = _pool.ResolveSafeWireTemplateId(core.Template.Id);
        Guid wireGuid = Guid.TryParse(safeWireId, out var parsedWire)
            ? parsedWire
            : (Guid.TryParse(core.Template.Id, out var directGuid)
                ? directGuid
                : Guid.Parse("5e17cfc3-25c0-4a74-8a33-07ee972e6f6e"));
        string? customCardId = RunState.ResolveCustomCardId(core.Template.Id, wireGuid, _pool.Get(core.Template.Id));
        if (customCardId != null)
            _run.VisualBindingsRevision++;

        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = wireGuid,
            CustomCardId = customCardId,
            Type = cardType,
            Size = ShopEngine.ParseSize(core.Template.Size),
            Tier = ShopEngine.ParseTier(core.Tier.ToString()),
            Enchantment = ench,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = socket,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, _pool.Get(core.Template.Id), core.Template);
        SyncEntryAttributesFromCore(entry, core);

        if (cardType == ECardType.Item)
        {
            var (buy, sell) = ShopEngine.PriceFor(entry.Size, entry.Tier, _repo.GameMode.StandardPrices);
            if (!entry.Attributes.ContainsKey(ECardAttributeType.BuyPrice))
                entry.Attributes[ECardAttributeType.BuyPrice] = buy;
            if (!entry.Attributes.ContainsKey(ECardAttributeType.SellPrice))
                entry.Attributes[ECardAttributeType.SellPrice] = sell;
        }

        _run.Cards[instId] = entry;
        foreach (var mv in displacedNeighbors)
        {
            result.Events.Add(new GameSimEventCardMoved(mv.Entry.InstanceId, ECombatantId.Player, mv.Section, mv.NewSocket));
        }
        if (!isTransform)
        {
            if (overwrittenSocketEffect != null)
            {
                result.Events.Add(new GameSimEventCardTransformed(
                    "skt_ovr_" + overwrittenSocketEffect.InstanceId,
                    overwrittenSocketEffect.InstanceId,
                    new List<SimEventCardTransformation>
                    {
                        new(
                            instId,
                            entry.TemplateId.ToString(),
                            entry.Type,
                            ECombatantId.Player,
                            entry.Section,
                            entry.Socket)
                    }));
                result.ChangeLogs.Add($"SocketEffectOverwrite:{overwrittenSocketEffect.InstanceId}->{instId}");
            }
            else
            {
                result.Events.Add(new GameSimEventCardSpawned(
                    instId,
                    entry.TemplateId.ToString(),
                    entry.Type,
                    ECombatantId.Player,
                    entry.Section,
                    entry.Socket));

                if (cardType == ECardType.Skill)
                    result.Events.Add(new GameSimEventPlayerSkillEquipped(instId, ECombatantId.Player));

                result.ChangeLogs.Add($"Spawn:{entry.Type}:{entry.TemplateId}");
            }
        }

        return entry;
    }

    private int FindFreeSocketEffectSlot()
    {
        // D2-P1-09：严格尊重 UnlockedSlots，先找已解锁空槽，全满时退回首个已解锁槽位以触发同槽覆盖，绝不落未解锁槽
        _run.HandGrid.UnlockedMask = _run.UnlockedSlots;
        var used = new HashSet<int>();
        foreach (var c in _run.Cards.Values)
        {
            if (c.Owner == ECombatantId.Player && c.Type == ECardType.SocketEffect && c.Socket.HasValue)
                used.Add((int)c.Socket.Value);
        }
        for (int i = 0; i < BoardGrid.Capacity; i++)
        {
            if (_run.HandGrid.IsSlotUnlocked(i) && !used.Contains(i))
                return i;
        }
        for (int i = 0; i < BoardGrid.Capacity; i++)
        {
            if (_run.HandGrid.IsSlotUnlocked(i))
                return i;
        }
        return -1;
    }

    private void RemoveEntryFromGridsAndDict(RunState.CardEntry entry)
    {
        if (entry.CoreCard != null)
        {
            _run.HandGrid.Remove(entry.CoreCard);
            _run.StashGrid.Remove(entry.CoreCard);
        }
        _run.Cards.Remove(entry.InstanceId);
        _run.SelectionSet.Remove(entry.InstanceId);
    }

    private static bool SyncEntryAttributesFromCore(
        RunState.CardEntry entry,
        CardInstance core,
        bool recordForceSync = false,
        bool forceAll = false)
    {
        bool anyChanged = false;
        foreach (string key in core.EnumeratePresentAttributeKeys())
        {
            if (Enum.TryParse<ECardAttributeType>(key, out var attrType))
            {
                int newVal = attrType == ECardAttributeType.Flying
                    ? Math.Max(core.IsFlying ? 1 : 0, (int)Math.Round(core.GetAttribute(key)))
                    : (int)Math.Round(core.GetAttribute(key));
                bool changed = !entry.Attributes.TryGetValue(attrType, out int oldVal) || oldVal != newVal;
                entry.Attributes[attrType] = newVal;
                if (changed)
                {
                    anyChanged = true;
                    if (recordForceSync)
                        entry.PendingForceSyncAttributes[attrType] = newVal;
                }
                else if (recordForceSync && forceAll)
                {
                    entry.PendingForceSyncAttributes[attrType] = newVal;
                }
            }
        }
        if (core.IsFlying)
        {
            bool changed = !entry.Attributes.TryGetValue(ECardAttributeType.Flying, out int oldFly) || oldFly != 1;
            entry.Attributes[ECardAttributeType.Flying] = 1;
            if (changed)
            {
                anyChanged = true;
                if (recordForceSync)
                    entry.PendingForceSyncAttributes[ECardAttributeType.Flying] = 1;
            }
        }
        return anyChanged;
    }

    // ── 2.8 Expedition 探索系统辅助 API ──────────────────────────────────────

    /// <summary>将 Expedition 门票模板 ID 映射至对应的 Expedition 房间遭遇模板 ID。</summary>
    public static bool TryMapTicketToExpeditionRoom(string? ticketTemplateId, out string? roomEncounterId)
        => CardPool.TryResolveExpeditionRoomForTicket(ticketTemplateId, out roomEncounterId);

    /// <summary>扫描当前 RunState 手牌与背包中玩家持有的有效 Expedition 门票。</summary>
    public static IReadOnlyList<HeldExpeditionTicket> FindHeldExpeditionTickets(RunState run)
    {
        var tickets = new List<HeldExpeditionTicket>();
        foreach (var entry in run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player &&
                        c.State == ECardState.Alive &&
                        !(c.CoreCard?.IsDestroyed ?? false))
            .OrderBy(c => c.Section == EInventorySection.Hand ? 0 : 1)
            .ThenBy(c => c.Socket.HasValue ? (int)c.Socket.Value : int.MaxValue)
            .ThenBy(c => c.InstanceId, StringComparer.Ordinal))
        {
            string tplId = entry.TemplateId.ToString();
            if (!CardPool.TryResolveExpeditionRoomForTicket(tplId, out var roomId) || string.IsNullOrEmpty(roomId))
                continue;

            tickets.Add(new HeldExpeditionTicket(
                entry.InstanceId,
                tplId,
                roomId,
                entry.Section,
                entry.Socket));
        }
        return tickets;
    }

    public IReadOnlyList<HeldExpeditionTicket> FindHeldExpeditionTickets()
        => FindHeldExpeditionTickets(_run);

    public static bool HasHeldExpeditionTicket(RunState run)
        => FindHeldExpeditionTickets(run).Count > 0;

    public bool HasHeldExpeditionTicket()
        => FindHeldExpeditionTickets(_run).Count > 0;

    /// <summary>
    /// 官方固定探索日（Day 6 / 9 / 10）与对应探险房间映射：
    ///   · Day 6  -> Crash Site Expedition (`ead0a1ed`)
    ///   · Day 9  -> Temple / Jungle Expedition (`91287d25`)
    ///   · Day 10 -> Tiny World Expedition (`42db3205`)
    /// </summary>
    public static bool TryResolveScheduledExpeditionRoomForDay(int day, out string? roomEncounterId)
    {
        roomEncounterId = day switch
        {
            6 => CardPool.CrashSiteExpeditionRoomId,
            9 => CardPool.JungleExpeditionRoomId,
            10 => CardPool.TinyWorldExpeditionRoomId,
            _ => null,
        };
        return roomEncounterId != null;
    }

    /// <summary>
    /// 基于当前天数（仅 Day 6 / 9 / 10 Hour 1）、玩家持有对应门票与未访问状态构建 Expedition 计划：
    ///   · 非 Day 6/9/10 或非 Hour 1、无当天匹配门票或该探险房已访问时 ShouldTrigger=false；
    ///   · 满足条件时返回当天唯一对应的探险房间 ID 与匹配门票列表（供 Choice 下发第 4 张入口卡或直接进入）。
    /// </summary>
    public static ExpeditionTransitionPlan BuildDayStartExpeditionPlan(
        RunState run,
        CardPool pool,
        GameDataRepository repo,
        int maxRoomChoices = 3)
    {
        var held = FindHeldExpeditionTickets(run);
        if (held.Count == 0 ||
            run.Hour != 1 ||
            !TryResolveScheduledExpeditionRoomForDay((int)run.Day, out string? scheduledRoomId) ||
            string.IsNullOrEmpty(scheduledRoomId))
        {
            return new ExpeditionTransitionPlan(false, false, null, held, Array.Empty<string>());
        }

        if (run.VisitedSingleSpawnEncounterIds.Contains(scheduledRoomId) ||
            run.VisitedDayEncounterIds.Contains(scheduledRoomId) ||
            !pool.IsAssetValid(scheduledRoomId) ||
            (pool.Get(scheduledRoomId) == null && repo.GetCard(scheduledRoomId) == null))
        {
            return new ExpeditionTransitionPlan(false, false, null, held, Array.Empty<string>());
        }

        var matchingTickets = held
            .Where(t => string.Equals(t.RoomEncounterId, scheduledRoomId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingTickets.Count == 0)
            return new ExpeditionTransitionPlan(false, false, null, held, Array.Empty<string>());

        return new ExpeditionTransitionPlan(
            ShouldTrigger: true,
            RequiresChoice: false,
            DirectRoomEncounterId: scheduledRoomId,
            HeldTickets: matchingTickets,
            CandidateRoomEncounterIds: new[] { scheduledRoomId });
    }

    public ExpeditionTransitionPlan BuildDayStartExpeditionPlan(int maxRoomChoices = 3)
        => BuildDayStartExpeditionPlan(_run, _pool, _repo, maxRoomChoices);

    /// <summary>消耗与指定 Expedition 房间匹配的一张持有门票（优先 Hand，其次 Stash），从网格与 Cards 移除并派发 Destroyed 事件。</summary>
    public bool ConsumeExpeditionTicketForRoom(string roomEncounterId, IList<IGameSimEvent>? events = null)
    {
        if (!CardPool.TryResolveTicketForExpeditionRoom(roomEncounterId, out var ticketTemplateId) ||
            string.IsNullOrEmpty(ticketTemplateId))
        {
            return false;
        }

        var match = FindHeldExpeditionTickets(_run)
            .FirstOrDefault(t => string.Equals(t.TicketTemplateId, ticketTemplateId, StringComparison.OrdinalIgnoreCase));
        if (match == null || !_run.Cards.TryGetValue(match.InstanceId, out var entry))
            return false;

        if (entry.CoreCard != null)
            entry.CoreCard.IsDestroyed = true;
        RemoveEntryFromGridsAndDict(entry);
        events?.Add(new GameSimEventCardDisposed(entry.InstanceId, ECardDisposalReason.Destroyed));
        return true;
    }

    /// <summary>在 Day-start ChoiceState 发出一组待选 Expedition 房间卡（持票置零 BuyPrice，供玩家选择进入）。</summary>
    public EncounterStepResult DealDayStartExpeditionSelection(int maxChoices = 3)
    {
        var plan = BuildDayStartExpeditionPlan(maxChoices);
        if (!plan.ShouldTrigger)
            return EncounterStepResult.Fail("当前未持有可用的探险门票");

        var events = _shop.DealExpeditionRoomChoices(plan.CandidateRoomEncounterIds);
        var result = new EncounterStepResult
        {
            Success = true,
            ShouldProgressHour = false,
            DealtNewCards = _run.SelectionSet.Count > 0,
            MaxSelections = _shop.CurrentMaxSelections,
        };
        result.Events.AddRange(events);
        foreach (string id in _run.SelectionSet)
        {
            if (_run.Cards.TryGetValue(id, out var entry))
                result.DealtCards.Add(entry);
        }
        result.ChangeLogs.Add($"ExpeditionChoice:{plan.CandidateRoomEncounterIds.Count}");
        return result;
    }

    /// <summary>
    /// D2-P1-07：零副作用只读校验探险房间准入前提（房间合法性、资产白名单及可选门票持有检查），
    /// 供 RunEngine 在修改 Visited* 与摘除货架卡前先行拦截。
    /// </summary>
    public bool CanEnterExpeditionRoom(string roomEncounterId, bool requireMatchingTicket, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(roomEncounterId) || !CardPool.IsExpeditionRoomTemplate(roomEncounterId))
        {
            errorMessage = $"未知的探险房间：{roomEncounterId}";
            return false;
        }
        if (!_pool.IsAssetValid(roomEncounterId) || (_pool.Get(roomEncounterId) == null && _repo.GetCard(roomEncounterId) == null))
        {
            errorMessage = $"探险房间资源缺失或无效：{roomEncounterId}";
            return false;
        }
        if (requireMatchingTicket)
        {
            bool hasTicket = FindHeldExpeditionTickets(_run)
                .Any(t => string.Equals(t.RoomEncounterId, roomEncounterId, StringComparison.OrdinalIgnoreCase));
            if (!hasTicket)
            {
                errorMessage = "未持有进入该探险房间所需的门票";
                return false;
            }
        }
        errorMessage = null;
        return true;
    }

    /// <summary>
    /// 直接切入指定 Expedition 房间遭遇：可选自动消耗对应门票并调用 ShopEngine.DealEncounterShelf 展开房间分支步骤。
    /// </summary>
    public EncounterStepResult EnterExpeditionRoom(string roomEncounterId, bool consumeMatchingTicket = true)
    {
        if (!CanEnterExpeditionRoom(roomEncounterId, requireMatchingTicket: false, out string? err))
            return EncounterStepResult.Fail(err ?? $"进入探险房间 {roomEncounterId} 失败");

        var result = new EncounterStepResult
        {
            Success = true,
            ShouldProgressHour = false,
            NextEncounterId = roomEncounterId,
            NextEncounterType = ECardType.EventEncounter,
            TransitionsToSubEncounter = true,
        };

        if (consumeMatchingTicket && ConsumeExpeditionTicketForRoom(roomEncounterId, result.Events))
        {
            result.ChangeLogs.Add($"ConsumeTicket:{roomEncounterId}");
        }

        // P1-01：激活探险上下文与默认替换集映射（后续步骤的 NewSetIdOnExit 会进一步切换至具体元素门/分支替换集）
        _run.ActiveExpeditionId = roomEncounterId;
        _run.ActiveExpeditionVariant = null;
        _run.ActiveReplacementSetId = ShopEngine.TryResolveReplacementSetIdForExpedition(roomEncounterId, variant: null);
        _run.ExpeditionVisitedRoomIds.Clear();
        _run.ExpeditionVisitedRoomIds.Add(roomEncounterId);

        var (syncedPlayer, _, _, _, _, _) = BuildSyncedPlayerState();
        result.Events.AddRange(_shop.DealEncounterShelf(roomEncounterId, isReroll: false, playerState: syncedPlayer));

        // P1-04：若探险入口房配置了 BoardOverride，持久化 CurrentBoardId 并与 StateTransitioned 同包发射 GameSimEventBoardOverridden
        var roomPoolTpl = _pool.Get(roomEncounterId);
        var roomCoreTpl = _repo.GetCard(roomEncounterId);
        string? boardOverride = roomCoreTpl?.BoardOverride ?? roomPoolTpl?.BoardOverride;
        string? presentation = roomCoreTpl?.Presentation ?? roomPoolTpl?.Presentation;
        if (!string.IsNullOrWhiteSpace(boardOverride))
        {
            _run.CurrentBoardId = boardOverride;
            result.Events.Add(new GameSimEventBoardOverridden(boardOverride, presentation));
        }

        result.DealtNewCards = _run.SelectionSet.Count > 0;
        result.MaxSelections = _shop.CurrentMaxSelections;
        foreach (string id in _run.SelectionSet)
        {
            if (_run.Cards.TryGetValue(id, out var entry))
                result.DealtCards.Add(entry);
        }
        result.ChangeLogs.Add($"EnterExpedition:{roomEncounterId}");
        return result;
    }

    /// <summary>
    /// Day-start 统一入口辅助：
    ///   · 持有 1 种门票时直接消费门票并切入对应 Expedition 房间；
    ///   · 持有多种门票时在 ChoiceState 展开全部合法 Expedition 房间供玩家选择；
    ///   · 未持票时返回 Success=false。
    /// </summary>
    public EncounterStepResult TryEnterDayStartExpedition(bool consumeMatchingTicket = true)
    {
        var plan = BuildDayStartExpeditionPlan();
        if (!plan.ShouldTrigger)
            return EncounterStepResult.Fail("当前未持有可用的探险门票");

        if (!plan.RequiresChoice && !string.IsNullOrEmpty(plan.DirectRoomEncounterId))
            return EnterExpeditionRoom(plan.DirectRoomEncounterId, consumeMatchingTicket);

        return DealDayStartExpeditionSelection();
    }

    /// <summary>
    /// P1-25：克隆并过滤 SpawnContext 内全部 TSpawnFilterIdList 中在当前 (day, hour) 已过期的探险门票 GUID，
    /// 使 Browse All Tickets / Surprise Me / [Jungle Ruins] Grab the Loot 等均只从合法未过期门票中抽选。
    /// </summary>
    public static RuleNode? FilterExpiredExpeditionTicketsInSpawnContext(RuleNode? spawnContext, int day, int hour)
    {
        if (spawnContext == null || spawnContext.Groups is not { Count: > 0 })
            return spawnContext;

        bool anyChanged = false;
        var clonedGroups = new List<RuleNode>(spawnContext.Groups.Count);
        foreach (var grp in spawnContext.Groups)
        {
            if (grp?.Filters is not { Count: > 0 })
            {
                clonedGroups.Add(grp!);
                continue;
            }

            bool grpChanged = false;
            bool dropGroup = false;
            var clonedFilters = new List<RuleNode>(grp.Filters.Count);
            foreach (var f in grp.Filters)
            {
                if (f != null &&
                    f.IsNot != true &&
                    string.Equals(f.Type, "TSpawnFilterIdList", StringComparison.OrdinalIgnoreCase) &&
                    f.Ids is { Count: > 0 } &&
                    f.Ids.Any(id => CardPool.IsExpeditionTicketExpired(id, day, hour)))
                {
                    var remainingIds = f.Ids
                        .Where(id => !CardPool.IsExpeditionTicketExpired(id, day, hour))
                        .ToList();
                    if (remainingIds.Count == 0)
                    {
                        // 正向 ID 白名单全部过期时整组剔除，防止空 Ids 在 SpawnQueryEngine 中退化为全库发牌
                        dropGroup = true;
                        anyChanged = true;
                        break;
                    }
                    clonedFilters.Add(new RuleNode
                    {
                        Type = f.Type,
                        Ids = remainingIds,
                        IsNot = f.IsNot,
                    });
                    grpChanged = true;
                    anyChanged = true;
                }
                else
                {
                    clonedFilters.Add(f!);
                }
            }

            if (dropGroup)
                continue;

            if (grpChanged)
            {
                clonedGroups.Add(new RuleNode
                {
                    Type = grp.Type,
                    Filters = clonedFilters,
                    SelectionMethod = grp.SelectionMethod,
                    Limit = grp.Limit,
                    Prerequisites = grp.Prerequisites,
                    RandomWeight = grp.RandomWeight,
                    Behaviors = grp.Behaviors,
                });
            }
            else
            {
                clonedGroups.Add(grp);
            }
        }

        if (!anyChanged) return spawnContext;

        return new RuleNode
        {
            Type = spawnContext.Type,
            Groups = clonedGroups,
            SelectionMethod = spawnContext.SelectionMethod,
            Limit = spawnContext.Limit,
            Behaviors = spawnContext.Behaviors,
        };
    }
}
