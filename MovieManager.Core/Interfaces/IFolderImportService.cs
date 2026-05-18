using MovieManager.Core.Models;

namespace MovieManager.Core.Interfaces;

/// <summary>
/// 文件夹导入结果
/// </summary>
public class FolderImportResult
{
    public int TotalFiles { get; set; }
    public int VideoFiles { get; set; }
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<Movie> ImportedMovies { get; set; } = new();
}

/// <summary>
/// 文件夹导入服务接口
/// </summary>
public interface IFolderImportService
{
    /// <summary>扫描文件夹中的视频文件</summary>
    Task<List<string>> ScanFolderAsync(string folderPath, bool recursive);

    /// <summary>从文件名解析电影信息</summary>
    (string title, int? year) ParseFileName(string fileName);

    /// <summary>导入文件夹中的所有视频文件</summary>
    Task<FolderImportResult> ImportFolderAsync(string folderPath, bool recursive, IMovieService movieService);
}
