using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests;

public sealed class PredictStreamDefaultDemosTests
{
    [Signature("Q&A")]
    public sealed record Sig : ISignature
    {
        [Input] public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
    }

    static Example Demo(string question, string answer) => Example.From(new Sig { Q = question, A = answer });

    static async Task<string> StreamedPromptAsync(IReadOnlyList<Example>? defaultDemos, IReadOnlyList<Example>? passed)
    {
        var chat = new FakeChatClient().EnqueueStreamingChunks("[[ ## A ## ]]\n", "ok");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            DefaultDemos = defaultDemos,
        };

        await foreach (var _ in new Predict<Sig>(ctx).StreamAsync(new Sig { Q = "hi" }, passed, TestContext.Current.CancellationToken)) { }

        return string.Join("\n", chat.RecordedCalls.Single().Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task StreamAsync_SendsTheContextsDefaultDemos_WhenTheCallerPassesNone()
    {
        var sent = await StreamedPromptAsync([Demo("DEFAULT-DEMO-QUESTION-81ac", "default answer")], null);

        sent.ShouldContain("DEFAULT-DEMO-QUESTION-81ac");
    }

    [Fact]
    public async Task StreamAsync_SendsTheContextsDefaultDemos_WhenTheCallerPassesAnEmptyList()
    {
        var sent = await StreamedPromptAsync([Demo("DEFAULT-DEMO-QUESTION-81ac", "default answer")], []);

        sent.ShouldContain("DEFAULT-DEMO-QUESTION-81ac");
    }

    [Fact]
    public async Task StreamAsync_SendsThePassedDemosInPlaceOfTheDefaults()
    {
        var sent = await StreamedPromptAsync(
            [Demo("DEFAULT-DEMO-QUESTION-81ac", "default answer")],
            [Demo("PASSED-DEMO-QUESTION-3f7d", "passed answer")]);

        sent.ShouldContain("PASSED-DEMO-QUESTION-3f7d");
        sent.ShouldNotContain("DEFAULT-DEMO-QUESTION-81ac");
    }

    [Fact]
    public async Task StreamAsync_SendsNoDemos_WhenNeitherTheCallerNorTheContextHasAny()
    {
        var sent = await StreamedPromptAsync(null, null);

        sent.ShouldNotContain("DEMO-QUESTION");
    }
}
