using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Quality gate that counts citations in markdown files.
/// A citation is any `[text](url)` pattern — a proxy for source credibility.</summary>
public sealed class CitationCountGate : IQualityGate
{
    private static readonly Regex CitationPattern = new(
        @"\[([^\]]+)\]\(https?://[^\)]+\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Id => "citation-count";
    public string DisplayName => "Citation Count";

    /// <summary>Minimum citations required to pass. Default: 3.</summary>
    public int MinCitations { get; init; } = 3;

    public async Task<QualityGateResult> RunAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!Directory.Exists(workspaceRoot))
                return Fail($"Directory not found: {workspaceRoot}", sw);

            var mdFiles = Directory.GetFiles(workspaceRoot, "*.md", SearchOption.AllDirectories);
            var totalCitations = 0;
            var errors = new List<string>();

            foreach (var file in mdFiles)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    var matches = CitationPattern.Matches(content);
                    totalCitations += matches.Count;
                }
                catch (Exception ex)
                {
                    errors.Add($"Could not read {file}: {ex.Message}");
                }
            }

            sw.Stop();
            var passed = totalCitations >= MinCitations;

            return new QualityGateResult(
                GateId: Id,
                Success: passed,
                Output: passed
                    ? $"Found {totalCitations} citations across {mdFiles.Length} markdown files (min: {MinCitations})"
                    : $"Only {totalCitations} citations found in {mdFiles.Length} markdown files (need {MinCitations})",
                FindingsCount: totalCitations,
                Duration: sw.Elapsed,
                Error: errors.Count > 0 ? string.Join("; ", errors) : null
            );
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, sw);
        }
    }

    private QualityGateResult Fail(string error, System.Diagnostics.Stopwatch sw)
    {
        sw.Stop();
        return new QualityGateResult(Id, false, null, null, sw.Elapsed, error);
    }
}

/// <summary>Quality gate that counts words in markdown files.
/// Fails if total word count is below the minimum threshold.</summary>
public sealed class WordCountGate : IQualityGate
{
    public string Id => "word-count";
    public string DisplayName => "Word Count";

    /// <summary>Minimum words required to pass. Default: 500.</summary>
    public int MinWords { get; init; } = 500;

    public async Task<QualityGateResult> RunAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!Directory.Exists(workspaceRoot))
                return Fail($"Directory not found: {workspaceRoot}", sw);

            var mdFiles = Directory.GetFiles(workspaceRoot, "*.md", SearchOption.AllDirectories);
            var totalWords = 0;
            var errors = new List<string>();

            foreach (var file in mdFiles)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    // Simple word count — split on whitespace
                    totalWords += content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                }
                catch (Exception ex)
                {
                    errors.Add($"Could not read {file}: {ex.Message}");
                }
            }

            sw.Stop();
            var passed = totalWords >= MinWords;

            return new QualityGateResult(
                GateId: Id,
                Success: passed,
                Output: passed
                    ? $"Found {totalWords} words across {mdFiles.Length} markdown files (min: {MinWords})"
                    : $"Only {totalWords} words in {mdFiles.Length} markdown files (need {MinWords})",
                FindingsCount: totalWords,
                Duration: sw.Elapsed,
                Error: errors.Count > 0 ? string.Join("; ", errors) : null
            );
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, sw);
        }
    }

    private QualityGateResult Fail(string error, System.Diagnostics.Stopwatch sw)
    {
        sw.Stop();
        return new QualityGateResult(Id, false, null, null, sw.Elapsed, error);
    }
}

/// <summary>Default gates for the research domain: citation count and word count.</summary>
public static class ResearchGates
{
    /// <summary>The standard research gates: citation-count, word-count.</summary>
    public static IReadOnlyList<IQualityGate> DefaultGates { get; } = new IQualityGate[]
    {
        new CitationCountGate { MinCitations = 3 },
        new WordCountGate { MinWords = 500 },
    };
}