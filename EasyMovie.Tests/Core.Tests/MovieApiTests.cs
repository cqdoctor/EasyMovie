using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Moq.Protected;
using EasyMovie.Core;
using EasyMovie.Core.Helpers;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using EasyMovie.Tools.MovieApi;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

public class MovieApiServiceTests
{
    [Fact]
    public void MapToMovie_ShouldMapAllFields()
    {
        var result = new MovieSearchResult
        {
            Title = "星际穿越",
            OriginalTitle = "Interstellar",
            Year = 2014,
            Director = "克里斯托弗·诺兰",
            Cast = "马修·麦康纳, 安妮·海瑟薇",
            Country = "美国",
            Synopsis = "探索宇宙与亲情",
            PosterUrl = "https://example.com/poster.jpg",
            Runtime = 169,
            Rating = 9.4,
            ExternalId = "1889243",
            Source = "douban"
        };

        var movie = MovieApiService.MapToMovie(result);

        movie.Title.Should().Be("星际穿越");
        movie.OriginalTitle.Should().Be("Interstellar");
        movie.Year.Should().Be(2014);
        movie.Director.Should().Be("克里斯托弗·诺兰");
        movie.Cast.Should().Be("马修·麦康纳, 安妮·海瑟薇");
        movie.Country.Should().Be("美国");
        movie.Synopsis.Should().Be("探索宇宙与亲情");
        movie.PosterUrl.Should().Be("https://example.com/poster.jpg");
        movie.Runtime.Should().Be(169);
        movie.DoubanId.Should().Be("1889243");
    }

    [Fact]
    public void MapToMovie_ShouldSetTmdbId_WhenSourceIsTmdb()
    {
        var result = new MovieSearchResult
        {
            Title = "Inception",
            Year = 2010,
            ExternalId = "27205",
            Source = "tmdb"
        };

        var movie = MovieApiService.MapToMovie(result);

        movie.TmdbId.Should().Be("27205");
        movie.DoubanId.Should().BeNull();
    }

    [Fact]
    public void MapToMovie_ShouldHandleNullFields()
    {
        var result = new MovieSearchResult
        {
            Title = "简单电影",
            Year = 2020
        };

        var movie = MovieApiService.MapToMovie(result);

        movie.Title.Should().Be("简单电影");
        movie.OriginalTitle.Should().BeNull();
        movie.Director.Should().BeNull();
        movie.Cast.Should().BeNull();
        movie.Synopsis.Should().BeNull();
    }
}

// 与 DoubanThrottlePolicyTests 同集合：冷却状态是 static 的，必须串行，
// 否则并行跑时互相重置计数会导致用例偶发失败。
[Collection("Douban")]
public class DoubanApiClientTests : IDisposable
{
    // 冷却状态是 static 的，会在用例之间互相污染（例如 500 错误会触发冷却，
    // 导致后续用例一进来就被 InCooldown 短路）。每个用例前后都重置，保证顺序无关。
    public DoubanApiClientTests() => DoubanApiClient.ResetThrottleState();

    public void Dispose() => DoubanApiClient.ResetThrottleState();

