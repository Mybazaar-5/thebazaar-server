using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BazaarServer.Endpoints;

/// <summary>
/// 1. 观战帧录制器：Wave 3A 实现，Wave 3B 在 GameServerEndpoints /commands 与 /sessions 中调用。
/// </summary>
public interface ISpectateFrameRecorder
{
    void RecordBootstrapSnapshot(string accountId, string runId, Guid? tournamentSessionId, long requestId, byte[] msgPackPayload);
    void RecordCommandDelta(string accountId, string runId, Guid? tournamentSessionId, long fromRequestId, long requestId, byte[] msgPackPayload, bool isCombatSequence);
    void ClearForAccount(string accountId);
}

/// <summary>
/// 真人同步 PvP 当日锁定盘面快照。
/// </summary>
public sealed record LockedPvpBoard(
    string AccountId,
    string Username,
    string Hero,
    uint Day,
    int HealthMax,
    int Prestige,
    int Level,
    uint Victories,
    int Gold,
    int Income,
    string RunSnapshotJson);

/// <summary>
/// 真人同步 PvP 对称裁决结果（保证 A 胜 &lt;=&gt; B 负且双方使用同一种子与盘面）。
/// </summary>
public sealed record SymmetricHumanPvpOutcome(
    string WinnerAccountId,
    string LoserAccountId,
    int BattleSeed,
    int LoserPrestigeDamage,
    string ReplayId,
    byte[]? CachedCombatSimForP1,
    byte[]? CachedCombatSimForP2);

/// <summary>
/// 2. 真人同步 PvP 唯一裁决协调器：Wave 3A 实现（落盘 tournament_matchups + 内存缓存），
///    Wave 3B 在 RunEngine 战斗结算中调用，彻底消除双胜双负与跨天脏盘面。
/// </summary>
public interface ITournamentPvpCoordinator
{
    void LockPlayerBoardForDay(Guid tournamentSessionId, uint day, LockedPvpBoard board);
    LockedPvpBoard? TryGetLockedBoard(Guid tournamentSessionId, uint day, string accountId);
    SymmetricHumanPvpOutcome? TryGetResolvedMatch(Guid tournamentSessionId, uint day, string accountA, string accountB);
    SymmetricHumanPvpOutcome RecordOrGetResolvedMatch(Guid tournamentSessionId, uint day, string accountA, string accountB, SymmetricHumanPvpOutcome proposed);
}

/// <summary>
/// 战斗回放持久化记录。
/// </summary>
public sealed record CombatReplayRecord(
    string ReplayId,
    string? RunId,
    string? LobbyId,
    string CombatType,
    uint Day,
    string PlayerAccountId,
    string PlayerUsername,
    string PlayerHero,
    string OpponentName,
    string OpponentHero,
    bool PlayerWon,
    int DurationMs,
    int Seed,
    string SummaryJson,
    byte[]? CombatSimMsgPack = null);

/// <summary>
/// 特殊模式 / 每日挑战 / 无尽爬塔 / Boss 车轮战排行榜提交记录。
/// </summary>
public sealed record RunLeaderboardSubmission(
    string ModeKey,
    string SeasonOrDateKey,
    string AccountId,
    string Username,
    string Hero,
    int Score,
    int DayReached,
    int Victories,
    int Defeats,
    double ScoreMultiplier,
    string? MutatorsJson = null,
    string? FinalBoardSummaryJson = null);

/// <summary>
/// 3. 战报回放、排行榜与系统播报统一写入器：Wave 3A 实现，Wave 3B 在战斗结算与终局时调用。
/// </summary>
public interface ISocialAndReplaySink
{
    string SaveCombatReplay(CombatReplayRecord record);
    void RecordRunLeaderboard(RunLeaderboardSubmission submission);
    void PublishSystemBroadcast(string? lobbyId, string msgType, string contentZh, object? meta = null);
}

/// <summary>
/// 沙盒模式操作请求与结果 DTO。
/// </summary>
public sealed record SandboxGrantCardRequest(
    string TemplateId,
    string Section = "Hand",
    string Tier = "Bronze",
    string? Enchantment = null,
    int? PreferredSlot = null);

public sealed record SandboxModifyCardRequest(
    string InstanceId,
    string? Tier = null,
    string? Enchantment = null,
    Dictionary<string, int>? AttributeDeltas = null,
    bool RemoveCard = false);

public sealed record SandboxSetStatsRequest(
    int? Gold = null,
    int? Income = null,
    int? HealthMax = null,
    int? Prestige = null,
    int? Level = null,
    int? Experience = null,
    int? Day = null,
    int? Hour = null,
    bool? UnlockAllSlots = null);

public sealed record SandboxTestCombatRequest(
    string OpponentSource = "self_mirror", // "self_mirror" | "monster" | "ghost"
    string? MonsterTemplateId = null,
    int? GhostDay = null,
    int? Seed = null);

public sealed record SandboxPresetBoardRequest(
    string PresetName,
    List<SandboxGrantCardRequest>? Cards = null,
    SandboxSetStatsRequest? Stats = null);

