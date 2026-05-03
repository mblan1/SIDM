using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SIDM.Data;

/// <summary>
/// Hosted service that runs once at app startup: enables WAL mode on the SQLite
/// database and applies any pending EF migrations. Idempotent — safe to run on
/// every launch.
/// </summary>
public sealed class SqliteSchemaInitializer : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SqliteSchemaInitializer> _logger;
    private readonly string _connectionString;

    public SqliteSchemaInitializer(IServiceProvider services, ILogger<SqliteSchemaInitializer> logger, string connectionString)
    {
        _services = services;
        _logger = logger;
        _connectionString = connectionString;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1. Ensure the directory exists.
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (!string.IsNullOrEmpty(builder.DataSource))
        {
            var dir = Path.GetDirectoryName(builder.DataSource);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        // 2. Apply pending migrations.
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SidmDbContext>();
        await db.Database.MigrateAsync(cancellationToken);

        // 3. Set WAL pragma + synchronous=NORMAL on a one-off connection.
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("SQLite ready at {DataSource} (WAL, synchronous=NORMAL)", builder.DataSource);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
