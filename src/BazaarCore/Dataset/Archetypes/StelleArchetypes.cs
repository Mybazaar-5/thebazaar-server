using static BazaarCore.Dataset.Archetypes.ArchetypeCatalog;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 斯特拉（Stelle）的 12 套社区主流与高协同流派蓝图。
/// </summary>
internal static class StelleArchetypes
{
    public static IReadOnlyList<ArchetypeDefinition> Build() =>
    [
        new ArchetypeDefinition
        {
            Id = "Stelle_Flight_Skyliner_Launch",
            Hero = "Stelle",
            NameZh = "天际舰发射塔全员起飞流",
            DescriptionZh = "发射塔与雷达穹顶瞬间拉起全队飞行状态，让吃满飞行数量加成的天际舰和私人喷气机打出海量多重齐射。",
            CoreItemNames = ["Skyliner", "Launch Tower", "Radar Dome"],
            SupportItemNames = ["Caracara", "Private Runabout", "Propeller", "Flycycle", "Headset", "Pilot's Wings"],
            CoreSkillNames = ["Above the Clouds", "Acclimation", "Assault Formation", "Thrill of the Flight", "Start-Up Sequence"],
            PreferredTags = Dict(("Vehicle", 3.4), ("Property", 2.2), ("Weapon", 2.0)),
            PreferredHiddenTags = Dict(("Flying", 3.5), ("FlyingReference", 3.5), ("Damage", 2.6), ("Cooldown", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardStartedFlying", 2.8), ("TTriggerOnItemUsed", 2.4), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Turbo", 3.0), ("Obsidian", 2.8), ("Deadly", 2.8), ("Fiery", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Flying", "Vehicle", "Burn"],
            EndgamePowerTag = "FullBoardFlightSalvo"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_StartStop_Glider_Daggerwing",
            Hero = "Stelle",
            NameZh = "匕翼滑翔翼升降循环流",
            DescriptionZh = "利用自由落体模拟器、弹跳棒与纸飞机反复切换飞行/降落状态，不断给匕翼叠伤并触发悬挂滑翔翼连环轰炸。",
            CoreItemNames = ["Daggerwing", "Hang Glider", "Freefall Simulator"],
            SupportItemNames = ["Air-Pressure Rifle", "Anemometer", "Pogo Stick", "Squirrel Suit", "Paper Airplane", "Clockwork Disc"],
            CoreSkillNames = ["Barrel Roll", "Emergency Landing", "Aerial Assault", "Bombing Run", "Defense Formation"],
            PreferredTags = Dict(("Vehicle", 3.0), ("Weapon", 2.6), ("Tech", 2.0), ("Toy", 1.6)),
            PreferredHiddenTags = Dict(("Flying", 3.5), ("FlyingReference", 3.5), ("DamageReference", 2.6), ("Haste", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardStartedFlying", 3.4), ("TTriggerOnCardStoppedFlying", 3.4), ("TTriggerOnCardAttributeChanged", 2.0)),
            PreferredEnchantments = Dict(("Deadly", 3.0), ("Obsidian", 2.8), ("Turbo", 2.8), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Flying", "Vehicle", "Haste"],
            EndgamePowerTag = "FlightToggleScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_Land_Tethers_BirdCage",
            Hero = "Stelle",
            NameZh = "降落伞鸟笼着陆护盾流",
            DescriptionZh = "通过降落伞、鸟笼和系绳主动结束飞行状态，每次着陆叠加海量护盾与充能，再由天空之锚与卷云之羽反制敌人。",
            CoreItemNames = ["Bird Cage", "Tethers", "Parachute"],
            SupportItemNames = ["Sky Anchor", "Eject Button", "Airplane Glue", "Cirrus Feather", "Marshalling Lights", "Orbital Polisher"],
            CoreSkillNames = ["Emergency Landing", "Regular Maintenance", "Quick Defenses", "Sharp Corners", "Shield Bash"],
            PreferredTags = Dict(("Tool", 2.8), ("Tech", 2.0), ("Apparel", 1.6), ("Weapon", 1.6)),
            PreferredHiddenTags = Dict(("Flying", 3.4), ("FlyingReference", 3.4), ("Shield", 3.0), ("Cooldown", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardStoppedFlying", 3.5), ("TTriggerOnCardStartedFlying", 2.4), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Shielded", 3.4), ("Restorative", 2.6), ("Obsidian", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore, PositioningRule.ShieldOnEdge],
            EarlyFocusTags = ["Flying", "Shield", "Tool"],
            EndgamePowerTag = "StopFlyingShieldEngine"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_Drone_Workshop_BattleDrone",
            Hero = "Stelle",
            NameZh = "工坊作战无人机集群流",
            DescriptionZh = "斯黛尔的工坊配合摇杆与中继无人机狂速充能无人机阵列，由作战无人机吃满多重释放收割战场。",
            CoreItemNames = ["Stelle's Workshop", "Battle Drone", "Delivery Drone"],
            SupportItemNames = ["Relay Drone", "Security Drone", "Lightning Butterfly", "Angle Grinder Drone", "Joystick", "Targeting Drone"],
            CoreSkillNames = ["Buffed and Polished", "It's a Drone", "Vector Command", "Green Light", "Expert Pilot"],
            PreferredTags = Dict(("Drone", 3.5), ("Tech", 3.0), ("Tool", 2.4), ("Property", 1.8)),
            PreferredHiddenTags = Dict(("Charge", 3.2), ("Damage", 2.6), ("Cooldown", 2.4), ("Flying", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.0), ("TTriggerOnCardFired", 2.0)),
            PreferredEnchantments = Dict(("Turbo", 3.0), ("Deadly", 2.8), ("Obsidian", 2.6), ("Shielded", 2.2)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Drone", "Tech", "Charge"],
            EndgamePowerTag = "DroneChargeSwarm"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_SpaceLaser_Observatory_Charge",
            Hero = "Stelle",
            NameZh = "天文台太空激光充能炮流",
            DescriptionZh = "用高频率小型飞行件与天文台、液压钳不断充能太空激光，并在聚光灯翻倍加持下一炮蒸发敌方生命。",
            CoreItemNames = ["Space Laser", "Observatory", "Spotlight"],
            SupportItemNames = ["Hydraulic Squeezer", "Altimeter", "Kite", "Binoculars", "Red Lantern", "Jetpack"],
            CoreSkillNames = ["Big Guns", "Big Numbers", "Juggler", "Captain's Charge", "Alacrity"],
            PreferredTags = Dict(("Tech", 2.8), ("Tool", 2.6), ("Weapon", 2.2), ("Property", 1.8), ("Ray", 1.6)),
            PreferredHiddenTags = Dict(("Charge", 3.4), ("FlyingReference", 3.0), ("Damage", 2.8), ("Crit", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.0), ("TTriggerOnCardCritted", 2.4)),
            PreferredEnchantments = Dict(("Obsidian", 3.2), ("Deadly", 3.0), ("Radiant", 2.6), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.RightMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Flying", "Charge", "Damage"],
            EndgamePowerTag = "LaserChargeOverdrive"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_Destroy_Oblivion_Saucer",
            Hero = "Stelle",
            NameZh = "湮灭火炮飞碟自毁重构流",
            DescriptionZh = "通过炸裂旅程、爆竹机器人和湮灭漩涡主动触发摧毁，借助拆卸场与修复技能不断回溯并强化湮灭火炮与强化飞碟。",
            CoreItemNames = ["Oblivion Cannon", "Boosted Saucer", "Salvage Yard"],
            SupportItemNames = ["Bomb Voyage", "Buster", "The Big One", "Oblivion Vortex", "Parts Picker", "Boom Boom Bot"],
            CoreSkillNames = ["Leverage Momentum", "Master Mechanic", "Get Back In There!", "Energy Transference", "Void Render"],
            PreferredTags = Dict(("Weapon", 2.6), ("Tech", 2.6), ("Vehicle", 2.2), ("Property", 1.8)),
            PreferredHiddenTags = Dict(("Damage", 3.0), ("DamageReference", 2.8), ("Shield", 2.0), ("Charge", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnBeforeCardDestroyed", 3.4), ("TTriggerOnCardPerformedDestruction", 3.4), ("TTriggerOnCardRepaired", 2.6)),
            PreferredEnchantments = Dict(("Obsidian", 3.2), ("Fiery", 2.6), ("Deadly", 2.6), ("Shielded", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.RightMost],
            EarlyFocusTags = ["Tech", "Vehicle", "Damage"],
            EndgamePowerTag = "DestroyRepairEngine"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_Haste_Swallowtail_Pillbuggy",
            Hero = "Stelle",
            NameZh = "凤蝶飞艇西瓜虫加速盾流",
            DescriptionZh = "星辉凤蝶飞艇与平衡车带动全队高速加速，将每次加速和飞行转化为西瓜虫车与磁力护盾的厚实护甲重击。",
            CoreItemNames = ["Stellar Swallowtail", "Pillbuggy", "MagShield"],
            SupportItemNames = ["Cloud Tanker", "Windbreaker", "Oxygen Mask", "Goggles", "Lowroller", "Pinwheel"],
            CoreSkillNames = ["Finesse Shield", "Streamline Weapon", "Static Acceleration", "Evasive Maneuvers", "Defense Formation"],
            PreferredTags = Dict(("Vehicle", 3.0), ("Property", 1.8), ("Apparel", 1.8), ("Tech", 1.6)),
            PreferredHiddenTags = Dict(("Haste", 3.4), ("HasteReference", 3.2), ("Shield", 3.0), ("FlyingReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedHaste", 3.4), ("TTriggerOnCardStartedFlying", 2.6), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Turbo", 3.2), ("Shielded", 3.2), ("Obsidian", 2.4), ("Restorative", 2.0)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Haste", "Shield", "Flying"],
            EndgamePowerTag = "HasteFlightShieldSlam"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_Burn_Balloon_Molten",
            Hero = "Stelle",
            NameZh = "气球引擎熔火弹灼烧流",
            DescriptionZh = "每次飞行或使用工具无人机都引爆气球引擎、烧灼器与熔火弹发射器的灼烧连锁，高温烧融敌方护盾。",
            CoreItemNames = ["Balloon Engine", "Battle Balloon", "Molten Ball Blaster"],
            SupportItemNames = ["LavaRoller", "Pressure Heater", "Cauterizer", "Flame Jet Drone", "Solar Drone", "Box of Fireworks"],
            CoreSkillNames = ["Bombs Away!", "Hot Wheels", "Hot Air", "Burn Containment", "Searing Flames"],
            PreferredTags = Dict(("Vehicle", 2.6), ("Tool", 2.4), ("Drone", 2.0), ("Tech", 1.8)),
            PreferredHiddenTags = Dict(("Burn", 3.5), ("BurnReference", 3.2), ("FlyingReference", 2.6), ("Shield", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardStartedFlying", 2.8), ("TTriggerOnCardPerformedBurn", 3.0), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Fiery", 3.5), ("Turbo", 2.8), ("Shielded", 2.2), ("Shiny", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Burn", "Flying", "Tool"],
            EndgamePowerTag = "FlightBurnExplosion"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_Slow_CruiseShip_LightningRod",
            Hero = "Stelle",
            NameZh = "避雷针游轮雷暴减速流",
            DescriptionZh = "借助游轮、天气机、消防水管与惯性阻尼器制造全屏减速，同时用避雷针吸收敌我控制反打巨额雷击伤害。",
            CoreItemNames = ["Lightning Rod", "Cruise Ship", "Weather Machine"],
            SupportItemNames = ["Lightning Focuser", "Fire Hose", "Sirens", "Steam Hose", "Inertial Dampener", "Tugboat"],
            CoreSkillNames = ["Outmaneuver", "Slow and Steady", "Temporal Strike", "Slowed Targets", "Slippery Fella"],
            PreferredTags = Dict(("Vehicle", 2.8), ("Property", 2.2), ("Tool", 2.0), ("Tech", 1.8)),
            PreferredHiddenTags = Dict(("Slow", 3.5), ("SlowReference", 3.4), ("Damage", 2.4), ("Burn", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedSlow", 3.5), ("TTriggerOnCardPerformedFreeze", 2.2), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Heavy", 3.4), ("Obsidian", 2.8), ("Fiery", 2.4), ("Icy", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Slow", "Vehicle", "Burn"],
            EndgamePowerTag = "SlowAbsorbLightning"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_Tool_DraftingTable_Wrench",
            Hero = "Stelle",
            NameZh = "制图桌扳手精密工具流",
            DescriptionZh = "制图桌每日自动升级载具并充能核心，配合扳手、工具箱、精密卡尺与鸡农炮形成工坊精密机械流。",
            CoreItemNames = ["Drafting Table", "Wrench", "Chicken Cannon"],
            SupportItemNames = ["Toolbox", "Precision Calipers", "Rivet Gun", "Hammer", "Multitool", "Box Cutter"],
            CoreSkillNames = ["Tool Fight", "Tools of the Trade", "Workin' Hard", "Flashy Mechanic", "The Right Tool"],
            PreferredTags = Dict(("Tool", 3.5), ("Weapon", 2.4), ("Vehicle", 1.6)),
            PreferredHiddenTags = Dict(("Haste", 2.6), ("Damage", 2.6), ("Charge", 2.6), ("FlyingReference", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnDayStarted", 2.4), ("TTriggerOnItemUsed", 2.6), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Deadly", 3.0), ("Obsidian", 2.8), ("Turbo", 2.8), ("Shielded", 2.2)),
            PositioningRules = [PositioningRule.FastTriggerLeftOfScaler, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Tool", "Weapon", "Haste"],
            EndgamePowerTag = "ToolUpgradeOverdrive"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_Vehicle_Vortex_Hangar",
            Hero = "Stelle",
            NameZh = "机库漩涡加农炮载具流",
            DescriptionZh = "漩涡加农炮拉起双方全员飞行并每次飞行永久成长，搭配机库全局光环、冲撞气球与氢气罐开局先手压制。",
            CoreItemNames = ["Vortex Cannon", "Hangar", "Ramming Balloon"],
            SupportItemNames = ["Private Runabout", "Steam Washer", "Hydrogen Tank", "Flycycle", "Squirrel Suit", "Cloud Tanker"],
            CoreSkillNames = ["Command Ship", "Expert Pilot", "Flashy Pilot", "Full Arsenal", "Eject, Eject!"],
            PreferredTags = Dict(("Vehicle", 3.5), ("Property", 2.2), ("Weapon", 2.2), ("Tech", 1.8)),
            PreferredHiddenTags = Dict(("FlyingReference", 3.2), ("DamageReference", 2.8), ("ShieldReference", 2.4), ("Haste", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.0), ("TTriggerOnFightStarted", 2.0), ("TTriggerOnCardPerformedShield", 2.0)),
            PreferredEnchantments = Dict(("Obsidian", 3.0), ("Deadly", 3.0), ("Shielded", 2.6), ("Turbo", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Vehicle", "Flying", "Damage"],
            EndgamePowerTag = "VortexHangarScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Stelle_Garage_Enchant_Crit",
            Hero = "Stelle",
            NameZh = "实验车库加特林附魔暴击流",
            DescriptionZh = "实验车库赋予开局全局附魔与冷却缩减，加特林机枪首次触发后冷却减半配合望远镜与陀螺瞄准镜爆发收割。",
            CoreItemNames = ["Experimental Garage", "Gatling Gun", "Portable Shield Generator"],
            SupportItemNames = ["Gyro Gunsight", "Binoculars", "Sweeper Drone", "Seeker Probe", "Atlas", "Cloche Hat"],
            CoreSkillNames = ["Happy Accident", "Honed Strike", "Alacrity", "Advanced Synthetics", "Critical Protector"],
            PreferredTags = Dict(("Tech", 3.0), ("Weapon", 2.6), ("Property", 1.8), ("Tool", 1.6)),
            PreferredHiddenTags = Dict(("Crit", 3.2), ("CritReference", 3.0), ("Cooldown", 2.6), ("Damage", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnFightStarted", 2.6), ("TTriggerOnCardCritted", 3.0), ("TTriggerOnCardPerformedShield", 2.0)),
            PreferredEnchantments = Dict(("Deadly", 3.4), ("Shiny", 2.8), ("Obsidian", 2.8), ("Turbo", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Crit", "Tech", "Weapon"],
            EndgamePowerTag = "EnchantCritGatling"
        }
    ];
}
