using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

public class CategoryRepositoryTests
{
    private static MovieDbContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new MovieDbContext(options);
    }

    [Fact]
    public async Task AddAsync_ShouldAddCategory()
    {
        using var context = CreateInMemoryContext(nameof(AddAsync_ShouldAddCategory));
        var repo = new CategoryRepository(context);
        var category = new Category { Name = "动作片", Description = "动作类电影" };

        var result = await repo.AddAsync(category);

        result.Id.Should().BeGreaterThan(0);
        result.Name.Should().Be("动作片");
        result.Description.Should().Be("动作类电影");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnCategory_WithChildren()
    {
        using var context = CreateInMemoryContext(nameof(GetByIdAsync_ShouldReturnCategory_WithChildren));
        var repo = new CategoryRepository(context);
        var parent = await repo.AddAsync(new Category { Name = "科幻" });
        context.Categories.Add(new Category { Name = "硬科幻", ParentId = parent.Id });
        context.Categories.Add(new Category { Name = "软科幻", ParentId = parent.Id });
        await context.SaveChangesAsync();

        var result = await repo.GetByIdAsync(parent.Id);

        result.Should().NotBeNull();
        result!.Children.Should().HaveCount(2);
        result.Children.Select(c => c.Name).Should().Contain(["硬科幻", "软科幻"]);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnAllCategories()
    {
        using var context = CreateInMemoryContext(nameof(GetAllAsync_ShouldReturnAllCategories));
        var repo = new CategoryRepository(context);
        await repo.AddAsync(new Category { Name = "动作" });
        await repo.AddAsync(new Category { Name = "剧情" });
        await repo.AddAsync(new Category { Name = "喜剧" });

        var results = await repo.GetAllAsync();

        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetRootCategoriesAsync_ShouldReturnOnlyRootCategories()
    {
        using var context = CreateInMemoryContext(nameof(GetRootCategoriesAsync_ShouldReturnOnlyRootCategories));
        var repo = new CategoryRepository(context);
        var root = await repo.AddAsync(new Category { Name = "电影类型" });
        context.Categories.Add(new Category { Name = "科幻", ParentId = root.Id });
        context.Categories.Add(new Category { Name = "动作", ParentId = root.Id });
        await context.SaveChangesAsync();

        // 添加另一个根分类
        await repo.AddAsync(new Category { Name = "电视剧" });

        var roots = await repo.GetRootCategoriesAsync();

        roots.Should().HaveCount(2);
        roots.Select(r => r.Name).Should().Contain(["电影类型", "电视剧"]);
    }

    [Fact]
    public async Task GetChildrenAsync_ShouldReturnChildrenOfCategory()
    {
        using var context = CreateInMemoryContext(nameof(GetChildrenAsync_ShouldReturnChildrenOfCategory));
        var repo = new CategoryRepository(context);
        var parent = await repo.AddAsync(new Category { Name = "地区" });
        context.Categories.Add(new Category { Name = "中国大陆", ParentId = parent.Id });
        context.Categories.Add(new Category { Name = "美国", ParentId = parent.Id });
        context.Categories.Add(new Category { Name = "日本", ParentId = parent.Id });
        await context.SaveChangesAsync();

        var children = await repo.GetChildrenAsync(parent.Id);

        children.Should().HaveCount(3);
    }

    [Fact]
    public async Task UpdateAsync_ShouldUpdateCategory()
    {
        using var context = CreateInMemoryContext(nameof(UpdateAsync_ShouldUpdateCategory));
        var repo = new CategoryRepository(context);
        var category = await repo.AddAsync(new Category { Name = "原始名称" });

        category.Name = "修改后名称";
        await repo.UpdateAsync(category);

        var updated = await repo.GetByIdAsync(category.Id);
        updated!.Name.Should().Be("修改后名称");
    }

    [Fact]
    public async Task DeleteAsync_ShouldDeleteCategory_WhenNoChildren()
    {
        using var context = CreateInMemoryContext(nameof(DeleteAsync_ShouldDeleteCategory_WhenNoChildren));
        var repo = new CategoryRepository(context);
        var category = await repo.AddAsync(new Category { Name = "待删除" });

        var result = await repo.DeleteAsync(category.Id);

        result.Should().BeTrue();
        (await repo.ExistsAsync(category.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ShouldNotDeleteCategory_WhenHasChildren()
    {
        using var context = CreateInMemoryContext(nameof(DeleteAsync_ShouldNotDeleteCategory_WhenHasChildren));
        var repo = new CategoryRepository(context);
        var parent = await repo.AddAsync(new Category { Name = "父分类" });
        context.Categories.Add(new Category { Name = "子分类", ParentId = parent.Id });
        await context.SaveChangesAsync();

        var result = await repo.DeleteAsync(parent.Id);

        result.Should().BeFalse();
        (await repo.ExistsAsync(parent.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotExists()
    {
        using var context = CreateInMemoryContext(nameof(DeleteAsync_ShouldReturnFalse_WhenNotExists));
        var repo = new CategoryRepository(context);

        var result = await repo.DeleteAsync(999);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasMoviesAsync_ShouldReturnTrue_WhenMoviesExist()
    {
        using var context = CreateInMemoryContext(nameof(HasMoviesAsync_ShouldReturnTrue_WhenMoviesExist));
        var repo = new CategoryRepository(context);
        var category = await repo.AddAsync(new Category { Name = "动作" });
        context.Movies.Add(new Movie { Title = "电影1", CategoryId = category.Id });
        await context.SaveChangesAsync();

        var hasMovies = await repo.HasMoviesAsync(category.Id);

        hasMovies.Should().BeTrue();
    }

    [Fact]
    public async Task HasMoviesAsync_ShouldReturnFalse_WhenNoMovies()
    {
        using var context = CreateInMemoryContext(nameof(HasMoviesAsync_ShouldReturnFalse_WhenNoMovies));
        var repo = new CategoryRepository(context);
        var category = await repo.AddAsync(new Category { Name = "空分类" });

        var hasMovies = await repo.HasMoviesAsync(category.Id);

        hasMovies.Should().BeFalse();
    }
}
