using System.Net;
using System.Text.RegularExpressions;
using MovieManager.Core;
using MovieManager.Core.Interfaces;

namespace MovieManager.Tools.MovieApi;

/// <summary>
/// 猫眼电影客户端 — 抓取 maoyan.com
/// </summary>
public class MaoyanApiClient : IMovieApiClient
{
    private readonly HttpClient _http;

    public MaoyanApiClient(HttpClient? http = null)
    {
        _http = http ?? HttpClientFactory.Create();
        _http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml");
        _http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
    }

    public string SourceName => "maoyan";

    public async Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default)
    {
        try
        {
            var kw = Uri.EscapeDataString(request.Keyword);
            var html = await _http.GetStringAsync($"https://maoyan.com/query?kw={kw}", ct);
            var results = ParseSearch(html).Take(request.PageSize).ToList();
            return new MovieSearchResponse { Results = results, TotalCount = results.Count };
        }
        catch { return new MovieSearchResponse(); }
    }

    public async Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"https://maoyan.com/films/{externalId}", ct);
            return ParseDetail(html, externalId);
        }
        catch { return null; }
    }

    private static List<MovieSearchResult> ParseSearch(string html)
    {
        var results = new List<MovieSearchResult>();
        // 猫眼搜索结果: href="/films/xxxx"  + 电影名
        foreach (Match m in Regex.Matches(html, @"href=""/films/(\d+)""[^>]*>.*?<span class=""name"">([^<]+)</span>"))
        {
            var id = m.Groups[1].Value;
            var title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            if (results.Any(r => r.ExternalId == id)) continue;

            // 尝试提取年份
            var year = 0;
            var yearM = Regex.Match(html.Substring(m.Index, Math.Min(500, html.Length - m.Index)), @"(\d{4})");
            if (yearM.Success) int.TryParse(yearM.Groups[1].Value, out year);

            // 评分
            double? rating = null;
            var rateM = Regex.Match(html.Substring(m.Index, Math.Min(500, html.Length - m.Index)), @"score"">\s*(\d+\.?\d*)\s*<");
            if (rateM.Success && double.TryParse(rateM.Groups[1].Value, out var r)) rating = r;

            results.Add(new MovieSearchResult
            {
                Title = title, Year = year, Rating = rating,
                ExternalId = id, Source = "maoyan"
            });
        }
        return results;
    }

    private static MovieSearchResult ParseDetail(string html, string id)
    {
        var r = new MovieSearchResult { ExternalId = id, Source = "maoyan" };

        var titleM = Regex.Match(html, @"<h1[^>]*>([^<]+)</h1>");
        if (titleM.Success) r.Title = WebUtility.HtmlDecode(titleM.Groups[1].Value).Trim();

        var yearM = Regex.Match(html, @"<li class=""ellipsis"">(?:(\d{4})[^<]*)?</li>");
        // 更宽松的年份
        var yearM2 = Regex.Match(html, @"(\d{4})[^<]*上映");
        if (yearM2.Success) { int.TryParse(yearM2.Groups[1].Value, out var yy); r.Year = yy; }

        var enM = Regex.Match(html, @"<div class=""ename"">([^<]+)</div>");
        if (enM.Success) r.OriginalTitle = WebUtility.HtmlDecode(enM.Groups[1].Value).Trim();

        var synM = Regex.Match(html, @"<span class=""dra"">([^<]+)</span>");
        if (synM.Success) r.Synopsis = WebUtility.HtmlDecode(synM.Groups[1].Value).Trim();

        var posterM = Regex.Match(html, @"<img[^>]*src=""(https?://[^""]+\.(?:jpg|png|webp))""");
        if (posterM.Success) r.PosterUrl = posterM.Groups[1].Value;

        var rateM = Regex.Match(html, @"电影评分[^<]*<[^>]*>([\d.]+)<");
        if (rateM.Success && double.TryParse(rateM.Groups[1].Value, out var rate)) r.Rating = rate;

        // 猫眼评分 /10
        var rateM2 = Regex.Match(html, @"class=""star-num""[^>]*>([\d.]+)<");
        if (rateM2.Success && double.TryParse(rateM2.Groups[1].Value, out var rate2)) r.Rating = rate2;

        var dirM = Regex.Match(html, @"导演[^<]*</span>\s*<[^>]*>\s*([^<]+)\s*<");
        if (dirM.Success) r.Director = dirM.Groups[1].Value.Trim();

        var actorsM = Regex.Matches(html, @"<a[^>]*href=""/films/celebrity/\d+""[^>]*>([^<]+)</a>");
        var actors = actorsM.Take(5).Select(a => a.Groups[1].Value.Trim()).ToList();
        if (actors.Any()) r.Cast = string.Join(", ", actors);

        // 时长
        var durM = Regex.Match(html, @"(\d+)\s*分钟");
        if (durM.Success) r.Runtime = int.Parse(durM.Groups[1].Value);

        return r;
    }
}
