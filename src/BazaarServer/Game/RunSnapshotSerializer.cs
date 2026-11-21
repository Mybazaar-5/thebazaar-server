using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarGameShared.TempoNet.Enums;
using BazaarServer.Data;

namespace BazaarServer.Game;

/// <summary>
/// 单局运行态（RunState）与挂起状态栈的无损 JSON 序列化器。
/// 负责将对局核心属性、候选项、选卡规则、PvP 对手信息、手牌/背包格子及每张卡牌的
/// CardEntry + CardInstance（PermanentAdd/PermanentMult/HourAdd/HourMult/DayAdd/DayMult/
/// ExtraTags/QuestProgress/CompletedQuests/TransformReplacementTemplateId 等）
/// 完整落盘，并在续玩或服务端重启时无损重建 RunState、HandGrid 与 StashGrid。
/// </summary>
public static class RunSnapshotSerializer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed class RunSnapshotDto
    {
        public int Version { get; set; } = 1;
        public string RunId { get; set; } = "";
        public Guid GameModeId { get; set; }
        public EHero Hero { get; set; }
        public EPlayMode PlayMode { get; set; }
        public string? AccountId { get; set; }
        public Guid? TournamentSessionId { get; set; }
        public CustomRunRuleSet? CustomRules { get; set; }
        public bool TendencyLocked { get; set; }
        public List<List<MonsterLootPageItemDto>>? MonsterLootPages { get; set; }
        public int MonsterLootPageIndex { get; set; }

        public uint Day { get; set; } = 1;
        public uint Hour { get; set; } = 1;
        public uint Victories { get; set; }
        public uint Defeats { get; set; }
        public bool HasVisitedFates { get; set; }
        public bool FatesUsed { get; set; }
        public string DataVersion { get; set; } = "5.0.0";

        public ERunState State { get; set; } = ERunState.Choice;
        public string? CurrentEncounterId { get; set; }
        public string? ParentEncounterTemplateId { get; set; }
        public string? ActiveEncounterCardInstanceId { get; set; }
        public uint RerollCost { get; set; } = 2;
        public uint RerollsRemaining { get; set; } = 3;
        public List<string> SelectionSet { get; set; } = new();
        public SelectionRulesSnapshotDto SelectionRules { get; set; } = new();
        public ushort UnlockedSlots { get; set; } = 0b00_0111_1000;

        public bool StartSelectionDone { get; set; }
        public bool PendingLevelUp { get; set; }
        public int PendingLevelUps { get; set; }
        public int PendingEncounterExitXp { get; set; }
        public bool GainedHourXpThisHour { get; set; }
        public bool GainedAdvanceHourXpThisHour { get; set; }
        public int SelectionsMadeInCurrentContext { get; set; }
        public int RngSeed { get; set; }

        public Dictionary<string, int> PlayerAttributes { get; set; } = new();
        public Dictionary<string, double> PlayerHourAdd { get; set; } = new();
        public Dictionary<string, double> PlayerHourMult { get; set; } = new();
        public Dictionary<string, double> PlayerDayAdd { get; set; } = new();
        public Dictionary<string, double> PlayerDayMult { get; set; } = new();
        public Dictionary<string, double> PlayerAuraAdd { get; set; } = new();
        public Dictionary<string, double> PlayerAuraMult { get; set; } = new();
        public List<string> VisitedSingleSpawnEncounterIds { get; set; } = new();
        public List<string> VisitedDayEncounterIds { get; set; } = new();
        public List<string> ExclusionSet { get; set; } = new();
        public bool ExclusionSetInitialized { get; set; }
        public Dictionary<string, int> OpponentAttributes { get; set; } = new();
        public PvpOpponentSnapshotDto? CurrentPvpOpponent { get; set; }
        public PvpGhostPool.GhostBoard? PendingPvpGhost { get; set; }

        /// <summary>预留给 Wave 2 局外升级/事件中断压栈（SuspendedStates）的序列化栈。</summary>
        public List<SuspendedStateSnapshotDto> SuspendedStateStack { get; set; } = new();

        public List<CardSnapshotEntryDto> Cards { get; set; } = new();
    }

    public sealed class MonsterLootPageItemDto
    {
        public string TemplateId { get; set; } = "";
        public string Tier { get; set; } = "Bronze";
        public string? Enchantment { get; set; }
        public int SlotCost { get; set; } = 1;
    }

    public sealed class SelectionRulesSnapshotDto
    {
        public bool CanSelectMultiple { get; set; } = true;
        public bool SelectionIsFree { get; set; }
        public bool CanExit { get; set; } = true;
        public RerollRulesSnapshotDto? RerollRules { get; set; }
        public bool WillAutoSellOnExit { get; set; }
        public Guid? NextEncounterOnExit { get; set; }
    }

    public sealed class RerollRulesSnapshotDto
    {
        public int? TotalAllowedRerolls { get; set; }
        public int? CostIncrease { get; set; }
        public int? StartingCost { get; set; }
        public int? CostMax { get; set; }
    }

    public sealed class SuspendedStateSnapshotDto
    {
        public ERunState State { get; set; }
        public string? CurrentEncounterId { get; set; }
        public string? ParentEncounterTemplateId { get; set; }
        public string? ActiveEncounterCardInstanceId { get; set; }
        public CardSnapshotEntryDto? ActiveEncounterCard { get; set; }
        public List<string> SelectionSet { get; set; } = new();
        public SelectionRulesSnapshotDto? SelectionRules { get; set; }
        public uint RerollCost { get; set; }
        public uint RerollsRemaining { get; set; }
        public int SelectionsMadeInCurrentContext { get; set; }
        public List<CardSnapshotEntryDto> SuspendedCards { get; set; } = new();
    }

    public sealed class PvpOpponentSnapshotDto
    {
        public string Name { get; set; } = "Unknown";
        public string TitlePrefix { get; set; } = "";
        public string TitleSuffix { get; set; } = "";
        public ERank? Rank { get; set; }
        public int Rating { get; set; }
        public int Division { get; set; }
        public uint Victories { get; set; }
        public int Prestige { get; set; }
        public int Level { get; set; }
        public EHero Hero { get; set; }
        public string? AccountId { get; set; }
    }

    public sealed class CardSnapshotEntryDto
    {
        public string InstanceId { get; set; } = "";
        public Guid TemplateId { get; set; }
        public ECardType Type { get; set; } = ECardType.Item;
        public ECardSize Size { get; set; } = ECardSize.Medium;
        public ETier Tier { get; set; } = ETier.Bronze;
        public EEnchantmentType? Enchantment { get; set; }
        public ECombatantId? Owner { get; set; }
        public EContainerSocketId? Socket { get; set; }
        public EInventorySection? Section { get; set; }
        public ECardState State { get; set; } = ECardState.Alive;
        public List<EHero> Heroes { get; set; } = new();
        public List<ECardTag> Tags { get; set; } = new();
        public List<EHiddenTag> HiddenTags { get; set; } = new();
        public Dictionary<string, int> Attributes { get; set; } = new();

        // CardInstance 持久态字段
        public bool HasCoreCard { get; set; }
        public int StartSlot { get; set; } = -1;
        public string? CoreEnchantment { get; set; }
        public Dictionary<string, double> PermanentAdd { get; set; } = new();
        public Dictionary<string, double> PermanentMult { get; set; } = new();
        public Dictionary<string, double> HourAdd { get; set; } = new();
        public Dictionary<string, double> HourMult { get; set; } = new();
        public Dictionary<string, double> DayAdd { get; set; } = new();
        public Dictionary<string, double> DayMult { get; set; } = new();
        public List<string> ExtraTags { get; set; } = new();
        public Dictionary<string, double> QuestProgress { get; set; } = new();
        public List<string> CompletedQuests { get; set; } = new();
        public string? TransformReplacementTemplateId { get; set; }
    }

    /// <summary>将 RunState 序列化为可落盘的 JSON 字符串。</summary>
    public static string Serialize(
        RunState run,
        bool? startSelectionDone = null,
        bool pendingLevelUp = false,
        IEnumerable<SuspendedStateSnapshotDto>? suspendedStates = null)
    {
        var dto = SerializeToDto(run, startSelectionDone, pendingLevelUp, suspendedStates);
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    /// <summary>将 RunState 转换为强类型快照 DTO（便于单测与上层组合）。</summary>
    public static RunSnapshotDto SerializeToDto(
        RunState run,
        bool? startSelectionDone = null,
        bool pendingLevelUp = false,
        IEnumerable<SuspendedStateSnapshotDto>? suspendedStates = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        int effectivePendingLevelUps = run.PendingLevelUps > 0
            ? run.PendingLevelUps
            : (pendingLevelUp ? 1 : 0);

        var dto = new RunSnapshotDto
        {
            Version = 1,
            RunId = run.RunId ?? "",
            GameModeId = run.GameModeId,
            Hero = run.Hero,
            PlayMode = run.PlayMode,
            AccountId = run.AccountId,
            TournamentSessionId = run.TournamentSessionId,
            CustomRules = (run.CustomRules ?? new CustomRunRuleSet()).CloneNormalized(),
            TendencyLocked = run.TendencyLocked,
            MonsterLootPageIndex = run.MonsterLootPageIndex,
            Day = run.Day,
            Hour = run.Hour,
            Victories = run.Victories,
            Defeats = run.Defeats,
            HasVisitedFates = run.FatesUsed,
            FatesUsed = run.FatesUsed,
            DataVersion = string.IsNullOrEmpty(run.DataVersion) ? "5.0.0" : run.DataVersion,
            State = run.State,
            CurrentEncounterId = run.CurrentEncounterId,
            ParentEncounterTemplateId = run.ParentEncounterTemplateId,
            ActiveEncounterCardInstanceId = run.ActiveEncounterCardInstanceId,
            RerollCost = run.RerollCost,
            RerollsRemaining = run.RerollsRemaining,
            SelectionSet = new List<string>(run.SelectionSet),
            SelectionRules = ToRulesDto(run.SelectionRules),
            UnlockedSlots = run.UnlockedSlots,
            StartSelectionDone = startSelectionDone ?? InferStartSelectionDone(run),
            PendingLevelUp = effectivePendingLevelUps > 0,
            PendingLevelUps = effectivePendingLevelUps,
            PendingEncounterExitXp = Math.Max(0, run.PendingEncounterExitXp),
            GainedHourXpThisHour = run.GainedHourXpThisHour,
            GainedAdvanceHourXpThisHour = run.GainedAdvanceHourXpThisHour,
            SelectionsMadeInCurrentContext = run.SelectionsMadeInCurrentContext,
            RngSeed = run.RngSeed,
            PendingPvpGhost = run.PendingPvpGhost,
        };

        foreach (var kv in run.PlayerAttributes)
            dto.PlayerAttributes[kv.Key.ToString()] = kv.Value;

        CopyDoubleDict(run.PlayerHourAdd, dto.PlayerHourAdd);
        CopyDoubleDict(run.PlayerHourMult, dto.PlayerHourMult);
        CopyDoubleDict(run.PlayerDayAdd, dto.PlayerDayAdd);
        CopyDoubleDict(run.PlayerDayMult, dto.PlayerDayMult);

        foreach (var kv in run.PlayerAuraAdd)
            dto.PlayerAuraAdd[kv.Key] = kv.Value;

        foreach (var kv in run.PlayerAuraMult)
            dto.PlayerAuraMult[kv.Key] = kv.Value;

        foreach (string visited in run.VisitedSingleSpawnEncounterIds)
            dto.VisitedSingleSpawnEncounterIds.Add(visited);

        foreach (string visitedDay in run.VisitedDayEncounterIds)
            dto.VisitedDayEncounterIds.Add(visitedDay);

        dto.ExclusionSetInitialized = run.ExclusionSetInitialized;
        foreach (string excludedId in run.ExclusionSet)
            dto.ExclusionSet.Add(excludedId);

        foreach (var kv in run.OpponentAttributes)
            dto.OpponentAttributes[kv.Key.ToString()] = kv.Value;

        if (run.CurrentPvpOpponent != null)
        {
            dto.CurrentPvpOpponent = new PvpOpponentSnapshotDto
            {
                Name = run.CurrentPvpOpponent.Name,
                TitlePrefix = run.CurrentPvpOpponent.TitlePrefix,
                TitleSuffix = run.CurrentPvpOpponent.TitleSuffix,
                Rank = run.CurrentPvpOpponent.Rank,
                Rating = run.CurrentPvpOpponent.Rating,
                Division = run.CurrentPvpOpponent.Division,
                Victories = run.CurrentPvpOpponent.Victories,
                Prestige = run.CurrentPvpOpponent.Prestige,
                Level = run.CurrentPvpOpponent.Level,
                Hero = run.CurrentPvpOpponent.Hero,
                AccountId = run.CurrentPvpOpponent.PlayerLoadout?.accountId,
            };
        }

        if (suspendedStates != null)
        {
            dto.SuspendedStateStack.AddRange(suspendedStates);
        }
        else if (run.SuspendedStates.Count > 0)
        {
            // Stack<T> 默认从栈顶枚举；反序写入以保持栈底→栈顶顺序，反序列化逐个 Push 即可原样恢复
            foreach (var s in run.SuspendedStates.Reverse())
            {
                var sDto = new SuspendedStateSnapshotDto
                {
                    State = s.State,
                    CurrentEncounterId = s.CurrentEncounterId,
                    ParentEncounterTemplateId = s.ParentEncounterTemplateId,
                    ActiveEncounterCardInstanceId = s.ActiveEncounterCardInstanceId,
                    ActiveEncounterCard = s.ActiveEncounterCard != null ? ToCardDto(s.ActiveEncounterCard) : null,
                    SelectionSet = new List<string>(s.SelectionSet),
                    SelectionRules = ToRulesDto(s.SelectionRules),
                    RerollCost = s.RerollCost,
                    RerollsRemaining = s.RerollsRemaining,
                    SelectionsMadeInCurrentContext = s.SelectionsMadeInCurrentContext,
                };
                foreach (var sc in s.SuspendedCards)
                    sDto.SuspendedCards.Add(ToCardDto(sc));
                dto.SuspendedStateStack.Add(sDto);
            }
        }

        if (run.MonsterLootPages.Count > 0)
        {
            dto.MonsterLootPages = run.MonsterLootPages
                .Select(page => page.Select(item => new MonsterLootPageItemDto
                {
                    TemplateId = item.TemplateId,
                    Tier = item.Tier,
                    Enchantment = item.Enchantment,
                    SlotCost = item.SlotCost,
                }).ToList())
                .ToList();
        }

        foreach (var card in run.Cards.Values)
            dto.Cards.Add(ToCardDto(card));

        return dto;
    }

    /// <summary>尝试反序列化快照 JSON，旧版非 JSON 文本或损坏数据会安全返回 false。</summary>
    public static bool TryDeserialize(
        string? json,
        GameDataRepository? repo,
        out RunState? run,
        out RunSnapshotDto? snapshot)
    {
        run = null;
        snapshot = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        string trimmed = json.TrimStart();
        if (!trimmed.StartsWith('{')) return false;

        try
        {
            snapshot = JsonSerializer.Deserialize<RunSnapshotDto>(json, JsonOpts);
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.RunId))
                return false;

            run = DeserializeFromDto(snapshot, repo);
            return true;
        }
        catch
        {
            run = null;
            snapshot = null;
            return false;
        }
    }

    /// <summary>从 JSON 字符串反序列化完整 RunState 并重建 HandGrid 与 StashGrid。</summary>
    public static RunState Deserialize(string json, GameDataRepository? repo)
    {
        if (!TryDeserialize(json, repo, out var run, out _) || run == null)
            throw new InvalidOperationException("Invalid run snapshot JSON.");
        return run;
    }

    /// <summary>从 RunSnapshotDto 重建完整 RunState、CardEntry、CardInstance、HandGrid 与 StashGrid。</summary>
    public static RunState DeserializeFromDto(RunSnapshotDto dto, GameDataRepository? repo)
    {
        ArgumentNullException.ThrowIfNull(dto);

        int restoredPendingLevelUps = dto.PendingLevelUps > 0
            ? dto.PendingLevelUps
            : (dto.PendingLevelUp ? 1 : 0);

        var run = new RunState
        {
            RunId = dto.RunId,
            GameModeId = dto.GameModeId,
            Hero = dto.Hero,
            PlayMode = dto.PlayMode,
            AccountId = dto.AccountId,
            TournamentSessionId = dto.TournamentSessionId,
            CustomRules = (dto.CustomRules ?? new CustomRunRuleSet()).CloneNormalized(),
            TendencyLocked = dto.TendencyLocked
                || dto.Day > 1
                || dto.Hour > 1
                || dto.State != ERunState.Choice
                || dto.SelectionsMadeInCurrentContext > 0,
            MonsterLootPageIndex = Math.Max(0, dto.MonsterLootPageIndex),
            Day = Math.Max(1u, dto.Day),
            Hour = Math.Max(1u, dto.Hour),
            Victories = dto.Victories,
            Defeats = dto.Defeats,
            FatesUsed = dto.FatesUsed || dto.HasVisitedFates,
            DataVersion = string.IsNullOrEmpty(dto.DataVersion) ? "5.0.0" : dto.DataVersion,
            State = dto.State,
            CurrentEncounterId = dto.CurrentEncounterId,
            ParentEncounterTemplateId = dto.ParentEncounterTemplateId,
            ActiveEncounterCardInstanceId = dto.ActiveEncounterCardInstanceId,
            ExclusionSetInitialized = dto.ExclusionSetInitialized,
            RerollCost = dto.RerollCost,
            RerollsRemaining = dto.RerollsRemaining,
            UnlockedSlots = dto.UnlockedSlots == 0 ? (ushort)0b00_0111_1000 : dto.UnlockedSlots,
            PendingLevelUps = restoredPendingLevelUps,
            PendingEncounterExitXp = Math.Max(0, dto.PendingEncounterExitXp),
            GainedHourXpThisHour = dto.GainedHourXpThisHour,
            GainedAdvanceHourXpThisHour = dto.GainedAdvanceHourXpThisHour,
            SelectionsMadeInCurrentContext = Math.Max(0, dto.SelectionsMadeInCurrentContext),
            RngSeed = dto.RngSeed,
            PendingPvpGhost = dto.PendingPvpGhost,
        };

        if (dto.MonsterLootPages is { Count: > 0 })
        {
            foreach (var page in dto.MonsterLootPages)
            {
                if (page == null) continue;
                var restoredPage = new List<RunState.MonsterLootPageItemState>();
                foreach (var item in page)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.TemplateId)) continue;
                    restoredPage.Add(new RunState.MonsterLootPageItemState(
                        item.TemplateId,
                        string.IsNullOrWhiteSpace(item.Tier) ? "Bronze" : item.Tier,
                        item.Enchantment,
                        Math.Max(1, item.SlotCost)));
                }
                if (restoredPage.Count > 0)
                    run.MonsterLootPages.Add(restoredPage);
            }
            if (run.MonsterLootPages.Count > 0)
                run.MonsterLootPageIndex = Math.Clamp(run.MonsterLootPageIndex, 0, run.MonsterLootPages.Count - 1);
            else
                run.MonsterLootPageIndex = 0;
        }

        if (dto.SelectionSet != null)
            run.SelectionSet.AddRange(dto.SelectionSet);

        if (dto.SelectionRules != null)
            run.SelectionRules = FromRulesDto(dto.SelectionRules);

        if (dto.PlayerAttributes is { Count: > 0 })
        {
            run.PlayerAttributes.Clear();
            foreach (var kv in dto.PlayerAttributes)
            {
                if (Enum.TryParse<EPlayerAttributeType>(kv.Key, true, out var pa))
                    run.PlayerAttributes[pa] = kv.Value;
            }
        }

        CopyDoubleDict(dto.PlayerHourAdd, run.PlayerHourAdd);
        CopyDoubleDict(dto.PlayerHourMult, run.PlayerHourMult);
        CopyDoubleDict(dto.PlayerDayAdd, run.PlayerDayAdd);
        CopyDoubleDict(dto.PlayerDayMult, run.PlayerDayMult);
        CopyDoubleDict(dto.PlayerAuraAdd, run.PlayerAuraAdd);
        CopyDoubleDict(dto.PlayerAuraMult, run.PlayerAuraMult);

        if (dto.VisitedSingleSpawnEncounterIds != null)
        {
            foreach (string visited in dto.VisitedSingleSpawnEncounterIds)
            {
                if (!string.IsNullOrWhiteSpace(visited))
                    run.VisitedSingleSpawnEncounterIds.Add(visited);
            }
        }

        if (dto.VisitedDayEncounterIds != null)
        {
            foreach (string visitedDay in dto.VisitedDayEncounterIds)
            {
                if (!string.IsNullOrWhiteSpace(visitedDay))
                    run.VisitedDayEncounterIds.Add(visitedDay);
            }
        }

        if (dto.ExclusionSet != null)
        {
            foreach (string excludedId in dto.ExclusionSet)
            {
                if (!string.IsNullOrWhiteSpace(excludedId))
                    run.ExclusionSet.Add(excludedId);
            }
            if (dto.ExclusionSet.Count > 0)
                run.ExclusionSetInitialized = true;
        }

        if (dto.OpponentAttributes is { Count: > 0 })
        {
            run.OpponentAttributes.Clear();
            foreach (var kv in dto.OpponentAttributes)
            {
                if (Enum.TryParse<EPlayerAttributeType>(kv.Key, true, out var oa))
                    run.OpponentAttributes[oa] = kv.Value;
            }
            if (!run.OpponentAttributes.ContainsKey(EPlayerAttributeType.Gold))
                run.OpponentAttributes[EPlayerAttributeType.Gold] = 0;
            if (!run.OpponentAttributes.TryGetValue(EPlayerAttributeType.Income, out int oppInc) || oppInc <= 0)
                run.OpponentAttributes[EPlayerAttributeType.Income] = 5;
        }

        if (dto.CurrentPvpOpponent != null)
        {
            run.CurrentPvpOpponent = new SimPvpOpponent(
                dto.CurrentPvpOpponent.Name,
                dto.CurrentPvpOpponent.TitlePrefix,
                dto.CurrentPvpOpponent.TitleSuffix,
                dto.CurrentPvpOpponent.Rank,
                dto.CurrentPvpOpponent.Rating,
                dto.CurrentPvpOpponent.Division,
                dto.CurrentPvpOpponent.Victories,
                dto.CurrentPvpOpponent.Prestige,
                dto.CurrentPvpOpponent.Level,
                dto.CurrentPvpOpponent.Hero,
                !string.IsNullOrWhiteSpace(dto.CurrentPvpOpponent.AccountId)
                    ? new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout { accountId = dto.CurrentPvpOpponent.AccountId }
                    : (dto.PendingPvpGhost != null && !string.IsNullOrWhiteSpace(dto.PendingPvpGhost.BoardId)
                        ? new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout { accountId = $"ghost_{dto.PendingPvpGhost.BoardId}" }
                        : null),
                null);
        }

        if (dto.SuspendedStateStack != null)
        {
            foreach (var sDto in dto.SuspendedStateStack)
            {
                var snap = new SuspendedStateSnapshot
                {
                    State = sDto.State,
                    CurrentEncounterId = sDto.CurrentEncounterId,
                    ParentEncounterTemplateId = sDto.ParentEncounterTemplateId,
                    ActiveEncounterCardInstanceId = sDto.ActiveEncounterCardInstanceId,
                    ActiveEncounterCard = FromCardDto(sDto.ActiveEncounterCard, repo),
                    SelectionSet = sDto.SelectionSet != null ? new List<string>(sDto.SelectionSet) : new List<string>(),
                    SelectionRules = sDto.SelectionRules != null ? FromRulesDto(sDto.SelectionRules) : new TSelectionContextRules(),
                    RerollCost = sDto.RerollCost,
                    RerollsRemaining = sDto.RerollsRemaining,
                    SelectionsMadeInCurrentContext = Math.Max(0, sDto.SelectionsMadeInCurrentContext),
                };
                if (sDto.SuspendedCards != null)
                {
                    foreach (var scDto in sDto.SuspendedCards)
                    {
                        var scEntry = FromCardDto(scDto, repo);
                        if (scEntry != null) snap.SuspendedCards.Add(scEntry);
                    }
                }
                run.SuspendedStates.Push(snap);
            }
        }

        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.HandGrid.Cards.Clear();
        run.StashGrid.UnlockedMask = 1023;
        run.StashGrid.Cards.Clear();

        var handEntries = new List<RunState.CardEntry>();
        var stashEntries = new List<RunState.CardEntry>();

        if (dto.Cards != null)
        {
            foreach (var cDto in dto.Cards)
            {
                var entry = FromCardDto(cDto, repo);
                if (entry == null) continue;

                run.Cards[entry.InstanceId] = entry;

                if (entry.Owner == ECombatantId.Player && entry.Type == ECardType.Item)
                {
                    if (entry.Section == EInventorySection.Stash)
                        stashEntries.Add(entry);
                    else
                        handEntries.Add(entry);
                }
            }
        }

        PlaceEntriesIntoGrid(handEntries, run.HandGrid, EInventorySection.Hand, defaultSlot: 3, fallbackEntries: stashEntries);
        PlaceEntriesIntoGrid(stashEntries, run.StashGrid, EInventorySection.Stash, defaultSlot: 0, fallbackEntries: null);

        // 兜底校验：若玩家为激活遭遇保留了卡片但 ActiveEncounterCardInstanceId 尚未记录，从当前遭遇或父遭遇匹配回填
        if (string.IsNullOrEmpty(run.ActiveEncounterCardInstanceId))
        {
            string? lookupId = !string.IsNullOrWhiteSpace(run.ParentEncounterTemplateId)
                ? run.ParentEncounterTemplateId
                : run.CurrentEncounterId;
            if (!string.IsNullOrEmpty(lookupId) && Guid.TryParse(lookupId, out var curEncGuid))
            {
                var matchedActive = run.Cards.Values.FirstOrDefault(c =>
                    c.Owner == null &&
                    c.TemplateId == curEncGuid &&
                    c.Type is ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter &&
                    !run.SelectionSet.Contains(c.InstanceId));
                if (matchedActive != null)
                {
                    run.ActiveEncounterCardInstanceId = matchedActive.InstanceId;
                    matchedActive.Socket ??= EContainerSocketId.Socket_5;
                }
            }
        }

        // 兼容历史快照（未持久化 ParentEncounterTemplateId）：若当前处于 Encounter 态且存在活跃父级事件/基座卡，自动回填
        if (string.IsNullOrWhiteSpace(run.ParentEncounterTemplateId) &&
            run.State == ERunState.Encounter &&
            !string.IsNullOrEmpty(run.ActiveEncounterCardInstanceId) &&
            run.Cards.TryGetValue(run.ActiveEncounterCardInstanceId, out var activeParentCard) &&
            activeParentCard.Type is ECardType.EventEncounter or ECardType.PedestalEncounter)
        {
            run.ParentEncounterTemplateId = activeParentCard.TemplateId.ToString();
        }

        return run;
    }

    private static CardSnapshotEntryDto ToCardDto(RunState.CardEntry card)
    {
        var cDto = new CardSnapshotEntryDto
        {
            InstanceId = card.InstanceId,
            TemplateId = card.TemplateId,
            Type = card.Type,
            Size = card.Size,
            Tier = card.Tier,
            Enchantment = card.Enchantment,
            Owner = card.Owner,
            Socket = card.Socket,
            Section = card.Section,
            State = card.State,
            Heroes = card.Heroes.ToList(),
            Tags = card.Tags.ToList(),
            HiddenTags = card.HiddenTags.ToList(),
        };

        foreach (var kv in card.Attributes)
            cDto.Attributes[kv.Key.ToString()] = kv.Value;

        if (card.CoreCard != null)
        {
            var core = card.CoreCard;
            cDto.HasCoreCard = true;
            cDto.StartSlot = core.StartSlot;
            cDto.CoreEnchantment = core.Enchantment;

            CopyDoubleDict(core.PermanentAdd, cDto.PermanentAdd);
            CopyDoubleDict(core.PermanentMult, cDto.PermanentMult);
            CopyDoubleDict(core.HourAdd, cDto.HourAdd);
            CopyDoubleDict(core.HourMult, cDto.HourMult);
            CopyDoubleDict(core.DayAdd, cDto.DayAdd);
            CopyDoubleDict(core.DayMult, cDto.DayMult);

            foreach (string tag in core.ExtraTags)
                cDto.ExtraTags.Add(tag);
            CopyDoubleDict(core.QuestProgress, cDto.QuestProgress);
            foreach (string q in core.CompletedQuests)
                cDto.CompletedQuests.Add(q);

            cDto.TransformReplacementTemplateId = core.TransformReplacementTemplateId;
        }

        return cDto;
    }

    private static RunState.CardEntry? FromCardDto(CardSnapshotEntryDto? cDto, GameDataRepository? repo)
    {
        if (cDto == null || string.IsNullOrWhiteSpace(cDto.InstanceId))
            return null;

        var entry = new RunState.CardEntry
        {
            InstanceId = cDto.InstanceId,
            TemplateId = cDto.TemplateId,
            Type = cDto.Type,
            Size = cDto.Size,
            Tier = cDto.Tier,
            Enchantment = cDto.Enchantment,
            Owner = cDto.Owner,
            Socket = cDto.Socket,
            Section = cDto.Section,
            State = cDto.State,
        };

        if (cDto.Heroes != null)
            foreach (var h in cDto.Heroes) entry.Heroes.Add(h);
        if (cDto.Tags != null)
            foreach (var t in cDto.Tags) entry.Tags.Add(t);
        if (cDto.HiddenTags != null)
            foreach (var ht in cDto.HiddenTags) entry.HiddenTags.Add(ht);
        if (cDto.Attributes != null)
        {
            foreach (var kv in cDto.Attributes)
            {
                if (Enum.TryParse<ECardAttributeType>(kv.Key, true, out var ca))
                    entry.Attributes[ca] = kv.Value;
            }
        }
        if (cDto.CompletedQuests != null)
        {
            foreach (string cq in cDto.CompletedQuests)
                entry.SyncedCompletedQuests.Add(cq);
        }

        if (cDto.HasCoreCard || cDto.Owner == ECombatantId.Player ||
            cDto.Type is ECardType.Item or ECardType.Skill or ECardType.SocketEffect)
        {
            // 仅还原 CoreCard 实例本身，不覆写 entry.HiddenTags，保持快照往返序列化字节级一致；
            // 下发客户端时 ToSimCard 与 SyncEntryFromCoreCardAndDiff 会按 CoreCard 动态重算精确 HiddenTags
            entry.CoreCard = RebuildCoreCard(cDto, repo);
        }

        return entry;
    }

    /// <summary>
    /// 将服务端 CardEntry（含 CoreCard 动态附魔/光环重算后的 HiddenTags 与权威槽位）转换为客户端消费的 SimUpdateCard。
    /// </summary>
    public static SimUpdateCard ToSimCard(RunState.CardEntry c, bool clearPendingForceSync = false)
    {
        ArgumentNullException.ThrowIfNull(c);

        HashSet<EHiddenTag>? hiddenTags = null;
        if (c.CoreCard != null &&
            (c.CoreCard.Template.HiddenTags.Count > 0 ||
             !string.IsNullOrEmpty(c.CoreCard.Enchantment) ||
             c.CoreCard.IsTiny ||
             c.CoreCard.ActiveAbilities.Count > 0))
        {
            var resolved = new HashSet<EHiddenTag>();
            foreach (string rawHt in c.CoreCard.GetAllHiddenTags())
            {
                if (Enum.TryParse<EHiddenTag>(rawHt, true, out var parsedHt))
                    resolved.Add(parsedHt);
            }
            if (resolved.Count > 0)
                hiddenTags = resolved;
        }
        else if (c.HiddenTags.Count > 0)
        {
            hiddenTags = new HashSet<EHiddenTag>(c.HiddenTags);
        }

        var delta = new SimUpdateCard
        {
            InstanceId = c.InstanceId,
            Enchantment = c.Enchantment,
            Size = c.Size,
            Tier = c.Tier,
            State = c.State,
            Tags = c.Tags.Count > 0 ? new HashSet<ECardTag>(c.Tags) : null,
            HiddenTags = hiddenTags,
            Heroes = c.Heroes.Count > 0 ? new HashSet<EHero>(c.Heroes) : null,
            Placement = new CardDeltaPlacement
            {
                Owner = c.Owner,
                Socket = c.Socket,
                Section = c.Section,
            },
        };
        foreach (var kv in c.Attributes)
        {
            delta.Attributes[kv.Key] = new AttributeDelta { DeltaType = EAttributeDeltaType.Update, Value = kv.Value };
        }
        if (c.PendingForceSyncAttributes.Count > 0)
        {
            foreach (var kv in c.PendingForceSyncAttributes)
            {
                delta.Attributes[kv.Key] = new AttributeDelta { DeltaType = EAttributeDeltaType.Update, Value = kv.Value };
            }
            if (clearPendingForceSync)
                c.PendingForceSyncAttributes.Clear();
        }
        return delta;
    }

    /// <summary>
    /// 推断是否已完成开局三选一技能环节，供恢复 RunEngine.StartSelectionDone 使用。
    /// </summary>
    public static bool InferStartSelectionDone(RunState run)
    {
        if (run.Day > 1 || run.Hour > 1) return true;
        if (run.State != ERunState.Encounter) return true;
        if (run.Cards.Values.Any(c => c.Owner == ECombatantId.Player)) return true;
        string starterId = ShopEngine.GetStarterEncounterId(run.Hero);
        if (!string.IsNullOrEmpty(run.CurrentEncounterId) &&
            !string.Equals(run.CurrentEncounterId, starterId, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// 生成客户端 Resume Run 所需的恢复事件流，使 StartRunAppState + GameSimHandler + BoardManager
    /// 在收到 NetMessageRunInitialized + NetMessageGameStateSync + NetMessageGameSim 后
    /// 无损还原玩家手牌、背包、技能、候选货架与当前状态。
    /// </summary>
    public static List<IGameSimEvent> BuildResumeEvents(RunState run)
    {
        var events = new List<IGameSimEvent>
        {
            new GameSimEventPlayerInitialized
            {
                CombatantId = ECombatantId.Player,
                Hero = run.Hero,
            },
            new GameSimEventPlayerInitialized
            {
                CombatantId = ECombatantId.Opponent,
                Hero = run.CurrentPvpOpponent?.Hero ?? EHero.Common,
            },
        };

        // 1. 恢复玩家已拥有的物品与技能
        foreach (var card in run.Cards.Values.Where(c => c.Owner == ECombatantId.Player))
        {
            events.Add(new GameSimEventCardSpawned(
                card.InstanceId,
                card.TemplateId.ToString(),
                card.Type,
                ECombatantId.Player,
                card.Section,
                card.Socket));

            if (card.Type == ECardType.Skill)
            {
                events.Add(new GameSimEventPlayerSkillEquipped(card.InstanceId, ECombatantId.Player));
            }
        }

        // 2. 恢复当前待选货架/遭遇/战利品/升级候选卡（优先按 SelectionSet 顺序，跳过已保留在顶栏的 ActiveEncounterCard）
        var dealtIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string selId in run.SelectionSet)
        {
            if (string.Equals(selId, run.ActiveEncounterCardInstanceId, StringComparison.Ordinal))
                continue;
            if (run.Cards.TryGetValue(selId, out var cand) && cand.Owner == null && dealtIds.Add(selId))
            {
                events.Add(new GameSimEventCardDealt(cand.InstanceId, cand.TemplateId.ToString(), cand.Type));
            }
        }
        foreach (var cand in run.Cards.Values.Where(c => c.Owner == null))
        {
            if (string.Equals(cand.InstanceId, run.ActiveEncounterCardInstanceId, StringComparison.Ordinal))
                continue;
            if (dealtIds.Add(cand.InstanceId))
            {
                events.Add(new GameSimEventCardDealt(cand.InstanceId, cand.TemplateId.ToString(), cand.Type));
            }
        }

        // 3. 恢复当前真实状态与当前遭遇（不再将 Combat/PVPCombat 盲目回退到战前 Choice）；
        //    若当前正处于多步奇遇的 EncounterStep 子步，则对外向客户端同步父级遭遇 ID（#19 保证顶栏肖像挂载）
        string? resumeEncounterId = run.CurrentEncounterId;
        if (run.State == ERunState.Encounter &&
            !string.IsNullOrWhiteSpace(run.ParentEncounterTemplateId) &&
            !string.Equals(run.CurrentEncounterId, run.ParentEncounterTemplateId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(run.ActiveEncounterCardInstanceId) &&
            run.Cards.TryGetValue(run.ActiveEncounterCardInstanceId, out var activeParent) &&
            activeParent.Type is ECardType.EventEncounter or ECardType.PedestalEncounter &&
            string.Equals(activeParent.TemplateId.ToString(), run.ParentEncounterTemplateId, StringComparison.OrdinalIgnoreCase))
        {
            resumeEncounterId = run.ParentEncounterTemplateId;
        }
        events.Add(new GameSimEventStateTransitioned(run.State, resumeEncounterId));

        return events;
    }

    private static CardInstance RebuildCoreCard(CardSnapshotEntryDto cDto, GameDataRepository? repo)
    {
        var coreTpl = repo?.GetCard(cDto.TemplateId.ToString());
        if (coreTpl == null)
        {
            coreTpl = new BazaarCore.Models.CardTemplate
            {
                Id = cDto.TemplateId.ToString(),
                Type = MapCoreType(cDto.Type),
                Size = cDto.Size.ToString(),
                StartingTier = cDto.Tier.ToString(),
            };
        }

        string? ench = cDto.Enchantment?.ToString() ?? cDto.CoreEnchantment;
        var core = new CardInstance(coreTpl, ShopEngine.ToCoreTier(cDto.Tier), ench, cDto.InstanceId);

        if (cDto.CompletedQuests is { Count: > 0 })
        {
            foreach (string q in cDto.CompletedQuests)
                core.CompletedQuests.Add(q);
            core.RebuildTierAndEnchantmentData();
        }

        CopyDoubleDict(cDto.PermanentAdd, core.PermanentAdd);
        CopyDoubleDict(cDto.PermanentMult, core.PermanentMult);
        CopyDoubleDict(cDto.HourAdd, core.HourAdd);
        CopyDoubleDict(cDto.HourMult, core.HourMult);
        CopyDoubleDict(cDto.DayAdd, core.DayAdd);
        CopyDoubleDict(cDto.DayMult, core.DayMult);

        if (cDto.ExtraTags != null)
        {
            foreach (string tag in cDto.ExtraTags)
                core.ExtraTags.Add(tag);
        }
        CopyDoubleDict(cDto.QuestProgress, core.QuestProgress);

        if (!string.IsNullOrEmpty(cDto.TransformReplacementTemplateId))
        {
            core.TransformReplacementTemplateId = cDto.TransformReplacementTemplateId;
        }

        core.StartSlot = cDto.Socket.HasValue ? (int)cDto.Socket.Value : cDto.StartSlot;
        // 还原所有永久/按日/按小时属性修饰符后重新补满有效弹药上限
        core.RefillAmmoToMax();
        return core;
    }

    private static void CopyDoubleDict(IDictionary<string, double>? src, IDictionary<string, double> dst)
    {
        if (src == null) return;
        foreach (var kv in src)
            dst[kv.Key] = kv.Value;
    }

    private static void PlaceEntriesIntoGrid(
        List<RunState.CardEntry> entries,
        BoardGrid grid,
        EInventorySection targetSection,
        int defaultSlot,
        List<RunState.CardEntry>? fallbackEntries)
    {
        var ordered = entries
            .OrderBy(e => e.Socket.HasValue ? (int)e.Socket.Value : int.MaxValue)
            .ToList();

        foreach (var entry in ordered)
        {
            if (entry.CoreCard == null) continue;
            entry.Size = entry.CoreCard.EffectiveSize switch
            {
                CardSize.Large => ECardSize.Large,
                CardSize.Medium => ECardSize.Medium,
                _ => ECardSize.Small,
            };

            int desired = entry.Socket.HasValue
                ? (int)entry.Socket.Value
                : (entry.CoreCard.StartSlot >= 0 ? entry.CoreCard.StartSlot : defaultSlot);

            bool placed = desired >= 0 && grid.TryPlaceOrShift(entry.CoreCard, desired);
            if (!placed)
            {
                int auto = grid.FindFirstAvailableSlot(entry.CoreCard);
                if (auto >= 0)
                    placed = grid.TryPlaceOrShift(entry.CoreCard, auto);
                else
                    placed = grid.TryAddAuto(entry.CoreCard);
            }

            if (!placed && fallbackEntries != null)
            {
                entry.Section = EInventorySection.Stash;
                entry.Socket = null;
                entry.CoreCard.StartSlot = -1;
                fallbackEntries.Add(entry);
            }
        }

        // 将网格权威坐标全量写回该区每一张卡，避免 TryPlaceOrShift 挤开邻卡后旧卡 Socket 失步
        foreach (var entry in ordered)
        {
            if (entry.CoreCard == null || !grid.Cards.Contains(entry.CoreCard)) continue;
            entry.Section = targetSection;
            entry.Socket = (EContainerSocketId)Math.Clamp(entry.CoreCard.StartSlot, 0, 9);
        }
    }

    private static string MapCoreType(ECardType type) => type switch
    {
        ECardType.Skill => "Skill",
        ECardType.EventEncounter => "EventEncounter",
        ECardType.EncounterStep => "EncounterStep",
        ECardType.PedestalEncounter => "PedestalEncounter",
        ECardType.CombatEncounter => "CombatEncounter",
        ECardType.PvpEncounter => "PvpEncounter",
        ECardType.SocketEffect => "SocketEffect",
        ECardType.PlayerEffect => "PlayerEffect",
        _ => "Item",
    };

    private static SelectionRulesSnapshotDto ToRulesDto(TSelectionContextRules? rules)
    {
        rules ??= new TSelectionContextRules();
        return new SelectionRulesSnapshotDto
        {
            CanSelectMultiple = rules.CanSelectMultiple,
            SelectionIsFree = rules.SelectionIsFree,
            CanExit = rules.CanExit,
            WillAutoSellOnExit = rules.WillAutoSellOnExit,
            NextEncounterOnExit = rules.NextEncounterOnExit,
            RerollRules = rules.RerollRules == null
                ? null
                : new RerollRulesSnapshotDto
                {
                    TotalAllowedRerolls = rules.RerollRules.TotalAllowedRerolls,
                    CostIncrease = rules.RerollRules.CostIncrease,
                    StartingCost = rules.RerollRules.StartingCost,
                    CostMax = rules.RerollRules.CostMax,
                },
        };
    }

    private static TSelectionContextRules FromRulesDto(SelectionRulesSnapshotDto dto)
    {
        return new TSelectionContextRules
        {
            CanSelectMultiple = dto.CanSelectMultiple,
            SelectionIsFree = dto.SelectionIsFree,
            CanExit = dto.CanExit,
            WillAutoSellOnExit = dto.WillAutoSellOnExit,
            NextEncounterOnExit = dto.NextEncounterOnExit,
            RerollRules = dto.RerollRules == null
                ? null
                : new RerollRules
                {
                    TotalAllowedRerolls = dto.RerollRules.TotalAllowedRerolls,
                    CostIncrease = dto.RerollRules.CostIncrease,
                    StartingCost = dto.RerollRules.StartingCost,
                    CostMax = dto.RerollRules.CostMax,
                },
        };
    }
}
