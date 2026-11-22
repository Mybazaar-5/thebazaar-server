using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BazaarLauncher.Core;

/// <summary>
/// 客户端本地保存的常用服务器列表（存放在 BepInEx/config/bazaar_servers.json）。
/// </summary>
internal static class ServerProfileStore
{
    internal sealed class ServerProfile
    {
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("url")] public string Url { get; set; } = string.Empty;
    }

    private static readonly object Sync = new();
    private static List<ServerProfile> _cached;

    private static string StorePath
    {
        get
        {
            string dir = Path.Combine(BepInEx.Paths.ConfigPath ?? ".");
            return Path.Combine(dir, "bazaar_servers.json");
        }
    }

    internal static List<ServerProfile> LoadAll()
    {
        lock (Sync)
        {
            if (_cached == null)
            {
                _cached = ReadFromDisk();
            }

            // 始终保证当前配置地址和本机单机地址可选
            string curUrl = NormalizeUrl(BazaarLauncherPlugin.ServerBase?.Value);
            if (!string.IsNullOrEmpty(curUrl) && !_cached.Any(s => string.Equals(NormalizeUrl(s.Url), curUrl, StringComparison.OrdinalIgnoreCase)))
            {
                string label = curUrl.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "本机单机"
                    : "默认服务器";
                _cached.Insert(0, new ServerProfile { Name = label, Url = curUrl });
            }

            if (!_cached.Any(s => string.Equals(NormalizeUrl(s.Url), "http://127.0.0.1:8080", StringComparison.OrdinalIgnoreCase)))
            {
                _cached.Add(new ServerProfile { Name = "本机单机", Url = "http://127.0.0.1:8080" });
            }

            return _cached.Select(s => new ServerProfile { Name = s.Name, Url = s.Url }).ToList();
        }
    }

    internal static bool AddOrUpdate(string name, string rawUrl, out string normalizedUrl, out string error)
    {
        normalizedUrl = NormalizeUrl(rawUrl);
        error = string.Empty;
        if (string.IsNullOrEmpty(normalizedUrl)
            || !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "请输入有效的 http:// 或 https:// 服务器地址";
            return false;
        }

        string safeName = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim();
        string normTarget = normalizedUrl;
        lock (Sync)
        {
            var list = LoadAll();
            var existing = list.FirstOrDefault(s => string.Equals(NormalizeUrl(s.Url), normTarget, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Name = safeName;
                existing.Url = normTarget;
            }
            else
            {
                list.Add(new ServerProfile { Name = safeName, Url = normTarget });
            }
            _cached = list;
            SaveToDiskLocked(list);
        }
        return true;
    }

    internal static bool Remove(string url)
    {
        string target = NormalizeUrl(url);
        if (string.IsNullOrEmpty(target)) return false;
        lock (Sync)
        {
            var list = LoadAll();
            int removed = list.RemoveAll(s => string.Equals(NormalizeUrl(s.Url), target, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                _cached = list;
                SaveToDiskLocked(list);
                return true;
            }
            return false;
        }
    }

    internal static async Task<(bool Online, long LatencyMs, string Info)> PingServerAsync(string rawUrl, CancellationToken ct = default)
    {
        string url = NormalizeUrl(rawUrl);
        if (string.IsNullOrEmpty(url))
            return (false, -1, "地址为空");

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var req = new HttpRequestMessage(HttpMethod.Get, url + "/healthz");
            using var resp = await HttpClientHub.SharedClient.SendAsync(req, cts.Token).ConfigureAwait(false);
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                return (true, sw.ElapsedMilliseconds, $"{sw.ElapsedMilliseconds} ms");
            }
            return (false, sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, -1, ex is TaskCanceledException ? "连接超时" : "无法连接");
        }
    }

    internal static string NormalizeUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = raw.Trim().TrimEnd('/');
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            s = "http://" + s;
        }
        return s;
    }

    private static List<ServerProfile> ReadFromDisk()
    {
        try
        {
            if (!File.Exists(StorePath)) return new List<ServerProfile>();
            string json = File.ReadAllText(StorePath, Encoding.UTF8);
            var parsed = JsonConvert.DeserializeObject<List<ServerProfile>>(json);
            return parsed ?? new List<ServerProfile>();
        }
        catch
        {
            return new List<ServerProfile>();
        }
    }

    private static void SaveToDiskLocked(List<ServerProfile> list)
    {
        try
        {
            string dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string json = JsonConvert.SerializeObject(list, Formatting.Indented);
            File.WriteAllText(StorePath, json, new UTF8Encoding(false));
        }
        catch { }
    }
}
