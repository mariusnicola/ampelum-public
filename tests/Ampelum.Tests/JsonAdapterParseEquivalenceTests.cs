using SignalGrid.Ampelum;
using Ampelum.Tests.Internal;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests;

[Signature("equiv")]
public sealed record EquivJsonSig : ISignature
{
    [Input]  public required string Text { get; init; }
    [Output] public string Summary { get; init; } = "";
    [Output] public string Sentiment { get; init; } = "";
    [Output] public int    WordCount { get; init; }
}

public sealed class JsonAdapterParseEquivalenceTests
{
    public static TheoryData<int, bool> CorpusIndices
    {
        get
        {
            var data = new TheoryData<int, bool>();
            // Strict mode handles indices % 6 != 2 (the prose-wrapped variant); lenient handles all six.
            for (var i = 0; i < DeterministicFuzzer.JsonCorpus().Count; i++)
            {
                if (i % 6 != 2) data.Add(i, /*strict=*/true);
                data.Add(i, /*strict=*/false);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CorpusIndices))]
    public void ParsedOutput_IsStableAcrossCorpus(int idx, bool strict)
    {
        var adapter = new JsonAdapter(strict);
        var meta = SignatureMetadataCache.For<EquivJsonSig>();
        var input = DeterministicFuzzer.JsonCorpus()[idx];
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
