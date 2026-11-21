using static BazaarCore.Dataset.Archetypes.ArchetypeCatalog;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 皮格马利翁（Pygmalien）的 12 套社区主流与高协同流派蓝图。
/// </summary>
internal static class PygmalienArchetypes
{
    public static IReadOnlyList<ArchetypeDefinition> Build() =>
    [
        new ArchetypeDefinition
        {
            Id = "Pygmalien_Skyscraper_Value",
            Hero = "Pygmalien",
            NameZh = "摩天大楼地产价值流",
            DescriptionZh = "通过露台、顶层豪华公寓、VIP 通行证与算盘把地产价值堆到极限，再用摩天大楼和摩空大楼打出毁灭性攻防。",
            CoreItemNames = ["Skyscraper", "Spacescraper", "Balcony"],
            SupportItemNames = ["Subscraper", "Billboard", "Open Sign", "VIP Pass", "Abacus", "Ledger"],
            CoreSkillNames = ["Prosperity", "Master Salesman", "Long-term Investments", "Commercial Zoning", "Industrialist"],
            PreferredTags = Dict(("Property", 3.4), ("Weapon", 1.8), ("Tool", 1.6)),
            PreferredHiddenTags = Dict(("Value", 3.4), ("EconomyReference", 3.2), ("Shield", 2.2), ("Damage", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 2.2), ("TTriggerOnCardSold", 2.2), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Golden", 3.4), ("Obsidian", 2.8), ("Shielded", 2.8), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Property", "Value", "EconomyReference"],
            EndgamePowerTag = "ValueScalingBurst"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_FixerUpper_Shield",
            Hero = "Pygmalien",
            NameZh = "毛坯房景观楼护盾反伤流",
            DescriptionZh = "毛坯房逐日自动升阶并配合景观楼叠出数万护盾，依靠棘刺盾把护盾等量转化为致命一击。",
            CoreItemNames = ["Fixer Upper", "Spiky Shield", "Landscraper"],
            SupportItemNames = ["Robe", "Silk Scarf", "Display Case", "Heirlooms", "Sponsored Apparel", "Fort"],
            CoreSkillNames = ["Huge Tank", "Quick Defenses", "Sharp Corners", "Renovation", "Defense Grid"],
            PreferredTags = Dict(("Property", 2.8), ("Apparel", 2.2), ("Weapon", 1.8)),
            PreferredHiddenTags = Dict(("Shield", 3.4), ("ShieldReference", 3.2), ("Value", 2.6), ("Damage", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnDayStarted", 2.4), ("TTriggerOnCardPerformedShield", 2.6), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Shielded", 3.2), ("Obsidian", 3.0), ("Golden", 2.6), ("Restorative", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.ShieldOnEdge],
            EarlyFocusTags = ["Property", "Shield", "Value"],
            EndgamePowerTag = "PropertyShieldSlam"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_MaxHP_DoubleWhammy",
            Hero = "Pygmalien",
            NameZh = "巨额血量双头巨锤流",
            DescriptionZh = "依靠当铺、腰带、风车磨坊与柠檬水摊滚出超高最大生命值，用双头巨锤与狮首手杖一锤定音。",
            CoreItemNames = ["Double Whammy", "Pawn Shop", "Lion Cane"],
            SupportItemNames = ["Belt", "Windmill", "Championship Belt", "Closed Sign", "Globe", "Signet Ring"],
            CoreSkillNames = ["Huge Warrior", "Healthy Hoarder", "Property Mogul", "Diamond Heart", "Growth Spurt"],
            PreferredTags = Dict(("Property", 2.6), ("Weapon", 2.4), ("Apparel", 2.0)),
            PreferredHiddenTags = Dict(("Health", 3.4), ("HealthReference", 3.2), ("Damage", 2.4), ("EconomyReference", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardSold", 2.4), ("TTriggerOnPlayerAttributeChanged", 2.2), ("TTriggerOnSandstorm", 1.8)),
            PreferredEnchantments = Dict(("Obsidian", 3.2), ("Deadly", 2.8), ("Radiant", 2.6), ("Shielded", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Health", "Property", "EconomyReference"],
            EndgamePowerTag = "MaxHealthScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_Heal_Regen_Vineyard",
            Hero = "Pygmalien",
            NameZh = "葡萄园猪猪洗衣房治疗流",
            DescriptionZh = "猪猪洗衣房、葡萄园与摇钱树组成钢铁治疗防线，触发治疗的同时不断提升最大生命并加速输出。",
            CoreItemNames = ["Hogwash", "Vineyard", "Money Tree"],
            SupportItemNames = ["Luxury Tents", "Lemonade Stand", "Bushel", "Textiles", "Succulents", "Weights"],
            CoreSkillNames = ["Huge Healer", "Healthy Jolt", "Medic's Wrath", "Medic's Shield", "Everlasting Life"],
            PreferredTags = Dict(("Property", 3.0), ("Food", 2.0), ("Tool", 1.5)),
            PreferredHiddenTags = Dict(("Heal", 3.4), ("HealReference", 3.2), ("Regen", 2.4), ("Health", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedHeal", 3.2), ("TTriggerOnCardPerformedOverHeal", 2.2), ("TTriggerOnCardSold", 1.8)),
            PreferredEnchantments = Dict(("Restorative", 3.2), ("Shielded", 2.6), ("Fiery", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Heal", "Regen", "Property"],
            EndgamePowerTag = "HealScalingLoop"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_Square_Crook_Atlatl",
            Hero = "Pygmalien",
            NameZh = "曲柄杖中型武器军团流",
            DescriptionZh = "经典中型物品阵列：曲柄杖和展品柜大幅拔高投矛器、帝王之刃与链枷伤害，使投矛器冷却压至 1 秒狂砍。",
            CoreItemNames = ["Crook", "Atlatl", "Regal Blade"],
            SupportItemNames = ["Dog", "Flail", "Caltrops", "Atlas Stone", "Showcase", "Grindstone"],
            CoreSkillNames = ["Hyper Focus", "Lifting", "Strong Arm", "Arms Dealer", "Reinforced Steel"],
            PreferredTags = Dict(("Weapon", 3.4), ("Tool", 2.0), ("Relic", 1.8), ("Property", 1.6)),
            PreferredHiddenTags = Dict(("Damage", 3.2), ("DamageReference", 3.0), ("Cooldown", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardFired", 2.0), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Obsidian", 3.0), ("Deadly", 3.0), ("Shiny", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.FastTriggerLeftOfScaler, PositioningRule.RightMost],
            EarlyFocusTags = ["Weapon", "Damage", "EconomyReference"],
            EndgamePowerTag = "MediumWeaponSynergy"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_Toy_Piggles_Yoyo",
            Hero = "Pygmalien",
            NameZh = "猪猪乐园玩具无限充能流",
            DescriptionZh = "竞技场、风暴猪猪电玩馆与小圆猪板让悠悠球和猪猪玩具互充永动，越转越强。",
            CoreItemNames = ["Tournament Arena", "PiggleStorm Gaming House", "Yo-Yo"],
            SupportItemNames = ["Piggles", "Piggles Board", "Piggles Launcher", "Piggles Protectors", "Streaming Setup", "Model Ship"],
            CoreSkillNames = ["Favorite Toy (Skill)", "Pop-Up Shop", "Running with Scissors", "Pillow Fort", "Playful Arsonist"],
            PreferredTags = Dict(("Toy", 3.4), ("Property", 2.0), ("Weapon", 1.8)),
            PreferredHiddenTags = Dict(("Charge", 3.0), ("Damage", 2.4), ("Shield", 2.2), ("Value", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.2), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Shiny", 3.0), ("Deadly", 2.6), ("Fiery", 2.4), ("Shielded", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Toy", "Charge", "Damage"],
            EndgamePowerTag = "ToyChargeMulticast"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_Freeze_IceCream_Club",
            Hero = "Pygmalien",
            NameZh = "冰淇淋耶塔里亚冻结重击流",
            DescriptionZh = "冰淇淋车、冰屋与冷冻间连环冰封敌人棋盘，持续充能耶塔里亚大棒和机动雪橇打出高倍伤害。",
            CoreItemNames = ["Ice Cream Truck", "Yetarian Club", "Private Hot Springs", "Igloo"],
            SupportItemNames = ["Snowmobile", "Cold Room", "Aurora Dome", "Snow Globe", "Stopwatch", "Nullfrost Altar"],
            CoreSkillNames = ["Shatter", "Reaching the Summit", "Snowball Fight", "Initial Chill", "Invigorating Cold"],
            PreferredTags = Dict(("Property", 2.8), ("Weapon", 2.0), ("Vehicle", 1.8), ("Toy", 1.6)),
            PreferredHiddenTags = Dict(("Freeze", 3.4), ("FreezeReference", 3.2), ("Shield", 2.0), ("Damage", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedFreeze", 3.2), ("TTriggerOnItemUsed", 2.2)),
            PreferredEnchantments = Dict(("Icy", 3.4), ("Obsidian", 2.6), ("Shielded", 2.4), ("Radiant", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Freeze", "Property", "Shield"],
            EndgamePowerTag = "FreezeClubControl"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_Burn_Sauna_Matchbox",
            Hero = "Pygmalien",
            NameZh = "桑拿熔炉火柴盒灼烧流",
            DescriptionZh = "豪华桑拿炉与烧钱炉把地产价值化作灼烧爆发，配合火柴盒与桑拿的非武器充能循环融化对手。",
            CoreItemNames = ["Luxury Kiuas", "Sauna", "Matchbox"],
            SupportItemNames = ["Money Furnace", "Riceballer", "Hot Stones", "Steam Ladle", "Pepper Spray", "Ganjo"],
            CoreSkillNames = ["Conflagration", "Medic's Flame", "Burning Infernal", "Pyromania", "Hot Spot"],
            PreferredTags = Dict(("Tool", 2.6), ("Property", 2.4), ("Food", 1.4)),
            PreferredHiddenTags = Dict(("Burn", 3.4), ("BurnReference", 3.0), ("Value", 2.4), ("EconomyReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedBurn", 2.8), ("TTriggerOnItemUsed", 2.4), ("TTriggerOnCardSold", 2.0)),
            PreferredEnchantments = Dict(("Fiery", 3.2), ("Golden", 2.6), ("Turbo", 2.4), ("Restorative", 2.0)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Burn", "Property", "Heal"],
            EndgamePowerTag = "ValueBurnScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_Beast_Cargo_MultiType",
            Hero = "Pygmalien",
            NameZh = "双峰驼兽多标签暴走流",
            DescriptionZh = "利用储存箱和全场标签使双峰驼兽、工装短裤与华丽巫师帽吃满多重释放，留声机加速直接清场。",
            CoreItemNames = ["Beast of Burden", "Cargo Shorts", "Apropos Chapeau"],
            SupportItemNames = ["Jabalian Totem", "Gramophone", "Abacus", "Balcony", "Keychain", "Briefcase"],
            CoreSkillNames = ["Boar Market", "Trader", "High-Yield Investments", "Long Strides", "Pacifist"],
            PreferredTags = Dict(("Weapon", 2.4), ("Apparel", 2.4), ("Relic", 2.0), ("Property", 1.8)),
            PreferredHiddenTags = Dict(("Damage", 2.6), ("Cooldown", 2.6), ("Value", 2.2), ("Shield", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardFired", 2.0), ("TTriggerOnCardSold", 1.6)),
            PreferredEnchantments = Dict(("Obsidian", 2.8), ("Deadly", 2.8), ("Shiny", 2.6), ("Golden", 2.4)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Weapon", "Apparel", "Value"],
            EndgamePowerTag = "MultiTypeMulticast"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_Beehive_Drone_Shield",
            Hero = "Pygmalien",
            NameZh = "蜂巢地产武装防线流",
            DescriptionZh = "购买地产不断养大蜂巢与雄蜂群，搭配 28 小时健身和铁腕战士让每次护盾同时打出连环物理打击。",
            CoreItemNames = ["Beehive", "28 Hour Fitness", "Wrist Warrior"],
            SupportItemNames = ["Showcase", "Brass Knuckles", "Spiked Buckler", "Robe", "Fort", "Badblocker"],
            CoreSkillNames = ["Heavy Mettle", "Shield Bash", "Toughness (Skill)", "Property Mogul", "Endurance"],
            PreferredTags = Dict(("Property", 3.0), ("Weapon", 2.2), ("Apparel", 1.8), ("Tool", 1.6)),
            PreferredHiddenTags = Dict(("Shield", 3.0), ("ShieldReference", 2.8), ("DamageReference", 2.6), ("Charge", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 2.6), ("TTriggerOnCardPurchased", 2.4), ("TTriggerOnCardPerformedShield", 2.4)),
            PreferredEnchantments = Dict(("Shielded", 3.0), ("Obsidian", 2.8), ("Deadly", 2.4), ("Heavy", 2.0)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Property", "Shield", "Weapon"],
            EndgamePowerTag = "ShieldDamageHybrid"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_Economy_CashCannon",
            Hero = "Pygmalien",
            NameZh = "财源炮高收入金币滚雪球流",
            DescriptionZh = "通过 ATM、回馈卡、名片与徽印戒指将收入和累计金币推到极高，用财源炮与拳台臂铠转化成恐怖炮火。",
            CoreItemNames = ["Cash Cannon", "Ring King Gauntlets", "ATM"],
            SupportItemNames = ["Jabalian Dagger", "Monocle", "Oinkment", "Rewards Card", "Business Card", "Cash Register"],
            CoreSkillNames = ["Cash Deposits", "Power Broker", "Decadence", "Healthy Tip", "Open for Business"],
            PreferredTags = Dict(("Weapon", 2.2), ("Property", 2.2), ("Apparel", 1.8), ("Tool", 1.6)),
            PreferredHiddenTags = Dict(("EconomyReference", 3.4), ("Income", 3.2), ("Value", 2.6), ("Damage", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnEncounterSelected", 2.4), ("TTriggerOnCardSold", 2.2), ("TTriggerOnHourStarted", 1.8)),
            PreferredEnchantments = Dict(("Golden", 3.4), ("Deadly", 2.8), ("Obsidian", 2.6), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.CoreCenter],
            EarlyFocusTags = ["EconomyReference", "Income", "Gold"],
            EndgamePowerTag = "GoldIncomeBurst"
        },
        new ArchetypeDefinition
        {
            Id = "Pygmalien_Flying_PrivateJet",
            Hero = "Pygmalien",
            NameZh = "私人喷气机浮空价值流",
            DescriptionZh = "私人喷气机、浮筒滑翔艇与飞猪带动全队起飞升降，同时边飞边叠加相邻价值与护盾充能。",
            CoreItemNames = ["Private Jet", "Pontoon Skimmer", "Uwashiwali Bird"],
            SupportItemNames = ["Flying Pig", "Boomerang", "Piggles Launcher", "Slingshot", "Cosmic Plumage", "Abducted Cow"],
            CoreSkillNames = ["Aerial Assault", "Bombing Run", "Scramble Fighters", "Free Ride", "Tall Buildings"],
            PreferredTags = Dict(("Vehicle", 3.0), ("Friend", 2.0), ("Toy", 1.8), ("Weapon", 1.6)),
            PreferredHiddenTags = Dict(("Flying", 3.4), ("FlyingReference", 3.2), ("Value", 2.4), ("Shield", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardStartedFlying", 2.8), ("TTriggerOnCardStoppedFlying", 2.4), ("TTriggerOnItemUsed", 2.2)),
            PreferredEnchantments = Dict(("Turbo", 2.8), ("Golden", 2.6), ("Shielded", 2.4), ("Deadly", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Flying", "Vehicle", "Value"],
            EndgamePowerTag = "FlyingValueScaling"
        }
    ];
}
