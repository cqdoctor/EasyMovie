using EasyMovie.Core;
using EasyMovie.Core.Helpers;
using EasyMovie.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Data;

/// <summary>
/// 电影库主页面（MovieListView）启动引导专用的一条电影记录。
///
/// 这是**刻意收窄**的投影：只包含构建筛选下拉框与分类自动分配所需的标量列。
/// 刻意排除：
///   - <c>PosterData</c>：单部平均 86 KB，占全库 99.4% —— 引导流程一个字节都不需要
///   - <c>Synopsis</c> / 导航集合 <c>MovieTags</c>：前者用不到；后者会让 JOIN 产生
///     重复行，使每部电影的海报按标签数被重复读取（每部 3 个标签 = 海报读 3 遍）
/// </summary>
public class MovieBootstrapRow
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string? Director { get; set; }
    public string? Cast { get; set; }
    public string? SearchIndex { get; set; }
    public int? CategoryId { get; set; }
    public string? Country { get; set; }
    public string? Language { get; set; }
    public int Year { get; set; }
    public int? Runtime { get; set; }
}

/// <summary>引导阶段派生的筛选下拉框选项（纯数据，与 WPF 控件无关，故可单元测试）。</summary>
public class MovieFilterOptions
{
    /// <summary>年份下拉框选项（Year &gt; 0，降序）。</summary>
    public List<int> Years { get; set; } = new();

    /// <summary>国家下拉框选项（已清洗 HTML 碎片 + 过滤垃圾分类名，升序）。</summary>
    public List<string> Countries { get; set; } = new();

    public List<string> Languages { get; set; } = new();
    public List<string> Directors { get; set; } = new();

    /// <summary>年份范围滑块。仅当存在 1880..(今年+1) 区间的影片时有效。</summary>
    public bool HasYearRange { get; set; }
    public double YearMin { get; set; }
    public double YearMax { get; set; }

    /// <summary>片长范围滑块。仅当存在 0..600 分钟区间的影片时有效。</summary>
    public bool HasRuntimeRange { get; set; }
    public double RuntimeMin { get; set; }
    public double RuntimeMax { get; set; }
}

/// <summary>引导结果。</summary>
public class MovieBootstrapResult
{
    /// <summary>全部分类（含无电影的分类，用于批量操作下拉框）。</summary>
    public List<Category> AllCategories { get; set; } = new();

    /// <summary>只含有电影的分类（用于筛选下拉框），顺序按分类名。</summary>
    public List<Category> CategoriesWithMovies { get; set; } = new();

    /// <summary>是否存在未分类电影（决定是否显示"未分类"筛选项）。</summary>
    public bool HasUncategorized { get; set; }

    public MovieFilterOptions FilterOptions { get; set; } = new();
}

/// <summary>
/// 电影库主页面启动引导服务。
///
/// 原本这段逻辑内联在 <c>MovieListView.LoadDataAsync()</c> 中，每次窗口 Loaded 都会
/// <c>_movieService.GetAllAsync()</c> —— 该方法 <c>Include(Category)</c> +
/// <c>Include(MovieTags).ThenInclude(Tag)</c>，把全库海报读进内存只为填充几个下拉框：
/// 290 部 24 MB、2000 部 172 MB，且集合导航 JOIN 还会成倍放大。
///
/// 改造要点（沿用统计/推荐服务已验证的「两阶段查询」模式，并进一步做到零 BLOB 加载）：
///   1. 阶段一：窄投影 <see cref="MovieBootstrapRow"/>，不含 PosterData、不含导航集合
///   2. 写入：只在「确实需要写」时才动手，且用**桩实体**（stub entity）只更新单个列，
///      因此即便需要更新也**不会**把整行（含海报）读进内存
///   3. 绝大多数情况（分类已合法、影片已分类、索引齐全）三条写入路径全部短路，开销为 0
/// </summary>
public class MovieBootstrapService
{
    private readonly MovieDbContext _context;

