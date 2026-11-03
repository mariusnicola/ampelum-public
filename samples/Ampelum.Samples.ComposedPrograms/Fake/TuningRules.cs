using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ampelum.Samples.ComposedPrograms.Fake;

/// <summary>Scripted answers for the tuner that proposes instructions and for the model that judges notes.</summary>
public static partial class TuningRules
{
    [GeneratedRegex(@"## Expected outputs\s*Note: (?<expected>.*?)\s*## Actual outputs\s*Note: (?<actual>.*?)\s*(## Criteria|Respond with)", RegexOptions.Singleline)]
    private static partial Regex Pair();

    public static ScriptedModel Create() => new ScriptedModel()
        .When("propose-instruction", call => call.Mentions("prompt engineer") || call.User.Contains("prompt engineer", StringComparison.Ordinal)
            ? DraftingRules.TunedInstruction
            : null)
        .When("judge", Judge);

    /// <summary>Scores the share of expected words that the actual note keeps.</summary>
    static string? Judge(ModelCall call)
    {
        if (!call.Mentions("You grade release notes"))
            return null;
        var pair = Pair().Match(call.User);
        var expected = Words(pair.Groups["expected"].Value);
        var actual = Words(pair.Groups["actual"].Value);
        var kept = expected.Count == 0 ? 1.0 : (double)expected.Count(actual.Contains) / expected.Count;
        return JsonSerializer.Serialize(new { score = Math.Round(kept, 2), reasoning = "Counts shared words." });
    }

    static HashSet<string> Words(string text) =>
        text.ToLowerInvariant().Split([' ', '.', ','], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
}
