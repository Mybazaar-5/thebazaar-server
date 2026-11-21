using static BazaarCore.Dataset.Archetypes.ArchetypeCatalog;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 卡诺克（Karnok）的 12 套社区主流与高协同流派蓝图。
/// </summary>
internal static class KarnokArchetypes
{
    public static IReadOnlyList<ArchetypeDefinition> Build() =>
    [
        new ArchetypeDefinition
        {
            Id = "Karnok_Rage_Anaconda_Cleaver",
            Hero = "Karnok",
            NameZh = "巨蟒行刑者纯怒暴走流",
            DescriptionZh = "通过熊面具、复仇符印和九头蛇树快速攒满怒火并高频进入暴怒，使巨蟒获得多重释放与双倍怒火直伤。",
            CoreItemNames = ["Anaconda", "Executioner's Cleaver", "Outlands Terror", "Hydra Tree"],
            SupportItemNames = ["Tree Club", "Battle Axe", "Sawpike", "Bear Mask", "Vengeful Sigil", "Adrenaline Shot"],
            CoreSkillNames = ["Blind Fury", "Burst of Rage", "Adrenaline Rush", "Piercing Rage", "Peak Rage"],
            PreferredTags = Dict(("Weapon", 3.2), ("Friend", 2.0), ("Property", 1.5), ("Apparel", 1.5)),
            PreferredHiddenTags = Dict(("Rage", 3.5), ("RageReference", 3.5), ("Damage", 2.8), ("Crit", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnPlayerEnraged", 3.4), ("TTriggerOnPlayerAttributeChanged", 2.4), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Obsidian", 3.2), ("Deadly", 3.0), ("Turbo", 2.6), ("Radiant", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.RightMost],
            EarlyFocusTags = ["Rage", "Weapon", "Damage"],
            EndgamePowerTag = "RageTotalScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_Sling_Ammo_Enrage",
            Hero = "Karnok",
            NameZh = "巨型投石索暴怒五倍弹药流",
            DescriptionZh = "利用弹药带与暴怒装填机制持续补弹，让暴怒期间五倍伤害的巨型投石索与猎枪打出毁灭齐射。",
            CoreItemNames = ["Giant Sling", "Hunting Rifle", "Bandoleer"],
            SupportItemNames = ["Shotgun", "Heavy Crossbow", "Wrist Crossbow", "Utility Belt", "Adrenaline Shot", "Log Trap"],
            CoreSkillNames = ["Frenzy Cache", "Gunner", "Flashy Reload", "Parting Shot", "Frontline Logistics"],
            PreferredTags = Dict(("Weapon", 3.2), ("Apparel", 2.0), ("Tool", 1.6)),
            PreferredHiddenTags = Dict(("Ammo", 3.2), ("RageReference", 3.0), ("Damage", 2.8), ("AmmoReference", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnPlayerEnraged", 3.0), ("TTriggerOnPlayerEnrageEnded", 2.2), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Obsidian", 3.2), ("Deadly", 3.0), ("Shiny", 2.6), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Ammo", "Rage", "Weapon"],
            EndgamePowerTag = "EnrageAmmoMultiplier"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_Crit_DualReaver_Claymore",
            Hero = "Karnok",
            NameZh = "掠夺双刃符文阔剑暴击流",
            DescriptionZh = "狼面具、鹰之符印和长矛互相叠满暴击率，把暴击直接转化为掠夺双刃和符文阔剑的五倍爆发伤害。",
            CoreItemNames = ["Dual Reaver", "Runic Claymore", "Eagle Sigil"],
            SupportItemNames = ["Wolf Mask", "Wrist Blade", "Spear", "Wolverine", "Hunter's Journal", "Pocket Knife"],
            CoreSkillNames = ["Ferocious Bite", "Foreboding Winds", "Alacrity", "Building Crescendo", "Deadly Eye"],
            PreferredTags = Dict(("Weapon", 3.2), ("Apparel", 2.0), ("Relic", 1.8), ("Friend", 1.6)),
            PreferredHiddenTags = Dict(("Crit", 3.5), ("CritReference", 3.4), ("Damage", 2.6), ("HasteReference", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardCritted", 3.4), ("TTriggerOnPlayerEnraged", 2.2), ("TTriggerOnCardPerformedHaste", 2.0)),
            PreferredEnchantments = Dict(("Deadly", 3.5), ("Obsidian", 3.0), ("Turbo", 2.6), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.FastTriggerLeftOfScaler, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Crit", "Weapon", "Haste"],
            EndgamePowerTag = "CritToDamageLoop"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_MaxHP_WildBear_LostBlade",
            Hero = "Karnok",
            NameZh = "荒原巨熊高血压制流",
            DescriptionZh = "通过野猪面具、森林斗篷与失落之刃叠加超高最大生命值，利用隐秘之湖半血倍速让巨熊一掌破敌。",
            CoreItemNames = ["Wild Bear", "Lost Blade", "Hidden Lake"],
            SupportItemNames = ["Assault Sigil", "Boar Mask", "Forest Cloak", "Resistance Bands", "Bear Mask", "Championship Belt"],
            CoreSkillNames = ["Brawler", "Arms Dealer", "Calming Vitality", "Healthy Heart", "All Talk"],
            PreferredTags = Dict(("Weapon", 2.6), ("Apparel", 2.2), ("Property", 1.8), ("Friend", 1.6)),
            PreferredHiddenTags = Dict(("Health", 3.5), ("HealthReference", 3.4), ("RageReference", 2.2), ("Damage", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnPlayerEnraged", 2.6), ("TTriggerOnPlayerAttributeChanged", 2.4), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Obsidian", 3.0), ("Shielded", 2.6), ("Restorative", 2.4), ("Radiant", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Health", "Weapon", "Rage"],
            EndgamePowerTag = "MaxHealthEnrageSmash"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_Shield_WildBoar_Pauldrons",
            Hero = "Karnok",
            NameZh = "野猪肩铠怒盾铁壁流",
            DescriptionZh = "喀斯特配肩铠在暴怒期间把护盾数值翻倍暴涨，由野猪和刃甲将坚固防线同步化作高频反击重拳。",
            CoreItemNames = ["Wild Boar", "Pauldrons", "Karst"],
            SupportItemNames = ["Bladed Armor", "Hunter's Blind", "Stretch Pants", "Security Blanket", "Warding Glyphs", "Infused Arm Wraps"],
            CoreSkillNames = ["Sharp Corners", "Electrified Hull", "Defensive Stance", "Hard Shell", "Heavy Mettle"],
            PreferredTags = Dict(("Apparel", 2.8), ("Weapon", 2.0), ("Property", 1.8), ("Friend", 1.6)),
            PreferredHiddenTags = Dict(("Shield", 3.4), ("ShieldReference", 3.2), ("RageReference", 2.6), ("Damage", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnPlayerEnraged", 2.6), ("TTriggerOnCardPerformedShield", 2.8), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Shielded", 3.4), ("Obsidian", 2.8), ("Restorative", 2.2), ("Heavy", 2.0)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.ShieldOnEdge],
            EarlyFocusTags = ["Shield", "Rage", "Apparel"],
            EndgamePowerTag = "RageShieldSlam"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_Friend_BeastPack",
            Hero = "Karnok",
            NameZh = "狼群蜜獾伙伴暴怒流",
            DescriptionZh = "围绕狼、蜜獾与狼獾搭建纯正野兽伙伴军团，靠获怒充能与兽笛加速形成连绵不绝的群兽扑杀。",
            CoreItemNames = ["Wolf", "Honey Badger", "Wolverine"],
            SupportItemNames = ["Beast Call", "Caracal", "Unibou", "Dryad", "Spiked Collar", "Hunting Knife"],
            CoreSkillNames = ["Feral Alliance", "Beautiful Friendship", "Balanced Friendship", "Weapon Bond", "Lone Charge"],
            PreferredTags = Dict(("Friend", 3.5), ("Weapon", 2.6), ("Tool", 1.4)),
            PreferredHiddenTags = Dict(("Damage", 2.6), ("Haste", 2.8), ("RageReference", 2.4), ("HasteReference", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnPlayerEnraged", 2.6), ("TTriggerOnCardPerformedHaste", 2.6), ("TTriggerOnItemUsed", 2.2)),
            PreferredEnchantments = Dict(("Turbo", 3.0), ("Deadly", 2.8), ("Obsidian", 2.6), ("Shiny", 2.2)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Friend", "Weapon", "Haste"],
            EndgamePowerTag = "FriendRageCharge"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_Slow_Trapvine_Boa",
            Hero = "Karnok",
            NameZh = "陷阱藤蚺蛇猎手减速流",
            DescriptionZh = "利用猎人之靴、锁链与夜视仪主动减速己方件换取高暴击与怒火，同时用陷阱藤与原木陷阱封锁敌手。",
            CoreItemNames = ["Trapvine", "Boa Constrictor", "Hunter's Boots"],
            SupportItemNames = ["Tri-Net", "Enervating Sigil", "Chains", "Night Vision", "Bear Trap", "Trapping Pit"],
            CoreSkillNames = ["Slow and Steady", "Temporal Strike", "Slowed Targets", "Paralyzing Rush", "Creeping Chill"],
            PreferredTags = Dict(("Trap", 2.8), ("Weapon", 2.4), ("Tool", 2.0), ("Friend", 1.8)),
            PreferredHiddenTags = Dict(("Slow", 3.5), ("SlowReference", 3.4), ("RageReference", 2.2), ("Damage", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedSlow", 3.5), ("TTriggerOnPlayerEnraged", 2.2), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Heavy", 3.4), ("Toxic", 2.6), ("Obsidian", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Slow", "Trap", "Weapon"],
            EndgamePowerTag = "SelfEnemySlowEngine"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_Burn_Geyser_FlameSigil",
            Hero = "Karnok",
            NameZh = "间歇泉烈焰符印灼烧怒焰流",
            DescriptionZh = "借助灼烧疤痕与烈焰符印让灼烧不断累积怒火，进入与退出暴怒时同时引爆间歇泉和萤火灯笼。",
            CoreItemNames = ["Geyser", "Burn Scar", "Flame Sigil"],
            SupportItemNames = ["Torch", "Tinderbox", "Signal Fire", "Firefly Lantern", "Flare", "Flamespitter Frog"],
            CoreSkillNames = ["Burning Rage", "Burning Temper", "Burning Infernal", "Living Flame", "Moth to a Flame"],
            PreferredTags = Dict(("Tool", 2.6), ("Property", 2.2), ("Friend", 1.6)),
            PreferredHiddenTags = Dict(("Burn", 3.5), ("BurnReference", 3.2), ("RageReference", 2.8), ("HealthReference", 1.8)),
            PreferredTriggers = Dict(("TTriggerOnPlayerEnraged", 3.0), ("TTriggerOnCardPerformedBurn", 2.8), ("TTriggerOnPlayerEnrageEnded", 2.2)),
            PreferredEnchantments = Dict(("Fiery", 3.5), ("Turbo", 2.6), ("Shiny", 2.4), ("Restorative", 2.0)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.RightMost],
            EarlyFocusTags = ["Burn", "Rage", "Tool"],
            EndgamePowerTag = "EnrageBurnExplosion"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_Poison_WorryWart_Frog",
            Hero = "Karnok",
            NameZh = "忧虑疣蛙毒曼巴剧毒怒流",
            DescriptionZh = "以剧毒符印将最大生命值转化为全队剧毒加成，搭配黑曼巴、喷火蛙与腐蚀溶剂双倍放大毒伤。",
            CoreItemNames = ["Toxic Sigil", "Worry Wart", "Black Mamba"],
            SupportItemNames = ["Caustic Solvent", "Flamespitter Frog", "Drowsdrop", "Frog Hollow", "Lichen", "Trapvine"],
            CoreSkillNames = ["Blight Rage", "Blight Temper", "Toxic Flame", "Poison Forge", "Improved Toxins"],
            PreferredTags = Dict(("Friend", 2.8), ("Potion", 2.0), ("Property", 1.6), ("Food", 1.4)),
            PreferredHiddenTags = Dict(("Poison", 3.5), ("PoisonReference", 3.2), ("RageReference", 2.6), ("Slow", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnPlayerEnraged", 2.8), ("TTriggerOnCardPerformedPoison", 2.8), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Toxic", 3.5), ("Heavy", 2.6), ("Fiery", 2.2), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Poison", "Rage", "Slow"],
            EndgamePowerTag = "EnragePoisonScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_Flying_GreatEagle_Hawk",
            Hero = "Karnok",
            NameZh = "巨鹰猎鹰羽翼符印空袭流",
            DescriptionZh = "羽翼符印与猎鹰在加速和暴怒时拉起全队飞行，激活巨鹰的飞行百分比伤盾光环，完成高空俯冲打击。",
            CoreItemNames = ["Great Eagle", "Hunting Hawk", "Wing Sigil"],
            SupportItemNames = ["Messenger Sparrow", "Flying Squirrel", "Bat", "Cosmic Plumage", "Cosmic Amulet", "Admiral's Badge"],
            CoreSkillNames = ["Death from Above", "Aerial Assault", "Bombing Run", "Scramble Fighters", "Swift Temper"],
            PreferredTags = Dict(("Friend", 2.8), ("Weapon", 2.4), ("Vehicle", 1.8), ("Relic", 1.4)),
            PreferredHiddenTags = Dict(("Flying", 3.5), ("FlyingReference", 3.2), ("Haste", 2.4), ("RageReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnPlayerEnraged", 2.8), ("TTriggerOnCardStartedFlying", 2.6), ("TTriggerOnCardPerformedHaste", 2.4)),
            PreferredEnchantments = Dict(("Turbo", 3.0), ("Deadly", 2.8), ("Obsidian", 2.6), ("Shielded", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Flying", "Friend", "Haste"],
            EndgamePowerTag = "FlyingEnrageDive"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_Heal_AuroraVista_Dryad",
            Hero = "Karnok",
            NameZh = "极光仙女治疗重击流",
            DescriptionZh = "树精、营火与极光之景将暴怒消耗与治疗循环彻底打通，每次治疗同步生成护盾并反哺宁静符印全队增益。",
            CoreItemNames = ["Aurora Vista", "Dryad", "Campfire"],
            SupportItemNames = ["Tranquil Sigil", "Bagpipes", "Lifting Stones", "Fairies", "Waterskin", "River Otter"],
            CoreSkillNames = ["Calming Vitality", "Calming Heals", "Medic's Wrath", "Medic's Shield", "Still Focus"],
            PreferredTags = Dict(("Property", 2.4), ("Friend", 2.2), ("Tool", 1.8), ("Food", 1.4)),
            PreferredHiddenTags = Dict(("Heal", 3.4), ("HealReference", 3.2), ("RageReference", 2.6), ("Shield", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedHeal", 3.2), ("TTriggerOnPlayerEnraged", 2.6), ("TTriggerOnPlayerEnrageEnded", 2.0)),
            PreferredEnchantments = Dict(("Restorative", 3.4), ("Shielded", 2.6), ("Deadly", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Heal", "Rage", "Property"],
            EndgamePowerTag = "HealShieldConversion"
        },
        new ArchetypeDefinition
        {
            Id = "Karnok_Freeze_FairyCircle_SnowWisp",
            Hero = "Karnok",
            NameZh = "仙女环雪魂减速冻结控制流",
            DescriptionZh = "利用仙女环对减速和冻结己方物品的充能回血反制，结合雪魂精灵、仙灵与安心毯边控场边高速获怒。",
            CoreItemNames = ["Fairy Circle", "Snow Wisp", "Furs"],
            SupportItemNames = ["Tent", "Fairies", "Snowshoes", "Security Blanket", "Trail Markers", "Nullfrost Altar"],
            CoreSkillNames = ["Shatter", "Fairy Haze", "Invigorating Cold", "Reaching the Summit", "Tranquil Purge"],
            PreferredTags = Dict(("Property", 2.4), ("Apparel", 2.4), ("Friend", 2.2), ("Relic", 1.6)),
            PreferredHiddenTags = Dict(("Freeze", 3.2), ("FreezeReference", 3.0), ("SlowReference", 2.6), ("Heal", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedFreeze", 3.2), ("TTriggerOnCardPerformedSlow", 2.6), ("TTriggerOnPlayerEnraged", 2.0)),
            PreferredEnchantments = Dict(("Icy", 3.4), ("Restorative", 2.6), ("Heavy", 2.4), ("Radiant", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Freeze", "Slow", "Heal"],
            EndgamePowerTag = "SelfControlFreezeLoop"
        }
    ];
}
