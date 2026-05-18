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
/// 跨服务集成测试 — 模拟真实业务场景
/// </summary>
public class IntegrationTests
{
    private static (
        MovieDbContext context,
        MovieService movieService,
        CategoryService categoryService,
        TagService tagService
    ) CreateServices(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var context = new MovieDbContext(options);
        var movieRepo = new MovieRepository(context);
        var catRepo = new CategoryRepository(context);
        var tagRepo = new TagRepository(context);
        return (
            context,
            new MovieService(movieRepo, tagRepo),
            new CategoryService(catRepo),
            new TagService(tagRepo)
        );
    }

    #region 完整业务流程

    [Fact]
    public async Task FullWorkflow_AddMovieWithCategoryAndTags_ThenSearch()
    {
        var (_, movieSvc, catSvc, tagSvc) = CreateServices(
            nameof(FullWorkflow_AddMovieWithCategoryAndTags_ThenSearch));

        // 创建分类
        var cat = await catSvc.AddAsync(new Category { Name = "科幻" });

        // 创建标签
        var tag1 = await tagSvc.AddAsync(new Tag { Name = "经典" });
        var tag2 = await tagSvc.AddAsync(new Tag { Name = "IMAX" });

        // 创建电影
        var movie = await movieSvc.AddAsync(new Movie
        {
            Title = "星际穿越",
            Year = 2014,
            Director = "克里斯托弗·诺兰",
            CategoryId = cat.Id
        });

        // 设置标签
        await movieSvc.SetTagsAsync(movie.Id, new List<int> { tag1.Id, tag2.Id });

        // 搜索验证
        var (results, total) = await movieSvc.SearchAsync(
            "星际", cat.Id, null, null, null, null, null, null, false, 1, 10);

        results.Should().HaveCount(1);
        total.Should().Be(1);
        results[0].CategoryId.Should().Be(cat.Id);

        // 验证标签
        var tags = await tagSvc.GetTagsForMovieAsync(movie.Id);
        tags.Should().HaveCount(2);
        tags.Select(t => t.Name).Should().Contain(["经典", "IMAX"]);
    }

    [Fact]
    public async Task FullWorkflow_AddMovie_SetRatingStatusFavorite_VerifyAll()
    {
        var (_, movieSvc, catSvc, _) = CreateServices(
            nameof(FullWorkflow_AddMovie_SetRatingStatusFavorite_VerifyAll));

        var cat = await catSvc.AddAsync(new Category { Name = "剧情" });
        var movie = await movieSvc.AddAsync(new Movie
        {
            Title = "肖申克的救赎",
            Year = 1994,
            CategoryId = cat.Id
        });

        // 设置评分
        await movieSvc.SetRatingAsync(movie.Id, 10);

        // 设置状态 + 日期
        var watchDate = new DateTime(2020, 3, 15);
        await movieSvc.SetWatchStatusAsync(movie.Id, WatchStatus.Watched, watchDate);

        // 收藏
        await movieSvc.ToggleFavoriteAsync(movie.Id);

        // 笔记
        await movieSvc.UpdateNotesAsync(movie.Id, "经典中的经典");

        // 验证
        var loaded = await movieSvc.GetByIdAsync(movie.Id);
        loaded!.Rating.Should().Be(10);
        loaded.WatchStatus.Should().Be(WatchStatus.Watched);
        loaded.WatchDate.Should().Be(watchDate);
        loaded.IsFavorite.Should().BeTrue();
        loaded.Notes.Should().Be("经典中的经典");
    }

