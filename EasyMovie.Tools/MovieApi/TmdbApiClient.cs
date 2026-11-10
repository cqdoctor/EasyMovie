﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using EasyMovie.Core.Helpers;
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
        // 注入的 HttpClient 归调用方所有，本类只读不写。
        // 旧实现**无论是否注入 http** 都会对 _http 调 DefaultRequestHeaders.Add —— 同一个 client
        // 被复用（或被注入到第二个实例）时，第二次 Add 会抛「重复头」异常。现在头部只在自建 client
        // 时设置一次。共享的原因见 HttpClientFactory.GetOrCreate；本类构造读取 HttpProxy，故须进 key。
        _http = http ?? EasyMovie.Core.HttpClientFactory.GetOrCreate(
            "tmdb|" + (EasyMovie.Core.AppSettings.HttpProxy ?? ""),
            () => CreateClient(EasyMovie.Core.AppSettings.HttpProxy ?? ""));
    }

    /// <summary>
    /// 网页抓取路径开关（默认关闭）：www.themoviedb.org 自 2026-11-06 起全站返回 Cloudflare 403，
    /// 保留方法体便于将来恢复，但正常流程不再调用（只走官方 API）。
    /// </summary>
    private static readonly bool EnableWebScrapingFallback = false;

    /// <summary>自建 HttpClient（含 UA / Accept-Language）。仅由共享缓存调用一次。</summary>
    private static HttpClient CreateClient(string proxy)
    {
        HttpMessageHandler handler;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            // 配了代理就交给代理做 DNS/连接；SocketsHttpHandler.ConnectCallback 与 Proxy 互斥，不能同时使用。
            var h = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            try
            {
                // 确保 URL 有 scheme（WebProxy 需要 http:// 前缀）
                if (!proxy.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !proxy.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    proxy = "http://" + proxy;
                h.Proxy = new WebProxy(proxy, true);
                h.UseProxy = true;
            }
            catch (Exception ex) { Log.Error(ex, "配置代理失败"); }
            handler = h;
        }
        else
        {
            // 系统 DNS 对 api.themoviedb.org 返回污染 IP（实测全部 TCP 超时），
            // 因此改用 www.themoviedb.org 的 A 记录建连，TLS 仍校验真实域名（见 ConnectViaIpPoolAsync）。
            handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(8),
                ConnectCallback = ConnectViaIpPoolAsync
            };
        }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }

    // ===== DNS 安全直连（IP 池 + 真实域名 TLS）=====
    private static readonly TimeSpan IpConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IpPoolTtl = TimeSpan.FromMinutes(10);
    private static readonly object IpPoolLock = new();
    private static string[] _ipPool = Array.Empty<string>();
    private static DateTime _ipPoolExpiresUtc = DateTime.MinValue;
    private static int _transportFailures;

    /// <summary>
    /// 逐 IP 建 TCP，再用真实主机名做 TLS 握手。
    /// 关键点：SNI 与证书校验都用 <paramref name="context"/> 里的真实域名（api.themoviedb.org），
    /// 绝不能传 IP；HttpClient 仍使用原 URI，Host 头也保持不变。
    /// </summary>
    private static async ValueTask<Stream> ConnectViaIpPoolAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var pool = await GetIpPoolAsync(ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var ip in pool)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(IpConnectTimeout);   // 单 IP 2s 超时，逐个回退
                await socket.ConnectAsync(IPAddress.Parse(ip), port, cts.Token).ConfigureAwait(false);

                var ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct).ConfigureAwait(false);
                return ssl;
            }
            catch (Exception ex)
            {
                last = ex;
                socket?.Dispose();
                // 建连/握手失败：立即失效缓存，下次请求重新解析（应对 CloudFront IP 漂移）
                InvalidateIpPool();
                if (ct.IsCancellationRequested) break;
            }
        }
        throw new HttpRequestException($"TMDB 直连失败（IP 池 {pool.Length} 个，主机 {host}）", last);
    }

    private static async Task<string[]> GetIpPoolAsync(CancellationToken ct)
    {
        lock (IpPoolLock)
        {
            if (_ipPool.Length > 0 && DateTime.UtcNow < _ipPoolExpiresUtc) return _ipPool;
        }
        // 只用 www.themoviedb.org 的 A 记录：api 域名被污染，但两者同属 CloudFront，边缘 IP 可复用。
        var addresses = await Dns.GetHostAddressesAsync("www.themoviedb.org", ct).ConfigureAwait(false);
        var pool = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                            .Select(a => a.ToString())
                            .Distinct()
                            .ToArray();
        if (pool.Length == 0) throw new HttpRequestException("TMDB 直连失败：www.themoviedb.org 无可用 A 记录");
        lock (IpPoolLock)
        {
            _ipPool = pool;
            _ipPoolExpiresUtc = DateTime.UtcNow.Add(IpPoolTtl);
        }
        return pool;
    }

    private static void InvalidateIpPool()
    {
        lock (IpPoolLock)
        {
            _ipPool = Array.Empty<string>();
            _ipPoolExpiresUtc = DateTime.MinValue;
        }
    }

    /// <summary>连续 3 次 5xx/超时：立即清空 IP 池，让下一次请求重新解析。</summary>
    private static void NoteTransportFailure()
    {
        if (Interlocked.Increment(ref _transportFailures) < 3) return;
        Interlocked.Exchange(ref _transportFailures, 0);
        InvalidateIpPool();
    }

    private static void NoteTransportSuccess() => Interlocked.Exchange(ref _transportFailures, 0);

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

        // 清洗文件名标题为可搜索关键词：优先纯中文（含续数字），无中文则用去标签的英文名。
        // 直接拿"花月杀手 Killers Of The Flower Moon EAC3 Atmos"整串搜索会导致 TMDB 匹配失败。
        var keyword = DoubanApiClient.ExtractChineseKeyword(request.Keyword);
        if (string.IsNullOrWhiteSpace(keyword))
            keyword = DoubanApiClient.ExtractEnglishHint(request.Keyword) ?? request.Keyword.Trim();

        // 只要配置了 API Key 就走官方 API：直连由 ConnectViaIpPoolAsync 解决 DNS 污染，
        // 配了代理则由代理建连（两条路径互斥），不再要求「未配置代理才走 API」。
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            var apiResult = await SearchViaApiAsync(keyword, request.PageSize, ct);
            if (apiResult != null) return apiResult;
        }

        // 网页抓取路径默认关闭（Cloudflare 403）：方法体保留，仅不再调用。
        if (!EnableWebScrapingFallback) return new MovieSearchResponse();

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

        // 只要配置了 API Key 就走官方 API（直连/代理由 CreateClient 决定），不再要求配置代理。
        if (!string.IsNullOrWhiteSpace(_apiKey) && int.TryParse(externalId, out var movieId))
        {
            var apiResult = await FetchDetailFromApiAsync(movieId, ct);
            if (apiResult != null) return apiResult;
        }

        // 网页抓取路径默认关闭（Cloudflare 403）：方法体保留，仅不再调用。
        if (!EnableWebScrapingFallback) return null;

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
                            var name = TextCleaner.StripHtml(WebUtility.HtmlDecode(nameM.Groups[1].Value.Trim()));
                            if (!IsTemplateOrLabel(name) && !dirNames.Contains(name))
                                dirNames.Add(name);
                        }
                    }
                }
                if (dirNames.Count > 0) director = string.Join(" / ", dirNames);
            }

            // 最后清理：去掉 HTML 标签、职业说明、非导演人员
            director = MovieCreditCleaner.CleanDirector(director);

            // 演员：优先新版结构 <ol class="people scroller"><li class="card">...<p><a href="/person/x">名字</a></p></li>
            var castList = new List<string>();
            var scrollerM = Regex.Match(html, @"<ol class=""people scroller"">(.*?)</ol>", RegexOptions.Singleline);
            if (scrollerM.Success)
            {
                foreach (Match cm in Regex.Matches(scrollerM.Groups[1].Value, @"<p><a href=""/person/[^""]*""[^>]*>(.*?)</a></p>", RegexOptions.Singleline))
                {
                    var name = TextCleaner.StripHtml(WebUtility.HtmlDecode(cm.Groups[1].Value.Trim()));
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
                    var name = TextCleaner.StripHtml(WebUtility.HtmlDecode(cm.Groups[1].Value.Trim()));
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

            // 类型：详情页 genres 以 <a href="/genre/...">链接呈现，锚文本即类型名。
            // API 被墙时这条 HTML 路径是 MovieTags 的唯一来源，必须解析。
            List<string>? genres = null;
            var genreMatches = Regex.Matches(html, @"<a[^>]*href=""/genre/[^""]*""[^>]*>([^<]{1,20})</a>");
            if (genreMatches.Count > 0)
            {
                var gs = genreMatches.Cast<Match>()
                    .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value.Trim()))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .ToList();
                if (gs.Count > 0) genres = gs;
            }

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
                Source = "tmdb",
                Genres = genres
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
                NoteTransportSuccess();
                return response;
        }
        catch (Exception ex) { Log.Error(ex, "TMDB 官方 API 搜索失败"); NoteTransportFailure(); return null; }
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
            // 优先 origin_country（ISO-3166-1 alpha-2）：production_countries 在 zh-CN 下仍是英文名。
            if (root.TryGetProperty("origin_country", out var ocProp) && ocProp.ValueKind == JsonValueKind.Array && ocProp.GetArrayLength() > 0)
            {
                var code = ocProp[0].ValueKind == JsonValueKind.String ? ocProp[0].GetString() ?? "" : "";
                country = CountryNameHelper.FromIso(code);
            }
            // 缺失回退：production_countries[].name
            if (string.IsNullOrEmpty(country) && root.TryGetProperty("production_countries", out var pcProp) && pcProp.ValueKind == JsonValueKind.Array && pcProp.GetArrayLength() > 0)
            {
                var first = pcProp[0];
                if (first.TryGetProperty("name", out var cnProp) && cnProp.ValueKind == JsonValueKind.String)
                    country = cnProp.GetString() ?? "";
            }
            // 沿用既有归一规则（拆分多国、取第一个合法国别），不在这里另造分类逻辑。
            country = CountryNameHelper.FirstValid(country) ?? country;

            // 类型（genres）：url 带 language=zh-CN，故 name 直接是中文标签。
            // 这是 MovieTags 关联的主要来源之一（豆瓣不可达时的替代路径）。
            List<string>? genres = null;
            if (root.TryGetProperty("genres", out var gProp) && gProp.ValueKind == JsonValueKind.Array)
            {
                var gs = gProp.EnumerateArray()
                    .Select(g => g.TryGetProperty("name", out var gn) && gn.ValueKind == JsonValueKind.String
                        ? gn.GetString() ?? "" : "")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .ToList();
                if (gs.Count > 0) genres = gs;
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

            NoteTransportSuccess();
            var originalLanguage = root.TryGetProperty("original_language", out var olProp) && olProp.ValueKind == JsonValueKind.String
                ? olProp.GetString() : null;
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
                Source = "tmdb",
                OriginalLanguage = originalLanguage,
                Genres = genres
            };
        }
        catch (Exception ex) { Log.Error(ex, "TMDB 官方 API 获取详情失败"); NoteTransportFailure(); return null; }
    }

    /// <summary>
    /// 拉取 TMDB 别名（alternative_titles）。仅在首轮无合格候选时调用，用于别名兜底；
    /// 失败返回空列表，不影响主流程。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAlternativeTitlesAsync(string externalId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || !int.TryParse(externalId, out var movieId))
            return Array.Empty<string>();
        try
        {
            var url = $"https://api.themoviedb.org/3/movie/{movieId}/alternative_titles?api_key={Uri.EscapeDataString(_apiKey)}";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            NoteTransportSuccess();
            if (!root.TryGetProperty("titles", out var titlesProp) || titlesProp.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return titlesProp.EnumerateArray()
                .Select(t => t.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() ?? "" : "")
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) { Log.Error(ex, "TMDB 别名获取失败"); NoteTransportFailure(); return Array.Empty<string>(); }
    }
}
