using System.Text.RegularExpressions;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Tools.MovieApi;

/// <summary>
/// 离线元数据缓存：作为「所有网络源不可达 / 首次导入」时的兜底匹配源。
/// - 启动时若 cache.db 不存在会自动建表；
/// - 种子数据（IMDb 西片 + 常见片清单）由离线种子工具预填充；
/// - 运行时联网命中结果也会回流 Upsert，逐步自学习扩充。
/// 与业务主库完全独立。
/// </summary>
public static class LocalMovieCache
{
    private static readonly Regex _norm = new(@"[^\p{L}\p{N}]", RegexOptions.Compiled);

    /// <summary>归一化键：去除标点空白、保留字母/数字/中日韩字符、转小写（与解析层/匹配层保持一致）。</summary>
    public static string NormalizeKey(string s) => _norm.Replace(s ?? "", "").ToLowerInvariant();

    /// <summary>确保缓存库与表存在（幂等，可重复调用）。</summary>
    public static void EnsureReady()
    {
        using var ctx = CacheDbContext.Create();
        ctx.Database.EnsureCreated();
    }

    public static int Count()
    {
        using var ctx = CacheDbContext.Create();
        return ctx.CachedMovies.Count();
    }

    /// <summary>
    /// 按文件名解析出的片名（及年份）在缓存中查找。命中返回元数据，未命中返回 null。
    /// 同时匹配展示片名与原产片名（中文/原名），并按年份接近度与评分择优。
    /// </summary>
    public static MovieSearchResult? Lookup(Movie movie)
    {
        EnsureReady();
        var q = NormalizeKey(movie.Title);
        if (string.IsNullOrEmpty(q)) return null;

        using var ctx = CacheDbContext.Create();
        var candidates = ctx.CachedMovies
            .Where(c => c.NormTitle == q || c.NormOriginal == q)
            .AsEnumerable()
            .ToList();
        if (candidates.Count == 0) return null;

        var match = candidates
            .Where(c => movie.Year == 0 || c.Year == 0 || Math.Abs(c.Year - movie.Year) <= 1)
            .OrderByDescending(c => c.Rating ?? 0)
            .FirstOrDefault()
            ?? candidates.OrderByDescending(c => c.Rating ?? 0).First();

        return ToResult(match);
    }

