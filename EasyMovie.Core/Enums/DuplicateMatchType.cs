namespace EasyMovie.Core.Enums;

/// <summary>
/// 重复匹配类型
/// </summary>
public enum DuplicateMatchType
{
    /// <summary>精确匹配（标题+年份完全相同）</summary>
    Exact = 0,

    /// <summary>模糊匹配（标题相似度≥85% + 年份相同）</summary>
    Fuzzy = 1,

    /// <summary>外部ID匹配（DoubanId或TmdbId相同）</summary>
    ExternalId = 2
}
