using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIDM.Core.Models;

namespace SIDM.Data.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("Categories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(64);
        b.Property(x => x.DefaultPath).HasMaxLength(1024);
        b.Property(x => x.Extensions).HasMaxLength(255);
        b.Property(x => x.Color).HasMaxLength(16);
        b.HasIndex(x => x.Name).IsUnique();
    }
}
