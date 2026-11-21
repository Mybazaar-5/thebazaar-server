using System;

namespace BazaarModSdk;

/// <summary>
/// 强类型、零装箱、异常自动隔离的客户端事件总线。
/// </summary>
public interface IModEventBus
{
    /// <summary>
    /// 订阅指定值类型事件；优先级数值越大越先执行。返回句柄释放后自动退订。
    /// </summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler, int priority = 0) where TEvent : struct;

    /// <summary>
    /// 发布值类型事件；单个订阅者抛出的异常会被沙箱捕获，绝不影响其他订阅者或调用方。
    /// </summary>
    void Publish<TEvent>(in TEvent evt) where TEvent : struct;
}

/// <summary>Unity 活动场景切换事件。</summary>
public readonly struct SceneChangedModEvent
{
    /// <summary>切换前的场景名。</summary>
    public string PreviousSceneName { get; }

    /// <summary>切换后的场景名。</summary>
    public string SceneName { get; }

    /// <summary>是否为游戏主大厅场景。</summary>
    public bool IsMainMenu { get; }

    /// <summary>构造场景切换事件。</summary>
    public SceneChangedModEvent(string previousSceneName, string sceneName, bool isMainMenu)
    {
        PreviousSceneName = previousSceneName ?? string.Empty;
        SceneName = sceneName ?? string.Empty;
        IsMainMenu = isMainMenu;
    }
}

/// <summary>对局应用状态机变化事件。</summary>
public readonly struct RunStateChangedModEvent
{
    /// <summary>上一个状态名称。</summary>
    public string PreviousState { get; }

    /// <summary>当前状态名称。</summary>
    public string CurrentState { get; }

    /// <summary>当前对局 ID（不在局内时为空）。</summary>
    public string RunId { get; }

    /// <summary>构造对局状态变化事件。</summary>
    public RunStateChangedModEvent(string previousState, string currentState, string runId = "")
    {
        PreviousState = previousState ?? string.Empty;
        CurrentState = currentState ?? string.Empty;
        RunId = runId ?? string.Empty;
    }
}

/// <summary>战斗播放生命周期事件。</summary>
public readonly struct CombatLifecycleModEvent
{
    /// <summary>true 表示战斗开始，false 表示战斗或回放结束。</summary>
    public bool Started { get; }

    /// <summary>是否为玩家 PvP 对战。</summary>
    public bool IsPvp { get; }

    /// <summary>是否为历史战报回放。</summary>
    public bool IsReplay { get; }

    /// <summary>构造战斗生命周期事件。</summary>
    public CombatLifecycleModEvent(bool started, bool isPvp, bool isReplay = false)
    {
        Started = started;
        IsPvp = isPvp;
        IsReplay = isReplay;
    }
}

/// <summary>玩法模式、预设或自定义规则发生变更事件（用于事件驱动刷新 UI）。</summary>
public readonly struct ActiveRulesUpdatedModEvent
{
    /// <summary>递增版本号。</summary>
    public int RulesVersion { get; }

    /// <summary>当前模式 ID（如 Standard / EndlessTower / SandboxLab）。</summary>
    public string ModeId { get; }

    /// <summary>当前预设 ID（如 PureOfficial / EnhancedFun / ExtremeRoguelike）。</summary>
    public string PresetId { get; }

    /// <summary>是否为锦标赛上下文。</summary>
    public bool IsTournament { get; }

    /// <summary>构造规则更新事件。</summary>
    public ActiveRulesUpdatedModEvent(int rulesVersion, string modeId, string presetId, bool isTournament = false)
    {
        RulesVersion = rulesVersion;
        ModeId = modeId ?? "Standard";
        PresetId = presetId ?? "PureOfficial";
        IsTournament = isTournament;
    }
}

/// <summary>游戏语言切换或外挂汉化词典热刷新事件。</summary>
public readonly struct LocaleSwitchedModEvent
{
    /// <summary>新激活语言代码（例如 zh-CN / en-US）。</summary>
    public string NewLocale { get; }

    /// <summary>词典修订号。</summary>
    public string OverlayRevision { get; }

    /// <summary>构造语言切换事件。</summary>
    public LocaleSwitchedModEvent(string newLocale, string overlayRevision = "")
    {
        NewLocale = newLocale ?? "zh-CN";
        OverlayRevision = overlayRevision ?? string.Empty;
    }
}

/// <summary>服务端 Mod 清单、自定义视觉绑定或 PNG 资产缓存完成同步事件。</summary>
public readonly struct ModAssetsSyncedModEvent
{
    /// <summary>已启用 Mod 数量。</summary>
    public int EnabledModCount { get; }

    /// <summary>已同步视觉绑定条数。</summary>
    public int VisualBindingCount { get; }

    /// <summary>本次增量下载的 PNG 资产文件数。</summary>
    public int DownloadedAssetCount { get; }

    /// <summary>服务端 Mod 集合哈希。</summary>
    public string ModSetHash { get; }

    /// <summary>构造 Mod 同步完成事件。</summary>
    public ModAssetsSyncedModEvent(int enabledModCount, int visualBindingCount, int downloadedAssetCount, string modSetHash)
    {
        EnabledModCount = enabledModCount;
        VisualBindingCount = visualBindingCount;
        DownloadedAssetCount = downloadedAssetCount;
        ModSetHash = modSetHash ?? string.Empty;
    }
}

/// <summary>收到房间或全服社交聊天/战况播报消息事件。</summary>
public readonly struct SocialChatReceivedModEvent
{
    /// <summary>消息递增 ID。</summary>
    public long MessageId { get; }

    /// <summary>频道类型（lobby / spectate / global / system）。</summary>
    public string ChannelType { get; }

    /// <summary>发送者显示名。</summary>
    public string SenderName { get; }

    /// <summary>已清洗的安全文本内容。</summary>
    public string Content { get; }

    /// <summary>构造社交消息事件。</summary>
    public SocialChatReceivedModEvent(long messageId, string channelType, string senderName, string content)
    {
        MessageId = messageId;
        ChannelType = channelType ?? "global";
        SenderName = senderName ?? string.Empty;
        Content = content ?? string.Empty;
    }
}
