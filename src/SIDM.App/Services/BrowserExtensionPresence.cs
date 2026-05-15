using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// Tracks "has the SIDM browser extension ever talked to this app, and
/// when?" — used by the in-app extension installer to decide whether to
/// show the "install browser extension" banner and to flip per-browser
/// rows in the install dialog to "Connected" once the extension says hello.
///
/// Persistence: a per-browser timestamp in the settings table, key
/// <c>extension.&lt;kind&gt;.lastSeenUtc</c>. Loaded eagerly on startup so
/// banners / dialogs can read synchronously.
/// </summary>
public sealed class BrowserExtensionPresence
{
    public const string SettingKeyPrefix = "extension.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BrowserExtensionPresence> _logger;
    private readonly ConcurrentDictionary<BrowserKind, DateTimeOffset?> _lastSeen = new();

    public BrowserExtensionPresence(IServiceScopeFactory scopeFactory, ILogger<BrowserExtensionPresence> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Raised when a browser kind transitions from "never seen" to "seen".</summary>
    public event Action<BrowserKind>? FirstSeen;

    /// <summary>Loads persisted last-seen timestamps. Call once during startup.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            foreach (var kind in Enum.GetValues<BrowserKind>())
            {
                var key = KeyFor(kind);
                var raw = await settings.GetAsync<string>(key, cancellationToken);
                if (DateTimeOffset.TryParse(raw, out var ts)) _lastSeen[kind] = ts;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load extension presence — defaulting to all-never-seen");
        }
    }

    public DateTimeOffset? GetLastSeen(BrowserKind kind) =>
        _lastSeen.TryGetValue(kind, out var ts) ? ts : null;

    public bool IsConnected(BrowserKind kind) =>
        GetLastSeen(kind) is { } ts && (DateTimeOffset.UtcNow - ts) < TimeSpan.FromDays(30);

    /// <summary>True iff at least one browser-kind has connected within the last 30 days.</summary>
    public bool AnyConnected =>
        Enum.GetValues<BrowserKind>().Any(IsConnected);

    /// <summary>
    /// Called by the IPC dispatcher when a HelloRequest arrives. Updates the
    /// timestamp and fires <see cref="FirstSeen"/> the first time a given
    /// kind shows up so the UI can collapse its install banner.
    /// </summary>
    public void MarkSeen(BrowserKind kind)
    {
        var wasFirstSeen = !_lastSeen.ContainsKey(kind) || _lastSeen[kind] is null;
        _lastSeen[kind] = DateTimeOffset.UtcNow;

        // Persist on a background task — never block the IPC dispatcher.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
                await settings.SetAsync(KeyFor(kind), _lastSeen[kind]!.Value.ToString("o"));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist extension presence for {Kind}", kind);
            }
        });

        if (wasFirstSeen)
        {
            try { FirstSeen?.Invoke(kind); }
            catch (Exception ex) { _logger.LogDebug(ex, "FirstSeen handler threw for {Kind}", kind); }
        }
    }

    /// <summary>
    /// Parses the IPC <c>HelloRequest.ClientName</c> into a browser kind.
    /// Extensions identify themselves as e.g. "SIDM-Chrome-Extension" /
    /// "SIDM-Firefox-Extension". Unknown names return null and are skipped.
    /// </summary>
    public static BrowserKind? KindFromClientName(string? clientName)
    {
        if (string.IsNullOrEmpty(clientName)) return null;
        var lower = clientName.ToLowerInvariant();
        if (lower.Contains("chrome")) return BrowserKind.Chrome;
        if (lower.Contains("edge")) return BrowserKind.Edge;
        if (lower.Contains("brave")) return BrowserKind.Brave;
        if (lower.Contains("firefox") || lower.Contains("gecko")) return BrowserKind.Firefox;
        return null;
    }

    private static string KeyFor(BrowserKind kind) =>
        SettingKeyPrefix + kind.ToString().ToLowerInvariant() + ".lastSeenUtc";
}
