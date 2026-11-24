using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Microsoft.Data.Sqlite;

namespace BazaarServer.Data;

/// <summary>
/// 游戏模式全局常数（GameData.db 的 game_modes 表）。
/// 单一信源为 <see cref="Source"/>（<see cref="GameModeData"/>），原 10 项战斗/沙暴冗余属性收敛为只读代理，
/// 彻底消除双份拷贝漂移并保持全部外部调用方只读向后兼容。
/// </summary>
public sealed class GameModeConfig
{
    /// <summary>底层权威游戏模式数据对象（来自 GameDataRepository.GameMode 或 game_modes 表）。</summary>
    public GameModeData Source { get; init; } = new();

    public Guid Id { get; init; }
    public string InternalName { get; init; } = "Base GameMode";

    /// <summary>达到多少胜即通关。</summary>
    public int VictoriesToWin { get; init; } = 10;
    /// <summary>最多多少天。</summary>
    public int NumberOfDays { get; init; } = 10;
    /// <summary>每天多少小时。</summary>
    public int HoursInADay { get; init; } = 6;
    /// <summary>每级解锁几个卡槽（无损透传自 GameModeData，兼容 Mod 设为 1）。</summary>
    public int NumCarpetSocketsUnlockPerLevel { get; init; } = 2;
    public int ExperiencePerLevel { get; init; } = 8;
    public int ExperiencePerHour { get; init; } = 1;
    public int MaxLevel { get; init; } = 999;

    public int PrestigeInitial { get; init; } = GameDefaults.StartingPrestige;
    public int PrestigeMax { get; init; } = GameDefaults.StartingPrestige;
    /// <summary>PvE 野怪战败声望扣减量（无损透传自 GameModeData.Prestige.PrestigeLossFromMonsters）。</summary>
    public int PrestigeLossFromMonsters { get; init; }

    /// <summary>开局初始资源默认值（可由配置或测试覆写；单一信源为 GameDefaults：300 HP / 15 Gold / 5 Income / 3 XP）。</summary>
    public int StartingGold { get; init; } = GameDefaults.StartingGold;
    public int StartingIncome { get; init; } = GameDefaults.StartingIncome;
    public int StartingExperience { get; init; } = GameDefaults.StartingExperience;
    public int StartingHealth { get; init; } = GameDefaults.StartingHealth;

    public string FatesTemplateId { get; init; } = "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
    public int FatesSpawnAtOrBelowPrestige { get; init; }
    public int FatesAfterPrestige { get; init; } = 1;

    // ── 单一信源代理（P2-04：原 10 项重复字段收敛为直接透传 Source，避免与 BazaarCore 漂移）──

    public int CombatSimEffectDelay => Source.CombatSimEffectDelay > 0 ? Source.CombatSimEffectDelay : 250;
    public int CritPercentMultiplier => Source.CritPercentMultiplier > 0 ? Source.CritPercentMultiplier : 200;
    public int BurnDecrementByAsPercentage => Source.BurnDecrementByAsPercentage > 0 ? Source.BurnDecrementByAsPercentage : 3;

    public int SandstormBaseTickRate => Source.Sandstorm?.BaseTickRate > 0 ? Source.Sandstorm.BaseTickRate : 250;
    public int SandstormTickRateDecrement => Source.Sandstorm?.TickRateDecrement ?? 10;
    public int SandstormTickRateFloor => Source.Sandstorm?.TickRateFloor > 0 ? Source.Sandstorm.TickRateFloor : 150;
    public int SandstormBaseDamage => Source.Sandstorm?.BaseDamage > 0 ? Source.Sandstorm.BaseDamage : 1;
    public int SandstormAdditiveScalar => Source.Sandstorm?.AdditiveScalar ?? 2;
    public int SandstormCountdownStartTime => Source.Sandstorm?.CountdownStartTime > 0 ? Source.Sandstorm.CountdownStartTime : 20000;
    public int SandstormCountdownDuration => Source.Sandstorm?.CountdownDuration > 0 ? Source.Sandstorm.CountdownDuration : 10000;

