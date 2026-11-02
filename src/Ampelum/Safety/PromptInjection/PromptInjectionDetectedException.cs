using System;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.PromptInjection;

/// <summary>Thrown by <see cref="PromptInjectionHook"/> when an input is flagged.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PromptInjectionDetectedException : InvalidOperationException
{
    /// <summary>Builds the exception around a detector verdict.</summary>
    public PromptInjectionDetectedException(string ruleId, string? excerpt)
        : base($"Prompt-injection rule '{ruleId}' matched: {excerpt ?? "<no excerpt>"}.")
    {
        RuleId = ruleId;
        Excerpt = excerpt;
    }

    /// <summary>The rule id that produced the verdict.</summary>
    public string RuleId { get; }

    /// <summary>The matched excerpt (may be null for rules that flag without a span).</summary>
    public string? Excerpt { get; }
}
