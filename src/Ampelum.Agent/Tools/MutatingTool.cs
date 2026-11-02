namespace SignalGrid.Ampelum.Agent;

/// <summary>Base class for compiled C# tool handlers that require observation tokens.
/// Provides belt-and-suspenders validation: ToolDispatcher validates first, this class
/// validates again in case ToolDispatcher validation is bypassed.
/// Callers implement ExecuteCoreAsync — token validation happens automatically.</summary>
public abstract class MutatingTool<TParams> where TParams : IHasObservationToken
{
    private readonly ITokenStore _tokenStore;
    private readonly AgentIdentity _session;

    protected MutatingTool(ITokenStore tokenStore, AgentIdentity session)
    {
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(session);
        _tokenStore = tokenStore;
        _session = session;
    }

    /// <summary>Execute the tool. Validates observation token before calling ExecuteCoreAsync.
    /// Returns a blocked ToolResult if token is invalid, expired, or from wrong session.</summary>
    public async Task<ToolResult> ExecuteAsync(TParams parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // Belt-and-suspenders: validate token (ToolDispatcher already validated — this is the backstop)
        var validationError = ValidateToken(parameters);
        if (validationError is not null)
        {
            return new ToolResult(false, null, validationError, null);
        }

        return await ExecuteCoreAsync(parameters, ct);
    }

    /// <summary>Implement tool logic here. Token is already validated when this is called.</summary>
    protected abstract Task<ToolResult> ExecuteCoreAsync(TParams parameters, CancellationToken ct = default);

    private string? ValidateToken(TParams parameters)
    {
        if (string.IsNullOrEmpty(parameters.Token))
            return "Observation token is required but was not provided.";

        var token = _tokenStore.Resolve(parameters.Token);
        if (token is null)
            return $"Observation token '{parameters.Token}' is invalid or has already been consumed.";

        if (token.SessionId != _session.InstanceId)
            return $"Observation token '{parameters.Token}' was issued for a different session.";

        // Check call-index expiry if using InMemoryTokenStore
        if (_tokenStore is InMemoryTokenStore memoryStore)
        {
            if (memoryStore.CallIndex > token.ExpiresAtCallIndex)
                return $"Observation token '{parameters.Token}' for path '{token.ResourcePath}' has expired.";
        }

        return null;
    }
}
