using System.Reflection;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Host.Contracts;

namespace SignalGrid.Ampelum.Host.Tests.Contracts;

public sealed class AssemblyMarkerTests
{
    private static Assembly ContractsAssembly =>
        typeof(BudgetHint).Assembly;

    [Fact]
    public void AssemblyMarker_IsInternalSealed()
    {
        var t = ContractsAssembly.GetTypes()
            .SingleOrDefault(x => x.Name == "AssemblyMarker");
        t.ShouldNotBeNull();
        t!.IsSealed.ShouldBeTrue();
    }

    [Fact]
    public void Assembly_HasZeroAspNetCoreReferences()
    {
        var asm = ContractsAssembly;
        foreach (var refName in asm.GetReferencedAssemblies())
            refName.Name!.StartsWith("Microsoft.AspNetCore").ShouldBeFalse(
                $"Contracts assembly must not reference any AspNetCore assembly; found {refName.Name}");
    }

    [Fact]
    public void Assembly_HasZeroAmpelumProjectReferences()
    {
        var asm = ContractsAssembly;
        foreach (var refName in asm.GetReferencedAssemblies())
        {
            refName.Name!.StartsWith("Ampelum").ShouldBeFalse(
                $"Contracts must not reference Ampelum core; found {refName.Name}");
            refName.Name!.StartsWith("SignalGrid.Ampelum.Host").ShouldBeFalse(
                $"Contracts must not reference its own host; found {refName.Name}");
        }
    }
}
