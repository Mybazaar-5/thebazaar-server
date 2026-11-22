using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Mods;

/// <summary>
/// V13.1 工业级 Mod 防 Bug 10 道铁闸校验与 Kahn 拓扑排序引擎：
/// 1. Schema 结构、废字段拦截与 tiers.*.abilityIds 完整性；
/// 2. ID 命名空间防撞与 Kahn 拓扑排序防循环依赖；
/// 3. baseVisualTemplateId 白名单 + 同 Type + Item 同 Size 硬校验；
/// 4. 全量 $type / Trigger / Prerequisite / 属性名递归白名单（杜绝 UnknownRuleTypes）；
/// 5. 冷却 [1000,60000]ms / 数值上界 / 光环倍率 / 自充能死循环静态拦截；
/// 6. 奇遇跳转图三色 DFS 防环、防悬空与防死胡同；
/// 7. 中文描述完整性与 {attr.*} 四阶真渲染 0 "?"；
/// 8. 规范 PNG 魔数 / 64~1024px / <= 2MB / SHA-256（严拒 Unity 不解码的 WebP）；
/// 9. 隔离 Overlay 50 场真实 CombatSimulator 压力体检（0 异常 / 单场 <= 200ms）；
/// 10. 幽灵池隔离非 standard + UnknownRuleTypes == 0 + 安全 wire 映射 + RollbackToBase 零残留。
/// </summary>
public static partial class ModValidator
{
    public const long MaxAssetBytes = 2L * 1024 * 1024; // 2 MB
    public const long MaxTotalAssetsBytes = 32L * 1024 * 1024; // 32 MB
    public const int MinAssetDimension = 64;
    public const int MaxAssetWidth = 1024;
    public const int MaxAssetHeight = 1024;
    public const double MinCooldownMs = 1000.0;
    public const double MaxRecommendedCooldownMs = 60_000.0;
    public const double MaxSafeAttributeValue = 100_000.0;
    public const double MinSafeAttributeValue = -10_000.0;
    public const double MinSafeAuraMultiplier = 0.05;
    public const double MaxSafeAuraMultiplier = 10.0;

