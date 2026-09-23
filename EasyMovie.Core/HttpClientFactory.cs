﻿using System.Net;

namespace EasyMovie.Core;

/// <summary>
/// 带代理支持的 HttpClient 工厂
/// </summary>
public static class HttpClientFactory
{
    public static HttpClient Create()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true
        };

        var proxy = AppSettings.HttpProxy;
        if (!string.IsNullOrEmpty(proxy))
        {
            // 确保 URL 有 scheme（WebProxy 需要 http:// 前缀）
            if (!proxy.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !proxy.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                proxy = "http://" + proxy;
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        http.Timeout = TimeSpan.FromSeconds(15);
        return http;
    }

    // —— 共享缓存 ——
    //
    // ⚠️ 为什么需要它：上面每个 CreateXxx() 都会 new 一个 HttpClientHandler，而**每个 handler 独占一个
    // 连接池**。各 API 客户端此前都是「每 new 一次就建一个 handler」，批量路径甚至每部影片都 new 一个
    // （MovieListView.FetchAll 最多遍历 1000 部、每部 3 个客户端），且这些客户端都没有实现 IDisposable，
    // 调用方连释放的机会都没有 → 连接池无法复用、TIME_WAIT 堆积、句柄缓慢泄漏。
    //
    // 用 key 复用后：配置不变时全场共用同一个连接池（HttpClient 的推荐用法；代价是不再感知 DNS TTL
    // 刷新，对本场景可接受）。key 里应包含所有「运行时可变的构造依据」——例如豆瓣的 Cookie 与全局代理，
    // 这样用户在设置里改了配置，key 随之变化、下一次构造立即拿到新实例，**保留「改完即生效」的语义**。
    // ⚠️ 切勿退化成无条件单例：那会把 Cookie 冻结在首次构造的值上，GUI 里更新 Cookie 后须重启才生效，
    // 而豆瓣配额对 Cookie 新鲜度敏感，属真实回归。
    // ⚠️ 必须是「按 key 的字典」而不是单槽缓存：热路径会交替构造 douban / maoyan / tmdb
    // （MovieListView 每部影片依次 new 这三个），单槽缓存会被后来的 key 挤掉，导致每次都重建，
    // 反而比不缓存更糟。key 集合很小且固定（douban/maoyan/tmdb/omdb/baike 各自的配置组合），
    // 不会无界增长。加锁而非 ConcurrentDictionary.GetOrAdd，确保每个 key 只创建一次
    // （GetOrAdd 的工厂在并发下可能被调用多次，产生孤儿 HttpClient）。
    private static readonly Dictionary<string, HttpClient> _cache = new();
    private static readonly object _cacheLock = new();

    /// <summary>
    /// 按 key 复用 HttpClient：key 相同返回同一实例；key 变化则用 <paramref name="factory"/> 重建。
    /// key 必须涵盖 factory 内部读取的全部运行时可变配置。
    /// </summary>
    public static HttpClient GetOrCreate(string key, Func<HttpClient> factory)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var created = factory();
            _cache[key] = created;
            return created;
        }
    }

    public static HttpClient CreateForDouban()
    {
        var http = Create();
        http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml");
        http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
        return http;
    }
}
