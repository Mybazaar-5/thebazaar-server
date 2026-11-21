using System.Text.RegularExpressions;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Engine;

/// <summary>
/// 羊皮纸 Tooltip 动态数值文本渲染器：优先使用官方中文词库将 {ability.x} / {aura.x} / {item.x} 解析为实时数值并附带附魔说明。
/// </summary>
public static partial class TooltipFormatter
{
    [GeneratedRegex(@"\{([^}]+)\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// 渲染卡牌实例的 Tooltip 文本列表；若提供 <paramref name="localization"/> 或 <c>ctx?.Repo?.Localization</c> 则优先输出官方中文译文。
    /// </summary>
    public static List<string> FormatTooltips(CardInstance card, RuleExecutionContext? ctx = null, ILocalizationStore? localization = null)
    {
        var lines = new List<string>();
        var loc = localization ?? ctx?.Repo?.Localization;
        var tooltips = card.Template.Localization?.Tooltips;

        if (tooltips != null && tooltips.Count > 0)
        {
            var indices = card.Template.Tiers != null && card.Template.Tiers.TryGetValue(card.Tier.ToString(), out var td) && td.TooltipIds.Count > 0
                ? td.TooltipIds
                : Enumerable.Range(0, tooltips.Count).ToList();

            foreach (int idx in indices.Distinct())
            {
                if (idx < 0 || idx >= tooltips.Count) continue;
                var entry = tooltips[idx];
                var raw = loc?.GetLocalizedText(entry.Content) ?? entry.Content?.Text;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                bool isZh = ContainsCjk(raw);
                var rendered = RenderTemplate(raw, card, ctx, isZh);
                var prefix = entry.TooltipType switch
                {
                    "Active" => "[主动] ",
                    "Passive" => "[被动] ",
                    _ => ""
                };
                lines.Add(prefix + rendered);
            }
        }
        else
        {
            var desc = loc?.GetLocalizedText(card.Template.Localization?.Description) ?? card.Template.Localization?.Description?.Text;
            if (!string.IsNullOrWhiteSpace(desc))
            {
                bool isZh = ContainsCjk(desc);
                lines.Add(RenderTemplate(desc, card, ctx, isZh));
            }
        }

        // 附加当前附魔效果说明
        if (!string.IsNullOrEmpty(card.Enchantment) &&
            card.Template.Enchantments != null &&
            card.Template.Enchantments.TryGetValue(card.Enchantment, out var ench) &&
            ench.Localization?.Tooltips != null)
        {
            foreach (var et in ench.Localization.Tooltips)
            {
                var raw = loc?.GetLocalizedText(et.Content) ?? et.Content?.Text;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    bool isZh = ContainsCjk(raw);
                    var rendered = RenderTemplate(raw, card, ctx, isZh);
                    lines.Add($"[{EnchantmentNames.GetChineseName(card.Enchantment)}] {rendered}");
                }
            }
        }

        // 附加 Quest 进度
        if (card.Template.Quests != null)
        {
            foreach (var q in card.Template.Quests)
            {
                foreach (var entry in q.Entries)
                {
                    double prog = card.QuestProgress.GetValueOrDefault(entry.AttributeType, 0);
                    bool done = card.CompletedQuests.Contains(entry.AttributeType);
                    lines.Add(done
                        ? $"[任务已完成] {entry.AttributeType} ({prog:0}/{entry.Target:0})"
                        : $"[任务进度] {entry.AttributeType}: {prog:0}/{entry.Target:0}");
                }
            }
        }

        return lines;
    }

    private static string RenderTemplate(string raw, CardInstance card, RuleExecutionContext? ctx, bool isZh)
    {
        return PlaceholderRegex().Replace(raw, m =>
        {
            var tail = raw.AsSpan(m.Index + m.Length).TrimStart();
            bool hasUnitSuffix = tail.StartsWith("秒") || tail.StartsWith("second", StringComparison.OrdinalIgnoreCase);
            return ResolvePlaceholder(m.Groups[1].Value, card, ctx, hasUnitSuffix, isZh);
        });
    }

    private static string ResolvePlaceholder(string token, CardInstance card, RuleExecutionContext? ctx, bool hasUnitSuffix, bool isZh)
    {
        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            string kind = parts[0];
            string id = parts[1];
            string? subKind = parts.Length >= 3 ? parts[2] : null;

            if (kind.Equals("ability", StringComparison.OrdinalIgnoreCase))
            {
                var action = ResolveAbilityAction(card, id);
                if (action != null)
                    return FormatActionToken(action, subKind, card, ctx, hasUnitSuffix, isZh);
            }
            else if (kind.Equals("aura", StringComparison.OrdinalIgnoreCase))
            {
                var action = ResolveAuraAction(card, id);
                if (action != null)
                    return FormatActionToken(action, subKind, card, ctx, hasUnitSuffix, isZh);
            }
            else if (kind.Equals("item", StringComparison.OrdinalIgnoreCase))
            {
                var v = card.GetAttribute(id);
                if (id.Contains("Cooldown", StringComparison.OrdinalIgnoreCase))
                    return hasUnitSuffix ? $"{v / 1000.0:0.##}" : $"{v / 1000.0:0.##}s";
                return $"{Math.Round(v):0.##}";
            }
        }

        // 兜底尝试按属性直接读取
        double direct = card.GetAttribute(token);
        if (Math.Abs(direct) > 1e-6) return $"{Math.Round(direct):0.##}";
        return "?";
    }

