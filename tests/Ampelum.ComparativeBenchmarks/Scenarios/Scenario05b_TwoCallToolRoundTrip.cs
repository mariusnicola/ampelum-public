using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using SignalGrid.Ampelum.Testing;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S5 follow-up (alpha.6) — full two-call LM round-trip.
///
/// Plan name: Scenario05b_TwoCallToolRoundTrip.
///
/// <para>Removes the alpha.5 scope reduction by scripting a deterministic
/// two-call sequence on a <see cref="FakeChatClient"/>: response 1 emits
/// a <see cref="FunctionCallContent"/> tool call, response 2 emits the
/// final-answer text. Driven through <see cref="ReAct{TSig}"/> end-to-end:
/// system+input → LM call → tool dispatch → tool result feed → LM call →
/// parse final answer. This is the production loop a Ampelum user gets.</para>
///
/// <para>SK row: a hand-written equivalent using <see cref="IChatClient"/>
/// directly so we measure the chat-API round-trip on the same primitive
/// (no <c>AutoInvokeKernelFunctions</c> fragility per advisor §S5). The SK
/// comparison is therefore "what a careful SK user would write at the same
/// abstraction level". Pinned SK 1.74.0 ships <c>Microsoft.SemanticKernel.ChatCompletion</c>
/// connectors but they assume a real provider; rather than wire that against
/// our mock, the SK row dispatches through MEAI directly with the same
/// scripted FakeChatClient.</para>
///
/// <para>Floor row: <c>RawTwoCalls</c> — the two MEAI calls + manual tool
/// dispatch with no orchestration framework at all.</para>
/// </summary>
[MemoryDiagnoser]
public class Scenario05b_TwoCallToolRoundTrip
{
    static readonly Microsoft.Extensions.AI.AIFunction _addFn =
        AIFunctionFactory.Create((int a, int b) => a + b, "add", "Adds two integers.");

    QaSig _input = null!;

    [GlobalSetup]
    public void Setup()
    {
        _input = new QaSig { Question = "What is 2 plus 3?" };
    }

    /// <summary>Builds a fresh FakeChatClient with two enqueued responses: tool_call then final.</summary>
    static FakeChatClient BuildScriptedClient()
    {
        var client = new FakeChatClient();
        // Response 1: function call to "add(a=2,b=3)".
        var toolCall = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call_1", "add",
                new Dictionary<string, object?> { ["a"] = 2, ["b"] = 3 })]);
        client.EnqueueRaw(new ChatResponse(toolCall));
        // Response 2: final-answer text matching ReAct's expected marker.
        client.EnqueueText("Reasoning: 2 + 3 = 5.\nfinal_answer: 5");
        return client;
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<QaSig>> Ampelum_ReAct()
    {
        var fake = BuildScriptedClient();
        var ctx = new AmpelumContext(
            fake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        var react = new ReAct<QaSig>(ctx, [_addFn]);
        return await react.ForwardAsync(_input);
    }

    /// <summary>
    /// SK-equivalent: a hand-written two-call dispatch using the same MEAI primitive
    /// (the abstraction level SK's user-facing <c>ChatCompletionAgent</c> ultimately
    /// calls into). Measures dispatch + tool-loop overhead at the same plumbing tier.
    /// </summary>
    [Benchmark]
    public async Task<string> Sk_TwoCallEquivalent()
    {
        var fake = BuildScriptedClient();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Use tools when needed."),
            new(ChatRole.User, _input.Question),
        };
        var options = new ChatOptions { Tools = [_addFn], ToolMode = ChatToolMode.Auto };

        var first = await fake.GetResponseAsync(messages, options);
        messages.Add(first.Messages[^1]);
        foreach (var fcc in first.Messages[^1].Contents.OfType<FunctionCallContent>())
        {
            var fnArgs = new Microsoft.Extensions.AI.AIFunctionArguments(fcc.Arguments ?? new Dictionary<string, object?>());
            var result = await _addFn.InvokeAsync(fnArgs);
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(fcc.CallId, result)]));
        }

        var forced = options.Clone();
        forced.Tools = null;
        forced.ToolMode = null;
        var second = await fake.GetResponseAsync(messages, forced);
        return second.Text ?? "";
    }

    /// <summary>Floor: just the two LM calls + manual delegate dispatch, no abstractions.</summary>
    [Benchmark]
    public async Task<string> RawTwoCalls()
    {
        var fake = BuildScriptedClient();
        var messages = new List<ChatMessage> { new(ChatRole.User, _input.Question) };

        var r1 = await fake.GetResponseAsync(messages);
        // Hand-pick the tool call shape we know the script enqueued.
        var fcc = r1.Messages[^1].Contents.OfType<FunctionCallContent>().First();
        var args = fcc.Arguments!;
        // Inline what the function does — we know it's `add(int,int)`.
        var sum = Convert.ToInt32(args["a"]) + Convert.ToInt32(args["b"]);
        messages.Add(new ChatMessage(ChatRole.Assistant, $"tool result: {sum}"));
        var r2 = await fake.GetResponseAsync(messages);
        return r2.Text ?? "";
    }
}
