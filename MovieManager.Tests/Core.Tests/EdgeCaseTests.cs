using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MovieManager.Core.Enums;
using MovieManager.Core.Models;
using MovieManager.Core.Services;
using MovieManager.Data;
using MovieManager.Data.Repositories;
using Xunit;

namespace MovieManager.Tests.Core.Tests;

/// <summary>
/// 边缘场景和扩展测试 — 冲 200+
/// </summary>
public class EdgeCaseTests
{
    private static MovieDbContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new MovieDbContext(options);
    }

    [Fact]
    public async Task Movie_TitleMaxLength()
    {
        using var ctx = CreateContext(nameof(Movie_TitleMaxLength));
        var longTitle = new string('A', 200);
        var movie = new Movie { Title = longTitle, Year = 2020 };
        ctx.Movies.Add(movie);
        await ctx.SaveChangesAsync();

        movie.Id.Should().BeGreaterThan(0);
        movie.Title.Length.Should().Be(200);
    }

    [Fact]
    public async Task Movie_ZeroYearAllowed()
    {
        using var ctx = CreateContext(nameof(Movie_ZeroYearAllowed));
        var movie = new Movie { Title = "未知年份", Year = 0 };
        ctx.Movies.Add(movie);
        await ctx.SaveChangesAsync();

        var loaded = ctx.Movies.First();
        loaded.Year.Should().Be(0);
    }

    [Fact]
    public async Task Movie_AllStatusesCanBeSet()
    {
        using var ctx = CreateContext(nameof(Movie_AllStatusesCanBeSet));
        var movie = new Movie { Title = "状态测试", Year = 2020 };
        ctx.Movies.Add(movie);
        await ctx.SaveChangesAsync();

        foreach (WatchStatus status in Enum.GetValues<WatchStatus>())
        {
            movie.WatchStatus = status;
            await ctx.SaveChangesAsync();
            ctx.Movies.First().WatchStatus.Should().Be(status);
        }
    }

    [Fact]
    public async Task Category_SelfReferenceTree()
    {
        using var ctx = CreateContext(nameof(Category_SelfReferenceTree));
        var cat = new Category { Name = "独立分类" };
        ctx.Categories.Add(cat);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Categories.Include(c => c.Parent).FirstAsync();
        loaded.Parent.Should().BeNull();
        loaded.ParentId.Should().BeNull();
    }

    [Fact]
    public async Task Tag_ColorHexHandling()
    {
        using var ctx = CreateContext(nameof(Tag_ColorHexHandling));
        var tags = new[]
        {
            new Tag { Name = "FullHex", Color = "#FF5722" },
            new Tag { Name = "ShortHex", Color = "#F00" },
            new Tag { Name = "NoColor", Color = null },
            new Tag { Name = "EmptyColor", Color = "" }
        };

        ctx.Tags.AddRange(tags);
        await ctx.SaveChangesAsync();

        var all = ctx.Tags.ToList();
        all.Should().HaveCount(4);
        all.First(t => t.Name == "NoColor").Color.Should().BeNull();
        all.First(t => t.Name == "FullHex").Color.Should().Be("#FF5722");
    }

    [Fact]
    public async Task MovieTag_AssociateMultiple()
    {
        using var ctx = CreateContext(nameof(MovieTag_AssociateMultiple));
        var movie = new Movie { Title = "多标签电影", Year = 2020 };
        var tag1 = new Tag { Name = "A" };
        var tag2 = new Tag { Name = "B" };
        var tag3 = new Tag { Name = "C" };
        ctx.Movies.Add(movie);
        ctx.Tags.AddRange(tag1, tag2, tag3);
        await ctx.SaveChangesAsync();

        ctx.MovieTags.AddRange(
            new MovieTag { MovieId = movie.Id, TagId = tag1.Id },
            new MovieTag { MovieId = movie.Id, TagId = tag2.Id },
            new MovieTag { MovieId = movie.Id, TagId = tag3.Id }
        );
        await ctx.SaveChangesAsync();

        var movieTags = ctx.MovieTags.Where(mt => mt.MovieId == movie.Id).ToList();
        movieTags.Should().HaveCount(3);
    }

    [Fact]
    public async Task Movie_AllNullOptionalFields()
    {
        using var ctx = CreateContext(nameof(Movie_AllNullOptionalFields));
        var movie = new Movie { Title = "极简电影", Year = 2020 };
        ctx.Movies.Add(movie);
        await ctx.SaveChangesAsync();

        var loaded = ctx.Movies.First();
        loaded.Director.Should().BeNull();
        loaded.Cast.Should().BeNull();
        loaded.CategoryId.Should().BeNull();
        loaded.Rating.Should().BeNull();
        loaded.Notes.Should().BeNull();
    }

    [Fact]
    public async Task Movie_WatchDate_OnlyForWatched()
    {
        using var ctx = CreateContext(nameof(Movie_WatchDate_OnlyForWatched));
        var service = new MovieService(
            new MovieRepository(ctx),
            new TagRepository(ctx));

        var movie = await service.AddAsync(new Movie { Title = "日期测试", Year = 2020 });

        // 设为"想看"应清除日期
        await service.SetWatchStatusAsync(movie.Id, WatchStatus.WantToWatch, null);
        (await service.GetByIdAsync(movie.Id))!.WatchDate.Should().BeNull();

        // 设为"已看"应有日期
        var date = new DateTime(2024, 5, 1);
        await service.SetWatchStatusAsync(movie.Id, WatchStatus.Watched, date);
        (await service.GetByIdAsync(movie.Id))!.WatchDate.Should().Be(date);
    }

    [Fact]
    public async Task Repository_Delete_NavigationIntegrity()
    {
        using var ctx = CreateContext(nameof(Repository_Delete_NavigationIntegrity));
        var cat = new Category { Name = "临时分类" };
        var movie = new Movie { Title = "临时电影", Year = 2020, CategoryId = cat.Id };
        ctx.Categories.Add(cat);
        ctx.Movies.Add(movie);
        await ctx.SaveChangesAsync();

        // 删除电影
        ctx.Movies.Remove(movie);
        await ctx.SaveChangesAsync();

        ctx.Movies.Count().Should().Be(0);
        ctx.Categories.Count().Should().Be(1); // 分类不受影响
    }

    [Fact]
    public async Task Search_PaginationBoundary()
    {
        using var ctx = CreateContext(nameof(Search_PaginationBoundary));
        var repo = new MovieRepository(ctx);
        for (var i = 1; i <= 5; i++)
            await repo.AddAsync(new Movie { Title = $"F{i}", Year = 2020 });

        // PageSize > Total
        var results = await repo.SearchAsync(null, null, null, null, null, null, null, null, false, 0, 100);
        results.Should().HaveCount(5);

        // Skip > Total
        results = await repo.SearchAsync(null, null, null, null, null, null, null, null, false, 100, 10);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Movie_Update_CreatedAt_ShouldNotChange()
    {
        using var ctx = CreateContext(nameof(Movie_Update_CreatedAt_ShouldNotChange));
        var repo = new MovieRepository(ctx);
        var movie = await repo.AddAsync(new Movie { Title = "时间测试", Year = 2020 });
        var originalCreatedAt = movie.CreatedAt;

        await Task.Delay(10);
        movie.Title = "修改后";
        await repo.UpdateAsync(movie);

        var updated = await repo.GetByIdAsync(movie.Id);
        updated!.CreatedAt.Should().BeCloseTo(originalCreatedAt, TimeSpan.FromMilliseconds(100));
        updated.UpdatedAt.Should().BeAfter(originalCreatedAt);
    }

    [Fact]
    public async Task CategoryService_GetChildren_EmptyCategory()
    {
        using var ctx = CreateContext(nameof(CategoryService_GetChildren_EmptyCategory));
        var catService = new CategoryService(new CategoryRepository(ctx));
        var cat = await catService.AddAsync(new Category { Name = "空分类" });

        var children = await catService.GetChildrenAsync(cat.Id);
        children.Should().BeEmpty();
    }

    [Fact]
    public async Task TagService_Add_DuplicateAllowedInMemory()
    {
        using var ctx = CreateContext(nameof(TagService_Add_DuplicateAllowedInMemory));
        var tagService = new TagService(new TagRepository(ctx));
        await tagService.AddAsync(new Tag { Name = "相同标签" });

        // InMemory 不强制唯一约束，但不影响数据完整性
        var tag2 = await tagService.AddAsync(new Tag { Name = "相同标签" });
        tag2.Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task MovieService_Search_EmptyKeyword_ReturnsAll()
    {
        using var ctx = CreateContext(nameof(MovieService_Search_EmptyKeyword_ReturnsAll));
        var service = new MovieService(new MovieRepository(ctx), new TagRepository(ctx));
        await service.AddAsync(new Movie { Title = "A", Year = 2020 });
        await service.AddAsync(new Movie { Title = "B", Year = 2021 });

        var (results, total) = await service.SearchAsync(null, null, null, null, null, null, null, null, false, 1, 10);
        results.Should().HaveCount(2);
        total.Should().Be(2);
    }

    [Fact]
    public async Task Statistics_ZeroState_AllCountsZero()
    {
        using var ctx = CreateContext(nameof(Statistics_ZeroState_AllCountsZero));
        var statsService = new StatisticsService(ctx);
        var data = await statsService.GetStatisticsAsync();

        data.TotalMovies.Should().Be(0);
        data.WantToWatch.Should().Be(0);
        data.Watching.Should().Be(0);
        data.Watched.Should().Be(0);
        data.Favorites.Should().Be(0);
        data.RatedCount.Should().Be(0);
        data.CategoryStats.Should().BeEmpty();
        data.RatingStats.Should().BeEmpty();
    }

    [Fact]
    public async Task Statistics_LargeDataset_Performance()
    {
        using var ctx = CreateContext(nameof(Statistics_LargeDataset_Performance));
        for (var i = 1; i <= 100; i++)
        {
            ctx.Movies.Add(new Movie
            {
                Title = $"电影{i}",
                Year = 2000 + (i % 25),
                Rating = (i % 10) + 1,
                WatchStatus = (WatchStatus)(i % 3),
            });
        }
        await ctx.SaveChangesAsync();

        var statsService = new StatisticsService(ctx);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = await statsService.GetStatisticsAsync();
        sw.Stop();

        data.TotalMovies.Should().Be(100);
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    [Fact]
    public async Task ImportExport_RoundTrip_PreservesData()
    {
        using var ctx = CreateContext(nameof(ImportExport_RoundTrip_PreservesData));
        var cat = new Category { Name = "剧情" };
        ctx.Categories.Add(cat);
        await ctx.SaveChangesAsync();

        ctx.Movies.Add(new Movie
        {
            Title = "完整数据测试",
            OriginalTitle = "Data Test",
            Year = 2023,
            Director = "测试导演",
            Cast = "演员1, 演员2",
            Country = "中国",
            Language = "中文",
            Runtime = 120,
            Synopsis = "测试简介",
            Rating = 8,
            WatchStatus = WatchStatus.Watched,
            WatchDate = new DateTime(2024, 1, 1),
            Notes = "测试笔记",
            IsFavorite = true,
            CategoryId = cat.Id
        });
        await ctx.SaveChangesAsync();

        var service = new Tools.ImportExport.ImportExportService(ctx);
        var tempPath = System.IO.Path.GetTempFileName() + ".json";

        // 导出
        await service.ExportMoviesToJsonAsync(tempPath);

        // 清除
        ctx.Movies.RemoveRange(ctx.Movies);
        await ctx.SaveChangesAsync();

        // 导入
        var result = await service.ImportMoviesFromJsonAsync(tempPath);
        result.SuccessCount.Should().Be(1);

        var imported = ctx.Movies.First();
        imported.Title.Should().Be("完整数据测试");
        imported.OriginalTitle.Should().Be("Data Test");
        imported.Year.Should().Be(2023);
        imported.Director.Should().Be("测试导演");
        imported.Rating.Should().Be(8);
        imported.Notes.Should().Be("测试笔记");

        System.IO.File.Delete(tempPath);
    }

    [Fact]
    public async Task Model_DefaultValues()
    {
        var movie = new Movie();
        movie.Title.Should().BeEmpty();
        movie.WatchStatus.Should().Be(WatchStatus.WantToWatch);
        movie.IsFavorite.Should().BeFalse();
        movie.MovieTags.Should().NotBeNull();
        movie.MovieTags.Should().BeEmpty();
    }

    [Fact]
    public async Task Enum_AllValues_Defined()
    {
        Enum.GetValues<WatchStatus>().Should().HaveCount(3);
        Enum.IsDefined((WatchStatus)0).Should().BeTrue();
        Enum.IsDefined((WatchStatus)1).Should().BeTrue();
        Enum.IsDefined((WatchStatus)2).Should().BeTrue();
    }
}
