using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

public class TagRepositoryTests
{
    private static MovieDbContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new MovieDbContext(options);
    }

    [Fact]
    public async Task AddAsync_ShouldAddTag()
    {
        using var context = CreateInMemoryContext(nameof(AddAsync_ShouldAddTag));
        var repo = new TagRepository(context);
        var tag = new Tag { Name = "经典", Color = "#FF5722" };

        var result = await repo.AddAsync(tag);

        result.Id.Should().BeGreaterThan(0);
        result.Name.Should().Be("经典");
        result.Color.Should().Be("#FF5722");
        result.CreatedAt.Should().BeCloseTo(System.DateTime.UtcNow, System.TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnTag_WhenExists()
    {
        using var context = CreateInMemoryContext(nameof(GetByIdAsync_ShouldReturnTag_WhenExists));
        var repo = new TagRepository(context);
        var tag = await repo.AddAsync(new Tag { Name = "IMDB Top 250" });

        var result = await repo.GetByIdAsync(tag.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("IMDB Top 250");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotExists()
    {
        using var context = CreateInMemoryContext(nameof(GetByIdAsync_ShouldReturnNull_WhenNotExists));
        var repo = new TagRepository(context);

        var result = await repo.GetByIdAsync(999);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnAllTags_OrderedByName()
    {
        using var context = CreateInMemoryContext(nameof(GetAllAsync_ShouldReturnAllTags_OrderedByName));
        var repo = new TagRepository(context);
        await repo.AddAsync(new Tag { Name = "C标签" });
        await repo.AddAsync(new Tag { Name = "A标签" });
        await repo.AddAsync(new Tag { Name = "B标签" });

        var results = await repo.GetAllAsync();

        results.Should().HaveCount(3);
        results[0].Name.Should().Be("A标签");
        results[1].Name.Should().Be("B标签");
        results[2].Name.Should().Be("C标签");
    }

    [Fact]
    public async Task GetByIdsAsync_ShouldReturnTags_ByIds()
    {
        using var context = CreateInMemoryContext(nameof(GetByIdsAsync_ShouldReturnTags_ByIds));
        var repo = new TagRepository(context);
        var t1 = await repo.AddAsync(new Tag { Name = "科幻" });
        var t2 = await repo.AddAsync(new Tag { Name = "动作" });
        var t3 = await repo.AddAsync(new Tag { Name = "剧情" });

        var results = await repo.GetByIdsAsync(new List<int> { t1.Id, t3.Id });

        results.Should().HaveCount(2);
        results.Select(t => t.Name).Should().Contain(["科幻", "剧情"]);
    }

    [Fact]
    public async Task UpdateAsync_ShouldUpdateTag()
    {
        using var context = CreateInMemoryContext(nameof(UpdateAsync_ShouldUpdateTag));
        var repo = new TagRepository(context);
        var tag = await repo.AddAsync(new Tag { Name = "原始标签", Color = "#000000" });

        tag.Name = "修改后标签";
        tag.Color = "#FFFFFF";
        await repo.UpdateAsync(tag);

        var updated = await repo.GetByIdAsync(tag.Id);
        updated!.Name.Should().Be("修改后标签");
        updated.Color.Should().Be("#FFFFFF");
    }

    [Fact]
    public async Task DeleteAsync_ShouldDeleteTag_WhenExists()
    {
        using var context = CreateInMemoryContext(nameof(DeleteAsync_ShouldDeleteTag_WhenExists));
        var repo = new TagRepository(context);
        var tag = await repo.AddAsync(new Tag { Name = "待删除" });

        var result = await repo.DeleteAsync(tag.Id);

        result.Should().BeTrue();
        (await repo.ExistsAsync(tag.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotExists()
    {
        using var context = CreateInMemoryContext(nameof(DeleteAsync_ShouldReturnFalse_WhenNotExists));
        var repo = new TagRepository(context);

        var result = await repo.DeleteAsync(999);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetTagsForMovieAsync_ShouldReturnTags()
    {
        using var context = CreateInMemoryContext(nameof(GetTagsForMovieAsync_ShouldReturnTags));
        var repo = new TagRepository(context);
        var tag1 = await repo.AddAsync(new Tag { Name = "科幻" });
        var tag2 = await repo.AddAsync(new Tag { Name = "冒险" });

        var movie = new Movie { Title = "测试电影" };
        context.Movies.Add(movie);
        await context.SaveChangesAsync();

        context.MovieTags.AddRange(
            new MovieTag { MovieId = movie.Id, TagId = tag1.Id },
            new MovieTag { MovieId = movie.Id, TagId = tag2.Id }
        );
        await context.SaveChangesAsync();

        var tags = await repo.GetTagsForMovieAsync(movie.Id);

        tags.Should().HaveCount(2);
        tags.Select(t => t.Name).Should().Contain(["科幻", "冒险"]);
    }

    [Fact]
    public async Task AddMovieTagsAsync_ShouldAddTags_WithoutDuplicates()
    {
        using var context = CreateInMemoryContext(nameof(AddMovieTagsAsync_ShouldAddTags_WithoutDuplicates));
        var repo = new TagRepository(context);
        var tag1 = await repo.AddAsync(new Tag { Name = "科幻" });
        var tag2 = await repo.AddAsync(new Tag { Name = "动作" });
        var tag3 = await repo.AddAsync(new Tag { Name = "剧情" });

        var movie = new Movie { Title = "测试电影" };
        context.Movies.Add(movie);
        context.MovieTags.Add(new MovieTag { MovieId = movie.Id, TagId = tag1.Id }); // 已存在
        await context.SaveChangesAsync();

        // 添加 tag1（重复）和 tag2, tag3
        await repo.AddMovieTagsAsync(movie.Id, new List<int> { tag1.Id, tag2.Id, tag3.Id });

        var tags = await repo.GetTagsForMovieAsync(movie.Id);
        tags.Should().HaveCount(3);
    }

    [Fact]
    public async Task RemoveMovieTagsAsync_ShouldRemoveSpecifiedTags()
    {
        using var context = CreateInMemoryContext(nameof(RemoveMovieTagsAsync_ShouldRemoveSpecifiedTags));
        var repo = new TagRepository(context);
        var tag1 = await repo.AddAsync(new Tag { Name = "科幻" });
        var tag2 = await repo.AddAsync(new Tag { Name = "动作" });
        var tag3 = await repo.AddAsync(new Tag { Name = "剧情" });

        var movie = new Movie { Title = "测试电影" };
        context.Movies.Add(movie);
        context.MovieTags.AddRange(
            new MovieTag { MovieId = movie.Id, TagId = tag1.Id },
            new MovieTag { MovieId = movie.Id, TagId = tag2.Id },
            new MovieTag { MovieId = movie.Id, TagId = tag3.Id }
        );
        await context.SaveChangesAsync();

        await repo.RemoveMovieTagsAsync(movie.Id, new List<int> { tag1.Id, tag3.Id });

        var tags = await repo.GetTagsForMovieAsync(movie.Id);
        tags.Should().HaveCount(1);
        tags[0].Name.Should().Be("动作");
    }
}
