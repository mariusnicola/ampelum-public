using System.Net;
using System.Text;
using System.Text.Json;
using SignalGrid.Ampelum.Agent.A2A;

namespace Ampelum.Samples.DocumentReview;

/// <summary>One message the fake remote agent received.</summary>
internal sealed record AgentCall(string Method, string Text);

/// <summary>
/// Plays the remote reviewer agent: it serves the agent card and answers the task send call with a
/// completed task whose reply quotes the text it was sent, so a request without the clause gets a wrong reply.
/// </summary>
internal sealed class FakeAgentHandler : HttpMessageHandler
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly List<AgentCall> _calls = [];
    readonly List<string> _cardRequests = [];

    public IReadOnlyList<AgentCall> Calls => _calls;

    public IReadOnlyList<string> CardRequests => _cardRequests;

    /// <summary>Address the card announces for task calls.</summary>
    public Uri CardUrl { get; set; } = FakeData.AgentRpc;

    /// <summary>When set, task calls fail with a JSON-RPC error carrying this message.</summary>
    public string? RpcError { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (request.Method == HttpMethod.Get && path == A2AMethods.AgentCardPath)
        {
            _cardRequests.Add(path);
            var card = new A2AAgentCard
            {
                Name = "supplier-clause-reviewer",
                Description = "Reviews supplier contract clauses that automatic ranking cannot settle.",
                Version = "1",
                Url = CardUrl.ToString(),
                Skills = [new A2AAgentSkill { Id = FakeData.AgentSkill, Name = "Clause review" }],
            };
            return Reply(card);
        }

        if (request.Method == HttpMethod.Post && path == FakeData.AgentRpc.AbsolutePath)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            var method = root.GetProperty("method").GetString() ?? "";
            var id = root.GetProperty("id").GetString();
            if (method != A2AMethods.SendMessage)
                return new HttpResponseMessage(HttpStatusCode.BadRequest);

            var text = root.GetProperty("params").GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            _calls.Add(new AgentCall(method, text));

            if (RpcError is not null)
                return Reply(new A2AResponse { Id = id, Error = new A2AError { Code = -32000, Message = RpcError } });

            var now = DateTimeOffset.UnixEpoch;
            var task = new A2ATask
            {
                Id = "task-" + id,
                State = TaskState.Completed,
                Messages = new MessageCollection(
                [
                    new A2AMessage
                    {
                        Id = "reply-" + id,
                        Role = MessageRole.Agent,
                        Parts = [new A2APart { Text = $"Needs legal sign-off. Reviewed: {text}" }],
                    },
                ]),
                CreatedAt = now,
                UpdatedAt = now,
            };
            return Reply(new A2AResponse { Id = id, Result = new A2AResult { Task = task } });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
    };
}
