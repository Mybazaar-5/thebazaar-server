using System;
using BazaarLauncher.Core;
using BazaarModSdk;
using HarmonyLib;

namespace BazaarLauncher.Modules;

/// <summary>
/// 将原版各核心、兼容、玩法与诊断组件封装为标准崩溃隔离模块（IBuiltInPatchModule），
/// 与 PerfOptimizerModule、NativeMenuEmbedModule、CustomContentVisualModule、LocalizationOverlayModule 共同构成完整 14 模块体系。
/// </summary>
internal abstract class BuiltInModuleBase : IBuiltInPatchModule
{
    public ModManifest Manifest { get; }
    public SafeHarmonyPatcher Patcher { get; protected set; }
    protected IModContext Context { get; private set; }

    protected BuiltInModuleBase(
        string id,
        string name,
        string category,
        string description,
        bool defaultEnabled = true,
        bool supportsHotReload = true)
    {
        Manifest = new ModManifest
        {
            Id = id,
            Name = name,
            Version = "2.1.0",
            Author = "BazaarModKit",
            Description = description,
            IsBuiltIn = true,
            DefaultEnabled = defaultEnabled,
            Category = category,
            SupportsHotReload = supportsHotReload,
        };
    }

    public virtual void OnLoad(IModContext context) => Context = context;

    public virtual void OnEnable()
    {
        Patcher = new SafeHarmonyPatcher(Manifest.Id);
        OnEnableWithPatcher(Patcher);
    }

    protected abstract void OnEnableWithPatcher(SafeHarmonyPatcher patcher);

    public virtual void OnDisable()
    {
        OnDisableCore();
        Patcher?.UnpatchAll();
    }

    protected virtual void OnDisableCore() { }

    public virtual void OnUnload() => OnDisable();
}

/// <summary>模块 1：云端隔离防火墙与 URL 重定向（Core.NetworkRedirectModule）。</summary>
internal sealed class NetworkRedirectModule : BuiltInModuleBase
{
    public NetworkRedirectModule()
        : base("bazaar.core.network_redirect", "云端隔离与服务器重定向", "Core",
            "拦截外部官方云/遥测/WebSocket 出站连接并重定向 5 大端点至自建服（0 外联泄漏）。", defaultEnabled: true, supportsHotReload: false)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher)
    {
        CloudGuard.Apply(patcher);
        BazaarLauncherPlugin.ApplyServerUrlPatches(patcher);
    }

    protected override void OnDisableCore() => CloudGuard.ResetAppliedFlag();
}

/// <summary>模块 2：离线 Steam 旁路与用户名即登录（Core.OfflineAuthModule）。</summary>
internal sealed class OfflineAuthModule : BuiltInModuleBase
{
    public OfflineAuthModule()
        : base("bazaar.core.offline_auth", "离线鉴权与昵称登录", "Core",
            "旁路 Steam 票据依赖并启用用户名即登录（Task<AuthResult> 精确泛型闭合 + 复用连接池）。", defaultEnabled: true, supportsHotReload: false)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher)
    {
        if (BazaarLauncherPlugin.BypassSteam?.Value != false)
        {
            NoSteam.Apply(patcher);
        }
        NameAuth.Apply(patcher);
    }

    protected override void OnDisableCore()
    {
        NoSteam.ResetAppliedFlag();
        NameAuth.ResetAppliedFlag();
    }
}

/// <summary>模块 3：全英雄/藏品解锁与 396 套藏品懒加载（Gameplay.CollectionUnlockModule）。</summary>
internal sealed class CollectionUnlockModule : BuiltInModuleBase
{
    public CollectionUnlockModule()
        : base("bazaar.gameplay.collection_unlock", "全解锁与藏品按需懒加载", "Gameplay",
            "全英雄（含双龙）与全外观解锁，启动跳过未穿戴藏品全量预加载（已清除 2 处死补丁并锁定 TryGetHeroSO 重载）。", defaultEnabled: true, supportsHotReload: true)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher)
    {
        if (BazaarLauncherPlugin.UnlockEverything?.Value != false)
        {
            Unlocker.Apply(patcher);
        }
    }

    protected override void OnDisableCore() => Unlocker.ResetAppliedFlag();
}

/// <summary>模块 4：战斗倍速稳定控制与战后视觉清理（Gameplay.CombatSyncModule）。</summary>
internal sealed class CombatSyncModule : BuiltInModuleBase
{
    public CombatSyncModule()
        : base("bazaar.gameplay.combat_sync", "战斗倍速与战后清理引擎", "Gameplay",
            "合并单一 Simulate 补丁、线程安全 1x/2x/3x/5x 倍速控制与战后冰冻/飞行视觉双保险清理。", defaultEnabled: true, supportsHotReload: true)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher)
    {
        CombatPatch.Apply(patcher);
    }

    protected override void OnDisableCore() => CombatPatch.ResetAppliedFlag();
}

