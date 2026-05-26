using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIDM.Core.Models;

namespace SIDM.Data.Configurations;

internal sealed class DownloadConfiguration : IEntityTypeConfiguration<Download>
{
    public void Configure(EntityTypeBuilder<Download> b)
    {
        b.ToTable("Downloads");
        b.HasKey(x => x.Id);

        b.Property(x => x.Url).IsRequired().HasMaxLength(4096);
        b.Property(x => x.EffectiveUrl).HasMaxLength(4096);
        b.Property(x => x.FileName).IsRequired().HasMaxLength(512);
        b.Property(x => x.TargetPath).IsRequired().HasMaxLength(1024);
        b.Property(x => x.Mime).HasMaxLength(255);
        b.Property(x => x.ETag).HasMaxLength(255);
        b.Property(x => x.LastModified).HasMaxLength(64);
        b.Property(x => x.CookiesJson).HasColumnType("TEXT");
        b.Property(x => x.HeadersJson).HasColumnType("TEXT");
        b.Property(x => x.ExpectedHash).HasMaxLength(128);
        b.Property(x => x.HashAlgo).HasMaxLength(16);
        b.Property(x => x.ErrorMessage).HasMaxLength(2048);
        b.Property(x => x.Manifest).HasColumnType("TEXT");
        b.Property(x => x.SelectedYtDlpFormat).HasMaxLength(256);

        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.Priority).HasConversion<int>();
        b.Property(x => x.SourceKind).HasConversion<int>();

        // SQLite's EF provider can't sort or filter on TEXT-stored DateTimeOffset.
        // Persist as Unix milliseconds (INTEGER) so ORDER BY works server-side and
        // storage stays compact.
        b.Property(x => x.CreatedUtc).HasConversion(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        b.Property(x => x.StartedUtc).HasConversion(
            v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : (long?)null,
            v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : (DateTimeOffset?)null);
        b.Property(x => x.CompletedUtc).HasConversion(
            v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : (long?)null,
            v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : (DateTimeOffset?)null);

        b.HasMany(x => x.Segments)
            .WithOne()
            .HasForeignKey(s => s.DownloadId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.CreatedUtc);
    }
}
