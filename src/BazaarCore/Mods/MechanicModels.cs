using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BazaarCore.Mods;

/// <summary>
/// 自定义机制对外暴露的参数定义（由 Web 创意工坊自动渲染为可视化参数表单，普通玩家直接填数即可复用任意新机制）。
/// </summary>
public sealed class MechanicParameterDef
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("labelZh")]
    public string LabelZh { get; set; } = string.Empty;

    [JsonPropertyName("descZh")]
    public string? DescZh { get; set; }

    /// <summary>参数类型：int | float | string | bool | tag | attribute | targetMode | vfxKey。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "int";

    [JsonPropertyName("defaultValue")]
    public JsonElement? DefaultValue { get; set; }

    [JsonPropertyName("min")]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    public double? Max { get; set; }

    [JsonPropertyName("options")]
    public List<string>? Options { get; set; }
}

/// <summary>
/// 通用管线钩子过滤器：在不硬编码具体玩法的前提下，精准匹配目标事件与状态。
/// </summary>
public sealed class MechanicHookFilter
{
    [JsonPropertyName("damageTypes")]
    public List<string>? DamageTypes { get; set; }

    [JsonPropertyName("statusTypes")]
    public List<string>? StatusTypes { get; set; }

    [JsonPropertyName("changeKinds")]
    public List<string>? ChangeKinds { get; set; }

    [JsonPropertyName("triggerEventTypes")]
    public List<string>? TriggerEventTypes { get; set; }

    [JsonPropertyName("reasonEquals")]
    public string? ReasonEquals { get; set; }

    [JsonPropertyName("reasonNotEquals")]
    public string? ReasonNotEquals { get; set; }

    [JsonPropertyName("customEventName")]
    public string? CustomEventName { get; set; }

    [JsonPropertyName("requireCardFlags")]
    public List<string>? RequireCardFlags { get; set; }

    [JsonPropertyName("forbidCardFlags")]
    public List<string>? ForbidCardFlags { get; set; }

    [JsonPropertyName("requireCombatFlags")]
    public List<string>? RequireCombatFlags { get; set; }

    [JsonPropertyName("forbidCombatFlags")]
    public List<string>? ForbidCombatFlags { get; set; }

    [JsonPropertyName("onlyWhenOwnerTarget")]
    public bool? OnlyWhenOwnerTarget { get; set; }

    [JsonPropertyName("onlyWhenSelfTarget")]
    public bool? OnlyWhenSelfTarget { get; set; }

    [JsonPropertyName("onlyWhenSelfSource")]
    public bool? OnlyWhenSelfSource { get; set; }

    /// <summary>仅当事件触发源卡牌位于本卡左右相邻棋盘格时触发。</summary>
    [JsonPropertyName("onlyWhenAdjacentSource")]
    public bool? OnlyWhenAdjacentSource { get; set; }

    /// <summary>允许已被禁用/摧毁的卡牌继续监听该钩子（例如拦截对自身的 Repair 修复请求或监听自爆后续事件）。</summary>
    [JsonPropertyName("includeDisabledSelf")]
    public bool? IncludeDisabledSelf { get; set; }
}

/// <summary>
/// 安全声明式数值表达式（零代码执行，仅允许读取常量、参数、卡牌属性、黑板计数器、玩家属性与钩子字段）。
/// </summary>
public sealed class MechanicValueExpr
{
    [JsonPropertyName("const")]
    public double? Const { get; set; }

    [JsonPropertyName("param")]
    public string? Param { get; set; }

    [JsonPropertyName("cardAttr")]
    public string? CardAttr { get; set; }

    [JsonPropertyName("counter")]
    public string? Counter { get; set; }

    [JsonPropertyName("playerAttr")]
    public string? PlayerAttr { get; set; }

    /// <summary>Self | Opponent（用于读取 playerAttr）。</summary>
    [JsonPropertyName("playerSide")]
    public string? PlayerSide { get; set; }

