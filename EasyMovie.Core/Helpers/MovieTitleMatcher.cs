using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EasyMovie.Core.Interfaces;

namespace EasyMovie.Core.Helpers;

/// <summary>
/// 片名匹配守卫：归一化 + 相似度 + 年份窗口，用于 TMDB 搜索结果挑选。
///
/// 背景（2026-11-06 实测）：搜「烈火中」时 TMDB 首条返回 1965 年《烈火中永生》，
/// 正解 id=946215 在第 2 条。仅靠「包含关系 + 同一年份」会把错配当命中，
/// 因此这类候选标记为 <see cref="Candidate.NeedsEvidence"/>，调用方必须先核对
/// 片长/导演/主演任一项（<see cref="MetadataOverlap"/>）才能采用，否则顺延到下一条。
///
/// 2026-11-07 追加：阈值 0.72 → 0.60（救回译名差异，如《史泰龙的传奇》0.625）；
/// 精确标题命中再做导演/原语言/片长软校验（挡住同名不同片，如《指尖》）。
/// 两道防线彼此独立：包含关系 + 同年仍必须有片长/导演/主演佐证。
/// </summary>
public static class MovieTitleMatcher
{
    /// <summary>可接受的最低相似度（0~1）。低于此值的候选视为「不同作品」。</summary>
    public const double Threshold = 0.60;

    /// <summary>排名后的候选。<see cref="NeedsEvidence"/> 为 true 时不得直接采用。</summary>
    public sealed record Candidate(MovieSearchResult Result, double Score, bool Exact, bool NeedsEvidence);

    /// <summary>被守卫拒绝的候选，供调用方汇总成一条 Info/Warn 日志。</summary>
    public sealed record Rejected(string Title, string? ExternalId, double Score, int YearDiff, string Reason);

    // 文件名常见噪声（归一化前先剔除），避免「中字/4K/BluRay」拉低相似度。
    private static readonly string[] NoiseTokens =
    {
        "中文字幕", "中字", "字幕", "国语", "粤语", "国粤双语", "双语", "原声",
        "bluray", "blu-ray", "web-dl", "webrip", "hdtv", "remux", "hdr", "hevc",
        "x264", "x265", "ddp", "atmos", "aac", "1080p", "2160p", "720p", "4k", "8k", "hd"
    };

