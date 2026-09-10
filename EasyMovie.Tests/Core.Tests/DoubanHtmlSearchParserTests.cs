using System.Linq;
using EasyMovie.Core.Helpers;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// DoubanHtmlSearchParser 契约测试（#10 豆瓣限流修复）。
///
/// 夹具取自 2026-09-10 实测抓取的真实 window.__DATA__ 片段（流浪地球2 / 沙丘2），
/// 另加两条合成边界样本。锁定的契约：
///   1) rexxar 403 need_login 时的兜底路径能产出与 rexxar 等价（且更全）的候选；
///   2) 任何畸形输入都不抛异常，只返回空/部分结果（解析失败绝不能拖垮补全主链路）；
///   3) 标题里的 U+200E 与尾随年份必须剥离，否则和主库片名对不上；
///   4) 0 分（未上映/无人评分）不能当成有效评分写进缓存。
/// </summary>
public class DoubanHtmlSearchParserTests
{
    // U+200E 等格式字符在 C# 源码里写成 "\u200e" 字面量不可靠（词法阶段会被吞成空串），
    // 因此统一用码点构造，保证夹具里确实存在这些不可见字符。
    private const char Lrm = (char)0x200E;   // LEFT-TO-RIGHT MARK
    private const char Zwsp = (char)0x200B;  // ZERO WIDTH SPACE

    // 真实片段（2026-09-10 抓取），标题中的 LRM 用插值注入，避免源码里不可见字符被编辑器吞掉
    private static readonly string RealFixture = $$"""
        <!DOCTYPE html><html><head><title>搜索</title></head><body>
        <div id="content"></div>
        <script>
          window.__DATA__ = {
            "count": 10,
            "items": [
              {
                "abstract": "中国大陆 / 科幻 / 冒险 / 灾难 / 流浪地球2(3D版) / The Wandering Earth Ⅱ / 173分钟",
                "abstract_2": "郭帆 / 吴京 / 刘德华 / 李雪健 / 沙溢 / 宁理 / 王智 / 朱颜曼滋 / 安地",
                "cover_url": "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p2916835424.jpg",
                "id": 35267208,
                "rating": { "count": 1427722, "rating_info": "", "star_count": 4, "value": 8.3 },
                "title": "流浪地球2{{Lrm}} (2023)",
                "tpl_name": "search_subject",
                "url": "https://movie.douban.com/subject/35267208/"
              },
              {
                "abstract": "美国 / 科幻 / 冒险 / Dune: Part Two / 166分钟",
                "abstract_2": "丹尼斯·维伦纽瓦 / 提莫西·查拉梅 / 赞达亚",
                "cover_url": "https://img1.doubanio.com/view/photo/s_ratio_poster/public/p2905327559.jpg",
                "id": 35928503,
                "rating": { "count": 380000, "rating_info": "", "star_count": 4, "value": 8.1 },
                "title": "沙丘2 Dune: Part Two{{Lrm}} (2024)",
                "tpl_name": "search_subject",
                "url": "https://movie.douban.com/subject/35928503/"
              }
            ],
            "start": 0,
            "total": 2
          };
        </script>
        </body></html>
        """;

    [Fact]
    public void Parse_RealFixture_ExtractsAllFields()
    {
        var list = DoubanHtmlSearchParser.Parse(RealFixture);

        Assert.Equal(2, list.Count);
        var first = list[0];
        Assert.Equal("流浪地球2", first.Title);
        Assert.Equal(2023, first.Year);
        Assert.Equal("35267208", first.ExternalId);
        Assert.Equal("douban", first.Source);
        Assert.Equal(8.3, first.Rating!.Value, 1);
        Assert.Equal(1427722, first.RatingCount);
        Assert.Equal("郭帆", first.Director);
        Assert.Equal("吴京, 刘德华, 李雪健, 沙溢, 宁理, 王智, 朱颜曼滋, 安地", first.Cast);
        Assert.Equal("中国大陆", first.Country);
        Assert.Equal("The Wandering Earth Ⅱ", first.OriginalTitle);
        Assert.Equal(173, first.Runtime);
        Assert.Contains("l_ratio_poster", first.PosterUrl!);
    }

    [Fact]
    public void Parse_RealFixture_SecondItem_SplitsTitleAndYear()
    {
        var second = DoubanHtmlSearchParser.Parse(RealFixture)[1];

        Assert.Equal("沙丘2 Dune: Part Two", second.Title);
        Assert.Equal(2024, second.Year);
        Assert.Equal("35928503", second.ExternalId);
        Assert.Equal("丹尼斯·维伦纽瓦", second.Director);
        Assert.Equal("提莫西·查拉梅, 赞达亚", second.Cast);
        Assert.Equal(166, second.Runtime);
    }

    [Fact]
    public void Parse_SkipsItemsWithoutId()
    {
        const string html = """
            <script>window.__DATA__ = {"items":[
              {"title":"无ID条目 (2020)","abstract":"中国 / 剧情"},
              {"id":123,"title":"有ID条目 (2021)","abstract":"中国 / 剧情"}
            ]};</script>
            """;
        var list = DoubanHtmlSearchParser.Parse(html);

        Assert.Single(list);
        Assert.Equal("有ID条目", list[0].Title);
        Assert.Equal("123", list[0].ExternalId);
    }

