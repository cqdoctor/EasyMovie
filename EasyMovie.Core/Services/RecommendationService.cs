using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Enums;

namespace EasyMovie.Core.Services;

/// <summary>
/// 电影推荐服务 - 基于本地算法（同导演、同类型、同国家、评分相关性）
/// </summary>
/// <remarks>
/// 性能设计（2026-08-29 实测重构，基线见 Tests/Core.Tests/RecommendationBenchmarkTests.cs）：
///
/// 优化前 @2000 部：冷 2990 ms / 187.69 MB；@290 部（当前真实库）：冷 353 ms / 27.26 MB。两处根因——
///   1. 通过 GetAllAsync() 把全库影片连同 PosterData 读进内存，而最终只展示 20 部。
///      实测 96 KB/部（海报平均 86.4 KB），2000 部 ≈ 188 MB 读完即弃 99%。
///   2. 组装阶段对每个打分项执行 candidates.FirstOrDefault(m =&gt; m.Id == key)，
///      是 O(n²) 线性查找——这是热耗时 2270 ms 的主因。
///
/// 对策：
///   1. 两阶段查询：先用不含海报的窄投影（GetRecommendationDataAsync）算出 topN 的 Id 与分数，
///      再只对这 topN 部加载含海报的完整实体（GetByIdsAsync）。内存从 O(n) 降到 O(topN)。
///   2. candidates 建一次 Dictionary 索引，O(1) 取代 O(n) 查找。
///   3. 标签关联与分类/标签名称改为查小表，避免 Include 的 JOIN 笛卡尔积。
///
/// 算法评分逻辑与理由文本与优化前逐行一致，由测试中的参考实现
/// （ReferenceRecommendation）逐项守护，任何改动都必须保持结果相同。
/// </remarks>
public class RecommendationService : IRecommendationService
{
    private readonly IMovieRepository _movieRepo;

    public RecommendationService(IMovieRepository movieRepo)
    {
        _movieRepo = movieRepo;
    }

    /// <summary>打分中间结果：与 RecommendedMovie 的区别是此处只持有 Id，尚未加载完整实体。</summary>
    private sealed record ScoredItem(int Id, double Score, string Reason);

