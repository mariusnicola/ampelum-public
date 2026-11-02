using SignalGrid.Ampelum.Exceptions;

namespace SignalGrid.Ampelum;

/// <summary>
/// Static helpers for module-level assertions and suggestions. Used inside user program code
/// to assert post-conditions on a prediction; caught and retried by <c>Retry.RetryModule&lt;TOut&gt;</c>.
/// </summary>
public static class AmpelumAssert
{
    /// <summary>Throws <see cref="AssertionFailedException"/> if <paramref name="condition"/> is false.</summary>
    /// <param name="condition">The condition to assert.</param>
    /// <param name="message">The message to include in the exception if the assertion fails.</param>
    /// <exception cref="AssertionFailedException">Thrown when <paramref name="condition"/> is false.</exception>
    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new AssertionFailedException(message);
    }

    /// <summary>Throws <see cref="SuggestionException"/> carrying <paramref name="suggestion"/> if <paramref name="condition"/> is false.</summary>
    /// <param name="condition">The condition to check.</param>
    /// <param name="suggestion">The suggestion feedback to inject into the next retry attempt.</param>
    /// <exception cref="SuggestionException">Thrown when <paramref name="condition"/> is false.</exception>
    public static void Suggest(bool condition, string suggestion)
    {
        if (!condition) throw new SuggestionException(suggestion);
    }
}
