using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Engine;
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
        public string? Username { get; set; }
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
        public uint? BaseRerollCost { get; set; }
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
        public int? CurrentMaxSelections { get; set; }
        public Dictionary<string, int> ActiveShelfDiscounts { get; set; } = new();
        public Dictionary<string, double>? TakeoverChanceBonuses { get; set; }
        public bool PendingClearExpeditionAfterLoot { get; set; }
        public int RngSeed { get; set; }
        public int BossRushWavesCleared { get; set; }
        public bool EndlessTenWinCelebrated { get; set; }
        public int DailyRerollSequence { get; set; }

        public Dictionary<string, int> PlayerAttributes { get; set; } = new();
        public Dictionary<string, double> PlayerHourAdd { get; set; } = new();
        public Dictionary<string, double> PlayerHourMult { get; set; } = new();
        public Dictionary<string, double> PlayerDayAdd { get; set; } = new();
        public Dictionary<string, double> PlayerDayMult { get; set; } = new();
        public Dictionary<string, double> PlayerAuraAdd { get; set; } = new();
        public Dictionary<string, double> PlayerAuraMult { get; set; } = new();
        public List<string> VisitedSingleSpawnEncounterIds { get; set; } = new();
        public List<string> VisitedDayEncounterIds { get; set; } = new();
        public List<string> OfferedDayEncounterIds { get; set; } = new();
        public uint OfferedDayEncounterDay { get; set; }
        public uint OfferedDayEncounterLastHour { get; set; }
        public string? ActiveReplacementSetId { get; set; }
        public string? ActiveExpeditionId { get; set; }
        public string? ActiveExpeditionVariant { get; set; }
        public List<string> ExpeditionVisitedRoomIds { get; set; } = new();
        public string? CurrentBoardId { get; set; }
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
        public uint? BaseRerollCost { get; set; }
        public uint RerollsRemaining { get; set; }
        public int SelectionsMadeInCurrentContext { get; set; }
        public int? CurrentMaxSelections { get; set; }
        public Dictionary<string, int> ActiveShelfDiscounts { get; set; } = new();
        public int PendingEncounterExitXp { get; set; }
        public List<CardSnapshotEntryDto> SuspendedCards { get; set; } = new();
    }

    public sealed class PvpOpponentLoadoutSnapshotDto
    {
        public string? AccountId { get; set; }
        public string? BoardId { get; set; }
        public string? CarpetId { get; set; }
        public string? CardBackId { get; set; }
        public string? ToyId { get; set; }
        public string? StashId { get; set; }
        public string? BankId { get; set; }
        public string? AlbumId { get; set; }
        public List<string>? HeroSkinIds { get; set; }
        public List<string>? CardSkinIds { get; set; }
    }

    public sealed class PvpOpponentCollectionItemDto
    {
        public Guid Id { get; set; }
        public Guid ItemId { get; set; }
        public int? MintNumber { get; set; }
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
        public PvpOpponentLoadoutSnapshotDto? PlayerLoadout { get; set; }
        public List<PvpOpponentCollectionItemDto>? PlayerCollection { get; set; }
    }

    public sealed class CardSnapshotEntryDto
    {
        public string InstanceId { get; set; } = "";
        public Guid TemplateId { get; set; }
        public string? CustomCardId { get; set; }
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

        // CardInstance 持久态与临时附魔/品阶分层字段（P1-30）
        public bool HasCoreCard { get; set; }
        public int StartSlot { get; set; } = -1;
        public string? CoreEnchantment { get; set; }
        public ETier? PermanentTier { get; set; }
        public bool HasEnchantmentLayers { get; set; }
        public string? PermanentEnchantment { get; set; }
        public string? DayEnchantment { get; set; }
        public string? HourEnchantment { get; set; }
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
            Username = string.IsNullOrEmpty(run.Username) ? null : run.Username,
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
            BaseRerollCost = run.BaseRerollCost,
            RerollsRemaining = run.RerollsRemaining,
            SelectionSet = new List<string>(run.SelectionSet),
            SelectionRules = ToRulesDto(run.SelectionRules),
            UnlockedSlots = run.UnlockedSlots,
            StartSelectionDone = startSelectionDone ?? (run.StartSelectionDone || run.Day > 1 || run.Hour > 1),
            PendingLevelUp = effectivePendingLevelUps > 0,
            PendingLevelUps = effectivePendingLevelUps,
            PendingEncounterExitXp = Math.Max(0, run.PendingEncounterExitXp),
            GainedHourXpThisHour = run.GainedHourXpThisHour,
            GainedAdvanceHourXpThisHour = run.GainedAdvanceHourXpThisHour,
            SelectionsMadeInCurrentContext = run.SelectionsMadeInCurrentContext,
            CurrentMaxSelections = run.CurrentMaxSelections,
            PendingClearExpeditionAfterLoot = run.PendingClearExpeditionAfterLoot,
            RngSeed = run.RngSeed,
            BossRushWavesCleared = run.BossRushWavesCleared,
            EndlessTenWinCelebrated = run.EndlessTenWinCelebrated,
            DailyRerollSequence = run.DailyRerollSequence,
            PendingPvpGhost = run.PendingPvpGhost,
            ActiveReplacementSetId = run.ActiveReplacementSetId,
            ActiveExpeditionId = run.ActiveExpeditionId,
            ActiveExpeditionVariant = run.ActiveExpeditionVariant,
            CurrentBoardId = run.CurrentBoardId,
        };

        foreach (var kv in run.ActiveShelfDiscounts)
            dto.ActiveShelfDiscounts[kv.Key] = kv.Value;
        if (run.TakeoverChanceBonuses.Count > 0)
        {
            dto.TakeoverChanceBonuses = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in run.TakeoverChanceBonuses)
                dto.TakeoverChanceBonuses[kv.Key] = kv.Value;
        }

        foreach (string expRoomId in run.ExpeditionVisitedRoomIds)
            dto.ExpeditionVisitedRoomIds.Add(expRoomId);

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

        foreach (string offeredDay in run.OfferedDayEncounterIds)
            dto.OfferedDayEncounterIds.Add(offeredDay);
        dto.OfferedDayEncounterDay = run.OfferedDayEncounterDay;
        dto.OfferedDayEncounterLastHour = run.OfferedDayEncounterLastHour;

        dto.ExclusionSetInitialized = run.ExclusionSetInitialized;
        foreach (string excludedId in run.ExclusionSet)
            dto.ExclusionSet.Add(excludedId);

        foreach (var kv in run.OpponentAttributes)
            dto.OpponentAttributes[kv.Key.ToString()] = kv.Value;

        if (run.CurrentPvpOpponent != null)
        {
            var opp = run.CurrentPvpOpponent;
            PvpOpponentLoadoutSnapshotDto? loadoutDto = null;
            if (opp.PlayerLoadout != null)
            {
                loadoutDto = new PvpOpponentLoadoutSnapshotDto
                {
                    AccountId = opp.PlayerLoadout.accountId,
                    BoardId = opp.PlayerLoadout.boardId,
                    CarpetId = opp.PlayerLoadout.carpetId,
                    CardBackId = opp.PlayerLoadout.cardBackId,
                    ToyId = opp.PlayerLoadout.toyId,
                    StashId = opp.PlayerLoadout.stashId,
                    BankId = opp.PlayerLoadout.bankId,
                    AlbumId = opp.PlayerLoadout.albumId,
                    HeroSkinIds = opp.PlayerLoadout.heroSkinIds != null ? new List<string>(opp.PlayerLoadout.heroSkinIds) : null,
                    CardSkinIds = opp.PlayerLoadout.cardSkinIds != null ? new List<string>(opp.PlayerLoadout.cardSkinIds) : null,
                };
            }
            List<PvpOpponentCollectionItemDto>? collectionDto = null;
            if (opp.PlayerCollection is { Count: > 0 })
            {
                collectionDto = opp.PlayerCollection
                    .Select(ci => new PvpOpponentCollectionItemDto
                    {
                        Id = ci.Id,
                        ItemId = ci.ItemId,
                        MintNumber = ci.MintNumber,
                    })
                    .ToList();
            }

            dto.CurrentPvpOpponent = new PvpOpponentSnapshotDto
            {
                Name = opp.Name,
                TitlePrefix = opp.TitlePrefix,
                TitleSuffix = opp.TitleSuffix,
                Rank = opp.Rank,
                Rating = opp.Rating,
                Division = opp.Division,
                Victories = opp.Victories,
                Prestige = opp.Prestige,
                Level = opp.Level,
                Hero = opp.Hero,
                AccountId = opp.PlayerLoadout?.accountId,
                PlayerLoadout = loadoutDto,
                PlayerCollection = collectionDto,
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
                    BaseRerollCost = s.BaseRerollCost,
                    RerollsRemaining = s.RerollsRemaining,
                    SelectionsMadeInCurrentContext = s.SelectionsMadeInCurrentContext,
                    CurrentMaxSelections = s.CurrentMaxSelections,
                    PendingEncounterExitXp = Math.Max(0, s.PendingEncounterExitXp),
                };
                foreach (var kv in s.ActiveShelfDiscounts)
                    sDto.ActiveShelfDiscounts[kv.Key] = kv.Value;
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
            Username = dto.Username ?? string.Empty,
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
            BaseRerollCost = dto.BaseRerollCost,
            RerollsRemaining = dto.RerollsRemaining,
            UnlockedSlots = dto.UnlockedSlots == 0 ? (ushort)0b00_0111_1000 : dto.UnlockedSlots,
            PendingLevelUps = restoredPendingLevelUps,
            PendingEncounterExitXp = Math.Max(0, dto.PendingEncounterExitXp),
            GainedHourXpThisHour = dto.GainedHourXpThisHour,
            GainedAdvanceHourXpThisHour = dto.GainedAdvanceHourXpThisHour,
            SelectionsMadeInCurrentContext = Math.Max(0, dto.SelectionsMadeInCurrentContext),
            CurrentMaxSelections = dto.CurrentMaxSelections.HasValue ? Math.Max(1, dto.CurrentMaxSelections.Value) : null,
            StartSelectionDone = dto.StartSelectionDone,
            PendingClearExpeditionAfterLoot = dto.PendingClearExpeditionAfterLoot,
            RngSeed = dto.RngSeed,
            BossRushWavesCleared = Math.Max(0, dto.BossRushWavesCleared),
            EndlessTenWinCelebrated = dto.EndlessTenWinCelebrated,
            DailyRerollSequence = Math.Max(0, dto.DailyRerollSequence),
            PendingPvpGhost = dto.PendingPvpGhost,
            ActiveReplacementSetId = dto.ActiveReplacementSetId
                ?? ShopEngine.TryResolveReplacementSetIdForExpedition(dto.ActiveExpeditionId, dto.ActiveExpeditionVariant),
            ActiveExpeditionId = dto.ActiveExpeditionId,
            ActiveExpeditionVariant = dto.ActiveExpeditionVariant,
            CurrentBoardId = dto.CurrentBoardId,
        };

        if (dto.ActiveShelfDiscounts != null)
        {
            foreach (var kv in dto.ActiveShelfDiscounts)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    run.ActiveShelfDiscounts[kv.Key] = Math.Max(0, kv.Value);
            }
        }

        if (dto.TakeoverChanceBonuses != null)
        {
            foreach (var kv in dto.TakeoverChanceBonuses)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && double.IsFinite(kv.Value) && kv.Value > 0)
                    run.TakeoverChanceBonuses[kv.Key] = Math.Min(1.0, kv.Value);
            }
        }

        if (dto.ExpeditionVisitedRoomIds != null)
        {
            foreach (string expRoomId in dto.ExpeditionVisitedRoomIds)
            {
                if (!string.IsNullOrWhiteSpace(expRoomId))
                    run.ExpeditionVisitedRoomIds.Add(expRoomId);
            }
        }

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

        if (dto.OfferedDayEncounterIds != null)
        {
            foreach (string offeredDay in dto.OfferedDayEncounterIds)
            {
                if (!string.IsNullOrWhiteSpace(offeredDay))
                    run.OfferedDayEncounterIds.Add(offeredDay);
            }
        }
        run.OfferedDayEncounterDay = dto.OfferedDayEncounterDay;
        run.OfferedDayEncounterLastHour = dto.OfferedDayEncounterLastHour;

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
            var oppDto = dto.CurrentPvpOpponent;
            BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout? loadout = null;
            if (oppDto.PlayerLoadout != null)
            {
                loadout = new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout
                {
                    accountId = oppDto.PlayerLoadout.AccountId ?? oppDto.AccountId,
                    boardId = oppDto.PlayerLoadout.BoardId,
                    carpetId = oppDto.PlayerLoadout.CarpetId,
                    cardBackId = oppDto.PlayerLoadout.CardBackId,
                    toyId = oppDto.PlayerLoadout.ToyId,
                    stashId = oppDto.PlayerLoadout.StashId,
                    bankId = oppDto.PlayerLoadout.BankId,
                    albumId = oppDto.PlayerLoadout.AlbumId,
                    heroSkinIds = oppDto.PlayerLoadout.HeroSkinIds?.ToArray(),
                    cardSkinIds = oppDto.PlayerLoadout.CardSkinIds?.ToArray(),
                };
            }
            else if (!string.IsNullOrWhiteSpace(oppDto.AccountId))
            {
                loadout = new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout { accountId = oppDto.AccountId };
            }
            else if (dto.PendingPvpGhost != null && !string.IsNullOrWhiteSpace(dto.PendingPvpGhost.BoardId))
            {
                loadout = new BazaarGameShared.TempoNet.Models.BazaarCollectionLoadout { accountId = $"ghost_{dto.PendingPvpGhost.BoardId}" };
            }

            List<BazaarGameShared.TempoNet.Responses.CollectionItemInstanceResponse>? collection = null;
            if (oppDto.PlayerCollection is { Count: > 0 })
            {
                collection = oppDto.PlayerCollection
                    .Select(ci => new BazaarGameShared.TempoNet.Responses.CollectionItemInstanceResponse
                    {
                        Id = ci.Id,
                        ItemId = ci.ItemId,
                        MintNumber = ci.MintNumber,
                    })
                    .ToList();
            }

            run.CurrentPvpOpponent = new SimPvpOpponent(
                oppDto.Name,
                oppDto.TitlePrefix,
                oppDto.TitleSuffix,
                oppDto.Rank,
                oppDto.Rating,
                oppDto.Division,
                oppDto.Victories,
                oppDto.Prestige,
                oppDto.Level,
                oppDto.Hero,
                loadout,
                collection);
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
                    BaseRerollCost = sDto.BaseRerollCost,
                    RerollsRemaining = sDto.RerollsRemaining,
                    SelectionsMadeInCurrentContext = Math.Max(0, sDto.SelectionsMadeInCurrentContext),
                    CurrentMaxSelections = sDto.CurrentMaxSelections.HasValue ? Math.Max(1, sDto.CurrentMaxSelections.Value) : null,
                    PendingEncounterExitXp = Math.Max(0, sDto.PendingEncounterExitXp),
                };
                if (sDto.ActiveShelfDiscounts != null)
                {
                    foreach (var kv in sDto.ActiveShelfDiscounts)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Key))
                            snap.ActiveShelfDiscounts[kv.Key] = Math.Max(0, kv.Value);
                    }
                }
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
            CustomCardId = card.CustomCardId,
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
            // P1-30：显式持久化永久品质与永久/当日/本小时三层附魔，防止重连后临时附魔永久转正
            cDto.PermanentTier = RunState.MapCoreTier(core.PermanentTier);
            cDto.HasEnchantmentLayers = true;
            cDto.PermanentEnchantment = core.PermanentEnchantment;
            cDto.DayEnchantment = core.DayEnchantment;
            cDto.HourEnchantment = core.HourEnchantment;

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
            CustomCardId = cDto.CustomCardId,
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
    /// 当 <paramref name="omitPlacement"/> 为 true 时（本帧存在对该现存卡的 GameSimEventCardMoved），
    /// 将 Placement 置 null，避免 Data.UpdateFromGameSimAsync 提前覆写坐标导致观战态吞掉 ProcessCardMovedEvents 移位动画。
    /// </summary>
    public static SimUpdateCard ToSimCard(
        RunState.CardEntry c,
        bool clearPendingForceSync = false,
        bool omitPlacement = false)
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
            Placement = omitPlacement
                ? null
                : new CardDeltaPlacement
                {
                    Owner = c.Owner,
                    Socket = c.Socket,
                    Section = c.Section,
                },
        };
        // P1-11：若上次已下发的属性键集合中有键已从当前 Attributes 移除，发射 EAttributeDeltaType.Delete 消除客户端 0 值幽灵宝石/价签
        if (c.LastEmittedAttributeKeys != null)
        {
            foreach (var prevKey in c.LastEmittedAttributeKeys)
            {
                if (!c.Attributes.ContainsKey(prevKey) && !c.PendingForceSyncAttributes.ContainsKey(prevKey))
                {
                    delta.Attributes[prevKey] = new AttributeDelta { DeltaType = EAttributeDeltaType.Delete, Value = 0 };
                }
            }
        }
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
        if (clearPendingForceSync)
        {
            var emittedNow = new HashSet<ECardAttributeType>(c.Attributes.Keys);
            foreach (var kv in delta.Attributes)
            {
                if (kv.Value.DeltaType == EAttributeDeltaType.Update)
                    emittedNow.Add(kv.Key);
            }
            c.LastEmittedAttributeKeys = emittedNow;
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

        // V15.3 问题 6：必须在 PlayerInitialized 之后、CardSpawned 之前显式下发玩家当前全部已解锁槽位掩码，
        // 防止客户端重连时 Hand 容器仍保持初始窄槽掩码导致左右外侧槽位物品被 PlaceOnSocket 拒放而凭空消失！
        var unlockedSockets = new HashSet<EContainerSocketId>();
        for (int s = 0; s < 10; s++)
        {
            if ((run.UnlockedSlots & (1 << s)) != 0)
                unlockedSockets.Add((EContainerSocketId)s);
        }
        if (unlockedSockets.Count > 0)
        {
            events.Add(new GameSimEventSocketsUnlocked(unlockedSockets, run.UnlockedSlots, EInventorySection.Hand));
        }

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

        // 2. P2-35：仅下发 SelectionSet 内的合法无主候选卡（跳过顶栏 ActiveEncounterCard），并启动清理不在 SelectionSet 中的孤儿无主卡，
        //    防止客户端 GameStateHandler 将残留孤儿脏卡错误渲染到货架上变成不可点击的幽灵卡。
        var activeSelectionSet = new HashSet<string>(run.SelectionSet, StringComparer.Ordinal);
        var orphanOwnerlessIds = run.Cards.Values
            .Where(c => c.Owner == null &&
                        !string.Equals(c.InstanceId, run.ActiveEncounterCardInstanceId, StringComparison.Ordinal) &&
                        !activeSelectionSet.Contains(c.InstanceId))
            .Select(c => c.InstanceId)
            .ToList();
        foreach (string orphanId in orphanOwnerlessIds)
        {
            run.Cards.Remove(orphanId);
        }

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

        // 3. 若处于远征/特殊遭遇切板态，同包下发 GameSimEventBoardOverridden（客户端 GameSimHandler 要求与 StateTransitioned 同包方触发 TransitionToBoard）
        if (!string.IsNullOrWhiteSpace(run.CurrentBoardId))
        {
            events.Add(new GameSimEventBoardOverridden(run.CurrentBoardId, null));
        }

        // 4. 恢复当前真实状态与当前遭遇（P1-28：统一复用 RunState.ResolveWireCurrentEncounterId，防止空串/子步/非 GUID Mod 遭遇崩溃）
        string? resumeEncounterId = run.ResolveWireCurrentEncounterId();
        events.Add(new GameSimEventStateTransitioned(run.State, resumeEncounterId));

        // 5. P1-35：若断线恢复结算中断战斗直接进入终局，补发终局结算事件使客户端弹出 RunCompleted 结算面板
        if (run.PendingResumeRunCompletedEvent != null)
        {
            events.Add(run.PendingResumeRunCompletedEvent);
            run.PendingResumeRunCompletedEvent = null;
        }

        return events;
    }

    private static CardInstance RebuildCoreCard(CardSnapshotEntryDto cDto, GameDataRepository? repo)
    {
        var coreTpl = (!string.IsNullOrWhiteSpace(cDto.CustomCardId) ? repo?.GetCard(cDto.CustomCardId) : null)
                   ?? repo?.GetCard(cDto.TemplateId.ToString());
        if (coreTpl == null)
        {
            coreTpl = new BazaarCore.Models.CardTemplate
            {
                Id = !string.IsNullOrWhiteSpace(cDto.CustomCardId) ? cDto.CustomCardId! : cDto.TemplateId.ToString(),
                Type = MapCoreType(cDto.Type),
                Size = cDto.Size.ToString(),
                StartingTier = cDto.Tier.ToString(),
            };
        }

        // P1-30：按永久品阶与持久/当日/本小时三层附魔无损重建 CoreCard（向后兼容未含分层字段的旧快照）
        bool hasLayers = cDto.HasEnchantmentLayers || cDto.DayEnchantment != null || cDto.HourEnchantment != null;
        CardTier permTier = cDto.PermanentTier.HasValue
            ? ShopEngine.ToCoreTier(cDto.PermanentTier.Value)
            : ShopEngine.ToCoreTier(cDto.Tier);
        string? permEnch = hasLayers
            ? cDto.PermanentEnchantment
            : (cDto.Enchantment?.ToString() ?? cDto.CoreEnchantment);

        var core = new CardInstance(coreTpl, permTier, permEnch, cDto.InstanceId);

        if (ShopEngine.ToCoreTier(cDto.Tier) != core.Tier)
            core.SetTier(ShopEngine.ToCoreTier(cDto.Tier), isTemporary: true);

        if (!string.IsNullOrWhiteSpace(cDto.DayEnchantment))
            core.ApplyEnchantment(cDto.DayEnchantment, preventOverride: false, DurationTarget.Day);
        if (!string.IsNullOrWhiteSpace(cDto.HourEnchantment))
            core.ApplyEnchantment(cDto.HourEnchantment, preventOverride: false, DurationTarget.Hour);

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
