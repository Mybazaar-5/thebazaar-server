using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Tournament;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 7 社区 Mod 上传/下载、按房/单局隔离加载与锦标赛房间 Mod 同步回归测试。
/// </summary>
public sealed class CommunityModAndRoomSyncTests
{
    private sealed record TestFixture(
        string RepoRoot,
        string DataDir,
        string DbPath,
        GameDataRepository BaseRepo,
        HashSet<string> Whitelist,
        CardPool DefaultPool,
        List<BazaarServer.Data.MonsterTemplate> DefaultMonsters,
        CardArchetypeIndex DefaultArchetypes);

    private static readonly Lazy<TestFixture> Shared = new(LoadFixture);

    private static TestFixture LoadFixture()
    {
        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        string? repoRoot = null;
        while (cur != null)
        {
            if (File.Exists(Path.Combine(cur.FullName, "Bazaar.sln")))
            {
                repoRoot = cur.FullName;
                break;
            }
            cur = cur.Parent;
        }
        Assert.False(string.IsNullOrWhiteSpace(repoRoot), "未找到 Bazaar.sln 根目录");

        string dataDir = Path.Combine(repoRoot!, "dist", "data");
        string dbPath = Path.Combine(dataDir, "GameData.db");
        string? zhPath = SteamPathResolver.ResolveZhCnTranslationPath(dataDir);
        var baseRepo = GameDatabaseLoader.Load(dbPath, zhPath);

        var wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "valid_card_ids.json"), Encoding.UTF8)))
        {
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("valid_ids");
            foreach (var it in arr.EnumerateArray())
                if (it.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(it.GetString()))
                    wl.Add(it.GetString()!);
        }

        var pool = CardPool.Load(dbPath);
        var monsters = MonsterPool.Load(dbPath);
        var archetypes = CardArchetypeIndex.Build(pool, baseRepo);

        return new TestFixture(repoRoot!, dataDir, dbPath, baseRepo, wl, pool, monsters, archetypes);
    }

    private static (string TempRoot, ServerModHost Host, CommunityModStore Store) CreateIsolatedHostAndStore(TestFixture fx)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "bazaar_comm_test_" + Guid.NewGuid().ToString("N")[..10]);
        string modsDir = Path.Combine(tempRoot, "mods");
        string stateDir = Path.Combine(tempRoot, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        var host = new ServerModHost(
            fx.BaseRepo,
            fx.DataDir,
            stateDir,
            fx.Whitelist,
            modsRootOverride: modsDir);
        var store = new CommunityModStore(host, stateDir);
        return (tempRoot, host, store);
    }

    /// <summary>
    /// 基于仓库中已验证合法的 mods/clockwork-forge 克隆并改名打包为测试社区 .bazaarmod.zip。
    /// </summary>
    private static byte[] BuildValidCommunityModZip(TestFixture fx, string newModId)
    {
        string srcDir = Path.Combine(fx.RepoRoot, "mods", "clockwork-forge");
        Assert.True(Directory.Exists(srcDir), "缺少示例 Mod 目录 mods/clockwork-forge");

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
                if (rel.StartsWith(".", StringComparison.Ordinal)) continue;

                var entry = archive.CreateEntry($"{newModId}/{rel}", CompressionLevel.Optimal);
                using var es = entry.Open();
                if (rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string text = File.ReadAllText(file, Encoding.UTF8)
                        .Replace("clockwork-forge", newModId, StringComparison.OrdinalIgnoreCase);
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    es.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    using var fs = File.OpenRead(file);
                    fs.CopyTo(es);
                }
            }
        }
        return ms.ToArray();
    }

    private static byte[] BuildRawZip(params (string EntryPath, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var (path, content) in entries)
            {
                var e = archive.CreateEntry(path);
                using var s = e.Open();
                s.Write(content, 0, content.Length);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task UploadDownloadPersistAndReload_ShouldSucceedAndTrackDownloads()
    {
        var fx = Shared.Value;
        var (tempRoot, host, store) = CreateIsolatedHostAndStore(fx);
        try
        {
            const string modId = "test-comm-forge";
            byte[] zipBytes = BuildValidCommunityModZip(fx, modId);

            var upload = await store.UploadZipAsync(zipBytes, "acc_alice", "Alice", isAdmin: false);
            Assert.True(upload.Ok, $"合法社区 Mod 上传应通过校验：{upload.Error}");
            Assert.Equal(200, upload.StatusCode);
            Assert.NotNull(upload.Record);
            Assert.Equal(modId, upload.Record!.ModId);
            Assert.True(upload.Record.CustomCardCount > 0);
            Assert.True(upload.Record.AssetCount > 0);

            // 上传者可见删除权限，且默认不污染服务器全局开启态
            var listForOwner = store.ListCatalog("acc_alice", isAdmin: false);
            var ownerItem = Assert.Single(listForOwner, m => m.ModId == modId);
            Assert.False(ownerItem.IsServerDefault);
            Assert.True(ownerItem.IsInCommunityStore);
            Assert.True(ownerItem.CanDelete);
            Assert.False(ownerItem.CanToggleServerDefault);
            Assert.Equal(0, ownerItem.DownloadCount);

            // 下载打包并计数 +1
            bool dlOk = store.TryGetDownloadZip(modId, out byte[] downloaded, out string fileName, out string? dlErr);
            Assert.True(dlOk, dlErr);
            Assert.True(downloaded.Length > 100);
            Assert.EndsWith(".bazaarmod.zip", fileName, StringComparison.OrdinalIgnoreCase);

            var afterDl = Assert.Single(store.ListCatalog("acc_alice", isAdmin: false), m => m.ModId == modId);
            Assert.Equal(1, afterDl.DownloadCount);

            // 模拟服务端重启：重新从 stateDir 加载 index.json 与 extracted/
            var reloadedStore = new CommunityModStore(host, Path.Combine(tempRoot, "state"));
            Assert.Equal(1, reloadedStore.Count);
            var reloadedItem = Assert.Single(reloadedStore.ListCatalog("acc_alice", isAdmin: false), m => m.ModId == modId);
            Assert.Equal(1, reloadedItem.DownloadCount);
            Assert.Equal(ownerItem.PackageSha256, reloadedItem.PackageSha256);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task UploadSecurity_ShouldRejectExecutableFilesAndZipSlip()
    {
        var fx = Shared.Value;
        var (tempRoot, _, store) = CreateIsolatedHostAndStore(fx);
        try
        {
            byte[] dllZip = BuildRawZip(
                ("bad-mod/mod.json", Encoding.UTF8.GetBytes("{\"id\":\"bad-mod\",\"name\":\"Bad\"}")),
                ("bad-mod/evil.dll", new byte[] { 0x4D, 0x5A, 0x90, 0x00 }));
            var r1 = await store.UploadZipAsync(dllZip, "acc_a", "A", isAdmin: false);
            Assert.False(r1.Ok);
            Assert.Equal(400, r1.StatusCode);

            byte[] slipZip = BuildRawZip(
                ("../escape.json", Encoding.UTF8.GetBytes("{}")));
            var r2 = await store.UploadZipAsync(slipZip, "acc_a", "A", isAdmin: false);
            Assert.False(r2.Ok);
            Assert.Equal(400, r2.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Ownership_ShouldBlockOtherPlayersFromOverwritingOrDeleting()
    {
        var fx = Shared.Value;
        var (tempRoot, _, store) = CreateIsolatedHostAndStore(fx);
        try
        {
            const string modId = "test-owner-forge";
            byte[] zipBytes = BuildValidCommunityModZip(fx, modId);

            var u1 = await store.UploadZipAsync(zipBytes, "acc_alice", "Alice", isAdmin: false);
            Assert.True(u1.Ok, u1.Error);

            // 非上传者 Bob 无法覆盖同名 Mod，也无法删除
            var overwriteByBob = await store.UploadZipAsync(zipBytes, "acc_bob", "Bob", isAdmin: false);
            Assert.False(overwriteByBob.Ok);
            Assert.Equal(403, overwriteByBob.StatusCode);

            var deleteByBob = store.DeleteCommunityMod(modId, "acc_bob", isAdmin: false);
            Assert.False(deleteByBob.Ok);
            Assert.Equal(403, deleteByBob.StatusCode);

            var listForBob = Assert.Single(store.ListCatalog("acc_bob", isAdmin: false), m => m.ModId == modId);
            Assert.False(listForBob.CanDelete);

            // 原上传者 Alice 可以覆盖更新，也可以删除
            var updateByAlice = await store.UploadZipAsync(zipBytes, "acc_alice", "Alice", isAdmin: false);
            Assert.True(updateByAlice.Ok, updateByAlice.Error);

            var deleteByAlice = store.DeleteCommunityMod(modId, "acc_alice", isAdmin: false);
            Assert.True(deleteByAlice.Ok, deleteByAlice.Error);
            Assert.Equal(0, store.Count);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RoomIsolatedModContext_ShouldLoadCommunityModOnlyForSelectedRunWithoutPollutingDefaultPool()
    {
        var fx = Shared.Value;
        var (tempRoot, host, store) = CreateIsolatedHostAndStore(fx);
        try
        {
            const string modId = "test-room-forge";
            const string customCardId = "test-room-forge:z_sword_custom_d83alo";
            byte[] zipBytes = BuildValidCommunityModZip(fx, modId);

            var upload = await store.UploadZipAsync(zipBytes, "acc_host", "Host", isAdmin: false);
            Assert.True(upload.Ok, upload.Error);

            // 全局默认仓库与默认卡池中均不应包含社区 Mod 卡牌
            Assert.False(host.ActiveRepository.CardsById.ContainsKey(customCardId));
            Assert.DoesNotContain(fx.DefaultPool.AllItems, c => string.Equals(c.Id, customCardId, StringComparison.OrdinalIgnoreCase));

            // 锦标赛房间或单局按需选中该社区 Mod
            var roomCtx = host.ResolveRunModContext([modId], fx.DefaultPool, fx.DefaultMonsters, fx.DefaultArchetypes);
            Assert.NotEqual("default", roomCtx.ContextKey);
            Assert.Contains(modId, roomCtx.ActiveModIds, StringComparer.OrdinalIgnoreCase);
            Assert.True(roomCtx.Repository.CardsById.ContainsKey(customCardId));
            Assert.Contains(roomCtx.Pool.AllItems, c => string.Equals(c.Id, customCardId, StringComparison.OrdinalIgnoreCase));
            Assert.True(roomCtx.VisualBindingsByCustomId.ContainsKey(customCardId));

            // 再次解析同一组合命中缓存
            var roomCtxCached = host.ResolveRunModContext([modId], fx.DefaultPool, fx.DefaultMonsters, fx.DefaultArchetypes);
            Assert.Same(roomCtx, roomCtxCached);

            // 全局默认池仍保持零污染
            Assert.False(host.ActiveRepository.CardsById.ContainsKey(customCardId));
            Assert.DoesNotContain(fx.DefaultPool.AllItems, c => string.Equals(c.Id, customCardId, StringComparison.OrdinalIgnoreCase));

            // 规则集选中 Mod 后自动隔离镜像池，不混入 standard
            var rules = new CustomRunRuleSet { SelectedModIds = ["  " + modId + "  ", modId] }.CloneNormalized();
            Assert.Single(rules.SelectedModIds);
            Assert.Equal(modId, rules.SelectedModIds[0]);
            Assert.True(rules.IsCustomPowerModified());
            Assert.NotEqual("standard", rules.ResolveGhostPoolPartition());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RoomBundleAndAssetResolve_ShouldExposeAssetsForNonDefaultCommunityMod()
    {
        var fx = Shared.Value;
        var (tempRoot, host, store) = CreateIsolatedHostAndStore(fx);
        try
        {
            const string modId = "test-bundle-forge";
            byte[] zipBytes = BuildValidCommunityModZip(fx, modId);

            var upload = await store.UploadZipAsync(zipBytes, "acc_host", "Host", isAdmin: false);
            Assert.True(upload.Ok, upload.Error);

            var reqMods = store.DescribeRequiredMods([modId, "missing-mod-xyz"]);
            Assert.Equal(2, reqMods.Count);
            Assert.True(reqMods[0].AvailableOnServer);
            Assert.Contains(modId, reqMods[0].DownloadUrl, StringComparison.OrdinalIgnoreCase);
            Assert.False(reqMods[1].AvailableOnServer);

            object bundleDto = store.BuildSelectedBundleDto([modId]);
            string json = JsonSerializer.Serialize(bundleDto);
            using var doc = JsonDocument.Parse(json);

            var assets = doc.RootElement.GetProperty("assets");
            Assert.True(assets.GetArrayLength() > 0, "社区 Mod 房间同步包应返回卡图资产列表");

            string firstSha = assets[0].GetProperty("sha256").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(firstSha));

            // 即使该 Mod 未全服启用，ServerModHost.TryResolveAssetBySha256 也能穿透返回文件
            Assert.True(host.TryResolveAssetBySha256(firstSha, out var assetEntry));
            Assert.NotNull(assetEntry);
            Assert.True(File.Exists(assetEntry!.FullFilePath));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AdminPromoteAndDemoteServerDefault_ShouldToggleGlobalAvailability()
    {
        var fx = Shared.Value;
        var (tempRoot, host, store) = CreateIsolatedHostAndStore(fx);
        try
        {
            const string modId = "test-promote-forge";
            const string customCardId = "test-promote-forge:z_sword_custom_d83alo";
            byte[] zipBytes = BuildValidCommunityModZip(fx, modId);

            var upload = await store.UploadZipAsync(zipBytes, "acc_alice", "Alice", isAdmin: false);
            Assert.True(upload.Ok, upload.Error);
            Assert.False(host.ActiveRepository.CardsById.ContainsKey(customCardId));

            var promoted = store.SetServerDefault(modId, enabled: true);
            Assert.True(promoted.Ok, promoted.Error);
            Assert.True(promoted.Enabled);
            Assert.True(host.ActiveRepository.CardsById.ContainsKey(customCardId));
            Assert.True(Assert.Single(store.ListCatalog("acc_admin", isAdmin: true), m => m.ModId == modId).IsServerDefault);

            var demoted = store.SetServerDefault(modId, enabled: false);
            Assert.True(demoted.Ok, demoted.Error);
            Assert.False(demoted.Enabled);
            Assert.False(host.ActiveRepository.CardsById.ContainsKey(customCardId));
            Assert.False(Assert.Single(store.ListCatalog("acc_admin", isAdmin: true), m => m.ModId == modId).IsServerDefault);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TournamentLobby_ShouldPersistAndDescribeSelectedModIds()
    {
        var fx = Shared.Value;
        var (tempRoot, _, store) = CreateIsolatedHostAndStore(fx);
        try
        {
            const string modId = "test-lobby-forge";
            byte[] zipBytes = BuildValidCommunityModZip(fx, modId);
            var upload = await store.UploadZipAsync(zipBytes, "acc_host", "Host", isAdmin: false);
            Assert.True(upload.Ok, upload.Error);

            var lobbyStore = new LobbyStore();
            var hostAcc = new AccountStore.Account
            {
                AccountId = Guid.NewGuid(),
                Username = "HostPlayer",
                IdentityKey = "host_key"
            };

            var lobby = lobbyStore.Create(
                hostAcc,
                name: "带 Mod 锦标赛",
                playMode: "Tournament",
                playerCap: 8,
                isPublic: true,
                rules: new CustomRunRuleSet { SelectedModIds = [modId] });

            Assert.Single(lobby.Rules.SelectedModIds);
            Assert.Equal(modId, lobby.Rules.SelectedModIds[0]);

            var required = store.DescribeRequiredMods(lobby.Rules.SelectedModIds);
            Assert.Single(required);
            Assert.Equal(modId, required[0].ModId);
            Assert.True(required[0].AvailableOnServer);

            // 房主动态调整房间选中的 Mod
            lobbyStore.UpdateRules(lobby, new CustomRunRuleSet { SelectedModIds = [] });
            Assert.Empty(lobby.Rules.SelectedModIds);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
