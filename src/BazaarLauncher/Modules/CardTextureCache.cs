using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace BazaarLauncher.Modules;

/// <summary>
/// D3-P2-06：零 Unity 依赖的并发同键读盘合并器（In-Flight Coalescer）：
/// - 多个并发请求命中相同纹理键时，仅首个请求成为唯一读盘加载者（TryBeginOrJoin 返回 true），其余请求挂接等待队列；
/// - Complete 时统一向全部等待者分别签发独立租约（调用 leaseFactory），彻底消除启动期重复磁盘 I/O 与解码风暴。
/// </summary>
public sealed class TextureInFlightCoalescer<TLease>
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<Action<TLease>>> _waiters = new(StringComparer.OrdinalIgnoreCase);

    public bool TryBeginOrJoin(string key, Action<TLease> callback)
    {
        if (string.IsNullOrEmpty(key) || callback == null)
            return false;

        lock (_sync)
        {
            if (_waiters.TryGetValue(key, out var list))
            {
                list.Add(callback);
                return false;
            }
            _waiters[key] = new List<Action<TLease>>(2) { callback };
            return true;
        }
    }

    public void Complete(string key, Func<TLease> leaseFactory)
    {
        if (string.IsNullOrEmpty(key)) return;
        List<Action<TLease>> callbacks;
        lock (_sync)
        {
            if (!_waiters.TryGetValue(key, out callbacks))
                return;
            _waiters.Remove(key);
        }

        for (int i = 0; i < callbacks.Count; i++)
        {
            TLease lease = leaseFactory != null ? leaseFactory() : default;
            try
            {
                callbacks[i]?.Invoke(lease);
            }
            catch
            {
                if (lease is IDisposable disp)
                    try { disp.Dispose(); } catch { }
            }
        }
    }

    public void Clear()
    {
        lock (_sync) _waiters.Clear();
    }
}

/// <summary>
/// 顶层零 Unity 依赖的纹理引用计数缓存条目（可供纯 .NET 8 单元测试直接加载验证）。
/// </summary>
internal sealed class TextureRefEntry<TAsset>
{
    public string CacheKey { get; }
    public TAsset Asset { get; }
    public long EstimatedBytes { get; }
    public int RefCount;
    public long LastAccessTick;
    public bool Evicted;

    public TextureRefEntry(string cacheKey, TAsset asset, long estimatedBytes, int initialRefCount, long lastAccessTick)
    {
        CacheKey = cacheKey;
        Asset = asset;
        EstimatedBytes = estimatedBytes;
        RefCount = initialRefCount;
        LastAccessTick = lastAccessTick;
    }
}

/// <summary>
/// RAII 纹理租约句柄（P0-35 根治核心）：
/// 1. Interlocked.Exchange 保证 Dispose() 100% 单次幂等释放，绝不重复递减 RefCount；
/// 2. 正常 Dispose 立即 SuppressFinalize，零 GC Finalizer 开销；
/// 3. 异常遗忘释放时由 ~TextureLease 兜底仅在纯 C# 锁内递减 RefCount，绝不触碰任何 Unity 主线程 API。
/// </summary>
internal sealed class TextureLease<TAsset> : IDisposable
{
    private readonly TextureRefPool<TAsset> _pool;
    internal readonly TextureRefEntry<TAsset> Entry;
    private int _released;

    internal TextureLease(TextureRefPool<TAsset> pool, TextureRefEntry<TAsset> entry)
    {
        _pool = pool;
        Entry = entry;
    }

    public bool IsActive => Volatile.Read(ref _released) == 0;
    public TAsset Asset => IsActive ? Entry.Asset : default;
    public string CacheKey => Entry.CacheKey;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            GC.SuppressFinalize(this);
            _pool.ReleaseEntry(Entry, fromFinalizer: false);
        }
    }

    ~TextureLease()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _pool.ReleaseEntry(Entry, fromFinalizer: true);
        }
    }
}

/// <summary>
/// 视图代理纹理租约槽位（单所有权语义）：
/// 同一底层缓存条目再次 Acquire 传入新租约时立即 Dispose 多余租约，彻底根除重绑早退锁死 VRAM 的漏洞。
/// </summary>
internal sealed class TextureLeaseSlot<TAsset> : IDisposable
{
    private TextureLease<TAsset> _current;

