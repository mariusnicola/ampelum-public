namespace SignalGrid.Ampelum.Agent;

/// <summary>Counts calls, tokens and output bytes against a <see cref="SessionBudget"/> and refuses a call once any
/// limit is used up. <see cref="ToolDispatcher"/> and <c>SubAgentRunner</c> take one to enforce the budget; the
/// duration limit counts from <see cref="SessionBudget.StartedAt"/>. Safe to share across threads.</summary>
public sealed class SessionBudgetMeter(SessionBudget limits)
{
    readonly object _gate = new();
    long _calls;
    long _tokens;
    long _outputBytes;
    long _refused;

    /// <summary>Calls let through so far.</summary>
    public long Calls { get { lock (_gate) return _calls; } }

    /// <summary>Tokens added so far.</summary>
    public long Tokens { get { lock (_gate) return _tokens; } }

    /// <summary>Output bytes added so far.</summary>
    public long OutputBytes { get { lock (_gate) return _outputBytes; } }

    /// <summary>Calls turned away because the budget was used up.</summary>
    public long Refused { get { lock (_gate) return _refused; } }

    /// <summary>True when the next call would be refused.</summary>
    public bool IsExhausted { get { lock (_gate) return limits.IsExhausted(_calls, _tokens, _outputBytes); } }

    /// <summary>Claims one call. Returns false, and counts a refusal, when no room is left.</summary>
    public bool TryReserveCall()
    {
        lock (_gate)
        {
            if (limits.IsExhausted(_calls, _tokens, _outputBytes))
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

    /// <summary>Adds the bytes of a tool result.</summary>
    public void AddOutputBytes(long bytes)
    {
        lock (_gate)
            _outputBytes += bytes;
    }
}
