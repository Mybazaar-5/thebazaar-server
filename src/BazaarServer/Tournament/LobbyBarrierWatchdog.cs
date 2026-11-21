using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;

namespace BazaarServer.Tournament;

/// <summary>
/// 锦标赛 1 秒后台屏障看门狗、掉线 AI 托管调度器与存储空间定期治理服务：
/// - 每 1 秒：驱动掉线状态机（online -> disconnected -> ai_takeover）、自动解锁超时 Hour 6 屏障并推送房间级 SSE；
/// - 每 60 秒：归档并驱逐过期/终局超 30 分钟的旧房间，释放内存与对称裁决缓存；
/// - 每 6 小时：触发 SQLite 存储空间治理（裁剪过期令牌、超过 30 天/5 万条旧回放二进制与超限聊天记录）。
/// </summary>
public sealed class LobbyBarrierWatchdog : BackgroundService
{
    private readonly LobbyStore _lobbies;
    private readonly SessionStore _sessions;
    private readonly Persistence _db;
    private readonly ITournamentPvpCoordinator _pvpCoordinator;
    private readonly ILogger<LobbyBarrierWatchdog> _log;

    private DateTime _lastEvictUtc = DateTime.UtcNow;
    private DateTime _lastMaintenanceUtc = DateTime.UtcNow;

    public LobbyBarrierWatchdog(
        LobbyStore lobbies,
        SessionStore sessions,
        Persistence db,
        ITournamentPvpCoordinator pvpCoordinator,
        ILogger<LobbyBarrierWatchdog> log)
    {
        _lobbies = lobbies;
        _sessions = sessions;
        _db = db;
        _pvpCoordinator = pvpCoordinator;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    _lobbies.RunWatchdogTick(_sessions, _pvpCoordinator);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "锦标赛屏障看门狗巡检异常");
                }

                DateTime now = DateTime.UtcNow;
                if (now - _lastEvictUtc >= TimeSpan.FromSeconds(60))
                {
                    _lastEvictUtc = now;
                    try
                    {
                        int evicted = _lobbies.EvictExpiredAndFinishedLobbies(_pvpCoordinator as TournamentPvpCoordinator);
                        if (evicted > 0)
                            _log.LogInformation("已归档并释放 {Count} 个过期或已结束的锦标赛房间", evicted);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "过期锦标赛房间清理异常");
                    }
                }

                if (now - _lastMaintenanceUtc >= TimeSpan.FromHours(6))
                {
                    _lastMaintenanceUtc = now;
                    try
                    {
                        var report = _db.RunStorageMaintenance(now);
                        if (report.DeletedExpiredTokens + report.TrimmedReplayBlobs + report.DeletedOldChatMessages + report.PurgedArchivedLobbies > 0)
                        {
                            _log.LogInformation(
                                "SQLite 存储空间治理完成：清理过期令牌 {Tokens} 条、裁剪旧回放二进制 {Blobs} 条、裁剪旧聊天 {Chat} 条、归档历史大厅 {Lobbies} 个",
                                report.DeletedExpiredTokens, report.TrimmedReplayBlobs, report.DeletedOldChatMessages, report.PurgedArchivedLobbies);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "SQLite 存储空间治理执行异常");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机退出
        }
    }
}
