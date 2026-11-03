using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Hooks.Internal;
using SignalGrid.Ampelum.Internal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum;

/// <summary>Static factory for creating <see cref="Predict{TSig}"/> instances with sensible defaults.</summary>
public static class Predict
{
    /// <summary>Creates a <see cref="Predict{TSig}"/> wired to the given <paramref name="chat"/> client.</summary>
    public static Predict<TSig> Create<TSig>(
        IChatClient chat,
        IAdapter? adapter = null,
        ITraceSink? sink = null,
        ILoggerFactory? loggerFactory = null) where TSig : ISignature
    {
        ArgumentNullException.ThrowIfNull(chat);
        return new Predict<TSig>(new AmpelumContext(
            chat,
            adapter ?? new ChatAdapter(),
            sink ?? NullTraceSink.Instance,
            loggerFactory ?? NullLoggerFactory.Instance));
    }
}

/// <summary>Core Ampelum module: formats a signature + demos into chat messages, calls the LM once,
/// parses the response, emits a <see cref="TraceRecord"/>, and returns a <see cref="Prediction{TSig}"/>.</summary>
public sealed class Predict<TSig> : IModule<TSig, TSig>, Optimizers.IBoundContext where TSig : ISignature
{
    // Cache PropertyInfo[] per concrete runtime type so ProjectInputs avoids per-call reflection.
    static readonly ConcurrentDictionary<Type, PropertyInfo[]> _inputPropCache = new();

    // OTel GenAI semantic conventions version this module emits attributes under.
    // v0.7 PSQ-2: pin 1.27.0 explicitly so dashboards can detect spec drift.
    internal const string GenAiSemconvVersion = "1.27.0";

    readonly AmpelumContext? _ctorCtx;   // null means "resolve from ambient per-call"
    readonly SignatureMetadata _meta;

    // v1.0-rc1 P1: hot-path allocation reduction. Cache the typed logger so
    // ForwardAsync doesn't allocate a Logger<Predict<TSig>> per call. The
    // logger factory may differ per resolved context (ambient path), so we
    // key the cache by factory identity. Single-entry cache covers the
    // overwhelmingly common case (one factory per process).
    ILoggerFactory? _cachedLoggerFactory;
    ILogger? _cachedLogger;

    // Shared empty metadata sentinel â€” used when a TraceRecord has no
    // metadata entries to attach. Avoids a per-call Dictionary alloc on
    // the success path. IReadOnlyDictionary is the public type, so callers
    // can't mutate it.
    static readonly IReadOnlyDictionary<string, object?> EmptyMetadata =
        new Dictionary<string, object?>(0);

    /// <summary>Returns a <see cref="Predict{TSig}"/> that resolves its context from
    /// <see cref="AmpelumContext.Ambient"/> on each <see cref="ForwardAsync"/> call.</summary>
    public static Predict<TSig> FromAmbient() => new();

    /// <summary>Initialises a new <see cref="Predict{TSig}"/> bound to the given context.</summary>
    public Predict(AmpelumContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctorCtx = context;
        _meta = SignatureMetadataCache.For<TSig>();
    }

    // Private constructor used by FromAmbient() â€” no context available at construction time.
    Predict()
    {
        _ctorCtx = null;
        _meta = SignatureMetadataCache.For<TSig>();
    }

    /// <summary>
    /// Returns a new <see cref="Predict{TSig}"/> immutably rebound to <paramref name="context"/>.
    /// The original instance is unchanged. Used by <c>OptimizedProgram.Bind</c> (v0.5b) to inject
    /// per-submodule contexts carrying <c>DefaultDemos</c> and <c>InstructionOverrides</c>.
    /// </summary>
    public Predict<TSig> WithContext(AmpelumContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Predict<TSig>(context);
    }

    AmpelumContext? Optimizers.IBoundContext.BoundContext => _ctorCtx;

