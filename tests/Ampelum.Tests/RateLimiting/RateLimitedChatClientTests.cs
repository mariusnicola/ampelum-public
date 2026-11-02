using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.RateLimiting;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum;

namespace Ampelum.Tests.RateLimiting;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RateLimitedChatClientTests
{
    static IList<ChatMessage> Msgs() => [new ChatMessage(ChatRole.User, "hi")];

    [Fact]
    public void Ctor_NullInner_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new RateLimitedChatClient(null!, new RateLimitingOptions()));
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var inner = new FakeChatClient();
        Should.Throw<ArgumentNullException>(() => new RateLimitedChatClient(inner, null!));
    }

    [Fact]
    public void Ctor_NonPositiveRpm_Throws()
    {
        var inner = new FakeChatClient();
        Should.Throw<ArgumentOutOfRangeException>(() => new RateLimitedChatClient(inner, new RateLimitingOptions { RequestsPerMinute = 0 }));
    }

    [Fact]
    public void Ctor_NonPositiveBurst_Throws()
    {
        var inner = new FakeChatClient();
        Should.Throw<ArgumentOutOfRangeException>(() => new RateLimitedChatClient(inner, new RateLimitingOptions { BurstMultiplier = 0 }));
    }

    [Fact]
    public async Task GetResponseAsync_WithinBudget_Forwards()
    {
        var inner = new FakeChatClient().EnqueueText("hello");
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions { RequestsPerMinute = 60, BurstMultiplier = 5 });
        var ct = TestContext.Current.CancellationToken;

        var resp = await rl.GetResponseAsync(Msgs(), null, ct);

        resp.Text.ShouldBe("hello");
    }

    [Fact]
    public async Task GetResponseAsync_UsesBudgetScopePartition()
    {
        var inner = new FakeChatClient().EnqueueText("ok");
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions { RequestsPerMinute = 60 });
        var opts = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { [HookMetadataKeys.BudgetScope] = "tenant-A" } };
        var ct = TestContext.Current.CancellationToken;

        var resp = await rl.GetResponseAsync(Msgs(), opts, ct);

        resp.Text.ShouldBe("ok");
    }

    [Fact]
    public async Task GetResponseAsync_FallsBackToRouterTargetName()
    {
        var inner = new FakeChatClient().EnqueueText("ok");
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions { RequestsPerMinute = 60 });
        var opts = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { [HookMetadataKeys.RouterTargetName] = "openai" } };
        var ct = TestContext.Current.CancellationToken;

        var resp = await rl.GetResponseAsync(Msgs(), opts, ct);

        resp.Text.ShouldBe("ok");
    }

    [Fact]
    public async Task GetResponseAsync_EmptyScope_FallsBackThroughChain()
    {
        var inner = new FakeChatClient().EnqueueText("ok");
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions { RequestsPerMinute = 60 });
        var opts = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [HookMetadataKeys.BudgetScope] = "",
                [HookMetadataKeys.RouterTargetName] = "",
            },
        };
        var ct = TestContext.Current.CancellationToken;

        (await rl.GetResponseAsync(Msgs(), opts, ct)).Text.ShouldBe("ok");
    }

    [Fact]
    public async Task GetResponseAsync_NoOptions_UsesDefaultPartition()
    {
        var inner = new FakeChatClient().EnqueueText("ok");
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions { RequestsPerMinute = 60 });
        (await rl.GetResponseAsync(Msgs(), null, TestContext.Current.CancellationToken)).Text.ShouldBe("ok");
    }

    [Fact]
    public async Task GetResponseAsync_BucketEmpty_ThrowsRateLimited()
    {
        // RPM=1, burst=1 â‡’ bucket size 1. Acquire timeout 50ms ensures fast failure.
        var inner = new FakeChatClient();
        inner.EnqueueText("a");
        inner.EnqueueText("b");
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions
        {
            RequestsPerMinute = 1,
            BurstMultiplier = 1,
            AcquireTimeout = TimeSpan.FromMilliseconds(50),
        });
        var ct = TestContext.Current.CancellationToken;

        // First permits â€” drain the bucket.
        await rl.GetResponseAsync(Msgs(), null, ct);
        // Second within the same window must fail (queue limit + tight timeout).
        var ex = await Should.ThrowAsync<RateLimitedException>(() => rl.GetResponseAsync(Msgs(), null, ct));
        ex.PartitionKey.ShouldBe("default");
        ex.WaitTime.ShouldBe(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task GetResponseAsync_OuterCtCancelled_PropagatesOce()
    {
        var inner = new FakeChatClient().EnqueueText("ok");
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions { RequestsPerMinute = 60 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => rl.GetResponseAsync(Msgs(), null, cts.Token));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_Forwards()
    {
        var inner = new FakeChatClient().EnqueueStreamingChunks("hi", " there");
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions { RequestsPerMinute = 60 });
        var ct = TestContext.Current.CancellationToken;

        var chunks = new List<string>();
        await foreach (var u in rl.GetStreamingResponseAsync(Msgs(), null, ct))
            chunks.Add(u.Text ?? "");

        string.Concat(chunks).ShouldBe("hi there");
    }

    [Fact]
    public void GetService_ReturnsMetadata()
    {
        var inner = new FakeChatClient();
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions());
        var meta = rl.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;
        meta.ShouldNotBeNull();
        meta!.ProviderName.ShouldBe(nameof(RateLimitedChatClient));
    }

    [Fact]
    public void GetService_ReturnsSelfWhenAssignable()
    {
        var inner = new FakeChatClient();
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions());
        rl.GetService(typeof(IChatClient)).ShouldBe(rl);
    }

    [Fact]
    public void GetService_DelegatesToInnerForUnknown()
    {
        var inner = new FakeChatClient();
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions());
        rl.GetService(typeof(string)).ShouldBeNull();
    }

    [Fact]
    public void GetService_NullType_Throws()
    {
        var inner = new FakeChatClient();
        using var rl = new RateLimitedChatClient(inner, new RateLimitingOptions());
        Should.Throw<ArgumentNullException>(() => rl.GetService(null!));
    }

    [Fact]
    public void Options_RecordEqualityAndDefaults()
    {
        var a = new RateLimitingOptions();
        var b = new RateLimitingOptions();
        a.ShouldBe(b);
        a.RequestsPerMinute.ShouldBe(60);
        a.BurstMultiplier.ShouldBe(5.0);
        a.AcquireTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }
}

