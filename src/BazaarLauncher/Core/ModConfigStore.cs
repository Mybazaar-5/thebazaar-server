using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BazaarModSdk;
using BepInEx;
using Newtonsoft.Json;

namespace BazaarLauncher.Core;

/// <summary>
/// 统一持久化配置存储（BepInEx/config/BazaarModKit.Modules.json），
/// 采用 .tmp -> .bak -> 原子替换写入，支持按 Mod 命名空间隔离读写。
/// </summary>
internal sealed class ModConfigRootStore
{
    private sealed class RootPayload
    {
        [JsonProperty("version")] public int Version { get; set; } = 1;
        [JsonProperty("moduleEnabled")] public Dictionary<string, bool> ModuleEnabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonProperty("sections")] public Dictionary<string, Dictionary<string, string>> Sections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _sync = new();
    private RootPayload _data;

    internal string FilePath
    {
        get
        {
            string cfgDir = Paths.ConfigPath;
            if (string.IsNullOrWhiteSpace(cfgDir))
            {
                cfgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "config");
            }
            return Path.Combine(cfgDir, "BazaarModKit.Modules.json");
        }
    }

    internal ModConfigRootStore()
    {
        Reload();
    }

    internal bool GetModuleEnabled(string modId, bool defaultValue)
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            if (_data.ModuleEnabled.TryGetValue(modId, out bool enabled))
            {
                return enabled;
            }
            return defaultValue;
        }
    }

    internal void SetModuleEnabled(string modId, bool enabled)
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            _data.ModuleEnabled[modId] = enabled;
            SaveLocked();
        }
    }

    internal IModConfig CreateScopedConfig(string modId)
        => new ScopedModConfig(this, string.IsNullOrWhiteSpace(modId) ? "default" : modId);

    internal string GetRaw(string section, string key, string defaultValue)
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            if (_data.Sections.TryGetValue(section, out var map) && map != null && map.TryGetValue(key, out string val))
            {
                return val ?? defaultValue;
            }
            return defaultValue;
        }
    }

    internal void SetRaw(string section, string key, string value)
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            if (!_data.Sections.TryGetValue(section, out var map) || map == null)
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _data.Sections[section] = map;
            }
            map[key] = value ?? string.Empty;
        }
    }

    internal IReadOnlyCollection<string> GetKeys(string section)
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            if (_data.Sections.TryGetValue(section, out var map) && map != null)
            {
                return new List<string>(map.Keys);
            }
            return Array.Empty<string>();
        }
    }

    internal void Save()
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            SaveLocked();
        }
    }

    internal void Reload()
    {
        lock (_sync)
        {
            _data = TryLoadFromDisk(FilePath) ?? TryLoadFromDisk(FilePath + ".bak") ?? new RootPayload();
            _data.ModuleEnabled ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            _data.Sections ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void EnsureLoadedLocked()
    {
        if (_data == null) Reload();
    }

    private static RootPayload TryLoadFromDisk(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string raw = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return JsonConvert.DeserializeObject<RootPayload>(raw);
        }
        catch
        {
            return null;
        }
    }

    private void SaveLocked()
    {
        string path = FilePath;
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonConvert.SerializeObject(_data, Formatting.Indented);
            string tmpPath = path + ".tmp";
            string bakPath = path + ".bak";
            File.WriteAllText(tmpPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmpPath, path, bakPath, ignoreMetadataErrors: true);
                }
                catch
                {
                    try { File.Copy(path, bakPath, overwrite: true); } catch { }
                    File.Copy(tmpPath, path, overwrite: true);
                    try { File.Delete(tmpPath); } catch { }
                }
            }
            else
            {
                File.Move(tmpPath, path);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("ModConfigRootStore 落盘失败: " + ex.Message);
        }
    }

    private sealed class ScopedModConfig : IModConfig
    {
        private readonly ModConfigRootStore _root;
        private readonly string _section;

        public ScopedModConfig(ModConfigRootStore root, string section)
        {
            _root = root;
            _section = section;
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            string raw = _root.GetRaw(_section, key, null);
            return bool.TryParse(raw, out bool v) ? v : defaultValue;
        }

        public void SetBool(string key, bool value) => _root.SetRaw(_section, key, value ? "true" : "false");

        public int GetInt(string key, int defaultValue = 0)
        {
            string raw = _root.GetRaw(_section, key, null);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : defaultValue;
        }

        public void SetInt(string key, int value) => _root.SetRaw(_section, key, value.ToString(CultureInfo.InvariantCulture));

        public float GetFloat(string key, float defaultValue = 0f)
        {
            string raw = _root.GetRaw(_section, key, null);
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : defaultValue;
        }

        public void SetFloat(string key, float value) => _root.SetRaw(_section, key, value.ToString(CultureInfo.InvariantCulture));

        public string GetString(string key, string defaultValue = "") => _root.GetRaw(_section, key, defaultValue ?? string.Empty);

        public void SetString(string key, string value) => _root.SetRaw(_section, key, value ?? string.Empty);

        public IReadOnlyCollection<string> GetAllKeys() => _root.GetKeys(_section);

        public void Save() => _root.Save();

        public void Reload() => _root.Reload();
    }
}
