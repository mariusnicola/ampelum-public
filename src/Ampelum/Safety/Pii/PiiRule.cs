using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Safety.Pii;

/// <summary>A single redaction rule: regex pattern + replacement token + category tag.</summary>
/// <param name="Category">The category this rule classifies matches under.</param>
/// <param name="Pattern">Compiled regex pattern that locates candidate PII substrings.</param>
/// <param name="ReplacementToken">Literal string substituted for each matched (and validated) substring.</param>
/// <param name="PostMatchValidator">Optional secondary validator (e.g. Luhn for credit cards) â€” if set and returns false the match is left unchanged.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PiiRule(
    PiiCategory Category,
    Regex Pattern,
    string ReplacementToken,
    Func<Match, bool>? PostMatchValidator = null);
