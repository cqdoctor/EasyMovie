using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using EasyMovie.Core.Models;

namespace EasyMovie.Data.Configurations;

public class WatchLogConfiguration : IEntityTypeConfiguration<WatchLog>
{
    public void Configure(EntityTypeBuilder<WatchLog> builder)
    {
        builder.ToTable("WatchLogs");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedOnAdd();

        builder.Property(w => w.Location).HasMaxLength(200);
        builder.Property(w => w.Companion).HasMaxLength(200);
        builder.Property(w => w.Notes).HasMaxLength(5000);

        builder.HasOne(w => w.Movie)
            .WithMany(m => m.WatchLogs)
            .HasForeignKey(w => w.MovieId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(w => w.MovieId);
        builder.HasIndex(w => w.WatchDate);
    }
}
