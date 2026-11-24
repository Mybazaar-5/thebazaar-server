using System;
using System.Collections.Concurrent;
using System.Reflection;
using BazaarLauncher.Modules;

namespace BazaarLauncher.Core;

/// <summary>
/// P0-20 卡牌逻辑身份解析器：
/// 将借用同一官方底座模板 GUID 的不同自定义 Mod 卡按 CustomCardId 严格隔离，
/// 同时保留原版卡与原位 Patch 卡的原版逻辑身份，防止融合光圈、融合判定与升阶对比跨卡串味。
/// </summary>
internal static class CardIdentityResolver
{
    private sealed class CardPropCache
    {
        public PropertyInfo InstanceId;
        public PropertyInfo TemplateId;
        public PropertyInfo Id;
    }

    private static readonly ConcurrentDictionary<Type, CardPropCache> PropCache = new();

    /// <summary>
    /// 零 Unity 依赖纯逻辑身份计算核心（供单元测试与运行时共用）。
    /// </summary>
    internal static string ResolveLogicalIdentity(string templateId, string customCardId, bool isPatchOverride)
    {
        string tpl = (templateId ?? string.Empty).Trim();
        string cid = (customCardId ?? string.Empty).Trim();
        if (!isPatchOverride &&
            !string.IsNullOrEmpty(cid) &&
            !string.Equals(cid, tpl, StringComparison.OrdinalIgnoreCase))
        {
            return "custom:" + cid;
        }

        return "vanilla:" + tpl;
    }

    /// <summary>
    /// 解析运行时任意 Card / ItemCard / SkillCard 对象的逻辑身份。
    /// </summary>
    internal static string ResolveLogicalCardIdentity(object card)
    {
        if (card == null) return "vanilla:";

        ExtractIds(card, out string instanceId, out string templateId);

        string previewCustomId = CustomContentVisualModule.TryGetPreviewCustomCardId(card);
        string customId = previewCustomId;
        bool isPatch = false;

        var l10n = LocalizationOverlayModule.Instance;
        if (l10n != null && l10n.TryResolveCardMetadata(instanceId, previewCustomId, templateId, out var ov) && ov != null)
        {
            if (!string.IsNullOrWhiteSpace(ov.CustomCardId))
            {
                customId = ov.CustomCardId;
            }
            isPatch = ov.IsPatchOverride;
        }
        else if (string.IsNullOrWhiteSpace(customId))
        {
            var visualMod = CustomContentVisualModule.Instance;
            if (visualMod != null && visualMod.TryLookupCustomIdentity(instanceId, templateId, out string foundCustomId, out bool foundPatch))
            {
                customId = foundCustomId;
                isPatch = foundPatch;
            }
        }

        return ResolveLogicalIdentity(templateId, customId, isPatch);
    }

    private static void ExtractIds(object card, out string instanceId, out string templateId)
    {
        instanceId = string.Empty;
        templateId = string.Empty;
        if (card == null) return;

        var cache = PropCache.GetOrAdd(card.GetType(), static t => new CardPropCache
        {
            InstanceId = t.GetProperty("InstanceId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            TemplateId = t.GetProperty("TemplateId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            Id = t.GetProperty("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
        });

        try
        {
            object instObj = cache.InstanceId?.GetValue(card) ?? cache.Id?.GetValue(card);
            if (instObj != null) instanceId = instObj.ToString() ?? string.Empty;
        }
        catch { }

        try
        {
            object tplObj = cache.TemplateId?.GetValue(card);
            if (tplObj != null) templateId = tplObj.ToString() ?? string.Empty;
        }
        catch { }
    }
}
