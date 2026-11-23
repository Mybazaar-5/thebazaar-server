using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BazaarModSdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarLauncher.Modules;

/// <summary>
/// 原生 3D World-Space 通用自定义机制/数值徽章渲染系统（彻底废除穿透 ESC 菜单的 OnGUI 黑框）：
///   - 直接挂载在卡牌自身的 World-Space Canvas（与原版 AmmoController / GemController 同层级同相机同渲染层），
///     天然随 3D 卡牌位移、悬停、倾斜、放大、翻面，并被 ESC 暂停菜单、设置弹窗、卡牌悬浮提示自然遮挡；
///   - 精绘古铜鎏金浮雕底板、晶球槽（pips）、鎏金进度槽（bar）、切面宝石（gem）与通用格式徽章（pill）4 种原生融合视觉；
///   - 完全面向任意玩家自定义机制开放（叠层/过载/剑意/充能/百分比/秒数/倍率/状态值），支持对象池回收零残留。
/// </summary>
internal static class CardBadgeArtLibrary
{
    private static Sprite _plaqueSprite;
    private static Sprite _plaqueReadySprite;
    private static Sprite _orbEmptySprite;
    private static Sprite _orbFilledSprite;
    private static Sprite _gemSprite;
    private static Sprite _barTrackSprite;
    private static Sprite _barFillSprite;

    internal static Sprite GetPlaqueSprite(bool ready)
    {
        EnsureSprites();
        return ready ? _plaqueReadySprite : _plaqueSprite;
    }

    internal static Sprite GetOrbEmptySprite()
    {
        EnsureSprites();
        return _orbEmptySprite;
    }

    internal static Sprite GetOrbFilledSprite()
    {
        EnsureSprites();
        return _orbFilledSprite;
    }

    internal static Sprite GetGemSprite()
    {
        EnsureSprites();
        return _gemSprite;
    }

    internal static Sprite GetBarTrackSprite()
    {
        EnsureSprites();
        return _barTrackSprite;
    }

    internal static Sprite GetBarFillSprite()
    {
        EnsureSprites();
        return _barFillSprite;
    }

    private static void EnsureSprites()
    {
        if (_plaqueSprite != null) return;
        _plaqueSprite = BuildPlaqueSprite(ready: false);
        _plaqueReadySprite = BuildPlaqueSprite(ready: true);
        _orbEmptySprite = BuildOrbSprite(filled: false);
        _orbFilledSprite = BuildOrbSprite(filled: true);
        _gemSprite = BuildGemSprite();
        _barTrackSprite = BuildBarSprite(isFill: false);
        _barFillSprite = BuildBarSprite(isFill: true);
    }

