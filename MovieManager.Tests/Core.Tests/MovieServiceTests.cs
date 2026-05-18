using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MovieManager.Core.Enums;
using MovieManager.Core.Interfaces;
using MovieManager.Core.Models;
using MovieManager.Core.Services;
using MovieManager.Data;
using MovieManager.Data.Repositories;
using Xunit;

namespace MovieManager.Tests.Core.Tests;

public class MovieServiceTests
{
    private static (MovieDbContext, IMovieService) CreateService(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var context = new MovieDbContext(options);
        var movieRepo = new MovieRepository(context);
        var tagRepo = new TagRepository(context);
        var service = new MovieService(movieRepo, tagRepo);
        return (context, service);
    }

    #region AddAsync Tests

    [Fact]
    public async Task AddAsync_ShouldAddMovie()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldAddMovie));
        var movie = new Movie { Title = "测试电影", Year = 2024 };

        var result = await service.AddAsync(movie);

        result.Id.Should().BeGreaterThan(0);
        result.Title.Should().Be("测试电影");
    }

    [Fact]
    public async Task AddAsync_ShouldThrow_WhenTitleIsEmpty()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldThrow_WhenTitleIsEmpty));
        var movie = new Movie { Title = "", Year = 2024 };

        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(movie));
    }

    [Fact]
    public async Task AddAsync_ShouldThrow_WhenTitleIsWhitespace()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldThrow_WhenTitleIsWhitespace));
        var movie = new Movie { Title = "   ", Year = 2024 };

        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(movie));
    }

    [Fact]
    public async Task AddAsync_ShouldThrow_WhenYearTooEarly()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldThrow_WhenYearTooEarly));
        var movie = new Movie { Title = "太早的电影", Year = 1887 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddAsync(movie));
    }

    [Fact]
    public async Task AddAsync_ShouldThrow_WhenYearTooFuture()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldThrow_WhenYearTooFuture));
        var movie = new Movie { Title = "未来电影", Year = DateTime.Now.Year + 10 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddAsync(movie));
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_ShouldUpdateMovie()
    {
        var (_, service) = CreateService(nameof(UpdateAsync_ShouldUpdateMovie));
        var movie = await service.AddAsync(new Movie { Title = "原始", Year = 2020 });

        movie.Title = "修改后";
        await service.UpdateAsync(movie);

        var updated = await service.GetByIdAsync(movie.Id);
        updated!.Title.Should().Be("修改后");
    }

    [Fact]
    public async Task UpdateAsync_ShouldThrow_WhenMovieNotExists()
    {
        var (_, service) = CreateService(nameof(UpdateAsync_ShouldThrow_WhenMovieNotExists));
        var movie = new Movie { Id = 999, Title = "不存在", Year = 2020 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(movie));
    }

    [Fact]
    public async Task UpdateAsync_ShouldThrow_WhenTitleIsEmpty()
    {
        var (_, service) = CreateService(nameof(UpdateAsync_ShouldThrow_WhenTitleIsEmpty));
        var movie = await service.AddAsync(new Movie { Title = "原始", Year = 2020 });

        movie.Title = "";
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(movie));
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_ShouldReturnTrue_WhenExists()
    {
        var (_, service) = CreateService(nameof(DeleteAsync_ShouldReturnTrue_WhenExists));
        var movie = await service.AddAsync(new Movie { Title = "待删除", Year = 2020 });

        var result = await service.DeleteAsync(movie.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotExists()
    {
        var (_, service) = CreateService(nameof(DeleteAsync_ShouldReturnFalse_WhenNotExists));

        var result = await service.DeleteAsync(999);

        result.Should().BeFalse();
    }

    #endregion

    #region SetRatingAsync Tests

    [Fact]
    public async Task SetRatingAsync_ShouldSetRating()
    {
        var (_, service) = CreateService(nameof(SetRatingAsync_ShouldSetRating));
        var movie = await service.AddAsync(new Movie { Title = "评分测试", Year = 2020 });

        var result = await service.SetRatingAsync(movie.Id, 8);

        result.Should().BeTrue();
        var updated = await service.GetByIdAsync(movie.Id);
        updated!.Rating.Should().Be(8);
    }

    [Fact]
    public async Task SetRatingAsync_ShouldClearRating_WhenNull()
    {
        var (_, service) = CreateService(nameof(SetRatingAsync_ShouldClearRating_WhenNull));
        var movie = await service.AddAsync(new Movie { Title = "清空评分", Year = 2020, Rating = 8 });

        var result = await service.SetRatingAsync(movie.Id, null);

        result.Should().BeTrue();
        var updated = await service.GetByIdAsync(movie.Id);
        updated!.Rating.Should().BeNull();
    }

    [Fact]
    public async Task SetRatingAsync_ShouldThrow_WhenRatingTooLow()
    {
        var (_, service) = CreateService(nameof(SetRatingAsync_ShouldThrow_WhenRatingTooLow));
        var movie = await service.AddAsync(new Movie { Title = "测试", Year = 2020 });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetRatingAsync(movie.Id, 0));
    }

    [Fact]
    public async Task SetRatingAsync_ShouldThrow_WhenRatingTooHigh()
    {
        var (_, service) = CreateService(nameof(SetRatingAsync_ShouldThrow_WhenRatingTooHigh));
        var movie = await service.AddAsync(new Movie { Title = "测试", Year = 2020 });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetRatingAsync(movie.Id, 11));
    }

    [Fact]
    public async Task SetRatingAsync_ShouldReturnFalse_WhenMovieNotExists()
    {
        var (_, service) = CreateService(nameof(SetRatingAsync_ShouldReturnFalse_WhenMovieNotExists));

        var result = await service.SetRatingAsync(999, 5);

        result.Should().BeFalse();
    }

    #endregion

    #region SetWatchStatusAsync Tests

    [Fact]
    public async Task SetWatchStatusAsync_ShouldSetWantToWatch()
    {
        var (_, service) = CreateService(nameof(SetWatchStatusAsync_ShouldSetWantToWatch));
        var movie = await service.AddAsync(new Movie { Title = "测试", Year = 2020 });

        var result = await service.SetWatchStatusAsync(movie.Id, WatchStatus.WantToWatch, null);

        result.Should().BeTrue();
        var updated = await service.GetByIdAsync(movie.Id);
        updated!.WatchStatus.Should().Be(WatchStatus.WantToWatch);
        updated.WatchDate.Should().BeNull();
    }

    [Fact]
    public async Task SetWatchStatusAsync_ShouldSetWatched_WithDate()
    {
        var (_, service) = CreateService(nameof(SetWatchStatusAsync_ShouldSetWatched_WithDate));
        var movie = await service.AddAsync(new Movie { Title = "测试", Year = 2020 });
        var watchDate = new DateTime(2024, 5, 1);

        var result = await service.SetWatchStatusAsync(movie.Id, WatchStatus.Watched, watchDate);

        result.Should().BeTrue();
        var updated = await service.GetByIdAsync(movie.Id);
        updated!.WatchStatus.Should().Be(WatchStatus.Watched);
        updated.WatchDate.Should().Be(watchDate);
    }

    [Fact]
    public async Task SetWatchStatusAsync_ShouldReturnFalse_WhenNotExists()
    {
        var (_, service) = CreateService(nameof(SetWatchStatusAsync_ShouldReturnFalse_WhenNotExists));

        var result = await service.SetWatchStatusAsync(999, WatchStatus.Watched, DateTime.Now);

        result.Should().BeFalse();
    }

    #endregion

    #region ToggleFavoriteAsync Tests

    [Fact]
    public async Task ToggleFavoriteAsync_ShouldToggleToTrue()
    {
        var (_, service) = CreateService(nameof(ToggleFavoriteAsync_ShouldToggleToTrue));
        var movie = await service.AddAsync(new Movie { Title = "收藏测试", Year = 2020, IsFavorite = false });

        var result = await service.ToggleFavoriteAsync(movie.Id);

        result.Should().BeTrue();
        var updated = await service.GetByIdAsync(movie.Id);
        updated!.IsFavorite.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleFavoriteAsync_ShouldToggleToFalse()
    {
        var (_, service) = CreateService(nameof(ToggleFavoriteAsync_ShouldToggleToFalse));
        var movie = await service.AddAsync(new Movie { Title = "取消收藏", Year = 2020, IsFavorite = true });

        var result = await service.ToggleFavoriteAsync(movie.Id);

        result.Should().BeTrue();
        var updated = await service.GetByIdAsync(movie.Id);
        updated!.IsFavorite.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleFavoriteAsync_ShouldReturnFalse_WhenNotExists()
    {
        var (_, service) = CreateService(nameof(ToggleFavoriteAsync_ShouldReturnFalse_WhenNotExists));

        var result = await service.ToggleFavoriteAsync(999);

        result.Should().BeFalse();
    }

    #endregion

    #region UpdateNotesAsync Tests

    [Fact]
    public async Task UpdateNotesAsync_ShouldUpdateNotes()
    {
        var (_, service) = CreateService(nameof(UpdateNotesAsync_ShouldUpdateNotes));
        var movie = await service.AddAsync(new Movie { Title = "笔记测试", Year = 2020 });

        var result = await service.UpdateNotesAsync(movie.Id, "这是一条笔记");

        result.Should().BeTrue();
        var updated = await service.GetByIdAsync(movie.Id);
        updated!.Notes.Should().Be("这是一条笔记");
    }

    [Fact]
    public async Task UpdateNotesAsync_ShouldClearNotes()
    {
        var (_, service) = CreateService(nameof(UpdateNotesAsync_ShouldClearNotes));
        var movie = await service.AddAsync(new Movie { Title = "清空笔记", Year = 2020, Notes = "旧笔记" });

        await service.UpdateNotesAsync(movie.Id, null);

        var updated = await service.GetByIdAsync(movie.Id);
        updated!.Notes.Should().BeNull();
    }

    [Fact]
    public async Task UpdateNotesAsync_ShouldThrow_WhenNotesTooLong()
    {
        var (_, service) = CreateService(nameof(UpdateNotesAsync_ShouldThrow_WhenNotesTooLong));
        var movie = await service.AddAsync(new Movie { Title = "测试", Year = 2020 });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateNotesAsync(movie.Id, new string('x', 2001)));
    }

    [Fact]
    public async Task UpdateNotesAsync_ShouldReturnFalse_WhenNotExists()
    {
        var (_, service) = CreateService(nameof(UpdateNotesAsync_ShouldReturnFalse_WhenNotExists));

        var result = await service.UpdateNotesAsync(999, "笔记");

        result.Should().BeFalse();
    }

    #endregion

    #region SetCategoryAsync Tests

    [Fact]
    public async Task SetCategoryAsync_ShouldSetCategory()
    {
        var (context, service) = CreateService(nameof(SetCategoryAsync_ShouldSetCategory));
        context.Categories.Add(new Category { Name = "科幻" });
        await context.SaveChangesAsync();
        var catId = context.Categories.First().Id;
        var movie = await service.AddAsync(new Movie { Title = "分类测试", Year = 2020 });

        var result = await service.SetCategoryAsync(movie.Id, catId);

        result.Should().BeTrue();
        var updated = await service.GetByIdAsync(movie.Id);
        updated!.CategoryId.Should().Be(catId);
    }

    [Fact]
    public async Task SetCategoryAsync_ShouldClearCategory()
    {
        var (context, service) = CreateService(nameof(SetCategoryAsync_ShouldClearCategory));
        context.Categories.Add(new Category { Name = "动作" });
        await context.SaveChangesAsync();
        var catId = context.Categories.First().Id;
        var movie = await service.AddAsync(new Movie { Title = "清空分类", Year = 2020, CategoryId = catId });

        await service.SetCategoryAsync(movie.Id, null);

        var updated = await service.GetByIdAsync(movie.Id);
        updated!.CategoryId.Should().BeNull();
    }

    [Fact]
    public async Task SetCategoryAsync_ShouldReturnFalse_WhenNotExists()
    {
        var (_, service) = CreateService(nameof(SetCategoryAsync_ShouldReturnFalse_WhenNotExists));

        var result = await service.SetCategoryAsync(999, 1);

        result.Should().BeFalse();
    }

    #endregion

    #region SetTagsAsync Tests

    [Fact]
    public async Task SetTagsAsync_ShouldAddTags()
    {
        var (context, service) = CreateService(nameof(SetTagsAsync_ShouldAddTags));
        var tag1 = new Tag { Name = "科幻" };
        var tag2 = new Tag { Name = "冒险" };
        context.Tags.AddRange(tag1, tag2);
        await context.SaveChangesAsync();
        var movie = await service.AddAsync(new Movie { Title = "标签测试", Year = 2020 });

        await service.SetTagsAsync(movie.Id, new List<int> { tag1.Id, tag2.Id });

        var tags = await service.GetByIdAsync(movie.Id);
        tags!.MovieTags.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetTagsAsync_ShouldReplaceTags()
    {
        var (context, service) = CreateService(nameof(SetTagsAsync_ShouldReplaceTags));
        var tag1 = new Tag { Name = "科幻" };
        var tag2 = new Tag { Name = "动作" };
        var tag3 = new Tag { Name = "剧情" };
        context.Tags.AddRange(tag1, tag2, tag3);
        await context.SaveChangesAsync();

        var movie = await service.AddAsync(new Movie { Title = "替换标签", Year = 2020 });
        await service.SetTagsAsync(movie.Id, new List<int> { tag1.Id, tag2.Id });

        // 替换
        await service.SetTagsAsync(movie.Id, new List<int> { tag3.Id });

        var tags = await service.GetByIdAsync(movie.Id);
        tags!.MovieTags.Should().HaveCount(1);
        tags.MovieTags.First().TagId.Should().Be(tag3.Id);
    }

    [Fact]
    public async Task SetTagsAsync_ShouldClearAllTags()
    {
        var (context, service) = CreateService(nameof(SetTagsAsync_ShouldClearAllTags));
        var tag = new Tag { Name = "科幻" };
        context.Tags.Add(tag);
        await context.SaveChangesAsync();
        var movie = await service.AddAsync(new Movie { Title = "清空标签", Year = 2020 });
        await service.SetTagsAsync(movie.Id, new List<int> { tag.Id });

        await service.SetTagsAsync(movie.Id, new List<int>());

        var tags = await service.GetByIdAsync(movie.Id);
        tags!.MovieTags.Should().BeEmpty();
    }

    #endregion

    #region SearchAsync Tests

    [Fact]
    public async Task SearchAsync_ShouldReturnPagedResults()
    {
        var (_, service) = CreateService(nameof(SearchAsync_ShouldReturnPagedResults));
        for (var i = 1; i <= 25; i++)
            await service.AddAsync(new Movie { Title = $"电影{i}", Year = 2020 });

        var (movies, total) = await service.SearchAsync(
            null, null, null, null, null, null, null, null, false, 1, 10);

        movies.Should().HaveCount(10);
        total.Should().Be(25);
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnSecondPage()
    {
        var (_, service) = CreateService(nameof(SearchAsync_ShouldReturnSecondPage));
        for (var i = 1; i <= 15; i++)
            await service.AddAsync(new Movie { Title = $"电影{i}", Year = 2020 });

        var (movies, total) = await service.SearchAsync(
            null, null, null, null, null, null, null, null, false, 2, 10);

        movies.Should().HaveCount(5);
        total.Should().Be(15);
    }

    #endregion

    #region GetTotalCountAsync Tests

    [Fact]
    public async Task GetTotalCountAsync_ShouldReturnCount()
    {
        var (_, service) = CreateService(nameof(GetTotalCountAsync_ShouldReturnCount));
        await service.AddAsync(new Movie { Title = "A", Year = 2020 });
        await service.AddAsync(new Movie { Title = "B", Year = 2021 });
        await service.AddAsync(new Movie { Title = "C", Year = 2022 });

        var count = await service.GetTotalCountAsync();

        count.Should().Be(3);
    }

    [Fact]
    public async Task GetTotalCountAsync_ShouldReturnZero_WhenNoMovies()
    {
        var (_, service) = CreateService(nameof(GetTotalCountAsync_ShouldReturnZero_WhenNoMovies));

        var count = await service.GetTotalCountAsync();

        count.Should().Be(0);
    }

    #endregion
}