    private static HttpClient CreateMockHttpClient(string responseJson)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
                // 2026-11-06 起 rexxar 是搜索主路径、网页搜索仅兜底：
                // 这里让 rexxar 请求级失败（500），以便验证网页搜索兜底路径的解析结果。
                req.RequestUri != null && req.RequestUri.Host.Contains("m.douban.com")
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(responseJson)
                    });

        return new HttpClient(handler.Object);
    }

    /// <summary>
    /// 解析网页搜索 window.__DATA__ 响应（rexxar 请求级失败时的兜底路径）。
    /// 样本取自 2026-09-10 实测结构（movie.douban.com/subject_search）。
    ///
    /// 2026-11-06 起搜索主路径为 rexxar（m.douban.com/rexxar/api/v2/search），
    /// 网页搜索仅在其请求级失败时兜底。
    /// </summary>
    [Fact]
    public async Task SearchAsync_ShouldParseHtmlResults()
    {
        var html = "<html><body><script>window.__DATA__ = {\"items\":[{" +
            "\"id\":35267208,\"title\":\"流浪地球2 (2023)\"," +
            "\"abstract\":\"中国大陆 / 科幻 / 冒险 / The Wandering Earth II / 173分钟\"," +
            "\"abstract_2\":\"郭帆 / 吴京 / 刘德华 / 李雪健\"," +
            "\"cover_url\":\"https://img9.doubanio.com/view/photo/s_ratio_poster/public/p2916835424.jpg\"," +
            "\"rating\":{\"count\":1427722,\"value\":8.3}," +
            "\"url\":\"https://movie.douban.com/subject/35267208/\"}]};</script></body></html>";

        var client = new DoubanApiClient(CreateMockHttpClient(html));
        var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "流浪地球2" });

        response.TotalCount.Should().Be(1);
        response.Results.Should().HaveCount(1);
        response.Results[0].Title.Should().Be("流浪地球2");
        response.Results[0].Year.Should().Be(2023);
        response.Results[0].Director.Should().Be("郭帆");
        response.Results[0].Cast.Should().Contain("吴京");
        response.Results[0].Rating.Should().Be(8.3);
        response.Results[0].RatingCount.Should().Be(1427722);
        response.Results[0].ExternalId.Should().Be("35267208");
        response.Results[0].Source.Should().Be("douban");
    }

    /// <summary>
    /// 回归：PickBestMatch 不得因 OriginalTitle 为 null 而崩溃。
    /// 豆瓣 rexxar 搜索结果不含英文名（OriginalTitle 恒为 null），
    /// 早期 Normalize(string) 未判空，Regex.Replace(null) 抛 ArgumentNullException，
    /// 导致只要搜索结果非空就崩、补全服务整体失效。
    /// </summary>
    [Fact]
    public void PickBestMatch_ShouldNotThrow_WhenOriginalTitleIsNull()
    {
        var results = new List<MovieSearchResult>
        {
            new MovieSearchResult { Title = "星际穿越", OriginalTitle = null, Year = 2014, ExternalId = "1889243" }
        };

        var act = () => DoubanApiClient.PickBestMatch(results, "星际穿越", 2014);

        act.Should().NotThrow();
        act().Should().NotBeNull();
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_OnHttpError()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var client = new DoubanApiClient(new HttpClient(handler.Object));
        var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "test" });

        response.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDetailAsync_ShouldReturnNull_OnError()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException());

        var client = new DoubanApiClient(new HttpClient(handler.Object));
        var result = await client.GetDetailAsync("123");

        result.Should().BeNull();
    }

    [Fact]
    public void SourceName_ShouldBeDouban()
    {
        new DoubanApiClient().SourceName.Should().Be("douban");
    }

    /// <summary>
    /// 实证：cache.db 短标题脏数据的产生路径。
    /// 观测（2026-08-28）：cache.db 680 条中 177 条 Title 长度&lt;=3，样本为
    /// 「爱」「杀」「我」「B」「S」「K」「法」「冷」「暗」——均非合法片名。
    /// 假设：PickBestMatch 第 2 步 TitleContains 是双向包含，
    /// 当「搜索词包含结果标题」时会让短片名截胡长片名（如搜「杀死比尔」命中「杀」）。
    /// </summary>
    [Theory]
    [InlineData("杀死比尔", "杀")]
    [InlineData("杀人回忆", "杀")]
    [InlineData("爱情神话", "爱")]
    [InlineData("速度与激情", "速")]
    public void Diagnose_ShortTitleShouldNotSwallowLongTitle(string query, string shortTitle)
    {
        var results = new List<MovieSearchResult>
        {
            new MovieSearchResult { Title = shortTitle, Year = 2018, ExternalId = "short" },
            new MovieSearchResult { Title = query, Year = 2023, ExternalId = "correct" }
        };

        var match = DoubanApiClient.PickBestMatch(results, query, null);

        match.Should().NotBeNull();
        match!.ExternalId.Should().Be("correct",
            $"搜索「{query}」时应命中同名结果，不应被短片名「{shortTitle}」截胡");
    }

    /// <summary>
    /// 实证：结果里只有短片名时，应判为「无可靠匹配」返回 null（跳过），
    /// 而不是返回该短片名——后者会把无关影片的元数据写成脏缓存。
    /// </summary>
    [Theory]
    [InlineData("杀死比尔", "杀")]
    [InlineData("爱情神话", "爱")]
    public void Diagnose_OnlyShortTitleShouldNotMatch(string query, string shortTitle)
    {
        var results = new List<MovieSearchResult>
        {
            new MovieSearchResult { Title = shortTitle, Year = 2018, ExternalId = "short" }
        };

        var match = DoubanApiClient.PickBestMatch(results, query, null);

        match.Should().BeNull(
            $"结果仅有短片名「{shortTitle}」而搜索词是「{query}」时，应判为无可靠匹配，" +
            "否则会把无关影片写入缓存");
    }

    /// <summary>
    /// 回归（2026-09-11 修复，豆瓣补全最致命根因）：主库「脏标题」（带编码/音轨标签）必须能匹配上干净结果。
    /// 旧实现直接用脏标题做反向包含判定，长度比被标签撑到 0.5 以下 → 判「无可靠匹配」永久跳过：
    ///   「东北警察故事2 EAC3 Atmos」vs 结果「东北警察故事2」= 7/16≈0.44 &lt; 0.5。
    /// 症状与「限流 / 豆瓣无收录」完全一样，导致这批影片每一轮都被静默跳过。
    /// 修法：PickBestMatch 内部先 CleanSearchTitle 再匹配（0.5 守卫本身不变，短片名截胡仍被挡住）。
    /// </summary>
    [Theory]
    [InlineData("东北警察故事2 EAC3 Atmos", "东北警察故事2")]
    [InlineData("毒战2 EAC3 Atmos", "毒战2")]
    [InlineData("惊天魔盗团3 出神入化 EAC3 Atmos", "惊天魔盗团3")]
    [InlineData("捕风追影 极盗跟踪 EAC3 Atmos", "捕风追影")]
    [InlineData("夺宝奇兵5：命运转盘 EAC3 Atmos", "夺宝奇兵5：命运转盘")]
    public void PickBestMatch_DirtyTitleWithCodecTags_ShouldMatch(string dirtyTitle, string cleanResult)
    {
        var results = new List<MovieSearchResult>
        {
            new MovieSearchResult { Title = cleanResult, Year = 2023, ExternalId = "correct", Rating = 7.2 }
        };

        var match = DoubanApiClient.PickBestMatch(results, dirtyTitle, null);

        match.Should().NotBeNull($"脏标题「{dirtyTitle}」应能匹配干净结果「{cleanResult}」");
        match!.Title.Should().Be(cleanResult);
    }

    /// <summary>
    /// 反向回归：清洗后短片名仍不得截胡长片名（0.5 守卫必须继续生效，不能因清洗而放松）。
    /// </summary>
    [Fact]
    public void PickBestMatch_DirtyTitleStillRejectsUnrelatedShortTitle()
    {
        var results = new List<MovieSearchResult>
        {
            new MovieSearchResult { Title = "杀", Year = 2018, ExternalId = "short" }
        };

        DoubanApiClient.PickBestMatch(results, "杀死比尔 EAC3 Atmos", null).Should().BeNull();
    }
}

