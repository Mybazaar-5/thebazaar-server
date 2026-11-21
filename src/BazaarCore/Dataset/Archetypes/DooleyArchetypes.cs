using static BazaarCore.Dataset.Archetypes.ArchetypeCatalog;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 杜利（Dooley）的 12 套社区主流与高协同流派蓝图。
/// </summary>
internal static class DooleyArchetypes
{
    public static IReadOnlyList<ArchetypeDefinition> Build() =>
    [
        new ArchetypeDefinition
        {
            Id = "Dooley_CombatCore_Buff",
            Hero = "Dooley",
            NameZh = "战斗核心双向增幅流",
            DescriptionZh = "将战斗核心置于中轴，左侧高频充能、右侧堆叠伤害与护盾，用小型科技件疯狂驱动核心输出。",
            CoreItemNames = ["Combat Core", "Charging Station", "Kinetic Cannon"],
            SupportItemNames = ["Fiber Optics", "GPU", "Battery", "Metronome", "Chris Army Knife", "Red Button"],
            CoreSkillNames = ["Core Surge", "Overclocked", "Well-Oiled Machine", "Firepower", "Titanium Casing"],
            PreferredTags = Dict(("Core", 3.4), ("Tech", 2.8), ("Weapon", 2.0)),
            PreferredHiddenTags = Dict(("Charge", 3.0), ("Damage", 2.4), ("Shield", 2.2), ("DamageReference", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.0), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Obsidian", 2.8), ("Shielded", 2.6), ("Turbo", 2.8), ("Shiny", 2.6)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore, PositioningRule.FastTriggerLeftOfScaler],
            EarlyFocusTags = ["Core", "Tech", "Damage"],
            EndgamePowerTag = "CoreScalingLoop"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_Burn_Solar_Torch",
            Hero = "Dooley",
            NameZh = "太阳能焊炬灼烧流",
            DescriptionZh = "太阳能农场、热能长枪与微波戴夫互相触发加速与灼烧，搭配火焰喷射枪与欧米伽射线极速叠加高额灼烧。",
            CoreItemNames = ["Solar Farm", "Flamethrower", "Thermal Lance"],
            SupportItemNames = ["Welding Torch", "Welding Helmet", "Micro Dave", "Omega Ray", "Bomb Squad", "Induction Aegis"],
            CoreSkillNames = ["Letting Off Steam", "Heat Shield", "Burn Containment", "Conflagration", "Searing Flames"],
            PreferredTags = Dict(("Tech", 2.4), ("Tool", 2.2), ("Property", 1.8), ("Ray", 1.6)),
            PreferredHiddenTags = Dict(("Burn", 3.2), ("BurnReference", 2.8), ("Haste", 2.4), ("HasteReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedBurn", 3.0), ("TTriggerOnCardPerformedHaste", 2.2), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Fiery", 3.2), ("Turbo", 2.6), ("Shielded", 2.2), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Burn", "Tech", "Haste"],
            EndgamePowerTag = "BurnChargeLoop"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_Armored_ForceField",
            Hero = "Dooley",
            NameZh = "掩体力场护盾反伤流",
            DescriptionZh = "以掩体与防御矩阵叠加海量护盾，利用力场、铁甲犰狳与愤怒气球机器人将护盾转化为毁灭性伤害。",
            CoreItemNames = ["Force Field", "Bunker", "Harmadillo"],
            SupportItemNames = ["Blast Doors", "Brick Buddy", "Defense Matrix", "Barbed Wire", "Duct Tape", "Epsilon Ray"],
            CoreSkillNames = ["Sharp Corners", "Shield Bash", "Defensive Stance", "Electrified Hull", "Quick Defenses"],
            PreferredTags = Dict(("Tech", 2.4), ("Property", 1.8), ("Friend", 1.8), ("Weapon", 1.8)),
            PreferredHiddenTags = Dict(("Shield", 3.2), ("ShieldReference", 3.2), ("Damage", 2.4), ("Charge", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedShield", 3.2), ("TTriggerOnPlayerAttributeChanged", 2.0)),
            PreferredEnchantments = Dict(("Shielded", 3.2), ("Obsidian", 2.8), ("Heavy", 2.2), ("Restorative", 2.0)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore, PositioningRule.ShieldOnEdge],
            EarlyFocusTags = ["Shield", "Tech", "Friend"],
            EndgamePowerTag = "ShieldSlamBurst"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_Friend_Factory_Dooltron",
            Hero = "Dooley",
            NameZh = "机器人工厂多尔王伙伴流",
            DescriptionZh = "机器人工厂赋予全体伙伴多重释放，由断路器 DJ 与多尔王主机无限驱动多尔王和推土比尔。",
            CoreItemNames = ["Robotic Factory", "Dooltron", "Bill Dozer"],
            SupportItemNames = ["Dooltron Mainframe", "DJ Circuit Breaker", "Clawrence", "First Aiden", "Sat-Comm", "Brick Buddy"],
            CoreSkillNames = ["Buddy System", "Best Friend", "Beautiful Friendship", "Balanced Friendship", "Intrusion Countermeasures"],
            PreferredTags = Dict(("Friend", 3.4), ("Vehicle", 2.0), ("Tech", 2.0), ("Property", 1.6)),
            PreferredHiddenTags = Dict(("Damage", 2.6), ("Haste", 2.4), ("Charge", 2.4), ("Shield", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.0), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Turbo", 2.8), ("Deadly", 2.6), ("Obsidian", 2.6), ("Shielded", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Friend", "Haste", "Damage"],
            EndgamePowerTag = "FriendMulticast"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_Poison_CORA_Lizard",
            Hero = "Dooley",
            NameZh = "CORA蜥蜴剧毒科技流",
            DescriptionZh = "以 C.O.R.A 剧毒转化为核心，结合显示屏蜥蜴、伽马射线与机械虫群利用加速减速联动狂叠剧毒。",
            CoreItemNames = ["C.O.R.A", "Monitor Lizard", "Chemsnail"],
            SupportItemNames = ["Copper Ed", "Gamma Ray", "BLK-SP1D3R", "GRN-W4SP", "Trojan", "Keylogger"],
            CoreSkillNames = ["Improved Toxins", "Creeping Toxins", "Exposing Toxins", "Toxic Weapons", "Sick Burn"],
            PreferredTags = Dict(("Friend", 2.6), ("Tech", 2.4), ("Core", 2.0), ("Ray", 1.6)),
            PreferredHiddenTags = Dict(("Poison", 3.2), ("PoisonReference", 3.0), ("HasteReference", 2.2), ("Slow", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedHaste", 2.6), ("TTriggerOnCardPerformedPoison", 2.6), ("TTriggerOnCardPerformedSlow", 2.2)),
            PreferredEnchantments = Dict(("Toxic", 3.2), ("Turbo", 2.6), ("Heavy", 2.4), ("Shiny", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Poison", "Friend", "Haste"],
            EndgamePowerTag = "PoisonHasteEngine"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_Dinosaur_Destruction",
            Hero = "Dooley",
            NameZh = "机械暴龙吞噬拆解流",
            DescriptionZh = "妈妈暴龙、机甲暴龙与小黛暴龙联手摧毁对手关键格位并使全队恐龙伤害指数级翻倍。",
            CoreItemNames = ["Momma-Saur", "Rex Spex", "Diana-Saur"],
            SupportItemNames = ["Tanky Anky", "Dilly", "Dino Disguise", "Dinosawer", "Trollosaur", "Dino Saddle"],
            CoreSkillNames = ["Time Traveler", "Primal Energy (Skill)", "Pack Infiltrator (Skill)", "Energy Transference", "Void Render"],
            PreferredTags = Dict(("Dinosaur", 3.4), ("Friend", 2.4), ("Weapon", 2.2), ("Relic", 2.0)),
            PreferredHiddenTags = Dict(("Damage", 2.8), ("DamageReference", 2.6), ("Cooldown", 1.8), ("Shield", 1.6)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedDestruction", 3.2), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Deadly", 3.0), ("Obsidian", 2.8), ("Radiant", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.CoreCenter],
            EarlyFocusTags = ["Dinosaur", "Friend", "Weapon"],
            EndgamePowerTag = "DestructionScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_Tech_Railgun_Printer",
            Hero = "Dooley",
            NameZh = "3D打印轨道炮科技连发流",
            DescriptionZh = "堆叠高频低冷却科技物品、主板与 3D 打印机复制组件，秒速充能轨道炮与特斯拉线圈轰穿敌阵。",
            CoreItemNames = ["Railgun", "3D Printer", "Motherboard"],
            SupportItemNames = ["Tesla Coil", "Arc Blaster", "Capacitor", "Battery", "Lens", "Crude Tools"],
            CoreSkillNames = ["Tech Savvy", "Data Transfer", "Like Clockwork", "Retro-Tech", "Power Chord"],
            PreferredTags = Dict(("Tech", 3.4), ("Weapon", 2.2), ("Tool", 2.0)),
            PreferredHiddenTags = Dict(("TechReference", 3.0), ("Charge", 2.8), ("Damage", 2.4), ("Cooldown", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.0), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Obsidian", 3.0), ("Turbo", 2.8), ("Deadly", 2.6), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.FastTriggerLeftOfScaler, PositioningRule.RightMost],
            EarlyFocusTags = ["Tech", "Charge", "Damage"],
            EndgamePowerTag = "TechChargeRailgun"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_Freeze_Nitrogen_Hammer",
            Hero = "Dooley",
            NameZh = "液氮锤冰能球冻结流",
            DescriptionZh = "通过冰能球、空调皮埃尔与贝塔射线制造全场冰冻连锁，持续喂大液氮锤和冰霜9000。",
            CoreItemNames = ["Nitrogen Hammer", "Cryosphere", "Ice 9000"],
            SupportItemNames = ["Pierre Conditioner", "Beta Ray", "Coolant", "Cooling Fan", "Isochoric Freezer", "BLU-B33TL3"],
            CoreSkillNames = ["Frozen Synapse", "Liquid Cooled", "Frozen Shot", "Shatter", "Coolant Leak"],
            PreferredTags = Dict(("Tech", 2.6), ("Friend", 2.0), ("Weapon", 1.8)),
            PreferredHiddenTags = Dict(("Freeze", 3.4), ("FreezeReference", 3.2), ("Damage", 2.2), ("Cooldown", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedFreeze", 3.4), ("TTriggerOnItemUsed", 1.8)),
            PreferredEnchantments = Dict(("Icy", 3.2), ("Radiant", 2.8), ("Obsidian", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.RightMost, PositioningRule.CoreCenter],
            EarlyFocusTags = ["Freeze", "Tech", "Slow"],
            EndgamePowerTag = "FreezeDamageScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_PowerDrill_StatusLoop",
            Hero = "Dooley",
            NameZh = "电钻多异常无限充能流",
            DescriptionZh = "集合减速、灼烧、剧毒、加速与冻结全异常状态触发器，使电钻实现零间隔永动连击。",
            CoreItemNames = ["Power Drill", "Pylon", "Weakpoint Detector"],
            SupportItemNames = ["Metronome", "Cool LEDs", "Duct Tape", "Plasma Grenade", "Hammlet", "Power Sander"],
            CoreSkillNames = ["Neophiliac", "Slow Burn", "Temporal Strike", "Creeping Chill", "Workin' Hard"],
            PreferredTags = Dict(("Tool", 2.6), ("Tech", 2.4), ("Weapon", 2.0), ("Property", 1.6)),
            PreferredHiddenTags = Dict(("Slow", 2.8), ("SlowReference", 2.8), ("HasteReference", 2.4), ("BurnReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedSlow", 2.8), ("TTriggerOnCardPerformedHaste", 2.6), ("TTriggerOnCardPerformedBurn", 2.2)),
            PreferredEnchantments = Dict(("Heavy", 2.8), ("Fiery", 2.6), ("Toxic", 2.6), ("Icy", 2.6)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Slow", "Haste", "Tool"],
            EndgamePowerTag = "StatusTriggerChargeLoop"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_Flying_PartyFloat",
            Hero = "Dooley",
            NameZh = "派对浮艇悬浮翼龙流",
            DescriptionZh = "以派对浮艇和悬浮科技带动特里翼龙与气球机器人全员起飞，通过起降触发与冷却缩减压制对手。",
            CoreItemNames = ["Party Float", "Terry-Dactyl", "Hover Tech"],
            SupportItemNames = ["Balloon Bot", "Propeller Hat", "Levitation Pad", "Thrusters", "Micro Mach", "Cosmic Plumage"],
            CoreSkillNames = ["Aerial Assault", "Bombing Run", "Scramble Fighters", "Haunting Flight", "Command Ship"],
            PreferredTags = Dict(("Vehicle", 2.8), ("Tech", 2.4), ("Dinosaur", 1.8), ("Apparel", 1.6)),
            PreferredHiddenTags = Dict(("Flying", 3.4), ("FlyingReference", 3.2), ("Shield", 2.0), ("Cooldown", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardStartedFlying", 2.8), ("TTriggerOnCardStoppedFlying", 2.4), ("TTriggerOnItemUsed", 2.2)),
            PreferredEnchantments = Dict(("Turbo", 2.8), ("Shielded", 2.6), ("Deadly", 2.6), ("Obsidian", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Flying", "Vehicle", "Haste"],
            EndgamePowerTag = "FlyingTriggerEngine"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_Crit_Bellelista_Ray",
            Hero = "Dooley",
            NameZh = "贝拉机弩射线暴击流",
            DescriptionZh = "通过监控摄像头、雷达模块和节拍器提供高频加速与暴击增益，让贝拉机弩或孤狼赛博保安打出恐怖暴击。",
            CoreItemNames = ["Bellelista", "Security Camera", "Cybersecurity"],
            SupportItemNames = ["Alpha Ray", "Pulse Rifle", "Radar Module", "Laser Pistol", "SMG", "Claw Arm"],
            CoreSkillNames = ["Critical Protector", "Alacrity", "Building Crescendo", "Honed Strike", "Improvised Weaponry"],
            PreferredTags = Dict(("Weapon", 2.8), ("Tech", 2.6), ("Friend", 1.8), ("Ray", 1.6)),
            PreferredHiddenTags = Dict(("Crit", 3.0), ("CritReference", 2.8), ("Damage", 2.6), ("HasteReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardCritted", 3.0), ("TTriggerOnCardPerformedHaste", 2.4), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Deadly", 3.2), ("Turbo", 2.8), ("Obsidian", 2.6), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.FastTriggerLeftOfScaler, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Crit", "Weapon", "Haste"],
            EndgamePowerTag = "CritHasteScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Dooley_Forklift_Press_Hybrid",
            Hero = "Dooley",
            NameZh = "叉车液压机多类型工业流",
            DescriptionZh = "利用液压机吞噬标签与叉车多类型增伤机制，辅以装配线和时空穿梭机堆叠跨类型大型重器。",
            CoreItemNames = ["Hydraulic Press", "Forklift", "Crane"],
            SupportItemNames = ["Assembly Line", "Temporal Navigator", "Hologram Projector", "Antimatter Chamber", "Memory Card", "Walter Cooler"],
            CoreSkillNames = ["It's All Connected", "Full Arsenal", "Big Guns", "Advanced Synthetics", "Machine Learning"],
            PreferredTags = Dict(("Vehicle", 2.8), ("Tool", 2.6), ("Weapon", 2.4), ("Tech", 2.0)),
            PreferredHiddenTags = Dict(("Damage", 2.6), ("DamageReference", 2.4), ("EconomyReference", 1.8), ("Level", 1.6)),
            PreferredTriggers = Dict(("TTriggerOnFightEnded", 2.4), ("TTriggerOnDayStarted", 2.2), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Obsidian", 3.0), ("Deadly", 2.8), ("Radiant", 2.4), ("Golden", 2.0)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.RightMost],
            EarlyFocusTags = ["Vehicle", "Tool", "EconomyReference"],
            EndgamePowerTag = "MultiTypeIndustrial"
        }
    ];
}