public sealed record SandboxOpResult(
    bool Ok,
    string MessageZh,
    object? Data = null);

public sealed record SandboxCombatTestResult(
    bool Ok,
    string MessageZh,
    bool PlayerWon = false,
    int DurationMs = 0,
    int PlayerRemainingHp = 0,
    int OpponentRemainingHp = 0,
    string? ReplayId = null,
    object? Summary = null);

/// <summary>
/// 4. 沙盒引擎桥接接口：Wave 3B 在 Game/SandboxEngineService.cs 中实现并注册 DI，
///    Wave 3A 的 SandboxEndpoints.cs 仅负责鉴权/校验/调用此接口，不直接触碰 RunEngine 内部细节。
/// </summary>
public interface ISandboxEngineBridge
{
    Task<SandboxOpResult> GrantCardAsync(string accountId, SandboxGrantCardRequest req, CancellationToken ct);
    Task<SandboxOpResult> ModifyCardAsync(string accountId, SandboxModifyCardRequest req, CancellationToken ct);
    Task<SandboxOpResult> SetStatsAsync(string accountId, SandboxSetStatsRequest req, CancellationToken ct);
    Task<SandboxCombatTestResult> RunTestCombatAsync(string accountId, SandboxTestCombatRequest req, CancellationToken ct);
    Task<SandboxOpResult> ApplyPresetBoardAsync(string accountId, SandboxPresetBoardRequest req, CancellationToken ct);
}

/// <summary>
/// 5. Wave 3A 多人扩展模块统一装配入口（通过 partial method 实现 Wave 3A/3B 并行编译零冲突）。
/// </summary>
public static partial class MultiplayerEndpointModule
{
    public static void RegisterServices(IServiceCollection services)
    {
        RegisterServicesCore(services);
        services.TryAddSingleton<ISpectateFrameRecorder, NullSpectateFrameRecorder>();
        services.TryAddSingleton<ITournamentPvpCoordinator, NullTournamentPvpCoordinator>();
        services.TryAddSingleton<ISocialAndReplaySink, NullSocialAndReplaySink>();
        services.TryAddSingleton<ISandboxEngineBridge, NullSandboxEngineBridge>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        MapEndpointsCore(app);
    }

    static partial void RegisterServicesCore(IServiceCollection services);
    static partial void MapEndpointsCore(WebApplication app);
}

internal sealed class NullSpectateFrameRecorder : ISpectateFrameRecorder
{
    public void RecordBootstrapSnapshot(string accountId, string runId, Guid? tournamentSessionId, long requestId, byte[] msgPackPayload) { }
    public void RecordCommandDelta(string accountId, string runId, Guid? tournamentSessionId, long fromRequestId, long requestId, byte[] msgPackPayload, bool isCombatSequence) { }
    public void ClearForAccount(string accountId) { }
}

internal sealed class NullTournamentPvpCoordinator : ITournamentPvpCoordinator
{
    public void LockPlayerBoardForDay(Guid tournamentSessionId, uint day, LockedPvpBoard board) { }
    public LockedPvpBoard? TryGetLockedBoard(Guid tournamentSessionId, uint day, string accountId) => null;
    public SymmetricHumanPvpOutcome? TryGetResolvedMatch(Guid tournamentSessionId, uint day, string accountA, string accountB) => null;
    public SymmetricHumanPvpOutcome RecordOrGetResolvedMatch(Guid tournamentSessionId, uint day, string accountA, string accountB, SymmetricHumanPvpOutcome proposed) => proposed;
}

internal sealed class NullSocialAndReplaySink : ISocialAndReplaySink
{
    public string SaveCombatReplay(CombatReplayRecord record) => record.ReplayId;
    public void RecordRunLeaderboard(RunLeaderboardSubmission submission) { }
    public void PublishSystemBroadcast(string? lobbyId, string msgType, string contentZh, object? meta = null) { }
}

internal sealed class NullSandboxEngineBridge : ISandboxEngineBridge
{
    public Task<SandboxOpResult> GrantCardAsync(string accountId, SandboxGrantCardRequest req, CancellationToken ct)
        => Task.FromResult(new SandboxOpResult(false, "沙盒引擎尚未注册"));
    public Task<SandboxOpResult> ModifyCardAsync(string accountId, SandboxModifyCardRequest req, CancellationToken ct)
        => Task.FromResult(new SandboxOpResult(false, "沙盒引擎尚未注册"));
    public Task<SandboxOpResult> SetStatsAsync(string accountId, SandboxSetStatsRequest req, CancellationToken ct)
        => Task.FromResult(new SandboxOpResult(false, "沙盒引擎尚未注册"));
    public Task<SandboxCombatTestResult> RunTestCombatAsync(string accountId, SandboxTestCombatRequest req, CancellationToken ct)
        => Task.FromResult(new SandboxCombatTestResult(false, "沙盒引擎尚未注册"));
    public Task<SandboxOpResult> ApplyPresetBoardAsync(string accountId, SandboxPresetBoardRequest req, CancellationToken ct)
        => Task.FromResult(new SandboxOpResult(false, "沙盒引擎尚未注册"));
}
