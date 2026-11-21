using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace BazaarLauncher;

/// <summary>
/// CustomGameplayUI 数据模型与规则状态摘要工具（与服务端 /custom-rules/* 及 /lobbies/* 契约严格对齐）。
/// </summary>
internal sealed partial class CustomGameplayUI
{
    internal enum UiTab
    {
        Normal = 0,
        Tournament = 1,
    }

    internal sealed class ArchetypeCatalogEntry
    {
        [JsonProperty("bit")] public int Bit { get; set; }
        [JsonProperty("key")] public string Key { get; set; } = "";
        [JsonProperty("nameZh")] public string NameZh { get; set; } = "";
        [JsonProperty("descZh")] public string DescZh { get; set; } = "";
        [JsonProperty("heroCounts")] public Dictionary<string, int> HeroCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public int GetHeroCount(string hero)
        {
            if (HeroCounts == null || string.IsNullOrWhiteSpace(hero)) return 0;
            if (HeroCounts.TryGetValue(hero, out int c)) return c;
            foreach (var kv in HeroCounts)
            {
                if (string.Equals(kv.Key, hero, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return 0;
        }
    }

    internal sealed class EncounterCategoryEntry
    {
        [JsonProperty("key")] public string Key { get; set; } = "";
        [JsonProperty("groupZh")] public string GroupZh { get; set; } = "";
        [JsonProperty("nameZh")] public string NameZh { get; set; } = "";
        [JsonProperty("defaultMult")] public double DefaultMult { get; set; } = 1.0;
    }

    internal sealed class CatalogCardEntry
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("nameZh")] public string NameZh { get; set; } = "";
        [JsonProperty("hero")] public string Hero { get; set; } = "";
        [JsonProperty("size")] public string Size { get; set; } = "";
        [JsonProperty("startingTier")] public string StartingTier { get; set; } = "Bronze";
        [JsonProperty("archetypeMask")] public ulong ArchetypeMask { get; set; }
    }

    internal sealed class CustomRulesCatalog
    {
        [JsonProperty("archetypes")] public List<ArchetypeCatalogEntry> Archetypes { get; set; } = new();
        [JsonProperty("encounterCategories")] public List<EncounterCategoryEntry> EncounterCategories { get; set; } = new();
        [JsonProperty("items")] public List<CatalogCardEntry> Items { get; set; } = new();
        [JsonProperty("skills")] public List<CatalogCardEntry> Skills { get; set; } = new();
    }

    internal sealed class StartingItemRule
    {
        [JsonProperty("templateId")] public string TemplateId { get; set; } = "";
        [JsonProperty("tier")] public string Tier { get; set; } = "Bronze";
        [JsonProperty("enchantment")] public string Enchantment { get; set; }
    }

    internal sealed class StartingSkillRule
    {
        [JsonProperty("templateId")] public string TemplateId { get; set; } = "";
        [JsonProperty("tier")] public string Tier { get; set; } = "Bronze";
    }

    internal sealed class CustomRunRuleSet
    {
        [JsonProperty("selectedArchetypeMask")] public ulong SelectedArchetypeMask { get; set; }
        [JsonProperty("allowTendencySelection")] public bool AllowTendencySelection { get; set; } = true;
        [JsonProperty("promptTendencyBeforeRun")] public bool PromptTendencyBeforeRun { get; set; } = true;
        [JsonProperty("startingGold")] public int StartingGold { get; set; } = 15;
        [JsonProperty("startingIncome")] public int StartingIncome { get; set; } = 5;
        [JsonProperty("startingExperience")] public int StartingExperience { get; set; } = 3;
        [JsonProperty("startingPrestige")] public int StartingPrestige { get; set; } = 20;
        [JsonProperty("startingHealth")] public int StartingHealth { get; set; } = 250;
        [JsonProperty("startingItems")] public List<StartingItemRule> StartingItems { get; set; } = new();
        [JsonProperty("startingSkills")] public List<StartingSkillRule> StartingSkills { get; set; } = new();
        [JsonProperty("monsterLootChooseOne")] public bool MonsterLootChooseOne { get; set; } = true; // 默认开启野怪多选一
        [JsonProperty("encounterCategoryMultipliers")] public Dictionary<string, double> EncounterCategoryMultipliers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonProperty("bannedEncounterIds")] public List<string> BannedEncounterIds { get; set; } = new();
        [JsonProperty("playerCap")] public int PlayerCap { get; set; } = 8;
        [JsonProperty("botNonLethalPrestigeLock")] public bool BotNonLethalPrestigeLock { get; set; } = true;
        [JsonProperty("humanMatchEvery4Rounds")] public bool HumanMatchEvery4Rounds { get; set; } = true;
        [JsonProperty("lastHumanStandingMode")] public bool LastHumanStandingMode { get; set; } = true;
        [JsonProperty("isCustomPowerModified")] public bool IsCustomPowerModified { get; set; }

        public void EnsureCollections()
        {
            StartingItems ??= new List<StartingItemRule>();
            StartingSkills ??= new List<StartingSkillRule>();
            EncounterCategoryMultipliers ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            BannedEncounterIds ??= new List<string>();
        }

