using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using HarmonyLib;
using UnityEngine;

namespace BazaarLauncher;

/// <summary>
/// 全解锁与 396 套藏品按需懒加载模块（修复 P1-7 与 Wave 1 审计 2 处死补丁 + 1 处多重载未锁签名）：
///   1. 彻底消除 CollectionManager.Awake / Start 2 处现存死补丁与不存在的 TheBazaar.ProfileData.PlayerProfile；
///   2. 精确按参数类型锁定 HeroManager.TryGetHeroSO(EHero, out HeroSO) 与 TryGetHeroSO(string, out HeroSO) 两个重载；
///   3. 去除与 NoSteam 重复的 SteamDLCService / SteamApps 补丁；
///   4. 扫描所有权方法时严格跳过含 out/ref 参数的方法，避免下游 NRE；
///   5. 后台回调统一通过 MainThreadDispatcher 派发至主线程，并对加载的 Addressables 句柄进行托管释放。
/// </summary>
internal static class Unlocker
{
    private static bool _applied;

    private static readonly object _lazySync = new();
    private static readonly HashSet<string> _lazyOnDemandLoading = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<object> _trackedAddressableHandles = new();
    private static FieldInfo _cmIsLoadingField;
    private static FieldInfo _cmLoadCtsField;
    private static FieldInfo _cmSaleMapField;
    private static FieldInfo _cmUnavailableField;
    private static MethodInfo _cmRebuildEquippedMethod;
    private static MethodInfo _cmLoadItemAsyncMethod;

    private static readonly string[] Hero8SoAddressablesKeys =
    {
        "Hero_DRA",
        "Hero_DRA.asset",
    };

    private static readonly string[] Hero8SkinAddressablesKeys =
    {
        "5fa7632e55d2f6349876e52f931cf6d9",
        "Skin_DRA_01/A",
        "Skin_DRA_01/Skin_DRA_01a_SkinData.asset",
        "Skin_DRA_01a_SkinData.asset",
        "b91b7e1e7c7c4494388b4d1d9a1a9606",
    };

    private static bool _hero8SoLogged;
    private static bool _hero8SkinLogged;

    internal static void ResetAppliedFlag()
    {
        _applied = false;
        _hero8SoLogged = false;
        _hero8SkinLogged = false;
        ReleaseTrackedAddressableHandles();
    }

