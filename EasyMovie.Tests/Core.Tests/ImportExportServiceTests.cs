using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using EasyMovie.Tools.ImportExport;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

public class ImportExportServiceTests : IDisposable
{
    private readonly MovieDbContext _context;
    private readonly ImportExportService _service;
    private readonly string _testDir;
    private readonly DbContextOptions<MovieDbContext> _options;

    public ImportExportServiceTests()
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _options = options;
        _context = new MovieDbContext(options);
        _service = new ImportExportService(_context);
        _testDir = Path.Combine(Path.GetTempPath(), "EasyMovieTests");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        _context.Dispose();
        try { Directory.Delete(_testDir, true); } catch { }
    }

    private string GetTempPath(string file) => Path.Combine(_testDir, file);

    // ═══════════════ CSV 导出 ═══════════════

    [Fact]
    public async Task ExportCsv_ShouldCreateFile_WithHeadersAndData()
    {
        var cat = new Category { Name = "科幻" };
        _context.Categories.Add(cat);
        _context.Movies.Add(new Movie
        {
            Title = "星际穿越", Year = 2014, Director = "诺兰",
            Rating = 9, WatchStatus = WatchStatus.Watched,
            IsFavorite = true, CategoryId = cat.Id
        });
        await _context.SaveChangesAsync();

        var path = GetTempPath("export_test.csv");
        await _service.ExportMoviesToCsvAsync(path);

        File.Exists(path).Should().BeTrue();
        var content = await File.ReadAllTextAsync(path, Encoding.UTF8);
        content.Should().Contain("片名");
        content.Should().Contain("星际穿越");
        content.Should().Contain("2014");
        content.Should().Contain("诺兰");
    }

    [Fact]
    public async Task ExportCsv_ShouldHandleEmptyDatabase()
    {
        var path = GetTempPath("export_empty.csv");
        await _service.ExportMoviesToCsvAsync(path);

        File.Exists(path).Should().BeTrue();
        var content = await File.ReadAllTextAsync(path, Encoding.UTF8);
        content.Should().Contain("片名"); // 有表头
    }

    // ═══════════════ CSV 导入 ═══════════════

    [Fact]
    public async Task ImportCsv_ShouldImportMovies()
    {
        var csv = "片名,原始片名,年份,导演,主演,国家,语言,片长,简介,评分,观看状态,观看日期,笔记,收藏\n"
                + "肖申克的救赎,The Shawshank Redemption,1994,弗兰克·德拉邦特,蒂姆·罗宾斯,美国,英语,142,经典越狱片,10,已看,2020-03-15,经典中的经典,是\n"
                + "教父,,,,,,,,,,想看,,,,";
        var path = GetTempPath("import_test.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        var result = await _service.ImportMoviesFromCsvAsync(path);

        result.SuccessCount.Should().Be(2);
        result.ErrorCount.Should().Be(0);
        result.ImportedMovies.Should().HaveCount(2);

        result.ImportedMovies[0].Title.Should().Be("肖申克的救赎");
        result.ImportedMovies[0].Year.Should().Be(1994);
        result.ImportedMovies[0].Rating.Should().Be(10);
        result.ImportedMovies[0].IsFavorite.Should().BeTrue();
        result.ImportedMovies[0].WatchStatus.Should().Be(WatchStatus.Watched);

        result.ImportedMovies[1].Title.Should().Be("教父");
        result.ImportedMovies[1].WatchStatus.Should().Be(WatchStatus.WantToWatch);
    }

    [Fact]
    public async Task ImportCsv_ShouldReturnError_WhenFileNotFound()
    {
        var path = GetTempPath("nonexistent.csv");
        var result = await _service.ImportMoviesFromCsvAsync(path);

        result.ErrorCount.Should().Be(1);
        result.Errors.Should().Contain(e => e.Contains("不存在"));
    }

    [Fact]
    public async Task ImportCsv_ShouldHandleMalformedData()
    {
        var csv = "片名,原始片名,年份,导演,主演,国家,语言,片长,简介,评分,观看状态,观看日期,笔记,收藏\n"
                + "正常电影,,,,,,,,,,,,,\n"
                + "坏电影, ,invalid_year,,,,,,,,,,,";
        var path = GetTempPath("import_bad.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        var result = await _service.ImportMoviesFromCsvAsync(path);

        result.SuccessCount.Should().BeGreaterOrEqualTo(1);
    }

    // ═══════════════ JSON 导出 ═══════════════

    [Fact]
    public async Task ExportJson_ShouldCreateValidJson()
    {
        _context.Movies.Add(new Movie { Title = "盗梦空间", Year = 2010, Director = "诺兰" });
        _context.Movies.Add(new Movie { Title = "黑客帝国", Year = 1999 });
        await _context.SaveChangesAsync();

        var path = GetTempPath("export_test.json");
        await _service.ExportMoviesToJsonAsync(path);

        File.Exists(path).Should().BeTrue();
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        json.Should().Contain("盗梦空间");
        json.Should().Contain("黑客帝国");
        json.Should().Contain("\"title\"");
    }

    // ═══════════════ JSON 导入 ═══════════════

    [Fact]
    public async Task ImportJson_ShouldImportMovies()
    {
        var json = @"[{
            ""title"": ""阿甘正传"",
            ""year"": 1994,
            ""director"": ""罗伯特·泽米吉斯"",
            ""rating"": 9,
            ""watchStatus"": 2,
            ""isFavorite"": true
        }]";
        var path = GetTempPath("import_test.json");
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);

        var result = await _service.ImportMoviesFromJsonAsync(path);

        result.SuccessCount.Should().Be(1);
        result.ImportedMovies[0].Title.Should().Be("阿甘正传");
        result.ImportedMovies[0].Rating.Should().Be(9);
    }

    [Fact]
    public async Task ImportJson_ShouldReturnError_WhenInvalidJson()
    {
        var path = GetTempPath("bad.json");
        await File.WriteAllTextAsync(path, "not valid json", Encoding.UTF8);

        var result = await _service.ImportMoviesFromJsonAsync(path);

        result.ErrorCount.Should().Be(1);
        result.Errors.Should().Contain(e => e.Contains("解析失败"));
    }

    [Fact]
    public async Task ImportJson_ShouldReturnError_WhenFileNotFound()
    {
        var path = GetTempPath("no_exist.json");
        var result = await _service.ImportMoviesFromJsonAsync(path);

        result.ErrorCount.Should().Be(1);
    }

    // ═══════════════ 全量备份/还原 ═══════════════

    [Fact]
    public async Task ExportFullBackup_ShouldCreateFile()
    {
        _context.Categories.Add(new Category { Name = "科幻" });
        _context.Tags.Add(new Tag { Name = "经典" });
        await _context.SaveChangesAsync();
        _context.Movies.Add(new Movie { Title = "星际穿越", Year = 2014,
            CategoryId = _context.Categories.First().Id });
        await _context.SaveChangesAsync();

        var path = GetTempPath("full_backup.json");
        await _service.ExportFullDataToJsonAsync(path);

        File.Exists(path).Should().BeTrue();
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        json.Should().Contain("星际穿越");
        json.Should().Contain("科幻");
        json.Should().Contain("经典"); // tag name
        json.Should().Contain("exportedAt");
        json.Should().Contain("\"version\"");
    }

    [Fact]
    public async Task ImportFullBackup_ShouldRestoreAllData()
    {
        // 先备份
        var cat = new Category { Name = "剧情" };
        var tag = new Tag { Name = "经典" };
        _context.Categories.Add(cat);
        _context.Tags.Add(tag);
        await _context.SaveChangesAsync();

        _context.Movies.Add(new Movie
        {
            Title = "肖申克的救赎", Year = 1994,
            CategoryId = cat.Id
        });
        await _context.SaveChangesAsync();

        var backupPath = GetTempPath("restore_test.json");
        await _service.ExportFullDataToJsonAsync(backupPath);

        // 清除数据
        _context.Movies.RemoveRange(_context.Movies);
        _context.Categories.RemoveRange(_context.Categories);
        _context.Tags.RemoveRange(_context.Tags);
        await _context.SaveChangesAsync();

        // 还原
        var result = await _service.ImportFullDataFromJsonAsync(backupPath);

        result.SuccessCount.Should().Be(1);
        _context.Movies.Count().Should().Be(1);
        _context.Categories.Count().Should().Be(1);
        _context.Tags.Count().Should().Be(1);

        var movie = _context.Movies.First();
        movie.Title.Should().Be("肖申克的救赎");
        movie.CategoryId.Should().NotBeNull();
    }

    // ═══════════════ 全量备份完整性（WatchLogs / Collections 曾被漏掉）═══════════════

    [Fact]
    public async Task FullBackup_ShouldIncludeWatchLogsAndCollections()
    {
        var movie = new Movie { Title = "星际穿越", Year = 2014 };
        _context.Movies.Add(movie);
        var collection = new MovieCollection { Name = "诺兰三部曲", SortOrder = 1 };
        _context.Collections.Add(collection);
        await _context.SaveChangesAsync();

        movie.CollectionId = collection.Id;
        _context.WatchLogs.Add(new WatchLog
        {
            MovieId = movie.Id,
            WatchDate = new DateTime(2026, 3, 15),
            Rating = 9,
            Location = "家里客厅",
            Companion = "和小明",
            Notes = "重温 Still Alive"
        });
        await _context.SaveChangesAsync();

        var path = GetTempPath("full_backup_completeness.json");
        await _service.ExportFullDataToJsonAsync(path);

        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        // 「全量」必须名副其实：日记与合集缺一不可，否则用户以为存下了观影历史
        json.Should().Contain("watchLogs");
        json.Should().Contain("Still Alive");
        json.Should().Contain("诺兰三部曲");
    }

    [Fact]
    public async Task ImportFullBackup_ShouldRestoreWatchLogsAndCollections()
    {
        var movie = new Movie { Title = "星际穿越", Year = 2014 };
        _context.Movies.Add(movie);
        var collection = new MovieCollection { Name = "诺兰三部曲", SortOrder = 1 };
        _context.Collections.Add(collection);
        await _context.SaveChangesAsync();

        movie.CollectionId = collection.Id;
        _context.WatchLogs.Add(new WatchLog
        {
            MovieId = movie.Id,
            WatchDate = new DateTime(2026, 3, 15),
            Rating = 9,
            Location = "家里客厅",
            Companion = "和小明",
            Notes = "重温 Still Alive"
        });
        await _context.SaveChangesAsync();

        var backupPath = GetTempPath("full_backup_restore_full.json");
        await _service.ExportFullDataToJsonAsync(backupPath);

        // 清空全部业务数据。
        // ⚠️ 必须 ChangeTracker.Clear()：否则被删实体仍留在变更跟踪里，
        // 还原时 _context.Movies.Add(同 Id 实体) 会撞上"同键实体已被跟踪"的跟踪冲突
        // （与生产侧 MovieDbContext 长生命周期 context 的清理契约同源）。
        _context.WatchLogs.RemoveRange(_context.WatchLogs);
        _context.Movies.RemoveRange(_context.Movies);
        _context.Collections.RemoveRange(_context.Collections);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var importResult = await _service.ImportFullDataFromJsonAsync(backupPath);

        // ⚠️ 关键：用全新 DbContext 读回（同 options 但新实例），否则会被同一 context
        // 的变更跟踪缓存掩盖成假阳性。与 MovieNarrowUpdateTests 保持同一范式。
        using var fresh = new MovieDbContext(_options);

        // 还原过程本身不能吞异常：ErrorCount>0 说明中途抛错被 catch 兜住，数据是残缺的
        importResult.ErrorCount.Should().Be(0, string.Join(" | ", importResult.Errors));
        importResult.SuccessCount.Should().Be(1);

        fresh.Collections.Count().Should().Be(1);
        fresh.WatchLogs.Count().Should().Be(1);

        var restoredCollection = fresh.Collections.Single();
        restoredCollection.Name.Should().Be("诺兰三部曲");

        // 影片的合集外键必须被换算到新合集 Id，而不是指向备份里的旧 Id
        var restoredMovie = fresh.Movies.Single();
        restoredMovie.CollectionId.Should().Be(restoredCollection.Id);

        var restoredLog = fresh.WatchLogs.Single();
        restoredLog.MovieId.Should().Be(restoredMovie.Id);
        restoredLog.Rating.Should().Be(9);
        restoredLog.Location.Should().Be("家里客厅");
        restoredLog.Companion.Should().Be("和小明");
        restoredLog.Notes.Should().Be("重温 Still Alive");
        restoredLog.WatchDate.Should().Be(new DateTime(2026, 3, 15));
    }

    [Fact]
    public async Task ImportFullBackup_ShouldAcceptLegacyBackupWithoutLogsAndCollections()
    {
        // 老备份（1.0 版）不含 WatchLogs / Collections 字段，反序列化得到 null。
        // 还原必须优雅跳过，不能因 null 抛异常—— 否则用户手里的历史备份文件就再也导不回来了。
        var legacyJson = """
        {
          "exportedAt": "2026-01-01T00:00:00Z",
          "version": "1.0",
          "movies": [ { "id": 1, "title": "老备份影片", "year": 2000, "watchStatus": 0, "isFavorite": false, "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z", "playbackPosition": 0 } ],
          "categories": [],
          "tags": []
        }
        """;

        var path = GetTempPath("legacy_backup.json");
        await File.WriteAllTextAsync(path, legacyJson, Encoding.UTF8);

        var result = await _service.ImportFullDataFromJsonAsync(path);

        result.SuccessCount.Should().Be(1);
        result.ErrorCount.Should().Be(0);
        _context.Movies.Single().Title.Should().Be("老备份影片");
        _context.WatchLogs.Should().BeEmpty();
    }

    // ═══════════════ 写入侧护栏（C 第一期）：脏值规整 ═══════════════

    [Fact]
    public async Task ImportCsv_ShouldSanitizeTitleAndZeroDirtyYear()
    {
        var csv = "片名,原始片名,年份,导演,主演,国家,语言,片长,简介,评分,观看状态,观看日期,笔记,收藏\n"
                + "三大队 Endless Journey EAC3,Endless Journey,1000,,,,,,,,,,,";
        var path = GetTempPath("dirty_csv.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        var result = await _service.ImportMoviesFromCsvAsync(path);

        // 脏值只做规整，不影响成功/失败计数语义
        result.SuccessCount.Should().Be(1);
        result.ErrorCount.Should().Be(0);

        var saved = _context.Movies.Single();
        saved.Title.Should().Be("三大队 Endless Journey");
        saved.Year.Should().Be(0);
        saved.OriginalTitle.Should().Be("Endless Journey");   // 原名不被清洗丢弃
    }

    [Fact]
    public async Task ImportJson_ShouldSanitizeTitleAndZeroDirtyYear()
    {
        var json = """
        [{
            "title": "地师传人Tomb Making Notes EAC3",
            "originalTitle": "Tomb Making Notes",
            "year": 1000,
            "filePath": "/tmp/dirty.mkv"
        }]
        """;
        var path = GetTempPath("dirty_import.json");
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);

        var result = await _service.ImportMoviesFromJsonAsync(path);

        result.SuccessCount.Should().Be(1);
        result.ErrorCount.Should().Be(0);

        var saved = _context.Movies.Single();
        saved.Title.Should().Be("地师传人Tomb Making Notes");
        saved.Year.Should().Be(0);
        saved.OriginalTitle.Should().Be("Tomb Making Notes");
        saved.FilePath.Should().Be("/tmp/dirty.mkv");          // 路径等原始信息保留
    }

    [Fact]
    public async Task ImportFullBackup_ShouldSanitizeTitleAndZeroDirtyYear()
    {
        var json = """
        {
          "exportedAt": "2026-01-01T00:00:00Z",
          "version": "1.1",
          "movies": [
            {
              "id": 7,
              "title": "奥本海默 TrueHD",
              "originalTitle": "Oppenheimer",
              "year": 1951,
              "filePath": "/tmp/oppenheimer.mkv",
              "watchStatus": 0,
              "isFavorite": false
            },
            {
              "id": 8,
              "title": "沙丘2 Dune: Part Two Atmos",
              "year": 1000,
              "filePath": "/tmp/dune2.mkv",
              "watchStatus": 0,
              "isFavorite": false
            }
          ],
          "categories": [],
          "tags": []
        }
        """;
        var path = GetTempPath("dirty_full_restore.json");
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);

        var result = await _service.ImportFullDataFromJsonAsync(path);

        result.SuccessCount.Should().Be(2);
        result.ErrorCount.Should().Be(0);

        var oppenheimer = _context.Movies.Single(m => m.FilePath == "/tmp/oppenheimer.mkv");
        oppenheimer.Title.Should().Be("奥本海默");
        oppenheimer.Year.Should().Be(1951);                    // 合法年份保持不动

        var dune = _context.Movies.Single(m => m.FilePath == "/tmp/dune2.mkv");
        dune.Title.Should().Be("沙丘2 Dune: Part Two");
        dune.Year.Should().Be(0);                              // 占位年份置 0
    }

    // ═══════════════ 映射辅助方法 ═══════════════

    [Fact]
    public async Task ImportCsv_ShouldMapWatchStatusCorrectly()
    {
        var csv = "片名,原始片名,年份,导演,主演,国家,语言,片长,简介,评分,观看状态,观看日期,笔记,收藏\n"
                + "未看片,,,,,,,,,,未看,,,,\n"
                + "想看片,,,,,,,,,,想看,,,,\n"
                + "已看片,,,,,,,,,,已看,2024-01-01,,,";
        var path = GetTempPath("status_test.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        var result = await _service.ImportMoviesFromCsvAsync(path);

        result.SuccessCount.Should().Be(3);
        result.ImportedMovies[0].WatchStatus.Should().Be(WatchStatus.NotWatched);
        result.ImportedMovies[1].WatchStatus.Should().Be(WatchStatus.WantToWatch);
        result.ImportedMovies[2].WatchStatus.Should().Be(WatchStatus.Watched);
        result.ImportedMovies[2].WatchDate.Should().Be(new DateTime(2024, 1, 1));
    }

    [Fact]
    public async Task ExportCsv_ShouldReImportCorrectly()
    {
        _context.Movies.Add(new Movie
        {
            Title = "往返测试", Year = 2022, Director = "测试导演",
            Rating = 7, WatchStatus = WatchStatus.Watched,
            WatchDate = new DateTime(2023, 6, 1), IsFavorite = true,
            Notes = "往返测试笔记"
        });
        await _context.SaveChangesAsync();

        var exportPath = GetTempPath("roundtrip.csv");
        await _service.ExportMoviesToCsvAsync(exportPath);

        // 清除后重新导入
        _context.Movies.RemoveRange(_context.Movies);
        await _context.SaveChangesAsync();

        var result = await _service.ImportMoviesFromCsvAsync(exportPath);
        result.SuccessCount.Should().Be(1);

        var imported = _context.Movies.First();
        imported.Title.Should().Be("往返测试");
        imported.Year.Should().Be(2022);
        imported.Rating.Should().Be(7);
        imported.IsFavorite.Should().BeTrue();
        imported.Notes.Should().Be("往返测试笔记");
    }
}
