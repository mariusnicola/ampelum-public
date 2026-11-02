#pragma warning disable CA1727

using Microsoft.Extensions.Logging;

namespace SignalGrid.Ampelum;

internal static partial class AmpelumLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "ampelum forward ok module={Module} signature={Signature} duration_ms={DurationMs} tokens_in={TokensIn} tokens_out={TokensOut}")]
    public static partial void ForwardOk(ILogger logger, string module, string signature, double durationMs, long? tokensIn, long? tokensOut);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error,
        Message = "ampelum forward failed module={Module} signature={Signature}")]
    public static partial void ForwardFailed(ILogger logger, string module, string signature, Exception exception);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "ampelum trace sink threw; suppressed to keep forward alive")]
    public static partial void TraceSinkThrew(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug,
        Message = "ampelum prompt messages count={Count}")]
    public static partial void PromptMessages(ILogger logger, int count);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Trace,
        Message = "ampelum raw response: {Response}")]
    public static partial void RawResponse(ILogger logger, string response);
}

#pragma warning restore CA1727
