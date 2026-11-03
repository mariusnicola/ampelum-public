namespace SignalGrid.Ampelum;

/// <summary>
/// Canonical well-known keys for <see cref="Hooks.HookContext.Metadata"/>. Hooks that participate
/// in the v0.6 cross-cutting protocol (BudgetHook, AuditHook, CachingHook) rely on these strings
/// being stable across versions. Always use the constants â€” never the raw strings â€” in library code.
/// </summary>
public static class HookMetadataKeys
{
    /// <summary>Value type: <see langword="string"/>. Identifier of the model that produced the response.
    /// Published by <c>Predict&lt;TSig&gt;.ForwardAsync</c> immediately after the LM call returns and
    /// before the descending OnAfter pass. Falls back to <c>resolved.DefaultChatOptions?.ModelId</c>
    /// or the literal <c>"unknown"</c> when the chat client does not expose a model ID.
    /// Absent on the OnBefore short-circuit path (PSQ-11).</summary>
    public const string ModelId = "lm.model_id";

    /// <summary>Value type: <see langword="long"/>. Prompt (input) token count from
    /// <c>response.Usage.InputTokenCount</c>. Published by <c>Predict</c> after the LM call.
    /// Read by BudgetHook.OnAfter (defaults missing to 0L) and by v0.6b AuditHook.
    /// Absent on the OnBefore short-circuit path (PSQ-11).</summary>
    public const string PromptTokens = "lm.tokens.prompt";

    /// <summary>Value type: <see langword="long"/>. Completion (output) token count from
    /// <c>response.Usage.OutputTokenCount</c>. Published by <c>Predict</c> after the LM call.
    /// Read by BudgetHook.OnAfter (defaults missing to 0L) and by v0.6b AuditHook.
    /// Absent on the OnBefore short-circuit path (PSQ-11).</summary>
    public const string CompletionTokens = "lm.tokens.completion";

    /// <summary>Value type: <see langword="double"/>. Wall-clock duration of the
    /// <c>IChatClient.GetResponseAsync</c> call in milliseconds, measured by Predict's <c>lmStopwatch</c>.
    /// Absent on the OnBefore short-circuit path (PSQ-11).</summary>
    public const string LmCallDurationMs = "lm.call.duration_ms";

    /// <summary>Value type: <see cref="decimal"/>. Per-call cost in cents. Optional and opt-in:
    /// must be written by user code (e.g. a higher-Order pricing hook with <c>Order &lt; 0</c>) before
    /// BudgetHook.OnAfter (Order=0) reads it. BudgetHook.OnAfter defaults to 0m when absent (PSQ-5).</summary>
    public const string BudgetCostCents = "budget.cost_cents";

    /// <summary>Value type: <see langword="string"/>. Scope key (e.g. tenant ID, customer ID) used
    /// by BudgetHook to look up a per-scope <c>IBudget</c> in the constructor-supplied
    /// scope map. Absence falls back to BudgetHook's global budget (PSQ-6).</summary>
    public const string BudgetScope = "budget.scope";

    // -- v0.9g cross-cutting infra -----------------------------------------------------------------

    /// <summary>Value type: <see langword="string"/>. Partition key consulted by
    /// <c>RateLimitingChatClient</c> when bucketing requests (PSQ-3, v0.9g). When absent the
    /// rate limiter falls back to <see cref="RouterTargetName"/> and finally to <c>"default"</c>.</summary>
    public const string RateLimitKey = "rate_limit.key";

    /// <summary>Value type: <see langword="string"/>. Name of the routed target chosen by
    /// <c>RoutedChatClient</c> for this call (PSQ-1, v0.9g). Used as a fallback partition
    /// key by <c>RateLimitingChatClient</c>.</summary>
    public const string RouterTargetName = "router.target.name";

    /// <summary>Value type: <see cref="DateTimeOffset"/>. Per-call deadline override
    /// (PSQ-5, v0.9g). The earlier of this value and <c>AmpelumContext.Deadline</c> wins.</summary>
    public const string DeadlineUtc = "deadline.utc";

    /// <summary>Value type: <see langword="bool"/>. Set to <see langword="true"/> when an
    /// audit replay is in progress so that semantic-cache decorators short-circuit and let
    /// the canonical recorded response surface (PSQ-2 F4 determinism, v0.9g).</summary>
    public const string AuditReplaying = "SignalGrid.Ampelum.audit.replaying";

