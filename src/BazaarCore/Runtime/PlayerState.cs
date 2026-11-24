using BazaarCore.Engine;
using BazaarCore.Models;

namespace BazaarCore.Runtime;

/// <summary>
/// 玩家（英雄或野怪）的完整局内与战斗运行时状态。
/// </summary>
public sealed class PlayerState
{
    public string PlayerId { get; }
    public string DisplayName { get; set; }
    public string Hero { get; set; }
    public bool IsMonster { get; set; }
    public bool IsBot { get; set; }

    public BoardGrid Hand { get; }
    public BoardGrid Stash { get; } = new(1023);
    public List<CardInstance> Skills { get; } = new();
    public List<CardInstance> SocketEffects { get; } = new();
    /// <summary>系统级 PlayerEffect 卡牌实例（独立于 Skills 存储，避免污染技能数量统计并随战斗克隆透传）。</summary>
    public List<CardInstance> PlayerEffects { get; } = new();

    /// <summary>当前局内禁用的模板 ID 集合（源自 GameMode.ExclusionSet，可被规则增删）。</summary>
    public HashSet<string> ExclusionSet { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ExclusionSetInitialized { get; set; }

    public void InitializeExclusionSet(GameModeData? gameMode)
    {
        ExclusionSet.Clear();
        if (gameMode?.ExclusionSet != null)
        {
            foreach (var id in gameMode.ExclusionSet)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    ExclusionSet.Add(id);
            }
        }
        ExclusionSetInitialized = true;
    }

