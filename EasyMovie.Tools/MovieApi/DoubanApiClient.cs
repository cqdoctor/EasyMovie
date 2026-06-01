﻿using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using EasyMovie.Core;
using EasyMovie.Core.Interfaces;

namespace EasyMovie.Tools.MovieApi;

public class DoubanApiClient : IMovieApiClient
{
    private readonly HttpClient _http;
    private static DateTime _lastRequest = DateTime.MinValue;
    private static readonly object _lock = new();
    private const int MinIntervalMs = 2000;

    public DoubanApiClient(HttpClient? http = null) { _http = http ?? CreateClient(); }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
        client.DefaultRequestHeaders.Referrer = new Uri("https://movie.douban.com/");
        client.Timeout = TimeSpan.FromSeconds(10);
        var cookie = AppSettings.DoubanCookie;
        if (!string.IsNullOrEmpty(cookie)) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static async Task ThrottleAsync()
    {
        TimeSpan wait;
        lock (_lock) { var e = DateTime.UtcNow - _lastRequest; wait = TimeSpan.FromMilliseconds(MinIntervalMs) - e; if (wait <= TimeSpan.Zero) { _lastRequest = DateTime.UtcNow; return; } _lastRequest = DateTime.UtcNow.Add(wait); }
        await Task.Delay(wait);
    }

    public string SourceName => "douban";

    public static string ExtractChineseKeyword(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        // 提取所有中文段
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in title)
        {
            if (c >= 0x4e00 && c <= 0x9fff)
            {
                current.Append(c);
            }
            else
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
        }
        if (current.Length > 0) parts.Add(current.ToString());

