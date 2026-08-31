using System.Linq;
using EasyMovie.Core.Helpers;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// MovieCreditCleaner 行为测试。
///
/// 与前面几轮「抽取」不同，本次**不是行为不变的搬运**，而是一次有意的语义收敛：
/// 原来 4 份 CleanDirector / 5 份 DirectorBlacklistTerms 已经漂移，同一个导演字符串
/// 来自不同数据源会得到不同结果。本测试既锁定收敛后的统一语义，也用专门的
/// 「回归防护」测试把两个已修复的真实缺陷钉死：
///   1. TMDB 返回 "N/A" 曾被当成合法导演名写库（原本只有 OMDb 那份拦截）；
///   2. 中文顿号分隔的 "张三、李四" 曾被整体存成一个假人名（原本只有百度百科那份认顿号）。
/// </summary>
public class MovieCreditCleanerTests
{
    // ───────────────────── 空值 / HTML ─────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CleanDirector_Blank_ReturnsInput(string? input)
        => Assert.Equal(input, MovieCreditCleaner.CleanDirector(input));

    [Fact]
    public void CleanDirector_StripsHtmlTags()
        => Assert.Equal("加里·道伯曼", MovieCreditCleaner.CleanDirector("<a href='/x'>加里·道伯曼</a>"));

    [Fact]
    public void CleanDirector_StripsSelfClosingTag()
        => Assert.Equal("张三", MovieCreditCleaner.CleanDirector("张三<br/>"));

    // ───────────────── 回归防护 1：「N/A」 ─────────────────

    /// <summary>
    /// OMDb 用 "N/A" 表示缺失。原本只有 OMDb 副本拦截它，TMDB / 百度百科 / MovieListView
    /// 三份副本会把它当成合法人名（长度 3，无黑名单命中）写进数据库。
    /// </summary>
    [Fact]
    public void Regression_NA_IsNotTreatedAsDirectorName()
        => Assert.Equal("", MovieCreditCleaner.CleanDirector("N/A"));

    [Fact]
    public void Regression_NA_AmongRealNames_IsDropped()
        => Assert.Equal("张三", MovieCreditCleaner.CleanDirector("N/A, 张三"));

    // ─────────────── 回归防护 2：中文顿号分隔 ───────────────

    /// <summary>
    /// 原本只有百度百科副本的分割符里含 '、'，其余 3 份会把 "张三、李四"
    /// 整体当成一个合法人名（长度 5）存库，产生一个不存在的导演。
    /// </summary>
    [Fact]
    public void Regression_ChineseEnumerationComma_SplitsNames()
        => Assert.Equal("张三 / 李四", MovieCreditCleaner.CleanDirector("张三、李四"));

    [Fact]
    public void Regression_MixedSeparators_SplitsNames()
        => Assert.Equal("张三 / 李四 / 王五", MovieCreditCleaner.CleanDirector("张三、李四/王五"));

    // ───────────────────── 分隔符 ─────────────────────

    [Theory]
    [InlineData("张三/李四", "张三 / 李四")]
    [InlineData("张三\\李四", "张三 / 李四")]
    [InlineData("张三|李四", "张三 / 李四")]
    [InlineData("张三,李四", "张三 / 李四")]
    [InlineData("张三\n李四", "张三 / 李四")]
    public void CleanDirector_AllSeparators_Split(string input, string expected)
        => Assert.Equal(expected, MovieCreditCleaner.CleanDirector(input));

    [Fact]
    public void CleanDirector_SurroundingSpaces_Trimmed()
        => Assert.Equal("张三 / 李四", MovieCreditCleaner.CleanDirector("  张三 / 李四  "));

    // ───────────────────── 职业标签过滤 ─────────────────────

    [Theory]
    [InlineData("screenplay")]
    [InlineData("story")]
    [InlineData("writer")]
    [InlineData("producer")]
    [InlineData("executive producer")]   // OMDb 原词表缺此项
    [InlineData("based on")]             // OMDb 原词表缺此项
    [InlineData("visual effects")]       // OMDb 原词表缺此项
    [InlineData("sound")]                // OMDb 原词表缺此项
    [InlineData("director of photography")] // OMDb 原词表缺此项
    [InlineData("编剧")]
    [InlineData("原著")]                  // OMDb 原词表缺此项
    [InlineData("角色")]                  // TMDB 原词表缺此项
    [InlineData("制片人")]
    [InlineData("摄影")]
    [InlineData("剪辑")]
    [InlineData("艺术指导")]
    [InlineData("服装设计")]
    public void CleanDirector_BlacklistedTerms_Dropped(string term)
        => Assert.Equal("", MovieCreditCleaner.CleanDirector(term));

    /// <summary>黑名单词表取 5 份副本的并集，共 26 项——防止将来删词时静默缩水。</summary>
    [Fact]
    public void Blacklist_IsUnionOfAllFormerCopies_26Terms()
        => Assert.Equal(26, MovieCreditCleaner.DirectorBlacklistTerms.Count);

    [Fact]
    public void Blacklist_MatchingIsCaseInsensitive()
        => Assert.Equal("", MovieCreditCleaner.CleanDirector("EXECUTIVE PRODUCER"));

    /// <summary>
    /// 黑名单只污染它所在的那一"段"，同字段里的其他人名照常保留——
    /// "张三 (executive producer), 李四" 应只丢掉张三（制片人），留下李四（导演）。
    /// </summary>
    [Fact]
    public void Blacklist_TermEmbeddedInPart_DropsOnlyThatPart()
        => Assert.Equal("李四", MovieCreditCleaner.CleanDirector("张三 (executive producer), 李四"));

