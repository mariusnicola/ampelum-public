using System.Diagnostics;
using SignalGrid.Ampelum.Exceptions;

namespace SignalGrid.Ampelum.Retry;

/// <summary>
/// Wraps a program delegate. On <see cref="AssertionFailedException"/> or <see cref="SuggestionException"/>,
/// retries up to <c>maxAttempts - 1</c> additional times, accumulating suggestion text into a feedback
/// list passed into each subsequent attempt.
/// </summary>
/// <remarks>
/// <b>Scope limit (Â§5.2.3):</b> this module does NOT implement upstream backtracking â€”
/// individual sub-modules inside the program delegate are NOT re-run independently. The entire
/// delegate is re-invoked as a whole. See spec Â§5.2.3 for rationale.
/// </remarks>
/// <typeparam name="TOut">The return type of the wrapped program delegate.</typeparam>
public sealed class RetryModule<TOut>
{
    readonly Func<IReadOnlyList<string>, CancellationToken, Task<TOut>> _program;
    readonly int _maxAttempts;

    /// <summary>
    /// Initializes a new <see cref="RetryModule{TOut}"/>.
    /// </summary>
    /// <param name="program">The delegate to invoke on each attempt. Receives accumulated feedback strings and a cancellation token.</param>
    /// <param name="maxAttempts">Maximum number of invocations. Must be >= 1. Default is 3.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="program"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxAttempts"/> is less than 1.</exception>
    public RetryModule(
        Func<IReadOnlyList<string>, CancellationToken, Task<TOut>> program,
        int maxAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts), "must be >= 1");
        _program = program;
        _maxAttempts = maxAttempts;
    }

    /// <summary>
    /// Executes the wrapped program, retrying on <see cref="AssertionFailedException"/> or <see cref="SuggestionException"/>
    /// up to the configured maximum number of attempts.
    /// </summary>
    /// <param name="ct">Cancellation token propagated to each attempt.</param>
    /// <returns>The first successful result from the program delegate.</returns>
    /// <exception cref="AssertionFailedException">Propagated after all attempts are exhausted.</exception>
    /// <exception cref="SuggestionException">Propagated after all attempts are exhausted.</exception>
    public async Task<TOut> ForwardAsync(CancellationToken ct = default)
    {
        var feedback = new List<string>();
        Exception? last = null;
        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.retry.attempt");
            activity?.SetTag("retry.attempt_index", attempt);

            Diagnostics.RetryAttempts.Add(1);

            try
            {
                var result = await _program(feedback, ct).ConfigureAwait(false);
                Diagnostics.RetrySucceededOnAttempt.Record(attempt);
                return MergeAttemptIndex(result, attempt);
            }
            catch (AssertionFailedException ex)
            {
                activity?.SetTag("retry.reason", "assertion");
                last = ex;
                feedback.Add($"## Feedback from previous attempt (attempt {attempt})\n{ex.Message}");
            }
            catch (SuggestionException ex)
            {
                activity?.SetTag("retry.reason", "suggestion");
                last = ex;
                feedback.Add($"## Feedback from previous attempt (attempt {attempt})\n{ex.Suggestion}");
            }
        }
        // All attempts exhausted â€” the last exception is assertion/suggestion.
        throw last!;
    }

    static TOut MergeAttemptIndex(TOut result, int attempt)
    {
        // If TOut is Prediction<T>, merge the attempt index into Trace.Metadata.
        if (result is null) return result!;
        var type = result.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Prediction<>)) return result;

        var traceProp = type.GetProperty("Trace")!;
        var trace = (TraceRecord)traceProp.GetValue(result)!;
        var newMeta = new Dictionary<string, object?>(trace.Metadata)
        {
            [TraceKeys.RetryAttempt] = attempt,
        };
        var newTrace = trace with { Metadata = newMeta };
        // Use the record's generated Clone + init on Trace via `with`; requires reflection on the
        // generic Prediction<T> record's `with` clone method.
        var clonedResult = typeof(RetryModule<TOut>)
            .GetMethod(nameof(CloneWithTrace), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(type.GetGenericArguments()[0])
            .Invoke(null, new object?[] { result, newTrace });
        return (TOut)clonedResult!;
    }

    static Prediction<T> CloneWithTrace<T>(Prediction<T> p, TraceRecord newTrace) =>
        p with { Trace = newTrace };
}

/// <summary>Extension methods for building a <see cref="RetryModule{TOut}"/> from a program delegate.</summary>
public static class RetryModuleExtensions
{
    /// <summary>Builds a feedback-aware retry wrapper: the delegate receives accumulated feedback strings.</summary>
    public static RetryModule<TOut> WithRetry<TOut>(
        this Func<IReadOnlyList<string>, CancellationToken, Task<TOut>> program,
        int maxAttempts = 3) =>
        new(program, maxAttempts);

    /// <summary>Builds a feedback-free retry wrapper: the delegate ignores feedback.</summary>
    public static RetryModule<TOut> WithRetry<TOut>(
        this Func<CancellationToken, Task<TOut>> program,
        int maxAttempts = 3) =>
        new((_, ct) => program(ct), maxAttempts);
}
