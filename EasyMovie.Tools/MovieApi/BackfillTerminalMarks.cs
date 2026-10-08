using System;
using System.Collections.Generic;
using System.Linq;
using EasyMovie.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Tools.MovieApi;

/// <summary>
/// 补全终态标记的读写访问层（存于 cache.db）。
///
/// <para><b>解决的问题</b>：豆瓣「有条目但评分人数=0」的冷门片、以及三源均命不中的脏标题片，
/// 是<b>数据源天花板</b>——重试再多次也不会产生评分。旧实现把这类跳过与「暂无限流」混同，
/// 导致每轮调度（默认每 3h）重复搜索同一部片：白耗豆瓣每日配额、真实进度被掩盖、
/// 「剩余待补全数」永不下降。本类让这类影片<b>一次确认、永久移出常驻队列</b>。</para>
///
/// <para><b>安全边界（重要）</b>：仅在确认「源本身无此数据」时调用 <see cref="Mark"/>；
/// 网络异常、被限流、解析失败一律不写。这样临时故障不会把影片永久踢出队列。
/// 需要让影片重新入队时（例如新增了数据源），调用 <see cref="Clear"/>。</para>
/// </summary>
public static class BackfillTerminalMarks
{
    /// <summary>终态原因：源有条目，但评分人数为 0 / 未上映 / 冷门。</summary>
    public const string ReasonNoRating = "source_no_rating";

    /// <summary>终态原因：源与全部替代源均搜不到该片（脏标题或源未收录）。</summary>
    public const string ReasonNoMatch = "source_no_match";

    /// <summary>
    /// 测试隔离用：非 null 时改用该 SQLite 连接串（指向临时库），而非生产的 cache.db。
    /// 生产路径恒为 null。<para>
    /// 之所以单独开这个口子：<see cref="LocalMovieCache"/> 的库路径是硬编码的
    /// （<c>Environment.SpecialFolder.LocalApplicationData</c>），无法注入，
    /// 导致既有补全测试只能用「负向用例在写库前被拦下」的方式规避污染——
    /// 正向覆盖因此长期缺失（见 DoubanBackfillRatingTests 末尾注释）。
    /// 终态标记是<b>纯标记数据</b>（无海报等大 BLOB），单独允许注入路径即可让本层测试完全隔离。</para>
    /// </summary>
    public static string? TestConnectionString { get; set; }

    /// <summary>
    /// 终态标记的<b>专用匹配键</b>：先剥掉编码/音轨/年份等噪声标签（复用
    /// <see cref="DoubanApiClient.CleanSearchTitle"/>，与搜索侧同一套规则），再归一化。
    ///
    /// <para><b>为什么不直接用 <see cref="LocalMovieCache.NormalizeKey"/></b>：主库脏标题普遍带
    /// 发布标签，如「牡丹令Peony Order EAC3」与干净标题「牡丹令 Peony Order」——纯归一化只去标点，
    /// 两者的键分别是「牡丹令peonyordeac3」与「牡丹令peonyorder」，<b>不相等</b>，
    /// 导致标记写入后永远命中不了，等于功能失效。这正是实测库里 9 部缺评分片的真实形态
    /// （多数带 EAC3）。</para>
    /// </summary>
    public static string MarkKey(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var cleaned = DoubanApiClient.CleanSearchTitle(title);
        return LocalMovieCache.NormalizeKey(cleaned);
    }

