using System.Linq;

namespace EasyMovie.Core.Helpers;

/// <summary>
/// 分类名合法性校验。
///
/// 背景：早期从网页抓取「类型/分类」时，会把页面的计数文本（如「12345人收藏」）
/// 或纯数字串当成分类名写进数据库，产生大量垃圾分类。本类负责把这些剔除。
///
/// 本实现于 2026-08-29 从 MovieListView.xaml.cs 的私有方法 IsValidCategoryName
/// 及其 JunkCategoryNames 集合原样抽取（行为逐字节一致），以便纳入单元测试保护。
/// 抽取时未改动任何逻辑；行为由 Tests/Core.Tests/CategoryNameValidatorTests.cs 锁定。
/// </summary>
public static class CategoryNameValidator
{
    /// <summary>
    /// 抓取页常见的计数型噪声后缀。包含这些片段的分类名一律视为无效。
    /// 注意：用 ordinal 比较（原实现即 StringComparer.Ordinal）。
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> JunkCategoryNames =
        new(System.StringComparer.Ordinal)
        {
            "人收藏", "人评论", "人看", "人想看", "人看过", "人评价", "人关注", "人推荐"
        };

    /// <summary>
    /// 判断分类名是否为合法分类（可用于建分类、自动归类）。
    /// </summary>
    /// <returns>非空、非纯数字、且不含计数噪声后缀时为 true。</returns>
    public static bool IsValidCategoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (int.TryParse(name, out _)) return false;
        if (JunkCategoryNames.Any(j => name.Contains(j))) return false;
        return true;
    }
}
