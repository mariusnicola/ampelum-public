namespace Ampelum.Tests.Orchestration;

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Orchestration;
using Ampelum.Tests.Composition._Sigs;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class ConditionalBranchTests
{
    sealed class ConstBool(bool value) : IModule<TestSig, bool>
    {
        public Task<Prediction<bool>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            Task.FromResult(new Prediction<bool>(value, Dummy("cond")));
    }

    sealed class ThenModule : IModule<TestSig, TestSig>
    {
        public Task<Prediction<TestSig>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            Task.FromResult(new Prediction<TestSig>(
                inputs with { Answer = "then:" + inputs.Question }, Dummy("then")));
    }

    sealed class ElseModule : IModule<TestSig, ScoreSig>
    {
        public Task<Prediction<ScoreSig>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            Task.FromResult(new Prediction<ScoreSig>(
                new ScoreSig { Question = inputs.Question, Score = 42 }, Dummy("else")));
    }

    sealed class ThrowingCondition : IModule<TestSig, bool>
    {
        public Task<Prediction<bool>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            throw new InvalidOperationException("condition_fail");
    }

    sealed class ThrowingThen : IModule<TestSig, TestSig>
    {
        public Task<Prediction<TestSig>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            throw new InvalidOperationException("then_fail");
    }

    sealed class ThrowingElse : IModule<TestSig, ScoreSig>
    {
        public Task<Prediction<ScoreSig>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            throw new InvalidOperationException("else_fail");
    }

    static TraceRecord Dummy(string modName) => new(
        Id: Guid.NewGuid(),
        ModuleName: modName,
        SignatureType: typeof(object),
        Inputs: new Dictionary<string, object?>(),
        Outputs: new Dictionary<string, object?>(),
        PromptMessages: [],
        RawResponse: "",
        Usage: null,
        Duration: TimeSpan.Zero,
        Error: null,
        Metadata: new Dictionary<string, object?>());

    [Fact]
    public async Task ConditionTrue_ExecutesThenBranch()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ConstBool(true), new ThenModule(), new ElseModule());

        var result = await branch.ForwardAsync(new TestSig { Question = "hello" }, TestContext.Current.CancellationToken);

        var val = (TestSig)result.Value;
        val.Answer.ShouldBe("then:hello");
    }

    [Fact]
    public async Task ConditionFalse_ExecutesElseBranch()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ConstBool(false), new ThenModule(), new ElseModule());

        var result = await branch.ForwardAsync(new TestSig { Question = "hello" }, TestContext.Current.CancellationToken);

        var val = (ScoreSig)result.Value;
        val.Score.ShouldBe(42);
    }

    [Fact]
    public async Task AsymmetricSignatures_ThenAndElseDiffer()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ConstBool(true), new ThenModule(), new ElseModule());

        var result = await branch.ForwardAsync(new TestSig { Question = "test" }, TestContext.Current.CancellationToken);

        result.Value.ShouldBeOfType<TestSig>();
    }

    [Fact]
    public async Task AsymmetricSignatures_ElseTypeDiffers()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ConstBool(false), new ThenModule(), new ElseModule());

        var result = await branch.ForwardAsync(new TestSig { Question = "test" }, TestContext.Current.CancellationToken);

        result.Value.ShouldBeOfType<ScoreSig>();
    }

    [Fact]
    public async Task ConditionError_Propagates()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ThrowingCondition(), new ThenModule(), new ElseModule());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            branch.ForwardAsync(new TestSig { Question = "x" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThenBranchError_Propagates()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ConstBool(true), new ThrowingThen(), new ElseModule());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            branch.ForwardAsync(new TestSig { Question = "x" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ElseBranchError_Propagates()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ConstBool(false), new ThenModule(), new ThrowingElse());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            branch.ForwardAsync(new TestSig { Question = "x" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_NullCondition_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new ConditionalBranch<TestSig, TestSig, ScoreSig>(null!, new ThenModule(), new ElseModule()));
    }

    [Fact]
    public void Constructor_NullThen_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new ConditionalBranch<TestSig, TestSig, ScoreSig>(new ConstBool(true), null!, new ElseModule()));
    }

    [Fact]
    public void Constructor_NullElse_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new ConditionalBranch<TestSig, TestSig, ScoreSig>(new ConstBool(true), new ThenModule(), null!));
    }

    [Fact]
    public async Task Trace_ContainsBranchMetadata_WhenTrue()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ConstBool(true), new ThenModule(), new ElseModule());

        var result = await branch.ForwardAsync(new TestSig { Question = "x" }, TestContext.Current.CancellationToken);

        result.Trace.Metadata.ShouldContainKey(SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive);
        result.Trace.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive].ShouldBe("ConditionalBranch");
    }

    [Fact]
    public async Task InputState_UnchangedInBothBranches()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ConstBool(true), new ThenModule(), new ElseModule());

        var result = await branch.ForwardAsync(new TestSig { Question = "original" }, TestContext.Current.CancellationToken);

        var val = (TestSig)result.Value;
        val.Answer.ShouldBe("then:original");
    }

    [Fact]
    public async Task OutputType_SameAsThen_WhenConditionTrue()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ConstBool(true), new ThenModule(), new ElseModule());

        var result = await branch.ForwardAsync(new TestSig { Question = "t" }, TestContext.Current.CancellationToken);

        result.Value.ShouldBeAssignableTo<TestSig>();
        result.Value.ShouldNotBeAssignableTo<ScoreSig>();
    }

    [Fact]
    public async Task OutputType_SameAsElse_WhenConditionFalse()
    {
        var branch = new ConditionalBranch<TestSig, TestSig, ScoreSig>(
            new ConstBool(false), new ThenModule(), new ElseModule());

        var result = await branch.ForwardAsync(new TestSig { Question = "t" }, TestContext.Current.CancellationToken);

        result.Value.ShouldBeAssignableTo<ScoreSig>();
        result.Value.ShouldNotBeAssignableTo<TestSig>();
    }
}
