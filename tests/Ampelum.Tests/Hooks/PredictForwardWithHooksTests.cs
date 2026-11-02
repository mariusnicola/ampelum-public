using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Hooks;

public sealed class PredictForwardWithHooksTests
{
    [Signature("Answer the question.")]
    public sealed record QaSig : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    sealed class CapturingHook : IHook
    {
        public int Order { get; init; }
        public List<string> Calls { get; } = new();
        public bool ThrowInBefore { get; init; }
        public IReadOnlyDictionary<string, object?>? OutputsToWriteOnBefore { get; init; }
        public IReadOnlyDictionary<string, object?>? OnAfterMetadataSnapshot { get; private set; }
        public ValueTask OnBeforeAsync(HookContext c, CancellationToken ct)
        {
            Calls.Add($"before:{c.ModuleName}:{c.Inputs["Question"]}");
            if (OutputsToWriteOnBefore is not null) c.Outputs = OutputsToWriteOnBefore;
            if (ThrowInBefore) throw new InvalidOperationException("blocked");
            return default;
        }
        public ValueTask OnAfterAsync(HookContext c, CancellationToken ct)
        {
            var ans = c.Outputs!["Answer"];
            var tIn = c.Metadata.TryGetValue(HookMetadataKeys.PromptTokens, out var v1) ? v1 : null;
            Calls.Add($"after:{ans}:tIn={tIn}");
            OnAfterMetadataSnapshot = c.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value);
            return default;
        }
        public ValueTask OnErrorAsync(HookContext c, Exception e, CancellationToken ct)
        {
            Calls.Add($"error:{e.Message}");
            return default;
        }
    }

    static AmpelumContext MakeCtx(FakeChatClient chat, params IHook[] hooks) =>
        new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        { Hooks = hooks.Length == 0 ? null : hooks };

    [Fact]
    public async Task SuccessPath_RunsBeforeAndAfter_WithOutputsAndTokens()
    {
        var chat = new FakeChatClient().EnqueueText(
            "[[ ## Answer ## ]]\nhello",
            new UsageDetails { InputTokenCount = 12, OutputTokenCount = 7 });
        var hook = new CapturingHook { Order = 0 };
        var predict = new Predict<QaSig>(MakeCtx(chat, hook));

        var result = await predict.ForwardAsync(
            new QaSig { Question = "hi?" },
            ct: TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("hello");
        hook.Calls.ShouldBe(new[]
        {
            "before:Predict:hi?",
            "after:hello:tIn=12",
        });

        var snap = hook.OnAfterMetadataSnapshot.ShouldNotBeNull();
        snap.ShouldContainKey(HookMetadataKeys.ModelId);
        snap[HookMetadataKeys.PromptTokens].ShouldBe(12L);
        snap[HookMetadataKeys.CompletionTokens].ShouldBe(7L);
        snap.ShouldContainKey(HookMetadataKeys.LmCallDurationMs);
        ((double)snap[HookMetadataKeys.LmCallDurationMs]!).ShouldBeGreaterThanOrEqualTo(0.0);
    }

    [Fact]
    public async Task SuccessPath_NoUsageOnResponse_PublishesZeroTokens()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nok");
        var hook = new CapturingHook { Order = 0 };
        var predict = new Predict<QaSig>(MakeCtx(chat, hook));

        await predict.ForwardAsync(new QaSig { Question = "?" },
            ct: TestContext.Current.CancellationToken);

        var snap = hook.OnAfterMetadataSnapshot.ShouldNotBeNull();
        snap[HookMetadataKeys.PromptTokens].ShouldBe(0L);
        snap[HookMetadataKeys.CompletionTokens].ShouldBe(0L);
    }

    [Fact]
    public async Task ShortCircuit_OnBeforeWritesOutputs_LmNeverCalled()
    {
        var chat = new FakeChatClient();
        var hook = new CapturingHook
        {
            Order = 0,
            OutputsToWriteOnBefore = new Dictionary<string, object?> { ["Answer"] = "replayed" },
        };
        var predict = new Predict<QaSig>(MakeCtx(chat, hook));

        var result = await predict.ForwardAsync(new QaSig { Question = "hi?" },
            ct: TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("replayed");
        result.Trace.Metadata.ShouldContainKeyAndValue(TraceKeys.HookShortCircuited, (object)true);
        var snap = hook.OnAfterMetadataSnapshot.ShouldNotBeNull();
        snap.ContainsKey(HookMetadataKeys.LmCallDurationMs).ShouldBeFalse();
        snap.ContainsKey(HookMetadataKeys.ModelId).ShouldBeFalse();
    }

    [Fact]
    public async Task ShortCircuit_LowerOrderWritesOutputs_HigherOrderOnBeforeSkipped()
    {
        var chat = new FakeChatClient();
        var lower = new CapturingHook
        {
            Order = 0,
            OutputsToWriteOnBefore = new Dictionary<string, object?> { ["Answer"] = "from-lower" },
        };
        var higher = new CapturingHook { Order = 100 };
        var predict = new Predict<QaSig>(MakeCtx(chat, lower, higher));

        var result = await predict.ForwardAsync(new QaSig { Question = "?" },
            ct: TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("from-lower");
        lower.Calls.Count(c => c.StartsWith("before:")).ShouldBe(1);
        higher.Calls.Count(c => c.StartsWith("before:")).ShouldBe(0);
        lower.Calls.Count(c => c.StartsWith("after:")).ShouldBe(1);
        higher.Calls.Count(c => c.StartsWith("after:")).ShouldBe(1);
    }

    [Fact]
    public async Task ShortCircuit_OnBeforeWritesOutputsThenThrows_ThrowWins_OnErrorRuns_NoOnAfter()
    {
        var chat = new FakeChatClient();
        var hook = new CapturingHook
        {
            Order = 0,
            OutputsToWriteOnBefore = new Dictionary<string, object?> { ["Answer"] = "x" },
            ThrowInBefore = true,
        };
        var predict = new Predict<QaSig>(MakeCtx(chat, hook));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            predict.ForwardAsync(new QaSig { Question = "?" },
                ct: TestContext.Current.CancellationToken));

        hook.Calls.Count(c => c.StartsWith("before:")).ShouldBe(1);
        hook.Calls.Count(c => c.StartsWith("after:")).ShouldBe(0);
        hook.Calls.Count(c => c.StartsWith("error:")).ShouldBe(1);
    }

    [Fact]
    public async Task BeforeThrows_AbortsLmCall_AndOnErrorRuns()
    {
        var chat = new FakeChatClient();
        var hook = new CapturingHook { Order = 0, ThrowInBefore = true };
        var predict = new Predict<QaSig>(MakeCtx(chat, hook));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            predict.ForwardAsync(new QaSig { Question = "hi?" },
                ct: TestContext.Current.CancellationToken));

        hook.Calls.ShouldContain("before:Predict:hi?");
        hook.Calls.ShouldContain("error:blocked");
    }

    [Fact]
    public async Task LmThrows_OnErrorRunsDescending_OnAfterDoesNot()
    {
        var chat = new FakeChatClient().EnqueueError(new HttpRequestException("boom"));
        var hookA = new CapturingHook { Order = 0 };
        var hookB = new CapturingHook { Order = 100 };
        var predict = new Predict<QaSig>(MakeCtx(chat, hookA, hookB));

        await Should.ThrowAsync<SignalGrid.Ampelum.Exceptions.LmCallException>(() =>
            predict.ForwardAsync(new QaSig { Question = "x" },
                ct: TestContext.Current.CancellationToken));

        hookA.Calls.Count(c => c.StartsWith("before:")).ShouldBe(1);
        hookB.Calls.Count(c => c.StartsWith("before:")).ShouldBe(1);
        hookA.Calls.Count(c => c.StartsWith("after:")).ShouldBe(0);
        hookB.Calls.Count(c => c.StartsWith("after:")).ShouldBe(0);
        hookA.Calls.Count(c => c.StartsWith("error:")).ShouldBe(1);
        hookB.Calls.Count(c => c.StartsWith("error:")).ShouldBe(1);
    }

    [Fact]
    public async Task NullHooks_Path_StillWorks()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nok");
        var predict = new Predict<QaSig>(MakeCtx(chat));

        var r = await predict.ForwardAsync(new QaSig { Question = "?" },
            ct: TestContext.Current.CancellationToken);
        r.Value.Answer.ShouldBe("ok");
    }
}
