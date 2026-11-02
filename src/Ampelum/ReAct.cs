using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Internal;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum;

/// <summary>
/// A Ampelum module that implements the ReAct (Reasoning + Acting) loop.
/// Enforces a single output field on <typeparamref name="TSig"/>; runs a self-driven tool-call
/// loop up to <c>maxIterations</c>, then emits one <see cref="TraceRecord"/> with
/// <see cref="TraceKeys.ToolCalls"/> and <see cref="TraceKeys.Reasoning"/> metadata.
/// One <c>ampelum.react.tool_call</c> child activity is emitted per tool invocation.
/// </summary>
/// <typeparam name="TSig">The signature type; must declare exactly one output field.</typeparam>
public sealed class ReAct<TSig> where TSig : ISignature
{
    const string ModuleName = "ReAct";
    const string FinalAnswerMarker = "final_answer:";

    readonly AmpelumContext _ctx;
    readonly IReadOnlyList<AIFunction> _tools;
    readonly int _maxIterations;
    readonly SignatureMetadata _meta;
    readonly Field _outputField;

    /// <summary>
    /// Initialises a <see cref="ReAct{TSig}"/> bound to the given context.
    /// Throws <see cref="SignatureDefinitionException"/> if <typeparamref name="TSig"/> does not
    /// declare exactly one output field.
    /// </summary>
    /// <param name="context">Runtime context supplying the LM client, trace sink, and logging.</param>
    /// <param name="tools">The set of <see cref="AIFunction"/> tools available to the LM.</param>
    /// <param name="maxIterations">Maximum number of tool-call iterations before forcing a text response.</param>
    public ReAct(AmpelumContext context, IReadOnlyList<AIFunction> tools, int maxIterations = 5)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tools);
        _ctx = context;
        _tools = tools;
        _maxIterations = maxIterations;
        _meta = SignatureMetadataCache.For<TSig>();
        if (_meta.Outputs.Count != 1)
            throw new SignatureDefinitionException(
                $"ReAct<TSig> requires a signature with exactly one output field. " +
                $"TSig '{typeof(TSig).Name}' declares {_meta.Outputs.Count} output fields.");
        _outputField = _meta.Outputs[0];
    }

    /// <summary>
    /// v0.9a overload: initialises ReAct with a list of <see cref="IReActTool"/> typed tools.
    /// Each typed tool is wrapped in a delegate-based <see cref="AIFunction"/> behind the scenes
    /// so the existing ReAct loop is unchanged.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
#pragma warning disable AMPELUM_V09_PREVIEW
    public ReAct(AmpelumContext context, IReadOnlyList<IReActTool> typedTools, int maxIterations = 5)
        : this(context, BuildAIFunctions(typedTools), maxIterations) { }

    static IReadOnlyList<AIFunction> BuildAIFunctions(IReadOnlyList<IReActTool> typedTools)
    {
        ArgumentNullException.ThrowIfNull(typedTools);
        var list = new List<AIFunction>(typedTools.Count);
        foreach (var t in typedTools)
            list.Add(new TypedToolAIFunction(t));
        return list;
    }

    sealed class TypedToolAIFunction(IReActTool tool) : AIFunction
    {
        public override string Name => tool.Name;
        public override string Description => tool.Description;
        public override System.Text.Json.JsonElement JsonSchema =>
            System.Text.Json.JsonDocument.Parse(tool.ParametersJsonSchema).RootElement.Clone();

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var dict = arguments as IReadOnlyDictionary<string, object?>
                ?? new Dictionary<string, object?>(arguments);
            var json = System.Text.Json.JsonSerializer.Serialize(dict);
            return await tool.InvokeAsync(json, cancellationToken).ConfigureAwait(false);
        }
    }
