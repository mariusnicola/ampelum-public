using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace SignalGrid.Ampelum;

/// <summary>
/// Immutable context record that bundles all runtime dependencies for a Ampelum module
/// (<see cref="IChatClient"/>, <see cref="IAdapter"/>, <see cref="ITraceSink"/>, logging, and optional default chat options).
/// Pass an instance to <c>Predict</c> or <c>ChainOfThought</c> constructors, or use the DI extension
/// <c>AddAmpelum</c> to have the framework resolve one automatically.
/// </summary>
/// <param name="Chat">The MEAI-compatible chat client used to call the LM.</param>
/// <param name="Adapter">The adapter that formats prompts and parses LM responses.</param>
/// <param name="TraceSink">Sink that receives a <see cref="TraceRecord"/> after every forward call.</param>
/// <param name="LoggerFactory">Factory used to create module-scoped loggers.</param>
/// <param name="DefaultChatOptions">Optional baseline <see cref="ChatOptions"/> cloned for every LM call; <see langword="null"/> means use provider defaults.</param>
/// <remarks>
/// <para>
/// This record's shape is intentionally frozen. It sits on the first constructor parameter of
/// <c>Predict&lt;TSig&gt;</c>, on <c>WithContext</c>, and across the public surface of every module —
/// it is the most load-bearing type in the library after <see cref="IModule{TIn, TOut}"/>. A shape
/// change here (moving existing members, or restructuring into per-concern option objects) would
/// break every consumer's construction site for no reduction in the number of optional features, so
/// it is not done on aesthetic grounds (architecture recommendations spec, item #7, 2026-09-11).
/// </para>
/// <para>
/// <b>For future cross-cutting features:</b> do not add another top-level nullable property here.
/// Group the new feature's settings into one nested options record (e.g. a hypothetical
/// <c>GovernanceOptions</c>) and expose it as a single <see langword="init"/> property instead — one
/// property added per feature, not N. Existing top-level optional properties stay where they are;
/// moving them later would be a breaking change that buys nothing.
/// </para>
/// </remarks>
public sealed record AmpelumContext(
    IChatClient Chat,
    IAdapter Adapter,
    ITraceSink TraceSink,
    ILoggerFactory LoggerFactory,
    ChatOptions? DefaultChatOptions = null)
{
    /// <summary>
    /// Optimizer-bound submodule identity. Set by <c>OptimizedProgram.Bind</c> (v0.5) via
    /// record <c>with</c>; <see langword="null"/> for any context constructed outside that path.
    /// Internal-init ensures external callers cannot spoof submodule identity; v0.5 optimizer
    /// code lives inside the same assembly and can set it via the record's <c>with</c> expression.
    /// </summary>
    public string? SubmoduleName { get; internal init; }

    /// <summary>
    /// The fields <c>OptimizedProgram.Bind</c> set on this context from an optimized program (client, demos,
    /// instruction override). A later <c>Bind</c> keeps a field of the module's current context only when it
    /// carries the mark; a context built by user code carries none.
    /// </summary>
    internal Optimizers.BoundFields OptimizerBound { get; init; }

    /// <summary>
    /// Optional usage tracker. When non-null, Predict/ReAct/etc. call Record after every LM response.
    /// </summary>
    public Usage.IUsageTracker? Usage { get; init; }

    /// <summary>
    /// Optional retriever. When non-null, user module code may call
    /// <c>context.Retriever!.RetrieveAsync(query, topK, ct: ct)</c> to perform RAG.
    /// <c>Predict&lt;TSig&gt;</c> and <c>ChainOfThought&lt;TSig&gt;</c> do NOT use this property;
    /// it is a user-composable primitive. v0.4 addition â€” non-breaking (init property, not positional).
    /// </summary>
    public Retrieval.IRetriever? Retriever { get; init; }

    /// <summary>
    /// Optional default demos. When non-null, <c>Predict&lt;TSig&gt;.ForwardAsync</c> uses these
    /// as the demo list when the caller passes <see langword="null"/> or an empty list.
    /// Optimizer-bound modules inject learned demos here via record <c>with</c> in
    /// <c>OptimizedProgram.Bind</c> (v0.5b). Explicit demos passed by the caller take precedence.
    /// Non-breaking: init property; existing ctors unchanged.
    /// </summary>
    public IReadOnlyList<Example>? DefaultDemos { get; init; }

    /// <summary>
    /// Optional per-submodule instruction overrides. Maps submodule name â†’ replacement instruction string.
    /// When non-null, <c>Predict&lt;TSig&gt;.ForwardAsync</c> looks up
    /// <c>InstructionOverrides[SubmoduleName]</c> (when <see cref="SubmoduleName"/> is non-null)
    /// and applies it for that call only via <c>SignatureMetadata.WithInstructions</c>.
    /// See spec Â§7.1.3b for the exact override contract.
    /// Non-breaking: init property; existing ctors unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, string>? InstructionOverrides { get; init; }

    /// <summary>
    /// Optional cross-cutting hook pipeline. When non-null, <c>Predict&lt;TSig&gt;.ForwardAsync</c>
    /// runs <c>OnBefore</c> before formatting the prompt, <c>OnAfter</c> after parsing succeeds, and
    /// <c>OnError</c> in the catch block. Order ascending for OnBefore, descending for OnAfter/OnError.
    /// Implementations must be stateless or thread-safe (PSQ-3). v0.6a addition â€” non-breaking.
    /// </summary>
    public IReadOnlyList<Hooks.IHook>? Hooks { get; init; }

    /// <summary>
    /// Optional wall-clock deadline for the operation. When non-null, <c>Predict&lt;TSig&gt;.ForwardAsync</c>
    /// checks <see cref="DateTimeOffset.UtcNow"/> against this value at every phase boundary
    /// and throws <see cref="Exceptions.DeadlineExceededException"/> when crossed (PSQ-5, v0.9g).
    /// Per-call overrides via <c>HookContext.Metadata["deadline.utc"]</c> use the EARLIER of
    /// the two values.
    /// </summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>
    /// Returns a new <see cref="AmpelumContext"/> whose <see cref="Deadline"/> is
    /// <c>DateTimeOffset.UtcNow + duration</c>. Negative or zero <paramref name="duration"/>
    /// throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public AmpelumContext WithDeadline(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Deadline duration must be positive.");
        return this with { Deadline = DateTimeOffset.UtcNow + duration };
    }

    /// <summary>
    /// Returns a new <see cref="AmpelumContext"/> with <paramref name="hook"/> appended to the existing
    /// <see cref="Hooks"/> list (or as the only entry when Hooks is null). The original instance is unchanged.
    /// </summary>
    public AmpelumContext WithHook(Hooks.IHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var existing = Hooks;
        var next = existing is null
            ? new List<Hooks.IHook>(1) { hook }
            : new List<Hooks.IHook>(existing) { hook };
        return this with { Hooks = next };
    }

    /// <summary>
    /// Returns a new <see cref="AmpelumContext"/> with all hooks of type <typeparamref name="THook"/> removed.
    /// If no hooks of that type exist, or <see cref="Hooks"/> is null, returns the current instance unchanged.
    /// </summary>
    public AmpelumContext WithoutHook<THook>() where THook : Hooks.IHook
    {
        if (Hooks is null) return this;
        var filtered = Hooks.Where(h => h is not THook).ToList();
        if (filtered.Count == Hooks.Count) return this;
        return this with { Hooks = filtered.Count > 0 ? (IReadOnlyList<Hooks.IHook>)filtered : null };
    }

    static readonly AsyncLocal<AmpelumContext?> _ambient = new();

    /// <summary>Current ambient context, or <see langword="null"/> if no <see cref="Push"/> is in effect.</summary>
    public static AmpelumContext? Ambient => _ambient.Value;

    /// <summary>
    /// Temporarily makes <paramref name="newCtx"/> the ambient context for the current
    /// AsyncLocal execution context. The returned <see cref="IDisposable"/> restores the previous
    /// ambient value on dispose. Reentrant; nested pushes form a LIFO stack.
    /// </summary>
    public static IDisposable Push(AmpelumContext newCtx)
    {
        ArgumentNullException.ThrowIfNull(newCtx);
        var previous = _ambient.Value;
        _ambient.Value = newCtx;
        return new Scope(previous);
    }

    sealed class Scope(AmpelumContext? previous) : IDisposable
    {
        bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ambient.Value = previous;
        }
    }

    /// <summary>
    /// Renders the last <paramref name="n"/> <see cref="TraceRecord"/> instances from <see cref="TraceSink"/>
    /// as a human-readable string. Requires sink to implement <see cref="Usage.IInspectableSink"/>;
    /// otherwise returns <see cref="string.Empty"/>.
    /// </summary>
    public string InspectHistory(int n = 1)
    {
        if (TraceSink is not Usage.IInspectableSink inspectable) return string.Empty;
        if (n <= 0) return string.Empty;
        var recs = inspectable.GetRecent(n);
        if (recs.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < recs.Count; i++)
        {
            var r = recs[i];
            var p = r.Usage?.InputTokenCount?.ToString() ?? "";
            var c = r.Usage?.OutputTokenCount?.ToString() ?? "";
            sb.Append('[').Append("attempt ").Append(i).Append("] ")
              .Append(r.ModuleName).Append(" | ")
              .Append(r.Duration.TotalMilliseconds.ToString("F0")).Append("ms | ")
              .Append("prompt_tokens=").Append(p).Append(' ')
              .AppendLine("completion_tokens=" + c);
            sb.AppendLine("--- PROMPT ---");
            foreach (var m in r.PromptMessages)
                sb.Append(m.Role.ToString().ToLowerInvariant()).Append(": ").AppendLine(m.Text);
            sb.AppendLine("--- RESPONSE ---");
            sb.AppendLine(r.RawResponse);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
