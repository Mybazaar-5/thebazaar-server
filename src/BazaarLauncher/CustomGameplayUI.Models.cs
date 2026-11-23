using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace BazaarLauncher;

/// <summary>
/// CustomGameplayUI 数据模型、规则摘要与零序列化手写深拷贝工具（修复 P1-1 每 0.15s 全量 JSON 克隆 GC 风暴，
/// 并完整对齐 Wave 2/3 的 3 套预设档位 + 7 大玩法模式 + 14 条肉鸽突变词条）。
/// </summary>
internal sealed partial class CustomGameplayUI
{
    internal enum UiTab
    {
        Normal = 0,
        Tournament = 1,
        Workshop = 2,
        Social = 3,
        Performance = 4,
    }

    internal sealed class ArchetypeCatalogEntry
    {
        [JsonProperty("bit")] public int Bit { get; set; }
        [JsonProperty("key")] public string Key { get; set; } = string.Empty;
        [JsonProperty("nameZh")] public string NameZh { get; set; } = string.Empty;
        [JsonProperty("descZh")] public string DescZh { get; set; } = string.Empty;
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
        [JsonProperty("key")] public string Key { get; set; } = string.Empty;
        [JsonProperty("groupZh")] public string GroupZh { get; set; } = string.Empty;
        [JsonProperty("nameZh")] public string NameZh { get; set; } = string.Empty;
        [JsonProperty("defaultMult")] public double DefaultMult { get; set; } = 1.0;
    }

