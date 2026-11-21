using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Dataset.Simulation;

/// <summary>
/// 商店在售商品的轻量候选描述，避免未购买前反复创建 CardInstance 产生额外 GC 分配。
/// </summary>
public readonly record struct ShopOffer(CardTemplate Template, CardTier Tier, int Price, bool IsSkill);

/// <summary>
/// Hour 3 PvE 野怪遭遇候选项。
/// </summary>
public sealed class MonsterCandidate
{
    public required CardTemplate Encounter { get; init; }
    public required MonsterTemplate Monster { get; init; }
    public required PlayerState MonsterState { get; init; }
    public int Level { get; init; }
    public int HealthMax { get; init; }
    public int GoldReward { get; init; }
    public int XpReward { get; init; }
    public IReadOnlyList<(CardTemplate Template, CardTier Tier, string? Enchantment)> LootItems { get; init; } = Array.Empty<(CardTemplate, CardTier, string?)>();
    public IReadOnlyList<(CardTemplate Template, CardTier Tier)> LootSkills { get; init; } = Array.Empty<(CardTemplate, CardTier)>();
}

/// <summary>
/// 升级奖励多选一类型。
/// </summary>
public enum LevelUpRewardKind
{
    MaxHealthBoost,
    GoldAndIncome,
    PermanentPowerBuff,
    SkillChoice,
    ItemTierUpgrade
}

/// <summary>
/// 8 种人类行为画像智能决策 Bot 与棋盘站位重排器。
/// 完整区分 TempoAggro、GreedyEco、HardForcer、FlexPivot、Casual、CluelessNewbie、MessyHybrid 与 GreedyBust 的购牌、选商、刷店、升阶、打野、转型与站位策略。
/// </summary>
public sealed class ArchetypeBot
{
    private static readonly string[] CoreDamageAttrs = ["DamageAmount", "BurnApplyAmount", "PoisonApplyAmount", "ShieldApplyAmount", "HealAmount", "RegenApplyAmount"];

    /// <summary>
    /// 初始化智能决策 Bot 实例（无状态线程安全）。
    /// </summary>
    public ArchetypeBot()
    {
    }

    /// <summary>
    /// 从 3 位候选商人中择优或盲选进入（HardForcer/FlexPivot/TempoAggro/GreedyEco 优先匹配核心 Tag 或大件尺寸定向商；萌新/四不像随机盲选）。
    /// </summary>
    public MerchantSpec ChooseMerchant(
        RunParticipantState participant,
        IReadOnlyList<MerchantSpec> candidates,
        SimulationRepoIndex index)
    {
        if (candidates.Count == 0) return new MerchantSpec("General Merchant", null, null);
        if (candidates.Count == 1) return candidates[0];

        var persona = participant.Persona;
        var rng = participant.Rng;

        if (persona is PersonaType.CluelessNewbie or PersonaType.MessyHybrid)
            return candidates[rng.Next(candidates.Count)];

        if (persona == PersonaType.Casual && rng.NextDouble() < 0.45)
            return candidates[rng.Next(candidates.Count)];

        var ps = participant.PlayerState;
        var arch = participant.Archetype;
        var ownedNames = new HashSet<string>(
            EnumerateOwnedItems(ps).Select(c => c.Template.InternalName),
            StringComparer.OrdinalIgnoreCase);

        MerchantSpec best = candidates[0];
        double bestScore = double.NegativeInfinity;

        foreach (var cand in candidates)
        {
            double score = 5.0;
            if (!string.IsNullOrEmpty(cand.FilterTag))
            {
                if (arch.PreferredTags.TryGetValue(cand.FilterTag, out var tw))
                    score += tw * 26.0;
                if (arch.PreferredHiddenTags.TryGetValue(cand.FilterTag, out var hw))
                    score += hw * 22.0;

                // 若核心物品还未凑齐或仍可升阶且命中该 Tag，赋予高权重进店追核
                foreach (var coreName in arch.CoreItemNames)
                {
                    if (index.TryGetCardByInternalName(coreName, out var tpl) && tpl != null &&
                        (tpl.Tags.Contains(cand.FilterTag, StringComparer.OrdinalIgnoreCase) ||
                         tpl.HiddenTags.Contains(cand.FilterTag, StringComparer.OrdinalIgnoreCase)))
                    {
                        bool owned = ownedNames.Contains(coreName);
                        score += owned ? 22.0 : 48.0;
                    }
                }

                if (persona == PersonaType.TempoAggro && ps.Day <= 5 && cand.FilterTag is "Weapon" or "Burn" or "Shield" or "Poison")
                    score += 24.0;
                if (persona is PersonaType.GreedyEco or PersonaType.GreedyBust && ps.Day <= 6 && cand.FilterTag is "Property" or "Value" or "Tool")
                    score += 28.0;
            }
            else if (cand.FilterSize.HasValue)
            {
                foreach (var coreName in arch.CoreItemNames)
                {
                    if (index.TryGetCardByInternalName(coreName, out var tpl) && tpl != null && tpl.CardSize == cand.FilterSize.Value)
                    {
                        bool owned = ownedNames.Contains(coreName);
                        score += owned ? 26.0 : 58.0;
                    }
                }
                foreach (var supName in arch.SupportItemNames)
                {
                    if (index.TryGetCardByInternalName(supName, out var tpl) && tpl != null && tpl.CardSize == cand.FilterSize.Value)
                        score += 14.0;
                }
            }
            else
            {
                // 通用商人保底适中分
                score += 12.0;
            }

            if (persona == PersonaType.HardForcer)
                score *= 1.35;

            if (score > bestScore)
            {
                bestScore = score;
                best = cand;
            }
        }

        return best;
    }

    /// <summary>
    /// 评估单张物品模板在当前英雄、流派、画像与天数下的价值评分。
    /// </summary>
    public double ScoreItem(
        CardTemplate template,
        CardTier tier,
        RunParticipantState participant,
        CardMeta? precomputedMeta = null)
        => ScoreItemCore(template, tier, participant, precomputedMeta, skipDuplicateAndWeaponCheck: false);

    private double ScoreItemCore(
        CardTemplate template,
        CardTier tier,
        RunParticipantState participant,
        CardMeta? precomputedMeta,
        bool skipDuplicateAndWeaponCheck)
    {
        var ps = participant.PlayerState;
        var arch = participant.Archetype;
        var persona = participant.Persona;
        int day = Math.Max(1, ps.Day);
        var meta = precomputedMeta ?? CardMeta.Build(template);
        int width = Math.Max(1, template.CardSize.SlotCount());

        double score = GetTierBaseScore(tier);

        // 1. 流派核心/配套锚点评分（2~3 格核心大件按占用格数放大总分，避免被 1 格小件性价比公式挤出）
        bool isCore = ContainsIgnoreCase(arch.CoreItemNames, template.InternalName);
        bool isSupport = !isCore && ContainsIgnoreCase(arch.SupportItemNames, template.InternalName);

        double coreWeight = persona switch
        {
            PersonaType.HardForcer => 175.0,
            PersonaType.FlexPivot => 120.0,
            PersonaType.GreedyEco => day >= 6 ? 125.0 : 80.0,
            PersonaType.GreedyBust => day >= 7 ? 115.0 : 35.0,
            PersonaType.TempoAggro => 95.0,
            PersonaType.Casual => 36.0,
            PersonaType.MessyHybrid => 28.0,
            PersonaType.CluelessNewbie => 18.0,
            _ => 90.0
        };
        double supportWeight = persona switch
        {
            PersonaType.HardForcer => 75.0,
            PersonaType.Casual => 20.0,
            PersonaType.MessyHybrid => 18.0,
            PersonaType.CluelessNewbie => 10.0,
            _ => 52.0
        };

        double sizeCoreMult = width >= 2 && (isCore || isSupport) ? (0.85 + 0.55 * width) : 1.0;
        if (isCore) score += coreWeight * sizeCoreMult;
        else if (isSupport) score += supportWeight * sizeCoreMult;

        // 2. Tag / HiddenTag / Trigger 协同评分
        double tagMult = persona switch
        {
            PersonaType.HardForcer => 1.45,
            PersonaType.Casual => 0.40,
            PersonaType.MessyHybrid => 0.35,
            PersonaType.CluelessNewbie => 0.15,
            PersonaType.GreedyBust => day <= 6 ? 0.55 : 1.05,
            _ => 1.0
        };

        double synergy = 0;
        foreach (var tag in template.Tags)
        {
            if (arch.PreferredTags.TryGetValue(tag, out var w))
                synergy += w * 16.0;
        }
        foreach (var htag in template.HiddenTags)
        {
            if (arch.PreferredHiddenTags.TryGetValue(htag, out var w))
                synergy += w * 13.0;
        }
        foreach (var trig in meta.TriggerTypes)
        {
            if (arch.PreferredTriggers.TryGetValue(trig, out var w))
                synergy += w * 12.0;
        }

        if (day <= 4 && arch.EarlyFocusTags.Count > 0)
        {
            foreach (var ef in arch.EarlyFocusTags)
            {
                if (meta.HasAnyTag(ef))
                    synergy += 16.0;
            }
        }
        if (day >= 8 && !string.IsNullOrEmpty(arch.EndgamePowerTag) && meta.HasAnyTag(arch.EndgamePowerTag))
        {
            synergy += 26.0 * (width >= 2 && isCore ? 1.35 : 1.0);
        }

        score += synergy * tagMult;

        // 3. 基础实战面板估算（含非本英雄折减与画像分化策略）
        bool isHeroExclusive = ContainsIgnoreCase(template.Heroes, participant.Hero);
        double panelPower = meta.EstimateCombatPower(tier);
        if (!isHeroExclusive && !isCore && !isSupport)
            panelPower *= 0.55;

        double slotEfficiency = (isCore || isSupport)
            ? panelPower * (0.9 + 0.25 * width)
            : panelPower / Math.Pow(width, 0.65);

        if (!isHeroExclusive && !isCore && !isSupport && synergy < 25.0
            && persona is not (PersonaType.CluelessNewbie or PersonaType.MessyHybrid))
        {
            score -= 35.0;
        }

        if (persona == PersonaType.TempoAggro)
        {
            double earlyMult = day <= 5 ? 1.55 : 1.05;
            score += slotEfficiency * 1.15 * earlyMult;
            if (day <= 5 && (meta.IsWeapon || meta.IsBurn || meta.IsShield || meta.IsPoison))
                score += 24.0;
            if (day <= 5 && meta.IsPureEconomy && !isCore)
                score -= 18.0;
        }
        else if (persona == PersonaType.GreedyEco)
        {
            score += slotEfficiency * 0.85;
            if (day <= 6 && (meta.IsEconomy || meta.IsProperty || meta.IsValueScaler))
                score += 48.0;
            if (day >= 7 && tier >= CardTier.Gold)
                score += 36.0;
        }
        else if (persona == PersonaType.GreedyBust)
        {
            if (day <= 6)
            {
                // 前期贪心崩盘哥疯狂偏爱经济/地产，甚至排斥即时战力卡导致前期裸奔挨打
                if (meta.IsEconomy || meta.IsProperty || meta.IsValueScaler)
                    score += 85.0;
                else if (meta.IsWeapon || meta.IsBurn || meta.IsShield || meta.IsPoison)
                    score -= 30.0;
                score += slotEfficiency * 0.45;
            }
            else
            {
                score += slotEfficiency * 1.05;
                if (tier >= CardTier.Gold) score += 45.0;
            }
        }
        else if (persona == PersonaType.HardForcer)
        {
            score += slotEfficiency * 0.75;
            if (!isCore && !isSupport && synergy < 8.0)
                score -= 16.0;
        }
        else if (persona == PersonaType.Casual)
        {
            score += panelPower * 1.10;
            score += (participant.Rng.NextDouble() * 2.0 - 1.0) * 28.0;
        }
        else if (persona == PersonaType.MessyHybrid)
        {
            // 四不像杂拼：调低泛用白板面板权重并提高噪声，避免靠中立重炮混进高分
            score += panelPower * 0.55;
            bool boardHasBurn = ps.Hand.Cards.Any(c => c.HasHiddenTag("Burn"));
            bool boardHasPoison = ps.Hand.Cards.Any(c => c.HasHiddenTag("Poison"));
            bool boardHasShield = ps.Hand.Cards.Any(c => c.HasHiddenTag("Shield"));
            bool boardHasWeapon = ps.Hand.Cards.Any(c => c.HasTag("Weapon"));
            if ((meta.IsBurn && !boardHasBurn) || (meta.IsPoison && !boardHasPoison) || (meta.IsShield && !boardHasShield) || (meta.IsWeapon && !boardHasWeapon))
                score += 18.0;
            score += (participant.Rng.NextDouble() * 2.0 - 1.0) * 34.0;
        }
        else if (persona == PersonaType.CluelessNewbie)
        {
            // 萌新唐人：巨大随机噪声，看不懂协同与格数性价比
            score += panelPower * 0.70;
            score += (participant.Rng.NextDouble() * 2.0 - 1.0) * 65.0;
        }
        else
        {
            score += slotEfficiency * 1.0;
        }

        // 4. 已有同名卡二合一升阶、重复废牌惩罚与单武器核心意识（评估已有实例自身时跳过，由背包阶段统一去重）
        if (!skipDuplicateAndWeaponCheck)
        {
            CardInstance? sameTierUpgradable = null;
            CardInstance? highestExisting = null;
            foreach (var owned in EnumerateOwnedItems(ps))
            {
                if (!string.Equals(owned.Template.Id, template.Id, StringComparison.OrdinalIgnoreCase)) continue;
                if (highestExisting == null || owned.Tier > highestExisting.Tier)
                    highestExisting = owned;
                if (owned.Tier == tier && owned.CanUpgrade())
                    sameTierUpgradable = owned;
            }

            bool isLowDiscipline = persona is PersonaType.CluelessNewbie or PersonaType.MessyHybrid;

            // 萌新 70% 错过二合一升阶识别
            if (sameTierUpgradable != null && persona == PersonaType.CluelessNewbie && participant.Rng.NextDouble() < 0.70)
                sameTierUpgradable = null;

            if (sameTierUpgradable != null)
            {
                double mergeBonus = persona switch
                {
                    PersonaType.HardForcer => 115.0,
                    PersonaType.Casual => 30.0,
                    PersonaType.CluelessNewbie => 20.0,
                    _ => 84.0
                };
                score += mergeBonus + (int)tier * 20.0;
            }
            else if (highestExisting != null)
            {
                if (tier > highestExisting.Tier)
                {
                    // 高阶同名卡不再按全分计价，改为「买新卖旧升级替补」净提升计分
                    if (persona != PersonaType.CluelessNewbie)
                        score = ((int)tier - (int)highestExisting.Tier) * 38.0;
                }
                else
                {
                    if (persona == PersonaType.CluelessNewbie)
                        score -= 25.0;
                    else if (persona == PersonaType.MessyHybrid)
                        score -= width >= 2 ? 140.0 : 70.0;
                    else
                        score -= width >= 2 ? 350.0 : 220.0;
                }
            }

            // 单武器核心意识（Single_Weapon 流派或已持有 Crow's Nest）
            if (!isLowDiscipline && IsSingleWeaponBuild(ps, arch) && meta.IsWeapon)
            {
                bool hasOtherWeapon = ps.Hand.Cards.Any(c =>
                    (c.HasTag("Weapon") || c.HasHiddenTag("Damage")) &&
                    !string.Equals(c.Template.Id, template.Id, StringComparison.OrdinalIgnoreCase));

                if (hasOtherWeapon)
                    score -= 90.0;
                else
                    score += 75.0;
            }
        }

        // 5. 若当前手牌未填满空闲槽位，鼓励尽快填格（萌新经常无意识留空）
        if (persona != PersonaType.CluelessNewbie && (ps.Hand.Cards.Count == 0 || CountFreeUnlockedSlots(ps.Hand) >= width))
        {
            double fillBonus = persona == PersonaType.TempoAggro ? 24.0 : 12.0;
            score += fillBonus;
        }

        // 6. 非萌新在商店买牌评估时，对 1 格且带有“卖出触发成长（HasCardSoldTrigger）”的战利品小件给予额外估值
        if (!skipDuplicateAndWeaponCheck && persona != PersonaType.CluelessNewbie && width == 1 && meta.HasCardSoldTrigger)
        {
            double soldTriggerBonus = persona is PersonaType.GreedyEco or PersonaType.HardForcer or PersonaType.FlexPivot
                ? 28.0
                : 18.0;
            score += soldTriggerBonus;
        }

        // 7. 若加载了巴扎罗盘分天真实获取概率表，轻度引导选牌贴合游戏当日真实出现概率曲线
        if (CompassAvailabilityRegistry.TryGetAvailability(participant.Hero, day, template.Id, out double compassAvail)
            && compassAvail > 0)
        {
            score += Math.Min(12.0, Math.Sqrt(compassAvail) * 24.0);
        }

        return score;
    }

