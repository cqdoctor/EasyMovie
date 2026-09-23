﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using EasyMovie.Core;
using EasyMovie.Core.Helpers;
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

    // rexxar 移动端接口（m.douban.com/rexxar/api/v2）：免 key、免签名，返回干净 JSON。
    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 MicroMessenger/8.0 Douban/7.38.0";
    // 网页搜索页兜底路径：需要桌面端 UA + movie.douban.com Referer，否则拿不到 window.__DATA__。
    private const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    // 限流自我冷却：确认命中反爬封控后进入递增冷却期，期间不再发送任何请求（避免加重风控），
    // 冷却到期自动恢复；连续触发则冷却时长递增直至封顶，正常响应即重置信任。
    //
    // 重要（#10）：冷却只应由「硬封禁」触发，绝不能由概率性 403 触发。
    // 实测 rexxar 接口会以约 40%~50% 的概率随机返回 403 need_login（与 Cookie 完整度无关：
    // 全量 Cookie / 仅 dbcl2+frodotk_db / 完全无 Cookie 三路对照无显著差异），
    // 旧实现把这种瞬时 403 也当成硬封禁，一次命中就冷却 60s 并逐次翻倍到 600s，
    // 结果系统几乎永久处于冷却态——实测 12 个片名仅 2 个命中、9 个被冷却跳过，补全被彻底饿死。
    private static DateTime _cooldownUntil = DateTime.MinValue;
    private static int _rateLimitStrikes = 0;
    private static bool InCooldown => DateTime.UtcNow < _cooldownUntil;

    private static void TriggerCooldown()
    {
        _rateLimitStrikes++;
        var seconds = Math.Min(60 * _rateLimitStrikes, 600);
        _cooldownUntil = DateTime.UtcNow.AddSeconds(seconds);
        Log.Warning("豆瓣命中硬封控，进入冷却 {Seconds}s（第 {Strikes} 次）", seconds, _rateLimitStrikes);
    }
    private static void ResetCooldown()
    {
        _rateLimitStrikes = 0;
        _cooldownUntil = DateTime.MinValue;
    }

    /// <summary>清空限流冷却状态（测试与「立即重试豆瓣」场景使用）。</summary>
    public static void ResetThrottleState()
    {
        lock (_lock)
        {
            _rateLimitStrikes = 0;
            _cooldownUntil = DateTime.MinValue;
        }
    }

    public DoubanApiClient(HttpClient? http = null) { _http = http ?? CreateClient(); }

    private static HttpClient CreateClient()
    {
        // AllowAutoRedirect=false：豆瓣限流时会 302 到 /misc/sorry 或 sec.douban.com，
        // 自动跟随会把限流页当成 200 正常响应（旧实现因此识别不到真正的封控）。
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = false
        };
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
        // UA / Referer / Accept 不在默认头上设置：两条路径的指纹不同，改为按请求逐个附加（见 CreateRequest）。
        client.Timeout = TimeSpan.FromSeconds(12);
        var cookie = AppSettings.DoubanCookie;
        if (!string.IsNullOrEmpty(cookie)) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    /// <summary>按目标路径附加对应的请求指纹（rexxar 移动端 / 网页搜索页）。</summary>
    private static HttpRequestMessage CreateRequest(string url, bool html)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (html)
        {
            req.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            req.Headers.TryAddWithoutValidation("Referer", "https://movie.douban.com/");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        }
        else
        {
            req.Headers.TryAddWithoutValidation("User-Agent", MobileUserAgent);
            req.Headers.TryAddWithoutValidation("Referer", "https://m.douban.com/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
        }
        return req;
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
    /// 高置信封控/验证码信号（命中即进入递增冷却，安静避让）。
    /// 仅收录“几乎只出现在风控页”的标记，避免误伤正常结果页
    /// （正常页顶部导航含“登录”链接，但不会出现“登录豆瓣”页标题或“过于频繁”等字样）。
    /// </summary>
    /// <remarks>
    /// 注意 <c>need_login</c> 已从此清单移除（#10）：rexxar 会概率性返回 403 + <c>{"msg":"need_login","code":103}</c>，
    /// 把它当硬封禁会让系统永久冷却。它属于<see cref="IsSoftThrottle"/>判定的软限流，改走网页搜索兜底。
    /// </remarks>
    private static readonly string[] BanSignals =
    {
        "禁止访问", "检测到有异常请求", "请输入验证码",
        "你当前访问过于频繁", "访问过于频繁", "安全验证", "安全校验",
        "登录豆瓣", "accounts.douban.com/login",
        // 限流跳转目标：实测密集请求后网页搜索会 302 到 /misc/sorry，详情页会 302 到 sec.douban.com
        "/misc/sorry", "misc/sorry", "sec.douban.com"
    };

    private static bool ContainsBanSignal(string html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        foreach (var s in BanSignals)
            if (html.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 是否为「软限流」：单次请求被随机拒绝，但账号/IP 并未被封。
    /// 判据：403/401 状态码，或响应体含 need_login / code 103。
    /// 软限流不触发冷却，改由网页搜索兜底路径补救。
    /// </summary>
    private static bool IsSoftThrottle(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.Forbidden || status == HttpStatusCode.Unauthorized) return true;
        if (string.IsNullOrEmpty(body)) return false;
        return body.Contains("need_login", StringComparison.OrdinalIgnoreCase)
            || body.Contains("\"code\": 103", StringComparison.Ordinal)
            || body.Contains("\"code\":103", StringComparison.Ordinal);
    }

    /// <summary>
    /// 最近一次搜索是否命中「搜索访问太频繁」配额软限流。
    /// 该限流的特征是 <b>HTTP 200 + window.__DATA__ 里 error_info="搜索访问太频繁。" + items=[]</b>——
    /// 既不是 403（IsSoftThrottle 抓不到），也不是硬封禁（不该冷却），
    /// 旧实现因此把它当成「豆瓣没有这部片」，报告里一律显示为「无可靠匹配」，
    /// 实际上只是配额用尽，下次调度就能继续（见 DoubanBackfillService 的提前停止分支）。
    /// </summary>
    public static bool LastSearchQuotaExceeded { get; private set; }

    private static bool IsSearchQuotaExceeded(string? body) =>
        !string.IsNullOrEmpty(body) &&
        (body.Contains("搜索访问太频繁", StringComparison.OrdinalIgnoreCase) ||
         body.Contains("\\u641c\\u7d22\\u8bbf\\u95ee\\u592a\\u9891\\u7e41", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 是否被 302 到风控中间页（/misc/sorry、sec.douban.com、登录页）。
    /// 这类跳转是货真价实的封控信号，必须触发冷却，不能继续请求。
    /// </summary>
    private static bool IsRedirectToInterstitial(HttpResponseMessage resp, out string? location)
    {
        location = resp.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(location)) return false;
        return location.Contains("/misc/sorry", StringComparison.OrdinalIgnoreCase)
            || location.Contains("sec.douban.com", StringComparison.OrdinalIgnoreCase)
            || location.Contains("accounts.douban.com/passport", StringComparison.OrdinalIgnoreCase);
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
            // 关键：只按“结果自身的 Title 是否为模板/占位符”判定；OriginalTitle 为空（豆瓣 rexxar 对中文片
            // 恒为 null，是常态）绝不能丢弃——IsTemplateOrLabel(null) 返回 true，若对 OriginalTitle 也加
            // “!IsTemplateOrLabel” 会让 `!true`=false 把整条合法结果过滤掉，导致 PickBestMatch 对全部中文片
            // 返回 null、补全服务静默跳过所有影片（2026-09-18 实测回归根因）。
            results = results.Where(r => !IsTemplateOrLabel(r.Title)
                && (string.IsNullOrEmpty(r.OriginalTitle) || !IsTemplateOrLabel(r.OriginalTitle))).ToList();
            if (results.Count == 0) return null;

            // 用清洗后的片名做匹配（剥编码/音轨/质量标签 EAC3/Atmos/AC3/TrueHD… 与独立年份）。
            // 不清洗会误杀：反向包含要求长度比 ≥0.5，而脏标题「东北警察故事2 EAC3 Atmos」
            // 相对干净结果「东北警察故事2」只有 7/16≈0.44，导致明明搜到了也判「无可靠匹配」永久跳过。
            var matchTitle = CleanSearchTitle(title);
            if (string.IsNullOrWhiteSpace(matchTitle)) matchTitle = title;

            // 0. 精确片名匹配优先：归一化完全相等的同名结果，优于带序号的续集
            //    （如“速度与激情”应优先于“速度与激情10”，“加勒比海盗”优于“加勒比海盗2”）
            var nt = Normalize(matchTitle);
            foreach (var r in results)
            {
                if (Normalize(r.Title) == nt || Normalize(r.OriginalTitle) == nt)
                    return r;
            }

            var eng = ExtractEnglishHint(matchTitle);
            var tokens = ExtractTitleTokens(matchTitle);

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
                if (TitleContains(r.Title, matchTitle) || TitleContains(r.OriginalTitle, matchTitle)) return r;
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
        /// <remarks>
        /// 两个方向的包含，风险完全不同，必须区别对待：
        ///   正向（结果标题包含搜索词）：安全。如搜「非诚勿扰」命中「非诚勿扰3」，是同一系列，放行。
        ///   反向（搜索词包含结果标题）：危险。此时结果标题是搜索词的「子串」，
        ///     越短越容易是无关片——实测搜「杀死比尔」会命中单字片「杀」，
        ///     进而把无关影片的元数据写进 cache.db（观测到 680 条缓存中 177 条 Title 长度&lt;=3，
        ///     样本为「爱」「杀」「我」「B」「S」，均为此类误匹配产物）。
        /// 因此反向分支设两道门槛：子串至少 3 个字符，且长度比不低于 0.5。
        /// 宁可判为「无匹配」跳过，也不能把错片写进缓存污染后续导入。
        /// </remarks>
        private static bool TitleContains(string? haystack, string? needle)
        {
            if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle)) return false;
            var h = Normalize(haystack);
            var n = Normalize(needle);
            if (h.Length == 0 || n.Length == 0) return false;

            if (h.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;

            if (n.Contains(h, StringComparison.OrdinalIgnoreCase))
            {
                const int MinSubstringLength = 3;
                const double MinLengthRatio = 0.5;
                return h.Length >= MinSubstringLength && (double)h.Length / n.Length >= MinLengthRatio;
            }
            return false;
        }

        /// <summary>归一化标题：只保留字母/数字/汉字，转小写。</summary>
        /// <remarks>
        /// 必须容忍 null：豆瓣 rexxar 搜索结果不含英文名（ParseRexxarSearch 里 OriginalTitle 恒为 null），
        /// 而 PickBestMatch 会对每条结果调用 Normalize(r.OriginalTitle)。
        /// 早期版本此处未判空，Regex.Replace(null) 直接抛 ArgumentNullException，
        /// 导致只要搜索结果非空就崩溃——补全服务因此整体失效（实测复现）。
        /// </remarks>
        private static string Normalize(string? s) =>
            Regex.Replace(s ?? "", @"[^\p{L}\p{N}]", "").ToLowerInvariant();

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

    /// <param name="value">待判定文本。<b>null/空白视为模板并返回 true</b>（契约即「不可信身份」），
    /// 故形参声明为可空，避免调用方对 null 的 OriginalTitle 触发 CS8604。</param>
    public static bool IsTemplateOrLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        // 覆盖服务端模板占位符：Ruby/ERB 风格 "#= data.original_title #"、JS 模板 "{{ data.x }}"、
        // "$=" / "<% %>" 等。TMDB 静态兜底页未渲染时会出现此类占位符，不能作为真实片名使用。
        if (Regex.IsMatch(value, @"\$\{.*?\}|\$\(data\.\w+\)|\{\{.*?\}\}|<%.*?%>|#=\s*data|#=\s*\w+\.|data\.original_title|data\.title\b")) return true;
        if (InvalidLabels.Contains(value)) return true;
        return false;
    }

    public async Task<MovieSearchResponse> SearchAsync(MovieSearchRequest req, CancellationToken ct = default)
    {
        // 限流冷却中：直接返回空，绝不发送请求（不再重试加重风控）。
        // 上层（MovieInfoFetcher 熔断）会暂时切到其他源，冷却到期自动恢复。
        if (InCooldown) return new MovieSearchResponse();
        LastSearchQuotaExceeded = false;   // 每次搜索重算，避免上一次的标记污染本次判定

        var keyword = CleanSearchTitle(req.Keyword);
        if (string.IsNullOrWhiteSpace(keyword)) keyword = req.Keyword;

        // 关键词分工（修复英文名影片匹配缺口 #豆瓣补全）：
        //   · 混合标题（中+英，如「困兽Death Stranding」「唐探1900Detective Chinatown」）：
        //     用【完整关键词】作主查询。完整词含英文，配合网页搜索（结果带 OriginalTitle）
        //     才能让 PickBestMatch 走英文校验分支命中；若网页搜索无候选，再用【中文核心】兜底。
        //   · 纯中文标题：用原样（rexxar 命中率高）；rexxar 的 OriginalTitle 恒为 null 不影响中文校验。
        //   注：SearchOnceAsync 内部对含拉丁字母的关键词优先走网页搜索，天然契合上述分工。
        var primary = keyword;
        string? fallback = null;
        if (SearchKeywordPurifier.TryExtractChineseCore(keyword, out var core))
        {
            fallback = core;   // 中文核心作为兜底，而非主查询（旧逻辑把核心当主查询，丢掉了英文鉴别符）
        }

        var attempt = await SearchOnceAsync(primary, req.PageSize, ct);
        if (attempt.results.Count == 0 && fallback != null)
        {
            Log.Information("豆瓣：完整词 [{Full}] 无候选，改用中文核心 [{Core}] 兜底", primary, fallback);
            attempt = await SearchOnceAsync(fallback, req.PageSize, ct);
        }

        if (attempt.results.Count > 0)
        {
            ResetCooldown();
            return new MovieSearchResponse { Results = attempt.results, TotalCount = attempt.results.Count };
        }
        // 仅硬封禁（302 风控页 / 验证码 / 禁止访问）才进入冷却并停 run。
        // 软限流（need_login 概率性 403）不在此冷却——它由上层补全服务按影片重试，
        // 重试仍保持 12s 节奏，不会密集探测加重风控（见 DoubanBackfillService）。
        if (attempt.hardBan) TriggerCooldown();
        return new MovieSearchResponse();
    }

    /// <summary>
    /// 单次查询：按关键词形态选择主路径——
    ///   · 含拉丁字母（英文名影片）：rexxar 返回的 OriginalTitle 恒为 null，
    ///     导致 PickBestMatch 无法按英文名校验，且网页搜索在同窗口更宽容、字段更全
    ///     （含原名/导演/主演）→ <b>优先走网页搜索</b>，失败再回退 rexxar。
    ///   · 纯中文：rexxar 优先（干净 JSON、命中率高），失败兜底网页搜索。
    /// 两条路径返回 (结果集, 是否命中硬封禁)。
    /// </summary>
    private async Task<(List<MovieSearchResult> results, bool hardBan)> SearchOnceAsync(
        string keyword, int pageSize, CancellationToken ct)
    {
        if (ContainsLatin(keyword))
        {
            // 英文名影片：网页搜索优先（字段更全，且对这类片名更宽容）
            var html = await TryHtmlSearchAsync(keyword, pageSize, ct);
            if (html.ok) return (html.results.Take(pageSize).ToList(), false);
            if (html.hardBan) return (new List<MovieSearchResult>(), true);

            // 网页搜索软失败 → rexxar 兜底（rexxar 偶尔能命中而网页被限）
            var rexxar = await TryRexxarSearchAsync(keyword, pageSize, ct);
            if (rexxar.ok) return (rexxar.results.Take(pageSize).ToList(), false);
            if (rexxar.hardBan) return (new List<MovieSearchResult>(), true);
            return (new List<MovieSearchResult>(), false);
        }

        // 纯中文：rexxar 优先
        var r = await TryRexxarSearchAsync(keyword, pageSize, ct);
        if (r.ok) return (r.results.Take(pageSize).ToList(), false);
        if (r.hardBan) return (new List<MovieSearchResult>(), true);

        // 路径 2：网页搜索页兜底。rexxar 的概率性 403（need_login）与「无结果」都走这里。
        // 实测该路径字段更全（评分/原名/片长/国别/导演/主演）且在同一时间窗内更宽容，
        // 是 rexxar 被限流时唯一能拿到数据的通道。
        var h = await TryHtmlSearchAsync(keyword, pageSize, ct);
        if (h.ok)
        {
            Log.Information("豆瓣 rexxar 未命中，已由网页搜索兜底：{Keyword}", keyword);
            return (h.results.Take(pageSize).ToList(), false);
        }
        return (new List<MovieSearchResult>(), h.hardBan);
    }

    /// <summary>关键词是否含拉丁字母（英文名影片判定）。</summary>
    private static bool ContainsLatin(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var c in s)
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') return true;
        return false;
    }

    /// <summary>路径 1：rexxar 移动端搜索。返回 (是否拿到数据, 是否硬封禁, 结果集)。</summary>
    private async Task<(bool ok, bool hardBan, List<MovieSearchResult> results)> TryRexxarSearchAsync(
        string keyword, int pageSize, CancellationToken ct)
    {
        try
        {
            await ThrottleAsync();
            var url = "https://m.douban.com/rexxar/api/v2/search?type=movie&q=" + Uri.EscapeDataString(keyword);
            using var req = CreateRequest(url, html: false);
            using var resp = await _http.SendAsync(req, ct);
            if (IsRedirectToInterstitial(resp, out var loc))
            {
                Log.Warning("豆瓣 rexxar 搜索被重定向到风控页：{Loc}", loc);
                return (false, true, new List<MovieSearchResult>());
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            // 概率性 403 need_login：软失败，交给网页搜索兜底，绝不进冷却。
            if (IsSoftThrottle(resp.StatusCode, body)) return (false, false, new List<MovieSearchResult>());
            // 其它非 2xx / 风控页 / 登录页(HTML) → 硬封禁
            if (!resp.IsSuccessStatusCode || ContainsBanSignal(body) || body.TrimStart().StartsWith("<"))
                return (false, true, new List<MovieSearchResult>());

            var results = ParseRexxarSearch(body);
            // 空结果不是封禁（片子确实没收录），但不算命中——交给兜底路径再用网页搜索确认一次
            if (results.Count == 0) return (false, false, new List<MovieSearchResult>());
            return (true, false, results.Take(pageSize).ToList());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "豆瓣 rexxar 搜索失败");
            return (false, false, new List<MovieSearchResult>());   // 网络异常按软失败处理，交由兜底
        }
    }

    /// <summary>路径 2：网页搜索页（movie.douban.com/subject_search）解析 window.__DATA__。</summary>
    private async Task<(bool ok, bool hardBan, List<MovieSearchResult> results)> TryHtmlSearchAsync(
        string keyword, int pageSize, CancellationToken ct)
    {
        try
        {
            await ThrottleAsync();
            var url = "https://movie.douban.com/subject_search?search_text=" + Uri.EscapeDataString(keyword);
            using var req = CreateRequest(url, html: true);
            using var resp = await _http.SendAsync(req, ct);
            if (IsRedirectToInterstitial(resp, out var loc))
            {
                Log.Warning("豆瓣网页搜索被重定向到风控页：{Loc}", loc);
                return (false, true, new List<MovieSearchResult>());
            }
            // 非风控类跳转（如 subject_search → movie/subject_search）无法取到数据，按软失败
            if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400)
                return (false, false, new List<MovieSearchResult>());

            var body = await resp.Content.ReadAsStringAsync(ct);
            // 概率性 403 need_login（code 103）按软限流处理（与 rexxar 对齐）：不进冷却、交由上层重试/兜底，
            // 否则拉丁关键词走网页搜索优先时会把瞬时拒绝误判为硬封禁而直接停 run（典型的“被方差劝退”）。
            if (IsSoftThrottle(resp.StatusCode, body)) return (false, false, new List<MovieSearchResult>());
            if (!resp.IsSuccessStatusCode || ContainsBanSignal(body))
                return (false, true, new List<MovieSearchResult>());

            var results = DoubanHtmlSearchParser.Parse(body);
            // 配额软限流（HTTP 200 + error_info="搜索访问太频繁" + items=[]）：不是「豆瓣没收录」，
            // 标记后交由上层提前停止 run，避免把剩余配额全耗在必然为空的请求上。
            if (results.Count == 0 && IsSearchQuotaExceeded(body))
            {
                LastSearchQuotaExceeded = true;
                Log.Warning("豆瓣网页搜索命中配额软限流（搜索访问太频繁）：{Keyword}", keyword);
            }
            if (results.Count == 0) return (false, false, new List<MovieSearchResult>());
            return (true, false, results.Take(pageSize).ToList());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "豆瓣网页搜索兜底失败");
            return (false, false, new List<MovieSearchResult>());
        }
    }

    public async Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
    {
        if (InCooldown) return null;
        if (string.IsNullOrWhiteSpace(externalId)) return null;

        // rexxar 详情端点同样存在概率性 403，因此软失败时重试一次（不是盲目重试，最多一次）。
        // 网页详情页（movie.douban.com/subject/{id}）会被 302 到 sec.douban.com，无法作为兜底路径。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (ok, hardBan, result) = await TryRexxarDetailAsync(externalId, ct);
            if (ok) { ResetCooldown(); return result; }
            if (hardBan) { TriggerCooldown(); return null; }
            if (attempt == 0)
            {
                Log.Information("豆瓣详情软失败，2s 后重试一次：{Id}", externalId);
                await Task.Delay(2000, ct);
            }
        }
        // 详情软失败不进冷却：硬封禁已在上方处理，软失败由上层按需重试。
        return null;
    }

    private async Task<(bool ok, bool hardBan, MovieSearchResult? result)> TryRexxarDetailAsync(
        string externalId, CancellationToken ct)
    {
        try
        {
            await ThrottleAsync();
            var url = "https://m.douban.com/rexxar/api/v2/movie/" + Uri.EscapeDataString(externalId);
            using var req = CreateRequest(url, html: false);
            using var resp = await _http.SendAsync(req, ct);
            if (IsRedirectToInterstitial(resp, out var loc))
            {
                Log.Warning("豆瓣 rexxar 详情被重定向到风控页：{Loc}", loc);
                return (false, true, null);
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (IsSoftThrottle(resp.StatusCode, body)) return (false, false, null);
            if (!resp.IsSuccessStatusCode || ContainsBanSignal(body) || body.TrimStart().StartsWith("<"))
                return (false, true, null);

            var parsed = ParseRexxarDetail(body, externalId);
            if (string.IsNullOrWhiteSpace(parsed.Title)) return (false, false, null);
            return (true, false, parsed);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "豆瓣 rexxar 详情获取失败");
            return (false, false, null);
        }
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
    public static string CleanSearchTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        var s = title;
        // 去括号/方括号/书名号内的注释（[1080p]、(2021)、(BluRay) 等）
        s = Regex.Replace(s, @"[\[\(【（].*?[\]\)】）]", " ");
        foreach (var label in NoiseLabels) s = s.Replace(label, " ");
        // 剥编码/音轨/质量/发布组标签（EAC3/Atmos/AC3/TrueHD/x264/DTS/WEB-DL…），
        // 与 FileNameParser 复用同一份清单，避免三处漂移；这些标签会污染搜索词导致豆瓣 0 候选。
        s = FileNameParser.StripTags(s);
        // 去独立年份
        s = Regex.Replace(s, @"\b(19|20)\d{2}\b", " ");
        s = Regex.Replace(s, @"[.\-_]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }
}
