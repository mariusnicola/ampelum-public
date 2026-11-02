using System.Linq;
using SignalGrid.Ampelum.Agent;
using Shouldly;
using Xunit;

namespace Ampelum.Agent.Tests.LongHorizon;

[Collection("LongHorizon")]
public sealed class JsonFileStateStoreTests
{
    private readonly string _tempDirectory;
    private readonly IStateStore _store;

    public JsonFileStateStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ampelum-test-" + Guid.NewGuid().ToString("N")[..8]);
        _store = new JsonFileStateStore(_tempDirectory);
    }

    [Fact]
    public void Constructor_CreatesBaseDirectory()
    {
        Directory.Exists(_tempDirectory).ShouldBeTrue();
    }

    [Fact]
    public async Task SaveAsync_PersistsCheckpoint()
    {
        var checkpoint = new PhaseCheckpoint
        {
            WorkflowId = "w1",
            Phase = "planning",
            GoalAnchor = "complete feature X",
            CompletedPhases = new List<PhaseSummary>
            {
                new() { PhaseName = "setup", Succeeded = true, TasksCompleted = 2, TasksFailed = 0, Duration = TimeSpan.FromSeconds(5), TokensUsed = 100 }
            },
            ActiveContext = "working context",
            WorkspaceId = "ws1",
        };

        await _store.SaveAsync(checkpoint);

        var loaded = await _store.LoadAsync("w1", "ws1");
        loaded.ShouldNotBeNull();
        loaded!.WorkflowId.ShouldBe("w1");
        loaded.Phase.ShouldBe("planning");
        loaded.GoalAnchor.ShouldBe("complete feature X");
        loaded.CompletedPhases.Count.ShouldBe(1);
        loaded.CompletedPhases[0]!.PhaseName.ShouldBe("setup");
        loaded.CompletedPhases[0]!.Succeeded.ShouldBeTrue();
        loaded.CompletedPhases[0]!.TasksCompleted.ShouldBe(2);
        loaded.CompletedPhases[0]!.TokensUsed.ShouldBe(100);
        loaded.ActiveContext.ShouldBe("working context");
        loaded.WorkspaceId.ShouldBe("ws1");
    }

    [Fact]
    public async Task LoadAsync_NoDirectory_ReturnsNull()
    {
        var result = await _store.LoadAsync("nonexistent-workflow");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_EmptyWorkflow_ReturnsEmpty()
    {
        var result = await _store.ListAsync("no-checkpoints-yet");
        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ListAsync_MultipleCheckpoints_ReturnsAll()
    {
        var cp1 = new PhaseCheckpoint { WorkflowId = "w2", Phase = "planning", GoalAnchor = "a" };
        var cp2 = new PhaseCheckpoint { WorkflowId = "w2", Phase = "coding", GoalAnchor = "b" };
        var cp3 = new PhaseCheckpoint { WorkflowId = "w2", Phase = "testing", GoalAnchor = "c" };

        await _store.SaveAsync(cp1);
        await _store.SaveAsync(cp2);
        await _store.SaveAsync(cp3);

        var result = await _store.ListAsync("w2");

        result.Count.ShouldBe(3);
        // Verify we got all three checkpoints back (order not guaranteed)
        var phases = result.Select(r => r.Phase).OrderBy(p => p).ToList();
        phases.ShouldContain("coding");
        phases.ShouldContain("planning");
        phases.ShouldContain("testing");
    }

    [Fact]
    public async Task PurgeAsync_DeletesAllWorkshop()
    {
        var cp = new PhaseCheckpoint { WorkflowId = "p1", Phase = "run", GoalAnchor = "g", CapturedAt = DateTimeOffset.UtcNow };
        await _store.SaveAsync(cp);

        await _store.PurgeAsync("p1");
        var result = await _store.ListAsync("p1");
        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task LoadAsync_WorkspaceNull_FallbackLoadAll()
    {
        var cp1 = new PhaseCheckpoint { WorkflowId = "w3", Phase = "phase1", GoalAnchor = "g" };
        var cp2 = new PhaseCheckpoint { WorkflowId = "w3", Phase = "phase1", GoalAnchor = "g", WorkspaceId = "ws-null" };
        var cp3 = new PhaseCheckpoint { WorkflowId = "w3", Phase = "phase1", GoalAnchor = "g", WorkspaceId = "ws-other" };

        await _store.SaveAsync(cp1);
        await _store.SaveAsync(cp2);
        await _store.SaveAsync(cp3);

        var result = await _store.LoadAsync("w3", null);
        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingCheckpoint()
    {
        // Note: File-based store saves by phase+workspaceId. Overwriting same phase updates the file.
        var cp1 = new PhaseCheckpoint 
        { 
            WorkflowId = "w4", 
            Phase = "phaseX", 
            GoalAnchor = "initial-g",
            WorkspaceId = "ws-X"
        };
        var cp2 = new PhaseCheckpoint 
        { 
            WorkflowId = "w4", 
            Phase = "phaseX", 
            GoalAnchor = "updated-g",
            WorkspaceId = "ws-X"
        };

        await _store.SaveAsync(cp1);
        await _store.SaveAsync(cp2);

        var result = await _store.LoadAsync("w4", "ws-X");
        result.ShouldNotBeNull();
        result!.Phase.ShouldBe("phaseX");
        result.GoalAnchor.ShouldBe("updated-g");
    }

    [Fact]
    public void SaveAsync_NullCheckpoint_Throws()
    {
        Should.Throw<ArgumentNullException>(() => _store.SaveAsync(null!)).Message.ShouldContain("checkpoint");
    }

    [Fact]
    public async Task LoadAsync_WorkspaceFilter_LimitsResults()
    {
        var cp1 = new PhaseCheckpoint { WorkflowId = "w5", Phase = "p1", GoalAnchor = "g", WorkspaceId = "exact-match" };
        var cp2 = new PhaseCheckpoint { WorkflowId = "w5", Phase = "p1", GoalAnchor = "g", WorkspaceId = "other-workspace" };

        await _store.SaveAsync(cp1);
        await _store.SaveAsync(cp2);

        var result = await _store.LoadAsync("w5", "exact-match");
        result.ShouldNotBeNull();
    }

    ~JsonFileStateStoreTests()
    {
        // Clean up temp directory
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch { }
    }
}