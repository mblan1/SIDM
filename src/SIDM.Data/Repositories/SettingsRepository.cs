using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIDM.Core.Models;
using SIDM.Core.Persistence;

namespace SIDM.Data.Repositories;

public sealed class SettingsRepository : ISettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SidmDbContext _db;

    public SettingsRepository(SidmDbContext db)
    {
        _db = db;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var entry = await _db.Settings.FindAsync(new object[] { key }, cancellationToken);
        if (entry?.Value is null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(entry.Value, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var existing = await _db.Settings.FindAsync(new object[] { key }, cancellationToken);
        if (existing is null)
        {
            _db.Settings.Add(new SettingEntry { Key = key, Value = json });
        }
        else
        {
            existing.Value = json;
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Settings.FindAsync(new object[] { key }, cancellationToken);
        if (entity is null) return;
        _db.Settings.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllRawAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.Settings.AsNoTracking().ToListAsync(cancellationToken);
        return rows
            .Where(r => r.Value is not null)
            .ToDictionary(r => r.Key, r => r.Value!);
    }
}
