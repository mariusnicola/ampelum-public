using System.Net;

namespace Ampelum.Samples.TracedPipeline;

/// <summary>One request an exporter sent.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Uri">Absolute request address.</param>
/// <param name="Headers">Request and content headers, name to joined values.</param>
/// <param name="Body">Raw request body.</param>
public sealed record RecordedRequest(
    HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    /// <summary>The body decoded as UTF-8.</summary>
    public string BodyText => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>Answers every request with 200, after an optional per-address delay, and keeps what was sent, so no network is used.</summary>
public sealed class RecordingHandler(Func<Uri, TimeSpan>? delayFor = null) : HttpMessageHandler
{
    readonly List<RecordedRequest> _requests = [];
    readonly object _gate = new();

    /// <summary>Requests received so far, in arrival order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToArray();
        }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        // A slow endpoint shows whether the caller really waits for its exports.
        if (delayFor?.Invoke(request.RequestUri!) is { } delay && delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return Record(request, body);
    }

    // The OTLP exporter sends synchronously.
    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
        return Record(request, body);
    }

    HttpResponseMessage Record(HttpRequestMessage request, byte[] body)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
            headers[header.Key] = string.Join(",", header.Value);
        if (request.Content is not null)
            foreach (var header in request.Content.Headers)
                headers[header.Key] = string.Join(",", header.Value);

        lock (_gate)
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
    }
}
