using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using EasyMovie.Core;
using EasyMovie.Core.Interfaces;

namespace EasyMovie.Tools.MovieApi;

/// <summary>
/// 影视资讯聚合服务 — 抓取即将上映、本周热映、豆瓣Top250、猫眼榜单
/// </summary>
public class MovieNewsService
{
    private readonly HttpClient _doubanHttp;
    private readonly HttpClient _maoyanHttp;
    private static DateTime _lastDoubanRequest = DateTime.MinValue;
    private static readonly object _doubanLock = new();
    private const int MinIntervalMs = 2000;

    public MovieNewsService()
    {
        _doubanHttp = CreateDoubanClient();
        _maoyanHttp = CreateMaoyanClient();
    }

    private static HttpClient CreateDoubanClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
        client.DefaultRequestHeaders.Referrer = new Uri("https://movie.douban.com/");
        client.Timeout = TimeSpan.FromSeconds(20);
        var cookie = AppSettings.DoubanCookie;
        if (!string.IsNullOrEmpty(cookie)) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static HttpClient CreateMaoyanClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
        client.DefaultRequestHeaders.Referrer = new Uri("https://maoyan.com/");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private static async Task ThrottleDoubanAsync()
    {
        TimeSpan wait;
        lock (_doubanLock)
        {
            var e = DateTime.UtcNow - _lastDoubanRequest;
            wait = TimeSpan.FromMilliseconds(MinIntervalMs) - e;
            if (wait <= TimeSpan.Zero) { _lastDoubanRequest = DateTime.UtcNow; return; }
            _lastDoubanRequest = DateTime.UtcNow.Add(wait);
        }
        await Task.Delay(wait);
    }

    /// <summary>获取豆瓣即将上映（使用 JSON API）</summary>
    public async Task<MovieNewsResult> GetComingSoonAsync(CancellationToken ct = default)
    {
        await ThrottleDoubanAsync();
        try
        {
            // 优先使用 JSON API，海报URL可直接访问
            var json = await _doubanHttp.GetStringAsync(
                "https://movie.douban.com/j/search_subjects?type=movie&tag=即将上映&page_limit=50&page_start=0", ct);
            var items = ParseJsonSubjects(json, "coming");
            if (items.Count > 0) return MovieNewsResult.Ok(items);

            // JSON API 数据不足时，回退到 HTML 页面补充
            await ThrottleDoubanAsync();
            var html = await _doubanHttp.GetStringAsync("https://movie.douban.com/coming", ct);
            if (html.Contains("禁止访问") || html.Contains("检测到异常"))
                return MovieNewsResult.Fail("豆瓣拒绝访问，请在设置中配置豆瓣Cookie");
            var htmlItems = ParseComingSoonHtml(html);
            // 合并：JSON API 已有的不再添加
            var existingIds = items.Select(i => i.ExternalId).ToHashSet();
            foreach (var item in htmlItems.Where(i => !existingIds.Contains(i.ExternalId)))
                items.Add(item);

            return items.Count > 0
                ? MovieNewsResult.Ok(items)
                : MovieNewsResult.Fail("未能解析到电影数据，页面结构可能已变更");
        }
        catch (TaskCanceledException) { return MovieNewsResult.Fail("请求超时，请检查网络连接"); }
        catch (HttpRequestException ex) { return MovieNewsResult.Fail($"网络请求失败: {ex.Message}"); }
        catch (Exception ex) { return MovieNewsResult.Fail($"获取失败: {ex.Message}"); }
    }

    /// <summary>获取豆瓣本周热映（使用 JSON API）</summary>
    public async Task<MovieNewsResult> GetNowPlayingAsync(CancellationToken ct = default)
    {
        await ThrottleDoubanAsync();
        try
        {
            var json = await _doubanHttp.GetStringAsync(
                "https://movie.douban.com/j/search_subjects?type=movie&tag=热门&page_limit=50&page_start=0", ct);
            var items = ParseJsonSubjects(json, "nowplaying");
            return items.Count > 0
                ? MovieNewsResult.Ok(items)
                : MovieNewsResult.Fail("未能解析到电影数据，页面结构可能已变更");
        }
        catch (TaskCanceledException) { return MovieNewsResult.Fail("请求超时，请检查网络连接"); }
        catch (HttpRequestException ex) { return MovieNewsResult.Fail($"网络请求失败: {ex.Message}"); }
        catch (Exception ex) { return MovieNewsResult.Fail($"获取失败: {ex.Message}"); }
    }

