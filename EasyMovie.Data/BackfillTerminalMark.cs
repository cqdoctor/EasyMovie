using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EasyMovie.Data;

/// <summary>
/// 补全终态标记：记录「已确认外部数据源本身查不到评分」的影片，使其不再进入后续每轮补全。
///
/// <para><b>为什么需要</b>：豆瓣有条目但 <c>rating.count=0</c>（评分人数不足 / 未上映 / 冷门）的影片，
/// 以及三源均无命中的脏标题影片，是<b>数据源天花板</b>而非临时限流——重试再多次也不会产生评分。
/// 旧实现把这类跳过与「暂无限流」混为一谈，导致每轮调度（默认每 3h）都对同一部冷门片重新搜索，
/// 白白消耗豆瓣每日配额、掩盖真实进度、并让「剩余待补全数」永远不下降。</para>
///
/// <para><b>语义</b>：这是<b>终态</b>（terminal）而非缓存——只在确认「源本身无此数据」时写入，
/// 绝不对「网络异常 / 被限流 / 解析失败」写入，保证临时故障不会把影片永久踢出队列。
/// 若日后新增数据源或该片在源侧补上评分，可调用 <see cref="BackfillTerminalMarks.Clear"/>
/// 清除标记让影片重新入队。</para>
///
/// <para><b>键空间</b>：沿用 <see cref="MovieApi.LocalMovieCache.NormalizeKey"/> 的归一化标题
/// （去标点空白、保留字母数字与中日韩字符、转小写），与 <c>CachedMovies.NormTitle</c> 同一套规则，
/// 保证跨轮次、跨进程稳定命中同一部片。</para>
/// </summary>
public class BackfillTerminalMark
{
    public int Id { get; set; }

    /// <summary>归一化片名键（与 CachedMovies.NormTitle 同规则）</summary>
    public string NormTitle { get; set; } = string.Empty;

    /// <summary>原始片名（仅供人读与排查，不参与匹配）</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>年份（冗余存一份，便于人工核对；匹配只用 <see cref="NormTitle"/>）</summary>
    public int? Year { get; set; }

    /// <summary>
    /// 终态原因分类：
    /// <c>source_no_rating</c> = 源有条目但评分人数为 0 / 未上映；
    /// <c>source_no_match</c>   = 源与替代源均搜不到该片（脏标题或源未收录）。
    /// </summary>
    public string Reason { get; set; } = "source_no_rating";

    /// <summary>已确认无评分的来源（逗号分隔，如 "douban,tmdb,omdb"）</summary>
    public string? ConfirmedSources { get; set; }

    /// <summary>首次确认时间（UTC）</summary>
    public DateTime MarkedAtUtc { get; set; }

    /// <summary>最近一次复核时间（UTC）</summary>
    public DateTime LastCheckedUtc { get; set; }

    /// <summary>累计确认次数（用于评估是否值得周期性重启尝试）</summary>
    public int HitCount { get; set; } = 1;
}

public class BackfillTerminalMarkConfiguration : IEntityTypeConfiguration<BackfillTerminalMark>
{
    public void Configure(EntityTypeBuilder<BackfillTerminalMark> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.NormTitle).IsRequired();
        b.HasIndex(x => x.NormTitle).IsUnique();
    }
}
