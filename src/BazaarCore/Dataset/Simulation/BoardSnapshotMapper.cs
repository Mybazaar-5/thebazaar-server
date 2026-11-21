using BazaarCore.Data;
using BazaarCore.Runtime;

namespace BazaarCore.Dataset.Simulation;

/// <summary>
/// 运行时参与者状态与 PvP 盘面快照（PvpBoardSnapshot）之间的无损映射工具。
/// </summary>
public static class BoardSnapshotMapper
{
    private static readonly BoardValidator DefaultValidator = new();

    /// <summary>
    /// 将当前发育完成待出战的 RunParticipantState 转换为标准 PvpBoardSnapshot 快照。
    /// </summary>
    public static PvpBoardSnapshot ToSnapshot(
        RunParticipantState participant,
        GameDataRepository repo,
        IBoardValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(repo);

        var ps = participant.PlayerState;
        int day = Math.Max(1, ps.Day);
        string hero = participant.Hero;
        string version = string.IsNullOrWhiteSpace(repo.GameMode.Version) ? "5.0.0" : repo.GameMode.Version;

        int unscaledHp = (int)Math.Round(ps.GetAttributeUnscaled("HealthMax"));
        int healthMax = Math.Max(1, unscaledHp > 0 ? unscaledHp : ps.HealthMax);

        var snapshot = new PvpBoardSnapshot
        {
            BoardId = $"syn_d{day:00}_{hero.ToLowerInvariant()}_{participant.RunId}",
            RunId = participant.RunId,
            Version = version,
            Source = "synthetic_selfplay",
            Persona = participant.Persona,
            Archetype = participant.Archetype.Id,
            Day = day,
            Hero = hero,
            Level = Math.Max(1, ps.Level),
            HealthMax = healthMax,
            Prestige = Math.Max(0, ps.Prestige),
            Wins = Math.Max(0, ps.Wins),
            Losses = Math.Max(0, ps.Losses),
            UnlockedSlotsMask = ps.Hand.UnlockedMask,
            PowerBucket = PowerBucket.Normal,
            EstimatedWinRate = 0.0
        };

        foreach (var card in ps.Hand.Cards.OrderBy(c => c.StartSlot))
        {
            var permAttrs = ExtractPermanentAttributes(card);
            snapshot.Items.Add(new PvpItemSnapshot
            {
                Slot = card.StartSlot,
                Width = card.SlotWidth,
                TemplateId = card.Template.Id,
                InternalName = card.Template.InternalName,
                ZhName = repo.GetLocalizedTitle(card.Template),
                Tier = card.Tier,
                Enchantment = string.IsNullOrWhiteSpace(card.Enchantment) ? null : card.Enchantment,
                PermanentAttributes = permAttrs
            });
        }

        foreach (var skill in ps.Skills)
        {
            snapshot.Skills.Add(new PvpSkillSnapshot
            {
                TemplateId = skill.Template.Id,
                InternalName = skill.Template.InternalName,
                ZhName = repo.GetLocalizedTitle(skill.Template),
                Tier = skill.Tier
            });
        }

        snapshot.BoardSignature = (validator ?? DefaultValidator).ComputeSignature(snapshot);
        return snapshot;
    }

    /// <summary>
    /// 仅提取发生过局内永久成长变化的属性（PermanentAdd 非 0 或 PermanentMult 非 1），
    /// 写入成长后的永久最终基础值，确保 ToPlayerState 按 v - BaseAttributes 往返 100% 无损。
    /// </summary>
    private static Dictionary<string, double> ExtractPermanentAttributes(CardInstance card)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (card.PermanentAdd.Count == 0 && card.PermanentMult.Count == 0)
            return result;

        var changedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in card.PermanentAdd)
        {
            if (Math.Abs(v) > 1e-6)
                changedKeys.Add(k);
        }
        foreach (var (k, m) in card.PermanentMult)
        {
            if (Math.Abs(m - 1.0) > 1e-6)
                changedKeys.Add(k);
        }

        foreach (var attr in changedKeys)
        {
            double unscaled = card.GetAttributeUnscaled(attr);
            if (card.PermanentMult.TryGetValue(attr, out var mult) && Math.Abs(mult - 1.0) > 1e-6)
                unscaled *= mult;

            result[attr] = Math.Round(unscaled, 4);
        }

        return result;
    }
}