    // ───────────────────── 日期 / 年份 ─────────────────────

    [Theory]
    [InlineData("1963-02-17")]
    [InlineData("1963")]
    public void CleanDirector_DateLikeValues_Dropped(string input)
        => Assert.Equal("", MovieCreditCleaner.CleanDirector(input));

    [Fact]
    public void CleanDirector_MixedWithDate_KeepsOnlyNames()
        => Assert.Equal("张三", MovieCreditCleaner.CleanDirector("张三, 1963-02-17"));

    // ───────────────────── 长度与数量 ─────────────────────

    [Fact]
    public void CleanDirector_TooShort_Dropped()
        => Assert.Equal("", MovieCreditCleaner.CleanDirector("A"));

    [Fact]
    public void CleanDirector_TooLong_Dropped()
        => Assert.Equal("", MovieCreditCleaner.CleanDirector(new string('张', 31)));

    [Fact]
    public void CleanDirector_BoundaryLengths_Kept()
    {
        Assert.Equal("ab", MovieCreditCleaner.CleanDirector("ab"));
        Assert.Equal(new string('张', 30), MovieCreditCleaner.CleanDirector(new string('张', 30)));
    }

    [Fact]
    public void CleanDirector_MoreThanThree_KeepsFirstThree()
        => Assert.Equal("张三 / 李四 / 王五", MovieCreditCleaner.CleanDirector("张三, 李四, 王五, 赵六, 钱七"));

    // ───────────────────── 兜底前缀截取 ─────────────────────

    [Fact]
    public void CleanDirector_AllPartsBlacklisted_ExtractsPrefixBeforeTerm()
        => Assert.Equal("张三", MovieCreditCleaner.CleanDirector("张三 编剧"));

    /// <summary>黑名单词出现在位置 0（字符串就以职业名开头）时不截取——原实现行为。</summary>
    [Fact]
    public void CleanDirector_BlackTermAtStart_NoPrefixExtracted()
        => Assert.Equal("", MovieCreditCleaner.CleanDirector("编剧张三"));

    [Fact]
    public void CleanDirector_FallbackPrefixStartingWithYear_Rejected()
        => Assert.Equal("", MovieCreditCleaner.CleanDirector("1963 编剧"));

    // ───────────────────── IsPlausibleDirector ─────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("1963-02-17", false)]
    [InlineData("1963", false)]
    [InlineData("A", false)]                 // 长度 < 2
    [InlineData("编剧", false)]
    [InlineData("张三", true)]
    [InlineData("Christopher Nolan", true)]
    public void IsPlausibleDirector_MatchesMovieInfoFetcherSemantics(string? input, bool expected)
        => Assert.Equal(expected, MovieCreditCleaner.IsPlausibleDirector(input));

    /// <summary>
    /// 校验口径比清洗宽松：上限 60（MovieInfoFetcher 原值），而非清洗用的 30。
    /// 这个差异是原实现就有的，合并时刻意保留——改动它会改变补全的"是否需要继续搜索"判定。
    /// </summary>
    [Fact]
    public void IsPlausibleDirector_AllowsLongerNamesThanCleaning()
    {
        var name45 = new string('张', 45);
        Assert.True(MovieCreditCleaner.IsPlausibleDirector(name45));   // 校验通过
        Assert.Equal("", MovieCreditCleaner.CleanDirector(name45));    // 清洗丢弃
    }

    [Fact]
    public void IsPlausibleDirector_IsConsistentWithBlacklist()
        => Assert.All(
            MovieCreditCleaner.DirectorBlacklistTerms.ToList(),
            term => Assert.False(MovieCreditCleaner.IsPlausibleDirector(term)));

    // ───────────────────── InvalidPersonLabels ─────────────────────

    /// <summary>
    /// 原 2 份副本（MovieApiService / DbHelper）各有 9 项，缺「编剧」等职位标签——
    /// 实测库中已产生 1 条导演字段值为「编剧」的脏数据（#207 幽灵 Phantom AC3）。
    /// 本表补齐后应覆盖原 9 项 + 10 个职位标签。
    /// </summary>
    [Theory]
    [InlineData("人员")]
    [InlineData("人物")]
    [InlineData("演员")]
    [InlineData("主演")]
    [InlineData("导演")]
    [InlineData("暂无")]
    [InlineData("未知")]
    [InlineData("暂未录入")]
    [InlineData("更多")]
    [InlineData("编剧")]      // 原来缺失，正是 #207 的成因
    [InlineData("原著")]
    [InlineData("角色")]
    [InlineData("制片人")]
    [InlineData("摄影")]
    [InlineData("剪辑")]
    [InlineData("艺术指导")]
    [InlineData("服装设计")]
    public void InvalidPersonLabels_CoversOriginalTermsAndJobTitles(string label)
        => Assert.Contains(label, MovieCreditCleaner.InvalidPersonLabels);

    [Fact]
    public void InvalidPersonLabels_Has19Terms()
        => Assert.Equal(19, MovieCreditCleaner.InvalidPersonLabels.Count);

    /// <summary>
    /// 这是**整串精确匹配**表，与 DirectorBlacklistTerms 的**子串包含匹配**不同：
    /// "编剧" 是标签（整串命中），但 "张三 编剧" 不是——后者应由 CleanDirector 处理。
    /// </summary>
    [Fact]
    public void InvalidPersonLabels_IsWholeStringMatch_NotSubstring()
    {
        Assert.Contains("编剧", MovieCreditCleaner.InvalidPersonLabels);
        Assert.DoesNotContain("张三 编剧", MovieCreditCleaner.InvalidPersonLabels);
    }
}
