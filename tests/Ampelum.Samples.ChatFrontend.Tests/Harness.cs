using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Moderation;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.ChatFrontend.Tests;

/// <summary>A backend over two scripted models. Ids are plain labels so a test can tell the endpoints apart.</summary>
sealed class Harness : IDisposable
{
    public const string PrimaryId = "test-primary";
    public const string FallbackId = "test-fallback";

    public FakeChatClient Primary { get; } = new();
    public FakeChatClient Fallback { get; } = new();
    public ChatBackend Backend { get; }

    public Harness(IEnumerable<IHook>? hooks = null, IChatClient? primary = null)
    {
        Backend = new ChatBackend(primary ?? Primary, PrimaryId, Fallback, FallbackId, hooks);
    }

    public static ReplySignature Reply(string answer, string followUp = "Anything else?") =>
        new() { Question = "", Answer = answer, FollowUp = followUp };

    public void Dispose() => Backend.Dispose();
}

/// <summary>A primary model that is down for every call, streaming included.</summary>
sealed class DownChatClient : IChatClient
{
    public int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new HttpRequestException("primary is down");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        await Task.Yield();
        if (Calls > 0)
            throw new HttpRequestException("primary is down");
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Flags any text that contains the marker and counts how often it was asked.</summary>
sealed class MarkerModerator : IOutputModerator
{
    public const string Marker = "UNSAFE-TEXT";

    public int Calls { get; private set; }

    public ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult(text.Contains(Marker, StringComparison.Ordinal)
            ? new ModerationResult(true, ["unsafe"], 1.0)
            : new ModerationResult(false, [], 0.0));
    }
}

/// <summary>Records what the after-pass of the hook pipeline can see.</summary>
sealed class AfterSpyHook(Func<int> deliveredSoFar) : IHook
{
    public int Order => 100;

    public int AfterRuns { get; private set; }
    public bool? OutputsWereNull { get; private set; }
    public int DeliveredWhenAfterRan { get; private set; } = -1;

    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;

    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
    {
        AfterRuns++;
        OutputsWereNull = ctx.Outputs is null;
        DeliveredWhenAfterRan = deliveredSoFar();
        return default;
    }

    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
}