    internal static void Apply(SafeHarmonyPatcher patcher)
    {
        if (_applied || patcher == null) return;
        _applied = true;

        int patched = 0;

        // 藏品 / 皮肤 / 装扮 / 英雄所有权白名单判定（去除与 NoSteam 重复项和已不存在类型，严格跳过 out/ref 参数方法）
        var returnTrueHook = AccessTools.Method(typeof(Unlocker), nameof(ReturnTrue));
        var ownsHeroHook = AccessTools.Method(typeof(Unlocker), nameof(OwnsPlayableHeroPrefix));

        foreach (var typeName in new[]
        {
            "TheBazaar.CollectionManager",
            "TheBazaar.Collection.CollectionUtility",
            "TheBazaar.LoadoutUtils",
            "TheBazaar.CosmeticsListManager",
            "TheBazaar.CosmeticsDataManager",
            "TheBazaar.HeroManager",
            "TheBazaar.PlayerProfile",
        })
        {
            var t = GameInspector.FindTypeSafe(typeName);
            if (t == null) continue;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.ReturnType != typeof(bool) || m.IsGenericMethodDefinition || m.IsAbstract) continue;

                // 严格禁止对包含 out/ref 参数的方法挂载 ReturnTrue，防止未初始化 out 参数导致下游 NRE（P1-7）
                if (HasOutOrRefParameter(m)) continue;

                string n = m.Name;
                if (typeName == "TheBazaar.HeroManager" && n == "OwnsHero")
                {
                    if (patcher.TryPatchMethod(m, prefix: ownsHeroHook, label: "HeroManager.OwnsHero"))
                    {
                        patched++;
                    }
                    continue;
                }

                bool isOwnershipCheck =
                    n.StartsWith("IsOwned", StringComparison.Ordinal) ||
                    n.StartsWith("HasOwned", StringComparison.Ordinal) ||
                    n.StartsWith("IsUnlocked", StringComparison.Ordinal) ||
                    n.StartsWith("HasUnlocked", StringComparison.Ordinal) ||
                    n == "IsCollectibleOwned" ||
                    n == "IsCollectibleInstanceOwned" ||
                    n == "HasCollectibleOfThisTypeInCollection" ||
                    n == "HasCollectibles";

                if (!isOwnershipCheck) continue;

                if (patcher.TryPatchMethod(m, prefix: returnTrueHook, optional: true, label: $"{t.Name}.{n}"))
                {
                    patched++;
                }
            }
        }

        patched += PatchHero8RuntimeFallbacks(patcher);
        patched += PatchLazyCollectionLoading(patcher);

        BazaarLauncherPlugin.Log?.LogInfo($"Unlocker: 已安全安装 {patched} 个全解锁与懒加载补丁");
    }

    internal static void Apply(Harmony harmony)
    {
        if (_applied || harmony == null) return;
        Apply(new SafeHarmonyPatcher("unlocker.legacy"));
    }

    private static bool HasOutOrRefParameter(MethodInfo m)
    {
        foreach (var p in m.GetParameters())
        {
            if (p.IsOut || p.ParameterType.IsByRef)
            {
                return true;
            }
        }
        return false;
    }

    private static int PatchLazyCollectionLoading(SafeHarmonyPatcher patcher)
    {
        int count = 0;
        var cmType = AccessTools.TypeByName("TheBazaar.CollectionManager");
        if (cmType == null) return 0;

        _cmIsLoadingField = AccessTools.Field(cmType, "isLoading");
        _cmLoadCtsField = AccessTools.Field(cmType, "_collectionLoadCts");
        _cmSaleMapField = AccessTools.Field(cmType, "_guidSaleItemMap");
        _cmUnavailableField = AccessTools.Field(cmType, "_unavailableItemIds");
        _cmRebuildEquippedMethod = AccessTools.Method(cmType, "RebuildEquippedCards");
        _cmLoadItemAsyncMethod = AccessTools.Method(cmType, "LoadCollectionItemAsync", new[] { typeof(string), typeof(CancellationToken) });

        if (patcher.TryPatch(
                cmType,
                "LoadRemainingCollectionAsync",
                new[] { typeof(Task), typeof(string[]), typeof(CancellationToken) },
                prefix: AccessTools.Method(typeof(Unlocker), nameof(LoadRemainingCollectionPrefix)),
                label: "CollectionManager.LoadRemainingCollectionAsync"))
        {
            count++;
        }

        if (patcher.TryPatch(
                cmType,
                "RetryMissingCollectionItemsAsync",
                prefix: AccessTools.Method(typeof(Unlocker), nameof(RetryMissingCollectionItemsPrefix)),
                label: "CollectionManager.RetryMissingCollectionItemsAsync"))
        {
            count++;
        }

        if (patcher.TryPatch(
                cmType,
                "WaitForCollectionItemAsync",
                new[] { typeof(string), typeof(CancellationToken) },
                prefix: AccessTools.Method(typeof(Unlocker), nameof(WaitForCollectionItemPrefix)),
                label: "CollectionManager.WaitForCollectionItemAsync"))
        {
            count++;
        }

        return count;
    }

    private static bool LoadRemainingCollectionPrefix(
        object __instance,
        Task cardSkinTask,
        string[] remaining,
        CancellationToken token,
        ref Task __result)
    {
        try
        {
            if (BazaarLauncherPlugin.LazyLoadCosmetics?.Value != true)
                return true;

            __result = CompleteLazyRemainingCollectionAsync(__instance, cardSkinTask, remaining?.Length ?? 0, token);
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[Unlocker] LoadRemainingCollectionPrefix 异常已拦截: " + ex.Message);
            return true;
        }
    }

    private static async Task CompleteLazyRemainingCollectionAsync(
        object cm,
        Task cardSkinTask,
        int skippedCount,
        CancellationToken token)
    {
        try
        {
            if (cardSkinTask != null)
            {
                await cardSkinTask;
            }
            token.ThrowIfCancellationRequested();

            if (!IsDeadUnityObj(cm))
            {
                _cmRebuildEquippedMethod?.Invoke(cm, null);
                _cmIsLoadingField?.SetValue(cm, false);
            }

            GameEventsBridge.TryTrigger("CollectionAssetsUpdated");
            GameEventsBridge.TryTrigger("CollectionsPopulated");

            BazaarLauncherPlugin.Log?.LogInfo(
                $"Unlocker 藏品懒加载生效：跳过 {skippedCount} 项未穿戴藏品后台全量预加载，改为打开藏品柜按需加载。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompleteLazyRemainingCollectionAsync: " + ex.Message);
        }
        finally
        {
            try
            {
                if (!IsDeadUnityObj(cm) && _cmLoadCtsField?.GetValue(cm) is CancellationTokenSource cts && cts.Token == token)
                {
                    _cmIsLoadingField?.SetValue(cm, false);
                }
            }
            catch { }
        }
    }

    private static bool RetryMissingCollectionItemsPrefix(ref Task<bool> __result)
    {
        try
        {
            if (BazaarLauncherPlugin.LazyLoadCosmetics?.Value != true)
                return true;

            __result = Task.FromResult(false);
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[Unlocker] RetryMissingCollectionItemsPrefix 异常已拦截: " + ex.Message);
            return true;
        }
    }

    private static void WaitForCollectionItemPrefix(object __instance, string id)
    {
        try
        {
            if (BazaarLauncherPlugin.LazyLoadCosmetics?.Value != true) return;
            if (IsDeadUnityObj(__instance) || string.IsNullOrWhiteSpace(id) || _cmLoadItemAsyncMethod == null) return;

            if (_cmSaleMapField?.GetValue(__instance) is IDictionary saleMap && saleMap.Contains(id)) return;
            if (_cmUnavailableField?.GetValue(__instance) is IEnumerable unavailable)
            {
                foreach (var u in unavailable)
                {
                    if (string.Equals(u?.ToString(), id, StringComparison.OrdinalIgnoreCase)) return;
                }
            }

            lock (_lazySync)
            {
                if (!_lazyOnDemandLoading.Add(id)) return;
            }

            CancellationToken loadToken = (_cmLoadCtsField?.GetValue(__instance) as CancellationTokenSource)?.Token ?? CancellationToken.None;
            var loadTask = _cmLoadItemAsyncMethod.Invoke(__instance, new object[] { id, loadToken }) as Task;
            if (loadTask != null)
            {
                loadTask.ContinueWith(_ =>
                {
                    lock (_lazySync) { _lazyOnDemandLoading.Remove(id); }
                }, TaskScheduler.Default);
            }
            else
            {
                lock (_lazySync) { _lazyOnDemandLoading.Remove(id); }
            }
        }
        catch
        {
            lock (_lazySync) { if (!string.IsNullOrEmpty(id)) _lazyOnDemandLoading.Remove(id); }
        }
    }

    private static int PatchHero8RuntimeFallbacks(SafeHarmonyPatcher patcher)
    {
        int count = 0;

        var hmType = AccessTools.TypeByName("TheBazaar.HeroManager");
        var heroEnumType = AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.EHero");
        var heroSoType = AccessTools.TypeByName("TheBazaar.HeroSO");

        if (hmType != null)
        {
            // 精确锁定 HeroManager.TryGetHeroSO 的两个重载签名：(EHero, out HeroSO) 与 (string, out HeroSO)
            var tryGetPrefix = AccessTools.Method(typeof(Unlocker), nameof(HeroManagerTryGetHeroSOPrefix));
            if (heroEnumType != null && heroSoType != null)
            {
                Type heroSoByRef = heroSoType.MakeByRefType();
                if (patcher.TryPatch(
                        hmType,
                        "TryGetHeroSO",
                        new[] { heroEnumType, heroSoByRef },
                        prefix: tryGetPrefix,
                        label: "HeroManager.TryGetHeroSO(EHero, out HeroSO)"))
                {
                    count++;
                }

                if (patcher.TryPatch(
                        hmType,
                        "TryGetHeroSO",
                        new[] { typeof(string), heroSoByRef },
                        prefix: tryGetPrefix,
                        label: "HeroManager.TryGetHeroSO(string, out HeroSO)"))
                {
                    count++;
                }
            }

            if (patcher.TryPatch(
                    hmType,
                    "InitializeAsync",
                    postfix: AccessTools.Method(typeof(Unlocker), nameof(HeroManagerInitializeAsyncPostfix)),
                    label: "HeroManager.InitializeAsync"))
            {
                count++;
            }

            if (patcher.TryPatch(
                    hmType,
                    "OnOwnedHeroesChanged",
                    postfix: AccessTools.Method(typeof(Unlocker), nameof(HeroManagerEnsureFallbackPostfix)),
                    label: "HeroManager.OnOwnedHeroesChanged"))
            {
                count++;
            }
        }

        // CollectionManager：彻底移除历史上不存在的 Awake / Start 2 处死补丁，仅精确挂钩实际存在的两个默认皮肤入口
        var cmType = AccessTools.TypeByName("TheBazaar.CollectionManager");
        if (cmType != null)
        {
            if (patcher.TryPatch(
                    cmType,
                    "GetDefaultHeroSkin",
                    prefix: AccessTools.Method(typeof(Unlocker), nameof(CollectionManagerGetDefaultHeroSkinPrefix)),
                    label: "CollectionManager.GetDefaultHeroSkin"))
            {
                count++;
            }

            if (patcher.TryPatch(
                    cmType,
                    "GetDefaultCollectibleSaleItem",
                    prefix: AccessTools.Method(typeof(Unlocker), nameof(CollectionManagerGetDefaultSaleItemPrefix)),
                    label: "CollectionManager.GetDefaultCollectibleSaleItem"))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsDeadUnityObj(object obj)
        => obj == null || (obj is UnityEngine.Object uo && uo == null);

    private static void HeroManagerTryGetHeroSOPrefix(object __instance, object[] __args)
    {
        try
        {
            if (IsDeadUnityObj(__instance) || __args == null || __args.Length == 0) return;
            if (string.Equals(__args[0]?.ToString(), "Hero8", StringComparison.OrdinalIgnoreCase))
            {
                EnsureHero8InHeroManager(__instance);
            }
        }
        catch { }
    }

    private static void HeroManagerInitializeAsyncPostfix(object __instance, Task __result)
    {
        try
        {
            if (IsDeadUnityObj(__instance)) return;
            EnsureHero8InHeroManager(__instance);
            if (__result != null && !__result.IsCompleted)
            {
                __result.ContinueWith(_ =>
                {
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        try
                        {
                            if (!IsDeadUnityObj(__instance))
                            {
                                EnsureHero8InHeroManager(__instance);
                            }
                        }
                        catch { }
                    });
                }, TaskScheduler.Default);
            }
        }
        catch { }
    }

    private static void HeroManagerEnsureFallbackPostfix(object __instance)
    {
        try
        {
            if (!IsDeadUnityObj(__instance))
            {
                EnsureHero8InHeroManager(__instance);
            }
        }
        catch { }
    }

    private static void CollectionManagerGetDefaultHeroSkinPrefix(object __instance, object[] __args)
    {
        try
        {
            if (IsDeadUnityObj(__instance) || __args == null || __args.Length == 0) return;
            if (string.Equals(__args[0]?.ToString(), "Hero8", StringComparison.OrdinalIgnoreCase))
            {
                EnsureHero8DefaultSkin(__instance);
            }
        }
        catch { }
    }

    private static void CollectionManagerGetDefaultSaleItemPrefix(object[] __args, object __instance)
    {
        try
        {
            if (IsDeadUnityObj(__instance) || __args == null || __args.Length < 2) return;
            if (string.Equals(__args[1]?.ToString(), "Hero8", StringComparison.OrdinalIgnoreCase))
            {
                EnsureHero8DefaultSkin(__instance);
            }
        }
        catch { }
    }

    private static void EnsureHero8InHeroManager(object hm)
    {
        try
        {
            var heroEnumType = AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.EHero");
            if (heroEnumType == null || !Enum.IsDefined(heroEnumType, "Hero8")) return;
            object hero8Enum = Enum.Parse(heroEnumType, "Hero8", ignoreCase: false);

            var hmType = hm.GetType();
            var lookupField = hmType.GetField("_heroDataLookup", BindingFlags.NonPublic | BindingFlags.Instance);
            var objectsField = hmType.GetField("_heroDataObjects", BindingFlags.NonPublic | BindingFlags.Instance);
            var lookup = lookupField?.GetValue(hm) as IDictionary;
            var arr = objectsField?.GetValue(hm) as Array;

            object existingHeroSo = lookup != null && lookup.Contains(hero8Enum) ? lookup[hero8Enum] : null;
            if (existingHeroSo == null)
            {
                var heroSoType = arr?.GetType().GetElementType() ?? AccessTools.TypeByName("TheBazaar.HeroSO");
                if (heroSoType == null) return;

                existingHeroSo = TryLoadAddressableAssetTracked(heroSoType, Hero8SoAddressablesKeys, out string usedKey);
                if (existingHeroSo == null) return;

                if (lookup != null)
                {
                    lookup[hero8Enum] = existingHeroSo;
                }

                if (objectsField != null && arr != null)
                {
                    var heroProp = heroSoType.GetProperty("Hero", BindingFlags.Public | BindingFlags.Instance);
                    bool alreadyInArray = false;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var item = arr.GetValue(i);
                        if (item != null && Equals(heroProp?.GetValue(item), hero8Enum))
                        {
                            alreadyInArray = true;
                            break;
                        }
                    }
                    if (!alreadyInArray)
                    {
                        var newArr = Array.CreateInstance(heroSoType, arr.Length + 1);
                        Array.Copy(arr, newArr, arr.Length);
                        newArr.SetValue(existingHeroSo, arr.Length);
                        objectsField.SetValue(hm, newArr);
                    }
                }

                if (!_hero8SoLogged)
                {
                    _hero8SoLogged = true;
                    BazaarLauncherPlugin.Log?.LogInfo($"Unlocker: 已通过 '{usedKey}' 注入双龙 (Hero8) HeroSO");
                }
            }

            var prefsType = AccessTools.TypeByName("PlayerPreferences");
            var prefsData = prefsType?.GetProperty("Data", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (prefsData != null)
            {
                var exclProp = prefsData.GetType().GetProperty("HeroesExcludedFromRandom", BindingFlags.Public | BindingFlags.Instance);
                if (exclProp?.GetValue(prefsData) is IDictionary exclDict && !exclDict.Contains("Hero8"))
                {
                    var setMethod = prefsData.GetType().GetMethod("SetHeroExcludedFromRandom", new[] { heroEnumType, typeof(bool) });
                    setMethod?.Invoke(prefsData, new[] { hero8Enum, (object)false });
                }
            }
        }
        catch { }
    }

    private static void EnsureHero8DefaultSkin(object cm)
    {
        try
        {
            var heroEnumType = AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.EHero");
            if (heroEnumType == null || !Enum.IsDefined(heroEnumType, "Hero8")) return;
            object hero8Enum = Enum.Parse(heroEnumType, "Hero8", ignoreCase: false);

            var cmType = cm.GetType();
            var field = cmType.GetField("defaultHeroSkins", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) return;

            var entryType = field.FieldType.GetElementType();
            if (entryType == null) return;
            var entryHeroField = entryType.GetField("hero", BindingFlags.Public | BindingFlags.Instance);
            var entrySkinField = entryType.GetField("skin", BindingFlags.Public | BindingFlags.Instance);
            if (entryHeroField == null || entrySkinField == null) return;

            var arr = field.GetValue(cm) as Array;
            int replaceIdx = -1;
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var entry = arr.GetValue(i);
                    if (entry != null && Equals(entryHeroField.GetValue(entry), hero8Enum))
                    {
                        if (entrySkinField.GetValue(entry) != null) return;
                        replaceIdx = i;
                        break;
                    }
                }
            }

            Type skinAssetType = entrySkinField.FieldType;
            object loadedSkin = TryLoadAddressableAssetTracked(skinAssetType, Hero8SkinAddressablesKeys, out string usedKey);
            if (loadedSkin == null) return;

            object newEntry = Activator.CreateInstance(entryType);
            entryHeroField.SetValue(newEntry, hero8Enum);
            entrySkinField.SetValue(newEntry, loadedSkin);

            int oldLen = arr?.Length ?? 0;
            if (replaceIdx >= 0 && arr != null)
            {
                arr.SetValue(newEntry, replaceIdx);
                field.SetValue(cm, arr);
            }
            else
            {
                var newArr = Array.CreateInstance(entryType, oldLen + 1);
                if (arr != null && oldLen > 0)
                    Array.Copy(arr, newArr, oldLen);
                newArr.SetValue(newEntry, oldLen);
                field.SetValue(cm, newArr);
            }

            if (!_hero8SkinLogged)
            {
                _hero8SkinLogged = true;
                BazaarLauncherPlugin.Log?.LogInfo($"Unlocker: 已通过 '{usedKey}' 注入双龙 (Hero8) 默认皮肤");
            }
        }
        catch { }
    }

    private static object TryLoadAddressableAssetTracked(Type assetType, string[] candidateKeys, out string matchedKey)
    {
        matchedKey = string.Empty;
        var addrType = AccessTools.TypeByName("UnityEngine.AddressableAssets.Addressables");
        if (addrType == null) return null;

        var locatorsProp = addrType.GetProperty("ResourceLocators", BindingFlags.Public | BindingFlags.Static);
        if (locatorsProp?.GetValue(null) is not IEnumerable locators) return null;

        var locatorList = new List<object>();
        foreach (var loc in locators)
        {
            if (loc != null) locatorList.Add(loc);
        }
        if (locatorList.Count == 0) return null;

        var iLocatorType = AccessTools.TypeByName("UnityEngine.AddressableAssets.ResourceLocators.IResourceLocator");
        var locateMethod = iLocatorType?.GetMethod("Locate", BindingFlags.Public | BindingFlags.Instance);

        MethodInfo openLoadMethod = null;
        foreach (var m in addrType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name == "LoadAssetAsync" && m.IsGenericMethodDefinition)
            {
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(object))
                {
                    openLoadMethod = m;
                    break;
                }
            }
        }
        if (openLoadMethod == null) return null;

        var genericLoadMethod = openLoadMethod.MakeGenericMethod(assetType);

        foreach (string key in candidateKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;

            bool keyExists = false;
            if (locateMethod != null)
            {
                foreach (var loc in locatorList)
                {
                    try
                    {
                        var args = new object[] { key, assetType, null };
                        if (locateMethod.Invoke(loc, args) is bool ok && ok)
                        {
                            keyExists = true;
                            break;
                        }
                    }
                    catch { }
                }
            }
            if (!keyExists) continue;

            try
            {
                object handle = genericLoadMethod.Invoke(null, new object[] { key });
                if (handle == null) continue;
                var waitMethod = handle.GetType().GetMethod("WaitForCompletion", BindingFlags.Public | BindingFlags.Instance);
                object asset = waitMethod?.Invoke(handle, null);
                if (asset != null)
                {
                    // 托管记录 AsyncOperationHandle 以便卸载时配对释放，杜绝永久引用泄漏（P1-7）
                    lock (_lazySync)
                    {
                        _trackedAddressableHandles.Add(handle);
                    }
                    matchedKey = key;
                    return asset;
                }
                TryReleaseHandle(addrType, handle);
            }
            catch { }
        }

        return null;
    }

    internal static void ReleaseTrackedAddressableHandles()
    {
        List<object> handles;
        lock (_lazySync)
        {
            if (_trackedAddressableHandles.Count == 0) return;
            handles = new List<object>(_trackedAddressableHandles);
            _trackedAddressableHandles.Clear();
        }

        var addrType = AccessTools.TypeByName("UnityEngine.AddressableAssets.Addressables");
        if (addrType == null) return;
        foreach (var h in handles)
        {
            TryReleaseHandle(addrType, h);
        }
    }

    private static void TryReleaseHandle(Type addrType, object handle)
    {
        if (addrType == null || handle == null) return;
        try
        {
            Type handleType = handle.GetType();
            var isValidMethod = handleType.GetMethod("IsValid", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (isValidMethod != null && isValidMethod.Invoke(handle, null) is bool valid && !valid) return;

            var methods = addrType.GetMethods(BindingFlags.Public | BindingFlags.Static);

            // D4-P1-2：优先匹配泛型 Addressables.Release<TObject>(AsyncOperationHandle<TObject>) 并用 MakeGenericMethod 闭合释放
            if (handleType.IsGenericType)
            {
                Type[] genArgs = handleType.GetGenericArguments();
                if (genArgs.Length == 1)
                {
                    for (int i = 0; i < methods.Length; i++)
                    {
                        var m = methods[i];
                        if (m.Name != "Release" || !m.IsGenericMethodDefinition) continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 1) continue;
                        Type pType = ps[0].ParameterType;
                        if (pType.IsGenericType && pType.GetGenericTypeDefinition() == handleType.GetGenericTypeDefinition())
                        {
                            var closed = m.MakeGenericMethod(genArgs[0]);
                            closed.Invoke(null, new[] { handle });
                            return;
                        }
                    }
                }
            }

            // 回退非泛型 Addressables.Release(AsyncOperationHandle)
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != "Release" || m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(handle))
                {
                    m.Invoke(null, new[] { handle });
                    return;
                }
            }

            BazaarLauncherPlugin.Log?.LogWarning($"[Unlocker] 未找到可匹配 {handleType.Name} 的 Addressables.Release 重载，跳过释放");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[Unlocker] TryReleaseHandle 释放异常: {ex.Message}");
        }
    }

    private static bool OwnsPlayableHeroPrefix(object[] __args, ref bool __result)
    {
        try
        {
            string heroName = __args != null && __args.Length > 0 ? __args[0]?.ToString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(heroName) ||
                string.Equals(heroName, "Common", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            __result = true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool ReturnTrue(ref bool __result)
    {
        __result = true;
        return false;
    }
}
