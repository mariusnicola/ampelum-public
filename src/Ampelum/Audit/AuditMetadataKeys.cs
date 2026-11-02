namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// Well-known <c>HookContext.Metadata</c> keys used by <see cref="AuditHook"/>.
/// PSQ-4: <see cref="ModelId"/> falls back to <c>"unknown"</c> when absent.
/// </summary>
public static class AuditMetadataKeys
{
    /// <summary>Value type: <see langword="string"/>. Model identifier for the audit entry (PSQ-4).</summary>
    public const string ModelId = "SignalGrid.Ampelum.audit.model_id";
    /// <summary>Value type: <see langword="long"/>. Prompt tokens consumed by the recorded call.</summary>
    public const string PromptTokens = "SignalGrid.Ampelum.audit.prompt_tokens";
    /// <summary>Value type: <see langword="long"/>. Completion tokens emitted by the recorded call.</summary>
    public const string CompletionTokens = "SignalGrid.Ampelum.audit.completion_tokens";

    /// <summary>
    /// v1.0-rc1.alpha.5 (Gap 2): metadata key. Hooks set this on <c>HookContext.Metadata</c>
    /// to specify the <see cref="AuditEntry.EventType"/> discriminator that <see cref="AuditHook"/>
    /// will propagate onto the appended entry. Default when absent: <c>"forward.completed"</c>.
    /// </summary>
    public const string EventType = "SignalGrid.Ampelum.audit.event_type";

    /// <summary>
    /// v1.0-rc1.alpha.13: metadata key. Hooks set this on <c>HookContext.Metadata</c>
    /// to supply the W3C TraceContext trace-id (32 lowercase hex chars) that
    /// <see cref="AuditHook"/> writes into <see cref="AuditEntry.TraceId"/> and folds
    /// into the V3 chain hash via <see cref="AuditChainHasher"/>. When absent,
    /// <see cref="AuditHook"/> falls back to <c>Activity.Current?.TraceId.ToHexString()</c>,
    /// then to <c>""</c>.
    /// </summary>
    public const string TraceId = "SignalGrid.Ampelum.audit.trace_id";

    /// <summary>EventType emitted when a <c>GroupChat</c> turn produces a speaker handoff (v1.0-rc1.alpha.5).</summary>
    public const string EventTypeAgentHandoff = "agent.handoff";

    // -- v0.9g event-type strings (used as AuditEntry.EventType) -----------------------------------

    /// <summary>EventType emitted when <c>RoutedChatClient</c> fails over from one target to another (v0.9g).</summary>
    public const string EventTypeRouterFailover = "router.failover";
    /// <summary>EventType emitted when a semantic cache lookup hits (v0.9g).</summary>
    public const string EventTypeSemcacheHit = "semcache.hit";
    /// <summary>EventType emitted when a semantic cache miss writes a fresh entry (v0.9g).</summary>
    public const string EventTypeSemcacheStore = "semcache.store";
    /// <summary>EventType emitted when the rate limiter granted a permit (v0.9g).</summary>
    public const string EventTypeRatelimitAcquired = "ratelimit.acquired";
    /// <summary>EventType emitted when the rate limiter rejected a request (v0.9g).</summary>
    public const string EventTypeRatelimitRejected = "ratelimit.rejected";
    /// <summary>EventType emitted when the circuit breaker transitions to <c>Open</c> (v0.9g).</summary>
    public const string EventTypeCircuitBreakerOpen = "circuitbreaker.open";
    /// <summary>EventType emitted when the circuit breaker transitions to <c>HalfOpen</c> (v0.9g).</summary>
    public const string EventTypeCircuitBreakerHalfOpen = "circuitbreaker.halfopen";
    /// <summary>EventType emitted when the circuit breaker transitions to <c>Closed</c> (v0.9g).</summary>
    public const string EventTypeCircuitBreakerClosed = "circuitbreaker.closed";
    /// <summary>EventType emitted when a deadline is exceeded (v0.9g).</summary>
    public const string EventTypeDeadlineExceeded = "deadline.exceeded";

    // -- v0.9g metadata keys -----------------------------------------------------------------------

    /// <summary>Metadata key on a router-failover audit entry: name of the target that failed.</summary>
    public const string RouterFrom = "router.from";
    /// <summary>Metadata key on a router-failover audit entry: name of the target that succeeded after failover.</summary>
    public const string RouterTo = "router.to";
    /// <summary>Metadata key on a router-failover audit entry: <c>error.GetType().Name</c> of the failure.</summary>
    public const string RouterErrorKind = "router.error.kind";
}
