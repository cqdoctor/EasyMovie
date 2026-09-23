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

// 2) 取主库 ExternalRating 仍为空 的全部影片（不限年份）。
//    这样被错标为 pre-2020 的 2020+ 影片、以及 Year=1000 的损坏元数据行也能进入队列；
//    豆瓣按片名匹配（PickBestMatch 第 0~3 步与年份无关），年份仅用于第 4 步消歧，不影响命中。
var queue = new List<(string Title, int? Year)>();
using (var ctx = new MovieDbContext(mainOptions))
{
    queue = ctx.Movies
        .Where(m => m.ExternalRating == null)
        .OrderBy(m => m.Year)
        .Select(m => new { m.Title, m.Year })
        .AsEnumerable()
        .Select(x => (x.Title, (int?)x.Year))
        .ToList();
}
Console.WriteLine($"[2/4] 待补全队列（全年份）：{queue.Count} 部");

// 2.5) 离线把 cache.db 里已有 donor 按「中文关键词双向子串 + 放宽年份容差」映射到主库脏标题键，
//      无需联网即可消化大量缺口（脏标题「白象危城悍将」命中干净 donor「白象」等）。
LocalMovieCache.SeedRawKeysFromLibrary(queue, yearTolerance: 8);
var syncedKeys = RatingBackfill.Sync(mainOptions, cacheOptions);
Console.WriteLine($"[2.5/4] 离线复用 cache.db 既有 donor 回写主库：{syncedKeys} 部");
if (queue.Count == 0)
{
    Console.WriteLine("无需补全。");
    return 0;
}

// 3) 慢速补全（豆瓣）→ 写入 cache.db（尊重内置防封控守卫：限流即安全停止，绝不重试加重封禁）
var progress = new Progress<string>(s => Console.WriteLine($"      {s}"));
Console.WriteLine($"[3/4] 开始慢速补全（每部间隔 {DoubanBackfillService.BackfillGapSeconds}s，每日上限 {DoubanBackfillService.BackfillDailyCap}）…");
var report = await DoubanBackfillService.RunAsync(queue, progress, CancellationToken.None);

// 3.5) 替代源补全（TMDB / OMDb）：豆瓣无法填充的影片（豆瓣 0 分冷门片 + 脏标题无匹配）试用替代源。
//      仅对已确认豆瓣补不上的影片生效，评分写 cache.db（主库脏标题键），由下方 [4/4] Sync 回写主库。
var stillMissingAlt = new List<(int Id, string Title, int? Year)>();
using (var ctx = new MovieDbContext(mainOptions))
{
    stillMissingAlt = ctx.Movies
        .Where(m => m.ExternalRating == null)
        .OrderBy(m => m.Year)
        .Select(m => new { m.Id, m.Title, m.Year })
        .AsEnumerable()
        .Select(x => (x.Id, x.Title, (int?)x.Year))
        .ToList();
}
if (stillMissingAlt.Count > 0)
{
    Console.WriteLine($"[3.5/4] 替代源（TMDB/OMDb）补全：{stillMissingAlt.Count} 部");
    var altReport = await AlternativeRatingBackfill.RunAsync(stillMissingAlt, mainOptions, progress, CancellationToken.None);
    Console.WriteLine($"        替代源补全回写主库：{altReport.Filled} 部（TMDB {altReport.TmdbFilled} / OMDb {altReport.OmdbFilled}），跳过 {altReport.Skipped} 部，异常 {altReport.Errors} 部");
}

// 4) 再次同步：把本次新补全的 cache.db 评分回写主库
var syncedAfter = RatingBackfill.Sync(mainOptions, cacheOptions);
Console.WriteLine($"[4/4] 本次新补全回写主库：{syncedAfter} 部");

Console.WriteLine();
Console.WriteLine("=== 补全报告 ===");
Console.WriteLine($"队列总数 {report.Total} | 本次豆瓣补全写入 {report.Filled} | 跳过(无匹配/限流) {report.Skipped} | 触发限流停止={report.StoppedByThrottle}");
if (!string.IsNullOrEmpty(report.Error)) Console.WriteLine($"备注：{report.Error}");
// 以主库实际剩余缺口为准（离线复用 + 本次豆瓣补全都已计入），避免重复计数
int stillMissing;
using (var ctx = new MovieDbContext(mainOptions))
    stillMissing = ctx.Movies.Count(m => m.ExternalRating == null);
Console.WriteLine($"主库仍缺 ExternalRating 的影片：{stillMissing} 部（限流时可下次自动化继续）");
return 0;
