using System.Reflection;
using System.Text.Json.Serialization;
using BazaarCore.Engine;
using BazaarCore.Models;

namespace BazaarCore.Mods;

/// <summary>
/// 权威规则积木中文目录（供 /api/workshop/rule-catalog、Web 零代码积木编辑器与 Gate #4 属性白名单共同消费）。
/// </summary>
public static class ModRuleCatalog
{
    public sealed record CatalogItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("nameZh")] string NameZh,
        [property: JsonPropertyName("descZh")] string DescZh,
        [property: JsonPropertyName("recommended")] bool Recommended = false,
        [property: JsonPropertyName("unit")] string? Unit = null,
        [property: JsonPropertyName("category")] string? Category = null);

    public sealed record EffectPreset(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("nameZh")] string NameZh,
        [property: JsonPropertyName("scope")] string Scope, // ability | aura | encounterStep
        [property: JsonPropertyName("descZh")] string DescZh,
        [property: JsonPropertyName("template")] object Template);

    /// <summary>引擎已真实实现的 4 种前置条件白名单（严格对齐 ValueAndConditionEvaluator.EvaluatePrerequisite）。</summary>
    public static readonly IReadOnlySet<string> SupportedPrerequisiteTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "TPrerequisiteCardAttributeComparator",
        "TPrerequisiteCardCount",
        "TPrerequisitePlayer",
        "TPrerequisiteRun"
    };

    /// <summary>RuleNode 所有合法 JSON 属性名集合（不区分大小写），用于 Gate #4 捕捉拼写错误字段。</summary>
    public static readonly IReadOnlySet<string> KnownRuleNodePropertyNames = BuildRuleNodePropertyNames();

    private static HashSet<string> BuildRuleNodePropertyNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "$type" };
        foreach (var prop in typeof(RuleNode).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<JsonExtensionDataAttribute>() != null) continue;
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            set.Add(attr?.Name ?? prop.Name);
        }
        return set;
    }

    /// <summary>计算两个字符串的编辑距离（用于给出拼错字段名的自动纠错提示）。</summary>
    public static string? FindClosestKnownRuleProperty(string unknownName, int maxDistance = 2)
    {
        string? best = null;
        int bestDist = maxDistance + 1;
        foreach (var known in KnownRuleNodePropertyNames)
        {
            int d = Levenshtein(unknownName.ToLowerInvariant(), known.ToLowerInvariant());
            if (d < bestDist)
            {
                bestDist = d;
                best = known;
            }
        }
        return bestDist <= maxDistance ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (Math.Abs(n - m) > 3) return Math.Abs(n - m);
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }

    /// <summary>构建完整的中文积木目录字典（可安全 JSON 序列化缓存）。</summary>
    public static object BuildCatalogDto()
    {
        var triggers = RuleTypeRegistry.KnownTypes
            .Where(t => t.StartsWith("TTrigger", StringComparison.Ordinal) && AbilityExecutor.IsKnownTriggerType(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => new CatalogItem(t, GetTriggerNameZh(t), GetTriggerDescZh(t), IsRecommendedTrigger(t)))
            .ToList();

        var actions = RuleTypeRegistry.KnownTypes
            .Where(t => t.StartsWith("TAction", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => new CatalogItem(t, GetActionNameZh(t), GetActionDescZh(t), IsRecommendedAction(t)))
            .ToList();

        var auraActions = RuleTypeRegistry.KnownTypes
            .Where(t => t.StartsWith("TAuraAction", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => new CatalogItem(t, GetAuraActionNameZh(t), GetAuraActionDescZh(t), true))
            .ToList();

        var targets = RuleTypeRegistry.KnownTypes
            .Where(t => t.StartsWith("TTarget", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => new CatalogItem(t, GetTargetNameZh(t), GetTargetDescZh(t), IsRecommendedTarget(t)))
            .ToList();

        var conditions = RuleTypeRegistry.KnownTypes
            .Where(t => t.Contains("Conditional", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => new CatalogItem(t, GetConditionNameZh(t), $"引擎条件节点 {t}", t is "TCardConditionalTag" or "TPlayerConditionalAttribute" or "TCardConditionalHiddenTag"))
            .ToList();

        var prerequisites = SupportedPrerequisiteTypes
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => new CatalogItem(t, GetPrerequisiteNameZh(t), GetPrerequisiteDescZh(t), true))
            .ToList();

        var values = RuleTypeRegistry.KnownTypes
            .Where(t => t.StartsWith("TFixedValue", StringComparison.Ordinal)
                        || t.StartsWith("TRangeValue", StringComparison.Ordinal)
                        || t.StartsWith("TReferenceValue", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => new CatalogItem(t, GetValueNameZh(t), $"数值来源节点 {t}", t is "TFixedValue" or "TReferenceValueCardAttribute" or "TReferenceValuePlayerAttribute"))
            .ToList();

        var durations = RuleTypeRegistry.KnownTypes
            .Where(t => t.EndsWith("Duration", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => new CatalogItem(t, t switch
            {
                "TCombatDuration" => "本场战斗持续",
                "TDeterminantDuration" => "固定毫秒时长",
                "TVariableDuration" => "动态引用时长",
                _ => t
            }, "持续时长节点", true))
            .ToList();

        return new
        {
            schemaVersion = 1,
            totalKnownRuleTypes = RuleTypeRegistry.KnownTypes.Count,
            triggers,
            actions,
            auraActions,
            targets,
            conditions,
            prerequisites,
            values,
            durations,
            attributes = GetCommonAttributes(),
            enchantments = EnchantmentNames.All.Select(e => new CatalogItem(e, EnchantmentNames.GetChineseName(e), $"附魔类型 {e}", true)).ToList(),
            tags = GetCommonTags(),
            hiddenTags = GetCommonHiddenTags(),
            heroes = new[] { HeroNames.Common }.Concat(HeroNames.PlayableHeroes).Select(h =>
            {
                var p = HeroNames.GetHeroProfile(h);
                return new CatalogItem(h, p.DisplayName, p.Summary, true);
            }).ToList(),
            sizes = new[]
            {
                new CatalogItem("Small", "小型 (占 1 格 · 竖图 512×1024)", "占棋盘 1 格，竖向长卡，推荐卡图 512×1024 PNG", true),
                new CatalogItem("Medium", "中型 (占 2 格 · 方图 512×512)", "占棋盘 2 格，方形卡面，推荐卡图 512×512 PNG", true),
                new CatalogItem("Large", "大型 (占 3 格 · 横图 768×512)", "占棋盘 3 格，横向宽卡（3:2），推荐卡图 768×512 PNG", true),
            },
            tiers = new[]
            {
                new CatalogItem("Bronze", "青铜 (Bronze)", "1 阶起始品质", true),
                new CatalogItem("Silver", "白银 (Silver)", "2 阶进阶品质", true),
                new CatalogItem("Gold", "黄金 (Gold)", "3 阶稀有品质", true),
                new CatalogItem("Diamond", "钻石 (Diamond)", "4 阶顶级品质", true),
                new CatalogItem("Legendary", "传说 (Legendary)", "特殊传说品质", false),
            },
            mechanicSchema = BuildMechanicSchemaDto(),
            presets = BuildEffectPresets()
        };
    }

    public static object BuildMechanicSchemaDto() => new
    {
        hooks = new[]
        {
            new CatalogItem("OnFightStart", "开战瞬间 (OnFightStart)", "战斗 0ms 启动时执行，适合初始化计数器与标记", true, null, "生命周期"),
            new CatalogItem("BeforeDamage", "受到伤害前拦截 (BeforeDamage)", "直伤/灼烧/剧毒/沙暴扣血前触发，可取消伤害或改写伤害量", true, null, "管线拦截"),
            new CatalogItem("BeforeStatusApply", "状态施加前拦截 (BeforeStatusApply)", "护盾/灼烧/剧毒/治疗/再生/加速/减速/冻结/充能生效前触发，可取消或改写数值", true, null, "管线拦截"),
            new CatalogItem("BeforeCardStateChange", "卡牌状态变更前拦截 (BeforeCardStateChange)", "摧毁/禁用/修复/变形发生前触发（含 Reason 原因），可拦截如不可复原", true, null, "管线拦截"),
            new CatalogItem("BeforeCardFire", "卡牌开火前拦截 (BeforeCardFire)", "物品冷却完成即将施放前触发，可取消或追加前置逻辑", true, null, "管线拦截"),
            new CatalogItem("OnCardFired", "卡牌开火时 (OnCardFired)", "卡牌实际开火时触发，适合主动触发自定义机制流程", true, null, "战斗触发"),
            new CatalogItem("AfterCardFire", "卡牌开火后 (AfterCardFire)", "卡牌开火结算完毕后触发", false, null, "战斗触发"),
            new CatalogItem("OnTriggerEvent", "任意官方触发事件 (OnTriggerEvent)", "监听全部 46 种官方 TTrigger* 事件（通过 filter.triggerEventTypes 筛选）", true, null, "战斗触发"),
            new CatalogItem("OnCustomEvent", "自定义事件广播 (OnCustomEvent)", "监听同场战斗内由 EmitCustomEvent 派发的命名事件（用于跨机制解耦联动）", true, null, "事件联动"),
            new CatalogItem("PostCombatCommit", "战后局外安全结转 (PostCombatCommit)", "战斗结束瞬间执行，可登记战后安全移除卡牌或修改永久属性", true, null, "局外结转")
        },
        ops = new[]
        {
            new CatalogItem("If", "条件分支 (If)", "根据黑板标记、计数器、属性或钩子上下文执行 Then/Else 分支", true, null, "流程控制"),
            new CatalogItem("Repeat", "受控多段重复 (Repeat)", "重复执行子步骤（硬上限 ≤20 次，支持 intervalMs 分帧间隔连击）", true, null, "流程控制"),
            new CatalogItem("ScheduleDelayed", "延迟排程执行 (ScheduleDelayed)", "延迟指定毫秒后在真实战斗时间轴上执行子步骤", true, null, "流程控制"),
            new CatalogItem("CancelHook", "拦截取消当前管线事件 (CancelHook)", "在 Before* 钩子中直接取消本次伤害/状态/修复/摧毁", true, null, "管线改写"),
            new CatalogItem("ModifyHookAmount", "改写当前管线数值 (ModifyHookAmount)", "在 BeforeDamage / BeforeStatusApply 中重写或按比例缩放数值", true, null, "管线改写"),
            new CatalogItem("Stop", "中止当前钩子后续步骤 (Stop)", "立即结束本次钩子链剩余步骤", false, null, "流程控制"),
            new CatalogItem("SetFlag", "写入自定义标记 (SetFlag)", "给卡牌或战斗黑板打上任意自定义 Flag（如 unrepairable / primed）", true, null, "状态黑板"),
            new CatalogItem("ClearFlag", "清除自定义标记 (ClearFlag)", "移除卡牌或战斗黑板上的指定 Flag", false, null, "状态黑板"),
            new CatalogItem("ModifyCounter", "读写黑板计数器 (ModifyCounter)", "增减/设值自定义计数器，并可选同步投影到 Custom_0..3 在卡面实时显示", true, null, "状态黑板"),
            new CatalogItem("DealDamage", "造成受控伤害 (DealDamage)", "对敌方或己方造成伤害（可选穿盾与官方白名单 VFX 键）", true, null, "战斗动作"),
            new CatalogItem("ApplyStatus", "施加标准状态 (ApplyStatus)", "施加 Shield/Burn/Poison/Heal/Regen/Haste/Slow/Freeze/Charge", true, null, "战斗动作"),
            new CatalogItem("ModifyCardAttribute", "修改卡牌属性 (ModifyCardAttribute)", "修改目标卡牌伤害/护盾/冷却百分比等（自动强钳制防 0 秒死循环）", true, null, "战斗动作"),
            new CatalogItem("ModifyPlayerAttribute", "修改玩家属性 (ModifyPlayerAttribute)", "修改生命上限/减伤等玩家属性", false, null, "战斗动作"),
            new CatalogItem("ChangeCardState", "变更卡牌状态 (ChangeCardState)", "受控执行 Disable / Destroy / Repair（支持自定义 Reason 与关闭常规摧毁联动）", true, null, "战斗动作"),
            new CatalogItem("ForceUseCard", "强制触发卡牌 (ForceUseCard)", "受帧预算保护强制触发目标卡牌一次", false, null, "战斗动作"),
            new CatalogItem("EmitCustomEvent", "广播自定义事件 (EmitCustomEvent)", "向同场战斗广播命名自定义事件供其他机制响应", true, null, "事件联动"),
            new CatalogItem("QueuePostCombatRemoveCard", "登记战后永久移除卡牌 (QueuePostCombatRemoveCard)", "战后由 RunEngine 统一从棋盘移除并下发官方协议事件", true, null, "局外结转"),
            new CatalogItem("QueuePostCombatModifyAttr", "登记战后永久属性改动 (QueuePostCombatModifyAttr)", "战后由 RunEngine 安全回写永久属性加成", false, null, "局外结转")
        },
        paramTypes = new[]
        {
            new CatalogItem("int", "整数 (int)", "离散整数参数（支持 min/max 上下限）", true),
            new CatalogItem("float", "小数 (float)", "浮点数参数", true),
            new CatalogItem("bool", "开关 (bool)", "是/否布尔开关", true),
            new CatalogItem("string", "文本 (string)", "短字符串参数", true),
            new CatalogItem("tag", "卡牌标签 (tag)", "下拉选择官方卡牌标签（Weapon/Friend/Aquatic 等）", true),
            new CatalogItem("attribute", "卡牌属性 (attribute)", "下拉选择卡牌属性（DamageAmount/CooldownMax/PercentCooldownReduction 等）", true),
            new CatalogItem("targetMode", "目标模式 (targetMode)", "下拉选择沙盒目标模式（Self/AllAllies/Adjacent 等）", true),
            new CatalogItem("vfxKey", "官方特效 (vfxKey)", "下拉选择官方 131 种白名单 VFX 特效键", true)
        },
        targetModes = new[]
        {
            new CatalogItem("Self", "自身本卡", "挂载该机制的卡牌自身", true),
            new CatalogItem("HookTarget", "钩子目标卡", "当前事件指向的目标卡", true),
            new CatalogItem("HookSource", "钩子来源卡", "引发当前事件的来源卡", true),
            new CatalogItem("AllAllies", "全部友方手牌", "己方棋盘所有在场物品", true),
            new CatalogItem("OtherAllies", "其余友方手牌（不含自身）", "己方除本卡外的全部物品", true),
            new CatalogItem("AllEnemies", "全部敌方手牌", "敌方棋盘所有在场物品", true),
            new CatalogItem("Adjacent", "左右相邻物品", "紧邻本卡左、右的友方物品", true),
            new CatalogItem("Left", "左侧紧邻物品", "紧邻本卡左侧的友方物品", true),
            new CatalogItem("Right", "右侧紧邻物品", "紧邻本卡右侧的友方物品", true),
            new CatalogItem("LeftMostAlly", "最左侧友方物品", "己方棋盘最左槽位物品", true),
            new CatalogItem("RightMostAlly", "最右侧友方物品", "己方棋盘最右槽位物品", true),
            new CatalogItem("RandomAllies", "随机 N 张友方物品", "按 count 随机抽取友方物品", true),
            new CatalogItem("RandomEnemies", "随机 N 张敌方物品", "按 count 随机抽取敌方物品", true)
        },
        statuses = new[]
        {
            new CatalogItem("Shield", "护盾", "给玩家施加护盾", true),
            new CatalogItem("Burn", "灼烧", "给玩家施加灼烧", true),
            new CatalogItem("Poison", "剧毒", "给玩家施加剧毒", true),
            new CatalogItem("Heal", "治疗", "回复玩家生命", true),
            new CatalogItem("Regen", "再生", "给玩家施加再生", true),
            new CatalogItem("Haste", "加速 (ms)", "加速目标卡牌冷却", true),
            new CatalogItem("Slow", "减速 (ms)", "减速目标卡牌冷却", true),
            new CatalogItem("Freeze", "冻结 (ms)", "冻结目标卡牌冷却", true),
            new CatalogItem("Charge", "充能 (ms)", "立即推进目标卡牌冷却进度", true)
        },
        syncAttributes = new[] { "Custom_0", "Custom_1", "Custom_2", "Custom_3" },
        budgets = new
        {
            maxChainDepth = CombatMechanicRuntime.MaxChainDepth,
            maxStepsPerHook = CombatMechanicRuntime.MaxStepsPerHook,
            maxStepsPerTick = CombatMechanicRuntime.MaxStepsPerTick,
            maxDelayedCallbacksPerCombat = CombatMechanicRuntime.MaxDelayedCallbacksPerCombat,
            maxRepeatCount = CombatMechanicRuntime.MaxRepeatCount
        }
    };

    private static List<CatalogItem> GetCommonAttributes() =>
    [
        new("BuyPrice", "购买价格", "商店购入金币价格", true, "金币", "经济"),
        new("SellPrice", "出售价格", "卖出获得金币（通常为买价一半）", true, "金币", "经济"),
        new("CooldownMax", "冷却时间（毫秒）", "主动触发周期，单位毫秒！例如 4 秒 = 4000（允许 1000~60000）", true, "ms", "核心"),
        new("DamageAmount", "造成伤害", "开火对敌方造成的基础伤害点数", true, "点", "输出"),
        new("ShieldApplyAmount", "获得护盾", "开火为己方叠加的护盾点数", true, "点", "防御"),
        new("BurnApplyAmount", "施加灼烧", "对敌方叠加的灼烧层数（每 0.5 秒结算）", true, "层", "异常"),
        new("PoisonApplyAmount", "施加剧毒", "对敌方叠加的剧毒层数（无视护盾，每秒结算）", true, "层", "异常"),
        new("HealAmount", "治疗生命", "为己方回复的生命值", true, "点", "回复"),
        new("RegenApplyAmount", "施加再生", "为己方叠加的再生层数", true, "层", "回复"),
        new("CritChance", "暴击率", "触发双倍效果的百分比概率（0~100）", true, "%", "输出"),
        new("Multicast", "多重施放", "单次冷却触发的次数（默认 1）", true, "次", "核心"),
        new("AmmoMax", "弹药上限", "最大可用弹药数（0 表示无限弹药）", false, "发", "资源"),
        new("Lifesteal", "吸血", "设为 100 时伤害等额治疗自身", false, "%", "回复"),
        new("HasteAmount", "加速时长（毫秒）", "使目标冷却速度翻倍的持续时间，1 秒 = 1000", true, "ms", "控制"),
        new("HasteTargets", "加速目标数", "单次加速影响的卡牌数量", false, "张", "控制"),
        new("SlowAmount", "减速时长（毫秒）", "使敌方冷却速度减半的持续时间，1 秒 = 1000", true, "ms", "控制"),
        new("SlowTargets", "减速目标数", "单次减速影响的敌方卡牌数", false, "张", "控制"),
        new("FreezeAmount", "冻结时长（毫秒）", "暂停目标冷却的持续时间，1 秒 = 1000", true, "ms", "控制"),
        new("FreezeTargets", "冻结目标数", "单次冻结影响的敌方卡牌数", false, "张", "控制"),
        new("ChargeAmount", "充能时长（毫秒）", "立即推进目标冷却进度的毫秒数，1 秒 = 1000", true, "ms", "控制"),
        new("PercentCooldownReduction", "冷却缩减百分比", "按百分比缩短冷却（安全上限 90%）", true, "%", "核心"),
        new("Custom_0", "自定义数值 0", "供技能成长/光环/自定义机制引用的自由数值槽", true, "点", "扩展"),
        new("Custom_1", "自定义数值 1", "供技能成长/经济奖励引用的自由数值槽", true, "点", "扩展"),
        new("Custom_2", "自定义数值 2", "自由数值槽 2", false, "点", "扩展"),
        new("GoldGainAmount", "金币获取量", "单次触发获得的金币数", false, "金币", "经济")
    ];

    private static List<CatalogItem> GetCommonTags() =>
    [
        new("Weapon", "武器", "进攻型装备标签", true),
        new("Tool", "工具", "工坊/机械辅助标签", true),
        new("Apparel", "服饰", "穿戴防护装备标签", true),
        new("Aquatic", "水生", "瓦内莎海洋流派标签", true),
        new("Friend", "好友", "机械伙伴/生物随从标签", true),
        new("Core", "核心", "杜利核心装置标签", true),
        new("Tech", "科技", "高科技装置标签", true),
        new("Property", "地产", "皮格马利翁高价值建筑标签", true),
        new("Potion", "药水", "马克炼金药剂标签", true),
        new("Relic", "遗物", "古老圣物/炼金奇物标签", true),
        new("Reagent", "试剂", "炼金原料标签", false),
        new("Food", "食物", "朱尔斯料理标签", true),
        new("Vehicle", "载具", "大型战车/飞船标签", true),
        new("Toy", "玩具", "趣味机械标签", false),
        new("Merchant", "商会", "贸易/经济标签", true),
        new("Dragon", "巨龙", "双龙流派标签", false),
        new("Instrument", "乐器", "音律/节奏流派标签", false),
        new("Dinosaur", "恐龙", "史前生物标签", false),
        new("Ray", "射线", "能量光束标签", false),
        new("Drone", "无人机", "无人飞行器标签", false),
        new("Loot", "战利品", "消耗型战利品标签", false),
        new("Quest", "任务", "可成长任务卡标签", false)
    ];

    private static List<CatalogItem> GetCommonHiddenTags() =>
    [
        new("Damage", "伤害", "隐藏语义：可造成伤害", true),
        new("Shield", "护盾", "隐藏语义：可提供护盾", true),
        new("Burn", "灼烧", "隐藏语义：可施加灼烧", true),
        new("Poison", "剧毒", "隐藏语义：可施加剧毒", true),
        new("Heal", "治疗", "隐藏语义：可回复生命", true),
        new("Regen", "再生", "隐藏语义：可施加再生", true),
        new("Haste", "加速", "隐藏语义：可提供加速", true),
        new("Slow", "减速", "隐藏语义：可施加减速", true),
        new("Freeze", "冻结", "隐藏语义：可施加冻结", true),
        new("Charge", "充能", "隐藏语义：可提供充能", true),
        new("Crit", "暴击", "隐藏语义：关联暴击机制", true),
        new("Ammo", "弹药", "隐藏语义：消耗或装填弹药", false),
        new("Economy", "经济", "隐藏语义：产出金币/价值", true),
        new("NonWeapon", "非武器", "隐藏语义：非武器物品", false),
        new("Active", "主动", "隐藏语义：具备主动冷却", false),
        new("Passive", "被动", "隐藏语义：纯被动触发或光环", false)
    ];

    private static List<EffectPreset> BuildEffectPresets() =>
    [
        new("fire_damage", "开火造成伤害", "ability", "冷却完成时对敌方造成 {attr.DamageAmount} 点伤害", new
        {
            trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardFired" },
            activeIn = "HandOnly",
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionPlayerDamage",
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["targetMode"] = "Opponent" }
            }
        }),
        new("fire_shield", "开火获得护盾", "ability", "冷却完成时为己方施加 {attr.ShieldApplyAmount} 点护盾", new
        {
            trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardFired" },
            activeIn = "HandOnly",
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionPlayerShieldApply",
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["targetMode"] = "Self" }
            }
        }),
        new("fire_burn", "开火施加灼烧", "ability", "冷却完成时对敌方施加 {attr.BurnApplyAmount} 层灼烧", new
        {
            trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardFired" },
            activeIn = "HandOnly",
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionPlayerBurnApply",
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["targetMode"] = "Opponent" }
            }
        }),
        new("fire_poison", "开火施加剧毒", "ability", "冷却完成时对敌方施加 {attr.PoisonApplyAmount} 层剧毒", new
        {
            trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardFired" },
            activeIn = "HandOnly",
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionPlayerPoisonApply",
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["targetMode"] = "Opponent" }
            }
        }),
        new("fire_heal", "开火治疗生命", "ability", "冷却完成时回复 {attr.HealAmount} 点生命", new
        {
            trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardFired" },
            activeIn = "HandOnly",
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionPlayerHeal",
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["targetMode"] = "Self" }
            }
        }),
        new("fire_haste_neighbor", "开火加速相邻物品", "ability", "冷却完成时使相邻物品加速 {attr.HasteAmount} 毫秒", new
        {
            trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardFired" },
            activeIn = "HandOnly",
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionCardHaste",
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetCardPositional", ["origin"] = "Self", ["targetMode"] = "Neighbor" }
            }
        }),
        new("neighbor_used_buff_dmg", "相邻使用时自身加攻", "ability", "每当相邻物品使用时，本场战斗自身伤害 +{attr.Custom_0}", new
        {
            trigger = new Dictionary<string, object>
            {
                ["$type"] = "TTriggerOnItemUsed",
                ["subject"] = new Dictionary<string, object> { ["$type"] = "TTargetCardPositional", ["origin"] = "Self", ["targetMode"] = "Neighbor" }
            },
            activeIn = "HandOnly",
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionCardModifyAttribute",
                ["attributeType"] = "DamageAmount",
                ["operation"] = "Add",
                ["value"] = new Dictionary<string, object> { ["$type"] = "TReferenceValueCardAttribute", ["attributeType"] = "Custom_0" },
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetCardSelf" }
            }
        }),
        new("neighbor_used_charge_self", "相邻使用时自身充能", "ability", "相邻物品使用时，为本卡充能 {attr.ChargeAmount} 毫秒（安全不死循环）", new
        {
            trigger = new Dictionary<string, object>
            {
                ["$type"] = "TTriggerOnItemUsed",
                ["subject"] = new Dictionary<string, object> { ["$type"] = "TTargetCardPositional", ["origin"] = "Self", ["targetMode"] = "Neighbor" }
            },
            activeIn = "HandOnly",
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionCardCharge",
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetCardSelf" }
            }
        }),
        new("fight_start_regen", "开战获得再生", "ability", "战斗开始时为己方施加 {attr.RegenApplyAmount} 层再生", new
        {
            trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnFightStarted" },
            activeIn = "HandOnly",
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionPlayerRegenApply",
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerRelative", ["targetMode"] = "Self" }
            }
        }),
        new("aura_gold_scaling_dmg", "金币条件增伤光环", "aura", "当玩家金币 ≥ 20 时，自身伤害提升 50%（使用合规 TPrerequisitePlayer）", new
        {
            activeIn = "HandOnly",
            prerequisites = new[]
            {
                new Dictionary<string, object>
                {
                    ["$type"] = "TPrerequisitePlayer",
                    ["subject"] = new Dictionary<string, object>
                    {
                        ["$type"] = "TTargetPlayerAbsolute",
                        ["targetMode"] = "Player",
                        ["conditions"] = new Dictionary<string, object>
                        {
                            ["$type"] = "TPlayerConditionalAttribute",
                            ["attributeType"] = "Gold",
                            ["comparisonOperator"] = "GreaterThanOrEqual",
                            ["comparisonValue"] = new Dictionary<string, object> { ["$type"] = "TFixedValue", ["value"] = 20.0 }
                        }
                    }
                }
            },
            action = new Dictionary<string, object>
            {
                ["$type"] = "TAuraActionCardModifyAttribute",
                ["attributeType"] = "DamageAmount",
                ["operation"] = "Multiply",
                ["value"] = new Dictionary<string, object> { ["$type"] = "TFixedValue", ["value"] = 1.5 },
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetCardSelf" }
            }
        }),
        new("step_gain_gold", "奇遇选项：获得金币", "encounterStep", "选中该奇遇步骤时给予玩家 6 金币", new
        {
            trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardSelected" },
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionPlayerModifyAttribute",
                ["attributeType"] = "Gold",
                ["operation"] = "Add",
                ["value"] = new Dictionary<string, object> { ["$type"] = "TFixedValue", ["value"] = 6.0 },
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetPlayerAbsolute", ["targetMode"] = "Player" }
            }
        }),
        new("step_upgrade_leftmost", "奇遇选项：升级最左侧物品", "encounterStep", "选中该奇遇步骤时升级手牌最左侧物品 1 阶", new
        {
            trigger = new Dictionary<string, object> { ["$type"] = "TTriggerOnCardSelected" },
            action = new Dictionary<string, object>
            {
                ["$type"] = "TActionCardUpgrade",
                ["target"] = new Dictionary<string, object> { ["$type"] = "TTargetCardXMost", ["targetSection"] = "SelfHand", ["targetMode"] = "LeftMostCard" }
            }
        })
    ];

    private static bool IsRecommendedTrigger(string t) => t is
        "TTriggerOnCardFired" or "TTriggerOnItemUsed" or "TTriggerOnFightStarted" or "TTriggerOnFightEnded"
        or "TTriggerOnCardSelected" or "TTriggerOnCardCritted" or "TTriggerOnDayStarted" or "TTriggerOnCardSold"
        or "TTriggerOnCardPurchased" or "TTriggerOnPlayerAttributeChanged" or "TTriggerOnCardPerformedBurn"
        or "TTriggerOnCardPerformedShield";

    private static bool IsRecommendedAction(string t) => t is
        "TActionPlayerDamage" or "TActionPlayerShieldApply" or "TActionPlayerBurnApply" or "TActionPlayerPoisonApply"
        or "TActionPlayerHeal" or "TActionPlayerRegenApply" or "TActionCardHaste" or "TActionCardSlow"
        or "TActionCardFreeze" or "TActionCardCharge" or "TActionCardModifyAttribute" or "TActionPlayerModifyAttribute"
        or "TActionCardUpgrade" or "TActionCardEnchantRandom" or "TActionGameSpawnCards" or "TActionAnd";

    private static bool IsRecommendedTarget(string t) => t is
        "TTargetCardSelf" or "TTargetCardPositional" or "TTargetCardSection" or "TTargetCardXMost"
        or "TTargetCardRandom" or "TTargetPlayerRelative" or "TTargetPlayerAbsolute";

    private static string GetTriggerNameZh(string t) => t switch
    {
        "TTriggerOnCardFired" => "本卡冷却完成开火时",
        "TTriggerOnItemUsed" => "指定物品被使用时",
        "TTriggerOnBeforeItemUsed" => "物品使用生效前",
        "TTriggerOnCardCritted" => "卡牌暴击时",
        "TTriggerOnCardSelected" => "奇遇步骤/卡牌被选中时",
        "TTriggerOnCardSold" => "卡牌被卖出时",
        "TTriggerOnCardPurchased" => "卡牌被购买时",
        "TTriggerOnCardUpgraded" => "卡牌升级时",
        "TTriggerOnCardEnchanted" => "卡牌被附魔时",
        "TTriggerOnCardTransformed" => "卡牌变形时",
        "TTriggerOnCardAfterTransform" => "卡牌变形完成后",
        "TTriggerOnBeforeCardDestroyed" => "卡牌被摧毁前（亡语）",
        "TTriggerOnCardDisabled" => "卡牌被禁用/碎裂时",
        "TTriggerOnCardRepaired" => "卡牌被修复时",
        "TTriggerOnCardPerformedDestruction" => "卡牌执行摧毁时",
        "TTriggerOnCardAttributeChanged" => "卡牌属性变化时",
        "TTriggerOnCardCharged" => "卡牌获得充能时",
        "TTriggerOnCardStartedFlying" => "卡牌开始飞行时",
        "TTriggerOnCardStoppedFlying" => "卡牌停止飞行时",
        "TTriggerOnCardQuestCompleted" => "卡牌任务完成时",
        "TTriggerOnFightStarted" => "战斗开始时",
        "TTriggerOnFightEnded" => "战斗结束时",
        "TTriggerOnDayStarted" => "新的一天开始时",
        "TTriggerOnHourStarted" => "新的小时开始时",
        "TTriggerOnEncounterSelected" => "遭遇被选中时",
        "TTriggerOnEncounterEntered" => "进入遭遇时",
        "TTriggerOnEncounterExited" => "离开遭遇时",
        "TTriggerOnEncounterCardsDealt" => "遭遇发牌完成时",
        "TTriggerOnPlayerAttributeChanged" => "玩家属性变化时",
        "TTriggerOnPlayerDamaged" => "玩家受到伤害时",
        "TTriggerOnPlayerDied" => "玩家生命归零濒死时",
        "TTriggerOnPlayerEnraged" => "玩家进入狂暴时",
        "TTriggerOnPlayerEnrageEnded" => "玩家狂暴结束时",
        "TTriggerOnCardPerformedBurn" => "卡牌施加灼烧时",
        "TTriggerOnCardPerformedShield" => "卡牌施加护盾时",
        "TTriggerOnCardPerformedDamage" => "卡牌造成伤害时",
        "TTriggerOnCardPerformedHaste" => "卡牌触发加速时",
        "TTriggerOnCardPerformedSlow" => "卡牌触发减速时",
        "TTriggerOnCardPerformedFreeze" => "卡牌触发冻结时",
        "TTriggerOnCardPerformedHeal" => "卡牌触发治疗时",
        "TTriggerOnCardPerformedOverHeal" => "卡牌触发过量治疗时",
        "TTriggerOnCardPerformedRegen" => "卡牌施加再生时",
        "TTriggerOnCardPerformedPoison" => "卡牌施加剧毒时",
        "TTriggerOnCardPerformedReload" => "卡牌装填弹药时",
        "TTriggerOnSandstorm" => "沙暴来袭时",
        "TTriggerOr" => "多触发器任一满足（OR 复合）",
        _ => t
    };

    private static string GetTriggerDescZh(string t) => t switch
    {
        "TTriggerOnCardFired" => "最常用主动触发器：卡牌冷却条走满时执行动作（要求物品配置 CooldownMax 毫秒）",
        "TTriggerOnItemUsed" => "监听自身、相邻或场上符合条件物品被使用（可配 subject 筛选标签/位置）",
        "TTriggerOnBeforeCardDestroyed" => "原生亡语触发器：目标卡牌被摧毁/禁用瞬间触发",
        "TTriggerOnCardSelected" => "奇遇子步骤（EncounterStep）专用触发器，玩家点选该选项立即执行",
        "TTriggerOnFightStarted" => "每场 PvP / PvE 战斗第 0ms 触发一次",
        "TTriggerOnFightEnded" => "战斗结算时触发，可设 combatOutcome 为 Win 或 Lose",
        "TTriggerOnDayStarted" => "每日开始时触发，适合经济成长类物品",
        "TTriggerOr" => "组合多个触发器，任意一个满足即执行动作链",
        _ => $"官方标准触发器 {t}"
    };

    private static string GetActionNameZh(string t) => t switch
    {
        "TActionPlayerDamage" => "对玩家造成伤害",
        "TActionPlayerShieldApply" => "给玩家施加护盾",
        "TActionPlayerBurnApply" => "给玩家施加灼烧",
        "TActionPlayerBurnRemove" => "移除玩家灼烧",
        "TActionPlayerPoisonApply" => "给玩家施加剧毒",
        "TActionPlayerPoisonRemove" => "移除玩家剧毒",
        "TActionPlayerHeal" => "治疗玩家生命",
        "TActionPlayerReviveHeal" => "濒死复活并治疗生命",
        "TActionPlayerRegenApply" => "给玩家施加再生",
        "TActionPlayerRageApply" => "给玩家施加狂暴",
        "TActionPlayerTempoApply" => "给玩家施加节奏能量",
        "TActionPlayerModifyAttribute" => "修改玩家属性（金币/经验/声望/收入/生命上限）",
        "TActionCostPlayerAttribute" => "消耗玩家属性（如金币/生命）",
        "TActionCardHaste" => "加速目标卡牌",
        "TActionCardSlow" => "减速目标卡牌",
        "TActionCardFreeze" => "冻结目标卡牌",
        "TActionCardCharge" => "充能目标卡牌",
        "TActionCardReload" => "装填目标卡牌弹药",
        "TActionCardForceUse" => "强制立即触发目标卡牌",
        "TActionCardModifyAttribute" => "修改卡牌属性（加攻/加盾/减冷却等）",
        "TActionCardDisable" => "战内禁用/摧毁目标卡牌",
        "TActionCardDestroy" => "摧毁目标卡牌",
        "TActionCardRepair" => "修复已被禁用/摧毁的卡牌",
        "TActionCardFlyingStart" => "令目标卡牌起飞",
        "TActionCardFlyingStop" => "令目标卡牌降落",
        "TActionCardFlyingToggle" => "切换目标卡牌飞行状态",
        "TActionCardUpgrade" => "升级目标卡牌品阶",
        "TActionCardTransform" => "变形目标卡牌",
        "TActionCardTransformDestroyed" => "将已摧毁卡牌变形为新卡",
        "TActionCardEnchant" => "为卡牌赋予指定附魔",
        "TActionCardEnchantRandom" => "为卡牌随机附魔",
        "TActionCardEnchantRemove" => "移除卡牌附魔",
        "TActionCardAddTagsList" => "为卡牌追加指定标签",
        "TActionCardAddTagsRandom" => "为卡牌追加随机标签",
        "TActionCardAddTagsBySource" => "按来源卡牌复制标签",
        "TActionCardTiny" => "令目标卡牌缩小（Tiny）",
        "TActionCardBeginSandstorm" => "立即提前开启沙暴",
        "TActionGameSpawnCards" => "向玩家发放/生成卡牌",
        "TActionGameDealCards" => "发牌进入选择区",
        "TActionGameReroll" => "刷新当前遭遇选项",
        "TActionGameAddToExclusionSet" => "将事件加入本局排除池",
        "TActionGameRemoveFromExclusionSet" => "将事件移出排除池（解锁事件链）",
        "TActionAnd" => "顺序组合执行多个动作",
        _ => t
    };

    private static string GetActionDescZh(string t) => t switch
    {
        "TActionPlayerDamage" => "默认读取本卡 DamageAmount，对 Opponent 造成伤害",
        "TActionPlayerShieldApply" => "默认读取本卡 ShieldApplyAmount，对 Self 施加护盾",
        "TActionPlayerBurnApply" => "默认读取本卡 BurnApplyAmount，对 Opponent 施加灼烧",
        "TActionPlayerPoisonApply" => "默认读取本卡 PoisonApplyAmount，对 Opponent 施加剧毒",
        "TActionPlayerModifyAttribute" => "可修改 Gold / Experience / Prestige / Income / HealthMax 等玩家属性",
        "TActionAnd" => "在 actions 数组中按顺序执行多条标准动作",
        _ => $"官方标准动作 {t}"
    };

    private static string GetAuraActionNameZh(string t) => t switch
    {
        "TAuraActionCardModifyAttribute" => "光环：持续修改卡牌属性",
        "TAuraActionPlayerModifyAttribute" => "光环：持续修改玩家属性",
        "TAuraActionCardAddTagsList" => "光环：临时赋予卡牌标签",
        "TAuraActionCardRemoveTagsList" => "光环：临时移除卡牌标签",
        "TAuraActionCardAddTagsBySource" => "光环：按来源赋予标签",
        "TAuraActionCardRemoveTagsBySource" => "光环：按来源移除标签",
        _ => t
    };

    private static string GetAuraActionDescZh(string t) => $"静态光环持续修正动作 {t}（Multiply 倍率需在 0.05~10.0 安全区间内）";

    private static string GetTargetNameZh(string t) => t switch
    {
        "TTargetCardSelf" => "本卡自身",
        "TTargetCardPositional" => "按位置定位（左/右/相邻）",
        "TTargetCardSection" => "按区域筛选（己方/敌方手牌或背包）",
        "TTargetCardXMost" => "最左侧 / 最右侧卡牌",
        "TTargetCardRandom" => "随机选取卡牌",
        "TTargetCardTriggerSource" => "触发事件源卡牌",
        "TTargetPlayerRelative" => "相对玩家（Self 己方 / Opponent 敌方）",
        "TTargetPlayerAbsolute" => "绝对玩家（Player 玩家本人）",
        _ => t
    };

    private static string GetTargetDescZh(string t) => $"目标解析节点 {t}";

    private static string GetConditionNameZh(string t) => t switch
    {
        "TCardConditionalTag" => "卡牌包含指定标签",
        "TCardConditionalHiddenTag" => "卡牌包含隐藏标签",
        "TCardConditionalAttribute" => "卡牌属性数值比较",
        "TCardConditionalSize" => "卡牌尺寸匹配",
        "TCardConditionalTier" => "卡牌品质匹配",
        "TCardConditionalType" => "卡牌类型匹配",
        "TPlayerConditionalAttribute" => "玩家属性数值比较（如 Gold >= 20）",
        "TCardConditionalAnd" => "多条件且（AND）",
        "TCardConditionalOr" => "多条件或（OR）",
        _ => t
    };

    private static string GetPrerequisiteNameZh(string t) => t switch
    {
        "TPrerequisitePlayer" => "玩家条件前置（配合 TTargetPlayerAbsolute + TPlayerConditionalAttribute）",
        "TPrerequisiteCardCount" => "场上符合条件卡牌数量前置",
        "TPrerequisiteRun" => "局内状态前置（天数/小时/沙暴等）",
        "TPrerequisiteCardAttributeComparator" => "双卡属性比较前置",
        _ => t
    };

    private static string GetPrerequisiteDescZh(string t) => t switch
    {
        "TPrerequisitePlayer" => "检查玩家金币/生命/护盾等属性时必须使用此节点（严禁编造 TPrerequisitePlayerAttribute！）",
        "TPrerequisiteCardCount" => "例如：场上至少有 3 件武器时才生效",
        _ => $"前置条件节点 {t}"
    };

    private static string GetValueNameZh(string t) => t switch
    {
        "TFixedValue" => "固定数值 (TFixedValue)",
        "TReferenceValueCardAttribute" => "读取卡牌当前属性",
        "TReferenceValuePlayerAttribute" => "读取玩家当前属性（如金币/生命）",
        "TReferenceValueCardCount" => "统计符合条件的卡牌数量",
        "TReferenceValueCardTagCount" => "统计标签数量",
        _ => t
    };
}
