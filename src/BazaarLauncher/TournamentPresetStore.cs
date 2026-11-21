using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using Newtonsoft.Json;

namespace BazaarLauncher;

/// <summary>
/// 锦标赛房主规则本地持久化与 3 槽位命名预设存储（BepInEx/config/bazaar_tournament_presets.json）。
/// </summary>
internal static class TournamentPresetStore
{
    internal const int SlotCount = 3;

    internal sealed class PresetSlot
    {
        [JsonProperty("slotIndex")] public int SlotIndex { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("updatedAtUtc")] public string UpdatedAtUtc { get; set; } = "";
        [JsonProperty("rules")] public CustomGameplayUI.CustomRunRuleSet Rules { get; set; }
    }

    internal sealed class StoreData
    {
        [JsonProperty("version")] public int Version { get; set; } = 1;
        [JsonProperty("lastTournamentRules")] public CustomGameplayUI.CustomRunRuleSet LastTournamentRules { get; set; }
        [JsonProperty("lastUpdatedAtUtc")] public string LastUpdatedAtUtc { get; set; } = "";
        [JsonProperty("presets")] public List<PresetSlot> Presets { get; set; } = new();
    }

    private static readonly object _sync = new();
    private static StoreData _cached;

    internal static string FilePath
    {
        get
        {
            string cfgDir = Paths.ConfigPath;
            if (string.IsNullOrWhiteSpace(cfgDir))
            {
                cfgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "config");
            }
            return Path.Combine(cfgDir, "bazaar_tournament_presets.json");
        }
    }

    internal static StoreData LoadSnapshot()
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            return CloneStore(_cached);
        }
    }

    internal static CustomGameplayUI.CustomRunRuleSet GetLastTournamentRules()
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            if (_cached.LastTournamentRules == null) return null;
            var copy = _cached.LastTournamentRules.Clone();
            NormalizeTournamentFlags(copy);
            return copy;
        }
    }

    internal static void SaveLastTournamentRules(CustomGameplayUI.CustomRunRuleSet rules)
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            if (rules == null)
            {
                _cached.LastTournamentRules = null;
            }
            else
            {
                var copy = rules.Clone();
                NormalizeTournamentFlags(copy);
                _cached.LastTournamentRules = copy;
            }
            _cached.LastUpdatedAtUtc = DateTime.UtcNow.ToString("O");
            SaveToDiskLocked();
        }
    }

    internal static StoreData SavePresetSlot(int slotIndex, string name, CustomGameplayUI.CustomRunRuleSet rules)
    {
        if (slotIndex < 0 || slotIndex >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slotIndex));
        lock (_sync)
        {
            EnsureLoadedLocked();
            var slot = _cached.Presets[slotIndex];
            string cleanName = string.IsNullOrWhiteSpace(name) ? $"预设方案 {slotIndex + 1}" : name.Trim();
            if (cleanName.Length > 24) cleanName = cleanName.Substring(0, 24);
            var copy = (rules ?? new CustomGameplayUI.CustomRunRuleSet()).Clone();
            NormalizeTournamentFlags(copy);
            slot.Name = cleanName;
            slot.Rules = copy;
            slot.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
            SaveToDiskLocked();
            return CloneStore(_cached);
        }
    }

    internal static StoreData RenamePresetSlot(int slotIndex, string name)
    {
        if (slotIndex < 0 || slotIndex >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slotIndex));
        lock (_sync)
        {
            EnsureLoadedLocked();
            var slot = _cached.Presets[slotIndex];
            string cleanName = string.IsNullOrWhiteSpace(name) ? $"预设方案 {slotIndex + 1}" : name.Trim();
            if (cleanName.Length > 24) cleanName = cleanName.Substring(0, 24);
            if (!string.Equals(slot.Name, cleanName, StringComparison.Ordinal))
            {
                slot.Name = cleanName;
                SaveToDiskLocked();
            }
            return CloneStore(_cached);
        }
    }

    internal static StoreData ClearPresetSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slotIndex));
        lock (_sync)
        {
            EnsureLoadedLocked();
            var slot = _cached.Presets[slotIndex];
            slot.Rules = null;
            slot.UpdatedAtUtc = "";
            SaveToDiskLocked();
            return CloneStore(_cached);
        }
    }

    internal static CustomGameplayUI.CustomRunRuleSet LoadPresetSlotRules(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= SlotCount) return null;
        lock (_sync)
        {
            EnsureLoadedLocked();
            var slot = _cached.Presets[slotIndex];
            if (slot?.Rules == null) return null;
            var copy = slot.Rules.Clone();
            NormalizeTournamentFlags(copy);
            return copy;
        }
    }

    internal static StoreData ResetAllToOfficialDefault()
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            var def = new CustomGameplayUI.CustomRunRuleSet();
            def.ResetToStandardValues(preserveArchetypeAndLobbyFlags: false);
            _cached.LastTournamentRules = def;
            _cached.LastUpdatedAtUtc = DateTime.UtcNow.ToString("O");
            SaveToDiskLocked();
            return CloneStore(_cached);
        }
    }

    /// <summary>V12.1 一次性迁移：升级旧版锦标赛磁盘记忆与预设槽位默认启用野怪多选一并落盘。</summary>
    internal static bool MigrateDefaultsToV12_1()
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            bool changed = false;

            if (_cached.LastTournamentRules != null && !_cached.LastTournamentRules.MonsterLootChooseOne)
            {
                _cached.LastTournamentRules.MonsterLootChooseOne = true;
                NormalizeTournamentFlags(_cached.LastTournamentRules);
                changed = true;
            }

            if (_cached.Presets != null)
            {
                foreach (var slot in _cached.Presets)
                {
                    if (slot?.Rules != null && !slot.Rules.MonsterLootChooseOne)
                    {
                        slot.Rules.MonsterLootChooseOne = true;
                        NormalizeTournamentFlags(slot.Rules);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                _cached.LastUpdatedAtUtc = DateTime.UtcNow.ToString("O");
                SaveToDiskLocked();
            }
            return changed;
        }
    }

    private static void EnsureLoadedLocked()
    {
        if (_cached != null) return;

        StoreData loaded = null;
        string path = FilePath;
        try
        {
            if (File.Exists(path))
            {
                string raw = File.ReadAllText(path, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    loaded = JsonConvert.DeserializeObject<StoreData>(raw);
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("TournamentPresetStore load failed, using fresh store: " + ex.Message);
        }

        _cached = loaded ?? new StoreData();
        NormalizeStore(_cached);
    }

    private static void NormalizeStore(StoreData data)
    {
        data.Version = 1;
        data.Presets ??= new List<PresetSlot>(SlotCount);
        var byIndex = new Dictionary<int, PresetSlot>();
        foreach (var s in data.Presets)
        {
            if (s != null && s.SlotIndex >= 0 && s.SlotIndex < SlotCount && !byIndex.ContainsKey(s.SlotIndex))
            {
                if (s.Rules != null) NormalizeTournamentFlags(s.Rules);
                if (string.IsNullOrWhiteSpace(s.Name)) s.Name = $"预设方案 {s.SlotIndex + 1}";
                byIndex[s.SlotIndex] = s;
            }
        }

        var normalized = new List<PresetSlot>(SlotCount);
        for (int i = 0; i < SlotCount; i++)
        {
            if (byIndex.TryGetValue(i, out var existing))
            {
                normalized.Add(existing);
            }
            else
            {
                normalized.Add(new PresetSlot
                {
                    SlotIndex = i,
                    Name = $"预设方案 {i + 1}",
                    UpdatedAtUtc = "",
                    Rules = null,
                });
            }
        }
        data.Presets = normalized;

        if (data.LastTournamentRules != null)
        {
            NormalizeTournamentFlags(data.LastTournamentRules);
        }
    }

    private static void NormalizeTournamentFlags(CustomGameplayUI.CustomRunRuleSet rules)
    {
        if (rules == null) return;
        rules.EnsureCollections();
        rules.PlayerCap = rules.PlayerCap == 10 ? 10 : 8;
        rules.RefreshPowerFlag();
    }

    private static void SaveToDiskLocked()
    {
        string path = FilePath;
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonConvert.SerializeObject(_cached, Formatting.Indented);
            string tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json, Encoding.UTF8);
            if (File.Exists(path))
            {
                File.Copy(tmpPath, path, overwrite: true);
                File.Delete(tmpPath);
            }
            else
            {
                File.Move(tmpPath, path);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("TournamentPresetStore save failed: " + ex.Message);
        }
    }

    private static StoreData CloneStore(StoreData src)
    {
        string json = JsonConvert.SerializeObject(src);
        var copy = JsonConvert.DeserializeObject<StoreData>(json) ?? new StoreData();
        NormalizeStore(copy);
        return copy;
    }
}
