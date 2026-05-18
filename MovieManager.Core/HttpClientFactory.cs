using System.Net;

namespace MovieManager.Core;

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
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        http.Timeout = TimeSpan.FromSeconds(15);
        return http;
    }

    public static HttpClient CreateForDouban()
    {
        var http = Create();
        http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml");
        http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
        return http;
    }
}
