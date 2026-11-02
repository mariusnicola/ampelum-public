using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks.Internal;

namespace SignalGrid.Ampelum.Hooks;

/// <summary>
/// Runs a single LM call through the same hook pipeline (budget/audit/PII/prompt-injection/
/// moderation/content-policy/semantic-cache) that <c>Predict&lt;TSig&gt;.ForwardAsync</c> drives,
/// for callers that need direct chat access without the Adapter/Parse/retry machinery those
/// callers don't need (ReAct, optimizers, retrieval patterns, etc.).
/// </summary>
/// <remarks>
/// <see cref="HookContext.Inputs"/> is a flat string-keyed dictionary, one entry per message
/// (key <c>"message.{index}.{role}"</c>, value the message text), and on a real LM call
/// <see cref="HookContext.Outputs"/> is set to <c>{ "response": responseText }</c> — both a
/// plain string, not the raw <see cref="ChatMessage"/>/<see cref="ChatResponse"/> objects.
/// <see cref="Safety.Pii.PiiRedactionHook"/> and <see cref="Safety.PromptInjection.PromptInjectionHook"/>
/// only inspect dictionary values typed as <see langword="string"/>, so anything richer would be
/// invisible to them.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class HookedLmCall
{
    /// <summary>Key under which the response text is published on <see cref="HookContext.Outputs"/>.</summary>
    public const string ResponseOutputKey = "response";

    /// <summary>
    /// Calls <paramref name="context"/>'s <see cref="AmpelumContext.Chat"/> with
    /// <paramref name="messages"/>, running <paramref name="context"/>'s
    /// <see cref="AmpelumContext.Hooks"/> pipeline (if any) around the call exactly like
    /// <c>Predict&lt;TSig&gt;.ForwardAsync</c> does: <c>OnBefore</c> first (an OnBefore hook that
    /// populates <see cref="HookContext.Outputs"/> — e.g. a semantic-cache hit — short-circuits
    /// the LM call), then the LM call itself (wrapped in <see cref="LmCallException"/> on
    /// failure), then <c>OnAfter</c>. Any exception raised after the hook context exists runs
    /// <c>OnError</c> before being rethrown.
    /// </summary>
    /// <param name="context">Supplies the chat client and the hook pipeline to run.</param>
    /// <param name="moduleName">Module name recorded on the hook context (e.g. "ReAct", "MIPROv2").</param>
    /// <param name="messages">The chat messages to send.</param>
    /// <param name="options">Optional chat options passed through to <see cref="IChatClient.GetResponseAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The LM's <see cref="ChatResponse"/> (returned as-is, tool-call metadata and all) on a real
    /// call, or a response synthesized from <see cref="HookContext.Outputs"/> when an OnBefore
    /// hook short-circuited the call.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="moduleName"/>, or <paramref name="messages"/> is null.
    /// </exception>
    /// <exception cref="LmCallException">The underlying <see cref="IChatClient.GetResponseAsync"/> call failed.</exception>
    public static async Task<ChatResponse> InvokeAsync(
        AmpelumContext context,
        string moduleName,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(moduleName);
        ArgumentNullException.ThrowIfNull(messages);

        if (context.Hooks is not { Count: > 0 } hookList)
            return await context.Chat.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var pipeline = HookPipeline.GetOrCreate(hookList);
        var hookCtx = new HookContext
        {
            ModuleName = moduleName,
            Inputs = FlattenMessages(messages),
        };

        try
        {
            await pipeline.RunBeforeAsync(hookCtx, cancellationToken).ConfigureAwait(false);

            // PSQ-11-style short-circuit: an OnBefore hook (e.g. SemanticCacheHook) populated
            // Outputs — skip the LM call, but still run OnAfter.
            if (hookCtx.Outputs is not null)
            {
                var cachedText = hookCtx.Outputs.TryGetValue(ResponseOutputKey, out var cached) && cached is string s
                    ? s
                    : string.Empty;
                var shortCircuited = new ChatResponse(new ChatMessage(ChatRole.Assistant, cachedText));
                await pipeline.RunAfterAsync(hookCtx, cancellationToken).ConfigureAwait(false);
                return shortCircuited;
            }

            ChatResponse response;
            var lmStartTs = Stopwatch.GetTimestamp();
            try
            {
                response = await context.Chat.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new LmCallException("IChatClient.GetResponseAsync failed.", ex);
            }
            var lmElapsed = Stopwatch.GetElapsedTime(lmStartTs);

            hookCtx.Set(HookMetadataKeys.ModelId, response.ModelId ?? options?.ModelId ?? "unknown");
            hookCtx.Set(HookMetadataKeys.PromptTokens, response.Usage?.InputTokenCount ?? 0L);
            hookCtx.Set(HookMetadataKeys.CompletionTokens, response.Usage?.OutputTokenCount ?? 0L);
            hookCtx.Set(HookMetadataKeys.LmCallDurationMs, lmElapsed.TotalMilliseconds);

            hookCtx.Outputs = new Dictionary<string, object?> { [ResponseOutputKey] = response.Text };

            await pipeline.RunAfterAsync(hookCtx, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex)
        {
            await pipeline.RunErrorAsync(hookCtx, ex, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    static Dictionary<string, object?> FlattenMessages(IReadOnlyList<ChatMessage> messages)
    {
        var dict = new Dictionary<string, object?>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            dict[$"message.{i}.{m.Role.ToString().ToLowerInvariant()}"] = m.Text;
        }
        return dict;
    }
}
