namespace Ampelum.Tests.Orchestration;

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Orchestration;
using Ampelum.Tests.Composition._Sigs;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

/// <summary>A mutable signature for testing config injection (WithConfig requires Action{T}).</summary>
public sealed record MutableSig : ISignature
{
    public string Label { get; set; } = "";
    public int Value { get; set; }
}

public sealed class WithConfigTests
{
    static TraceRecord Dummy(string modName) => new(
        Id: Guid.NewGuid(),
        ModuleName: modName,
        SignatureType: typeof(object),
        Inputs: new Dictionary<string, object?>(),
        Outputs: new Dictionary<string, object?>(),
        PromptMessages: [],
        RawResponse: "",
        Usage: null,
        Duration: TimeSpan.Zero,
        Error: null,
        Metadata: new Dictionary<string, object?>());

    sealed class EchoMut : IModule<MutableSig, MutableSig>
    {
        public Task<Prediction<MutableSig>> ForwardAsync(MutableSig inputs, CancellationToken ct) =>
            Task.FromResult(new Prediction<MutableSig>(
                inputs with { Value = inputs.Label.Length },
                Dummy("echo")));
    }

    sealed class TrackedMut : IModule<MutableSig, MutableSig>
    {
        public MutableSig? ReceivedInput;
        public Task<Prediction<MutableSig>> ForwardAsync(MutableSig inputs, CancellationToken ct)
        {
            ReceivedInput = inputs with { };
            return Task.FromResult(new Prediction<MutableSig>(
                inputs with { Value = 99 },
                Dummy("tracked")));
        }
    }

    [Fact]
    public async Task ConfigApplied_BeforeInnerExecution()
    {
        var tracked = new TrackedMut();
        var withConfig = new WithConfig<MutableSig>(tracked, sig =>
        {
            sig.Label = "configured";
        });

        await withConfig.ForwardAsync(new MutableSig { Label = "original" }, TestContext.Current.CancellationToken);

        tracked.ReceivedInput.ShouldNotBeNull();
        tracked.ReceivedInput.Label.ShouldBe("configured");
    }

    [Fact]
    public async Task InnerModule_SeesConfiguredState()
    {
        var withConfig = new WithConfig<MutableSig>(new EchoMut(), sig =>
        {
            sig.Label = "hello";
        });

        var result = await withConfig.ForwardAsync(new MutableSig { Label = "ignored" }, TestContext.Current.CancellationToken);

        result.Value.Label.ShouldBe("hello");
        result.Value.Value.ShouldBe(5); // "hello".Length
    }

    [Fact]
    public async Task ConfigApplierError_Propagates()
    {
        var withConfig = new WithConfig<MutableSig>(new EchoMut(), sig =>
        {
            throw new InvalidOperationException("config_fail");
        });

        await Should.ThrowAsync<InvalidOperationException>(() =>
            withConfig.ForwardAsync(new MutableSig { Label = "x" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_NullInner_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new WithConfig<MutableSig>(null!, _ => { }));
    }

    [Fact]
    public void Constructor_NullConfigApplier_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new WithConfig<MutableSig>(new EchoMut(), null!));
    }

    [Fact]
    public async Task CompositionWrapping_WithConfigWrapsInner()
    {
        var withConfig = new WithConfig<MutableSig>(new EchoMut(), sig =>
        {
            sig.Label = "wrapped";
        });

        var result = await withConfig.ForwardAsync(new MutableSig { Label = "x" }, TestContext.Current.CancellationToken);

        result.Value.Label.ShouldBe("wrapped");
        result.Value.Value.ShouldBe(7); // "wrapped".Length
    }

    [Fact]
    public async Task Trace_ContainsWithConfigMetadata()
    {
        var withConfig = new WithConfig<MutableSig>(new EchoMut(), _ => { });

        var result = await withConfig.ForwardAsync(new MutableSig { Label = "x" }, TestContext.Current.CancellationToken);

        result.Trace.Metadata.ShouldContainKey(SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive);
        result.Trace.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive].ShouldBe("WithConfig");
        result.Trace.ModuleName.ShouldBe("WithConfig<MutableSig>");
    }

    [Fact]
    public async Task MultipleConfigs_BothApplyInOrder()
    {
        var trackedA = new TrackedMut();
        var outer = new WithConfig<MutableSig>(
            new WithConfig<MutableSig>(trackedA, sig => { sig.Label += "_inner"; }),
            sig => { sig.Label += "_outer"; });

        await outer.ForwardAsync(new MutableSig { Label = "base" }, TestContext.Current.CancellationToken);

        trackedA.ReceivedInput.ShouldNotBeNull();
        trackedA.ReceivedInput.Label.ShouldBe("base_outer_inner");
    }

    [Fact]
    public async Task ConfigMutatesLabel_ThenInnerTransformsValue()
    {
        var withConfig = new WithConfig<MutableSig>(new EchoMut(), sig =>
        {
            sig.Label = "mutated";
        });

        var result = await withConfig.ForwardAsync(new MutableSig { Label = "original" }, TestContext.Current.CancellationToken);

        result.Value.Label.ShouldBe("mutated");
        result.Value.Value.ShouldBe(7); // "mutated".Length
    }
}
