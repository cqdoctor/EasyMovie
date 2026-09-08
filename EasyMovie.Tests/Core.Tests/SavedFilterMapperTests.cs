using EasyMovie.Core.Enums;
using EasyMovie.Core.Helpers;
using EasyMovie.Core.Models;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// SavedFilterMapper 契约测试（#3）。
///
/// 这些断言锁定的是「保存筛选方案」与「查询路径」必须共用同一套语义，
/// 防止再次出现第二份内联控件读取导致的漂移。
/// 此前 SaveFilter 内联副本的四个缺陷，逐一在此用例化：
///   多选含 "_all" 未剔除 / 空列表未置 null / 关键词未归一化 / 状态未走规范解析。
/// </summary>
public class SavedFilterMapperTests
{
    // ───────── 保存方向：FromFilterValues ─────────

    [Fact]
    public void FromFilterValues_ExcludesAllSentinelFromMultiSelect()
    {
        var v = new MovieFilterValues
        {
            Countries = new List<string> { "_all", "美国" },
            Languages = new List<string> { "英语", "_all" },
        };

        var f = SavedFilterMapper.FromFilterValues("方案A", v);

        Assert.Equal(new[] { "美国" }, f.Countries);
        Assert.Equal(new[] { "英语" }, f.Languages);
    }

    [Fact]
    public void FromFilterValues_EmptyOrAllOnlyMultiSelect_BecomesNull()
    {
        var v = new MovieFilterValues
        {
            Countries = new List<string>(),      // 空列表 → null（此前会存成 []）
            Languages = new List<string> { "_all" }, // 只选了「全部」→ null
            Directors = null,
        };

        var f = SavedFilterMapper.FromFilterValues("方案A", v);

        Assert.Null(f.Countries);
        Assert.Null(f.Languages);
        Assert.Null(f.Directors);
    }

    [Fact]
    public void FromFilterValues_NormalizesKeyword()
    {
        // 空白关键词应视为「不筛选」（null），而不是存一个空串
        Assert.Null(SavedFilterMapper.FromFilterValues("x", new MovieFilterValues { Keyword = "   " }).Keyword);
        Assert.Null(SavedFilterMapper.FromFilterValues("x", new MovieFilterValues { Keyword = null }).Keyword);
        Assert.Equal("盗梦", SavedFilterMapper.FromFilterValues("x", new MovieFilterValues { Keyword = "  盗梦  " }).Keyword);
    }

    [Fact]
    public void FromFilterValues_MapsStatusToTagString()
    {
        Assert.Equal("Watched",
            SavedFilterMapper.FromFilterValues("x", new MovieFilterValues { Status = WatchStatus.Watched }).Status);
        Assert.Equal("WantToWatch",
            SavedFilterMapper.FromFilterValues("x", new MovieFilterValues { Status = WatchStatus.WantToWatch }).Status);
        Assert.Equal("NotWatched",
            SavedFilterMapper.FromFilterValues("x", new MovieFilterValues { Status = WatchStatus.NotWatched }).Status);
        Assert.Null(SavedFilterMapper.FromFilterValues("x", new MovieFilterValues { Status = null }).Status);
    }

    [Fact]
    public void FromFilterValues_CopiesBoundsAndSortVerbatim()
    {
        var v = new MovieFilterValues
        {
            CategoryId = 7,
            YearFrom = 2001, YearTo = 2024,
            RatingMin = 6, RatingMax = 10,
            RuntimeMin = 90, RuntimeMax = 180,
            SortBy = "rating", SortDesc = true,
        };

        var f = SavedFilterMapper.FromFilterValues("方案A", v);

        Assert.Equal(7, f.CategoryId);
        Assert.Equal(2001, f.YearFrom);
        Assert.Equal(2024, f.YearTo);
        Assert.Equal(6, f.RatingMin);
        Assert.Equal(10, f.RatingMax);
        Assert.Equal(90, f.RuntimeMin);
        Assert.Equal(180, f.RuntimeMax);
        Assert.Equal("rating", f.SortBy);
        Assert.True(f.SortDesc);
        Assert.Equal("方案A", f.Name);
    }

    // ───────── 载入方向：NormalizeLegacy（老数据兼容） ─────────

    [Fact]
    public void NormalizeLegacy_StripsAllSentinelAndNullsEmpty()
    {
        var legacy = new SavedFilter
        {
            Name = "老方案",
            Keyword = "   ",                                  // 老版本可能存了空白
            Countries = new List<string> { "_all", "美国" },   // 老版本未剔除哨兵
            Languages = new List<string>(),                    // 老版本空列表未置 null
            Directors = new List<string> { "_all" },
        };

        var f = SavedFilterMapper.NormalizeLegacy(legacy);

        Assert.Null(f.Keyword);
        Assert.Equal(new[] { "美国" }, f.Countries);
        Assert.Null(f.Languages);
        Assert.Null(f.Directors);
    }

    [Fact]
    public void NormalizeLegacy_IsIdempotent()
    {
        var f = new SavedFilter { Countries = new List<string> { "_all", "美国" } };

        var once = SavedFilterMapper.NormalizeLegacy(f);
        var twice = SavedFilterMapper.NormalizeLegacy(once);

        Assert.Equal(once.Countries, twice.Countries);
        Assert.Equal(new[] { "美国" }, twice.Countries);
    }

    /// <summary>
    /// 核心不变量：保存路径产出的数据，再经载入路径归一化后必须**完全不变**。
    /// 若此用例失败，说明保存与载入两套语义又漂移了。
    /// </summary>
    [Fact]
    public void SaveThenLoadNormalize_RoundTripsWithoutSemanticChange()
    {
        var v = new MovieFilterValues
        {
            Keyword = "  盗梦  ",
            Status = WatchStatus.Watched,
            Countries = new List<string> { "_all", "美国" },
            Languages = new List<string>(),
            Directors = new List<string> { "诺兰" },
            YearFrom = 2010,
        };

        var saved = SavedFilterMapper.FromFilterValues("方案A", v);

        var expectedCountries = saved.Countries;
        var expectedLanguages = saved.Languages;
        var expectedDirectors = saved.Directors;
        var expectedKeyword = saved.Keyword;

        var loaded = SavedFilterMapper.NormalizeLegacy(saved);

        Assert.Equal(expectedKeyword, loaded.Keyword);
        Assert.Equal(expectedCountries, loaded.Countries);
        Assert.Equal(expectedLanguages, loaded.Languages);
        Assert.Equal(expectedDirectors, loaded.Directors);
        Assert.Equal("盗梦", loaded.Keyword);
        Assert.Equal(new[] { "美国" }, loaded.Countries);
    }
}