/// <summary>模块 5：GameSim/VFX/结算兼容加固组（Compat.GameSimPatchModule）。</summary>
internal sealed class CompatGameSimModule : BuiltInModuleBase
{
    public CompatGameSimModule()
        : base("bazaar.compat.gamesim", "协议与战斗事件兼容防护", "Compat",
            "保护 CombatSimHandler / VFXManager / GameSimHandler / Quest / 结算与 PvP 对手同步，缓存热路径反射并兼容只读事件列表。", defaultEnabled: true, supportsHotReload: true)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher)
    {
        CompatAuditPatch.ApplyGameSimGroup(patcher.RawHarmony);
    }

    protected override void OnDisableCore() => CompatAuditPatch.ResetAppliedFlag();
}

/// <summary>模块 6：强化基座与悬停高亮兼容组（Compat.HintEnchantModule）。</summary>
internal sealed class CompatHintEnchantModule : BuiltInModuleBase
{
    public CompatHintEnchantModule()
        : base("bazaar.compat.hints_pedestal", "强化基座与悬停高亮优化", "Compat",
            "加速强化基座刷新、优化悬停与目标筛选热路径（ThreadStatic 复用池零高频分配、跨局自动清理）。", defaultEnabled: true, supportsHotReload: true)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher)
    {
        CompatAuditPatch.ApplyPedestalAndHintGroup(patcher.RawHarmony);
    }

    protected override void OnDisableCore() => CompatAuditPatch.ResetAppliedFlag();
}

/// <summary>模块 7：遭遇立绘补全与挑战空安全组（Compat.EncounterAssetModule）。</summary>
internal sealed class CompatEncounterAssetModule : BuiltInModuleBase
{
    public CompatEncounterAssetModule()
        : base("bazaar.compat.encounter_assets", "遭遇立绘与挑战防崩溃防护", "Compat",
            "为子事件/EncounterStep 自动回退父事件立绘与 Tooltip，防止结算页 PrizePassChallengesView 空引用崩溃。", defaultEnabled: true, supportsHotReload: true)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher)
    {
        CompatAuditPatch.ApplyEncounterAndChallengeGroup(patcher.RawHarmony);
    }

    protected override void OnDisableCore() => CompatAuditPatch.ResetAppliedFlag();
}

/// <summary>模块 8：原生工坊、规则预设与多人社交综合面板（Gameplay.WorkshopUiModule）。</summary>
internal sealed class WorkshopUiModule : BuiltInModuleBase
{
    public WorkshopUiModule()
        : base("bazaar.gameplay.workshop_ui", "自建工坊、多人社交与规则控制台", "Gameplay",
            "事件驱动按需刷新的综合原生界面（7 模式/3 预设/14 词条/Mod 中心/沙盒控制台/社交聊天与观战回放/性能诊断，保留 F8 快捷键）。", defaultEnabled: true, supportsHotReload: true)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher)
    {
        CustomGameplayUI.EnsureInstalled(patcher);
    }

    protected override void OnDisableCore()
    {
        CustomGameplayUI.Shutdown();
    }
}

/// <summary>模块 9（诊断，默认关闭）：只读状态观测服务（Diag.InspectorModule）。</summary>
internal sealed class DiagInspectorModule : BuiltInModuleBase
{
    public DiagInspectorModule()
        : base("bazaar.diag.inspector", "只读运行时状态观测器", "Diagnostics",
            "提供纯净只读的内存与 UI 树快照查询（已彻底移除 ValidateCards 破坏性副作用）。", defaultEnabled: true, supportsHotReload: true)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher) { }
}

/// <summary>模块 10（诊断，默认关闭）：本地自动化驱动服务（Diag.GameDriverModule）。</summary>
internal sealed class DiagGameDriverModule : BuiltInModuleBase
{
    public DiagGameDriverModule()
        : base("bazaar.diag.gamedriver", "本地 HTTP 自动化驱动（默认关闭）", "Diagnostics",
            "本地 127.0.0.1:27182 调试/自动化接口（异步无死锁 + 防 CSRF + 可选 Token 鉴权），默认关闭，支持 bz.exe 环境变量按需启用。", defaultEnabled: false, supportsHotReload: true)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher)
    {
        GameDriver.EnsureInstalled();
    }

    protected override void OnDisableCore()
    {
        GameDriver.Shutdown();
    }
}

/// <summary>模块 11（诊断，默认关闭）：异步 HTTP 流量抓包模块（Diag.TrafficDumpModule）。</summary>
internal sealed class DiagTrafficDumpModule : BuiltInModuleBase
{
    public DiagTrafficDumpModule()
        : base("bazaar.diag.traffic_dump", "HTTP 流量诊断抓包（默认关闭）", "Diagnostics",
            "无阻塞异步落盘 HttpClient 请求/响应诊断日志（已修复响应侧 Task<HttpResponseMessage> 捕获）。", defaultEnabled: false, supportsHotReload: true)
    { }

    protected override void OnEnableWithPatcher(SafeHarmonyPatcher patcher)
    {
        TrafficDump.Apply(patcher);
    }

    protected override void OnDisableCore() => TrafficDump.ResetAppliedFlag();
}
