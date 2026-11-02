namespace SignalGrid.Ampelum.Tools;

/// <summary>
/// Marks a tool as having side effects that should be previewed before execution. Spec Â§3.2.
/// Side-effecting tools that omit this interface trigger AMP003.
/// </summary>
public interface IPreviewable
{
    /// <summary>Render a human-readable description of the side effect that <c>InvokeAsync</c> would produce.</summary>
    /// <param name="ct">Cancellation token.</param>
    string PreviewAsync(CancellationToken ct);
}
