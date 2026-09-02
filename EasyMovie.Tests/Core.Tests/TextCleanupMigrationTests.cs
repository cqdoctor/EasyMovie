using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// TextCleanupMigration 行为锁定测试。
///
/// 为什么要有这份测试：这段迁移**改写用户真实数据**，而它此前住在 WPF 的
/// <c>DbHelper</c> 里（Client 层零测试覆盖），只能靠"启动一次 GUI"才能验证。
/// 搬到 Data 层后即可拿临时 SQLite 库跑真实迁移，把下面三条最关键的契约钉死：
///   1. 脏文本会被清洗（HTML 标签、导演职位标签）
///   2. **绝不触碰 PosterData 等未被清洗的列**（桩实体按列标脏，整行回写会把海报清空）
///   3. 标志文件存在时整个迁移被跳过（幂等，不重复写库）
///
/// 这些断言描述的是迁移的**真实行为**，不是理想行为。改语义时请显式修改本测试并说明理由。
/// </summary>
public class TextCleanupMigrationTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles.SelectMany(p => new[] { p, p + "-wal", p + "-shm" }))
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* 清理失败不影响测试结果 */ }
        }
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* 同上 */ }
        }
    }

    private string NewTempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EasyMovieTextMigration");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"mig_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return path;
    }

    private string NewTempFlagPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EasyMovieTextMigration", $"flag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return Path.Combine(dir, ".html_cleaned_v3");
    }

    private static MovieDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new MovieDbContext(options);
    }

    private static DbContextOptions<MovieDbContext> CreateOptions(string path)
        => new DbContextOptionsBuilder<MovieDbContext>().UseSqlite($"Data Source={path}").Options;

    /// <summary>
    /// 种子：一条脏到骨子里的记录 + 一条干净记录。
    /// 脏记录覆盖 Synopsis/Cast/Country/Notes 的 HTML 标签与 Director 的职位标签。
    /// </summary>
    private static (int dirtyId, int cleanId, byte[] poster) Seed(string path)
    {
        using var ctx = CreateContext(path);
        ctx.Database.EnsureCreated();

        var poster = new byte[4096];
        new Random(7).NextBytes(poster);

        var dirty = new Movie
        {
            Title = "脏片 EAC3",
            Year = 2020,
            Synopsis = "<p>第一段</p><p>第二段</p>",
            Cast = "<div>张三</div>&amp;<div>李四</div>",
            Country = "<span>中国</span>",
            Notes = "<b>备注</b>",
            Director = "编剧",                      // 整值命中 InvalidPersonLabels
            PosterData = poster,
            Rating = 9,
            WatchStatus = WatchStatus.Watched,
            FilePath = @"D:\movies\dirty.mkv",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var clean = new Movie
        {
            Title = "干净片",
            Year = 2021,
            Synopsis = "一段纯文本简介",
            Cast = "张三 / 李四",
            Country = "中国",
            Notes = "无",
            Director = "诺兰",
            PosterData = poster,
            Rating = 8,
            WatchStatus = WatchStatus.NotWatched,
            FilePath = @"D:\movies\clean.mkv",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        ctx.Movies.AddRange(dirty, clean);
        ctx.SaveChanges();

        return (dirty.Id, clean.Id, poster);
    }

    [Fact]
    public void Migration_CleansHtmlFromAllTextFields()
    {
        var path = NewTempDb();
        var (dirtyId, _, _) = Seed(path);

        var changed = TextCleanupMigration.CleanHtmlInExistingData(CreateOptions(path), NewTempFlagPath());

        using var ctx = CreateContext(path);
        var m = ctx.Movies.AsNoTracking().Single(x => x.Id == dirtyId);
        // 已知行为（非理想行为，此处刻意锁定）：相邻标签之间的文本会被**直接拼接**，
        // 中间不插空格 —— <p>第一段</p><p>第二段</p> → "第一段第二段"。
        // 改语义属独立变更，不应在搬移过程中顺手改实现。
        Assert.Equal("第一段第二段", m.Synopsis);
        Assert.Equal("张三&李四", m.Cast);   // &amp; 被解码为 &；标签本身不留空格
        Assert.Equal("中国", m.Country);
        Assert.Equal("备注", m.Notes);
        Assert.Equal(1, changed);              // 只有脏记录被改写
    }

    [Fact]
    public void Migration_TurnsJobLabelDirectorIntoNull()
    {
        var path = NewTempDb();
        var (dirtyId, _, _) = Seed(path);

        TextCleanupMigration.CleanHtmlInExistingData(CreateOptions(path), NewTempFlagPath());

        using var ctx = CreateContext(path);
        // 「编剧」是职位标签而非人名：清洗后应归一为 null，而不是留下空串
        Assert.Null(ctx.Movies.AsNoTracking().Single(x => x.Id == dirtyId).Director);
    }

    /// <summary>
    /// 最关键的一条：桩实体 + 按列标脏的意义就在**只写该写的列**。
    /// 若有人改成"加载整行再 SaveChanges"，海报会在这里被清空（用户库 99.4% 的体积是海报）。
    /// </summary>
    [Fact]
    public void Migration_PreservesPosterDataAndUntouchedColumns()
    {
        var path = NewTempDb();
        var (dirtyId, _, poster) = Seed(path);

        TextCleanupMigration.CleanHtmlInExistingData(CreateOptions(path), NewTempFlagPath());

        using var ctx = CreateContext(path);
        var m = ctx.Movies.AsNoTracking().Single(x => x.Id == dirtyId);
        Assert.Equal(poster, m.PosterData);          // BLOB 逐字节未变
        Assert.Equal(9, m.Rating);
        Assert.Equal(WatchStatus.Watched, m.WatchStatus);
        Assert.Equal(@"D:\movies\dirty.mkv", m.FilePath);
        Assert.Equal(2020, m.Year);
        Assert.Equal("脏片 EAC3", m.Title);          // 清洗范围不含 Title
    }

    [Fact]
    public void Migration_LeavesCleanRowsAlone()
    {
        var path = NewTempDb();
        var (_, cleanId, _) = Seed(path);

        TextCleanupMigration.CleanHtmlInExistingData(CreateOptions(path), NewTempFlagPath());

        using var ctx = CreateContext(path);
        var m = ctx.Movies.AsNoTracking().Single(x => x.Id == cleanId);
        Assert.Equal("一段纯文本简介", m.Synopsis);
        Assert.Equal("诺兰", m.Director);
        Assert.Equal("无", m.Notes);
    }

    [Fact]
    public void Migration_IsIdempotentViaFlagFile()
    {
        var path = NewTempDb();
        Seed(path);
        var flag = NewTempFlagPath();

        var first = TextCleanupMigration.CleanHtmlInExistingData(CreateOptions(path), flag);
        Assert.Equal(1, first);
        Assert.True(File.Exists(flag));

        // 标志文件已存在 → 第二次整个跳过（返回 0），即使此时又有人写进了新的脏数据
        using (var ctx = CreateContext(path))
        {
            var m = ctx.Movies.Single(x => x.Synopsis == "第一段第二段");
            m.Synopsis = "<p>又被写脏了</p>";
            ctx.SaveChanges();
        }

        var second = TextCleanupMigration.CleanHtmlInExistingData(CreateOptions(path), flag);
        Assert.Equal(0, second);

        using var check = CreateContext(path);
        // 这正是"标志文件一存在就永远跳过"的代价：新脏数据不会被再清理，
        // 所以扩大清洗范围必须升版本号（v2 → v3）。
        Assert.Equal("<p>又被写脏了</p>", check.Movies.AsNoTracking().Single(x => x.Synopsis == "<p>又被写脏了</p>").Synopsis);
    }

    [Fact]
    public void Migration_EmptyFlagPath_Throws()
    {
        var path = NewTempDb();
        Seed(path);
        Assert.Throws<ArgumentException>(() => TextCleanupMigration.CleanHtmlInExistingData(CreateOptions(path), ""));
    }

    [Fact]
    public void StripHtml_DecodesEntitiesAndCollapsesWhitespace()
    {
        Assert.Equal("A & B", TextCleanupMigration.StripHtml("A &amp; B"));
        Assert.Equal("a b", TextCleanupMigration.StripHtml("a\n\t  b"));
        Assert.Equal("文本", TextCleanupMigration.StripHtml("<p>文本</p>"));
    }

    [Fact]
    public void StripHtml_NullOrEmpty_PassesThroughOrNull()
    {
        // 实测行为（写测试时原本以为 "" 也会归 null，实际不会）：
        // 开头的 string.IsNullOrEmpty 短路让 null 与 "" **原样返回**，
        // 只有"非空、但清洗后变空"的串（纯标签 / 纯空白）才归一为 null。
        // 因此库中已存在的空串不会被本迁移改成 null —— 属已知行为，此处锁定。
        Assert.Null(TextCleanupMigration.StripHtml(null));
        Assert.Equal("", TextCleanupMigration.StripHtml(""));
        Assert.Null(TextCleanupMigration.StripHtml("<p></p>"));
        Assert.Null(TextCleanupMigration.StripHtml("   "));
    }
}