    /// <summary>获取豆瓣 Top250（分页加载）</summary>
    public async Task<MovieNewsResult> GetTop250Async(int start = 0, int count = 25, CancellationToken ct = default)
    {
        var all = new List<MovieNewsItem>();
        await ThrottleDoubanAsync();
        try
        {
            var html = await _doubanHttp.GetStringAsync($"https://movie.douban.com/top250?start={start}&filter=", ct);
            if (html.Contains("禁止访问") || html.Contains("检测到异常"))
                return MovieNewsResult.Fail("豆瓣拒绝访问，请在设置中配置豆瓣Cookie");
            var items = ParseTop250(html);
            all.AddRange(items);
            return all.Count > 0 ? MovieNewsResult.Ok(all) : MovieNewsResult.Fail("未能获取 Top250 数据");
        }
        catch (TaskCanceledException) { return MovieNewsResult.Fail("请求超时，请检查网络连接"); }
        catch (HttpRequestException ex) { return MovieNewsResult.Fail($"网络请求失败: {ex.Message}"); }
        catch (Exception ex) { return MovieNewsResult.Fail($"获取失败: {ex.Message}"); }
    }

    /// <summary>获取猫眼热门榜单</summary>
    public async Task<MovieNewsResult> GetMaoyanHotAsync(CancellationToken ct = default)
    {
        try
        {
            var html = await _maoyanHttp.GetStringAsync("https://maoyan.com/board", ct);
            var items = ParseMaoyanBoard(html);
            return items.Count > 0
                ? MovieNewsResult.Ok(items)
                : MovieNewsResult.Fail("未能解析到猫眼榜单数据");
        }
        catch (TaskCanceledException) { return MovieNewsResult.Fail("请求超时，请检查网络连接"); }
        catch (HttpRequestException ex) { return MovieNewsResult.Fail($"网络请求失败: {ex.Message}"); }
        catch (Exception ex) { return MovieNewsResult.Fail($"获取失败: {ex.Message}"); }
    }

    #region ParseJsonSubjects

