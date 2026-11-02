using Shouldly;
using Xunit;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum.Agent.Synthesis;
using SignalGrid.Ampelum.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum.Agent.Tests.Synthesis;

public sealed class SkillSynthesizerTests
{
    // ── Stub Infrastructure ─────────────────────────────────────────────

    private sealed class StubSynthesisInfrastructure : ISynthesisInfrastructure
    {
        public string? LastPrompt { get; private set; }
        public string GeneratedContent { get; set; } = """
            name: test-skill
            version: 1.0.0
            description: A test skill
            system_prompt: You are a helpful assistant.
            """;
        public bool ReturnValid { get; set; } = true;
        public string WrittenPath { get; private set; } = "";
        public string WrittenContent { get; private set; } = "";
        public string WrittenVersion { get; private set; } = "";

        public Task<SynthesisResult> GenerateAndValidateAsync(
            string prompt,
            string? existingContent,
            Func<string, string[]> validator,
            CancellationToken ct = default)
        {
            LastPrompt = prompt;
            var errors = ReturnValid ? validator(GeneratedContent) : new[] { "Validation failed" };
            return Task.FromResult(new SynthesisResult(
                GeneratedContent,
                errors.Length == 0,
                1,
                errors));
        }

        public Task<string> WriteVersionedAsync(
            string canonicalPath,
            string content,
            string version,
            CancellationToken ct = default)
        {
            WrittenPath = canonicalPath;
            WrittenContent = content;
            WrittenVersion = version;
            return Task.FromResult(canonicalPath);
        }

