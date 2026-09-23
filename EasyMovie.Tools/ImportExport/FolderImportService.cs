using EasyMovie.Core;
using EasyMovie.Core.Helpers;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Tools.MovieApi;
using Serilog;

namespace EasyMovie.Tools.ImportExport;

/// <summary>
/// 文件夹导入服务 - 扫描视频文件 + 自动获取豆瓣/TMDB 元数据
/// </summary>
public class FolderImportService : IFolderImportService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
        ".m4v", ".mpg", ".mpeg", ".ts", ".rmvb", ".rm", ".3gp", ".vob"
    };

    public FolderImportService(IMovieApiClient? apiClient = null)
    {
        // apiClient 参数保留以兼容旧调用方；实际元数据获取统一走 MovieInfoFetcher
        // （多源级联 + 限流熔断 + 结果缓存），避免批量导入时单一源被封禁导致全部失败。
    }

    public Task<List<string>> ScanFolderAsync(string folderPath, bool recursive)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.GetFiles(folderPath, "*.*", option)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .ToList();
        return Task.FromResult(files);
    }

    public (string title, int? year) ParseFileName(string fileName)
        => FileNameParser.Parse(fileName);

    public async Task<FolderImportResult> ImportFolderAsync(string folderPath, bool recursive, IMovieService movieService)
    {
        var result = new FolderImportResult();
        if (!Directory.Exists(folderPath)) { result.Errors.Add($"文件夹不存在: {folderPath}"); return result; }

        var files = await ScanFolderAsync(folderPath, recursive);
        result.TotalFiles = files.Count;
        result.VideoFiles = files.Count;

        // 获取所有已有电影的文件路径用于去重
        var (existing, _) = await movieService.SearchAsync(null, null, null, null, null, null, null, null, null, null, null, null, null, null, false, 1, int.MaxValue);
        var existingPaths = existing.Where(m => m.FilePath != null).Select(m => m.FilePath!).ToHashSet();

        foreach (var file in files)
        {
            try
            {
                // 复用单文件导入实现（FolderWatcher 走的是同一条路径）
                var movie = await ImportFileAsync(file, movieService, existingPaths);
                if (movie == null) { result.Skipped++; continue; }

                result.Imported++;
                result.ImportedMovies.Add(movie);
                // 从黑名单中移除（用户手动导入）
                AppSettings.DeletedFilePaths.Remove(file);
            }
            catch (Exception ex)
            {
                result.Skipped++;
                result.Errors.Add($"导入失败「{Path.GetFileName(file)}」: {ex.Message}");
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<Movie?> ImportFileAsync(string filePath, IMovieService movieService, ISet<string>? existingPaths = null)
    {
        // 去重：批量导入由调用方预加载的集合兜住（避免 N 次查询）；单文件导入自行查一次窄投影。
        if (existingPaths != null)
        {
            if (existingPaths.Contains(filePath)) return null;
        }
        else
        {
            if (await movieService.ExistsByFilePathAsync(filePath)) return null;
        }

        var (title, year) = ParseFileName(filePath);
        var movie = new Movie
        {
            Title = title,
            Year = year ?? 0,
            FilePath = filePath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // 🔍 自动从多数据源（豆瓣/TMDB/OMDb/百度百科）级联获取元数据，
        // MovieInfoFetcher 内部自带限流熔断与结果缓存，避免单一数据源被反爬封禁导致匹配失败。
        if (!string.IsNullOrWhiteSpace(title))
        {
            try
            {
                var fetcher = new MovieInfoFetcher();
                var fetchResult = await fetcher.FetchAsync(movie);
                if (fetchResult.Success && fetchResult.Info != null)
                {
                    var apiResult = fetchResult.Info;
                    movie.Title = apiResult.Title;
                    movie.OriginalTitle = apiResult.OriginalTitle;
                    movie.Year = apiResult.Year > 0 ? apiResult.Year : (year ?? 0);
                    movie.Director = MovieCreditCleaner.CleanDirector(apiResult.Director);
                    movie.Cast = TextCleaner.StripHtml(apiResult.Cast);
                    movie.Country = TextCleaner.StripHtml(apiResult.Country);
                    movie.Synopsis = TextCleaner.StripHtml(apiResult.Synopsis);
                    movie.PosterUrl = apiResult.PosterUrl;
                    movie.Runtime = apiResult.Runtime;

                    if (apiResult.Source == "douban")
                        movie.DoubanId = apiResult.ExternalId;
                    else if (apiResult.Source == "tmdb")
                        movie.TmdbId = apiResult.ExternalId;
                }
            }
            catch (Exception ex) { Log.Error(ex, "获取元数据失败，已跳过: {File}", Path.GetFileName(filePath)); }
        }

        // 走 movieService.AddAsync 而非直接 ctx.Movies.Add：前者会补建拼音搜索索引（SearchIndex）
        await movieService.AddAsync(movie);
        existingPaths?.Add(filePath);
        return movie;
    }
}
