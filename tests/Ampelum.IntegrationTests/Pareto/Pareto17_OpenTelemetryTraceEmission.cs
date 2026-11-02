using System.Collections.Concurrent;
using System.Diagnostics;
using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #17 (master plan Â§6.H, v0.9.1-alpha.3). OpenTelemetry trace
/// emission: a single <see cref="Predict{TSig}.ForwardAsync"/> against a deterministic
/// <see cref="FakeChatClient"/> emits exactly one <see cref="Activity"/> on the
/// <c>SignalGrid.Ampelum</c> <see cref="ActivitySource"/> with the expected span name,
/// the gen_ai.* OTel semconv 1.27 attributes, and the SignalGrid.Ampelum.* span attributes
/// (module, signature_type, adapter, demos.count, trace_id) plus the expected
/// hook / adapter phase events.
/// </summary>
/// <remarks>
/// Runs unconditionally â€” no real LM, no env-var skip. Uses a process-local
/// <see cref="ActivityListener"/> rather than a real OTLP exporter so the test stays
/// offline. The product currently emits hook-phase data as <see cref="ActivityEvent"/>s
/// (<c>SignalGrid.Ampelum.hook.before</c>, <c>SignalGrid.Ampelum.hook.after</c>) rather than as span tags;
/// the user task said "SignalGrid.Ampelum.* span attributes for hook phases" but the shipped
/// surface uses events â€” assertions match what the product actually does today.
/// </remarks>
public sealed class Pareto17_OpenTelemetryTraceEmission
{
    /// <summary>One Forward â†’ one captured Activity with expected gen_ai.* + SignalGrid.Ampelum.* tags + phase events.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task Forward_EmitsAmpelumActivity_WithGenAiAndAmpelumAttributes_AndPhaseEvents()
    {
        var ct = TestContext.Current.CancellationToken;

        var captured = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var fake = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nLisbon\n[[ ## completed ## ]]");

        var ctx = new AmpelumContext(
            fake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);

        var predict = new Predict<QaSig>(ctx);
        var pred = await predict.ForwardAsync(
            new QaSig { Question = "Capital of Portugal?" },
            ct: ct);

        pred.Value.Answer.ShouldBe("Lisbon");

        // The xUnit.v3 runner executes test classes in parallel by default, so the
        // listener may capture activities from concurrent tests. Filter to the activity
        // whose trace_id tag matches our prediction's TraceId (set inside Predict.ForwardAsync).
        var act = captured.SingleOrDefault(a =>
            a.GetTagItem("SignalGrid.Ampelum.trace_id") is string s
            && s == pred.Trace.Id.ToString())
            ?? throw new Xunit.Sdk.XunitException(
                $"No SignalGrid.Ampelum activity captured for trace_id {pred.Trace.Id}; saw {captured.Count} activities total.");

        act.OperationName.ShouldBe("SignalGrid.Ampelum.module.forward");
        act.Source.Name.ShouldBe("SignalGrid.Ampelum");

        var tags = act.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        // SignalGrid.Ampelum.* span attributes (per Diagnostics.AttributeAllowlist).
        tags.ShouldContainKey("SignalGrid.Ampelum.module");
        tags["SignalGrid.Ampelum.module"].ShouldBe("Predict");
        tags.ShouldContainKey("SignalGrid.Ampelum.signature_type");
        tags["SignalGrid.Ampelum.signature_type"].ShouldBe(nameof(QaSig));
        tags.ShouldContainKey("SignalGrid.Ampelum.adapter");
        tags["SignalGrid.Ampelum.adapter"].ShouldBe(nameof(ChatAdapter));
        tags.ShouldContainKey("SignalGrid.Ampelum.demos.count");
        tags["SignalGrid.Ampelum.demos.count"].ShouldBe(0);
        tags.ShouldContainKey("SignalGrid.Ampelum.trace_id");
        tags["SignalGrid.Ampelum.trace_id"]!.ToString().ShouldNotBeNullOrEmpty();

        // gen_ai.* OTel semconv 1.27 attributes.
        tags.ShouldContainKey("gen_ai.system");
        tags.ShouldContainKey("gen_ai.operation.name");
        tags["gen_ai.operation.name"].ShouldBe("chat");
        tags.ShouldContainKey("gen_ai.semconv.version");
        tags["gen_ai.semconv.version"]!.ToString().ShouldStartWith("1.");

        // Phase events: adapter format + parse fire on every successful Forward.
        var eventNames = act.Events.Select(e => e.Name).ToArray();
        eventNames.ShouldContain("SignalGrid.Ampelum.adapter.format");
        eventNames.ShouldContain("SignalGrid.Ampelum.adapter.parse");
    }

    /// <summary>With a hook attached, the activity additionally emits <c>SignalGrid.Ampelum.hook.before</c> and <c>SignalGrid.Ampelum.hook.after</c> events.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task Forward_WithHook_EmitsHookBeforeAndAfterEvents()
    {
        var ct = TestContext.Current.CancellationToken;

        var captured = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var fake = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nMadrid\n[[ ## completed ## ]]");

        var sink = new SignalGrid.Ampelum.Audit.InMemoryAuditSink();
        var ctx = new AmpelumContext(
            fake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
            { Hooks = new SignalGrid.Ampelum.Hooks.IHook[] { new SignalGrid.Ampelum.Audit.AuditHook(sink) } };

        var predict = new Predict<QaSig>(ctx);
        var pred = await predict.ForwardAsync(new QaSig { Question = "Capital of Spain?" }, ct: ct);

        var act = captured.SingleOrDefault(a =>
            a.GetTagItem("SignalGrid.Ampelum.trace_id") is string s
            && s == pred.Trace.Id.ToString())
            ?? throw new Xunit.Sdk.XunitException(
                $"No SignalGrid.Ampelum activity captured for trace_id {pred.Trace.Id}; saw {captured.Count} activities total.");
        var eventNames = act.Events.Select(e => e.Name).ToArray();
        eventNames.ShouldContain("SignalGrid.Ampelum.hook.before");
        eventNames.ShouldContain("SignalGrid.Ampelum.hook.after");

        // v1.0-rc1.alpha.5 (Gap 4 / G2 spec): hook-phase data is also published as
        // SignalGrid.Ampelum.hook.<phase>.duration_us span tags so OTel exporters can graph
        // hook latency without parsing event timestamps.
        var phaseTags = act.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        phaseTags.ShouldContainKey("SignalGrid.Ampelum.hook.before.duration_us");
        phaseTags.ShouldContainKey("SignalGrid.Ampelum.hook.after.duration_us");
        phaseTags["SignalGrid.Ampelum.hook.before.duration_us"].ShouldBeOfType<long>();
        phaseTags["SignalGrid.Ampelum.hook.after.duration_us"].ShouldBeOfType<long>();
    }
}