    /// <summary>
    /// 直接从已单次加载的 <see cref="GameDataRepository"/> 构建只读模式配置，彻底消除启动期重复打开 GameData.db。
    /// </summary>
    public static GameModeConfig FromRepository(GameDataRepository repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var cfg = FromGameModeData(repo.GameMode);
        Console.WriteLine(
            $"[GameMode] (shared repo) {cfg.InternalName}: win={cfg.VictoriesToWin} days={cfg.NumberOfDays} " +
            $"hours/day={cfg.HoursInADay} prestige={cfg.PrestigeInitial} pveLoss={cfg.PrestigeLossFromMonsters} " +
            $"socketsPerLv={cfg.NumCarpetSocketsUnlockPerLevel} " +
            $"start(gold={cfg.StartingGold}, inc={cfg.StartingIncome}, exp={cfg.StartingExperience}, hp={cfg.StartingHealth})");
        return cfg;
    }

    /// <summary>
    /// 直接从权威 <see cref="GameModeData"/> 构造只读模式配置（单一信源构造入口，便于 Mod 覆盖与回归测试）。
    /// </summary>
    public static GameModeConfig FromGameModeData(GameModeData? gm, string? fallbackId = null)
    {
        var source = gm ?? new GameModeData();
        string rawId = !string.IsNullOrWhiteSpace(source.Id) ? source.Id : (fallbackId ?? string.Empty);

        return new GameModeConfig
        {
            Source = source,
            Id = Guid.TryParse(rawId, out var g) ? g : Guid.Empty,
            InternalName = string.IsNullOrWhiteSpace(source.InternalName) ? "Base GameMode" : source.InternalName,

            VictoriesToWin = source.VictoriesToWin > 0 ? source.VictoriesToWin : 10,
            NumberOfDays = source.NumberOfDays > 0 ? source.NumberOfDays : 10,
            HoursInADay = source.HoursInADay > 0 ? source.HoursInADay : 6,
            NumCarpetSocketsUnlockPerLevel = source.NumCarpetSocketsUnlockPerLevel > 0 ? source.NumCarpetSocketsUnlockPerLevel : 2,
            ExperiencePerLevel = source.ExperiencePerLevel > 0 ? source.ExperiencePerLevel : 8,
            ExperiencePerHour = source.ExperiencePerHour >= 0 ? source.ExperiencePerHour : 1,
            MaxLevel = source.MaxLevel > 0 ? source.MaxLevel : 999,

            PrestigeInitial = source.Prestige?.PrestigeInitial > 0 ? source.Prestige.PrestigeInitial : GameDefaults.StartingPrestige,
            PrestigeMax = source.Prestige?.PrestigeMax > 0 ? source.Prestige.PrestigeMax : GameDefaults.StartingPrestige,
            PrestigeLossFromMonsters = Math.Max(0, source.Prestige?.PrestigeLossFromMonsters ?? 0),

            StartingGold = GameDefaults.StartingGold,
            StartingIncome = GameDefaults.StartingIncome,
            StartingExperience = source.StartingExperience > 0 ? source.StartingExperience : GameDefaults.StartingExperience,
            StartingHealth = GameDefaults.StartingHealth,

            FatesTemplateId = !string.IsNullOrWhiteSpace(source.Fates?.TemplateId)
                ? source.Fates.TemplateId!
                : "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10",
            FatesSpawnAtOrBelowPrestige = source.Fates?.SpawnAtOrBelowPrestigeValue ?? 0,
            FatesAfterPrestige = source.Fates?.AfterFatesPrestigeValue > 0 ? source.Fates.AfterFatesPrestigeValue : 1,
        };
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
            var gm = JsonSerializer.Deserialize<GameModeData>(json, GameDatabaseLoader.JsonOptions) ?? new GameModeData();
            if (string.IsNullOrWhiteSpace(gm.Id)) gm.Id = id;
            var cfg = FromGameModeData(gm, id);
            Console.WriteLine(
                $"[GameMode] {cfg.InternalName}: win={cfg.VictoriesToWin} days={cfg.NumberOfDays} " +
                $"hours/day={cfg.HoursInADay} prestige={cfg.PrestigeInitial} pveLoss={cfg.PrestigeLossFromMonsters} " +
                $"socketsPerLv={cfg.NumCarpetSocketsUnlockPerLevel} " +
                $"start(gold={cfg.StartingGold}, inc={cfg.StartingIncome}, exp={cfg.StartingExperience}, hp={cfg.StartingHealth})");
            return cfg;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[GameMode] load failed: " + ex.Message);
            return new GameModeConfig();
        }
    }
}
