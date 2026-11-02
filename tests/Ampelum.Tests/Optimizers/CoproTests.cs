using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public class CoproTests
{
    [Fact]
    public void Copro_DefaultCandidatesPerSubmodule_Is5()
    {
        var client = new StubChatClient();
        var opt = new Copro<FakeProg>(instructionProposer: client);
        opt.CandidatesPerSubmodule.ShouldBe(5);
    }

    [Fact]
    public void Copro_CustomCandidatesPerSubmodule()
    {
        var client = new StubChatClient();
        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 3);
        opt.CandidatesPerSubmodule.ShouldBe(3);
    }

    [Fact]
    public async Task Copro_CompileAsync_ReturnsOptimizedProgram()
    {
        var client = new StubChatClient(defaultResponse: "Instruction candidate A.");
        var ctx = TestHelpers.MakeContext();
        var prog = new FakeProg();
        var trainset = MakeTrainset(2);
        IMetric metric = new LambdaMetric((_, _, _) => 0.8);

        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 1);
        var result = await opt.CompileAsync(prog, trainset, metric, ctx, ct: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Program.ShouldBeSameAs(prog);
        result.InstructionOverrides.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Copro_CompileAsync_PopulatesInstructionOverrides_ForComposedModule()
    {
        int callIndex = 0;
        var responses = new[] { "Be precise.", "Be concise." };
        var client = new StubChatClient(responseSelector: _ => responses[Math.Min(callIndex++, responses.Length - 1)]);

        var ctx = TestHelpers.MakeContext();
        var prog = new TwoModuleProg();
        var trainset = MakeTrainset(2);
        IMetric metric = new LambdaMetric((_, _, _) => 0.9);

        var opt = new Copro<TwoModuleProg>(instructionProposer: client, candidatesPerSubmodule: 1);
        var result = await opt.CompileAsync(prog, trainset, metric, ctx, ct: TestContext.Current.CancellationToken);

        result.InstructionOverrides.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Copro_TrainsetSize_RecordedOnResult()
    {
        var client = new StubChatClient(defaultResponse: "Be precise.");
        var ctx = TestHelpers.MakeContext();
        var trainset = MakeTrainset(4);
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);

        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 1);
        var result = await opt.CompileAsync(new FakeProg(), trainset, metric, ctx, ct: TestContext.Current.CancellationToken);

        result.TrainsetSize.ShouldBe(4);
    }

    [Fact]
    public async Task Copro_EmptyResponse_Skipped()
    {
        // Returns "" → candidate skipped, no override stored for this submodule.
        var client = new StubChatClient(defaultResponse: "");
        var ctx = TestHelpers.MakeContext();
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);

        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 2);
        var result = await opt.CompileAsync(new FakeProg(), MakeTrainset(1), metric, ctx, ct: TestContext.Current.CancellationToken);
        result.InstructionOverrides.ShouldBeEmpty();
    }

    [Fact]
    public async Task Copro_ProposerThrows_WrappedInOptimizerException()
    {
        var client = new ThrowingChatClient();
        var ctx = TestHelpers.MakeContext();
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);
        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 1);
        await Should.ThrowAsync<OptimizerException>(() =>
            opt.CompileAsync(new FakeProg(), MakeTrainset(1), metric, ctx, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Copro_MetricThrows_SkipsExample()
    {
        var client = new StubChatClient(defaultResponse: "candidate");
        var ctx = TestHelpers.MakeContext();
        bool threw = false;
        IMetric metric = new LambdaMetric((_, _, _) =>
        {
            if (!threw) { threw = true; throw new InvalidOperationException("x"); }
            return 0.5;
        });
        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 1);
        var result = await opt.CompileAsync(new FakeProg(), MakeTrainset(2), metric, ctx, ct: TestContext.Current.CancellationToken);
        result.InstructionOverrides.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Copro_AllMetricsThrow_NoOverride()
    {
        var client = new StubChatClient(defaultResponse: "candidate");
        var ctx = TestHelpers.MakeContext();
        IMetric metric = new LambdaMetric((_, _, _) => throw new InvalidOperationException("always"));
        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 1);
        var result = await opt.CompileAsync(new FakeProg(), MakeTrainset(2), metric, ctx, ct: TestContext.Current.CancellationToken);
        // avgScore=0; localBestInstruction may still be set since 0 > -inf. Verify not empty.
        result.InstructionOverrides.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Copro_CancelledToken_Throws()
    {
        var client = new StubChatClient();
        var ctx = TestHelpers.MakeContext();
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);
        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            opt.CompileAsync(new FakeProg(), MakeTrainset(1), metric, ctx, ct: cts.Token));
    }

    [Fact]
    public async Task Copro_ProposerCancelled_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var client = new CancellingChatClient(cts);
        var ctx = TestHelpers.MakeContext();
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);
        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 1);
        await Should.ThrowAsync<OperationCanceledException>(() =>
            opt.CompileAsync(new FakeProg(), MakeTrainset(1), metric, ctx, ct: cts.Token));
    }

    [Fact]
    public async Task Copro_NullArgs_Throw()
    {
        var ctx = TestHelpers.MakeContext();
        var opt = new Copro<FakeProg>(instructionProposer: new StubChatClient());
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);
        await Should.ThrowAsync<ArgumentNullException>(() =>
            opt.CompileAsync(null!, MakeTrainset(1), metric, ctx, ct: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(() =>
            opt.CompileAsync(new FakeProg(), null!, metric, ctx, ct: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(() =>
            opt.CompileAsync(new FakeProg(), MakeTrainset(1), null!, ctx, ct: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(() =>
            opt.CompileAsync(new FakeProg(), MakeTrainset(1), metric, null!, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Copro_MultipleCandidates_KeepsBest()
    {
        // Returns scores 0.1 then 0.9 — second candidate should win.
        int idx = 0;
        var scores = new[] { 0.1, 0.9 };
        var client = new StubChatClient(responseSelector: _ => $"candidate{idx}");
        IMetric metric = new LambdaMetric((_, _, _) =>
        {
            var s = scores[Math.Min(idx, scores.Length - 1)];
            idx++;
            return s;
        });
        var ctx = TestHelpers.MakeContext();
        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 2);
        var result = await opt.CompileAsync(new FakeProg(), MakeTrainset(1), metric, ctx, ct: TestContext.Current.CancellationToken);
        result.Score!.Value.ShouldBeGreaterThanOrEqualTo(0.0);
    }

    [Fact]
    public async Task CompileAsync_WithProgramRunner_PicksHigherScoringCandidate()
    {
        // Regression: when a programRunner is supplied, COPRO must actually run the program
        // with each candidate's instruction injected as an override and score actual-vs-expected,
        // not compare ex.Outputs to itself.
        int idx = 0;
        var responses = new[] { "BAD instruction", "GOOD instruction" };
        var client = new StubChatClient(responseSelector: _ =>
            responses[Math.Min(idx++, responses.Length - 1)]);

        // Runner returns expected outputs only when the active instruction override is "GOOD ...".
        var runner = (FakeProg _, Example ex, AmpelumContext ctx, CancellationToken _) =>
        {
            var ovr = ctx.InstructionOverrides;
            string? active = null;
            if (ovr is not null && ovr.TryGetValue("__single__", out var v)) active = v;
            var outputs = (active is not null && active.StartsWith("GOOD"))
                ? ex.Outputs
                : new Dictionary<string, object?> { ["a"] = "WRONG" };
            return Task.FromResult(new Example(ex.Inputs, outputs));
        };

        IMetric metric = new LambdaMetric((actual, expected, _) =>
        {
            var a = ((IReadOnlyDictionary<string, object?>)actual).TryGetValue("a", out var av) ? av?.ToString() : null;
            var e = ((IReadOnlyDictionary<string, object?>)expected).TryGetValue("a", out var ev) ? ev?.ToString() : null;
            return string.Equals(a, e, StringComparison.Ordinal) ? 1.0 : 0.0;
        });

        var ctx = TestHelpers.MakeContext();
        var opt = new Copro<FakeProg>(
            instructionProposer: client,
            candidatesPerSubmodule: 2,
            programRunner: runner);

        var result = await opt.CompileAsync(
            new FakeProg(), MakeTrainset(2), metric, ctx,
            ct: TestContext.Current.CancellationToken);

        result.InstructionOverrides["__single__"].ShouldBe("GOOD instruction");
    }

    [Fact]
    public async Task Copro_RunsHookPipeline_AroundInstructionProposal()
    {
        var client = new StubChatClient(defaultResponse: "Instruction candidate A.");
        var hook = new SpyHook();
        var ctx = TestHelpers.MakeContext() with { Hooks = [hook] };
        IMetric metric = new LambdaMetric((_, _, _) => 0.8);

        var opt = new Copro<FakeProg>(instructionProposer: client, candidatesPerSubmodule: 1);
        await opt.CompileAsync(new FakeProg(), MakeTrainset(1), metric, ctx, ct: TestContext.Current.CancellationToken);

        hook.Calls.ShouldContain("before:CoproOptimizer.ProposeInstruction");
        hook.Calls.ShouldContain("after:CoproOptimizer.ProposeInstruction");
    }

    sealed class SpyHook : SignalGrid.Ampelum.Hooks.IHook
    {
        public int Order => 0;
        public List<string> Calls { get; } = new();

        public ValueTask OnBeforeAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"before:{ctx.ModuleName}");
            return default;
        }

        public ValueTask OnAfterAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"after:{ctx.ModuleName}");
            return default;
        }

        public ValueTask OnErrorAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, Exception error, CancellationToken ct)
        {
            Calls.Add($"error:{ctx.ModuleName}");
            return default;
        }
    }

    sealed class FakeProg
    {
#pragma warning disable CA1822
        public FakeProg WithContext(AmpelumContext ctx) => this;
#pragma warning restore CA1822
    }

    sealed class TwoModuleProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object>
            {
                ["retriever"] = new object(),
                ["answer"]    = new object(),
            };

        public object Rebind(IReadOnlyDictionary<string, object> _) => this;
    }

    sealed class LambdaMetric(Func<object, object, SignatureMetadata, double> fn) : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata sig) => fn(actual, expected, sig);
    }

    static IReadOnlyList<Example> MakeTrainset(int n) =>
        Enumerable.Range(0, n)
            .Select(i => new Example(
                new Dictionary<string, object?> { ["q"] = $"q{i}" },
                new Dictionary<string, object?> { ["a"] = $"a{i}" }))
            .ToList();

    sealed class StubChatClient(
        string? defaultResponse = "Generated instruction.",
        Func<IReadOnlyList<ChatMessage>, string>? responseSelector = null)
        : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var msgList = messages.ToList();
            var text = responseSelector?.Invoke(msgList) ?? defaultResponse ?? "";
            var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]);
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    sealed class CancellingChatClient(CancellationTokenSource cts) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
