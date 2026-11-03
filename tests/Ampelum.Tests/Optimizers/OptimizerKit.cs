using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;

namespace Ampelum.Tests.Optimizers;

[Signature("Given a question, produce an answer.")]
public sealed record KitSig : ISignature
{
    [Input] public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

/// <summary>Helpers for the optimizer tests that run a real Predict against a scripted model.</summary>
internal static class Kit
{
    public static AmpelumContext Context(IChatClient chat, ITraceSink? sink = null) =>
        new(chat, new ChatAdapter(), sink ?? NullTraceSink.Instance, NullLoggerFactory.Instance);

    public static Example Qa(string question, string answer) =>
        new(new Dictionary<string, object?> { ["Question"] = question },
            new Dictionary<string, object?> { ["Answer"] = answer });

    public static string Answer(Example example) => (string)example.Outputs["Answer"]!;

    public static string Question(Example example) => (string)example.Inputs["Question"]!;

    public static string SystemText(IEnumerable<ChatMessage> messages) =>
        messages.First(m => m.Role == ChatRole.System).Text;

    public static string UserText(IEnumerable<ChatMessage> messages) =>
        messages.Last(m => m.Role == ChatRole.User).Text;

    /// <summary>Runs the Predict and writes its trace to the sink of the context the optimizer gave.</summary>
    public static async Task<Example> RunAsync(
        Predict<KitSig> program, Example example, AmpelumContext context, CancellationToken ct)
    {
        var prediction = await program.ForwardAsync(new KitSig { Question = Question(example) }, ct: ct);
        context.TraceSink.Write(prediction.Trace);
        return Example.From(prediction.Value);
    }

    public static Task Trace(Predict<KitSig> program, Example example, AmpelumContext context, CancellationToken ct) =>
        RunAsync(program, example, context, ct);
}

/// <summary>1 when the Answer field matches, else 0.</summary>
internal sealed class AnswerMatch : IMetric
{
    public double Score(object actual, object expected, SignatureMetadata signature) =>
        Equals(((IReadOnlyDictionary<string, object?>)actual)["Answer"],
               ((IReadOnlyDictionary<string, object?>)expected)["Answer"]) ? 1.0 : 0.0;
}

/// <summary>A scripted model: the reply is chosen by a function of the messages it receives.</summary>
internal sealed class ScriptedModel(Func<IReadOnlyList<ChatMessage>, string> answer) : IChatClient
{
    readonly object _gate = new();
    readonly List<IReadOnlyList<ChatMessage>> _calls = [];

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        lock (_gate) _calls.Add(list);
        return Task.FromResult(new ChatAdapter().FormatResponse(
            SignatureMetadataCache.For<KitSig>(),
            new Dictionary<string, object?> { ["Answer"] = answer(list) }));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A model that always replies with the given texts in turn (the last one repeats).</summary>
internal sealed class TextModel(params string[] replies) : IChatClient
{
    readonly object _gate = new();
    readonly List<IReadOnlyList<ChatMessage>> _calls = [];

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var reply = replies[Math.Min(_calls.Count, replies.Length - 1)];
            _calls.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
