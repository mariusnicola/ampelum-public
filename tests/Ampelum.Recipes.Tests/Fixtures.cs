using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;

namespace Ampelum.Recipes.Tests;

/// <summary>Shared signature used by recipes; declared once so individual recipes don't redeclare.</summary>
[Signature("Reply to a greeting with a one-word reply.")]
public sealed record HelloSig : ISignature
{
    /// <summary>Input greeting.</summary>
    [Input] public string Greeting { get; init; } = "";
    /// <summary>Output reply.</summary>
    [Output] public string Reply { get; init; } = "";

    /// <summary>Init ctor for ergonomics in recipes.</summary>
    public HelloSig() { }
    /// <summary>Convenience ctor used by recipe code.</summary>
    /// <param name="greeting">Greeting text.</param>
    public HelloSig(string greeting) { Greeting = greeting; }
}

/// <summary>Signature with a single output, used by ReAct + Evaluate recipes.</summary>
[Signature("Answer the math question.")]
public sealed record MathQa : ISignature
{
    /// <summary>Question text.</summary>
    [Input] public string Question { get; init; } = "";
    /// <summary>Answer text.</summary>
    [Output] public string Answer { get; init; } = "";
}

/// <summary>Trivial optimizer-side metric returning constant 1.0.</summary>
public sealed class ExactMatchOptMetric : SignalGrid.Ampelum.Optimizers.IMetric
{
    /// <summary>Always returns 1.0.</summary>
    public double Score(object actual, object expected, SignatureMetadata signature) => 1.0;
}

/// <summary>Minimal failing chat client â€” every call throws. Used by failover recipe.</summary>
public sealed class AlwaysThrowingChatClient : IChatClient
{
    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromException<ChatResponse>(new InvalidOperationException("primary down"));
    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("primary down");
    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    /// <inheritdoc/>
    public void Dispose() { }
}

/// <summary>Custom logging hook used by the hook-composition recipe â€” appends every lifecycle phase to a list.</summary>
public sealed class RecipeLoggingHook : SignalGrid.Ampelum.Hooks.IHook
{
    /// <summary>Captured lifecycle events; cleared by the recipe between assertions.</summary>
    public List<string> Events { get; } = new();
    /// <inheritdoc/>
    public int Order { get; init; }
    /// <inheritdoc/>
    public ValueTask OnBeforeAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
    { Events.Add($"before:{ctx.ModuleName}"); return ValueTask.CompletedTask; }
    /// <inheritdoc/>
    public ValueTask OnAfterAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
    { Events.Add($"after:{ctx.ModuleName}"); return ValueTask.CompletedTask; }
    /// <inheritdoc/>
    public ValueTask OnErrorAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, Exception error, CancellationToken ct)
    { Events.Add($"error:{ctx.ModuleName}:{error.GetType().Name}"); return ValueTask.CompletedTask; }
}

/// <summary>Hook that short-circuits the LM call by writing <c>HookContext.Outputs</c> in <c>OnBefore</c>.</summary>
public sealed class RecipeShortCircuitHook : SignalGrid.Ampelum.Hooks.IHook
{
    readonly IReadOnlyDictionary<string, object?> _cached;
    /// <summary>Constructs a short-circuit hook that surfaces <paramref name="cached"/> as the parsed outputs.</summary>
    public RecipeShortCircuitHook(IReadOnlyDictionary<string, object?> cached, int order)
    { _cached = cached; Order = order; }
    /// <inheritdoc/>
    public int Order { get; }
    /// <inheritdoc/>
    public ValueTask OnBeforeAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
    { ctx.Outputs = _cached; return ValueTask.CompletedTask; }
    /// <inheritdoc/>
    public ValueTask OnAfterAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
    /// <inheritdoc/>
    public ValueTask OnErrorAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, Exception error, CancellationToken ct) => ValueTask.CompletedTask;
}

#pragma warning disable AMPELUM_V09_PREVIEW
/// <summary>Minimal scripted agent for the group-chat recipe.</summary>
public sealed class RecipeScriptedAgent : SignalGrid.Ampelum.Agents.IAgent
{
    readonly Queue<SignalGrid.Ampelum.Agents.AgentMessage> _replies;
    /// <summary>Constructs a scripted agent that emits the given replies in order.</summary>
    public RecipeScriptedAgent(string name, string role, IEnumerable<SignalGrid.Ampelum.Agents.AgentMessage> replies)
    { Name = name; Role = role; _replies = new Queue<SignalGrid.Ampelum.Agents.AgentMessage>(replies); }
    /// <inheritdoc/>
    public string Name { get; }
    /// <inheritdoc/>
    public string Role { get; }
    /// <inheritdoc/>
    public ValueTask<SignalGrid.Ampelum.Agents.AgentMessage> ReplyAsync(SignalGrid.Ampelum.Agents.ConversationContext ctx, CancellationToken cancellationToken)
        => ValueTask.FromResult(_replies.Dequeue());
#pragma warning restore AMPELUM_V09_PREVIEW
}
