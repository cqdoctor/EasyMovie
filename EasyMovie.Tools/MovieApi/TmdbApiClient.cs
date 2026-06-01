using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using EasyMovie.Core.Interfaces;

namespace EasyMovie.Tools.MovieApi;

public class TmdbApiClient : IMovieApiClient
{
    private readonly HttpClient _http;
    private const string ImageBaseUrl = "https://media.themoviedb.org/t/p/w500";

    public TmdbApiClient(string apiKey = "", HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    private static readonly string[] InvalidLabels = { "人员", "人物", "演员", "主演", "导演", "暂无", "未知", "暂未录入", "更多" };

    private static bool IsTemplateOrLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (Regex.IsMatch(value, @"\$\{.*?\}|\$\(data\.\w+\)|\{\{.*?\}\}|<%.*?%>")) return true;
        if (InvalidLabels.Contains(value)) return true;
        return false;
    }

    public string SourceName => "tmdb";

    public async Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Keyword))
            return new MovieSearchResponse();

        try
        {
            var encoded = Uri.EscapeDataString(request.Keyword);
            var url = $"https://www.themoviedb.org/search?query={encoded}";
            var html = await _http.GetStringAsync(url, ct);

            var results = new List<MovieSearchResult>();
            var movieSection = Regex.Match(html, @"<section[^>]*class=""search""[^>]*>(.*?)</section>", RegexOptions.Singleline);
            var searchHtml = movieSection.Success ? movieSection.Groups[1].Value : html;

            var itemMatches = Regex.Matches(searchHtml, @"<div[^>]*class=""card\s+v2""[^>]*>(.*?)</div>\s*</div>\s*</div>", RegexOptions.Singleline);

            if (itemMatches.Count == 0)
            {
                var linkMatches = Regex.Matches(searchHtml, @"<a[^>]*href=""(/movie/(\d+)[^""]*)""[^>]*>");
                var seen = new HashSet<string>();
                foreach (Match lm in linkMatches)
                {
                    var id = lm.Groups[2].Value;
                    if (!seen.Add(id)) continue;
                    results.Add(new MovieSearchResult
                    {
                        Title = "",
                        ExternalId = id,
                        Source = "tmdb"
                    });
                    if (results.Count >= request.PageSize) break;
                }

                foreach (var r in results.Where(r => string.IsNullOrEmpty(r.Title)))
                {
                    try
                    {
                        var detail = await GetDetailAsync(r.ExternalId!, ct);
                        if (detail != null)
                        {
                            r.Title = detail.Title;
                            r.OriginalTitle = detail.OriginalTitle;
                            r.Year = detail.Year;
                            r.PosterUrl = detail.PosterUrl;
                            r.Synopsis = detail.Synopsis;
                            r.Rating = detail.Rating;
                        }
                    }
                    catch { }
                }
            }
            else
            {
                foreach (Match im in itemMatches)
                {
                    var block = im.Groups[1].Value;
                    var idM = Regex.Match(block, @"/movie/(\d+)");
                    if (!idM.Success) continue;

                    var titleM = Regex.Match(block, @"class=""title""[^>]*>\s*<a[^>]*>(.*?)</a>");
                    var title = titleM.Success ? WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim()) : "";

                    var imgM = Regex.Match(block, @"<img[^>]*src=""([^""]*)""");
                    string? posterUrl = null;
                    if (imgM.Success && !string.IsNullOrEmpty(imgM.Groups[1].Value))
                    {
                        var imgSrc = imgM.Groups[1].Value;
                        if (imgSrc.StartsWith("//")) posterUrl = $"https:{imgSrc}".Replace("/w94_and_h141_face/", "/w500/");
                        else if (imgSrc.StartsWith("http")) posterUrl = imgSrc.Replace("/w94_and_h141_face/", "/w500/");
                        else if (imgSrc.StartsWith("/")) posterUrl = $"https://media.themoviedb.org{imgSrc}".Replace("/w94_and_h141_face/", "/w500/");
                        // 防护：确保不会产生双重前缀
                        if (posterUrl != null && posterUrl.Contains("https:https://")) posterUrl = posterUrl.Replace("https:https://", "https://");
                        if (posterUrl != null && posterUrl.Contains("http:http://")) posterUrl = posterUrl.Replace("http:http://", "http://");
                    }

                    var yearM = Regex.Match(block, @"class=""release_date"">(.*?)</span>");
                    var year = 0;
                    if (yearM.Success)
                    {
                        var ys = Regex.Match(yearM.Groups[1].Value, @"\d{4}");
                        if (ys.Success) year = int.Parse(ys.Value);
                    }

                    var overviewM = Regex.Match(block, @"class=""overview""[^>]*>(.*?)</p>");
                    var overview = overviewM.Success ? WebUtility.HtmlDecode(overviewM.Groups[1].Value.Trim()) : null;

                    results.Add(new MovieSearchResult
                    {
                        Title = title,
                        Year = year,
                        Synopsis = overview,
                        PosterUrl = posterUrl,
                        ExternalId = idM.Groups[1].Value,
                        Source = "tmdb"
                    });
                    if (results.Count >= request.PageSize) break;
                }
            }

            return new MovieSearchResponse
            {
                Results = results,
                TotalCount = results.Count,
                Page = request.Page,
                PageSize = request.PageSize
            };
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

        try
        {
            var url = $"https://www.themoviedb.org/movie/{externalId}";
            var html = await _http.GetStringAsync(url, ct);

            var title = "";
            var titleM = Regex.Match(html, @"<h2[^>]*>\s*<a[^>]*>(.*?)</a>");
            if (titleM.Success) title = WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim());

            if (string.IsNullOrEmpty(title))
            {
                titleM = Regex.Match(html, @"<title>(.*?)</title>");
                if (titleM.Success)
                {
                    var raw = WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim());
                    title = raw.Split("—")[0].Split("—")[0].Split('|')[0].Trim();
                }
            }

            var origTitleM = Regex.Match(html, @"class=""tagline""[^>]*>(.*?)</h3>");
            if (!origTitleM.Success) origTitleM = Regex.Match(html, @"class=""original_title""[^>]*>(.*?)</span>");
            var origTitle = origTitleM.Success ? WebUtility.HtmlDecode(origTitleM.Groups[1].Value.Trim()) : null;

            var year = 0;
            var yearM = Regex.Match(html, @"class=""release""[^>]*>(.*?)</span>");
            if (yearM.Success) { var ym = Regex.Match(yearM.Groups[1].Value, @"\d{4}"); if (ym.Success) year = int.Parse(ym.Value); }
            if (year == 0) { var ym = Regex.Match(html, @"\((\d{4})\)"); if (ym.Success) year = int.Parse(ym.Groups[1].Value); }

            var director = "";
            var dirM = Regex.Match(html, @"<p><a[^>]*href=""[^""]*person[^""]*""[^>]*>(.*?)</a></p>\s*<p[^>]*class=""character""[^>]*>\s*Director\s*</p>", RegexOptions.Singleline);
            if (dirM.Success) { var dir = WebUtility.HtmlDecode(dirM.Groups[1].Value.Trim()); if (!IsTemplateOrLabel(dir)) director = dir; }
            if (string.IsNullOrEmpty(director))
            {
                var dirM2 = Regex.Match(html, @"<li[^>]*>\s*<a[^>]*href=""[^""]*person[^""]*""[^>]*>(.*?)</a>\s*.*?Director", RegexOptions.Singleline);
                if (dirM2.Success) { var dir = WebUtility.HtmlDecode(dirM2.Groups[1].Value.Trim()); if (!IsTemplateOrLabel(dir)) director = dir; }
            }

            var castList = new List<string>();
            var castMatches = Regex.Matches(html, @"class=""name""[^>]*>\s*<a[^>]*>(.*?)</a>");
            foreach (Match cm in castMatches.Take(5))
            {
                var name = WebUtility.HtmlDecode(cm.Groups[1].Value.Trim());
                if (!IsTemplateOrLabel(name)) castList.Add(name);
            }

            var country = "";
            // 优先从 JSON-LD 提取国家（TMDB 新版页面格式）
            var jsonLdM = Regex.Match(html, @"<script\s+type=""application/ld\+json"">\s*(\{.*?\})\s*</script>", RegexOptions.Singleline);
            if (jsonLdM.Success)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(jsonLdM.Groups[1].Value);
                    if (doc.RootElement.TryGetProperty("countryOfOrigin", out var co))
                    {
                        if (co.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var names = new List<string>();
                            foreach (var item in co.EnumerateArray())
                                if (item.TryGetProperty("name", out var n)) names.Add(n.GetString() ?? "");
                            if (names.Count > 0) country = string.Join("/", names);
                        }
                        else if (co.TryGetProperty("name", out var n)) country = n.GetString() ?? "";
                    }
                }
                catch { }
            }
            // 从日期行提取国家代码，如 "2023-08-26 (CN)" → "CN"
            if (string.IsNullOrEmpty(country))
            {
                var dateCountryM = Regex.Match(html, @"\d{4}-\d{2}-\d{2}\s*\((\w{2,3})\)");
                if (dateCountryM.Success)
                {
                    var code = dateCountryM.Groups[1].Value.ToUpper();
                    country = code switch
                    {
                        "CN" => "中国大陆",
                        "HK" => "中国香港",
                        "TW" => "中国台湾",
                        "JP" => "日本",
                        "KR" => "韩国",
                        "US" => "美国",
                        "GB" => "英国",
                        "FR" => "法国",
                        "DE" => "德国",
                        "IN" => "印度",
                        "TH" => "泰国",
                        "RU" => "俄罗斯",
                        "ES" => "西班牙",
                        "IT" => "意大利",
                        "CA" => "加拿大",
                        "AU" => "澳大利亚",
                        "BR" => "巴西",
                        "MX" => "墨西哥",
                        "PH" => "菲律宾",
                        "VN" => "越南",
                        "MY" => "马来西亚",
                        "SG" => "新加坡",
                        "ID" => "印度尼西亚",
                        "TR" => "土耳其",
                        "IR" => "伊朗",
                        "IL" => "以色列",
                        "DK" => "丹麦",
                        "SE" => "瑞典",
                        "NO" => "挪威",
                        "FI" => "芬兰",
                        "NL" => "荷兰",
                        "BE" => "比利时",
                        "PL" => "波兰",
                        "CZ" => "捷克",
                        "AT" => "奥地利",
                        "CH" => "瑞士",
                        "PT" => "葡萄牙",
                        "AR" => "阿根廷",
                        "NZ" => "新西兰",
                        "ZA" => "南非",
                        "EG" => "埃及",
                        "NG" => "尼日利亚",
                        "CO" => "哥伦比亚",
                        "CL" => "智利",
                        "PE" => "秘鲁",
                        "UA" => "乌克兰",
                        "HU" => "匈牙利",
                        "RO" => "罗马尼亚",
                        "IE" => "爱尔兰",
                        _ => code
                    };
                }
            }
            // 回退到旧版 HTML 格式
            if (string.IsNullOrEmpty(country))
            {
                var countryM = Regex.Match(html, @"制片国家[^<]*<[^>]*>([^<]+)");
                if (!countryM.Success) countryM = Regex.Match(html, @"Country[^<]*<[^>]*>([^<]+)");
                if (!countryM.Success) countryM = Regex.Match(html, @"class=""production""[^>]*>(.*?)</li>", RegexOptions.Singleline);
                if (countryM.Success) country = WebUtility.HtmlDecode(countryM.Groups[1].Value.Trim());
            }

            var synopsis = "";
            var synM = Regex.Match(html, @"class=""overview""[^>]*>(.*?)</p>", RegexOptions.Singleline);
            if (!synM.Success) synM = Regex.Match(html, @"class=""text""[^>]*>(.*?)</div>", RegexOptions.Singleline);
            if (synM.Success) synopsis = WebUtility.HtmlDecode(synM.Groups[1].Value.Trim());

            var posterUrl = "";
            // 优先从 JSON-LD 提取海报（最可靠）
            if (jsonLdM.Success)
            {
                try
                {
                    using var doc2 = System.Text.Json.JsonDocument.Parse(jsonLdM.Groups[1].Value);
                    if (doc2.RootElement.TryGetProperty("image", out var img))
                        posterUrl = img.GetString() ?? "";
                }
                catch { }
            }
            // 回退到 HTML img 标签
            if (string.IsNullOrEmpty(posterUrl))
            {
                var posterM = Regex.Match(html, @"<img[^>]*src=""(//media\.themoviedb\.org/t/p/[^""]*)""[^>]*");
                if (!posterM.Success) posterM = Regex.Match(html, @"<img[^>]*src=""(https://media\.themoviedb\.org/t/p/[^""]*)""[^>]*");
                if (!posterM.Success) posterM = Regex.Match(html, @"<img[^>]*src=""(https://image\.tmdb\.org/t/p/[^""]*)""[^>]*");
                if (posterM.Success)
                {
                    var raw = posterM.Groups[1].Value;
                    posterUrl = raw.StartsWith("//") ? $"https:{raw}" : raw;
                    posterUrl = posterUrl.Replace("/w300_and_h450_face/", "/w500/").Replace("/w94_and_h141_face/", "/w500/");
                    if (posterUrl.Contains("https:https://")) posterUrl = posterUrl.Replace("https:https://", "https://");
                    if (posterUrl.Contains("http:http://")) posterUrl = posterUrl.Replace("http:http://", "http://");
                }
            }

            var runtime = 0;
            var rtM = Regex.Match(html, @"(\d+)\s*h\s*(\d+)\s*m");
            if (rtM.Success) runtime = int.Parse(rtM.Groups[1].Value) * 60 + int.Parse(rtM.Groups[2].Value);
            else
            {
                var rtM2 = Regex.Match(html, @"(\d+)\s*m");
                if (rtM2.Success) runtime = int.Parse(rtM2.Groups[1].Value);
                else
                {
                    var rtM3 = Regex.Match(html, @"(\d+)\s*h(?!\s*\d)");
                    if (rtM3.Success) runtime = int.Parse(rtM3.Groups[1].Value) * 60;
                }
            }

            double rating = 0;
            var ratingM = Regex.Match(html, @"class=""user_score_chart""[^>]*data-percent=""([\d.]+)""");
            if (ratingM.Success) rating = double.Parse(ratingM.Groups[1].Value) / 10.0;

            return new MovieSearchResult
            {
                Title = title,
                OriginalTitle = origTitle != title ? origTitle : null,
                Year = year,
                Director = director,
                Cast = castList.Count > 0 ? string.Join(", ", castList) : null,
                Country = country,
                Synopsis = synopsis,
                PosterUrl = string.IsNullOrEmpty(posterUrl) ? null : posterUrl,
                Runtime = runtime > 0 ? runtime : null,
                Rating = rating,
                ExternalId = externalId,
                Source = "tmdb"
            };
        }
        catch
        {
            return null;
        }
    }
}
