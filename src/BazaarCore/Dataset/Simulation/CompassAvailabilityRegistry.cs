using System.Collections.Concurrent;

namespace BazaarCore.Dataset.Simulation;

/// <summary>
/// 来自巴扎罗盘（availability/{hero}.json）的各英雄分天真实卡牌获取概率缓存（并发安全只读）。
/// </summary>
public static class CompassAvailabilityRegistry
{
    private sealed record HeroAvailability(int[] Days, Dictionary<string, double[]> Cards);

    private static volatile Dictionary<string, HeroAvailability>? _byHero;

    public static void Register(Dictionary<string, (int[] Days, Dictionary<string, double[]> Cards)> data)
    {
        if (data == null || data.Count == 0)
        {
            _byHero = null;
            return;
        }

        var map = new Dictionary<string, HeroAvailability>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hero, val) in data)
        {
            if (val.Days == null || val.Days.Length == 0 || val.Cards == null) continue;
            var cards = new Dictionary<string, double[]>(val.Cards, StringComparer.OrdinalIgnoreCase);
            map[hero] = new HeroAvailability(val.Days, cards);
        }
        _byHero = map;
    }

    public static void Clear() => _byHero = null;

    public static bool TryGetAvailability(string hero, int day, string cardTemplateId, out double probability)
    {
        probability = 0.0;
        var map = _byHero;
        if (map == null || string.IsNullOrWhiteSpace(hero) || string.IsNullOrWhiteSpace(cardTemplateId))
            return false;

        if (!map.TryGetValue(hero, out var heroAvail))
            return false;

        if (!heroAvail.Cards.TryGetValue(cardTemplateId, out var probs) || probs.Length == 0)
            return false;

        int[] days = heroAvail.Days;
        int len = Math.Min(days.Length, probs.Length);
        if (len == 0) return false;

        if (day <= days[0])
        {
            probability = probs[0];
            return true;
        }
        if (day >= days[len - 1])
        {
            probability = probs[len - 1];
            return true;
        }

        for (int i = 0; i < len - 1; i++)
        {
            int d0 = days[i];
            int d1 = days[i + 1];
            if (day >= d0 && day <= d1)
            {
                if (d1 == d0)
                {
                    probability = probs[i];
                    return true;
                }
                double t = (day - d0) / (double)(d1 - d0);
                probability = probs[i] + t * (probs[i + 1] - probs[i]);
                return true;
            }
        }

        probability = probs[len - 1];
        return true;
    }
}
