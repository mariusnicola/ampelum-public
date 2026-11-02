using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public sealed class InferRulesTests
{
    sealed class SpyHook : IHook
    {
        public int Order => 0;
        public List<string> Calls { get; } = new();
        public IReadOnlyDictionary<string, object?>? InputsSeen { get; private set; }

        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"before:{ctx.ModuleName}");
            InputsSeen = ctx.Inputs;
            return default;
        }

        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"after:{ctx.ModuleName}");
            return default;
        }

        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
        {
            Calls.Add($"error:{error.GetType().Name}");
            return default;
        }
    }

    [Fact]
    public async Task CompileAsync_RunsProposerCallThroughHookedLmCall_UsingTheRuleProposerClient()
    {
        // context.Chat and _ruleProposer are deliberately DIFFERENT clients here: the hook
        // pipeline lives on `context`, but the actual LM call must still go to the proposer
        // the optimizer was constructed with, not context.Chat.
        var contextChat = new FakeChatClient(); // nothing enqueued — must never be called
        var proposerChat = new FakeChatClient();
        proposerChat.EnqueueText("[[ ## Rules ## ]]\nAlways answer in English.");
        var hook = new SpyHook();
        var ctx = TestHelpers.MakeContext(contextChat) with { Hooks = [hook] };

        var result = await new InferRules<FakeProg>(proposerChat)
            .CompileAsync(new FakeProg(), MakeTrainset(1), new TrueMetric(),
                ctx, null, TestContext.Current.CancellationToken);

        result.InstructionOverrides["qa"].ShouldContain("English");
        hook.Calls.ShouldBe(new[] { "before:InferRules.ProposeRules", "after:InferRules.ProposeRules" });
        contextChat.RecordedCalls.ShouldBeEmpty();
        proposerChat.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public void Ctor_NullRuleProposer_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new InferRules<FakeProg>(null!));

    [Fact]
    public async Task CompileAsync_NullProgram_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            new InferRules<FakeProg>(new FakeChatClient())
                .CompileAsync(null!, Array.Empty<Example>(),
                    new TrueMetric(), TestHelpers.MakeContext(),
                    null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullTrainset_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            new InferRules<FakeProg>(new FakeChatClient())
                .CompileAsync(new FakeProg(), null!,
                    new TrueMetric(), TestHelpers.MakeContext(),
                    null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullMetric_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            new InferRules<FakeProg>(new FakeChatClient())
                .CompileAsync(new FakeProg(), Array.Empty<Example>(),
                    null!, TestHelpers.MakeContext(),
                    null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            new InferRules<FakeProg>(new FakeChatClient())
                .CompileAsync(new FakeProg(), Array.Empty<Example>(),
                    new TrueMetric(), null!,
                    null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_EmptyTrainset_ReturnsEmptyOverrides()
    {
        var result = await new InferRules<FakeProg>(new FakeChatClient())
            .CompileAsync(new FakeProg(), Array.Empty<Example>(),
                new TrueMetric(), TestHelpers.MakeContext(),
                null, TestContext.Current.CancellationToken);

        result.InstructionOverrides.ShouldBeEmpty();
    }

    [Fact]
    public async Task CompileAsync_ExtractsRuleString_IntoInstructionOverrides()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText("[[ ## Rules ## ]]\nAlways answer in English.");

        var result = await new InferRules<FakeProg>(chat)
            .CompileAsync(new FakeProg(), MakeTrainset(1), new TrueMetric(),
                TestHelpers.MakeContext(), null,
                TestContext.Current.CancellationToken);

        result.InstructionOverrides.ShouldNotBeEmpty();
        result.InstructionOverrides["qa"].ShouldContain("English");
    }

    [Fact]
    public async Task CompileAsync_NoMarker_FallsBackToFullText()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText("Be concise and accurate.");

        var result = await new InferRules<FakeProg>(chat)
            .CompileAsync(new FakeProg(), MakeTrainset(1), new TrueMetric(),
                TestHelpers.MakeContext(), null,
                TestContext.Current.CancellationToken);

        result.InstructionOverrides["qa"].ShouldContain("concise");
    }

    [Fact]
    public async Task CompileAsync_EmptySubmodules_FallsBackToDefaultKey()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText("[[ ## Rules ## ]]\nBe concise.");

        var result = await new InferRules<EmptyComposed>(chat)
            .CompileAsync(new EmptyComposed(), MakeTrainset2(1), new TrueMetric(),
                TestHelpers.MakeContext(), null,
                TestContext.Current.CancellationToken);

        result.InstructionOverrides.ShouldContainKey("default");
    }

    [Fact]
    public async Task CompileAsync_NonComposedProgram_FallsBackToDefaultKey()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText("[[ ## Rules ## ]]\nFollow patterns.");

        var result = await new InferRules<PlainProg>(chat)
            .CompileAsync(new PlainProg(), MakeTrainsetPlain(1), new TrueMetric(),
                TestHelpers.MakeContext(), null,
                TestContext.Current.CancellationToken);

        result.InstructionOverrides.ShouldContainKey("default");
    }

    [Fact]
    public async Task CompileAsync_ProposerThrows_WrappedInOptimizerException()
    {
        var chat = new FakeChatClient();
        chat.EnqueueError(new InvalidOperationException("boom"));

        await Should.ThrowAsync<OptimizerException>(() =>
            new InferRules<FakeProg>(chat)
                .CompileAsync(new FakeProg(), MakeTrainset(1), new TrueMetric(),
                    TestHelpers.MakeContext(), null,
                    TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_ReportsProgress()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText("[[ ## Rules ## ]]\nx");
        var reports = new List<OptimizerProgress>();

        await new InferRules<FakeProg>(chat)
            .CompileAsync(new FakeProg(), MakeTrainset(1), new TrueMetric(),
                TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add),
                TestContext.Current.CancellationToken);

        reports.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CompileAsync_CancelledToken_Throws()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new InferRules<FakeProg>(new FakeChatClient())
                .CompileAsync(new FakeProg(), MakeTrainset(1),
                    new TrueMetric(), TestHelpers.MakeContext(),
                    null, cts.Token));
    }

    static List<Example> MakeTrainset(int n) =>
        Enumerable.Range(0, n)
            .Select(i => new Example(
                new Dictionary<string, object?> { ["q"] = $"q{i}" },
                new Dictionary<string, object?> { ["a"] = $"a{i}" }))
            .ToList();

    static List<Example> MakeTrainset2(int n) => MakeTrainset(n);
    static List<Example> MakeTrainsetPlain(int n) => MakeTrainset(n);

    sealed class SyncProgress<T>(Action<T> cb) : IProgress<T>
    { public void Report(T value) => cb(value); }

    sealed class FakeProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["qa"] = new object() };
        public object Rebind(IReadOnlyDictionary<string, object> r) => this;
    }

    sealed class EmptyComposed : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object>();
        public object Rebind(IReadOnlyDictionary<string, object> r) => this;
    }

    sealed class PlainProg { }

    sealed class TrueMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => 1.0;
    }
}
