using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>Thrown when the session budget has no room for another model call.</summary>
public sealed class BudgetExhaustedException(string message) : InvalidOperationException(message)
{
    /// <summary>Finds the budget refusal in an exception or its inner chain, as when a module wraps what the chat client threw.</summary>
    public static BudgetExhaustedException? Find(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is BudgetExhaustedException budget)
                return budget;
        return null;
    }
}

/// <summary>Counts model calls and tokens against a <see cref="SessionBudget"/> with the library's <see cref="SessionBudgetMeter"/>, the same meter <c>ToolDispatcher</c> and <c>SubAgentRunner</c> take.</summary>
public sealed class ModelBudget(SessionBudget limits)
{
    readonly SessionBudgetMeter _meter = new(limits);

    /// <summary>The meter itself, so the tool dispatcher and the sub-agent runner count against the same budget.</summary>
    public SessionBudgetMeter Meter => _meter;

    /// <summary>Calls let through so far.</summary>
    public long Calls => _meter.Calls;

    /// <summary>Tokens reported by the provider so far.</summary>
    public long Tokens => _meter.Tokens;

    /// <summary>Calls turned away because the budget was used up.</summary>
    public long Refused => _meter.Refused;

    /// <summary>True when the next call would be refused.</summary>
    public bool IsExhausted => _meter.IsExhausted;

    /// <summary>Claims one call. Returns false, and counts a refusal, when no room is left.</summary>
    public bool TryReserve() => _meter.TryReserveCall();

    /// <summary>Adds the tokens a finished call used.</summary>
    public void AddTokens(long tokens) => _meter.AddTokens(tokens);
}

/// <summary>Sends every call to one model id, whatever the caller put in the options.</summary>
public sealed class PinnedModelClient(IChatClient inner, string modelId) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var pinned = options?.Clone() ?? new ChatOptions();
        pinned.ModelId = modelId;
        return base.GetResponseAsync(messages, pinned, cancellationToken);
    }
}

/// <summary>Refuses a call once the session budget is used up, and books the tokens of each answer.</summary>
public sealed class BudgetedChatClient(IChatClient inner, ModelBudget budget) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!budget.TryReserve())
            throw new BudgetExhaustedException($"Session budget used up after {budget.Calls} calls and {budget.Tokens} tokens.");
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        budget.AddTokens(response.Usage?.TotalTokenCount ?? 0);
        return response;
    }
}
