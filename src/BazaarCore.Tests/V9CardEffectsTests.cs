using System.Globalization;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V9 Stage A 核心卡牌效果与生成引擎回归测试套件：
/// 覆盖 #34（出售触发目标排除 soldCard 与最左/最右按 StartSlot 排序）、
/// #12（火药角装弹与直接修改 Ammo 严格受 [0, AmmoMax] 钳制）、
/// #13（出售生成随机附魔物品与 TActionCardSpawn/TActionGameSpawnCards -> TActionCardEnchantRandom 链路）、
/// #4（多战利品生成与 Sequential 单组严格生成满 Limit 且不因批内重复被上层去重吞掉数量）。
/// </summary>
public sealed class V9CardEffectsTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());

    private static JsonElement JsonFixed(double val)
        => JsonDocument.Parse($"{{\"$type\":\"TFixedValue\",\"Value\":{val.ToString(CultureInfo.InvariantCulture)}}}").RootElement.Clone();

    private static JsonElement JsonNumber(double val)
        => JsonDocument.Parse(val.ToString(CultureInfo.InvariantCulture)).RootElement.Clone();

    private static CardTemplate MakeSimpleWeapon(string id, string size = "Small", double dmg = 20, double ammoMax = 0)
    {
        var attrs = new Dictionary<string, double>
        {
            ["DamageAmount"] = dmg,
            ["CooldownMax"] = 3000,
        };
        if (ammoMax > 0) attrs["AmmoMax"] = ammoMax;

        return new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = "Item",
            Size = size,
            StartingTier = "Bronze",
            Tags = ["Weapon"],
            HiddenTags = ammoMax > 0 ? ["Damage", "Ammo"] : ["Damage"],
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData { Attributes = attrs },
            },
        };
    }

    // =========================================================================
    // #34：TTriggerOnCardSold 严格排除正在出售的 soldCard 本身并按 StartSlot 排序
    // =========================================================================

    [Fact]
    public void Issue34_FieryEagleTalisman_ConsecutiveSells_MustBuffLeftmostCannonNotSelfOrFang()
    {
        var repo = Repo.Value;
        var cannonTpl = repo.GetCard("c74239b9-563c-4672-b664-9c9f22329f19")!;     // Cannon (Medium, Weapon+Burn, Bronze)
        var fangTpl = repo.GetCard("7cbc8056-482b-4ccf-887a-ddb2702c856c")!;       // Fang (Small, Weapon)
        var musketTpl = repo.GetCard("5e7abe19-0087-4bb1-a8bf-617706cc67c9")!;     // Musket (Medium, Weapon)
        var talismanTpl = repo.GetCard("0022c409-c839-41e8-8022-65a407457dfe")!;   // Eagle Talisman (Diamond Custom_0=20)

        // 模拟标准 6 格解锁手牌布局 [火炮@2(2格), 獠牙@4(1格), 火枪@5(2格), 炽焰·鹰之护符@7(1格)]
        var player = new PlayerState("p1", "Vanessa", HeroNames.Vanessa, 500, 30, 10, 4);
        player.Hand.UnlockedMask = 0;
        player.Hand.UnlockCountCentered(6); // bits 2..7

        var cannon = new CardInstance(cannonTpl, CardTier.Bronze);
        var fang = new CardInstance(fangTpl, CardTier.Bronze);
        var musket = new CardInstance(musketTpl, CardTier.Silver);

        Assert.True(player.Hand.TryPlaceOrShift(cannon, 2));
        Assert.True(player.Hand.TryPlaceOrShift(fang, 4));
        Assert.True(player.Hand.TryPlaceOrShift(musket, 5));

        AuraEngine.RecalculateAuras(player, null, RuleScope.OutOfCombat, player.Day, player.Hour, repo);
        double baseBurn = cannon.GetAttribute("BurnApplyAmount"); // Cannon 光环 0.1 * 50 = 5
        Assert.Equal(5, baseBurn);
        Assert.Equal(0, cannon.GetAttribute("CritChance"));

        // 连续卖出 3 张钻石阶炽焰·鹰之护符（DB 中 Fiery 仅赋予 BurnReference 隐藏标签，不误赋予 Burn 标签）
        for (int sellRound = 1; sellRound <= 3; sellRound++)
        {
            var fieryTalisman = new CardInstance(talismanTpl, CardTier.Diamond, "Fiery");
            Assert.False(fieryTalisman.HasTag("Burn"), "炽焰·鹰之护符不应误带 Burn 可见标签");
            Assert.False(fieryTalisman.HasHiddenTag("Burn"), "炽焰·鹰之护符不应误带 Burn 隐藏标签");
            Assert.True(fieryTalisman.HasHiddenTag("BurnReference"), "炽焰·鹰之护符应保留模板定义的 BurnReference 隐藏标签");
            Assert.True(player.Hand.TryPlaceOrShift(fieryTalisman, 7));

            var batch = OutOfCombatController.OnCardSoldOrConsumed(player, fieryTalisman, repo, new Random(100 + sellRound));

            Assert.DoesNotContain(fieryTalisman, player.Hand.Cards);
            Assert.Contains(fieryTalisman, batch.RemovedCards);

            // 每次都必须精准命中手牌最左的火炮（+20% CritChance 与 +1 BurnApplyAmount）
            Assert.Equal(20 * sellRound, cannon.GetAttribute("CritChance"));
            Assert.Equal(baseBurn + sellRound, cannon.GetAttribute("BurnApplyAmount"));

            // 獠牙、火枪与刚卖出的护符自身绝不应吃错任何暴击或灼烧
            Assert.Equal(0, fang.GetAttribute("CritChance"));
            Assert.Equal(0, fang.GetAttribute("BurnApplyAmount"));
            Assert.Equal(0, musket.GetAttribute("CritChance"));
            Assert.Equal(0, musket.GetAttribute("BurnApplyAmount"));
            Assert.Equal(0, fieryTalisman.GetAttribute("CritChance"));
            Assert.Equal(0, fieryTalisman.GetAttribute("BurnApplyAmount"));
        }

        // 手牌同时存在两张炽焰·鹰之护符（一张位于火炮更左侧 slot 0）时，卖出另一张不得误给左侧护符加灼烧或暴击
        player.Hand.UnlockedMask = 0x3FF;
        var leftFieryTalisman = new CardInstance(talismanTpl, CardTier.Diamond, "Fiery");
        var secondFieryTalisman = new CardInstance(talismanTpl, CardTier.Diamond, "Fiery");
        Assert.True(player.Hand.TryPlaceOrShift(leftFieryTalisman, 0));
        Assert.True(player.Hand.TryPlaceOrShift(secondFieryTalisman, 7));

        OutOfCombatController.OnCardSoldOrConsumed(player, secondFieryTalisman, repo, new Random(104));
        Assert.Equal(0, leftFieryTalisman.GetAttribute("CritChance"));
        Assert.Equal(0, leftFieryTalisman.GetAttribute("BurnApplyAmount"));
        Assert.Equal(80, cannon.GetAttribute("CritChance"));
        Assert.Equal(baseBurn + 4, cannon.GetAttribute("BurnApplyAmount"));
    }

    [Fact]
    public void Issue34_UnsortedHandGhostSlot_And_ExcludeSelfFalse_And_PositionalPreserved()
    {
        var repo = Repo.Value;
        var cannonTpl = repo.GetCard("c74239b9-563c-4672-b664-9c9f22329f19")!;
        var fangTpl = repo.GetCard("7cbc8056-482b-4ccf-887a-ddb2702c856c")!;
        var runeAxeTpl = repo.GetCard("cd651487-174f-49dd-9022-c27e1fcd2f7c")!; // ExcludeSelf=false, LeftMost Weapon +100 Lifesteal

        // 1. 构造列表倒序 + 存在 StartSlot=-1 幽灵未落位卡 + ExcludeSelf=false 的 Rune Axe
        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 400, 20, 10, 5);
        var fangRight = new CardInstance(fangTpl, CardTier.Bronze) { StartSlot = 4 };
        var cannonLeft = new CardInstance(cannonTpl, CardTier.Bronze) { StartSlot = 0 };
        var ghostWeapon = new CardInstance(MakeSimpleWeapon("ghost_wpn", "Small", 99)) { StartSlot = -1 };
        var runeAxe = new CardInstance(runeAxeTpl, CardTier.Bronze) { StartSlot = 6 };

        // 故意先插入 StartSlot=-1 与 StartSlot=4，再插入 StartSlot=0 与 RuneAxe
        player.Hand.Cards.Add(ghostWeapon);
        player.Hand.Cards.Add(fangRight);
        player.Hand.Cards.Add(cannonLeft);
        player.Hand.Cards.Add(runeAxe);

        OutOfCombatController.OnCardSoldOrConsumed(player, runeAxe, repo, new Random(7));

        // Rune Axe 虽然 JSON 配置 ExcludeSelf=false 且存在鬼牌(-1)和先插的獠牙(4)，仍须严格排除自身与 -1 并命中 StartSlot=0 的火炮
        Assert.Equal(100, cannonLeft.GetAttribute("Lifesteal"));
        Assert.Equal(0, fangRight.GetAttribute("Lifesteal"));
        Assert.Equal(0, ghostWeapon.GetAttribute("Lifesteal"));
        Assert.Equal(100, runeAxe.GetAttribute("Lifesteal")); // 基础值 100，未额外叠加自身 +100
        Assert.DoesNotContain(runeAxe, player.Hand.Cards);

        // 2. 验证 TTriggerOnCardSold 期间仍保留被卖卡原始站位供 Positional / Neighbors 邻接查找
        var leftNeighbor = new CardInstance(MakeSimpleWeapon("adj_left", "Small", 10));
        var rightNeighbor = new CardInstance(MakeSimpleWeapon("adj_right", "Small", 10));
        var adjSellTpl = new CardTemplate
        {
            Id = "adj_sell_tester",
            InternalName = "Adjacent Sell Tester",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData { AbilityIds = ["0"] },
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSold" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardModifyAttribute",
                        AttributeType = "DamageAmount",
                        Operation = "Add",
                        RawValueNode = JsonFixed(15),
                        Target = new RuleNode { Type = "TTargetCardPositional", Origin = "Self", TargetMode = "Neighbor" },
                    },
                },
            },
        };
        var adjSeller = new CardInstance(adjSellTpl);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 400, 20, 10, 10);
        Assert.True(p2.Hand.TryPlaceOrShift(leftNeighbor, 1));
        Assert.True(p2.Hand.TryPlaceOrShift(adjSeller, 2));
        Assert.True(p2.Hand.TryPlaceOrShift(rightNeighbor, 3));

        OutOfCombatController.OnCardSoldOrConsumed(p2, adjSeller, repo, new Random(8));
        Assert.Equal(25, leftNeighbor.GetAttribute("DamageAmount"));
        Assert.Equal(25, rightNeighbor.GetAttribute("DamageAmount"));
        Assert.DoesNotContain(adjSeller, p2.Hand.Cards);
    }

    // =========================================================================
    // #12：火药角与所有 Ammo/Reload 修改路径严格钳制在 [0, AmmoMax]
    // =========================================================================

    [Fact]
    public void Issue12_PowderHorn_And_DirectAmmoReloadOrModify_NeverExceedAmmoMax()
    {
        var repo = Repo.Value;
        var hornTpl = repo.GetCard("03e4c71d-5317-4c35-a0fb-5348356edc03")!;   // Powder Horn (Diamond ReloadAmount=4, CD=3000ms)
        var cannonTpl = repo.GetCard("c74239b9-563c-4672-b664-9c9f22329f19")!; // Cannon (Bronze AmmoMax=2, CD=4000ms)
        var musketTpl = repo.GetCard("5e7abe19-0087-4bb1-a8bf-617706cc67c9")!; // Musket (Silver AmmoMax=1)

        // 1. 战斗模拟：钻石阶 Powder Horn（3秒CD，每次+4发）放在 Cannon（4秒CD，AmmoMax=2）左侧
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 2000, 0, 10, 10);
        var p2 = new PlayerState("p2", "Dummy", HeroNames.Pygmalien, 2000, 0, 10, 10);

        var horn = new CardInstance(hornTpl, CardTier.Diamond);
        var cannon = new CardInstance(cannonTpl, CardTier.Bronze);
        Assert.True(p1.Hand.TryPlaceOrShift(horn, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(cannon, 1));

        // 给对手放一块慢速武器让战斗持续多轮触发
        var enemyWpn = new CardInstance(MakeSimpleWeapon("dummy_wpn", "Small", 5));
        Assert.True(p2.Hand.TryPlaceOrShift(enemyWpn, 0));

        var combatRes = CombatSimulator.Simulate(repo, p1, p2, seed: 42, recordTimeline: true, recordEvents: true);
        Assert.True(combatRes.Events.Any(e => e.SourceCardInstanceId == horn.InstanceId && e.Type == CombatEventType.CardFired),
            "战斗中 Powder Horn 必须至少开火一次");

        foreach (var frame in combatRes.Timeline)
        {
            foreach (var cf in frame.Cards.Where(c => c.InstanceId == cannon.InstanceId))
            {
                Assert.InRange(cf.Ammo, 0, cf.AmmoMax);
                Assert.Equal(2, cf.AmmoMax);
            }
        }
        foreach (var reloadEv in combatRes.Events.Where(e => e.Type == CombatEventType.CardReloaded && e.TargetCardInstanceId == cannon.InstanceId))
        {
            Assert.True(reloadEv.Value <= 2, $"单次装填增量不得超过 AmmoMax(2)，实际 {reloadEv.Value}");
        }

        // 2. 直接执行 AbilityExecutor 的 TActionCardReload 与 TActionCardModifyAttribute（Add / Set / Subtract / AmmoMax）
        var pDirect = new PlayerState("pd", "PD", HeroNames.Vanessa, 500, 0, 10, 5);
        var musket = new CardInstance(musketTpl, CardTier.Silver); // AmmoMax=1
        musket.ResetForCombat(null);
        Assert.Equal(1, musket.CurrentAmmo);
        pDirect.Hand.TryPlaceOrShift(musket, 0);

        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, pDirect, null, new Random(1)) { SelfCard = musket };

        // 对满弹 Musket 执行 +4 Reload，仍应死守 1
        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionCardReload",
            RawValueNode = JsonFixed(4),
            Target = new RuleNode { Type = "TTargetCardSelf" },
        }, musket, ctx);
        Assert.Equal(1, musket.CurrentAmmo);

        // 直接 ModifyAttribute("Ammo", "Add", 10) 与 "Set" 99，仍应死守 AmmoMax=1
        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionCardModifyAttribute",
            AttributeType = "Ammo",
            Operation = "Add",
            RawValueNode = JsonFixed(10),
            Target = new RuleNode { Type = "TTargetCardSelf" },
        }, musket, ctx);
        Assert.Equal(1, musket.CurrentAmmo);

        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionCardModifyAttribute",
            AttributeType = "Ammo",
            Operation = "Set",
            RawValueNode = JsonFixed(99),
            Target = new RuleNode { Type = "TTargetCardSelf" },
        }, musket, ctx);
        Assert.Equal(1, musket.CurrentAmmo);

        // 扣减超过当前弹药应钳制到 0
        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionCardModifyAttribute",
            AttributeType = "Ammo",
            Operation = "Subtract",
            RawValueNode = JsonFixed(5),
            Target = new RuleNode { Type = "TTargetCardSelf" },
        }, musket, ctx);
        Assert.Equal(0, musket.CurrentAmmo);

        // 只有提升 AmmoMax 后才能装填超过原上限
        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionCardModifyAttribute",
            AttributeType = "AmmoMax",
            Operation = "Add",
            RawValueNode = JsonFixed(2),
            Target = new RuleNode { Type = "TTargetCardSelf" },
        }, musket, ctx);
        Assert.Equal(3, (int)Math.Round(musket.GetAttribute("AmmoMax")));

        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionCardReload",
            RawValueNode = JsonFixed(10),
            Target = new RuleNode { Type = "TTargetCardSelf" },
        }, musket, ctx);
        Assert.Equal(3, musket.CurrentAmmo);

        // 若 AmmoMax 降低，当前 Ammo 必须同步下钳至新上限
        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionCardModifyAttribute",
            AttributeType = "AmmoMax",
            Operation = "Subtract",
            RawValueNode = JsonFixed(2),
            Target = new RuleNode { Type = "TTargetCardSelf" },
        }, musket, ctx);
        Assert.Equal(1, musket.CurrentAmmo);
    }

    // =========================================================================
    // #13：出售触发生成带随机附魔物品（ConstraintEnchantmentEligible & 多动作链路）
    // =========================================================================

    [Fact]
    public void Issue13_OnSell_ChimericEgg_MysteriousGift_And_RaffleTicket_AlwaysSpawnEnchantedItems()
    {
        var repo = Repo.Value;
        var eggTpl = repo.GetCard("28024275-df05-422c-800c-9960d83c24fa")!;    // Chimeric Egg
        var giftTpl = repo.GetCard("96b9cc27-b148-4d71-ab5d-21a9c3b64998")!;   // Mysterious Gift
        var ticketTpl = repo.GetCard("3174b74a-9e67-4db7-ba98-0d4935b2586d")!; // Raffle Ticket

        CardTemplate[] templates = [eggTpl, giftTpl, ticketTpl];
        foreach (var tpl in templates)
        {
            for (int i = 0; i < 6; i++)
            {
                var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 5);
                var card = new CardInstance(tpl, tpl.DefaultTier);
                Assert.True(player.Hand.TryPlaceOrShift(card, 3));

                var batch = OutOfCombatController.OnCardSoldOrConsumed(player, card, repo, new Random(200 + i));
                Assert.NotEmpty(batch.SpawnedCards);
                foreach (var sp in batch.SpawnedCards)
                {
                    Assert.False(string.IsNullOrEmpty(sp.Card.Enchantment),
                        $"{tpl.InternalName} (seed={200 + i}) 卖出生出的物品 {sp.Card.Template.InternalName} 不得是无附魔白板");
                    if (sp.Card.Template.Enchantments is { Count: > 0 } supported)
                    {
                        Assert.True(supported.ContainsKey(sp.Card.Enchantment!),
                            $"{sp.Card.Template.InternalName} 获得附魔 {sp.Card.Enchantment} 必须存在于其 Template.Enchantments");
                    }
                }
            }
        }
    }

    [Fact]
    public void Issue13_MultiAction_CardSpawnAndGameSpawn_To_EnchantRandom_WorksEndToEnd()
    {
        var repo = Repo.Value;
        var fangTpl = repo.GetCard("7cbc8056-482b-4ccf-887a-ddb2702c856c")!;

        // 验证 1：TActionAnd([TActionCardSpawn, TActionCardEnchantRandom(Target=null, empty pool)])
        // 验证 2：TActionAnd([TActionGameSpawnCards(TSpawnBehaviorEnchantRandom), ...])
        var sellerTpl = new CardTemplate
        {
            Id = "spawn_and_enchant_seller",
            InternalName = "Spawn & Enchant Seller",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData { AbilityIds = ["and_spawn_enchant"] },
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["and_spawn_enchant"] = new AbilityDefinition
                {
                    Id = "and_spawn_enchant",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSold" },
                    Action = new RuleNode
                    {
                        Type = "TActionAnd",
                        Actions =
                        [
                            new RuleNode
                            {
                                Type = "TActionCardSpawn",
                                Id = fangTpl.Id,
                            },
                            new RuleNode
                            {
                                Type = "TActionCardEnchantRandom",
                                // 故意留空 Target 与 Enchantments 权重池，验证从 LastSpawnedCardsMap 与 DefaultRandomEnchantments 正确串联
                            },
                        ],
                    },
                },
            },
        };

        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 10);
        var seller = new CardInstance(sellerTpl);
        Assert.True(player.Hand.TryPlaceOrShift(seller, 0));

        var batch = OutOfCombatController.OnCardSoldOrConsumed(player, seller, repo, new Random(77));
        Assert.Single(batch.SpawnedCards);
        var spawned = batch.SpawnedCards[0].Card;
        Assert.Equal(fangTpl.Id, spawned.Template.Id);
        Assert.False(string.IsNullOrEmpty(spawned.Enchantment), "TActionCardSpawn + TActionCardEnchantRandom 链路必须赋上附魔");
        Assert.True(spawned.Template.Enchantments!.ContainsKey(spawned.Enchantment!));
        Assert.Contains(spawned, batch.EnchantedCards);

        // 验证 3：SpawnContext 带 TSpawnBehaviorEnchantRandom
        var behaviorQuery = new RuleNode
        {
            Type = "TSpawnContextQuery",
            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(1) },
            Behaviors = [new RuleNode { Type = "TSpawnBehaviorEnchantRandom" }],
            Groups =
            [
                new RuleNode
                {
                    Type = "TSpawnGroup",
                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [fangTpl.Id] }],
                },
            ],
        };
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(88));
        var directSpawned = SpawnQueryEngine.SpawnCards(behaviorQuery, ctx);
        Assert.Single(directSpawned);
        Assert.False(string.IsNullOrEmpty(directSpawned[0].Enchantment));
        Assert.True(directSpawned[0].Template.Enchantments!.ContainsKey(directSpawned[0].Enchantment!));
    }

    // =========================================================================
    // #4：多战利品生成严格产满 Limit，不被重复或单组默认上限吞数量
    // =========================================================================

    [Fact]
    public void Issue04_MultiLootSpawn_GrabTheLoot_MysteryBundle_CandyStash_Safe_AlwaysYieldFullLimit()
    {
        var repo = Repo.Value;
        string[] encounterIds =
        [
            "e73e7956-1875-4f40-b5f2-d3c4ed975751", // [Jungle Ruins] Grab the Loot (一把捞走, Limit=3, Random)
            "ed15d9f2-b77e-4038-a540-84f381e0cb08", // Haddy - Mystery Bundle (Sequential 单组 Limit=null, Limit=3)
            "cbcb84f9-631a-4d85-bed3-cfd2f86fb8d6", // Candy Stash (Diamond, Sequential 单组 Limit=null, Limit=3)
            "e6e09e6a-ebe0-46f8-bfbb-716b74f78bc6", // Candy Stash (Bronze, Sequential 单组 Limit=null, Limit=3)
        ];

        foreach (var encId in encounterIds)
        {
            var cardTpl = repo.GetCard(encId)!;
            var ab = cardTpl.Abilities!.Values.First(a => a.Action?.SpawnContext != null);
            var spawnCtxNode = ab.Action!.SpawnContext!;

            for (int seed = 1; seed <= 25; seed++)
            {
                var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 5);
                var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(seed));
                var spawned = SpawnQueryEngine.SpawnCards(spawnCtxNode, ctx);

                Assert.Equal(3, spawned.Count);

                // Grab the Loot（一把捞走）和 Mystery Bundle 的候选池足够大，不得出现批内同 Template.Id 碰撞导致上层去重丢件
                if (encId is "e73e7956-1875-4f40-b5f2-d3c4ed975751" or "ed15d9f2-b77e-4038-a540-84f381e0cb08")
                {
                    Assert.Equal(3, spawned.Select(c => c.Template.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                }
            }
        }

        // 1张卡单池但允许重复的 Safe (15a2cf7d-4e08-4358-8bd1-730919719023 -> 3x Spare Change) 依然应出满 3 件
        var safeTpl = repo.GetCard("15a2cf7d-4e08-4358-8bd1-730919719023")!;
        var safePlayer = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 10, 5);
        var safeCard = new CardInstance(safeTpl, safeTpl.DefaultTier);
        safePlayer.Hand.TryPlaceOrShift(safeCard, 0);
        var safeBatch = OutOfCombatController.OnCardSoldOrConsumed(safePlayer, safeCard, repo, new Random(9));
        Assert.Equal(3, safeBatch.SpawnedCards.Count);
    }
}
