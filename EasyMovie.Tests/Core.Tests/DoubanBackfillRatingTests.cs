using System;
using System.Collections.Generic;
using System.IO;
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
public class DoubanBackfillRatingTests : IDisposable
{
    /// <summary>
    /// 隔离「补全终态标记」的存储路径（2026-10-08 补）。
    ///
    /// <para><b>为什么必须隔离</b>：豆瓣 rating=0 分支现在会调用
    /// <c>BackfillTerminalMarks.Mark(...)</c> 写终态标记。而主库中<b>真实存在</b>片名含
    /// 「神探坤潘」的影片，一旦本组用例把测试标记写进生产 cache.db，该真实影片就会被
    /// 永久移出补全队列（实测已发生：生产库曾出现 HitCount=4 的测试标记行）。
    /// 标记表是纯元数据、体积小，故允许用可注入连接串把本组测试隔离到临时库。</para>
    /// </summary>
    private readonly string _markDbPath =
        Path.Combine(Path.GetTempPath(), $"em_rating_{Guid.NewGuid():N}.db");
    private readonly string? _origMarkConn;

    public DoubanBackfillRatingTests()
    {
        _origMarkConn = BackfillTerminalMarks.TestConnectionString;
        BackfillTerminalMarks.TestConnectionString = $"Data Source={_markDbPath}";
    }

    public void Dispose()
    {
        BackfillTerminalMarks.TestConnectionString = _origMarkConn;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { var f = _markDbPath + suffix; if (File.Exists(f)) File.Delete(f); }
            catch { /* 清理失败不影响断言 */ }
        }
    }

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
    //
    // 2026-10-08 补：负向用例现已会写「补全终态标记」（BackfillTerminalMarks.Mark），
    // 故构造函数必须把标记库指向临时库，否则主库里真实存在的「神探坤潘」会被测试标记
    // 永久移出补全队列。缓存层（LocalMovieCache）的写入隔离仍待 LocalMovieCache 支持注入后处理。
}