    public async Task<List<RecommendedMovie>> GetRecommendationsAsync(int topN = 20)
    {
        // 阶段一：只取算法所需字段，不读海报
        var data = await _movieRepo.GetRecommendationDataAsync();
        var all = data.Movies;
        if (all.Count == 0) return new List<RecommendedMovie>();

        // 影片 → 标签 Id 列表（按 MovieTags 表顺序，与 Include 加载顺序一致）
        var tagsByMovie = new Dictionary<int, List<int>>();
        foreach (var link in data.TagLinks)
        {
            if (!tagsByMovie.TryGetValue(link.MovieId, out var list))
                tagsByMovie[link.MovieId] = list = new List<int>();
            list.Add(link.TagId);
        }

        var watched = all.Where(m =>
            m.WatchStatus == WatchStatus.Watched ||
            m.Rating.HasValue ||
            m.IsFavorite).ToList();

        // 候选池：想看状态的电影（排除已看的）
        var candidates = all.Where(m => m.WatchStatus != WatchStatus.Watched).ToList();

        // 如果没有偏好依据，返回高分+近期电影
        if (watched.Count == 0)
        {
            var picked = all
                .OrderByDescending(m => m.Rating ?? 0)
                .ThenByDescending(m => m.Year)
                .Take(topN)
                .Select(m => new ScoredItem(
                    m.Id,
                    (m.Rating ?? 5) + (m.Year >= DateTime.UtcNow.Year - 1 ? 1 : 0),
                    m.Rating >= 7 ? "高分佳片" : (m.Year >= DateTime.UtcNow.Year - 1 ? "近期热门" : "猜你喜欢")))
                .ToList();
            return await MaterializeAsync(picked);
        }

        var scored = new Dictionary<int, (double score, List<string> reasons)>();

        // 1. 同导演推荐
        var watchedDirectors = watched
            .Where(m => !string.IsNullOrWhiteSpace(m.Director))
            .SelectMany(m => m.Director!.Split('/', ','))
            .Select(d => d.Trim())
            .Where(d => !string.IsNullOrEmpty(d))
            .ToHashSet();

        foreach (var movie in candidates)
        {
            if (string.IsNullOrWhiteSpace(movie.Director)) continue;
            var directors = movie.Director.Split('/', ',').Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d));
            foreach (var director in directors)
            {
                if (watchedDirectors.Contains(director))
                {
                    if (!scored.ContainsKey(movie.Id))
                        scored[movie.Id] = (0, new List<string>());
                    var entry = scored[movie.Id];
                    entry.score += 3.0;
                    if (!entry.reasons.Any(r => r.Contains(director)))
                        entry.reasons.Add($"同导演: {director}");
                    scored[movie.Id] = entry;
                }
            }
        }

        // 2. 同类型推荐（分类名称查小表，避免导航属性 JOIN）
        var watchedCategoryIds = watched
            .Where(m => m.CategoryId.HasValue)
            .Select(m => m.CategoryId!.Value)
            .ToHashSet();

        foreach (var movie in candidates)
        {
            if (!movie.CategoryId.HasValue || !watchedCategoryIds.Contains(movie.CategoryId.Value)) continue;
            if (!scored.ContainsKey(movie.Id))
                scored[movie.Id] = (0, new List<string>());
            var entry = scored[movie.Id];
            entry.score += 2.0;
            var catName = (movie.CategoryId.HasValue
                && data.CategoryNames.TryGetValue(movie.CategoryId.Value, out var cn))
                ? cn : "同类型";
            if (!entry.reasons.Any(r => r.Contains(catName)))
                entry.reasons.Add($"同类型: {catName}");
            scored[movie.Id] = entry;
        }

        // 3. 同国家推荐
        var watchedCountries = watched
            .Where(m => !string.IsNullOrWhiteSpace(m.Country))
            .SelectMany(m => m.Country!.Split('/', ' ', '·', ','))
            .Select(c => c.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .ToHashSet();

        foreach (var movie in candidates)
        {
            if (string.IsNullOrWhiteSpace(movie.Country)) continue;
            var countries = movie.Country.Split('/', ' ', '·', ',').Select(c => c.Trim()).Where(c => !string.IsNullOrEmpty(c));
            var matchCountries = countries.Where(c => watchedCountries.Contains(c)).ToList();
            if (matchCountries.Count == 0) continue;
            if (!scored.ContainsKey(movie.Id))
                scored[movie.Id] = (0, new List<string>());
            var entry = scored[movie.Id];
            entry.score += 1.5;
            var cName = matchCountries.First();
            if (!entry.reasons.Any(r => r.Contains(cName)))
                entry.reasons.Add($"同地区: {cName}");
            scored[movie.Id] = entry;
        }

        // 4. 同标签推荐
        var watchedTagIds = watched
            .SelectMany(m => tagsByMovie.TryGetValue(m.Id, out var tl) ? tl : Enumerable.Empty<int>())
            .ToHashSet();

        foreach (var movie in candidates)
        {
            if (!tagsByMovie.TryGetValue(movie.Id, out var movieTagIds)) continue;
            var matchTags = movieTagIds.Where(t => watchedTagIds.Contains(t)).ToList();
            if (matchTags.Count == 0) continue;
            if (!scored.ContainsKey(movie.Id))
                scored[movie.Id] = (0, new List<string>());
            var entry = scored[movie.Id];
            entry.score += matchTags.Count * 1.5;
            var tagNames = matchTags
                .Select(t => data.TagNames.TryGetValue(t, out var tn) ? tn : null)
                .Where(n => n != null)
                .Take(3);
            foreach (var tn in tagNames)
                if (!entry.reasons.Any(r => r.Contains(tn!)))
                    entry.reasons.Add($"同标签: {tn}");
            scored[movie.Id] = entry;
        }

        // 5. 评分加权
        foreach (var movie in candidates)
        {
            if (!movie.Rating.HasValue) continue;
            if (!scored.ContainsKey(movie.Id))
                scored[movie.Id] = (0, new List<string>());
            var entry = scored[movie.Id];
            var ratingBonus = (movie.Rating.Value - 5.0) * 0.5;
            if (ratingBonus > 0)
            {
                entry.score += ratingBonus;
                scored[movie.Id] = entry;
            }
        }

        // 6. 收藏加权（候选池建索引，取代优化前的 O(n) 线性查找）
        var candidateById = candidates.ToDictionary(m => m.Id);

        var favoriteDirectors = watched
            .Where(m => m.IsFavorite && !string.IsNullOrWhiteSpace(m.Director))
            .SelectMany(m => m.Director!.Split('/', ','))
            .Select(d => d.Trim())
            .Where(d => !string.IsNullOrEmpty(d))
            .ToHashSet();

        var favoriteCategoryIds = watched
            .Where(m => m.IsFavorite && m.CategoryId.HasValue)
            .Select(m => m.CategoryId!.Value)
            .ToHashSet();

        foreach (var kvp in scored.ToList())
        {
            if (!candidateById.TryGetValue(kvp.Key, out var movie)) continue;
            var bonus = 0.0;
            if (!string.IsNullOrWhiteSpace(movie.Director))
            {
                var dirs = movie.Director.Split('/', ',').Select(d => d.Trim());
                if (dirs.Any(d => favoriteDirectors.Contains(d)))
                    bonus += 2.0;
            }
            if (movie.CategoryId.HasValue && favoriteCategoryIds.Contains(movie.CategoryId.Value))
                bonus += 1.5;
            if (bonus > 0)
            {
                var entry = kvp.Value;
                entry.score += bonus;
                scored[kvp.Key] = entry;
            }
        }

        // 7. 组装打分结果（此处仍只有 Id，不加载实体）
        var top = scored
            .Select(kvp => new ScoredItem(
                kvp.Key,
                Math.Round(kvp.Value.score, 1),
                string.Join(" | ", kvp.Value.reasons.Take(2))))
            .OrderByDescending(r => r.Score)
            .Take(topN)
            .ToList();

        // 8. 补充高分电影
        if (top.Count < topN)
        {
            var existingIds = top.Select(r => r.Id).ToHashSet();
            var fillers = candidates
                .Where(m => !existingIds.Contains(m.Id) && m.Rating.HasValue && m.Rating >= 6)
                .OrderByDescending(m => m.Rating)
                .Take(topN - top.Count)
                .Select(m => new ScoredItem(m.Id, m.Rating ?? 0, "高分佳片"));
            top.AddRange(fillers);
        }

        // 9. 补充近期电影
        if (top.Count < topN)
        {
            var existingIds = top.Select(r => r.Id).ToHashSet();
            var yearFillers = all
                .Where(m => !existingIds.Contains(m.Id) && m.Year > 0)
                .OrderByDescending(m => m.Year)
                .Take(topN - top.Count)
                .Select(m => new ScoredItem(m.Id, 0, "近期新片"));
            top.AddRange(yearFillers);
        }

        // 阶段二：只为最终要展示的这 topN 部加载含海报的完整实体
        return await MaterializeAsync(top);
    }

    /// <summary>
    /// 按 Id 批量加载完整实体并按原顺序组装结果。
    /// 这是两阶段查询的第二阶段——只加载真正要展示给用户的那几部（默认 20 部）。
    /// </summary>
    private async Task<List<RecommendedMovie>> MaterializeAsync(List<ScoredItem> items)
    {
        if (items.Count == 0) return new List<RecommendedMovie>();

        var ids = items.Select(i => i.Id).Distinct().ToList();
        var movies = await _movieRepo.GetByIdsAsync(ids);
        var byId = movies.ToDictionary(m => m.Id);

        var result = new List<RecommendedMovie>(items.Count);
        foreach (var item in items)
        {
            if (!byId.TryGetValue(item.Id, out var movie)) continue;
            result.Add(new RecommendedMovie
            {
                Movie = movie,
                Reason = item.Reason,
                Score = item.Score
            });
        }
        return result;
    }

    // 以下三个方法当前无调用方（UI 只用 GetRecommendationsAsync），仍走 GetAllAsync 全量加载。
    // 若日后启用，需同步改为上面的两阶段查询，否则会重新引入全库海报入内存的问题。

    public async Task<List<RecommendedMovie>> GetBySameDirectorAsync(int topN = 10)
    {
        var allMovies = await _movieRepo.GetAllAsync();
        var watched = allMovies.Where(m => m.WatchStatus == WatchStatus.Watched || m.IsFavorite).ToList();
        var watchedDirectors = watched
            .Where(m => !string.IsNullOrWhiteSpace(m.Director))
            .SelectMany(m => m.Director!.Split('/', ','))
            .Select(d => d.Trim())
            .Where(d => !string.IsNullOrEmpty(d))
            .ToHashSet();

        return allMovies
            .Where(m => m.WatchStatus != WatchStatus.Watched && !string.IsNullOrWhiteSpace(m.Director))
            .Select(m => new
            {
                Movie = m,
                MatchDirs = m.Director!.Split('/', ',').Select(d => d.Trim()).Count(d => watchedDirectors.Contains(d))
            })
            .Where(x => x.MatchDirs > 0)
            .OrderByDescending(x => x.MatchDirs)
            .ThenByDescending(x => x.Movie.Rating)
            .Take(topN)
            .Select(x => new RecommendedMovie
            {
                Movie = x.Movie,
                Reason = $"同导演: {x.Movie.Director}",
                Score = x.MatchDirs * 3 + (x.Movie.Rating ?? 0) * 0.5
            })
            .ToList();
    }

    public async Task<List<RecommendedMovie>> GetBySameCategoryAsync(int topN = 10)
    {
        var allMovies = await _movieRepo.GetAllAsync();
        var watched = allMovies.Where(m => m.WatchStatus == WatchStatus.Watched || m.IsFavorite).ToList();
        var watchedCategoryIds = watched.Where(m => m.CategoryId.HasValue).Select(m => m.CategoryId!.Value).ToHashSet();

        return allMovies
            .Where(m => m.WatchStatus != WatchStatus.Watched && m.CategoryId.HasValue && watchedCategoryIds.Contains(m.CategoryId.Value))
            .OrderByDescending(m => m.Rating)
            .Take(topN)
            .Select(m => new RecommendedMovie
            {
                Movie = m,
                Reason = $"同类型: {m.Category?.Name}",
                Score = (m.Rating ?? 0) * 0.5 + 2
            })
            .ToList();
    }

    public async Task<List<RecommendedMovie>> GetHighRatedUnwatchedAsync(int topN = 10)
    {
        var allMovies = await _movieRepo.GetAllAsync();
        return allMovies
            .Where(m => m.WatchStatus != WatchStatus.Watched && m.Rating.HasValue && m.Rating >= 7)
            .OrderByDescending(m => m.Rating)
            .Take(topN)
            .Select(m => new RecommendedMovie
            {
                Movie = m,
                Reason = "高分佳片",
                Score = m.Rating ?? 0
            })
            .ToList();
    }
}