    private static CacheDbContext CreateContext()
        => TestConnectionString != null
            ? new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>()
                .UseSqlite(TestConnectionString)
                .AddInterceptors(BusyTimeoutInterceptor.Instance)
                .Options)
            : CacheDbContext.Create();

    /// <summary>确保标记表存在。幂等，且能补建历史 cache.db 缺失的表（见 EnsureTable）。</summary>
    public static void EnsureReady()
    {
        if (TestConnectionString == null) LocalMovieCache.EnsureReady();
        EnsureTable();
    }

    /// <summary>
    /// 显式确保 <c>BackfillTerminalMarks</c> 表与唯一索引存在。
    ///
    /// <para><b>为什么必须显式做</b>：<see cref="LocalMovieCache.EnsureReady"/> 走
    /// <c>Database.EnsureCreated()</c>，而 EnsureCreated <b>只在库文件不存在时</b>才建表——
    /// 对已存在的 cache.db 补建新表<b>无效</b>（这正是 cache.db 已有 724 行却无新表的原因）。
    /// 若依赖它，老用户的库永远拿不到标记表，本功能会静默失效。</para>
    /// </summary>
    private static void EnsureTable()
    {
        using var ctx = CreateContext();
        // 借一次空查询触发 EF 的表/列映射校验：缺表时抛 SqliteException，据此补建。
        try
        {
            _ = ctx.BackfillTerminalMarks.AsNoTracking().Take(1).ToList();
            return;
        }
        catch (SqliteException) { /* 缺表 → 下面补建 */ }
        catch (InvalidOperationException) { /* 测试库的 EF 模型未就绪 → 继续建表 */ }

        // CREATE TABLE IF NOT EXISTS：幂等，WAL + busy_timeout 已由 DbContext 配置，
        // 与主进程 / BackfillRunner 并发安全。
        ctx.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "BackfillTerminalMarks" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_BackfillTerminalMarks" PRIMARY KEY AUTOINCREMENT,
                "NormTitle" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Year" INTEGER NULL,
                "Reason" TEXT NOT NULL,
                "ConfirmedSources" TEXT NULL,
                "MarkedAtUtc" TEXT NOT NULL,
                "LastCheckedUtc" TEXT NOT NULL,
                "HitCount" INTEGER NOT NULL
            );
            """);
        // 唯一索引：同一归一化标题只保留一条标记（重复写入走 UPDATE 分支）。
        ctx.Database.ExecuteSqlRaw(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_BackfillTerminalMarks_NormTitle" ON "BackfillTerminalMarks" ("NormTitle");""");

        // 若 DbContext 在本进程内缓存了旧的空模型，刷新一次以免后续查询仍指向缺表状态。
        ctx.Database.OpenConnection();
        ctx.Database.CloseConnection();
    }

    /// <summary>
    /// 查询某片是否已被标记为终态（键空间与 CachedMovies.NormTitle 同规则）。
    /// </summary>
    public static bool IsMarked(string title)
    {
        var key = MarkKey(title);
        if (string.IsNullOrEmpty(key)) return false;
        try
        {
            EnsureReady();
            using var ctx = CreateContext();
            return ctx.BackfillTerminalMarks.AsNoTracking().Any(m => m.NormTitle == key);
        }
        catch (Exception ex)
        {
            // 标记层是优化而非正确性依赖：任何异常都必须降级为「未标记」，绝不能拖垮补全主流程。
            Serilog.Log.Debug(ex, "终态标记查询失败，降级为未标记：{Title}", title);
            return false;
        }
    }

    /// <summary>批量过滤：返回队列中<b>尚未被标记终态</b>的影片。</summary>
    public static List<(string Title, int? Year)> FilterUnmarked(IEnumerable<(string Title, int? Year)> queue)
    {
        var all = queue as List<(string, int?)> ?? queue.ToList();
        if (all.Count == 0) return all;
        try
        {
            EnsureReady();
            using var ctx = CreateContext();
            var marked = ctx.BackfillTerminalMarks.AsNoTracking()
                .Select(m => m.NormTitle).ToHashSet();
            return all.Where(x => !marked.Contains(MarkKey(x.Item1))).ToList();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "终态标记批量过滤失败，放行全部队列");
            return all;
        }
    }

    /// <summary>
    /// 按终态原因过滤：只剔除<b>指定原因</b>的标记片，其余放行。
    ///
    /// <para><b>用途（关键）</b>：豆瓣阶段与替代源阶段的过滤策略必须不同。
    /// <c>source_no_rating</c>（豆瓣有条目但评分人数=0）只代表豆瓣没分，<b>TMDB/OMDb 往往有分</b>，
    /// 所以替代源阶段<b>必须</b>继续尝试这类片；若一并剔除，会永久丢掉替代源本可补全的机会。
    /// 只有 <c>source_no_match</c>（豆瓣 + 替代源全部确认命不中）才是全链路终态，可安全排除。</para>
    /// </summary>
    public static List<(int Id, string Title, int? Year)> FilterByReason<TQueue>(
        IEnumerable<TQueue> queue, Func<TQueue, string> titleSelector,
        Func<TQueue, (int, string, int?)> projector, string excludeReason)
    {
        var all = queue as List<TQueue> ?? queue.ToList();
        if (all.Count == 0) return new List<(int, string, int?)>();
        try
        {
            EnsureReady();
            using var ctx = CreateContext();
            var excluded = ctx.BackfillTerminalMarks.AsNoTracking()
                .Where(m => m.Reason == excludeReason)
                .Select(m => m.NormTitle).ToHashSet();
            return all.Where(x => !excluded.Contains(MarkKey(titleSelector(x))))
                      .Select(projector).ToList();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "终态标记按原因过滤失败，放行全部队列");
            return all.Select(projector).ToList();
        }
    }

    /// <summary>
    /// 防测试污染守卫：单元测试若忘设 <see cref="TestConnectionString"/> 就写生产 cache.db，
    /// 会把真实影片永久移出补全队列（实测：主库真实存在的「神探坤潘」被测试标记污染过）。
    /// 这里<b>让异常直接抛出</b>，使污染在测试当场暴露为红灯，而非静默落库。
    /// 仅在进程以 testhost/vstest 运行且未隔离时触发，生产进程不受影响。
    /// </summary>
    private static void GuardAgainstTestPollution(string title)
    {
        if (TestConnectionString != null) return;
        var proc = Environment.ProcessPath ?? "";
        var isTestHost = proc.Contains("testhost", StringComparison.OrdinalIgnoreCase)
                      || proc.Contains("vstest", StringComparison.OrdinalIgnoreCase)
                      || proc.Contains("nunit", StringComparison.OrdinalIgnoreCase);
        if (!isTestHost) return;
        throw new InvalidOperationException(
            $"测试进程试图写入生产 cache.db 的终态标记（片名：{title}）。" +
            "测试必须先设置 BackfillTerminalMarks.TestConnectionString 指向临时库。");
    }

    /// <summary>
    /// 标记某片为终态（幂等：已存在则累加 HitCount 并刷新复核时间，不重复插入）。
    /// </summary>
    public static void Mark(string title, int? year, string reason, string? sources = null)
    {
        var key = MarkKey(title);
        if (string.IsNullOrEmpty(key)) return;
        GuardAgainstTestPollution(title);
        try
        {
            EnsureReady();
            using var ctx = CreateContext();
            var now = DateTime.UtcNow;
            var existing = ctx.BackfillTerminalMarks.FirstOrDefault(m => m.NormTitle == key);
            if (existing != null)
            {
                existing.HitCount++;
                existing.LastCheckedUtc = now;
                // Reason 优先级：no_rating（源明确有条目无评分）比 no_match（可能只是没搜到）更「确定」，
                // 不用更弱的 no_match 覆盖更强的 no_rating。
                if (existing.Reason != ReasonNoRating && reason == ReasonNoRating)
                    existing.Reason = reason;
                // 必须接住返回值：MergeSources 返回合并后的新串，不赋值等于没做（曾漏赋值导致
                // 后续追加的来源（如 tmdb/omdb）永远写不进去）。
                existing.ConfirmedSources = MergeSources(existing.ConfirmedSources, sources);
            }
            else
            {
                ctx.BackfillTerminalMarks.Add(new BackfillTerminalMark
                {
                    NormTitle = key,
                    Title = title,
                    Year = year,
                    Reason = reason,
                    ConfirmedSources = sources,
                    MarkedAtUtc = now,
                    LastCheckedUtc = now,
                    HitCount = 1,
                });
            }
            ctx.SaveChanges();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "写入终态标记失败（忽略，不影响补全）：{Title}", title);
        }
    }

    /// <summary>清除某片的终态标记，使其重新进入补全队列（新增数据源后使用）。</summary>
    /// <summary>
    /// 判断某片的替代源机会是否已耗尽：已标记终态，且 <see cref="BackfillTerminalMark.ConfirmedSources"/>
    /// 里同时记录过 tmdb 与 omdb（即豆瓣 + 两个替代源全部确认无评分）。
    ///
    /// <para>用于替代源阶段的短路：豆瓣 rating=0 只说明豆瓣没分，替代源仍应尝试；
    /// 但一旦替代源也试过并失败，就再无新信息可得，继续重试只是徒增延迟与配额消耗。</para>
    /// </summary>
    public static bool AltSourcesExhausted(string title)
    {
        var key = MarkKey(title);
        if (string.IsNullOrEmpty(key)) return false;
        try
        {
            EnsureReady();
            using var ctx = CreateContext();
            var sources = ctx.BackfillTerminalMarks.AsNoTracking()
                .Where(m => m.NormTitle == key)
                .Select(m => m.ConfirmedSources)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(sources)) return false;
            return sources.Contains("tmdb", StringComparison.OrdinalIgnoreCase)
                && sources.Contains("omdb", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "替代源耗尽判定失败，按未耗尽处理：{Title}", title);
            return false;
        }
    }

    public static bool Clear(string title)
    {
        var key = MarkKey(title);
        if (string.IsNullOrEmpty(key)) return false;
        try
        {
            EnsureReady();
            using var ctx = CreateContext();
            var existing = ctx.BackfillTerminalMarks.FirstOrDefault(m => m.NormTitle == key);
            if (existing == null) return false;
            ctx.BackfillTerminalMarks.Remove(existing);
            ctx.SaveChanges();
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "清除终态标记失败：{Title}", title);
            return false;
        }
    }

    /// <summary>列出全部终态标记（供诊断 / 报告展示）。</summary>
    public static List<BackfillTerminalMark> ListAll()
    {
        try
        {
            EnsureReady();
            using var ctx = CreateContext();
            return ctx.BackfillTerminalMarks.AsNoTracking().OrderByDescending(m => m.LastCheckedUtc).ToList();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "读取终态标记列表失败");
            return new List<BackfillTerminalMark>();
        }
    }

    /// <summary>把新增来源并入已有来源串（去重、忽略大小写），返回合并后的新串。</summary>
    private static string? MergeSources(string? existing, string? add)
    {
        if (string.IsNullOrWhiteSpace(add)) return existing;
        var set = new HashSet<string>(
            (existing ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        foreach (var s in add.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(s.Trim());
        return string.Join(",", set);
    }
}
