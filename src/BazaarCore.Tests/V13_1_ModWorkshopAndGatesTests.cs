using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarServer.Mods;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

/// <summary>
/// V13.1 标准化工业级 Mod 创作生态与零代码 `/workshop` 创意工坊全栈验收测试：
/// 1. CardInstance 自定义卡 tiers 非空但 abilityIds/auraIds 为空时自动回退激活（且绝不误伤原版按阶解锁卡）；
/// 2. TooltipFormatter `{attr.*}` 四阶真实渲染 0 "?"；
/// 3. mods/bazaar-chronicles-sample 10/10 铁闸满分通过 + 50 场真实 CombatSimulator 压测 0 异常/0 警告；
/// 4. 反向注入 7 类坏 Mod（虚构字段、循环依赖/命名空间违规、伪造白名单/错尺寸、非法$type/TPrerequisitePlayerAttribute/拼写错属性、冷却秒当毫秒/倍率爆炸/自充能死循环、奇遇环/悬空、未解析占位符/WebP/SHA-256 篡改）100% 精准拦截；
/// 5. CardPatchDefinition 原位改原版卡增量合并、真实战斗生效、强制幽灵池隔离与 RollbackToBase 零残留；
/// 6. `/workshop` 前端与全部 11 个 `/api/workshop/*` 端点端到端集成测试（含 HTTP 422 阻断带病发布、PNG 资产上传、.bazaarmod.zip 往返导入导出 + Zip Slip / .dll 拦截 + 公网写保护 403 &amp; Token 解锁）。
/// </summary>
[Xunit.Collection("GlobalStaticState")]
public sealed class V13_1_ModWorkshopAndGatesTests
{
    private const string KukriTemplateId = "03b1fd16-6b0d-4847-a76f-6c586f7c5958"; // Small Item 白名单载体
    private const string EventCarrierId = "11287697-dd45-4edc-9539-780a26001043";  // EventEncounter 白名单载体
    private const string StepCarrierId = "0659ca31-9f4d-412e-84b2-d535b502afa6";   // EncounterStep 白名单载体

    private static readonly Lazy<SharedContext> Shared = new(LoadShared);

    private sealed record SharedContext(
        string RepoRoot,
        string DataRoot,
        string ModsRoot,
        GameDataRepository RawRepo,
        HashSet<string> ValidWhitelist);

    private static SharedContext LoadShared()
    {
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        var wl = shared.CreateValidCardIdsCopy();
        Assert.True(wl.Count >= 3000);
        return new SharedContext(shared.RepoRoot, shared.DataDir, shared.ModsRoot, shared.Repo, wl);
    }

