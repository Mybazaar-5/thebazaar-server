using System.Text;

namespace BazaarHub.Services;

/// <summary>
/// 无损 INI / BepInEx CFG 文档读写器。
/// 完整保留原始注释（# / ## / ;）、空行、未知段落、未知键值对以及原始行尾风格（CRLF/LF）。
/// </summary>
public sealed class IniDocument
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly List<string> _lines;
    private readonly string _lineEnding;
    private readonly bool _trailingNewLine;

    private IniDocument(List<string> lines, string lineEnding, bool trailingNewLine)
    {
        _lines = lines;
        _lineEnding = lineEnding;
        _trailingNewLine = trailingNewLine;
    }

    /// <summary>
    /// 从磁盘加载 INI/CFG 文档；若文件不存在则返回空白文档。
    /// </summary>
    public static IniDocument LoadOrEmpty(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new IniDocument(new List<string>(), "\r\n", true);
        }

        string raw = File.ReadAllText(filePath, Encoding.UTF8);
        return Parse(raw);
    }

    /// <summary>
    /// 从字符串解析无损文档。
    /// </summary>
    public static IniDocument Parse(string raw)
    {
        string lineEnding = raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool trailingNewLine = raw.EndsWith('\n');

        var lines = new List<string>();
        if (raw.Length > 0)
        {
            string normalized = raw.Replace("\r\n", "\n");
            string[] split = normalized.Split('\n');
            int count = trailingNewLine ? split.Length - 1 : split.Length;
            for (int i = 0; i < count; i++)
            {
                lines.Add(split[i]);
            }
        }

        return new IniDocument(lines, lineEnding, trailingNewLine);
    }

    /// <summary>
    /// 读取指定段落与键名的值（大小写不敏感）。
    /// </summary>
    public string? GetValue(string section, string key)
    {
        string currentSection = string.Empty;
        foreach (string line in _lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            if (TryParseSectionHeader(trimmed, out string header))
            {
                currentSection = header;
                continue;
            }

            if (!string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryParseKeyValue(line, out string parsedKey, out string parsedValue) &&
                string.Equals(parsedKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return parsedValue;
            }
        }

        return null;
    }

    public bool GetBool(string section, string key, bool defaultValue)
    {
        string? raw = GetValue(section, key);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        return bool.TryParse(raw.Trim(), out bool parsed) ? parsed : defaultValue;
    }

    public int GetInt(string section, string key, int defaultValue)
    {
        string? raw = GetValue(section, key);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        return int.TryParse(raw.Trim(), out int parsed) ? parsed : defaultValue;
    }

    /// <summary>
    /// 无损设置指定段落的键值；若键已存在则原地替换值，若不存在则在段落内追加，若段落不存在则在文件末尾追加。
    /// </summary>
    public void SetValue(string section, string key, string value, IEnumerable<string>? commentsIfInserted = null)
    {
        string currentSection = string.Empty;
        int sectionHeaderIndex = -1;
        int sectionEndExclusive = _lines.Count;

        for (int i = 0; i < _lines.Count; i++)
        {
            string line = _lines[i];
            string trimmed = line.Trim();

            if (TryParseSectionHeader(trimmed, out string header))
            {
                if (sectionHeaderIndex >= 0)
                {
                    sectionEndExclusive = i;
                    break;
                }

                currentSection = header;
                if (string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase))
                {
                    sectionHeaderIndex = i;
                }
                continue;
            }

            if (sectionHeaderIndex < 0)
            {
                continue;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            if (TryParseKeyValue(line, out string parsedKey, out _) &&
                string.Equals(parsedKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = FormatKeyValue(key, value);
                return;
            }
        }

        var linesToInsert = new List<string>();
        if (commentsIfInserted != null)
        {
            foreach (string comment in commentsIfInserted)
            {
                linesToInsert.Add(comment);
            }
        }
        linesToInsert.Add(FormatKeyValue(key, value));

        if (sectionHeaderIndex >= 0)
        {
            // 插入在段落末尾的非空行之后，保留段落之间的空行
            int insertPos = sectionEndExclusive;
            while (insertPos > sectionHeaderIndex + 1 && string.IsNullOrWhiteSpace(_lines[insertPos - 1]))
            {
                insertPos--;
            }

            if (insertPos > sectionHeaderIndex + 1 && commentsIfInserted != null)
            {
                linesToInsert.Insert(0, string.Empty);
            }

            _lines.InsertRange(insertPos, linesToInsert);
            return;
        }

        // 段落不存在，追加到文档末尾
        if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[^1]))
        {
            _lines.Add(string.Empty);
        }

        _lines.Add($"[{section}]");
        _lines.Add(string.Empty);
        _lines.AddRange(linesToInsert);
    }

    /// <summary>
    /// 序列化回完整文本字符串。
    /// </summary>
    public string Serialize()
    {
        if (_lines.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (i > 0) sb.Append(_lineEnding);
            sb.Append(_lines[i]);
        }

        if (_trailingNewLine)
        {
            sb.Append(_lineEnding);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 原子保存为无 BOM UTF-8 文件（D7-P2-05：经 AtomicFileWriter 唯一随机临时文件防碰撞替换）。
    /// </summary>
    public void SaveAtomic(string filePath)
    {
        AtomicFileWriter.WriteAllTextAtomic(filePath, Serialize(), Utf8NoBom);
    }

    private static string FormatKeyValue(string key, string value)
    {
        return string.IsNullOrEmpty(value) ? $"{key} = " : $"{key} = {value}";
    }

    private static bool TryParseSectionHeader(string trimmed, out string sectionName)
    {
        if (trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            sectionName = trimmed[1..^1].Trim();
            return sectionName.Length > 0;
        }

        sectionName = string.Empty;
        return false;
    }

    private static bool TryParseKeyValue(string line, out string key, out string value)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = line[..eq].Trim();
        value = line[(eq + 1)..].Trim();
        return key.Length > 0 && !key.StartsWith('#') && !key.StartsWith(';');
    }
}
