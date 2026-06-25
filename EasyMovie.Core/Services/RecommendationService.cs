using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Enums;

namespace EasyMovie.Core.Services;

/// <summary>
/// 电影推荐服务 - 基于本地算法（同导演、同类型、同国家、评分相关性）
/// </summary>
public class RecommendationService : IRecommendationService
{
    private readonly IMovieRepository _movieRepo;

    public RecommendationService(IMovieRepository movieRepo)
    {
        _movieRepo = movieRepo;
    }

    public async Task<List<RecommendedMovie>> GetRecommendationsAsync(int topN = 20)
    {
        var allMovies = await _movieRepo.GetAllAsync();
        if (allMovies.Count == 0) return new List<RecommendedMovie>();

        var scored = new Dictionary<int, (double score, List<string> reasons)>();

        // 以已看/已评分/收藏的电影作为偏好依据
        var watched = allMovies.Where(m =>
            m.WatchStatus == WatchStatus.Watched ||
            m.Rating.HasValue ||
            m.IsFavorite).ToList();

        // 候选池：想看状态的电影（排除已看的）
        var candidates = allMovies.Where(m => m.WatchStatus != WatchStatus.Watched).ToList();

        // 如果没有偏好依据，返回高分+近期电影
        if (watched.Count == 0)
        {
            return allMovies
                .OrderByDescending(m => m.Rating ?? 0)
                .ThenByDescending(m => m.Year)
                .Take(topN)
                .Select(m => new RecommendedMovie
                {
                    Movie = m,
                    Reason = m.Rating >= 7 ? "高分佳片" : (m.Year >= DateTime.UtcNow.Year - 1 ? "近期热门" : "猜你喜欢"),
                    Score = (m.Rating ?? 5) + (m.Year >= DateTime.UtcNow.Year - 1 ? 1 : 0)
                })
                .ToList();
        }

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

        // 2. 同类型推荐
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
            var catName = movie.Category?.Name ?? "同类型";
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
            .Where(m => m.MovieTags != null)
            .SelectMany(m => m.MovieTags.Select(mt => mt.TagId))
            .ToHashSet();

        foreach (var movie in candidates)
        {
            if (movie.MovieTags == null) continue;
            var matchTags = movie.MovieTags.Where(mt => watchedTagIds.Contains(mt.TagId)).ToList();
            if (matchTags.Count == 0) continue;
            if (!scored.ContainsKey(movie.Id))
                scored[movie.Id] = (0, new List<string>());
            var entry = scored[movie.Id];
            entry.score += matchTags.Count * 1.5;
            var tagNames = matchTags.Select(mt => mt.Tag?.Name).Where(n => n != null).Take(3);
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

        // 6. 收藏加权
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
            var movie = candidates.FirstOrDefault(m => m.Id == kvp.Key);
            if (movie == null) continue;
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

        // 7. 组装结果
        var result = scored
            .Select(kvp =>
            {
                var movie = candidates.FirstOrDefault(m => m.Id == kvp.Key);
                return new RecommendedMovie
                {
                    Movie = movie!,
                    Reason = string.Join(" | ", kvp.Value.reasons.Take(2)),
                    Score = Math.Round(kvp.Value.score, 1)
                };
            })
            .Where(r => r.Movie != null)
            .OrderByDescending(r => r.Score)
            .Take(topN)
            .ToList();

        // 8. 补充高分电影
        if (result.Count < topN)
        {
            var existingIds = result.Select(r => r.Movie.Id).ToHashSet();
            var fillers = candidates
                .Where(m => !existingIds.Contains(m.Id) && m.Rating.HasValue && m.Rating >= 6)
                .OrderByDescending(m => m.Rating)
                .Take(topN - result.Count)
                .Select(m => new RecommendedMovie { Movie = m, Reason = "高分佳片", Score = m.Rating ?? 0 });
            result.AddRange(fillers);
        }

        // 9. 补充近期电影
        if (result.Count < topN)
        {
            var existingIds = result.Select(r => r.Movie.Id).ToHashSet();
            var yearFillers = allMovies
                .Where(m => !existingIds.Contains(m.Id) && m.Year > 0)
                .OrderByDescending(m => m.Year)
                .Take(topN - result.Count)
                .Select(m => new RecommendedMovie { Movie = m, Reason = "近期新片", Score = 0 });
            result.AddRange(yearFillers);
        }

        return result;
    }

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
