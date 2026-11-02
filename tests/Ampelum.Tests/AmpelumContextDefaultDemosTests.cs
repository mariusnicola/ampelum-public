using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class AmpelumContextDefaultDemosTests
{
    static AmpelumContext MakeBase() => new(
        new FakeChatClient(),
        new ChatAdapter(),
        NullTraceSink.Instance,
        NullLoggerFactory.Instance);

    static Example MakeDemo(string key, string val) =>
        new(new Dictionary<string, object?> { [key] = val },
            new Dictionary<string, object?> { ["out"] = "v" });

    [Fact]
    public void DefaultDemos_DefaultsToNull()
    {
        var ctx = MakeBase();
        ctx.DefaultDemos.ShouldBeNull();
    }

    [Fact]
    public void DefaultDemos_CanBeSetViaInitProperty()
    {
        var demos = new List<Example> { MakeDemo("q", "hi") };
        var ctx = MakeBase() with { DefaultDemos = demos };
        ctx.DefaultDemos.ShouldBeSameAs(demos);
    }

    [Fact]
    public void DefaultDemos_WithExpression_PreservesOtherFields()
    {
        var base_ = MakeBase();
        var demos = new List<Example> { MakeDemo("q", "hi") };
        var ctx = base_ with { DefaultDemos = demos };
        ctx.Chat.ShouldBeSameAs(base_.Chat);
        ctx.Adapter.ShouldBeSameAs(base_.Adapter);
        ctx.TraceSink.ShouldBeSameAs(base_.TraceSink);
        ctx.LoggerFactory.ShouldBeSameAs(base_.LoggerFactory);
    }

    [Fact]
    public void DefaultDemos_IsIReadOnlyList()
    {
        var demos = new List<Example> { MakeDemo("q", "hi") };
        var ctx = MakeBase() with { DefaultDemos = demos };
        ctx.DefaultDemos.ShouldBeAssignableTo<IReadOnlyList<Example>>();
    }

    [Fact]
    public void Ctor_FourArgs_Unaffected_NonBreaking()
    {
        var ctx = new AmpelumContext(
            new FakeChatClient(),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        ctx.DefaultDemos.ShouldBeNull();
    }
}