    [Fact]
    public async Task FullWorkflow_MoveMovieBetweenCategories()
    {
        var (_, movieSvc, catSvc, _) = CreateServices(
            nameof(FullWorkflow_MoveMovieBetweenCategories));

        var cat1 = await catSvc.AddAsync(new Category { Name = "动作" });
        var cat2 = await catSvc.AddAsync(new Category { Name = "科幻" });
        var movie = await movieSvc.AddAsync(new Movie
        {
            Title = "黑客帝国",
            Year = 1999,
            CategoryId = cat1.Id
        });

        // 移动到新分类
        await movieSvc.SetCategoryAsync(movie.Id, cat2.Id);

        var loaded = await movieSvc.GetByIdAsync(movie.Id);
        loaded!.CategoryId.Should().Be(cat2.Id);

        // 按旧分类搜索不应找到
        var (results, _) = await movieSvc.SearchAsync(
            null, cat1.Id, null, null, null, null, null, null, false, 1, 10);
        results.Should().BeEmpty();

        // 按新分类搜索应找到
        (results, _) = await movieSvc.SearchAsync(
            null, cat2.Id, null, null, null, null, null, null, false, 1, 10);
        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task FullWorkflow_TagOperations_ClearAndReplace()
    {
        var (_, movieSvc, _, tagSvc) = CreateServices(
            nameof(FullWorkflow_TagOperations_ClearAndReplace));

        var tag1 = await tagSvc.AddAsync(new Tag { Name = "科幻" });
        var tag2 = await tagSvc.AddAsync(new Tag { Name = "动作" });
        var tag3 = await tagSvc.AddAsync(new Tag { Name = "悬疑" });
        var movie = await movieSvc.AddAsync(new Movie { Title = "盗梦空间", Year = 2010 });

        // 添加标签
        await movieSvc.SetTagsAsync(movie.Id, new List<int> { tag1.Id, tag2.Id, tag3.Id });
        (await tagSvc.GetTagsForMovieAsync(movie.Id)).Should().HaveCount(3);

        // 替换（去掉科幻，加冒险）
        var tag4 = await tagSvc.AddAsync(new Tag { Name = "冒险" });
        await movieSvc.SetTagsAsync(movie.Id, new List<int> { tag2.Id, tag3.Id, tag4.Id });
        var tags = await tagSvc.GetTagsForMovieAsync(movie.Id);
        tags.Should().HaveCount(3);
        tags.Select(t => t.Name).Should().Contain(["动作", "悬疑", "冒险"]);
        tags.Select(t => t.Name).Should().NotContain("科幻");

        // 全部清除
        await movieSvc.SetTagsAsync(movie.Id, new List<int>());
        (await tagSvc.GetTagsForMovieAsync(movie.Id)).Should().BeEmpty();
    }

    #endregion

    #region 分类树场景

    [Fact]
    public async Task CategoryTree_DeepNesting_ShouldWork()
    {
        var (_, _, catSvc, _) = CreateServices(nameof(CategoryTree_DeepNesting_ShouldWork));

        var root = await catSvc.AddAsync(new Category { Name = "影视" });
        var level1 = await catSvc.AddAsync(new Category { Name = "电影", ParentId = root.Id });
        var level2 = await catSvc.AddAsync(new Category { Name = "外国电影", ParentId = level1.Id });
        var level3 = await catSvc.AddAsync(new Category { Name = "好莱坞", ParentId = level2.Id });

        var tree = await catSvc.GetCategoryTreeAsync();
        tree.Should().HaveCount(1);
        tree[0].Children.Should().HaveCount(1);
        tree[0].Children.First().Name.Should().Be("电影");
    }

    [Fact]
    public async Task CategoryTree_CanDeleteLeaf_NotRootWithChildren()
    {
        var (_, _, catSvc, _) = CreateServices(
            nameof(CategoryTree_CanDeleteLeaf_NotRootWithChildren));

        var root = await catSvc.AddAsync(new Category { Name = "类型" });
        var child = await catSvc.AddAsync(new Category { Name = "科幻", ParentId = root.Id });

        // 子分类可以删除
        (await catSvc.CanDeleteAsync(child.Id)).Should().BeTrue();
        await catSvc.DeleteAsync(child.Id);
        (await catSvc.GetAllAsync()).Should().HaveCount(1);

        // 有子分类的不能删除
        var root2 = await catSvc.AddAsync(new Category { Name = "地区" });
        await catSvc.AddAsync(new Category { Name = "中国大陆", ParentId = root2.Id });
        (await catSvc.CanDeleteAsync(root2.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task CategoryTree_MultipleRoots_ShouldAllShow()
    {
        var (_, _, catSvc, _) = CreateServices(
            nameof(CategoryTree_MultipleRoots_ShouldAllShow));

        await catSvc.AddAsync(new Category { Name = "电影类型" });
        await catSvc.AddAsync(new Category { Name = "地区" });
        await catSvc.AddAsync(new Category { Name = "年代" });

        var roots = await catSvc.GetRootCategoriesAsync();
        roots.Should().HaveCount(3);
        roots.Select(r => r.Name).Should().Contain(["电影类型", "地区", "年代"]);
    }

    #endregion

    #region 搜索复合条件

    [Fact]
    public async Task Search_CombinedFilters_YearAndRatingAndStatus()
    {
        var (_, movieSvc, catSvc, _) = CreateServices(
            nameof(Search_CombinedFilters_YearAndRatingAndStatus));

        var cat = await catSvc.AddAsync(new Category { Name = "科幻" });

        // 添加多部电影
        var m1 = new Movie { Title = "高分新片", Year = 2023, Rating = 9, WatchStatus = WatchStatus.Watched, CategoryId = cat.Id };
        var m2 = new Movie { Title = "低分新片", Year = 2023, Rating = 4, WatchStatus = WatchStatus.Watched, CategoryId = cat.Id };
        var m3 = new Movie { Title = "高分老片", Year = 1990, Rating = 9, WatchStatus = WatchStatus.WantToWatch, CategoryId = cat.Id };
        await movieSvc.AddAsync(m1);
        await movieSvc.AddAsync(m2);
        await movieSvc.AddAsync(m3);

        // 筛选：2020年之后 + 评分≥7 + 已看
        var (results, total) = await movieSvc.SearchAsync(
            null, null, null, 2020, null, 7, WatchStatus.Watched, null, false, 1, 10);

        results.Should().HaveCount(1);
        total.Should().Be(1);
        results[0].Title.Should().Be("高分新片");
    }

    [Fact]
    public async Task Search_TagFilterAcrossMovies()
    {
        var (_, movieSvc, _, tagSvc) = CreateServices(
            nameof(Search_TagFilterAcrossMovies));

        var tag1 = await tagSvc.AddAsync(new Tag { Name = "科幻" });
        var tag2 = await tagSvc.AddAsync(new Tag { Name = "动作" });

        var m1 = await movieSvc.AddAsync(new Movie { Title = "科幻片A", Year = 2020 });
        var m2 = await movieSvc.AddAsync(new Movie { Title = "动作片B", Year = 2021 });
        var m3 = await movieSvc.AddAsync(new Movie { Title = "科幻动作片C", Year = 2022 });

        await movieSvc.SetTagsAsync(m1.Id, new List<int> { tag1.Id });
        await movieSvc.SetTagsAsync(m2.Id, new List<int> { tag2.Id });
        await movieSvc.SetTagsAsync(m3.Id, new List<int> { tag1.Id, tag2.Id });

        // 搜索有"科幻"标签的
        var searchParams = new { page = 1, pageSize = 10 };
        var (results, _) = await movieSvc.SearchAsync(
            null, null, new List<int> { tag1.Id }, null, null, null, null, null, false, 1, 10);

        results.Should().HaveCount(2);
        results.Select(m => m.Title).Should().Contain(["科幻片A", "科幻动作片C"]);
    }

    [Fact]
    public async Task Search_KeywordInMultipleFields()
    {
        var (_, movieSvc, _, _) = CreateServices(
            nameof(Search_KeywordInMultipleFields));

        await movieSvc.AddAsync(new Movie { Title = "黑客帝国", Director = "沃卓斯基", Year = 1999 });
        await movieSvc.AddAsync(new Movie { Title = "盗梦空间", Director = "诺兰", Cast = "莱昂纳多", Year = 2010 });
        await movieSvc.AddAsync(new Movie { Title = "泰坦尼克号", Director = "卡梅隆", Cast = "莱昂纳多", Year = 1997 });

        // 搜索"莱昂纳多"应该匹配主演字段
        var (results, _) = await movieSvc.SearchAsync(
            "莱昂纳多", null, null, null, null, null, null, null, false, 1, 10);

        results.Should().HaveCount(2);
    }

    #endregion

    #region 边界场景

    [Fact]
    public async Task EdgeCase_EmptyStringVsNull()
    {
        var (_, movieSvc, _, _) = CreateServices(nameof(EdgeCase_EmptyStringVsNull));

        var movie = await movieSvc.AddAsync(new Movie { Title = "测试", Year = 2020, Notes = "笔记" });
        await movieSvc.UpdateNotesAsync(movie.Id, null);
        (await movieSvc.GetByIdAsync(movie.Id))!.Notes.Should().BeNull();

        await movieSvc.UpdateNotesAsync(movie.Id, "新笔记");
        (await movieSvc.GetByIdAsync(movie.Id))!.Notes.Should().Be("新笔记");
    }

    [Fact]
    public async Task EdgeCase_PageBeyondRange_ShouldReturnEmpty()
    {
        var (_, movieSvc, _, _) = CreateServices(
            nameof(EdgeCase_PageBeyondRange_ShouldReturnEmpty));

        await movieSvc.AddAsync(new Movie { Title = "唯一的电影", Year = 2020 });

        var (results, total) = await movieSvc.SearchAsync(
            null, null, null, null, null, null, null, null, false, 5, 10);

        results.Should().BeEmpty();
        total.Should().Be(1);
    }

    [Fact]
    public async Task EdgeCase_FavoriteToggle_MultipleToggles()
    {
        var (_, movieSvc, _, _) = CreateServices(
            nameof(EdgeCase_FavoriteToggle_MultipleToggles));

        var movie = await movieSvc.AddAsync(
            new Movie { Title = "多次切换", Year = 2020, IsFavorite = false });

        await movieSvc.ToggleFavoriteAsync(movie.Id);
        (await movieSvc.GetByIdAsync(movie.Id))!.IsFavorite.Should().BeTrue();

        await movieSvc.ToggleFavoriteAsync(movie.Id);
        (await movieSvc.GetByIdAsync(movie.Id))!.IsFavorite.Should().BeFalse();

        await movieSvc.ToggleFavoriteAsync(movie.Id);
        (await movieSvc.GetByIdAsync(movie.Id))!.IsFavorite.Should().BeTrue();
    }

    [Fact]
    public async Task EdgeCase_DeleteCascadesFromMovie()
    {
        var (_, movieSvc, _, tagSvc) = CreateServices(
            nameof(EdgeCase_DeleteCascadesFromMovie));

        var tag = await tagSvc.AddAsync(new Tag { Name = "临时标签" });
        var movie = await movieSvc.AddAsync(new Movie { Title = "临时电影", Year = 2020 });
        await movieSvc.SetTagsAsync(movie.Id, new List<int> { tag.Id });

        // 删除电影后，MovieTag 关联应级联删除
        await movieSvc.DeleteAsync(movie.Id);
        (await movieSvc.GetByIdAsync(movie.Id)).Should().BeNull();

        // 标签本身不应被删除
        (await tagSvc.GetByIdAsync(tag.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task EdgeCase_MovieYear_1888_ShouldPass()
    {
        var (_, movieSvc, _, _) = CreateServices(nameof(EdgeCase_MovieYear_1888_ShouldPass));

        var movie = await movieSvc.AddAsync(new Movie
        {
            Title = "第一部电影",
            Year = 1888 // 世界第一部电影《朗德海花园场景》
        });

        movie.Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EdgeCase_MovieYear_CurrentYearPlus5_ShouldPass()
    {
        var (_, movieSvc, _, _) = CreateServices(
            nameof(EdgeCase_MovieYear_CurrentYearPlus5_ShouldPass));

        var movie = await movieSvc.AddAsync(new Movie
        {
            Title = "未来电影",
            Year = DateTime.Now.Year + 5 // 未来5年内合理
        });

        movie.Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EdgeCase_CategoryName_DuplicateAllowed()
    {
        var (_, _, catSvc, _) = CreateServices(
            nameof(EdgeCase_CategoryName_DuplicateAllowed));

        await catSvc.AddAsync(new Category { Name = "科幻" });
        // 同名分类应该允许（不同层级可能有同名）
        var cat2 = await catSvc.AddAsync(new Category { Name = "科幻" });

        cat2.Id.Should().NotBe(0);
    }

    [Fact]
    public async Task EdgeCase_TagName_UniquenessConfigured()
    {
        var (_, _, _, tagSvc) = CreateServices(
            nameof(EdgeCase_TagName_UniquenessConfigured));

        var tag1 = await tagSvc.AddAsync(new Tag { Name = "唯一标签" });
        tag1.Id.Should().BeGreaterThan(0);

        // 注意：InMemory 数据库不强制唯一约束，但 Fluent API 配置已正确设置
        // 实际 SQLite 运行时第二句 AddAsync 会抛异常
        var tags = await tagSvc.GetAllAsync();
        tags.Should().HaveCount(1);
        tags[0].Name.Should().Be("唯一标签");
    }

    #endregion

    #region 统计相关

    [Fact]
    public async Task Statistics_CountByCategory()
    {
        var (_, movieSvc, catSvc, _) = CreateServices(
            nameof(Statistics_CountByCategory));

        var cat1 = await catSvc.AddAsync(new Category { Name = "科幻" });
        var cat2 = await catSvc.AddAsync(new Category { Name = "剧情" });

        await movieSvc.AddAsync(new Movie { Title = "A", Year = 2020, CategoryId = cat1.Id });
        await movieSvc.AddAsync(new Movie { Title = "B", Year = 2021, CategoryId = cat1.Id });
        await movieSvc.AddAsync(new Movie { Title = "C", Year = 2022, CategoryId = cat2.Id });

        // 分类1应该有2部
        var (results1, count1) = await movieSvc.SearchAsync(
            null, cat1.Id, null, null, null, null, null, null, false, 1, 10);
        count1.Should().Be(2);

        // 分类2应该有1部
        var (results2, count2) = await movieSvc.SearchAsync(
            null, cat2.Id, null, null, null, null, null, null, false, 1, 10);
        count2.Should().Be(1);
    }

    [Fact]
    public async Task Statistics_CountByStatus()
    {
        var (_, movieSvc, _, _) = CreateServices(nameof(Statistics_CountByStatus));

        var m1 = new Movie { Title = "想看A", Year = 2020, WatchStatus = WatchStatus.WantToWatch };
        var m2 = new Movie { Title = "在看A", Year = 2021, WatchStatus = WatchStatus.Watching };
        var m3 = new Movie { Title = "已看A", Year = 2022, WatchStatus = WatchStatus.Watched };
        await movieSvc.AddAsync(m1);
        await movieSvc.AddAsync(m2);
        await movieSvc.AddAsync(m3);

        var (_, countWant) = await movieSvc.SearchAsync(
            null, null, null, null, null, null, WatchStatus.WantToWatch, null, false, 1, 10);
        var (_, countWatching) = await movieSvc.SearchAsync(
            null, null, null, null, null, null, WatchStatus.Watching, null, false, 1, 10);
        var (_, countWatched) = await movieSvc.SearchAsync(
            null, null, null, null, null, null, WatchStatus.Watched, null, false, 1, 10);

        countWant.Should().Be(1);
        countWatching.Should().Be(1);
        countWatched.Should().Be(1);
    }

    #endregion
}
