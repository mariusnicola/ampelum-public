using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ampelum.Samples.ComposedPrograms.Fake;

/// <summary>One request as the model received it.</summary>
public sealed record ModelCall(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)
{
    /// <summary>Text of all system messages, which carries the signature instructions.</summary>
    public string System => string.Join("\n", Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));

    /// <summary>Text of the last user message, which carries the input fields.</summary>
    public string User => Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

    public string All => string.Join("\n", Messages.Select(m => m.Text));

    /// <summary>True when the system text or the call options mention the phrase.</summary>
    public bool Mentions(string phrase) =>
        System.Contains(phrase, StringComparison.Ordinal)
        || (Options?.Instructions?.Contains(phrase, StringComparison.Ordinal) ?? false);
}

/// <summary>
/// A chat client for fake mode and tests. Each reply is chosen from the request by the first rule that
/// matches, so concurrent calls still get the right answer, and every request is kept for inspection.
/// The answers come from these rules, not from a model.
/// </summary>
public sealed class ScriptedModel : IChatClient
{
    readonly List<(string Name, Func<ModelCall, ChatMessage?> Rule)> _rules = [];
    readonly List<ModelCall> _calls = [];
    readonly object _gate = new();

    public IReadOnlyList<ModelCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>Adds a rule that answers with plain text, or returns null to leave the call to later rules.</summary>
    public ScriptedModel When(string name, Func<ModelCall, string?> rule) =>
        WhenMessage(name, call => rule(call) is { } text ? new ChatMessage(ChatRole.Assistant, text) : null);

    /// <summary>Adds a rule that answers with a full message, for example one that calls a tool.</summary>
    public ScriptedModel WhenMessage(string name, Func<ModelCall, ChatMessage?> rule)
    {
        _rules.Add((name, rule));
        return this;
    }

    /// <summary>Copies the rules of another script after this one's own.</summary>
    public ScriptedModel Include(ScriptedModel other)
    {
        _rules.AddRange(other._rules);
        return this;
    }

    ChatMessage Answer(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var call = new ModelCall(messages.ToArray(), options);
        lock (_gate) _calls.Add(call);
        foreach (var (_, rule) in _rules)
            if (rule(call) is { } answer)
                return answer;
        var excerpt = call.All.Length > 200 ? call.All[..200] : call.All;
        throw new InvalidOperationException($"ScriptedModel: no rule answers this request: {excerpt}");
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(Answer(messages, options)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var answer = Answer(messages, options);
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, answer.Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
