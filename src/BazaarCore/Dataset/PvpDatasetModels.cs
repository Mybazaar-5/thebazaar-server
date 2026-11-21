using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Models;

namespace BazaarCore.Dataset;

/// <summary>
/// PvP 数据集统一的 JSON 序列化配置提供类。
/// </summary>
public static class PvpDatasetJson
{
    /// <summary>
    /// 统一静态只读 JSON 序列化选项：CamelCase 命名、枚举字符串化、忽略 null 字段，并使用宽松编码保证中文不转义为 \uXXXX。
    /// </summary>
    public static readonly JsonSerializerOptions PvpJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>
/// 模拟玩家的决策行为画像类型。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PersonaType
{
    /// <summary>保血快攻流：前中期积极补强战力保连胜。</summary>
    TempoAggro,

    /// <summary>贪经济大后期：前期偏重存钱与收益，后期追高阶核心。</summary>
    GreedyEco,

    /// <summary>强追流派赌狗：执着于刷新凑齐预设流派核心组件。</summary>
    HardForcer,

    /// <summary>灵活转型高玩：根据关键掉落与商店高阶卡动态切换主轴。</summary>
    FlexPivot,

    /// <summary>随机随缘新手：站位与构筑偏向次优，丰富中低分段样本分布。</summary>
    Casual,

    /// <summary>萌新唐人：随机进店、跳过背包优化、乱摆站位且经常漏二合一。</summary>
    CluelessNewbie,

    /// <summary>四不像杂拼：烧毒盾武杂烩乱塞不舍得卖低阶旧牌、缺乏协同。</summary>
    MessyHybrid,

    /// <summary>贪心崩盘哥：前期盲目裸奔存钱与放经济牌挨打，高概率 Day 7~10 崩盘淘汰。</summary>
    GreedyBust
}

/// <summary>
/// 同天数交叉对抗定级后的盘面强度分档。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PowerBucket
{
    /// <summary>弱势盘面（预估胜率 0–25%）。</summary>
    Weak,

    /// <summary>常规盘面（预估胜率 25–60%）。</summary>
    Normal,

    /// <summary>强势盘面（预估胜率 60–88%）。</summary>
    Strong,

    /// <summary>版本霸主盘面（预估胜率 88–100%）。</summary>
    MetaBoss
}

/// <summary>
/// 每日晚间 PvP 开战瞬间的玩家完整盘面状态快照。
/// </summary>
public sealed class PvpBoardSnapshot
{
    /// <summary>全局唯一盘面快照 ID。</summary>
    public string BoardId { get; set; } = string.Empty;

    /// <summary>所属完整对局 Run 的唯一 ID。</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>游戏数据版本号。</summary>
    public string Version { get; set; } = "5.0.0";

    /// <summary>数据来源标识（如 synthetic_selfplay 或 real_community）。</summary>
    public string Source { get; set; } = "synthetic_selfplay";

    /// <summary>生成该对局使用的玩家画像。</summary>
    public PersonaType Persona { get; set; }

    /// <summary>盘面所属流派标识。</summary>
    public string Archetype { get; set; } = string.Empty;

    /// <summary>当前天数（1..N）。</summary>
    public int Day { get; set; }

    /// <summary>使用英雄内部名称。</summary>
    public string Hero { get; set; } = string.Empty;

    /// <summary>玩家当前等级。</summary>
    public int Level { get; set; }

    /// <summary>玩家最大生命值上限。</summary>
    public int HealthMax { get; set; }

    /// <summary>玩家当前声望值。</summary>
    public int Prestige { get; set; }

    /// <summary>当前累计胜场数。</summary>
    public int Wins { get; set; }

    /// <summary>当前累计败场数。</summary>
    public int Losses { get; set; }

    /// <summary>10 格棋盘的已解锁格子位掩码。</summary>
    public int UnlockedSlotsMask { get; set; }

    /// <summary>盘面强度分档。</summary>
    public PowerBucket PowerBucket { get; set; } = PowerBucket.Normal;

    /// <summary>同天数交叉验证实测预估胜率（0.0 ~ 1.0）。</summary>
    public double EstimatedWinRate { get; set; }

    /// <summary>场上物品列表。</summary>
    public List<PvpItemSnapshot> Items { get; set; } = new();

    /// <summary>已装备技能列表。</summary>
    public List<PvpSkillSnapshot> Skills { get; set; } = new();