    public bool HasLease => _current != null && _current.IsActive;

    public void Take(ref TextureLease<TAsset> incoming)
    {
        var next = incoming;
        incoming = null;

        if (next == null)
        {
            Clear();
            return;
        }

        if (ReferenceEquals(_current, next))
        {
            return;
        }

        if (_current != null && ReferenceEquals(_current.Entry, next.Entry))
        {
            // P0-35 根治点：同一底层缓存条目重新获取了新租约，立即归还多余的 1 次 RefCount
            next.Dispose();
            return;
        }

        var prev = _current;
        _current = next;
        prev?.Dispose();
    }

    public void Clear()
    {
        var prev = _current;
        _current = null;
        prev?.Dispose();
    }

    public void Dispose() => Clear();
}

/// <summary>
/// 顶层零 Unity 依赖的线程安全引用计数 + LRU 淘汰 + 单调世代隔离池：
/// - _sync 严格作为绝对叶子锁，锁内绝不执行外部回调、队列投递或资产销毁；
/// - DrainAll 递增 _generation，彻底拦截后台解码完成于卸载之后的跨世代 VRAM 泄漏。
/// </summary>
internal sealed class TextureRefPool<TAsset>
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TextureRefEntry<TAsset>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _totalEstimatedBytes;
    private long _accessClock;
    private long _generation;
    private long _finalizerLeakCount;

    public long Generation
    {
        get { lock (_sync) return _generation; }
    }

    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    public long TotalEstimatedBytes
    {
        get { lock (_sync) return _totalEstimatedBytes; }
    }

    public long FinalizerLeakCount
    {
        get { lock (_sync) return _finalizerLeakCount; }
    }

    public int GetRefCount(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        lock (_sync)
        {
            return _entries.TryGetValue(key, out var e) ? e.RefCount : 0;
        }
    }

    public bool TryAcquireExisting(string key, out TextureLease<TAsset> lease, out long capturedGeneration)
    {
        lock (_sync)
        {
            capturedGeneration = _generation;
            if (!string.IsNullOrEmpty(key) && _entries.TryGetValue(key, out var existing) && existing.Asset != null)
            {
                existing.RefCount++;
                existing.LastAccessTick = ++_accessClock;
                lease = new TextureLease<TAsset>(this, existing);
                return true;
            }
        }

        lease = null;
        return false;
    }

    public void RegisterOrAcquire(
        long expectedGeneration,
        string key,
        TAsset candidateAsset,
        long estimatedBytes,
        out TextureLease<TAsset> lease,
        out TAsset redundantAssetToDestroy)
    {
        redundantAssetToDestroy = default;
        lock (_sync)
        {
            if (expectedGeneration != _generation)
            {
                // 后台读盘/解码期间已发生 DrainAll 卸载：严禁入池，原样交回调用方在锁外销毁
                lease = null;
                redundantAssetToDestroy = candidateAsset;
                return;
            }

            if (_entries.TryGetValue(key, out var existing) && existing.Asset != null)
            {
                // 并发竞态命中：复用已有条目递增引用计数，冗余解码产物交回锁外销毁
                existing.RefCount++;
                existing.LastAccessTick = ++_accessClock;
                lease = new TextureLease<TAsset>(this, existing);
                redundantAssetToDestroy = candidateAsset;
                return;
            }

            var entry = new TextureRefEntry<TAsset>(key, candidateAsset, Math.Max(0L, estimatedBytes), initialRefCount: 1, ++_accessClock);
            _entries[key] = entry;
            _totalEstimatedBytes += entry.EstimatedBytes;
            lease = new TextureLease<TAsset>(this, entry);
        }
    }

    internal void ReleaseEntry(TextureRefEntry<TAsset> entry, bool fromFinalizer)
    {
        if (entry == null) return;
        lock (_sync)
        {
            // 对 EvictLru / DrainAll 后的残留旧租约仅安全递减其自身 RefCount，严禁下溢且绝不触碰 _entries
            if (entry.RefCount > 0) entry.RefCount--;
            if (fromFinalizer) _finalizerLeakCount++;
        }
    }

    public void EvictLru(long targetBudgetBytes, List<TAsset> evictedAssetsOut)
    {
        lock (_sync)
        {
            if (_totalEstimatedBytes <= targetBudgetBytes) return;

            var candidates = new List<TextureRefEntry<TAsset>>();
            foreach (var e in _entries.Values)
            {
                if (e.RefCount <= 0) candidates.Add(e);
            }
            if (candidates.Count == 0) return;

            candidates.Sort((a, b) => a.LastAccessTick.CompareTo(b.LastAccessTick));
            foreach (var c in candidates)
            {
                if (_totalEstimatedBytes <= targetBudgetBytes) break;
                _entries.Remove(c.CacheKey);
                c.Evicted = true;
                _totalEstimatedBytes = Math.Max(0L, _totalEstimatedBytes - c.EstimatedBytes);
                evictedAssetsOut?.Add(c.Asset);
            }
        }
    }

    public void DrainAll(List<TAsset> allAssetsOut)
    {
        lock (_sync)
        {
            _generation++;
            foreach (var e in _entries.Values)
            {
                e.Evicted = true;
                allAssetsOut?.Add(e.Asset);
            }
            _entries.Clear();
            _totalEstimatedBytes = 0;
        }
    }
}

