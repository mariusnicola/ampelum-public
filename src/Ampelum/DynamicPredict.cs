using System.Diagnostics;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum;

/// <summary>Untyped predict module consuming runtime-constructed <see cref="SignatureMetadata"/>.</summary>
public sealed class DynamicPredict
{
    readonly AmpelumContext _ctx;

    /// <summary>Initialises a new <see cref="DynamicPredict"/> bound to the given context.</summary>
    public DynamicPredict(AmpelumContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctx = context;
    }

    /// <summary>Creates a <see cref="DynamicPredict"/> wired to the given <paramref name="chat"/> client with sensible defaults.</summary>
    public static DynamicPredict Create(
        IChatClient chat,
        IAdapter? adapter = null,
        ITraceSink? sink = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return new DynamicPredict(new AmpelumContext(
            chat,
            adapter ?? new ChatAdapter(),
            sink ?? NullTraceSink.Instance,
            loggerFactory ?? NullLoggerFactory.Instance));
    }

    /// <summary>Run one forward pass: validate inputs â†’ format â†’ LM call â†’ parse â†’ trace â†’ return.</summary>
    public async Task<Prediction<IReadOnlyDictionary<string, object?>>> ForwardAsync(
        SignatureMetadata signature,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyList<Example>? demos = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(inputs);
        demos ??= [];

        // Validate missing inputs before any LM call (outside the try so no trace is written).
        foreach (var f in signature.Inputs)
            if (!inputs.ContainsKey(f.Name))
                throw new AdapterFormatException($"Missing input '{f.Name}' for DynamicPredict.");

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.module.forward");
        activity?.SetTag("SignalGrid.Ampelum.module", nameof(DynamicPredict));
        activity?.SetTag("SignalGrid.Ampelum.signature_type", signature.SignatureType.Name);
        var traceId = Guid.NewGuid();
        activity?.SetTag("SignalGrid.Ampelum.trace_id", traceId.ToString());

        var stopwatch = Stopwatch.StartNew();
        ChatResponse? response = null;
        IReadOnlyList<ChatMessage> messages = [];
        IReadOnlyDictionary<string, object?>? outputs = null;

        try
        {
            messages = _ctx.Adapter.Format(signature, inputs, demos);
#pragma warning disable AMPELUM_V09_PREVIEW
            response = await HookedLmCall.InvokeAsync(_ctx, "DynamicPredict.Forward", messages, _ctx.DefaultChatOptions, ct).ConfigureAwait(false);
#pragma warning restore AMPELUM_V09_PREVIEW
            outputs = _ctx.Adapter.Parse(signature, response);
            stopwatch.Stop();

            var trace = new TraceRecord(
                Id: traceId,
                ModuleName: nameof(DynamicPredict),
                SignatureType: signature.SignatureType,
                Inputs: inputs,
                Outputs: outputs,
                PromptMessages: messages,
                RawResponse: response.Text!,
                Usage: response.Usage,
                Duration: stopwatch.Elapsed,
                Error: null,
                Metadata: new Dictionary<string, object?>());
            _ctx.TraceSink.Write(trace);
            return new Prediction<IReadOnlyDictionary<string, object?>>(outputs, trace);
        }
        catch (Exception ex)
        {
            if (stopwatch.IsRunning) stopwatch.Stop();
            var failureTrace = new TraceRecord(
                Id: traceId,
                ModuleName: nameof(DynamicPredict),
                SignatureType: signature.SignatureType,
                Inputs: inputs,
                Outputs: outputs ?? new Dictionary<string, object?>(),
                PromptMessages: messages,
                RawResponse: response?.Text ?? "",
                Usage: response?.Usage,
                Duration: stopwatch.Elapsed,
                Error: ex,
                Metadata: new Dictionary<string, object?>());
            _ctx.TraceSink.Write(failureTrace);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
