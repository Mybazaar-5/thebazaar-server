using BazaarCore.Models;
using Microsoft.Data.Sqlite;

namespace BazaarCore.Data;

/// <summary>
/// 官方简体中文翻译词库加载器：从 zh-CN.bytes 只读加载哈希词典并提供多级回退查词能力。
/// </summary>
public sealed class LocalizationLoader : ILocalizationStore
{
    private readonly Dictionary<string, string> _translations;

    /// <summary>空词库实例（当本地不存在 zh-CN.bytes 时使用）。</summary>
    public static LocalizationLoader Empty { get; } = new(null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <inheritdoc />
    public int EntryCount => _translations.Count;

    /// <inheritdoc />
    public string? SourcePath { get; }

    public LocalizationLoader(string? sourcePath, Dictionary<string, string> translations)
    {
        SourcePath = sourcePath;
        _translations = translations ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 只读加载官方简体中文翻译库 zh-CN.bytes；若目标文件不存在则返回空词库且不抛出异常。
    /// </summary>
    public static LocalizationLoader Load(string? customPath = null)
    {
        var resolvedPath = SteamPathResolver.ResolveZhCnTranslationPath(customPath);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            return Empty;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = resolvedPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash, text FROM translation";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            var hash = reader.GetString(0);
            var text = reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(hash) && !string.IsNullOrEmpty(text))
                dict[hash] = text;
        }

        return new LocalizationLoader(resolvedPath, dict);
    }

    /// <inheritdoc />
    public bool TryGetText(string? hashKey, out string text)
    {
        if (string.IsNullOrWhiteSpace(hashKey))
        {
            text = string.Empty;
            return false;
        }

        if (_translations.TryGetValue(hashKey, out var found) && !string.IsNullOrEmpty(found))
        {
            text = found;
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public bool HasOfficialTitle(CardTemplate card)
    {
        if (card == null) return false;
        if (TryGetText(card.Localization?.Title?.Key, out var titleText) && !string.IsNullOrWhiteSpace(titleText))
            return true;
        if (TryGetText(card.TranslationKey, out var fallbackText) && !string.IsNullOrWhiteSpace(fallbackText))
            return true;
        return false;
    }

    /// <inheritdoc />
    public string GetLocalizedTitle(CardTemplate card)
    {
        if (card == null) return string.Empty;
        if (TryGetText(card.Localization?.Title?.Key, out var titleText) && !string.IsNullOrWhiteSpace(titleText))
            return titleText;
        if (TryGetText(card.TranslationKey, out var fallbackText) && !string.IsNullOrWhiteSpace(fallbackText))
            return fallbackText;
        return card.DisplayTitle;
    }

    /// <inheritdoc />
    public string? GetLocalizedText(LocalizedText? localized)
    {
        if (localized == null) return null;
        if (TryGetText(localized.Key, out var translated) && !string.IsNullOrWhiteSpace(translated))
            return translated;
        return localized.Text;
    }
}
