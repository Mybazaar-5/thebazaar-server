using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BazaarGameShared.Domain.Core.Types;
using BazaarModSdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarLauncher.Modules;

/// <summary>
/// D3-P1-07：纯逻辑徽章 5 样式 × 6 值型格式化评估器（零 Unity 依赖，供运行期渲染与单元测试共用）。
/// </summary>
internal sealed class BadgeFormatResult
{
    public string DisplayText { get; set; } = string.Empty;
    public bool SuppressSeparateLabel { get; set; }
    public bool IsHidden { get; set; }
    public string EffectiveStyle { get; set; } = "pill";
}

internal static class BadgeFormatEvaluator
{
    internal static BadgeFormatResult Evaluate(
        int curVal,
        int maxVal,
        string style,
        string valueFormat,
        string label,
        string unit,
        string format,
        bool hideWhenZero)
    {
        bool hasMax = maxVal > 0;
        bool isHidden = hideWhenZero && curVal == 0;
        string vt = string.IsNullOrWhiteSpace(valueFormat) ? "int" : valueFormat.Trim().ToLowerInvariant();
        string effStyle = ResolveEffectiveStyle(style, vt, hasMax, maxVal);
        string cleanUnit = string.IsNullOrWhiteSpace(unit) ? string.Empty : unit.Trim();

        string coreVal;
        bool unitAlreadyIncluded = false;
        switch (vt)
        {
            case "seconds":
            {
                double sec = Math.Abs(curVal) >= 100 ? curVal / 1000.0 : curVal;
                coreVal = Math.Round(sec, 2).ToString("0.##", CultureInfo.InvariantCulture) + "s";
                unitAlreadyIncluded = string.Equals(cleanUnit, "s", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cleanUnit, "sec", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cleanUnit, "秒", StringComparison.Ordinal);
                break;
            }
            case "percent":
            {
                coreVal = curVal.ToString(CultureInfo.InvariantCulture) + "%";
                unitAlreadyIncluded = string.Equals(cleanUnit, "%", StringComparison.Ordinal);
                break;
            }
            case "multiplier":
            {
                coreVal = "x" + curVal.ToString(CultureInfo.InvariantCulture);
                unitAlreadyIncluded = string.Equals(cleanUnit, "x", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cleanUnit, "×", StringComparison.Ordinal);
                break;
            }
            case "delta":
            {
                coreVal = (curVal > 0 ? "+" : string.Empty) + curVal.ToString(CultureInfo.InvariantCulture);
                break;
            }
            case "fraction":
            case "progress":
            {
                coreVal = hasMax
                    ? $"{curVal.ToString(CultureInfo.InvariantCulture)}/{maxVal.ToString(CultureInfo.InvariantCulture)}"
                    : curVal.ToString(CultureInfo.InvariantCulture);
                break;
            }
            case "count":
            {
                coreVal = hasMax
                    ? $"{curVal.ToString(CultureInfo.InvariantCulture)}/{maxVal.ToString(CultureInfo.InvariantCulture)}"
                    : curVal.ToString(CultureInfo.InvariantCulture);
                break;
            }
            default:
            {
                coreVal = curVal.ToString(CultureInfo.InvariantCulture);
                break;
            }
        }

        string effectiveUnit = unitAlreadyIncluded ? string.Empty : cleanUnit;
        bool suppressLabel = false;
        string displayText;

        if (!string.IsNullOrWhiteSpace(format))
        {
            suppressLabel = format.IndexOf("{label}", StringComparison.OrdinalIgnoreCase) >= 0;
            string safeLabel = label?.Trim() ?? string.Empty;
            string maxStr = hasMax ? maxVal.ToString(CultureInfo.InvariantCulture) : string.Empty;
            displayText = ReplaceOrdinalIgnoreCase(format, "{label}", safeLabel);
            displayText = ReplaceOrdinalIgnoreCase(displayText, "{val}", coreVal);
            displayText = ReplaceOrdinalIgnoreCase(displayText, "{value}", coreVal);
            displayText = ReplaceOrdinalIgnoreCase(displayText, "{max}", maxStr);
            displayText = ReplaceOrdinalIgnoreCase(displayText, "{unit}", effectiveUnit);
        }
        else
        {
            if (!string.IsNullOrEmpty(effectiveUnit)
                && coreVal.EndsWith(effectiveUnit, StringComparison.OrdinalIgnoreCase))
            {
                displayText = coreVal;
            }
            else
            {
                displayText = coreVal + effectiveUnit;
            }
        }

        return new BadgeFormatResult
        {
            DisplayText = displayText,
            SuppressSeparateLabel = suppressLabel,
            IsHidden = isHidden,
            EffectiveStyle = effStyle,
        };
    }

    private static string ResolveEffectiveStyle(string style, string valueType, bool hasMax, int maxVal)
    {
        string explicitStyle = style?.Trim().ToLowerInvariant();
        if (explicitStyle == "bar")
        {
            return hasMax ? "bar" : "pill";
        }
        if (explicitStyle == "pips")
        {
            if (!hasMax) return "pill";
            return maxVal > 6 ? "bar" : "pips";
        }
        if (explicitStyle == "gem") return "gem";
        if (explicitStyle == "pill") return "pill";

        // auto 智能自适应：2..6 阶层数用发光晶球槽，>6 用鎏金进度条，无上限按值型选胶囊/宝石
        if (hasMax && maxVal >= 2 && maxVal <= 6) return "pips";
        if (hasMax && maxVal > 6) return "bar";
        if (valueType == "percent" || valueType == "seconds" || valueType == "multiplier" || valueType == "delta")
            return "pill";
        return "gem";
    }

