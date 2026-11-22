using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V12 Shard A（Vanessa / Pygmalien / Dooley / Mak）全量 Always 物品与技能语义增量审计测试套件：
/// 1. 枚举 GameData.db 中全部归属 Shard A 的 Always 物品与技能；
/// 2. 为每张卡的每一条有效 Ability、Aura、Quest 构建满足前置条件与目标约束的富合成棋盘；
/// 3. 精准派发匹配的局内/局外事件并刷新光环，校验可观测的真实状态增量（消除 Silent No-Op）；
/// 4. 同步校验 CombatSimulator 端到端关键机制（如复活、清冻/减速、沙暴、重装等），并将审计结果落盘至 logs/v12_sem_audit_a.json。
/// </summary>
public sealed class V12SemanticAuditHeroesATests
{
    private const string AuthoritativeDbPath = @"e:\thebazaar\dist\data\GameData.db";
    private const string AuditReportPath = @"e:\thebazaar\logs\v12_sem_audit_a.json";

    private static readonly HashSet<string> ShardAHeroes = new(StringComparer.OrdinalIgnoreCase)
    {
        HeroNames.Vanessa,
        HeroNames.Pygmalien,
        HeroNames.Dooley,
        HeroNames.Mak
    };

    private static readonly Lazy<GameDataRepository> Repo = new(() =>
    {
        if (File.Exists(AuthoritativeDbPath))
            return GameDatabaseLoader.Load(AuthoritativeDbPath);
        return GameDatabaseLoader.Load();
    });

    private static readonly string[] AllNonWeaponNonTechNonToyTags =
    [
        "Property", "Food", "Potion", "Tool", "Vehicle", "Aquatic", "Friend",
        "Core", "Ray", "Dinosaur", "Apparel", "Dragon", "Ingredient",
        "Relic", "Reagent", "Map", "Key", "Drone", "Sigil", "Trap", "Instrument", "Loot"
    ];

    private static readonly string[] AllCoreHiddenTags =
    [
        "Damage", "Shield", "Burn", "Poison", "Heal", "Regen", "Haste", "Slow",
        "Freeze", "Charge", "Crit", "Lifesteal", "Ammo", "HeatedReference", "ChilledReference",
        "CanCrit", "Tempo"
    ];

    private sealed class AuditRecord
    {
        public string CardId { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;
        public string CardType { get; set; } = string.Empty;
        public List<string> Heroes { get; set; } = [];
        public string Kind { get; set; } = string.Empty; // Ability / Aura / Quest
        public string EntryId { get; set; } = string.Empty;
        public string TierTested { get; set; } = string.Empty;
        public string TriggerType { get; set; } = string.Empty;
        public string ActionType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // Pass / IntentionalZero / KnownEngineBug / UnexplainedNoOp
        public string Detail { get; set; } = string.Empty;
    }

    private sealed class CombatCallbackProbe
    {
        public int DamageEvents { get; set; }
        public int ShieldEvents { get; set; }
        public int BurnEvents { get; set; }
        public int PoisonEvents { get; set; }
        public int HealEvents { get; set; }
        public int RegenEvents { get; set; }
        public int HasteEvents { get; set; }
        public int SlowEvents { get; set; }
        public int FreezeEvents { get; set; }
        public int ChargeEvents { get; set; }
        public int ForceUseEvents { get; set; }
        public int SandstormEvents { get; set; }
        public int SpawnEvents { get; set; }
        public int TransformEvents { get; set; }
        public int DestroyEvents { get; set; }
        public int UpgradeEvents { get; set; }
        public int EnchantEvents { get; set; }
        public int DealSelectionEvents { get; set; }
        public int RerollEvents { get; set; }
        public int ExitSelectionEvents { get; set; }
        public int CostSpentEvents { get; set; }
        public int CombatEvents { get; set; }

        public int Total => DamageEvents + ShieldEvents + BurnEvents + PoisonEvents + HealEvents
                          + RegenEvents + HasteEvents + SlowEvents + FreezeEvents + ChargeEvents
                          + ForceUseEvents + SandstormEvents + SpawnEvents + TransformEvents
                          + DestroyEvents + UpgradeEvents + EnchantEvents + DealSelectionEvents
                          + RerollEvents + ExitSelectionEvents + CostSpentEvents + CombatEvents;
    }

    // Wave 2 合流后已全部修齐 #253 移交的引擎缺陷，临时豁免表严格清零并强制校验 zero knownBugs / zero e2eFindings
    private static readonly Dictionary<string, string> KnownEngineIssues = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ShardA_AllAlwaysItemsAndSkills_AbilitiesAurasAndQuests_ShouldProduceSemanticDelta()
    {
        var baseRepo = Repo.Value;
        var shardACards = baseRepo.CardsById.Values
            .Where(c => c.Type is "Item" or "Skill"
                     && c.IsAlwaysSpawnable
                     && c.Heroes.Any(h => ShardAHeroes.Contains(h)))
            .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(shardACards.Count >= 750, $"Shard A Always 卡牌数量应 >= 750，实际={shardACards.Count}");

        var records = new List<AuditRecord>(2500);
        var unknownRuleHits = new ConcurrentBag<string>();

        using (RuleExecutionContext.BeginUnknownRuleTypeScope((cat, typeName) => unknownRuleHits.Add($"{cat}:{typeName}")))
        {
            foreach (var tpl in shardACards)
            {
                // 1. 收集并审计该卡跨所有 Tier 的全部有效 Ability
                var abilityBestTiers = CollectBestTiersForAbilities(tpl);
                foreach (var (abId, tier) in abilityBestTiers)
                {
                    if (!tpl.Abilities.TryGetValue(abId, out var ab)) continue;
                    var rec = AuditSingleAbility(baseRepo, tpl, tier, ab);
                    records.Add(rec);
                }

                // 2. 收集并审计该卡跨所有 Tier 的全部有效 Aura
                var auraBestTiers = CollectBestTiersForAuras(tpl);
                foreach (var (auId, tier) in auraBestTiers)
                {
                    if (!tpl.Auras.TryGetValue(auId, out var aura)) continue;
                    var rec = AuditSingleAura(baseRepo, tpl, tier, aura);
                    records.Add(rec);
                }

                // 3. 审计该卡的全部 Quest 条目（触发推进 + 达成奖励）
                if (tpl.Quests != null)
                {
                    foreach (var q in tpl.Quests)
                    {
                        foreach (var entry in q.Entries)
                        {
                            var rec = AuditSingleQuest(baseRepo, tpl, entry);
                            records.Add(rec);
                        }
                    }
                }
            }
        }

        // 4. 同步校验关键战斗端到端行为（CombatSimulator），捕获纯单元测试可能遗漏的队列/时序型 Silent No-Op
        var e2eFindings = RunTargetedEndToEndCombatChecks(baseRepo);

        var unexplained = records.Where(r => r.Status == "UnexplainedNoOp").ToList();
        var knownBugs = records.Where(r => r.Status == "KnownEngineBug").ToList();
        var intentionalZero = records.Where(r => r.Status == "IntentionalZero").ToList();
        var passed = records.Where(r => r.Status == "Pass").ToList();

        WriteAuditReport(shardACards.Count, records, e2eFindings, unknownRuleHits);

        Assert.Empty(unknownRuleHits);
        Assert.Empty(KnownEngineIssues);
        Assert.Empty(knownBugs);
        Assert.Empty(e2eFindings);
        if (unexplained.Count > 0)
        {
            var preview = string.Join(Environment.NewLine,
                unexplained.Take(40).Select(u =>
                    $"[{u.Kind}] {u.CardName} ({u.CardId}) Tier={u.TierTested} Id={u.EntryId} Trig={u.TriggerType} Act={u.ActionType} => {u.Detail}"));
            Assert.Fail($"发现 {unexplained.Count} 个未归类的 Silent No-Op（总审计条目 {records.Count}，Pass={passed.Count}，IntentionalZero={intentionalZero.Count}，KnownBug={knownBugs.Count}）：\n{preview}");
        }
    }

