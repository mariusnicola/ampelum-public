using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents;

public class GroupChatTests
{
    sealed class ScriptedAgent(string name, string role, IReadOnlyList<AgentMessage> script) : IAgent
    {
        int _i;
        public string Name { get; } = name;
        public string Role { get; } = role;
        public ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var msg = script[_i++ % script.Count];
            return ValueTask.FromResult(msg with { SenderName = Name, Role = Role });
        }
    }

    [Fact]
    public async Task Stops_when_terminal_flag_set()
    {
        var a = new ScriptedAgent("a", "ra",
        [
            new AgentMessage("a", "ra", "1"),
            new AgentMessage("a", "ra", "3") { IsTerminal = true },
        ]);
        var b = new ScriptedAgent("b", "rb",
        [
            new AgentMessage("b", "rb", "2"),
            new AgentMessage("b", "rb", "4"),
        ]);
        var chat = new GroupChat([a, b], new RoundRobinSelector());
        var result = await chat.RunAsync("kickoff", maxTurns: 20, TestContext.Current.CancellationToken);
        result.Reason.ShouldBe(TerminationReason.TerminalMessage);
        // history = system seed + a("1") + b("2") + a("3" terminal) = 4
        result.History.Count.ShouldBe(4);
        result.History[0].SenderName.ShouldBe("system");
        result.History[1].Content.ShouldBe("1");
        result.History[2].Content.ShouldBe("2");
        result.History[3].Content.ShouldBe("3");
        result.History[3].IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public async Task Returns_after_max_turns_when_no_terminal()
    {
        var a = new ScriptedAgent("a", "ra", [new AgentMessage("a", "ra", "x")]);
        var b = new ScriptedAgent("b", "rb", [new AgentMessage("b", "rb", "y")]);
        var chat = new GroupChat([a, b], new RoundRobinSelector());
        var result = await chat.RunAsync("k", maxTurns: 2, TestContext.Current.CancellationToken);
        result.Reason.ShouldBe(TerminationReason.MaxTurnsReached);
        // system seed + 2 turns
        result.History.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Max_turns_zero_throws()
    {
        var a = new ScriptedAgent("a", "ra", [new AgentMessage("a", "ra", "x")]);
        var chat = new GroupChat([a], new RoundRobinSelector());
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await chat.RunAsync("k", maxTurns: 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Empty_agents_throws()
    {
        Should.Throw<ArgumentException>(() => new GroupChat([], new RoundRobinSelector()));
    }

    [Fact]
    public void Duplicate_agent_names_throws()
    {
        var a = new ScriptedAgent("a", "ra", [new AgentMessage("a", "ra", "x")]);
        var a2 = new ScriptedAgent("a", "rb", [new AgentMessage("a", "rb", "y")]);
        Should.Throw<ArgumentException>(() => new GroupChat([a, a2], new RoundRobinSelector()));
    }

    [Fact]
    public async Task Selector_receives_correct_turn_index()
    {
        var seenIndices = new List<int>();
        var spySel = new SpySelector(seenIndices);
        var a = new ScriptedAgent("a", "ra",
        [
            new AgentMessage("a", "ra", "1"),
            new AgentMessage("a", "ra", "2") { IsTerminal = true },
        ]);
        var chat = new GroupChat([a], spySel);
        await chat.RunAsync("k", maxTurns: 5, TestContext.Current.CancellationToken);
        seenIndices.ShouldBe([0, 1]);
    }

    [Fact]
    public async Task Handoff_overrides_round_robin_pick()
    {
        // alpha returns msg with Handoff="gamma"; round-robin would otherwise pick beta next.
        var alpha = new ScriptedAgent("alpha", "ra",
        [
            new AgentMessage("alpha", "ra", "1") { Handoff = "gamma" },
        ]);
        var beta = new ScriptedAgent("beta", "rb",
        [
            new AgentMessage("beta", "rb", "should-not-speak") { IsTerminal = true },
        ]);
        var gamma = new ScriptedAgent("gamma", "rc",
        [
            new AgentMessage("gamma", "rc", "3") { IsTerminal = true },
        ]);
        var chat = new GroupChat([alpha, beta, gamma], new RoundRobinSelector());
        var result = await chat.RunAsync("k", maxTurns: 5, TestContext.Current.CancellationToken);
        result.Reason.ShouldBe(TerminationReason.TerminalMessage);
        // system + alpha + gamma (NOT beta)
        result.History.Count.ShouldBe(3);
        result.History[1].SenderName.ShouldBe("alpha");
        result.History[2].SenderName.ShouldBe("gamma");
    }

    [Fact]
    public async Task Handoff_to_unknown_terminates()
    {
        var alpha = new ScriptedAgent("alpha", "ra",
        [
            new AgentMessage("alpha", "ra", "1") { Handoff = "ghost" },
        ]);
        var beta = new ScriptedAgent("beta", "rb",
        [
            new AgentMessage("beta", "rb", "x"),
        ]);
        var chat = new GroupChat([alpha, beta], new RoundRobinSelector());
        var result = await chat.RunAsync("k", maxTurns: 5, TestContext.Current.CancellationToken);
        result.Reason.ShouldBe(TerminationReason.HandoffToUnknown);
        result.History.Count.ShouldBe(2); // system + alpha
    }

    sealed class SpySelector(List<int> indices) : ISpeakerSelector
    {
        public ValueTask<IAgent> SelectAsync(ConversationContext ctx, IReadOnlyList<IAgent> agents, CancellationToken ct)
        {
            indices.Add(ctx.TurnIndex);
            return ValueTask.FromResult(agents[0]);
        }
    }

    // alpha.5 (Gap 3): GroupChat audit-sink integration coverage.

    [Fact]
    public async Task AuditSink_NullByDefault_NoEntriesEmitted()
    {
        var a = new ScriptedAgent("a", "r", [new AgentMessage("a", "r", "1") { IsTerminal = true }]);
        var sink = new SignalGrid.Ampelum.Audit.InMemoryAuditSink();
        // 3-arg ctor (back-compat) wires no audit sink.
        var chat = new GroupChat([a], new RoundRobinSelector(), memory: null);
        await chat.RunAsync("kick", maxTurns: 4, TestContext.Current.CancellationToken);
        sink.NextSequence.ShouldBe(0L);
    }

    [Fact]
    public async Task AuditSink_Provided_EmitsOneRowPerSpeakerSelection()
    {
        var a = new ScriptedAgent("a", "r", [new AgentMessage("a", "r", "1") { Handoff = "b" }]);
        var b = new ScriptedAgent("b", "r", [new AgentMessage("b", "r", "2") { IsTerminal = true }]);
        var sink = new SignalGrid.Ampelum.Audit.InMemoryAuditSink();
        var chat = new GroupChat([a, b], new RoundRobinSelector(), memory: null, auditSink: sink);
        await chat.RunAsync("kick", maxTurns: 4, TestContext.Current.CancellationToken);

        var entries = new List<SignalGrid.Ampelum.Audit.AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entries.Add(e);
        entries.Count.ShouldBe(2);
        entries[0].EventType.ShouldBe("agent.handoff");
        entries[0].Inputs["agent.handoff.kind"].ShouldBe("selector");
        entries[0].Inputs["agent.handoff.source"].ShouldBe("system");
        entries[0].Inputs["agent.handoff.target"].ShouldBe("a");
        entries[0].Inputs["agent.handoff.turn"].ShouldBe(0L);
        entries[1].Inputs["agent.handoff.kind"].ShouldBe("explicit");
        entries[1].Inputs["agent.handoff.source"].ShouldBe("a");
        entries[1].Inputs["agent.handoff.target"].ShouldBe("b");
        entries[1].Outputs["agent.conversation.length"].ShouldBe(2L);
        entries[1].PrevHash.ShouldBe(entries[0].EntryHash);
    }

    [Fact]
    public async Task AuditSink_HandoffToUnknown_StillEmitsHandoffRowsForCompletedTurns()
    {
        var alpha = new ScriptedAgent("alpha", "r", [new AgentMessage("alpha", "r", "1") { Handoff = "ghost" }]);
        var beta = new ScriptedAgent("beta", "r", [new AgentMessage("beta", "r", "x")]);
        var sink = new SignalGrid.Ampelum.Audit.InMemoryAuditSink();
        var chat = new GroupChat([alpha, beta], new RoundRobinSelector(), memory: null, auditSink: sink);
        var result = await chat.RunAsync("k", maxTurns: 5, TestContext.Current.CancellationToken);
        result.Reason.ShouldBe(TerminationReason.HandoffToUnknown);
        // One handoff row written before the unknown-target check.
        sink.NextSequence.ShouldBe(1L);
    }
}
