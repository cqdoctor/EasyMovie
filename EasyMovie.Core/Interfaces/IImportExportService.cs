using EasyMovie.Core.Models;

namespace EasyMovie.Core.Interfaces;

/// <summary>
/// 导入结果
/// </summary>
public class ImportResult
{
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<Movie> ImportedMovies { get; set; } = new();
}

/// <summary>
/// 导入导出服务接口
/// </summary>
public interface IImportExportService
{
    // CSV
    Task ExportMoviesToCsvAsync(string filePath);
    Task<ImportResult> ImportMoviesFromCsvAsync(string filePath);

    // JSON
    Task ExportMoviesToJsonAsync(string filePath);
    Task<ImportResult> ImportMoviesFromJsonAsync(string filePath);

    // 全量备份/还原
    Task ExportFullDataToJsonAsync(string filePath);
    Task<ImportResult> ImportFullDataFromJsonAsync(string filePath);

    // 数据库文件备份
    Task BackupDatabaseAsync(string backupPath);
    Task RestoreDatabaseAsync(string backupPath);
}
