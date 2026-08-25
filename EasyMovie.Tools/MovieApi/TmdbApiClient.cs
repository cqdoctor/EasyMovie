﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using EasyMovie.Core.Interfaces;
using Serilog;

namespace EasyMovie.Tools.MovieApi;

public class TmdbApiClient : IMovieApiClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private const string ImageBaseUrl = "https://media.themoviedb.org/t/p/w500";

    public TmdbApiClient(string apiKey = "", HttpClient? http = null)
    {
        _apiKey = apiKey ?? "";
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        // 应用代理设置
        var proxy = EasyMovie.Core.AppSettings.HttpProxy;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try
            {
                // 确保 URL 有 scheme（WebProxy 需要 http:// 前缀）
                if (!proxy.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !proxy.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    proxy = "http://" + proxy;
                handler.Proxy = new WebProxy(proxy, true);
                handler.UseProxy = true;
            }
            catch (Exception ex) { Log.Error(ex, "配置代理失败"); }
        }
        _http = http ?? new HttpClient(handler)
        { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    private static readonly string[] InvalidLabels = { "人员", "人物", "演员", "主演", "导演", "暂无", "未知", "暂未录入", "更多" };

    private static readonly HashSet<string> DirectorBlacklistTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenplay", "story", "characters", "writer", "novel", "based on", "book",
        "director of photography", "editor", "producer", "executive producer",
        "music", "composer", "sound", "visual effects",
        // 中文职业标签
        "制片人", "制片", "编剧", "原著", "摄影", "剪辑", "音乐", "视觉效果", "艺术指导", "服装设计"
    };

    private static bool IsTemplateOrLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (Regex.IsMatch(value, @"\$\{.*?\}|\$\(data\.\w+\)|\{\{.*?\}\}|<%.*?%>")) return true;
        if (InvalidLabels.Contains(value)) return true;
        return false;
    }

    private static string StripHtml(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return Regex.Replace(value, @"<[^>]+>", "").Trim();
    }

    /// <summary>
    /// 清理导演字段：去掉 HTML 标签、职业说明、非导演人员、日期，只保留人名。
    /// </summary>
    private static string CleanDirector(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        value = StripHtml(value);

        // 按常见分隔符分行/分段
        var parts = value.Split(new[] { '/', '\\', '|', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        // 过滤掉包含职业标签或日期格式的段落
        var names = parts.Where(p =>
            !DirectorBlacklistTerms.Any(b => p.Contains(b, StringComparison.OrdinalIgnoreCase)) &&
            !Regex.IsMatch(p, @"^\d{4}-\d{2}-\d{2}$") &&  // 日期如 1963-02-17
            !Regex.IsMatch(p, @"^\d{4}$") &&                // 纯年份
            p.Length >= 2 && p.Length <= 30                  // 人名长度合理范围
        ).ToList();

        // 如果段落被职业标签污染，尝试取标签前的人名
        if (names.Count == 0)
        {
            foreach (var part in parts)
            {
                var firstBlackIdx = DirectorBlacklistTerms
                    .Select(b => part.IndexOf(b, StringComparison.OrdinalIgnoreCase))
                    .Where(i => i >= 0)
                    .DefaultIfEmpty(-1)
                    .Min();
                if (firstBlackIdx > 0)
                {
                    var name = part.Substring(0, firstBlackIdx).Trim();
                    if (!string.IsNullOrWhiteSpace(name) && name.Length <= 30 && !Regex.IsMatch(name, @"^\d{4}")) names.Add(name);
                }
            }
        }

        // 合并前 3 个导演，用 / 分隔
        var result = string.Join(" / ", names.Take(3));
        return result;
    }

    public string SourceName => "tmdb";

    public async Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Keyword))
            return new MovieSearchResponse();

        // 清洗文件名标题为可搜索关键词：优先纯中文（含续数字），无中文则用去标签的英文名。
        // 直接拿"花月杀手 Killers Of The Flower Moon EAC3 Atmos"整串搜索会导致 TMDB 匹配失败。
        var keyword = DoubanApiClient.ExtractChineseKeyword(request.Keyword);
        if (string.IsNullOrWhiteSpace(keyword))
            keyword = DoubanApiClient.ExtractEnglishHint(request.Keyword) ?? request.Keyword.Trim();

        // 配置了 API Key 且有代理时优先使用官方 API（API 在国内被墙，需代理）
        var proxy = EasyMovie.Core.AppSettings.HttpProxy;
        if (!string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(proxy))
        {
            var apiResult = await SearchViaApiAsync(keyword, request.PageSize, ct);
            if (apiResult != null) return apiResult;
            // API 失败时回退到网站爬取
        }

        // 网站爬取（www.themoviedb.org 在国内可访问）
        try
        {
            var encoded = Uri.EscapeDataString(keyword);
            var url = $"https://www.themoviedb.org/search?query={encoded}";
            var html = await _http.GetStringAsync(url, ct);
            var results = ParseSearchNew(html);
            return new MovieSearchResponse { Results = results.Take(request.PageSize).ToList(), TotalCount = results.Count, Page = request.Page, PageSize = request.PageSize };
        }
        catch (Exception ex) { Log.Error(ex, "TMDB 网页搜索解析失败"); return new MovieSearchResponse(); }
    }

    /// <summary>解析新版 TMDB 搜索页面（comp:media-card 结构）</summary>
    private static List<MovieSearchResult> ParseSearchNew(string html)
    {
        var results = new List<MovieSearchResult>();
        var seen = new HashSet<string>();

        // 匹配 comp:media-card 卡片块
        var cardMatches = Regex.Matches(html, @"class=""comp:media-card[^""]*""[^>]*>(.*?)</div>\s*</div>\s*</div>\s*</div>", RegexOptions.Singleline);

        foreach (Match cm in cardMatches)
        {
            var block = cm.Groups[1].Value;

            // 电影 ID
            var idM = Regex.Match(block, @"href=""/movie/(\d+)[^""]*""");
            if (!idM.Success) continue;
            var id = idM.Groups[1].Value;
            if (!seen.Add(id)) continue;

            // 标题：<h2><span>中文标题</span><span class="font-light"> (英文标题)</span></h2>
            var titleM = Regex.Match(block, @"<h2[^>]*>\s*<span>([^<]+)</span>(?:\s*<span[^>]*>\s*\(([^)]+)\)</span>)?</h2>");
            var title = titleM.Success ? WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim()) : "";
            var originalTitle = titleM.Success && titleM.Groups[2].Success ? WebUtility.HtmlDecode(titleM.Groups[2].Value.Trim()) : "";

            // 年份：<span class="release_date...">2023 年 08 月 30 日</span>
            var yearM = Regex.Match(block, @"class=""release_date[^""]*""[^>]*>\s*(\d{4})");
            var year = yearM.Success ? int.Parse(yearM.Groups[1].Value) : 0;

            // 海报：<img src="https://media.themoviedb.org/t/p/w94_and_h141_face/xxx.jpg" />
            var imgM = Regex.Match(block, @"<img[^>]*src=""(https://media\.themoviedb\.org/t/p/[^""]+)""");
            string? posterUrl = imgM.Success ? imgM.Groups[1].Value.Replace("/w94_and_h141_face/", "/w500/") : null;

            // 简介：<p>...</p>
            var overviewM = Regex.Match(block, @"<p[^>]*>(.*?)</p>", RegexOptions.Singleline);
            var overview = overviewM.Success ? WebUtility.HtmlDecode(overviewM.Groups[1].Value.Trim()) : null;

            results.Add(new MovieSearchResult
            {
                Title = title,
                OriginalTitle = originalTitle,
                Year = year,
                Synopsis = overview,
                PosterUrl = posterUrl,
                ExternalId = id,
                Source = "tmdb"
            });
        }

        // 兜底：如果卡片匹配失败，只提取 movie 链接（不获取详情，由调用方按需获取）
        if (results.Count == 0)
        {
            var linkMatches = Regex.Matches(html, @"href=""/movie/(\d+)[^""]*""");
            foreach (Match lm in linkMatches)
            {
                var id = lm.Groups[1].Value;
                if (!seen.Add(id)) continue;
                results.Add(new MovieSearchResult { Title = "", ExternalId = id, Source = "tmdb" });
                if (results.Count >= 10) break;
            }
        }

        return results;
    }

    public async Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        // 配置了 API Key 且有代理时优先使用官方 API（API 在国内被墙，需代理）
        var proxy = EasyMovie.Core.AppSettings.HttpProxy;
        if (!string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(proxy) && int.TryParse(externalId, out var movieId))
        {
            var apiResult = await FetchDetailFromApiAsync(movieId, ct);
            if (apiResult != null) return apiResult;
            // API 失败时回退到网站爬取
        }

        // 网站爬取（www.themoviedb.org 在国内可访问）
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

            // 优先从 JSON-LD 提取导演（最可靠）
            // 注意：不能用 \{.*?\} 因为 JSON 有嵌套大括号，会截断。改为匹配整个 script 标签内容
            var jsonLdM = Regex.Match(html, @"<script\s+type=""application/ld\+json"">\s*(.*?)\s*</script>", RegexOptions.Singleline);
            if (jsonLdM.Success)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(jsonLdM.Groups[1].Value);
                    if (doc.RootElement.TryGetProperty("director", out var dirs))
                    {
                        var dirNames = new List<string>();
                        if (dirs.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var d in dirs.EnumerateArray())
                                if (d.TryGetProperty("name", out var n)) dirNames.Add(n.GetString() ?? "");
                        }
                        else if (dirs.TryGetProperty("name", out var n)) dirNames.Add(n.GetString() ?? "");
                        if (dirNames.Count > 0) director = string.Join("/", dirNames);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "解析 JSON-LD 导演失败");
                }
            }

            // HTML fallback: 严格匹配 crew 列表中 character 为 Director 的 li.profile 块。
            // 新版页面 character 可能为 "Director, Screenplay"（带职业后缀），用 Director[^<]* 匹配开头。
            if (string.IsNullOrEmpty(director))
            {
                var dirNames = new List<string>();
                foreach (Match profile in Regex.Matches(html, @"<li[^>]*class=""profile""[^>]*>(.*?)</li>", RegexOptions.Singleline))
                {
                    var profileHtml = profile.Groups[1].Value;
                    if (Regex.IsMatch(profileHtml, @"class=""character""[^>]*>\s*Director[^<]*</p>", RegexOptions.Singleline))
                    {
                        var nameM = Regex.Match(profileHtml, @"<a[^>]*href=""/person/[^""]*""[^>]*>(.*?)</a>", RegexOptions.Singleline);
                        if (nameM.Success)
                        {
                            var name = StripHtml(WebUtility.HtmlDecode(nameM.Groups[1].Value.Trim()));
                            if (!IsTemplateOrLabel(name) && !dirNames.Contains(name))
                                dirNames.Add(name);
                        }
                    }
                }
                if (dirNames.Count > 0) director = string.Join(" / ", dirNames);
            }

            // 最后清理：去掉 HTML 标签、职业说明、非导演人员
            director = CleanDirector(director);

            // 演员：优先新版结构 <ol class="people scroller"><li class="card">...<p><a href="/person/x">名字</a></p></li>
            var castList = new List<string>();
            var scrollerM = Regex.Match(html, @"<ol class=""people scroller"">(.*?)</ol>", RegexOptions.Singleline);
            if (scrollerM.Success)
            {
                foreach (Match cm in Regex.Matches(scrollerM.Groups[1].Value, @"<p><a href=""/person/[^""]*""[^>]*>(.*?)</a></p>", RegexOptions.Singleline))
                {
                    var name = StripHtml(WebUtility.HtmlDecode(cm.Groups[1].Value.Trim()));
                    if (!IsTemplateOrLabel(name)) castList.Add(name);
                    if (castList.Count >= 8) break;
                }
            }

            // 演员兜底：旧版 class="name" 结构
            if (castList.Count == 0)
            {
                var castMatches = Regex.Matches(html, @"class=""name""[^>]*>\s*<a[^>]*>(.*?)</a>");
                foreach (Match cm in castMatches.Take(5))
                {
                    var name = StripHtml(WebUtility.HtmlDecode(cm.Groups[1].Value.Trim()));
                    if (!IsTemplateOrLabel(name)) castList.Add(name);
                }
            }

            var country = "";
            // 复用上面已解析的 JSON-LD 提取国家
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
                catch (Exception ex)
                {
                    Log.Error(ex, "解析 JSON-LD 国家失败");
                }
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
            var synM = Regex.Match(html, @"class=""overview""[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
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
                catch (Exception ex)
                {
                    Log.Error(ex, "解析 JSON-LD 海报失败");
                }
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

            double? rating = null;
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
                Rating = rating > 0 ? rating : null,
                ExternalId = externalId,
                Source = "tmdb"
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 使用 TMDB 官方 API 搜索电影（需 API Key）。
    /// </summary>
    private async Task<MovieSearchResponse?> SearchViaApiAsync(string keyword, int pageSize, CancellationToken ct)
    {
        try
        {
            var encoded = Uri.EscapeDataString(keyword);
            var url = $"https://api.themoviedb.org/3/search/movie?api_key={Uri.EscapeDataString(_apiKey)}&query={encoded}&language=zh-CN&page=1";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var response = new MovieSearchResponse();
            if (root.TryGetProperty("total_results", out var trProp) && trProp.ValueKind == JsonValueKind.Number)
                response.TotalCount = trProp.GetInt32();

            if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resultsProp.EnumerateArray().Take(pageSize > 0 ? pageSize : 5))
                {
                    var id = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32().ToString() : "";
                    if (string.IsNullOrEmpty(id)) continue;

                    var title = item.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String ? tProp.GetString() ?? "" : "";
                    var origTitle = item.TryGetProperty("original_title", out var otProp) && otProp.ValueKind == JsonValueKind.String ? otProp.GetString() ?? "" : "";
                    var overview = item.TryGetProperty("overview", out var oProp) && oProp.ValueKind == JsonValueKind.String ? oProp.GetString() : null;
                    var year = 0;
                    if (item.TryGetProperty("release_date", out var rdProp) && rdProp.ValueKind == JsonValueKind.String)
                    {
                        var rd = rdProp.GetString() ?? "";
                        if (rd.Length >= 4 && int.TryParse(rd.Substring(0, 4), out var y)) year = y;
                    }
                    var rating = item.TryGetProperty("vote_average", out var vProp) && vProp.ValueKind == JsonValueKind.Number ? vProp.GetDouble() : (double?)null;
                    string? posterUrl = null;
                    if (item.TryGetProperty("poster_path", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                    {
                        var pp = pProp.GetString();
                        if (!string.IsNullOrEmpty(pp)) posterUrl = $"{ImageBaseUrl}{pp}";
                    }

                    response.Results.Add(new MovieSearchResult
                    {
                        Title = title,
                        OriginalTitle = origTitle != title ? origTitle : null,
                        Year = year,
                        Synopsis = overview,
                        PosterUrl = posterUrl,
                        Rating = rating,
                        ExternalId = id,
                        Source = "tmdb"
                    });
                }
            }
                return response;
        }
        catch (Exception ex) { Log.Error(ex, "TMDB 官方 API 搜索失败"); return null; }
    }

    /// <summary>
    /// 使用 TMDB 官方 API 获取详情（需 API Key），包含完整导演/演员/国家信息。
    /// </summary>
    private async Task<MovieSearchResult?> FetchDetailFromApiAsync(int movieId, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.themoviedb.org/3/movie/{movieId}?api_key={Uri.EscapeDataString(_apiKey)}&language=zh-CN&append_to_response=credits";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String ? tProp.GetString() ?? "" : "";
            var originalTitle = root.TryGetProperty("original_title", out var otProp) && otProp.ValueKind == JsonValueKind.String ? otProp.GetString() ?? "" : "";
            var overview = root.TryGetProperty("overview", out var oProp) && oProp.ValueKind == JsonValueKind.String ? oProp.GetString() : null;

            var year = 0;
            if (root.TryGetProperty("release_date", out var rdProp) && rdProp.ValueKind == JsonValueKind.String)
            {
                var rd = rdProp.GetString() ?? "";
                if (rd.Length >= 4 && int.TryParse(rd.Substring(0, 4), out var y)) year = y;
            }

            var runtime = root.TryGetProperty("runtime", out var rtProp) && rtProp.ValueKind == JsonValueKind.Number ? rtProp.GetInt32() : (int?)null;
            var rating = root.TryGetProperty("vote_average", out var vProp) && vProp.ValueKind == JsonValueKind.Number ? vProp.GetDouble() : (double?)null;

            string? posterUrl = null;
            if (root.TryGetProperty("poster_path", out var pProp) && pProp.ValueKind == JsonValueKind.String)
            {
                var pp = pProp.GetString();
                if (!string.IsNullOrEmpty(pp)) posterUrl = $"{ImageBaseUrl}{pp}";
            }

            var country = "";
            if (root.TryGetProperty("production_countries", out var pcProp) && pcProp.ValueKind == JsonValueKind.Array && pcProp.GetArrayLength() > 0)
            {
                var first = pcProp[0];
                if (first.TryGetProperty("name", out var cnProp) && cnProp.ValueKind == JsonValueKind.String)
                    country = cnProp.GetString() ?? "";
            }
            // 兜底：用 origin_country 国家代码映射为中文名
            if (string.IsNullOrEmpty(country) && root.TryGetProperty("origin_country", out var ocProp) && ocProp.ValueKind == JsonValueKind.Array && ocProp.GetArrayLength() > 0)
            {
                var code = ocProp[0].GetString() ?? "";
                country = code switch
                {
                    "US" => "美国", "CN" => "中国", "HK" => "中国香港", "TW" => "中国台湾",
                    "JP" => "日本", "KR" => "韩国", "GB" => "英国", "FR" => "法国",
                    "DE" => "德国", "IT" => "意大利", "ES" => "西班牙", "CA" => "加拿大",
                    "AU" => "澳大利亚", "IN" => "印度", "RU" => "俄罗斯", "TH" => "泰国",
                    _ => ""
                };
            }

            var directors = new List<string>();
            var cast = new List<string>();
            if (root.TryGetProperty("credits", out var creditsProp) && creditsProp.ValueKind == JsonValueKind.Object)
            {
                if (creditsProp.TryGetProperty("crew", out var crewProp) && crewProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var crew in crewProp.EnumerateArray())
                    {
                        if (crew.TryGetProperty("job", out var jobProp) && jobProp.ValueKind == JsonValueKind.String &&
                            "Director".Equals(jobProp.GetString(), StringComparison.OrdinalIgnoreCase))
                        {
                            if (crew.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                            {
                                var name = nameProp.GetString();
                                if (!string.IsNullOrWhiteSpace(name) && !directors.Contains(name))
                                    directors.Add(name);
                            }
                        }
                    }
                }

                if (creditsProp.TryGetProperty("cast", out var castProp) && castProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var actor in castProp.EnumerateArray().Take(5))
                    {
                        if (actor.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                        {
                            var name = nameProp.GetString();
                            if (!string.IsNullOrWhiteSpace(name)) cast.Add(name);
                        }
                    }
                }
            }

            return new MovieSearchResult
            {
                Title = title,
                OriginalTitle = originalTitle != title ? originalTitle : null,
                Year = year,
                Director = directors.Count > 0 ? string.Join(" / ", directors.Take(3)) : null,
                Cast = cast.Count > 0 ? string.Join(", ", cast) : null,
                Country = country,
                Synopsis = overview,
                PosterUrl = posterUrl,
                Runtime = runtime,
                Rating = rating,
                ExternalId = movieId.ToString(),
                Source = "tmdb"
            };
        }
        catch (Exception ex) { Log.Error(ex, "TMDB 官方 API 获取详情失败"); return null; }
    }
}
