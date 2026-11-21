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
                PrecompileCardTemplateRules(card);
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
                PrecompileRuleNode(lu.Rewards);
                PrecompileRuleNode(lu.TutorialRewards);
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

        // 6. 单一权威信源：严格以 GameData.db 为准，不再回读过期 21.5 MB cards.json（消除 60MB 临时堆与旧卡污染）
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

    /// <summary>
    /// 加载期一次性预编译卡牌模板下所有 RuleNode 的延迟反序列化字段（Value/Conditions/Constraints/ComparisonValue/Enchantments），
    /// 彻底消除战斗和抽卡热路径上的 JsonElement.Deserialize 与 GC 分配（Top #13）。
    /// </summary>
    internal static void PrecompileCardTemplateRules(CardTemplate card)
    {
        if (card == null) return;
        foreach (var ab in card.Abilities.Values) PrecompileAbility(ab);
        foreach (var au in card.Auras.Values) PrecompileAura(au);
        if (card.Enchantments != null)
        {
            foreach (var ench in card.Enchantments.Values)
            {
                foreach (var ab in ench.Abilities.Values) PrecompileAbility(ab);
                foreach (var au in ench.Auras.Values) PrecompileAura(au);
            }
        }
        if (card.Quests != null)
        {
            for (int i = 0; i < card.Quests.Count; i++)
            {
                var q = card.Quests[i];
                PrecompileRuleList(q.Prerequisites);
                PrecompileRuleList(q.Repeatable?.Prerequisites);
                for (int j = 0; j < q.Entries.Count; j++)
                {
                    var entry = q.Entries[j];
                    PrecompileRuleList(entry.Prerequisites);
                    PrecompileRuleNode(entry.Trigger);
                    PrecompileRuleNode(entry.Update?.Value);
                    PrecompileRuleNode(entry.Update?.Duration);
                    if (entry.Reward?.Abilities != null)
                        foreach (var ab in entry.Reward.Abilities.Values) PrecompileAbility(ab);
                    if (entry.Reward?.Auras != null)
                        foreach (var au in entry.Reward.Auras.Values) PrecompileAura(au);
                    if (entry.CompletionEffects != null)
                        foreach (var eff in entry.CompletionEffects.Values) PrecompileAbility(eff);
                }
            }
        }
        if (card.Transform != null)
        {
            PrecompileRuleNode(card.Transform.SpawnContext);
            if (card.Transform.Abilities != null)
                foreach (var ab in card.Transform.Abilities.Values) PrecompileAbility(ab);
            if (card.Transform.Auras != null)
                foreach (var au in card.Transform.Auras.Values) PrecompileAura(au);
        }
        PrecompileRuleNode(card.SelectionContext?.SpawnContext);
        PrecompileRuleNode(card.SelectionRequirements);
        PrecompileRuleNode(card.SelectionCriteria);
        PrecompileRuleNode(card.CombatantType);
        PrecompileRuleNode(card.RewardVictory?.SpawnContext);
        PrecompileRuleNode(card.RewardDefeat?.SpawnContext);
        PrecompileRuleNode(card.Behavior);
        if (card.NextEncounterOnSelection != null)
        {
            for (int i = 0; i < card.NextEncounterOnSelection.Count; i++)
                PrecompileRuleList(card.NextEncounterOnSelection[i].Prerequisites);
        }
    }

    private static void PrecompileAbility(AbilityDefinition? ab)
    {
        if (ab == null) return;
        PrecompileRuleNode(ab.Trigger);
        PrecompileRuleNode(ab.Action);
        PrecompileRuleList(ab.Prerequisites);
    }

    private static void PrecompileAura(AuraDefinition? au)
    {
        if (au == null) return;
        PrecompileRuleNode(au.Action);
        PrecompileRuleList(au.Prerequisites);
    }

    private static void PrecompileRuleList(List<RuleNode>? list)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
            PrecompileRuleNode(list[i]);
    }

    internal static void PrecompileRuleNode(RuleNode? node, int depth = 0)
    {
        if (node == null || depth > 24) return;

        // 触发并缓存全部延迟解析字段
        PrecompileRuleNode(node.GetValueNode(JsonOptions), depth + 1);
        PrecompileRuleNode(node.GetComparisonValueNode(JsonOptions), depth + 1);
        PrecompileRuleNode(node.GetSingleCondition(JsonOptions), depth + 1);
        var condList = node.GetConditionList(JsonOptions);
        for (int i = 0; i < condList.Count; i++)
            PrecompileRuleNode(condList[i], depth + 1);
        PrecompileRuleNode(node.GetSingleConstraint(JsonOptions), depth + 1);
        var consList = node.GetConstraintList(JsonOptions);
        for (int i = 0; i < consList.Count; i++)
            PrecompileRuleNode(consList[i], depth + 1);
        _ = node.GetWeightedEnchantments();

        PrecompileRuleNode(node.ReferenceValue, depth + 1);
        PrecompileRuleNode(node.Target, depth + 1);
        PrecompileRuleNode(node.TargetPlayer, depth + 1);
        PrecompileRuleNode(node.TargetCount, depth + 1);
        PrecompileRuleNode(node.Source, depth + 1);
        PrecompileRuleNode(node.Subject, depth + 1);
        PrecompileRuleNode(node.SubjectOther, depth + 1);
        PrecompileRuleNode(node.Duration, depth + 1);
        PrecompileRuleNode(node.Cost, depth + 1);
        PrecompileRuleNode(node.Limit, depth + 1);
        PrecompileRuleNode(node.SpawnContext, depth + 1);
        PrecompileRuleNode(node.SelectionContextRules, depth + 1);
        PrecompileRuleNode(node.Modifier?.Value, depth + 1);

        PrecompileRuleList(node.Actions);
        PrecompileRuleList(node.Triggers);
        PrecompileRuleList(node.Groups);
        PrecompileRuleList(node.Filters);
        PrecompileRuleList(node.Behaviors);
        PrecompileRuleList(node.Prerequisites);

        if (node.Abilities != null)
            foreach (var ab in node.Abilities.Values) PrecompileAbility(ab);
        if (node.Attributes != null)
            foreach (var attrNode in node.Attributes.Values) PrecompileRuleNode(attrNode, depth + 1);
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
