using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;

namespace EasyMovie.Core.Services;

/// <summary>
/// 重复电影检测服务（纯算法，不依赖数据库）
/// </summary>
public class DuplicateDetectionService
{
    /// <summary>检测进度回调</summary>
    public event Action<int, int>? ProgressChanged;

    /// <summary>
    /// 检测重复电影
    /// </summary>
    /// <param name="movies">待检测的电影列表（需包含 MovieTags）</param>
    public List<DuplicateGroup> Detect(List<Movie> movies)
    {
        var total = movies.Count;
        var groups = new List<DuplicateGroup>();
        var matchedIds = new HashSet<int>();

        // 1. 外部ID匹配（最高优先级）
        DetectByExternalId(movies, groups, matchedIds);
        ProgressChanged?.Invoke(total / 3, total);

        // 2. 精确匹配（标题+年份完全相同）
        DetectExact(movies, groups, matchedIds);
        ProgressChanged?.Invoke(total * 2 / 3, total);

        // 3. 模糊匹配（标题相似度≥85% + 年份相同）
        DetectFuzzy(movies, groups, matchedIds);
        ProgressChanged?.Invoke(total, total);

        // 自动选择每组中信息最完整的作为主记录
        foreach (var group in groups)
        {
            group.SelectedPrimaryId = group.Movies
                .OrderByDescending(m => m.CompletenessScore)
                .First().MovieId;
        }

        return groups;
    }

    #region 检测算法

    private void DetectByExternalId(List<Movie> movies, List<DuplicateGroup> groups, HashSet<int> matchedIds)
    {
        // 按 DoubanId 分组
        var byDouban = movies
            .Where(m => !string.IsNullOrEmpty(m.DoubanId))
            .GroupBy(m => m.DoubanId)
            .Where(g => g.Count() > 1);

        foreach (var g in byDouban)
        {
            var ids = g.Select(m => m.Id).ToList();
            if (ids.Any(id => matchedIds.Contains(id))) continue;

            var group = CreateGroup(g.ToList(), DuplicateMatchType.ExternalId,
                $"Douban:{g.Key}");
            groups.Add(group);
            foreach (var id in ids) matchedIds.Add(id);
        }

        // 按 TmdbId 分组
        var byTmdb = movies
            .Where(m => !string.IsNullOrEmpty(m.TmdbId))
            .GroupBy(m => m.TmdbId)
            .Where(g => g.Count() > 1);

        foreach (var g in byTmdb)
        {
            var ids = g.Select(m => m.Id).ToList();
            if (ids.Any(id => matchedIds.Contains(id))) continue;

            var group = CreateGroup(g.ToList(), DuplicateMatchType.ExternalId,
                $"TMDB:{g.Key}");
            groups.Add(group);
            foreach (var id in ids) matchedIds.Add(id);
        }
    }

    private void DetectExact(List<Movie> movies, List<DuplicateGroup> groups, HashSet<int> matchedIds)
    {
        var byTitleYear = movies
            .Where(m => !matchedIds.Contains(m.Id))
            .GroupBy(m => new { Title = NormalizeTitle(m.Title), m.Year })
            .Where(g => g.Count() > 1);

        foreach (var g in byTitleYear)
        {
            var group = CreateGroup(g.ToList(), DuplicateMatchType.Exact,
                $"{g.Key.Title}/{g.Key.Year}");
            groups.Add(group);
            foreach (var m in g) matchedIds.Add(m.Id);
        }
    }

    private void DetectFuzzy(List<Movie> movies, List<DuplicateGroup> groups, HashSet<int> matchedIds)
    {
        // 按年份分组，只比较同年电影
        var remaining = movies.Where(m => !matchedIds.Contains(m.Id)).ToList();
        var byYear = remaining.GroupBy(m => m.Year);

        foreach (var yearGroup in byYear)
        {
            var list = yearGroup.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (matchedIds.Contains(list[i].Id)) continue;
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (matchedIds.Contains(list[j].Id)) continue;

                    var sim = GetSimilarity(
                        NormalizeTitle(list[i].Title),
                        NormalizeTitle(list[j].Title));

                    if (sim >= 0.85)
                    {
                        var pair = new List<Movie> { list[i], list[j] };
                        var group = CreateGroup(pair, DuplicateMatchType.Fuzzy,
                            $"{list[i].Title}/{list[i].Year}");
                        groups.Add(group);
                        matchedIds.Add(list[i].Id);
                        matchedIds.Add(list[j].Id);
                        break;
                    }
                }
            }
        }
    }

    #endregion

    #region 工具方法

    private static DuplicateGroup CreateGroup(List<Movie> movies, DuplicateMatchType matchType, string groupKey)
    {
        return new DuplicateGroup
        {
            GroupKey = groupKey,
            MatchType = matchType,
            Movies = movies.Select(m => new DuplicateMovieItem
            {
                MovieId = m.Id,
                Title = m.Title,
                OriginalTitle = m.OriginalTitle,
                Year = m.Year,
                Director = m.Director,
                Rating = m.Rating,
                WatchStatus = m.WatchStatus,
                PosterData = m.PosterData,
                HasNotes = !string.IsNullOrEmpty(m.Notes),
                TagCount = m.MovieTags?.Count ?? 0,
                IsFavorite = m.IsFavorite,
                FilePath = m.FilePath
            }).ToList()
        };
    }

    /// <summary>
    /// 标题标准化：去除空格、统一标点、全角转半角
    /// </summary>
    internal static string NormalizeTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var s = title.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= 0xFF01 && c <= 0xFF5E)
                sb.Append((char)(c - 0xFEE0));
            else if (c == 0x3000)
                sb.Append(' ');
            else
                sb.Append(c);
        }
        var result = sb.ToString();
        result = System.Text.RegularExpressions.Regex.Replace(result, @"[\s\-_\.:：·,，、!！?？()（）\[\]【】""''""]", "");
        return result;
    }

    /// <summary>
    /// 计算两个字符串的相似度（基于 Levenshtein 距离）
    /// </summary>
    internal static double GetSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0;
        if (s1 == s2) return 1.0;

        var dist = LevenshteinDistance(s1, s2);
        var maxLen = Math.Max(s1.Length, s2.Length);
        return 1.0 - (double)dist / maxLen;
    }

    /// <summary>
    /// Levenshtein 编辑距离
    /// </summary>
    internal static int LevenshteinDistance(string s1, string s2)
    {
        var len1 = s1.Length;
        var len2 = s2.Length;
        var dp = new int[len1 + 1, len2 + 1];

        for (int i = 0; i <= len1; i++) dp[i, 0] = i;
        for (int j = 0; j <= len2; j++) dp[0, j] = j;

        for (int i = 1; i <= len1; i++)
        {
            for (int j = 1; j <= len2; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        return dp[len1, len2];
    }

    #endregion
}