    internal sealed class CatalogCardEntry
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("nameZh")] public string NameZh { get; set; } = string.Empty;
        [JsonProperty("hero")] public string Hero { get; set; } = string.Empty;
        [JsonProperty("size")] public string Size { get; set; } = string.Empty;
        [JsonProperty("startingTier")] public string StartingTier { get; set; } = "Bronze";
        [JsonProperty("archetypeMask")] public ulong ArchetypeMask { get; set; }
    }

    internal sealed class MutatorCatalogItem
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("nameZh")] public string NameZh { get; set; } = string.Empty;
        [JsonProperty("descZh")] public string DescZh { get; set; } = string.Empty;
        [JsonProperty("conflictGroup")] public string ConflictGroup { get; set; }
        [JsonProperty("scoreMultiplierDelta")] public double ScoreMultiplierDelta { get; set; }
    }

    internal sealed class CustomRulesCatalog
    {
        [JsonProperty("archetypes")] public List<ArchetypeCatalogEntry> Archetypes { get; set; } = new();
        [JsonProperty("encounterCategories")] public List<EncounterCategoryEntry> EncounterCategories { get; set; } = new();
        [JsonProperty("items")] public List<CatalogCardEntry> Items { get; set; } = new();
        [JsonProperty("skills")] public List<CatalogCardEntry> Skills { get; set; } = new();
        [JsonProperty("mutators")] public List<MutatorCatalogItem> Mutators { get; set; } = new();
    }

    internal sealed class StartingItemRule
    {
        [JsonProperty("templateId")] public string TemplateId { get; set; } = string.Empty;
        [JsonProperty("tier")] public string Tier { get; set; } = "Bronze";
        [JsonProperty("enchantment")] public string Enchantment { get; set; }
    }

    internal sealed class StartingSkillRule
    {
        [JsonProperty("templateId")] public string TemplateId { get; set; } = string.Empty;
        [JsonProperty("tier")] public string Tier { get; set; } = "Bronze";
    }

    internal sealed class CustomRunRuleSet
    {
        [JsonProperty("selectedArchetypeMask")] public ulong SelectedArchetypeMask { get; set; }
        [JsonProperty("archetypeWeightMultiplier")] public double ArchetypeWeightMultiplier { get; set; } = 2.0;
        [JsonProperty("archetypeMultiMatchCap")] public double ArchetypeMultiMatchCap { get; set; } = 3.0;
        [JsonProperty("allowTendencySelection")] public bool AllowTendencySelection { get; set; } = true;
        [JsonProperty("promptTendencyBeforeRun")] public bool PromptTendencyBeforeRun { get; set; } = true;
        [JsonProperty("startingGold")] public int StartingGold { get; set; } = 15;
        [JsonProperty("startingIncome")] public int StartingIncome { get; set; } = 5;
        [JsonProperty("startingExperience")] public int StartingExperience { get; set; } = 3;
        [JsonProperty("startingPrestige")] public int StartingPrestige { get; set; } = 20;
        [JsonProperty("startingHealth")] public int StartingHealth { get; set; } = 300;
        [JsonProperty("startingItems")] public List<StartingItemRule> StartingItems { get; set; } = new();
        [JsonProperty("startingSkills")] public List<StartingSkillRule> StartingSkills { get; set; } = new();
        [JsonProperty("monsterLootChooseOne")] public bool MonsterLootChooseOne { get; set; } = true;
        [JsonProperty("encounterCategoryMultipliers")] public Dictionary<string, double> EncounterCategoryMultipliers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonProperty("bannedEncounterIds")] public List<string> BannedEncounterIds { get; set; } = new();
        [JsonProperty("playerCap")] public int PlayerCap { get; set; } = 8;
        [JsonProperty("botNonLethalPrestigeLock")] public bool BotNonLethalPrestigeLock { get; set; } = true;
        [JsonProperty("humanMatchEvery4Rounds")] public bool HumanMatchEvery4Rounds { get; set; } = true;
        [JsonProperty("lastHumanStandingMode")] public bool LastHumanStandingMode { get; set; } = true;
        [JsonProperty("isCustomPowerModified")] public bool IsCustomPowerModified { get; set; }

        // Wave 2/3 新增：预设档位 + 7 大模式 + 14 突变词条 + 经济/掉率/种子调参（完整对齐服务端 CustomRuleModels）
        [JsonProperty("presetId")] public string PresetId { get; set; } = "PureOfficial";
        [JsonProperty("specialModeId")] public string SpecialModeId { get; set; } = "Standard";
        [JsonProperty("mutatorIds")] public List<string> MutatorIds { get; set; } = new();
        [JsonProperty("dailyChallengeDate")] public string DailyChallengeDate { get; set; }
        [JsonProperty("fixedRunSeed")] public int? FixedRunSeed { get; set; }
        [JsonProperty("endlessMaxDay")] public int EndlessMaxDay { get; set; } = 99;
        [JsonProperty("crossHeroOwnHeroBias")] public double CrossHeroOwnHeroBias { get; set; } = 0.40;
        [JsonProperty("bossRushTotalWaves")] public int BossRushTotalWaves { get; set; } = 10;
        [JsonProperty("bossRushIncludeMilestonePvp")] public bool BossRushIncludeMilestonePvp { get; set; } = true;
        [JsonProperty("sandboxFreeInfiniteReroll")] public bool SandboxFreeInfiniteReroll { get; set; } = true;
        [JsonProperty("sandboxZeroShopPrice")] public bool SandboxZeroShopPrice { get; set; } = false;
        [JsonProperty("shopHighTierBonusPct")] public double ShopHighTierBonusPct { get; set; } = 0.0;
        [JsonProperty("shopPriceMultiplier")] public double ShopPriceMultiplier { get; set; } = 1.0;
        [JsonProperty("monsterLootBonusChoices")] public int MonsterLootBonusChoices { get; set; } = 0;
        [JsonProperty("monsterLootTierBonus")] public int MonsterLootTierBonus { get; set; } = 0;
        [JsonProperty("enemyStatMultiplier")] public double EnemyStatMultiplier { get; set; } = 1.0;
        [JsonProperty("disableFatesRevive")] public bool DisableFatesRevive { get; set; } = false;
        [JsonProperty("selectedModIds")] public List<string> SelectedModIds { get; set; } = new();

        public void EnsureCollections()
        {
            StartingItems ??= new List<StartingItemRule>();
            StartingSkills ??= new List<StartingSkillRule>();
            EncounterCategoryMultipliers ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            BannedEncounterIds ??= new List<string>();
            MutatorIds ??= new List<string>();
            SelectedModIds ??= new List<string>();
            if (string.IsNullOrWhiteSpace(PresetId)) PresetId = "PureOfficial";
            if (string.IsNullOrWhiteSpace(SpecialModeId)) SpecialModeId = "Standard";
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
            ArchetypeWeightMultiplier = 2.0;
            ArchetypeMultiMatchCap = 3.0;
            AllowTendencySelection = allowTend;
            PromptTendencyBeforeRun = promptBefore;
            StartingGold = 15;
            StartingIncome = 5;
            StartingExperience = 3;
            StartingPrestige = 20;
            StartingHealth = 300;
            StartingItems = new List<StartingItemRule>();
            StartingSkills = new List<StartingSkillRule>();
            MonsterLootChooseOne = true;
            EncounterCategoryMultipliers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            BannedEncounterIds = new List<string>();
            PlayerCap = cap;
            BotNonLethalPrestigeLock = botLock;
            HumanMatchEvery4Rounds = match4;
            LastHumanStandingMode = lastHuman;
            PresetId = "PureOfficial";
            SpecialModeId = "Standard";
            MutatorIds = new List<string>();
            DailyChallengeDate = null;
            FixedRunSeed = null;
            EndlessMaxDay = 99;
            CrossHeroOwnHeroBias = 0.40;
            BossRushTotalWaves = 10;
            BossRushIncludeMilestonePvp = true;
            SandboxFreeInfiniteReroll = true;
            SandboxZeroShopPrice = false;
            ShopHighTierBonusPct = 0.0;
            ShopPriceMultiplier = 1.0;
            MonsterLootBonusChoices = 0;
            MonsterLootTierBonus = 0;
            EnemyStatMultiplier = 1.0;
            DisableFatesRevive = false;
            SelectedModIds = new List<string>();
            IsCustomPowerModified = false;
        }

        public bool ComputeCustomPowerModified()
        {
            EnsureCollections();
            if (!string.Equals(SpecialModeId, "Standard", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(PresetId, "PureOfficial", StringComparison.OrdinalIgnoreCase)
                || MutatorIds.Count > 0
                || SelectedModIds.Count > 0)
            {
                return true;
            }
            if (StartingGold != 15 || StartingIncome != 5 || StartingExperience != 3
                || StartingPrestige != 20 || StartingHealth != 300
                || Math.Abs(ShopHighTierBonusPct) > 1e-6
                || Math.Abs(ShopPriceMultiplier - 1.0) > 1e-6
                || MonsterLootBonusChoices != 0
                || MonsterLootTierBonus != 0
                || Math.Abs(EnemyStatMultiplier - 1.0) > 1e-6
                || DisableFatesRevive)
            {
                return true;
            }
            if (StartingItems.Count > 0 || StartingSkills.Count > 0) return true;
            if (BannedEncounterIds.Count > 0) return true;
            foreach (var kv in EncounterCategoryMultipliers)
            {
                if (kv.Value < 0.8 || kv.Value > 1.25) return true;
            }
            return false;
        }

        public void RefreshPowerFlag() => IsCustomPowerModified = ComputeCustomPowerModified();

        /// <summary>
        /// 高性能手写成员深拷贝（彻底替代 JsonConvert.SerializeObject/DeserializeObject，根治 P1-1 托管 GC 尖峰）。
        /// </summary>
        public CustomRunRuleSet Clone()
        {
            EnsureCollections();
            var copy = new CustomRunRuleSet
            {
                SelectedArchetypeMask = SelectedArchetypeMask,
                ArchetypeWeightMultiplier = ArchetypeWeightMultiplier,
                ArchetypeMultiMatchCap = ArchetypeMultiMatchCap,
                AllowTendencySelection = AllowTendencySelection,
                PromptTendencyBeforeRun = PromptTendencyBeforeRun,
                StartingGold = StartingGold,
                StartingIncome = StartingIncome,
                StartingExperience = StartingExperience,
                StartingPrestige = StartingPrestige,
                StartingHealth = StartingHealth,
                StartingItems = new List<StartingItemRule>(StartingItems.Count),
                StartingSkills = new List<StartingSkillRule>(StartingSkills.Count),
                MonsterLootChooseOne = MonsterLootChooseOne,
                EncounterCategoryMultipliers = new Dictionary<string, double>(EncounterCategoryMultipliers, StringComparer.OrdinalIgnoreCase),
                BannedEncounterIds = new List<string>(BannedEncounterIds),
                PlayerCap = PlayerCap,
                BotNonLethalPrestigeLock = BotNonLethalPrestigeLock,
                HumanMatchEvery4Rounds = HumanMatchEvery4Rounds,
                LastHumanStandingMode = LastHumanStandingMode,
                PresetId = PresetId,
                SpecialModeId = SpecialModeId,
                MutatorIds = new List<string>(MutatorIds),
                DailyChallengeDate = DailyChallengeDate,
                FixedRunSeed = FixedRunSeed,
                EndlessMaxDay = EndlessMaxDay,
                CrossHeroOwnHeroBias = CrossHeroOwnHeroBias,
                BossRushTotalWaves = BossRushTotalWaves,
                BossRushIncludeMilestonePvp = BossRushIncludeMilestonePvp,
                SandboxFreeInfiniteReroll = SandboxFreeInfiniteReroll,
                SandboxZeroShopPrice = SandboxZeroShopPrice,
                ShopHighTierBonusPct = ShopHighTierBonusPct,
                ShopPriceMultiplier = ShopPriceMultiplier,
                MonsterLootBonusChoices = MonsterLootBonusChoices,
                MonsterLootTierBonus = MonsterLootTierBonus,
                EnemyStatMultiplier = EnemyStatMultiplier,
                DisableFatesRevive = DisableFatesRevive,
                SelectedModIds = new List<string>(SelectedModIds),
            };

            for (int i = 0; i < StartingItems.Count; i++)
            {
                var it = StartingItems[i];
                if (it != null)
                {
                    copy.StartingItems.Add(new StartingItemRule
                    {
                        TemplateId = it.TemplateId,
                        Tier = it.Tier,
                        Enchantment = it.Enchantment,
                    });
                }
            }

            for (int i = 0; i < StartingSkills.Count; i++)
            {
                var sk = StartingSkills[i];
                if (sk != null)
                {
                    copy.StartingSkills.Add(new StartingSkillRule
                    {
                        TemplateId = sk.TemplateId,
                        Tier = sk.Tier,
                    });
                }
            }

            copy.RefreshPowerFlag();
            return copy;
        }
    }

    internal sealed class LobbyMemberSummary
    {
        public string AccountId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Hero { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public bool IsAi { get; set; }
    }

    internal sealed class RequiredModEntry
    {
        public string ModId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public int CustomCardCount { get; set; }
        public int CustomEventCount { get; set; }
        public int AssetCount { get; set; }
        public bool AvailableOnServer { get; set; } = true;
        public string DownloadUrl { get; set; } = string.Empty;
    }

    internal sealed class LobbyDetailSnapshot
    {
        public string LobbyId { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string HostAccountId { get; set; } = string.Empty;
        public int PlayerCap { get; set; } = 8;
        public List<LobbyMemberSummary> Members { get; set; } = new();
        public List<RequiredModEntry> RequiredMods { get; set; } = new();
        public CustomRunRuleSet Rules { get; set; }
    }

    internal sealed class RuleEditorBuffer
    {
        public string GoldText = "15";
        public string IncomeText = "5";
        public string ExperienceText = "3";
        public string PrestigeText = "20";
        public string HealthText = "300";

        public string ItemSearch = string.Empty;
        public bool ItemHeroFilterOnly = true;
        public int ItemTierIdx;
        public int ItemEnchantIdx;

        public string SkillSearch = string.Empty;
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

    internal static string GetModeDisplayZh(string modeId, string presetId)
    {
        if (string.Equals(modeId, "EndlessTower", StringComparison.OrdinalIgnoreCase)) return "无尽爬塔";
        if (string.Equals(modeId, "CrossHeroMayhem", StringComparison.OrdinalIgnoreCase)) return "跨职业混池";
        if (string.Equals(modeId, "DailyChallenge", StringComparison.OrdinalIgnoreCase)) return "每日同种子挑战";
        if (string.Equals(modeId, "MutatorChallenge", StringComparison.OrdinalIgnoreCase)) return "肉鸽突变词条";
        if (string.Equals(modeId, "BossRush", StringComparison.OrdinalIgnoreCase)) return "Boss车轮战";
        if (string.Equals(modeId, "SandboxLab", StringComparison.OrdinalIgnoreCase)) return "沙盒实验室";
        if (string.Equals(presetId, "EnhancedFun", StringComparison.OrdinalIgnoreCase)) return "爽玩增强";
        if (string.Equals(presetId, "ExtremeRoguelike", StringComparison.OrdinalIgnoreCase)) return "极限肉鸽";
        return "经典官方";
    }

    internal static string TranslateTierZh(string tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return "青铜";
        if (string.Equals(tier, "Bronze", StringComparison.OrdinalIgnoreCase)) return "青铜";
        if (string.Equals(tier, "Silver", StringComparison.OrdinalIgnoreCase)) return "白银";
        if (string.Equals(tier, "Gold", StringComparison.OrdinalIgnoreCase)) return "黄金";
        if (string.Equals(tier, "Diamond", StringComparison.OrdinalIgnoreCase)) return "钻石";
        if (string.Equals(tier, "Legendary", StringComparison.OrdinalIgnoreCase)) return "传奇";
        return tier;
    }

    internal static RuleActivationSummary SummarizeRules(CustomRunRuleSet rules, bool isTournament)
    {
        if (rules == null)
        {
            return new RuleActivationSummary(false, false, 0, "经典官方");
        }
        rules.EnsureCollections();

        var tags = new List<string>(8);

        string modeLabel = GetModeDisplayZh(rules.SpecialModeId, rules.PresetId);
        if (!string.Equals(modeLabel, "经典官方", StringComparison.Ordinal))
        {
            tags.Add(modeLabel);
        }

        if (rules.MutatorIds.Count > 0)
        {
            tags.Add($"词条×{rules.MutatorIds.Count}");
        }

        int archCount = CountBits(rules.SelectedArchetypeMask);
        if (archCount > 0)
        {
            tags.Add($"流派倾向×{archCount}");
        }

        if (rules.StartingGold != 15 || rules.StartingIncome != 5 || rules.StartingExperience != 3
            || rules.StartingPrestige != 20 || rules.StartingHealth != 300)
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
        string summary = customActive ? string.Join(" / ", tags) : "经典官方";
        return new RuleActivationSummary(customActive, powerMod, tags.Count, summary);
    }

    private static readonly string[] Tiers = { "Bronze", "Silver", "Gold", "Diamond" };
    private static readonly string[] TierLabelsZh = { "青铜", "白银", "黄金", "钻石" };

    private static readonly (string Key, string LabelZh)[] Enchantments =
    {
        ("", "无附魔"),
        ("Fiery", "炽焰"),
        ("Toxic", "剧毒"),
        ("Shielded", "护盾"),
        ("Heavy", "沉重"),
        ("Icy", "冰霜"),
        ("Turbo", "极速"),
        ("Restorative", "治愈"),
        ("Deadly", "致命"),
        ("Golden", "黄金"),
        ("Radiant", "辉光"),
        ("Obsidian", "黑曜"),
        ("Shiny", "闪耀"),
        ("Mossy", "青苔"),
        ("Mystical", "秘法"),
        ("Ethereal", "虚灵"),
        ("Tiny", "微型"),
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
        ["Vanessa"] = "瓦内莎",
        ["Pygmalien"] = "皮格马利翁",
        ["Dooley"] = "杜利",
        ["Mak"] = "马克",
        ["Stelle"] = "斯黛拉",
        ["Jules"] = "朱尔斯",
        ["Karnok"] = "卡诺克",
        ["Hero8"] = "双龙",
        ["Common"] = "通用",
    };

    private static readonly List<EncounterCategoryEntry> FallbackEncounterCategories = new()
    {
        new() { Key = "PedestalEnchant", GroupZh = "祭坛强化", NameZh = "指定附魔祭坛", DefaultMult = 1.0 },
        new() { Key = "PedestalEnchantRandom", GroupZh = "祭坛强化", NameZh = "随机附魔祭坛", DefaultMult = 1.0 },
        new() { Key = "PedestalUpgrade", GroupZh = "祭坛强化", NameZh = "升阶改造祭坛", DefaultMult = 1.0 },
        new() { Key = "EventCultAndMystery", GroupZh = "奇遇事件", NameZh = "邪教与神秘事件", DefaultMult = 1.0 },
        new() { Key = "EventExpeditionAgent", GroupZh = "奇遇事件", NameZh = "探险与特工事件", DefaultMult = 1.0 },
        new() { Key = "EventEconomyStats", GroupZh = "奇遇事件", NameZh = "经济与属性事件", DefaultMult = 1.0 },
        new() { Key = "EventFreeDealTransform", GroupZh = "奇遇事件", NameZh = "免费交易与变形事件", DefaultMult = 1.0 },
        new() { Key = "MerchantItems", GroupZh = "商店商人", NameZh = "常规物品商人", DefaultMult = 1.0 },
        new() { Key = "MerchantSkillsRare", GroupZh = "商店商人", NameZh = "技能与稀有商人", DefaultMult = 1.0 },
        new() { Key = "Monsters", GroupZh = "野怪战斗", NameZh = "PvE 野怪遭遇", DefaultMult = 1.0 },
    };
}
