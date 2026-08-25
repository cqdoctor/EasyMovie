using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace EasyMovie.Tools.MovieApi;

/// <summary>
/// 离线预种子导入器：把豆瓣离线数据集（csuldw 14万集 CSV）灌入生产缓存库 cache.db。
/// 全程离线文件读取，不发起任何网络请求，因此不会触碰 MovieInfoFetcher / DoubanApiClient 的限流与熔断逻辑。
/// 设计要点：
///  - 兼容 UTF-8(BOM)/GBK 编码、逗号/制表符分隔（csuldw 不同导出变体）；
///  - 剥离豆瓣 NAME 字段常见的 " - 电影" 后缀与末尾 "(年份)"，否则归一化键与干净片名对不上；
///  - 批量(5000)落库，避免逐行查库的 O(N²)；
///  - 幂等合并：已存在条目不降级；若已存在条目缺海报而本行有，则补图；
///  - 可选过滤：仅灌「评分≥minRating 且 评价数≥minVotes」的热门高分片，降低库体积。
/// 与 LocalMovieCache 共用 NormalizeKey，保证与运行时 Lookup 命中逻辑一致。
/// </summary>
public static class SeedImporter
{
    private const int Batch = 5000;

    // 豆瓣 NAME 常带 " - 电影"/" - 电视剧" 等后缀，剥离后才能与干净片名匹配
    private static readonly Regex _mediaSuffix = new(@"\s*-\s*(电影|电视剧|综艺|纪录片|短片|动画|电影版|剧集)\s*$", RegexOptions.Compiled);
    // 标题末尾 "(1994)" / " (1994)" 剥离，年份用于补足 Year
    private static readonly Regex _yearSuffix = new(@"^(?<t>.+?)\s*\((?<y>\d{4})\)\s*$", RegexOptions.Compiled);

    /// <summary>导入进度</summary>
    public sealed class SeedProgress
    {
        public int Done { get; set; }
        public int Total { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>导入结果报告</summary>
    public sealed class SeedImportReport
    {
        public int TotalRows { get; set; }
        public int Inserted { get; set; }
        public int Skipped { get; set; }      // 空标题 / 文件内重复
        public int Filtered { get; set; }     // 被 minRating/minVotes 过滤掉
        public int PosterFilled { get; set; } // 为已有条目补缺字段（导演/评分/演员/海报等）的数量
        public int NewWithPoster { get; set; } // 新增条目中带海报的数量
        public string? Error { get; set; }
    }

    /// <summary>
    /// 导入豆瓣离线 CSV 到生产缓存库。
    /// </summary>
    /// <param name="path">豆瓣 movies.csv 路径（csuldw 格式）</param>
    /// <param name="minRating">仅灌评分≥此值的影片（0=不过滤）</param>
    /// <param name="minVotes">仅灌评价数≥此值的影片（0=不过滤；源无评价数列时忽略此维度）</param>
    /// <param name="progress">进度回调</param>
    public static async Task<SeedImportReport> ImportDoubanCsvAsync(
        string path, double minRating = 0, long minVotes = 0,
        IProgress<SeedProgress>? progress = null, CancellationToken ct = default)
    {
        var report = new SeedImportReport();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            report.Error = "文件不存在：" + path;
            return report;
        }

        // GBK 等代码页在非 Windows 环境需注册提供器（Windows 自带）
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { /* 已注册则忽略 */ }

        var (enc, delim, bom) = ProbeEncodingAndDelimiter(path);

        await using var fs = File.OpenRead(path);
        fs.Seek(bom ? 3 : 0, SeekOrigin.Begin); // 跳过 BOM，避免首列名带 \uFEFF
        using var reader = new StreamReader(fs, enc, leaveOpen: true);

        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delim,
            HasHeaderRecord = true,
            BadDataFound = _ => { },               // 容忍个别坏字段，跳过而非中断
            MissingFieldFound = _ => { },
            IgnoreBlankLines = true,
        };

        using var csv = new CsvReader(reader, cfg);

