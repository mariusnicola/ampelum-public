using Shouldly;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace SignalGrid.Ampelum.AgentRunner.Tests;

public sealed class AgentRunnerTests
{
    [Fact]
    public void Create_HasEmptyList()
    {
        using var runner = new AgentRunner(() => new FakeChatClient());
        runner.ListAll().ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_ReturnsId()
    {
        var fake = new FakeChatClient();
        fake.EnqueueText("TASK_COMPLETE done");
        using var runner = new AgentRunner(() => fake);
        var id = await runner.StartAsync("test-proj", "do something");
        id.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task StartAndStop_ChangesStatus()
    {
        var fake = new FakeChatClient();
        fake.EnqueueStreamingChunks("working...");
        using var runner = new AgentRunner(() => fake);
        await runner.StartAsync("proj-a", "work");
        await runner.StopAsync("proj-a");
        var status = runner.GetStatus("proj-a");
        status.ShouldBeNull(); // removed on completion
    }

    [Fact]
    public async Task MaxConcurrent_Throws()
    {
        var fake = new FakeChatClient();
        fake.EnqueueStreamingChunks("slow...", "slow...", "slow...", "slow...", "slow...", "slow...");
        using var runner = new AgentRunner(() => fake);
        for (int i = 0; i < 5; i++)
            await runner.StartAsync($"proj-{i}", "work");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            runner.StartAsync("proj-over", "nope"));
    }

    [Fact]
    public void ListAll_ReturnsSessions()
    {
        var fake = new FakeChatClient();
        fake.EnqueueText("TASK_COMPLETE fast");
        using var runner = new AgentRunner(() => fake);
        runner.ListAll().Count.ShouldBe(0);
    }
}