public class TmdbApiClientTests
{
    private static HttpClient CreateMockHttpClient(string responseBody)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseBody)
            });
        return new HttpClient(handler.Object);
    }

    [Fact]
    public void SourceName_ShouldBeTmdb()
    {
        new TmdbApiClient("test_key").SourceName.Should().Be("tmdb");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenApiKeyIsEmpty()
    {
        // 无 API Key 且网页抓取默认关闭：直接返回空结果（由上层回落豆瓣）
        var client = new TmdbApiClient("", CreateMockHttpClient("<html><body>no results</body></html>"));
        var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "test" });

        response.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDetailAsync_ShouldReturnNull_WhenApiKeyIsEmpty()
    {
        // 无 API Key 且网页抓取默认关闭：返回 null，由上层回落豆瓣
        var client = new TmdbApiClient("", CreateMockHttpClient("<html><body>no detail</body></html>"));
        var result = await client.GetDetailAsync("123");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_ShouldParseResults()
    {
        var json = @"{
            ""page"": 1,
            ""total_results"": 1,
            ""results"": [{
                ""id"": 157336,
                ""title"": ""星际穿越"",
                ""original_title"": ""Interstellar"",
                ""release_date"": ""2014-11-07"",
                ""overview"": ""探索宇宙与亲情"",
                ""poster_path"": ""/poster.jpg"",
                ""vote_average"": 8.4,
                ""vote_count"": 30000
            }]
        }";

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri!.ToString().Contains("search/movie")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json)
            });

        // 有 API Key 即走官方 API（直连由 ConnectViaIpPoolAsync 处理），不再要求配置代理
        var client = new TmdbApiClient("fake_key", new HttpClient(handler.Object));
        var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "Interstellar" });

        response.TotalCount.Should().Be(1);
        response.Results.Should().HaveCount(1);
        response.Results[0].Title.Should().Be("星际穿越");
        response.Results[0].Year.Should().Be(2014);
        response.Results[0].ExternalId.Should().Be("157336");
        response.Results[0].Source.Should().Be("tmdb");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenTransportFails()
    {
        // 模拟 IP 池全失败（建连/握手异常）：请求级失败应降级为空结果，不向调用方抛异常
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("TMDB 直连失败（IP 池 0 个）"));

        var client = new TmdbApiClient("fake_key", new HttpClient(handler.Object));
        var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "Interstellar" });

        response.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDetailAsync_ShouldReturnNull_WhenTransportFails()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("TMDB 直连失败"));

        var client = new TmdbApiClient("fake_key", new HttpClient(handler.Object));
        var result = await client.GetDetailAsync("157336");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_ShouldFallbackToDouban_WhenTmdbTransportFails()
    {
        // TMDB 请求级失败 → 空结果 → MovieApiService 回落豆瓣
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("TMDB 直连失败（IP 池全失败）"));

        var fallback = new Mock<IMovieApiClient>();
        fallback.SetupGet(c => c.SourceName).Returns("douban");
        fallback.Setup(c => c.SearchAsync(It.IsAny<MovieSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieSearchResponse
            {
                Results = new List<MovieSearchResult>
                {
                    new() { Title = "星际穿越", Year = 2014, ExternalId = "1889243", Source = "douban" }
                },
                TotalCount = 1
            });

        var service = new MovieApiService(new TmdbApiClient("fake_key", new HttpClient(handler.Object)), fallback.Object);
        var response = await service.SearchAsync("Interstellar");

        response.Results.Should().ContainSingle();
        response.Results[0].Source.Should().Be("douban");
    }

    [Fact]
    public async Task GetDetailAsync_ShouldMapOriginCountryIsoToChinese()
    {
        // origin_country 是 ISO-3166-1 alpha-2，需映射成与豆瓣一致的中文分类名
        var json = @"{
            ""id"": 603,
            ""title"": ""黑客帝国"",
            ""original_title"": ""The Matrix"",
            ""release_date"": ""1999-03-31"",
            ""origin_country"": [""US""],
            ""production_countries"": [{ ""iso_3166_1"": ""US"", ""name"": ""United States of America"" }]
        }";
        var client = new TmdbApiClient("fake_key", CreateMockHttpClient(json));

        var result = await client.GetDetailAsync("603");

        result.Should().NotBeNull();
        result!.Country.Should().Be("美国");
    }

    [Fact]
    public async Task GetDetailAsync_ShouldFallbackToProductionCountries_WhenOriginCountryMissing()
    {
        var json = @"{
            ""id"": 1,
            ""title"": ""某部法国片"",
            ""release_date"": ""2020-01-01"",
            ""production_countries"": [{ ""iso_3166_1"": ""FR"", ""name"": ""France"" }]
        }";
        var client = new TmdbApiClient("fake_key", CreateMockHttpClient(json));

        var result = await client.GetDetailAsync("1");

        result.Should().NotBeNull();
        result!.Country.Should().Be("France");
    }

    [Fact]
    public async Task GetAlternativeTitlesAsync_ShouldParseTitles()
    {
        var json = @"{
            ""id"": 1146302,
            ""titles"": [
                { ""iso_3166_1"": ""CN"", ""title"": ""史泰龙的传奇"", ""type"": ""alternative"" },
                { ""iso_3166_1"": ""US"", ""title"": ""Sly"", ""type"": ""alternative"" }
            ]
        }";
        var client = new TmdbApiClient("fake_key", CreateMockHttpClient(json));

        var titles = await client.GetAlternativeTitlesAsync("1146302");

        titles.Should().BeEquivalentTo(new[] { "史泰龙的传奇", "Sly" });
    }

    [Fact]
    public async Task GetAlternativeTitlesAsync_ShouldReturnEmpty_WhenApiKeyIsEmpty()
    {
        var client = new TmdbApiClient("", CreateMockHttpClient("{}"));

        var titles = await client.GetAlternativeTitlesAsync("1146302");

        titles.Should().BeEmpty();
    }
}

