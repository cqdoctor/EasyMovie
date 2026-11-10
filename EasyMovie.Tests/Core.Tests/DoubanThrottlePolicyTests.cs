using System.Net;
using EasyMovie.Core.Interfaces;
using EasyMovie.Tools.MovieApi;
using FluentAssertions;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 豆瓣限流策略契约测试。
///
/// 2026-11-06 起搜索主路径回到 rexxar（m.douban.com/rexxar/api/v2/search，带 Referer 可用），
/// 网页搜索（movie.douban.com/subject_search）仅在 rexxar **请求级失败**时兜底，
/// 且一旦命中配额页就停用 30 分钟。
///
/// 本组测试用桩 handler 精确控制响应，断言：
///   1) rexxar 成功 → 只发一次请求，不浪费网页搜索；
///   2) rexxar 硬封禁（302 到 /misc/sorry、验证码页）→ 立刻进冷却，不兜底；
///   3) rexxar 请求级失败（500）→ 回退网页搜索一次，且不进冷却；
///   4) rexxar 200 + items=[] → 确定无收录，不打网页搜索；
///   5) 连续 3 次软 403 → 暂停搜索 10 分钟（不是冷却），期间不再发请求并标记配额；
///   6) 网页搜索命中配额页 → 打配额标记 + 网页路径停用 30 分钟。
/// </summary>
// 冷却/软失败计数是 DoubanApiClient 的 static 状态，xunit 默认并行跑不同测试类，
// 另一用例的构造/Dispose 重置会清掉本类的计数（实测全量跑时偶发失败）。
// 与 DoubanApiClientTests 放同一集合，强制串行。
[Collection("Douban")]
public class DoubanThrottlePolicyTests : IDisposable
{
    // 一条真实结构的 window.__DATA__ 响应（字段与 2026-09-10 实测一致）
    private const string HtmlWithData =
        "<html><body><script>window.__DATA__ = {\"items\":[{" +
        "\"id\":35267208,\"title\":\"流浪地球2 (2023)\"," +
        "\"abstract\":\"中国大陆 / 科幻 / 冒险 / The Wandering Earth II / 173分钟\"," +
        "\"abstract_2\":\"郭帆 / 吴京 / 刘德华\"," +
        "\"cover_url\":\"https://img9.doubanio.com/view/photo/s_ratio_poster/public/p2916835424.jpg\"," +
        "\"rating\":{\"count\":1427722,\"value\":8.3}," +
        "\"url\":\"https://movie.douban.com/subject/35267208/\"}]};</script></body></html>";

    private const string HtmlWithoutData =
        "<html><body><script>window.__DATA__ = {\"items\":[]};</script></body></html>";

    // rexxar 搜索响应：subjects.items[].target
    private const string RexxarWithData =
        "{\"subjects\":{\"items\":[{\"target\":{\"id\":\"35267208\",\"title\":\"流浪地球2\"," +
        "\"year\":\"2023\",\"rating\":{\"value\":8.3,\"count\":1427722}," +
        "\"card_subtitle\":\"中国大陆 / 科幻 / 郭帆 / 吴京\"}}]}}";

    private const string RexxarEmpty = "{\"subjects\":{\"items\":[]}}";

    private static readonly int[] DefaultBackoff = { 20, 40, 60 };

    private static readonly string SoftThrottleBody =
        "{\"request\":\"GET /v2/search\",\"msg\":\"need_login\",\"code\":103,\"localized_message\":\"需要登录\"}";

    public DoubanThrottlePolicyTests()
    {
        DoubanApiClient.ResetThrottleState();
        // 退避阶梯置零：单测不真的等 20/40/60 秒（生产值见 DoubanApiClient.SearchBackoffSeconds）
        DoubanApiClient.SearchBackoffSeconds = new[] { 0, 0, 0 };
    }

