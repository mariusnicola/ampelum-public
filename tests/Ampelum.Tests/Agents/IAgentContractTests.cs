using System;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents;

public class IAgentContractTests
{
    sealed class TestEchoAgent(string name = "echo", string role = "echo-role") : IAgent
    {
        public string Name { get; } = name;
        public string Role { get; } = role;

        public ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastContent = ctx.History.Count > 0 ? ctx.History[^1].Content : "";
            return ValueTask.FromResult(new AgentMessage(Name, Role, $"echo:{lastContent}"));
        }
    }

    [Fact]
    public async Task ReplyAsync_returns_message_with_agent_name()
    {
        IAgent agent = new TestEchoAgent("alice", "planner");
        var ctx = new ConversationContext([new AgentMessage("user", "user", "ping")], memory: null, turnIndex: 0);
        var msg = await agent.ReplyAsync(ctx, TestContext.Current.CancellationToken);
        msg.SenderName.ShouldBe("alice");
        msg.Role.ShouldBe("planner");
        msg.Content.ShouldBe("echo:ping");
        agent.Name.ShouldNotBeNull();
        agent.Role.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReplyAsync_honors_already_cancelled_token()
    {
        IAgent agent = new TestEchoAgent();
        var ctx = new ConversationContext([], memory: null, turnIndex: 0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await agent.ReplyAsync(ctx, cts.Token));
    }
}
