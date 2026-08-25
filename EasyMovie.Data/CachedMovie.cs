using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EasyMovie.Data;

/// <summary>
/// 离线元数据缓存条目：作为「首次导入 / 所有网络源不可达」时的兜底匹配源。
/// 种子数据由 IMDb（西片与国际发行片）与常见片清单预填充；运行时联网命中结果也会回流写入，逐步自学习。
/// 与业务主库（MovieDbContext）分离，独立存放在 cache.db，避免污染用户片库。
/// </summary>
public class CachedMovie
{
    public int Id { get; set; }

    /// <summary>展示片名（通常取英文/拉丁译名，便于与英文文件名匹配）</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>原产片名（中文片名或原名，便于与中文文件名匹配）</summary>
    public string? OriginalTitle { get; set; }

    public int Year { get; set; }

    public string? Director { get; set; }
    public string? Cast { get; set; }
    public string? Country { get; set; }
    public string? Language { get; set; }
    public string? PosterUrl { get; set; }
    public double? Rating { get; set; }
    public int? RatingCount { get; set; }

    /// <summary>种子来源标记：imdb / seed / douban / tmdb ...（用于统计与调试）</summary>
    public string Source { get; set; } = "seed";

    /// <summary>归一化片名键（去标点空白、保留字母数字与中日韩字符、转小写），用于精确匹配</summary>
    public string NormTitle { get; set; } = string.Empty;

    /// <summary>归一化原产片名键，用于中文/原名文件名的精确匹配</summary>
    public string? NormOriginal { get; set; }
}

public class CachedMovieConfiguration : IEntityTypeConfiguration<CachedMovie>
{
    public void Configure(EntityTypeBuilder<CachedMovie> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired();
        b.Property(x => x.NormTitle).IsRequired();
        b.HasIndex(x => x.NormTitle);
        b.HasIndex(x => x.NormOriginal);
    }
}
