using Xunit;
using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;

namespace Ampelum.Observability.LangSmith.Tests;

internal static class TraceRecordBuilder
{
    sealed class TestSig { }

    public static TraceRecord Sample(
        string moduleName = "Predict",
        bool error = false,
        bool withUsage = true,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var inputs = new Dictionary<string, object?> { ["question"] = "What is 2+2?" };
        var outputs = error
            ? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["answer"] = "4" };
        var msgs = new List<ChatMessage> { new(ChatRole.User, "What is 2+2?") };
        var usage = withUsage ? new UsageDetails { InputTokenCount = 12, OutputTokenCount = 3, TotalTokenCount = 15 } : null;
        return new TraceRecord(
            Id: Guid.NewGuid(),
            ModuleName: moduleName,
            SignatureType: typeof(TestSig),
            Inputs: inputs,
            Outputs: outputs,
            PromptMessages: msgs,
            RawResponse: error ? "" : "4",
            Usage: usage,
            Duration: TimeSpan.FromMilliseconds(123),
            Error: error ? new InvalidOperationException("boom") : null,
            Metadata: metadata ?? new Dictionary<string, object?>());
    }
}
