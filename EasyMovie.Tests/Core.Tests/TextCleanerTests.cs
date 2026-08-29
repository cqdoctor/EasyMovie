using EasyMovie.Core.Helpers;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// TextCleaner 行为锁定测试。
///
/// 目的：把 MovieListView.xaml.cs（2500+ 行 code-behind，此前零测试覆盖）里
/// 的 HTML 清洗逻辑抽出时，用测试钉住现有行为，确保搬运过程中没有改坏。
/// 这些断言描述的是**抽取前的真实行为**，不是理想行为——若将来要改变语义，
/// 应显式修改本测试并说明理由。
/// </summary>
public class TextCleanerTests
{
    [Fact]
    public void CleanHtmlFragment_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", TextCleaner.CleanHtmlFragment(null));
        Assert.Equal("", TextCleaner.CleanHtmlFragment(""));
        Assert.Equal("", TextCleaner.CleanHtmlFragment("   "));
    }

    /// <summary>文档中的真实脏数据样例：抓取时把 HTML 属性残留一起写进了数据库。</summary>
    [Fact]
    public void CleanHtmlFragment_AttributeResidueBeforeChineseName_ExtractsName()
    {
        var input = "1338249-gary-dauberman'>加里·道伯曼<";
        Assert.Equal("加里·道伯曼", TextCleaner.CleanHtmlFragment(input));
    }

    [Fact]
    public void CleanHtmlFragment_CompleteHtmlTags_RemovesThem()
    {
        Assert.Equal("张三", TextCleaner.CleanHtmlFragment("<a>张三</a>"));
        Assert.Equal("美国", TextCleaner.CleanHtmlFragment("<span class=\"x\">美国</span>"));
    }

    [Fact]
    public void CleanHtmlFragment_UnclosedTag_RemovesIt()
    {
        Assert.Equal("克里斯托弗·诺兰", TextCleaner.CleanHtmlFragment("克里斯托弗·诺兰<"));
    }

    /// <summary>纯英文/数字/符号串会被判定为 HTML 属性或 URL 残留而丢弃。</summary>
    [Theory]
    [InlineData("gary-dauberman")]
    [InlineData("1338249")]
    [InlineData("movie_123")]
    public void CleanHtmlFragment_LooksLikeAttributeOrUrl_ReturnsEmpty(string input)
    {
        Assert.Equal("", TextCleaner.CleanHtmlFragment(input));
    }

    /// <summary>
    /// 已知行为（不理想，但此处锁定现状以免抽取过程改坏）：
    /// 含冒号的 URL 不会被过滤，原样保留。
    /// 原因：LooksLikeAttribute 正则的字符类是 [\d\-a-zA-Z_=./&amp;?]，不含冒号，
    /// 所以 "http://x.com/a?b=1" 整体不匹配该模式。
    /// 若日后要修正（把 ':' 也纳入过滤），应显式更新本测试并说明理由。
    /// </summary>
    [Fact]
    public void CleanHtmlFragment_UrlWithColon_IsPreserved_KnownBehavior()
    {
        Assert.Equal("http://x.com/a?b=1", TextCleaner.CleanHtmlFragment("http://x.com/a?b=1"));
    }

    [Fact]
    public void CleanHtmlFragment_PlainChineseName_Preserved()
    {
        Assert.Equal("加里·道伯曼", TextCleaner.CleanHtmlFragment("加里·道伯曼"));
        Assert.Equal("吴京", TextCleaner.CleanHtmlFragment("吴京"));
    }

    [Fact]
    public void CleanHtmlFragment_StrayQuotesAndBrackets_Removed()
    {
        Assert.Equal("美国", TextCleaner.CleanHtmlFragment("\"美国'"));
        Assert.Equal("中国", TextCleaner.CleanHtmlFragment("中国>"));
    }

    /// <summary>首尾的空格、逗号、斜杠、连字符、等号会被裁剪（这些都是抓取残留的常见形式）。</summary>
    [Theory]
    [InlineData("  美国  ", "美国")]
    [InlineData("-美国/", "美国")]
    [InlineData(",美国,", "美国")]
    [InlineData("=美国=", "美国")]
    public void CleanHtmlFragment_TrimsLeadingAndTrailingJunk(string input, string expected)
    {
        Assert.Equal(expected, TextCleaner.CleanHtmlFragment(input));
    }

    [Fact]
    public void CleanHtmlFragment_OnlyTags_ReturnsEmpty()
    {
        Assert.Equal("", TextCleaner.CleanHtmlFragment("<br/>"));
        Assert.Equal("", TextCleaner.CleanHtmlFragment("<div></div>"));
    }

    /// <summary>真实场景：国家字段按分隔符拆开后逐段清洗。</summary>
    [Fact]
    public void CleanHtmlFragment_SplitCountryField_EachSegmentCleaned()
    {
        var raw = "美国 / 中国大陆";
        var cleaned = raw.Split('/', ' ', '·', ',')
            .Select(c => TextCleaner.CleanHtmlFragment(c.Trim()))
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();
        Assert.Equal(new[] { "美国", "中国大陆" }, cleaned);
    }
}
