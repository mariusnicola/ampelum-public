using SignalGrid.Ampelum.Audit;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

/// <summary>
/// Tests for <see cref="AuditChainHasher.ComputeReplay"/> — alpha.10 (Gap 1).
/// </summary>
public sealed class AuditChainHasherReplayTests
{
    static IReadOnlyDictionary<string, object?> Inputs(string q) =>
        new Dictionary<string, object?> { ["Question"] = q };

    [Fact]
    public void ComputeReplay_IsDeterministic_OverSameInputs()
    {
        var a = AuditChainHasher.ComputeReplay("Module", "forward.completed", "model-x", Inputs("hi"));
        var b = AuditChainHasher.ComputeReplay("Module", "forward.completed", "model-x", Inputs("hi"));
        a.ShouldBe(b);
    }

    [Fact]
    public void ComputeReplay_ReturnsLowercaseHexString_Of32Bytes()
    {
        var hash = AuditChainHasher.ComputeReplay("M", "e", "m", Inputs("x"));
        hash.Length.ShouldBe(64); // 32 bytes * 2 hex chars
        hash.ShouldAllBe(c => char.IsDigit(c) || (c >= 'a' && c <= 'f'));
    }

    [Fact]
    public void ComputeReplay_DiffersWhenModuleChanges()
    {
        var a = AuditChainHasher.ComputeReplay("ModuleA", "forward.completed", "m", Inputs("hi"));
        var b = AuditChainHasher.ComputeReplay("ModuleB", "forward.completed", "m", Inputs("hi"));
        a.ShouldNotBe(b);
    }

    [Fact]
    public void ComputeReplay_DiffersWhenEventTypeChanges()
    {
        var a = AuditChainHasher.ComputeReplay("M", "forward.completed", "m", Inputs("hi"));
        var b = AuditChainHasher.ComputeReplay("M", "agent.handoff", "m", Inputs("hi"));
        a.ShouldNotBe(b);
    }

    [Fact]
    public void ComputeReplay_DiffersWhenModelIdChanges()
    {
        var a = AuditChainHasher.ComputeReplay("M", "e", "model-a", Inputs("hi"));
        var b = AuditChainHasher.ComputeReplay("M", "e", "model-b", Inputs("hi"));
        a.ShouldNotBe(b);
    }

    [Fact]
    public void ComputeReplay_DiffersWhenInputsChange()
    {
        var a = AuditChainHasher.ComputeReplay("M", "e", "m", Inputs("hello"));
        var b = AuditChainHasher.ComputeReplay("M", "e", "m", Inputs("world"));
        a.ShouldNotBe(b);
    }

    [Fact]
    public void ComputeReplay_IsAgnosticToInputDictionaryOrdering()
    {
        var d1 = new Dictionary<string, object?> { ["A"] = 1L, ["B"] = "x" };
        var d2 = new Dictionary<string, object?> { ["B"] = "x", ["A"] = 1L };
        AuditChainHasher.ComputeReplay("M", "e", "m", d1)
            .ShouldBe(AuditChainHasher.ComputeReplay("M", "e", "m", d2));
    }
}
