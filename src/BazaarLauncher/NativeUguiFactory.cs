using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace BazaarLauncher;

/// <summary>
/// 原生 Unity uGUI 构建与视觉资产管理（深色巴扎鎏金风）：
/// - 复用游戏内置中文字体 TMP_FontAsset（含 NotoSans / NotoSerif 回退链）与原生 UI 材质；
/// - 仅在初始化时一次性烘焙并缓存 9-slice 圆角卡片/胶囊/边框/圆点 Sprite，隐藏面板时 0 GC、0 布局重算；
/// - 所有控件均采用事件驱动（Button.onClick / Toggle.onValueChanged / Slider.onValueChanged / TMP_InputField.onEndEdit）。
/// </summary>
internal sealed class NativeUguiFactory
{
    // ── 巴扎暗金调色板 ────────────────────────────────────────────────────
    internal static readonly Color ColBackdrop = new(0.02f, 0.02f, 0.04f, 0.74f);
    internal static readonly Color ColPanelBg = new(0.08f, 0.07f, 0.11f, 0.98f);
    internal static readonly Color ColHeaderBg = new(0.14f, 0.10f, 0.06f, 0.99f);
    internal static readonly Color ColCardBg = new(0.12f, 0.10f, 0.16f, 0.98f);
    internal static readonly Color ColSubCardBg = new(0.16f, 0.14f, 0.21f, 0.98f);
    internal static readonly Color ColAmberBg = new(0.20f, 0.14f, 0.06f, 0.96f);
    internal static readonly Color ColInputBg = new(0.05f, 0.05f, 0.08f, 0.99f);

    internal static readonly Color ColGold = new(0.93f, 0.78f, 0.34f, 1f);
    internal static readonly Color ColGoldDim = new(0.70f, 0.56f, 0.20f, 0.92f);
    internal static readonly Color ColGoldBtn = new(0.54f, 0.40f, 0.10f, 1f);
    internal static readonly Color ColGoldBtnHover = new(0.70f, 0.53f, 0.15f, 1f);
    internal static readonly Color ColCream = new(0.96f, 0.92f, 0.84f, 1f);
    internal static readonly Color ColMuted = new(0.68f, 0.65f, 0.60f, 1f);

    internal static readonly Color ColBtnNormal = new(0.21f, 0.18f, 0.26f, 1f);
    internal static readonly Color ColBtnHover = new(0.30f, 0.25f, 0.36f, 1f);
    internal static readonly Color ColBtnDisabled = new(0.13f, 0.12f, 0.16f, 0.85f);

    // 规则启用双态配色：默认青灰 vs 自定义翠绿鎏金
    internal static readonly Color ColDefaultPillBg = new(0.11f, 0.14f, 0.18f, 0.93f);
    internal static readonly Color ColDefaultPillBorder = new(0.36f, 0.44f, 0.50f, 0.88f);
    internal static readonly Color ColDefaultPillText = new(0.78f, 0.84f, 0.88f, 1f);

    internal static readonly Color ColCustomPillBg = new(0.07f, 0.22f, 0.16f, 0.96f);
    internal static readonly Color ColCustomPillBorder = new(0.94f, 0.78f, 0.28f, 1f);
    internal static readonly Color ColCustomPillText = new(0.98f, 0.89f, 0.48f, 1f);
    internal static readonly Color ColEmeraldText = new(0.50f, 0.96f, 0.68f, 1f);
    internal static readonly Color ColDangerBg = new(0.38f, 0.12f, 0.14f, 1f);
    internal static readonly Color ColDangerBorder = new(0.86f, 0.36f, 0.32f, 1f);
    internal static readonly Color ColErrorText = new(1.0f, 0.56f, 0.54f, 1f);

    private TMP_FontAsset _font;
    private bool _hasVerifiedChineseFont;
    private bool _hasNativeChineseFont;
    private UnityEngine.Font _osSystemFont;
    private TMP_FontAsset _osFallbackFont;
    private Material _uiMaterial;
    private Texture2D _texRounded;
    private Texture2D _texBorder;
    private Texture2D _texCircle;
    private Texture2D _texCheck;

    internal Sprite RoundedSprite { get; private set; }
    internal Sprite BorderSprite { get; private set; }
    internal Sprite CircleSprite { get; private set; }
    internal Sprite CheckSprite { get; private set; }

    private readonly List<TMP_Text> _trackedTexts = new(256);

    internal TMP_FontAsset Font => _font;
    internal bool HasVerifiedChineseFont => _hasVerifiedChineseFont;

    internal void EnsureInitialized()
    {
        if (RoundedSprite == null)
        {
            BuildSprites();
        }
        RefreshNativeAssetsIfNeeded();
    }

