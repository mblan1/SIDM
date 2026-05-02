using System.Net.Http;

namespace SIDM.Core.Tests.Http;

/// <summary>
/// Records every outgoing request and returns canned responses based on a per-request
/// dispatch function. Lets us unit-test HTTP-driven services without a real server.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var response = _responder(request);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }

    public static IHttpClientFactory ToFactory(string clientName, FakeHttpMessageHandler handler) =>
        new SingleClientFactory(clientName, handler);

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly string _name;
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(string name, HttpMessageHandler handler)
        {
            _name = name;
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            // Return same handler regardless of name in tests.
            _ = name;
            return new HttpClient(_handler, disposeHandler: false);
        }
    }
}
