using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using EasyMovie.Core.Models;

namespace EasyMovie.Data.Configurations;

public class MovieCollectionConfiguration : IEntityTypeConfiguration<MovieCollection>
{
    public void Configure(EntityTypeBuilder<MovieCollection> builder)
    {
        builder.ToTable("Collections");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedOnAdd();

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(c => c.Description)
            .HasMaxLength(1000);

        builder.HasMany(c => c.Movies)
            .WithOne(m => m.Collection)
            .HasForeignKey(m => m.CollectionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(c => c.Name);
    }
}
