using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace EasyMovie.Core.Helpers;

/// <summary>
/// 演职人员字段清洗（当前主要是导演）。
///
/// 背景与动因（2026-08-31）：<c>CleanDirector</c> 此前在代码库里有 **4 份复制粘贴的实现**
/// （MovieListView、TmdbApiClient、OmdbApiClient、BaiduBaikeApiClient），
/// 配套的 <c>DirectorBlacklistTerms</c> 有 **5 份**（上述 4 处 + MovieInfoFetcher）。
/// 这些副本**已经漂移**，导致同一个导演字符串来自不同数据源会得到不同的清洗结果：
///
/// | 副本 | 分隔符 | "N/A" | 黑名单词数 |
/// |---|---|---|---|
/// | MovieListView     | <c>/ \ | \n \r ,</c>     | 无 | 26 |
/// | MovieInfoFetcher | —（仅校验）                | —  | 26 |
/// | TmdbApiClient    | <c>/ \ | \n \r ,</c>     | 无 | 25（缺「角色」） |
/// | BaiduBaikeClient | 同上 **+ <c>、</c>**      | 无 | 18 |
/// | OmdbApiClient    | <c>/ \ | \n \r ,</c>     | **有** | 16（缺 10 个词） |
///
/// 两个可观测的实际后果：
/// 1. TMDB 返回 <c>"N/A"</c> 时会被当成合法导演名写入数据库（只有 OMDb 那份做了拦截）。
/// 2. 中文顿号分隔的 <c>"张三、李四"</c> 因其余 3 份不认 <c>、</c>，会被整体存成一个假人名。
///
/// 因此本类**不是无行为变更的搬运**，而是一次有意的收敛：取各副本的**并集**语义
/// （分隔符并集 + 统一拦截 "N/A" + 黑名单词表并集 26 项），让所有数据源口径一致。
/// 合并后所有调用方共用同一实现，上述 1、2 两点在所有源上都被修复。
/// </summary>
public static class MovieCreditCleaner
{
    /// <summary>
    /// 导演字段中需要剔除的职业/职责标签（中英文）。
    /// 取原 5 份副本的**并集**（26 项）——各副本原有的缺失都在此补齐。
    /// </summary>
    public static readonly IReadOnlyCollection<string> DirectorBlacklistTerms =
        new ReadOnlyCollection<string>(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "screenplay", "story", "characters", "writer", "novel", "based on", "book",
            "director of photography", "editor", "producer", "executive producer",
            "music", "composer", "sound", "visual effects", "编剧", "原著", "角色",
            // 中文职业标签
            "制片人", "制片", "摄影", "剪辑", "音乐", "视觉效果", "艺术指导", "服装设计"
        }.OrderBy(x => x, StringComparer.Ordinal).ToList());

    /// <summary>多个导演的分隔符：斜杠、反斜杠、竖线、换行、逗号，以及中文顿号。</summary>
    private static readonly char[] NameSeparators = { '/', '\\', '|', '\n', '\r', ',', '、' };

    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private static readonly Regex BareYear = new(@"^\d{4}$", RegexOptions.Compiled);
    private static readonly Regex StartsWithYear = new(@"^\d{4}", RegexOptions.Compiled);

    /// <summary>导演名合理长度区间（用于清洗）。</summary>
    private const int MinNameLength = 2;
    private const int MaxNameLength = 30;

    /// <summary>校验用途的导演名长度上限（MovieInfoFetcher 口径，比清洗更宽松）。</summary>
    private const int MaxNameLengthForValidation = 60;

    /// <summary>最多保留的导演人数。</summary>
    private const int MaxDirectors = 3;

    /// <summary>
    /// 清理导演字段：去掉 HTML 标签、职业说明、非导演人员、日期，只保留人名。
    /// 多个导演以 " / " 分隔，最多保留前 3 个。
    /// </summary>
    /// <param name="value">原始导演字段（可为 null）。</param>
    /// <returns>清洗后的导演字符串；输入为空白时原样返回。</returns>
    public static string CleanDirector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value!;
        value = TextCleaner.StripHtml(value);

        // OMDb 用 "N/A" 表示缺失（原仅 OMDb 副本拦截，现统一）：不处理会被当成合法人名存库
        if (value == "N/A") return "";

        var parts = value.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var names = parts.Where(IsPlausibleNamePart).ToList();

        // 若所有段落都被职业标签污染，退而求其次：截取标签之前的人名前缀
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
                    if (!string.IsNullOrWhiteSpace(name)
                        && name.Length <= MaxNameLength
                        && !StartsWithYear.IsMatch(name))
                        names.Add(name);
                }
            }
        }

        return string.Join(" / ", names.Take(MaxDirectors));
    }

    /// <summary>
    /// 判断导演字符串是否可信（非空、非日期、非年份、不含职业标签、长度合理）。
    /// 合并自 MovieInfoFetcher.IsDirectorValid，长度上限沿用其较宽松的 60。
    /// </summary>
    public static bool IsPlausibleDirector(string? director)
    {
        if (string.IsNullOrWhiteSpace(director)) return false;
        if (IsoDate.IsMatch(director)) return false;
        if (BareYear.IsMatch(director)) return false;
        if (DirectorBlacklistTerms.Any(b => director.Contains(b, StringComparison.OrdinalIgnoreCase))) return false;
        return director.Length >= MinNameLength && director.Length <= MaxNameLengthForValidation;
    }

    private static bool IsPlausibleNamePart(string part)
        => !DirectorBlacklistTerms.Any(b => part.Contains(b, StringComparison.OrdinalIgnoreCase))
           && !IsoDate.IsMatch(part)
           && !BareYear.IsMatch(part)
           && part.Length >= MinNameLength
           && part.Length <= MaxNameLength;
}