    /// <summary>Amount | PayloadValue | TimeMs（用于读取当前管线钩子上下文数值）。</summary>
    [JsonPropertyName("hookField")]
    public string? HookField { get; set; }

    /// <summary>Self | HookSource | HookTarget | Combat（用于读取 cardAttr / counter）。</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("scale")]
    public double? Scale { get; set; }

    [JsonPropertyName("offset")]
    public double? Offset { get; set; }
}

/// <summary>
/// 安全声明式布尔条件树（支持 All/Any/Not 嵌套、黑板标记判定、数值比较与标签/钩子上下文校验）。
/// </summary>
public sealed class MechanicCondition
{
    [JsonPropertyName("all")]
    public List<MechanicCondition>? All { get; set; }

    [JsonPropertyName("any")]
    public List<MechanicCondition>? Any { get; set; }

    [JsonPropertyName("not")]
    public MechanicCondition? Not { get; set; }

    [JsonPropertyName("hasFlag")]
    public string? HasFlag { get; set; }

    /// <summary>Self | HookTarget | HookSource | Combat。</summary>
    [JsonPropertyName("flagTarget")]
    public string? FlagTarget { get; set; }

    [JsonPropertyName("left")]
    public MechanicValueExpr? Left { get; set; }

    /// <summary>Eq | Neq | Gt | Gte | Lt | Lte。</summary>
    [JsonPropertyName("compare")]
    public string? Compare { get; set; }

    [JsonPropertyName("right")]
    public MechanicValueExpr? Right { get; set; }

    [JsonPropertyName("cardHasTag")]
    public string? CardHasTag { get; set; }

    [JsonPropertyName("cardHasHiddenTag")]
    public string? CardHasHiddenTag { get; set; }

    [JsonPropertyName("cardTarget")]
    public string? CardTarget { get; set; }

    [JsonPropertyName("hookReasonEquals")]
    public string? HookReasonEquals { get; set; }

    [JsonPropertyName("hookChangeKindEquals")]
    public string? HookChangeKindEquals { get; set; }

    [JsonPropertyName("hookDamageTypeEquals")]
    public string? HookDamageTypeEquals { get; set; }

    [JsonPropertyName("paramBool")]
    public string? ParamBool { get; set; }
}

/// <summary>
/// 沙盒目标选择器（严格路由至受控解析器，禁止直接操纵底层数组）。
/// </summary>
public sealed class MechanicTargetSpec
{
    /// <summary>
    /// Self | HookTarget | HookSource | AllAllies | OtherAllies | AllEnemies |
    /// Adjacent | Left | Right | LeftMostAlly | RightMostAlly | RandomAllies | RandomEnemies。
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "Self";

    /// <summary>可选从参数键读取目标模式（支持让卡牌配置者自由切换目标）。</summary>
    [JsonPropertyName("modeParam")]
    public string? ModeParam { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("countParam")]
    public string? CountParam { get; set; }

    [JsonPropertyName("requireTags")]
    public List<string>? RequireTags { get; set; }

    [JsonPropertyName("requireTagParam")]
    public string? RequireTagParam { get; set; }

    [JsonPropertyName("requireHiddenTags")]
    public List<string>? RequireHiddenTags { get; set; }

    [JsonPropertyName("includeDisabled")]
    public bool IncludeDisabled { get; set; }
}