/// <summary>
/// 片名匹配守卫：归一化 + 相似度 + 年份窗口 + 包含关系证据核对。
/// 回归场景（2026-11-06 实测）：搜「烈火中」时首条是 1965《烈火中永生》，正解 id=946215 在第 2 条。
/// </summary>
public class MovieTitleMatcherTests
{
    private static MovieSearchResult C(string title, int year, string id) =>
        new() { Title = title, Year = year, ExternalId = id };

    [Fact]
    public void Pick_烈火中_跳过包含关系错配_采用精确匹配()
    {
        var results = new List<MovieSearchResult>
        {
            C("烈火中永生", 1965, "1965"),
            C("烈火中", 2024, "946215")
        };

        var picked = MovieTitleMatcher.Pick(results, "烈火中", 2024);

        picked.Should().NotBeNull();
        picked!.ExternalId.Should().Be("946215");
    }

    [Fact]
    public void Rank_包含关系且同年_标记为需要额外证据()
    {
        var results = new List<MovieSearchResult> { C("烈火中永生", 2024, "1965") };

        var ranked = MovieTitleMatcher.Rank(results, "烈火中", 2024);

        ranked.Should().ContainSingle();
        ranked[0].Exact.Should().BeFalse();
        ranked[0].NeedsEvidence.Should().BeTrue();
        // 没有第 2 顺位候选时，不允许把「包含关系 + 同年」直接当成命中
        MovieTitleMatcher.Pick(results, "烈火中", 2024).Should().BeNull();
    }

