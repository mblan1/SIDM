using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SIDM.Data;

/// <summary>
/// Used by `dotnet ef migrations add` to construct a DbContext at design time
/// without booting the WPF app's full IHost. Points at a throwaway path under TEMP
/// so design-time tooling never reads or writes the real user database.
/// </summary>
public sealed class SidmDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SidmDbContext>
{
    public SidmDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SidmDbContext>()
            .UseSqlite("Data Source=" + Path.Combine(Path.GetTempPath(), "sidm-design.db"))
            .Options;
        return new SidmDbContext(options);
    }
}