/// <summary>
/// 中心化自定义 PNG 卡图解码、URP Shader 注入与显存 LRU 缓存管理器。
/// 严格执行 Wave 1 审计第四章全部 6 道防白块/防粉块/防穿透/防泄漏铁律：
///   铁律一：保留原版 URP 材质 Shader + 属性自省缓存 + 次级视差槽清零（绑定共享 Transparent1x1）+ 可选关 _ISPREMIUM_ON；
///   铁律二：后台读盘 + PNG/JPEG 文件头魔数校验 + 大小上限 8MB + 主线程 sRGB(linear=false) 解码 + 严格校验 LoadImage 返回值与尺寸 > 8px；
///   铁律三：长边 > 1024px 时在 GPU RenderTexture(sRGB) 双线性缩放 + Clamp/Trilinear/Aniso4 + Apply(true, makeNoLongerReadable:true)；
///   铁律四：与 CardVisualOverrideAgent 配合维护 Epoch 版本号防对象池异步竞态；
///   铁律五：仅写实例化材质，严禁污染 AssetLoader 共享材质；
///   铁律六：基于 RAII TextureLease + TextureRefPool + 单调世代隔离与 LRU 显存上限（默认 128MB），锁外销毁并彻底杜绝 VRAM 泄漏。
/// </summary>
internal sealed class CardTextureCache
{
    private const long MaxImageFileBytes = 8L * 1024 * 1024;
    private const int MaxTextureEdgePx = 1024;
    private const long DefaultVramBudgetBytes = 128L * 1024 * 1024;

    internal sealed class CacheEntry
    {
        public string CacheKey;
        public Texture2D Texture;
        public Sprite DerivedSprite;
        public long EstimatedVramBytes;
    }

    internal sealed class ShaderSlotProfile
    {
        public int PrimaryTexturePropId = -1;
        public int[] SecondaryParallaxPropIds = Array.Empty<int>();
    }

    internal readonly struct CacheStats
    {
        public readonly int Count;
        public readonly long EstimatedVramBytes;
        public CacheStats(int count, long bytes) { Count = count; EstimatedVramBytes = bytes; }
    }

    internal static CardTextureCache Instance { get; } = new();

    private readonly TextureRefPool<CacheEntry> _pool = new();
    private readonly object _shaderSync = new();
    private readonly Dictionary<int, ShaderSlotProfile> _shaderProfiles = new();
    private Texture2D _transparent1x1;
    private Shader _cachedSafeUnlitShader;
    private readonly TextureInFlightCoalescer<TextureLease<CacheEntry>> _inFlight = new();

    internal long VramBudgetBytes { get; set; } = DefaultVramBudgetBytes;

    internal CacheStats GetStats()
    {
        return new CacheStats(_pool.Count, _pool.TotalEstimatedBytes);
    }

