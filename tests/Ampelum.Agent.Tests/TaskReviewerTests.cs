using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class TaskReviewerTests
{
    // ── FileChange properties ────────────────────────────────────

    [Fact]
    public void FileChange_Path_SetsCorrectly()
    {
        var change = new FileChange("/src/example.cs", "--- a\n+++ b\n@@ -1,1 +1,1 @@");
        change.Path.ShouldBe("/src/example.cs");
    }

    [Fact]
    public void FileChange_Diff_SetsCorrectly()
    {
        var diff = "--- a\n+++ b\n@@ -1,3 +1,4 @@";
        var change = new FileChange("/src/file.cs", diff);
        change.Diff.ShouldBe(diff);
    }

    [Fact]
    public void FileChange_BothProperties()
    {
        var change = new FileChange("/test/code.cs", "diff content");
        change.Path.ShouldBe("/test/code.cs");
        change.Diff.ShouldBe("diff content");
    }

    // ── ReviewSeverity enum ─────────────────────────────────────

    [Fact]
    public void ReviewSeverity_Info_Exists()
    {
        var severity = ReviewSeverity.Info;
        severity.ShouldBe(ReviewSeverity.Info);
    }

    [Fact]
    public void ReviewSeverity_Warning_Exists()
    {
        var severity = ReviewSeverity.Warning;
        severity.ShouldBe(ReviewSeverity.Warning);
    }

    [Fact]
    public void ReviewSeverity_Error_Exists()
    {
        var severity = ReviewSeverity.Error;
        severity.ShouldBe(ReviewSeverity.Error);
    }

    // ── ReviewFinding properties ─────────────────────────────────

    [Fact]
    public void ReviewFinding_Message_Required()
    {
        var finding = new ReviewFinding("Code review issue", ReviewSeverity.Warning);
        finding.Message.ShouldBe("Code review issue");
        finding.Severity.ShouldBe(ReviewSeverity.Warning);
    }

    [Fact]
    public void ReviewFinding_AllProperties()
    {
        var finding = new ReviewFinding(
            "Missing docstring",
            ReviewSeverity.Error,
            "/src/code.cs",
            42);

        finding.Message.ShouldBe("Missing docstring");
        finding.Severity.ShouldBe(ReviewSeverity.Error);
        finding.FilePath.ShouldBe("/src/code.cs");
        finding.Line.ShouldBe(42);
    }

    [Fact]
    public void ReviewFinding_FilePathOptional()
    {
        var finding = new ReviewFinding("General issue", ReviewSeverity.Info, null, null);
        finding.FilePath.ShouldBeNull();
        finding.Line.ShouldBeNull();
    }

    [Fact]
    public void ReviewFinding_LineNumberOptional()
    {
        var finding = new ReviewFinding(
            "File-level issue",
            ReviewSeverity.Warning,
            "/src/file.cs",
            null);

        finding.FilePath.ShouldBe("/src/file.cs");
        finding.Line.ShouldBeNull();
    }

    // ── ReviewResult properties ──────────────────────────────────

    [Fact]
    public void ReviewResult_ReviewerId_SetCorrectly()
    {
        var findings = new List<ReviewFinding>();
        var result = new ReviewResult("code-reviewer-v1", true, findings);
        result.ReviewerId.ShouldBe("code-reviewer-v1");
    }

    [Fact]
    public void ReviewResult_Approved_True()
    {
        var result = new ReviewResult("reviewer", true, Array.Empty<ReviewFinding>());
        result.Approved.ShouldBeTrue();
    }

    [Fact]
    public void ReviewResult_Approved_False()
    {
        var findings = new[] { new ReviewFinding("Issue", ReviewSeverity.Error) };
        var result = new ReviewResult("reviewer", false, findings);
        result.Approved.ShouldBeFalse();
    }

    [Fact]
    public void ReviewResult_NoFindings_Approved()
    {
        var result = new ReviewResult("reviewer", true, Array.Empty<ReviewFinding>());
        result.Findings.Count.ShouldBe(0);
        result.Approved.ShouldBeTrue();
    }

    [Fact]
    public void ReviewResult_WithFindings()
    {
        var findings = new List<ReviewFinding>
        {
            new("Warning 1", ReviewSeverity.Warning),
            new("Error 1", ReviewSeverity.Error),
            new("Info 1", ReviewSeverity.Info)
        };

        var result = new ReviewResult("reviewer", false, findings);
        result.Findings.Count.ShouldBe(3);
        result.Findings[0].Severity.ShouldBe(ReviewSeverity.Warning);
        result.Findings[1].Severity.ShouldBe(ReviewSeverity.Error);
        result.Findings[2].Severity.ShouldBe(ReviewSeverity.Info);
    }

    [Fact]
    public void ReviewResult_ErrorSeverityFinding_ImpliesFailed()
    {
        var findings = new[] { new ReviewFinding("Blocking issue", ReviewSeverity.Error) };
        var result = new ReviewResult("reviewer", false, findings);

        result.Findings.Count.ShouldBe(1);
        result.Findings[0].Severity.ShouldBe(ReviewSeverity.Error);
        result.Approved.ShouldBeFalse();
    }

    // ── ITaskReviewer contract ───────────────────────────────────

    [Fact]
    public void StubReviewer_Domain_SetsCorrectly()
    {
        var reviewer = new StubReviewer("software");
        reviewer.Domain.ShouldBe("software");
    }

    [Fact]
    public async Task StubReviewer_ReviewAsync_ReturnsApproved()
    {
        var reviewer = new StubReviewer("software");
        var task = new TaskDefinition { Id = "task-1", Description = "Test", TaskType = "code-review" };
        var changes = new[] { new FileChange("/src/test.cs", "diff") };

        var result = await reviewer.ReviewAsync(task, changes, TestContext.Current.CancellationToken);

        result.Approved.ShouldBeTrue();
        result.ReviewerId.ShouldBe("stub-reviewer");
        result.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task StubReviewer_ReviewAsync_ReturnsRejected()
    {
        var reviewer = new StubReviewer("research", approved: false);
        var task = new TaskDefinition { Id = "task-1", Description = "Test", TaskType = "review" };
        var changes = new[] { new FileChange("/docs/analysis.md", "content diff") };

        var result = await reviewer.ReviewAsync(task, changes, TestContext.Current.CancellationToken);

        result.Approved.ShouldBeFalse();
        result.Findings.ShouldContain(f => f.Severity == ReviewSeverity.Error);
    }

    [Fact]
    public async Task StubReviewer_ReviewAsync_WithEmptyChanges()
    {
        var reviewer = new StubReviewer("content");
        var task = new TaskDefinition { Id = "task-2", Description = "Empty", TaskType = "review" };
        var changes = Array.Empty<FileChange>();

        var result = await reviewer.ReviewAsync(task, changes, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.ReviewerId.ShouldBe("stub-reviewer");
    }

    // ── IDomainProfile.Reviewers default ─────────────────────────

    [Fact]
    public void SoftwareDomainProfile_Reviewers_DefaultIsNull()
    {
        IDomainProfile profile = new SoftwareDomainProfile();
        profile.Reviewers.ShouldBeNull();
    }

    [Fact]
    public void ResearchDomainProfile_Reviewers_DefaultIsNull()
    {
        IDomainProfile profile = new ResearchDomainProfile();
        profile.Reviewers.ShouldBeNull();
    }

    [Fact]
    public void DomainProfile_CanHaveReviewers()
    {
        var reviewers = new[] { new StubReviewer("software") };
        IDomainProfile profile = new CustomProfile(reviewers);
        profile.Reviewers.ShouldNotBeNull();
        profile.Reviewers.Count.ShouldBe(1);
        profile.Reviewers[0].Domain.ShouldBe("software");
    }

    // ── Stub implementations for testing ──────────────────────────

    private sealed class StubReviewer : ITaskReviewer
    {
        private readonly bool _approved;

        public string Domain { get; }

        public StubReviewer(string domain, bool approved = true)
        {
            Domain = domain;
            _approved = approved;
        }

        public Task<ReviewResult> ReviewAsync(
            TaskDefinition task,
            IReadOnlyList<FileChange> changes,
            CancellationToken ct = default)
        {
            var findings = _approved
                ? Array.Empty<ReviewFinding>()
                : new[] { new ReviewFinding("Review failed", ReviewSeverity.Error) };

            var result = new ReviewResult("stub-reviewer", _approved, findings);
            return Task.FromResult(result);
        }
    }

    private sealed class CustomProfile : IDomainProfile
    {
        private readonly IReadOnlyList<ITaskReviewer>? _reviewers;

        public string Name => "custom";
        public IReadOnlySet<string> ToolIds => new HashSet<string>();
        public IReadOnlyList<string> RelevantSkillTags => Array.Empty<string>();
        public IReadOnlyList<ITaskReviewer>? Reviewers => _reviewers;

        public CustomProfile(IReadOnlyList<ITaskReviewer>? reviewers = null)
        {
            _reviewers = reviewers;
        }

        public BlastRadiusScope BuildBlastRadius(string rootPath) => new();
    }
}
