using System;
using System.IO;
using System.Net.Http;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace BazaarLauncher;

/// <summary>
/// 从最底层抓 HTTP 流量：patch HttpClient.SendAsync。
///
/// 为什么不 patch HttpGameClient.SendCommand：它是泛型方法，Harmony 无法生成
/// 非泛型 trampoline，会报 "IL Compile Error"。
/// HttpClient.SendAsync 是非泛型，稳定可 patch，且能同时抓到 JSON 与 msgpack。
/// </summary>
internal static class TrafficDump
{
    private static long _seq;

    internal static void Apply(Harmony harmony)
    {
        try
        {
            string dir = ResolveDir();
            Directory.CreateDirectory(dir);
            BazaarLauncherPlugin.Log.LogInfo("Traffic dump (HttpClient level) -> " + dir);

            var sendAsync = AccessTools.Method(typeof(HttpClient), "SendAsync",
                new[] { typeof(HttpRequestMessage), typeof(System.Threading.CancellationToken) });
            if (sendAsync == null)
            {
                BazaarLauncherPlugin.Log.LogWarning("HttpClient.SendAsync not found");
                return;
            }

            harmony.Patch(sendAsync,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TrafficDump), nameof(Prefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TrafficDump), nameof(Postfix))));
            BazaarLauncherPlugin.Log.LogInfo("Patched HttpClient.SendAsync for dumping");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("traffic dump patch failed: " + ex.Message);
        }
    }

    private static string ResolveDir()
    {
        string dir = BazaarLauncherPlugin.DumpDir?.Value;
        if (string.IsNullOrWhiteSpace(dir) || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            dir = Path.Combine(Path.GetTempPath(), "bazaar-trace");
        }
        return Environment.ExpandEnvironmentVariables(dir);
    }

    /// <summary>请求侧：记录 method / uri / headers / body。</summary>
    internal static void Prefix(ref HttpRequestMessage __0, out DumpCtx __state)
    {
        __state = null;
        try
        {
            if (__0 == null) return;
            string uri = __0.RequestUri?.ToString() ?? "";
            // 只关心自建服与对局端点
            if (!uri.Contains("/sessions") && !uri.Contains("/commands") && !uri.Contains("/api/"))
                return;

            string body = "";
            if (__0.Content != null)
            {
                try
                {
                    var bytes = __0.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    body = $"[{bytes.Length} bytes] {ToHexOrText(bytes)}";
                }
                catch (Exception e) { body = "<unreadable: " + e.Message + ">"; }
            }

            var headers = new StringBuilder();
            foreach (var h in __0.Headers)
                headers.Append(h.Key).Append(": ").Append(string.Join(",", h.Value)).Append("; ");
            if (__0.Content != null)
                foreach (var h in __0.Content.Headers)
                    headers.Append(h.Key).Append(": ").Append(string.Join(",", h.Value)).Append("; ");

            long n = System.Threading.Interlocked.Increment(ref _seq);
            string stem = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{n:D4}-{Sanitize(uri)}";
            string dir = ResolveDir();
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, stem + "-req.txt"),
                $"{__0.Method} {uri}\nHEADERS: {headers}\nBODY: {body}", Encoding.UTF8);

            __state = new DumpCtx { Dir = dir, Stem = stem, Uri = uri, Method = __0.Method.Method };
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("dump prefix: " + ex.Message);
        }
    }

    /// <summary>响应侧：记录 status / headers / body。</summary>
    internal static void Postfix(object __result, DumpCtx __state)
    {
        if (__state == null) return;
        try
        {
            if (!(__result is HttpResponseMessage resp)) return;
            var headers = new StringBuilder();
            foreach (var h in resp.Headers)
                headers.Append(h.Key).Append(": ").Append(string.Join(",", h.Value)).Append("; ");
            foreach (var h in resp.Content.Headers)
                headers.Append(h.Key).Append(": ").Append(string.Join(",", h.Value)).Append("; ");

            string stem = __state.Stem + "-res.txt";
            string path = Path.Combine(__state.Dir, stem);

            // 先写头部，保证即使 body 读不到也有记录
            File.WriteAllText(path,
                $"{(int)resp.StatusCode} {resp.StatusCode}\nHEADERS: {headers}\nBODY: <streaming, see .bin>",
                Encoding.UTF8);

            // 后台异步读 body（对 ResponseHeadersRead 也有效）
            string binPath = Path.Combine(__state.Dir, __state.Stem + "-res.bin");
            string uri = __state.Uri;
            var respCopy = resp;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    byte[] bytes = await respCopy.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes.Length == 0) return;
                    await File.WriteAllBytesAsync(binPath, bytes).ConfigureAwait(false);

                    string text = $"{(int)respCopy.StatusCode} {respCopy.StatusCode}\nHEADERS: {headers}\nBODY: [{bytes.Length} bytes]\n{Preview(bytes)}";
                    await File.WriteAllTextAsync(path, text, Encoding.UTF8).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        await File.AppendAllTextAsync(path, "\n<body read failed: " + ex.Message + ">").ConfigureAwait(false);
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("dump postfix: " + ex.Message);
        }
    }

    private static string Preview(byte[] bytes)
    {
        if (bytes.Length == 0) return "<empty>";
        // 看起来像 JSON 就给文本
        byte b0 = bytes[0];
        if (b0 == (byte)'{' || b0 == (byte)'[')
        {
            string s = Encoding.UTF8.GetString(bytes);
            return s.Length > 4000 ? s.Substring(0, 4000) + "…" : s;
        }
        var sb = new StringBuilder();
        int limit = Math.Min(bytes.Length, 1024);
        for (int i = 0; i < limit; i++) sb.Append(bytes[i].ToString("X2")).Append(' ');
        if (bytes.Length > limit) sb.Append($"… ({bytes.Length - limit} more)");
        return "HEX: " + sb.ToString();
    }

    private static string ToHexOrText(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "<empty>";
        // msgpack 以二进制为主，直接给 hex；JSON 给文本
        if (bytes.Length > 2 && bytes[0] == (byte)'{' || (bytes.Length > 0 && bytes[0] == (byte)'['))
        {
            return Encoding.UTF8.GetString(bytes);
        }
        var sb = new StringBuilder(bytes.Length * 3);
        int limit = Math.Min(bytes.Length, 2048);
        for (int i = 0; i < limit; i++) sb.Append(bytes[i].ToString("X2")).Append(' ');
        if (bytes.Length > limit) sb.Append($"… ({bytes.Length - limit} more)");
        return sb.ToString();
    }

    private static string Sanitize(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "unknown";
        var sb = new StringBuilder();
        foreach (char c in uri)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == '/' || c == '?' || c == '=' || c == '&') sb.Append('_');
        }
        string s = sb.ToString();
        if (s.Length > 60) s = s.Substring(s.Length - 60);
        return s.Trim('_');
    }

    internal sealed class DumpCtx
    {
        public string Dir;
        public string Stem;
        public string Uri;
        public string Method;
    }
}
