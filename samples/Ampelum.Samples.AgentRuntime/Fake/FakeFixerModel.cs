using Microsoft.Extensions.AI;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>
/// Stand-in for the coding sub-agent's model. It plays a fixed script, one message per model call, and the script
/// holds the tool calls too. It proves the wiring around the runner and its tools, not any model quality.
/// </summary>
public sealed class FakeFixerModel(IEnumerable<ChatMessage> script) : IChatClient
{
    readonly object _gate = new();
    readonly Queue<ChatMessage> _script = new(script);
    readonly List<string> _prompts = [];
    readonly List<IReadOnlyList<string>> _toolNames = [];

    /// <summary>The text each call received, tool results included, in arrival order.</summary>
    public IReadOnlyList<string> Prompts
    {
        get { lock (_gate) return [.. _prompts]; }
    }

    /// <summary>The tool names the runner offered on each call.</summary>
    public IReadOnlyList<IReadOnlyList<string>> ToolNames
    {
        get { lock (_gate) return [.. _toolNames]; }
    }

    /// <summary>Model calls so far. A tool call and the answer that follows it are two calls.</summary>
    public int Calls
    {
        get { lock (_gate) return _prompts.Count; }
    }

    /// <summary>The scripted fix for the pricing issue: read the file, write it back rounded, report done.</summary>
    public static IReadOnlyList<ChatMessage> PricingFix() =>
    [
        // The runner's tool parameters have no defaults, so an optional one must still be sent, as null.
        ToolCall("read-1", "read_file", ("path", IssueFixer.PricingFile), ("offset", null), ("limit", null)),
        ToolCall("write-1", "write_file", ("path", IssueFixer.PricingFile), ("content", IssueFixer.RoundedPricing), ("append", null)),
        new(ChatRole.Assistant, "DONE: ApplyDiscount now rounds the discounted amount to two decimals, away from zero."),
    ];

    /// <summary>One assistant message that asks for one tool call.</summary>
    public static ChatMessage ToolCall(string callId, string tool, params (string Name, object? Value)[] arguments) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, tool, arguments.ToDictionary(a => a.Name, a => a.Value))]);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatMessage next;
        lock (_gate)
        {
            _prompts.Add(string.Join("\n", messages.SelectMany(m => m.Contents).Select(Describe)));
            _toolNames.Add([.. options?.Tools?.Select(t => t.Name) ?? []]);
            next = _script.Count > 0
                ? _script.Dequeue()
                : throw new InvalidOperationException("FakeFixerModel script is used up.");
        }
        return Task.FromResult(new ChatResponse(next));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The sub-agent does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    static string Describe(AIContent content) => content switch
    {
        TextContent text => text.Text,
        FunctionResultContent result => $"[result {result.CallId}] {result.Result}",
        FunctionCallContent call => $"[call {call.Name}]",
        _ => string.Empty,
    };
}
