using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class ResearchGateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"research-test-{Guid.NewGuid():N}");

    public ResearchGateTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── CitationCountGate ────────────────────────────────────────

    [Fact]
    public void CitationCountGate_HasCorrectId()
    {
        var gate = new CitationCountGate();
        gate.Id.ShouldBe("citation-count");
        gate.DisplayName.ShouldBe("Citation Count");
    }

    [Fact]
    public async Task CitationCountGate_NoFiles_Fails()
    {
        var gate = new CitationCountGate { MinCitations = 1 };
        var result = await gate.RunAsync(_tempDir);

        result.Success.ShouldBeFalse();
        result.FindingsCount.ShouldBe(0);
    }

    [Fact]
    public async Task CitationCountGate_EnoughCitations_Passes()
    {
        File.WriteAllText(Path.Combine(_tempDir, "report.md"),
            "# Report\n" +
            "According to [source A](https://example.com/a), the sky is blue.\n" +
            "According to [source B](https://example.com/b), water is wet.\n" +
            "According to [source C](https://example.com/c), grass is green.\n");

        var gate = new CitationCountGate { MinCitations = 3 };
        var result = await gate.RunAsync(_tempDir);

        result.Success.ShouldBeTrue();
        result.FindingsCount.ShouldBe(3);
    }

    [Fact]
    public async Task CitationCountGate_TooFewCitations_Fails()
    {
        File.WriteAllText(Path.Combine(_tempDir, "report.md"),
            "# Report\nJust [one source](https://example.com/a).\n");

        var gate = new CitationCountGate { MinCitations = 3 };
        var result = await gate.RunAsync(_tempDir);

        result.Success.ShouldBeFalse();
        result.FindingsCount.ShouldBe(1);
    }

    [Fact]
    public async Task CitationCountGate_AcrossMultipleFiles_Aggregates()
    {
        File.WriteAllText(Path.Combine(_tempDir, "part1.md"),
            "[A](https://a.com) and [B](https://b.com)\n");
        File.WriteAllText(Path.Combine(_tempDir, "part2.md"),
            "[C](https://c.com) and [D](https://d.com)\n");

        var gate = new CitationCountGate { MinCitations = 4 };
        var result = await gate.RunAsync(_tempDir);

        result.Success.ShouldBeTrue();
        result.FindingsCount.ShouldBe(4);
    }

    // ── WordCountGate ────────────────────────────────────────────

    [Fact]
    public void WordCountGate_HasCorrectId()
    {
        var gate = new WordCountGate();
        gate.Id.ShouldBe("word-count");
        gate.DisplayName.ShouldBe("Word Count");
    }

    [Fact]
    public async Task WordCountGate_NoFiles_Fails()
    {
        var gate = new WordCountGate { MinWords = 1 };
        var result = await gate.RunAsync(_tempDir);

        result.Success.ShouldBeFalse();
        result.FindingsCount.ShouldBe(0);
    }

    [Fact]
    public async Task WordCountGate_EnoughWords_Passes()
    {
        // Create a file with exactly 10 words
        File.WriteAllText(Path.Combine(_tempDir, "article.md"),
            "one two three four five six seven eight nine ten\n");

        var gate = new WordCountGate { MinWords = 10 };
        var result = await gate.RunAsync(_tempDir);

        result.Success.ShouldBeTrue();
        result.FindingsCount.ShouldBe(10);
    }

    [Fact]
    public async Task WordCountGate_TooFewWords_Fails()
    {
        File.WriteAllText(Path.Combine(_tempDir, "article.md"),
            "just three words\n");

        var gate = new WordCountGate { MinWords = 10 };
        var result = await gate.RunAsync(_tempDir);

        result.Success.ShouldBeFalse();
        result.FindingsCount.ShouldBe(3);
    }

    // ── ResearchGates.DefaultGates ────────────────────────────────

    [Fact]
    public void ResearchGates_HasTwoGates()
    {
        ResearchGates.DefaultGates.Count.ShouldBe(2);
        ResearchGates.DefaultGates.ShouldContain(g => g.Id == "citation-count");
        ResearchGates.DefaultGates.ShouldContain(g => g.Id == "word-count");
    }

    // ── ResearchDomainProfile registers gates ─────────────────────

    [Fact]
    public void ResearchDomainProfile_HasNonSoftwareGates()
    {
        IDomainProfile profile = new ResearchDomainProfile();
        profile.Gates.ShouldNotBeNull();
        profile.Gates!.Count.ShouldBe(2);
        profile.Gates.ShouldContain(g => g.Id == "citation-count");
    }

    [Fact]
    public void SoftwareDomainProfile_GatesIsNull()
    {
        IDomainProfile profile = new SoftwareDomainProfile();
        profile.Gates.ShouldBeNull();
    }

    // ── Run gates directly (without GateRunner) ─────

    [Fact]
    public async Task RunResearchGatesDirectly_PassesBoth()
    {
        // Write a long enough article to pass default thresholds
        var words = new List<string>();
        for (var i = 0; i < 550; i++)
            words.Add($"word{i}");
        var citations = new List<string>();
        for (var i = 0; i < 5; i++)
            citations.Add($"[source {i}](https://example.com/{i})");

        var content = "# Test Article\n\n" +
            string.Join(" ", words) + "\n\n" +
            string.Join("\n", citations);
        File.WriteAllText(Path.Combine(_tempDir, "article.md"), content);

        var gates = ResearchGates.DefaultGates;
        foreach (var gate in gates)
        {
            var result = await gate.RunAsync(_tempDir);
            result.Success.ShouldBeTrue($"Gate '{gate.Id}' should pass with {result.FindingsCount}");
        }
    }
}