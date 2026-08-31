using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyMovie.Core;
using EasyMovie.Core.Helpers;
using EasyMovie.Core.Interfaces;
using System.Globalization;

namespace EasyMovie.Tools.MovieApi;

public class OmdbApiClient : IMovieApiClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private const string BaseUrl = "http://www.omdbapi.com/";

    
    public OmdbApiClient(string apiKey = "", HttpClient? http = null)
    {
        _apiKey = apiKey ?? "";
        // OMDb 为国外站点，国内常被 GFW 拦截；若用户配置了全局代理则走代理。
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var proxy = AppSettings.HttpProxy;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try
            {
                if (!proxy.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !proxy.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    proxy = "http://" + proxy;
                handler.Proxy = new WebProxy(proxy, true);
                handler.UseProxy = true;
            }
            catch (Exception ex) { Serilog.Log.Error(ex, "配置代理失败"); }
        }
        _http = http ?? new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
    }

    public string SourceName => "omdb";

    public async Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Keyword))
            return new MovieSearchResponse();

        if (string.IsNullOrWhiteSpace(_apiKey))
            return new MovieSearchResponse();

        // OMDb 是英文源（IMDb 包装），用中文片名必 0 结果。先按原关键词搜，
        // 若落空且能从标题抽取英文名（如“寄生虫 Parasite”），自动回退英文名再搜一次。
        var resp = await SearchByKeywordAsync(request.Keyword, request, ct);
        if (resp.Results.Count > 0)
            return resp;

        var en = DoubanApiClient.ExtractEnglishHint(request.Keyword);
        if (!string.IsNullOrWhiteSpace(en) && en != request.Keyword.Trim())
            return await SearchByKeywordAsync(en, request, ct);

        return resp;
    }

    private async Task<MovieSearchResponse> SearchByKeywordAsync(string keyword, MovieSearchRequest request, CancellationToken ct)
    {
        try
        {
            var encoded = Uri.EscapeDataString(keyword);
            var url = $"{BaseUrl}?apikey={Uri.EscapeDataString(_apiKey)}&s={encoded}&type=movie";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var response = new MovieSearchResponse
            {
                Page = request.Page,
                PageSize = request.PageSize
            };

            if (root.TryGetProperty("Response", out var respProp) && respProp.ValueKind == JsonValueKind.String
                && respProp.GetString() == "False")
                return response;

            if (root.TryGetProperty("totalResults", out var trProp) && trProp.ValueKind == JsonValueKind.String
                && int.TryParse(trProp.GetString(), out var total))
                response.TotalCount = total;

            if (root.TryGetProperty("Search", out var searchProp) && searchProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in searchProp.EnumerateArray().Take(request.PageSize > 0 ? request.PageSize : 10))
                {
                    var title = GetString(item, "Title");
                    var year = 0;
                    if (item.TryGetProperty("Year", out var yProp) && yProp.ValueKind == JsonValueKind.String)
                    {
                        var ys = Regex.Match(yProp.GetString() ?? "", @"\d{4}");
                        if (ys.Success) year = int.Parse(ys.Value);
                    }
                    var poster = GetString(item, "Poster");

                    response.Results.Add(new MovieSearchResult
                    {
                        Title = title,
                        Year = year,
                        PosterUrl = (!string.IsNullOrEmpty(poster) && poster != "N/A") ? poster : null,
                        ExternalId = $"{title}|{year}",
                        Source = "omdb"
                    });
                }
            }

            return response;
        }
        catch
        {
            return new MovieSearchResponse();
        }
    }

    public async Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        if (string.IsNullOrWhiteSpace(_apiKey))
            return null;

        try
        {
            // externalId 格式: "title|year"
            string title;
            string? year = null;
            var parts = externalId.Split('|');
            title = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out var y) && y > 0) year = y.ToString();

            var encodedTitle = Uri.EscapeDataString(title);
            var url = $"{BaseUrl}?apikey={Uri.EscapeDataString(_apiKey)}&t={encodedTitle}&plot=full";
            if (!string.IsNullOrEmpty(year)) url += $"&y={year}";

            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Response", out var respProp) && respProp.ValueKind == JsonValueKind.String
                && respProp.GetString() == "False")
                return null;

            return ParseDetail(root);
        }
        catch
        {
            return null;
        }
    }

    private MovieSearchResult ParseDetail(JsonElement root)
    {
        var title = GetString(root, "Title");

        var year = 0;
        if (root.TryGetProperty("Year", out var yProp) && yProp.ValueKind == JsonValueKind.String)
        {
            var ys = Regex.Match(yProp.GetString() ?? "", @"\d{4}");
            if (ys.Success) year = int.Parse(ys.Value);
        }

        var director = MovieCreditCleaner.CleanDirector(GetString(root, "Director"));

        var actors = GetString(root, "Actors");
        var cast = (string.IsNullOrEmpty(actors) || actors == "N/A") ? null : actors;

        var country = GetString(root, "Country");
        if (country == "N/A") country = "";
        country = MapCountryToChinese(country);

        var plot = GetString(root, "Plot");
        if (plot == "N/A") plot = "";

        var poster = GetString(root, "Poster");
        var runtime = ExtractRuntime(GetString(root, "Runtime"));

        double? rating = null;
        if (root.TryGetProperty("imdbRating", out var rProp) && rProp.ValueKind == JsonValueKind.String)
        {
            var rs = rProp.GetString();
            if (!string.IsNullOrEmpty(rs) && rs != "N/A" && double.TryParse(rs, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) rating = r;
        }

        int? ratingCount = null;
        if (root.TryGetProperty("imdbVotes", out var vProp) && vProp.ValueKind == JsonValueKind.String)
        {
            var vs = (vProp.GetString() ?? "").Replace(",", "");
            if (int.TryParse(vs, out var rc)) ratingCount = rc;
        }

        return new MovieSearchResult
        {
            Title = title,
            Year = year,
            Director = string.IsNullOrEmpty(director) ? null : director,
            Cast = cast,
            Country = string.IsNullOrEmpty(country) ? null : country,
            Synopsis = string.IsNullOrEmpty(plot) ? null : plot,
            PosterUrl = (!string.IsNullOrEmpty(poster) && poster != "N/A") ? poster : null,
            Runtime = runtime > 0 ? runtime : null,
            Rating = rating,
            RatingCount = ratingCount,
            ExternalId = $"{title}|{year}",
            Source = "omdb"
        };
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? "";
        return "";
    }

    /// <summary>
    /// 从 Runtime 字符串（如 "148 min"）中提取分钟数。
    /// </summary>
    private static int ExtractRuntime(string runtime)
    {
        if (string.IsNullOrEmpty(runtime)) return 0;
        var m = Regex.Match(runtime, @"\d+");
        if (m.Success) return int.Parse(m.Value);
        return 0;
    }

    
    /// <summary>英文国家名映射为中文</summary>
    private static readonly Dictionary<string, string> CountryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "United States", "美国" }, { "USA", "美国" }, { "United States of America", "美国" },
        { "United Kingdom", "英国" }, { "UK", "英国" }, { "Great Britain", "英国" },
        { "China", "中国" }, { "Hong Kong", "中国香港" }, { "Taiwan", "中国台湾" },
        { "Japan", "日本" }, { "South Korea", "韩国" }, { "Korea", "韩国" },
        { "France", "法国" }, { "Germany", "德国" }, { "Italy", "意大利" },
        { "Spain", "西班牙" }, { "Canada", "加拿大" }, { "Australia", "澳大利亚" },
        { "Russia", "俄罗斯" }, { "India", "印度" }, { "Thailand", "泰国" },
        { "Iran", "伊朗" }, { "Sweden", "瑞典" }, { "Denmark", "丹麦" },
        { "Norway", "挪威" }, { "Finland", "芬兰" }, { "Netherlands", "荷兰" },
        { "Belgium", "比利时" }, { "Switzerland", "瑞士" }, { "Austria", "奥地利" },
        { "Poland", "波兰" }, { "Czech Republic", "捷克" }, { "Hungary", "匈牙利" },
        { "Ireland", "爱尔兰" }, { "Portugal", "葡萄牙" }, { "Brazil", "巴西" },
        { "Mexico", "墨西哥" }, { "Argentina", "阿根廷" }, { "Turkey", "土耳其" },
        { "Egypt", "埃及" }, { "South Africa", "南非" }, { "New Zealand", "新西兰" },
        { "Philippines", "菲律宾" }, { "Malaysia", "马来西亚" }, { "Singapore", "新加坡" },
        { "Indonesia", "印度尼西亚" }, { "Vietnam", "越南" },
    };

    private static string MapCountryToChinese(string country)
    {
        if (string.IsNullOrWhiteSpace(country)) return country;
        var parts = country.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Select(p => CountryMap.TryGetValue(p, out var cn) ? cn : p)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        return string.Join(" / ", parts);
    }

    }
