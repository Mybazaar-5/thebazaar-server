using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// Windows Job Object 托管句柄（D7-P1-03）：
/// 绑定 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE，确保即便 BazaarHub 异常崩溃或被强杀，
/// 托管的本地 BazaarServer.exe 子进程树也会由操作系统内核自动清理，彻底杜绝孤儿残留。
/// </summary>
internal sealed class ManagedJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private IntPtr _handle;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int jobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    public bool IsValid => !_disposed && _handle != IntPtr.Zero;

    public ManagedJobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IntPtr hJob = CreateJobObjectW(IntPtr.Zero, null);
        if (hJob == IntPtr.Zero)
        {
            return;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
            if (!SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                CloseHandle(hJob);
                return;
            }
            _handle = hJob;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public bool TryAssignProcess(Process proc)
    {
        if (!IsValid || proc == null)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(_handle, proc.Handle);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>
/// 本地单机服务端进程托管器（D7-P1-03 强化版）：
///   1. 强制回环监听 BAZAAR_BIND=127.0.0.1，防止单机服暴露局域网/公网；
///   2. 启动前执行 TCP 端口预检 + /healthz 专属 JSON 指纹校验，精准区分自有 BazaarServer 与外部占端口进程；
///   3. 通过 Windows Job Object (KILL_ON_JOB_CLOSE) 防 Hub 崩溃遗留孤儿进程；
///   4. 停服时拉起独立短命子进程 BazaarHub.exe --send-ctrl-c &lt;pid&gt; 安全发送 CTRL_C_EVENT 触发
///      Kestrel ApplicationStopping 刷盘落库，零切断自身/测试宿主控制台，超时才降级强杀。
/// </summary>
public sealed class StandaloneServerHost : IDisposable
{
    private static readonly string[] BazaarHealthzFingerprintKeys =
    {
        "loadedCards",
        "totalTemplates",
        "ghostBoards",
        "activeSessions",
        "modSetHash",
        "enabledMods"
    };

    private readonly object _gate = new();
    private Process? _serverProcess;
    private ManagedJobObject? _jobObject;
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
    /// 校验 /healthz 返回体是否确为 BazaarServer 专属健康响应（D7-P1-03）：
    /// 要求 JSON 根节点 ok == true 且至少包含 2 个 BazaarServer 专属运行态指纹字段，防止误复用占端口的第三方服务。
    /// </summary>
    public static bool IsBazaarHealthzPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool ok = false;
            int fingerprintHits = 0;
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, "ok", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.True)
                {
                    ok = true;
                }
                else
                {
                    foreach (string key in BazaarHealthzFingerprintKeys)
                    {
                        if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                        {
                            fingerprintHits++;
                            break;
                        }
                    }
                }
            }

            return ok && fingerprintHits >= 2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 自检辅助：验证当前系统 Windows Job Object 可正常创建并安全释放（供 SelfTestRunner Case 11 使用）。
    /// </summary>
    public static bool VerifyJobObjectLifecycle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        using var job = new ManagedJobObject();
        return job.IsValid;
    }

    /// <summary>
    /// 探测本机回环指定端口是否已被 TCP 监听占用。
    /// </summary>
    public static async Task<bool> IsLocalTcpPortListeningAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync("127.0.0.1", port, timeoutCts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 请求 http://127.0.0.1:{port}/healthz 并校验其 JSON 指纹是否确为 BazaarServer。
    /// </summary>
    public static async Task<bool> VerifyLocalBazaarHealthzAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{port}/healthz", cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }
            string body = await resp.Content.ReadAsStringAsync(cancellationToken);
            return IsBazaarHealthzPayload(body);
        }
        catch
        {
            return false;
        }
    }

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
    /// 确保本机单机服务端就绪（预检端口占用 + /healthz 专属指纹校验；仅复用真实 BazaarServer，遇外部冲突明确报错阻断）。
    /// </summary>
    public async Task<(bool Ready, string Message)> EnsureStartedAsync(
        int port = 8080,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        string localBase = $"http://127.0.0.1:{port}";

        // D7-P1-03：TCP 端口预检 + /healthz 专属 JSON 指纹校验
        bool portOccupied = await IsLocalTcpPortListeningAsync(port, cancellationToken);
        if (portOccupied)
        {
            if (await VerifyLocalBazaarHealthzAsync(port, cancellationToken))
            {
                string msg = $"检测到本机单机服务端（BazaarServer）已在 {localBase} 健康运行，直接复用。";
                log?.Invoke($"[单机服] {msg}");
                return (true, msg);
            }

            string conflictMsg = $"本地端口 127.0.0.1:{port} 已被其他非 BazaarServer 程序占用（/healthz 校验未通过），请先关闭占用该端口的程序后重试。";
            log?.Invoke($"[错误] {conflictMsg}");
            return (false, conflictMsg);
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

        // D7-P1-03：强制仅绑定回环地址 127.0.0.1，严禁单机服暴露公网/局域网
        psi.Environment["BAZAAR_BIND"] = "127.0.0.1";
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

        log?.Invoke($"[单机服] 正在启动本地服务端（127.0.0.1:{port}）：{serverExe}");
        log?.Invoke($"[单机服] 存档目录：{stateDir} ｜ 数据目录：{dataDir ?? "默认"}");

        Process proc = Process.Start(psi) ?? throw new InvalidOperationException("无法拉起 BazaarServer.exe 进程。");
        var job = new ManagedJobObject();
        if (job.TryAssignProcess(proc))
        {
            log?.Invoke($"[单机服] 已绑定 Windows Job Object（PID={proc.Id}，防孤儿进程保护已启用）。");
        }

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
            _jobObject?.Dispose();
            _jobObject = job;
            _serverProcess = proc;
            _startedByUs = true;
        }

        // 轮询 /healthz（校验 BazaarServer 专属 JSON 指纹），最长等待 30 秒
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

            if (await VerifyLocalBazaarHealthzAsync(port, cancellationToken))
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
    /// 优雅停止由本启动器拉起的单机服务端进程（D7-P1-03）：
    /// 拉起独立短命辅助子进程 --send-ctrl-c 投递 CTRL_C_EVENT 触发 ASP.NET Core ApplicationStopping 刷盘落库，
    /// 5 秒超时未退出才降级 Kill(entireProcessTree: true)。
    /// </summary>
    public void StopManagedServer(Action<string>? log = null)
    {
        Process? proc;
        ManagedJobObject? job;
        lock (_gate)
        {
            if (!_startedByUs || _serverProcess == null)
            {
                return;
            }
            proc = _serverProcess;
            job = _jobObject;
            _serverProcess = null;
            _jobObject = null;
            _startedByUs = false;
        }

        try
        {
            if (!proc.HasExited)
            {
                log?.Invoke($"[单机服] 正在优雅停止本地服务端（PID={proc.Id}，发送 CTRL_C 触发 SQLite WAL 刷盘）...");
                bool gracefulSignaled = TrySendCtrlCViaHelperProcess(proc.Id);
                if (!gracefulSignaled)
                {
                    try { proc.CloseMainWindow(); } catch { }
                }

                if (!proc.WaitForExit(5000))
                {
                    log?.Invoke($"[警告] 本地服务端 5 秒内未完成优雅退出，正在强制终止进程树（PID={proc.Id}）...");
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
            job?.Dispose();
        }
    }

    private static bool TrySendCtrlCViaHelperProcess(int targetPid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            string? selfExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(selfExe) || !File.Exists(selfExe))
            {
                return false;
            }

            var psi = new ProcessStartInfo(selfExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--send-ctrl-c");
            psi.ArgumentList.Add(targetPid.ToString());

            using Process? helper = Process.Start(psi);
            if (helper == null)
            {
                return false;
            }

            helper.WaitForExit(1500);
            return helper.HasExited && helper.ExitCode == 0;
        }
        catch
        {
            return false;
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
