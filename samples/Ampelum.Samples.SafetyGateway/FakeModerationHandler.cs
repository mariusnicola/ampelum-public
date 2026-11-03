using System.Net;
using System.Text;
using System.Text.Json;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>One request the fake moderation service received.</summary>
public sealed record ModerationRequest(string? Authorization, string Input);

/// <summary>
/// Plays the moderation service over HTTP: it flags any input that contains the marker and records
/// what it was sent, including the bearer header.
/// </summary>
public sealed class FakeModerationHandler(string flagMarker) : HttpMessageHandler
{
    readonly List<ModerationRequest> _requests = [];

    public IReadOnlyList<ModerationRequest> Requests => _requests;

    /// <summary>When set, every request fails with this error instead of an answer.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>When set, the service answers 200 with this body instead of a verdict.</summary>
    public string? RawBody { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (FailWith is not null)
            throw FailWith;

        if (RawBody is not null)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RawBody, Encoding.UTF8, "application/json") };

        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);
        var input = json.RootElement.GetProperty("input").GetString() ?? "";
        _requests.Add(new ModerationRequest(request.Headers.Authorization?.ToString(), input));

        var flagged = input.Contains(flagMarker, StringComparison.Ordinal);
        var reply = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new
                {
                    flagged,
                    categories = new Dictionary<string, bool> { [FakeData.FakeModerationCategory] = flagged, ["violence"] = false },
                },
            },
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(reply, Encoding.UTF8, "application/json"),
        };
    }
}
