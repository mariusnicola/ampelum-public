using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.PromptInjection;

/// <summary>The verdict from a single <see cref="IPromptInjectionDetector.Scan(string)"/> call.</summary>
/// <param name="Flagged">True when a rule matched.</param>
/// <param name="RuleId">Identifier of the matched rule (null when not flagged).</param>
/// <param name="Excerpt">The matched substring (null when not flagged).</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptInjectionResult(
    bool Flagged,
    string? RuleId,
    string? Excerpt);
