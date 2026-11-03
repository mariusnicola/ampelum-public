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

/// <summary>Counts model calls and tokens against a <see cref="SessionBudget"/>. The budget type only answers questions; this class does the counting and the refusing.</summary>
public sealed class ModelBudget(SessionBudget limits)
{
    readonly object _gate = new();
    long _calls;
    long _tokens;
    long _refused;

    /// <summary>Calls let through so far.</summary>
    public long Calls { get { lock (_gate) return _calls; } }

    /// <summary>Tokens reported by the provider so far.</summary>
    public long Tokens { get { lock (_gate) return _tokens; } }

    /// <summary>Calls turned away because the budget was used up.</summary>
    public long Refused { get { lock (_gate) return _refused; } }

    /// <summary>True when the next call would be refused.</summary>
    public bool IsExhausted { get { lock (_gate) return limits.IsExhausted(_calls, _tokens, 0); } }

    /// <summary>Claims one call. Returns false, and counts a refusal, when no room is left.</summary>
    public bool TryReserve()
    {
        lock (_gate)
        {
            if (limits.IsExhausted(_calls, _tokens, 0))
            {
                _refused++;
                return false;
            }
            _calls++;
            return true;
        }
    }

    /// <summary>Adds the tokens a finished call used.</summary>
    public void AddTokens(long tokens)
    {
        lock (_gate)
            _tokens += tokens;
    }
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
            throw new BudgetExhaustedException($"Model budget used up after {budget.Calls} calls and {budget.Tokens} tokens.");
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        budget.AddTokens(response.Usage?.TotalTokenCount ?? 0);
        return response;
    }
}
