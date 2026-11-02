using SignalGrid.Ampelum.Agent;
using Shouldly;
using Xunit;

namespace Ampelum.Agent.Tests.LongHorizon;

[Collection("LongHorizon")]
public sealed class MemoryManagerTests
{
    [Fact]
    public async Task InMemoryStateStore_SaveAndLoad_ReturnsLatestCheckpoint()
    {
        var store = new InMemoryStateStore();
        var cp1 = new PhaseCheckpoint
        {
            CheckpointId = "cp1",
            WorkflowId = "w1",
            Phase = "phase1",
            GoalAnchor = "goal1",
        };
        var cp2 = new PhaseCheckpoint
        {
            CheckpointId = "cp2",
            WorkflowId = "w1",
            Phase = "phase2",
            GoalAnchor = "goal1",
        };

        await store.SaveAsync(cp1, TestContext.Current.CancellationToken);
        await store.SaveAsync(cp2, TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync("w1", TestContext.Current.CancellationToken);
        loaded.ShouldNotBeNull();
        loaded!.CheckpointId.ShouldBe("cp2");
        loaded.Phase.ShouldBe("phase2");
    }

    [Fact]
    public async Task InMemoryStateStore_LoadByIdAsync_ReturnsCheckpointById()
    {
        var store = new InMemoryStateStore();
        var cp1 = new PhaseCheckpoint
        {
            CheckpointId = "unique-id-1",
            WorkflowId = "w1",
            Phase = "phase1",
            GoalAnchor = "goal1",
        };
        var cp2 = new PhaseCheckpoint
        {
            CheckpointId = "unique-id-2",
            WorkflowId = "w1",
            Phase = "phase2",
            GoalAnchor = "goal1",
        };

        await store.SaveAsync(cp1, TestContext.Current.CancellationToken);
        await store.SaveAsync(cp2, TestContext.Current.CancellationToken);

        var loaded = await store.LoadByIdAsync("unique-id-1", TestContext.Current.CancellationToken);
        loaded.ShouldNotBeNull();
        loaded!.CheckpointId.ShouldBe("unique-id-1");
        loaded.Phase.ShouldBe("phase1");
    }

    [Fact]
    public async Task InMemoryStateStore_PurgeAsync_RemovesAllCheckpoints()
    {
        var store = new InMemoryStateStore();
        var cp = new PhaseCheckpoint
        {
            CheckpointId = "cp1",
            WorkflowId = "w1",
            Phase = "phase1",
            GoalAnchor = "goal1",
        };

        await store.SaveAsync(cp, TestContext.Current.CancellationToken);
        await store.PurgeAsync("w1", TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync("w1", TestContext.Current.CancellationToken);
        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task MemoryManager_ArchivePhaseAsync_PreservesGoalAnchor()
    {
        var store = new InMemoryStateStore();
        var manager = new MemoryManager(store);

        var checkpoint = new PhaseCheckpoint
        {
            CheckpointId = "cp1",
            WorkflowId = "w1",
            Phase = "phase1",
            PhaseIndex = 0,
            GoalAnchor = "original goal",
            CompletedPhases = [],
            ActiveContext = "some working context",
        };

        var archived = await manager.ArchivePhaseAsync(checkpoint, "phase2", TestContext.Current.CancellationToken);

        archived.GoalAnchor.ShouldBe("original goal");
        archived.Phase.ShouldBe("phase2");
        archived.PhaseIndex.ShouldBe(1);
        archived.ActiveContext.ShouldBeNull();
    }

    [Fact]
    public async Task MemoryManager_ArchivePhaseAsync_AddsPhaseSummary()
    {
        var store = new InMemoryStateStore();
        var manager = new MemoryManager(store);

        var checkpoint = new PhaseCheckpoint
        {
            CheckpointId = "cp1",
            WorkflowId = "w1",
            Phase = "phase1",
            GoalAnchor = "goal",
            CompletedPhases = [],
        };

        var archived = await manager.ArchivePhaseAsync(checkpoint, "phase2", TestContext.Current.CancellationToken);

        archived.CompletedPhases.Count.ShouldBe(1);
        archived.CompletedPhases[0].PhaseName.ShouldBe("phase1");
        archived.CompletedPhases[0].Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void MemoryManager_BuildPhaseContext_GoalAnchorIsAlwaysFirst()
    {
        var store = new InMemoryStateStore();
        var manager = new MemoryManager(store);

        var checkpoint = new PhaseCheckpoint
        {
            WorkflowId = "w1",
            Phase = "phase1",
            GoalAnchor = "complete the task",
            CompletedPhases = [],
        };

        var context = manager.BuildPhaseContext(checkpoint);

        context.Count.ShouldBeGreaterThan(0);
        context[0].Role.ShouldBe("system");
        context[0].Content.ShouldContain("GOAL ANCHOR");
        context[0].Content.ShouldContain("complete the task");
    }

    [Fact]
    public void MemoryManager_BuildPhaseContext_IncludesSummaries()
    {
        var store = new InMemoryStateStore();
        var manager = new MemoryManager(store);

        var summary = new PhaseSummary
        {
            PhaseName = "planning",
            Succeeded = true,
            TasksCompleted = 3,
            TasksFailed = 0,
        };

        var checkpoint = new PhaseCheckpoint
        {
            WorkflowId = "w1",
            Phase = "phase2",
            GoalAnchor = "goal",
            CompletedPhases = [summary],
        };

        var context = manager.BuildPhaseContext(checkpoint);

        context.Count.ShouldBeGreaterThanOrEqualTo(2);
        var summaryMsg = context.FirstOrDefault(m => m.Content.Contains("Completed phases"));
        summaryMsg.ShouldNotBeNull();
        summaryMsg!.Content.ShouldContain("planning");
        summaryMsg.Content.ShouldContain("3 tasks completed");
    }
}
