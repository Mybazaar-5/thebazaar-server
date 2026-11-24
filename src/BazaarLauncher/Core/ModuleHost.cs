using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BazaarModSdk;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarLauncher.Core;

/// <summary>
/// 内置模块可选实现的补丁统计与热管理扩展接口。
/// </summary>
internal interface IBuiltInPatchModule : IBazaarClientMod
{
    /// <summary>返回该模块持有的安全补丁器（用于统计 Attempted/Succeeded/Degraded 与卸载）。</summary>
    SafeHarmonyPatcher Patcher { get; }
}

/// <summary>
/// 崩溃隔离模块化宿主（ModSandbox + 异常熔断 + 耗时统计 + 游戏内动态启停 + 外部 BazaarMods/*.dll 热加载）。
/// 彻底修复 P0-6（线性初始化无崩溃隔离、独立 Harmony 实例泄漏、静态标志阻断重载）。
/// </summary>
internal sealed class ModuleHost
{
    private const int CircuitBreakerThreshold = 5;
    private static readonly TimeSpan CircuitBreakerWindow = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private readonly List<ModContainer> _containers = new();
    private readonly Dictionary<string, ModContainer> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedAssemblySha256 = new(StringComparer.OrdinalIgnoreCase);

    internal ModEventBus EventBus { get; } = new();
    internal ModConfigRootStore ConfigStore { get; } = new();

    internal INativeUiHost UiHost { get; set; }
    internal ICardVisualOverrideRegistry VisualRegistry { get; set; }
    internal ILocalizationOverrideRegistry LocalizationRegistry { get; set; }
    internal IPerformanceTelemetry Telemetry { get; set; }

