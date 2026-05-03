using Microsoft.EntityFrameworkCore;
using SIDM.Core.Models;

namespace SIDM.Data;

public sealed class SidmDbContext : DbContext
{
    public SidmDbContext(DbContextOptions<SidmDbContext> options) : base(options) { }

    public DbSet<Download> Downloads => Set<Download>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ScheduleRule> ScheduleRules => Set<ScheduleRule>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SidmDbContext).Assembly);
    }
}
