using System;
using System.Collections.Generic;

namespace BazaarModSdk;

/// <summary>
/// 自定义 PNG 卡图的渲染通路目标类型。
/// </summary>
public enum CardVisualTarget
{
    /// <summary>3D 物品卡面与 2D 物品预览（ItemVisualsController / CardPreviewItem）。</summary>
    ItemArt = 0,

    /// <summary>3D 技能圆牌与 2D 技能图标（SkillController / SkillIconUpdater / CardPreviewSkill）。</summary>
    SkillIcon = 1,

    /// <summary>遭遇立绘与战斗立绘（EncounterController / CardPreviewEncounter / PortraitApplyHelper）。</summary>
    EncounterPortrait = 2,

    /// <summary>遭遇卡背景底图（EncounterController / CardFrameController）。</summary>
    EncounterBackground = 3,
}

/// <summary>
/// 单条自定义卡牌/技能/遭遇的视觉覆盖绑定描述符。
/// </summary>
public sealed class CardVisualBinding
{
    /// <summary>所属 Mod ID（用于诊断和热卸载）。</summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>实例级匹配锚点（最高优先级，例如 itm_mod_a7k9x2）。</summary>
    public string? InstanceId { get; set; }

    /// <summary>自定义内容逻辑 ID（次优先级，例如 bazaar-chronicles-sample:ember_anvil）。</summary>
    public string? CustomCardId { get; set; }

    /// <summary>原版合法底座模板 GUID（兜底优先级，必须对应合法白名单底座）。</summary>
    public string? TemplateId { get; set; }

    /// <summary>渲染目标通路。</summary>
    public CardVisualTarget Target { get; set; } = CardVisualTarget.ItemArt;

    /// <summary>本地 PNG/JPG 图片绝对路径。</summary>
    public string LocalImagePath { get; set; } = string.Empty;

    /// <summary>文件内容 SHA-256 哈希（用于显存去重与引用计数缓存键）。</summary>
    public string? ContentSha256 { get; set; }

    /// <summary>替换静态 PNG 时是否默认关闭原版视差闪卡关键字 _ISPREMIUM_ON 防止边缘拉伸。</summary>
    public bool DisableParallaxPremium { get; set; } = true;

    /// <summary>同级冲突时的覆盖优先级（越大越优先）。</summary>
    public int Priority { get; set; }
}

/// <summary>
/// 视觉覆盖查询上下文（值类型避免热路径装箱）。
/// </summary>
public readonly struct CardVisualQuery
{
    /// <summary>卡牌实例 ID。</summary>
    public string? InstanceId { get; }

    /// <summary>自定义卡牌逻辑 ID。</summary>
    public string? CustomCardId { get; }

    /// <summary>原版底座模板 ID。</summary>
    public string TemplateId { get; }

    /// <summary>当前品阶（0=Bronze, 1=Silver, 2=Gold, 3=Diamond, 4=Legendary）。</summary>
    public int Tier { get; }

    /// <summary>当前附魔名（无附魔时为 null）。</summary>
    public string? Enchantment { get; }

    /// <summary>请求的渲染通路类型。</summary>
    public CardVisualTarget Target { get; }

    /// <summary>构造视觉覆盖查询结构体。</summary>
    public CardVisualQuery(
        string? instanceId,
        string? customCardId,
        string templateId,
        int tier,
        string? enchantment,
        CardVisualTarget target)
    {
        InstanceId = instanceId;
        CustomCardId = customCardId;
        TemplateId = templateId ?? string.Empty;
        Tier = tier;
        Enchantment = enchantment;
        Target = target;
    }
}

/// <summary>
/// 自定义 PNG 卡图渲染覆盖注册中心契约（统一覆盖 4 大通路并受 6 道防白块铁律保护）。
/// </summary>
public interface ICardVisualOverrideRegistry
{
    /// <summary>注册静态视觉绑定（按 InstanceId &gt; CustomCardId &gt; TemplateId 解析）。</summary>
    IDisposable RegisterVisualBinding(CardVisualBinding binding);