    private static string ReplaceOrdinalIgnoreCase(string source, string token, string replacement)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(token)) return source ?? string.Empty;
        int idx = source.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return source;

        var sb = new System.Text.StringBuilder(source.Length + 16);
        int pos = 0;
        while (idx >= 0)
        {
            sb.Append(source, pos, idx - pos);
            sb.Append(replacement ?? string.Empty);
            pos = idx + token.Length;
            idx = source.IndexOf(token, pos, StringComparison.OrdinalIgnoreCase);
        }
        sb.Append(source, pos, source.Length - pos);
        return sb.ToString();
    }
}

/// <summary>
/// 原生 3D World-Space 通用自定义机制/数值徽章程序化图集库：
///   - D3-P1-09：全部 7 套 Texture2D 与 7 套 Sprite 均设 HideFlags.HideAndDontSave，切场景防白块；
///   - EnsureSprites() 全量校验全部 7 项纹理与 Sprite 存活；
///   - D4-P1-8：提供 ReleaseAllOnMain() 在插件卸载时统一 Destroy 避免 GPU 显存泄漏。
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

    private static Texture2D _plaqueTex;
    private static Texture2D _plaqueReadyTex;
    private static Texture2D _orbEmptyTex;
    private static Texture2D _orbFilledTex;
    private static Texture2D _gemTex;
    private static Texture2D _barTrackTex;
    private static Texture2D _barFillTex;

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

    private static bool IsSpriteAlive(Sprite sp) => sp != null && sp.texture != null;

    private static void EnsureSprites()
    {
        if (!IsSpriteAlive(_plaqueSprite))
        {
            DestroySpriteAndTex(ref _plaqueSprite, ref _plaqueTex);
            _plaqueSprite = BuildPlaqueSprite(ready: false, out _plaqueTex);
        }
        if (!IsSpriteAlive(_plaqueReadySprite))
        {
            DestroySpriteAndTex(ref _plaqueReadySprite, ref _plaqueReadyTex);
            _plaqueReadySprite = BuildPlaqueSprite(ready: true, out _plaqueReadyTex);
        }
        if (!IsSpriteAlive(_orbEmptySprite))
        {
            DestroySpriteAndTex(ref _orbEmptySprite, ref _orbEmptyTex);
            _orbEmptySprite = BuildOrbSprite(filled: false, out _orbEmptyTex);
        }
        if (!IsSpriteAlive(_orbFilledSprite))
        {
            DestroySpriteAndTex(ref _orbFilledSprite, ref _orbFilledTex);
            _orbFilledSprite = BuildOrbSprite(filled: true, out _orbFilledTex);
        }
        if (!IsSpriteAlive(_gemSprite))
        {
            DestroySpriteAndTex(ref _gemSprite, ref _gemTex);
            _gemSprite = BuildGemSprite(out _gemTex);
        }
        if (!IsSpriteAlive(_barTrackSprite))
        {
            DestroySpriteAndTex(ref _barTrackSprite, ref _barTrackTex);
            _barTrackSprite = BuildBarSprite(isFill: false, out _barTrackTex);
        }
        if (!IsSpriteAlive(_barFillSprite))
        {
            DestroySpriteAndTex(ref _barFillSprite, ref _barFillTex);
            _barFillSprite = BuildBarSprite(isFill: true, out _barFillTex);
        }
    }

    /// <summary>D3-P1-09 &amp; D4-P1-8：插件卸载时在主线程释放全部 7 套程序化 Sprite 与 Texture2D 显存。</summary>
    internal static void ReleaseAllOnMain()
    {
        DestroySpriteAndTex(ref _plaqueSprite, ref _plaqueTex);
        DestroySpriteAndTex(ref _plaqueReadySprite, ref _plaqueReadyTex);
        DestroySpriteAndTex(ref _orbEmptySprite, ref _orbEmptyTex);
        DestroySpriteAndTex(ref _orbFilledSprite, ref _orbFilledTex);
        DestroySpriteAndTex(ref _gemSprite, ref _gemTex);
        DestroySpriteAndTex(ref _barTrackSprite, ref _barTrackTex);
        DestroySpriteAndTex(ref _barFillSprite, ref _barFillTex);
    }

    private static void DestroySpriteAndTex(ref Sprite sp, ref Texture2D tex)
    {
        try
        {
            if (sp != null) UnityEngine.Object.Destroy(sp);
        }
        catch { }
        sp = null;

        try
        {
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }
        catch { }
        tex = null;
    }

    /// <summary>生成 9-slice 雕花古铜鎏金内嵌牌匾（带柔和投影、顶边鎏金高光与深曜石内凹底面）。</summary>
    private static Sprite BuildPlaqueSprite(bool ready, out Texture2D tex)
    {
        const int w = 64;
        const int h = 64;
        tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: false)
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
            float vy = (float)y / (h - 1);
            for (int x = 0; x < w; x++)
            {
                float px = x - (w - 1) * 0.5f;
                float py = y - (h - 1) * 0.5f;
                float sdf = RoundedBoxSdf(px, py, halfW, halfH, radius);

                float shadowSdf = RoundedBoxSdf(px, py + 1.8f, halfW + 1.2f, halfH + 1.2f, radius + 1.2f);
                float shadowAlpha = Mathf.Clamp01(1f - Mathf.Clamp01((shadowSdf + 0.5f) / 4.0f)) * 0.52f;
                Color outCol = new Color(0.03f, 0.02f, 0.02f, shadowAlpha);

                if (sdf <= 1.2f)
                {
                    float bodyMask = 1f - SmoothStep(-0.8f, 0.9f, sdf);
                    Color rimCol = vy > 0.5f
                        ? Color.Lerp(rimMid, rimTop, (vy - 0.5f) * 2f)
                        : Color.Lerp(rimBot, rimMid, vy * 2f);

                    Color innerCol = Color.Lerp(fillBot, fillTop, vy);
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
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(18f, 18f, 18f, 18f));
        sp.hideFlags = HideFlags.HideAndDontSave;
        return sp;
    }

    private static Sprite BuildOrbSprite(bool filled, out Texture2D tex)
    {
        const int sz = 48;
        tex = new Texture2D(sz, sz, TextureFormat.RGBA32, mipChain: false, linear: false)
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
                    Color ringCol = Color.Lerp(new Color(0.36f, 0.25f, 0.13f, 0.95f), new Color(0.76f, 0.58f, 0.30f, 0.95f), Mathf.Clamp01(vy));
                    float holeT = Mathf.Clamp01(dist / innerR);
                    Color holeCol = Color.Lerp(new Color(0.05f, 0.04f, 0.06f, 0.94f), new Color(0.14f, 0.11f, 0.13f, 0.94f), holeT * holeT);
                    float ringMix = SmoothStep(innerR - 0.9f, innerR + 0.7f, dist);
                    col = Color.Lerp(holeCol, ringCol, ringMix);
                }
                else
                {
                    float norm = Mathf.Clamp01(dist / innerR);
                    float core = Mathf.Pow(1f - norm, 0.55f);
                    float shade = Mathf.Lerp(0.58f, 1.0f, core);
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
        var sp = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f);
        sp.hideFlags = HideFlags.HideAndDontSave;
        return sp;
    }

    private static Sprite BuildGemSprite(out Texture2D tex)
    {
        const int sz = 64;
        tex = new Texture2D(sz, sz, TextureFormat.RGBA32, mipChain: false, linear: false)
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
                float octSdf = Mathf.Max(Mathf.Max(px, py), (px + py) * 0.70710678f) - r;
                if (octSdf > 2.2f)
                {
                    pixels[y * sz + x] = Color.clear;
                    continue;
                }

                float mask = 1f - SmoothStep(-0.8f, 0.9f, octSdf);
                float vy = (y - c) / r * 0.5f + 0.5f;
                float rimMix = SmoothStep(-3.4f, -2.0f, octSdf);

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
        var sp = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(16f, 16f, 16f, 16f));
        sp.hideFlags = HideFlags.HideAndDontSave;
        return sp;
    }

    private static Sprite BuildBarSprite(bool isFill, out Texture2D tex)
    {
        const int w = 48;
        const int h = 24;
        tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: false)
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
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(10f, 6f, 10f, 6f));
        sp.hideFlags = HideFlags.HideAndDontSave;
        return sp;
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
/// 挂载在单张 3D 卡牌/技能/遭遇载体上的原生 World-Space 自定义机制数值徽章宿主（D3-P1-07 升级版）：
///   - 全载体支持：ItemVisualsController / CardController / SkillController / SkillProxyRenderer / EncounterController；
///   - 按 Position 分 Dock 独立锚定与避让（Bottom* 避让弹药条，TopCenter 避让顶部宝石，同组垂直步进零重叠）；
///   - 预编译 ECardAttributeType O(1) 查表 + 10Hz 零分配 long 状态哈希脏检替代每帧 GC。
/// </summary>
internal sealed class CardMechanismBadgeView
{
    private const float LogicalPlaqueHeight = 30f;
    private const int MaxBadgeSlots = 4;
    private const float PollIntervalSeconds = 0.10f; // 10Hz 零 GC 脏检

