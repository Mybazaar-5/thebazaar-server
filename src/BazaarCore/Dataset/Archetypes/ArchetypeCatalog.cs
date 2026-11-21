using BazaarCore.Data;
using BazaarCore.Models;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 全 8 位可玩英雄的社区主流与高协同流派蓝图目录（共 96 套，每英雄 12 套）。
/// </summary>
public sealed class ArchetypeCatalog : IArchetypeCatalog
{
    private readonly IReadOnlyList<ArchetypeDefinition> _all;
    private readonly Dictionary<string, IReadOnlyList<ArchetypeDefinition>> _byHero;
    private readonly Dictionary<string, ArchetypeDefinition> _byId;

    /// <summary>
    /// 使用内置 96 套全英雄蓝图构造目录。
    /// </summary>
    public ArchetypeCatalog() : this(null)
    {
    }

    /// <summary>
    /// 构造目录；当传入完整游戏数据仓库（Always 物品数 &gt;= 500）时自动执行全量数据真值校验。
    /// </summary>
    public ArchetypeCatalog(GameDataRepository? repo)
        : this(BuildBase96(), repo)
    {
    }

    private ArchetypeCatalog(List<ArchetypeDefinition> initialList, GameDataRepository? repo)
    {
        var allList = new List<ArchetypeDefinition>(initialList);

        _all = allList.AsReadOnly();
        _byHero = allList
            .GroupBy(a => a.Hero, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ArchetypeDefinition>)g.ToList().AsReadOnly(),
                StringComparer.OrdinalIgnoreCase);

