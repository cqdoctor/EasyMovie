using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MovieManager.Core.Interfaces;
using MovieManager.Core.Models;
using MovieManager.Core.Services;
using MovieManager.Data;
using MovieManager.Data.Repositories;
using Xunit;

namespace MovieManager.Tests.Core.Tests;

public class CategoryServiceTests
{
    private static (MovieDbContext, ICategoryService) CreateService(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var context = new MovieDbContext(options);
        var repo = new CategoryRepository(context);
        var service = new CategoryService(repo);
        return (context, service);
    }

    [Fact]
    public async Task AddAsync_ShouldAddCategory()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldAddCategory));
        var result = await service.AddAsync(new Category { Name = "动作片" });

        result.Id.Should().BeGreaterThan(0);
        result.Name.Should().Be("动作片");
    }

    [Fact]
    public async Task AddAsync_ShouldThrow_WhenNameIsEmpty()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldThrow_WhenNameIsEmpty));
        await Assert.ThrowsAsync<System.ArgumentException>(() =>
            service.AddAsync(new Category { Name = "" }));
    }

    [Fact]
    public async Task AddAsync_ShouldThrow_WhenParentNotExists()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldThrow_WhenParentNotExists));
        await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
            service.AddAsync(new Category { Name = "子分类", ParentId = 999 }));
    }

    [Fact]
    public async Task AddAsync_ShouldAddChildCategory()
    {
        var (_, service) = CreateService(nameof(AddAsync_ShouldAddChildCategory));
        var parent = await service.AddAsync(new Category { Name = "电影类型" });
        var child = await service.AddAsync(new Category { Name = "科幻", ParentId = parent.Id });

        child.ParentId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task UpdateAsync_ShouldUpdateCategory()
    {
        var (_, service) = CreateService(nameof(UpdateAsync_ShouldUpdateCategory));
        var cat = await service.AddAsync(new Category { Name = "原始名称" });

        cat.Name = "修改后";
        await service.UpdateAsync(cat);

        var updated = await service.GetByIdAsync(cat.Id);
        updated!.Name.Should().Be("修改后");
    }

    [Fact]
    public async Task UpdateAsync_ShouldThrow_WhenNameIsEmpty()
    {
        var (_, service) = CreateService(nameof(UpdateAsync_ShouldThrow_WhenNameIsEmpty));
        var cat = await service.AddAsync(new Category { Name = "原始" });

        cat.Name = "";
        await Assert.ThrowsAsync<System.ArgumentException>(() => service.UpdateAsync(cat));
    }

    [Fact]
    public async Task UpdateAsync_ShouldThrow_WhenNotExists()
    {
        var (_, service) = CreateService(nameof(UpdateAsync_ShouldThrow_WhenNotExists));
        await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
            service.UpdateAsync(new Category { Id = 999, Name = "不存在" }));
    }

    [Fact]
    public async Task UpdateAsync_ShouldThrow_WhenSelfParenting()
    {
        var (_, service) = CreateService(nameof(UpdateAsync_ShouldThrow_WhenSelfParenting));
        var cat = await service.AddAsync(new Category { Name = "自引用测试" });

        cat.ParentId = cat.Id;
        await Assert.ThrowsAsync<System.InvalidOperationException>(() => service.UpdateAsync(cat));
    }

    [Fact]
    public async Task CanDeleteAsync_ShouldReturnTrue_WhenEmptyCategory()
    {
        var (_, service) = CreateService(nameof(CanDeleteAsync_ShouldReturnTrue_WhenEmptyCategory));
        var cat = await service.AddAsync(new Category { Name = "空分类" });

        var canDelete = await service.CanDeleteAsync(cat.Id);

        canDelete.Should().BeTrue();
    }

    [Fact]
    public async Task CanDeleteAsync_ShouldReturnFalse_WhenHasChildren()
    {
        var (context, service) = CreateService(nameof(CanDeleteAsync_ShouldReturnFalse_WhenHasChildren));
        var parent = await service.AddAsync(new Category { Name = "父分类" });
        context.Categories.Add(new Category { Name = "子分类", ParentId = parent.Id });
        await context.SaveChangesAsync();

        var canDelete = await service.CanDeleteAsync(parent.Id);

        canDelete.Should().BeFalse();
    }

    [Fact]
    public async Task CanDeleteAsync_ShouldReturnFalse_WhenHasMovies()
    {
        var (context, service) = CreateService(nameof(CanDeleteAsync_ShouldReturnFalse_WhenHasMovies));
        var cat = await service.AddAsync(new Category { Name = "有电影的分类" });
        context.Movies.Add(new Movie { Title = "电影1", CategoryId = cat.Id, Year = 2020 });
        await context.SaveChangesAsync();

        var canDelete = await service.CanDeleteAsync(cat.Id);

        canDelete.Should().BeFalse();
    }

    [Fact]
    public async Task CanDeleteAsync_ShouldReturnFalse_WhenNotExists()
    {
        var (_, service) = CreateService(nameof(CanDeleteAsync_ShouldReturnFalse_WhenNotExists));

        var canDelete = await service.CanDeleteAsync(999);

        canDelete.Should().BeFalse();
    }

    [Fact]
    public async Task GetCategoryTreeAsync_ShouldBuildTree()
    {
        var (context, service) = CreateService(nameof(GetCategoryTreeAsync_ShouldBuildTree));
        var root = await service.AddAsync(new Category { Name = "类型" });
        context.Categories.AddRange(
            new Category { Name = "科幻", ParentId = root.Id },
            new Category { Name = "动作", ParentId = root.Id },
            new Category { Name = "剧情", ParentId = root.Id }
        );
        await context.SaveChangesAsync();

        var tree = await service.GetCategoryTreeAsync();

        tree.Should().HaveCount(1);
        tree[0].Name.Should().Be("类型");
        tree[0].Children.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetCategoryTreeAsync_ShouldReturnEmpty_WhenNoCategories()
    {
        var (_, service) = CreateService(nameof(GetCategoryTreeAsync_ShouldReturnEmpty_WhenNoCategories));

        var tree = await service.GetCategoryTreeAsync();

        tree.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCategoryTreeAsync_ShouldHandleMultipleRoots()
    {
        var (_, service) = CreateService(nameof(GetCategoryTreeAsync_ShouldHandleMultipleRoots));
        await service.AddAsync(new Category { Name = "电影" });
        await service.AddAsync(new Category { Name = "电视剧" });
        await service.AddAsync(new Category { Name = "纪录片" });

        var tree = await service.GetCategoryTreeAsync();

        tree.Should().HaveCount(3);
        tree.Select(c => c.Name).Should().Contain(["电影", "电视剧", "纪录片"]);
    }
}
