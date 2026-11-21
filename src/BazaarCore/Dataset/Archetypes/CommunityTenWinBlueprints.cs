using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 5.0.0 版本校验版社区 10 胜标准参考蓝本规格定义。
/// </summary>
public sealed record CommunityBlueprintSpec
{
    /// <summary>参考蓝本全局唯一标识。</summary>
    public required string BlueprintId { get; init; }

    /// <summary>所属英雄内部名称。</summary>
    public required string Hero { get; init; }

    /// <summary>对应的流派目录 ArchetypeDefinition.Id。</summary>
    public required string ArchetypeId { get; init; }

    /// <summary>参考蓝本简体中文标题。</summary>
    public required string TitleZh { get; init; }

    /// <summary>来源与版本校准说明。</summary>
    public required string SourceNote { get; init; }

    /// <summary>按从左到右合理相邻联动站位排列的物品 InternalName 列表（总宽度 &lt;= 10）。</summary>
    public required IReadOnlyList<string> ItemInternalNames { get; init; }

    /// <summary>配套核心技能 InternalName 列表。</summary>
    public required IReadOnlyList<string> SkillInternalNames { get; init; }

    /// <summary>推荐附魔物品映射（仅 1~2 项，键为物品 InternalName，值为合法附魔名称）。</summary>
    public required IReadOnlyDictionary<string, string> EnchantedItems { get; init; }

    /// <summary>简体中文成长机制与左右协同站位说明。</summary>
    public required string ScalingNote { get; init; }
}

/// <summary>
/// 全 8 位英雄共 32 套 5.0.0 实测可用的社区 10 胜高频参考蓝本库与盘面构建器。
/// </summary>
public static class CommunityTenWinBlueprints
{
    private const string DefaultSourceNote = "社区 10 胜高频体系·5.0.0 校验版";

    private static readonly IReadOnlyList<CommunityBlueprintSpec> AllSpecs = BuildAll();

    private static readonly Dictionary<string, IReadOnlyList<CommunityBlueprintSpec>> ByHero = AllSpecs
        .GroupBy(s => s.Hero, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<CommunityBlueprintSpec>)g.ToList().AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取全部 8 位英雄的 32 套社区 10 胜参考蓝本。
    /// </summary>
    public static IReadOnlyList<CommunityBlueprintSpec> GetAll() => AllSpecs;

    /// <summary>
    /// 获取指定英雄的社区 10 胜参考蓝本列表（每位英雄 4 套）。
    /// </summary>
    public static IReadOnlyList<CommunityBlueprintSpec> GetByHero(string hero)
    {
        if (string.IsNullOrWhiteSpace(hero))
            return Array.Empty<CommunityBlueprintSpec>();
        return ByHero.TryGetValue(hero, out var list) ? list : Array.Empty<CommunityBlueprintSpec>();
    }

