using MovieManager.Core.Enums;
using MovieManager.Core.Models;

namespace MovieManager.Core.Interfaces;

/// <summary>
/// 电影仓储接口
/// </summary>
public interface IMovieRepository
{
    Task<Movie?> GetByIdAsync(int id);
    Task<List<Movie>> GetAllAsync();
    Task<List<Movie>> SearchAsync(string? keyword, int? categoryId, List<int>? tagIds,
        int? yearFrom, int? yearTo, int? ratingMin, WatchStatus? status,
        string? sortBy, bool sortDesc, int skip, int take);
    Task<int> CountAsync(string? keyword, int? categoryId, List<int>? tagIds,
        int? yearFrom, int? yearTo, int? ratingMin, WatchStatus? status);
    Task<Movie> AddAsync(Movie movie);
    Task<Movie> UpdateAsync(Movie movie);
    Task<bool> DeleteAsync(int id);
    Task<bool> ExistsAsync(int id);
}