        public Task<WorkflowMetrics?> ReadMetricsAsync(
            string canonicalPath,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowMetrics?>(null);

        public Task WriteMetricsAsync(
            string canonicalPath,
            WorkflowMetrics metrics,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    // ── CreateAsync Tests ─────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidInput_CreatesSkillWithCorrectPath()
    {
        var stub = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultSkillSynthesizer(stub);

        var result = await synthesizer.CreateAsync(
            "analyze code quality",
            Array.Empty<ToolSurvey>(),
            "/skills",
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.SkillName.ShouldBe("analyze-code-quality");
        result.Version.ShouldBe("1.0.0");
        result.ValidationErrors.ShouldBeEmpty();
        stub.WrittenVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task CreateAsync_ValidationFails_ReturnsFailure()
    {
        var stub = new StubSynthesisInfrastructure
        {
            ReturnValid = false,
            GeneratedContent = "invalid content"
        };
        var synthesizer = new DefaultSkillSynthesizer(stub);

        var result = await synthesizer.CreateAsync(
            "test skill",
            Array.Empty<ToolSurvey>(),
            "/skills",
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ValidationErrors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CreateAsync_NullInfrastructure_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new DefaultSkillSynthesizer(null!));
    }

    [Fact]
    public async Task CreateAsync_NullTaskDescription_Throws()
    {
        var stub = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultSkillSynthesizer(stub);

        await Should.ThrowAsync<ArgumentException>(
            async () => await synthesizer.CreateAsync(
                null!,
                Array.Empty<ToolSurvey>(),
                "/skills",
                TestContext.Current.CancellationToken));
    }

    // ── ImproveAsync Tests ─────────────────────────────────────────────

    [Fact]
    public async Task ImproveAsync_SkillNotFound_ReturnsFailure()
    {
        var stub = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultSkillSynthesizer(stub);

        var result = await synthesizer.ImproveAsync(
            "nonexistent-skill",
            "/nonexistent/path",
            Array.Empty<SkillExecutionRecord>(),
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImproveAsync_WithSuccessfulHistory_BumpsVersion()
    {
        // Arrange: create a temporary SKILL.md file
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var skillDir = Path.Combine(tempDir, "test-skill");
        Directory.CreateDirectory(skillDir);

        var skillFile = Path.Combine(skillDir, "SKILL.md");
        await File.WriteAllTextAsync(skillFile, """
            name: test-skill
            version: 1.0.0
            description: Original skill
            system_prompt: Original prompt.
            """);

        var stub = new StubSynthesisInfrastructure
        {
            GeneratedContent = """
                name: test-skill
                version: 1.1.0
                description: Improved skill
                system_prompt: Improved prompt based on feedback.
                """
        };
        var synthesizer = new DefaultSkillSynthesizer(stub);

        var history = new[]
        {
            new SkillExecutionRecord(
                RunInstanceId: "run-1",
                TaskDescription: "test task",
                Succeeded: true,
                ToolsUsed: Array.Empty<string>(),
                UserFeedback: "good",
                OccurredAt: DateTimeOffset.UtcNow)
        };

        // Act
        var result = await synthesizer.ImproveAsync(
            "test-skill",
            tempDir,
            history,
            TestContext.Current.CancellationToken);

        // Assert
        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("1.1.0");

        // Cleanup
        Directory.Delete(tempDir, recursive: true);
    }

    // ── SkillProgram Tests ─────────────────────────────────────────────

    private static AmpelumContext MakeContext() => new(
        new FakeChatClient(),
        new ChatAdapter(),
        NullTraceSink.Instance,
        NullLoggerFactory.Instance);

    [Fact]
    public void SkillProgram_Rebind_ReturnsNewInstance()
    {
        var manifest = new SkillManifest { Name = "test-skill", Version = "1.0.0" };
        var predict1 = new Predict<SkillInstructionSig>(MakeContext());

        var program = new SkillProgram(manifest, predict1);

        var predict2 = new Predict<SkillInstructionSig>(MakeContext());

        var newSubmodules = new Dictionary<string, object>
        {
            { "instruction", predict2 }
        };

        var rebound = program.Rebind(newSubmodules);

        rebound.ShouldNotBeNull();
        rebound.ShouldBeOfType<SkillProgram>();
        ((SkillProgram)rebound).InstructionSubmodule.ShouldNotBeSameAs(predict1);
    }

    [Fact]
    public void SkillProgram_Submodules_ContainsInstruction()
    {
        var manifest = new SkillManifest { Name = "test-skill", Version = "1.0.0" };
        var predict = new Predict<SkillInstructionSig>(MakeContext());

        var program = new SkillProgram(manifest, predict);

        program.Submodules.ShouldContainKey("instruction");
        program.Submodules["instruction"].ShouldBe(predict);
    }

    [Fact]
    public void SkillProgram_NullManifest_Throws()
    {
        var predict = new Predict<SkillInstructionSig>(MakeContext());

        Should.Throw<ArgumentNullException>(() =>
            new SkillProgram(null!, predict));
    }

    // ── SkillQualityMetric Tests ─────────────────────────────────────────────

    [Fact]
    public void SkillQualityMetric_EmptyHistory_ReturnsNeutralScore()
    {
        var metric = new SkillQualityMetric(Array.Empty<SkillExecutionRecord>());

        var sig = SignatureMetadataCache.For<SkillInstructionSig>();
        var score = metric.Score(string.Empty, string.Empty, sig);

        // Empty history: (0.7 * 0.5) + (0.3 * 0.5) = 0.5
        score.ShouldBe(0.5);
    }

    [Fact]
    public void SkillQualityMetric_AllSucceeded_ReturnsHighScore()
    {
        var history = new[]
        {
            new SkillExecutionRecord(
                RunInstanceId: "run-1",
                TaskDescription: "task 1",
                Succeeded: true,
                ToolsUsed: Array.Empty<string>(),
                UserFeedback: null,
                OccurredAt: DateTimeOffset.UtcNow),
            new SkillExecutionRecord(
                RunInstanceId: "run-2",
                TaskDescription: "task 2",
                Succeeded: true,
                ToolsUsed: Array.Empty<string>(),
                UserFeedback: null,
                OccurredAt: DateTimeOffset.UtcNow),
        };

        var metric = new SkillQualityMetric(history);
        var sig = SignatureMetadataCache.For<SkillInstructionSig>();
        var score = metric.Score(string.Empty, string.Empty, sig);

        // 2 successes, 0 feedback: (0.7 * 1.0) + (0.3 * 0.5) = 0.85
        score.ShouldBe(0.85);
    }

    [Fact]
    public void SkillQualityMetric_AllSucceededWithPositiveFeedback_ReturnsVeryHighScore()
    {
        var history = new[]
        {
            new SkillExecutionRecord(
                RunInstanceId: "run-1",
                TaskDescription: "task 1",
                Succeeded: true,
                ToolsUsed: Array.Empty<string>(),
                UserFeedback: "good",
                OccurredAt: DateTimeOffset.UtcNow),
            new SkillExecutionRecord(
                RunInstanceId: "run-2",
                TaskDescription: "task 2",
                Succeeded: true,
                ToolsUsed: Array.Empty<string>(),
                UserFeedback: "excellent",
                OccurredAt: DateTimeOffset.UtcNow),
        };

        var metric = new SkillQualityMetric(history);
        var sig = SignatureMetadataCache.For<SkillInstructionSig>();
        var score = metric.Score(string.Empty, string.Empty, sig);

        // 2 successes, 2 positive feedback: (0.7 * 1.0) + (0.3 * 1.0) = 1.0
        score.ShouldBe(1.0);
    }

    [Fact]
    public void SkillQualityMetric_MixedFeedback_ReturnsBalancedScore()
    {
        var history = new[]
        {
            new SkillExecutionRecord(
                RunInstanceId: "run-1",
                TaskDescription: "task 1",
                Succeeded: true,
                ToolsUsed: Array.Empty<string>(),
                UserFeedback: "good",
                OccurredAt: DateTimeOffset.UtcNow),
            new SkillExecutionRecord(
                RunInstanceId: "run-2",
                TaskDescription: "task 2",
                Succeeded: true,
                ToolsUsed: Array.Empty<string>(),
                UserFeedback: "bad",
                OccurredAt: DateTimeOffset.UtcNow),
        };

        var metric = new SkillQualityMetric(history);
        var sig = SignatureMetadataCache.For<SkillInstructionSig>();
        var score = metric.Score(string.Empty, string.Empty, sig);

        // 2 successes, 1 positive of 2 feedback: (0.7 * 1.0) + (0.3 * 0.5) = 0.85
        score.ShouldBe(0.85);
    }

    [Fact]
    public void SkillExecutionRecord_Properties_Preserved()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new SkillExecutionRecord(
            RunInstanceId: "run-123",
            TaskDescription: "analyze code",
            Succeeded: true,
            ToolsUsed: new[] { "linter", "formatter" },
            UserFeedback: "good",
            OccurredAt: now);

        record.RunInstanceId.ShouldBe("run-123");
        record.TaskDescription.ShouldBe("analyze code");
        record.Succeeded.ShouldBeTrue();
        record.ToolsUsed.ShouldContain("linter");
        record.ToolsUsed.ShouldContain("formatter");
        record.UserFeedback.ShouldBe("good");
        record.OccurredAt.ShouldBe(now);
    }

    [Fact]
    public void SkillQualityMetric_NullHistory_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new SkillQualityMetric(null!));
    }

    [Fact]
    public void SkillProgram_RebindWithWrongType_ReturnsSelf()
    {
        var manifest = new SkillManifest { Name = "test-skill", Version = "1.0.0" };
        var predict = new Predict<SkillInstructionSig>(MakeContext());

        var program = new SkillProgram(manifest, predict);

        var newSubmodules = new Dictionary<string, object>
        {
            { "instruction", new object() } // wrong type
        };

        var rebound = program.Rebind(newSubmodules);

        rebound.ShouldBeSameAs(program);
    }
}
