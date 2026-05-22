using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using EasyMovie.Core.Models;

namespace EasyMovie.Data.Configurations;

public class MovieConfiguration : IEntityTypeConfiguration<Movie>
{
    public void Configure(EntityTypeBuilder<Movie> builder)
    {
        builder.ToTable("Movies");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedOnAdd();

        builder.Property(m => m.Title)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(m => m.OriginalTitle)
            .HasMaxLength(200);

        builder.Property(m => m.Director)
            .HasMaxLength(100);

        builder.Property(m => m.Cast)
            .HasMaxLength(500);

        builder.Property(m => m.Country)
            .HasMaxLength(100);

        builder.Property(m => m.Language)
            .HasMaxLength(100);

        builder.Property(m => m.Synopsis)
            .HasMaxLength(4000);

        builder.Property(m => m.Notes)
            .HasMaxLength(2000);

        builder.Property(m => m.CoverImagePath)
            .HasMaxLength(500);

        builder.Property(m => m.PosterUrl)
            .HasMaxLength(500);

        builder.Property(m => m.PosterData)
            .HasColumnType("BLOB");

        builder.Property(m => m.FilePath)
            .HasMaxLength(1000);

        builder.Property(m => m.DoubanId)
            .HasMaxLength(50);

        builder.Property(m => m.TmdbId)
            .HasMaxLength(50);

        builder.Property(m => m.SearchIndex)
            .HasMaxLength(2000);

        // 分类关系
        builder.HasOne(m => m.Category)
            .WithMany(c => c.Movies)
            .HasForeignKey(m => m.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // 索引
        builder.HasIndex(m => m.Title);
        builder.HasIndex(m => m.Year);
        builder.HasIndex(m => m.Rating);
        builder.HasIndex(m => m.WatchStatus);
        builder.HasIndex(m => m.CategoryId);
    }
}
