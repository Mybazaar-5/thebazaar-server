using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using BazaarLauncher.Core;

namespace BazaarLauncher;

/// <summary>
/// V15.3 用户反馈 6 项问题客户端专项修复补丁：
///   1. 问题 2：GameplayMessageController（格子满/金币不足/不可出售等浮窗提示）补齐 CJK 中文字体，根治 □□□ 豆腐块；
///   2. 问题 1：BoardManager 补齐 Encounter/LevelUp/Loot/Pedestal 阶段的待删卡前置清理、禁用碰撞体宽度归零防错位、以及通用货架翻牌看门狗；
///   3. 问题 5B：ProcessCardSpawnedEvents 前置解锁 Opponent（及 Player）容器槽位掩码，并在战斗与异步发卡结束后强制揭示对手物品，杜绝对手物品不可见；
///   4. 问题 6：Resume 重连时强制 PlayerSkillPresentationManager.Initialize 批量重建技能、并在加载完成后巡检补救被隐藏/遗漏的手牌物品与技能。
/// </summary>
internal static partial class CompatAuditPatch
{
    private static int _userFeedbackPatchedCount;
    private static int _gameplayMsgFontFixedCount;
    private static int _layoutColliderGuardCount;
    private static int _opponentCardsForceShownCount;
    private static int _resumeCardsRecoveredCount;
    private static int _resumeSkillsRecoveredCount;
    private static int _generalShelfWatchdogSeq;
    private static int _opponentRevealSweepSeq;
    private static int _resumeReconcileSeq;

    // ── GameplayMessageController 反射与私有 CJK 字体缓存 ──
    private static FieldInfo _gmcTextObjectField;
    private static TMP_FontAsset _gmcPrivateOsCjkFont;
    private static bool _gmcOsFontAttempted;

    // ── BoardManager 布局/显隐/对手/技能反射缓存 ──
    private static PropertyInfo _bmCurrentStateProp;
    private static MethodInfo _bmShowOpponentCardsMethod;
    private static MethodInfo _bmSpawnCardsMethod;
    private static FieldInfo _bmCardsBeingSpawnedField;
    private static FieldInfo _bmCardsBeingDealtField;
    private static PropertyInfo _bmIsUpdatingBoardProp;
    private static FieldInfo _bmIsRevealingField;
    private static PropertyInfo _bmIsRevealingProp;
    private static FieldInfo _bmIsLoadingCollectiblesField;
    private static FieldInfo _bmSkillsForPlayerPresField;
    private static FieldInfo _bmSkillsForOpponentPresField;
    private static int _spawnCardsQueueCleanedCount;
    private static PropertyInfo _dataCanWeDisplaySeqNodesProp;
    private static PropertyInfo _dataIsStorageOpenProp;
    private static MethodInfo _cardRemovalHasPendingMethod;
    private static MethodInfo _tiTryProcessPendingRemovalMethod;
    private static MethodInfo _lookupGetSkillProxiesMethod;
    private static MethodInfo _playerSpmInitMethod;
    private static PropertyInfo _ufCardCtrlIsVisibleProp;
    private static MethodInfo _ufCardCtrlShowCardMethod;
    private static FieldInfo _ufCardCtrlAnimatorField;
    private static PropertyInfo _ufCardCtrlAnimatorProp;
    private static FieldInfo _ufAnimCardFaceUpParamField;
    private static MethodInfo _ufAnimatorSetBoolHashMethod;
    private static MethodInfo _ufAnimatorSetBoolStrMethod;
    private static Type _ufUnityColliderType;
    private static PropertyInfo _ufColliderEnabledProp;
    private static MethodInfo _ufPhysicsSyncTransformsMethod;

    /// <summary>D4-P1-8：插件卸载时在主线程释放动态 CJK 字体图集、材质与字体资产 GPU 显存。</summary>
    internal static void ReleasePrivateCjkFontOnMain()
    {
        DestroyTmpFontAssetWithGpuResources(ref _gmcPrivateOsCjkFont);
        _gmcOsFontAttempted = false;
    }

    private static void DestroyTmpFontAssetWithGpuResources(ref TMP_FontAsset fa)
    {
        if (fa == null) return;
        try
        {
            if (fa.atlasTextures != null)
            {
                for (int i = 0; i < fa.atlasTextures.Length; i++)
                {
                    if (fa.atlasTextures[i] != null)
                    {
                        try { UnityEngine.Object.Destroy(fa.atlasTextures[i]); } catch { }
                    }
                }
            }
            if (fa.material != null)
            {
                try { UnityEngine.Object.Destroy(fa.material); } catch { }
            }
            try { UnityEngine.Object.Destroy(fa); } catch { }
        }
        catch { }
        fa = null;
    }

    /// <summary>切场/结束对局/热卸载时递增看门狗序列号，取消全部尚未触发的延迟主线程回调。</summary>
    private static void CancelPendingRunScopedWatchdogs()
    {
        Interlocked.Increment(ref _hour6WatchdogSeq);
        Interlocked.Increment(ref _generalShelfWatchdogSeq);
        Interlocked.Increment(ref _opponentRevealSweepSeq);
        Interlocked.Increment(ref _resumeReconcileSeq);
        CancelWave5Watchdogs();
    }

