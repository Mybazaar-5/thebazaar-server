using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.TempoNet.Enums;
using BazaarServer.Data;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V10 Core 引擎专项修复回归测试套件：
/// 1. #3 净化普通商店与常规物品池（排除未显式请求的 Common 中立野怪物品与 Loot 战利品，同时保留 Curio 等显式例外）；
/// 2. #6 &amp; #20 TargetResolver 局外玩家绝对目标解析、技能池过滤/去重/逐阶向上回退，以及 Skill Voucher / Learning Crystal / Book of Secrets 真实触发；
/// 3. #17 Shuriken 等弹药卡在非战斗升阶/永久改 AmmoMax/脱战复位后的满弹同步与 Multicast 光环联动。
/// </summary>
public sealed class V10CoreSpawnAndSkillsTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);

    private const string JayJayId = "85db60ee-3619-4e39-bfb2-2f4ac56f172b";
    private const string KinaId = "5b7c5fc4-c942-44fe-9ca8-726dc36a2ad6";
    private const string MittelId = "912c8b09-3b76-4aa1-8e21-245e0dfb5046";
    private const string AilaId = "dc12f4bd-6c33-41f0-b7ad-d62a0be09a47";
    private const string PolId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string CurioId = "c6cffe25-8e94-4a7b-a821-3e3c7093333b";
    private const string BronzeLootLevelUpId = "146907bc-135e-4d54-bf13-f530605ee673";
    private const string WorkshopSilverId = "011b22fa-4a25-48a6-93e0-7602ceffcd13";

    private const string BookOfSecretsId = "a34b3465-d460-4f53-9223-8a3f4b77b012";
    private const string SkillVoucherId = "7607b1b9-4982-422d-b84a-edd9334c92c3";
    private const string LearningCrystalId = "55a20bf3-e81a-4f76-ade4-75d56b41f29c";
    private const string ShurikenId = "3af3c836-7224-47bc-bc27-dade6f9d210c";
    private const string GunpowderId = "3e563d2a-87e7-4e9a-bfa2-3268b27b7447";

    private static string ResolveDataPath(string fileName)
        => TestInfrastructure.TestDataLocator.ResolveDataFile(fileName);

    private static HashSet<string> BuildValidAssetSet(CardPool pool, GameDataRepository repo)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tpl in repo.CardsById.Values)
        {
            if (pool.IsAssetValid(tpl.Id))
                set.Add(tpl.Id);
        }
        return set;
    }

    private static bool IsPureCommonTemplate(CardTemplate tpl)
        => tpl.Heroes.Count == 1 && string.Equals(tpl.Heroes[0], HeroNames.Common, StringComparison.OrdinalIgnoreCase);

    // =========================================================================
    // #3：普通商店与常规物品池净化（0 Loot / 0 中立 Common；Curio 与显式 Loot/房间例外放行）
    // =========================================================================

    [Fact]
    public void Issue03_RegularHeroMerchants_50Refreshes_YieldZeroLootAndZeroCommonOnlyItems_WhileCurioStillYieldsCommon()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var validAssets = BuildValidAssetSet(pool, repo);

        string[] regularMerchantIds = [JayJayId, KinaId, MittelId, AilaId, PolId];
        string[] testHeroes = [HeroNames.Vanessa, HeroNames.Dooley, HeroNames.Pygmalien, HeroNames.Mak, HeroNames.Hero8];

        foreach (string merchantId in regularMerchantIds)
        {
            var merchantTpl = repo.GetCard(merchantId)!;
            var spawnCtx = merchantTpl.SelectionContext!.SpawnContext!;

            int totalSpawned = 0;
            for (int i = 0; i < 50; i++)
            {
                string hero = testHeroes[i % testHeroes.Length];
                // Pol 仅面向 Dooley/Jules/Karnok/Mak/Stelle/Vanessa，且 Dooley/Mak/Hero8 无青铜大物品（Day 2+ 开放白银大物品池）
                if (merchantId == PolId && hero == HeroNames.Hero8)
                    hero = HeroNames.Vanessa;

                int day = merchantId == PolId ? (2 + (i % 7)) : (1 + (i % 8));
                var player = new PlayerState("p1", hero, hero, 500, 20, 20, 10)
                {
                    Day = day,
                    Hour = 1,
                    Level = day,
                };
                var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(2000 + i * 17))
                {
                    CurrentDay = day,
                    CurrentHour = 1,
                    CurrentEncounterId = merchantTpl.Id,
                    SelfCard = new CardInstance(merchantTpl),
                };

                var spawned = SpawnQueryEngine.SpawnCards(spawnCtx, ctx, validAssetIds: validAssets);
                Assert.NotEmpty(spawned);
                foreach (var card in spawned)
                {
                    totalSpawned++;
                    Assert.False(card.Template.Tags.Contains("Loot", StringComparer.OrdinalIgnoreCase),
                        $"常规商店 {merchantTpl.InternalName} 不应刷新出 Loot 战利品: {card.Template.InternalName}");
                    Assert.False(IsPureCommonTemplate(card.Template),
                        $"常规商店 {merchantTpl.InternalName} 不应刷新出仅 Heroes=[\"Common\"] 的中立物品: {card.Template.InternalName}");
                    Assert.Contains(hero, card.Template.Heroes, StringComparer.OrdinalIgnoreCase);
                }
            }
            Assert.True(totalSpawned >= 100, $"商店 {merchantTpl.InternalName} 50 次刷新总产出过少: {totalSpawned}");
        }

        // 显式声明 ConstraintHero: ["Common"] 的古董商 Curio 仍必须正常刷出非 Loot 的 Common 道具
        var curioTpl = repo.GetCard(CurioId)!;
        var curioCtxNode = curioTpl.SelectionContext!.SpawnContext!;
        int curioCount = 0;
        for (int i = 0; i < 20; i++)
        {
            var player = new PlayerState("p_curio", "Vanessa", HeroNames.Vanessa, 500, 20, 20, 10) { Day = 3, Hour = 1 };
            var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(500 + i))
            {
                CurrentDay = 3,
                CurrentHour = 1,
                CurrentEncounterId = curioTpl.Id,
                SelfCard = new CardInstance(curioTpl),
            };
            var spawned = SpawnQueryEngine.SpawnCards(curioCtxNode, ctx, validAssetIds: validAssets);
            Assert.NotEmpty(spawned);
            foreach (var card in spawned)
            {
                curioCount++;
                Assert.Contains(HeroNames.Common, card.Template.Heroes, StringComparer.OrdinalIgnoreCase);
                Assert.False(card.Template.Tags.Contains("Loot", StringComparer.OrdinalIgnoreCase),
                    $"Curio 未显式声明 Loot 标签时也不应刷出 Loot: {card.Template.InternalName}");
            }
        }
        Assert.True(curioCount >= 20);

        // 显式声明 ConstraintTag: ["Loot"] 的事件（Bronze Loot）仍正常产出 Loot 战利品
        var bronzeLootTpl = repo.GetCard(BronzeLootLevelUpId)!;
        var bronzeLootCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, new PlayerState("p_loot", "Vanessa", HeroNames.Vanessa), null, new Random(77))
        {
            CurrentEncounterId = bronzeLootTpl.Id,
            SelfCard = new CardInstance(bronzeLootTpl),
        };
        var lootItems = SpawnQueryEngine.SpawnCards(bronzeLootTpl.SelectionContext!.SpawnContext!, bronzeLootCtx, validAssetIds: validAssets);
        Assert.Single(lootItems);
        Assert.Contains("Loot", lootItems[0].Template.Tags, StringComparer.OrdinalIgnoreCase);

        // 显式声明 TSpawnBehaviorIncludeCommonAndHero 的主题房间（Workshop）仍允许 Common 工具
        var workshopTpl = repo.GetCard(WorkshopSilverId)!;
        bool sawCommonOrHeroTool = false;
        for (int i = 0; i < 20; i++)
        {
            var wsCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, new PlayerState("p_ws", "Vanessa", HeroNames.Vanessa), null, new Random(800 + i))
            {
                CurrentEncounterId = workshopTpl.Id,
                SelfCard = new CardInstance(workshopTpl),
            };
            var wsItems = SpawnQueryEngine.SpawnCards(workshopTpl.SelectionContext!.SpawnContext!, wsCtx, validAssetIds: validAssets);
            Assert.Single(wsItems);
            Assert.Contains("Tool", wsItems[0].Template.Tags, StringComparer.OrdinalIgnoreCase);
            if (IsPureCommonTemplate(wsItems[0].Template))
                sawCommonOrHeroTool = true;
        }
        Assert.True(sawCommonOrHeroTool, "携带 TSpawnBehaviorIncludeCommonAndHero 的 Workshop 应允许产出 Common 工具");
    }

    // =========================================================================
    // #6 & #20：TargetResolver 局外绝对玩家解析、技能过滤与逐阶向上提升回退
    // =========================================================================

    [Fact]
    public void Issue06_20_TargetResolver_OutOfCombatAbsolutePlayer_AlwaysResolvesToSelfPlayerRegardlessOfId()
    {
        var repo = Repo.Value;
        var absPlayerTarget = new RuleNode { Type = "TTargetPlayerAbsolute", TargetMode = "Player" };
        var absLowerTarget = new RuleNode { Type = "TTargetPlayerAbsolute", TargetMode = "player" };

        // 1. GUID 作为 PlayerId，未显式覆盖默认 LocalPlayerId ("player")
        var guidPlayer = new PlayerState(Guid.NewGuid().ToString("N"), "RunUser", HeroNames.Vanessa);
        var ctxNoOpp = new RuleExecutionContext(repo, RuleScope.OutOfCombat, guidPlayer, null, new Random(1));
        Assert.Equal([guidPlayer], TargetResolver.ResolvePlayers(absPlayerTarget, ctxNoOpp));
        Assert.Equal([guidPlayer], TargetResolver.ResolvePlayers(absLowerTarget, ctxNoOpp));

        // 2. 即使存在 OpponentPlayer 且其 PlayerId 恰好撞名默认 "player"，单人局外上下文仍必须解析到 SelfPlayer
        var fakeOppNamedPlayer = new PlayerState("player", "SnapshotOpp", HeroNames.Dooley);
        var ctxWithNamedOpp = new RuleExecutionContext(repo, RuleScope.OutOfCombat, guidPlayer, fakeOppNamedPlayer, new Random(2));
        Assert.Equal([guidPlayer], TargetResolver.ResolvePlayers(absPlayerTarget, ctxWithNamedOpp));

        // 3. 当 LocalPlayerId 与 PlayerId 均为任意非标准串时同样正常解析
        var customPlayer = new PlayerState("acct_998877", "Custom", HeroNames.Mak);
        var ctxCustom = new RuleExecutionContext(repo, RuleScope.OutOfCombat, customPlayer, null, new Random(3))
        {
            LocalPlayerId = "totally_different_local_id",
        };
        Assert.Equal([customPlayer], TargetResolver.ResolvePlayers(absPlayerTarget, ctxCustom));
    }

    [Fact]
    public void Issue06_20_SkillSpawn_InheritTierAscendingFallback_And_NoPlayerDuplicates()
    {
        var repo = Repo.Value;
        var voucherTpl = repo.GetCard(SkillVoucherId)!;

        // 构造技能查询：允许全英雄技能（IgnoreHero），不允许玩家重复，InheritTier=true
        // 附加 HiddenTag=["Freeze"]：DB 中 Freeze 技能在 Bronze=0、Silver=11、Gold=1、Diamond=0
        // 当以青铜触发源继承青铜阶时，Bronze 档空池必须自动提升至 Silver 档，绝不越级直跳 Gold/Diamond
        var freezeSkillQuery = JsonSerializer.Deserialize<RuleNode>(
            """
            {
              "$type": "TSpawnContextQuery",
              "Limit": { "$type": "TFixedValue", "Value": 1.0 },
              "Behaviors": [
                { "$type": "TSpawnBehaviorAllowPlayerDuplicates", "AllowPlayerDuplicates": false },
                { "$type": "TSpawnBehaviorIgnoreHero", "IgnoreHero": true },
                { "$type": "TSpawnBehaviorInheritTier", "Inherits": true }
              ],
              "Groups": [{
                "$type": "TSpawnGroup",
                "SelectionMethod": "Random",
                "Filters": [{
                  "$type": "TSpawnFilterQuery",
                  "Constraints": {
                    "$type": "ConstraintAnd",
                    "Constraints": [
                      { "$type": "ConstraintCardType", "Types": ["Skill"], "IsNot": false },
                      { "$type": "ConstraintHiddenTag", "HiddenTags": ["Freeze"], "IsNot": false }
                    ]
                  }
                }]
              }]
            }
            """,
            GameDatabaseLoader.JsonOptions)!;

        var player = new PlayerState(Guid.NewGuid().ToString("N"), "Vanessa", HeroNames.Vanessa);
        var bronzeSource = new CardInstance(voucherTpl, CardTier.Bronze);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先连续抽取直至全部 8 张 Silver-starting 的 Freeze 技能耗尽：前 8 次必须全为 Silver 阶且绝不重复
        for (int i = 0; i < 8; i++)
        {
            var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(10 + i))
            {
                SelfCard = bronzeSource,
            };
            var picked = SpawnQueryEngine.SpawnCards(freezeSkillQuery, ctx);
            Assert.Single(picked);
            Assert.Equal(CardTier.Silver, picked[0].Tier);
            Assert.True(seenIds.Add(picked[0].Template.Id), "不允许重复给出已有技能");
            player.Skills.Add(picked[0]);
        }

        // 第 9、10 次 Silver 已耗尽，应自动向上提升至 Gold 阶拿到全部 2 张 Gold Freeze 技能
        for (int i = 0; i < 2; i++)
        {
            var goldFallbackCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(90 + i))
            {
                SelfCard = bronzeSource,
            };
            var pickedGold = SpawnQueryEngine.SpawnCards(freezeSkillQuery, goldFallbackCtx);
            Assert.Single(pickedGold);
            Assert.Equal(CardTier.Gold, pickedGold[0].Tier);
            Assert.True(seenIds.Add(pickedGold[0].Template.Id));
            player.Skills.Add(pickedGold[0]);
        }

        // 第 11 次 Gold 也耗尽，自动提升至 Diamond 阶拿到剩余 1 张 Diamond Freeze 技能
        var diamondFallbackCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(99))
        {
            SelfCard = bronzeSource,
        };
        var pickedDiamond = SpawnQueryEngine.SpawnCards(freezeSkillQuery, diamondFallbackCtx);
        Assert.Single(pickedDiamond);
        Assert.Equal(CardTier.Diamond, pickedDiamond[0].Tier);
        Assert.True(seenIds.Add(pickedDiamond[0].Template.Id));
        player.Skills.Add(pickedDiamond[0]);

        // 全部 Freeze 技能已耗尽且 AllowPlayerDuplicates=false 时，不再重复发放
        var exhaustedCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(100))
        {
            SelfCard = bronzeSource,
        };
        Assert.Empty(SpawnQueryEngine.SpawnCards(freezeSkillQuery, exhaustedCtx));
    }

    [Fact]
    public void Issue06_20_SkillVoucher_LearningCrystal_And_BookOfSecrets_EndToEndOutOfCombat()
    {
        var repo = Repo.Value;
        var voucherTpl = repo.GetCard(SkillVoucherId)!;
        var crystalTpl = repo.GetCard(LearningCrystalId)!;
        var bookTpl = repo.GetCard(BookOfSecretsId)!;

        // 1. 青铜 Skill Voucher 卖出 -> 100% 生成 1 个合法不重复技能并加入 Skills
        var pVoucher = new PlayerState(Guid.NewGuid().ToString("N"), "Vanessa", HeroNames.Vanessa, 500, 20, 10, 10);
        for (int round = 0; round < 10; round++)
        {
            var voucher = new CardInstance(voucherTpl, CardTier.Bronze);
            Assert.True(pVoucher.Hand.TryAddAuto(voucher));
            var beforeIds = pVoucher.Skills.Select(s => s.Template.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var batch = OutOfCombatController.OnCardSoldOrConsumed(pVoucher, voucher, repo, new Random(300 + round));
            Assert.Single(batch.SpawnedCards);
            var spawned = batch.SpawnedCards[0].Card;
            Assert.Equal("Skill", spawned.Template.Type);
            Assert.DoesNotContain(spawned.Template.Id, beforeIds);
            Assert.Contains(spawned, pVoucher.Skills);
            Assert.True(repo.HasZhTranslation(spawned.Template));
        }

        // 2. Learning Crystal 卖出 -> 100% 生成 1 个“其他英雄”的合法不重复新技能（不含玩家自身英雄专属）
        var pCrystal = new PlayerState(Guid.NewGuid().ToString("N"), "Dooley", HeroNames.Dooley, 500, 20, 10, 10);
        for (int round = 0; round < 10; round++)
        {
            var crystal = new CardInstance(crystalTpl, CardTier.Gold);
            Assert.True(pCrystal.Hand.TryAddAuto(crystal));
            var beforeIds = pCrystal.Skills.Select(s => s.Template.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var batch = OutOfCombatController.OnCardSoldOrConsumed(pCrystal, crystal, repo, new Random(400 + round));
            Assert.Single(batch.SpawnedCards);
            var spawned = batch.SpawnedCards[0].Card;
            Assert.Equal("Skill", spawned.Template.Type);
            Assert.DoesNotContain(HeroNames.Dooley, spawned.Template.Heroes, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(CardTier.Legendary, spawned.Template.DefaultTier);
            Assert.DoesNotContain(spawned.Template.Id, beforeIds);
            Assert.Contains(spawned, pCrystal.Skills);
        }

        // 3. 奥秘之书 (Book of Secrets) 跨天 OnDayStarted -> 全 8 英雄（含 Hero8）+ Common 已汉化非 Legendary 不重复技能
        var pBook = new PlayerState(Guid.NewGuid().ToString("N"), "Mak", HeroNames.Mak, 500, 20, 10, 10);
        var bookInStash = new CardInstance(bookTpl, CardTier.Silver);
        Assert.True(pBook.Stash.TryAddAuto(bookInStash));

        var seenBookHeroes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int day = 2; day <= 35; day++)
        {
            pBook.Day = day;
            pBook.Hour = 1;
            var beforeIds = pBook.Skills.Select(s => s.Template.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var dayBatch = OutOfCombatController.OnDayStarted(pBook, repo, new Random(700 + day));
            // 若前面跨天抽到自带 OnDayStarted 的技能（如 Supply Cache / Dumpster Diving），其也会往 Hand 产生物品；
            // 此处校验奥秘之书每天精确产出 1 张置于 Skills 的新技能。
            var skillSpawn = Assert.Single(dayBatch.SpawnedCards, x => x.Card.Template.Type == "Skill");
            Assert.Equal("Skills", skillSpawn.Section);
            var skill = skillSpawn.Card;
            Assert.Equal("Skill", skill.Template.Type);
            Assert.NotEqual(CardTier.Legendary, skill.Template.DefaultTier);
            Assert.NotEqual(CardTier.Legendary, skill.Tier);
            Assert.DoesNotContain(skill.Template.Id, beforeIds);
            Assert.True(repo.HasZhTranslation(skill.Template));
            foreach (var h in skill.Template.Heroes)
                seenBookHeroes.Add(h);
        }
        Assert.True(seenBookHeroes.Count >= 5, $"奥秘之书多次跨天应抽到跨多英雄技能，实际覆盖: {string.Join(",", seenBookHeroes)}");

        // 验证 Book of Secrets 候选池确实包含 Hero8（双龙）专属已汉化 Always 技能
        var bookQuery = bookTpl.Abilities!["0"].Action!.SpawnContext!;
        var hero8SkillPool = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int s = 1; s <= 120; s++)
        {
            var probePlayer = new PlayerState("probe_book", "Mak", HeroNames.Mak);
            var probeCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, probePlayer, null, new Random(9000 + s))
            {
                SelfCard = new CardInstance(bookTpl, CardTier.Silver),
            };
            var spawned = SpawnQueryEngine.SpawnCards(bookQuery, probeCtx);
            Assert.Single(spawned);
            foreach (var h in spawned[0].Template.Heroes)
                hero8SkillPool.Add(h);
        }
        Assert.Contains(HeroNames.Hero8, hero8SkillPool);
        Assert.Contains(HeroNames.Common, hero8SkillPool);

        // 4. 验证 TCardConditionalHasEnchantment（Enchantment=null 与 Radiant / Golden 互斥）：
        // - Radiant Book of Secrets 仅触发基础能力 "0" 产出 1 张技能
        // - Golden Book of Secrets 屏蔽基础能力 "0" 仅触发 Golden 能力 "e1" 产出 1 张 Gold/Income 经济技能
        var pRadiant = new PlayerState(Guid.NewGuid().ToString("N"), "Vanessa", HeroNames.Vanessa);
        pRadiant.Hand.TryAddAuto(new CardInstance(bookTpl, CardTier.Silver, "Radiant"));
        var radiantBatch = OutOfCombatController.OnDayStarted(pRadiant, repo, new Random(1234));
        Assert.Single(radiantBatch.SpawnedCards);

        var pGolden = new PlayerState(Guid.NewGuid().ToString("N"), "Vanessa", HeroNames.Vanessa);
        pGolden.Hand.TryAddAuto(new CardInstance(bookTpl, CardTier.Silver, "Golden"));
        var goldenBatch = OutOfCombatController.OnDayStarted(pGolden, repo, new Random(5678));
        Assert.Single(goldenBatch.SpawnedCards);
        var goldenSkill = goldenBatch.SpawnedCards[0].Card;
        Assert.True(
            goldenSkill.Template.HiddenTags.Any(t => t is "EconomyReference" or "Income" or "Gold"),
            $"Golden Book of Secrets 应仅触发经济技能，实际: {goldenSkill.Template.InternalName}");
    }

    // =========================================================================
    // #17：Shuriken 等弹药卡局外升阶/永久改 AmmoMax/脱战满弹与 Multicast 同步
    // =========================================================================

    [Fact]
    public void Issue17_Shuriken_OutOfCombatUpgradeAndAmmoMaxSync_RefillsAmmoAndUpdatesMulticast()
    {
        var repo = Repo.Value;
        var shurikenTpl = repo.GetCard(ShurikenId)!;
        var gunpowderTpl = repo.GetCard(GunpowderId)!;

        // 1. 初始青铜 Shuriken：AmmoMax=3, CurrentAmmo=3, 经光环重算后 Multicast=3
        var player = new PlayerState("p_shuriken", "Vanessa", HeroNames.Vanessa, 500, 20, 10, 10);
        var shuriken = new CardInstance(shurikenTpl, CardTier.Bronze);
        Assert.True(player.Hand.TryPlaceOrShift(shuriken, 0));

        Assert.Equal(3, shuriken.EffectiveAmmoMax);
        Assert.Equal(3, shuriken.CurrentAmmo);
        OutOfCombatController.RecalculateOutOfCombatAuras(player, repo);
        Assert.Equal(3, (int)Math.Round(shuriken.GetAttribute("Multicast")));

        // 2. 局外直接升至白银（SetTier）：立即变为 4/4，重算光环后 Multicast=4
        shuriken.SetTier(CardTier.Silver, isTemporary: false);
        Assert.Equal(4, shuriken.EffectiveAmmoMax);
        Assert.Equal(4, shuriken.CurrentAmmo);
        OutOfCombatController.OnCardUpgraded(player, shuriken, repo, new Random(1));
        Assert.Equal(4, (int)Math.Round(shuriken.GetAttribute("Multicast")));

        // 3. TryUpgrade 升至黄金 & 钻石：自动满弹 5/5 -> 6/6 且 Multicast=5 -> 6
        Assert.True(shuriken.TryUpgrade(isTemporary: false));
        Assert.Equal(CardTier.Gold, shuriken.Tier);
        Assert.Equal(5, shuriken.EffectiveAmmoMax);
        Assert.Equal(5, shuriken.CurrentAmmo);
        OutOfCombatController.RecalculateOutOfCombatAuras(player, repo);
        Assert.Equal(5, (int)Math.Round(shuriken.GetAttribute("Multicast")));

        Assert.True(shuriken.TryUpgrade(isTemporary: false));
        Assert.Equal(CardTier.Diamond, shuriken.Tier);
        Assert.Equal(6, shuriken.EffectiveAmmoMax);
        Assert.Equal(6, shuriken.CurrentAmmo);
        OutOfCombatController.RecalculateOutOfCombatAuras(player, repo);
        Assert.Equal(6, (int)Math.Round(shuriken.GetAttribute("Multicast")));

        // 4. 局外通过 ModifyAttribute 永久增加 AmmoMax（如白银黑火药 Gunpowder 卖出）-> 立即补满且 Multicast 同步增长
        var gunpowder = new CardInstance(gunpowderTpl, gunpowderTpl.DefaultTier); // Silver: Custom_0=1
        Assert.True(player.Hand.TryPlaceOrShift(gunpowder, 2));
        OutOfCombatController.OnCardSoldOrConsumed(player, gunpowder, repo, new Random(2));
        Assert.Equal(7, shuriken.EffectiveAmmoMax);
        Assert.Equal(7, shuriken.CurrentAmmo);
        Assert.Equal(7, (int)Math.Round(shuriken.GetAttribute("Multicast")));

        // 5. 战斗消耗与脱战复位：战斗克隆体耗光弹药后临时升阶不凭空补弹，原卡脱战 ResetForCombat 恢复满弹
        var combatClone = player.CloneForCombat();
        var simShuriken = combatClone.Hand.Cards.Single(c => c.InstanceId == shuriken.InstanceId);
        simShuriken.SetTier(CardTier.Bronze, isTemporary: false); // 回到低阶测临时升级（EffectiveAmmoMax=4）
        simShuriken.CurrentAmmo = 1; // 模拟战斗中消耗弹药剩余 1 发
        Assert.True(simShuriken.TryUpgrade(isTemporary: true));
        Assert.Equal(1, simShuriken.CurrentAmmo); // 战斗临时升阶不直接凭空满弹

        shuriken.CurrentAmmo = 0;
        shuriken.ResetForCombat(repo);
        Assert.Equal(7, shuriken.CurrentAmmo);
    }
}
