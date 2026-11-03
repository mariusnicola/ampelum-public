using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Context;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Hooks.Internal;
using SelSelReq = SignalGrid.Ampelum.Context.Selection.SelectionRequest;
using SelPlan = SignalGrid.Ampelum.Context.Selection.SelectionPlan;
using SelSelector = SignalGrid.Ampelum.Context.Selection.IContextSelector;

namespace SignalGrid.Ampelum;

/// <summary>
/// An agentic free-form chat loop that wires an <see cref="AmpelumContext"/> (hook pipeline,
/// trace sink) to a streaming LM call, tool dispatch, and cost tracking. Analogous to
/// <see cref="Predict{TSig}"/> but for unstructured interactive chat rather than typed I/O.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class StreamingChatSession
{
    readonly AmpelumContext _ctx;
    readonly IList<AITool>? _tools;
    readonly ModelRateTable? _rates;
    readonly IEventStore? _eventStore;
    readonly SelSelector? _contextSelector;
    readonly List<ChatMessage> _history;
    int _turnCounter;
    readonly string _currentMode;

    /// <summary>Usage from the most recent <see cref="SendAsync"/> call, or <see langword="null"/> before the first call.</summary>
    public ChatCostSummary? LastUsage { get; private set; }

    /// <summary>Cumulative usage across all <see cref="SendAsync"/> calls in this session.</summary>
    public ChatCostSummary SessionTotal { get; private set; } = ChatCostSummary.Zero;

    /// <summary>The live conversation history including the system prompt.</summary>
    public IList<ChatMessage> History => _history;

    /// <summary>
    /// Initialises a new <see cref="StreamingChatSession"/>.
    /// </summary>
    /// <param name="ctx">Ampelum runtime context supplying the chat client, hooks, and trace sink.</param>
    /// <param name="systemPrompt">The system message prepended to every LM call.</param>
    /// <param name="tools">Optional tools available for the LM to call.</param>
    /// <param name="priorHistory">Optional prior conversation to restore (resume mode); excludes the system prompt. Only used when <paramref name="eventStore"/> is not provided — when eventStore is present it is the canonical source of history.</param>
    /// <param name="rates">Optional per-token pricing used to populate <see cref="ChatCostSummary.EstimatedCostUsd"/>.</param>
    /// <param name="eventStore">Optional typed event store for context-aware sessions. When provided with <paramref name="contextSelector"/>, context is derived per turn instead of flat history.</param>
    /// <param name="contextSelector">Optional selector that plans which events to include per turn. Requires <paramref name="eventStore"/>.</param>
    /// <param name="mode">Optional mode string passed to the context selector. Defaults to "talk".</param>
    public StreamingChatSession(
        AmpelumContext ctx,
        string systemPrompt,
        IList<AITool>? tools = null,
        IList<ChatMessage>? priorHistory = null,
        ModelRateTable? rates = null,
        IEventStore? eventStore = null,
        SelSelector? contextSelector = null,
        string? mode = null)
    {
        _ctx = ctx;
        _tools = tools;
        _rates = rates;
        _eventStore = eventStore;
        _contextSelector = contextSelector;
        _currentMode = mode ?? "talk";
        _history = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
        // Only restore priorHistory when the event store is absent — otherwise
        // the EventStore is the canonical source and _history should start fresh.
        if (priorHistory is not null && _eventStore is null)
            _history.AddRange(priorHistory);
    }

    static string MakeKey(string type, int turn, int seq, string payload) =>
        EventKey.Make(type, turn, seq, payload);

    /// <summary>
    /// Sends <paramref name="userMessage"/> to the LM and streams the response tokens.
    /// Fires the hook pipeline (OnBefore/OnAfter/OnError), dispatches tool calls in a loop,
    /// writes a <see cref="TraceRecord"/> to <see cref="AmpelumContext.TraceSink"/>, and updates
    /// <see cref="LastUsage"/> and <see cref="SessionTotal"/> after the full turn completes.
    /// </summary>
    /// <param name="userMessage">The user's message for this turn.</param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<string> SendAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var turn = ++_turnCounter;
        var now = DateTimeOffset.UtcNow;
        var seq = 0;

        // Acquire writer lock for the duration of the turn (Bug 1 fix)
        IAsyncDisposable? writerLock = null;
        if (_eventStore is not null)
        {
            writerLock = await _eventStore.AcquireWriterLockAsync(ct).ConfigureAwait(false);
        }
        try
        {
            // Write UserMessage event to the event store at start of turn
            if (_eventStore is not null)
            {
                var userEvent = new UserMessage(turn, seq++, now, MakeKey("user_msg", turn, seq, userMessage), null, userMessage);
                await _eventStore.AppendAsync(userEvent, ct).ConfigureAwait(false);
            }

            _history.Add(new ChatMessage(ChatRole.User, userMessage));
            var historyAfterUserMessage = _history.Count;

            var hookPipeline = _ctx.Hooks is { } h ? HookPipeline.GetOrCreate(h) : null;
            var hookCtx = new HookContext
            {
                ModuleName = "StreamingChatSession",
                Inputs = new Dictionary<string, object?> { ["message"] = userMessage }
            };
            hookCtx.Set(HookMetadataKeys.Streamed, true);
            if (_tools is { Count: > 0 })
                hookCtx.Set(Safety.Tools.ToolGateHook.OfferedKey, _tools.Select(t => t.Name).ToArray());

            Exception? terminalError = null;
            long totalInput = 0, totalOutput = 0;
            string? lastModelId = null;
            decimal? totalCost = _rates is null ? null : 0m;
            var startTs = Stopwatch.GetTimestamp();
            var promptSnapshot = _history.ToArray();
            var fullResponse = new StringBuilder();

            if (hookPipeline is not null)
            {
                try { await hookPipeline.RunBeforeAsync(hookCtx, ct).ConfigureAwait(false); }
                catch (Exception ex) { terminalError = ex; }
            }

            if (terminalError is null)
            {
                var options = _tools is { Count: > 0 } ? new ChatOptions { Tools = _tools } : null;

                // Derive context from event store via selector when both are available.
                // Use a mutable List so we can append assistant + tool results during the
                // tool-call loop — without this the LLM sees the same frozen context on
                // every iteration and becomes forgetful/repetitive.
                //
                // When the selector path is active, BuildContextFromPlan only includes
                // PAST events from the event store — the current turn's user message must
                // be added explicitly so the LLM knows what was asked.
                List<ChatMessage> contextHistory;
                if (_eventStore is not null && _contextSelector is not null)
                {
                    try
                    {
                        var request = new SelSelReq(
                            Mode: _currentMode,
                            UserText: userMessage,
                            PinnedKeys: new HashSet<string>(),
                            DroppedKeys: new HashSet<string>(),
                            ChapterIndex: null,
                            Retriever: null);
                        var plan = await _contextSelector.PlanAsync(_eventStore, request, ct).ConfigureAwait(false);
                        contextHistory = BuildContextFromPlan(_history[0], plan);
                        // Current turn's user message is not yet in the event store, so the
                        // selector plan won't include it. Add it explicitly — without this
                        // the LLM has prior context but doesn't know what was just asked.
                        contextHistory.Add(new ChatMessage(ChatRole.User, userMessage));
                    }
                    catch
                    {
                        // Fall back to flat history on selector failure
                        contextHistory = new List<ChatMessage>(_history);
                    }
                }
                else
                {
                    contextHistory = new List<ChatMessage>(_history);
                }

                while (true)
                {
                    var chunks = new List<ChatResponseUpdate>();
                    long stepInput = 0, stepOutput = 0;
                    string? stepModelId = null;
                    var enumerator = _ctx.Chat
                        .GetStreamingResponseAsync(contextHistory, options, ct)
                        .GetAsyncEnumerator(ct);
                    try
                    {
                        while (true)
                        {
                            ChatResponseUpdate? chunk = null;
                            try
                            {
                                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                                chunk = enumerator.Current;
                            }
                            catch (Exception ex) { terminalError = ex; break; }

                            chunks.Add(chunk);
                            foreach (var c in chunk.Contents)
                                if (c is UsageContent uc)
                                {
                                    totalInput += uc.Details.InputTokenCount ?? 0;
                                    totalOutput += uc.Details.OutputTokenCount ?? 0;
                                    stepInput += uc.Details.InputTokenCount ?? 0;
                                    stepOutput += uc.Details.OutputTokenCount ?? 0;
                                }
                            if (chunk.ModelId is { } mid) { lastModelId = mid; stepModelId = mid; }

                            if (chunk.Text is { Length: > 0 } t)
                            {
                                fullResponse.Append(t);
                                yield return t;
                            }
                        }
                    }
                    finally { await enumerator.DisposeAsync().ConfigureAwait(false); }

                    // Each model call is priced at the rates of the model that answered it.
                    if (_rates is not null)
                        totalCost += _rates.EstimateCost(stepModelId ?? string.Empty, stepInput, stepOutput);

                    if (terminalError is not null) break;

                    var response = chunks.ToChatResponse();
                    var assistantMsg = response.Messages[^1];
                    _history.Add(assistantMsg);
                    contextHistory.Add(assistantMsg);     // so LLM sees its own response on next loop iteration

                    var toolCalls = assistantMsg.Contents
                        .OfType<FunctionCallContent>().ToList();
                    if (toolCalls.Count == 0) break;

                    foreach (var call in toolCalls)
                    {
                        var func = _tools?.OfType<AIFunction>()
                            .FirstOrDefault(f => f.Name == call.Name);
                        if (func is null)
                        {
                            // Write ToolCall for missing tool
                            if (_eventStore is not null)
                            {
                                var argsJson = call.Arguments is { } a
                                    ? System.Text.Json.JsonSerializer.Serialize(a.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""))
                                    : "{}";
                                var toolCallEvent = new ToolCall(turn, seq++, now, MakeKey("tool_call", turn, seq, call.Name + argsJson), null, call.Name, argsJson);
                                await _eventStore.AppendAsync(toolCallEvent, ct).ConfigureAwait(false);
                            }
                            AddToolResult(call.CallId, $"Tool '{call.Name}' not found", contextHistory);
                            continue;
                        }
                        try
                        {
                            var invokeArgs = call.Arguments is { } a ? new AIFunctionArguments(a) : null;
                            var result = await func.InvokeAsync(invokeArgs, ct).ConfigureAwait(false);
                            var resultStr = result?.ToString() ?? "";

                            // Write ToolCall and ToolResult events
                            if (_eventStore is not null)
                            {
                                var argsJson = call.Arguments is { } ca
                                    ? System.Text.Json.JsonSerializer.Serialize(ca.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""))
                                    : "{}";
                                var toolCallKey = MakeKey("tool_call", turn, seq, call.Name + argsJson);
                                var toolCallEvent = new ToolCall(turn, seq++, now, toolCallKey, null, call.Name, argsJson);
                                await _eventStore.AppendAsync(toolCallEvent, ct).ConfigureAwait(false);
                                var resultBytes = System.Text.Encoding.UTF8.GetByteCount(resultStr);
                                var resultHash = ContentHasher.HashHex(resultStr);
                                var toolResultKey = MakeKey("tool_result", turn, seq, resultStr);
                                var toolResultEvent = new ToolResult(turn, seq++, now, toolResultKey, null, toolCallKey, resultStr, resultBytes, resultHash, null, null);
                                await _eventStore.AppendAsync(toolResultEvent, ct).ConfigureAwait(false);
                            }
                            AddToolResult(call.CallId, result, contextHistory);
                        }
                        catch (Exception ex)
                        {
                            var error = $"ERROR: {ex.Message}";
                            // Write ToolCall + failed ToolResult events
                            if (_eventStore is not null)
                            {
                                var argsJson = call.Arguments is { } ca
                                    ? System.Text.Json.JsonSerializer.Serialize(ca.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""))
                                    : "{}";
                                var toolCallKey = MakeKey("tool_call", turn, seq, call.Name + argsJson);
                                var toolCallEvent = new ToolCall(turn, seq++, now, toolCallKey, null, call.Name, argsJson);
                                await _eventStore.AppendAsync(toolCallEvent, ct).ConfigureAwait(false);
                                var errorBytes = System.Text.Encoding.UTF8.GetByteCount(error);
                                var errorHash = ContentHasher.HashHex(error);
                                var toolResultKey = MakeKey("tool_result", turn, seq, error);
                                var toolResultEvent = new ToolResult(turn, seq++, now, toolResultKey, null, toolCallKey, error, errorBytes, errorHash, null, null);
                                await _eventStore.AppendAsync(toolResultEvent, ct).ConfigureAwait(false);
                            }
                            AddToolResult(call.CallId, error, contextHistory);
                        }
                    }
                }
            }

            // Post-processing runs after the last yield — before the caller's foreach completes.
            var elapsed = Stopwatch.GetElapsedTime(startTs);
            hookCtx.PromptTokens = totalInput;
            hookCtx.CompletionTokens = totalOutput;
            if (lastModelId is not null) hookCtx.ModelId = lastModelId;

            if (hookPipeline is not null)
            {
                if (terminalError is null)
                {
                    // The after hooks (moderation, PII redaction, audit) work on outputs, so they get the whole reply.
                    hookCtx.Outputs = new Dictionary<string, object?> { ["text"] = fullResponse.ToString() };
                    try { await hookPipeline.RunAfterAsync(hookCtx, ct).ConfigureAwait(false); }
                    catch
                    {
                        // The turn failed: the reply (and any tool exchange) does not stay in the history.
                        _history.RemoveRange(historyAfterUserMessage, _history.Count - historyAfterUserMessage);
                        throw;
                    }
                }
                else
                    await hookPipeline.RunErrorAsync(hookCtx, terminalError, CancellationToken.None)
                        .ConfigureAwait(false);
            }

            // Write the AssistantText event only now: a reply the after hooks blocked threw above and is never stored,
            // so a context selector cannot send it to the model on a later turn.
            var responseText = fullResponse.Length > 0 ? fullResponse.ToString() : "";
            if (responseText.Length > 0 && _eventStore is not null)
            {
                var assistantEvent = new AssistantText(turn, seq++, now, MakeKey("assistant", turn, seq, responseText), null, responseText);
                await _eventStore.AppendAsync(assistantEvent, ct).ConfigureAwait(false);
            }

            _ctx.TraceSink.Write(new TraceRecord(
                Id: Guid.NewGuid(),
                ModuleName: "StreamingChatSession",
                SignatureType: typeof(StreamingChatSession),
                Inputs: new Dictionary<string, object?> { ["message"] = userMessage },
                Outputs: terminalError is null
                    ? new Dictionary<string, object?> { ["text"] = fullResponse.ToString() }
                    : new Dictionary<string, object?>(),
                PromptMessages: promptSnapshot,
                RawResponse: fullResponse.ToString(),
                Usage: totalInput > 0 || totalOutput > 0
                    ? new UsageDetails { InputTokenCount = (int)totalInput, OutputTokenCount = (int)totalOutput }
                    : null,
                Duration: elapsed,
                Error: terminalError,
                Metadata: new Dictionary<string, object?>(hookCtx)));

            if (terminalError is not null)
                ExceptionDispatchInfo.Capture(terminalError).Throw();

            var summary = new ChatCostSummary(totalInput, totalOutput, totalCost);
            LastUsage = summary;
            SessionTotal += summary;
        }
        finally
        {
            if (writerLock is not null)
                await writerLock.DisposeAsync().ConfigureAwait(false);
        }
    }

        void AddToolResult(string? callId, object? result, List<ChatMessage> context)
        {
            var toolMsg = new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(callId ?? string.Empty, result)]);
            _history.Add(toolMsg);
            context.Add(toolMsg);   // LLM must see tool results on next loop iteration
        }

    /// <summary>
    /// Builds a context-aware chat message list from a selection plan, replacing the plain history
    /// with a structured context that includes the plan prefix and selected events.
    /// Bug 2 fix: maps event source strings to the correct ChatRole.
    /// </summary>
    static List<ChatMessage> BuildContextFromPlan(ChatMessage systemMessage, SelPlan plan)
    {
        var messages = new List<ChatMessage> { systemMessage };

        // Add contextual prefix from the selection plan
        var prefixBuilder = new StringBuilder();
        prefixBuilder.AppendLine(plan.Prefix.ModeBanner);
        if (!string.IsNullOrEmpty(plan.Prefix.GoalAnchor))
            prefixBuilder.AppendLine($"Goal: {plan.Prefix.GoalAnchor}");
        prefixBuilder.AppendLine($"\nSelected context (~{plan.EstimatedTokens} tokens):");

        messages.Add(new ChatMessage(ChatRole.System, prefixBuilder.ToString()));

        // Add rendered selected events with correct role mapping
        foreach (var evt in plan.SelectedEvents)
        {
            var role = evt.Source switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                "tool_call" => ChatRole.Assistant,  // tool calls are assistant-originated
                "tool_result" => ChatRole.Tool,
                "mode_transition" => ChatRole.System,
                "chapter_boundary" => ChatRole.System,
                "decision" => ChatRole.Assistant,
                _ => ChatRole.System,
            };
            messages.Add(new ChatMessage(role, evt.Body));
        }

        return messages;
    }
}