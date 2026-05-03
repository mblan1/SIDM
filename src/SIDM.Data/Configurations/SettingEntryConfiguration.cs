using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIDM.Core.Models;

namespace SIDM.Data.Configurations;

internal sealed class SettingEntryConfiguration : IEntityTypeConfiguration<SettingEntry>
{
    public void Configure(EntityTypeBuilder<SettingEntry> b)
    {
        b.ToTable("Settings");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(128);
        b.Property(x => x.Value).HasColumnType("TEXT");
    }
}
