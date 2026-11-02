using System;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Policy;

/// <summary>Thrown when a <see cref="OutputRuleAction.Reject"/> rule matches.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ContentPolicyViolationException : Exception
{
    /// <summary>Builds the exception around the violated rule.</summary>
    public ContentPolicyViolationException(string ruleId, string excerpt)
        : base($"Content-policy rule '{ruleId}' rejected output: {excerpt}.")
    {
        RuleId = ruleId;
        Excerpt = excerpt;
    }

    /// <summary>The rule id that produced the rejection.</summary>
    public string RuleId { get; }

    /// <summary>The matched excerpt.</summary>
    public string Excerpt { get; }
}
