using EasyMovie.Core.Helpers;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// SearchKeywordPurifier 契约测试（#10 第二根因：搜索关键词太脏）。
///
/// 背景：主库 2020+ 影片 188/243（77%）是「中文译名 + 英文原名」混合形态，
/// 整串丢给豆瓣会返回 0 条候选。提纯后实测 6/6 有候选（原 4/6）。
///
/// 锁定的契约：
///   1) 中英混合 → 只剥拉丁词串，保留汉字与罗马数字（这是与 ExtractChineseKeyword 的关键差异）；
///   2) 纯中文 / 纯英文 → 不动（避免改变本来就能查到的行为）；
///   3) 过短（&lt;2 字符）或剥离后无汉字 → 不采用（单字片名极易误匹配，历史踩过坑）；
///   4) 剥离后与原词相同 → 返回 false，不制造无意义的重复请求。
/// </summary>
public class SearchKeywordPurifierTests
{
    [Theory]
    // 真实片名（来自主库 2020+）
    [InlineData("困兽Death Stranding EAC3", "困兽")]
    [InlineData("三大队 Endless Journey EAC3", "三大队")]
    [InlineData("刀尖 Seven Killings", "刀尖")]
    [InlineData("地师传人Tomb Making Notes EAC3", "地师传人")]
    [InlineData("千鹤先生Mr Qianhe EAC3", "千鹤先生")]
    [InlineData("反贪风暴之加密危机Crypto Storm EAC3", "反贪风暴之加密危机")]
    [InlineData("冰雪大围捕Snowstorm", "冰雪大围捕")]
    [InlineData("人生路不熟Godspeed", "人生路不熟")]
    [InlineData("沙丘2 Dune: Part Two", "沙丘2")]
    public void TryExtractChineseCore_StripsLatinWordsOnly(string keyword, string expected)
    {
        Assert.True(SearchKeywordPurifier.TryExtractChineseCore(keyword, out var core));
        Assert.Equal(expected, core);
    }

    [Fact]
    public void TryExtractChineseCore_PreservesRomanNumerals()
    {
        // ExtractChineseKeyword 会把「Ⅱ」丢掉得到「一狱劫数难逃」，与豆瓣真实片名对不上；
        // 本实现只剥拉丁词串，罗马数字必须保留。
        Assert.True(SearchKeywordPurifier.TryExtractChineseCore("一狱Ⅱ劫数难逃 Imprisoned Ⅱ There is No Escape from Fate", out var core));
        Assert.Equal("一狱Ⅱ劫数难逃", core);
    }

    [Fact]
    public void TryExtractChineseCore_StripsTrailingPunctuation()
    {
        Assert.True(SearchKeywordPurifier.TryExtractChineseCore("坚如磐石 - Under the Light", out var core));
        Assert.Equal("坚如磐石", core);
    }

    [Theory]
    [InlineData("流浪地球2")]          // 纯中文
    [InlineData("周处除三害")]          // 纯中文
    [InlineData("Oppenheimer")]       // 纯英文
    [InlineData("Dune: Part Two")]    // 纯英文
    [InlineData("2023")]              // 纯数字
    public void TryExtractChineseCore_PureLanguage_NotChanged(string keyword)
    {
        Assert.False(SearchKeywordPurifier.TryExtractChineseCore(keyword, out _));
    }

    [Fact]
    public void TryExtractChineseCore_TooShort_Rejected()
    {
        // 单字片名（「爱」「杀」「我」）历史上造成过大量误匹配，宁可不提纯
        Assert.False(SearchKeywordPurifier.TryExtractChineseCore("爱 Love", out _));
    }

    [Fact]
    public void TryExtractChineseCore_NoHanAfterStrip_Rejected()
    {
        Assert.False(SearchKeywordPurifier.TryExtractChineseCore("AB CD", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryExtractChineseCore_EmptyInput_Rejected(string? keyword)
    {
        Assert.False(SearchKeywordPurifier.TryExtractChineseCore(keyword, out _));
    }

    [Fact]
    public void TryExtractChineseCore_ResultDiffersFromInput()
    {
        // 剥离后若与原词相同，不应触发重试（无意义的重复请求）
        Assert.False(SearchKeywordPurifier.TryExtractChineseCore("无变化", out _));
    }
}
