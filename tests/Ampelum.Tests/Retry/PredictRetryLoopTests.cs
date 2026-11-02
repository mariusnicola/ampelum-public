using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retry;

/// <summary>
/// v1.0-rc1 alpha.3 Gap 2 unit coverage: Predict.ForwardAsync auto-loops on
/// AdapterParseException when RetryHook publishes a corrective prompt.
/// </summary>
public sealed class PredictRetryLoopTests
{
    [Signature("Be brief.")]
    public sealed record Sig : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public string A { get; init; } = "";
    }

    [Fact]
    public async Task RetryLoop_FirstParseFails_SecondAttemptSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fake = new FakeChatClient();
        fake.EnqueueText("garbage no marker");
        fake.EnqueueText("[[ ## A ## ]]\nok\n[[ ## completed ## ]]");

        var ctx = new AmpelumContext(fake, new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = new IHook[] { new RetryHook(maxRecoveryAttempts: 2) },
        };

        var p = new Predict<Sig>(ctx);
        var r = await p.ForwardAsync(new Sig { Q = "hi" }, ct: ct);
        r.Value.A.ShouldBe("ok");
    }

    [Fact]
    public async Task RetryLoop_NoHookPipeline_AdapterParseExceptionStillPropagates()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fake = new FakeChatClient();
        fake.EnqueueText("garbage no marker");

        var ctx = new AmpelumContext(fake, new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance);
        // No Hooks â†’ no retry pipeline â†’ original behaviour preserved.

        var p = new Predict<Sig>(ctx);
        await Should.ThrowAsync<AdapterParseException>(
            async () => await p.ForwardAsync(new Sig { Q = "hi" }, ct: ct));
    }

    [Fact]
    public async Task RetryLoop_AttemptsExhausted_RethrowsLastParseException()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fake = new FakeChatClient();
        fake.EnqueueText("first garbage");
        fake.EnqueueText("second garbage");
        fake.EnqueueText("third garbage");

        var ctx = new AmpelumContext(fake, new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = new IHook[] { new RetryHook(maxRecoveryAttempts: 1) },
        };

        var p = new Predict<Sig>(ctx);
        await Should.ThrowAsync<AdapterParseException>(
            async () => await p.ForwardAsync(new Sig { Q = "hi" }, ct: ct));
    }
}