    internal bool RefreshNativeAssetsIfNeeded(bool forceRescan = false)
    {
        // 仅当已锁定经过验证的原生中文字库时跳过常规刷新；若尚未验证中文或仍在使用系统字体保底，切场景/重开弹窗时允许继续探测原生字库
        if (!forceRescan && _hasVerifiedChineseFont && _font != null && _uiMaterial != null)
        {
            return false;
        }
        if (forceRescan && _hasVerifiedChineseFont && _hasNativeChineseFont && _font != null && _uiMaterial != null)
        {
            return false;
        }

        TMP_FontAsset nextFont = _font;
        bool nextVerified = _hasVerifiedChineseFont && _font != null;
        bool nextNative = _hasNativeChineseFont && _font != null;

        if (!_hasVerifiedChineseFont || !_hasNativeChineseFont || forceRescan || _font == null)
        {
            nextFont = ResolveChineseFont(out nextVerified, out nextNative);
        }

        Material nextMat = (_uiMaterial != null && !forceRescan) ? _uiMaterial : ResolveNativeUiMaterial();
        bool changed = false;

        if (nextFont != null && (nextFont != _font || (nextVerified && !_hasVerifiedChineseFont)))
        {
            _font = nextFont;
            changed = true;
        }
        if (nextFont != null)
        {
            _hasVerifiedChineseFont = nextVerified;
            _hasNativeChineseFont = nextNative;
        }
        else
        {
            _hasVerifiedChineseFont = false;
            _hasNativeChineseFont = false;
        }

        if (nextMat != null && nextMat != _uiMaterial)
        {
            _uiMaterial = nextMat;
        }

        if (changed && _font != null)
        {
            for (int i = _trackedTexts.Count - 1; i >= 0; i--)
            {
                var t = _trackedTexts[i];
                if (t == null)
                {
                    _trackedTexts.RemoveAt(i);
                    continue;
                }
                t.font = _font;
                t.SetAllDirty();
            }
        }
        return changed;
    }

    internal void Dispose()
    {
        try
        {
            var def = TMP_Settings.defaultFontAsset;
            if (def != null && def.fallbackFontAssetTable != null && _osFallbackFont != null)
            {
                def.fallbackFontAssetTable.Remove(_osFallbackFont);
            }
            if (TMP_Settings.fallbackFontAssets != null && _osFallbackFont != null)
            {
                TMP_Settings.fallbackFontAssets.Remove(_osFallbackFont);
            }
        }
        catch { }

        var osMat = _osFallbackFont != null ? _osFallbackFont.material : null;
        DestroyObj(RoundedSprite);
        DestroyObj(BorderSprite);
        DestroyObj(CircleSprite);
        DestroyObj(CheckSprite);
        DestroyObj(_texRounded);
        DestroyObj(_texBorder);
        DestroyObj(_texCircle);
        DestroyObj(_texCheck);
        DestroyObj(_osFallbackFont);
        DestroyObj(osMat);
        DestroyObj(_osSystemFont);
        RoundedSprite = null;
        BorderSprite = null;
        CircleSprite = null;
        CheckSprite = null;
        _texRounded = null;
        _texBorder = null;
        _texCircle = null;
        _texCheck = null;
        _osFallbackFont = null;
        _osSystemFont = null;
        _font = null;
        _hasVerifiedChineseFont = false;
        _hasNativeChineseFont = false;
        _trackedTexts.Clear();
    }

    private static void DestroyObj(UnityEngine.Object obj)
    {
        if (obj != null) UnityEngine.Object.Destroy(obj);
    }

    // ── 字体与原生 UI 材质采集 ──────────────────────────────────────────────

    private static readonly string[] ChineseFontNameHints =
    {
        "SC", "CN", "Hans", "Chinese", "CJK", "Noto", "SourceHan",
    };

