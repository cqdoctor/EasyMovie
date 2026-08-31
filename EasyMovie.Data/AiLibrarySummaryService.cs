using EasyMovie.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Data;

/// <summary>AI 推荐页 system prompt 所需的影库概况（纯字符串，与 UI 无关，故可单元测试）。</summary>
public class AiLibrarySummary
{
    public int Total { get; set; }
    public int Watched { get; set; }
    public int WantToWatch { get; set; }
    public int Favorites { get; set; }

    /// <summary>类型分布 Top10，形如 "科幻(12部)"。</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>最爱导演 Top10，形如 "诺兰(5部)"。</summary>
    public List<string> TopDirectors { get; set; } = new();

    /// <summary>常用标签 Top10，形如 "动作(8部)"。</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>已看且评分高的电影 Top15，形如 "- 标题 (2020) ⭐9 | 导演 | 分类"。</summary>
    public List<string> WatchedTop { get; set; } = new();

    /// <summary>想看清单 Top20，形如 "- 标题 (2020) | 分类"。</summary>
    public List<string> WantWatchList { get; set; } = new();

    /// <summary>库中高分未看 Top20，形如 "- 标题 (2020) ⭐9 | 导演 | 分类"。</summary>
    public List<string> UnwatchedHighRated { get; set; } = new();
}

/// <summary>
/// 构建 AI 推荐页的影库概况。
///
/// 原实现内联在 AIRecommendationView.PreBuildSystemPromptAsync()，第一句就是
/// ctx.Movies.Include(Category).Include(MovieTags).ThenInclude(Tag).ToListAsync() ——
/// 把全库海报读进内存（290 部 24.29 MB，集合导航 JOIN 还会按标签数成倍放大），
/// 而生成的 prompt 里一个字节的海报都不需要，只用到 4 个计数和几个 Top10。
///
/// 改造：
///   1. 四个计数走 SQL COUNT（不再物化实体）
///   2. 主体走窄投影（不含 PosterData、不含导航集合）
///   3. 标签改为「查 MovieTags + Tags 两张小表、内存分组」，避开 Include 的 JOIN 笛卡尔积
///   4. 分类 Id→Name 用字典映射，不加载 Category 实体
///
/// 顺序约定（并列排序的可复现性依赖它）：
///   窄投影按 Id 升序，复刻原查询无 ORDER BY 时 SQLite 的 rowid 返回顺序；
///   标签按 (MovieId, TagId) 升序，复刻「按电影遍历 → SelectMany 其标签」的顺序。
/// </summary>
public class AiLibrarySummaryService
{
    private readonly MovieDbContext _context;

    public AiLibrarySummaryService(MovieDbContext context)
    {
        _context = context;
    }

    public async Task<AiLibrarySummary> BuildAsync()
    {
        var summary = new AiLibrarySummary
        {
            Total = await _context.Movies.CountAsync(),
            Watched = await _context.Movies.CountAsync(m => m.WatchStatus == WatchStatus.Watched),
            WantToWatch = await _context.Movies.CountAsync(m => m.WatchStatus == WatchStatus.WantToWatch),
            Favorites = await _context.Movies.CountAsync(m => m.IsFavorite)
        };

        // 窄投影：只取 prompt 真正用到的标量列
        var rows = await _context.Movies
            .AsNoTracking()
            .OrderBy(m => m.Id)
            .Select(m => new
            {
                m.Id,
                m.Title,
                m.Year,
                m.Director,
                m.Rating,
                m.WatchStatus,
                m.CategoryId
            })
            .ToListAsync();

        // 分类 Id→Name（小表，几十行无 BLOB）
        var catNameById = await _context.Categories
            .AsNoTracking()
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        string CatNameOf(int? id)
            => id.HasValue && catNameById.TryGetValue(id.Value, out var name) ? name : "";

        // 类型分布：原实现按 Category.Name 分组，这里映射后按名字分组，语义一致
        summary.Categories = rows
            .Where(r => r.CategoryId.HasValue && catNameById.ContainsKey(r.CategoryId.Value))
            .GroupBy(r => catNameById[r.CategoryId!.Value])
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => $"{g.Key}({g.Count()}部)")
            .ToList();

        summary.TopDirectors = rows
            .Where(r => !string.IsNullOrEmpty(r.Director))
            .SelectMany(r => r.Director!.Split('/', ',').Select(d => d.Trim()))
            .GroupBy(d => d)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => $"{g.Key}({g.Count()}部)")
            .ToList();

        // 标签：两张小表 + 内存分组。Join 自然过滤掉 Tag 不存在的脏关联（等价于原实现的 Where(n => n != null)）
        var tagPairs = await _context.MovieTags
            .AsNoTracking()
            .Select(mt => new { mt.MovieId, mt.TagId })
            .OrderBy(x => x.MovieId).ThenBy(x => x.TagId)
            .ToListAsync();

        var tagNames = await _context.Tags
            .AsNoTracking()
            .Select(t => new { t.Id, t.Name })
            .ToListAsync();

        summary.Tags = tagPairs
            .Join(tagNames, mt => mt.TagId, t => t.Id, (_, t) => t.Name)
            .GroupBy(n => n)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => $"{g.Key}({g.Count()}部)")
            .ToList();

        summary.WatchedTop = rows
            .Where(r => r.WatchStatus == WatchStatus.Watched && r.Rating.HasValue)
            .OrderByDescending(r => r.Rating)
            .Take(15)
            .Select(r => $"- {r.Title} ({r.Year}) ⭐{r.Rating} | {FirstDirectorOf(r.Director)} | {CatNameOf(r.CategoryId)}")
            .ToList();

        summary.WantWatchList = rows
            .Where(r => r.WatchStatus == WatchStatus.WantToWatch)
            .Take(20)
            .Select(r => $"- {r.Title} ({r.Year}) | {CatNameOf(r.CategoryId)}")
            .ToList();

        summary.UnwatchedHighRated = rows
            .Where(r => r.WatchStatus == WatchStatus.NotWatched && r.Rating.HasValue)
            .OrderByDescending(r => r.Rating)
            .Take(20)
            .Select(r => $"- {r.Title} ({r.Year}) ⭐{r.Rating} | {FirstDirectorOf(r.Director)} | {CatNameOf(r.CategoryId)}")
            .ToList();

        return summary;
    }

    /// <summary>复刻原实现的 m.Director?.Split('/').FirstOrDefault() ?? ""。</summary>
    private static string FirstDirectorOf(string? director)
        => director?.Split('/').FirstOrDefault() ?? "";
}
