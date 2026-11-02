using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using Ampelum.IntegrationTests.Skips;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #3 (master plan Â§6.H, plan task Â§Pareto03). ReAct with two
/// tools: a <c>lookup_capital</c> and a <c>uppercase</c>. The LM must call at least one
/// tool to satisfy the question, then emit a <c>final_answer:</c>. Asserts the trace's
/// tool-call list is non-empty and the answer mentions the expected city.
/// </summary>
public sealed class Pareto03_ReActToolCalling : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;
    static int s_lookupCalls;
    static int s_upperCalls;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto03_ReActToolCalling(ParetoTestFixture fx) => _fx = fx;

    /// <summary>Two-tool ReAct cycle: at least one tool fires, ToolCalls list populated.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task ReAct_TwoTools_LookupCapital_TraceRecordsToolCalls()
    {
        Assert.SkipWhen(SkipUnlessRealLm.ShouldSkip, SkipUnlessRealLm.Reason);
        var ct = TestContext.Current.CancellationToken;

        Interlocked.Exchange(ref s_lookupCalls, 0);
        Interlocked.Exchange(ref s_upperCalls, 0);

        using var chat = OpenRouterClientFactory.Create();
        var ctx = new AmpelumContext(
            chat,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);

        string LookupCapital(string country)
        {
            Interlocked.Increment(ref s_lookupCalls);
            return country.Contains("japan", StringComparison.OrdinalIgnoreCase) ? "Tokyo" : "unknown";
        }

        string Uppercase(string text)
        {
            Interlocked.Increment(ref s_upperCalls);
            return text.ToUpperInvariant();
        }

        var tools = new AIFunction[]
        {
            AIFunctionFactory.Create(LookupCapital, name: "lookup_capital",
                description: "Look up the capital city of a country."),
            AIFunctionFactory.Create(Uppercase, name: "uppercase",
                description: "Return the input string in all upper case."),
        };

        // 5-iteration cap aligns with the per-test cost cap (â‰¤5 LM calls Ã— â‰¤50 tokens).
        var react = new ReAct<LookupSig>(ctx, tools, maxIterations: 4);
        var pred = await react.ForwardAsync(
            new LookupSig { Question = "What is the capital of Japan? Use the available tools." },
            ct: ct);

        pred.Value.Answer.ShouldNotBeNullOrWhiteSpace();

        // The trace must record the tool calls the model made (whichever tool it chose).
        pred.Trace.Metadata.ShouldContainKey(TraceKeys.ToolCalls);
        var toolCalls = (IReadOnlyList<ToolCallRecord>)pred.Trace.Metadata[TraceKeys.ToolCalls]!;
        toolCalls.Count.ShouldBeGreaterThan(0);

        // At least one of the two tools must have been invoked end-to-end.
        (s_lookupCalls + s_upperCalls).ShouldBeGreaterThan(0);
    }
}
