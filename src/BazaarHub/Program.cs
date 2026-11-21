using System.Runtime.InteropServices;
using System.Text;
using BazaarHub.Services;
using BazaarHub.UI;

namespace BazaarHub;

internal static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    [STAThread]
    private static int Main(string[] args)
    {
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

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
        return 0;
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
