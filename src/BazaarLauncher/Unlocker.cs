using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace BazaarLauncher;

/// <summary>
/// 全解锁：英雄、皮肤、藏品、赛季奖励、箱子全部视为已拥有。
///
/// 服务端已经返回全英雄（BootstrapHeroesDto.owned）与 catalog.bin 白名单藏品，
/// 这里再补一层客户端真实命名空间下的兜底，保证所有「已拥有/已解锁」判定返回 true。
/// </summary>
internal static class Unlocker
{
    private static bool _applied;
    private static int _mainThreadId;
    private static SynchronizationContext _unitySyncContext;

    internal static void Apply(Harmony harmony)
    {
        if (_applied) return;
        _applied = true;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _unitySyncContext ??= SynchronizationContext.Current;

        int patched = 0;

        // 1) Steam DLC 放行
        patched += PatchReturnTrue(harmony, "TheBazaar.Steam.SteamDLCService", "IsInstalled");
        patched += PatchReturnTrue(harmony, "Steamworks.SteamApps", "BIsDlcInstalled");
        patched += PatchReturnTrue(harmony, "Steamworks.SteamApps", "BIsSubscribedApp");

        // 2) 藏品 / 皮肤 / 装扮 / 英雄：修正为客户端真实命名空间，仅拦截所有权判定（不拦截 IsEquipped）
        foreach (var typeName in new[]
        {
            "TheBazaar.CollectionManager",
            "TheBazaar.Collection.CollectionUtility",
            "TheBazaar.LoadoutUtils",
            "TheBazaar.CosmeticsListManager",
            "TheBazaar.CosmeticsDataManager",
            "TheBazaar.HeroManager",
            "TheBazaar.PlayerProfile",
            "TheBazaar.ProfileData.PlayerProfile",
        })
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null) continue;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.ReturnType != typeof(bool) || m.IsGenericMethodDefinition) continue;
                string n = m.Name;

                if (typeName == "TheBazaar.HeroManager" && n == "OwnsHero")
                {
                    try
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(Unlocker), nameof(OwnsPlayableHeroPrefix))));
                        patched++;
                    }
                    catch { }
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

                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(Unlocker), nameof(ReturnTrue))));
                    patched++;
                }
                catch { }
            }
        }

        patched += PatchHero8RuntimeFallbacks(harmony);
        patched += PatchLazyCollectionLoading(harmony);

        BazaarLauncherPlugin.Log.LogInfo($"Unlocker applied {patched} patches");
    }

    // ── 396 套全解锁藏品按需懒加载（砍掉启动 26,669 个闲置 3D 资产显存常驻）──

    private static readonly object _lazySync = new();
    private static readonly HashSet<string> _lazyOnDemandLoading = new(StringComparer.OrdinalIgnoreCase);
    private static FieldInfo _cmIsLoadingField;
    private static FieldInfo _cmLoadCtsField;
    private static FieldInfo _cmSaleMapField;
    private static FieldInfo _cmUnavailableField;
    private static MethodInfo _cmRebuildEquippedMethod;
    private static MethodInfo _cmLoadItemAsyncMethod;
    private static FieldInfo _eventsCollectionUpdatedField;
    private static FieldInfo _eventsCollectionsPopulatedField;

    private static int PatchLazyCollectionLoading(Harmony harmony)
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

        var loadRem = AccessTools.Method(cmType, "LoadRemainingCollectionAsync");
        if (loadRem != null)
        {
            try
            {
                harmony.Patch(loadRem, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Unlocker), nameof(LoadRemainingCollectionPrefix))));
                count++;
            }
            catch { }
        }

        var retryMissing = AccessTools.Method(cmType, "RetryMissingCollectionItemsAsync");
        if (retryMissing != null)
        {
            try
            {
                harmony.Patch(retryMissing, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Unlocker), nameof(RetryMissingCollectionItemsPrefix))));
                count++;
            }
            catch { }
        }

        var waitItem = AccessTools.Method(cmType, "WaitForCollectionItemAsync", new[] { typeof(string), typeof(CancellationToken) });
        if (waitItem != null)
        {
            try
            {
                harmony.Patch(waitItem, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Unlocker), nameof(WaitForCollectionItemPrefix))));
                count++;
            }
            catch { }
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
        if (BazaarLauncherPlugin.LazyLoadCosmetics?.Value != true)
            return true;

        __result = CompleteLazyRemainingCollectionAsync(__instance, cardSkinTask, remaining?.Length ?? 0, token);
        return false;
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

            TriggerCollectionStaticEvent("CollectionAssetsUpdated", ref _eventsCollectionUpdatedField);
            TriggerCollectionStaticEvent("CollectionsPopulated", ref _eventsCollectionsPopulatedField);

            BazaarLauncherPlugin.Log?.LogInfo(
                $"LazyLoadCosmetics active: skipped background preloading of {skippedCount} unequipped collectibles (on-demand load enabled).");
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

    private static void TriggerCollectionStaticEvent(string fieldName, ref FieldInfo cachedField)
    {
        try
        {
            if (cachedField == null)
            {
                var evType = AccessTools.TypeByName("TheBazaar.Events");
                cachedField = evType?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            }
            var evObj = cachedField?.GetValue(null);
            evObj?.GetType().GetMethod("Trigger", BindingFlags.Public | BindingFlags.Instance)?.Invoke(evObj, Type.EmptyTypes);
        }
        catch { }
    }

    private static bool RetryMissingCollectionItemsPrefix(ref Task<bool> __result)
    {
        if (BazaarLauncherPlugin.LazyLoadCosmetics?.Value != true)
            return true;

        __result = Task.FromResult(false);
        return false;
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

    private static readonly string[] Hero8SoAddressablesKeys =
    {
        "Hero_DRA",
        "Hero_DRA.asset",
    };

    // Skin_DRA_01a_SkinData.asset（基础皮肤）优先，缺失时回退 Skin_DRA_02a_SkinData.asset
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

    private static int PatchHero8RuntimeFallbacks(Harmony harmony)
    {
        int count = 0;

        var hmType = AccessTools.TypeByName("TheBazaar.HeroManager");
        if (hmType != null)
        {
            foreach (var m in hmType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name == "TryGetHeroSO")
                {
                    try
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(Unlocker), nameof(HeroManagerTryGetHeroSOPrefix))));
                        count++;
                    }
                    catch { }
                }
                else if (m.Name == "InitializeAsync")
                {
                    try
                    {
                        harmony.Patch(m, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Unlocker), nameof(HeroManagerInitializeAsyncPostfix))));
                        count++;
                    }
                    catch { }
                }
                else if (m.Name == "OnOwnedHeroesChanged")
                {
                    try
                    {
                        harmony.Patch(m, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Unlocker), nameof(HeroManagerEnsureFallbackPostfix))));
                        count++;
                    }
                    catch { }
                }
            }
        }

        var cmType = AccessTools.TypeByName("TheBazaar.CollectionManager");
        if (cmType != null)
        {
            var getDefaultSkin = AccessTools.Method(cmType, "GetDefaultHeroSkin");
            if (getDefaultSkin != null)
            {
                try
                {
                    harmony.Patch(getDefaultSkin, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(Unlocker), nameof(CollectionManagerGetDefaultHeroSkinPrefix))));
                    count++;
                }
                catch { }
            }

            var getDefaultSale = AccessTools.Method(cmType, "GetDefaultCollectibleSaleItem");
            if (getDefaultSale != null)
            {
                try
                {
                    harmony.Patch(getDefaultSale, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(Unlocker), nameof(CollectionManagerGetDefaultSaleItemPrefix))));
                    count++;
                }
                catch { }
            }

            foreach (var initName in new[] { "Awake", "Start" })
            {
                var mInit = AccessTools.Method(cmType, initName);
                if (mInit == null) continue;
                try
                {
                    harmony.Patch(mInit, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(Unlocker), nameof(CollectionManagerEnsureFallbackPostfix))));
                    count++;
                }
                catch { }
            }
        }

        return count;
    }

    private static void PostToMainThread(Action action)
    {
        if (action == null) return;
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            action();
            return;
        }
        if (_unitySyncContext != null)
        {
            _unitySyncContext.Post(_ => action(), null);
            return;
        }
        action();
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
                    PostToMainThread(() =>
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

    private static void CollectionManagerEnsureFallbackPostfix(object __instance)
    {
        try
        {
            if (!IsDeadUnityObj(__instance))
            {
                EnsureHero8DefaultSkin(__instance);
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

    /// <summary>
    /// 确保 HeroManager 中已注入 EHero.Hero8 对应的 HeroSO（通过 catalog.bin 核实的 Hero_DRA 键同步加载）并纳入随机池。
    /// </summary>
    private static void EnsureHero8InHeroManager(object hm)
    {
        try
        {
            var heroEnumType = AccessTools.TypeByName("BazaarGameShared.Domain.Core.Types.EHero");
            if (heroEnumType == null) return;
            if (!Enum.IsDefined(heroEnumType, "Hero8")) return;
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

                existingHeroSo = TryLoadAddressableAssetSync(heroSoType, Hero8SoAddressablesKeys, out string usedKey);
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
                    BazaarLauncherPlugin.Log.LogInfo($"Injected Hero8 HeroSO into HeroManager via key '{usedKey}'");
                }
            }

            // 同步玩家随机英雄偏好：若尚未记录 Hero8，默认纳入随机池（对齐 HeroManager.InitializeAsync）
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

    /// <summary>
    /// 确保 CollectionManager.defaultHeroSkins 包含 EHero.Hero8 默认皮肤（优先加载 Skin_DRA_01a，回退 Skin_DRA_02a）。
    /// </summary>
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
                        if (entrySkinField.GetValue(entry) != null)
                            return; // 已完整配置 Hero8 默认皮肤
                        replaceIdx = i;
                        break;
                    }
                }
            }

            Type skinAssetType = entrySkinField.FieldType;
            object loadedSkin = TryLoadAddressableAssetSync(skinAssetType, Hero8SkinAddressablesKeys, out string usedKey);
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
                BazaarLauncherPlugin.Log.LogInfo($"Injected Hero8 default skin into CollectionManager via key '{usedKey}'");
            }
        }
        catch { }
    }

    private static object TryLoadAddressableAssetSync(Type assetType, string[] candidateKeys, out string matchedKey)
    {
        matchedKey = "";
        var addrType = AccessTools.TypeByName("UnityEngine.AddressableAssets.Addressables");
        if (addrType == null) return null;

        var locatorsProp = addrType.GetProperty("ResourceLocators", BindingFlags.Public | BindingFlags.Static);
        if (locatorsProp?.GetValue(null) is not IEnumerable locators) return null;

        var locatorList = new List<object>();
        foreach (var loc in locators)
        {
            if (loc != null) locatorList.Add(loc);
        }
        if (locatorList.Count == 0) return null; // Addressables 尚未初始化完成

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
                    matchedKey = key;
                    return asset;
                }
            }
            catch { }
        }

        return null;
    }

    private static int PatchReturnTrue(Harmony harmony, string typeName, string methodName)
    {
        var t = AccessTools.TypeByName(typeName);
        if (t == null) return 0;
        var m = AccessTools.Method(t, methodName);
        if (m == null || m.ReturnType != typeof(bool)) return 0;
        try
        {
            harmony.Patch(m, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(Unlocker), nameof(ReturnTrue))));
            return 1;
        }
        catch { return 0; }
    }

    private static bool OwnsPlayableHeroPrefix(object[] __args, ref bool __result)
    {
        try
        {
            // 仅排除空值与占位 Common，彻底放行包括 Hero8（双龙）在内的所有正式可玩英雄
            string heroName = __args != null && __args.Length > 0 ? __args[0]?.ToString() ?? "" : "";
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
        try
        {
            __result = true;
            return false;
        }
        catch
        {
            return true;
        }
    }
}
