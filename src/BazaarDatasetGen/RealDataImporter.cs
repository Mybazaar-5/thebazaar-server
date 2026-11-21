using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Models;
using Microsoft.Data.Sqlite;

namespace BazaarDatasetGen;

/// <summary>
/// 外部真实社区玩家数据导入结果统计。
/// </summary>
public sealed class RealImportResult
{
    public int TotalBoardsRead { get; init; }
    public int ImportedRuns { get; init; }
    public int ImportedBoards { get; init; }
    public int SkippedDuplicateBoards { get; init; }
    public int FailedValidationCount { get; init; }
    public IReadOnlyList<string> SampleErrors { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 外部真实社区数据并池导入器（--import-real &lt;path&gt;）：
/// 支持 JSON 数组、包装对象或 JSONL（兼容 PvpBoardSnapshot 与 PvpRunRecord 两种输入结构），
/// 强制标注 source = "real_community"，补齐官方简体中文名，通过 IBoardValidator 校验后幂等写入 SQLite。
/// </summary>
public sealed class RealDataImporter
{
    private readonly GameDataRepository _repo;
    private readonly IBoardValidator _validator;
    private readonly Dictionary<string, CardTemplate> _itemsByInternalName;
    private readonly Dictionary<string, CardTemplate> _skillsByInternalName;

    public RealDataImporter(GameDataRepository repo, IBoardValidator validator)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));

