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

        // 附加 Quest 进度（P2-02：优先渲染任务条目本地化目标与 Reward.Localization，缺省再回退 AttributeType）
        if (card.Template.Quests != null)
        {
            foreach (var q in card.Template.Quests)
            {
                foreach (var entry in q.Entries)
                {
                    double prog = card.GetQuestProgress(entry.AttributeType);
                    bool done = card.IsQuestCompleted(entry.AttributeType);

                    string goalText = FormatQuestGoalTextInternal(card, entry, loc, ctx);
                    string rewardText = FormatQuestRewardTextInternal(card, entry, loc, ctx);

                    if (!string.IsNullOrWhiteSpace(goalText))
                    {
                        string progressTag = done
                            ? $"[任务已完成] {goalText} ({prog:0}/{entry.Target:0})"
                            : $"[任务进度] {goalText} ({prog:0}/{entry.Target:0})";
                        lines.Add(!string.IsNullOrWhiteSpace(rewardText)
                            ? $"{progressTag} → {rewardText}"
                            : progressTag);
                    }
                    else
                    {
                        string fallback = done
                            ? $"[任务已完成] {entry.AttributeType} ({prog:0}/{entry.Target:0})"
                            : $"[任务进度] {entry.AttributeType}: {prog:0}/{entry.Target:0}";
                        lines.Add(!string.IsNullOrWhiteSpace(rewardText)
                            ? $"{fallback} → {rewardText}"
                            : fallback);
                    }
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// 渲染单条任务的目标文本（优先官方中文词库并动态解析占位符；缺省返回空字符串）。
    /// </summary>
    public static string FormatQuestGoalText(CardInstance card, QuestEntry entry, ILocalizationStore? loc = null)
        => FormatQuestGoalTextInternal(card, entry, loc, null);

    /// <summary>
    /// 渲染单条任务的奖励文本；若任务尚未完成，内部构建无副作用隔离预览副本计算奖励数值，绝不修改原 <paramref name="card"/>。
    /// </summary>
    public static string FormatQuestRewardText(CardInstance card, QuestEntry entry, ILocalizationStore? loc = null)
        => FormatQuestRewardTextInternal(card, entry, loc, null);

    /// <summary>
    /// 渲染指定卡牌模板在给定品阶下某附魔的效果摘要文本（不含 "[附魔名]" 前缀）；附魔不存在或无文案时返回空字符串。
    /// </summary>
    public static string FormatEnchantmentSummary(CardTemplate tpl, string enchantment, CardTier tier, ILocalizationStore? loc = null)
    {
        if (tpl == null || string.IsNullOrWhiteSpace(enchantment) || tpl.Enchantments == null)
            return string.Empty;

        string? canonKey = null;
        EnchantmentData? ench = null;
        if (tpl.Enchantments.TryGetValue(enchantment, out var direct))
        {
            canonKey = enchantment;
            ench = direct;
        }
        else
        {
            foreach (var kv in tpl.Enchantments)
            {
                if (string.Equals(kv.Key, enchantment, StringComparison.OrdinalIgnoreCase))
                {
                    canonKey = kv.Key;
                    ench = kv.Value;
                    break;
                }
            }
        }

        if (ench?.Localization == null || string.IsNullOrEmpty(canonKey))
            return string.Empty;

        var preview = new CardInstance(tpl, tier, canonKey);
        return RenderLocalizedBlockFirstLine(ench.Localization, loc, preview, null) ?? string.Empty;
    }

    /// <summary>
    /// 查找指定卡牌模板中不可重复任务的目标进度值（严格跳过 <see cref="QuestDefinition.EffectiveRepeatable"/> 为 true 的任务组）。
    /// 命中时返回 <c>Math.Max(1, entry.Target)</c>；未命中或属于可重复任务组时返回 false。
    /// </summary>
    public static bool TryFindNonRepeatableQuestTarget(CardTemplate? tpl, string? questAttr, out double target)
    {
        target = 0;
        if (tpl?.Quests == null || string.IsNullOrWhiteSpace(questAttr))
            return false;

        for (int qi = 0; qi < tpl.Quests.Count; qi++)
        {
            var group = tpl.Quests[qi];
            if (group.EffectiveRepeatable || group.Entries == null)
                continue;

            for (int ei = 0; ei < group.Entries.Count; ei++)
            {
                var entry = group.Entries[ei];
                if (string.Equals(entry.AttributeType, questAttr, StringComparison.OrdinalIgnoreCase))
                {
                    target = Math.Max(1, entry.Target);
                    return true;
                }
            }
        }

        return false;
    }

    private static string FormatQuestGoalTextInternal(
        CardInstance card,
        QuestEntry entry,
        ILocalizationStore? loc,
        RuleExecutionContext? ctx)
    {
        if (card == null || entry == null) return string.Empty;
        var effectiveLoc = loc ?? ctx?.Repo?.Localization;
        return RenderLocalizedBlockFirstLine(entry.Localization, effectiveLoc, card, ctx) ?? string.Empty;
    }

    private static string FormatQuestRewardTextInternal(
        CardInstance card,
        QuestEntry entry,
        ILocalizationStore? loc,
        RuleExecutionContext? ctx)
    {
        if (card == null || entry?.Reward?.Localization == null) return string.Empty;
        var effectiveLoc = loc ?? ctx?.Repo?.Localization;
        bool done = card.IsQuestCompleted(entry.AttributeType);
        CardInstance evalCard = !done ? BuildQuestRewardPreviewCard(card, entry) : card;
        return RenderLocalizedBlockFirstLine(entry.Reward.Localization, effectiveLoc, evalCard, ctx) ?? string.Empty;
    }

    /// <summary>
    /// 为未完成任务的奖励文本构造轻量级无副作用隔离预览副本：
    /// 保留原卡品阶、附魔、既有已完成任务与持久层修正，仅临时加入当前任务以计算准确奖励数值，绝不修改原 <paramref name="card"/>。
    /// </summary>
    private static CardInstance BuildQuestRewardPreviewCard(CardInstance card, QuestEntry entry)
    {
        var preview = new CardInstance(card.Template, card.Tier, card.Enchantment, card.InstanceId)
        {
            AttachedStandardPrices = card.AttachedStandardPrices
        };

        foreach (string q in card.CompletedQuests)
            preview.CompletedQuests.Add(q);
        foreach (string q in card.CombatCompletedQuests)
            preview.CombatCompletedQuests.Add(q);
        if (!string.IsNullOrWhiteSpace(entry.AttributeType))
            preview.CompletedQuests.Add(entry.AttributeType);

        preview.RebuildTierAndEnchantmentData();

        CopyDict(card.PermanentAdd, preview.PermanentAdd);
        CopyDict(card.PermanentMult, preview.PermanentMult);
        CopyDict(card.PermanentAdditiveMult, preview.PermanentAdditiveMult);
        CopyDict(card.DayAdd, preview.DayAdd);
        CopyDict(card.DayMult, preview.DayMult);
        CopyDict(card.DayAdditiveMult, preview.DayAdditiveMult);
        CopyDict(card.HourAdd, preview.HourAdd);
        CopyDict(card.HourMult, preview.HourMult);
        CopyDict(card.HourAdditiveMult, preview.HourAdditiveMult);
        CopyDict(card.CombatAdd, preview.CombatAdd);
        CopyDict(card.CombatMult, preview.CombatMult);
        CopyDict(card.CombatAdditiveMult, preview.CombatAdditiveMult);
        CopyDict(card.AuraAdd, preview.AuraAdd);
        CopyDict(card.AuraMult, preview.AuraMult);
        CopyDict(card.AuraAdditiveMult, preview.AuraAdditiveMult);
        CopyDict(card.QuestProgress, preview.QuestProgress);
        CopyDict(card.CombatQuestProgress, preview.CombatQuestProgress);
        foreach (string tag in card.ExtraTags) preview.ExtraTags.Add(tag);
        foreach (string tag in card.DayTags) preview.DayTags.Add(tag);
        foreach (string tag in card.HourTags) preview.HourTags.Add(tag);
        foreach (string tag in card.CombatTags) preview.CombatTags.Add(tag);
        foreach (string tag in card.AuraTags) preview.AuraTags.Add(tag);
        preview.MarkAttributeDirty();
        return preview;
    }

    private static void CopyDict(Dictionary<string, double> src, Dictionary<string, double> dst)
    {
        if (src.Count == 0) return;
        foreach (var (k, v) in src)
            dst[k] = v;
    }

    private static string? RenderLocalizedBlockFirstLine(
        LocalizationBlock? block,
        ILocalizationStore? loc,
        CardInstance card,
        RuleExecutionContext? ctx)
    {
        if (block == null) return null;
        if (block.Tooltips is { Count: > 0 })
        {
            var renderedParts = new List<string>(block.Tooltips.Count);
            for (int i = 0; i < block.Tooltips.Count; i++)
            {
                var raw = loc?.GetLocalizedText(block.Tooltips[i].Content) ?? block.Tooltips[i].Content?.Text;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                renderedParts.Add(RenderTemplate(raw, card, ctx, ContainsCjk(raw)));
            }
            if (renderedParts.Count > 0)
                return string.Join(" ", renderedParts);
        }

        var desc = loc?.GetLocalizedText(block.Description) ?? block.Description?.Text;
        if (!string.IsNullOrWhiteSpace(desc))
            return RenderTemplate(desc, card, ctx, ContainsCjk(desc));

        var title = loc?.GetLocalizedText(block.Title) ?? block.Title?.Text;
        if (!string.IsNullOrWhiteSpace(title))
            return RenderTemplate(title, card, ctx, ContainsCjk(title));

        return null;
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
            else if (kind.Equals("attr", StringComparison.OrdinalIgnoreCase))
            {
                // V13.1：兼容自定义 Mod 卡与客户端 LocalizationOverlayModule 的 {attr.AttributeName} 四阶属性模板占位符
                bool hasDeclared = card.BaseAttributes.ContainsKey(id)
                    || card.CombatAdd.ContainsKey(id)
                    || card.AuraAdd.ContainsKey(id)
                    || card.PermanentAdd.ContainsKey(id)
                    || card.DayAdd.ContainsKey(id)
                    || card.HourAdd.ContainsKey(id);
                double v = card.GetAttribute(id);
                if (!hasDeclared && Math.Abs(v) <= 1e-6)
                    return "?";

                if (IsDurationMsAttribute(id) && Math.Abs(v) >= 100)
                {
                    double sec = v / 1000.0;
                    if (hasUnitSuffix) return $"{sec:0.##}";
                    return isZh ? $"{sec:0.##}秒" : $"{sec:0.##}s";
                }
                return $"{Math.Round(v, 1):0.#}";
            }
        }

        // 兜底尝试按属性直接读取
        double direct = card.GetAttribute(token);
        if (Math.Abs(direct) > 1e-6) return $"{Math.Round(direct):0.##}";
        return "?";
    }

    private static bool IsDurationMsAttribute(string attrName)
        => attrName.Contains("Cooldown", StringComparison.OrdinalIgnoreCase)
           || attrName.Equals("HasteAmount", StringComparison.OrdinalIgnoreCase)
           || attrName.Equals("SlowAmount", StringComparison.OrdinalIgnoreCase)
           || attrName.Equals("FreezeAmount", StringComparison.OrdinalIgnoreCase)
           || attrName.Equals("ChargeAmount", StringComparison.OrdinalIgnoreCase)
           || attrName.EndsWith("Duration", StringComparison.OrdinalIgnoreCase);

    private static RuleNode? ResolveAbilityAction(CardInstance card, string id)
    {
        // D1-P2-02：优先扫描已激活能力（含已完成 Quest 奖励能力与附魔能力），再回退模板/附魔定义
        for (int i = 0; i < card.ActiveAbilities.Count; i++)
        {
            var actAb = card.ActiveAbilities[i];
            if (string.Equals(actAb.Id, id, StringComparison.Ordinal) && actAb.Action != null)
                return actAb.Action;
        }
        if (card.Template.Abilities.TryGetValue(id, out var ab) && ab.Action != null)
            return ab.Action;
        if (!string.IsNullOrEmpty(card.Enchantment) &&
            card.Template.Enchantments != null &&
            card.Template.Enchantments.TryGetValue(card.Enchantment, out var ench) &&
            ench.Abilities.TryGetValue(id, out var eab) && eab.Action != null)
            return eab.Action;
        // V15.6 B1：第 4 级回退扫描任务奖励能力与一次性完成效果（对齐客户端 TooltipComponentAbility）
        if (card.Template.Quests != null)
        {
            for (int qi = 0; qi < card.Template.Quests.Count; qi++)
            {
                var entries = card.Template.Quests[qi].Entries;
                if (entries == null) continue;
                for (int ei = 0; ei < entries.Count; ei++)
                {
                    var entry = entries[ei];
                    var rewardAbilities = entry.Reward?.Abilities;
                    if (rewardAbilities != null)
                    {
                        if (rewardAbilities.TryGetValue(id, out var qab) && qab.Action != null)
                            return qab.Action;
                        foreach (var qabVal in rewardAbilities.Values)
                        {
                            if (string.Equals(qabVal.Id, id, StringComparison.Ordinal) && qabVal.Action != null)
                                return qabVal.Action;
                        }
                    }

                    var completionEffects = entry.CompletionEffects;
                    if (completionEffects != null)
                    {
                        if (completionEffects.TryGetValue(id, out var cab) && cab.Action != null)
                            return cab.Action;
                        foreach (var cabVal in completionEffects.Values)
                        {
                            if (string.Equals(cabVal.Id, id, StringComparison.Ordinal) && cabVal.Action != null)
                                return cabVal.Action;
                        }
                    }
                }
            }
        }
        return null;
    }

    private static RuleNode? ResolveAuraAction(CardInstance card, string id)
    {
        // D1-P2-02：优先扫描已激活光环（含已完成 Quest 奖励光环与附魔光环）
        for (int i = 0; i < card.ActiveAuras.Count; i++)
        {
            var actAu = card.ActiveAuras[i];
            if (string.Equals(actAu.Id, id, StringComparison.Ordinal) && actAu.Action != null)
                return actAu.Action;
        }
        if (card.Template.Auras.TryGetValue(id, out var au) && au.Action != null)
            return au.Action;
        if (!string.IsNullOrEmpty(card.Enchantment) &&
            card.Template.Enchantments != null &&
            card.Template.Enchantments.TryGetValue(card.Enchantment, out var ench) &&
            ench.Auras.TryGetValue(id, out var eau) && eau.Action != null)
            return eau.Action;
        // V15.6 B1：第 4 级回退扫描任务奖励光环（对齐客户端 TooltipComponentAura）
        if (card.Template.Quests != null)
        {
            for (int qi = 0; qi < card.Template.Quests.Count; qi++)
            {
                var entries = card.Template.Quests[qi].Entries;
                if (entries == null) continue;
                for (int ei = 0; ei < entries.Count; ei++)
                {
                    var rewardAuras = entries[ei].Reward?.Auras;
                    if (rewardAuras != null)
                    {
                        if (rewardAuras.TryGetValue(id, out var qau) && qau.Action != null)
                            return qau.Action;
                        foreach (var qauVal in rewardAuras.Values)
                        {
                            if (string.Equals(qauVal.Id, id, StringComparison.Ordinal) && qauVal.Action != null)
                                return qauVal.Action;
                        }
                    }
                }
            }
        }
        return null;
    }

    private static string FormatActionToken(RuleNode action, string? subKind, CardInstance card, RuleExecutionContext? ctx, bool hasUnitSuffix, bool isZh)
    {
        var evalCtx = BuildEvalCtx(card, ctx);
        if (string.Equals(subKind, "targets", StringComparison.OrdinalIgnoreCase))
        {
            double targets;
            if (action.TargetCount != null)
            {
                targets = ValueAndConditionEvaluator.EvaluateValue(action.TargetCount, evalCtx, 1);
            }
            else
            {
                // D1-P2-02：缺少显式 TargetCount 时回退检索对应动作的 *Targets 卡面属性
                string? fallbackTargetsAttr = ResolveFallbackTargetsAttribute(action.Type);
                targets = (!string.IsNullOrEmpty(fallbackTargetsAttr) && card.HasAttribute(fallbackTargetsAttr))
                    ? card.GetAttribute(fallbackTargetsAttr)
                    : 1;
            }
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

    private static string? ResolveFallbackTargetsAttribute(string? actionType) => actionType switch
    {
        "TActionCardHaste" => "HasteTargets",
        "TActionCardSlow" => "SlowTargets",
        "TActionCardFreeze" => "FreezeTargets",
        "TActionCardCharge" => "ChargeTargets",
        "TActionCardReload" => "ReloadTargets",
        "TActionCardBeginFlying" or "TActionCardStopFlying" or "TActionCardToggleFlying" => "FlyingTargets",
        "TActionCardForceUse" => "ForceUseTargets",
        "TActionCardUpgrade" => "UpgradeTargets",
        "TActionCardDestroy" => "DestroyTargets",
        "TActionCardDisable" => "DisableTargets",
        "TActionCardEnchant" or "TActionCardEnchantRandom" => "EnchantTargets",
        "TActionCardTransform" => "TransformTargets",
        _ => null
    };

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

        if (action.SpawnContext?.Limit != null)
            return ValueAndConditionEvaluator.EvaluateValue(action.SpawnContext.Limit, evalCtx);

        if (action.Limit != null)
            return ValueAndConditionEvaluator.EvaluateValue(action.Limit, evalCtx);

        if (action.AttributeType != null)
            return card.GetAttribute(action.AttributeType);

        // 动作本身没写数值时，和战斗结算一样回落到卡面的默认属性（例如加速读 HasteAmount）
        string? fallbackAttr = ResolveFallbackAmountAttribute(action.Type);
        if (fallbackAttr != null && card.HasAttribute(fallbackAttr))
            return card.GetAttribute(fallbackAttr);

        return null;
    }

    /// <summary>与 AbilityExecutor 结算时的默认数值属性保持一致。</summary>
    private static string? ResolveFallbackAmountAttribute(string? actionType) => actionType switch
    {
        "TActionPlayerDamage" => "DamageAmount",
        "TActionPlayerShieldApply" => "ShieldApplyAmount",
        "TActionPlayerBurnApply" => "BurnApplyAmount",
        "TActionPlayerPoisonApply" => "PoisonApplyAmount",
        "TActionPlayerHeal" or "TActionPlayerReviveHeal" => "HealAmount",
        "TActionPlayerRegenApply" => "RegenApplyAmount",
        "TActionPlayerRageApply" => "RageApplyAmount",
        "TActionPlayerRageRemove" => "RageRemoveAmount",
        "TActionPlayerJoyApply" => "JoyApplyAmount",
        "TActionPlayerJoyRemove" => "JoyRemoveAmount",
        "TActionPlayerTempoApply" => "TempoApplyAmount",
        "TActionPlayerTempoRemove" => "TempoRemoveAmount",
        "TActionPlayerBurnRemove" => "BurnRemoveAmount",
        "TActionPlayerPoisonRemove" => "PoisonRemoveAmount",
        "TActionPlayerShieldRemove" => "ShieldRemoveAmount",
        "TActionPlayerRegenRemove" => "RegenRemoveAmount",
        "TActionCardHaste" => "HasteAmount",
        "TActionCardSlow" => "SlowAmount",
        "TActionCardFreeze" => "FreezeAmount",
        "TActionCardCharge" => "ChargeAmount",
        "TActionCardReload" => "ReloadAmount",
        _ => null,
    };

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
