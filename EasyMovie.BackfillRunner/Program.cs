using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyMovie.Core;
using EasyMovie.Data;
using EasyMovie.Tools.MovieApi;
using Microsoft.EntityFrameworkCore;

// 触发 AppSettings 静态构造（从 %LocalAppData%/EasyMovie/settings.json 载入 Cookie 等）
_ = AppSettings.DoubanCookie;
// 兜底：独立进程里若未自动加载 Cookie，手动灌入，贴近客户端行为
if (string.IsNullOrWhiteSpace(AppSettings.DoubanCookie))
{
    var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie", "settings.json");
    if (File.Exists(settingsPath))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (doc.RootElement.TryGetProperty("DoubanCookie", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                AppSettings.DoubanCookie = c.GetString();
        }
        catch { /* 忽略，游客模式继续 */ }
    }
}

Console.WriteLine($"Cookie: {(string.IsNullOrWhiteSpace(AppSettings.DoubanCookie) ? "未加载（游客模式）" : $"已加载 {AppSettings.DoubanCookie!.Length} 字符")}");

var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var dbDir = Path.Combine(local, "EasyMovie");
var mainDb = Path.Combine(dbDir, "EasyMovie.db");
var mainOptions = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite($"Data Source={mainDb}").Options;
var cacheOptions = CacheDbContext.CreateOptions();

if (!File.Exists(mainDb))
{
    Console.WriteLine($"SKIP: 主库不存在 {mainDb}");
    return 2;
}

// 1) 先把 cache.db 中已有评分同步到主库（处理历史“孤儿”评分：曾被一次性 flag 挡住未回写的电影）
var syncedBefore = RatingBackfill.Sync(mainOptions, cacheOptions);
Console.WriteLine($"[1/4] cache.db→主库 同步：{syncedBefore} 部（处理历史孤儿评分）");

// 2) 取 2020+ 且主库 ExternalRating 仍为空 的队列
var queue = new List<(string Title, int? Year)>();
using (var ctx = new MovieDbContext(mainOptions))
{
    queue = ctx.Movies
        .Where(m => m.ExternalRating == null && m.Year >= 2020)
        .OrderBy(m => m.Year)
        .Select(m => new { m.Title, m.Year })
        .AsEnumerable()
        .Select(x => (x.Title, (int?)x.Year))
        .ToList();
}
Console.WriteLine($"[2/4] 2020+ 待补全队列：{queue.Count} 部");
if (queue.Count == 0)
{
    Console.WriteLine("无需补全。");
    return 0;
}

// 3) 慢速补全（豆瓣）→ 写入 cache.db（尊重内置防封控守卫：限流即安全停止，绝不重试加重封禁）
var progress = new Progress<string>(s => Console.WriteLine($"      {s}"));
Console.WriteLine($"[3/4] 开始慢速补全（每部间隔 {DoubanBackfillService.BackfillGapSeconds}s，每日上限 {DoubanBackfillService.BackfillDailyCap}）…");
var report = await DoubanBackfillService.RunAsync(queue, progress, CancellationToken.None);

// 4) 再次同步：把本次新补全的 cache.db 评分回写主库
var syncedAfter = RatingBackfill.Sync(mainOptions, cacheOptions);
Console.WriteLine($"[4/4] 本次新补全回写主库：{syncedAfter} 部");

Console.WriteLine();
Console.WriteLine("=== 补全报告 ===");
Console.WriteLine($"队列总数 {report.Total} | 已补全写入 {report.Filled} | 跳过(无匹配/限流) {report.Skipped} | 触发限流停止={report.StoppedByThrottle}");
if (!string.IsNullOrEmpty(report.Error)) Console.WriteLine($"备注：{report.Error}");
var stillMissing = Math.Max(0, queue.Count - report.Filled);
Console.WriteLine($"主库仍缺 ExternalRating 的 2020+ 影片：{stillMissing} 部（限流时可下次自动化继续）");
return 0;