    /// <summary>
    /// 将一次成功的联网匹配结果回流写入缓存（按归一化键去重，不覆盖已有种子）。
    /// 这样用户首次联网命中后，后续离线也能直接命中。
    /// </summary>
    public static void Upsert(MovieSearchResult result, string source)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.Title)) return;
        var nt = NormalizeKey(result.Title);
        var no = NormalizeKey(result.OriginalTitle ?? "");
        if (string.IsNullOrEmpty(nt) && string.IsNullOrEmpty(no)) return;

        EnsureReady();
        using var ctx = CacheDbContext.Create();
        var exists = ctx.CachedMovies.Any(c => c.NormTitle == nt || (no != "" && c.NormOriginal == no));
        if (exists) return;

        ctx.CachedMovies.Add(new CachedMovie
        {
            Title = result.Title,
            OriginalTitle = result.OriginalTitle,
            Year = result.Year,
            Director = result.Director,
            Cast = result.Cast,
            Country = result.Country,
            Language = result.Language,
            PosterUrl = result.PosterUrl,
            Rating = result.Rating,
            RatingCount = result.RatingCount,
            Source = source,
            NormTitle = nt,
            NormOriginal = string.IsNullOrEmpty(no) ? null : no,
        });
        ctx.SaveChanges();
    }

    /// <summary>
    /// 补全式写入：缓存中不存在则插入；已存在则按字段补缺（导演/演员/国家/语言/海报为空才补，
    /// 评分/评分人数/年份为 0 或 null 视为缺失才补），绝不覆盖已有有效数据，避免降级。
    /// 用于“慢速补全 2020+ 元数据”等回流场景：同一部片多次补全只会越补越全，不会回退。
    /// 幂等、线程安全（CacheDbContext 内部写锁）。
    /// </summary>
    public static void UpsertOrMerge(MovieSearchResult result, string source)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.Title)) return;
        var nt = NormalizeKey(result.Title);
        var no = NormalizeKey(result.OriginalTitle ?? "");
        if (string.IsNullOrEmpty(nt) && string.IsNullOrEmpty(no)) return;

        EnsureReady();
        using var ctx = CacheDbContext.Create();
        // 命中优先级：先按「应用主路径键」NormTitle 找（FetchAsync 用原始标题直接 Lookup），
        // 只有主键不存在时才退回「清洗别名键」NormOriginal。这样补全数据一定写进应用真正查的那条键，
        // 不会再把好数据并进清洗键记录、把原始噪声键的空记录晾在一边（旧 bug：部分片库标题带
        // “中俄字幕”等噪声后缀，应用按原始标题查到空记录而看不见评分）。
        var primaryHit = ctx.CachedMovies.FirstOrDefault(c => c.NormTitle == nt);
        // 所有以清洗别名键命中的条目，可能含“好数据在清洗键、原始键空着”的残留（如早前 TMDB 写下的评分）。
        var aliasHits = no != ""
            ? ctx.CachedMovies.Where(c => c.NormOriginal == no && c != primaryHit).ToList()
            : new List<CachedMovie>();
        // 把别名记录里已有的有效字段回灌到本次结果，避免好数据被孤立在清洗键：
        // 例如本次只有 1905 返回空数据，但别名键下早有 TMDB 评分 7.6，应并回原始键供应用显示。
        var donor = aliasHits
            .OrderByDescending(a => (a.Rating.HasValue ? 1 : 0) + (string.IsNullOrEmpty(a.PosterUrl) ? 0 : 1))
            .FirstOrDefault();
        if (donor != null)
        {
            if (string.IsNullOrEmpty(result.Director)) result.Director = donor.Director;
            if (string.IsNullOrEmpty(result.Cast)) result.Cast = donor.Cast;
            if (string.IsNullOrEmpty(result.Country)) result.Country = donor.Country;
            if (string.IsNullOrEmpty(result.Language)) result.Language = donor.Language;
            if (string.IsNullOrEmpty(result.PosterUrl)) result.PosterUrl = donor.PosterUrl;
            if (!result.Rating.HasValue && donor.Rating.HasValue) result.Rating = donor.Rating;
            if (!result.RatingCount.HasValue && donor.RatingCount.HasValue) result.RatingCount = donor.RatingCount;
            if (result.Year == 0) result.Year = donor.Year;
            if (string.IsNullOrEmpty(result.OriginalTitle)) result.OriginalTitle = donor.OriginalTitle;
        }
        var existing = primaryHit ?? aliasHits.FirstOrDefault();
        // 仅命中别名键、但本次写回带有效主键：把别名记录提升为主键，保证原始标题也能命中。
        bool promoteToPrimary = (primaryHit == null && existing != null && !string.IsNullOrEmpty(nt));
        if (existing == null)
        {
            ctx.CachedMovies.Add(new CachedMovie
            {
                Title = result.Title,
                OriginalTitle = result.OriginalTitle,
                Year = result.Year,
                Director = result.Director,
                Cast = result.Cast,
                Country = result.Country,
                Language = result.Language,
                PosterUrl = result.PosterUrl,
                Rating = result.Rating,
                RatingCount = result.RatingCount,
                Source = source,
                NormTitle = nt,
                NormOriginal = string.IsNullOrEmpty(no) ? null : no,
            });
            ctx.SaveChanges();
            return;
        }

        // 已存在：仅补缺，不降级
        bool changed = false;
        if (promoteToPrimary)
        {
            existing.NormTitle = nt;
            existing.Title = result.Title;
            changed = true;
        }
        if (string.IsNullOrEmpty(existing.Director) && !string.IsNullOrEmpty(result.Director)) { existing.Director = result.Director; changed = true; }
        if (string.IsNullOrEmpty(existing.Cast) && !string.IsNullOrEmpty(result.Cast)) { existing.Cast = result.Cast; changed = true; }
        if (string.IsNullOrEmpty(existing.Country) && !string.IsNullOrEmpty(result.Country)) { existing.Country = result.Country; changed = true; }
        if (string.IsNullOrEmpty(existing.Language) && !string.IsNullOrEmpty(result.Language)) { existing.Language = result.Language; changed = true; }
        if (string.IsNullOrEmpty(existing.PosterUrl) && !string.IsNullOrEmpty(result.PosterUrl)) { existing.PosterUrl = result.PosterUrl; changed = true; }
        if ((!existing.Rating.HasValue || existing.Rating.Value <= 0) && result.Rating.HasValue && result.Rating.Value > 0) { existing.Rating = result.Rating; changed = true; }
        if ((!existing.RatingCount.HasValue || existing.RatingCount.Value <= 0) && result.RatingCount.HasValue && result.RatingCount.Value > 0) { existing.RatingCount = result.RatingCount; changed = true; }
        if (existing.Year == 0 && result.Year > 0) { existing.Year = result.Year; changed = true; }
        // 关键修复：OriginalTitle 变更时必须同步重算 NormOriginal，否则 Lookup 按 NormOriginal 命中时会漏掉。
        // 同时改为「只要结果带有效原名就采用」(不再要求 existing 为空)，确保慢速补全写回的规范清洗键能覆盖旧值。
        if (!string.IsNullOrEmpty(result.OriginalTitle))
        {
            existing.OriginalTitle = result.OriginalTitle;
            existing.NormOriginal = NormalizeKey(result.OriginalTitle);
            changed = true;
        }
        if (changed) ctx.SaveChanges();
    }

    /// <summary>
    /// 一次性修复：为所有条目按 Title 重算「规范清洗键」并写入 OriginalTitle/NormOriginal，
    /// 使应用层清洗键 Lookup（SettingsView / MovieListView 先 ExtractChineseKeyword 再 Lookup）也能命中。
    /// 与 UpsertOrMerge 的写回键策略保持一致（canonical = ExtractChineseKeyword(Title) 或 ExtractEnglishHint）。
    /// 幂等，返回实际变更行数。
    /// </summary>
    public static int RecomputeCanonicalKeys()
    {
        EnsureReady();
        int n = 0;
        using var ctx = CacheDbContext.Create();
        foreach (var c in ctx.CachedMovies.AsEnumerable())
        {
            var canonical = DoubanApiClient.ExtractChineseKeyword(c.Title);
            if (string.IsNullOrWhiteSpace(canonical))
                canonical = DoubanApiClient.ExtractEnglishHint(c.Title) ?? c.Title.Trim();
            var no = NormalizeKey(canonical);
            if (c.OriginalTitle != canonical || c.NormOriginal != no)
            {
                c.OriginalTitle = canonical;
                c.NormOriginal = string.IsNullOrEmpty(no) ? null : no;
                n++;
            }
        }
        if (n > 0) ctx.SaveChanges();
        return n;
    }

    /// <summary>
    /// 离线合并：同一部片常因「原始噪声键」与「清洗键」拆成多条缓存记录，且好数据只落在其中一条
    /// （例如 TMDB 评分写在清洗键、原始键那条是 1905 空记录）。本方法按共享 NormTitle / NormOriginal
    /// 把互为别名的记录聚成一组，将组内每个字段的「非空最优值」写回组内每一条，确保应用无论按
    /// 哪个键（原始标题 / 清洗标题）Lookup 都能命中完整数据。不联网、幂等，返回变更行数。
    /// </summary>
    public static int ConsolidateAliases()
    {
        EnsureReady();
        int n = 0;
        using var ctx = CacheDbContext.Create();
        var all = ctx.CachedMovies.AsEnumerable().ToList();
        var byKey = new Dictionary<string, List<CachedMovie>>();
        void Add(string? k, CachedMovie c)
        {
            if (string.IsNullOrEmpty(k)) return;
            if (!byKey.TryGetValue(k, out var lst)) { lst = new List<CachedMovie>(); byKey[k] = lst; }
            lst.Add(c);
        }
        foreach (var c in all) { Add(c.NormTitle, c); Add(c.NormOriginal, c); }

        var seen = new HashSet<CachedMovie>();
        foreach (var c in all)
        {
            if (!seen.Add(c)) continue;
            // BFS 连通分量：共享 NormTitle 或 NormOriginal 的记录视为同一部片
            var comp = new List<CachedMovie>();
            var stack = new Stack<CachedMovie>(); stack.Push(c);
            while (stack.Count > 0)
            {
                var x = stack.Pop(); comp.Add(x);
                var keys = new[] { x.NormTitle, x.NormOriginal }.Where(k => !string.IsNullOrEmpty(k));
                foreach (var k in keys)
                    if (byKey.TryGetValue(k!, out var lst))
                        foreach (var y in lst) if (seen.Add(y)) stack.Push(y);
            }
            // 组内字段并集（取非空值）
            string? BestStr(Func<CachedMovie, string?> f) => comp.FirstOrDefault(m => !string.IsNullOrEmpty(f(m))) is { } m ? f(m) : null;
            double? BestRating() => comp.FirstOrDefault(m => m.Rating.HasValue)?.Rating;
            double? BestCount() => comp.FirstOrDefault(m => m.RatingCount.HasValue)?.RatingCount;
            int BestYear() => comp.FirstOrDefault(m => m.Year > 0)?.Year ?? 0;
            var bDir = BestStr(m => m.Director);
            var bCast = BestStr(m => m.Cast);
            var bCountry = BestStr(m => m.Country);
            var bLang = BestStr(m => m.Language);
            var bPoster = BestStr(m => m.PosterUrl);
            var bRating = BestRating();
            var bCount = BestCount();
            var bYear = BestYear();
            foreach (var m in comp)
            {
                bool ch = false;
                if (string.IsNullOrEmpty(m.Director) && !string.IsNullOrEmpty(bDir)) { m.Director = bDir; ch = true; }
                if (string.IsNullOrEmpty(m.Cast) && !string.IsNullOrEmpty(bCast)) { m.Cast = bCast; ch = true; }
                if (string.IsNullOrEmpty(m.Country) && !string.IsNullOrEmpty(bCountry)) { m.Country = bCountry; ch = true; }
                if (string.IsNullOrEmpty(m.Language) && !string.IsNullOrEmpty(bLang)) { m.Language = bLang; ch = true; }
                if (string.IsNullOrEmpty(m.PosterUrl) && !string.IsNullOrEmpty(bPoster)) { m.PosterUrl = bPoster; ch = true; }
                if (!m.Rating.HasValue && bRating.HasValue) { m.Rating = bRating; ch = true; }
                if (!m.RatingCount.HasValue && bCount.HasValue) { m.RatingCount = (int?)bCount; ch = true; }
                if (m.Year == 0 && bYear > 0) { m.Year = bYear; ch = true; }
                if (ch) n++;
            }
        }
        if (n > 0) ctx.SaveChanges();
        return n;
    }

    /// <summary>
    /// 为片库影片补齐「原始键」缓存记录：若某片按原始标题（应用主路径键 NormTitle）查无数据，
    /// 但其清洗键（ExtractChineseKeyword / ExtractEnglishHint）下有评分或海报，则额外写一条以原始标题
    /// 为主键的记录并复制清洗键的数据。仅新增/补缺，不删除、不改写清洗键记录，保证应用层
    /// FetchAsync 用原始标题 Lookup 一定能命中（解决“数据只在清洗键、应用按原始键查不到”的残留）。
    /// 幂等，返回新增/补缺行数。
    /// </summary>
    public static int SeedRawKeysFromLibrary(IEnumerable<(string Title, int? Year)> movies)
    {
        EnsureReady();
        int n = 0;
        using var ctx = CacheDbContext.Create();
        foreach (var mv in movies)
        {
            var nt = NormalizeKey(mv.Title);
            if (string.IsNullOrEmpty(nt)) continue;
            var raw = ctx.CachedMovies.FirstOrDefault(c => c.NormTitle == nt);
            if (raw != null && (!string.IsNullOrEmpty(raw.PosterUrl) || raw.Rating.HasValue)) continue;

            var clean = DoubanApiClient.ExtractChineseKeyword(mv.Title);
            if (string.IsNullOrWhiteSpace(clean))
                clean = DoubanApiClient.ExtractEnglishHint(mv.Title) ?? mv.Title.Trim();
            var no = NormalizeKey(clean);
            // 主匹配：精确清洗键；兜底：清洗键互为前后缀（中文片名片段差异，如 白象 vs 白象危城悍将、
            // 狙击手 vs 狙击手环球反应与情报小组），并要求年份相近，避免误并不同影片。
            var donor = ctx.CachedMovies
                .Where(c => c.NormTitle == no
                    || (no != "" && c.NormOriginal == no)
                    || (no != "" && c.NormTitle.StartsWith(no))
                    || (no != "" && no.StartsWith(c.NormTitle))
                    || (no != "" && c.NormOriginal != null && c.NormOriginal.StartsWith(no))
                    || (no != "" && c.NormOriginal != null && no.StartsWith(c.NormOriginal)))
                .Where(c => !string.IsNullOrEmpty(c.PosterUrl) || c.Rating.HasValue)
                .Where(c => !mv.Year.HasValue || c.Year == 0 || Math.Abs(c.Year - mv.Year.Value) <= 2)
                .OrderByDescending(c => (c.Rating.HasValue ? 1 : 0) + (string.IsNullOrEmpty(c.PosterUrl) ? 0 : 1))
                .FirstOrDefault();
            if (donor == null) continue;

            if (raw == null)
            {
                ctx.CachedMovies.Add(new CachedMovie
                {
                    Title = mv.Title,
                    OriginalTitle = clean,
                    Year = donor.Year,
                    Director = donor.Director,
                    Cast = donor.Cast,
                    Country = donor.Country,
                    Language = donor.Language,
                    PosterUrl = donor.PosterUrl,
                    Rating = donor.Rating,
                    RatingCount = donor.RatingCount,
                    Source = donor.Source,
                    NormTitle = nt,
                    NormOriginal = string.IsNullOrEmpty(no) ? null : no,
                });
                n++;
            }
            else
            {
                if (string.IsNullOrEmpty(raw.PosterUrl) && !string.IsNullOrEmpty(donor.PosterUrl)) { raw.PosterUrl = donor.PosterUrl; n++; }
                if (!raw.Rating.HasValue && donor.Rating.HasValue) { raw.Rating = donor.Rating; n++; }
                if (string.IsNullOrEmpty(raw.Director) && !string.IsNullOrEmpty(donor.Director)) { raw.Director = donor.Director; n++; }
                if (string.IsNullOrEmpty(raw.Cast) && !string.IsNullOrEmpty(donor.Cast)) { raw.Cast = donor.Cast; n++; }
            }
        }
        if (n > 0) ctx.SaveChanges();
        return n;
    }
    /// 按归一化片名/原产名定位条目，幂等。
    /// </summary>
    public static void UpdatePoster(string title, string? originalTitle, int year, string posterUrl)
    {
        if (string.IsNullOrWhiteSpace(posterUrl)) return;
        var nt = NormalizeKey(title);
        var no = NormalizeKey(originalTitle ?? "");
        if (string.IsNullOrEmpty(nt) && string.IsNullOrEmpty(no)) return;

        EnsureReady();
        using var ctx = CacheDbContext.Create();
        var rows = ctx.CachedMovies
            .Where(c => c.NormTitle == nt || (no != "" && c.NormOriginal == no))
            .AsEnumerable()
            .ToList();
        if (rows.Count == 0) return;
        foreach (var c in rows) c.PosterUrl = posterUrl;
        ctx.SaveChanges();
    }

    private static MovieSearchResult ToResult(CachedMovie c) => new()
    {
        Title = c.Title,
        OriginalTitle = c.OriginalTitle,
        Year = c.Year,
        Director = c.Director,
        Cast = c.Cast,
        Country = c.Country,
        Language = c.Language,
        PosterUrl = c.PosterUrl,
        Rating = c.Rating,
        RatingCount = c.RatingCount,
        Source = "cache",
        ExternalId = "cache:" + c.Id,
    };
}
