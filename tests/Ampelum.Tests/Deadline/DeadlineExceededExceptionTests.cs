using SignalGrid.Ampelum.Exceptions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Deadline;

public sealed class DeadlineExceededExceptionTests
{
    [Fact]
    public void Constructor_StoresPhaseAndDeadline()
    {
        var when = DateTimeOffset.UtcNow;
        var ex = new DeadlineExceededException(when, "post-lm");
        ex.Deadline.ShouldBe(when);
        ex.Phase.ShouldBe("post-lm");
        ex.Message.ShouldContain("post-lm");
    }

    [Fact]
    public void Derives_From_TimeoutException()
    {
        // PSQ-5 originally proposed OperationCanceledException as the base, but the
        // async-state-machine special-cases OCE-derived exceptions (marks the Task as
        // Canceled, dropping the derived type), preventing callers from observing the
        // concrete DeadlineExceededException type. TimeoutException is semantically
        // correct (a deadline crossing is a timeout) and preserves the type across await.
        var ex = new DeadlineExceededException(DateTimeOffset.UtcNow, "x");
        ex.ShouldBeAssignableTo<TimeoutException>();
    }
}
