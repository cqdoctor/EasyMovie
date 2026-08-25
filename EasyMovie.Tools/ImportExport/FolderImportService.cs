using EasyMovie.Core;
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
                if (existingPaths.Contains(file)) { result.Skipped++; continue; }

                var (title, year) = ParseFileName(file);
                var movie = new Movie
                {
                    Title = title,
                    Year = year ?? 0,
                    FilePath = file,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                // 🔍 自动从多数据源（豆瓣/TMDB/OMDb/百度百科）级联获取元数据，
                // MovieInfoFetcher 内部自带限流熔断与结果缓存，避免批量导入大量影片时
                // 单一数据源（如豆瓣）被反爬封禁导致全部匹配失败。
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
                            movie.Director = apiResult.Director;
                            movie.Cast = apiResult.Cast;
                            movie.Country = apiResult.Country;
                            movie.Synopsis = apiResult.Synopsis;
                            movie.PosterUrl = apiResult.PosterUrl;
                            movie.Runtime = apiResult.Runtime;

                            if (apiResult.Source == "douban")
                                movie.DoubanId = apiResult.ExternalId;
                            else if (apiResult.Source == "tmdb")
                                movie.TmdbId = apiResult.ExternalId;
                        }
                    }
                    catch (Exception ex) { Log.Error(ex, "文件夹导入时获取元数据失败，已跳过"); }
                }

                await movieService.AddAsync(movie);
                result.Imported++;
                result.ImportedMovies.Add(movie);
                existingPaths.Add(file);
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
}