    private TMP_FontAsset ResolveChineseFont(out bool isVerifiedChinese, out bool isNativeChinese)
    {
        isVerifiedChinese = false;
        isNativeChinese = false;

        try
        {
            // 无论游戏原生字库是否存在或是否为静默子集，优先构建并挂载真实系统动态中文字体作为三层全局兜底，彻底消灭 □ 缺字
            var osFallback = GetOrCreateOsChineseFontAsset();

            // 1. 扫描场景 TMP_Text，优先复用原生 Sans 中文字库，并在其 fallback 首位挂载 OS 动态字体
            var sceneTexts = UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            TMP_FontAsset chineseTextSans = null;
            TMP_FontAsset chineseTextAny = null;
            TMP_FontAsset otherTextSans = null;
            TMP_FontAsset otherTextAny = null;

            foreach (var t in sceneTexts)
            {
                if (t == null || t.font == null) continue;
                var f = t.font;
                if (!FontSupportsChineseWithoutOsFallback(f)) continue;

                bool hasZhText = ContainsChineseChar(t.text);
                bool isSans = (f.name ?? "").IndexOf("Sans", StringComparison.OrdinalIgnoreCase) >= 0;

                if (hasZhText)
                {
                    if (isSans)
                    {
                        chineseTextSans = f;
                        break;
                    }
                    chineseTextAny ??= f;
                }
                else
                {
                    if (isSans) otherTextSans ??= f;
                    else otherTextAny ??= f;
                }
            }

            TMP_FontAsset chosenNative = chineseTextSans ?? chineseTextAny ?? otherTextSans ?? otherTextAny;
            if (chosenNative != null)
            {
                EnsureOsFallbackAttachedToFont(chosenNative, osFallback);
                EnsureRegisteredInDefaultFallback(osFallback);
                isVerifiedChinese = true;
                isNativeChinese = true;
                return chosenNative;
            }

            // 2. 扫描已加载的 TMP_FontAsset 资源：优先名称包含 SC/CN/Hans/Chinese/CJK/Noto/SourceHan 的字库
            var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            TMP_FontAsset hintedSans = null;
            TMP_FontAsset hintedAny = null;
            TMP_FontAsset generalSans = null;
            TMP_FontAsset generalAny = null;

            foreach (var f in allFonts)
            {
                if (f == null || f == _osFallbackFont) continue;
                if (!FontSupportsChineseWithoutOsFallback(f)) continue;

                string n = f.name ?? "";
                bool isSans = n.IndexOf("Sans", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasHint = ContainsAnyHint(n, ChineseFontNameHints);

                if (hasHint)
                {
                    if (isSans)
                    {
                        hintedSans = f;
                        break;
                    }
                    hintedAny ??= f;
                }
                else
                {
                    if (isSans) generalSans ??= f;
                    else generalAny ??= f;
                }
            }

            chosenNative = hintedSans ?? hintedAny ?? generalSans ?? generalAny;
            if (chosenNative != null)
            {
                EnsureOsFallbackAttachedToFont(chosenNative, osFallback);
                EnsureRegisteredInDefaultFallback(osFallback);
                isVerifiedChinese = true;
                isNativeChinese = true;
                return chosenNative;
            }

            // 3. 核查 TMP 默认字体本身（含原生回退链）是否可常显中文
            var defFont = TMP_Settings.defaultFontAsset;
            if (defFont != null && FontSupportsChineseWithoutOsFallback(defFont))
            {
                EnsureOsFallbackAttachedToFont(defFont, osFallback);
                EnsureRegisteredInDefaultFallback(osFallback);
                isVerifiedChinese = true;
                isNativeChinese = true;
                return defFont;
            }

            // 4. 终极保底：使用系统动态字体作为主字库
            if (osFallback != null && FontSupportsChinese(osFallback))
            {
                EnsureRegisteredInDefaultFallback(osFallback);
                isVerifiedChinese = true;
                isNativeChinese = false;
                return osFallback;
            }

            // 5. 若系统字体亦不可用，暂退回 TMP 默认字体并保持 _hasVerifiedChineseFont=false 以便后续继续重试
            if (defFont != null)
            {
                return defFont;
            }
            foreach (var f in allFonts)
            {
                if (f != null) return f;
            }
        }
        catch { }
        return null;
    }

    private TMP_FontAsset GetOrCreateOsChineseFontAsset()
    {
        if (_osFallbackFont != null)
        {
            EnsureRegisteredInDefaultFallback(_osFallbackFont);
            return _osFallbackFont;
        }

        // Unity 6 (6000.x) 中 Font.CreateDynamicFontFromOSFont 对系统字体报 Unable to load font face，
        // 必须直接通过真实字体文件路径创建可动态光栅化全汉字集的多图集 TMP_FontAsset。
        var candidates = CollectCandidateChineseFontPaths();
        foreach (string fontPath in candidates)
        {
            TMP_FontAsset tmpFont = null;
            try
            {
                tmpFont = TMP_FontAsset.CreateFontAsset(
                    fontPath,
                    0,
                    36,
                    4,
                    GlyphRenderMode.SDFAA,
                    1024,
                    1024);

                if (tmpFont == null) continue;

                tmpFont.name = "BazaarOsChineseFallbackFont";
                tmpFont.hideFlags = HideFlags.HideAndDontSave;
                tmpFont.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                tmpFont.isMultiAtlasTexturesEnabled = true;

                // 确保材质与主贴图完整有效，避免渲染异常或隐形字形
                if (tmpFont.material == null || tmpFont.material.shader == null)
                {
                    Shader fallbackShader = TMP_Settings.defaultFontAsset?.material?.shader
                        ?? Shader.Find("TextMeshPro/Mobile/Distance Field")
                        ?? Shader.Find("TextMeshPro/Distance Field");
                    if (fallbackShader != null)
                    {
                        var mat = new Material(fallbackShader)
                        {
                            name = "BazaarOsChineseFallbackMat",
                            hideFlags = HideFlags.HideAndDontSave,
                        };
                        if (tmpFont.atlasTexture != null)
                        {
                            mat.SetTexture(ShaderUtilities.ID_MainTex, tmpFont.atlasTexture);
                        }
                        tmpFont.material = mat;
                    }
                }
                else
                {
                    tmpFont.material.hideFlags = HideFlags.HideAndDontSave;
                    if (tmpFont.atlasTexture != null && tmpFont.material.GetTexture(ShaderUtilities.ID_MainTex) == null)
                    {
                        tmpFont.material.SetTexture(ShaderUtilities.ID_MainTex, tmpFont.atlasTexture);
                    }
                }

                // 实测游戏静态字库曾缺字的汉字是否可直接动态光栅化，确认无豆腐块后才采纳
                bool probeOk = tmpFont.HasCharacter('槽', searchFallbacks: false, tryAddCharacter: true)
                    && tmpFont.HasCharacter('键', searchFallbacks: false, tryAddCharacter: true)
                    && tmpFont.HasCharacter('倾', searchFallbacks: false, tryAddCharacter: true)
                    && tmpFont.HasCharacter('帧', searchFallbacks: false, tryAddCharacter: true);
                if (!probeOk)
                {
                    DestroyObj(tmpFont.material);
                    DestroyObj(tmpFont);
                    continue;
                }

                _osFallbackFont = tmpFont;
                EnsureRegisteredInDefaultFallback(_osFallbackFont);
                BazaarLauncherPlugin.Log?.LogInfo($"NativeUguiFactory: created Unity6 file-backed dynamic TMP_FontAsset from '{Path.GetFileName(fontPath)}'");
                return _osFallbackFont;
            }
            catch (Exception ex)
            {
                if (tmpFont != null)
                {
                    DestroyObj(tmpFont.material);
                    DestroyObj(tmpFont);
                }
                BazaarLauncherPlugin.Log?.LogWarning($"NativeUguiFactory: failed loading OS font file '{fontPath}': {ex.Message}");
            }
        }

        // 最后保底尝试家族名+样式重载
        try
        {
            var byFamily = TMP_FontAsset.CreateFontAsset("Microsoft YaHei", "Regular", 36);
            if (byFamily != null && byFamily.HasCharacter('槽', searchFallbacks: false, tryAddCharacter: true))
            {
                byFamily.name = "BazaarOsChineseFallbackFont";
                byFamily.hideFlags = HideFlags.HideAndDontSave;
                byFamily.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                byFamily.isMultiAtlasTexturesEnabled = true;
                _osFallbackFont = byFamily;
                EnsureRegisteredInDefaultFallback(_osFallbackFont);
                BazaarLauncherPlugin.Log?.LogInfo("NativeUguiFactory: created family-backed OS Chinese dynamic TMP_FontAsset fallback");
                return _osFallbackFont;
            }
            if (byFamily != null) DestroyObj(byFamily);
        }
        catch { }

        return null;
    }

    private static List<string> CollectCandidateChineseFontPaths()
    {
        var result = new List<string>(8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (File.Exists(path) && seen.Add(path))
                {
                    result.Add(path);
                }
            }
            catch { }
        }

        string fontsDir = "";
        try { fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts); }
        catch { }
        if (string.IsNullOrEmpty(fontsDir))
        {
            fontsDir = @"C:\Windows\Fonts";
        }

