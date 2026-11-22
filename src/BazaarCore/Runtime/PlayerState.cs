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

    public PlayerState(string playerId, string displayName, string hero, int initialHealthMax = 250, int initialPrestige = 20, int initialGold = 10, int unlockedHandSlots = 4)
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

    public int HealthMax => (int)Math.Clamp(Math.Round(GetAttribute("HealthMax")), 1, int.MaxValue);
    public int Gold
    {
        get => (int)Math.Round(GetAttribute("Gold"));
        set => Attributes["Gold"] = Math.Max(0, value);
    }
    public int Prestige
    {
        get => (int)Math.Round(GetAttribute("Prestige"));
        set => Attributes["Prestige"] = Math.Max(0, value);
    }
    public int Level
    {
        get => (int)Math.Round(GetAttribute("Level"));
        set => Attributes["Level"] = Math.Max(1, value);
    }
    public int Experience
    {
        get => (int)Math.Round(GetAttribute("Experience"));
        set => Attributes["Experience"] = Math.Max(0, value);
    }
    public int Income => (int)Math.Round(GetAttribute("Income"));

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
        return baseVal * (1.0 + totalAdditiveMult) * totalMult;
    }

    public void ModifyAttribute(string attr, string operation, double value)
        => ModifyAttribute(attr, operation, value, DurationTarget.Permanent);

    public void ModifyAttribute(string attr, string operation, double value, DurationTarget target)
    {
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
                    CurrentHealth = Math.Clamp((int)Math.Round(nextRt), 0, HealthMax);
                    break;
                case "Shield":
                    Shield = Math.Max(0, (int)Math.Round(nextRt));
                    break;
                case "Burn":
                    Burn = Math.Max(0, (int)Math.Round(nextRt));
                    break;
                case "Poison":
                    Poison = Math.Max(0, (int)Math.Round(nextRt));
                    break;
                case "Rage":
                    Rage = Math.Max(0, (int)Math.Round(nextRt));
                    break;
                case "Tempo":
                    Tempo = Math.Max(0, (int)Math.Round(nextRt));
                    break;
                case "Enraged":
                    if (nextRt <= 0)
                    {
                        EnragedRemainingMs = 0;
                        CombatAdd.Remove("Enraged");
                        CombatAdd.Remove("EnragedDuration");
                    }
                    else
                    {
                        if (EnragedRemainingMs <= 0)
                            EnragedRemainingMs = Math.Max(1000, (int)Math.Round(GetAttribute("EnragedDurationMax")));
                        CombatAdd["Enraged"] = 1.0;
                        CombatAdd["EnragedDuration"] = EnragedRemainingMs;
                    }
                    break;
                case "EnragedDuration":
                    EnragedRemainingMs = Math.Max(0, (int)Math.Round(nextRt));
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
                double delta = next - cur;
                if (delta > 0) CurrentHealth = Math.Min(HealthMax, CurrentHealth + (int)Math.Round(delta));
                else CurrentHealth = Math.Min(CurrentHealth, HealthMax);
            }
            return;
        }

        var (addDict, addMultDict, pureDict, syncedDict, exposedMultDict) = target switch
        {
            DurationTarget.Hour => (HourAdd, HourAdditiveMult, _hourPureMult, _hourSyncedMult, HourMult),
            DurationTarget.Day => (DayAdd, DayAdditiveMult, _dayPureMult, _daySyncedMult, DayMult),
            _ => (CombatAdd, CombatAdditiveMult, _combatPureMult, _combatSyncedMult, CombatMult)
        };

        double prevEff = GetAttribute(attr);
        switch (operation)
        {
            case "Subtract":
                addDict[attr] = addDict.GetValueOrDefault(attr, 0) - value;
                break;
            case "Multiply":
                SeedPureMultIfExternallySet(attr, addMultDict, pureDict, syncedDict, exposedMultDict);
                if (target == DurationTarget.Combat && Math.Abs(value) < 1e-9)
                {
                    CombatZeroMultCount[attr] = CombatZeroMultCount.GetValueOrDefault(attr, 0) + 1;
                }
                else
                {
                    double nextPure = pureDict.GetValueOrDefault(attr, 1.0) * value;
                    if (Math.Abs(nextPure - 1.0) < 1e-9) pureDict.Remove(attr);
                    else pureDict[attr] = nextPure;
                }
                SyncLayerExposedMult(attr, addMultDict, pureDict, syncedDict, exposedMultDict,
                    target == DurationTarget.Combat ? CombatZeroMultCount.GetValueOrDefault(attr, 0) : 0);
                break;
            case "AdditiveMultiply":
            {
                SeedPureMultIfExternallySet(attr, addMultDict, pureDict, syncedDict, exposedMultDict);
                double nextAddMult = addMultDict.GetValueOrDefault(attr, 0.0) + value;
                if (Math.Abs(nextAddMult) < 1e-9) addMultDict.Remove(attr);
                else addMultDict[attr] = nextAddMult;
                SyncLayerExposedMult(attr, addMultDict, pureDict, syncedDict, exposedMultDict,
                    target == DurationTarget.Combat ? CombatZeroMultCount.GetValueOrDefault(attr, 0) : 0);
                break;
            }
            default:
                addDict[attr] = addDict.GetValueOrDefault(attr, 0) + value;
                break;
        }
        if (attr.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
        {
            double curEff = GetAttribute(attr);
            double delta = curEff - prevEff;
            if (delta > 0) CurrentHealth = Math.Min(HealthMax, CurrentHealth + (int)Math.Round(delta));
            else CurrentHealth = Math.Min(CurrentHealth, HealthMax);
        }
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
        var snapshot = new List<CardInstance>(Hand.Cards.Count + Stash.Cards.Count + Skills.Count + SocketEffects.Count);
        snapshot.AddRange(Hand.Cards);
        snapshot.AddRange(Stash.Cards);
        snapshot.AddRange(Skills);
        snapshot.AddRange(SocketEffects);
        return snapshot;
    }
}