    /// <summary>当晚 PvP 对战结算结果（未进行匹配或外部待定则可为空）。</summary>
    public PvpMatchOutcome? MatchOutcome { get; set; }

    /// <summary>用于去重与相似性检索的内部盘面指纹（不参与 JSON 序列化）。</summary>
    [JsonIgnore]
    public string? BoardSignature { get; set; }
}

/// <summary>
/// 棋盘上单件物品的静态与永久成长快照。
/// </summary>
public sealed class PvpItemSnapshot
{
    /// <summary>物品起始槽位下标（0..9）。</summary>
    public int Slot { get; set; }

    /// <summary>物品占用格数（1=Small, 2=Medium, 3=Large）。</summary>
    public int Width { get; set; }

    /// <summary>卡牌模板 GUID。</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>卡牌内部英文名。</summary>
    public string InternalName { get; set; } = string.Empty;

    /// <summary>卡牌官方简体中文名称。</summary>
    public string ZhName { get; set; } = string.Empty;

    /// <summary>卡牌品质等级。</summary>
    public CardTier Tier { get; set; }

    /// <summary>附魔类型名称（无附魔时为 null）。</summary>
    public string? Enchantment { get; set; }

    /// <summary>
    /// 局内永久成长后的属性覆写表。
    /// 契约：PermanentAttributes 仅记录发生过局内永久成长变化的属性，值为成长后的永久最终基础值，
    /// 还原为 CardInstance 时按 PermanentAdd[k] = v - BaseAttributes.GetValueOrDefault(k, 0) 精确重建。
    /// </summary>
    public Dictionary<string, double> PermanentAttributes { get; set; } = new();
}

/// <summary>
/// 玩家已持有技能的快照。
/// </summary>
public sealed class PvpSkillSnapshot
{
    /// <summary>技能模板 GUID。</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>技能内部英文名。</summary>
    public string InternalName { get; set; } = string.Empty;

    /// <summary>技能官方简体中文名称。</summary>
    public string ZhName { get; set; } = string.Empty;

    /// <summary>技能品质等级。</summary>
    public CardTier Tier { get; set; }
}

/// <summary>
/// 单晚 PvP 战斗对阵结果。
/// </summary>
public sealed class PvpMatchOutcome
{
    /// <summary>对手盘面快照 ID。</summary>
    public string OpponentBoardId { get; set; } = string.Empty;

    /// <summary>本方是否获胜。</summary>
    public bool Won { get; set; }

    /// <summary>战斗持续毫秒数。</summary>
    public int CombatDurationMs { get; set; }

    /// <summary>战斗结束时本方剩余生命值。</summary>
    public int RemainingHp { get; set; }

    /// <summary>本场战斗中本方造成总伤害（含灼烧/剧毒等直伤来源）。</summary>
    public double DamageDealt { get; set; }

    /// <summary>本场战斗中本方累计获得护盾量。</summary>
    public double ShieldGained { get; set; }
}

/// <summary>
/// 单局完整 Run 从 Day 1 到终局的轨迹记录。
/// </summary>
public sealed class PvpRunRecord
{
    /// <summary>完整对局 Run 的唯一 ID。</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>游戏数据版本号。</summary>
    public string Version { get; set; } = "5.0.0";

    /// <summary>数据来源标识。</summary>
    public string Source { get; set; } = "synthetic_selfplay";

    /// <summary>使用英雄内部名称。</summary>
    public string Hero { get; set; } = string.Empty;

    /// <summary>玩家决策画像。</summary>
    public PersonaType Persona { get; set; }

    /// <summary>最终成型或主轴流派标识。</summary>
    public string Archetype { get; set; } = string.Empty;

    /// <summary>终局所在天数。</summary>
    public int FinalDay { get; set; }

    /// <summary>终局总胜场数。</summary>
    public int FinalWins { get; set; }

    /// <summary>终局总败场数。</summary>
    public int FinalLosses { get; set; }

    /// <summary>是否达成十胜通关。</summary>
    public bool IsVictory { get; set; }

    /// <summary>逐日 PvP 盘面快照列表。</summary>
    public List<PvpBoardSnapshot> DayBoards { get; set; } = new();
}

/// <summary>
/// 盘面合法性校验结果。
/// </summary>
public sealed class BoardValidationResult
{
    /// <summary>盘面是否完全合法。</summary>
    public bool IsValid { get; set; }

    /// <summary>不合法时的错误明细列表。</summary>
    public List<string> Errors { get; set; } = new();
}