    /// <summary>注册动态视觉解析器（可按当前品阶、附魔等动态切换卡图）。</summary>
    IDisposable RegisterDynamicResolver(Func<CardVisualQuery, CardVisualBinding?> resolver, int priority = 0);

    /// <summary>按优先级查询匹配的自定义视觉绑定。</summary>
    bool TryResolveVisual(in CardVisualQuery query, out CardVisualBinding? binding);

    /// <summary>热更新后失效指定键（或全部）的视觉缓存并通知活动视图刷新。</summary>
    void InvalidateVisualCache(string? customIdOrTemplateId = null);

    /// <summary>返回当前已注册的静态视觉绑定总数。</summary>
    int GetRegisteredBindingCount();
}

/// <summary>
/// 正则本地化替换规则。
/// </summary>
public sealed class LocalizationRegexRule
{
    /// <summary>规则唯一 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>正则表达式模式。</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>替换模板（支持 $1、$2 分组引用）。</summary>
    public string Replacement { get; set; } = string.Empty;

    /// <summary>可选作用域（如 global / NetMessageError / EndOfMatchView）。</summary>
    public string[] Scopes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 实例级或模板级卡牌名称、描述、主被动词条与附加展示标签覆盖模型。
/// </summary>
public sealed class CardTextOverride
{
    /// <summary>所属 Mod ID。</summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>实例 ID（最高优先级）。</summary>
    public string? InstanceId { get; set; }

    /// <summary>自定义卡逻辑 ID（次优先级）。</summary>
    public string? CustomCardId { get; set; }

    /// <summary>模板 GUID（兜底优先级）。</summary>
    public string? TemplateId { get; set; }

    /// <summary>适用语言代码（默认 zh-CN）。</summary>
    public string Locale { get; set; } = "zh-CN";

    /// <summary>覆盖标题（自动保留原版附魔与 Tiny 富文本前缀）。</summary>
    public string? Title { get; set; }

    /// <summary>覆盖基础描述（支持原版及 {attr.*} 动态属性占位符）。</summary>
    public string? Description { get; set; }

    /// <summary>覆盖主动触发词条列表。</summary>
    public string[]? ActiveTooltips { get; set; }

    /// <summary>覆盖被动/光环词条列表。</summary>
    public string[]? PassiveTooltips { get; set; }

    /// <summary>按附魔名覆盖附魔词条列表。</summary>
    public Dictionary<string, string[]>? EnchantmentTooltips { get; set; }

    /// <summary>附加纯展示标签（不改动底层 Card.Tags，确保联机协议与巴扎罗盘兼容）。</summary>
    public string[]? ExtraDisplayTags { get; set; }

    /// <summary>四阶属性表（Bronze/Silver/Gold/Diamond），供局内自定义卡升级预览精准渲染下一阶数值。</summary>
    public Dictionary<string, Dictionary<string, double>>? TierAttributes { get; set; }

    /// <summary>同级冲突时的覆盖优先级。</summary>
    public int Priority { get; set; }
}

/// <summary>
/// 全链路四层本地化词典与卡牌文案覆盖注册中心契约。
/// </summary>
public interface ILocalizationOverrideRegistry
{
    /// <summary>当前激活的游戏语言代码（如 zh-CN / en-US）。</summary>
    string CurrentLocale { get; }

    /// <summary>注册 MD5 哈希精确覆盖表（对齐官方 zh-CN.bytes 与 hashOverrides）。</summary>
    IDisposable RegisterHashOverrides(string locale, IReadOnlyDictionary<string, string> hashMap, int priority = 0);

    /// <summary>注册原文精确覆盖表（支持可选上下文消歧键 contextKey）。</summary>
    IDisposable RegisterExactOverrides(
        string locale,
        IReadOnlyDictionary<string, string> exactMap,
        string? contextKey = null,
        int priority = 0);