    /// <summary>
    /// 评估单个技能在当前流派与画像下的价值评分。
    /// </summary>
    public double ScoreSkill(CardTemplate template, CardTier tier, RunParticipantState participant, CardMeta? precomputedMeta = null)
    {
        var arch = participant.Archetype;
        var persona = participant.Persona;
        _ = precomputedMeta ?? CardMeta.Build(template);

        double skillAlignMult = persona switch
        {
            PersonaType.HardForcer => 1.35,
            PersonaType.Casual => 0.45,
            PersonaType.MessyHybrid => 0.35,
            PersonaType.CluelessNewbie => 0.15,
            _ => 1.0
        };

        double score = GetTierBaseScore(tier) * 0.9;
        if (ContainsIgnoreCase(arch.CoreSkillNames, template.InternalName))
            score += 95.0 * skillAlignMult;

        foreach (var tag in template.Tags)
        {
            if (arch.PreferredTags.TryGetValue(tag, out var w))
                score += w * 14.0 * skillAlignMult;
        }
        foreach (var htag in template.HiddenTags)
        {
            if (arch.PreferredHiddenTags.TryGetValue(htag, out var w))
                score += w * 12.0 * skillAlignMult;
        }

        // 检查与场上物品标签的联动覆盖度
        int handTagMatches = 0;
        foreach (var c in participant.PlayerState.Hand.Cards)
        {
            foreach (var t in template.Tags)
            {
                if (c.HasTag(t) || c.HasHiddenTag(t))
                {
                    handTagMatches++;
                    break;
                }
            }
        }
        score += handTagMatches * 10.0 * skillAlignMult;

        if (persona == PersonaType.Casual)
            score += (participant.Rng.NextDouble() * 2.0 - 1.0) * 20.0;
        else if (persona == PersonaType.CluelessNewbie)
            score += (participant.Rng.NextDouble() * 2.0 - 1.0) * 55.0;

        return score;
    }

