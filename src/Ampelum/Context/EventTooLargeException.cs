namespace SignalGrid.Ampelum.Context;

/// <summary>Thrown when an event body exceeds the 256 KiB per-event cap.</summary>
public sealed class EventTooLargeException : Exception
{
    /// <summary>Actual serialized byte count of the event.</summary>
    public int ActualBytes { get; }

    /// <summary>Maximum permitted byte count.</summary>
    public int MaxBytes { get; }

    /// <summary>Creates a new EventTooLargeException.</summary>
    public EventTooLargeException(int actualBytes, int maxBytes)
        : base($"Event body is {actualBytes} bytes; maximum permitted is {maxBytes} bytes. Spill oversize bodies to blobs/.")
    {
        ActualBytes = actualBytes;
        MaxBytes = maxBytes;
    }
}