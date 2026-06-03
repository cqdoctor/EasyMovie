using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

public class StatisticsServiceTests
{
    private static (MovieDbContext context, StatisticsService service) CreateService(string dbName)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var context = new MovieDbContext(options);
        var service = new StatisticsService(context);
        return (context, service);
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldReturnZero_WhenNoMovies()
    {
        var (_, service) = CreateService(nameof(GetStatisticsAsync_ShouldReturnZero_WhenNoMovies));

        var data = await service.GetStatisticsAsync();

        data.TotalMovies.Should().Be(0);
        data.Watched.Should().Be(0);
        data.WantToWatch.Should().Be(0);
        data.Favorites.Should().Be(0);
        data.AverageRating.Should().Be(0);
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldCountCorrectTotals()
    {
        var (context, service) = CreateService(nameof(GetStatisticsAsync_ShouldCountCorrectTotals));

        context.Movies.AddRange(
            new Movie { Title = "A", Year = 2020, WatchStatus = WatchStatus.Watched, Rating = 8, IsFavorite = true },
            new Movie { Title = "B", Year = 2021, WatchStatus = WatchStatus.NotWatched, Rating = 6 },
            new Movie { Title = "C", Year = 2022, WatchStatus = WatchStatus.WantToWatch },
            new Movie { Title = "D", Year = 2023, WatchStatus = WatchStatus.Watched, Rating = 10, IsFavorite = true }
        );
        await context.SaveChangesAsync();

        var data = await service.GetStatisticsAsync();

        data.TotalMovies.Should().Be(4);
        data.Watched.Should().Be(2);
        data.NotWatched.Should().Be(1);
        data.WantToWatch.Should().Be(1);
        data.Favorites.Should().Be(2);
        data.RatedCount.Should().Be(3);
        data.AverageRating.Should().BeApproximately(8.0, 0.01);
    }

    [Fact]
    public async Task GetCategoryDistributionAsync_ShouldGroupByCategory()
    {
        var (context, service) = CreateService(nameof(GetCategoryDistributionAsync_ShouldGroupByCategory));

        var sciFi = new Category { Name = "科幻" };
        var drama = new Category { Name = "剧情" };
        context.Categories.AddRange(sciFi, drama);
        await context.SaveChangesAsync();

        context.Movies.AddRange(
            new Movie { Title = "A", Year = 2020, CategoryId = sciFi.Id },
            new Movie { Title = "B", Year = 2021, CategoryId = sciFi.Id },
            new Movie { Title = "C", Year = 2022, CategoryId = drama.Id }
        );
        await context.SaveChangesAsync();

        var stats = await service.GetCategoryDistributionAsync();

        stats.Should().HaveCount(2);
        stats[0].Name.Should().Be("科幻");
        stats[0].Count.Should().Be(2);
        stats[1].Name.Should().Be("剧情");
        stats[1].Count.Should().Be(1);
    }

    [Fact]
    public async Task GetRatingDistributionAsync_ShouldCountRatings()
    {
        var (context, service) = CreateService(nameof(GetRatingDistributionAsync_ShouldCountRatings));

        context.Movies.AddRange(
            new Movie { Title = "A", Year = 2020, Rating = 8 },
            new Movie { Title = "B", Year = 2021, Rating = 8 },
            new Movie { Title = "C", Year = 2022, Rating = 5 },
            new Movie { Title = "D", Year = 2023, Rating = 10 }
        );
        await context.SaveChangesAsync();

        var stats = await service.GetRatingDistributionAsync();

        stats.Should().Contain(s => s.Rating == 8 && s.Count == 2);
        stats.Should().Contain(s => s.Rating == 5 && s.Count == 1);
        stats.Should().Contain(s => s.Rating == 10 && s.Count == 1);
        stats.Sum(s => s.Count).Should().Be(4);
    }

    [Fact]
    public async Task GetYearlyStatsAsync_ShouldGroupByYear()
    {
        var (context, service) = CreateService(nameof(GetYearlyStatsAsync_ShouldGroupByYear));

        context.Movies.AddRange(
            new Movie { Title = "A", Year = 2020, WatchStatus = WatchStatus.Watched },
            new Movie { Title = "B", Year = 2020, WatchStatus = WatchStatus.WantToWatch },
            new Movie { Title = "C", Year = 2022, WatchStatus = WatchStatus.Watched }
        );
        await context.SaveChangesAsync();

        var stats = await service.GetYearlyStatsAsync();

        stats.Should().HaveCount(2);
        stats.Should().Contain(s => s.Year == 2020 && s.AddedCount == 2 && s.WatchedCount == 1);
        stats.Should().Contain(s => s.Year == 2022 && s.AddedCount == 1 && s.WatchedCount == 1);
    }

    [Fact]
    public async Task GetMonthlyWatchStatsAsync_ShouldCountByMonth()
    {
        var (context, service) = CreateService(nameof(GetMonthlyWatchStatsAsync_ShouldCountByMonth));

        context.Movies.AddRange(
            new Movie { Title = "A", Year = 2020, WatchStatus = WatchStatus.Watched,
                WatchDate = new DateTime(2024, 3, 15) },
            new Movie { Title = "B", Year = 2020, WatchStatus = WatchStatus.Watched,
                WatchDate = new DateTime(2024, 3, 20) },
            new Movie { Title = "C", Year = 2020, WatchStatus = WatchStatus.Watched,
                WatchDate = new DateTime(2024, 7, 1) }
        );
        await context.SaveChangesAsync();

        var stats = await service.GetMonthlyWatchStatsAsync(2024);

        stats.Should().HaveCount(12);
        stats[2].WatchedCount.Should().Be(2); // 3月 = index 2
        stats[6].WatchedCount.Should().Be(1); // 7月 = index 6
        stats.Where(s => s.Month != 3 && s.Month != 7)
            .All(s => s.WatchedCount == 0).Should().BeTrue();
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldIncludeUncategorizedInCategory()
    {
        var (context, service) = CreateService(
            nameof(GetStatisticsAsync_ShouldIncludeUncategorizedInCategory));

        context.Movies.AddRange(
            new Movie { Title = "A", Year = 2020 }, // 无分类
            new Movie { Title = "B", Year = 2021 }  // 无分类
        );
        await context.SaveChangesAsync();

        var data = await service.GetStatisticsAsync();

        data.CategoryStats.Should().Contain(s => s.Name == "未分类" && s.Count == 2);
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldHandleEmptyRatingDistribution()
    {
        var (context, service) = CreateService(
            nameof(GetStatisticsAsync_ShouldHandleEmptyRatingDistribution));

        context.Movies.Add(new Movie { Title = "A", Year = 2020 }); // 无评分
        await context.SaveChangesAsync();

        var data = await service.GetStatisticsAsync();

        data.RatingStats.Should().BeEmpty();
        data.RatedCount.Should().Be(0);
    }

    [Fact]
    public async Task GetStatisticsAsync_WithManyMovies_ShouldNotThrow()
    {
        var (context, service) = CreateService(
            nameof(GetStatisticsAsync_WithManyMovies_ShouldNotThrow));

        for (var i = 1; i <= 50; i++)
        {
            context.Movies.Add(new Movie
            {
                Title = $"电影{i}",
                Year = 2000 + (i % 25),
                Rating = (i % 10) + 1,
                WatchStatus = (WatchStatus)(i % 3),
                IsFavorite = i % 5 == 0,
                WatchDate = i % 3 == 0 ? new DateTime(2024, (i % 12) + 1, 1) : null
            });
        }
        await context.SaveChangesAsync();

        var data = await service.GetStatisticsAsync();

        data.TotalMovies.Should().Be(50);
        data.YearlyStats.Should().NotBeEmpty();
        data.RatingStats.Should().NotBeEmpty();
    }
}
