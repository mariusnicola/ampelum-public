using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Retry;
using Shouldly;
using Xunit;

public sealed class WithRetryExtensionsTests
{
    [Fact]
    public async Task FeedbackFreeOverload_IgnoresFeedbackList()
    {
        int count = 0;
        Func<CancellationToken, Task<int>> fn = _ =>
        {
            count++;
            if (count < 2) throw new AssertionFailedException("x");
            return Task.FromResult(42);
        };
        var wrapped = fn.WithRetry(maxAttempts: 3);
        (await wrapped.ForwardAsync(TestContext.Current.CancellationToken)).ShouldBe(42);
        count.ShouldBe(2);
    }

    [Fact]
    public async Task FeedbackOverload_PassesFeedback()
    {
        IReadOnlyList<string>? observed = null;
        Func<IReadOnlyList<string>, CancellationToken, Task<int>> fn = (fb, _) =>
        {
            observed = fb;
            if (fb.Count == 0) throw new SuggestionException("try harder");
            return Task.FromResult(7);
        };
        var wrapped = fn.WithRetry(maxAttempts: 3);
        (await wrapped.ForwardAsync(TestContext.Current.CancellationToken)).ShouldBe(7);
        observed!.Count.ShouldBe(1);
        observed![0].ShouldContain("try harder");
    }
}
