using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Caching;
using Xunit;

namespace Ampelum.Tests.Caching;

public sealed class TokenCacheKeyTests
{
    static readonly ChatMessage[] Messages = [new(ChatRole.User, "Opening hours?")];

    static string Key(ChatOptions? options) => TokenCache.BuildKey(Messages, options);

    [Fact]
    public void No_options_and_empty_options_share_a_key() =>
        Key(null).ShouldBe(Key(new ChatOptions()));

    [Fact]
    public void Identical_options_share_a_key() =>
        Key(new ChatOptions { Temperature = 0.2f, ModelId = "m" })
            .ShouldBe(Key(new ChatOptions { Temperature = 0.2f, ModelId = "m" }));

    public static TheoryData<string, ChatOptions> Changing => new()
    {
        { "ModelId", new ChatOptions { ModelId = "other" } },
        { "Temperature", new ChatOptions { Temperature = 0.9f } },
        { "TopP", new ChatOptions { TopP = 0.5f } },
        { "TopK", new ChatOptions { TopK = 5 } },
        { "MaxOutputTokens", new ChatOptions { MaxOutputTokens = 10 } },
        { "FrequencyPenalty", new ChatOptions { FrequencyPenalty = 0.5f } },
        { "PresencePenalty", new ChatOptions { PresencePenalty = 0.5f } },
        { "Seed", new ChatOptions { Seed = 7 } },
        { "StopSequences", new ChatOptions { StopSequences = ["END"] } },
        { "ResponseFormat", new ChatOptions { ResponseFormat = ChatResponseFormat.Json } },
        { "ToolMode", new ChatOptions { ToolMode = ChatToolMode.RequireAny } },
        { "Instructions", new ChatOptions { Instructions = "Answer in French." } },
        { "ConversationId", new ChatOptions { ConversationId = "c1" } },
        { "Tools", new ChatOptions { Tools = [AIFunctionFactory.Create(() => "x", "lookup", "Look up.")] } },
    };

    [Theory]
    [MemberData(nameof(Changing))]
    public void An_option_that_changes_the_output_changes_the_key(string name, ChatOptions options)
    {
        name.ShouldNotBeNullOrEmpty();
        Key(options).ShouldNotBe(Key(null));
    }

    [Fact]
    public void Two_different_tools_give_different_keys()
    {
        var a = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "x", "lookup", "Look up.")] };
        var b = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "x", "delete", "Delete.")] };

        Key(a).ShouldNotBe(Key(b));
    }

    [Fact]
    public void Stop_sequences_in_a_different_order_give_different_keys() =>
        Key(new ChatOptions { StopSequences = ["A", "B"] }).ShouldNotBe(Key(new ChatOptions { StopSequences = ["B", "A"] }));
}
