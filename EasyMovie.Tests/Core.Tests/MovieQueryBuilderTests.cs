using System.Collections.Generic;
using System.Linq;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Helpers;
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
}
