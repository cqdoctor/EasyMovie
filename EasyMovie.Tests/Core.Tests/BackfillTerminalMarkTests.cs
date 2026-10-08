using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Tools.MovieApi;
using FluentAssertions;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 补全「终态标记」契约测试（2026-10-07）。
///
/// <para><b>要解决的实测问题</b>：豆瓣对「条目已收录但 rating.count=0」的冷门片
/// （未上映 / 评分人数不足）永远返回 0 分，TMDB API 在本机又全线 502。
/// 旧实现把这类「数据源天花板」与「暂无限流」混为一谈，导致每 3h 调度都对同一部冷门片
/// 重新搜索（每部间隔 12s），白耗豆瓣每日配额，且「剩余待补全数」永不下降、掩盖真实进度。</para>
///
/// <para><b>本组锁定的三件事</b>：
/// ① rating=0 → 必须标记终态（数据源天花板，重试无用）；
/// ② <b>限流 / 网络异常 → 绝不能标记终态</b>（临时故障不该把影片永久踢出队列，这是最危险的退化）；
/// ③ 标记跨轮次生效，且「豆瓣 0 分」与「三源全败」两类在替代源阶段的过滤策略必须不同
/// （前者 TMDB/OMDb 常有分，必须留机会）。</para>
/// </summary>
[Collection("Douban")]
public class BackfillTerminalMarkTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string? _originalConn;

    public BackfillTerminalMarkTests()
    {
        // 每个用例一份独立临时库，杜绝污染用户真实 cache.db
        _dbPath = Path.Combine(Path.GetTempPath(), $"em_term_{Guid.NewGuid():N}.db");
        _originalConn = BackfillTerminalMarks.TestConnectionString;
        BackfillTerminalMarks.TestConnectionString = $"Data Source={_dbPath}";
    }

    public void Dispose()
    {
        BackfillTerminalMarks.TestConnectionString = _originalConn;
        SqliteCleanup(_dbPath);
        SqliteCleanup(_dbPath + "-wal");
        SqliteCleanup(_dbPath + "-shm");
    }

    private static void SqliteCleanup(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* 清理失败不影响断言 */ }
    }

    /// <summary>搜索结果可控的假客户端：可模拟 0 分条目、无候选、抛异常。</summary>
    private sealed class StubClient : IMovieApiClient
    {
        private readonly List<MovieSearchResult> _results;
        private readonly bool _throw;
        public int SearchCalls { get; private set; }

        public StubClient(double? rating, bool noResults = false, bool throwOnSearch = false)
        {
            _results = new List<MovieSearchResult>();
            _throw = throwOnSearch;
            if (!noResults)
                _results.Add(new MovieSearchResult
                {
                    Title = "神探坤潘3",
                    OriginalTitle = "神探坤潘3",
                    Year = 2023,
                    Rating = rating,
                    RatingCount = rating > 0 ? 120 : 0,
                    Director = "某某",
                    Cast = "某某",
                    PosterUrl = "https://example.com/p.jpg",
                    ExternalId = "36104109",
                });
        }

        public string SourceName => "stub";

        public Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default)
        {
            SearchCalls++;
            if (_throw) throw new HttpRequestException("模拟网络异常");
            return Task.FromResult(new MovieSearchResponse { Results = _results, TotalCount = _results.Count });
        }

        public Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
            => Task.FromResult<MovieSearchResult?>(null);
    }

    private static async Task<DoubanBackfillReport> RunAsync(IMovieApiClient client, string title = "神探坤潘3")
    {
        var originalGap = DoubanBackfillService.BackfillGapSeconds;
        DoubanBackfillService.BackfillGapSeconds = 0;
        try
        {
            return await DoubanBackfillService.RunAsync(
                new List<(string, int?)> { (title, 2023) },
                progress: null,
                ct: CancellationToken.None,
                clientFactory: () => client,
                writeAction: _ => { });
        }
        finally { DoubanBackfillService.BackfillGapSeconds = originalGap; }
    }

    // ── ① 数据源天花板 → 必须标记终态 ────────────────────────────────

    [Fact]
    public async Task RatingZero_IsMarkedTerminal_AndCountedSeparately()
    {
        var rep = await RunAsync(new StubClient(0));

        rep.Filled.Should().Be(0);
        rep.TerminalNoRating.Should().Be(1, "rating=0 是数据源天花板，须与「限流跳过」区分统计");
        rep.Skipped.Should().Be(1);

        BackfillTerminalMarks.IsMarked("神探坤潘3").Should().BeTrue();
        var marks = BackfillTerminalMarks.ListAll();
        marks.Should().ContainSingle();
        marks[0].Reason.Should().Be(BackfillTerminalMarks.ReasonNoRating);
        marks[0].ConfirmedSources.Should().Contain("douban");
    }

    [Fact]
    public async Task RatingNull_IsAlsoMarkedTerminal()
    {
        var rep = await RunAsync(new StubClient(null));

        rep.TerminalNoRating.Should().Be(1);
        BackfillTerminalMarks.IsMarked("神探坤潘3").Should().BeTrue();
    }

    // ── ② 最危险的退化：临时故障绝不能写成终态 ──────────────────────

    [Fact]
    public async Task SearchException_IsNotMarkedTerminal()
    {
        var rep = await RunAsync(new StubClient(0, throwOnSearch: true));

        BackfillTerminalMarks.IsMarked("神探坤潘3").Should().BeFalse(
            "网络异常是临时故障，写终态会把影片永久踢出队列——下次数据源恢复也补不回来");
        BackfillTerminalMarks.ListAll().Should().BeEmpty();
        rep.TerminalNoRating.Should().Be(0);
    }

    [Fact]
    public async Task NoSearchResults_IsNotMarkedTerminal_DoubanStage()
    {
        // 豆瓣搜不到时无法区分「真没收录」与「概率性 need_login 假阴性」，
        // 必须等替代源也确认失败后才由 AlternativeRatingBackfill 标记。
        var rep = await RunAsync(new StubClient(0, noResults: true));

        BackfillTerminalMarks.IsMarked("神探坤潘3").Should().BeFalse(
            "豆瓣阶段搜不到≠终态；提前标记会把替代源本可补全的片永久排除");
        rep.TerminalNoMatch.Should().Be(0);
    }

    [Fact]
    public async Task ThrottledClient_StopsRun_WithoutMarkingTerminal()
    {
        var rep = await RunAsync(new ThrottlingClient());

        rep.StoppedByThrottle.Should().BeTrue();
        BackfillTerminalMarks.ListAll().Should().BeEmpty(
            "被限流是窗口级临时状态，绝不可写成终态");
    }

    private sealed class ThrottlingClient : IMovieApiClient
    {
        public string SourceName => "throttle";
        public Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default)
            => Task.FromResult(new MovieSearchResponse { Results = new List<MovieSearchResult>() });
        public Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default)
            => Task.FromResult<MovieSearchResult?>(null);
        public bool IsThrottled() => true;
    }

    // ── ③ 跨轮次复用与两类原因的差异化过滤 ────────────────────────────

    [Fact]
    public void Mark_IsIdempotent_AccumulatesHitCount_AndKeepsStrongerReason()
    {
        BackfillTerminalMarks.Mark("神探坤潘3", 2023, BackfillTerminalMarks.ReasonNoRating, "douban");
        BackfillTerminalMarks.Mark("神探坤潘3", 2023, BackfillTerminalMarks.ReasonNoRating, "douban");
        BackfillTerminalMarks.Mark("神探坤潘3", 2023, BackfillTerminalMarks.ReasonNoMatch, "tmdb,omdb");

        var marks = BackfillTerminalMarks.ListAll();
        marks.Should().ContainSingle("重复标记不应插入新行");
        marks[0].HitCount.Should().Be(3);
        marks[0].Reason.Should().Be(BackfillTerminalMarks.ReasonNoRating,
            "no_rating（源明确有条目）比 no_match（可能只是没搜到）更确定，不能被弱原因覆盖");
        marks[0].ConfirmedSources.Should().Contain("tmdb").And.Contain("omdb");
    }

    [Fact]
    public void FilterUnmarked_RemovesMarked_KeepsOthers()
    {
        BackfillTerminalMarks.Mark("冷门片A", 2023, BackfillTerminalMarks.ReasonNoRating, "douban");

        var kept = BackfillTerminalMarks.FilterUnmarked(new List<(string, int?)>
        {
            ("冷门片A", 2023),
            ("正常片B", 2023),
        });

        kept.Should().ContainSingle().Which.Title.Should().Be("正常片B");
    }

    [Fact]
    public void FilterUnmatched_MatchesByCleanedKey_IgnoringReleaseTags()
    {
        // 复现实测库的脏标题形态：标记由清洗后的标题写入，队列里是带 EAC3 的完整脏标题。
        // 若匹配键只用「去标点+小写」，两者键为「牡丹令peonyorder」vs「牡丹令peonyordeac3」，
        // 永不相等 → 标记写了不起作用（这正是首版实现的真实缺陷，本用例锁死修复）。
        BackfillTerminalMarks.Mark("牡丹令 Peony Order", 2024, BackfillTerminalMarks.ReasonNoMatch, "douban,tmdb,omdb");

        var kept = BackfillTerminalMarks.FilterUnmarked(new List<(string, int?)>
        {
            ("牡丹令Peony Order EAC3", 2024),   // 实测库原题，带音轨标签
            ("牡丹令 Peony Order", 2024),        // 已清洗
            ("别的片", 2024),
        });

        kept.Should().ContainSingle("两种标题形态都应命中同一条标记").Which.Title.Should().Be("别的片");
    }

    [Fact]
    public void MarkKey_StripsYearAndTags_SoBothFormsCollapse()
    {
        // 直接钉住 MarkKey 的语义：年份与发布标签不参与键，避免同类片互相误判
        BackfillTerminalMarks.MarkKey("诡摇铃 Strange Bell Tolls EAC3")
            .Should().Be(BackfillTerminalMarks.MarkKey("诡摇铃 Strange Bell Tolls"));
        BackfillTerminalMarks.MarkKey("功夫雄狮Kung Fu Lion (2023)")
            .Should().Be(BackfillTerminalMarks.MarkKey("功夫雄狮Kung Fu Lion"));
    }

    [Fact]
    public void FilterByReason_OnlyExcludesNoMatch_KeepsNoRatingForAlternativeSources()
    {
        // 关键语义：豆瓣 0 分 ≠ 全链路终态。TMDB/OMDb 常能补上豆瓣没有的评分，
        // 若一并剔除会永久丢掉替代源的机会。
        BackfillTerminalMarks.Mark("豆瓣0分片", 2023, BackfillTerminalMarks.ReasonNoRating, "douban");
        BackfillTerminalMarks.Mark("三源全败片", 2023, BackfillTerminalMarks.ReasonNoMatch, "douban,tmdb,omdb");

        var kept = BackfillTerminalMarks.FilterByReason(
            new List<(int Id, string Title, int? Year)>
            {
                (1, "豆瓣0分片", 2023),
                (2, "三源全败片", 2023),
            },
            titleSelector: x => x.Title,
            projector: x => x,
            excludeReason: BackfillTerminalMarks.ReasonNoMatch);

        kept.Should().ContainSingle().Which.Title.Should().Be("豆瓣0分片");
    }

    [Fact]
    public void Clear_ReopensMovieForBackfill()
    {
        BackfillTerminalMarks.Mark("冷门片A", 2023, BackfillTerminalMarks.ReasonNoMatch, "douban");
        BackfillTerminalMarks.IsMarked("冷门片A").Should().BeTrue();

        BackfillTerminalMarks.Clear("冷门片A").Should().BeTrue();

        BackfillTerminalMarks.IsMarked("冷门片A").Should().BeFalse("新增数据源后应能重新入队");
        BackfillTerminalMarks.Clear("冷门片A").Should().BeFalse("重复清除应返回 false 而非抛异常");
    }

    [Fact]
    public void FilterUnmarked_WhenStoreBroken_StillReturnsQueue()
    {
        // 标记层是优化而非正确性依赖：存储异常时必须放行全队列，绝不能让补全主流程崩掉。
        BackfillTerminalMarks.TestConnectionString = "Data Source=/nonexistent-dir-xyz/does-not-exist.db";
        var queue = new List<(string, int?)> { ("任意片", 2023) };

        var kept = BackfillTerminalMarks.FilterUnmarked(queue);

        kept.Should().HaveCount(1, "存储不可用时降级为放行，不得吞掉队列");
    }

    [Fact]
    public void Mark_WithProductionPath_InsideTestProcess_Throws()
    {
        // 防回归：2026-10-08 实测事故——DoubanBackfillRatingTests 未设隔离，
        // 把测试标记写进了生产 cache.db，导致主库真实存在的「神探坤潘」被永久移出补全队列。
        // 本用例锁定「测试进程内写生产库必须当场抛错」，让污染变成红灯而非静默落库。
        BackfillTerminalMarks.TestConnectionString = null;

        var act = () => BackfillTerminalMarks.Mark("神探坤潘3", 2016, BackfillTerminalMarks.ReasonNoRating, "douban");

        act.Should().Throw<InvalidOperationException>("测试进程写生产库必须当场失败，不能静默污染");
    }

    [Fact]
    public void AltSourcesExhausted_OnlyTrue_WhenBothAlternativeSourcesRecorded()
    {
        // 只被豆瓣确认过 -> 替代源仍有机会，不能算耗尽
        BackfillTerminalMarks.Mark("冷门片A", 2023, BackfillTerminalMarks.ReasonNoRating, "douban");
        BackfillTerminalMarks.AltSourcesExhausted("冷门片A").Should().BeFalse(
            "豆瓣 0 分不代表 TMDB/OMDb 也无分，替代源必须保留尝试机会");

        // 替代源也确认失败 -> 全链路耗尽
        BackfillTerminalMarks.Mark("冷门片A", 2023, BackfillTerminalMarks.ReasonNoMatch, "douban,tmdb,omdb");
        BackfillTerminalMarks.AltSourcesExhausted("冷门片A").Should().BeTrue(
            "三源全败后再无新信息可得，继续重试只是徒增延迟与配额消耗");
    }

    [Fact]
    public void AltSourcesExhausted_RequiresBothTmdbAndOmdb_NotJustOne()
    {
        // 只配了 OMDb key 的部署：tmdb 缺失不等于「已试过并失败」，不能误判为耗尽
        BackfillTerminalMarks.Mark("冷门片B", 2023, BackfillTerminalMarks.ReasonNoRating, "douban,omdb");
        BackfillTerminalMarks.AltSourcesExhausted("冷门片B").Should().BeFalse(
            "未配置 TMDB key 时该源本就不可试，不能算作已确认无评分");
    }

    [Fact]
    public void AltSourcesExhausted_UnmarkedMovie_ReturnsFalse()
    {
        BackfillTerminalMarks.AltSourcesExhausted("从未标记的片").Should().BeFalse();
    }
}
