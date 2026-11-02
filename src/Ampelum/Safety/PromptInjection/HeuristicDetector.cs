using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Safety.PromptInjection;

/// <summary>Pattern-based first-pass detector. Ships five frozen rules covering common
/// prompt-injection vectors observed in production red-team corpora as of v0.9c.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class HeuristicDetector : IPromptInjectionDetector
{
    /// <summary>Frozen built-in rule set (RuleId, Pattern). Returned as a fresh array per read.</summary>
    public static IReadOnlyList<(string RuleId, Regex Pattern)> DefaultRules => new[]
    {
        ("IGNORE_PRIOR",      new Regex(@"ignore (the )?(prior|previous|all|above) instructions?",
                                  RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("OVERRIDE_SYSTEM",   new Regex(@"(disregard|override|bypass) (the )?system( prompt)?",
                                  RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("REVEAL_PROMPT",     new Regex(@"(show|reveal|repeat|print|leak) (me )?(the )?(system )?prompt",
                                  RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("STRUCTURAL_MARKER", new Regex(@"\[\[\s*##\s*\w+\s*##\s*\]\]",
                                  RegexOptions.Compiled)),
        ("JAILBREAK_DAN",     new Regex(@"\b(DAN|do anything now)\b",
                                  RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    readonly IReadOnlyList<(string RuleId, Regex Pattern)> _rules;

    /// <summary>Builds a detector with the given rules, or the default set when null.</summary>
    public HeuristicDetector(IReadOnlyList<(string RuleId, Regex Pattern)>? rules = null)
        => _rules = rules is null ? DefaultRules : rules.ToArray();

    /// <inheritdoc/>
    public PromptInjectionResult Scan(string text)
    {
        foreach (var (ruleId, pattern) in _rules)
        {
            var m = pattern.Match(text);
            if (m.Success)
                return new PromptInjectionResult(Flagged: true, RuleId: ruleId, Excerpt: m.Value);
        }
        return new PromptInjectionResult(Flagged: false, RuleId: null, Excerpt: null);
    }
}
