using System.Diagnostics;
using System.Diagnostics.Metrics;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Observability;

/// <summary>
/// Verifies v0.7 Task 5 (per-step perf attribution audit + gap-fill):
/// every <c>Predict.ForwardAsync</c> phase emits both an <see cref="ActivityEvent"/>
/// AND a histogram measurement.
/// </summary>
[Collection(ActivityListenerCollection.Name)]
public sealed class PerStepAttributionTests
{
    [Fact]
    public async Task PredictForward_EmitsAllFivePhasesAsEventsAndMetrics()
    {
        // The listener is process-wide, so only this test's trace may reach the list.
        using var parent = new Activity("per-step-attribution-test").Start();
        var traceId = parent.TraceId;
        var events = new List<ActivityEvent>();
        var meterRecords = new List<string>();

        using var actListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.TraceId == traceId)
                    events.AddRange(a.Events);
            },
        };
        ActivitySource.AddActivityListener(actListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "SignalGrid.Ampelum") l.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
            meterRecords.Add(instrument.Name));
        meterListener.Start();

        var lm = new FakeChatClient();
        lm.EnqueueText("[[ ## Answer ## ]]\n42");
        var ctx = new AmpelumContext(lm, new ChatAdapter(),
            SignalGrid.Ampelum.NullTraceSink.Instance,
            NullLoggerFactory.Instance)
        {
            Hooks = [new InertHook()],
        };
        var predict = new Predict<MathQa>(ctx);

        _ = await predict.ForwardAsync(
            new MathQa { Question = "2+2" },
            ct: TestContext.Current.CancellationToken);

        var eventNames = events.Select(e => e.Name).ToHashSet();
        eventNames.ShouldContain("SignalGrid.Ampelum.adapter.format");
        eventNames.ShouldContain("SignalGrid.Ampelum.lm.call");
        eventNames.ShouldContain("SignalGrid.Ampelum.adapter.parse");
        eventNames.ShouldContain("SignalGrid.Ampelum.hook.before");
        eventNames.ShouldContain("SignalGrid.Ampelum.hook.after");

        var instrumentNames = meterRecords.ToHashSet();
        instrumentNames.ShouldContain("SignalGrid.Ampelum.adapter.format.duration");
        instrumentNames.ShouldContain("SignalGrid.Ampelum.lm.call.duration");
        instrumentNames.ShouldContain("SignalGrid.Ampelum.adapter.parse.duration");
        instrumentNames.ShouldContain("SignalGrid.Ampelum.hook.before.duration");
        instrumentNames.ShouldContain("SignalGrid.Ampelum.hook.after.duration");
    }

    sealed record MathQa : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string? Answer { get; init; }
    }

    sealed class InertHook : IHook
    {
        public int Order => 0;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
