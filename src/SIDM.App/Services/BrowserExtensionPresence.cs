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
    /// <summary>
    /// Last <c>clientVersion</c> reported by each browser's extension via
    /// the IPC hello handshake. Used by the install dialog to render
    /// "installed 0.1.4 → bundled 0.1.5" and flip the action button to
    /// "Update" when the user is on an older build.
    /// </summary>
    private readonly ConcurrentDictionary<BrowserKind, string?> _lastSeenVersion = new();

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
                var raw = await settings.GetAsync<string>(KeyFor(kind), cancellationToken);
                if (DateTimeOffset.TryParse(raw, out var ts)) _lastSeen[kind] = ts;
                var ver = await settings.GetAsync<string>(VersionKeyFor(kind), cancellationToken);
                if (!string.IsNullOrEmpty(ver)) _lastSeenVersion[kind] = ver;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load extension presence — defaulting to all-never-seen");
        }
    }

    public DateTimeOffset? GetLastSeen(BrowserKind kind) =>
        _lastSeen.TryGetValue(kind, out var ts) ? ts : null;

    /// <summary>Last clientVersion the extension reported via hello, or null
    /// if no extension has ever connected (or version wasn't recorded yet
    /// — older builds didn't capture it).</summary>
    public string? GetLastSeenVersion(BrowserKind kind) =>
        _lastSeenVersion.TryGetValue(kind, out var v) ? v : null;

    public bool IsConnected(BrowserKind kind) =>
        GetLastSeen(kind) is { } ts && (DateTimeOffset.UtcNow - ts) < TimeSpan.FromDays(30);

    /// <summary>True iff at least one browser-kind has connected within the last 30 days.</summary>
    public bool AnyConnected =>
        Enum.GetValues<BrowserKind>().Any(IsConnected);

    /// <summary>
    /// Called by the IPC dispatcher when a HelloRequest arrives. Updates the
    /// timestamp + recorded version, and fires <see cref="FirstSeen"/> the
    /// first time a given kind shows up so the UI can collapse its install
    /// banner. <see cref="VersionChanged"/> fires whenever the reported
    /// version differs from what we had on file (covers fresh-install,
    /// upgrade, and the very rare downgrade).
    /// </summary>
    public void MarkSeen(BrowserKind kind, string? clientVersion = null)
    {
        var wasFirstSeen = !_lastSeen.ContainsKey(kind) || _lastSeen[kind] is null;
        _lastSeen[kind] = DateTimeOffset.UtcNow;

        var versionChanged = false;
        if (!string.IsNullOrEmpty(clientVersion))
        {
            var previous = _lastSeenVersion.TryGetValue(kind, out var v) ? v : null;
            if (!string.Equals(previous, clientVersion, StringComparison.Ordinal))
            {
                _lastSeenVersion[kind] = clientVersion;
                versionChanged = true;
            }
        }

        // Persist on a background task — never block the IPC dispatcher.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
                await settings.SetAsync(KeyFor(kind), _lastSeen[kind]!.Value.ToString("o"));
                if (_lastSeenVersion.TryGetValue(kind, out var v) && !string.IsNullOrEmpty(v))
                {
                    await settings.SetAsync(VersionKeyFor(kind), v);
                }
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
        if (versionChanged)
        {
            try { VersionChanged?.Invoke(kind); }
            catch (Exception ex) { _logger.LogDebug(ex, "VersionChanged handler threw for {Kind}", kind); }
        }
    }

    /// <summary>
    /// Raised when the version reported by an extension changes (typical
    /// trigger: the user clicks Update in the install dialog, reloads the
    /// extension in Chrome, and the extension reconnects on the new
    /// version). UI rows subscribe to refresh their "Up to date"/"Update
    /// available" badge without polling.
    /// </summary>
    public event Action<BrowserKind>? VersionChanged;

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

    private static string VersionKeyFor(BrowserKind kind) =>
        SettingKeyPrefix + kind.ToString().ToLowerInvariant() + ".lastSeenVersion";
}
