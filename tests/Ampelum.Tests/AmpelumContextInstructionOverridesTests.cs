using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class AmpelumContextInstructionOverridesTests
{
    static AmpelumContext MakeBase() => new(
        new FakeChatClient(),
        new ChatAdapter(),
        NullTraceSink.Instance,
        NullLoggerFactory.Instance);

    [Fact]
    public void InstructionOverrides_DefaultsToNull()
    {
        var ctx = MakeBase();
        ctx.InstructionOverrides.ShouldBeNull();
    }

    [Fact]
    public void InstructionOverrides_CanBeSetViaInitProperty()
    {
        var overrides = new Dictionary<string, string> { ["predict0"] = "custom instr" };
        var ctx = MakeBase() with { InstructionOverrides = overrides };
        ctx.InstructionOverrides.ShouldBeSameAs(overrides);
    }

    [Fact]
    public void InstructionOverrides_CanBeComposedWithSubmoduleName()
    {
        var overrides = new Dictionary<string, string> { ["predict0"] = "custom instr" };
        var ctx = MakeBase() with
        {
            InstructionOverrides = overrides,
            SubmoduleName = "predict0"
        };
        ctx.SubmoduleName.ShouldBe("predict0");
        ctx.InstructionOverrides!["predict0"].ShouldBe("custom instr");
    }

    [Fact]
    public void InstructionOverrides_IsIReadOnlyDictionary()
    {
        var overrides = new Dictionary<string, string> { ["a"] = "b" };
        var ctx = MakeBase() with { InstructionOverrides = overrides };
        ctx.InstructionOverrides.ShouldBeAssignableTo<IReadOnlyDictionary<string, string>>();
    }

    [Fact]
    public void InstructionOverrides_WithExpression_PreservesOtherFields()
    {
        var base_ = MakeBase();
        var ctx = base_ with { InstructionOverrides = new Dictionary<string, string>() };
        ctx.Chat.ShouldBeSameAs(base_.Chat);
        ctx.Adapter.ShouldBeSameAs(base_.Adapter);
        ctx.DefaultDemos.ShouldBeNull();
    }

    [Fact]
    public void Ctor_FourArgs_Unaffected_NonBreaking()
    {
        var ctx = new AmpelumContext(
            new FakeChatClient(),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        ctx.InstructionOverrides.ShouldBeNull();
    }
}
