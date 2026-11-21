using static BazaarCore.Dataset.Archetypes.ArchetypeCatalog;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 瓦内莎（Vanessa）的 12 套社区主流与高协同流派蓝图。
/// </summary>
internal static class VanessaArchetypes
{
    public static IReadOnlyList<ArchetypeDefinition> Build() =>
    [
        new ArchetypeDefinition
        {
            Id = "Vanessa_Crit_Ammo_Burst",
            Hero = "Vanessa",
            NameZh = "暴击弹药连射流",
            DescriptionZh = "以弩炮、刺刀手枪和连发步枪为核心，通过弹药装填与高暴击快速倾泻爆发伤害。",
            CoreItemNames = ["Ballista", "Pistol Sword", "Repeater"],
            SupportItemNames = ["Port", "Double Barrel", "Revolver", "Ramrod", "Cannonball", "Powder Horn"],
            CoreSkillNames = ["Flashy Reload", "Gunner", "Loaded Fury", "Parting Shot", "Frontline Logistics"],
            PreferredTags = Dict(("Weapon", 3.0), ("Tool", 1.6), ("Property", 1.4)),
            PreferredHiddenTags = Dict(("Ammo", 3.0), ("AmmoReference", 2.8), ("Crit", 2.4), ("Damage", 2.5)),
            PreferredTriggers = Dict(("TTriggerOnCardFired", 1.5), ("TTriggerOnItemUsed", 2.4), ("TTriggerOnCardCritted", 2.0)),
            PreferredEnchantments = Dict(("Deadly", 3.0), ("Obsidian", 2.6), ("Shiny", 2.4), ("Turbo", 2.0)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore, PositioningRule.FastTriggerLeftOfScaler],
            EarlyFocusTags = ["Weapon", "Ammo", "Crit"],
            EndgamePowerTag = "AmmoCritBurst"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_Aquatic_Puffer_Poison",
            Hero = "Vanessa",
            NameZh = "水车河豚剧毒流",
            DescriptionZh = "利用水车与船舵的高频水系加速不断充能河豚与毒须鲶，迅速叠加致命剧毒。",
            CoreItemNames = ["Pufferfish", "Water Wheel", "Catfish"],
            SupportItemNames = ["Jellyfish", "Beach Ball", "Astrolabe", "Captain's Wheel", "Dive Weights", "Seaweed"],
            CoreSkillNames = ["Improved Toxins", "Exposing Toxins", "Toxicity", "Toxin Injector", "Barnacle Crusted"],
            PreferredTags = Dict(("Aquatic", 3.2), ("Friend", 2.2), ("Property", 1.5)),
            PreferredHiddenTags = Dict(("Poison", 3.0), ("Haste", 2.8), ("HasteReference", 2.6), ("PoisonReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedHaste", 3.0), ("TTriggerOnItemUsed", 2.0), ("TTriggerOnCardFired", 1.5)),
            PreferredEnchantments = Dict(("Toxic", 3.0), ("Turbo", 2.8), ("Heavy", 2.0), ("Shiny", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Aquatic", "Poison", "Haste"],
            EndgamePowerTag = "HastePoisonLoop"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_Single_Weapon_Lifesteal",
            Hero = "Vanessa",
            NameZh = "鸦巢单核吸血秒杀流",
            DescriptionZh = "依靠鸦巢、定制准镜与消音器把全部资源堆砌到唯一武器上，兼具吸血续航与极限单发伤害。",
            CoreItemNames = ["Crow's Nest", "Sniper Rifle", "Submarine"],
            SupportItemNames = ["Custom Scope", "Suppressor", "Star Chart", "Swash Buckle", "Rowboat", "Life Preserver"],
            CoreSkillNames = ["Quality over Quantity", "Sharpshooter", "Circle of Life", "One Shot, One Kill", "Depth Charge"],
            PreferredTags = Dict(("Property", 2.0), ("Tool", 2.0), ("Tech", 1.8), ("Aquatic", 1.6)),
            PreferredHiddenTags = Dict(("Crit", 3.0), ("Cooldown", 2.6), ("Lifesteal", 2.5), ("Damage", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnCardCritted", 2.5), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Deadly", 3.0), ("Obsidian", 2.8), ("Radiant", 2.6), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.RightMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Weapon", "Crit"],
            EndgamePowerTag = "SingleWeaponLifesteal"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_Burn_Trebuchet",
            Hero = "Vanessa",
            NameZh = "急速抛石机灼烧流",
            DescriptionZh = "通过快速武器和加速牌疯狂给抛石机与火炮阵列充能，配合恶鬼面具与燃烧子弹融化对手。",
            CoreItemNames = ["Trebuchet", "Cannonade", "Blunderbuss"],
            SupportItemNames = ["Zoarcid", "Volcanic Vents", "Bonfire", "Incendiary Rounds", "Lighter", "Captain's Wheel"],
            CoreSkillNames = ["Conflagration", "Searing Flames", "Heated Shells", "Flashpoint Ignition", "Quick Ignition"],
            PreferredTags = Dict(("Weapon", 2.5), ("Aquatic", 1.6), ("Tool", 1.4)),
            PreferredHiddenTags = Dict(("Burn", 3.0), ("HasteReference", 2.4), ("BurnReference", 2.4), ("Damage", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedBurn", 2.6), ("TTriggerOnCardPerformedHaste", 2.4), ("TTriggerOnItemUsed", 2.2)),
            PreferredEnchantments = Dict(("Fiery", 3.0), ("Turbo", 2.6), ("Shiny", 2.2), ("Heavy", 1.8)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.FastTriggerLeftOfScaler],
            EarlyFocusTags = ["Burn", "Weapon", "Haste"],
            EndgamePowerTag = "HasteBurnCharge"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_Slow_Lighthouse",
            Hero = "Vanessa",
            NameZh = "灯塔减速灼烧控场流",
            DescriptionZh = "拍立蚌、码头缆索与绊索全覆盖减速敌方节奏，同时持续触发灯塔灼烧与幽渊鮟鱇充能。",
            CoreItemNames = ["Lighthouse", "Darkwater Anglerfish", "Electric Eels"],
            SupportItemNames = ["Dock Lines", "Clamera", "Tripwire", "IllusoRay", "Jitte", "Spyglass"],
            CoreSkillNames = ["Slow Burn", "Slow and Steady", "Slowed Targets", "Creeping Chill", "Creeping Toxins"],
            PreferredTags = Dict(("Aquatic", 2.6), ("Property", 1.8), ("Trap", 1.8)),
            PreferredHiddenTags = Dict(("Slow", 3.2), ("SlowReference", 3.0), ("Burn", 2.2), ("Cooldown", 1.8)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedSlow", 3.2), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Heavy", 3.0), ("Fiery", 2.5), ("Icy", 2.2), ("Turbo", 2.0)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.LeftMost],
            EarlyFocusTags = ["Slow", "Aquatic", "Burn"],
            EndgamePowerTag = "SlowBurnControl"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_Flagship_Multicast",
            Hero = "Vanessa",
            NameZh = "旗舰全标签多重连击流",
            DescriptionZh = "凑齐工具、地产、伙伴、弹药与遗物五类标签喂满旗舰多重释放，配合船骸与船首像横扫战场。",
            CoreItemNames = ["Flagship", "Shipwreck", "Captain's Quarters"],
            SupportItemNames = ["Figurehead", "Star Chart", "Sharkray", "Rowboat", "Clamera", "Korxena Crest"],
            CoreSkillNames = ["Big Guns", "Command Ship", "Flanking Criticals", "Captain's Charge", "Deadly Waters (Skill)"],
            PreferredTags = Dict(("Aquatic", 3.0), ("Vehicle", 2.6), ("Property", 2.0), ("Relic", 1.8)),
            PreferredHiddenTags = Dict(("Damage", 2.6), ("Cooldown", 2.4), ("AmmoReference", 2.0), ("DamageReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardFired", 2.0), ("TTriggerOnItemUsed", 1.6)),
            PreferredEnchantments = Dict(("Obsidian", 2.8), ("Deadly", 2.8), ("Radiant", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.RightMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Aquatic", "Weapon", "Vehicle"],
            EndgamePowerTag = "MulticastFlagship"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_Friend_Tortuga",
            Hero = "Vanessa",
            NameZh = "巨龟伙伴急速齐射流",
            DescriptionZh = "以巨龟托图加与海狗沙龙为核心，搭配水母、食人鱼和鳐鲨构建高频率伙伴充能循环。",
            CoreItemNames = ["Tortuga", "Seadog's Saloon", "Card Table"],
            SupportItemNames = ["Sharkray", "Jellyfish", "Catfish", "Piranha", "Piano", "Calico"],
            CoreSkillNames = ["Beautiful Friendship", "Balanced Friendship", "Friend of the Sea (Skill)", "Denizens of the Deep", "Warm Hugs"],
            PreferredTags = Dict(("Friend", 3.2), ("Aquatic", 2.6), ("Instrument", 1.5)),
            PreferredHiddenTags = Dict(("Haste", 2.6), ("Damage", 2.4), ("HasteReference", 2.2), ("ShieldReference", 1.6)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 2.8), ("TTriggerOnCardPerformedHaste", 2.4)),
            PreferredEnchantments = Dict(("Turbo", 2.8), ("Shiny", 2.6), ("Deadly", 2.2), ("Toxic", 2.0)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Friend", "Aquatic", "Haste"],
            EndgamePowerTag = "FriendChargeSalvo"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_Oni_Crit_Burn",
            Hero = "Vanessa",
            NameZh = "恶鬼面具暴击灼烧流",
            DescriptionZh = "利用高暴击连动触发恶鬼面具全队增灼烧、赛博铁尺加伤与投掷飞刀免费追击。",
            CoreItemNames = ["Oni Mask", "Cyber-Sai", "Throwing Knives"],
            SupportItemNames = ["Cutlass", "Swash Buckle", "Integrated HUD", "Sextant", "Shoe Blade", "Korxena Crest"],
            CoreSkillNames = ["Improvised Burn", "Improvised Weaponry", "Alacrity", "Building Crescendo", "Tracer Fire"],
            PreferredTags = Dict(("Weapon", 2.8), ("Apparel", 2.0), ("Tech", 1.8)),
            PreferredHiddenTags = Dict(("Crit", 3.2), ("CritReference", 3.0), ("Burn", 2.4), ("Damage", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnCardCritted", 3.2), ("TTriggerOnCardFired", 1.6)),
            PreferredEnchantments = Dict(("Deadly", 3.2), ("Fiery", 2.8), ("Obsidian", 2.4), ("Turbo", 2.0)),
            PositioningRules = [PositioningRule.FastTriggerLeftOfScaler, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Crit", "Weapon", "Burn"],
            EndgamePowerTag = "CritBurnScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_Freeze_Primordial",
            Hero = "Vanessa",
            NameZh = "元初体冰山控制流",
            DescriptionZh = "通过冰山、雪怪蟹与元素深水炸弹频繁触发冻结、剧毒和灼烧，无限驱动沉眠元初体清场。",
            CoreItemNames = ["Slumbering Primordial", "Iceberg", "Elemental Depth Charge"],
            SupportItemNames = ["Weather Glass", "Yeti Crab", "Ice Pick", "Spyglass", "Icicle", "Dock Lines", "Nullfrost Altar"],
            CoreSkillNames = ["Shatter", "Reaching the Summit", "Frostfire", "Paralytic Poison", "Invigorating Cold"],
            PreferredTags = Dict(("Aquatic", 2.6), ("Relic", 2.0), ("Property", 1.6)),
            PreferredHiddenTags = Dict(("Freeze", 3.0), ("FreezeReference", 2.8), ("PoisonReference", 2.0), ("BurnReference", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedFreeze", 3.0), ("TTriggerOnCardPerformedBurn", 2.0), ("TTriggerOnCardPerformedPoison", 2.0)),
            PreferredEnchantments = Dict(("Icy", 3.2), ("Heavy", 2.4), ("Toxic", 2.2), ("Fiery", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Freeze", "Aquatic", "Slow"],
            EndgamePowerTag = "FreezeTriggerCharge"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_PowderKeg_OneShot",
            Hero = "Vanessa",
            NameZh = "火药桶巨石爆破斩杀流",
            DescriptionZh = "以火药桶、滚石与船锚为核心，借助灼烧充能与最大生命值百分比机制达成一击秒杀。",
            CoreItemNames = ["Powder Keg", "The Boulder", "Anchor"],
            SupportItemNames = ["Musket", "Cannon", "Incendiary Rounds", "Volcanic Vents", "Rowboat", "Life Preserver", "Swash Buckle", "Custom Scope"],
            CoreSkillNames = ["Big Numbers", "Double Down", "Execute", "Glass Cannon", "Bloodhound"],
            PreferredTags = Dict(("Weapon", 2.8), ("Relic", 1.8), ("Trap", 1.5)),
            PreferredHiddenTags = Dict(("HealthReference", 2.8), ("BurnReference", 2.4), ("Damage", 2.6), ("Crit", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedBurn", 2.6), ("TTriggerOnPlayerAttributeChanged", 2.0)),
            PreferredEnchantments = Dict(("Obsidian", 3.2), ("Deadly", 3.0), ("Fiery", 2.4), ("Radiant", 2.2)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.CoreCenter],
            EarlyFocusTags = ["Burn", "HealthReference", "Damage"],
            EndgamePowerTag = "PercentHpOneShot"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_Flying_Jetbike",
            Hero = "Vanessa",
            NameZh = "喷射摩托飞行水手流",
            DescriptionZh = "围绕喷射摩托、潜行滑翔机与带刃悬浮板，借助飞鱼与马龙鱼频繁起飞触发充能与减伤。",
            CoreItemNames = ["Jetbike", "Stealth Glider", "Bladed Hoverboard"],
            SupportItemNames = ["Flying Fish", "Marlon", "Pesky Pete", "Cosmic Amulet", "Cosmic Plumage", "Captain's Wheel"],
            CoreSkillNames = ["Aerial Assault", "Bombing Run", "Scramble Fighters", "Command Ship", "Outmaneuver"],
            PreferredTags = Dict(("Vehicle", 3.0), ("Weapon", 2.2), ("Tech", 1.8), ("Aquatic", 1.6)),
            PreferredHiddenTags = Dict(("Flying", 3.2), ("FlyingReference", 3.0), ("Haste", 2.0), ("Damage", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardStartedFlying", 2.8), ("TTriggerOnItemUsed", 2.4)),
            PreferredEnchantments = Dict(("Turbo", 2.8), ("Deadly", 2.6), ("Shielded", 2.2), ("Obsidian", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Flying", "Vehicle", "Weapon"],
            EndgamePowerTag = "FlyingVehicleCharge"
        },
        new ArchetypeDefinition
        {
            Id = "Vanessa_Cove_Eco_Julian",
            Hero = "Vanessa",
            NameZh = "海湾锁匣金币成长流",
            DescriptionZh = "前期靠温馨海湾、锁匣、钓鱼竿和铲子不断积累金币与局内价值，中后期通过橘利安与狼筅转化巨额护盾与战力。",
            CoreItemNames = ["Cove", "Lockbox", "Orange Julian"],
            SupportItemNames = ["Langxian", "Ambergris", "Wanted Poster", "Shovel", "Fishing Net", "Fishing Rod"],
            CoreSkillNames = ["Augmented Weaponry", "Augmented Defenses", "Iron Sharpens Iron", "Master Salesman", "Prosperity"],
            PreferredTags = Dict(("Aquatic", 2.4), ("Property", 2.2), ("Relic", 2.0), ("Tool", 1.6)),
            PreferredHiddenTags = Dict(("EconomyReference", 3.2), ("Value", 3.0), ("Shield", 2.2), ("DamageReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardSold", 2.8), ("TTriggerOnFightEnded", 2.6), ("TTriggerOnDayStarted", 2.2)),
            PreferredEnchantments = Dict(("Golden", 3.2), ("Shielded", 2.6), ("Obsidian", 2.4), ("Restorative", 2.0)),
            PositioningRules = [PositioningRule.ShieldOnEdge, PositioningRule.LeftMost],
            EarlyFocusTags = ["EconomyReference", "Value", "Property"],
            EndgamePowerTag = "EconomyScaling"
        }
    ];
}
