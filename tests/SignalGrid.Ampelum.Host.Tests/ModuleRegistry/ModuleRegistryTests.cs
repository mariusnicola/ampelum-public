#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.ModuleRegistry;

public sealed class ModuleRegistryTests
{
    [Fact]
    public void Register_BeforeFreeze_Succeeds()
    {
        var r = new global::SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry();
        var slot = new ModuleSlot("A", new FakeComposedModule(), TestData.SimpleDescriptor("A"));
        r.Register(slot);
        r.Names.ShouldBe(new[] { "A" });
        r.Get("A").ShouldBeSameAs(slot);
    }

    [Fact]
    public void Register_DuplicateName_Throws()
    {
        var r = new global::SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry();
        r.Register(new ModuleSlot("A", new FakeComposedModule(), TestData.SimpleDescriptor("A")));
        Should.Throw<InvalidOperationException>(() =>
            r.Register(new ModuleSlot("A", new FakeComposedModule(), TestData.SimpleDescriptor("A"))));
    }

    [Fact]
    public void Register_AfterFreeze_Throws()
    {
        var r = new global::SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry();
        r.Freeze();
        Should.Throw<InvalidOperationException>(() =>
            r.Register(new ModuleSlot("A", new FakeComposedModule(), TestData.SimpleDescriptor("A"))));
    }

    [Fact]
    public void Freeze_IsIdempotent()
    {
        var r = new global::SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry();
        r.Freeze(); r.Freeze();
        r.IsFrozen.ShouldBeTrue();
    }

    [Fact]
    public void TryGet_Missing_ReturnsFalse()
    {
        var r = new global::SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry();
        r.TryGet("X", out var slot).ShouldBeFalse();
        slot.ShouldBeNull();
    }

    [Fact]
    public void Get_Missing_Throws() =>
        Should.Throw<KeyNotFoundException>(() => new global::SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry().Get("X"));

    [Fact]
    public void Names_ReflectsInsertionOrder()
    {
        var r = new global::SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry();
        r.Register(new ModuleSlot("B", new FakeComposedModule(), TestData.SimpleDescriptor("B")));
        r.Register(new ModuleSlot("A", new FakeComposedModule(), TestData.SimpleDescriptor("A")));
        r.Names.ShouldBe(new[] { "B", "A" });
    }

    [Fact]
    public void Count_ReflectsRegistrations()
    {
        var r = new global::SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry();
        r.Count.ShouldBe(0);
        r.Register(new ModuleSlot("A", new FakeComposedModule(), TestData.SimpleDescriptor("A")));
        r.Count.ShouldBe(1);
    }
}