    internal static string CacheDirectory
    {
        get
        {
            string cfgDir = Paths.ConfigPath;
            if (string.IsNullOrWhiteSpace(cfgDir))
            {
                cfgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "config");
            }
            string path = Path.Combine(cfgDir, "BazaarModCache");
            try { Directory.CreateDirectory(path); } catch { }
            return path;
        }
    }

    internal static string ExternalDllModsDirectory
    {
        get
        {
            string pluginDir = Paths.PluginPath;
            if (string.IsNullOrWhiteSpace(pluginDir))
            {
                pluginDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "plugins");
            }
            return Path.Combine(pluginDir, "BazaarMods");
        }
    }

    internal static string ExternalContentModsDirectory
    {
        get
        {
            string bepRoot = Paths.BepInExRootPath;
            if (string.IsNullOrWhiteSpace(bepRoot))
            {
                bepRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx");
            }
            return Path.Combine(bepRoot, "mods");
        }
    }

    /// <summary>注册并沙箱化加载单个内置或外部 Mod。</summary>
    internal bool RegisterAndLoad(IBazaarClientMod mod, bool? enabledOverride = null)
    {
        if (mod == null) return false;
        ModManifest manifest = NormalizeManifest(mod);
        if (string.IsNullOrWhiteSpace(manifest.Id)) return false;

        ModContainer container;
        lock (_sync)
        {
            if (_byId.ContainsKey(manifest.Id))
            {
                BazaarLauncherPlugin.Log?.LogWarning($"[ModuleHost] 跳过重复注册模块: {manifest.Id}");
                return false;
            }

            bool configuredEnabled = enabledOverride ?? ConfigStore.GetModuleEnabled(manifest.Id, manifest.DefaultEnabled);
            container = new ModContainer(this, mod, manifest, configuredEnabled);
            _containers.Add(container);
            _byId[manifest.Id] = container;
        }

        container.LoadAndActivateIfEnabled();
        return true;
    }

    /// <summary>
    /// 扫描 BepInEx/plugins/BazaarMods/*.dll 与 BepInEx/mods/<mod-id>/（数据包与 DLL）热加载第三方扩展。
    /// 采用影子字节加载（Assembly.Load(byte[])）避免锁定磁盘文件，支持游戏内热重载。
    /// </summary>
    internal int DiscoverAndLoadExternalMods()
    {
        int loadedCount = 0;

        // 1. 扫描 BepInEx/plugins/BazaarMods/*.dll
        try
        {
            string dllRoot = ExternalDllModsDirectory;
            Directory.CreateDirectory(dllRoot);
            foreach (string dllPath in Directory.GetFiles(dllRoot, "*.dll", SearchOption.AllDirectories))
            {
                loadedCount += TryLoadExternalAssemblyDll(dllPath, Path.GetDirectoryName(dllPath) ?? dllRoot);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[ModuleHost] 扫描 BazaarMods DLL 目录异常: {ex.Message}");
        }

        // 2. 扫描 BepInEx/mods/<mod-id>/（支持零代码数据包 JSON+PNG 及可选 C# DLL）
        try
        {
            string modsRoot = ExternalContentModsDirectory;
            if (Directory.Exists(modsRoot))
            {
                foreach (string dir in Directory.GetDirectories(modsRoot))
                {
                    loadedCount += TryLoadLocalContentPack(dir);
                    foreach (string dllPath in Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                    {
                        loadedCount += TryLoadExternalAssemblyDll(dllPath, dir);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[ModuleHost] 扫描 BepInEx/mods 目录异常: {ex.Message}");
        }

        return loadedCount;
    }

    /// <summary>热重载所有第三方外部 Mod（卸载旧实例并释放其托管句柄，再重新影子加载）。</summary>
    internal int ReloadExternalMods()
    {
        List<ModContainer> externals;
        lock (_sync)
        {
            externals = _containers.Where(c => !c.Manifest.IsBuiltIn).ToList();
            foreach (var c in externals)
            {
                _containers.Remove(c);
                _byId.Remove(c.Manifest.Id);
            }
        }

        foreach (var c in externals)
        {
            c.UnloadSafely();
        }

        int count = DiscoverAndLoadExternalMods();
        VisualRegistry?.InvalidateVisualCache(null);
        LocalizationRegistry?.NotifyLocaleReloaded("external-reload");
        MainThreadDispatcher.Enqueue(() =>
        {
            BazaarLauncher.Modules.CustomContentVisualModule.Instance?.ReapplyActiveVisualsOnMainThread();
        });
        BazaarLauncherPlugin.Log?.LogInfo($"[ModuleHost] 外部 Mod 热重载完成，当前加载外部扩展数: {count}");
        return count;
    }

    /// <summary>游戏内动态开关指定模块（立即持久化配置并触发 OnEnable / OnDisable 与视觉/文案热重绘）。</summary>
    internal bool SetModuleEnabled(string modId, bool enabled)
    {
        ModContainer container;
        lock (_sync)
        {
            if (!_byId.TryGetValue(modId, out container))
            {
                return false;
            }
        }

        ConfigStore.SetModuleEnabled(modId, enabled);
        bool ok = container.SetEnabled(enabled);
        if (ok)
        {
            VisualRegistry?.InvalidateVisualCache(null);
            LocalizationRegistry?.NotifyLocaleReloaded("module-toggle");
            MainThreadDispatcher.Enqueue(() =>
            {
                BazaarLauncher.Modules.CustomContentVisualModule.Instance?.ReapplyActiveVisualsOnMainThread();
            });
        }
        return ok;
    }

    /// <summary>尝试将已被熔断的模块解除隔离并重新启用。</summary>
    internal bool TryResetQuarantinedModule(string modId)
    {
        ModContainer container;
        lock (_sync)
        {
            if (!_byId.TryGetValue(modId, out container))
            {
                return false;
            }
        }
        return container.ResetAndReenable();
    }

    /// <summary>沙箱受保护调用：捕获指定模块运行期异常并在 10 秒内连续异常 5 次时自动熔断。</summary>
    internal void InvokeGuarded(string modId, string phase, Action action)
    {
        if (action == null) return;
        ModContainer container = null;
        lock (_sync)
        {
            _byId.TryGetValue(modId ?? string.Empty, out container);
        }

        if (container != null && container.State == ModLifecycleState.Quarantined)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (container != null)
            {
                container.RecordRuntimeFault(phase, ex);
            }
            else
            {
                BazaarLauncherPlugin.Log?.LogWarning($"[ModSandbox:{modId}] {phase} 异常: {ex.Message}");
            }
        }
    }

    /// <summary>获取全部内置与外部模块的健康度快照列表。</summary>
    internal IReadOnlyList<ModHealthSnapshot> GetHealthSnapshots()
    {
        lock (_sync)
        {
            var list = new List<ModHealthSnapshot>(_containers.Count);
            for (int i = 0; i < _containers.Count; i++)
            {
                list.Add(_containers[i].BuildSnapshot());
            }
            return list;
        }
    }

    /// <summary>尝试按 ID 获取模块健康快照。</summary>
    internal ModHealthSnapshot GetHealthSnapshot(string modId)
    {
        lock (_sync)
        {
            return _byId.TryGetValue(modId, out var c) ? c.BuildSnapshot() : null;
        }
    }

    /// <summary>卸载全部模块并释放全部 Harmony 实例与托管句柄。</summary>
    internal void ShutdownAll()
    {
        List<ModContainer> copy;
        lock (_sync)
        {
            copy = new List<ModContainer>(_containers);
            _containers.Clear();
            _byId.Clear();
        }

        for (int i = copy.Count - 1; i >= 0; i--)
        {
            copy[i].UnloadSafely();
        }

        EventBus.ClearAll();
        ConfigStore.Save();
    }

    private int TryLoadExternalAssemblyDll(string dllPath, string rootDir)
    {
        string fileName = Path.GetFileName(dllPath);
        if (string.Equals(fileName, "BazaarModSdk.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "BazaarLauncher.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "0Harmony.dll", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        try
        {
            byte[] rawBytes = File.ReadAllBytes(dllPath);
            // D4-P2-5：按文件 SHA-256 去重，防止相同扩展 DLL 在不同扫描目录下被重复 Assembly.Load
            string sha256;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                sha256 = BitConverter.ToString(sha.ComputeHash(rawBytes)).Replace("-", string.Empty);
            }
            lock (_sync)
            {
                if (!_loadedAssemblySha256.Add(sha256))
                {
                    BazaarLauncherPlugin.Log?.LogDebug($"[ModuleHost] 跳过重复 DLL（SHA-256 已加载）: {dllPath}");
                    return 0;
                }
            }
            Assembly asm = Assembly.Load(rawBytes);
            int count = 0;

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                types = rtle.Types?.Where(t => t != null).ToArray() ?? Array.Empty<Type>();
            }

            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IBazaarClientMod).IsAssignableFrom(type)) continue;

                try
                {
                    if (Activator.CreateInstance(type) is not IBazaarClientMod modInstance) continue;
                    var attr = type.GetCustomAttribute<ModMetadataAttribute>();
                    if (attr != null && modInstance.Manifest != null && string.IsNullOrWhiteSpace(modInstance.Manifest.Id))
                    {
                        ModManifest fromAttr = attr.ToManifest(rootDir);
                        modInstance.Manifest.Id = fromAttr.Id;
                        modInstance.Manifest.Name = fromAttr.Name;
                        modInstance.Manifest.Version = fromAttr.Version;
                        modInstance.Manifest.Author = fromAttr.Author;
                        modInstance.Manifest.Description = fromAttr.Description;
                        modInstance.Manifest.RootDirectory = rootDir;
                    }
                    else if (modInstance.Manifest != null && string.IsNullOrWhiteSpace(modInstance.Manifest.RootDirectory))
                    {
                        modInstance.Manifest.RootDirectory = rootDir;
                    }

                    if (RegisterAndLoad(modInstance))
                    {
                        count++;
                        BazaarLauncherPlugin.Log?.LogInfo($"[ModuleHost] 已加载外部 C# Mod: {modInstance.Manifest?.Id} ({fileName})");
                    }
                }
                catch (Exception ex)
                {
                    BazaarLauncherPlugin.Log?.LogWarning($"[ModuleHost] 实例化外部 Mod {type.FullName} 失败: {ex.Message}");
                }
            }

            return count;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[ModuleHost] 加载外部 DLL 失败 '{dllPath}': {ex.Message}");
            return 0;
        }
    }

    private int TryLoadLocalContentPack(string modDir)
    {
        string manifestPath = Path.Combine(modDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            manifestPath = Path.Combine(modDir, "mod.json");
        }
        if (!File.Exists(manifestPath)) return 0;

        try
        {
            var jobj = JObject.Parse(File.ReadAllText(manifestPath));
            string id = (string)(jobj["id"] ?? jobj["Id"]) ?? Path.GetFileName(modDir);
            string name = (string)(jobj["name"] ?? jobj["Name"]) ?? id;
            string version = (string)(jobj["version"] ?? jobj["Version"]) ?? "1.0.0";
            string author = (string)(jobj["author"] ?? jobj["Author"]) ?? "社区作者";
            string desc = (string)(jobj["description"] ?? jobj["Description"]) ?? "本地自定义内容数据包";

            var packMod = new LocalContentPackMod(new ModManifest
            {
                Id = "pack." + id,
                Name = name,
                Version = version,
                Author = author,
                Description = desc,
                IsBuiltIn = false,
                Category = "ExternalPack",
                SupportsHotReload = true,
                RootDirectory = modDir,
            });

            return RegisterAndLoad(packMod) ? 1 : 0;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[ModuleHost] 解析本地数据包 '{manifestPath}' 失败: {ex.Message}");
            return 0;
        }
    }

    private static ModManifest NormalizeManifest(IBazaarClientMod mod)
    {
        ModManifest m = mod.Manifest;
        var attr = mod.GetType().GetCustomAttribute<ModMetadataAttribute>();
        if (m == null)
        {
            m = attr != null
                ? attr.ToManifest()
                : new ModManifest
                {
                    Id = mod.GetType().FullName ?? mod.GetType().Name,
                    Name = mod.GetType().Name,
                    Version = "1.0.0",
                };
        }

        if (string.IsNullOrWhiteSpace(m.Id) && attr != null) m.Id = attr.Id;
        if (string.IsNullOrWhiteSpace(m.Name)) m.Name = m.Id;
        if (string.IsNullOrWhiteSpace(m.Version)) m.Version = "1.0.0";
        return m;
    }

    // ── 本地纯 JSON+PNG 数据包适配器 ─────────────────────────────────────────

    private sealed class LocalContentPackMod : IBazaarClientMod
    {
        private IModContext _ctx;
        public ModManifest Manifest { get; }

        public LocalContentPackMod(ModManifest manifest)
        {
            Manifest = manifest;
        }

        public void OnLoad(IModContext context)
        {
            _ctx = context;
        }

        public void OnEnable()
        {
            if (_ctx == null || string.IsNullOrWhiteSpace(Manifest.RootDirectory)) return;

            // 加载 localization/zh-CN.json
            string zhPath = Path.Combine(Manifest.RootDirectory, "localization", "zh-CN.json");
            if (File.Exists(zhPath))
            {
                try
                {
                    var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(zhPath));
                    if (map != null && map.Count > 0)
                    {
                        _ctx.TrackDisposable(_ctx.Localization.RegisterExactOverrides("zh-CN", map, null, priority: 200));
                    }
                }
                catch (Exception ex)
                {
                    _ctx.Logger.LogWarning("加载数据包本地化失败: " + ex.Message);
                }
            }

            // 加载 cards.json 中的可选视觉与文案映射
            string cardsJson = Path.Combine(Manifest.RootDirectory, "cards.json");
            if (File.Exists(cardsJson))
            {
                try
                {
                    var arr = JArray.Parse(File.ReadAllText(cardsJson));
                    foreach (var token in arr)
                    {
                        if (token is not JObject jo) continue;
                        string imgRel = (string)(jo["image"] ?? jo["localImagePath"] ?? jo["art"] ?? jo["customArtFile"]);
                        string templateId = (string)(jo["templateId"] ?? jo["baseVisualTemplateId"]);
                        string customId = (string)(jo["customId"] ?? jo["id"]);
                        bool explicitPatch = jo["isPatchOverride"]?.Value<bool>() ?? false;
                        // P0-29：非原位 Patch 严禁写入模板级 TemplateId 覆盖，防止本地 Mod 污染所有同底座原版卡
                        bool isDirectTemplateOverride = explicitPatch
                            || string.IsNullOrWhiteSpace(customId)
                            || string.Equals(customId, templateId, StringComparison.OrdinalIgnoreCase);
                        bool isCustomBorrowShell = !isDirectTemplateOverride && !string.IsNullOrWhiteSpace(customId);
                        string effectiveTemplateId = isDirectTemplateOverride ? templateId : null;

                        if (!string.IsNullOrWhiteSpace(imgRel))
                        {
                            string fullImg = Path.IsPathRooted(imgRel) ? imgRel : Path.Combine(Manifest.RootDirectory, imgRel);
                            if (File.Exists(fullImg))
                            {
                                _ctx.TrackDisposable(_ctx.VisualOverrides.RegisterVisualBinding(new CardVisualBinding
                                {
                                    ModId = Manifest.Id,
                                    CustomCardId = customId,
                                    TemplateId = effectiveTemplateId,
                                    LocalImagePath = fullImg,
                                    IsPatchOverride = explicitPatch,
                                    IsCustomBorrowShell = isCustomBorrowShell,
                                    Priority = 150,
                                }));
                            }
                        }

                        string titleZh = (string)(jo["titleZh"] ?? jo["nameZh"]);
                        string titleEn = (string)(jo["titleEn"] ?? jo["nameEn"]);
                        string descZh = (string)(jo["descZh"] ?? jo["descriptionZh"]);
                        string descEn = (string)(jo["descEn"] ?? jo["descriptionEn"]);
                        if (!string.IsNullOrWhiteSpace(titleZh) || !string.IsNullOrWhiteSpace(titleEn)
                            || !string.IsNullOrWhiteSpace(descZh) || !string.IsNullOrWhiteSpace(descEn))
                        {
                            _ctx.TrackDisposable(_ctx.Localization.RegisterCardTextOverride(new CardTextOverride
                            {
                                ModId = Manifest.Id,
                                CustomCardId = customId,
                                TemplateId = effectiveTemplateId,
                                Locale = "zh-CN",
                                Title = titleZh,
                                TitleEn = titleEn,
                                Description = descZh,
                                DescriptionEn = descEn,
                                IsPatchOverride = explicitPatch,
                                IsCustomBorrowShell = isCustomBorrowShell,
                                Priority = 150,
                            }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _ctx.Logger.LogWarning("加载数据包 cards.json 失败: " + ex.Message);
                }
            }
        }

        public void OnDisable() { }
        public void OnUnload() { }
    }

    // ── 单模块崩溃隔离容器（ModSandbox + 熔断） ────────────────────────────────

    private sealed class ModContainer
    {
        private readonly ModuleHost _host;
        private readonly IBazaarClientMod _mod;
        private readonly object _lock = new();
        private readonly List<IDisposable> _trackedDisposables = new();
        private readonly Queue<DateTime> _recentFaultTimestamps = new();
        private CancellationTokenSource _modCts = new();
        private ModContextImpl _context;

        public ModManifest Manifest { get; }
        public ModLifecycleState State { get; private set; } = ModLifecycleState.Unloaded;
        public bool Enabled { get; private set; }
        public double LoadDurationMs { get; private set; }
        public int FaultCount { get; private set; }
        public string LastError { get; private set; } = string.Empty;
        public DateTime LastUpdatedUtc { get; private set; } = DateTime.UtcNow;

        public ModContainer(ModuleHost host, IBazaarClientMod mod, ModManifest manifest, bool initialEnabled)
        {
            _host = host;
            _mod = mod;
            Manifest = manifest;
            Enabled = initialEnabled;
        }

        public void LoadAndActivateIfEnabled()
        {
            var sw = Stopwatch.StartNew();
            Action postUnlockCleanup = null;
            lock (_lock)
            {
                try
                {
                    _context = new ModContextImpl(_host, this);
                    _mod.OnLoad(_context);
                    State = ModLifecycleState.Loaded;
                    if (Enabled)
                    {
                        ActivateInternal();
                    }
                    else
                    {
                        State = ModLifecycleState.Disabled;
                    }
                }
                catch (Exception ex)
                {
                    postUnlockCleanup = PrepareQuarantineLocked("OnLoad", ex);
                }
                finally
                {
                    sw.Stop();
                    LoadDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                    LastUpdatedUtc = DateTime.UtcNow;
                }
            }
            postUnlockCleanup?.Invoke();
        }

        public bool SetEnabled(bool targetEnabled)
        {
            lock (_lock)
            {
                if (State == ModLifecycleState.Quarantined && targetEnabled)
                {
                    return ResetAndReenable();
                }

                Enabled = targetEnabled;
                if (targetEnabled)
                {
                    if (State is ModLifecycleState.Enabled or ModLifecycleState.Degraded)
                    {
                        return true;
                    }
                    try
                    {
                        if (_modCts.IsCancellationRequested)
                        {
                            try { _modCts.Dispose(); } catch { }
                            _modCts = new CancellationTokenSource();
                        }
                        ActivateInternal();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        QuarantineInternal("OnEnable", ex);
                        return false;
                    }
                }
                else
                {
                    DeactivateInternal(ModLifecycleState.Disabled);
                    return true;
                }
            }
        }

        public bool ResetAndReenable()
        {
            lock (_lock)
            {
                _recentFaultTimestamps.Clear();
                LastError = string.Empty;
                Enabled = true;
                if (_modCts.IsCancellationRequested)
                {
                    try { _modCts.Dispose(); } catch { }
                    _modCts = new CancellationTokenSource();
                }
                try
                {
                    ActivateInternal();
                    return true;
                }
                catch (Exception ex)
                {
                    QuarantineInternal("ResetAndReenable", ex);
                    return false;
                }
            }
        }

        public void RecordRuntimeFault(string phase, Exception ex)
        {
            Action postUnlockCleanup = null;
            lock (_lock)
            {
                FaultCount++;
                LastError = $"[{phase}] {ex.GetType().Name}: {ex.Message}";
                LastUpdatedUtc = DateTime.UtcNow;

                DateTime now = DateTime.UtcNow;
                _recentFaultTimestamps.Enqueue(now);
                while (_recentFaultTimestamps.Count > 0 && now - _recentFaultTimestamps.Peek() > CircuitBreakerWindow)
                {
                    _recentFaultTimestamps.Dequeue();
                }

                BazaarLauncherPlugin.Log?.LogWarning($"[ModSandbox:{Manifest.Id}] 异常({FaultCount}): {LastError}");

                if (_recentFaultTimestamps.Count >= CircuitBreakerThreshold && State != ModLifecycleState.Quarantined)
                {
                    postUnlockCleanup = PrepareQuarantineLocked($"熔断({CircuitBreakerThreshold}次/{CircuitBreakerWindow.TotalSeconds:0}s)", ex);
                }
            }
            // D4-P2-5：严格在释放 _lock 之后再执行外部模块 OnDisable 与 UI 提示，彻底根除锁内重入死锁
            postUnlockCleanup?.Invoke();
        }

        public void TrackDisposable(IDisposable disposable)
        {
            if (disposable == null) return;
            lock (_lock)
            {
                _trackedDisposables.Add(disposable);
            }
        }

        public CancellationToken GetShutdownToken()
        {
            lock (_lock)
            {
                return _modCts.Token;
            }
        }

        public void UnloadSafely()
        {
            lock (_lock)
            {
                DeactivateInternal(ModLifecycleState.Unloaded);
                try { _mod.OnUnload(); }
                catch (Exception ex)
                {
                    BazaarLauncherPlugin.Log?.LogWarning($"[ModuleHost:{Manifest.Id}] OnUnload 异常: {ex.Message}");
                }
                try { _modCts.Cancel(); _modCts.Dispose(); } catch { }
                LastUpdatedUtc = DateTime.UtcNow;
            }
        }

        public ModHealthSnapshot BuildSnapshot()
        {
            lock (_lock)
            {
                var patcher = (_mod as IBuiltInPatchModule)?.Patcher;
                int attempted = patcher?.AttemptedCount ?? 0;
                int succeeded = patcher?.SucceededCount ?? 0;
                int degraded = patcher?.DegradedCount ?? 0;
                var notes = patcher?.GetDiagnosticNotesSnapshot() ?? Array.Empty<string>();

                ModLifecycleState effectiveState = State;
                if (effectiveState == ModLifecycleState.Enabled && degraded > 0)
                {
                    effectiveState = ModLifecycleState.Degraded;
                }

                return new ModHealthSnapshot
                {
                    ModId = Manifest.Id,
                    Name = Manifest.Name,
                    Version = Manifest.Version,
                    Category = Manifest.Category,
                    IsBuiltIn = Manifest.IsBuiltIn,
                    State = effectiveState,
                    Enabled = Enabled,
                    SupportsHotReload = Manifest.SupportsHotReload,
                    LoadDurationMs = LoadDurationMs,
                    PatchAttempted = attempted,
                    PatchSucceeded = succeeded,
                    PatchDegraded = degraded,
                    FaultCount = FaultCount,
                    LastError = LastError,
                    LastUpdatedUtc = LastUpdatedUtc.ToString("O"),
                    DiagnosticNotes = notes,
                };
            }
        }

        private void ActivateInternal()
        {
            _mod.OnEnable();
            var patcher = (_mod as IBuiltInPatchModule)?.Patcher;
            State = (patcher != null && patcher.DegradedCount > 0)
                ? ModLifecycleState.Degraded
                : ModLifecycleState.Enabled;
            LastUpdatedUtc = DateTime.UtcNow;
        }

        private void DeactivateInternal(ModLifecycleState nextState)
        {
            try { _modCts.Cancel(); } catch { }
            try { _mod.OnDisable(); }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning($"[ModuleHost:{Manifest.Id}] OnDisable 异常: {ex.Message}");
            }

            if (_mod is IBuiltInPatchModule builtIn && builtIn.Patcher != null)
            {
                builtIn.Patcher.UnpatchAll();
            }

            DisposeTrackedLocked();
            State = nextState;
            LastUpdatedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// D4-P2-5：锁内仅完成状态切换、CTS 取消与资源摘除，返回需在释放 _lock 后执行的外部 OnDisable / Toast 委托，
        /// 彻底避免锁内回调外部代码引发死锁或非主线程访问 Unity UI 崩溃。
        /// </summary>
        private Action PrepareQuarantineLocked(string phase, Exception ex)
        {
            FaultCount++;
            LastError = $"[{phase}] {ex.GetType().Name}: {ex.Message}";
            BazaarLauncherPlugin.Log?.LogError($"[ModSandbox:{Manifest.Id}] 触发崩溃隔离熔断 -> {LastError}");
            try { _modCts.Cancel(); } catch { }
            var disposablesCopy = new List<IDisposable>(_trackedDisposables);
            _trackedDisposables.Clear();
            State = ModLifecycleState.Quarantined;
            LastUpdatedUtc = DateTime.UtcNow;

            return () =>
            {
                void RunCleanup()
                {
                    try { _mod.OnDisable(); }
                    catch (Exception dex)
                    {
                        BazaarLauncherPlugin.Log?.LogWarning($"[ModuleHost:{Manifest.Id}] Quarantine OnDisable 异常: {dex.Message}");
                    }
                    if (_mod is IBuiltInPatchModule builtIn && builtIn.Patcher != null)
                    {
                        try { builtIn.Patcher.UnpatchAll(); } catch { }
                    }
                    for (int i = disposablesCopy.Count - 1; i >= 0; i--)
                    {
                        try { disposablesCopy[i]?.Dispose(); } catch { }
                    }
                    try
                    {
                        _host.UiHost?.ShowToast($"模块「{Manifest.Name}」发生异常已自动隔离，游戏可继续运行", isError: true);
                    }
                    catch { }
                }

                if (MainThreadDispatcher.IsOnMainThread)
                    RunCleanup();
                else
                    MainThreadDispatcher.Enqueue(RunCleanup);
            };
        }

        private void QuarantineInternal(string phase, Exception ex)
        {
            // 兼容 SetEnabled / ResetAndReenable 锁内同步调用路径：锁内更新态，通过 MainThreadDispatcher 延后执行外部 OnDisable
            var cleanup = PrepareQuarantineLocked(phase, ex);
            MainThreadDispatcher.Enqueue(cleanup);
        }

        private void DisposeTrackedLocked()
        {
            for (int i = _trackedDisposables.Count - 1; i >= 0; i--)
            {
                try { _trackedDisposables[i]?.Dispose(); }
                catch { }
            }
            _trackedDisposables.Clear();
        }
    }

    private sealed class ModContextImpl : IModContext
    {
        private readonly ModuleHost _host;
        private readonly ModContainer _container;

        public ModManifest Manifest => _container.Manifest;
        public IModLogger Logger { get; }
        public IModConfig Config { get; }
        public IModEventBus EventBus { get; }
        public INativeUiHost UiHost { get; }
        public ICardVisualOverrideRegistry VisualOverrides { get; }
        public ILocalizationOverrideRegistry Localization { get; }
        public IPerformanceTelemetry Telemetry => _host.Telemetry;
        public string ServerBaseUrl => NameAuth.GetServerBaseUrl();
        public string CacheDirectory => ModuleHost.CacheDirectory;
        public CancellationToken ShutdownToken => _container.GetShutdownToken();

        public ModContextImpl(ModuleHost host, ModContainer container)
        {
            _host = host;
            _container = container;
            Logger = new PrefixedModLogger(container.Manifest.Id);
            Config = host.ConfigStore.CreateScopedConfig(container.Manifest.Id);
            EventBus = new TrackedEventBusProxy(host.EventBus, container);
            UiHost = new TrackedUiHostProxy(host, container);
            VisualOverrides = new TrackedVisualProxy(host, container);
            Localization = new TrackedLocalizationProxy(host, container);
        }

        public void RunOnMainThread(Action action)
        {
            if (action == null) return;
            MainThreadDispatcher.Enqueue(() => _host.InvokeGuarded(Manifest.Id, "MainThread", action));
        }

        public void TrackDisposable(IDisposable disposable) => _container.TrackDisposable(disposable);
    }

    private sealed class PrefixedModLogger : IModLogger
    {
        private readonly string _prefix;
        public PrefixedModLogger(string modId) => _prefix = $"[Mod:{modId}] ";
        public void LogDebug(string message) => BazaarLauncherPlugin.Log?.LogDebug(_prefix + message);
        public void LogInfo(string message) => BazaarLauncherPlugin.Log?.LogInfo(_prefix + message);
        public void LogWarning(string message) => BazaarLauncherPlugin.Log?.LogWarning(_prefix + message);
        public void LogError(string message, Exception exception = null)
            => BazaarLauncherPlugin.Log?.LogError(exception == null ? _prefix + message : $"{_prefix}{message}: {exception}");
    }

    private sealed class TrackedEventBusProxy : IModEventBus
    {
        private readonly IModEventBus _inner;
        private readonly ModContainer _container;
        public TrackedEventBusProxy(IModEventBus inner, ModContainer container) { _inner = inner; _container = container; }
        public IDisposable Subscribe<TEvent>(Action<TEvent> handler, int priority = 0) where TEvent : struct
        {
            var d = _inner.Subscribe<TEvent>(evt =>
            {
                try { handler(evt); }
                catch (Exception ex) { _container.RecordRuntimeFault($"Event:{typeof(TEvent).Name}", ex); }
            }, priority);
            _container.TrackDisposable(d);
            return d;
        }
        public void Publish<TEvent>(in TEvent evt) where TEvent : struct => _inner.Publish(evt);
    }

    private sealed class TrackedUiHostProxy : INativeUiHost
    {
        private readonly ModuleHost _host;
        private readonly ModContainer _container;
        public TrackedUiHostProxy(ModuleHost host, ModContainer container) { _host = host; _container = container; }
        private INativeUiHost Inner => _host.UiHost;

        public IDisposable RegisterNavDrawerEntry(NativeNavEntryDescriptor descriptor)
        {
            var d = Inner?.RegisterNavDrawerEntry(descriptor);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public IDisposable RegisterPlaymodeOption(NativePlaymodeOptionDescriptor descriptor)
        {
            var d = Inner?.RegisterPlaymodeOption(descriptor);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public IDisposable RegisterEscapeMenuButton(NativeMenuButtonDescriptor descriptor)
        {
            var d = Inner?.RegisterEscapeMenuButton(descriptor);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public IDisposable RegisterOptionsSection(NativeOptionsSectionDescriptor descriptor)
        {
            var d = Inner?.RegisterOptionsSection(descriptor);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public IDisposable RegisterWorkshopTab(NativeWorkshopTabDescriptor descriptor)
        {
            var d = Inner?.RegisterWorkshopTab(descriptor);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public void OpenNativePopup(string popupId, Action<object> buildContent = null) => Inner?.OpenNativePopup(popupId, buildContent);
        public void CloseNativePopup(string popupId) => Inner?.CloseNativePopup(popupId);
        public void ShowToast(string message, bool isError = false) => Inner?.ShowToast(message, isError);
        public IReadOnlyList<NativeNavEntryDescriptor> GetRegisteredNavEntries() => Inner?.GetRegisteredNavEntries() ?? Array.Empty<NativeNavEntryDescriptor>();
        public IReadOnlyList<NativePlaymodeOptionDescriptor> GetRegisteredPlaymodeOptions() => Inner?.GetRegisteredPlaymodeOptions() ?? Array.Empty<NativePlaymodeOptionDescriptor>();
        public IReadOnlyList<NativeMenuButtonDescriptor> GetRegisteredEscapeMenuButtons() => Inner?.GetRegisteredEscapeMenuButtons() ?? Array.Empty<NativeMenuButtonDescriptor>();
        public IReadOnlyList<NativeWorkshopTabDescriptor> GetRegisteredWorkshopTabs() => Inner?.GetRegisteredWorkshopTabs() ?? Array.Empty<NativeWorkshopTabDescriptor>();
    }

    private sealed class TrackedVisualProxy : ICardVisualOverrideRegistry
    {
        private readonly ModuleHost _host;
        private readonly ModContainer _container;
        public TrackedVisualProxy(ModuleHost host, ModContainer container) { _host = host; _container = container; }
        private ICardVisualOverrideRegistry Inner => _host.VisualRegistry;

        public IDisposable RegisterVisualBinding(CardVisualBinding binding)
        {
            var d = Inner?.RegisterVisualBinding(binding);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public IDisposable RegisterDynamicResolver(Func<CardVisualQuery, CardVisualBinding> resolver, int priority = 0)
        {
            var d = Inner?.RegisterDynamicResolver(resolver, priority);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public bool TryResolveVisual(in CardVisualQuery query, out CardVisualBinding binding)
        {
            if (Inner != null) return Inner.TryResolveVisual(query, out binding);
            binding = null;
            return false;
        }

        public void InvalidateVisualCache(string customIdOrTemplateId = null) => Inner?.InvalidateVisualCache(customIdOrTemplateId);
        public int GetRegisteredBindingCount() => Inner?.GetRegisteredBindingCount() ?? 0;
    }

    private sealed class TrackedLocalizationProxy : ILocalizationOverrideRegistry
    {
        private readonly ModuleHost _host;
        private readonly ModContainer _container;
        public TrackedLocalizationProxy(ModuleHost host, ModContainer container) { _host = host; _container = container; }
        private ILocalizationOverrideRegistry Inner => _host.LocalizationRegistry;

        public string CurrentLocale => Inner?.CurrentLocale ?? "zh-CN";

        public IDisposable RegisterHashOverrides(string locale, IReadOnlyDictionary<string, string> hashMap, int priority = 0)
        {
            var d = Inner?.RegisterHashOverrides(locale, hashMap, priority);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public IDisposable RegisterExactOverrides(string locale, IReadOnlyDictionary<string, string> exactMap, string contextKey = null, int priority = 0)
        {
            var d = Inner?.RegisterExactOverrides(locale, exactMap, contextKey, priority);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public IDisposable RegisterRegexOverride(string locale, LocalizationRegexRule rule, int priority = 0)
        {
            var d = Inner?.RegisterRegexOverride(locale, rule, priority);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public IDisposable RegisterCardTextOverride(CardTextOverride cardOverride)
        {
            var d = Inner?.RegisterCardTextOverride(cardOverride);
            if (d != null) _container.TrackDisposable(d);
            return d ?? EmptyDisposable.Instance;
        }

        public bool TryResolveText(string locale, string hash, string rawText, string contextKey, out string translated)
        {
            if (Inner != null) return Inner.TryResolveText(locale, hash, rawText, contextKey, out translated);
            translated = rawText ?? string.Empty;
            return false;
        }

        public bool TryResolveCardText(string locale, string instanceId, string customCardId, string templateId, out CardTextOverride cardOverride)
        {
            if (Inner != null) return Inner.TryResolveCardText(locale, instanceId, customCardId, templateId, out cardOverride);
            cardOverride = null;
            return false;
        }

        public void NotifyLocaleReloaded(string overlayRevision = "") => Inner?.NotifyLocaleReloaded(overlayRevision);
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
