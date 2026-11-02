using System.Collections.Generic;
using System.Text.RegularExpressions;
using SignalGrid.Ampelum.Safety.Moderation;
using SignalGrid.Ampelum.Safety.PromptInjection;
using SignalGrid.Ampelum.Safety.Tools;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety;

// Coverage closures for exception/record types â€” exercise every property + record member
// (Equals/GetHashCode/ToString/Deconstruct/with) so [Ampelum]* coverage gate stays green.
public sealed class SafetyExceptionRecordTests
{
    [Fact]
    public void ForbiddenToolException_exposes_all_properties()
    {
        var ex = new ForbiddenToolException("t", "1", "denied");
        ex.ToolName.ShouldBe("t");
        ex.ToolVersion.ShouldBe("1");
        ex.Reason.ShouldBe("denied");
        ex.Message.ShouldContain("denied");
    }

    [Fact]
    public void PromptInjectionDetectedException_exposes_all_properties_with_null_excerpt()
    {
        var ex = new PromptInjectionDetectedException("RID", null);
        ex.RuleId.ShouldBe("RID");
        ex.Excerpt.ShouldBeNull();
        ex.Message.ShouldContain("RID");
    }

    [Fact]
    public void PromptInjectionDetectedException_exposes_all_properties_with_excerpt()
    {
        var ex = new PromptInjectionDetectedException("RID", "match");
        ex.Excerpt.ShouldBe("match");
        ex.Message.ShouldContain("match");
    }

    [Fact]
    public void ModerationException_exposes_categories()
    {
        var ex = new ModerationException(new[] { "hate", "violence" });
        ex.Categories.ShouldBe(new[] { "hate", "violence" });
        ex.Message.ShouldContain("hate");
    }

    [Fact]
    public void ToolRegistration_record_round_trip()
    {
        var meta = new Dictionary<string, object?> { ["k"] = "v" };
        var reg = new ToolRegistration("n", "1", ToolCategory.Computation, "desc", meta);
        reg.Name.ShouldBe("n");
        reg.Version.ShouldBe("1");
        reg.Category.ShouldBe(ToolCategory.Computation);
        reg.Description.ShouldBe("desc");
        reg.Metadata.ShouldBe(meta);

        var (n, v, c, d, m) = reg;
        n.ShouldBe("n"); v.ShouldBe("1"); c.ShouldBe(ToolCategory.Computation); d.ShouldBe("desc"); m.ShouldBe(meta);

        var copy = reg with { Description = "other" };
        copy.Description.ShouldBe("other");
        reg.Equals(copy).ShouldBeFalse();
        reg.GetHashCode().ShouldNotBe(0);
        reg.ToString().ShouldContain("ToolRegistration");
    }

    [Fact]
    public void ToolGatePolicy_record_round_trip()
    {
        var p = new ToolGatePolicy(
            Allow: new HashSet<string> { "a" },
            Deny: new HashSet<string> { "b" },
            AllowCategories: new HashSet<ToolCategory> { ToolCategory.Information },
            DenyCategories: new HashSet<ToolCategory> { ToolCategory.SystemControl });
        p.Allow!.ShouldContain("a");
        p.Deny!.ShouldContain("b");
        p.AllowCategories!.ShouldContain(ToolCategory.Information);
        p.DenyCategories!.ShouldContain(ToolCategory.SystemControl);
        p.ToString().ShouldContain("ToolGatePolicy");
    }

    [Fact]
    public void OutputRule_record_round_trip()
    {
        var rule = new SignalGrid.Ampelum.Safety.Policy.OutputRule(
            "R", new Regex("x"),
            SignalGrid.Ampelum.Safety.Policy.OutputRuleAction.Redact, "<X>");
        rule.RuleId.ShouldBe("R");
        rule.OnMatch.ShouldBe(SignalGrid.Ampelum.Safety.Policy.OutputRuleAction.Redact);
        rule.RedactionToken.ShouldBe("<X>");
        var copy = rule with { RuleId = "R2" };
        copy.RuleId.ShouldBe("R2");
        rule.ToString().ShouldContain("OutputRule");
    }

    [Fact]
    public void PromptInjectionResult_record_round_trip()
    {
        var r = new PromptInjectionResult(true, "RID", "match");
        var copy = r with { RuleId = "Other" };
        copy.RuleId.ShouldBe("Other");
        r.Equals(copy).ShouldBeFalse();
        r.ToString().ShouldContain("PromptInjectionResult");
    }

    [Fact]
    public void ModerationResult_record_round_trip()
    {
        var r = new ModerationResult(true, new[] { "x" }, 0.5);
        var copy = r with { Confidence = 0.9 };
        copy.Confidence.ShouldBe(0.9);
        r.ToString().ShouldContain("ModerationResult");
    }
}