        foreach (string fileName in new[] { "msyh.ttc", "msyhbd.ttc", "msyh.ttf", "simhei.ttf", "Deng.ttf", "simsun.ttc", "msjh.ttc" })
        {
            TryAdd(Path.Combine(fontsDir, fileName));
        }

        try
        {
            var osPaths = UnityEngine.Font.GetPathsToOSFonts();
            if (osPaths != null)
            {
                foreach (string p in osPaths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    string fn = Path.GetFileName(p);
                    if (fn.IndexOf("msyh", StringComparison.OrdinalIgnoreCase) >= 0
                        || fn.IndexOf("simhei", StringComparison.OrdinalIgnoreCase) >= 0
                        || fn.IndexOf("deng", StringComparison.OrdinalIgnoreCase) >= 0
                        || fn.IndexOf("yahei", StringComparison.OrdinalIgnoreCase) >= 0
                        || fn.IndexOf("noto", StringComparison.OrdinalIgnoreCase) >= 0
                        || fn.IndexOf("pingfang", StringComparison.OrdinalIgnoreCase) >= 0
                        || fn.IndexOf("simsun", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        TryAdd(p);
                    }
                }
            }
        }
        catch { }

        return result;
    }

    private static void EnsureOsFallbackAttachedToFont(TMP_FontAsset targetFont, TMP_FontAsset osFallback)
    {
        if (targetFont == null || osFallback == null || targetFont == osFallback) return;
        try
        {
            targetFont.fallbackFontAssetTable ??= new List<TMP_FontAsset>();
            if (!targetFont.fallbackFontAssetTable.Contains(osFallback))
            {
                // 插入首位确保比损坏的备用回退更早兜底补字，同时不影响主字体的已有汉字
                targetFont.fallbackFontAssetTable.Insert(0, osFallback);
            }
        }
        catch { }
    }

    private static void EnsureRegisteredInDefaultFallback(TMP_FontAsset osFontAsset)
    {
        if (osFontAsset == null) return;
        try
        {
            var def = TMP_Settings.defaultFontAsset;
            if (def != null)
            {
                def.fallbackFontAssetTable ??= new List<TMP_FontAsset>();
                if (!def.fallbackFontAssetTable.Contains(osFontAsset))
                {
                    def.fallbackFontAssetTable.Add(osFontAsset);
                }
            }

            var globalFallbacks = TMP_Settings.fallbackFontAssets;
            if (globalFallbacks != null && !globalFallbacks.Contains(osFontAsset))
            {
                globalFallbacks.Add(osFontAsset);
            }
        }
        catch { }
    }