    [Fact]
    public void Rank_年份超出容差_直接丢弃()
    {
        var results = new List<MovieSearchResult> { C("烈火中", 1965, "1965") };

        MovieTitleMatcher.Rank(results, "烈火中", 2024).Should().BeEmpty();
    }

    [Fact]
    public void MetadataOverlap_片长接近_视为同一部作品()
    {
        MovieTitleMatcher.MetadataOverlap(169, null, null, 171, null, null).Should().BeTrue();
    }

    [Fact]
    public void MetadataOverlap_导演或主演有交集_视为同一部作品()
    {
        MovieTitleMatcher.MetadataOverlap(null, "克里斯托弗·诺兰", "墨菲", null, "克里斯托弗·诺兰", null).Should().BeTrue();
        MovieTitleMatcher.MetadataOverlap(null, "甲", "演员A / 演员B", 150, "乙", "演员B").Should().BeTrue();
    }

    [Fact]
    public void MetadataOverlap_全都不一致_返回false()
    {
        MovieTitleMatcher.MetadataOverlap(100, "甲", "演员A", 150, "乙", "演员B").Should().BeFalse();
        MovieTitleMatcher.MetadataOverlap(null, null, null, null, null, null).Should().BeFalse();
    }

    [Fact]
    public void Normalize_去除字幕与分辨率噪声()
    {
        MovieTitleMatcher.Normalize("烈火中.2024.中字.4K.BluRay").Should().Be("烈火中");
    }