        public void ResetToStandardValues(bool preserveArchetypeAndLobbyFlags = false)
        {
            ulong mask = preserveArchetypeAndLobbyFlags ? SelectedArchetypeMask : 0UL;
            bool allowTend = !preserveArchetypeAndLobbyFlags || AllowTendencySelection;
            bool promptBefore = !preserveArchetypeAndLobbyFlags || PromptTendencyBeforeRun;
            int cap = preserveArchetypeAndLobbyFlags ? (PlayerCap == 10 ? 10 : 8) : 8;
            bool botLock = !preserveArchetypeAndLobbyFlags || BotNonLethalPrestigeLock;
            bool match4 = !preserveArchetypeAndLobbyFlags || HumanMatchEvery4Rounds;
            bool lastHuman = !preserveArchetypeAndLobbyFlags || LastHumanStandingMode;

            SelectedArchetypeMask = mask;
            AllowTendencySelection = allowTend;
            PromptTendencyBeforeRun = promptBefore;
            StartingGold = 15;
            StartingIncome = 5;
            StartingExperience = 3;
            StartingPrestige = 20;
            StartingHealth = 250;
            StartingItems = new List<StartingItemRule>();
            StartingSkills = new List<StartingSkillRule>();
            MonsterLootChooseOne = true;
            EncounterCategoryMultipliers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            BannedEncounterIds = new List<string>();
            PlayerCap = cap;
            BotNonLethalPrestigeLock = botLock;
            HumanMatchEvery4Rounds = match4;
            LastHumanStandingMode = lastHuman;
            IsCustomPowerModified = false;
        }

        public bool ComputeCustomPowerModified()
        {
            EnsureCollections();
            if (StartingGold != 15 || StartingIncome != 5 || StartingExperience != 3
                || StartingPrestige != 20 || StartingHealth != 250)
            {
                return true;
            }
            if (StartingItems.Count > 0 || StartingSkills.Count > 0) return true;
            if (BannedEncounterIds.Count > 0) return true;
            foreach (var kv in EncounterCategoryMultipliers)
            {
                if (Math.Abs(kv.Value - 1.0) > 0.001) return true;
            }
            return false;
        }

        public void RefreshPowerFlag() => IsCustomPowerModified = ComputeCustomPowerModified();

        public CustomRunRuleSet Clone()
        {
            var json = JsonConvert.SerializeObject(this);
            var copy = JsonConvert.DeserializeObject<CustomRunRuleSet>(json) ?? new CustomRunRuleSet();
            copy.EnsureCollections();
            copy.RefreshPowerFlag();
            return copy;
        }
    }

    internal sealed class LobbyMemberSummary
    {
        public string AccountId { get; set; } = "";
        public string Username { get; set; } = "";
        public string Hero { get; set; } = "";
        public string State { get; set; } = "";
        public bool IsAi { get; set; }
    }

    internal sealed class LobbyDetailSnapshot
    {
        public string LobbyId { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string HostAccountId { get; set; } = "";
        public int PlayerCap { get; set; } = 8;
        public List<LobbyMemberSummary> Members { get; set; } = new();
        public CustomRunRuleSet Rules { get; set; }
    }

    internal sealed class RuleEditorBuffer
    {
        public string GoldText = "15";
        public string IncomeText = "5";
        public string ExperienceText = "3";
        public string PrestigeText = "20";
        public string HealthText = "250";

        public string ItemSearch = "";
        public bool ItemHeroFilterOnly = true;
        public int ItemTierIdx;
        public int ItemEnchantIdx;

        public string SkillSearch = "";
        public bool SkillHeroFilterOnly = true;
        public int SkillTierIdx;

        public void SyncFromRules(CustomRunRuleSet rules)
        {
            if (rules == null) return;
            GoldText = rules.StartingGold.ToString();
            IncomeText = rules.StartingIncome.ToString();
            ExperienceText = rules.StartingExperience.ToString();
            PrestigeText = rules.StartingPrestige.ToString();
            HealthText = rules.StartingHealth.ToString();
        }
    }

    internal readonly struct RuleActivationSummary
    {
        public readonly bool IsCustomActive;
        public readonly bool IsPowerModified;
        public readonly int ActiveCount;
        public readonly string SummaryText;

        public RuleActivationSummary(bool isCustomActive, bool isPowerModified, int activeCount, string summaryText)
        {
            IsCustomActive = isCustomActive;
            IsPowerModified = isPowerModified;
            ActiveCount = activeCount;
            SummaryText = summaryText;
        }
    }

