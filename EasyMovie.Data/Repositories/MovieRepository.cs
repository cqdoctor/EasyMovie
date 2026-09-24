using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
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
        // AsSplitQuery：把 MovieTags/Category 的 Include 拆成独立查询，避免" Movie × 标签数 "
        // 的笛卡尔积把 PosterData（占库 99.4%）按标签数重复物化——用户一打标签，单页 20 部
        // 就会重复读数百 KB 乃至数 MB。拆分后每部电影的海报只加载一次，标签/分类仍照常填充。
        // AsNoTracking：与其它只读查询（SearchAsync/GetByIdsAsync/CountAsync）保持一致。
        // 写路径不受影响——UpdateAsync 用 Movies.Update() 重新附着，DeleteAsync 用 FindAsync() 重取。
        return await _context.Movies
            .AsNoTracking()
            .Include(m => m.Category)
            .Include(m => m.MovieTags)
                .ThenInclude(mt => mt.Tag)
            .OrderByDescending(m => m.CreatedAt)
            .AsSplitQuery()
            .ToListAsync();
    }

    public async Task<List<Movie>> GetByIdsAsync(IEnumerable<int> ids)
    {
        var idList = ids as List<int> ?? ids.ToList();
        if (idList.Count == 0) return new List<Movie>();

        // AsSplitQuery：同下方 GetAllAsync，避免标签 JOIN 重复物化 PosterData。
        return await _context.Movies
            .AsNoTracking()
            .Where(m => idList.Contains(m.Id))
            .Include(m => m.Category)
            .Include(m => m.MovieTags)
                .ThenInclude(mt => mt.Tag)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <summary>
    /// 推荐专用：一次取回算法所需的全部轻量数据。
    /// 刻意不 Include 导航属性，改为分别查小表——避免 MovieTags 的 JOIN 笛卡尔积，
    /// 也避免把 PosterData（实测占库 99.4%）读进内存。
    /// </summary>
    public async Task<RecommendationData> GetRecommendationDataAsync()
    {
        var movies = await _context.Movies
            .AsNoTracking()
            .Select(m => new MovieRecommendRow
            {
                Id = m.Id,
                Year = m.Year,
                Rating = m.Rating,
                WatchStatus = m.WatchStatus,
                IsFavorite = m.IsFavorite,
                Director = m.Director,
                Country = m.Country,
                CategoryId = m.CategoryId,
                CreatedAt = m.CreatedAt
            })
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        var tagLinks = await _context.MovieTags
            .AsNoTracking()
            .Select(mt => new MovieTagLink { MovieId = mt.MovieId, TagId = mt.TagId })
            .ToListAsync();

        var categoryNames = await _context.Categories
            .AsNoTracking()
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        var tagNames = await _context.Tags
            .AsNoTracking()
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name);

        return new RecommendationData
        {
            Movies = movies,
            TagLinks = tagLinks,
            CategoryNames = categoryNames,
            TagNames = tagNames
        };
    }

    public async Task<List<Movie>> SearchAsync(
        string? keyword, int? categoryId, List<int>? tagIds,
        int? yearFrom, int? yearTo, int? ratingMin, int? ratingMax, WatchStatus? status,
        List<string>? countries, List<string>? languages, int? runtimeMin, int? runtimeMax, List<string>? directors,
        string? sortBy, bool sortDesc, int skip, int take, bool? isFavorite = null)
    {
        var query = _context.Movies
            .AsNoTracking()
            .Include(m => m.Category)
            .Include(m => m.MovieTags)
                .ThenInclude(mt => mt.Tag)
            .AsSplitQuery()
            .AsQueryable();

        // 关键词搜索（片名、导演、演员、拼音索引）
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim().ToLower();
            query = query.Where(m =>
                (m.Title != null && m.Title.ToLower().Contains(kw)) ||
                (m.OriginalTitle != null && m.OriginalTitle.ToLower().Contains(kw)) ||
                (m.Director != null && m.Director.ToLower().Contains(kw)) ||
                (m.Cast != null && m.Cast.ToLower().Contains(kw)) ||
                (m.SearchIndex != null && m.SearchIndex.ToLower().Contains(kw)));
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

        // 国家（多选 OR）
        if (countries is { Count: > 0 })
            query = query.Where(m => m.Country != null && countries.Any(c => m.Country.Contains(c)));

        // 语言（多选 OR）
        if (languages is { Count: > 0 })
            query = query.Where(m => m.Language != null && languages.Any(l => m.Language.Contains(l)));

        // 片长范围
        if (runtimeMin.HasValue)
            query = query.Where(m => m.Runtime >= runtimeMin.Value);
        if (runtimeMax.HasValue)
            query = query.Where(m => m.Runtime <= runtimeMax.Value);

        // 导演（多选 OR）
        if (directors is { Count: > 0 })
            query = query.Where(m => m.Director != null && directors.Any(d => m.Director.Contains(d)));

        if (isFavorite.HasValue)
            query = query.Where(m => m.IsFavorite == isFavorite.Value);

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
        List<string>? countries, List<string>? languages, int? runtimeMin, int? runtimeMax, List<string>? directors,
        bool? isFavorite = null)
    {
        var query = _context.Movies.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim().ToLower();
            query = query.Where(m =>
                (m.Title != null && m.Title.ToLower().Contains(kw)) ||
                (m.OriginalTitle != null && m.OriginalTitle.ToLower().Contains(kw)) ||
                (m.Director != null && m.Director.ToLower().Contains(kw)) ||
                (m.Cast != null && m.Cast.ToLower().Contains(kw)) ||
                (m.SearchIndex != null && m.SearchIndex.ToLower().Contains(kw)));
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

        if (countries is { Count: > 0 })
            query = query.Where(m => m.Country != null && countries.Any(c => m.Country.Contains(c)));

        if (languages is { Count: > 0 })
            query = query.Where(m => m.Language != null && languages.Any(l => m.Language.Contains(l)));

        if (runtimeMin.HasValue)
            query = query.Where(m => m.Runtime >= runtimeMin.Value);
        if (runtimeMax.HasValue)
            query = query.Where(m => m.Runtime <= runtimeMax.Value);

        if (directors is { Count: > 0 })
            query = query.Where(m => m.Director != null && directors.Any(d => m.Director.Contains(d)));

        if (isFavorite.HasValue)
            query = query.Where(m => m.IsFavorite == isFavorite.Value);

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

    public async Task<bool> ExistsByFilePathAsync(string filePath)
    {
        // AsNoTracking + 只判存在：不物化实体，避免把 86KB 的 PosterData 读进内存
        return await _context.Movies.AsNoTracking().AnyAsync(m => m.FilePath == filePath);
    }

    // —— 定点更新（见 IMovieRepository 上的契约说明）——
    //
    // 统一走「桩实体 Attach + 逐列 IsModified」：构造一个只有 Id 的 Movie，附着为 Unchanged，
    // 再把真正要改的那一列标脏。EF 只会为标脏列生成 UPDATE，既不用读取实体（86KB 海报不会进内存），
    // 也不会像 Movies.Update(全实体) 那样把 Category / MovieTags 导航图和海报一并回写。
    //
    // ⚠️ 为什么不用 ExecuteUpdateAsync（EF Core 7+ 的单列 UPDATE，更现代）：
    // 本项目 MovieService / Integration / EdgeCase / Regression 等 25+ 条测试跑在 **InMemory
    // provider** 上，而 InMemory **不支持 ExecuteUpdate / ExecuteDelete**，会直接抛
    // InvalidOperationException("The methods 'ExecuteUpdate' and 'ExecuteUpdateAsync' are not
    // supported by the current database provider")。变更跟踪方式对所有 provider 通用。
    // 代价是无法像 ExecuteUpdate 那样在 SQL 里直接对列求值（如 `IsFavorite = NOT IsFavorite`），
    // 因此 ToggleFavoriteAsync 需要先窄读一次当前值。

    public async Task<string?> GetFilePathAsync(int id)
        // 只 SELECT FilePath 一列：删除/播放等场景拿一个字符串即可，
        // 不必把整行（含 86KB PosterData）物化成实体。
        => await _context.Movies
            .AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => m.FilePath)
            .FirstOrDefaultAsync();

    public async Task<bool> SetRatingAsync(int movieId, int? rating)
        => await SaveColumnAsync(movieId, m => m.Rating = rating, m => m.Rating);

    public async Task<bool> SetWatchStatusAsync(int movieId, WatchStatus status, DateTime? watchDate)
    {
        if (!await SaveColumnAsync(movieId, m => m.WatchStatus = status, m => m.WatchStatus)) return false;
        // WatchDate 要跟着一起改；分成两次单列写入，避免为多列场景引入更复杂的标脏签名。
        return await SaveColumnAsync(movieId, m => m.WatchDate = watchDate, m => m.WatchDate);
    }

    /// <summary>翻转收藏状态。需先窄读当前值（只 SELECT IsFavorite 一列，不读海报）。</summary>
    public async Task<bool> ToggleFavoriteAsync(int movieId)
    {
        var current = await _context.Movies
            .AsNoTracking()
            .Where(m => m.Id == movieId)
            .Select(m => (bool?)m.IsFavorite)
            .FirstOrDefaultAsync();
        if (current == null) return false; // 电影不存在

        return await SaveColumnAsync(movieId, m => m.IsFavorite = !current.Value, m => m.IsFavorite);
    }

    public async Task<bool> SetNotesAsync(int movieId, string? notes)
        => await SaveColumnAsync(movieId, m => m.Notes = notes, m => m.Notes);

    public async Task<bool> SetCategoryIdAsync(int movieId, int? categoryId)
        => await SaveColumnAsync(movieId, m => m.CategoryId = categoryId, m => m.CategoryId);

    /// <summary>
    /// 「桩实体 + 单列标脏」写入。<paramref name="property"/> 同时是标脏依据和编译期校验，避免手写列名字符串。
    /// </summary>
    /// <returns>false = 电影不存在（UPDATE 未命中任何行）。</returns>
    private async Task<bool> SaveColumnAsync<T>(
        int movieId, Action<Movie> setValue, Expression<Func<Movie, T>> property)
    {
        // 若同 Id 的实体已被本 context 跟踪（长生命周期 context 场景），直接改它，
        // 否则 Attach 会抛「another instance with the same key value is already being tracked」。
        var tracked = _context.ChangeTracker.Entries<Movie>().FirstOrDefault(e => e.Entity.Id == movieId);
        Movie entity;
        if (tracked != null)
        {
            entity = tracked.Entity;
        }
        else
        {
            entity = new Movie { Id = movieId };
            _context.Attach(entity); // Unchanged
        }

        setValue(entity);
        entity.UpdatedAt = DateTime.UtcNow;

        var entry = _context.Entry(entity);
        entry.Property(property).IsModified = true;
        entry.Property(m => m.UpdatedAt).IsModified = true;

        try
        {
            return await _context.SaveChangesAsync() > 0;
        }
        catch (DbUpdateConcurrencyException)
        {
            // 行不存在：UPDATE 未命中任何行。EF 把它表达为并发冲突。
            return false;
        }
    }
}
