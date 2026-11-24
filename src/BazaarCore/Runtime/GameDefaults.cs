namespace BazaarCore.Runtime;

/// <summary>
/// 全栈单一默认初始属性常量源（D6-P1-18）。
/// 统一权威基线：300 HP / 15 Gold / 5 Income / 3 XP / 20 Prestige / 初始 4 槽位掩码，
/// 彻底消除服务端各处 250 HP / 8 Gold / 0 XP 漂移。
/// </summary>
public static class GameDefaults
{
    public const int StartingHealth = 300;
    public const int StartingGold = 15;
    public const int StartingIncome = 5;
    public const int StartingExperience = 3;
    public const int StartingPrestige = 20;
    public const ushort InitialUnlockedSlotsMask = 0b00_0111_1000;
}

/// <summary>
/// 同程序集零侵入桥接（D2-P1-08）：直接对接 <see cref="PlayerState"/> 既有 internal 分层乘区读写接口与 Aura 写入接口，
/// 确保 Hour/Day/Aura 的纯乘区（PureMult）与加权乘区（AdditiveMult）在四表之间 100% 同步，
/// 杜绝外部写暴露合成字典导致的 <c>(1 + additive)^2 * pure</c> 双重乘算与可复用实例旧层残留。
/// </summary>
public static class PlayerLayerModifierBridge
{
    /// <summary>清空可复用 <see cref="PlayerState"/> 上的 Hour 玩家修饰层（不动任何卡牌）。</summary>
    public static void ClearHourPlayerLayer(PlayerState ps)
    {
        ArgumentNullException.ThrowIfNull(ps);
        ps.HourAdd.Clear();
        var keys = ps.GetAllHourPureMultKeys();
        foreach (string k in ps.HourAdditiveMult.Keys)
            keys.Add(k);
        foreach (string k in keys)
        {
            ps.SetHourPureMult(k, 1.0);
            ps.SetHourAdditiveMult(k, 0.0);
        }
        ps.HourAdditiveMult.Clear();
        ps.HourMult.Clear();
    }

    /// <summary>清空可复用 <see cref="PlayerState"/> 上的 Day 玩家修饰层（不动任何卡牌）。</summary>
    public static void ClearDayPlayerLayer(PlayerState ps)
    {
        ArgumentNullException.ThrowIfNull(ps);
        ps.DayAdd.Clear();
        var keys = ps.GetAllDayPureMultKeys();
        foreach (string k in ps.DayAdditiveMult.Keys)
            keys.Add(k);
        foreach (string k in keys)
        {
            ps.SetDayPureMult(k, 1.0);
            ps.SetDayAdditiveMult(k, 0.0);
        }
        ps.DayAdditiveMult.Clear();
        ps.DayMult.Clear();
    }

    /// <summary>先清空旧层，再按纯乘区与加权乘区精确装载 Hour 层（含 pure == 0.0 精确落盘）。</summary>
    public static void ApplyHourLayer(
        PlayerState ps,
        IReadOnlyDictionary<string, double>? adds,
        IReadOnlyDictionary<string, double>? pureMults,
        IReadOnlyDictionary<string, double>? additiveMults)
    {
        ArgumentNullException.ThrowIfNull(ps);
        ClearHourPlayerLayer(ps);

        if (adds != null)
        {
            foreach (var (k, v) in adds)
            {
                if (!string.IsNullOrWhiteSpace(k) && double.IsFinite(v) && Math.Abs(v) > 1e-9)
                    ps.HourAdd[k] = v;
            }
        }

        var multKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pureMults != null)
        {
            foreach (string k in pureMults.Keys)
                if (!string.IsNullOrWhiteSpace(k)) multKeys.Add(k);
        }
        if (additiveMults != null)
        {
            foreach (string k in additiveMults.Keys)
                if (!string.IsNullOrWhiteSpace(k)) multKeys.Add(k);
        }

