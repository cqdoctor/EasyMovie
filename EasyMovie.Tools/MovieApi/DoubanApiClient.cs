﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using EasyMovie.Core;
using EasyMovie.Tools.ImportExport;
using EasyMovie.Core.Interfaces;
using Serilog;

namespace EasyMovie.Tools.MovieApi;

public class DoubanApiClient : IMovieApiClient
{
    private readonly HttpClient _http;
    private static DateTime _lastRequest = DateTime.MinValue;
    private static readonly object _lock = new();
    private const int MinIntervalMs = 1500;

    // 限流自我冷却：触发反爬限流后进入递增冷却期，期间不再发送任何请求（避免加重风控），
    // 冷却到期自动恢复；连续触发则冷却时长递增直至封顶，正常响应即重置信任。
    private static DateTime _cooldownUntil = DateTime.MinValue;
    private static int _rateLimitStrikes = 0;
    private static bool InCooldown => DateTime.UtcNow < _cooldownUntil;
    private static void TriggerCooldown()
    {
        _rateLimitStrikes++;
        var seconds = Math.Min(60 * _rateLimitStrikes, 600);
        _cooldownUntil = DateTime.UtcNow.AddSeconds(seconds);
    }
    private static void ResetCooldown()
    {
        _rateLimitStrikes = 0;
        _cooldownUntil = DateTime.MinValue;
    }