        // 建立「小写列名 → 索引」映射，避免依赖 CsvHelper 的表头匹配 API 差异（含大小写）
        if (!csv.Read()) { report.Error = "CSV 为空或无法读取表头"; return report; }
        csv.ReadHeader();
        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var header = csv.HeaderRecord ?? Array.Empty<string>();
        for (int i = 0; i < header.Length; i++)
            headerMap[header[i].Trim().ToLowerInvariant()] = i;
        int Idx(string logic) => headerMap.TryGetValue(logic, out var i) ? i : -1;

        // 载入已有缓存用于合并（幂等 + 补海报）。键为 NormTitle，值为是否已有海报。
        using (var ctx0 = CacheDbContext.Create())
            ctx0.Database.EnsureCreated();
        // 载入已有条目的字段完整度，用于「按字段补缺」合并（不覆盖已有值，只填补空缺）
        var existing = new Dictionary<string, Ext>(StringComparer.Ordinal);
        using (var ctx0 = CacheDbContext.Create())
        {
            foreach (var m in ctx0.CachedMovies.Select(x => new { x.NormTitle, x.PosterUrl, x.Director, x.Rating, x.Cast, x.Year, x.Country, x.Language }).AsEnumerable())
                existing[m.NormTitle] = new Ext
                {
                    HasPoster = !string.IsNullOrEmpty(m.PosterUrl),
                    HasDirector = !string.IsNullOrEmpty(m.Director),
                    HasRating = m.Rating.HasValue && m.Rating.Value > 0,
                    HasCast = !string.IsNullOrEmpty(m.Cast),
                    HasYear = m.Year > 0,
                    HasCountry = !string.IsNullOrEmpty(m.Country),
                    HasLanguage = !string.IsNullOrEmpty(m.Language),
                };
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<CachedMovie>(Batch);
        // 已存在条目中需要补缺的：nt → 新数据（仅用于填补空缺字段）
        var backfill = new List<(string nt, CachedMovie data)>();
        var backfillNts = new HashSet<string>(StringComparer.Ordinal);
        int total = 0;

        try
        {
            while (csv.Read())
            {
                ct.ThrowIfCancellationRequested();
                total++;

                string name, yearStr, cover, directors, actors, regions, languages, score, votes, alias;
                try
                {
                    int iName = Idx("name"), iYear = Idx("year"), iCover = Idx("cover"), iDir = Idx("directors"),
                        iActors = Idx("actors"), iRegions = Idx("regions"), iLang = Idx("languages"),
                        iScore = Idx("douban_score"), iVotes = Idx("douban_votes"), iAlias = Idx("alias");
                    name = iName >= 0 ? (csv.GetField(iName) ?? "") : "";
                    yearStr = iYear >= 0 ? (csv.GetField(iYear) ?? "") : "";
                    cover = iCover >= 0 ? (csv.GetField(iCover) ?? "") : "";
                    directors = iDir >= 0 ? (csv.GetField(iDir) ?? "") : "";
                    actors = iActors >= 0 ? (csv.GetField(iActors) ?? "") : "";
                    regions = iRegions >= 0 ? (csv.GetField(iRegions) ?? "") : "";
                    languages = iLang >= 0 ? (csv.GetField(iLang) ?? "") : "";
                    score = iScore >= 0 ? (csv.GetField(iScore) ?? "") : "";
                    votes = iVotes >= 0 ? (csv.GetField(iVotes) ?? "") : "";
                    alias = iAlias >= 0 ? (csv.GetField(iAlias) ?? "") : "";
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[SEED] 第 {Row} 行字段解析跳过", total);
                    report.Skipped++;
                    continue;
                }

                // 剥离 " - 电影" 后缀，再剥离末尾 "(年份)"
                name = _mediaSuffix.Replace(name, "").Trim();
                int yFromTitle = 0;
                var ym = _yearSuffix.Match(name);
                if (ym.Success) { name = ym.Groups["t"].Value.Trim(); yFromTitle = int.Parse(ym.Groups["y"].Value); }

                if (string.IsNullOrWhiteSpace(name)) { report.Skipped++; continue; }
                var nt = LocalMovieCache.NormalizeKey(name);
                if (string.IsNullOrEmpty(nt)) { report.Skipped++; continue; }
                if (seen.Contains(nt)) { report.Skipped++; continue; }

                int year = 0;
                int.TryParse(yearStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out year);
                if (year == 0) year = yFromTitle;

                var poster = cover.Trim();
                var rating = ParseDouble(score);
                var voteCount = ParseLong(votes);

                // 过滤：仅保留热门高分片以降低库体积
                if (minRating > 0 && (!rating.HasValue || rating.Value < minRating)) { report.Filtered++; continue; }
                if (minVotes > 0 && voteCount.HasValue && voteCount.Value < minVotes) { report.Filtered++; continue; }

                seen.Add(nt);

                // 已存在条目：不降级，仅按字段补缺（导演/评分/演员/年份/国家/语言/海报任一空缺则用新数据补）
                if (existing.TryGetValue(nt, out var exMeta))
                {
                    bool need = (!exMeta.HasPoster && !string.IsNullOrEmpty(poster))
                                || (!exMeta.HasDirector && !string.IsNullOrEmpty(directors))
                                || (!exMeta.HasRating && rating.HasValue && rating.Value > 0)
                                || (!exMeta.HasCast && !string.IsNullOrEmpty(actors))
                                || (!exMeta.HasYear && year > 0)
                                || (!exMeta.HasCountry && !string.IsNullOrEmpty(regions))
                                || (!exMeta.HasLanguage && !string.IsNullOrEmpty(languages));
                    if (need && backfillNts.Add(nt))
                    {
                        backfill.Add((nt, new CachedMovie
                        {
                            Title = name,
                            Year = year,
                            Director = directors.Trim(),
                            Cast = actors.Trim(),
                            Country = regions.Trim(),
                            Language = languages.Trim(),
                            PosterUrl = poster,
                            Rating = rating,
                            RatingCount = voteCount.HasValue ? (int?)voteCount.Value : null,
                        }));
                    }
                    continue;
                }

                // 别名取首个片段作为原产片名，利于英文文件名匹配（不覆盖中文 NAME）
                string? origTitle = null, normOrig = null;
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    var firstAlias = alias.Split('/')[0].Trim();
                    if (firstAlias != name)
                    {
                        origTitle = firstAlias;
                        normOrig = LocalMovieCache.NormalizeKey(firstAlias);
                    }
                }

                batch.Add(new CachedMovie
                {
                    Title = name,
                    OriginalTitle = origTitle,
                    Year = year,
                    Director = directors.Trim(),
                    Cast = actors.Trim(),
                    Country = regions.Trim(),
                    Language = languages.Trim(),
                    PosterUrl = poster,
                    Rating = (rating.HasValue && rating.Value > 0) ? rating : null,
                    RatingCount = (voteCount.HasValue && voteCount.Value > 0) ? (int?)voteCount.Value : null,
                    Source = "douban",
                    NormTitle = nt,
                    NormOriginal = normOrig,
                });
                if (!string.IsNullOrEmpty(poster)) report.NewWithPoster++;
                report.Inserted++;

                if (batch.Count >= Batch)
                {
                    await FlushInsertAsync(batch);
                    progress?.Report(new SeedProgress { Done = total, Total = total, Message = $"已导入 {report.Inserted} 条…" });
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "[SEED] CSV 读取中断于第 {Row} 行", total);
            report.Error = "读取中断：" + ex.Message;
        }

        if (batch.Count > 0) await FlushInsertAsync(batch);
        if (backfill.Count > 0) report.PosterFilled = await FlushBackfillAsync(backfill);

        progress?.Report(new SeedProgress { Done = total, Total = total, Message = "导入完成" });
        report.TotalRows = total;
        return report;
    }

