using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Tests.TestInfrastructure;
using BazaarServer.Mods;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2A Mod 双作用域、资产格式收口、两阶段 Patch 与全载体绑定强断言套件（[Collection("GlobalStaticState")]）。
/// 覆盖 P0-18、P0-23、P0-24、P0-25、P0-27、P0-28（方案 A）、D3-P1-01/02/06/08/12。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave2A_ModScopeAssetsAndBindingsTests
{
    private const string SmallItemCarrier = "03b1fd16-6b0d-4847-a76f-6c586f7c5958"; // Kukri
    private const string SkillCarrier = "06f3e3b4-1790-4a8e-9262-84c66c8e8d3e";
    private const string EventCarrier = "11287697-dd45-4edc-9539-780a26001043";

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

    private static byte[] BuildValidPng64x64()
    {
        const int w = 64, h = 64;
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        static uint Crc32(byte[] p1, byte[] p2)
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
            Feed(p1);
            Feed(p2);
            return crc ^ 0xFFFFFFFFu;
        }

        void WriteChunk(string type, byte[] data)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            ms.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length)));
            ms.Write(typeBytes);
            ms.Write(data);
            ms.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)Crc32(typeBytes, data))));
        }

        using (var ihdr = new MemoryStream())
        {
            ihdr.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(w)));
            ihdr.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(h)));
            ihdr.Write(new byte[] { 8, 2, 0, 0, 0 });
            WriteChunk("IHDR", ihdr.ToArray());
        }

        byte[] rawPixels = new byte[h * (1 + w * 3)];
        using (var idat = new MemoryStream())
        {
            using (var zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(rawPixels, 0, rawPixels.Length);
            WriteChunk("IDAT", idat.ToArray());
        }

        WriteChunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>构造带真实 SOI + APP0 + SOF0（64x64 大端宽高）+ EOI 的合法最小 JPEG 二进制头。</summary>
    private static byte[] BuildValidJpeg64x64()
    {
        return new byte[]
        {
            0xFF, 0xD8,                         // SOI
            0xFF, 0xE0, 0x00, 0x10,             // APP0 length = 16
            0x4A, 0x46, 0x49, 0x46, 0x00,       // "JFIF\0"
            0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11,             // SOF0 length = 17
            0x08,                               // 8-bit precision
            0x00, 0x40,                         // height = 64
            0x00, 0x40,                         // width = 64
            0x03,                               // 3 components
            0x01, 0x22, 0x00,
            0x02, 0x11, 0x01,
            0x03, 0x11, 0x01,
            0xFF, 0xD9                          // EOI
        };
    }

    /// <summary>构造带真实 RIFF + WEBP 魔数的最小 WebP 字节流（用于 P0-28 方案 A 拒收测试）。</summary>
    private static byte[] BuildMinimalWebpHeader()
    {
        return new byte[]
        {
            (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            0x20, 0x00, 0x00, 0x00,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P',
            (byte)'V', (byte)'P', (byte)'8', (byte)' ',
            0x10, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
    }

    private static byte[] ZipDirectory(string modDir, string modId)
    {
        using var ms = new MemoryStream();
        using (var arc = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (string file in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(modDir, file).Replace('\\', '/');
                var entry = arc.CreateEntry($"{modId}/{rel}", CompressionLevel.Optimal);
                using var es = entry.Open();
                using var fs = File.OpenRead(file);
                fs.CopyTo(es);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void P018_DualScopeIsoRegistry_RecursiveDependencies_AndBindToInstanceDeepCopy()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempRoot = CreateTempDir("w2a_p018");
        try
        {
            string modsDir = Path.Combine(tempRoot, "mods");
            string stateDir = Path.Combine(tempRoot, "state");
            string depDir = Path.Combine(modsDir, "dep-base");
            string appDir = Path.Combine(modsDir, "app-top");
            Directory.CreateDirectory(Path.Combine(depDir, "cards"));
            Directory.CreateDirectory(Path.Combine(appDir, "cards"));
            Directory.CreateDirectory(stateDir);

            // 被依赖包 dep-base（未启用全局，但被 app-top 依赖）
            File.WriteAllText(Path.Combine(depDir, "mod.json"), """
            {
              "id": "dep-base",
              "name": "基础依赖包",
              "version": "1.0.0",
              "author": "w2a",
              "scope": "multiplayer-safe"
            }
            """);
            File.WriteAllText(Path.Combine(depDir, "cards", "base_item.json"), $$"""
            {
              "customId": "dep-base:core_crystal",
              "baseVisualTemplateId": "{{SmallItemCarrier}}",
              "type": "Item",
              "size": "Small",
              "startingTier": "Bronze",
              "titleZh": "核心晶核",
              "titleEn": "Core Crystal",
              "descZh": "基础依赖卡",
              "descEn": "Base dependency card",
              "attributes": { "CooldownMax": 3000, "DamageAmount": 12 }
            }
            """);

            // 上层包 app-top（声明依赖 dep-base）
            File.WriteAllText(Path.Combine(appDir, "mod.json"), """
            {
              "id": "app-top",
              "name": "上层扩展包",
              "version": "1.0.0",
              "author": "w2a",
              "scope": "multiplayer-safe",
              "dependencies": ["dep-base"]
            }
            """);
            File.WriteAllText(Path.Combine(appDir, "cards", "top_item.json"), $$"""
            {
              "customId": "app-top:star_blade",
              "baseVisualTemplateId": "{{SmallItemCarrier}}",
              "type": "Item",
              "size": "Small",
              "startingTier": "Bronze",
              "titleZh": "星辰之刃",
              "titleEn": "Star Blade",
              "descZh": "上层依赖卡",
              "descEn": "Top dependent card",
              "attributes": { "CooldownMax": 2500, "DamageAmount": 18 }
            }
            """);

            var host = new ServerModHost(shared.Repo, shared.DataDir, stateDir, shared.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            // 先全部禁用来测试单局只选 app-top 时是否递归拉起 dep-base
            host.ToggleMod("dep-base", false);
            host.ToggleMod("app-top", false);

            var isoReg = host.ResolveEffectiveRegistryForMods(new[] { "app-top" });
            Assert.Contains(isoReg.LoadedPackages, p => p.Manifest.Id == "dep-base");
            Assert.Contains(isoReg.LoadedPackages, p => p.Manifest.Id == "app-top");
            // 依赖拓扑顺序：dep-base 必须先于 app-top 加载
            int idxDep = isoReg.LoadedPackages.ToList().FindIndex(p => p.Manifest.Id == "dep-base");
            int idxApp = isoReg.LoadedPackages.ToList().FindIndex(p => p.Manifest.Id == "app-top");
            Assert.True(idxDep >= 0 && idxApp > idxDep);

            Assert.True(isoReg.VisualBindingsByCustomId.ContainsKey("dep-base:core_crystal"));
            Assert.True(isoReg.VisualBindingsByCustomId.ContainsKey("app-top:star_blade"));
            Assert.NotNull(isoReg.ActiveRepository.GetCard("dep-base:core_crystal"));
            Assert.NotNull(isoReg.ActiveRepository.GetCard("app-top:star_blade"));

            // 验证 BindToInstance 与 CloneVisualBinding 完整深拷贝全部 11 新属性与子对象新字段（双审硬备忘 #2）
            var src = new CustomCardVisualBinding
            {
                CustomTemplateId = "app-top:star_blade",
                ModId = "app-top",
                BaseVisualTemplateId = SmallItemCarrier,
                CardType = "CombatEncounter",
                Size = "Medium",
                IsPatchOverride = false,
                IsCustomBorrowShell = true,
                TitleZh = "星辰之刃",
                TitleEn = "Star Blade",
                DescZh = "中文描述",
                DescEn = "English Desc",
                ActiveTooltipsEn = new List<string> { "Deal 18 damage." },
                PassiveTooltipsEn = new List<string> { "Passive EN." },
                EnchantTooltipsEn = new Dictionary<string, List<string>> { ["Fiery"] = new() { "Burn 5." } },
                VisualTarget = "EncounterPortrait",
                CustomBgArtFile = "art/bg.png",
                CustomBgArtSha256 = "deadbeef",
                EnchantmentPreviewOps = new Dictionary<string, List<ResolvedPreviewAttrOp>>
                {
                    ["Fiery"] = new()
                    {
                        new ResolvedPreviewAttrOp
                        {
                            Attribute = "BurnApplyAmount",
                            Operation = "Add",
                            Value = 4,
                            SourceAttribute = "DamageAmount",
                            SourceMultiplier = 0.5,
                            SourceAddOffset = 2
                        }
                    }
                },
                CounterDisplays = new List<ResolvedCounterDisplay>
                {
                    new() { LabelZh = "充能", SyncAttribute = "Custom_0", MaxValue = 10, MaxAttr = "Custom_1" }
                },
                MonsterPreview = new CustomMonsterPreviewBinding
                {
                    MonsterId = "mon_1",
                    NameZh = "星兽",
                    NameEn = "Star Beast",
                    MinDay = 3,
                    MaxDay = 6,
                    DifficultySlot = "Silver",
                    Level = 4,
                    HealthMax = 650,
                    Items = new List<CustomMonsterLoadoutItemBinding>
                    {
                        new() { CardId = "app-top:star_blade", WireTemplateId = SmallItemCarrier, Size = "Small", Tier = "Silver", TitleZh = "星辰之刃", TitleEn = "Star Blade" }
                    },
                    Skills = new List<CustomMonsterLoadoutItemBinding>
                    {
                        new() { CardId = "dep-base:core_crystal", Size = "Small", Tier = "Bronze" }
                    }
                }
            };

            var bound = src.BindToInstance("inst_copy_1");

            // 修改原对象全部集合后，bound 绝不受影响
            src.ActiveTooltipsEn!.Add("MUTATED");
            src.EnchantTooltipsEn!["Fiery"].Add("MUTATED");
            src.MonsterPreview!.Items.Clear();
            src.EnchantmentPreviewOps!["Fiery"][0].SourceMultiplier = 999;
            src.CounterDisplays![0].MaxAttr = "MUTATED";

            Assert.True(bound.IsCustomBorrowShell);
            Assert.False(bound.IsPatchOverride);
            Assert.Equal("Star Blade", bound.TitleEn);
            Assert.Equal("English Desc", bound.DescEn);
            Assert.Single(bound.ActiveTooltipsEn!);
            Assert.Single(bound.PassiveTooltipsEn!);
            Assert.Single(bound.EnchantTooltipsEn!["Fiery"]);
            Assert.Equal("EncounterPortrait", bound.VisualTarget);
            Assert.Equal("art/bg.png", bound.CustomBgArtFile);
            Assert.Equal("deadbeef", bound.CustomBgArtSha256);
            Assert.Equal(0.5, bound.EnchantmentPreviewOps!["Fiery"][0].SourceMultiplier);
            Assert.Equal(2.0, bound.EnchantmentPreviewOps!["Fiery"][0].SourceAddOffset);
            Assert.Equal("Custom_1", bound.CounterDisplays![0].MaxAttr);
            Assert.NotNull(bound.MonsterPreview);
            Assert.Single(bound.MonsterPreview!.Items);
            Assert.Equal("Small", bound.MonsterPreview.Items[0].Size);
            Assert.Single(bound.MonsterPreview.Skills);
            Assert.Equal("inst_copy_1", bound.InstanceId);
        }
        finally
        {
            SafeDeleteDir(tempRoot);
        }
    }

    [Fact]
    public void P028_AssetGate8_AllowsRealPngAndJpeg_RejectsWebpAndDisguisedWebp()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempDir = CreateTempDir("w2a_p028");
        try
        {
            // 1. 合法 PNG + 合法 JPEG（真实 SOF0 64x64）均应 0 Error 通过 Gate #8
            string okMod = Path.Combine(tempDir, "ok-assets");
            Directory.CreateDirectory(Path.Combine(okMod, "assets"));
            File.WriteAllText(Path.Combine(okMod, "mod.json"), """{"id":"ok-assets","name":"合法图","version":"1.0.0","scope":"cosmetic-only"}""");
            File.WriteAllBytes(Path.Combine(okMod, "assets", "icon.png"), BuildValidPng64x64());
            File.WriteAllBytes(Path.Combine(okMod, "assets", "bg.jpg"), BuildValidJpeg64x64());

            var issues1 = new List<ModValidationIssue>();
            var okPkg = ModLoader.LoadSinglePackage(okMod, "ok-assets", issues1);
            Assert.NotNull(okPkg);
            var (_, valOk, _) = ModValidator.ValidateAndSort(new[] { okPkg! }, shared.Repo, shared.ValidCardIds);
            Assert.DoesNotContain(valOk, i => i.GateId == 8 && i.Severity == ModValidationSeverity.Error);

            // 2. .webp 文件必须被显式登记并由 Gate #8 报 ASSET_FORMAT_UNSUPPORTED_FOR_UNITY
            string webpMod = Path.Combine(tempDir, "bad-webp");
            Directory.CreateDirectory(Path.Combine(webpMod, "assets"));
            File.WriteAllText(Path.Combine(webpMod, "mod.json"), """{"id":"bad-webp","name":"WebP拒收","version":"1.0.0","scope":"cosmetic-only"}""");
            File.WriteAllBytes(Path.Combine(webpMod, "assets", "card.webp"), BuildMinimalWebpHeader());

            var issues2 = new List<ModValidationIssue>();
            var webpPkg = ModLoader.LoadSinglePackage(webpMod, "bad-webp", issues2);
            Assert.NotNull(webpPkg);
            var (_, valWebp, _) = ModValidator.ValidateAndSort(new[] { webpPkg! }, shared.Repo, shared.ValidCardIds);
            Assert.Contains(valWebp, i => i.GateId == 8 && i.Severity == ModValidationSeverity.Error && i.Code == "ASSET_FORMAT_UNSUPPORTED_FOR_UNITY");

            // 3. 把 WebP 改名为 .png 伪装上传同样必须被魔数识别并报 ASSET_FORMAT_UNSUPPORTED_FOR_UNITY
            string fakePngMod = Path.Combine(tempDir, "fake-png");
            Directory.CreateDirectory(Path.Combine(fakePngMod, "assets"));
            File.WriteAllText(Path.Combine(fakePngMod, "mod.json"), """{"id":"fake-png","name":"伪装PNG","version":"1.0.0","scope":"cosmetic-only"}""");
            File.WriteAllBytes(Path.Combine(fakePngMod, "assets", "disguised.png"), BuildMinimalWebpHeader());

            var issues3 = new List<ModValidationIssue>();
            var fakePkg = ModLoader.LoadSinglePackage(fakePngMod, "fake-png", issues3);
            Assert.NotNull(fakePkg);
            var (_, valFake, _) = ModValidator.ValidateAndSort(new[] { fakePkg! }, shared.Repo, shared.ValidCardIds);
            Assert.Contains(valFake, i => i.GateId == 8 && i.Severity == ModValidationSeverity.Error && i.Code == "ASSET_FORMAT_UNSUPPORTED_FOR_UNITY");
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact]
    public void P025_D3P101_D3P102_D3P106_TwoPhasePatchMerge_EnchantPreviewOps_TierFill_AndMultiCounters()
    {
        var shared = SharedGameDataFixture.Shared;
        var baseCard = shared.Repo.GetCard(SmallItemCarrier);
        Assert.NotNull(baseCard);

        // Mod 1：改标题、英文双语、多槽 CounterDisplaysOverride，并给 Fiery 附魔注入带 SourceMultiplier 的 Modifier 技能
        var pkg1 = new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest { Id = "patch-mod-1", Name = "补丁1", Version = "1.0.0", Scope = "multiplayer-safe" },
            Patches =
            {
                new CardPatchDefinition
                {
                    TargetTemplateId = SmallItemCarrier,
                    TitleZh = "魔改库克里",
                    TitleEn = "Modded Kukri",
                    ActiveTooltipsEn = new List<string> { "Deal {attr.DamageAmount} damage." },
                    EnchantTooltipsEn = new Dictionary<string, List<string>> { ["Fiery"] = new() { "Burn {attr.BurnApplyAmount}." } },
                    CounterDisplaysOverride = new List<MechanicCounterDisplay>
                    {
                        new() { LabelZh = "蓄能", SyncAttribute = "Custom_0", MaxValueAlias = 5, MaxAttr = "Custom_1" },
                        new() { LabelZh = "连击", SyncAttribute = "Custom_2", MaxValueAlias = 3 }
                    },
                    EnchantmentOverrides = new Dictionary<string, EnchantmentData>
                    {
                        ["Fiery"] = new EnchantmentData
                        {
                            Auras = new Dictionary<string, AuraDefinition>
                            {
                                ["e0"] = new AuraDefinition
                                {
                                    Id = "e0",
                                    Action = JsonSerializer.Deserialize<RuleNode>("""
                                    {
                                      "$type": "TAuraActionCardModifyAttribute",
                                      "AttributeType": "BurnApplyAmount",
                                      "Operation": "Add",
                                      "Target": { "$type": "TTargetCardSelf" },
                                      "Value": {
                                        "$type": "TReferenceValueCardAttribute",
                                        "AttributeType": "DamageAmount",
                                        "Modifier": {
                                          "ModifyMode": "Multiply",
                                          "Value": { "$type": "TFixedValue", "Value": 0.5 }
                                        }
                                      }
                                    }
                                    """)!
                                },
                                ["e1"] = new AuraDefinition
                                {
                                    Id = "e1",
                                    Action = JsonSerializer.Deserialize<RuleNode>("""
                                    {
                                      "$type": "TAuraActionCardModifyAttribute",
                                      "AttributeType": "ShieldApplyAmount",
                                      "Operation": "Add",
                                      "Target": { "$type": "TTargetCardSelf" },
                                      "Value": {
                                        "$type": "TReferenceValueCardAttribute",
                                        "AttributeType": "DamageAmount",
                                        "Modifier": {
                                          "ModifyMode": "Add",
                                          "Value": { "$type": "TFixedValue", "Value": 3 }
                                        }
                                      }
                                    }
                                    """)!
                                }
                            }
                        }
                    }
                }
            }
        };

        // Mod 2：仅修改数值 AttributeOverrides（不碰 TitleZh/CounterDisplaysOverride）
        var pkg2 = new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest { Id = "patch-mod-2", Name = "补丁2", Version = "1.0.0", Scope = "multiplayer-safe", Dependencies = { "patch-mod-1" } },
            Patches =
            {
                new CardPatchDefinition
                {
                    TargetTemplateId = SmallItemCarrier,
                    AttributeOverrides = new Dictionary<string, double> { ["DamageAmount"] = 77 }
                }
            }
        };

        Assert.True(pkg2.Patches[0].HasAnyMutation);

        var isoReg = ModRegistry.CreateIsolated(shared.Repo, shared.ValidCardIds);
        var rep = isoReg.LoadPackages(new[] { pkg1, pkg2 }, sourceLabel: "two-phase-patch");
        Assert.True(rep.Succeeded, string.Join("; ", rep.Issues.Select(i => $"{i.Code}:{i.Message}")));

        Assert.True(isoReg.VisualBindingsByCustomId.TryGetValue(SmallItemCarrier, out var vb));
        Assert.NotNull(vb);
        Assert.True(vb!.IsPatchOverride);
        Assert.False(vb.IsCustomBorrowShell);
        Assert.Equal("魔改库克里", vb.TitleZh);
        Assert.Equal("Modded Kukri", vb.TitleEn);
        Assert.NotNull(vb.CounterDisplays);
        Assert.Equal(2, vb.CounterDisplays!.Count);
        Assert.Equal("Custom_1", vb.CounterDisplays[0].MaxAttr);
        Assert.NotNull(vb.TierAttributes);
        Assert.True(vb.TierAttributes!.ContainsKey("Legendary"));
        Assert.Equal(77, vb.TierAttributes["Bronze"]["DamageAmount"]);
        Assert.NotNull(vb.EnchantmentPreviewOps);
        Assert.True(vb.EnchantmentPreviewOps!.TryGetValue("Fiery", out var ops));
        Assert.Contains(ops!, o => o.Attribute == "BurnApplyAmount" && o.SourceAttribute == "DamageAmount" && o.SourceMultiplier == 0.5);
        Assert.Contains(ops!, o => o.Attribute == "ShieldApplyAmount" && o.SourceAttribute == "DamageAmount" && o.SourceAddOffset == 3.0);
    }

    [Fact]
    public void P023_P024_P027_D3P112_UniversalSkill_EventSchedule_VisualTarget_AndMechanicCount()
    {
        var shared = SharedGameDataFixture.Shared;
        var skillBase = shared.Repo.GetCard(SkillCarrier) ?? shared.Repo.CardsById.Values.First(c => c.Type == "Skill");
        var eventBase = shared.Repo.GetCard(EventCarrier)!;

        // 1. P0-23：通用自定义技能 vs 限定英雄技能
        var universalSkill = new CustomCardDefinition
        {
            CustomId = "w2a-meta:uni_skill",
            BaseVisualTemplateId = skillBase.Id,
            Type = "Skill",
            Size = "Small",
            TitleZh = "通用星火",
            TitleEn = "Universal Spark",
            DescZh = "任意英雄均可习得的通用星火技能",
            Heroes = new List<string>()
        };
        var heroSkill = new CustomCardDefinition
        {
            CustomId = "w2a-meta:vanessa_only",
            BaseVisualTemplateId = skillBase.Id,
            Type = "Skill",
            Size = "Small",
            TitleZh = "船长专精",
            DescZh = "仅瓦内莎专属技能",
            Heroes = new List<string> { "Vanessa" }
        };
        var tplUni = universalSkill.ToCardTemplate(skillBase, "w2a-meta");
        var tplHero = heroSkill.ToCardTemplate(skillBase, "w2a-meta");
        Assert.True(tplUni.IsCustomModCard);
        Assert.True(tplUni.IsUniversalCustomSkill);
        Assert.True(tplHero.IsCustomModCard);
        Assert.False(tplHero.IsUniversalCustomSkill);

        // 2. P0-24：CustomEventDefinition.Schedule 全透传 Hours / OncePerRun / ChainOnly / DifficultySlot
        var normalEvDef = new CustomEventDefinition
        {
            EventId = "w2a-meta:ev_normal",
            BaseVisualTemplateId = EventCarrier,
            TitleZh = "常规奇遇",
            TitleEn = "Normal Event",
            DescZh = "测试常规排期奇遇",
            Schedule = new CustomEventSchedule
            {
                MinDay = 2,
                MaxDay = 7,
                Weight = 3.0,
                Hours = new List<int> { 1, 4 },
                OncePerRun = true,
                ChainOnly = false,
                DifficultySlot = "Gold"
            }
        };
        var normalEvTpl = normalEvDef.ToCardDefinition().ToCardTemplate(eventBase, "w2a-meta");
        Assert.True(normalEvTpl.IsCustomModCard);
        Assert.Equal(2, normalEvTpl.ModMinDay);
        Assert.Equal(7, normalEvTpl.ModMaxDay);
        Assert.Equal(3.0, normalEvTpl.ModSpawnWeight);
        Assert.Equal(new[] { 1, 4 }, normalEvTpl.AllowedHours);
        Assert.True(normalEvTpl.IsSingleSpawn);
        Assert.Equal("Always", normalEvTpl.SpawningEligibility);
        Assert.Equal("Gold", normalEvTpl.ModDifficultySlot);

        var stepCarrier = shared.Repo.CardsById.Values.First(c => c.Type == "EncounterStep" && shared.ValidCardIds.Contains(c.Id)).Id;
        var evDef = new CustomEventDefinition
        {
            EventId = "w2a-meta:ev_chain",
            BaseVisualTemplateId = EventCarrier,
            TitleZh = "秘境奇遇",
            TitleEn = "Secret Event",
            DescZh = "仅由事件链触发的秘境奇遇",
            CustomBgArtFile = "assets/bg.png",
            Steps =
            {
                new CustomCardDefinition
                {
                    CustomId = "w2a-meta:ev_chain_step1",
                    BaseVisualTemplateId = stepCarrier,
                    Type = "EncounterStep",
                    Size = "Medium",
                    TitleZh = "领取补给",
                    DescZh = "获得少量金币补给"
                }
            },
            Schedule = new CustomEventSchedule
            {
                MinDay = 2,
                MaxDay = 7,
                Weight = 3.0,
                Hours = new List<int> { 1, 4 },
                OncePerRun = true,
                ChainOnly = true,
                DifficultySlot = "Gold"
            }
        };
        var evTpl = evDef.ToCardDefinition().ToCardTemplate(eventBase, "w2a-meta");
        Assert.True(evTpl.IsCustomModCard);
        Assert.Equal(2, evTpl.ModMinDay);
        Assert.Equal(7, evTpl.ModMaxDay);
        Assert.Equal(0.0, evTpl.ModSpawnWeight);
        Assert.Equal(new[] { 1, 4 }, evTpl.AllowedHours);
        Assert.True(evTpl.IsSingleSpawn);
        Assert.Equal("GuidOnly", evTpl.SpawningEligibility);
        Assert.Equal("Gold", evTpl.ModDifficultySlot);

        // 3. VisualTarget 对齐客户端 CardVisualTarget，且 ModSummaryDto.CustomMechanicCount 真赋值
        var pkg = new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest { Id = "w2a-meta", Name = "元数据测试", Version = "1.0.0", Scope = "multiplayer-safe" },
            Cards = { universalSkill },
            Events = { evDef },
            Mechanics =
            {
                new MechanicDefinition
                {
                    Id = "w2a-meta:mech_1",
                    NameZh = "测试机制1",
                    Hooks = { new MechanicHookDefinition { Hook = "OnFightStart", Scope = "SelfCard", Steps = { new MechanicStep { Op = "SetFlag", Flag = "M1" } } } }
                },
                new MechanicDefinition
                {
                    Id = "w2a-meta:mech_2",
                    NameZh = "测试机制2",
                    Hooks = { new MechanicHookDefinition { Hook = "OnFightStart", Scope = "SelfCard", Steps = { new MechanicStep { Op = "SetFlag", Flag = "M2" } } } }
                }
            }
        };
        pkg.AssetsByLogicalPath["assets/bg.png"] = new ModAssetEntry
        {
            ModId = "w2a-meta",
            LogicalPath = "assets/bg.png",
            FullFilePath = Path.Combine(Path.GetTempPath(), "bg.png"),
            Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            Bytes = 1024,
            Width = 64,
            Height = 64,
            ContentType = "image/png"
        };
        var isoReg = ModRegistry.CreateIsolated(shared.Repo, shared.ValidCardIds);
        var report = isoReg.LoadPackages(new[] { pkg }, sourceLabel: "meta-test");
        Assert.True(report.Succeeded, string.Join("; ", report.Issues.Select(i => $"{i.Code}:{i.Message}")));

        Assert.Equal("SkillIcon", isoReg.VisualBindingsByCustomId["w2a-meta:uni_skill"].VisualTarget);
        Assert.Equal("EncounterPortrait", isoReg.VisualBindingsByCustomId["w2a-meta:ev_chain"].VisualTarget);
        Assert.Equal("assets/bg.png", isoReg.VisualBindingsByCustomId["w2a-meta:ev_chain"].CustomBgArtFile);

        var manifestDto = isoReg.BuildClientManifestDto();
        var summary = Assert.Single(manifestDto.Mods);
        Assert.Equal(2, summary.CustomMechanicCount);
    }

    [Fact]
    public async Task D3P108_CanonicalPackageHash_AndCommunityDependencyAndDirLockConcurrency()
    {
        var shared = SharedGameDataFixture.Shared;
        string tempRoot = CreateTempDir("w2a_d3p108");
        try
        {
            // 1. 整包规范 JSON ComputePackageHash 对任意字段改动敏感且确定性一致
            var pA = new ModPackage
            {
                RootDirectory = Path.GetTempPath(),
                Manifest = new ModManifest { Id = "hash-mod", Name = "Hash", Version = "1.0.0" },
                Patches = { new CardPatchDefinition { TargetTemplateId = SmallItemCarrier, AttributeOverrides = new() { ["DamageAmount"] = 10 } } }
            };
            var pB = new ModPackage
            {
                RootDirectory = Path.GetTempPath(),
                Manifest = new ModManifest { Id = "hash-mod", Name = "Hash", Version = "1.0.0" },
                Patches = { new CardPatchDefinition { TargetTemplateId = SmallItemCarrier, AttributeOverrides = new() { ["DamageAmount"] = 11 } } }
            };
            string hA1 = ModLoader.ComputePackageHash(pA);
            string hA2 = ModLoader.ComputePackageHash(pA);
            string hB = ModLoader.ComputePackageHash(pB);
            Assert.Equal(hA1, hA2);
            Assert.NotEqual(hA1, hB);

            // 2. 社区校验纳入已上架社区依赖包 + 并发上传/查询/删除在严格单向锁序 DAG 下 0 死锁
            string modsDir = Path.Combine(tempRoot, "mods");
            string stateDir = Path.Combine(tempRoot, "state");
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(stateDir);

            var host = new ServerModHost(shared.Repo, shared.DataDir, stateDir, shared.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            var community = new CommunityModStore(host, stateDir);

            string baseSrc = Path.Combine(tempRoot, "src_base");
            string childSrc = Path.Combine(tempRoot, "src_child");
            Directory.CreateDirectory(Path.Combine(baseSrc, "cards"));
            Directory.CreateDirectory(Path.Combine(childSrc, "cards"));
            File.WriteAllText(Path.Combine(baseSrc, "mod.json"), """{"id":"comm-base","name":"社区基础包","version":"1.0.0","author":"u1","scope":"multiplayer-safe"}""");
            File.WriteAllText(Path.Combine(baseSrc, "cards", "c1.json"), $$$"""{"customId":"comm-base:c1","baseVisualTemplateId":"{{{SmallItemCarrier}}}","type":"Item","size":"Small","titleZh":"基卡","attributes":{"CooldownMax":3000,"DamageAmount":10}}""");

            File.WriteAllText(Path.Combine(childSrc, "mod.json"), """{"id":"comm-child","name":"社区子包","version":"1.0.0","author":"u1","scope":"multiplayer-safe","dependencies":["comm-base"]}""");
            File.WriteAllText(Path.Combine(childSrc, "cards", "c2.json"), $$$"""{"customId":"comm-child:c2","baseVisualTemplateId":"{{{SmallItemCarrier}}}","type":"Item","size":"Small","titleZh":"子卡","attributes":{"CooldownMax":3000,"DamageAmount":12}}""");

            byte[] baseZip = ZipDirectory(baseSrc, "comm-base");
            byte[] childZip = ZipDirectory(childSrc, "comm-child");

            var upBase = await community.UploadZipAsync(baseZip, "acc_u1", "u1", isAdmin: true);
            Assert.True(upBase.Ok, upBase.Error);

            var upChild = await community.UploadZipAsync(childZip, "acc_u1", "u1", isAdmin: true);
            Assert.True(upChild.Ok, upChild.Error);

            var tasks = Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
            {
                if (i % 2 == 0)
                {
                    var r = await community.UploadZipAsync(childZip, "acc_u1", "u1", isAdmin: true);
                    Assert.True(r.Ok, r.Error);
                }
                else
                {
                    var list = community.ListCatalog(requesterAccountId: "acc_u1", isAdmin: true);
                    Assert.True(list.Count >= 2);
                    Assert.True(community.TryGetDownloadZip("comm-child", out var zipBytes, out _, out _));
                    Assert.NotEmpty(zipBytes);
                }
            })).ToArray();

            var allDone = Task.WhenAll(tasks);
            var completed = await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(allDone, completed);
            await allDone;

            var delChild = community.DeleteCommunityMod("comm-child", "acc_u1", isAdmin: true);
            Assert.True(delChild.Ok, delChild.Error);
        }
        finally
        {
            SafeDeleteDir(tempRoot);
        }
    }

    [Fact]
    public async Task P018_CommunityBuildSelectedBundleDto_TransitiveDeps_TwoPhasePatches_AndSynthesizedL10n()
    {
        var shared = SharedGameDataFixture.Shared;
        string skillId = (shared.Repo.GetCard(SkillCarrier)
            ?? shared.Repo.CardsById.Values.First(c => c.Type == "Skill" && shared.ValidCardIds.Contains(c.Id))).Id;
        string tempRoot = CreateTempDir("w2a_p018_bundle");
        try
        {
            string modsDir = Path.Combine(tempRoot, "mods");
            string stateDir = Path.Combine(tempRoot, "state");
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(stateDir);

            var host = new ServerModHost(shared.Repo, shared.DataDir, stateDir, shared.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            var community = new CommunityModStore(host, stateDir);

            // 1. 传递依赖社区包 bundle-dep-a：带自定义 PNG 贴图、自定义卡、自定义机制（含计数徽章）与对 SmallItemCarrier 的标题 Patch；故意不写 localization/zh-CN.json
            string depDir = Path.Combine(tempRoot, "src_dep_a");
            Directory.CreateDirectory(Path.Combine(depDir, "cards"));
            Directory.CreateDirectory(Path.Combine(depDir, "patches"));
            Directory.CreateDirectory(Path.Combine(depDir, "mechanics"));
            Directory.CreateDirectory(Path.Combine(depDir, "assets"));

            File.WriteAllText(Path.Combine(depDir, "mod.json"), """
            {
              "id": "bundle-dep-a",
              "name": "房间依赖底包A",
              "version": "1.0.0",
              "author": "w2a",
              "scope": "multiplayer-safe"
            }
            """);
            File.WriteAllBytes(Path.Combine(depDir, "assets", "dep_art.png"), BuildValidPng64x64());
            File.WriteAllText(Path.Combine(depDir, "cards", "dep_blade.json"), $$$"""
            {
              "customId": "bundle-dep-a:dep_blade",
              "baseVisualTemplateId": "{{{SmallItemCarrier}}}",
              "type": "Item",
              "size": "Small",
              "startingTier": "Bronze",
              "titleZh": "依赖灵刃",
              "descZh": "来自传递依赖包的自定义卡",
              "customArtFile": "assets/dep_art.png",
              "attributes": { "CooldownMax": 3000, "DamageAmount": 14 }
            }
            """);
            File.WriteAllText(Path.Combine(depDir, "mechanics", "charge_mech.json"), """
            {
              "id": "bundle-dep-a:charge_mech",
              "nameZh": "灵能充能",
              "counterDisplay": {
                "labelZh": "灵能",
                "syncAttribute": "Custom_0",
                "maxConst": 5
              },
              "hooks": [
                {
                  "hook": "OnFightStart",
                  "scope": "SelfCard",
                  "steps": [
                    { "op": "ModifyCounter", "key": "charge", "operation": "Add", "value": { "const": 1 }, "syncAttribute": "Custom_0" }
                  ]
                }
              ]
            }
            """);
            File.WriteAllText(Path.Combine(depDir, "patches", "kukri_title.json"), $$$"""
            {
              "targetTemplateId": "{{{SmallItemCarrier}}}",
              "titleZh": "魔改库克里A",
              "descZh": "底包修改的标题与说明"
            }
            """);

            // 2. 上层社区包 bundle-top-b（声明依赖 bundle-dep-a）：
            //    - 对 SmallItemCarrier 做纯数值 + 挂载跨包依赖机制的增量 Patch（不覆写标题）；
            //    - 对原版 Skill 做原位 Skill Patch（验证 CardType=Skill、VisualTarget=SkillIcon 不退化为 Item/ItemArt）；
            //    - 同样故意不写 localization/zh-CN.json
            string topDir = Path.Combine(tempRoot, "src_top_b");
            Directory.CreateDirectory(Path.Combine(topDir, "patches"));
            File.WriteAllText(Path.Combine(topDir, "mod.json"), """
            {
              "id": "bundle-top-b",
              "name": "房间上层包B",
              "version": "1.0.0",
              "author": "w2a",
              "scope": "multiplayer-safe",
              "dependencies": ["bundle-dep-a"]
            }
            """);
            File.WriteAllText(Path.Combine(topDir, "patches", "kukri_stats.json"), $$$"""
            {
              "targetTemplateId": "{{{SmallItemCarrier}}}",
              "attributeOverrides": { "DamageAmount": 88 },
              "customMechanicsOverride": [
                { "mechanicId": "bundle-dep-a:charge_mech" }
              ]
            }
            """);
            File.WriteAllText(Path.Combine(topDir, "patches", "skill_patch.json"), $$$"""
            {
              "targetTemplateId": "{{{skillId}}}",
              "titleZh": "魔改技能B",
              "descZh": "原位魔改的技能说明"
            }
            """);

            var upA = await community.UploadZipAsync(ZipDirectory(depDir, "bundle-dep-a"), "acc_room", "Host", isAdmin: true);
            Assert.True(upA.Ok, upA.Error);
            var upB = await community.UploadZipAsync(ZipDirectory(topDir, "bundle-top-b"), "acc_room", "Host", isAdmin: true);
            Assert.True(upB.Ok, upB.Error);

            // 3. 房间仅显式选中 ["bundle-top-b", "missing-mod-xyz"]（未显式列出传递依赖 bundle-dep-a）
            object bundleDto = community.BuildSelectedBundleDto(new[] { "bundle-top-b", "missing-mod-xyz" });
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(bundleDto));
            var root = doc.RootElement;

            // 3.1 missingModIds 精确保留缺失项
            var missing = root.GetProperty("missingModIds").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("missing-mod-xyz", missing);

            // 3.2 mods 递归包含传递依赖 bundle-dep-a 且依赖排在 bundle-top-b 之前
            var modIds = root.GetProperty("mods").EnumerateArray().Select(m => m.GetProperty("modId").GetString()).ToList();
            int idxA = modIds.IndexOf("bundle-dep-a");
            int idxB = modIds.IndexOf("bundle-top-b");
            Assert.True(idxA >= 0 && idxB > idxA, $"应按依赖拓扑序导出 bundle-dep-a 与 bundle-top-b，实际={string.Join(",", modIds)}");

            // 3.3 传递依赖包 bundle-dep-a 的贴图资产一并下发客户端，且可被 ServerModHost 穿透解析
            var assetsArr = root.GetProperty("assets").EnumerateArray().ToList();
            var depAsset = Assert.Single(assetsArr, a =>
                string.Equals(a.GetProperty("modId").GetString(), "bundle-dep-a", StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.GetProperty("logicalPath").GetString(), "assets/dep_art.png", StringComparison.OrdinalIgnoreCase));
            string depSha = depAsset.GetProperty("sha256").GetString()!;
            Assert.True(host.TryResolveAssetBySha256(depSha, out var resolvedAsset));
            Assert.NotNull(resolvedAsset);

            // 3.4 visualBindings 经隔离 ModRegistry 两阶段合并与全局 100% 同构
            var vbs = root.GetProperty("visualBindings");
            Assert.True(vbs.TryGetProperty("bundle-dep-a:dep_blade", out var depCardVb));
            Assert.Equal("依赖灵刃", depCardVb.GetProperty("titleZh").GetString());
            Assert.Equal(depSha, depCardVb.GetProperty("customArtSha256").GetString());

            // 3.4.1 Item 原位 Patch：保留底包标题、叠加顶包数值至完整 TierAttributes、解析跨包依赖机制 CounterDisplays
            Assert.True(vbs.TryGetProperty(SmallItemCarrier, out var itemPatchVb));
            Assert.True(itemPatchVb.GetProperty("isPatchOverride").GetBoolean());
            Assert.False(itemPatchVb.GetProperty("isCustomBorrowShell").GetBoolean());
            Assert.Equal("Item", itemPatchVb.GetProperty("cardType").GetString());
            Assert.Equal("ItemArt", itemPatchVb.GetProperty("visualTarget").GetString());
            Assert.Equal("魔改库克里A", itemPatchVb.GetProperty("titleZh").GetString());
            Assert.Equal(88, itemPatchVb.GetProperty("tierAttributes").GetProperty("Bronze").GetProperty("DamageAmount").GetDouble());
            Assert.True(itemPatchVb.TryGetProperty("counterDisplays", out var counters) && counters.ValueKind == JsonValueKind.Array);
            Assert.Contains(counters.EnumerateArray(), c =>
                c.GetProperty("labelZh").GetString() == "灵能"
                && c.GetProperty("syncAttribute").GetString() == "Custom_0"
                && c.GetProperty("maxValue").GetInt32() == 5);

            // 3.4.2 Skill 原位 Patch：保持真实 CardType="Skill" 与 VisualTarget="SkillIcon"，绝不退化为 Item/ItemArt
            Assert.True(vbs.TryGetProperty(skillId, out var skillPatchVb));
            Assert.True(skillPatchVb.GetProperty("isPatchOverride").GetBoolean());
            Assert.False(skillPatchVb.GetProperty("isCustomBorrowShell").GetBoolean());
            Assert.Equal("Skill", skillPatchVb.GetProperty("cardType").GetString());
            Assert.Equal("SkillIcon", skillPatchVb.GetProperty("visualTarget").GetString());
            Assert.Equal("魔改技能B", skillPatchVb.GetProperty("titleZh").GetString());

            // 3.5 cardOverrides 通过隔离 ModRegistry.ExportMergedLocalization() 完整保留自动合成的中文词条
            var overrides = root.GetProperty("cardOverrides");
            Assert.True(overrides.TryGetProperty("bundle-dep-a:dep_blade", out var depCardOv));
            Assert.Equal("依赖灵刃", depCardOv.GetProperty("title").GetString());
            Assert.True(overrides.TryGetProperty(SmallItemCarrier, out var itemOv));
            Assert.Equal("魔改库克里A", itemOv.GetProperty("title").GetString());
            Assert.True(overrides.TryGetProperty(skillId, out var skillOv));
            Assert.Equal("魔改技能B", skillOv.GetProperty("title").GetString());
        }
        finally
        {
            SafeDeleteDir(tempRoot);
        }
    }
}
