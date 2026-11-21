using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace BazaarLauncher.Core;

/// <summary>
/// 统一无阻塞 Unity 主线程调度泵：
/// 取代各模块散落的 _unitySyncContext 兜底直调逻辑，彻底杜绝后台线程调用 Unity API 崩溃（修复 P1-5 / P1-7）。
/// </summary>
internal sealed class MainThreadDispatcher : MonoBehaviour
{
    private static MainThreadDispatcher _instance;
    private static int _mainThreadId = -1;
    private static readonly ConcurrentQueue<Action> _queue = new();

    internal static bool IsOnMainThread => _mainThreadId > 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

    internal static void EnsureInstalled()
    {
        if (_mainThreadId <= 0)
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        if (_instance != null) return;
        var go = new GameObject("BazaarModKitDispatcher");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<MainThreadDispatcher>();
    }

    internal static void Shutdown()
    {
        while (_queue.TryDequeue(out _)) { }
        if (_instance != null)
        {
            var go = _instance.gameObject;
            _instance = null;
            if (go != null) Destroy(go);
        }
    }

    /// <summary>安全派发到 Unity 主线程执行（已在主线程则立即同步执行，否则入队于下一帧 Update 执行）。</summary>
    internal static void Enqueue(Action action)
    {
        if (action == null) return;
        if (IsOnMainThread)
        {
            try { action(); }
            catch (Exception ex) { BazaarLauncherPlugin.Log?.LogWarning("MainThreadDispatcher 立即执行异常: " + ex.Message); }
            return;
        }

        _queue.Enqueue(action);
    }

    /// <summary>强制入队到下一帧主线程执行（避免当前栈重入）。</summary>
    internal static void EnqueueNextFrame(Action action)
    {
        if (action == null) return;
        _queue.Enqueue(action);
    }

    private void Awake()
    {
        _instance = this;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    private void Update()
    {
        int processed = 0;
        while (processed++ < 128 && _queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning("MainThreadDispatcher 队列回调异常: " + ex.Message);
            }
        }
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }
}
