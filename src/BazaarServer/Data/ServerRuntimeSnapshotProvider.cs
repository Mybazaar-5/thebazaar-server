using BazaarCore.Data;
using BazaarServer.Mods;

namespace BazaarServer.Data;

/// <summary>
/// D2-P2-04 / D8-P2-06：以 <see cref="ServerModHost.ModSetHash"/> 为版本哨兵的不可变派生运行时视图提供器。
/// 统一托管热重载同步刷新的 4 份派生只读视图（OverlayLocalizationStore / LevelUpTable / GameModeConfig / CardArchetypeIndex）
/// 以及大厅目录 DTO（CardTaxonomyCatalogDto）：
/// - 读路径无锁（Volatile.Read + Hash 比对），零撕裂；
/// - 仅在 Mod 启停/热重载致 ModSetHash 变化时单次重建全套视图，彻底消除 Program.cs 启动期单次缓存陈旧与重复构建问题。
/// </summary>
public sealed class ServerRuntimeSnapshotProvider
{
    public sealed record RuntimeSnapshot(
        string ModSetHash,
        OverlayLocalizationStore Localization,
        LevelUpTable LevelUpTable,
        GameModeConfig GameModeConfig,
        CardArchetypeIndex ArchetypeIndex,
        CardTaxonomyCatalogDto CatalogDto);

    private readonly ServerModHost _modHost;
    private readonly CardPool _cardPool;
    private readonly object _rebuildLock = new();
    private RuntimeSnapshot _current;

    public ServerRuntimeSnapshotProvider(ServerModHost modHost, CardPool cardPool)
    {
        _modHost = modHost ?? throw new ArgumentNullException(nameof(modHost));
        _cardPool = cardPool ?? throw new ArgumentNullException(nameof(cardPool));
        _current = BuildSnapshot(_modHost.ModSetHash);
    }

    /// <summary>获取与当前 <see cref="ServerModHost.ModSetHash"/> 严格对齐的不可变派生运行时快照。</summary>
    public RuntimeSnapshot Current
    {
        get
        {
            string liveHash = _modHost.ModSetHash;
            var snap = Volatile.Read(ref _current);
            if (string.Equals(snap.ModSetHash, liveHash, StringComparison.Ordinal))
                return snap;

            lock (_rebuildLock)
            {
                liveHash = _modHost.ModSetHash;
                if (string.Equals(_current.ModSetHash, liveHash, StringComparison.Ordinal))
                    return _current;

                var rebuilt = BuildSnapshot(liveHash);
                Volatile.Write(ref _current, rebuilt);
                return rebuilt;
            }
        }
    }

    private RuntimeSnapshot BuildSnapshot(string modSetHash)
    {
        var activeRepo = _modHost.ActiveRepository;
        var loc = _modHost.ActiveLocalization;
        var levelUps = LevelUpTable.FromRepository(activeRepo);
        var gameMode = GameModeConfig.FromRepository(activeRepo);
        var archetypeIndex = CardArchetypeIndex.Build(_cardPool, activeRepo);
        var catalogDto = archetypeIndex.ToCatalogDto(_cardPool, activeRepo);
        return new RuntimeSnapshot(modSetHash, loc, levelUps, gameMode, archetypeIndex, catalogDto);
    }
}
