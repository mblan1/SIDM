using SIDM.Core.Http;

namespace SIDM.Core.Abstractions;

public interface IRangeProbe
{
    Task<ProbeResult> ProbeAsync(
        Uri url,
        IReadOnlyDictionary<string, string>? requestHeaders,
        IReadOnlyDictionary<string, string>? cookies,
        CancellationToken cancellationToken);
}
