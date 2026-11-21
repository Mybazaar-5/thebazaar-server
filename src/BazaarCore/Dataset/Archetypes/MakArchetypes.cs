using static BazaarCore.Dataset.Archetypes.ArchetypeCatalog;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 马克（Mak）的 12 套社区主流与高协同流派蓝图。
/// </summary>
internal static class MakArchetypes
{
    public static IReadOnlyList<ArchetypeDefinition> Build() =>
    [
        new ArchetypeDefinition
        {
            Id = "Mak_Potion_Athanor_Burn",
            Hero = "Mak",
            NameZh = "炼金炉沸腾药水连爆流",
            DescriptionZh = "药水蒸馏厂减冷却加弹药，沸腾烧瓶赋予多重释放，炼金炉与药瓶发射器在狂甩药水中引爆灼烧与直伤。",
            CoreItemNames = ["Athanor", "Boiling Flask", "Vial Launcher"],
            SupportItemNames = ["Potion Distillery", "Bottled Explosion", "Fire Potion", "Bottled Lightning", "Energy Potion", "Recycling Bin"],
            CoreSkillNames = ["Explosive Potions", "Brewmaster", "Alchemical Precision", "Stocked", "The One Potion"],
            PreferredTags = Dict(("Potion", 3.5), ("Property", 2.0), ("Tool", 2.0), ("Weapon", 1.6)),
            PreferredHiddenTags = Dict(("PotionReference", 3.4), ("Ammo", 2.8), ("Burn", 2.4), ("AmmoReference", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.0), ("TTriggerOnCardPerformedReload", 2.4), ("TTriggerOnDayStarted", 2.0)),
            PreferredEnchantments = Dict(("Fiery", 3.0), ("Shiny", 2.8), ("Turbo", 2.6), ("Obsidian", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Potion", "Burn", "Ammo"],
            EndgamePowerTag = "PotionAmmoMulticast"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_PoppyField_Poison_Weapons",
            Hero = "Mak",
            NameZh = "罂粟花田剧毒武器流",
            DescriptionZh = "罂粟花田把剧毒层数等比例转化为全武器伤害，配合黏液链枷、瘟疫长柄刀与蜘蛛连枷速斩对手。",
            CoreItemNames = ["Poppy Field", "Goop Flail", "Plague Glaive"],
            SupportItemNames = ["Spider Mace", "Floor Spike", "Barbed Claws", "Basilisk Fang", "Death Caps", "Venom"],
            CoreSkillNames = ["Venomous Blade", "Poison Forge", "Toxic Weapons", "Biohazard", "Improved Toxins"],
            PreferredTags = Dict(("Weapon", 3.2), ("Property", 2.0), ("Relic", 1.8)),
            PreferredHiddenTags = Dict(("Poison", 3.2), ("Damage", 2.8), ("PoisonReference", 3.0), ("DamageReference", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 2.6), ("TTriggerOnCardPerformedPoison", 2.8)),
            PreferredEnchantments = Dict(("Toxic", 3.2), ("Obsidian", 2.8), ("Deadly", 2.6), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.FastTriggerLeftOfScaler],
            EarlyFocusTags = ["Poison", "Weapon", "Damage"],
            EndgamePowerTag = "PoisonWeaponScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_Regen_StaffOfMoose_Sunlight",
            Hero = "Mak",
            NameZh = "驼鹿角杖日光再生流",
            DescriptionZh = "先祖墓、秘密配方与生命导体叠高生命再生量，再通过驼鹿角杖和日光矛将其转换为巨额直伤与灼烧。",
            CoreItemNames = ["Staff of the Moose", "Sunlight Spear", "Tomb of the Ancients"],
            SupportItemNames = ["Soul Ring", "Secret Formula", "Life Conduit", "Philosopher's Stone", "Sunlight Vault", "Spiked Censer"],
            CoreSkillNames = ["Essence Overflow", "Moth to a Flame", "Venomous Vitality", "Vital Renewal", "Herbal Supplements"],
            PreferredTags = Dict(("Relic", 3.2), ("Property", 2.0), ("Weapon", 1.8)),
            PreferredHiddenTags = Dict(("Regen", 3.5), ("RegenReference", 3.2), ("DamageReference", 2.4), ("Burn", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedRegen", 2.8), ("TTriggerOnCardFired", 1.8), ("TTriggerOnPlayerAttributeChanged", 1.8)),
            PreferredEnchantments = Dict(("Mossy", 3.4), ("Fiery", 2.6), ("Toxic", 2.4), ("Radiant", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Regen", "Relic", "Burn"],
            EndgamePowerTag = "RegenOverflowBurst"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_Runic_Lifesteal_Crit",
            Hero = "Mak",
            NameZh = "符文巨斧吸血暴击流",
            DescriptionZh = "符文巨斧赋予全吸血武器高暴击率，符文之刃暴击翻倍伤害，搭配符文匕首与手术刀高速吸血斩杀。",
            CoreItemNames = ["Runic Great Axe", "Runic Blade", "Runic Double Bow"],
            SupportItemNames = ["Runic Daggers", "Runic Potion", "Mortar & Pestle", "Mortal Coil", "Basilisk Fang", "Scalpel"],
            CoreSkillNames = ["Bloodthirst", "Crimson Dash", "Hungry Blades", "Circle of Life", "Alacrity"],
            PreferredTags = Dict(("Weapon", 3.4), ("Relic", 2.8), ("Potion", 1.2)),
            PreferredHiddenTags = Dict(("Lifesteal", 3.4), ("Crit", 3.0), ("CritReference", 2.8), ("Damage", 2.6)),
            PreferredTriggers = Dict(("TTriggerOnCardCritted", 3.2), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Deadly", 3.4), ("Obsidian", 3.0), ("Shiny", 2.4), ("Radiant", 2.2)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Weapon", "Lifesteal", "Crit"],
            EndgamePowerTag = "LifestealCritMultiplier"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_Femur_Slow_Regen_Bonk",
            Hero = "Mak",
            NameZh = "马格努斯股骨减速重击流",
            DescriptionZh = "时间之砂、嗅盐、琥珀与马克显微镜高频率减速敌方，无限充能马格努斯的股骨与蜘蛛连枷重击。",
            CoreItemNames = ["Magnus' Femur", "Sands of Time", "Smelling Salts"],
            SupportItemNames = ["Spider Mace", "Thrown Net", "Amber", "Makroscope", "Incense", "Sleeping Potion"],
            CoreSkillNames = ["Slow and Steady", "Temporal Strike", "Calming Heals", "Debilitating Secrets", "Thick Hide"],
            PreferredTags = Dict(("Relic", 2.8), ("Weapon", 2.2), ("Tool", 1.8)),
            PreferredHiddenTags = Dict(("Slow", 3.4), ("SlowReference", 3.2), ("Damage", 2.4), ("RegenReference", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedSlow", 3.4), ("TTriggerOnCardPerformedRegen", 2.2)),
            PreferredEnchantments = Dict(("Heavy", 3.4), ("Obsidian", 2.8), ("Turbo", 2.4), ("Mossy", 2.0)),
            PositioningRules = [PositioningRule.AdjacentToCore, PositioningRule.RightMost],
            EarlyFocusTags = ["Slow", "Relic", "Regen"],
            EndgamePowerTag = "SlowChargeScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_VatOfAcid_Cauldron_Transform",
            Hero = "Mak",
            NameZh = "酸液槽坩埚全类型炼金流",
            DescriptionZh = "蒸馏器、煅烧釜与曲颈甑不断转化原料刷高全局加成，再用酸液槽和坩埚吸纳全类型同时引爆灼烧剧毒。",
            CoreItemNames = ["Vat of Acid", "Cauldron", "Calcinator"],
            SupportItemNames = ["Retort", "Alembic", "The Tome of Yyahan", "Philosopher's Stone", "Quicksilver", "Mirror"],
            CoreSkillNames = ["Catalytic Converter", "Chemical Fire", "Poison Flame", "Toxic Flame", "Sick Burn"],
            PreferredTags = Dict(("Tool", 2.8), ("Reagent", 2.4), ("Relic", 2.2)),
            PreferredHiddenTags = Dict(("Burn", 2.8), ("Poison", 2.8), ("BurnReference", 2.6), ("PoisonReference", 2.6)),
            PreferredTriggers = Dict(("TTriggerOnCardTransformed", 2.6), ("TTriggerOnCardSold", 2.4), ("TTriggerOnDayStarted", 2.2)),
            PreferredEnchantments = Dict(("Toxic", 3.0), ("Fiery", 3.0), ("Shiny", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.LeftMost],
            EarlyFocusTags = ["Reagent", "Burn", "Poison"],
            EndgamePowerTag = "TransformMultiTypeDot"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_SelfPoison_RapidInjection",
            Hero = "Mak",
            NameZh = "自毒转换肾上腺素狂流",
            DescriptionZh = "快速注射系统与毒液注射主动给自己上毒，触发肾上腺素调节服和注能护腕转化为巨额再生、加速和武器伤害。",
            CoreItemNames = ["Rapid Injection System", "Adrenal Converter", "Infused Bracers"],
            SupportItemNames = ["Venomous Dose", "Optical Augment", "Wild Quillback", "Test Subject Alpha", "Ouroboros Statue", "Black Rose"],
            CoreSkillNames = ["Poison Tyrant", "Toxicity", "Exposing Toxins", "Toxic Fuel", "Retaliatory Toxins"],
            PreferredTags = Dict(("Tool", 2.4), ("Apparel", 2.2), ("Friend", 1.8), ("Weapon", 1.6)),
            PreferredHiddenTags = Dict(("Poison", 3.4), ("PoisonReference", 3.2), ("Regen", 2.8), ("Haste", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedPoison", 3.4), ("TTriggerOnItemUsed", 2.4)),
            PreferredEnchantments = Dict(("Toxic", 3.2), ("Mossy", 2.8), ("Turbo", 2.6), ("Restorative", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Poison", "Regen", "Haste"],
            EndgamePowerTag = "SelfPoisonConverter"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_Freeze_BlackIce_FrozenFlame",
            Hero = "Mak",
            NameZh = "冰霜烈焰黑冰冻结流",
            DescriptionZh = "借助冰霜图腾、蓝宝石与霜冻药水稳定冰控，触发黑冰上毒、冰霜烈焰叠烧以及冰爪加伤的冰火三重奏。",
            CoreItemNames = ["Frozen Flame", "Black Ice", "Ice Claw"],
            SupportItemNames = ["Frost Totem", "Sapphire", "Frozen Horror", "Frost Potion", "Weaselpede", "Nullfrost Altar"],
            CoreSkillNames = ["Frozen Secrets", "Frostfire", "Shatter", "Paralytic Poison", "Reaching the Summit"],
            PreferredTags = Dict(("Relic", 3.0), ("Reagent", 1.6), ("Potion", 1.6)),
            PreferredHiddenTags = Dict(("Freeze", 3.4), ("FreezeReference", 3.2), ("Burn", 2.2), ("Poison", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedFreeze", 3.4), ("TTriggerOnPlayerAttributeChanged", 1.8)),
            PreferredEnchantments = Dict(("Icy", 3.4), ("Fiery", 2.4), ("Toxic", 2.4), ("Radiant", 2.4)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Freeze", "Relic", "Burn"],
            EndgamePowerTag = "FreezeStatusScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_Flying_Carpet_Skyterror",
            Hero = "Mak",
            NameZh = "空惧巨龙魔法飞毯暴击飞行流",
            DescriptionZh = "魔法飞毯与猫头鹰奥利靠暴击持续缩短冷却并让风之巨龙和空惧巨龙全员起飞，在空中打出狂暴输出。",
            CoreItemNames = ["Skyterror", "Magic Carpet", "Wind Dragon"],
            SupportItemNames = ["Covetous Raven", "Orly", "Fireflies", "Flying Potion", "Atmospheric Sampler", "Gravstone"],
            CoreSkillNames = ["Aerial Assault", "Bombing Run", "Scramble Fighters", "Haunting Flight", "Alacrity"],
            PreferredTags = Dict(("Dragon", 2.8), ("Vehicle", 2.4), ("Friend", 2.2), ("Relic", 1.8)),
            PreferredHiddenTags = Dict(("Flying", 3.4), ("FlyingReference", 3.2), ("Crit", 2.6), ("Damage", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnCardStartedFlying", 2.8), ("TTriggerOnCardStoppedFlying", 2.4), ("TTriggerOnCardCritted", 2.6)),
            PreferredEnchantments = Dict(("Deadly", 3.0), ("Turbo", 2.8), ("Fiery", 2.4), ("Obsidian", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Flying", "Dragon", "Crit"],
            EndgamePowerTag = "FlyingCritDragon"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_Enchant_Laboratory_Relic",
            Hero = "Mak",
            NameZh = "实验室空灵灰烬附魔遗物流",
            DescriptionZh = "实验室与空灵灰烬在开局疯狂为己方遗物赋予附魔，再由摆锤、天平和图书馆触发高频遗物充能。",
            CoreItemNames = ["Laboratory", "Skyspirit Ashes", "Pendulum", "Library"],
            SupportItemNames = ["Scales", "Tomb of the Ancients", "Excavation Tools", "Spirit Diffuser", "Darkstone Engine", "Ethergy Conduit"],
            CoreSkillNames = ["Archeologist", "Quick Study", "Secrets of Life", "Reserve Collection", "Ancient Weapons"],
            PreferredTags = Dict(("Relic", 3.4), ("Property", 2.4), ("Tool", 1.8), ("Reagent", 1.6)),
            PreferredHiddenTags = Dict(("Charge", 3.0), ("Cooldown", 2.4), ("Regen", 1.8), ("Crit", 1.8)),
            PreferredTriggers = Dict(("TTriggerOnFightStarted", 2.6), ("TTriggerOnItemUsed", 2.8), ("TTriggerOnBeforeItemUsed", 1.8)),
            PreferredEnchantments = Dict(("Shiny", 3.0), ("Turbo", 2.8), ("Radiant", 2.6), ("Obsidian", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Relic", "Property", "Regen"],
            EndgamePowerTag = "EnchantRelicLoop"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_Burn_FireClaw_DragonBreath",
            Hero = "Mak",
            NameZh = "火爪巨龙吐息烈焰流",
            DescriptionZh = "火爪按己方其他灼烧总和倍增灼烧，巨龙吐息在再生和治疗中飙升火势，蜡烛和油灯不停充能燃尽对手。",
            CoreItemNames = ["Fire Claw", "Eternal Torch", "Dragon's Breath"],
            SupportItemNames = ["Candles", "Ruby", "Show Globe", "Thurible", "Sulphur", "Blazehowl Warden"],
            CoreSkillNames = ["Ancient Flamekeeper", "Conflagration", "Living Flame", "Heat Lover", "Tracer Fire"],
            PreferredTags = Dict(("Relic", 2.8), ("Dragon", 2.0), ("Reagent", 1.8), ("Tool", 1.6)),
            PreferredHiddenTags = Dict(("Burn", 3.4), ("BurnReference", 3.2), ("RegenReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedBurn", 2.8), ("TTriggerOnCardPerformedRegen", 2.2), ("TTriggerOnItemUsed", 2.2)),
            PreferredEnchantments = Dict(("Fiery", 3.4), ("Turbo", 2.6), ("Mossy", 2.2), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.RightMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Burn", "Relic", "Regen"],
            EndgamePowerTag = "BurnAmplifyEngine"
        },
        new ArchetypeDefinition
        {
            Id = "Mak_Poison_Aludel_Satchel",
            Hero = "Mak",
            NameZh = "炼金梨缶毒蛇群流",
            DescriptionZh = "围绕炼金梨缶的多重剧毒、衔尾蛇雕像的毒转再生，以及蕨叶蜘蛛连胜成长多重释放，压制中后期长局。",
            CoreItemNames = ["Aludel", "Ouroboros Statue", "Idol of Decay"],
            SupportItemNames = ["Spider Fern", "Nightshade", "Fungal Spores", "Emerald", "Noxious Potion", "Quill and Ink"],
            CoreSkillNames = ["Ancient Toxins", "Potent Potables", "Creeping Toxins", "Final Dose", "Initial Dose"],
            PreferredTags = Dict(("Relic", 2.6), ("Potion", 2.4), ("Reagent", 2.0), ("Tool", 1.8)),
            PreferredHiddenTags = Dict(("Poison", 3.4), ("PoisonReference", 3.0), ("Regen", 2.2), ("PotionReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedPoison", 2.8), ("TTriggerOnDayStarted", 2.0), ("TTriggerOnFightEnded", 2.0)),
            PreferredEnchantments = Dict(("Toxic", 3.4), ("Mossy", 2.4), ("Turbo", 2.4), ("Shiny", 2.2)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.RightMost],
            EarlyFocusTags = ["Poison", "Potion", "Relic"],
            EndgamePowerTag = "PoisonRelicBurst"
        }
    ];
}
