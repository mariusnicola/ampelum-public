namespace SignalGrid.Ampelum;

/// <summary>
/// Immutable record of a single tool invocation within a ReAct forward pass.
/// <see cref="Result"/> and <see cref="Error"/> are mutually exclusive â€” use the
/// <see cref="Success"/> / <see cref="Failed"/> factory methods to construct.
/// </summary>
public sealed record ToolCallRecord(
    string FunctionName,
    IReadOnlyDictionary<string, object?> Arguments,
    object? Result,
    Exception? Error,
    TimeSpan Duration)
{
    /// <summary>Creates a successful tool call record. <see cref="Error"/> is <see langword="null"/>.</summary>
    public static ToolCallRecord Success(
        string functionName,
        IReadOnlyDictionary<string, object?> arguments,
        object? result,
        TimeSpan duration) =>
        new(functionName, arguments, result, Error: null, duration);

    /// <summary>Creates a failed tool call record. <see cref="Result"/> is <see langword="null"/>.</summary>
    public static ToolCallRecord Failed(
        string functionName,
        IReadOnlyDictionary<string, object?> arguments,
        Exception error,
        TimeSpan duration) =>
        new(functionName, arguments, Result: null, error, duration);
}