        _itemsByInternalName = new Dictionary<string, CardTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in repo.AllItems)
            _itemsByInternalName.TryAdd(item.InternalName, item);

        _skillsByInternalName = new Dictionary<string, CardTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in repo.AllSkills)
            _skillsByInternalName.TryAdd(skill.InternalName, skill);
    }

    /// <summary>
    /// 读取外部 JSON/JSONL 文件，校验并幂等写入输出目录下的 bazaar_pvp_dataset.db。
    /// </summary>
    public RealImportResult Import(
        string inputPath,
        string outputDir,
        bool requireOfficialZh = true,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            throw new FileNotFoundException($"未找到外部社区数据文件: {inputPath}", inputPath);

        Directory.CreateDirectory(outputDir);
        string dbPath = Path.Combine(outputDir, "bazaar_pvp_dataset.db");

        var (parsedRuns, parsedBoards) = ParseInputFile(inputPath);

        // 1. 统一强制标注 source = "real_community"，补齐官方中文名与默认标识，并执行合法性校验
        var validRuns = new List<PvpRunRecord>();
        var validBoards = new List<PvpBoardSnapshot>();
        var errors = new List<string>();
        int totalBoardsRead = 0;
        int failedCount = 0;

        foreach (var run in parsedRuns)
        {
            run.Source = "real_community";
            if (string.IsNullOrWhiteSpace(run.Version)) run.Version = "5.0.0";
            if (string.IsNullOrWhiteSpace(run.RunId))
                run.RunId = BuildDeterministicId("real_run", $"{run.Hero}|{run.Persona}|{run.Archetype}|{run.FinalDay}|{run.DayBoards.Count}");

            bool anyValidInRun = false;
            foreach (var snap in run.DayBoards)
            {
                totalBoardsRead++;
                if (string.IsNullOrWhiteSpace(snap.RunId)) snap.RunId = run.RunId;
                if (ProcessAndValidateSnapshot(snap, requireOfficialZh, out var err))
                {
                    validBoards.Add(snap);
                    anyValidInRun = true;
                }
                else
                {
                    failedCount++;
                    if (errors.Count < 20)
                        errors.Add($"Board({snap.BoardId}): {err}");
                }
            }

            if (anyValidInRun)
                validRuns.Add(run);
        }

        // 处理独立的 PvpBoardSnapshot 列表
        var syntheticRunsById = new Dictionary<string, PvpRunRecord>(StringComparer.Ordinal);
        foreach (var snap in parsedBoards)
        {
            totalBoardsRead++;
            if (ProcessAndValidateSnapshot(snap, requireOfficialZh, out var err))
            {
                validBoards.Add(snap);
                if (!syntheticRunsById.TryGetValue(snap.RunId, out var synRun))
                {
                    synRun = new PvpRunRecord
                    {
                        RunId = snap.RunId,
                        Version = snap.Version,
                        Source = "real_community",
                        Hero = snap.Hero,
                        Persona = snap.Persona,
                        Archetype = snap.Archetype,
                        FinalDay = snap.Day,
                        FinalWins = snap.Wins,
                        FinalLosses = snap.Losses,
                        IsVictory = snap.Wins >= 10
                    };
                    syntheticRunsById[snap.RunId] = synRun;
                }
                else
                {
                    synRun.FinalDay = Math.Max(synRun.FinalDay, snap.Day);
                    synRun.FinalWins = Math.Max(synRun.FinalWins, snap.Wins);
                    synRun.FinalLosses = Math.Max(synRun.FinalLosses, snap.Losses);
                    if (snap.Wins >= 10) synRun.IsVictory = true;
                }
            }
            else
            {
                failedCount++;
                if (errors.Count < 20)
                    errors.Add($"Board({snap.BoardId}): {err}");
            }
        }
        validRuns.AddRange(syntheticRunsById.Values);

        // 2. 幂等写入 bazaar_pvp_dataset.db（已存在相同 board_id 则去重跳过，不重复写入子表）
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        DatasetExporter.EnsureDatabaseSchema(conn);

        var existingRunIds = LoadExistingIdSet(conn, "SELECT run_id FROM runs");
        var existingBoardIds = LoadExistingIdSet(conn, "SELECT board_id FROM day_boards");

        var newRuns = new List<PvpRunRecord>();
        foreach (var run in validRuns)
        {
            if (existingRunIds.Add(run.RunId))
                newRuns.Add(run);
        }

        var newBoards = new List<PvpBoardSnapshot>();
        int skippedDup = 0;
        foreach (var board in validBoards)
        {
            if (existingBoardIds.Add(board.BoardId))
                newBoards.Add(board);
            else
                skippedDup++;
        }

        using var tx = conn.BeginTransaction();
        if (newRuns.Count > 0)
            DatasetExporter.InsertRunsBatch(conn, tx, newRuns);
        if (newBoards.Count > 0)
            DatasetExporter.InsertBoardsAndChildrenBatch(conn, tx, newBoards, new HashSet<string>(), _validator);
        tx.Commit();

        string balancedJsonlPath = Path.Combine(outputDir, "ghost_pool_balanced.jsonl");
        int appendedToBalanced = 0;
        if (newBoards.Count > 0 && File.Exists(balancedJsonlPath))
        {
            appendedToBalanced = AppendNewBoardsToBalancedPool(balancedJsonlPath, Path.Combine(outputDir, "by_day"), newBoards);
        }

        var result = new RealImportResult
        {
            TotalBoardsRead = totalBoardsRead,
            ImportedRuns = newRuns.Count,
            ImportedBoards = newBoards.Count,
            SkippedDuplicateBoards = skippedDup,
            FailedValidationCount = failedCount,
            SampleErrors = errors
        };

        log?.Invoke($"[真实数据导入] 读取={result.TotalBoardsRead}，新增 Run={result.ImportedRuns}，新增盘面={result.ImportedBoards}（同步并入均衡池={appendedToBalanced}），幂等去重跳过={result.SkippedDuplicateBoards}，校验失败={result.FailedValidationCount}");
        return result;
    }

    private static int AppendNewBoardsToBalancedPool(
        string balancedJsonlPath,
        string byDayDir,
        IReadOnlyList<PvpBoardSnapshot> newBoards)
    {
        var existingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(balancedJsonlPath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("boardId", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrEmpty(id)) existingIds.Add(id);
                }
            }
            catch
            {
                // 忽略异常单行
            }
        }

        var toAppend = new List<PvpBoardSnapshot>();
        foreach (var board in newBoards)
        {
            if (existingIds.Add(board.BoardId))
                toAppend.Add(board);
        }
        if (toAppend.Count == 0) return 0;

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using (var fs = new FileStream(balancedJsonlPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(fs, utf8NoBom))
        {
            foreach (var board in toAppend)
                writer.WriteLine(JsonSerializer.Serialize(board, PvpDatasetJson.PvpJsonOptions));
        }

        if (Directory.Exists(byDayDir))
        {
            foreach (var group in toAppend.GroupBy(b => DayBucketHelper.GetDayBucket(b.Day)))
            {
                string dayFile = Path.Combine(byDayDir, DayBucketHelper.GetDayFileName(group.Key));
                var dayList = File.Exists(dayFile)
                    ? (JsonSerializer.Deserialize<List<PvpBoardSnapshot>>(File.ReadAllText(dayFile, Encoding.UTF8), PvpDatasetJson.PvpJsonOptions) ?? new List<PvpBoardSnapshot>())
                    : new List<PvpBoardSnapshot>();

                var dayIds = new HashSet<string>(dayList.Select(b => b.BoardId), StringComparer.Ordinal);
                bool changed = false;
                foreach (var b in group)
                {
                    if (dayIds.Add(b.BoardId))
                    {
                        dayList.Add(b);
                        changed = true;
                    }
                }
                if (changed)
                    File.WriteAllText(dayFile, JsonSerializer.Serialize(dayList, PvpDatasetJson.PvpJsonOptions), utf8NoBom);
            }
        }

        return toAppend.Count;
    }

    private bool ProcessAndValidateSnapshot(PvpBoardSnapshot snap, bool requireOfficialZh, out string error)
    {
        snap.Source = "real_community";
        if (string.IsNullOrWhiteSpace(snap.Version)) snap.Version = "5.0.0";
        if (snap.UnlockedSlotsMask <= 0) snap.UnlockedSlotsMask = 1023;

        // 补齐官方简体中文名与基础字段
        foreach (var item in snap.Items)
        {
            CardTemplate? tpl = !string.IsNullOrWhiteSpace(item.TemplateId) ? _repo.GetCard(item.TemplateId) : null;
            if (tpl == null && !string.IsNullOrWhiteSpace(item.InternalName))
                _itemsByInternalName.TryGetValue(item.InternalName, out tpl);

            if (tpl != null)
            {
                if (string.IsNullOrWhiteSpace(item.TemplateId)) item.TemplateId = tpl.Id;
                if (string.IsNullOrWhiteSpace(item.InternalName)) item.InternalName = tpl.InternalName;
                if (item.Width <= 0) item.Width = tpl.CardSize.SlotCount();
                if (_repo.HasOfficialZhTitle(tpl) || string.IsNullOrWhiteSpace(item.ZhName))
                    item.ZhName = _repo.GetLocalizedTitle(tpl);
            }
        }

        foreach (var skill in snap.Skills)
        {
            CardTemplate? tpl = !string.IsNullOrWhiteSpace(skill.TemplateId) ? _repo.GetCard(skill.TemplateId) : null;
            if (tpl == null && !string.IsNullOrWhiteSpace(skill.InternalName))
                _skillsByInternalName.TryGetValue(skill.InternalName, out tpl);

            if (tpl != null)
            {
                if (string.IsNullOrWhiteSpace(skill.TemplateId)) skill.TemplateId = tpl.Id;
                if (string.IsNullOrWhiteSpace(skill.InternalName)) skill.InternalName = tpl.InternalName;
                if (_repo.HasOfficialZhTitle(tpl) || string.IsNullOrWhiteSpace(skill.ZhName))
                    skill.ZhName = _repo.GetLocalizedTitle(tpl);
            }
        }

        snap.BoardSignature = _validator.ComputeSignature(snap);

        if (string.IsNullOrWhiteSpace(snap.BoardId))
            snap.BoardId = BuildDeterministicId("real", $"{snap.Day}|{snap.Hero}|{snap.HealthMax}|{snap.Wins}|{snap.BoardSignature}");

        if (string.IsNullOrWhiteSpace(snap.RunId))
            snap.RunId = BuildDeterministicId("real_run", $"{snap.Hero}|{snap.Archetype}|{snap.BoardId}");

        var vr = _validator.Validate(snap, _repo, requireOfficialZh);
        if (!vr.IsValid)
        {
            error = string.Join("; ", vr.Errors);
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static (List<PvpRunRecord> Runs, List<PvpBoardSnapshot> Boards) ParseInputFile(string inputPath)
    {
        var runs = new List<PvpRunRecord>();
        var boards = new List<PvpBoardSnapshot>();
        string raw = File.ReadAllText(inputPath, Encoding.UTF8).Trim();
        if (string.IsNullOrEmpty(raw))
            return (runs, boards);

        if (raw.StartsWith('['))
        {
            using var doc = JsonDocument.Parse(raw);
            foreach (var elem in doc.RootElement.EnumerateArray())
                ClassifyJsonElement(elem, runs, boards);
            return (runs, boards);
        }

        if (raw.StartsWith('{'))
        {
            // 先尝试整文件作为单个 JSON 对象（支持 { runs: [...] } / { boards: [...] } / 单条 Run / 单条 Snapshot），
            // 若解析为多行 JSONL 则逐行处理
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("runs", out var runsEl) && runsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in runsEl.EnumerateArray())
                        ClassifyJsonElement(elem, runs, boards);
                    return (runs, boards);
                }
                if (root.TryGetProperty("boards", out var boardsEl) && boardsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in boardsEl.EnumerateArray())
                        ClassifyJsonElement(elem, runs, boards);
                    return (runs, boards);
                }
                ClassifyJsonElement(root, runs, boards);
                return (runs, boards);
            }
            catch (JsonException)
            {
                // 回退至 JSONL 逐行解析
            }
        }

        foreach (var line in File.ReadLines(inputPath, Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            using var doc = JsonDocument.Parse(trimmed);
            ClassifyJsonElement(doc.RootElement, runs, boards);
        }
        return (runs, boards);
    }

    private static void ClassifyJsonElement(
        JsonElement elem,
        List<PvpRunRecord> runs,
        List<PvpBoardSnapshot> boards)
    {
        if (elem.ValueKind != JsonValueKind.Object) return;
        if (elem.TryGetProperty("dayBoards", out _) || elem.TryGetProperty("DayBoards", out _))
        {
            var run = elem.Deserialize<PvpRunRecord>(PvpDatasetJson.PvpJsonOptions);
            if (run != null) runs.Add(run);
        }
        else
        {
            var snap = elem.Deserialize<PvpBoardSnapshot>(PvpDatasetJson.PvpJsonOptions);
            if (snap != null) boards.Add(snap);
        }
    }

    private static HashSet<string> LoadExistingIdSet(SqliteConnection conn, string sql)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            set.Add(reader.GetString(0));
        return set;
    }

    private static string BuildDeterministicId(string prefix, string seedText)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seedText));
        var hex = Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
        return $"{prefix}_{hex}";
    }
}
