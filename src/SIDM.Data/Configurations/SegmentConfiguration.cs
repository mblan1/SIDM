using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIDM.Core.Models;

namespace SIDM.Data.Configurations;

internal sealed class SegmentConfiguration : IEntityTypeConfiguration<Segment>
{
    public void Configure(EntityTypeBuilder<Segment> b)
    {
        b.ToTable("Segments");
        b.HasKey(x => x.Id);

        b.Property(x => x.Status).HasConversion<int>();

        b.Property(x => x.LastErrorUtc).HasConversion(
            v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : (long?)null,
            v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : (DateTimeOffset?)null);

        // The ByteRange/RemainingRange/IsComplete properties on Segment are derived;
        // EF would otherwise try to map them. Tell it to ignore.
        b.Ignore(x => x.Range);
        b.Ignore(x => x.RemainingRange);
        b.Ignore(x => x.IsComplete);

        b.HasIndex(x => x.DownloadId);
        b.HasIndex(x => new { x.DownloadId, x.Idx }).IsUnique();
    }
}
