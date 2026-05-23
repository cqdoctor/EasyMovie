using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Data.Repositories;

public class MovieRepository : IMovieRepository
{
    private readonly MovieDbContext _context;

    public MovieRepository(MovieDbContext context)
    {
        _context = context;
    }

    public async Task<Movie?> GetByIdAsync(int id)
    {
        return await _context.Movies
            .Include(m => m.Category)
            .Include(m => m.MovieTags)
                .ThenInclude(mt => mt.Tag)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<List<Movie>> GetAllAsync()
    {
        return await _context.Movies
            .Include(m => m.Category)
            .Include(m => m.MovieTags)
                .ThenInclude(mt => mt.Tag)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Movie>> SearchAsync(
        string? keyword, int? categoryId, List<int>? tagIds,
        int? yearFrom, int? yearTo, int? ratingMin, int? ratingMax, WatchStatus? status,
        string? country, string? language, int? runtimeMin, int? runtimeMax, string? director,
        string? sortBy, bool sortDesc, int skip, int take)
    {
        var query = _context.Movies
            .Include(m => m.Category)
            .Include(m => m.MovieTags)
                .ThenInclude(mt => mt.Tag)
            .AsQueryable();

        // 关键词搜索（片名、导演、演员、拼音索引）
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim().ToLower();
            query = query.Where(m =>
                m.Title.Contains(kw) ||
                (m.OriginalTitle != null && m.OriginalTitle.Contains(kw)) ||
                (m.Director != null && m.Director.Contains(kw)) ||
                (m.Cast != null && m.Cast.Contains(kw)) ||
                (m.SearchIndex != null && m.SearchIndex.Contains(kw)));
        }

        // 分类筛选（-1 表示"未分类"，即 CategoryId 为 null）
        if (categoryId.HasValue)
        {
            if (categoryId.Value == -1)
                query = query.Where(m => !m.CategoryId.HasValue);
            else
                query = query.Where(m => m.CategoryId == categoryId.Value);
        }

        // 标签筛选（包含任一标签）
        if (tagIds is { Count: > 0 })
        {
            query = query.Where(m => m.MovieTags.Any(mt => tagIds.Contains(mt.TagId)));
        }

        // 年份范围
        if (yearFrom.HasValue)
            query = query.Where(m => m.Year >= yearFrom.Value);
        if (yearTo.HasValue)
            query = query.Where(m => m.Year <= yearTo.Value);

        // 评分范围
        if (ratingMin.HasValue)
            query = query.Where(m => m.Rating >= ratingMin.Value);
        if (ratingMax.HasValue)
            query = query.Where(m => m.Rating <= ratingMax.Value);

        // 观看状态
        if (status.HasValue)
            query = query.Where(m => m.WatchStatus == status.Value);

        // 国家
        if (!string.IsNullOrWhiteSpace(country))
            query = query.Where(m => m.Country != null && m.Country.Contains(country));

        // 语言
        if (!string.IsNullOrWhiteSpace(language))
            query = query.Where(m => m.Language != null && m.Language.Contains(language));

        // 片长范围
        if (runtimeMin.HasValue)
            query = query.Where(m => m.Runtime >= runtimeMin.Value);
        if (runtimeMax.HasValue)
            query = query.Where(m => m.Runtime <= runtimeMax.Value);

        // 导演
        if (!string.IsNullOrWhiteSpace(director))
            query = query.Where(m => m.Director != null && m.Director.Contains(director));

        // 排序
        query = sortBy?.ToLowerInvariant() switch
        {
            "title" => sortDesc
                ? query.OrderByDescending(m => m.Title)
                : query.OrderBy(m => m.Title),
            "year" => sortDesc
                ? query.OrderByDescending(m => m.Year)
                : query.OrderBy(m => m.Year),
            "rating" => sortDesc
                ? query.OrderByDescending(m => m.Rating)
                : query.OrderBy(m => m.Rating),
            "createdat" => sortDesc
                ? query.OrderByDescending(m => m.CreatedAt)
                : query.OrderBy(m => m.CreatedAt),
            _ => sortDesc
                ? query.OrderByDescending(m => m.CreatedAt)
                : query.OrderBy(m => m.CreatedAt)
        };

        return await query.Skip(skip).Take(take).ToListAsync();
    }

    public async Task<int> CountAsync(
        string? keyword, int? categoryId, List<int>? tagIds,
        int? yearFrom, int? yearTo, int? ratingMin, int? ratingMax, WatchStatus? status,
        string? country, string? language, int? runtimeMin, int? runtimeMax, string? director)
    {
        var query = _context.Movies.AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim().ToLower();
            query = query.Where(m =>
                m.Title.Contains(kw) ||
                (m.OriginalTitle != null && m.OriginalTitle.Contains(kw)) ||
                (m.Director != null && m.Director.Contains(kw)) ||
                (m.Cast != null && m.Cast.Contains(kw)) ||
                (m.SearchIndex != null && m.SearchIndex.Contains(kw)));
        }

        if (categoryId.HasValue)
        {
            if (categoryId.Value == -1)
                query = query.Where(m => !m.CategoryId.HasValue);
            else
                query = query.Where(m => m.CategoryId == categoryId.Value);
        }

        if (tagIds is { Count: > 0 })
            query = query.Where(m => m.MovieTags.Any(mt => tagIds.Contains(mt.TagId)));

        if (yearFrom.HasValue)
            query = query.Where(m => m.Year >= yearFrom.Value);
        if (yearTo.HasValue)
            query = query.Where(m => m.Year <= yearTo.Value);

        if (ratingMin.HasValue)
            query = query.Where(m => m.Rating >= ratingMin.Value);
        if (ratingMax.HasValue)
            query = query.Where(m => m.Rating <= ratingMax.Value);

        if (status.HasValue)
            query = query.Where(m => m.WatchStatus == status.Value);

        if (!string.IsNullOrWhiteSpace(country))
            query = query.Where(m => m.Country != null && m.Country.Contains(country));

        if (!string.IsNullOrWhiteSpace(language))
            query = query.Where(m => m.Language != null && m.Language.Contains(language));

        if (runtimeMin.HasValue)
            query = query.Where(m => m.Runtime >= runtimeMin.Value);
        if (runtimeMax.HasValue)
            query = query.Where(m => m.Runtime <= runtimeMax.Value);

        if (!string.IsNullOrWhiteSpace(director))
            query = query.Where(m => m.Director != null && m.Director.Contains(director));

        return await query.CountAsync();
    }

    public async Task<Movie> AddAsync(Movie movie)
    {
        movie.CreatedAt = DateTime.UtcNow;
        movie.UpdatedAt = DateTime.UtcNow;
        _context.Movies.Add(movie);
        await _context.SaveChangesAsync();
        return movie;
    }

    public async Task<Movie> UpdateAsync(Movie movie)
    {
        movie.UpdatedAt = DateTime.UtcNow;
        _context.Movies.Update(movie);
        await _context.SaveChangesAsync();
        return movie;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var movie = await _context.Movies.FindAsync(id);
        if (movie == null) return false;
        _context.Movies.Remove(movie);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ExistsAsync(int id)
    {
        return await _context.Movies.AnyAsync(m => m.Id == id);
    }
}
