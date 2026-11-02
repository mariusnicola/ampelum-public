using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum;

/// <summary>
/// A Ampelum module that augments a standard <see cref="Predict{TSig}"/> call with a prepended
/// <c>reasoning</c> output field, prompting the LM to think step-by-step before producing its final answer.
/// The reasoning text is available via <see cref="PredictionExtensions.Reasoning{T}"/> on the returned prediction.
/// </summary>
/// <typeparam name="TSig">The signature type describing the task's inputs and outputs.</typeparam>
public sealed class ChainOfThought<TSig> where TSig : ISignature
{
    readonly Predict<TSig> _inner;

    /// <summary>Exposes the augmenting adapter for testing (e.g., to call FormatResponse directly).
    /// Internal visibility matches the InternalsVisibleTo grants in Ampelum.csproj.</summary>
    internal IAdapter AugmentedAdapter { get; }

    /// <summary>
    /// Initializes a <see cref="ChainOfThought{TSig}"/> using the provided context.
    /// The context's adapter is wrapped with a reasoning-injecting adapter automatically.
    /// </summary>
    /// <param name="context">Runtime context supplying the LM client, adapter, trace sink, and logging.</param>
    public ChainOfThought(AmpelumContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Choose the wrapper based on inner adapter capability so that
        // Predict.BuildOptions only invokes IStructuredOutputAdapter when the inner
        // adapter actually supports it.
        AugmentedAdapter = context.Adapter is IStructuredOutputAdapter
            ? new StructuredAugmentingAdapter(context.Adapter)
            : new AugmentingAdapter(context.Adapter);

        _inner = new Predict<TSig>(context with { Adapter = AugmentedAdapter });
    }

    /// <summary>
    /// Runs the chain-of-thought forward pass: sends the inputs to the LM with an injected
    /// <c>reasoning</c> field and returns both the typed outputs and the full trace.
    /// </summary>
    /// <param name="inputs">Populated signature record with all required input fields set.</param>
    /// <param name="demos">Optional few-shot demonstrations; reasoning output is automatically injected.</param>
    /// <param name="ct">Cancellation token; cancellation rethrows <see cref="OperationCanceledException"/> unwrapped.</param>
    /// <returns>A <see cref="Prediction{TSig}"/> containing the typed output and trace.</returns>
    public Task<Prediction<TSig>> ForwardAsync(
        TSig inputs,
        IReadOnlyList<Example>? demos = null,
        CancellationToken ct = default) =>
        _inner.ForwardAsync(inputs, InjectReasoningDemos(demos), ct);

    /// <summary>
    /// Returns a new <see cref="ChainOfThought{TSig}"/> bound to the supplied context.
    /// Used by optimizers and submodule binding to rebind a module to a child context.
    /// </summary>
    public ChainOfThought<TSig> WithContext(AmpelumContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ChainOfThought<TSig>(context);
    }

    static IReadOnlyList<Example>? InjectReasoningDemos(IReadOnlyList<Example>? demos)
    {
        if (demos is null || demos.Count == 0) return demos;
        var augmented = new List<Example>(demos.Count);
        foreach (var d in demos)
        {
            var outs = new Dictionary<string, object?>(d.Outputs) { [TraceKeys.Reasoning] = "" };
            augmented.Add(d with { Outputs = outs });
        }
        return augmented;
    }

    // Base wrapper: handles Format, Parse, and FormatResponse (ITestableAdapter).
    class AugmentingAdapter(IAdapter inner) : IAdapter, ITestableAdapter
    {
        protected readonly IAdapter Inner = inner;

        public IReadOnlyList<ChatMessage> Format(SignatureMetadata signature,
            IReadOnlyDictionary<string, object?> inputs,
            IReadOnlyList<Example> demos) =>
            Inner.Format(Augment(signature), inputs, demos);

        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata signature, ChatResponse response) =>
            Inner.Parse(Augment(signature), response);

        public ChatResponse FormatResponse(SignatureMetadata signature, IReadOnlyDictionary<string, object?> outputs) =>
            Inner is ITestableAdapter t
                ? t.FormatResponse(Augment(signature), outputs)
                : throw new InvalidOperationException(
                    $"Inner adapter '{Inner.GetType().Name}' does not implement ITestableAdapter.");

        protected static SignatureMetadata Augment(SignatureMetadata s)
        {
            var reasoning = new Field(
                TraceKeys.Reasoning,
                typeof(string),
                "Step-by-step reasoning before the answer.",
                FieldKind.Output,
                IsOptional: false);
            var outs = new List<Field>(s.Outputs.Count + 1) { reasoning };
            outs.AddRange(s.Outputs);
            return s with { Outputs = outs };
        }
    }

    // Extended wrapper for adapters that also support IStructuredOutputAdapter.
    sealed class StructuredAugmentingAdapter(IAdapter inner)
        : AugmentingAdapter(inner), IStructuredOutputAdapter
    {
        public ChatResponseFormat BuildResponseFormat(SignatureMetadata signature) =>
            ((IStructuredOutputAdapter)Inner).BuildResponseFormat(Augment(signature));
    }
}
