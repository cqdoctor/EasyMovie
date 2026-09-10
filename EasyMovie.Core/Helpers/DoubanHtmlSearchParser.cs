using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyMovie.Core.Interfaces;

namespace EasyMovie.Core.Helpers;

/// <summary>
/// 解析豆瓣网页搜索页（movie.douban.com/subject_search）返回的 window.__DATA__ JSON。
/// </summary>
/// <remarks>
/// 为什么要这个解析器（#10 实测结论）：
///   - rexxar 移动端接口（m.douban.com/rexxar/api/v2/search）存在**概率性 403 need_login**（实测 40%~50%），
///     且与 Cookie 完整度无关（全量 Cookie / 仅 dbcl2+frodotk_db / 无 Cookie 三路对照无显著差异）；
///   - 网页搜索页在同一时间窗内明显更宽容（低强度实测 5/5 成功），且字段更全：
///     评分与评分人数、封面、国别、类型、原名、片长、导演、主演一应俱全；
///   - 因此把网页搜索作为 rexxar 的**兜底路径**，可把单次查询的整体成功率从约 55% 拉到接近 100%。
/// 数据来源是页面内联的 <c>window.__DATA__ = {...}</c>，属于前端渲染用的结构化数据，
/// 比正则抠 HTML 标签稳定得多；解析全程无异常抛出，失败即返回空列表，由上层继续走其它数据源。
/// </remarks>
public static class DoubanHtmlSearchParser
{
    private const string Marker = "window.__DATA__";