    /// <summary>生成 9-slice 雕花古铜鎏金内嵌牌匾（带柔和投影、顶边鎏金高光与深曜石内凹底面）。</summary>
    private static Sprite BuildPlaqueSprite(bool ready)
    {
        const int w = 64;
        const int h = 64;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: false)
        {
            name = ready ? "BazaarBadge_PlaqueReady" : "BazaarBadge_Plaque",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        var pixels = new Color[w * h];
        float pad = 4.5f;
        float halfW = (w * 0.5f) - pad;
        float halfH = (h * 0.5f) - pad;
        float radius = 12f;

        Color rimTop = ready ? new Color(1.00f, 0.89f, 0.45f, 1f) : new Color(0.86f, 0.69f, 0.36f, 0.98f);
        Color rimMid = ready ? new Color(0.95f, 0.72f, 0.22f, 1f) : new Color(0.64f, 0.46f, 0.21f, 0.98f);
        Color rimBot = ready ? new Color(0.72f, 0.46f, 0.12f, 1f) : new Color(0.39f, 0.26f, 0.12f, 0.98f);

        Color fillTop = ready ? new Color(0.24f, 0.14f, 0.05f, 0.94f) : new Color(0.14f, 0.11f, 0.12f, 0.93f);
        Color fillBot = ready ? new Color(0.14f, 0.07f, 0.03f, 0.96f) : new Color(0.07f, 0.06f, 0.08f, 0.95f);

        for (int y = 0; y < h; y++)
        {
            float vy = (float)y / (h - 1); // 0=底, 1=顶
            for (int x = 0; x < w; x++)
            {
                float px = x - (w - 1) * 0.5f;
                float py = y - (h - 1) * 0.5f;
                float sdf = RoundedBoxSdf(px, py, halfW, halfH, radius);

                // 外投影（略向下偏移）
                float shadowSdf = RoundedBoxSdf(px, py + 1.8f, halfW + 1.2f, halfH + 1.2f, radius + 1.2f);
                float shadowAlpha = Mathf.Clamp01(1f - Mathf.Clamp01((shadowSdf + 0.5f) / 4.0f)) * 0.52f;
                Color outCol = new Color(0.03f, 0.02f, 0.02f, shadowAlpha);

                if (sdf <= 1.2f)
                {
                    float bodyMask = 1f - SmoothStep(-0.8f, 0.9f, sdf);
                    // 3px 立体鎏金浮雕外框
                    Color rimCol = vy > 0.5f
                        ? Color.Lerp(rimMid, rimTop, (vy - 0.5f) * 2f)
                        : Color.Lerp(rimBot, rimMid, vy * 2f);

                    Color innerCol = Color.Lerp(fillBot, fillTop, vy);
                    // 顶部细腻高光与底部暗槽
                    if (sdf < -2.6f && sdf > -4.2f)
                    {
                        float groove = 1f - Mathf.Abs(sdf + 3.4f) / 0.8f;
                        innerCol = Color.Lerp(innerCol, new Color(0.02f, 0.015f, 0.02f, innerCol.a), groove * 0.65f);
                    }
                    else if (sdf <= -4.2f && vy > 0.72f)
                    {
                        float sheen = (vy - 0.72f) / 0.28f;
                        innerCol = Color.Lerp(innerCol, new Color(0.95f, 0.80f, 0.48f, innerCol.a), sheen * (ready ? 0.20f : 0.08f));
                    }

                    float rimMix = SmoothStep(-3.2f, -2.1f, sdf);
                    Color surface = Color.Lerp(innerCol, rimCol, rimMix);
                    outCol = AlphaBlend(outCol, new Color(surface.r, surface.g, surface.b, surface.a * bodyMask));
                }

                pixels[y * w + x] = outCol;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(18f, 18f, 18f, 18f));
    }

    /// <summary>生成晶球槽（空槽为内凹古铜底座，满槽为可染色的立体发光晶球，带左上高光点）。</summary>
    private static Sprite BuildOrbSprite(bool filled)
    {
        const int sz = 48;
        var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, mipChain: false, linear: false)
        {
            name = filled ? "BazaarBadge_OrbFilled" : "BazaarBadge_OrbEmpty",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        var pixels = new Color[sz * sz];
        float c = (sz - 1) * 0.5f;
        float outerR = sz * 0.43f;
        float innerR = sz * 0.33f;

        for (int y = 0; y < sz; y++)
        {
            for (int x = 0; x < sz; x++)
            {
                float dx = x - c;
                float dy = y - c;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float vy = (dy / outerR) * 0.5f + 0.5f;

                if (dist > outerR + 1.5f)
                {
                    pixels[y * sz + x] = Color.clear;
                    continue;
                }

                float outerMask = 1f - SmoothStep(outerR - 0.8f, outerR + 0.8f, dist);
                Color col;

                if (!filled)
                {
                    // 空晶槽：古铜镶边 + 深邃内凹暗孔
                    Color ringCol = Color.Lerp(new Color(0.36f, 0.25f, 0.13f, 0.95f), new Color(0.76f, 0.58f, 0.30f, 0.95f), Mathf.Clamp01(vy));
                    float holeT = Mathf.Clamp01(dist / innerR);
                    Color holeCol = Color.Lerp(new Color(0.05f, 0.04f, 0.06f, 0.94f), new Color(0.14f, 0.11f, 0.13f, 0.94f), holeT * holeT);
                    float ringMix = SmoothStep(innerR - 0.9f, innerR + 0.7f, dist);
                    col = Color.Lerp(holeCol, ringCol, ringMix);
                }
                else
                {
                    // 实心发光晶球（以纯白为基底，运行时由 Image.color 精准着色至对应机制主题色）
                    float norm = Mathf.Clamp01(dist / innerR);
                    // 球面径向明暗 + 中心灼亮核
                    float core = Mathf.Pow(1f - norm, 0.55f);
                    float shade = Mathf.Lerp(0.58f, 1.0f, core);
                    // 左上切面高光
                    float specDx = dx + innerR * 0.30f;
                    float specDy = dy - innerR * 0.32f;
                    float specDist = Mathf.Sqrt(specDx * specDx + specDy * specDy);
                    float spec = Mathf.Exp(-(specDist * specDist) / (innerR * innerR * 0.11f));

                    float lum = Mathf.Clamp01(shade + spec * 0.65f);
                    Color gemCore = new Color(lum, lum, lum, 1f);
                    Color outerGlow = new Color(0.95f, 0.95f, 0.95f, Mathf.Clamp01(1f - (dist - innerR) / (outerR - innerR + 0.01f)) * 0.85f);
                    float ringMix = SmoothStep(innerR - 1.0f, innerR + 0.8f, dist);
                    col = Color.Lerp(gemCore, outerGlow, ringMix);
                }

                col.a *= outerMask;
                pixels[y * sz + x] = col;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f);
    }

    /// <summary>生成呼应原版顶标风格的八角切面宝石底图（纯白基底可按主题色染色 + 顶面光泽）。</summary>
    private static Sprite BuildGemSprite()
    {
        const int sz = 64;
        var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, mipChain: false, linear: false)
        {
            name = "BazaarBadge_Gem",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        var pixels = new Color[sz * sz];
        float c = (sz - 1) * 0.5f;
        float r = sz * 0.41f;

        for (int y = 0; y < sz; y++)
        {
            for (int x = 0; x < sz; x++)
            {
                float px = Mathf.Abs(x - c);
                float py = Mathf.Abs(y - c);
                // 切角八边形 SDF
                float octSdf = Mathf.Max(Mathf.Max(px, py), (px + py) * 0.70710678f) - r;
                if (octSdf > 2.2f)
                {
                    pixels[y * sz + x] = Color.clear;
                    continue;
                }

                float mask = 1f - SmoothStep(-0.8f, 0.9f, octSdf);
                float vy = (y - c) / r * 0.5f + 0.5f;
                float rimMix = SmoothStep(-3.4f, -2.0f, octSdf);

                // 立体切面：上半亮、下半深，边缘暗化
                float facet = Mathf.Lerp(0.54f, 0.98f, Mathf.Clamp01(vy));
                if (y > c + r * 0.15f && octSdf < -3.0f)
                {
                    facet = Mathf.Clamp01(facet + 0.14f);
                }
                Color body = new Color(facet, facet, facet, 0.97f);
                Color rim = Color.Lerp(new Color(0.45f, 0.34f, 0.22f, 1f), new Color(0.95f, 0.84f, 0.55f, 1f), Mathf.Clamp01(vy));

                Color finalCol = Color.Lerp(body, rim, rimMix);
                finalCol.a *= mask;
                pixels[y * sz + x] = finalCol;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(16f, 16f, 16f, 16f));
    }

    private static Sprite BuildBarSprite(bool isFill)
    {
        const int w = 48;
        const int h = 24;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: false)
        {
            name = isFill ? "BazaarBadge_BarFill" : "BazaarBadge_BarTrack",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        var pixels = new Color[w * h];
        float halfW = w * 0.5f - 2f;
        float halfH = h * 0.5f - 2f;
        float rad = halfH;

        for (int y = 0; y < h; y++)
        {
            float vy = (float)y / (h - 1);
            for (int x = 0; x < w; x++)
            {
                float px = x - (w - 1) * 0.5f;
                float py = y - (h - 1) * 0.5f;
                float sdf = RoundedBoxSdf(px, py, halfW, halfH, rad);
                if (sdf > 1.0f)
                {
                    pixels[y * w + x] = Color.clear;
                    continue;
                }
                float mask = 1f - SmoothStep(-0.7f, 0.8f, sdf);
                Color c;
                if (!isFill)
                {
                    Color bg = Color.Lerp(new Color(0.05f, 0.04f, 0.06f, 0.94f), new Color(0.11f, 0.09f, 0.11f, 0.94f), vy);
                    Color rim = new Color(0.55f, 0.42f, 0.22f, 0.85f);
                    c = Color.Lerp(bg, rim, SmoothStep(-2.0f, -0.9f, sdf));
                }
                else
                {
                    float lum = Mathf.Lerp(0.68f, 1.0f, vy);
                    if (vy > 0.65f) lum = Mathf.Clamp01(lum + 0.12f);
                    c = new Color(lum, lum, lum, 1f);
                }
                c.a *= mask;
                pixels[y * w + x] = c;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(10f, 6f, 10f, 6f));
    }

    private static float RoundedBoxSdf(float px, float py, float bx, float by, float r)
    {
        float qx = Mathf.Abs(px) - bx + r;
        float qy = Mathf.Abs(py) - by + r;
        float ox = Mathf.Max(qx, 0f);
        float oy = Mathf.Max(qy, 0f);
        return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Mathf.Clamp01((x - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }

    private static Color AlphaBlend(Color dst, Color src)
    {
        float outA = src.a + dst.a * (1f - src.a);
        if (outA <= 0.0001f) return Color.clear;
        float r = (src.r * src.a + dst.r * dst.a * (1f - src.a)) / outA;
        float g = (src.g * src.a + dst.g * dst.a * (1f - src.a)) / outA;
        float b = (src.b * src.a + dst.b * dst.a * (1f - src.a)) / outA;
        return new Color(r, g, b, outA);
    }
}

/// <summary>
/// 挂载在单张 3D 卡牌上的原生 World-Space 自定义机制数值徽章宿主（零 OnGUI、零穿模、零热路径 GC）。
/// </summary>
internal sealed class CardMechanismBadgeView
{
    private const float LogicalPlaqueHeight = 30f;

    private static readonly ConcurrentDictionary<Type, PropertyInfo> AttrsPropCache = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo> ItemAmmoFieldCache = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo> ItemGemFieldCache = new();
    private static readonly Vector3[] CornerBuf = new Vector3[8];

    private enum BadgeStyleKind
    {
        Pips,
        Bar,
        Gem,
        Pill,
    }

    private sealed class SlotView
    {
        public CardCounterDisplayInfo Config;
        public BadgeStyleKind Style;
        public Color AccentColor;
        public GameObject RootGo;
        public RectTransform RootRt;
        public Image BgImage;
        public TextMeshProUGUI LabelText;
        public TextMeshProUGUI ValueText;
        public Image[] PipImages;
        public Image BarFillImage;
        public int LastValue = int.MinValue;
        public bool LastVisible;
        public float PunchUntil;
    }

    private GameObject _dockRootGo;
    private RectTransform _dockRt;
    private GameObject _fallbackCanvasGo;
    private Transform _mountedParent;
    private string _activeSignature;
    private readonly List<SlotView> _slots = new();
    private readonly StringBuilder _sigBuf = new(128);
    private float _nextLayoutRefreshTime;
    private bool _lastAmmoActive;

    internal void HideAndClear()
    {
        _activeSignature = null;
        if (_dockRootGo != null && _dockRootGo.activeSelf)
        {
            _dockRootGo.SetActive(false);
        }
        for (int i = 0; i < _slots.Count; i++)
        {
            _slots[i].LastValue = int.MinValue;
            _slots[i].LastVisible = false;
            _slots[i].PunchUntil = 0f;
        }
    }

    internal void DestroyAll()
    {
        _slots.Clear();
        _activeSignature = null;
        if (_dockRootGo != null)
        {
            UnityEngine.Object.Destroy(_dockRootGo);
            _dockRootGo = null;
            _dockRt = null;
        }
        if (_fallbackCanvasGo != null)
        {
            UnityEngine.Object.Destroy(_fallbackCanvasGo);
            _fallbackCanvasGo = null;
        }
        _mountedParent = null;
    }

    /// <summary>在 LateUpdate 中轻量同步当前卡牌的自定义数值徽章；仅在数值变化或布局切换时触碰 UI。</summary>
    internal void Sync(
        Component visualsComp,
        object itemControllerObj,
        Renderer artRenderer,
        Transform cardFrameAnchor,
        bool isBackVisible,
        object liveCard,
        CardCounterDisplayInfo[] displays)
    {
        if (visualsComp == null || liveCard == null || displays == null || displays.Length == 0
            || isBackVisible || artRenderer == null || !artRenderer.enabled || !artRenderer.gameObject.activeInHierarchy)
        {
            if (_dockRootGo != null && _dockRootGo.activeSelf)
                _dockRootGo.SetActive(false);
            return;
        }

        Component ammoCtrl = ResolveItemSubController(itemControllerObj, ItemAmmoFieldCache, "ammoController");
        Component gemCtrl = ResolveItemSubController(itemControllerObj, ItemGemFieldCache, "GemController");

        if (!EnsureMounted(visualsComp, ammoCtrl, gemCtrl, artRenderer, cardFrameAnchor))
        {
            return;
        }

        string sig = BuildSignature(displays);
        if (!string.Equals(sig, _activeSignature, StringComparison.Ordinal))
        {
            RebuildSlots(displays, gemCtrl);
            _activeSignature = sig;
            _nextLayoutRefreshTime = 0f;
        }

        bool ammoActive = ammoCtrl != null && ammoCtrl.gameObject.activeInHierarchy;
        float now = Time.unscaledTime;
        if (now >= _nextLayoutRefreshTime || ammoActive != _lastAmmoActive)
        {
            _lastAmmoActive = ammoActive;
            _nextLayoutRefreshTime = now + 0.5f;
            PositionDockOnCardFace(artRenderer, cardFrameAnchor, ammoCtrl, ammoActive, displays[0]?.Position);
        }

        var attrsProp = AttrsPropCache.GetOrAdd(
            liveCard.GetType(),
            static t => t.GetProperty("Attributes", BindingFlags.Public | BindingFlags.Instance));
        var attrs = attrsProp?.GetValue(liveCard) as IDictionary;

        bool anyVisible = false;
        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            int curVal = ReadLiveIntAttr(attrs, slot.Config.SyncAttribute);
            bool visible = !(slot.Config.HideWhenZero && curVal <= 0);

            if (slot.LastVisible != visible)
            {
                slot.LastVisible = visible;
                if (slot.RootGo != null) slot.RootGo.SetActive(visible);
            }
            if (!visible) continue;
            anyVisible = true;

            if (slot.LastValue != curVal)
            {
                bool hadPrev = slot.LastValue != int.MinValue;
                slot.LastValue = curVal;
                if (hadPrev)
                {
                    slot.PunchUntil = now + 0.30f;
                }
                UpdateSlotVisuals(slot, curVal);
            }

            // 数值跳变弹跳缩放动画（无 GC）
            if (slot.RootRt != null)
            {
                if (now < slot.PunchUntil)
                {
                    float remain = (slot.PunchUntil - now) / 0.30f; // 1 -> 0
                    float punch = 1f + Mathf.Sin(remain * Mathf.PI) * 0.22f;
                    slot.RootRt.localScale = new Vector3(punch, punch, 1f);
                }
                else if (slot.RootRt.localScale.x != 1f)
                {
                    slot.RootRt.localScale = Vector3.one;
                }
            }
        }

        if (_dockRootGo != null && _dockRootGo.activeSelf != anyVisible)
        {
            _dockRootGo.SetActive(anyVisible);
        }
    }

    private bool EnsureMounted(
        Component visualsComp,
        Component ammoCtrl,
        Component gemCtrl,
        Renderer artRenderer,
        Transform cardFrameAnchor)
    {
        Transform targetParent = null;
        int targetLayer = artRenderer != null ? artRenderer.gameObject.layer : visualsComp.gameObject.layer;

        // 优先挂载进卡牌自带的 World-Space Canvas（与原版弹药条/宝石同 Canvas、同透视、同层遮挡）
        Component anchorRef = ammoCtrl != null ? ammoCtrl : gemCtrl;
        if (anchorRef != null)
        {
            Canvas nativeCanvas = anchorRef.GetComponentInParent<Canvas>();
            if (nativeCanvas != null && nativeCanvas.renderMode == RenderMode.WorldSpace)
            {
                targetParent = anchorRef.transform.parent != null ? anchorRef.transform.parent : nativeCanvas.transform;
                targetLayer = targetParent.gameObject.layer;
            }
        }

        // 回退（例如 Recap 战报卡或无弹药/宝石 Canvas 的卡）：在 cardFrameAnchor 下建轻量 World-Space Canvas
        if (targetParent == null)
        {
            Transform anchor = cardFrameAnchor != null ? cardFrameAnchor : visualsComp.transform;
            if (_fallbackCanvasGo == null)
            {
                _fallbackCanvasGo = new GameObject("BazaarCustomBadgeCanvas", typeof(RectTransform), typeof(Canvas));
                _fallbackCanvasGo.transform.SetParent(anchor, worldPositionStays: false);
                _fallbackCanvasGo.transform.localPosition = new Vector3(0f, 0.04f, 0f);
                _fallbackCanvasGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                _fallbackCanvasGo.transform.localScale = Vector3.one;
                _fallbackCanvasGo.layer = targetLayer;

                var canvas = _fallbackCanvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.overrideSorting = false;
            }
            targetParent = _fallbackCanvasGo.transform;
        }

        if (_dockRootGo != null && _mountedParent == targetParent)
        {
            return true;
        }

        if (_dockRootGo != null)
        {
            UnityEngine.Object.Destroy(_dockRootGo);
            _dockRootGo = null;
            _dockRt = null;
            _slots.Clear();
            _activeSignature = null;
        }

        _mountedParent = targetParent;
        _dockRootGo = new GameObject("BazaarCustomMechanismDock", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        _dockRootGo.transform.SetParent(targetParent, worldPositionStays: false);
        _dockRootGo.layer = targetLayer;

        _dockRt = _dockRootGo.GetComponent<RectTransform>();
        _dockRt.anchorMin = new Vector2(0.5f, 0.5f);
        _dockRt.anchorMax = new Vector2(0.5f, 0.5f);
        _dockRt.pivot = new Vector2(0.5f, 0.5f);

        var vlg = _dockRootGo.GetComponent<VerticalLayoutGroup>();
        vlg.spacing = 4f;
        vlg.childAlignment = TextAnchor.MiddleCenter;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false;
        vlg.childForceExpandHeight = false;

        var csf = _dockRootGo.GetComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _dockRootGo.SetActive(false);
        return true;
    }

    private void PositionDockOnCardFace(
        Renderer artRenderer,
        Transform cardFrameAnchor,
        Component ammoCtrl,
        bool ammoActive,
        string positionHint)
    {
        if (_dockRt == null || _mountedParent == null || artRenderer == null) return;

        // 将 3D 卡画网格的 8 个角点精确投影进目标 Canvas 局部坐标，自动兼容小/中/大卡与任意 Canvas Scale
        Transform artTf = artRenderer.transform;
        Bounds lb;
        var mf = artRenderer.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null && mf.sharedMesh.bounds.size.sqrMagnitude > 0.00001f)
        {
            lb = mf.sharedMesh.bounds;
            Vector3 mn = lb.min;
            Vector3 mx = lb.max;
            CornerBuf[0] = artTf.TransformPoint(new Vector3(mn.x, mn.y, mn.z));
            CornerBuf[1] = artTf.TransformPoint(new Vector3(mx.x, mn.y, mn.z));
            CornerBuf[2] = artTf.TransformPoint(new Vector3(mn.x, mx.y, mn.z));
            CornerBuf[3] = artTf.TransformPoint(new Vector3(mx.x, mx.y, mn.z));
            CornerBuf[4] = artTf.TransformPoint(new Vector3(mn.x, mn.y, mx.z));
            CornerBuf[5] = artTf.TransformPoint(new Vector3(mx.x, mn.y, mx.z));
            CornerBuf[6] = artTf.TransformPoint(new Vector3(mn.x, mx.y, mx.z));
            CornerBuf[7] = artTf.TransformPoint(new Vector3(mx.x, mx.y, mx.z));
        }
        else
        {
            Bounds wb = artRenderer.bounds;
            if (wb.size.sqrMagnitude < 0.00001f) return;
            Vector3 mn = wb.min;
            Vector3 mx = wb.max;
            CornerBuf[0] = new Vector3(mn.x, mn.y, mn.z);
            CornerBuf[1] = new Vector3(mx.x, mn.y, mn.z);
            CornerBuf[2] = new Vector3(mn.x, mx.y, mn.z);
            CornerBuf[3] = new Vector3(mx.x, mx.y, mn.z);
            CornerBuf[4] = new Vector3(mn.x, mn.y, mx.z);
            CornerBuf[5] = new Vector3(mx.x, mn.y, mx.z);
            CornerBuf[6] = new Vector3(mn.x, mx.y, mx.z);
            CornerBuf[7] = new Vector3(mx.x, mx.y, mx.z);
        }

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            Vector3 lp = _mountedParent.InverseTransformPoint(CornerBuf[i]);
            if (lp.x < minX) minX = lp.x;
            if (lp.x > maxX) maxX = lp.x;
            if (lp.y < minY) minY = lp.y;
            if (lp.y > maxY) maxY = lp.y;
            if (lp.z < minZ) minZ = lp.z;
            if (lp.z > maxZ) maxZ = lp.z;
        }

        float artW = maxX - minX;
        float artH = maxY - minY;
        if (artW < 0.001f || artH < 0.001f) return;

        // 计算朝向卡牌正面的安全 Z 平面（若存在原版 ammoCtrl，直接对齐其 Z 平面零穿模）
        float targetZ;
        if (ammoCtrl != null && ammoCtrl.transform.parent == _mountedParent)
        {
            targetZ = ammoCtrl.transform.localPosition.z;
        }
        else
        {
            Vector3 frontWorld = cardFrameAnchor != null ? cardFrameAnchor.TransformDirection(Vector3.up) : artTf.up;
            Vector3 frontLocal = _mountedParent.InverseTransformDirection(frontWorld);
            float zSign = frontLocal.z >= 0f ? 1f : -1f;
            float baseZ = zSign >= 0f ? maxZ : minZ;
            targetZ = baseZ + zSign * Mathf.Max(0.01f, artH * 0.012f);
        }

        // 逻辑高度 30px 映射到卡高的 13.8%，确保与原版宝石/价值徽标比例和谐且清晰锐利
        float targetHeight = artH * 0.138f;
        float scale = targetHeight / LogicalPlaqueHeight;
        // 限制最大宽度不超过卡面宽度的 80%，避免小卡超出左右金属边框
        LayoutRebuilder.ForceRebuildLayoutImmediate(_dockRt);
        float prefW = _dockRt.rect.width;
        if (prefW > 1f && prefW * scale > artW * 0.80f)
        {
            scale = (artW * 0.80f) / prefW;
        }

        _dockRt.localScale = new Vector3(scale, scale, scale);
        _dockRt.localRotation = (ammoCtrl != null && ammoCtrl.transform.parent == _mountedParent)
            ? ammoCtrl.transform.localRotation
            : Quaternion.identity;

        float cx = (minX + maxX) * 0.5f;
        float posX = cx;
        float posY = minY + artH * 0.105f;

        string pos = positionHint?.Trim().ToLowerInvariant() ?? "bottom";
        if (pos == "bottomright")
        {
            float halfDockW = (prefW > 1f ? prefW * scale * 0.5f : artW * 0.25f);
            posX = maxX - halfDockW - artW * 0.045f;
            posY = minY + artH * 0.105f;
        }
        else if (pos == "topleft")
        {
            float halfDockW = (prefW > 1f ? prefW * scale * 0.5f : artW * 0.25f);
            posX = minX + halfDockW + artW * 0.045f;
            posY = maxY - artH * 0.15f;
        }
        else if (pos == "topright")
        {
            float halfDockW = (prefW > 1f ? prefW * scale * 0.5f : artW * 0.25f);
            posX = maxX - halfDockW - artW * 0.045f;
            posY = maxY - artH * 0.15f;
        }
        else
        {
            // 默认 bottom：轻向右偏移 5% 避开左下角价格菱标，并在有弹药条时顺滑上移防重叠
            posX = cx + artW * 0.055f;
            posY = minY + artH * (ammoActive ? 0.22f : 0.095f);
        }

        _dockRt.localPosition = new Vector3(posX, posY, targetZ);
    }

    private void RebuildSlots(CardCounterDisplayInfo[] displays, Component gemCtrl)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].RootGo != null) UnityEngine.Object.Destroy(_slots[i].RootGo);
        }
        _slots.Clear();

        TMP_FontAsset font = ResolveBadgeFont(gemCtrl);
        int layer = _dockRootGo != null ? _dockRootGo.layer : 0;

        for (int i = 0; i < displays.Length; i++)
        {
            var cd = displays[i];
            if (cd == null || string.IsNullOrWhiteSpace(cd.SyncAttribute)) continue;

            BadgeStyleKind style = ResolveStyle(cd);
            Color accent = ParseHexColor(cd.ColorHex, new Color(0.98f, 0.52f, 0.14f, 1f));
            SlotView slot = CreateSlotObject(cd, style, accent, font, layer);
            _slots.Add(slot);
        }
    }

