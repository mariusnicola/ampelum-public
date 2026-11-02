using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

#pragma warning disable AMPELUM_V09_PREVIEW

namespace SignalGrid.Ampelum.Safety.Policy;

/// <summary>Fluent builder for <see cref="ContentPolicy"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ContentPolicyBuilder
{
    readonly List<OutputRule> _rules = new();

    /// <summary>Adds a Reject rule.</summary>
    public ContentPolicyBuilder Reject(string ruleId, Regex pattern)
    {
        _rules.Add(new OutputRule(ruleId, pattern, OutputRuleAction.Reject));
        return this;
    }

    /// <summary>Adds a Redact rule with the given replacement token.</summary>
    public ContentPolicyBuilder Redact(string ruleId, Regex pattern, string token)
    {
        _rules.Add(new OutputRule(ruleId, pattern, OutputRuleAction.Redact, token));
        return this;
    }

    /// <summary>Adds a Warn rule.</summary>
    public ContentPolicyBuilder Warn(string ruleId, Regex pattern)
    {
        _rules.Add(new OutputRule(ruleId, pattern, OutputRuleAction.Warn));
        return this;
    }

    /// <summary>Builds an immutable snapshot.</summary>
    public ContentPolicy Build() => new(_rules.ToArray());
}
