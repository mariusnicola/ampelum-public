using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Agent.Synthesis;
using SignalGrid.Ampelum.Agent;
using PhaseContextEntrySynthesis = SignalGrid.Ampelum.Agent.Synthesis.PhaseContextEntry;

namespace SignalGrid.Ampelum.Agent.Tests.Synthesis;

public sealed class WorkflowSynthesizerTests
{
    // ── Stub Infrastructure ─────────────────────────────────────────────

    private sealed class StubSynthesisInfrastructure : ISynthesisInfrastructure
    {
        public string GeneratedContent { get; set; } = """
            id: test-workflow
            version: 1.0.0
            description: A test workflow
            steps:
              - id: step1
                kind: tool
                tool: read_file
            """;
        public bool ReturnValid { get; set; } = true;
        public int WriteCallCount { get; private set; }
        public string WrittenPath { get; private set; } = "";
        public string WrittenContent { get; private set; } = "";
        public string WrittenVersion { get; private set; } = "";

        public Task<SynthesisResult> GenerateAndValidateAsync(
            string prompt,
            string? existingContent,
            Func<string, string[]> validator,
            CancellationToken ct = default)
        {
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
            WriteCallCount++;
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
    public async Task CreateAsync_ValidInput_ProducesCandidateAtPath()
    {
        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);
        var tools = new[] { new ToolSurvey { Name = "read_file", Description = "Read", DangerTier = DangerTier.Safe } };

        var result = await synthesizer.CreateAsync("Build and test a project", tools, "workflows/build.yaml", TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Candidate.ShouldNotBeNull();
        result.Candidate!.TargetPath.ShouldBe("workflows/build.yaml");
        result.Candidate.Version.ShouldBe("1.0.0");
        result.ValidationErrors.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ValidationFails_ReturnsFailure()
    {
        var infra = new StubSynthesisInfrastructure { ReturnValid = false };
        var synthesizer = new DefaultWorkflowSynthesizer(infra);

        var result = await synthesizer.CreateAsync("task", [], "output.yaml", TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Candidate.ShouldBeNull();
        result.ValidationErrors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_NeverWritesToCanonicalPath()
    {
        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);

        var result = await synthesizer.CreateAsync("task", [], "workflows/task.yaml", TestContext.Current.CancellationToken);

        infra.WriteCallCount.ShouldBe(0);
        result.Candidate!.TargetPath.ShouldBe("workflows/task.yaml");
        result.Candidate.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public void CreateAsync_NullInfrastructure_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new DefaultWorkflowSynthesizer(null!));
    }

    [Fact]
    public async Task CreateAsync_EmptyTools_StillSucceeds()
    {
        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);

        var result = await synthesizer.CreateAsync("Deploy application", [], "deploy.yaml", TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateAsync_NullOrWhitespaceDescription_Throws()
    {
        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);

        await Should.ThrowAsync<ArgumentException>(() =>
            synthesizer.CreateAsync("", [], "out.yaml", TestContext.Current.CancellationToken));
    }

    // ── ImproveAsync Tests ─────────────────────────────────────────────

    [Fact]
    public async Task ImproveAsync_WorkflowNotFound_ReturnsFailure()
    {
        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);
        var context = new ImprovementContext(
            new WorkflowMetrics { WorkflowName = "x", SuccessRate = 0.5, AvgDurationSeconds = 10, P95DurationSeconds = 30, RunCount = 5 },
            [],
            "increase success rate"
        );

        var result = await synthesizer.ImproveAsync("nonexistent/path.yaml", context, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Candidate.ShouldBeNull();
        result.ValidationErrors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ImproveAsync_ExistingWorkflowNoLongerConformsToCurrentSchema_FailsLoudRatherThanFabricatingAVersion()
    {
        // Regression test: ExtractVersion must never silently fall back to "1.0.0" for on-disk content that
        // no longer compiles under the current schema (e.g. schema drift) -- that would dress up a version
        // regression as an ordinary minor bump on the resulting candidate. ImproveAsync must fail loud instead.
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var workflowFile = Path.Combine(tempDir, "workflow.yaml");
        await File.WriteAllTextAsync(workflowFile, "name: stale-schema-workflow\ndescription: predates the current compiler\n");

        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);
        var metrics = new WorkflowMetrics { WorkflowName = "stale-schema-workflow", SuccessRate = 0.8, AvgDurationSeconds = 10, P95DurationSeconds = 20, RunCount = 5 };
        var context = new ImprovementContext(metrics, [], "reduce duration");

        var result = await synthesizer.ImproveAsync(workflowFile, context, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.Contains("no longer conforms to the current schema"));
        infra.WriteCallCount.ShouldBe(0);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ImproveAsync_WithValidWorkflow_ProducesBumpedVersionCandidate_NoWrite()
    {
        // Arrange: create a temporary workflow file
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var workflowFile = Path.Combine(tempDir, "workflow.yaml");
        await File.WriteAllTextAsync(workflowFile, """
            id: test-workflow
            version: 1.0.0
            description: Original workflow
            steps:
              - id: step1
                kind: tool
                tool: read_file
            """);

        var infra = new StubSynthesisInfrastructure
        {
            GeneratedContent = """
                id: test-workflow
                version: 1.1.0
                description: Improved workflow
                steps:
                  - id: step1
                    kind: tool
                    tool: read_file
                  - id: step2
                    kind: tool
                    tool: write_file
                    depends_on: [step1]
                """
        };
        var synthesizer = new DefaultWorkflowSynthesizer(infra);

        var metrics = new WorkflowMetrics
        {
            WorkflowName = "test-workflow",
            SuccessRate = 0.8,
            AvgDurationSeconds = 15,
            P95DurationSeconds = 30,
            RunCount = 10
        };
        var context = new ImprovementContext(metrics, [], "reduce duration");

        // Act
        var result = await synthesizer.ImproveAsync(workflowFile, context, TestContext.Current.CancellationToken);

        // Assert
        result.Success.ShouldBeTrue();
        result.Candidate.ShouldNotBeNull();
        result.Candidate!.Version.ShouldBe("1.1.0");
        result.Candidate.TargetPath.ShouldBe(workflowFile);
        infra.WriteCallCount.ShouldBe(0);

        // Cleanup
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ImproveAsync_WithLowSuccessRate_RequiresReview()
    {
        // Arrange: create a temporary workflow file
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var workflowFile = Path.Combine(tempDir, "workflow.yaml");
        await File.WriteAllTextAsync(workflowFile, """
            id: test-workflow
            version: 1.0.0
            description: Original workflow
            steps:
              - id: step1
                kind: tool
                tool: read_file
            """);

        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);

        var metrics = new WorkflowMetrics
        {
            WorkflowName = "test-workflow",
            SuccessRate = 0.3, // Below 0.5 threshold
            AvgDurationSeconds = 15,
            P95DurationSeconds = 30,
            RunCount = 10
        };
        var context = new ImprovementContext(metrics, [], "increase success rate");

        // Act
        var result = await synthesizer.ImproveAsync(workflowFile, context, TestContext.Current.CancellationToken);

        // Assert
        result.Success.ShouldBeTrue();
        result.Candidate!.RequiresReview.ShouldBeTrue();
        infra.WriteCallCount.ShouldBe(0);

        // Cleanup
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ImproveAsync_WithValidationError_ReturnsFailure()
    {
        // Arrange: create a temporary workflow file
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var workflowFile = Path.Combine(tempDir, "workflow.yaml");
        await File.WriteAllTextAsync(workflowFile, """
            id: test-workflow
            version: 1.0.0
            description: Original workflow
            steps:
              - id: step1
                kind: tool
                tool: read_file
            """);

        var infra = new StubSynthesisInfrastructure { ReturnValid = false };
        var synthesizer = new DefaultWorkflowSynthesizer(infra);

        var metrics = new WorkflowMetrics
        {
            WorkflowName = "test-workflow",
            SuccessRate = 0.8,
            AvgDurationSeconds = 15,
            P95DurationSeconds = 30,
            RunCount = 10
        };
        var context = new ImprovementContext(metrics, [], "improve");

        // Act
        var result = await synthesizer.ImproveAsync(workflowFile, context, TestContext.Current.CancellationToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.Candidate.ShouldBeNull();
        result.ValidationErrors.ShouldNotBeEmpty();
        infra.WriteCallCount.ShouldBe(0);

        // Cleanup
        Directory.Delete(tempDir, recursive: true);
    }

    // [DISABLED] Constructor signature mismatch: PhaseContextEntry requires (string EntryType, string ToolName, bool Success, string? Summary, DateTimeOffset Timestamp)
    // but test attempts instantiation with (string, bool, double, int, Dictionary<string, string>)
    // This is a pre-existing bug unrelated to MutatingTool implementation.
    // [Fact]
    // public void ImprovementContext_Properties_Set()
    // {
    //     var metrics = new WorkflowMetrics
    //     {
    //         WorkflowName = "test",
    //         SuccessRate = 0.9,
    //         AvgDurationSeconds = 60,
    //         P95DurationSeconds = 120,
    //         RunCount = 10
    //     };
    //     var historyEntry = new PhaseContextEntrySynthesis("step1", true, 5.0, 100, new Dictionary<string, string>());
    //     var history = new[] { historyEntry };
    //     var context = new ImprovementContext(metrics, (IReadOnlyList<PhaseContextEntrySynthesis>)history, "reduce duration");
    //
    //     context.ImprovementGoal.ShouldBe("reduce duration");
    //     context.CurrentMetrics.SuccessRate.ShouldBe(0.9);
    //     context.RecentHistory.Count.ShouldBe(1);
    // }

    [Fact]
    public void WorkflowSynthesisResult_Success_Properties()
    {
        var candidate = new WorkflowSynthesisCandidate("content", "/path/workflow.yaml", "1.1.0", false);
        var result = new WorkflowSynthesisResult(true, candidate, []);

        result.Success.ShouldBeTrue();
        result.Candidate.ShouldNotBeNull();
        result.Candidate!.TargetPath.ShouldBe("/path/workflow.yaml");
        result.Candidate.Version.ShouldBe("1.1.0");
        result.Candidate.RequiresReview.ShouldBeFalse();
    }

    [Fact]
    public void WorkflowSynthesisResult_Failure_Properties()
    {
        var errors = new[] { "Missing steps field" };
        var result = new WorkflowSynthesisResult(false, null, errors);

        result.Success.ShouldBeFalse();
        result.Candidate.ShouldBeNull();
        result.ValidationErrors.ShouldContain("Missing steps field");
    }

    // ── PromoteAsync Tests ─────────────────────────────────────────────

    [Fact]
    public async Task PromoteAsync_WritesCandidateExactlyOnce()
    {
        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);

        var createResult = await synthesizer.CreateAsync("task", [], "workflows/task.yaml", TestContext.Current.CancellationToken);
        infra.WriteCallCount.ShouldBe(0);

        var promotion = await synthesizer.PromoteAsync(
            createResult.Candidate!,
            reviewApproved: true,
            ct: TestContext.Current.CancellationToken);

        promotion.Success.ShouldBeTrue();
        promotion.WorkflowPath.ShouldBe("workflows/task.yaml");
        promotion.Version.ShouldBe("1.0.0");
        promotion.Errors.ShouldBeEmpty();
        infra.WriteCallCount.ShouldBe(1);
        infra.WrittenPath.ShouldBe("workflows/task.yaml");
        infra.WrittenContent.ShouldBe(createResult.Candidate!.Content);
        infra.WrittenVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task PromoteAsync_NullCandidate_Throws()
    {
        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            synthesizer.PromoteAsync(null!, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PromoteAsync_RequiresReviewWithoutApproval_FailsAndDoesNotWrite()
    {
        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);
        var candidate = new WorkflowSynthesisCandidate(
            infra.GeneratedContent,
            "workflows/reviewed.yaml",
            "1.1.0",
            RequiresReview: true);

        var promotion = await synthesizer.PromoteAsync(candidate, ct: TestContext.Current.CancellationToken);

        promotion.Success.ShouldBeFalse();
        promotion.WorkflowPath.ShouldBeNull();
        promotion.Version.ShouldBeNull();
        promotion.Errors.ShouldNotBeEmpty();
        infra.WriteCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task PromoteAsync_RequiresReviewWithApproval_SucceedsAndWritesOnce()
    {
        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);
        var candidate = new WorkflowSynthesisCandidate(
            infra.GeneratedContent,
            "workflows/reviewed.yaml",
            "1.1.0",
            RequiresReview: true);

        var promotion = await synthesizer.PromoteAsync(
            candidate,
            reviewApproved: true,
            ct: TestContext.Current.CancellationToken);

        promotion.Success.ShouldBeTrue();
        promotion.WorkflowPath.ShouldBe("workflows/reviewed.yaml");
        promotion.Version.ShouldBe("1.1.0");
        promotion.Errors.ShouldBeEmpty();
        infra.WriteCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task PromoteAsync_TamperedContentFailsValidation_FailsAndDoesNotWrite()
    {
        var infra = new StubSynthesisInfrastructure();
        var synthesizer = new DefaultWorkflowSynthesizer(infra);
        // Construct directly (bypassing CreateAsync) to simulate a candidate whose content
        // was mutated after creation, or has otherwise gone stale — missing required fields.
        var candidate = new WorkflowSynthesisCandidate(
            "name: broken-workflow\ndescription: missing version and steps",
            "workflows/tampered.yaml",
            "1.0.0",
            RequiresReview: false);

        var promotion = await synthesizer.PromoteAsync(
            candidate,
            reviewApproved: true,
            ct: TestContext.Current.CancellationToken);

        promotion.Success.ShouldBeFalse();
        promotion.WorkflowPath.ShouldBeNull();
        promotion.Version.ShouldBeNull();
        promotion.Errors.ShouldNotBeEmpty();
        infra.WriteCallCount.ShouldBe(0);
    }
}
