using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.Extensibility;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// Bridges Sentry crash reporting with SIDM's user preferences. Sentry is
/// off by default — the user must explicitly opt in via Settings → Privacy
/// before any data leaves the machine. Once enabled, exceptions thrown on
/// the UI thread or anywhere reachable from the .NET host are captured
/// after running through <see cref="SidmEventScrubber"/>, which redacts
/// URLs, filenames, and cookie-shaped strings from messages and stack
/// traces.
///
/// The DSN can come from two places:
///   1. A persisted user setting <c>crashReports.dsn</c> — useful for
///      contributors who run their own Sentry org.
///   2. A compile-time fallback (currently empty). When SIDM goes public,
///      we'll hard-code the production DSN here so opting in "just works"
///      without the user having to find a DSN.
/// </summary>
public sealed class CrashReportingService
{
    public const string EnabledSettingKey = "crashReports.enabled";
    public const string DsnSettingKey = "crashReports.dsn";

    /// <summary>Hard-coded fallback DSN. Empty until the public Sentry org is provisioned.</summary>
    private const string DefaultDsn = "";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CrashReportingService> _logger;

    private IDisposable? _sentryHandle;

    public CrashReportingService(IServiceScopeFactory scopeFactory, ILogger<CrashReportingService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsEnabled { get; private set; }
    public string? Dsn { get; private set; }

    public async Task LoadAndStartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            IsEnabled = await settings.GetAsync<bool>(EnabledSettingKey, cancellationToken);
            Dsn = await settings.GetAsync<string>(DsnSettingKey, cancellationToken) ?? DefaultDsn;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load crash reporting settings");
            return;
        }

        if (IsEnabled) Start();
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        IsEnabled = enabled;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await settings.SetAsync(EnabledSettingKey, enabled, cancellationToken);

        if (enabled) Start();
        else Stop();
    }

    public async Task SetDsnAsync(string? dsn, CancellationToken cancellationToken = default)
    {
        Dsn = string.IsNullOrWhiteSpace(dsn) ? null : dsn.Trim();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        if (Dsn is null) await settings.RemoveAsync(DsnSettingKey, cancellationToken);
        else await settings.SetAsync(DsnSettingKey, Dsn, cancellationToken);

        // Re-start so the new DSN takes effect immediately.
        if (IsEnabled)
        {
            Stop();
            Start();
        }
    }

    private void Start()
    {
        if (_sentryHandle is not null) return;
        if (string.IsNullOrWhiteSpace(Dsn))
        {
            _logger.LogInformation("Crash reporting is enabled but no DSN configured — skipping init.");
            return;
        }

        _sentryHandle = SentrySdk.Init(o =>
        {
            o.Dsn = Dsn!;
            o.Release = $"sidm@{SIDM.Core.AppInfo.Version}";
            o.AutoSessionTracking = false;
            o.IsGlobalModeEnabled = true;
            // Strip PII before anything goes over the wire.
            o.SetBeforeSend(SidmEventScrubber.Scrub);
            o.SetBeforeBreadcrumb(SidmEventScrubber.ScrubBreadcrumb);
            // Don't ship logs / network events / etc. by default.
            o.SendDefaultPii = false;
            o.AttachStacktrace = true;
        });
        _logger.LogInformation("Sentry crash reporting started");
    }

    private void Stop()
    {
        _sentryHandle?.Dispose();
        _sentryHandle = null;
        SentrySdk.Close();
    }
}

/// <summary>
/// Hosted service that boots <see cref="CrashReportingService"/> on app
/// start so a crash in the first 30 seconds isn't lost.
/// </summary>
public sealed class CrashReportingStartup : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly CrashReportingService _service;

    public CrashReportingStartup(CrashReportingService service)
    {
        _service = service;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _service.LoadAndStartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Strips PII from Sentry events before they leave the machine. The user's
/// download URLs, cookies, and local file paths are all sensitive — they
/// can identify the user or the content they're downloading. We redact
/// them and keep the structural part of stack traces / messages that's
/// actually useful for triage.
///
/// All methods are pure / static and safe to unit-test.
/// </summary>
internal static class SidmEventScrubber
{
    // Match URLs (incl. http/https/file/ftp), Windows paths (C:\ ... \ file),
    // and cookie-shaped substrings (name=value separated by ;).
    private static readonly Regex UrlRegex = new(
        @"\b(?:https?|ftp|file)://[^\s\""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WindowsPathRegex = new(
        @"[a-zA-Z]:\\(?:[^\\:*?\""<>|\r\n]+\\)*[^\\:*?\""<>|\r\n]+",
        RegexOptions.Compiled);

    private static readonly Regex CookieishRegex = new(
        @"\b[\w-]+=[^;\s\""]{4,}",
        RegexOptions.Compiled);

    public static SentryEvent? Scrub(SentryEvent e, SentryHint _)
    {
        if (e.Message?.Message is { } msg)
        {
            e.Message.Message = Redact(msg);
        }
        if (e.Message?.Formatted is { } fmt)
        {
            e.Message.Formatted = Redact(fmt);
        }
        if (e.SentryExceptions is { } exceptions)
        {
            foreach (var ex in exceptions)
            {
                if (ex.Value is { } val) ex.Value = Redact(val);
            }
        }
        return e;
    }

    public static Breadcrumb? ScrubBreadcrumb(Breadcrumb b, SentryHint _)
    {
        // Breadcrumbs are immutable on the public surface; rebuild via the
        // available constructor when the message needs redaction. The
        // public ctor requires non-null message + type, so fall back to
        // empties if Sentry ever hands us a sparse breadcrumb.
        if (b.Message is null) return b;
        var msg = Redact(b.Message);
        if (msg == b.Message) return b;
        return new Breadcrumb(msg, b.Type ?? "default", b.Data, b.Category, b.Level);
    }

    /// <summary>Public for tests.</summary>
    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var s = UrlRegex.Replace(input, "<url>");
        s = WindowsPathRegex.Replace(s, "<path>");
        s = CookieishRegex.Replace(s, "<cookie>");
        return s;
    }
}
