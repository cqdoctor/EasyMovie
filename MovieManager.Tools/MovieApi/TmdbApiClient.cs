using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MovieManager.Core.Interfaces;

namespace MovieManager.Tools.MovieApi;

/// <summary>
/// TMDB (The Movie Database) API v3 客户端
/// 需要 API Key，申请地址: https://www.themoviedb.org/settings/api
/// </summary>
public class TmdbApiClient : IMovieApiClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/w500";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public TmdbApiClient(string apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string SourceName => "tmdb";

    public async Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new MovieSearchResponse();

        try
        {
            var encoded = HttpUtility.UrlEncode(request.Keyword);
            var url = $"{BaseUrl}/search/movie?api_key={_apiKey}&language=zh-CN&query={encoded}&page={request.Page}";

            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var raw = await response.Content.ReadFromJsonAsync<TmdbSearchResponse>(JsonOptions, ct);

            if (raw?.Results == null)
                return new MovieSearchResponse();

            var results = new List<MovieSearchResult>();
            foreach (var r in raw.Results)
            {
                results.Add(new MovieSearchResult
                {
                    Title = r.Title ?? r.OriginalTitle ?? "",
                    OriginalTitle = r.OriginalTitle != r.Title ? r.OriginalTitle : null,
                    Year = TryParseYear(r.ReleaseDate),
                    Synopsis = r.Overview,
                    PosterUrl = string.IsNullOrEmpty(r.PosterPath)
                        ? null : $"{ImageBaseUrl}{r.PosterPath}",
                    Rating = r.VoteAverage,
                    RatingCount = r.VoteCount,
                    ExternalId = r.Id.ToString(),
                    Source = "tmdb"
                });
            }

            return new MovieSearchResponse
            {
                Results = results,
                TotalCount = raw.TotalResults,
                Page = raw.Page,
                PageSize = request.PageSize
            };
        }
        catch (Exception)
        {
            return new MovieSearchResponse();
        }
    }

    public async Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return null;

        try
        {
            // 获取基本信息
            var url = $"{BaseUrl}/movie/{externalId}?api_key={_apiKey}&language=zh-CN&append_to_response=credits";
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var raw = await response.Content.ReadFromJsonAsync<TmdbMovieDetail>(JsonOptions, ct);
            if (raw == null) return null;

            var director = raw.Credits?.Crew?
                .FirstOrDefault(c => c.Job == "Director")?.Name;

            var cast = raw.Credits?.Cast?
                .Take(5)
                .Select(c => c.Name)
                .ToArray();

            return new MovieSearchResult
            {
                Title = raw.Title ?? raw.OriginalTitle ?? "",
                OriginalTitle = raw.OriginalTitle != raw.Title ? raw.OriginalTitle : null,
                Year = TryParseYear(raw.ReleaseDate),
                Director = director,
                Cast = cast is { Length: > 0 } ? string.Join(", ", cast) : null,
                Country = raw.ProductionCountries?.FirstOrDefault()?.Name,
                Synopsis = raw.Overview,
                PosterUrl = string.IsNullOrEmpty(raw.PosterPath)
                    ? null : $"{ImageBaseUrl}{raw.PosterPath}",
                Runtime = raw.Runtime,
                Rating = raw.VoteAverage,
                RatingCount = raw.VoteCount,
                ExternalId = externalId,
                Source = "tmdb"
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int TryParseYear(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return 0;
        // TMDB 日期格式 "2024-06-15"
        var parts = dateStr.Split('-');
        return parts.Length > 0 && int.TryParse(parts[0], out var y) ? y : 0;
    }

    // ═══════════ TMDB JSON 模型 ═══════════

    private class TmdbSearchResponse
    {
        [JsonPropertyName("page")]
        public int Page { get; set; }
        [JsonPropertyName("total_results")]
        public int TotalResults { get; set; }
        [JsonPropertyName("results")]
        public List<TmdbSearchResult>? Results { get; set; }
    }

    private class TmdbSearchResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }
        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }
        [JsonPropertyName("overview")]
        public string? Overview { get; set; }
        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }
        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }
        [JsonPropertyName("vote_count")]
        public int VoteCount { get; set; }
    }

    private class TmdbMovieDetail
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }
        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }
        [JsonPropertyName("overview")]
        public string? Overview { get; set; }
        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }
        [JsonPropertyName("runtime")]
        public int? Runtime { get; set; }
        [JsonPropertyName("vote_average")]
        public double VoteAverage { get; set; }
        [JsonPropertyName("vote_count")]
        public int VoteCount { get; set; }
        [JsonPropertyName("credits")]
        public TmdbCredits? Credits { get; set; }
        [JsonPropertyName("production_countries")]
        public List<TmdbCountry>? ProductionCountries { get; set; }
    }

    private class TmdbCredits
    {
        [JsonPropertyName("cast")]
        public List<TmdbPerson>? Cast { get; set; }
        [JsonPropertyName("crew")]
        public List<TmdbCrew>? Crew { get; set; }
    }

    private class TmdbPerson
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class TmdbCrew
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("job")]
        public string? Job { get; set; }
    }

    private class TmdbCountry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
