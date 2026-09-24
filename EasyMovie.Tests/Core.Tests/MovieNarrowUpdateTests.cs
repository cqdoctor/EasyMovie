using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 锁死「单列定点更新」的安全属性：改一个标量的同时，**绝不能把其它列写没**。
///
/// 为什么必须有这组测试：旧写法是「GetByIdAsync 读整实体 → UpdateAsync（Movies.Update()
/// 标脏全列）」，有两个毛病——① 读的那一下把 86KB 海报拉进内存；② 一旦 GetByIdAsync
/// 将来改成窄投影，Update 标脏全列会把没投影到的列写成 null（PosterData 直接被抹掉）。
/// 改成「桩实体 Attach + 逐列 IsModified」后只有目标列参与 UPDATE，这里把它钉死。
///
/// 注意：断言一律用**全新的 DbContext** 读回，确保验证的是真实持久化结果，
/// 而不是被同一 context 的变更跟踪或缓存实例掩盖出来的假象。
/// </summary>
public class MovieNarrowUpdateTests
{
    private static readonly byte[] Poster =
        System.Text.Encoding.UTF8.GetBytes("FAKE-JPEG-BYTES-0123456789");

    private static (DbContextOptions<MovieDbContext> Options, IMovieService Service, MovieDbContext Context)
        Create(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var context = new MovieDbContext(options);
        var service = new MovieService(new MovieRepository(context), new TagRepository(context));
        return (options, service, context);
    }

    /// <summary>存一部"满字段"的电影，返回其 Id。</summary>
    private static async Task<int> SeedAsync(MovieDbContext context)
    {
        var movie = new Movie
        {
            Title = "定点更新测试片",
            Year = 2024,
            Director = "某导演",
            Synopsis = "一段不该被动到的简介",
            Notes = "一段不该被动到的笔记",
            PosterData = Poster,
            PosterUrl = "http://example.com/p.jpg",
            Rating = 7,
            IsFavorite = false,
            WatchStatus = WatchStatus.NotWatched
        };
        context.Movies.Add(movie);
        await context.SaveChangesAsync();
        return movie.Id;
    }

    /// <summary>用全新 context 读回，避开原 context 的变更跟踪缓存。</summary>
    private static Movie Reload(DbContextOptions<MovieDbContext> options, int id)
    {
        using var fresh = new MovieDbContext(options);
        return fresh.Movies.AsNoTracking().Single(m => m.Id == id);
    }

    [Fact]
    public async Task SetRatingAsync_ShouldUpdateOnlyRating_AndKeepEverythingElse()
    {
        var (options, service, context) = Create(nameof(SetRatingAsync_ShouldUpdateOnlyRating_AndKeepEverythingElse));
        var id = await SeedAsync(context);

        (await service.SetRatingAsync(id, 9)).Should().BeTrue();

        var after = Reload(options, id);
        after.Rating.Should().Be(9);
        // 核心：海报、简介、笔记等未被标脏的列必须原样保留
        after.PosterData.Should().NotBeNull().And.Equal(Poster);
        after.Synopsis.Should().Be("一段不该被动到的简介");
        after.Notes.Should().Be("一段不该被动到的笔记");
        after.Director.Should().Be("某导演");
        after.PosterUrl.Should().Be("http://example.com/p.jpg");
        after.Year.Should().Be(2024);
    }

    [Fact]
    public async Task ToggleFavoriteAsync_ShouldFlip_AndKeepPoster()
    {
        var (options, service, context) = Create(nameof(ToggleFavoriteAsync_ShouldFlip_AndKeepPoster));
        var id = await SeedAsync(context);

        (await service.ToggleFavoriteAsync(id)).Should().BeTrue();
        Reload(options, id).IsFavorite.Should().BeTrue();

        (await service.ToggleFavoriteAsync(id)).Should().BeTrue();
        var after = Reload(options, id);
        after.IsFavorite.Should().BeFalse();   // 翻回来，证明读的是列现值而非恒写 true
        after.PosterData.Should().NotBeNull().And.Equal(Poster);
        after.Notes.Should().Be("一段不该被动到的笔记");
    }

    [Fact]
    public async Task SetWatchStatusAsync_ShouldSetStatusAndDate_AndKeepPoster()
    {
        var (options, service, context) = Create(nameof(SetWatchStatusAsync_ShouldSetStatusAndDate_AndKeepPoster));
        var id = await SeedAsync(context);
        var date = new DateTime(2026, 9, 23);

        (await service.SetWatchStatusAsync(id, WatchStatus.Watched, date)).Should().BeTrue();

        var after = Reload(options, id);
        after.WatchStatus.Should().Be(WatchStatus.Watched);
        after.WatchDate.Should().Be(date);
        after.PosterData.Should().NotBeNull().And.Equal(Poster);
    }

    [Fact]
    public async Task UpdateNotesAsync_And_SetCategoryAsync_ShouldKeepPoster()
    {
        var (options, service, context) = Create(nameof(UpdateNotesAsync_And_SetCategoryAsync_ShouldKeepPoster));
        var id = await SeedAsync(context);

        (await service.UpdateNotesAsync(id, "换了新笔记")).Should().BeTrue();
        (await service.SetCategoryAsync(id, null)).Should().BeTrue();

        var after = Reload(options, id);
        after.Notes.Should().Be("换了新笔记");
        after.CategoryId.Should().BeNull();
        after.PosterData.Should().NotBeNull().And.Equal(Poster);
        after.Synopsis.Should().Be("一段不该被动到的简介");
    }

    [Theory]
    [InlineData(9999)]
    [InlineData(-1)]
    public async Task SingleColumnUpdates_OnMissingMovie_ShouldReturnFalse_AndNotThrow(int missingId)
    {
        var (_, service, context) = Create(
            nameof(SingleColumnUpdates_OnMissingMovie_ShouldReturnFalse_AndNotThrow) + missingId);
        _ = await SeedAsync(context); // 库里有别的电影，但要改的 Id 不存在

        (await service.SetRatingAsync(missingId, 8)).Should().BeFalse();
        (await service.ToggleFavoriteAsync(missingId)).Should().BeFalse();
        (await service.UpdateNotesAsync(missingId, "x")).Should().BeFalse();
        (await service.SetWatchStatusAsync(missingId, WatchStatus.Watched, null)).Should().BeFalse();
        (await service.SetCategoryAsync(missingId, null)).Should().BeFalse();
    }

    [Fact]
    public async Task GetFilePathAsync_ShouldReturnOnlyPath_AndNullForMissingMovie()
    {
        var (options, service, context) = Create(nameof(GetFilePathAsync_ShouldReturnOnlyPath_AndNullForMissingMovie));
        var id = await SeedAsync(context);

        (await service.GetFilePathAsync(id)).Should().BeNull(); // Seed 未设 FilePath

        // 用**独立 context** 补上 FilePath（Seed 用的 context 还在跟踪该实体，
        // 直接 Attach 同 Id 的桩实体会抛 identity conflict）。
        using (var writer = new MovieDbContext(options))
        {
            var stub = new Movie { Id = id, FilePath = @"D:\movies\test.mkv" };
            writer.Attach(stub);
            writer.Entry(stub).Property(m => m.FilePath).IsModified = true;
            await writer.SaveChangesAsync();
        }

        (await service.GetFilePathAsync(id)).Should().Be(@"D:\movies\test.mkv");
        (await service.GetFilePathAsync(9999)).Should().BeNull(); // 不存在的 Id

        // 前提校验：海报仍在，说明窄查询没有副作用
        Reload(options, id).PosterData.Should().NotBeNull().And.Equal(Poster);
    }
}
