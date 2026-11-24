using System.Reflection;
using System.Text.RegularExpressions;
using BazaarCore.Tests.TestInfrastructure;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 5B 客户端 Mod 视觉与覆层专项回归测试（对应 w2-visuals 4 项修复）：
///   1. P1-01（RC-MOD-SKILL-PROXY-ASYNC-INIT-RACE）：SkillProxyRenderer 异步装帧 4 连修 + 异步租约先取纹理防 NRE；
///   2. P2-01（RC-CUSTOM-VISUAL-PATCH-27-OF-28）：GetPveEncounterLevelPostfix ref uint 匹配、全量 29/29 补丁回调校验、
///      移除无效 _isRecapViewOpen 字段、修正 TMonster 命名空间、SafeHarmonyPatcher 失败 label 明文汇总；
///   3. P3-06（RC-UPGRADE-PREVIEW-ADDITIVE-ONLY）：条件整倍/半整数比例缩放外推（如以太壁垒提灯 70 -> 140）与加法成长卡防回归；
///   4. P3-08（RC-OFFICIAL-BLANK-SKILL-ARTKEY）：SkillProxyRenderer.Initialize 空/空白/Invalid ArtKey 静默安全回退兜底。
/// </summary>
public sealed class Wave5B_VisualsAndOverlayTests : IClassFixture<Wave2CLauncherAssemblyFixture>
{
    private readonly Wave2CLauncherAssemblyFixture _fx;

    public Wave5B_VisualsAndOverlayTests(Wave2CLauncherAssemblyFixture fx) => _fx = fx;

    // ── 模拟官方目标签名（100% 对照 docs/recon 反编译源码） ──────────────────

    private sealed class MockOfficialTargets
    {
        // 对照 docs/recon/src/TheBazaar.Tooltips/CardTooltipData.cs:144
        public uint GetPVEEncounterLevel() => 1u;

        // 对照 docs/recon/src/SkillIconUpdater.cs:21
        public bool UpdateSkillIconMaterial(object? newSkillIconTexture) => true;

        // 对照 docs/recon/src/TheBazaar/SkillProxyRenderer.cs:104, 184, 196
        public void Initialize(object? card, int tier, string? spriteTexture, bool aboveBoard = false) { }
        public void UpdateSkillIcon(object? newSkillIconTexture) { }
        public Task UpdateCard(object? cardData, bool movedLocally = false) => Task.CompletedTask;

        // 对照 docs/recon/src/TheBazaar.UI.Tooltips/MonsterBoardRenderer.cs:24
        public void BuildDisplay(BazaarGameShared.Domain.Players.TMonster? monster, object? tooltipData) { }

        // 错误签名对比桩：验证旧版 ref int __result 必被 SafeHarmonyPatcher.ValidateHookMethod 拦截
        private static void BadGetPveEncounterLevelPostfix(object __instance, ref int __result)
        {
            __result = 1;
        }
    }

