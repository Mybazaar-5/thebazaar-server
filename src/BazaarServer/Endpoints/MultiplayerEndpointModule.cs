using BazaarServer.Tournament;
using Microsoft.Extensions.DependencyInjection;

namespace BazaarServer.Endpoints;

/// <summary>
/// Wave 3A 多人联机、观战、社交聊天、排行榜与沙盒扩展模块装配实现：
/// 通过分部类（partial）自动接入 MultiplayerContracts.cs 的 RegisterServices / MapEndpoints 入口，
/// 将真实实现注册至 DI 容器以覆盖 Null 占位服务，并与 Wave 3B 保持 100% 零文件冲突。
/// </summary>
public static partial class MultiplayerEndpointModule
{
    static partial void RegisterServicesCore(IServiceCollection services)
    {
        services.AddSingleton<SseEventHub>();

        services.AddSingleton<SpectateFrameStore>();
        services.AddSingleton<ISpectateFrameRecorder>(sp => sp.GetRequiredService<SpectateFrameStore>());

        services.AddSingleton<TournamentPvpCoordinator>();
        services.AddSingleton<ITournamentPvpCoordinator>(sp => sp.GetRequiredService<TournamentPvpCoordinator>());

        services.AddSingleton<SocialAndReplayService>();
        services.AddSingleton<ISocialAndReplaySink>(sp => sp.GetRequiredService<SocialAndReplayService>());

        services.AddHostedService<LobbyBarrierWatchdog>();
    }

    static partial void MapEndpointsCore(WebApplication app)
    {
        // 将运行期 SSE 中枢与社交播报写入器绑定到 LobbyStore
        var lobbies = app.Services.GetService<LobbyStore>();
        var sseHub = app.Services.GetService<SseEventHub>();
        var socialSink = app.Services.GetService<ISocialAndReplaySink>();
        lobbies?.AttachRuntimeHooks(sseHub, socialSink);

        SpectateEndpoints.Map(app);
        SocialEndpoints.Map(app);
        LeaderboardEndpoints.Map(app);
        SandboxEndpoints.Map(app);
    }
}
