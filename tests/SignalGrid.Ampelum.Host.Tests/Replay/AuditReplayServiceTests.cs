#pragma warning disable AMPELUM_V09_PREVIEW
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Host.Replay;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Replay;

public sealed class AuditReplayServiceTests
{
    sealed class FixedLookup(AuditEntry entry) : IAuditEntryLookup
    {
        public ValueTask<AuditEntry?> TryGetByTraceIdAsync(string traceId, CancellationToken ct) => new(entry);
        public ValueTask<AuditEntry?> TryGetBySequenceAsync(long sequence, CancellationToken ct) => new(entry);
    }

    /// <summary>One real Predict call, audited the way the Host audits it: module name "Predict".</summary>
    static async Task<(AuditEntry Entry, AmpelumContext Ctx)> AuditedCallAsync()
    {
        var sink = new InMemoryAuditSink();
        var ctx = new AmpelumContext(
            new FakeChatClient().EnqueueText("[[ ## A ## ]]\nanswer\n[[ ## completed ## ]]"),
            new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [new AuditHook(sink)],
        };
        await new Predict<FakeSig>(ctx).ForwardAsync(
            new FakeSig { Q = "q" }, null, TestContext.Current.CancellationToken);
        AuditEntry? entry = null;
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entry = e;
        return (entry!, ctx);
    }

    static ModuleSlot Slot(string name, AmpelumContext ctx) =>
        new(name, new PredictComposedAdapter<FakeSig>(new Predict<FakeSig>(ctx)), TestData.SimpleDescriptor(name));

    static AuditReplayService Service(AuditEntry entry, AmpelumContext ctx, params string[] servedNames)
    {
        var registry = new SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry();
        foreach (var n in servedNames) registry.Register(Slot(n, ctx));
        return new AuditReplayService(new FixedLookup(entry), registry, ctx, NullLogger<AuditReplayService>.Instance);
    }

    [Signature("Wider signature: an optional second input.")]
    public sealed record WideSig : ISignature
    {
        [Input] public required string Q { get; init; }
        [Input] public string? Context { get; init; }
        [Output] public string B { get; init; } = "";
    }

    static AuditReplayService NarrowAndWide(AuditEntry entry, AmpelumContext ctx)
    {
        var registry = new SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry();
        registry.Register(new ModuleSlot("Wide",
            new PredictComposedAdapter<WideSig>(new Predict<WideSig>(ctx)), TestData.SimpleDescriptor("Wide")));
        registry.Register(Slot("Narrow", ctx));
        return new AuditReplayService(new FixedLookup(entry), registry, ctx, NullLogger<AuditReplayService>.Instance);
    }

    [Fact]
    public async Task Replay_refuses_an_entry_with_an_input_the_signature_does_not_have()
    {
        var (entry, ctx) = await AuditedCallAsync();
        var withExtra = entry with { Inputs = new Dictionary<string, object?> { ["Q"] = "q", ["Unknown"] = "x" } };

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            Service(withExtra, ctx, "Answer").ReplayAsync("t", TestContext.Current.CancellationToken).AsTask());

        ex.Message.ShouldStartWith("module_not_found:");
    }

    [Fact]
    public async Task Replay_prefers_the_module_whose_inputs_match_the_recorded_names_exactly()
    {
        var (entry, ctx) = await AuditedCallAsync();

        var response = await NarrowAndWide(entry, ctx).ReplayAsync("t", TestContext.Current.CancellationToken);

        // Both accept {Q}; the narrow one has exactly those inputs, and its output field is A.
        response.Outputs.ContainsKey("A").ShouldBeTrue();
        response.Outputs.ContainsKey("B").ShouldBeFalse();
    }

    [Fact]
    public async Task Replay_uses_the_wider_module_when_only_it_accepts_the_recorded_inputs()
    {
        var (entry, ctx) = await AuditedCallAsync();
        var wide = entry with { Inputs = new Dictionary<string, object?> { ["Q"] = "q", ["Context"] = "c" } };

        var response = await NarrowAndWide(wide, ctx).ReplayAsync("t", TestContext.Current.CancellationToken);

        response.Outputs.ContainsKey("B").ShouldBeTrue();
    }

    [Fact]
    public async Task Replay_finds_a_module_served_under_another_name()
    {
        var (entry, ctx) = await AuditedCallAsync();
        entry.ModuleName.ShouldBe("Predict");

        var response = await Service(entry, ctx, "Answer").ReplayAsync("t", TestContext.Current.CancellationToken);

        response.ModuleName.ShouldBe("Predict");
        response.TraceId.ShouldBe("t");
    }

    [Fact]
    public async Task Replay_prefers_the_module_registered_under_the_audited_name()
    {
        var (entry, ctx) = await AuditedCallAsync();

        var response = await Service(entry, ctx, "Answer", "Predict", "Other")
            .ReplayAsync("t", TestContext.Current.CancellationToken);

        response.ShouldNotBeNull();
    }

    [Fact]
    public async Task Replay_with_two_candidate_modules_and_no_exact_name_is_refused_as_ambiguous()
    {
        var (entry, ctx) = await AuditedCallAsync();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            Service(entry, ctx, "A1", "A2").ReplayAsync("t", TestContext.Current.CancellationToken).AsTask());

        ex.Message.ShouldStartWith("module_not_found:");
        ex.Message.ShouldContain("A1");
        ex.Message.ShouldContain("A2");
    }

    [Fact]
    public async Task Replay_with_no_registered_module_is_module_not_found()
    {
        var (entry, ctx) = await AuditedCallAsync();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            Service(entry, ctx).ReplayAsync("t", TestContext.Current.CancellationToken).AsTask());

        ex.Message.ShouldBe("module_not_found:Predict");
    }
}
