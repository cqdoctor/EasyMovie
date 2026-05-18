using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using MovieManager.Core.Enums;
using MovieManager.Core.Interfaces;
using MovieManager.Core.Models;
using MovieManager.Data;

namespace MovieManager.Tools.ImportExport;

/// <summary>
/// 导入导出服务实现
/// </summary>
public class ImportExportService : IImportExportService
{
    private readonly MovieDbContext _context;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    public ImportExportService(MovieDbContext context)
    {
        _context = context;
    }

    // ═══════════════════ CSV ═══════════════════

    public async Task ExportMoviesToCsvAsync(string filePath)
    {
        var movies = await _context.Movies
            .Include(m => m.Category)
            .ToListAsync();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Encoding = Encoding.UTF8,
            HasHeaderRecord = true
        };

        await using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        await using var csv = new CsvWriter(writer, config);

        // 写表头
        csv.WriteHeader<CsvMovieRecord>();
        await csv.NextRecordAsync();

        foreach (var movie in movies)
        {
            var record = MapToCsvRecord(movie);
            csv.WriteRecord(record);
            await csv.NextRecordAsync();
        }
    }

    public async Task<ImportResult> ImportMoviesFromCsvAsync(string filePath)
    {
        var result = new ImportResult();
        if (!File.Exists(filePath))
        {
            result.Errors.Add($"文件不存在: {filePath}");
            result.ErrorCount = 1;
            return result;
        }

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Encoding = Encoding.UTF8,
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
            ReadingExceptionOccurred = args => false // 跳过异常行
        };

        using var reader = new StreamReader(filePath, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        var records = csv.GetRecords<CsvMovieRecord>();

        foreach (var record in records)
        {
            try
            {
                var movie = MapFromCsvRecord(record);
                _context.Movies.Add(movie);
                result.SuccessCount++;
                result.ImportedMovies.Add(movie);
            }
            catch (Exception ex)
            {
                result.ErrorCount++;
                result.Errors.Add($"导入「{record.Title}」失败: {ex.Message}");
            }
        }

        if (result.SuccessCount > 0)
            await _context.SaveChangesAsync();

        return result;
    }

    // ═══════════════════ JSON ═══════════════════

    public async Task ExportMoviesToJsonAsync(string filePath)
    {
        var movies = await _context.Movies
            .Include(m => m.Category)
            .Include(m => m.MovieTags)
                .ThenInclude(mt => mt.Tag)
            .ToListAsync();

        var json = JsonSerializer.Serialize(movies, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
    }

    public async Task<ImportResult> ImportMoviesFromJsonAsync(string filePath)
    {
        var result = new ImportResult();
        if (!File.Exists(filePath))
        {
            result.Errors.Add($"文件不存在: {filePath}");
            result.ErrorCount = 1;
            return result;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            var movies = JsonSerializer.Deserialize<List<Movie>>(json, JsonOptions);

            if (movies == null || !movies.Any())
            {
                result.Errors.Add("JSON 文件为空或格式不正确");
                result.ErrorCount = 1;
                return result;
            }

            foreach (var movie in movies)
            {
                try
                {
                    // 清除导航属性避免EF跟踪问题
                    movie.Id = 0;
                    movie.Category = null;
                    movie.MovieTags?.Clear();
                    movie.CreatedAt = DateTime.UtcNow;
                    movie.UpdatedAt = DateTime.UtcNow;

                    _context.Movies.Add(movie);
                    result.SuccessCount++;
                    result.ImportedMovies.Add(movie);
                }
                catch (Exception ex)
                {
                    result.ErrorCount++;
                    result.Errors.Add($"导入「{movie.Title}」失败: {ex.Message}");
                }
            }

            if (result.SuccessCount > 0)
                await _context.SaveChangesAsync();

            return result;
        }
        catch (JsonException ex)
        {
            result.Errors.Add($"JSON 解析失败: {ex.Message}");
            result.ErrorCount = 1;
            return result;
        }
    }

    // ═══════════════════ 全量备份/还原 ═══════════════════

    public async Task ExportFullDataToJsonAsync(string filePath)
    {
        var movies = await _context.Movies
            .Include(m => m.Category)
            .Include(m => m.MovieTags)
                .ThenInclude(mt => mt.Tag)
            .ToListAsync();

        var categories = await _context.Categories.ToListAsync();
        var tags = await _context.Tags.ToListAsync();

        var backup = new FullDataBackup
        {
            ExportedAt = DateTime.UtcNow,
            Version = "1.0",
            Movies = movies,
            Categories = categories,
            Tags = tags
        };

        var json = JsonSerializer.Serialize(backup, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
    }

    public async Task<ImportResult> ImportFullDataFromJsonAsync(string filePath)
    {
        var result = new ImportResult();
        if (!File.Exists(filePath))
        {
            result.Errors.Add($"文件不存在: {filePath}");
            result.ErrorCount = 1;
            return result;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            var backup = JsonSerializer.Deserialize<FullDataBackup>(json, JsonOptions);

            if (backup == null)
            {
                result.Errors.Add("备份文件格式不正确");
                result.ErrorCount = 1;
                return result;
            }

            // 先导入分类
            var catIdMap = new Dictionary<int, int>();
            if (backup.Categories != null)
            {
                foreach (var cat in backup.Categories)
                {
                    var oldId = cat.Id;
                    cat.Id = 0;
                    cat.Movies?.Clear(); // 防止 EF 重复添加
                    cat.Parent = null;
                    cat.Children?.Clear();
                    _context.Categories.Add(cat);
                    await _context.SaveChangesAsync();
                    catIdMap[oldId] = cat.Id;
                }
            }

            // 导入标签
            var tagIdMap = new Dictionary<int, int>();
            if (backup.Tags != null)
            {
                foreach (var tag in backup.Tags)
                {
                    var oldId = tag.Id;
                    tag.Id = 0;
                    _context.Tags.Add(tag);
                    await _context.SaveChangesAsync();
                    tagIdMap[oldId] = tag.Id;
                }
            }

            // 导入电影
            if (backup.Movies != null)
            {
                foreach (var movie in backup.Movies)
                {
                    movie.Id = 0;

                    if (movie.CategoryId.HasValue && catIdMap.ContainsKey(movie.CategoryId.Value))
                        movie.CategoryId = catIdMap[movie.CategoryId.Value];
                    else
                        movie.CategoryId = null;

                    movie.Category = null;
                    movie.MovieTags?.Clear();
                    movie.CreatedAt = DateTime.UtcNow;
                    movie.UpdatedAt = DateTime.UtcNow;

                    _context.Movies.Add(movie);
                    result.SuccessCount++;
                    result.ImportedMovies.Add(movie);
                }

                if (result.SuccessCount > 0)
                    await _context.SaveChangesAsync();
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"还原失败: {ex.Message}");
            result.ErrorCount = 1;
            return result;
        }
    }

    // ═══════════════════ 数据库文件备份 ═══════════════════

    public async Task BackupDatabaseAsync(string backupPath)
    {
        var dbPath = GetActualDbPath();

        var dir = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await _context.Database.CloseConnectionAsync();

        File.Copy(dbPath, backupPath, true);
    }

    public async Task RestoreDatabaseAsync(string backupPath)
    {
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("备份文件不存在", backupPath);

        var dbPath = GetActualDbPath();

        await _context.Database.CloseConnectionAsync();

        File.Copy(backupPath, dbPath, true);
    }

    private static string GetActualDbPath()
    {
        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MovieManager");
        var dbPath = Path.Combine(dbDir, "MovieManager.db");
        if (File.Exists(dbPath)) return dbPath;

        var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MovieManager.db");
        if (File.Exists(fallback)) return fallback;

        return dbPath;
    }

    // ═══════════════════ 辅助方法 ═══════════════════

    private static CsvMovieRecord MapToCsvRecord(Movie movie)
    {
        return new CsvMovieRecord
        {
            Title = movie.Title,
            OriginalTitle = movie.OriginalTitle,
            Year = movie.Year,
            Director = movie.Director,
            Cast = movie.Cast,
            Country = movie.Country,
            Language = movie.Language,
            Runtime = movie.Runtime,
            Synopsis = movie.Synopsis,
            Rating = movie.Rating,
            WatchStatusStr = movie.WatchStatus switch
            {
                WatchStatus.WantToWatch => "想看",
                WatchStatus.Watching => "在看",
                WatchStatus.Watched => "已看",
                _ => ""
            },
            WatchDateStr = movie.WatchDate?.ToString("yyyy-MM-dd"),
            Notes = movie.Notes,
            IsFavoriteStr = movie.IsFavorite ? "是" : ""
        };
    }

    private static Movie MapFromCsvRecord(CsvMovieRecord record)
    {
        var movie = new Movie
        {
            Title = record.Title?.Trim() ?? "",
            OriginalTitle = string.IsNullOrWhiteSpace(record.OriginalTitle) ? null : record.OriginalTitle.Trim(),
            Year = record.Year ?? 0,
            Director = string.IsNullOrWhiteSpace(record.Director) ? null : record.Director.Trim(),
            Cast = string.IsNullOrWhiteSpace(record.Cast) ? null : record.Cast.Trim(),
            Country = string.IsNullOrWhiteSpace(record.Country) ? null : record.Country.Trim(),
            Language = string.IsNullOrWhiteSpace(record.Language) ? null : record.Language.Trim(),
            Runtime = record.Runtime,
            Synopsis = string.IsNullOrWhiteSpace(record.Synopsis) ? null : record.Synopsis.Trim(),
            Rating = record.Rating,
            WatchStatus = record.WatchStatusStr switch
            {
                "想看" => WatchStatus.WantToWatch,
                "在看" => WatchStatus.Watching,
                "已看" => WatchStatus.Watched,
                _ => WatchStatus.WantToWatch
            },
            WatchDate = DateTime.TryParse(record.WatchDateStr, out var dt) ? dt : null,
            Notes = string.IsNullOrWhiteSpace(record.Notes) ? null : record.Notes.Trim(),
            IsFavorite = record.IsFavoriteStr?.Trim() == "是",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        return movie;
    }
}

/// <summary>
/// 全量数据备份模型
/// </summary>
public class FullDataBackup
{
    public DateTime ExportedAt { get; set; }
    public string Version { get; set; } = "1.0";
    public List<Movie>? Movies { get; set; }
    public List<Category>? Categories { get; set; }
    public List<Tag>? Tags { get; set; }
}
