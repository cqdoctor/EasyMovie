using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using EasyMovie.Core;
using EasyMovie.Core.Helpers;
using EasyMovie.Core.Interfaces;
using Serilog;

namespace EasyMovie.Tools.MovieApi;

/// <summary>
/// 百度百科电影信息爬取客户端
/// </summary>
public class BaiduBaikeApiClient : IMovieApiClient
{
    private readonly HttpClient _http;

    public BaiduBaikeApiClient(HttpClient? http = null)
    {
        _http = http ?? CreateClient();
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        };
        // 百度百科是国内站点，不走代理（代理可能干扰国内访问）
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
        client.DefaultRequestHeaders.Referrer = new Uri("https://baike.baidu.com/");
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    public string SourceName => "baike";

    private static DateTime _lastRequest = DateTime.MinValue;
    private static readonly object _lock = new();
    private const int MinIntervalMs = 500;
    private static async Task ThrottleAsync()
    {
        TimeSpan wait;
        lock (_lock)
        {
            var e = DateTime.UtcNow - _lastRequest;
            wait = TimeSpan.FromMilliseconds(MinIntervalMs) - e;
            if (wait <= TimeSpan.Zero) { _lastRequest = DateTime.UtcNow; return; }
            _lastRequest = DateTime.UtcNow.Add(wait);
        }
        await Task.Delay(wait);
    }

    private static readonly string[] InvalidLabels = { "人员", "人物", "演员", "主演", "导演", "暂无", "未知", "暂未录入", "更多" };

    // 导演黑名单：中英文职业标签，提取导演人名时需排除
    