    private bool InvokeValidateHookMethod(MethodBase target, MethodInfo hook, string role, out string error)
    {
        Type patcherType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.SafeHarmonyPatcher", throwOnError: true)!;
        MethodInfo? validateM = patcherType.GetMethod("ValidateHookMethod", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(validateM);

        object?[] args = { target, hook, role, null };
        bool ok = (bool)validateM!.Invoke(null, args)!;
        error = args[3]?.ToString() ?? string.Empty;
        return ok;
    }

    [Fact]
    public void P2_01_GetPveEncounterLevelPostfix_UsesRefUint_AndAll29VisualPatchHooksResolveCleanly()
    {
        Type visType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.CustomContentVisualModule", throwOnError: true)!;

        // 1. 强校验 GetPveEncounterLevelPostfix 的 __result 必须为 ref uint，能通过针对官方 uint 返回值的静态预检
        MethodInfo? pvePostfix = visType.GetMethod("GetPveEncounterLevelPostfix", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(pvePostfix);

        ParameterInfo? resultParam = Array.Find(pvePostfix!.GetParameters(), p => p.Name == "__result");
        Assert.NotNull(resultParam);
        Assert.True(resultParam!.ParameterType.IsByRef, "GetPveEncounterLevelPostfix.__result 必须按 ref 传递");
        Assert.Equal(typeof(uint), resultParam.ParameterType.GetElementType());

        MethodInfo officialPveLevelTarget = typeof(MockOfficialTargets).GetMethod(nameof(MockOfficialTargets.GetPVEEncounterLevel))!;
        Assert.True(
            InvokeValidateHookMethod(officialPveLevelTarget, pvePostfix, "Postfix", out string okErr),
            $"ref uint 钩子应通过 ValidateHookMethod，实际错误：{okErr}");

        // 反例验证：旧版 ref int __result 必须被 ValidateHookMethod 精确拦截
        MethodInfo badIntPostfix = typeof(MockOfficialTargets).GetMethod("BadGetPveEncounterLevelPostfix", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.False(InvokeValidateHookMethod(officialPveLevelTarget, badIntPostfix, "Postfix", out string badErr));
        Assert.Contains("UInt32", badErr, StringComparison.Ordinal);
        Assert.Contains("Int32", badErr, StringComparison.Ordinal);

        // 2. 校验 TMonster 官方真实类型可强引用解析、MonsterBoardRendererBuildDisplayPrefix 通过预检，且源码不再含旧错写命名空间与无效 _isRecapViewOpen 查找
        Assert.Equal("BazaarGameShared.Domain.Players.TMonster", typeof(BazaarGameShared.Domain.Players.TMonster).FullName);
        MethodInfo? monsterBoardPrefix = visType.GetMethod("MonsterBoardRendererBuildDisplayPrefix", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(monsterBoardPrefix);
        Assert.True(
            InvokeValidateHookMethod(
                typeof(MockOfficialTargets).GetMethod(nameof(MockOfficialTargets.BuildDisplay))!,
                monsterBoardPrefix!,
                "Prefix",
                out string mbErr),
            mbErr);

        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string visSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "Modules", "CustomContentVisualModule.cs"));
        Assert.Contains("\"BazaarGameShared.Domain.Players.TMonster\"", visSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("BazaarGameShared.Domain.Monsters.TMonster", visSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("BazaarGameShared.Domain.Cards.Encounter.TMonster", visSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessTools.Field(boardMgrType, \"_isRecapViewOpen\")", visSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("_boardManagerIsRecapOpenField", visSrc, StringComparison.Ordinal);

        // 3. 扫描源码中 CustomContentVisualModule 注册的全部 patcher.TryPatch / TryPatchMethod，验证满编 29 项且每个回调方法真实存在
        MatchCollection labelMatches = Regex.Matches(visSrc, @"label:\s*""([^""]+)""");
        Assert.Equal(29, labelMatches.Count);

        MatchCollection hookNameMatches = Regex.Matches(visSrc, @"AccessTools\.Method\(typeof\(CustomContentVisualModule\),\s*nameof\(([A-Za-z0-9_]+)\)\)");
        Assert.True(hookNameMatches.Count >= 29, $"预期至少引用 29 个补丁回调方法，实际 {hookNameMatches.Count}");
        foreach (Match m in hookNameMatches)
        {
            string hookName = m.Groups[1].Value;
            MethodInfo? hookMethod = visType.GetMethod(hookName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.True(hookMethod != null, $"补丁回调 {hookName} 在 CustomContentVisualModule 中不存在");
        }
    }

    [Fact]
    public void P2_01_SafeHarmonyPatcher_ReportsExplicitFailedLabelsAndReasons_EvenWhenOptional()
    {
        Type patcherType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.SafeHarmonyPatcher", throwOnError: true)!;
        ConstructorInfo? ctor = patcherType.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(string) },
            null);
        Assert.NotNull(ctor);

        using var patcher = (IDisposable)ctor!.Invoke(new object[] { "test.wave5b.visuals" });

        MethodInfo? formatSummaryM = patcherType.GetMethod("FormatSummary", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo? failedLabelsM = patcherType.GetMethod("GetFailedLabelsSnapshot", BindingFlags.NonPublic | BindingFlags.Instance);
        PropertyInfo? failedCountP = patcherType.GetProperty("FailedCount", BindingFlags.NonPublic | BindingFlags.Instance);
        PropertyInfo? degradedCountP = patcherType.GetProperty("DegradedCount", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo? tryPatchTypeM = patcherType.GetMethod(
            "TryPatch",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[]
            {
                typeof(Type), typeof(string), typeof(Type[]),
                typeof(MethodInfo), typeof(MethodInfo), typeof(MethodInfo),
                typeof(int), typeof(int), typeof(bool), typeof(string)
            },
            null);

        Assert.NotNull(formatSummaryM);
        Assert.NotNull(failedLabelsM);
        Assert.NotNull(failedCountP);
        Assert.NotNull(degradedCountP);
        Assert.NotNull(tryPatchTypeM);

        // 初始 0 失败：汇总文案保持干净的 "成功=0/0"，不含 "失败=["
        string cleanSummary = (string)formatSummaryM!.Invoke(patcher, new object[] { false })!;
        Assert.Equal("成功=0/0", cleanSummary);
        Assert.Equal(0, (int)failedCountP!.GetValue(patcher)!);

        // 触发 1 个 optional:true 的签名不兼容失败（复现历史 27/28 静默吞错场景）
        MethodInfo badIntPostfix = typeof(MockOfficialTargets).GetMethod("BadGetPveEncounterLevelPostfix", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool ok1 = (bool)tryPatchTypeM!.Invoke(patcher, new object?[]
        {
            typeof(MockOfficialTargets),
            nameof(MockOfficialTargets.GetPVEEncounterLevel),
            Type.EmptyTypes,
            null,
            badIntPostfix,
            null,
            400,
            400,
            true,
            "MonsterPreview.CardTooltipData.GetPVEEncounterLevel"
        })!;
        Assert.False(ok1);

        // 触发 1 个非可选的缺失方法失败
        bool ok2 = (bool)tryPatchTypeM.Invoke(patcher, new object?[]
        {
            typeof(MockOfficialTargets),
            "NonExistentMethodForTest",
            null,
            null,
            badIntPostfix,
            null,
            400,
            400,
            false,
            "Visual.Test.MissingRequiredMethod"
        })!;
        Assert.False(ok2);

        Assert.Equal(2, (int)failedCountP.GetValue(patcher)!);
        Assert.Equal(1, (int)degradedCountP!.GetValue(patcher)!);

        var failedLabels = (IReadOnlyList<string>)failedLabelsM!.Invoke(patcher, null)!;
        Assert.Equal(2, failedLabels.Count);
        Assert.Contains("MonsterPreview.CardTooltipData.GetPVEEncounterLevel", failedLabels);
        Assert.Contains("Visual.Test.MissingRequiredMethod", failedLabels);

        string summaryShort = (string)formatSummaryM.Invoke(patcher, new object[] { false })!;
        Assert.Contains("成功=0/2", summaryShort, StringComparison.Ordinal);
        Assert.Contains("失败=[MonsterPreview.CardTooltipData.GetPVEEncounterLevel, Visual.Test.MissingRequiredMethod]", summaryShort, StringComparison.Ordinal);

        string summaryFull = (string)formatSummaryM.Invoke(patcher, new object[] { true })!;
        Assert.Contains("UInt32", summaryFull, StringComparison.Ordinal);
        Assert.Contains("Int32", summaryFull, StringComparison.Ordinal);
    }

    [Fact]
    public void P1_01_SkillProxyAsyncInitRace_FourPartFixesAndLeaseOrdering_AreWiredAndValid()
    {
        Type visType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.CustomContentVisualModule", throwOnError: true)!;
        Type? agentType = visType.GetNestedType("CardVisualOverrideAgent", BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(agentType);

        // 1. 核实新增/强化的技能装帧落地钩子与全场景补刷方法全部存在且签名合规
        MethodInfo? updaterPostfix = visType.GetMethod("SkillIconUpdaterMaterialPostfix", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? updateIconPostfix = visType.GetMethod("SkillUpdateIconPostfix", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? proxyUpdateCardPostfix = visType.GetMethod("SkillProxyUpdateCardPostfix", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? missingSkillScanM = visType.GetMethod("ReapplyMissingSkillAgentsOnMainThread", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(updaterPostfix);
        Assert.NotNull(updateIconPostfix);
        Assert.NotNull(proxyUpdateCardPostfix);
        Assert.NotNull(missingSkillScanM);

        // 对照官方目标签名跑 SafeHarmonyPatcher.ValidateHookMethod
        Assert.True(
            InvokeValidateHookMethod(
                typeof(MockOfficialTargets).GetMethod(nameof(MockOfficialTargets.UpdateSkillIconMaterial))!,
                updaterPostfix!,
                "Postfix",
                out string err1),
            err1);
        Assert.True(
            InvokeValidateHookMethod(
                typeof(MockOfficialTargets).GetMethod(nameof(MockOfficialTargets.UpdateSkillIcon))!,
                updateIconPostfix!,
                "Postfix",
                out string err2),
            err2);
        Assert.True(
            InvokeValidateHookMethod(
                typeof(MockOfficialTargets).GetMethod(nameof(MockOfficialTargets.UpdateCard))!,
                proxyUpdateCardPostfix!,
                "Postfix",
                out string err3),
            err3);

        // 2. 核实 CardVisualOverrideAgent 具备待装帧挂起标记、幂等落盘状态与 OnEnable 消费钩子
        Assert.NotNull(agentType!.GetProperty("PendingSkillApply", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(agentType.GetProperty("HasAppliedCustomSkill", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(agentType.GetMethod("IsSkillAppliedTo", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(agentType.GetMethod("MarkSkillApplied", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(agentType.GetMethod("InvalidateSkillAppliedMarker", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(agentType.GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance));

        // 3. 核实源码关键防退化契约：FindSkillHostForUpdater 全面 includeInactive: true + Reapply 扫描 SkillProxyRenderer + 异步回调先取纹理再 TakePrimaryLease
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string visSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "Modules", "CustomContentVisualModule.cs"));

        Assert.Contains("updater.GetComponentInParent(_skillControllerType, includeInactive: true)", visSrc, StringComparison.Ordinal);
        Assert.Contains("updater.GetComponentInParent(_skillProxyRendererType, includeInactive: true)", visSrc, StringComparison.Ordinal);
        Assert.Contains("updater.GetComponentInParent<CardVisualOverrideAgent>(includeInactive: true)", visSrc, StringComparison.Ordinal);
        Assert.Contains("ReapplyMissingSkillAgentsOnMainThread(_skillProxyRendererType, visited)", visSrc, StringComparison.Ordinal);
        Assert.Contains("ReapplyMissingSkillAgentsOnMainThread(_skillControllerType, visited)", visSrc, StringComparison.Ordinal);
        Assert.Contains("Texture asyncTex = lease.Asset.Texture;", visSrc, StringComparison.Ordinal);
    }

    [Fact]
    public void P3_08_BlankSkillArtKey_SkillProxyInitializePrefix_SignatureAndFallbackGuard()
    {
        Type visType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.CustomContentVisualModule", throwOnError: true)!;
        MethodInfo? initPrefix = visType.GetMethod("SkillProxyInitializePrefix", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? initPostfix = visType.GetMethod("SkillProxyInitializePostfix", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? blankAsyncHelper = visType.GetMethod("CompleteBlankArtKeySkillProxyInitAsync", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(initPrefix);
        Assert.NotNull(initPostfix);
        Assert.NotNull(blankAsyncHelper);

        // Prefix 返回 bool 用于空 ArtKey 时跳过原版 Addressables 寻址与刷屏错误日志
        Assert.Equal(typeof(bool), initPrefix!.ReturnType);
        ParameterInfo[] ps = initPrefix.GetParameters();
        Assert.Equal(5, ps.Length);
        Assert.Equal("__instance", ps[0].Name);
        Assert.Equal("__0", ps[1].Name);
        Assert.Equal("__1", ps[2].Name);
        Assert.Equal("__2", ps[3].Name);
        Assert.Equal(typeof(string), ps[3].ParameterType);
        Assert.Equal("__3", ps[4].Name);
        Assert.Equal(typeof(bool), ps[4].ParameterType);

        // 验证 Prefix/Postfix 均通过针对官方 SkillProxyRenderer.Initialize 签名的 ValidateHookMethod 静态预检
        MethodInfo officialInitTarget = typeof(MockOfficialTargets).GetMethod(nameof(MockOfficialTargets.Initialize))!;
        Assert.True(InvokeValidateHookMethod(officialInitTarget, initPrefix, "Prefix", out string errPre), errPre);
        Assert.True(InvokeValidateHookMethod(officialInitTarget, initPostfix!, "Postfix", out string errPost), errPost);

        // 空路径识别与默认占位图标回退契约校验
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string visSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "Modules", "CustomContentVisualModule.cs"));
        Assert.Contains("string.IsNullOrWhiteSpace(__2)", visSrc, StringComparison.Ordinal);
        Assert.Contains("_skillIconDefaultTexture", visSrc, StringComparison.Ordinal);
    }

    [Theory]
    // 以太壁垒提灯 ≥20 金翻倍：银(35->70)升金(70)应得 140；金(70->140)升钻(140)应得 280
    [InlineData("ShieldApplyAmount", 70, 35, 70, 140)]
    [InlineData("ShieldApplyAmount", 140, 70, 140, 280)]
    // 无翻倍正常升阶：银(35)升金(70)应得 70
    [InlineData("ShieldApplyAmount", 35, 35, 70, 70)]
    // 复合条件 3x（≥20 金 2x + Shielded 1.5x）：35->105 升金(70)应得 210
    [InlineData("ShieldApplyAmount", 105, 35, 70, 210)]
    // Shielded +50% 附魔（含服务端取整 35*1.5=52.5->53）：升金(70)应得 105
    [InlineData("ShieldApplyAmount", 53, 35, 70, 105)]
    [InlineData("DamageAmount", 30, 20, 40, 60)]
    // 0.5x 减半光环：40->20 升阶(80)应得 40
    [InlineData("DamageAmount", 20, 40, 80, 40)]
    // 持续时间量级属性（毫秒单位翻倍）：1000->2000 升阶(1500)应得 3000
    [InlineData("HasteAmount", 2000, 1000, 1500, 3000)]
    // 回归保护：星港罗盘短刃纯加法成长 12->23 升银(24) 必须维持加法 35，绝不误缩放
    [InlineData("DamageAmount", 23, 12, 24, 35)]
    // 回归保护：普通加法增量 12->17 升阶(24) => 29；100->115 升阶(200) => 215
    [InlineData("DamageAmount", 17, 12, 24, 29)]
    [InlineData("DamageAmount", 115, 100, 200, 215)]
    // 回归保护：极小基数防偶合（Burn 2->4 升阶 4 => 加法 6）
    [InlineData("BurnApplyAmount", 4, 2, 4, 6)]
    // 回归保护：离散/经济/冷却属性不走倍率外推，维持加法差值
    [InlineData("SellPrice", 8, 4, 8, 12)]
    [InlineData("CooldownMax", 4000, 5000, 4500, 3500)]
    [InlineData("AmmoMax", 6, 3, 5, 8)]
    // 零基数安全防除零
    [InlineData("DamageAmount", 10, 0, 20, 30)]
    public void P3_06_ComputeUpgradePreviewAttrValue_ScalesProportionalBuffs_AndPreservesAdditiveGrowth(
        string attrName,
        double curVal,
        double curBaseVal,
        double nextBaseVal,
        double expected)
    {
        Type l10nType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.LocalizationOverlayModule", throwOnError: true)!;
        MethodInfo? computeM = l10nType.GetMethod(
            "ComputeUpgradePreviewAttrValue",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(double), typeof(double), typeof(double) },
            null);
        Assert.NotNull(computeM);

        double actual = (double)computeM!.Invoke(null, new object[] { attrName, curVal, curBaseVal, nextBaseVal })!;
        Assert.Equal(expected, actual, precision: 4);
    }
}
