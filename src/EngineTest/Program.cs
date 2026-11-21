using System;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 1) 引擎程序集能不能在 .NET 8 上加载
        var shared = typeof(BazaarGameShared.Infra.Commands.INetCommand).Assembly;
        var battle = Assembly.Load("BazaarBattleService");
        var unity  = Assembly.Load("BazaarUnityTools");
        Console.WriteLine("BazaarGameShared : " + shared.GetName().Version);
        Console.WriteLine("BazaarBattleService: " + battle.GetName().Version);
        Console.WriteLine("BazaarUnityTools  : " + unity.GetName().Version);

        // 2) 关键类型能否实例化 / 调用
        var seedMgrType = unity.GetType("TempoUnityUtils.TempoSeedManager");
        Console.WriteLine("TempoSeedManager  : " + (seedMgrType != null ? "OK" : "MISSING"));
        var sm = Activator.CreateInstance(seedMgrType);
        seedMgrType.GetMethod("Initialize").Invoke(sm, new object[] { 12345 });
        double d = (double)seedMgrType.GetMethod("GetDouble").Invoke(sm, null);
        Console.WriteLine("GetDouble(seed=12345) = " + d.ToString("F17"));

        // 3) 卡牌交易器（战斗引擎核心）能否加载
        var dealerType = battle.GetType("BazaarBattleService.BazaarCardDealer");
        Console.WriteLine("BazaarCardDealer  : " + (dealerType != null ? "OK" : "MISSING"));

        // 4) 枚举公开面
        var enumType = shared.GetType("BazaarGameShared.Domain.Core.Types.EHero");
        Console.WriteLine("EHero values: " + string.Join(",", Enum.GetNames(enumType)));

        // 5) MessagePack 往返
        var init = new BazaarGameShared.Infra.Commands.InitializeRunCommand
        {
            PlayMode = BazaarGameShared.Domain.Core.Types.EPlayMode.Tournament,
            SelectedHero = BazaarGameShared.Domain.Core.Types.EHero.Vanessa,
        };
        byte[] bytes = MessagePack.MessagePackSerializer.Serialize<BazaarGameShared.Infra.Commands.INetCommand>(
            init, BazaarGameShared.MessagePackConfig.Options);
        Console.WriteLine("InitializeRunCommand bytes = " + bytes.Length);
        Console.WriteLine("hex = " + Convert.ToHexString(bytes));

        Console.WriteLine();
        Console.WriteLine("=== ALL GREEN: engine runs on .NET " + Environment.Version + " ===");
    }
}
