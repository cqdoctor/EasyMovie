using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MovieManager.Core.Models;

namespace MovieManager.Data.Configurations;

public class MovieTagConfiguration : IEntityTypeConfiguration<MovieTag>
{
    public void Configure(EntityTypeBuilder<MovieTag> builder)
    {
        builder.ToTable("MovieTags");

        // 复合主键
        builder.HasKey(mt => new { mt.MovieId, mt.TagId });

        builder.HasOne(mt => mt.Movie)
            .WithMany(m => m.MovieTags)
            .HasForeignKey(mt => mt.MovieId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(mt => mt.Tag)
            .WithMany(t => t.MovieTags)
            .HasForeignKey(mt => mt.TagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
