namespace BazaarHub.Models;

/// <summary>
/// 服务端 /api/client-update/manifest 响应中的单个组件文件描述。
/// </summary>
public sealed class RemoteClientFileEntry
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = "plugin";
    public string Version { get; init; } = string.Empty;
    public string RelativeTargetPath { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public bool Required { get; init; }
}

/// <summary>
/// 服务端客户端更新清单汇总。
/// </summary>
public sealed class ClientUpdateManifest
{
    public string Channel { get; init; } = "stable";
    public string LauncherVersion { get; init; } = string.Empty;
    public string HubVersion { get; init; } = string.Empty;
    public string LocalizationVersion { get; init; } = string.Empty;
    public string MinRequiredVersion { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public List<RemoteClientFileEntry> Files { get; init; } = new();
}

/// <summary>
/// 服务端 /api/mods/manifest 响应中的单个 Mod 描述。
/// </summary>
public sealed class RemoteModEntry
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Scope { get; init; } = "multiplayer-safe";
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string LocalizationHash { get; init; } = string.Empty;
    public string VisualCatalogHash { get; init; } = string.Empty;
    public List<RemoteModAssetEntry> Assets { get; init; } = new();
}

/// <summary>
/// 服务端 Mod 资产（PNG/WebP 等客户端贴图）描述。
/// </summary>
public sealed class RemoteModAssetEntry
{
    public string ModId { get; init; } = string.Empty;
    public string LogicalPath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// 服务端 Mod 列表清单汇总。
/// </summary>
public sealed class RemoteModsManifest
{
    public string ManifestHash { get; init; } = string.Empty;
    public List<RemoteModEntry> Mods { get; init; } = new();
    public List<RemoteModAssetEntry> Assets { get; init; } = new();

    public int TotalAssetCount =>
        Assets.Count > 0 ? Assets.Count : Mods.Sum(m => m.Assets.Count);
}

/// <summary>
/// 服务端外挂汉化词典摘要。
/// </summary>
public sealed class LocalizationOverlayInfo
{
    public bool Available { get; init; }
    public string Revision { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public int EntryCount { get; init; }
    public byte[] RawBytes { get; init; } = Array.Empty<byte>();
    public string SourceEndpoint { get; init; } = string.Empty;
}

/// <summary>
/// 单条文件同步状态项（供 UI 表格展示）。
/// </summary>
public sealed class SyncItemStatus
{
    public string Category { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string TargetRelativePath { get; init; } = string.Empty;
    public string LocalSha256 { get; init; } = string.Empty;
    public string RemoteSha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// 同步检查与执行结果汇总。
/// </summary>
public sealed class UpdateSyncReport
{
    public bool Success { get; set; }
    public bool DegradedOffline { get; set; }
    public string ServerBase { get; set; } = string.Empty;
    public string SummaryMessage { get; set; } = string.Empty;
    public ClientUpdateManifest? ClientManifest { get; set; }
    public RemoteModsManifest? ModsManifest { get; set; }
    public string LocalizationRevision { get; set; } = string.Empty;
    public int DownloadedCount { get; set; }
    public int SkippedUpToDateCount { get; set; }
    public int FailedCount { get; set; }
    public long TotalBytesDownloaded { get; set; }
    public List<SyncItemStatus> Items { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>
/// 增量同步实时进度回调模型。
/// </summary>
public sealed class SyncProgress
{
    public int Percent { get; init; }
    public string Stage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