    public MovieBootstrapService(MovieDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 执行启动引导：补全搜索索引 → 清理无效分类 → 自动分配国家分类 → 派生筛选选项。
    /// </summary>
    public async Task<MovieBootstrapResult> BootstrapAsync()
    {
        // 阶段一：窄投影。排序保留原实现的 CreatedAt 降序 —— 它决定新建分类的创建顺序，
        // 从而决定分类 Id 的分配，Oracle 比对依赖这一顺序稳定。
        var rows = await _context.Movies
            .AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new MovieBootstrapRow
            {
                Id = m.Id,
                Title = m.Title,
                OriginalTitle = m.OriginalTitle,
                Director = m.Director,
                Cast = m.Cast,
                SearchIndex = m.SearchIndex,
                CategoryId = m.CategoryId,
                Country = m.Country,
                Language = m.Language,
                Year = m.Year,
                Runtime = m.Runtime
            })
            .ToListAsync();

        // 分类表很小（几十行、无 BLOB），保持 tracked 以便直接 Add/Remove，
        // 查询形状（Include(Children) + OrderBy(Name)）与 CategoryRepository.GetAllAsync 一致。
        var categories = await LoadCategoriesAsync();

        await RebuildSearchIndexAsync(rows);
        await AutoAssignCountryCategoriesAsync(rows, categories);

        // 数据可能已变更，重新加载分类（与原实现一致）
        var allCats = await LoadCategoriesAsync();

        var usedCategoryIds = rows
            .Where(r => r.CategoryId.HasValue)
            .Select(r => r.CategoryId!.Value)
            .ToHashSet();

        return new MovieBootstrapResult
        {
            AllCategories = allCats,
            CategoriesWithMovies = allCats.Where(c => usedCategoryIds.Contains(c.Id)).ToList(),
            HasUncategorized = rows.Any(r => !r.CategoryId.HasValue),
            FilterOptions = BuildFilterOptions(rows)
        };
    }

    /// <summary>
    /// 批量导入去重专用：只取已存在的文件路径。
    /// 原实现为此调用 <c>GetAllAsync()</c>（全库海报 + 标签 JOIN），而这里只需要一列字符串。
    /// </summary>
    public async Task<HashSet<string>> GetExistingFilePathsAsync()
    {
        var paths = await _context.Movies
            .AsNoTracking()
            .Where(m => m.FilePath != null)
            .Select(m => m.FilePath!)
            .ToListAsync();

        // 保持原实现的默认字符串比较器（Ordinal）
        return new HashSet<string>(paths);
    }

