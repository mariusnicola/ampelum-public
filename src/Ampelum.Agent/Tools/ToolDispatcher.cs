using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Central, generic enforcement point for tool invocation. It evaluates
/// policy, enforces confirmation and observation tokens, invokes handlers, and
/// records every decision and outcome under one trace/workflow correlation.</summary>
public class ToolDispatcher : IToolDispatcher
{
    private readonly IToolLoader _loader;
    private readonly ITokenStore _tokenStore;
    private readonly PolicyEngine _policyEngine;
    private readonly AgentIdentity _currentSession;
    private readonly int _defaultTokenExpiry;
    private readonly IConventionEnforcer? _conventionEnforcer;
    private readonly IToolAuditLog _auditLog;
    private readonly IToolConfirmationStore _confirmationStore;
    private readonly TimeSpan _confirmationTtl;
    private readonly string _workspaceId;
    private readonly string _issuerId;
    private readonly SessionBudgetMeter? _budget;
    private readonly Dictionary<string, HashSet<string>> _listingResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<ToolResult>>> _handlers = new(StringComparer.Ordinal);

    /// <summary>The configured append-only audit sink. Use a durable sink such as
    /// <see cref="SqliteToolAuditLog"/> for processes that must survive restart.</summary>
    public IToolAuditLog AuditLog => _auditLog;

    public ToolDispatcher(
        IToolLoader loader,
        ITokenStore tokenStore,
        PolicyEngine policyEngine,
        AgentIdentity currentSession,
        int defaultTokenExpiry = 10,
        IConventionEnforcer? conventionEnforcer = null,
        IToolAuditLog? auditLog = null,
        IToolConfirmationStore? confirmationStore = null,
        TimeSpan? confirmationTtl = null,
        string? workspaceId = null,
        string? issuerId = null,
        SessionBudgetMeter? budget = null)
    {
        _budget = budget;
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
        _currentSession = currentSession ?? throw new ArgumentNullException(nameof(currentSession));
        if (defaultTokenExpiry < 1) throw new ArgumentOutOfRangeException(nameof(defaultTokenExpiry));
        _defaultTokenExpiry = defaultTokenExpiry;
        _conventionEnforcer = conventionEnforcer;
        _auditLog = auditLog ?? new InMemoryToolAuditLog();
        _confirmationStore = confirmationStore ?? new InMemoryToolConfirmationStore();
        _confirmationTtl = confirmationTtl ?? TimeSpan.FromMinutes(5);
        if (_confirmationTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(confirmationTtl));
        _workspaceId = string.IsNullOrWhiteSpace(workspaceId) ? "default" : workspaceId;
        _issuerId = string.IsNullOrWhiteSpace(issuerId) ? currentSession.AgentType : issuerId;
    }

    public void RegisterHandler(string toolName, Func<IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<ToolResult>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[toolName] = handler;
    }

    /// <summary>True when a handler is already registered under <paramref name="toolName"/>.</summary>
    public bool HasHandler(string toolName) => _handlers.ContainsKey(toolName);

    public Task<ToolResult> InvokeAsync(string toolName, IReadOnlyDictionary<string, JsonElement> args, AgentIdentity caller, CancellationToken ct = default) =>
        InvokeAsync(toolName, args, caller, context: null, ct);

