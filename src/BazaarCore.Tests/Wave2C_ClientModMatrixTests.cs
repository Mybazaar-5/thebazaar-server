using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using BazaarCore.Tests.TestInfrastructure;
using Newtonsoft.Json.Linq;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2C 专用隔离 Fixture：同时检查 BazaarLauncher 与 BazaarModSdk 源码时间戳，
/// 在隔离 AssemblyLoadContext 中加载 BazaarLauncher.dll + BazaarModSdk.dll 及零 Unity 托管依赖，
/// 优先复用最新编译产物，缺失或过期时用独立 BaseIntermediateOutputPath 隔离构建，绝不锁死 worktree obj/。
/// </summary>
public sealed class Wave2CLauncherAssemblyFixture : IDisposable
{
    private readonly string? _tempBuildRoot;
    private readonly AssemblyLoadContext _alc;

    public Assembly LauncherAssembly { get; }
    public Assembly ModSdkAssembly { get; }
    public string LauncherDllPath { get; }
    public string ModSdkDllPath { get; }

    public Wave2CLauncherAssemblyFixture()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string mainRepoRoot = TestDataLocator.ResolveMainRepoRoot();
        string launcherDir = Path.Combine(repoRoot, "src", "BazaarLauncher");
        string sdkDir = Path.Combine(repoRoot, "src", "BazaarModSdk");
        string csprojPath = Path.Combine(launcherDir, "BazaarLauncher.csproj");
        Assert.True(File.Exists(csprojPath), $"未找到 BazaarLauncher.csproj：{csprojPath}");

        string? reusableDll = TryFindFreshBuiltDll(repoRoot, launcherDir, sdkDir);
        string outDir;
        if (reusableDll != null)
        {
            LauncherDllPath = reusableDll;
            outDir = Path.GetDirectoryName(reusableDll)!;
        }
        else
        {
            _tempBuildRoot = Path.Combine(Path.GetTempPath(), $"w2c_launcher_build_{Guid.NewGuid():N}");
            string tempObj = Path.Combine(_tempBuildRoot, "obj");
            string tempOut = Path.Combine(_tempBuildRoot, "out");
            Directory.CreateDirectory(tempObj);
            Directory.CreateDirectory(tempOut);

            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(csprojPath);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Release");
            psi.ArgumentList.Add($"-p:BaseIntermediateOutputPath={tempObj.Replace('\\', '/')}/");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(tempOut);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 dotnet build BazaarLauncher");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"隔离构建 BazaarLauncher 失败（ExitCode={proc.ExitCode}）：\n{stdout}\n{stderr}");
            }

