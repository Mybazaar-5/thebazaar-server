using System;
using System.Collections.Generic;

namespace BazaarModSdk;

/// <summary>
/// SDK 版本常量，供宿主与第三方 Mod 校验契约兼容性。
/// </summary>
public static class BazaarModSdkVersion
{
    /// <summary>当前 SDK 语义化版本号。</summary>
    public const string Current = "1.0.0";
}

/// <summary>
/// 标注在实现 <see cref="IBazaarClientMod"/> 的第三方扩展类上，用于声明 Mod 元数据。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ModMetadataAttribute : Attribute
{
    /// <summary>全局唯一 Mod 标识符（建议采用反向域名或小写连字符风格，例如 community.dragon-forge）。</summary>
    public string Id { get; }

    /// <summary>面向玩家的中文/显示名称。</summary>
    public string Name { get; }

    /// <summary>语义化版本号（例如 1.0.0）。</summary>
    public string Version { get; }

    /// <summary>作者名称。</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>Mod 简短说明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>要求的最低 SDK 版本号。</summary>
    public string MinSdkVersion { get; set; } = BazaarModSdkVersion.Current;

    /// <summary>依赖的其他 Mod ID 列表。</summary>
    public string[] Dependencies { get; set; } = Array.Empty<string>();

    /// <summary>是否支持在游戏运行期间热启停或热重载。</summary>
    public bool SupportsHotReload { get; set; } = true;

    /// <summary>初始化 Mod 元数据特性。</summary>
    public ModMetadataAttribute(string id, string name, string version = "1.0.0")
    {
        Id = id ?? string.Empty;
        Name = name ?? string.Empty;
        Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version;
    }

    /// <summary>转换为运行时只读清单对象。</summary>
    public ModManifest ToManifest(string? rootDirectory = null)
    {
        return new ModManifest
        {
            Id = Id,
            Name = Name,
            Version = Version,
            Author = Author,
            Description = Description,
            MinSdkVersion = MinSdkVersion,
            Dependencies = Dependencies ?? Array.Empty<string>(),
            SupportsHotReload = SupportsHotReload,
            RootDirectory = rootDirectory ?? string.Empty,
        };
    }
}

/// <summary>
/// Mod 运行时元数据清单（可源自 <see cref="ModMetadataAttribute"/> 或 manifest.json）。
/// </summary>
public sealed class ModManifest
{
    /// <summary>全局唯一 Mod 标识符。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>面向玩家的显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>语义化版本号。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>作者名称。</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>功能简介。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>最低兼容 SDK 版本。</summary>
    public string MinSdkVersion { get; set; } = BazaarModSdkVersion.Current;

    /// <summary>前置依赖 Mod ID 列表。</summary>
    public string[] Dependencies { get; set; } = Array.Empty<string>();

    /// <summary>是否支持运行期热启停/热重载。</summary>
    public bool SupportsHotReload { get; set; } = true;

    /// <summary>是否属于宿主内置核心模块。</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>是否默认启用。</summary>
    public bool DefaultEnabled { get; set; } = true;

    /// <summary>所属分组标签（如 Core / Perf / Compat / Gameplay / Diagnostics / External）。</summary>
    public string Category { get; set; } = "External";

    /// <summary>Mod 所在本地根目录绝对路径（内置模块可为空）。</summary>
    public string RootDirectory { get; set; } = string.Empty;
}

/// <summary>
/// 模块/Mod 的运行时生命周期与熔断状态。
/// </summary>
public enum ModLifecycleState
{
    /// <summary>尚未加载。</summary>
    Unloaded = 0,

    /// <summary>已加载但未激活。</summary>
    Loaded = 1,

    /// <summary>正常运行中。</summary>
    Enabled = 2,

    /// <summary>已被用户或配置停用。</summary>
    Disabled = 3,

    /// <summary>部分补丁或依赖预检未通过，降级安全运行中。</summary>
    Degraded = 4,

    /// <summary>短时间异常超限已被沙箱自动熔断隔离。</summary>
    Quarantined = 5,
}

/// <summary>
/// 单个内置模块或外部第三方 Mod 的健康度与诊断快照。
/// </summary>
public sealed class ModHealthSnapshot
{
    /// <summary>模块/Mod 唯一标识。</summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>版本号。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>所属分组。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>是否为内置模块。</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>当前生命周期状态。</summary>
    public ModLifecycleState State { get; set; } = ModLifecycleState.Unloaded;

    /// <summary>当前配置是否开启。</summary>
    public bool Enabled { get; set; }

    /// <summary>是否支持在游戏内动态热启停。</summary>
    public bool SupportsHotReload { get; set; }

    /// <summary>初始化加载耗时（毫秒）。</summary>
    public double LoadDurationMs { get; set; }

    /// <summary>尝试安装的 Harmony 补丁总数。</summary>
    public int PatchAttempted { get; set; }

    /// <summary>安装成功的 Harmony 补丁数。</summary>
    public int PatchSucceeded { get; set; }

    /// <summary>因签名变化被安全降级跳过的补丁数。</summary>
    public int PatchDegraded { get; set; }

    /// <summary>累计被沙箱捕获的运行期异常次数。</summary>
    public int FaultCount { get; set; }

    /// <summary>最近一次错误信息。</summary>
    public string LastError { get; set; } = string.Empty;

    /// <summary>状态最近更新时间（UTC ISO-8601）。</summary>
    public string LastUpdatedUtc { get; set; } = string.Empty;

    /// <summary>降级或诊断明细条目列表。</summary>
    public IReadOnlyList<string> DiagnosticNotes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 所有内置功能模块与外部第三方 C# 客户端 Mod 统一实现的入口契约。
/// </summary>
public interface IBazaarClientMod
{
    /// <summary>声明当前 Mod 的元数据清单。</summary>
    ModManifest Manifest { get; }

    /// <summary>
    /// 宿主初始化加载阶段调用，用于保存上下文、注册配置、事件、UI 与覆盖表。
    /// 严禁在此方法内同步阻塞 Unity 主线程。
    /// </summary>
    void OnLoad(IModContext context);

    /// <summary>
    /// 启用回调：激活补丁、原生 UI 入口、视觉/文本覆盖与事件订阅。
    /// </summary>
    void OnEnable();

    /// <summary>
    /// 停用回调：暂停功能并回收临时 UI，但保留持久化配置。
    /// </summary>
    void OnDisable();

    /// <summary>
    /// 卸载或热重载前清理所有自持资源（通过 <see cref="IModContext"/> 追踪的资源亦会被宿主自动回收）。
    /// </summary>
    void OnUnload();
}
