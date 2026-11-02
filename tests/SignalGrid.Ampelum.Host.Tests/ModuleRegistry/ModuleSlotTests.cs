#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.ModuleRegistry;

public sealed class ModuleSlotTests
{
    [Fact]
    public void Current_ReturnsInitial()
    {
        var initial = new FakeComposedModule();
        var desc = TestData.SimpleDescriptor("M");
        var slot = new ModuleSlot("M", initial, desc);
        slot.Current.ShouldBeSameAs(initial);
    }

    [Fact]
    public void SwapTo_AtomicallyReplaces_AndReturnsPrevious()
    {
        var a = new FakeComposedModule();
        var b = new FakeComposedModule();
        var slot = new ModuleSlot("M", a, TestData.SimpleDescriptor("M"));
        slot.SwapTo(b).ShouldBeSameAs(a);
        slot.Current.ShouldBeSameAs(b);
    }

    [Fact]
    public async Task SwapTo_IsLinearizable_UnderConcurrentReaders()
    {
        var initial = new FakeComposedModule();
        var slot = new ModuleSlot("M", initial, TestData.SimpleDescriptor("M"));
        var swappers = Enumerable.Range(0, 100).Select(_ => new FakeComposedModule()).ToArray();
        var ct = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource();
        var readerTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
                slot.Current.ShouldNotBeNull();
        }, cts.Token);
        for (var i = 0; i < swappers.Length; i++) slot.SwapTo(swappers[i]);
        await Task.Delay(50, ct);
        cts.Cancel();
        await readerTask.ContinueWith(_ => { }, TaskScheduler.Default);
        slot.Current.ShouldBeSameAs(swappers[^1]);
    }

    [Fact] public void Ctor_NullInitial_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new ModuleSlot("M", null!, TestData.SimpleDescriptor("M")));

    [Fact] public void Ctor_EmptyName_Throws() =>
        Should.Throw<ArgumentException>(() =>
            new ModuleSlot("", new FakeComposedModule(), TestData.SimpleDescriptor("M")));

    [Fact] public void SwapTo_NullNext_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new ModuleSlot("M", new FakeComposedModule(), TestData.SimpleDescriptor("M")).SwapTo(null!));

    [Fact] public void SupportsStreaming_ReflectsDescriptor()
    {
        var desc = new SignalGrid.Ampelum.Host.Contracts.ModuleDescriptor(
            "M", "T", "D", Array.Empty<SignalGrid.Ampelum.Host.Contracts.FieldDescriptor>(),
            Array.Empty<SignalGrid.Ampelum.Host.Contracts.FieldDescriptor>(), true);
        var slot = new ModuleSlot("M", new FakeComposedModule(), desc);
        slot.SupportsStreaming.ShouldBeTrue();
    }
}
