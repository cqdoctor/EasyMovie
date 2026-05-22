using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Moq.Protected;
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

    [Fact]
    public async Task SearchAsync_ShouldParseResults()
    {
        var json = @"{
            ""total"": 1,
            ""subjects"": [{
                ""id"": ""1889243"",
                ""title"": ""星际穿越"",
                ""original_title"": ""Interstellar"",
                ""year"": ""2014"",
                ""directors"": [{""name"": ""克里斯托弗·诺兰""}],
                ""casts"": [{""name"": ""马修·麦康纳""}, {""name"": ""安妮·海瑟薇""}],
                ""images"": {""large"": ""https://img.douban.com/poster.jpg""},
                ""rating"": {""average"": 9.4}
            }]
        }";

        var client = new DoubanApiClient(CreateMockHttpClient(json));
        var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "星际穿越" });

        response.TotalCount.Should().Be(1);
        response.Results.Should().HaveCount(1);
        response.Results[0].Title.Should().Be("星际穿越");
        response.Results[0].Year.Should().Be(2014);
        response.Results[0].Director.Should().Be("克里斯托弗·诺兰");
        response.Results[0].Cast.Should().Contain("马修·麦康纳");
        response.Results[0].Rating.Should().Be(9.4);
        response.Results[0].ExternalId.Should().Be("1889243");
        response.Results[0].Source.Should().Be("douban");
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
}

public class TmdbApiClientTests
{
    [Fact]
    public void SourceName_ShouldBeTmdb()
    {
        new TmdbApiClient("test_key").SourceName.Should().Be("tmdb");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenApiKeyIsEmpty()
    {
        var client = new TmdbApiClient("");
        var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "test" });

        response.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDetailAsync_ShouldReturnNull_WhenApiKeyIsEmpty()
    {
        var client = new TmdbApiClient("");
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

        // 详情 mock
        var detailJson = @"{
            ""title"": ""星际穿越"",
            ""original_title"": ""Interstellar"",
            ""release_date"": ""2014-11-07"",
            ""overview"": ""探索宇宙与亲情"",
            ""poster_path"": ""/poster.jpg"",
            ""runtime"": 169,
            ""vote_average"": 8.4,
            ""vote_count"": 30000,
            ""credits"": {
                ""cast"": [{""name"": ""马修·麦康纳""}, {""name"": ""安妮·海瑟薇""}],
                ""crew"": [{""name"": ""克里斯托弗·诺兰"", ""job"": ""Director""}]
            },
            ""production_countries"": [{""name"": ""美国""}]
        }";

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri!.ToString().Contains("/movie/157336")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(detailJson)
            });

        var client = new TmdbApiClient("fake_key", new HttpClient(handler.Object));
        var response = await client.SearchAsync(new MovieSearchRequest { Keyword = "Interstellar" });

        response.TotalCount.Should().Be(1);
        response.Results.Should().HaveCount(1);
        response.Results[0].Title.Should().Be("星际穿越");
        response.Results[0].Year.Should().Be(2014);
        response.Results[0].Director.Should().Be("克里斯托弗·诺兰");
        response.Results[0].Runtime.Should().Be(169);
        response.Results[0].Source.Should().Be("tmdb");
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
