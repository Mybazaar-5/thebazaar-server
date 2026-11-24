using System.Collections.Concurrent;
using System.Text;
using BazaarGameShared.Infra.Messages;
using BazaarServer.Protocol;

namespace BazaarServer.Endpoints;

/// <summary>
/// 把每条响应的 MessagePack 字节与 JSON 视图落盘，用于对齐客户端行为。
///
/// 为什么在服务端做：客户端的 Harmony 补丁打在泛型方法上会 IL 编译失败，
/// 而服务端本来就在生成这些字节，直接记下来最省事。
///
/// 开启：环境变量 BAZAAR_DEBUG_DUMP=1，或 data/debug-dump.on 存在。
/// 输出：$BAZAAR_DUMP_DIR（默认 ./logs/dump）
/// </summary>
public static class DebugDump
{
    private static readonly ConcurrentDictionary<string, int> _counters = new(StringComparer.Ordinal);
    private const int MaxTrackedCounterSessions = 2048;
    private static readonly object _lock = new();

    public static bool Enabled { get; } = ResolveEnabled();
    public static string Dir { get; } = ResolveDir();

    private static bool ResolveEnabled()
    {
        if (Environment.GetEnvironmentVariable("BAZAAR_DEBUG_DUMP") == "1") return true;
        try
        {
            string data = Environment.GetEnvironmentVariable("BAZAAR_DATA")
                          ?? Path.Combine(AppContext.BaseDirectory, "data");
            return File.Exists(Path.Combine(data, "debug-dump.on"));
        }
        catch { return false; }
    }

    private static string ResolveDir()
    {
        string dir = Environment.GetEnvironmentVariable("BAZAAR_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(AppContext.BaseDirectory, "dump");
        }
        // D8-P2-05：默认禁用期间严禁在静态构造函数预先创建空 dump 目录，推迟至 Write 真正落盘时懒创建
        return dir;
    }

    public static void Write(string sid, long rid, byte[] payload, INetMessage message)
    {
        if (!Enabled) return;
        try
        {
            string json;
            try { json = Codec.ToJson(message); }
            catch (Exception ex) { json = "<json failed: " + ex.Message + ">"; }

            // 摘要：事件类型 + 关键属性，方便一眼看出问题
            string summary = Summarize(message);

            string shortSid = (sid ?? "nosid").Length > 12 ? sid[..12] : (sid ?? "nosid");
            if (_counters.Count >= MaxTrackedCounterSessions && !_counters.ContainsKey(shortSid))
                _counters.Clear();
            int n = _counters.AddOrUpdate(shortSid, 1, (_, v) => v + 1);
            string stem = $"{DateTime.UtcNow:HHmmss-fff}-{shortSid}-{n:D4}-rid{rid}";

            lock (_lock)
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(Path.Combine(Dir, stem + ".bin"), payload);
                File.WriteAllText(Path.Combine(Dir, stem + ".json"),
                    $"SUMMARY: {summary}\n\nJSON: {json}", Encoding.UTF8);
            }
        }
        catch { }
    }

    /// <summary>把消息压成一行人能读的摘要。</summary>
    private static string Summarize(INetMessage message)
    {
        try
        {
            if (message is NetMessageAggregate agg)
            {
                var parts = new List<string>();
                foreach (var m in agg.Messages) parts.Add(Summarize(m));
                return "Aggregate[" + string.Join(" | ", parts) + "]";
            }

            if (message is NetMessageGameSim gs)
            {
                var sb = new StringBuilder();
                sb.Append("GameSim(");
                sb.Append($"gold={Attr(gs.Data.Player, "Gold")},");
                sb.Append($"income={Attr(gs.Data.Player, "Income")},");
                sb.Append($"prestige={Attr(gs.Data.Player, "Prestige")},");
                sb.Append($"day={gs.Data.Run.Day},hour={gs.Data.Run.Hour},");
                sb.Append($"state={gs.Data.CurrentState?.StateName},");
                sb.Append($"sel={gs.Data.CurrentState?.SelectionSet?.Count ?? 0},");
                sb.Append($"cards={gs.Data.Cards?.Count ?? 0},");
                sb.Append($"events=[{string.Join(",", gs.Data.Events.Select(e => e.GetType().Name.Replace("GameSimEvent", "")))}]");
                sb.Append(")");
                return sb.ToString();
            }

            if (message is NetMessageGameStateSync sync)
            {
                return $"GameStateSync(day={sync.Data.Run.Day},hour={sync.Data.Run.Hour}," +
                       $"state={sync.Data.CurrentState?.StateName}," +
                       $"gold={Attr(sync.Data.Player, "Gold")}," +
                       $"sel={sync.Data.CurrentState?.SelectionSet?.Count ?? 0}," +
                       $"cards={sync.Data.Cards?.Count ?? 0})";
            }

            if (message is NetMessageRunInitialized ri)
            {
                return $"RunInitialized(run={ri.RunId},mode={ri.PlayMode})";
            }

            if (message is NetMessageError err)
            {
                return $"Error({err.ErrorType}: {err.Message})";
            }

            return message.GetType().Name;
        }
        catch (Exception ex)
        {
            return "<summary failed: " + ex.Message + ">";
        }
    }

    private static string Attr(BazaarGameShared.Infra.Messages.GameSimEvents.SimUpdatePlayer p, string name)
    {
        if (p?.Attributes == null) return "-";
        if (!Enum.TryParse<BazaarGameShared.Domain.Core.Types.EPlayerAttributeType>(name, out var t)) return "-";
        return p.Attributes.TryGetValue(t, out int v) ? v.ToString() : "-";
    }

    private static string Attr(BazaarGameShared.Infra.Messages.PlayerSnapshotDTO p, string name)
    {
        if (p?.Attributes == null) return "-";
        if (!Enum.TryParse<BazaarGameShared.Domain.Core.Types.EPlayerAttributeType>(name, out var t)) return "-";
        return p.Attributes.TryGetValue(t, out int v) ? v.ToString() : "-";
    }
}
