using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Tools.MovieApi;
using FluentAssertions;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 豆瓣补全「评分有效性」契约测试（2026-09-11）。
///
/// 实测事故：一轮 21 部队列跑完，报告写着「已补全 4」，而 [4/4] 回写主库却是 0 部。
/// 根因是 DoubanBackfillService 用 <c>match.Rating.HasValue</c> 判定补全成功——
/// 而豆瓣对「条目已收录但评分人数不足/未上映」的影片会老老实实返回 <c>rating.value = 0</c>，
/// HasValue 为 true。于是 0 分被当成有效评分：报告虚高、cache.db 被写入 Rating=0 占位行，
/// 而 RatingBackfill.Sync 只搬运 &gt;0 的评分，主库一个字节都没变。
///
/// 本组测试锁定：<b>评分 &lt;= 0 的匹配一律不计入 Filled，也不得写回缓存</b>。
/// </summary>
[Collection("Douban")]
public class DoubanBackfillRatingTests
{
    private sealed class StubClient : IMovieApiClient
    {
        private readonly double? _rating;
        public int SearchCalls { get; private set; }

        public StubClient(double? rating) => _rating = rating;

        public string SourceName => "stub";

        public Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default)
        {
            SearchCalls++;
            var results = new List<MovieSearchResult>
            {
                new()
                {
                    Title = "神探坤潘3",
                    OriginalTitle = "神探坤潘3",
                    Year = 2023,
                    Rating = _rating,
                    RatingCount = _rating.HasValue && _rating.Value > 0 ? 120 : 0,
                    Director = "某某",
                    Cast = "某某",
                    PosterUrl = "https://example.com/p.jpg",
                    ExternalId = "36104109",
                }
            };
            return Task.FromResult(new MovieSearchResponse { Results = results, TotalCount = results.Count });
        }

        public Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
            => Task.FromResult<MovieSearchResult?>(null);
    }

    private static async Task<DoubanBackfillReport> RunAsync(IMovieApiClient client, List<MovieSearchResult> written)
    {
        var originalGap = DoubanBackfillService.BackfillGapSeconds;
        DoubanBackfillService.BackfillGapSeconds = 0;
        try
        {
            return await DoubanBackfillService.RunAsync(
                new List<(string, int?)> { ("神探坤潘3", 2016) },
                progress: null,
                ct: CancellationToken.None,
                clientFactory: () => client,
                writeAction: r => written.Add(r));
        }
        finally
        {
            DoubanBackfillService.BackfillGapSeconds = originalGap;
        }
    }

    [Fact]
    public async Task RatingZero_IsNotCountedAsFilled_AndNotWritten()
    {
        var written = new List<MovieSearchResult>();
        var client = new StubClient(0);

        var rep = await RunAsync(client, written);

        rep.Filled.Should().Be(0, "豆瓣 rating.value=0 表示条目暂无评分，不是补全成功");
        rep.Skipped.Should().Be(1);
        written.Should().BeEmpty("0 分行写进 cache.db 会占位并让报告虚高");
    }

    [Fact]
    public async Task RatingNull_IsNotCountedAsFilled_AndNotWritten()
    {
        var written = new List<MovieSearchResult>();
        var client = new StubClient(null);

        var rep = await RunAsync(client, written);

        rep.Filled.Should().Be(0);
        rep.Skipped.Should().Be(1);
        written.Should().BeEmpty();
    }

    // 注：这里**故意不写**「正评分应计入 Filled」的用例——RunAsync 的成功分支会先调
    // WriteBackfill → LocalMovieCache.UpsertOrMerge，直接写用户真实的 cache.db（无测试专用库可注入），
    // 用例跑一次就往生产缓存里塞一行假数据。负向用例（0 分/null 分）在写库之前就被拦下，因此安全。
    // 想补正向覆盖，需要先给 LocalMovieCache 加可注入的 DB 路径。
}
