namespace SIDM.Core.Persistence;

public interface ISettingsRepository
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> GetAllRawAsync(CancellationToken cancellationToken = default);
}
