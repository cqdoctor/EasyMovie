using MovieManager.Core.Enums;
using MovieManager.Core.Interfaces;
using MovieManager.Core.Models;

namespace MovieManager.Core.Services;

/// <summary>
/// 电影业务服务
/// </summary>
public class MovieService : IMovieService
{
    private readonly IMovieRepository _movieRepo;
    private readonly ITagRepository _tagRepo;

    public MovieService(IMovieRepository movieRepo, ITagRepository tagRepo)
    {
        _movieRepo = movieRepo;
        _tagRepo = tagRepo;
    }

    public async Task<Movie?> GetByIdAsync(int id)
    {
        return await _movieRepo.GetByIdAsync(id);
    }

    public async Task<List<Movie>> GetAllAsync()
    {
        return await _movieRepo.GetAllAsync();
    }

    public async Task<(List<Movie> Movies, int TotalCount)> SearchAsync(
        string? keyword, int? categoryId, List<int>? tagIds,
        int? yearFrom, int? yearTo, int? ratingMin, WatchStatus? status,
        string? sortBy, bool sortDesc, int page, int pageSize)
    {
        var totalCount = await _movieRepo.CountAsync(keyword, categoryId, tagIds,
            yearFrom, yearTo, ratingMin, status);

        var skip = (page - 1) * pageSize;
        var movies = await _movieRepo.SearchAsync(keyword, categoryId, tagIds,
            yearFrom, yearTo, ratingMin, status, sortBy, sortDesc, skip, pageSize);

        return (movies, totalCount);
    }

    public async Task<Movie> AddAsync(Movie movie)
    {
        // 验证必填字段
        if (string.IsNullOrWhiteSpace(movie.Title))
            throw new ArgumentException("电影标题不能为空");
        if (movie.Year != 0 && movie.Year < 1888)
            throw new ArgumentOutOfRangeException(nameof(movie.Year), "电影年份不合理");

        return await _movieRepo.AddAsync(movie);
    }

    public async Task<Movie> UpdateAsync(Movie movie)
    {
        if (!await _movieRepo.ExistsAsync(movie.Id))
            throw new InvalidOperationException($"电影 ID {movie.Id} 不存在");

        if (string.IsNullOrWhiteSpace(movie.Title))
            throw new ArgumentException("电影标题不能为空");

        return await _movieRepo.UpdateAsync(movie);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        return await _movieRepo.DeleteAsync(id);
    }

    public async Task<bool> SetRatingAsync(int movieId, int? rating)
    {
        if (rating.HasValue && (rating < 1 || rating > 10))
            throw new ArgumentOutOfRangeException(nameof(rating), "评分必须在 1-10 之间");

        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null) return false;

        movie.Rating = rating;
        await _movieRepo.UpdateAsync(movie);
        return true;
    }

    public async Task<bool> SetWatchStatusAsync(int movieId, WatchStatus status, DateTime? watchDate)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null) return false;

        movie.WatchStatus = status;
        movie.WatchDate = status == WatchStatus.Watched ? (watchDate ?? DateTime.Now) : null;
        await _movieRepo.UpdateAsync(movie);
        return true;
    }

    public async Task<bool> ToggleFavoriteAsync(int movieId)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null) return false;

        movie.IsFavorite = !movie.IsFavorite;
        await _movieRepo.UpdateAsync(movie);
        return true;
    }

    public async Task<bool> UpdateNotesAsync(int movieId, string? notes)
    {
        if (notes?.Length > 2000)
            throw new ArgumentException("笔记不能超过 2000 字");

        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null) return false;

        movie.Notes = notes;
        await _movieRepo.UpdateAsync(movie);
        return true;
    }

    public async Task<bool> SetCategoryAsync(int movieId, int? categoryId)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null) return false;

        movie.CategoryId = categoryId;
        await _movieRepo.UpdateAsync(movie);
        return true;
    }

    public async Task SetTagsAsync(int movieId, List<int> tagIds)
    {
        var existingTags = await _tagRepo.GetTagsForMovieAsync(movieId);
        var existingIds = existingTags.Select(t => t.Id).ToList();

        var toAdd = tagIds.Except(existingIds).ToList();
        var toRemove = existingIds.Except(tagIds).ToList();

        if (toAdd.Any())
            await _tagRepo.AddMovieTagsAsync(movieId, toAdd);
        if (toRemove.Any())
            await _tagRepo.RemoveMovieTagsAsync(movieId, toRemove);
    }

    public async Task<int> GetTotalCountAsync()
    {
        return await _movieRepo.CountAsync(null, null, null, null, null, null, null);
    }
}
