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

    public ImportExportServiceTests()
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
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

    // ═══════════════ 映射辅助方法 ═══════════════

    [Fact]
    public async Task ImportCsv_ShouldMapWatchStatusCorrectly()
    {
        var csv = "片名,原始片名,年份,导演,主演,国家,语言,片长,简介,评分,观看状态,观看日期,笔记,收藏\n"
                + "想看片,,,,,,,,,,想看,,,,\n"
                + "在看片,,,,,,,,,,在看,,,,\n"
                + "已看片,,,,,,,,,,已看,2024-01-01,,,";
        var path = GetTempPath("status_test.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        var result = await _service.ImportMoviesFromCsvAsync(path);

        result.SuccessCount.Should().Be(3);
        result.ImportedMovies[0].WatchStatus.Should().Be(WatchStatus.WantToWatch);
        result.ImportedMovies[1].WatchStatus.Should().Be(WatchStatus.Watching);
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