        foreach (string k in multKeys)
        {
            double pure = pureMults != null && pureMults.TryGetValue(k, out double pv) && double.IsFinite(pv) ? pv : 1.0;
            double addMult = additiveMults != null && additiveMults.TryGetValue(k, out double av) && double.IsFinite(av) ? av : 0.0;
            // 确保 pure == 0.0（|0.0 - 1.0| >= 1e-9）精确落盘不被 1.0 默认跳过误滤
            ps.SetHourPureMult(k, pure);
            ps.SetHourAdditiveMult(k, addMult);
        }
    }

    /// <summary>先清空旧层，再按纯乘区与加权乘区精确装载 Day 层（含 pure == 0.0 精确落盘）。</summary>
    public static void ApplyDayLayer(
        PlayerState ps,
        IReadOnlyDictionary<string, double>? adds,
        IReadOnlyDictionary<string, double>? pureMults,
        IReadOnlyDictionary<string, double>? additiveMults)
    {
        ArgumentNullException.ThrowIfNull(ps);
        ClearDayPlayerLayer(ps);

        if (adds != null)
        {
            foreach (var (k, v) in adds)
            {
                if (!string.IsNullOrWhiteSpace(k) && double.IsFinite(v) && Math.Abs(v) > 1e-9)
                    ps.DayAdd[k] = v;
            }
        }

        var multKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pureMults != null)
        {
            foreach (string k in pureMults.Keys)
                if (!string.IsNullOrWhiteSpace(k)) multKeys.Add(k);
        }
        if (additiveMults != null)
        {
            foreach (string k in additiveMults.Keys)
                if (!string.IsNullOrWhiteSpace(k)) multKeys.Add(k);
        }

        foreach (string k in multKeys)
        {
            double pure = pureMults != null && pureMults.TryGetValue(k, out double pv) && double.IsFinite(pv) ? pv : 1.0;
            double addMult = additiveMults != null && additiveMults.TryGetValue(k, out double av) && double.IsFinite(av) ? av : 0.0;
            ps.SetDayPureMult(k, pure);
            ps.SetDayAdditiveMult(k, addMult);
        }
    }

    /// <summary>先清空旧光环层，再精确装载 Aura 加法/纯乘区/加权乘区（含 pure == 0.0 精确落盘）。</summary>
    public static void ApplyAuraLayer(
        PlayerState ps,
        IReadOnlyDictionary<string, double>? adds,
        IReadOnlyDictionary<string, double>? pureMults,
        IReadOnlyDictionary<string, double>? additiveMults)
    {
        ArgumentNullException.ThrowIfNull(ps);
        ps.ClearAuraModifiers();

        if (adds != null)
        {
            foreach (var (k, v) in adds)
            {
                if (!string.IsNullOrWhiteSpace(k) && double.IsFinite(v) && Math.Abs(v) > 1e-9)
                    ps.AuraAdd[k] = v;
            }
        }

        if (pureMults != null)
        {
            foreach (var (k, v) in pureMults)
            {
                if (!string.IsNullOrWhiteSpace(k) && double.IsFinite(v) && Math.Abs(v - 1.0) >= 1e-9)
                    ps.ApplyAuraAttribute(k, "Multiply", v);
            }
        }

        if (additiveMults != null)
        {
            foreach (var (k, v) in additiveMults)
            {
                if (!string.IsNullOrWhiteSpace(k) && double.IsFinite(v) && Math.Abs(v) >= 1e-9)
                    ps.ApplyAuraAttribute(k, "AdditiveMultiply", v);
            }
        }
    }

    /// <summary>提取 <see cref="PlayerState"/> 上纯净的 Hour 纯乘区（排除已合成的 AdditiveMult）。</summary>
    public static void CopyHourPureMults(PlayerState ps, Dictionary<string, double> dest)
    {
        ArgumentNullException.ThrowIfNull(ps);
        ArgumentNullException.ThrowIfNull(dest);
        dest.Clear();
        foreach (string k in ps.GetAllHourPureMultKeys())
        {
            double pure = ps.GetHourPureMult(k);
            if (double.IsFinite(pure) && Math.Abs(pure - 1.0) >= 1e-9)
                dest[k] = pure;
        }
    }

    /// <summary>提取 <see cref="PlayerState"/> 上纯净的 Day 纯乘区（排除已合成的 AdditiveMult）。</summary>
    public static void CopyDayPureMults(PlayerState ps, Dictionary<string, double> dest)
    {
        ArgumentNullException.ThrowIfNull(ps);
        ArgumentNullException.ThrowIfNull(dest);
        dest.Clear();
        foreach (string k in ps.GetAllDayPureMultKeys())
        {
            double pure = ps.GetDayPureMult(k);
            if (double.IsFinite(pure) && Math.Abs(pure - 1.0) >= 1e-9)
                dest[k] = pure;
        }
    }

    /// <summary>提取 <see cref="PlayerState"/> 上纯净的 Aura 纯乘区（当 |1 + aAdd| > 1e-9 时无损拆分，否则回退 1.0）。</summary>
    public static void CopyAuraPureMults(PlayerState ps, Dictionary<string, double> dest)
    {
        ArgumentNullException.ThrowIfNull(ps);
        ArgumentNullException.ThrowIfNull(dest);
        dest.Clear();
        foreach (var (k, exposed) in ps.AuraMult)
        {
            if (!double.IsFinite(exposed)) continue;
            double pure;
            if (!ps.AuraAdditiveMult.TryGetValue(k, out double aAdd) || Math.Abs(aAdd) < 1e-9)
            {
                pure = exposed;
            }
            else if (Math.Abs(1.0 + aAdd) > 1e-9)
            {
                pure = exposed / (1.0 + aAdd);
            }
            else
            {
                pure = 1.0;
            }

            if (double.IsFinite(pure) && Math.Abs(pure - 1.0) >= 1e-9)
                dest[k] = pure;
        }
    }

    /// <summary>D2-P1-14：同程序集零侵入挂接/读取 <see cref="CardInstance.AttachedStandardPrices"/>，无需修改受限的 CardInstance.cs。</summary>
    public static void SetAttachedStandardPrices(CardInstance card, BazaarCore.Models.StandardPricesSettings? prices)
    {
        ArgumentNullException.ThrowIfNull(card);
        card.AttachedStandardPrices = prices;
    }

    public static BazaarCore.Models.StandardPricesSettings? GetAttachedStandardPrices(CardInstance card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card.AttachedStandardPrices;
    }
}