    private static readonly HashSet<string> ValidSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Small", "Medium", "Large"
    };

    private static readonly HashSet<string> ValidTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bronze", "Silver", "Gold", "Diamond", "Legendary"
    };

    private static readonly HashSet<string> ValidScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "singleplayer-only", "multiplayer-safe", "cosmetic-only"
    };

    private static readonly HashSet<string> ForbiddenLegacyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "effects", "costGold", "rewardGold", "rewardXp", "chainEvent", "grantCards"
    };

    private static readonly HashSet<string> IgnoredMetaExtensionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "$schema", "note", "comment", "baseVisualNote", "tier", "displayNameEn", "affectsStandardGhostPool"
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$|^[a-z0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModIdRegex();

    [GeneratedRegex(@"\{([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderTokenRegex();

    [GeneratedRegex(@"<sprite(?:\s+name)?\s*=\s*(?:""([^""]+)""|'([^']+)'|([^\s>/]+))[^>]*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TooltipSpriteTagRegex();

    [GeneratedRegex(@"<link\s*=\s*(?:""([^""]+)""|'([^']+)'|([^\s>/]+))[^>]*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TooltipLinkTagRegex();

    public static bool IsValidModId(string? modId)
        => !string.IsNullOrWhiteSpace(modId) && ModIdRegex().IsMatch(modId.Trim());

    /// <summary>
    /// 校验并按依赖拓扑顺序排序全部候选 ModPackage（执行全部静态 8 闸门拦截，供 ModRegistry 装载与热重载调用）。
    /// </summary>
    public static (List<ModPackage> ValidOrderedPackages, List<ModValidationIssue> Issues, List<string> RejectedModIds) ValidateAndSort(
        IReadOnlyList<ModPackage> packages,
        GameDataRepository baseRepo,
        IReadOnlySet<string>? validVisualWhitelist = null)
    {
        var issues = new List<ModValidationIssue>();
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 清单基础校验与 ModId 唯一性检查
        var pkgById = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < packages.Count; i++)
        {
            var pkg = packages[i];
            var m = pkg.Manifest;
            string modId = (m.Id ?? string.Empty).Trim();

            if (!IsValidModId(modId))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    string.IsNullOrWhiteSpace(modId) ? $"<unknown-{i}>" : modId,
                    "INVALID_MOD_ID",
                    $"Mod ID '{m.Id}' 格式非法，必须为 2~64 位小写字母、数字或连字符（禁止路径穿越字符）。",
                    GateId: 2,
                    FixHintZh: "将 mod.json 中的 id 改为小写字母、数字和连字符，例如 starport-forge。"));
                rejected.Add(string.IsNullOrWhiteSpace(modId) ? $"<unknown-{i}>" : modId);
                continue;
            }

            if (pkgById.ContainsKey(modId))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    modId,
                    "DUPLICATE_MOD_ID",
                    $"检测到重复的 Mod ID '{modId}'，已拒绝重复包。",
                    GateId: 2,
                    FixHintZh: "删除重复目录或为其中一个 Mod 更换唯一 id。"));
                rejected.Add(modId);
                continue;
            }

            if (m.SchemaVersion != 1)
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    modId,
                    "UNSUPPORTED_SCHEMA_VERSION",
                    $"Mod '{modId}' 的 schemaVersion={m.SchemaVersion} 不受支持，当前标准为 1。",
                    GateId: 1,
                    FixHintZh: "在 mod.json 中将 schemaVersion 设为 1。"));
                rejected.Add(modId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(m.Name))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Warning,
                    modId,
                    "MISSING_MOD_NAME",
                    $"Mod '{modId}' 缺少 name 展示名称，将回退使用 Mod ID。",
                    GateId: 1));
                m.Name = modId;
            }

            if (!ValidScopes.Contains(m.Scope ?? string.Empty))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    modId,
                    "INVALID_MOD_SCOPE",
                    $"Mod '{modId}' 的 scope '{m.Scope}' 非法，仅允许 singleplayer-only | multiplayer-safe | cosmetic-only。",
                    GateId: 1,
                    FixHintZh: "将 mod.json 中的 scope 改为 multiplayer-safe、singleplayer-only 或 cosmetic-only。"));
                rejected.Add(modId);
                continue;
            }

            pkgById[modId] = pkg;
        }

        // 2. 依赖完整性检查与 Kahn 拓扑排序（含循环依赖精准检测）
        var topoOrdered = TopologicalSort(pkgById, issues, rejected);

        // 3. 逐包执行静态 1~8 闸门深度校验
        var globalCustomCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalCustomModeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalCustomMonsterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validPackages = new List<ModPackage>(topoOrdered.Count);

        foreach (var pkg in topoOrdered)
        {
            string modId = pkg.Manifest.Id;
            var pkgIssues = new List<ModValidationIssue>();
            var localCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localModeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localMonsterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            RunGate1Schema(pkg, pkgIssues);
            RunGate2IdsAndNamespace(pkg, baseRepo, globalCustomCardIds, globalCustomModeIds, globalCustomMonsterIds, localCardIds, localModeIds, localMonsterIds, pkgIssues);
            RunGate3VisualWhitelist(pkg, baseRepo, validVisualWhitelist, pkgIssues);
            RunGate4RuleTypeWhitelist(pkg, pkgIssues, validPackages);
            RunGate5NumericAndLoopSafety(pkg, baseRepo, pkgIssues);
            RunGate6EncounterDag(pkg, baseRepo, localCardIds, pkgIssues);
            RunGate7LocalizationAndTooltips(pkg, baseRepo, pkgIssues);
            RunGate8Assets(pkg, pkgIssues);

            issues.AddRange(pkgIssues);

            if (pkgIssues.Any(i => i.Severity == ModValidationSeverity.Error))
            {
                rejected.Add(modId);
                continue;
            }

            foreach (var cid in localCardIds) globalCustomCardIds.Add(cid);
            foreach (var mid in localModeIds) globalCustomModeIds.Add(mid);
            foreach (var monId in localMonsterIds) globalCustomMonsterIds.Add(monId);
            validPackages.Add(pkg);
        }

        return (validPackages, issues, rejected.ToList());
    }

    /// <summary>
    /// 针对单个目标 Mod 执行完整的 **10 道工业级防 Bug 铁闸**（静态 1~8 + 动态 50 场真战斗 9 + 隔离回归不变量 10），
    /// 输出标准化 <see cref="ModTenGateValidationReport"/> 供 Web 创意工坊、CLI 与自动化测试统一消费。
    /// </summary>
    public static ModTenGateValidationReport RunTenGateValidation(
        ModPackage targetPkg,
        IReadOnlyList<ModPackage>? allContextPackages,
        GameDataRepository baseRepo,
        IReadOnlySet<string>? validVisualWhitelist = null,
        int combatMatches = 50,
        int combatSeed = 2026131,
        bool runDynamicGates = true)
    {
        ArgumentNullException.ThrowIfNull(targetPkg);
        ArgumentNullException.ThrowIfNull(baseRepo);

        var totalSw = Stopwatch.StartNew();
        string modId = string.IsNullOrWhiteSpace(targetPkg.Manifest.Id) ? "<unknown>" : targetPkg.Manifest.Id.Trim();
        var gates = new List<ModGateResult>(10);

        // 组装同级上下文（不含本包自身，用于检测与其他已安装 Mod 的 ID 撞名与依赖拓扑）
        var otherPackages = (allContextPackages ?? Array.Empty<ModPackage>())
            .Where(p => !ReferenceEquals(p, targetPkg) && !string.Equals(p.Manifest.Id, modId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var globalCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalModeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalMonsterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in otherPackages)
        {
            foreach (var c in other.Cards) if (!string.IsNullOrWhiteSpace(c.CustomId)) globalCardIds.Add(c.CustomId.Trim());
            foreach (var ev in other.Events)
            {
                if (!string.IsNullOrWhiteSpace(ev.EventId)) globalCardIds.Add(ev.EventId.Trim());
                foreach (var st in ev.Steps) if (!string.IsNullOrWhiteSpace(st.CustomId)) globalCardIds.Add(st.CustomId.Trim());
                if (ev.CombatMonster != null && !string.IsNullOrWhiteSpace(ev.CombatMonster.Id)) globalMonsterIds.Add(ev.CombatMonster.Id.Trim());
            }
            foreach (var mon in other.Monsters) if (!string.IsNullOrWhiteSpace(mon.Id)) globalMonsterIds.Add(mon.Id.Trim());
            foreach (var md in other.Modes) if (!string.IsNullOrWhiteSpace(md.ModeId)) globalModeIds.Add(md.ModeId.Trim());
        }

        var localCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localModeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localMonsterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ModGateResult ExecuteGate(int gateId, string key, string nameZh, Action<List<ModValidationIssue>> runner)
        {
            var gsw = Stopwatch.StartNew();
            var list = new List<ModValidationIssue>();
            try
            {
                runner(list);
            }
            catch (Exception ex)
            {
                list.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    modId,
                    $"GATE_{gateId}_EXCEPTION",
                    $"闸门 #{gateId} 执行异常：{ex.Message}",
                    GateId: gateId));
            }
            gsw.Stop();
            string status = list.Any(i => i.Severity == ModValidationSeverity.Error)
                ? "FAIL"
                : (list.Any(i => i.Severity == ModValidationSeverity.Warning) ? "WARN" : "PASS");
            return new ModGateResult
            {
                GateId = gateId,
                Key = key,
                NameZh = nameZh,
                Status = status,
                DurationMs = gsw.ElapsedMilliseconds,
                Issues = list
            };
        }

        // Gate #1: Schema 结构与字段完整性
        gates.Add(ExecuteGate(1, "schema_structure", "闸门 1：Schema 结构、废字段与四阶绑定完整性", list =>
        {
            if (targetPkg.Manifest.SchemaVersion != 1)
                list.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "UNSUPPORTED_SCHEMA_VERSION",
                    $"schemaVersion={targetPkg.Manifest.SchemaVersion} 非法，必须为 1。", GateId: 1, FixHintZh: "将 mod.json 中的 schemaVersion 设为 1。"));
            if (!ValidScopes.Contains(targetPkg.Manifest.Scope ?? string.Empty))
                list.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_MOD_SCOPE",
                    $"scope '{targetPkg.Manifest.Scope}' 非法，仅允许 singleplayer-only | multiplayer-safe | cosmetic-only。", GateId: 1, FixHintZh: "请修改 mod.json 中的 scope 字段。"));
            if (string.IsNullOrWhiteSpace(targetPkg.Manifest.Name))
                list.Add(new ModValidationIssue(ModValidationSeverity.Warning, modId, "MISSING_MOD_NAME",
                    "mod.json 缺少展示名称 name。", GateId: 1, FixHintZh: "填写清晰的中文 Mod 名称。"));
            RunGate1Schema(targetPkg, list);
        }));

        // Gate #2: ID 命名空间与 Kahn 拓扑排序防环
        gates.Add(ExecuteGate(2, "id_namespace_and_deps", "闸门 2：ID 全局命名空间防撞与 Kahn 拓扑依赖", list =>
        {
            if (!IsValidModId(modId))
            {
                list.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_MOD_ID",
                    $"Mod ID '{targetPkg.Manifest.Id}' 格式非法，必须为 2~64 位小写字母、数字或连字符。",
                    GateId: 2, FixHintZh: "改为如 starport-forge 的小写短横线格式。"));
            }
            // 依赖拓扑检查（包含上下文已有 Mod + 本 Mod）
            var topoMap = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in otherPackages)
            {
                if (IsValidModId(p.Manifest.Id)) topoMap[p.Manifest.Id.Trim()] = p;
            }
            if (IsValidModId(modId)) topoMap[modId] = targetPkg;
            var topoIssues = new List<ModValidationIssue>();
            var topoRejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TopologicalSort(topoMap, topoIssues, topoRejected);
            foreach (var ti in topoIssues)
            {
                if (string.Equals(ti.ModId, modId, StringComparison.OrdinalIgnoreCase))
                    list.Add(ti);
            }

            RunGate2IdsAndNamespace(targetPkg, baseRepo, globalCardIds, globalModeIds, globalMonsterIds, localCardIds, localModeIds, localMonsterIds, list);
        }));

        // Gate #3: baseVisualTemplateId 白名单 + 同 Type + Item 同 Size
        gates.Add(ExecuteGate(3, "visual_whitelist_and_size", "闸门 3：白名单视觉载体与同 Type/Size 防白块穿模", list =>
        {
            RunGate3VisualWhitelist(targetPkg, baseRepo, validVisualWhitelist, list);
        }));

        // Gate #4: 全量 $type / Trigger / Prerequisite / 属性名递归白名单 + 开放自定义沙盒机制审计
        gates.Add(ExecuteGate(4, "rule_type_whitelist", "闸门 4：全量 $type / 触发器 / 前置条件与自定义机制白名单", list =>
        {
            RunGate4RuleTypeWhitelist(targetPkg, list, otherPackages);
        }));

        // Gate #5: 冷却 / 数值 / 倍率 / 自充能死循环安全边界
        gates.Add(ExecuteGate(5, "numeric_and_loop_safety", "闸门 5：冷却毫秒 / 数值上界 / 倍率与自充能防死循环", list =>
        {
            RunGate5NumericAndLoopSafety(targetPkg, baseRepo, list);
        }));

        // Gate #6: 奇遇跳转图三色 DFS 防环与防死胡同
        gates.Add(ExecuteGate(6, "encounter_dag_integrity", "闸门 6：奇遇事件链三色 DFS 防环与死胡同检查", list =>
        {
            RunGate6EncounterDag(targetPkg, baseRepo, localCardIds, list);
        }));

        // Gate #7: 中文描述与 {attr.*} 四阶真渲染 0 "?"
        gates.Add(ExecuteGate(7, "localization_and_placeholders", "闸门 7：中文描述与 {attr.*} 四阶真渲染 0 问号", list =>
        {
            RunGate7LocalizationAndTooltips(targetPkg, baseRepo, list);
        }));

        // Gate #8: PNG 魔数 / 64~1024px / <= 2MB / SHA-256（拒绝 WebP）
        gates.Add(ExecuteGate(8, "png_asset_integrity", "闸门 8：PNG 魔数 / 分辨率 / 体积与 SHA-256 校验", list =>
        {
            RunGate8Assets(targetPkg, list);
        }));

        ModCombatStressReport? stressReport = null;

        if (!runDynamicGates)
        {
            gates.Add(new ModGateResult { GateId = 9, Key = "combat_simulator_stress", NameZh = "闸门 9：50 场真实 CombatSimulator 压力体检", Status = "SKIP" });
            gates.Add(new ModGateResult { GateId = 10, Key = "isolation_and_regression_invariants", NameZh = "闸门 10：幽灵池隔离、未知规则归零与回滚零残留", Status = "SKIP" });
        }
        else
        {
            // 构建隔离 Overlay 用于 Gate #9（即使某条静态规则如 Gate #7 缺文案有 Error，只要能构建隔离 Overlay 仍继续跑战斗体检帮助作者一次定位全部问题）
            bool canBuildOverlay = gates.Take(3).All(g => g.Status != "FAIL")
                && gates[3].Status != "FAIL"; // Gate #4 含非法 $type 时不应硬跑

            // Gate #9: 50 场真实 CombatSimulator 沙盒压力体检
            if (!canBuildOverlay)
            {
                gates.Add(new ModGateResult
                {
                    GateId = 9,
                    Key = "combat_simulator_stress",
                    NameZh = "闸门 9：50 场真实 CombatSimulator 压力体检",
                    Status = "SKIP",
                    Issues =
                    [
                        new ModValidationIssue(ModValidationSeverity.Info, modId, "SIM_SKIPPED_STATIC_ERROR",
                            "因前置静态闸门（#1~#4）未通过，暂跳过 50 场真战斗体检；修复上述静态错误后自动运行。",
                            GateId: 9)
                    ]
                });
            }
            else
            {
                gates.Add(ExecuteGate(9, "combat_simulator_stress", "闸门 9：50 场真实 CombatSimulator 压力体检", list =>
                {
                    var overlayRepo = BuildIsolatedOverlayForPackage(targetPkg, baseRepo);
                    var candidateMechs = new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase);
                    foreach (var op in otherPackages)
                        foreach (var om in op.Mechanics)
                            if (!string.IsNullOrWhiteSpace(om.Id)) candidateMechs[om.Id.Trim()] = om;
                    foreach (var tm in targetPkg.Mechanics)
                        if (!string.IsNullOrWhiteSpace(tm.Id)) candidateMechs[tm.Id.Trim()] = tm;
                    var (sr, simIssues) = ModCombatStressTester.RunStressTest(targetPkg, overlayRepo, combatMatches, combatSeed, candidateMechs);
                    stressReport = sr;
                    list.AddRange(simIssues);
                }));
            }

            // Gate #10: 幽灵池隔离 + UnknownRuleTypes == 0 + 安全 wire 映射 + RollbackToBase 零残留
            gates.Add(ExecuteGate(10, "isolation_and_regression_invariants", "闸门 10：幽灵池隔离、未知规则归零与回滚零残留", list =>
            {
                RunGate10IsolationAndInvariants(targetPkg, otherPackages, baseRepo, validVisualWhitelist, list);
            }));
        }

        totalSw.Stop();
        int errorCount = gates.Sum(g => g.Issues.Count(i => i.Severity == ModValidationSeverity.Error));
        int warningCount = gates.Sum(g => g.Issues.Count(i => i.Severity == ModValidationSeverity.Warning));
        bool passed = errorCount == 0 && gates.All(g => g.Status != "FAIL");

        string summaryZh = passed
            ? (runDynamicGates
                ? $"10 道防 Bug 铁闸全部通过（0 错误，{warningCount} 建议），{stressReport?.MatchesRun ?? combatMatches} 场真实战斗压力体检稳定通过，可安全发布热重载。"
                : $"静态 8 道铁闸全部通过（0 错误，{warningCount} 建议）。")
            : $"校验未通过：共发现 {errorCount} 处阻断级错误、{warningCount} 条优化建议，请按下方红色闸门提示修复后重试。";

        return new ModTenGateValidationReport
        {
            Ok = true,
            Passed = passed,
            ModId = modId,
            ValidatedAtUtc = DateTimeOffset.UtcNow,
            TotalDurationMs = totalSw.ElapsedMilliseconds,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            SummaryZh = summaryZh,
            Gates = gates,
            CombatStressReport = stressReport
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // Gate #1 ~ Gate #8 & Gate #10 精确实现
    // ════════════════════════════════════════════════════════════════════════

    private static void RunGate1Schema(ModPackage pkg, List<ModValidationIssue> issues)
    {
        string modId = pkg.Manifest.Id;
        var roots = pkg.Manifest.ContentRoots ?? new ModContentRoots();
        CheckSafeContentRoot(modId, "contentRoots.cards", roots.Cards, issues);
        CheckSafeContentRoot(modId, "contentRoots.patches", roots.Patches, issues);
        CheckSafeContentRoot(modId, "contentRoots.events", roots.Events, issues);
        CheckSafeContentRoot(modId, "contentRoots.modes", roots.Modes, issues);
        CheckSafeContentRoot(modId, "contentRoots.localization", roots.Localization, issues);
        CheckSafeContentRoot(modId, "contentRoots.assets", roots.Assets, issues);

        void CheckCardSchema(CustomCardDefinition card)
        {
            string cid = string.IsNullOrWhiteSpace(card.CustomId) ? "<unnamed-card>" : card.CustomId.Trim();
            CheckLegacyExtensionFields(modId, cid, card.ExtensionData, issues);

            if (string.IsNullOrWhiteSpace(card.Type) || !CustomCardDefinition.SupportedCardTypes.Contains(card.Type))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_CARD_TYPE",
                    $"自定义卡 '{cid}' 的 type '{card.Type}' 不受支持（支持：Item/Skill/EventEncounter/EncounterStep/PedestalEncounter/CombatEncounter）。",
                    cid, GateId: 1, FixHintZh: "将 type 修正为合法的卡牌类型枚举值。"));
            }
            if (string.IsNullOrWhiteSpace(card.Size) || !ValidSizes.Contains(card.Size))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_CARD_SIZE",
                    $"自定义卡 '{cid}' 的 size '{card.Size}' 非法（必须为 Small/Medium/Large）。",
                    cid, GateId: 1, FixHintZh: "将 size 设为 Small、Medium 或 Large。"));
            }
            if (string.IsNullOrWhiteSpace(card.StartingTier) || !ValidTiers.Contains(card.StartingTier))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_STARTING_TIER",
                    $"自定义卡 '{cid}' 的 startingTier '{card.StartingTier}' 非法。",
                    cid, GateId: 1, FixHintZh: "将 startingTier 设为 Bronze/Silver/Gold/Diamond/Legendary 之一。"));
            }

            if (card.Tiers is { Count: > 0 })
            {
                bool anyAbilityRef = false;
                bool anyAuraRef = false;
                foreach (var (tierKey, td) in card.Tiers)
                {
                    if (!ValidTiers.Contains(tierKey))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_TIER_KEY",
                            $"自定义卡 '{cid}' 的 tiers 包含非法品阶键 '{tierKey}'。",
                            cid, GateId: 1, FixHintZh: "品阶键仅允许 Bronze/Silver/Gold/Diamond/Legendary。"));
                    }
                    if (td.AbilityIds.Count > 0) anyAbilityRef = true;
                    if (td.AuraIds.Count > 0) anyAuraRef = true;

                    foreach (var abId in td.AbilityIds)
                    {
                        if (!card.Abilities.ContainsKey(abId))
                        {
                            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "TIER_ABILITY_ID_NOT_FOUND",
                                $"自定义卡 '{cid}' 的 {tierKey} 阶 abilityIds 引用了不存在的 ability '{abId}'。",
                                cid, GateId: 1, FixHintZh: $"检查 tiers.{tierKey}.abilityIds 或在 abilities 中补齐键 '{abId}'。"));
                        }
                    }
                    foreach (var auId in td.AuraIds)
                    {
                        if (!card.Auras.ContainsKey(auId))
                        {
                            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "TIER_AURA_ID_NOT_FOUND",
                                $"自定义卡 '{cid}' 的 {tierKey} 阶 auraIds 引用了不存在的 aura '{auId}'。",
                                cid, GateId: 1, FixHintZh: $"检查 tiers.{tierKey}.auraIds 或在 auras 中补齐键 '{auId}'。"));
                        }
                    }
                }

                if (card.Abilities.Count > 0 && !anyAbilityRef)
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Warning, modId, "TIER_ABILITY_IDS_EMPTY",
                        $"自定义卡 '{cid}' 声明了 tiers 与 abilities，但所有阶均未填写 abilityIds（运行时已兜底自动激活，建议显式补齐以符合 V13.1 标准）。",
                        cid, GateId: 1, FixHintZh: "为每个 tiers.* 显式补齐 \"abilityIds\": [\"0\", ...] 或在工坊点击「一键自动修复」。"));
                }
                if (card.Auras.Count > 0 && !anyAuraRef)
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Warning, modId, "TIER_AURA_IDS_EMPTY",
                        $"自定义卡 '{cid}' 声明了 tiers 与 auras，但所有阶均未填写 auraIds（建议显式补齐以符合 V13.1 标准）。",
                        cid, GateId: 1, FixHintZh: "为每个 tiers.* 显式补齐 \"auraIds\": [\"0\", ...]。"));
                }
            }
        }

        foreach (var c in pkg.Cards) CheckCardSchema(c);
        foreach (var ev in pkg.Events)
        {
            string evId = string.IsNullOrWhiteSpace(ev.EventId) ? "<unnamed-event>" : ev.EventId.Trim();
            CheckLegacyExtensionFields(modId, evId, ev.ExtensionData, issues);
            if (!double.IsFinite(ev.Weight) || ev.Weight < 0.0)
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_EVENT_WEIGHT",
                    $"自定义奇遇 '{evId}' 的 weight={ev.Weight} 非法（必须为 >= 0 的有限数值）。",
                    evId, GateId: 1, FixHintZh: "设置 weight > 0 作为常规白昼抽签相对权重，或设为 0 表示仅由前置奇遇跳转进入。"));
            }
            if (ev.MinDay < 1 || ev.MaxDay < 1 || ev.MinDay > ev.MaxDay)
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_EVENT_DAY_WINDOW",
                    $"自定义奇遇 '{evId}' 的天数窗 [{ev.MinDay}, {ev.MaxDay}] 非法（必须满足 1 <= minDay <= maxDay）。",
                    evId, GateId: 1, FixHintZh: "将 minDay/maxDay 调整为合法范围，例如 minDay=1, maxDay=99。"));
            }
            CheckCardSchema(ev.ToCardDefinition());
            foreach (var step in ev.Steps)
                CheckCardSchema(step);
        }
        foreach (var patch in pkg.Patches)
        {
            string pid = string.IsNullOrWhiteSpace(patch.TargetTemplateId) ? "<unnamed-patch>" : patch.TargetTemplateId.Trim();
            CheckLegacyExtensionFields(modId, pid, patch.ExtensionData, issues);
            if (!string.IsNullOrWhiteSpace(patch.StartingTier) && !ValidTiers.Contains(patch.StartingTier))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_STARTING_TIER",
                    $"原位补丁 '{pid}' 的 startingTier '{patch.StartingTier}' 非法。", pid, GateId: 1));
            }
            if (patch.TierAttributeOverrides != null)
            {
                foreach (var tk in patch.TierAttributeOverrides.Keys)
                {
                    if (!ValidTiers.Contains(tk))
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_TIER_KEY",
                            $"原位补丁 '{pid}' 包含非法品阶键 '{tk}'。", pid, GateId: 1));
                }
            }
        }
    }

    private static void CheckSafeContentRoot(string modId, string fieldName, string? pathValue, List<ModValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(pathValue)) return;
        string normalized = NormalizeLogicalPath(pathValue).Replace("*", "x");
        if (!IsSafeRelativePath(normalized))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "SCHEMA_PATH_TRAVERSAL",
                $"mod.json 中 {fieldName}='{pathValue}' 包含越界目录穿越或绝对路径。", fieldName, GateId: 1,
                FixHintZh: "所有 contentRoots 必须使用 Mod 目录内的相对路径（不得包含 '..' 或盘符）。"));
        }
    }

    private static void CheckLegacyExtensionFields(string modId, string targetId, Dictionary<string, JsonElement>? ext, List<ModValidationIssue> issues)
    {
        if (ext == null || ext.Count == 0) return;
        foreach (var key in ext.Keys)
        {
            if (IgnoredMetaExtensionKeys.Contains(key)) continue;
            if (ForbiddenLegacyFields.Contains(key))
            {
                string hint = key.Equals("effects", StringComparison.OrdinalIgnoreCase)
                    ? "EncounterStep 引擎不执行虚构的 effects 字段，请改用标准 abilities（trigger: TTriggerOnCardSelected + TActionPlayerModifyAttribute / TActionGameSpawnCards / TActionCardUpgrade）！"
                    : (key.Equals("costGold", StringComparison.OrdinalIgnoreCase)
                        ? "请移除虚构字段 costGold，改在 attributes 中配置 \"BuyPrice\": <金币数>。"
                        : $"请移除非引擎字段 '{key}'，按 docs/schemas/bazaar-mod.schema.json 规范编写。");

                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    modId,
                    "UNSUPPORTED_LEGACY_FIELD",
                    $"条目 '{targetId}' 包含引擎不会执行的非标准虚构字段 '{key}'。",
                    targetId,
                    GateId: 1,
                    FixHintZh: hint));
            }
        }
    }

    private static void RunGate2IdsAndNamespace(
        ModPackage pkg,
        GameDataRepository baseRepo,
        HashSet<string> globalCustomCardIds,
        HashSet<string> globalCustomModeIds,
        HashSet<string> globalCustomMonsterIds,
        HashSet<string> localCardIds,
        HashSet<string> localModeIds,
        HashSet<string> localMonsterIds,
        List<ModValidationIssue> issues)
    {
        string modId = pkg.Manifest.Id;
        string requiredPrefix = modId + ":";

        void ValidateNewCardId(CustomCardDefinition card)
        {
            if (string.IsNullOrWhiteSpace(card.CustomId))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "MISSING_CUSTOM_CARD_ID",
                    "自定义卡牌/事件/步骤缺少 customId 字段。", GateId: 2,
                    FixHintZh: $"填写形如 '{requiredPrefix}my_card' 的命名空间唯一 ID。"));
                return;
            }

            string cid = card.CustomId.Trim();
            card.CustomId = cid;

            if (!cid.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ID_NAMESPACE_VIOLATION",
                    $"自定义条目 ID '{cid}' 必须以 '{requiredPrefix}' 为命名空间前缀，防止多 Mod 撞名。",
                    cid, GateId: 2, FixHintZh: $"将 ID 改为 '{requiredPrefix}{cid.Replace(':', '_')}'。"));
            }

            if (baseRepo.CardsById.ContainsKey(cid) || globalCustomCardIds.Contains(cid) || !localCardIds.Add(cid))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "DUPLICATE_CARD_ID",
                    $"自定义卡牌 ID '{cid}' 与官方原版卡或其他 Mod 冲突。",
                    cid, GateId: 2, FixHintZh: "如果要原位修改原版卡，请改写到 patches/vanilla_patches.json；如果是新卡请更换唯一 ID。"));
            }
        }

        foreach (var c in pkg.Cards) ValidateNewCardId(c);
        foreach (var ev in pkg.Events)
        {
            ValidateNewCardId(ev.ToCardDefinition());
            foreach (var st in ev.Steps) ValidateNewCardId(st);
            if (ev.CombatMonster != null)
                ValidateNewMonsterId(ev.CombatMonster.Id, ev.EventId, modId, requiredPrefix, baseRepo, globalCustomMonsterIds, localMonsterIds, issues);
        }
        foreach (var m in pkg.Monsters)
            ValidateNewMonsterId(m.Id, m.Id, modId, requiredPrefix, baseRepo, globalCustomMonsterIds, localMonsterIds, issues);

        var localPatchTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in pkg.Patches)
        {
            if (string.IsNullOrWhiteSpace(patch.TargetTemplateId))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "PATCH_TARGET_MISSING",
                    "原位补丁缺少 targetTemplateId 字段。", GateId: 2, FixHintZh: "填写目标原版卡的 GUID。"));
                continue;
            }
            string tid = patch.TargetTemplateId.Trim();
            patch.TargetTemplateId = tid;
            if (!baseRepo.CardsById.ContainsKey(tid))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "PATCH_TARGET_NOT_FOUND",
                    $"原位补丁目标 '{tid}' 在基础数据库 GameData.db 中不存在。",
                    tid, GateId: 2, FixHintZh: "在工坊「原版卡搜索」中检索合法原版卡 GUID 后再点击「原位改原版卡」。"));
            }
            if (!localPatchTargets.Add(tid))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Warning, modId, "DUPLICATE_PATCH_TARGET",
                    $"同一 Mod 内对原版卡 '{tid}' 声明了多条补丁，后者将覆盖前者同名字段。", tid, GateId: 2));
            }
        }

        foreach (var mode in pkg.Modes)
        {
            if (string.IsNullOrWhiteSpace(mode.ModeId))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_MODE_ID",
                    "自定义模式缺少 modeId。", GateId: 2));
                continue;
            }
            string mid = mode.ModeId.Trim();
            mode.ModeId = mid;
            if (!mid.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ID_NAMESPACE_VIOLATION",
                    $"自定义模式 ID '{mid}' 必须以 '{requiredPrefix}' 为前缀。", mid, GateId: 2));
            }
            if (globalCustomModeIds.Contains(mid) || !localModeIds.Add(mid))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "DUPLICATE_MODE_ID",
                    $"自定义模式 ID '{mid}' 重复。", mid, GateId: 2));
            }
            if (string.IsNullOrWhiteSpace(mode.NameZh))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Warning, modId, "MISSING_MODE_NAME",
                    $"自定义模式 '{mid}' 缺少 nameZh，将回退使用 modeId。", mid, GateId: 2));
                mode.NameZh = mid;
            }
        }
    }

    private static void ValidateNewMonsterId(
        string? monsterId,
        string ownerTarget,
        string modId,
        string requiredPrefix,
        GameDataRepository baseRepo,
        HashSet<string> globalCustomMonsterIds,
        HashSet<string> localMonsterIds,
        List<ModValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(monsterId))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_MONSTER_ID",
                $"自定义野怪（{ownerTarget}）缺少 Id。", ownerTarget, GateId: 2));
            return;
        }
        string mid = monsterId.Trim();
        if (!mid.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ID_NAMESPACE_VIOLATION",
                $"自定义野怪 ID '{mid}' 必须以 '{requiredPrefix}' 为前缀。", mid, GateId: 2));
        }
        if (baseRepo.MonstersById.ContainsKey(mid) || globalCustomMonsterIds.Contains(mid) || !localMonsterIds.Add(mid))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "DUPLICATE_MONSTER_ID",
                $"自定义野怪 ID '{mid}' 与已有野怪冲突。", mid, GateId: 2));
        }
    }

    private static void RunGate3VisualWhitelist(
        ModPackage pkg,
        GameDataRepository baseRepo,
        IReadOnlySet<string>? validVisualWhitelist,
        List<ModValidationIssue> issues)
    {
        string modId = pkg.Manifest.Id;

        void CheckCardCarrier(CustomCardDefinition card)
        {
            string cid = string.IsNullOrWhiteSpace(card.CustomId) ? "<unnamed>" : card.CustomId.Trim();
            if (string.IsNullOrWhiteSpace(card.BaseVisualTemplateId))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "MISSING_BASE_VISUAL_ID",
                    $"自定义卡 '{cid}' 必须声明合法的 baseVisualTemplateId 以防客户端白块。",
                    cid, GateId: 3, FixHintZh: "通过工坊搜索原版白名单卡或执行 python tools/mod_sdk_cli.py clone-card --search 选择同 Type 同 Size 的原版 GUID。"));
                return;
            }

            string baseId = card.BaseVisualTemplateId.Trim();
            card.BaseVisualTemplateId = baseId;

            if (validVisualWhitelist != null && !validVisualWhitelist.Contains(baseId))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "BASE_VISUAL_NOT_IN_WHITELIST",
                    $"自定义卡 '{cid}' 的 baseVisualTemplateId '{baseId}' 不在客户端合法资产白名单 valid_card_ids.json（3,368 张）中。",
                    cid, GateId: 3, FixHintZh: "切勿编造 GUID，请从工坊白名单检索库选取合法原版载体 GUID。"));
            }

            var baseTpl = baseRepo.GetCard(baseId);
            if (baseTpl == null)
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "BASE_VISUAL_NOT_FOUND",
                    $"自定义卡 '{cid}' 的 baseVisualTemplateId '{baseId}' 在 GameData.db 中不存在。",
                    cid, GateId: 3, FixHintZh: "请使用工坊或 CLI 检索真实存在的原版卡 GUID。"));
                return;
            }

            if (!string.Equals(baseTpl.Type, card.Type, StringComparison.Ordinal))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "BASE_VISUAL_TYPE_MISMATCH",
                    $"自定义卡 '{cid}' (Type={card.Type}) 与载体卡 '{baseId}' (Type={baseTpl.Type}) 类型不一致。",
                    cid, GateId: 3, FixHintZh: $"请选择 Type=\"{card.Type}\" 的原版载体卡。"));
            }

            if (string.Equals(card.Type, "Item", StringComparison.Ordinal)
                && !string.Equals(baseTpl.Size, card.Size, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "BASE_VISUAL_SIZE_MISMATCH",
                    $"自定义物品 '{cid}' (Size={card.Size}) 与载体物品 '{baseId}' (Size={baseTpl.Size}) 尺寸不一致，将导致 3D 卡框穿模。",
                    cid, GateId: 3, FixHintZh: $"请更换为 Size=\"{card.Size}\" 的原版白名单物品作为 baseVisualTemplateId。"));
            }

            if (card.Type is "Item" or "Skill" && string.IsNullOrWhiteSpace(baseTpl.ArtKey))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Warning, modId, "BASE_VISUAL_EMPTY_ARTKEY",
                    $"载体卡 '{baseId}' 的 ArtKey 为空，建议换用带完整原版卡面的载体模板。", cid, GateId: 3));
            }
        }

        foreach (var c in pkg.Cards) CheckCardCarrier(c);
        foreach (var ev in pkg.Events)
        {
            CheckCardCarrier(ev.ToCardDefinition());
            foreach (var st in ev.Steps) CheckCardCarrier(st);
        }
    }

    private static void RunGate4RuleTypeWhitelist(ModPackage pkg, List<ModValidationIssue> issues, IReadOnlyList<ModPackage>? contextPackages = null)
    {
        string modId = pkg.Manifest.Id;
        var jsonOpts = GameDatabaseLoader.JsonOptions;

        void CheckAbilityMap(string ownerId, Dictionary<string, AbilityDefinition>? abilities)
        {
            if (abilities == null) return;
            foreach (var (abKey, ab) in abilities)
            {
                if (ab == null) continue;
                string loc = $"{ownerId}.abilities.{abKey}";
                if (ab.Trigger != null)
                {
                    string trigType = ab.Trigger.Type ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(trigType)
                        || (!AbilityExecutor.IsKnownTriggerType(trigType) && !trigType.StartsWith("TTriggerMod_", StringComparison.Ordinal)))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "UNSUPPORTED_TRIGGER_TYPE",
                            $"规则 '{loc}' 使用了未受支持的触发器 $type='{trigType}'。",
                            ownerId, GateId: 4, FixHintZh: "请查阅 GET /api/workshop/rule-catalog 选用引擎支持的 TTrigger* 类型。"));
                    }
                    InspectRuleNodeRecursive(ab.Trigger, modId, ownerId, loc + ".trigger", jsonOpts, issues);
                }
                if (ab.Action != null)
                    InspectRuleNodeRecursive(ab.Action, modId, ownerId, loc + ".action", jsonOpts, issues);
                CheckPrerequisites(modId, ownerId, loc + ".prerequisites", ab.Prerequisites, jsonOpts, issues);
            }
        }

        void CheckAuraMap(string ownerId, Dictionary<string, AuraDefinition>? auras)
        {
            if (auras == null) return;
            foreach (var (auKey, au) in auras)
            {
                if (au == null) continue;
                string loc = $"{ownerId}.auras.{auKey}";
                if (au.Action != null)
                    InspectRuleNodeRecursive(au.Action, modId, ownerId, loc + ".action", jsonOpts, issues);
                CheckPrerequisites(modId, ownerId, loc + ".prerequisites", au.Prerequisites, jsonOpts, issues);
            }
        }

        void CheckCardRules(CustomCardDefinition card)
        {
            string cid = string.IsNullOrWhiteSpace(card.CustomId) ? "<unnamed>" : card.CustomId.Trim();
            CheckAbilityMap(cid, card.Abilities);
            CheckAuraMap(cid, card.Auras);
            if (card.Enchantments != null)
            {
                foreach (var (ek, ed) in card.Enchantments)
                {
                    if (ed == null) continue;
                    CheckAbilityMap($"{cid}.enchant.{ek}", ed.Abilities);
                    CheckAuraMap($"{cid}.enchant.{ek}", ed.Auras);
                }
            }
            if (card.Quests != null)
            {
                for (int qi = 0; qi < card.Quests.Count; qi++)
                {
                    var q = card.Quests[qi];
                    CheckPrerequisites(modId, cid, $"{cid}.quests[{qi}]", q.Prerequisites, jsonOpts, issues);
                    foreach (var qe in q.Entries)
                    {
                        CheckPrerequisites(modId, cid, $"{cid}.quests[{qi}].entry", qe.Prerequisites, jsonOpts, issues);
                        if (qe.Trigger != null) InspectRuleNodeRecursive(qe.Trigger, modId, cid, $"{cid}.quests[{qi}].trigger", jsonOpts, issues);
                        if (qe.Update?.Value != null) InspectRuleNodeRecursive(qe.Update.Value, modId, cid, $"{cid}.quests[{qi}].update", jsonOpts, issues);
                        if (qe.Reward != null)
                        {
                            CheckAbilityMap($"{cid}.quests[{qi}].reward", qe.Reward.Abilities);
                            CheckAuraMap($"{cid}.quests[{qi}].reward", qe.Reward.Auras);
                        }
                        CheckAbilityMap($"{cid}.quests[{qi}].completion", qe.CompletionEffects);
                    }
                }
            }
            if (card.SelectionContext?.SpawnContext != null)
                InspectRuleNodeRecursive(card.SelectionContext.SpawnContext, modId, cid, $"{cid}.selectionContext", jsonOpts, issues);
            if (card.SelectionRequirements != null)
                InspectRuleNodeRecursive(card.SelectionRequirements, modId, cid, $"{cid}.selectionRequirements", jsonOpts, issues);
            if (card.SelectionCriteria != null)
                InspectRuleNodeRecursive(card.SelectionCriteria, modId, cid, $"{cid}.selectionCriteria", jsonOpts, issues);
            if (card.CombatantType != null)
                InspectRuleNodeRecursive(card.CombatantType, modId, cid, $"{cid}.combatantType", jsonOpts, issues);
        }

        foreach (var c in pkg.Cards) CheckCardRules(c);
        foreach (var ev in pkg.Events)
        {
            CheckCardRules(ev.ToCardDefinition());
            foreach (var st in ev.Steps) CheckCardRules(st);
        }
        foreach (var p in pkg.Patches)
        {
            string pid = string.IsNullOrWhiteSpace(p.TargetTemplateId) ? "<patch>" : p.TargetTemplateId.Trim();
            CheckAbilityMap(pid, p.AbilitiesOverride);
            CheckAuraMap(pid, p.AurasOverride);
            if (p.EnchantmentOverrides != null)
            {
                foreach (var (ek, ed) in p.EnchantmentOverrides)
                {
                    if (ed == null) continue;
                    CheckAbilityMap($"{pid}.enchant.{ek}", ed.Abilities);
                    CheckAuraMap($"{pid}.enchant.{ek}", ed.Auras);
                }
            }
        }

        // V14 开放自定义沙盒机制静态审计与卡牌引用检查（所有静态问题归入闸门 #4）
        var knownMechanicIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalMechs = ModRuntimeHooks.GetRegisteredMechanics();
        if (globalMechs != null)
            foreach (var k in globalMechs.Keys) knownMechanicIds.Add(k);
        if (contextPackages != null)
            foreach (var cp in contextPackages)
                foreach (var cm in cp.Mechanics)
                    if (!string.IsNullOrWhiteSpace(cm.Id)) knownMechanicIds.Add(cm.Id.Trim());

        var localMechIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int mi = 0; mi < pkg.Mechanics.Count; mi++)
        {
            var mech = pkg.Mechanics[mi];
            if (mech == null) continue;
            var mechIssues = new List<ModValidationIssue>();
            MechanicSandboxInterpreter.ValidateStatic(mech, modId, mechIssues);
            foreach (var iss in mechIssues)
                issues.Add(iss with { GateId = 4 });

            if (!string.IsNullOrWhiteSpace(mech.Id))
            {
                string trimmed = mech.Id.Trim();
                if (!localMechIds.Add(trimmed))
                {
                    issues.Add(new ModValidationIssue(
                        ModValidationSeverity.Error, modId, "MECHANIC_DUPLICATE_ID",
                        $"Mod 内存在重复的自定义机制 ID '{trimmed}'。", trimmed, GateId: 4));
                }
                knownMechanicIds.Add(trimmed);
            }
        }

        void CheckAttachments(string ownerId, List<CardMechanicAttachment>? attachments)
        {
            if (attachments == null) return;
            for (int ai = 0; ai < attachments.Count; ai++)
            {
                var att = attachments[ai];
                if (att == null || !att.Enabled) continue;
                if (string.IsNullOrWhiteSpace(att.MechanicId) || !knownMechanicIds.Contains(att.MechanicId.Trim()))
                {
                    issues.Add(new ModValidationIssue(
                        ModValidationSeverity.Error, modId, "MECHANIC_REFERENCE_NOT_FOUND",
                        $"卡牌 '{ownerId}' 挂载了未注册的自定义机制 '{att.MechanicId}'。",
                        ownerId, GateId: 4,
                        FixHintZh: "请先在 mechanics/*.json 或工坊「机制工坊」中定义该机制，或确认依赖 Mod 已注册该 ID。"));
                }
            }
        }

        foreach (var c in pkg.Cards) CheckAttachments(c.CustomId, c.CustomMechanics);
        foreach (var p in pkg.Patches) CheckAttachments(p.TargetTemplateId, p.CustomMechanicsOverride);
    }

    private static void CheckPrerequisites(
        string modId,
        string targetId,
        string path,
        List<RuleNode>? prereqs,
        JsonSerializerOptions jsonOpts,
        List<ModValidationIssue> issues)
    {
        if (prereqs == null) return;
        for (int i = 0; i < prereqs.Count; i++)
        {
            var p = prereqs[i];
            if (p == null) continue;
            string pt = p.Type ?? string.Empty;
            if (!ModRuleCatalog.SupportedPrerequisiteTypes.Contains(pt))
            {
                string hint = string.Equals(pt, "TPrerequisitePlayerAttribute", StringComparison.Ordinal)
                    ? "引擎不存在 TPrerequisitePlayerAttribute！请改用 TPrerequisitePlayer，并在 subject 中放置 TTargetPlayerAbsolute + conditions: TPlayerConditionalAttribute。"
                    : "仅支持 4 种前置节点：TPrerequisitePlayer、TPrerequisiteCardCount、TPrerequisiteRun、TPrerequisiteCardAttributeComparator。";
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    modId,
                    "UNSUPPORTED_PREREQUISITE_TYPE",
                    $"前置条件 '{path}[{i}]' 使用了未实现的前置类型 '$type={pt}'，在战斗中会被忽略导致条件永远生效。",
                    targetId,
                    GateId: 4,
                    FixHintZh: hint));
            }
            InspectRuleNodeRecursive(p, modId, targetId, $"{path}[{i}]", jsonOpts, issues);
        }
    }

    private static void InspectRuleNodeRecursive(
        RuleNode node,
        string modId,
        string targetId,
        string path,
        JsonSerializerOptions jsonOpts,
        List<ModValidationIssue> issues,
        int depth = 0)
    {
        if (depth > 32) return;

        string typeName = (node.Type ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(typeName))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "MISSING_RULE_TYPE",
                $"规则节点 '{path}' 缺少必需的 \"$type\" 鉴别字段。", targetId, GateId: 4,
                FixHintZh: "为每个规则对象添加合法的 \"$type\" 字段。"));
        }
        else if (!RuleTypeRegistry.KnownTypes.Contains(typeName)
                 && !typeName.StartsWith("TActionMod_", StringComparison.Ordinal)
                 && !typeName.StartsWith("TTriggerMod_", StringComparison.Ordinal))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "UNKNOWN_RULE_TYPE",
                $"规则节点 '{path}' 使用了未登记的非法类型 \"$type\": \"{typeName}\"。",
                targetId, GateId: 4,
                FixHintZh: "请对照 /api/workshop/rule-catalog 或 docs/skills/bazaar-mod-creator/references/rule_types_cheatsheet.md 替换为官方已登记的 $type。"));
        }

        // 检查 ExtensionData 中的拼写错误/未知属性
        if (node.ExtensionData is { Count: > 0 })
        {
            foreach (var propKey in node.ExtensionData.Keys)
            {
                if (IgnoredMetaExtensionKeys.Contains(propKey)) continue;
                string? closest = ModRuleCatalog.FindClosestKnownRuleProperty(propKey, maxDistance: 2);
                string hint = closest != null
                    ? $"疑似拼写错误，你是不是想写 '{closest}'？"
                    : "请移除引擎不识别的自定义属性字段，以免逻辑静默失效。";
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    modId,
                    "UNKNOWN_RULE_PROPERTY",
                    $"规则节点 '{path}' ({typeName}) 包含引擎未识别的属性 '{propKey}'。",
                    targetId,
                    GateId: 4,
                    FixHintZh: hint));
            }
        }

        // 递归所有强类型及延迟 JsonElement 子节点
        void Visit(RuleNode? child, string suffix)
        {
            if (child != null) InspectRuleNodeRecursive(child, modId, targetId, $"{path}.{suffix}", jsonOpts, issues, depth + 1);
        }

        void VisitList(List<RuleNode>? children, string suffix)
        {
            if (children == null) return;
            for (int i = 0; i < children.Count; i++)
                Visit(children[i], $"{suffix}[{i}]");
        }

        Visit(node.ReferenceValue, "referenceValue");
        Visit(node.Target, "target");
        Visit(node.TargetPlayer, "targetPlayer");
        Visit(node.TargetCount, "targetCount");
        Visit(node.Source, "source");
        Visit(node.Subject, "subject");
        Visit(node.SubjectOther, "subjectOther");
        Visit(node.Duration, "duration");
        Visit(node.Cost, "cost");
        Visit(node.Limit, "limit");
        Visit(node.SpawnContext, "spawnContext");
        Visit(node.SelectionContextRules, "selectionContextRules");
        Visit(node.Modifier?.Value, "modifier.value");

        VisitList(node.Actions, "actions");
        VisitList(node.Triggers, "triggers");
        VisitList(node.Groups, "groups");
        VisitList(node.Filters, "filters");
        VisitList(node.Behaviors, "behaviors");
        CheckPrerequisites(modId, targetId, $"{path}.prerequisites", node.Prerequisites, jsonOpts, issues);

        if (node.Attributes != null)
        {
            foreach (var (ak, av) in node.Attributes)
                Visit(av, $"attributes.{ak}");
        }

        try { Visit(node.GetValueNode(jsonOpts), "value"); } catch { }
        try { Visit(node.GetComparisonValueNode(jsonOpts), "comparisonValue"); } catch { }
        try { VisitList(node.GetConditionList(jsonOpts), "conditions"); } catch { }
        try { VisitList(node.GetConstraintList(jsonOpts), "constraints"); } catch { }
    }

    private static void RunGate5NumericAndLoopSafety(ModPackage pkg, GameDataRepository baseRepo, List<ModValidationIssue> issues)
    {
        string modId = pkg.Manifest.Id;
        var jsonOpts = GameDatabaseLoader.JsonOptions;

        void ValidateAttrBag(string targetId, string scopeLabel, Dictionary<string, double>? attrs)
        {
            if (attrs == null) return;
            foreach (var (ak, av) in attrs)
            {
                if (!double.IsFinite(av))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_ATTR_VALUE",
                        $"'{targetId}' ({scopeLabel}) 属性 '{ak}' 包含 NaN 或 Infinity。", targetId, GateId: 5,
                        FixHintZh: "将所有数值改为有限实数。"));
                    continue;
                }
                if (av > MaxSafeAttributeValue || av < MinSafeAttributeValue)
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "NUMERIC_OUT_OF_SAFE_BOUNDS",
                        $"'{targetId}' ({scopeLabel}) 属性 '{ak}={av}' 超出安全区间 [{MinSafeAttributeValue}, {MaxSafeAttributeValue}]，存在 Int32 溢出风险。",
                        targetId, GateId: 5, FixHintZh: "将基础属性值收窄至合理战斗数值范围内。"));
                }
            }
        }

        void ValidateCooldownAndSelfLoop(
            string targetId,
            string cardType,
            Dictionary<string, double>? topAttrs,
            Dictionary<string, TierData>? tiers,
            Dictionary<string, AbilityDefinition>? abilities,
            Dictionary<string, AuraDefinition>? auras)
        {
            ValidateAttrBag(targetId, "attributes", topAttrs);
            var cooldowns = new List<(string Scope, double Value)>();
            var charges = new List<double>();

            if (topAttrs != null)
            {
                if (topAttrs.TryGetValue("CooldownMax", out var cd)) cooldowns.Add(("attributes", cd));
                if (topAttrs.TryGetValue("ChargeAmount", out var ch)) charges.Add(ch);
            }
            if (tiers != null)
            {
                foreach (var (tk, td) in tiers)
                {
                    ValidateAttrBag(targetId, $"tiers.{tk}", td.Attributes);
                    if (td.Attributes.TryGetValue("CooldownMax", out var cd)) cooldowns.Add(($"tiers.{tk}", cd));
                    if (td.Attributes.TryGetValue("ChargeAmount", out var ch)) charges.Add(ch);
                }
            }

            bool hasActiveFiredTrigger = abilities?.Values.Any(a =>
                string.Equals(a.Trigger?.Type, "TTriggerOnCardFired", StringComparison.Ordinal)) == true;

            if (string.Equals(cardType, "Item", StringComparison.Ordinal) && hasActiveFiredTrigger)
            {
                if (cooldowns.Count == 0)
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "COOLDOWN_MISSING",
                        $"主动开火物品 '{targetId}' 包含 TTriggerOnCardFired，但未声明 CooldownMax（毫秒）。",
                        targetId, GateId: 5, FixHintZh: "在 attributes 或各阶 tiers.*.attributes 中添加 CooldownMax（如 4000 表示 4 秒）。"));
                }
                else
                {
                    foreach (var (scope, cd) in cooldowns)
                    {
                        if (cd < MinCooldownMs)
                        {
                            string unitTip = (cd >= 1 && cd <= 60)
                                ? $"检测到 CooldownMax={cd}，疑似把「秒」当成「毫秒」！请改为 {cd * 1000:0}（单位必须为毫秒 ms）。"
                                : $"CooldownMax={cd}ms 低于引擎安全下限 {MinCooldownMs:0}ms（防 0ms 瞬爆与帧率崩溃），请设为 1000~60000 之间的毫秒数。";
                            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "COOLDOWN_TOO_LOW_OR_UNIT_ERROR",
                                $"物品 '{targetId}' ({scope}) 的 CooldownMax={cd} 低于安全下限 1000ms。",
                                targetId, GateId: 5, FixHintZh: unitTip));
                        }
                        else if (cd > MaxRecommendedCooldownMs)
                        {
                            issues.Add(new ModValidationIssue(ModValidationSeverity.Warning, modId, "COOLDOWN_TOO_HIGH",
                                $"物品 '{targetId}' ({scope}) 的 CooldownMax={cd}ms 超过 60 秒（在战斗沙暴前可能永远无法开火）。",
                                targetId, GateId: 5, FixHintZh: "建议将冷却调整至 1000~20000ms 区间。"));
                        }
                    }
                }
            }
            else
            {
                // 非主动开火物品若也显式写了 CooldownMax（如原位 Patch），仍拦截 <1000ms 单位写错
                foreach (var (scope, cd) in cooldowns)
                {
                    if (cd > 0 && cd < MinCooldownMs)
                    {
                        string unitTip = (cd >= 1 && cd <= 60)
                            ? $"检测到 CooldownMax={cd}，疑似把「秒」错写成了「毫秒」，请改为 {cd * 1000:0}。"
                            : "CooldownMax 必须落在 [1000, 60000] ms 范围内。";
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "COOLDOWN_TOO_LOW_OR_UNIT_ERROR",
                            $"卡牌 '{targetId}' ({scope}) 的 CooldownMax={cd} 低于安全下限 1000ms。",
                            targetId, GateId: 5, FixHintZh: unitTip));
                    }
                }
            }

            // 检查光环 Multiply 倍率上/下界
            if (auras != null)
            {
                foreach (var (auKey, au) in auras)
                {
                    if (au.Action == null) continue;
                    if (string.Equals(au.Action.Operation, "Multiply", StringComparison.OrdinalIgnoreCase))
                    {
                        var valNode = au.Action.GetValueNode(jsonOpts);
                        double? mult = valNode?.GetFixedNumericValue() ?? au.Action.GetFixedNumericValue();
                        if (mult.HasValue && (mult.Value < MinSafeAuraMultiplier || mult.Value > MaxSafeAuraMultiplier))
                        {
                            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "MULTIPLIER_EXPLOSION_RISK",
                                $"卡牌 '{targetId}' 光环 '{auKey}' 的乘法倍率 Multiply={mult.Value} 超出安全区间 [{MinSafeAuraMultiplier}, {MaxSafeAuraMultiplier}]。",
                                targetId, GateId: 5, FixHintZh: "将光环乘法倍率控制在 0.05 ~ 10.0 倍以内防止数值爆炸。"));
                        }
                    }
                }
            }

            // 静态死循环启发式：自身开火/自身使用 -> 自强发 ForceUse(Self) 或 自充能 Charge(Self) >= 0.9 * CooldownMax
            double minCd = cooldowns.Count > 0 ? cooldowns.Min(x => x.Value) : 4000.0;
            double maxChargeAttr = charges.Count > 0 ? charges.Max() : 0.0;
            if (abilities != null)
            {
                foreach (var (abKey, ab) in abilities)
                {
                    if (ab.Trigger == null || ab.Action == null) continue;
                    bool isSelfTrigger = string.Equals(ab.Trigger.Type, "TTriggerOnCardFired", StringComparison.Ordinal)
                        || (string.Equals(ab.Trigger.Type, "TTriggerOnItemUsed", StringComparison.Ordinal)
                            && (ab.Trigger.Subject == null || string.Equals(ab.Trigger.Subject.Type, "TTargetCardSelf", StringComparison.Ordinal)));
                    if (!isSelfTrigger) continue;

                    foreach (var act in FlattenActions(ab.Action))
                    {
                        bool targetsSelf = act.Target == null || string.Equals(act.Target.Type, "TTargetCardSelf", StringComparison.Ordinal);
                        if (!targetsSelf) continue;

                        if (string.Equals(act.Type, "TActionCardForceUse", StringComparison.Ordinal))
                        {
                            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "SELF_CHARGE_INFINITE_LOOP_RISK",
                                $"卡牌 '{targetId}' 能力 '{abKey}' 存在「自身触发 → 强制再次使用自身 (TActionCardForceUse)」无限递归风险。",
                                targetId, GateId: 5, FixHintZh: "请将触发对象改为相邻物品，或将目标改为其他卡牌。"));
                        }
                        else if (string.Equals(act.Type, "TActionCardCharge", StringComparison.Ordinal))
                        {
                            var valNode = act.GetValueNode(jsonOpts);
                            double chargeMs = valNode?.GetFixedNumericValue()
                                ?? act.GetFixedNumericValue()
                                ?? (maxChargeAttr > 0 ? maxChargeAttr : 0);
                            if (minCd > 0 && chargeMs >= minCd * 0.9)
                            {
                                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "SELF_CHARGE_INFINITE_LOOP_RISK",
                                    $"卡牌 '{targetId}' 能力 '{abKey}' 存在「自身开火 → 自充能 {chargeMs:0}ms（≥ 90% 冷却 {minCd:0}ms）」永动死循环风险。",
                                    targetId, GateId: 5, FixHintZh: "降低自充能时长至冷却的 50% 以下，或改为由相邻物品触发充能。"));
                            }
                        }
                    }
                }
            }
        }

        foreach (var c in pkg.Cards)
            ValidateCooldownAndSelfLoop(c.CustomId, c.Type, c.Attributes, c.Tiers, c.Abilities, c.Auras);

        foreach (var p in pkg.Patches)
        {
            var baseCard = baseRepo.GetCard(p.TargetTemplateId);
            var fakeTiers = p.TierAttributeOverrides?.ToDictionary(
                kv => kv.Key,
                kv => new TierData { Attributes = kv.Value },
                StringComparer.OrdinalIgnoreCase);
            ValidateCooldownAndSelfLoop(
                p.TargetTemplateId,
                baseCard?.Type ?? "Item",
                p.AttributeOverrides,
                fakeTiers,
                p.AbilitiesOverride ?? baseCard?.Abilities,
                p.AurasOverride ?? baseCard?.Auras);
        }
    }

    private static IEnumerable<RuleNode> FlattenActions(RuleNode root)
    {
        yield return root;
        if (string.Equals(root.Type, "TActionAnd", StringComparison.Ordinal) && root.Actions != null)
        {
            foreach (var sub in root.Actions)
            {
                if (sub == null) continue;
                foreach (var nested in FlattenActions(sub))
                    yield return nested;
            }
        }
    }

    private static void RunGate6EncounterDag(
        ModPackage pkg,
        GameDataRepository baseRepo,
        HashSet<string> localCardIds,
        List<ModValidationIssue> issues)
    {
        string modId = pkg.Manifest.Id;
        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var validLocalIds = new HashSet<string>(localCardIds, StringComparer.OrdinalIgnoreCase);
        foreach (var c in pkg.Cards) if (!string.IsNullOrWhiteSpace(c.CustomId)) validLocalIds.Add(c.CustomId.Trim());
        foreach (var ev in pkg.Events)
        {
            if (!string.IsNullOrWhiteSpace(ev.EventId)) validLocalIds.Add(ev.EventId.Trim());
            foreach (var st in ev.Steps) if (!string.IsNullOrWhiteSpace(st.CustomId)) validLocalIds.Add(st.CustomId.Trim());
        }

        void AddEdge(string from, string to)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return;
            if (!graph.TryGetValue(from, out var list))
            {
                list = new List<string>();
                graph[from] = list;
            }
            if (!list.Contains(to, StringComparer.OrdinalIgnoreCase))
                list.Add(to);
        }

        void CheckTransitions(string sourceId, List<NextEncounterOption>? nexts, SelectionContextData? selCtx)
        {
            if (nexts != null)
            {
                foreach (var opt in nexts)
                {
                    if (string.IsNullOrWhiteSpace(opt.Id)) continue;
                    string target = opt.Id.Trim();
                    AddEdge(sourceId, target);
                    if (!validLocalIds.Contains(target) && !baseRepo.CardsById.ContainsKey(target))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ENCOUNTER_DANGLING_TARGET",
                            $"奇遇/步骤 '{sourceId}' 跳转至不存在的目标遭遇 '{target}'。",
                            sourceId, GateId: 6, FixHintZh: "确认目标事件 ID 拼写无误，且已在本 Mod 或原版数据库中定义。"));
                    }
                }
            }
            if (selCtx?.Rules?.NextEncounterOnExit is { ValueKind: JsonValueKind.String } exitEl)
            {
                string? target = exitEl.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(target))
                {
                    AddEdge(sourceId, target);
                    if (!validLocalIds.Contains(target) && !baseRepo.CardsById.ContainsKey(target))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ENCOUNTER_DANGLING_TARGET",
                            $"奇遇 '{sourceId}' 退出后跳转目标 '{target}' 不存在。",
                            sourceId, GateId: 6));
                    }
                }
            }
        }

        foreach (var c in pkg.Cards)
            CheckTransitions(c.CustomId, c.NextEncounterOnSelection, c.SelectionContext);

        foreach (var ev in pkg.Events)
        {
            string evId = (ev.EventId ?? string.Empty).Trim();
            if (ev.MinDay > ev.MaxDay)
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ENCOUNTER_INVALID_DAY_RANGE",
                    $"奇遇 '{evId}' 的天数区间非法（minDay={ev.MinDay} > maxDay={ev.MaxDay}）。",
                    evId, GateId: 6, FixHintZh: "确保 minDay <= maxDay。"));
            }
            if (ev.Weight < 0)
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ENCOUNTER_INVALID_WEIGHT",
                    $"奇遇 '{evId}' 的权重 weight={ev.Weight} 不得为负数。",
                    evId, GateId: 6, FixHintZh: "将 weight 设为 >= 0（链式子事件可设 0，普通池事件设为正数）。"));
            }

            var evDef = ev.ToCardDefinition();
            CheckTransitions(evId, evDef.NextEncounterOnSelection, evDef.SelectionContext);

            if (string.Equals(ev.EventType, "EventEncounter", StringComparison.OrdinalIgnoreCase))
            {
                if (ev.Steps.Count == 0 && evDef.SelectionContext == null)
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ENCOUNTER_EMPTY_OPTIONS",
                        $"奇遇事件 '{evId}' 既没有定义 steps 也没有 selectionContext，进入后将导致玩家无选项可点软锁。",
                        evId, GateId: 6, FixHintZh: "至少为该奇遇添加 1 个 EncounterStep 子选项。"));
                }
            }

            foreach (var st in ev.Steps)
            {
                string stId = (st.CustomId ?? string.Empty).Trim();
                AddEdge(evId, stId);
                CheckTransitions(stId, st.NextEncounterOnSelection, st.SelectionContext);
            }
        }

        if (TryFindEncounterCycle(graph, out var cyclePath))
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                modId,
                "ENCOUNTER_CYCLE_DETECTED",
                $"检测到非法奇遇循环跳转：{string.Join(" -> ", cyclePath)}，禁止加载以防玩家死循环软锁。",
                cyclePath.FirstOrDefault(),
                GateId: 6,
                FixHintZh: "断开环路中的 nextEncounterOnSelection 边，确保事件链为严格有向无环图 (DAG)。"));
        }
    }

    private static void RunGate7LocalizationAndTooltips(ModPackage pkg, GameDataRepository baseRepo, List<ModValidationIssue> issues)
    {
        string modId = pkg.Manifest.Id;

        void CheckTextAndRender(
            string targetId,
            string? titleZh,
            string? descZh,
            List<string>? activeTips,
            List<string>? passiveTips,
            Dictionary<string, List<string>>? enchantTips,
            CardTemplate? templateForRender,
            bool requireTitleAndDesc)
        {
            if (requireTitleAndDesc)
            {
                if (string.IsNullOrWhiteSpace(titleZh))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "MISSING_ZH_TITLE",
                        $"条目 '{targetId}' 缺少中文标题 titleZh。", targetId, GateId: 7, FixHintZh: "填写清晰的中文名称 titleZh。"));
                }
                else if (ContainsPlaceholderStub(titleZh))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "UNFINISHED_ZH_TEXT",
                        $"条目 '{targetId}' 的 titleZh 包含未完成占位符（如 TODO/TBD）：'{titleZh}'。", targetId, GateId: 7));
                }

                if (string.IsNullOrWhiteSpace(descZh))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Warning, modId, "MISSING_ZH_DESC",
                        $"条目 '{targetId}' 缺少中文背景/说明 descZh。", targetId, GateId: 7, FixHintZh: "补充简短中文说明 descZh 提升游戏内体验。"));
                }
                else if (ContainsPlaceholderStub(descZh))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "UNFINISHED_ZH_TEXT",
                        $"条目 '{targetId}' 的 descZh 包含未完成占位符（TODO/TBD）。", targetId, GateId: 7));
                }
            }

            var allTips = new List<string>();
            if (activeTips != null) allTips.AddRange(activeTips);
            if (passiveTips != null) allTips.AddRange(passiveTips);
            if (enchantTips != null)
            {
                foreach (var kv in enchantTips)
                    if (kv.Value != null) allTips.AddRange(kv.Value);
            }

            foreach (var tip in allTips)
            {
                if (!AreBracesBalanced(tip))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "BROKEN_PLACEHOLDER_SYNTAX",
                        $"条目 '{targetId}' 的提示文本存在未闭合花括号：\"{tip}\"。",
                        targetId, GateId: 7, FixHintZh: "检查并配对所有 {attr.*} 占位符花括号。"));
                    continue;
                }
                foreach (Match m in PlaceholderTokenRegex().Matches(tip))
                {
                    string token = m.Groups[1].Value;
                    int dot = token.IndexOf('.');
                    string prefix = dot > 0 ? token[..dot] : string.Empty;
                    if (!prefix.Equals("attr", StringComparison.OrdinalIgnoreCase)
                        && !prefix.Equals("item", StringComparison.OrdinalIgnoreCase)
                        && !prefix.Equals("ability", StringComparison.OrdinalIgnoreCase)
                        && !prefix.Equals("aura", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "BROKEN_PLACEHOLDER_SYNTAX",
                            $"条目 '{targetId}' 的占位符 '{{{token}}}' 前缀非法（仅支持 attr/item/ability/aura）。",
                            targetId, GateId: 7, FixHintZh: "请改用如 {attr.DamageAmount} 或 {item.CooldownMax} 的标准占位符。"));
                    }
                }

                // P2-03：基于 GameData.db 官方 tooltips 表（136 条 TTooltipSymbol）校验富文本 <sprite> 图标名与 <link> 词条标识
                if (baseRepo.TooltipSymbolsById is { Count: > 0 })
                {
                    issues.AddRange(ValidateTooltipSymbolReferences(modId, targetId, tip, baseRepo.TooltipSymbolsById));
                }
            }

            if (baseRepo.TooltipSymbolsById is { Count: > 0 } && !string.IsNullOrWhiteSpace(descZh))
            {
                issues.AddRange(ValidateTooltipSymbolReferences(modId, targetId, descZh, baseRepo.TooltipSymbolsById));
            }

            // 对带提示文本的模板，逐品阶构造真实 CardInstance 调用 TooltipFormatter.FormatTooltips 真渲染，确保 0 "?"
            if (templateForRender != null && ((activeTips?.Count ?? 0) > 0 || (passiveTips?.Count ?? 0) > 0))
            {
                var tiersToRender = new List<CardTier>();
                if (templateForRender.Tiers is { Count: > 0 })
                {
                    foreach (var tk in templateForRender.Tiers.Keys)
                    {
                        if (Enum.TryParse<CardTier>(tk, true, out var parsedTier))
                            tiersToRender.Add(parsedTier);
                    }
                }
                if (tiersToRender.Count == 0)
                    tiersToRender.Add(TierHelper.ParseTier(templateForRender.StartingTier, CardTier.Bronze));

                foreach (var tier in tiersToRender.Distinct().OrderBy(t => t))
                {
                    var inst = new CardInstance(templateForRender, tier, enchantment: null, instanceId: $"preview_{targetId}_{tier}");
                    var rendered = TooltipFormatter.FormatTooltips(inst);
                    foreach (var line in rendered)
                    {
                        if (ContainsUnresolvedQuestionMark(line))
                        {
                            issues.Add(new ModValidationIssue(
                                ModValidationSeverity.Error,
                                modId,
                                "UNRESOLVED_TOOLTIP_PLACEHOLDER",
                                $"条目 '{targetId}' 在 {tier.ToChineseName()}({tier}) 阶真渲染 Tooltip 时出现未解析占位符 \"?\"：\"{line}\"。",
                                targetId,
                                GateId: 7,
                                FixHintZh: $"检查该卡在 tiers.{tier}.attributes 中是否补齐了 Tooltip 引用的所有 {{attr.*}} 属性键。"));
                        }
                    }
                }
            }
        }

        foreach (var card in pkg.Cards)
        {
            var baseTpl = baseRepo.GetCard(card.BaseVisualTemplateId) ?? new CardTemplate { Id = card.BaseVisualTemplateId, Type = card.Type, Size = card.Size };
            var renderedTpl = card.ToCardTemplate(baseTpl, modId);
            CheckTextAndRender(card.CustomId, card.TitleZh, card.DescZh, card.ActiveTooltipsZh, card.PassiveTooltipsZh, card.EnchantTooltipsZh, renderedTpl, requireTitleAndDesc: true);

            // V14.2 问题 2 提示：若常规可刷新商店物品仅勾选了 Common，提醒作者它只会出现在 Curio 等中立池而非杰杰等常规英雄商店
            if (string.Equals(card.Type, "Item", StringComparison.OrdinalIgnoreCase)
                && string.Equals(card.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase)
                && card.Heroes is { Count: 1 }
                && string.Equals(card.Heroes[0], HeroNames.Common, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Warning,
                    modId,
                    "COMMON_ONLY_SHOP_POOL",
                    $"物品 '{card.CustomId}' 仅选择了 Common（中立池），只会出现在 Curio 等中立商店，不会在杰杰等常规英雄商店刷新。",
                    card.CustomId,
                    GateId: 7,
                    FixHintZh: "若希望常规商店正常刷出，请在「所属英雄」中勾选目标英雄或点击「全英雄常规店通用」。"));
            }

            // V14.4 防呆提示：物品勾选了 Burn/Poison/Shield/Heal/Regen 等效果分类隐藏标签，但本体属性/原生动作/自定义机制均无对应施加效果
            if (string.Equals(card.Type, "Item", StringComparison.OrdinalIgnoreCase)
                && card.HiddenTags is { Count: > 0 })
            {
                CheckItemHiddenTagConsistency(pkg, modId, card, issues);
            }
        }

        foreach (var ev in pkg.Events)
        {
            CheckTextAndRender(ev.EventId, ev.TitleZh, ev.DescZh, null, null, null, null, requireTitleAndDesc: true);
            foreach (var st in ev.Steps)
                CheckTextAndRender(st.CustomId, st.TitleZh, st.DescZh, st.ActiveTooltipsZh, st.PassiveTooltipsZh, st.EnchantTooltipsZh, null, requireTitleAndDesc: true);
        }

        foreach (var p in pkg.Patches)
        {
            if (!p.HasVisualOrTextOverride) continue;
            var baseTpl = baseRepo.GetCard(p.TargetTemplateId);
            var patchedTpl = baseTpl != null ? p.ApplyToTemplate(baseTpl, modId) : null;
            CheckTextAndRender(p.TargetTemplateId, p.TitleZh, p.DescZh, p.ActiveTooltipsZh, p.PassiveTooltipsZh, p.EnchantTooltipsZh, patchedTpl, requireTitleAndDesc: false);
        }
    }

    private static bool ContainsPlaceholderStub(string text)
        => text.Contains("TODO", StringComparison.OrdinalIgnoreCase)
           || text.Contains("TBD", StringComparison.OrdinalIgnoreCase)
           || text.Any(ch => char.IsControl(ch) && ch != '\n' && ch != '\r' && ch != '\t');

    private static bool AreBracesBalanced(string text)
    {
        int depth = 0;
        foreach (char ch in text)
        {
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth < 0) return false;
            }
        }
        return depth == 0;
    }

    private static bool ContainsUnresolvedQuestionMark(string renderedLine)
    {
        // 只匹配孤立占位符替换产出的半角 '?'（避免误判正常中文问号 '？'）
        for (int i = 0; i < renderedLine.Length; i++)
        {
            if (renderedLine[i] == '?')
                return true;
        }
        return false;
    }

    /// <summary>
    /// P2-03：依据官方 GameData.db 的 136 条 <see cref="TooltipSymbolTemplate"/> 校验 Mod 文案中的
    /// `&lt;sprite name="..."&gt;` 图标引用与 `&lt;link="..."&gt;` 词条标识，及时告警拼写错误（对普通中文文案零误报）。
    /// </summary>
    public static List<ModValidationIssue> ValidateTooltipSymbolReferences(
        string modId,
        string targetId,
        string text,
        IReadOnlyDictionary<string, TooltipSymbolTemplate> tooltipSymbols)
    {
        var found = new List<ModValidationIssue>();
        if (string.IsNullOrWhiteSpace(text) || tooltipSymbols == null || tooltipSymbols.Count == 0)
            return found;

        bool hasSprite = text.Contains("<sprite", StringComparison.OrdinalIgnoreCase);
        bool hasLink = text.Contains("<link", StringComparison.OrdinalIgnoreCase);
        if (!hasSprite && !hasLink)
            return found;

        HashSet<string>? validIcons = null;
        HashSet<string>? validKeys = null;

        if (hasSprite)
        {
            validIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in tooltipSymbols.Values)
            {
                if (!string.IsNullOrWhiteSpace(sym.Icon)
                    && !string.Equals(sym.Icon, "Invalid", StringComparison.OrdinalIgnoreCase))
                {
                    validIcons.Add(sym.Icon.Trim());
                }
            }

            foreach (Match m in TooltipSpriteTagRegex().Matches(text))
            {
                string iconName = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
                // 允许纯数字索引（如 <sprite=0>）跳过名称表检查
                if (string.IsNullOrEmpty(iconName) || int.TryParse(iconName, out _))
                    continue;
                if (!validIcons.Contains(iconName))
                {
                    string sampleIcons = string.Join("、", validIcons.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(8));
                    found.Add(new ModValidationIssue(
                        ModValidationSeverity.Warning,
                        modId,
                        "UNKNOWN_TOOLTIP_SPRITE_ICON",
                        $"条目 '{targetId}' 的文案引用了未知的官方词条图标 '<sprite name=\"{iconName}\">'（不在官方 136 条 tooltips 图标库中）。",
                        targetId,
                        GateId: 7,
                        FixHintZh: $"请检查图标名拼写；常见官方有效图标包括：{sampleIcons} 等。"));
                }
            }
        }

        if (hasLink)
        {
            validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (idKey, sym) in tooltipSymbols)
            {
                if (!string.IsNullOrWhiteSpace(idKey)
                    && !string.Equals(idKey, "Invalid", StringComparison.OrdinalIgnoreCase))
                {
                    validKeys.Add(idKey.Trim());
                }
                if (!string.IsNullOrWhiteSpace(sym.Id)
                    && !string.Equals(sym.Id, "Invalid", StringComparison.OrdinalIgnoreCase))
                {
                    validKeys.Add(sym.Id.Trim());
                }
                if (!string.IsNullOrWhiteSpace(sym.Tag)
                    && !string.Equals(sym.Tag, "Invalid", StringComparison.OrdinalIgnoreCase))
                {
                    validKeys.Add(sym.Tag.Trim());
                }
            }

            foreach (Match m in TooltipLinkTagRegex().Matches(text))
            {
                string linkKey = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
                if (string.IsNullOrEmpty(linkKey)) continue;
                if (!validKeys.Contains(linkKey))
                {
                    found.Add(new ModValidationIssue(
                        ModValidationSeverity.Warning,
                        modId,
                        "UNKNOWN_TOOLTIP_SYMBOL_LINK",
                        $"条目 '{targetId}' 的文案引用了未知的官方词条链接 '<link=\"{linkKey}\">'（不在官方 136 条 tooltips 词条表中）。",
                        targetId,
                        GateId: 7,
                        FixHintZh: "请确认 <link=\"...\"> 使用官方标准词条键（如 Burn、Poison、Shield、Haste、Charge、Freeze、Crit Chance 等）。"));
                }
            }
        }

        return found;
    }

    private static readonly (string HiddenTag, string LabelZh, string AttrKey, string[] ActionTypes, string MechanicStatus)[] ItemEffectHiddenTagSpecs =
    [
        ("Burn", "灼烧", "BurnApplyAmount", ["TActionPlayerBurnApply"], "Burn"),
        ("Poison", "剧毒", "PoisonApplyAmount", ["TActionPlayerPoisonApply"], "Poison"),
        ("Shield", "护盾", "ShieldApplyAmount", ["TActionPlayerShieldApply"], "Shield"),
        ("Heal", "治疗", "HealAmount", ["TActionPlayerHeal", "TActionPlayerReviveHeal"], "Heal"),
        ("Regen", "再生", "RegenApplyAmount", ["TActionPlayerRegenApply"], "Regen"),
    ];

    private static void CheckItemHiddenTagConsistency(
        ModPackage pkg,
        string modId,
        CustomCardDefinition card,
        List<ModValidationIssue> issues)
    {
        foreach (var (hiddenTag, labelZh, attrKey, actionTypes, mechStatus) in ItemEffectHiddenTagSpecs)
        {
            if (!card.HiddenTags.Any(t => string.Equals(t, hiddenTag, StringComparison.OrdinalIgnoreCase)))
                continue;

            bool hasAttr = (card.Attributes != null && card.Attributes.TryGetValue(attrKey, out var topV) && topV > 0)
                || (card.Tiers != null && card.Tiers.Values.Any(td => td?.Attributes != null && td.Attributes.TryGetValue(attrKey, out var tv) && tv > 0));
            if (hasAttr) continue;

            bool hasAction = card.Abilities.Values.Any(ab => RuleNodeContainsAnyType(ab?.Action, actionTypes));
            if (hasAction) continue;

            bool hasMechanicStatus = false;
            if (card.CustomMechanics is { Count: > 0 })
            {
                var globalMechs = ModRuntimeHooks.GetRegisteredMechanics();
                foreach (var att in card.CustomMechanics)
                {
                    if (att == null || !att.Enabled || string.IsNullOrWhiteSpace(att.MechanicId)) continue;
                    string mid = att.MechanicId.Trim();
                    var def = pkg.Mechanics.FirstOrDefault(m => string.Equals(m.Id?.Trim(), mid, StringComparison.OrdinalIgnoreCase))
                        ?? (globalMechs != null && globalMechs.TryGetValue(mid, out var gm) ? gm : null);
                    if (def?.Hooks != null && def.Hooks.Any(h => MechanicStepsApplyStatus(h?.Steps, mechStatus)))
                    {
                        hasMechanicStatus = true;
                        break;
                    }
                }
            }
            if (hasMechanicStatus) continue;

            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Warning,
                modId,
                "HIDDEN_TAG_WITHOUT_MATCHING_EFFECT",
                $"物品 '{card.CustomId}' 的效果分类勾选了 {hiddenTag}（{labelZh}），但卡牌基础属性、原生技能与自定义机制均未施加{labelZh}效果，局内会被误判为{labelZh}物品并白吃无效加成。",
                card.CustomId,
                GateId: 7,
                FixHintZh: $"若本卡本身不施加{labelZh}，请从 hiddenTags 中移除 \"{hiddenTag}\"（附魔带出的{labelZh}由附魔定义自动提供，无需在本体勾选）。"));
        }
    }

    private static bool RuleNodeContainsAnyType(RuleNode? node, string[] targetTypes, int depth = 0)
    {
        if (node == null || depth > 8) return false;
        if (!string.IsNullOrEmpty(node.Type) && targetTypes.Any(t => string.Equals(t, node.Type, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (node.Actions is { Count: > 0 })
        {
            for (int i = 0; i < node.Actions.Count; i++)
                if (RuleNodeContainsAnyType(node.Actions[i], targetTypes, depth + 1))
                    return true;
        }
        return false;
    }

    private static bool MechanicStepsApplyStatus(List<MechanicStep>? steps, string status, int depth = 0)
    {
        if (steps == null || depth > 8) return false;
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (s == null) continue;
            if (string.Equals(s.Op, "ApplyStatus", StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Status, status, StringComparison.OrdinalIgnoreCase))
                return true;
            if (MechanicStepsApplyStatus(s.Then, status, depth + 1)
                || MechanicStepsApplyStatus(s.Else, status, depth + 1)
                || MechanicStepsApplyStatus(s.Steps, status, depth + 1))
                return true;
        }
        return false;
    }

    private static void RunGate8Assets(ModPackage pkg, List<ModValidationIssue> issues)
    {
        string modId = pkg.Manifest.Id;
        long totalBytes = 0;

        foreach (var asset in pkg.AssetsByLogicalPath.Values)
        {
            totalBytes += Math.Max(0, asset.Bytes);

            if (!IsSafeRelativePath(asset.LogicalPath))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_PATH_TRAVERSAL",
                    $"资产路径 '{asset.LogicalPath}' 包含非法目录穿越。", asset.LogicalPath, GateId: 8));
            }

            if (string.Equals(asset.ContentType, "image/webp", StringComparison.OrdinalIgnoreCase)
                || asset.LogicalPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_FORMAT_UNSUPPORTED_FOR_UNITY",
                    $"资产 '{asset.LogicalPath}' 为 WebP 格式；原版 Unity 客户端 ImageConversion.LoadImage 无法原生解码 WebP，会导致卡图静默失效。",
                    asset.LogicalPath, GateId: 8, FixHintZh: "请将图片转换为标准 PNG 格式（或直接在 /workshop 网页端拖拽上传自动转 PNG）。"));
                continue;
            }

            if (!string.Equals(asset.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_MAGIC_INVALID",
                    $"资产 '{asset.LogicalPath}' 不是合法的标准 PNG 文件（PNG 8 字节魔数校验失败）。",
                    asset.LogicalPath, GateId: 8, FixHintZh: "请提供真实 PNG 图像（64~1024px，<= 2MB）。"));
                continue;
            }

            if (asset.Bytes <= 0 || asset.Bytes > MaxAssetBytes)
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_SIZE_EXCEEDED",
                    $"资产 '{asset.LogicalPath}' 体积 {asset.Bytes} 字节超出单图上限 {MaxAssetBytes} 字节（2 MB）。",
                    asset.LogicalPath, GateId: 8, FixHintZh: "请压缩或缩放至 512×512 / 512×1024 PNG 后重试。"));
            }

            if (asset.Width < MinAssetDimension || asset.Height < MinAssetDimension
                || asset.Width > MaxAssetWidth || asset.Height > MaxAssetHeight)
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_DIMENSION_OUT_OF_RANGE",
                    $"资产 '{asset.LogicalPath}' 分辨率 {asset.Width}×{asset.Height} 超出允许区间 [{MinAssetDimension}, {MaxAssetWidth}]。",
                    asset.LogicalPath, GateId: 8, FixHintZh: "推荐尺寸：小型/大型物品 512×1024，中型物品与技能 512×512。"));
            }

            if (pkg.ExpectedAssetSha256ByPath.TryGetValue(asset.LogicalPath, out var expectedSha)
                && !string.Equals(expectedSha, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_SHA256_MISMATCH",
                    $"资产 '{asset.LogicalPath}' 实际 SHA-256 ({asset.Sha256[..Math.Min(12, asset.Sha256.Length)]}…) 与 art_manifest.json 登记值不一致。",
                    asset.LogicalPath, GateId: 8, FixHintZh: "在工坊保存草稿或重新上传该 PNG 自动刷新 art_manifest.json。"));
            }
        }

        if (totalBytes > MaxTotalAssetsBytes)
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_SIZE_EXCEEDED",
                $"Mod 资产总大小 {totalBytes / (1024.0 * 1024.0):0.0} MB 超出 32 MB 总配额。",
                GateId: 8, FixHintZh: "精简未使用的贴图或压缩 PNG 体积。"));
        }

        void CheckArtReference(string ownerId, string? artFile)
        {
            if (string.IsNullOrWhiteSpace(artFile)) return;
            string artPath = NormalizeLogicalPath(artFile);
            if (!IsSafeRelativePath(artPath))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_PATH_TRAVERSAL",
                    $"条目 '{ownerId}' 贴图路径 '{artFile}' 非法。", ownerId, GateId: 8));
            }
            else if (!pkg.AssetsByLogicalPath.ContainsKey(artPath))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "CARD_ART_FILE_MISSING",
                    $"条目 '{ownerId}' 引用的贴图 '{artPath}' 在 assets 目录中不存在。",
                    ownerId, GateId: 8, FixHintZh: "上传对应 PNG 到 assets/ 目录，或清空 customArtFile 以继承原版载体卡面。"));
            }
        }

        foreach (var c in pkg.Cards) CheckArtReference(c.CustomId, c.CustomArtFile);
        foreach (var ev in pkg.Events)
        {
            CheckArtReference(ev.EventId, ev.CustomArtFile);
            foreach (var st in ev.Steps) CheckArtReference(st.CustomId, st.CustomArtFile);
        }
        foreach (var p in pkg.Patches) CheckArtReference(p.TargetTemplateId, p.CustomArtFile);
    }

    private static void RunGate10IsolationAndInvariants(
        ModPackage targetPkg,
        List<ModPackage> otherPackages,
        GameDataRepository baseRepo,
        IReadOnlySet<string>? validVisualWhitelist,
        List<ModValidationIssue> issues)
    {
        string modId = targetPkg.Manifest.Id;
        bool hasGameplayChange = targetPkg.Cards.Count > 0 || targetPkg.Patches.Count > 0
            || targetPkg.Events.Count > 0 || targetPkg.Monsters.Count > 0 || targetPkg.Modes.Count > 0;

        // 1. 幽灵池隔离验证
        if (hasGameplayChange && string.Equals(targetPkg.Manifest.Scope, "cosmetic-only", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "GHOST_POOL_ISOLATION_VIOLATION",
                "包含玩法内容（卡牌/补丁/奇遇/野怪/模式）的 Mod 严禁声明 scope='cosmetic-only'，必须强制隔离标准镜像池。",
                GateId: 10, FixHintZh: "将 mod.json 中的 scope 改为 'multiplayer-safe' 或 'singleplayer-only'。"));
        }

        if (hasGameplayChange && targetPkg.Manifest.AffectsGhostPool)
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "GHOST_POOL_ISOLATION_VIOLATION",
                "玩法 Mod 严禁将 affectsGhostPool 设为 true（防止携带自定义卡的镜像污染标准天梯幽灵池）。",
                GateId: 10, FixHintZh: "将 mod.json 中的 affectsGhostPool 设为 false。"));
        }

        // 2. 隔离沙盒 ModRegistry 装入 -> 检查 UnknownRuleTypes / Wire 映射 / RollbackToBase 零残留
        var sandbox = new ModRegistry(baseRepo, validVisualWhitelist);
        int baseCardCount = baseRepo.CardsById.Count;

        // 若目标包有声明依赖且在上下文中存在，一并装入沙盒以保证真实装载
        var loadSet = new List<ModPackage>();
        var neededDeps = new HashSet<string>(targetPkg.Manifest.Dependencies ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var p in otherPackages)
        {
            if (neededDeps.Contains(p.Manifest.Id)) loadSet.Add(p);
        }
        loadSet.Add(targetPkg);

        var loadReport = sandbox.LoadPackages(loadSet, "<gate10-sandbox>", baseRepo, validVisualWhitelist, keepPreviousOnError: false);

        if (hasGameplayChange && loadReport.LoadedModIdsInOrder.Contains(modId, StringComparer.OrdinalIgnoreCase))
        {
            if (!sandbox.ForcesGhostPoolIsolation)
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "GHOST_POOL_ISOLATION_VIOLATION",
                    "沙盒装载后未能触发 ForcesGhostPoolIsolation=true，存在污染 standard 幽灵池风险。", GateId: 10));
            }
            string shortHash = string.IsNullOrEmpty(loadReport.ModSetHash) ? "local" : loadReport.ModSetHash[..Math.Min(12, loadReport.ModSetHash.Length)];
            string resolvedPartition = $"mod:{shortHash}";
            if (string.Equals(resolvedPartition, "standard", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "GHOST_POOL_ISOLATION_VIOLATION",
                    "解析出的镜像池分区等于 standard，违反隔离红线。", GateId: 10));
            }
        }

        if (sandbox.ActiveRepository.UnknownRuleTypes.Count > 0)
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "OVERLAY_UNKNOWN_RULE_TYPES",
                $"隔离叠加仓库存在 {sandbox.ActiveRepository.UnknownRuleTypes.Count} 个未识别规则类型：{string.Join(", ", sandbox.ActiveRepository.UnknownRuleTypes.Take(5))}。",
                GateId: 10));
        }

        // 检查所有新卡视觉绑定均可安全映射回白名单 Wire GUID
        foreach (var (cid, binding) in sandbox.VisualBindingsByCustomId)
        {
            if (string.IsNullOrWhiteSpace(binding.BaseVisualTemplateId)
                || (validVisualWhitelist != null && !validVisualWhitelist.Contains(binding.BaseVisualTemplateId)))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "WIRE_TEMPLATE_ID_UNSAFE",
                    $"自定义卡 '{cid}' 无法安全映射为白名单 Wire 模板 GUID（BaseVisualTemplateId='{binding.BaseVisualTemplateId}'）。",
                    cid, GateId: 10));
            }
        }

        // 3. 回滚零残留校验（不影响外层真实服务状态：注意不清空全局 ModRuntimeHooks 如果外部已有注册，只校验沙盒自身状态归零）
        sandbox.RollbackToBase();
        if (sandbox.ActiveRepository.CardsById.Count != baseCardCount
            || sandbox.LoadedPackages.Count != 0
            || sandbox.VisualBindingsByCustomId.Count != 0)
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ROLLBACK_STATE_LEAK",
                "执行 ModRegistry.RollbackToBase() 后仍有自定义卡或视觉绑定残留。", GateId: 10));
        }
    }

    private static GameDataRepository BuildIsolatedOverlayForPackage(ModPackage pkg, GameDataRepository baseRepo)
    {
        var merged = new Dictionary<string, CardTemplate>(StringComparer.OrdinalIgnoreCase);
        string modId = pkg.Manifest.Id;

        foreach (var c in pkg.Cards)
        {
            var baseTpl = baseRepo.GetCard(c.BaseVisualTemplateId);
            if (baseTpl != null)
                merged[c.CustomId] = c.ToCardTemplate(baseTpl, modId);
        }
        foreach (var p in pkg.Patches)
        {
            var baseTpl = baseRepo.GetCard(p.TargetTemplateId);
            if (baseTpl != null)
                merged[p.TargetTemplateId] = p.ApplyToTemplate(baseTpl, modId);
        }
        foreach (var ev in pkg.Events)
        {
            var evDef = ev.ToCardDefinition();
            var evBase = baseRepo.GetCard(evDef.BaseVisualTemplateId);
            if (evBase != null) merged[evDef.CustomId] = evDef.ToCardTemplate(evBase, modId);
            foreach (var st in ev.Steps)
            {
                var stBase = baseRepo.GetCard(st.BaseVisualTemplateId);
                if (stBase != null) merged[st.CustomId] = st.ToCardTemplate(stBase, modId);
            }
        }

        var monsters = new List<MonsterTemplate>(pkg.Monsters);
        foreach (var ev in pkg.Events)
            if (ev.CombatMonster != null) monsters.Add(ev.CombatMonster);

        return (merged.Count > 0 || monsters.Count > 0)
            ? baseRepo.CreateWithOverlay(merged.Values, monsters)
            : baseRepo;
    }

    private static List<ModPackage> TopologicalSort(
        Dictionary<string, ModPackage> pkgById,
        List<ModValidationIssue> issues,
        HashSet<string> rejected)
    {
        var active = new Dictionary<string, ModPackage>(pkgById, StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            var toRemove = new List<string>();
            foreach (var (id, pkg) in active)
            {
                if (pkg.Manifest.Dependencies == null) continue;
                foreach (var depRaw in pkg.Manifest.Dependencies)
                {
                    if (string.IsNullOrWhiteSpace(depRaw)) continue;
                    string dep = depRaw.Trim();
                    if (string.Equals(dep, id, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, id, "SELF_DEPENDENCY",
                            $"Mod '{id}' 声明了自身循环依赖。", GateId: 2, FixHintZh: "从 mod.json 的 dependencies 中移除自身 ID。"));
                        toRemove.Add(id);
                        break;
                    }
                    if (!active.ContainsKey(dep))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, id, "MISSING_DEPENDENCY",
                            $"Mod '{id}' 依赖的 Mod '{dep}' 缺失或校验失败，已跳过加载。", GateId: 2, FixHintZh: $"请先安装并启用前置 Mod '{dep}'，或移除该依赖项。"));
                        toRemove.Add(id);
                        break;
                    }
                }
            }
            foreach (var r in toRemove)
            {
                active.Remove(r);
                rejected.Add(r);
                changed = true;
            }
        } while (changed);

        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in active.Keys)
        {
            inDegree[id] = 0;
            dependents[id] = new List<string>();
        }

        foreach (var (id, pkg) in active)
        {
            if (pkg.Manifest.Dependencies == null) continue;
            var distinctDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var depRaw in pkg.Manifest.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(depRaw)) continue;
                string dep = depRaw.Trim();
                if (!distinctDeps.Add(dep) || !active.ContainsKey(dep)) continue;
                inDegree[id]++;
                dependents[dep].Add(id);
            }
        }

        int CompareReady(string a, string b)
        {
            int cmp = active[a].Manifest.LoadOrder.CompareTo(active[b].Manifest.LoadOrder);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a, b);
        }

        var ready = inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
        ready.Sort(CompareReady);
        var ordered = new List<ModPackage>(active.Count);

        while (ready.Count > 0)
        {
            string current = ready[0];
            ready.RemoveAt(0);
            ordered.Add(active[current]);

            foreach (var next in dependents[current])
            {
                inDegree[next]--;
                if (inDegree[next] == 0)
                {
                    ready.Add(next);
                    ready.Sort(CompareReady);
                }
            }
        }

        if (ordered.Count < active.Count)
        {
            var cyclicIds = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            string cycleSummary = string.Join(", ", cyclicIds);
            foreach (var cid in cyclicIds)
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    cid,
                    "CIRCULAR_DEPENDENCY",
                    $"检测到 Mod 循环依赖环（涉及：{cycleSummary}），环内全部 Mod 均被拒绝加载。",
                    GateId: 2,
                    FixHintZh: "调整 dependencies 打破循环依赖。"));
                rejected.Add(cid);
            }
        }

        return ordered;
    }

    private static bool TryFindEncounterCycle(
        Dictionary<string, List<string>> graph,
        out List<string> cyclePath)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();

        bool Dfs(string node, out List<string> foundCycle)
        {
            state[node] = 1;
            stack.Add(node);
            if (graph.TryGetValue(node, out var neighbors))
            {
                for (int i = 0; i < neighbors.Count; i++)
                {
                    var next = neighbors[i];
                    if (string.Equals(next, node, StringComparison.OrdinalIgnoreCase))
                    {
                        foundCycle = [node, next];
                        return true;
                    }
                    if (!graph.ContainsKey(next))
                        continue;

                    int st = state.GetValueOrDefault(next, 0);
                    if (st == 1)
                    {
                        int idx = stack.FindIndex(x => string.Equals(x, next, StringComparison.OrdinalIgnoreCase));
                        foundCycle = stack.Skip(Math.Max(0, idx)).Append(next).ToList();
                        return true;
                    }
                    if (st == 0 && Dfs(next, out foundCycle))
                        return true;
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
            foundCycle = [];
            return false;
        }

        foreach (var node in graph.Keys)
        {
            if (state.GetValueOrDefault(node, 0) != 0) continue;
            if (Dfs(node, out cyclePath))
                return true;
        }

        cyclePath = [];
        return false;
    }

    /// <summary>校验相对路径不含盘符、绝对根路径或 `..` 目录穿越段。</summary>
    public static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        string normalized = NormalizeLogicalPath(relativePath);
        if (Path.IsPathRooted(normalized) || normalized.StartsWith('/') || normalized.Contains(':'))
            return false;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i] == ".." || segments[i] == ".")
                return false;
        }
        return segments.Length > 0;
    }

    public static string NormalizeLogicalPath(string path)
        => path.Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>
    /// 校验图片字节流的魔数头并解析宽高像素（识别 PNG 与 WebP 以便 Gate #8 精准拒绝 WebP 并给出清晰提示）。
    /// </summary>
    public static bool TryValidateImageHeader(ReadOnlySpan<byte> bytes, out int width, out int height, out string contentType)
    {
        width = 0;
        height = 0;
        contentType = string.Empty;

        // PNG: 8 字节签名 \x89PNG\r\n\x1A\n + IHDR 块在偏移 16..23 存大端宽高
        if (bytes.Length >= 24
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A
            && bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R')
        {
            width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            contentType = "image/png";
            return width > 0 && height > 0;
        }

        // WebP: RIFF....WEBP（识别出来交由 Gate #8 报 ASSET_FORMAT_UNSUPPORTED_FOR_UNITY，而非误判未知文件）
        if (bytes.Length >= 30
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            contentType = "image/webp";
            if (bytes[12] == (byte)'V' && bytes[13] == (byte)'P' && bytes[14] == (byte)'8' && bytes[15] == (byte)'X')
            {
                width = 1 + (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16));
                height = 1 + (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16));
                return width > 0 && height > 0;
            }
            if (bytes[12] == (byte)'V' && bytes[13] == (byte)'P' && bytes[14] == (byte)'8' && bytes[15] == (byte)'L'
                && bytes[20] == 0x2F)
            {
                uint bits = (uint)(bytes[21] | (bytes[22] << 8) | (bytes[23] << 16) | (bytes[24] << 24));
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
                return width > 0 && height > 0;
            }
            if (bytes[12] == (byte)'V' && bytes[13] == (byte)'P' && bytes[14] == (byte)'8' && bytes[15] == (byte)' '
                && bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
            {
                width = (bytes[26] | (bytes[27] << 8)) & 0x3FFF;
                height = (bytes[28] | (bytes[29] << 8)) & 0x3FFF;
                return width > 0 && height > 0;
            }
        }

        return false;
    }
}
