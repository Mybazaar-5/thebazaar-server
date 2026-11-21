using System;
using System.Linq;
using System.Reflection;

namespace EngineSmoke;

/// <summary>
/// 冒烟测试：确认官方引擎程序集能在 .NET 8（Linux/Windows）上被加载并实例化。
/// 这是「服务端直接复用官方引擎」这条路线的前置条件。
/// </summary>
internal static class Program
{
    private static int Main()
    {
        int fails = 0;

        void Check(string label, Func<string> probe)
        {
            try
            {
                Console.WriteLine($"  OK   {label}: {probe()}");
            }
            catch (Exception ex)
            {
                fails++;
                Console.WriteLine($"  FAIL {label}: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"         inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
            }
        }

        Console.WriteLine($"runtime: {Environment.Version} | {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine();

        Console.WriteLine("== 1. 加载官方程序集 ==");
        Assembly shared = null, battle = null, tools = null;
        Check("BazaarGameShared", () => (shared = Assembly.Load("BazaarGameShared")).GetName().Version.ToString());
        Check("BazaarUnityTools", () => (tools = Assembly.Load("BazaarUnityTools")).GetName().Version.ToString());
        Check("BazaarBattleService", () => (battle = Assembly.Load("BazaarBattleService")).GetName().Version.ToString());
        Check("MessagePack", () => Assembly.Load("MessagePack").GetName().Version.ToString());

        Console.WriteLine();
        Console.WriteLine("== 2. 关键类型可解析 ==");
        Check("BazaarCardDealer", () =>
        {
            var t = battle.GetType("BazaarBattleService.BazaarCardDealer", true);
            return $"{t.FullName} methods={t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Length}";
        });
        Check("BazaarBattleSettings", () =>
        {
            var t = battle.GetType("BazaarBattleService.BazaarBattleSettings", true);
            var inst = Activator.CreateInstance(t);
            var p = t.GetProperty("StaticFolderPath");
            return $"StaticFolderPath default='{p.GetValue(inst)}'";
        });
        Check("TempoSeedManager", () =>
        {
            var t = tools.GetType("TempoUnityUtils.TempoSeedManager", true);
            var inst = Activator.CreateInstance(t);
            t.GetMethod("Initialize").Invoke(inst, new object[] { 12345 });
            var d = (double)t.GetMethod("GetDouble").Invoke(inst, null);
            return $"seed=12345 first GetDouble()={d.ToString("G17")}";
        });
        Check("MessagePackConfig.Options", () =>
        {
            var t = shared.GetType("BazaarGameShared.MessagePackConfig", true);
            var opt = t.GetField("Options", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            return opt.GetType().Name + " ok";
        });

        Console.WriteLine();
        Console.WriteLine("== 3. 实例化 CardDealer（不初始化静态数据）==");
        object dealer = null;
        Check("new BazaarCardDealer()", () =>
        {
            var t = battle.GetType("BazaarBattleService.BazaarCardDealer", true);
            dealer = Activator.CreateInstance(t);
            var gm = t.GetField("gameConfig").GetValue(dealer);
            return "instance created, gameConfig=" + (gm != null ? "present" : "null");
        });

        Console.WriteLine();
        Console.WriteLine("== 4. 战斗相关公开成员 ==");
        Check("SimulateCombat", () =>
        {
            var t = battle.GetType("BazaarBattleService.BazaarCardDealer", true);
            var m = t.GetMethod("SimulateCombat", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return m != null ? "found public SimulateCombat()" : "NOT FOUND";
        });
        Check("BazaarCombatSimData", () =>
        {
            var t = battle.GetType("BazaarBattleService.Models.BazaarCombatSimData", false);
            return t != null ? t.FullName : "NOT FOUND";
        });

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL OK" : $"FAILURES: {fails}");
        return fails == 0 ? 0 : 1;
    }
}
