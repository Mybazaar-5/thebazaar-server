using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using HarmonyLib;

namespace BazaarLauncher;

/// <summary>
/// HTTP 流量诊断抓包模块（修复 P0-3）：
///   1. 请求侧严禁同步调用 ReadAsByteArrayAsync().GetAwaiter().GetResult()，避免死锁与消耗不可重读 StreamContent；
///   2. 响应侧正确识别 Task&lt;HttpResponseMessage&gt; 并使用 ContinueWith 异步落盘响应头与正文，彻底修复死代码问题；
///   3. 同时覆盖 2 参数与底层 3 参数 HttpClient.SendAsync。
/// </summary>
internal static class TrafficDump
{
    private static long _seq;
    private static bool _applied;

    internal static void ResetAppliedFlag() => _applied = false;

    internal static void Apply(SafeHarmonyPatcher patcher)
    {
        if (_applied || patcher == null) return;
        _applied = true;

        try
        {
            string dir = ResolveDir();
            Directory.CreateDirectory(dir);
            BazaarLauncherPlugin.Log?.LogInfo("TrafficDump 已启用，抓包目录 -> " + dir);

            var prefix = AccessTools.Method(typeof(TrafficDump), nameof(Prefix));
            var postfix = AccessTools.Method(typeof(TrafficDump), nameof(Postfix));

            // D4-P1-7：仅挂底层 3 参 HttpClient.SendAsync，消除 2 参转发 3 参造成的双倍重复落盘
            patcher.TryPatch(
                typeof(HttpClient),
                "SendAsync",
                new[] { typeof(HttpRequestMessage), typeof(HttpCompletionOption), typeof(CancellationToken) },
                prefix: prefix,
                postfix: postfix,
                optional: true,
                label: "TrafficDump.SendAsync(3)");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("TrafficDump 挂载失败: " + ex.Message);
        }
    }