    private static List<(string Id, CardTier Tier)> CollectBestTiersForAbilities(CardTemplate tpl)
    {
        var result = new Dictionary<string, CardTier>(StringComparer.Ordinal);
        if (tpl.Tiers == null || tpl.Tiers.Count == 0)
        {
            foreach (var k in tpl.Abilities.Keys)
                result[k] = tpl.DefaultTier;
            return result.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        foreach (var t in Enum.GetValues<CardTier>())
        {
            if (t < tpl.DefaultTier) continue;
            if (!tpl.Tiers.TryGetValue(t.ToString(), out var td)) continue;
            foreach (var aid in td.AbilityIds)
                result[aid] = t;
        }
        return result.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static List<(string Id, CardTier Tier)> CollectBestTiersForAuras(CardTemplate tpl)
    {
        var result = new Dictionary<string, CardTier>(StringComparer.Ordinal);
        if (tpl.Tiers == null || tpl.Tiers.Count == 0)
        {
            foreach (var k in tpl.Auras.Keys)
                result[k] = tpl.DefaultTier;
            return result.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        foreach (var t in Enum.GetValues<CardTier>())
        {
            if (t < tpl.DefaultTier) continue;
            if (!tpl.Tiers.TryGetValue(t.ToString(), out var td)) continue;
            foreach (var auid in td.AuraIds)
                result[auid] = t;
        }
        return result.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // =========================================================================
    // 合成棋盘与环境构建器
    // =========================================================================

    private sealed class SyntheticHarness
    {
        public required GameDataRepository Repo { get; init; }
        public required PlayerState Player { get; init; }
        public required PlayerState Opponent { get; init; }
        public required CardInstance SubjectCard { get; init; }
        public CardInstance? LeftNeighbor { get; init; }
        public CardInstance? RightNeighbor { get; init; }
        public CardInstance? StashPartner { get; init; }
        public CardInstance? OppCard { get; init; }
    }

    private static SyntheticHarness BuildHarnessForRule(
        GameDataRepository baseRepo,
        CardTemplate tpl,
        CardTier tier,
        RuleNode? triggerNode,
        List<RuleNode>? prereqs,
        RuleNode? actionNode,
        bool addSubjectToPlayer = true,
        bool allowDestroyedPartner = false)
    {
        var hero = tpl.Heroes.FirstOrDefault(h => ShardAHeroes.Contains(h)) ?? HeroNames.Vanessa;

        // 1. 解析卡牌自身所需状态（如 Shiny 附魔、非最高阶、Flying、Tiny 等）
        string? requiredEnchantment = FindRequiredEnchantment(prereqs, triggerNode, actionNode);
        bool forceNonDiamond = NeedsNonDiamondSelf(prereqs, actionNode);
        var effectiveTier = forceNonDiamond && tier == CardTier.Diamond
            ? (tpl.DefaultTier < CardTier.Diamond ? tpl.DefaultTier : CardTier.Gold)
            : tier;

        var subjectCard = new CardInstance(tpl, effectiveTier, requiredEnchantment);

        // 若自身需要特定动态状态（如 Flying=1 以触发 FlyingStop，或 Tiny=1）
        if (NeedsSelfFlying(prereqs, triggerNode, actionNode))
            subjectCard.IsFlying = true;
        if (NeedsSelfTiny(prereqs))
            subjectCard.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);
        if (NeedsSelfDisabledOrDestroyed(prereqs, actionNode))
        {
            // 若为修复动作或 Disabled/Destroyed 条件则按需准备
        }

        // 2. 分析所需特定模板 ID（TCardConditionalId）、尺寸、标签与状态条件
        var requiredTemplateIds = new List<string>();
        CollectRequiredIds(triggerNode, requiredTemplateIds);
        if (prereqs != null)
            foreach (var p in prereqs) CollectRequiredIds(p, requiredTemplateIds);
        CollectRequiredIds(actionNode, requiredTemplateIds);

        var neighborSize = DetermineNeighborSize(triggerNode, prereqs, actionNode);
        var rightNeighborSize = DetermineRightNeighborSize(triggerNode, prereqs, actionNode);
        bool neighborNeedsEmptyAmmo = NeedsEmptyAmmoCondition(triggerNode, prereqs, actionNode);
        bool needsDestroyedPartner = allowDestroyedPartner || NeedsDestroyedCondition(triggerNode, prereqs, actionNode);
        int minDayRequired = FindRequiredMinDay(prereqs);
        string? uniqueTagOnly = FindUniqueSelfHandTagRequirement(prereqs);
        string? notEqualOneTag = FindNotEqualOneTagRequirement(prereqs);
        string? zeroTagInHand = FindZeroTagInHandOrNeighborRequirement(prereqs);
        int minWeaponsInHand = FindMinTagCountInHandRequirement(prereqs, "Weapon");
        if (triggerNode?.Type == "TTriggerOnItemUsed" && actionNode?.Type == "TActionCardModifyAttribute")
            minWeaponsInHand = Math.Max(minWeaponsInHand, 2);
        int? maxHandCount = FindMaxSelfHandCountRequirement(prereqs);
        int minHandCount = FindMinSelfHandCountRequirement(prereqs);
        bool needsOppLowHp20 = NeedsOpponentLowHpThreshold(prereqs);
        bool needsMediumCore = NeedsMediumCorePartner(prereqs, triggerNode, actionNode);
        bool needsTwoEnchantedOrFlying = NeedsTwoEnchantedOrFlyingInHand(triggerNode, actionNode);
        var requiredOppPoison = FindRequiredOpponentAttrMin(prereqs, "Poison");
        bool needsSelfHpGreaterOpp = NeedsSelfHpGreaterThanOpp(prereqs);
        bool subjectOccupiesSelfHand = addSubjectToPlayer && string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase);

        var stdTiers = BuildStandardTiers();

        // 构造正交化伙伴模板：左邻与右邻默认同时具备 Weapon（除非要求 0 武器或唯一武器）与 AllCoreHiddenTags（含 Heal/CanCrit）；
        // 左邻保持 Lifesteal=0 兼容 Runic Potion Ab#1，右邻补充 Lifesteal=1 覆盖 Crimson Dash/Bloodthirst/Hungry Blades/Runic Potion Ab#0
        bool allowWeaponOnPartners = !string.Equals(zeroTagInHand, "Weapon", StringComparison.OrdinalIgnoreCase);
        bool leftKeepUnique = uniqueTagOnly == null
            ? allowWeaponOnPartners
            : !(subjectOccupiesSelfHand && subjectCard.HasTag(uniqueTagOnly));
        var leftExclude = new List<string> { "Toy", "Tech", "Core", "Loot" };
        if (!allowWeaponOnPartners) leftExclude.Add("Weapon");
        if (!string.IsNullOrEmpty(zeroTagInHand) && !leftExclude.Contains(zeroTagInHand, StringComparer.OrdinalIgnoreCase))
            leftExclude.Add(zeroTagInHand);
        var leftTags = BuildPartnerTags(leftExclude, uniqueTagOnly, keepIfUnique: leftKeepUnique);
        bool forbidHealOnLeft = HasNoneHiddenTagCondition(actionNode, "Heal") || (prereqs != null && prereqs.Any(p => HasNoneHiddenTagCondition(p, "Heal")));
        var leftHidden = forbidHealOnLeft
            ? AllCoreHiddenTags.Where(ht => !string.Equals(ht, "Heal", StringComparison.OrdinalIgnoreCase)).ToList()
            : AllCoreHiddenTags.ToList();
        var leftPartnerTpl = MakeSyntheticCard(
            "v12_syn_left",
            size: neighborSize,
            heroes: [hero],
            tags: leftTags,
            hiddenTags: leftHidden,
            tiers: stdTiers,
            baseAttrs: new()
            {
                ["CooldownMax"] = 6000, // > 5000ms 以满足 Grindstone 等高冷却左邻条件
                ["DamageAmount"] = 25,
                ["ShieldApplyAmount"] = 25,
                ["BurnApplyAmount"] = 6,
                ["PoisonApplyAmount"] = 6,
                ["HealAmount"] = 25,
                ["RegenApplyAmount"] = 5,
                ["CritChance"] = 20,
                ["AmmoMax"] = 5,
                ["SellPrice"] = 6,
                ["BuyPrice"] = 12
            });

        bool rightKeepWeapon = allowWeaponOnPartners
            && !string.Equals(uniqueTagOnly, "Weapon", StringComparison.OrdinalIgnoreCase);
        bool forceWeaponOnRight = string.Equals(notEqualOneTag, "Weapon", StringComparison.OrdinalIgnoreCase)
            || minWeaponsInHand >= 2
            || actionNode?.Target?.TargetMode is "RightCard" or "AllRightCards";
        var rightExclude = new List<string> { "Vehicle", "Loot" };
        if (!(rightKeepWeapon || (forceWeaponOnRight && allowWeaponOnPartners)))
            rightExclude.Add("Weapon");
        if (!string.IsNullOrEmpty(zeroTagInHand) && !rightExclude.Contains(zeroTagInHand, StringComparer.OrdinalIgnoreCase))
            rightExclude.Add(zeroTagInHand);
        var rightTags = BuildPartnerTags(rightExclude, uniqueTagOnly, keepIfUnique: false);
        var rightPartnerTpl = MakeSyntheticCard(
            "v12_syn_right",
            size: rightNeighborSize,
            heroes: [HeroNames.Jules],
            tags: rightTags,
            hiddenTags: AllCoreHiddenTags,
            tiers: stdTiers,
            baseAttrs: new()
            {
                ["CooldownMax"] = 5000,
                ["DamageAmount"] = 18,
                ["ShieldApplyAmount"] = 18,
                ["BurnApplyAmount"] = 4,
                ["PoisonApplyAmount"] = 4,
                ["HealAmount"] = 18,
                ["RegenApplyAmount"] = 4,
                ["CritChance"] = 15,
                ["Lifesteal"] = 1,
                ["AmmoMax"] = 4,
                ["SellPrice"] = 5,
                ["BuyPrice"] = 10
            });

        // 专门的附魔Core + 飞行 + SellPrice>10 + 非Weapon/非Friend伙伴
        var enchExclude = new List<string> { "Weapon", "Friend", "Vehicle", "Loot" };
        if (!string.IsNullOrEmpty(zeroTagInHand)) enchExclude.Add(zeroTagInHand);
        var enchantedCoreTags = BuildPartnerTags(enchExclude, uniqueTagOnly, keepIfUnique: false);
        var enchantedCoreTpl = MakeSyntheticCard(
            "v12_syn_ench_core",
            size: needsMediumCore ? "Medium" : "Small",
            tier: "Diamond",
            heroes: [HeroNames.Dooley == hero ? HeroNames.Vanessa : HeroNames.Dooley],
            tags: enchantedCoreTags,
            hiddenTags: AllCoreHiddenTags.Append("Quest"),
            tiers: stdTiers,
            baseAttrs: new()
            {
                ["CooldownMax"] = 3000,
                ["DamageAmount"] = 15,
                ["ShieldApplyAmount"] = 15,
                ["BurnApplyAmount"] = 4,
                ["PoisonApplyAmount"] = 4,
                ["HealAmount"] = 15,
                ["RegenApplyAmount"] = 4,
                ["CritChance"] = 10,
                ["AmmoMax"] = 3,
                ["SellPrice"] = 12
            });

        // 干净的非Weapon/非Friend/非Tech/非Toy/非Shield纯功能物品（覆盖 Healthy Hoarder / Healthy Lifestyle / Robe 等）
        var cleanExclude = new List<string> { "Weapon", "Friend", "Tech", "Toy", "Loot", "Core", "Potion" };
        if (!string.IsNullOrEmpty(zeroTagInHand)) cleanExclude.Add(zeroTagInHand);
        var cleanNonWeaponTags = BuildPartnerTags(cleanExclude, uniqueTagOnly, keepIfUnique: false);
        var cleanNonWeaponTpl = MakeSyntheticCard(
            "v12_syn_clean_non_wpn",
            size: "Small",
            tier: "Gold",
            heroes: [hero],
            tags: cleanNonWeaponTags,
            hiddenTags: ["Heal", "Regen", "Damage", "Quest", "CanCrit"],
            tiers: stdTiers,
            baseAttrs: new()
            {
                ["CooldownMax"] = 4000,
                ["HealAmount"] = 20,
                ["DamageAmount"] = 15,
                ["AmmoMax"] = 3,
                ["SellPrice"] = 6
            });

        // 合成技能卡（覆盖 Staff of the Wise 等 SelfBoard Skill 统计）
        var helperSkillTpl = MakeSyntheticCard(
            "v12_syn_skill",
            cardType: "Skill",
            tier: "Silver",
            heroes: [hero]);

        var oppSmallTpl = MakeSyntheticCard(
            "v12_syn_opp_small",
            size: "Small",
            tags: ["Weapon", "Tool", "Friend", "Potion"], // 不含 Core/Tech
            hiddenTags: AllCoreHiddenTags,
            tiers: stdTiers,
            baseAttrs: new()
            {
                ["CooldownMax"] = 4000,
                ["DamageAmount"] = 20,
                ["ShieldApplyAmount"] = 20,
                ["BurnApplyAmount"] = 5,
                ["PoisonApplyAmount"] = 5,
                ["AmmoMax"] = 4,
                ["SellPrice"] = 4
            });

        var oppMediumTpl = MakeSyntheticCard(
            "v12_syn_opp_medium",
            size: "Medium",
            tags: ["Weapon", "Tool", "Relic"],
            hiddenTags: AllCoreHiddenTags,
            tiers: stdTiers,
            baseAttrs: new()
            {
                ["CooldownMax"] = 5000,
                ["DamageAmount"] = 30,
                ["ShieldApplyAmount"] = 30,
                ["AmmoMax"] = 4,
                ["SellPrice"] = 6
            });

        var oppPassiveTpl = MakeSyntheticCard(
            "v12_syn_opp_passive",
            size: "Small",
            tags: ["Property", "Relic"], // CooldownMax = 0 且不含 Core/Tech
            hiddenTags: ["EconomyReference"],
            tiers: stdTiers,
            baseAttrs: new()
            {
                ["SellPrice"] = 5
            });

        var oppLargeTpl = MakeSyntheticCard(
            "v12_syn_opp_large",
            size: "Large",
            tier: "Gold",
            tags: ["Property", "Vehicle", "Core", "Tech"],
            hiddenTags: AllCoreHiddenTags,
            tiers: stdTiers,
            baseAttrs: new()
            {
                ["CooldownMax"] = 6000,
                ["DamageAmount"] = 50,
                ["AmmoMax"] = 4,
                ["SellPrice"] = 10
            });

        var extraCards = new List<CardTemplate>
        {
            leftPartnerTpl, rightPartnerTpl, enchantedCoreTpl, cleanNonWeaponTpl, helperSkillTpl,
            oppSmallTpl, oppMediumTpl, oppPassiveTpl, oppLargeTpl
        };

        var synthRepo = CloneRepoWithExtras(baseRepo, extraCards);

        var player = new PlayerState("v12_p1", "AuditP1", hero, initialHealthMax: 1000, initialPrestige: 20, initialGold: 100, unlockedHandSlots: 10)
        {
            Day = Math.Max(5, minDayRequired),
            Hour = 3,
            Level = 5,
            CurrentHealth = needsSelfHpGreaterOpp ? 800 : (needsOppLowHp20 ? 100 : 400), // < 50% 满足半血；若需要压对血线或 <20% 处决则自动协同设置
            Shield = 50,
            Burn = 20,
            Poison = 20,
            Rage = 20,
            Tempo = 10
        };
        player.Attributes["Income"] = 10;
        player.Attributes["HealthRegen"] = 10;
        player.Attributes["Custom_3"] = 5; // Gold Gained 累计计数
        player.Attributes["Custom_4"] = 5; // Weapons Bought 累计计数
        player.Attributes["Custom_9"] = 4; // Reagents Transformed 累计计数
        player.InitializeExclusionSet(synthRepo.GameMode);

        var opponent = new PlayerState("v12_p2", "AuditP2", HeroNames.Pygmalien, initialHealthMax: 1000, initialPrestige: 20, initialGold: 100, unlockedHandSlots: 10)
        {
            Day = Math.Max(5, minDayRequired),
            Hour = 3,
            Level = 5,
            CurrentHealth = needsOppLowHp20 ? 150 : 450, // < 50% 同时保证 400 < 450，满足 Self.Health < Opponent.Health；若需 20% 处决则设 150
            Shield = 50,
            Burn = 20,
            Poison = Math.Max(20, requiredOppPoison),
            Rage = 20,
            Tempo = 10
        };
        opponent.Attributes["Income"] = 10;
        opponent.Attributes["HealthRegen"] = 10;

        // 3. 布局玩家手牌（尽量把目标卡置于中段槽位 3，使左右邻居同时成立）
        CardInstance? leftNeighbor = null;
        CardInstance? rightNeighbor = null;

        if (addSubjectToPlayer)
        {
            if (tpl.Type == "Item")
            {
                int targetSlot = tpl.CardSize switch
                {
                    CardSize.Large => 3,
                    CardSize.Medium => 3,
                    _ => 3
                };
                // 先在目标卡左侧放置邻接卡（若是 Mirror 等要求左邻为 Medium 则按需占据 1..2）
                int leftWidth = TierHelper.ParseSize(neighborSize).SlotCount();
                int leftSlot = targetSlot - leftWidth;
                leftNeighbor = new CardInstance(leftPartnerTpl, CardTier.Bronze, null)
                {
                    CurrentAmmo = neighborNeedsEmptyAmmo ? 0 : 2,
                    FreezeMs = 500,
                    SlowMs = 500,
                    HasteMs = 500
                };
                if (NeedsTinyNeighbor(prereqs, actionNode))
                    leftNeighbor.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);
                player.Hand.TryPlaceOrShift(leftNeighbor, Math.Max(0, leftSlot));

                player.Hand.TryPlaceWithDisplaceOrSwap(subjectCard, targetSlot);

                int rightSlot = subjectCard.StartSlot >= 0 ? subjectCard.StartSlot + subjectCard.SlotWidth : targetSlot + subjectCard.SlotWidth;
                rightNeighbor = new CardInstance(rightPartnerTpl, CardTier.Bronze, needsTwoEnchantedOrFlying ? "Turbo" : null)
                {
                    CurrentAmmo = 2,
                    IsFlying = needsTwoEnchantedOrFlying,
                    FreezeMs = 500,
                    SlowMs = 500,
                    HasteMs = 500
                };
                if (rightSlot < 10)
                    player.Hand.TryPlaceWithDisplaceOrSwap(rightNeighbor, rightSlot);
                else
                    player.Hand.TryAddAuto(rightNeighbor);
            }
            else
            {
                player.Skills.Add(subjectCard);
                leftNeighbor = new CardInstance(leftPartnerTpl, CardTier.Bronze, null)
                {
                    CurrentAmmo = neighborNeedsEmptyAmmo ? 0 : 2,
                    FreezeMs = 500,
                    SlowMs = 500,
                    HasteMs = 500
                };
                rightNeighbor = new CardInstance(rightPartnerTpl, CardTier.Bronze, needsTwoEnchantedOrFlying ? "Turbo" : null)
                {
                    CurrentAmmo = 2,
                    IsFlying = needsTwoEnchantedOrFlying,
                    FreezeMs = 500,
                    SlowMs = 500,
                    HasteMs = 500
                };
                player.Hand.TryPlaceOrShift(leftNeighbor, 0);
                // Skill 卡无需预留中间主卡槽，令左右伙伴紧邻以支持 Neighbor(Weapon) 条件（如 The Best Defense）
                int adjSlot = Math.Max(1, leftNeighbor.StartSlot + leftNeighbor.SlotWidth);
                player.Hand.TryPlaceOrShift(rightNeighbor, adjSlot);
            }
        }
        else
        {
            leftNeighbor = new CardInstance(leftPartnerTpl, CardTier.Bronze, null) { CurrentAmmo = 2 };
            rightNeighbor = new CardInstance(rightPartnerTpl, CardTier.Bronze, null) { CurrentAmmo = 2 };
            player.Hand.TryPlaceOrShift(leftNeighbor, 0);
            player.Hand.TryPlaceOrShift(rightNeighbor, Math.Max(1, leftNeighbor.SlotWidth));
        }

        // 若条件要求特定真实模板 ID（例如 The Core / Piggles / Lead / Cult Member），去重后注入到棋盘与背包中
        foreach (var reqId in requiredTemplateIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (reqId.Equals(tpl.Id, StringComparison.OrdinalIgnoreCase)) continue;
            var reqTpl = synthRepo.GetCard(reqId);
            if (reqTpl == null) continue;
            var reqCard = new CardInstance(reqTpl, reqTpl.DefaultTier, null);
            if (reqCard.EffectiveAmmoMax > 0) reqCard.CurrentAmmo = Math.Max(0, reqCard.EffectiveAmmoMax - 1);
            if (reqTpl.Type == "Skill")
                player.Skills.Add(reqCard);
            else if (player.Hand.Cards.Count < (maxHandCount ?? 10) && player.Hand.TryAddAuto(reqCard))
                { }
            else
                player.Stash.TryAddAuto(reqCard);
        }

        // 补充不同尺寸/品质伙伴到空余手牌格与背包；若前置限制 <=5 张手牌则上限控制在 maxHandCount 内
        int effectiveMaxHand = maxHandCount ?? 10;
        int effectiveMinHand = Math.Max(minHandCount, 5);
        var helperEnchCore = new CardInstance(enchantedCoreTpl, CardTier.Diamond, "Fiery")
        {
            CurrentAmmo = neighborNeedsEmptyAmmo ? 0 : 1,
            IsFlying = true,
            FreezeMs = 500,
            SlowMs = 500,
            HasteMs = 500
        };
        if (player.Hand.Cards.Count < effectiveMaxHand && player.Hand.TryAddAuto(helperEnchCore))
            { }
        else
            player.Stash.TryAddAuto(helperEnchCore);

        var helperClean = new CardInstance(cleanNonWeaponTpl, CardTier.Gold, null) { CurrentAmmo = 2 };
        if (player.Hand.Cards.Count < effectiveMaxHand && player.Hand.TryAddAuto(helperClean))
            { }
        else
            player.Stash.TryAddAuto(helperClean);

        // 按需补齐手牌卡数量（>=7 时循环补到 7 张，例如 Tiny Dancer / Noisy Cricket；<=5 时严格不再超限）
        int safety = 0;
        while (player.Hand.Cards.Count < Math.Min(effectiveMaxHand, effectiveMinHand) && safety++ < 6)
        {
            var fillerTier = (safety % 2 == 1) ? CardTier.Silver : CardTier.Bronze;
            if (!player.Hand.TryAddAuto(new CardInstance(cleanNonWeaponTpl, fillerTier, null) { CurrentAmmo = 1 }))
                break;
        }

        player.Skills.Add(new CardInstance(helperSkillTpl, CardTier.Silver, null));

        if (needsDestroyedPartner)
        {
            var destroyedCard = new CardInstance(cleanNonWeaponTpl, CardTier.Bronze, null) { IsDestroyed = true };
            if (player.Hand.Cards.Count < effectiveMaxHand && player.Hand.TryAddAuto(destroyedCard))
                { }
            else
                player.Stash.TryAddAuto(destroyedCard);
        }

        var stashPartner = new CardInstance(cleanNonWeaponTpl, CardTier.Bronze, null) { CurrentAmmo = 1 };
        player.Stash.TryAddAuto(stashPartner);
        var stashGoldSmall = new CardInstance(rightPartnerTpl, CardTier.Gold, null) { CurrentAmmo = 1 };
        player.Stash.TryAddAuto(stashGoldSmall);
        var stashSilverSmall = new CardInstance(rightPartnerTpl, CardTier.Silver, null) { CurrentAmmo = 1 };
        player.Stash.TryAddAuto(stashSilverSmall);

        // 4. 构造对手棋盘（覆盖 Small / Medium / Large 主动卡、CooldownMax=0 被动卡、Frozen/Slowed 卡牌）
        var oppSmall = new CardInstance(oppSmallTpl, CardTier.Bronze, null) { CurrentAmmo = 2, FreezeMs = 1000, SlowMs = 1000 };
        var oppMedium = new CardInstance(oppMediumTpl, CardTier.Silver, null) { CurrentAmmo = 2 };
        var oppLarge = new CardInstance(oppLargeTpl, CardTier.Gold, null) { CurrentAmmo = 2 };
        var oppPassive = new CardInstance(oppPassiveTpl, CardTier.Bronze, null);
        opponent.Hand.TryPlaceOrShift(oppSmall, 0);
        opponent.Hand.TryPlaceOrShift(oppMedium, 1);
        opponent.Hand.TryPlaceOrShift(oppLarge, 3);
        opponent.Hand.TryPlaceOrShift(oppPassive, 6);
        opponent.Stash.TryAddAuto(new CardInstance(oppSmallTpl, CardTier.Bronze, null));

        return new SyntheticHarness
        {
            Repo = synthRepo,
            Player = player,
            Opponent = opponent,
            SubjectCard = subjectCard,
            LeftNeighbor = leftNeighbor,
            RightNeighbor = rightNeighbor,
            StashPartner = stashPartner,
            OppCard = oppSmall
        };
    }

    // =========================================================================
    // 单条 Ability 语义增量审计
    // =========================================================================

    private static AuditRecord AuditSingleAbility(
        GameDataRepository baseRepo,
        CardTemplate tpl,
        CardTier tier,
        AbilityDefinition ab)
    {
        var triggerTypes = CollectAllTriggerTypes(ab.Trigger);
        string primaryTrigger = triggerTypes.FirstOrDefault() ?? "(None)";
        string actionType = ab.Action?.Type ?? "(None)";

        var rec = new AuditRecord
        {
            CardId = tpl.Id,
            CardName = tpl.InternalName,
            CardType = tpl.Type,
            Heroes = tpl.Heroes.ToList(),
            Kind = "Ability",
            EntryId = ab.Id,
            TierTested = tier.ToString(),
            TriggerType = string.Join("|", triggerTypes),
            ActionType = actionType
        };

        if (IsIntentionalZeroValueAction(ab.Action))
        {
            rec.Status = "IntentionalZero";
            rec.Detail = "模板 JSON 自身 Value 固定为 0.0（官方占位/无消耗槽）";
            return rec;
        }

        // 若能力包含 TTriggerOnCardSelected 且前置要求玩家尚未拥有自身，则暂不预装入 Skills
        bool skipPreOwn = triggerTypes.Contains("TTriggerOnCardSelected") && HasZeroSelfSkillPrereq(ab.Prerequisites, tpl.Id);

        // 针对部分在常规状态已达极值导致“再执行无变化”的动作进行精准上下文微调
        var harness = BuildHarnessForRule(baseRepo, tpl, tier, ab.Trigger, ab.Prerequisites, ab.Action, addSubjectToPlayer: !skipPreOwn);
        AdjustSubjectForActionPreconditions(harness, ab);

        // 先刷新光环（使依赖自身光环赋值的动作属性，如 Healthy Heart / Luxury Tents / Calcinator 就绪）
        var preScope = string.Equals(ab.WorksIn, "OutOfCombatOnly", StringComparison.OrdinalIgnoreCase)
            ? RuleScope.OutOfCombat
            : RuleScope.InCombat;
        AuraEngine.RecalculateAuras(harness.Player, harness.Opponent, preScope, harness.Player.Day, harness.Player.Hour, harness.Repo);
        // 若光环修改了自身属性比较双方之一（如 Scales Aura#2/#3 依据左右卡数设置 Custom_0/Custom_1），重算后再次精准对齐比较条件
        AdjustSelfAttributesForPrerequisites(harness.SubjectCard, ab.Prerequisites);

        // 特例：若目标卡基础需要 Cost 付款（如 TActionCostPlayerAttribute）且消耗品属性不足，则确保充足
        EnsureCostSatisfied(harness, ab.Action);

        var snapBefore = CaptureBoardSnapshot(harness.Player, harness.Opponent, harness.SubjectCard);
        var probe = new CombatCallbackProbe();
        var detailedEvents = new List<string>();
        bool triggered = false;

        var oocLeaves = string.Equals(ab.WorksIn, "CombatOnly", StringComparison.OrdinalIgnoreCase)
            ? []
            : CollectTriggerLeafNodes(ab.Trigger).Where(t => IsOutOfCombatTrigger(t.Type)).ToList();

        if (oocLeaves.Count > 0)
        {
            // 局外单条 Ability 审计隔离：临时只保留被测 ab，防止同卡更靠前的其他同触发器能力抢先修改状态（如 Business Card/Assembly Line）
            var savedAbilities = harness.SubjectCard.ActiveAbilities.ToList();
            try
            {
                harness.SubjectCard.ActiveAbilities.Clear();
                harness.SubjectCard.ActiveAbilities.Add(ab);
                foreach (var oocLeaf in oocLeaves)
                {
                    triggered = DispatchViaOutOfCombatController(harness, ab, oocLeaf, out var oocBatch);
                    bool hasDelta = HasOutOfCombatDelta(oocBatch, snapBefore, harness.Player, harness.Opponent, harness.SubjectCard);
                    if (triggered && hasDelta)
                    {
                        rec.Status = "Pass";
                        rec.Detail = $"OOC触发成功: abilities={oocBatch?.TriggeredAbilityLogs.Count ?? 0}";
                        return rec;
                    }
                }
            }
            finally
            {
                harness.SubjectCard.ActiveAbilities.Clear();
                harness.SubjectCard.ActiveAbilities.AddRange(savedAbilities);
            }
        }
        else
        {
            // 在 Combat/RuleExecutionContext 中按真实触发规则匹配并执行
            triggered = DispatchViaCombatRuleContext(harness, ab, triggerTypes, probe, detailedEvents);
        }

        var snapAfter = CaptureBoardSnapshot(harness.Player, harness.Opponent, harness.SubjectCard);
        bool boardDelta = HasBoardDelta(snapBefore, snapAfter);

        if (triggered && (probe.Total > 0 || boardDelta))
        {
            rec.Status = "Pass";
            rec.Detail = $"触发并产生语义增量: callbacks={probe.Total}, boardDelta={boardDelta}";
            return rec;
        }

        string issueKey = $"{tpl.Id}:Ability:{ab.Id}";
        if (KnownEngineIssues.TryGetValue(issueKey, out var knownReason))
        {
            rec.Status = "KnownEngineBug";
            rec.Detail = knownReason;
            return rec;
        }

        rec.Status = "UnexplainedNoOp";
        rec.Detail = triggered
            ? $"触发器命中但无任何状态变化与事件回调 (Action={actionType}, callbacks={probe.Total}, boardDelta={boardDelta})"
            : $"触发器/前置条件未命中 (Trigger={rec.TriggerType}, ActiveIn={ab.ActiveIn}, WorksIn={ab.WorksIn})";
        return rec;
    }

    // =========================================================================
    // 单条 Aura 语义增量审计
    // =========================================================================

    private static AuditRecord AuditSingleAura(
        GameDataRepository baseRepo,
        CardTemplate tpl,
        CardTier tier,
        AuraDefinition aura)
    {
        var rec = new AuditRecord
        {
            CardId = tpl.Id,
            CardName = tpl.InternalName,
            CardType = tpl.Type,
            Heroes = tpl.Heroes.ToList(),
            Kind = "Aura",
            EntryId = aura.Id,
            TierTested = tier.ToString(),
            TriggerType = $"Aura({aura.ActiveIn}/{aura.WorksIn})",
            ActionType = aura.Action?.Type ?? "(None)"
        };

        if (aura.Action == null)
        {
            rec.Status = "IntentionalZero";
            rec.Detail = "模板 JSON 自身 Aura Action 为 null";
            return rec;
        }

        var harness = BuildHarnessForRule(baseRepo, tpl, tier, triggerNode: null, aura.Prerequisites, aura.Action);
        AdjustSubjectForAuraPreconditions(harness, aura);

        // 优先尊重 WorksIn 作用域（如 Subscraper / Skyscraper / Abacus / Balcony 标为 CombatOnly 的 SellPrice 战斗增殖光环）
        var scope = string.Equals(aura.WorksIn, "OutOfCombatOnly", StringComparison.OrdinalIgnoreCase)
            ? RuleScope.OutOfCombat
            : (string.Equals(aura.WorksIn, "CombatOnly", StringComparison.OrdinalIgnoreCase)
                ? RuleScope.InCombat
                : (IsEconomyAura(aura.Action) ? RuleScope.OutOfCombat : RuleScope.InCombat));

        // 保留其他依赖光环（如 Venomous Vitality / Moth to a Flame / Healthy Lifestyle 的 Au 1）作为基线，再额外开启当前被测 Aura
        var savedAuras = harness.SubjectCard.ActiveAuras.ToList();
        var otherAuras = savedAuras.Where(a => !string.Equals(a.Id, aura.Id, StringComparison.Ordinal)).ToList();

        harness.SubjectCard.ActiveAuras.Clear();
        harness.SubjectCard.ActiveAuras.AddRange(otherAuras);
        AuraEngine.RecalculateAuras(harness.Player, harness.Opponent, scope, harness.Player.Day, harness.Player.Hour, harness.Repo);
        var baseline = CaptureAuraSnapshot(harness.Player, harness.Opponent);

        harness.SubjectCard.ActiveAuras.Clear();
        harness.SubjectCard.ActiveAuras.AddRange(otherAuras);
        harness.SubjectCard.ActiveAuras.Add(aura);
        AuraEngine.RecalculateAuras(harness.Player, harness.Opponent, scope, harness.Player.Day, harness.Player.Hour, harness.Repo);
        var after = CaptureAuraSnapshot(harness.Player, harness.Opponent);

        // 恢复原集合
        harness.SubjectCard.ActiveAuras.Clear();
        harness.SubjectCard.ActiveAuras.AddRange(savedAuras);

        bool changed = HasAuraDelta(baseline, after);
        if (changed)
        {
            rec.Status = "Pass";
            rec.Detail = "光环重算产生非零属性或标签增量";
            return rec;
        }

        string issueKey = $"{tpl.Id}:Aura:{aura.Id}";
        if (KnownEngineIssues.TryGetValue(issueKey, out var knownReason))
        {
            rec.Status = "KnownEngineBug";
            rec.Detail = knownReason;
            return rec;
        }

        rec.Status = "UnexplainedNoOp";
        rec.Detail = $"光环重算后未产生任何 AuraAdd/AuraMult/AuraTags 增量 (Attr={aura.Action?.AttributeType}, Op={aura.Action?.Operation})";
        return rec;
    }

    // =========================================================================
    // 单条 Quest 语义增量审计
    // =========================================================================

    private static AuditRecord AuditSingleQuest(
        GameDataRepository baseRepo,
        CardTemplate tpl,
        QuestEntry entry)
    {
        var triggerTypes = CollectAllTriggerTypes(entry.Trigger);
        string primaryTrigger = triggerTypes.FirstOrDefault() ?? "(None)";

        var rec = new AuditRecord
        {
            CardId = tpl.Id,
            CardName = tpl.InternalName,
            CardType = tpl.Type,
            Heroes = tpl.Heroes.ToList(),
            Kind = "Quest",
            EntryId = entry.AttributeType,
            TierTested = tpl.DefaultTier.ToString(),
            TriggerType = string.Join("|", triggerTypes),
            ActionType = "QuestProgressAndReward"
        };

        var harness = BuildHarnessForRule(baseRepo, tpl, tpl.DefaultTier, entry.Trigger, entry.Prerequisites, actionNode: null);
        AdjustSelfAttributesForPrerequisites(harness.SubjectCard, entry.Prerequisites);
        // 将进度预置到 Target - 1，从而一次触发即可验证进度推进 + 完成解锁奖励！
        double targetVal = Math.Max(1, entry.Target);
        if (targetVal > 1)
            harness.SubjectCard.QuestProgress[entry.AttributeType] = targetVal - 1;

        var prevAbCount = harness.SubjectCard.ActiveAbilities.Count;
        var prevAuCount = harness.SubjectCard.ActiveAuras.Count;
        var prevAttrs = new Dictionary<string, double>(harness.SubjectCard.BaseAttributes, StringComparer.OrdinalIgnoreCase);

        int questUpdatedCalls = 0;
        int questCompletedCalls = 0;

        var ctx = new RuleExecutionContext(harness.Repo, RuleScope.OutOfCombat, harness.Player, harness.Opponent, new Random(13))
        {
            LocalPlayerId = harness.Player.PlayerId,
            CurrentDay = harness.Player.Day,
            CurrentHour = harness.Player.Hour,
            OnQuestUpdated = (_, _, _, _, _) => questUpdatedCalls++,
            OnQuestCompleted = _ => questCompletedCalls++
        };

        FireMatchingQuestTrigger(harness, entry.Trigger, primaryTrigger, ctx);

        double newProgress = harness.SubjectCard.QuestProgress.GetValueOrDefault(entry.AttributeType, 0);
        bool completed = harness.SubjectCard.CompletedQuests.Contains(entry.AttributeType);
        bool rewardApplied = harness.SubjectCard.ActiveAbilities.Count > prevAbCount
                          || harness.SubjectCard.ActiveAuras.Count > prevAuCount
                          || !DictionariesRoughlyEqual(prevAttrs, harness.SubjectCard.BaseAttributes)
                          || entry.Reward == null
                          || (entry.Reward.Abilities is not { Count: > 0 } && entry.Reward.Auras is not { Count: > 0 } && entry.Reward.Attributes is not { Count: > 0 } && entry.Reward.Tiers is not { Count: > 0 });

        if (newProgress >= targetVal && completed && questUpdatedCalls > 0 && questCompletedCalls > 0 && rewardApplied)
        {
            rec.Status = "Pass";
            rec.Detail = $"Quest推进并完成 ({targetVal - 1}->{newProgress}/{targetVal}), rewardApplied={rewardApplied}";
            return rec;
        }

        string issueKey = $"{tpl.Id}:Quest:{entry.AttributeType}";
        if (KnownEngineIssues.TryGetValue(issueKey, out var knownReason))
        {
            rec.Status = "KnownEngineBug";
            rec.Detail = knownReason;
            return rec;
        }

        rec.Status = "UnexplainedNoOp";
        rec.Detail = $"Quest未推进或未正确解锁奖励: progress={newProgress}/{targetVal}, completed={completed}, updated={questUpdatedCalls}, completedCalls={questCompletedCalls}, rewardApplied={rewardApplied}";
        return rec;
    }

    // =========================================================================
    // 端到端 CombatSimulator 回归核验（专门捕获取决于队列/ICD/时机而非单步调用的引擎 Silent No-Op）
    // =========================================================================

    private static List<string> RunTargetedEndToEndCombatChecks(GameDataRepository baseRepo)
    {
        var findings = new List<string>();

        // 1. Memento Mori（34618f7b-f262-4d49-b395-de92bef0508c）：玩家被击杀时应当触发 ReviveHeal 复活并至少撑过死亡回合
        if (baseRepo.GetCard("34618f7b-f262-4d49-b395-de92bef0508c") is { } mementoTpl)
        {
            var oppBurstTpl = MakeSyntheticCard("v12_e2e_burst", baseAttrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 500 },
                abilities: new()
                {
                    ["0"] = new()
                    {
                        Id = "0",
                        Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                        Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                    }
                });
            var p1KillTpl = MakeSyntheticCard("v12_e2e_p1_kill", baseAttrs: new() { ["CooldownMax"] = 1500, ["DamageAmount"] = 2000 },
                abilities: new()
                {
                    ["0"] = new()
                    {
                        Id = "0",
                        Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                        Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                    }
                });
            var synthRepo = CloneRepoWithExtras(baseRepo, [oppBurstTpl, p1KillTpl]);
            var p1 = new PlayerState("e2e_p1", "P1", HeroNames.Mak, 300, 20, 10, 10);
            var p2 = new PlayerState("e2e_p2", "P2", HeroNames.Vanessa, 1000, 20, 10, 10);
            p1.Hand.TryAddAuto(new CardInstance(mementoTpl, CardTier.Gold));
            p1.Hand.TryAddAuto(new CardInstance(p1KillTpl));
            p2.Hand.TryAddAuto(new CardInstance(oppBurstTpl));

            var res = CombatSimulator.Simulate(synthRepo, p1, p2, seed: 42, recordTimeline: false, recordEvents: true);
            // 1000ms 时 p2 打死 p1(300hp)，若有 Memento Mori 应复活且有 1000ms 100% 减伤，1500ms 时 p1 反杀 p2 胜出
            if (res.WinnerPlayerId != p1.PlayerId)
                findings.Add($"[E2E-ENGINE-BUG] Memento Mori (34618f7b-f262-4d49-b395-de92bef0508c): 死亡后未能触发 ReviveHeal 复活反杀，Winner={res.WinnerPlayerId}, DurationMs={res.DurationMs}");
        }

        // 2. Desperate Cleanse（69e15083-66e9-40ca-a3c9-f8bdd8b3887d）：玩家半血以下应当把己方手牌的 FreezeMs/SlowMs 清零
        if (baseRepo.GetCard("69e15083-66e9-40ca-a3c9-f8bdd8b3887d") is { } dcTpl)
        {
            var p1 = new PlayerState("dc_p1", "P1", HeroNames.Vanessa, 1000, 20, 10, 10)
            {
                CurrentHealth = 400,
                Burn = 20,
                Poison = 20
            };
            var frozenWeaponTpl = MakeSyntheticCard("v12_e2e_frozen_wpn", baseAttrs: new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 50 });
            var synthRepo = CloneRepoWithExtras(baseRepo, [frozenWeaponTpl]);
            var frozenWpn = new CardInstance(frozenWeaponTpl) { FreezeMs = 5000, SlowMs = 5000 };
            var dcSkill = new CardInstance(dcTpl, CardTier.Diamond);
            p1.Hand.TryAddAuto(frozenWpn);
            p1.Skills.Add(dcSkill);

            AuraEngine.RecalculateAuras(p1, null, RuleScope.InCombat, 5, 3, synthRepo);
            var ctx = new RuleExecutionContext(synthRepo, RuleScope.InCombat, p1, null, new Random(1))
            {
                SelfCard = dcSkill
            };
            foreach (var ab in dcSkill.ActiveAbilities)
            {
                if (ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, ctx))
                    AbilityExecutor.ExecuteAction(ab.Action, dcSkill, ctx);
            }
            if (frozenWpn.FreezeMs > 0 || frozenWpn.SlowMs > 0)
                findings.Add($"[E2E-ENGINE-BUG] Desperate Cleanse (69e15083-66e9-40ca-a3c9-f8bdd8b3887d): TActionCardModifyAttribute(Subtract Freeze/Slow) 未清零 FreezeMs={frozenWpn.FreezeMs} / SlowMs={frozenWpn.SlowMs}");
        }

