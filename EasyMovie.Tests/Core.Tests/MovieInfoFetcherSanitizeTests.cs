using System.Reflection;
using EasyMovie.Core.Interfaces;
using EasyMovie.Tools.MovieApi;
using FluentAssertions;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 元数据回写路径（MovieInfoFetcher）的写入护栏。
///
/// <c>CleanMerged</c> / <c>MergeResult</c> 是私有静态方法（前者是"全项目清洗的唯一汇合点"），
/// 这里沿用 <c>ApiClientHttpSharingTests</c> 的做法用反射直测，不为此改动生产代码可见性。
/// </summary>
public class MovieInfoFetcherSanitizeTests
{
    private static readonly MethodInfo CleanMerged = typeof(MovieInfoFetcher)
        .GetMethod("CleanMerged", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo MergeResult = typeof(MovieInfoFetcher)
        .GetMethod("MergeResult", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void CleanMerged_ShouldStripReleaseTagsFromTitle()
    {
        var result = new MovieSearchResult { Title = "三大队 Endless Journey EAC3" };

        CleanMerged.Invoke(null, new object[] { result });

        result.Title.Should().Be("三大队 Endless Journey");
    }

    [Fact]
    public void CleanMerged_ShouldKeepTitle_WhenEverythingLooksLikeATag()
    {
        var result = new MovieSearchResult { Title = "Atmos TrueHD" };

        CleanMerged.Invoke(null, new object[] { result });

        result.Title.Should().Be("Atmos TrueHD");   // 清洗为空时回退原值，不写空标题
    }

    [Fact]
    public void CleanMerged_ShouldZeroImplausibleYear_ButKeepPlausibleOne()
    {
        var dirty = new MovieSearchResult { Title = "脏年份", Year = 1000 };
        var good = new MovieSearchResult { Title = "正常年份", Year = 1972 };

        CleanMerged.Invoke(null, new object[] { dirty });
        CleanMerged.Invoke(null, new object[] { good });

        dirty.Year.Should().Be(0);
        good.Year.Should().Be(1972);
    }

    [Fact]
    public void MergeResult_ShouldRejectImplausibleYear_ButAcceptPlausibleOne()
    {
        var target = new MovieSearchResult { Title = "目标", Year = 0 };
        var dirtySource = new MovieSearchResult { Title = "源", Year = 1000 };
        var goodSource = new MovieSearchResult { Title = "源2", Year = 1972 };

        MergeResult.Invoke(null, new object[] { target, dirtySource });
        target.Year.Should().Be(0);

        MergeResult.Invoke(null, new object[] { target, goodSource });
        target.Year.Should().Be(1972);
    }
}
