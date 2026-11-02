using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;

namespace Ampelum.Observability.Phoenix.Tests;

internal static class TraceRecordBuilder
{
    sealed class TestSig { }

    public static TraceRecord Sample(
        bool error = false,
        bool withUsage = true,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var inputs = new Dictionary<string, object?> { ["q"] = "x" };
        var outputs = error
            ? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["a"] = "y" };
        var msgs = new List<ChatMessage> { new(ChatRole.User, "hello") };
        var usage = withUsage ? new UsageDetails { InputTokenCount = 4, OutputTokenCount = 6, TotalTokenCount = 10 } : null;
        return new TraceRecord(
            Id: Guid.NewGuid(),
            ModuleName: "Predict",
            SignatureType: typeof(TestSig),
            Inputs: inputs,
            Outputs: outputs,
            PromptMessages: msgs,
            RawResponse: error ? "" : "hi",
            Usage: usage,
            Duration: TimeSpan.FromMilliseconds(40),
            Error: error ? new InvalidOperationException("nope") : null,
            Metadata: metadata ?? new Dictionary<string, object?>());
    }
}
