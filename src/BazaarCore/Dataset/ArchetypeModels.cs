namespace BazaarCore.Dataset;

/// <summary>
/// 棋盘物品摆位策略规则。
/// </summary>
public enum PositioningRule
{
    /// <summary>无特殊摆位偏好。</summary>
    None,

    /// <summary>核心物品尽量置于棋盘中央位置。</summary>
    CoreCenter,

    /// <summary>辅助或充能件紧贴核心物品相邻摆放。</summary>
    AdjacentToCore,

    /// <summary>关键触发或光环件尽量置于最左侧。</summary>
    LeftMost,

    /// <summary>关键终结或收益件尽量置于最右侧。</summary>
    RightMost,

    /// <summary>短冷却快速触发件放在成长/倍增件左侧。</summary>
    FastTriggerLeftOfScaler,

    /// <summary>护盾防御类组件放置于边缘槽位。</summary>
    ShieldOnEdge
}

/// <summary>
/// 单套流派构筑蓝图定义，描述英雄流派的核心组件、标签权重、推荐附魔与摆位规范。
/// </summary>
public sealed class ArchetypeDefinition
{
    /// <summary>流派唯一标识 ID（如 Vanessa_Weapon_Crit_Ammo）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属英雄内部名称。</summary>
    public string Hero { get; init; } = string.Empty;

    /// <summary>流派简体中文名称。</summary>
    public string NameZh { get; init; } = string.Empty;

    /// <summary>流派核心思路简体中文描述。</summary>
    public string DescriptionZh { get; init; } = string.Empty;

    /// <summary>核心基石物品的 InternalName 列表。</summary>
    public IReadOnlyList<string> CoreItemNames { get; init; } = Array.Empty<string>();

    /// <summary>强力配套辅助物品的 InternalName 列表。</summary>
    public IReadOnlyList<string> SupportItemNames { get; init; } = Array.Empty<string>();

    /// <summary>核心联动技能的 InternalName 列表。</summary>
    public IReadOnlyList<string> CoreSkillNames { get; init; } = Array.Empty<string>();

    /// <summary>流派偏好的显式 Tag 及其评分权重。</summary>
    public IReadOnlyDictionary<string, double> PreferredTags { get; init; } = new Dictionary<string, double>();

    /// <summary>流派偏好的 HiddenTag 及其评分权重。</summary>
    public IReadOnlyDictionary<string, double> PreferredHiddenTags { get; init; } = new Dictionary<string, double>();

    /// <summary>流派偏好的触发器类型及其评分权重。</summary>
    public IReadOnlyDictionary<string, double> PreferredTriggers { get; init; } = new Dictionary<string, double>();

    /// <summary>流派偏好的附魔类型及其评分权重。</summary>
    public IReadOnlyDictionary<string, double> PreferredEnchantments { get; init; } = new Dictionary<string, double>();

    /// <summary>棋盘站位约束规则列表。</summary>
    public IReadOnlyList<PositioningRule> PositioningRules { get; init; } = Array.Empty<PositioningRule>();

    /// <summary>前期过渡重点关注的标签列表。</summary>
    public IReadOnlyList<string> EarlyFocusTags { get; init; } = Array.Empty<string>();

    /// <summary>大后期决胜核心机制标签。</summary>
    public string EndgamePowerTag { get; init; } = string.Empty;
}

/// <summary>
/// 流派蓝图目录接口，提供按英雄与画像检索和抽样流派的能力。
/// </summary>
public interface IArchetypeCatalog
{
    /// <summary>全部已注册流派蓝图列表。</summary>
    IReadOnlyList<ArchetypeDefinition> All { get; }

    /// <summary>获取指定英雄的全部流派蓝图。</summary>
    IReadOnlyList<ArchetypeDefinition> GetByHero(string hero);

    /// <summary>按流派 ID 精确查找蓝图。</summary>
    ArchetypeDefinition? GetById(string id);

    /// <summary>根据英雄与玩家决策画像随机抽取一套流派蓝图。</summary>
    ArchetypeDefinition PickForHero(string hero, PersonaType persona, Random rng);
}
