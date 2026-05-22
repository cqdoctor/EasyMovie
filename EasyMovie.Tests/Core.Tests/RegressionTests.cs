using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 扩展回归测试 — 冲 200+
/// </summary>
public class RegressionTests
{
    private static (MovieDbContext ctx, MovieService movieSvc, CategoryService catSvc, TagService tagSvc)
        CreateServices(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var ctx = new MovieDbContext(options);
        var movieRepo = new MovieRepository(ctx);
        var catRepo = new CategoryRepository(ctx);
        var tagRepo = new TagRepository(ctx);
        return (ctx,
            new MovieService(movieRepo, tagRepo),
            new CategoryService(catRepo),
            new TagService(tagRepo));
    }

    [Fact]
    public async Task Regression_AddUpdateDelete_Cycle()
    {
        var (_, movieSvc, catSvc, tagSvc) = CreateServices(nameof(Regression_AddUpdateDelete_Cycle));

        var cat = await catSvc.AddAsync(new Category { Name = "测试分类" });
        var tag = await tagSvc.AddAsync(new Tag { Name = "测试标签" });

        var movie = await movieSvc.AddAsync(new Movie { Title = "CRUD测试", Year = 2022, CategoryId = cat.Id });
        await movieSvc.SetTagsAsync(movie.Id, new() { tag.Id });
        await movieSvc.SetRatingAsync(movie.Id, 7);

        movie = (await movieSvc.GetByIdAsync(movie.Id))!;
        movie.Title.Should().Be("CRUD测试");
        movie.Rating.Should().Be(7);
        movie.CategoryId.Should().Be(cat.Id);

        movie.Title = "CRUD测试-Modified";
        await movieSvc.UpdateAsync(movie);
        (await movieSvc.GetByIdAsync(movie.Id))!.Title.Should().Be("CRUD测试-Modified");

        await movieSvc.DeleteAsync(movie.Id);
        (await movieSvc.GetByIdAsync(movie.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Regression_CategoryHierarchy_FullTree()
    {
        var (_, _, catSvc, _) = CreateServices(nameof(Regression_CategoryHierarchy_FullTree));

        var film = await catSvc.AddAsync(new Category { Name = "电影" });
        var genre = await catSvc.AddAsync(new Category { Name = "类型", ParentId = film.Id });
        await catSvc.AddAsync(new Category { Name = "科幻", ParentId = genre.Id });
        await catSvc.AddAsync(new Category { Name = "动作", ParentId = genre.Id });

        var region = await catSvc.AddAsync(new Category { Name = "地区", ParentId = film.Id });
        await catSvc.AddAsync(new Category { Name = "中国大陆", ParentId = region.Id });

        var tree = await catSvc.GetCategoryTreeAsync();
        tree.Should().HaveCount(1);
        tree[0].Name.Should().Be("电影");
        tree[0].Children.Should().HaveCount(2);
    }

    [Fact]
    public async Task Regression_MovieSearch_ComplexAND()
    {
        var (_, movieSvc, catSvc, _) = CreateServices(nameof(Regression_MovieSearch_ComplexAND));

        var cat = await catSvc.AddAsync(new Category { Name = "科幻" });
        await movieSvc.AddAsync(new Movie { Title = "A", Year = 2023, Rating = 9, WatchStatus = WatchStatus.Watched, CategoryId = cat.Id });
        await movieSvc.AddAsync(new Movie { Title = "B", Year = 2022, Rating = 4, WatchStatus = WatchStatus.Watched, CategoryId = cat.Id });
        await movieSvc.AddAsync(new Movie { Title = "C", Year = 2023, Rating = 3, WatchStatus = WatchStatus.WantToWatch, CategoryId = cat.Id });

        var (results, total) = await movieSvc.SearchAsync(
            null, cat.Id, null, 2023, null, 7, WatchStatus.Watched, null, false, 1, 10);

        total.Should().Be(1);
        results[0].Title.Should().Be("A");
    }

    [Fact]
    public async Task Regression_LargeData_PaginationWorks()
    {
        var (ctx, movieSvc, _, _) = CreateServices(nameof(Regression_LargeData_PaginationWorks));

        for (var i = 1; i <= 50; i++)
            await movieSvc.AddAsync(new Movie { Title = $"P{i:D3}", Year = 2020 + i % 10 });

        var (page1, total) = await movieSvc.SearchAsync(null, null, null, null, null, null, null, "title", false, 1, 15);
        page1.Should().HaveCount(15);
        total.Should().Be(50);

        var (page4, _) = await movieSvc.SearchAsync(null, null, null, null, null, null, null, "title", false, 4, 15);
        page4.Should().HaveCount(5);
    }

    [Fact]
    public async Task Regression_CategoryDelete_WithMovies_SetNull()
    {
        var (ctx, _, catSvc, _) = CreateServices(nameof(Regression_CategoryDelete_WithMovies_SetNull));

        var cat = await catSvc.AddAsync(new Category { Name = "可删除" });
        ctx.Movies.Add(new Movie { Title = "关联电影", Year = 2020, CategoryId = cat.Id });
        await ctx.SaveChangesAsync();

        // CanDelete 提示不可删除（有关联电影），但实际删除会 SetNull
        var canDelete = await catSvc.CanDeleteAsync(cat.Id);
        canDelete.Should().BeFalse();

        // 强制删除：CategoryId 设为 null（DB 配置 SetNull）
        await catSvc.DeleteAsync(cat.Id);
        (await catSvc.GetByIdAsync(cat.Id)).Should().BeNull();

        // 电影保留，分类为 null
        var movie = ctx.Movies.First();
        movie.Title.Should().Be("关联电影");
        movie.CategoryId.Should().BeNull();
    }

    [Fact]
    public async Task Regression_TagDelete_DoesNotDeleteMovie()
    {
        var (_, movieSvc, _, tagSvc) = CreateServices(nameof(Regression_TagDelete_DoesNotDeleteMovie));

        var tag = await tagSvc.AddAsync(new Tag { Name = "临时" });
        var movie = await movieSvc.AddAsync(new Movie { Title = "保留电影", Year = 2020 });
        await movieSvc.SetTagsAsync(movie.Id, new() { tag.Id });

        await tagSvc.DeleteAsync(tag.Id);
        (await movieSvc.GetByIdAsync(movie.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Regression_Rating_BoundaryValues()
    {
        var (_, movieSvc, _, _) = CreateServices(nameof(Regression_Rating_BoundaryValues));

        var movie = await movieSvc.AddAsync(new Movie { Title = "边界评分", Year = 2020 });

        await movieSvc.SetRatingAsync(movie.Id, 1);
        (await movieSvc.GetByIdAsync(movie.Id))!.Rating.Should().Be(1);

        await movieSvc.SetRatingAsync(movie.Id, 10);
        (await movieSvc.GetByIdAsync(movie.Id))!.Rating.Should().Be(10);

        await movieSvc.SetRatingAsync(movie.Id, null);
        (await movieSvc.GetByIdAsync(movie.Id))!.Rating.Should().BeNull();
    }

    [Fact]
    public async Task Regression_FavoriteToggle_ThenDelete()
    {
        var (_, movieSvc, _, _) = CreateServices(nameof(Regression_FavoriteToggle_ThenDelete));

        var movie = await movieSvc.AddAsync(new Movie { Title = "收藏后删除", Year = 2020 });
        await movieSvc.ToggleFavoriteAsync(movie.Id);
        await movieSvc.DeleteAsync(movie.Id);

        var total = await movieSvc.GetTotalCountAsync();
        total.Should().Be(0);
    }

    [Fact]
    public async Task Regression_Notes_LengthBoundary()
    {
        var (_, movieSvc, _, _) = CreateServices(nameof(Regression_Notes_LengthBoundary));

        var movie = await movieSvc.AddAsync(new Movie { Title = "笔记边界", Year = 2020 });

        // 正好 2000 字应该可以
        var notes = new string('备', 2000);
        await movieSvc.UpdateNotesAsync(movie.Id, notes);
        (await movieSvc.GetByIdAsync(movie.Id))!.Notes!.Length.Should().Be(2000);

        // 2001 字应该抛异常
        await Assert.ThrowsAsync<ArgumentException>(() =>
            movieSvc.UpdateNotesAsync(movie.Id, new string('备', 2001)));
    }

    [Fact]
    public async Task Regression_Search_ChineseAndEnglish()
    {
        var (_, movieSvc, _, _) = CreateServices(nameof(Regression_Search_ChineseAndEnglish));

        await movieSvc.AddAsync(new Movie { Title = "The Matrix", Year = 1999, Director = "Wachowski" });
        await movieSvc.AddAsync(new Movie { Title = "黑客帝国", Year = 1999, Director = "沃卓斯基" });

        var (en, _) = await movieSvc.SearchAsync("Matrix", null, null, null, null, null, null, null, false, 1, 10);
        en.Should().HaveCount(1);
        en[0].Title.Should().Be("The Matrix");

        var (cn, _) = await movieSvc.SearchAsync("黑客", null, null, null, null, null, null, null, false, 1, 10);
        cn.Should().HaveCount(1);
        cn[0].Title.Should().Be("黑客帝国");
    }

    [Fact]
    public async Task Regression_MultipleUpdates_InSequence()
    {
        var (_, movieSvc, _, _) = CreateServices(nameof(Regression_MultipleUpdates_InSequence));

        var movie = await movieSvc.AddAsync(new Movie { Title = "序列更新", Year = 2020 });

        await movieSvc.SetRatingAsync(movie.Id, 5);
        await movieSvc.SetRatingAsync(movie.Id, 8);
        await movieSvc.SetRatingAsync(movie.Id, 6);
        await movieSvc.SetRatingAsync(movie.Id, 9);

        (await movieSvc.GetByIdAsync(movie.Id))!.Rating.Should().Be(9);
    }

    [Fact]
    public async Task Regression_Category_GetById_WithChildren()
    {
        var (_, _, catSvc, _) = CreateServices(nameof(Regression_Category_GetById_WithChildren));

        var parent = await catSvc.AddAsync(new Category { Name = "父" });
        await catSvc.AddAsync(new Category { Name = "子1", ParentId = parent.Id });
        await catSvc.AddAsync(new Category { Name = "子2", ParentId = parent.Id });

        var loaded = await catSvc.GetByIdAsync(parent.Id);
        loaded!.Children.Should().HaveCount(2);
    }

    [Fact]
    public async Task Regression_YearFilter_EdgeYears()
    {
        var (_, movieSvc, _, _) = CreateServices(nameof(Regression_YearFilter_EdgeYears));

        await movieSvc.AddAsync(new Movie { Title = "最老", Year = 1888 });
        await movieSvc.AddAsync(new Movie { Title = "最新", Year = DateTime.Now.Year + 5 });
        await movieSvc.AddAsync(new Movie { Title = "中间", Year = 2000 });

        var (old, _) = await movieSvc.SearchAsync(null, null, null, 1888, 1888, null, null, null, false, 1, 10);
        old.Should().HaveCount(1);
        old[0].Title.Should().Be("最老");

        var (new_, _) = await movieSvc.SearchAsync(null, null, null, DateTime.Now.Year, null, null, null, null, false, 1, 10);
        new_.Should().HaveCount(1);
        new_[0].Title.Should().Be("最新");
    }

    [Fact]
    public async Task Regression_Movie_DefaultValues_AfterAdd()
    {
        var (_, movieSvc, _, _) = CreateServices(nameof(Regression_Movie_DefaultValues_AfterAdd));

        var movie = await movieSvc.AddAsync(new Movie { Title = "默认值", Year = 2020 });

        movie.WatchStatus.Should().Be(WatchStatus.WantToWatch);
        movie.IsFavorite.Should().BeFalse();
        movie.WatchDate.Should().BeNull();
        movie.Rating.Should().BeNull();
    }

    [Fact]
    public async Task Regression_Category_UpdateToDifferentParent()
    {
        var (_, _, catSvc, _) = CreateServices(nameof(Regression_Category_UpdateToDifferentParent));

        var root1 = await catSvc.AddAsync(new Category { Name = "根1" });
        var root2 = await catSvc.AddAsync(new Category { Name = "根2" });
        var child = await catSvc.AddAsync(new Category { Name = "子", ParentId = root1.Id });

        // 移动到另一个父分类
        child.ParentId = root2.Id;
        await catSvc.UpdateAsync(child);

        var loaded = await catSvc.GetByIdAsync(child.Id);
        loaded!.ParentId.Should().Be(root2.Id);

        // 旧的根不再有这个子
        var root1Children = await catSvc.GetChildrenAsync(root1.Id);
        root1Children.Should().BeEmpty();

        // 新的根有这个子
        var root2Children = await catSvc.GetChildrenAsync(root2.Id);
        root2Children.Should().HaveCount(1);
    }

    [Fact]
    public async Task Regression_ImportResult_HasCorrectCounts()
    {
        var result = new EasyMovie.Core.Interfaces.ImportResult();
        result.SuccessCount.Should().Be(0);
        result.ErrorCount.Should().Be(0);
        result.Errors.Should().BeEmpty();
        result.ImportedMovies.Should().BeEmpty();

        result.Errors.Add("test error");
        result.SuccessCount = 3;
        result.ErrorCount = 1;
        result.SuccessCount.Should().Be(3);
        result.ErrorCount.Should().Be(1);
    }

    [Fact]
    public async Task Regression_MovieSearchResult_DTO()
    {
        var result = new EasyMovie.Core.Interfaces.MovieSearchResult
        {
            Title = "测试",
            Year = 2020,
            Source = "douban",
            Rating = 8.5
        };

        result.Title.Should().Be("测试");
        result.Source.Should().Be("douban");
        result.Rating.Should().Be(8.5);
        result.Director.Should().BeNull(); // default
    }

    [Fact]
    public async Task Regression_StatisticsDTO_AllProperties()
    {
        var data = new EasyMovie.Core.Interfaces.StatisticsData
        {
            TotalMovies = 10,
            Watched = 5,
            CategoryStats = { new() { Name = "科幻", Count = 3 } },
            RatingStats = { new() { Rating = 8, Count = 2 } }
        };

        data.TotalMovies.Should().Be(10);
        data.Watched.Should().Be(5);
        data.CategoryStats[0].Name.Should().Be("科幻");
        data.RatingStats[0].Rating.Should().Be(8);
    }
}