    /// <summary>
    /// 依据本地真实卡库在指定天数（建议 Day 10~17）构建全 32 套合法 10 格满解锁参考盘面，
    /// 严格采用真实卡牌模板、合法品质/附魔、等级血量曲线与流派成长 PermanentAdd，可直接通过 BoardValidator 与 CombatSimulator 验证。
    /// </summary>
    public static List<PlayerState> BuildReferenceBoards(GameDataRepository repo, int day, int seed = 1001)
    {
        ArgumentNullException.ThrowIfNull(repo);

        int clampedDay = Math.Clamp(day, 1, 20);
        var rng = new Random(seed ^ (clampedDay * 7919));

        var itemsByName = repo.AllItems
            .GroupBy(c => c.InternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var skillsByName = repo.AllSkills
            .GroupBy(c => c.InternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var boards = new List<PlayerState>(AllSpecs.Count);
        foreach (var spec in AllSpecs)
        {
            var board = BuildSingleBoard(repo, spec, clampedDay, rng, itemsByName, skillsByName);
            boards.Add(board);
        }

        return boards;
    }

    private static PlayerState BuildSingleBoard(
        GameDataRepository repo,
        CommunityBlueprintSpec spec,
        int day,
        Random rng,
        Dictionary<string, List<CardTemplate>> itemsByName,
        Dictionary<string, List<CardTemplate>> skillsByName)
    {
        int level = Math.Clamp(day + 1 + (day >= 8 ? rng.Next(0, 2) : 0), 2, 20);
        int baseMaxHp = ComputeBaseMaxHp(repo, level);
        int bonusHp = ComputeArchetypeBonusHp(spec, day, rng);
        int healthMax = Math.Max(300, baseMaxHp + bonusHp);

        int wins = Math.Min(9, Math.Max(1, day - 2));
        int losses = Math.Max(0, Math.Min(4, day - 1 - wins));
        int prestige = Math.Clamp(20 - losses * 3, 2, 20);
        int unlockedSlots = day switch
        {
            <= 1 => 4,
            2 => 6,
            3 => 8,
            _ => 10
        };

        var ps = new PlayerState(
            playerId: $"ref_d{day:00}_{spec.BlueprintId}",
            displayName: $"{spec.TitleZh} (D{day})",
            hero: spec.Hero,
            initialHealthMax: healthMax,
            initialPrestige: prestige,
            initialGold: 12,
            unlockedHandSlots: unlockedSlots)
        {
            Day = day,
            Hour = 6,
            Wins = wins,
            Losses = losses,
            IsBot = true
        };

        ps.Level = level;
        ps.Attributes["HealthMax"] = healthMax;
        ps.Attributes["Health"] = healthMax;
        ps.Attributes["Income"] = Math.Max(6, 5 + day / 2);

        int slot = 0;
        for (int i = 0; i < spec.ItemInternalNames.Count; i++)
        {
            var itemName = spec.ItemInternalNames[i];
            var tpl = ResolveTemplate(itemsByName, itemName, spec.Hero, "Item");

            // 10 胜后期盘面在 Day 10+ 合理混合 Gold 与 Diamond 品质，主核/高阶单卡优先 Diamond
            CardTier desiredTier = day switch
            {
                >= 13 => (i < 2 || spec.EnchantedItems.ContainsKey(itemName) || rng.NextDouble() < 0.55)
                    ? CardTier.Diamond
                    : CardTier.Gold,
                >= 10 => (spec.EnchantedItems.ContainsKey(itemName) || rng.NextDouble() < 0.40)
                    ? CardTier.Diamond
                    : CardTier.Gold,
                >= 7 => rng.NextDouble() < 0.35 ? CardTier.Gold : CardTier.Silver,
                _ => CardTier.Silver
            };

            var tier = ResolveValidTier(tpl, desiredTier);
            string? enchant = null;
            if (spec.EnchantedItems.TryGetValue(itemName, out var desiredEnch) &&
                tpl.Enchantments != null &&
                tpl.Enchantments.ContainsKey(desiredEnch))
            {
                enchant = desiredEnch;
            }

            var card = new CardInstance(tpl, tier, enchant);
            if (slot + card.SlotWidth > BoardGrid.Capacity)
            {
                throw new InvalidOperationException(
                    $"参考蓝本 {spec.BlueprintId} 物品总宽度超过 10 格（在 '{itemName}' 越界）。");
            }

            ApplyRealisticPermanentGrowth(card, spec, day, rng);

            if (!ps.Hand.TryPlaceOrShift(card, slot))
            {
                throw new InvalidOperationException(
                    $"无法将蓝本 {spec.BlueprintId} 的物品 '{itemName}' 放置于槽位 {slot}。");
            }
            slot += card.SlotWidth;
        }

        foreach (var skillName in spec.SkillInternalNames)
        {
            var skillTpl = ResolveTemplate(skillsByName, skillName, spec.Hero, "Skill");
            var desiredSkillTier = day >= 12 ? CardTier.Gold : CardTier.Silver;
            var skillTier = ResolveValidTier(skillTpl, desiredSkillTier);
            ps.Skills.Add(new CardInstance(skillTpl, skillTier));
        }

        ps.PrepareForCombat();
        return ps;
    }

    private static int ComputeBaseMaxHp(GameDataRepository repo, int level)
    {
        int hp = 250;
        for (int lv = 1; lv < level; lv++)
        {
            hp += repo.LevelUps.TryGetValue(lv, out var lu) ? lu.HealthIncrease : 500;
        }
        return hp;
    }

    private static int ComputeArchetypeBonusHp(CommunityBlueprintSpec spec, int day, Random rng)
    {
        if (day <= 3) return 0;
        bool isMaxHpArchetype = spec.BlueprintId.Contains("PawnShop", StringComparison.Ordinal) ||
                                spec.BlueprintId.Contains("MaxHP", StringComparison.Ordinal) ||
                                spec.Hero == HeroNames.Pygmalien;

        if (spec.BlueprintId.Contains("PawnShop", StringComparison.Ordinal))
            return (day - 3) * 260 + rng.Next(100, 400);
        if (isMaxHpArchetype)
            return (day - 3) * 140 + rng.Next(60, 240);
        return (day - 4) * 45 + rng.Next(0, 120);
    }

    private static CardTemplate ResolveTemplate(
        Dictionary<string, List<CardTemplate>> pool,
        string internalName,
        string hero,
        string kind)
    {
        if (!pool.TryGetValue(internalName, out var candidates) || candidates.Count == 0)
            throw new InvalidOperationException($"在本地数据仓库中未找到社区蓝本 {kind}: '{internalName}'。");

        return candidates.FirstOrDefault(c => c.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(c => c.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase))
            ?? candidates[0];
    }

    private static CardTier ResolveValidTier(CardTemplate tpl, CardTier desired)
    {
        if (tpl.Tiers == null || tpl.Tiers.Count == 0)
            return desired;

        if (tpl.Tiers.ContainsKey(desired.ToString()))
            return desired;

        var available = Enum.GetValues<CardTier>()
            .Where(t => tpl.Tiers.ContainsKey(t.ToString()))
            .OrderBy(t => (int)t)
            .ToList();
        if (available.Count == 0)
            return tpl.DefaultTier;

        var notHigher = available.Where(t => t <= desired).ToList();
        return notHigher.Count > 0 ? notHigher[^1] : available[0];
    }

    private static void ApplyRealisticPermanentGrowth(CardInstance card, CommunityBlueprintSpec spec, int day, Random rng)
    {
        if (day <= 4) return;
        double scale = Math.Clamp((day - 4) / 8.0, 0.25, 1.8);
        var name = card.Template.InternalName;

        // 价值成长型地产与核心件
        if (name is "Fixer Upper" or "Skyscraper" or "Spacescraper" or "Landscraper" or "Pawn Shop" or "Abacus" or "VIP Pass")
        {
            card.PermanentAdd["SellPrice"] = Math.Round((22 + rng.Next(8, 22)) * scale);
        }

        if (card.BaseAttributes.ContainsKey("DamageAmount") && card.HasTag("Weapon"))
        {
            double baseBoost = spec.ArchetypeId.Contains("Single_Weapon", StringComparison.Ordinal) ? 45 : 22;
            card.PermanentAdd["DamageAmount"] = Math.Round((baseBoost + rng.Next(6, 18)) * scale);
        }

        if (card.BaseAttributes.ContainsKey("ShieldApplyAmount") && card.HasHiddenTag("Shield"))
        {
            card.PermanentAdd["ShieldApplyAmount"] = Math.Round((28 + rng.Next(8, 22)) * scale);
        }

        if (card.BaseAttributes.ContainsKey("BurnApplyAmount") && card.HasHiddenTag("Burn"))
        {
            card.PermanentAdd["BurnApplyAmount"] = Math.Round((5 + rng.Next(2, 6)) * scale);
        }

        if (card.BaseAttributes.ContainsKey("PoisonApplyAmount") && card.HasHiddenTag("Poison"))
        {
            card.PermanentAdd["PoisonApplyAmount"] = Math.Round((4 + rng.Next(1, 5)) * scale);
        }

        if (card.BaseAttributes.ContainsKey("RegenApplyAmount") && card.HasHiddenTag("Regen"))
        {
            card.PermanentAdd["RegenApplyAmount"] = Math.Round((4 + rng.Next(1, 4)) * scale);
        }
    }

    private static IReadOnlyDictionary<string, string> Ench(params (string Item, string Enchant)[] pairs)
    {
        var dict = new Dictionary<string, string>(pairs.Length, StringComparer.Ordinal);
        foreach (var (item, enchant) in pairs)
            dict[item] = enchant;
        return dict;
    }

    private static IReadOnlyList<CommunityBlueprintSpec> BuildAll() =>
    [
        // ======================== Vanessa (4 套) ========================
        new CommunityBlueprintSpec
        {
            BlueprintId = "Vanessa_BP_CrowNest_OneWeapon",
            Hero = HeroNames.Vanessa,
            ArchetypeId = "Vanessa_Single_Weapon_Lifesteal",
            TitleZh = "鸦巢单武暴击吸血狙击",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Custom Scope", "Sniper Rifle", "Suppressor", "Swash Buckle", "Crow's Nest", "Clamera"],
            SkillInternalNames = ["Quality over Quantity", "Sharpshooter", "Circle of Life", "One Shot, One Kill"],
            EnchantedItems = Ench(("Sniper Rifle", "Obsidian"), ("Crow's Nest", "Turbo")),
            ScalingNote = "定制准镜在狙击步枪左侧加暴击并暴击充能非武件，消音器在右侧增伤减 CD，鸦巢赋予全额吸血与暴击光环。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Vanessa_BP_Puffer_WaterWheel",
            Hero = HeroNames.Vanessa,
            ArchetypeId = "Vanessa_Aquatic_Puffer_Poison",
            TitleZh = "水车河豚永动剧毒流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Clamera", "Jellyfish", "Water Wheel", "Pufferfish", "Catfish", "Captain's Wheel"],
            SkillInternalNames = ["Improved Toxins", "Exposing Toxins", "Toxicity", "Barnacle Crusted"],
            EnchantedItems = Ench(("Pufferfish", "Toxic"), ("Water Wheel", "Shielded")),
            ScalingNote = "水母与河豚分列水车左右持续充能水车，水车高频加速全盘水系物品反哺河豚充能与毒须鲶叠毒。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Vanessa_BP_Primordial_DepthCharge",
            Hero = HeroNames.Vanessa,
            ArchetypeId = "Vanessa_Freeze_Primordial",
            TitleZh = "沉眠元初体冰山风暴流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Iceberg", "Slumbering Primordial", "Weather Glass", "Yeti Crab", "Jellyfish"],
            SkillInternalNames = ["Shatter", "Reaching the Summit", "Frostfire", "Invigorating Cold"],
            EnchantedItems = Ench(("Slumbering Primordial", "Obsidian"), ("Weather Glass", "Fiery")),
            ScalingNote = "冰山反手冻结、风暴瓶高频烧毒与雪怪蟹冰控共同充能沉眠元初体并叠加多重与面板成长。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Vanessa_BP_Tortuga_Friends",
            Hero = HeroNames.Vanessa,
            ArchetypeId = "Vanessa_Friend_Tortuga",
            TitleZh = "巨龟海狗伙伴急速齐射",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Jellyfish", "Catfish", "Tortuga", "Seadog's Saloon", "Sharkray"],
            SkillInternalNames = ["Beautiful Friendship", "Balanced Friendship", "Friend of the Sea (Skill)", "Denizens of the Deep"],
            EnchantedItems = Ench(("Tortuga", "Deadly"), ("Seadog's Saloon", "Turbo")),
            ScalingNote = "水系伙伴触发充能巨龟托图加，海狗沙龙吃满伙伴数量多重加速减速并带动鳐鲨全队成长。"
        },

        // ======================== Dooley (4 套) ========================
        new CommunityBlueprintSpec
        {
            BlueprintId = "Dooley_BP_CombatCore_Tech",
            Hero = HeroNames.Dooley,
            ArchetypeId = "Dooley_CombatCore_Buff",
            TitleZh = "战斗核心双向充能循环",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["GPU", "First Aiden", "Charging Station", "Combat Core", "Battery", "Metronome", "Alpha Ray"],
            SkillInternalNames = ["Core Surge", "Overclocked", "Well-Oiled Machine", "Firepower"],
            EnchantedItems = Ench(("Combat Core", "Obsidian"), ("Charging Station", "Shielded")),
            ScalingNote = "急救艾登在充能站左侧频繁充能战斗核心，右侧电池与节拍器进一步压缩核心周期并在左右连动中叠高攻防。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Dooley_BP_Ignition_Burn_Solar",
            Hero = HeroNames.Dooley,
            ArchetypeId = "Dooley_Burn_Solar_Torch",
            TitleZh = "引燃核心太阳能喷火流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Bomb Squad", "Char Cole", "Ignition Core", "Flamethrower", "Solar Farm", "Omega Ray"],
            SkillInternalNames = ["Letting Off Steam", "Heat Shield", "Burn Containment", "Conflagration"],
            EnchantedItems = Ench(("Flamethrower", "Fiery"), ("Solar Farm", "Turbo")),
            ScalingNote = "引燃核心左侧由炸弹小队和炭火科尔充能，右侧为火焰喷射枪与欧米伽射线全局加灼烧，配合太阳能农场永动。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Dooley_BP_Bunker_ForceField",
            Hero = HeroNames.Dooley,
            ArchetypeId = "Dooley_Armored_ForceField",
            TitleZh = "装甲核心掩体力场反伤",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Duct Tape", "Armored Core", "Bunker", "Force Field", "Barbed Wire"],
            SkillInternalNames = ["Sharp Corners", "Shield Bash", "Defensive Stance", "Quick Defenses"],
            EnchantedItems = Ench(("Force Field", "Shielded"), ("Armored Core", "Turbo")),
            ScalingNote = "掩体居中同时为左侧装甲核心与右侧力场提供多重释放并减伤，力场以海量护盾等量转化为超高直伤。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Dooley_BP_RoboticFactory_Dooltron",
            Hero = HeroNames.Dooley,
            ArchetypeId = "Dooley_Friend_Factory_Dooltron",
            TitleZh = "机器人工厂多尔王多重流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["First Aiden", "DJ Circuit Breaker", "Dooltron", "Robotic Factory", "Copper Ed"],
            SkillInternalNames = ["Buddy System", "Beautiful Friendship", "Balanced Friendship", "Intrusion Countermeasures"],
            EnchantedItems = Ench(("Dooltron", "Deadly"), ("Robotic Factory", "Shielded")),
            ScalingNote = "机器人工厂赋予全部伙伴+1多重释放，断路器 DJ 加速全伙伴并反复驱动多尔王实现多重高伤打击。"
        },

