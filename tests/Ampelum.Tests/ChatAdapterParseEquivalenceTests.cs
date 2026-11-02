using SignalGrid.Ampelum;
using Ampelum.Tests.Internal;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests;

[Signature("equiv")]
public sealed record EquivChatSig : ISignature
{
    [Input]  public required string Text { get; init; }
    [Output] public string Summary { get; init; } = "";
    [Output] public string Sentiment { get; init; } = "";
    [Output] public int    WordCount { get; init; }
}

public sealed class ChatAdapterParseEquivalenceTests
{
    public static TheoryData<int> CorpusIndices
    {
        get
        {
            var data = new TheoryData<int>();
            for (var i = 0; i < DeterministicFuzzer.ChatCorpus().Count; i++) data.Add(i);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CorpusIndices))]
    public void ParsedOutput_IsStableAcrossCorpus(int idx)
    {
        var adapter = new ChatAdapter();
        var meta = SignatureMetadataCache.For<EquivChatSig>();
        var input = DeterministicFuzzer.ChatCorpus()[idx];
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, input));

        var dict = adapter.Parse(meta, resp);
        dict.ShouldContainKey("Summary");
        dict.ShouldContainKey("Sentiment");
        dict.ShouldContainKey("WordCount");
        dict["Summary"].ShouldBeOfType<string>();
        dict["Sentiment"].ShouldBeOfType<string>();
        dict["WordCount"].ShouldBeOfType<int>();
    }
}
