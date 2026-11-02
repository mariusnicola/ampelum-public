using BenchmarkDotNet.Attributes;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Internal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

// ──────────────────────────────────────────────────────────────────────────────
// Source-gen as the
// default path in Predict: measures the residual reflection cost the audit
// flagged — Predict<TSig>.ProjectInputs's PropertyInfo.GetValue path (default,
// no [GenerateSignatureMetadata] on the consuming assembly) vs the delegate
// accessor path (what the source generator emits in AOT-strict mode, reached
// via SignatureMetadataCache.RegisterAccessors<TSig>).
//
// ProjectInputs itself is private, so this measures it the same way every
// other benchmark in this file measures Predict internals: end-to-end through
// Predict<TSig>.ForwardAsync with a NullChatClient, holding everything else
// (adapter, tracing, record cloning) fixed and toggling only whether accessors
// are registered for the signature type before the first call. Four distinct
// signature types are used (two reflection-path, two delegate-path) because
// SignatureMetadataCache's accessor registration is a process-lifetime,
// first-writer-wins cache keyed by Type — one type cannot represent both arms.
// ──────────────────────────────────────────────────────────────────────────────

[Signature("One-field reflection-path bench.")]
public sealed record ProjInReflect1 : ISignature
{
    [Input]  public required string F1 { get; init; }
    [Output] public string O1 { get; init; } = "";
}

[Signature("One-field delegate-path bench.")]
public sealed record ProjInDelegate1 : ISignature
{
    [Input]  public required string F1 { get; init; }
    [Output] public string O1 { get; init; } = "";
}

[Signature("Five-field reflection-path bench.")]
public sealed record ProjInReflect5 : ISignature
{
    [Input]  public required string F1 { get; init; }
    [Input]  public required string F2 { get; init; }
    [Input]  public required string F3 { get; init; }
    [Input]  public required string F4 { get; init; }
    [Input]  public required string F5 { get; init; }
    [Output] public string O1 { get; init; } = "";
}

[Signature("Five-field delegate-path bench.")]
public sealed record ProjInDelegate5 : ISignature
{
    [Input]  public required string F1 { get; init; }
    [Input]  public required string F2 { get; init; }
    [Input]  public required string F3 { get; init; }
    [Input]  public required string F4 { get; init; }
    [Input]  public required string F5 { get; init; }
    [Output] public string O1 { get; init; } = "";
}

[MemoryDiagnoser]
public class ProjectInputsBenchmark
{
    Predict<ProjInReflect1> _reflect1 = null!;
    Predict<ProjInDelegate1> _delegate1 = null!;
    Predict<ProjInReflect5> _reflect5 = null!;
    Predict<ProjInDelegate5> _delegate5 = null!;

    static AmpelumContext MakeContext(ChatResponse resp) => new(
        new NullChatClient(resp),
        new ChatAdapter(),
        NullTraceSink.Instance,
        NullLoggerFactory.Instance);

    [GlobalSetup]
    public void Setup()
    {
        var resp1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## O1 ## ]]\nx"));
        var resp5 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## O1 ## ]]\nx"));

        // Delegate accessors, hand-written to match what the source generator emits in
        // AOT-strict mode (property order matches declaration order == SignatureMetadata.Inputs order).
        SignatureMetadataCache.RegisterAccessors<ProjInDelegate1>(
        [
            s => s.F1
        ]);
        SignatureMetadataCache.RegisterAccessors<ProjInDelegate5>(
        [
            s => s.F1,
            s => s.F2,
            s => s.F3,
            s => s.F4,
            s => s.F5
        ]);
        // ProjInReflect1 / ProjInReflect5 are deliberately left unregistered so
        // Predict falls through to the cached-PropertyInfo[]/GetValue path.

        _reflect1 = new Predict<ProjInReflect1>(MakeContext(resp1));
        _delegate1 = new Predict<ProjInDelegate1>(MakeContext(resp1));
        _reflect5 = new Predict<ProjInReflect5>(MakeContext(resp5));
        _delegate5 = new Predict<ProjInDelegate5>(MakeContext(resp5));

        // Prime PropertyInfo[]/SignatureMetadata caches so [Benchmark] methods measure
        // only the steady-state per-call path, matching this file's other benchmarks.
        _reflect1.ForwardAsync(new ProjInReflect1 { F1 = "a" }).GetAwaiter().GetResult();
        _delegate1.ForwardAsync(new ProjInDelegate1 { F1 = "a" }).GetAwaiter().GetResult();
        _reflect5.ForwardAsync(new ProjInReflect5 { F1 = "a", F2 = "b", F3 = "c", F4 = "d", F5 = "e" })
            .GetAwaiter().GetResult();
        _delegate5.ForwardAsync(new ProjInDelegate5 { F1 = "a", F2 = "b", F3 = "c", F4 = "d", F5 = "e" })
            .GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<ProjInReflect1>> OneField_Reflection() =>
        await _reflect1.ForwardAsync(new ProjInReflect1 { F1 = "a" });

    [Benchmark]
    public async Task<Prediction<ProjInDelegate1>> OneField_Delegate() =>
        await _delegate1.ForwardAsync(new ProjInDelegate1 { F1 = "a" });

    [Benchmark]
    public async Task<Prediction<ProjInReflect5>> FiveField_Reflection() =>
        await _reflect5.ForwardAsync(new ProjInReflect5 { F1 = "a", F2 = "b", F3 = "c", F4 = "d", F5 = "e" });

    [Benchmark]
    public async Task<Prediction<ProjInDelegate5>> FiveField_Delegate() =>
        await _delegate5.ForwardAsync(new ProjInDelegate5 { F1 = "a", F2 = "b", F3 = "c", F4 = "d", F5 = "e" });
}
