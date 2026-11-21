using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;

namespace BazaarCore.Mods;

/// <summary>
/// 离线 10 闸门 & 50 场真实战斗体检统一入口（供 BazaarCli 与 tools/mod_sdk_cli.py 在无服务端运行状态下直接调用真实 BazaarCore 引擎）。
/// </summary>
public static class ModWorkshopOfflineRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 执行离线 mod-validate 或 mod-sim-test，返回 (ExitCode, JsonOutput)。
    /// 退出码规范：0=全部通过；1=参数/数据缺失；2=静态闸门 #1~#8 失败；3=动态战斗 #9 或隔离回归 #10 失败。
    /// </summary>
    public static (int ExitCode, string Json) Execute(
        string command,
        string modDirectory,
        string? dataRootOverride = null,
        int matches = 50,
        int seed = 2026131,
        bool staticOnly = false)
    {
        if (string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory))
        {
            return (1, JsonSerializer.Serialize(new
            {
                ok = false,
                passed = false,
                error = $"指定的 Mod 目录不存在：{modDirectory}"
            }, JsonOpts));
        }

        string? dbSource = SteamPathResolver.ResolveGameDataSourcePath(dataRootOverride);
        string? dataRoot = ResolveDataRoot(dataRootOverride, modDirectory);
        if (string.IsNullOrEmpty(dbSource) && !string.IsNullOrEmpty(dataRoot))
            dbSource = SteamPathResolver.ResolveGameDataSourcePath(dataRoot);

        if (string.IsNullOrEmpty(dbSource))
        {
            return (1, JsonSerializer.Serialize(new
            {
                ok = false,
                passed = false,
                error = "未能定位权威数据库 GameData.db（请确认 dist/data/GameData.db 或通过 --data-root / BAZAAR_GAME_DATA_DB 指定）。"
            }, JsonOpts));
        }

        string? zhPath = SteamPathResolver.ResolveZhCnTranslationPath(dataRootOverride ?? dataRoot);
        var baseRepo = GameDatabaseLoader.Load(dbSource, zhPath);
        var whitelist = TryLoadWhitelist(dataRoot ?? Path.GetDirectoryName(dbSource) ?? string.Empty);

        string fullModDir = Path.GetFullPath(modDirectory);
        string dirName = Path.GetFileName(fullModDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var scanIssues = new List<ModValidationIssue>();
        var targetPkg = ModLoader.LoadSinglePackage(fullModDir, dirName, scanIssues);

        if (targetPkg == null)
        {
            var fatalReport = new ModTenGateValidationReport
            {
                Ok = true,
                Passed = false,
                ModId = dirName,
                ErrorCount = scanIssues.Count(i => i.Severity == ModValidationSeverity.Error),
                WarningCount = scanIssues.Count(i => i.Severity == ModValidationSeverity.Warning),
                SummaryZh = "加载 Mod 清单或 JSON 文件失败，请先修正语法错误。",
                Gates =
                [
                    new ModGateResult
                    {
                        GateId = 1,
                        Key = "schema_structure",
                        NameZh = "闸门 1：Schema 结构、废字段与四阶绑定完整性",
                        Status = "FAIL",
                        Issues = scanIssues
                    }
                ]
            };
            return (2, JsonSerializer.Serialize(fatalReport, JsonOpts));
        }

        // 同时加载同级 mods/ 目录下其他已装 Mod 作为跨包冲突/依赖上下文
        var contextPkgs = new List<ModPackage>();
        string? parentDir = Directory.GetParent(fullModDir)?.FullName;
        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
        {
            var (allScanned, _) = ModLoader.ScanDirectory(parentDir);
            contextPkgs.AddRange(allScanned);
        }

        bool runDynamic = !staticOnly || string.Equals(command, "mod-sim-test", StringComparison.OrdinalIgnoreCase);
        var report = ModValidator.RunTenGateValidation(
            targetPkg,
            contextPkgs,
            baseRepo,
            whitelist,
            combatMatches: matches,
            combatSeed: seed,
            runDynamicGates: runDynamic);

        // 将扫描期致命解析错误并入 Gate #1
        if (scanIssues.Count > 0 && report.Gates.Count > 0)
        {
            report.Gates[0].Issues.InsertRange(0, scanIssues);
            if (scanIssues.Any(i => i.Severity == ModValidationSeverity.Error))
            {
                report.Gates[0].Status = "FAIL";
                report.Passed = false;
                report.ErrorCount = report.Gates.Sum(g => g.Issues.Count(i => i.Severity == ModValidationSeverity.Error));
            }
        }

        int exitCode = 0;
        if (!report.Passed)
        {
            bool staticFailed = report.Gates.Where(g => g.GateId <= 8).Any(g => g.Status == "FAIL");
            exitCode = staticFailed ? 2 : 3;
        }

        return (exitCode, JsonSerializer.Serialize(report, JsonOpts));
    }

    /// <summary>专供反射无耦合调用的单字符串返回封装（格式："{exitCode}\n{json}"）。</summary>
    public static string ExecuteForReflection(string command, string modDirectory, string? dataRootOverride, int matches, int seed, bool staticOnly)
    {
        var (code, json) = Execute(command, modDirectory, dataRootOverride, matches, seed, staticOnly);
        return $"{code}\n{json}";
    }

    private static string? ResolveDataRoot(string? explicitRoot, string modDir)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitRoot)) candidates.Add(explicitRoot);
        string? env = Environment.GetEnvironmentVariable("BAZAAR_DATA");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

        void AddAncestors(string start)
        {
            var cur = new DirectoryInfo(Path.GetFullPath(start));
            for (int i = 0; i < 6 && cur != null; i++, cur = cur.Parent)
            {
                candidates.Add(Path.Combine(cur.FullName, "dist", "data"));
                candidates.Add(Path.Combine(cur.FullName, "data"));
            }
        }

        AddAncestors(Environment.CurrentDirectory);
        AddAncestors(AppContext.BaseDirectory);
        AddAncestors(modDir);

        foreach (var c in candidates)
        {
            if (File.Exists(Path.Combine(c, "GameData.db")) || File.Exists(Path.Combine(c, "valid_card_ids.json")))
                return Path.GetFullPath(c);
        }
        return null;
    }

    private static HashSet<string>? TryLoadWhitelist(string dataRoot)
    {
        string path = Path.Combine(dataRoot, "valid_card_ids.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.GetProperty("valid_ids");
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    set.Add(item.GetString()!);
            }
            return set.Count > 0 ? set : null;
        }
        catch
        {
            return null;
        }
    }
}
