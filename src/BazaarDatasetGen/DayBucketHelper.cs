using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Runtime;

namespace BazaarDatasetGen;

/// <summary>
/// 15 个天桶（Day 1..14 及 Day 15+ 合并桶）及强度分档与战斗调用辅助方法。
/// </summary>
public static class DayBucketHelper
{
    public const int MinDayBucket = 1;
    public const int MaxDayBucket = 15;

    public static readonly int[] AllDayBuckets = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    public static readonly PowerBucket[] AllPowerBuckets =
    [
        PowerBucket.Weak,
        PowerBucket.Normal,
        PowerBucket.Strong,
        PowerBucket.MetaBoss
    ];

    /// <summary>
    /// 将实际天数映射到 1..15 的标准天桶（Day >= 15 统一归入 15+ 桶）。
    /// </summary>
    public static int ToDayBucket(int day) => day >= MaxDayBucket ? MaxDayBucket : Math.Clamp(day, MinDayBucket, MaxDayBucket);

    /// <summary>
    /// ToDayBucket 的别名方法，供各调度与统计模块统一调用。
    /// </summary>
    public static int GetDayBucket(int day) => ToDayBucket(day);

    /// <summary>
    /// 获取天桶对应的分天 JSON 文件名。
    /// </summary>
    public static string GetDayFileName(int dayBucket) => dayBucket >= MaxDayBucket ? "day_15_plus.json" : $"day_{dayBucket:00}.json";

    /// <summary>
    /// 获取天桶的可读中文/英文标签。
    /// </summary>
    public static string GetDayLabel(int dayBucket) => dayBucket >= MaxDayBucket ? "Day 15+" : $"Day {dayBucket}";

    /// <summary>
    /// 依据交叉验证实测胜率划分为四档强度标签：
    /// Weak (&lt;0.25) / Normal ([0.25, 0.60)) / Strong ([0.60, 0.88)) / MetaBoss (&gt;=0.88)。
    /// </summary>
    public static PowerBucket ClassifyPowerBucket(double estimatedWinRate)
    {
        if (estimatedWinRate < 0.25) return PowerBucket.Weak;
        if (estimatedWinRate < 0.60) return PowerBucket.Normal;
        if (estimatedWinRate < 0.88) return PowerBucket.Strong;
        return PowerBucket.MetaBoss;
    }

    /// <summary>
    /// 无头高性能调用 CombatSimulator（关闭时间轴采样与战斗事件分配以获得最高吞吐）。
    /// </summary>
    public static CombatResult SimulateHeadless(
        GameDataRepository repo,
        PlayerState p1,
        PlayerState p2,
        int seed,
        string combatType = "PVP")
    {
        return CombatSimulator.Simulate(
            repo,
            p1,
            p2,
            seed,
            combatType,
            recordTimeline: false,
            recordEvents: false);
    }

    /// <summary>
    /// 从 CombatResult 判断 P1 是否获胜（含平局时按剩余生命/造成伤害比较，避免双赢或双输）。
    /// </summary>
    public static bool DidPlayer1Win(CombatResult result, string player1Id)
    {
        if (!result.IsDraw && !string.IsNullOrEmpty(result.WinnerPlayerId))
            return string.Equals(result.WinnerPlayerId, player1Id, StringComparison.Ordinal);

        if (result.Player1RemainingHp != result.Player2RemainingHp)
            return result.Player1RemainingHp > result.Player2RemainingHp;

        return result.Player1TotalDamageDealt >= result.Player2TotalDamageDealt;
    }

    /// <summary>
    /// 若双方 PlayerState 的 PlayerId 相同（如镜像或 Stub 场景），复制生成具有独立 PlayerId 的对手状态，
    /// 避免 CombatSimulator 内部 TriggerQueueKey 与 WinnerPlayerId 发生 ID 碰撞。
    /// </summary>
    public static PlayerState EnsureDistinctOpponentState(PlayerState p1, PlayerState p2)
    {
        if (!string.Equals(p1.PlayerId, p2.PlayerId, StringComparison.Ordinal))
            return p2;

        return CloneWithPlayerId(p2, p2.PlayerId + "_opp");
    }

    /// <summary>
    /// 深拷贝 PlayerState 并赋予指定的新 PlayerId，供跨线程只读基准模板及镜像对手复用。
    /// </summary>
    public static PlayerState CloneWithPlayerId(PlayerState src, string newPlayerId)
    {
        var copy = new PlayerState(
            newPlayerId,
            src.DisplayName,
            src.Hero,
            src.HealthMax,
            src.Prestige,
            src.Gold,
            10)
        {
            IsMonster = src.IsMonster,
            IsBot = src.IsBot,
            Day = src.Day,
            Hour = src.Hour,
            Wins = src.Wins,
            Losses = src.Losses
        };
        copy.Hand.UnlockedMask = src.Hand.UnlockedMask;
        foreach (var (k, v) in src.Attributes) copy.Attributes[k] = v;
        foreach (var c in src.Hand.Cards) copy.Hand.Cards.Add(c.CloneForCombat());
        foreach (var c in src.Stash.Cards) copy.Stash.Cards.Add(c.CloneForCombat());
        foreach (var s in src.Skills) copy.Skills.Add(s.CloneForCombat());
        foreach (var se in src.SocketEffects) copy.SocketEffects.Add(se.CloneForCombat());
        copy.PrepareForCombat();
        return copy;
    }

    /// <summary>
    /// 计算两个盘面物品列表之间的 Jaccard 相似度（按 TemplateId:Tier 集合）。
    /// 用于在均衡池采样时过滤近乎完全雷同的盘面。
    /// </summary>
    public static double ComputeItemSimilarity(PvpBoardSnapshot a, PvpBoardSnapshot b)
    {
        if (a.Items.Count == 0 && b.Items.Count == 0) return 1.0;
        if (a.Items.Count == 0 || b.Items.Count == 0) return 0.0;

        var setA = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in a.Items)
            setA.Add($"{item.TemplateId}:{item.Tier}:{item.Enchantment ?? ""}");

        int intersect = 0;
        var setB = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in b.Items)
        {
            var key = $"{item.TemplateId}:{item.Tier}:{item.Enchantment ?? ""}";
            if (setB.Add(key) && setA.Contains(key))
                intersect++;
        }

        int union = setA.Count + setB.Count - intersect;
        return union <= 0 ? 0.0 : (double)intersect / union;
    }
}
