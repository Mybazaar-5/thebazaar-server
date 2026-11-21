using static BazaarCore.Dataset.Archetypes.ArchetypeCatalog;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 第八英雄（Hero8）的 12 套高辨识度节奏、乐器、巨龙与附魔流派蓝图。
/// </summary>
internal static class Hero8Archetypes
{
    public static IReadOnlyList<ArchetypeDefinition> Build() =>
    [
        new ArchetypeDefinition
        {
            Id = "Hero8_ConcertHall_Instrument_Tempo",
            Hero = "Hero8",
            NameZh = "音乐厅节奏乐器多重流",
            DescriptionZh = "以音乐厅赋予全乐器额外多重释放为核心，搭配巨龙竖琴、管风琴与键盘吉他奏出高频节奏协奏。",
            CoreItemNames = ["Concert Hall", "Dragon Harp", "Pipe Organ"],
            SupportItemNames = ["Keytar", "Trumpet", "Mini Keyboard", "Lumi", "Bongos", "Harmonica"],
            CoreSkillNames = ["Virtuoso", "Allegro", "Reverb", "Breakdown Interlude", "Band's All Here"],
            PreferredTags = Dict(("Instrument", 3.5), ("Property", 2.0), ("Tech", 1.6)),
            PreferredHiddenTags = Dict(("Tempo", 3.0), ("TempoReference", 3.0), ("Haste", 2.4), ("Damage", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.0), ("TTriggerOnCardFired", 2.0)),
            PreferredEnchantments = Dict(("Turbo", 3.0), ("Fiery", 2.6), ("Obsidian", 2.6), ("Shiny", 2.6)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.CoreCenter],
            EarlyFocusTags = ["Instrument", "Tempo", "Haste"],
            EndgamePowerTag = "ConcertMulticastTempo"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_Guzheng_Crit_Tempo",
            Hero = "Hero8",
            NameZh = "古筝暴击攒节奏攻防流",
            DescriptionZh = "手提音响、万用表与调音师把暴击率推至满溢，每次暴击源源不断积攒节奏并让古筝同时打出巨额伤害与护盾。",
            CoreItemNames = ["Guzheng", "Boombox", "Sound Engineer"],
            SupportItemNames = ["Tambourine", "Multimeter", "Razor Bow", "Microphone", "Mini Disc Launcher", "Heels"],
            CoreSkillNames = ["Critical Sound", "Alacrity", "Building Crescendo", "Covering Fire", "Debut Album"],
            PreferredTags = Dict(("Instrument", 2.8), ("Weapon", 2.6), ("Tech", 2.0), ("Apparel", 1.6)),
            PreferredHiddenTags = Dict(("Crit", 3.5), ("CritReference", 3.4), ("Tempo", 2.8), ("TempoReference", 2.6)),
            PreferredTriggers = Dict(("TTriggerOnCardCritted", 3.5), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Deadly", 3.5), ("Obsidian", 2.8), ("Shielded", 2.6), ("Turbo", 2.4)),
            PositioningRules = [PositioningRule.FastTriggerLeftOfScaler, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Crit", "Instrument", "Tempo"],
            EndgamePowerTag = "CritTempoGuzheng"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_WhistlingGlaive_TempoBurst",
            Hero = "Hero8",
            NameZh = "呼啸弯刀爆燃节奏斩杀流",
            DescriptionZh = "开局疯狂获取节奏能量，配合死亡金属架子鼓、街机、音箱与呼啸弯刀集中消耗节奏完成爆发斩杀。",
            CoreItemNames = ["Whistling Glaive", "Death Metal Drum Kit", "Arcade Machine"],
            SupportItemNames = ["Shredder", "Cymbal", "Black Lipstick", "Boom Mic", "Ticket Terra", "Fingerless Gloves"],
            CoreSkillNames = ["Fortissimo", "Pianissimo", "Hook", "G Major", "G Minor"],
            PreferredTags = Dict(("Weapon", 3.0), ("Instrument", 2.6), ("Tech", 1.8), ("Apparel", 1.6)),
            PreferredHiddenTags = Dict(("Tempo", 3.4), ("TempoReference", 3.5), ("Damage", 3.0), ("DamageReference", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnPlayerAttributeChanged", 2.8), ("TTriggerOnItemUsed", 2.4), ("TTriggerOnCardFired", 2.0)),
            PreferredEnchantments = Dict(("Obsidian", 3.2), ("Deadly", 3.0), ("Shiny", 2.6), ("Turbo", 2.4)),
            PositioningRules = [PositioningRule.RightMost, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Tempo", "Weapon", "Damage"],
            EndgamePowerTag = "TempoConsumeBurst"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_Dragon_Mecha_Superfan",
            Hero = "Hero8",
            NameZh = "机甲巨龙超级粉丝多重流",
            DescriptionZh = "活动海报、飞行员夹克与利爪耳环围绕巨龙标签构筑，靠超级粉丝吃满巨龙数量的多重释放横扫攻防。",
            CoreItemNames = ["Mecha Dragon", "Superfan", "Bomber Jacket"],
            SupportItemNames = ["Event Poster", "Talon Earrings", "Dragonheart Pendant", "Dragon Statue", "Wings", "Scaled Jacket"],
            CoreSkillNames = ["Honorary Dragons", "Draconic Might", "Draconic Resolve", "Draconic Passion", "Big Guns"],
            PreferredTags = Dict(("Dragon", 3.6), ("Apparel", 2.2), ("Friend", 2.0), ("Weapon", 1.8)),
            PreferredHiddenTags = Dict(("Damage", 2.8), ("Shield", 2.6), ("Burn", 2.0), ("Flying", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 2.6), ("TTriggerOnFightStarted", 2.2), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Obsidian", 3.0), ("Shielded", 2.8), ("Deadly", 2.6), ("Fiery", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Dragon", "Shield", "Damage"],
            EndgamePowerTag = "DragonMulticastSwarm"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_Burn_Ignitar_Scorch",
            Hero = "Hero8",
            NameZh = "炽热吉他阿烤节奏灼烧流",
            DescriptionZh = "阿烤、炽热吉他与键盘吉他互促灼烧叠加，每攒 10 点节奏继续额外释放炽热吉他，辅以烟雾机叠盾续航。",
            CoreItemNames = ["Ignitar", "Scorch", "Keytar"],
            SupportItemNames = ["Dragon Lighter", "Pyrotechnics", "Smoke Machine", "Laser Bow", "Confetti Cannon", "Sparkler"],
            CoreSkillNames = ["Tremolo", "B Major", "B Minor", "Burning Blade", "Conflagration"],
            PreferredTags = Dict(("Instrument", 2.6), ("Dragon", 2.2), ("Tech", 2.0), ("Tool", 1.6)),
            PreferredHiddenTags = Dict(("Burn", 3.5), ("BurnReference", 3.0), ("TempoReference", 2.6), ("Tempo", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedBurn", 2.8), ("TTriggerOnItemUsed", 2.6), ("TTriggerOnPlayerAttributeChanged", 2.2)),
            PreferredEnchantments = Dict(("Fiery", 3.5), ("Turbo", 2.8), ("Shielded", 2.2), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Burn", "Dragon", "Instrument"],
            EndgamePowerTag = "TempoBurnMulticast"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_Apparel_SpikedJacket_Stompers",
            Hero = "Hero8",
            NameZh = "尖刺夹克重踏靴服饰护盾流",
            DescriptionZh = "堆叠重踏靴、羽绒外套、发卡与热裤等服饰护盾件，由尖刺夹克吸纳全体服饰护盾总量轰出重击。",
            CoreItemNames = ["Spiked Jacket", "Stompers", "Puffer Jacket"],
            SupportItemNames = ["Hot Pants", "Scaled Jacket", "Hairpins", "Bandana", "Choker", "Hoodie"],
            CoreSkillNames = ["Thrifted Threads", "Critical Fashion", "Sharp Corners", "D Major", "D Minor"],
            PreferredTags = Dict(("Apparel", 3.6), ("Weapon", 2.2), ("Dragon", 1.6)),
            PreferredHiddenTags = Dict(("Shield", 3.4), ("ShieldReference", 3.2), ("Damage", 2.4), ("Crit", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardCritted", 2.6), ("TTriggerOnCardPerformedShield", 2.4), ("TTriggerOnCardFired", 1.8)),
            PreferredEnchantments = Dict(("Shielded", 3.4), ("Obsidian", 2.8), ("Deadly", 2.6), ("Fiery", 2.2)),
            PositioningRules = [PositioningRule.LeftMost, PositioningRule.CoreCenter, PositioningRule.ShieldOnEdge],
            EarlyFocusTags = ["Apparel", "Shield", "Crit"],
            EndgamePowerTag = "ApparelShieldDamage"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_Enchant_NeonSign_GreenScreen",
            Hero = "Hero8",
            NameZh = "霓虹绿幕全员附魔复制流",
            DescriptionZh = "霓虹灯招牌、镶钻器与喷漆罐给整盘物品打满附魔，再通过绿幕复制强力附魔件与假发柜高频充能。",
            CoreItemNames = ["Neon Sign", "Green Screen", "Wig Closet"],
            SupportItemNames = ["Bedazzler", "Spray Can", "Lightstick", "Light-Up Sneakers", "Lumi", "Spirit Diffuser"],
            CoreSkillNames = ["Disco", "It's All Connected", "Jack of All Trades", "Power Chord", "Resonant Frequency"],
            PreferredTags = Dict(("Tech", 2.8), ("Apparel", 2.4), ("Tool", 2.0), ("Toy", 1.6)),
            PreferredHiddenTags = Dict(("Charge", 3.0), ("Shield", 2.6), ("Tempo", 2.4), ("TempoReference", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnFightStarted", 2.8), ("TTriggerOnCardEnchanted", 2.6), ("TTriggerOnCardFired", 2.0)),
            PreferredEnchantments = Dict(("Shiny", 3.2), ("Turbo", 2.8), ("Radiant", 2.6), ("Shielded", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Tech", "Shield", "Tempo"],
            EndgamePowerTag = "EnchantmentCopyEngine"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_Haste_DragRacer_Blades",
            Hero = "Hero8",
            NameZh = "直线加速车刀片极速飙车流",
            DescriptionZh = "利用特纳、声码器、频闪手雷与合成器创造永动加速链，同时喂大直线加速车、刀片与小提琴弓。",
            CoreItemNames = ["Drag Racer", "Blades", "Violin Bow"],
            SupportItemNames = ["Synth", "Turner", "Vocoder", "Strobe Grenade", "Wireless Headset", "Amp"],
            CoreSkillNames = ["F Major", "F Minor", "Static Acceleration", "Streamline Weapon", "Free Ride"],
            PreferredTags = Dict(("Tech", 3.0), ("Weapon", 2.6), ("Vehicle", 2.2), ("Instrument", 1.8)),
            PreferredHiddenTags = Dict(("Haste", 3.5), ("HasteReference", 3.2), ("Damage", 2.6), ("TempoReference", 2.4)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedHaste", 3.5), ("TTriggerOnPlayerAttributeChanged", 2.2)),
            PreferredEnchantments = Dict(("Turbo", 3.5), ("Obsidian", 2.8), ("Deadly", 2.6), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.FastTriggerLeftOfScaler, PositioningRule.RightMost],
            EarlyFocusTags = ["Haste", "Vehicle", "Weapon"],
            EndgamePowerTag = "HasteTempoCharge"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_Slow_Tuba_FlameSkirt",
            Hero = "Hero8",
            NameZh = "大号火焰裙减速控场流",
            DescriptionZh = "大号、萨克斯风、星光投影仪与干扰器全盘减速压制对手冷却，触发火焰裙灼烧、烟花无人机与贝斯叠伤。",
            CoreItemNames = ["Tuba", "Flame Skirt", "Bass"],
            SupportItemNames = ["Saxophone", "Jammer", "Star Projector", "Fireworks Drone", "Smokey", "Zoot Flute"],
            CoreSkillNames = ["Adagio", "C Major", "C Minor", "Slow Burn", "Matching Tempo"],
            PreferredTags = Dict(("Instrument", 2.8), ("Apparel", 2.0), ("Tech", 2.0), ("Drone", 1.6)),
            PreferredHiddenTags = Dict(("Slow", 3.5), ("SlowReference", 3.2), ("Burn", 2.4), ("DamageReference", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnCardPerformedSlow", 3.5), ("TTriggerOnFightStarted", 2.0)),
            PreferredEnchantments = Dict(("Heavy", 3.5), ("Fiery", 2.6), ("Icy", 2.2), ("Turbo", 2.2)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Slow", "Instrument", "Burn"],
            EndgamePowerTag = "SlowTempoControl"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_Flying_HoverPad_Wings",
            Hero = "Hero8",
            NameZh = "飘浮垫翅膀巨龙空战流",
            DescriptionZh = "借助飘浮垫消耗节奏充能飞行阵型，配合翅膀、ISO-贝尔、灯光无人机与炫羽在空中快速滚雪球。",
            CoreItemNames = ["Hover Pad", "Wings", "ISO-Belle"],
            SupportItemNames = ["Light Drone", "Scaled Jacket", "Cosmic Amulet", "Cosmic Plumage", "Abducted Cow", "Dragon Whelp"],
            CoreSkillNames = ["Leaping Tempo", "Bombing Run", "Scramble Fighters", "Aerial Assault", "Draconic Might"],
            PreferredTags = Dict(("Tech", 2.6), ("Dragon", 2.4), ("Drone", 2.0), ("Apparel", 2.0)),
            PreferredHiddenTags = Dict(("Flying", 3.5), ("FlyingReference", 3.4), ("Charge", 2.4), ("Haste", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnCardStartedFlying", 3.0), ("TTriggerOnItemUsed", 2.6), ("TTriggerOnFightStarted", 2.0)),
            PreferredEnchantments = Dict(("Turbo", 3.0), ("Deadly", 2.8), ("Shielded", 2.4), ("Fiery", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Flying", "Tech", "Tempo"],
            EndgamePowerTag = "FlyingTempoCharge"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_Chibi_Toy_PhotoBooth",
            Hero = "Hero8",
            NameZh = "照相亭Q版娃娃玩具伙伴流",
            DescriptionZh = "照相亭每日为全队伙伴增设新类型，围绕一组高协同 Q 版娃娃玩具和伴舞触发高频充能、护盾和节奏回复。",
            CoreItemNames = ["Photo Booth", "Backup Dancer", "Chronos Chibi"],
            SupportItemNames = ["Quixel Chibi", "Shelby Chibi", "Aila Chibi", "Hef Chibi", "Jin Chibi", "Cobweb Chibi"],
            CoreSkillNames = ["Balanced Friendship", "Beautiful Friendship", "Friendzone", "Running with Scissors", "Pillow Fort"],
            PreferredTags = Dict(("Toy", 3.4), ("Friend", 3.4), ("Property", 1.8)),
            PreferredHiddenTags = Dict(("Tempo", 2.8), ("Charge", 2.6), ("Haste", 2.4), ("Shield", 2.0)),
            PreferredTriggers = Dict(("TTriggerOnItemUsed", 3.2), ("TTriggerOnDayStarted", 2.0), ("TTriggerOnPlayerAttributeChanged", 2.0)),
            PreferredEnchantments = Dict(("Turbo", 3.0), ("Shielded", 2.6), ("Fiery", 2.4), ("Shiny", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
            EarlyFocusTags = ["Toy", "Friend", "Tempo"],
            EndgamePowerTag = "ChibiToyTempoLoop"
        },
        new ArchetypeDefinition
        {
            Id = "Hero8_EquipmentVan_MusicNote_Flex",
            Hero = "Hero8",
            NameZh = "装备车音符跨职业百变流",
            DescriptionZh = "装备车吸纳储存箱全类型并每日白嫖跨英雄单卡，结合音符转化与巡演日程学习跨英雄技能打造百变大核。",
            CoreItemNames = ["Equipment Van", "Tour Bus", "Tour Schedule"],
            SupportItemNames = ["Kicks", "Power Bank", "Disco Drone", "Ticket Terra", "A Note", "D Note"],
            CoreSkillNames = ["A Major", "E Major", "It's All Connected", "Full Arsenal", "Master Salesman"],
            PreferredTags = Dict(("Vehicle", 3.0), ("Tech", 2.4), ("Dragon", 1.8), ("Apparel", 1.6)),
            PreferredHiddenTags = Dict(("EconomyReference", 2.6), ("Value", 2.4), ("Quest", 2.4), ("Shield", 2.2)),
            PreferredTriggers = Dict(("TTriggerOnDayStarted", 2.6), ("TTriggerOnCardSold", 2.6), ("TTriggerOnCardQuestCompleted", 2.2)),
            PreferredEnchantments = Dict(("Golden", 3.0), ("Shiny", 2.8), ("Shielded", 2.6), ("Turbo", 2.4)),
            PositioningRules = [PositioningRule.CoreCenter, PositioningRule.LeftMost],
            EarlyFocusTags = ["Vehicle", "EconomyReference", "Value"],
            EndgamePowerTag = "MultiTypeCrossHero"
        }
    ];
}