    private bool FontSupportsChineseWithoutOsFallback(TMP_FontAsset font)
    {
        if (font == null || font == _osFallbackFont) return false;

        // 若当前字体或 TMP_Settings.defaultFontAsset 的回退链挂有自建 OS 保底字体，先暂移后判定，避免把纯拉丁字库误判为原生中文字库
        List<TMP_FontAsset> table = font.fallbackFontAssetTable;
        int removedIdx = -1;
        if (_osFallbackFont != null && table != null)
        {
            removedIdx = table.IndexOf(_osFallbackFont);
            if (removedIdx >= 0)
            {
                table.RemoveAt(removedIdx);
            }
        }

        var def = TMP_Settings.defaultFontAsset;
        List<TMP_FontAsset> defTable = (def != null && def != font) ? def.fallbackFontAssetTable : null;
        int defRemovedIdx = -1;
        if (_osFallbackFont != null && defTable != null)
        {
            defRemovedIdx = defTable.IndexOf(_osFallbackFont);
            if (defRemovedIdx >= 0)
            {
                defTable.RemoveAt(defRemovedIdx);
            }
        }

        try
        {
            return FontSupportsChinese(font);
        }
        finally
        {
            if (defRemovedIdx >= 0 && defTable != null && _osFallbackFont != null)
            {
                if (defRemovedIdx <= defTable.Count) defTable.Insert(defRemovedIdx, _osFallbackFont);
                else defTable.Add(_osFallbackFont);
            }
            if (removedIdx >= 0 && table != null && _osFallbackFont != null)
            {
                if (removedIdx <= table.Count) table.Insert(removedIdx, _osFallbackFont);
                else table.Add(_osFallbackFont);
            }
        }
    }

