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
    public Dictionary<string, double> HourMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当日临时玩家属性（UntilEndOfDay）。</summary>
    public Dictionary<string, double> DayAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> DayMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>单场战斗临时玩家属性（UntilEndOfCombat / TCombatDuration）。</summary>
    public Dictionary<string, double> CombatAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> CombatMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>光环动态加成属性。</summary>
    public Dictionary<string, double> AuraAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> AuraMult { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    public int HealthMax => Math.Max(1, (int)Math.Round(GetAttribute("HealthMax")));
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

    public double GetAttributeUnscaled(string attr)
        => Attributes.GetValueOrDefault(attr, 0);

    public double GetAttribute(string attr)
    {
        switch (attr)
        {
            case "Health": return CurrentHealth;
            case "Shield": return Shield;
            case "Burn": return Burn;
            case "Poison": return Poison;
            case "Rage": return Rage;
            case "Tempo": return Tempo;
        }
        var baseVal = Attributes.GetValueOrDefault(attr, 0)
                    + HourAdd.GetValueOrDefault(attr, 0)
                    + DayAdd.GetValueOrDefault(attr, 0)
                    + CombatAdd.GetValueOrDefault(attr, 0)
                    + AuraAdd.GetValueOrDefault(attr, 0);
        double mult = 1.0;
        if (HourMult.TryGetValue(attr, out var hm)) mult *= hm;
        if (DayMult.TryGetValue(attr, out var dm)) mult *= dm;
        if (CombatMult.TryGetValue(attr, out var cm)) mult *= cm;
        if (AuraMult.TryGetValue(attr, out var am)) mult *= am;
        return baseVal * mult;
    }

    public void ModifyAttribute(string attr, string operation, double value)
        => ModifyAttribute(attr, operation, value, DurationTarget.Permanent);

    public void ModifyAttribute(string attr, string operation, double value, DurationTarget target)
    {
        // 战斗实时池（Health/Shield/Burn/Poison/Rage/Tempo）直接作用于实时属性
        if (attr is "Health" or "Shield" or "Burn" or "Poison" or "Rage" or "Tempo")
        {
            double curRt = GetAttribute(attr);
            double nextRt = operation switch
            {
                "Subtract" => curRt - value,
                "Multiply" => curRt * value,
                "AdditiveMultiply" => curRt * (1.0 + value),
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

        var (addDict, multDict) = target switch
        {
            DurationTarget.Hour => (HourAdd, HourMult),
            DurationTarget.Day => (DayAdd, DayMult),
            _ => (CombatAdd, CombatMult)
        };

        double prevEff = GetAttribute(attr);
        switch (operation)
        {
            case "Subtract":
                addDict[attr] = addDict.GetValueOrDefault(attr, 0) - value;
                break;
            case "Multiply":
                multDict[attr] = multDict.GetValueOrDefault(attr, 1.0) * value;
                break;
            case "AdditiveMultiply":
                multDict[attr] = multDict.GetValueOrDefault(attr, 1.0) + value;
                break;
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

    public void ApplyAuraAttribute(string attr, string operation, double value)
    {
        switch (operation)
        {
            case "Subtract":
                AuraAdd[attr] = AuraAdd.GetValueOrDefault(attr, 0) - value;
                break;
            case "Multiply":
                AuraMult[attr] = AuraMult.GetValueOrDefault(attr, 1.0) * value;
                break;
            case "AdditiveMultiply":
                AuraMult[attr] = AuraMult.GetValueOrDefault(attr, 1.0) + value;
                break;
            default:
                AuraAdd[attr] = AuraAdd.GetValueOrDefault(attr, 0) + value;
                break;
        }
    }

    public void ClearAuraModifiers()
    {
        AuraAdd.Clear();
        AuraMult.Clear();
    }

    public void ClearEndOfHourModifiers()
    {
        HourAdd.Clear();
        HourMult.Clear();
        foreach (var c in EnumerateAllOwnedCards())
            c.ClearEndOfHourModifiers();
        CurrentHealth = Math.Min(CurrentHealth, HealthMax);
    }

    public void ClearEndOfDayModifiers()
    {
        DayAdd.Clear();
        DayMult.Clear();
        foreach (var c in EnumerateAllOwnedCards())
            c.ClearEndOfDayModifiers();
        CurrentHealth = Math.Min(CurrentHealth, HealthMax);
    }

    public void PrepareForCombat()
    {
        CombatAdd.Clear();
        CombatMult.Clear();
        CurrentHealth = HealthMax;
        Shield = 0;
        Burn = 0;
        Poison = 0;
        Rage = 0;
        EnragedRemainingMs = 0;
        Tempo = 0;
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
        foreach (var (k, v) in HourMult) clone.HourMult[k] = v;
        foreach (var (k, v) in DayAdd) clone.DayAdd[k] = v;
        foreach (var (k, v) in DayMult) clone.DayMult[k] = v;

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