            outDir = tempOut;
            LauncherDllPath = Path.Combine(tempOut, "BazaarLauncher.dll");
            Assert.True(File.Exists(LauncherDllPath), $"未找到隔离编译产物：{LauncherDllPath}");
        }

        ModSdkDllPath = Path.Combine(outDir, "BazaarModSdk.dll");
        if (!File.Exists(ModSdkDllPath))
        {
            ModSdkDllPath = Path.Combine(sdkDir, "bin", "Release", "netstandard2.1", "BazaarModSdk.dll");
        }
        Assert.True(File.Exists(ModSdkDllPath), $"未找到 BazaarModSdk.dll：{ModSdkDllPath}");

        string extManaged = Path.Combine(repoRoot, "ext", "Managed");
        string mainExtManaged = Path.Combine(mainRepoRoot, "ext", "Managed");
        string extBep = Path.Combine(repoRoot, "ext", "bepinex", "extracted", "BepInEx", "core");
        string mainExtBep = Path.Combine(mainRepoRoot, "ext", "bepinex", "extracted", "BepInEx", "core");
        string testBaseDir = AppContext.BaseDirectory;
        (string? msbuildManagedDir, string? msbuildBepInExCore) = ResolveMsBuildReferenceDirs(repoRoot, csprojPath);

        string[] probeDirs = new[]
        {
            outDir,
            testBaseDir,
            msbuildManagedDir ?? "",
            msbuildBepInExCore ?? "",
            extManaged,
            mainExtManaged,
            extBep,
            mainExtBep,
        }
        .Where(d => !string.IsNullOrWhiteSpace(d))
        .Select(d => Path.GetFullPath(d))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        // 使用非可卸载上下文（isCollectible: false），避免多测试类并行执行期间 Dispose/Unload 撞碎并发 GetAssemblies() 扫描
        _alc = new AssemblyLoadContext($"Wave2C_Launcher_{Guid.NewGuid():N}", isCollectible: false);
        _alc.Resolving += (ctx, asmName) =>
        {
            // 与主默认上下文共享 Newtonsoft.Json / BazaarGameShared，避免类型身份不一致与重复落盘
            if (string.Equals(asmName.Name, "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase))
                return typeof(JObject).Assembly;
            if (string.Equals(asmName.Name, "BazaarGameShared", StringComparison.OrdinalIgnoreCase))
                return typeof(BazaarGameShared.Domain.Core.Types.EHero).Assembly;

            foreach (string dir in probeDirs)
            {
                if (!Directory.Exists(dir)) continue;
                string cand = Path.Combine(dir, asmName.Name + ".dll");
                if (File.Exists(cand))
                {
                    try
                    {
                        using var depMs = new MemoryStream(File.ReadAllBytes(cand));
                        return ctx.LoadFromStream(depMs);
                    }
                    catch (InvalidOperationException)
                    {
                        return null;
                    }
                }
            }
            return null;
        };

        using (var sdkMs = new MemoryStream(File.ReadAllBytes(ModSdkDllPath)))
        {
            ModSdkAssembly = _alc.LoadFromStream(sdkMs);
        }
        using (var lMs = new MemoryStream(File.ReadAllBytes(LauncherDllPath)))
        {
            LauncherAssembly = _alc.LoadFromStream(lMs);
        }
    }

    private static string? TryFindFreshBuiltDll(string repoRoot, string launcherDir, string sdkDir)
    {
        static DateTime LatestIn(string dir) => !Directory.Exists(dir)
            ? DateTime.MinValue
            : Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(p =>
                    !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

        DateTime latestSourceUtc = new[]
        {
            LatestIn(launcherDir),
            LatestIn(sdkDir),
            File.Exists(Path.Combine(repoRoot, "Directory.Build.props"))
                ? File.GetLastWriteTimeUtc(Path.Combine(repoRoot, "Directory.Build.props"))
                : DateTime.MinValue,
        }.Max();

        string[] candidates =
        {
            Path.Combine(launcherDir, "bin", "Release", "netstandard2.1", "BazaarLauncher.dll"),
            Path.Combine(launcherDir, "bin", "Debug", "netstandard2.1", "BazaarLauncher.dll"),
        };

        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string c in candidates)
        {
            if (!File.Exists(c)) continue;
            DateTime t = File.GetLastWriteTimeUtc(c);
            if (t >= latestSourceUtc && t > bestTime)
            {
                best = c;
                bestTime = t;
            }
        }
        return best;
    }

    private static (string? ManagedDir, string? BepInExCore) ResolveMsBuildReferenceDirs(string repoRoot, string csprojPath)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("msbuild");
            psi.ArgumentList.Add(csprojPath);
            psi.ArgumentList.Add("-getProperty:ManagedDir");
            psi.ArgumentList.Add("-getProperty:BepInExCore");
            psi.ArgumentList.Add("--nologo");

            using var proc = Process.Start(psi);
            if (proc == null) return (null, null);
            string stdout = proc.StandardOutput.ReadToEnd();
            _ = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(15000) || proc.ExitCode != 0)
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                return (null, null);
            }

            var root = JObject.Parse(stdout);
            var props = root["Properties"] as JObject;
            string? managed = props?["ManagedDir"]?.Value<string>();
            string? bep = props?["BepInExCore"]?.Value<string>();
            return (
                string.IsNullOrWhiteSpace(managed) ? null : managed.Trim(),
                string.IsNullOrWhiteSpace(bep) ? null : bep.Trim());
        }
        catch
        {
            return (null, null);
        }
    }

    public void Dispose()
    {
        if (_alc.IsCollectible)
        {
            try { _alc.Unload(); } catch { }
        }
        if (!string.IsNullOrWhiteSpace(_tempBuildRoot))
        {
            try { if (Directory.Exists(_tempBuildRoot)) Directory.Delete(_tempBuildRoot, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Wave 2C 模块一独占测试：验证自定义 Mod 客户端 8 矩阵闭环纯逻辑内核
/// （P0-18 字段级合并、P0-20 融合逻辑身份、P0-21/26/27 plan_w2a §0.1 契约解析与双语借壳被动清零、
/// P0-28 WebP 方案 A、D3-P1-02 附魔乘偏、D3-P1-07 徽章 5×6 格式化 &amp; MaxAttr、D7-P1-02 离线词典选新）。
/// </summary>
public sealed class Wave2C_ClientModMatrixTests : IClassFixture<Wave2CLauncherAssemblyFixture>
{
    private readonly Wave2CLauncherAssemblyFixture _fx;

    public Wave2C_ClientModMatrixTests(Wave2CLauncherAssemblyFixture fx) => _fx = fx;

    [Fact]
    public void P0_18_FieldLevelMerge_HighPriorityTextOnlyOverride_PreservesLowerPriorityMetadata()
    {
        Type l10nType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.LocalizationOverlayModule", throwOnError: true)!;
        Type ovType = _fx.ModSdkAssembly.GetType("BazaarModSdk.CardTextOverride", throwOnError: true)!;
        MethodInfo? mergeMethod = l10nType.GetMethod("MergeCardOverridesForTest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(mergeMethod);

        // 模拟：低优先级 visualBindings（100）带有完整 TierAttributes/CounterDisplays/ExtraDisplayTags/PassiveTooltips
        object visualOv = Activator.CreateInstance(ovType)!;
        ovType.GetProperty("TemplateId")!.SetValue(visualOv, "donor-tpl-1");
        ovType.GetProperty("CustomCardId")!.SetValue(visualOv, "mod_sword_a");
        ovType.GetProperty("Priority")!.SetValue(visualOv, 100);
        ovType.GetProperty("Title")!.SetValue(visualOv, "旧标题");
        ovType.GetProperty("TitleEn")!.SetValue(visualOv, "Ember Blade");
        ovType.GetProperty("Description")!.SetValue(visualOv, "原描述");
        ovType.GetProperty("IsCustomBorrowShell")!.SetValue(visualOv, true);
        ovType.GetProperty("ExtraDisplayTags")!.SetValue(visualOv, new[] { "Relic", "Starforge" });
        ovType.GetProperty("TierAttributes")!.SetValue(visualOv, new Dictionary<string, Dictionary<string, double>>
        {
            ["Bronze"] = new Dictionary<string, double> { ["DamageAmount"] = 12 }
        });

        // 模拟：高优先级纯文本 cardOverrides（150）仅覆盖 Title，其余字段为 null
        object textOnlyOv = Activator.CreateInstance(ovType)!;
        ovType.GetProperty("TemplateId")!.SetValue(textOnlyOv, "donor-tpl-1");
        ovType.GetProperty("CustomCardId")!.SetValue(textOnlyOv, "mod_sword_a");
        ovType.GetProperty("Priority")!.SetValue(textOnlyOv, 150);
        ovType.GetProperty("Title")!.SetValue(textOnlyOv, "烬炎圣剑");

        Array arr = Array.CreateInstance(ovType, 2);
        arr.SetValue(visualOv, 0);
        arr.SetValue(textOnlyOv, 1);

        object? merged = mergeMethod!.Invoke(null, new object?[] { arr, null, "mod_sword_a", "donor-tpl-1" });
        Assert.NotNull(merged);
        Assert.Equal("烬炎圣剑", ovType.GetProperty("Title")!.GetValue(merged));
        Assert.Equal("Ember Blade", ovType.GetProperty("TitleEn")!.GetValue(merged));
        Assert.Equal("原描述", ovType.GetProperty("Description")!.GetValue(merged));
        Assert.Equal(true, ovType.GetProperty("IsCustomBorrowShell")!.GetValue(merged));

        var mergedTiers = ovType.GetProperty("TierAttributes")!.GetValue(merged) as Dictionary<string, Dictionary<string, double>>;
        Assert.NotNull(mergedTiers);
        Assert.Equal(12, mergedTiers!["Bronze"]["DamageAmount"]);

        var mergedTags = ovType.GetProperty("ExtraDisplayTags")!.GetValue(merged) as string[];
        Assert.NotNull(mergedTags);
        Assert.Contains("Starforge", mergedTags!);
    }

    [Fact]
    public void P0_18_P0_27_MergeCardOverrides_IsolatesBorrowShellNewCardFromSameTemplateInPlacePatch()
    {
        Type l10nType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.LocalizationOverlayModule", throwOnError: true)!;
        Type ovType = _fx.ModSdkAssembly.GetType("BazaarModSdk.CardTextOverride", throwOnError: true)!;
        Type counterType = ovType.GetProperty("CounterDisplays")!.PropertyType.GetElementType()!;
        MethodInfo? mergeMethod = l10nType.GetMethod("MergeCardOverridesForTest", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? shouldClearPassive = l10nType.GetMethod("ShouldSuppressVanillaPassives", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(mergeMethod);
        Assert.NotNull(shouldClearPassive);

        // Mod A 借壳新卡（visualBindings, priority=100）：未配 PassiveTooltips/TitleEn/CounterDisplays/TierAttributes
        object modABorrowVisual = Activator.CreateInstance(ovType)!;
        ovType.GetProperty("ModId")!.SetValue(modABorrowVisual, "mod_a");
        ovType.GetProperty("TemplateId")!.SetValue(modABorrowVisual, "donor-tpl-shared");
        ovType.GetProperty("CustomCardId")!.SetValue(modABorrowVisual, "mod_a:star_blade");
        ovType.GetProperty("Priority")!.SetValue(modABorrowVisual, 100);
        ovType.GetProperty("Title")!.SetValue(modABorrowVisual, "星辰借壳刃");
        ovType.GetProperty("IsCustomBorrowShell")!.SetValue(modABorrowVisual, true);
        ovType.GetProperty("IsPatchOverride")!.SetValue(modABorrowVisual, false);

        // Mod A 借壳新卡同 CustomCardId 的高优文案覆写（cardOverrides, priority=150）
        object modABorrowText = Activator.CreateInstance(ovType)!;
        ovType.GetProperty("ModId")!.SetValue(modABorrowText, "mod_a");
        ovType.GetProperty("TemplateId")!.SetValue(modABorrowText, "donor-tpl-shared");
        ovType.GetProperty("CustomCardId")!.SetValue(modABorrowText, "mod_a:star_blade");
        ovType.GetProperty("Priority")!.SetValue(modABorrowText, 150);
        ovType.GetProperty("Description")!.SetValue(modABorrowText, "造成 20 点星辰伤害");

        // Mod B 对同一原版底座 TemplateId 的原位 Patch（IsPatchOverride=true, priority=200，塞满所有可选字段）
        object modBInPlacePatch = Activator.CreateInstance(ovType)!;
        ovType.GetProperty("ModId")!.SetValue(modBInPlacePatch, "mod_b");
        ovType.GetProperty("TemplateId")!.SetValue(modBInPlacePatch, "donor-tpl-shared");
        ovType.GetProperty("Priority")!.SetValue(modBInPlacePatch, 200);
        ovType.GetProperty("Title")!.SetValue(modBInPlacePatch, "魔改原版长剑");
        ovType.GetProperty("TitleEn")!.SetValue(modBInPlacePatch, "Patched Vanilla Sword");
        ovType.GetProperty("Description")!.SetValue(modBInPlacePatch, "原位 Patch 描述");
        ovType.GetProperty("PassiveTooltips")!.SetValue(modBInPlacePatch, new[] { "原位 Patch 被动词条" });
        ovType.GetProperty("IsPatchOverride")!.SetValue(modBInPlacePatch, true);
        ovType.GetProperty("IsCustomBorrowShell")!.SetValue(modBInPlacePatch, false);
        ovType.GetProperty("EnchantmentTooltips")!.SetValue(modBInPlacePatch, new Dictionary<string, string[]>
        {
            ["Heavy"] = new[] { "减速 2 秒" }
        });
        ovType.GetProperty("TierAttributes")!.SetValue(modBInPlacePatch, new Dictionary<string, Dictionary<string, double>>
        {
            ["Bronze"] = new Dictionary<string, double> { ["DamageAmount"] = 99 }
        });
        Array patchCounters = Array.CreateInstance(counterType, 1);
        patchCounters.SetValue(Activator.CreateInstance(counterType)!, 0);
        ovType.GetProperty("CounterDisplays")!.SetValue(modBInPlacePatch, patchCounters);

        Array arr = Array.CreateInstance(ovType, 3);
        arr.SetValue(modABorrowVisual, 0);
        arr.SetValue(modABorrowText, 1);
        arr.SetValue(modBInPlacePatch, 2);

        // 1. 查询 Mod A 借壳新卡：只合并 Mod A 自身字段，绝不混入 Mod B 原位 Patch 的任何字段，且不击穿 ShouldSuppressVanillaPassives
        object? borrowMerged = mergeMethod!.Invoke(null, new object?[] { arr, "inst-a-1", "mod_a:star_blade", "donor-tpl-shared" });
        Assert.NotNull(borrowMerged);
        Assert.Equal("星辰借壳刃", ovType.GetProperty("Title")!.GetValue(borrowMerged));
        Assert.Equal("造成 20 点星辰伤害", ovType.GetProperty("Description")!.GetValue(borrowMerged));
        Assert.True(string.IsNullOrEmpty((string?)ovType.GetProperty("TitleEn")!.GetValue(borrowMerged)));
        Assert.Null(ovType.GetProperty("PassiveTooltips")!.GetValue(borrowMerged));
        Assert.Null(ovType.GetProperty("CounterDisplays")!.GetValue(borrowMerged));
        Assert.Null(ovType.GetProperty("EnchantmentTooltips")!.GetValue(borrowMerged));
        Assert.Null(ovType.GetProperty("TierAttributes")!.GetValue(borrowMerged));
        Assert.Equal(true, ovType.GetProperty("IsCustomBorrowShell")!.GetValue(borrowMerged));
        Assert.Equal(false, ovType.GetProperty("IsPatchOverride")!.GetValue(borrowMerged));

        bool isBorrow = (bool)ovType.GetProperty("IsCustomBorrowShell")!.GetValue(borrowMerged)!;
        var passives = ovType.GetProperty("PassiveTooltips")!.GetValue(borrowMerged) as string[];
        Assert.Equal(true, shouldClearPassive!.Invoke(null, new object?[] { isBorrow, passives }));

        // 2. 查询原版底座卡（无 CustomCardId）：仍干净命中 Mod B 原位 Patch，绝不被 Mod A 借壳卡污染
        object? vanillaMerged = mergeMethod.Invoke(null, new object?[] { arr, null, null, "donor-tpl-shared" });
        Assert.NotNull(vanillaMerged);
        Assert.Equal("魔改原版长剑", ovType.GetProperty("Title")!.GetValue(vanillaMerged));
        Assert.Equal("Patched Vanilla Sword", ovType.GetProperty("TitleEn")!.GetValue(vanillaMerged));
        Assert.Equal(true, ovType.GetProperty("IsPatchOverride")!.GetValue(vanillaMerged));
        Assert.Equal(false, ovType.GetProperty("IsCustomBorrowShell")!.GetValue(vanillaMerged));
    }

    [Fact]
    public void P0_20_CardIdentityResolver_IsolatesBorrowShellCards_AndPreservesVanillaPatch()
    {
        Type resolverType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.CardIdentityResolver", throwOnError: true)!;
        MethodInfo? resolveMethod = resolverType.GetMethod(
            "ResolveLogicalIdentity",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(string), typeof(bool) },
            null);
        Assert.NotNull(resolveMethod);

        string idA = (string)resolveMethod!.Invoke(null, new object?[] { "tpl-donor-001", "custom_mod_card_a", false })!;
        string idB = (string)resolveMethod.Invoke(null, new object?[] { "tpl-donor-001", "custom_mod_card_b", false })!;
        string idVanilla = (string)resolveMethod.Invoke(null, new object?[] { "tpl-donor-001", null, false })!;
        string idPatch = (string)resolveMethod.Invoke(null, new object?[] { "tpl-donor-001", "tpl-donor-001", true })!;
        string idPatchFlag = (string)resolveMethod.Invoke(null, new object?[] { "tpl-donor-001", "custom_alias", true })!;

        Assert.NotEqual(idA, idB);
        Assert.NotEqual(idA, idVanilla);
        Assert.Equal("custom:custom_mod_card_a", idA);
        Assert.Equal("vanilla:tpl-donor-001", idVanilla);
        Assert.Equal(idVanilla, idPatch);
        Assert.Equal(idVanilla, idPatchFlag);
    }

    [Fact]
    public void P0_27_BilingualDisplayAndBorrowShellPassiveIsolation_WorksForZhAndEn()
    {
        Type l10nType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.LocalizationOverlayModule", throwOnError: true)!;
        MethodInfo? pickText = l10nType.GetMethod(
            "PickLocalizedString",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(bool), typeof(string), typeof(string), typeof(bool) },
            null);
        Assert.NotNull(pickText);

        // 中文态：优先中文，缺中文退英文
        Assert.Equal("中文名", pickText!.Invoke(null, new object?[] { true, "中文名", "English Name", true }));
        Assert.Equal("English Name", pickText.Invoke(null, new object?[] { true, "", "English Name", true }));

        // 英文态 + 全新借壳自定义卡：优先英文，缺英文回退中文，绝不返回 null 跌落原版底座
        Assert.Equal("English Name", pickText.Invoke(null, new object?[] { false, "中文名", "English Name", true }));
        Assert.Equal("中文名", pickText.Invoke(null, new object?[] { false, "中文名", "", true }));

        // 英文态 + 原位 Patch：有英文覆写则用英文，缺英文返回 null 保留原版英文
        Assert.Equal("Patched En", pickText.Invoke(null, new object?[] { false, "补丁中文", "Patched En", false }));
        Assert.Null(pickText.Invoke(null, new object?[] { false, "补丁中文", "", false }));

        // 被动词条防泄漏规则
        MethodInfo? shouldClearPassive = l10nType.GetMethod(
            "ShouldSuppressVanillaPassives",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(shouldClearPassive);
        Assert.Equal(true, shouldClearPassive!.Invoke(null, new object?[] { true, null }));
        Assert.Equal(true, shouldClearPassive.Invoke(null, new object?[] { true, Array.Empty<string>() }));
        Assert.Equal(false, shouldClearPassive.Invoke(null, new object?[] { false, null }));
    }

    [Fact]
    public void P0_21_24_27_D3P1_02_PlanW2AContractParsing_MapsAllExactKeysAndVisualTargets()
    {
        Type syncType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.ModContentSyncClient", throwOnError: true)!;
        MethodInfo? mapTarget = syncType.GetMethod("MapVisualTarget", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? parseEnchantMap = syncType.GetMethod("ParseEnchantTooltipsMap", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? parseMonster = syncType.GetMethod("ParseMonsterPreview", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? evalPreviewOp = _fx.LauncherAssembly
            .GetType("BazaarLauncher.Modules.LocalizationOverlayModule", throwOnError: true)!
            .GetMethod("EvaluatePreviewAttrDerivedValue", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mapTarget);
        Assert.NotNull(parseEnchantMap);
        Assert.NotNull(parseMonster);
        Assert.NotNull(evalPreviewOp);

        // 1. VisualTarget 双向映射（plan_w2a Rev 2 枚举值 + 旧/通用别名 "card"/"event"/"monster"）
        Assert.Equal("ItemArt", mapTarget!.Invoke(null, new object?[] { "ItemArt", "Item" })!.ToString());
        Assert.Equal("SkillIcon", mapTarget.Invoke(null, new object?[] { "SkillIcon", "Skill" })!.ToString());
        Assert.Equal("EncounterPortrait", mapTarget.Invoke(null, new object?[] { "EncounterPortrait", "Event" })!.ToString());
        Assert.Equal("EncounterBackground", mapTarget.Invoke(null, new object?[] { "EncounterBackground", "Event" })!.ToString());
        Assert.Equal("SkillIcon", mapTarget.Invoke(null, new object?[] { "card", "Skill" })!.ToString());
        Assert.Equal("ItemArt", mapTarget.Invoke(null, new object?[] { "card", "Item" })!.ToString());
        Assert.Equal("EncounterPortrait", mapTarget.Invoke(null, new object?[] { "event", "Event" })!.ToString());
        Assert.Equal("EncounterPortrait", mapTarget.Invoke(null, new object?[] { "monster", "CombatEncounter" })!.ToString());

        // 2. enchantTooltipsEn 同时兼容 Dictionary<string, List<string>> 与 Dictionary<string, string>
        var mixObj = JObject.Parse("""
        {
          "Fiery": ["Burn equal to 10% of Damage", "Gain 1 Burn"],
          "Heavy": "Slow 1 item for 2s"
        }
        """);
        var parsedEnch = parseEnchantMap!.Invoke(null, new object?[] { mixObj }) as Dictionary<string, string[]>;
        Assert.NotNull(parsedEnch);
        Assert.Equal(2, parsedEnch!["Fiery"].Length);
        Assert.Single(parsedEnch["Heavy"]);
        Assert.Equal("Slow 1 item for 2s", parsedEnch["Heavy"][0]);

        // 3. CustomMonsterPreviewBinding 精准解析（rewardGold/rewardXp/items[] 含 size/wireTemplateId/cardId）
        var mpJson = JObject.Parse("""
        {
          "monsterId": "mod_boss_01",
          "nameZh": "星港巨像",
          "nameEn": "Starport Colossus",
          "level": 9,
          "healthMax": 1200,
          "rewardGold": 8,
          "rewardXp": 4,
          "items": [
            { "cardId": "mod_cannon", "wireTemplateId": "00000000-0000-0000-0000-000000000001", "tier": "Gold", "size": "Large", "enchantment": "Fiery" }
          ],
          "skills": [
            { "cardId": "mod_aura", "wireTemplateId": "00000000-0000-0000-0000-000000000002", "tier": "Silver" }
          ]
        }
        """);
        object? mp = parseMonster!.Invoke(null, new object?[] { mpJson });
        Assert.NotNull(mp);
        Type mpType = mp!.GetType();
        Assert.Equal(1200, mpType.GetProperty("HealthMax")!.GetValue(mp));
        Assert.Equal(8, mpType.GetProperty("RewardGold")!.GetValue(mp));
        Assert.Equal(4, mpType.GetProperty("RewardXp")!.GetValue(mp));
        Assert.Equal(9, mpType.GetProperty("Level")!.GetValue(mp));
        var items = mpType.GetProperty("Items")!.GetValue(mp) as Array;
        Assert.NotNull(items);
        Assert.Single(items!);
        object item0 = items!.GetValue(0)!;
        Assert.Equal("mod_cannon", item0.GetType().GetProperty("CardId")!.GetValue(item0));
        Assert.Equal("Large", item0.GetType().GetProperty("Size")!.GetValue(item0));

        // 4. D3-P1-02 附魔乘数与加偏移公式：src=40, mult=0.25, addOffset=3 -> 13
        int derived = (int)evalPreviewOp!.Invoke(null, new object?[] { 40, 0.25d, 3.0d })!;
        Assert.Equal(13, derived);
    }

    [Fact]
    public void P0_28_CardTextureCache_AllowsPngJpeg_DetectsAndRejectsWebp()
    {
        Type cacheType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.CardTextureCache", throwOnError: true)!;
        MethodInfo? isSupported = cacheType.GetMethod("IsSupportedImageMagic", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? isWebp = cacheType.GetMethod("IsWebpMagic", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(isSupported);
        Assert.NotNull(isWebp);

        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        byte[] jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1 };
        byte[] webp = Encoding.ASCII.GetBytes("RIFF\x24\x00\x00\x00WEBPVP8 ");

        Assert.Equal(true, isSupported!.Invoke(null, new object?[] { png }));
        Assert.Equal(true, isSupported.Invoke(null, new object?[] { jpg }));
        Assert.Equal(false, isSupported.Invoke(null, new object?[] { webp }));
        Assert.Equal(true, isWebp!.Invoke(null, new object?[] { webp }));
        Assert.Equal(false, isWebp.Invoke(null, new object?[] { png }));
    }

    [Fact]
    public void D3_P1_07_BadgeFormatEvaluator_FixesAllFiveStylesAndSixValueFormats()
    {
        Type evalType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.BadgeFormatEvaluator", throwOnError: true)!;
        MethodInfo? evalMethod = evalType.GetMethod("Evaluate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(evalMethod);

        // 参数顺序：(int curVal, int maxVal, string style, string valueFormat, string label, string unit, string format, bool hideWhenZero)
        // 1. Format 含 {label} 抑制独立 LabelText
        object r1 = evalMethod!.Invoke(null, new object?[] { 5, 0, "pill", "int", "层数", "", "{label}:{val}", true })!;
        Assert.Equal("层数:5", r1.GetType().GetProperty("DisplayText")!.GetValue(r1));
        Assert.Equal(true, r1.GetType().GetProperty("SuppressSeparateLabel")!.GetValue(r1));
        Assert.Equal(false, r1.GetType().GetProperty("IsHidden")!.GetValue(r1));

        // 2. seconds >= 100 毫秒转秒且不重复拼单位
        object r2 = evalMethod.Invoke(null, new object?[] { 2500, 0, "pill", "seconds", "冷却", "s", "", false })!;
        Assert.Equal("2.5s", r2.GetType().GetProperty("DisplayText")!.GetValue(r2));

        // 3. percent 避免 %% 重复
        object r3 = evalMethod.Invoke(null, new object?[] { 35, 0, "pill", "percent", "暴击", "%", "", false })!;
        Assert.Equal("35%", r3.GetType().GetProperty("DisplayText")!.GetValue(r3));

        // 4. multiplier 格式化
        object r4 = evalMethod.Invoke(null, new object?[] { 3, 0, "pill", "multiplier", "倍率", "", "", false })!;
        Assert.Equal("x3", r4.GetType().GetProperty("DisplayText")!.GetValue(r4));

        // 5. HideWhenZero 保留负 delta（-3 不可隐藏，0 才隐藏）
        object rNeg = evalMethod.Invoke(null, new object?[] { -3, 0, "pill", "delta", "减益", "", "", true })!;
        Assert.Equal(false, rNeg.GetType().GetProperty("IsHidden")!.GetValue(rNeg));
        Assert.Equal("-3", rNeg.GetType().GetProperty("DisplayText")!.GetValue(rNeg));

        object rZero = evalMethod.Invoke(null, new object?[] { 0, 0, "pill", "delta", "减益", "", "", true })!;
        Assert.Equal(true, rZero.GetType().GetProperty("IsHidden")!.GetValue(rZero));

        // 6. bar 无上限退化为 pill；pips > 6 降级为 bar
        object rBarNoMax = evalMethod.Invoke(null, new object?[] { 4, 0, "bar", "int", "充能", "", "", false })!;
        Assert.Equal("pill", rBarNoMax.GetType().GetProperty("EffectiveStyle")!.GetValue(rBarNoMax));

        object rPipsOver = evalMethod.Invoke(null, new object?[] { 7, 10, "pips", "fraction", "弹药", "", "", false })!;
        Assert.Equal("bar", rPipsOver.GetType().GetProperty("EffectiveStyle")!.GetValue(rPipsOver));
        Assert.Equal("7/10", rPipsOver.GetType().GetProperty("DisplayText")!.GetValue(rPipsOver));
    }

    [Fact]
    public void D7_P1_02_OverlayCandidateSelector_PicksHigherRevisionThenNewerTimestamp()
    {
        Type selType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.OverlayCandidateSelector", throwOnError: true)!;
        MethodInfo? pickMethod = selType.GetMethod("PickLatestPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(pickMethod);

        string sandbox = Path.Combine(Path.GetTempPath(), $"w2c_overlay_pick_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        try
        {
            string olderRevPath = Path.Combine(sandbox, "overlay_cache.json");
            string newerRevPath = Path.Combine(sandbox, "custom_zh_overlay.json");
            File.WriteAllText(olderRevPath, """{"revision":"2026.10.01","uiExact":{}}""", Encoding.UTF8);
            File.WriteAllText(newerRevPath, """{"revision":"2026.11.20","uiExact":{}}""", Encoding.UTF8);
            // 故意把旧修订文件时间戳设得更晚，验证高 revision 优先
            File.SetLastWriteTimeUtc(olderRevPath, DateTime.UtcNow.AddMinutes(5));
            File.SetLastWriteTimeUtc(newerRevPath, DateTime.UtcNow);

            string? picked = (string?)pickMethod!.Invoke(null, new object?[] { new[] { olderRevPath, newerRevPath } });
            Assert.Equal(newerRevPath, picked);

            // 同 revision 时按 LastWriteTimeUtc 更新者胜出
            File.WriteAllText(olderRevPath, """{"revision":"2026.11.20","uiExact":{}}""", Encoding.UTF8);
            File.SetLastWriteTimeUtc(olderRevPath, DateTime.UtcNow.AddMinutes(10));
            File.SetLastWriteTimeUtc(newerRevPath, DateTime.UtcNow);
            string? pickedByTime = (string?)pickMethod.Invoke(null, new object?[] { new[] { newerRevPath, olderRevPath } });
            Assert.Equal(olderRevPath, pickedByTime);
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }
}