/// <summary>
/// 沙盒机制单步声明式操作节点（受严格白名单、步数预算与受控变更门面约束）。
/// </summary>
public sealed class MechanicStep
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("condition")]
    public MechanicCondition? Condition { get; set; }

    [JsonPropertyName("then")]
    public List<MechanicStep>? Then { get; set; }

    [JsonPropertyName("else")]
    public List<MechanicStep>? Else { get; set; }

    [JsonPropertyName("count")]
    public MechanicValueExpr? Count { get; set; }

    /// <summary>可选首段延迟（ms），用于 Repeat 连击与主攻击错峰发射（0 表示首段立即触发）。</summary>
    [JsonPropertyName("initialDelayMs")]
    public MechanicValueExpr? InitialDelayMs { get; set; }

    [JsonPropertyName("intervalMs")]
    public MechanicValueExpr? IntervalMs { get; set; }

    [JsonPropertyName("delayMs")]
    public MechanicValueExpr? DelayMs { get; set; }

    [JsonPropertyName("steps")]
    public List<MechanicStep>? Steps { get; set; }

    /// <summary>Set | Add | Subtract | Multiply。</summary>
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("value")]
    public MechanicValueExpr? Value { get; set; }

    /// <summary>Self | HookTarget | HookSource | Combat（用于 SetFlag/ClearFlag/ModifyCounter）。</summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("flag")]
    public string? Flag { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>可选将黑板计数器同步投影至官方原生属性槽（Custom_0..Custom_3），客户端卡面自动实时显示。</summary>
    [JsonPropertyName("syncAttribute")]
    public string? SyncAttribute { get; set; }

    /// <summary>Opponent | Self。</summary>
    [JsonPropertyName("playerTarget")]
    public string? PlayerTarget { get; set; }

    [JsonPropertyName("amount")]
    public MechanicValueExpr? Amount { get; set; }

    [JsonPropertyName("bypassShield")]
    public bool BypassShield { get; set; }

    [JsonPropertyName("vfxKey")]
    public string? VfxKey { get; set; }

    [JsonPropertyName("vfxKeyParam")]
    public string? VfxKeyParam { get; set; }

    /// <summary>Shield | Burn | Poison | Heal | Regen | Haste | Slow | Freeze | Charge。</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("targetSpec")]
    public MechanicTargetSpec? TargetSpec { get; set; }

    [JsonPropertyName("attribute")]
    public string? Attribute { get; set; }

    [JsonPropertyName("attributeParam")]
    public string? AttributeParam { get; set; }

    /// <summary>Combat | Permanent。</summary>
    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    /// <summary>
    /// 仅用于 ModifyCardAttribute：默认 false，对伤害/护盾/治疗/灼烧/剧毒/再生/喜悦等产出型属性仅增益原本具备该能力的卡牌，
    /// 严禁给非武器/非对应产出物品凭空挂出伤害红标；若 Mod 明确需要“赋予新产出数值”可显式设为 true。
    /// </summary>
    [JsonPropertyName("grantIfMissing")]
    public bool GrantIfMissing { get; set; }

    /// <summary>Disable | Destroy | Repair。</summary>
    [JsonPropertyName("changeKind")]
    public string? ChangeKind { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>为 true 时执行状态变更不触发普通敌方摧毁联动（如区分“自爆”与“被敌方摧毁”）。</summary>
    [JsonPropertyName("suppressNormalDestroyTriggers")]
    public bool SuppressNormalDestroyTriggers { get; set; }

    [JsonPropertyName("eventName")]
    public string? EventName { get; set; }

    [JsonPropertyName("payloadValue")]
    public MechanicValueExpr? PayloadValue { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 自定义机制的一条管线钩子绑定。
/// </summary>
public sealed class MechanicHookDefinition
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// OnFightStart | OnCardFired | BeforeDamage | BeforeStatusApply | BeforeCardStateChange |
    /// BeforeCardFire | AfterCardFire | OnCustomEvent | OnTriggerEvent | PostCombatCommit。
    /// </summary>
    [JsonPropertyName("hook")]
    public string Hook { get; set; } = string.Empty;

    /// <summary>SelfCard | OwnerPlayer | EnemyPlayer | OwnerBoard | EnemyBoard | Any。</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "SelfCard";

    [JsonPropertyName("filter")]
    public MechanicHookFilter? Filter { get; set; }

    [JsonPropertyName("maxTriggersPerCombat")]
    public int? MaxTriggersPerCombat { get; set; }

    [JsonPropertyName("steps")]
    public List<MechanicStep> Steps { get; set; } = new();
}

/// <summary>
/// 通用自定义机制/数值卡面实时徽章声明（不绑定任何特定玩法：过载/剑意/热量/灵魂/蓄力/倍率/倒计时等均可声明）。
/// 客户端收到后会在卡牌自身 3D World-Space 层级以原版鎏金/晶球/进度条/宝石美学渲染，并随 Custom_0..Custom_9 或指定属性逐帧同步。
/// </summary>
public sealed class MechanicCounterDisplay
{
    /// <summary>徽章中文短名（如「过载」「剑意」「热量」「充能」；留空时仅展示纯数值/晶球）。</summary>
    [JsonPropertyName("labelZh")]
    public string LabelZh { get; set; } = string.Empty;

    /// <summary>绑定的同步属性槽（Custom_0..Custom_9 或任意卡牌属性名）。</summary>
    [JsonPropertyName("syncAttribute")]
    public string SyncAttribute { get; set; } = "Custom_3";

    /// <summary>可选：从机制参数读取上限值（如 "threshold"），用于渲染晶球/进度条或「当前/上限」格式。</summary>
    [JsonPropertyName("maxParam")]
    public string? MaxParam { get; set; }

    /// <summary>可选：固定上限常量（若未指定 maxParam 且未指定 maxConst，则按纯数值宝石渲染）。</summary>
    [JsonPropertyName("maxConst")]
    public int? MaxConst { get; set; }

    /// <summary>徽章主题色（十六进制如 "#F97316"）。</summary>
    [JsonPropertyName("colorHex")]
    public string? ColorHex { get; set; }

    /// <summary>为 true 时当层数为 0 自动隐藏徽章；默认 false 以便局外也能清晰看到当前进度。</summary>
    [JsonPropertyName("hideWhenZero")]
    public bool HideWhenZero { get; set; }

    /// <summary>视觉样式：auto | pips | bar | gem | pill。</summary>
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    /// <summary>数值语义类型：count | progress | percent | seconds | multiplier | delta。</summary>
    [JsonPropertyName("valueType")]
    public string? ValueType { get; set; }

    /// <summary>可选自定义格式模板（支持 {label} / {value} / {max} / {unit}）。</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>可选短单位后缀（如「层」「秒」「%」）。</summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    /// <summary>锚点位置：bottom（默认底框）| bottomRight | topLeft | topRight。</summary>
    [JsonPropertyName("position")]
    public string? Position { get; set; }
}

/// <summary>
/// 声明式开放自定义机制完整定义（对应 mods/&lt;mod-id&gt;/mechanics/*.json）。
/// 不预制具体玩法，由玩家或 Mod 开发者自由声明管线钩子、黑板状态与参数 Schema，并自动同步至 Web 创意工坊表单。
/// </summary>
public sealed class MechanicDefinition
{
    public static readonly IReadOnlySet<string> AllowedHooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "OnFightStart",
        "OnCardFired",
        "BeforeDamage",
        "BeforeStatusApply",
        "BeforeCardStateChange",
        "BeforeCardFire",
        "AfterCardFire",
        "OnCustomEvent",
        "OnTriggerEvent",
        "PostCombatCommit"
    };

    public static readonly IReadOnlySet<string> AllowedOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "If",
        "Repeat",
        "ScheduleDelayed",
        "CancelHook",
        "ModifyHookAmount",
        "Stop",
        "SetFlag",
        "ClearFlag",
        "ModifyCounter",
        "DealDamage",
        "ApplyStatus",
        "ModifyCardAttribute",
        "ModifyPlayerAttribute",
        "ChangeCardState",
        "ForceUseCard",
        "EmitCustomEvent",
        "QueuePostCombatRemoveCard",
        "QueuePostCombatModifyAttr"
    };

    public static readonly IReadOnlySet<string> AllowedParamTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "int", "float", "string", "bool", "tag", "attribute", "targetMode", "vfxKey"
    };

    public static readonly IReadOnlySet<string> AllowedTargetModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Self", "HookTarget", "HookSource", "AllAllies", "OtherAllies", "AllEnemies",
        "Adjacent", "Left", "Right", "LeftMostAlly", "RightMostAlly", "RandomAllies", "RandomEnemies"
    };

    public static readonly IReadOnlySet<string> AllowedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Shield", "Burn", "Poison", "Heal", "Regen", "Haste", "Slow", "Freeze", "Charge"
    };

    public static readonly IReadOnlySet<string> AllowedSyncAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Custom_0", "Custom_1", "Custom_2", "Custom_3", "Custom_4",
        "Custom_5", "Custom_6", "Custom_7", "Custom_8"
    };

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("nameZh")]
    public string NameZh { get; set; } = string.Empty;

    [JsonPropertyName("descZh")]
    public string DescZh { get; set; } = string.Empty;

    [JsonPropertyName("categoryZh")]
    public string CategoryZh { get; set; } = "自定义机制";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("parameters")]
    public List<MechanicParameterDef> Parameters { get; set; } = new();

    [JsonPropertyName("tooltipTemplateZh")]
    public string? TooltipTemplateZh { get; set; }

    /// <summary>可选：通用卡面实时计数角标配置（任意叠层/充能/倒计时机制均可声明）。</summary>
    [JsonPropertyName("counterDisplay")]
    public MechanicCounterDisplay? CounterDisplay { get; set; }

    [JsonPropertyName("hooks")]
    public List<MechanicHookDefinition> Hooks { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>按已挂载的参数值渲染中文卡面描述（用于自动补全官方客户端可读 Tooltip）。</summary>
    public string RenderTooltip(CardMechanicAttachment? attachment)
    {
        string tpl = !string.IsNullOrWhiteSpace(TooltipTemplateZh)
            ? TooltipTemplateZh!
            : (!string.IsNullOrWhiteSpace(DescZh) ? DescZh : NameZh);
        if (string.IsNullOrWhiteSpace(tpl)) return string.Empty;

        string result = tpl;
        foreach (var p in Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Key)) continue;
            string token = "{" + p.Key + "}";
            if (!result.Contains(token, StringComparison.Ordinal)) continue;
            string formatted = FormatParamValue(p, attachment);
            result = result.Replace(token, formatted, StringComparison.Ordinal);
        }
        return result;
    }

    private static string FormatParamValue(MechanicParameterDef def, CardMechanicAttachment? attachment)
    {
        JsonElement? elem = null;
        if (attachment?.Params != null && attachment.Params.TryGetValue(def.Key, out var bound))
            elem = bound;
        else if (def.DefaultValue.HasValue)
            elem = def.DefaultValue;

        if (!elem.HasValue) return "0";
        var el = elem.Value;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out long l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : el.GetDouble().ToString("0.##", CultureInfo.InvariantCulture),
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            _ => el.ToString()
        };
    }
}

