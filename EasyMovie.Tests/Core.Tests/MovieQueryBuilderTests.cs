using System.Collections.Generic;
using System.Linq;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Helpers;
using EasyMovie.Core.Models;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// MovieQueryBuilder 行为锁定测试。
///
/// 目的：把 MovieListView.xaml.cs（2400+ 行 code-behind，Client 层此前零测试覆盖）
/// 里的筛选/排序解析逻辑抽出时，用测试钉住现有行为，确保搬运过程中没有改坏。
/// 这些断言描述的是**抽取前的真实行为**，不是理想行为——其中若干条是已知缺陷
/// （已在注释中标明「已知行为」），将来若要修正语义，应显式修改对应测试并说明理由。
/// </summary>
public class MovieQueryBuilderTests
{
    // ───────────────────────── 关键词 ─────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void NormalizeKeyword_Blank_ReturnsNull(string? input)
        => Assert.Null(MovieQueryBuilder.NormalizeKeyword(input));

    [Theory]
    [InlineData("奥本海默", "奥本海默")]
    [InlineData("  奥本海默  ", "奥本海默")]
    [InlineData("  a b  ", "a b")]
    public void NormalizeKeyword_NonBlank_Trims(string input, string expected)
        => Assert.Equal(expected, MovieQueryBuilder.NormalizeKeyword(input));

    /// <summary>全角空格也应被视作空白（string.IsNullOrWhiteSpace 的 Unicode 语义）。</summary>
    [Fact]
    public void NormalizeKeyword_FullWidthSpace_ReturnsNull()
        => Assert.Null(MovieQueryBuilder.NormalizeKeyword("　"));

    // ───────────────────────── Tag → Id ─────────────────────────

    [Fact]
    public void ParseIdTag_Int_ReturnsValue()
        => Assert.Equal(42, MovieQueryBuilder.ParseIdTag(42));

    [Theory]
    [InlineData(null)]
    [InlineData("42")]      // 字符串形式的 Id 不被识别（原实现用 `is int`）
    [InlineData(42L)]
    public void ParseIdTag_NonInt_ReturnsNull(object? tag)
        => Assert.Null(MovieQueryBuilder.ParseIdTag(tag));

    // ───────────────────────── Tag → 观看状态 ─────────────────────────

    [Theory]
    [InlineData("NotWatched", WatchStatus.NotWatched)]
    [InlineData("WantToWatch", WatchStatus.WantToWatch)]
    [InlineData("Watched", WatchStatus.Watched)]
    public void ParseStatusTag_KnownValue_MapsToEnum(string tag, WatchStatus expected)
        => Assert.Equal(expected, MovieQueryBuilder.ParseStatusTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("notwatched")]   // 已知行为：大小写敏感，小写不识别
    [InlineData("Unknown")]
    [InlineData(0)]              // 已知行为：传枚举的整数值也不识别，只认字符串
    public void ParseStatusTag_Unknown_ReturnsNull(object? tag)
        => Assert.Null(MovieQueryBuilder.ParseStatusTag(tag));

    // ───────────────────────── 排序 ─────────────────────────

