using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

public class TagServiceTests
{
    private static (MovieDbContext, ITagService) CreateService(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var context = new MovieDbContext(options);
        var repo = new TagRepository(context);
        var service = new TagService(repo);
        return (context, service);
    }

    [Fact]
    public async Task AddAsync_ShouldAddTag()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldAddTag));
        var result = await service.AddAsync(new Tag { Name = "经典", Color = "#FF5722" });

        result.Id.Should().BeGreaterThan(0);
        result.Name.Should().Be("经典");
        result.Color.Should().Be("#FF5722");
    }

    [Fact]
    public async Task AddAsync_ShouldThrow_WhenNameIsEmpty()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldThrow_WhenNameIsEmpty));
        await Assert.ThrowsAsync<System.ArgumentException>(() =>
            service.AddAsync(new Tag { Name = "" }));
    }

    [Fact]
    public async Task UpdateAsync_ShouldUpdateTag()
    {
        var (_, service) = CreateService(nameof(UpdateAsync_ShouldUpdateTag));
        var tag = await service.AddAsync(new Tag { Name = "原始标签" });

        tag.Name = "修改后";
        await service.UpdateAsync(tag);

        var updated = await service.GetByIdAsync(tag.Id);
        updated!.Name.Should().Be("修改后");
    }

    [Fact]
    public async Task UpdateAsync_ShouldThrow_WhenNameIsEmpty()
    {
        var (_, service) = CreateService(nameof(UpdateAsync_ShouldThrow_WhenNameIsEmpty));
        var tag = await service.AddAsync(new Tag { Name = "原始" });

        tag.Name = "";
        await Assert.ThrowsAsync<System.ArgumentException>(() => service.UpdateAsync(tag));
    }

    [Fact]
    public async Task UpdateAsync_ShouldThrow_WhenNotExists()
    {
        var (_, service) = CreateService(nameof(UpdateAsync_ShouldThrow_WhenNotExists));
        await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
            service.UpdateAsync(new Tag { Id = 999, Name = "不存在" }));
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnTrue_WhenExists()
    {
        var (_, service) = CreateService(nameof(DeleteAsync_ShouldReturnTrue_WhenExists));
        var tag = await service.AddAsync(new Tag { Name = "待删除" });

        var result = await service.DeleteAsync(tag.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotExists()
    {
        var (_, service) = CreateService(nameof(DeleteAsync_ShouldReturnFalse_WhenNotExists));

        var result = await service.DeleteAsync(999);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetTagsForMovieAsync_ShouldReturnMovieTags()
    {
        var (context, service) = CreateService(nameof(GetTagsForMovieAsync_ShouldReturnMovieTags));
        var tag1 = await service.AddAsync(new Tag { Name = "科幻" });
        var tag2 = await service.AddAsync(new Tag { Name = "动作" });

        var movie = new Movie { Title = "测试", Year = 2020 };
        context.Movies.Add(movie);
        await context.SaveChangesAsync();
        context.MovieTags.AddRange(
            new MovieTag { MovieId = movie.Id, TagId = tag1.Id },
            new MovieTag { MovieId = movie.Id, TagId = tag2.Id }
        );
        await context.SaveChangesAsync();

        var tags = await service.GetTagsForMovieAsync(movie.Id);

        tags.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnAllTags()
    {
        var (_, service) = CreateService(nameof(GetAllAsync_ShouldReturnAllTags));
        await service.AddAsync(new Tag { Name = "B" });
        await service.AddAsync(new Tag { Name = "A" });
        await service.AddAsync(new Tag { Name = "C" });

        var tags = await service.GetAllAsync();

        tags.Should().HaveCount(3);
        tags[0].Name.Should().Be("A"); // 按名称排序
    }
}
