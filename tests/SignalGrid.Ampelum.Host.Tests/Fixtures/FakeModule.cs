#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Host.Contracts;

namespace SignalGrid.Ampelum.Host.Tests.Fixtures;

[Signature("Take a string in, give a string out.")]
public sealed record FakeSig : ISignature
{
    [Input] public required string Q { get; init; }
    [Output] public string A { get; init; } = "";
}

public sealed class FakeComposedModule : IComposedModule
{
    public IReadOnlyDictionary<string, object> Submodules =>
        new Dictionary<string, object> { ["default"] = new object() };

    public object Rebind(IReadOnlyDictionary<string, object> newSubmodules) => this;
}

internal static partial class TestData
{
    public static ModuleDescriptor SimpleDescriptor(string name) =>
        new(name, "FakeType", "Fake description",
            Array.Empty<FieldDescriptor>(), Array.Empty<FieldDescriptor>(), false);
}
