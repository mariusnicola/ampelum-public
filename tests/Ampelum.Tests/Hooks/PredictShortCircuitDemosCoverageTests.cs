using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Hooks;

/// <summary>
/// Closes coverage on the short-circuit-with-demos branch in Predict.ForwardAsync where
/// <c>demos.Count &gt; 0</c> writes <see cref="TraceKeys.DemosUsed"/> to the trace metadata.
/// </summary>
public sealed class PredictShortCircuitDemosCoverageTests
{
    [Signature("Q&A")]
    public sealed record Sig : ISignature
    {
        [Input] public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
    }

    sealed class WriterHook : IHook
    {
        public int Order => 0;
        public ValueTask OnBeforeAsync(HookContext c, CancellationToken ct)
        {
            c.Outputs = new Dictionary<string, object?> { ["A"] = "cached" };
            return default;
        }
        public ValueTask OnAfterAsync(HookContext c, CancellationToken ct) => default;
        public ValueTask OnErrorAsync(HookContext c, Exception e, CancellationToken ct) => default;
    }

    [Fact]
    public async Task ShortCircuit_WithDemos_RecordsDemosUsedInTrace()
    {
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance)
        { Hooks = new IHook[] { new WriterHook() } };
        var predict = new Predict<Sig>(ctx);

        var demos = new[]
        {
            new Example(new Dictionary<string, object?> { ["Q"] = "x" },
                        new Dictionary<string, object?> { ["A"] = "y" }),
        };

        var result = await predict.ForwardAsync(
            new Sig { Q = "hi" },
            demos: demos,
            ct: TestContext.Current.CancellationToken);

        result.Trace.Metadata.ShouldContainKey(TraceKeys.DemosUsed);
        result.Trace.Metadata[TraceKeys.HookShortCircuited].ShouldBe(true);
    }
}
