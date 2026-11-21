using System.Diagnostics;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// 本地单机服务端进程托管器。
/// 在用户选择「本地单机离线版」时：
///   1. 自动寻路 dist\server\BazaarServer.exe 或同级 server\BazaarServer.exe；
///   2. 配置 BAZAAR_PORT / BAZAAR_DATA / BAZAAR_STATE / BAZAAR_GHOSTS 后静默拉起服务端；
///   3. 轮询 http://127.0.0.1:{port}/healthz 直至就绪；
///   4. 启动 TheBazaar.exe 并监控游戏退出后自动优雅停止单机服务端（不破坏独立运行的外部服务端）。
/// </summary>
public sealed class StandaloneServerHost : IDisposable
{
    private readonly object _gate = new();
    private Process? _serverProcess;
    private bool _startedByUs;
    private Process? _monitoredGameProcess;
    private bool _disposed;

    public bool IsManagedServerRunning
    {
        get
        {
            lock (_gate)
            {
                return _startedByUs && _serverProcess != null && !_serverProcess.HasExited;
            }
        }
    }

    public string? ActiveServerExePath { get; private set; }
    public string? ActiveStateDirectory { get; private set; }

    /// <summary>
    /// 当托管的游戏进程退出且单机服务端完成清理后触发。
    /// </summary>
    public event Action<string>? LifecycleMessage;

    /// <summary>
    /// 定位本地 BazaarServer.exe（兼容发布包同级 server\、仓库 dist\server\ 与编译输出目录）。
    /// </summary>
    public static string? LocateServerExecutable(string? extraBaseDir = null)
    {
        var roots = new List<string>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddAncestors(string? start)
        {
            if (string.IsNullOrWhiteSpace(start)) return;
            string cur = Path.GetFullPath(start);
            for (int depth = 0; depth < 7; depth++)
            {
                if (seenRoots.Add(cur))
                {
                    roots.Add(cur);
                }

                string? parent = Directory.GetParent(cur)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, cur, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                cur = parent;
            }
        }

        AddAncestors(AppContext.BaseDirectory);
        AddAncestors(Environment.CurrentDirectory);
        AddAncestors(extraBaseDir);

        string[] relativeCandidates =
        {
            @"server\BazaarServer.exe",
            @"dist\server\BazaarServer.exe",
            @"BazaarServer.exe",
            @"src\BazaarServer\bin\Release\net8.0\BazaarServer.exe",
            @"src\BazaarServer\bin\Debug\net8.0\BazaarServer.exe"
        };

        foreach (string root in roots)
        {
            foreach (string rel in relativeCandidates)
            {
                string full = Path.Combine(root, rel);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 根据服务端位置定位权威数据目录（含 GameData.db）。
    /// </summary>
    public static string? LocateDataDirectory(string serverExePath)
    {
        string serverDir = Path.GetDirectoryName(serverExePath) ?? AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(serverDir, "data"),
            Path.GetFullPath(Path.Combine(serverDir, "..", "data")),
            Path.GetFullPath(Path.Combine(serverDir, "..", "..", "data")),
            Path.GetFullPath(Path.Combine(serverDir, "..", "..", "dist", "data"))
        };

        string cur = serverDir;
        for (int i = 0; i < 6; i++)
        {
            candidates.Add(Path.Combine(cur, "dist", "data"));
            string? parent = Directory.GetParent(cur)?.FullName;
            if (string.IsNullOrWhiteSpace(parent)) break;
            cur = parent;
        }

        foreach (string dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(dir) &&
                (File.Exists(Path.Combine(dir, "GameData.db")) || File.Exists(Path.Combine(dir, "ghost_pool_balanced.jsonl"))))
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// 确保本机单机服务端就绪（若已在 127.0.0.1:port 运行则直接复用，否则自动拉起并等待 /healthz）。
    /// </summary>
    public async Task<(bool Ready, string Message)> EnsureStartedAsync(
        int port = 8080,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        string localBase = $"http://127.0.0.1:{port}";
        using var probe = new ServerProbeService();

        // 若端口上已有健康的本地服务端，直接复用
        ServerProbeResult preCheck = await probe.ProbeAsync(localBase, cancellationToken);
        if (preCheck.HealthzOk)
        {
            string msg = $"检测到本机单机服务端已在 {localBase} 运行，直接复用。";
            log?.Invoke($"[单机服] {msg}");
            return (true, msg);
        }

        string? serverExe = LocateServerExecutable();
        if (string.IsNullOrWhiteSpace(serverExe))
        {
            string err = "未找到本地单机服务端 BazaarServer.exe（已自动搜索同级 server\\ 与仓库 dist\\server\\）。";
            log?.Invoke($"[错误] {err}");
            return (false, err);
        }

        string serverDir = Path.GetDirectoryName(serverExe)!;
        string? dataDir = LocateDataDirectory(serverExe);
        string stateDir = ResolveStateDirectory(serverDir, dataDir);
        Directory.CreateDirectory(stateDir);

        ActiveServerExePath = serverExe;
        ActiveStateDirectory = stateDir;

        var psi = new ProcessStartInfo
        {
            FileName = serverExe,
            WorkingDirectory = dataDir != null ? (Path.GetDirectoryName(dataDir) ?? serverDir) : serverDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.Environment["BAZAAR_PORT"] = port.ToString();
        psi.Environment["BAZAAR_STATE"] = stateDir;
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            psi.Environment["BAZAAR_DATA"] = dataDir;
            string ghostFile = Path.Combine(dataDir, "ghost_pool_balanced.jsonl");
            if (File.Exists(ghostFile))
            {
                psi.Environment["BAZAAR_GHOSTS"] = ghostFile;
            }
        }

        log?.Invoke($"[单机服] 正在启动本地服务端：{serverExe}");
        log?.Invoke($"[单机服] 存档目录：{stateDir} ｜ 数据目录：{dataDir ?? "默认"}");

        Process proc = Process.Start(psi) ?? throw new InvalidOperationException("无法拉起 BazaarServer.exe 进程。");
        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                log?.Invoke($"[单机服] {e.Data}");
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                log?.Invoke($"[单机服/ERR] {e.Data}");
            }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        lock (_gate)
        {
            _serverProcess = proc;
            _startedByUs = true;
        }

        // 轮询 /healthz，最长等待 30 秒
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (proc.HasExited)
            {
                string exitMsg = $"本地单机服务端提前退出（退出码 {proc.ExitCode}），请查看上方日志。";
                log?.Invoke($"[错误] {exitMsg}");
                return (false, exitMsg);
            }

            ServerProbeResult check = await probe.ProbeAsync(localBase, cancellationToken);
            if (check.HealthzOk)
            {
                string okMsg = $"本地单机服务端已就绪（PID={proc.Id}，{localBase}/healthz）。";
                log?.Invoke($"[成功] {okMsg}");
                return (true, okMsg);
            }

            await Task.Delay(400, cancellationToken);
        }

        StopManagedServer(log);
        const string timeoutMsg = "等待本地单机服务端 /healthz 就绪超时（30 秒），已终止进程。";
        log?.Invoke($"[错误] {timeoutMsg}");
        return (false, timeoutMsg);
    }

    /// <summary>
    /// 启动《The Bazaar》游戏进程；若当前托管着单机服务端，自动在游戏退出后优雅停服。
    /// </summary>
    public Process LaunchGameAndWatch(
        string gameDir,
        PerformanceTuningOptions perfOptions,
        bool stopServerOnGameExit,
        Action<string>? log = null)
    {
        string gameExe = Path.Combine(gameDir, "TheBazaar.exe");
        if (!File.Exists(gameExe))
        {
            throw new FileNotFoundException($"未找到游戏主程序：{gameExe}", gameExe);
        }

        string unityArgs = perfOptions.BuildUnityLaunchArguments();
        var psi = new ProcessStartInfo
        {
            FileName = gameExe,
            Arguments = unityArgs,
            WorkingDirectory = gameDir,
            UseShellExecute = true
        };

        log?.Invoke($"[启动] 正在拉起游戏：{gameExe}" + (string.IsNullOrWhiteSpace(unityArgs) ? string.Empty : $" {unityArgs}"));
        Process gameProc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 TheBazaar.exe。");

        if (perfOptions.ArgHighProcessPriority)
        {
            try
            {
                gameProc.PriorityClass = ProcessPriorityClass.High;
                log?.Invoke("[启动] 已将 TheBazaar.exe 进程调度优先级提升为 High。");
            }
            catch
            {
                // 权限不足时静默降级，不影响游戏启动
            }
        }

        lock (_gate)
        {
            _monitoredGameProcess = gameProc;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await gameProc.WaitForExitAsync();
                string exitNotice = $"游戏进程（PID={gameProc.Id}）已退出（退出码 {gameProc.ExitCode}）。";
                log?.Invoke($"[启动] {exitNotice}");
                LifecycleMessage?.Invoke(exitNotice);

                if (stopServerOnGameExit && IsManagedServerRunning)
                {
                    StopManagedServer(log);
                    LifecycleMessage?.Invoke("本地单机服务端已随游戏退出优雅停止，存档已完整保留。");
                }
            }
            catch
            {
                // 忽略监控结束异常
            }
        });

