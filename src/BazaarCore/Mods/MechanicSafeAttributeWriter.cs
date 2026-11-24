using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Mods;

/// <summary>
/// 单一权威属性安全写入结果（供战中沙盒与战后持久化共查）。
/// </summary>
public readonly record struct MechanicAttrWriteResult(
    bool Applied,
    double EffectiveBefore,
    double EffectiveAfter,
    string? SkippedReason = null);

/// <summary>
/// P0-32 / D3-P1-05：战中沙盒（MechanicSafetyRuntime）与战后提交（RunEngine）共用的单一权威属性安全写入与不变量清洗器。
/// </summary>
public static class MechanicSafeAttributeWriter
{
    public const double MinMultiplyStep = 0.01;
    public const double MaxMultiplyStep = 10.0;
    public const double MinEffectiveMultiplier = 0.001;
    public const double MaxEffectiveMultiplier = 1000.0;
    public const double MinCooldownMs = 1000.0;
    public const double MaxCooldownMs = 60000.0;
    public const double MinPercentCooldownReduction = -200.0;
    public const double MaxPercentCooldownReduction = 90.0;
    public const double MinMulticast = 1.0;
    public const double MaxMulticast = 10.0;
    private const double MaxSafeScalar = 1_000_000.0;

    /// <summary>受保护输出/核心能力属性集合（D3-P1-05：扩充覆盖 AmmoMax / RageApplyAmount / TempoApplyAmount / JoyApplyAmount / CritChance / Lifesteal）。</summary>
    public static readonly IReadOnlySet<string> ProtectedOutputAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DamageAmount",
        "ShieldApplyAmount",
        "BurnApplyAmount",
        "PoisonApplyAmount",
        "HealAmount",
        "RegenApplyAmount",
        "Lifesteal",
        "CritChance",
        "AmmoMax",
        "RageApplyAmount",
        "TempoApplyAmount",
        "JoyApplyAmount"
    };

    /// <summary>必须保持非负（>= 0）的属性集合。</summary>
    public static readonly IReadOnlySet<string> NonNegativeAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DamageAmount",
        "ShieldApplyAmount",
        "BurnApplyAmount",
        "PoisonApplyAmount",
        "HealAmount",
        "RegenApplyAmount",
        "Lifesteal",
        "CritChance",
        "Ammo",
        "AmmoMax",
        "RageApplyAmount",
        "TempoApplyAmount",
        "JoyApplyAmount",
        "Counter"
    };

    /// <summary>
    /// 战中沙盒与战后持久化共用的单一权威卡牌属性安全写入入口（P0-32 / D3-P1-05）。
    /// </summary>
    public static MechanicAttrWriteResult ApplyCardAttribute(
        CardInstance card,
        string attribute,
        string operation,
        double value,
        DurationTarget duration,
        bool grantIfMissing = false,
        IReadOnlyDictionary<string, MechanicDefinition>? mechanicsLookup = null)
    {
        if (card == null || string.IsNullOrWhiteSpace(attribute))
            return new MechanicAttrWriteResult(false, 0, 0, "InvalidTargetOrAttribute");
        if (!double.IsFinite(value))
            return new MechanicAttrWriteResult(false, 0, 0, "NonFiniteValue");

        string attr = attribute.Trim();
        string op = string.IsNullOrWhiteSpace(operation) ? "Add" : operation.Trim();
        double effBefore = card.GetAttribute(attr);
        if (!double.IsFinite(effBefore)) effBefore = 0;

        // 1. 输出/核心能力属性固有基线守卫：严禁凭空向非输出卡写入，且禁止被光环临时借值穿透
        if (!grantIfMissing && ProtectedOutputAttributes.Contains(attr) && !HasInherentProtectedAttribute(card, attr, mechanicsLookup))
        {
            return new MechanicAttrWriteResult(false, effBefore, effBefore, "MissingInherentProtectedAttribute");
        }

        // 2. CooldownMax 保护：仅限原生具备主动冷却（curBaseCd > 0）的卡牌，0 CD 被动卡直接跳过绝不拉成 1000ms
        if (attr.Equals("CooldownMax", StringComparison.OrdinalIgnoreCase))
        {
            double curBaseCd = GetInherentBaseCooldown(card);
            if (curBaseCd <= 0)
                return new MechanicAttrWriteResult(false, effBefore, effBefore, "PassiveCardNoCooldown");

            double nextCd = ComputeClampedTarget(effBefore, op, value, MinCooldownMs, MaxCooldownMs);
            card.ModifyAttribute(attr, "Set", nextCd, duration);
            ClampCardInvariants(card);
            return new MechanicAttrWriteResult(true, effBefore, card.GetAttribute(attr));
        }

        // 3. PercentCooldownReduction 强钳制 [-200, 90]
        if (attr.Equals("PercentCooldownReduction", StringComparison.OrdinalIgnoreCase))
        {
            double nextPcr = ComputeClampedTarget(effBefore, op, value, MinPercentCooldownReduction, MaxPercentCooldownReduction);
            card.ModifyAttribute(attr, "Set", nextPcr, duration);
            ClampCardInvariants(card);
            return new MechanicAttrWriteResult(true, effBefore, card.GetAttribute(attr));
        }

        // 4. Multicast 强钳制 [1, 10]
        if (attr.Equals("Multicast", StringComparison.OrdinalIgnoreCase))
        {
            if (!grantIfMissing && !HasInherentBaseKey(card, "Multicast") && effBefore <= 0)
                return new MechanicAttrWriteResult(false, effBefore, effBefore, "MissingInherentMulticast");

            double nextMc = ComputeClampedTarget(effBefore, op, value, MinMulticast, MaxMulticast);
            card.ModifyAttribute(attr, "Set", nextMc, duration);
            ClampCardInvariants(card);
            return new MechanicAttrWriteResult(true, effBefore, card.GetAttribute(attr));
        }

        // 5. Multiply 单步强钳 [0.01, 10.0] + 四层累计有效乘区上限 [0.001, 1000.0]
        if (op.Equals("Multiply", StringComparison.OrdinalIgnoreCase))
        {
            double stepMult = Math.Clamp(value, MinMultiplyStep, MaxMultiplyStep);
            double curTotalMult = ComputeCardEffectiveNonZeroMult(card, attr);
            double projected = curTotalMult * stepMult;
            if (projected > MaxEffectiveMultiplier)
                stepMult = Math.Max(MinMultiplyStep, MaxEffectiveMultiplier / Math.Max(1e-9, curTotalMult));
            else if (projected < MinEffectiveMultiplier)
                stepMult = Math.Min(MaxMultiplyStep, MinEffectiveMultiplier / Math.Max(1e-9, curTotalMult));

            if (Math.Abs(stepMult - 1.0) > 1e-9)
                card.ModifyAttribute(attr, "Multiply", stepMult, duration);
        }
        else if (op.Equals("Set", StringComparison.OrdinalIgnoreCase))
        {
            double safeTarget = Math.Clamp(value, NonNegativeAttributes.Contains(attr) ? 0.0 : -MaxSafeScalar, MaxSafeScalar);
            // 直接委托 W1-B 已修复的 CardInstance.ModifyAttribute("Set")，避免把光环反向烘入基线层
            card.ModifyAttribute(attr, "Set", safeTarget, duration);
        }
        else if (op.Equals("Subtract", StringComparison.OrdinalIgnoreCase))
        {
            double safeDelta = Math.Clamp(value, -MaxSafeScalar, MaxSafeScalar);
            card.ModifyAttribute(attr, "Subtract", safeDelta, duration);
        }
        else if (op.Equals("AdditiveMultiply", StringComparison.OrdinalIgnoreCase))
        {
            // D1-P1-07 (#118-2/#128)：单步钳制 [-5.0, 10.0]，同层累计上限 [-10.0, 100.0]
            double safeAddMult = Math.Clamp(value, -5.0, 10.0);
            var layerDict = duration switch
            {
                DurationTarget.Permanent => card.PermanentAdditiveMult,
                DurationTarget.Day => card.DayAdditiveMult,
                DurationTarget.Hour => card.HourAdditiveMult,
                _ => card.CombatAdditiveMult
            };
            double curLayer = layerDict.TryGetValue(attr, out var curVal) && double.IsFinite(curVal) ? curVal : 0.0;
            double projected = Math.Clamp(curLayer + safeAddMult, -10.0, 100.0);
            double clampedStep = projected - curLayer;
            if (Math.Abs(clampedStep) > 1e-9)
                card.ModifyAttribute(attr, "AdditiveMultiply", clampedStep, duration);
        }
        else
        {
            double safeDelta = Math.Clamp(value, -MaxSafeScalar, MaxSafeScalar);
            card.ModifyAttribute(attr, "Add", safeDelta, duration);
        }

        // 6. 非负输出/资源属性钳底 >= 0
        if (NonNegativeAttributes.Contains(attr))
        {
            double after = card.GetAttribute(attr);
            if (!double.IsFinite(after) || after < 0)
            {
                card.ModifyAttribute(attr, "Set", 0.0, duration);
            }
        }

        ClampCardInvariants(card);
        double effAfter = card.GetAttribute(attr);
        return new MechanicAttrWriteResult(true, effBefore, effAfter);
    }

    /// <summary>
    /// D3-P1-05：严格基于固有模板/阶位基线、永久附魔、永久增益或机制声明判断卡牌是否天然具备受保护输出属性，
    /// 绝不读取可能被邻卡光环或临时战斗层污染的 card.GetAttribute(attr)。
    /// </summary>
    public static bool HasInherentProtectedAttribute(
        CardInstance card,
        string attribute,
        IReadOnlyDictionary<string, MechanicDefinition>? mechanicsLookup = null)
    {
        if (card == null || string.IsNullOrWhiteSpace(attribute)) return false;

        // 1. 当前实例固有 BaseAttributes（初始化自模板当前阶位与永久附魔基线，不含光环与临时战斗加层）
        if (card.BaseAttributes.ContainsKey(attribute))
            return true;

        // 2. 模板顶层 Attributes 或当前阶位 Tiers[tier].Attributes 显式声明
        if (card.Template.Attributes != null && card.Template.Attributes.ContainsKey(attribute))
            return true;

        string tierName = card.Tier.ToString();
        if (card.Template.Tiers != null
            && card.Template.Tiers.TryGetValue(tierName, out var td)
            && td.Attributes.ContainsKey(attribute))
            return true;

        // 3. 永久附魔配置赋予该属性或对应动作
        if (!string.IsNullOrWhiteSpace(card.Enchantment)
            && card.Template.Enchantments != null
            && card.Template.Enchantments.TryGetValue(card.Enchantment, out var enchData))
        {
            if (enchData.Attributes.ContainsKey(attribute))
                return true;
        }

        // 4. 战前已永久落盘的 PermanentAdd > 0（如已通过 grantIfMissing=true 显式授予并持久化）
        if (card.PermanentAdd.TryGetValue(attribute, out double permAdd) && permAdd > 0)
            return true;

        // 5. 绑定的自定义机制在 GrantedAttributes 中显式声明
        if (card.Template.CustomMechanics is { Count: > 0 } && mechanicsLookup != null)
        {
            for (int i = 0; i < card.Template.CustomMechanics.Count; i++)
            {
                var att = card.Template.CustomMechanics[i];
                if (att == null || !att.Enabled || string.IsNullOrWhiteSpace(att.MechanicId)) continue;
                if (mechanicsLookup.TryGetValue(att.MechanicId.Trim(), out var def)
                    && def.GrantedAttributes != null
                    && def.GrantedAttributes.Any(a => string.Equals(a, attribute, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        // 6. 卡牌原生/永久附魔激活的 Ability 动作树确实执行对应产出动作（仅查模板与当前永久附魔技能）
        string? requiredAction = attribute.ToLowerInvariant() switch
        {
            "damageamount" => "TActionPlayerDamage",
            "shieldapplyamount" => "TActionPlayerShieldApply",
            "healamount" => "TActionPlayerHeal",
            "burnapplyamount" => "TActionPlayerBurnApply",
            "poisonapplyamount" => "TActionPlayerPoisonApply",
            "regenapplyamount" => "TActionPlayerRegenApply",
            "joyapplyamount" => "TActionPlayerJoyApply",
            "rageapplyamount" => "TActionPlayerRageApply",
            "tempoapplyamount" => "TActionPlayerTempoApply",
            _ => null
        };
        if (requiredAction != null)
        {
            for (int i = 0; i < card.ActiveAbilities.Count; i++)
            {
                if (ActionTreeHasActionType(card.ActiveAbilities[i].Action, requiredAction))
                    return true;
            }
        }

        return false;
    }

    /// <summary>清洗卡牌四层修改器中的 NaN/Infinity、状态倒计时负值及核心乘区/冷却不变式。</summary>
    public static void ClampCardInvariants(CardInstance card)
    {
        if (card == null) return;
        if (card.HasteMs < 0) card.HasteMs = 0;
        if (card.SlowMs < 0) card.SlowMs = 0;
        if (card.FreezeMs < 0) card.FreezeMs = 0;
        if (card.CooldownProgressMs < 0) card.CooldownProgressMs = 0;

        SanitizeAddLayer(card.PermanentAdd);
        SanitizeAddLayer(card.DayAdd);
        SanitizeAddLayer(card.HourAdd);
        SanitizeAddLayer(card.CombatAdd);
        SanitizeAddLayer(card.AuraAdd);

        SanitizeMultLayer(card.PermanentMult);
        SanitizeMultLayer(card.DayMult);
        SanitizeMultLayer(card.HourMult);
        SanitizeMultLayer(card.CombatMult);
        SanitizeMultLayer(card.AuraMult);

        // D1-P1-07 (#118-2/#128)：同步清洗 5 张新 *AdditiveMult 字典，防止 NaN/Infinity 或无界叠加绕过防线
        SanitizeAdditiveMultLayer(card.PermanentAdditiveMult);
        SanitizeAdditiveMultLayer(card.DayAdditiveMult);
        SanitizeAdditiveMultLayer(card.HourAdditiveMult);
        SanitizeAdditiveMultLayer(card.CombatAdditiveMult);
        SanitizeAdditiveMultLayer(card.AuraAdditiveMult);
        SanitizeAdditiveMultLayer(card.EnchantmentAuraAdditiveMult);

        if (card.CombatAdd.TryGetValue("PercentCooldownReduction", out double pcr) && pcr > MaxPercentCooldownReduction)
            card.CombatAdd["PercentCooldownReduction"] = MaxPercentCooldownReduction;
    }

    private static void SanitizeAdditiveMultLayer(Dictionary<string, double> dict)
    {
        if (dict.Count == 0) return;
        List<string>? fix = null;
        foreach (var (k, v) in dict)
        {
            if (!double.IsFinite(v) || v < -10.0 || v > 100.0)
            {
                fix ??= new List<string>();
                fix.Add(k);
            }
        }
        if (fix == null) return;
        foreach (var k in fix)
        {
            double v = dict[k];
            if (!double.IsFinite(v)) dict.Remove(k);
            else dict[k] = Math.Clamp(v, -10.0, 100.0);
        }
    }

    /// <summary>委托 PlayerState.SanitizeAllAttributeLayers() 清洗玩家全部属性层并清洗玩家所有卡牌不变式。</summary>
    public static void ClampPlayerInvariants(PlayerState player)
    {
        if (player == null) return;
        player.SanitizeAllAttributeLayers();

        foreach (var c in player.Hand.Cards) ClampCardInvariants(c);
        foreach (var c in player.Stash.Cards) ClampCardInvariants(c);
        foreach (var c in player.Skills) ClampCardInvariants(c);
        foreach (var c in player.SocketEffects) ClampCardInvariants(c);
    }

    private static double GetInherentBaseCooldown(CardInstance card)
    {
        if (card.BaseAttributes.TryGetValue("CooldownMax", out double bc) && bc > 0)
            return bc;
        if (card.Template.Attributes != null && card.Template.Attributes.TryGetValue("CooldownMax", out double tc) && tc > 0)
            return tc;
        string tierName = card.Tier.ToString();
        if (card.Template.Tiers != null
            && card.Template.Tiers.TryGetValue(tierName, out var td)
            && td.Attributes.TryGetValue("CooldownMax", out double tdc) && tdc > 0)
            return tdc;
        return 0;
    }

    private static bool HasInherentBaseKey(CardInstance card, string attr)
    {
        if (card.BaseAttributes.ContainsKey(attr)) return true;
        if (card.Template.Attributes != null && card.Template.Attributes.ContainsKey(attr)) return true;
        string tierName = card.Tier.ToString();
        return card.Template.Tiers != null
            && card.Template.Tiers.TryGetValue(tierName, out var td)
            && td.Attributes.ContainsKey(attr);
    }

    private static double ComputeClampedTarget(double current, string op, double rawValue, double min, double max)
    {
        double next = op.ToLowerInvariant() switch
        {
            "set" => rawValue,
            "subtract" => current - rawValue,
            "multiply" => current * Math.Clamp(rawValue, MinMultiplyStep, MaxMultiplyStep),
            "additivemultiply" => current * (1.0 + Math.Clamp(rawValue, -10.0, 10.0)),
            _ => current + rawValue
        };
        if (!double.IsFinite(next)) next = current;
        return Math.Clamp(next, min, max);
    }

    private static double ComputeCardEffectiveNonZeroMult(CardInstance card, string attr)
    {
        static double ReadPosMult(Dictionary<string, double> d, string k)
            => d.TryGetValue(k, out double v) && double.IsFinite(v) && v > 0 ? v : 1.0;

        double total = ReadPosMult(card.PermanentMult, attr)
                     * ReadPosMult(card.DayMult, attr)
                     * ReadPosMult(card.HourMult, attr)
                     * ReadPosMult(card.CombatMult, attr);
        return double.IsFinite(total) && total > 0 ? total : 1.0;
    }

    private static void SanitizeAddLayer(Dictionary<string, double> dict)
    {
        if (dict.Count == 0) return;
        List<string>? bad = null;
        foreach (var (k, v) in dict)
        {
            if (!double.IsFinite(v))
            {
                bad ??= new List<string>();
                bad.Add(k);
            }
        }
        if (bad != null)
        {
            foreach (var k in bad) dict[k] = 0;
        }
    }

    private static void SanitizeMultLayer(Dictionary<string, double> dict)
    {
        if (dict.Count == 0) return;
        List<string>? fix = null;
        foreach (var (k, v) in dict)
        {
            if (!double.IsFinite(v) || (Math.Abs(v) > 1e-12 && (v < MinEffectiveMultiplier || v > MaxEffectiveMultiplier)))
            {
                fix ??= new List<string>();
                fix.Add(k);
            }
        }
        if (fix == null) return;
        foreach (var k in fix)
        {
            double v = dict[k];
            if (!double.IsFinite(v)) dict.Remove(k);
            else if (Math.Abs(v) > 1e-12) dict[k] = Math.Clamp(v, MinEffectiveMultiplier, MaxEffectiveMultiplier);
        }
    }

    private static bool ActionTreeHasActionType(RuleNode? node, string targetActionType, int depth = 0)
    {
        if (node == null || depth > 16) return false;
        if (string.Equals(node.Type, targetActionType, StringComparison.OrdinalIgnoreCase))
            return true;
        if (node.Actions != null)
        {
            for (int i = 0; i < node.Actions.Count; i++)
            {
                if (ActionTreeHasActionType(node.Actions[i], targetActionType, depth + 1))
                    return true;
            }
        }
        return false;
    }
}