    public DoubanApiClient(HttpClient? http = null) { _http = http ?? CreateClient(); }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        // 若用户配置了全局代理，则让豆瓣也走代理（国内站直连通常更快，但配了代理即表示希望统一出口）
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
            catch (Exception ex) { Log.Error(ex, "配置代理失败"); }
        }
        var client = new HttpClient(handler);
        // rexxar 移动端接口（m.douban.com/rexxar/api/v2）：免 key、免签名，比网页端宽松，返回干净 JSON。
        // 需要移动端 UA + m.douban.com Referer + JSON Accept；登录态 Cookie 可显著提升额度、解除 need_login。
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 MicroMessenger/8.0 Douban/7.38.0");
        client.DefaultRequestHeaders.Add("Referer", "https://m.douban.com/");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.Timeout = TimeSpan.FromSeconds(12);
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

    /// <summary>豆瓣处于限流自我冷却期时返回 true，调用方据此跳过而不计入熔断失败。</summary>
    public bool IsThrottled() => InCooldown;

    /// <summary>最近一次真正发出豆瓣请求的 UTC 时间（供慢速补全服务计算节奏，只读）。</summary>
    public static DateTime LastRequestUtc => _lastRequest;

    /// <summary>
    /// 高置信封控/验证码信号。仅收录“几乎只出现在风控页”的标记，避免误伤正常结果页
    /// （正常页顶部导航含“登录”链接，但不会出现“登录豆瓣”页标题或“过于频繁”等字样）。
    /// 一旦命中即进入递增冷却，安静避让，绝不重试。
    /// </summary>
    private static readonly string[] BanSignals =
    {
        "禁止访问", "检测到有异常请求", "请输入验证码",
        "你当前访问过于频繁", "访问过于频繁", "安全验证", "安全校验",
        "登录豆瓣", "need_login", "accounts.douban.com/login"
    };

    private static bool ContainsBanSignal(string html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        foreach (var s in BanSignals)
            if (html.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // 常见字幕/音轨/版本标签，提取关键词时需移除
    private static readonly string[] NoiseLabels = {
        "字幕", "双语", "双字", "中字", "英字", "中日", "中俄", "中韩", "国粤", "粤韩",
        "国语", "粤语", "台配", "原声", "国配", "英语", "日语", "韩语",
        "限制级", "未删减", "导演剪辑", "加长版", "终极版", "修复版", "重映"
    };

    public static string ExtractChineseKeyword(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;

        // 先移除已知的噪声标签
        var cleaned = title;
        foreach (var label in NoiseLabels)
            cleaned = cleaned.Replace(label, "");

        // 提取所有中文段，并保留紧随中文的数字（如"惊奇队长2"不会丢掉"2"）
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        for (int i = 0; i < cleaned.Length; i++)
        {
            var c = cleaned[i];
            if (c >= 0x4e00 && c <= 0x9fff)
            {
                current.Append(c);
            }
            else if (current.Length > 0 && char.IsDigit(c))
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
            return string.Join("", parts);
        }
        // 无中文段（纯英文/数字片名）：用英文原名整体作为搜索词，不要只取首个单词，
        // 否则 “Beast Race” 会被截断成 “Beast”，导致豆瓣模糊匹配到其它名字含 Beast 的无关老片。
        return title.Trim();
    }

    /// <summary>提取文件名中的英文名用于验证 (去掉中文、年份、编码标记)</summary>
    public static string? ExtractEnglishHint(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var cleaned = Regex.Replace(title, @"[\u4e00-\u9fff]+\d*", " ");
        cleaned = Regex.Replace(cleaned, @"\d*[\u4e00-\u9fff]+", " ");
        // 复用统一的发布标签剥离（避免三处清单漂移）
        cleaned = FileNameParser.StripTags(cleaned);
        cleaned = Regex.Replace(cleaned, @"\b\d{4,}\b", " ");
        cleaned = Regex.Replace(cleaned, @"[.\-_]", " ");
        cleaned = Regex.Replace(cleaned, @"[^\w\s]", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }

        /// <summary>
        /// 从搜索结果中选出最佳匹配。匹配优先级：
        ///   1) 英文名（OriginalTitle）包含完整搜索词 engHint，或命中文件名全部英文词；
        ///   2) 标题/原名命中文件名全部英文词（中文译名场景）；
        ///   3) 年份一致（仅当电影本身有年份时）；
        ///   4) 以上都不满足时返回 null，交由上层换关键词或换数据源，
        ///      避免“无年份 + 模糊搜索第一条”把无关老片（如 1991 年）错误套用。
        /// </summary>
        public static MovieSearchResult? PickBestMatch(List<MovieSearchResult> results, string title, int? year)
        {
            if (results == null || results.Count == 0) return null;
            // 过滤掉数据源偶发返回的模板/占位符脏数据（如 TMDB 返回 "#= data.original_title #"）。
            // 注意：只按“结果自身的 Title 是否为模板/占位符”判定，绝不能因 OriginalTitle 为空而丢弃——
            // 中文片（尤其 2020+ 国产片）常无英文名，OriginalTitle 为空是常态，误删会导致 PickBestMatch
            // 对大量合法结果返回 null，进而令补全服务静默跳过全部影片。
            results = results.Where(r => !IsTemplateOrLabel(r.Title)).ToList();
            if (results.Count == 0) return null;

            // 0. 精确片名匹配优先：归一化完全相等的同名结果，优于带序号的续集
            //    （如“速度与激情”应优先于“速度与激情10”，“加勒比海盗”优于“加勒比海盗2”）
            var nt = Normalize(title);
            foreach (var r in results)
            {
                if (Normalize(r.Title) == nt || Normalize(r.OriginalTitle) == nt)
                    return r;
            }

            var eng = ExtractEnglishHint(title);
            var tokens = ExtractTitleTokens(title);

            // 1. 英文名整体匹配
            if (!string.IsNullOrEmpty(eng))
            {
                foreach (var r in results)
                {
                    if (!string.IsNullOrEmpty(r.OriginalTitle) &&
                        (r.OriginalTitle.Contains(eng, StringComparison.OrdinalIgnoreCase) || TokensAllMatch(r, tokens)))
                        return r;
                }
            }

            // 2. 片名直接包含（中英文通用）：去除标点/空白后互相包含即视为命中。
            //    这一步让无年份的纯中文片名也能被接纳（否则会一律返回 null 而匹配失败），
            //    同时要求片名确实相关，避免 TMDB 等宽松匹配把无关片（如年份撞上的错片）误收。
            foreach (var r in results)
            {
                if (TitleContains(r.Title, title) || TitleContains(r.OriginalTitle, title)) return r;
            }

            // 3. 中文/原名的英文词全命中（译名场景）
            if (tokens.Count > 0)
            {
                foreach (var r in results)
                    if (TokensAllMatch(r, tokens)) return r;
            }

            // 4. 年份匹配
            if (year.HasValue && year.Value > 0)
            {
                var y = results.FirstOrDefault(r => r.Year == year.Value);
                if (y != null) return y;
            }

            // 5. 无可靠匹配
            return null;
        }

        /// <summary>去除标点/空白后（保留字母数字与汉字）判断两标题是否互相包含</summary>
        private static bool TitleContains(string? haystack, string? needle)
        {
            if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle)) return false;
            var h = Normalize(haystack);
            var n = Normalize(needle);
            if (h.Length == 0 || n.Length == 0) return false;
            return h.Contains(n, StringComparison.OrdinalIgnoreCase)
                || n.Contains(h, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string s) =>
            Regex.Replace(s, @"[^\p{L}\p{N}]", "").ToLowerInvariant();

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "of", "for", "to", "a", "an", "with", "from"
        };

        /// <summary>从标题中提取可校验的英文/数字词（长度≥3，排除通用词）</summary>
        private static HashSet<string> ExtractTitleTokens(string title)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(title)) return set;
            foreach (Match m in Regex.Matches(title, @"[a-zA-Z0-9]+"))
            {
                var t = m.Value;
                if (t.Length >= 3 && !StopWords.Contains(t)) set.Add(t.ToLowerInvariant());
            }
            return set;
        }

        /// <summary>结果的 Title+OriginalTitle 是否包含文件名里的全部 token</summary>
        private static bool TokensAllMatch(MovieSearchResult r, HashSet<string> tokens)
        {
            if (tokens.Count == 0) return false;
            var hay = $"{r.Title} {r.OriginalTitle}".ToLowerInvariant();
            return tokens.All(t => hay.Contains(t));
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
        // 限流冷却中：直接返回空，绝不发送请求（不再重试加重风控）。
        // 上层（MovieInfoFetcher 熔断）会暂时切到其他源，冷却到期自动恢复。
        if (InCooldown) return new MovieSearchResponse();

        // rexxar 移动端搜索：返回干净 JSON（标题/id/评分/年份/导演/主演/封面），比网页端可靠得多。
        var keyword = CleanSearchTitle(req.Keyword);
        if (string.IsNullOrWhiteSpace(keyword)) keyword = req.Keyword;
        try
        {
            await ThrottleAsync();
            var url = "https://m.douban.com/rexxar/api/v2/search?type=movie&q=" + Uri.EscapeDataString(keyword);
            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            // 非 2xx / 风控页 / 登录页(HTML) → 进入递增冷却，安静避让，不再重试。
            if (!resp.IsSuccessStatusCode || ContainsBanSignal(body) || body.TrimStart().StartsWith("<"))
            {
                TriggerCooldown();
                return new MovieSearchResponse();
            }
            ResetCooldown();   // 正常响应，恢复信任
            var results = ParseRexxarSearch(body);
            return new MovieSearchResponse { Results = results.Take(req.PageSize).ToList(), TotalCount = results.Count };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "豆瓣 rexxar 搜索失败");
            TriggerCooldown();
            return new MovieSearchResponse();
        }
    }

    public async Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
    {
        if (InCooldown) return null;
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        try
        {
            await ThrottleAsync();
            // rexxar 移动端详情端点：返回干净 JSON（导演/演员/评分/年份/海报/国家/语言/时长/简介），
            // 比网页端静态 HTML（无 rating/year，JS 动态加载，解析长期失效）可靠得多。
            var url = "https://m.douban.com/rexxar/api/v2/movie/" + Uri.EscapeDataString(externalId);
            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            // 非 2xx / 风控页 / 登录页(HTML) → 进入递增冷却，安静避让，不再重试。
            if (!resp.IsSuccessStatusCode || ContainsBanSignal(body) || body.TrimStart().StartsWith("<"))
            {
                TriggerCooldown();
                return null;
            }
            ResetCooldown();   // 正常响应，恢复信任
            return ParseRexxarDetail(body, externalId);
        }
        catch (Exception ex) { Log.Error(ex, "豆瓣 rexxar 详情获取失败"); TriggerCooldown(); return null; }
    }

    /// <summary>
    /// 解析 rexxar 搜索返回的 JSON（subjects.items[].target），得到干净的候选列表。
    /// 每条含 title/id/rating{value,count}/cover_url/card_subtitle(国别 / 类型 / 导演 / 主演)/year。
    /// 注意：这里只用于 PickBestMatch 选最佳 ExternalId；完整导演/演员在 GetDetailAsync 的 detail 端点补齐。
    /// </summary>
    private static List<MovieSearchResult> ParseRexxarSearch(string json)
    {
        var results = new List<MovieSearchResult>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("subjects", out var sub)) return results;
            if (!sub.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return results;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("target", out var t)) continue;
                var title = t.TryGetProperty("title", out var tt) ? (tt.GetString() ?? "") : "";
                if (IsTemplateOrLabel(title)) continue;
                var id = t.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;

                double? rating = null; int? rc = null;
                if (t.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Object)
                {
                    if (r.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.Number) rating = rv.GetDouble();
                    if (r.TryGetProperty("count", out var rcv) && rcv.ValueKind == JsonValueKind.Number) rc = rcv.GetInt32();
                }
                var cover = t.TryGetProperty("cover_url", out var c) ? c.GetString() : null;
                if (!string.IsNullOrEmpty(cover)) cover = cover.Replace("/m/", "/l/").Replace("/s/", "/l/");
                int? year = null;
                if (t.TryGetProperty("year", out var y))
                {
                    if (y.ValueKind == JsonValueKind.Number) year = y.GetInt32();
                    else if (y.ValueKind == JsonValueKind.String && int.TryParse(y.GetString(), out var yi)) year = yi;
                }
                // card_subtitle 形如 "中国大陆 / 科幻 / 郭帆 / 吴京 屈楚萧"，取导演(第3段)与主演(第4段)
                string? director = null; string? cast = null;
                if (t.TryGetProperty("card_subtitle", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    var parts = s.GetString()!.Split('/').Select(p => p.Trim()).ToArray();
                    if (parts.Length >= 3) director = parts[2];
                    if (parts.Length >= 4) cast = parts[3];
                }

                results.Add(new MovieSearchResult
                {
                    Title = title,
                    OriginalTitle = null,
                    Year = year ?? 0,
                    Rating = rating,
                    RatingCount = rc,
                    Director = director,
                    Cast = cast,
                    PosterUrl = cover,
                    Runtime = null,
                    ExternalId = id,
                    Source = "douban"
                });
            }
        }
        catch (Exception ex) { Log.Error(ex, "豆瓣 rexxar 搜索结果解析失败"); }
        return results;
    }

    /// <summary>
    /// 解析 rexxar 详情端点返回的 JSON（https://m.douban.com/rexxar/api/v2/movie/{id}），
    /// 补齐完整元数据：导演/演员/评分/年份/海报/国家/语言/时长/简介。
    /// 任何字段缺失都安全留空（不抛异常），交由上层决定是否需要。
    /// </summary>
    private static MovieSearchResult ParseRexxarDetail(string json, string id)
    {
        var r = new MovieSearchResult { ExternalId = id, Source = "douban" };
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            r.Title = root.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";

            if (root.TryGetProperty("year", out var y))
            {
                if (y.ValueKind == JsonValueKind.Number) r.Year = y.GetInt32();
                else if (y.ValueKind == JsonValueKind.String && int.TryParse(y.GetString(), out var yi)) r.Year = yi;
            }

            if (root.TryGetProperty("rating", out var rt) && rt.ValueKind == JsonValueKind.Object)
            {
                if (rt.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.Number) r.Rating = rv.GetDouble();
                if (rt.TryGetProperty("count", out var rcv) && rcv.ValueKind == JsonValueKind.Number) r.RatingCount = rcv.GetInt32();
            }

            if (root.TryGetProperty("directors", out var dirs) && dirs.ValueKind == JsonValueKind.Array)
            {
                var names = dirs.EnumerateArray()
                    .Select(d => d.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(3).ToList();
                if (names.Count > 0) r.Director = string.Join(" / ", names);
            }

            if (root.TryGetProperty("actors", out var acts) && acts.ValueKind == JsonValueKind.Array)
            {
                var names = acts.EnumerateArray()
                    .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(10).ToList();
                if (names.Count > 0) r.Cast = string.Join(", ", names);
            }

            // 海报：优先 cover_url，其次 pic.large / pic.normal
            string? poster = null;
            if (root.TryGetProperty("cover_url", out var cu) && (poster = cu.GetString()) != null) { /* assign */ }
            else if (root.TryGetProperty("pic", out var pic) && pic.ValueKind == JsonValueKind.Object)
            {
                if (pic.TryGetProperty("large", out var pl) && !string.IsNullOrEmpty(pl.GetString())) poster = pl.GetString();
                else if (pic.TryGetProperty("normal", out var pn) && !string.IsNullOrEmpty(pn.GetString())) poster = pn.GetString();
            }
            if (!string.IsNullOrEmpty(poster))
                r.PosterUrl = poster!.Replace("/m/", "/l/").Replace("/s/", "/l/");

            if (root.TryGetProperty("countries", out var ctry) && ctry.ValueKind == JsonValueKind.Array)
                r.Country = string.Join(", ", ctry.EnumerateArray().Select(c => c.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)));

            if (root.TryGetProperty("languages", out var lang) && lang.ValueKind == JsonValueKind.Array)
                r.Language = string.Join(", ", lang.EnumerateArray().Select(c => c.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)));

            if (root.TryGetProperty("durations", out var dur) && dur.ValueKind == JsonValueKind.Array && dur.GetArrayLength() > 0)
            {
                var d0 = dur[0].GetString() ?? "";
                var m = Regex.Match(d0, @"\d+");
                if (m.Success && int.TryParse(m.Value, out var mins)) r.Runtime = mins;
            }

            if (root.TryGetProperty("summary", out var sum) && !string.IsNullOrEmpty(sum.GetString()))
                r.Synopsis = sum.GetString();

            // card_subtitle 也可补导演/主演兜底（detail 端点正常时上面 arrays 已覆盖）
            if (string.IsNullOrEmpty(r.Director) && root.TryGetProperty("card_subtitle", out var cs) && cs.ValueKind == JsonValueKind.String)
            {
                var parts = cs.GetString()!.Split('/').Select(p => p.Trim()).ToArray();
                if (parts.Length >= 3) r.Director = parts[2];
                if (parts.Length >= 4 && string.IsNullOrEmpty(r.Cast)) r.Cast = parts[3];
            }
        }
        catch (Exception ex) { Log.Error(ex, "豆瓣 rexxar 详情解析失败"); }
        return r;
    }

    /// <summary>
    /// 把文件名/标题清洗为适合 rexxar 搜索的纯片名：去年份、去字幕/版本标签、去括号注释、去编码/分辨率噪声。
    /// </summary>
    private static string CleanSearchTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        var s = title;
        // 去括号/方括号/书名号内的注释（[1080p]、(2021)、(BluRay) 等）
        s = Regex.Replace(s, @"[\[\(【（].*?[\]\)】）]", " ");
        foreach (var label in NoiseLabels) s = s.Replace(label, " ");
        // 去独立年份
        s = Regex.Replace(s, @"\b(19|20)\d{2}\b", " ");
        s = Regex.Replace(s, @"[.\-_]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }
}
