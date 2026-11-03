using System.Diagnostics;
using SignalGrid.Ampelum;

namespace Ampelum.Samples.ComposedPrograms.Drafting;

/// <summary>Adapters that let plain functions take part in Sequence, Loop and ConditionalBranch.</summary>
internal static class Step
{
    /// <summary>A stage that makes no model call of its own; its trace says so.</summary>
    public static IModule<TIn, TOut> Of<TIn, TOut>(string name, Func<TIn, CancellationToken, Task<TOut>> run) =>
        new FuncModule<TIn, TOut>(async (input, ct) =>
        {
            var clock = Stopwatch.StartNew();
            var value = await run(input, ct).ConfigureAwait(false);
            return new Prediction<TOut>(value, Trace(name, typeof(TOut), clock.Elapsed));
        });

    /// <summary>A stage whose function already returns a prediction, so its own trace is kept.</summary>
    public static IModule<TIn, TOut> OfPrediction<TIn, TOut>(Func<TIn, CancellationToken, Task<Prediction<TOut>>> run) =>
        new FuncModule<TIn, TOut>(run);

    static TraceRecord Trace(string name, Type output, TimeSpan duration) =>
        new(Guid.NewGuid(), name, output, new Dictionary<string, object?>(), new Dictionary<string, object?>(),
            [], "", null, duration, null, new Dictionary<string, object?>());

    sealed class FuncModule<TIn, TOut>(Func<TIn, CancellationToken, Task<Prediction<TOut>>> run) : IModule<TIn, TOut>
    {
        public Task<Prediction<TOut>> ForwardAsync(TIn inputs, CancellationToken ct = default) => run(inputs, ct);
    }
}
