using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V12 底层规则引擎前置能力单元测试：
/// 1. 无主事件卡（EventEncounter / EncounterStep / PlayerEffect / null SelfCard）的 SelfSkills / SelfHand / SelfStash / SelfHandAndStash 区段回退解析；
/// 2. 全库 EventEncounter / PedestalEncounter / EncounterStep / PlayerEffect 触发器与 18 种动作覆盖核验（含 Stash 目标与光环联动）；
/// 3. 官方 14 张 PlayerEffect 系统卡在局外生命周期（开局、购入、战后、Reagent 变形、金币增减、锦标赛光环、狂暴与节奏压制）全链路验证。
/// </summary>
public sealed class V12CorePlayerEffectsTests
{
    private const string EliEncounterId = "0ff275f7-60ef-4428-acb6-1a138d03152d";
    private const string EliGroup1SkillId = "afe451fd-7726-4ed6-9ad9-48d917a12012";
    private const string LangxianId = "53099361-599f-4c1a-ad4a-d2fbb491b756";
    private const string AbsorbMemoriesStepId = "5fff0970-87d1-4e43-a466-b771deaf40fe";
    private const string BurnItDownStepId = "38ec2202-644e-42d5-8d6b-7476e7df9f42";
    private const string JungleCaltropsExitStepId = "0b9d5868-5ea5-4507-8faf-1654dda11c08";
    private const string FoundHealingSpringStepId = "3ca4a8dc-a6cc-41ae-9f98-b0e99e0cf85d";
    private const string PhotoBoothStepId = "4503488d-5c22-4853-988e-a9045146fb40";
    private const string KukriWeaponId = "03b1fd16-6b0d-4847-a76f-6c586f7c5958";
    private const string PearlAquaticId = "1312cf29-3dbb-446f-88b2-0d4999e68d78";
    private const string NarwhalAquaticWeaponId = "00e3a7ff-af5e-47ce-a2c0-2d4ec31ac7d7";
    private const string MyrrhReagentId = "01f6e150-ba23-429c-a43b-2508f63fc798";
    private const string NightshadeReagentId = "0bf49a3b-e610-4f2b-8420-1a8a7d48e70a";
    private const string StoveSocketId = "c017c0dd-af3c-47e0-b510-3661d9dcd7f2";
    private const string CoolerSocketId = "d5515ae3-b98d-4272-8add-82f441969ffe";

    private const string BaseRageEffectId = "4472da8a-26a3-4e10-bd9a-e93c2e22f19c";
    private const string BaseTempoEffectId = "e8478722-1851-4b05-9749-1480352ff710";
    private const string EnragePortraitEffectId = "d2ba43c2-0ba9-4299-b737-7594caa507ea";

    private static readonly Lazy<GameDataRepository> SharedRepo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    private static CardInstance MakeCard(GameDataRepository repo, string id, CardTier? tier = null)
    {
        var tpl = repo.GetCard(id) ?? throw new InvalidOperationException($"未找到模板 {id}");
        return new CardInstance(tpl, tier);
    }

    private static bool ContainsSection(RuleNode? node, string section)
    {
        if (node == null) return false;
        if (string.Equals(node.TargetSection, section, StringComparison.Ordinal))
            return true;
        if (ContainsSection(node.Target, section) || ContainsSection(node.Subject, section) || ContainsSection(node.Source, section))
            return true;
        if (node.Actions != null && node.Actions.Any(a => ContainsSection(a, section)))
            return true;
        if (node.Prerequisites != null && node.Prerequisites.Any(p => ContainsSection(p, section)))
            return true;
        foreach (var sub in node.GetConditionList(GameDatabaseLoader.JsonOptions))
        {
            if (ContainsSection(sub, section)) return true;
        }
        return false;
    }

    // =========================================================================
    // 1. 无主事件卡区段回退解析（TargetResolver.cs）
    // =========================================================================