        // ======================== Pygmalien (4 套) ========================
        new CommunityBlueprintSpec
        {
            BlueprintId = "Pygmalien_BP_Crook_Atlatl",
            Hero = HeroNames.Pygmalien,
            ArchetypeId = "Pygmalien_Square_Crook_Atlatl",
            TitleZh = "曲柄杖投矛器中型军团",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Atlatl", "Crook", "Regal Blade", "Flail", "Showcase"],
            SkillInternalNames = ["Hyper Focus", "Lifting", "Strong Arm", "Reinforced Steel"],
            EnchantedItems = Ench(("Atlatl", "Obsidian"), ("Regal Blade", "Deadly")),
            ScalingNote = "全盘 5 件 2 格中型武器/展品柜吃满曲柄杖高额全队增伤，投矛器按伤害比例将冷却压低至秒级高频扫射。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Pygmalien_BP_FixerUpper_SpikyShield",
            Hero = HeroNames.Pygmalien,
            ArchetypeId = "Pygmalien_FixerUpper_Shield",
            TitleZh = "毛坯房露台棘刺盾高盾流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Fixer Upper", "Balcony", "Spiky Shield", "Landscraper"],
            SkillInternalNames = ["Huge Tank", "Quick Defenses", "Sharp Corners", "Renovation"],
            EnchantedItems = Ench(("Fixer Upper", "Shielded"), ("Spiky Shield", "Obsidian")),
            ScalingNote = "毛坯房置于露台左侧战斗中价值翻倍并大幅缩减冷却，配合景观楼叠加高盾后由棘刺盾一击引爆等量护盾伤害。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Pygmalien_BP_PawnShop_DoubleWhammy",
            Hero = HeroNames.Pygmalien,
            ArchetypeId = "Pygmalien_MaxHP_DoubleWhammy",
            TitleZh = "当铺大血双头巨锤碾压",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Pawn Shop", "Belt", "Double Whammy", "Lion Cane"],
            SkillInternalNames = ["Huge Warrior", "Healthy Hoarder", "Property Mogul", "Growth Spurt"],
            EnchantedItems = Ench(("Double Whammy", "Deadly"), ("Lion Cane", "Obsidian")),
            ScalingNote = "当铺与腰带把最大生命值堆至万点以上，双头巨锤与狮首手杖按最大生命值百分比打出毁灭性重击。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Pygmalien_BP_Skyscraper_Value",
            Hero = HeroNames.Pygmalien,
            ArchetypeId = "Pygmalien_Skyscraper_Value",
            TitleZh = "摩天大楼露台价值核爆流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Skyscraper", "Balcony", "Spacescraper", "VIP Pass", "Abacus"],
            SkillInternalNames = ["Prosperity", "Master Salesman", "Long-term Investments", "Commercial Zoning"],
            EnchantedItems = Ench(("Skyscraper", "Golden"), ("Spacescraper", "Shielded")),
            ScalingNote = "摩天大楼在露台左侧获得双倍价值与减冷却，摩空大楼战斗价值翻倍并由算盘和 VIP 通行证继续放大总价值伤害与护盾。"
        },

        // ======================== Mak (4 套) ========================
        new CommunityBlueprintSpec
        {
            BlueprintId = "Mak_BP_Athanor_PotionBurn",
            Hero = HeroNames.Mak,
            ArchetypeId = "Mak_Potion_Athanor_Burn",
            TitleZh = "炼金炉沸腾药水连爆流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Fire Potion", "Boiling Flask", "Energy Potion", "Athanor", "Bottled Explosion", "Strength Potion"],
            SkillInternalNames = ["Explosive Potions", "Brewmaster", "Alchemical Precision", "Stocked"],
            EnchantedItems = Ench(("Athanor", "Fiery"), ("Bottled Explosion", "Deadly")),
            ScalingNote = "沸腾烧瓶与炼金炉相邻护航所有药水弹药装填与多重释放，药水倾泻同时触发炼金炉高频灼烧与瓶装爆炸暴击。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Mak_BP_PoppyField_PoisonWeapons",
            Hero = HeroNames.Mak,
            ArchetypeId = "Mak_PoppyField_Poison_Weapons",
            TitleZh = "罂粟花田剧毒武器连击",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Poppy Field", "Goop Flail", "Venom", "Spider Mace", "Floor Spike"],
            SkillInternalNames = ["Venomous Blade", "Poison Forge", "Toxic Weapons", "Improved Toxins"],
            EnchantedItems = Ench(("Goop Flail", "Toxic"), ("Spider Mace", "Obsidian")),
            ScalingNote = "使用武器时罂粟花田叠毒并把敌方剧毒直接转化为全武器伤害，黏液链枷右侧的毒液同步联动充能蜘蛛连枷。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Mak_BP_StaffOfMoose_Regen",
            Hero = HeroNames.Mak,
            ArchetypeId = "Mak_Regen_StaffOfMoose_Sunlight",
            TitleZh = "驼鹿角杖日光再生爆发",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Staff of the Moose", "Sunlight Spear", "Tomb of the Ancients", "Soul Ring", "Emerald"],
            SkillInternalNames = ["Essence Overflow", "Moth to a Flame", "Vital Renewal", "Herbal Supplements"],
            EnchantedItems = Ench(("Staff of the Moose", "Obsidian"), ("Sunlight Spear", "Fiery")),
            ScalingNote = "先祖墓与魂戒快速堆高生命再生，驼鹿角杖将再生等量转化为全武器伤害并在半血提供无敌窗口，日光矛同步燃尽敌方。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Mak_BP_EternalTorch_Library",
            Hero = HeroNames.Mak,
            ArchetypeId = "Mak_Burn_FireClaw_DragonBreath",
            TitleZh = "图书馆火爪永恒火炬流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Library", "Fire Claw", "Eternal Torch", "Ruby", "Candles"],
            SkillInternalNames = ["Ancient Flamekeeper", "Conflagration", "Living Flame", "Tracer Fire"],
            EnchantedItems = Ench(("Eternal Torch", "Fiery"), ("Fire Claw", "Turbo")),
            ScalingNote = "全非武器阵容无视图书馆武器惩罚并享受全面大幅减冷却，永恒火炬、火爪与蜡烛极速叠加高额灼烧。"
        },

        // ======================== Karnok (4 套) ========================
        new CommunityBlueprintSpec
        {
            BlueprintId = "Karnok_BP_Anaconda_Rage",
            Hero = HeroNames.Karnok,
            ArchetypeId = "Karnok_Rage_Anaconda_Cleaver",
            TitleZh = "巨蟒九头蛇怒火暴走流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Bear Mask", "Hydra Tree", "Anaconda", "Battle Axe", "Adrenaline Shot"],
            SkillInternalNames = ["Blind Fury", "Burst of Rage", "Adrenaline Rush", "Piercing Rage"],
            EnchantedItems = Ench(("Anaconda", "Obsidian"), ("Battle Axe", "Deadly")),
            ScalingNote = "九头蛇树降低暴怒阈值并提供暴怒多重释放，搭配熊面具与肾上腺素注射高频暴怒，巨蟒按怒火总量打出双倍连击。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Karnok_BP_OutlandsTerror_Berserk",
            Hero = HeroNames.Karnok,
            ArchetypeId = "Karnok_Rage_Anaconda_Cleaver",
            TitleZh = "荒原恐兽行刑者狂暴斩",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Outlands Terror", "Executioner's Cleaver", "Vengeful Sigil", "Forest Cloak"],
            SkillInternalNames = ["Peak Rage", "Brawler", "Bloodhound", "Adrenaline Rush"],
            EnchantedItems = Ench(("Executioner's Cleaver", "Deadly"), ("Outlands Terror", "Turbo")),
            ScalingNote = "荒原恐兽使获怒翻倍且暴怒期间自带吸血减 CD，配合复仇符印疯狂攒怒，行刑者斩肉刀在敌人半血后双倍暴击斩杀。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Karnok_BP_Geyser_BurnRage",
            Hero = HeroNames.Karnok,
            ArchetypeId = "Karnok_Burn_Geyser_FlameSigil",
            TitleZh = "间歇泉烈焰符印怒焰流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Torch", "Tinderbox", "Geyser", "Flame Sigil", "Burn Scar", "Signal Fire"],
            SkillInternalNames = ["Burning Rage", "Burning Temper", "Living Flame", "Moth to a Flame"],
            EnchantedItems = Ench(("Geyser", "Fiery"), ("Burn Scar", "Turbo")),
            ScalingNote = "火绒盒、火把与灼烧疤痕的每次灼烧都喂给烈焰符印转化为怒火，进入暴怒瞬间触发间歇泉大额百分比灼烧。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Karnok_BP_GreatEagle_Flying",
            Hero = HeroNames.Karnok,
            ArchetypeId = "Karnok_Flying_GreatEagle_Hawk",
            TitleZh = "巨鹰猎鹰羽翼符印空袭",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Wing Sigil", "Great Eagle", "Hunting Hawk", "Messenger Sparrow", "Flying Squirrel", "Bat"],
            SkillInternalNames = ["Death from Above", "Aerial Assault", "Bombing Run", "Swift Temper"],
            EnchantedItems = Ench(("Great Eagle", "Obsidian"), ("Hunting Hawk", "Deadly")),
            ScalingNote = "羽翼符印与猎鹰将全队野兽伙伴拉入飞行状态并大幅缩减飞行冷却，巨鹰直接为全飞行阵容提供百分比增伤与护盾光环。"
        },

        // ======================== Jules (4 套) ========================
        new CommunityBlueprintSpec
        {
            BlueprintId = "Jules_BP_Nullfrost_Freezer",
            Hero = HeroNames.Jules,
            ArchetypeId = "Jules_Chilled_Freezer_Slushee",
            TitleZh = "霜绝祭坛冷库永冻冰沙",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Nullfrost Altar", "Slushee", "Walk-In Freezer", "Sorbet", "Ice Swan"],
            SkillInternalNames = ["Flash Freeze", "Chilled Delights", "Shatter", "Coolant Leak"],
            EnchantedItems = Ench(("Walk-In Freezer", "Icy"), ("Ice Swan", "Shielded")),
            ScalingNote = "霜绝祭坛令己方免疫冻结并每次触发冻结全局加攻加盾，冰沙与雪葩紧贴冷库两侧触发冻结与冰天鹅多重爆发。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Jules_BP_GiantLollipop_HeatedSlow",
            Hero = HeroNames.Jules,
            ArchetypeId = "Jules_Slow_Luau_Bubblegum",
            TitleZh = "巨型棒棒糖高温减速宴",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Giant Lollipop", "Scorchpepper", "Luau", "Excellent Vintage", "Curry"],
            SkillInternalNames = ["Dirty Dishes", "Slow Burn", "Slow and Steady", "Piping Hot"],
            EnchantedItems = Ench(("Giant Lollipop", "Obsidian"), ("Luau", "Heavy")),
            ScalingNote = "火焰辣椒直接赋予左侧巨型棒棒糖高温状态激活群体减速，串联热带野宴回血与咖喱充能打出稳定控场输出。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Jules_BP_Grill_JumboWok_Burn",
            Hero = HeroNames.Jules,
            ArchetypeId = "Jules_Heated_Grill_Wok_Burn",
            TitleZh = "烧烤架烤箱巨型炒锅灼烧",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Hot Sauce", "Grill", "Jumbo Wok", "Oven"],
            SkillInternalNames = ["Rolling Boil", "Hot Meal", "Conflagration", "Searing Flames"],
            EnchantedItems = Ench(("Jumbo Wok", "Fiery"), ("Grill", "Deadly")),
            ScalingNote = "烧烤架加热两侧的辣酱与巨型炒锅，烤箱进一步赋予高温物品额外多重释放，实现高频高温叠烧碾压。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Jules_BP_Cleaver_Kitchen_Crit",
            Hero = HeroNames.Jules,
            ArchetypeId = "Jules_Tool_Dishwasher_KnifeSet",
            TitleZh = "洗碗机菜刀厨具暴击流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Dishwasher", "Cutlery Forge", "Knife Set", "Cleaver", "Santoku"],
            SkillInternalNames = ["Re-Tooled", "Tools of the Trade", "Plowshares to Swords", "The Right Tool"],
            EnchantedItems = Ench(("Knife Set", "Deadly"), ("Cleaver", "Obsidian")),
            ScalingNote = "洗碗机加速全部工具武器并拉起高温增伤，餐具锻造炉联动刀具组、华丽菜刀和三德刀在高频使用中指数级加伤。"
        },

        // ======================== Stelle (4 套) ========================
        new CommunityBlueprintSpec
        {
            BlueprintId = "Stelle_BP_CruiseShip_LightningRod",
            Hero = HeroNames.Stelle,
            ArchetypeId = "Stelle_Slow_CruiseShip_LightningRod",
            TitleZh = "避雷针游轮雷暴减速流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Lightning Rod", "Cruise Ship", "Lightning Focuser", "Inertial Dampener", "Multitool"],
            SkillInternalNames = ["Outmaneuver", "Slow and Steady", "Temporal Strike", "Slowed Targets"],
            EnchantedItems = Ench(("Lightning Rod", "Obsidian"), ("Cruise Ship", "Heavy")),
            ScalingNote = "游轮与惯性阻尼器主动频繁触发减速，持续引爆避雷针高额雷击伤害并同时充能减速中的闪电聚焦器。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Stelle_BP_SpaceLaser_Observatory",
            Hero = HeroNames.Stelle,
            ArchetypeId = "Stelle_SpaceLaser_Observatory_Charge",
            TitleZh = "天文台聚光灯太空激光炮",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Space Laser", "Spotlight", "Observatory", "Lightning Butterfly", "Pinwheel"],
            SkillInternalNames = ["Big Guns", "Big Numbers", "Juggler", "Haunting Flight"],
            EnchantedItems = Ench(("Space Laser", "Deadly"), ("Observatory", "Turbo")),
            ScalingNote = "聚光灯将左侧太空激光基础伤害直接翻倍，闪电蝴蝶开局起飞配合纸风车与天文台持续加速充能太空激光发射。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Stelle_BP_BalloonEngine_MoltenBurn",
            Hero = HeroNames.Stelle,
            ArchetypeId = "Stelle_Burn_Balloon_Molten",
            TitleZh = "气球引擎熔火弹飞行灼烧",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Balloon Engine", "Battle Balloon", "Molten Ball Blaster", "Pressure Heater", "Flame Jet Drone", "Solar Drone"],
            SkillInternalNames = ["Start-Up Sequence", "Bombs Away!", "Hot Wheels", "Hot Air"],
            EnchantedItems = Ench(("Balloon Engine", "Fiery"), ("Molten Ball Blaster", "Turbo")),
            ScalingNote = "起步流程在开局瞬间拉起载具飞行激活气球引擎叠烧并充能作战气球，压力热炉与双无人机持续叠加飞行灼烧爆发。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Stelle_BP_OblivionCannon_Saucer",
            Hero = HeroNames.Stelle,
            ArchetypeId = "Stelle_Destroy_Oblivion_Saucer",
            TitleZh = "湮灭火炮飞碟自毁重构",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Oblivion Cannon", "Boosted Saucer", "Salvage Yard", "Boom Boom Bot"],
            SkillInternalNames = ["Leverage Momentum", "Master Mechanic", "Get Back In There!", "Energy Transference"],
            EnchantedItems = Ench(("Oblivion Cannon", "Obsidian"), ("Boosted Saucer", "Deadly")),
            ScalingNote = "爆竹机器人起飞自毁后由拆卸场与修复技师秒速修复并叠盾，同时无限充能并堆高湮灭火炮和强化飞碟伤害。"
        },

        // ======================== Hero8 (4 套) ========================
        new CommunityBlueprintSpec
        {
            BlueprintId = "Hero8_BP_Guzheng_Crit_Tempo",
            Hero = HeroNames.Hero8,
            ArchetypeId = "Hero8_Guzheng_Crit_Tempo",
            TitleZh = "古筝手提音响暴击节奏流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Black Lipstick", "Tambourine", "Boombox", "Guzheng", "Multimeter", "Razor Bow", "Straight Edge"],
            SkillInternalNames = ["Critical Sound", "Alacrity", "Building Crescendo", "Debut Album"],
            EnchantedItems = Ench(("Guzheng", "Obsidian"), ("Boombox", "Shielded")),
            ScalingNote = "黑色唇膏与首张专辑提供开局节奏，手提音响、万用表与铃鼓将暴击叠满，每次暴击疯狂攒节奏推高古筝攻防双倍率。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Hero8_BP_Ignitar_PipeOrgan_Burn",
            Hero = HeroNames.Hero8,
            ArchetypeId = "Hero8_Burn_Ignitar_Scorch",
            TitleZh = "炽热吉他管风琴灼烧节奏",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Black Lipstick", "Confetti Cannon", "Ignitar", "Pipe Organ", "Keytar", "Dragon Lighter"],
            SkillInternalNames = ["Tremolo", "B Major", "B Minor", "Conflagration"],
            EnchantedItems = Ench(("Ignitar", "Fiery"), ("Pipe Organ", "Turbo")),
            ScalingNote = "彩带炮、键盘吉他与巨龙打火机在乐器互动中滚起节奏与灼烧，每积攒 10 点节奏便为炽热吉他永久追加多重释放。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Hero8_BP_MechaDragon_Superfan",
            Hero = HeroNames.Hero8,
            ArchetypeId = "Hero8_Dragon_Mecha_Superfan",
            TitleZh = "机甲巨龙超级粉丝多重流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Mecha Dragon", "Talon Earrings", "Superfan", "Bomber Jacket", "Dragonheart Pendant", "Smokey"],
            SkillInternalNames = ["Honorary Dragons", "Draconic Might", "Draconic Resolve", "Big Guns"],
            EnchantedItems = Ench(("Superfan", "Deadly"), ("Bomber Jacket", "Shielded")),
            ScalingNote = "利爪耳环对左侧机甲巨龙双倍充能并触发飞行与战斗附魔，飞行员夹克、龙心吊坠和超级粉丝吃满全队巨龙多重联动。"
        },
        new CommunityBlueprintSpec
        {
            BlueprintId = "Hero8_BP_DragRacer_Synth_Haste",
            Hero = HeroNames.Hero8,
            ArchetypeId = "Hero8_Haste_DragRacer_Blades",
            TitleZh = "直线加速车合成器飙车流",
            SourceNote = DefaultSourceNote,
            ItemInternalNames = ["Black Lipstick", "Blades", "Drag Racer", "Synth", "Turner"],
            SkillInternalNames = ["Debut Album", "F Major", "Static Acceleration", "Streamline Weapon"],
            EnchantedItems = Ench(("Drag Racer", "Obsidian"), ("Blades", "Turbo")),
            ScalingNote = "黑色唇膏、首张专辑与特纳提供充沛节奏并触发高频加速，合成器缩短左侧直线加速车冷却使其消耗节奏反复翻倍重击。"
        }
    ];
}
