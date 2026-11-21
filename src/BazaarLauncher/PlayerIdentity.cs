using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;

namespace BazaarLauncher;

/// <summary>
/// 玩家身份不可变快照管理（修复 P2-1 静态无锁读写竞态）：
/// 优先级：
///   1. BepInEx 配置里的 PlayerName
///   2. 游戏目录下 player-name.txt（便于分发包预置）
///   3. 系统用户名
/// </summary>
internal static class PlayerIdentity
{
    private sealed class IdentitySnapshot
    {
        public string Name { get; }
        public string FakeTicket { get; }
        public ulong SteamId64 { get; }

        public IdentitySnapshot(string name)
        {
            Name = name;
            FakeTicket = ComputeFakeSteamTicket(name);
            SteamId64 = ComputeStableSteamId(name);
        }
    }

    private static readonly object _sync = new();
    private static volatile IdentitySnapshot _snapshot;

    internal static string ResolveName() => EnsureSnapshot().Name;

    internal static string FakeSteamTicket(string name)
    {
        var snap = EnsureSnapshot();
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, snap.Name, StringComparison.Ordinal))
        {
            return snap.FakeTicket;
        }
        return ComputeFakeSteamTicket(name);
    }

    internal static ulong StableSteamId() => EnsureSnapshot().SteamId64;

    /// <summary>配置更新玩家名后安全刷新不可变快照。</summary>
    internal static void InvalidateSnapshot()
    {
        lock (_sync)
        {
            _snapshot = null;
        }
    }

    private static IdentitySnapshot EnsureSnapshot()
    {
        var existing = _snapshot;
        if (existing != null) return existing;

        lock (_sync)
        {
            if (_snapshot != null) return _snapshot;

            string name = null;
            try { name = BazaarLauncherPlugin.PlayerName?.Value; } catch { }

            if (string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    string file = Path.Combine(Paths.GameRootPath, "player-name.txt");
                    if (File.Exists(file))
                    {
                        name = File.ReadAllText(file, Encoding.UTF8).Trim();
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = Environment.UserName;
            }

            name = Sanitize(name);
            if (string.IsNullOrWhiteSpace(name)) name = "Player";

            var created = new IdentitySnapshot(name);
            _snapshot = created;
            BazaarLauncherPlugin.Log?.LogInfo("玩家身份已锁定: " + name);
            return created;
        }
    }

    private static string ComputeFakeSteamTicket(string name)
    {
        byte[] hash;
        using (var sha = SHA256.Create())
        {
            hash = sha.ComputeHash(Encoding.UTF8.GetBytes("bazaar-local:" + name));
        }
        var sb = new StringBuilder(160);
        sb.Append("14000000");
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private static ulong ComputeStableSteamId(string name)
    {
        byte[] hash;
        using (var sha = SHA256.Create())
        {
            hash = sha.ComputeHash(Encoding.UTF8.GetBytes("steamid:" + name));
        }
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | hash[i];
        return 76561197960265728UL + (v % 1000000000UL);
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c > 127) sb.Append(c);
        }
        string r = sb.ToString();
        if (r.Length > 32) r = r.Substring(0, 32);
        return r;
    }
}
