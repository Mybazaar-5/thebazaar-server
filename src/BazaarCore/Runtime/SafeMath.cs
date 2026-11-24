using System;

namespace BazaarCore.Runtime;

/// <summary>
/// 战斗与属性计算的零分配数值安全工具类：提供整型饱和加减、有限双精度转整型与 NaN/Infinity 防护。
/// </summary>
public static class SafeMath
{
    /// <summary>
    /// 战斗属性与状态池默认饱和上限（10 亿），确保多次累加与乘法缩放绝不触发 int32 符号翻转或下溢。
    /// </summary>
    public const int DefaultCombatMax = 1_000_000_000;

    /// <summary>
    /// 在 64 位整型域执行求和并饱和钳制到 [min, max]，彻底杜绝 int32 溢出回绕。
    /// </summary>
    public static int SaturatingAddInt(int a, int b, int min = 0, int max = DefaultCombatMax)
    {
        if (max < min) max = min;
        long sum = (long)a + b;
        if (sum < min) return min;
        if (sum > max) return max;
        return (int)sum;
    }

    /// <summary>
    /// 将有限或非有限双精度数安全转换为 [min, max] 范围内的 int：
    /// NaN 回退为 min，+Infinity 饱和到 max，-Infinity 饱和到 min，避免 (int)double.NaN 产生 int.MinValue。
    /// </summary>
    public static int ToSafeInt(
        double value,
        int min = 0,
        int max = DefaultCombatMax,
        MidpointRounding rounding = MidpointRounding.ToEven)
    {
        if (max < min) max = min;
        if (double.IsNaN(value)) return min;
        if (double.IsPositiveInfinity(value)) return max;
        if (double.IsNegativeInfinity(value)) return min;
        double rounded = Math.Round(value, rounding);
        if (rounded <= min) return min;
        if (rounded >= max) return max;
        return (int)rounded;
    }

    /// <summary>
    /// 将双精度数安全钳制至有限区间 [min, max]：NaN 返回 fallback，+Inf 返回 max，-Inf 返回 min。
    /// </summary>
    public static double ClampFinite(double value, double min, double max, double fallback)
    {
        if (max < min) max = min;
        if (double.IsNaN(value))
            return Math.Clamp(fallback, min, max);
        if (double.IsPositiveInfinity(value)) return max;
        if (double.IsNegativeInfinity(value)) return min;
        return Math.Clamp(value, min, max);
    }
}
