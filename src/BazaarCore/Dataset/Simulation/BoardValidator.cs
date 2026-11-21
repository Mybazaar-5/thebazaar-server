using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Dataset.Simulation;

/// <summary>
/// PvP 盘面快照校验器、运行时状态还原器与去重指纹生成器。
/// 线程安全无状态实现，支持高并发淘汰赛与质量验收调用。
/// </summary>
public sealed class BoardValidator : IBoardValidator
{
    private static readonly HashSet<string> PlayableHeroSet = new(HeroNames.PlayableHeroes, StringComparer.Ordinal);
    private static readonly HashSet<string> ValidEnchantments = new(EnchantmentNames.All, StringComparer.Ordinal);

    /// <summary>
    /// 初始化盘面校验器实例。
    /// </summary>
    public BoardValidator()
    {
    }

    /// <summary>
    /// 校验 PvP 盘面快照的英雄、属性范围、卡槽掩码、槽位无重叠/越界、品质/附魔合法性与官方中文名称完备性。
    /// </summary>
    public BoardValidationResult Validate(PvpBoardSnapshot snapshot, GameDataRepository repo, bool requireOfficialZh = true)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var errors = new List<string>();
        if (snapshot == null)
        {
            errors.Add("盘面快照不能为空。");
            return new BoardValidationResult { IsValid = false, Errors = errors };
        }

        if (string.IsNullOrWhiteSpace(snapshot.Hero) || !PlayableHeroSet.Contains(snapshot.Hero))
            errors.Add($"非法英雄名称: '{snapshot.Hero}'，必须属于 PlayableHeroes。");

        if (snapshot.Day < 1)
            errors.Add($"非法天数 Day={snapshot.Day}，必须 >= 1。");

        if (snapshot.Level < 1)
            errors.Add($"非法等级 Level={snapshot.Level}，必须 >= 1。");

        if (snapshot.HealthMax <= 0)
            errors.Add($"非法最大生命 HealthMax={snapshot.HealthMax}，必须 > 0。");

        if (snapshot.Prestige < 0)
            errors.Add($"非法声望 Prestige={snapshot.Prestige}，不能为负数。");

        if (snapshot.UnlockedSlotsMask < 1 || snapshot.UnlockedSlotsMask > 1023)
            errors.Add($"非法槽位掩码 UnlockedSlotsMask={snapshot.UnlockedSlotsMask}，必须在 1..1023 范围内。");

        var occupied = new bool[BoardGrid.Capacity];