    internal void EnsureExclusionSetInitialized(GameModeData? gameMode)
    {
        if (ExclusionSetInitialized || gameMode == null) return;
        if (gameMode.ExclusionSet != null)
        {
            foreach (var id in gameMode.ExclusionSet)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    ExclusionSet.Add(id);
            }
        }
        ExclusionSetInitialized = true;
    }

    /// <summary>玩家基础与永久属性（Gold, Income, Prestige, Level, Experience, HealthMax, RerollCostModifier等）。</summary>
    public Dictionary<string, double> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本小时临时玩家属性（UntilEndOfHour）。</summary>
    public Dictionary<string, double> HourAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> HourAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> HourMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当日临时玩家属性（UntilEndOfDay）。</summary>
    public Dictionary<string, double> DayAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> DayAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> DayMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>单场战斗临时玩家属性（UntilEndOfCombat / TCombatDuration）。</summary>
    public Dictionary<string, double> CombatAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> CombatAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> CombatMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>单场战斗零乘数（Multiply 0.0）引用计数，避免覆写破坏 CombatMult 非零连乘值，到期减 1 无损恢复。</summary>
    public Dictionary<string, int> CombatZeroMultCount { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>光环动态加成属性。</summary>
    public Dictionary<string, double> AuraAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> AuraAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> AuraMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    // 各层纯乘法因子与最近一次同步到公开 *Mult 字典的组合值镜像：
    // 既保证 AdditiveMultiply（加权初值 0.0）与 Multiply（连乘初值 1.0）彻底解耦、遍历顺序无关，
    // 又使 RunEngine/EncounterStepEngine 读取公开 HourMult/DayMult/AuraMult 时获得完整等效乘区且支持外部直写。
    private readonly Dictionary<string, double> _hourPureMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _hourSyncedMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _dayPureMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _daySyncedMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _combatPureMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _combatSyncedMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _auraPureMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _auraSyncedMult = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>由 CloneForCombat() 记录的克隆体与原玩家卡牌同 InstanceId 源引用映射。</summary>
    public Dictionary<string, CardInstance>? CombatSourceCardMap { get; private set; }

    /// <summary>由 CloneForCombat() 记录的克隆时刻永久玩家属性快照。</summary>
    internal Dictionary<string, double>? CombatSourcePlayerAttributes { get; private set; }

    /// <summary>P0-04：由 CloneForCombat() 记录的源玩家 Day/Hour 层基线快照，供战后差量同步。</summary>
    internal Dictionary<string, double>? CombatSourceDayAdd { get; private set; }
    internal Dictionary<string, double>? CombatSourceDayAdditiveMult { get; private set; }
    internal Dictionary<string, double>? CombatSourceDayPureMult { get; private set; }
    internal Dictionary<string, double>? CombatSourceHourAdd { get; private set; }
    internal Dictionary<string, double>? CombatSourceHourAdditiveMult { get; private set; }
    internal Dictionary<string, double>? CombatSourceHourPureMult { get; private set; }

    /// <summary>P0-05：战后临时变形逆序弹栈还原的原卡条目（保留原容器归属，满盘也不丢同步引用）。</summary>
    internal List<RestoredTemporaryCardEntry>? RestoredTemporaryOriginals { get; set; }

    // 战斗实时状态
    public int CurrentHealth { get; set; }
    public int Shield { get; set; }
    public int Burn { get; set; }
    public int Poison { get; set; }
    public int Rage { get; set; }
    public int EnragedRemainingMs { get; set; }
    public int Tempo { get; set; }
    public int PortraitIndex { get; set; }

    public int Day { get; set; } = 1;
    public int Hour { get; set; } = 1;
    public int Wins { get; set; }
    public int Losses { get; set; }
    public bool IsEliminated { get; set; }
    public int EliminatedDay { get; set; }
    public bool FatesUsed { get; set; }

    public PlayerState(string playerId, string displayName, string hero, int initialHealthMax = GameDefaults.StartingHealth, int initialPrestige = GameDefaults.StartingPrestige, int initialGold = 10, int unlockedHandSlots = 4)
    {
        PlayerId = playerId;
        DisplayName = displayName;
        Hero = hero;
        Hand = new BoardGrid(0);
        if (unlockedHandSlots >= 10)
            Hand.UnlockedMask = 1023;
        else
            Hand.UnlockCountCentered(unlockedHandSlots);

        Attributes["HealthMax"] = initialHealthMax;
        Attributes["Health"] = initialHealthMax;
        Attributes["Prestige"] = initialPrestige;
        Attributes["Gold"] = initialGold;
        Attributes["Income"] = 5;
        Attributes["Level"] = 1;
        Attributes["Experience"] = 0;
        Attributes["HealthRegen"] = 0;
        Attributes["RageMax"] = 100;
        Attributes["EnragedDurationMax"] = 5000;
        Attributes["PercentDamageReduction"] = 0;
        CurrentHealth = initialHealthMax;
    }

    public int HealthMax => SafeMath.ToSafeInt(GetAttribute("HealthMax"), 1, SafeMath.DefaultCombatMax);
    public int Gold
    {
        get => SafeMath.ToSafeInt(GetAttribute("Gold"), 0, SafeMath.DefaultCombatMax);
        set => Attributes["Gold"] = Math.Clamp(value, 0, SafeMath.DefaultCombatMax);
    }
    public int Prestige
    {
        get => SafeMath.ToSafeInt(GetAttribute("Prestige"), 0, SafeMath.DefaultCombatMax);
        set => Attributes["Prestige"] = Math.Clamp(value, 0, SafeMath.DefaultCombatMax);
    }
    public int Level
    {
        get => SafeMath.ToSafeInt(GetAttribute("Level"), 1, SafeMath.DefaultCombatMax);
        set => Attributes["Level"] = Math.Clamp(value, 1, SafeMath.DefaultCombatMax);
    }
    public int Experience
    {
        get => SafeMath.ToSafeInt(GetAttribute("Experience"), 0, SafeMath.DefaultCombatMax);
        set => Attributes["Experience"] = Math.Clamp(value, 0, SafeMath.DefaultCombatMax);
    }
    public int Income => SafeMath.ToSafeInt(GetAttribute("Income"), -SafeMath.DefaultCombatMax, SafeMath.DefaultCombatMax);

    public bool IsEnraged => EnragedRemainingMs > 0;
    public bool IsAlive => CurrentHealth > 0;

    public double GetAttributeUnscaled(string attr) => attr switch
    {
        "Enraged" => EnragedRemainingMs > 0
            ? 1.0
            : (CombatAdd.TryGetValue("Enraged", out var cEnr) ? cEnr : Attributes.GetValueOrDefault("Enraged", 0)),
        "EnragedDuration" => EnragedRemainingMs > 0
            ? EnragedRemainingMs
            : (CombatAdd.TryGetValue("EnragedDuration", out var cDur) ? cDur : Attributes.GetValueOrDefault("EnragedDuration", 0)),
        _ => Attributes.GetValueOrDefault(attr, 0)
    };

    private static (double Additive, double Pure) GetLayerMultipliers(
        string attr,
        Dictionary<string, double> additiveDict,
        Dictionary<string, double> pureDict,
        Dictionary<string, double> syncedDict,
        Dictionary<string, double> exposedMultDict)
    {
        double additive = additiveDict.GetValueOrDefault(attr, 0.0);
        if (exposedMultDict.TryGetValue(attr, out double exposed))
        {
            if (syncedDict.TryGetValue(attr, out double lastSynced) && Math.Abs(exposed - lastSynced) <= 1e-9)
                return (additive, pureDict.GetValueOrDefault(attr, 1.0));
            return (additive, exposed);
        }
        return (additive, pureDict.GetValueOrDefault(attr, 1.0));
    }

    private static void SeedPureMultIfExternallySet(
        string attr,
        Dictionary<string, double> additiveDict,
        Dictionary<string, double> pureDict,
        Dictionary<string, double> syncedDict,
        Dictionary<string, double> exposedMultDict)
    {
        if (exposedMultDict.TryGetValue(attr, out double exposed)
            && !pureDict.ContainsKey(attr)
            && !additiveDict.ContainsKey(attr)
            && !syncedDict.ContainsKey(attr))
        {
            pureDict[attr] = exposed;
        }
    }

    private static void SyncLayerExposedMult(
        string attr,
        Dictionary<string, double> additiveDict,
        Dictionary<string, double> pureDict,
        Dictionary<string, double> syncedDict,
        Dictionary<string, double> exposedMultDict,
        int zeroMultCount = 0)
    {
        bool hasAdd = additiveDict.TryGetValue(attr, out double additive) && Math.Abs(additive) > 1e-9;
        bool hasPure = pureDict.TryGetValue(attr, out double pure) && Math.Abs(pure - 1.0) > 1e-9;
        if (!hasAdd) additive = 0.0;
        if (!hasPure) pure = 1.0;

        if (!hasAdd && !hasPure && zeroMultCount <= 0)
        {
            exposedMultDict.Remove(attr);
            syncedDict.Remove(attr);
            return;
        }

        double combined = zeroMultCount > 0 ? 0.0 : (1.0 + additive) * pure;
        exposedMultDict[attr] = combined;
        syncedDict[attr] = combined;
    }

    public double GetAttribute(string attr)
    {
        double basePoolVal;
        switch (attr)
        {
            case "Health":
                return CurrentHealth;
            case "Shield":
                basePoolVal = Shield;
                break;
            case "Burn":
                basePoolVal = Burn;
                break;
            case "Poison":
                basePoolVal = Poison;
                break;
            case "Rage":
                basePoolVal = Rage;
                break;
            case "Tempo":
                basePoolVal = Tempo;
                break;
            case "Enraged":
                return EnragedRemainingMs > 0
                    ? 1.0
                    : (CombatAdd.TryGetValue("Enraged", out var cEnr) ? cEnr : Attributes.GetValueOrDefault("Enraged", 0));
            case "EnragedDuration":
                return EnragedRemainingMs > 0
                    ? EnragedRemainingMs
                    : (CombatAdd.TryGetValue("EnragedDuration", out var cDur) ? cDur : Attributes.GetValueOrDefault("EnragedDuration", 0));
            default:
                basePoolVal = Attributes.GetValueOrDefault(attr, 0);
                break;
        }

        // 修复 P2-24：保留 Health 直读语义；Shield/Burn/Poison/Rage/Tempo 在保留实时池基值的基础上叠加 AuraAdd/CombatAdd 及乘区
        var baseVal = basePoolVal
                    + HourAdd.GetValueOrDefault(attr, 0)
                    + DayAdd.GetValueOrDefault(attr, 0)
                    + CombatAdd.GetValueOrDefault(attr, 0)
                    + AuraAdd.GetValueOrDefault(attr, 0);

        var (hAdd, hPure) = GetLayerMultipliers(attr, HourAdditiveMult, _hourPureMult, _hourSyncedMult, HourMult);
        var (dAdd, dPure) = GetLayerMultipliers(attr, DayAdditiveMult, _dayPureMult, _daySyncedMult, DayMult);
        var (cAdd, cPure) = GetLayerMultipliers(attr, CombatAdditiveMult, _combatPureMult, _combatSyncedMult, CombatMult);
        var (aAdd, aPure) = GetLayerMultipliers(attr, AuraAdditiveMult, _auraPureMult, _auraSyncedMult, AuraMult);

        double totalAdditiveMult = hAdd + dAdd + cAdd + aAdd;
        double totalMult = (CombatZeroMultCount.Count > 0 && CombatZeroMultCount.GetValueOrDefault(attr, 0) > 0)
            ? 0.0
            : (hPure * dPure * cPure * aPure);
        double res = baseVal * Math.Max(0.0, 1.0 + totalAdditiveMult) * totalMult;
        return double.IsFinite(res)
            ? res
            : SafeMath.ClampFinite(res, -SafeMath.DefaultCombatMax, SafeMath.DefaultCombatMax, 0.0);
    }

    internal double GetDayPureMult(string attr)
        => GetLayerMultipliers(attr, DayAdditiveMult, _dayPureMult, _daySyncedMult, DayMult).Pure;

    internal double GetHourPureMult(string attr)
        => GetLayerMultipliers(attr, HourAdditiveMult, _hourPureMult, _hourSyncedMult, HourMult).Pure;

    internal HashSet<string> GetAllDayPureMultKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _dayPureMult.Keys) keys.Add(k);
        foreach (var k in DayMult.Keys) keys.Add(k);
        return keys;
    }

    internal HashSet<string> GetAllHourPureMultKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _hourPureMult.Keys) keys.Add(k);
        foreach (var k in HourMult.Keys) keys.Add(k);
        return keys;
    }

    internal void SetDayAdditiveMult(string attr, double value)
    {
        if (Math.Abs(value) < 1e-9) DayAdditiveMult.Remove(attr);
        else DayAdditiveMult[attr] = value;
        SyncLayerExposedMult(attr, DayAdditiveMult, _dayPureMult, _daySyncedMult, DayMult);
    }

    internal void SetDayPureMult(string attr, double value)
    {
        if (Math.Abs(value - 1.0) < 1e-9) _dayPureMult.Remove(attr);
        else _dayPureMult[attr] = value;
        SyncLayerExposedMult(attr, DayAdditiveMult, _dayPureMult, _daySyncedMult, DayMult);
    }

    internal void SetHourAdditiveMult(string attr, double value)
    {
        if (Math.Abs(value) < 1e-9) HourAdditiveMult.Remove(attr);
        else HourAdditiveMult[attr] = value;
        SyncLayerExposedMult(attr, HourAdditiveMult, _hourPureMult, _hourSyncedMult, HourMult);
    }

    internal void SetHourPureMult(string attr, double value)
    {
        if (Math.Abs(value - 1.0) < 1e-9) _hourPureMult.Remove(attr);
        else _hourPureMult[attr] = value;
        SyncLayerExposedMult(attr, HourAdditiveMult, _hourPureMult, _hourSyncedMult, HourMult);
    }

    /// <summary>P0-31：统一玩家属性名别名（将 Regen 归一化为 HealthRegen）。</summary>
    public static string NormalizeAttributeName(string? attr)
    {
        if (string.IsNullOrWhiteSpace(attr)) return string.Empty;
        string trimmed = attr.Trim();
        if (trimmed.Equals("Regen", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("HealthRegen", StringComparison.OrdinalIgnoreCase))
            return "HealthRegen";
        return trimmed;
    }

    public void ModifyAttribute(string attr, string operation, double value)
        => ModifyAttribute(attr, operation, value, DurationTarget.Permanent);

    public void ModifyAttribute(string attr, string operation, double value, DurationTarget target)
    {
        attr = NormalizeAttributeName(attr);
        if (string.IsNullOrEmpty(attr) || !double.IsFinite(value)) return;

        // 战斗实时池（Health/Shield/Burn/Poison/Rage/Tempo/Enraged/EnragedDuration）直接作用于实时属性，绝不污染永久 Attributes；
        // 对实时池增减取裸实时字段而非含光环层的 GetAttribute，防止将光环加成重复烘入实时池（P2-24）。
        if (attr is "Health" or "Shield" or "Burn" or "Poison" or "Rage" or "Tempo" or "Enraged" or "EnragedDuration")
        {
            double curRt = attr switch
            {
                "Health" => CurrentHealth,
                "Shield" => Shield,
                "Burn" => Burn,
                "Poison" => Poison,
                "Rage" => Rage,
                "Tempo" => Tempo,
                _ => GetAttribute(attr)
            };
            double nextRt = operation switch
            {
                "Subtract" => curRt - value,
                "Multiply" => curRt * value,
                "AdditiveMultiply" => curRt * (1.0 + value),
                "Set" => value,
                _ => curRt + value
            };

            switch (attr)
            {
                case "Health":
                    CurrentHealth = Math.Clamp(SafeMath.ToSafeInt(nextRt, 0, Math.Max(1, HealthMax)), 0, Math.Max(1, HealthMax));
                    break;
                case "Shield":
                    Shield = SafeMath.ToSafeInt(nextRt, 0, SafeMath.DefaultCombatMax);
                    break;
                case "Burn":
                    Burn = SafeMath.ToSafeInt(nextRt, 0, SafeMath.DefaultCombatMax);
                    break;
                case "Poison":
                    Poison = SafeMath.ToSafeInt(nextRt, 0, SafeMath.DefaultCombatMax);
                    break;
                case "Rage":
                    Rage = SafeMath.ToSafeInt(nextRt, 0, SafeMath.DefaultCombatMax);
                    break;
                case "Tempo":
                    Tempo = SafeMath.ToSafeInt(nextRt, 0, SafeMath.DefaultCombatMax);
                    break;
                case "Enraged":
                    if (!double.IsFinite(nextRt) || nextRt <= 0)
                    {
                        EnragedRemainingMs = 0;
                        CombatAdd.Remove("Enraged");
                        CombatAdd.Remove("EnragedDuration");
                    }
                    else
                    {
                        if (EnragedRemainingMs <= 0)
                            EnragedRemainingMs = SafeMath.ToSafeInt(GetAttribute("EnragedDurationMax"), 1000, SafeMath.DefaultCombatMax);
                        CombatAdd["Enraged"] = 1.0;
                        CombatAdd["EnragedDuration"] = EnragedRemainingMs;
                    }
                    break;
                case "EnragedDuration":
                    EnragedRemainingMs = SafeMath.ToSafeInt(nextRt, 0, SafeMath.DefaultCombatMax);
                    if (EnragedRemainingMs > 0)
                    {
                        CombatAdd["Enraged"] = 1.0;
                        CombatAdd["EnragedDuration"] = EnragedRemainingMs;
                    }
                    else
                    {
                        CombatAdd.Remove("Enraged");
                        CombatAdd.Remove("EnragedDuration");
                    }
                    break;
            }
            return;
        }

        if (target == DurationTarget.Permanent)
        {
            double cur = GetAttributeUnscaled(attr);
            double next = operation switch
            {
                "Subtract" => cur - value,
                "Multiply" => cur * value,
                "AdditiveMultiply" => cur * (1.0 + value),
                "Set" => value,
                _ => cur + value
            };

            Attributes[attr] = next;
            if (attr.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
            {
                double eff = GetAttribute(attr);
                if (eff < 1.0)
                {
                    Attributes[attr] = next + (1.0 - eff);
                    next = Attributes[attr];
                }
                double delta = next - cur;
                int cap = Math.Max(1, HealthMax);
                if (delta > 0) CurrentHealth = SafeMath.SaturatingAddInt(CurrentHealth, SafeMath.ToSafeInt(delta, 0, SafeMath.DefaultCombatMax), 0, cap);
                else CurrentHealth = Math.Clamp(CurrentHealth, 0, cap);
            }
            else if (attr.Equals("HealthRegen", StringComparison.OrdinalIgnoreCase)
                     || attr.Equals("Joy", StringComparison.OrdinalIgnoreCase))
            {
                // 按总有效值钳底 >= 0（允许跨层叠加时 Permanent 持有合法负差值，仅当总有效值跌破 0 时回弹）
                double eff = GetAttribute(attr);
                if (eff < 0.0)
                    Attributes[attr] = next - eff;
            }
            return;
        }

        // D1-P1-07 (#126/#128 备忘)：若该 attr 存在活跃限时修饰，则非限时战中修改写入 _combatBase* 并单向重算聚合
        bool hasActiveTimedForAttr = target == DurationTarget.Combat && HasActiveTimedModifierForAttr(attr);
        var (addDict, addMultDict, pureDict, syncedDict, exposedMultDict) = target switch
        {
            DurationTarget.Hour => (HourAdd, HourAdditiveMult, _hourPureMult, _hourSyncedMult, HourMult),
            DurationTarget.Day => (DayAdd, DayAdditiveMult, _dayPureMult, _daySyncedMult, DayMult),
            _ => hasActiveTimedForAttr
                ? (_combatBaseAdd, _combatBaseAdditiveMult, _combatBasePureMult, _combatSyncedMult, CombatMult)
                : (CombatAdd, CombatAdditiveMult, _combatPureMult, _combatSyncedMult, CombatMult)
        };
        var zeroDict = hasActiveTimedForAttr ? _combatBaseZeroMultCount : CombatZeroMultCount;

        double prevEff = GetAttribute(attr);
        switch (operation)
        {
            case "Set":
            {
                double desired = attr.Equals("HealthMax", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(1.0, value)
                    : ((attr.Equals("HealthRegen", StringComparison.OrdinalIgnoreCase) || attr.Equals("Joy", StringComparison.OrdinalIgnoreCase))
                        ? Math.Max(0.0, value)
                        : value);
                // D1-P1-07：参照 CardInstance 干净分槽 Set 语义——清除当前目标层乘区，仅汇总不高于目标层的非光环基线乘区
                // （Combat 层仅用 Day+Hour，绝不混入当前战中/活跃限时乘区，避免限时 Multiply 被反向烘入非限时基线）
                pureDict.Remove(attr);
                addMultDict.Remove(attr);
                if (target == DurationTarget.Combat)
                    zeroDict.Remove(attr);
                if (!hasActiveTimedForAttr)
                    SyncLayerExposedMult(attr, addMultDict, pureDict, syncedDict, exposedMultDict, 0);

                double lowerAddMult = 0.0;
                double lowerPureMult = 1.0;
                if (target is DurationTarget.Hour or DurationTarget.Combat)
                {
                    var (dAdd, dPure) = GetLayerMultipliers(attr, DayAdditiveMult, _dayPureMult, _daySyncedMult, DayMult);
                    lowerAddMult += dAdd;
                    lowerPureMult *= dPure;
                }
                if (target == DurationTarget.Combat)
                {
                    var (hAdd, hPure) = GetLayerMultipliers(attr, HourAdditiveMult, _hourPureMult, _hourSyncedMult, HourMult);
                    lowerAddMult += hAdd;
                    lowerPureMult *= hPure;
                }
                double nonAuraMult = Math.Max(0.0, 1.0 + lowerAddMult) * lowerPureMult;
                if (Math.Abs(nonAuraMult) < 1e-9)
                {
                    if (target is DurationTarget.Hour or DurationTarget.Combat)
                    {
                        if (_dayPureMult.TryGetValue(attr, out var dp0) && Math.Abs(dp0) < 1e-9) _dayPureMult.Remove(attr);
                        DayAdditiveMult.Remove(attr);
                        SyncLayerExposedMult(attr, DayAdditiveMult, _dayPureMult, _daySyncedMult, DayMult, 0);
                    }
                    if (target == DurationTarget.Combat)
                    {
                        if (_hourPureMult.TryGetValue(attr, out var hp0) && Math.Abs(hp0) < 1e-9) _hourPureMult.Remove(attr);
                        HourAdditiveMult.Remove(attr);
                        SyncLayerExposedMult(attr, HourAdditiveMult, _hourPureMult, _hourSyncedMult, HourMult, 0);
                    }
                    nonAuraMult = 1.0;
                }
                double otherAdds = Attributes.GetValueOrDefault(attr, 0)
                    + (target is DurationTarget.Hour or DurationTarget.Combat ? DayAdd.GetValueOrDefault(attr, 0) : 0)
                    + (target == DurationTarget.Combat ? HourAdd.GetValueOrDefault(attr, 0) : 0);
                addDict[attr] = (desired / nonAuraMult) - otherAdds;
                break;
            }
            case "Subtract":
                addDict[attr] = addDict.GetValueOrDefault(attr, 0) - value;
                break;
            case "Multiply":
                if (!hasActiveTimedForAttr)
                    SeedPureMultIfExternallySet(attr, addMultDict, pureDict, syncedDict, exposedMultDict);
                if (target == DurationTarget.Combat && Math.Abs(value) < 1e-9)
                {
                    zeroDict[attr] = zeroDict.GetValueOrDefault(attr, 0) + 1;
                }
                else
                {
                    double nextPure = pureDict.GetValueOrDefault(attr, 1.0) * value;
                    if (Math.Abs(nextPure - 1.0) < 1e-9) pureDict.Remove(attr);
                    else pureDict[attr] = nextPure;
                }
                if (!hasActiveTimedForAttr)
                    SyncLayerExposedMult(attr, addMultDict, pureDict, syncedDict, exposedMultDict,
                        target == DurationTarget.Combat ? CombatZeroMultCount.GetValueOrDefault(attr, 0) : 0);
                break;
            case "AdditiveMultiply":
            {
                if (!hasActiveTimedForAttr)
                    SeedPureMultIfExternallySet(attr, addMultDict, pureDict, syncedDict, exposedMultDict);
                double nextAddMult = addMultDict.GetValueOrDefault(attr, 0.0) + value;
                if (Math.Abs(nextAddMult) < 1e-9) addMultDict.Remove(attr);
                else addMultDict[attr] = nextAddMult;
                if (!hasActiveTimedForAttr)
                    SyncLayerExposedMult(attr, addMultDict, pureDict, syncedDict, exposedMultDict,
                        target == DurationTarget.Combat ? CombatZeroMultCount.GetValueOrDefault(attr, 0) : 0);
                break;
            }
            default:
                addDict[attr] = addDict.GetValueOrDefault(attr, 0) + value;
                break;
        }
        if (hasActiveTimedForAttr)
            RecomputeCombatAttributeFromBaseline(attr);
        if (attr.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
        {
            double curEff = GetAttribute(attr);
            if (curEff < 1.0)
            {
                addDict[attr] = addDict.GetValueOrDefault(attr, 0) + (1.0 - curEff);
                if (hasActiveTimedForAttr) RecomputeCombatAttributeFromBaseline(attr);
                curEff = GetAttribute(attr);
            }
            double delta = curEff - prevEff;
            int cap = Math.Max(1, HealthMax);
            if (delta > 0) CurrentHealth = SafeMath.SaturatingAddInt(CurrentHealth, SafeMath.ToSafeInt(delta, 0, SafeMath.DefaultCombatMax), 0, cap);
            else CurrentHealth = Math.Clamp(CurrentHealth, 0, cap);
        }
        else if (attr.Equals("HealthRegen", StringComparison.OrdinalIgnoreCase)
                 || attr.Equals("Joy", StringComparison.OrdinalIgnoreCase))
        {
            double curEff = GetAttribute(attr);
            if (curEff < 0.0)
            {
                addDict[attr] = addDict.GetValueOrDefault(attr, 0) - curEff;
                if (hasActiveTimedForAttr) RecomputeCombatAttributeFromBaseline(attr);
            }
        }
    }

    /// <summary>
    /// P0-31/P0-32：受控全层属性清洗（同步内部 _*PureMult / _*SyncedMult / 公开 *Mult 三件套，防外部直改撕裂）。
    /// - 剔除/归零 NaN/Infinity；
    /// - 加法层按 ±SafeMath.DefaultCombatMax（1e9）清洗，显式豁免官方合法有限 -1e15 Tempo 压制，绝不误伤无尽高塔 >1e6 高血；
    /// - 非零纯乘区钳制 [0.001, 1000.0]，完整保留 0 乘与 CombatZeroMultCount；
    /// - 同步夹紧 CurrentHealth/Shield/Burn/Poison。
    /// </summary>
    public void SanitizeAllAttributeLayers()
    {
        static bool IsOfficialTempoSuppression(string key, double value)
            => double.IsFinite(value) && value <= -1e12
               && (key.Equals("TempoGainCooldownMax", StringComparison.OrdinalIgnoreCase)
                   || key.Equals("Tempo", StringComparison.OrdinalIgnoreCase));

        static void CleanAddDict(Dictionary<string, double> dict, bool isBaseAttributes)
        {
            if (dict.Count == 0) return;
            List<string>? keys = null;
            foreach (var (k, v) in dict)
            {
                if (!double.IsFinite(v) || (!IsOfficialTempoSuppression(k, v) && Math.Abs(v) > SafeMath.DefaultCombatMax))
                {
                    keys ??= new List<string>();
                    keys.Add(k);
                }
            }
            if (keys == null) return;
            foreach (var k in keys)
            {
                double v = dict[k];
                if (!double.IsFinite(v))
                {
                    if (isBaseAttributes && k.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
                        dict[k] = GameDefaults.StartingHealth;
                    else
                        dict[k] = 0.0;
                }
                else
                {
                    dict[k] = Math.Clamp(v, -SafeMath.DefaultCombatMax, SafeMath.DefaultCombatMax);
                }
            }
        }

        void CleanMultLayer(
            Dictionary<string, double> addMult,
            Dictionary<string, double> pureMult,
            Dictionary<string, double> syncedMult,
            Dictionary<string, double> exposedMult,
            bool isCombat)
        {
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in addMult.Keys) touched.Add(k);
            foreach (var k in pureMult.Keys) touched.Add(k);
            foreach (var k in exposedMult.Keys) touched.Add(k);
            if (touched.Count == 0) return;

            foreach (var k in touched)
            {
                // 若外部曾直改过公开 *Mult，先吸收进 pureMult，再统一钳制与同步
                SeedPureMultIfExternallySet(k, addMult, pureMult, syncedMult, exposedMult);

                if (addMult.TryGetValue(k, out double am))
                {
                    if (!double.IsFinite(am)) addMult.Remove(k);
                    else if (Math.Abs(am) > 1000.0) addMult[k] = Math.Clamp(am, -1000.0, 1000.0);
                }

                if (pureMult.TryGetValue(k, out double pm))
                {
                    if (!double.IsFinite(pm))
                    {
                        pureMult.Remove(k);
                    }
                    else if (Math.Abs(pm) > 1e-12)
                    {
                        double clamped = Math.Clamp(pm, 0.001, 1000.0);
                        if (Math.Abs(clamped - 1.0) < 1e-9) pureMult.Remove(k);
                        else pureMult[k] = clamped;
                    }
                    // 若 pm == 0（或存在 CombatZeroMultCount），保留 0 乘语义不破坏 Multiply 0
                }

                SyncLayerExposedMult(k, addMult, pureMult, syncedMult, exposedMult,
                    isCombat ? CombatZeroMultCount.GetValueOrDefault(k, 0) : 0);
            }
        }

        CleanAddDict(Attributes, isBaseAttributes: true);
        CleanAddDict(HourAdd, isBaseAttributes: false);
        CleanAddDict(DayAdd, isBaseAttributes: false);
        CleanAddDict(CombatAdd, isBaseAttributes: false);
        CleanAddDict(AuraAdd, isBaseAttributes: false);

        CleanMultLayer(HourAdditiveMult, _hourPureMult, _hourSyncedMult, HourMult, isCombat: false);
        CleanMultLayer(DayAdditiveMult, _dayPureMult, _daySyncedMult, DayMult, isCombat: false);
        CleanMultLayer(CombatAdditiveMult, _combatPureMult, _combatSyncedMult, CombatMult, isCombat: true);
        CleanMultLayer(AuraAdditiveMult, _auraPureMult, _auraSyncedMult, AuraMult, isCombat: false);

        if (Shield < 0) Shield = 0;
        if (Burn < 0) Burn = 0;
        if (Poison < 0) Poison = 0;
        if (Attributes.TryGetValue("HealthRegen", out double reg) && reg < 0)
            Attributes["HealthRegen"] = 0;
        if (HealthMax < 1)
            Attributes["HealthMax"] = 1;
        // 仅夹紧上界防止 CurrentHealth > HealthMax 溢出；允许伤害管线产生的负血击杀视觉留待战斗判定收口
        if (CurrentHealth > HealthMax)
            CurrentHealth = HealthMax;
    }

    // D1-P1-07 (#126/#128 备忘)：非限时战中基线与限时 TCombatDuration 活性修饰列表严格分槽，单向重算聚合（0 逆除法）
    private readonly Dictionary<string, double> _combatBaseAdd = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _combatBaseAdditiveMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _combatBasePureMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _combatBaseZeroMultCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CardInstance.TimedAttributeModifier> _activeTimedCombatModifiers = new();
    private int _nextTimedModifierToken = 1;

    private bool HasActiveTimedModifierForAttr(string attr)
    {
        for (int i = 0; i < _activeTimedCombatModifiers.Count; i++)
        {
            if (_activeTimedCombatModifiers[i].Attr.Equals(attr, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>D1-P1-07 (#126/#128 备忘)：向玩家限时活性列表登记 TCombatDuration 战斗修饰并单向重算。</summary>
    public int ApplyTimedCombatModifier(string attr, string effectiveOp, double value)
    {
        if (string.IsNullOrEmpty(attr)) return 0;
        double prevEff = attr.Equals("HealthMax", StringComparison.OrdinalIgnoreCase) ? GetAttribute(attr) : 0;

        if (!HasActiveTimedModifierForAttr(attr))
        {
            SeedPureMultIfExternallySet(attr, CombatAdditiveMult, _combatPureMult, _combatSyncedMult, CombatMult);
            if (CombatAdd.TryGetValue(attr, out var ca)) _combatBaseAdd[attr] = ca; else _combatBaseAdd.Remove(attr);
            if (CombatAdditiveMult.TryGetValue(attr, out var cam)) _combatBaseAdditiveMult[attr] = cam; else _combatBaseAdditiveMult.Remove(attr);
            if (_combatPureMult.TryGetValue(attr, out var cp)) _combatBasePureMult[attr] = cp; else _combatBasePureMult.Remove(attr);
            if (CombatZeroMultCount.TryGetValue(attr, out var cz) && cz > 0) _combatBaseZeroMultCount[attr] = cz; else _combatBaseZeroMultCount.Remove(attr);
        }

        int tokenId = _nextTimedModifierToken++;
        _activeTimedCombatModifiers.Add(new CardInstance.TimedAttributeModifier(tokenId, attr, effectiveOp, value));
        RecomputeCombatAttributeFromBaseline(attr);

        if (attr.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
        {
            double curEff = GetAttribute(attr);
            double delta = curEff - prevEff;
            int cap = Math.Max(1, HealthMax);
            if (delta > 0) CurrentHealth = SafeMath.SaturatingAddInt(CurrentHealth, SafeMath.ToSafeInt(delta, 0, SafeMath.DefaultCombatMax), 0, cap);
            else CurrentHealth = Math.Clamp(CurrentHealth, 0, cap);
        }
        return tokenId;
    }

    /// <summary>D1-P1-07 (#126/#128 备忘) &amp; D1-P2-02：摘除到期限时修饰并单向重算，HealthMax 回退后立即夹紧 CurrentHealth。</summary>
    public bool RevertTimedCombatModifier(int tokenId)
    {
        if (tokenId <= 0) return false;
        int idx = -1;
        for (int i = 0; i < _activeTimedCombatModifiers.Count; i++)
        {
            if (_activeTimedCombatModifiers[i].TokenId == tokenId) { idx = i; break; }
        }
        if (idx < 0) return false;

        string attr = _activeTimedCombatModifiers[idx].Attr;
        _activeTimedCombatModifiers.RemoveAt(idx);
        RecomputeCombatAttributeFromBaseline(attr);

        if (attr.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
            CurrentHealth = Math.Min(CurrentHealth, Math.Max(1, HealthMax));
        return true;
    }

    private void RecomputeCombatAttributeFromBaseline(string attr)
    {
        double add = _combatBaseAdd.GetValueOrDefault(attr, 0.0);
        double addMult = _combatBaseAdditiveMult.GetValueOrDefault(attr, 0.0);
        double pureMult = _combatBasePureMult.GetValueOrDefault(attr, 1.0);
        int zeroCnt = _combatBaseZeroMultCount.GetValueOrDefault(attr, 0);

        for (int i = 0; i < _activeTimedCombatModifiers.Count; i++)
        {
            var m = _activeTimedCombatModifiers[i];
            if (!m.Attr.Equals(attr, StringComparison.OrdinalIgnoreCase)) continue;
            switch (m.EffectiveOp)
            {
                case "Subtract":
                    add -= m.Value;
                    break;
                case "Multiply":
                    if (Math.Abs(m.Value) < 1e-9) zeroCnt++;
                    else pureMult *= m.Value;
                    break;
                case "AdditiveMultiply":
                    addMult += m.Value;
                    break;
                default:
                    add += m.Value;
                    break;
            }
        }

        if (Math.Abs(add) < 1e-9) CombatAdd.Remove(attr); else CombatAdd[attr] = add;
        if (Math.Abs(addMult) < 1e-9) CombatAdditiveMult.Remove(attr); else CombatAdditiveMult[attr] = addMult;
        if (Math.Abs(pureMult - 1.0) < 1e-9) _combatPureMult.Remove(attr); else _combatPureMult[attr] = pureMult;
        if (zeroCnt <= 0) CombatZeroMultCount.Remove(attr); else CombatZeroMultCount[attr] = zeroCnt;
        SyncLayerExposedMult(attr, CombatAdditiveMult, _combatPureMult, _combatSyncedMult, CombatMult, zeroCnt);
    }

    public void RevertCombatAttributeModifier(string attr, string operation, double value)
    {
        switch (operation)
        {
            case "Subtract":
            {
                double next = CombatAdd.GetValueOrDefault(attr, 0) + value;
                if (Math.Abs(next) < 1e-9) CombatAdd.Remove(attr);
                else CombatAdd[attr] = next;
                break;
            }
            case "Multiply":
            {
                if (Math.Abs(value) < 1e-9)
                {
                    if (CombatZeroMultCount.TryGetValue(attr, out int cnt))
                    {
                        if (cnt <= 1) CombatZeroMultCount.Remove(attr);
                        else CombatZeroMultCount[attr] = cnt - 1;
                    }
                }
                else
                {
                    SeedPureMultIfExternallySet(attr, CombatAdditiveMult, _combatPureMult, _combatSyncedMult, CombatMult);
                    double next = _combatPureMult.GetValueOrDefault(attr, 1.0) / value;
                    if (Math.Abs(next - 1.0) < 1e-9) _combatPureMult.Remove(attr);
                    else _combatPureMult[attr] = next;
                }
                SyncLayerExposedMult(attr, CombatAdditiveMult, _combatPureMult, _combatSyncedMult, CombatMult,
                    CombatZeroMultCount.GetValueOrDefault(attr, 0));
                break;
            }
            case "AdditiveMultiply":
            {
                SeedPureMultIfExternallySet(attr, CombatAdditiveMult, _combatPureMult, _combatSyncedMult, CombatMult);
                double next = CombatAdditiveMult.GetValueOrDefault(attr, 0.0) - value;
                if (Math.Abs(next) < 1e-9) CombatAdditiveMult.Remove(attr);
                else CombatAdditiveMult[attr] = next;
                SyncLayerExposedMult(attr, CombatAdditiveMult, _combatPureMult, _combatSyncedMult, CombatMult,
                    CombatZeroMultCount.GetValueOrDefault(attr, 0));
                break;
            }
            default: // Add
            {
                double next = CombatAdd.GetValueOrDefault(attr, 0) - value;
                if (Math.Abs(next) < 1e-9) CombatAdd.Remove(attr);
                else CombatAdd[attr] = next;
                break;
            }
        }
        // D1-P2-02：限时 HealthMax 撤销后立即按最新上限夹紧当前血量
        if (attr.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
            CurrentHealth = Math.Min(CurrentHealth, Math.Max(1, HealthMax));
    }

    public void ApplyAuraAttribute(string attr, string operation, double value)
    {
        switch (operation)
        {
            case "Subtract":
                AuraAdd[attr] = AuraAdd.GetValueOrDefault(attr, 0) - value;
                break;
            case "Multiply":
            {
                SeedPureMultIfExternallySet(attr, AuraAdditiveMult, _auraPureMult, _auraSyncedMult, AuraMult);
                double nextPure = _auraPureMult.GetValueOrDefault(attr, 1.0) * value;
                if (Math.Abs(nextPure - 1.0) < 1e-9) _auraPureMult.Remove(attr);
                else _auraPureMult[attr] = nextPure;
                SyncLayerExposedMult(attr, AuraAdditiveMult, _auraPureMult, _auraSyncedMult, AuraMult);
                break;
            }
            case "AdditiveMultiply":
            {
                SeedPureMultIfExternallySet(attr, AuraAdditiveMult, _auraPureMult, _auraSyncedMult, AuraMult);
                double nextAddMult = AuraAdditiveMult.GetValueOrDefault(attr, 0.0) + value;
                if (Math.Abs(nextAddMult) < 1e-9) AuraAdditiveMult.Remove(attr);
                else AuraAdditiveMult[attr] = nextAddMult;
                SyncLayerExposedMult(attr, AuraAdditiveMult, _auraPureMult, _auraSyncedMult, AuraMult);
                break;
            }
            default:
                AuraAdd[attr] = AuraAdd.GetValueOrDefault(attr, 0) + value;
                break;
        }
    }

    public void ClearAuraModifiers()
    {
        AuraAdd.Clear();
        AuraAdditiveMult.Clear();
        AuraMult.Clear();
        _auraPureMult.Clear();
        _auraSyncedMult.Clear();
    }

    public void ClearEndOfHourModifiers()
    {
        HourAdd.Clear();
        HourAdditiveMult.Clear();
        HourMult.Clear();
        _hourPureMult.Clear();
        _hourSyncedMult.Clear();
        foreach (var c in EnumerateAllOwnedCards())
            c.ClearEndOfHourModifiers();
        CurrentHealth = Math.Min(CurrentHealth, HealthMax);
    }

    public void ClearEndOfDayModifiers()
    {
        DayAdd.Clear();
        DayAdditiveMult.Clear();
        DayMult.Clear();
        _dayPureMult.Clear();
        _daySyncedMult.Clear();
        // D1-P2-05：换日同步清空 Hour 临时层
        HourAdd.Clear();
        HourAdditiveMult.Clear();
        HourMult.Clear();
        _hourPureMult.Clear();
        _hourSyncedMult.Clear();
        foreach (var c in EnumerateAllOwnedCards())
            c.ClearEndOfDayModifiers();
        CurrentHealth = Math.Min(CurrentHealth, HealthMax);
    }

    public void PrepareForCombat()
    {
        CombatAdd.Clear();
        CombatAdditiveMult.Clear();
        CombatMult.Clear();
        CombatZeroMultCount.Clear();
        _combatPureMult.Clear();
        _combatSyncedMult.Clear();
        _combatBaseAdd.Clear();
        _combatBaseAdditiveMult.Clear();
        _combatBasePureMult.Clear();
        _combatBaseZeroMultCount.Clear();
        _activeTimedCombatModifiers.Clear();
        CurrentHealth = HealthMax;
        Shield = 0;
        Burn = 0;
        Poison = 0;
        Rage = 0;
        EnragedRemainingMs = 0;
        Tempo = 0;
        PortraitIndex = 0;
        foreach (var c in Hand.Cards) c.ResetForCombat(null);
        foreach (var c in Stash.Cards) c.ResetForCombat(null);
        foreach (var s in Skills) s.ResetForCombat(null);
        foreach (var se in SocketEffects) se.ResetForCombat(null);
        foreach (var pe in PlayerEffects) pe.ResetForCombat(null);
    }

    public PlayerState CloneForCombat()
    {
        var clone = new PlayerState(PlayerId, DisplayName, Hero, HealthMax, Prestige, Gold, 10)
        {
            IsMonster = IsMonster,
            IsBot = IsBot,
            Day = Day,
            Hour = Hour,
            Wins = Wins,
            Losses = Losses,
            FatesUsed = FatesUsed,
            PortraitIndex = PortraitIndex
        };
        clone.Hand.UnlockedMask = Hand.UnlockedMask;
        clone.Stash.UnlockedMask = Stash.UnlockedMask;
        foreach (var exId in ExclusionSet) clone.ExclusionSet.Add(exId);
        clone.ExclusionSetInitialized = ExclusionSetInitialized;
        foreach (var (k, v) in Attributes) clone.Attributes[k] = v;
        foreach (var (k, v) in HourAdd) clone.HourAdd[k] = v;
        foreach (var (k, v) in HourAdditiveMult) clone.HourAdditiveMult[k] = v;
        foreach (var (k, v) in HourMult) clone.HourMult[k] = v;
        foreach (var (k, v) in _hourPureMult) clone._hourPureMult[k] = v;
        foreach (var (k, v) in _hourSyncedMult) clone._hourSyncedMult[k] = v;
        foreach (var (k, v) in DayAdd) clone.DayAdd[k] = v;
        foreach (var (k, v) in DayAdditiveMult) clone.DayAdditiveMult[k] = v;
        foreach (var (k, v) in DayMult) clone.DayMult[k] = v;
        foreach (var (k, v) in _dayPureMult) clone._dayPureMult[k] = v;
        foreach (var (k, v) in _daySyncedMult) clone._daySyncedMult[k] = v;

        var sourceMap = new Dictionary<string, CardInstance>(StringComparer.Ordinal);
        foreach (var c in Hand.Cards)
        {
            var cc = c.CloneForCombatInternal(resetImmediately: false);
            sourceMap[c.InstanceId] = c;
            clone.Hand.Cards.Add(cc);
        }
        foreach (var c in Stash.Cards)
        {
            var cc = c.CloneForCombatInternal(resetImmediately: false);
            sourceMap[c.InstanceId] = c;
            clone.Stash.Cards.Add(cc);
        }
        foreach (var s in Skills)
        {
            var sc = s.CloneForCombatInternal(resetImmediately: false);
            sourceMap[s.InstanceId] = s;
            clone.Skills.Add(sc);
        }
        foreach (var se in SocketEffects)
        {
            var sec = se.CloneForCombatInternal(resetImmediately: false);
            sourceMap[se.InstanceId] = se;
            clone.SocketEffects.Add(sec);
        }
        foreach (var pe in PlayerEffects)
        {
            var pec = pe.CloneForCombatInternal(resetImmediately: false);
            sourceMap[pe.InstanceId] = pe;
            clone.PlayerEffects.Add(pec);
        }
        clone.CombatSourceCardMap = sourceMap;
        clone.CombatSourcePlayerAttributes = new Dictionary<string, double>(Attributes, StringComparer.OrdinalIgnoreCase);
        clone.CombatSourceDayAdd = new Dictionary<string, double>(DayAdd, StringComparer.OrdinalIgnoreCase);
        clone.CombatSourceDayAdditiveMult = new Dictionary<string, double>(DayAdditiveMult, StringComparer.OrdinalIgnoreCase);
        var srcDayPure = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in GetAllDayPureMultKeys())
            srcDayPure[k] = GetDayPureMult(k);
        clone.CombatSourceDayPureMult = srcDayPure;
        clone.CombatSourceHourAdd = new Dictionary<string, double>(HourAdd, StringComparer.OrdinalIgnoreCase);
        clone.CombatSourceHourAdditiveMult = new Dictionary<string, double>(HourAdditiveMult, StringComparer.OrdinalIgnoreCase);
        var srcHourPure = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in GetAllHourPureMultKeys())
            srcHourPure[k] = GetHourPureMult(k);
        clone.CombatSourceHourPureMult = srcHourPure;
        clone.PrepareForCombat();
        return clone;
    }

    public IReadOnlyList<CardInstance> EnumerateHandAndSkills()
    {
        var snapshot = new List<CardInstance>(Hand.Cards.Count + Skills.Count + SocketEffects.Count);
        snapshot.AddRange(Hand.Cards);
        snapshot.AddRange(Skills);
        snapshot.AddRange(SocketEffects);
        return snapshot;
    }

    public IReadOnlyList<CardInstance> EnumerateAllOwnedCards()
    {
        var snapshot = new List<CardInstance>(Hand.Cards.Count + Stash.Cards.Count + Skills.Count + SocketEffects.Count + PlayerEffects.Count);
        snapshot.AddRange(Hand.Cards);
        snapshot.AddRange(Stash.Cards);
        snapshot.AddRange(Skills);
        snapshot.AddRange(SocketEffects);
        snapshot.AddRange(PlayerEffects);
        return snapshot;
    }
}

/// <summary>P0-05：战后临时变形弹栈还原条目，记录还原卡及其原属容器区域。</summary>
internal sealed record RestoredTemporaryCardEntry(CardInstance Card, bool IsStash, bool IsSkill, bool IsSocketEffect);