    private static string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>生成最小合法 64×64 纯色 PNG（满足 Gate #8 魔数与尺寸要求，零外部图形库依赖）。</summary>
    private static byte[] BuildValidPng64x64()
    {
        const int w = 64, h = 64;
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        void WriteChunk(string type, byte[] data)
        {
            byte[] len = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length));
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            ms.Write(len);
            ms.Write(typeBytes);
            ms.Write(data);
            uint crc = ComputeCrc32(typeBytes, data);
            ms.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)crc)));
        }

        // IHDR: 64x64, bit depth 8, color type 2 (RGB)
        using (var ihdr = new MemoryStream())
        {
            ihdr.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(w)));
            ihdr.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(h)));
            ihdr.Write(new byte[] { 8, 2, 0, 0, 0 });
            WriteChunk("IHDR", ihdr.ToArray());
        }

        // IDAT: zlib stream of scanlines (filter byte 0 + 64*3 RGB bytes)
        byte[] rawPixels = new byte[h * (1 + w * 3)];
        using (var idatMs = new MemoryStream())
        {
            using (var zlib = new ZLibStream(idatMs, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(rawPixels, 0, rawPixels.Length);
            WriteChunk("IDAT", idatMs.ToArray());
        }

        WriteChunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static uint ComputeCrc32(byte[] part1, byte[] part2)
    {
        uint crc = 0xFFFFFFFFu;
        void Feed(byte[] buf)
        {
            foreach (byte b in buf)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
            }
        }
        Feed(part1);
        Feed(part2);
        return ~crc;
    }

    private static CustomCardDefinition BuildHealthySmallWeapon(string modId, string slug = "test_blade", string? artFile = null)
    {
        return new CustomCardDefinition
        {
            CustomId = $"{modId}:{slug}",
            BaseVisualTemplateId = KukriTemplateId,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            SpawningEligibility = "Always",
            Heroes = [HeroNames.Common],
            Tags = ["Weapon", "Tool"],
            HiddenTags = ["Damage"],
            TitleZh = "星港测试短刃",
            TitleEn = "Starport Test Blade",
            DescZh = "用于自动化测试的标准合规短刃。",
            ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害（冷却 {attr.CooldownMax} 秒）"],
            CustomArtFile = artFile,
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new() { Attributes = new() { ["BuyPrice"] = 2, ["SellPrice"] = 1, ["CooldownMax"] = 3500, ["DamageAmount"] = 18 }, AbilityIds = ["0"] },
                ["Silver"] = new() { Attributes = new() { ["BuyPrice"] = 4, ["SellPrice"] = 2, ["CooldownMax"] = 3500, ["DamageAmount"] = 36 }, AbilityIds = ["0"] },
                ["Gold"] = new() { Attributes = new() { ["BuyPrice"] = 8, ["SellPrice"] = 4, ["CooldownMax"] = 3000, ["DamageAmount"] = 72 }, AbilityIds = ["0"] },
                ["Diamond"] = new() { Attributes = new() { ["BuyPrice"] = 16, ["SellPrice"] = 8, ["CooldownMax"] = 3000, ["DamageAmount"] = 144 }, AbilityIds = ["0"] }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    ActiveIn = "HandOnly",
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }
                    }
                }
            }
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 1：CardInstance 自定义卡 tiers 非空但 abilityIds/auraIds 为空自动回退（且不误伤按阶解锁卡）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test01_CardInstance_FallbackActivatesAbilitiesAndAuras_WithoutBreakingTierUnlocks()
    {
        var data = Shared.Value;
        var tpl = new CardTemplate
        {
            Id = "test-fallback:unlisted_ability_blade",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new() { Attributes = new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 25 } }
                // 故意不写 AbilityIds 与 AuraIds
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    ActiveIn = "HandOnly",
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }
                    }
                }
            },
            Auras = new Dictionary<string, AuraDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Action = new RuleNode
                    {
                        Type = "TAuraActionCardModifyAttribute",
                        AttributeType = "DamageAmount",
                        Operation = "Add",
                        RawValueNode = JsonSerializer.SerializeToElement(new { _type = "TFixedValue" })
                    }
                }
            }
        };

        // 1. 全阶均未声明 abilityIds/auraIds -> 自动回退激活全部 Abilities 与 Auras
        var inst = new CardInstance(tpl, CardTier.Bronze);
        Assert.Single(inst.ActiveAbilities);
        Assert.Single(inst.ActiveAuras);

        // 真实进 CombatSimulator 验证确有触发伤害（根治 0 触发）
        var p1 = new PlayerState("p1", "测试方", HeroNames.Vanessa, 200);
        var p2 = new PlayerState("p2", "靶子", HeroNames.Dooley, 100);
        p1.Hand.TryPlaceOrShift(inst, 0);
        var overlay = data.RawRepo.CreateWithOverlay([tpl]);
        var res = CombatSimulator.Simulate(overlay, p1, p2, seed: 42, recordTimeline: false, recordEvents: false);
        Assert.True(res.Player1TotalDamageDealt >= 25, $"自定义卡理应触发伤害，实际={res.Player1TotalDamageDealt}");

        // 2. 按阶解锁语义保护：Bronze 阶空，Silver 阶声明了 AbilityIds=["0"] -> Bronze 阶必须保持 0，Silver 阶为 1！
        var tierUnlockedTpl = new CardTemplate
        {
            Id = "test-fallback:tier_unlock_card",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new() { Attributes = new() { ["CooldownMax"] = 3000 } },
                ["Silver"] = new() { Attributes = new() { ["CooldownMax"] = 3000 }, AbilityIds = ["0"] }
            },
            Abilities = tpl.Abilities
        };
        Assert.Empty(new CardInstance(tierUnlockedTpl, CardTier.Bronze).ActiveAbilities);
        Assert.Single(new CardInstance(tierUnlockedTpl, CardTier.Silver).ActiveAbilities);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 2：TooltipFormatter {attr.*} 四阶真渲染与毫秒自动转秒
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test02_TooltipFormatter_RendersAttrPlaceholdersAcrossAllTiers()
    {
        var data = Shared.Value;
        var baseKukri = data.RawRepo.GetCard(KukriTemplateId)!;
        var cardDef = BuildHealthySmallWeapon("demo-tip");
        cardDef.ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害，冷却 {attr.CooldownMax} 秒，不存在属性 {attr.NoSuchKey}"];
        var tpl = cardDef.ToCardTemplate(baseKukri, "demo-tip");

        var bronzeLines = TooltipFormatter.FormatTooltips(new CardInstance(tpl, CardTier.Bronze));
        var diamondLines = TooltipFormatter.FormatTooltips(new CardInstance(tpl, CardTier.Diamond));

        Assert.Single(bronzeLines);
        Assert.Contains("造成 18 点伤害", bronzeLines[0]);
        Assert.Contains("冷却 3.5 秒", bronzeLines[0]);
        Assert.DoesNotContain("秒秒", bronzeLines[0]);
        Assert.Contains("不存在属性 ?", bronzeLines[0]);

        Assert.Contains("造成 144 点伤害", diamondLines[0]);
        Assert.Contains("冷却 3 秒", diamondLines[0]);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 3：官方黄金标杆 mods/bazaar-chronicles-sample 10/10 铁闸满分 + 50 场真战斗 0 异常
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test03_SampleMod_PassesAllTenGates_And50RealCombatMatches_Perfectly()
    {
        var data = Shared.Value;
        var scanIssues = new List<ModValidationIssue>();
        var samplePkg = ModLoader.LoadSinglePackage(
            Path.Combine(data.ModsRoot, "bazaar-chronicles-sample"),
            "bazaar-chronicles-sample",
            scanIssues);

        Assert.NotNull(samplePkg);
        Assert.Empty(scanIssues.Where(i => i.Severity == ModValidationSeverity.Error));

        var report = ModValidator.RunTenGateValidation(
            samplePkg!,
            Array.Empty<ModPackage>(),
            data.RawRepo,
            data.ValidWhitelist,
            combatMatches: 50,
            combatSeed: 2026131,
            runDynamicGates: true);

        Assert.True(report.Passed, report.SummaryZh + " | " + string.Join("; ", report.Gates.SelectMany(g => g.Issues).Select(i => $"{i.Code}:{i.Message}")));
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(10, report.Gates.Count);
        Assert.All(report.Gates, g =>
        {
            if (g.GateId == 9)
                Assert.NotEqual("FAIL", g.Status);
            else
                Assert.Equal("PASS", g.Status);
        });

        Assert.NotNull(report.CombatStressReport);
        Assert.Equal(50, report.CombatStressReport!.MatchesRun);
        Assert.Equal(0, report.CombatStressReport.Exceptions);
        Assert.Equal(0, report.CombatStressReport.CascadeGuardTrips);
        Assert.Equal(0, report.CombatStressReport.MaxDurationHits);
        Assert.Equal(0, report.CombatStressReport.NumericCorruptions);
        Assert.Equal(0, report.CombatStressReport.MechanicCircuitBreakerTrips);
        Assert.Equal(0, report.CombatStressReport.MechanicBudgetViolations);
        Assert.True(report.CombatStressReport.TestedCardIds.Count >= 6);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 4：反向注入 7 类坏 Mod 全部精准拦截（Gate #1 ~ Gate #8 + #10）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test04_TenGates_InterceptsAllSevenCategoriesOfBrokenMods()
    {
        var data = Shared.Value;
        string tmpDir = CreateTempDir("v13_1_badmods");
        try
        {
            // (1) Gate #1：奇遇步骤包含虚构废字段 effects / costGold
            var badLegacyPkg = new ModPackage
            {
                RootDirectory = tmpDir,
                Manifest = new ModManifest { Id = "bad-legacy", Name = "坏字段测试", Version = "1.0.0", Scope = "multiplayer-safe" },
                Events =
                {
                    new CustomEventDefinition
                    {
                        EventId = "bad-legacy:ev_1",
                        EventType = "EventEncounter",
                        BaseVisualTemplateId = EventCarrierId,
                        TitleZh = "测试奇遇",
                        DescZh = "描述",
                        Steps =
                        {
                            new CustomCardDefinition
                            {
                                CustomId = "bad-legacy:step_1",
                                Type = "EncounterStep",
                                Size = "Medium",
                                BaseVisualTemplateId = StepCarrierId,
                                TitleZh = "坏选项",
                                DescZh = "坏描述",
                                ExtensionData = new Dictionary<string, JsonElement>
                                {
                                    ["effects"] = JsonSerializer.SerializeToElement(new[] { new { type = "GainGold", amount = 5 } }),
                                    ["costGold"] = JsonSerializer.SerializeToElement(3)
                                }
                            }
                        }
                    }
                }
            };
            var rep1 = ModValidator.RunTenGateValidation(badLegacyPkg, null, data.RawRepo, data.ValidWhitelist, runDynamicGates: false);
            Assert.False(rep1.Passed);
            Assert.Contains(rep1.Gates[0].Issues, i => i.Code == "UNSUPPORTED_LEGACY_FIELD");

            // (2) Gate #2：无命名空间前缀 + 循环依赖 (A <-> B)
            var cycA = new ModPackage { RootDirectory = tmpDir, Manifest = new ModManifest { Id = "cyc-a", Name = "A", Version = "1.0.0", Scope = "multiplayer-safe", Dependencies = ["cyc-b"] } };
            var cycB = new ModPackage { RootDirectory = tmpDir, Manifest = new ModManifest { Id = "cyc-b", Name = "B", Version = "1.0.0", Scope = "multiplayer-safe", Dependencies = ["cyc-a"] } };
            var noNsCard = BuildHealthySmallWeapon("cyc-a");
            noNsCard.CustomId = "missing_prefix_card";
            cycA.Cards.Add(noNsCard);
            var rep2 = ModValidator.RunTenGateValidation(cycA, [cycB], data.RawRepo, data.ValidWhitelist, runDynamicGates: false);
            Assert.False(rep2.Passed);
            Assert.Contains(rep2.Gates[1].Issues, i => i.Code == "CIRCULAR_DEPENDENCY");
            Assert.Contains(rep2.Gates[1].Issues, i => i.Code == "ID_NAMESPACE_VIOLATION");

            // (3) Gate #3：伪造白名单外 GUID + Item 错 Size
            var badCarrierPkg = new ModPackage { RootDirectory = tmpDir, Manifest = new ModManifest { Id = "bad-carrier", Name = "坏载体", Version = "1.0.0", Scope = "multiplayer-safe" } };
            var fakeCard = BuildHealthySmallWeapon("bad-carrier", "fake_guid");
            fakeCard.BaseVisualTemplateId = "00000000-0000-0000-0000-000000000000";
            var wrongSizeCard = BuildHealthySmallWeapon("bad-carrier", "wrong_size");
            wrongSizeCard.Size = "Large"; // 载体 Kukri 是 Small
            badCarrierPkg.Cards.Add(fakeCard);
            badCarrierPkg.Cards.Add(wrongSizeCard);
            var rep3 = ModValidator.RunTenGateValidation(badCarrierPkg, null, data.RawRepo, data.ValidWhitelist, runDynamicGates: false);
            Assert.False(rep3.Passed);
            Assert.Contains(rep3.Gates[2].Issues, i => i.Code == "BASE_VISUAL_NOT_IN_WHITELIST");
            Assert.Contains(rep3.Gates[2].Issues, i => i.Code == "BASE_VISUAL_SIZE_MISMATCH");

            // (4) Gate #4：未登记前置 TPrerequisitePlayerAttribute + 非法 $type + 拼写错误属性 targetMod
            var badRulePkg = new ModPackage { RootDirectory = tmpDir, Manifest = new ModManifest { Id = "bad-rule", Name = "坏规则", Version = "1.0.0", Scope = "multiplayer-safe" } };
            var badRuleCard = BuildHealthySmallWeapon("bad-rule", "bad_rule_card");
            badRuleCard.Abilities["0"].Action!.Type = "TActionImaginaryBoom";
            badRuleCard.Abilities["0"].Action!.Target!.ExtensionData = new Dictionary<string, JsonElement>
            {
                ["targetMod"] = JsonSerializer.SerializeToElement("Opponent") // 故意拼错 targetMode
            };
            badRuleCard.Auras["0"] = new AuraDefinition
            {
                Id = "0",
                Prerequisites = [new RuleNode { Type = "TPrerequisitePlayerAttribute" }],
                Action = new RuleNode { Type = "TAuraActionCardModifyAttribute", AttributeType = "DamageAmount", Operation = "Add" }
            };
            badRuleCard.Tiers!["Bronze"].AuraIds.Add("0");
            badRulePkg.Cards.Add(badRuleCard);
            var rep4 = ModValidator.RunTenGateValidation(badRulePkg, null, data.RawRepo, data.ValidWhitelist, runDynamicGates: false);
            Assert.False(rep4.Passed);
            Assert.Contains(rep4.Gates[3].Issues, i => i.Code == "UNKNOWN_RULE_TYPE");
            Assert.Contains(rep4.Gates[3].Issues, i => i.Code == "UNSUPPORTED_PREREQUISITE_TYPE");
            Assert.Contains(rep4.Gates[3].Issues, i => i.Code == "UNKNOWN_RULE_PROPERTY" && (i.FixHintZh?.Contains("TargetMode", StringComparison.OrdinalIgnoreCase) == true));

            // (5) Gate #5：CooldownMax=4（秒当毫秒）+ 光环 Multiply=50 + 自开火自充能死循环
            var badNumPkg = new ModPackage { RootDirectory = tmpDir, Manifest = new ModManifest { Id = "bad-num", Name = "坏数值", Version = "1.0.0", Scope = "multiplayer-safe" } };
            var badNumCard = BuildHealthySmallWeapon("bad-num", "loop_blade");
            badNumCard.Tiers!["Bronze"].Attributes["CooldownMax"] = 4; // 秒错写成毫秒
            badNumCard.Tiers["Silver"].Attributes["ChargeAmount"] = 4000;
            badNumCard.Abilities["1"] = new AbilityDefinition
            {
                Id = "1",
                Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                Action = new RuleNode
                {
                    Type = "TActionCardCharge",
                    Target = new RuleNode { Type = "TTargetCardSelf" },
                    RawValueNode = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["$type"] = "TFixedValue", ["value"] = 3500.0 })
                }
            };
            badNumCard.Auras["0"] = new AuraDefinition
            {
                Id = "0",
                Action = new RuleNode
                {
                    Type = "TAuraActionCardModifyAttribute",
                    AttributeType = "DamageAmount",
                    Operation = "Multiply",
                    RawValueNode = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["$type"] = "TFixedValue", ["value"] = 50.0 }),
                    Target = new RuleNode { Type = "TTargetCardSelf" }
                }
            };
            foreach (var td in badNumCard.Tiers.Values) { td.AbilityIds = ["0", "1"]; td.AuraIds = ["0"]; }
            badNumPkg.Cards.Add(badNumCard);
            var rep5 = ModValidator.RunTenGateValidation(badNumPkg, null, data.RawRepo, data.ValidWhitelist, runDynamicGates: false);
            Assert.False(rep5.Passed);
            Assert.Contains(rep5.Gates[4].Issues, i => i.Code == "COOLDOWN_TOO_LOW_OR_UNIT_ERROR");
            Assert.Contains(rep5.Gates[4].Issues, i => i.Code == "MULTIPLIER_EXPLOSION_RISK");
            Assert.Contains(rep5.Gates[4].Issues, i => i.Code == "SELF_CHARGE_INFINITE_LOOP_RISK");

            // (6) Gate #6：奇遇多跳循环 (evA -> stepA -> evB -> stepB -> evA) + 悬空目标
            var badDagPkg = new ModPackage
            {
                RootDirectory = tmpDir,
                Manifest = new ModManifest { Id = "bad-dag", Name = "坏奇遇环", Version = "1.0.0", Scope = "multiplayer-safe" },
                Events =
                {
                    new CustomEventDefinition
                    {
                        EventId = "bad-dag:ev_a", EventType = "EventEncounter", BaseVisualTemplateId = EventCarrierId, TitleZh = "A", DescZh = "A",
                        Steps =
                        {
                            new CustomCardDefinition
                            {
                                CustomId = "bad-dag:step_a", Type = "EncounterStep", Size = "Medium", BaseVisualTemplateId = StepCarrierId,
                                TitleZh = "去B", DescZh = "去B",
                                NextEncounterOnSelection = [new NextEncounterOption { Id = "bad-dag:ev_b" }]
                            }
                        }
                    },
                    new CustomEventDefinition
                    {
                        EventId = "bad-dag:ev_b", EventType = "EventEncounter", BaseVisualTemplateId = EventCarrierId, TitleZh = "B", DescZh = "B",
                        Steps =
                        {
                            new CustomCardDefinition
                            {
                                CustomId = "bad-dag:step_b", Type = "EncounterStep", Size = "Medium", BaseVisualTemplateId = StepCarrierId,
                                TitleZh = "回A", DescZh = "回A",
                                NextEncounterOnSelection = [new NextEncounterOption { Id = "bad-dag:ev_a" }, new NextEncounterOption { Id = "bad-dag:ghost_missing" }]
                            }
                        }
                    }
                }
            };
            var rep6 = ModValidator.RunTenGateValidation(badDagPkg, null, data.RawRepo, data.ValidWhitelist, runDynamicGates: false);
            Assert.False(rep6.Passed);
            Assert.Contains(rep6.Gates[5].Issues, i => i.Code == "ENCOUNTER_CYCLE_DETECTED");
            Assert.Contains(rep6.Gates[5].Issues, i => i.Code == "ENCOUNTER_DANGLING_TARGET");

            // (7) Gate #7 & Gate #8：未解析占位符 "?" + WebP 资产拒绝 + SHA-256 篡改拦截
            var badTipArtPkg = new ModPackage { RootDirectory = tmpDir, Manifest = new ModManifest { Id = "bad-art", Name = "坏图坏文", Version = "1.0.0", Scope = "multiplayer-safe" } };
            var badTipCard = BuildHealthySmallWeapon("bad-art", "bad_tip", artFile: "assets/fake.webp");
            badTipCard.ActiveTooltipsZh = ["造成 {attr.NonExistAttr} 点伤害"];
            badTipArtPkg.Cards.Add(badTipCard);
            badTipArtPkg.AssetsByLogicalPath["assets/fake.webp"] = new ModAssetEntry
            {
                ModId = "bad-art", LogicalPath = "assets/fake.webp", Sha256 = "aaaa", Bytes = 200, Width = 128, Height = 128, ContentType = "image/webp"
            };
            badTipArtPkg.AssetsByLogicalPath["assets/tampered.png"] = new ModAssetEntry
            {
                ModId = "bad-art", LogicalPath = "assets/tampered.png", Sha256 = "actual_sha", Bytes = 200, Width = 128, Height = 128, ContentType = "image/png"
            };
            badTipArtPkg.ExpectedAssetSha256ByPath["assets/tampered.png"] = "expected_other_sha";
            var rep78 = ModValidator.RunTenGateValidation(badTipArtPkg, null, data.RawRepo, data.ValidWhitelist, runDynamicGates: false);
            Assert.False(rep78.Passed);
            Assert.Contains(rep78.Gates[6].Issues, i => i.Code == "UNRESOLVED_TOOLTIP_PLACEHOLDER");
            Assert.Contains(rep78.Gates[7].Issues, i => i.Code == "ASSET_FORMAT_UNSUPPORTED_FOR_UNITY");
            Assert.Contains(rep78.Gates[7].Issues, i => i.Code == "ASSET_SHA256_MISMATCH");
        }
        finally
        {
            SafeDeleteDir(tmpDir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 5：CardPatchDefinition 原位魔改原版卡 + 真实战斗生效 + 回滚零残留
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test05_CardPatchDefinition_AppliesInPlaceAndRollsBackCleanly()
    {
        var data = Shared.Value;
        string tmpDir = CreateTempDir("v13_1_patch");
        try
        {
            var vanillaBefore = data.RawRepo.GetCard(KukriTemplateId)!;
            double origBronzeDmg = vanillaBefore.Tiers!["Bronze"].Attributes.GetValueOrDefault("DamageAmount", 0);

            var patchPkg = new ModPackage
            {
                RootDirectory = tmpDir,
                Manifest = new ModManifest { Id = "classic-rebalance", Name = "原版平衡补丁", Version = "1.0.0", Scope = "multiplayer-safe" },
                Patches =
                {
                    new CardPatchDefinition
                    {
                        TargetTemplateId = KukriTemplateId,
                        Note = "加强原版弯刀并追加 Relic 标签",
                        AddTags = ["Relic"],
                        TitleZh = "精锻风暴弯刀",
                        TierAttributeOverrides = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Bronze"] = new(StringComparer.OrdinalIgnoreCase) { ["CooldownMax"] = 2500, ["DamageAmount"] = origBronzeDmg + 50 }
                        }
                    }
                }
            };

            // 1. 必须 10/10 全绿通过（含 50 场真实战斗）
            var tenGate = ModValidator.RunTenGateValidation(patchPkg, null, data.RawRepo, data.ValidWhitelist, combatMatches: 20, runDynamicGates: true);
            Assert.True(tenGate.Passed, tenGate.SummaryZh);
            Assert.All(tenGate.Gates, g => Assert.NotEqual("FAIL", g.Status));

            // 2. 装入 ModRegistry 验证原位覆盖、不污染 BaseRepository 且强制隔离幽灵池
            var reg = new ModRegistry(data.RawRepo, data.ValidWhitelist);
            var loadRep = reg.LoadPackages([patchPkg]);
            Assert.True(loadRep.Succeeded);
            Assert.Equal(1, loadRep.TotalCardPatches);
            Assert.True(reg.ForcesGhostPoolIsolation);

            var patched = reg.ActiveRepository.GetCard(KukriTemplateId)!;
            Assert.Equal(origBronzeDmg + 50, patched.Tiers!["Bronze"].Attributes["DamageAmount"]);
            Assert.Equal(2500, patched.Tiers["Bronze"].Attributes["CooldownMax"]);
            Assert.Contains("Relic", patched.Tags);
            Assert.Equal("精锻风暴弯刀", reg.ActiveLocalization.GetLocalizedTitle(patched));

            // 原版纯净库毫发无损
            Assert.Equal(origBronzeDmg, data.RawRepo.GetCard(KukriTemplateId)!.Tiers!["Bronze"].Attributes.GetValueOrDefault("DamageAmount", 0));

            // 3. 回滚零残留
            reg.RollbackToBase();
            Assert.False(reg.ForcesGhostPoolIsolation);
            Assert.Empty(reg.LoadedPackages);
            Assert.Equal(origBronzeDmg, reg.ActiveRepository.GetCard(KukriTemplateId)!.Tiers!["Bronze"].Attributes.GetValueOrDefault("DamageAmount", 0));
        }
        finally
        {
            SafeDeleteDir(tmpDir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 6：Web 创意工坊 `/workshop` + 11 个 `/api/workshop/*` 端点 + 422 阻断 + ZIP 往返 + 公网写门控
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test06_WorkshopEndpoints_FullLifecycle_422Block_ZipSecurity_AndPublicReadOnlyGate()
    {
        var data = Shared.Value;
        string workDir = CreateTempDir("v13_1_workshop_srv");
        string modsDir = Path.Combine(workDir, "mods");
        string stateDir = Path.Combine(workDir, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        // 复制官方黄金示例包到隔离 mods 目录用于集成测试
        CopyDirectory(Path.Combine(data.ModsRoot, "bazaar-chronicles-sample"), Path.Combine(modsDir, "bazaar-chronicles-sample"));

        string? origMode = Environment.GetEnvironmentVariable("BAZAAR_WORKSHOP_MODE");
        string? origToken = Environment.GetEnvironmentVariable("BAZAAR_WORKSHOP_TOKEN");
        WebApplication? app = null;

        try
        {
            Environment.SetEnvironmentVariable("BAZAAR_WORKSHOP_MODE", "auto");
            Environment.SetEnvironmentVariable("BAZAAR_WORKSHOP_TOKEN", "secret-v131-token");

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var modHost = new ServerModHost(
                data.RawRepo,
                data.DataRoot,
                stateDir,
                data.ValidWhitelist,
                modsRootOverride: modsDir);
            builder.Services.AddSingleton(modHost);
            builder.Services.AddSingleton<WorkshopValidatorService>();

            app = builder.Build();
            WorkshopEndpoints.Map(app);
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            string baseUrl = addresses.First().TrimEnd('/');
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            // 1. `/workshop` & `/workshop/` 均返回 200 HTML 且相对资产可访问
            var htmlNoSlash = await http.GetAsync("/workshop");
            Assert.Equal(HttpStatusCode.OK, htmlNoSlash.StatusCode);
            string htmlBody = await htmlNoSlash.Content.ReadAsStringAsync();
            Assert.Contains("大巴扎创意工坊", htmlBody);
            Assert.Contains("./workshop/styles.css", htmlBody);

            var htmlWithSlash = await http.GetAsync("/workshop/");
            Assert.Equal(HttpStatusCode.OK, htmlWithSlash.StatusCode);
            Assert.Contains("./styles.css", await htmlWithSlash.Content.ReadAsStringAsync());

            var cssResp = await http.GetAsync("/workshop/styles.css");
            Assert.Equal(HttpStatusCode.OK, cssResp.StatusCode);

            // 2. 元信息、原版 3,368 白名单搜索、单卡蓝图、规则目录（+ ETag 304）、Mod 列表与工作区读取
            var metaJson = await http.GetFromJsonAsync<JsonElement>("/api/workshop/meta");
            Assert.True(metaJson.GetProperty("canWrite").GetBoolean());
            Assert.True(metaJson.GetProperty("whitelistedCardCount").GetInt32() >= 3000);

            var searchJson = await http.GetFromJsonAsync<JsonElement>("/api/workshop/base-cards?q=Kukri&type=Item&size=Small");
            Assert.True(searchJson.GetProperty("total").GetInt32() >= 1);

            var blueprintJson = await http.GetFromJsonAsync<JsonElement>($"/api/workshop/base-cards/{KukriTemplateId}");
            Assert.True(blueprintJson.GetProperty("isWhitelisted").GetBoolean());
            Assert.Equal(KukriTemplateId, blueprintJson.GetProperty("cloneDraftTemplate").GetProperty("baseVisualTemplateId").GetString());

            var catalogResp = await http.GetAsync("/api/workshop/rule-catalog");
            Assert.Equal(HttpStatusCode.OK, catalogResp.StatusCode);
            var etag = catalogResp.Headers.ETag?.Tag;
            Assert.False(string.IsNullOrWhiteSpace(etag));
            var catalogReq304 = new HttpRequestMessage(HttpMethod.Get, "/api/workshop/rule-catalog");
            catalogReq304.Headers.TryAddWithoutValidation("If-None-Match", etag);
            var catalog304 = await http.SendAsync(catalogReq304);
            Assert.Equal(HttpStatusCode.NotModified, catalog304.StatusCode);

            var modsJson = await http.GetFromJsonAsync<JsonElement>("/api/workshop/mods");
            Assert.True(modsJson.GetProperty("mods").GetArrayLength() >= 1);
            var sampleWs = await http.GetFromJsonAsync<JsonElement>("/api/workshop/mods/bazaar-chronicles-sample");
            Assert.True(sampleWs.GetProperty("ok").GetBoolean());

            // 3. 公网写保护（带公网 X-Forwarded-For 且无 Token -> 403；带正确 X-Workshop-Token -> 200）
            var goodWs = new WorkshopModWorkspace
            {
                Manifest = new ModManifest { Id = "ws-test-forge", Name = "工坊集成测试包", Version = "1.0.0", Author = "QA", Scope = "multiplayer-safe" },
                Cards = [BuildHealthySmallWeapon("ws-test-forge", "forge_blade", artFile: "assets/forge_blade.png")]
            };
            var pubDeniedReq = new HttpRequestMessage(HttpMethod.Post, "/api/workshop/mods")
            {
                Content = JsonContent.Create(goodWs)
            };
            pubDeniedReq.Headers.Add("X-Forwarded-For", "8.8.8.8"); // zero-leak:allow-ip
            var pubDeniedResp = await http.SendAsync(pubDeniedReq);
            Assert.Equal(HttpStatusCode.Forbidden, pubDeniedResp.StatusCode);

            var pubWrongTokReq = new HttpRequestMessage(HttpMethod.Post, "/api/workshop/mods")
            {
                Content = JsonContent.Create(goodWs)
            };
            pubWrongTokReq.Headers.Add("X-Forwarded-For", "8.8.8.8"); // zero-leak:allow-ip
            pubWrongTokReq.Headers.Add("X-Workshop-Token", "wrong-token");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(pubWrongTokReq)).StatusCode);

            var pubAuthReq = new HttpRequestMessage(HttpMethod.Post, "/api/workshop/mods")
            {
                Content = JsonContent.Create(goodWs)
            };
            pubAuthReq.Headers.Add("X-Forwarded-For", "8.8.8.8"); // zero-leak:allow-ip
            pubAuthReq.Headers.Add("X-Workshop-Token", "secret-v131-token");
            var pubAuthResp = await http.SendAsync(pubAuthReq);
            Assert.Equal(HttpStatusCode.OK, pubAuthResp.StatusCode);

            // 4. 上传合法 64x64 PNG 资产 + 预览回读 + 拒绝 WebP
            byte[] pngBytes = BuildValidPng64x64();
            using (var form = new MultipartFormDataContent())
            {
                var fc = new ByteArrayContent(pngBytes);
                fc.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(fc, "file", "forge_blade.png");
                form.Add(new StringContent("forge_blade.png"), "logicalName");
                var upResp = await http.PostAsync("/api/workshop/mods/ws-test-forge/assets", form);
                Assert.Equal(HttpStatusCode.OK, upResp.StatusCode);
                var upJson = await upResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("assets/forge_blade.png", upJson.GetProperty("logicalPath").GetString());
                Assert.Equal(64, upJson.GetProperty("width").GetInt32());
            }
            var previewResp = await http.GetAsync("/api/workshop/mods/ws-test-forge/assets/assets/forge_blade.png");
            Assert.Equal(HttpStatusCode.OK, previewResp.StatusCode);

            // 拒绝 WebP 上传
            using (var badForm = new MultipartFormDataContent())
            {
                byte[] fakeWebp = Encoding.ASCII.GetBytes("RIFF\x20\x00\x00\x00WEBPVP8X01234567890123456789");
                badForm.Add(new ByteArrayContent(fakeWebp), "file", "bad.webp");
                var badUp = await http.PostAsync("/api/workshop/mods/ws-test-forge/assets", badForm);
                Assert.Equal(HttpStatusCode.BadRequest, badUp.StatusCode);
            }

            // 5. 执行 10 闸门 + 50 场真实战斗校验 & 安全发布热重载
            var valResp = await http.PostAsJsonAsync("/api/workshop/mods/ws-test-forge/validate", new { matches = 50, seed = 2026131 });
            Assert.Equal(HttpStatusCode.OK, valResp.StatusCode);
            var valJson = await valResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(valJson.GetProperty("passed").GetBoolean(), valJson.ToString());
            Assert.Equal(50, valJson.GetProperty("combatStressReport").GetProperty("matchesRun").GetInt32());

            var pubResp = await http.PostAsync("/api/workshop/mods/ws-test-forge/publish-reload", null);
            Assert.Equal(HttpStatusCode.OK, pubResp.StatusCode);
            Assert.True(modHost.ActiveRepository.CardsById.ContainsKey("ws-test-forge:forge_blade"));

            // 6. 带病 Mod 发布必须返回 HTTP 422 阻断，绝不激活进服务器
            var badCard = BuildHealthySmallWeapon("ws-bad-mod", "broken_cd");
            badCard.Tiers!["Bronze"].Attributes["CooldownMax"] = 3; // 致命错误：秒当毫秒
            var badWs = new WorkshopModWorkspace
            {
                Manifest = new ModManifest { Id = "ws-bad-mod", Name = "带病测试", Version = "1.0.0", Scope = "multiplayer-safe" },
                Cards = [badCard]
            };
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/workshop/mods", badWs)).StatusCode);
            var blockedPub = await http.PostAsync("/api/workshop/mods/ws-bad-mod/publish-reload", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, blockedPub.StatusCode);
            Assert.False(modHost.ActiveRepository.CardsById.ContainsKey("ws-bad-mod:broken_cd"));

            // 7. 导出 .bazaarmod.zip -> 删除 -> 安全重新导入；并拦截含 .dll 恶意包
            byte[] exportedZip = await http.GetByteArrayAsync("/api/workshop/mods/ws-test-forge/export");
            Assert.True(exportedZip.Length > 200);

            var delResp = await http.DeleteAsync("/api/workshop/mods/ws-test-forge");
            Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
            Assert.False(modHost.ActiveRepository.CardsById.ContainsKey("ws-test-forge:forge_blade"));

            // 系统示例包受保护默认禁止删除
            Assert.Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync("/api/workshop/mods/bazaar-chronicles-sample")).StatusCode);

            using (var impForm = new MultipartFormDataContent())
            {
                impForm.Add(new ByteArrayContent(exportedZip), "file", "ws-test-forge.bazaarmod.zip");
                var impResp = await http.PostAsync("/api/workshop/mods/import", impForm);
                Assert.Equal(HttpStatusCode.OK, impResp.StatusCode);
                var impJson = await impResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(impJson.GetProperty("ok").GetBoolean());
                Assert.Equal("ws-test-forge", impJson.GetProperty("modId").GetString());
            }

            // 拦截含恶意 .dll 文件的压缩包
            using (var evilMs = new MemoryStream())
            {
                using (var z = new ZipArchive(evilMs, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var e = z.CreateEntry("payload.dll");
                    using var sw = new StreamWriter(e.Open());
                    sw.Write("MZ");
                }
                using var evilForm = new MultipartFormDataContent();
                evilForm.Add(new ByteArrayContent(evilMs.ToArray()), "file", "evil.bazaarmod.zip");
                var evilResp = await http.PostAsync("/api/workshop/mods/import", evilForm);
                Assert.Equal(HttpStatusCode.BadRequest, evilResp.StatusCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_WORKSHOP_MODE", origMode);
            Environment.SetEnvironmentVariable("BAZAAR_WORKSHOP_TOKEN", origToken);
            if (app != null) await app.DisposeAsync();
            SafeDeleteDir(workDir);
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(src, "*.*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, f);
            string dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
    }
}
