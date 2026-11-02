using System.Collections.Generic;
using System.Text.Json;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents;

public class AgentMessageTests
{
    [Fact]
    public void Default_metadata_is_empty()
    {
        var msg = new AgentMessage("alice", "planner", "hi");
        msg.SenderName.ShouldBe("alice");
        msg.Role.ShouldBe("planner");
        msg.Content.ShouldBe("hi");
        msg.Handoff.ShouldBeNull();
        msg.IsTerminal.ShouldBeFalse();
        msg.Metadata.ShouldNotBeNull();
        msg.Metadata.Count.ShouldBe(0);
    }

    [Fact]
    public void Value_equality_holds_for_same_data()
    {
        var ts = new System.DateTimeOffset(2026, 4, 25, 0, 0, 0, System.TimeSpan.Zero);
        var meta = (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["k"] = "v" };
        var a = new AgentMessage("a", "r", "x") { Timestamp = ts, Metadata = meta };
        var b = new AgentMessage("a", "r", "x") { Timestamp = ts, Metadata = meta };
        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void With_expression_produces_immutable_copy()
    {
        var msg = new AgentMessage("a", "r", "x");
        var msg2 = msg with { Content = "y" };
        msg.Content.ShouldBe("x");
        msg2.Content.ShouldBe("y");
    }

    [Fact]
    public void Handoff_and_terminal_round_trip()
    {
        var msg = new AgentMessage("a", "r", "x") { Handoff = "b", IsTerminal = true };
        msg.Handoff.ShouldBe("b");
        msg.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void Json_round_trip_preserves_fields()
    {
        var ts = new System.DateTimeOffset(2026, 4, 25, 12, 0, 0, System.TimeSpan.Zero);
        var meta = new Dictionary<string, string> { ["key"] = "v" };
        var msg = new AgentMessage("a", "planner", "hello") { Handoff = "b", IsTerminal = true, Timestamp = ts, Metadata = meta };
        var json = JsonSerializer.Serialize(msg);
        var clone = JsonSerializer.Deserialize<AgentMessage>(json)!;
        clone.SenderName.ShouldBe("a");
        clone.Role.ShouldBe("planner");
        clone.Content.ShouldBe("hello");
        clone.Handoff.ShouldBe("b");
        clone.IsTerminal.ShouldBeTrue();
        clone.Timestamp.ShouldBe(ts);
        clone.Metadata["key"].ShouldBe("v");
    }
}
