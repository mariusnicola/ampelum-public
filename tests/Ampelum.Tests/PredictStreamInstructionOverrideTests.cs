using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests;

public sealed class PredictStreamInstructionOverrideTests
{
    [Signature("ORIGINAL-SIGNATURE-INSTRUCTION-5d20")]
    public sealed record Sig : ISignature
    {
        [Input] public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
    }

    const string Override = "OVERRIDE-INSTRUCTION-b81f";

    static async Task<string> StreamedPromptAsync(IReadOnlyDictionary<string, string>? overrides, string? submodule)
    {
        var chat = new FakeChatClient().EnqueueStreamingChunks("[[ ## A ## ]]\n", "ok");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            InstructionOverrides = overrides,
            SubmoduleName = submodule,
        };

        await foreach (var _ in new Predict<Sig>(ctx).StreamAsync(new Sig { Q = "hi" }, ct: TestContext.Current.CancellationToken)) { }

        return string.Join("\n", chat.RecordedCalls.Single().Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task StreamAsync_SendsTheOverrideKeyedByTheSubmoduleName_InPlaceOfTheSignatureInstruction()
    {
        var sent = await StreamedPromptAsync(new Dictionary<string, string> { ["default"] = Override }, "default");

        sent.ShouldContain(Override);
        sent.ShouldNotContain("ORIGINAL-SIGNATURE-INSTRUCTION-5d20");
    }

    [Fact]
    public async Task StreamAsync_KeepsTheSignatureInstruction_WhenTheOverrideIsKeyedByAnotherName()
    {
        var sent = await StreamedPromptAsync(new Dictionary<string, string> { ["other"] = Override }, "default");

        sent.ShouldContain("ORIGINAL-SIGNATURE-INSTRUCTION-5d20");
        sent.ShouldNotContain(Override);
    }

    [Fact]
    public async Task StreamAsync_KeepsTheSignatureInstruction_WhenTheOverrideIsBlank()
    {
        var sent = await StreamedPromptAsync(new Dictionary<string, string> { ["default"] = "  " }, "default");

        sent.ShouldContain("ORIGINAL-SIGNATURE-INSTRUCTION-5d20");
    }

    [Fact]
    public async Task StreamAsync_KeepsTheSignatureInstruction_WhenTheContextHasNoSubmoduleName()
    {
        var sent = await StreamedPromptAsync(new Dictionary<string, string> { ["default"] = Override }, null);

        sent.ShouldContain("ORIGINAL-SIGNATURE-INSTRUCTION-5d20");
        sent.ShouldNotContain(Override);
    }
}