    private static readonly ConcurrentDictionary<Type, PropertyInfo> AttrsPropCache = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo> ItemAmmoFieldCache = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo> ItemGemFieldCache = new();
    private static readonly Vector3[] CornerBuf = new Vector3[8];

    private enum BadgeAnchorGroup
    {
        BottomCenter,
        BottomLeft,
        BottomRight,
        TopCenter,
        TopLeft,
        TopRight,
    }

    private sealed class DockGroupView
    {
        public BadgeAnchorGroup Anchor;
        public GameObject RootGo;
        public RectTransform RootRt;
        public readonly List<SlotView> Slots = new();
    }

    private sealed class SlotView
    {
        public CardCounterDisplayInfo Config;
        public ECardAttributeType? ParsedAttrKey;
        public ECardAttributeType? ParsedMaxAttrKey;
        public BadgeAnchorGroup Anchor;
        public string ActiveStyle;
        public Color AccentColor;
        public GameObject RootGo;
        public RectTransform RootRt;
        public Image BgImage;
        public TextMeshProUGUI LabelText;
        public TextMeshProUGUI ValueText;
        public Image[] PipImages;
        public Image BarFillImage;
        public int LastValue = int.MinValue;
        public int LastMaxValue = int.MinValue;
        public bool LastVisible;
        public float PunchUntil;
    }