/// <summary>
/// 卡牌挂载自定义机制的实例化参数绑定（可用于 CustomCardDefinition 与 CardPatchDefinition）。
/// </summary>
public sealed class CardMechanicAttachment
{
    [JsonPropertyName("mechanicId")]
    public string MechanicId { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public Dictionary<string, JsonElement> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 战后由 RunEngine 统一校验并安全提交至局外棋盘的结转操作（防止自定义机制绕开协议直接篡改局外棋盘）。
/// </summary>
public sealed class PostCombatMutationOp
{
    /// <summary>RemoveCard | ModifyCardPermanentAttribute。</summary>
    public string OpKind { get; init; } = string.Empty;
    public string CardInstanceId { get; init; } = string.Empty;
    public string? Attribute { get; init; }
    public string? Operation { get; init; }
    public double Value { get; init; }
    public string? Reason { get; init; }
    public string? SourceMechanicId { get; init; }
}

/// <summary>
/// 自定义机制运行时安全诊断与熔断记录（供战斗体检、日志与工坊排障消费）。
/// </summary>
public sealed record MechanicDiagnostic(
    string Severity,
    string MechanicId,
    string? CardInstanceId,
    string? CardTemplateId,
    string Hook,
    string Code,
    string MessageZh,
    int TimeMs);
