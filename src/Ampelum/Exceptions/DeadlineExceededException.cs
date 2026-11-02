namespace SignalGrid.Ampelum.Exceptions;

/// <summary>
/// Thrown by <c>Predict&lt;TSig&gt;.ForwardAsync</c> at a phase boundary when
/// <c>AmpelumContext.Deadline</c> (or the per-call <c>HookContext.Metadata["deadline.utc"]</c>
/// override) is reached before the call completes (PSQ-5, v0.9g).
/// </summary>
/// <remarks>
/// Derives from <see cref="TimeoutException"/>. Note: PSQ-5 originally proposed
/// <see cref="OperationCanceledException"/> as the base, but the async-state-machine
/// special-cases OCE-derived exceptions (marks the Task as Canceled, dropping the
/// derived type), which prevents callers (and Shouldly's <c>ShouldThrowAsync&lt;T&gt;</c>)
/// from observing the concrete <see cref="DeadlineExceededException"/> type. <see cref="TimeoutException"/>
/// is semantically correct (a deadline crossing is a timeout) and preserves the type
/// across <c>await</c>.
/// </remarks>
public sealed class DeadlineExceededException : TimeoutException
{
    /// <summary>The deadline (UTC) that was crossed.</summary>
    public DateTimeOffset Deadline { get; }

    /// <summary>The phase boundary at which the deadline was observed.</summary>
    public string Phase { get; }

    /// <summary>Initializes a new <see cref="DeadlineExceededException"/>.</summary>
    /// <param name="deadline">The deadline (UTC) that was crossed.</param>
    /// <param name="phase">The phase boundary at which the deadline was observed.</param>
    public DeadlineExceededException(DateTimeOffset deadline, string phase)
        : base($"Deadline {deadline:O} exceeded at phase '{phase}'.")
    {
        Deadline = deadline;
        Phase = phase;
    }
}
