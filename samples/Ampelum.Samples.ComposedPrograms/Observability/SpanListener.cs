using System.Diagnostics;

namespace Ampelum.Samples.ComposedPrograms.Observability;

/// <summary>
/// Turns span recording on for the Ampelum activity source. Without a listener no span exists, and the
/// GenAI attributes that the log hook copies into its scope would be missing.
/// </summary>
public sealed class SpanListener : IDisposable
{
    public const string SourceName = "SignalGrid.Ampelum";

    readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name == SourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    };

    public SpanListener() => ActivitySource.AddActivityListener(_listener);

    public void Dispose() => _listener.Dispose();
}
