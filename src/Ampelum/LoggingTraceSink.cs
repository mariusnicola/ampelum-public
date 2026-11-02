#pragma warning disable CA1848

using Microsoft.Extensions.Logging;

namespace SignalGrid.Ampelum;

/// <summary>
/// An <see cref="ITraceSink"/> that logs a one-line summary of each trace at <c>Information</c> level.
/// Register via <c>AmpelumBuilder.UseTraceSink&lt;LoggingTraceSink&gt;()</c> or dependency injection.
/// </summary>
public sealed class LoggingTraceSink(ILogger<LoggingTraceSink> logger) : ITraceSink
{
    /// <inheritdoc/>
    public void Write(TraceRecord record) =>
        logger.LogInformation(
            "ampelum trace: module={Module} signature={Signature} duration_ms={Duration} tokens_in={In} tokens_out={Out}",
            record.ModuleName,
            record.SignatureType.Name,
            record.Duration.TotalMilliseconds,
            record.Usage?.InputTokenCount,
            record.Usage?.OutputTokenCount);
}