    private readonly Dictionary<BadgeAnchorGroup, DockGroupView> _docks = new();
    private readonly List<SlotView> _slots = new();
    private GameObject _fallbackCanvasGo;
    private Transform _mountedParent;
    private long _activeConfigHash;
    private bool _hasActiveConfig;
    private float _nextPollTime;
    private float _nextLayoutRefreshTime;
    private bool _lastAmmoActive;
    private bool _forceImmediatePoll;

    /// <summary>D3-P1-07：全载体宿主变换解析（兼容 ItemVisualsController / CardController / SkillController / SkillProxyRenderer / EncounterController）。</summary>
    internal static Transform ResolveBadgeHostTransform(Component host, Transform preferredAnchor = null)
    {
        if (preferredAnchor != null) return preferredAnchor;
        if (host == null) return null;

        try
        {
            var t = host.GetType();
            var frameField = t.GetField("cardFrameAnchor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? t.GetField("cardVisuals", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? t.GetField("_currentFrame", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object val = frameField?.GetValue(host);
            if (val is Transform tf && tf != null) return tf;
            if (val is GameObject go && go != null) return go.transform;
            if (val is Component comp && comp != null) return comp.transform;
        }
        catch { }

        return host.transform;
    }

    internal void MarkDirty()
    {
        _forceImmediatePoll = true;
    }

    internal void HideAndClear()
    {
        _hasActiveConfig = false;
        _activeConfigHash = 0;
        _forceImmediatePoll = false;
        foreach (var dock in _docks.Values)
        {
            if (dock.RootGo != null && dock.RootGo.activeSelf)
            {
                dock.RootGo.SetActive(false);
            }
        }
        for (int i = 0; i < _slots.Count; i++)
        {
            _slots[i].LastValue = int.MinValue;
            _slots[i].LastMaxValue = int.MinValue;
            _slots[i].LastVisible = false;
            _slots[i].PunchUntil = 0f;
        }
    }

    internal void DestroyAll()
    {
        _slots.Clear();
        _hasActiveConfig = false;
        _activeConfigHash = 0;
        foreach (var dock in _docks.Values)
        {
            if (dock.RootGo != null)
            {
                UnityEngine.Object.Destroy(dock.RootGo);
            }
        }
        _docks.Clear();

        if (_fallbackCanvasGo != null)
        {
            UnityEngine.Object.Destroy(_fallbackCanvasGo);
            _fallbackCanvasGo = null;
        }
        _mountedParent = null;
    }

    /// <summary>物品卡标准同步入口（保留原签名兼容既有调用点）。</summary>
    internal void Sync(
        Component visualsComp,
        object itemControllerObj,
        Renderer artRenderer,
        Transform cardFrameAnchor,
        bool isBackVisible,
        object liveCard,
        CardCounterDisplayInfo[] displays)
    {
        SyncForAnyHost(visualsComp, artRenderer, cardFrameAnchor, liveCard, displays, itemControllerObj, isBackVisible);
    }

    /// <summary>D3-P1-07：全载体统一同步入口（支持物品/技能/遭遇），10Hz 零分配哈希脏检驱动。</summary>
    internal void SyncForAnyHost(
        Component hostComp,
        Renderer boundsRenderer,
        Transform preferredAnchor,
        object liveCard,
        CardCounterDisplayInfo[] displays,
        object itemControllerObj = null,
        bool isBackVisible = false)
    {
        if (hostComp == null || liveCard == null || displays == null || displays.Length == 0 || isBackVisible)
        {
            SetAllDocksActive(false);
            return;
        }

        if (boundsRenderer == null)
        {
            boundsRenderer = hostComp.GetComponentInChildren<Renderer>(includeInactive: false);
        }
        if (boundsRenderer == null || !boundsRenderer.enabled || !boundsRenderer.gameObject.activeInHierarchy)
        {
            SetAllDocksActive(false);
            return;
        }

        float now = Time.unscaledTime;
        // 仅在 10Hz 节拍、存在活跃跳变动画或外部标记脏态时执行刷新，避免 60/144fps 无效计算
        bool anyPunchActive = HasActivePunchAnimation(now);
        if (!_forceImmediatePoll && _hasActiveConfig && now < _nextPollTime)
        {
            if (anyPunchActive)
            {
                TickPunchAnimations(now);
            }
            return;
        }

        _forceImmediatePoll = false;
        _nextPollTime = now + PollIntervalSeconds;

        Component ammoCtrl = ResolveItemSubController(itemControllerObj, ItemAmmoFieldCache, "ammoController");
        Component gemCtrl = ResolveItemSubController(itemControllerObj, ItemGemFieldCache, "GemController");
        Transform hostAnchor = ResolveBadgeHostTransform(hostComp, preferredAnchor);

        if (!EnsureMountParent(hostComp, ammoCtrl, gemCtrl, boundsRenderer, hostAnchor))
        {
            return;
        }

        var attrsProp = AttrsPropCache.GetOrAdd(
            liveCard.GetType(),
            static t => t.GetProperty("Attributes", BindingFlags.Public | BindingFlags.Instance));
        var attrs = attrsProp?.GetValue(liveCard) as IDictionary;

        long cfgHash = ComputeDisplayConfigHash(displays, attrs);
        if (!_hasActiveConfig || cfgHash != _activeConfigHash)
        {
            RebuildSlots(displays, attrs, gemCtrl);
            _activeConfigHash = cfgHash;
            _hasActiveConfig = true;
            _nextLayoutRefreshTime = 0f;
        }

        bool ammoActive = ammoCtrl != null && ammoCtrl.gameObject.activeInHierarchy;
        if (now >= _nextLayoutRefreshTime || ammoActive != _lastAmmoActive)
        {
            _lastAmmoActive = ammoActive;
            _nextLayoutRefreshTime = now + 0.5f;
            PositionAllDocksOnHostFace(boundsRenderer, hostAnchor, ammoCtrl, ammoActive);
        }

        foreach (var dock in _docks.Values)
        {
            bool dockAnyVisible = false;
            for (int i = 0; i < dock.Slots.Count; i++)
            {
                var slot = dock.Slots[i];
                int curVal = ReadLiveIntAttr(attrs, slot.ParsedAttrKey, slot.Config.SyncAttribute);
                int maxVal = ResolveEffectiveMax(attrs, slot);
                var eval = BadgeFormatEvaluator.Evaluate(
                    curVal,
                    maxVal,
                    slot.Config.Style,
                    slot.Config.ValueType,
                    slot.Config.LabelZh,
                    slot.Config.Unit,
                    slot.Config.Format,
                    slot.Config.HideWhenZero);

                bool visible = !eval.IsHidden;
                if (slot.LastVisible != visible)
                {
                    slot.LastVisible = visible;
                    if (slot.RootGo != null) slot.RootGo.SetActive(visible);
                }
                if (!visible) continue;
                dockAnyVisible = true;

                if (slot.LastValue != curVal || slot.LastMaxValue != maxVal)
                {
                    bool hadPrev = slot.LastValue != int.MinValue;
                    slot.LastValue = curVal;
                    slot.LastMaxValue = maxVal;
                    if (hadPrev)
                    {
                        slot.PunchUntil = now + 0.30f;
                    }
                    UpdateSlotVisuals(slot, curVal, maxVal, eval);
                }
            }

            if (dock.RootGo != null && dock.RootGo.activeSelf != dockAnyVisible)
            {
                dock.RootGo.SetActive(dockAnyVisible);
            }
        }

        TickPunchAnimations(now);
    }

    private bool HasActivePunchAnimation(float now)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            var s = _slots[i];
            if (s.LastVisible && (now < s.PunchUntil || (s.RootRt != null && s.RootRt.localScale.x != 1f)))
            {
                return true;
            }
        }
        return false;
    }

    private void TickPunchAnimations(float now)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (!slot.LastVisible || slot.RootRt == null) continue;
            if (now < slot.PunchUntil)
            {
                float remain = (slot.PunchUntil - now) / 0.30f;
                float punch = 1f + Mathf.Sin(remain * Mathf.PI) * 0.22f;
                slot.RootRt.localScale = new Vector3(punch, punch, 1f);
            }
            else if (slot.RootRt.localScale.x != 1f)
            {
                slot.RootRt.localScale = Vector3.one;
            }
        }
    }

    private void SetAllDocksActive(bool active)
    {
        foreach (var dock in _docks.Values)
        {
            if (dock.RootGo != null && dock.RootGo.activeSelf != active)
            {
                dock.RootGo.SetActive(active);
            }
        }
    }

    private bool EnsureMountParent(
        Component hostComp,
        Component ammoCtrl,
        Component gemCtrl,
        Renderer boundsRenderer,
        Transform hostAnchor)
    {
        Transform targetParent = null;
        int targetLayer = boundsRenderer != null ? boundsRenderer.gameObject.layer : hostComp.gameObject.layer;

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

        if (targetParent == null)
        {
            Transform anchor = hostAnchor != null ? hostAnchor : hostComp.transform;
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

        if (_mountedParent == targetParent && _mountedParent != null)
        {
            return true;
        }

        foreach (var dock in _docks.Values)
        {
            if (dock.RootGo != null) UnityEngine.Object.Destroy(dock.RootGo);
        }
        _docks.Clear();
        _slots.Clear();
        _hasActiveConfig = false;
        _mountedParent = targetParent;
        return true;
    }

    private DockGroupView GetOrCreateDock(BadgeAnchorGroup anchor, int layer)
    {
        if (_docks.TryGetValue(anchor, out var existing) && existing.RootGo != null)
        {
            return existing;
        }

        var go = new GameObject("BazaarCustomMechanismDock_" + anchor, typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        go.transform.SetParent(_mountedParent, worldPositionStays: false);
        go.layer = layer;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);

        var vlg = go.GetComponent<VerticalLayoutGroup>();
        vlg.spacing = 4f;
        vlg.childAlignment = TextAnchor.MiddleCenter;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false;
        vlg.childForceExpandHeight = false;

        var csf = go.GetComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        go.SetActive(false);
        var dock = new DockGroupView { Anchor = anchor, RootGo = go, RootRt = rt };
        _docks[anchor] = dock;
        return dock;
    }

    private void PositionAllDocksOnHostFace(
        Renderer boundsRenderer,
        Transform hostAnchor,
        Component ammoCtrl,
        bool ammoActive)
    {
        if (_mountedParent == null || boundsRenderer == null || _docks.Count == 0) return;

        Transform artTf = boundsRenderer.transform;
        var mf = boundsRenderer.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null && mf.sharedMesh.bounds.size.sqrMagnitude > 0.00001f)
        {
            Bounds lb = mf.sharedMesh.bounds;
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
            Bounds wb = boundsRenderer.bounds;
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

        float targetZ;
        if (ammoCtrl != null && ammoCtrl.transform.parent == _mountedParent)
        {
            targetZ = ammoCtrl.transform.localPosition.z;
        }
        else
        {
            Vector3 frontWorld = hostAnchor != null ? hostAnchor.TransformDirection(Vector3.up) : artTf.up;
            Vector3 frontLocal = _mountedParent.InverseTransformDirection(frontWorld);
            float zSign = frontLocal.z >= 0f ? 1f : -1f;
            float baseZ = zSign >= 0f ? maxZ : minZ;
            targetZ = baseZ + zSign * Mathf.Max(0.01f, artH * 0.012f);
        }

        Quaternion rot = (ammoCtrl != null && ammoCtrl.transform.parent == _mountedParent)
            ? ammoCtrl.transform.localRotation
            : Quaternion.identity;

        float cx = (minX + maxX) * 0.5f;
        float baseTargetHeight = artH * 0.138f;
        float baseScale = baseTargetHeight / LogicalPlaqueHeight;
        // D3-P1-07：Bottom* 组在有弹药条时上抬 +0.18*artH 避让弹药条，TopCenter 组下沉 -0.14*artH 避让顶部宝石
        float bottomRaise = ammoActive ? artH * 0.18f : 0f;

        foreach (var dock in _docks.Values)
        {
            if (dock.RootRt == null || dock.Slots.Count == 0) continue;
            LayoutRebuilder.ForceRebuildLayoutImmediate(dock.RootRt);
            float prefW = dock.RootRt.rect.width;
            float scale = baseScale;
            if (prefW > 1f && prefW * scale > artW * 0.80f)
            {
                scale = (artW * 0.80f) / prefW;
            }
            dock.RootRt.localScale = new Vector3(scale, scale, scale);
            dock.RootRt.localRotation = rot;

            float halfDockW = prefW > 1f ? prefW * scale * 0.5f : artW * 0.24f;
            float posX = cx;
            float posY = minY + artH * 0.095f + bottomRaise;

            switch (dock.Anchor)
            {
                case BadgeAnchorGroup.BottomLeft:
                    posX = minX + halfDockW + artW * 0.045f;
                    posY = minY + artH * 0.105f + bottomRaise;
                    break;
                case BadgeAnchorGroup.BottomRight:
                    posX = maxX - halfDockW - artW * 0.045f;
                    posY = minY + artH * 0.105f + bottomRaise;
                    break;
                case BadgeAnchorGroup.TopLeft:
                    posX = minX + halfDockW + artW * 0.045f;
                    posY = maxY - artH * 0.15f;
                    break;
                case BadgeAnchorGroup.TopRight:
                    posX = maxX - halfDockW - artW * 0.045f;
                    posY = maxY - artH * 0.15f;
                    break;
                case BadgeAnchorGroup.TopCenter:
                    posX = cx;
                    posY = maxY - artH * 0.14f - artH * 0.08f;
                    break;
                default: // BottomCenter
                    posX = cx + artW * 0.055f;
                    posY = minY + artH * 0.095f + bottomRaise;
                    break;
            }

            dock.RootRt.localPosition = new Vector3(posX, posY, targetZ);
        }
    }

    private void RebuildSlots(CardCounterDisplayInfo[] displays, IDictionary attrs, Component gemCtrl)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].RootGo != null) UnityEngine.Object.Destroy(_slots[i].RootGo);
        }
        _slots.Clear();
        foreach (var dock in _docks.Values)
        {
            dock.Slots.Clear();
            if (dock.RootGo != null) dock.RootGo.SetActive(false);
        }

        TMP_FontAsset font = ResolveBadgeFont(gemCtrl);
        int layer = _mountedParent != null ? _mountedParent.gameObject.layer : 0;

        int added = 0;
        for (int i = 0; i < displays.Length && added < MaxBadgeSlots; i++)
        {
            var cd = displays[i];
            if (cd == null || string.IsNullOrWhiteSpace(cd.SyncAttribute)) continue;

            ECardAttributeType? parsedAttr = Enum.TryParse<ECardAttributeType>(cd.SyncAttribute.Trim(), ignoreCase: true, out var ea) ? ea : null;
            ECardAttributeType? parsedMaxAttr = !string.IsNullOrWhiteSpace(cd.MaxAttr) && Enum.TryParse<ECardAttributeType>(cd.MaxAttr.Trim(), ignoreCase: true, out var em) ? em : null;

            int initMax = ResolveEffectiveMax(attrs, parsedMaxAttr, cd.MaxAttr, cd.MaxValue);
            var probe = BadgeFormatEvaluator.Evaluate(0, initMax, cd.Style, cd.ValueType, cd.LabelZh, cd.Unit, cd.Format, hideWhenZero: false);

            BadgeAnchorGroup anchor = ParseAnchorGroup(cd.Position);
            var dock = GetOrCreateDock(anchor, layer);
            Color accent = ParseHexColor(cd.ColorHex, new Color(0.98f, 0.52f, 0.14f, 1f));
            SlotView slot = CreateSlotObject(dock, cd, parsedAttr, parsedMaxAttr, probe, initMax, accent, font, layer);
            dock.Slots.Add(slot);
            _slots.Add(slot);
            added++;
        }
    }

    private static SlotView CreateSlotObject(
        DockGroupView dock,
        CardCounterDisplayInfo cd,
        ECardAttributeType? parsedAttr,
        ECardAttributeType? parsedMaxAttr,
        BadgeFormatResult probe,
        int initMax,
        Color accent,
        TMP_FontAsset font,
        int layer)
    {
        var slot = new SlotView
        {
            Config = cd,
            ParsedAttrKey = parsedAttr,
            ParsedMaxAttrKey = parsedMaxAttr,
            Anchor = dock.Anchor,
            ActiveStyle = probe.EffectiveStyle,
            AccentColor = accent,
        };

        var rootGo = new GameObject("Badge_" + cd.SyncAttribute, typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
        rootGo.transform.SetParent(dock.RootGo.transform, worldPositionStays: false);
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

        bool showSeparateLabel = !probe.SuppressSeparateLabel && !string.IsNullOrWhiteSpace(cd.LabelZh);

        if (probe.EffectiveStyle == "gem")
        {
            bg.sprite = CardBadgeArtLibrary.GetGemSprite();
            bg.color = Color.Lerp(accent, new Color(0.18f, 0.12f, 0.14f, 1f), 0.22f);
            hlg.padding = new RectOffset(10, 10, 2, 2);
            hlg.spacing = 4f;

            if (showSeparateLabel)
            {
                slot.LabelText = CreateBadgeTmp(rootGo.transform, "Label", cd.LabelZh.Trim(), 12.5f, new Color(0.98f, 0.92f, 0.78f, 1f), FontStyles.Bold, font, layer);
            }
            slot.ValueText = CreateBadgeTmp(rootGo.transform, "Val", "0", 16f, Color.white, FontStyles.Bold, font, layer);
        }
        else if (probe.EffectiveStyle == "pips")
        {
            bg.sprite = CardBadgeArtLibrary.GetPlaqueSprite(ready: false);
            bg.color = Color.white;
            hlg.padding = new RectOffset(10, 10, 2, 2);
            hlg.spacing = 5f;

            if (showSeparateLabel)
            {
                slot.LabelText = CreateBadgeTmp(rootGo.transform, "Label", cd.LabelZh.Trim(), 13f, new Color(0.95f, 0.85f, 0.62f, 1f), FontStyles.Bold, font, layer);
            }

            int pipCount = Mathf.Clamp(initMax > 0 ? initMax : 3, 1, 6);
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

            slot.ValueText = CreateBadgeTmp(rootGo.transform, "Val", $"0/{pipCount}", 13.5f, new Color(0.98f, 0.96f, 0.90f, 1f), FontStyles.Bold, font, layer);
        }
        else if (probe.EffectiveStyle == "bar")
        {
            bg.sprite = CardBadgeArtLibrary.GetPlaqueSprite(ready: false);
            bg.color = Color.white;
            hlg.padding = new RectOffset(10, 10, 2, 2);
            hlg.spacing = 6f;

            if (showSeparateLabel)
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

            slot.ValueText = CreateBadgeTmp(rootGo.transform, "Val", $"0/{Math.Max(1, initMax)}", 12.5f, Color.white, FontStyles.Bold, font, layer);
        }
        else
        {
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

            if (showSeparateLabel)
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

        var shadow = go.AddComponent<Shadow>();
        shadow.effectColor = new Color(0.04f, 0.025f, 0.02f, 0.92f);
        shadow.effectDistance = new Vector2(1.1f, -1.2f);
        shadow.useGraphicAlpha = true;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = LogicalPlaqueHeight - 4f;
        return tmp;
    }

    private static void UpdateSlotVisuals(SlotView slot, int rawVal, int maxVal, BadgeFormatResult eval)
    {
        bool hasMax = maxVal > 0;
        bool isReady = hasMax && rawVal >= maxVal;

        if (slot.LabelText != null && slot.LabelText.gameObject.activeSelf == eval.SuppressSeparateLabel)
        {
            slot.LabelText.gameObject.SetActive(!eval.SuppressSeparateLabel);
        }

        if (slot.ValueText != null)
        {
            slot.ValueText.text = eval.DisplayText;
            slot.ValueText.color = isReady
                ? new Color(1.0f, 0.93f, 0.56f, 1f)
                : Color.white;
        }

        if (slot.BgImage != null && slot.ActiveStyle != "gem")
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
            slot.BarFillImage.fillAmount = Mathf.Clamp01((float)rawVal / maxVal);
            slot.BarFillImage.color = isReady ? new Color(1.0f, 0.84f, 0.22f, 1f) : slot.AccentColor;
        }
    }

    /// <summary>零分配 64 位 FNV-1a 哈希：纳入 HideWhenZero、MaxAttr 动态上限与全部配置字段，替代每帧字符串拼接 GC。</summary>
    private static long ComputeDisplayConfigHash(CardCounterDisplayInfo[] displays, IDictionary attrs)
    {
        unchecked
        {
            long hash = -3750763034362895579L;
            const long prime = 1099511628211L;

            int limit = Math.Min(displays.Length, MaxBadgeSlots);
            for (int i = 0; i < limit; i++)
            {
                var d = displays[i];
                if (d == null) continue;
                hash = MixString(hash, d.SyncAttribute, prime);
                hash = MixString(hash, d.LabelZh, prime);
                hash = MixString(hash, d.ColorHex, prime);
                hash = MixString(hash, d.Style, prime);
                hash = MixString(hash, d.ValueType, prime);
                hash = MixString(hash, d.Format, prime);
                hash = MixString(hash, d.Unit, prime);
                hash = MixString(hash, d.Position, prime);
                hash = MixString(hash, d.MaxAttr, prime);
                hash = (hash ^ (d.MaxValue ?? -1)) * prime;
                hash = (hash ^ (d.HideWhenZero ? 1L : 0L)) * prime;

                // 若配置了 MaxAttr 且会导致样式从 pips<->bar<->pill 跳变，把样式桶也混入哈希
                if (!string.IsNullOrWhiteSpace(d.MaxAttr))
                {
                    ECardAttributeType? parsedMax = Enum.TryParse<ECardAttributeType>(d.MaxAttr.Trim(), ignoreCase: true, out var em) ? em : null;
                    int dynMax = ResolveEffectiveMax(attrs, parsedMax, d.MaxAttr, d.MaxValue);
                    int bucket = dynMax <= 0 ? 0 : (dynMax <= 6 ? dynMax : 99);
                    hash = (hash ^ bucket) * prime;
                }
            }
            return hash;
        }
    }

    private static long MixString(long hash, string s, long prime)
    {
        unchecked
        {
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    hash = (hash ^ s[i]) * prime;
                }
            }
            return (hash ^ 0xFF) * prime;
        }
    }

    private static BadgeAnchorGroup ParseAnchorGroup(string positionHint)
    {
        string pos = positionHint?.Trim().ToLowerInvariant() ?? string.Empty;
        return pos switch
        {
            "bottomleft" or "bottom_left" => BadgeAnchorGroup.BottomLeft,
            "bottomright" or "bottom_right" => BadgeAnchorGroup.BottomRight,
            "topleft" or "top_left" => BadgeAnchorGroup.TopLeft,
            "topright" or "top_right" => BadgeAnchorGroup.TopRight,
            "top" or "topcenter" or "top_center" => BadgeAnchorGroup.TopCenter,
            _ => BadgeAnchorGroup.BottomCenter,
        };
    }

    private static int ResolveEffectiveMax(IDictionary attrs, SlotView slot)
    {
        return ResolveEffectiveMax(attrs, slot.ParsedMaxAttrKey, slot.Config.MaxAttr, slot.Config.MaxValue);
    }

    private static int ResolveEffectiveMax(IDictionary attrs, ECardAttributeType? parsedMaxKey, string maxAttrName, int? staticMax)
    {
        if (!string.IsNullOrWhiteSpace(maxAttrName) && attrs != null)
        {
            int dyn = ReadLiveIntAttr(attrs, parsedMaxKey, maxAttrName);
            if (dyn > 0) return dyn;
        }
        return staticMax.HasValue && staticMax.Value > 0 ? staticMax.Value : 0;
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

    private static int ReadLiveIntAttr(IDictionary attrs, ECardAttributeType? precompiledKey, string attrName)
    {
        if (attrs == null || string.IsNullOrEmpty(attrName)) return 0;

        // O(1) 枚举键快速路径（避免每帧枚举 DictionaryEntry 产生装箱分配）
        if (precompiledKey.HasValue)
        {
            if (attrs is Dictionary<ECardAttributeType, int> typedDict)
            {
                return typedDict.TryGetValue(precompiledKey.Value, out int v) ? v : 0;
            }
            if (attrs.Contains(precompiledKey.Value))
            {
                object raw = attrs[precompiledKey.Value];
                if (raw is int iv) return iv;
                if (raw != null && double.TryParse(raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double dv))
                    return (int)Math.Round(dv);
                return 0;
            }
        }

        foreach (DictionaryEntry kv in attrs)
        {
            if (string.Equals(kv.Key?.ToString(), attrName, StringComparison.OrdinalIgnoreCase))
            {
                if (kv.Value is int iv) return iv;
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