    // 已有条目的字段完整度
    private sealed record Ext
    {
        public bool HasPoster { get; set; }
        public bool HasDirector { get; set; }
        public bool HasRating { get; set; }
        public bool HasCast { get; set; }
        public bool HasYear { get; set; }
        public bool HasCountry { get; set; }
        public bool HasLanguage { get; set; }
    }

    private static async Task FlushInsertAsync(List<CachedMovie> batch)
    {
        using var ctx = CacheDbContext.Create();
        ctx.CachedMovies.AddRange(batch);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        batch.Clear();
    }

    /// <summary>为已存在条目按字段补缺：仅填补空缺字段，不覆盖已有值。分块处理避免超大 IN 查询。</summary>
    private static async Task<int> FlushBackfillAsync(List<(string nt, CachedMovie data)> updates)
    {
        var byNt = updates.ToDictionary(u => u.nt, u => u.data, StringComparer.Ordinal);
        int done = 0;
        const int Chunk = 2000;
        using var ctx = CacheDbContext.Create();
        for (int i = 0; i < byNt.Count; i += Chunk)
        {
            var nts = byNt.Keys.Skip(i).Take(Chunk).ToList();
            var rows = ctx.CachedMovies.Where(c => nts.Contains(c.NormTitle)).AsEnumerable().ToList();
            foreach (var r in rows)
            {
                if (!byNt.TryGetValue(r.NormTitle, out var d)) continue;
                if (string.IsNullOrEmpty(r.PosterUrl) && !string.IsNullOrEmpty(d.PosterUrl)) r.PosterUrl = d.PosterUrl;
                if (string.IsNullOrEmpty(r.Director) && !string.IsNullOrEmpty(d.Director)) r.Director = d.Director;
                if ((!r.Rating.HasValue || r.Rating.Value <= 0) && d.Rating.HasValue && d.Rating.Value > 0) r.Rating = d.Rating;
                if (string.IsNullOrEmpty(r.Cast) && !string.IsNullOrEmpty(d.Cast)) r.Cast = d.Cast;
                if (r.Year == 0 && d.Year > 0) r.Year = d.Year;
                if (string.IsNullOrEmpty(r.Country) && !string.IsNullOrEmpty(d.Country)) r.Country = d.Country;
                if (string.IsNullOrEmpty(r.Language) && !string.IsNullOrEmpty(d.Language)) r.Language = d.Language;
                if ((!r.RatingCount.HasValue || r.RatingCount.Value <= 0) && d.RatingCount.HasValue && d.RatingCount.Value > 0) r.RatingCount = d.RatingCount;
            }
            await ctx.SaveChangesAsync();
            ctx.ChangeTracker.Clear();
            done += rows.Count;
        }
        return done;
    }