    private static bool ContainsChineseChar(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch >= '\u4E00' && ch <= '\u9FFF') return true;
        }
        return false;
    }

    private static bool ContainsAnyHint(string text, string[] hints)
    {
        if (string.IsNullOrEmpty(text) || hints == null) return false;
        for (int i = 0; i < hints.Length; i++)
        {
            if (text.IndexOf(hints[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool FontSupportsChinese(TMP_FontAsset font)
    {
        if (font == null) return false;
        try
        {
            // 测试常用字及静态子集易缺汉字，确认主字库或已挂载 OS 动态回退可完整出字（消除 □ 豆腐块）
            return font.HasCharacter('规', searchFallbacks: true, tryAddCharacter: true)
                   && font.HasCharacter('则', searchFallbacks: true, tryAddCharacter: true)
                   && font.HasCharacter('槽', searchFallbacks: true, tryAddCharacter: true)
                   && font.HasCharacter('倾', searchFallbacks: true, tryAddCharacter: true)
                   && font.HasCharacter('帧', searchFallbacks: true, tryAddCharacter: true);
        }
        catch
        {
            return false;
        }
    }

    private static Material ResolveNativeUiMaterial()
    {
        try
        {
            return Graphic.defaultGraphicMaterial;
        }
        catch
        {
            return null;
        }
    }

    // ── 一次性 9-slice 圆角贴图生成 ────────────────────────────────────────

    private void BuildSprites()
    {
        const int size = 32;
        const int radius = 8;

        _texRounded = CreateRoundedRectTexture(size, radius, borderWidth: 0, fill: true);
        RoundedSprite = Sprite.Create(_texRounded, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect, new Vector4(radius + 2, radius + 2, radius + 2, radius + 2));
        RoundedSprite.hideFlags = HideFlags.HideAndDontSave;

        _texBorder = CreateRoundedRectTexture(size, radius, borderWidth: 2, fill: false);
        BorderSprite = Sprite.Create(_texBorder, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect, new Vector4(radius + 2, radius + 2, radius + 2, radius + 2));
        BorderSprite.hideFlags = HideFlags.HideAndDontSave;

        _texCircle = CreateCircleTexture(24);
        CircleSprite = Sprite.Create(_texCircle, new Rect(0, 0, 24, 24), new Vector2(0.5f, 0.5f), 100f);
        CircleSprite.hideFlags = HideFlags.HideAndDontSave;

        _texCheck = CreateCheckmarkTexture(24);
        CheckSprite = Sprite.Create(_texCheck, new Rect(0, 0, 24, 24), new Vector2(0.5f, 0.5f), 100f);
        CheckSprite.hideFlags = HideFlags.HideAndDontSave;
    }

    private static Texture2D CreateRoundedRectTexture(int size, int radius, int borderWidth, bool fill)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        float half = size * 0.5f;
        float r = radius;
        float innerHalf = half - r;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float px = Mathf.Abs((x + 0.5f) - half);
                float py = Mathf.Abs((y + 0.5f) - half);
                float qx = Mathf.Max(px - innerHalf, 0f);
                float qy = Mathf.Max(py - innerHalf, 0f);
                float dist = Mathf.Sqrt(qx * qx + qy * qy) + Mathf.Min(Mathf.Max(px - innerHalf, py - innerHalf), 0f) - r;

                float alpha;
                if (fill)
                {
                    alpha = Mathf.Clamp01(0.5f - dist);
                }
                else
                {
                    float outerA = Mathf.Clamp01(0.5f - dist);
                    float innerA = Mathf.Clamp01(0.5f - (dist + borderWidth));
                    alpha = Mathf.Clamp01(outerA - innerA);
                }
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return tex;
    }

    private static Texture2D CreateCircleTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        float c = size * 0.5f;
        float r = c - 1.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) - c;
                float dy = (y + 0.5f) - c;
                float dist = Mathf.Sqrt(dx * dx + dy * dy) - r;
                float a = Mathf.Clamp01(0.5f - dist);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return tex;
    }

    private static Texture2D CreateCheckmarkTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)(size - 1);
                float v = y / (float)(size - 1);
                // 两段勾选折线距离：(0.20, 0.52) -> (0.42, 0.28) -> (0.82, 0.74)
                float d1 = DistanceToSegment(u, v, 0.20f, 0.50f, 0.42f, 0.27f);
                float d2 = DistanceToSegment(u, v, 0.42f, 0.27f, 0.82f, 0.74f);
                float d = Mathf.Min(d1, d2);
                float a = Mathf.Clamp01(1f - Mathf.Max(0f, (d - 0.075f) * 28f));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return tex;
    }

    private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        float vx = bx - ax;
        float vy = by - ay;
        float wx = px - ax;
        float wy = py - ay;
        float lenSq = vx * vx + vy * vy;
        float t = lenSq > 1e-6f ? Mathf.Clamp01((wx * vx + wy * vy) / lenSq) : 0f;
        float dx = px - (ax + t * vx);
        float dy = py - (ay + t * vy);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    // ── Canvas 与容器原语 ─────────────────────────────────────────────────

    internal Canvas CreateOverlayCanvas(string name, Transform parent, int sortingOrder)
    {
        EnsureInitialized();
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        canvas.pixelPerfect = false;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        var raycaster = go.AddComponent<GraphicRaycaster>();
        raycaster.ignoreReversedGraphics = true;
        raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.None;
        return canvas;
    }

    internal GameObject CreateBox(string name, Transform parent, Color bgColor, bool rounded = true, bool raycastTarget = true)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, worldPositionStays: false);
        var img = go.AddComponent<Image>();
        img.color = bgColor;
        img.raycastTarget = raycastTarget;
        if (_uiMaterial != null) img.material = _uiMaterial;
        if (rounded && RoundedSprite != null)
        {
            img.sprite = RoundedSprite;
            img.type = Image.Type.Sliced;
        }
        return go;
    }

    internal Image AddOutlineOverlay(Transform parent, Color borderColor)
    {
        var go = new GameObject("Outline", typeof(RectTransform));
        go.transform.SetParent(parent, worldPositionStays: false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // 严格脱离 VerticalLayoutGroup / HorizontalLayoutGroup 排版，避免在卡片顶部多占一行空槽位
        var le = go.AddComponent<LayoutElement>();
        le.ignoreLayout = true;

        var img = go.AddComponent<Image>();
        img.sprite = BorderSprite;
        img.type = Image.Type.Sliced;
        img.color = borderColor;
        img.raycastTarget = false;
        if (_uiMaterial != null) img.material = _uiMaterial;
        return img;
    }

    internal GameObject CreateCard(
        string name,
        Transform parent,
        Color bgColor,
        Color? borderColor = null,
        int padH = 12,
        int padV = 10,
        float spacing = 6f)
    {
        var card = CreateBox(name, parent, bgColor, rounded: true, raycastTarget: true);
        if (borderColor.HasValue)
        {
            AddOutlineOverlay(card.transform, borderColor.Value);
        }
        var vlg = card.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(padH, padH, padV, padV);
        vlg.spacing = spacing;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        return card;
    }

    internal GameObject CreateHorizontalRow(string name, Transform parent, float spacing = 8f, int padH = 0, int padV = 0)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, worldPositionStays: false);
        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = spacing;
        hlg.padding = new RectOffset(padH, padH, padV, padV);
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;
        return go;
    }

    internal GameObject CreateVerticalGroup(string name, Transform parent, float spacing = 6f, int padH = 0, int padV = 0)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, worldPositionStays: false);
        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = spacing;
        vlg.padding = new RectOffset(padH, padH, padV, padV);
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        return go;
    }

    internal static LayoutElement SetLayout(GameObject go, float minW = -1f, float prefW = -1f, float flexW = -1f, float minH = -1f, float prefH = -1f, float flexH = -1f)
    {
        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        if (minW >= 0f) le.minWidth = minW;
        if (prefW >= 0f) le.preferredWidth = prefW;
        if (flexW >= 0f) le.flexibleWidth = flexW;
        if (minH >= 0f) le.minHeight = minH;
        if (prefH >= 0f) le.preferredHeight = prefH;
        if (flexH >= 0f) le.flexibleHeight = flexH;
        return le;
    }

    // ── TextMeshPro 文本与按钮 / 开关 / 输入框 / 滑条 ─────────────────────

    internal TextMeshProUGUI CreateText(
        string name,
        Transform parent,
        string text,
        float fontSize,
        Color color,
        FontStyles style = FontStyles.Normal,
        TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft,
        bool wrap = true,
        bool raycastTarget = false)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, worldPositionStays: false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (_font != null) tmp.font = _font;
        tmp.text = text ?? "";
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.fontStyle = style;
        tmp.alignment = align;
        tmp.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.raycastTarget = raycastTarget;
        _trackedTexts.Add(tmp);
        return tmp;
    }

    internal sealed class ButtonHandle
    {
        public GameObject Root;
        public Button Button;
        public Image Background;
        public Image Border;
        public TextMeshProUGUI Label;
    }

    internal ButtonHandle CreateButton(
        string name,
        Transform parent,
        string label,
        float fontSize,
        Color normalBg,
        Color hoverBg,
        Color textColor,
        UnityAction onClick,
        Color? borderColor = null,
        FontStyles fontStyle = FontStyles.Normal,
        TextAlignmentOptions align = TextAlignmentOptions.Center,
        int padH = 12,
        int padV = 4)
    {
        var go = CreateBox(name, parent, Color.white, rounded: true, raycastTarget: true);
        var bg = go.GetComponent<Image>();
        bg.color = Color.white;

        Image border = null;
        if (borderColor.HasValue)
        {
            border = AddOutlineOverlay(go.transform, borderColor.Value);
        }

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = bg;
        ApplyButtonColors(btn, normalBg, hoverBg);
        if (onClick != null) btn.onClick.AddListener(onClick);

        var txt = CreateText("Label", go.transform, label, fontSize, textColor, fontStyle, align, wrap: true, raycastTarget: false);
        var trt = txt.rectTransform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(padH, padV);
        trt.offsetMax = new Vector2(-padH, -padV);

        return new ButtonHandle
        {
            Root = go,
            Button = btn,
            Background = bg,
            Border = border,
            Label = txt,
        };
    }

    internal static void ApplyButtonColors(Button btn, Color normalBg, Color hoverBg)
    {
        if (btn == null) return;
        var cb = btn.colors;
        cb.normalColor = normalBg;
        cb.highlightedColor = hoverBg;
        cb.pressedColor = Color.Lerp(hoverBg, Color.white, 0.15f);
        cb.selectedColor = hoverBg;
        cb.disabledColor = ColBtnDisabled;
        cb.colorMultiplier = 1f;
        cb.fadeDuration = 0.08f;
        btn.colors = cb;
    }

    internal sealed class ToggleHandle
    {
        public GameObject Root;
        public Toggle Toggle;
        public Image BoxBg;
        public Image Checkmark;
        public TextMeshProUGUI Label;
    }

    internal ToggleHandle CreateToggle(
        string name,
        Transform parent,
        string label,
        bool initialValue,
        Action<bool> onChanged,
        float fontSize = 15f)
    {
        var row = CreateHorizontalRow(name, parent, spacing: 10f);
        var toggle = row.AddComponent<Toggle>();

        var boxGo = CreateBox("Box", row.transform, ColInputBg, rounded: true, raycastTarget: true);
        SetLayout(boxGo, minW: 24f, prefW: 24f, minH: 24f, prefH: 24f);
        AddOutlineOverlay(boxGo.transform, ColGoldDim);
        var boxImg = boxGo.GetComponent<Image>();

        var checkGo = new GameObject("Checkmark", typeof(RectTransform));
        checkGo.transform.SetParent(boxGo.transform, worldPositionStays: false);
        var crt = (RectTransform)checkGo.transform;
        crt.anchorMin = new Vector2(0.14f, 0.14f);
        crt.anchorMax = new Vector2(0.86f, 0.86f);
        crt.offsetMin = Vector2.zero;
        crt.offsetMax = Vector2.zero;
        var checkImg = checkGo.AddComponent<Image>();
        checkImg.sprite = CheckSprite;
        checkImg.color = ColGold;
        checkImg.raycastTarget = false;

        var txt = CreateText("Label", row.transform, label, fontSize, ColCream, FontStyles.Normal, TextAlignmentOptions.MidlineLeft, wrap: true, raycastTarget: true);
        SetLayout(txt.gameObject, flexW: 1f, minH: 26f);

        toggle.targetGraphic = boxImg;
        toggle.graphic = checkImg;
        toggle.SetIsOnWithoutNotify(initialValue);
        if (onChanged != null)
        {
            toggle.onValueChanged.AddListener(v => onChanged(v));
        }

        return new ToggleHandle
        {
            Root = row,
            Toggle = toggle,
            BoxBg = boxImg,
            Checkmark = checkImg,
            Label = txt,
        };
    }

    internal TMP_InputField CreateInputField(
        string name,
        Transform parent,
        string placeholderText,
        string initialText,
        float fontSize = 15f,
        int charLimit = 48,
        TMP_InputField.ContentType contentType = TMP_InputField.ContentType.Standard)
    {
        var root = CreateBox(name, parent, ColInputBg, rounded: true, raycastTarget: true);
        AddOutlineOverlay(root.transform, new Color(0.45f, 0.37f, 0.18f, 0.9f));

        var viewport = new GameObject("Text Area", typeof(RectTransform));
        viewport.transform.SetParent(root.transform, worldPositionStays: false);
        var vrt = (RectTransform)viewport.transform;
        vrt.anchorMin = Vector2.zero;
        vrt.anchorMax = Vector2.one;
        vrt.offsetMin = new Vector2(8f, 3f);
        vrt.offsetMax = new Vector2(-8f, -3f);
        viewport.AddComponent<RectMask2D>();

        var ph = CreateText("Placeholder", viewport.transform, placeholderText ?? "", fontSize, ColMuted, FontStyles.Italic, TextAlignmentOptions.MidlineLeft, wrap: false, raycastTarget: false);
        StretchFill(ph.rectTransform);

        var txt = CreateText("Text", viewport.transform, initialText ?? "", fontSize, ColCream, FontStyles.Normal, TextAlignmentOptions.MidlineLeft, wrap: false, raycastTarget: false);
        StretchFill(txt.rectTransform);

        var input = root.AddComponent<TMP_InputField>();
        input.targetGraphic = root.GetComponent<Image>();
        input.textViewport = vrt;
        input.textComponent = txt;
        input.placeholder = ph;
        input.characterLimit = charLimit;
        input.contentType = contentType;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.caretColor = ColGold;
        input.selectionColor = new Color(0.83f, 0.69f, 0.22f, 0.35f);
        input.SetTextWithoutNotify(initialText ?? "");
        return input;
    }

    internal Slider CreateSlider(string name, Transform parent, float min, float max, float value, Action<float> onChanged)
    {
        var root = new GameObject(name, typeof(RectTransform));
        root.transform.SetParent(parent, worldPositionStays: false);

        var bgGo = CreateBox("Background", root.transform, ColInputBg, rounded: true, raycastTarget: true);
        var bgRt = (RectTransform)bgGo.transform;
        bgRt.anchorMin = new Vector2(0f, 0.32f);
        bgRt.anchorMax = new Vector2(1f, 0.68f);
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        var fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(root.transform, worldPositionStays: false);
        var fart = (RectTransform)fillArea.transform;
        fart.anchorMin = new Vector2(0f, 0.32f);
        fart.anchorMax = new Vector2(1f, 0.68f);
        fart.offsetMin = new Vector2(4f, 0f);
        fart.offsetMax = new Vector2(-4f, 0f);

        var fillGo = CreateBox("Fill", fillArea.transform, ColGoldDim, rounded: true, raycastTarget: false);
        StretchFill((RectTransform)fillGo.transform);

        var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(root.transform, worldPositionStays: false);
        var hart = (RectTransform)handleArea.transform;
        hart.anchorMin = Vector2.zero;
        hart.anchorMax = Vector2.one;
        hart.offsetMin = new Vector2(8f, 0f);
        hart.offsetMax = new Vector2(-8f, 0f);

        var handleGo = new GameObject("Handle", typeof(RectTransform));
        handleGo.transform.SetParent(handleArea.transform, worldPositionStays: false);
        var handleRt = (RectTransform)handleGo.transform;
        handleRt.sizeDelta = new Vector2(18f, 18f);
        var handleImg = handleGo.AddComponent<Image>();
        handleImg.sprite = CircleSprite;
        handleImg.color = ColGold;

        var slider = root.AddComponent<Slider>();
        slider.fillRect = (RectTransform)fillGo.transform;
        slider.handleRect = handleRt;
        slider.targetGraphic = handleImg;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = min;
        slider.maxValue = max;
        slider.SetValueWithoutNotify(value);
        if (onChanged != null)
        {
            slider.onValueChanged.AddListener(v => onChanged(v));
        }
        return slider;
    }

    internal sealed class ScrollViewHandle
    {
        public GameObject Root;
        public ScrollRect ScrollRect;
        public RectTransform Content;
    }

    internal ScrollViewHandle CreateScrollView(string name, Transform parent)
    {
        var root = new GameObject(name, typeof(RectTransform));
        root.transform.SetParent(parent, worldPositionStays: false);
        var rootImg = root.AddComponent<Image>();
        rootImg.color = new Color(0f, 0f, 0f, 0.01f);
        rootImg.raycastTarget = true;

        var viewport = new GameObject("Viewport", typeof(RectTransform));
        viewport.transform.SetParent(root.transform, worldPositionStays: false);
        var vrt = (RectTransform)viewport.transform;
        vrt.anchorMin = Vector2.zero;
        vrt.anchorMax = Vector2.one;
        vrt.offsetMin = Vector2.zero;
        vrt.offsetMax = new Vector2(-14f, 0f);
        viewport.AddComponent<RectMask2D>();

        var content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(viewport.transform, worldPositionStays: false);
        var crt = (RectTransform)content.transform;
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot = new Vector2(0.5f, 1f);
        crt.offsetMin = new Vector2(0f, 0f);
        crt.offsetMax = new Vector2(0f, 0f);

        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 10f;
        vlg.padding = new RectOffset(4, 6, 4, 12);
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        var csf = content.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // 垂直滚动条
        var sbGo = CreateBox("Scrollbar", root.transform, new Color(0.06f, 0.05f, 0.09f, 0.85f), rounded: true, raycastTarget: true);
        var sbrt = (RectTransform)sbGo.transform;
        sbrt.anchorMin = new Vector2(1f, 0f);
        sbrt.anchorMax = new Vector2(1f, 1f);
        sbrt.pivot = new Vector2(1f, 0.5f);
        sbrt.offsetMin = new Vector2(-10f, 2f);
        sbrt.offsetMax = new Vector2(-2f, -2f);

        var slidingArea = new GameObject("Sliding Area", typeof(RectTransform));
        slidingArea.transform.SetParent(sbGo.transform, worldPositionStays: false);
        StretchFill((RectTransform)slidingArea.transform);

        var handleGo = CreateBox("Handle", slidingArea.transform, ColGoldDim, rounded: true, raycastTarget: true);
        StretchFill((RectTransform)handleGo.transform);

        var scrollbar = sbGo.AddComponent<Scrollbar>();
        scrollbar.handleRect = (RectTransform)handleGo.transform;
        scrollbar.targetGraphic = handleGo.GetComponent<Image>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;

        var sr = root.AddComponent<ScrollRect>();
        sr.viewport = vrt;
        sr.content = crt;
        sr.horizontal = false;
        sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 42f;
        sr.verticalScrollbar = scrollbar;
        sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;

        return new ScrollViewHandle
        {
            Root = root,
            ScrollRect = sr,
            Content = crt,
        };
    }

    internal static void StretchFill(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
}
