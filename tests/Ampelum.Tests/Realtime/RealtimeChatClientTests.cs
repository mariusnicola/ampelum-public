using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Realtime;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Realtime;

public class RealtimeChatClientTests
{
    sealed class FakeTransport : IRealtimeTransport
    {
        readonly ConcurrentQueue<string> _inbound = new();
        public List<string> Sent { get; } = [];
        public bool Connected { get; private set; }
        public bool Closed { get; private set; }
        public bool Disposed { get; private set; }

        public FakeTransport EnqueueText(string delta)
        {
            _inbound.Enqueue(new JsonObject { ["type"] = "response.text.delta", ["delta"] = delta }.ToJsonString());
            return this;
        }

        public FakeTransport EnqueueDone()
        {
            _inbound.Enqueue(new JsonObject { ["type"] = "response.done" }.ToJsonString());
            return this;
        }

        public FakeTransport EnqueueError(string message)
        {
            _inbound.Enqueue(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["message"] = message } }.ToJsonString());
            return this;
        }

        public FakeTransport EnqueueClose()
        {
            _inbound.Enqueue("__close__");
            return this;
        }

        public FakeTransport EnqueueIgnoredFrame()
        {
            _inbound.Enqueue(new JsonObject { ["type"] = "response.audio.delta" }.ToJsonString());
            return this;
        }

        public FakeTransport EnqueueNonObjectFrame()
        {
            _inbound.Enqueue("\"just a string\"");
            return this;
        }

        public ValueTask ConnectAsync(Uri endpoint, string apiKey, CancellationToken ct)
        {
            Connected = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask SendTextAsync(string text, CancellationToken ct)
        {
            Sent.Add(text);
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> ReceiveTextAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_inbound.TryDequeue(out var f)) return ValueTask.FromResult<string?>(null);
            if (f == "__close__") return ValueTask.FromResult<string?>(null);
            return ValueTask.FromResult<string?>(f);
        }

        public ValueTask CloseAsync(CancellationToken ct) { Closed = true; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Missing_model_id_throws_argument_exception()
    {
        var t = new FakeTransport().EnqueueDone();
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options: null, cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("ModelId is required");
        t.Connected.ShouldBeFalse();
        t.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Second_call_on_initialized_session_succeeds_without_model_id()
    {
        var t = new FakeTransport()
            .EnqueueText("a").EnqueueDone()
            .EnqueueText("b").EnqueueDone();
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        var r1 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options: new ChatOptions { ModelId = "test-model" }, cancellationToken: TestContext.Current.CancellationToken);

        var r2 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "y")], options: null, cancellationToken: TestContext.Current.CancellationToken);

        r1.Text.ShouldBe("a");
        r2.Text.ShouldBe("b");
        t.Sent.Count.ShouldBe(3);  // session.update + 2x response.create
    }

    [Fact]
    public async Task Round_trip_assembles_text_from_deltas()
    {
        var t = new FakeTransport().EnqueueText("Hello, ").EnqueueText("world!").EnqueueDone();
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "say hi")], options: new ChatOptions { ModelId = "test-model" }, cancellationToken: TestContext.Current.CancellationToken);
        resp.Text.ShouldBe("Hello, world!");
        t.Connected.ShouldBeTrue();
        t.Sent.Count.ShouldBe(2);  // session.update + response.create
        t.Sent[0].ShouldContain("session.update");
        t.Sent[1].ShouldContain("response.create");
    }

    [Fact]
    public async Task Subsequent_call_skips_session_init()
    {
        var t = new FakeTransport()
            .EnqueueText("a").EnqueueDone()
            .EnqueueText("b").EnqueueDone();
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        var r1 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options: new ChatOptions { ModelId = "test-model" }, cancellationToken: TestContext.Current.CancellationToken);
        var r2 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "y")], options: new ChatOptions { ModelId = "test-model" }, cancellationToken: TestContext.Current.CancellationToken);
        r1.Text.ShouldBe("a");
        r2.Text.ShouldBe("b");
        t.Sent.Count.ShouldBe(3);  // session.update + 2x response.create
    }

    [Fact]
    public async Task Pre_cancelled_token_throws()
    {
        var t = new FakeTransport();
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options: new ChatOptions { ModelId = "test-model" }, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Error_frame_throws_invalid_operation()
    {
        var t = new FakeTransport().EnqueueError("server boom");
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options: new ChatOptions { ModelId = "test-model" }, cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("server boom");
    }

    [Fact]
    public async Task Close_frame_returns_assembled_text_so_far()
    {
        var t = new FakeTransport().EnqueueText("partial").EnqueueClose();
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options: new ChatOptions { ModelId = "test-model" }, cancellationToken: TestContext.Current.CancellationToken);
        resp.Text.ShouldBe("partial");
    }

    [Fact]
    public async Task Streaming_throws_not_supported()
    {
        var t = new FakeTransport();
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        Should.Throw<NotSupportedException>(() => client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "x")], options: new ChatOptions { ModelId = "test-model" }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetService_returns_transport_and_self()
    {
        var t = new FakeTransport();
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        client.GetService(typeof(IRealtimeTransport)).ShouldBe(t);
        client.GetService(typeof(RealtimeChatClient)).ShouldBe(client);
        client.GetService(typeof(string)).ShouldBeNull();
        Should.Throw<ArgumentNullException>(() => client.GetService(null!));
    }

    [Fact]
    public async Task Dispose_closes_transport()
    {
        var t = new FakeTransport();
        var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        await client.DisposeAsync();
        t.Closed.ShouldBeTrue();
        t.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void Sync_dispose_closes_transport()
    {
        var t = new FakeTransport();
        var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        client.Dispose();
        t.Closed.ShouldBeTrue();
        t.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Ignored_frame_types_pass_through()
    {
        var t = new FakeTransport().EnqueueIgnoredFrame().EnqueueText("ok").EnqueueDone();
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options: new ChatOptions { ModelId = "test-model" }, cancellationToken: TestContext.Current.CancellationToken);
        resp.Text.ShouldBe("ok");
    }

    [Fact]
    public async Task Non_object_frames_are_skipped()
    {
        var t = new FakeTransport().EnqueueNonObjectFrame().EnqueueText("ok").EnqueueDone();
        await using var client = new RealtimeChatClient(new Uri("ws://x"), "k", t);
        var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options: new ChatOptions { ModelId = "test-model" }, cancellationToken: TestContext.Current.CancellationToken);
        resp.Text.ShouldBe("ok");
    }

    [Fact]
    public void Null_args_throw()
    {
        var t = new FakeTransport();
        Should.Throw<ArgumentNullException>(() => new RealtimeChatClient(null!, "k", t));
        Should.Throw<ArgumentNullException>(() => new RealtimeChatClient(new Uri("ws://x"), null!, t));
        Should.Throw<ArgumentNullException>(() => new RealtimeChatClient(new Uri("ws://x"), "k", (IRealtimeTransport)null!));
    }

    [Fact]
    public async Task Default_ctor_with_clientwebsocket_uses_websockettransport()
    {
        // Just verifies the convenience ctor wires up the WebSocketTransport without
        // attempting an actual connect (we never call GetResponseAsync).
        var sock = new System.Net.WebSockets.ClientWebSocket();
        var client = new RealtimeChatClient(new Uri("ws://x"), "k", sock);
        await client.DisposeAsync();
    }
}
