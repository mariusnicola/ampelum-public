using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Retry;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class RetryModuleTests
{
    static Prediction<string> StrPred(string val, int attempt) => new(val,
        new TraceRecord(Guid.NewGuid(), "Predict", typeof(string),
            new Dictionary<string, object?>(), new Dictionary<string, object?>(),
            Array.Empty<ChatMessage>(), "", null, TimeSpan.Zero, null,
            new Dictionary<string, object?>()));

    [Fact]
    public async Task Succeeds_OnFirstAttempt_AttemptIndex_Zero()
    {
        int count = 0;
        var module = new RetryModule<Prediction<string>>(
            (_, _) => { count++; return Task.FromResult(StrPred("ok", 0)); },
            maxAttempts: 3);
        var res = await module.ForwardAsync(TestContext.Current.CancellationToken);
        count.ShouldBe(1);
        res.Trace.Metadata[TraceKeys.RetryAttempt].ShouldBe(0);
    }

    [Fact]
    public async Task Retries_OnAssertionFailure_UpTo_MaxAttempts()
    {
        int count = 0;
        var module = new RetryModule<Prediction<string>>(
            (_, _) => { count++; throw new AssertionFailedException("too long"); },
            maxAttempts: 3);
        await Should.ThrowAsync<AssertionFailedException>(
            () => module.ForwardAsync(TestContext.Current.CancellationToken));
        count.ShouldBe(3);
    }

    [Fact]
    public async Task Retries_OnSuggestion_AccumulatesFeedback()
    {
        int count = 0;
        IReadOnlyList<string>? capturedFeedback = null;
        var module = new RetryModule<Prediction<string>>(
            (fb, _) =>
            {
                count++;
                capturedFeedback = fb;
                if (count < 2) throw new SuggestionException($"hint-{count}");
                return Task.FromResult(StrPred("ok", count - 1));
            },
            maxAttempts: 3);
        var res = await module.ForwardAsync(TestContext.Current.CancellationToken);
        count.ShouldBe(2);
        capturedFeedback!.Count.ShouldBe(1);
        capturedFeedback![0].ShouldContain("hint-1");
        res.Trace.Metadata[TraceKeys.RetryAttempt].ShouldBe(1);
    }

    [Fact]
    public async Task NonAssert_Exception_Propagates_WithoutRetry()
    {
        int count = 0;
        var module = new RetryModule<Prediction<string>>(
            (_, _) => { count++; throw new InvalidOperationException("nope"); },
            maxAttempts: 5);
        await Should.ThrowAsync<InvalidOperationException>(
            () => module.ForwardAsync(TestContext.Current.CancellationToken));
        count.ShouldBe(1);
    }

    [Fact]
    public async Task CancellationToken_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var module = new RetryModule<Prediction<string>>(
            (_, t) => { t.ThrowIfCancellationRequested(); return Task.FromResult(StrPred("ok", 0)); },
            maxAttempts: 3);
        await Should.ThrowAsync<OperationCanceledException>(() => module.ForwardAsync(cts.Token));
    }

    [Fact]
    public async Task FinalAttemptFailure_PropagatesLastAssertionException()
    {
        var module = new RetryModule<Prediction<string>>(
            (_, _) => throw new AssertionFailedException("attempt-n"),
            maxAttempts: 2);
        var ex = await Should.ThrowAsync<AssertionFailedException>(
            () => module.ForwardAsync(TestContext.Current.CancellationToken));
        ex.Message.ShouldBe("attempt-n");
    }

    [Fact]
    public void Constructor_NullProgram_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new RetryModule<Prediction<string>>(null!, maxAttempts: 3));
    }

    [Fact]
    public void Constructor_MaxAttempts_LessThan1_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new RetryModule<Prediction<string>>(
                (_, _) => Task.FromResult(StrPred("ok", 0)),
                maxAttempts: 0));
    }

    [Fact]
    public async Task NonPrediction_TOut_ReturnsUnchanged()
    {
        // Covers the !type.IsGenericType branch (int is not Prediction<T>)
        var module = new RetryModule<int>(
            (_, _) => Task.FromResult(42),
            maxAttempts: 3);
        var result = await module.ForwardAsync(TestContext.Current.CancellationToken);
        result.ShouldBe(42);
    }

    [Fact]
    public async Task NullResult_ReturnsNull()
    {
        // Covers the `result is null` branch in MergeAttemptIndex
        var module = new RetryModule<string?>(
            (_, _) => Task.FromResult<string?>(null),
            maxAttempts: 3);
        var result = await module.ForwardAsync(TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task NonPredictionTOut_ReturnsResultAsIs()
    {
        // Covers the non-Prediction<T> branch of MergeAttemptIndex (generic-type-definition check falls through).
        int count = 0;
        var module = new RetryModule<string>(
            (_, _) => { count++; return Task.FromResult("ok"); },
            maxAttempts: 3);
        var res = await module.ForwardAsync(TestContext.Current.CancellationToken);
        res.ShouldBe("ok");
        count.ShouldBe(1);
    }
}