        if (snapshot.Items == null)
        {
            errors.Add("Items 列表不能为空。");
        }
        else
        {
            for (int i = 0; i < snapshot.Items.Count; i++)
            {
                var item = snapshot.Items[i];
                if (item == null)
                {
                    errors.Add($"第 {i} 个物品条目为 null。");
                    continue;
                }

                var tpl = repo.GetCard(item.TemplateId);
                if (tpl == null)
                {
                    errors.Add($"物品模板不存在: TemplateId='{item.TemplateId}'。");
                    continue;
                }

                if (!string.Equals(tpl.Type, "Item", StringComparison.Ordinal))
                {
                    errors.Add($"物品模板 '{item.TemplateId}' ({tpl.InternalName}) 的 Type 为 '{tpl.Type}'，而非 'Item'。");
                }

                int expectedWidth = tpl.CardSize.SlotCount();
                if (item.Width != expectedWidth)
                {
                    errors.Add($"物品 '{tpl.InternalName}' 宽度不匹配: 实际 Width={item.Width}，模板要求 {expectedWidth}。");
                }

                if (item.Slot < 0 || item.Width <= 0 || item.Slot + item.Width > BoardGrid.Capacity)
                {
                    errors.Add($"物品 '{tpl.InternalName}' 槽位越界: Slot={item.Slot}, Width={item.Width}。");
                }
                else
                {
                    for (int s = item.Slot; s < item.Slot + item.Width; s++)
                    {
                        if ((snapshot.UnlockedSlotsMask & (1 << s)) == 0)
                            errors.Add($"物品 '{tpl.InternalName}' 占用了未解锁槽位 {s} (Mask={snapshot.UnlockedSlotsMask})。");

                        if (occupied[s])
                            errors.Add($"槽位 {s} 发生物品重叠（物品 '{tpl.InternalName}'）。");

                        occupied[s] = true;
                    }
                }

                if (!Enum.IsDefined(item.Tier))
                {
                    errors.Add($"物品 '{tpl.InternalName}' 品质枚举无效: {item.Tier}。");
                }
                else if (tpl.Tiers != null && tpl.Tiers.Count > 0 && !tpl.Tiers.ContainsKey(item.Tier.ToString()))
                {
                    errors.Add($"物品 '{tpl.InternalName}' 不支持品质 {item.Tier}。");
                }

                if (!string.IsNullOrEmpty(item.Enchantment))
                {
                    if (!ValidEnchantments.Contains(item.Enchantment))
                    {
                        errors.Add($"物品 '{tpl.InternalName}' 包含未知附魔 '{item.Enchantment}'。");
                    }
                    else if (tpl.Enchantments == null || !tpl.Enchantments.ContainsKey(item.Enchantment))
                    {
                        errors.Add($"物品 '{tpl.InternalName}' 模板不支持附魔 '{item.Enchantment}'。");
                    }
                }

                if (requireOfficialZh)
                {
                    if (string.IsNullOrWhiteSpace(item.ZhName))
                        errors.Add($"物品 '{tpl.InternalName}' ({item.TemplateId}) 缺少中文名称 ZhName。");

                    if (repo.Localization != null && !repo.HasOfficialZhTitle(tpl))
                        errors.Add($"物品 '{tpl.InternalName}' ({item.TemplateId}) 在官方词库中缺少中文标题。");
                }
            }
        }

        if (snapshot.Skills == null)
        {
            errors.Add("Skills 列表不能为空。");
        }
        else
        {
            for (int i = 0; i < snapshot.Skills.Count; i++)
            {
                var skill = snapshot.Skills[i];
                if (skill == null)
                {
                    errors.Add($"第 {i} 个技能条目为 null。");
                    continue;
                }

                var tpl = repo.GetCard(skill.TemplateId);
                if (tpl == null)
                {
                    errors.Add($"技能模板不存在: TemplateId='{skill.TemplateId}'。");
                    continue;
                }

                if (!string.Equals(tpl.Type, "Skill", StringComparison.Ordinal))
                {
                    errors.Add($"技能模板 '{skill.TemplateId}' ({tpl.InternalName}) 的 Type 为 '{tpl.Type}'，而非 'Skill'。");
                }

                if (!Enum.IsDefined(skill.Tier))
                {
                    errors.Add($"技能 '{tpl.InternalName}' 品质枚举无效: {skill.Tier}。");
                }
                else if (tpl.Tiers != null && tpl.Tiers.Count > 0 && !tpl.Tiers.ContainsKey(skill.Tier.ToString()))
                {
                    errors.Add($"技能 '{tpl.InternalName}' 不支持品质 {skill.Tier}。");
                }

                if (requireOfficialZh)
                {
                    if (string.IsNullOrWhiteSpace(skill.ZhName))
                        errors.Add($"技能 '{tpl.InternalName}' ({skill.TemplateId}) 缺少中文名称 ZhName。");

                    if (repo.Localization != null && !repo.HasOfficialZhTitle(tpl))
                        errors.Add($"技能 '{tpl.InternalName}' ({skill.TemplateId}) 在官方词库中缺少中文标题。");
                }
            }
        }