    internal static RuleActivationSummary SummarizeRules(CustomRunRuleSet rules, bool isTournament)
    {
        if (rules == null)
        {
            return new RuleActivationSummary(false, false, 0, "官方默认");
        }
        rules.EnsureCollections();

        var tags = new List<string>(6);

        int archCount = CountBits(rules.SelectedArchetypeMask);
        if (archCount > 0)
        {
            tags.Add($"流派倾向×{archCount}");
        }

        if (rules.StartingGold != 15 || rules.StartingIncome != 5 || rules.StartingExperience != 3
            || rules.StartingPrestige != 20 || rules.StartingHealth != 250)
        {
            tags.Add("开局预设");
        }

        int cardCount = rules.StartingItems.Count + rules.StartingSkills.Count;
        if (cardCount > 0)
        {
            tags.Add($"固定发牌×{cardCount}");
        }

        if (!rules.MonsterLootChooseOne)
        {
            tags.Add("野怪单掉");
        }

        bool hasMultOverride = false;
        foreach (var kv in rules.EncounterCategoryMultipliers)
        {
            if (Math.Abs(kv.Value - 1.0) > 0.001)
            {
                hasMultOverride = true;
                break;
            }
        }
        if (hasMultOverride || rules.BannedEncounterIds.Count > 0)
        {
            tags.Add("事件调整");
        }

        if (isTournament && (rules.PlayerCap == 10 || !rules.BotNonLethalPrestigeLock
                             || !rules.HumanMatchEvery4Rounds || !rules.LastHumanStandingMode
                             || !rules.AllowTendencySelection))
        {
            tags.Add(rules.PlayerCap == 10 ? "10人赛制" : "赛制调整");
        }

        bool powerMod = rules.ComputeCustomPowerModified();
        bool customActive = tags.Count > 0;
        string summary = customActive ? string.Join(" / ", tags) : "官方默认";
        return new RuleActivationSummary(customActive, powerMod, tags.Count, summary);
    }

    private static readonly string[] Tiers = { "Bronze", "Silver", "Gold", "Diamond" };
    private static readonly string[] TierLabelsZh = { "铜 Bronze", "银 Silver", "金 Gold", "钻 Diamond" };

    private static readonly (string Key, string LabelZh)[] Enchantments =
    {
        ("", "无附魔"),
        ("Fiery", "炽焰 Fiery"),
        ("Toxic", "剧毒 Toxic"),
        ("Shielded", "护盾 Shielded"),
        ("Heavy", "沉重 Heavy"),
        ("Icy", "冰霜 Icy"),
        ("Turbo", "极速 Turbo"),
        ("Restorative", "治愈 Restorative"),
        ("Deadly", "致命 Deadly"),
        ("Golden", "黄金 Golden"),
        ("Radiant", "辉光 Radiant"),
        ("Obsidian", "黑曜 Obsidian"),
        ("Shiny", "闪耀 Shiny"),
        ("Mossy", "青苔 Mossy"),
        ("Mystical", "秘法 Mystical"),
        ("Ethereal", "虚灵 Ethereal"),
        ("Tiny", "微型 Tiny"),
    };

    private static readonly (string Key, string ShortZh)[] QuickSwitchHeroes =
    {
        ("Vanessa", "瓦内莎"),
        ("Pygmalien", "皮格马利翁"),
        ("Dooley", "杜利"),
        ("Mak", "马克"),
        ("Jules", "朱尔斯"),
        ("Stelle", "斯黛拉"),
        ("Karnok", "卡诺克"),
        ("Hero8", "双龙"),
    };

    private static readonly Dictionary<string, string> HeroNameZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Vanessa"] = "瓦内莎 Vanessa",
        ["Pygmalien"] = "皮格马利翁 Pygmalien",
        ["Dooley"] = "杜利 Dooley",
        ["Mak"] = "马克 Mak",
        ["Stelle"] = "斯黛拉 Stelle",
        ["Jules"] = "朱尔斯 Jules",
        ["Karnok"] = "卡诺克 Karnok",
        ["Hero8"] = "双龙 The Dragons",
        ["Common"] = "通用 Common",
    };

    private static readonly List<EncounterCategoryEntry> FallbackEncounterCategories = new()
    {
        new() { Key = "merchant_weapon", GroupZh = "商人", NameZh = "武器/战斗商人", DefaultMult = 1.0 },
        new() { Key = "merchant_defense", GroupZh = "商人", NameZh = "护盾/回复商人", DefaultMult = 1.0 },
        new() { Key = "merchant_element", GroupZh = "商人", NameZh = "灼烧/剧毒/元素商人", DefaultMult = 1.0 },
        new() { Key = "merchant_special", GroupZh = "商人", NameZh = "特殊/藏品/综合商人", DefaultMult = 1.0 },
        new() { Key = "event_economy", GroupZh = "奇遇事件", NameZh = "经济/宝箱/理财奇遇", DefaultMult = 1.0 },
        new() { Key = "event_upgrade", GroupZh = "奇遇事件", NameZh = "升级/技能/成长奇遇", DefaultMult = 1.0 },
        new() { Key = "event_special", GroupZh = "奇遇事件", NameZh = "命运/远征/特殊奇遇", DefaultMult = 1.0 },
        new() { Key = "monster_pve", GroupZh = "野怪战斗", NameZh = "常规野怪战斗", DefaultMult = 1.0 },
        new() { Key = "pedestal_enchant", GroupZh = "强化基座", NameZh = "附魔祭坛/附魔基座", DefaultMult = 1.0 },
        new() { Key = "pedestal_upgrade", GroupZh = "强化基座", NameZh = "升级/打造强化基座", DefaultMult = 1.0 },
    };
}
