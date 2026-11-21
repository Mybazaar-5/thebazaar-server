using System.Text.Json;
using BazaarCore.Data;
using Microsoft.Data.Sqlite;

namespace BazaarServer.Data;

/// <summary>
/// 游戏模式全局常数（GameData.db 的 game_modes 表）。
///
/// 实测结构（关键字段）：
///   {
///     "$type": "TGameMode",
///     "Id": "818c1c4b-4ddf-4d06-b369-e2a56c1cfb2d",
///     "InternalName": "Base GameMode",
///     "VictoriesToWin": 10,
///     "NumberOfDays": 10,
///     "HoursInADay": 6,
///     "NumCarpetSocketsUnlockPerLevel": 2,
///     "ExperiencePerLevel": 8,
///     "ExperiencePerHour": 1,
///     "MaxLevel": 999,
///     "Prestige": { "PrestigeInitial": 20, "PrestigeMax": 20, "PrestigeLossFromMonsters": 0 },
///     "Fates": { "TemplateId": "...", "SpawnAtOrBelowPrestigeValue": 0, "AfterFatesPrestigeValue": 1 },
///     "CombatSimEffectDelay": 250,
///     "CritPercentMultiplier": 200,
///     "BurnDecrementByAsPercentage": 3,
///     "Sandstorm": { "BaseTickRate": 250, "TickRateDecrement": 10, "TickRateFloor": 150,
///                    "BaseDamage": 1, "AdditiveScalar": 2,
///                    "CountdownStartTime": 20000, "CountdownDuration": 10000 },
///     "StandardPrices": { ... }
///   }
/// </summary>
public sealed class GameModeConfig
{
    public Guid Id { get; init; }
    public string InternalName { get; init; } = "Base GameMode";

    /// <summary>达到多少胜即通关。</summary>
    public int VictoriesToWin { get; init; } = 10;
    /// <summary>最多多少天。</summary>
    public int NumberOfDays { get; init; } = 10;
    /// <summary>每天多少小时。</summary>
    public int HoursInADay { get; init; } = 6;
    /// <summary>每级解锁几个卡槽。</summary>
    public int NumCarpetSocketsUnlockPerLevel { get; init; } = 2;
    public int ExperiencePerLevel { get; init; } = 8;
    public int ExperiencePerHour { get; init; } = 1;
    public int MaxLevel { get; init; } = 999;

    public int PrestigeInitial { get; init; } = 20;
    public int PrestigeMax { get; init; } = 20;
    public int PrestigeLossFromMonsters { get; init; }

    /// <summary>开局初始资源默认值（可由配置或测试覆写；官方默认起始经验为 0）。</summary>
    public int StartingGold { get; init; } = 15;
    public int StartingIncome { get; init; } = 5;
    public int StartingExperience { get; init; } = 0;
    public int StartingHealth { get; init; } = 250;

    public string FatesTemplateId { get; init; } = "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
    public int FatesSpawnAtOrBelowPrestige { get; init; }
    public int FatesAfterPrestige { get; init; } = 1;

    public int CombatSimEffectDelay { get; init; } = 250;
    public int CritPercentMultiplier { get; init; } = 200;
    public int BurnDecrementByAsPercentage { get; init; } = 3;

    public int SandstormBaseTickRate { get; init; } = 250;
    public int SandstormTickRateDecrement { get; init; } = 10;
    public int SandstormTickRateFloor { get; init; } = 150;
    public int SandstormBaseDamage { get; init; } = 1;
    public int SandstormAdditiveScalar { get; init; } = 2;
    public int SandstormCountdownStartTime { get; init; } = 20000;
    public int SandstormCountdownDuration { get; init; } = 10000;

