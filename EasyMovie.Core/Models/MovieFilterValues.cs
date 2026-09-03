using System.Collections.Generic;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Helpers;

namespace EasyMovie.Core.Models;

/// <summary>
/// 电影库列表「筛选值」快照。于 2026-09-03 从 MovieListView 的私有
/// <c>AdvancedFilterValues</c> record 与 <c>GetFilterValues/GetYearFilter/GetSortInfo</c>
/// 的局部变量合并而成，是 B2 切片3 的载体，由 <see cref="MovieFilterState.FilterValues"/> 持有。
///
/// 所有字段的解析语义见 <see cref="EasyMovie.Core.Helpers.MovieQueryBuilder"/>（纯函数，已单测），
/// 本类只做承载，不含逻辑。
/// </summary>
public class MovieFilterValues
{
    /// <summary>搜索关键词；空白/null 表示不筛选。</summary>
    public string? Keyword { get; set; }
    /// <summary>分类 Id；null 表示不筛选。</summary>
    public int? CategoryId { get; set; }
    /// <summary>观看状态（未经快速筛选覆盖的原始值）。</summary>
    public WatchStatus? Status { get; set; }

    /// <summary>年份下拉框选中的年份。作为区间滑块未设置时的回落值（见 <see cref="MovieQueryBuilder.ResolveYear"/>）。</summary>
    public int? DropdownYear { get; set; }

    // ───────── 高级筛选（区间滑块 + 多选） ─────────
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public int? RatingMin { get; set; }
    public int? RatingMax { get; set; }
    public int? RuntimeMin { get; set; }
    public int? RuntimeMax { get; set; }
    public List<string>? Countries { get; set; }
    public List<string>? Languages { get; set; }
    public List<string>? Directors { get; set; }

    // ───────── 排序 ─────────
    public string SortBy { get; set; } = MovieQueryBuilder.DefaultSortBy;
    public bool SortDesc { get; set; } = MovieQueryBuilder.DefaultSortDesc;

    // ───────── 快速筛选 ─────────
    public bool QuickFilterFavorites { get; set; }
    public bool QuickFilterWatchlist { get; set; }
}
