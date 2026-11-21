using System;
using System.Collections.Generic;
using System.Threading;

namespace BazaarModSdk;

/// <summary>
/// 面向单个 Mod 隔离分发的日志接口。
/// </summary>
public interface IModLogger
{
    /// <summary>输出调试级日志。</summary>
    void LogDebug(string message);

    /// <summary>输出常规信息日志。</summary>
    void LogInfo(string message);

    /// <summary>输出警告日志。</summary>
    void LogWarning(string message);

    /// <summary>输出错误日志（可选附带异常对象）。</summary>
    void LogError(string message, Exception? exception = null);
}

/// <summary>
/// 面向单个 Mod 隔离命名空间的持久化键值配置接口。
/// </summary>
public interface IModConfig
{
    /// <summary>读取布尔配置项，缺失时返回默认值。</summary>
    bool GetBool(string key, bool defaultValue = false);

    /// <summary>写入布尔配置项。</summary>
    void SetBool(string key, bool value);

    /// <summary>读取整数配置项，缺失时返回默认值。</summary>
    int GetInt(string key, int defaultValue = 0);

    /// <summary>写入整数配置项。</summary>
    void SetInt(string key, int value);

    /// <summary>读取单精度浮点配置项，缺失时返回默认值。</summary>
    float GetFloat(string key, float defaultValue = 0f);

    /// <summary>写入单精度浮点配置项。</summary>
    void SetFloat(string key, float value);

    /// <summary>读取字符串配置项，缺失时返回默认值。</summary>
    string GetString(string key, string defaultValue = "");

    /// <summary>写入字符串配置项。</summary>
    void SetString(string key, string value);

    /// <summary>获取当前命名空间下已记录的全部配置键。</summary>
    IReadOnlyCollection<string> GetAllKeys();

    /// <summary>将配置原子落盘持久化。</summary>
    void Save();

    /// <summary>从磁盘重新加载配置。</summary>
    void Reload();
}

/// <summary>
/// 提供给每个内置模块或第三方 Mod 的隔离宿主上下文。
/// 通过该上下文注册的所有订阅、UI 入口、卡图与本地化覆盖句柄均会被宿主自动追踪并在卸载/熔断时释放。
/// </summary>
public interface IModContext
{
    /// <summary>当前 Mod 元数据清单。</summary>
    ModManifest Manifest { get; }

    /// <summary>带 Mod ID 前缀的隔离日志器。</summary>
    IModLogger Logger { get; }

    /// <summary>当前 Mod 独立命名空间的持久化配置存储。</summary>
    IModConfig Config { get; }

    /// <summary>强类型零装箱客户端事件总线。</summary>
    IModEventBus EventBus { get; }

    /// <summary>原生游戏 UI 深度嵌入宿主。</summary>
    INativeUiHost UiHost { get; }

    /// <summary>自定义 PNG 卡图视觉覆盖注册中心。</summary>
    ICardVisualOverrideRegistry VisualOverrides { get; }

    /// <summary>四层多语言与自定义卡牌文本覆盖注册中心。</summary>
    ILocalizationOverrideRegistry Localization { get; }

    /// <summary>实时客户端性能与模块健康遥测接口。</summary>
    IPerformanceTelemetry Telemetry { get; }

    /// <summary>当前自建服基址（已去除末尾斜杠）。</summary>
    string ServerBaseUrl { get; }

    /// <summary>客户端 Mod 资产本地缓存目录（BepInEx/config/BazaarModCache）。</summary>
    string CacheDirectory { get; }

    /// <summary>客户端卸载或该 Mod 停用时触发取消的令牌。</summary>
    CancellationToken ShutdownToken { get; }

    /// <summary>将委托安全投递至 Unity 主线程异步执行（绝不死锁）。</summary>
    void RunOnMainThread(Action action);

    /// <summary>将自定义可释放资源纳入当前 Mod 的生命周期托管表，在禁用/卸载/熔断时自动释放。</summary>
    void TrackDisposable(IDisposable disposable);
}