    // ===== 2026-11-07：阈值 0.60、别名兜底、精确命中软校验 =====

    [Fact]
    public void Rank_阈值060_史泰龙译名差异仍命中()
    {
        // Id 19 实测：本地《史泰龙的传奇》vs TMDB《史泰龙：不只是传奇》，相似度 0.625
        var results = new List<MovieSearchResult> { C("史泰龙：不只是传奇", 2023, "1146302") };

        var ranked = MovieTitleMatcher.Rank(results, "史泰龙的传奇", 2023);

        ranked.Should().ContainSingle();
        ranked[0].Score.Should().BeGreaterThan(0.60);
        ranked[0].Result.ExternalId.Should().Be("1146302");
    }

    [Fact]
    public void Rank_别名精确命中_参与打分()
    {
        var results = new List<MovieSearchResult> { C("Sly", 2023, "1146302") };
        var aliases = new Dictionary<string, IReadOnlyList<string>>
        {
            ["1146302"] = new[] { "史泰龙的传奇", "En Sylvester Stallone Dokumentär" }
        };

        // 仅标题/原名时相似度为 0，被丢弃
        MovieTitleMatcher.Rank(results, "史泰龙的传奇", 2023).Should().BeEmpty();
        // 别名参与后精确命中
        var ranked = MovieTitleMatcher.Rank(results, "史泰龙的传奇", 2023, 10, aliases);
        ranked.Should().ContainSingle();
        ranked[0].Exact.Should().BeTrue();
    }

    [Fact]
    public void Rank_阈值060_烈火中1965错配仍需证据()
    {
        // 放宽阈值不得绕过「包含关系 + 同年」的佐证要求
        var results = new List<MovieSearchResult> { C("烈火中永生", 1965, "202603") };

        var ranked = MovieTitleMatcher.Rank(results, "烈火中", 1965);

        ranked.Should().ContainSingle();
        ranked[0].NeedsEvidence.Should().BeTrue();
        MovieTitleMatcher.Pick(results, "烈火中", 1965).Should().BeNull();
    }

    [Fact]
    public void ExactMismatchReason_Id76型同名不同片_降级()
    {
        // 本地泰国片《指尖》vs TMDB 法国片 481157：导演、原语言均不符；
        // 片长恰好都是 105（本地片长为历史脏数据）仍必须降级。
        var reason = MovieTitleMatcher.ExactMismatchReason(
            105, "纳卢拜·库诺", "泰语",
            105, "Ludovic Bernard", "fr");

        reason.Should().NotBeNull();
        reason.Should().Contain("软校验不通过");
    }

    [Fact]
    public void ExactMismatchReason_三项全部不符_降级()
    {
        var reason = MovieTitleMatcher.ExactMismatchReason(
            100, "甲", "日语",
            150, "乙", "fr");

        reason.Should().NotBeNull();
    }

