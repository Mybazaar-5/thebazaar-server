using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Models;
using Microsoft.Data.Sqlite;

namespace BazaarCore.Data;

/// <summary>
/// 从本地热更新 GameData.db 或 GameData.db.zip 只读加载全量卡牌、怪物、升级、模式、提示词典及官方中文词库数据。
/// </summary>
public sealed class GameDatabaseLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>
    /// 加载并返回只读全量数据仓库；若数据源为 .db 则直接只读打开，若为 .zip 则解压至系统临时缓存目录复用，绝不拷贝进项目目录。
    /// </summary>
    public static GameDataRepository Load(string? customDataPath = null, string? customTranslationPath = null)
    {
        var sourcePath = SteamPathResolver.ResolveGameDataSourcePath(customDataPath)
            ?? throw new FileNotFoundException(
                "未能在本地热更新缓存、dist 目录或 Steam 目录找到《The Bazaar》的 GameData.db / GameData.db.zip，请在设置中手动指定路径。");

        var dbPath = sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? ExtractToTempCacheIfNeeded(sourcePath)
            : sourcePath;

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        var cards = new Dictionary<string, CardTemplate>(StringComparer.OrdinalIgnoreCase);
        var monsters = new Dictionary<string, MonsterTemplate>(StringComparer.OrdinalIgnoreCase);
        var levelUps = new Dictionary<int, LevelUpTemplate>();
        var gameModes = new List<GameModeData>();
        var tooltips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unknownTypes = new HashSet<string>(StringComparer.Ordinal);
        var observedTypes = new HashSet<string>(StringComparer.Ordinal);

        void AuditJsonTypes(string rawJson)
        {
            using var doc = JsonDocument.Parse(rawJson);
            WalkJsonTypes(doc.RootElement, observedTypes, unknownTypes);
        }

        // 1. cards
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Data FROM cards";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var raw = reader.GetFieldValue<byte[]>(1);
                var json = System.Text.Encoding.UTF8.GetString(raw);
                AuditJsonTypes(json);
                var card = JsonSerializer.Deserialize<CardTemplate>(json, JsonOptions)
                    ?? throw new InvalidDataException($"反序列化卡牌失败: {id}");
                if (string.IsNullOrEmpty(card.Id)) card.Id = id;
                cards[card.Id] = card;
            }
        }

        // 2. monsters
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Data FROM monsters";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var raw = reader.GetFieldValue<byte[]>(1);
                var json = System.Text.Encoding.UTF8.GetString(raw);
                AuditJsonTypes(json);
                var monster = JsonSerializer.Deserialize<MonsterTemplate>(json, JsonOptions)
                    ?? throw new InvalidDataException($"反序列化怪物失败: {id}");
                if (string.IsNullOrEmpty(monster.Id)) monster.Id = id;
                monsters[monster.Id] = monster;
            }
        }

        // 3. level_ups
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Data FROM level_ups";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                var raw = reader.GetFieldValue<byte[]>(1);
                var json = System.Text.Encoding.UTF8.GetString(raw);
                AuditJsonTypes(json);
                var lu = JsonSerializer.Deserialize<LevelUpTemplate>(json, JsonOptions)
                    ?? throw new InvalidDataException($"反序列化等级奖励失败: {id}");
                if (lu.Level == 0) lu.Level = id;
                levelUps[lu.Level] = lu;
            }
        }

        // 4. game_modes
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Data FROM game_modes";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var raw = reader.GetFieldValue<byte[]>(1);
                var json = System.Text.Encoding.UTF8.GetString(raw);
                AuditJsonTypes(json);
                var gm = JsonSerializer.Deserialize<GameModeData>(json, JsonOptions);
                if (gm != null) gameModes.Add(gm);
            }
        }

        // 5. tooltips
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Data FROM tooltips LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var raw = reader.GetFieldValue<byte[]>(0);
                using var doc = JsonDocument.Parse(raw);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var text = prop.Value.TryGetProperty("Keyword", out var kwEl) ? kwEl.GetString()
                            : prop.Value.TryGetProperty("Description", out var descEl) ? descEl.GetString()
                            : prop.Value.TryGetProperty("Text", out var txtEl) ? txtEl.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(text))
                            tooltips[prop.Name] = text;
                    }
                }
            }
        }

        // 6. 兼容补齐 GameData.db 缺失的白名单卡（例如仅存于同目录 cards.json 或 dist/data/cards.json 的 13 张有效卡），绝不覆盖 db 已有模板
        SupplementMissingCardsFromJson(sourcePath, dbPath, cards, observedTypes, unknownTypes);

        var localization = LocalizationLoader.Load(customTranslationPath);
        var primaryMode = gameModes.FirstOrDefault() ?? new GameModeData();
        return new GameDataRepository(
            sourcePath,
            primaryMode,
            cards,
            monsters,
            levelUps,
            tooltips,
            observedTypes,
            unknownTypes,
            localization,
            sourcePath);
    }

    private static void SupplementMissingCardsFromJson(
        string sourcePath,
        string dbPath,
        Dictionary<string, CardTemplate> cards,
        HashSet<string> observedTypes,
        HashSet<string> unknownTypes)
    {
        var cardsJsonPath = ResolveCompanionFilePath(sourcePath, dbPath, "cards.json");
        if (string.IsNullOrEmpty(cardsJsonPath) || !File.Exists(cardsJsonPath))
            return;

        HashSet<string>? validIds = null;
        var validIdsPath = Path.Combine(Path.GetDirectoryName(cardsJsonPath)!, "valid_card_ids.json");
        if (File.Exists(validIdsPath))
        {
            try
            {
                using var validDoc = JsonDocument.Parse(File.ReadAllText(validIdsPath));
                if (validDoc.RootElement.TryGetProperty("valid_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    validIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var el in arr.EnumerateArray())
                    {
                        var id = el.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) validIds.Add(id);
                    }
                }
            }
            catch
            {
                validIds = null;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(cardsJsonPath));
            IEnumerable<JsonElement> EnumerateCardElements(JsonElement root)
            {
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in root.EnumerateArray()) yield return el;
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in prop.Value.EnumerateArray()) yield return el;
                        }
                    }
                }
            }

            foreach (var el in EnumerateCardElements(doc.RootElement))
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var id = el.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || cards.ContainsKey(id))
                    continue;
                if (validIds != null && !validIds.Contains(id))
                    continue;

                var card = el.Deserialize<CardTemplate>(JsonOptions);
                if (card == null) continue;
                if (string.IsNullOrEmpty(card.Id)) card.Id = id;
                if (cards.ContainsKey(card.Id)) continue;

                WalkJsonTypes(el, observedTypes, unknownTypes);
                cards[card.Id] = card;
            }
        }
        catch
        {
            // 若外部 cards.json 解析异常则静默跳过，保障 GameData.db 主库正常加载
        }
    }

    private static string? ResolveCompanionFilePath(string sourcePath, string dbPath, string fileName)
    {
        var candidateDirs = new List<string>();
        var srcDir = Path.GetDirectoryName(sourcePath);
        if (!string.IsNullOrWhiteSpace(srcDir)) candidateDirs.Add(srcDir);
        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dbDir) && !string.Equals(dbDir, srcDir, StringComparison.OrdinalIgnoreCase))
            candidateDirs.Add(dbDir);
        candidateDirs.Add(Path.Combine(Environment.CurrentDirectory, "dist", "data"));
        candidateDirs.Add(@"e:\thebazaar\dist\data");

        foreach (var dir in candidateDirs)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string ExtractToTempCacheIfNeeded(string zipPath)
    {
        var fi = new FileInfo(zipPath);
        var cacheKey = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
        var cacheDir = Path.Combine(Path.GetTempPath(), "BazaarCloneCache", cacheKey);
        Directory.CreateDirectory(cacheDir);
        var targetDbPath = Path.Combine(cacheDir, "GameData.db");

        if (File.Exists(targetDbPath) && new FileInfo(targetDbPath).Length > 0)
            return targetDbPath;

        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("GameData.db")
            ?? throw new InvalidDataException($"压缩包 {zipPath} 中不包含 GameData.db");

        var tmpPath = targetDbPath + ".tmp." + Guid.NewGuid().ToString("N");
        entry.ExtractToFile(tmpPath, overwrite: true);
        if (!File.Exists(targetDbPath))
            File.Move(tmpPath, targetDbPath, overwrite: true);
        else
            File.Delete(tmpPath);

        return targetDbPath;
    }

    private static void WalkJsonTypes(JsonElement element, HashSet<string> observed, HashSet<string> unknown)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("$type") && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var typeName = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(typeName))
                        {
                            observed.Add(typeName);
                            if (!RuleTypeRegistry.KnownTypes.Contains(typeName))
                                unknown.Add(typeName);
                        }
                    }
                    else
                    {
                        WalkJsonTypes(prop.Value, observed, unknown);
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    WalkJsonTypes(item, observed, unknown);
                break;
        }
    }
}
