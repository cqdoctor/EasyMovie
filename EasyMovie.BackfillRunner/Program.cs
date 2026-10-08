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

// ── --dry-run：只读探测模式（供调度脚本前置判断，见 run_backfill_automation.ps1）─────────
// 只报告「有多少影片待补全、是否值得联网」，**不做任何写库、不发任何网络请求**。
// 退出码：0=有活可干  3=无活可干（全部终态/已补全）  2=环境不可用（缺库）
// 之所以做成只读：终态标记落地后，绝大多数轮次的队列为空，若照常启动会白跑进程、
// 反复打开库文件并输出噪声报告。让调度在发起前就知道「这轮不必跑」。
var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

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

if (dryRun)
{
    var pending = new List<(string Title, int? Year)>();
    using (var ctx = new MovieDbContext(mainOptions))
    {
        pending = ctx.Movies
            .Where(m => m.ExternalRating == null)
            .Select(m => new { m.Title, m.Year })
            .AsEnumerable()
            .Select(x => (x.Title, (int?)x.Year))
            .ToList();
    }

    var total = pending.Count;
    var actionable = BackfillTerminalMarks.FilterUnmarked(pending).Count;
    var markedCount = BackfillTerminalMarks.ListAll().Count;

    Console.WriteLine($"[DRY-RUN] 主库缺 ExternalRating：{total} 部");
    Console.WriteLine($"[DRY-RUN] 其中待处理（非终态）：{actionable} 部 | 终态标记：{markedCount} 部");
    if (actionable == 0)
    {
        Console.WriteLine("[DRY-RUN] 无需联网：全部为终态片或已补全，跳过本轮。");
        return 3;
    }
    Console.WriteLine($"[DRY-RUN] 有活可干：{actionable} 部待补全。");
    return 0;
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

// 2.2) 过滤「终态片」：已确认三源皆无评分的数据源天花板影片（豆瓣 rating.count=0 的冷门片、
//      或三源均命不中的脏标题片）不再进入本轮及后续每轮队列。
//      旧实现每 3h 都对同一部冷门片重新搜索，白耗豆瓣每日配额（每部间隔 12s），
//      且让「剩余待补全数」永不下降、掩盖真实进度。此处按归一化标题剔除。
var terminalTotal = BackfillTerminalMarks.ListAll().Count;
if (terminalTotal > 0)
{
    var before = queue.Count;
    queue = BackfillTerminalMarks.FilterUnmarked(queue);
    Console.WriteLine($"[2.2/4] 终态片过滤：剔除已确认无评分 {before - queue.Count} 部（累计标记 {terminalTotal} 部）");
}

// 2.5) 离线把 cache.db 里已有 donor 按「中文关键词双向子串 + 放宽年份容差」映射到主库脏标题键，
//      无需联网即可消化大量缺口（脏标题「白象危城悍将」命中干净 donor「白象」等）。
LocalMovieCache.SeedRawKeysFromLibrary(queue, yearTolerance: 8);
var syncedKeys = RatingBackfill.Sync(mainOptions, cacheOptions);
Console.WriteLine($"[2.5/4] 离线复用 cache.db 既有 donor 回写主库：{syncedKeys} 部");

// 3) 慢速补全（豆瓣）→ 写入 cache.db（尊重内置防封控守卫：限流即安全停止，绝不重试加重封禁）
//    注意：queue 为空（全部为终态片）时<b>不能</b>直接 return —— 替代源阶段（3.5）仍需走完，
//    否则替代源可能补上的片永远没有机会被尝试（终态标记只代表豆瓣无评分，不代表 TMDB/OMDb 也无）。
var progress = new Progress<string>(s => Console.WriteLine($"      {s}"));
DoubanBackfillReport report;
if (queue.Count == 0)
{
    Console.WriteLine("[3/4] 队列已无待补影片（全部为终态片），跳过豆瓣阶段");
    report = new DoubanBackfillReport { Total = 0 };
}
else
{
    Console.WriteLine($"[3/4] 开始慢速补全（每部间隔 {DoubanBackfillService.BackfillGapSeconds}s，每日上限 {DoubanBackfillService.BackfillDailyCap}）…");
    report = await DoubanBackfillService.RunAsync(queue, progress, CancellationToken.None);
}

// 3.5) 替代源补全（TMDB / OMDb）：豆瓣无法填充的影片（豆瓣 0 分冷门片 + 脏标题无匹配）试用替代源。
//      仅对已确认豆瓣补不上的影片生效，评分写 cache.db（主库脏标题键），由下方 [4/4] Sync 回写主库。
//      此处<b>只</b>剔除「三源全败」的 no_match 终态片；豆瓣 0 分的 no_rating 必须保留尝试机会
//      ——TMDB/OMDb 常能补上豆瓣没有的评分，一并剔除会永久丢掉替代源本可补全的片。
var altCandidates = new List<(int Id, string Title, int? Year)>();
using (var ctx = new MovieDbContext(mainOptions))
{
    altCandidates = ctx.Movies
        .Where(m => m.ExternalRating == null)
        .OrderBy(m => m.Year)
        .Select(m => new { m.Id, m.Title, m.Year })
        .AsEnumerable()
        .Select(x => (x.Id, x.Title, (int?)x.Year))
        .ToList();
}
// 仅剔除「三源全败」的 no_match 终态片；no_rating（豆瓣 0 分）保留，替代源往往能补上。
var stillMissingAlt = BackfillTerminalMarks.FilterByReason(
    altCandidates,
    titleSelector: x => x.Title,
    projector: x => x,
    excludeReason: BackfillTerminalMarks.ReasonNoMatch);
if (altCandidates.Count != stillMissingAlt.Count)
    Console.WriteLine($"        （已剔除全链路终态片 {altCandidates.Count - stillMissingAlt.Count} 部）");
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
// 终态片单独统计：这些是「三源确认无评分」的数据源天花板，已移出常驻队列、不再重复消耗配额。
var marks = BackfillTerminalMarks.ListAll();
var tNoRating = marks.Count(m => m.Reason == BackfillTerminalMarks.ReasonNoRating);
var tNoMatch = marks.Count(m => m.Reason == BackfillTerminalMarks.ReasonNoMatch);
if (marks.Count > 0)
    Console.WriteLine($"终态标记累计 {marks.Count} 部（豆瓣0分冷门片 {tNoRating} / 三源全败 {tNoMatch}）——后续轮次自动跳过，不再重试");
// 以主库实际剩余缺口为准（离线复用 + 本次豆瓣补全都已计入），避免重复计数
int stillMissing;
using (var ctx = new MovieDbContext(mainOptions))
    stillMissing = ctx.Movies.Count(m => m.ExternalRating == null);
Console.WriteLine($"主库仍缺 ExternalRating 的影片：{stillMissing} 部（限流时可下次自动化继续）");
return 0;