    internal Texture2D GetTransparent1x1()
    {
        if (_transparent1x1 == null)
        {
            _transparent1x1 = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                name = "BazaarModKit_Transparent1x1",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
            };
            _transparent1x1.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
            _transparent1x1.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        }
        return _transparent1x1;
    }

    /// <summary>
    /// 异步读盘校验魔数 -> 主线程 sRGB 安全解码并签发 RAII TextureLease。
    /// 调用方须通过 TextureLeaseSlot.Take(ref lease) 接管所有权，或在 finally 中 lease?.Dispose() 归还计数。
    /// </summary>
    internal void AcquireTextureAsync(string localPath, string sha256Hint, Action<TextureLease<CacheEntry>> onReadyOnMain)
    {
        if (string.IsNullOrWhiteSpace(localPath) || onReadyOnMain == null)
        {
            return;
        }

        string key = BuildCacheKey(localPath, sha256Hint);
        if (_pool.TryAcquireExisting(key, out var hitLease, out long capturedGen))
        {
            // 在 _pool 叶子锁之外投递主线程，彻底消除跨锁嵌套
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_pool.Generation != capturedGen || hitLease.Asset == null || hitLease.Asset.Texture == null)
                {
                    hitLease.Dispose();
                    onReadyOnMain(null);
                    return;
                }
                InvokeLeaseCallbackSafely(hitLease, onReadyOnMain);
            });
            return;
        }

        // D3-P2-06：同键并发读盘合并，避免多卡共享同贴图时同时触发多次重复文件读取与解码
        if (!_inFlight.TryBeginOrJoin(key, onReadyOnMain))
            return;

        _ = Task.Run(() =>
        {
            byte[] bytes = null;
            try
            {
                bytes = TryReadAndValidateImageBytes(localPath);
            }
            catch (Exception ex)
            {
                DiagLog.SwallowOnce("CardTextureCache.ReadBytes", ex);
            }

            if (bytes == null)
            {
                BareClientAuditRuntime.NotifyCardArtLoadFailure("CardTextureCache", localPath, "read_or_magic_validation_failed");
                MainThreadDispatcher.Enqueue(() => _inFlight.Complete(key, static () => null));
                return;
            }

            MainThreadDispatcher.Enqueue(() =>
            {
                TextureLease<CacheEntry> firstLease = null;
                bool usedFirst = false;
                try
                {
                    firstLease = DecodeAndRegisterOnMain(capturedGen, key, bytes, localPath);
                    if (firstLease == null)
                    {
                        BareClientAuditRuntime.NotifyCardArtLoadFailure("CardTextureCache", localPath, "decode_or_register_returned_null");
                    }
                    _inFlight.Complete(key, () =>
                    {
                        if (!usedFirst)
                        {
                            usedFirst = true;
                            return firstLease;
                        }
                        if (firstLease == null) return null;
                        return _pool.TryAcquireExisting(key, out var extraLease, out long gen) && gen == capturedGen
                            ? extraLease
                            : null;
                    });
                }
                catch (Exception ex)
                {
                    BareClientAuditRuntime.NotifyCardArtLoadFailure("CardTextureCache", localPath, "decode_exception:" + ex.GetType().Name);
                    DiagLog.SwallowOnce("CardTextureCache.DecodeOnMain", ex);
                    _inFlight.Complete(key, static () => null);
                }
                finally
                {
                    if (!usedFirst && firstLease != null)
                        firstLease.Dispose();
                }
            });
        });
    }

    private static void InvokeLeaseCallbackSafely(TextureLease<CacheEntry> lease, Action<TextureLease<CacheEntry>> callback)
    {
        try
        {
            callback(lease);
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    /// <summary>获取或按需派生对应租约缓存项的 2D Sprite（供 EncounterController / CardPreviewEncounter 使用）。</summary>
    internal Sprite GetOrCreateSpriteOnMain(TextureLease<CacheEntry> lease)
    {
        if (lease == null || !lease.IsActive) return null;
        return GetOrCreateSpriteOnMain(lease.Asset);
    }

    /// <summary>获取或按需派生对应缓存项的 2D Sprite。</summary>
    internal Sprite GetOrCreateSpriteOnMain(CacheEntry entry)
    {
        if (entry == null || entry.Texture == null) return null;
        if (entry.DerivedSprite != null) return entry.DerivedSprite;

        try
        {
            var tex = entry.Texture;
            entry.DerivedSprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                100f);
            // D3-P1-09：防止切场景 Resources.UnloadUnusedAssets 意外回收派生 Sprite 导致奇遇/2D预览白块
            entry.DerivedSprite.hideFlags = HideFlags.HideAndDontSave;
            entry.DerivedSprite.name = "ModCardSprite_" + entry.CacheKey.Substring(0, Math.Min(8, entry.CacheKey.Length));
            return entry.DerivedSprite;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>切主大厅时仅驱逐 RefCount == 0 的冷纹理，绝不造成野指针白块（铁律六）。</summary>
    internal void EvictUnreferencedOnSceneChange()
    {
        MainThreadDispatcher.Enqueue(() => EvictLruOnMain(VramBudgetBytes / 2));
    }

    /// <summary>热更新清理全部未引用缓存纹理。</summary>
    internal void ClearUnreferencedOnMain()
    {
        MainThreadDispatcher.Enqueue(() => EvictLruOnMain(0));
    }

    /// <summary>销毁全部纹理资源并递增世代号拦截在途解码（插件卸载时调用）。</summary>
    internal void DisposeAllOnMain()
    {
        _inFlight.Clear();
        var all = new List<CacheEntry>();
        _pool.DrainAll(all);

        foreach (var e in all)
        {
            DestroyEntryAssets(e);
        }
        if (_transparent1x1 != null)
        {
            UnityEngine.Object.Destroy(_transparent1x1);
            _transparent1x1 = null;
        }
    }

    internal sealed class MaterialTextureSnapshot
    {
        public int MaterialInstanceId;
        public Shader OriginalShader;
        public int PrimaryPropId = -1;
        public Texture OriginalPrimaryTex;
        public (int PropId, Texture OriginalTex)[] SecondaryOriginalTexs = Array.Empty<(int, Texture)>();
        public bool HadPremiumKeyword;
    }

    /// <summary>在首次覆写某张卡牌的实例化材质前抓取原版贴图快照，供禁用 Mod/切服/池化回退时无损恢复原版借壳贴图。</summary>
    internal MaterialTextureSnapshot CaptureOriginalMaterialState(Material instanceMaterial)
    {
        if (instanceMaterial == null || instanceMaterial.shader == null) return null;
        try
        {
            Shader shader = instanceMaterial.shader;
            ShaderSlotProfile profile = GetOrBuildShaderProfile(shader);
            var snap = new MaterialTextureSnapshot
            {
                MaterialInstanceId = instanceMaterial.GetInstanceID(),
                OriginalShader = shader,
                PrimaryPropId = profile.PrimaryTexturePropId,
                OriginalPrimaryTex = profile.PrimaryTexturePropId >= 0 ? instanceMaterial.GetTexture(profile.PrimaryTexturePropId) : null,
                HadPremiumKeyword = instanceMaterial.IsKeywordEnabled("_ISPREMIUM_ON"),
            };

            if (profile.SecondaryParallaxPropIds.Length > 0)
            {
                var arr = new (int PropId, Texture OriginalTex)[profile.SecondaryParallaxPropIds.Length];
                for (int i = 0; i < profile.SecondaryParallaxPropIds.Length; i++)
                {
                    int pid = profile.SecondaryParallaxPropIds[i];
                    arr[i] = (pid, instanceMaterial.GetTexture(pid));
                }
                snap.SecondaryOriginalTexs = arr;
            }
            return snap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>将之前覆写过自定义 PNG 的实例材质无损还原回原版贴图与关键字，彻底杜绝自定义卡与原版卡串图。</summary>
    internal bool RestoreOriginalMaterialState(Material instanceMaterial, MaterialTextureSnapshot snapshot)
    {
        if (instanceMaterial == null || snapshot == null) return false;
        if (instanceMaterial.GetInstanceID() != snapshot.MaterialInstanceId) return false;
        try
        {
            if (snapshot.OriginalShader != null && instanceMaterial.shader != snapshot.OriginalShader)
            {
                instanceMaterial.shader = snapshot.OriginalShader;
            }
            if (snapshot.PrimaryPropId >= 0 && instanceMaterial.HasProperty(snapshot.PrimaryPropId))
            {
                instanceMaterial.SetTexture(snapshot.PrimaryPropId, snapshot.OriginalPrimaryTex);
            }
            if (snapshot.SecondaryOriginalTexs != null)
            {
                for (int i = 0; i < snapshot.SecondaryOriginalTexs.Length; i++)
                {
                    var (pid, tex) = snapshot.SecondaryOriginalTexs[i];
                    if (pid >= 0 && instanceMaterial.HasProperty(pid))
                    {
                        instanceMaterial.SetTexture(pid, tex);
                    }
                }
            }
            if (snapshot.HadPremiumKeyword)
            {
                try { instanceMaterial.EnableKeyword("_ISPREMIUM_ON"); } catch { }
            }
            else
            {
                try { instanceMaterial.DisableKeyword("_ISPREMIUM_ON"); } catch { }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 铁律一：对原版 URP 实例材质执行 Shader 属性自省与安全贴图注入（防粉块 &amp; 防原版视差前景穿透）。
    /// </summary>
    internal bool ApplyTextureToUrpMaterial(Material instanceMaterial, Texture2D customTex, bool disableParallaxPremium)
    {
        if (instanceMaterial == null || customTex == null || instanceMaterial.shader == null)
        {
            return false;
        }

        Shader shader = instanceMaterial.shader;
        ShaderSlotProfile profile = GetOrBuildShaderProfile(shader);

        if (profile.PrimaryTexturePropId >= 0)
        {
            instanceMaterial.SetTexture(profile.PrimaryTexturePropId, customTex);

            Texture2D clear1x1 = GetTransparent1x1();
            for (int i = 0; i < profile.SecondaryParallaxPropIds.Length; i++)
            {
                instanceMaterial.SetTexture(profile.SecondaryParallaxPropIds[i], clear1x1);
            }

            if (disableParallaxPremium)
            {
                try { instanceMaterial.DisableKeyword("_ISPREMIUM_ON"); } catch { }
            }
            return true;
        }

        Shader safeShader = ResolveSafeUrpShader();
        if (safeShader != null)
        {
            instanceMaterial.shader = safeShader;
            if (instanceMaterial.HasProperty("_BaseMap")) instanceMaterial.SetTexture("_BaseMap", customTex);
            else if (instanceMaterial.HasProperty("_MainTex")) instanceMaterial.SetTexture("_MainTex", customTex);
            return true;
        }

        return false;
    }

    private ShaderSlotProfile GetOrBuildShaderProfile(Shader shader)
    {
        int id = shader.GetInstanceID();
        lock (_shaderSync)
        {
            if (_shaderProfiles.TryGetValue(id, out var cached)) return cached;
        }

        var profile = new ShaderSlotProfile();
        var secondaryList = new List<int>();
        int propCount = shader.GetPropertyCount();

        int bestPrimaryScore = -1;
        int bestPrimaryId = -1;
        string bestPrimaryName = null;

        for (int i = 0; i < propCount; i++)
        {
            if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
            string propName = shader.GetPropertyName(i) ?? string.Empty;
            int propId = shader.GetPropertyNameId(i);

            if (IsUtilityTextureSlot(propName)) continue;

            int score = ScorePrimaryArtSlot(propName);
            if (score > bestPrimaryScore)
            {
                if (bestPrimaryId >= 0 && !string.IsNullOrEmpty(bestPrimaryName) && IsParallaxLayerSlot(bestPrimaryName))
                {
                    secondaryList.Add(bestPrimaryId);
                }
                bestPrimaryScore = score;
                bestPrimaryId = propId;
                bestPrimaryName = propName;
            }
            else if (IsParallaxLayerSlot(propName))
            {
                secondaryList.Add(propId);
            }
        }

        profile.PrimaryTexturePropId = bestPrimaryId;
        profile.SecondaryParallaxPropIds = secondaryList.ToArray();

        lock (_shaderSync)
        {
            _shaderProfiles[id] = profile;
        }
        return profile;
    }

    private static int ScorePrimaryArtSlot(string name)
    {
        if (string.Equals(name, "_BaseMap", StringComparison.OrdinalIgnoreCase)) return 100;
        if (string.Equals(name, "_MainTex", StringComparison.OrdinalIgnoreCase)) return 95;
        if (name.IndexOf("CardArt", StringComparison.OrdinalIgnoreCase) >= 0) return 90;
        if (name.IndexOf("Background", StringComparison.OrdinalIgnoreCase) >= 0) return 80;
        if (name.IndexOf("Illustration", StringComparison.OrdinalIgnoreCase) >= 0) return 75;
        if (name.IndexOf("Albedo", StringComparison.OrdinalIgnoreCase) >= 0) return 70;
        if (IsParallaxLayerSlot(name)) return 20;
        return 40;
    }

    private static bool IsParallaxLayerSlot(string name)
    {
        return name.IndexOf("Foreground", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Midground", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Parallax", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Layer1", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Layer2", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Layer3", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Character", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsUtilityTextureSlot(string name)
    {
        return name.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Noise", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Enchant", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Foil", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Dissolve", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Sparkle", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Ramp", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Lut", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private Shader ResolveSafeUrpShader()
    {
        if (_cachedSafeUnlitShader != null) return _cachedSafeUnlitShader;
        _cachedSafeUnlitShader =
            Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Sprites/Default")
            ?? Shader.Find("UI/Default");
        return _cachedSafeUnlitShader;
    }

    /// <summary>铁律二：后台读盘并严格验证 PNG/JPEG 文件魔数与文件大小上限（<= 8MB）。</summary>
    private static byte[] TryReadAndValidateImageBytes(string localPath)
    {
        try
        {
            var fi = new FileInfo(localPath);
            if (!fi.Exists || fi.Length < 16 || fi.Length > MaxImageFileBytes)
            {
                BareClientAuditRuntime.NotifyCardArtLoadFailure(
                    "CardTextureCache",
                    localPath,
                    !fi.Exists ? "file_not_found" : $"file_size_out_of_range({fi.Length})");
                return null;
            }

            byte[] bytes = File.ReadAllBytes(localPath);
            if (!IsSupportedImageMagic(bytes))
            {
                if (IsWebpMagic(bytes))
                {
                    BareClientAuditRuntime.NotifyCardArtLoadFailure("CardTextureCache", localPath, "unsupported_webp_format");
                    BazaarLauncherPlugin.Log?.LogWarning($"[CardTextureCache] 跳过不支持的 WebP 资产（Unity 运行期不支持 WebP 解码，请将 Mod 资产转换为 PNG 或 JPEG）：{localPath}");
                }
                else
                {
                    BareClientAuditRuntime.NotifyCardArtLoadFailure("CardTextureCache", localPath, "invalid_image_magic");
                    BazaarLauncherPlugin.Log?.LogWarning($"[CardTextureCache] 跳过非合法 PNG/JPEG 文件: {localPath}");
                }
                return null;
            }
            return bytes;
        }
        catch (Exception ex)
        {
            BareClientAuditRuntime.NotifyCardArtLoadFailure("CardTextureCache", localPath, "read_exception:" + ex.GetType().Name);
            return null;
        }
    }

    internal static bool IsSupportedImageMagic(byte[] b)
    {
        if (b == null || b.Length < 12) return false;
        bool isPng = b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
                  && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;
        if (isPng) return true;
        bool isJpeg = b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
        return isJpeg;
    }

    /// <summary>P0-28 方案 A：识别 RIFF....WEBP 魔数以输出清晰告警，优雅回落原版图不生白块。</summary>
    internal static bool IsWebpMagic(byte[] b)
    {
        if (b == null || b.Length < 12) return false;
        return b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';
    }

    private TextureLease<CacheEntry> DecodeAndRegisterOnMain(long expectedGeneration, string key, byte[] bytes, string localPath = null)
    {
        if (_pool.Generation != expectedGeneration)
        {
            return null;
        }

        if (_pool.TryAcquireExisting(key, out var raceHitLease, out long currentGen))
        {
            if (currentGen == expectedGeneration && raceHitLease.Asset != null && raceHitLease.Asset.Texture != null)
            {
                return raceHitLease;
            }
            raceHitLease.Dispose();
            return null;
        }

        // 铁律二：强制 sRGB (linear: false) 防止 URP Linear 管线过曝发白
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true, linear: false)
        {
            name = "ModCardTex_" + key.Substring(0, Math.Min(8, key.Length)),
            hideFlags = HideFlags.HideAndDontSave,
        };

        try
        {
            bool loaded = ImageConversion.LoadImage(tex, bytes, markNonReadable: false);
            if (!loaded || tex.width <= 8 || tex.height <= 8)
            {
                BareClientAuditRuntime.NotifyCardArtLoadFailure(
                    "CardTextureCache",
                    localPath ?? key,
                    !loaded ? "unity_load_image_false" : $"decoded_dimensions_too_small({tex.width}x{tex.height})");
                UnityEngine.Object.Destroy(tex);
                return null;
            }

            if (tex.width > MaxTextureEdgePx || tex.height > MaxTextureEdgePx)
            {
                Texture2D resized = DownscaleTextureOnGpuSrgb(tex, MaxTextureEdgePx);
                UnityEngine.Object.Destroy(tex);
                tex = resized;
                if (tex == null) return null;
            }

            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);

            long estVram = (long)(tex.width * (long)tex.height * 4L * 1.33);
            var candidate = new CacheEntry
            {
                CacheKey = key,
                Texture = tex,
                EstimatedVramBytes = estVram,
            };

            _pool.RegisterOrAcquire(expectedGeneration, key, candidate, estVram, out var lease, out var redundant);
            if (redundant != null)
            {
                DestroyEntryAssets(redundant);
            }

            if (lease != null && _pool.TotalEstimatedBytes > VramBudgetBytes)
            {
                EvictLruOnMain(VramBudgetBytes);
            }

            return lease;
        }
        catch (Exception ex)
        {
            if (tex != null) UnityEngine.Object.Destroy(tex);
            BazaarLauncherPlugin.Log?.LogWarning("[CardTextureCache] 解码 PNG 失败: " + ex.Message);
            return null;
        }
    }

    private static Texture2D DownscaleTextureOnGpuSrgb(Texture2D src, int maxEdge)
    {
        float scale = Math.Min((float)maxEdge / src.width, (float)maxEdge / src.height);
        int dstW = Math.Max(16, Mathf.RoundToInt(src.width * scale));
        int dstH = Math.Max(16, Mathf.RoundToInt(src.height * scale));

        RenderTexture rt = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture prev = RenderTexture.active;
        try
        {
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var dst = new Texture2D(dstW, dstH, TextureFormat.RGBA32, mipChain: true, linear: false)
            {
                name = src.name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            dst.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
            return dst;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    private void EvictLruOnMain(long targetBudgetBytes)
    {
        var toDestroy = new List<CacheEntry>();
        _pool.EvictLru(targetBudgetBytes, toDestroy);

        foreach (var e in toDestroy)
        {
            DestroyEntryAssets(e);
        }
    }

    private static void DestroyEntryAssets(CacheEntry entry)
    {
        if (entry == null) return;
        try
        {
            if (entry.DerivedSprite != null)
            {
                UnityEngine.Object.Destroy(entry.DerivedSprite);
                entry.DerivedSprite = null;
            }
            if (entry.Texture != null)
            {
                UnityEngine.Object.Destroy(entry.Texture);
                entry.Texture = null;
            }
        }
        catch { }
    }

    private static string BuildCacheKey(string localPath, string shaHint)
    {
        if (!string.IsNullOrWhiteSpace(shaHint))
        {
            return shaHint.Trim().ToLowerInvariant();
        }
        try
        {
            long ticks = File.Exists(localPath) ? File.GetLastWriteTimeUtc(localPath).Ticks : 0L;
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(localPath.ToLowerInvariant() + "|" + ticks));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }
        catch
        {
            return localPath.ToLowerInvariant();
        }
    }
}
