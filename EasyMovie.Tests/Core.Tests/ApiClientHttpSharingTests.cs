using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using FluentAssertions;
using EasyMovie.Tools.MovieApi;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 锁死 API 客户端的 HttpClient 生命周期契约（详见 EasyMovie.Core.HttpClientFactory.GetOrCreate）。
///
/// 背景：每个 HttpClientHandler 独占一个连接池。此前这些客户端「每 new 一次就建一个 handler」，
/// 而 MovieListView.FetchAll 会每部影片都 new 一遍 douban/maoyan/tmdb（最多 1000 部），
/// 且它们都没有实现 IDisposable，调用方无从释放 → 连接池无法复用、句柄缓慢泄漏。
///
/// 改造为按 key 复用后，这里用测试固定三条不变量，防止后续改回去或改坏。
/// </summary>
public class ApiClientHttpSharingTests
{
    /// <summary>取客户端内部的 HttpClient。白盒，但有意为之：共享与否只能通过实例身份来断言。</summary>
    private static HttpClient InnerHttp(object client)
    {
        var field = client.GetType()
            .GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{client.GetType().Name} 未找到 _http 字段（改名后请同步更新本测试）");
        return (HttpClient)field.GetValue(client)!;
    }

    [Fact]
    public void SameConfiguration_ShouldReuseTheSameHttpClient()
    {
        var a = new DoubanApiClient();
        var b = new DoubanApiClient();
        var c = new DoubanApiClient();

        // 核心修复：不再每次 new 一个 handler/连接池
        InnerHttp(a).Should().BeSameAs(InnerHttp(b));
        InnerHttp(b).Should().BeSameAs(InnerHttp(c));
    }

    [Fact]
    public void DifferentClientTypes_ShouldEachGetTheirOwnHttpClient()
    {
        // 回归保护：缓存若退化成「单槽」，热路径按顺序构造 douban/maoyan/tmdb 会互相挤掉，
        // 导致每次都重建——比不缓存更糟。不同类型必须各自持有独立的实例。
        var douban = new DoubanApiClient();
        var maoyan = new MaoyanApiClient();
        var tmdb = new TmdbApiClient("dummy_key");
        var omdb = new OmdbApiClient("dummy_key");
        var baike = new BaiduBaikeApiClient();

        InnerHttp(douban).Should().NotBeSameAs(InnerHttp(maoyan));
        InnerHttp(maoyan).Should().NotBeSameAs(InnerHttp(tmdb));
        InnerHttp(tmdb).Should().NotBeSameAs(InnerHttp(omdb));
        InnerHttp(omdb).Should().NotBeSameAs(InnerHttp(baike));
    }

    [Fact]
    public void RepeatedConstruction_ShouldNotThrowDuplicateHeader()
    {
        // 这是「改成共享」后引入的新风险：旧实现无论是否注入都会对 HttpClient 追加默认头，
        // 一旦实例被复用，第二次 Add 会抛「无法添加重复项」。必须保证它不再发生。
        Action act = () =>
        {
            for (var i = 0; i < 5; i++)
            {
                _ = new MaoyanApiClient();
                _ = new TmdbApiClient("dummy_key");
                _ = new OmdbApiClient("dummy_key");
                _ = new BaiduBaikeApiClient();
            }
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void InjectedHttpClient_ShouldBeUsedAsIs_AndNeverMutated()
    {
        // 调用方注入的 HttpClient 归调用方所有：客户端必须原样采纳，且不得追加/修改其默认头
        // （否则「同一个 client 注入到第二个实例」会因重复 Add 而抛异常）。
        // 情形一：注入后必须原样采纳同一个实例。
        var injected = new HttpClient();
        foreach (var (name, client) in new (string, object)[]
                 {
                     ("douban", new DoubanApiClient(injected)),
                     ("maoyan", new MaoyanApiClient(injected)),
                     ("tmdb", new TmdbApiClient("dummy_key", injected)),
                     ("omdb", new OmdbApiClient("dummy_key", injected)),
                     ("baike", new BaiduBaikeApiClient(injected))
                 })
        {
            InnerHttp(client).Should().BeSameAs(injected, $"{name} 应原样采纳注入的 HttpClient");
        }

        // 情形二：注入不得污染调用方的 HttpClient（旧实现会追加默认头，
        // 导致同一个 client 注入到第二个实例时抛「重复头」异常）。
        injected.DefaultRequestHeaders.Count().Should().Be(0);
    }
}
