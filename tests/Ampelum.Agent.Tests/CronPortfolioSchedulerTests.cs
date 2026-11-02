using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

[Collection("Orchestration")]
public class CronPortfolioSchedulerTests : IDisposable
{
    private readonly string _schedulesPath =
        Path.Combine(Path.GetTempPath(), $"schedules-{Guid.NewGuid():N}.json");

    private static IProjectPortfolio EmptyPortfolio() => new InMemoryProjectRegistry();

    private CronPortfolioScheduler MakeScheduler(IPortfolioOrchestrator? orchestrator = null)
    {
        var portfolio = EmptyPortfolio();
        var orch = orchestrator ?? new StubOrchestrator(portfolio);
        return new CronPortfolioScheduler(orch, sanitizer: null, schedulesPath: _schedulesPath);
    }

    [Fact]
    public void Schedule_AddsEntry_ReturnsId()
    {
        var scheduler = MakeScheduler();
        var expr = new ScheduleExpression { TimeOfDay = new TimeOnly(2, 0), Repeat = RepeatInterval.Daily };

        var id = scheduler.Schedule("advance on all projects", PortfolioOptions.Overnight, expr);

        id.Value.ShouldNotBeNullOrEmpty();
        scheduler.List().Count.ShouldBe(1);
        scheduler.List()[0].Intent.ShouldBe("advance on all projects");
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var scheduler = MakeScheduler();
        var id = scheduler.Schedule("intent", PortfolioOptions.Default,
            new ScheduleExpression { TimeOfDay = new TimeOnly(2, 0) });

        scheduler.Remove(id);

        scheduler.List().ShouldBeEmpty();
    }

    [Fact]
    public void List_ReturnsAllSchedules()
    {
        var scheduler = MakeScheduler();
        scheduler.Schedule("a", PortfolioOptions.Default,
            new ScheduleExpression { TimeOfDay = new TimeOnly(1, 0) });
        scheduler.Schedule("b", PortfolioOptions.Default,
            new ScheduleExpression { TimeOfDay = new TimeOnly(3, 0) });

        scheduler.List().Count.ShouldBe(2);
    }

    [Fact]
    public void Schedule_PersistsToDisk()
    {
        var scheduler = MakeScheduler();
        scheduler.Schedule("intent", PortfolioOptions.Default,
            new ScheduleExpression { TimeOfDay = new TimeOnly(2, 0) });

        var scheduler2 = MakeScheduler();
        scheduler2.List().Count.ShouldBe(1);
        scheduler2.List()[0].Intent.ShouldBe("intent");
    }

    [Fact]
    public async Task RunPendingAsync_DueSchedule_ExecutesOrchestrator()
    {
        var portfolio = EmptyPortfolio();
        var executed = new List<string>();
        var stub = new CapturingOrchestrator(portfolio, executed);
        var scheduler = new CronPortfolioScheduler(stub, sanitizer: null, schedulesPath: _schedulesPath);

        var expr = new ScheduleExpression { TimeOfDay = new TimeOnly(2, 0), Repeat = RepeatInterval.Daily };
        scheduler.Schedule("run all", PortfolioOptions.Default, expr);

        var due = new DateTimeOffset(2026, 5, 1, 2, 0, 0, TimeSpan.Zero);
        await scheduler.RunPendingAsync(portfolio, due);

        executed.ShouldContain("run all");
    }

    [Fact]
    public async Task RunPendingAsync_NotDueYet_SkipsExecution()
    {
        var portfolio = EmptyPortfolio();
        var executed = new List<string>();
        var stub = new CapturingOrchestrator(portfolio, executed);
        var scheduler = new CronPortfolioScheduler(stub, sanitizer: null, schedulesPath: _schedulesPath);

        var expr = new ScheduleExpression { TimeOfDay = new TimeOnly(2, 0), Repeat = RepeatInterval.Daily };
        scheduler.Schedule("later", PortfolioOptions.Default, expr);

        var notDue = new DateTimeOffset(2026, 5, 1, 1, 0, 0, TimeSpan.Zero);
        await scheduler.RunPendingAsync(portfolio, notDue);

        executed.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunPendingAsync_ReportSanitizer_CalledOnOutput()
    {
        var portfolio = new InMemoryProjectRegistry();
        var dir = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            portfolio.Register(new WorkspaceContext
            {
                Id = WorkspaceId.New("test-proj"),
                Label = "test-proj",
                RootPath = dir,
                MaxBlastRadius = new BlastRadiusScope(),
                TokenStore = new InMemoryTokenStore(),
                SecretStore = new InMemorySecretStore(),
                Skills = new SkillRegistry(),
            });

            var sanitizerCalled = false;
            Func<string, string> sanitizer = content =>
            {
                sanitizerCalled = true;
                return content.Replace("SECRET", "[REDACTED]");
            };

            var orch = new StubOrchestrator(portfolio);
            var scheduler = new CronPortfolioScheduler(orch, sanitizer, schedulesPath: _schedulesPath);
            scheduler.Schedule("run",
                PortfolioOptions.Default,
                new ScheduleExpression { TimeOfDay = new TimeOnly(2, 0) });

            var due = new DateTimeOffset(2026, 5, 1, 2, 0, 0, TimeSpan.Zero);
            await scheduler.RunPendingAsync(portfolio, due);

            sanitizerCalled.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_schedulesPath)) File.Delete(_schedulesPath);
    }

    // ── Test doubles ──────────────────────────────────────────────

    private sealed class StubOrchestrator(IProjectPortfolio portfolio) : IPortfolioOrchestrator
    {
        public Task<PortfolioResult> RunAsync(
            string intent, PortfolioOptions options, CancellationToken ct = default)
        {
            var workspaces = portfolio.GetAll();
            var completed = workspaces.Select(ws => new WorkspaceExecutionResult
            {
                WorkspaceId = ws.Id,
                Success = true,
                TaskResults = Array.Empty<TaskExecutionResult>(),
            }).ToList();

            return Task.FromResult(new PortfolioResult
            {
                OverallSuccess = true,
                CompletedWorkspaces = completed,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        }

        public async IAsyncEnumerable<PortfolioProgressEvent> StreamAsync(
            string intent, PortfolioOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new PortfolioProgressEvent
            {
                WorkspaceId = default,
                Message = "stub",
                Timestamp = DateTimeOffset.UtcNow,
            };
            await Task.CompletedTask;
        }
    }

    private sealed class CapturingOrchestrator(
        IProjectPortfolio portfolio,
        List<string> captured) : IPortfolioOrchestrator
    {
        public Task<PortfolioResult> RunAsync(
            string intent, PortfolioOptions options, CancellationToken ct = default)
        {
            captured.Add(intent);
            var workspaces = portfolio.GetAll();
            var completed = workspaces.Select(ws => new WorkspaceExecutionResult
            {
                WorkspaceId = ws.Id,
                Success = true,
                TaskResults = Array.Empty<TaskExecutionResult>(),
            }).ToList();
            return Task.FromResult(new PortfolioResult
            {
                OverallSuccess = true,
                CompletedWorkspaces = completed,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        }

        public async IAsyncEnumerable<PortfolioProgressEvent> StreamAsync(
            string intent, PortfolioOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { yield return new PortfolioProgressEvent { WorkspaceId = default, Message = intent, Timestamp = DateTimeOffset.UtcNow }; await Task.CompletedTask; }
    }
}