    public void Dispose()
    {
        DoubanApiClient.ResetThrottleState();
        DoubanApiClient.SearchBackoffSeconds = DefaultBackoff;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
        private int _call;
        public List<string> Urls { get; } = new();
        public List<string> UserAgents { get; } = new();
        public List<string> Referers { get; } = new();

        public StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = _call++;
            Urls.Add(request.RequestUri?.ToString() ?? "");
            UserAgents.Add(request.Headers.TryGetValues("User-Agent", out var v) ? string.Join("", v) : "");
            Referers.Add(request.Headers.TryGetValues("Referer", out var r) ? string.Join("", r) : "");
            return Task.FromResult(_responder(request, index));
        }
    }

    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, System.Text.Encoding.UTF8, "text/html")
    };

    private static HttpResponseMessage Forbidden(string body) => new(HttpStatusCode.Forbidden)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ServerError() => new(HttpStatusCode.InternalServerError);

    private static HttpResponseMessage Redirect(string location)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Found);
        resp.Headers.TryAddWithoutValidation("Location", location);
        return resp;
    }

    [Fact]
    public async Task RexxarPrimary_ReturnsData_WithoutHtmlFallback()
    {
        var handler = new StubHandler((_, _) => Ok(RexxarWithData));
        var client = new DoubanApiClient(new HttpClient(handler));

        var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = "流浪地球2", PageSize = 5 });

        resp.Results.Should().HaveCount(1);
        resp.Results[0].Title.Should().Be("流浪地球2");
        resp.Results[0].Year.Should().Be(2023);
        resp.Results[0].Rating.Should().Be(8.3);

        // rexxar 已是主路径：成功就不能再打网页搜索（网页搜索最耗配额）
        handler.Urls.Should().HaveCount(1);
        handler.Urls[0].Should().Contain("m.douban.com/rexxar/api/v2/search");
        handler.UserAgents[0].Should().Contain("iPhone");
        // 缺 Referer 会 400 invalid_request_1284，必须带上
        handler.Referers[0].Should().Be("https://m.douban.com/");
        client.IsThrottled().Should().BeFalse();
    }

    [Fact]
    public async Task RexxarRedirectToSorry_IsHardBan_AndDoesNotFallBack()
    {
        var handler = new StubHandler((_, _) => Redirect("https://www.douban.com/misc/sorry?original-url=x"));
        var client = new DoubanApiClient(new HttpClient(handler));

        var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = "沙丘2" });

        resp.Results.Should().BeEmpty();
        handler.Urls.Should().HaveCount(1);
        client.IsThrottled().Should().BeTrue();
    }

    [Fact]
    public async Task RexxarBanPage_IsRecognizedEvenWithoutRedirect()
    {
        // 有些限流页直接 200 返回风控文案（未走 302），同样必须识别为硬封禁，且不再回退网页搜索
        var handler = new StubHandler((_, _) => Ok("<html><body>检测到有异常请求，请输入验证码</body></html>"));
        var client = new DoubanApiClient(new HttpClient(handler));

        await client.SearchAsync(new MovieSearchRequest { Keyword = "封神第一部" });

        handler.Urls.Should().HaveCount(1);
        client.IsThrottled().Should().BeTrue();
    }

    [Fact]
    public async Task RexxarRequestFailed_FallsBackToHtmlOnce()
    {
        // 第 0 次 rexxar 请求级失败（500）→ 第 1 次网页搜索兜底成功
        var handler = new StubHandler((_, i) => i == 0 ? ServerError() : Ok(HtmlWithData));
        var client = new DoubanApiClient(new HttpClient(handler));

        var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = "流浪地球2", PageSize = 5 });

        resp.Results.Should().HaveCount(1);
        handler.Urls.Should().HaveCount(2);
        handler.Urls[0].Should().Contain("m.douban.com/rexxar");
        handler.Urls[1].Should().Contain("movie.douban.com/subject_search");
        handler.UserAgents[1].Should().Contain("Windows NT");
        client.IsThrottled().Should().BeFalse();
        DoubanApiClient.LastSearchQuotaExceeded.Should().BeFalse();
    }

    [Fact]
    public async Task RexxarEmpty_IsPlainMiss_NoHtmlFallback()
    {
        // rexxar 200 + items=[]：这是「确定没收录」，不必再打网页搜索。
        var handler = new StubHandler((_, _) => Ok(RexxarEmpty));
        var client = new DoubanApiClient(new HttpClient(handler));

        // 用纯中文关键词，避免触发「剥离英文后二次搜索」的核心重试（那只影响请求次数，不影响本断言）
        var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = "不存在的片名", PageSize = 5 });

        resp.Results.Should().BeEmpty();
        handler.Urls.Should().HaveCount(1);
        handler.Urls[0].Should().Contain("m.douban.com/rexxar");
        DoubanApiClient.LastSearchQuotaExceeded.Should().BeFalse("真的没收录时不能谎报配额用尽");
        client.IsThrottled().Should().BeFalse();
    }

    [Fact]
    public async Task ConsecutiveSoft403_SuspendsSearch_ButDoesNotCooldown()
    {
        // IP 搜索门控：连续 3 次 403（退避重试后仍 403）→ 暂停搜索 10 分钟，
        // 而不是进入「硬封控冷却」（两者语义不同：暂停只针对搜索，冷却会停全部豆瓣请求）。
        var handler = new StubHandler((_, _) => Forbidden(SoftThrottleBody));
        var client = new DoubanApiClient(new HttpClient(handler));

        var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = "片名甲" });

        resp.Results.Should().BeEmpty();
        handler.Urls.Should().HaveCount(3);            // 首次 + 两次退避重试
        client.IsThrottled().Should().BeFalse();       // 软 403 不是硬封禁
        DoubanApiClient.IsSearchSuspended.Should().BeTrue();

        // 暂停期内的搜索直接短路：不再发请求，但必须标记为配额跳过（上层据此分类）
        var before = handler.Urls.Count;
        var skipped = await client.SearchAsync(new MovieSearchRequest { Keyword = "片名乙" });
        skipped.Results.Should().BeEmpty();
        handler.Urls.Should().HaveCount(before);
        DoubanApiClient.LastSearchQuotaExceeded.Should().BeTrue();
    }

    // 实测（2026-09-11）：配额耗尽时网页搜索返回的是 **HTTP 200**，且 window.__DATA__ 里
    // 只有 error_info="搜索访问太频繁。" 与空的 items 数组——既不是 403（IsSoftThrottle 抓不到），
    // 也不是硬封禁（不该冷却）。旧实现因此把它当成「豆瓣没收录这部片」，报告里全显示成
    // 「无可靠匹配」，真实原因被掩盖。
    private const string HtmlQuotaExceeded =
        "<html><body><script>window.__DATA__ = {\"total\": 0, \"start\": 0, \"count\": 15," +
        "\"error_info\": \"\\u641c\\u7d22\\u8bbf\\u95ee\\u592a\\u9891\\u7e41\\u3002\"," +
        "\"items\": [], \"text\": \"\\u795e\\u63a2\\u5764\\u6f583\"};</script></body></html>";

    [Fact]
    public async Task HtmlQuotaExceeded_FlagsQuota_AndDisablesHtmlPath()
    {
        // rexxar 请求级失败 → 网页搜索兜底 → 命中配额页：打标记、停用网页路径，但不冷却
        var handler = new StubHandler((_, i) => i switch
        {
            0 => ServerError(),
            1 => Ok(HtmlQuotaExceeded),
            _ => ServerError(),
        });
        var client = new DoubanApiClient(new HttpClient(handler));

        var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = "神探坤潘3", PageSize = 5 });

        resp.Results.Should().BeEmpty();
        handler.Urls.Should().HaveCount(2);
        client.IsThrottled().Should().BeFalse();
        DoubanApiClient.LastSearchQuotaExceeded.Should().BeTrue();
        DoubanApiClient.IsHtmlSearchDisabled.Should().BeTrue();

        // 网页路径停用期间：只打一次 rexxar，不再白白消耗网页搜索配额
        var before = handler.Urls.Count;
        await client.SearchAsync(new MovieSearchRequest { Keyword = "神探坤潘4", PageSize = 5 });
        handler.Urls.Should().HaveCount(before + 1);
        handler.Urls[^1].Should().Contain("m.douban.com/rexxar");
    }

    [Fact]
    public async Task HtmlRealResults_DoesNotFlagQuota()
    {
        var handler = new StubHandler((_, i) => i == 0 ? ServerError() : Ok(HtmlWithData));
        var client = new DoubanApiClient(new HttpClient(handler));

        var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = "The Wandering Earth II", PageSize = 5 });

        resp.Results.Should().HaveCount(1);
        DoubanApiClient.LastSearchQuotaExceeded.Should().BeFalse();
        DoubanApiClient.IsHtmlSearchDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task Detail_SoftFail_RetriesOnceThenGivesUp()
    {
        var handler = new StubHandler((_, _) => Forbidden(SoftThrottleBody));
        var client = new DoubanApiClient(new HttpClient(handler));

        var result = await client.GetDetailAsync("35267208");

        result.Should().BeNull();
        handler.Urls.Should().HaveCount(2);              // 软失败最多重试一次，不无限重试
        client.IsThrottled().Should().BeFalse();         // 单次软失败不进冷却
    }

    [Fact]
    public async Task Detail_HardBan_CooldownsImmediately()
    {
        var handler = new StubHandler((_, _) => Redirect("https://sec.douban.com/c?r=xxx"));
        var client = new DoubanApiClient(new HttpClient(handler));

        var result = await client.GetDetailAsync("35267208");

        result.Should().BeNull();
        handler.Urls.Should().HaveCount(1);              // 硬封禁不重试
        client.IsThrottled().Should().BeTrue();
    }
}
