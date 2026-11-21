using static BazaarCore.Dataset.Archetypes.ArchetypeCatalog;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 朱尔斯（Jules）的 12 套社区主流与高协同流派蓝图。
/// </summary>
internal static class JulesArchetypes
{
    public static IReadOnlyList<ArchetypeDefinition> Build() =>
    [
        new ArchetypeDefinition
        {
            Id = "Jules_Heated_Grill_Wok_Burn",
            Hero = "Jules",
            NameZh = "烧烤架巨型炒锅高温灼烧流",
            DescriptionZh = "以烤箱和烧烤架赋予全食物高温状态，激活巨型炒锅的多重释放并让辣酱、腌辣椒叠加海量灼烧。",
            CoreItemNames = ["Grill", "Jumbo Wok", "Oven"],
            SupportItemNames = ["Hot Box", "Pickled Peppers", "Hot Sauce", "Scorchpepper", "Tea Siphon", "Toaster", "Giant Lollipop"],
            CoreSkillNames = ["Rolling Boil", "Hot Meal", "Piping Hot", "Conflagration", "Searing Flames"],
            PreferredTags = Dict(("Food", 3.2), ("Tool", 2.8)),
            PreferredHiddenTags = Dict(("HeatedReference", 3.5), ("Burn", 3.4), ("BurnReference", 3.0), ("Charge", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 2.6), ("TTriggerOnCardPerformedBurn", 2.6), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Fiery", 3.4), ("Turbo", 2.8), ("Shiny", 2.6), ("Shielded", 2.0)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["HeatedReference", "Burn", "Food"],
            EndgamePowerTag = "HeatedBurnOven"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_Chilled_Freezer_Slushee",
            Hero = "Jules",
            NameZh = "冷库冻酸奶冰沙霜冻流",
            DescriptionZh = "冷库、冻酸奶车与搅拌机把小型食物转化为霜冻冰沙群，配合冰天鹅赋予霜冻多重释放与冻结爆发。",
            CoreItemNames = ["Walk-In Freezer", "Froyo Cart", "Ice Swan"],
            SupportItemNames = ["Blender", "Slushee", "Sorbet", "Mini Fridge", "Ice Cubes", "Strawberries", "Nullfrost Altar"],
            CoreSkillNames = ["Flash Freeze", "Chilled Delights", "Shatter", "Refreshing Treat", "Coolant Leak"],
            PreferredTags = Dict(("Food", 3.0), ("Tool", 2.4), ("Vehicle", 1.8), ("Weapon", 1.6)),
            PreferredHiddenTags = Dict(("ChilledReference", 3.5), ("Freeze", 3.4), ("FreezeReference", 3.0), ("Shield", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedFreeze", 3.4), ("TTriggerOnItemUsed", 2.4)),
            PreferredEnchantments = Dict(("Icy", 3.5), ("Shielded", 2.6), ("Obsidian", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["ChilledReference", "Freeze", "Food"],
            EndgamePowerTag = "ChilledFreezeBurst"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_Shield_Gingerbread_Cheese",
            Hero = "Jules",
            NameZh = "姜饼小屋奶酪轮高盾反打流",
            DescriptionZh = "通过奶酪轮翻倍叠盾和巨型三明治复制最高食物护盾，搭配榴莲和番红花将厚重护盾化为致命输出。",
            CoreItemNames = ["Gingerbread House", "Giant Sub", "Cheese Wheel"],
            SupportItemNames = ["Durian", "Saffron", "Blueberry Pie", "Apron", "Colander", "Chef Hat"],
            CoreSkillNames = ["Heat Shield", "Quick Defenses", "Sharp Corners", "Heavy Mettle", "Critical Protector"],
            PreferredTags = Dict(("Food", 3.2), ("Tool", 2.0), ("Property", 1.8), ("Apparel", 1.6)),
            PreferredHiddenTags = Dict(("Shield", 3.5), ("ShieldReference", 3.4), ("Burn", 2.0), ("Damage", 1.8)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 2.4), ("TTriggerOnCardPerformedShield", 2.6), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Shielded", 3.4), ("Obsidian", 2.8), ("Fiery", 2.4), ("Restorative", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.ShieldOnEdge],
            EarlyFocusTags = ["Shield", "Food", "Tool"],
            EndgamePowerTag = "FoodShieldScaling"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_Regen_Garden_Feast",
            Hero = "Jules",
            NameZh = "农贸种植园盛宴再生流",
            DescriptionZh = "农贸集市与蔬菜种植园互乘最大生命值与再生量，让盛宴、阿扎坎乱炖与意大利面越打回复越高。",
            CoreItemNames = ["Veggie Garden", "Farmer's Market", "Feast"],
            SupportItemNames = ["Azakan Stew", "Pasta", "Bread", "Rice", "Poke", "Basket"],
            CoreSkillNames = ["Food Coma", "Healthy Lifestyle", "Vital Reserve", "Walk-in Clinic", "Waters of Infinity"],
            PreferredTags = Dict(("Food", 3.2), ("Property", 2.6)),
            PreferredHiddenTags = Dict(("Regen", 3.5), ("RegenReference", 3.2), ("Health", 2.6), ("HealthReference", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 2.4), ("TTriggerOnCardPerformedRegen", 2.4), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Mossy", 3.5), ("Shielded", 2.6), ("Fiery", 2.4), ("Toxic", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Regen", "Food", "Health"],
            EndgamePowerTag = "RegenMaxHpGarden"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_Tool_Dishwasher_KnifeSet",
            Hero = "Jules",
            NameZh = "洗碗机刀具组厨具齐鸣流",
            DescriptionZh = "洗碗机加速全厨具并赋予高温加成，餐具锻造炉与刀具组在高频工具触发中连续增伤灼烧。",
            CoreItemNames = ["Dishwasher", "Cutlery Forge", "Knife Set"],
            SupportItemNames = ["Attachment Set", "Cleaver", "Santoku", "Cutting Board", "Meat Grinder", "Grater"],
            CoreSkillNames = ["Re-Tooled", "Tools of the Trade", "Plowshares to Swords", "The Right Tool", "Farm To Table"],
            PreferredTags = Dict(("Tool", 3.5), ("Weapon", 2.8), ("Property", 1.6)),
            PreferredHiddenTags = Dict(("Damage", 3.0), ("DamageReference", 2.8), ("Haste", 2.2), ("HeatedReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.2), ("TTriggerOnBeforeItemUsed", 2.0), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Obsidian", 3.0), ("Deadly", 2.8), ("Turbo", 2.8), ("Fiery", 2.4)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.FastTriggerLeftOfScaler],
            EarlyFocusTags = ["Tool", "Weapon", "Damage"],
            EndgamePowerTag = "ToolKitchenSwarm"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_Haste_FoodTruck_Butter",
            Hero = "Jules",
            NameZh = "餐车黄油高速上菜流",
            DescriptionZh = "餐车把每次对食物的加速直接转为充能，配合厨师机、黄油、微波炉与厨房秤打造超高速料理引擎。",
            CoreItemNames = ["Food Truck", "Stand Mixer", "Butter"],
            SupportItemNames = ["Microwave", "Kitchen Scale", "Pasta Maker", "Rolling Pin", "Coffee", "Zarlic"],
            CoreSkillNames = ["Fast Food", "Morning Rush", "Street Food", "Static Acceleration", "Streamline Weapon"],
            PreferredTags = Dict(("Food", 3.0), ("Tool", 2.8), ("Vehicle", 2.0)),
            PreferredHiddenTags = Dict(("Haste", 3.5), ("HasteReference", 3.2), ("Charge", 2.6), ("Cooldown", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedHaste", 3.4), ("TTriggerOnCardFired", 2.0)),
            PreferredEnchantments = Dict(("Turbo", 3.4), ("Shiny", 2.6), ("Fiery", 2.2), ("Obsidian", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Haste", "Food", "Tool"],
            EndgamePowerTag = "FoodHasteCharge"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_Slow_Luau_Bubblegum",
            Hero = "Jules",
            NameZh = "热带野宴泡泡糖减速流",
            DescriptionZh = "通过泡泡糖地板、陈年佳酿与烤野猪高频触发减速，同步驱动热带野宴再生、伊姆灼烧与刨丝器连击。",
            CoreItemNames = ["Giant Lollipop", "Luau", "Bubblegum Floor", "Excellent Vintage"],
            SupportItemNames = ["Pumpkin", "Imu", "Boar Roast", "Curry", "Honeycomb", "Grater"],
            CoreSkillNames = ["Dirty Dishes", "Slow Burn", "Slow and Steady", "Calming Heals", "Slippery Fella"],
            PreferredTags = Dict(("Food", 3.0), ("Property", 2.4), ("Tool", 1.8)),
            PreferredHiddenTags = Dict(("Slow", 3.5), ("SlowReference", 3.2), ("Regen", 2.2), ("Shield", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedSlow", 3.4), ("TTriggerOnItemUsed", 2.2)),
            PreferredEnchantments = Dict(("Heavy", 3.4), ("Mossy", 2.6), ("Fiery", 2.4), ("Shielded", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Slow", "Food", "Property"],
            EndgamePowerTag = "SlowRegenControl"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_Crit_SushiBoat_Salt",
            Hero = "Jules",
            NameZh = "寿司船鱼子酱食盐暴击流",
            DescriptionZh = "以寿司船、鱼子酱、三德刀与万能酱料堆满美食暴击率，触发食盐暴击灼烧、巨龙肉排和上菜托盘充能。",
            CoreItemNames = ["Sushi Boat", "Dragon Steak", "Salt"],
            SupportItemNames = ["Caviar", "Spice Rack", "Serving Platter", "Universal Sauce", "Skewer", "Spatula"],
            CoreSkillNames = ["Gold Garnish", "Inspirational Cooking", "Catch of the Day", "Tracer Fire", "Building Crescendo"],
            PreferredTags = Dict(("Food", 3.2), ("Tool", 2.0), ("Vehicle", 1.8), ("Dragon", 1.4)),
            PreferredHiddenTags = Dict(("Crit", 3.5), ("CritReference", 3.4), ("Burn", 2.4), ("Value", 1.8)),
            PreferredTriggers = Dict(("TTriggerOnCardCritted", 3.4), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Deadly", 3.5), ("Fiery", 2.8), ("Golden", 2.4), ("Obsidian", 2.4)),
            PositioningRules = [PositioningRule.FastTriggerLeftOfScaler, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Crit", "Food", "Burn"],
            EndgamePowerTag = "FoodCritBurn"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_Cleaver_Health_Veggie",
            Hero = "Jules",
            NameZh = "巨型菜刀龙瓜大血牛流",
            DescriptionZh = "利用火龙瓜、意大利面、烤野猪和蔬菜种植园暴涨最大生命值，再靠巨型菜刀暴击百分比斩杀对手。",
            CoreItemNames = ["Massive Cleaver", "Dragonmelon", "Veggie Garden"],
            SupportItemNames = ["Pasta", "Boar Roast", "Beach Umbrella", "Refrigerator", "Cooler", "Cake Batter"],
            CoreSkillNames = ["Protein Powder", "Farm To Table", "Big Guns", "Healthy Heart", "Second Wind"],
            PreferredTags = Dict(("Food", 2.8), ("Weapon", 2.4), ("Tool", 2.0), ("Property", 1.8)),
            PreferredHiddenTags = Dict(("Health", 3.4), ("HealthReference", 3.2), ("Damage", 2.6), ("CritReference", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardCritted", 2.6), ("TTriggerOnCardPurchased", 2.0), ("TTriggerOnItemUsed", 2.0)),
            PreferredEnchantments = Dict(("Obsidian", 3.2), ("Deadly", 3.0), ("Shielded", 2.4), ("Restorative", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Health", "Weapon", "Food"],
            EndgamePowerTag = "MaxHpCleaverCrit"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_Destroy_FruitPress_Cleaver",
            Hero = "Jules",
            NameZh = "榨汁机美食献祭暴打流",
            DescriptionZh = "储粮柜和制面机每天源源不断产出小食物供榨汁机摧毁，让榨汁机伤害持续翻倍并由开瓶器连续引爆。",
            CoreItemNames = ["Fruit Press", "Pantry", "Cookbook"],
            SupportItemNames = ["Corkscrew", "Cookie Cutters", "Apron", "Chef Hat", "Egg Timer", "Pizza Cutter"],
            CoreSkillNames = ["Secret Ingredient", "War Chef", "Table Setting", "Big Guns", "Experimental Chef"],
            PreferredTags = Dict(("Food", 3.0), ("Tool", 2.8), ("Weapon", 2.4), ("Property", 1.6)),
            PreferredHiddenTags = Dict(("Damage", 3.0), ("DamageReference", 2.8), ("Charge", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnBeforeCardDestroyed", 2.8), ("TTriggerOnDayStarted", 2.4), ("TTriggerOnItemUsed", 2.2)),
            PreferredEnchantments = Dict(("Obsidian", 3.2), ("Deadly", 3.0), ("Shiny", 2.4), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.RightMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Food", "Weapon", "Damage"],
            EndgamePowerTag = "FoodSacrificeMultiplier"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_SoloFood_Laurel_Fortress",
            Hero = "Jules",
            NameZh = "劳雷尔堡垒单美食混合流",
            DescriptionZh = "利用劳雷尔堡垒的多标签多重释放与劳雷尔的最爱单食物翻倍效果，结合丰饶之角实现温差双修永动。",
            CoreItemNames = ["Laurel's Fortress", "Laurel's Favorite", "Cornucopia"],
            SupportItemNames = ["Prep Station", "Cake Batter", "Birthday Cake", "Flaming Gelato", "LauLau", "Ulu Knife"],
            CoreSkillNames = ["Secret Ingredient", "I Would Eat That", "It's All Connected", "Some Like It Hot", "Quick Bite"],
            PreferredTags = Dict(("Food", 2.8), ("Property", 2.4), ("Tool", 2.2), ("Friend", 1.8)),
            PreferredHiddenTags = Dict(("HeatedReference", 2.6), ("ChilledReference", 2.6), ("Shield", 2.6), ("Regen", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 2.6), ("TTriggerOnCardFired", 2.0)),
            PreferredEnchantments = Dict(("Shielded", 3.0), ("Fiery", 2.6), ("Icy", 2.4), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Food", "Property", "Shield"],
            EndgamePowerTag = "HybridTemperatureCore"
        },
        new ArchetypeDefinition
        {
            Id = "Jules_Toy_Trap_MouseTrap",
            Hero = "Jules",
            NameZh = "鼠夹冰箱贴画玩具美食流",
            DescriptionZh = "冰箱贴画把小型厨具视为玩具，搭配劳雷尔的弹弹球高速加速、老鼠夹叠弹药轰击与黑胡椒相邻充能。",
            CoreItemNames = ["Mouse Trap", "Fridge Art", "Laurel's Bouncy Ball"],
            SupportItemNames = ["Fire Fly Trap", "Seafood Cracker", "Whisk", "Black Pepper", "Pizza", "Instant Noodles"],
            CoreSkillNames = ["Tiny Dancer", "Nanobot Construction", "Ammo Stash", "Mixed Message", "The Right Tool"],
            PreferredTags = Dict(("Tool", 2.8), ("Toy", 2.8), ("Food", 2.4), ("Trap", 1.8)),
            PreferredHiddenTags = Dict(("Ammo", 2.6), ("Damage", 2.6), ("Haste", 2.4), ("Charge", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedHaste", 2.6), ("TTriggerOnDayStarted", 2.0), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Deadly", 3.0), ("Obsidian", 2.8), ("Turbo", 2.6), ("Fiery", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Tool", "Food", "Damage"],
            EndgamePowerTag = "SmallFoodTrapAmmo"
        }
    ];
}