    /// <summary>探测编码(UTF-8 BOM/GBK)、分隔符(逗号/制表符)，并判断是否含 BOM。</summary>
    private static (Encoding enc, string delim, bool bom) ProbeEncodingAndDelimiter(string path)
    {
        using var fs = File.OpenRead(path);
        bool bom = false;
        var b = new byte[3];
        if (fs.Read(b, 0, 3) == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) bom = true;
        fs.Position = 0;

        Encoding enc;
        if (bom)
        {
            enc = new UTF8Encoding(false);
        }
        else
        {
            var buf = new byte[Math.Min(fs.Length, 1 << 20)];
            int n = fs.Read(buf, 0, buf.Length);
            try
            {
                _ = new UTF8Encoding(false, true).GetString(buf, 0, n);
                enc = new UTF8Encoding(false);
            }
            catch
            {
                try { enc = Encoding.GetEncoding("gbk"); }
                catch { enc = Encoding.GetEncoding("gb2312"); }
            }
            fs.Position = 0;
        }

        // 分隔符：读首行(跳过 BOM)统计制表符 vs 逗号
        fs.Position = bom ? 3 : 0;
        using var sr = new StreamReader(fs, enc, leaveOpen: true);
        var line = sr.ReadLine();
        int tabs = line?.Count(c => c == '\t') ?? 0;
        int commas = line?.Count(c => c == ',') ?? 0;
        return (enc, tabs > commas ? "\t" : ",", bom);
    }

    private static double? ParseDouble(string s)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }
    private static long? ParseLong(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        return null;
    }
}
