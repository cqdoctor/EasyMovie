using System.Text.RegularExpressions;
using EasyMovie.Core.Helpers;
using EasyMovie.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Data;

/// <summary>
/// 一次性文本清洗迁移：把历史脏数据（简介残留 HTML 标签、导演整值是职位标签等）重写一遍。
///
/// 从 Client 的 <c>DbHelper</c> 搬到 Data 层的唯一目的是**可测试**——Client 是 WPF 项目、
/// 零测试覆盖，这段"改用户真实数据"的逻辑放在那里只能靠启动一次 GUI 才能验证。
/// 搬过来后可以拿临时 SQLite 库跑真实迁移，见 EasyMovie.Tests/Core.Tests/TextCleanupMigrationTests。
///
/// <para><b>标志文件的语义（footgun）</b>：迁移只跑一次，标志文件一存在就永远跳过。
/// 后果是<b>迁移之后新写入的脏数据再也不会被清理</b>——实测（2026-08-31）290 部里
/// 116 部简介仍带 &lt;p&gt; 标签，就是 v2 跑完之后由文件夹监控自动入库 / 定时同步等
/// 当时未清洗的写入路径写进去的。因此每次<b>扩大清洗范围</b>都必须升版本号
/// （v3 = v2 + Director 过 MovieCreditCleaner），否则存量数据不会再过一遍。</para>
///
/// <para>本迁移<b>只按列回写变化的文本字段</b>：先窄投影读取，再用只附载主键的桩实体
/// 按列标脏。绝不要把整行（含 PosterData 等大 BLOB）加载进内存再回写。</para>
/// </summary>
public static class TextCleanupMigration
{
    /// <summary>
    /// 执行迁移。标志文件已存在时直接返回 0。
    /// </summary>
    /// <param name="options">数据库选项（生产环境由 DbHelper.CreateOptions() 提供）</param>
    /// <param name="flagPath">完成标志文件路径</param>
    /// <returns>被改写的行数；跳过或无需改写时返回 0</returns>
    public static int CleanHtmlInExistingData(DbContextOptions<MovieDbContext> options, string flagPath)
    {
        if (string.IsNullOrEmpty(flagPath)) throw new ArgumentException("迁移标志文件路径不能为空", nameof(flagPath));
        if (File.Exists(flagPath)) return 0;

        using var ctx = new MovieDbContext(options);

        // 只投影需要的文本字段，避免把 PosterData 等大 BLOB 整行加载进内存
        var rows = ctx.Movies
            .Select(m => new { m.Id, m.Synopsis, m.Director, m.Cast, m.Country, m.Notes })
            .AsNoTracking()
            .ToList();

        var changedRows = 0;
        foreach (var row in rows)
        {
            var cleanSynopsis = StripHtml(row.Synopsis);
            // 导演除剥 HTML 外还要过职位标签黑名单：实测库中已有导演值为「编剧」的脏数据
            // （#207 幽灵 Phantom AC3），纯 StripHtml 抓不到。空串归一为 null，与其它字段一致。
            var cleanedDirector = MovieCreditCleaner.CleanDirector(StripHtml(row.Director));
            var cleanDirector = string.IsNullOrEmpty(cleanedDirector) ? null : cleanedDirector;
            var cleanCast = StripHtml(row.Cast);
            var cleanCountry = StripHtml(row.Country);
            var cleanNotes = StripHtml(row.Notes);

            if (cleanSynopsis == row.Synopsis && cleanDirector == row.Director &&
                cleanCast == row.Cast && cleanCountry == row.Country && cleanNotes == row.Notes)
                continue;

            // 仅附载主键，按列标脏回写变化字段，绝不触碰 PosterData 等其它列
            var tracked = new Movie { Id = row.Id };
            ctx.Attach(tracked);
            if (cleanSynopsis != row.Synopsis)
            {
                ctx.Entry(tracked).Property(x => x.Synopsis).CurrentValue = cleanSynopsis;
                ctx.Entry(tracked).Property(x => x.Synopsis).IsModified = true;
            }
            if (cleanDirector != row.Director)
            {
                ctx.Entry(tracked).Property(x => x.Director).CurrentValue = cleanDirector;
                ctx.Entry(tracked).Property(x => x.Director).IsModified = true;
            }
            if (cleanCast != row.Cast)
            {
                ctx.Entry(tracked).Property(x => x.Cast).CurrentValue = cleanCast;
                ctx.Entry(tracked).Property(x => x.Cast).IsModified = true;
            }
            if (cleanCountry != row.Country)
            {
                ctx.Entry(tracked).Property(x => x.Country).CurrentValue = cleanCountry;
                ctx.Entry(tracked).Property(x => x.Country).IsModified = true;
            }
            if (cleanNotes != row.Notes)
            {
                ctx.Entry(tracked).Property(x => x.Notes).CurrentValue = cleanNotes;
                ctx.Entry(tracked).Property(x => x.Notes).IsModified = true;
            }
            changedRows++;
        }

        if (changedRows > 0) ctx.SaveChanges();

        File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
        return changedRows;
    }

    /// <summary>
    /// 增强版 HTML 清洗：剥标签 + HTML 实体解码 + 空白折叠；空串归一为 null。
    ///
    /// 与 <see cref="TextCleaner.StripHtml"/> <b>刻意不合并</b>——两者契约不同：
    /// 本方法把"非空但清洗后为空"的串归一为 null（把脏值表达为"无值"，方便迁移写回），
    /// TextCleaner.StripHtml 则保留调用方传入的空串语义。合并会改变其中一侧的行为。
    ///
    /// 注意开头的短路：null 与 "" <b>原样返回</b>（"" 不会被改成 null）。
    /// 这是实测确认的历史行为，由 TextCleanupMigrationTests 锁定。
    /// </summary>
    public static string? StripHtml(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var result = Regex.Replace(input, @"<[^>]+>", "");
        result = System.Net.WebUtility.HtmlDecode(result);
        result = Regex.Replace(result, @"\s+", " ").Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }
}