    /// <summary>
    /// 评估当前是否应花费金币刷新商店。
    /// </summary>
    public bool ShouldRerollShop(
        RunParticipantState participant,
        IReadOnlyList<ShopOffer> offers,
        int rerollCost,
        int rerollsUsedThisHour,
        SimulationRepoIndex index)
    {
        var ps = participant.PlayerState;
        int day = ps.Day;
        int reserve = GetTargetGoldReserve(participant);

        if (ps.Gold - rerollCost < reserve)
            return false;

        int maxRerolls = participant.Persona switch
        {
            PersonaType.TempoAggro => day <= 5 ? 1 : 2,
            PersonaType.GreedyEco => day <= 6 ? 0 : 4,
            PersonaType.GreedyBust => day <= 6 ? 0 : 5,
            PersonaType.HardForcer => day <= 3 ? 2 : 5,
            PersonaType.FlexPivot => day <= 3 ? 1 : 3,
            PersonaType.Casual => 0,
            PersonaType.MessyHybrid => 0,
            PersonaType.CluelessNewbie => 0,
            _ => 1
        };

        if (rerollsUsedThisHour >= maxRerolls)
            return false;

        // 若货架上已有明显优质且买得起的目标，则先买再看
        foreach (var offer in offers)
        {
            if (offer.Price <= ps.Gold)
            {
                double s = offer.IsSkill
                    ? ScoreSkill(offer.Template, offer.Tier, participant, index.GetMeta(offer.Template))
                    : ScoreItem(offer.Template, offer.Tier, participant, index.GetMeta(offer.Template));
                if (s >= 75.0) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 从当前商店货架中选出最值得购买（或二合一升阶/替换）的商品下标；无合适目标时返回 -1。
    /// </summary>
    public int ChooseBestOfferIndex(
        RunParticipantState participant,
        IReadOnlyList<ShopOffer> offers,
        GameDataRepository repo,
        SimulationRepoIndex index)
    {
        var ps = participant.PlayerState;
        var persona = participant.Persona;
        int reserve = GetTargetGoldReserve(participant);
        int freeSlots = CountFreeUnlockedSlots(ps.Hand);

        int bestIdx = -1;
        double bestNetGain = double.NegativeInfinity;

        for (int i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];
            if (offer.IsSkill)
            {
                if (ps.Gold < offer.Price) continue;
                bool hasSkill = ps.Skills.Any(s => string.Equals(s.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase));
                var existingSameSkill = ps.Skills.FirstOrDefault(s =>
                    string.Equals(s.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase) &&
                    s.Tier == offer.Tier && s.CanUpgrade());

                if (hasSkill && existingSameSkill == null) continue;

                double skillScore = ScoreSkill(offer.Template, offer.Tier, participant, index.GetMeta(offer.Template));
                if (existingSameSkill != null) skillScore += 45.0;

                bool breakReserve = skillScore >= 110.0 && !(persona == PersonaType.GreedyBust && ps.Day <= 6);
                if (!breakReserve && ps.Gold - offer.Price < reserve) continue;

                double net = skillScore - offer.Price * 1.4;
                if (net > 18.0 && net > bestNetGain)
                {
                    bestNetGain = net;
                    bestIdx = i;
                }
                continue;
            }

            var meta = index.GetMeta(offer.Template);
            double candScore = ScoreItem(offer.Template, offer.Tier, participant, meta);
            int width = Math.Max(1, offer.Template.CardSize.SlotCount());
            bool isConsumableToStash = persona != PersonaType.CluelessNewbie
                && ps.Stash.Cards.Count < 8
                && IsSellConsumableCard(offer.Template, participant, index);

            // 是否可直接二合一升阶（萌新 70% 漏掉）
            bool isMerge = EnumerateOwnedItems(ps).Any(c =>
                string.Equals(c.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase) &&
                c.Tier == offer.Tier && c.CanUpgrade());
            if (isMerge && persona == PersonaType.CluelessNewbie && participant.Rng.NextDouble() < 0.70)
                isMerge = false;

            // 同名非合并检查：高阶同名卡按净替补换下旧卡评估；同阶/低阶同名卡禁止买入
            var handDup = !isMerge && persona != PersonaType.CluelessNewbie
                ? ps.Hand.Cards
                    .Where(c => string.Equals(c.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.Tier)
                    .FirstOrDefault()
                : null;
            if (handDup != null && handDup.Tier >= offer.Tier && (width >= 2 || persona != PersonaType.MessyHybrid))
                continue;

            bool isUpgradeReplaceInHand = handDup != null && handDup.Tier < offer.Tier;

            // 评估可释放/可卖出换位空间（纯卖出消耗品直接进 Stash，不牺牲手牌战力；高阶同名替补按卖旧换新计算）
            double displacedScore = 0;
            int potentialSellGold = 0;
            bool fitsDirectly = isMerge || isConsumableToStash || (isUpgradeReplaceInHand && freeSlots + handDup!.SlotWidth >= width) || freeSlots >= width;

            if (isUpgradeReplaceInHand)
            {
                potentialSellGold += handDup!.GetSellPrice(repo.GameMode.StandardPrices);
            }

            if (!fitsDirectly)
            {
                if (persona == PersonaType.CluelessNewbie && participant.Rng.NextDouble() < 0.55)
                    continue; // 萌新常因不会腾格子而放弃买卡

                var rankedHand = ps.Hand.Cards
                    .Where(c => !ReferenceEquals(c, handDup))
                    .Select(c => (Card: c, Score: ScoreExistingCard(c, participant, index)))
                    .OrderBy(x => x.Score)
                    .ToList();

                int freed = freeSlots + (handDup != null ? handDup.SlotWidth : 0);
                foreach (var item in rankedHand)
                {
                    if (freed >= width) break;
                    displacedScore += item.Score;
                    potentialSellGold += item.Card.GetSellPrice(repo.GameMode.StandardPrices);
                    freed += item.Card.SlotWidth;
                }

                if (freed < width) continue;
            }

            // 如果金币略缺，考虑额外卖 Stash 卡凑钱（仅限非低段画像且是高价值目标）
            int stashSellGold = 0;
            if (ps.Gold + potentialSellGold < offer.Price
                && !isConsumableToStash
                && persona is not (PersonaType.Casual or PersonaType.CluelessNewbie or PersonaType.MessyHybrid)
                && candScore >= 85.0)
            {
                foreach (var sc in ps.Stash.Cards)
                    stashSellGold += sc.GetSellPrice(repo.GameMode.StandardPrices);
            }

            if (ps.Gold + potentialSellGold + stashSellGold < offer.Price) continue;

            bool allowBreakReserve = isMerge || candScore >= 95.0 || ps.Hand.Cards.Count < 2;
            if (persona == PersonaType.GreedyBust && ps.Day <= 6 && !meta.IsEconomy && !meta.IsProperty && !meta.IsValueScaler)
                allowBreakReserve = false; // 贪心崩盘哥前 6 天绝不动用预留金买纯战力卡

            if (!allowBreakReserve && ps.Gold + potentialSellGold - offer.Price < reserve) continue;

            double replaceThreshold = persona switch
            {
                PersonaType.HardForcer => 6.0,
                PersonaType.TempoAggro => 8.0,
                PersonaType.FlexPivot => 8.0,
                PersonaType.GreedyEco => 12.0,
                PersonaType.GreedyBust => ps.Day <= 6 ? 20.0 : 8.0,
                PersonaType.Casual => 20.0,
                PersonaType.MessyHybrid => 45.0, // 四不像极不舍得卖低阶旧牌
                PersonaType.CluelessNewbie => 25.0,
                _ => 10.0
            };

            double netGain = isMerge
                ? candScore - offer.Price * 1.1
                : candScore - displacedScore - offer.Price * 1.15 - (fitsDirectly ? 0 : replaceThreshold);

            if (fitsDirectly && (freeSlots > 0 || isConsumableToStash) && candScore >= 10.0)
                netGain = Math.Max(netGain, candScore - offer.Price * 0.8);

            if (netGain > 4.0 && netGain > bestNetGain)
            {
                bestNetGain = netGain;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    /// <summary>
    /// 执行购买、二合一升阶或腾位换装动作。
    /// </summary>
    public bool TryExecuteBuyOrMerge(
        RunParticipantState participant,
        ShopOffer offer,
        GameDataRepository repo,
        SimulationRepoIndex index)
    {
        var ps = participant.PlayerState;
        if (offer.IsSkill)
        {
            if (ps.Gold < offer.Price) return false;
            var mergeSkill = ps.Skills.FirstOrDefault(s =>
                string.Equals(s.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase) &&
                s.Tier == offer.Tier && s.CanUpgrade());

            ps.Gold -= offer.Price;
            if (mergeSkill != null)
            {
                mergeSkill.TryUpgrade();
                FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardUpgraded", mergeSkill, repo, participant.Rng, index);
                return true;
            }

            var skillCard = new CardInstance(offer.Template, offer.Tier);
            ps.Skills.Add(skillCard);
            FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardPurchased", skillCard, repo, participant.Rng, index);
            return true;
        }

        // 1. 优先检查手牌与背包的同名同品质二合一升阶（萌新 70% 概率错过二合一）
        var mergeTarget = ps.Hand.Cards.FirstOrDefault(c =>
            string.Equals(c.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase) &&
            c.Tier == offer.Tier && c.CanUpgrade())
            ?? ps.Stash.Cards.FirstOrDefault(c =>
            string.Equals(c.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase) &&
            c.Tier == offer.Tier && c.CanUpgrade());

        if (mergeTarget != null && ps.Gold >= offer.Price)
        {
            bool missedMerge = participant.Persona == PersonaType.CluelessNewbie && participant.Rng.NextDouble() < 0.70;
            if (!missedMerge)
            {
                ps.Gold -= offer.Price;
                mergeTarget.TryUpgrade();
                FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardUpgraded", mergeTarget, repo, participant.Rng, index);
                return true;
            }
        }

        var newCard = new CardInstance(offer.Template, offer.Tier);
        int width = newCard.SlotWidth;

        // 1.5 纯卖出消耗品直接存入 Stash 等待消化卖出，避免挤占 Hand 主力或浪费上阵格
        if (participant.Persona != PersonaType.CluelessNewbie
            && ps.Gold >= offer.Price
            && ps.Stash.Cards.Count < 8
            && IsSellConsumableCard(offer.Template, participant, index)
            && ps.Stash.TryAddAuto(newCard))
        {
            ps.Gold -= offer.Price;
            FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardPurchased", newCard, repo, participant.Rng, index);
            return true;
        }

        // 2. 若手牌有空余解锁格但缺少量金币，可先卖 Stash 闲置卡
        if (ps.Gold < offer.Price && ps.Stash.Cards.Count > 0)
        {
            SellStashCardsUntilGold(participant, offer.Price, repo);
        }

        // 2.5 非萌新在直接放手牌前检查同名卡：存在低阶则卖旧替补，存在同/高阶则拒绝双摆
        if (participant.Persona != PersonaType.CluelessNewbie)
        {
            var sameInHand = ps.Hand.Cards
                .Where(c => string.Equals(c.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Tier)
                .FirstOrDefault();
            if (sameInHand != null)
            {
                if (sameInHand.Tier < newCard.Tier)
                {
                    int oldSellGold = sameInHand.GetSellPrice(repo.GameMode.StandardPrices);
                    if (ps.Gold + oldSellGold >= offer.Price && CountFreeUnlockedSlots(ps.Hand) + sameInHand.SlotWidth >= width)
                    {
                        SellCardFromHand(ps, sameInHand, repo, participant.Rng);
                        if (ps.Gold >= offer.Price && PlaceIntoHandSafe(ps.Hand, newCard))
                        {
                            ps.Gold -= offer.Price;
                            FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardPurchased", newCard, repo, participant.Rng, index);
                            return true;
                        }
                    }
                    return false;
                }
                else if (width >= 2 || participant.Persona != PersonaType.MessyHybrid)
                {
                    return false;
                }
            }
        }

        if (CountFreeUnlockedSlots(ps.Hand) >= width && ps.Gold >= offer.Price)
        {
            if (PlaceIntoHandSafe(ps.Hand, newCard))
            {
                ps.Gold -= offer.Price;
                FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardPurchased", newCard, repo, participant.Rng, index);
                return true;
            }
        }

        // 3. 腾退手牌最弱物品以换取空间与卖牌金币（若有同 ID 低阶卡，优先先卖掉低阶卡防双占位）
        var rankedHand = ps.Hand.Cards
            .Select(c => (
                Card: c,
                IsLowerDup: string.Equals(c.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase) && c.Tier < offer.Tier,
                Score: ScoreExistingCard(c, participant, index)))
            .OrderByDescending(x => x.IsLowerDup ? 1 : 0)
            .ThenBy(x => x.Score)
            .ToList();

        int freed = CountFreeUnlockedSlots(ps.Hand);
        var toRemove = new List<CardInstance>();
        int projectedSell = 0;

        foreach (var entry in rankedHand)
        {
            if (freed >= width && ps.Gold + projectedSell >= offer.Price)
                break;

            toRemove.Add(entry.Card);
            freed += entry.Card.SlotWidth;
            projectedSell += entry.Card.GetSellPrice(repo.GameMode.StandardPrices);
        }

        if (freed < width || ps.Gold + projectedSell < offer.Price)
            return false;

        foreach (var old in toRemove)
        {
            bool keepInStash = participant.Persona == PersonaType.HardForcer
                && !string.Equals(old.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase)
                && ps.Gold >= offer.Price
                && ps.Stash.Cards.Count < 4
                && ContainsIgnoreCase(participant.Archetype.CoreItemNames, old.Template.InternalName)
                && ps.Stash.TryAddAuto(old);

            if (keepInStash)
            {
                ps.Hand.Remove(old);
            }
            else
            {
                SellCardFromHand(ps, old, repo, participant.Rng);
            }
        }

        if (ps.Gold < offer.Price) return false;
        if (!PlaceIntoHandSafe(ps.Hand, newCard))
        {
            if (participant.Persona != PersonaType.CluelessNewbie
                && ps.Hand.Cards.Any(c => string.Equals(c.Template.Id, offer.Template.Id, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (!ps.Stash.TryAddAuto(newCard))
                return false;
            ps.Gold -= offer.Price;
            FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardPurchased", newCard, repo, participant.Rng, index);
            return true;
        }

        ps.Gold -= offer.Price;
        FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardPurchased", newCard, repo, participant.Rng, index);
        return true;
    }

    /// <summary>
    /// 从 Hour 3 低/中/高 3 个野怪候选中按当前战力与画像挑选最合适的一位。
    /// </summary>
    public int SelectMonsterIndex(RunParticipantState participant, IReadOnlyList<MonsterCandidate> candidates, SimulationRepoIndex index)
    {
        if (candidates.Count <= 1) return 0;

        // 萌新随机盲选野怪（经常选到高难度翻车）
        if (participant.Persona == PersonaType.CluelessNewbie)
            return participant.Rng.Next(candidates.Count);

        var ps = participant.PlayerState;
        double myPower = EstimateBoardCombatStrength(ps, index);
        double ratioToMed = candidates.Count >= 2 && candidates[1].HealthMax > 0
            ? myPower / candidates[1].HealthMax
            : 1.0;

        int bestIdx = 0;
        double bestScore = double.NegativeInfinity;

        for (int i = 0; i < candidates.Count; i++)
        {
            var m = candidates[i];
            double lootBonus = 0;
            foreach (var (lt, ltier, _) in m.LootItems)
                lootBonus = Math.Max(lootBonus, ScoreItem(lt, ltier, participant, index.GetMeta(lt)) * 0.25);
            foreach (var (st, stier) in m.LootSkills)
                lootBonus = Math.Max(lootBonus, ScoreSkill(st, stier, participant, index.GetMeta(st)) * 0.30);

            double difficultyRisk = m.HealthMax / Math.Max(80.0, myPower * 1.15);
            double personaRiskTolerance = participant.Persona switch
            {
                PersonaType.TempoAggro => 1.35,
                PersonaType.HardForcer => 1.15,
                PersonaType.FlexPivot => 1.20,
                PersonaType.GreedyEco => ps.Day <= 3 ? 0.78 : 1.10,
                PersonaType.GreedyBust => ps.Day <= 6 ? 0.72 : 1.10,
                PersonaType.MessyHybrid => 1.05,
                PersonaType.Casual => 0.90,
                _ => 1.0
            };

            double expectedReward = m.GoldReward * 4.0 + m.XpReward * 6.0 + lootBonus;
            double riskPenalty = difficultyRisk > personaRiskTolerance
                ? (difficultyRisk - personaRiskTolerance) * 65.0
                : 0.0;

            double total = expectedReward - riskPenalty;
            if (participant.Persona is PersonaType.Casual or PersonaType.MessyHybrid)
                total += (participant.Rng.NextDouble() * 2.0 - 1.0) * 24.0;

            if (ratioToMed < 0.45 && i == 0 && participant.Persona != PersonaType.MessyHybrid)
                total += 25.0;

            if (total > bestScore)
            {
                bestScore = total;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    /// <summary>
    /// 按玩家画像选择升级多选一奖励。
    /// </summary>
    public LevelUpRewardKind ChooseLevelUpReward(RunParticipantState participant, IReadOnlyList<LevelUpRewardKind> choices)
    {
        if (choices.Count == 0) return LevelUpRewardKind.MaxHealthBoost;
        if (participant.Persona is PersonaType.Casual or PersonaType.CluelessNewbie or PersonaType.MessyHybrid)
            return choices[participant.Rng.Next(choices.Count)];

        return choices.OrderByDescending(c => (participant.Persona, c) switch
        {
            (PersonaType.TempoAggro, LevelUpRewardKind.PermanentPowerBuff) => 95,
            (PersonaType.TempoAggro, LevelUpRewardKind.SkillChoice) => 90,
            (PersonaType.TempoAggro, LevelUpRewardKind.ItemTierUpgrade) => 88,
            (PersonaType.TempoAggro, LevelUpRewardKind.MaxHealthBoost) => 75,
            (PersonaType.TempoAggro, LevelUpRewardKind.GoldAndIncome) => 55,

            (PersonaType.GreedyEco, LevelUpRewardKind.GoldAndIncome) => 98,
            (PersonaType.GreedyEco, LevelUpRewardKind.ItemTierUpgrade) => 88,
            (PersonaType.GreedyEco, LevelUpRewardKind.MaxHealthBoost) => 80,
            (PersonaType.GreedyEco, LevelUpRewardKind.SkillChoice) => 72,
            (PersonaType.GreedyEco, LevelUpRewardKind.PermanentPowerBuff) => 65,

            (PersonaType.GreedyBust, LevelUpRewardKind.GoldAndIncome) => 99,
            (PersonaType.GreedyBust, LevelUpRewardKind.MaxHealthBoost) => 82,
            (PersonaType.GreedyBust, LevelUpRewardKind.ItemTierUpgrade) => 75,
            (PersonaType.GreedyBust, LevelUpRewardKind.SkillChoice) => 68,
            (PersonaType.GreedyBust, LevelUpRewardKind.PermanentPowerBuff) => 60,

            (PersonaType.HardForcer, LevelUpRewardKind.ItemTierUpgrade) => 96,
            (PersonaType.HardForcer, LevelUpRewardKind.SkillChoice) => 92,
            (PersonaType.HardForcer, LevelUpRewardKind.GoldAndIncome) => 82,
            (PersonaType.HardForcer, LevelUpRewardKind.PermanentPowerBuff) => 78,
            (PersonaType.HardForcer, LevelUpRewardKind.MaxHealthBoost) => 62,

            (PersonaType.FlexPivot, LevelUpRewardKind.ItemTierUpgrade) => 94,
            (PersonaType.FlexPivot, LevelUpRewardKind.SkillChoice) => 90,
            (PersonaType.FlexPivot, LevelUpRewardKind.PermanentPowerBuff) => 84,
            (PersonaType.FlexPivot, LevelUpRewardKind.GoldAndIncome) => 78,
            (PersonaType.FlexPivot, LevelUpRewardKind.MaxHealthBoost) => 70,

            _ => 50
        }).First();
    }

    /// <summary>
    /// 在附魔台或 Lv10 / Fates 时，为手牌上最核心且合法的物品赋予契合且多样化的附魔。
    /// 严格保证所选附魔属于目标卡牌 Template.Enchantments 且属于 EnchantmentNames.All。
    /// </summary>
    public bool TryApplyBestEnchantment(
        RunParticipantState participant,
        SimulationRepoIndex index,
        IReadOnlyList<string>? offeredEnchantments = null,
        bool allowOverride = false)
    {
        var ps = participant.PlayerState;
        if (ps.Hand.Cards.Count == 0) return false;

        List<CardInstance> candidates;
        if (participant.Persona is PersonaType.CluelessNewbie or PersonaType.MessyHybrid)
        {
            candidates = ps.Hand.Cards
                .Where(c => c.Template.Enchantments != null && c.Template.Enchantments.Count > 0)
                .Where(c => allowOverride || string.IsNullOrEmpty(c.Enchantment))
                .OrderBy(_ => participant.Rng.Next())
                .ToList();
        }
        else
        {
            candidates = ps.Hand.Cards
                .Where(c => c.Template.Enchantments != null && c.Template.Enchantments.Count > 0)
                .Where(c => allowOverride || string.IsNullOrEmpty(c.Enchantment))
                .OrderByDescending(c => ScoreExistingCard(c, participant, index) + (int)c.Tier * 18.0)
                .ToList();
        }

        if (candidates.Count == 0 && !allowOverride)
            return false;

        foreach (var card in candidates)
        {
            var legalEnchs = card.Template.Enchantments!.Keys
                .Where(e => EnchantmentNames.All.Contains(e, StringComparer.Ordinal))
                .Where(e => offeredEnchantments == null || offeredEnchantments.Count == 0 || offeredEnchantments.Contains(e, StringComparer.Ordinal))
                .ToList();

            if (legalEnchs.Count == 0) continue;

            string bestEnch;
            if (participant.Persona is PersonaType.Casual or PersonaType.CluelessNewbie or PersonaType.MessyHybrid)
            {
                bestEnch = legalEnchs[participant.Rng.Next(legalEnchs.Count)];
            }
            else
            {
                bestEnch = legalEnchs
                    .OrderByDescending(e => ScoreEnchantmentForCard(e, card, ps, participant.Archetype, index.GetMeta(card.Template)))
                    .First();
            }

            if (bestEnch == card.Enchantment) continue;
            card.ApplyEnchantment(bestEnch, preventOverride: false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// FlexPivot 画像在 Day 3~6 依据当前持有的全部卡牌（含跨英雄野怪掉落、附魔结果、高阶卡）动态评估并切换流派蓝图。
    /// </summary>
    public bool TryPivotArchetype(
        RunParticipantState participant,
        IArchetypeCatalog catalog,
        SimulationRepoIndex index,
        IReadOnlyList<CardTemplate>? recentLootOrShop = null)
    {
        if (participant.Persona != PersonaType.FlexPivot) return false;
        int day = participant.PlayerState.Day;
        if (day < 3 || day > 6) return false;

        var heroArchetypes = catalog.GetByHero(participant.Hero);
        if (heroArchetypes.Count <= 1) return false;

        double EvaluateFit(ArchetypeDefinition arch)
        {
            double fit = 0;
            foreach (var c in participant.PlayerState.Hand.Cards)
            {
                double tierW = 1.0 + (int)c.Tier * 0.45 + (string.IsNullOrEmpty(c.Enchantment) ? 0 : 0.4);
                bool isCrossHero = !c.Template.Heroes.Contains(participant.Hero, StringComparer.OrdinalIgnoreCase)
                                && !c.Template.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase);
                if (isCrossHero) tierW *= 1.25;

                fit += ScoreCardAgainstArchetype(c.Template, arch, index.GetMeta(c.Template)) * tierW;
            }
            foreach (var c in participant.PlayerState.Stash.Cards)
                fit += ScoreCardAgainstArchetype(c.Template, arch, index.GetMeta(c.Template)) * 0.45;
            foreach (var s in participant.PlayerState.Skills)
                fit += ScoreCardAgainstArchetype(s.Template, arch, index.GetMeta(s.Template)) * 0.85;
            if (recentLootOrShop != null)
            {
                foreach (var tpl in recentLootOrShop)
                    fit += ScoreCardAgainstArchetype(tpl, arch, index.GetMeta(tpl)) * 0.40;
            }
            return fit;
        }

        double currentFit = EvaluateFit(participant.Archetype);
        ArchetypeDefinition? bestArch = null;
        double bestFit = currentFit;

        foreach (var cand in heroArchetypes)
        {
            if (cand.Id == participant.Archetype.Id) continue;
            double f = EvaluateFit(cand);
            if (f > bestFit)
            {
                bestFit = f;
                bestArch = cand;
            }
        }

        if (bestArch != null && bestFit >= currentFit * 1.20 + 25.0)
        {
            participant.Archetype = bestArch;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 赛前从 Hand ∪ Stash 中选出最佳组合并依据 PositioningRules 智能重排站位，
    /// 严格通过 BoardGrid 放置并保证零重叠、零越界且全部处于解锁槽位内。
    /// CluelessNewbie 跳过 0/1 背包优化并保留先到先得拼凑/留空与全随机站位；MessyHybrid 保留大量低阶旧卡与 75% 随机站位。
    /// </summary>
    public void OptimizeAndPositionBoard(RunParticipantState participant, SimulationRepoIndex index, GameDataRepository repo)
    {
        var ps = participant.PlayerState;
        int unlockedCount = ps.Hand.UnlockedCount;
        if (unlockedCount <= 0) return;

        // 萌新唐人：跳出精密背包优化，保留先到先得拼凑感；但 Day 3+ 只要拥有真实战斗卡，仍清理战棋上的无战斗代币/纯产牌工具
        if (participant.Persona == PersonaType.CluelessNewbie)
        {
            bool hasCombatCardAnywhere = ps.Hand.Cards.Any(c => !IsNonCombatBoardDeadWeight(c.Template, index))
                || ps.Stash.Cards.Any(c => !IsNonCombatBoardDeadWeight(c.Template, index));

            if (ps.Day >= 3 && hasCombatCardAnywhere)
            {
                foreach (var hc in ps.Hand.Cards.ToList())
                {
                    if (!IsNonCombatBoardDeadWeight(hc.Template, index)) continue;

                    if (IsPureSellToken(hc.Template, index))
                    {
                        SellCard(ps, hc, repo, participant.Rng, index);
                    }
                    else
                    {
                        ps.Hand.Remove(hc);
                        hc.StartSlot = -1;
                        if (!ps.Stash.TryAddAuto(hc))
                            SellCard(ps, hc, repo, participant.Rng, index);
                    }
                }
            }

            // 若 Hand 还能直接塞进 Stash 里的卡则顺手塞入（Day 1~7 有 32% 概率懒得整理而留空 1~2 格）
            bool skipFill = ps.Day <= 7 && ps.Hand.Cards.Count >= 2 && participant.Rng.NextDouble() < 0.32;
            if (!skipFill)
            {
                foreach (var sc in ps.Stash.Cards.ToList())
                {
                    if (ps.Day >= 2 && hasCombatCardAnywhere && IsNonCombatBoardDeadWeight(sc.Template, index))
                        continue;
                    if (PlaceIntoHandSafe(ps.Hand, sc))
                        ps.Stash.Remove(sc);
                }
            }
            ArrangeHandPositions(participant, ps.Hand.Cards.ToList(), index);
            return;
        }

        var allOwned = new List<CardInstance>(ps.Hand.Cards.Count + ps.Stash.Cards.Count);
        allOwned.AddRange(ps.Hand.Cards);
        allOwned.AddRange(ps.Stash.Cards);

        if (allOwned.Count == 0) return;

        var inHandSet = new HashSet<CardInstance>(ps.Hand.Cards);
        var scored = allOwned
            .Select(c =>
            {
                if (IsSellConsumableCard(c.Template, participant, index))
                    return (Card: c, Width: Math.Clamp(c.SlotWidth, 1, 3), Score: -450.0);

                if (ps.Day >= 2 && IsNonCombatBoardDeadWeight(c.Template, index))
                    return (Card: c, Width: Math.Clamp(c.SlotWidth, 1, 3), Score: -350.0);

                double s = ScoreExistingCard(c, participant, index);
                // 四不像杂拼不舍得卖/换下已在场上的低阶旧牌
                if (participant.Persona == PersonaType.MessyHybrid && inHandSet.Contains(c))
                    s += 42.0;
                return (Card: c, Width: Math.Clamp(c.SlotWidth, 1, 3), Score: s);
            })
            .ToList();

        // 非萌新/四不像在赛前子集选择中强制贯彻“无重复杂牌 + 单武器核心唯一武器”规则；四不像亦禁止大中型同名卡并排上阵
        if (participant.Persona != PersonaType.MessyHybrid)
        {
            // 同名卡仅保留最高分那张，其余压至负分确保背包筛选绝不并排上阵
            var seenTemplate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var idx in Enumerable.Range(0, scored.Count).OrderByDescending(i => scored[i].Score))
            {
                var item = scored[idx];
                if (!seenTemplate.Add(item.Card.Template.Id))
                    scored[idx] = (item.Card, item.Width, Math.Min(item.Score - 400.0, -250.0));
            }

            // 单武器体系（Single_Weapon 或持有 Crow's Nest）保留最强唯一武器并惩罚第 2 把及以上武器 -90
            bool singleWeapon = IsSingleWeaponBuild(ps, participant.Archetype);
            if (singleWeapon)
            {
                bool keptWeapon = false;
                foreach (var idx in Enumerable.Range(0, scored.Count).OrderByDescending(i => scored[i].Score))
                {
                    var item = scored[idx];
                    var meta = index.GetMeta(item.Card.Template);
                    if (meta.IsWeapon)
                    {
                        if (!keptWeapon)
                        {
                            keptWeapon = true;
                            scored[idx] = (item.Card, item.Width, item.Score + 75.0);
                        }
                        else
                        {
                            scored[idx] = (item.Card, item.Width, item.Score - 90.0);
                        }
                    }
                }
            }
        }
        else
        {
            // 非 CluelessNewbie 禁止从 Stash 拉出与 Hand 同模板且 width >= 2 的重名大中型卡并排上阵
            var seenWideTemplate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var idx in Enumerable.Range(0, scored.Count).OrderByDescending(i => scored[i].Score))
            {
                var item = scored[idx];
                if (item.Width >= 2 && !seenWideTemplate.Add(item.Card.Template.Id))
                    scored[idx] = (item.Card, item.Width, -500.0);
            }
        }

        // 若全部为负分（极少见），仍保底允许选出至少 1 张最强卡以免空场
        if (scored.All(x => x.Score <= 0))
        {
            int topIdx = Enumerable.Range(0, scored.Count).OrderByDescending(i => scored[i].Score).First();
            scored[topIdx] = (scored[topIdx].Card, scored[topIdx].Width, 1.0);
        }

        var chosen = SelectBestSubsetByWidth(scored, unlockedCount);
        if (chosen.Count == 0 && scored.Count > 0)
        {
            var bestFallback = scored.OrderByDescending(x => x.Score).First();
            if (bestFallback.Width <= unlockedCount)
                chosen.Add(bestFallback.Card);
        }

        var chosenSet = new HashSet<CardInstance>(chosen);

        // 先按决策划分留存 Stash 与卖出淘汰：先把要卖出的牌通过统一 SellCard 卖出，以在完整在场状态下触发 TTriggerOnCardSold
        int stashCap = participant.Persona == PersonaType.HardForcer ? 6 : 4;
        var toKeepInStash = new List<CardInstance>();
        var toSell = new List<CardInstance>();
        foreach (var c in allOwned)
        {
            if (chosenSet.Contains(c)) continue;
            bool isConsumable = participant.Persona != PersonaType.CluelessNewbie && IsSellConsumableCard(c.Template, participant, index);
            bool keep = !isConsumable
                && toKeepInStash.Count < stashCap
                && (participant.Persona == PersonaType.HardForcer || c.Tier >= CardTier.Silver);
            if (keep)
                toKeepInStash.Add(c);
            else
                toSell.Add(c);
        }

        foreach (var c in toSell)
        {
            if (ps.Hand.Cards.Contains(c) || ps.Stash.Cards.Contains(c))
                SellCard(ps, c, repo, participant.Rng, index);
        }

        // 保留 SellCard 触发期内可能新衍生到 Stash 的合法物品，并排除已销毁卡牌
        var toSellSet = new HashSet<CardInstance>(toSell);
        var extraSpawned = ps.Stash.Cards
            .Where(c => !c.IsDestroyed && !chosenSet.Contains(c) && !toKeepInStash.Contains(c) && !toSellSet.Contains(c))
            .ToList();

        ps.Hand.Cards.Clear();
        ps.Stash.Cards.Clear();

        foreach (var c in toKeepInStash)
        {
            if (c.IsDestroyed) continue;
            c.StartSlot = -1;
            ps.Stash.TryAddAuto(c);
        }
        foreach (var c in extraSpawned)
        {
            if (ps.Stash.Cards.Count >= BoardGrid.Capacity) break;
            c.StartSlot = -1;
            ps.Stash.TryAddAuto(c);
        }

        var survivingChosen = chosen.Where(c => !c.IsDestroyed).ToList();
        // 2. 智能排序与站位重排
        if (survivingChosen.Count > 0)
            ArrangeHandPositions(participant, survivingChosen, index);
    }

    /// <summary>
    /// 依据流派 PositioningRules 与邻接光环重新整理当前 Hand 上的物品站位，保证通过 BoardGrid 零重叠零越界放置。
    /// </summary>
    public void ArrangeHandPositions(RunParticipantState participant, SimulationRepoIndex? index = null)
    {
        var ps = participant.PlayerState;
        if (ps.Hand.Cards.Count <= 1) return;
        var currentCards = ps.Hand.Cards.ToList();
        ArrangeHandPositions(participant, currentCards, index);
    }

    private void ArrangeHandPositions(RunParticipantState participant, List<CardInstance> targetCards, SimulationRepoIndex? index)
    {
        var ordered = OrderCardsForPositioning(targetCards, participant, index);
        PlaceOrderedCardsIntoUnlockedMask(participant.PlayerState.Hand, ordered);
    }

    private static List<CardInstance> SelectBestSubsetByWidth(
        List<(CardInstance Card, int Width, double Score)> items,
        int capacity)
    {
        int n = items.Count;
        var dp = new double[n + 1, capacity + 1];
        var take = new bool[n + 1, capacity + 1];

        for (int i = 1; i <= n; i++)
        {
            int w = items[i - 1].Width;
            double v = items[i - 1].Score;
            for (int c = 0; c <= capacity; c++)
            {
                dp[i, c] = dp[i - 1, c];
                if (w <= c && dp[i - 1, c - w] + v > dp[i, c])
                {
                    dp[i, c] = dp[i - 1, c - w] + v;
                    take[i, c] = true;
                }
            }
        }

        var result = new List<CardInstance>();
        int rem = capacity;
        for (int i = n; i >= 1; i--)
        {
            if (take[i, rem])
            {
                result.Add(items[i - 1].Card);
                rem -= items[i - 1].Width;
            }
        }
        result.Reverse();
        return result;
    }

    private List<CardInstance> OrderCardsForPositioning(
        List<CardInstance> cards,
        RunParticipantState participant,
        SimulationRepoIndex? index)
    {
        if (cards.Count <= 1) return cards;

        // 萌新 100% 随机乱摆、四不像 75% 随机乱摆、随缘新手 65% 随机排列
        double randomSwapProb = participant.Persona switch
        {
            PersonaType.CluelessNewbie => 1.0,
            PersonaType.MessyHybrid => 0.75,
            PersonaType.Casual => 0.65,
            _ => 0.0
        };

        if (randomSwapProb > 0 && participant.Rng.NextDouble() < randomSwapProb)
        {
            var shuffled = new List<CardInstance>(cards);
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = participant.Rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }
            return shuffled;
        }

        CardMeta GetMeta(CardTemplate tpl) => index != null ? index.GetMeta(tpl) : CardMeta.Build(tpl);

        var rules = participant.Archetype.PositioningRules;
        bool ruleCenter = rules.Contains(PositioningRule.CoreCenter);
        bool ruleAdjacent = rules.Contains(PositioningRule.AdjacentToCore);
        bool ruleLeftMost = rules.Contains(PositioningRule.LeftMost);
        bool ruleRightMost = rules.Contains(PositioningRule.RightMost);
        bool ruleFastLeftOfScaler = rules.Contains(PositioningRule.FastTriggerLeftOfScaler);
        bool ruleShieldEdge = rules.Contains(PositioningRule.ShieldOnEdge);

        var scores = new Dictionary<CardInstance, double>();
        bool shieldToggle = false;
        CardInstance? coreAnchor = null;
        double bestCoreVal = double.NegativeInfinity;

        foreach (var c in cards)
        {
            var meta = GetMeta(c.Template);
            bool isCore = ContainsIgnoreCase(participant.Archetype.CoreItemNames, c.Template.InternalName);
            double existingVal = ScoreExistingCard(c, participant, index);
            if (isCore || existingVal > bestCoreVal)
            {
                if (isCore && (coreAnchor == null || !ContainsIgnoreCase(participant.Archetype.CoreItemNames, coreAnchor.Template.InternalName) || existingVal > bestCoreVal))
                {
                    bestCoreVal = existingVal;
                    coreAnchor = c;
                }
                else if (coreAnchor == null || (!ContainsIgnoreCase(participant.Archetype.CoreItemNames, coreAnchor.Template.InternalName) && existingVal > bestCoreVal))
                {
                    bestCoreVal = existingVal;
                    coreAnchor = c;
                }
            }

            double pos = 0.50;
            if (ruleLeftMost && (meta.PrefersLeftMost || isCore && !ruleCenter))
                pos = 0.08;
            if (ruleRightMost && (meta.PrefersRightMost || meta.IsValueScaler))
                pos = 0.92;
            if (ruleFastLeftOfScaler)
            {
                if (meta.IsFastTrigger) pos -= 0.22;
                if (meta.IsScaler) pos += 0.22;
            }
            if (ruleShieldEdge && meta.IsShield && !isCore)
            {
                pos = shieldToggle ? 0.94 : 0.06;
                shieldToggle = !shieldToggle;
            }
            if (ruleCenter && isCore)
            {
                pos = 0.50;
            }

            // 细微协同方向：RightCard 光环希望在目标左侧，LeftCard 光环希望在目标右侧
            if (meta.BuffsRightNeighbor) pos -= 0.08;
            if (meta.BuffsLeftNeighbor) pos += 0.08;

            scores[c] = pos;
        }

        var ordered = cards.OrderBy(c => scores[c]).ThenByDescending(c => ScoreExistingCard(c, participant, index)).ToList();

        // 处理 CoreCenter 与 AdjacentToCore：将核心置中并让相邻光环/辅助件紧贴核心左右
        if ((ruleCenter || ruleAdjacent) && coreAnchor != null && ordered.Count >= 3)
        {
            ordered.Remove(coreAnchor);
            var leftNeighbors = new List<CardInstance>();
            var rightNeighbors = new List<CardInstance>();
            var rest = new List<CardInstance>();

            foreach (var c in ordered)
            {
                var meta = GetMeta(c.Template);
                bool isSup = ContainsIgnoreCase(participant.Archetype.SupportItemNames, c.Template.InternalName);
                if (meta.BuffsRightNeighbor)
                    leftNeighbors.Add(c);
                else if (meta.BuffsLeftNeighbor)
                    rightNeighbors.Add(c);
                else if (ruleAdjacent && (meta.HasNeighborEffect || isSup))
                {
                    if (leftNeighbors.Count <= rightNeighbors.Count) leftNeighbors.Add(c);
                    else rightNeighbors.Add(c);
                }
                else
                {
                    rest.Add(c);
                }
            }

            int mid = rest.Count / 2;
            var rebuilt = new List<CardInstance>(cards.Count);
            for (int i = 0; i < mid; i++) rebuilt.Add(rest[i]);
            rebuilt.AddRange(leftNeighbors);
            rebuilt.Add(coreAnchor);
            rebuilt.AddRange(rightNeighbors);
            for (int i = mid; i < rest.Count; i++) rebuilt.Add(rest[i]);
            ordered = rebuilt;
        }

        return ordered;
    }

    private static void PlaceOrderedCardsIntoUnlockedMask(BoardGrid hand, List<CardInstance> ordered)
    {
        if (TryPackSequential(hand, ordered)) return;

        // 若因解锁格不连续导致按序贪心失败，退回按大到小尺寸填充保底零重叠
        var bySizeDesc = ordered.OrderByDescending(c => c.SlotWidth).ToList();
        hand.Cards.Clear();
        foreach (var c in bySizeDesc) c.StartSlot = -1;
        if (TryPackSequential(hand, bySizeDesc)) return;

        hand.Cards.Clear();
        foreach (var c in ordered)
        {
            c.StartSlot = -1;
            hand.TryAddAuto(c);
        }
    }

    private static bool TryPackSequential(BoardGrid hand, List<CardInstance> cards)
    {
        hand.Cards.Clear();
        int cursor = 0;
        foreach (var card in cards)
        {
            int w = card.SlotWidth;
            bool placed = false;
            while (cursor + w <= BoardGrid.Capacity)
            {
                if (hand.CanFitAt(card, cursor))
                {
                    card.StartSlot = cursor;
                    hand.Cards.Add(card);
                    cursor += w;
                    placed = true;
                    break;
                }
                cursor++;
            }
            if (!placed)
            {
                hand.Cards.Clear();
                foreach (var c in cards) c.StartSlot = -1;
                return false;
            }
        }
        hand.Cards.Sort((a, b) => a.StartSlot.CompareTo(b.StartSlot));
        return true;
    }

    private double ScoreExistingCard(CardInstance card, RunParticipantState participant, SimulationRepoIndex? index)
    {
        double baseScore = ScoreItemCore(card.Template, card.Tier, participant, index?.GetMeta(card.Template), skipDuplicateAndWeaponCheck: true);
        if (!string.IsNullOrEmpty(card.Enchantment))
        {
            baseScore += 28.0;
            if (participant.Archetype.PreferredEnchantments.TryGetValue(card.Enchantment, out var ew))
                baseScore += ew * 12.0;
        }
        // 计入局内已累积的永久成长收益
        foreach (var attr in CoreDamageAttrs)
        {
            if (card.PermanentAdd.TryGetValue(attr, out var add) && add > 0)
                baseScore += add * 0.45;
        }
        if (card.PermanentAdd.TryGetValue("CritChance", out var cc) && cc > 0)
            baseScore += cc * 0.8;

        return baseScore;
    }

    private static double ScoreCardAgainstArchetype(CardTemplate template, ArchetypeDefinition arch, CardMeta meta)
    {
        double score = 0;
        if (ContainsIgnoreCase(arch.CoreItemNames, template.InternalName)) score += 65.0;
        else if (ContainsIgnoreCase(arch.SupportItemNames, template.InternalName)) score += 35.0;
        if (ContainsIgnoreCase(arch.CoreSkillNames, template.InternalName)) score += 50.0;

        foreach (var t in template.Tags)
        {
            if (arch.PreferredTags.TryGetValue(t, out var w)) score += w * 12.0;
        }
        foreach (var ht in template.HiddenTags)
        {
            if (arch.PreferredHiddenTags.TryGetValue(ht, out var w)) score += w * 10.0;
        }
        foreach (var tr in meta.TriggerTypes)
        {
            if (arch.PreferredTriggers.TryGetValue(tr, out var w)) score += w * 9.0;
        }
        return score;
    }

    private static double ScoreEnchantmentForCard(string enchant, CardInstance card, PlayerState ps, ArchetypeDefinition arch, CardMeta meta)
    {
        double score = 10.0;
        if (arch.PreferredEnchantments.TryGetValue(enchant, out var w))
            score += w * 22.0;

        // 若场上已有其他物品佩戴同一附魔，给予明显去重惩罚以保证多样化附魔配置
        int sameOnBoard = 0;
        foreach (var c in ps.Hand.Cards)
        {
            if (c != card && string.Equals(c.Enchantment, enchant, StringComparison.OrdinalIgnoreCase))
                sameOnBoard++;
        }
        score -= sameOnBoard * 24.0;

        score += enchant switch
        {
            "Fiery" => meta.IsBurn ? 28.0 : (meta.IsFastTrigger ? 18.0 : 8.0),
            "Toxic" => meta.IsPoison ? 28.0 : (meta.IsFastTrigger ? 17.0 : 7.0),
            "Shielded" => meta.IsShield ? 28.0 : 12.0,
            "Restorative" => meta.IsHeal || meta.IsRegen ? 26.0 : 11.0,
            "Deadly" => meta.IsWeapon ? 27.0 : 8.0,
            "Obsidian" => meta.IsWeapon || meta.IsFastTrigger ? 22.0 : 10.0,
            "Turbo" => meta.IsFastTrigger || meta.IsHaste ? 24.0 : 16.0,
            "Shiny" => card.Tier >= CardTier.Gold ? 24.0 : 14.0,
            "Golden" => meta.IsEconomy || meta.IsProperty || meta.IsValueScaler ? 30.0 : 6.0,
            "Heavy" => meta.IsSlow ? 24.0 : 12.0,
            "Icy" => meta.IsFreeze ? 26.0 : 13.0,
            "Mossy" => meta.IsRegen || meta.IsPoison ? 24.0 : 9.0,
            "Radiant" => card.Tier >= CardTier.Gold ? 18.0 : 9.0,
            _ => 5.0
        };
        return score;
    }

    private static bool IsSingleWeaponBuild(PlayerState ps, ArchetypeDefinition arch)
    {
        if (arch.Id.Contains("Single_Weapon", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var c in EnumerateOwnedItems(ps))
        {
            if (string.Equals(c.Template.InternalName, "Crow's Nest", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int GetTargetGoldReserve(RunParticipantState participant)
    {
        int day = participant.PlayerState.Day;
        return participant.Persona switch
        {
            PersonaType.TempoAggro => 0,
            PersonaType.GreedyEco => day <= 6 ? Math.Min(20, 4 + day * 2) : 2,
            PersonaType.GreedyBust => day <= 6 ? Math.Min(35, 10 + day * 4) : 2,
            PersonaType.HardForcer => 1,
            PersonaType.FlexPivot => day <= 4 ? 3 : 1,
            PersonaType.Casual => 2,
            PersonaType.MessyHybrid => 1,
            PersonaType.CluelessNewbie => 0,
            _ => 1
        };
    }

    private static double EstimateBoardCombatStrength(PlayerState ps, SimulationRepoIndex index)
    {
        double power = ps.HealthMax * 0.55;
        foreach (var c in ps.Hand.Cards)
            power += index.GetMeta(c.Template).EstimateCombatPower(c.Tier) * (1.0 + (int)c.Tier * 0.35) * 4.0;
        power += ps.Skills.Count * 55.0;
        return power;
    }

    private static double GetTierBaseScore(CardTier tier) => tier switch
    {
        CardTier.Bronze => 10.0,
        CardTier.Silver => 24.0,
        CardTier.Gold => 48.0,
        CardTier.Diamond => 82.0,
        CardTier.Legendary => 105.0,
        _ => 10.0
    };

    private static IEnumerable<CardInstance> EnumerateOwnedItems(PlayerState ps)
    {
        foreach (var c in ps.Hand.Cards) yield return c;
        foreach (var c in ps.Stash.Cards) yield return c;
    }

    private static int CountFreeUnlockedSlots(BoardGrid hand)
    {
        int free = 0;
        for (int s = 0; s < BoardGrid.Capacity; s++)
        {
            if (hand.IsSlotUnlocked(s) && hand.GetCardAtSlot(s) == null)
                free++;
        }
        return free;
    }

    private static bool PlaceIntoHandSafe(BoardGrid hand, CardInstance card)
    {
        if (hand.TryAddAuto(card)) return true;

        // 若手牌已紧凑仍无法放入则返回 false
        var existing = hand.Cards.OrderBy(c => c.StartSlot).ToList();
        existing.Add(card);
        if (TryPackSequential(hand, existing)) return true;

        existing.Remove(card);
        TryPackSequential(hand, existing);
        return false;
    }

    /// <summary>
    /// 主动消化 Stash 中的纯卖出型战利品消耗品（如 Spare Change、口香糖、巧克力棒等），
    /// 避免在背包中闲置而错失金币与 TTriggerOnCardSold 永久属性成长。
    /// CluelessNewbie 跳过此主动消化步骤。
    /// </summary>
    private static readonly HashSet<string> OutOfCombatOnlyTriggerTypes = new(StringComparer.Ordinal)
    {
        "TTriggerOnCardSold",
        "TTriggerOnDayStarted",
        "TTriggerOnHourStarted",
        "TTriggerOnCardPurchased",
        "TTriggerOnCardUpgraded",
        "TTriggerOnFightEnded",
        "TTriggerOnPlayerAttributeChanged",
        "TTriggerOnCardAttributeChanged",
        "TTriggerOnCardQuestCompleted",
        "TTriggerOnCardEnchanted"
    };

    /// <summary>
    /// 判定卡牌在 PvP 战斗中是否完全无效（无冷却、无战斗内光环、无任务且无战斗触发器，例如重掷代币、椰子、零钱、铲子等纯局外经济或产牌工具）。
    /// </summary>
    public static bool IsNonCombatBoardDeadWeight(CardTemplate template, SimulationRepoIndex index)
    {
        if (HasAnyBaseCooldown(template))
            return false;

        if (template.Auras.Values.Any(au => !string.Equals(au.WorksIn, "OutOfCombatOnly", StringComparison.OrdinalIgnoreCase)))
            return false;

        var meta = index.GetMeta(template);
        if (meta.HasQuests)
            return false;

        foreach (var trig in meta.TriggerTypes)
        {
            if (!OutOfCombatOnlyTriggerTypes.Contains(trig))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 判定是否属于一次性纯售卖代币/战利品小道具（如 Reroll Token、Coconut、Spare Change），应直接卖出变现而非留场占格。
    /// </summary>
    internal static bool IsPureSellToken(CardTemplate template, SimulationRepoIndex index)
    {
        if (template.CardSize != CardSize.Small)
            return false;

        var meta = index.GetMeta(template);
        if (meta.IsProperty || meta.HasOutOfCombatAura)
            return false;

        if (!IsNonCombatBoardDeadWeight(template, index))
            return false;

        if (ContainsIgnoreCase(meta.TriggerTypes, "TTriggerOnDayStarted")
            || ContainsIgnoreCase(meta.TriggerTypes, "TTriggerOnHourStarted")
            || ContainsIgnoreCase(meta.TriggerTypes, "TTriggerOnFightEnded"))
        {
            return false;
        }

        return true;
    }

    public void DigestStashConsumables(RunParticipantState participant, GameDataRepository repo, SimulationRepoIndex index)
    {
        var ps = participant.PlayerState;
        bool hasCombatCard = ps.Hand.Cards.Any(c => !IsNonCombatBoardDeadWeight(c.Template, index))
            || ps.Stash.Cards.Any(c => !IsNonCombatBoardDeadWeight(c.Template, index));

        if (participant.Persona == PersonaType.CluelessNewbie)
        {
            if (ps.Day >= 3 && hasCombatCard)
            {
                foreach (var card in ps.Stash.Cards.ToList())
                {
                    if (!card.IsDestroyed && ps.Stash.Cards.Contains(card) && IsPureSellToken(card.Template, index))
                        SellCard(ps, card, repo, participant.Rng, index);
                }
                foreach (var card in ps.Hand.Cards.ToList())
                {
                    if (!card.IsDestroyed && ps.Hand.Cards.Contains(card) && IsPureSellToken(card.Template, index))
                        SellCard(ps, card, repo, participant.Rng, index);
                }
            }
            return;
        }

        foreach (var card in ps.Stash.Cards.ToList())
        {
            if (card.IsDestroyed || !ps.Stash.Cards.Contains(card)) continue;
            if (IsSellConsumableCard(card.Template, participant, index) || IsPureSellToken(card.Template, index))
            {
                SellCard(ps, card, repo, participant.Rng, index);
            }
        }

        if (ps.Day >= 2 && hasCombatCard)
        {
            foreach (var card in ps.Hand.Cards.ToList())
            {
                if (card.IsDestroyed || !ps.Hand.Cards.Contains(card)) continue;
                if (IsSellConsumableCard(card.Template, participant, index) || IsPureSellToken(card.Template, index))
                {
                    SellCard(ps, card, repo, participant.Rng, index);
                }
            }
        }
    }

    private static bool IsSellConsumableCard(CardTemplate template, RunParticipantState participant, SimulationRepoIndex index)
    {
        var meta = index.GetMeta(template);
        if (template.CardSize != CardSize.Small || meta.IsProperty || meta.HasOutOfCombatAura)
            return false;
        if (ContainsIgnoreCase(participant.Archetype.CoreItemNames, template.InternalName)
            || ContainsIgnoreCase(participant.Archetype.SupportItemNames, template.InternalName))
            return false;
        if (string.Equals(template.InternalName, "Spare Change", StringComparison.OrdinalIgnoreCase))
            return true;

        bool hasCombatTriggers = ContainsIgnoreCase(meta.TriggerTypes, "TTriggerOnCardFired")
            || ContainsIgnoreCase(meta.TriggerTypes, "TTriggerOnFightStarted");
        if (hasCombatTriggers || HasAnyBaseCooldown(template))
            return false;

        return meta.HasCardSoldTrigger || meta.IsPureEconomy;
    }

    private static bool HasAnyBaseCooldown(CardTemplate tpl)
    {
        if (tpl.Attributes != null && tpl.Attributes.GetValueOrDefault("CooldownMax", 0) > 0)
            return true;
        if (tpl.Tiers != null)
        {
            foreach (var td in tpl.Tiers.Values)
            {
                if (td.Attributes.GetValueOrDefault("CooldownMax", 0) > 0)
                    return true;
            }
        }
        return false;
    }

    private static void SellStashCardsUntilGold(RunParticipantState participant, int targetGold, GameDataRepository repo)
    {
        var ps = participant.PlayerState;
        var index = SimulationRepoIndex.GetOrCreate(repo);
        while (ps.Gold < targetGold && ps.Stash.Cards.Count > 0)
        {
            var last = ps.Stash.Cards[^1];
            SellCard(ps, last, repo, participant.Rng, index);
        }
    }

    internal static void SellCardFromHand(PlayerState ps, CardInstance card, GameDataRepository repo, Random rng)
        => SellCard(ps, card, repo, rng);

    /// <summary>
    /// 统一卖出入口（Hand / Stash 通用）：先在卡牌仍归属玩家的状态下触发 TTriggerOnCardSold 与任务推进，
    /// 随后从 Hand/Stash 移除并结算售价金币。
    /// </summary>
    internal static void SellCard(
        PlayerState ps,
        CardInstance card,
        GameDataRepository repo,
        Random rng,
        SimulationRepoIndex? index = null)
    {
        FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardSold", card, repo, rng, index);
        int sellPrice = card.GetSellPrice(repo.GameMode.StandardPrices);
        ps.Hand.Remove(card);
        ps.Stash.Remove(card);
        ps.Gold += sellPrice;
    }

    /// <summary>
    /// 安全派发局外触发器（同时扫描 Hand ∪ Stash ∪ Skills 并遵守 ActiveIn 作用域、重算局外光环与推进 Quests）。
    /// 使用 CardMeta 预计算标记执行零分配预判，仅在存在匹配触发或任务时分配执行上下文。
    /// </summary>
    public static void FireSafeOutOfCombatTriggers(
        PlayerState ps,
        string triggerType,
        CardInstance? sourceCard,
        GameDataRepository repo,
        Random rng,
        SimulationRepoIndex? index = null,
        string? changedAttr = null,
        double prevVal = 0,
        double curVal = 0,
        string combatType = "PVE",
        string combatOutcome = "",
        int depth = 0)
    {
        if (depth > 4) return;
        var idx = index ?? SimulationRepoIndex.GetOrCreate(repo);

        bool hasMatchingTrigger = false;
        bool hasQuests = false;
        bool hasOocAura = false;

        for (int i = 0; i < ps.Hand.Cards.Count; i++)
            InspectOwnedCardQuick(ps.Hand.Cards[i], inHand: true, idx, triggerType, ref hasMatchingTrigger, ref hasQuests, ref hasOocAura);
        for (int i = 0; i < ps.Stash.Cards.Count; i++)
            InspectOwnedCardQuick(ps.Stash.Cards[i], inHand: false, idx, triggerType, ref hasMatchingTrigger, ref hasQuests, ref hasOocAura);
        for (int i = 0; i < ps.Skills.Count; i++)
            InspectOwnedCardQuick(ps.Skills[i], inHand: true, idx, triggerType, ref hasMatchingTrigger, ref hasQuests, ref hasOocAura);

        if (!hasMatchingTrigger && !hasQuests) return;

        if (hasOocAura)
            Engine.AuraEngine.RecalculateAuras(ps, null, Engine.RuleScope.OutOfCombat, ps.Day, ps.Hour);

        var baseCtx = new Engine.RuleExecutionContext(repo, Engine.RuleScope.OutOfCombat, ps, null, rng)
        {
            CurrentDay = ps.Day,
            CurrentHour = ps.Hour,
            OnQuestCompleted = completedCard =>
            {
                if (depth < 4)
                    FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardQuestCompleted", completedCard, repo, rng, idx, depth: depth + 1);
            }
        };

        if (hasQuests)
        {
            Engine.AbilityExecutor.TryAdvanceQuests(
                triggerType, ps, sourceCard, sourceCard, ps,
                changedAttr, prevVal, curVal, combatType, combatOutcome, baseCtx);
        }

        if (!hasMatchingTrigger) return;

        var candidates = new List<(CardInstance Card, bool InHand)>(ps.Hand.Cards.Count + ps.Stash.Cards.Count + ps.Skills.Count);
        for (int i = 0; i < ps.Hand.Cards.Count; i++)
            candidates.Add((ps.Hand.Cards[i], true));
        for (int i = 0; i < ps.Stash.Cards.Count; i++)
            candidates.Add((ps.Stash.Cards[i], false));
        for (int i = 0; i < ps.Skills.Count; i++)
            candidates.Add((ps.Skills[i], true));

        foreach (var (ownerCard, inHand) in candidates)
        {
            if (ownerCard.IsDestroyed || ownerCard.IsDisabled) continue;
            var abilities = ownerCard.ActiveAbilities;
            if (abilities.Count == 0) continue;

            for (int a = 0; a < abilities.Count; a++)
            {
                var ab = abilities[a];
                if (ab.Trigger == null || ab.Action == null) continue;
                if (!IsActiveInMatch(ab.ActiveIn, inHand)) continue;
                if (!TriggerContainsType(ab.Trigger, triggerType)) continue;

                var ctx = baseCtx.WithCards(ownerCard, sourceCard, sourceCard);
                if (!Engine.ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, ctx))
                    continue;
                if (!Engine.AbilityExecutor.MatchesTrigger(
                    ab.Trigger, triggerType, ownerCard, ps, sourceCard, sourceCard, ps,
                    changedAttr, prevVal, curVal, combatType, combatOutcome, ctx))
                    continue;

                ExecuteSafeOutOfCombatAction(ab.Action, ownerCard, ctx, idx, depth);
            }
        }
    }

    private static void InspectOwnedCardQuick(
        CardInstance card,
        bool inHand,
        SimulationRepoIndex idx,
        string triggerType,
        ref bool hasMatchingTrigger,
        ref bool hasQuests,
        ref bool hasOocAura)
    {
        if (card.IsDestroyed || card.IsDisabled) return;
        var meta = idx.GetMeta(card.Template);
        if (meta.HasQuests) hasQuests = true;
        if (meta.HasOutOfCombatAura) hasOocAura = true;
        if (!hasMatchingTrigger && (meta.HasOutOfCombatTrigger || !string.IsNullOrEmpty(card.Enchantment) || card.CompletedQuests.Count > 0))
        {
            var abs = card.ActiveAbilities;
            for (int i = 0; i < abs.Count; i++)
            {
                var ab = abs[i];
                if (ab.Trigger != null && IsActiveInMatch(ab.ActiveIn, inHand) && TriggerContainsType(ab.Trigger, triggerType))
                {
                    hasMatchingTrigger = true;
                    break;
                }
            }
        }
    }

    private static bool IsActiveInMatch(string? activeIn, bool inHand) => activeIn switch
    {
        "HandOnly" => inHand,
        "StashOnly" => !inHand,
        _ => true // HandAndStash
    };

    private static bool TriggerContainsType(RuleNode? trig, string targetType)
    {
        if (trig == null || string.IsNullOrEmpty(trig.Type)) return false;
        if (string.Equals(trig.Type, targetType, StringComparison.Ordinal)) return true;
        if (trig.Triggers != null)
        {
            for (int i = 0; i < trig.Triggers.Count; i++)
            {
                if (TriggerContainsType(trig.Triggers[i], targetType)) return true;
            }
        }
        return false;
    }

    private static bool TryPayOutOfCombatCost(RuleNode cost, Engine.RuleExecutionContext ctx)
    {
        if (!string.Equals(cost.Type, "TActionCostPlayerAttribute", StringComparison.Ordinal))
            return true;
        var valNode = cost.GetValueNode(GameDatabaseLoader.JsonOptions);
        double amt = valNode != null
            ? Engine.ValueAndConditionEvaluator.EvaluateValue(valNode, ctx)
            : (cost.GetFixedNumericValue() ?? 0);
        if (double.IsNaN(amt) || double.IsInfinity(amt) || amt <= 0)
            return true;

        var attr = cost.PlayerAttributeType ?? "Gold";
        if (string.Equals(attr, "MaxHealth", StringComparison.OrdinalIgnoreCase))
            attr = "HealthMax";
        if (ctx.SelfPlayer.GetAttribute(attr) < amt)
            return false;

        ctx.SelfPlayer.ModifyAttribute(attr, "Subtract", amt);
        return true;
    }

    private static void ExecuteSafeOutOfCombatAction(
        RuleNode? action,
        CardInstance ownerCard,
        Engine.RuleExecutionContext ctx,
        SimulationRepoIndex idx,
        int depth)
    {
        if (action == null) return;
        if (action.Cost != null && !TryPayOutOfCombatCost(action.Cost, ctx))
            return;

        int? targetCount = action.TargetCount != null
            ? Math.Max(1, (int)Math.Round(Engine.ValueAndConditionEvaluator.EvaluateValue(action.TargetCount, ctx, 1)))
            : null;
        var ps = ctx.SelfPlayer;

        switch (action.Type)
        {
            case "TActionAnd":
                if (action.Actions == null) return;
                foreach (var sub in action.Actions)
                    ExecuteSafeOutOfCombatAction(sub, ownerCard, ctx, idx, depth);
                break;

            case "TActionPlayerModifyAttribute":
            {
                var attr = action.AttributeType ?? "Gold";
                if (string.Equals(attr, "MaxHealth", StringComparison.OrdinalIgnoreCase))
                    attr = "HealthMax";

                if (attr is not ("Gold" or "Income" or "HealthMax" or "Experience" or "HealthRegen" or "PercentDamageReduction" or "Prestige"))
                    break;

                var valNode = action.GetValueNode(GameDatabaseLoader.JsonOptions);
                double val = valNode != null
                    ? Engine.ValueAndConditionEvaluator.EvaluateValue(valNode, ctx)
                    : (action.GetFixedNumericValue() ?? 0);
                if (double.IsNaN(val) || double.IsInfinity(val)) val = 0;

                var op = action.Operation ?? "Add";
                foreach (var target in Engine.TargetResolver.ResolvePlayers(action.Target, ctx))
                {
                    if (!ReferenceEquals(target, ps)) continue;
                    double prev = target.GetAttribute(attr);
                    target.ModifyAttribute(attr, op, val);

                    switch (attr)
                    {
                        case "HealthMax":
                            target.Attributes["HealthMax"] = Math.Clamp(target.Attributes.GetValueOrDefault("HealthMax", 250), 100, 100000);
                            target.CurrentHealth = target.HealthMax;
                            break;
                        case "Gold":
                            target.Attributes["Gold"] = Math.Max(0, target.Attributes.GetValueOrDefault("Gold", 0));
                            break;
                        case "Income":
                            target.Attributes["Income"] = Math.Max(0, target.Attributes.GetValueOrDefault("Income", 0));
                            break;
                        case "Prestige":
                            target.Attributes["Prestige"] = Math.Clamp(target.Attributes.GetValueOrDefault("Prestige", 0), 0, 50);
                            break;
                        case "PercentDamageReduction":
                            target.Attributes["PercentDamageReduction"] = Math.Clamp(target.Attributes.GetValueOrDefault("PercentDamageReduction", 0), 0, 90);
                            break;
                        case "HealthRegen":
                        case "Experience":
                            target.Attributes[attr] = Math.Max(0, target.Attributes.GetValueOrDefault(attr, 0));
                            break;
                    }

                    double cur = target.GetAttribute(attr);
                    ctx.LastAttributeChangeDelta = cur - prev;
                    if (Math.Abs(cur - prev) > 1e-6 && depth < 4 && ctx.Repo != null)
                    {
                        FireSafeOutOfCombatTriggers(
                            ps, "TTriggerOnPlayerAttributeChanged", ownerCard, ctx.Repo, ctx.Rng, idx,
                            changedAttr: attr, prevVal: prev, curVal: cur, depth: depth + 1);
                    }
                }
                break;
            }

            case "TActionCardModifyAttribute":
            {
                var attr = action.AttributeType ?? "DamageAmount";
                if (string.Equals(attr, "CooldownDisabled", StringComparison.OrdinalIgnoreCase))
                    break;

                bool isAllowed = attr is "DamageAmount" or "ShieldApplyAmount" or "BurnApplyAmount"
                    or "PoisonApplyAmount" or "HealAmount" or "RegenApplyAmount" or "CritChance"
                    or "Lifesteal" or "AmmoMax" or "SellPrice" or "HasteAmount" or "SlowAmount"
                    or "FreezeAmount" or "CooldownMax" or "PercentCooldownReduction" or "Multicast"
                    or "Value"
                    || attr.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);
                if (!isAllowed) break;

                var valNode = action.GetValueNode(GameDatabaseLoader.JsonOptions);
                double val = valNode != null
                    ? Engine.ValueAndConditionEvaluator.EvaluateValue(valNode, ctx)
                    : (action.GetFixedNumericValue() ?? 0);
                if (double.IsNaN(val) || double.IsInfinity(val)) val = 0;

                var op = action.Operation ?? "Add";
                bool isPerm = action.Duration == null;

                foreach (var card in Engine.TargetResolver.ResolveCards(action.Target, ctx, targetCount))
                {
                    double prev = card.GetAttribute(attr);
                    card.ModifyAttribute(attr, op, val, isPerm);

                    // 安全护栏：防止永久冷却缩减越界（CooldownMax 最低 1000ms，PercentCooldownReduction 最高 75%）
                    if (isPerm && string.Equals(attr, "CooldownMax", StringComparison.OrdinalIgnoreCase))
                    {
                        if (card.BaseAttributes.GetValueOrDefault("CooldownMax", 0) > 0)
                        {
                            double unscaled = card.GetAttributeUnscaled("CooldownMax");
                            if (unscaled < 1000)
                                card.PermanentAdd["CooldownMax"] = card.PermanentAdd.GetValueOrDefault("CooldownMax", 0) + (1000 - unscaled);
                        }
                    }
                    else if (isPerm && string.Equals(attr, "PercentCooldownReduction", StringComparison.OrdinalIgnoreCase))
                    {
                        double unscaled = card.GetAttributeUnscaled("PercentCooldownReduction");
                        if (unscaled > 75)
                            card.PermanentAdd["PercentCooldownReduction"] = card.PermanentAdd.GetValueOrDefault("PercentCooldownReduction", 0) + (75 - unscaled);
                        else if (unscaled < 0)
                            card.PermanentAdd["PercentCooldownReduction"] = card.PermanentAdd.GetValueOrDefault("PercentCooldownReduction", 0) - unscaled;
                    }

                    double cur = card.GetAttribute(attr);
                    ctx.LastAttributeChangeDelta = cur - prev;
                    if (Math.Abs(cur - prev) > 1e-6 && depth < 4 && ctx.Repo != null)
                    {
                        FireSafeOutOfCombatTriggers(
                            ps, "TTriggerOnCardAttributeChanged", ownerCard, ctx.Repo, ctx.Rng, idx,
                            changedAttr: attr, prevVal: prev, curVal: cur, depth: depth + 1);
                    }
                }
                break;
            }

            case "TActionCardUpgrade":
                foreach (var card in Engine.TargetResolver.ResolveCards(action.Target, ctx, targetCount))
                {
                    if (!ps.Hand.Cards.Contains(card) && !ps.Stash.Cards.Contains(card) && !ps.Skills.Contains(card))
                        continue;

                    var oldTier = card.Tier;
                    if (!string.IsNullOrEmpty(action.UpgradeToTier))
                    {
                        var targetTier = TierHelper.ParseTier(action.UpgradeToTier);
                        if (card.Tier < targetTier)
                            card.SetTier(targetTier);
                    }
                    else
                    {
                        card.TryUpgrade();
                    }

                    if (card.Tier != oldTier && depth < 4 && ctx.Repo != null)
                        FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardUpgraded", card, ctx.Repo, ctx.Rng, idx, depth: depth + 1);
                }
                break;

            case "TActionGameSpawnCards":
            {
                if (ctx.Repo == null || action.SpawnContext == null || ps.Stash.Cards.Count >= 8)
                    break;

                var spawned = Engine.SpawnQueryEngine.SpawnCards(action.SpawnContext, ctx, targetCount);
                int added = 0;
                foreach (var c in spawned)
                {
                    if (added >= 2 || ps.Stash.Cards.Count >= 8) break;
                    if (!string.Equals(c.Template.Type, "Item", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ctx.Repo.Localization != null && !ctx.Repo.HasOfficialZhTitle(c.Template)) continue;

                    // 二次防线兜底：若衍生卡牌自带/继承了不被自身模板允许的附魔，或超出单局附魔上限，立刻剥离
                    if (!string.IsNullOrEmpty(c.Enchantment))
                    {
                        bool supported = ContainsIgnoreCase(EnchantmentNames.All, c.Enchantment)
                            && c.Template.Enchantments != null
                            && c.Template.Enchantments.ContainsKey(c.Enchantment);
                        if (!supported || FastRunSimulator.CountEnchantedItems(ps) >= FastRunSimulator.MaxEnchantedItemsPerRun)
                            c.RemoveEnchantment();
                    }

                    if (ps.Stash.TryAddAuto(c))
                        added++;
                }
                break;
            }

            case "TActionCardEnchant":
            {
                if (string.IsNullOrEmpty(action.Enchantment) || !ContainsIgnoreCase(EnchantmentNames.All, action.Enchantment))
                    break;

                bool preventOverride = action.PreventOverride ?? false;
                foreach (var card in Engine.TargetResolver.ResolveCards(action.Target, ctx, targetCount))
                {
                    if (!ps.Hand.Cards.Contains(card) && !ps.Stash.Cards.Contains(card)) continue;
                    bool alreadyEnchanted = !string.IsNullOrEmpty(card.Enchantment);
                    if (!alreadyEnchanted && FastRunSimulator.CountEnchantedItems(ps) >= FastRunSimulator.MaxEnchantedItemsPerRun)
                        continue;
                    if (card.Template.Enchantments == null || !card.Template.Enchantments.ContainsKey(action.Enchantment))
                        continue;

                    string? oldEnch = card.Enchantment;
                    card.ApplyEnchantment(action.Enchantment, preventOverride);
                    if (!string.Equals(oldEnch, card.Enchantment, StringComparison.OrdinalIgnoreCase) && depth < 4 && ctx.Repo != null)
                        FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardEnchanted", card, ctx.Repo, ctx.Rng, idx, depth: depth + 1);
                }
                break;
            }

            case "TActionCardEnchantRandom":
            {
                bool preventOverride = action.PreventOverride ?? false;
                var pool = action.GetWeightedEnchantments();
                if (pool.Count == 0) break;

                foreach (var card in Engine.TargetResolver.ResolveCards(action.Target, ctx, targetCount))
                {
                    if (!ps.Hand.Cards.Contains(card) && !ps.Stash.Cards.Contains(card)) continue;
                    bool alreadyEnchanted = !string.IsNullOrEmpty(card.Enchantment);
                    if (!alreadyEnchanted && FastRunSimulator.CountEnchantedItems(ps) >= FastRunSimulator.MaxEnchantedItemsPerRun)
                        continue;

                    var eligible = pool
                        .Where(p => ContainsIgnoreCase(EnchantmentNames.All, p.Enchantment)
                            && card.Template.Enchantments != null
                            && card.Template.Enchantments.ContainsKey(p.Enchantment))
                        .ToList();
                    if (eligible.Count == 0) continue;

                    double totalW = eligible.Sum(e => Math.Max(0.1, e.Weight));
                    double roll = ctx.Rng.NextDouble() * totalW;
                    double acc = 0;
                    string picked = eligible[^1].Enchantment;
                    foreach (var e in eligible)
                    {
                        acc += Math.Max(0.1, e.Weight);
                        if (roll <= acc) { picked = e.Enchantment; break; }
                    }

                    string? oldEnch = card.Enchantment;
                    card.ApplyEnchantment(picked, preventOverride);
                    if (!string.Equals(oldEnch, card.Enchantment, StringComparison.OrdinalIgnoreCase) && depth < 4 && ctx.Repo != null)
                        FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardEnchanted", card, ctx.Repo, ctx.Rng, idx, depth: depth + 1);
                }
                break;
            }

            case "TActionCardDestroy":
                foreach (var card in Engine.TargetResolver.ResolveCards(action.Target, ctx, targetCount))
                {
                    if (card.GetAttribute("DestroyImmunity") > 0
                        || string.Equals(card.Enchantment, "Radiant", StringComparison.OrdinalIgnoreCase)
                        || card.IsDestroyed)
                        continue;

                    card.IsDestroyed = true;
                    if (depth < 4 && ctx.Repo != null)
                    {
                        FireSafeOutOfCombatTriggers(ps, "TTriggerOnBeforeCardDestroyed", ownerCard, ctx.Repo, ctx.Rng, idx, depth: depth + 1);
                        FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardPerformedDestruction", ownerCard, ctx.Repo, ctx.Rng, idx, depth: depth + 1);
                    }
                    ps.Hand.Remove(card);
                    ps.Stash.Remove(card);
                }
                break;
        }
    }

    private static bool ContainsIgnoreCase(IReadOnlyList<string> list, string name)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