    /// <summary>Run one forward pass: format â†’ LM call â†’ parse â†’ trace â†’ return.</summary>
    public async Task<Prediction<TSig>> ForwardAsync(
        TSig inputs,
        IReadOnlyList<Example>? demos = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var resolved = _ctorCtx ?? AmpelumContext.Ambient
            ?? throw new ArgumentNullException(nameof(AmpelumContext),
                "Predict requires either a constructor-injected AmpelumContext or an active AmpelumContext.Push scope.");

        var logger = GetOrCreateLogger(resolved.LoggerFactory);
        if (demos is null or { Count: 0 })
            demos = resolved.DefaultDemos ?? [];

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.module.forward");
        activity?.SetTag("SignalGrid.Ampelum.module", nameof(Predict));
        activity?.SetTag("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name);
        activity?.SetTag("SignalGrid.Ampelum.adapter", resolved.Adapter.GetType().Name);
        activity?.SetTag("SignalGrid.Ampelum.demos.count", demos.Count);
        activity?.SetTag("gen_ai.system", InferGenAiSystem(resolved.Chat));
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("gen_ai.semconv.version", GenAiSemconvVersion);

        var traceId = Guid.NewGuid();
        activity?.SetTag("SignalGrid.Ampelum.trace_id", traceId.ToString());

        var inputsDict = ProjectInputs(inputs);
        // v1.0-rc1 P1: Stopwatch.StartNew allocates a class instance. Use the
        // value-typed Stopwatch.GetTimestamp / GetElapsedTime pair (already used
        // for sub-phase timings below) for the outer wall-clock too.
        var forwardStartTs = Stopwatch.GetTimestamp();
        TimeSpan forwardElapsed = default;
        bool forwardStopped = false;
        IReadOnlyList<ChatMessage> messages = [];
        ChatResponse? response = null;                          // Fix B: track outside try
        IReadOnlyDictionary<string, object?>? outputs = null;  // Fix A: track outside try

        // v0.6a: hook pipeline (PSQ-2 ordering)
        HookPipeline? hookPipeline = null;
        HookContext? hookCtx = null;
        if (resolved.Hooks is { Count: > 0 } hookList)
        {
            hookPipeline = HookPipeline.GetOrCreate(hookList);
            hookCtx = new HookContext
            {
                ModuleName = resolved.SubmoduleName ?? nameof(Predict),
                Inputs = inputsDict,
            };
            hookCtx.Set(HookMetadataKeys.SignatureType, typeof(TSig).FullName);
        }

        try
        {
            // PSQ-5 phase boundary 1: OnBefore start
            CheckDeadline(resolved, hookCtx, "before-onbefore");
            var effectiveMetadata = _meta;
            if (resolved.InstructionOverrides is { Count: > 0 })
            {
                var submoduleName = resolved.SubmoduleName;
                if (submoduleName is not null &&
                    resolved.InstructionOverrides.TryGetValue(submoduleName, out var overrideInstr) &&
                    !string.IsNullOrWhiteSpace(overrideInstr))
                {
                    effectiveMetadata = _meta.WithInstructions(overrideInstr);
                }
            }

            if (hookPipeline is not null)
            {
                activity?.AddEvent(new ActivityEvent("SignalGrid.Ampelum.hook.before"));
                var hookBeforeSw = Stopwatch.GetTimestamp();
                await hookPipeline.RunBeforeAsync(hookCtx!, ct).ConfigureAwait(false);
                var hookBeforeElapsed = Stopwatch.GetElapsedTime(hookBeforeSw);
                Diagnostics.HookBeforeDuration.Record(
                    hookBeforeElapsed.TotalMilliseconds,
                    new("SignalGrid.Ampelum.module", nameof(Predict)),
                    new("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));
                // v1.0-rc1.alpha.5 (Gap 4): hook-phase span tag for OTel-savvy users.
                activity?.SetTag("SignalGrid.Ampelum.hook.before.duration_us",
                    (long)(hookBeforeElapsed.TotalMilliseconds * 1000));
                // PSQ-5 phase boundary 2: post-OnBefore
                CheckDeadline(resolved, hookCtx, "post-onbefore");

                // PSQ-11 short-circuit: an OnBefore hook populated Outputs â€” skip the LM call.
                if (hookCtx!.Outputs is not null)
                {
                    var scOutputs = hookCtx.Outputs;
                    var scValue = RecordCloner.CloneWith(inputs, scOutputs);
                    if (!forwardStopped) { forwardElapsed = Stopwatch.GetElapsedTime(forwardStartTs); forwardStopped = true; }
                    var scMetadata = new Dictionary<string, object?>
                    {
                        [TraceKeys.HookShortCircuited] = true,
                    };
                    if (demos.Count > 0)
                        scMetadata[TraceKeys.DemosUsed] = (IReadOnlyList<Example>)demos;
                    var scTrace = new TraceRecord(
                        Id: traceId,
                        ModuleName: resolved.SubmoduleName ?? nameof(Predict),
                        SignatureType: _meta.SignatureType,
                        Inputs: inputsDict,
                        Outputs: scOutputs,
                        PromptMessages: Array.Empty<ChatMessage>(),
                        RawResponse: string.Empty,
                        Usage: null,
                        Duration: forwardElapsed,
                        Error: null,
                        Metadata: scMetadata);
                    SafeSinkWrite(resolved, logger, scTrace);
                    // PSQ-5 phase boundary 5: pre-OnAfter (short-circuit path)
                    CheckDeadline(resolved, hookCtx, "pre-onafter");
                    activity?.AddEvent(new ActivityEvent("SignalGrid.Ampelum.hook.after"));
                    var hookAfterSwSc = Stopwatch.GetTimestamp();
                    await hookPipeline.RunAfterAsync(hookCtx, ct).ConfigureAwait(false);
                    var hookAfterScElapsed = Stopwatch.GetElapsedTime(hookAfterSwSc);
                    Diagnostics.HookAfterDuration.Record(
                        hookAfterScElapsed.TotalMilliseconds,
                        new("SignalGrid.Ampelum.module", nameof(Predict)),
                        new("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));
                    // v1.0-rc1.alpha.5 (Gap 4): hook-phase span tag (short-circuit OnAfter).
                    activity?.SetTag("SignalGrid.Ampelum.hook.after.duration_us",
                        (long)(hookAfterScElapsed.TotalMilliseconds * 1000));
                    // PSQ-5 phase boundary 6: post-OnAfter (short-circuit path)
                    CheckDeadline(resolved, hookCtx, "post-onafter");
                    return new Prediction<TSig>(scValue, scTrace);
                }
            }

            activity?.AddEvent(new ActivityEvent("SignalGrid.Ampelum.adapter.format"));
            var adapterFormatSw = Stopwatch.GetTimestamp();
            messages = resolved.Adapter.Format(effectiveMetadata, inputsDict, demos);
            Diagnostics.AdapterFormatDuration.Record(
                Stopwatch.GetElapsedTime(adapterFormatSw).TotalMilliseconds,
                new("SignalGrid.Ampelum.module", nameof(Predict)),
                new("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));
            AmpelumLog.PromptMessages(logger, messages.Count);

            var chatOptions = BuildOptions(resolved);
            activity?.SetTag("gen_ai.request.model", chatOptions?.ModelId);

            // v1.0-rc1 alpha.3 (Gap 2): adapter-parse retry loop. RetryHook.OnError publishes
            // `retry.corrective_prompt` + `retry.recovered=true` when an AdapterParseException
            // is observed. ForwardAsync now consumes that signal: append the corrective text as
            // a follow-up user message and retry the LM call up to `retry.max_recovery_attempts`
            // (capped by RetryHook.RecoveryAttemptsCeiling). Replay determinism is preserved â€”
            // each retry produces an audit entry tagged with `retry.attempt`.
            int retryAttempt = 0;
            var workingMessages = messages;
            while (true)
            {
                var lmStartTs = Stopwatch.GetTimestamp();
                try
                {
                    response = await resolved.Chat.GetResponseAsync(workingMessages, chatOptions, ct).ConfigureAwait(false);
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
                Diagnostics.LmCallDuration.Record(
                    lmElapsed.TotalMilliseconds,
                    new("SignalGrid.Ampelum.module", nameof(Predict)),
                    new("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));
                activity?.SetTag("gen_ai.response.model", response.ModelId);
                activity?.SetTag("gen_ai.response.id", response.ResponseId);
                activity?.AddEvent(new ActivityEvent("SignalGrid.Ampelum.lm.call"));
                AmpelumLog.RawResponse(logger, response.Text);
                // PSQ-5 phase boundary 3: post-LM-call
                CheckDeadline(resolved, hookCtx, "post-lm-call");

                if (hookCtx is not null)
                {
                    hookCtx.Set(HookMetadataKeys.ModelId,
                        response.ModelId ?? resolved.DefaultChatOptions?.ModelId ?? "unknown");
                    hookCtx.Set(HookMetadataKeys.PromptTokens, response.Usage?.InputTokenCount ?? 0L);
                    hookCtx.Set(HookMetadataKeys.CompletionTokens, response.Usage?.OutputTokenCount ?? 0L);
                    hookCtx.Set(HookMetadataKeys.LmCallDurationMs, lmElapsed.TotalMilliseconds);
                }

                activity?.AddEvent(new ActivityEvent("SignalGrid.Ampelum.adapter.parse"));
                var adapterParseSw = Stopwatch.GetTimestamp();
                AdapterParseException? parseFailure = null;
                try
                {
                    // An adapter that calls a model while parsing (TwoStepAdapter) reads the ambient context
                    // to run this call's hooks around that call too.
                    using (AmpelumContext.Push(resolved))
                        outputs = resolved.Adapter.Parse(_meta, response);
                }
                catch (AdapterParseException ex)
                {
                    Diagnostics.AdapterParseFailures.Add(1,
                        new KeyValuePair<string, object?>("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));
                    activity?.AddEvent(new ActivityEvent("parse.failed"));
                    parseFailure = ex;
                }

                if (parseFailure is null)
                {
                    if (retryAttempt > 0 && hookCtx is not null)
                        hookCtx.Set("retry.attempt", retryAttempt);
                    Diagnostics.AdapterParseDuration.Record(
                        Stopwatch.GetElapsedTime(adapterParseSw).TotalMilliseconds,
                        new("SignalGrid.Ampelum.module", nameof(Predict)),
                        new("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));
                    break;
                }

                // Drive the hook pipeline so RetryHook can publish `retry.corrective_prompt`.
                if (hookPipeline is null)
                    throw parseFailure;
                await hookPipeline.RunErrorAsync(hookCtx!, parseFailure, ct).ConfigureAwait(false);

                bool recovered =
                    hookCtx!.Metadata.TryGetValue("retry.recovered", out var rec) && rec is true;
                bool hasCorrective =
                    hookCtx.Metadata.TryGetValue("retry.corrective_prompt", out var cp)
                    && cp is string corrective
                    && !string.IsNullOrWhiteSpace(corrective);
                if (!recovered || !hasCorrective)
                    throw parseFailure;

                retryAttempt++;
                activity?.AddEvent(new ActivityEvent("retry.attempt"));

                // Append the corrective prompt as a follow-up user message for the next attempt.
                var rebuilt = new List<ChatMessage>(workingMessages.Count + 1);
                rebuilt.AddRange(workingMessages);
                rebuilt.Add(new ChatMessage(ChatRole.User,
                    (string)hookCtx.Metadata["retry.corrective_prompt"]!));
                workingMessages = rebuilt;
                messages = rebuilt; // reflected in trace + OnAfter parity

                // Clear the corrective marker so a successful next attempt doesn't re-trigger.
                hookCtx.Set("retry.corrective_prompt", null);
            }
            if (hookCtx is not null)
                hookCtx.Outputs = outputs;
            // PSQ-5 phase boundary 4: post-Adapter.Parse
            CheckDeadline(resolved, hookCtx, "post-parse");

            // Fix A: post-parse work is inside the outer try so failures here
            // are caught and still produce a trace.
            if (!forwardStopped) { forwardElapsed = Stopwatch.GetElapsedTime(forwardStartTs); forwardStopped = true; }
            var value = RecordCloner.CloneWith(inputs, outputs!);
            // v1.0-rc1 P1: metadata dict allocated lazily â€” many TraceRecords
            // carry no metadata, and the success-path Dictionary() alloc shows
            // up in S1/S2 alloc profile. Use the EmptyMetadata sentinel when
            // nothing was added.
            Dictionary<string, object?>? metadata = null;
            bool cacheHit = false;
            if (response.AdditionalProperties is not null
                && response.AdditionalProperties.TryGetValue("SignalGrid.Ampelum.cache_hit", out var hit)
                && hit is true)
            {
                metadata ??= new Dictionary<string, object?>(2);
                metadata[TraceKeys.CacheHit] = true;
                cacheHit = true;
                activity?.SetTag("SignalGrid.Ampelum.cache_hit", true);
            }
            if (demos.Count > 0)
            {
                metadata ??= new Dictionary<string, object?>(2);
                metadata[TraceKeys.DemosUsed] = (IReadOnlyList<Example>)demos;
            }
            var trace = new TraceRecord(
                Id: traceId,
                ModuleName: resolved.SubmoduleName ?? nameof(Predict),
                SignatureType: _meta.SignatureType,
                Inputs: inputsDict,
                Outputs: outputs!,
                PromptMessages: messages,
                RawResponse: response!.Text!, // M.E.AI Text is always non-null (string.Concat semantics)
                Usage: response.Usage,
                Duration: forwardElapsed,
                Error: null,
                Metadata: (IReadOnlyDictionary<string, object?>?)metadata ?? EmptyMetadata);

            Diagnostics.ModuleForwardDuration.Record(
                forwardElapsed.TotalMilliseconds,
                new("SignalGrid.Ampelum.module", nameof(Predict)),
                new("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));

            if (response.Usage?.InputTokenCount is long ti) Diagnostics.TokensInput.Add(ti);
            if (response.Usage?.OutputTokenCount is long to) Diagnostics.TokensOutput.Add(to);

            if (resolved.Usage is not null)
                resolved.Usage.Record(response.Usage, cacheHit: cacheHit);

            activity?.SetTag("gen_ai.usage.input_tokens", response.Usage?.InputTokenCount);
            activity?.SetTag("gen_ai.usage.output_tokens", response.Usage?.OutputTokenCount);

            SafeSinkWrite(resolved, logger, trace);
            if (hookPipeline is not null)
            {
                // PSQ-5 phase boundary 5: pre-OnAfter
                CheckDeadline(resolved, hookCtx, "pre-onafter");
                activity?.AddEvent(new ActivityEvent("SignalGrid.Ampelum.hook.after"));
                var hookAfterSw = Stopwatch.GetTimestamp();
                await hookPipeline.RunAfterAsync(hookCtx!, ct).ConfigureAwait(false);
                var hookAfterElapsed = Stopwatch.GetElapsedTime(hookAfterSw);
                Diagnostics.HookAfterDuration.Record(
                    hookAfterElapsed.TotalMilliseconds,
                    new("SignalGrid.Ampelum.module", nameof(Predict)),
                    new("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));
                // v1.0-rc1.alpha.5 (Gap 4): hook-phase span tag for OTel-savvy users.
                activity?.SetTag("SignalGrid.Ampelum.hook.after.duration_us",
                    (long)(hookAfterElapsed.TotalMilliseconds * 1000));
                // PSQ-5 phase boundary 6: post-OnAfter
                CheckDeadline(resolved, hookCtx, "post-onafter");
            }
            AmpelumLog.ForwardOk(logger, nameof(Predict), _meta.SignatureType.Name,
                forwardElapsed.TotalMilliseconds,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount);

            return new Prediction<TSig>(value, trace);
        }
        catch (Exception ex)
        {
            if (!forwardStopped) { forwardElapsed = Stopwatch.GetElapsedTime(forwardStartTs); forwardStopped = true; }
            // Fix B: use response?.Text if the LM call succeeded but a later step failed
            WriteTrace(resolved, logger, traceId, inputsDict, outputs: outputs ?? EmptyMetadata,
                messages: messages,
                rawResponse: response?.Text ?? "",
                usage: response?.Usage,
                duration: forwardElapsed, error: ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // OTel semantic conventions: exception.type and exception.message tags
            // complement SetStatus so exporters can filter/group by exception type.
            activity?.SetTag("exception.type", ex.GetType().FullName);
            activity?.SetTag("exception.message", ex.Message);
            Diagnostics.ModuleForwardErrors.Add(1,
                new KeyValuePair<string, object?>("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));
            AmpelumLog.ForwardFailed(logger, nameof(Predict), _meta.SignatureType.Name, ex);
            if (hookPipeline is not null)
            {
                activity?.AddEvent(new ActivityEvent("SignalGrid.Ampelum.hook.error"));
                var hookErrorSw = Stopwatch.GetTimestamp();
                await hookPipeline.RunErrorAsync(hookCtx!, ex, ct).ConfigureAwait(false);
                // v1.0-rc1.alpha.5 (Gap 4): hook-phase span tag for OTel-savvy users.
                activity?.SetTag("SignalGrid.Ampelum.hook.error.duration_us",
                    (long)(Stopwatch.GetElapsedTime(hookErrorSw).TotalMilliseconds * 1000));
            }
            throw;
        }
    }

    /// <summary>Run one streaming forward pass: format â†’ LM streaming call â†’ emit partial updates â†’
    /// finalize â†’ emit terminal update + trace.</summary>
    public async IAsyncEnumerable<PredictionUpdate<TSig>> StreamAsync(
        TSig inputs,
        IReadOnlyList<Example>? demos = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var resolved = _ctorCtx ?? AmpelumContext.Ambient
            ?? throw new ArgumentNullException(nameof(AmpelumContext),
                "Predict requires either a constructor-injected AmpelumContext or an active AmpelumContext.Push scope.");

        if (resolved.Adapter is not IStreamingAdapter streamAdapter)
            throw new NotSupportedException(
                $"The configured adapter '{resolved.Adapter.GetType().Name}' does not implement IStreamingAdapter. " +
                "Use an adapter that supports streaming (e.g. ChatAdapter) or call ForwardAsync instead.");

        var logger = GetOrCreateLogger(resolved.LoggerFactory);
        demos ??= [];

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.module.forward");
        activity?.SetTag("SignalGrid.Ampelum.module", nameof(Predict));
        activity?.SetTag("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name);
        activity?.SetTag("gen_ai.system", InferGenAiSystem(resolved.Chat));
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("gen_ai.semconv.version", GenAiSemconvVersion);
        var traceId = Guid.NewGuid();
        activity?.SetTag("SignalGrid.Ampelum.trace_id", traceId.ToString());

        var inputsDict = ProjectInputs(inputs);

        // v1.0-rc1 alpha.3: streaming participates in the hook pipeline so audit/budget/observability
        // hooks fire symmetrically with the non-streaming path. OnBefore runs once before the LM
        // streaming call; OnAfter runs once after Finalize (with hookCtx.Outputs populated);
        // OnError runs on any terminal error. Per-chunk hook fan-out is intentionally NOT wired
        // (would be too chatty and provides no audit-chain value over the once-per-call entry).
        HookPipeline? hookPipeline = null;
        HookContext? hookCtx = null;
        if (resolved.Hooks is { Count: > 0 } hookList)
        {
            hookPipeline = HookPipeline.GetOrCreate(hookList);
            hookCtx = new HookContext
            {
                ModuleName = resolved.SubmoduleName ?? nameof(Predict),
                Inputs = inputsDict,
            };
            hookCtx.Set(HookMetadataKeys.SignatureType, typeof(TSig).FullName);
        }

        var messages = resolved.Adapter.Format(_meta, inputsDict, demos);
        var chatOptions = BuildOptions(resolved);
        activity?.SetTag("gen_ai.request.model", chatOptions?.ModelId);
        var streamStartTs = Stopwatch.GetTimestamp();
        TimeSpan streamElapsed = default;
        var state = new StreamingParseState("", "", "", new Dictionary<string, object?>(), IsComplete: false);
        long chunkCount = 0;
        bool cacheHit = false;
        UsageDetails? streamUsage = null;
        Exception? terminalError = null;
        IReadOnlyDictionary<string, object?>? outputs = null;

        // PSQ-streaming-hooks: OnBefore runs once before the LM streaming call. A throw aborts
        // the stream entirely (hook can also short-circuit by populating hookCtx.Outputs).
        if (hookPipeline is not null)
        {
            activity?.AddEvent(new ActivityEvent("SignalGrid.Ampelum.hook.before"));
            try
            {
                await hookPipeline.RunBeforeAsync(hookCtx!, ct).ConfigureAwait(false);
            }
            catch (Exception hookEx)
            {
                terminalError = hookEx;
            }
        }

        // If OnBefore wrote Outputs (PSQ-11 short-circuit) or threw, skip the LM call entirely.
        bool shortCircuited = terminalError is null && hookCtx?.Outputs is not null;
        bool runStream = terminalError is null && !shortCircuited;
        IAsyncEnumerator<ChatResponseUpdate>? enumerator = runStream
            ? resolved.Chat.GetStreamingResponseAsync(messages, chatOptions, ct).GetAsyncEnumerator(ct)
            : null;
        try
        {
            while (runStream)
            {
                ChatResponseUpdate? chunk = null;
                try
                {
                    if (!await enumerator!.MoveNextAsync().ConfigureAwait(false))
                        break;
                    chunk = enumerator.Current;
                }
                catch (Exception ex)
                {
                    terminalError = ex;
                    break;
                }

                chunkCount++;

                // C2: detect cache-hit annotation injected by AmpelumCachingChatClient.ReadCacheStreamingAsync
                if (!cacheHit
                    && chunk.AdditionalProperties is not null
                    && chunk.AdditionalProperties.TryGetValue("SignalGrid.Ampelum.cache_hit", out var hit)
                    && hit is true)
                {
                    cacheHit = true;
                }

                // I3: accumulate UsageDetails from UsageContent items in the chunk
                foreach (var content in chunk.Contents)
                {
                    if (content is UsageContent uc)
                        streamUsage = uc.Details;
                }

                state = streamAdapter.OnChunk(_meta, state, chunk);

                // C1: derive clean partial text from the adapter state (markers stripped).
                // CompletedFields holds the accumulated literal text for each field after markers are removed.
                // PendingTail is suppressed because it may contain a partial marker prefix.
                string partialText = "";
                if (!string.IsNullOrEmpty(state.CurrentFieldName)
                    && state.CompletedFields.TryGetValue(state.CurrentFieldName, out var fieldVal)
                    && fieldVal is string fieldStr)
                {
                    partialText = fieldStr;
                }

                yield return new PredictionUpdate<TSig>(
                    FieldName: state.CurrentFieldName,
                    PartialText: partialText,
                    IsFieldComplete: false,
                    IsFinal: false,
                    Value: default,
                    Trace: null);
            }
        }
        finally
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        // Finalize path: only attempted when no terminal error yet, and not on the OnBefore
        // short-circuit path (where Outputs came from a hook, not the streaming adapter).
        if (terminalError is null && !shortCircuited)
        {
            try { outputs = streamAdapter.Finalize(_meta, state); }
            catch (Exception ex) { terminalError = ex; }
        }
        if (shortCircuited)
        {
            outputs = hookCtx!.Outputs;
        }

        streamElapsed = Stopwatch.GetElapsedTime(streamStartTs);

        // Publish LM-call signals into the hook context for OnAfter parity with Forward.
        if (hookCtx is not null && terminalError is null && !shortCircuited)
        {
            // ChatResponseUpdate stream may carry per-chunk ModelId; capture the last non-null seen.
            // For deterministic FakeChatClient streams ModelId is typically null on chunks, so fall
            // back to default options to mirror Forward's "unknown" sentinel.
            hookCtx.Set(HookMetadataKeys.ModelId,
                resolved.DefaultChatOptions?.ModelId ?? "unknown");
            hookCtx.Set(HookMetadataKeys.PromptTokens, streamUsage?.InputTokenCount ?? 0L);
            hookCtx.Set(HookMetadataKeys.CompletionTokens, streamUsage?.OutputTokenCount ?? 0L);
            hookCtx.Set(HookMetadataKeys.LmCallDurationMs, streamElapsed.TotalMilliseconds);
            hookCtx.Outputs = outputs;
        }

        if (hookPipeline is not null && terminalError is null)
        {
            activity?.AddEvent(new ActivityEvent("SignalGrid.Ampelum.hook.after"));
            try
            {
                await hookPipeline.RunAfterAsync(hookCtx!, ct).ConfigureAwait(false);
            }
            catch (Exception hookEx)
            {
                terminalError = hookEx;
            }
        }

        if (terminalError is null)
        {
            Diagnostics.ModuleStreamDuration.Record(streamElapsed.TotalMilliseconds,
                new("SignalGrid.Ampelum.module", nameof(Predict)), new("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));
            Diagnostics.ModuleStreamChunks.Record(chunkCount,
                new("SignalGrid.Ampelum.module", nameof(Predict)), new("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));

            // I3: report usage counters if any chunk carried a UsageContent
            if (streamUsage?.InputTokenCount is long ti)
            {
                Diagnostics.TokensInput.Add(ti);
                activity?.SetTag("gen_ai.usage.input_tokens", ti);
            }
            if (streamUsage?.OutputTokenCount is long to)
            {
                Diagnostics.TokensOutput.Add(to);
                activity?.SetTag("gen_ai.usage.output_tokens", to);
            }

            if (resolved.Usage is not null)
                resolved.Usage.Record(streamUsage, cacheHit: cacheHit);

            var rawResponse = state.AccumulatedText + state.PendingTail;
            var value = RecordCloner.CloneWith(inputs, outputs!);

            // C2 + I3: build metadata and populate trace fields
            var successMeta = new Dictionary<string, object?>();
            if (cacheHit)
            {
                successMeta[TraceKeys.CacheHit] = true;
                activity?.SetTag("SignalGrid.Ampelum.cache_hit", true);
            }

            var successTrace = new TraceRecord(
                Id: traceId,
                ModuleName: resolved.SubmoduleName ?? nameof(Predict),
                SignatureType: _meta.SignatureType,
                Inputs: inputsDict,
                Outputs: outputs!,
                PromptMessages: messages,
                RawResponse: rawResponse,
                Usage: streamUsage,
                Duration: streamElapsed,
                Error: null,
                Metadata: successMeta);
            SafeSinkWrite(resolved, logger, successTrace);

            // C1: final partial text â€” the finalized field values from the adapter
            string finalPartialText = "";
            if (!string.IsNullOrEmpty(state.CurrentFieldName)
                && outputs!.TryGetValue(state.CurrentFieldName, out var finalVal)
                && finalVal is string finalStr)
            {
                finalPartialText = finalStr;
            }

            yield return new PredictionUpdate<TSig>(
                FieldName: state.CurrentFieldName,
                PartialText: finalPartialText,
                IsFieldComplete: true,
                IsFinal: true,
                Value: value,
                Trace: successTrace);
            yield break;
        }

        // Error path â€” emit trace, tag activity, then throw.
        var errMeta = new Dictionary<string, object?>();
        if (terminalError is AdapterParseException)
            errMeta[TraceKeys.StreamingParseFailure] = true;
        var errTrace = new TraceRecord(
            Id: traceId,
            ModuleName: resolved.SubmoduleName ?? nameof(Predict),
            SignatureType: _meta.SignatureType,
            Inputs: inputsDict,
            Outputs: new Dictionary<string, object?>(),
            PromptMessages: messages,
            RawResponse: state.AccumulatedText + state.PendingTail,
            Usage: null,
            Duration: streamElapsed,
            Error: terminalError,
            Metadata: errMeta);
        SafeSinkWrite(resolved, logger, errTrace);
        activity?.SetStatus(ActivityStatusCode.Error, terminalError.Message);
        activity?.SetTag("exception.type", terminalError.GetType().FullName);
        activity?.SetTag("exception.message", terminalError.Message);
        if (hookPipeline is not null)
            await hookPipeline.RunErrorAsync(hookCtx!, terminalError, ct).ConfigureAwait(false);
        throw terminalError;
    }

    IReadOnlyDictionary<string, object?> ProjectInputs(TSig inputs)
    {
        // Fast path: AOT-strict source generator registered delegate accessors at module init.
        if (SignatureMetadataCache.TryGetAccessors(typeof(TSig), out var accessors))
        {
            var fast = new Dictionary<string, object?>(_meta.Inputs.Count);
            for (var i = 0; i < _meta.Inputs.Count; i++)
            {
                var getter = (Func<TSig, object?>)accessors![i];
                fast[_meta.Inputs[i].Name] = getter(inputs);
            }
            return fast;
        }

        var type = inputs.GetType();
        // Cache PropertyInfo[] ordered to match _meta.Inputs so hot calls are reflection-free.
        var props = _inputPropCache.GetOrAdd(type, t =>
        {
            var result = new PropertyInfo[_meta.Inputs.Count];
            for (var i = 0; i < _meta.Inputs.Count; i++)
                result[i] = t.GetProperty(_meta.Inputs[i].Name)!;
            return result;
        });
        var dict = new Dictionary<string, object?>(_meta.Inputs.Count);
        for (var i = 0; i < props.Length; i++)
            dict[_meta.Inputs[i].Name] = props[i].GetValue(inputs);
        return dict;
    }

    // v1.0-rc1 P1: returns a cached ILogger<Predict<TSig>> when the same
    // factory is observed back-to-back (the universal case in production).
    // Falls back to a one-shot CreateLogger when the factory changes.
    ILogger GetOrCreateLogger(ILoggerFactory factory)
    {
        if (ReferenceEquals(_cachedLoggerFactory, factory) && _cachedLogger is { } cached)
            return cached;
        var fresh = factory.CreateLogger<Predict<TSig>>();
        _cachedLoggerFactory = factory;
        _cachedLogger = fresh;
        return fresh;
    }

    ChatOptions? BuildOptions(AmpelumContext ctx)
    {
        if (ctx.Adapter is not IStructuredOutputAdapter structured) return ctx.DefaultChatOptions;
        var cloned = ctx.DefaultChatOptions?.Clone() ?? new ChatOptions();
        cloned.ResponseFormat = structured.BuildResponseFormat(_meta);
        return cloned;
    }

    void SafeSinkWrite(AmpelumContext ctx, ILogger logger, TraceRecord record)
    {
        try { ctx.TraceSink.Write(record); }
        catch (Exception ex) { AmpelumLog.TraceSinkThrew(logger, ex); }
    }

    /// <summary>
    /// Maps a <see cref="ChatClientMetadata.ProviderName"/> to a bounded <c>gen_ai.system</c> enum
    /// per OTel GenAI semconv 1.27.0. The enum is closed (allowed values listed in the v0.7
    /// high-cardinality allowlist), so unknown providers collapse to <c>"unknown"</c> rather than
    /// leaking arbitrary user-supplied strings into span attributes.
    /// </summary>
    static string InferGenAiSystem(IChatClient client)
    {
        var md = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;
        var name = md?.ProviderName;
        if (string.IsNullOrEmpty(name)) return "unknown";
        // Case-insensitive match against the closed allowlist.
        if (name.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)) return "openai";
        if (name.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)) return "anthropic";
        if (name.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase)) return "openrouter";
        return "unknown";
    }

    /// <summary>
    /// PSQ-5 (v0.9g): resolves the effective deadline from <see cref="AmpelumContext.Deadline"/>
    /// and <c>HookContext.Metadata["deadline.utc"]</c> (the EARLIER of the two wins) and throws
    /// <see cref="Exceptions.DeadlineExceededException"/> when the current UTC time has crossed it.
    /// </summary>
    /// <summary>
    /// PSQ-5 (v0.9g) deadline check helper. Resolves the effective deadline from
    /// <see cref="AmpelumContext.Deadline"/> and <c>HookContext.Metadata["deadline.utc"]</c>
    /// (the EARLIER of the two wins) and throws <see cref="Exceptions.DeadlineExceededException"/>
    /// when the current UTC time has crossed it. Internal pending v0.9h Predict integration.
    /// </summary>
    internal static void CheckDeadline(AmpelumContext ctx, HookContext? hookCtx, string phase)
    {
        DateTimeOffset? deadline = ctx.Deadline;
        if (hookCtx is not null
            && hookCtx.Metadata.TryGetValue(HookMetadataKeys.DeadlineUtc, out var raw)
            && raw is DateTimeOffset perCall)
        {
            deadline = deadline is null || perCall < deadline.Value ? perCall : deadline;
        }
        if (deadline.HasValue && DateTimeOffset.UtcNow >= deadline.Value)
            throw new Exceptions.DeadlineExceededException(deadline.Value, phase);
    }

    void WriteTrace(AmpelumContext ctx, ILogger logger, Guid id,
        IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, object?> outputs,
        IReadOnlyList<ChatMessage> messages, string rawResponse, UsageDetails? usage, TimeSpan duration, Exception? error)
        => SafeSinkWrite(ctx, logger, new TraceRecord(id, ctx.SubmoduleName ?? nameof(Predict), _meta.SignatureType,
            inputs, outputs, messages, rawResponse, usage, duration, error, EmptyMetadata));

    /// <summary>Explicit <see cref="IModule{TIn, TOut}"/> implementation. Delegates to the
    /// canonical <c>ForwardAsync(inputs, demos: null, ct)</c> so per-call demos resolve from
    /// <see cref="AmpelumContext.DefaultDemos"/> on the chosen context.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
    Task<Prediction<TSig>> IModule<TSig, TSig>.ForwardAsync(TSig inputs, CancellationToken ct)
        => ForwardAsync(inputs, demos: null, ct);
}
