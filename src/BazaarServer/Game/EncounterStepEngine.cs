using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
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
    public bool DealtNewCards { get; set; }
    public bool RerollTriggered { get; set; }
    public string? NextEncounterId { get; set; }
    public ECardType? NextEncounterType { get; set; }
    public bool TriggersCombat { get; set; }
    public bool TransitionsToPedestal { get; set; }
    public bool TransitionsToSubEncounter { get; set; }
    public int GoldSpent { get; set; }
    public int ExperienceAwarded { get; set; }
    public int? MaxSelections { get; set; }

    public List<string> ChangeLogs { get; } = new();
    public List<IGameSimEvent> Events { get; } = new();
    public List<RunState.CardEntry> DealtCards { get; } = new();
    public List<RunState.CardEntry> SpawnedCards { get; } = new();
    public List<RunState.CardEntry> TransformedCards { get; } = new();

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

        return ExecuteStep(stepEntry, grantExperience);
    }

    public EncounterStepResult ExecuteStep(
        RunState.CardEntry stepEntry,
        Func<int, List<IGameSimEvent>>? grantExperience = null)
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

        // 2. 构建权威 PlayerState 与 ExecutionContext，并校验 SelectionRequirements
        var (playerState, ownedByCore, initialHandSet, initialStashSet, initialSkillSet, initialSocketSet) = BuildSyncedPlayerState();
        var (shelfState, shelfByCore) = BuildShelfState();

        var stepInstance = stepEntry.CoreCard ?? BuildStepCardInstance(stepEntry, coreTpl);
        var result = new EncounterStepResult
        {
            Success = true,
            IsReselectable = coreTpl?.IsReselectable ?? poolTpl?.IsReselectable ?? false,
            ShouldProgressHour = coreTpl?.ShouldProgressHour ?? poolTpl?.ShouldProgressHour ?? true,
            MaxSelections = _shop.CurrentMaxSelections,
        };

        var ctx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, playerState, shelfState, _rng)
        {
            CurrentDay = (int)_run.Day,
            CurrentHour = (int)_run.Hour,
            CurrentEncounterId = _run.CurrentEncounterId,
            PlayMode = _run.PlayMode.ToString(),
            SelfCard = stepInstance,
            TriggerSourceCard = stepInstance,
            OnRerollEncounter = rerollType =>
            {
                result.RerollTriggered = true;
                result.ChangeLogs.Add($"Reroll({rerollType})");
            },
            OnExitSelection = () =>
            {
                result.ExitedEncounter = true;
                result.ChangeLogs.Add("ExitReplacementSet");
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
                RefreshShelfState(shelfState, shelfByCore);
            },
        };

        if (!ValidateSelectionRequirements(poolTpl, coreTpl, ctx, out string? reqErr))
            return EncounterStepResult.Fail(reqErr ?? "未满足该选项的触发前提条件");

        // 记录执行前快照用于精准生成变更日志与事件（扣费前记快照以便 ChangeLogs 统一记录净变化）
        var prevPlayerAttrs = new Dictionary<string, double>(playerState.Attributes, StringComparer.OrdinalIgnoreCase);

        // 扣费
        if (buyPrice > 0)
        {
            playerState.ModifyAttribute("Gold", "Subtract", buyPrice);
            result.GoldSpent = buyPrice;
        }

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

        foreach (var ability in abilitiesToRun)
        {
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

            ExecuteStepAction(ability.Action, stepInstance, ctx, result, shelfState, () => RefreshShelfState(shelfState, shelfByCore));
        }

        // 若 reroll 触发且未重新 DealCards，则重抽当前遭遇货架
        if (result.RerollTriggered && !result.DealtNewCards && !string.IsNullOrEmpty(_run.CurrentEncounterId))
        {
            result.Events.AddRange(_shop.DealEncounterShelf(_run.CurrentEncounterId, isReroll: true, playerState));
            result.DealtNewCards = _run.SelectionSet.Count > 0;
        }

        // 4. 将 PlayerState 变动无损回写 RunState，并生成完整增量事件与变更日志
        SyncPlayerAttributesBack(playerState, prevPlayerAttrs, result, grantExperience);
        SyncOwnedCardsBack(playerState, ownedByCore, initialHandSet, initialStashSet, initialSkillSet, initialSocketSet, prevCardSnapshot, result);
        SyncShelfChangesBack(shelfState, shelfByCore, result);

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
                    _run.PendingEncounterExitXp = Math.Max(0, _run.PendingEncounterExitXp - xpAward);
                }
                result.ExperienceAwarded = xpAward;
                result.ChangeLogs.Add($"XP+{xpAward}");
                if (grantExperience != null)
                {
                    result.Events.AddRange(grantExperience(xpAward));
                }
                else
                {
                    int curXp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
                    _run.PlayerAttributes[EPlayerAttributeType.Experience] = curXp + xpAward;
                    result.Events.Add(new GameSimEventPlayerExperienceGained((uint)xpAward, EExperienceType.Hour));
                }
            }
            else if (_run.PendingEncounterExitXp > 0)
            {
                _run.PendingEncounterExitXp = Math.Max(0, _run.PendingEncounterExitXp - xpAward);
            }
        }

        // 6. 计算 NextEncounterOnSelection（按权重与 Prerequisites 条件抽取下一跳遭遇）
        var nextOptions = coreTpl?.NextEncounterOnSelection ?? poolTpl?.NextEncounterOnSelection;
        string? nextEncounterId = PickNextEncounter(nextOptions, ctx);
        if (nextEncounterId == null && result.ExitedEncounter && !string.IsNullOrEmpty(coreTpl?.NewSetIdOnExit ?? poolTpl?.NewSetIdOnExit))
        {
            string? exitNext = coreTpl?.NewSetIdOnExit ?? poolTpl?.NewSetIdOnExit;
            if (_pool.IsAssetValid(exitNext) || _repo.GetCard(exitNext) != null)
                nextEncounterId = exitNext;
        }

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
        PlayerState shelfState,
        Action? refreshShelfState = null)
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
                        ExecuteStepAction(sub, stepCard, ctx, result, shelfState, refreshShelfState);
                }
                return;

            case "TActionGameDealCards":
                if (action.Cost != null && !AbilityExecutor.ExecuteAction(action.Cost, stepCard, ctx))
                    return;
                var normalizedDealCtx = action.SpawnContext != null
                    ? ShopEngine.NormalizeSequentialSingleGroupQuery(action.SpawnContext)
                    : null;
                IReadOnlySet<string>? validAssetIds = _pool.WhitelistEnabled ? _pool.ValidCardIds : null;
                IReadOnlyDictionary<string, CardTier>? ownedMaxTiers = IsCurrentEncounterMerchant()
                    ? ShopEngine.BuildOwnedMaxTiers(_run, ctx.SelfPlayer)
                    : null;
                var dealt = SpawnQueryEngine.SpawnCards(
                    normalizedDealCtx,
                    ctx,
                    defaultCountOverride: null,
                    ownedMaxTiers: ownedMaxTiers,
                    validAssetIds: validAssetIds);
                ApplyDealtCardsToSelection(
                    dealt,
                    action.SpawnMode ?? "Replace",
                    action.SelectionContextRules,
                    result,
                    playerState: ctx.SelfPlayer,
                    hasExplicitSpawnContext: action.SpawnContext != null,
                    spawnContextNode: action.SpawnContext,
                    evalCtx: ctx);
                refreshShelfState?.Invoke();
                return;

            case "TActionCardModifyAttribute":
            case "TActionCardUpgrade":
            case "TActionCardEnchant":
            case "TActionCardEnchantRandom":
                if (IsSelectionSetTarget(action.Target))
                {
                    refreshShelfState?.Invoke();
                    // TargetResolver 对 SelectionSet 默认 yield break，此处回退让货架卡片也能正确生效
                    var shelfCtx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, shelfState, ctx.SelfPlayer, _rng)
                    {
                        CurrentDay = ctx.CurrentDay,
                        CurrentHour = ctx.CurrentHour,
                        CurrentEncounterId = ctx.CurrentEncounterId,
                        SelfCard = stepCard,
                        TriggerSourceCard = stepCard,
                    };
                    var rewrittenTarget = new RuleNode
                    {
                        Type = action.Target!.Type,
                        TargetSection = "SelfHand",
                        TargetMode = action.Target.TargetMode,
                        ExcludeSelf = action.Target.ExcludeSelf,
                        RawConditionsElement = action.Target.RawConditionsElement,
                    };
                    var clonedAction = CloneActionWithTarget(action, rewrittenTarget);
                    AbilityExecutor.ExecuteAction(clonedAction, stepCard, shelfCtx);
                    return;
                }
                AbilityExecutor.ExecuteAction(action, stepCard, ctx);
                return;

            default:
                AbilityExecutor.ExecuteAction(action, stepCard, ctx);
                return;
        }
    }

    private static bool IsSelectionSetTarget(RuleNode? target)
        => target != null && string.Equals(target.TargetSection, "SelectionSet", StringComparison.OrdinalIgnoreCase);

    private static RuleNode CloneActionWithTarget(RuleNode src, RuleNode newTarget) => new()
    {
        Type = src.Type,
        AttributeType = src.AttributeType,
        Operation = src.Operation,
        RawValueNode = src.RawValueNode,
        ReferenceValue = src.ReferenceValue,
        Duration = src.Duration,
        Target = newTarget,
        TargetCount = src.TargetCount,
        Cost = src.Cost,
        Enchantment = src.Enchantment,
        RawEnchantmentsElement = src.RawEnchantmentsElement,
        UpgradeToTier = src.UpgradeToTier,
        PreventOverride = src.PreventOverride,
    };

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
        RuleExecutionContext? evalCtx = null)
    {
        bool isReplaceMode = string.Equals(spawnMode, "Replace", StringComparison.OrdinalIgnoreCase);
        bool isMerchant = IsCurrentEncounterMerchant();
        if (isReplaceMode)
        {
            result.Events.AddRange(_shop.ClearShelf());
        }

        // Bug #4：非商人奇遇通过 Replace 发牌（尤其是免费选物品事件）若未显式给出 CanSelectMultiple=true 或 rulesNode 为空，
        // 严禁继承旧规则的 CanSelectMultiple=true，强制默认单选免费可退出（选 1 件即完成选择）。
        bool defaultSingleFreeForReplace = isReplaceMode && !isMerchant;
        if (rulesNode != null)
        {
            ApplyRuleNodeToSelectionRules(rulesNode, defaultSingleFreeForReplace);
        }
        else if (defaultSingleFreeForReplace)
        {
            // 修复 #4 残留：rulesNode 为空时清空旧上下文的 RerollRules / WillAutoSellOnExit / NextEncounterOnExit，绝不误继承
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
                .Select(id => _run.Cards[id].TemplateId.ToString()),
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
            playerState ?? new PlayerState("p1", "Player", _run.Hero.ToString(), 250, 20, 0, 10),
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
            var poolTpl = _pool.Get(inst.Template.Id);
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

            int width = ShopEngine.GetShelfSlotWidth(inst);
            if (usedWidth + width > ShopEngine.MaxShelfSlotWidth)
                continue;

            var entry = _shop.CreateEntryFromCoreInstance(inst, _run.SelectionRules.SelectionIsFree);
            if (isPaidContext && entry.Type is ECardType.Item or ECardType.Skill &&
                entry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0) <= 0)
            {
                continue;
            }

            _run.Cards[entry.InstanceId] = entry;
            _run.SelectionSet.Add(entry.InstanceId);
            result.DealtCards.Add(entry);
            result.Events.Add(new GameSimEventCardDealt(entry.InstanceId, entry.TemplateId.ToString(), entry.Type));
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

        RerollRules? reroll = _run.SelectionRules.RerollRules;
        if (ext.TryGetValue("RerollRules", out var rrEl) && rrEl.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var rrData = rrEl.Deserialize<RerollRulesData>(GameDatabaseLoader.JsonOptions);
                if (rrData != null)
                {
                    var (resolvedRules, startingCost, rerollsRemaining) = ShopEngine.ResolveRerollSettings(rrData);
                    reroll = resolvedRules;
                    _run.RerollCost = startingCost;
                    _run.RerollsRemaining = rerollsRemaining;
                }
            }
            catch { /* ignore malformed optional reroll node */ }
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

        if (coreTpl?.SelectionRequirements != null &&
            !ShopEngine.EvaluateRequirementNode(coreTpl.SelectionRequirements, ctx))
        {
            reason = "selection requirements failed";
            return false;
        }

        if (poolTpl?.SelectionRequirements is { Count: > 0 })
        {
            foreach (var req in poolTpl.SelectionRequirements)
            {
                if (!ShopEngine.EvaluateRequirementNode(req, ctx))
                {
                    reason = "selection requirement prerequisites not met";
                    return false;
                }
            }
        }

        return true;
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
        int hpMax = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250);
        int hp = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, hpMax);
        int prestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20);
        int gold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 8);
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
        // P0-1：从 _run.PlayerAttributes 剥离局外光环追踪层，将 AuraAdd/AuraMult 放回对应字段，防止属性加成被反复吞入基础值
        foreach (var (ak, av) in _run.PlayerAttributes)
        {
            string key = ak.ToString();
            double baseVal = av;
            double mult = _run.PlayerAuraMult.GetValueOrDefault(key, 1.0);
            if (Math.Abs(mult) > 1e-6 && Math.Abs(mult - 1.0) > 1e-6)
                baseVal /= mult;
            baseVal -= _run.PlayerAuraAdd.GetValueOrDefault(key, 0.0);
            ps.Attributes[key] = baseVal;
        }
        foreach (var (k, v) in _run.PlayerAuraAdd) ps.AuraAdd[k] = v;
        foreach (var (k, v) in _run.PlayerAuraMult) ps.AuraMult[k] = v;

        ps.CurrentHealth = Math.Clamp(hp, 0, Math.Max(1, hpMax));
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

    private (PlayerState Shelf, Dictionary<CardInstance, RunState.CardEntry> ShelfByCore) BuildShelfState()
    {
        var shelf = new PlayerState("shelf", "Shelf", _run.Hero.ToString(), 100, 20, 0, 10);
        shelf.Hand.UnlockedMask = 1023;
        var map = new Dictionary<CardInstance, RunState.CardEntry>();
        RefreshShelfState(shelf, map);
        return (shelf, map);
    }

    private void RefreshShelfState(PlayerState shelf, Dictionary<CardInstance, RunState.CardEntry> map)
    {
        foreach (var existing in shelf.Hand.Cards.ToList())
            shelf.Hand.Remove(existing);
        map.Clear();
        foreach (string id in _run.SelectionSet)
        {
            if (!_run.Cards.TryGetValue(id, out var entry)) continue;
            var core = EnsureSyncedCoreCard(entry);
            if (core == null) continue;
            map[core] = entry;
            shelf.Hand.TryAddAuto(core);
        }
    }

    private CardInstance? EnsureSyncedCoreCard(RunState.CardEntry entry)
    {
        var coreTpl = _repo.GetCard(entry.TemplateId.ToString());
        if (entry.CoreCard == null)
        {
            if (coreTpl == null)
            {
                coreTpl = new BazaarCore.Models.CardTemplate
                {
                    Id = entry.TemplateId.ToString(),
                    Type = entry.Type.ToString(),
                    Size = entry.Size.ToString(),
                    StartingTier = entry.Tier.ToString(),
                };
            }
            entry.CoreCard = new CardInstance(coreTpl, ShopEngine.ToCoreTier(entry.Tier), entry.Enchantment?.ToString(), entry.InstanceId);
        }

        var ci = entry.CoreCard;
        if (entry.Socket.HasValue) ci.StartSlot = (int)entry.Socket.Value;
        foreach (var (ak, av) in entry.Attributes)
        {
            string key = ak.ToString();
            if ((int)Math.Round(ci.GetAttribute(key)) == av) continue;
            double auraMult = ci.AuraMult.GetValueOrDefault(key, 1.0);
            double auraAdd = ci.AuraAdd.GetValueOrDefault(key, 0.0);
            double effectiveBase = Math.Abs(auraMult) > 1e-6 && Math.Abs(auraMult - 1.0) > 1e-6
                ? av / auraMult
                : av;
            effectiveBase -= auraAdd;
            double baseV = ci.BaseAttributes.GetValueOrDefault(key, 0);
            ci.PermanentAdd[key] = effectiveBase - baseV;
        }
        return ci;
    }

    private CardInstance BuildStepCardInstance(RunState.CardEntry entry, BazaarCore.Models.CardTemplate? coreTpl)
    {
        var tpl = coreTpl ?? new BazaarCore.Models.CardTemplate
        {
            Id = entry.TemplateId.ToString(),
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
            foreach (var (k, v) in ci.BaseAttributes)
                attrs[k] = ci.GetAttribute(k);
            foreach (var (k, _) in ci.PermanentAdd)
                attrs[k] = ci.GetAttribute(k);
            snap[ci] = new CardSnap(ci.Tier, ci.Enchantment, ci.StartSlot, attrs, ci.ExtraTags.Count);
        }
        return snap;
    }

    private void SyncPlayerAttributesBack(
        PlayerState ps,
        Dictionary<string, double> prevAttrs,
        EncounterStepResult result,
        Func<int, List<IGameSimEvent>>? grantExperience = null)
    {
        // Health 特殊处理：PlayerState.CurrentHealth 反映当前血量
        ps.Attributes["Health"] = ps.CurrentHealth;

        int deferredXpDelta = 0;
        foreach (EPlayerAttributeType attrType in Enum.GetValues<EPlayerAttributeType>())
        {
            string key = attrType.ToString();
            if (!ps.Attributes.TryGetValue(key, out double curBaseVal)) continue;

            // 仅回写执行期在 PlayerState 上真实发生变动的属性，防止经验升级写回的新 Level/HealthMax 被后续旧快照覆写还原
            bool changedInStep = !prevAttrs.TryGetValue(key, out double prevVal) || Math.Abs(curBaseVal - prevVal) > 1e-6;
            if (!changedInStep) continue;

            double effectiveVal = attrType == EPlayerAttributeType.Health
                ? curBaseVal
                : (curBaseVal
                   + ps.AuraAdd.GetValueOrDefault(key, 0.0)
                   + ps.HourAdd.GetValueOrDefault(key, 0.0)
                   + ps.DayAdd.GetValueOrDefault(key, 0.0))
                  * ps.AuraMult.GetValueOrDefault(key, 1.0)
                  * ps.HourMult.GetValueOrDefault(key, 1.0)
                  * ps.DayMult.GetValueOrDefault(key, 1.0);

            int newInt = (int)Math.Round(effectiveVal);
            int oldInt = _run.PlayerAttributes.TryGetValue(attrType, out int oldV) ? oldV : 0;
            if (newInt == oldInt) continue;

            int delta = newInt - oldInt;
            // 奇遇步骤内直接加经验（如 Trip Him Up +2 XP / Join the Cult +3 XP）延迟到属性同步循环完成后再经 grantExperience 正规结算升级、PendingLevelUps 与格位解锁，
            // 防止后续遍历到 Level/HealthMax 时被旧值覆写回降。
            if (attrType == EPlayerAttributeType.Experience && delta > 0 && grantExperience != null)
            {
                deferredXpDelta += delta;
                continue;
            }

            _run.PlayerAttributes[attrType] = newInt;
            if (attrType == EPlayerAttributeType.Gold && delta > 0)
                _run.IncrementPlayerAttribute(EPlayerAttributeType.Custom_3, delta);
            result.ChangeLogs.Add($"{key}{(delta >= 0 ? "+" : "")}{delta}");

            if (attrType == EPlayerAttributeType.Income && delta > 0)
                result.Events.Add(new GameSimEventPlayerIncomeGained((uint)delta));
            else if (attrType == EPlayerAttributeType.Prestige)
                result.Events.Add(new GameSimEventPlayerPrestigeChanged(delta));
        }

        if (deferredXpDelta > 0 && grantExperience != null)
        {
            result.Events.AddRange(grantExperience(deferredXpDelta));
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
        EncounterStepResult result)
    {
        var currentHand = new HashSet<CardInstance>(ps.Hand.Cards);
        var currentStash = new HashSet<CardInstance>(ps.Stash.Cards);
        var currentSkills = new HashSet<CardInstance>(ps.Skills);
        var currentSocketEffects = new HashSet<CardInstance>(ps.SocketEffects);
        var matchedReplacements = new HashSet<CardInstance>();

        // 1. 检查已存在卡牌的销毁、变阶、附魔、属性与标签更新
        foreach (var (core, entry) in ownedByCore)
        {
            bool stillPresent = currentHand.Contains(core)
                             || currentStash.Contains(core)
                             || currentSkills.Contains(core)
                             || currentSocketEffects.Contains(core);
            if (!stillPresent || core.IsDestroyed)
            {
                // 判断是否被 TActionCardTransform 变形为另一张新卡（优先同槽位，其次同区未匹配新卡）
                CardInstance? replacement = null;
                if (initialHand.Contains(core))
                {
                    replacement = ps.Hand.Cards.FirstOrDefault(c => !ownedByCore.ContainsKey(c) && !matchedReplacements.Contains(c) && c.StartSlot == core.StartSlot)
                                  ?? ps.Hand.Cards.FirstOrDefault(c => !ownedByCore.ContainsKey(c) && !matchedReplacements.Contains(c));
                }
                if (replacement == null && initialStash.Contains(core))
                {
                    replacement = ps.Stash.Cards.FirstOrDefault(c => !ownedByCore.ContainsKey(c) && !matchedReplacements.Contains(c) && c.StartSlot == core.StartSlot)
                                  ?? ps.Stash.Cards.FirstOrDefault(c => !ownedByCore.ContainsKey(c) && !matchedReplacements.Contains(c));
                }
                if (replacement == null && initialSocketEffects.Contains(core))
                {
                    replacement = ps.SocketEffects.FirstOrDefault(c => !ownedByCore.ContainsKey(c) && !matchedReplacements.Contains(c) && c.StartSlot == core.StartSlot)
                                  ?? ps.SocketEffects.FirstOrDefault(c => !ownedByCore.ContainsKey(c) && !matchedReplacements.Contains(c));
                }

                RemoveEntryFromGridsAndDict(entry);

                if (replacement != null)
                {
                    _run.TrackReagentTransformIfApplicable(core, entry);
                    matchedReplacements.Add(replacement);
                    var sec = ps.Stash.Cards.Contains(replacement)
                        ? EInventorySection.Stash
                        : (ps.Skills.Contains(replacement) ? (EInventorySection?)null : EInventorySection.Hand);
                    var newEntry = RegisterNewOwnedCoreCard(replacement, sec, result, isTransform: true);
                    result.TransformedCards.Add(newEntry);
                    result.Events.Add(new GameSimEventCardTransformed(
                        "step_" + entry.InstanceId,
                        entry.InstanceId,
                        new List<SimEventCardTransformation>
                        {
                            new SimEventCardTransformation(
                                newEntry.InstanceId,
                                newEntry.TemplateId.ToString(),
                                newEntry.Type,
                                ECombatantId.Player,
                                newEntry.Section,
                                newEntry.Socket)
                        }));
                    result.ChangeLogs.Add($"Transform:{entry.TemplateId}->{newEntry.TemplateId}");
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
                }
                if (!string.Equals(core.Enchantment, snap.Enchantment, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(core.Enchantment) &&
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
                }
            }

            foreach (var tag in core.ExtraTags)
            {
                if (Enum.TryParse<ECardTag>(tag, true, out var et))
                    entry.Tags.Add(et);
            }

            SyncEntryAttributesFromCore(entry, core);
        }

        // 2. 检查新生成（SpawnCards / 非变形式生成）的 Hand / Stash / Skill / SocketEffect 卡牌
        var registeredNew = new HashSet<CardInstance>(result.TransformedCards.Where(c => c.CoreCard != null).Select(c => c.CoreCard!));
        foreach (var c in ps.Hand.Cards)
        {
            if (ownedByCore.ContainsKey(c) || registeredNew.Contains(c)) continue;
            var newEntry = RegisterNewOwnedCoreCard(c, EInventorySection.Hand, result, isTransform: false);
            result.SpawnedCards.Add(newEntry);
            registeredNew.Add(c);
        }
        foreach (var c in ps.Stash.Cards)
        {
            if (ownedByCore.ContainsKey(c) || registeredNew.Contains(c)) continue;
            var newEntry = RegisterNewOwnedCoreCard(c, EInventorySection.Stash, result, isTransform: false);
            result.SpawnedCards.Add(newEntry);
            registeredNew.Add(c);
        }
        foreach (var s in ps.Skills)
        {
            if (ownedByCore.ContainsKey(s) || registeredNew.Contains(s)) continue;
            var newEntry = RegisterNewOwnedCoreCard(s, null, result, isTransform: false);
            result.SpawnedCards.Add(newEntry);
            registeredNew.Add(s);
        }
        foreach (var se in ps.SocketEffects)
        {
            if (ownedByCore.ContainsKey(se) || registeredNew.Contains(se)) continue;
            var newEntry = RegisterNewOwnedCoreCard(se, EInventorySection.Hand, result, isTransform: false);
            result.SpawnedCards.Add(newEntry);
            registeredNew.Add(se);
        }
    }

    private void SyncShelfChangesBack(
        PlayerState shelfState,
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

    private RunState.CardEntry RegisterNewOwnedCoreCard(
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
        if (cardType == ECardType.SocketEffect)
        {
            // 客户端要求 SocketEffect 挂在 Owner=Player, Section=Hand 下，且必须有 0..9 有效 LeftSocketId（但不占 HandGrid）
            section = EInventorySection.Hand;
            int resolvedSlot = core.StartSlot is >= 0 and <= 9
                ? core.StartSlot
                : FindFreeSocketEffectSlot();
            core.StartSlot = resolvedSlot;
            socket = (EContainerSocketId)resolvedSlot;
        }
        else if (cardType != ECardType.Skill && section.HasValue)
        {
            var grid = section == EInventorySection.Stash ? _run.StashGrid : _run.HandGrid;
            if (core.StartSlot >= 0)
            {
                if (!grid.TryPlaceOrShift(core, core.StartSlot))
                    grid.TryAddAuto(core);
            }
            else
            {
                grid.TryAddAuto(core);
            }
            if (core.StartSlot >= 0)
                socket = (EContainerSocketId)Math.Clamp(core.StartSlot, 0, 9);
        }

        EEnchantmentType? ench = null;
        if (!string.IsNullOrEmpty(core.Enchantment) &&
            Enum.TryParse<EEnchantmentType>(core.Enchantment, true, out var e))
        {
            ench = e;
        }

        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(core.Template.Id),
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
            var (buy, sell) = ShopEngine.PriceFor(entry.Size, entry.Tier);
            if (!entry.Attributes.ContainsKey(ECardAttributeType.BuyPrice))
                entry.Attributes[ECardAttributeType.BuyPrice] = buy;
            if (!entry.Attributes.ContainsKey(ECardAttributeType.SellPrice))
                entry.Attributes[ECardAttributeType.SellPrice] = sell;
        }

        _run.Cards[instId] = entry;
        if (!isTransform)
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

        return entry;
    }

    private int FindFreeSocketEffectSlot()
    {
        var used = new HashSet<int>();
        foreach (var c in _run.Cards.Values)
        {
            if (c.Owner == ECombatantId.Player && c.Type == ECardType.SocketEffect && c.Socket.HasValue)
                used.Add((int)c.Socket.Value);
        }
        for (int i = 0; i < 10; i++)
        {
            if (!used.Contains(i)) return i;
        }
        return 0;
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

    private static void SyncEntryAttributesFromCore(RunState.CardEntry entry, CardInstance core)
    {
        var keys = new HashSet<string>(core.BaseAttributes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in core.PermanentAdd.Keys) keys.Add(k);
        foreach (var k in core.PermanentMult.Keys) keys.Add(k);
        foreach (var k in core.AuraAdd.Keys) keys.Add(k);
        foreach (var k in core.AuraMult.Keys) keys.Add(k);

        foreach (string key in keys)
        {
            if (Enum.TryParse<ECardAttributeType>(key, out var attrType))
            {
                entry.Attributes[attrType] = attrType == ECardAttributeType.Flying
                    ? Math.Max(core.IsFlying ? 1 : 0, (int)Math.Round(core.GetAttribute(key)))
                    : (int)Math.Round(core.GetAttribute(key));
            }
        }
        if (core.IsFlying)
            entry.Attributes[ECardAttributeType.Flying] = 1;
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
    /// 直接切入指定 Expedition 房间遭遇：可选自动消耗对应门票并调用 ShopEngine.DealEncounterShelf 展开房间分支步骤。
    /// </summary>
    public EncounterStepResult EnterExpeditionRoom(string roomEncounterId, bool consumeMatchingTicket = true)
    {
        if (string.IsNullOrWhiteSpace(roomEncounterId) || !CardPool.IsExpeditionRoomTemplate(roomEncounterId))
            return EncounterStepResult.Fail($"未知的探险房间：{roomEncounterId}");
        if (!_pool.IsAssetValid(roomEncounterId) || (_pool.Get(roomEncounterId) == null && _repo.GetCard(roomEncounterId) == null))
            return EncounterStepResult.Fail($"探险房间资源缺失或无效：{roomEncounterId}");

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

        var (syncedPlayer, _, _, _, _, _) = BuildSyncedPlayerState();
        result.Events.AddRange(_shop.DealEncounterShelf(roomEncounterId, isReroll: false, playerState: syncedPlayer));
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
}
