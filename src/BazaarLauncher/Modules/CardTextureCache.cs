using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace BazaarLauncher.Modules;

/// <summary>
/// 中心化自定义 PNG 卡图解码、URP Shader 注入与显存 LRU 缓存管理器。
/// 严格执行 Wave 1 审计第四章全部 6 道防白块/防粉块/防穿透/防泄漏铁律：
///   铁律一：保留原版 URP 材质 Shader + 属性自省缓存 + 次级视差槽清零（绑定共享 Transparent1x1）+ 可选关 _ISPREMIUM_ON；
///   铁律二：后台读盘 + PNG/JPEG 文件头魔数校验 + 大小上限 8MB + 主线程 sRGB(linear=false) 解码 + 严格校验 LoadImage 返回值与尺寸 > 8px；
///   铁律三：长边 > 1024px 时在 GPU RenderTexture(sRGB) 双线性缩放 + Clamp/Trilinear/Aniso4 + Apply(true, makeNoLongerReadable:true)；
///   铁律四：与 CardVisualOverrideAgent 配合维护 Epoch 版本号防对象池异步竞态；
///   铁律五：仅写实例化材质，严禁污染 AssetLoader 共享材质；
///   铁律六：按 SHA-256（或路径+时间戳）去重 + RefCount 引用计数 + LRU 显存上限（默认 128MB），仅驱逐 RefCount==0 纹理并销毁派生 Sprite。
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
        public int RefCount;
        public long LastAccessTick;
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

    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ShaderSlotProfile> _shaderProfiles = new();
    private long _totalEstimatedVramBytes;
    private long _accessClock;
    private Texture2D _transparent1x1;
    private Shader _cachedSafeUnlitShader;

    internal long VramBudgetBytes { get; set; } = DefaultVramBudgetBytes;

    internal CacheStats GetStats()
    {
        lock (_sync)
        {
            return new CacheStats(_entries.Count, _totalEstimatedVramBytes);
        }
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
    /// 异步读盘校验魔数 -> 主线程 sRGB 安全解码并增加引用计数。失败时返回 null（上层自动回退原版底座，0 白块 0 红问号）。
    /// </summary>
    internal void AcquireTextureAsync(string localPath, string sha256Hint, Action<CacheEntry> onReadyOnMain)
    {
        if (string.IsNullOrWhiteSpace(localPath) || onReadyOnMain == null)
        {
            return;
        }

        string key = BuildCacheKey(localPath, sha256Hint);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing) && existing.Texture != null)
            {
                existing.RefCount++;
                existing.LastAccessTick = ++_accessClock;
                MainThreadDispatcher.Enqueue(() => onReadyOnMain(existing));
                return;
            }
        }

        _ = Task.Run(() =>
        {
            byte[] bytes = TryReadAndValidateImageBytes(localPath);
            if (bytes == null)
            {
                MainThreadDispatcher.Enqueue(() => onReadyOnMain(null));
                return;
            }

            MainThreadDispatcher.Enqueue(() =>
            {
                var entry = DecodeAndRegisterOnMain(key, bytes);
                onReadyOnMain(entry);
            });
        });
    }

    /// <summary>获取或按需派生对应缓存项的 2D Sprite（供 EncounterController / CardPreviewEncounter 使用）。</summary>
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
            entry.DerivedSprite.name = "ModCardSprite_" + entry.CacheKey.Substring(0, Math.Min(8, entry.CacheKey.Length));
            return entry.DerivedSprite;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>递减纹理引用计数（视图销毁或对象池换卡时调用）。</summary>
    internal void Release(CacheEntry entry)
    {
        if (entry == null) return;
        lock (_sync)
        {
            if (entry.RefCount > 0) entry.RefCount--;
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

    /// <summary>销毁全部纹理资源（插件卸载时调用）。</summary>
    internal void DisposeAllOnMain()
    {
        List<CacheEntry> all;
        lock (_sync)
        {
            all = new List<CacheEntry>(_entries.Values);
            _entries.Clear();
            _totalEstimatedVramBytes = 0;
        }

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

        // 兜底降级：仅当 Shader 没有任何合规 2D 主画槽位时才回退到安全 URP Unlit Shader（严禁使用 Standard 变洋红粉块）
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
        lock (_sync)
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
                // 修正：判断被替换下来的旧 bestPrimaryName 是否为视差层，防止前景槽位排在主贴图之前时漏清零
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

        lock (_sync)
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
                return null;
            }

            byte[] bytes = File.ReadAllBytes(localPath);
            if (!IsSupportedImageMagic(bytes))
            {
                BazaarLauncherPlugin.Log?.LogWarning($"[CardTextureCache] 跳过非合法 PNG/JPEG 文件: {localPath}");
                return null;
            }
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportedImageMagic(byte[] b)
    {
        if (b == null || b.Length < 12) return false;
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        bool isPng = b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
                  && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;
        if (isPng) return true;
        // JPEG: FF D8 FF
        bool isJpeg = b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
        return isJpeg;
    }

    private CacheEntry DecodeAndRegisterOnMain(string key, byte[] bytes)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var raceHit) && raceHit.Texture != null)
            {
                raceHit.RefCount++;
                raceHit.LastAccessTick = ++_accessClock;
                return raceHit;
            }
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
                // 铁律二：LoadImage 失败时 Unity 会生成 8x8 红问号，必须立即销毁并回退原版底座
                UnityEngine.Object.Destroy(tex);
                return null;
            }

            // 铁律三：长边 > 1024px 时在 GPU sRGB RenderTexture 缩放，防 4K 巨图撑爆显存
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
            // 铁律三：上传 GPU 并立即释放 CPU 端系统内存拷贝（立省 50% 内存）
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);

            long estVram = (long)(tex.width * (long)tex.height * 4L * 1.33);
            var entry = new CacheEntry
            {
                CacheKey = key,
                Texture = tex,
                EstimatedVramBytes = estVram,
                RefCount = 1,
                LastAccessTick = ++_accessClock,
            };

            lock (_sync)
            {
                _entries[key] = entry;
                _totalEstimatedVramBytes += estVram;
            }

            if (_totalEstimatedVramBytes > VramBudgetBytes)
            {
                EvictLruOnMain(VramBudgetBytes);
            }

            return entry;
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
        List<CacheEntry> toDestroy = null;
        lock (_sync)
        {
            if (_totalEstimatedVramBytes <= targetBudgetBytes) return;

            var candidates = new List<CacheEntry>();
            foreach (var e in _entries.Values)
            {
                if (e.RefCount <= 0) candidates.Add(e);
            }
            if (candidates.Count == 0) return;

            candidates.Sort((a, b) => a.LastAccessTick.CompareTo(b.LastAccessTick));
            toDestroy = new List<CacheEntry>();

            foreach (var c in candidates)
            {
                if (_totalEstimatedVramBytes <= targetBudgetBytes) break;
                _entries.Remove(c.CacheKey);
                _totalEstimatedVramBytes = Math.Max(0, _totalEstimatedVramBytes - c.EstimatedVramBytes);
                toDestroy.Add(c);
            }
        }

        if (toDestroy != null)
        {
            foreach (var e in toDestroy)
            {
                DestroyEntryAssets(e);
            }
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
