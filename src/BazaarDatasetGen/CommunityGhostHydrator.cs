using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Dataset.Archetypes;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarDatasetGen;

/// <summary>
/// 方案 B 核心桥梁：将 32 套 5.0.0 校验的社区十胜参考体系多天期（Day 6~16）、多变体水合为标准 PvpBoardSnapshot，
/// 标记 source = "real_community"，送入交叉定级与均衡采样池，与进化合成盘面双源共存。
/// </summary>
public static class CommunityGhostHydrator
{
    public sealed record HydratedCommunityResult(
        IReadOnlyList<PvpRunRecord> CommunityRuns,
        IReadOnlyList<PvpBoardSnapshot> CommunitySnapshots);

    /// <summary>
    /// 基于 CommunityTenWinBlueprints 构建跨 Day 6~16 × 3 变体的社区真值对局轨迹与合法盘面快照。
    /// </summary>
    public static HydratedCommunityResult BuildCommunitySnapshots(
        GameDataRepository repo,
        IBoardValidator validator,
        int baseSeed,
        bool requireOfficialZh = true)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(validator);

        var specs = CommunityTenWinBlueprints.GetAll();
        string version = string.IsNullOrWhiteSpace(repo.GameMode.Version) ? "5.0.0" : repo.GameMode.Version;

        var runs = new List<PvpRunRecord>(specs.Count * 3);
        var snapshots = new List<PvpBoardSnapshot>(specs.Count * 3 * 11);

        for (int variant = 0; variant < 3; variant++)
        {
            var runBoardsBySpec = new Dictionary<string, List<PvpBoardSnapshot>>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in specs)
                runBoardsBySpec[spec.BlueprintId] = new List<PvpBoardSnapshot>();

            for (int day = 6; day <= 16; day++)
            {
                int seed = unchecked(baseSeed + variant * 1009 + day * 37);
                var boards = CommunityTenWinBlueprints.BuildReferenceBoards(repo, day, seed);

                for (int i = 0; i < boards.Count && i < specs.Count; i++)
                {
                    var spec = specs[i];
                    var ps = boards[i];
                    ps.ClearAuraModifiers();
                    foreach (var c in ps.Hand.Cards) c.ClearAuraModifiers();
                    foreach (var s in ps.Skills) s.ClearAuraModifiers();

                    string runId = $"comm_run_v{variant}_{spec.BlueprintId}";
                    string boardId = $"comm_d{day:00}_v{variant}_{spec.BlueprintId}";

                    int unscaledHp = (int)Math.Round(ps.GetAttributeUnscaled("HealthMax"));
                    int healthMax = Math.Max(200, unscaledHp > 0 ? unscaledHp : ps.HealthMax);

                    var snap = new PvpBoardSnapshot
                    {
                        BoardId = boardId,
                        RunId = runId,
                        Version = version,
                        Source = "real_community",
                        Persona = PersonaType.HardForcer,
                        Archetype = $"Comm10W_{spec.BlueprintId}",
                        Day = day,
                        Hero = spec.Hero,
                        Level = Math.Max(1, ps.Level),
                        HealthMax = healthMax,
                        Prestige = Math.Max(1, ps.Prestige),
                        Wins = Math.Max(0, ps.Wins),
                        Losses = Math.Max(0, ps.Losses),
                        UnlockedSlotsMask = ps.Hand.UnlockedMask,
                        PowerBucket = PowerBucket.Strong,
                        EstimatedWinRate = 0.72
                    };

                    foreach (var card in ps.Hand.Cards.OrderBy(c => c.StartSlot))
                    {
                        snap.Items.Add(new PvpItemSnapshot
                        {
                            Slot = card.StartSlot,
                            Width = card.SlotWidth,
                            TemplateId = card.Template.Id,
                            InternalName = card.Template.InternalName,
                            ZhName = repo.GetLocalizedTitle(card.Template),
                            Tier = card.Tier,
                            Enchantment = string.IsNullOrWhiteSpace(card.Enchantment) ? null : card.Enchantment,
                            PermanentAttributes = ExtractPermAttrs(card)
                        });
                    }

                    foreach (var skill in ps.Skills)
                    {
                        snap.Skills.Add(new PvpSkillSnapshot
                        {
                            TemplateId = skill.Template.Id,
                            InternalName = skill.Template.InternalName,
                            ZhName = repo.GetLocalizedTitle(skill.Template),
                            Tier = skill.Tier
                        });
                    }

                    snap.BoardSignature = validator.ComputeSignature(snap);
                    var vr = validator.Validate(snap, repo, requireOfficialZh);
                    if (!vr.IsValid)
                        continue;

                    runBoardsBySpec[spec.BlueprintId].Add(snap);
                    snapshots.Add(snap);
                }
            }

            foreach (var spec in specs)
            {
                var dayBoards = runBoardsBySpec[spec.BlueprintId];
                if (dayBoards.Count == 0) continue;

                var last = dayBoards[^1];
                runs.Add(new PvpRunRecord
                {
                    RunId = $"comm_run_v{variant}_{spec.BlueprintId}",
                    Version = version,
                    Source = "real_community",
                    Hero = spec.Hero,
                    Persona = PersonaType.HardForcer,
                    Archetype = $"Comm10W_{spec.BlueprintId}",
                    FinalDay = last.Day,
                    FinalWins = Math.Max(10, last.Wins),
                    FinalLosses = last.Losses,
                    IsVictory = true,
                    DayBoards = dayBoards
                });
            }
        }

        return new HydratedCommunityResult(runs, snapshots);
    }

    private static Dictionary<string, double> ExtractPermAttrs(CardInstance card)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (card.PermanentAdd.Count == 0 && card.PermanentMult.Count == 0)
            return result;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in card.PermanentAdd)
            if (Math.Abs(v) > 1e-6) keys.Add(k);
        foreach (var (k, m) in card.PermanentMult)
            if (Math.Abs(m - 1.0) > 1e-6) keys.Add(k);

        foreach (var attr in keys)
        {
            double unscaled = card.GetAttributeUnscaled(attr);
            if (card.PermanentMult.TryGetValue(attr, out var mult) && Math.Abs(mult - 1.0) > 1e-6)
                unscaled *= mult;
            result[attr] = Math.Round(unscaled, 4);
        }
        return result;
    }
}
