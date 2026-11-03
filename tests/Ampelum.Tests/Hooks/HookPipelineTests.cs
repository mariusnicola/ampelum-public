using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Hooks.Internal;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Hooks;

public sealed class HookPipelineTests
{
    sealed class RecordingHook : IHook
    {
        readonly List<string> _log;
        readonly string _name;
        public int Order { get; }
        public Exception? ThrowOnBefore { get; init; }
        public Exception? ThrowOnAfter { get; init; }
        public Exception? ThrowOnError { get; init; }
        public RecordingHook(List<string> log, string name, int order)
        { _log = log; _name = name; Order = order; }
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            _log.Add($"before:{_name}");
            if (ThrowOnBefore is not null) throw ThrowOnBefore;
            return default;
        }
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            _log.Add($"after:{_name}");
            if (ThrowOnAfter is not null) throw ThrowOnAfter;
            return default;
        }
        public ValueTask OnErrorAsync(HookContext ctx, Exception err, CancellationToken ct)
        {
            _log.Add($"error:{_name}");
            if (ThrowOnError is not null) throw ThrowOnError;
            return default;
        }
    }

    static HookContext NewCtx() => new()
    {
        ModuleName = "Predict",
        Inputs = new Dictionary<string, object?>(),
    };

    [Fact]
    public async Task OnBefore_RunsAscendingByOrder()
    {
        var log = new List<string>();
        var pipe = HookPipeline.GetOrCreate(new IHook[]
        {
            new RecordingHook(log, "B", order: 100),
            new RecordingHook(log, "A", order: 0),
        });
        await pipe.RunBeforeAsync(NewCtx(), TestContext.Current.CancellationToken);
        log.ShouldBe(new[] { "before:A", "before:B" });
    }

    [Fact]
    public async Task HooksWithTheSameOrder_RunInRegistrationOrderBefore_AndTheReverseAfter()
    {
        var log = new List<string>();
        var pipe = HookPipeline.GetOrCreate(new IHook[]
        {
            new RecordingHook(log, "first", order: 51),
            new RecordingHook(log, "second", order: 51),
            new RecordingHook(log, "third", order: 51),
        });
        await pipe.RunBeforeAsync(NewCtx(), TestContext.Current.CancellationToken);
        await pipe.RunAfterAsync(NewCtx(), TestContext.Current.CancellationToken);
        log.ShouldBe(new[]
        {
            "before:first", "before:second", "before:third",
            "after:third", "after:second", "after:first",
        });
    }

    [Fact]
    public async Task OnAfter_RunsDescendingByOrder()
    {
        var log = new List<string>();
        var pipe = HookPipeline.GetOrCreate(new IHook[]
        {
            new RecordingHook(log, "B", order: 100),
            new RecordingHook(log, "A", order: 0),
        });
        await pipe.RunAfterAsync(NewCtx(), TestContext.Current.CancellationToken);
        log.ShouldBe(new[] { "after:B", "after:A" });
    }

    [Fact]
    public async Task OnBefore_Throws_AbortsRemainingHooks()
    {
        var log = new List<string>();
        var pipe = HookPipeline.GetOrCreate(new IHook[]
        {
            new RecordingHook(log, "A", order: 0) { ThrowOnBefore = new InvalidOperationException("stop") },
            new RecordingHook(log, "B", order: 100),
        });
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => pipe.RunBeforeAsync(NewCtx(), TestContext.Current.CancellationToken).AsTask());
        ex.Message.ShouldBe("stop");
        log.ShouldBe(new[] { "before:A" });
    }

    [Fact]
    public async Task OnError_RunsDescending_AndSwallowsHookExceptions()
    {
        var log = new List<string>();
        var pipe = HookPipeline.GetOrCreate(new IHook[]
        {
            new RecordingHook(log, "A", order: 0) { ThrowOnError = new Exception("nope") },
            new RecordingHook(log, "B", order: 100),
        });
        var trigger = new InvalidOperationException("LM failed");
        await pipe.RunErrorAsync(NewCtx(), trigger, TestContext.Current.CancellationToken);
        log.ShouldBe(new[] { "error:B", "error:A" });
    }

    [Fact]
    public async Task OnAfter_Throws_PropagatesAndStopsRemaining()
    {
        var log = new List<string>();
        var pipe = HookPipeline.GetOrCreate(new IHook[]
        {
            new RecordingHook(log, "A", order: 0),
            new RecordingHook(log, "B", order: 100) { ThrowOnAfter = new InvalidOperationException("after-fail") },
        });
        await Should.ThrowAsync<InvalidOperationException>(
            () => pipe.RunAfterAsync(NewCtx(), TestContext.Current.CancellationToken).AsTask());
        log.ShouldBe(new[] { "after:B" });
    }

    [Fact]
    public async Task EmptyPipeline_IsNoop()
    {
        var pipe = HookPipeline.GetOrCreate(Array.Empty<IHook>());
        await pipe.RunBeforeAsync(NewCtx(), TestContext.Current.CancellationToken);
        await pipe.RunAfterAsync(NewCtx(), TestContext.Current.CancellationToken);
        await pipe.RunErrorAsync(NewCtx(), new Exception(), TestContext.Current.CancellationToken);
    }

    sealed class OutputWritingHook : IHook
    {
        readonly List<string> _log;
        readonly string _name;
        public int Order { get; }
        public IReadOnlyDictionary<string, object?>? OutputsToWrite { get; init; }
        public Exception? ThrowAfterWrite { get; init; }
        public OutputWritingHook(List<string> log, string name, int order)
        { _log = log; _name = name; Order = order; }
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            _log.Add($"before:{_name}");
            if (OutputsToWrite is not null) ctx.Outputs = OutputsToWrite;
            if (ThrowAfterWrite is not null) throw ThrowAfterWrite;
            return default;
        }
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        { _log.Add($"after:{_name}"); return default; }
        public ValueTask OnErrorAsync(HookContext ctx, Exception err, CancellationToken ct)
        { _log.Add($"error:{_name}"); return default; }
    }

    [Fact]
    public async Task OnBefore_WhenOutputsWritten_StopsRemainingOnBefore()
    {
        var log = new List<string>();
        var ctx = NewCtx();
        var pipe = HookPipeline.GetOrCreate(new IHook[]
        {
            new OutputWritingHook(log, "A", order: 0)
            {
                OutputsToWrite = new Dictionary<string, object?> { ["Answer"] = "cached" },
            },
            new RecordingHook(log, "B", order: 100),
        });

        await pipe.RunBeforeAsync(ctx, TestContext.Current.CancellationToken);

        log.ShouldBe(new[] { "before:A" });
        ctx.Outputs.ShouldNotBeNull();
        ctx.Outputs!["Answer"].ShouldBe("cached");
    }

    [Fact]
    public async Task OnBefore_WritesOutputs_ThenThrows_ThrowWins()
    {
        var log = new List<string>();
        var ctx = NewCtx();
        var pipe = HookPipeline.GetOrCreate(new IHook[]
        {
            new OutputWritingHook(log, "A", order: 0)
            {
                OutputsToWrite = new Dictionary<string, object?> { ["k"] = "v" },
                ThrowAfterWrite = new InvalidOperationException("boom"),
            },
            new RecordingHook(log, "B", order: 100),
        });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => pipe.RunBeforeAsync(ctx, TestContext.Current.CancellationToken).AsTask());
        ex.Message.ShouldBe("boom");
        log.ShouldBe(new[] { "before:A" });
    }

    [Fact]
    public async Task OnAfter_StillRunsForAllHooks_AfterShortCircuit()
    {
        var log = new List<string>();
        var ctx = NewCtx();
        var pipe = HookPipeline.GetOrCreate(new IHook[]
        {
            new OutputWritingHook(log, "A", order: 0)
            {
                OutputsToWrite = new Dictionary<string, object?> { ["k"] = "v" },
            },
            new RecordingHook(log, "B", order: 100),
        });

        await pipe.RunBeforeAsync(ctx, TestContext.Current.CancellationToken);
        await pipe.RunAfterAsync(ctx, TestContext.Current.CancellationToken);

        log.ShouldBe(new[] { "before:A", "after:B", "after:A" });
    }
}
