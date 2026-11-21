using System.Text.Json.Serialization;

namespace BazaarCore.Models;

/// <summary>
/// 对应 GameData.db 中 game_modes 表的全局模式参数。
/// </summary>
public sealed class GameModeData
{
    [JsonPropertyName("$type")]
    public string? TypeDiscriminator { get; set; }

    public string Id { get; set; } = string.Empty;
    public string InternalName { get; set; } = string.Empty;
    public string? Version { get; set; }

    public Dictionary<string, double> EncounterSpawnTierPercentages { get; set; } = new();
    public Dictionary<string, Dictionary<string, double>> ItemSkillSpawnTierPercantagesByDay { get; set; } = new();
    public Dictionary<string, double> RewardSpawnTierPercentages { get; set; } = new();

    public int VictoriesToWin { get; set; } = 10;
    public int NumCarpetSocketsUnlockPerLevel { get; set; } = 2;
    public int CombatSimEffectDelay { get; set; } = 250;
    public int CritPercentMultiplier { get; set; } = 200;
    public int NumberOfDays { get; set; } = 10;
    public int HoursInADay { get; set; } = 6;
    public int ExperiencePerLevel { get; set; } = 8;
    public int ExperiencePerHour { get; set; } = 1;
    public int StartingExperience { get; set; } = 0;
    public int MaxLevel { get; set; } = 999;
    public int BurnDecrementByAsPercentage { get; set; } = 3;
    public List<string> ExclusionSet { get; set; } = new();

    public PrestigeSettings Prestige { get; set; } = new();
    public SandstormSettings Sandstorm { get; set; } = new();
    public FatesSettings Fates { get; set; } = new();
    public StandardPricesSettings StandardPrices { get; set; } = new();
}

public sealed class PrestigeSettings
{
    public int PrestigeInitial { get; set; } = 20;
    public int PrestigeMax { get; set; } = 20;
    public int PrestigeLossFromMonsters { get; set; } = 0;
}

public sealed class SandstormSettings
{
    public int BaseTickRate { get; set; } = 250;
    public int TickRateDecrement { get; set; } = 10;
    public int TickRateFloor { get; set; } = 150;
    public int BaseDamage { get; set; } = 1;
    public int AdditiveScalar { get; set; } = 2;
    public int CountdownStartTime { get; set; } = 20000;
    public int CountdownDuration { get; set; } = 10000;
}

public sealed class FatesSettings
{
    public string? TemplateId { get; set; }
    public int SpawnAtOrBelowPrestigeValue { get; set; } = 0;
    public int AfterFatesPrestigeValue { get; set; } = 1;
}

public sealed class StandardPricesSettings
{
    public Dictionary<string, Dictionary<string, int>> ItemBuyPrices { get; set; } = new();
    public Dictionary<string, Dictionary<string, int>> ItemSellPrices { get; set; } = new();
    public Dictionary<string, Dictionary<string, int>> SkillBuyPrices { get; set; } = new();
    public Dictionary<string, Dictionary<string, int>> SkillSellPrices { get; set; } = new();

    public int GetItemBuyPrice(CardSize size, CardTier tier)
    {
        if (ItemBuyPrices.TryGetValue(size.ToString(), out var tiers) && tiers.TryGetValue(tier.ToString(), out var price))
        {
            // 防御 dist/gamemodes.json 将 Small/Medium Legendary 错写为 64 的残缺数据，统一校准为 GameData.db 权威值 24/48
            if (tier == CardTier.Legendary && price == 64)
            {
                if (size == CardSize.Small) return 24;
                if (size == CardSize.Medium) return 48;
            }
            return price;
        }
        if (tier == CardTier.Legendary)
        {
            return size switch
            {
                CardSize.Small => 24,
                CardSize.Medium => 48,
                CardSize.Large => 64,
                _ => (int)size * 24
            };
        }
        return (int)size * (1 << (int)tier) * 2;
    }

    public int GetItemSellPrice(CardSize size, CardTier tier)
    {
        if (ItemSellPrices.TryGetValue(size.ToString(), out var tiers) && tiers.TryGetValue(tier.ToString(), out var price))
        {
            // 防御 dist/gamemodes.json 将 Small/Medium Legendary 错写为 32 的残缺数据，统一校准为 GameData.db 权威值 12/24
            if (tier == CardTier.Legendary && price == 32)
            {
                if (size == CardSize.Small) return 12;
                if (size == CardSize.Medium) return 24;
            }
            return price;
        }
        if (tier == CardTier.Legendary)
        {
            return size switch
            {
                CardSize.Small => 12,
                CardSize.Medium => 24,
                CardSize.Large => 32,
                _ => (int)size * 12
            };
        }
        return (int)size * (1 << (int)tier);
    }

    public int GetSkillBuyPrice(CardTier tier)
    {
        if (SkillBuyPrices.TryGetValue("Medium", out var tiers) && tiers.TryGetValue(tier.ToString(), out var price))
            return price;
        return tier == CardTier.Legendary ? 50 : 5 * (1 << (int)tier);
    }

    public int GetSkillSellPrice(CardTier tier)
    {
        if (SkillSellPrices.TryGetValue("Medium", out var tiers) && tiers.TryGetValue(tier.ToString(), out var price))
            return price;
        return tier switch
        {
            CardTier.Bronze => 2,
            CardTier.Silver => 5,
            CardTier.Gold => 10,
            CardTier.Diamond => 20,
            CardTier.Legendary => 25,
            _ => 2
        };
    }
}

/// <summary>
/// 对应 level_ups 表中的等级奖励配置。
/// </summary>
public sealed class LevelUpTemplate
{
    [JsonPropertyName("$type")]
    public string? TypeDiscriminator { get; set; }

    public int Level { get; set; }
    public int HealthIncrease { get; set; }
    public RuleNode? Rewards { get; set; }
    public RuleNode? TutorialRewards { get; set; }
    public string? Version { get; set; }
}

/// <summary>
/// 对应 monsters 表中的 PvE 野怪模板。
/// </summary>
public sealed class MonsterTemplate
{
    public string Id { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string InternalName { get; set; } = string.Empty;
    public MonsterPlayerData Player { get; set; } = new();
}

public sealed class MonsterPlayerData
{
    public Dictionary<string, double> Attributes { get; set; } = new();
    public MonsterHandData Hand { get; set; } = new();
    public List<MonsterCardEntry> Skills { get; set; } = new();
    public List<MonsterCardEntry> SocketEffects { get; set; } = new();
}

public sealed class MonsterHandData
{
    public int UnlockedSlots { get; set; } = 1023;
    public List<MonsterCardEntry> Items { get; set; } = new();
}

public sealed class MonsterCardEntry
{
    [JsonPropertyName("$type")]
    public string? TypeDiscriminator { get; set; }

    public string TemplateId { get; set; } = string.Empty;
    public string? TemplateVersion { get; set; }
    public string? InstanceId { get; set; }
    public string? SocketId { get; set; }
    public string Tier { get; set; } = "Bronze";
    public string? EnchantmentType { get; set; }
    public Dictionary<string, double>? Attributes { get; set; }
}
