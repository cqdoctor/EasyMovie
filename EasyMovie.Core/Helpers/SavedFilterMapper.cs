using EasyMovie.Core.Models;

namespace EasyMovie.Core.Helpers;

/// <summary>
/// 「筛选值快照 ⇄ 已保存筛选方案」的映射（纯函数，便于单元测试）。
///
/// 背景：保存筛选方案（MovieListView 的 SaveFilter 按钮）历史上内联了**第二份**控件读取，
/// 与查询路径的权威实现 <c>CaptureFilterValues()</c> 在四个维度上漂移——
///   1) 关键词未归一化（用裸 Trim，空串也不置 null）；
///   2) 分类/状态/排序用临时 is int / is string 解析，未走 ParseIdTag/ParseStatusTag/ParseSortTag；
///   3) 上下界手写三元，未走 LowerBound/UpperBound；
///   4) 多选未剔除 "_all" 哨兵、空列表也不置 null。
/// 后果是「保存 → 重新载入」得到的筛选条件可能与实际生效的不一致。
///
/// 本类把该映射收口为唯一入口：一律复用 <see cref="MovieQueryBuilder"/> 的纯函数，
/// 与查询路径共用同一套语义，杜绝再次漂移。
/// </summary>
public static class SavedFilterMapper
{
    /// <summary>
    /// 从权威筛选值快照构造一个待持久化的筛选方案。
    /// </summary>
    /// <param name="name">方案名（由调用方保证非空）。</param>
    /// <param name="v">权威筛选值快照，应来自 <c>CaptureFilterValues()</c> 刷新后的 <c>MovieFilterState.FilterValues</c>。</param>
    public static SavedFilter FromFilterValues(string name, MovieFilterValues v)
    {
        return new SavedFilter
        {
            Name = name,
            Keyword = MovieQueryBuilder.NormalizeKeyword(v.Keyword),
            CategoryId = v.CategoryId,
            // 枚举名与下拉框 Tag 字符串一致（NotWatched / WantToWatch / Watched）
            Status = v.Status?.ToString(),
            YearFrom = v.YearFrom,
            YearTo = v.YearTo,
            RatingMin = v.RatingMin,
            RatingMax = v.RatingMax,
            Countries = MovieQueryBuilder.NormalizeMultiSelect(v.Countries),
            Languages = MovieQueryBuilder.NormalizeMultiSelect(v.Languages),
            RuntimeMin = v.RuntimeMin,
            RuntimeMax = v.RuntimeMax,
            Directors = MovieQueryBuilder.NormalizeMultiSelect(v.Directors),
            SortBy = v.SortBy,
            SortDesc = v.SortDesc,
        };
    }

    /// <summary>
    /// 清洗**历史保存**的筛选方案，使其符合当前语义（就地修改并返回同一实例，便于链式使用）。
    ///
    /// 老版本保存时未剔除 "_all" 哨兵、空列表也未置 null、关键词未归一化。
    /// 载入时统一清洗，避免老数据把 "_all" 当成一个真实的国家/语言/导演去筛选。
    /// 对新保存的数据是幂等的（重复调用结果不变）。
    /// </summary>
    public static SavedFilter NormalizeLegacy(SavedFilter f)
    {
        f.Keyword = MovieQueryBuilder.NormalizeKeyword(f.Keyword);
        f.Countries = MovieQueryBuilder.NormalizeMultiSelect(f.Countries);
        f.Languages = MovieQueryBuilder.NormalizeMultiSelect(f.Languages);
        f.Directors = MovieQueryBuilder.NormalizeMultiSelect(f.Directors);
        return f;
    }
}
