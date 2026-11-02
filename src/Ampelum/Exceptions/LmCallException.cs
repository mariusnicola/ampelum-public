namespace SignalGrid.Ampelum.Exceptions;

/// <summary>
/// Thrown when the underlying <c>IChatClient.GetResponseAsync</c> call fails with an unexpected exception.
/// The original exception is available via <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class LmCallException(string message, Exception inner)
    : AmpelumException(message, inner);
