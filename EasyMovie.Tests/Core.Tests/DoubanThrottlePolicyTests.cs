using System.Net;
using EasyMovie.Core.Interfaces;
using EasyMovie.Tools.MovieApi;
using FluentAssertions;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 豆瓣限流策略契约测试（#10）。
///
/// 锁定的核心事实：rexxar 接口会以约 40%~50% 的概率随机返回 <c>403 {"msg":"need_login","code":103}</c>，
/// 这是「软限流」而非封禁——旧实现把它当硬封禁，一次命中就冷却 60s 并逐次翻倍到 600s，
/// 实测 12 个片名只有 2 个能查到、其余 9 个被冷却跳过，补全被彻底饿死。
///
/// 本组测试用桩 handler 精确控制响应，断言：
///   1) 软 403 → 走网页搜索兜底，且**不进冷却**；
///   2) 硬封禁（302 到 /misc/sorry）→ 立刻进冷却；
///   3) rexxar 成功 → 不额外消耗网页搜索请求；
///   4) 两条路径都拿不到数据达阈值 → 才升级为冷却（防止无限空转）。
/// </summary>
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

    private static readonly string SoftThrottleBody =
        "{\"request\":\"GET /v2/search\",\"msg\":\"need_login\",\"code\":103,\"localized_message\":\"需要登录\"}";

    public DoubanThrottlePolicyTests() => DoubanApiClient.ResetThrottleState();

    public void Dispose() => DoubanApiClient.ResetThrottleState();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
        private int _call;
        public List<string> Urls { get; } = new();
        public List<string> UserAgents { get; } = new();

        public StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = _call++;
            Urls.Add(request.RequestUri?.ToString() ?? "");
            UserAgents.Add(request.Headers.TryGetValues("User-Agent", out var v) ? string.Join("", v) : "");
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

    private static HttpResponseMessage Redirect(string location)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Found);
        resp.Headers.TryAddWithoutValidation("Location", location);
        return resp;
    }

    [Fact]
    public async Task SoftFourThree_FallsBackToHtmlSearch_AndDoesNotCooldown()
    {
        var handler = new StubHandler((_, i) => i == 0 ? Forbidden(SoftThrottleBody) : Ok(HtmlWithData));
        var client = new DoubanApiClient(new HttpClient(handler));

        var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = "流浪地球2", PageSize = 5 });

        // 兜底路径必须真的被走到，且拿到数据
        resp.Results.Should().HaveCount(1);
        resp.Results[0].Title.Should().Be("流浪地球2");
        resp.Results[0].Year.Should().Be(2023);
        resp.Results[0].Rating.Should().Be(8.3);
        resp.Results[0].Director.Should().Be("郭帆");

        // 第二条请求必须打到网页搜索页，并使用桌面端 UA（rexxar 用的是移动端 UA）
        handler.Urls.Should().HaveCount(2);
        handler.Urls[1].Should().Contain("movie.douban.com/subject_search");
        handler.UserAgents[1].Should().Contain("Windows NT");

        // 关键：软限流绝不能触发冷却，否则后续补全会被饿死
        client.IsThrottled().Should().BeFalse();
    }

    [Fact]
    public async Task SoftFourThreeThenHtmlBan_DoesCooldown()
    {
        var handler = new StubHandler((_, i) => i == 0
            ? Forbidden(SoftThrottleBody)
            : Redirect("https://www.douban.com/misc/sorry?original-url=x"));
        var client = new DoubanApiClient(new HttpClient(handler));

        var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = "沙丘2" });

        resp.Results.Should().BeEmpty();
        client.IsThrottled().Should().BeTrue();   // 网页搜索被重定向到限流页 → 硬封禁
    }

    [Fact]
    public async Task HtmlBanPage_IsRecognizedEvenWithoutRedirect()
    {
        // 有些限流页直接 200 返回风控文案（未走 302），同样必须识别为硬封禁
        var handler = new StubHandler((_, i) => i == 0
            ? Forbidden(SoftThrottleBody)
            : Ok("<html><body>检测到有异常请求，请输入验证码</body></html>"));
        var client = new DoubanApiClient(new HttpClient(handler));

        await client.SearchAsync(new MovieSearchRequest { Keyword = "封神第一部" });

        client.IsThrottled().Should().BeTrue();
    }

    [Fact]
    public async Task RexxarSuccess_DoesNotSpendHtmlRequest()
    {
        const string rexxarJson =
            "{\"subjects\":{\"items\":[{\"target\":{\"id\":\"35267208\",\"title\":\"流浪地球2\"," +
            "\"year\":\"2023\",\"rating\":{\"value\":8.3,\"count\":1427722}," +
            "\"card_subtitle\":\"中国大陆 / 科幻 / 郭帆 / 吴京\"}}]}}";
        var handler = new StubHandler((_, _) => Ok(rexxarJson));
        var client = new DoubanApiClient(new HttpClient(handler));

        var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = "流浪地球2", PageSize = 5 });

        resp.Results.Should().HaveCount(1);
        handler.Urls.Should().HaveCount(1);                                  // 成功就别再浪费网页搜索的配额
        handler.Urls[0].Should().Contain("m.douban.com/rexxar");
        handler.UserAgents[0].Should().Contain("iPhone");                    // rexxar 走移动端 UA
        client.IsThrottled().Should().BeFalse();
    }

    [Fact]
    public async Task RepeatedSoftFailures_EventuallyCooldown()
    {
        // 两条路径都拿不到数据（无硬封禁信号）达到阈值后，必须升级为冷却，避免无限空转
        var handler = new StubHandler((_, i) => i % 2 == 0 ? Forbidden(SoftThrottleBody) : Ok(HtmlWithoutData));
        var client = new DoubanApiClient(new HttpClient(handler));

        for (var i = 0; i < 6; i++)
        {
            var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = $"片名{i}" });
            resp.Results.Should().BeEmpty();
            if (i < 5) client.IsThrottled().Should().BeFalse($"第 {i + 1} 次软失败不应立刻冷却");
        }

        client.IsThrottled().Should().BeTrue("连续软失败达阈值后应升级为冷却");
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