    [Fact]
    public void ExactMismatchReason_语言相符_不降级()
    {
        // 译名/导演写法差异常见，语言一致时不应误伤
        MovieTitleMatcher.ExactMismatchReason(
            100, "甲", "韩语",
            120, "乙", "ko").Should().BeNull();
    }

    [Fact]
    public void ExactMismatchReason_导演相符_不降级()
    {
        MovieTitleMatcher.ExactMismatchReason(
            100, "克里斯托弗·诺兰", "英语",
            130, "克里斯托弗·诺兰", "fr").Should().BeNull();
    }

    [Fact]
    public void ExactMismatchReason_语言无法映射_不降级()
    {
        // 本地语言名不在映射表（如「汉语普通话」）时视为不可比较，保守放行
        MovieTitleMatcher.ExactMismatchReason(
            100, "甲", "汉语普通话",
            130, "乙", "fr").Should().BeNull();
    }

    [Fact]
    public void Rank_记录拒绝原因_供汇总日志()
    {
        var results = new List<MovieSearchResult>
        {
            C("烈火中", 1965, "1965"),          // 年份超容差
            C("完全无关的片名", 2024, "999")    // 相似度不足
        };
        var rejected = new List<MovieTitleMatcher.Rejected>();

        MovieTitleMatcher.Rank(results, "烈火中", 2024, 10, null, rejected);

        rejected.Should().HaveCount(2);
        rejected.Should().Contain(r => r.Reason == "年份超容差" && r.YearDiff > 1);
        rejected.Should().Contain(r => r.Reason == "相似度不足" && r.Score < MovieTitleMatcher.Threshold);
    }
}

public class MovieApiServiceFallbackTests
{
    private class MockClient : IMovieApiClient
    {
        private readonly MovieSearchResponse _response;
        public string SourceName { get; }

        public MockClient(string sourceName, MovieSearchResponse response)
        {
            SourceName = sourceName;
            _response = response;
            // 设置所有结果的 Source
            foreach (var r in _response.Results)
                r.Source = sourceName;
        }

        public Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default)
            => Task.FromResult(_response);

