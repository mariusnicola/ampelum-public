using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Safety.Pii;

/// <summary>Default <see cref="IPiiRedactor"/> backed by a frozen list of regex rules
/// (email / phone / SSN / credit-card by default â€” see <see cref="DefaultRules"/>).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RegexPiiRedactor : IPiiRedactor
{
    static readonly Regex EmailRegex = new(
        @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex PhoneRegex = new(
        @"(\+?\d{1,3}[\s.-]?)?(\(?\d{3}\)?[\s.-]?)\d{3}[\s.-]?\d{4}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex SsnRegex = new(
        @"\b\d{3}-\d{2}-\d{4}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex CreditCardRegex = new(
        @"\b(?:\d[ -]?){13,19}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Frozen built-in rule set. Returned as a fresh array on every read.</summary>
    public static IReadOnlyList<PiiRule> DefaultRules => new[]
    {
        new PiiRule(PiiCategory.Email, EmailRegex, "<REDACTED:EMAIL>"),
        new PiiRule(PiiCategory.Phone, PhoneRegex, "<REDACTED:PHONE>"),
        new PiiRule(PiiCategory.Ssn, SsnRegex, "<REDACTED:SSN>"),
        new PiiRule(PiiCategory.CreditCard, CreditCardRegex, "<REDACTED:CC>", LuhnValid),
    };

    readonly IReadOnlyList<PiiRule> _rules;

    /// <summary>Builds a redactor with the given rules. Pass <c>null</c> for the built-in defaults.</summary>
    public RegexPiiRedactor(IReadOnlyList<PiiRule>? rules = null)
    {
        _rules = rules is null ? DefaultRules : rules.ToArray();
    }

    /// <inheritdoc/>
    public string Redact(string text)
    {
        var current = text;
        foreach (var rule in _rules)
        {
            current = rule.Pattern.Replace(current, m =>
                rule.PostMatchValidator is null || rule.PostMatchValidator(m)
                    ? rule.ReplacementToken
                    : m.Value);
        }
        return current;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> RedactDictionary(IReadOnlyDictionary<string, object?> source)
    {
        var result = new Dictionary<string, object?>(source.Count, System.StringComparer.Ordinal);
        foreach (var kv in source)
        {
            result[kv.Key] = kv.Value is string s ? Redact(s) : kv.Value;
        }
        return result;
    }

    static bool LuhnValid(Match match)
    {
        int sum = 0;
        bool alt = false;
        for (int i = match.Length - 1; i >= 0; i--)
        {
            char c = match.Value[i];
            if (c is ' ' or '-') continue;
            if (c is < '0' or > '9') return false;
            int n = c - '0';
            if (alt) { n *= 2; if (n > 9) n -= 9; }
            sum += n;
            alt = !alt;
        }
        return sum % 10 == 0;
    }
}
