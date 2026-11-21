using BazaarCore.Models;

namespace BazaarCore.Data;

/// <summary>
/// 本地化词库存储接口，提供按哈希键与卡牌模板查询官方中文文本的能力。
/// </summary>
public interface ILocalizationStore
{
    /// <summary>已加载的本地化词条总数。</summary>
    int EntryCount { get; }

    /// <summary>词库数据源路径。</summary>
    string? SourcePath { get; }

    /// <summary>按翻译哈希键尝试获取本地化文本。</summary>
    bool TryGetText(string? hashKey, out string text);

    /// <summary>判断卡牌模板是否具有官方本地化标题。</summary>
    bool HasOfficialTitle(CardTemplate card);

    /// <summary>获取卡牌模板的本地化标题。</summary>
    string GetLocalizedTitle(CardTemplate card);

    /// <summary>获取本地化文本对象的译文；若无译文则返回原始文本或空。</summary>
    string? GetLocalizedText(LocalizedText? localized);
}
