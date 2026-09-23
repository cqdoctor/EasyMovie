using EasyMovie.Core.Models;

namespace EasyMovie.Core.Interfaces;

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

    /// <summary>
    /// 导入**单个**视频文件：解析文件名 → 多源元数据级联 → 入库。
    /// 文件夹批量导入与文件夹监控（FolderWatcher）共用这一条实现，杜绝第二份副本。
    /// </summary>
    /// <param name="existingPaths">
    /// 调用方已预加载的库内文件路径集合（批量导入为避免 N 次查询而传入）；
    /// 传 null 则本方法自行做一次窄查询判断重复。命中时方法会把该路径补进集合。
    /// </param>
    /// <returns>已入库的 Movie；重复/失败返回 null。</returns>
    Task<Movie?> ImportFileAsync(string filePath, IMovieService movieService, ISet<string>? existingPaths = null);
}