    private static void PatchUserFeedbackFixes(SafeHarmonyPatcher patcher)
    {
        int before = patcher.SucceededCount;

        // ── A. 问题 2：GameplayMessageController CJK 字体修复（根治格子满提示 □□□）──
        try
        {
            var gmcType = AccessTools.TypeByName("TheBazaar.GameplayMessageController");
            if (gmcType != null)
            {
                _gmcTextObjectField = gmcType.GetField("textObject", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var initMethod = AccessTools.Method(gmcType, "Init");
                if (initMethod != null)
                {
                    patcher.TryPatchMethod(initMethod,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(GameplayMessageInitPostfix)),
                        optional: true,
                        label: "UserFix.GameplayMessageController.Init");
                }
                var showMethod = AccessTools.Method(gmcType, "ShowNotification", new[] { typeof(string) });
                if (showMethod != null)
                {
                    patcher.TryPatchMethod(showMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(GameplayMessageShowNotificationPrefix)),
                        optional: true,
                        label: "UserFix.GameplayMessageController.ShowNotification");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.UserFix.GameplayMessage: " + ex.Message);
        }

        // ── B. 问题 1 & 5B & 6：BoardManager 布局防错位、待删清理、对手显隐与 Resume 技能/手牌修复 ──
        try
        {
            var bmType = GameInspector.FindTypeSafe("BoardManager");
            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
            if (dataType != null)
            {
                _dataCanWeDisplaySeqNodesProp = dataType.GetProperty("CanWeDisplaySequenceNodes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _dataIsStorageOpenProp = dataType.GetProperty("IsStorageOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            if (bmType != null)
            {
                const BindingFlags instFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _bmCurrentStateProp = bmType.GetProperty("CurrentState", instFlags);
                _bmShowOpponentCardsMethod = bmType.GetMethod("ShowOpponentCards", instFlags);
                _bmCardsBeingSpawnedField = bmType.GetField("_cardsBeingSpawned", instFlags);
                _bmCardsBeingDealtField = bmType.GetField("_cardsBeingDealt", instFlags);
                _bmIsUpdatingBoardProp = bmType.GetProperty("IsUpdatingBoard", instFlags);
                // BoardManager.cs:370/374 中 _isRevealing 与 _isLoadingCollectibles 均为私有字段（IsRevealing 为只读属性）
                _bmIsRevealingField = bmType.GetField("_isRevealing", instFlags);
                _bmIsRevealingProp = bmType.GetProperty("IsRevealing", instFlags);
                _bmIsLoadingCollectiblesField = bmType.GetField("_isLoadingCollectibles", instFlags);
                _bmSkillsForPlayerPresField = bmType.GetField("skillsForPlayerPresentation", instFlags);
                _bmSkillsForOpponentPresField = bmType.GetField("skillsForOpponentPresentation", instFlags);

                // 1. 待删卡清理扩展至 Encounter / LevelUp / Loot / Pedestal（防止旧货架卡与新选项/商店卡错位重叠）
                var pendingRemovalMethod = AccessTools.Method(bmType, "ProcessPendingRemovalBeforeChoiceLayout", new[] { typeof(bool) });
                if (pendingRemovalMethod != null)
                {
                    patcher.TryPatchMethod(pendingRemovalMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ProcessPendingRemovalBeforeLayoutPrefix)),
                        optional: true,
                        label: "UserFix.BoardManager.ProcessPendingRemovalBeforeChoiceLayout");
                }

                // 2. 布局宽度计算与等距排版时防护禁用碰撞体（bounds.size.x == 0 导致错位/堆叠）
                var getWidthMethod = bmType.GetMethod("GetCombinedWidthOfCardsToLayout", instFlags);
                if (getWidthMethod != null)
                {
                    patcher.TryPatchMethod(getWidthMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(LayoutGuardCardsPrefix)),
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(LayoutGuardCardsPostfix)),
                        optional: true,
                        label: "UserFix.BoardManager.GetCombinedWidthOfCardsToLayout");
                }
                var layoutEvenMethod = bmType.GetMethod("LayoutChoicesEvenlySpaced", instFlags);
                if (layoutEvenMethod != null)
                {
                    patcher.TryPatchMethod(layoutEvenMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(LayoutGuardCardsPrefix)),
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(LayoutGuardCardsPostfix)),
                        optional: true,
                        label: "UserFix.BoardManager.LayoutChoicesEvenlySpaced");
                }

