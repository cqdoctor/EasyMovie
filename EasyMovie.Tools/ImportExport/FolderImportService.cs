using System.Text.RegularExpressions;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

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

    private readonly IMovieApiClient? _apiClient;

    public FolderImportService(IMovieApiClient? apiClient = null)
    {
        _apiClient = apiClient;
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
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        name = Regex.Replace(name, @"\[.*?\]", " ");
        name = Regex.Replace(name, @"\(.*?\)", " ");
        name = Regex.Replace(name, @"\b(4K|1080p|720p|2160p|BluRay|Blu-ray|WEB-DL|WEBRip|HDRip|BRRip|HDTV|x264|x265|H264|H265|AAC|DTS|DD5\.1|DD2\.0|HEVC|10bit|SDR|HDR|Remux|PROPER|REPACK|EXTENDED|UNCUT|Director.s.Cut|Theatrical.Cut)\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\b(\.|\-|_)\b", " ");

        int? year = null;
        var yearMatch = Regex.Match(name, @"\b(18[8-9]\d|19\d{2}|20[0-2]\d|2030)\b");
        if (yearMatch.Success)
        {
            year = int.Parse(yearMatch.Value);
            name = name.Replace(yearMatch.Value, "");
        }

        name = Regex.Replace(name, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(fileName);

        return (name, year);
    }

    public async Task<FolderImportResult> ImportFolderAsync(string folderPath, bool recursive, IMovieService movieService)
    {
        var result = new FolderImportResult();
        if (!Directory.Exists(folderPath)) { result.Errors.Add($"文件夹不存在: {folderPath}"); return result; }

        var files = await ScanFolderAsync(folderPath, recursive);
        result.TotalFiles = files.Count;
        result.VideoFiles = files.Count;

        // 获取所有已有电影的文件路径用于去重
        var (existing, _) = await movieService.SearchAsync(null, null, null, null, null, null, null, null, false, 1, int.MaxValue);
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

                // 🔍 自动从豆瓣/TMDB 获取元数据
                if (_apiClient != null && !string.IsNullOrWhiteSpace(title))
                {
                    try
                    {
                        var searchResponse = await _apiClient.SearchAsync(
                            new MovieSearchRequest { Keyword = title, Page = 1, PageSize = 1 });

                        if (searchResponse.Results.Count > 0)
                        {
                            var apiResult = searchResponse.Results[0];
                            // 年份匹配或接近才采用（±1年）
                            if (year == null || apiResult.Year == 0 ||
                                Math.Abs(apiResult.Year - (year ?? 0)) <= 1)
                            {
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

                                // 尝试获取详情（含完整简介）
                                var detail = await _apiClient.GetDetailAsync(
                                    apiResult.ExternalId ?? "");
                                if (detail != null)
                                {
                                    movie.Synopsis ??= detail.Synopsis;
                                    movie.Runtime ??= detail.Runtime;
                                    movie.Director ??= detail.Director;
                                    movie.Cast ??= detail.Cast;
                                    movie.Country ??= detail.Country;
                                }
                            }
                        }
                    }
                    catch { /* API 失败不影响导入 */ }
                }

                await movieService.AddAsync(movie);
                result.Imported++;
                result.ImportedMovies.Add(movie);
                existingPaths.Add(file);
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