    private Task<List<Category>> LoadCategoriesAsync()
    {
        return _context.Categories
            .Include(c => c.Children)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    /// <summary>
    /// 批量重建搜索索引。缺失索引的影片才更新，且用桩实体只写 SearchIndex 一列
    /// （不加载实体 → 不读取 PosterData）。
    /// </summary>
    private async Task RebuildSearchIndexAsync(List<MovieBootstrapRow> rows)
    {
        var missing = rows.Where(r => string.IsNullOrEmpty(r.SearchIndex)).ToList();
        if (missing.Count == 0) return;

        EnsureNoTrackedMovies();
        foreach (var row in missing)
        {
            var index = PinyinIndexHelper.BuildSearchIndex(row.Title, row.OriginalTitle, row.Director, row.Cast);
            var stub = new Movie { Id = row.Id, SearchIndex = index };
            _context.Movies.Attach(stub);
            _context.Entry(stub).Property(m => m.SearchIndex).IsModified = true;
            row.SearchIndex = index;
        }
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 清理无效分类，并为有国家信息但无分类的电影自动分配国家分类。
    /// 语义与原 MovieListView.AutoAssignCountryCategoriesBatchAsync 逐条对齐。
    /// </summary>
    private async Task AutoAssignCountryCategoriesAsync(List<MovieBootstrapRow> rows, List<Category> categories)
    {
        // 1. 清理无效分类：先把引用它的电影置空，再删除分类
        var invalidCats = categories
            .Where(c => !CategoryNameValidator.IsValidCategoryName(c.Name))
            .ToList();

        if (invalidCats.Count > 0)
        {
            var invalidIds = invalidCats.Select(c => c.Id).ToHashSet();
            var affected = rows
                .Where(r => r.CategoryId.HasValue && invalidIds.Contains(r.CategoryId.Value))
                .ToList();

            if (affected.Count > 0)
            {
                EnsureNoTrackedMovies();
                foreach (var row in affected)
                {
                    var stub = new Movie { Id = row.Id, CategoryId = null };
                    _context.Movies.Attach(stub);
                    _context.Entry(stub).Property(m => m.CategoryId).IsModified = true;
                    row.CategoryId = null;
                }
                // 先落库再删分类，避免外键约束的瞬时冲突
                await _context.SaveChangesAsync();
            }

            foreach (var cat in invalidCats) _context.Categories.Remove(cat);
            await _context.SaveChangesAsync();
        }

        // 2. 为有国家信息但无分类的电影自动分配分类
        var uncat = rows
            .Where(r => !r.CategoryId.HasValue && !string.IsNullOrWhiteSpace(r.Country))
            .ToList();
        if (uncat.Count == 0) return;

        // 重新加载分类（可能已删除无效分类）
        var validCats = await LoadCategoriesAsync();
        var pendingUpdates = new Dictionary<int, int?>();

        foreach (var row in uncat)
        {
            var firstCountry = row.Country!
                .Split('/', '·')
                .FirstOrDefault(c => CategoryNameValidator.IsValidCategoryName(c.Trim()))
                ?.Trim();
            if (string.IsNullOrEmpty(firstCountry)
                || !CategoryNameValidator.IsValidCategoryName(firstCountry)) continue;

            var existing = validCats.FirstOrDefault(c => c.Name == firstCountry);
            if (existing != null)
            {
                row.CategoryId = existing.Id;
                pendingUpdates[row.Id] = existing.Id;
            }
            else
            {
                var newCat = new Category
                {
                    Name = firstCountry,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.Categories.Add(newCat);
                await _context.SaveChangesAsync();
                validCats.Add(newCat);
                row.CategoryId = newCat.Id;
                pendingUpdates[row.Id] = newCat.Id;
            }
        }

        if (pendingUpdates.Count > 0)
        {
            EnsureNoTrackedMovies();
            foreach (var (movieId, catId) in pendingUpdates)
            {
                var stub = new Movie { Id = movieId, CategoryId = catId };
                _context.Movies.Attach(stub);
                _context.Entry(stub).Property(m => m.CategoryId).IsModified = true;
            }
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 桩实体更新的前提：Movies 集合中不能有已跟踪的同键实例，否则 Attach 会抛异常。
    ///
    /// 引导流程中该前提恒成立（所有电影查询都走了 AsNoTracking）。这里做一次兜底清理，
    /// 并显式记录前提 —— 若将来有人在 Bootstrap 之前改动过 Movie 实体且未保存，
    /// 那些改动会被丢弃。
    /// </summary>
    private void EnsureNoTrackedMovies()
    {
        if (_context.ChangeTracker.Entries<Movie>().Any())
            _context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 派生筛选下拉框选项。逻辑原样搬自 MovieListView.PopulateYearFilter /
    /// PopulateAdvancedFilterOptions（含滑块范围），仅去掉 WPF 控件赋值，使其可测试。
    /// </summary>
    public static MovieFilterOptions BuildFilterOptions(List<MovieBootstrapRow> rows)
    {
        var options = new MovieFilterOptions
        {
            Years = rows.Where(r => r.Year > 0)
                        .Select(r => r.Year)
                        .Distinct()
                        .OrderByDescending(y => y)
                        .ToList(),

            Countries = rows.Where(r => !string.IsNullOrWhiteSpace(r.Country))
                            .SelectMany(r => r.Country!.Split('/', ' ', '·', ','))
                            .Select(c => TextCleaner.CleanHtmlFragment(c.Trim()))
                            .Where(c => !string.IsNullOrEmpty(c) && CategoryNameValidator.IsValidCategoryName(c))
                            .Distinct()
                            .OrderBy(c => c)
                            .ToList(),

            Languages = rows.Where(r => !string.IsNullOrWhiteSpace(r.Language))
                            .SelectMany(r => r.Language!.Split('/', ' ', '·', ','))
                            .Select(l => TextCleaner.CleanHtmlFragment(l.Trim()))
                            .Where(l => !string.IsNullOrEmpty(l))
                            .Distinct()
                            .OrderBy(l => l)
                            .ToList(),

            Directors = rows.Where(r => !string.IsNullOrWhiteSpace(r.Director))
                            .SelectMany(r => r.Director!.Split('/', ','))
                            .Select(d => TextCleaner.CleanHtmlFragment(d.Trim()))
                            .Where(d => !string.IsNullOrEmpty(d))
                            .Distinct()
                            .OrderBy(d => d)
                            .ToList()
        };

        // 年份范围滑块
        var currentYear = DateTime.Now.Year;
        var validYears = rows.Where(r => r.Year >= 1880 && r.Year <= currentYear + 1)
                             .Select(r => (double)r.Year)
                             .ToList();
        if (validYears.Count > 0)
        {
            options.HasYearRange = true;
            options.YearMin = Math.Floor(validYears.Min() / 10.0) * 10;
            options.YearMax = Math.Min(currentYear, Math.Ceiling(validYears.Max() / 10.0) * 10);
        }

        // 片长范围滑块
        var validRuntimes = rows.Where(r => r.Runtime > 0 && r.Runtime < 600)
                                .Select(r => (double)r.Runtime!.Value)
                                .ToList();
        if (validRuntimes.Count > 0)
        {
            options.HasRuntimeRange = true;
            options.RuntimeMin = Math.Floor(validRuntimes.Min() / 30.0) * 30;
            options.RuntimeMax = Math.Ceiling(validRuntimes.Max() / 30.0) * 30;
        }

        return options;
    }
}