    /// <summary>Value type: <see langword="bool"/>. Stamped on responses that were served
    /// from the semantic cache (PSQ-2, v0.9g).</summary>
    public const string SemanticCacheHit = "SignalGrid.Ampelum.semantic_cache_hit";

    /// <summary>Value type: <see langword="string"/>. Full name of the signature type of the module call that
    /// owns the hook context; <c>Predict</c> sets it so a hook can tell two signatures apart, because every
    /// <c>Predict</c> reports the module name <c>Predict</c>.</summary>
    public const string SignatureType = "SignalGrid.Ampelum.signature_type";

    /// <summary>Value type: <see langword="bool"/>. Set to <see langword="true"/> by <c>SemanticCacheHook</c> when an
    /// embedding call failed and was skipped (the lookup fell back to the exact match, or the entry was stored without
    /// a vector), so audit and telemetry hooks can see it. The failure itself is not raised.</summary>
    public const string SemanticCacheEmbeddingFailed = "SignalGrid.Ampelum.semantic_cache_embedding_failed";

    /// <summary>Value type: <c>IReadOnlyList&lt;Exception&gt;</c>. Set by the hook pipeline when more than one after hook
    /// threw: the first failure in run order (the highest-order hook) is rethrown with its own type, and the others
    /// are listed here in run order. Absent when at most one hook threw.</summary>
    public const string AfterHookFailures = "SignalGrid.Ampelum.after_hook_failures";

    /// <summary>Value type: <see langword="bool"/>. Set to <see langword="true"/> by the hook pipeline as soon as an
    /// after hook throws, before the remaining (lower-order) after hooks run. A hook that stores the call's outputs,
    /// such as the semantic cache, must not store them: the call failed and its reply was blocked.</summary>
    public const string AfterHookFailed = "SignalGrid.Ampelum.after_hook_failed";

    /// <summary>Value type: <see langword="bool"/>. Set to <see langword="true"/> by <c>StreamingChatSession</c> on
    /// the context of a streamed turn. The semantic cache neither serves from nor stores into the cache for it,
    /// because the tokens already reached the caller before any cached answer could be used.</summary>
    public const string Streamed = "SignalGrid.Ampelum.streamed";

    /// <summary>Value type: <see langword="double"/>. Cosine similarity between the query
    /// embedding and the cached entry that satisfied the lookup (PSQ-2, v0.9g).</summary>
    public const string SemanticCacheSimilarity = "SignalGrid.Ampelum.semantic_cache_similarity";

    // -- v1.0-rc1 alpha.8 (Gap 5) IBudget.TryReserve pre-LM reservation ---------------------------

    /// <summary>Value type: <see langword="long"/>. Caller-supplied estimate of prompt tokens
    /// for the upcoming LM call, consulted by <c>BudgetHook.OnBeforeAsync</c> when reserving
    /// budget capacity via <c>IBudget.TryReserve</c>. Absence falls back to
    /// <see cref="BudgetExpectedPromptTokensDefault"/>. Set this from a higher-Order pre-hook
    /// when the caller has a better prediction (e.g. a token-counting hook with Order &lt; 0).</summary>
    public const string ExpectedPromptTokens = "budget.expected.prompt_tokens";

    /// <summary>Value type: <see langword="long"/>. Caller-supplied estimate of completion
    /// tokens for the upcoming LM call. Same protocol as <see cref="ExpectedPromptTokens"/>;
    /// falls back to <see cref="BudgetExpectedCompletionTokensDefault"/>.</summary>
    public const string ExpectedCompletionTokens = "budget.expected.completion_tokens";

    /// <summary>Value type: <see cref="decimal"/>. Caller-supplied estimate of cost in cents
    /// for the upcoming LM call. Defaults to 0m when absent (cost reservation is opt-in,
    /// matching <see cref="BudgetCostCents"/> semantics on the OnAfter side).</summary>
    public const string ExpectedCostCents = "budget.expected.cost_cents";

    /// <summary>Default value used by <c>BudgetHook.OnBeforeAsync</c> when
    /// <see cref="ExpectedPromptTokens"/> is absent. 500 tokens â€” chosen as a conservative
    /// median for short-form QA prompts; tune via <c>BudgetHook</c> ctor for your workload.</summary>
    public const long BudgetExpectedPromptTokensDefault = 500L;

    /// <summary>Default value used by <c>BudgetHook.OnBeforeAsync</c> when
    /// <see cref="ExpectedCompletionTokens"/> is absent. 500 tokens.</summary>
    public const long BudgetExpectedCompletionTokensDefault = 500L;
}