    private static bool IsTemplateOrLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (Regex.IsMatch(value, @"\$\{.*?\}|\$\(data\.\w+\)|\{\{.*?\}\}|<%.*?%>")) return true;
        if (InvalidLabels.Contains(value)) return true;
        return false;
    }

    /// <summary>移除 HTML 标签并去除首尾空白</summary>
    
    
    public async Task<MovieSearchResponse> SearchAsync(MovieSearchRequest req, CancellationToken ct = default)
    {
        try
        {
            await ThrottleAsync();
            var keyword = Uri.EscapeDataString(req.Keyword);
            var html = await _http.GetStringAsync($"https://baike.baidu.com/search?word={keyword}", ct);
            var results = ParseSearch(html).Take(req.PageSize).ToList();
            return new MovieSearchResponse { Results = results, TotalCount = results.Count };
        }
        catch (Exception ex) { Log.Error(ex, "百度百科搜索解析失败"); return new MovieSearchResponse(); }
    }

    public async Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
    {
        try
        {
            await ThrottleAsync();
            var html = await _http.GetStringAsync($"https://baike.baidu.com/item/{Uri.EscapeDataString(externalId)}", ct);
            return ParseDetail(html, externalId);
        }
        catch (Exception ex) { Log.Error(ex, "百度百科获取详情失败"); return null; }
    }

    private static List<MovieSearchResult> ParseSearch(string html)
    {
        var results = new List<MovieSearchResult>();
        var seen = new HashSet<string>();
        // 百度百科搜索结果中的词条链接: /item/xxx 或 https://baike.baidu.com/item/xxx
        foreach (Match m in Regex.Matches(html, @"href=""(?:https://baike\.baidu\.com)?(/item/[^""#?]+)""[^>]*>([^<]+)</a>"))
        {
            var path = m.Groups[1].Value;
            var title = HttpUtility.HtmlDecode(m.Groups[2].Value).Trim();
            if (string.IsNullOrWhiteSpace(title) || IsTemplateOrLabel(title)) continue;
            // 提取 item slug 作为 ExternalId
            var slug = path.TrimStart('/').Substring("item/".Length).TrimEnd('/');
            if (seen.Add(slug))
            {
                results.Add(new MovieSearchResult
                {
                    Title = title,
                    ExternalId = slug,
                    Source = "baike"
                });
            }
        }
        return results;
    }

    private static MovieSearchResult ParseDetail(string html, string id)
    {
        var r = new MovieSearchResult { ExternalId = id, Source = "baike" };

        // 标题：优先 <h1>，兜底 <title>
        var tm = Regex.Match(html, @"<h1[^>]*>([^<]+)</h1>");
        if (tm.Success) r.Title = HttpUtility.HtmlDecode(tm.Groups[1].Value).Trim();
        else
        {
            var tm2 = Regex.Match(html, @"<title>([^_<]+?)(?:_百度百科)?</title>");
            if (tm2.Success) r.Title = HttpUtility.HtmlDecode(tm2.Groups[1].Value).Trim();
        }

        // 解析基本信息表格 basicInfo-item
        var info = ParseBasicInfo(html);

        // 导演
        if (info.TryGetValue("导演", out var director))
            r.Director = MovieCreditCleaner.CleanDirector(director);

        // 主演
        if (info.TryGetValue("主演", out var cast))
        {
            var castClean = MovieCreditCleaner.CleanDirector(cast);
            if (!string.IsNullOrWhiteSpace(castClean)) r.Cast = castClean;
        }

        // 制片地区 / 国家/地区 / 国家地区
        if (info.TryGetValue("制片地区", out var country)
            || info.TryGetValue("国家/地区", out country)
            || info.TryGetValue("国家地区", out country))
            r.Country = HttpUtility.HtmlDecode(TextCleaner.StripHtml(country)).Trim();

        // 上映时间：提取年份
        if (info.TryGetValue("上映时间", out var release))
        {
            var ym = Regex.Match(release, @"(\d{4})");
            if (ym.Success && int.TryParse(ym.Groups[1].Value, out var year)) r.Year = year;
        }

        // 片长 / 时长：提取数字
        if (info.TryGetValue("片长", out var runtime) || info.TryGetValue("时长", out runtime))
        {
            var rm = Regex.Match(runtime, @"(\d+)");
            if (rm.Success && int.TryParse(rm.Groups[1].Value, out var rt)) r.Runtime = rt;
        }

        // 语言
        if (info.TryGetValue("语言", out var lang))
            r.Language = HttpUtility.HtmlDecode(TextCleaner.StripHtml(lang)).Trim();

        // 简介：剧情简介 / 内容简介
        r.Synopsis = ParseSynopsis(html);

        // 海报：summary-pic 中的 img src
        var pm = Regex.Match(html, @"class=""summary-pic""[^>]*>[\s\S]*?<img[^>]*src=""([^""]+)""");
        if (!pm.Success) pm = Regex.Match(html, @"<img[^>]*class=""[^""]*summary[^""]*""[^>]*src=""([^""]+)""");
        if (pm.Success) r.PosterUrl = pm.Groups[1].Value;

        return r;
    }

    /// <summary>
    /// 解析百度百科基本信息表格（class="basicInfo-item"），
    /// 将 dt(标签)/dd(值) 配对存入字典。
    /// </summary>
    private static Dictionary<string, string> ParseBasicInfo(string html)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 匹配 dt/dd 配对
        var dtMatches = Regex.Matches(html, @"<dt[^>]*class=""[^""]*basicInfo-item[^""]*name[^""]*""[^>]*>([\s\S]*?)</dt>");
        var ddMatches = Regex.Matches(html, @"<dd[^>]*class=""[^""]*basicInfo-item[^""]*value[^""]*""[^>]*>([\s\S]*?)</dd>");

        var count = Math.Min(dtMatches.Count, ddMatches.Count);
        for (int i = 0; i < count; i++)
        {
            var key = TextCleaner.StripHtml(dtMatches[i].Groups[1].Value).Trim();
            var val = ddMatches[i].Groups[1].Value;
            if (!string.IsNullOrEmpty(key) && !dict.ContainsKey(key))
                dict[key] = val;
        }
        return dict;
    }

    /// <summary>
    /// 解析剧情简介/内容简介段落。
    /// </summary>
    private static string? ParseSynopsis(string html)
    {
        foreach (var heading in new[] { "剧情简介", "内容简介", "剧情介绍", "简介" })
        {
            // 标题后紧跟的 para 段落
            var sm = Regex.Match(html, $@"{heading}[\s\S]*?<div[^>]*class=""[^""]*para[^""]*""[^>]*>([\s\S]*?)</div>");
            if (!sm.Success)
                sm = Regex.Match(html, $@"{heading}[\s\S]*?<p[^>]*>([\s\S]*?)</p>");
            if (sm.Success)
            {
                var text = HttpUtility.HtmlDecode(TextCleaner.StripHtml(sm.Groups[1].Value)).Trim();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return null;
    }
}
