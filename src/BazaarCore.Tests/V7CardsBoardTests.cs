using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V7 Official Parity 卡牌、棋盘与任务规则专项单元测试：
/// 覆盖 Stash 光环战斗隔离、Tiny 占格与 Transform 保留/跳金、动态效果/附魔标签、BoardGrid 挤位换位及 Quest 计数修复。
/// </summary>
public sealed class V7CardsBoardTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());

    private static JsonElement JsonFixed(double val)
        => JsonDocument.Parse($"{{\"$type\":\"TFixedValue\",\"Value\":{val.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}").RootElement.Clone();

    private static JsonElement JsonTagCondition(string tag)
        => JsonDocument.Parse($"{{\"$type\":\"TCardConditionalTag\",\"Tags\":[\"{tag}\"],\"Operator\":\"Any\"}}").RootElement.Clone();

    private static RuleNode MakeSpawnByIdContext(string cardId, bool? inheritEnchantment = null)
    {
        var behaviors = new List<RuleNode>
        {
            new() { Type = "TSpawnBehaviorIgnoreHero", IgnoreHero = true },
            new() { Type = "TSpawnBehaviorIgnoreTierTable", IgnoreTierTable = true },
            new() { Type = "TSpawnBehaviorAllowDuplicates", AllowDuplicates = true }
        };
        if (inheritEnchantment.HasValue)
            behaviors.Add(new RuleNode { Type = "TSpawnBehaviorInheritEnchantment", Inherits = inheritEnchantment.Value });

        return new RuleNode
        {
            Type = "TSpawnContextQuery",
            Groups =
            [
                new RuleNode
                {
                    Type = "TSpawnGroup",
                    Behaviors = behaviors,
                    Filters =
                    [
                        new RuleNode { Type = "TSpawnFilterIdList", Ids = [cardId] }
                    ]
                }
            ]
        };
    }

    private static CardTemplate MakeItemTemplate(
        string id,
        string size = "Small",
        string startingTier = "Bronze",
        IEnumerable<string>? tags = null,
        IEnumerable<string>? hiddenTags = null,
        Dictionary<string, double>? baseAttrs = null,
        Dictionary<string, AbilityDefinition>? abilities = null,
        Dictionary<string, AuraDefinition>? auras = null,
        Dictionary<string, EnchantmentData>? enchantments = null,
        List<QuestDefinition>? quests = null,
        string cardType = "Item")
    {
        return new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = cardType,
            Size = size,
            StartingTier = startingTier,
            Tags = tags?.ToList() ?? [],
            HiddenTags = hiddenTags?.ToList() ?? [],
            Attributes = baseAttrs ?? new Dictionary<string, double>(),
            Abilities = abilities ?? new Dictionary<string, AbilityDefinition>(),
            Auras = auras ?? new Dictionary<string, AuraDefinition>(),
            Enchantments = enchantments ?? new Dictionary<string, EnchantmentData>(),
            Quests = quests ?? []
        };
    }

    private static AuraDefinition MakeHandBuffAura(string id, string activeIn, string worksIn, double damageBonus) => new()
    {
        Id = id,
        ActiveIn = activeIn,
        WorksIn = worksIn,
        Action = new RuleNode
        {
            Type = "TAuraActionCardModifyAttribute",
            AttributeType = "DamageAmount",
            Operation = "Add",
            RawValueNode = JsonFixed(damageBonus),
            Target = new RuleNode
            {
                Type = "TTargetCardSection",
                TargetSection = "SelfHand",
                ExcludeSelf = true
            }
        }
    };

    // =========================================================================
    // Task 1: [4.3] Stash 光环战斗隔离
    // =========================================================================

    [Fact]
    public void StashAura_InCombat_ShouldIsolateRegularHandAndStashCombatAuras_WhileKeepingOutOfCombatAndStashOnlyException()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("p1", "Player1", HeroNames.Vanessa, 500, 10, 10, 10);
        var p2 = new PlayerState("p2", "Player2", HeroNames.Pygmalien, 500, 10, 10, 10);

        var weapon = new CardInstance(MakeItemTemplate(
            "v7_hand_weapon",
            tags: ["Weapon"],
            baseAttrs: new Dictionary<string, double> { ["DamageAmount"] = 10, ["CooldownMax"] = 2000 }));
        p1.Hand.TryPlaceWithDisplaceOrSwap(weapon, 0);

        // 常规 HandAndStash 战斗光环置于 Stash：局内不得向 Hand 生效
        var regularStashBuffer = new CardInstance(MakeItemTemplate(
            "v7_stash_handandstash",
            auras: new Dictionary<string, AuraDefinition>
            {
                ["a1"] = MakeHandBuffAura("a1", activeIn: "HandAndStash", worksIn: "Anywhere", damageBonus: 50)
            }));
        p1.Stash.TryPlaceWithDisplaceOrSwap(regularStashBuffer, 0);

        // 真实 DB 卡 Speedrunner [Deadly]（ActiveIn=HandAndStash, WorksIn=Anywhere, SelfHand CritChance+30）在 Stash 中同样不得在局内加成 Hand
        var realSpeedrunner = new CardInstance(repo.GetCard("2215483f-1fc3-495a-b492-d917dd7d588c")!, enchantment: "Deadly");
        p1.Stash.TryPlaceWithDisplaceOrSwap(realSpeedrunner, 1);

        // 局外（OutOfCombat）仍保留 HandAndStash 经济/商店光环（例如真实 Loupe SellPrice 加成 & regular HandAndStash 光环）
        var realLoupe = new CardInstance(repo.GetCard("234ae6aa-b94b-456d-a2fb-f0cbcaa9c655")!); // Loupe
        p1.Stash.TryPlaceWithDisplaceOrSwap(realLoupe, 2);

        double sellBeforeOoc = weapon.GetAttribute("SellPrice");
        AuraEngine.RecalculateAuras(p1, null, RuleScope.OutOfCombat, p1.Day, p1.Hour, repo);
        Assert.Equal(60, weapon.GetAttribute("DamageAmount"));
        Assert.True(weapon.GetAttribute("SellPrice") > sellBeforeOoc, "局外 Stash 中的 Loupe HandAndStash 经济光环应保持生效");

        // 切换为局内 InCombat：Stash 中常规 HandAndStash 光环不得加成 Hand
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
        Assert.Equal(10, weapon.GetAttribute("DamageAmount"));
        Assert.Equal(0, weapon.GetAttribute("CritChance"));

        // 显式 StashOnly 局内例外应允许生效
        var stashOnlyException = new CardInstance(MakeItemTemplate(
            "v7_stash_only_exception",
            auras: new Dictionary<string, AuraDefinition>
            {
                ["so"] = MakeHandBuffAura("so", activeIn: "StashOnly", worksIn: "CombatOnly", damageBonus: 25)
            }));
        p1.Stash.TryPlaceWithDisplaceOrSwap(stashOnlyException, 3);
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
        Assert.Equal(35, weapon.GetAttribute("DamageAmount"));

        // 若把 regularStashBuffer 放到 Hand 中，则应在 InCombat 生效
        p1.Stash.Remove(regularStashBuffer);
        p1.Hand.TryPlaceWithDisplaceOrSwap(regularStashBuffer, 1);
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
        Assert.Equal(85, weapon.GetAttribute("DamageAmount"));
    }

    // =========================================================================
    // Task 2: [4.10] Tiny 物理尺寸缩水与同步 + Transform 属性/附魔保留 + 跳过 Legendary
    // =========================================================================

    [Fact]
    public void TinyAttribute_ShouldShrinkEffectiveSizeAndSlotWidth_AndSyncAttributeWithTags()
    {
        var large = new CardInstance(MakeItemTemplate("v7_large", size: "Large"));
        var medium = new CardInstance(MakeItemTemplate("v7_medium", size: "Medium"));
        var small = new CardInstance(MakeItemTemplate("v7_small", size: "Small"));

        Assert.Equal(CardSize.Large, large.EffectiveSize);
        Assert.Equal(3, large.SlotWidth);
        Assert.Equal(CardSize.Medium, medium.EffectiveSize);
        Assert.Equal(2, medium.SlotWidth);
        Assert.Equal(CardSize.Small, small.EffectiveSize);
        Assert.Equal(1, small.SlotWidth);

        // 永久 Tiny
        large.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);
        medium.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);
        small.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);

        Assert.True(large.IsTiny && medium.IsTiny && small.IsTiny);
        Assert.Equal(CardSize.Medium, large.EffectiveSize);
        Assert.Equal(2, large.SlotWidth);
        Assert.Equal(CardSize.Small, medium.EffectiveSize);
        Assert.Equal(1, medium.SlotWidth);
        Assert.Equal(CardSize.Small, small.EffectiveSize);
        Assert.Equal(1, small.SlotWidth);

        // 检查属性与标签同步
        Assert.Equal(1, large.GetAttribute("Tiny"));
        Assert.True(large.HasTag("Tiny"));
        Assert.Contains("Tiny", large.ExtraTags);
        Assert.Contains("Tiny", large.GetAllTags());

        // 战斗临时 Tiny 应在 ResetForCombat 后清理
        var combatTiny = new CardInstance(MakeItemTemplate("v7_combat_tiny", size: "Large"));
        combatTiny.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Combat);
        Assert.Equal(2, combatTiny.SlotWidth);
        Assert.Contains("Tiny", combatTiny.CombatTags);
        combatTiny.ResetForCombat(null);
        Assert.False(combatTiny.IsTiny);
        Assert.Equal(3, combatTiny.SlotWidth);
        Assert.False(combatTiny.HasTag("Tiny"));

        // 反过来：仅加 ExtraTag("Tiny") 亦应保持 IsTiny / GetAttribute("Tiny") 同步
        var tagSynced = new CardInstance(MakeItemTemplate("v7_tag_tiny", size: "Medium"));
        tagSynced.ExtraTags.Add("Tiny");
        Assert.True(tagSynced.IsTiny);
        Assert.Equal(1, tagSynced.GetAttribute("Tiny"));
        Assert.Equal(1, tagSynced.SlotWidth);

        // 物理摆盘检验：1 个 Tiny Large(2格) + 4 个 Tiny Medium(各1格) 可正好塞满 6 格已解锁棋盘
        var grid = new BoardGrid(unlockedMask: 0b00_1111_1100); // 仅 2..7 共 6 格解锁
        Assert.True(grid.TryPlaceWithDisplaceOrSwap(large, 2));
        for (int i = 0; i < 4; i++)
        {
            var tm = new CardInstance(MakeItemTemplate($"v7_tiny_med_{i}", size: "Medium"));
            tm.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);
            Assert.True(grid.TryAddAuto(tm));
        }
        Assert.Equal(5, grid.Cards.Count);
    }

    [Fact]
    public void TransformAndUpgrade_ShouldPreservePermanentAddExtraTagsTinyAndCompatibleEnchantment()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 10, 10, 10);

        // 用真实 DB 物品 Duct Tape (支持 Fiery 附魔等) 与另一件真实物品检验兼容附魔保留
        var ductTapeTpl = repo.GetCard("0966f887-5aaf-44a5-90fa-ecb194270513")!;
        Assert.Contains("Fiery", ductTapeTpl.Enchantments!.Keys);

        var originTpl = MakeItemTemplate(
            "v7_transform_origin",
            size: "Large",
            tags: ["Tool"],
            baseAttrs: new Dictionary<string, double> { ["DamageAmount"] = 5 },
            enchantments: new Dictionary<string, EnchantmentData>
            {
                ["Fiery"] = new EnchantmentData { HiddenTags = ["Burn"] },
                ["Icy"] = new EnchantmentData { HiddenTags = ["Freeze"] }
            });

        var origin = new CardInstance(originTpl, enchantment: "Fiery");
        origin.ModifyAttribute("DamageAmount", "Add", 35, DurationTarget.Permanent);
        origin.ModifyAttribute("Custom_0", "Add", 4, DurationTarget.Permanent);
        origin.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);
        origin.ExtraTags.Add("Relic");
        p1.Hand.TryPlaceWithDisplaceOrSwap(origin, 2);

        // 1. 升级必须完整保留上述状态
        var upgradeAction = new RuleNode
        {
            Type = "TActionCardUpgrade",
            UpgradeToTier = "Silver",
            Target = new RuleNode { Type = "TTargetCardSelf" }
        };
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, null, new Random(1)) { SelfCard = origin };
        Assert.True(AbilityExecutor.ExecuteAction(upgradeAction, origin, ctx));
        Assert.Equal(CardTier.Silver, origin.Tier);
        Assert.Equal(35, origin.PermanentAdd["DamageAmount"]);
        Assert.True(origin.IsTiny);
        Assert.Equal("Fiery", origin.Enchantment);
        Assert.Contains("Relic", origin.ExtraTags);

        // 2. Transform 为支持 Fiery 的真实模板 (Duct Tape)：保留 PermanentAdd / ExtraTags / Tiny / 兼容 Enchantment
        var transformAction = new RuleNode
        {
            Type = "TActionCardTransform",
            Target = new RuleNode { Type = "TTargetCardSelf" },
            SpawnContext = MakeSpawnByIdContext(ductTapeTpl.Id)
        };
        CardInstance? transformed = null;
        ctx.OnCardTransformed = (_, _, rep) => transformed = rep;
        Assert.True(AbilityExecutor.ExecuteAction(transformAction, origin, ctx));
        Assert.NotNull(transformed);
        Assert.Equal(ductTapeTpl.Id, transformed!.Template.Id);
        Assert.Equal(35, transformed.PermanentAdd["DamageAmount"]);
        Assert.Equal(4, transformed.PermanentAdd["Custom_0"]);
        Assert.True(transformed.IsTiny);
        Assert.Contains("Tiny", transformed.ExtraTags);
        Assert.Contains("Relic", transformed.ExtraTags);
        Assert.Equal("Fiery", transformed.Enchantment);
        Assert.True(transformed.HasTag("Burn"));

        // 3. 当源附魔与目标模板不兼容（目标模板不含 "NonExistentCustomEnch"）时，不得强行保留不兼容附魔
        var noEnchTargetTpl = repo.GetCard("7b30db1a-80c8-4889-ae2f-12b0ad047796")!; // Sizeinator (Legendary 仅测试目标 Enchantments 检查)
        var tinyNoEnchOrigin = new CardInstance(originTpl, enchantment: "NonExistentCustomEnch");
        tinyNoEnchOrigin.ModifyAttribute("PermanentTestAttr", "Add", 9, DurationTarget.Permanent);
        p1.Hand.TryPlaceWithDisplaceOrSwap(tinyNoEnchOrigin, 5);
        var incompatibleTransform = new RuleNode
        {
            Type = "TActionCardTransform",
            Target = new RuleNode { Type = "TTargetCardSelf" },
            SpawnContext = MakeSpawnByIdContext(noEnchTargetTpl.Id)
        };
        CardInstance? transformed2 = null;
        var ctx2 = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, null, new Random(2))
        {
            SelfCard = tinyNoEnchOrigin,
            OnCardTransformed = (_, _, rep) => transformed2 = rep
        };
        Assert.True(AbilityExecutor.ExecuteAction(incompatibleTransform, tinyNoEnchOrigin, ctx2));
        Assert.NotNull(transformed2);
        Assert.Null(transformed2!.Enchantment);
        Assert.Equal(9, transformed2.PermanentAdd["PermanentTestAttr"]);

        // 4. 显式配置 TSpawnBehaviorInheritEnchantment(Inherits=false) 时不继承附魔，但仍保留 PermanentAdd / ExtraTags / Tiny
        var explicitNoInheritOrigin = new CardInstance(originTpl, enchantment: "Fiery");
        explicitNoInheritOrigin.ModifyAttribute("Custom_0", "Add", 7, DurationTarget.Permanent);
        explicitNoInheritOrigin.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);
        explicitNoInheritOrigin.ExtraTags.Add("Relic");
        p1.Hand.TryPlaceWithDisplaceOrSwap(explicitNoInheritOrigin, 8);
        var noInheritTransform = new RuleNode
        {
            Type = "TActionCardTransform",
            Target = new RuleNode { Type = "TTargetCardSelf" },
            SpawnContext = MakeSpawnByIdContext(ductTapeTpl.Id, inheritEnchantment: false)
        };
        CardInstance? transformed3 = null;
        var ctx3 = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, null, new Random(3))
        {
            SelfCard = explicitNoInheritOrigin,
            OnCardTransformed = (_, _, rep) => transformed3 = rep
        };
        Assert.True(AbilityExecutor.ExecuteAction(noInheritTransform, explicitNoInheritOrigin, ctx3));
        Assert.NotNull(transformed3);
        Assert.Null(transformed3!.Enchantment);
        Assert.Equal(7, transformed3.PermanentAdd["Custom_0"]);
        Assert.True(transformed3.IsTiny);
        Assert.Contains("Relic", transformed3.ExtraTags);
    }

    [Fact]
    public void RandomTransform_ShouldSkipLegendaryTargets_EvenAcrossManyRandomSeeds()
    {
        var repo = Repo.Value;
        var sizeinatorTpl = repo.GetCard("7b30db1a-80c8-4889-ae2f-12b0ad047796")!; // Sizeinator (Legendary)
        var ductTapeTpl = repo.GetCard("0966f887-5aaf-44a5-90fa-ecb194270513")!;

        for (int seed = 1; seed <= 25; seed++)
        {
            var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 10, 10, 10);
            var legendary = new CardInstance(sizeinatorTpl);
            var bronze = new CardInstance(MakeItemTemplate($"bronze_{seed}", size: "Small", startingTier: "Bronze"));
            p1.Hand.TryPlaceWithDisplaceOrSwap(legendary, 0);
            p1.Hand.TryPlaceWithDisplaceOrSwap(bronze, 3);

            var randTransform = new RuleNode
            {
                Type = "TActionCardTransform",
                Target = new RuleNode { Type = "TTargetCardRandom", TargetSection = "SelfHand" },
                SpawnContext = MakeSpawnByIdContext(ductTapeTpl.Id)
            };
            var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, null, new Random(seed));
            Assert.True(AbilityExecutor.ExecuteAction(randTransform, bronze, ctx));
            Assert.Contains(legendary, p1.Hand.Cards);
            Assert.False(legendary.IsDestroyed);
            Assert.Contains(p1.Hand.Cards, c => c.Template.Id == ductTapeTpl.Id);
        }

        // 若场上仅剩 Legendary 目标，随机 Transform 应安全跳过不毁坏或变形该卡
        var soloPlayer = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 10, 10, 10);
        var onlyLegendary = new CardInstance(sizeinatorTpl);
        soloPlayer.Hand.TryPlaceWithDisplaceOrSwap(onlyLegendary, 0);
        var soloAction = new RuleNode
        {
            Type = "TActionCardTransform",
            Target = new RuleNode { Type = "TTargetCardRandom", TargetSection = "SelfHand" },
            SpawnContext = MakeSpawnByIdContext(ductTapeTpl.Id)
        };
        var soloCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, soloPlayer, null, new Random(99));
        AbilityExecutor.ExecuteAction(soloAction, onlyLegendary, soloCtx);
        Assert.Single(soloPlayer.Hand.Cards);
        Assert.Same(onlyLegendary, soloPlayer.Hand.Cards[0]);
    }

    // =========================================================================
    // Task 3: [4.8 & 6.3] 动态效果标签与附魔标签衍生
    // =========================================================================

    [Fact]
    public void DynamicTags_ShouldDeriveAll8EffectTagsFromAbilitiesAndEnchantments()
    {
        var repo = Repo.Value;

        // 1. 空白卡挂载动态复合能力，覆盖全部 8 种动态效果标签
        var allEffectsTpl = MakeItemTemplate("v7_blank_dynamic");
        var dynamicCard = new CardInstance(allEffectsTpl);
        Assert.Empty(dynamicCard.GetAllTags());

        dynamicCard.ActiveAbilities.Add(new AbilityDefinition
        {
            Id = "dyn_all",
            Action = new RuleNode
            {
                Type = "TActionAnd",
                Actions =
                [
                    new RuleNode { Type = "TActionPlayerBurnApply" },
                    new RuleNode { Type = "TActionPlayerPoisonApply" },
                    new RuleNode { Type = "TActionPlayerShieldApply" },
                    new RuleNode { Type = "TActionPlayerHeal" },
                    new RuleNode { Type = "TActionPlayerRegenApply" },
                    new RuleNode { Type = "TActionCardFreeze" },
                    new RuleNode { Type = "TActionCardSlow" },
                    new RuleNode { Type = "TActionCardHaste" }
                ]
            }
        });

        string[] expected8 = ["Burn", "Poison", "Shield", "Heal", "Regen", "Freeze", "Slow", "Haste"];
        var allTags = dynamicCard.GetAllTags().ToList();
        var allHidden = dynamicCard.GetAllHiddenTags().ToList();
        foreach (var tag in expected8)
        {
            Assert.True(dynamicCard.HasTag(tag), $"HasTag 应包含动态衍生标签 {tag}");
            Assert.True(dynamicCard.HasHiddenTag(tag), $"HasHiddenTag 应包含动态衍生标签 {tag}");
            Assert.Contains(tag, allTags, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(tag, allHidden, StringComparer.OrdinalIgnoreCase);
        }

        // 2. 用无原始效果的模板挂载真实 Fang (7cbc8056) 的全套附魔定义，逐一验证 8 种直接效果附魔；
        //    同时单独校验 Duct Tape (0966f887) 的 Shielded/Heavy 仅赋予 ShieldReference/SlowReference，以及 Obsidian/自身 Slow 标签
        var fangTpl = repo.GetCard("7cbc8056-482b-4ccf-887a-ddb2702c856c")!;
        var cleanWithFangEnchTpl = MakeItemTemplate("v7_clean_ench_base", enchantments: fangTpl.Enchantments);
        (string Enchant, string ExpectedTag)[] enchantCases =
        [
            ("Fiery", "Burn"),
            ("Toxic", "Poison"),
            ("Shielded", "Shield"),
            ("Restorative", "Heal"),
            ("Mossy", "Regen"),
            ("Icy", "Freeze"),
            ("Heavy", "Slow"),
            ("Turbo", "Haste")
        ];

        foreach (var (ench, expTag) in enchantCases)
        {
            var inst = new CardInstance(cleanWithFangEnchTpl);
            Assert.False(inst.HasTag(expTag), $"未附魔前干净物品不应携带 {expTag}");

            inst.ApplyEnchantment(ench);
            Assert.True(inst.HasTag(expTag), $"附魔 {ench} 后 HasTag 应包含 {expTag}");
            Assert.True(inst.HasHiddenTag(expTag), $"附魔 {ench} 后 HasHiddenTag 应包含 {expTag}");
            Assert.Contains(expTag, inst.GetAllTags(), StringComparer.OrdinalIgnoreCase);

            inst.RemoveEnchantment();
            Assert.False(inst.HasTag(expTag), $"移除附魔 {ench} 后应停止包含 {expTag}");
        }

        // Duct Tape 自带 Shield/Slow，其 Shielded/Heavy 在 DB 中仅携带 ShieldReference/SlowReference 隐藏标签（不误衍生 Shield/Slow）
        var ductTpl = repo.GetCard("0966f887-5aaf-44a5-90fa-ecb194270513")!;
        var cleanWithDuctEnchTpl = MakeItemTemplate("v7_clean_duct_ench_base", enchantments: ductTpl.Enchantments);
        var ductShieldedOnly = new CardInstance(cleanWithDuctEnchTpl, enchantment: "Shielded");
        Assert.True(ductShieldedOnly.HasHiddenTag("ShieldReference"));
        Assert.False(ductShieldedOnly.HasHiddenTag("Shield"));
        Assert.False(ductShieldedOnly.HasTag("Shield"));

        var ductHeavyOnly = new CardInstance(cleanWithDuctEnchTpl, enchantment: "Heavy");
        Assert.True(ductHeavyOnly.HasHiddenTag("SlowReference"));
        Assert.False(ductHeavyOnly.HasHiddenTag("Slow"));
        Assert.False(ductHeavyOnly.HasTag("Slow"));

        // 真实 DB 卡 Duct Tape 自带 TActionCardSlow，动态衍生也应直接检出 Slow，且附魔 Obsidian 后变成 Weapon
        var realDuct = new CardInstance(ductTpl);
        Assert.True(realDuct.HasTag("Slow"));
        Assert.Contains("Slow", realDuct.GetAllTags(), StringComparer.OrdinalIgnoreCase);

        var obsidianDuct = new CardInstance(ductTpl, enchantment: "Obsidian");
        Assert.True(obsidianDuct.HasTag("Weapon"));
        Assert.True(obsidianDuct.HasHiddenTag("Damage"));
        Assert.Contains("Weapon", obsidianDuct.GetAllTags(), StringComparer.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Task 4: [4.1] BoardGrid.TryPlaceWithDisplaceOrSwap 挤位与换位算法
    // =========================================================================

    [Fact]
    public void BoardGrid_TryPlaceWithDisplaceOrSwap_ShouldDisplaceSwapAndRespectLockedSlots()
    {
        // 场景 A：同棋盘直接换位（满格 4 格窗口 2..5，左右边缘 0..1 和 6..9 锁定）
        int middle4Mask = 0b0000_1111_00; // slots 2, 3, 4, 5 解锁
        var gridA = new BoardGrid(unlockedMask: middle4Mask);
        var s0 = new CardInstance(MakeItemTemplate("s0", "Small"));
        var m1 = new CardInstance(MakeItemTemplate("m1", "Medium"));
        var s3 = new CardInstance(MakeItemTemplate("s3", "Small"));
        Assert.True(gridA.TryPlaceWithDisplaceOrSwap(s0, 2));
        Assert.True(gridA.TryPlaceWithDisplaceOrSwap(m1, 3));
        Assert.True(gridA.TryPlaceWithDisplaceOrSwap(s3, 5));

        // 把 s0(slot 2) 拖到 s3(slot 5) 上：应干净互换位置，m1(3..4) 完全不动
        Assert.True(gridA.TryPlaceWithDisplaceOrSwap(s0, 5));
        Assert.Equal(5, s0.StartSlot);
        Assert.Equal(2, s3.StartSlot);
        Assert.Equal(3, m1.StartSlot);
        AssertGridInvariants(gridA, expectedCount: 3);

        // 把 m1(3..4) 拖到 slot 2（压住 s3(2) 并占 2..3）：通过挤位/换位把 s3 干净放到 slot 4
        Assert.True(gridA.TryPlaceWithDisplaceOrSwap(m1, 2));
        Assert.Equal(2, m1.StartSlot);
        Assert.Equal(4, s3.StartSlot);
        Assert.Equal(5, s0.StartSlot);
        AssertGridInvariants(gridA, expectedCount: 3);

        // 场景 B：局部最小位移顺次挤开（空闲充足时不整体乱跳）
        var gridB = new BoardGrid();
        var b0 = new CardInstance(MakeItemTemplate("b0", "Small"));
        var b1 = new CardInstance(MakeItemTemplate("b1", "Small"));
        var b2 = new CardInstance(MakeItemTemplate("b2", "Small"));
        var b9 = new CardInstance(MakeItemTemplate("b9", "Small"));
        Assert.True(gridB.TryPlaceWithDisplaceOrSwap(b0, 0));
        Assert.True(gridB.TryPlaceWithDisplaceOrSwap(b1, 1));
        Assert.True(gridB.TryPlaceWithDisplaceOrSwap(b2, 2));
        Assert.True(gridB.TryPlaceWithDisplaceOrSwap(b9, 9));

        var dropMed = new CardInstance(MakeItemTemplate("dropMed", "Medium"));
        Assert.True(gridB.TryPlaceWithDisplaceOrSwap(dropMed, 1));
        Assert.Equal(1, dropMed.StartSlot);
        Assert.Equal(0, b0.StartSlot);
        Assert.Equal(9, b9.StartSlot); // 无关卡保持原位
        AssertGridInvariants(gridB, expectedCount: 5);

        // 场景 C：尝试放到锁定槽位或满盘无解时原子回滚
        var snap = gridA.Cards.ToDictionary(c => c, c => c.StartSlot);
        var extra = new CardInstance(MakeItemTemplate("extra", "Small"));
        Assert.False(gridA.TryPlaceWithDisplaceOrSwap(extra, 1)); // 锁定槽
        Assert.False(gridA.TryPlaceWithDisplaceOrSwap(extra, 2)); // 已满无解
        Assert.Equal(3, gridA.Cards.Count);
        foreach (var (c, slot) in snap)
            Assert.Equal(slot, c.StartSlot);

        // 场景 D：不同宽度满盘需回溯重排换位（A(1)@0, B(2)@1..2, C(1)@3, D(2)@4..5，拖 A 到 1）
        var gridD = new BoardGrid(unlockedMask: 0b0000_1111_11); // slots 0..5 解锁
        var dA = new CardInstance(MakeItemTemplate("dA", "Small"));
        var dB = new CardInstance(MakeItemTemplate("dB", "Medium"));
        var dC = new CardInstance(MakeItemTemplate("dC", "Small"));
        var dD = new CardInstance(MakeItemTemplate("dD", "Medium"));
        Assert.True(gridD.TryPlaceWithDisplaceOrSwap(dA, 0));
        Assert.True(gridD.TryPlaceWithDisplaceOrSwap(dB, 1));
        Assert.True(gridD.TryPlaceWithDisplaceOrSwap(dC, 3));
        Assert.True(gridD.TryPlaceWithDisplaceOrSwap(dD, 4));

        Assert.True(gridD.TryPlaceWithDisplaceOrSwap(dA, 1));
        Assert.Equal(1, dA.StartSlot);
        AssertGridInvariants(gridD, expectedCount: 4);
    }

    private static void AssertGridInvariants(BoardGrid grid, int expectedCount)
    {
        Assert.Equal(expectedCount, grid.Cards.Count);
        var seenSlots = new HashSet<int>();
        foreach (var c in grid.Cards)
        {
            Assert.InRange(c.StartSlot, 0, BoardGrid.Capacity - c.SlotWidth);
            for (int s = c.StartSlot; s < c.StartSlot + c.SlotWidth; s++)
            {
                Assert.True(grid.IsSlotUnlocked(s), $"卡牌 {c.Template.InternalName} 占用了锁定槽位 {s}");
                Assert.True(seenSlots.Add(s), $"槽位 {s} 发生重叠");
            }
        }
    }

    // =========================================================================
    // Task 5: [4.9] Quest 计数（buy-fusion、免费发牌不计买、技能/光环赋 Flying 计飞行任务）
    // =========================================================================

    [Fact]
    public void PurchaseQuests_ShouldCountBuyFusion_AndIgnoreFreeDealtCards()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Pygmalien, 500, 30, 10, 10);

        // Regal Blade [Quest_1]: TTriggerOnCardPurchased Weapon (target = 8)
        var regalBlade = new CardInstance(repo.GetCard("6ada601b-88a5-4eaf-8813-805bd51c7f9b")!);
        // Sands of Time [Quest_1]: TTriggerOnCardPurchased Slow Item (target = 5)
        var sandsOfTime = new CardInstance(repo.GetCard("0c1d2e3f-4a5b-6c7d-8e9f-0a1b2c3d4e5f")!);
        player.Hand.TryPlaceWithDisplaceOrSwap(regalBlade, 0);
        player.Hand.TryPlaceWithDisplaceOrSwap(sandsOfTime, 2);

        // 1. 免费发牌（isFree = true、OnEncounterCardsDealt、TActionGameSpawnCards）一律不得计为购买
        var freeWeapon = new CardInstance(MakeItemTemplate("v7_free_weapon", tags: ["Weapon"]));
        OutOfCombatController.OnItemPurchased(player, freeWeapon, repo, new Random(1), isFree: true);
        Assert.Equal(0, regalBlade.QuestProgress.GetValueOrDefault("Quest_1", 0));

        var dealtList = new List<CardInstance> { freeWeapon };
        OutOfCombatController.OnEncounterCardsDealt(player, null, dealtList, repo, new Random(2));
        Assert.Equal(0, regalBlade.QuestProgress.GetValueOrDefault("Quest_1", 0));

        // 2. buy-fusion 融合购买应正常计为购买 1 次
        var fusedWeapon = new CardInstance(MakeItemTemplate("v7_fused_weapon", tags: ["Weapon"]));
        player.Hand.TryPlaceWithDisplaceOrSwap(fusedWeapon, 4);
        OutOfCombatController.OnItemPurchased(player, fusedWeapon, repo, new Random(3), isFree: false, isFusion: true);
        OutOfCombatController.OnCardUpgraded(player, fusedWeapon, repo, new Random(3), isFusion: true);
        Assert.Equal(1, regalBlade.QuestProgress.GetValueOrDefault("Quest_1", 0));

        // 3. 结合 Task 3：购买 Heavy 附魔（原本非 Slow 的武器）同样应同时推进 Regal Blade(Weapon) 与 Sands of Time(Slow)
        var heavyWeapon = new CardInstance(repo.GetCard("0966f887-5aaf-44a5-90fa-ecb194270513")!); // Duct Tape
        // 使用支持 Heavy 且为 Weapon 的卡或给 Weapon 挂 Heavy 附魔
        var customSlowWeapon = new CardInstance(
            MakeItemTemplate("v7_heavy_weapon", tags: ["Weapon"], enchantments: new() { ["Heavy"] = new() { HiddenTags = ["Slow"] } }),
            enchantment: "Heavy");
        OutOfCombatController.OnItemPurchased(player, customSlowWeapon, repo, new Random(4));
        Assert.Equal(2, regalBlade.QuestProgress.GetValueOrDefault("Quest_1", 0));
        Assert.Equal(1, sandsOfTime.QuestProgress.GetValueOrDefault("Quest_1", 0));
    }

    [Fact]
    public void FlyingQuests_ShouldAdvanceWhenSkillsOrAurasGrantFlying_WithoutDoubleCounting()
    {
        var repo = Repo.Value;

        // 1. 技能在战斗中通过 TActionCardFlyingStart 赋予友方武器飞行 -> Orbital Polisher(TTriggerOnCardStartsFlying)、In-Flight Meal(StartedFlying)、Radar Dome(StartedFlying Weapon) 全部计数
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 10, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Pygmalien, 500, 10, 10, 10);

        var orbital = new CardInstance(repo.GetCard("d619c665-1d58-46e8-828f-6e7e36a86a7d")!);     // Orbital Polisher
        var inflight = new CardInstance(repo.GetCard("e93df1d5-112d-41ef-9336-e784053d09be")!);    // In-Flight Meal
        var radar = new CardInstance(repo.GetCard("48080618-333b-4628-af57-de57fbd2f7d4")!);       // Radar Dome
        var weapon = new CardInstance(MakeItemTemplate("v7_fly_weapon", tags: ["Weapon"], baseAttrs: new() { ["DamageAmount"] = 5, ["CooldownMax"] = 3000 }));
        var flySkill = new CardInstance(MakeItemTemplate(
            "v7_skill_grant_flying",
            cardType: "Skill",
            abilities: new Dictionary<string, AbilityDefinition>
            {
                ["sk_fly"] = new()
                {
                    Id = "sk_fly",
                    Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardFlyingStart",
                        Target = new RuleNode
                        {
                            Type = "TTargetCardSection",
                            TargetSection = "SelfHand",
                            RawConditionsElement = JsonTagCondition("Weapon")
                        }
                    }
                }
            }));

        p1.Hand.TryPlaceWithDisplaceOrSwap(orbital, 0);
        p1.Hand.TryPlaceWithDisplaceOrSwap(inflight, 1);
        p1.Hand.TryPlaceWithDisplaceOrSwap(radar, 3);
        p1.Hand.TryPlaceWithDisplaceOrSwap(weapon, 6);
        p1.Skills.Add(flySkill);
        p2.Hand.TryPlaceWithDisplaceOrSwap(new CardInstance(MakeItemTemplate("p2_dummy", tags: ["Weapon"], baseAttrs: new() { ["DamageAmount"] = 5, ["CooldownMax"] = 3000 })), 0);

        CombatSimulator.Simulate(repo, p1, p2, seed: 42, syncPermanentChanges: true);
        Assert.True(orbital.QuestProgress.GetValueOrDefault("Quest_1", 0) >= 1, "技能赋飞应推进 Orbital Polisher (TTriggerOnCardStartsFlying)");
        Assert.True(inflight.QuestProgress.GetValueOrDefault("Quest_1", 0) >= 1, "技能赋飞应推进 In-Flight Meal");
        Assert.True(radar.QuestProgress.GetValueOrDefault("Quest_1", 0) >= 1, "技能赋飞 Weapon 应推进 Radar Dome [Quest_1]");

        // 2. 被动光环（TAuraActionCardModifyAttribute "Flying" +1）赋予飞行 -> 推进所有飞行任务且反复重算不得重复计数
        var oocPlayer = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 10, 10, 10);
        var oocOrbital = new CardInstance(repo.GetCard("d619c665-1d58-46e8-828f-6e7e36a86a7d")!);
        var oocMeal = new CardInstance(repo.GetCard("e93df1d5-112d-41ef-9336-e784053d09be")!);
        var oocRadar = new CardInstance(repo.GetCard("48080618-333b-4628-af57-de57fbd2f7d4")!);
        var oocWeapon = new CardInstance(MakeItemTemplate("v7_ooc_fly_weapon", tags: ["Weapon"]));
        var auraFlySkill = new CardInstance(MakeItemTemplate(
            "v7_aura_fly_skill",
            cardType: "Skill",
            auras: new Dictionary<string, AuraDefinition>
            {
                ["af"] = new()
                {
                    Id = "af",
                    ActiveIn = "HandAndStash",
                    WorksIn = "Anywhere",
                    Action = new RuleNode
                    {
                        Type = "TAuraActionCardModifyAttribute",
                        AttributeType = "Flying",
                        Operation = "Add",
                        RawValueNode = JsonFixed(1),
                        Target = new RuleNode
                        {
                            Type = "TTargetCardSection",
                            TargetSection = "SelfHand",
                            RawConditionsElement = JsonTagCondition("Weapon")
                        }
                    }
                }
            }));

        oocPlayer.Hand.TryPlaceWithDisplaceOrSwap(oocOrbital, 0);
        oocPlayer.Hand.TryPlaceWithDisplaceOrSwap(oocMeal, 1);
        oocPlayer.Hand.TryPlaceWithDisplaceOrSwap(oocRadar, 3);
        oocPlayer.Hand.TryPlaceWithDisplaceOrSwap(oocWeapon, 6);
        oocPlayer.Skills.Add(auraFlySkill);

        // 第一次重算应检测到 oocWeapon 0→1 跃迁，各自 +1
        var b1 = OutOfCombatController.OnCardMoved(oocPlayer, repo);
        Assert.Equal(1, oocWeapon.GetAttribute("Flying"));
        Assert.Equal(1, oocOrbital.QuestProgress.GetValueOrDefault("Quest_1", 0));
        Assert.Equal(1, oocMeal.QuestProgress.GetValueOrDefault("Quest_1", 0));
        Assert.Equal(1, oocRadar.QuestProgress.GetValueOrDefault("Quest_1", 0));
        Assert.NotEmpty(b1.QuestLogs);

        // 光环持续存在时再次反复重算/战斗光环重算：不得重复叠加同一张已飞卡
        OutOfCombatController.OnCardMoved(oocPlayer, repo);
        AuraEngine.RecalculateAuras(oocPlayer, null, RuleScope.InCombat, oocPlayer.Day, oocPlayer.Hour, repo);
        Assert.Equal(1, oocOrbital.QuestProgress.GetValueOrDefault("Quest_1", 0));
        Assert.Equal(1, oocMeal.QuestProgress.GetValueOrDefault("Quest_1", 0));
        Assert.Equal(1, oocRadar.QuestProgress.GetValueOrDefault("Quest_1", 0));

        // 3. 技能在战斗中通过 TActionCardModifyAttribute("Flying", "Add", 1) 赋予目标新卡飞行
        var attrP1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 10, 10, 10);
        var attrP2 = new PlayerState("p2", "P2", HeroNames.Pygmalien, 500, 10, 10, 10);
        var attrOrbital = new CardInstance(repo.GetCard("d619c665-1d58-46e8-828f-6e7e36a86a7d")!);
        var attrMeal = new CardInstance(repo.GetCard("e93df1d5-112d-41ef-9336-e784053d09be")!);
        var attrRadar = new CardInstance(repo.GetCard("48080618-333b-4628-af57-de57fbd2f7d4")!);
        var attrWeapon = new CardInstance(MakeItemTemplate("v7_attr_fly_weapon", tags: ["Weapon"], baseAttrs: new() { ["DamageAmount"] = 5, ["CooldownMax"] = 3000 }));
        var attrFlySkill = new CardInstance(MakeItemTemplate(
            "v7_attr_fly_skill",
            cardType: "Skill",
            abilities: new Dictionary<string, AbilityDefinition>
            {
                ["sk_attr_fly"] = new()
                {
                    Id = "sk_attr_fly",
                    Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardModifyAttribute",
                        AttributeType = "Flying",
                        Operation = "Add",
                        RawValueNode = JsonFixed(1),
                        Target = new RuleNode
                        {
                            Type = "TTargetCardSection",
                            TargetSection = "SelfHand",
                            RawConditionsElement = JsonTagCondition("Weapon")
                        }
                    }
                }
            }));

        attrP1.Hand.TryPlaceWithDisplaceOrSwap(attrOrbital, 0);
        attrP1.Hand.TryPlaceWithDisplaceOrSwap(attrMeal, 1);
        attrP1.Hand.TryPlaceWithDisplaceOrSwap(attrRadar, 3);
        attrP1.Hand.TryPlaceWithDisplaceOrSwap(attrWeapon, 6);
        attrP1.Skills.Add(attrFlySkill);
        attrP2.Hand.TryPlaceWithDisplaceOrSwap(new CardInstance(MakeItemTemplate("p2_attr_dummy", tags: ["Weapon"], baseAttrs: new() { ["DamageAmount"] = 5, ["CooldownMax"] = 3000 })), 0);

        CombatSimulator.Simulate(repo, attrP1, attrP2, seed: 43, syncPermanentChanges: true);
        Assert.True(attrOrbital.QuestProgress.GetValueOrDefault("Quest_1", 0) >= 1);
        Assert.True(attrMeal.QuestProgress.GetValueOrDefault("Quest_1", 0) >= 1);
        Assert.True(attrRadar.QuestProgress.GetValueOrDefault("Quest_1", 0) >= 1);
    }
}
