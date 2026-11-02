namespace SignalGrid.Ampelum.Exceptions;

/// <summary>
/// Thrown when <see cref="AmpelumAssert.Assert"/> is called with a false condition.
/// Caught by <c>Retry.RetryModule&lt;TOut&gt;</c>; propagates unchanged if no retry wrapper is present.
/// </summary>
public sealed class AssertionFailedException(string message) : AmpelumException(message);
