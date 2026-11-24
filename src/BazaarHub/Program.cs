using System.Runtime.InteropServices;
using System.Text;
using BazaarHub.Services;
using BazaarHub.UI;

namespace BazaarHub;

internal static class Program
{
    private const int AttachParentProcess = -1;
    private const uint CtrlCEvent = 0;

    private delegate bool ConsoleCtrlHandlerRoutine(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandlerRoutine? handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    [STAThread]
    private static int Main(string[] args)
    {
        // D7-P1-03：短命独立辅助子进程发送 CTRL_C_EVENT，彻底隔离自身与主进程/终端控制台防自杀
        int ctrlCIdx = Array.FindIndex(args, a => string.Equals(a, "--send-ctrl-c", StringComparison.OrdinalIgnoreCase));
        if (ctrlCIdx >= 0)
        {
            if (ctrlCIdx + 1 >= args.Length || !int.TryParse(args[ctrlCIdx + 1], out int targetPid) || targetPid <= 0)
            {
                return 2;
            }
            return SendCtrlCToTargetProcess(targetPid);
        }

        // 支持无头自动化自检模式：不创建、不弹出任何 GUI 窗口，验证通过返回 0
        if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            EnsureConsoleForHeadlessCli();
            try
            {
                return SelfTestRunner.RunAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FATAL] --self-test 执行异常：{ex}");
                return 1;
            }
            finally
            {
                Console.Out.Flush();
                Console.Error.Flush();
            }
        }

        // 支持端到端无头客户端热更新契约联调：--verify-client-update-sync <serverUrl> <gameDir>
        int verifyIdx = Array.FindIndex(args, a => string.Equals(a, "--verify-client-update-sync", StringComparison.OrdinalIgnoreCase));
        if (verifyIdx >= 0)
        {
            EnsureConsoleForHeadlessCli();
            try
            {
                if (verifyIdx + 2 >= args.Length)
                {
                    Console.Error.WriteLine("[FATAL] 用法：BazaarHub.exe --verify-client-update-sync <serverUrl> <gameDir>");
                    return 2;
                }

                string serverUrl = args[verifyIdx + 1];
                string gameDir = args[verifyIdx + 2];
                return SelfTestRunner.VerifyClientUpdateSyncAsync(serverUrl, gameDir).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FATAL] --verify-client-update-sync 执行异常：{ex}");
                return 1;
            }
            finally
            {
                Console.Out.Flush();
                Console.Error.Flush();
            }
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
        return 0;
    }

    private static int SendCtrlCToTargetProcess(int targetPid)
    {
        try
        {
            // 先脱离任何继承控制台，附着目标隐藏控制台并永久屏蔽本辅助进程自身 Ctrl 处理直至退出
            FreeConsole();
            if (!AttachConsole(targetPid))
            {
                return 1;
            }

            SetConsoleCtrlHandler(null, true);
            bool sent = GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            if (sent)
            {
                Thread.Sleep(50);
            }
            return sent ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static void EnsureConsoleForHeadlessCli()
    {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (Console.IsOutputRedirected)
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8NoBom) { AutoFlush = true });
        }

        if (Console.IsErrorRedirected)
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8NoBom) { AutoFlush = true });
        }

        if (!Console.IsOutputRedirected && !Console.IsErrorRedirected)
        {
            if (AttachConsole(AttachParentProcess))
            {
                SetConsoleOutputCP(65001);
                try
                {
                    Console.OutputEncoding = utf8NoBom;
                }
                catch
                {
                    // 忽略非标准终端编码设置异常
                }
            }
        }
    }
}