        if (parts.Count > 0)
        {
            // 拼接所有中文段（如"哆啦A梦：大雄与天空的理想乡" → "哆啦梦大雄与天空的理想乡"）
            return string.Join("", parts);
        }
        return title.Split(' ')[0].Trim();
    }

    /// <summary>提取文件名中的英文名用于验证 (去掉中文、年份、编码标记)</summary>
    public static string? ExtractEnglishHint(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var cleaned = Regex.Replace(title, @"[\u4e00-\u9fff]+\d*", " ");
        cleaned = Regex.Replace(cleaned, @"\d*[\u4e00-\u9fff]+", " ");
        cleaned = Regex.Replace(cleaned, @"\b(?:4K|1080[pi]|720p|2160p|BluRay|WEB-?DL|WEBRip|HDRip|BDRip|BRRip|x26[45]|AAC|DTS|DD5\.1|E?AC3|DDP?5\.1|HEVC|10bit|HDR|SDR|Remux|HC|H264|H265|PROPER|REPACK|EXTENDED|UNCUT|NF|AMZN|DSNP|HMAX|ATVP|WEB|Audio|Multi|Dual|Dubbed|Subbed|Complete|REMUX|DV|Hybrid|REPACK)\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\b\d{4,}\b", " ");
        cleaned = Regex.Replace(cleaned, @"[.\-_]", " ");
        cleaned = Regex.Replace(cleaned, @"[^\w\s]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static readonly string[] InvalidLabels = { "人员", "人物", "演员", "主演", "导演", "暂无", "未知", "暂未录入", "更多" };

    private static bool IsTemplateOrLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (Regex.IsMatch(value, @"\$\{.*?\}|\$\(data\.\w+\)|\{\{.*?\}\}|<%.*?%>")) return true;
        if (InvalidLabels.Contains(value)) return true;
        return false;
    }

    public async Task<MovieSearchResponse> SearchAsync(MovieSearchRequest req, CancellationToken ct = default)
    {
        await ThrottleAsync();
        try
        {
            var keyword = ExtractChineseKeyword(req.Keyword);
            var html = await _http.GetStringAsync($"https://movie.douban.com/subject_search?search_text={Uri.EscapeDataString(keyword)}", ct);
            if (html.Contains("禁止访问")) return new MovieSearchResponse();
            return new MovieSearchResponse { Results = ParseSearch(html).Take(req.PageSize).ToList(), TotalCount = 1 };
        }
        catch { return new MovieSearchResponse(); }
    }

    public async Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
    {
        await ThrottleAsync();
        try
        {
            var html = await _http.GetStringAsync($"https://movie.douban.com/subject/{externalId}/", ct);
            return html.Contains("禁止访问") ? null : ParseDetail(html, externalId);
        }
        catch { return null; }
    }

    private static List<MovieSearchResult> ParseSearch(string html)
    {
        var results = new List<MovieSearchResult>();
        var idx = html.IndexOf("window.__DATA__ = {");
        if (idx >= 0)
        {
            idx = html.IndexOf('{', idx);
            var depth = 0; var end = idx;
            for (int i = idx; i < html.Length; i++) { if (html[i] == '{') depth++; else if (html[i] == '}') { depth--; if (depth == 0) { end = i + 1; break; } } }
            var json = html.Substring(idx, end - idx);
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    var id = item.GetProperty("id").GetInt32().ToString();
                    var title = item.GetProperty("title").GetString() ?? "";
                    var year = 0;
                    var ym = Regex.Match(title, @"\((\d{4})\)");
                    if (ym.Success) { int.TryParse(ym.Groups[1].Value, out year); title = title.Replace(ym.Value, "").Trim(); }
                    double? rating = item.TryGetProperty("rating", out var r) && r.TryGetProperty("value", out var v) ? v.GetDouble() : null;
                    var cover = item.TryGetProperty("cover_url", out var cu) ? cu.GetString() : null;
                    if (!string.IsNullOrEmpty(cover)) cover = cover.Replace("/m/", "/l/").Replace("/s/", "/l/");
                    var abs = item.TryGetProperty("abstract", out var a) ? a.GetString() ?? "" : "";
                    var abs2 = item.TryGetProperty("abstract_2", out var a2) ? a2.GetString() ?? "" : "";
                    var country = ""; var runtime = 0;
                    var genres = new HashSet<string> { "动作", "剧情", "科幻", "喜剧", "爱情", "恐怖", "悬疑", "惊悚", "犯罪", "冒险", "奇幻", "动画", "纪录", "战争", "历史", "歌舞", "家庭", "传记", "武侠", "古装", "运动", "音乐", "伦理", "短片", "脱口秀", "真人秀", "鬼怪", "西部", "灾难", "黑色电影" };
                    var junkSuffixes = new HashSet<string> { "人收藏", "人评论", "人看", "人想看", "人看过", "人评价", "人关注", "人推荐" };
                    foreach (var part in abs.Split(" / ").Select(p => p.Trim()))
                    {
                        if (part.EndsWith("分钟") && int.TryParse(part.Replace("分钟", ""), out var rt)) runtime = rt;
                        else if (!genres.Contains(part) && !part.EndsWith("分钟") && !int.TryParse(part, out _) && part.Any(c => c >= 0x4e00 && c <= 0x9fff) && !part.Contains("导演") && part.Length <= 10 && country == "" && !junkSuffixes.Any(j => part.EndsWith(j) || part.Contains(j))) country = part;
                    }
                    var director = ""; var cast = "";
                    if (!string.IsNullOrEmpty(abs2)) { var people = abs2.Split(" / ").Select(p => p.Trim()).Where(p => !IsTemplateOrLabel(p)).ToList(); if (people.Count > 0) director = people[0]; if (people.Count > 1) cast = string.Join(", ", people.Skip(1).Take(8)); }
                    // 从abstract中提取英文名 (非类型/国家/片长的部分)
                    var engTitle = "";
                    foreach (var part in abs.Split(" / ").Select(p => p.Trim()))
                    {
                        if (part == country || part.EndsWith("分钟") || genres.Contains(part)) continue;
                        if (int.TryParse(part, out _)) continue;
                        if (part.Any(c => c >= 0x4e00 && c <= 0x9fff)) continue;
                        if (!string.IsNullOrEmpty(part) && part.Length > 1) { engTitle = part; break; }
                    }

                    results.Add(new MovieSearchResult { Title = title, OriginalTitle = engTitle, Year = year, Rating = rating, Country = country, Director = director, Cast = cast, Runtime = runtime > 0 ? runtime : null, PosterUrl = cover, ExternalId = id, Source = "douban" });
                }
            }
            catch { }
        }
        if (results.Count == 0)
        {
            foreach (Match m in Regex.Matches(html, @"href=""https://movie\.douban\.com/subject/(\d+)/""[^>]*>\s*<img[^>]*alt=""([^""]+)"")"))
            { var id = m.Groups[1].Value; var t = WebUtility.HtmlDecode(m.Groups[2].Value).Trim(); if (!results.Any(x => x.ExternalId == id)) results.Add(new MovieSearchResult { Title = t, ExternalId = id, Source = "douban" }); }
        }
        return results;
    }

    private static MovieSearchResult ParseDetail(string html, string id)
    {
        var r = new MovieSearchResult { ExternalId = id, Source = "douban" };
        var tm = Regex.Match(html, @"<span property=""v:itemreviewed"">([^<]+)</span>"); if (tm.Success) r.Title = WebUtility.HtmlDecode(tm.Groups[1].Value).Trim();
        var ym = Regex.Match(html, @"<span class=""year"">\((\d{4})\)</span>"); if (ym.Success) r.Year = int.Parse(ym.Groups[1].Value);
        var dm = Regex.Match(html, @"rel=""v:directedBy""[^>]*>([^<]+)</a>"); if (dm.Success) { var dir = dm.Groups[1].Value.Trim(); if (!IsTemplateOrLabel(dir)) r.Director = dir; }
        var cl = Regex.Matches(html, @"rel=""v:starring""[^>]*>([^<]+)</a>").Select(c => c.Groups[1].Value.Trim()).Where(c => !IsTemplateOrLabel(c)).Take(8).ToList(); if (cl.Any()) r.Cast = string.Join(", ", cl);
        var sm = Regex.Match(html, @"<span property=""v:summary""[^>]*>\s*([\s\S]*?)\s*</span>"); if (sm.Success) r.Synopsis = WebUtility.HtmlDecode(Regex.Replace(sm.Groups[1].Value, @"<[^>]+>", "").Trim());
        var rm = Regex.Match(html, @"<strong class=""ll rating_num""[^>]*>([\d.]+)</strong>"); if (rm.Success) r.Rating = double.Parse(rm.Groups[1].Value);
        var rt = Regex.Match(html, @"<span property=""v:runtime""[^>]*>(\d+)"); if (rt.Success) r.Runtime = int.Parse(rt.Groups[1].Value);

        // 海报：优先匹配主海报区域的大图
        var pm = Regex.Match(html, @"<img[^>]*rel=""v:image""[^>]*src=""([^""]+)""");
        if (!pm.Success) pm = Regex.Match(html, @"<img[^>]*src=""(https://img\d+\.doubanio\.com/view/photo/[^""]+)""");
        if (!pm.Success) pm = Regex.Match(html, @"<img[^>]*src=""(https?://[^""]+(?:photo|poster|cover)[^""]*\.(?:jpg|png|webp))""");
        if (pm.Success)
        {
            var posterUrl = pm.Groups[1].Value;
            posterUrl = posterUrl.Replace("/m/", "/l/").Replace("/s/", "/l/");
            r.PosterUrl = posterUrl;
        }

        // 国家/地区：从 info 区域提取
        var countryM = Regex.Match(html, @"<span class=""pl"">制片国家/地区:</span>([^<]+)");
        if (!countryM.Success) countryM = Regex.Match(html, @"制片国家/地区[：:]\s*([^<\n]+)");
        if (countryM.Success) r.Country = WebUtility.HtmlDecode(countryM.Groups[1].Value).Trim();

        // 语言
        var langM = Regex.Match(html, @"<span class=""pl"">语言:</span>([^<]+)");
        if (langM.Success) r.Language = WebUtility.HtmlDecode(langM.Groups[1].Value).Trim();

        return r;
    }
}