        return gameProc;
    }

    /// <summary>
    /// 优雅停止由本启动器拉起的单机服务端进程。
    /// </summary>
    public void StopManagedServer(Action<string>? log = null)
    {
        Process? proc;
        lock (_gate)
        {
            if (!_startedByUs || _serverProcess == null)
            {
                return;
            }
            proc = _serverProcess;
            _serverProcess = null;
            _startedByUs = false;
        }

        try
        {
            if (!proc.HasExited)
            {
                log?.Invoke($"[单机服] 正在优雅停止本地服务端（PID={proc.Id}）...");
                try
                {
                    proc.CloseMainWindow();
                }
                catch
                {
                    // 无窗口控制台忽略
                }

                if (!proc.WaitForExit(3000))
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }

                log?.Invoke($"[单机服] 本地服务端已停止，存档位于：{ActiveStateDirectory}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[警告] 停止本地服务端时出现异常：{ex.Message}");
        }
        finally
        {
            proc.Dispose();
        }
    }

    private static string ResolveStateDirectory(string serverDir, string? dataDir)
    {
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            string? distDir = Directory.GetParent(dataDir)?.FullName;
            string? repoRoot = distDir != null ? Directory.GetParent(distDir)?.FullName : null;
            if (!string.IsNullOrWhiteSpace(repoRoot) &&
                File.Exists(Path.Combine(repoRoot, "src", "BazaarServer", "BazaarServer.csproj")))
            {
                return Path.Combine(repoRoot, "logs", "standalone-state");
            }
        }

        return Path.Combine(serverDir, "state");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopManagedServer();
        lock (_gate)
        {
            _monitoredGameProcess?.Dispose();
            _monitoredGameProcess = null;
        }
    }
}
