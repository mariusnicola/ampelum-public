using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Safety.Policy;

/// <summary>A single content-policy rule: regex pattern + action.</summary>
/// <param name="RuleId">Identifier surfaced in metadata + exceptions.</param>
/// <param name="Pattern">Compiled regex evaluated against each string-typed output value.</param>
/// <param name="OnMatch">Action taken when the pattern matches.</param>
/// <param name="RedactionToken">Replacement token for <see cref="OutputRuleAction.Redact"/>; ignored otherwise.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record OutputRule(
    string RuleId,
    Regex Pattern,
    OutputRuleAction OnMatch,
    string? RedactionToken = null);