                // 3. SpawnCards：Resume 期间强制走技能快照批量 Initialize，并挂载 Finalizer 兜底清理 skillsForPlayerPresentation 与 IsUpdatingSkillBoard (P1-11)
                foreach (var m in bmType.GetMethods(instFlags))
                {
                    if (m.Name != "SpawnCards") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 6 && ps[5].ParameterType == typeof(bool))
                    {
                        _bmSpawnCardsMethod = m;
                        patcher.TryPatchMethod(m,
                            prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardManagerSpawnCardsPrefix)),
                            finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardManagerSpawnCardsFinalizer)),
                            optional: true,
                            label: "UserFix.BoardManager.SpawnCards");
                        break;
                    }
                }

                // 4. PlayRevealAnimations 结束后补一次 Resume 手牌与对手物品显隐巡检
                var playRevealMethod = AccessTools.Method(bmType, "PlayRevealAnimations");
                if (playRevealMethod != null)
                {
                    patcher.TryPatchMethod(playRevealMethod,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardManagerPlayRevealPostfix)),
                        optional: true,
                        label: "UserFix.BoardManager.PlayRevealAnimations");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.UserFix.BoardManager: " + ex.Message);
        }

        // ── C. 问题 5B & 6：GameSimHandler.ProcessCardSpawnedEvents 前置解锁槽位掩码 + 通用看门狗 ──
        try
        {
            var gshType = AccessTools.TypeByName("TheBazaar.GameSimHandler");
            if (gshType != null)
            {
                var procSpawned = gshType.GetMethod("ProcessCardSpawnedEvents", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (procSpawned != null)
                {
                    patcher.TryPatchMethod(procSpawned,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ProcessCardSpawnedEventsUnlockPrefix)),
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ProcessCardSpawnedEventsSweepPostfix)),
                        optional: true,
                        label: "UserFix.GameSimHandler.ProcessCardSpawnedEvents");
                }
                var procDealt = gshType.GetMethod("ProcessCardDealtEvents", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (procDealt != null)
                {
                    patcher.TryPatchMethod(procDealt,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ProcessCardDealtGeneralWatchdogPostfix)),
                        optional: true,
                        label: "UserFix.GameSimHandler.ProcessCardDealtEvents");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.UserFix.GameSimHandler: " + ex.Message);
        }

        // ── D. 问题 6：StartRunAppState.FinalizeRunInitialization 结束后执行 Resume 物品与技能完整性自愈 ──
        try
        {
            var startRunType = AccessTools.TypeByName("TheBazaar.StartRunAppState");
            var finalizeMethod = startRunType?.GetMethod("FinalizeRunInitialization", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (finalizeMethod != null)
            {
                patcher.TryPatchMethod(finalizeMethod,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(StartRunFinalizePostfix)),
                    optional: true,
                    label: "UserFix.StartRunAppState.FinalizeRunInitialization");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.UserFix.StartRunAppState: " + ex.Message);
        }

        Interlocked.Exchange(ref _userFeedbackPatchedCount, Math.Max(0, patcher.SucceededCount - before));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  1. 问题 2：GameplayMessageController CJK 字体修复（根治 □□□）
    // ════════════════════════════════════════════════════════════════════════

    private static void GameplayMessageInitPostfix(object __instance)
    {
        EnsureGameplayMessageChineseFont(__instance, "你没有空间放置该物品");
    }

    private static void GameplayMessageShowNotificationPrefix(object __instance, string notificationText)
    {
        if (string.IsNullOrEmpty(notificationText)) return;
        EnsureGameplayMessageChineseFont(__instance, notificationText);
    }

    private static void EnsureGameplayMessageChineseFont(object controller, string sampleText)
    {
        try
        {
            if (controller == null || _gmcTextObjectField == null) return;
            if (_gmcTextObjectField.GetValue(controller) is not TextMeshProUGUI tmp || tmp == null) return;

            bool needsCjk = ContainsCjk(sampleText);
            if (!needsCjk) return;

            // 若当前已挂载本补丁的私有动态 CJK 字体，直接放行避免反复重解析
            if (_gmcPrivateOsCjkFont != null && tmp.font == _gmcPrivateOsCjkFont)
                return;

            // 在 Unity 主线程允许动态字体按需尝试烘焙字形（tryAddCharacter: true），准确判定是否真支持中文
            if (tmp.font != null && FontSupportsAllCjkChars(tmp.font, sampleText, tryAdd: true))
                return;

            var cjkFont = ResolveGameplayMessageCjkFont(sampleText);
            if (cjkFont == null) return;

            if (tmp.font != cjkFont)
            {
                tmp.font = cjkFont;
                tmp.UpdateMeshPadding();
                tmp.SetAllDirty();
                Interlocked.Increment(ref _gameplayMsgFontFixedCount);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.EnsureGameplayMessageChineseFont: " + ex.Message);
        }
    }

    private static bool ContainsCjk(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= '\u4E00' && c <= '\u9FFF') return true;
        }
        return false;
    }

    private static bool FontSupportsAllCjkChars(TMP_FontAsset font, string text, bool tryAdd)
    {
        if (font == null) return false;
        try
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '\u4E00' && c <= '\u9FFF')
                {
                    if (!font.HasCharacter(c, searchFallbacks: true, tryAddCharacter: tryAdd))
                        return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static TMP_FontAsset ResolveGameplayMessageCjkFont(string sampleText)
    {
        string probeText = ContainsCjk(sampleText) ? sampleText : "空间放置";

        // 1. 优先零开销复用原版 NotoFontFallbackRuntime 已挂载 CJK 回退链的原生中文字库（必须实测支持中文，防止英文/未挂载回退链时误返回拉丁字体）
        try
        {
            var notoType = AccessTools.TypeByName("TheBazaar.Localization.NotoFontFallbackRuntime");
            if (notoType != null)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                foreach (var name in new[] { "_loadedSansPrimary", "_loadedSerifPrimary", "_sansPrimary", "_serifPrimary" })
                {
                    var f = notoType.GetField(name, flags);
                    if (f?.GetValue(null) is TMP_FontAsset fa && fa != null
                        && fa.fallbackFontAssetTable != null && fa.fallbackFontAssetTable.Count > 0
                        && FontSupportsAllCjkChars(fa, probeText, tryAdd: true))
                    {
                        return fa;
                    }
                }
            }
        }
        catch { }

        // 2. 复用本插件 UI 已验证可用的中文字体（若 CustomGameplayUI 已初始化）
        try
        {
            var uiFont = CustomGameplayUI.Instance?.GetActiveUiFont();
            if (uiFont != null && FontSupportsAllCjkChars(uiFont, probeText, tryAdd: true))
                return uiFont;
        }
        catch { }

        // 3. 若原版默认字体支持中文则直接复用
        try
        {
            var def = TMP_Settings.defaultFontAsset;
            if (def != null && FontSupportsAllCjkChars(def, probeText, tryAdd: true))
                return def;
        }
        catch { }

        // 4. 隔离私有 OS 动态中文字体兜底（完整配置 Dynamic + 多图集 + 材质着色器 + 实测字形探测，绝不污染全局 TMP 回退表）
        if (_gmcPrivateOsCjkFont != null)
            return _gmcPrivateOsCjkFont;
        if (_gmcOsFontAttempted)
            return null;
        _gmcOsFontAttempted = true;

        try
        {
            string winFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            string[] candidates =
            {
                Path.Combine(winFonts, "msyh.ttc"),
                Path.Combine(winFonts, "msyhbd.ttc"),
                Path.Combine(winFonts, "simhei.ttf"),
                Path.Combine(winFonts, "simsun.ttc"),
            };
            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                TMP_FontAsset fa = null;
                try
                {
                    fa = TMP_FontAsset.CreateFontAsset(path, 0, 36, 4, GlyphRenderMode.SDFAA, 1024, 1024);
                    if (fa == null) continue;
                    fa.name = "BazaarLauncher_GameplayMsg_CJK";
                    fa.hideFlags = HideFlags.HideAndDontSave;
                    fa.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                    fa.isMultiAtlasTexturesEnabled = true;

                    if (fa.material == null || fa.material.shader == null)
                    {
                        Shader shader = TMP_Settings.defaultFontAsset?.material?.shader
                            ?? Shader.Find("TextMeshPro/Mobile/Distance Field")
                            ?? Shader.Find("TextMeshPro/Distance Field");
                        if (shader != null)
                        {
                            var mat = new Material(shader)
                            {
                                name = "BazaarLauncher_GameplayMsg_CJK_Mat",
                                hideFlags = HideFlags.HideAndDontSave,
                            };
                            if (fa.atlasTexture != null)
                                mat.SetTexture(ShaderUtilities.ID_MainTex, fa.atlasTexture);
                            fa.material = mat;
                        }
                    }
                    else
                    {
                        fa.material.hideFlags = HideFlags.HideAndDontSave;
                        if (fa.atlasTexture != null && fa.material.GetTexture(ShaderUtilities.ID_MainTex) == null)
                            fa.material.SetTexture(ShaderUtilities.ID_MainTex, fa.atlasTexture);
                    }

                    if (fa.HasCharacter('空', searchFallbacks: false, tryAddCharacter: true)
                        && fa.HasCharacter('放', searchFallbacks: false, tryAddCharacter: true))
                    {
                        UnityEngine.Object.DontDestroyOnLoad(fa);
                        _gmcPrivateOsCjkFont = fa;
                        return fa;
                    }
                    DestroyTmpFontAssetWithGpuResources(ref fa);
                }
                catch
                {
                    DestroyTmpFontAssetWithGpuResources(ref fa);
                }
            }
        }
        catch { }

        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  2. 问题 1：BoardManager 待删卡前置清理 + 禁用碰撞体防错位 + 通用看门狗
    // ════════════════════════════════════════════════════════════════════════

    private static bool ProcessPendingRemovalBeforeLayoutPrefix(object __instance, bool repositionExistingCards, ref Task __result)
    {
        try
        {
            if (!repositionExistingCards || __instance == null) return true;
            if (_bmCurrentStateProp?.GetValue(__instance) is not ERunState state) return true;

            // 原版仅对 Choice/TournamentPvpChoice 清理待删卡，此处补齐 Encounter / LevelUp / Loot / Pedestal
            if (state is not (ERunState.Encounter or ERunState.LevelUp or ERunState.Loot or ERunState.Pedestal))
                return true;

            var cardRemoval = GetStaticDataMember("CardRemoval");
            if (cardRemoval == null) return true;
            _cardRemovalHasPendingMethod ??= cardRemoval.GetType().GetMethod("HasCardsPendingRemoval", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_cardRemovalHasPendingMethod?.Invoke(cardRemoval, null) is not bool hasPending || !hasPending)
                return true;

            var ti = GetStaticDataMember("TransitionIn");
            if (!IsUnityObjectAlive(ti)) return true;
            _tiTryProcessPendingRemovalMethod ??= ti.GetType().GetMethod("TryProcessCardsPendingRemoval",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(bool), typeof(bool) }, null);
            if (_tiTryProcessPendingRemovalMethod == null) return true;

            if (_tiTryProcessPendingRemovalMethod.Invoke(ti, new object[] { false, false }) is Task task)
            {
                __result = task;
                return false;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ProcessPendingRemovalBeforeLayoutPrefix: " + ex.Message);
        }
        return true;
    }

    private static void LayoutGuardCardsPrefix(object __instance, object[] __args, out List<object> __state)
    {
        __state = null;
        try
        {
            if (__args == null || __args.Length == 0 || __args[0] is not IEnumerable list) return;
            var lookup = GetStaticDataMember("CardAndSkillLookup");
            if (lookup == null) return;
            _getCardControllerMethod ??= lookup.GetType().GetMethod("GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_getCardControllerMethod == null) return;

            _ufUnityColliderType ??= AccessTools.TypeByName("UnityEngine.Collider");
            if (_ufUnityColliderType == null) return;
            _ufColliderEnabledProp ??= _ufUnityColliderType.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
            if (_ufColliderEnabledProp == null) return;

            bool anyEnabled = false;
            foreach (var cardObj in list)
            {
                if (cardObj == null) continue;
                // 仅处理无主货架/选项卡（LeftSocketId == null）
                if (GameInspector.GetMember(cardObj, "LeftSocketId") != null) continue;
                var ctrlObj = _getCardControllerMethod.Invoke(lookup, new[] { cardObj });
                if (ctrlObj is not Component comp || comp == null) continue;
                var col = comp.gameObject.GetComponent(_ufUnityColliderType);
                if (col != null && _ufColliderEnabledProp.GetValue(col) is bool isEn && !isEn)
                {
                    _ufColliderEnabledProp.SetValue(col, true);
                    __state ??= new List<object>(8);
                    __state.Add(col);
                    anyEnabled = true;
                }
            }
            if (anyEnabled)
            {
                if (_ufPhysicsSyncTransformsMethod == null)
                {
                    var physType = AccessTools.TypeByName("UnityEngine.Physics");
                    _ufPhysicsSyncTransformsMethod = physType?.GetMethod("SyncTransforms", BindingFlags.Public | BindingFlags.Static);
                }
                _ufPhysicsSyncTransformsMethod?.Invoke(null, null);
                Interlocked.Increment(ref _layoutColliderGuardCount);
            }
        }
        catch { }
    }

    private static void LayoutGuardCardsPostfix(List<object> __state)
    {
        // D4-P1-12：不依赖 IsStorageOpen，无条件成对还原 Prefix 临时启用的碰撞体，防止货架/选牌卡片在非储物箱态仍残留误开启碰撞体
        try
        {
            if (__state == null || __state.Count == 0 || _ufColliderEnabledProp == null) return;
            for (int i = 0; i < __state.Count; i++)
            {
                var col = __state[i];
                if (IsUnityObjectAlive(col))
                {
                    _ufColliderEnabledProp.SetValue(col, false);
                }
            }
        }
        catch { }
    }

    private static void ProcessCardDealtGeneralWatchdogPostfix()
    {
        ScheduleGeneralShelfRevealWatchdog();
    }

    private static void ScheduleGeneralShelfRevealWatchdog()
    {
        try
        {
            int seq = Interlocked.Increment(ref _generalShelfWatchdogSeq);
            _ = Task.Delay(1350).ContinueWith(_ =>
            {
                if (Volatile.Read(ref _generalShelfWatchdogSeq) != seq) return;
                MainThreadDispatcher.Enqueue(() => RunGeneralShelfRevealWatchdogOnMain(seq));
            }, TaskScheduler.Default);
        }
        catch { }
    }

    private static void RunGeneralShelfRevealWatchdogOnMain(int expectedSeq)
    {
        try
        {
            if (Volatile.Read(ref _generalShelfWatchdogSeq) != expectedSeq) return;
            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is not ERunState runState) return;
            if (runState is not (ERunState.Choice or ERunState.TournamentPvpChoice or ERunState.Encounter or ERunState.LevelUp or ERunState.Loot))
                return;

            if (IsNewDayTransitionActiveOrPending()) return;
            if (_dataCanWeDisplaySeqNodesProp?.GetValue(null) is bool canDisp && !canDisp) return;

            var bm = GameInspector.GetBoardManagerCached();
            if (!IsUnityObjectAlive(bm) || _bmHasCardsToRevealMethod == null) return;
            if (IsBoardManagerBusy(bm)) return;
            if (_bmHasCardsToRevealMethod.Invoke(bm, null) is not bool hasToReveal || !hasToReveal) return;

            var ti = GetStaticDataMember("TransitionIn");
            if (!IsUnityObjectAlive(ti)) return;
            if (_tiIsProcessingProp != null && _tiIsProcessingProp.DeclaringType.IsInstanceOfType(ti)
                && _tiIsProcessingProp.GetValue(ti) is bool proc && proc)
                return;

            if (_tiTryRevealCardsMethod != null && _tiTryRevealCardsMethod.DeclaringType.IsInstanceOfType(ti))
            {
                _ = _tiTryRevealCardsMethod.Invoke(ti, null);
                BazaarLauncherPlugin.Log?.LogInfo($"[UserFix] General shelf reveal watchdog nudged TryRevealCards in state={runState}");
            }
        }
        catch { }
    }

    private static bool IsBoardManagerBusy(object bm)
    {
        try
        {
            if (_bmCardsBeingSpawnedField?.GetValue(bm) is bool bs && bs) return true;
            if (_bmCardsBeingDealtField?.GetValue(bm) is bool bd && bd) return true;
            if (_bmIsUpdatingBoardProp?.GetValue(bm) is bool ub && ub) return true;
            if (_bmIsRevealingField?.GetValue(bm) is bool rvf && rvf) return true;
            if (_bmIsRevealingField == null && _bmIsRevealingProp?.GetValue(bm) is bool rvp && rvp) return true;
            if (_bmIsLoadingCollectiblesField?.GetValue(bm) is bool lc && lc) return true;
        }
        catch { }
        return false;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  3. 问题 5B & 6：槽位掩码前置解锁 + 对手物品必显 + Resume 物品技能自愈
    // ════════════════════════════════════════════════════════════════════════

    private static void ProcessCardSpawnedEventsUnlockPrefix(ref IEnumerable<GameSimEventCardSpawned> events)
    {
        try
        {
            var run = GameInspector.GetDataRun();
            if (run == null) return;

            // 1. 确保 Opponent 的 Hand / Socket 容器始终处于全解锁状态（防止客户端未初始化 UnlockedSockets 导致对手物品被 PlaceOnSocket 拒放）
            var opponent = GameInspector.GetMember(run, "Opponent");
            EnsurePlayerContainerSocketsUnlocked(opponent, 1023);

            // 2. 扫描本批次即将生成的玩家手牌槽位，按物品真实 Size (Small=1/Medium=2/Large=3) 前置补齐全部占用槽位掩码
            if (events != null)
            {
                var materialized = events as IList<GameSimEventCardSpawned> ?? events.ToList();
                events = materialized;

                int neededPlayerHandMask = 0;
                bool hasOpponentSpawn = false;
                for (int i = 0; i < materialized.Count; i++)
                {
                    var ev = materialized[i];
                    if (ev == null) continue;
                    if (ev.CombatantId == ECombatantId.Opponent)
                    {
                        hasOpponentSpawn = true;
                    }
                    else if (ev.CombatantId == ECombatantId.Player &&
                             ev.Section == EInventorySection.Hand &&
                             ev.Socket.HasValue)
                    {
                        int sock = (int)ev.Socket.Value;
                        int width = ResolveSpawnedCardSlotWidth(ev.TemplateId);
                        int endSock = Math.Min(10, sock + width);
                        for (int s = Math.Max(0, sock); s < endSock; s++)
                        {
                            neededPlayerHandMask |= (1 << s);
                        }
                    }
                }

                if (neededPlayerHandMask != 0)
                {
                    var player = GameInspector.GetMember(run, "Player");
                    EnsurePlayerContainerSocketsUnlocked(player, neededPlayerHandMask, unionOnly: true, socketMaskOverride: neededPlayerHandMask);
                }

                if (hasOpponentSpawn)
                {
                    ScheduleOpponentCardsVisibilitySweep();
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ProcessCardSpawnedEventsUnlockPrefix: " + ex.Message);
        }
    }

    private static int ResolveSpawnedCardSlotWidth(string templateId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(templateId) || !Guid.TryParse(templateId, out Guid parsedId))
                return 1;
            var tpl = ResolveCardTemplateById(parsedId);
            if (tpl != null)
            {
                int sz = (int)tpl.Size;
                if (sz >= 1 && sz <= 3) return sz;
            }
        }
        catch { }
        return 1;
    }

    private static void ProcessCardSpawnedEventsSweepPostfix()
    {
        try
        {
            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is ERunState runState &&
                runState is ERunState.PVPCombat or ERunState.Combat)
            {
                ScheduleOpponentCardsVisibilitySweep();
            }
        }
        catch { }
    }

    private static void EnsurePlayerContainerSocketsUnlocked(object playerObj, int requiredMask, bool unionOnly = false, int socketMaskOverride = 1023)
    {
        if (playerObj == null) return;
        try
        {
            if (requiredMask != 0)
            {
                var hand = GameInspector.GetMember(playerObj, "Hand");
                TryUnlockContainerObject(hand, requiredMask, unionOnly);
            }
            if (socketMaskOverride != 0)
            {
                var socket = GameInspector.GetMember(playerObj, "Socket");
                if (socket != null)
                    TryUnlockContainerObject(socket, socketMaskOverride, unionOnly: true);
            }
        }
        catch { }
    }

    private static void TryUnlockContainerObject(object inventoryObj, int requiredMask, bool unionOnly)
    {
        if (inventoryObj == null || requiredMask == 0) return;
        var container = GameInspector.GetMember(inventoryObj, "Container") ?? inventoryObj;
        if (container == null) return;

        // SocketedContainer.UnlockedSockets 在 BazaarGameShared.dll 中是 public ushort 字段（非属性），直接强类型读写零反射损耗
        if (container is SocketedContainer sc)
        {
            ushort cur = sc.UnlockedSockets;
            ushort target = (ushort)(unionOnly ? (cur | requiredMask) : (cur == 0 ? requiredMask : (cur | requiredMask)));
            if (target != cur)
            {
                sc.UnlockedSockets = target;
            }
            return;
        }

        // 兜底：若出现自定义派生容器类型，依次兼容 Field / Property / UnlockSocket(int)
        var t = container.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var field = t.GetField("UnlockedSockets", flags);
        if (field != null)
        {
            object curObj = field.GetValue(container);
            int cur = curObj != null ? Convert.ToInt32(curObj) : 0;
            int target = unionOnly ? (cur | requiredMask) : (cur == 0 ? requiredMask : (cur | requiredMask));
            if (target != cur)
            {
                object converted = field.FieldType.IsEnum
                    ? Enum.ToObject(field.FieldType, target)
                    : Convert.ChangeType(target, field.FieldType);
                field.SetValue(container, converted);
            }
            return;
        }

        var prop = t.GetProperty("UnlockedSockets", flags);
        if (prop != null && prop.CanRead && prop.CanWrite)
        {
            object curObj = prop.GetValue(container);
            int cur = curObj != null ? Convert.ToInt32(curObj) : 0;
            int target = unionOnly ? (cur | requiredMask) : (cur == 0 ? requiredMask : (cur | requiredMask));
            if (target != cur)
            {
                object converted = prop.PropertyType.IsEnum
                    ? Enum.ToObject(prop.PropertyType, target)
                    : Convert.ChangeType(target, prop.PropertyType);
                prop.SetValue(container, converted);
            }
        }
    }

    private static void BoardManagerSpawnCardsPrefix(object __instance, object cards, ref bool rebuildSkillPresentationsFromSnapshot)
    {
        try
        {
            if (rebuildSkillPresentationsFromSnapshot || cards is not IEnumerable list) return;

            // 仅在加载/Resume 初期（CanWeDisplaySequenceNodes == false）且非战斗态、本批含玩家技能时，
            // 确认双侧 SkillPresentationManager 均存活且当前尚未渲染任何玩家技能代理，才强制走 Initialize 批量重建，
            // 既规避逐个 Add() 播放时间轴中断导致技能与后续物品丢失，又避免二次局部批次清空已有技能或触发 OpponentSPM 空引用
            bool canDisplay = _dataCanWeDisplaySeqNodesProp?.GetValue(null) is bool cd && cd;
            if (canDisplay) return;

            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is ERunState runState &&
                runState is ERunState.Combat or ERunState.PVPCombat)
            {
                return;
            }

            if (!IsUnityObjectAlive(GetStaticDataMember("PlayerSkillPresentationManager"))
                || !IsUnityObjectAlive(GetStaticDataMember("OpponentSkillPresenationManager")))
            {
                return;
            }

            var run = GameInspector.GetDataRun();
            var player = GameInspector.GetMember(run, "Player");
            if (player == null) return;

            bool hasPlayerSkill = false;
            foreach (var cardObj in list)
            {
                if (cardObj is ICard ic && ic.Type == ECardType.Skill)
                {
                    var owner = GameInspector.GetMember(cardObj, "Owner");
                    if (ReferenceEquals(owner, player))
                    {
                        hasPlayerSkill = true;
                        break;
                    }
                }
            }

            if (!hasPlayerSkill) return;

            // 若当前已存在已渲染的玩家技能代理，交由 RunResumeIntegritySweepOnMain 按全量实体列表增量校验，避免局部 Initialize 冲掉已有技能
            var lookup = GetStaticDataMember("CardAndSkillLookup");
            if (lookup != null)
            {
                _lookupGetSkillProxiesMethod ??= lookup.GetType().GetMethod("GetSkillProxyRendererCollection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_lookupGetSkillProxiesMethod?.Invoke(lookup, null) is IEnumerable proxies)
                {
                    foreach (var proxy in proxies)
                    {
                        var c = GameInspector.GetMember(proxy, "Card");
                        if (c != null && ReferenceEquals(GameInspector.GetMember(c, "Owner"), player))
                            return;
                    }
                }
            }

            rebuildSkillPresentationsFromSnapshot = true;
            BazaarLauncherPlugin.Log?.LogInfo("[UserFix] Resume SpawnCards: forced rebuildSkillPresentationsFromSnapshot=true for player skills");
        }
        catch { }
    }

    private static Exception BoardManagerSpawnCardsFinalizer(object __instance, Exception __exception, ref Task __result)
    {
        try
        {
            if (__exception != null)
            {
                BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch[P1-11]: BoardManager.SpawnCards 同步异常，兜底清理技能待展现队列并复位 IsUpdatingSkillBoard: {__exception.Message}");
                CleanupSpawnCardsSkillQueuesOnMain(__instance, forceResetSkillUpdating: true);
                __result = Task.CompletedTask;
                return null;
            }

            if (__result == null || __result.IsCompleted)
            {
                bool faulted = __result != null && (__result.IsFaulted || __result.IsCanceled);
                CleanupSpawnCardsSkillQueuesOnMain(__instance, forceResetSkillUpdating: faulted);
                RunPostSpawnCardsCombatOpponentSweep();
                return null;
            }

            __result = WrapBoardManagerSpawnCardsTask(__result, __instance);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.BoardManagerSpawnCardsFinalizer: " + ex.Message);
        }
        return __exception;
    }

    private static async Task WrapBoardManagerSpawnCardsTask(Task inner, object bm)
    {
        bool hadError = false;
        try
        {
            await inner;
        }
        catch (OperationCanceledException)
        {
            hadError = true;
            throw;
        }
        catch (Exception ex)
        {
            hadError = true;
            BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch[P1-11]: BoardManager.SpawnCards 异步异常，兜底清理技能队列: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                CleanupSpawnCardsSkillQueuesOnMain(bm, forceResetSkillUpdating: hadError);
                RunPostSpawnCardsCombatOpponentSweep();
            });
        }
    }

    private static void RunPostSpawnCardsCombatOpponentSweep()
    {
        try
        {
            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is ERunState runState
                && runState is ERunState.PVPCombat or ERunState.Combat)
            {
                EnsureOpponentCardsVisibleOnMain();
            }
        }
        catch { }
    }

    /// <summary>
    /// P1-11 根治：确保 BoardManager.SpawnCards 异常或提前退出后必定清空 skillsForPlayerPresentation / skillsForOpponentPresentation
    /// 并复位 IsUpdatingSkillBoard = false，彻底切断“遗留队列触发滚雪球克隆 7 个钻石技能分身”的恶性循环。
    /// </summary>
    private static void CleanupSpawnCardsSkillQueuesOnMain(object bm, bool forceResetSkillUpdating)
    {
        try
        {
            if (IsUnityObjectAlive(bm))
            {
                bool stillUpdating = _bmIsUpdatingBoardProp?.GetValue(bm) is bool ub && ub;
                if (!stillUpdating || forceResetSkillUpdating)
                {
                    if (_bmSkillsForPlayerPresField?.GetValue(bm) is IList playerSkills && playerSkills.Count > 0)
                    {
                        int staleCount = playerSkills.Count;
                        playerSkills.Clear();
                        int cnt = Interlocked.Increment(ref _spawnCardsQueueCleanedCount);
                        BazaarLauncherPlugin.Log?.LogWarning(
                            $"CompatAuditPatch[P1-11]: SpawnCards 退出后残留 skillsForPlayerPresentation={staleCount}，已兜底清空防技能克隆爆炸 (count={cnt})");
                    }
                    if (_bmSkillsForOpponentPresField?.GetValue(bm) is IList oppSkills && oppSkills.Count > 0)
                    {
                        oppSkills.Clear();
                    }
                }
            }

            if (forceResetSkillUpdating || Volatile.Read(ref _activeSkillAddCount) <= 0)
            {
                ResetSkillBoardUpdatingFlag(GetStaticDataMember("PlayerSkillPresentationManager"));
                ResetSkillBoardUpdatingFlag(GetStaticDataMember("OpponentSkillPresenationManager"));
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CleanupSpawnCardsSkillQueuesOnMain: " + ex.Message);
        }
    }

    private static void BoardManagerPlayRevealPostfix(Task __result)
    {
        try
        {
            if (__result == null || __result.IsCompleted)
            {
                if (_dataCanWeDisplaySeqNodesProp?.GetValue(null) is bool canDisp && canDisp)
                {
                    ScheduleResumeIntegritySweep(350);
                }
                return;
            }

            // PlayRevealAnimations 为 async Task，必须在完整动画结束后再安排巡检，避免 _isRevealing 仍为 true 时过早退出
            __result.ContinueWith(_ =>
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    try
                    {
                        if (_dataCanWeDisplaySeqNodesProp?.GetValue(null) is bool canDisp && canDisp)
                        {
                            ScheduleResumeIntegritySweep(250);
                        }
                    }
                    catch { }
                });
            }, TaskScheduler.Default);
        }
        catch { }
    }

    private static void StartRunFinalizePostfix(Task __result)
    {
        if (__result == null)
        {
            ScheduleResumeIntegritySweep(450, 1300);
            return;
        }
        try
        {
            __result.ContinueWith(_ =>
            {
                ScheduleResumeIntegritySweep(250, 1100);
            }, TaskScheduler.Default);
        }
        catch { }
    }

    private static void ScheduleOpponentCardsVisibilitySweep()
    {
        try
        {
            int seq = Interlocked.Increment(ref _opponentRevealSweepSeq);
            foreach (int delay in new[] { 250, 750, 1400 })
            {
                _ = Task.Delay(delay).ContinueWith(_ =>
                {
                    if (Volatile.Read(ref _opponentRevealSweepSeq) != seq) return;
                    MainThreadDispatcher.Enqueue(EnsureOpponentCardsVisibleOnMain);
                }, TaskScheduler.Default);
            }
        }
        catch { }
    }

    private static void EnsureOpponentCardsVisibleOnMain()
    {
        try
        {
            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is not ERunState runState ||
                runState is not (ERunState.PVPCombat or ERunState.Combat))
            {
                return;
            }

            var bm = GameInspector.GetBoardManagerCached();
            if (!IsUnityObjectAlive(bm)) return;

            var run = GameInspector.GetDataRun();
            var opponent = GameInspector.GetMember(run, "Opponent");
            if (opponent == null) return;

            var entities = GameInspector.GetDataEntities();
            var lookup = GetStaticDataMember("CardAndSkillLookup");
            if (entities == null || lookup == null) return;

            _getCardControllerMethod ??= lookup.GetType().GetMethod("GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_getCardControllerMethod == null) return;

            var hiddenOppCtrls = new List<object>();
            var missingOppCards = new List<object>();

            foreach (DictionaryEntry kv in entities)
            {
                var cardObj = kv.Value;
                if (cardObj is not ICard ic || ic.Type != ECardType.Item) continue;
                var owner = GameInspector.GetMember(cardObj, "Owner");
                if (!ReferenceEquals(owner, opponent)) continue;

                var ctrl = _getCardControllerMethod.Invoke(lookup, new[] { cardObj });
                if (!IsUnityObjectAlive(ctrl))
                {
                    missingOppCards.Add(cardObj);
                    continue;
                }

                EnsureCardControllerVisibleCache(ctrl);
                bool visible = _ufCardCtrlIsVisibleProp?.GetValue(ctrl) is bool v && v;
                if (!visible)
                {
                    hiddenOppCtrls.Add(ctrl);
                }
            }

            if (hiddenOppCtrls.Count > 0)
            {
                // 先调原生 ShowOpponentCards()（其内部按 !IsCardVisible 过滤并同步刷新 Heated/Chilled VFX），再补翻面 Animator 参数
                if (_bmShowOpponentCardsMethod != null)
                {
                    try { _bmShowOpponentCardsMethod.Invoke(bm, null); } catch { }
                }
                for (int i = 0; i < hiddenOppCtrls.Count; i++)
                {
                    ForceShowCardController(hiddenOppCtrls[i]);
                }
                Interlocked.Increment(ref _opponentCardsForceShownCount);
                BazaarLauncherPlugin.Log?.LogInfo($"[UserFix] Forced ShowOpponentCards() for {hiddenOppCtrls.Count} late-spawned opponent item(s)");
            }

            if (missingOppCards.Count > 0 && !IsBoardManagerBusy(bm))
            {
                TryRespawnMissingCards(bm, missingOppCards, rebuildSkills: false);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.EnsureOpponentCardsVisibleOnMain: " + ex.Message);
        }
    }

    private static void ScheduleResumeIntegritySweep(params int[] delaysMs)
    {
        if (delaysMs == null || delaysMs.Length == 0) return;
        try
        {
            int seq = Interlocked.Increment(ref _resumeReconcileSeq);
            for (int i = 0; i < delaysMs.Length; i++)
            {
                int delay = delaysMs[i];
                _ = Task.Delay(delay).ContinueWith(_ =>
                {
                    if (Volatile.Read(ref _resumeReconcileSeq) != seq) return;
                    MainThreadDispatcher.Enqueue(() => RunResumeIntegritySweepOnMain(seq));
                }, TaskScheduler.Default);
            }
        }
        catch { }
    }

    private static void RunResumeIntegritySweepOnMain(int expectedSeq)
    {
        try
        {
            if (Volatile.Read(ref _resumeReconcileSeq) != expectedSeq) return;
            if (_dataCanWeDisplaySeqNodesProp?.GetValue(null) is bool canDisp && !canDisp) return;

            var bm = GameInspector.GetBoardManagerCached();
            if (!IsUnityObjectAlive(bm) || IsBoardManagerBusy(bm)) return;

            var run = GameInspector.GetDataRun();
            var player = GameInspector.GetMember(run, "Player");
            var entities = GameInspector.GetDataEntities();
            var lookup = GetStaticDataMember("CardAndSkillLookup");
            if (player == null || entities == null || lookup == null) return;

            _getCardControllerMethod ??= lookup.GetType().GetMethod("GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_getCardControllerMethod == null) return;

            var missingPlayerItems = new List<object>();
            var allPlayerSkills = new List<object>();
            int revealedHiddenHand = 0;

            foreach (DictionaryEntry kv in entities)
            {
                var cardObj = kv.Value;
                if (cardObj is not ICard ic) continue;
                var owner = GameInspector.GetMember(cardObj, "Owner");
                if (!ReferenceEquals(owner, player)) continue;

                if (ic.Type == ECardType.Skill)
                {
                    allPlayerSkills.Add(cardObj);
                    continue;
                }

                if (ic.Type != ECardType.Item) continue;

                var ctrl = _getCardControllerMethod.Invoke(lookup, new[] { cardObj });
                if (!IsUnityObjectAlive(ctrl))
                {
                    missingPlayerItems.Add(cardObj);
                    continue;
                }

                // 修复 Resume 中因 _playerCardsOnBoard.Clear() 或 PlayRevealAnimations 跳过而卡死在 ShowCard(false) 的玩家手牌
                var secObj = GameInspector.GetMember(cardObj, "Section");
                if (secObj is EInventorySection sec && sec == EInventorySection.Hand)
                {
                    EnsureCardControllerVisibleCache(ctrl);
                    bool visible = _ufCardCtrlIsVisibleProp?.GetValue(ctrl) is bool v && v;
                    if (!visible)
                    {
                        ForceShowCardController(ctrl);
                        revealedHiddenHand++;
                    }
                }
            }

            if (revealedHiddenHand > 0)
            {
                Interlocked.Add(ref _resumeCardsRecoveredCount, revealedHiddenHand);
                BazaarLauncherPlugin.Log?.LogInfo($"[UserFix] Resume sweep revealed {revealedHiddenHand} hidden player hand item(s)");
            }

            if (missingPlayerItems.Count > 0)
            {
                Interlocked.Add(ref _resumeCardsRecoveredCount, missingPlayerItems.Count);
                BazaarLauncherPlugin.Log?.LogInfo($"[UserFix] Resume sweep respawning {missingPlayerItems.Count} missing player item controller(s)");
                TryRespawnMissingCards(bm, missingPlayerItems, rebuildSkills: false);
            }

            // 核验全部玩家技能在 PlayerSkillPresentationManager 中均有对应代理渲染器，缺失则批量 Initialize 重建
            if (allPlayerSkills.Count > 0)
            {
                _lookupGetSkillProxiesMethod ??= lookup.GetType().GetMethod("GetSkillProxyRendererCollection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var existingSkillIds = new HashSet<InstanceId>();
                if (_lookupGetSkillProxiesMethod?.Invoke(lookup, null) is IEnumerable proxies)
                {
                    foreach (var proxy in proxies)
                    {
                        var c = GameInspector.GetMember(proxy, "Card");
                        if (c is ICard pic)
                            existingSkillIds.Add(pic.GetInstanceId());
                    }
                }

                int missingSkills = 0;
                foreach (var sk in allPlayerSkills)
                {
                    if (sk is ICard sic && !existingSkillIds.Contains(sic.GetInstanceId()))
                        missingSkills++;
                }

                if (missingSkills > 0)
                {
                    var spm = GetStaticDataMember("PlayerSkillPresentationManager");
                    if (spm != null)
                    {
                        _playerSpmInitMethod ??= spm.GetType().GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_playerSpmInitMethod != null)
                        {
                            var ps = _playerSpmInitMethod.GetParameters();
                            if (ps.Length == 1 && ps[0].ParameterType.IsGenericType)
                            {
                                var elemType = ps[0].ParameterType.GetGenericArguments()[0];
                                var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType));
                                foreach (var sk in allPlayerSkills)
                                {
                                    if (elemType.IsInstanceOfType(sk))
                                        typedList.Add(sk);
                                }
                                if (typedList.Count > 0)
                                {
                                    _ = _playerSpmInitMethod.Invoke(spm, new object[] { typedList });
                                    Interlocked.Add(ref _resumeSkillsRecoveredCount, missingSkills);
                                    BazaarLauncherPlugin.Log?.LogInfo($"[UserFix] Resume sweep rebuilt {typedList.Count} player skill(s) (missing={missingSkills})");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RunResumeIntegritySweepOnMain: " + ex.Message);
        }
    }

    private static void TryRespawnMissingCards(object bm, List<object> cards, bool rebuildSkills)
    {
        if (bm == null || cards == null || cards.Count == 0 || _bmSpawnCardsMethod == null) return;
        try
        {
            var ps = _bmSpawnCardsMethod.GetParameters();
            if (ps.Length != 6 || !ps[0].ParameterType.IsGenericType) return;
            var elemType = ps[0].ParameterType.GetGenericArguments()[0];
            var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType));
            for (int i = 0; i < cards.Count; i++)
            {
                if (elemType.IsInstanceOfType(cards[i]))
                    typedList.Add(cards[i]);
            }
            if (typedList.Count == 0) return;

            _ = _bmSpawnCardsMethod.Invoke(bm, new object[] { typedList, false, false, true, true, rebuildSkills });
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.TryRespawnMissingCards: " + ex.Message);
        }
    }

    private static void EnsureCardControllerVisibleCache(object ctrl)
    {
        if (ctrl == null) return;
        var t = ctrl.GetType();
        if (_ufCardCtrlIsVisibleProp == null || !_ufCardCtrlIsVisibleProp.DeclaringType.IsInstanceOfType(ctrl))
            _ufCardCtrlIsVisibleProp = t.GetProperty("IsCardVisible", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (_ufCardCtrlShowCardMethod == null || !_ufCardCtrlShowCardMethod.DeclaringType.IsInstanceOfType(ctrl))
            _ufCardCtrlShowCardMethod = t.GetMethod("ShowCard", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
        // CardController.Animator 为 public Animator 字段（CardController.cs:61），优先按继承链查找字段并保留属性兜底
        if (_ufCardCtrlAnimatorField == null || !_ufCardCtrlAnimatorField.DeclaringType.IsInstanceOfType(ctrl))
            _ufCardCtrlAnimatorField = FindFieldInHierarchy(t, "Animator");
        if (_ufCardCtrlAnimatorField == null && (_ufCardCtrlAnimatorProp == null || !_ufCardCtrlAnimatorProp.DeclaringType.IsInstanceOfType(ctrl)))
            _ufCardCtrlAnimatorProp = t.GetProperty("Animator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
    }

    private static void ForceShowCardController(object ctrl)
    {
        if (ctrl == null) return;
        try
        {
            _ufCardCtrlShowCardMethod?.Invoke(ctrl, new object[] { true });
            var anim = _ufCardCtrlAnimatorField != null
                ? _ufCardCtrlAnimatorField.GetValue(ctrl)
                : _ufCardCtrlAnimatorProp?.GetValue(ctrl);
            if (IsUnityObjectAlive(anim))
            {
                if (_ufAnimCardFaceUpParamField == null)
                {
                    var defType = AccessTools.TypeByName("TheBazaar.AnimationParameterDefinitions") ?? GameInspector.FindTypeSafe("AnimationParameterDefinitions");
                    _ufAnimCardFaceUpParamField = defType?.GetField("CardFaceUpParam", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                var paramVal = _ufAnimCardFaceUpParamField?.GetValue(null);
                if (paramVal is int hash)
                {
                    _ufAnimatorSetBoolHashMethod ??= anim.GetType().GetMethod("SetBool", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(bool) }, null);
                    _ufAnimatorSetBoolHashMethod?.Invoke(anim, new object[] { hash, true });
                }
                else if (paramVal is string name && !string.IsNullOrEmpty(name))
                {
                    _ufAnimatorSetBoolStrMethod ??= anim.GetType().GetMethod("SetBool", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(bool) }, null);
                    _ufAnimatorSetBoolStrMethod?.Invoke(anim, new object[] { name, true });
                }
            }
        }
        catch { }
    }
}
