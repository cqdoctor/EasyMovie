using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Data;

namespace EasyMovie.Tools.ImportExport;

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

            // 整个还原过程包裹在事务中：分类/标签/电影的分步写入要么全部提交，要么整体回滚，
            // 避免中途失败时数据库留下"半导入"的脏数据。
            // InMemory 等测试用 provider 不支持显式事务，此时跳过（await using 对 null 无操作）
            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync()
                : null;

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

            if (transaction != null)
                await transaction.CommitAsync();
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

        // 关闭连接会触发 WAL checkpoint，把 -wal 合并回主库；仍顺带拷贝 -wal/-shm 伴随文件以防残留未落盘。
        CopyDbWithCompanions(dbPath, backupPath);
    }

    public async Task RestoreDatabaseAsync(string backupPath)
    {
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("备份文件不存在", backupPath);

        var dbPath = GetActualDbPath();

        await _context.Database.CloseConnectionAsync();

        CopyDbWithCompanions(backupPath, dbPath);
    }

    /// <summary>拷贝 SQLite 主库及其 WAL/SHM 伴随文件（存在才拷）。</summary>
    private static void CopyDbWithCompanions(string src, string dst)
    {
        File.Copy(src, dst, true);
        foreach (var ext in new[] { "-wal", "-shm" })
        {
            var companion = src + ext;
            if (File.Exists(companion))
                File.Copy(companion, dst + ext, true);
        }
    }

    private static string GetActualDbPath()
    {
        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyMovie");
        var dbPath = Path.Combine(dbDir, "EasyMovie.db");
        if (File.Exists(dbPath)) return dbPath;

        var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EasyMovie.db");
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
                WatchStatus.NotWatched => "未看",
                WatchStatus.WantToWatch => "想看",
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
                "未看" => WatchStatus.NotWatched,
                "想看" => WatchStatus.WantToWatch,
                "已看" => WatchStatus.Watched,
                _ => WatchStatus.NotWatched
            },
            WatchDate = DateTime.TryParse(record.WatchDateStr, out var dt) ? dt : null,
            Notes = string.IsNullOrWhiteSpace(record.Notes) ? null : record.Notes.Trim(),
            IsFavorite = record.IsFavoriteStr?.Trim() == "是",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        return movie;
    }

    // ═══════════════════ Excel / HTML ═══════════════════

    public async Task ExportMoviesToExcelAsync(string filePath)
    {
        var movies = await _context.Movies
            .Include(m => m.Category)
            .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
            .AsNoTracking()
            .OrderBy(m => m.Title)
            .ToListAsync();
        var (headers, rows) = BuildExportRows(movies);
        await WriteExcelAsync(filePath, headers, rows);
    }

    public async Task ExportMoviesToHtmlAsync(string filePath)
    {
        var movies = await _context.Movies
            .Include(m => m.Category)
            .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
            .AsNoTracking()
            .OrderBy(m => m.Title)
            .ToListAsync();
        var (headers, rows) = BuildExportRows(movies);

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.Append("<title>EasyMovie 导出</title><style>");
        sb.Append("body{font-family:-apple-system,'Segoe UI',Roboto,sans-serif;margin:24px;color:#222}");
        sb.Append("h2{color:#3f51b5;margin-bottom:4px}p{color:#666;font-size:13px}");
        sb.Append("table{border-collapse:collapse;width:100%;font-size:13px;margin-top:12px}");
        sb.Append("th,td{border:1px solid #ddd;padding:6px 8px;text-align:left;vertical-align:top}");
        sb.Append("th{background:#3f51b5;color:#fff}tr:nth-child(even){background:#f6f6f6}");
        sb.Append("</style></head><body>");
        sb.Append($"<h2>EasyMovie 电影导出（共 {rows.Count} 部）</h2>");
        sb.Append($"<p>导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}</p>");
        sb.Append("<table><thead><tr>");
        foreach (var h in headers) sb.Append($"<th>{EscapeHtml(h)}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            foreach (var cell in row) sb.Append($"<td>{EscapeHtml(cell)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></body></html>");
        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
    }

    // 导出列：标题/原名/年份/导演/主演/国家/语言/片长/简介/评分/状态/观看日期/笔记/收藏/分类/标签
    // 数值列（年份=2、片长=7、评分=9）在 Excel 中输出为数字单元格
    private static readonly HashSet<int> NumericColumns = new() { 2, 7, 9 };

    private static (string[] Headers, List<string[]> Rows) BuildExportRows(List<Movie> movies)
    {
        string[] headers =
        {
            "标题", "原名", "年份", "导演", "主演", "国家/地区", "语言",
            "片长(分)", "简介", "评分", "状态", "观看日期", "笔记", "收藏", "分类", "标签"
        };
        var rows = new List<string[]>();
        foreach (var m in movies)
        {
            var tags = string.Join("、", m.MovieTags
                .Select(mt => mt.Tag?.Name)
                .Where(n => !string.IsNullOrEmpty(n)));
            rows.Add(new[]
            {
                m.Title,
                m.OriginalTitle ?? "",
                m.Year > 0 ? m.Year.ToString(CultureInfo.InvariantCulture) : "",
                m.Director ?? "",
                m.Cast ?? "",
                m.Country ?? "",
                m.Language ?? "",
                m.Runtime?.ToString(CultureInfo.InvariantCulture) ?? "",
                m.Synopsis ?? "",
                m.Rating?.ToString(CultureInfo.InvariantCulture) ?? "",
                m.WatchStatus switch
                {
                    WatchStatus.NotWatched => "未看",
                    WatchStatus.WantToWatch => "想看",
                    WatchStatus.Watched => "已看",
                    _ => ""
                },
                m.WatchDate?.ToString("yyyy-MM-dd") ?? "",
                m.Notes ?? "",
                m.IsFavorite ? "是" : "",
                m.Category?.Name ?? "",
                tags
            });
        }
        return (headers, rows);
    }

    private static Task WriteExcelAsync(string filePath, string[] headers, List<string[]> rows)
    {
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        // 表头（加粗样式 s="1"）
        sheet.Append("<row r=\"1\">");
        for (int c = 0; c < headers.Length; c++)
            sheet.Append($"<c r=\"{ColumnLetter(c)}1\" s=\"1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{EscapeXml(headers[c])}</t></is></c>");
        sheet.Append("</row>");
        // 数据行
        for (int r = 0; r < rows.Count; r++)
        {
            int rowNum = r + 2;
            sheet.Append($"<row r=\"{rowNum}\">");
            var row = rows[r];
            for (int c = 0; c < headers.Length; c++)
            {
                var val = c < row.Length ? row[c] ?? "" : "";
                var addr = ColumnLetter(c) + rowNum;
                if (NumericColumns.Contains(c) && !string.IsNullOrEmpty(val) && double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    sheet.Append($"<c r=\"{addr}\" s=\"0\"><v>{EscapeXml(val)}</v></c>");
                else
                    sheet.Append($"<c r=\"{addr}\" s=\"0\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{EscapeXml(val)}</t></is></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            AddZipEntry(zip, "[Content_Types].xml", ContentTypesXml);
            AddZipEntry(zip, "_rels/.rels", RelsXml);
            AddZipEntry(zip, "xl/workbook.xml", WorkbookXml);
            AddZipEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
            AddZipEntry(zip, "xl/styles.xml", StylesXml);
            AddZipEntry(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return File.WriteAllBytesAsync(filePath, ms.ToArray());
    }

    private static void AddZipEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static string ColumnLetter(int index)
    {
        int col = index + 1;
        var s = "";
        while (col > 0)
        {
            int rem = (col - 1) % 26;
            s = (char)('A' + rem) + s;
            col = (col - 1) / 26;
        }
        return s;
    }

    private static string EscapeXml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    private static string EscapeHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private const string ContentTypesXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private const string RelsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorkbookXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"电影\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

    private const string WorkbookRelsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    private const string StylesXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
        "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
        "<borders count=\"1\"><border/></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
        "</styleSheet>";
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
