using System.Net;
using System.Text.RegularExpressions;
using EasyMovie.Core;
using EasyMovie.Core.Interfaces;

namespace EasyMovie.Tools.MovieApi;

/// <summary>
/// 1905电影网客户端
/// </summary>
public class C1905ApiClient : IMovieApiClient
{
    private readonly HttpClient _http;

    public C1905ApiClient(HttpClient? http = null)
    {
        _http = http ?? HttpClientFactory.Create();
        _http.DefaultRequestHeaders.Add("Accept", "text/html");
        _http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
    }

    public string SourceName => "1905";

    public async Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default)
    {
        try
        {
            var kw = Uri.EscapeDataString(request.Keyword);
            var html = await _http.GetStringAsync($"https://www.1905.com/search/?q={kw}", ct);
            var results = ParseSearch(html).Take(request.PageSize).ToList();
            return new MovieSearchResponse { Results = results, TotalCount = results.Count };
        }
        catch { return new MovieSearchResponse(); }
    }

    public async Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync(externalId, ct);
            return ParseDetail(html, externalId);
        }
        catch { return null; }
    }

    private static readonly string[] InvalidLabels = { "人员", "人物", "演员", "主演", "导演", "暂无", "未知", "暂未录入", "更多" };

    private static bool IsTemplateOrLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"\$\{.*?\}|\$\(data\.\w+\)|\{\{.*?\}\}|<%.*?%>")) return true;
        if (InvalidLabels.Contains(value)) return true;
        return false;
    }

    private static List<MovieSearchResult> ParseSearch(string html)
    {
        var results = new List<MovieSearchResult>();
        // 1905 搜索链接: href="https://www.1905.com/mdb/film/xxxxx/"
        foreach (Match m in Regex.Matches(html, @"href=""(https://www\.1905\.com/mdb/film/\d+/?)""[^>]*>([^<]+)</a>"))
        {
            var url = m.Groups[1].Value;
            var title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            if (results.Any(r => r.ExternalId == url)) continue;
            results.Add(new MovieSearchResult { Title = title, ExternalId = url, Source = "1905" });
        }
        return results;
    }

    private static MovieSearchResult ParseDetail(string html, string url)
    {
        var r = new MovieSearchResult { ExternalId = url, Source = "1905" };

        // 标题
        var tm = Regex.Match(html, @"<h1[^>]*>([^<]+)</h1>");
        if (tm.Success) r.Title = WebUtility.HtmlDecode(tm.Groups[1].Value).Trim();

        // 英文名
        var enm = Regex.Match(html, @"英文名[：:]\s*([^<\n]+)");
        if (enm.Success) r.OriginalTitle = enm.Groups[1].Value.Trim();

        // 导演
        var dm = Regex.Match(html, @"导演[：:]\s*<[^>]*>\s*([^<\n]+)");
        if (dm.Success)
        {
            var dir = dm.Groups[1].Value.Trim();
            if (!IsTemplateOrLabel(dir)) r.Director = dir;
        }

        var actors = Regex.Matches(html, @"主演[：:][^<]*(?:<[^>]*>([^<]*)</a>\s*)+");
        var castMatch = Regex.Match(html, @"主演[：:]\s*([^<\n]+)");
        if (castMatch.Success)
        {
            var cast = castMatch.Groups[1].Value.Trim().Replace("&nbsp;", " ");
            if (!IsTemplateOrLabel(cast)) r.Cast = cast;
        }

        // 年份
        var ym = Regex.Match(html, @"上映[：:]\s*(\d{4})");
        if (ym.Success && int.TryParse(ym.Groups[1].Value, out var y2)) r.Year = y2;

        // 简介
        var sm = Regex.Match(html, @"剧情[：:][\s\n]*([^<]+)");
        if (sm.Success) r.Synopsis = WebUtility.HtmlDecode(sm.Groups[1].Value).Trim();

        // 海报
        var pm = Regex.Match(html, @"<img[^>]*src=""(https?://[^""]+(?:poster|cover|pic)[^""]+\.(?:jpg|png))""");
        if (pm.Success) r.PosterUrl = pm.Groups[1].Value;

        // 评分
        var rm = Regex.Match(html, @"评分[：:]\s*([\d.]+)");
        if (rm.Success && double.TryParse(rm.Groups[1].Value, out var rate)) r.Rating = rate;

        return r;
    }
}
