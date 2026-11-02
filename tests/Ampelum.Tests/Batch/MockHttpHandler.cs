using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Ampelum.Tests.Batch;

/// <summary>
/// Test-only HttpMessageHandler that matches incoming requests by (method, path)
/// and returns canned responses. Tracks the call sequence for assertions.
/// </summary>
internal sealed class MockHttpHandler : HttpMessageHandler
{
    readonly List<Func<HttpRequestMessage, HttpResponseMessage?>> _matchers = new();
    public List<HttpRequestMessage> Calls { get; } = new();

    public MockHttpHandler When(HttpMethod method, string pathSubstring, HttpStatusCode status, string body, string contentType = "application/json")
    {
        _matchers.Add(req =>
        {
            if (req.Method != method) return null;
            var url = req.RequestUri?.ToString() ?? "";
            if (!url.Contains(pathSubstring, StringComparison.Ordinal)) return null;
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return resp;
        });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        foreach (var matcher in _matchers)
        {
            var resp = matcher(request);
            if (resp is not null) return Task.FromResult(resp);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"unmatched: {request.Method} {request.RequestUri}"),
        });
    }
}
