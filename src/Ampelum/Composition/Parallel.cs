using System.Diagnostics;
using System.Reflection;
using SignalGrid.Ampelum.Internal;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Composition;

/// <summary>
/// Executes a fixed list of <see cref="Predict{TSig}"/> modules concurrently and aggregates their outputs
/// per field: strings concatenated with <c>" | "</c>, numerics averaged, other types fall back to
/// <c>modules[0]</c>'s value.
/// </summary>
public sealed class Parallel<TSig> : IModule<TSig, TSig> where TSig : ISignature
{
    readonly AmpelumContext _ctx;
    readonly IReadOnlyList<Predict<TSig>> _modules;
    readonly SignatureMetadata _meta;

    /// <summary>
    /// Initializes a <see cref="Parallel{TSig}"/> module.
    /// </summary>
    /// <param name="context">Runtime context supplying the LM client, adapter, trace sink, and logging.</param>
    /// <param name="modules">List of modules to execute in parallel. Must not be empty.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> or <paramref name="modules"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="modules"/> is empty.</exception>
    public Parallel(AmpelumContext context, IReadOnlyList<Predict<TSig>> modules)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(modules);
        if (modules.Count == 0) throw new ArgumentException("at least one module required", nameof(modules));
        _ctx = context;
        _modules = modules;
        _meta = SignatureMetadataCache.For<TSig>();
    }

    /// <summary>
    /// Executes all modules in parallel and aggregates their outputs per field.
    /// Strings are concatenated with <c>" | "</c>, numerics are averaged,
    /// and other types fall back to the first module's output.
    /// </summary>
    /// <param name="inputs">Input signature to pass to each module.</param>
    /// <param name="demos">Optional in-context examples to pass to each module.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A prediction wrapping the aggregated output.</returns>
    public async Task<Prediction<TSig>> ForwardAsync(
        TSig inputs,
        IReadOnlyList<Example>? demos = null,
        CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.module.forward");
        activity?.SetTag("SignalGrid.Ampelum.module", "Parallel");
        activity?.SetTag("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name);

        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(_modules.Select(m => m.ForwardAsync(inputs, demos, ct))).ConfigureAwait(false);
        sw.Stop();

        var aggregated = new Dictionary<string, object?>();
        foreach (var f in _meta.Outputs)
        {
            var prop = typeof(TSig).GetProperty(f.Name, BindingFlags.Public | BindingFlags.Instance)!;
            var vals = results.Select(r => prop.GetValue(r.Value)).ToArray();
            if (f.ClrType == typeof(string))
            {
                aggregated[f.Name] = string.Join(" | ", vals.Select(v => v?.ToString() ?? ""));
            }
            else if (IsNumeric(f.ClrType))
            {
                double sum = 0; int count = 0;
                foreach (var v in vals) { if (v is not null) { sum += Convert.ToDouble(v); count++; } }
                var mean = count == 0 ? 0.0 : sum / count;
                aggregated[f.Name] = f.ClrType == typeof(int) ? (object)(int)mean
                    : f.ClrType == typeof(long) ? (long)mean
                    : f.ClrType == typeof(float) ? (float)mean
                    : f.ClrType == typeof(decimal) ? (decimal)mean
                    : mean;
            }
            else
            {
                aggregated[f.Name] = prop.GetValue(results[0].Value);
            }
        }

        var value = RecordCloner.CloneWith(inputs, aggregated);

        var trace = new TraceRecord(
            Id: Guid.NewGuid(),
            ModuleName: "Parallel",
            SignatureType: _meta.SignatureType,
            Inputs: new Dictionary<string, object?>(),
            Outputs: aggregated,
            PromptMessages: Array.Empty<ChatMessage>(),
            RawResponse: "",
            Usage: null,
            Duration: sw.Elapsed,
            Error: null,
            Metadata: new Dictionary<string, object?> { ["module_count"] = _modules.Count });
        try { _ctx.TraceSink.Write(trace); } catch { }

        return new Prediction<TSig>(value, trace);
    }

    static bool IsNumeric(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(float) || t == typeof(decimal);

    [System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
    Task<Prediction<TSig>> IModule<TSig, TSig>.ForwardAsync(TSig inputs, CancellationToken ct)
        => ForwardAsync(inputs, demos: null, ct);
}