    [Theory]
    [InlineData("title_asc", "title", false)]
    [InlineData("title_desc", "title", true)]
    [InlineData("createdat_desc", "createdat", true)]
    [InlineData("year_asc", "year", false)]
    public void ParseSortTag_WellFormed_ParsesFieldAndDirection(string tag, string by, bool desc)
    {
        var (sortBy, sortDesc) = MovieQueryBuilder.ParseSortTag(tag);
        Assert.Equal(by, sortBy);
        Assert.Equal(desc, sortDesc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("title")]              // 只有一段 → 回落默认
    [InlineData("date_added_desc")]    // 已知行为：三段 → 字段含下划线也会被判无效
    [InlineData(123)]
    public void ParseSortTag_Malformed_FallsBackToDefault(object? tag)
    {
        var (sortBy, sortDesc) = MovieQueryBuilder.ParseSortTag(tag);
        Assert.Equal("createdat", sortBy);
        Assert.True(sortDesc);
    }

    /// <summary>已知行为：方向段大小写敏感，"DESC" 会按升序处理（不是抛错、也不是降序）。</summary>
    [Fact]
    public void ParseSortTag_UpperCaseDesc_TreatedAsAscending()
    {
        var (sortBy, sortDesc) = MovieQueryBuilder.ParseSortTag("title_DESC");
        Assert.Equal("title", sortBy);
        Assert.False(sortDesc);
    }

    [Fact]
    public void DefaultSort_IsCreatedAtDescending()
    {
        Assert.Equal("createdat", MovieQueryBuilder.DefaultSortBy);
        Assert.True(MovieQueryBuilder.DefaultSortDesc);
    }

    // ───────────────────────── 区间滑块边界 ─────────────────────────

    [Fact]
    public void LowerBound_AtMinimum_ReturnsNull()
        => Assert.Null(MovieQueryBuilder.LowerBound(1990, 1990));

    [Fact]
    public void LowerBound_AboveMinimum_ReturnsTruncatedInt()
        => Assert.Equal(2000, MovieQueryBuilder.LowerBound(2000.7, 1990));

    [Fact]
    public void UpperBound_AtMaximum_ReturnsNull()
        => Assert.Null(MovieQueryBuilder.UpperBound(2026, 2026));

    [Fact]
    public void UpperBound_BelowMaximum_ReturnsTruncatedInt()
        => Assert.Equal(2020, MovieQueryBuilder.UpperBound(2020.9, 2026));

    /// <summary>
    /// 已知缺陷（锁现状，不修）：滑块拖到最左/最右端等价于「不限制」，
    /// 因此用户无法表达「只看 1990 年起」这类以最小值为下界的筛选。
    /// </summary>
    [Fact]
    public void KnownIssue_CannotExpressBoundaryValueAsFilter()
    {
        Assert.Null(MovieQueryBuilder.LowerBound(1990, 1990));
        Assert.Null(MovieQueryBuilder.UpperBound(2026, 2026));
    }

    /// <summary>区间滑块默认值（未拖动时上下界都贴边）→ 两个维度都不参与筛选。</summary>
    [Fact]
    public void RangeSlider_DefaultPosition_ProducesNoBounds()
    {
        const double min = 1990, max = 2026;
        Assert.Null(MovieQueryBuilder.LowerBound(min, min));
        Assert.Null(MovieQueryBuilder.UpperBound(max, max));
    }

    // ───────────────────────── 多选 ─────────────────────────

    [Fact]
    public void NormalizeMultiSelect_Null_ReturnsNull()
        => Assert.Null(MovieQueryBuilder.NormalizeMultiSelect(null));

    [Fact]
    public void NormalizeMultiSelect_Empty_ReturnsNull()
        => Assert.Null(MovieQueryBuilder.NormalizeMultiSelect(new List<object?>()));

    [Fact]
    public void NormalizeMultiSelect_SelectedValues_ReturnsThem()
    {
        var result = MovieQueryBuilder.NormalizeMultiSelect(new object?[] { "中国", "美国" });
        Assert.NotNull(result);
        Assert.Equal(new[] { "中国", "美国" }, result!);
    }

    [Fact]
    public void NormalizeMultiSelect_OnlyAllSentinel_ReturnsNull()
        => Assert.Null(MovieQueryBuilder.NormalizeMultiSelect(new object?[] { "_all" }));

    /// <summary>混选「全部」与具体项时，「全部」被剔除，具体项保留（原实现行为）。</summary>
    [Fact]
    public void NormalizeMultiSelect_AllSentinelMixedWithValues_DropsSentinel()
    {
        var result = MovieQueryBuilder.NormalizeMultiSelect(new object?[] { "_all", "中国" });
        Assert.NotNull(result);
        Assert.Equal(new[] { "中国" }, result!);
    }

    /// <summary>非字符串 Tag（null / 数字）被忽略；保留原有相对顺序。</summary>
    [Fact]
    public void NormalizeMultiSelect_NonStringTags_IgnoredPreservingOrder()
    {
        var result = MovieQueryBuilder.NormalizeMultiSelect(new object?[] { null, "中国", 42, "美国" });
        Assert.NotNull(result);
        Assert.Equal(new[] { "中国", "美国" }, result!);
    }

    // ════════════════════════════════════════════════════════════════
    // B2 切片3（2026-09-03）：从 LoadMoviesAsync 组装实参处抽出的三个解析器。
    // 此前这段逻辑在 code-behind 里，零测试覆盖。
    // ════════════════════════════════════════════════════════════════

    /// <summary>「想看」快速筛选开启时，强制覆盖状态下拉框的值（即便下拉框选了「已看」）。</summary>
    [Fact]
    public void ResolveStatus_QuickFilterOn_ForcesWantToWatch()
        => Assert.Equal(WatchStatus.WantToWatch,
            MovieQueryBuilder.ResolveStatus(WatchStatus.Watched, true));

    /// <summary>「想看」快速筛选未开启时，原样透传状态下拉框的值（含 null＝不筛选）。</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(WatchStatus.NotWatched, WatchStatus.NotWatched)]
    [InlineData(WatchStatus.Watched, WatchStatus.Watched)]
    public void ResolveStatus_QuickFilterOff_PassesThrough(WatchStatus? input, WatchStatus? expected)
        => Assert.Equal(expected, MovieQueryBuilder.ResolveStatus(input, false));

    /// <summary>
    /// 已知缺陷锁定：只有 true / null 两态，无法表达「只看未收藏」。
    /// 若日后 View 侧新增「非收藏」快捷筛选，此处即是需改的地方。
    /// </summary>
    [Fact]
    public void ResolveFavorites_OnlyTwoStates_TrueOrNull()
    {
        Assert.True(MovieQueryBuilder.ResolveFavorites(true));
        Assert.Null(MovieQueryBuilder.ResolveFavorites(false));
    }

    /// <summary>高级筛选区间值优先。</summary>
    [Fact]
    public void ResolveYear_AdvancedSet_ReturnsAdvanced()
        => Assert.Equal(1999, MovieQueryBuilder.ResolveYear(1999, 2020));

    /// <summary>
    /// 区间滑块停在端点＝不限制（LowerBound/UpperBound 返回 null），此时回落到年份下拉框。
    /// 这是「只看 1990 年起」无法表达的同一处根因：滑块拖到最右端即等价于不限制。
    /// </summary>
    [Fact]
    public void ResolveYear_AdvancedNull_FallsBackToDropdown()
        => Assert.Equal(2020, MovieQueryBuilder.ResolveYear(null, 2020));

    /// <summary>两者皆无（滑块不限制 + 下拉框选「全部年份」）→ null，即不筛选年份。</summary>
    [Fact]
    public void ResolveYear_BothNull_ReturnsNull()
        => Assert.Null(MovieQueryBuilder.ResolveYear(null, null));
}

// ════════════════════════════════════════════════════════════════════
// MovieFilterValues：B2 切片3 的筛选值快照载体，只承载不含逻辑。
// 这里锁定「新建实例的默认值必须等于 MovieQueryBuilder 的排序默认值」——
// 否则 View 尚未调用 CaptureFilterValues 就发起查询会拿到错误的排序。
// ════════════════════════════════════════════════════════════════════
public class MovieFilterValuesTests
{
    [Fact]
    public void NewInstance_SortDefaults_MatchQueryBuilderDefaults()
    {
        var v = new MovieFilterValues();
        Assert.Equal(MovieQueryBuilder.DefaultSortBy, v.SortBy);
        Assert.Equal(MovieQueryBuilder.DefaultSortDesc, v.SortDesc);
    }

    /// <summary>所有筛选条件默认应为「不筛选」：null / false，不得有意外初值。</summary>
    [Fact]
    public void NewInstance_AllFiltersAreUnset()
    {
        var v = new MovieFilterValues();
        Assert.Null(v.Keyword);
        Assert.Null(v.CategoryId);
        Assert.Null(v.Status);
        Assert.Null(v.DropdownYear);
        Assert.Null(v.YearFrom);
        Assert.Null(v.YearTo);
        Assert.Null(v.RatingMin);
        Assert.Null(v.RatingMax);
        Assert.Null(v.RuntimeMin);
        Assert.Null(v.RuntimeMax);
        Assert.Null(v.Countries);
        Assert.Null(v.Languages);
        Assert.Null(v.Directors);
        Assert.False(v.QuickFilterFavorites);
        Assert.False(v.QuickFilterWatchlist);
    }

    /// <summary>MovieFilterState 必须自带一个非 null 的 FilterValues，否则 View 首次读取会 NRE。</summary>
    [Fact]
    public void MovieFilterState_FilterValues_IsNotNullByDefault()
        => Assert.NotNull(new MovieFilterState().FilterValues);
}