    private static RuleNode? ResolveAbilityAction(CardInstance card, string id)
    {
        if (card.Template.Abilities.TryGetValue(id, out var ab) && ab.Action != null)
            return ab.Action;
        if (!string.IsNullOrEmpty(card.Enchantment) &&
            card.Template.Enchantments != null &&
            card.Template.Enchantments.TryGetValue(card.Enchantment, out var ench) &&
            ench.Abilities.TryGetValue(id, out var eab) && eab.Action != null)
            return eab.Action;
        return null;
    }

    private static RuleNode? ResolveAuraAction(CardInstance card, string id)
    {
        if (card.Template.Auras.TryGetValue(id, out var au) && au.Action != null)
            return au.Action;
        if (!string.IsNullOrEmpty(card.Enchantment) &&
            card.Template.Enchantments != null &&
            card.Template.Enchantments.TryGetValue(card.Enchantment, out var ench) &&
            ench.Auras.TryGetValue(id, out var eau) && eau.Action != null)
            return eau.Action;
        return null;
    }

    private static string FormatActionToken(RuleNode action, string? subKind, CardInstance card, RuleExecutionContext? ctx, bool hasUnitSuffix, bool isZh)
    {
        var evalCtx = BuildEvalCtx(card, ctx);
        if (string.Equals(subKind, "targets", StringComparison.OrdinalIgnoreCase))
        {
            double targets = action.TargetCount != null
                ? ValueAndConditionEvaluator.EvaluateValue(action.TargetCount, evalCtx, 1)
                : 1;
            return $"{Math.Max(1, Math.Round(targets)):0}";
        }

        if (string.Equals(subKind, "mod", StringComparison.OrdinalIgnoreCase))
        {
            var baseNode = action.ReferenceValue ?? action.GetValueNode(Data.GameDatabaseLoader.JsonOptions);
            var modValNode = baseNode?.Modifier?.Value ?? action.Modifier?.Value;
            if (modValNode != null)
            {
                double modVal = ValueAndConditionEvaluator.EvaluateValue(modValNode, evalCtx);
                return $"{Math.Round(modVal, 1):0.#}";
            }
        }

        var val = ExtractActionDisplayNumber(action, card, evalCtx);
        return val.HasValue ? FormatNumber(val.Value, action, hasUnitSuffix, isZh) : "?";
    }

    private static RuleExecutionContext BuildEvalCtx(CardInstance card, RuleExecutionContext? ctx)
    {
        return ctx != null
            ? ctx.WithCards(card, card)
            : new RuleExecutionContext(null!, RuleScope.OutOfCombat, new PlayerState("tmp", "tmp", HeroNames.Common)).WithCards(card, card);
    }

    private static double? ExtractActionDisplayNumber(RuleNode action, CardInstance card, RuleExecutionContext evalCtx)
    {
        if (action.ReferenceValue != null)
            return ValueAndConditionEvaluator.EvaluateValue(action.ReferenceValue, evalCtx);

        var valNode = action.GetValueNode(Data.GameDatabaseLoader.JsonOptions);
        if (valNode != null)
            return ValueAndConditionEvaluator.EvaluateValue(valNode, evalCtx);

        var fixedNum = action.GetFixedNumericValue();
        if (fixedNum.HasValue) return fixedNum.Value;

        if (action.AttributeType != null)
            return card.GetAttribute(action.AttributeType);

        return null;
    }

    private static string FormatNumber(double val, RuleNode action, bool hasUnitSuffix, bool isZh)
    {
        if (action.Type is "TActionCardHaste" or "TActionCardSlow" or "TActionCardFreeze" or "TActionCardCharge"
            || (action.AttributeType != null && action.AttributeType.Contains("Cooldown", StringComparison.OrdinalIgnoreCase)))
        {
            if (Math.Abs(val) >= 100)
            {
                double sec = val / 1000.0;
                if (hasUnitSuffix) return $"{sec:0.##}";
                return isZh ? $"{sec:0.##}秒" : $"{sec:0.##} second(s)";
            }
        }
        return $"{Math.Round(val, 1):0.#}";
    }

    private static bool ContainsCjk(string text)
    {
        foreach (char ch in text)
        {
            if (ch >= '\u4E00' && ch <= '\u9FFF') return true;
        }
        return false;
    }
}
