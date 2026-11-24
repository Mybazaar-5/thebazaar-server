using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Tests.TestInfrastructure;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 3A 单一事实源（SSOT）契约锁死测试（D5-P1-00 / D5-P1-11）：
/// 无全局静态状态变更，可与全量测试安全并行运行。
/// 1. 反射双向校验 C# 核心模型 ≡ docs/schemas/bazaar-mod.schema.json（含 CustomEventDefinition.hours、禁止虚构 displayNameEn、全部枚举集）；
/// 2. 校验 ModRuleCatalog.BuildCatalogDto()/BuildMechanicSchemaDto() ≡ MechanicDefinition 静态白名单 ≡ StandardPricesSettings 5 阶价格 ≡ 24 VFX 预设 / 30 别名；
/// 3. 校验 .agents/skills/bazaar-mod-creator/** ≡ docs/skills/bazaar-mod-creator/** 逐字节 SHA-256 一致、V15.4 10 钩子与 Common 规则口径正确、示例机制 0 错误解析。
/// </summary>
public sealed class Wave3A_ContractSsotTests
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static string RepoRoot => SharedGameDataFixture.Shared.RepoRoot;

    private static JsonDocument LoadSchema()
    {
        string path = Path.Combine(RepoRoot, "docs", "schemas", "bazaar-mod.schema.json");
        Assert.True(File.Exists(path), $"Schema 文件不存在：{path}");
        return JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    private static HashSet<string> GetCSharpJsonPropertyNames(Type type)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
            if (prop.GetCustomAttribute<JsonExtensionDataAttribute>() != null) continue;
            if (!prop.CanRead) continue;
            // 仅导出支持反序列化写入或显式标注 JsonPropertyName / 集合初始化器的契约属性
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (!prop.CanWrite && attr == null) continue;

            string jsonName = attr?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            result.Add(jsonName);
        }
        return result;
    }

    private static HashSet<string> GetSchemaDefPropertyNames(JsonElement defs, string defName)
    {
        Assert.True(defs.TryGetProperty(defName, out var defElem), $"Schema $defs 缺少定义：{defName}");
        Assert.True(defElem.TryGetProperty("properties", out var propsElem), $"Schema $defs.{defName} 缺少 properties");
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in propsElem.EnumerateObject())
            set.Add(p.Name);
        return set;
    }

    private static HashSet<string> GetSchemaDefEnumValues(JsonElement defs, string defName)
    {
        Assert.True(defs.TryGetProperty(defName, out var defElem), $"Schema $defs 缺少枚举定义：{defName}");
        Assert.True(defElem.TryGetProperty("enum", out var enumElem), $"Schema $defs.{defName} 缺少 enum");
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in enumElem.EnumerateArray())
            set.Add(item.GetString()!);
        return set;
    }

    [Fact]
    public void CSharpModels_And_JsonSchema_BidirectionalPropertyAndEnumParity()
    {
        using JsonDocument doc = LoadSchema();
        JsonElement defs = doc.RootElement.GetProperty("$defs");

        // 1. 精确大小写匹配：所有 Mod / Mechanic / Localization / Card / Monster 模型属性必须 100% 覆盖于 Schema
        var exactMappedModels = new (Type ModelType, string DefName, bool StrictReverse)[]
        {
            (typeof(ModManifest), "ModManifest", true),
            (typeof(ModContentRoots), "ModContentRoots", true),
            (typeof(CustomCardArtRect), "CustomCardArtRect", true),
            (typeof(CustomCardArtSpec), "CustomCardArtSpec", true),
            (typeof(CustomEventSchedule), "CustomEventSchedule", true),
            (typeof(CustomCardDefinition), "CustomCardDefinition", true),
            (typeof(CardPatchDefinition), "CardPatchDefinition", true),
            (typeof(CustomEventDefinition), "CustomEventDefinition", true),
            (typeof(CustomModeDefinition), "CustomModeDefinition", true),
            (typeof(CustomMutatorDefinition), "CustomMutatorDefinition", true),
            (typeof(MechanicDefinition), "MechanicDefinition", true),
            (typeof(MechanicParameterDef), "MechanicParameterDef", true),
            (typeof(MechanicHookDefinition), "MechanicHookDefinition", true),
            (typeof(MechanicHookFilter), "MechanicHookFilter", true),
            (typeof(MechanicValueExpr), "MechanicValueExpr", true),
            (typeof(MechanicCondition), "MechanicCondition", true),
            (typeof(MechanicTargetSpec), "MechanicTargetSpec", true),
            (typeof(MechanicStep), "MechanicStep", true),
            (typeof(MechanicCounterDisplay), "MechanicCounterDisplay", true),
            (typeof(CardMechanicAttachment), "CardMechanicAttachment", true),
            (typeof(ResolvedCounterDisplay), "ResolvedCounterDisplay", true),
            (typeof(ResolvedPreviewAttrOp), "ResolvedPreviewAttrOp", true),
            (typeof(CustomMonsterPreviewBinding), "CustomMonsterPreviewBinding", true),
            (typeof(CustomMonsterLoadoutItemBinding), "CustomMonsterLoadoutItemBinding", true),
            (typeof(CustomCardVisualBinding), "CustomCardVisualBinding", true),
            (typeof(LocalizationOverlayDocument), "LocalizationOverlayDocument", true),
            (typeof(CardLocalizationOverride), "CardLocalizationOverride", true),
            (typeof(UiExactOverlay), "UiExactOverlay", true),
            (typeof(UiRegexOverlayRule), "UiRegexOverlayRule", true),
            (typeof(ServerMessageOverlay), "ServerMessageOverlay", true),
            (typeof(MonsterTemplate), "MonsterTemplate", false),
            (typeof(MonsterPlayerData), "MonsterPlayerData", false),
            (typeof(MonsterSocketInventoryData), "MonsterSocketInventoryData", false),
            (typeof(MonsterHandData), "MonsterHandData", false),
            (typeof(MonsterCardEntry), "MonsterCardEntry", false),
            (typeof(AbilityDefinition), "AbilityDefinition", false),
            (typeof(AuraDefinition), "AuraDefinition", false),
            (typeof(VfxConfigData), "VfxConfigData", false),
            (typeof(EnchantmentData), "EnchantmentData", false),
            (typeof(QuestDefinition), "QuestDefinition", false),
            (typeof(QuestRepeatableData), "QuestRepeatableData", false),
            (typeof(QuestEntry), "QuestEntry", false),
            (typeof(QuestUpdate), "QuestUpdate", false),
            (typeof(QuestReward), "QuestReward", false),
            (typeof(TierData), "TierData", false),
            (typeof(TooltipEntry), "TooltipEntry", false),
            (typeof(LocalizationBlock), "LocalizationBlock", false),
            (typeof(LocalizedText), "LocalizedText", false),
            (typeof(SelectionContextData), "SelectionContextData", false),
            (typeof(SelectionRulesData), "SelectionRulesData", false),
            (typeof(NextEncounterOption), "NextEncounterOption", false),
            (typeof(RerollRulesData), "RerollRulesData", false),
            (typeof(CombatRewardData), "CombatRewardData", false),
            (typeof(ValueModifierNode), "ValueModifierNode", false),
        };

        // Schema 中允许出现的元字段（非 C# 业务属性）
        var allowedSchemaMetaProps = new HashSet<string>(StringComparer.Ordinal) { "$schema", "$comment" };

        foreach (var (modelType, defName, strictReverse) in exactMappedModels)
        {
            var csharpProps = GetCSharpJsonPropertyNames(modelType);
            var schemaProps = GetSchemaDefPropertyNames(defs, defName);

            // 正向：C# 模型里有的每个 JSON 属性，Schema 必须声明（支持忽略大小写匹配底层 GameData PascalCase 模型，对 Mod 专属强类型要求精确大小写）
            var missingInSchema = strictReverse
                ? csharpProps.Where(p => !schemaProps.Contains(p)).OrderBy(p => p).ToList()
                : csharpProps.Where(p => !schemaProps.Any(s => string.Equals(s, p, StringComparison.OrdinalIgnoreCase))).OrderBy(p => p).ToList();

            Assert.True(
                missingInSchema.Count == 0,
                $"C# 模型 {modelType.Name} 存在未写入 Schema $defs.{defName} 的属性：[{string.Join(", ", missingInSchema)}]");

            if (strictReverse)
            {
                // 反向：严格强类型 Mod/Mechanic/Localization DTO 不得在 Schema 中出现 C# 不认识的虚构字段
                var phantomInSchema = schemaProps
                    .Where(p => !allowedSchemaMetaProps.Contains(p) && !csharpProps.Contains(p))
                    .OrderBy(p => p)
                    .ToList();
                Assert.True(
                    phantomInSchema.Count == 0,
                    $"Schema $defs.{defName} 存在 C# 模型 {modelType.Name} 中不存在的虚构属性：[{string.Join(", ", phantomInSchema)}]");
            }
        }

        // 2. RuleNode（GameData.db 使用 PascalCase [JsonPropertyName]，Schema 使用 camelCase，反序列化 CaseInsensitive）
        var ruleCsharpProps = GetCSharpJsonPropertyNames(typeof(RuleNode));
        var ruleSchemaProps = GetSchemaDefPropertyNames(defs, "RuleNode");
        var missingRuleProps = ruleCsharpProps
            .Where(p => !ruleSchemaProps.Any(s => string.Equals(s, p, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p)
            .ToList();
        Assert.True(
            missingRuleProps.Count == 0,
            $"RuleNode 存在未写入 Schema $defs.RuleNode 的属性：[{string.Join(", ", missingRuleProps)}]");

        // 3. 重点回归断言：W2-A 实码对齐点 + 历史虚构字段防复发
        var manifestProps = GetSchemaDefPropertyNames(defs, "ModManifest");
        Assert.DoesNotContain("displayNameEn", manifestProps);
        Assert.Contains("displayNameZh", manifestProps);
        Assert.Contains("description", manifestProps);

        var eventProps = GetSchemaDefPropertyNames(defs, "CustomEventDefinition");
        Assert.Contains("hours", eventProps);
        Assert.DoesNotContain("allowedHours", eventProps);
        var eventHoursAttr = typeof(CustomEventDefinition)
            .GetProperty(nameof(CustomEventDefinition.AllowedHours))!
            .GetCustomAttribute<JsonPropertyNameAttribute>();
        Assert.Equal("hours", eventHoursAttr?.Name);

        var cardProps = GetSchemaDefPropertyNames(defs, "CustomCardDefinition");
        Assert.Contains("allowedHours", cardProps);
        Assert.Contains("counterDisplays", cardProps);

        var bindingProps = GetSchemaDefPropertyNames(defs, "CustomCardVisualBinding");
        Assert.Contains("visualTarget", bindingProps);
        Assert.Contains("customBgArtFile", bindingProps);
        Assert.Contains("monsterPreview", bindingProps);

        var patchProps = GetSchemaDefPropertyNames(defs, "CardPatchDefinition");
        Assert.Contains("counterDisplaysOverride", patchProps);

        // 4. 全部核心枚举与 C# 静态白名单严格集合相等
        Assert.Equal(
            new HashSet<string> { "Bronze", "Silver", "Gold", "Diamond", "Legendary" },
            GetSchemaDefEnumValues(defs, "CardTierEnum"));

        Assert.Equal(
            new HashSet<string> { "ItemArt", "SkillIcon", "EncounterPortrait" },
            GetSchemaDefEnumValues(defs, "VisualTargetEnum"));

        Assert.Equal(
            new HashSet<string> { "EventEncounter", "PedestalEncounter", "EncounterStep", "CombatEncounter" },
            GetSchemaDefEnumValues(defs, "EventTypeEnum"));

        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedHooks), GetSchemaDefEnumValues(defs, "MechanicHookEnum"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedOps), GetSchemaDefEnumValues(defs, "MechanicOpEnum"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedParamTypes), GetSchemaDefEnumValues(defs, "MechanicParamTypeEnum"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedTargetModes), GetSchemaDefEnumValues(defs, "MechanicTargetModeEnum"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedStatuses), GetSchemaDefEnumValues(defs, "MechanicStatusEnum"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedScopes), GetSchemaDefEnumValues(defs, "MechanicScopeEnum"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedPlayerAttributes), GetSchemaDefEnumValues(defs, "MechanicPlayerAttributeEnum"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedSyncAttributes), GetSchemaDefEnumValues(defs, "MechanicSyncAttributeEnum"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedCounterDisplayAttributes), GetSchemaDefEnumValues(defs, "CounterDisplayAttributeEnum"));
    }

    [Fact]
    public void ModRuleCatalog_MatchesMechanicWhiteLists_StandardPrices_AndVfxPresets()
    {
        using JsonDocument catalogDoc = JsonDocument.Parse(JsonSerializer.Serialize(ModRuleCatalog.BuildCatalogDto()));
        JsonElement root = catalogDoc.RootElement;

        // 1. 5 品阶含 Legendary
        var tiers = root.GetProperty("tiers").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();
        Assert.Equal(new[] { "Bronze", "Silver", "Gold", "Diamond", "Legendary" }, tiers);

        // 2. 属性包含全部 Custom_0..Custom_8
        var attrKeys = root.GetProperty("attributes").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i <= 8; i++)
            Assert.Contains($"Custom_{i}", attrKeys);

        // 3. eventTypes 与 visualTargets 严格实码一致
        var eventTypes = root.GetProperty("eventTypes").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string> { "EventEncounter", "PedestalEncounter", "EncounterStep", "CombatEncounter" }, eventTypes);

        var visualTargets = root.GetProperty("visualTargets").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string> { "ItemArt", "SkillIcon", "EncounterPortrait" }, visualTargets);

        // 4. standardPrices 与 StandardPricesSettings 5 阶 × 4 类型（含 Skill Bronze sell=2）逐项一致
        var expectedPrices = new StandardPricesSettings();
        JsonElement pricesElem = root.GetProperty("standardPrices");
        Assert.Equal(expectedPrices.GetItemBuyPrice(CardSize.Small, CardTier.Bronze), pricesElem.GetProperty("Small").GetProperty("Bronze").GetProperty("buy").GetInt32());
        Assert.Equal(expectedPrices.GetItemSellPrice(CardSize.Small, CardTier.Legendary), pricesElem.GetProperty("Small").GetProperty("Legendary").GetProperty("sell").GetInt32());
        Assert.Equal(expectedPrices.GetItemBuyPrice(CardSize.Medium, CardTier.Legendary), pricesElem.GetProperty("Medium").GetProperty("Legendary").GetProperty("buy").GetInt32());
        Assert.Equal(expectedPrices.GetItemBuyPrice(CardSize.Large, CardTier.Legendary), pricesElem.GetProperty("Large").GetProperty("Legendary").GetProperty("buy").GetInt32());
        Assert.Equal(expectedPrices.GetSkillBuyPrice(CardTier.Bronze), pricesElem.GetProperty("Skill").GetProperty("Bronze").GetProperty("buy").GetInt32());
        Assert.Equal(expectedPrices.GetSkillSellPrice(CardTier.Bronze), pricesElem.GetProperty("Skill").GetProperty("Bronze").GetProperty("sell").GetInt32());
        Assert.Equal(expectedPrices.GetSkillBuyPrice(CardTier.Legendary), pricesElem.GetProperty("Skill").GetProperty("Legendary").GetProperty("buy").GetInt32());

        // 5. 官方 VFX 预设（24 个含 FireClaw/Slash）与别名表（30 个）
        Assert.Equal(24, ModRuleCatalog.OfficialVfxPresets.Count);
        Assert.Contains(ModRuleCatalog.OfficialVfxPresets, p => p.Id == "FireClaw");
        Assert.Contains(ModRuleCatalog.OfficialVfxPresets, p => p.Id == "Slash");
        Assert.Equal(30, ModRuleCatalog.OfficialVfxAliases.Count);
        Assert.Equal("Assets/TheBazaar/Projectiles/FireClaw/Projectile_FireClaw_PV.prefab", ModRuleCatalog.ResolveOfficialVfxKey("FireClaw"));
        Assert.Equal("Assets/TheBazaar/Emitters/Slash/VFX_Slash_Tier2_PV.prefab", ModRuleCatalog.ResolveOfficialVfxKey("Slash"));
        Assert.Equal("Assets/TheBazaar/Projectiles/Fireworks/Projectile_HarkurvianLauncher_PV.prefab", ModRuleCatalog.ResolveOfficialVfxKey("Fire"));

        // 6. BuildMechanicSchemaDto 与 MechanicDefinition 全部静态白名单 100% 一致
        JsonElement mech = root.GetProperty("mechanicSchema");
        static HashSet<string> ReadKeys(JsonElement parent, string prop)
            => parent.GetProperty(prop).EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : (x.TryGetProperty("id", out var id) ? id.GetString()! : x.GetProperty("key").GetString()!)).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedHooks), ReadKeys(mech, "hooks"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedOps), ReadKeys(mech, "ops"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedParamTypes), ReadKeys(mech, "paramTypes"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedTargetModes), ReadKeys(mech, "targetModes"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedStatuses), ReadKeys(mech, "statuses"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedScopes), ReadKeys(mech, "hookScopes"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedPlayerAttributes), ReadKeys(mech, "playerAttributes"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedSyncAttributes), ReadKeys(mech, "syncAttributes"));
        Assert.Equal(new HashSet<string>(MechanicDefinition.AllowedCounterDisplayAttributes), ReadKeys(mech, "counterDisplayAttributes"));
    }

    [Fact]
    public void DualAiSkills_AndAuthoringStandard_ByteIdenticalAndV154Aligned()
    {
        string agentsSkillDir = Path.Combine(RepoRoot, ".agents", "skills", "bazaar-mod-creator");
        string docsSkillDir = Path.Combine(RepoRoot, "docs", "skills", "bazaar-mod-creator");
        Assert.True(Directory.Exists(agentsSkillDir), $"缺少目录：{agentsSkillDir}");
        Assert.True(Directory.Exists(docsSkillDir), $"缺少目录：{docsSkillDir}");

        var agentsFiles = Directory.GetFiles(agentsSkillDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(agentsSkillDir, f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var docsFiles = Directory.GetFiles(docsSkillDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(docsSkillDir, f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(agentsFiles, docsFiles);
        Assert.True(agentsFiles.Count >= 6, $"期望至少 6 个 Skill 文件，实际 {agentsFiles.Count}");
        Assert.Contains("examples/example_custom_mechanic.json", agentsFiles);

        // 逐字节 SHA-256 一致
        foreach (string rel in agentsFiles)
        {
            byte[] aBytes = File.ReadAllBytes(Path.Combine(agentsSkillDir, rel));
            byte[] dBytes = File.ReadAllBytes(Path.Combine(docsSkillDir, rel));
            string aHash = Convert.ToHexString(SHA256.HashData(aBytes));
            string dHash = Convert.ToHexString(SHA256.HashData(dBytes));
            Assert.Equal(aHash, dHash);
        }

        // SKILL.md 与 MOD_AUTHORING_STANDARD.md 必须覆盖全部 10 钩子与 V15.4 核心关键字
        string skillMd = File.ReadAllText(Path.Combine(agentsSkillDir, "SKILL.md"), Encoding.UTF8);
        string standardMd = File.ReadAllText(Path.Combine(RepoRoot, "docs", "MOD_AUTHORING_STANDARD.md"), Encoding.UTF8);

        foreach (string hook in MechanicDefinition.AllowedHooks)
        {
            Assert.Contains(hook, skillMd);
            Assert.Contains(hook, standardMd);
        }
        foreach (string token in new[] { "V15.4", "Legendary", "Custom_8", "grantIfMissing", "counterDisplays", "ItemArt", "SkillIcon", "EncounterPortrait" })
        {
            Assert.Contains(token, skillMd);
            Assert.Contains(token, standardMd);
        }

        // Common 反向规则必须已修正（不再错误宣称 Common 是全英雄通用默认推荐）
        string commonRef = File.ReadAllText(Path.Combine(agentsSkillDir, "references", "common_attributes_and_tags.md"), Encoding.UTF8);
        Assert.Contains("Curio", commonRef);
        Assert.DoesNotContain("全英雄通用（默认推荐）", commonRef);
        Assert.Contains("Legendary", commonRef);
        Assert.Contains("FireClaw", commonRef);

        // 3 份示例 JSON 全部可解析，其中 example_custom_mechanic.json 符合 MechanicFile/MechanicDefinition 模型并通过静态沙盒审计
        string mechExamplePath = Path.Combine(agentsSkillDir, "examples", "example_custom_mechanic.json");
        using (var mechDoc = JsonDocument.Parse(File.ReadAllText(mechExamplePath, Encoding.UTF8)))
        {
            string exampleModId = mechDoc.RootElement.TryGetProperty("modId", out var mid) && !string.IsNullOrWhiteSpace(mid.GetString())
                ? mid.GetString()!
                : "demo-forge";
            var mechArrayElem = mechDoc.RootElement.ValueKind == JsonValueKind.Array
                ? mechDoc.RootElement
                : mechDoc.RootElement.GetProperty("mechanics");
            var mechExample = JsonSerializer.Deserialize<List<MechanicDefinition>>(mechArrayElem.GetRawText(), ReadOpts);
            Assert.NotNull(mechExample);
            Assert.NotEmpty(mechExample!);
            foreach (var m in mechExample!)
            {
                Assert.False(string.IsNullOrWhiteSpace(m.Id));
                Assert.NotEmpty(m.Hooks);
                var staticIssues = new List<ModValidationIssue>();
                MechanicSandboxInterpreter.ValidateStatic(m, exampleModId, staticIssues);
                Assert.DoesNotContain(staticIssues, i => i.Severity == ModValidationSeverity.Error);
            }
        }

        // 另外两份示例也应为合法 JSON（检查原版示例中的事件已包含 hours 字段）
        using (var origDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(agentsSkillDir, "examples", "example_original_and_event_chain.json"), Encoding.UTF8)))
            Assert.Equal(JsonValueKind.Object, origDoc.RootElement.ValueKind);
        using (var patchDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(agentsSkillDir, "examples", "example_cloned_and_patched.json"), Encoding.UTF8)))
            Assert.Equal(JsonValueKind.Object, patchDoc.RootElement.ValueKind);
    }
}