        // 3. Venomous Vitality / Moth to a Flame / Healthy Lifestyle：天然顺序 Aura#0 在 Aura#1 之前，单遍光环重算会读到 RegenApplyAmount==0 导致 HealthRegen 增量丢失
        foreach (var (cid, cname) in new[]
        {
            ("6f593cc1-b44a-43d6-a83b-a68fb477aef2", "Venomous Vitality"),
            ("529f2133-0d39-4827-9788-2b90c5356b7e", "Moth to a Flame"),
            ("76ac0aa6-ef43-4a5b-9a30-f0ab4186dc83", "Healthy Lifestyle")
        })
        {
            if (baseRepo.GetCard(cid) is not { } auraOrderTpl) continue;
            var p1 = new PlayerState("e2e_ao1", "P1", HeroNames.Mak, 1000, 20, 10, 10);
            var p2 = new PlayerState("e2e_ao2", "P2", HeroNames.Vanessa, 1000, 20, 10, 10) { Burn = 20, Poison = 20 };
            var c = new CardInstance(auraOrderTpl, CardTier.Diamond);
            if (auraOrderTpl.Type == "Skill") p1.Skills.Add(c);
            else p1.Hand.TryAddAuto(c);
            var nonWpn = new CardInstance(MakeSyntheticCard("v12_e2e_nonwpn", baseAttrs: new() { ["CooldownMax"] = 4000 }));
            p1.Hand.TryAddAuto(nonWpn);
            AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 5, 3, baseRepo);
            if (p1.AuraAdd.GetValueOrDefault("HealthRegen", 0) <= 0)
                findings.Add($"[E2E-ENGINE-BUG] {cname} ({cid}): 单遍 AuraEngine 按 [\"0\", \"1\"] 顺序执行时 Aura#0 读到 RegenApplyAmount==0，导致 Player.AuraAdd[HealthRegen]={p1.AuraAdd.GetValueOrDefault("HealthRegen", 0)}（应支持多遍收敛/依赖重算）");
        }

        // 4. Shipment Ability#1：TActionGameSpawnCards.Attributes={\"Tiny\":1} 反序列化与衍生赋予检查
        if (baseRepo.GetCard("103cd65b-0b3d-418d-92d8-0dfa5f19d7c4") is { } shipTpl)
        {
            var p1 = new PlayerState("e2e_ship1", "P1", HeroNames.Pygmalien, 1000, 20, 10, 10);
            var ship = new CardInstance(shipTpl, CardTier.Diamond);
            ship.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);
            p1.Hand.TryAddAuto(ship);
            var batch = OutOfCombatController.OnCardSoldOrConsumed(p1, ship, baseRepo, new Random(7));
            if (batch.SpawnedCards.Count > 0 && batch.SpawnedCards.All(s => !s.Card.IsTiny))
                findings.Add("[E2E-ENGINE-BUG] Shipment (103cd65b-0b3d-418d-92d8-0dfa5f19d7c4) Ability#1: RuleNode 缺少 TActionGameSpawnCards.Attributes 反序列化字段，Tiny Shipment 售出衍生的中型物品均未获得 Tiny=1");
        }

        return findings;
    }

    // =========================================================================
    // 触发器派发辅助逻辑
    // =========================================================================

    private static bool DispatchViaOutOfCombatController(
        SyntheticHarness h,
        AbilityDefinition ab,
        RuleNode triggerLeaf,
        out OutOfCombatBatchResult? batch)
    {
        batch = null;
        var rng = new Random(7);
        var p = h.Player;
        var card = h.SubjectCard;
        var repo = h.Repo;

        switch (triggerLeaf.Type)
        {
            case "TTriggerOnCardSold":
            {
                var soldCard = PickMatchingSubjectCard(triggerLeaf.Subject, card, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, h.Opponent) ?? card;
                batch = OutOfCombatController.OnCardSoldOrConsumed(p, soldCard, repo, rng);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            }
            case "TTriggerOnCardPurchased":
            {
                var boughtCard = PickMatchingSubjectCard(triggerLeaf.Subject, card, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, h.Opponent) ?? card;
                batch = OutOfCombatController.OnItemPurchased(p, boughtCard, repo, rng);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            }
            case "TTriggerOnCardSelected":
            {
                batch = OutOfCombatController.OnCardSelected(p, card, repo, rng, [card]);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            }
            case "TTriggerOnCardUpgraded":
            {
                var upgCard = PickMatchingSubjectCard(triggerLeaf.Subject, card, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, h.Opponent) ?? card;
                batch = OutOfCombatController.OnCardUpgraded(p, upgCard, repo, rng);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            }
            case "TTriggerOnCardEnchanted":
            {
                var enchCard = PickMatchingSubjectCard(triggerLeaf.Subject, card, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, h.Opponent) ?? card;
                batch = OutOfCombatController.OnCardEnchanted(p, enchCard, repo, rng);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            }
            case "TTriggerOnDayStarted":
                batch = OutOfCombatController.OnDayStarted(p, repo, rng);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            case "TTriggerOnHourStarted":
                batch = OutOfCombatController.OnHourStarted(p, repo, rng);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            case "TTriggerOnEncounterSelected":
            {
                var encCard = CreateMatchingEncounterCard(triggerLeaf, repo);
                batch = OutOfCombatController.OnEncounterSelected(p, encCard, repo, rng, encCard.Template.Id, encCard);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            }
            case "TTriggerOnFightEnded":
            {
                string outcome = string.IsNullOrEmpty(triggerLeaf.CombatOutcome) ? "Win" : triggerLeaf.CombatOutcome!;
                string ctype = string.IsNullOrEmpty(triggerLeaf.CombatType) ? "PVP" : triggerLeaf.CombatType!;
                batch = OutOfCombatController.OnFightEnded(p, ctype, outcome, repo, rng, h.Opponent);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            }
            case "TTriggerOnPlayerLeveledUp":
                p.Level += 1;
                batch = OutOfCombatController.OnPlayerLeveledUp(p, repo, rng, p.Level);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            case "TTriggerOnRunStarted":
                batch = OutOfCombatController.OnRunStarted(p, repo, rng);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            case "TTriggerOnRunCompleted":
                batch = OutOfCombatController.OnRunCompleted(p, repo, "Victory", rng);
                return batch.TriggeredAbilityLogs.Any(l => l.StartsWith($"{card.Template.InternalName}:{ab.Id}:", StringComparison.Ordinal));
            default:
                return false;
        }
    }

    private static bool DispatchViaCombatRuleContext(
        SyntheticHarness h,
        AbilityDefinition ab,
        List<string> triggerTypes,
        CombatCallbackProbe probe,
        List<string> detailedEvents)
    {
        var p = h.Player;
        var opp = h.Opponent;
        var card = h.SubjectCard;
        var scope = string.Equals(ab.WorksIn, "OutOfCombatOnly", StringComparison.OrdinalIgnoreCase)
            ? RuleScope.OutOfCombat
            : RuleScope.InCombat;

        var ctx = new RuleExecutionContext(h.Repo, scope, p, opp, new Random(19))
        {
            LocalPlayerId = p.PlayerId,
            CurrentDay = p.Day,
            CurrentHour = p.Hour,
            TimeMs = 1000,
            CombatType = string.IsNullOrEmpty(ab.Trigger?.CombatType) ? "PVP" : ab.Trigger!.CombatType!,
            EmitCombatEvent = _ => probe.CombatEvents++,
            OnDetailedTriggeredEvent = (ev, _, _, _, _, _, _, _) => detailedEvents.Add(ev),
            OnDamageApplied = (target, amt, _, _) =>
            {
                if (amt > 0) target.CurrentHealth = Math.Max(0, target.CurrentHealth - amt);
                probe.DamageEvents++;
            },
            OnShieldApplied = (target, amt, _) =>
            {
                if (amt > 0) target.Shield += amt;
                probe.ShieldEvents++;
            },
            OnBurnApplied = (target, amt, _) =>
            {
                if (amt > 0) target.Burn += amt;
                probe.BurnEvents++;
            },
            OnPoisonApplied = (target, amt, _) =>
            {
                if (amt > 0) target.Poison += amt;
                probe.PoisonEvents++;
            },
            OnHealApplied = (target, amt, _) =>
            {
                if (amt > 0) target.CurrentHealth = Math.Min(target.HealthMax, target.CurrentHealth + amt);
                probe.HealEvents++;
            },
            OnRegenApplied = (target, amt, _, duration) =>
            {
                if (amt > 0) target.ModifyAttribute("HealthRegen", "Add", amt, duration);
                probe.RegenEvents++;
            },
            OnHasteApplied = (c, ms, _) =>
            {
                if (ms > 0) c.HasteMs += ms;
                probe.HasteEvents++;
            },
            OnSlowApplied = (c, ms, _) =>
            {
                if (ms > 0) c.SlowMs += ms;
                probe.SlowEvents++;
            },
            OnFreezeApplied = (c, ms, _) =>
            {
                if (ms > 0) c.FreezeMs += ms;
                probe.FreezeEvents++;
            },
            OnChargeApplied = (c, ms, _) =>
            {
                if (ms > 0) c.CooldownProgressMs += ms;
                probe.ChargeEvents++;
            },
            OnForceUseRequested = (_, _) => probe.ForceUseEvents++,
            OnBeginSandstormRequested = () => probe.SandstormEvents++,
            OnCardSpawned = (_, _) => probe.SpawnEvents++,
            OnCardTransformed = (_, _, _) => probe.TransformEvents++,
            OnCardDestroyed = (_, _) => probe.DestroyEvents++,
            OnCardUpgraded = (_, _) => probe.UpgradeEvents++,
            OnCardEnchanted = (_, _) => probe.EnchantEvents++,
            OnDealCardsToSelection = (_, _) => probe.DealSelectionEvents++,
            OnRerollEncounter = _ => probe.RerollEvents++,
            OnExitSelection = () => probe.ExitSelectionEvents++,
            OnActionCostSpent = (_, _, _) => probe.CostSpentEvents++
        };

        // 依次尝试该 Ability 支持的每一个触发分支（兼容 TTriggerOr）
        var triggerLeaves = CollectTriggerLeafNodes(ab.Trigger);
        foreach (var trig in triggerLeaves)
        {
            string tType = trig.Type;
            var (evSrc, evTgt, subjPlayer, changedAttr, prevVal, curVal, outcome) = BuildTriggerEventArgs(h, card, trig, ctx);
            var abCtx = ctx.WithCards(card, evSrc, evTgt);
            abCtx.LastAttributeChangeDelta = Math.Abs(curVal - prevVal);

            if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, abCtx))
                continue;
            if (!AbilityExecutor.MatchesTrigger(ab.Trigger, tType, card, p, evSrc, evTgt, subjPlayer, changedAttr, prevVal, curVal, ctx.CombatType, outcome, abCtx))
                continue;

            abCtx.ActiveExecution.AbilityId = ab.Id;
            if (AbilityExecutor.ExecuteAction(ab.Action, card, abCtx))
                return true;
        }
        return false;
    }

    private static (CardInstance? Src, CardInstance? Tgt, PlayerState? SubjPlayer, string? Attr, double Prev, double Cur, string Outcome)
        BuildTriggerEventArgs(SyntheticHarness h, CardInstance ownerCard, RuleNode trig, RuleExecutionContext ctx)
    {
        var p = h.Player;
        var opp = h.Opponent;
        string tType = trig.Type;

        switch (tType)
        {
            case "TTriggerOnCardFired":
                return (ownerCard, ownerCard, p, null, 0, 0, "");

            case "TTriggerOnItemUsed":
            case "TTriggerOnBeforeItemUsed":
            case "TTriggerOnCardCritted":
            {
                var src = PickMatchingSubjectCard(trig.Subject, ownerCard, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, opp) ?? h.LeftNeighbor ?? ownerCard;
                return (src, src, p, null, 0, 0, "");
            }

            case "TTriggerOnCardPerformedBurn":
            case "TTriggerOnCardPerformedCharge":
            case "TTriggerOnCardPerformedDamage":
            case "TTriggerOnCardPerformedDestruction":
            case "TTriggerOnCardPerformedFreeze":
            case "TTriggerOnCardPerformedHaste":
            case "TTriggerOnCardPerformedHeal":
            case "TTriggerOnCardPerformedOverHeal":
            case "TTriggerOnCardPerformedPoison":
            case "TTriggerOnCardPerformedRegen":
            case "TTriggerOnCardPerformedReload":
            case "TTriggerOnCardPerformedShield":
            case "TTriggerOnCardPerformedSlow":
            {
                var src = PickMatchingSubjectCard(trig.Subject, ownerCard, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, opp) ?? h.LeftNeighbor ?? ownerCard;
                var tgt = PickMatchingSubjectCard(trig.Target, ownerCard, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, opp) ?? h.RightNeighbor ?? src;
                return (src, tgt, p, null, 0, 0, "");
            }

            case "TTriggerOnCardStartedFlying":
            case "TTriggerOnCardStoppedFlying":
            case "TTriggerOnCardStartsFlying":
            case "TTriggerOnBeforeCardDestroyed":
            case "TTriggerOnCardTransformed":
            case "TTriggerOnCardAfterTransform":
            case "TTriggerOnCardQuestCompleted":
            case "TTriggerOnCardDisabled":
            case "TTriggerOnCardEnchanted":
            case "TTriggerOnCardRepaired":
            case "TTriggerOnCardCharged":
            case "TTriggerOnCardUpgraded":
            {
                var tgt = PickMatchingSubjectCard(trig.Subject, ownerCard, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, opp) ?? ownerCard;
                var src = PickMatchingSubjectCard(trig.Source, ownerCard, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, opp) ?? ownerCard;
                return (src, tgt, p, null, 0, 0, "");
            }

            case "TTriggerOnCardAttributeChanged":
            {
                var tgt = PickMatchingSubjectCard(trig.Subject, ownerCard, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, opp) ?? ownerCard;
                string attr = trig.AttributeChanged ?? "DamageAmount";
                bool isLoss = string.Equals(trig.ChangeType, "Loss", StringComparison.OrdinalIgnoreCase);
                double prev = isLoss ? 10 : 0;
                double cur = isLoss ? 0 : 10;
                return (tgt, tgt, p, attr, prev, cur, "");
            }

            case "TTriggerOnPlayerAttributeChanged":
            {
                var subjP = TargetResolver.ResolvePlayers(trig.Subject, ctx.WithCards(ownerCard)).FirstOrDefault() ?? p;
                string attr = trig.AttributeType ?? "Health";
                bool isLoss = string.Equals(trig.ChangeType, "Loss", StringComparison.OrdinalIgnoreCase);
                double prev = isLoss ? 600 : 10;
                double cur = isLoss ? 400 : 20;
                return (ownerCard, null, subjP, attr, prev, cur, "");
            }

            case "TTriggerOnPlayerDamaged":
            case "TTriggerOnPlayerDied":
            case "TTriggerOnPlayerEnraged":
            case "TTriggerOnPlayerEnrageEnded":
            case "TTriggerOnPlayerShielded":
            case "TTriggerOnPlayerLifeStealed":
            {
                var subjP = TargetResolver.ResolvePlayers(trig.Subject, ctx.WithCards(ownerCard)).FirstOrDefault() ?? p;
                return (h.OppCard ?? ownerCard, ownerCard, subjP, null, 0, 100, "");
            }

            case "TTriggerOnCardSold":
            case "TTriggerOnCardPurchased":
            case "TTriggerOnCardSelected":
            {
                var src = PickMatchingSubjectCard(trig.Subject, ownerCard, p, h.LeftNeighbor, h.RightNeighbor, h.StashPartner, h.Repo, opp) ?? ownerCard;
                return (src, src, p, null, 0, 0, "");
            }

            case "TTriggerOnEncounterSelected":
            {
                var enc = CreateMatchingEncounterCard(trig, h.Repo);
                return (enc, enc, p, null, 0, 0, "");
            }

            case "TTriggerOnFightEnded":
            {
                string outcome = string.IsNullOrEmpty(trig.CombatOutcome) ? "Win" : trig.CombatOutcome;
                return (null, null, p, null, 0, 0, outcome);
            }

            default:
                return (ownerCard, ownerCard, p, null, 0, 0, "");
        }
    }

    private static void FireMatchingQuestTrigger(SyntheticHarness h, RuleNode? trigger, string primaryTrigger, RuleExecutionContext ctx)
    {
        var p = h.Player;
        var card = h.SubjectCard;
        var leaf = CollectTriggerLeafNodes(trigger).FirstOrDefault() ?? trigger;
        if (leaf == null) return;

        var (evSrc, evTgt, subjPlayer, changedAttr, prevVal, curVal, outcome) = BuildTriggerEventArgs(h, card, leaf, ctx);
        ctx.LastAttributeChangeDelta = Math.Abs(curVal - prevVal);
        AbilityExecutor.TryAdvanceQuests(
            leaf.Type, p, evSrc, evTgt, subjPlayer ?? p,
            changedAttr, prevVal, curVal,
            string.IsNullOrEmpty(leaf.CombatType) ? "PVE" : leaf.CombatType!,
            outcome, ctx);
    }

    // =========================================================================
    // 前置条件与测试环境智能适配
    // =========================================================================

    private static void AdjustSubjectForActionPreconditions(SyntheticHarness h, AbilityDefinition ab)
    {
        var card = h.SubjectCard;
        if (ab.Action == null) return;

        // 若目标自身动作是 Reload 且自身无弹药损耗，确保 CurrentAmmo < AmmoMax
        if (ab.Action.Type == "TActionCardReload")
        {
            if (card.EffectiveAmmoMax > 0 && card.CurrentAmmo >= card.EffectiveAmmoMax)
                card.CurrentAmmo = Math.Max(0, card.EffectiveAmmoMax - 1);
            if (h.LeftNeighbor != null && h.LeftNeighbor.EffectiveAmmoMax > 0)
                h.LeftNeighbor.CurrentAmmo = 1;
            if (h.RightNeighbor != null && h.RightNeighbor.EffectiveAmmoMax > 0)
                h.RightNeighbor.CurrentAmmo = 1;
        }
        // 若目标自身动作是 FlyingStop 且针对 Self，则先把 Self 设为 Flying=true
        else if (ab.Action.Type == "TActionCardFlyingStop")
        {
            if (ab.Action.Target?.Type == "TTargetCardSelf")
                card.IsFlying = true;
            if (h.LeftNeighbor != null) h.LeftNeighbor.IsFlying = true;
            if (h.RightNeighbor != null) h.RightNeighbor.IsFlying = true;
        }
        // 若动作针对对手施加状态或伤害，确保对手卡牌允许目标
        else if (ab.Action.Type == "TActionCardDisable" && ab.Action.Target?.TargetSection == "OpponentHand")
        {
            foreach (var oc in h.Opponent.Hand.Cards) oc.IsDisabled = false;
        }
        // 若动作要求 TActionCardModifyAttribute(AttributeType="Ammo", Operation="Subtract")（如 Shuriken），确保弹药>0
        if (card.EffectiveAmmoMax > 0 && card.CurrentAmmo == 0 && ab.Action.Type != "TActionCardReload")
            card.CurrentAmmo = card.EffectiveAmmoMax;

        // 若存在自定义前置计数或阈值（如 Custom_0 >= Custom_1, Custom_0 > 4, Custom_2 > 0, Ammo == 0），按规则自适应准备
        if (ab.Trigger?.Type == "TTriggerOnCardAttributeChanged"
            && string.Equals(ab.Trigger.AttributeChanged, "Ammo", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ab.Trigger.ChangeType, "Loss", StringComparison.OrdinalIgnoreCase))
        {
            card.CurrentAmmo = 0;
        }
        AdjustSelfAttributesForPrerequisites(card, ab.Prerequisites);
        if (ab.Action.Type == "TActionGameSpawnCards"
            && string.Equals(ab.Action.SpawnContext?.Limit?.AttributeType, "Custom_2", StringComparison.OrdinalIgnoreCase)
            && card.GetAttribute("Custom_2") <= 0)
        {
            card.PermanentAdd["Custom_2"] = 1;
        }
    }

    private static void AdjustSubjectForAuraPreconditions(SyntheticHarness h, AuraDefinition aura)
    {
        var card = h.SubjectCard;
        AdjustSelfAttributesForPrerequisites(card, aura.Prerequisites);

        var act = aura.Action;
        if (act == null) return;
        var vn = act.GetValueNode(GameDatabaseLoader.JsonOptions);
        if (vn == null) return;

        // 若光环读取卡牌累计属性（如 Dragon Tooth Custom_2、Bootstraps Custom_1、Spider Fern Custom_3）且初始为 0，则予置非零样本值
        if (vn.Type is "TReferenceValueCardAttribute" or "TReferenceValueCardAttributeUnscaled"
            && vn.Target?.Type == "TTargetCardSelf"
            && !string.IsNullOrEmpty(vn.AttributeType)
            && vn.AttributeType.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase)
            && card.GetAttribute(vn.AttributeType) <= 0)
        {
            card.PermanentAdd[vn.AttributeType] = 2;
        }
        // 若光环按自身可见标签计数（如 Vat of Acid 初始 Tags=[] 靠卖牌加标签），补充样本标签以测试缩放链路
        if (vn.Type == "TReferenceValueCardTagCount" && card.Template.Tags.Count == 0 && card.ExtraTags.Count == 0)
            card.ExtraTags.Add("Potion");
    }

    private static void AdjustSelfAttributesForPrerequisites(CardInstance card, List<RuleNode>? prereqs)
    {
        if (prereqs == null) return;
        foreach (var pr in prereqs)
        {
            if (pr.Type == "TPrerequisiteCardAttributeComparator" && !string.IsNullOrEmpty(pr.Attribute))
            {
                if (!string.IsNullOrEmpty(pr.AttributeOther) && string.Equals(pr.Comparison, "Equal", StringComparison.OrdinalIgnoreCase))
                {
                    double otherVal = card.GetAttribute(pr.AttributeOther);
                    if (otherVal <= 0)
                    {
                        card.PermanentAdd[pr.AttributeOther] = card.PermanentAdd.GetValueOrDefault(pr.AttributeOther, 0) + 5;
                        otherVal = card.GetAttribute(pr.AttributeOther);
                    }
                    double nonPermAttr = card.GetAttribute(pr.Attribute) - card.PermanentAdd.GetValueOrDefault(pr.Attribute, 0);
                    card.PermanentAdd[pr.Attribute] = otherVal - nonPermAttr;
                }
                else
                {
                    card.PermanentAdd[pr.Attribute] = card.PermanentAdd.GetValueOrDefault(pr.Attribute, 0) + 10;
                }
            }
            else if (pr.Type == "TPrerequisiteCardCount" && pr.Subject?.Type == "TTargetCardSelf")
            {
                var cond = pr.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                SatisfySelfAttributeConditions(card, cond);
            }
        }
    }

    private static void SatisfySelfAttributeConditions(CardInstance card, RuleNode? cond)
    {
        if (cond == null) return;
        if (cond.Type == "TCardConditionalAttribute" && !string.IsNullOrEmpty(cond.Attribute))
        {
            if (cond.Attribute.Equals("Tiny", StringComparison.OrdinalIgnoreCase) || cond.Attribute.Equals("Flying", StringComparison.OrdinalIgnoreCase))
                return;
            var cvNode = cond.GetComparisonValueNode(GameDatabaseLoader.JsonOptions);
            double targetVal = 0;
            if (cvNode?.Type == "TReferenceValueCardAttribute" && !string.IsNullOrEmpty(cvNode.AttributeType))
                targetVal = Math.Max(1, card.GetAttribute(cvNode.AttributeType));
            else
                targetVal = cvNode?.GetFixedNumericValue() ?? cond.GetFixedComparisonValue() ?? 0;

            string op = cond.ComparisonOperator ?? "Equal";
            double cur = card.GetAttribute(cond.Attribute);
            if (op is "GreaterThan" && cur <= targetVal)
                card.PermanentAdd[cond.Attribute] = card.PermanentAdd.GetValueOrDefault(cond.Attribute, 0) + (targetVal - cur + 1);
            else if (op is "GreaterThanOrEqual" && cur < targetVal)
                card.PermanentAdd[cond.Attribute] = card.PermanentAdd.GetValueOrDefault(cond.Attribute, 0) + (targetVal - cur);
            else if (op is "LessThan" && cur >= targetVal)
                card.PermanentAdd[cond.Attribute] = card.PermanentAdd.GetValueOrDefault(cond.Attribute, 0) - (cur - targetVal + 1);
            else if (op is "LessThanOrEqual" && cur > targetVal)
                card.PermanentAdd[cond.Attribute] = card.PermanentAdd.GetValueOrDefault(cond.Attribute, 0) - (cur - targetVal);
            else if (op is "Equal" && Math.Abs(cur - targetVal) > 1e-6)
                card.PermanentAdd[cond.Attribute] = card.PermanentAdd.GetValueOrDefault(cond.Attribute, 0) + (targetVal - cur);
        }
        foreach (var sub in cond.GetConditionList(GameDatabaseLoader.JsonOptions))
            SatisfySelfAttributeConditions(card, sub);
    }

    private static void EnsureCostSatisfied(SyntheticHarness h, RuleNode? action)
    {
        if (action == null) return;
        if (action.Cost?.Type == "TActionCostPlayerAttribute")
        {
            var attr = action.Cost.PlayerAttributeType ?? "Gold";
            h.Player.Attributes[attr] = Math.Max(h.Player.GetAttribute(attr), 1000);
        }
        if (action.Type == "TActionCostPlayerAttribute")
        {
            var attr = action.PlayerAttributeType ?? "Gold";
            h.Player.Attributes[attr] = Math.Max(h.Player.GetAttribute(attr), 1000);
        }
    }

    private static CardInstance? PickMatchingSubjectCard(
        RuleNode? subject,
        CardInstance selfCard,
        PlayerState p,
        CardInstance? left,
        CardInstance? right,
        CardInstance? stash,
        GameDataRepository repo,
        PlayerState opp)
    {
        if (subject == null) return null;
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p, opp, new Random(1)) { SelfCard = selfCard };

        // 若 Subject 是 TTargetCardTriggerSource，直接测试候选卡牌哪一张满足 Conditions
        if (subject.Type == "TTargetCardTriggerSource")
        {
            var cond = subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            bool exclSelf = subject.ExcludeSelf ?? false;
            var candidates = new List<CardInstance>();
            if (left != null) candidates.Add(left);
            if (right != null) candidates.Add(right);
            if (stash != null) candidates.Add(stash);
            candidates.AddRange(p.Hand.Cards);
            candidates.AddRange(p.Stash.Cards);
            if (!exclSelf) candidates.Add(selfCard);
            foreach (var c in candidates)
            {
                if (exclSelf && ReferenceEquals(c, selfCard)) continue;
                if (cond == null || ValueAndConditionEvaluator.EvaluateCardCondition(cond, c, p, ctx))
                    return c;
            }
            return null;
        }

        var resolved = TargetResolver.ResolveCards(subject, ctx);
        return resolved.FirstOrDefault();
    }

    private static CardInstance CreateMatchingEncounterCard(RuleNode? trigger, GameDataRepository repo)
    {
        string id = Guid.NewGuid().ToString();
        string tier = "Gold";
        var tags = new List<string> { "Merchant", "Event" };

        if (trigger != null)
        {
            foreach (var leaf in CollectTriggerLeafNodes(trigger))
            {
                var cond = leaf.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (cond == null) continue;
                var foundIds = new List<string>();
                CollectRequiredIds(cond, foundIds);
                if (foundIds.Count > 0) id = foundIds[0];

                var foundTiers = new List<string>();
                CollectConditionalTiers(cond, foundTiers);
                if (foundTiers.Count > 0) tier = foundTiers[0];

                var foundTags = new List<string>();
                CollectConditionalTags(cond, foundTags);
                foreach (var t in foundTags)
                    if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase)) tags.Add(t);
            }
        }

        if (repo.GetCard(id) is { } realEnc)
            return new CardInstance(realEnc, TierHelper.ParseTier(tier));

        var synTpl = MakeSyntheticCard(id, cardType: "EventEncounter", tier: tier, tags: tags);
        return new CardInstance(synTpl, TierHelper.ParseTier(tier));
    }

    // =========================================================================
    // AST 辅助扫描工具
    // =========================================================================

    private static List<string> CollectAllTriggerTypes(RuleNode? trigger)
    {
        var list = new List<string>();
        if (trigger == null) return list;
        if (trigger.Type == "TTriggerOr" && trigger.Triggers != null)
        {
            foreach (var sub in trigger.Triggers)
                list.AddRange(CollectAllTriggerTypes(sub));
        }
        else if (!string.IsNullOrEmpty(trigger.Type))
        {
            list.Add(trigger.Type);
        }
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<RuleNode> CollectTriggerLeafNodes(RuleNode? trigger)
    {
        var list = new List<RuleNode>();
        if (trigger == null) return list;
        if (trigger.Type == "TTriggerOr" && trigger.Triggers != null)
        {
            foreach (var sub in trigger.Triggers)
                list.AddRange(CollectTriggerLeafNodes(sub));
        }
        else
        {
            list.Add(trigger);
        }
        return list;
    }

    private static bool IsOutOfCombatTrigger(string triggerType) => triggerType is
        "TTriggerOnCardSold" or "TTriggerOnCardPurchased" or "TTriggerOnCardSelected"
        or "TTriggerOnCardUpgraded" or "TTriggerOnCardEnchanted"
        or "TTriggerOnDayStarted" or "TTriggerOnHourStarted"
        or "TTriggerOnEncounterSelected" or "TTriggerOnEncounterEntered"
        or "TTriggerOnEncounterExited" or "TTriggerOnEncounterCardsDealt"
        or "TTriggerOnFightEnded" or "TTriggerOnPlayerLeveledUp"
        or "TTriggerOnRunStarted" or "TTriggerOnRunCompleted";

    private static bool IsEconomyAura(RuleNode? action)
        => action?.AttributeType is "SellPrice" or "BuyPrice" or "RerollCostModifier" or "Income";

    private static bool IsIntentionalZeroValueAction(RuleNode? action)
    {
        if (action == null) return false;
        if (action.Type is "TActionCardModifyAttribute" or "TActionPlayerModifyAttribute")
        {
            var vn = action.GetValueNode(GameDatabaseLoader.JsonOptions);
            if (vn != null && vn.Type == "TFixedValue")
            {
                double val = vn.GetFixedNumericValue() ?? 0;
                if (Math.Abs(val) < 1e-9) return true;
            }
        }
        return false;
    }

    private static bool HasZeroSelfSkillPrereq(List<RuleNode>? prereqs, string selfId)
    {
        if (prereqs == null) return false;
        foreach (var p in prereqs)
        {
            if (p.Type == "TPrerequisiteCardCount"
                && p.Comparison == "Equal"
                && Math.Abs(p.Amount ?? -1) < 1e-9
                && p.Subject?.TargetSection == "AbsolutePlayerSkills")
            {
                var ids = new List<string>();
                CollectRequiredIds(p.Subject, ids);
                if (ids.Contains(selfId, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string? FindRequiredEnchantment(List<RuleNode>? prereqs, RuleNode? trig, RuleNode? act)
    {
        string? found = null;
        void Walk(RuleNode? n)
        {
            if (n == null || found != null) return;
            if (n.Type == "TCardConditionalHasEnchantment" && !string.IsNullOrEmpty(n.Enchantment) && n.IsNot != true)
            {
                found = n.Enchantment;
                return;
            }
            if (n.Subject != null) Walk(n.Subject);
            if (n.Target != null) Walk(n.Target);
            var cond = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (cond != null) Walk(cond);
            foreach (var c in n.GetConditionList(GameDatabaseLoader.JsonOptions)) Walk(c);
        }
        if (prereqs != null) foreach (var p in prereqs)
        {
            if (p.Type == "TPrerequisiteCardCount" && (p.Amount ?? 1) >= 1 && p.Subject?.Type == "TTargetCardSelf")
                Walk(p);
        }
        if (found != null) return found;

        // 同步支持 Enchantment==null 且 IsNot==true（代表“必须带任意附魔”，如 Potion Potion Ability#2/#4）
        if (prereqs != null) foreach (var p in prereqs)
        {
            if (p.Type != "TPrerequisiteCardCount" || p.Subject?.Type != "TTargetCardSelf") continue;
            bool requiresZero = string.Equals(p.Comparison, "Equal", StringComparison.OrdinalIgnoreCase) && Math.Abs(p.Amount ?? 0) < 1e-9;
            if (requiresZero) continue;
            bool hasAnyEnchCond(RuleNode? n)
            {
                if (n == null) return false;
                if (n.Type == "TCardConditionalHasEnchantment" && n.IsNot == true && string.IsNullOrEmpty(n.Enchantment))
                    return true;
                var c = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (hasAnyEnchCond(c)) return true;
                return n.GetConditionList(GameDatabaseLoader.JsonOptions).Any(hasAnyEnchCond);
            }
            if (hasAnyEnchCond(p.Subject))
                return "Fiery";
        }
        return found;
    }

    private static bool NeedsNonDiamondSelf(List<RuleNode>? prereqs, RuleNode? act)
    {
        if (act?.Type == "TActionCardUpgrade" && act.Target?.Type == "TTargetCardSelf") return true;
        if (prereqs == null) return false;
        foreach (var p in prereqs)
        {
            var cond = p.Subject?.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (cond?.Type == "TCardConditionalTier" && cond.IsNot == true && (cond.Tiers?.Contains("Diamond") ?? false))
                return true;
        }
        return false;
    }

    private static bool NeedsSelfFlying(List<RuleNode>? prereqs, RuleNode? trig, RuleNode? act)
    {
        if (trig?.Type is "TTriggerOnCardStoppedFlying") return true;
        if (act?.Type is "TActionCardFlyingStop" && act.Target?.Type == "TTargetCardSelf") return true;
        return false;
    }

    private static bool NeedsSelfTiny(List<RuleNode>? prereqs)
    {
        if (prereqs == null) return false;
        foreach (var p in prereqs)
        {
            if (p.Subject?.Type != "TTargetCardSelf") continue;
            // 若外层 Prerequisite 要求 Count == 0（如 Shipment Ability#0 要求并非 Tiny），绝不能反向注入 Tiny=1
            if (string.Equals(p.Comparison, "Equal", StringComparison.OrdinalIgnoreCase) && Math.Abs(p.Amount ?? 0) < 1e-9)
                continue;
            var cond = p.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (cond?.Type == "TCardConditionalAttribute"
                && string.Equals(cond.Attribute, "Tiny", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(cond.ComparisonOperator, "GreaterThan", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(cond.ComparisonOperator, "Equal", StringComparison.OrdinalIgnoreCase)
                        && (cond.GetComparisonValueNode(GameDatabaseLoader.JsonOptions)?.GetFixedNumericValue() ?? cond.GetFixedComparisonValue() ?? 0) >= 1)))
                return true;
        }
        return false;
    }

    private static int FindRequiredMinDay(List<RuleNode>? prereqs)
    {
        int maxDay = 0;
        void Walk(RuleNode? n)
        {
            if (n == null) return;
            if (n.Type == "TRunConditionalCurrentDay" && n.CurrentDay.HasValue)
                maxDay = Math.Max(maxDay, n.CurrentDay.Value);
            var c = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (c != null) Walk(c);
            foreach (var sub in n.GetConditionList(GameDatabaseLoader.JsonOptions)) Walk(sub);
        }
        if (prereqs != null) foreach (var p in prereqs) Walk(p);
        return maxDay;
    }

    private static string? FindUniqueSelfHandTagRequirement(List<RuleNode>? prereqs)
    {
        if (prereqs == null) return null;
        foreach (var p in prereqs)
        {
            if (p.Type == "TPrerequisiteCardCount"
                && string.Equals(p.Comparison, "Equal", StringComparison.OrdinalIgnoreCase)
                && Math.Abs((p.Amount ?? 0) - 1.0) < 1e-6
                && p.Subject?.Type == "TTargetCardSection"
                && p.Subject.TargetSection is "SelfHand" or "SelfBoard" or "SelfHandAndStash")
            {
                var cond = p.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (cond?.Type == "TCardConditionalTag" && cond.Operator != "None" && cond.Tags is { Count: > 0 })
                    return cond.Tags[0];
            }
        }
        return null;
    }

    private static int FindRequiredOpponentAttrMin(List<RuleNode>? prereqs, string attrName)
    {
        int found = 0;
        void Walk(RuleNode? n)
        {
            if (n == null) return;
            if (n.Type == "TPlayerConditionalAttribute" && string.Equals(n.Attribute, attrName, StringComparison.OrdinalIgnoreCase))
            {
                double val = n.GetComparisonValueNode(GameDatabaseLoader.JsonOptions)?.GetFixedNumericValue() ?? n.GetFixedComparisonValue() ?? 0;
                found = Math.Max(found, (int)Math.Ceiling(val) + 5);
            }
            if (n.Subject != null) Walk(n.Subject);
            var c = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (c != null) Walk(c);
            foreach (var sub in n.GetConditionList(GameDatabaseLoader.JsonOptions)) Walk(sub);
        }
        if (prereqs != null) foreach (var p in prereqs) Walk(p);
        return found;
    }

    private static bool NeedsSelfHpGreaterThanOpp(List<RuleNode>? prereqs)
    {
        bool hit = false;
        void Walk(RuleNode? n)
        {
            if (n == null || hit) return;
            if (n.Type == "TPlayerConditionalAttribute"
                && string.Equals(n.Attribute, "Health", StringComparison.OrdinalIgnoreCase)
                && string.Equals(n.ComparisonOperator, "GreaterThan", StringComparison.OrdinalIgnoreCase))
            {
                var cv = n.GetComparisonValueNode(GameDatabaseLoader.JsonOptions);
                if (cv?.Target?.TargetMode == "Opponent") hit = true;
            }
            if (n.Subject != null) Walk(n.Subject);
            var c = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (c != null) Walk(c);
            foreach (var sub in n.GetConditionList(GameDatabaseLoader.JsonOptions)) Walk(sub);
        }
        if (prereqs != null) foreach (var p in prereqs) Walk(p);
        return hit;
    }

    private static List<string> BuildPartnerTags(IEnumerable<string> excludeTags, string? uniqueTagOnly, bool keepIfUnique)
    {
        var excl = new HashSet<string>(excludeTags, StringComparer.OrdinalIgnoreCase);
        var res = new List<string>();
        foreach (var t in AllNonWeaponNonTechNonToyTags.Concat(["Weapon", "Tech", "Toy"]))
        {
            if (excl.Contains(t)) continue;
            if (!string.IsNullOrEmpty(uniqueTagOnly) && string.Equals(t, uniqueTagOnly, StringComparison.OrdinalIgnoreCase) && !keepIfUnique)
                continue;
            if (!res.Contains(t, StringComparer.OrdinalIgnoreCase))
                res.Add(t);
        }
        if (keepIfUnique && !string.IsNullOrEmpty(uniqueTagOnly) && !res.Contains(uniqueTagOnly, StringComparer.OrdinalIgnoreCase))
            res.Add(uniqueTagOnly);
        return res;
    }

    private static Dictionary<string, TierData> BuildStandardTiers() => new()
    {
        ["Bronze"] = new TierData(),
        ["Silver"] = new TierData(),
        ["Gold"] = new TierData(),
        ["Diamond"] = new TierData()
    };

    private static string DetermineRightNeighborSize(RuleNode? trig, List<RuleNode>? prereqs, RuleNode? act)
    {
        string found = "Small";
        void Walk(RuleNode? n)
        {
            if (n == null) return;
            if (n.Type == "TTargetCardPositional" && string.Equals(n.TargetMode, "RightCard", StringComparison.OrdinalIgnoreCase))
            {
                var cond = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (cond != null)
                {
                    var sizes = new List<string>();
                    CollectSizes(cond, sizes);
                    if (sizes.Contains("Large", StringComparer.OrdinalIgnoreCase)) found = "Large";
                    else if (sizes.Contains("Medium", StringComparer.OrdinalIgnoreCase)) found = "Medium";
                }
            }
            if (n.Subject != null) Walk(n.Subject);
            if (n.Target != null) Walk(n.Target);
        }
        Walk(trig);
        if (prereqs != null) foreach (var p in prereqs) Walk(p);
        Walk(act);
        return found;
    }

    private static void CollectSizes(RuleNode? n, List<string> sizes)
    {
        if (n == null) return;
        if (n.Type == "TCardConditionalSize" && n.IsNot != true && n.Sizes != null)
            sizes.AddRange(n.Sizes);
        var c = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        if (c != null) CollectSizes(c, sizes);
        foreach (var sub in n.GetConditionList(GameDatabaseLoader.JsonOptions)) CollectSizes(sub, sizes);
    }

    private static bool NeedsSelfDisabledOrDestroyed(List<RuleNode>? prereqs, RuleNode? act) => false;

    private static bool NeedsTinyNeighbor(List<RuleNode>? prereqs, RuleNode? act)
    {
        if (prereqs == null) return false;
        foreach (var p in prereqs)
        {
            if (p.Subject?.Type != "TTargetCardPositional") continue;
            var cond = p.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            foreach (var sub in cond?.GetConditionList(GameDatabaseLoader.JsonOptions) ?? [])
            {
                if (sub.Type == "TCardConditionalAttribute"
                    && string.Equals(sub.Attribute, "Tiny", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(sub.ComparisonOperator, "GreaterThan", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string DetermineNeighborSize(RuleNode? trig, List<RuleNode>? prereqs, RuleNode? act)
    {
        string found = "Small";
        void Walk(RuleNode? n)
        {
            if (n == null) return;
            if (n.Type == "TCardConditionalSize" && n.IsNot != true && n.Sizes is { Count: > 0 })
            {
                if (n.Sizes.Contains("Medium", StringComparer.OrdinalIgnoreCase) && !n.Sizes.Contains("Small", StringComparer.OrdinalIgnoreCase))
                    found = "Medium";
                else if (n.Sizes.Contains("Large", StringComparer.OrdinalIgnoreCase) && !n.Sizes.Contains("Small", StringComparer.OrdinalIgnoreCase))
                    found = "Large";
            }
            if (n.Subject != null) Walk(n.Subject);
            if (n.Target != null) Walk(n.Target);
            if (n.Source != null) Walk(n.Source);
            var cond = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (cond != null) Walk(cond);
            foreach (var c in n.GetConditionList(GameDatabaseLoader.JsonOptions)) Walk(c);
        }
        Walk(trig);
        if (prereqs != null) foreach (var p in prereqs) Walk(p);
        Walk(act);
        return found;
    }

    private static bool NeedsEmptyAmmoCondition(RuleNode? trig, List<RuleNode>? prereqs, RuleNode? act)
    {
        bool hit = false;
        void Walk(RuleNode? n)
        {
            if (n == null || hit) return;
            if (n.Type == "TCardConditionalAttribute" && string.Equals(n.Attribute, "Ammo", StringComparison.OrdinalIgnoreCase)
                && string.Equals(n.ComparisonOperator, "Equal", StringComparison.OrdinalIgnoreCase))
            {
                double? cv = n.GetComparisonValueNode(GameDatabaseLoader.JsonOptions)?.GetFixedNumericValue() ?? n.GetFixedComparisonValue();
                if (cv.HasValue && Math.Abs(cv.Value) < 1e-9) hit = true;
            }
            if (n.Subject != null) Walk(n.Subject);
            if (n.Target != null) Walk(n.Target);
            var cond = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (cond != null) Walk(cond);
            foreach (var c in n.GetConditionList(GameDatabaseLoader.JsonOptions)) Walk(c);
        }
        Walk(trig);
        if (prereqs != null) foreach (var p in prereqs) Walk(p);
        Walk(act);
        return hit;
    }

    private static bool NeedsDestroyedCondition(RuleNode? trig, List<RuleNode>? prereqs, RuleNode? act)
    {
        bool hit = false;
        void Walk(RuleNode? n)
        {
            if (n == null || hit) return;
            if (n.Type == "TCardConditionalState" && (n.States?.Contains("Destroyed", StringComparer.OrdinalIgnoreCase) ?? false))
                hit = true;
            if (n.Subject != null) Walk(n.Subject);
            if (n.Target != null) Walk(n.Target);
            var cond = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (cond != null) Walk(cond);
            foreach (var c in n.GetConditionList(GameDatabaseLoader.JsonOptions)) Walk(c);
        }
        Walk(trig);
        if (prereqs != null) foreach (var p in prereqs) Walk(p);
        Walk(act);
        return hit;
    }

    private static void CollectRequiredIds(RuleNode? n, List<string> ids)
    {
        if (n == null) return;
        // 若是 TPrerequisiteCardCount 要求 Count == 0（如 Red Button #0/#1），不得注入该 ID
        if (n.Type == "TPrerequisiteCardCount")
        {
            if (string.Equals(n.Comparison, "Equal", StringComparison.OrdinalIgnoreCase) && Math.Abs(n.Amount ?? 0) < 1e-9)
                return;
            // 若 Subject 下有 TCardConditionalOr 且要求 Count == 1（如 Red Button #2/#3），仅采集第一个分支 ID
            if (string.Equals(n.Comparison, "Equal", StringComparison.OrdinalIgnoreCase)
                && Math.Abs((n.Amount ?? 0) - 1.0) < 1e-6
                && n.Subject != null)
            {
                var subCond = n.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (subCond?.Type == "TCardConditionalOr")
                {
                    var orIds = new List<string>();
                    foreach (var branch in subCond.GetConditionList(GameDatabaseLoader.JsonOptions))
                    {
                        var tmp = new List<string>();
                        CollectRequiredIds(branch, tmp);
                        if (tmp.Count > 0)
                        {
                            orIds.Add(tmp[0]);
                            break;
                        }
                    }
                    ids.AddRange(orIds);
                    return;
                }
            }
        }
        if (n.Type == "TCardConditionalId" && n.IsNot != true)
        {
            if (!string.IsNullOrEmpty(n.Id)) ids.Add(n.Id);
            if (n.Ids != null) ids.AddRange(n.Ids);
        }
        if (n.Subject != null) CollectRequiredIds(n.Subject, ids);
        if (n.Target != null) CollectRequiredIds(n.Target, ids);
        if (n.Source != null) CollectRequiredIds(n.Source, ids);
        if (n.SpawnContext != null) CollectRequiredIds(n.SpawnContext, ids);
        if (n.Triggers != null) foreach (var t in n.Triggers) CollectRequiredIds(t, ids);
        if (n.Actions != null) foreach (var a in n.Actions) CollectRequiredIds(a, ids);
        if (n.Groups != null) foreach (var g in n.Groups) CollectRequiredIds(g, ids);
        if (n.Filters != null) foreach (var f in n.Filters) CollectRequiredIds(f, ids);
        foreach (var c in n.GetConditionList(GameDatabaseLoader.JsonOptions)) CollectRequiredIds(c, ids);
    }

    private static string? FindNotEqualOneTagRequirement(List<RuleNode>? prereqs)
    {
        if (prereqs == null) return null;
        foreach (var p in prereqs)
        {
            if (p.Type == "TPrerequisiteCardCount"
                && string.Equals(p.Comparison, "NotEqual", StringComparison.OrdinalIgnoreCase)
                && Math.Abs((p.Amount ?? 0) - 1.0) < 1e-6
                && p.Subject?.TargetSection is "SelfHand" or "SelfBoard" or "SelfHandAndStash")
            {
                var cond = p.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (cond?.Type == "TCardConditionalTag" && cond.Operator != "None" && cond.Tags is { Count: > 0 })
                    return cond.Tags[0];
            }
        }
        return null;
    }

    private static string? FindZeroTagInHandOrNeighborRequirement(List<RuleNode>? prereqs)
    {
        if (prereqs == null) return null;
        foreach (var p in prereqs)
        {
            if (p.Type == "TPrerequisiteCardCount"
                && string.Equals(p.Comparison, "Equal", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(p.Amount ?? -1) < 1e-6
                && p.Subject != null)
            {
                var cond = p.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (cond?.Type == "TCardConditionalTag" && cond.Operator != "None" && cond.Tags is { Count: > 0 })
                    return cond.Tags[0];
            }
        }
        return null;
    }

    private static int FindMinTagCountInHandRequirement(List<RuleNode>? prereqs, string tag)
    {
        int min = 0;
        if (prereqs == null) return 0;
        foreach (var p in prereqs)
        {
            if (p.Type == "TPrerequisiteCardCount"
                && p.Comparison is "GreaterThanOrEqual" or "GreaterThan"
                && p.Subject?.TargetSection is "SelfHand" or "SelfBoard" or "SelfHandAndStash")
            {
                var cond = p.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (cond?.Type == "TCardConditionalTag" && cond.Operator != "None" && (cond.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) ?? false))
                {
                    int req = (int)Math.Ceiling(p.Amount ?? 0) + (p.Comparison == "GreaterThan" ? 1 : 0);
                    min = Math.Max(min, req);
                }
            }
        }
        return min;
    }

    private static int? FindMaxSelfHandCountRequirement(List<RuleNode>? prereqs)
    {
        int? max = null;
        if (prereqs == null) return null;
        foreach (var p in prereqs)
        {
            if (p.Type == "TPrerequisiteCardCount"
                && p.Subject?.TargetSection == "SelfHand"
                && p.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions) == null)
            {
                int cap = p.Comparison switch
                {
                    "LessThanOrEqual" => (int)Math.Floor(p.Amount ?? 5),
                    "LessThan" => (int)Math.Floor((p.Amount ?? 6) - 1),
                    _ => -1
                };
                if (cap >= 0) max = max.HasValue ? Math.Min(max.Value, cap) : cap;
            }
        }
        return max;
    }

    private static int FindMinSelfHandCountRequirement(List<RuleNode>? prereqs)
    {
        int min = 0;
        if (prereqs == null) return 0;
        foreach (var p in prereqs)
        {
            if (p.Type == "TPrerequisiteCardCount"
                && p.Subject?.TargetSection == "SelfHand"
                && p.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions) == null)
            {
                int floor = p.Comparison switch
                {
                    "GreaterThanOrEqual" => (int)Math.Ceiling(p.Amount ?? 0),
                    "GreaterThan" => (int)Math.Floor(p.Amount ?? 0) + 1,
                    _ => 0
                };
                min = Math.Max(min, floor);
            }
        }
        return min;
    }

    private static bool HasNoneHiddenTagCondition(RuleNode? root, string hiddenTag)
    {
        bool hit = false;
        void Walk(RuleNode? n)
        {
            if (n == null || hit) return;
            if (n.Type == "TCardConditionalHiddenTag"
                && string.Equals(n.Operator, "None", StringComparison.OrdinalIgnoreCase)
                && ((n.Tags?.Contains(hiddenTag, StringComparer.OrdinalIgnoreCase) ?? false)
                    || (n.HiddenTags?.Contains(hiddenTag, StringComparer.OrdinalIgnoreCase) ?? false)))
            {
                hit = true;
                return;
            }
            if (n.Subject != null) Walk(n.Subject);
            if (n.Target != null) Walk(n.Target);
            if (n.Triggers != null) foreach (var t in n.Triggers) Walk(t);
            foreach (var sub in n.GetConditionList(GameDatabaseLoader.JsonOptions)) Walk(sub);
        }
        Walk(root);
        return hit;
    }

    private static bool NeedsOpponentLowHpThreshold(List<RuleNode>? prereqs)
    {
        bool hit = false;
        void Walk(RuleNode? n)
        {
            if (n == null || hit) return;
            if (n.Type == "TPlayerConditionalAttribute"
                && string.Equals(n.Attribute, "Health", StringComparison.OrdinalIgnoreCase)
                && n.ComparisonOperator is "LessThan" or "LessThanOrEqual")
            {
                var cv = n.GetComparisonValueNode(GameDatabaseLoader.JsonOptions);
                if (cv?.Type == "TReferenceValuePlayerAttribute"
                    && string.Equals(cv.AttributeType, "HealthMax", StringComparison.OrdinalIgnoreCase)
                    && (cv.Modifier?.Value?.GetFixedNumericValue() ?? 1.0) < 0.45)
                {
                    hit = true;
                }
            }
            if (n.Subject != null) Walk(n.Subject);
            var c = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (c != null) Walk(c);
            foreach (var sub in n.GetConditionList(GameDatabaseLoader.JsonOptions)) Walk(sub);
        }
        if (prereqs != null) foreach (var p in prereqs) Walk(p);
        return hit;
    }

    private static bool NeedsMediumCorePartner(List<RuleNode>? prereqs, RuleNode? trig, RuleNode? act)
    {
        bool hasCore = false, hasMedium = false;
        void Walk(RuleNode? n)
        {
            if (n == null) return;
            if (n.Type == "TCardConditionalTag" && (n.Tags?.Contains("Core", StringComparer.OrdinalIgnoreCase) ?? false))
                hasCore = true;
            if (n.Type == "TCardConditionalSize" && (n.Sizes?.Contains("Medium", StringComparer.OrdinalIgnoreCase) ?? false))
                hasMedium = true;
            if (n.Subject != null) Walk(n.Subject);
            if (n.Target != null) Walk(n.Target);
            var c = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (c != null) Walk(c);
            foreach (var sub in n.GetConditionList(GameDatabaseLoader.JsonOptions)) Walk(sub);
        }
        Walk(trig);
        if (prereqs != null) foreach (var p in prereqs) Walk(p);
        Walk(act);
        return hasCore && hasMedium;
    }

    private static bool NeedsTwoEnchantedOrFlyingInHand(RuleNode? trig, RuleNode? act)
    {
        bool HasCond(RuleNode? n, string tName)
        {
            if (n == null) return false;
            if (n.Type == tName) return true;
            if (n.Subject != null && HasCond(n.Subject, tName)) return true;
            if (n.Target != null && HasCond(n.Target, tName)) return true;
            if (HasCond(n.GetSingleCondition(GameDatabaseLoader.JsonOptions), tName)) return true;
            return n.GetConditionList(GameDatabaseLoader.JsonOptions).Any(sub => HasCond(sub, tName));
        }
        return HasCond(act, "TCardConditionalTriggerSource")
            && (HasCond(act, "TCardConditionalHasEnchantment") || HasCond(trig, "TCardConditionalHasEnchantment"));
    }

    private static void CollectConditionalTiers(RuleNode? n, List<string> tiers)
    {
        if (n == null) return;
        if (n.Type == "TCardConditionalTier" && n.IsNot != true && n.Tiers != null)
            tiers.AddRange(n.Tiers);
        var cond = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        if (cond != null) CollectConditionalTiers(cond, tiers);
        foreach (var c in n.GetConditionList(GameDatabaseLoader.JsonOptions)) CollectConditionalTiers(c, tiers);
    }

    private static void CollectConditionalTags(RuleNode? n, List<string> tags)
    {
        if (n == null) return;
        if (n.Type == "TCardConditionalTag" && n.Tags != null)
            tags.AddRange(n.Tags);
        var cond = n.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        if (cond != null) CollectConditionalTags(cond, tags);
        foreach (var c in n.GetConditionList(GameDatabaseLoader.JsonOptions)) CollectConditionalTags(c, tags);
    }

    // =========================================================================
    // 状态快照与差分比对工具
    // =========================================================================

    private sealed class BoardSnapshot
    {
        public Dictionary<string, double> P1Attrs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> P2Attrs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int P1Portrait { get; init; }
        public int P1CardCount { get; init; }
        public int P2CardCount { get; init; }
        public Dictionary<string, CardSnap> Cards { get; init; } = new();
    }

    private sealed record CardSnap(
        string TemplateId,
        CardTier Tier,
        string? Enchantment,
        int CurrentAmmo,
        int FreezeMs,
        int SlowMs,
        int HasteMs,
        bool IsFlying,
        bool IsDisabled,
        bool IsDestroyed,
        int TagCount,
        Dictionary<string, double> Attrs);

    private static readonly string[] TrackedPlayerAttrs =
    [
        "Gold", "Income", "HealthMax", "Health", "Experience", "Level", "Prestige",
        "HealthRegen", "Shield", "Burn", "Poison", "Rage", "Tempo", "PercentDamageReduction"
    ];

    private static readonly string[] TrackedCardAttrs =
    [
        "DamageAmount", "ShieldApplyAmount", "BurnApplyAmount", "PoisonApplyAmount",
        "HealAmount", "RegenApplyAmount", "CritChance", "Multicast", "Lifesteal",
        "CooldownMax", "FlatCooldownReduction", "PercentCooldownReduction",
        "AmmoMax", "Ammo", "SellPrice", "BuyPrice", "Tiny", "Flying",
        "Custom_0", "Custom_1", "Custom_2", "Custom_3", "Custom_4", "Custom_5", "Custom_6", "Custom_8"
    ];

    private static BoardSnapshot CaptureBoardSnapshot(PlayerState p1, PlayerState p2, CardInstance extraSelf)
    {
        static Dictionary<string, double> SnapPlayer(PlayerState p)
        {
            var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in TrackedPlayerAttrs) dict[k] = p.GetAttribute(k);
            foreach (var k in p.Attributes.Keys) dict[k] = p.GetAttribute(k);
            return dict;
        }

        var cards = new Dictionary<string, CardSnap>();
        var allCards = p1.EnumerateAllOwnedCards().Concat(p2.EnumerateAllOwnedCards()).Append(extraSelf);
        foreach (var c in allCards)
        {
            if (cards.ContainsKey(c.InstanceId)) continue;
            var a = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in TrackedCardAttrs) a[k] = c.GetAttribute(k);
            foreach (var k in c.BaseAttributes.Keys) a[k] = c.GetAttribute(k);
            foreach (var k in c.PermanentAdd.Keys) a[k] = c.GetAttribute(k);
            foreach (var k in c.CombatAdd.Keys) a[k] = c.GetAttribute(k);
            cards[c.InstanceId] = new CardSnap(
                c.Template.Id, c.Tier, c.Enchantment, c.CurrentAmmo,
                c.FreezeMs, c.SlowMs, c.HasteMs, c.IsFlying, c.IsDisabled, c.IsDestroyed,
                c.GetAllTags().Count(), a);
        }

        return new BoardSnapshot
        {
            P1Attrs = SnapPlayer(p1),
            P2Attrs = SnapPlayer(p2),
            P1Portrait = p1.PortraitIndex,
            P1CardCount = p1.EnumerateAllOwnedCards().Count,
            P2CardCount = p2.EnumerateAllOwnedCards().Count,
            Cards = cards
        };
    }

    private static bool HasBoardDelta(BoardSnapshot before, BoardSnapshot after)
    {
        if (before.P1Portrait != after.P1Portrait) return true;
        if (before.P1CardCount != after.P1CardCount || before.P2CardCount != after.P2CardCount) return true;
        if (!DictionariesRoughlyEqual(before.P1Attrs, after.P1Attrs)) return true;
        if (!DictionariesRoughlyEqual(before.P2Attrs, after.P2Attrs)) return true;
        if (before.Cards.Count != after.Cards.Count) return true;
        foreach (var (id, b) in before.Cards)
        {
            if (!after.Cards.TryGetValue(id, out var a)) return true;
            if (!string.Equals(b.TemplateId, a.TemplateId, StringComparison.Ordinal)) return true;
            if (b.Tier != a.Tier || !string.Equals(b.Enchantment, a.Enchantment, StringComparison.Ordinal)) return true;
            if (b.CurrentAmmo != a.CurrentAmmo || b.FreezeMs != a.FreezeMs || b.SlowMs != a.SlowMs || b.HasteMs != a.HasteMs) return true;
            if (b.IsFlying != a.IsFlying || b.IsDisabled != a.IsDisabled || b.IsDestroyed != a.IsDestroyed || b.TagCount != a.TagCount) return true;
            if (!DictionariesRoughlyEqual(b.Attrs, a.Attrs)) return true;
        }
        return false;
    }

    private static bool HasOutOfCombatDelta(
        OutOfCombatBatchResult? batch,
        BoardSnapshot before,
        PlayerState p1,
        PlayerState p2,
        CardInstance extraSelf)
    {
        if (batch != null)
        {
            if (batch.PlayerAttributeChanges.Count > 0
                || batch.CardAttributeChanges.Count > 0
                || batch.SpawnedCards.Count > 0
                || batch.RemovedCards.Count > 0
                || batch.UpgradedCards.Count > 0
                || batch.EnchantedCards.Count > 0
                || batch.TransformedCards.Count > 0
                || batch.DealtSelectionRequests.Count > 0
                || batch.RerollRequests.Count > 0
                || batch.ExitSelectionRequested)
                return true;
        }
        return HasBoardDelta(before, CaptureBoardSnapshot(p1, p2, extraSelf));
    }

    private sealed class AuraSnapshot
    {
        public Dictionary<string, double> P1AuraAdd { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> P1AuraMult { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> P2AuraAdd { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> P2AuraMult { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (Dictionary<string, double> Add, Dictionary<string, double> Mult, int AuraTagCount)> CardAuras { get; init; } = new();
    }

    private static AuraSnapshot CaptureAuraSnapshot(PlayerState p1, PlayerState p2)
    {
        var map = new Dictionary<string, (Dictionary<string, double>, Dictionary<string, double>, int)>();
        foreach (var c in p1.EnumerateAllOwnedCards().Concat(p2.EnumerateAllOwnedCards()))
        {
            map[c.InstanceId] = (
                new Dictionary<string, double>(c.AuraAdd, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, double>(c.AuraMult, StringComparer.OrdinalIgnoreCase),
                c.AuraTags.Count);
        }
        return new AuraSnapshot
        {
            P1AuraAdd = new Dictionary<string, double>(p1.AuraAdd, StringComparer.OrdinalIgnoreCase),
            P1AuraMult = new Dictionary<string, double>(p1.AuraMult, StringComparer.OrdinalIgnoreCase),
            P2AuraAdd = new Dictionary<string, double>(p2.AuraAdd, StringComparer.OrdinalIgnoreCase),
            P2AuraMult = new Dictionary<string, double>(p2.AuraMult, StringComparer.OrdinalIgnoreCase),
            CardAuras = map
        };
    }

    private static bool HasAuraDelta(AuraSnapshot a, AuraSnapshot b)
    {
        if (!DictionariesRoughlyEqual(a.P1AuraAdd, b.P1AuraAdd, 0.0)) return true;
        if (!DictionariesRoughlyEqual(a.P1AuraMult, b.P1AuraMult, 1.0)) return true;
        if (!DictionariesRoughlyEqual(a.P2AuraAdd, b.P2AuraAdd, 0.0)) return true;
        if (!DictionariesRoughlyEqual(a.P2AuraMult, b.P2AuraMult, 1.0)) return true;
        foreach (var (id, va) in a.CardAuras)
        {
            if (!b.CardAuras.TryGetValue(id, out var vb)) continue;
            if (va.AuraTagCount != vb.AuraTagCount) return true;
            if (!DictionariesRoughlyEqual(va.Add, vb.Add, 0.0)) return true;
            if (!DictionariesRoughlyEqual(va.Mult, vb.Mult, 1.0)) return true;
        }
        return false;
    }

    private static bool DictionariesRoughlyEqual(Dictionary<string, double> a, Dictionary<string, double> b, double defaultVal = 0.0)
    {
        var allKeys = new HashSet<string>(a.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in b.Keys) allKeys.Add(k);
        foreach (var k in allKeys)
        {
            double va = a.GetValueOrDefault(k, defaultVal);
            double vb = b.GetValueOrDefault(k, defaultVal);
            if (Math.Abs(va - vb) > 1e-6) return false;
        }
        return true;
    }

    private static CardTemplate MakeSyntheticCard(
        string id,
        string size = "Small",
        string cardType = "Item",
        string tier = "Bronze",
        IEnumerable<string>? heroes = null,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? hiddenTags = null,
        Dictionary<string, TierData>? tiers = null,
        Dictionary<string, double>? baseAttrs = null,
        Dictionary<string, AbilityDefinition>? abilities = null)
    {
        return new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = cardType,
            Size = size,
            StartingTier = tier,
            Heroes = heroes?.ToList() ?? [],
            Tags = tags?.ToList() ?? [],
            HiddenTags = hiddenTags?.ToList() ?? [],
            Tiers = tiers,
            Attributes = baseAttrs ?? new Dictionary<string, double>(),
            Abilities = abilities ?? new Dictionary<string, AbilityDefinition>(),
            Auras = new Dictionary<string, AuraDefinition>(),
            Enchantments = new Dictionary<string, EnchantmentData>()
        };
    }

    private static GameDataRepository CloneRepoWithExtras(GameDataRepository baseRepo, IEnumerable<CardTemplate> extras)
    {
        var cards = new Dictionary<string, CardTemplate>(baseRepo.CardsById, StringComparer.OrdinalIgnoreCase);
        foreach (var c in extras) cards[c.Id] = c;
        return new GameDataRepository(
            baseRepo.SourceZipPath,
            baseRepo.GameMode,
            cards,
            new Dictionary<string, MonsterTemplate>(baseRepo.MonstersById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<int, LevelUpTemplate>(baseRepo.LevelUps),
            new Dictionary<string, string>(baseRepo.TooltipGlossary, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(baseRepo.ObservedRuleTypes, StringComparer.Ordinal),
            new HashSet<string>(baseRepo.UnknownRuleTypes, StringComparer.Ordinal),
            baseRepo.Localization,
            baseRepo.SourceDataPath);
    }

    private static void WriteAuditReport(
        int cardCount,
        List<AuditRecord> records,
        List<string> e2eFindings,
        ConcurrentBag<string> unknownRuleHits)
    {
        var dir = Path.GetDirectoryName(AuditReportPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var summary = new
        {
            generatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            shard = "ShardA(Vanessa,Pygmalien,Dooley,Mak)",
            totalCardsAudited = cardCount,
            totalChecks = records.Count,
            passedCount = records.Count(r => r.Status == "Pass"),
            intentionalZeroCount = records.Count(r => r.Status == "IntentionalZero"),
            knownEngineBugCount = records.Count(r => r.Status == "KnownEngineBug"),
            unexplainedNoOpCount = records.Count(r => r.Status == "UnexplainedNoOp"),
            unknownRuleHitCount = unknownRuleHits.Count,
            e2eFindingsCount = e2eFindings.Count,
            e2eFindings,
            knownEngineBugs = records.Where(r => r.Status == "KnownEngineBug").ToList(),
            unexplainedNoOps = records.Where(r => r.Status == "UnexplainedNoOp").ToList()
        };

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AuditReportPath, json);
    }
}