    internal static void Apply(Harmony harmony)
    {
        if (_applied || harmony == null) return;
        Apply(new SafeHarmonyPatcher("trafficdump.legacy"));
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

    private const long MaxBufferDumpBytes = 4 * 1024 * 1024;

    internal static void Prefix(HttpRequestMessage __0, HttpCompletionOption __1, out DumpCtx __state)
    {
        __state = null;
        try
        {
            if (__0 == null) return;
            string uri = __0.RequestUri?.ToString() ?? string.Empty;
            if (!uri.Contains("/sessions") && !uri.Contains("/commands") && !uri.Contains("/api/"))
            {
                return;
            }

            var headers = new StringBuilder();
            foreach (var h in __0.Headers)
                headers.Append(h.Key).Append(": ").Append(string.Join(",", h.Value)).Append("; ");
            if (__0.Content != null)
            {
                foreach (var h in __0.Content.Headers)
                    headers.Append(h.Key).Append(": ").Append(string.Join(",", h.Value)).Append("; ");
            }

            string bodyNote = "<empty>";
            bool safeBufferable = false;
            if (__0.Content != null)
            {
                if (__0.Content is StringContent || __0.Content is ByteArrayContent)
                {
                    safeBufferable = true;
                    bodyNote = $"<{__0.Content.GetType().Name}, 异步记录中>";
                }
                else
                {
                    bodyNote = $"<{__0.Content.GetType().Name}, 流式体不同步读取以防破坏请求>";
                }
            }

            long n = Interlocked.Increment(ref _seq);
            string stem = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{n:D4}-{Sanitize(uri)}";
            string dir = ResolveDir();

            string reqLine = $"{__0.Method} {uri}\nHEADERS: {headers}\nBODY: {bodyNote}";
            string reqPath = Path.Combine(dir, stem + "-req.txt");
            var contentRef = safeBufferable ? __0.Content : null;

            _ = Task.Run(async () =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string finalText = reqLine;
                    if (contentRef != null)
                    {
                        byte[] bytes = await contentRef.ReadAsByteArrayAsync().ConfigureAwait(false);
                        finalText = $"{__0.Method} {uri}\nHEADERS: {headers}\nBODY: [{bytes.Length} bytes] {ToHexOrText(bytes)}";
                    }
                    await File.WriteAllTextAsync(reqPath, finalText, Encoding.UTF8).ConfigureAwait(false);
                }
                catch { }
            });

            __state = new DumpCtx
            {
                Dir = dir,
                Stem = stem,
                Uri = uri,
                Method = __0.Method.Method,
                CompletionOption = __1,
            };
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("TrafficDump Prefix 异常: " + ex.Message);
        }
    }

    internal static void Postfix(ref Task<HttpResponseMessage> __result, DumpCtx __state)
    {
        if (__state == null || __result == null) return;
        try
        {
            __result = WrapSendAsync(__result, __state);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("TrafficDump Postfix 异常: " + ex.Message);
        }
    }

    private static async Task<HttpResponseMessage> WrapSendAsync(Task<HttpResponseMessage> innerTask, DumpCtx state)
    {
        HttpResponseMessage resp = await innerTask.ConfigureAwait(false);
        if (resp == null) return null;

        try
        {
            var headers = new StringBuilder();
            foreach (var h in resp.Headers)
                headers.Append(h.Key).Append(": ").Append(string.Join(",", h.Value)).Append("; ");
            if (resp.Content != null)
            {
                foreach (var h in resp.Content.Headers)
                    headers.Append(h.Key).Append(": ").Append(string.Join(",", h.Value)).Append("; ");
            }

            string contentType = resp.Content?.Headers?.ContentType?.MediaType ?? string.Empty;
            long? contentLen = resp.Content?.Headers?.ContentLength;
            bool skipBodyBuffer = state.CompletionOption == HttpCompletionOption.ResponseHeadersRead
                || contentType.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0
                || (contentLen.HasValue && contentLen.Value > MaxBufferDumpBytes);

            byte[] copiedBytes = null;
            string skipReason = null;
            if (resp.Content == null)
            {
                skipReason = "<empty>";
            }
            else if (skipBodyBuffer)
            {
                skipReason = state.CompletionOption == HttpCompletionOption.ResponseHeadersRead
                    ? "<ResponseHeadersRead 流式响应已跳过缓冲>"
                    : contentType.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "<SSE 流式响应已跳过全量读取>"
                        : $"<超大响应体 ({contentLen} bytes > 4MB) 已跳过缓冲>";
            }
            else
            {
                // D4-P1-7：在调用方读取前先完成安全缓冲，确保调用方与抓包均可独立无竞态读取完整字节副本
                await resp.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                byte[] raw = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (raw != null && raw.Length > 0)
                {
                    copiedBytes = new byte[raw.Length];
                    Buffer.BlockCopy(raw, 0, copiedBytes, 0, raw.Length);
                }
                else
                {
                    copiedBytes = Array.Empty<byte>();
                }
            }

            int statusCode = (int)resp.StatusCode;
            string statusName = resp.StatusCode.ToString();
            string headerDump = headers.ToString();
            _ = Task.Run(async () =>
            {
                try
                {
                    Directory.CreateDirectory(state.Dir);
                    string resPath = Path.Combine(state.Dir, state.Stem + "-res.txt");
                    if (skipReason != null)
                    {
                        await File.WriteAllTextAsync(
                            resPath,
                            $"{statusCode} {statusName}\nHEADERS: {headerDump}\nBODY: {skipReason}",
                            Encoding.UTF8).ConfigureAwait(false);
                        return;
                    }
                    if (copiedBytes != null && copiedBytes.Length > 0)
                    {
                        string binPath = Path.Combine(state.Dir, state.Stem + "-res.bin");
                        await File.WriteAllBytesAsync(binPath, copiedBytes).ConfigureAwait(false);
                    }
                    int len = copiedBytes?.Length ?? 0;
                    string text = $"{statusCode} {statusName}\nHEADERS: {headerDump}\nBODY: [{len} bytes]\n{Preview(copiedBytes)}";
                    await File.WriteAllTextAsync(resPath, text, Encoding.UTF8).ConfigureAwait(false);
                }
                catch { }
            });
        }
        catch { }

        return resp;
    }

    private static string Preview(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "<empty>";
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
        return "HEX: " + sb;
    }

    private static string ToHexOrText(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "<empty>";
        if ((bytes.Length > 2 && bytes[0] == (byte)'{') || (bytes.Length > 0 && bytes[0] == (byte)'['))
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
        public HttpCompletionOption CompletionOption;
    }
}