        _byId = new Dictionary<string, ArchetypeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var arch in allList)
        {
            if (!_byId.TryAdd(arch.Id, arch))
                throw new InvalidOperationException($"存在重复的流派蓝图 ID: {arch.Id}");
        }

        if (repo != null && repo.AlwaysSpawnableItems.Count >= 500)
        {
            var errors = ValidateAgainst(repo);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"流派蓝图目录校验失败（共 {errors.Count} 处错误），首条错误: {errors[0]}");
            }
        }
    }

    private static List<ArchetypeDefinition> BuildBase96()
    {
        var allList = new List<ArchetypeDefinition>(96);
        allList.AddRange(VanessaArchetypes.Build());
        allList.AddRange(DooleyArchetypes.Build());
        allList.AddRange(PygmalienArchetypes.Build());
        allList.AddRange(MakArchetypes.Build());
        allList.AddRange(KarnokArchetypes.Build());
        allList.AddRange(JulesArchetypes.Build());
        allList.AddRange(StelleArchetypes.Build());
        allList.AddRange(Hero8Archetypes.Build());
        return allList;
    }

    /// <summary>
    /// 构造方案 A+B 扩展流派目录：包含基础 96 套 + 32 套社区十胜转正蓝图 + 192 套基于 GameData.db 自动挖掘的高协同流派。
    /// </summary>
    public static ArchetypeCatalog CreateExpanded(GameDataRepository repo, int autoPerHero = 24)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var baseCatalog = new ArchetypeCatalog(repo);
        var merged = new List<ArchetypeDefinition>(baseCatalog.All.Count + 32 + autoPerHero * 8);
        merged.AddRange(baseCatalog.All);

        var commArchetypes = AutoSynergyArchetypeBuilder.BuildCommunityArchetypes(repo, baseCatalog);
        merged.AddRange(commArchetypes);

        var autoArchetypes = AutoSynergyArchetypeBuilder.BuildDataDrivenArchetypes(repo, merged, autoPerHero);
        merged.AddRange(autoArchetypes);

        var candidateCatalog = new ArchetypeCatalog(merged, repo: null);
        var errors = candidateCatalog.ValidateAgainst(repo);
        if (errors.Count == 0)
            return candidateCatalog;

        var invalidIds = new HashSet<string>(
            errors.Select(e => e.Split(':', 2)[0].Trim()),
            StringComparer.OrdinalIgnoreCase);

        var filtered = merged.Where(a => !invalidIds.Contains(a.Id)).ToList();
        return new ArchetypeCatalog(filtered, repo);
    }

    /// <summary>
    /// 基于现有扩展目录叠加外部真值构筑蓝图（如巴扎罗盘 builds），自动按 ID 去重并经过 ValidateAgainst 校验。
    /// </summary>
    public static ArchetypeCatalog CreateFromMerged(
        GameDataRepository repo,
        IEnumerable<ArchetypeDefinition> baseAndExpanded,
        IEnumerable<ArchetypeDefinition> extraArchetypes)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var merged = new List<ArchetypeDefinition>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var arch in baseAndExpanded)
        {
            if (seenIds.Add(arch.Id))
                merged.Add(arch);
        }
        foreach (var arch in extraArchetypes)
        {
            if (seenIds.Add(arch.Id))
                merged.Add(arch);
        }

        var candidate = new ArchetypeCatalog(merged, repo: null);
        var errors = candidate.ValidateAgainst(repo);
        if (errors.Count == 0)
            return candidate;

        var invalidIds = new HashSet<string>(
            errors.Select(e => e.Split(':', 2)[0].Trim()),
            StringComparer.OrdinalIgnoreCase);

        var filtered = merged.Where(a => !invalidIds.Contains(a.Id)).ToList();
        return new ArchetypeCatalog(filtered, repo);
    }

    /// <inheritdoc />
    public IReadOnlyList<ArchetypeDefinition> All => _all;

    /// <inheritdoc />
    public IReadOnlyList<ArchetypeDefinition> GetByHero(string hero)
    {
        if (string.IsNullOrWhiteSpace(hero))
            return Array.Empty<ArchetypeDefinition>();
        return _byHero.TryGetValue(hero, out var list) ? list : Array.Empty<ArchetypeDefinition>();
    }

    /// <inheritdoc />
    public ArchetypeDefinition? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return _byId.TryGetValue(id, out var arch) ? arch : null;
    }

    /// <inheritdoc />
    public ArchetypeDefinition PickForHero(string hero, PersonaType persona, Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        var candidates = GetByHero(hero);
        if (candidates.Count == 0)
            throw new ArgumentException($"未找到英雄 {hero} 对应的流派蓝图。", nameof(hero));

        var weights = new double[candidates.Count];
        double totalWeight = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            var w = ComputePersonaWeight(candidates[i], persona);
            weights[i] = w;
            totalWeight += w;
        }

        if (totalWeight <= 0)
            return candidates[rng.Next(candidates.Count)];

        var roll = rng.NextDouble() * totalWeight;
        double cumulative = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            cumulative += weights[i];
            if (roll <= cumulative)
                return candidates[i];
        }

        return candidates[^1];
    }

    /// <summary>
    /// 对全部流派蓝图执行针对指定游戏数据仓库的数据真值校验，返回所有发现的错误描述。
    /// </summary>
    public IReadOnlyList<string> ValidateAgainst(GameDataRepository repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var errors = new List<string>();
        var validEnchants = new HashSet<string>(EnchantmentNames.All, StringComparer.Ordinal);

        var itemsByName = repo.AlwaysSpawnableItems
            .GroupBy(c => c.InternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var skillsByName = repo.AlwaysSpawnableSkills
            .GroupBy(c => c.InternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var arch in _all)
        {
            if (string.IsNullOrWhiteSpace(arch.Id) || string.IsNullOrWhiteSpace(arch.Hero))
                errors.Add($"流派定义缺少 Id 或 Hero: {arch.Id}");

            if (arch.CoreItemNames.Count < 2)
                errors.Add($"{arch.Id}: CoreItemNames 数量不足 2 个 ({arch.CoreItemNames.Count})");
            if (arch.SupportItemNames.Count < 3)
                errors.Add($"{arch.Id}: SupportItemNames 数量不足 3 个 ({arch.SupportItemNames.Count})");
            if (arch.CoreSkillNames.Count < 2)
                errors.Add($"{arch.Id}: CoreSkillNames 数量不足 2 个 ({arch.CoreSkillNames.Count})");

            void CheckCardList(IReadOnlyList<string> names, Dictionary<string, List<CardTemplate>> pool, string kind)
            {
                foreach (var name in names)
                {
                    if (!pool.TryGetValue(name, out var templates))
                    {
                        errors.Add($"{arch.Id}: {kind} '{name}' 未在 Always {kind} 池中找到");
                        continue;
                    }

                    var matchedHero = templates.Any(t =>
                        t.Heroes.Contains(arch.Hero, StringComparer.OrdinalIgnoreCase) ||
                        t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase));
                    if (!matchedHero)
                    {
                        errors.Add($"{arch.Id}: {kind} '{name}' 不属于 {arch.Hero} 或 Common 卡池");
                        continue;
                    }

                    if (repo.Localization != null && !templates.Any(repo.HasOfficialZhTitle))
                    {
                        errors.Add($"{arch.Id}: {kind} '{name}' 缺少官方简体中文标题");
                    }
                }
            }

            CheckCardList(arch.CoreItemNames, itemsByName, "CoreItem");
            CheckCardList(arch.SupportItemNames, itemsByName, "SupportItem");
            CheckCardList(arch.CoreSkillNames, skillsByName, "CoreSkill");

            foreach (var ench in arch.PreferredEnchantments.Keys)
            {
                if (!validEnchants.Contains(ench))
                    errors.Add($"{arch.Id}: PreferredEnchantments 包含非法附魔键 '{ench}'");
            }
        }

        return errors;
    }

    /// <summary>
    /// 根据玩家画像计算流派抽样权重，体现快攻、贪经济/贪崩、硬追核心、灵活转型与萌新/杂拼等 8 种画像的抽样分布差异。
    /// </summary>
    private static double ComputePersonaWeight(ArchetypeDefinition arch, PersonaType persona)
    {
        bool HasEarly(params string[] tags) =>
            tags.Any(t => arch.EarlyFocusTags.Contains(t, StringComparer.OrdinalIgnoreCase));

        bool HasHidden(string tag, double minWeight = 1.0) =>
            arch.PreferredHiddenTags.TryGetValue(tag, out var w) && w >= minWeight;

        bool HasTag(string tag, double minWeight = 1.0) =>
            arch.PreferredTags.TryGetValue(tag, out var w) && w >= minWeight;

        bool EndgameContains(params string[] tokens) =>
            tokens.Any(tok => arch.EndgamePowerTag.Contains(tok, StringComparison.OrdinalIgnoreCase));

        double weight = 1.0;

        switch (persona)
        {
            case PersonaType.TempoAggro:
                if (HasEarly("Weapon", "Burn", "Ammo", "Damage", "Crit", "Haste", "Shield"))
                    weight += 1.6;
                if (HasHidden("Damage", 2.0) || HasHidden("Burn", 2.0) || HasHidden("Ammo", 1.8) || HasHidden("Haste", 1.8))
                    weight += 1.0;
                if (HasEarly("EconomyReference", "Value", "Income") ||
                    EndgameContains("Economy", "ValueScaling", "MaxHealth", "Transform", "GoldIncome", "RegenMaxHp", "Overdrive"))
                    weight *= 0.42;
                else if ((HasTag("Property", 1.8) || HasTag("Relic", 2.2)) && !HasTag("Weapon", 2.5))
                    weight *= 0.65;
                break;

            case PersonaType.GreedyEco:
            case PersonaType.GreedyBust:
                if (HasEarly("EconomyReference", "Value", "Income", "Health", "Property", "Regen", "Relic"))
                    weight += 1.9;
                if (HasHidden("EconomyReference", 1.5) || HasHidden("Value", 1.5) || HasHidden("Health", 1.5) ||
                    HasHidden("Income", 1.2) || HasTag("Property", 1.8) || HasTag("Relic", 1.8))
                    weight += 1.4;
                if (EndgameContains("Scaling", "Economy", "MaxH", "Value", "Gold", "Transform", "Enchant", "Overdrive"))
                    weight += 1.2;
                if (HasHidden("Charge", 2.8) && !HasHidden("Ammo"))
                    weight += 0.9;
                if (HasEarly("Ammo", "Weapon") && !HasHidden("EconomyReference") && !HasHidden("Value"))
                    weight *= 0.45;
                break;

            case PersonaType.HardForcer:
                if (arch.PositioningRules.Contains(PositioningRule.CoreCenter) ||
                    arch.PositioningRules.Contains(PositioningRule.AdjacentToCore))
                    weight += 1.4;
                if (arch.PreferredTriggers.Keys.Any(t =>
                        t.StartsWith("TTriggerOnCardPerformed", StringComparison.Ordinal) ||
                        t is "TTriggerOnCardCritted" or "TTriggerOnPlayerEnraged" or "TTriggerOnCardStartedFlying"))
                    weight += 1.3;
                if (EndgameContains("Loop", "Multicast", "OneShot", "Single", "Lifesteal", "Combo", "Engine", "Solo"))
                    weight += 1.4;
                if (HasEarly("Weapon") && !EndgameContains("Loop", "OneShot", "Single", "Multicast", "Engine"))
                    weight *= 0.65;
                break;

            case PersonaType.FlexPivot:
                if (arch.PreferredTags.Count >= 3 && arch.PreferredHiddenTags.Count >= 4)
                    weight += 1.3;
                if ((HasHidden("Shield") || HasHidden("Heal") || HasHidden("Regen") || HasHidden("Slow")) &&
                    (HasHidden("Damage") || HasHidden("Burn") || HasHidden("Poison")))
                    weight += 1.1;
                if (EndgameContains("Hybrid", "Control", "CrossHero", "Charge", "Slow", "Freeze", "Flying"))
                    weight += 0.9;
                if (EndgameContains("Single", "Solo", "OneShot"))
                    weight *= 0.55;
                break;

            case PersonaType.CluelessNewbie:
                // 萌新唐人：近似均匀随机抽选全流派
                weight = 1.0;
                break;

            case PersonaType.MessyHybrid:
                // 四不像杂拼：近似均匀随机抽选，仅对多标签杂糅流派给予极轻微倾向
                weight = arch.PreferredTags.Count >= 3 ? 1.12 : 1.0;
                break;

            case PersonaType.Casual:
            default:
                weight = 1.0;
                if (HasTag("Weapon", 1.5) || HasTag("Friend", 1.5) || HasTag("Food", 1.5) || HasHidden("Shield", 1.5))
                    weight += 0.35;
                if (arch.PreferredTriggers.ContainsKey("TTriggerOnBeforeCardDestroyed"))
                    weight *= 0.75;
                break;
        }

        return Math.Max(0.35, weight);
    }

    internal static IReadOnlyDictionary<string, double> Dict(params (string Key, double Value)[] entries)
    {
        var dict = new Dictionary<string, double>(entries.Length, StringComparer.Ordinal);
        foreach (var (k, v) in entries)
            dict[k] = v;
        return dict;
    }
}