    /// <summary>Invokes a tool with runtime correlation and, after a policy gate,
    /// an approved confirmation id. Confirmation ids are one invocation's scoped
    /// authorization and cannot be reused for another caller/tool/trace.</summary>
    public async Task<ToolResult> InvokeAsync(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> args,
        AgentIdentity caller,
        ToolInvocationContext? context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(caller);

        var correlation = Normalize(context);
        var safeArgs = SafeAuditArgs(args);
        var manifest = await _loader.TryUnpackAsync(toolName, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            var result = Failure(toolName, $"Unknown tool '{toolName}'.");
            await AuditAsync(toolName, caller, safeArgs, PolicyDecision.Blocked($"Tool '{toolName}' not found."), result, correlation, "policy.blocked", null, null).ConfigureAwait(false);
            return result;
        }

        var decision = _policyEngine.Evaluate(manifest, new PolicyContext { Caller = caller, Args = PolicyArgs(args) });
        await AuditAsync(toolName, caller, safeArgs, decision, null, correlation, "policy.decision", correlation.ConfirmationId, null).ConfigureAwait(false);
        if (decision.Decision == Decision.Block)
        {
            var result = Failure(toolName, decision.Reason ?? "Tool is blocked by policy.");
            await AuditAsync(toolName, caller, safeArgs, decision, result, correlation, "invocation.blocked", correlation.ConfirmationId, null).ConfigureAwait(false);
            return result;
        }

        if (decision.Decision == Decision.RequireConfirmation)
        {
            var confirmation = await ResolveConfirmationAsync(toolName, caller, correlation, ct).ConfigureAwait(false);
            if (confirmation is null || confirmation.Status != ToolConfirmationStatus.Approved)
            {
                var id = confirmation?.ConfirmationId ?? await CreateConfirmationAsync(toolName, caller, correlation, ct).ConfigureAwait(false);
                var eventType = confirmation is null ? "confirmation.requested" : "confirmation.not-approved";
                var result = Failure(toolName, $"Confirmation required for '{toolName}'. ConfirmationId: {id}.");
                await AuditAsync(toolName, caller, safeArgs, decision, result, correlation with { ConfirmationId = id }, eventType, id, null).ConfigureAwait(false);
                return result;
            }

            decision = PolicyDecision.Allowed;
            await AuditAsync(toolName, caller, safeArgs, decision, null, correlation, "confirmation.approved", confirmation.ConfirmationId, null).ConfigureAwait(false);
        }

        if (_conventionEnforcer is not null && manifest.DangerTier >= DangerTier.Destructive && TryExtractFileContent(args, out var filePath, out var content))
        {
            var errors = (await _conventionEnforcer.CheckAsync(filePath, content, ct).ConfigureAwait(false)).Where(x => x.Severity == "error").ToArray();
            if (errors.Length > 0)
            {
                var reason = string.Join("; ", errors.Select(x => $"{x.Rule}: {x.Message}"));
                var result = Failure(toolName, $"Convention violations: {reason}");
                var blocked = PolicyDecision.Blocked(reason);
                await AuditAsync(toolName, caller, safeArgs, blocked, result, correlation, "convention.blocked", correlation.ConfirmationId, null).ConfigureAwait(false);
                return result;
            }
        }

        var effectiveManifest = ApplySafeDefault(manifest);

        if (!_handlers.TryGetValue(toolName, out var handler))
        {
            var blocked = PolicyDecision.Blocked($"No handler for '{toolName}'.");
            var result = Failure(toolName, $"No handler registered for tool '{toolName}'.");
            await AuditAsync(toolName, caller, safeArgs, blocked, result, correlation, "handler.missing", correlation.ConfirmationId, null).ConfigureAwait(false);
            return result;
        }

        // Counted here, after policy and confirmation, so a call that never reaches its handler for those reasons
        // does not use up the budget, and before an observation token is consumed.
        if (_budget is not null && !_budget.TryReserveCall())
        {
            var blocked = PolicyDecision.Blocked("Session budget exhausted.");
            var result = Failure(toolName, "Session budget exhausted.");
            await AuditAsync(toolName, caller, safeArgs, blocked, result, correlation, "budget.exhausted", correlation.ConfirmationId, null).ConfigureAwait(false);
            return result;
        }

        string? consumedTokenId = null;
        try
        {
            consumedTokenId = ValidateObservationToken(effectiveManifest, args, correlation);
            if (consumedTokenId is not null)
                await AuditAsync(toolName, caller, safeArgs, decision, null, correlation, "observation.token.consumed", correlation.ConfirmationId, consumedTokenId).ConfigureAwait(false);
        }
        catch (MissingObservationTokenException ex)
        {
            await AuditAsync(toolName, caller, safeArgs, PolicyDecision.Blocked(ex.Message), null, correlation, "observation.token.rejected", correlation.ConfirmationId, null).ConfigureAwait(false);
            throw;
        }
        catch (InvalidOperationException ex)
        {
            var blocked = PolicyDecision.Blocked(ex.Message);
            var result = Failure(toolName, ex.Message);
            await AuditAsync(toolName, caller, safeArgs, blocked, result, correlation, "observation.token.rejected", correlation.ConfirmationId, ExtractTokenId(effectiveManifest, args)).ConfigureAwait(false);
            return result;
        }

        ToolResult handlerResult;
        try
        {
            handlerResult = await handler(StripTokenArgs(effectiveManifest, args), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var result = Failure(toolName, "Tool invocation canceled.", consumedTokenId);
            await AuditAsync(toolName, caller, safeArgs, decision, result, correlation, "handler.cancelled", correlation.ConfirmationId, null).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            var result = Failure(toolName, ex.Message, consumedTokenId);
            await AuditAsync(toolName, caller, safeArgs, decision, result, correlation, "handler.failed", correlation.ConfirmationId, null).ConfigureAwait(false);
            return result;
        }

        if (_budget is not null && handlerResult.Output is { } output)
            _budget.AddOutputBytes(System.Text.Encoding.UTF8.GetByteCount(output));

        var normalizedResult = handlerResult with { ToolName = string.IsNullOrWhiteSpace(handlerResult.ToolName) ? toolName : handlerResult.ToolName, ObservationTokenId = consumedTokenId };
        await AuditAsync(toolName, caller, safeArgs, decision, normalizedResult, correlation, normalizedResult.Success ? "handler.succeeded" : "handler.failed", correlation.ConfirmationId, null).ConfigureAwait(false);
        if (effectiveManifest.IssuesObservationToken is not null && normalizedResult.Success)
        {
            var issued = IssueObservationToken(effectiveManifest, args, normalizedResult.Output, correlation);
            normalizedResult = normalizedResult with { ObservationTokenId = issued };
            await AuditAsync(toolName, caller, safeArgs, decision, normalizedResult, correlation, "observation.token.issued", correlation.ConfirmationId, issued).ConfigureAwait(false);
        }
        await AuditAsync(toolName, caller, safeArgs, decision, normalizedResult, correlation, "invocation.completed", correlation.ConfirmationId, null).ConfigureAwait(false);
        return normalizedResult;
    }

    private async Task<ToolConfirmation?> ResolveConfirmationAsync(string toolName, AgentIdentity caller, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.ConfirmationId)) return null;
        var confirmation = await _confirmationStore.GetAsync(context.ConfirmationId, ct).ConfigureAwait(false);
        if (confirmation is null || confirmation.ToolName != toolName || confirmation.ActorId != caller.InstanceId || confirmation.TraceId != context.TraceId || confirmation.WorkflowId != context.WorkflowId)
            return null;
        return confirmation;
    }

    private async Task<string> CreateConfirmationAsync(string toolName, AgentIdentity caller, ToolInvocationContext context, CancellationToken ct)
    {
        var confirmation = new ToolConfirmation(Guid.NewGuid().ToString("N"), toolName, caller.InstanceId, context.TraceId!, context.WorkflowId!, ToolConfirmationStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Add(_confirmationTtl));
        await _confirmationStore.CreateAsync(confirmation, ct).ConfigureAwait(false);
        return confirmation.ConfirmationId;
    }

    private async Task AuditAsync(string toolName, AgentIdentity caller, IReadOnlyDictionary<string, object?> args, PolicyDecision decision, ToolResult? result, ToolInvocationContext context, string eventType, string? confirmationId, string? tokenId) =>
        await _auditLog.RecordAsync(new ToolAuditEntry(Guid.NewGuid().ToString("N"), toolName, caller.InstanceId, caller.AgentType, args, decision, result, DateTimeOffset.UtcNow, context.TraceId, context.WorkflowId, eventType, confirmationId, tokenId), CancellationToken.None).ConfigureAwait(false);

    private static ToolInvocationContext Normalize(ToolInvocationContext? value)
    {
        var trace = string.IsNullOrWhiteSpace(value?.TraceId) ? (string.IsNullOrWhiteSpace(value?.WorkflowId) ? Guid.NewGuid().ToString("N") : value!.WorkflowId!) : value!.TraceId!;
        return new ToolInvocationContext(trace, string.IsNullOrWhiteSpace(value?.WorkflowId) ? trace : value!.WorkflowId, value?.ConfirmationId, string.IsNullOrWhiteSpace(value?.InvocationId) ? Guid.NewGuid().ToString("N") : value!.InvocationId);
    }

    private static ToolResult Failure(string toolName, string error, string? tokenId = null) => new(toolName, false, null, error, DateTimeOffset.UtcNow, TimeSpan.Zero, tokenId);
    private static IReadOnlyDictionary<string, object?> SafeAuditArgs(IReadOnlyDictionary<string, JsonElement> args) => args.Keys.ToDictionary<string, string, object?>(x => x, _ => "[provided]", StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, object?> PolicyArgs(IReadOnlyDictionary<string, JsonElement> args) => args.ToDictionary(x => x.Key, x => JsonValue(x.Value), StringComparer.Ordinal);
    private static object? JsonValue(JsonElement value) => value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number when value.TryGetInt64(out var number) => number, JsonValueKind.Number => value.GetDouble(), JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null, _ => value.GetRawText() };

    private ToolManifest ApplySafeDefault(ToolManifest manifest) => manifest.DangerTier < DangerTier.Destructive || manifest.RequiresObservationToken is not null || manifest.ObservationExemption is not null ? manifest : manifest with { RequiresObservationToken = new ObservationRequirement { ArgName = "observationToken", AcceptedTypes = [] } };

    private string? ValidateObservationToken(ToolManifest manifest, IReadOnlyDictionary<string, JsonElement> args, ToolInvocationContext correlation)
    {
        var requirement = manifest.RequiresObservationToken;
        if (requirement is null || manifest.ObservationExemption is not null) return null;
        var tokenId = ExtractTokenId(manifest, args);
        if (tokenId is null) throw new MissingObservationTokenException(manifest.Name, requirement.ArgName);
        var token = _tokenStore.Resolve(tokenId);
        if (token is null)
        {
            if (_tokenStore is IDurableObservationTokenStore missingDurable)
                throw new InvalidOperationException(missingDurable.Consume(tokenId, TokenScope(), ResourcePath(requirement, args), AuditContext(correlation)).RejectionReason ?? $"Observation token '{tokenId}' is invalid.");
            throw new InvalidOperationException($"Observation token '{tokenId}' is invalid or has already been consumed for tool '{manifest.Name}'.");
        }
        if (token.SessionId != _currentSession.InstanceId) throw new InvalidOperationException($"Observation token '{tokenId}' was issued for a different session.");
        if (_tokenStore is InMemoryTokenStore memory && memory.CallIndex > token.ExpiresAtCallIndex) throw new InvalidOperationException($"Observation token '{tokenId}' for path '{token.ResourcePath}' has expired.");
        if (requirement.ResourcePathArg is not null && args.TryGetValue(requirement.ResourcePathArg, out var path) && path.GetString() is { } target && token.ResourcePath != target) throw new InvalidOperationException($"Observation token '{tokenId}' path mismatch: token is for '{token.ResourcePath}', but tool '{manifest.Name}' targets '{target}'.");
        if (requirement.AcceptedTypes.Length > 0 && !requirement.AcceptedTypes.Contains(GuessTokenType(manifest.IssuesObservationToken?.TokenType, token.IssuedByTool))) throw new InvalidOperationException($"Observation token '{tokenId}' has an unsupported type for tool '{manifest.Name}'.");
        if (requirement.VerifyPathAbsent is not null && requirement.AcceptedTypes.Contains(TokenType.ListToken) && _listingResults.TryGetValue(tokenId, out var paths) && args.TryGetValue(requirement.VerifyPathAbsent, out var verify) && verify.GetString() is { } file && paths.Contains(file)) throw new InvalidOperationException($"File '{file}' already exists - use an edit tool with a read token.");
        if (_tokenStore is IDurableObservationTokenStore durable)
        {
            var consumption = durable.Consume(tokenId, TokenScope(), ResourcePath(requirement, args), AuditContext(correlation));
            if (!consumption.Consumed) throw new InvalidOperationException(consumption.RejectionReason ?? $"Observation token '{tokenId}' cannot be consumed.");
        }
        else if (manifest.DangerTier == DangerTier.Catastrophic && _tokenStore is InMemoryTokenStore consumingStore) consumingStore.Consume(tokenId);
        return tokenId;
    }

    private static string? ExtractTokenId(ToolManifest manifest, IReadOnlyDictionary<string, JsonElement> args) => manifest.RequiresObservationToken is not null && args.TryGetValue(manifest.RequiresObservationToken.ArgName, out var raw) && raw.ValueKind != JsonValueKind.Null ? raw.GetString() : null;

    private string IssueObservationToken(ToolManifest manifest, IReadOnlyDictionary<string, JsonElement> args, string? output, ToolInvocationContext correlation)
    {
        var source = manifest.IssuesObservationToken!;
        var tokenId = Guid.NewGuid().ToString("N");
        var path = source.ResourcePathArg is not null && args.TryGetValue(source.ResourcePathArg, out var pathValue) ? pathValue.GetString() ?? "" : "";
        var expiry = manifest.DangerTier switch { DangerTier.Catastrophic => 1, DangerTier.Destructive => 3, _ => _defaultTokenExpiry };
        var issued = _tokenStore is InMemoryTokenStore memory ? memory.NextCallIndex() : 0;
        var token = new ObservationToken(tokenId, path, manifest.Name, _currentSession.InstanceId, issued, issued + expiry);
        if (_tokenStore is IDurableObservationTokenStore durable)
            durable.Store(token, TokenScope(), DateTimeOffset.UtcNow.AddMinutes(expiry), AuditContext(correlation));
        else
            _tokenStore.Store(token);
        if (source.TokenType == TokenType.ListToken)
        {
            var listedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(output))
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    listedPaths.Add(line);
            }
            _listingResults[tokenId] = listedPaths;
        }
        return tokenId;
    }

    private ObservationTokenScope TokenScope() => new(_workspaceId, _currentSession.InstanceId, _issuerId);
    private ObservationTokenAuditContext AuditContext(ToolInvocationContext correlation) => new(correlation.TraceId!, correlation.WorkflowId!, _currentSession.InstanceId);
    private static string? ResourcePath(ObservationRequirement requirement, IReadOnlyDictionary<string, JsonElement> args) => requirement.ResourcePathArg is not null && args.TryGetValue(requirement.ResourcePathArg, out var path) ? path.GetString() : null;

    private static TokenType GuessTokenType(TokenType? declaredType, string toolName) => declaredType ?? (toolName.Contains("list", StringComparison.OrdinalIgnoreCase) || toolName.Contains("dir", StringComparison.OrdinalIgnoreCase) ? TokenType.ListToken : TokenType.ReadToken);
    private static IReadOnlyDictionary<string, JsonElement> StripTokenArgs(ToolManifest manifest, IReadOnlyDictionary<string, JsonElement> args) => manifest.RequiresObservationToken is null || !args.ContainsKey(manifest.RequiresObservationToken.ArgName) ? args : args.Where(x => x.Key != manifest.RequiresObservationToken.ArgName).ToDictionary();
    private static bool TryExtractFileContent(IReadOnlyDictionary<string, JsonElement> args, out string path, out string content)
    {
        path = args.FirstOrDefault(x => x.Key.Equals("filePath", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("path", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("file", StringComparison.OrdinalIgnoreCase)).Value.GetString() ?? "";
        content = args.FirstOrDefault(x => x.Key.Equals("content", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("body", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("text", StringComparison.OrdinalIgnoreCase)).Value.GetString() ?? "";
        return path.Length > 0 && content.Length > 0;
    }
}
