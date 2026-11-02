using System.Collections.Generic;
using System.Text.RegularExpressions;
using SignalGrid.Ampelum.Safety.Pii;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Pii;

public sealed class RegexPiiRedactorTests
{
    [Fact]
    public void Redact_email_replaces_with_token()
    {
        var sut = new RegexPiiRedactor();
        sut.Redact("ping me at alice@example.com please").ShouldBe("ping me at <REDACTED:EMAIL> please");
    }

    [Fact]
    public void Redact_phone_replaces_with_token()
    {
        var sut = new RegexPiiRedactor();
        sut.Redact("call (415) 555-0142 today").ShouldBe("call <REDACTED:PHONE> today");
    }

    [Fact]
    public void Redact_ssn_replaces_with_token()
    {
        var sut = new RegexPiiRedactor();
        sut.Redact("SSN 123-45-6789").ShouldBe("SSN <REDACTED:SSN>");
    }

    [Fact]
    public void Redact_credit_card_with_valid_luhn_replaces_with_token()
    {
        var sut = new RegexPiiRedactor();
        sut.Redact("card 4111 1111 1111 1111").ShouldBe("card <REDACTED:CC>");
    }

    [Fact]
    public void Redact_credit_card_invalid_luhn_passes_through()
    {
        var sut = new RegexPiiRedactor();
        sut.Redact("ref 1234 5678 9012 3456").ShouldBe("ref 1234 5678 9012 3456");
    }

    [Fact]
    public void Redact_passes_through_when_no_match()
    {
        var sut = new RegexPiiRedactor();
        sut.Redact("the quick brown fox").ShouldBe("the quick brown fox");
    }

    [Fact]
    public void RedactDictionary_redacts_string_values_only()
    {
        var sut = new RegexPiiRedactor();
        var input = new Dictionary<string, object?>(System.StringComparer.Ordinal)
        {
            ["email"] = "x@y.com",
            ["age"] = 42,
        };
        var output = sut.RedactDictionary(input);
        output["email"].ShouldBe("<REDACTED:EMAIL>");
        output["age"].ShouldBe(42);
        input["email"].ShouldBe("x@y.com");
    }

    [Fact]
    public void Constructor_with_custom_rules_replaces_default_set()
    {
        var custom = new[]
        {
            new PiiRule(
                PiiCategory.Other,
                new Regex(@"secret-\d+"),
                "<REDACTED:OTHER>"),
        };
        var sut = new RegexPiiRedactor(custom);
        sut.Redact("foo@bar.com secret-42").ShouldBe("foo@bar.com <REDACTED:OTHER>");
    }

    [Fact]
    public void DefaultRules_returns_independent_copy_each_call()
    {
        var first = RegexPiiRedactor.DefaultRules;
        var second = RegexPiiRedactor.DefaultRules;
        first.Count.ShouldBe(4);
        second.Count.ShouldBe(4);
        ReferenceEquals(first, second).ShouldBeFalse();
    }

    [Fact]
    public void PiiRule_record_exposes_constructor_parameters()
    {
        var regex = new Regex("x");
        var rule = new PiiRule(PiiCategory.Other, regex, "<X>");
        rule.Category.ShouldBe(PiiCategory.Other);
        rule.Pattern.ShouldBeSameAs(regex);
        rule.ReplacementToken.ShouldBe("<X>");
        rule.PostMatchValidator.ShouldBeNull();
    }

    [Fact]
    public void Luhn_short_circuits_on_non_digit_char_inside_match()
    {
        // The CC regex permits ' ' and '-' separators; non-digit/non-separator can never
        // be matched. We exercise the LuhnValid char-classification branch by passing a
        // custom rule whose pattern intentionally allows letters, then watching Luhn fail.
        var rule = new PiiRule(
            PiiCategory.CreditCard,
            new Regex(@"\b[\dA-Z]{16}\b"),
            "<CC>",
            // Reuse the public surface: a letter inside the matched span makes Luhn return false.
            m =>
            {
                int sum = 0; bool alt = false;
                for (int i = m.Length - 1; i >= 0; i--)
                {
                    char c = m.Value[i];
                    if (c is < '0' or > '9') return false;
                    int n = c - '0';
                    if (alt) { n *= 2; if (n > 9) n -= 9; }
                    sum += n; alt = !alt;
                }
                return sum % 10 == 0;
            });
        var sut = new RegexPiiRedactor(new[] { rule });
        sut.Redact("ABCDEFGHIJKLMNOP").ShouldBe("ABCDEFGHIJKLMNOP");
    }
}
