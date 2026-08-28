using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Moq.Protected;
using EasyMovie.Core;
using EasyMovie.Core.Interfaces;
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

public class DoubanApiClientTests
{
    private static HttpClient CreateMockHttpClient(string responseJson)
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
                Content = new StringContent(responseJson)
            });

        return new HttpClient(handler.Object);
    }

    /// <summary>
    /// 解析 rexxar 移动端搜索响应。
    /// 样本取自 2026-08-28 抓到的真实响应结构（m.douban.com/rexxar/api/v2/search），
    /// 字段类型与线上一致：id/year 均为字符串，rating 为对象，导演/主演来自 card_subtitle 第 3/4 段。
    ///
    /// 注意：不要改回网页版 window.__DATA__ 格式——DoubanApiClient 早已全面切到 rexxar，
    /// 网页版解析路径已废弃（仅 MovieNewsService 抓新闻页时仍用 __DATA__，与本客户端无关）。
    /// 用错格式会让本测试永远挂在「解析返回 0 条」上，掩盖真实回归。
    /// </summary>
    [Fact]
    public async Task SearchAsync_ShouldParseRexxarResults()
    {
        var json = @"{""subjects"":{""items"":[{""target"":{
            ""title"":""星际穿越"",
            ""id"":""1889243"",
            ""year"":""2014"",
            ""rating"":{""value"":9.4,""count"":2220848,""max"":10},
            ""card_subtitle"":""美国 英国 加拿大 / 剧情 科幻 冒险 / 克里斯托弗·诺兰 / 马修·麦康纳 安妮·海瑟薇"",
            ""cover_url"":""https://img.douban.com/poster.jpg""}}]}}";

        var client = new DoubanApiClient(CreateMockHttpClient(json));
        var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "星际穿越" });

        response.TotalCount.Should().Be(1);
        response.Results.Should().HaveCount(1);
        response.Results[0].Title.Should().Be("星际穿越");
        response.Results[0].Year.Should().Be(2014);
        response.Results[0].Director.Should().Be("克里斯托弗·诺兰");
        response.Results[0].Cast.Should().Contain("马修·麦康纳");
        response.Results[0].Rating.Should().Be(9.4);
        response.Results[0].RatingCount.Should().Be(2220848);
        response.Results[0].ExternalId.Should().Be("1889243");
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
        // 无 API Key 时走网站爬取路径，不应调用官方 API；空页面返回空结果
        var client = new TmdbApiClient("", CreateMockHttpClient("<html><body>no results</body></html>"));
        var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "test" });

        response.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDetailAsync_ShouldFallbackToScraping_WhenApiKeyIsEmpty()
    {
        // 无 API Key 时走网站爬取路径，不应调用官方 API；返回对象而非抛异常
        var client = new TmdbApiClient("", CreateMockHttpClient("<html><body>no detail</body></html>"));
        var result = await client.GetDetailAsync("123");

        result.Should().NotBeNull();
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

        // 官方 API 分支需要 API Key + 代理（API 在国内被墙），这里模拟已配置代理
        var oldProxy = AppSettings.HttpProxy;
        AppSettings.HttpProxy = "http://127.0.0.1:8080";
        try
        {
            var client = new TmdbApiClient("fake_key", new HttpClient(handler.Object));
            var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "Interstellar" });

            response.TotalCount.Should().Be(1);
            response.Results.Should().HaveCount(1);
            response.Results[0].Title.Should().Be("星际穿越");
            response.Results[0].Year.Should().Be(2014);
            response.Results[0].ExternalId.Should().Be("157336");
            response.Results[0].Source.Should().Be("tmdb");
        }
        finally
        {
            AppSettings.HttpProxy = oldProxy;
        }
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
}