    /// <summary>解析豆瓣 JSON API 返回的电影列表</summary>
    private static List<MovieNewsItem> ParseJsonSubjects(string json, string category)
    {
        var results = new List<MovieNewsItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("subjects", out var subjects)) return results;
            foreach (var el in subjects.EnumerateArray())
            {
                var title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                var cover = el.TryGetProperty("cover", out var c) ? c.GetString() : null;
                var rate = el.TryGetProperty("rate", out var r) ? r.GetString() : null;
                var url = el.TryGetProperty("url", out var u) ? u.GetString() : null;

                // 从 url 提取 douban id
                var externalId = id;
                if (string.IsNullOrEmpty(externalId) && !string.IsNullOrEmpty(url))
                {
                    var m = Regex.Match(url, @"/subject/(\d+)");
                    if (m.Success) externalId = m.Groups[1].Value;
                }

                double? rating = null;
                if (!string.IsNullOrEmpty(rate) && double.TryParse(rate, out var rv)) rating = rv;

                // 海报URL替换为大图
                if (!string.IsNullOrEmpty(cover))
                    cover = cover.Replace("/s_ratio_poster/", "/l_ratio_poster/").Replace("/m/", "/l/").Replace("/s/", "/l/");

                if (!string.IsNullOrEmpty(title))
                {
                    results.Add(new MovieNewsItem
                    {
                        Title = title,
                        PosterUrl = cover,
                        Rating = rating,
                        ExternalId = externalId,
                        Source = "douban",
                        Category = category
                    });
                }
            }
        }
        catch { }
        return results;
    }

    #endregion

    #region ParseComingSoonHtml

    private static List<MovieNewsItem> ParseComingSoonHtml(string html)
    {
        var results = new List<MovieNewsItem>();

        // 尝试从 JSON 数据解析（优先，信息更丰富）
        var jsonIdx = html.IndexOf("window.__DATA__");
        if (jsonIdx >= 0)
        {
            try
            {
                var braceIdx = html.IndexOf('{', jsonIdx);
                if (braceIdx >= 0)
                {
                    var depth = 0; var end = braceIdx;
                    for (int i = braceIdx; i < html.Length; i++)
                    {
                        if (html[i] == '{') depth++;
                        else if (html[i] == '}')
                        {
                            depth--;
                            if (depth == 0) { end = i + 1; break; }
                        }
                    }
                    var json = html.Substring(braceIdx, end - braceIdx);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("items", out var items))
                    {
                        foreach (var el in items.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idProp) ? idProp.GetInt32().ToString() : "";
                            var title = el.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                            var cover = el.TryGetProperty("cover_url", out var cu) ? cu.GetString() : null;
                            if (!string.IsNullOrEmpty(cover)) cover = cover.Replace("/m/", "/l/").Replace("/s/", "/l/");
                            double? rating = el.TryGetProperty("rating", out var r) && r.TryGetProperty("value", out var v) ? v.GetDouble() : null;
                            var abs = el.TryGetProperty("abstract", out var a) ? a.GetString() ?? "" : "";

                            var item = new MovieNewsItem
                            {
                                Title = title,
                                PosterUrl = cover,
                                Rating = rating,
                                ExternalId = id,
                                Source = "douban",
                                Category = "coming"
                            };

                            // 解析 abstract 中的信息
                            var parts = abs.Split(" / ").Select(p => p.Trim()).ToList();
                            var genres = new HashSet<string> { "动作", "剧情", "科幻", "喜剧", "爱情", "恐怖", "悬疑", "惊悚", "犯罪", "冒险", "奇幻", "动画", "纪录", "战争", "历史", "歌舞", "家庭", "传记", "武侠", "古装", "运动", "音乐" };
                            foreach (var part in parts)
                            {
                                if (part.EndsWith("分钟") && int.TryParse(part.Replace("分钟", ""), out var rt)) item.Runtime = rt;
                                else if (!genres.Contains(part) && !part.EndsWith("分钟") && !int.TryParse(part, out _) && part.Any(c => c >= 0x4e00 && c <= 0x9fff) && !part.Contains("导演") && part.Length <= 10 && item.Country == null) item.Country = part;
                            }

                            var abs2 = el.TryGetProperty("abstract_2", out var a2) ? a2.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(abs2))
                            {
                                var people = abs2.Split(" / ").Select(p => p.Trim()).Where(p => !IsInvalidLabel(p)).ToList();
                                if (people.Count > 0) item.Director = people[0];
                                if (people.Count > 1) item.Cast = string.Join(", ", people.Skip(1).Take(6));
                            }

                            results.Add(item);
                        }
                    }
                }
            }
            catch { /* fallback to regex */ }
        }

        // 正则兜底: 表格结构
        if (results.Count == 0)
        {
            // 匹配 <a href="https://movie.douban.com/subject/xxx/">标题</a>
            foreach (Match m in Regex.Matches(html, @"href=""https?://movie\.douban\.com/subject/(\d+)/?""[^>]*>([^<]+)</a>"))
            {
                var id = m.Groups[1].Value;
                var title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (string.IsNullOrEmpty(title) || results.Any(r => r.ExternalId == id)) continue;

                var item = new MovieNewsItem
                {
                    Title = title,
                    ExternalId = id,
                    Source = "douban",
                    Category = "coming"
                };

                // 从上下文中提取上映日期
                var ctx = html.Substring(Math.Max(0, m.Index - 200), Math.Min(m.Index + m.Length + 500 - Math.Max(0, m.Index - 200), html.Length - Math.Max(0, m.Index - 200)));
                var dateM = Regex.Match(ctx, @"(\d{1,2}月\d{1,2}日|\d{4}年\d{1,2}月\d{1,2}日|\d{4}-\d{2}-\d{2})");
                if (dateM.Success) item.ReleaseDate = dateM.Groups[1].Value;

                results.Add(item);
            }
        }

        return results;
    }

    #endregion

    #region ParseNowPlaying

    private static List<MovieNewsItem> ParseNowPlaying(string html)
    {
        var results = new List<MovieNewsItem>();

        // 方法1: 匹配 <li class="poster"> 结构
        foreach (Match m in Regex.Matches(html, @"<li[^>]*class=""[^""]*poster[^""]*""[^>]*>([\s\S]*?)</li>"))
        {
            var block = m.Groups[1].Value;
            var idM = Regex.Match(block, @"href=""https?://movie\.douban\.com/subject/(\d+)/?""");
            if (!idM.Success) continue;
            var id = idM.Groups[1].Value;
            if (results.Any(r => r.ExternalId == id)) continue;

            var title = "";
            var tm = Regex.Match(block, @"alt=""([^""]+)""");
            if (tm.Success) title = WebUtility.HtmlDecode(tm.Groups[1].Value).Trim();
            if (string.IsNullOrEmpty(title))
            {
                tm = Regex.Match(block, @"href=""https?://movie\.douban\.com/subject/\d+/?""[^>]*>([^<]+)</a>");
                if (tm.Success) title = WebUtility.HtmlDecode(tm.Groups[1].Value).Trim();
            }

            var poster = "";
            var pm = Regex.Match(block, @"<img[^>]*src=""([^""]+)""");
            if (pm.Success) poster = pm.Groups[1].Value.Replace("/m/", "/l/").Replace("/s/", "/l/");

            double? rating = null;
            var rm = Regex.Match(block, @"subject-rate[^>]*>([\d.]+)<");
            if (!rm.Success) rm = Regex.Match(block, @"rating[^>]*>([\d.]+)<");
            if (!rm.Success) rm = Regex.Match(block, @">([\d.]+)</span>\s*$", RegexOptions.Multiline);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, out var r)) rating = r;

            results.Add(new MovieNewsItem
            {
                Title = title,
                PosterUrl = poster,
                Rating = rating,
                ExternalId = id,
                Source = "douban",
                Category = "nowplaying"
            });
        }

        // 方法2: 宽松匹配 — 只要有 subject 链接 + 图片
        if (results.Count == 0)
        {
            foreach (Match m in Regex.Matches(html, @"href=""https?://movie\.douban\.com/subject/(\d+)/?""[^>]*>([\s\S]*?)<img[^>]*src=""([^""]+)""[^>]*alt=""([^""]*)"""))
            {
                var id = m.Groups[1].Value;
                var poster = m.Groups[3].Value.Replace("/m/", "/l/").Replace("/s/", "/l/");
                var title = WebUtility.HtmlDecode(m.Groups[4].Value).Trim();
                if (results.Any(r => r.ExternalId == id)) continue;
                if (string.IsNullOrEmpty(title)) continue;
                results.Add(new MovieNewsItem
                {
                    Title = title,
                    PosterUrl = poster,
                    ExternalId = id,
                    Source = "douban",
                    Category = "nowplaying"
                });
            }
        }

        // 方法3: 最宽松 — 只匹配 subject 链接 + 标题
        if (results.Count == 0)
        {
            foreach (Match m in Regex.Matches(html, @"href=""https?://movie\.douban\.com/subject/(\d+)/?[^""]*""[^>]*>([^<]{1,100})</a>"))
            {
                var id = m.Groups[1].Value;
                var title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (results.Any(r => r.ExternalId == id)) continue;
                if (string.IsNullOrEmpty(title) || title.Contains("选座") || title.Contains("购票")) continue;
                results.Add(new MovieNewsItem
                {
                    Title = title,
                    ExternalId = id,
                    Source = "douban",
                    Category = "nowplaying"
                });
            }
        }

        return results;
    }

    #endregion

    #region ParseTop250

    private static List<MovieNewsItem> ParseTop250(string html)
    {
        var results = new List<MovieNewsItem>();

        // 方法1: 匹配 <div class="item"> 块
        foreach (Match m in Regex.Matches(html, @"<div[^>]*class=""item""[^>]*>([\s\S]*?)</div>\s*</div>"))
        {
            var block = m.Groups[1].Value;
            var idM = Regex.Match(block, @"href=""https?://movie\.douban\.com/subject/(\d+)/?""");
            if (!idM.Success) continue;
            var id = idM.Groups[1].Value;
            if (results.Any(r => r.ExternalId == id)) continue;

            var title = "";
            var tm = Regex.Match(block, @"<span[^>]*class=""title""[^>]*>([^<]+)</span>");
            if (tm.Success) title = WebUtility.HtmlDecode(tm.Groups[1].Value).Trim();

            var otherTitle = "";
            var otm = Regex.Match(block, @"class=""title""[^>]*>[^<]*</span>\s*<span[^>]*class=""title""[^>]*>([^<]+)</span>");
            if (otm.Success) otherTitle = WebUtility.HtmlDecode(otm.Groups[1].Value).Trim().TrimStart('/', ' ');

            var poster = "";
            var pm = Regex.Match(block, @"<img[^>]*src=""([^""]+)""");
            if (pm.Success) poster = pm.Groups[1].Value.Replace("/m/", "/l/").Replace("/s/", "/l/");

            double? rating = null;
            var rm = Regex.Match(block, @"class=""rating_num""[^>]*>([\d.]+)<");
            if (rm.Success && double.TryParse(rm.Groups[1].Value, out var r)) rating = r;

            int? rank = null;
            var rkm = Regex.Match(block, @"<em[^>]*>(\d+)</em>");
            if (rkm.Success && int.TryParse(rkm.Groups[1].Value, out var rk)) rank = rk;

            var info = "";
            var bm = Regex.Match(block, @"<p[^>]*class=""[^""]*""[^>]*>([\s\S]*?)</p>");
            if (bm.Success) info = bm.Groups[1].Value;

            var director = "";
            var cast = "";
            var cleanInfo = Regex.Replace(info, @"<[^>]+>", " ");
            var dm = Regex.Match(cleanInfo, @"导演[：:]\s*([^/]+?)(?:\s*主演|$)");
            if (dm.Success) director = dm.Groups[1].Value.Trim();
            var cm = Regex.Match(cleanInfo, @"主演[：:]\s*([^/]+?)(?:\s*/|$)");
            if (cm.Success) cast = cm.Groups[1].Value.Trim();

            var year = 0;
            var ym = Regex.Match(cleanInfo, @"(\d{4})");
            if (ym.Success) int.TryParse(ym.Groups[1].Value, out year);

            var country = "";
            // 格式: 导演:xxx / 主演:yyy / 2022 / 中国 / 动作
            var segments = cleanInfo.Split('/').Select(s => s.Trim()).ToList();
            foreach (var seg in segments)
            {
                if (seg.Contains("导演") || seg.Contains("主演") || int.TryParse(seg, out _) || seg.Length > 15) continue;
                if (seg.Any(c => c >= 0x4e00 && c <= 0x9fff) && !seg.Contains("导演") && !seg.Contains("主演"))
                {
                    country = seg;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(title))
            {
                results.Add(new MovieNewsItem
                {
                    Title = title,
                    OriginalTitle = otherTitle != title ? otherTitle : null,
                    Year = year,
                    Director = director,
                    Cast = cast,
                    Country = country,
                    PosterUrl = poster,
                    Rating = rating,
                    ExternalId = id,
                    Source = "douban",
                    Category = "top250",
                    Rank = rank
                });
            }
        }

        // 方法2: 宽松匹配
        if (results.Count == 0)
        {
            foreach (Match m in Regex.Matches(html, @"href=""https?://movie\.douban\.com/subject/(\d+)/?""[^>]*>[\s\S]*?<span[^>]*class=""title""[^>]*>([^<]+)</span>"))
            {
                var id = m.Groups[1].Value;
                var title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (results.Any(r => r.ExternalId == id) || string.IsNullOrEmpty(title)) continue;
                results.Add(new MovieNewsItem
                {
                    Title = title,
                    ExternalId = id,
                    Source = "douban",
                    Category = "top250"
                });
            }
        }

        return results;
    }

    #endregion

    #region ParseMaoyanBoard

    private static List<MovieNewsItem> ParseMaoyanBoard(string html)
    {
        var results = new List<MovieNewsItem>();

        // 匹配 <dd>...</dd> 块
        foreach (Match m in Regex.Matches(html, @"<dd>([\s\S]*?)</dd>"))
        {
            var block = m.Groups[1].Value;

            var title = "";
            var tm = Regex.Match(block, @"class=""name""[^>]*>([^<]+)</a>");
            if (!tm.Success) tm = Regex.Match(block, @"/films/\d+[^>]*>([^<]+)</a>");
            if (tm.Success) title = WebUtility.HtmlDecode(tm.Groups[1].Value).Trim();

            var id = "";
            var im = Regex.Match(block, @"href=""/films/(\d+)""");
            if (im.Success) id = im.Groups[1].Value;

            // 海报: 优先取 data-src (懒加载)，其次 src
            var poster = "";
            var pm = Regex.Match(block, @"data-src=""([^""]+)""");
            if (!pm.Success) pm = Regex.Match(block, @"<img[^>]*src=""([^""]+)""");
            if (pm.Success) poster = pm.Groups[1].Value;

            double? rating = null;
            // 猫眼评分拆分为 integer + fraction
            var rm = Regex.Match(block, @"class=""integer""[^>]*>(\d+)</i>\s*<i[^>]*class=""fraction""[^>]*>(\d+)</i>");
            if (rm.Success && double.TryParse(rm.Groups[1].Value + "." + rm.Groups[2].Value, out var r)) rating = r;
            if (rating == null)
            {
                rm = Regex.Match(block, @"class=""score[^""]*""[^>]*>([\d.]+)<");
                if (!rm.Success) rm = Regex.Match(block, @"score[^>]*>([\d.]+)<");
                if (rm.Success && double.TryParse(rm.Groups[1].Value, out var r2)) rating = r2;
            }

            int? rank = null;
            var rkm = Regex.Match(block, @"class=""board-index[^""]*""[^>]*>(\d+)</i>");
            if (rkm.Success && int.TryParse(rkm.Groups[1].Value, out var rk)) rank = rk;

            var star = "";
            var sm = Regex.Match(block, @"class=""star""[^>]*>([^<]+)</p>");
            if (sm.Success) star = WebUtility.HtmlDecode(sm.Groups[1].Value).Trim();

            var releaseInfo = "";
            var rlm = Regex.Match(block, @"class=""releasetime""[^>]*>([^<]+)</p>");
            if (rlm.Success) releaseInfo = WebUtility.HtmlDecode(rlm.Groups[1].Value).Trim();

            var boxOffice = "";
            var bom = Regex.Match(block, @"class=""movie-wish""[^>]*>([\s\S]*?)</p>");
            if (bom.Success) boxOffice = WebUtility.HtmlDecode(Regex.Replace(bom.Groups[1].Value, @"<[^>]+>", "")).Trim();

            if (!string.IsNullOrEmpty(title))
            {
                var director = "";
                var cast = "";
                if (!string.IsNullOrEmpty(star))
                {
                    var dm2 = Regex.Match(star, @"主演[：:]\s*(.+)");
                    if (dm2.Success) cast = dm2.Groups[1].Value.Trim();
                    else cast = star.Replace("主演：", "").Replace("主演:", "").Trim();
                }

                var year = 0;
                var ym = Regex.Match(releaseInfo, @"(\d{4})");
                if (ym.Success) int.TryParse(ym.Groups[1].Value, out year);

                results.Add(new MovieNewsItem
                {
                    Title = title,
                    Year = year,
                    Director = director,
                    Cast = cast,
                    PosterUrl = poster,
                    Rating = rating,
                    ExternalId = id,
                    Source = "maoyan",
                    Category = "hot",
                    Rank = rank,
                    ReleaseDate = releaseInfo,
                    BoxOffice = boxOffice
                });
            }
        }

        return results;
    }

    #endregion

    private static readonly string[] InvalidLabels = { "人员", "人物", "演员", "主演", "导演", "暂无", "未知", "暂未录入", "更多" };

    private static bool IsInvalidLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (Regex.IsMatch(value, @"\$\{.*?\}|\$\(data\.\w+\)|\{\{.*?\}\}|<%.*?%>")) return true;
        if (InvalidLabels.Contains(value)) return true;
        return false;
    }
}

/// <summary>
/// 资讯查询结果（含错误信息）
/// </summary>
public class MovieNewsResult
{
    public List<MovieNewsItem> Items { get; set; } = new();
    public string? Error { get; set; }
    public bool Success => Error == null;

    public static MovieNewsResult Ok(List<MovieNewsItem> items) => new() { Items = items };
    public static MovieNewsResult Fail(string error) => new() { Error = error };
}
