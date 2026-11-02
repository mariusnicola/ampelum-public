using System.Text.RegularExpressions;
using SignalGrid.Ampelum.Safety.Policy;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Policy;

public sealed class ContentPolicyBuilderTests
{
    [Fact]
    public void Builder_adds_three_rule_types()
    {
        var policy = new ContentPolicyBuilder()
            .Reject("R1", new Regex("a"))
            .Redact("R2", new Regex("b"), "X")
            .Warn("R3", new Regex("c"))
            .Build();
        policy.Rules.Count.ShouldBe(3);
        policy.Rules[0].OnMatch.ShouldBe(OutputRuleAction.Reject);
        policy.Rules[1].RedactionToken.ShouldBe("X");
        policy.Rules[2].OnMatch.ShouldBe(OutputRuleAction.Warn);
    }

    [Fact]
    public void Empty_has_no_rules() => ContentPolicy.Empty.Rules.Count.ShouldBe(0);

    [Fact]
    public void Build_returns_independent_snapshot()
    {
        var b = new ContentPolicyBuilder().Reject("X", new Regex("z"));
        var p1 = b.Build();
        b.Warn("Y", new Regex("q"));
        var p2 = b.Build();
        p1.Rules.Count.ShouldBe(1);
        p2.Rules.Count.ShouldBe(2);
    }
}