        public Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
            => Task.FromResult<MovieSearchResult?>(null);
    }

    [Fact]
    public async Task SearchAsync_ShouldUsePrimary_WhenHasResults()
    {
        var primary = new MockClient("douban", new MovieSearchResponse
        {
            Results = new() { new MovieSearchResult { Title = "豆瓣结果" } },
            TotalCount = 1
        });
        var fallback = new MockClient("tmdb", new MovieSearchResponse
        {
            Results = new() { new MovieSearchResult { Title = "TMDB结果" } },
            TotalCount = 1
        });

        var service = new MovieApiService(primary, fallback);
        var response = await service.SearchAsync("test");

        response.Results.Should().HaveCount(1);
        response.Results[0].Title.Should().Be("豆瓣结果");
    }

    [Fact]
    public async Task SearchAsync_ShouldFallback_WhenPrimaryEmpty()
    {
        var primary = new MockClient("douban", new MovieSearchResponse());
        var fallback = new MockClient("tmdb", new MovieSearchResponse
        {
            Results = new() { new MovieSearchResult { Title = "TMDB结果" } },
            TotalCount = 1
        });

        var service = new MovieApiService(primary, fallback);
        var response = await service.SearchAsync("test");

        response.Results.Should().HaveCount(1);
        response.Results[0].Title.Should().Be("TMDB结果");
        response.Results[0].Source.Should().Be("tmdb");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenKeywordIsEmpty()
    {
        var primary = new MockClient("douban", new MovieSearchResponse());
        var service = new MovieApiService(primary);

        var response = await service.SearchAsync("");

        response.Results.Should().BeEmpty();
    }

    // ═══════════════ 自动打标（genres → MovieTags）═══════════════
    // 这组测试锁死一个真实缺陷：MovieSearchResult 曾经没有 Genres 字段，
    // 豆瓣/TMDB 返回的类型在解析阶段就无处安放，导致库里 290 部影片 0 条标签关联，
    // 统计页「类型分布」、推荐算法标签权重、关系图谱、AI 摘要、去重完整度打分五处全部空转。

    [Fact]
    public async Task MapToMovieAsync_ShouldAttachTagsFromGenres()
    {
        using var ctx = new MovieDbContext(new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tagService = new TagService(new TagRepository(ctx));

        var result = new MovieSearchResult
        {
            Title = "星际穿越",
            Year = 2014,
            Genres = new List<string> { "科幻", "剧情" }
        };

        var movie = await MovieApiService.MapToMovieAsync(result, null, tagService);

        movie.MovieTags.Should().HaveCount(2);

        // 标签实体必须真的建出来了（不只是内存里的临时对象）
        var tags = ctx.Tags.ToList();
        tags.Should().HaveCount(2);
        tags.Should().Contain(t => t.Name == "科幻");
        tags.Should().Contain(t => t.Name == "剧情");
    }

    [Fact]
    public async Task MapToMovieAsync_ShouldReuseExistingTag_NotDuplicate()
    {
        using var ctx = new MovieDbContext(new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tagService = new TagService(new TagRepository(ctx));
        ctx.Tags.Add(new Tag { Name = "科幻" });
        await ctx.SaveChangesAsync();

        var movie = await MovieApiService.MapToMovieAsync(
            new MovieSearchResult { Title = "星际穿越", Genres = new List<string> { "科幻" } },
            null, tagService);

        movie.MovieTags.Should().HaveCount(1);
        ctx.Tags.Should().HaveCount(1);   // 没被重复插入
    }

    [Fact]
    public async Task MapToMovieAsync_ShouldDedupeRepeatedGenres()
    {
        using var ctx = new MovieDbContext(new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tagService = new TagService(new TagRepository(ctx));

        var movie = await MovieApiService.MapToMovieAsync(
            new MovieSearchResult { Title = "重复类型片", Genres = new List<string> { "动作", "动作", "动作" } },
            null, tagService);

        movie.MovieTags.Should().HaveCount(1);
    }

    [Fact]
    public async Task MapToMovieAsync_ShouldSkipInvalidGenreNames()
    {
        using var ctx = new MovieDbContext(new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tagService = new TagService(new TagRepository(ctx));

        // 空白 / 纯数字 / 超长 都应被跳过，且不阻断整部影片的映射
        var movie = await MovieApiService.MapToMovieAsync(
            new MovieSearchResult
            {
                Title = "含噪声类型",
                Genres = new List<string> { "  ", "123", new string('长', 60), "剧情" }
            },
            null, tagService);

        movie.Title.Should().Be("含噪声类型");
        movie.MovieTags.Should().HaveCount(1);
        ctx.Tags.Should().Contain(t => t.Name == "剧情");
    }

    [Fact]
    public async Task MapToMovieAsync_ShouldNotThrow_WhenTagServiceIsNull()
    {
        // 旧调用方（不传标签服务）必须保持原行为，不能因新参数崩掉
        var movie = await MovieApiService.MapToMovieAsync(
            new MovieSearchResult { Title = "无标签服务", Genres = new List<string> { "科幻" } },
            null, null);

        movie.Title.Should().Be("无标签服务");
        movie.MovieTags.Should().BeEmpty();
    }

    [Fact]
    public async Task MapToMovieAsync_ShouldNotThrow_WhenGenresIsNull()
    {
        using var ctx = new MovieDbContext(new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tagService = new TagService(new TagRepository(ctx));

        var movie = await MovieApiService.MapToMovieAsync(
            new MovieSearchResult { Title = "无类型字段", Genres = null },
            null, tagService);

        movie.MovieTags.Should().BeEmpty();
    }
}
