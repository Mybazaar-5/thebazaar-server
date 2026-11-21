using BazaarCore.Data;
using BazaarCore.Runtime;

namespace BazaarCore.Dataset;

/// <summary>
/// PvP 盘面合法性校验与运行时状态还原接口。
/// </summary>
public interface IBoardValidator
{
    /// <summary>
    /// 校验盘面快照是否合法（槽位无重叠/越界、卡槽掩码一致、模板存在、官方中文完备等）。
    /// </summary>
    BoardValidationResult Validate(PvpBoardSnapshot snapshot, GameDataRepository repo, bool requireOfficialZh = true);

    /// <summary>
    /// 将盘面快照精确还原为可用于 CombatSimulator 的 PlayerState 实例。
    /// </summary>
    PlayerState ToPlayerState(PvpBoardSnapshot snapshot, GameDataRepository repo);

    /// <summary>
    /// 计算盘面的去重指纹签名（基于核心阵容、品质与附魔结构）。
    /// </summary>
    string ComputeSignature(PvpBoardSnapshot snapshot);
}

/// <summary>
/// 高速无头局内发育模拟器的运行参数配置。
/// </summary>
public sealed class FastRunOptions
{
    /// <summary>单局最大允许推进天数。</summary>
    public int MaxDay { get; set; } = 17;

    /// <summary>达成胜利所需的 PvP 胜场数。</summary>
    public int VictoriesToWin { get; set; } = 10;

    /// <summary>达成十胜后是否继续进行无尽模式。</summary>
    public bool AllowEndlessAfterVictory { get; set; } = false;

    /// <summary>是否严格要求仅选取具备官方简体中文名称的卡牌。</summary>
    public bool RequireOfficialZhCards { get; set; } = true;
}

/// <summary>
/// 单局 Run 参与者在逐日推进过程中的持久化上下文状态。
/// </summary>
public sealed class RunParticipantState
{
    /// <summary>全局唯一 Run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>使用英雄内部名称。</summary>
    public required string Hero { get; init; }

    /// <summary>玩家决策画像。</summary>
    public PersonaType Persona { get; set; }

    /// <summary>当前锚定或转型后的流派蓝图。</summary>
    public required ArchetypeDefinition Archetype { get; set; }

    /// <summary>底层局内与战斗运行时玩家状态。</summary>
    public required PlayerState PlayerState { get; init; }

    /// <summary>该局专属确定性随机数生成器。</summary>
    public required Random Rng { get; init; }

    /// <summary>该局是否已结束（十胜通关、声望归零淘汰或达到最大天数）。</summary>
    public bool IsFinished { get; set; }

    /// <summary>该局最终是否胜利。</summary>
    public bool IsVictory { get; set; }

    /// <summary>已生成的逐日 PvP 盘面快照列表。</summary>
    public List<PvpBoardSnapshot> Snapshots { get; } = new();
}

/// <summary>
/// 高速无头局内发育模拟器接口，驱动单局从 Day 1 逐日演化至 PvP 结算。
/// </summary>
public interface IFastRunSimulator
{
    /// <summary>初始化一个指定英雄、画像、流派与随机种子的 Run 参与者状态。</summary>
    RunParticipantState CreateParticipant(string runId, string hero, PersonaType persona, ArchetypeDefinition archetype, int seed, GameDataRepository repo);

    /// <summary>推进指定天数当天的白天事件/商店/升级/野怪阶段，产出当晚待出战的 PvP 盘面快照。</summary>
    PvpBoardSnapshot AdvanceOneDayToPvp(RunParticipantState participant, int day, GameDataRepository repo, IArchetypeCatalog catalog, FastRunOptions? options = null);

    /// <summary>根据当晚 PvP 战斗结果更新胜负场次、声望、淘汰或通关状态并回填快照。</summary>
    void ApplyPvpOutcome(RunParticipantState participant, PvpBoardSnapshot todaySnapshot, PvpMatchOutcome outcome, GameDataRepository repo, FastRunOptions? options = null);

    /// <summary>将已完成的参与者状态汇总构建为完整 PvP Run 记录。</summary>
    PvpRunRecord BuildRunRecord(RunParticipantState participant, GameDataRepository repo);
}
