using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Mods;
using Xunit;
using CoreMonsterTemplate = BazaarCore.Models.MonsterTemplate;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 5C（Wave 3 w3-perf-mods）专项回归测试套件：
/// 1. P1-18（RC-PERF-SERVERGC-EXPORT-BLOAT）：BazaarServer.csproj 默认 WorkstationGC/DATAS 配置、
///    WorkshopValidatorService 导出路径按目录内容指纹 + modSetHash 复用缓存与变更即时失效、
///    ModValidator 共享只读底库 + 轻量 Overlay 零底库污染与并发安全；
/// 2. P1-16（RC-MOD-ENCHANT-ELIGIBILITY）：未手写 enchantments 的自定义 Mod 物品自动合成 13 种标准附魔兼容集、
///    被原版附魔祭坛接受并在真实战斗中生效，且支持 disableAutoEnchantments / disabledEnchantments 显式关闭与排除；
/// 3. P1-17（RC-SAMPLE-MOD-HARDCODED-AURA-MULT）+ P1-07：星港贸易特许状与无畏宝库舰光环随 Custom_0 严格递增、
///    ModValidator 孤立 Custom_N 静态体检 Warning 与非官方 ECardAttributeType 属性键 Warning；
/// 4. P3-08（RC-OFFICIAL-BLANK-SKILL-ARTKEY，服务端侧）：MonsterPool 与 RunEngine（含 BossRush 强化抽技与战斗装配）
///    严格过滤 SpawningEligibility=="Never" 且 ArtKey 为空的官方调试技能。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave5C_ModsPerfAndValidationTests
{
    private const string KukriTemplateId = "03b1fd16-6b0d-4847-a76f-6c586f7c5958";        // Small Item 白名单载体
    private const string WeldingHelmetTemplateId = "08630fca-24b9-4d9b-9dbf-7da743a09dfa"; // Medium Item 白名单载体
    private const string BackroomDealingsId = "0045d15e-ac74-436f-a200-eeedab7baf90";      // Medium Skill 白名单载体

    private static SharedGameDataFixture.SharedGameDataSnapshot Fx => SharedGameDataFixture.Shared;

    private static (ModRegistry Registry, GameDataRepository Repo) LoadIsolatedSampleMod(string modFolderName)
    {
        var scanIssues = new List<ModValidationIssue>();
        var pkg = ModLoader.LoadSinglePackage(
            Path.Combine(Fx.ModsRoot, modFolderName),
            modFolderName,
            scanIssues);
        Assert.NotNull(pkg);
        Assert.Empty(scanIssues.Where(i => i.Severity == ModValidationSeverity.Error));

        var registry = ModRegistry.CreateIsolated(Fx.Repo, Fx.CreateValidCardIdsCopy());
        var loadReport = registry.LoadPackages([pkg!], Fx.ModsRoot, Fx.Repo, Fx.CreateValidCardIdsCopy(), keepPreviousOnError: false);
        Assert.True(loadReport.Succeeded, string.Join(" | ", loadReport.Issues.Select(i => $"{i.Code}:{i.Message}")));
        return (registry, registry.ActiveRepository);
    }

    private static CustomCardDefinition CreateMinimalActiveModWeapon(string modId, string slug = "test_blade") => new()
    {
        CustomId = $"{modId}:{slug}",
        BaseVisualTemplateId = KukriTemplateId,
        Type = "Item",
        Size = "Small",
        StartingTier = "Bronze",
        SpawningEligibility = "Always",
        Heroes = [HeroNames.Vanessa],
        Tags = ["Weapon", "Tool"],
        HiddenTags = ["Damage"],
        TitleZh = "测试星港短刃",
        DescZh = "用于验证附魔合成与静态体检的测试自定义武器。",
        ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害"],
        Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bronze"] = new()
            {
                Attributes = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["BuyPrice"] = 2,
                    ["SellPrice"] = 1,
                    ["CooldownMax"] = 4000,
                    ["DamageAmount"] = 20
                },
                AbilityIds = ["0"]
            },
            ["Silver"] = new()
            {
                Attributes = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["BuyPrice"] = 4,
                    ["SellPrice"] = 2,
                    ["CooldownMax"] = 4000,
                    ["DamageAmount"] = 40
                },
                AbilityIds = ["0"]
            }
        },
        Abilities = new Dictionary<string, AbilityDefinition>
        {
            ["0"] = new()
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                Action = new RuleNode
                {
                    Type = "TActionPlayerDamage",
                    Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }
                }
            }
        }
    };

    private static ModPackage WrapSingleCard(string modId, CustomCardDefinition card) => new()
    {
        RootDirectory = Path.GetTempPath(),
        Manifest = new ModManifest
        {
            Id = modId,
            Name = $"测试包-{modId}",
            Version = "1.0.0",
            Scope = "multiplayer-safe"
        },
        Cards = { card }
    };

    private static RuleNode ParseRule(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)
           ?? throw new InvalidOperationException("RuleNode 反序列化失败");

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (string subDir in Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
    }

    // ════════════════════════════════════════════════════════════════════════
    // 1. P1-18：BazaarServer.csproj 默认 WorkstationGC + DATAS（保留命令行覆盖）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_18_BazaarServerCsproj_DefaultsToWorkstationGcWithCliOverrideAndDatas()
    {
        string csprojPath = Path.Combine(Fx.RepoRoot, "src", "BazaarServer", "BazaarServer.csproj");
        Assert.True(File.Exists(csprojPath), $"未找到 {csprojPath}");

        var doc = XDocument.Load(csprojPath);
        var serverGcEl = doc.Descendants("ServerGarbageCollection").FirstOrDefault();
        Assert.NotNull(serverGcEl);
        Assert.Equal("false", serverGcEl!.Value.Trim(), ignoreCase: true);
        // Microsoft.NET.Sdk.Web.ProjectSystem.props 在 Sdk.props 阶段会预先将 ServerGarbageCollection 设为 true，
        // 若加 Condition="'$(ServerGarbageCollection)'==''" 会恒为假而失效；必须无条件声明 false（CLI -p:ServerGarbageCollection=true 作为全局属性仍天然覆盖）。
        string? gcCondition = serverGcEl.Attribute("Condition")?.Value;
        Assert.True(string.IsNullOrWhiteSpace(gcCondition), "ServerGarbageCollection 必须无条件声明 false 以覆盖 Microsoft.NET.Sdk.Web 默认值（命令行 -p:ServerGarbageCollection=true 仍可覆盖）");

        var datasEl = doc.Descendants("GarbageCollectionAdaptationMode").FirstOrDefault();
        Assert.NotNull(datasEl);
        Assert.Equal("1", datasEl!.Value.Trim());

        // 校验真实编译产物 BazaarServer.runtimeconfig.json 中的 GC 配置确已生效
#if DEBUG
        const string buildConfig = "Debug";
#else
        const string buildConfig = "Release";
#endif
        string[] candidateRuntimeConfigs =
        [
            Path.Combine(AppContext.BaseDirectory, "BazaarServer.runtimeconfig.json"),
            Path.Combine(Fx.RepoRoot, "src", "BazaarServer", "bin", buildConfig, "net8.0", "BazaarServer.runtimeconfig.json"),
            Path.Combine(Fx.RepoRoot, "src", "BazaarServer", "bin", buildConfig, "net8.0", "win-x64", "BazaarServer.runtimeconfig.json"),
            Path.Combine(Fx.RepoRoot, "dist", "server", "BazaarServer.runtimeconfig.json"),
        ];
        string? runtimeConfigPath = candidateRuntimeConfigs.FirstOrDefault(File.Exists);
        Assert.False(string.IsNullOrWhiteSpace(runtimeConfigPath), $"未找到已生成的 BazaarServer.runtimeconfig.json（已检查：{string.Join(", ", candidateRuntimeConfigs)}）");

        using var rcDoc = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath!));
        var configProps = rcDoc.RootElement
            .GetProperty("runtimeOptions")
            .GetProperty("configProperties");
        Assert.True(configProps.TryGetProperty("System.GC.Server", out var gcServerProp), "runtimeconfig.json 缺少 System.GC.Server");
        Assert.Equal(JsonValueKind.False, gcServerProp.ValueKind);
        Assert.True(configProps.TryGetProperty("System.GC.DynamicAdaptationMode", out var gcDatasProp), "runtimeconfig.json 缺少 System.GC.DynamicAdaptationMode");
        Assert.Equal(1, gcDatasProp.GetInt32());
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2. P1-18：WorkshopValidatorService export 同指纹复用校验缓存 + 变更立即失效
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P1_18_WorkshopValidatorService_ExportReusesCachedValidationByFingerprint_AndInvalidatesOnContentChange()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"bazaar_w5c_export_cache_{Guid.NewGuid():N}");
        string modsDir = Path.Combine(tempRoot, "mods");
        string stateDir = Path.Combine(tempRoot, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        try
        {
            string sampleDest = Path.Combine(modsDir, "bazaar-chronicles-sample");
            CopyDirectory(Path.Combine(Fx.ModsRoot, "bazaar-chronicles-sample"), sampleDest);

            var modHost = new ServerModHost(Fx.Repo, Fx.DataDir, stateDir, Fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            var validator = new WorkshopValidatorService(modHost);

            // 第 1 次导出：冷缓存未命中，执行完整 20 场校验并回填指纹缓存
            var exp1 = await validator.ExportZipWithReportAsync("bazaar-chronicles-sample", allowDraft: false);
            Assert.True(exp1.Ok, exp1.Error);
            Assert.Equal(200, exp1.StatusCode);
            Assert.NotNull(exp1.ZipBytes);
            Assert.True(exp1.ZipBytes!.Length > 1024);
            Assert.NotNull(exp1.Validation);
            Assert.True(exp1.Validation!.Passed);
            Assert.Equal(0, validator.ExportValidationCacheHits);
            Assert.Equal(1, validator.ExportValidationCacheMisses);

            // 第 2 次导出（同内容、同 modSetHash）：必须命中缓存，零重复战斗模拟，复用同一校验报告
            var exp2 = await validator.ExportZipWithReportAsync("bazaar-chronicles-sample", allowDraft: false);
            Assert.True(exp2.Ok, exp2.Error);
            Assert.Equal(200, exp2.StatusCode);
            Assert.NotNull(exp2.ZipBytes);
            Assert.Equal(1, validator.ExportValidationCacheHits);
            Assert.Equal(1, validator.ExportValidationCacheMisses);
            Assert.Same(exp1.Validation, exp2.Validation);

            // 验证导出的 zip 内仍包含有效 validation_stamp.json
            using (var ms = new MemoryStream(exp2.ZipBytes!))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var stamp = zip.GetEntry("validation_stamp.json");
                Assert.NotNull(stamp);
            }

            // 变更磁盘源码内容（修改 mod.json 简介 1 处字符）：指纹变化，缓存必须立即失效并重跑校验
            string modJsonPath = Path.Combine(sampleDest, "mod.json");
            string origJson = await File.ReadAllTextAsync(modJsonPath, Encoding.UTF8);
            Assert.Contains("星港奇物志", origJson);
            string mutatedJson = origJson.Replace("星港奇物志", "星港奇物志-缓存失效校验");
            await File.WriteAllTextAsync(modJsonPath, mutatedJson, new UTF8Encoding(false));

            var exp3 = await validator.ExportZipWithReportAsync("bazaar-chronicles-sample", allowDraft: false);
            Assert.True(exp3.Ok, exp3.Error);
            Assert.Equal(1, validator.ExportValidationCacheHits);
            Assert.Equal(2, validator.ExportValidationCacheMisses);
            Assert.NotSame(exp1.Validation, exp3.Validation);

            // 第 4 次导出（基于新内容无变动）：新指纹再次命中缓存
            var exp4 = await validator.ExportZipWithReportAsync("bazaar-chronicles-sample", allowDraft: false);
            Assert.True(exp4.Ok, exp4.Error);
            Assert.Equal(2, validator.ExportValidationCacheHits);
            Assert.Equal(2, validator.ExportValidationCacheMisses);
            Assert.Same(exp3.Validation, exp4.Validation);
        }
        finally
        {
            ModRuntimeHooks.ClearAll();
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 3. P1-18：ModValidator 共享只读底库 + 轻量 Overlay（零底库污染 & 并发安全）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P1_18_ModValidator_LightweightOverlay_SharesReadOnlyBaseTables_ZeroBasePollution_AndThreadSafe()
    {
        var baseRepo = Fx.Repo;
        int baseCardCount = baseRepo.CardsById.Count;
        int baseMonsterCount = baseRepo.MonstersById.Count;
        var whitelist = Fx.CreateValidCardIdsCopy();

        // 1) 无改动叠加直接复用原始只读底库引用（0 克隆）
        var identityRepo = ModValidator.BuildLightweightOverlayRepository(baseRepo, Array.Empty<CardTemplate>(), Array.Empty<CoreMonsterTemplate>());
        Assert.Same(baseRepo, identityRepo);

        // 2) 含新增 Mod 卡且无新增野怪：仅合并 CardsById，其余只读元表与 MonstersById 零拷贝共享底库，且底库零污染
        var cardA = CreateMinimalActiveModWeapon("w5c-overlay-a", "alpha_blade");
        var cardB = CreateMinimalActiveModWeapon("w5c-overlay-b", "beta_blade");
        var carrier = baseRepo.GetCard(KukriTemplateId)!;
        var tplA = cardA.ToCardTemplate(carrier, "w5c-overlay-a");

        var overlayA = ModValidator.BuildLightweightOverlayRepository(baseRepo, [tplA], Array.Empty<CoreMonsterTemplate>());
        Assert.NotSame(baseRepo, overlayA);
        Assert.NotNull(overlayA.GetCard(tplA.Id));
        Assert.Null(baseRepo.GetCard(tplA.Id));
        Assert.Equal(baseCardCount, baseRepo.CardsById.Count);
        Assert.Equal(baseCardCount + 1, overlayA.CardsById.Count);
        Assert.Same(baseRepo.MonstersById, overlayA.MonstersById);
        Assert.Same(baseRepo.LevelUps, overlayA.LevelUps);
        Assert.Same(baseRepo.CollectiblesById, overlayA.CollectiblesById);
        Assert.Same(baseRepo.ChallengesById, overlayA.ChallengesById);
        Assert.Same(baseRepo.TooltipSymbolsById, overlayA.TooltipSymbolsById);

        // 3) 并发运行三份不同 Mod 的完整 10 道铁闸校验（含 Gate #9 战斗与 Gate #10 沙盒共享 Overlay），验证互不串扰且底库零污染
        var cardC = CreateMinimalActiveModWeapon("w5c-overlay-c", "gamma_blade");
        var pkgA = WrapSingleCard("w5c-overlay-a", cardA);
        var pkgB = WrapSingleCard("w5c-overlay-b", cardB);
        var pkgC = WrapSingleCard("w5c-overlay-c", cardC);
        var tasks = new[]
        {
            Task.Run(() => ModValidator.RunTenGateValidation(pkgA, Array.Empty<ModPackage>(), baseRepo, whitelist, combatMatches: 5, combatSeed: 2026501, runDynamicGates: true)),
            Task.Run(() => ModValidator.RunTenGateValidation(pkgB, Array.Empty<ModPackage>(), baseRepo, whitelist, combatMatches: 5, combatSeed: 2026502, runDynamicGates: true)),
            Task.Run(() => ModValidator.RunTenGateValidation(pkgC, Array.Empty<ModPackage>(), baseRepo, whitelist, combatMatches: 5, combatSeed: 2026503, runDynamicGates: true))
        };

        var reports = await Task.WhenAll(tasks);
        foreach (var rep in reports)
        {
            Assert.True(rep.Passed, rep.SummaryZh + " | " + string.Join("; ", rep.Gates.SelectMany(g => g.Issues).Select(i => $"{i.Code}:{i.Message}")));
            Assert.Equal(0, rep.ErrorCount);
            Assert.Equal("PASS", rep.Gates[9].Status); // Gate #10 回滚零残留与共享 Overlay 验证通过
        }

        Assert.Equal(baseCardCount, baseRepo.CardsById.Count);
        Assert.Equal(baseMonsterCount, baseRepo.MonstersById.Count);
        Assert.Null(baseRepo.GetCard(cardA.CustomId));
        Assert.Null(baseRepo.GetCard(cardB.CustomId));
        Assert.Null(baseRepo.GetCard(cardC.CustomId));
    }

    // ════════════════════════════════════════════════════════════════════════
    // 4. P1-16：未手写 enchantments 的 Mod 物品自动合成标准附魔集 + 兼容原版附魔台 + 真实战中生效
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_16_CustomModItems_AutoSynthesizeStandardEnchantments_AndAreAcceptedByVanillaEnchantPedestals()
    {
        var (registry, modRepo) = LoadIsolatedSampleMod("bazaar-chronicles-sample");

        var bladeTpl = modRepo.GetCard("bazaar-chronicles-sample:starport_compass_blade");
        var furnaceTpl = modRepo.GetCard("bazaar-chronicles-sample:solar_kiln_furnace");
        var charterTpl = modRepo.GetCard("bazaar-chronicles-sample:starport_trade_charter");
        Assert.NotNull(bladeTpl);
        Assert.NotNull(furnaceTpl);
        Assert.NotNull(charterTpl);

        // 1) 未手写 enchantments 的自定义物品自动合成全套 13 种标准附魔；技能卡不合成附魔
        Assert.NotNull(bladeTpl!.Enchantments);
        Assert.NotNull(furnaceTpl!.Enchantments);
        Assert.Null(charterTpl!.Enchantments);
        foreach (string stdEnch in ModDefaultEnchantmentSynthesizer.StandardEnchantmentNames)
        {
            Assert.True(bladeTpl.Enchantments!.ContainsKey(stdEnch), $"星港罗盘短刃应自动兼容标准附魔 {stdEnch}");
            Assert.True(furnaceTpl.Enchantments!.ContainsKey(stdEnch), $"日耀熔炉应自动兼容标准附魔 {stdEnch}");
        }

        // 2) ValueAndConditionEvaluator.EvaluateCardCondition 对 TCardConditionalEnchantmentEligible 精准返回 true / IsNot 取反
        var evalPlayer = new PlayerState("p1", "Eval", HeroNames.Dooley, initialHealthMax: 1000);
        var evalCtx = new RuleExecutionContext(modRepo, RuleScope.OutOfCombat, evalPlayer, null, new Random(516));
        var bladeInst = new CardInstance(bladeTpl, CardTier.Bronze, instanceId: "itm_eval_blade");
        var furnaceInst = new CardInstance(furnaceTpl, CardTier.Bronze, instanceId: "itm_eval_furnace");

        Assert.True(ValueAndConditionEvaluator.EvaluateCardCondition(
            new RuleNode { Type = "TCardConditionalEnchantmentEligible", Enchantment = "Toxic" },
            bladeInst, evalPlayer, evalCtx));
        Assert.False(ValueAndConditionEvaluator.EvaluateCardCondition(
            new RuleNode { Type = "TCardConditionalEnchantmentEligible", Enchantment = "Toxic", IsNot = true },
            bladeInst, evalPlayer, evalCtx));
        Assert.True(ValueAndConditionEvaluator.EvaluateCardCondition(
            new RuleNode { Type = "TCardConditionalEnchantmentEligible", Enchantment = "Mossy" },
            furnaceInst, evalPlayer, evalCtx));

        // 3) 端到端通过真实 RunEngine 提交至原版 Toxic 附魔祭坛（Murkwood Bayou）与 Mossy 附魔祭坛
        CardTemplate FindFixedEnchantPedestal(string enchName) => modRepo.CardsById.Values.First(c =>
            string.Equals(c.Type, "PedestalEncounter", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Behavior?.Type, "TPedestalBehaviorEnchant", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Behavior?.Enchantment, enchName, StringComparison.OrdinalIgnoreCase));

        var toxicPed = FindFixedEnchantPedestal("Toxic");
        var mossyPed = FindFixedEnchantPedestal("Mossy");

        var pool = CardPool.FromRepository(modRepo, Fx.DataDir, Fx.CreateValidCardIdsCopy(), registry.VisualBindingsByCustomId);
        var monsters = MonsterPool.FromRepository(modRepo);

        RunState.CardEntry CommitModItemToPedestal(CardTemplate itemTpl, string baseCarrierId, CardTemplate pedTpl, out RunEngine.Outcome outcome)
        {
            var run = new RunState
            {
                RunId = "w5c_ench_" + Guid.NewGuid().ToString("N")[..8],
                GameModeId = pool.DefaultGameModeId,
                Hero = EHero.Dooley,
                PlayMode = EPlayMode.Unranked,
                RngSeed = 2026516
            };
            var engine = new RunEngine(run, pool, monsters, Fx.LevelUps, Fx.Mode, modRepo, Fx.Ghosts);
            engine.StartRun();
            engine.Handle(new CheatCommand { Args = ["skipstart"] });
            engine.Handle(new CheatCommand { Args = ["slots"] });

            string instId = "itm_mod_" + Guid.NewGuid().ToString("N")[..6];
            var coreCard = new CardInstance(itemTpl, CardTier.Bronze, null, instId);
            var entry = new RunState.CardEntry
            {
                InstanceId = instId,
                TemplateId = Guid.Parse(baseCarrierId),
                CustomCardId = itemTpl.Id,
                Type = ECardType.Item,
                Size = ShopEngine.ParseSize(itemTpl.Size),
                Tier = ETier.Bronze,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_2,
                CoreCard = coreCard
            };
            RunState.PopulateMetaFromTemplate(entry, pool.Get(itemTpl.Id) ?? pool.Get(baseCarrierId), itemTpl);
            run.Cards[entry.InstanceId] = entry;
            run.HandGrid.TryPlaceWithDisplaceOrSwap(coreCard, 2);

            run.State = ERunState.Pedestal;
            run.CurrentEncounterId = pedTpl.Id;
            outcome = engine.Handle(new CommitToPedestalCommand { InstanceId = new InstanceId(entry.InstanceId) });
            return entry;
        }

        var toxicEntry = CommitModItemToPedestal(bladeTpl, KukriTemplateId, toxicPed, out var toxicOut);
        Assert.Null(toxicOut.Error);
        Assert.Equal(EEnchantmentType.Toxic, toxicEntry.Enchantment);
        Assert.Contains(toxicOut.Events, e => e is GameSimEventCardEnchanted ev && ev.InstanceId == toxicEntry.InstanceId && ev.EnchantmentType == EEnchantmentType.Toxic);

        var mossyEntry = CommitModItemToPedestal(furnaceTpl, WeldingHelmetTemplateId, mossyPed, out var mossyOut);
        Assert.Null(mossyOut.Error);
        Assert.Equal(EEnchantmentType.Mossy, mossyEntry.Enchantment);
        Assert.Contains(mossyOut.Events, e => e is GameSimEventCardEnchanted ev && ev.InstanceId == mossyEntry.InstanceId && ev.EnchantmentType == EEnchantmentType.Mossy);

        // 4) 实战核验：附魔了 Toxic 的星港罗盘短刃与附魔了 Mossy 的日耀熔炉在真实 CombatSimulator 中触发剧毒与再生
        var p1 = new PlayerState("p1", "附魔实战", HeroNames.Dooley, initialHealthMax: 2000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "标靶", HeroNames.Vanessa, initialHealthMax: 2000, unlockedHandSlots: 10);
        var toxicBlade = new CardInstance(bladeTpl, CardTier.Bronze, enchantment: "Toxic", instanceId: "combat_toxic_blade");
        var mossyFurnace = new CardInstance(furnaceTpl, CardTier.Bronze, enchantment: "Mossy", instanceId: "combat_mossy_furnace");
        Assert.True(p1.Hand.TryPlaceOrShift(toxicBlade, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(mossyFurnace, 1));

        var sim = CombatSimulator.Simulate(
            modRepo, p1, p2, seed: 2026516, combatType: "PVP",
            recordTimeline: false, recordEvents: true, syncPermanentChanges: false,
            modifiers: new CombatModifiers { MaxDurationMsOverride = 8_000, SandstormEnabled = false });

        Assert.Contains(sim.Events, e => e.Type == CombatEventType.PoisonApplied && e.SourceCardInstanceId == toxicBlade.InstanceId && e.Value >= 1);
        Assert.Contains(sim.Events, e => e.Type == CombatEventType.RegenApplied && e.SourceCardInstanceId == mossyFurnace.InstanceId && e.Value >= 1);
    }

    [Fact]
    public void P1_16_CustomModItems_SupportDisableAutoEnchantments_AndExplicitExclusionsAndOverrides()
    {
        var carrier = Fx.Repo.GetCard(KukriTemplateId)!;

        // 场景 A：disableAutoEnchantments = true -> 不自动合成任何附魔，附魔条件返回 false
        var disabledCard = CreateMinimalActiveModWeapon("w5c-ench-opt", "no_ench_blade");
        disabledCard.DisableAutoEnchantments = true;
        var disabledTpl = disabledCard.ToCardTemplate(carrier, "w5c-ench-opt");
        Assert.Null(disabledTpl.Enchantments);

        var p = new PlayerState("p1", "Test", HeroNames.Vanessa, initialHealthMax: 1000);
        var ctx = new RuleExecutionContext(Fx.Repo, RuleScope.OutOfCombat, p, null, new Random(1));
        Assert.False(ValueAndConditionEvaluator.EvaluateCardCondition(
            new RuleNode { Type = "TCardConditionalEnchantmentEligible", Enchantment = "Toxic" },
            new CardInstance(disabledTpl, CardTier.Bronze, instanceId: "no_ench_inst"), p, ctx));

        // 场景 B：disabledEnchantments = ["Toxic", "Icy"] -> 排除指定附魔，其余标准附魔保留
        var partialExcludeCard = CreateMinimalActiveModWeapon("w5c-ench-opt", "partial_blade");
        partialExcludeCard.DisabledEnchantments = ["Toxic", "Icy"];
        var partialTpl = partialExcludeCard.ToCardTemplate(carrier, "w5c-ench-opt");
        Assert.NotNull(partialTpl.Enchantments);
        Assert.False(partialTpl.Enchantments!.ContainsKey("Toxic"));
        Assert.False(partialTpl.Enchantments.ContainsKey("Icy"));
        Assert.True(partialTpl.Enchantments.ContainsKey("Fiery"));
        Assert.True(partialTpl.Enchantments.ContainsKey("Obsidian"));
        Assert.True(partialTpl.Enchantments.ContainsKey("Mossy"));
        Assert.Equal(ModDefaultEnchantmentSynthesizer.StandardEnchantmentNames.Count - 2, partialTpl.Enchantments.Count);

        // 场景 C：作者显式配置特定附魔（Fiery）-> 作者配置优先保留，未手写的标准附魔自动补齐
        var customFiery = new EnchantmentData
        {
            Attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["BurnApplyAmount"] = 77.0 },
            HasAbilities = false,
            HasAuras = false
        };
        var overrideCard = CreateMinimalActiveModWeapon("w5c-ench-opt", "override_blade");
        overrideCard.Enchantments = new Dictionary<string, EnchantmentData>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fiery"] = customFiery
        };
        var overrideTpl = overrideCard.ToCardTemplate(carrier, "w5c-ench-opt");
        Assert.NotNull(overrideTpl.Enchantments);
        Assert.Same(customFiery, overrideTpl.Enchantments!["Fiery"]);
        Assert.Equal(77.0, overrideTpl.Enchantments["Fiery"].Attributes["BurnApplyAmount"]);
        Assert.True(overrideTpl.Enchantments.ContainsKey("Toxic"));
        Assert.Equal(ModDefaultEnchantmentSynthesizer.StandardEnchantmentNames.Count, overrideTpl.Enchantments.Count);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 5. P1-17：星港贸易特许状（及无畏宝库舰）银/金/钻阶光环增益严格等比递增
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_17_StarportTradeCharter_AndDreadnoughtVault_AuraScalingStrictlyIncreasesAcrossTiers()
    {
        var (_, modRepo) = LoadIsolatedSampleMod("bazaar-chronicles-sample");
        var charterTpl = modRepo.GetCard("bazaar-chronicles-sample:starport_trade_charter");
        var bladeTpl = modRepo.GetCard("bazaar-chronicles-sample:starport_compass_blade");
        var vaultTpl = modRepo.GetCard("bazaar-chronicles-sample:starport_dreadnought_vault");
        Assert.NotNull(charterTpl);
        Assert.NotNull(bladeTpl);
        Assert.NotNull(vaultTpl);

        // A. 星港贸易特许状：Gold=30，基础青铜短刃 12 伤；
        //    Silver(Custom_0=3) -> +9 (21)；Gold(Custom_0=6) -> +18 (30)；Diamond(Custom_0=12) -> +36 (48)
        double MeasureBladeDamageWithCharterTier(CardTier charterTier)
        {
            var p1 = new PlayerState("p1", "商会", HeroNames.Vanessa, initialHealthMax: 1000, initialGold: 30, unlockedHandSlots: 10);
            var p2 = new PlayerState("p2", "木桩", HeroNames.Dooley, initialHealthMax: 1000, unlockedHandSlots: 10);
            var blade = new CardInstance(bladeTpl!, CardTier.Bronze, instanceId: $"blade_{charterTier}");
            var charter = new CardInstance(charterTpl!, charterTier, instanceId: $"charter_{charterTier}");
            Assert.True(p1.Hand.TryPlaceOrShift(blade, 0));
            p1.Skills.Add(charter);

            AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, day: 1, hour: 1, modRepo);
            return blade.GetAttribute("DamageAmount");
        }

        double silverDmg = MeasureBladeDamageWithCharterTier(CardTier.Silver);
        double goldDmg = MeasureBladeDamageWithCharterTier(CardTier.Gold);
        double diamondDmg = MeasureBladeDamageWithCharterTier(CardTier.Diamond);

        Assert.Equal(21.0, silverDmg);
        Assert.Equal(30.0, goldDmg);
        Assert.Equal(48.0, diamondDmg);
        Assert.True(silverDmg < goldDmg && goldDmg < diamondDmg, $"特许状三阶武器伤害应严格递增：Silver={silverDmg}, Gold={goldDmg}, Diamond={diamondDmg}");

        // B. 星港无畏宝库舰：Gold=20；
        //    Gold(基础80, Custom_0=15) -> +30 = 110；Diamond(基础160, Custom_0=30) -> +60 = 220
        (double Dmg, double Shield) MeasureVaultAtTier(CardTier vaultTier)
        {
            var p1 = new PlayerState("p1", "舰长", HeroNames.Vanessa, initialHealthMax: 1000, initialGold: 20, unlockedHandSlots: 10);
            var p2 = new PlayerState("p2", "靶舰", HeroNames.Dooley, initialHealthMax: 1000, unlockedHandSlots: 10);
            var vault = new CardInstance(vaultTpl!, vaultTier, instanceId: $"vault_{vaultTier}");
            Assert.True(p1.Hand.TryPlaceOrShift(vault, 0));

            AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, day: 1, hour: 1, modRepo);
            return (vault.GetAttribute("DamageAmount"), vault.GetAttribute("ShieldApplyAmount"));
        }

        var vaultGold = MeasureVaultAtTier(CardTier.Gold);
        var vaultDiamond = MeasureVaultAtTier(CardTier.Diamond);
        Assert.Equal(110.0, vaultGold.Dmg);
        Assert.Equal(110.0, vaultGold.Shield);
        Assert.Equal(220.0, vaultDiamond.Dmg);
        Assert.Equal(220.0, vaultDiamond.Shield);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 6. P1-17 + P1-07：ModValidator 孤立 Custom_N 警告 + 非官方属性键警告
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P1_17_ModValidator_WarnsOnOrphanScalingCustomAttribute_AndPassesWhenReferenced()
    {
        var whitelist = Fx.CreateValidCardIdsCopy();

        CustomCardDefinition BuildCharterLikeSkill(string modId, bool useHardcodedMult) => new()
        {
            CustomId = $"{modId}:charter_check",
            BaseVisualTemplateId = BackroomDealingsId,
            Type = "Skill",
            Size = "Medium",
            StartingTier = "Silver",
            SpawningEligibility = "Always",
            Heroes = [HeroNames.Vanessa],
            Tags = ["Merchant"],
            TitleZh = "缩放体检测试技能",
            DescZh = "用于验证孤立 Custom_N 静态体检。",
            PassiveTooltipsZh = ["每 10 金币使所有武器获得 +{attr.Custom_0} 伤害"],
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Silver"] = new() { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 10, ["SellPrice"] = 5, ["Custom_0"] = 3 }, AuraIds = ["0"] },
                ["Gold"] = new() { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 20, ["SellPrice"] = 10, ["Custom_0"] = 6 }, AuraIds = ["0"] },
                ["Diamond"] = new() { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 40, ["SellPrice"] = 20, ["Custom_0"] = 12 }, AuraIds = ["0"] }
            },
            Auras = new Dictionary<string, AuraDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Action = ParseRule(useHardcodedMult
                        ? """
                          {
                            "$type": "TAuraActionCardModifyAttribute",
                            "attributeType": "DamageAmount",
                            "operation": "Add",
                            "value": {
                              "$type": "TReferenceValuePlayerAttribute",
                              "attributeType": "Gold",
                              "target": { "$type": "TTargetPlayerRelative", "targetMode": "Self" },
                              "modifier": {
                                "modifyMode": "Multiply",
                                "value": { "$type": "TFixedValue", "value": 0.3 }
                              }
                            },
                            "target": { "$type": "TTargetCardSection", "targetSection": "SelfHand" }
                          }
                          """
                        : """
                          {
                            "$type": "TAuraActionCardModifyAttribute",
                            "attributeType": "DamageAmount",
                            "operation": "Add",
                            "value": {
                              "$type": "TReferenceValuePlayerAttribute",
                              "attributeType": "Gold",
                              "target": { "$type": "TTargetPlayerRelative", "targetMode": "Self" },
                              "modifier": {
                                "modifyMode": "Multiply",
                                "value": {
                                  "$type": "TReferenceValueCardAttribute",
                                  "attributeType": "Custom_0",
                                  "target": { "$type": "TTargetCardSelf" },
                                  "modifier": {
                                    "modifyMode": "Multiply",
                                    "shouldRound": false,
                                    "value": { "$type": "TFixedValue", "value": 0.1 }
                                  }
                                }
                              }
                            },
                            "target": { "$type": "TTargetCardSection", "targetSection": "SelfHand" }
                          }
                          """)
                }
            }
        };

        // 负例：Custom_0 跨三阶递增（3/6/12）但光环硬编码 0.3 未引用 -> Warning（不阻断 Passed）
        var badRep = ModValidator.RunTenGateValidation(
            WrapSingleCard("w5c-orphan-bad", BuildCharterLikeSkill("w5c-orphan-bad", useHardcodedMult: true)),
            Array.Empty<ModPackage>(), Fx.Repo, whitelist, runDynamicGates: false);
        Assert.True(badRep.Passed);
        Assert.Equal(0, badRep.ErrorCount);
        Assert.Contains(badRep.Gates[3].Issues, i =>
            i.Severity == ModValidationSeverity.Warning
            && i.Code == "ORPHAN_SCALING_CUSTOM_ATTRIBUTE"
            && i.Message.Contains("Custom_0", StringComparison.OrdinalIgnoreCase));

        // 正例：光环通过 TReferenceValueCardAttribute(Custom_0) 引用 -> 0 条孤立告警且 Gate #4 PASS
        var goodRep = ModValidator.RunTenGateValidation(
            WrapSingleCard("w5c-orphan-good", BuildCharterLikeSkill("w5c-orphan-good", useHardcodedMult: false)),
            Array.Empty<ModPackage>(), Fx.Repo, whitelist, runDynamicGates: false);
        Assert.True(goodRep.Passed);
        Assert.Equal(0, goodRep.ErrorCount);
        Assert.DoesNotContain(goodRep.Gates[3].Issues, i => i.Code == "ORPHAN_SCALING_CUSTOM_ATTRIBUTE");
        Assert.Equal("PASS", goodRep.Gates[3].Status);
    }

    [Fact]
    public void P1_07_ModValidator_WarnsOnNonOfficialCardAttributeKey_WithoutBreakingGatePass()
    {
        var whitelist = Fx.CreateValidCardIdsCopy();

        // 负例：自定义卡包含非官方 ECardAttributeType 且非 Custom_N 的属性键（如 GoldGainAmount、CustomNonWireStat）
        var cardWithNonOfficial = CreateMinimalActiveModWeapon("w5c-non-official", "non_wire_blade");
        cardWithNonOfficial.Tiers!["Bronze"].Attributes["GoldGainAmount"] = 1;
        cardWithNonOfficial.Tiers["Silver"].Attributes["GoldGainAmount"] = 2;
        cardWithNonOfficial.Tiers["Bronze"].Attributes["CustomNonWireStat"] = 5;

        var rep = ModValidator.RunTenGateValidation(
            WrapSingleCard("w5c-non-official", cardWithNonOfficial),
            Array.Empty<ModPackage>(), Fx.Repo, whitelist, runDynamicGates: false);

        // 必须按 Warning 告警，绝不能升级为 Error 破坏 10 闸门通过性
        Assert.True(rep.Passed);
        Assert.Equal(0, rep.ErrorCount);
        var nonOfficialWarnings = rep.Gates[3].Issues
            .Where(i => i.Severity == ModValidationSeverity.Warning && i.Code == "NON_OFFICIAL_CARD_ATTRIBUTE_KEY")
            .ToList();
        Assert.Equal(2, nonOfficialWarnings.Count); // 同卡多阶同键去重，仅报 GoldGainAmount 与 CustomNonWireStat 各 1 条
        Assert.Contains(nonOfficialWarnings, i => i.Message.Contains("GoldGainAmount", StringComparison.Ordinal));
        Assert.Contains(nonOfficialWarnings, i => i.Message.Contains("CustomNonWireStat", StringComparison.Ordinal));

        // 辅助方法核验
        Assert.True(ModValidator.IsOfficialOrCustomCardAttributeKey("DamageAmount"));
        Assert.True(ModValidator.IsOfficialOrCustomCardAttributeKey("Custom_0"));
        Assert.True(ModValidator.IsOfficialOrCustomCardAttributeKey("Custom_9"));
        Assert.False(ModValidator.IsOfficialOrCustomCardAttributeKey("GoldGainAmount"));

        // 正例：仓库内置黄金样例包 bazaar-chronicles-sample 0 Error & 0 Warning（1~8 闸门全 PASS）
        var scanIssues = new List<ModValidationIssue>();
        var samplePkg = ModLoader.LoadSinglePackage(Path.Combine(Fx.ModsRoot, "bazaar-chronicles-sample"), "bazaar-chronicles-sample", scanIssues);
        Assert.NotNull(samplePkg);
        var sampleRep = ModValidator.RunTenGateValidation(samplePkg!, Array.Empty<ModPackage>(), Fx.Repo, whitelist, runDynamicGates: false);
        Assert.True(sampleRep.Passed);
        Assert.Equal(0, sampleRep.ErrorCount);
        Assert.All(sampleRep.Gates.Where(g => g.GateId <= 8), g =>
            Assert.True(g.Status == "PASS", $"Gate #{g.GateId}={g.Status}: {string.Join(" | ", g.Issues.Select(i => $"{i.Code}:{i.Message}"))}"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // 7. P3-08：服务端过滤 SpawningEligibility=="Never" 且空 ArtKey 官方调试技能
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P3_08_ServerFiltersNeverAndBlankArtKeyDebugSkills_InMonsterPoolAndBossRushAndCombat()
    {
        // 1) 从真实 GameData.db 定位全部官方 Never + 空 ArtKey 调试技能（至少 5 张）
        var blankDebugSkills = Fx.Repo.CardsById.Values
            .Where(c => string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(c.ArtKey))
            .ToList();
        Assert.True(blankDebugSkills.Count >= 5, $"预期官方库含至少 5 张 Never+空 ArtKey 技能，实际={blankDebugSkills.Count}");
        Assert.All(blankDebugSkills, sk => Assert.True(MonsterPool.IsBlockedDebugOrBlankArtSkill(sk)));

        var normalSkill = Fx.Repo.AlwaysSpawnableSkills.First(s => !string.IsNullOrWhiteSpace(s.ArtKey));
        Assert.False(MonsterPool.IsBlockedDebugOrBlankArtSkill(normalSkill));

        // 显式挂载的普通合成技能（即使未填 ArtKey 且 SpawningEligibility=="Never"）不得被误杀
        var syntheticRegularSkill = new CardTemplate
        {
            Id = "77777777-7777-4777-8777-777777777799",
            Type = "Skill",
            InternalName = "Synthetic Test Shield Skill",
            SpawningEligibility = "Never",
            ArtKey = null
        };
        Assert.False(MonsterPool.IsBlockedDebugOrBlankArtSkill(syntheticRegularSkill));

        // 2) MonsterPool.FromRepository 映射野怪技能时自动过滤 Never + 空 ArtKey 调试技能，保留合法技能
        var debugSkill = blankDebugSkills[0];
        var pollutedMonster = new CoreMonsterTemplate
        {
            Id = "w5c_test_void_golem_polluted",
            InternalName = "W5C Polluted Void Golem",
            Player = new MonsterPlayerData
            {
                Attributes = new Dictionary<string, double> { ["HealthMax"] = 1200, ["Level"] = 11, ["Prestige"] = 20 },
                Hand = new MonsterHandData
                {
                    UnlockedSlots = 1023,
                    Items = [new MonsterCardEntry { SocketId = "Socket_0", TemplateId = KukriTemplateId, Tier = "Gold" }]
                },
                Skills =
                [
                    new MonsterCardEntry { TemplateId = debugSkill.Id, Tier = "Diamond" },
                    new MonsterCardEntry { TemplateId = normalSkill.Id, Tier = "Diamond" }
                ]
            }
        };

        var overlayWithMonster = ModValidator.BuildLightweightOverlayRepository(Fx.Repo, Array.Empty<CardTemplate>(), [pollutedMonster]);
        var mappedMonsters = MonsterPool.FromRepository(overlayWithMonster);
        var mappedTarget = Assert.Single(mappedMonsters, m => m.Id == pollutedMonster.Id);
        Assert.DoesNotContain(mappedTarget.Skills, s => string.Equals(s.TemplateId, debugSkill.Id, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mappedTarget.Skills, s => string.Equals(s.TemplateId, normalSkill.Id, StringComparison.OrdinalIgnoreCase));

        // 3) RunEngine.ApplyBossRushWaveReinforcements 在多轮高波次抽技中 0 次选中 Never / 空 ArtKey / [DEBUG] 技能
        var run = new RunState
        {
            RunId = "w5c_bossrush_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = Fx.Pool.DefaultGameModeId,
            Hero = EHero.Karnok,
            PlayMode = EPlayMode.Unranked,
            RngSeed = 2026308,
            CustomRules = PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.BossRush)
        };
        var engine = new RunEngine(run, Fx.CreateIsolatedCardPool(), Fx.CreateMonstersCopy(), Fx.LevelUps, Fx.Mode, Fx.Repo, Fx.Ghosts);
        var applyReinforce = typeof(RunEngine).GetMethod("ApplyBossRushWaveReinforcements", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyReinforce);

        var highWaveCfg = BossRushCatalog.GetWave(15);
        Assert.True(highWaveCfg.BonusReinforceSkills > 0);
        for (int trial = 0; trial < 12; trial++)
        {
            var bossPlayer = new PlayerState("boss", "Void Golem", HeroNames.Common, initialHealthMax: 3000) { IsMonster = true };
            applyReinforce!.Invoke(engine, [bossPlayer, highWaveCfg, 2026308 + trial * 97]);
            Assert.True(bossPlayer.Skills.Count >= highWaveCfg.BonusReinforceSkills);
            Assert.All(bossPlayer.Skills, sk =>
            {
                Assert.False(MonsterPool.IsBlockedDebugOrBlankArtSkill(sk.Template),
                    $"BossRush 强化抽中了应过滤的空 ArtKey 调试技能：{sk.Template.Id} ({sk.Template.InternalName})");
                Assert.False(string.IsNullOrWhiteSpace(sk.Template.ArtKey),
                    $"BossRush 强化技能 ArtKey 不得为空：{sk.Template.Id}");
                Assert.NotEqual("Never", sk.Template.SpawningEligibility, StringComparer.OrdinalIgnoreCase);
            });
        }
    }
}