    [Fact]
    public void Parse_FallsBackToIdFromUrl()
    {
        const string html = """
            <script>window.__DATA__ = {"items":[
              {"title":"从URL取ID (2019)","url":"https://movie.douban.com/subject/30166972/","abstract":"日本 / 动画 / 110分钟"}
            ]};</script>
            """;
        var list = DoubanHtmlSearchParser.Parse(html);

        Assert.Single(list);
        Assert.Equal("30166972", list[0].ExternalId);
        Assert.Equal("日本", list[0].Country);
        Assert.Equal(110, list[0].Runtime);
    }

    [Fact]
    public void Parse_ZeroRating_TreatedAsNoRating()
    {
        const string html = """
            <script>window.__DATA__ = {"items":[
              {"id":999,"title":"未上映新片 (2026)","abstract":"中国大陆 / 剧情 / 120分钟",
               "rating":{"count":0,"star_count":0,"value":0}}
            ]};</script>
            """;
        var list = DoubanHtmlSearchParser.Parse(html);

        Assert.Single(list);
        Assert.Null(list[0].Rating);   // 0 分不能当成有效评分，否则会污染统计页的外部评分
        Assert.Equal(0, list[0].RatingCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>没有数据</body></html>")]
    [InlineData("<script>window.__DATA__ = </script>")]
    [InlineData("<script>window.__DATA__ = { 这不是 JSON ;</script>")]
    [InlineData("<script>window.__DATA__ = {\"items\": \"不是数组\"};</script>")]
    public void Parse_MalformedInput_ReturnsEmptyInsteadOfThrowing(string html)
    {
        var list = DoubanHtmlSearchParser.Parse(html);
        Assert.Empty(list);
    }

    [Fact]
    public void Parse_TruncatedJson_DoesNotThrow()
    {
        const string html = "<script>window.__DATA__ = {\"items\":[{\"id\":1,\"title\":\"截断 (2020)\"";
        Assert.Empty(DoubanHtmlSearchParser.Parse(html));
    }

    [Fact]
    public void Parse_StripsInvisibleMarksFromTitle()
    {
        var html = "<script>window.__DATA__ = {\"items\":[{\"id\":1,\"title\":\"隐形" +
                   Lrm + " (2023)\",\"abstract\":\"中国 / 剧情\"}]};</script>";
        var list = DoubanHtmlSearchParser.Parse(html);

        Assert.Single(list);
        Assert.Equal("隐形", list[0].Title);
        Assert.DoesNotContain(Lrm, list[0].Title);
        Assert.Equal(2023, list[0].Year);
    }

    [Theory]
    [InlineData("流浪地球2", 2023, "流浪地球2")]
    [InlineData("沙丘2 Dune: Part Two", 2024, "沙丘2 Dune: Part Two")]
    [InlineData("无年份片名", 0, "无年份片名")]
    [InlineData("全角括号", 2019, "全角括号")]
    public void CleanTitle_StripsYearAndBidiMarks(string baseTitle, int year, string expectedTitle)
    {
        var raw = year > 0 ? $"{baseTitle}{Lrm} ({year})" : baseTitle;
        var rawFullWidth = year > 0 ? $"{baseTitle}（{year}）" : baseTitle;

        var title = DoubanHtmlSearchParser.CleanTitle(raw, out var parsedYear);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(year, parsedYear);

        var title2 = DoubanHtmlSearchParser.CleanTitle(rawFullWidth, out var parsedYear2);
        Assert.Equal(expectedTitle, title2);
        Assert.Equal(year, parsedYear2);

        Assert.DoesNotContain(Lrm, title);   // char 重载，避免空串歧义
        Assert.DoesNotContain(Lrm, title2);
    }

    [Fact]
    public void CleanTitle_RemovesZeroWidthCharacters()
    {
        var title = DoubanHtmlSearchParser.CleanTitle($"零宽{Zwsp}字符 (2022)", out var year);
        Assert.Equal("零宽字符", title);
        Assert.Equal(2022, year);
        Assert.DoesNotContain(Zwsp, title);
    }

    [Fact]
    public void CleanTitle_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DoubanHtmlSearchParser.CleanTitle("", out var y));
        Assert.Equal(0, y);
    }

    [Fact]
    public void ExtractJson_HandlesBracesInsideStrings()
    {
        // 字符串里的花括号不能被当成对象边界
        const string html = "<script>window.__DATA__ = {\"a\":\"x{y}z\",\"items\":[]};</script>";
        Assert.Equal("{\"a\":\"x{y}z\",\"items\":[]}", DoubanHtmlSearchParser.ExtractJson(html));
    }

    [Fact]
    public void ExtractJson_ReturnsNullWhenNoMarker()
    {
        Assert.Null(DoubanHtmlSearchParser.ExtractJson("<html><body>nothing</body></html>"));
    }
}
