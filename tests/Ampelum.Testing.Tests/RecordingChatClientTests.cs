using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Testing.Tests;

public sealed class RecordingChatClientTests
{
    [Fact]
    public async Task FirstCall_DelegatesAndRecords()
    {
        var inner = new FakeChatClient().EnqueueText("hello from inner");
        var recordPath = Path.Combine(Path.GetTempPath(), $"record-test-{Guid.NewGuid()}.json");
        try
        {
            var client = new RecordingChatClient(inner, recordPath);
            var messages = new[] { new ChatMessage(ChatRole.User, "test") };
            var response = await client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);
            response.Text.ShouldBe("hello from inner");
            File.Exists(recordPath).ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(recordPath)) File.Delete(recordPath);
        }
    }

    [Fact]
    public async Task SecondCall_ReplaysWithoutDelegating()
    {
        var inner = new FakeChatClient().EnqueueText("first call");
        var recordPath = Path.Combine(Path.GetTempPath(), $"record-replay-{Guid.NewGuid()}.json");
        try
        {
            // First session records
            var client = new RecordingChatClient(inner, recordPath);
            await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "call 1") }, cancellationToken: TestContext.Current.CancellationToken);

            // New session loads from disk — replays without delegating
            var replayClient = new RecordingChatClient(new FakeChatClient(), recordPath);
            var result = await replayClient.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "call 2") }, cancellationToken: TestContext.Current.CancellationToken);
            result.Text.ShouldBe("first call");
        }
        finally
        {
            if (File.Exists(recordPath)) File.Delete(recordPath);
        }
    }

    [Fact]
    public async Task MultipleCalls_RecordsAll()
    {
        var inner = new FakeChatClient();
        inner.EnqueueText("response A");
        inner.EnqueueText("response B");
        var recordPath = Path.Combine(Path.GetTempPath(), $"record-multi-{Guid.NewGuid()}.json");
        try
        {
            var client = new RecordingChatClient(inner, recordPath);
            await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "msg 1") }, cancellationToken: TestContext.Current.CancellationToken);
            await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "msg 2") }, cancellationToken: TestContext.Current.CancellationToken);

            var json = await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken);
            json.ShouldContain("response A");
            json.ShouldContain("response B");
        }
        finally
        {
            if (File.Exists(recordPath)) File.Delete(recordPath);
        }
    }
}
