using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Extensions.Http;
using SIDM.Core.Abstractions;

namespace SIDM.Core.Http;

public static class HttpClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SIDM HTTP infrastructure: a probe client, a download client, and
    /// the typed services that depend on them. Both named clients use a SocketsHttp
    /// handler tuned for parallel range requests and a default Polly retry policy.
    /// </summary>
    public static IServiceCollection AddSidmHttp(this IServiceCollection services, int maxConnectionsPerServer = 16)
    {
        services.AddHttpClient(RangeProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => CreateHandler(maxConnectionsPerServer))
            .AddPolicyHandler(GetRetryPolicy());

        services.AddHttpClient(DownloadHttpClient.Name)
            .ConfigurePrimaryHttpMessageHandler(() => CreateHandler(maxConnectionsPerServer))
            .AddPolicyHandler(GetRetryPolicy());

        services.AddSingleton<IRangeProbe, RangeProbe>();
        return services;
    }

    private static SocketsHttpHandler CreateHandler(int maxConnectionsPerServer) => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        MaxConnectionsPerServer = maxConnectionsPerServer,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        UseCookies = false, // we forward cookies explicitly per request
    };

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        var delays = Backoff.DecorrelatedJitterBackoffV2(
            medianFirstRetryDelay: TimeSpan.FromMilliseconds(250),
            retryCount: 5);

        return HttpPolicyExtensions
            .HandleTransientHttpError() // 5xx, 408
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(delays);
    }
}

/// <summary>
/// Marker for the named HttpClient that segment workers use to fetch byte ranges.
/// </summary>
public static class DownloadHttpClient
{
    public const string Name = "sidm-download";
}
