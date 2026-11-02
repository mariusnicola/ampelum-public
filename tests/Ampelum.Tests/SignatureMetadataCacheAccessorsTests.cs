using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests;

public sealed class SignatureMetadataCacheAccessorsTests
{
    [Signature("acc-test")]
    public sealed record AccSig : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Input]  public required int    N { get; init; }
        [Output] public string Out { get; init; } = "";
    }

    [Signature("acc-test-2")]
    public sealed record AccSig2 : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Input]  public required int    N { get; init; }
        [Output] public string Out { get; init; } = "";
    }

    [Signature("acc-test-3")]
    public sealed record AccSig3 : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public string Out { get; init; } = "";
    }

    [Fact]
    public void RegisterAccessors_NullArray_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            SignatureMetadataCache.RegisterAccessors<AccSig3>(null!));
    }

    [Fact]
    public void RegisterAccessors_ThenTryGetAccessors_ReturnsRegistered()
    {
        var getters = new Func<AccSig, object?>[]
        {
            static s => s.Q,
            static s => s.N
        };
        SignatureMetadataCache.RegisterAccessors<AccSig>(getters);

        SignatureMetadataCache.TryGetAccessors(typeof(AccSig), out var stored).ShouldBeTrue();
        stored.ShouldNotBeNull();
        stored.Length.ShouldBe(2);

        var sig = new AccSig { Q = "hello", N = 42 };
        ((Func<AccSig, object?>)stored[0])(sig).ShouldBe("hello");
        ((Func<AccSig, object?>)stored[1])(sig).ShouldBe(42);
    }

    [Fact]
    public void TryGetAccessors_Unregistered_ReturnsFalse()
    {
        SignatureMetadataCache.TryGetAccessors(typeof(int), out var stored).ShouldBeFalse();
        stored.ShouldBeNull();
    }

    [Signature("acc-fastpath")]
    public sealed record AccFastPathSig : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public async Task Predict_UsesAccessorFastPath_WhenRegistered()
    {
        // Register accessors so Predict.ProjectInputs takes the delegate fast path.
        SignatureMetadataCache.RegisterAccessors<AccFastPathSig>(new Func<AccFastPathSig, object?>[]
        {
            static s => s.Question,
        });

        var fake = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nParis");
        var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var predict = new Predict<AccFastPathSig>(ctx);

        var result = await predict.ForwardAsync(
            new AccFastPathSig { Question = "Capital of France?" },
            ct: TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("Paris");
    }

    [Fact]
    public void RegisterAccessors_FirstWriterWins()
    {
        var first  = new Func<AccSig2, object?>[] { static s => "first", static s => 1 };
        var second = new Func<AccSig2, object?>[] { static s => "second", static s => 2 };

        SignatureMetadataCache.RegisterAccessors<AccSig2>(first);
        SignatureMetadataCache.RegisterAccessors<AccSig2>(second);

        SignatureMetadataCache.TryGetAccessors(typeof(AccSig2), out var stored).ShouldBeTrue();
        var sig = new AccSig2 { Q = "x", N = 0 };
        var firstResult = ((Func<AccSig2, object?>)stored![0])(sig);
        firstResult.ShouldBe("first");
    }
}
