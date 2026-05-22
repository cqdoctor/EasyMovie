using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

public class MovieRepositoryTests
{
    private static MovieDbContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new MovieDbContext(options);
    }

    private static Movie CreateTestMovie(string title = "测试电影", int year = 2024, string director = "测试导演")
    {
        return new Movie
        {
            Title = title,
            OriginalTitle = $"Original {title}",
            Year = year,
            Director = director,
            Cast = "演员A, 演员B",
            Country = "中国",
            Language = "中文",
            Runtime = 120,
            Synopsis = "这是一个测试电影的简介。",
            Rating = 8,
            WatchStatus = WatchStatus.Watched,
            WatchDate = new DateTime(2024, 6, 15),
            Notes = "还不错",
            IsFavorite = true
        };
    }

    #region AddAsync Tests

    [Fact]
    public async Task AddAsync_ShouldAddMovie_AndSetTimestamps()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(AddAsync_ShouldAddMovie_AndSetTimestamps));
        var repo = new MovieRepository(context);
        var movie = CreateTestMovie();

        // Act
        var result = await repo.AddAsync(movie);

        // Assert
        result.Id.Should().BeGreaterThan(0);
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        result.Title.Should().Be("测试电影");
    }

    [Fact]
    public async Task AddAsync_ShouldPersistToDb()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(AddAsync_ShouldPersistToDb));
        var repo = new MovieRepository(context);
        var movie = CreateTestMovie();

        // Act
        await repo.AddAsync(movie);

        // Assert
        var saved = await context.Movies.FindAsync(movie.Id);
        saved.Should().NotBeNull();
        saved!.Title.Should().Be("测试电影");
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ShouldReturnMovie_WhenExists()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(GetByIdAsync_ShouldReturnMovie_WhenExists));
        var repo = new MovieRepository(context);
        var movie = await repo.AddAsync(CreateTestMovie());

        // Act
        var result = await repo.GetByIdAsync(movie.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(movie.Id);
        result.Title.Should().Be("测试电影");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotExists()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(GetByIdAsync_ShouldReturnNull_WhenNotExists));
        var repo = new MovieRepository(context);

        // Act
        var result = await repo.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldIncludeCategory_WhenExists()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(GetByIdAsync_ShouldIncludeCategory_WhenExists));
        var repo = new MovieRepository(context);
        var category = new Category { Name = "动作片" };
        context.Categories.Add(category);
        await context.SaveChangesAsync();

        var movie = CreateTestMovie();
        movie.CategoryId = category.Id;
        await repo.AddAsync(movie);

        // Act
        var result = await repo.GetByIdAsync(movie.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Category.Should().NotBeNull();
        result.Category!.Name.Should().Be("动作片");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldIncludeTags_WhenExists()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(GetByIdAsync_ShouldIncludeTags_WhenExists));
        var repo = new MovieRepository(context);
        var tag = new Tag { Name = "经典" };
        context.Tags.Add(tag);
        await context.SaveChangesAsync();

        var movie = CreateTestMovie();
        await repo.AddAsync(movie);
        context.MovieTags.Add(new MovieTag { MovieId = movie.Id, TagId = tag.Id });
        await context.SaveChangesAsync();

        // Act
        var result = await repo.GetByIdAsync(movie.Id);

        // Assert
        result.Should().NotBeNull();
        result!.MovieTags.Should().HaveCount(1);
        result.MovieTags.First().Tag.Name.Should().Be("经典");
    }

    #endregion

    #region GetAllAsync Tests

    [Fact]
    public async Task GetAllAsync_ShouldReturnAllMovies_OrderedByCreatedAtDesc()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(GetAllAsync_ShouldReturnAllMovies_OrderedByCreatedAtDesc));
        var repo = new MovieRepository(context);
        await repo.AddAsync(CreateTestMovie("A电影"));
        await Task.Delay(10);
        await repo.AddAsync(CreateTestMovie("B电影"));
        await Task.Delay(10);
        await repo.AddAsync(CreateTestMovie("C电影"));

        // Act
        var results = await repo.GetAllAsync();

        // Assert
        results.Should().HaveCount(3);
        results[0].Title.Should().Be("C电影"); // 最新的在前
        results[2].Title.Should().Be("A电影"); // 最旧的在后
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnEmpty_WhenNoMovies()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(GetAllAsync_ShouldReturnEmpty_WhenNoMovies));
        var repo = new MovieRepository(context);

        // Act
        var results = await repo.GetAllAsync();

        // Assert
        results.Should().BeEmpty();
    }

    #endregion

    #region SearchAsync Tests

    [Fact]
    public async Task SearchAsync_ShouldFilterByKeyword_InTitle()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(SearchAsync_ShouldFilterByKeyword_InTitle));
        var repo = new MovieRepository(context);
        await repo.AddAsync(CreateTestMovie("肖申克的救赎", 1994));
        await repo.AddAsync(CreateTestMovie("阿甘正传", 1994));
        await repo.AddAsync(CreateTestMovie("教父", 1972));

        // Act
        var results = await repo.SearchAsync("肖申克", null, null, null, null, null, null, null, false, 0, 10);

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("肖申克的救赎");
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByKeyword_InDirector()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(SearchAsync_ShouldFilterByKeyword_InDirector));
        var repo = new MovieRepository(context);
        await repo.AddAsync(CreateTestMovie("电影A", 2020, "张艺谋"));
        await repo.AddAsync(CreateTestMovie("电影B", 2021, "陈凯歌"));

        // Act
        var results = await repo.SearchAsync("张艺谋", null, null, null, null, null, null, null, false, 0, 10);

        // Assert
        results.Should().HaveCount(1);
        results[0].Director.Should().Be("张艺谋");
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByCategory()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(SearchAsync_ShouldFilterByCategory));
        var repo = new MovieRepository(context);
        var cat1 = new Category { Name = "动作" };
        var cat2 = new Category { Name = "剧情" };
        context.Categories.AddRange(cat1, cat2);
        await context.SaveChangesAsync();

        var m1 = CreateTestMovie("动作片1");
        m1.CategoryId = cat1.Id;
        var m2 = CreateTestMovie("剧情片1");
        m2.CategoryId = cat2.Id;
        await repo.AddAsync(m1);
        await repo.AddAsync(m2);

        // Act
        var results = await repo.SearchAsync(null, cat1.Id, null, null, null, null, null, null, false, 0, 10);

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("动作片1");
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByYearRange()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(SearchAsync_ShouldFilterByYearRange));
        var repo = new MovieRepository(context);
        await repo.AddAsync(CreateTestMovie("老片", 1990));
        await repo.AddAsync(CreateTestMovie("中片", 2005));
        await repo.AddAsync(CreateTestMovie("新片", 2024));

        // Act
        var results = await repo.SearchAsync(null, null, null, 2000, 2020, null, null, null, false, 0, 10);

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("中片");
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByRating()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(SearchAsync_ShouldFilterByRating));
        var repo = new MovieRepository(context);
        var m1 = CreateTestMovie("高分片");
        m1.Rating = 9;
        var m2 = CreateTestMovie("低分片");
        m2.Rating = 5;
        await repo.AddAsync(m1);
        await repo.AddAsync(m2);

        // Act
        var results = await repo.SearchAsync(null, null, null, null, null, 7, null, null, false, 0, 10);

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("高分片");
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByWatchStatus()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(SearchAsync_ShouldFilterByWatchStatus));
        var repo = new MovieRepository(context);
        var m1 = CreateTestMovie("已看片");
        m1.WatchStatus = WatchStatus.Watched;
        var m2 = CreateTestMovie("想看片");
        m2.WatchStatus = WatchStatus.WantToWatch;
        await repo.AddAsync(m1);
        await repo.AddAsync(m2);

        // Act
        var results = await repo.SearchAsync(null, null, null, null, null, null, WatchStatus.WantToWatch, null, false, 0, 10);

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("想看片");
    }

    [Fact]
    public async Task SearchAsync_ShouldSortByYearAsc()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(SearchAsync_ShouldSortByYearAsc));
        var repo = new MovieRepository(context);
        await repo.AddAsync(CreateTestMovie("C", 2020));
        await repo.AddAsync(CreateTestMovie("A", 2010));
        await repo.AddAsync(CreateTestMovie("B", 2015));

        // Act
        var results = await repo.SearchAsync(null, null, null, null, null, null, null, "year", false, 0, 10);

        // Assert
        results[0].Year.Should().Be(2010);
        results[1].Year.Should().Be(2015);
        results[2].Year.Should().Be(2020);
    }

    [Fact]
    public async Task SearchAsync_ShouldSortByRatingDesc()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(SearchAsync_ShouldSortByRatingDesc));
        var repo = new MovieRepository(context);
        var m1 = CreateTestMovie("中分");
        m1.Rating = 6;
        var m2 = CreateTestMovie("高分");
        m2.Rating = 9;
        var m3 = CreateTestMovie("低分");
        m3.Rating = 3;
        await repo.AddAsync(m1);
        await repo.AddAsync(m2);
        await repo.AddAsync(m3);

        // Act
        var results = await repo.SearchAsync(null, null, null, null, null, null, null, "rating", true, 0, 10);

        // Assert
        results[0].Rating.Should().Be(9);
        results[1].Rating.Should().Be(6);
        results[2].Rating.Should().Be(3);
    }

    [Fact]
    public async Task SearchAsync_ShouldPaginateCorrectly()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(SearchAsync_ShouldPaginateCorrectly));
        var repo = new MovieRepository(context);
        for (var i = 1; i <= 10; i++)
            await repo.AddAsync(CreateTestMovie($"电影{i}"));

        // Act - 第一页
        var page1 = await repo.SearchAsync(null, null, null, null, null, null, null, null, false, 0, 3);

        // Act - 第二页
        var page2 = await repo.SearchAsync(null, null, null, null, null, null, null, null, false, 3, 3);

        // Assert
        page1.Should().HaveCount(3);
        page2.Should().HaveCount(3);
        page1[0].Title.Should().NotBe(page2[0].Title);
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_ShouldUpdateMovie()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(UpdateAsync_ShouldUpdateMovie));
        var repo = new MovieRepository(context);
        var movie = await repo.AddAsync(CreateTestMovie("原始标题"));

        // Act
        movie.Title = "修改后标题";
        movie.Rating = 5;
        await repo.UpdateAsync(movie);

        // Assert
        var updated = await repo.GetByIdAsync(movie.Id);
        updated!.Title.Should().Be("修改后标题");
        updated.Rating.Should().Be(5);
        updated.UpdatedAt.Should().BeAfter(updated.CreatedAt.AddSeconds(-1));
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_ShouldDeleteMovie_WhenExists()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(DeleteAsync_ShouldDeleteMovie_WhenExists));
        var repo = new MovieRepository(context);
        var movie = await repo.AddAsync(CreateTestMovie());

        // Act
        var result = await repo.DeleteAsync(movie.Id);

        // Assert
        result.Should().BeTrue();
        (await repo.ExistsAsync(movie.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotExists()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(DeleteAsync_ShouldReturnFalse_WhenNotExists));
        var repo = new MovieRepository(context);

        // Act
        var result = await repo.DeleteAsync(999);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region CountAsync Tests

    [Fact]
    public async Task CountAsync_ShouldReturnCorrectCount()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(CountAsync_ShouldReturnCorrectCount));
        var repo = new MovieRepository(context);
        await repo.AddAsync(CreateTestMovie("A", 2020));
        await repo.AddAsync(CreateTestMovie("B", 2021));
        await repo.AddAsync(CreateTestMovie("C", 2022));

        // Act
        var total = await repo.CountAsync(null, null, null, null, null, null, null);
        var filtered = await repo.CountAsync(null, null, null, 2020, 2020, null, null);

        // Assert
        total.Should().Be(3);
        filtered.Should().Be(1);
    }

    #endregion

    #region ExistsAsync Tests

    [Fact]
    public async Task ExistsAsync_ShouldReturnTrue_WhenExists()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(ExistsAsync_ShouldReturnTrue_WhenExists));
        var repo = new MovieRepository(context);
        var movie = await repo.AddAsync(CreateTestMovie());

        // Act
        var exists = await repo.ExistsAsync(movie.Id);

        // Assert
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_ShouldReturnFalse_WhenNotExists()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(ExistsAsync_ShouldReturnFalse_WhenNotExists));
        var repo = new MovieRepository(context);

        // Act
        var exists = await repo.ExistsAsync(999);

        // Assert
        exists.Should().BeFalse();
    }

    #endregion

    #region Tag Filtering Tests

    [Fact]
    public async Task SearchAsync_ShouldFilterByTags()
    {
        // Arrange
        using var context = CreateInMemoryContext(nameof(SearchAsync_ShouldFilterByTags));
        var repo = new MovieRepository(context);
        var tag1 = new Tag { Name = "科幻" };
        var tag2 = new Tag { Name = "动作" };
        context.Tags.AddRange(tag1, tag2);
        await context.SaveChangesAsync();

        var m1 = CreateTestMovie("星际穿越");
        var m2 = CreateTestMovie("谍影重重");
        await repo.AddAsync(m1);
        await repo.AddAsync(m2);
        context.MovieTags.Add(new MovieTag { MovieId = m1.Id, TagId = tag1.Id });
        context.MovieTags.Add(new MovieTag { MovieId = m2.Id, TagId = tag2.Id });
        await context.SaveChangesAsync();

        // Act
        var results = await repo.SearchAsync(null, null, new List<int> { tag1.Id }, null, null, null, null, null, false, 0, 10);

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("星际穿越");
    }

    #endregion
}