    /// <summary>注册正则表达式覆盖规则（覆盖插值 UI 与服务端错误消息）。</summary>
    IDisposable RegisterRegexOverride(string locale, LocalizationRegexRule rule, int priority = 0);

    /// <summary>注册卡牌实例/模板级文案覆盖。</summary>
    IDisposable RegisterCardTextOverride(CardTextOverride cardOverride);

    /// <summary>按四层优先级解析文本翻译。</summary>
    bool TryResolveText(string locale, string? hash, string? rawText, string? contextKey, out string translated);

    /// <summary>按 InstanceId &gt; CustomCardId &gt; TemplateId 解析卡牌文案覆盖。</summary>
    bool TryResolveCardText(
        string locale,
        string? instanceId,
        string? customCardId,
        string? templateId,
        out CardTextOverride? cardOverride);

    /// <summary>通知宿主外挂词典或语言包已更新，触发底层缓存清理与界面热刷新。</summary>
    void NotifyLocaleReloaded(string overlayRevision = "");
}

/// <summary>
/// 实时客户端性能指标与资源开销快照。
/// </summary>
public sealed class PerformanceSnapshot
{
    /// <summary>当前平滑帧率（FPS）。</summary>
    public double Fps { get; set; }

    /// <summary>1% Low 帧率（衡量微卡顿下限）。</summary>
    public double OnePercentLowFps { get; set; }

    /// <summary>最近窗口平均帧时间（毫秒）。</summary>
    public double FrameTimeMs { get; set; }

    /// <summary>Mono 托管堆内存估算（MB）。</summary>
    public double ManagedHeapMb { get; set; }

    /// <summary>网络 /api/ping 往返延迟（毫秒，-1 表示尚未探测或离线）。</summary>
    public long PingMs { get; set; } = -1;

    /// <summary>网络延迟抖动（毫秒）。</summary>
    public long PingJitterMs { get; set; }

    /// <summary>当前并发播放或追踪的 VFX 特效数。</summary>
    public int ActiveVfxCount { get; set; }

    /// <summary>被性能限流安全跳过的累计 VFX 次数。</summary>
    public long ThrottledVfxCount { get; set; }

    /// <summary>自定义卡图缓存纹理总数。</summary>
    public int CachedCustomTextures { get; set; }

    /// <summary>自定义卡图缓存估算显存占用（MB）。</summary>
    public double CachedCustomTextureVramMb { get; set; }

    /// <summary>当前运行性能预设档位名称。</summary>
    public string ActiveProfile { get; set; } = "Balanced";

    /// <summary>当前场景名称。</summary>
    public string SceneName { get; set; } = string.Empty;

    /// <summary>是否处于后台失焦降频状态。</summary>
    public bool IsBackgroundCapped { get; set; }
}

/// <summary>
/// 实时性能遥测与模块健康诊断读取契约。
/// </summary>
public interface IPerformanceTelemetry
{
    /// <summary>当前平滑 FPS。</summary>
    double CurrentFps { get; }

    /// <summary>当前 1% Low FPS。</summary>
    double OnePercentLowFps { get; }

    /// <summary>当前平均帧时间（ms）。</summary>
    double FrameTimeMs { get; }

    /// <summary>当前 Mono 托管堆占用（MB）。</summary>
    double ManagedHeapMb { get; }

    /// <summary>当前服务器 /api/ping 延迟（ms，-1 为未知）。</summary>
    long CurrentPingMs { get; }

    /// <summary>当前活跃 VFX 计数。</summary>
    int ActiveVfxCount { get; }

    /// <summary>获取完整实时性能指标快照。</summary>
    PerformanceSnapshot GetLatestSnapshot();

    /// <summary>获取全部内置模块与外部 Mod 的健康度与熔断快照列表。</summary>
    IReadOnlyList<ModHealthSnapshot> GetModuleHealthSnapshots();
}
