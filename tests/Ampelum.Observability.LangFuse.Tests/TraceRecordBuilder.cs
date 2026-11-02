using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;

namespace Ampelum.Observability.LangFuse.Tests;

internal static class TraceRecordBuilder
{
    sealed class TestSig { }

    public static TraceRecord Sample(
        string moduleName = "Predict",
        bool error = false,
        bool withUsage = true,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var inputs = new Dictionary<string, object?> { ["q"] = "x" };
        var outputs = error
            ? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["a"] = "y" };
        var msgs = new List<ChatMessage> { new(ChatRole.User, "x") };
        var usage = withUsage ? new UsageDetails { InputTokenCount = 5, OutputTokenCount = 7, TotalTokenCount = 12 } : null;
        return new TraceRecord(
            Id: Guid.NewGuid(),
            ModuleName: moduleName,
            SignatureType: typeof(TestSig),
            Inputs: inputs,
            Outputs: outputs,
            PromptMessages: msgs,
            RawResponse: error ? "" : "y",
            Usage: usage,
            Duration: TimeSpan.FromMilliseconds(50),
            Error: error ? new InvalidOperationException("oops") : null,
            Metadata: metadata ?? new Dictionary<string, object?>());
    }
}