    /// <summary>解析整页 HTML，返回候选影片列表；任何异常都吞掉并返回空列表。</summary>
    public static List<MovieSearchResult> Parse(string? html)
    {
        var results = new List<MovieSearchResult>();
        if (string.IsNullOrWhiteSpace(html)) return results;
        try
        {
            var json = ExtractJson(html!);
            if (string.IsNullOrEmpty(json)) return results;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array) return results;

            foreach (var item in items.EnumerateArray())
            {
                var r = ParseItem(item);
                if (r != null) results.Add(r);
            }
        }
        catch
        {
            // 解析失败一律返回已收集到的部分结果，绝不抛给调用方
        }
        return results;
    }

    /// <summary>
    /// 从 HTML 中截取 <c>window.__DATA__ = { ... }</c> 的 JSON 片段（按大括号配对截断，直到深度归零）。
    /// </summary>
    public static string? ExtractJson(string html)
    {
        var markerIdx = html.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIdx < 0) return null;
        var start = html.IndexOf('{', markerIdx);
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return html.Substring(start, i - start + 1);
                    break;
            }
        }
        return null;
    }

    private static MovieSearchResult? ParseItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var id = item.TryGetProperty("id", out var idEl) ? ToIdString(idEl) : null;
        // id 缺失时尝试从 url（https://movie.douban.com/subject/35267208/）里抠
        if (string.IsNullOrEmpty(id) && item.TryGetProperty("url", out var urlEl))
            id = Regex.Match(urlEl.GetString() ?? "", @"/subject/(\d+)").Success
                ? Regex.Match(urlEl.GetString() ?? "", @"/subject/(\d+)").Groups[1].Value
                : null;
        if (string.IsNullOrEmpty(id)) return null;

        var rawTitle = item.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
        var title = CleanTitle(rawTitle, out var year);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var r = new MovieSearchResult
        {
            Title = title,
            Year = year,
            ExternalId = id,
            Source = "douban"
        };

        var abstractText = item.TryGetProperty("abstract", out var a) ? (a.GetString() ?? "") : "";
        var creditText = item.TryGetProperty("abstract_2", out var a2) ? (a2.GetString() ?? "") : "";

        var parts = abstractText.Split('/').Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p)).ToList();

        // 片长：形如 "173分钟"
        foreach (var p in parts)
        {
            var m = Regex.Match(p, @"^(\d+)\s*分钟$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var mins)) { r.Runtime = mins; break; }
        }

        // 国别：首个片段通常是地区（中国大陆 / 美国 / 中国香港 ...）
        if (parts.Count > 0 && LooksLikeRegion(parts[0])) r.Country = parts[0];

        // 原名：首个不含汉字的片段（排除 3D/IMAX 等版本词）
        r.OriginalTitle = parts.FirstOrDefault(IsLatinTitle);

        // 评分
        if (item.TryGetProperty("rating", out var rt) && rt.ValueKind == JsonValueKind.Object)
        {
            if (rt.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.Number)
            {
                var v = rv.GetDouble();
                if (v > 0) r.Rating = v;
            }
            if (rt.TryGetProperty("count", out var rc) && rc.ValueKind == JsonValueKind.Number)
                r.RatingCount = rc.GetInt32();
        }

        // 封面：豆瓣默认给 s_ratio_poster（小图），换成大图
        if (item.TryGetProperty("cover_url", out var cu))
            r.PosterUrl = EnlargePoster(cu.GetString());

        // 演职人员：abstract_2 形如 "郭帆 / 吴京 / 刘德华 / ..."，第一段是导演，其余为主演
        if (!string.IsNullOrWhiteSpace(creditText))
        {
            var names = creditText.Split('/').Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (names.Count > 0) r.Director = names[0];
            if (names.Count > 1) r.Cast = string.Join(", ", names.Skip(1).Take(10));
        }

        return r;
    }

    private static string ToIdString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.String => el.GetString() ?? "",
        _ => ""
    };

    /// <summary>
    /// 清洗标题并剥离尾随年份。豆瓣标题形如「流浪地球2‎ (2023)」，其中的 U+200E（左至右标记）
    /// 会导致字符串比较与归一化结果和数据库里的片名对不上，必须剔除。
    /// </summary>
    /// <summary>
    /// Unicode 双向控制符 / 零宽字符（U+200B–U+200F、U+202A–U+202E、U+FEFF）。
    /// 注意：这里刻意用码点构造而不用 C# 的 <c>\u200e</c> 字面量——
    /// 该类格式字符属于 Unicode Cf 类别，编译器在词法阶段对它的处理与常规字符不同，
    /// 源码里写 <c>"\u200e"</c> 容易被当成空串，导致清洗逻辑静默失效。
    /// </summary>
    private static readonly Regex BidiMarks = new(@"[\u200B-\u200F\u202A-\u202E\uFEFF]", RegexOptions.Compiled);

    private static string StripBidi(string s) => BidiMarks.Replace(s, "");

    public static string CleanTitle(string raw, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = StripBidi(raw).Trim();
        var m = Regex.Match(s, @"\s*[\(（]\s*(\d{4})\s*[\)）]\s*$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var y))
        {
            year = y;
            s = s.Substring(0, m.Index).Trim();
        }
        return StripBidi(s).Trim();
    }

    private static readonly HashSet<string> Regions = new(StringComparer.Ordinal)
    {
        "中国大陆", "中国", "美国", "日本", "韩国", "英国", "法国", "德国", "印度", "加拿大",
        "意大利", "西班牙", "俄罗斯", "泰国", "中国香港", "香港", "中国台湾", "台湾", "中国澳门", "澳门",
        "澳大利亚", "新西兰", "新加坡", "马来西亚", "印度尼西亚", "菲律宾", "越南", "伊朗", "以色列",
        "土耳其", "巴西", "墨西哥", "阿根廷", "波兰", "捷克", "匈牙利", "丹麦", "瑞典", "挪威",
        "芬兰", "荷兰", "比利时", "瑞士", "奥地利", "希腊", "葡萄牙", "爱尔兰", "南非", "埃及"
    };

    /// <summary>是否像地区名：命中地区清单，或是“不超过 4 个汉字且不含数字/版本词”的短片段。</summary>
    private static bool LooksLikeRegion(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (Regions.Contains(s)) return true;
        // 兜底：纯中文且很短（如「中国大陆」「美国」），且不是片长/类型
        if (s.Length <= 6 && s.All(c => c >= 0x4e00 && c <= 0x9fff) &&
            !s.Contains("分钟") && !s.Contains("版")) return true;
        return false;
    }

    private static readonly HashSet<string> NotOriginalTitle = new(StringComparer.OrdinalIgnoreCase)
    {
        "3D", "2D", "IMAX", "4K", "BD", "DVD", "HD", "国语", "粤语", "原声", "中字", "双语"
    };

    /// <summary>是否可作为原名：含拉丁字母或数字、长度&gt;1，且不是版本/语言标签。</summary>
    private static bool IsLatinTitle(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length <= 1) return false;
        if (NotOriginalTitle.Contains(s)) return false;
        if (s.Any(c => c >= 0x4e00 && c <= 0x9fff)) return false; // 含汉字就不是原名
        var hasLetterOrDigit = s.Any(char.IsLetterOrDigit);
        return hasLetterOrDigit;
    }

    /// <summary>豆瓣封面图放大：s_ratio_poster / m 后缀 → l（大图）。</summary>
    private static string? EnlargePoster(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return url.Replace("/s_ratio_poster/", "/l_ratio_poster/")
                  .Replace("/m_ratio_poster/", "/l_ratio_poster/")
                  .Replace("/m/", "/l/")
                  .Replace("/s/", "/l/");
    }
}