    private SlotView CreateSlotObject(
        CardCounterDisplayInfo cd,
        BadgeStyleKind style,
        Color accent,
        TMP_FontAsset font,
        int layer)
    {
        var slot = new SlotView
        {
            Config = cd,
            Style = style,
            AccentColor = accent,
        };

        var rootGo = new GameObject("Badge_" + cd.SyncAttribute, typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
        rootGo.transform.SetParent(_dockRootGo.transform, worldPositionStays: false);
        rootGo.layer = layer;

        var rt = rootGo.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(96f, LogicalPlaqueHeight);

        var bg = rootGo.GetComponent<Image>();
        bg.raycastTarget = false;
        bg.type = Image.Type.Sliced;

        var hlg = rootGo.GetComponent<HorizontalLayoutGroup>();
        hlg.childAlignment = TextAnchor.MiddleCenter;
        hlg.childControlWidth = true;
        hlg.childControlHeight = false;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;

        var csf = rootGo.GetComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

        var le = rootGo.AddComponent<LayoutElement>();
        le.preferredHeight = LogicalPlaqueHeight;
        le.minHeight = LogicalPlaqueHeight;

        slot.RootGo = rootGo;
        slot.RootRt = rt;
        slot.BgImage = bg;

        bool hasLabel = !string.IsNullOrWhiteSpace(cd.LabelZh);

        if (style == BadgeStyleKind.Gem)
        {
            bg.sprite = CardBadgeArtLibrary.GetGemSprite();
            bg.color = Color.Lerp(accent, new Color(0.18f, 0.12f, 0.14f, 1f), 0.22f);
            hlg.padding = new RectOffset(10, 10, 2, 2);
            hlg.spacing = 4f;

            if (hasLabel)
            {
                slot.LabelText = CreateBadgeTmp(rootGo.transform, "Label", cd.LabelZh.Trim(), 12.5f, new Color(0.98f, 0.92f, 0.78f, 1f), FontStyles.Bold, font, layer);
            }
            slot.ValueText = CreateBadgeTmp(rootGo.transform, "Val", "0", 16f, Color.white, FontStyles.Bold, font, layer);
        }
        else if (style == BadgeStyleKind.Pips)
        {
            bg.sprite = CardBadgeArtLibrary.GetPlaqueSprite(ready: false);
            bg.color = Color.white;
            hlg.padding = new RectOffset(10, 10, 2, 2);
            hlg.spacing = 5f;

            if (hasLabel)
            {
                slot.LabelText = CreateBadgeTmp(rootGo.transform, "Label", cd.LabelZh.Trim(), 13f, new Color(0.95f, 0.85f, 0.62f, 1f), FontStyles.Bold, font, layer);
            }

            int pipCount = Mathf.Clamp(cd.MaxValue ?? 3, 1, 6);
            var pipRow = new GameObject("Pips", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            pipRow.transform.SetParent(rootGo.transform, false);
            pipRow.layer = layer;
            var pipHlg = pipRow.GetComponent<HorizontalLayoutGroup>();
            pipHlg.spacing = 3f;
            pipHlg.childAlignment = TextAnchor.MiddleCenter;
            pipHlg.childControlWidth = false;
            pipHlg.childControlHeight = false;
            var pipCsf = pipRow.GetComponent<ContentSizeFitter>();
            pipCsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            pipCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            slot.PipImages = new Image[pipCount];
            for (int p = 0; p < pipCount; p++)
            {
                var pGo = new GameObject("Pip_" + p, typeof(RectTransform), typeof(Image));
                pGo.transform.SetParent(pipRow.transform, false);
                pGo.layer = layer;
                var pRt = pGo.GetComponent<RectTransform>();
                pRt.sizeDelta = new Vector2(14f, 14f);
                var pImg = pGo.GetComponent<Image>();
                pImg.sprite = CardBadgeArtLibrary.GetOrbEmptySprite();
                pImg.color = Color.white;
                pImg.raycastTarget = false;
                slot.PipImages[p] = pImg;
            }

            slot.ValueText = CreateBadgeTmp(rootGo.transform, "Val", "0/3", 13.5f, new Color(0.98f, 0.96f, 0.90f, 1f), FontStyles.Bold, font, layer);
        }
        else if (style == BadgeStyleKind.Bar)
        {
            bg.sprite = CardBadgeArtLibrary.GetPlaqueSprite(ready: false);
            bg.color = Color.white;
            hlg.padding = new RectOffset(10, 10, 2, 2);
            hlg.spacing = 6f;

            if (hasLabel)
            {
                slot.LabelText = CreateBadgeTmp(rootGo.transform, "Label", cd.LabelZh.Trim(), 12.5f, new Color(0.95f, 0.85f, 0.62f, 1f), FontStyles.Bold, font, layer);
            }

            var barGo = new GameObject("BarTrack", typeof(RectTransform), typeof(Image));
            barGo.transform.SetParent(rootGo.transform, false);
            barGo.layer = layer;
            var barRt = barGo.GetComponent<RectTransform>();
            barRt.sizeDelta = new Vector2(54f, 11f);
            var barLe = barGo.AddComponent<LayoutElement>();
            barLe.preferredWidth = 54f;
            barLe.preferredHeight = 11f;
            var trackImg = barGo.GetComponent<Image>();
            trackImg.sprite = CardBadgeArtLibrary.GetBarTrackSprite();
            trackImg.type = Image.Type.Sliced;
            trackImg.raycastTarget = false;

            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(barGo.transform, false);
            fillGo.layer = layer;
            var fillRt = fillGo.GetComponent<RectTransform>();
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(1f, 1f);
            fillRt.offsetMin = new Vector2(2f, 2f);
            fillRt.offsetMax = new Vector2(-2f, -2f);
            var fillImg = fillGo.GetComponent<Image>();
            fillImg.sprite = CardBadgeArtLibrary.GetBarFillSprite();
            fillImg.type = Image.Type.Filled;
            fillImg.fillMethod = Image.FillMethod.Horizontal;
            fillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImg.fillAmount = 0f;
            fillImg.color = accent;
            fillImg.raycastTarget = false;
            slot.BarFillImage = fillImg;

            slot.ValueText = CreateBadgeTmp(rootGo.transform, "Val", "0/10", 12.5f, Color.white, FontStyles.Bold, font, layer);
        }
        else
        {
            // Pill：古铜浮雕牌匾 + 主题晶点 + 通用格式化文本
            bg.sprite = CardBadgeArtLibrary.GetPlaqueSprite(ready: false);
            bg.color = Color.white;
            hlg.padding = new RectOffset(9, 10, 2, 2);
            hlg.spacing = 5f;

            var dotGo = new GameObject("AccentOrb", typeof(RectTransform), typeof(Image));
            dotGo.transform.SetParent(rootGo.transform, false);
            dotGo.layer = layer;
            var dotRt = dotGo.GetComponent<RectTransform>();
            dotRt.sizeDelta = new Vector2(11f, 11f);
            var dotImg = dotGo.GetComponent<Image>();
            dotImg.sprite = CardBadgeArtLibrary.GetOrbFilledSprite();
            dotImg.color = accent;
            dotImg.raycastTarget = false;

            if (hasLabel)
            {
                slot.LabelText = CreateBadgeTmp(rootGo.transform, "Label", cd.LabelZh.Trim(), 13f, new Color(0.95f, 0.85f, 0.62f, 1f), FontStyles.Bold, font, layer);
            }
            slot.ValueText = CreateBadgeTmp(rootGo.transform, "Val", "0", 13.5f, Color.white, FontStyles.Bold, font, layer);
        }

        return slot;
    }

    private static TextMeshProUGUI CreateBadgeTmp(
        Transform parent,
        string name,
        string initialText,
        float fontSize,
        Color color,
        FontStyles style,
        TMP_FontAsset font,
        int layer)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        go.layer = layer;

        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.text = initialText;
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.raycastTarget = false;

        // 使用轻量 UGUI Shadow 替代修改 TMP.outlineWidth，避免每张卡派生独立字体材质造成显存泄漏
        var shadow = go.AddComponent<Shadow>();
        shadow.effectColor = new Color(0.04f, 0.025f, 0.02f, 0.92f);
        shadow.effectDistance = new Vector2(1.1f, -1.2f);
        shadow.useGraphicAlpha = true;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = LogicalPlaqueHeight - 4f;
        return tmp;
    }

    private static void UpdateSlotVisuals(SlotView slot, int rawVal)
    {
        var cd = slot.Config;
        bool hasMax = cd.MaxValue.HasValue && cd.MaxValue.Value > 0;
        bool isReady = hasMax && rawVal >= cd.MaxValue.Value;
        string formattedVal = FormatCounterValue(cd, rawVal, hasMax);

        if (slot.ValueText != null)
        {
            slot.ValueText.text = formattedVal;
            slot.ValueText.color = isReady
                ? new Color(1.0f, 0.93f, 0.56f, 1f)
                : Color.white;
        }

        if (slot.BgImage != null && slot.Style != BadgeStyleKind.Gem)
        {
            slot.BgImage.sprite = CardBadgeArtLibrary.GetPlaqueSprite(isReady);
        }

        if (slot.PipImages != null)
        {
            Color litColor = isReady ? new Color(1.0f, 0.84f, 0.22f, 1f) : slot.AccentColor;
            for (int p = 0; p < slot.PipImages.Length; p++)
            {
                bool lit = p < rawVal;
                slot.PipImages[p].sprite = lit
                    ? CardBadgeArtLibrary.GetOrbFilledSprite()
                    : CardBadgeArtLibrary.GetOrbEmptySprite();
                slot.PipImages[p].color = lit ? litColor : Color.white;
            }
        }

        if (slot.BarFillImage != null && hasMax)
        {
            slot.BarFillImage.fillAmount = Mathf.Clamp01((float)rawVal / cd.MaxValue.Value);
            slot.BarFillImage.color = isReady ? new Color(1.0f, 0.84f, 0.22f, 1f) : slot.AccentColor;
        }
    }

    private static string FormatCounterValue(CardCounterDisplayInfo cd, int rawVal, bool hasMax)
    {
        string valueType = cd.ValueType?.Trim().ToLowerInvariant() ?? "count";
        string valStr;
        if (valueType == "seconds")
        {
            double sec = Math.Abs(rawVal) >= 100 ? rawVal / 1000.0 : rawVal;
            valStr = Math.Round(sec, 1).ToString("0.#", CultureInfo.InvariantCulture) + "s";
        }
        else if (valueType == "percent")
        {
            valStr = rawVal.ToString(CultureInfo.InvariantCulture) + "%";
        }
        else if (valueType == "multiplier")
        {
            valStr = "×" + rawVal.ToString(CultureInfo.InvariantCulture);
        }
        else if (valueType == "delta")
        {
            valStr = (rawVal >= 0 ? "+" : string.Empty) + rawVal.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            valStr = rawVal.ToString(CultureInfo.InvariantCulture);
        }

        string unit = string.IsNullOrWhiteSpace(cd.Unit) ? string.Empty : cd.Unit.Trim();

        if (!string.IsNullOrWhiteSpace(cd.Format))
        {
            return cd.Format
                .Replace("{label}", cd.LabelZh ?? string.Empty)
                .Replace("{value}", valStr)
                .Replace("{max}", hasMax ? cd.MaxValue.Value.ToString(CultureInfo.InvariantCulture) : string.Empty)
                .Replace("{unit}", unit);
        }

        if (hasMax && (valueType == "count" || valueType == "progress"))
        {
            return $"{valStr}/{cd.MaxValue.Value}{unit}";
        }
        return valStr + unit;
    }

    private static BadgeStyleKind ResolveStyle(CardCounterDisplayInfo cd)
    {
        string explicitStyle = cd.Style?.Trim().ToLowerInvariant();
        if (explicitStyle == "pips") return BadgeStyleKind.Pips;
        if (explicitStyle == "bar") return BadgeStyleKind.Bar;
        if (explicitStyle == "gem") return BadgeStyleKind.Gem;
        if (explicitStyle == "pill") return BadgeStyleKind.Pill;

        // auto 智能自适应：2..6 阶层数用发光晶球槽，>6 用鎏金进度条，无上限用切面宝石/胶囊
        if (cd.MaxValue.HasValue && cd.MaxValue.Value >= 2 && cd.MaxValue.Value <= 6)
            return BadgeStyleKind.Pips;
        if (cd.MaxValue.HasValue && cd.MaxValue.Value > 6)
            return BadgeStyleKind.Bar;
        string vt = cd.ValueType?.Trim().ToLowerInvariant();
        if (vt == "percent" || vt == "seconds" || vt == "multiplier" || vt == "delta")
            return BadgeStyleKind.Pill;
        return BadgeStyleKind.Gem;
    }

    private string BuildSignature(CardCounterDisplayInfo[] displays)
    {
        _sigBuf.Clear();
        for (int i = 0; i < displays.Length; i++)
        {
            var d = displays[i];
            if (d == null) continue;
            _sigBuf.Append(d.SyncAttribute).Append('|')
                .Append(d.LabelZh).Append('|')
                .Append(d.MaxValue?.ToString(CultureInfo.InvariantCulture) ?? "").Append('|')
                .Append(d.ColorHex).Append('|')
                .Append(d.Style).Append('|')
                .Append(d.ValueType).Append('|')
                .Append(d.Format).Append('|')
                .Append(d.Unit).Append('|')
                .Append(d.Position).Append(';');
        }
        return _sigBuf.ToString();
    }

    private static Component ResolveItemSubController(object itemCtrlObj, ConcurrentDictionary<Type, FieldInfo> cache, string fieldName)
    {
        if (itemCtrlObj == null) return null;
        try
        {
            var f = cache.GetOrAdd(itemCtrlObj.GetType(), t => t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            return f?.GetValue(itemCtrlObj) as Component;
        }
        catch
        {
            return null;
        }
    }

    private static TMP_FontAsset ResolveBadgeFont(Component gemCtrl)
    {
        var uiFont = CustomGameplayUI.Instance?.GetActiveUiFont();
        if (uiFont != null) return uiFont;
        try
        {
            var tmp = gemCtrl != null ? gemCtrl.GetComponentInChildren<TMP_Text>(true) : null;
            if (tmp != null && tmp.font != null) return tmp.font;
        }
        catch { }
        return TMP_Settings.defaultFontAsset;
    }

    private static int ReadLiveIntAttr(IDictionary attrs, string attrName)
    {
        if (attrs == null || string.IsNullOrEmpty(attrName)) return 0;
        foreach (DictionaryEntry kv in attrs)
        {
            if (string.Equals(kv.Key?.ToString(), attrName, StringComparison.OrdinalIgnoreCase))
            {
                if (kv.Value != null && double.TryParse(kv.Value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    return (int)Math.Round(d);
                return 0;
            }
        }
        return 0;
    }

    private static Color ParseHexColor(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string s = hex.Trim();
        if (!s.StartsWith("#", StringComparison.Ordinal)) s = "#" + s;
        return ColorUtility.TryParseHtmlString(s, out Color parsed) ? parsed : fallback;
    }
}
