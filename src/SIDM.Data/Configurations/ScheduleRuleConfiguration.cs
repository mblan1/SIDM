using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIDM.Core.Models;

namespace SIDM.Data.Configurations;

internal sealed class ScheduleRuleConfiguration : IEntityTypeConfiguration<ScheduleRule>
{
    public void Configure(EntityTypeBuilder<ScheduleRule> b)
    {
        b.ToTable("ScheduleRules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(64);
        b.Property(x => x.StartTime).IsRequired().HasMaxLength(8);
        b.Property(x => x.EndTime).IsRequired().HasMaxLength(8);
        b.Property(x => x.DaysOfWeek).HasConversion<int>();
    }
}
