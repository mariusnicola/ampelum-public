#pragma warning disable MEAI001
using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Caching;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Caching;

/// <summary>Requests that differ in anything the model sees must not share a key; requests the key cannot represent are not cached.</summary>
public sealed class TokenCacheCollisionTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly ChatMessage[] Hi = [new(ChatRole.User, "hi")];

    static string Key(IEnumerable<ChatMessage> messages, ChatOptions? options = null) => TokenCache.BuildKey(messages, options);

    [Fact]
    public void Tools_with_the_same_name_and_description_but_different_parameters_differ()
    {
        var a = new ChatOptions { Tools = [AIFunctionFactory.Create((int x) => x, "calc", "does math")] };
        var b = new ChatOptions { Tools = [AIFunctionFactory.Create((string x, string y) => x + y, "calc", "does math")] };

        Key(Hi, a).ShouldNotBe(Key(Hi, b));
    }

    [Fact]
    public void Messages_with_the_same_text_but_different_data_content_differ()
    {
        var a = new ChatMessage(ChatRole.User, [new TextContent("what is this"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        var b = new ChatMessage(ChatRole.User, [new TextContent("what is this"), new DataContent(new byte[] { 9, 9, 9 }, "image/png")]);

        Key([a]).ShouldNotBe(Key([b]));
    }

    [Fact]
    public void Different_function_results_differ()
    {
        var a = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "result A")]);
        var b = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "result B")]);

        Key([a]).ShouldNotBe(Key([b]));
    }

    [Fact]
    public void Different_function_calls_differ()
    {
        var a = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup", new Dictionary<string, object?> { ["q"] = "a" })]);
        var b = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup", new Dictionary<string, object?> { ["q"] = "b" })]);

        Key([a]).ShouldNotBe(Key([b]));
    }

    [Fact]
    public void Different_author_names_differ() =>
        Key([new ChatMessage(ChatRole.User, "hi") { AuthorName = "alice" }])
            .ShouldNotBe(Key([new ChatMessage(ChatRole.User, "hi") { AuthorName = "bob" }]));

    [Fact]
    public void Different_message_additional_properties_differ() =>
        Key([new ChatMessage(ChatRole.User, "hi") { AdditionalProperties = new() { ["k"] = "1" } }])
            .ShouldNotBe(Key([new ChatMessage(ChatRole.User, "hi") { AdditionalProperties = new() { ["k"] = "2" } }]));

    [Fact]
    public void Different_option_additional_properties_differ() =>
        Key(Hi, new ChatOptions { AdditionalProperties = new() { ["reasoning_effort"] = "low" } })
            .ShouldNotBe(Key(Hi, new ChatOptions { AdditionalProperties = new() { ["reasoning_effort"] = "high" } }));

    [Fact]
    public void Different_reasoning_options_differ() =>
        Key(Hi, new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } })
            .ShouldNotBe(Key(Hi, new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } }));

    [Fact]
    public void Equal_requests_built_separately_share_a_key()
    {
        ChatMessage[] Make() => [new ChatMessage(ChatRole.User, [new TextContent("t"), new DataContent(new byte[] { 1 }, "image/png")]) { AuthorName = "a" }];
        ChatOptions Opt() => new()
        {
            Temperature = 0.3f,
            Tools = [AIFunctionFactory.Create((int x) => x, "calc", "does math")],
            AdditionalProperties = new() { ["k"] = "v" },
        };

        Key(Make(), Opt()).ShouldBe(Key(Make(), Opt()));
    }

    static async Task<(int InnerCalls, ChatResponse First, ChatResponse Second)> TwiceAsync(ChatOptions? options, FakeChatClient? chat = null)
    {
        var inner = chat ?? new FakeChatClient()
            .EnqueueText("one", new UsageDetails { InputTokenCount = 50, OutputTokenCount = 5 })
            .EnqueueText("two", new UsageDetails { InputTokenCount = 50, OutputTokenCount = 5 });
        using var cache = new TokenCache();
        var sut = new TokenCachedChatClient(inner, cache);
        var first = await sut.GetResponseAsync(Hi, options, Ct);
        var second = await sut.GetResponseAsync(Hi, options, Ct);
        return (inner.RecordedCalls.Count, first, second);
    }

    [Fact]
    public async Task A_request_with_a_raw_representation_factory_is_not_cached()
    {
        var (calls, _, second) = await TwiceAsync(new ChatOptions { RawRepresentationFactory = _ => new object() });

        calls.ShouldBe(2);
        second.Text.ShouldBe("two");
    }

    [Fact]
    public async Task A_request_with_a_continuation_token_is_not_cached()
    {
        var (calls, _, _) = await TwiceAsync(new ChatOptions { ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1 }) });

        calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_request_that_allows_background_responses_is_not_cached()
    {
        var (calls, _, _) = await TwiceAsync(new ChatOptions { AllowBackgroundResponses = true });

        calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_request_that_disallows_background_responses_is_cached()
    {
        var (calls, _, _) = await TwiceAsync(new ChatOptions { AllowBackgroundResponses = false });

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_hit_is_a_copy_not_the_stored_instance()
    {
        var (_, first, second) = await TwiceAsync(null);

        second.ShouldNotBeSameAs(first);
        second.Text.ShouldBe("one");
        second.Messages[0].ShouldNotBeSameAs(first.Messages[0]);
    }

    [Fact]
    public async Task Changing_a_returned_response_does_not_change_what_the_cache_serves()
    {
        var inner = new FakeChatClient().EnqueueText("one").EnqueueText("two");
        using var cache = new TokenCache();
        var sut = new TokenCachedChatClient(inner, cache);

        var first = await sut.GetResponseAsync(Hi, cancellationToken: Ct);
        first.Messages[0].Contents.Clear();
        first.Messages.Clear();
        var second = await sut.GetResponseAsync(Hi, cancellationToken: Ct);
        second.Messages.Clear();
        var third = await sut.GetResponseAsync(Hi, cancellationToken: Ct);

        third.Text.ShouldBe("one");
    }

    [Fact]
    public async Task Mutating_the_text_of_a_returned_hit_does_not_change_the_next_hit()
    {
        var inner = new FakeChatClient().EnqueueText("one").EnqueueText("unused");
        using var cache = new TokenCache();
        var sut = new TokenCachedChatClient(inner, cache);

        await sut.GetResponseAsync(Hi, cancellationToken: Ct);
        var hit = await sut.GetResponseAsync(Hi, cancellationToken: Ct);
        ((TextContent)hit.Messages[0].Contents[0]).Text = "MUTATED";
        var next = await sut.GetResponseAsync(Hi, cancellationToken: Ct);

        next.Text.ShouldBe("one");
    }

    [Fact]
    public async Task Mutating_the_text_of_a_miss_does_not_change_what_the_cache_serves()
    {
        var inner = new FakeChatClient().EnqueueText("one").EnqueueText("unused");
        using var cache = new TokenCache();
        var sut = new TokenCachedChatClient(inner, cache);

        var miss = await sut.GetResponseAsync(Hi, cancellationToken: Ct);
        ((TextContent)miss.Messages[0].Contents[0]).Text = "MUTATED";
        var hit = await sut.GetResponseAsync(Hi, cancellationToken: Ct);

        hit.Text.ShouldBe("one");
    }

    [Fact]
    public async Task A_miss_does_not_share_its_usage_with_the_cache()
    {
        var inner = new FakeChatClient().EnqueueText("one", new UsageDetails { InputTokenCount = 50, OutputTokenCount = 5 });
        using var cache = new TokenCache();
        var sut = new TokenCachedChatClient(inner, cache);

        var miss = await sut.GetResponseAsync(Hi, cancellationToken: Ct);
        var stored = cache.TryGet(TokenCache.BuildKey(Hi, null))!;

        stored.Usage.ShouldNotBeSameAs(miss.Usage);
        miss.Usage!.InputTokenCount = 999;
        cache.TryGet(TokenCache.BuildKey(Hi, null))!.Usage!.InputTokenCount.ShouldBe(50);
    }

    [Fact]
    public async Task The_first_call_keeps_its_usage_and_a_hit_reports_zero_tokens()
    {
        var (_, first, second) = await TwiceAsync(null);

        first.Usage!.InputTokenCount.ShouldBe(50);
        first.Usage.OutputTokenCount.ShouldBe(5);
        second.Usage.ShouldNotBeNull();
        second.Usage.InputTokenCount.ShouldBe(0);
        second.Usage.OutputTokenCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_hit_keeps_the_rest_of_the_response()
    {
        var (_, first, second) = await TwiceAsync(null);

        second.ModelId.ShouldBe(first.ModelId);
        second.FinishReason.ShouldBe(first.FinishReason);
        second.ResponseId.ShouldBe(first.ResponseId);
        second.Messages.Count.ShouldBe(first.Messages.Count);
    }
}
