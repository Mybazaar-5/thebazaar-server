using System.Text;

namespace BazaarDatasetGen;

/// <summary>
/// PvP 数据集生成与真实数据合并命令行参数配置（v2 方案 A：高拟真天梯生态与 12k 扩容）。
/// </summary>
public sealed class DatasetGenOptions
{
    /// <summary>淘汰赛总参赛规模（默认 24,000 局，按 8 英雄均分每英雄 3,000 局）。</summary>
    public int TournamentSize { get; set; } = 24_000;

    /// <summary>精选完整对局 Run 目标数量（默认 12,000 局，每英雄 1,500 局）。</summary>
    public int TargetRuns { get; set; } = 12_000;

    /// <summary>均衡单日镜像池每日目标采样条数（默认 480 条/天，15 天共 7,200 条）。</summary>
    public int PoolPerDay { get; set; } = 480;

    /// <summary>自然漏斗池 Day 1~6 前期每日基准条数（默认 1,250 条/天，随真实存活率逐日递减，合计约 14,000~16,000 条）。</summary>
    public int NaturalPoolEarlyPerDay { get; set; } = 1_250;

    /// <summary>自然漏斗池 Day 15+ 后期每日保底条数（默认 400 条/天）。</summary>
    public int NaturalPoolLateMinPerDay { get; set; } = 400;

    /// <summary>单局最大推进天数（默认 17 天以覆盖 Day 15+ 深水区）。</summary>
    public int MaxDay { get; set; } = 17;

    /// <summary>深水区保障时每个 (DayBucket, Hero) 的最小去重候选盘面数。</summary>
    public int MinCandidatesPerDayHeroCell { get; set; } = 80;

    /// <summary>同天数交叉验证使用的代表性基准对手数量（默认 20，Day 10+ 含 5 位 5.0.0 社区 10 胜考官与 15 位合成盘）。</summary>
    public int BenchmarkOpponentsPerDay { get; set; } = 20;

    /// <summary>全局确定性随机种子。</summary>
    public int Seed { get; set; } = 20260501;

    /// <summary>最大并发线程数（默认使用全部逻辑核心）。</summary>
    public int MaxDegreeOfParallelism { get; set; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>输出目录路径。</summary>
    public string OutputDir { get; set; } = Path.Combine("output", "pvp_dataset");

    /// <summary>可选自定义 GameData.db 或 GameData.db.zip 路径。</summary>
    public string? DataPath { get; set; }

    /// <summary>可选自定义 zh-CN.bytes 本地化词库路径。</summary>
    public string? TranslationPath { get; set; }

    /// <summary>可选外部天梯统计源四层真值蒸馏包（compass_distilled.json）路径。</summary>
    public string? CompassDataPath { get; set; }

    /// <summary>若设置则进入真实社区数据导入模式，读取该 JSON/JSONL 路径。</summary>
    public string? ImportRealPath { get; set; }

    /// <summary>是否严格要求卡牌具备官方简体中文名称。</summary>
    public bool RequireOfficialZh { get; set; } = true;

    /// <summary>是否仅显示命令行帮助后退出。</summary>
    public bool ShowHelp { get; set; }

    /// <summary>
    /// 解析命令行参数（同时兼容 --runs 与 --target-runs 别名）。
    /// </summary>
    public static DatasetGenOptions Parse(string[] args)
    {
        var opts = new DatasetGenOptions();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    opts.ShowHelp = true;
                    break;
                case "--tournament-size" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var ts) && ts > 0) opts.TournamentSize = ts;
                    break;
                case "--runs":
                case "--target-runs":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var tr) && tr > 0) opts.TargetRuns = tr;
                    break;
                case "--pool-per-day" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var ppd) && ppd > 0) opts.PoolPerDay = ppd;
                    break;
                case "--natural-early" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var ne) && ne > 0) opts.NaturalPoolEarlyPerDay = ne;
                    break;
                case "--natural-late-min" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var nl) && nl > 0) opts.NaturalPoolLateMinPerDay = nl;
                    break;
                case "--max-day" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var md) && md >= 15) opts.MaxDay = md;
                    break;
                case "--min-cell-candidates" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var mc) && mc > 0) opts.MinCandidatesPerDayHeroCell = mc;
                    break;
                case "--benchmarks" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var bm) && bm >= 8) opts.BenchmarkOpponentsPerDay = bm;
                    break;
                case "--seed" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var sd)) opts.Seed = sd;
                    break;
                case "--threads" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var th) && th > 0) opts.MaxDegreeOfParallelism = th;
                    break;
                case "--out" when i + 1 < args.Length:
                    opts.OutputDir = args[++i];
                    break;
                case "--data" when i + 1 < args.Length:
                    opts.DataPath = args[++i];
                    break;
                case "--zh" when i + 1 < args.Length:
                    opts.TranslationPath = args[++i];
                    break;
                case "--compass" when i + 1 < args.Length:
                    opts.CompassDataPath = args[++i];
                    break;
                case "--import-real" when i + 1 < args.Length:
                    opts.ImportRealPath = args[++i];
                    break;
                case "--allow-non-zh":
                    opts.RequireOfficialZh = false;
                    break;
            }
        }
        return opts;
    }

    /// <summary>
    /// 返回 CLI 使用说明文本。
    /// </summary>
    public static string GetHelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BazaarDatasetGen - 《The Bazaar》高拟真逐日 PvP 阵容数据集生成与双源合并工具 (v2)");
        sb.AppendLine("用法:");
        sb.AppendLine("  BazaarDatasetGen [选项]");
        sb.AppendLine("  BazaarDatasetGen --import-real <json/jsonl路径> [--out <目录>] [--data <db路径>]");
        sb.AppendLine("选项:");
        sb.AppendLine("  --tournament-size <N>   淘汰赛规模（默认 24000）");
        sb.AppendLine("  --runs / --target-runs <N>  精选完整 Run 数量（默认 12000）");
        sb.AppendLine("  --pool-per-day <N>      均衡池每天目标条数（默认 480，15 天共 7200 条）");
        sb.AppendLine("  --natural-early <N>     自然漏斗池 Day 1~6 每日条数（默认 1250）");
        sb.AppendLine("  --natural-late-min <N>  自然漏斗池 Day 15+ 每日保底条数（默认 400）");
        sb.AppendLine("  --max-day <N>           单局最大天数（默认 17，需 >= 15）");
        sb.AppendLine("  --benchmarks <N>        每日交叉定级基准对手数（默认 20）");
        sb.AppendLine("  --threads <N>           并发线程数（默认 CPU 核心数）");
        sb.AppendLine("  --seed <N>              全局随机种子（默认 20260501）");
        sb.AppendLine($"  --out <目录>            输出目录（默认 {Path.Combine("output", "pvp_dataset")}）");
        sb.AppendLine("  --data <路径>           自定义 GameData.db / GameData.db.zip 路径");
        sb.AppendLine("  --zh <路径>             自定义 zh-CN.bytes 路径");
        sb.AppendLine("  --compass <路径>        指定外部天梯统计源四层真值蒸馏包 compass_distilled.json");
        sb.AppendLine("  --import-real <路径>    导入外部脱敏社区玩家数据 JSON/JSONL 至 SQLite");
        sb.AppendLine("  --allow-non-zh          允许缺少官方中文名的卡牌（仅调试用）");
        return sb.ToString();
    }
}