#pragma warning restore AMPELUM_V09_PREVIEW

    /// <summary>
    /// Runs the ReAct forward pass: builds the conversation, iterates the tool loop,
    /// parses <c>final_answer:</c>, and returns a <see cref="Prediction{TSig}"/>.
    /// </summary>
    /// <param name="inputs">Populated signature record with all required input fields set.</param>
    /// <param name="demos">Optional few-shot demonstrations rendered as <c>final_answer:</c> pairs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Prediction{TSig}"/> containing the typed output and trace.</returns>
    public async Task<Prediction<TSig>> ForwardAsync(
        TSig inputs,
        IReadOnlyList<Example>? demos = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        demos ??= [];

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.module.forward");
        activity?.SetTag("SignalGrid.Ampelum.module", ModuleName);
        activity?.SetTag("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name);
        var traceId = Guid.NewGuid();
        activity?.SetTag("SignalGrid.Ampelum.trace_id", traceId.ToString());

        // Build initial conversation: system + optional demos (user/assistant pairs) + current input.
        var messages = new List<ChatMessage> { new(ChatRole.System, BuildSystem()) };
        foreach (var demo in demos)
        {
            messages.Add(new ChatMessage(ChatRole.User, RenderInputs(demo.Inputs)));
            messages.Add(new ChatMessage(ChatRole.Assistant, RenderDemoOutput(demo.Outputs)));
        }
        var inputsDict = ProjectInputs(inputs);
        messages.Add(new ChatMessage(ChatRole.User, RenderInputs(inputsDict)));

        // Build options with tools enabled.
        ChatOptions options;
        if (_ctx.DefaultChatOptions is { } defaults)
        {
            options = defaults.Clone();
            options.Tools = [.. _tools];
            options.ToolMode = ChatToolMode.Auto;
        }
        else
        {
            options = new ChatOptions { Tools = [.. _tools], ToolMode = ChatToolMode.Auto };
        }

        var toolCalls = new List<ToolCallRecord>();
        var reasoningParts = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<string, object?>? outputs = null;
        Exception? terminalError = null;

        try
        {
            // Main loop: iterate up to maxIterations.
            var iteration = 0;
            for (; iteration < _maxIterations; iteration++)
            {
#pragma warning disable AMPELUM_V09_PREVIEW
                var response = await HookedLmCall.InvokeAsync(_ctx, "ReAct.Reason", messages, options, ct).ConfigureAwait(false);
#pragma warning restore AMPELUM_V09_PREVIEW
                if (_ctx.Usage is not null)
                    _ctx.Usage.Record(response.Usage, cacheHit: false);
                var assistantMsg = response.Messages[^1];
                messages.Add(assistantMsg);

                var functionCalls = assistantMsg.Contents.OfType<FunctionCallContent>().ToList();
                if (functionCalls.Count == 0)
                {
                    // No tool calls â€” attempt to parse final_answer.
                    // M.E.AI Text is always non-null (string.Concat semantics)
                    reasoningParts.Add(response.Text!);
                    outputs = ProjectFinalAnswer(response.Text!);
                    break;
                }

                // Dispatch all tool calls in this response.
                foreach (var call in functionCalls)
                {
                    var fn = _tools.FirstOrDefault(t => t.Name == call.Name)
                        ?? throw new AdapterParseException(
                            _meta.SignatureType.Name, ModuleName, call.Name,
                            $"ReAct received a tool call for unknown function '{call.Name}'.");

                    var toolSw = Stopwatch.StartNew();
                    using var childActivity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.react.tool_call");
                    childActivity?.SetTag("tool.name", call.Name);
                    ToolCallRecord rec;
                    try
                    {
                        var fnArgs = new AIFunctionArguments(
                            call.Arguments ?? new Dictionary<string, object?>());
                        var result = await fn.InvokeAsync(fnArgs, ct).ConfigureAwait(false);
                        toolSw.Stop();
                        rec = ToolCallRecord.Success(
                            call.Name,
                            (IReadOnlyDictionary<string, object?>)(call.Arguments ?? new Dictionary<string, object?>()),
                            result,
                            toolSw.Elapsed);
                        childActivity?.SetTag("tool.success", true);
                        Diagnostics.ReactToolCalls.Add(1,
                            new("SignalGrid.Ampelum.tool.name", call.Name),
                            new("SignalGrid.Ampelum.tool.success", true));
                    }
                    catch (Exception toolEx) when (toolEx is not OperationCanceledException)
                    {
                        toolSw.Stop();
                        rec = ToolCallRecord.Failed(
                            call.Name,
                            (IReadOnlyDictionary<string, object?>)(call.Arguments ?? new Dictionary<string, object?>()),
                            toolEx,
                            toolSw.Elapsed);
                        childActivity?.SetTag("tool.success", false);
                        Diagnostics.ReactToolCalls.Add(1,
                            new("SignalGrid.Ampelum.tool.name", call.Name),
                            new("SignalGrid.Ampelum.tool.success", false));
                    }
                    childActivity?.SetTag("tool.duration_ms", toolSw.Elapsed.TotalMilliseconds);
                    toolCalls.Add(rec);

                    // Feed tool result back into the conversation.
                    var resultContent = rec.Error is null ? rec.Result : (object?)rec.Error.Message;
                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(call.CallId, resultContent)]));
                }
            }

            if (outputs is null)
            {
                // Max iterations reached with tool calls still pending.
                // One final call with Tools=null to force a text response.
                var forcedOptions = options.Clone();
                forcedOptions.Tools = null;
                forcedOptions.ToolMode = null;
#pragma warning disable AMPELUM_V09_PREVIEW
                var forced = await HookedLmCall.InvokeAsync(_ctx, "ReAct.ForceFinalAnswer", messages, forcedOptions, ct).ConfigureAwait(false);
#pragma warning restore AMPELUM_V09_PREVIEW
                if (_ctx.Usage is not null)
                    _ctx.Usage.Record(forced.Usage, cacheHit: false);
                messages.Add(forced.Messages[^1]);
                reasoningParts.Add(forced.Text);
                try
                {
                    outputs = ProjectFinalAnswer(forced.Text);
                }
                catch (AdapterParseException)
                {
                    throw new ReActIterationLimitException(
                        _maxIterations,
                        $"ReAct<{typeof(TSig).Name}> exhausted {_maxIterations} iterations without producing a final answer.");
                }
            }

            stopwatch.Stop();
            Diagnostics.ReactIterations.Record(
                toolCalls.Count == 0 ? 1 : iteration + 1,
                new KeyValuePair<string, object?>("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));

            var value = RecordCloner.CloneWith(inputs, outputs);
            var metadata = new Dictionary<string, object?>
            {
                [TraceKeys.ToolCalls] = (IReadOnlyList<ToolCallRecord>)toolCalls,
                [TraceKeys.Reasoning] = string.Join("\n", reasoningParts),
            };
            var trace = new TraceRecord(
                traceId,
                ModuleName,
                _meta.SignatureType,
                inputsDict,
                outputs,
                messages,
                messages[^1].Text,
                Usage: null,
                stopwatch.Elapsed,
                Error: null,
                metadata);
            _ctx.TraceSink.Write(trace);
            return new Prediction<TSig>(value, trace);
        }
        catch (Exception ex)
        {
            if (stopwatch.IsRunning) stopwatch.Stop();
            terminalError = ex;
            var metadata = new Dictionary<string, object?>
            {
                [TraceKeys.ToolCalls] = (IReadOnlyList<ToolCallRecord>)toolCalls,
                [TraceKeys.Reasoning] = string.Join("\n", reasoningParts),
            };
            _ctx.TraceSink.Write(new TraceRecord(
                traceId,
                ModuleName,
                _meta.SignatureType,
                inputsDict,
                outputs ?? new Dictionary<string, object?>(),
                messages,
                messages[^1].Text,
                Usage: null,
                stopwatch.Elapsed,
                terminalError,
                metadata));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            activity?.SetTag("exception.message", ex.Message);
            throw;
        }
    }

    string BuildSystem()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_meta.Instructions))
            sb.AppendLine(_meta.Instructions).AppendLine();
        sb.AppendLine("Input fields:");
        foreach (var f in _meta.Inputs)  sb.AppendLine($"- {f.Name} ({f.Description})");
        sb.AppendLine("Output fields:");
        foreach (var f in _meta.Outputs) sb.AppendLine($"- {f.Name} ({f.Description})");
        sb.AppendLine("Tools:");
        foreach (var t in _tools)
            sb.AppendLine($"- {t.Name}: {t.Description}");
        sb.AppendLine();
        sb.AppendLine("Think step-by-step. Use tools when needed. When you have the final answer, respond with 'final_answer: <value>'.");
        return sb.ToString();
    }

    static string RenderInputs(IReadOnlyDictionary<string, object?> inputs)
    {
        var sb = new StringBuilder();
        foreach (var kv in inputs)
            sb.Append(kv.Key).Append(": ").AppendLine(Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
        return sb.ToString().TrimEnd();
    }

    static string RenderDemoOutput(IReadOnlyDictionary<string, object?> outs)
    {
        // Demos for ReAct are rendered in final_answer form.
        var value = outs.Values.FirstOrDefault();
        return $"final_answer: {Convert.ToString(value, CultureInfo.InvariantCulture)}";
    }

    IReadOnlyDictionary<string, object?> ProjectInputs(TSig inputs)
    {
        var d = new Dictionary<string, object?>(_meta.Inputs.Count);
        foreach (var f in _meta.Inputs)
            d[f.Name] = typeof(TSig).GetProperty(f.Name)!.GetValue(inputs);
        return d;
    }

    /// <summary>
    /// Parses the LM text for a <c>final_answer:</c> marker and converts the extracted value
    /// to the declared output field's CLR type. Throws <see cref="AdapterParseException"/> when
    /// the marker is absent or the value cannot be converted.
    /// </summary>
    IReadOnlyDictionary<string, object?> ProjectFinalAnswer(string text)
    {
        var idx = text.IndexOf(FinalAnswerMarker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            throw new AdapterParseException(
                _meta.SignatureType.Name, ModuleName, text,
                $"response did not contain 'final_answer:' marker");
        var raw = text[(idx + FinalAnswerMarker.Length)..].Trim();
        var targetType = _outputField.ClrType;
        object? value;
        if (targetType == typeof(string))
        {
            value = raw;
        }
        else if (targetType.IsPrimitive)
        {
            try { value = Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture); }
            catch (Exception ex)
            {
                throw new AdapterParseException(
                    _meta.SignatureType.Name, ModuleName, raw, ex.Message);
            }
        }
        else
        {
            try { value = JsonSerializer.Deserialize(raw, targetType, JsonDefaults.Options); }
            catch (JsonException ex)
            {
                throw new AdapterParseException(
                    _meta.SignatureType.Name, ModuleName, raw, ex.Message);
            }
        }
        return new Dictionary<string, object?> { [_outputField.Name] = value };
    }
}
