using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BazaarGameShared;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using MessagePack;

namespace BazaarProbe;

/// <summary>
/// 官方协议探针：直接用客户端自带的 BazaarGameShared.dll + MessagePack.dll
/// 做「序列化 / 反序列化 / 转 JSON」，用来验证线格式与服务端契约。
///
///   BazaarProbe selftest            自检 + 打印 InitializeRunCommand 的字节布局
///   BazaarProbe dump  &lt;file.bin&gt;    把抓包文件解析成 INetMessage 并打印 JSON
///   BazaarProbe extractions         打印命令/消息的 union key 与字段表
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var normArgs = args.SkipWhile(a => a == "--").ToArray();
        var cmd = normArgs.Length > 0 ? normArgs[0] : "selftest";
        return cmd switch
        {
            "selftest" => SelfTest(),
            "dump" => Dump(normArgs.Length > 1 ? normArgs[1] : throw new ArgumentException("missing file")),
            "extractions" => Extractions(),
            "e2e" => E2E.Run(normArgs.Length > 1 ? normArgs[1] : "http://127.0.0.1:8080").GetAwaiter().GetResult(),
            "runloop" => RunLoop.Run(normArgs.Length > 1 ? normArgs[1] : "http://127.0.0.1:8080").GetAwaiter().GetResult(),
            "fuzz" => RunFuzzCommand(normArgs.Skip(1).ToArray()),
            _ => Usage(),
        };
    }

    private static int RunFuzzCommand(string[] rest)
    {
        int count = 100;
        string url = "http://127.0.0.1:8080";
        string logPath = null;
        int concurrency = 1;
        bool includeTournaments = false;
        int? explicitTournamentCount = null;
        string outJsonPath = null;

        var positional = new List<string>();
        for (int i = 0; i < rest.Length; i++)
        {
            string arg = rest[i];
            if (arg == "--log" && i + 1 < rest.Length) { logPath = rest[++i]; continue; }
            if (arg == "--url" && i + 1 < rest.Length) { url = rest[++i]; continue; }
            if (arg == "--count" && i + 1 < rest.Length && int.TryParse(rest[++i], out int c)) { count = c; continue; }
            if (arg == "--concurrency" && i + 1 < rest.Length && int.TryParse(rest[++i], out int conc)) { concurrency = Math.Max(1, conc); continue; }
            if (arg == "--include-tournaments") { includeTournaments = true; continue; }
            if (arg == "--tournament-count" && i + 1 < rest.Length && int.TryParse(rest[++i], out int tc))
            {
                explicitTournamentCount = Math.Max(0, tc);
                if (tc > 0) includeTournaments = true;
                continue;
            }
            if (arg == "--out" && i + 1 < rest.Length) { outJsonPath = rest[++i]; continue; }
            positional.Add(arg);
        }

        // 保留旧版位置参数兼容：fuzz <runs> [url] [logPath]
        if (positional.Count > 0 && int.TryParse(positional[0], out int posCount)) count = posCount;
        if (positional.Count > 1) url = positional[1];
        if (positional.Count > 2) logPath = positional[2];

        int tournamentCount = includeTournaments ? (explicitTournamentCount ?? 20) : 0;
        var options = new FuzzOptions(
            TotalRuns: count,
            BaseUrl: url,
            LogPath: logPath,
            Concurrency: concurrency,
            IncludeTournaments: includeTournaments,
            TournamentCount: tournamentCount,
            OutReportPath: outJsonPath);

        return Fuzz.Run(options).GetAwaiter().GetResult();
    }

    private static int Usage()
    {
        Console.WriteLine(
            "usage: BazaarProbe [--] [selftest|dump <file.bin>|extractions|e2e [url]|runloop [url]|" +
            "fuzz [count] [url] [logPath] [--url <baseUrl>] [--count <N>] [--log <path>] [--concurrency <N>] " +
            "[--include-tournaments] [--tournament-count <M>] [--out <jsonReportPath>]]");
        return 2;
    }

    private static int SelfTest()
    {
        var runGuid = Guid.Parse("01a1f3bc-3677-7ad6-afe3-9635997ba3d4");
        var init = new InitializeRunCommand
        {
            PlayMode = EPlayMode.Tournament,
            SelectedHero = EHero.Vanessa,
            TournamentSessionId = runGuid,
            GameModeId = null,
        };

        byte[] bytes = MessagePackSerializer.Serialize<INetCommand>(init, MessagePackConfig.Options);
        Console.WriteLine($"InitializeRunCommand -> {bytes.Length} bytes");
        Console.WriteLine("hex: " + Convert.ToHexString(bytes));

        INetCommand back = MessagePackSerializer.Deserialize<INetCommand>(bytes, MessagePackConfig.Options);
        Console.WriteLine("roundtrip type: " + back.GetType().Name);
        if (back is InitializeRunCommand r)
        {
            Console.WriteLine($"  PlayMode={r.PlayMode} Hero={r.SelectedHero} TournamentSessionId={r.TournamentSessionId}");
        }

        Console.WriteLine();
        Console.WriteLine("json: " + MessagePackSerializer.ConvertToJson(bytes, MessagePackConfig.Options));

        // 小包不压缩；>1024 字节会走 Lz4BlockArray，用一个大对象验证
        var big = new InitializeRunCommand { PlayMode = EPlayMode.Ranked, SelectedHero = EHero.Mak };
        byte[] bigBytes = MessagePackSerializer.Serialize<INetCommand>(big, MessagePackConfig.Options);
        Console.WriteLine($"small payload bytes={bigBytes.Length} (compressionMinLength=1024)");

        return 0;
    }

    private static int Dump(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Console.WriteLine($"{path}: {bytes.Length} bytes");
        try
        {
            INetMessage msg = MessagePackSerializer.Deserialize<INetMessage>(bytes, MessagePackConfig.Options);
            Console.WriteLine("type: " + msg.GetType().FullName);
            Console.WriteLine(MessagePackSerializer.ConvertToJson(bytes, MessagePackConfig.Options));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("as INetMessage failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        try
        {
            INetCommand cmd = MessagePackSerializer.Deserialize<INetCommand>(bytes, MessagePackConfig.Options);
            Console.WriteLine("as INetCommand type: " + cmd.GetType().FullName);
            Console.WriteLine(MessagePackSerializer.ConvertToJson(bytes, MessagePackConfig.Options));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("as INetCommand failed: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static int Extractions()
    {
        var asm = typeof(INetCommand).Assembly;
        Console.WriteLine("BazaarGameShared: " + asm.GetName().Version + " / " + asm.Location);
        Console.WriteLine();
        Console.WriteLine("== INetCommand unions ==");
        foreach (var t in asm.GetTypes().Where(t => typeof(INetCommand).IsAssignableFrom(t) && !t.IsInterface))
        {
            var key = t.GetCustomAttributes(false)
                .Where(a => a.GetType().Name == "UnionAttribute")
                .Select(a => a.GetType().GetProperty("Key")?.GetValue(a))
                .FirstOrDefault();
            Console.WriteLine($"  key={key}  {t.Name}");
        }
        Console.WriteLine();
        Console.WriteLine("== INetMessage unions ==");
        foreach (var t in asm.GetTypes().Where(t => typeof(INetMessage).IsAssignableFrom(t) && !t.IsInterface))
        {
            var key = t.GetCustomAttributes(false)
                .Where(a => a.GetType().Name == "UnionAttribute")
                .Select(a => a.GetType().GetProperty("Key")?.GetValue(a))
                .FirstOrDefault();
            Console.WriteLine($"  key={key}  {t.Name}");
        }
        return 0;
    }
}
