using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class ChainOfThoughtWithContextTests
{
    static AmpelumContext MakeCtx(string? submodule = null) =>
        new AmpelumContext(
            new FakeChatClient(),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
        { SubmoduleName = submodule };

    [Signature("Reason about text.")]
    sealed record CotSig : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public void WithContext_ReturnsNewInstance()
    {
        var original = new ChainOfThought<CotSig>(MakeCtx());
        var rebound  = original.WithContext(MakeCtx("cot0"));
        ReferenceEquals(original, rebound).ShouldBeFalse();
    }

    [Fact]
    public void WithContext_NewInstanceIsDecoupledFromOriginal()
    {
        var ctx1 = MakeCtx();
        var ctx2 = MakeCtx("cot0") with
        {
            DefaultDemos = new List<Example>
            {
                new(new Dictionary<string, object?> { ["Question"] = "x" },
                    new Dictionary<string, object?> { ["Answer"] = "y" })
            }
        };
        var original = new ChainOfThought<CotSig>(ctx1);
        var rebound  = original.WithContext(ctx2);
        rebound.ShouldBeOfType<ChainOfThought<CotSig>>();
    }

    [Fact]
    public void WithContext_NullContext_Throws()
    {
        var cot = new ChainOfThought<CotSig>(MakeCtx());
        Should.Throw<ArgumentNullException>(() => cot.WithContext(null!));
    }
}