        return new BoardValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }

    /// <summary>
    /// 将盘面快照精确还原为可用于 CombatSimulator 的 PlayerState 运行时对象。
    /// 严格按照契约公式 PermanentAdd[k] = v - BaseAttributes.GetValueOrDefault(k, 0) 还原永久成长属性。
    /// </summary>
    public PlayerState ToPlayerState(PvpBoardSnapshot snapshot, GameDataRepository repo)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(repo);

        string playerId = !string.IsNullOrWhiteSpace(snapshot.BoardId)
            ? snapshot.BoardId
            : (!string.IsNullOrWhiteSpace(snapshot.RunId) ? snapshot.RunId : Guid.NewGuid().ToString("N"));

        int healthMax = Math.Max(1, snapshot.HealthMax);
        var state = new PlayerState(
            playerId: playerId,
            displayName: $"{snapshot.Hero}_D{snapshot.Day}",
            hero: snapshot.Hero,
            initialHealthMax: healthMax,
            initialPrestige: Math.Max(0, snapshot.Prestige),
            initialGold: 0,
            unlockedHandSlots: 0)
        {
            Day = Math.Max(1, snapshot.Day),
            Wins = Math.Max(0, snapshot.Wins),
            Losses = Math.Max(0, snapshot.Losses)
        };

        state.Hand.UnlockedMask = snapshot.UnlockedSlotsMask;
        state.Attributes["HealthMax"] = healthMax;
        state.Attributes["Health"] = healthMax;
        state.Level = Math.Max(1, snapshot.Level);
        state.Prestige = Math.Max(0, snapshot.Prestige);

        foreach (var itemSnap in snapshot.Items.OrderBy(i => i.Slot))
        {
            var tpl = repo.GetCard(itemSnap.TemplateId)
                ?? throw new InvalidOperationException($"无法还原物品，找不到模板: {itemSnap.TemplateId}");

            string? ench = string.IsNullOrWhiteSpace(itemSnap.Enchantment) ? null : itemSnap.Enchantment;
            var card = new CardInstance(tpl, itemSnap.Tier, ench);

            if (itemSnap.PermanentAttributes != null)
            {
                foreach (var (attrKey, finalVal) in itemSnap.PermanentAttributes)
                {
                    double baseVal = card.BaseAttributes.GetValueOrDefault(attrKey, 0);
                    double delta = finalVal - baseVal;
                    if (Math.Abs(delta) > 1e-6)
                        card.PermanentAdd[attrKey] = delta;
                }
            }

            if (!state.Hand.CanFitAt(card, itemSnap.Slot) || !state.Hand.TryPlaceOrShift(card, itemSnap.Slot))
            {
                throw new InvalidOperationException(
                    $"无法将物品 '{tpl.InternalName}' 放置到槽位 {itemSnap.Slot} (Width={card.SlotWidth}, Mask={state.Hand.UnlockedMask})。");
            }
        }

        foreach (var skillSnap in snapshot.Skills)
        {
            var tpl = repo.GetCard(skillSnap.TemplateId)
                ?? throw new InvalidOperationException($"无法还原技能，找不到模板: {skillSnap.TemplateId}");

            state.Skills.Add(new CardInstance(tpl, skillSnap.Tier));
        }

        state.PrepareForCombat();
        return state;
    }

    /// <summary>
    /// 按槽位排序的物品结构（TemplateId:Tier:Enchantment）与排序后的技能结构（TemplateId:Tier）生成稳定盘面指纹。
    /// </summary>
    public string ComputeSignature(PvpBoardSnapshot snapshot)
    {
        if (snapshot == null) return string.Empty;

        var itemParts = snapshot.Items
            .OrderBy(i => i.Slot)
            .ThenBy(i => i.TemplateId, StringComparer.Ordinal)
            .ThenBy(i => i.Tier)
            .Select(i => $"{i.TemplateId}:{i.Tier}:{i.Enchantment ?? string.Empty}");

        var skillParts = snapshot.Skills
            .OrderBy(s => s.TemplateId, StringComparer.Ordinal)
            .ThenBy(s => s.Tier)
            .Select(s => $"{s.TemplateId}:{s.Tier}");

        return $"{string.Join("|", itemParts)}||{string.Join("|", skillParts)}";
    }
}
