using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Ampelum.Samples.DocumentReview;

/// <summary>One request line the fake batch API received inside an uploaded file.</summary>
internal sealed record BatchLine(string CustomId, string Model, string UserText);

/// <summary>
/// Plays the batch API: file upload, batch create, status, output file and cancel. A batch reports
/// in progress for a fixed number of status calls, then completed; a cancelled batch reports cancelled.
/// The output file holds one summary per uploaded line, built from the text that line carried.
/// </summary>
internal sealed class FakeBatchHandler(string expectedKey, int inProgressPolls) : HttpMessageHandler
{
    sealed class Batch(List<BatchLine> lines)
    {
        public List<BatchLine> Lines { get; } = lines;
        public int StatusCalls { get; set; }
        public bool Cancelled { get; set; }
    }

    readonly Dictionary<string, List<BatchLine>> _files = [];
    readonly Dictionary<string, Batch> _batches = [];
    readonly List<string> _calls = [];
    readonly List<BatchLine> _submitted = [];

    /// <summary>Every call as "METHOD path", in order.</summary>
    public IReadOnlyList<string> Calls => _calls;

    public IReadOnlyList<BatchLine> Submitted => _submitted;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.ToString() != $"Bearer {expectedKey}")
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var path = request.RequestUri!.AbsolutePath;
        _calls.Add($"{request.Method} {path}");
        const string root = "/v1/";
        if (!path.StartsWith(root, StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        var route = path[root.Length..];

        if (request.Method == HttpMethod.Post && route == "files")
        {
            var upload = await request.Content!.ReadAsStringAsync(cancellationToken);
            var lines = upload.Split('\n', StringSplitOptions.TrimEntries)
                .Where(l => l.StartsWith("{\"custom_id\"", StringComparison.Ordinal))
                .Select(ParseLine)
                .ToList();
            var fileId = $"file-{_files.Count + 1}";
            _files[fileId] = lines;
            return Json(new JsonObject { ["id"] = fileId });
        }

        if (request.Method == HttpMethod.Post && route == "batches")
        {
            var create = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            if (!_files.TryGetValue(create["input_file_id"]!.GetValue<string>(), out var lines))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            var batchId = $"batch-{_batches.Count + 1}";
            _batches[batchId] = new Batch(lines);
            _submitted.AddRange(lines);
            return Json(new JsonObject { ["id"] = batchId });
        }

        if (request.Method == HttpMethod.Get && route.StartsWith("batches/", StringComparison.Ordinal))
        {
            if (!_batches.TryGetValue(route["batches/".Length..], out var batch))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            batch.StatusCalls++;
            var total = batch.Lines.Count;
            if (batch.Cancelled)
                return Json(Status("cancelled", 0, total));
            if (batch.StatusCalls <= inProgressPolls)
                return Json(Status("in_progress", 0, total));
            var done = Status("completed", total, total);
            done["output_file_id"] = "out-" + route["batches/".Length..];
            return Json(done);
        }

        if (request.Method == HttpMethod.Post && route.StartsWith("batches/", StringComparison.Ordinal) && route.EndsWith("/cancel", StringComparison.Ordinal))
        {
            var id = route["batches/".Length..^"/cancel".Length];
            if (!_batches.TryGetValue(id, out var batch))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            batch.Cancelled = true;
            return Json(new JsonObject { ["id"] = id, ["status"] = "cancelling" });
        }

        if (request.Method == HttpMethod.Get && route.StartsWith("files/out-", StringComparison.Ordinal) && route.EndsWith("/content", StringComparison.Ordinal))
        {
            var id = route["files/out-".Length..^"/content".Length];
            if (!_batches.TryGetValue(id, out var batch))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var output = new StringBuilder();
            foreach (var line in batch.Lines)
            {
                var summary = $"Summary of {line.CustomId}: {Truncate(line.UserText, 48)}";
                output.Append(new JsonObject
                {
                    ["custom_id"] = line.CustomId,
                    ["response"] = new JsonObject
                    {
                        ["body"] = new JsonObject
                        {
                            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = summary } }),
                        },
                    },
                }.ToJsonString()).Append('\n');
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(output.ToString(), Encoding.UTF8, "application/jsonl") };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    static JsonObject Status(string status, int completed, int total) => new()
    {
        ["status"] = status,
        ["request_counts"] = new JsonObject { ["completed"] = completed, ["failed"] = 0, ["total"] = total },
    };

    static BatchLine ParseLine(string line)
    {
        var json = JsonNode.Parse(line)!;
        var body = json["body"]!;
        var user = body["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "user")!;
        return new BatchLine(json["custom_id"]!.GetValue<string>(), body["model"]!.GetValue<string>(), user["content"]!.GetValue<string>());
    }

    static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    static HttpResponseMessage Json(JsonNode node) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"),
    };
}
