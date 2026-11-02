using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #12 (master plan Â§6.H, alpha.2; promoted alpha.3). Adapter parse-error
/// recovery: when the LM emits a response missing the required field marker, <see cref="ChatAdapter"/>
/// throws <see cref="AdapterParseException"/>; <see cref="RetryHook"/> publishes a
/// <c>retry.corrective_prompt</c> in <see cref="HookContext"/> metadata; <see cref="Predict{TSig}.ForwardAsync"/>
/// auto-loops the LM call up to <c>maxRecoveryAttempts</c> times with the corrective prompt
/// appended, and the second response (schema-conforming) yields the canonical answer in a single
/// <c>ForwardAsync</c> call.
/// </summary>
public sealed class Pareto12_AdapterParseRecovery : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto12_AdapterParseRecovery(ParetoTestFixture fx) => _fx = fx;

    /// <summary>Malformed first response â†’ corrective re-prompt â†’ schema-conforming second response,
    /// all within one ForwardAsync call. Audit context records retry.attempt=1.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task AdapterParseError_RetryHook_AutoLoops_FirstForwardSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        const string canonicalAnswer = "Lisbon";

        // Single FakeChatClient serves: malformed response first, valid response second.
        using var fake = new FakeChatClient();
        fake.EnqueueText("Sorry, I cannot answer.");
        fake.EnqueueText($"[[ ## Answer ## ]]\n{canonicalAnswer}\n[[ ## completed ## ]]");

        // Probe captures HookContext metadata at OnAfter time (after the successful retry attempt).
        var probe = new MetadataProbe();
        var retry = new RetryHook(maxRecoveryAttempts: 2);

        var ctx = new AmpelumContext(
            fake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
        {
            // OnAfter runs in descending Order: probe (Order=1) is the LAST hook to fire and
            // therefore observes the retry-attempt metadata written by Predict's loop on success.
            Hooks = new IHook[] { retry, probe },
        };

        var predict = new Predict<QaSig>(ctx);
        var result = await predict.ForwardAsync(
            new QaSig { Question = "Capital of Portugal?" }, ct: ct);

        // Single ForwardAsync produced the canonical answer despite the first attempt failing.
        result.Value.Answer.ShouldBe(canonicalAnswer);

        probe.OnAfterMetadata.ShouldNotBeNull();
        probe.OnAfterMetadata!.ShouldContainKey("retry.attempt");
        probe.OnAfterMetadata["retry.attempt"].ShouldBe(1);
    }

    /// <summary>OnAfter-time HookContext metadata snapshot probe (Order=1, runs last in descending OnAfter).</summary>
    sealed class MetadataProbe : IHook
    {
        public IReadOnlyDictionary<string, object?>? OnAfterMetadata { get; private set; }
        public int Order => 1;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            OnAfterMetadata = new Dictionary<string, object?>(ctx.Metadata);
            return default;
        }
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }
}
