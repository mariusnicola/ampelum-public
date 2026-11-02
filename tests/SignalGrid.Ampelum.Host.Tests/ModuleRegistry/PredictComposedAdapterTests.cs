#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.ModuleRegistry;

public sealed class PredictComposedAdapterTests
{
    private static Predict<FakeSig> MakePredict() =>
        new(new AmpelumContext(
            new FakeChatClient(),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance));

    [Fact]
    public void Submodules_ContainsDefaultKey()
    {
        var p = MakePredict();
        var adapter = new PredictComposedAdapter<FakeSig>(p);
        adapter.Submodules.Keys.ShouldBe(["default"]);
        adapter.Submodules["default"].ShouldBeSameAs(p);
    }

    [Fact]
    public void Rebind_HappyPath_ReturnsNewAdapterWithNewPredict()
    {
        var p1 = MakePredict();
        var p2 = MakePredict();
        var adapter = new PredictComposedAdapter<FakeSig>(p1);
        var rebound = adapter.Rebind(new Dictionary<string, object> { ["default"] = p2 });
        rebound.ShouldBeOfType<PredictComposedAdapter<FakeSig>>()
            .Inner.ShouldBeSameAs(p2);
    }

    [Fact]
    public void Rebind_MissingKey_Throws()
    {
        var adapter = new PredictComposedAdapter<FakeSig>(MakePredict());
        Should.Throw<InvalidOperationException>(() =>
            adapter.Rebind(new Dictionary<string, object>()));
    }

    [Fact]
    public void Rebind_WrongType_Throws()
    {
        var adapter = new PredictComposedAdapter<FakeSig>(MakePredict());
        Should.Throw<InvalidOperationException>(() =>
            adapter.Rebind(new Dictionary<string, object> { ["default"] = new object() }));
    }

    [Fact]
    public void Ctor_NullInner_Throws() =>
        Should.Throw<ArgumentNullException>(() => new PredictComposedAdapter<FakeSig>(null!));
}