    /// <summary>
    /// 直接从已单次加载的 <see cref="GameDataRepository"/> 构建只读模式配置，彻底消除启动期重复打开 GameData.db。
    /// </summary>
    public static GameModeConfig FromRepository(GameDataRepository repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var gm = repo.GameMode;
        var cfg = new GameModeConfig
        {
            Id = Guid.TryParse(gm.Id, out var g) ? g : Guid.Empty,
            InternalName = string.IsNullOrWhiteSpace(gm.InternalName) ? "Base GameMode" : gm.InternalName,

            VictoriesToWin = gm.VictoriesToWin > 0 ? gm.VictoriesToWin : 10,
            NumberOfDays = gm.NumberOfDays > 0 ? gm.NumberOfDays : 10,
            HoursInADay = gm.HoursInADay > 0 ? gm.HoursInADay : 6,
            NumCarpetSocketsUnlockPerLevel = gm.NumCarpetSocketsUnlockPerLevel > 0 ? gm.NumCarpetSocketsUnlockPerLevel : 2,
            ExperiencePerLevel = gm.ExperiencePerLevel > 0 ? gm.ExperiencePerLevel : 8,
            ExperiencePerHour = gm.ExperiencePerHour >= 0 ? gm.ExperiencePerHour : 1,
            MaxLevel = gm.MaxLevel > 0 ? gm.MaxLevel : 999,

            PrestigeInitial = gm.Prestige?.PrestigeInitial > 0 ? gm.Prestige.PrestigeInitial : 20,
            PrestigeMax = gm.Prestige?.PrestigeMax > 0 ? gm.Prestige.PrestigeMax : 20,
            PrestigeLossFromMonsters = gm.Prestige?.PrestigeLossFromMonsters ?? 0,

            StartingGold = 15,
            StartingIncome = 5,
            StartingExperience = Math.Max(0, gm.StartingExperience),
            StartingHealth = 250,

            FatesTemplateId = !string.IsNullOrWhiteSpace(gm.Fates?.TemplateId)
                ? gm.Fates.TemplateId!
                : "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10",
            FatesSpawnAtOrBelowPrestige = gm.Fates?.SpawnAtOrBelowPrestigeValue ?? 0,
            FatesAfterPrestige = gm.Fates?.AfterFatesPrestigeValue > 0 ? gm.Fates.AfterFatesPrestigeValue : 1,

            CombatSimEffectDelay = gm.CombatSimEffectDelay > 0 ? gm.CombatSimEffectDelay : 250,
            CritPercentMultiplier = gm.CritPercentMultiplier > 0 ? gm.CritPercentMultiplier : 200,
            BurnDecrementByAsPercentage = gm.BurnDecrementByAsPercentage > 0 ? gm.BurnDecrementByAsPercentage : 3,

            SandstormBaseTickRate = gm.Sandstorm?.BaseTickRate > 0 ? gm.Sandstorm.BaseTickRate : 250,
            SandstormTickRateDecrement = gm.Sandstorm?.TickRateDecrement ?? 10,
            SandstormTickRateFloor = gm.Sandstorm?.TickRateFloor > 0 ? gm.Sandstorm.TickRateFloor : 150,
            SandstormBaseDamage = gm.Sandstorm?.BaseDamage > 0 ? gm.Sandstorm.BaseDamage : 1,
            SandstormAdditiveScalar = gm.Sandstorm?.AdditiveScalar ?? 2,
            SandstormCountdownStartTime = gm.Sandstorm?.CountdownStartTime > 0 ? gm.Sandstorm.CountdownStartTime : 20000,
            SandstormCountdownDuration = gm.Sandstorm?.CountdownDuration > 0 ? gm.Sandstorm.CountdownDuration : 10000,
        };
        Console.WriteLine(
            $"[GameMode] (shared repo) {cfg.InternalName}: win={cfg.VictoriesToWin} days={cfg.NumberOfDays} " +
            $"hours/day={cfg.HoursInADay} prestige={cfg.PrestigeInitial} " +
            $"start(gold={cfg.StartingGold}, inc={cfg.StartingIncome}, exp={cfg.StartingExperience}, hp={cfg.StartingHealth})");
        return cfg;
    }

    public static GameModeConfig Load(string dbPath)
    {
        if (!File.Exists(dbPath)) return new GameModeConfig();

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Data FROM game_modes LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new GameModeConfig();

            string id = r.GetString(0);
            string json = System.Text.Encoding.UTF8.GetString((byte[])r.GetValue(1));
            var cfg = Parse(id, json);
            Console.WriteLine(
                $"[GameMode] {cfg.InternalName}: win={cfg.VictoriesToWin} days={cfg.NumberOfDays} " +
                $"hours/day={cfg.HoursInADay} prestige={cfg.PrestigeInitial} " +
                $"start(gold={cfg.StartingGold}, inc={cfg.StartingIncome}, exp={cfg.StartingExperience}, hp={cfg.StartingHealth})");
            return cfg;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[GameMode] load failed: " + ex.Message);
            return new GameModeConfig();
        }
    }

    private static GameModeConfig Parse(string id, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var prestige = r.TryGetProperty("Prestige", out var p) ? p : default;
        var fates = r.TryGetProperty("Fates", out var f) ? f : default;
        var sand = r.TryGetProperty("Sandstorm", out var s) ? s : default;

        return new GameModeConfig
        {
            Id = Guid.TryParse(id, out var g) ? g : Guid.Empty,
            InternalName = Str(r, "InternalName", "Base GameMode"),

            VictoriesToWin = Int(r, "VictoriesToWin", 10),
            NumberOfDays = Int(r, "NumberOfDays", 10),
            HoursInADay = Int(r, "HoursInADay", 6),
            NumCarpetSocketsUnlockPerLevel = Int(r, "NumCarpetSocketsUnlockPerLevel", 2),
            ExperiencePerLevel = Int(r, "ExperiencePerLevel", 8),
            ExperiencePerHour = Int(r, "ExperiencePerHour", 1),
            MaxLevel = Int(r, "MaxLevel", 999),

            PrestigeInitial = Int(prestige, "PrestigeInitial", 20),
            PrestigeMax = Int(prestige, "PrestigeMax", 20),
            PrestigeLossFromMonsters = Int(prestige, "PrestigeLossFromMonsters", 0),

            StartingGold = Int(r, "StartingGold", 15),
            StartingIncome = Int(r, "StartingIncome", 5),
            StartingExperience = Int(r, "StartingExperience", 0),
            StartingHealth = Int(r, "StartingHealth", 250),

            FatesTemplateId = Str(fates, "TemplateId", "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10"),
            FatesSpawnAtOrBelowPrestige = Int(fates, "SpawnAtOrBelowPrestigeValue", 0),
            FatesAfterPrestige = Int(fates, "AfterFatesPrestigeValue", 1),

            CombatSimEffectDelay = Int(r, "CombatSimEffectDelay", 250),
            CritPercentMultiplier = Int(r, "CritPercentMultiplier", 200),
            BurnDecrementByAsPercentage = Int(r, "BurnDecrementByAsPercentage", 3),

            SandstormBaseTickRate = Int(sand, "BaseTickRate", 250),
            SandstormTickRateDecrement = Int(sand, "TickRateDecrement", 10),
            SandstormTickRateFloor = Int(sand, "TickRateFloor", 150),
            SandstormBaseDamage = Int(sand, "BaseDamage", 1),
            SandstormAdditiveScalar = Int(sand, "AdditiveScalar", 2),
            SandstormCountdownStartTime = Int(sand, "CountdownStartTime", 20000),
            SandstormCountdownDuration = Int(sand, "CountdownDuration", 10000),
        };
    }

    private static string Str(JsonElement el, string name, string fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static int Int(JsonElement el, string name, int fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
}
