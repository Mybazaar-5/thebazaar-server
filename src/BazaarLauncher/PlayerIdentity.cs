using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;

namespace BazaarLauncher;

/// <summary>
/// 玩家身份：不再依赖 Steam，改为「名字即账号」。
///
/// 优先级：
///   1. BepInEx 配置里的 PlayerName
///   2. 游戏目录下 player-name.txt（便于分发包预置）
///   3. 系统用户名
///
/// 伪票据 = SHA256("bazaar-local:" + name) 的十六进制，长度与真实 Steam 票据同量级，
/// 服务端拿它当稳定指纹 → 同一个名字永远进同一个账号。
/// </summary>
internal static class PlayerIdentity
{
    private static string _cached;

    internal static string ResolveName()
    {
        if (_cached != null) return _cached;

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

        _cached = name;
        BazaarLauncherPlugin.Log?.LogInfo("Player identity: " + name);
        return name;
    }

    internal static string FakeSteamTicket(string name)
    {
        byte[] hash;
        using (var sha = SHA256.Create())
        {
            hash = sha.ComputeHash(Encoding.UTF8.GetBytes("bazaar-local:" + name));
        }
        var sb = new StringBuilder(160);
        sb.Append("14000000");           // 与真实 WebAPI 票据同前缀，长度接近
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        foreach (byte b in hash)         // 拉长到 ~144 hex 字符，贴近真实票据
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    internal static ulong StableSteamId()
    {
        byte[] hash;
        using (var sha = SHA256.Create())
        {
            hash = sha.ComputeHash(Encoding.UTF8.GetBytes("steamid:" + ResolveName()));
        }
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | hash[i];
        // 落在合法 SteamID64 区间
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

