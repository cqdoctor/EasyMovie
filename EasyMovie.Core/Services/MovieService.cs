using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Core.Services;

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

    public async Task<string?> GetFilePathAsync(int id)
    {
        return await _movieRepo.GetFilePathAsync(id);
    }

    public async Task<List<Movie>> GetAllAsync()
    {
        return await _movieRepo.GetAllAsync();
    }

    public async Task<(List<Movie> Movies, int TotalCount)> SearchAsync(
        string? keyword, int? categoryId, List<int>? tagIds,
        int? yearFrom, int? yearTo, int? ratingMin, int? ratingMax, WatchStatus? status,
        List<string>? countries, List<string>? languages, int? runtimeMin, int? runtimeMax, List<string>? directors,
        string? sortBy, bool sortDesc, int page, int pageSize, bool? isFavorite = null)
    {
        var totalCount = await _movieRepo.CountAsync(keyword, categoryId, tagIds,
            yearFrom, yearTo, ratingMin, ratingMax, status, countries, languages, runtimeMin, runtimeMax, directors,
            isFavorite);

        var skip = (page - 1) * pageSize;
        var movies = await _movieRepo.SearchAsync(keyword, categoryId, tagIds,
            yearFrom, yearTo, ratingMin, ratingMax, status, countries, languages, runtimeMin, runtimeMax, directors,
            sortBy, sortDesc, skip, pageSize, isFavorite);

        return (movies, totalCount);
    }

    public async Task<Movie> AddAsync(Movie movie)
    {
        // 验证必填字段
        if (string.IsNullOrWhiteSpace(movie.Title))
            throw new ArgumentException("电影标题不能为空");
        if (movie.Year != 0 && movie.Year < 1888)
            throw new ArgumentOutOfRangeException(nameof(movie.Year), "电影年份不合理");
        if (movie.Year != 0 && movie.Year > DateTime.Now.Year + 5)
            throw new ArgumentOutOfRangeException(nameof(movie.Year), "电影年份不合理");

        // 自动生成搜索索引
        movie.SearchIndex = PinyinIndexHelper.BuildSearchIndex(movie.Title, movie.OriginalTitle, movie.Director, movie.Cast);

        return await _movieRepo.AddAsync(movie);
    }

    public async Task<Movie> UpdateAsync(Movie movie)
    {
        if (!await _movieRepo.ExistsAsync(movie.Id))
            throw new InvalidOperationException($"电影 ID {movie.Id} 不存在");

        if (string.IsNullOrWhiteSpace(movie.Title))
            throw new ArgumentException("电影标题不能为空");

        if (movie.Year != 0 && (movie.Year < 1888 || movie.Year > DateTime.Now.Year + 5))
            throw new ArgumentOutOfRangeException(nameof(movie.Year), "电影年份不合理");

        // 自动更新搜索索引
        movie.SearchIndex = PinyinIndexHelper.BuildSearchIndex(movie.Title, movie.OriginalTitle, movie.Director, movie.Cast);

        return await _movieRepo.UpdateAsync(movie);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        return await _movieRepo.DeleteAsync(id);
    }

    public async Task<bool> ExistsByFilePathAsync(string filePath)
    {
        return await _movieRepo.ExistsByFilePathAsync(filePath);
    }

    public async Task<bool> SetRatingAsync(int movieId, int? rating)
    {
        if (rating.HasValue && (rating < 1 || rating > 10))
            throw new ArgumentOutOfRangeException(nameof(rating), "评分必须在 1-10 之间");

        // 定点更新：旧实现是「GetByIdAsync 读整实体（含 86KB 海报）→ UpdateAsync 标脏全列」，
        // 改个评分会把 PosterData 拉进内存再整体回写一遍。改为直接下发 Rating 单列 UPDATE。
        return await _movieRepo.SetRatingAsync(movieId, rating);
    }

    public async Task<bool> SetWatchStatusAsync(int movieId, WatchStatus status, DateTime? watchDate)
    {
        // 注意：这里沿用既有的 DateTime.Now（本地时间）而非 UtcNow，刻意不改变语义，
        // 避免在本轮改动里引入时间基准回归（DateTime.Now/UtcNow 混用是另一项已知待办）。
        DateTime? effectiveDate = status == WatchStatus.Watched ? (watchDate ?? DateTime.Now) : null;
        return await _movieRepo.SetWatchStatusAsync(movieId, status, effectiveDate);
    }

    public async Task<bool> ToggleFavoriteAsync(int movieId)
    {
        // 直接在 SQL 里对列现值求反（IsFavorite = NOT IsFavorite），连「先读旧值」都省了。
        return await _movieRepo.ToggleFavoriteAsync(movieId);
    }

    public async Task<bool> UpdateNotesAsync(int movieId, string? notes)
    {
        if (notes?.Length > 2000)
            throw new ArgumentException("笔记不能超过 2000 字");

        return await _movieRepo.SetNotesAsync(movieId, notes);
    }

    public async Task<bool> SetCategoryAsync(int movieId, int? categoryId)
    {
        return await _movieRepo.SetCategoryIdAsync(movieId, categoryId);
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
        return await _movieRepo.CountAsync(null, null, null, null, null, null, null, null, null, null, null, null, null);
    }
}