    [Fact]
    public void UnownedEventCard_SelfSections_ShouldResolveToSelfPlayerAndUnlockEliGroup()
    {
        var repo = SharedRepo.Value;
        var player = new PlayerState("run-p1", "MakPlayer", HeroNames.Mak, 500, 20, 50, 10);
        var skillCard = MakeCard(repo, EliGroup1SkillId);
        var handItem = MakeCard(repo, KukriWeaponId);
        var stashItem = MakeCard(repo, PearlAquaticId);
        player.Skills.Add(skillCard);
        Assert.True(player.Hand.TryAddAuto(handItem));
        Assert.True(player.Stash.TryAddAuto(stashItem));

        var eliCard = MakeCard(repo, EliEncounterId); // 无主 EventEncounter 临时卡
        var shelfOpp = new PlayerState("shelf", "Shelf", HeroNames.Common, 100, 20, 0, 10);
        var ctxWithEventSelf = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, shelfOpp, new Random(42))
        {
            LocalPlayerId = "different-local-id", // 故意错开 LocalPlayerId 验证不误伤无主事件卡
            SelfCard = eliCard
        };
        var ctxWithNullSelf = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, shelfOpp, new Random(42))
        {
            LocalPlayerId = "different-local-id",
            SelfCard = null
        };

        foreach (var ctx in new[] { ctxWithEventSelf, ctxWithNullSelf })
        {
            var skills = TargetResolver.ResolveCards(new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfSkills" }, ctx);
            Assert.Single(skills);
            Assert.Same(skillCard, skills[0]);

            var hand = TargetResolver.ResolveCards(new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" }, ctx);
            Assert.Single(hand);
            Assert.Same(handItem, hand[0]);

            var stash = TargetResolver.ResolveCards(new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfStash" }, ctx);
            Assert.Single(stash);
            Assert.Same(stashItem, stash[0]);

            var both = TargetResolver.ResolveCards(new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHandAndStash" }, ctx);
            Assert.Equal(2, both.Count);
            Assert.Contains(handItem, both);
            Assert.Contains(stashItem, both);
        }

        // 核对 Eli 的全部 SelfSkills 条件组在真实 SpawnQueryEngine 中精确判定通过/未通过
        var eliTpl = repo.GetCard(EliEncounterId)!;
        var eliSpawn = eliTpl.SelectionContext!.SpawnContext!;
        var matchingGroup = eliSpawn.Groups!.First(g =>
            g.Prerequisites != null &&
            g.Prerequisites.Any(p => ContainsSection(p, "SelfSkills") && p.Subject?.RawConditionsElement?.GetRawText().Contains(EliGroup1SkillId, StringComparison.OrdinalIgnoreCase) == true));

        Assert.True(ValueAndConditionEvaluator.EvaluatePrerequisites(matchingGroup.Prerequisites, ctxWithEventSelf));

        // 若移除技能，前置条件应严格失败
        player.Skills.Clear();
        Assert.False(ValueAndConditionEvaluator.EvaluatePrerequisites(matchingGroup.Prerequisites, ctxWithEventSelf));

        // 扫描 DB 中所有使用 SelfSkills / SelfHand / SelfStash / SelfHandAndStash 的 EventEncounter SpawnGroup 前置条件
        int checkedGroups = 0;
        var fullPlayer = new PlayerState("sweep-p", "Sweep", HeroNames.Vanessa, 500, 20, 50, 10);
        fullPlayer.Skills.Add(MakeCard(repo, EliGroup1SkillId));
        fullPlayer.Hand.TryAddAuto(MakeCard(repo, KukriWeaponId));
        fullPlayer.Stash.TryAddAuto(MakeCard(repo, PearlAquaticId));

        foreach (var enc in repo.CardsById.Values.Where(c => c.Type is "EventEncounter" or "EncounterStep"))
        {
            var groups = enc.SelectionContext?.SpawnContext?.Groups;
            if (groups == null) continue;
            var encCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, fullPlayer, shelfOpp, new Random(7))
            {
                SelfCard = new CardInstance(enc)
            };
            foreach (var grp in groups)
            {
                if (grp.Prerequisites == null) continue;
                foreach (var prereq in grp.Prerequisites)
                {
                    if (ContainsSection(prereq, "SelfSkills")
                        || ContainsSection(prereq, "SelfHand")
                        || ContainsSection(prereq, "SelfStash")
                        || ContainsSection(prereq, "SelfHandAndStash"))
                    {
                        // 不应抛异常；针对 Eli 对应技能应可命中
                        _ = ValueAndConditionEvaluator.EvaluatePrerequisite(prereq, encCtx);
                        checkedGroups++;
                    }
                }
            }
        }
        Assert.True(checkedGroups >= 300, $"预期 DB 存在大量 Self* 条件分支，实际={checkedGroups}");
    }

    // =========================================================================
    // 2. 全库事件动作 / 触发器覆盖检查与 Stash / 光环联动
    // =========================================================================

    [Fact]
    public void AllEventEncountersStepsPedestalsAndPlayerEffects_ShouldHaveZeroUnknownTriggersOrAuraActions()
    {
        var repo = SharedRepo.Value;
        int checkedCards = 0;
        int checkedAbilities = 0;
        int checkedAuras = 0;

        foreach (var tpl in repo.CardsById.Values.Where(c =>
                     c.Type is "EventEncounter" or "PedestalEncounter" or "EncounterStep" or "PlayerEffect"))
        {
            checkedCards++;
            foreach (var ab in tpl.Abilities.Values)
            {
                checkedAbilities++;
                Assert.NotNull(ab.Trigger);
                Assert.True(AbilityExecutor.IsKnownTriggerType(ab.Trigger!.Type),
                    $"事件/系统卡 {tpl.InternalName} ({tpl.Id}) 使用了未注册触发器 {ab.Trigger.Type}");
            }
            foreach (var au in tpl.Auras.Values)
            {
                checkedAuras++;
                if (au.Action != null)
                {
                    Assert.True(AuraEngine.IsKnownAuraActionType(au.Action.Type),
                        $"事件/系统卡 {tpl.InternalName} ({tpl.Id}) 使用了未注册光环动作 {au.Action.Type}");
                }
            }
        }

        Assert.True(checkedCards >= 1200, $"预期至少扫描 1200 张事件/基座/步骤/系统卡，实际={checkedCards}");
        Assert.True(checkedAbilities >= 2000, $"预期至少扫描 2000 条事件能力，实际={checkedAbilities}");
        Assert.True(checkedAuras >= 20, $"预期至少扫描 20 条事件光环，实际={checkedAuras}");
    }

    [Fact]
    public void EventActions_ExitReplacement_ExclusionSet_Regen_Tags_Transform_Upgrade_InHandAndStash_WorkCleanly()
    {
        var repo = SharedRepo.Value;
        var unknownHits = new List<string>();
        using var scopeGuard = RuleExecutionContext.BeginUnknownRuleTypeScope((cat, t) => unknownHits.Add($"{cat}:{t}"));

        var player = new PlayerState("act-p", "Player", HeroNames.Vanessa, 500, 20, 50, 10);
        player.InitializeExclusionSet(repo.GameMode);

        // 2a. TActionExitReplacementSet（[Jungle Expedition] Caltrops Exit）
        var exitStep = MakeCard(repo, JungleCaltropsExitStepId);
        var exitBatch = OutOfCombatController.OnCardSelected(player, exitStep, repo, new Random(1));
        Assert.True(exitBatch.ExitSelectionRequested, "TActionExitReplacementSet 应触发 ExitSelectionRequested");

        // 2b. TActionGameAddToExclusionSet / RemoveFromExclusionSet + TActionCardUpgrade（Burn it Down）
        const string burnItemId = "069755d6-fa1b-4c09-a7c9-31c3338184f3"; // Pickled Peppers (Burn item)
        var burnWeapon = MakeCard(repo, burnItemId, CardTier.Bronze);
        var burnWeapon2 = MakeCard(repo, burnItemId, CardTier.Bronze);
        var burnWeapon3 = MakeCard(repo, burnItemId, CardTier.Bronze);
        player.Hand.TryAddAuto(burnWeapon);
        player.Hand.TryAddAuto(burnWeapon2);
        player.Stash.TryAddAuto(burnWeapon3);
        var burnStep = MakeCard(repo, BurnItDownStepId);
        var burnBatch = OutOfCombatController.OnCardSelected(player, burnStep, repo, new Random(2));
        Assert.Contains("48a25657-baf6-440d-9085-bd7aeda1638a", player.ExclusionSet); // Library 被排除
        Assert.DoesNotContain("bea25390-0ec6-45fd-be00-d07649d7586a", player.ExclusionSet); // Burning Library 被解锁
        Assert.True(burnBatch.UpgradedCards.Count >= 1, "Burn it Down 应至少升阶 1 件 Burn 物品");

        // 2c. TActionPlayerRegenApply（Found a Healing Spring）
        var springStep = MakeCard(repo, FoundHealingSpringStepId);
        double regenBefore = player.GetAttribute("HealthRegen");
        var regenBatch = OutOfCombatController.OnCardSelected(player, springStep, repo, new Random(3));
        Assert.True(player.GetAttribute("HealthRegen") > regenBefore, "Found a Healing Spring 应提升永久 HealthRegen");
        Assert.True(regenBatch.PlayerAttributeDeltas.GetValueOrDefault("HealthRegen", 0) > 0);

        // 2d. TActionCardAddTagsRandom + TActionPlayerModifyAttribute（Photo Booth - Share with Followers）
        var photoStep = MakeCard(repo, PhotoBoothStepId);
        int goldBefore = player.Gold;
        OutOfCombatController.OnCardSelected(player, photoStep, repo, new Random(4));
        Assert.True(player.Gold > goldBefore, "Photo Booth 应先加随机标签再按标签数增加金币");

        // 2e. TActionCardTransform 针对 Stash 中的目标通过 SelfStash 正确替换并留在 Stash
        var stashSource = MakeCard(repo, KukriWeaponId);
        var stashP = new PlayerState("st-p", "StashPlayer", HeroNames.Vanessa, 500, 20, 50, 10);
        stashP.Stash.TryPlaceOrShift(stashSource, 2);
        var transformAction = new RuleNode
        {
            Type = "TActionCardTransform",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfStash" },
            SpawnContext = new RuleNode
            {
                Type = "TSpawnContextQuery",
                SelectionMethod = "Sequential",
                Groups =
                [
                    new RuleNode
                    {
                        Type = "TSpawnGroup",
                        Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [PearlAquaticId] }]
                    }
                ]
            }
        };
        var txCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, stashP, null, new Random(5));
        bool txOk = AbilityExecutor.ExecuteAction(transformAction, stashSource, txCtx);
        Assert.True(txOk);
        Assert.True(stashSource.IsDestroyed);
        Assert.DoesNotContain(stashSource, stashP.Stash.Cards);
        Assert.Contains(stashP.Stash.Cards, c => c.Template.Id == PearlAquaticId && c.StartSlot == 2);

        Assert.Empty(unknownHits);
    }

    [Fact]
    public void Langxian_InStashAndHand_AbsorbMemories_UpdatesCustom1AndDamageAura()
    {
        var repo = SharedRepo.Value;
        var player = new PlayerState("lx-p", "Vanessa", HeroNames.Vanessa, 500, 20, 50, 10);
        OutOfCombatController.InjectOfficialPlayerEffects(player, repo);

        var opp = new PlayerState("opp", "Opp", HeroNames.Pygmalien, 500, 20, 0, 10) { IsMonster = true };

        // 1. 先打赢 3 场战斗（2 PvE + 1 PvP），此时玩家尚未拿到狼筅（或狼筅在 Stash 中）
        var langxianInStash = MakeCard(repo, LangxianId, CardTier.Bronze); // Damage=40, Custom_0=40
        Assert.True(player.Stash.TryPlaceOrShift(langxianInStash, 4));

        OutOfCombatController.OnFightEnded(player, "PVE", "Win", repo, new Random(1), opp);
        OutOfCombatController.OnFightEnded(player, "PVE", "Victory", repo, new Random(2), opp);
        OutOfCombatController.OnFightEnded(player, "PVP", "Win", repo, new Random(3), opp);

        Assert.Equal(3.0, player.GetAttribute("Custom_5"));
        Assert.Equal(0.0, langxianInStash.GetAttribute("Custom_1"));

        // 2. 在 Hand 再放一把有旧胜场 Custom_1 = 1 的狼筅，验证 Absorb Memories 先 Immediate(*0) 再 Medium(+Custom_5)
        var langxianInHand = MakeCard(repo, LangxianId, CardTier.Bronze);
        langxianInHand.PermanentAdd["Custom_1"] = 1;
        Assert.True(player.Hand.TryPlaceOrShift(langxianInHand, 0));

        // 3. 选中 [Shrouded Figure] Absorb Memories (5fff0970)
        var absorbStep = MakeCard(repo, AbsorbMemoriesStepId);
        var batch = OutOfCombatController.OnCardSelected(player, absorbStep, repo, new Random(7));

        // 严正校验：Stash 与 Hand 两把狼筅均通过 AbsolutePlayerHandAndStash 被修正为 3（而非 1+3=4）
        Assert.Equal(3.0, langxianInStash.GetAttribute("Custom_1"));
        Assert.Equal(3.0, langxianInHand.GetAttribute("Custom_1"));
        // 光环重算应同步生效：40 + 3 * 40 = 160
        Assert.Equal(160.0, langxianInStash.GetAttribute("DamageAmount"));
        Assert.Equal(160.0, langxianInHand.GetAttribute("DamageAmount"));
        // Batch 变更日志应正确标脏 Stash 中的狼筅与其 DamageAmount / Custom_1 变动
        Assert.Contains(langxianInStash, batch.AffectedCards);
        Assert.Contains(batch.CardAttributeChanges, ch => ReferenceEquals(ch.Card, langxianInStash) && ch.Attribute == "Custom_1" && Math.Abs(ch.CurrentValue - 3.0) < 1e-6);
        Assert.Contains(batch.CardAttributeChanges, ch => ReferenceEquals(ch.Card, langxianInStash) && ch.Attribute == "DamageAmount" && Math.Abs(ch.CurrentValue - 160.0) < 1e-6);
    }

    // =========================================================================
    // 3. 官方 14 张 PlayerEffect 系统卡全量规则触发验证
    // =========================================================================

    [Fact]
    public void Official14PlayerEffects_AllCardsInDb_ShouldTriggerAccuratelyAcrossLifecycles()
    {
        var repo = SharedRepo.Value;

        // 先断言 DB 中确有全部 14 张 Type == "PlayerEffect" 系统卡
        var allPeTemplates = repo.CardsById.Values
            .Where(c => string.Equals(c.Type, "PlayerEffect", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(14, allPeTemplates.Count);

        // 3a. 验证未注入 PlayerEffect 时不擅自修改 Custom_3..Custom_11（防止与上层双计）
        var plainPlayer = new PlayerState("plain", "Plain", HeroNames.Vanessa, 500, 20, 50, 10);
        OutOfCombatController.OnItemPurchased(plainPlayer, MakeCard(repo, KukriWeaponId), repo, new Random(1));
        OutOfCombatController.OnFightEnded(plainPlayer, "PVE", "Win", repo, new Random(2));
        OutOfCombatController.OnPlayerAttributeChanged(plainPlayer, "Gold", 50, 70, repo, new Random(3));
        Assert.Equal(0.0, plainPlayer.GetAttribute("Custom_3"));
        Assert.Equal(0.0, plainPlayer.GetAttribute("Custom_4"));
        Assert.Equal(0.0, plainPlayer.GetAttribute("Custom_5"));

        // 3b. 注入官方追踪器（不含锦标赛）并验证不污染 player.Skills
        var p = new PlayerState("pe-p", "Vanessa", HeroNames.Vanessa, 500, 20, 10, 10);
        OutOfCombatController.InjectOfficialPlayerEffects(p, repo, includeTournamentMode: false, includeHeroRunStartSpawners: true);
        Assert.Empty(p.Skills); // 绝不污染 AbsolutePlayerSkills 无条件计数
        Assert.Equal(0.0, p.GetAttribute("Custom_11"));

        // (1) Gold Gained -> Custom_3 (f74011cc)
        p.Gold = 25;
        var gGainBatch = OutOfCombatController.OnPlayerAttributeChanged(p, "Gold", 10, 25, repo, new Random(10));
        Assert.Equal(15.0, p.GetAttribute("Custom_3"));
        Assert.Equal(15.0, gGainBatch.PlayerAttributeDeltas.GetValueOrDefault("Custom_3", 0));

        // 扣金币（Loss）不得增加 Custom_3
        p.Gold = 18;
        OutOfCombatController.OnPlayerAttributeChanged(p, "Gold", 25, 18, repo, new Random(11));
        Assert.Equal(15.0, p.GetAttribute("Custom_3"));

        // (2) Weapons Bought -> Custom_4 (5b39876b) & Aquatic Bought -> Custom_10 (cef03dfe)
        var kukri = MakeCard(repo, KukriWeaponId); // Weapon only
        var pearl = MakeCard(repo, PearlAquaticId); // Aquatic only
        var narwhal = MakeCard(repo, NarwhalAquaticWeaponId); // Weapon + Aquatic
        var freeWeapon = MakeCard(repo, KukriWeaponId);

        p.Hand.TryAddAuto(kukri);
        OutOfCombatController.OnItemPurchased(p, kukri, repo, new Random(12), isFree: false);
        Assert.Equal(1.0, p.GetAttribute("Custom_4"));
        Assert.Equal(0.0, p.GetAttribute("Custom_10"));

        p.Hand.TryAddAuto(pearl);
        OutOfCombatController.OnItemPurchased(p, pearl, repo, new Random(13), isFree: false);
        Assert.Equal(1.0, p.GetAttribute("Custom_4"));
        Assert.Equal(1.0, p.GetAttribute("Custom_10"));

        p.Hand.TryAddAuto(narwhal);
        OutOfCombatController.OnItemPurchased(p, narwhal, repo, new Random(14), isFree: false);
        Assert.Equal(2.0, p.GetAttribute("Custom_4"));
        Assert.Equal(2.0, p.GetAttribute("Custom_10"));

        p.Hand.TryAddAuto(freeWeapon);
        OutOfCombatController.OnItemPurchased(p, freeWeapon, repo, new Random(15), isFree: true);
        Assert.Equal(2.0, p.GetAttribute("Custom_4")); // 免费获得不计购买

        // (3) Fights Won / Lost / Monster Lost / Player Lost -> Custom_5..Custom_8
        OutOfCombatController.OnFightEnded(p, "PVE", "Win", repo, new Random(20));
        OutOfCombatController.OnFightEnded(p, "PVP", "Victory", repo, new Random(21));
        OutOfCombatController.OnFightEnded(p, "PVE", "Lose", repo, new Random(22));
        OutOfCombatController.OnFightEnded(p, "PVP", "Defeat", repo, new Random(23));
        Assert.Equal(2.0, p.GetAttribute("Custom_5")); // 总胜场 2
        Assert.Equal(2.0, p.GetAttribute("Custom_6")); // 总败场 2
        Assert.Equal(1.0, p.GetAttribute("Custom_7")); // PvE 败场 1
        Assert.Equal(1.0, p.GetAttribute("Custom_8")); // PvP 败场 1

        // (4) Reagents Transformed -> Custom_9 (142058d9)
        // 4a. 通过 TActionCardTransform 变形手牌中的 Reagent（Nightshade）
        var reagentHand = MakeCard(repo, NightshadeReagentId);
        Assert.True(p.Hand.TryAddAuto(reagentHand));
        var txStep = new CardInstance(new CardTemplate
        {
            Id = "test_tx_reagent",
            InternalName = "TestTransformReagent",
            Type = "EncounterStep",
            Size = "Medium",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSelected" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardTransform",
                        Target = new RuleNode
                        {
                            Type = "TTargetCardSection",
                            TargetSection = "AbsolutePlayerHandAndStash",
                            RawConditionsElement = JsonDocument.Parse($"{{\"$type\":\"TCardConditionalId\",\"Id\":\"{NightshadeReagentId}\"}}").RootElement.Clone()
                        },
                        SpawnContext = new RuleNode
                        {
                            Type = "TSpawnContextQuery",
                            SelectionMethod = "Sequential",
                            Groups = [new RuleNode { Type = "TSpawnGroup", Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [PearlAquaticId] }] }]
                        }
                    }
                }
            }
        });
        OutOfCombatController.OnCardSelected(p, txStep, repo, new Random(30));
        Assert.Equal(1.0, p.GetAttribute("Custom_9"));

        // 4b. 通过 OutOfCombatController.OnCardTransformed 变形 Stash 中的 Reagent（Myrrh）
        var reagentStash = MakeCard(repo, MyrrhReagentId);
        Assert.True(p.Stash.TryAddAuto(reagentStash));
        var potionReplacement = MakeCard(repo, PearlAquaticId);
        OutOfCombatController.OnCardTransformed(p, reagentStash, potionReplacement, repo, new Random(31), swapInPlaceIfOldStillPresent: true);
        Assert.Equal(2.0, p.GetAttribute("Custom_9"));

        // 4c. 变形非 Reagent 物品不得累加 Custom_9
        OutOfCombatController.OnCardTransformed(p, kukri, MakeCard(repo, PearlAquaticId), repo, new Random(32), swapInPlaceIfOldStillPresent: true);
        Assert.Equal(2.0, p.GetAttribute("Custom_9"));

        // (5) Tournament Mode -> Custom_11 (01dfd497)
        OutOfCombatController.InjectOfficialPlayerEffects(p, repo, includeTournamentMode: true, includeHeroRunStartSpawners: false);
        OutOfCombatController.RecalculateOutOfCombatAuras(p, repo);
        Assert.Equal(1.0, p.GetAttribute("Custom_11"));

        // (6) Base Tempo Gain Cooldown Effect (e8478722)
        // 无 Tempo 卡时触发 FlatTempoGainCooldownReduction 压制（-1e15），有 Tempo 卡时取消压制
        Assert.True(p.GetAttribute("FlatTempoGainCooldownReduction") <= -1e14, "无 Tempo 卡时 Base Tempo Gain Cooldown Effect 应生效压制");
        var tempoCard = new CardInstance(new CardTemplate
        {
            Id = "test_tempo_item",
            InternalName = "TestTempoItem",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            HiddenTags = ["Tempo"]
        });
        p.Hand.TryAddAuto(tempoCard);
        OutOfCombatController.RecalculateOutOfCombatAuras(p, repo);
        Assert.Equal(0.0, p.GetAttribute("FlatTempoGainCooldownReduction"));

        // (7) Add random Stove or Cooler (15d06ad8) - Jules OnRunStarted
        var jules = new PlayerState("jules-p", "Jules", HeroNames.Jules, 500, 20, 10, 4);
        OutOfCombatController.InjectOfficialPlayerEffects(jules, repo, includeTournamentMode: false, includeHeroRunStartSpawners: true);
        var julesStartBatch = OutOfCombatController.OnRunStarted(jules, repo, new Random(40));
        Assert.Contains(jules.SocketEffects, se => se.Template.Id is StoveSocketId or CoolerSocketId);
        Assert.NotEmpty(julesStartBatch.SpawnedCards);

        // (8) Add random music note (1979db9f) - Hero8 OnRunStarted
        var hero8 = new PlayerState("h8-p", "Hero8", HeroNames.Hero8, 500, 20, 10, 4);
        OutOfCombatController.InjectOfficialPlayerEffects(hero8, repo, includeTournamentMode: false, includeHeroRunStartSpawners: true);
        var h8StartBatch = OutOfCombatController.OnRunStarted(hero8, repo, new Random(41));
        Assert.Contains(hero8.SocketEffects, se => se.Template.TypeDiscriminator == "TCardMusicNoteSocketEffect" || se.Template.MusicNote.HasValue);
        Assert.NotEmpty(h8StartBatch.SpawnedCards);

        // (9) Base Rage Effect (4472da8a) & Enrage portrait update (d2ba43c2)
        var ragePlayer = new PlayerState("rage-p", "Karnok", HeroNames.Karnok, 500, 20, 10, 10);
        var baseRage = MakeCard(repo, BaseRageEffectId); // 官方 Diamond 默认 Custom_0 = 10
        var enragePortrait = MakeCard(repo, EnragePortraitEffectId);
        ragePlayer.Skills.Add(baseRage);
        ragePlayer.Skills.Add(enragePortrait);

        var handWeapon = MakeCard(repo, KukriWeaponId);
        handWeapon.FreezeMs = 3000;
        handWeapon.SlowMs = 2000;
        ragePlayer.Hand.TryAddAuto(handWeapon);

        var rageCtx = new RuleExecutionContext(repo, RuleScope.InCombat, ragePlayer, null, new Random(50))
        {
            SelfCard = baseRage,
            OnDetailedTriggeredEvent = (evName, actor, src, tgt, subj, attr, prev, cur) =>
            {
                foreach (var sk in ragePlayer.Skills.ToList())
                {
                    foreach (var ab in sk.ActiveAbilities)
                    {
                        if (AbilityExecutor.MatchesTrigger(ab.Trigger, evName, sk, ragePlayer, src, tgt, subj, attr, prev, cur, "PVP", "",
                                new RuleExecutionContext(repo, RuleScope.InCombat, ragePlayer, null, new Random(50)) { SelfCard = sk }))
                        {
                            AbilityExecutor.ExecuteAction(ab.Action, sk,
                                new RuleExecutionContext(repo, RuleScope.InCombat, ragePlayer, null, new Random(50)) { SelfCard = sk });
                        }
                    }
                }
            }
        };

        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionPlayerRageApply",
            RawValueNode = JsonDocument.Parse("{\"$type\":\"TFixedValue\",\"Value\":100}").RootElement.Clone(),
            Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" }
        }, baseRage, rageCtx);

        Assert.True(ragePlayer.IsEnraged);
        Assert.Equal(0, handWeapon.FreezeMs);
        Assert.Equal(0, handWeapon.SlowMs);
        Assert.Equal(1, ragePlayer.PortraitIndex);

        AuraEngine.RecalculateAuras(ragePlayer, null, RuleScope.InCombat, 1, 1, repo);
        Assert.Equal(10.0, handWeapon.GetAttribute("PercentCooldownReduction"));

        // 狂暴结束触发 Enrage portrait update 重置头像索引
        ragePlayer.EnragedRemainingMs = 0;
        ragePlayer.Attributes["Enraged"] = 0;
        foreach (var ab in enragePortrait.ActiveAbilities)
        {
            var epCtx = new RuleExecutionContext(repo, RuleScope.InCombat, ragePlayer, null, new Random(51)) { SelfCard = enragePortrait };
            if (AbilityExecutor.MatchesTrigger(ab.Trigger, "TTriggerOnPlayerEnrageEnded", enragePortrait, ragePlayer, null, null, ragePlayer, null, 0, 0, "PVP", "", epCtx))
                AbilityExecutor.ExecuteAction(ab.Action, enragePortrait, epCtx);
        }
        Assert.Equal(0, ragePlayer.PortraitIndex);
    }
}
