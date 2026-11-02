using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.SpeakerSelectors;

public class RoleBasedSelectorTests
{
    sealed class StubAgent(string name, string role) : IAgent
    {
        public string Name { get; } = name;
        public string Role { get; } = role;
        public ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, System.Threading.CancellationToken ct) =>
            ValueTask.FromResult(new AgentMessage(Name, Role, ""));
    }

    static IReadOnlyList<IAgent> Agents() =>
    [
        new StubAgent("p1", "planner"),
        new StubAgent("c1", "coder"),
        new StubAgent("k1", "critic"),
    ];

    [Fact]
    public async Task Cycles_through_role_sequence()
    {
        var sel = new RoleBasedSelector(["planner", "coder", "critic"]);
        var agents = Agents();
        for (var i = 0; i < 6; i++)
        {
            var ctx = new ConversationContext([], memory: null, turnIndex: i);
            var pick = await sel.SelectAsync(ctx, agents, TestContext.Current.CancellationToken);
            pick.Role.ShouldBe(new[] { "planner", "coder", "critic" }[i % 3]);
        }
    }

    [Fact]
    public async Task Missing_role_throws_invalid_operation()
    {
        var sel = new RoleBasedSelector(["planner", "coder", "critic"]);
        var agents = new IAgent[] { new StubAgent("p1", "planner"), new StubAgent("c1", "coder") };
        var ctx = new ConversationContext([], memory: null, turnIndex: 2);
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sel.SelectAsync(ctx, agents, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("critic");
    }

    [Fact]
    public void Empty_role_sequence_throws_argument_exception()
    {
        Should.Throw<ArgumentException>(() => new RoleBasedSelector([]));
    }

    [Fact]
    public void Null_roles_throws_argument_null()
    {
        Should.Throw<ArgumentNullException>(() => new RoleBasedSelector(null!));
    }
}