    /// <summary>归一化：全角转半角 → 小写 → 去噪声词 → 去年份/括号 → 只保留文字与数字。</summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title)
        {
            // 全角 ASCII（！～）与全角空格 → 半角
            if (ch >= '\uFF01' && ch <= '\uFF5E') sb.Append((char)(ch - 0xFEE0));
            else if (ch == '\u3000') sb.Append(' ');
            else sb.Append(ch);
        }
        var s = sb.ToString().ToLowerInvariant();
        foreach (var token in NoiseTokens) s = s.Replace(token, "", StringComparison.Ordinal);
        // 去年份段（1900-2099）与括号/标点，只保留中日韩文字与字母数字
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(19|20)\d{2}", " ");
        var result = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) result.Append(ch);
        }
        return result.ToString();
    }

    /// <summary>归一化后的编辑距离相似度（0~1）。任一为空返回 0。</summary>
    public static double Similarity(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1;
        var prev = new int[y.Length + 1];
        var cur = new int[y.Length + 1];
        for (var j = 0; j <= y.Length; j++) prev[j] = j;
        for (var i = 1; i <= x.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= y.Length; j++)
            {
                var cost = x[i - 1] == y[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        var distance = prev[y.Length];
        return 1.0 - (double)distance / Math.Max(x.Length, y.Length);
    }

    /// <summary>
    /// 按「完全匹配优先、相似度次之」排序候选；年份窗口 ±1，阈值以下直接丢弃。
    /// 包含关系且年份相同（如 烈火中 / 烈火中永生）标记 NeedsEvidence。
    /// </summary>
    /// <param name="aliases">可选：按 ExternalId 提供 TMDB 别名，参与打分（别名兜底轮次才传入）。</param>
    /// <param name="rejected">可选：收集被拒绝的候选及原因，供调用方汇总日志。</param>
    public static IReadOnlyList<Candidate> Rank(
        IEnumerable<MovieSearchResult> results, string? localTitle, int year, int max = 5,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? aliases = null,
        List<Rejected>? rejected = null)
    {
        var list = new List<Candidate>();
        var nt = Normalize(localTitle);
        if (nt.Length == 0) return list;

        foreach (var r in results.Take(max))
        {
            var titles = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.Title)) titles.Add(r.Title);
            if (!string.IsNullOrWhiteSpace(r.OriginalTitle)) titles.Add(r.OriginalTitle);
            if (aliases != null && !string.IsNullOrEmpty(r.ExternalId)
                && aliases.TryGetValue(r.ExternalId, out var alts))
            {
                foreach (var alt in alts)
                    if (!string.IsNullOrWhiteSpace(alt)) titles.Add(alt);
            }

            var score = 0.0;
            var exact = false;
            var contain = false;
            foreach (var title in titles)
            {
                var normalized = Normalize(title);
                if (normalized.Length == 0) continue;
                if (normalized == nt) exact = true;
                if (normalized.Contains(nt) || nt.Contains(normalized)) contain = true;
                score = Math.Max(score, Similarity(nt, normalized));
            }
            if (exact) score = 1.0;

            var yearDiff = year > 0 && r.Year > 0 ? Math.Abs(r.Year - year) : 0;
            if (year > 0 && r.Year > 0 && yearDiff > 1)
            {
                rejected?.Add(new Rejected(r.Title ?? "", r.ExternalId, score, yearDiff, "年份超容差"));
                continue;
            }

            var needsEvidence = !exact && contain && year > 0 && r.Year == year;
            if (!exact && !needsEvidence && score < Threshold)
            {
                rejected?.Add(new Rejected(r.Title ?? "", r.ExternalId, score, yearDiff, "相似度不足"));
                continue;
            }
            list.Add(new Candidate(r, score, exact, needsEvidence));
        }

        return list.OrderByDescending(c => c.Exact).ThenByDescending(c => c.Score).ToList();
    }

    /// <summary>取第一条可直接采用的候选（跳过需要额外证据的包含关系候选）。</summary>
    public static MovieSearchResult? Pick(IEnumerable<MovieSearchResult> results, string? localTitle, int year)
        => Rank(results, localTitle, year).FirstOrDefault(c => !c.NeedsEvidence)?.Result;

    /// <summary>
    /// 精确标题命中的软校验：导演 + 原语言两项都「可比较且不符」时判定为同名不同片。
    ///
    /// 为什么不是严格「三项全部不符」：Id 76《指尖》本地片长 105 与 TMDB 法国片
    /// 481157 的 105 恰好相同（本地片长是历史脏数据），若要求三项全不符会放行该错配。
    /// 因此以「导演 + 原语言」两条强信号作为降级条件，片长只写入日志说明；
    /// 只要语言或导演任一项相符（例如译名不同但语种一致），就不降级，避免误伤。
    /// 任一项不可比较（字段为空/无法映射）时同样不降级。
    /// </summary>
    /// <returns>通过返回 null；否则返回拒绝原因。</returns>
    public static string? ExactMismatchReason(
        int? localRuntime, string? localDirector, string? localLanguage,
        int? candidateRuntime, string? candidateDirector, string? candidateLanguage)
    {
        var localNames = Names(localDirector);
        var candidateNames = Names(candidateDirector);
        var directorComparable = localNames.Count > 0 && candidateNames.Count > 0;
        var directorMismatch = directorComparable && !localNames.Overlaps(candidateNames);

        var languageComparable = LanguageComparable(localLanguage) && !string.IsNullOrWhiteSpace(candidateLanguage);
        var languageMismatch = languageComparable && !LanguageMatches(localLanguage, candidateLanguage);

        var runtimeComparable = localRuntime is > 0 && candidateRuntime is > 0;
        var runtimeMismatch = runtimeComparable && Math.Abs(localRuntime!.Value - candidateRuntime!.Value) > 3;

        if (!directorComparable || !languageComparable || !directorMismatch || !languageMismatch)
            return null;

        var runtimeNote = !runtimeComparable
            ? "片长未知"
            : runtimeMismatch
                ? "片长不符"
                : $"片长 {localRuntime}≈{candidateRuntime} 相符但不足以佐证";
        return $"软校验不通过（导演、原语言不符；{runtimeNote}）";
    }

    /// <summary>
    /// 片长（±3 分钟）/ 导演 / 主演任一一致即视为同一部作品。
    /// 用于「包含关系且年份相同」的候选核对。
    /// </summary>
    public static bool MetadataOverlap(
        int? runtimeA, string? directorsA, string? castA,
        int? runtimeB, string? directorsB, string? castB)
    {
        if (runtimeA is > 0 && runtimeB is > 0 && Math.Abs(runtimeA.Value - runtimeB.Value) <= 3) return true;
        if (Names(directorsA).Overlaps(Names(directorsB))) return true;
        return Names(castA).Overlaps(Names(castB));
    }

    /// <summary>姓名列表是否有交集（导演/主演两处共用，精确命中的软校验也用它）。</summary>
    public static bool NamesOverlap(string? a, string? b) => Names(a).Overlaps(Names(b));

    /// <summary>片长是否吻合（任一方未知返回 false）。</summary>
    public static bool RuntimeMatches(int? a, int? b) => a is > 0 && b is > 0 && Math.Abs(a.Value - b.Value) <= 3;

    // ===== 语言软校验（本地中文语言名 vs TMDB original_language ISO-639-1）=====
    // 只收录常见产影语言；未收录时视为「不可比较」，不参与降级，避免误伤。
    private static readonly Dictionary<string, string[]> LanguageToIso = new(StringComparer.OrdinalIgnoreCase)
    {
        ["汉语"] = new[] { "zh", "cn" }, ["中文"] = new[] { "zh", "cn" }, ["国语"] = new[] { "zh", "cn" },
        ["普通话"] = new[] { "zh", "cn" }, ["华语"] = new[] { "zh", "cn" }, ["粤语"] = new[] { "cn", "zh" },
        ["英语"] = new[] { "en" }, ["泰语"] = new[] { "th" }, ["法语"] = new[] { "fr" },
        ["日语"] = new[] { "ja" }, ["韩语"] = new[] { "ko" }, ["朝鲜语"] = new[] { "ko" },
        ["俄语"] = new[] { "ru" }, ["西班牙语"] = new[] { "es" }, ["德语"] = new[] { "de" },
        ["意大利语"] = new[] { "it" }, ["葡萄牙语"] = new[] { "pt" }, ["印地语"] = new[] { "hi" },
        ["泰米尔语"] = new[] { "ta" }, ["泰卢固语"] = new[] { "te" }, ["土耳其语"] = new[] { "tr" },
        ["波兰语"] = new[] { "pl" }, ["荷兰语"] = new[] { "nl" }, ["瑞典语"] = new[] { "sv" },
        ["挪威语"] = new[] { "no", "nb" }, ["丹麦语"] = new[] { "da" }, ["芬兰语"] = new[] { "fi" },
        ["冰岛语"] = new[] { "is" }, ["阿拉伯语"] = new[] { "ar" }, ["希伯来语"] = new[] { "he", "iw" },
        ["波斯语"] = new[] { "fa" }, ["越南语"] = new[] { "vi" }, ["印尼语"] = new[] { "id" },
        ["印度尼西亚语"] = new[] { "id" }, ["马来语"] = new[] { "ms" }, ["菲律宾语"] = new[] { "tl" },
        ["他加禄语"] = new[] { "tl" }, ["乌克兰语"] = new[] { "uk" }, ["捷克语"] = new[] { "cs" },
        ["匈牙利语"] = new[] { "hu" }, ["希腊语"] = new[] { "el" }, ["罗马尼亚语"] = new[] { "ro" },
        ["保加利亚语"] = new[] { "bg" }, ["塞尔维亚语"] = new[] { "sr" }, ["克罗地亚语"] = new[] { "hr" },
        ["斯洛伐克语"] = new[] { "sk" }, ["斯洛文尼亚语"] = new[] { "sl" }, ["立陶宛语"] = new[] { "lt" },
        ["拉脱维亚语"] = new[] { "lv" }, ["爱沙尼亚语"] = new[] { "et" }, ["加泰罗尼亚语"] = new[] { "ca" },
        ["巴斯克语"] = new[] { "eu" }, ["加利西亚语"] = new[] { "gl" }, ["蒙古语"] = new[] { "mn" },
        ["藏语"] = new[] { "bo" }, ["哈萨克语"] = new[] { "kk" }, ["乌兹别克语"] = new[] { "uz" },
        ["阿塞拜疆语"] = new[] { "az" }, ["格鲁吉亚语"] = new[] { "ka" }, ["亚美尼亚语"] = new[] { "hy" },
        ["孟加拉语"] = new[] { "bn" }, ["乌尔都语"] = new[] { "ur" }, ["尼泊尔语"] = new[] { "ne" },
        ["僧伽罗语"] = new[] { "si" }, ["缅甸语"] = new[] { "my" }, ["高棉语"] = new[] { "km" },
        ["柬埔寨语"] = new[] { "km" }, ["老挝语"] = new[] { "lo" }, ["斯瓦希里语"] = new[] { "sw" },
        ["南非荷兰语"] = new[] { "af" }, ["拉丁语"] = new[] { "la" }, ["世界语"] = new[] { "eo" }
    };

    /// <summary>本地语言名是否可映射为 ISO 代码（可比较）。</summary>
    public static bool LanguageComparable(string? localLanguage) => LanguageTokens(localLanguage).Any(t => LanguageToIso.ContainsKey(t));

    /// <summary>本地语言名与 TMDB original_language 是否一致（任一 token 命中即算一致）。</summary>
    public static bool LanguageMatches(string? localLanguage, string? isoCode)
    {
        if (string.IsNullOrWhiteSpace(isoCode)) return false;
        foreach (var token in LanguageTokens(localLanguage))
        {
            if (LanguageToIso.TryGetValue(token, out var codes)
                && codes.Any(c => c.Equals(isoCode.Trim(), StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> LanguageTokens(string? localLanguage)
    {
        if (string.IsNullOrWhiteSpace(localLanguage)) yield break;
        foreach (var part in localLanguage.Split(new[] { ',', '，', '、', '/', '·', '|', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            if (token.Length > 0) yield return token;
        }
    }

    private static HashSet<string> Names(string? value)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return set;
        foreach (var part in value.Split(new[] { '/', '、', '，', ',', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim();
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }
}
