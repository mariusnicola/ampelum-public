using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ampelum.Samples.BulkDocuments;

/// <summary>
/// Plays the Anthropic message batches endpoints for fake mode and tests: accepts a batch, reports it in
/// progress for a few status checks, then returns one result line per request, in reverse order.
/// The summary text is built from the submitted document text so a mismatch between id and text shows.
/// </summary>
public sealed class FakeAnthropicBatchHandler : HttpMessageHandler
{
    readonly Dictionary<string, List<string>> _resultLines = new();
    readonly Dictionary<string, int> _polls = new();
    readonly Dictionary<string, int> _failuresLeft = new();
    readonly HashSet<string> _alwaysFail = [];
    readonly HashSet<string> _canceled = [];
    readonly HashSet<string> _neverEnd = [];
    readonly Dictionary<int, HttpStatusCode> _rejectedSubmits = new();
    int _submitCalls;
    readonly int _pollsBeforeEnded;

    public FakeAnthropicBatchHandler(int pollsBeforeEnded = 1) => _pollsBeforeEnded = pollsBeforeEnded;

    /// <summary>Bodies of every batch create call, in order.</summary>
    public List<JsonNode> Submissions { get; } = [];

    /// <summary>Count of HTTP calls that reached the handler.</summary>
    public int Calls { get; private set; }

    /// <summary>Report this document as errored in its first <paramref name="times"/> submissions.</summary>
    public FakeAnthropicBatchHandler FailDocument(string customId, int times = 1)
    {
        _failuresLeft[customId] = times;
        return this;
    }

    /// <summary>Report this document as errored in every submission.</summary>
    public FakeAnthropicBatchHandler AlwaysFailDocument(string customId)
    {
        _alwaysFail.Add(customId);
        return this;
    }

    /// <summary>Report the n-th created batch (1 based) as canceled instead of ended.</summary>
    public FakeAnthropicBatchHandler CancelBatchNumber(int number)
    {
        _canceled.Add($"batch_{number}");
        return this;
    }

    /// <summary>Keep the n-th created batch (1 based) in progress for ever.</summary>
    public FakeAnthropicBatchHandler NeverEndBatchNumber(int number)
    {
        _neverEnd.Add($"batch_{number}");
        return this;
    }

    /// <summary>Answer the n-th batch create call (1 based) with this HTTP status instead of accepting it.</summary>
    public FakeAnthropicBatchHandler RejectSubmitNumber(int number, HttpStatusCode status)
    {
        _rejectedSubmits[number] = status;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path.EndsWith("/v1/messages/batches", StringComparison.Ordinal))
        {
            if (_rejectedSubmits.TryGetValue(++_submitCalls, out var rejected))
                return Task.FromResult(new HttpResponseMessage(rejected));
            return Json(Create(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()));
        }
        if (request.Method == HttpMethod.Get && path.EndsWith("/results", StringComparison.Ordinal))
            return Task.FromResult(Reply(string.Join("\n", _resultLines[BatchId(path, 2)]), "application/x-jsonl"));
        if (request.Method == HttpMethod.Get && path.Contains("/v1/messages/batches/", StringComparison.Ordinal))
            return Json(Status(BatchId(path, 1)));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    string Create(string body)
    {
        var parsed = JsonNode.Parse(body)!;
        Submissions.Add(parsed);
        var id = $"batch_{Submissions.Count}";
        var lines = new List<string>();
        foreach (var request in parsed["requests"]!.AsArray())
        {
            var customId = request!["custom_id"]!.GetValue<string>();
            lines.Add(ResultLine(customId, request["params"]!["messages"]![0]!["content"]!.GetValue<string>()));
        }
        lines.Reverse();
        _resultLines[id] = lines;
        _polls[id] = 0;
        return JsonSerializer.Serialize(new { id });
    }

    string ResultLine(string customId, string documentText)
    {
        var fails = _alwaysFail.Contains(customId);
        if (!fails && _failuresLeft.TryGetValue(customId, out var left) && left > 0)
        {
            _failuresLeft[customId] = left - 1;
            fails = true;
        }
        object result = fails
            ? new { type = "errored", error = new { message = "overloaded" } }
            : new { type = "succeeded", message = new { content = new[] { new { type = "text", text = $"Summary: {documentText}" } } } };
        return JsonSerializer.Serialize(new { custom_id = customId, result });
    }

    string Status(string id)
    {
        var polls = ++_polls[id];
        var total = _resultLines[id].Count;
        if (_canceled.Contains(id))
            return JsonSerializer.Serialize(new { processing_status = "canceled", request_counts = new { processing = 0, succeeded = 0, errored = 0, canceled = total, expired = 0 } });
        var ended = polls > _pollsBeforeEnded && !_neverEnd.Contains(id);
        return JsonSerializer.Serialize(new
        {
            processing_status = ended ? "ended" : "in_progress",
            request_counts = new
            {
                processing = ended ? 0 : total,
                succeeded = ended ? total : 0,
                errored = 0,
                canceled = 0,
                expired = 0,
            },
        });
    }

    static string BatchId(string path, int fromEnd) => path.Split('/')[^fromEnd];

    static Task<HttpResponseMessage> Json(string body) => Task.FromResult(Reply(body, "application/json"));

    static HttpResponseMessage Reply(string body, string contentType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };
}
