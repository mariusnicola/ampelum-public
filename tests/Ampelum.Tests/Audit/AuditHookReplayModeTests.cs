using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

public sealed class AuditHookReplayModeTests
{
    static async Task<InMemoryAuditSink> SinkWithEntries(params (string mod, IReadOnlyDictionary<string, object?> ins, IReadOnlyDictionary<string, object?> outs)[] tuples)
    {
        var sink = new InMemoryAuditSink();
        var rec = new AuditHook(sink);
        foreach (var (mod, ins, outs) in tuples)
        {
            var ctx = new HookContext { ModuleName = mod, Inputs = ins };
            ctx.Set(AuditMetadataKeys.ModelId, "m");
            ctx.Set(AuditMetadataKeys.PromptTokens, 0L);
            ctx.Set(AuditMetadataKeys.CompletionTokens, 0L);
            ctx.Outputs = outs;
            await rec.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        }
        return sink;
    }

    [Fact]
    public async Task Replay_PopulatesOutputs_FromMatchingEntry()
    {
        var sink = await SinkWithEntries(
            ("M", new Dictionary<string, object?> { ["q"] = "hi" },
                  new Dictionary<string, object?> { ["a"] = "recorded" }));
        var hook = new AuditHook(sink, replayMode: true);

        var ctx = new HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?> { ["q"] = "hi" }
        };
        await hook.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Outputs.ShouldNotBeNull();
        ctx.Outputs!["a"].ShouldBe("recorded");
    }

    [Fact]
    public async Task Replay_RepeatsServedInSequenceOrder()
    {
        var ins  = new Dictionary<string, object?> { ["q"] = "same" };
        var outs1 = new Dictionary<string, object?> { ["a"] = "first" };
        var outs2 = new Dictionary<string, object?> { ["a"] = "second" };
        var sink = await SinkWithEntries(("M", ins, outs1), ("M", ins, outs2));
        var hook = new AuditHook(sink, replayMode: true);

        var ctx1 = new HookContext { ModuleName = "M", Inputs = ins };
        await hook.OnBeforeAsync(ctx1, TestContext.Current.CancellationToken);
        ctx1.Outputs!["a"].ShouldBe("first");

        var ctx2 = new HookContext { ModuleName = "M", Inputs = ins };
        await hook.OnBeforeAsync(ctx2, TestContext.Current.CancellationToken);
        ctx2.Outputs!["a"].ShouldBe("second");
    }

    [Fact]
    public async Task Replay_OfAQuestionThePiiHookChanged_ServesTheRecordedOutputs()
    {
        var question = new Dictionary<string, object?> { ["q"] = "Mail me at jane.doe@example.com" };
        var redactor = new SignalGrid.Ampelum.Safety.Pii.RegexPiiRedactor();

        // Recording: the audit entry holds the redacted form of the question.
        var sink = new InMemoryAuditSink();
        var recording = new HookContext { ModuleName = "M", Inputs = question };
        await new SignalGrid.Ampelum.Safety.Pii.PiiRedactionHook(redactor)
            .OnBeforeAsync(recording, TestContext.Current.CancellationToken);
        recording.Outputs = new Dictionary<string, object?> { ["a"] = "recorded" };
        await new AuditHook(sink).OnAfterAsync(recording, TestContext.Current.CancellationToken);

        // Replay: the before pass runs in hook order until a hook supplies the outputs.
        var replaying = new HookContext { ModuleName = "M", Inputs = question };
        IHook[] hooks = [new AuditHook(sink, replayMode: true), new SignalGrid.Ampelum.Safety.Pii.PiiRedactionHook(redactor)];
        foreach (var hook in hooks.OrderBy(h => h.Order))
        {
            await hook.OnBeforeAsync(replaying, TestContext.Current.CancellationToken);
            if (replaying.Outputs is not null) break;
        }

        replaying.Outputs.ShouldNotBeNull();
        replaying.Outputs!["a"].ShouldBe("recorded");
    }

    [Fact]
    public async Task Replay_NoMatch_ThrowsAuditReplayMissingException()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink, replayMode: true);
        var ctx = new HookContext { ModuleName = "M", Inputs = new Dictionary<string, object?> { ["q"] = "missing" } };
        var ex = await Should.ThrowAsync<AuditReplayMissingException>(async () =>
            await hook.OnBeforeAsync(ctx, TestContext.Current.CancellationToken));
        ex.ModuleName.ShouldBe("M");
    }

    [Fact]
    public async Task Replay_OnAfter_DoesNotAppend()
    {
        var sink = await SinkWithEntries(
            ("M", new Dictionary<string, object?> { ["q"] = "hi" },
                  new Dictionary<string, object?> { ["a"] = "x" }));
        var hook = new AuditHook(sink, replayMode: true);
        var ctx = new HookContext { ModuleName = "M", Inputs = new Dictionary<string, object?> { ["q"] = "hi" } };
        await hook.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        sink.NextSequence.ShouldBe(1L);
    }
}
